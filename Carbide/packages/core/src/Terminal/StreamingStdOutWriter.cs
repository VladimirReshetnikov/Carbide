// T1 — a TextWriter that forwards buffered writes to the JS terminal bridge in bounded
// windows instead of capturing into a StringWriter for end-of-run retrieval.
//
// Thread-unsafe by design. Mono-WASM is single-threaded and Carbide's interactive run path
// never shares the writer across threads, so TextWriter.Synchronized's per-write lock would
// be pure overhead (DT-T1.1). Users that want synchronized behavior can wrap explicitly.

using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Carbide.Terminal;

/// <summary>
/// <see cref="TextWriter"/> that forwards buffered writes to a caller-supplied sink in bounded
/// time or size windows. Built for the interactive terminal path: the sink is typically a
/// JSImport into <c>globalThis.Carbide.Terminal.write</c>.
/// </summary>
/// <remarks>
/// <para>
/// The writer coalesces small writes (e.g. per-character <c>Console.Write(char)</c> in a
/// tight loop) into one cross-boundary call per flush window. Flush is triggered by either
/// (a) the internal buffer filling to <paramref name="flushBytes"/>, or (b) the elapsed time
/// since the last flush exceeding <paramref name="flushIntervalMs"/> — whichever trips first.
/// Time is measured with <see cref="Stopwatch.GetTimestamp"/>, and the check runs inside
/// each <c>Write</c> call rather than on a timer callback (Mono-WASM main-thread doesn't
/// reliably schedule timer callbacks between user-program suspensions).
/// </para>
/// <para>
/// Not thread-safe. The interactive run path is single-threaded on Mono-WASM.
/// </para>
/// <para>
/// <see cref="Dispose(bool)"/> performs a final drain flush before releasing the pooled
/// buffer, so bytes already in the buffer reach the sink even if the caller forgets a
/// final <see cref="Flush"/>.
/// </para>
/// </remarks>
internal sealed class StreamingStdOutWriter : TextWriter
{
    /// <summary>Default flush-on-size threshold. Above this, the next write forces a flush.</summary>
    internal const int DefaultFlushBytes = 4 * 1024;

    /// <summary>Default flush-on-time threshold, in wall-clock milliseconds.</summary>
    internal const int DefaultFlushIntervalMs = 32;

    private readonly Action<string> _sink;
    private readonly int _flushBytes;
    private readonly long _flushIntervalTicks;

    private char[] _buffer;
    private int _length;
    private long _lastFlushTimestamp;
    private volatile bool _disposed;

    public StreamingStdOutWriter(
        Action<string> sink,
        int flushBytes = DefaultFlushBytes,
        int flushIntervalMs = DefaultFlushIntervalMs)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (flushBytes <= 0) throw new ArgumentOutOfRangeException(nameof(flushBytes));
        if (flushIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(flushIntervalMs));

        _sink = sink;
        _flushBytes = flushBytes;
        // Pre-compute the Stopwatch-tick threshold so MaybeFlush is one multiply + compare,
        // not a division per write.
        _flushIntervalTicks = (long)(flushIntervalMs / 1000.0 * Stopwatch.Frequency);
        _buffer = ArrayPool<char>.Shared.Rent(flushBytes);
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (_disposed) return;
        EnsureCapacity(1);
        _buffer[_length++] = value;
        MaybeFlush();
    }

    public override void Write(string? value)
    {
        if (_disposed || string.IsNullOrEmpty(value)) return;
        WriteSpan(value.AsSpan());
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (_disposed || count <= 0) return;
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)index > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - index))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        WriteSpan(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        if (_disposed || buffer.IsEmpty) return;
        WriteSpan(buffer);
    }

    public override void WriteLine() => Write(CoreNewLine);

    public override void WriteLine(string? value)
    {
        if (value is not null) Write(value);
        Write(CoreNewLine);
    }

    public override void Flush() => FlushNow();

    /// <summary>
    /// Override the default <see cref="TextWriter.FlushAsync()"/> implementation which
    /// dispatches to <see cref="System.Threading.Tasks.TaskScheduler.Default"/> (the
    /// thread pool). On single-threaded Mono-WASM browser there's no thread pool, so
    /// the default impl throws <c>PlatformNotSupportedException: Cannot wait on monitors
    /// on this runtime</c>. Flushing is synchronous here anyway — the buffer is in-process.
    /// </summary>
    public override Task FlushAsync()
    {
        try
        {
            FlushNow();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        return FlushAsync();
    }

    // Additional async overrides so `Console.Out.WriteLineAsync("...")` and friends
    // complete synchronously on Mono-WASM browser instead of dispatching to an absent
    // thread pool. Writing is already in-memory + buffered; no threadpool hop is useful.

    public override Task WriteAsync(char value) { Write(value); return Task.CompletedTask; }
    public override Task WriteAsync(string? value) { Write(value); return Task.CompletedTask; }
    public override Task WriteAsync(char[] buffer, int index, int count) { Write(buffer, index, count); return Task.CompletedTask; }
    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        Write(buffer.Span);
        return Task.CompletedTask;
    }
    public override Task WriteLineAsync() { WriteLine(); return Task.CompletedTask; }
    public override Task WriteLineAsync(char value) { Write(value); WriteLine(); return Task.CompletedTask; }
    public override Task WriteLineAsync(string? value) { WriteLine(value); return Task.CompletedTask; }
    public override Task WriteLineAsync(char[] buffer, int index, int count)
    {
        Write(buffer, index, count);
        WriteLine();
        return Task.CompletedTask;
    }
    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        Write(buffer.Span);
        WriteLine();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Forcibly drain the buffer now, even if neither the size nor the time threshold has
    /// been reached. Intended for end-of-run orchestration; user code shouldn't need it.
    /// </summary>
    internal void FlushNow()
    {
        if (_length == 0) return;
        var text = new string(_buffer, 0, _length);
        _length = 0;
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
        _sink(text);
    }

    private void WriteSpan(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty)
        {
            var remaining = _buffer.Length - _length;
            if (remaining == 0)
            {
                // Flush to free the buffer rather than grow it unboundedly; keeps the
                // per-flush payload sized to flushBytes.
                FlushNow();
                remaining = _buffer.Length;
            }
            var chunkLen = Math.Min(value.Length, remaining);
            value.Slice(0, chunkLen).CopyTo(_buffer.AsSpan(_length));
            _length += chunkLen;
            value = value.Slice(chunkLen);
            MaybeFlush();
        }
    }

    private void EnsureCapacity(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        // ArrayPool.Rent may return a bucket larger than flushBytes; that's fine and the
        // intended behaviour. We only grow when a single Write needs more than the current
        // bucket; in practice the caller-supplied flushBytes is the lower bound on the
        // buffer size, not an upper bound (see DT-T1.1 notes).
        FlushNow();
        if (extra > _buffer.Length)
        {
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = ArrayPool<char>.Shared.Rent(extra);
        }
    }

    private void MaybeFlush()
    {
        if (_length >= _flushBytes)
        {
            FlushNow();
            return;
        }
        if (_flushIntervalTicks == 0) return;
        var elapsed = Stopwatch.GetTimestamp() - _lastFlushTimestamp;
        if (elapsed >= _flushIntervalTicks)
        {
            FlushNow();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (disposing)
            {
                FlushNow();
            }
        }
        finally
        {
            var buffer = _buffer;
            _buffer = Array.Empty<char>();
            if (buffer.Length > 0)
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
            base.Dispose(disposing);
        }
    }
}
