// T2 — a TextReader that resolves `ReadLineAsync` from a queue filled by JS-side
// DeliverStdIn calls. Synchronous reads throw a pointed exception: blocking the Mono-WASM
// main thread would deadlock the xterm event pump, and the JS line editor would never get
// a chance to push a line.

using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Carbide.Terminal;

/// <summary>
/// <see cref="TextReader"/> backed by an async line queue. Lines arrive from the JS terminal
/// bridge's line editor via <see cref="Enqueue"/> (wired through
/// <see cref="CarbideTerminalInterop.DeliverStdIn"/>); consumers pull them out via
/// <see cref="ReadLineAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thread-unsafe by design — Mono-WASM is single-threaded and the interactive run path
/// never shares the reader across threads. Queue + TCS mutations run on the same thread as
/// their consumers.
/// </para>
/// <para>
/// Synchronous <see cref="Read()"/>, <see cref="ReadLine()"/>, and <see cref="ReadToEnd()"/>
/// throw <see cref="NotSupportedException"/> with a pointed message directing callers at the
/// Async equivalents. Blocking the main thread waiting for JS-side input deadlocks the xterm
/// event pump, which is the thread that would deliver the line — the pointed throw is
/// preferable to an infinite spin.
/// </para>
/// </remarks>
internal sealed class BrowserTerminalReader : TextReader
{
    private readonly Queue<string> _lines = new();
    private TaskCompletionSource<string?>? _pendingRead;
    private readonly StringBuilder _partialLineBuffer = new();
    private bool _closed;

    /// <summary>
    /// Push a complete line from the JS side. The line should not carry its trailing newline
    /// (the JS editor strips it before delivery). If a <see cref="ReadLineAsync"/> call is
    /// pending, the line resolves it immediately; otherwise it queues.
    /// </summary>
    internal void EnqueueLine(string line)
    {
        if (_closed) return;
        if (_pendingRead is { } tcs)
        {
            _pendingRead = null;
            tcs.TrySetResult(line);
        }
        else
        {
            _lines.Enqueue(line);
        }
    }

    /// <summary>
    /// Push raw bytes from the JS side (key mode). Bytes accumulate in
    /// <see cref="_partialLineBuffer"/> until <see cref="DrainPartialBufferAsLine"/> is
    /// called — used by <see cref="CarbideConsole.ReadKeyAsync"/>'s byte-at-a-time decode.
    /// If a key-mode consumer is waiting via <see cref="WaitForBytesAsync"/>, this resolves
    /// the TCS so the consumer's next iteration sees the new bytes.
    /// </summary>
    internal void EnqueueRaw(string data)
    {
        if (_closed) return;
        _partialLineBuffer.Append(data);
        if (_keyWaiter is { } k)
        {
            _keyWaiter = null;
            k.TrySetResult(true);
        }
    }

    private TaskCompletionSource<bool>? _keyWaiter;

    /// <summary>
    /// Awaited by <see cref="CarbideConsole.ReadKeyAsync"/> when the raw buffer is empty
    /// and more bytes are needed. Resolves when <see cref="EnqueueRaw"/> next fires, or
    /// when the reader is disposed (EOF).
    /// </summary>
    internal Task WaitForBytesAsync(CancellationToken ct)
    {
        if (_partialLineBuffer.Length > 0 || _closed)
        {
            return Task.CompletedTask;
        }
        if (_keyWaiter is not null)
        {
            // Only one pending key waiter at a time — under Mono-WASM's single-threaded
            // model this is impossible unless user code holds multiple ReadKeyAsyncs.
            throw new InvalidOperationException(
                "BrowserTerminalReader: a previous key-mode wait is still pending.");
        }
        // T2.1 — flush stdout before suspending (see ReadLineAsync for the rationale).
        try { Console.Out.Flush(); } catch { /* best-effort */ }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.None);
        _keyWaiter = tcs;
        if (ct.CanBeCanceled)
        {
            ct.Register(static state =>
            {
                var (self, sourceTcs) = ((BrowserTerminalReader, TaskCompletionSource<bool>))state!;
                if (ReferenceEquals(self._keyWaiter, sourceTcs))
                {
                    self._keyWaiter = null;
                }
                sourceTcs.TrySetCanceled();
            }, (this, tcs));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Pull and clear the raw buffer built up by <see cref="EnqueueRaw"/>. Returns
    /// <see cref="string.Empty"/> when nothing is buffered.
    /// </summary>
    internal string DrainPartialBufferAsLine()
    {
        if (_partialLineBuffer.Length == 0) return string.Empty;
        var text = _partialLineBuffer.ToString();
        _partialLineBuffer.Clear();
        return text;
    }

    /// <summary>Whether any raw byte is available from the key-mode buffer.</summary>
    internal bool HasPartialBytes => _partialLineBuffer.Length > 0;

    /// <summary>
    /// Signal end of input. Any pending <see cref="ReadLineAsync"/> or
    /// <see cref="WaitForBytesAsync"/> call resolves; subsequent calls also resolve
    /// immediately.
    /// </summary>
    internal void Complete()
    {
        _closed = true;
        if (_pendingRead is { } tcs)
        {
            _pendingRead = null;
            tcs.TrySetResult(null);
        }
        if (_keyWaiter is { } k)
        {
            _keyWaiter = null;
            k.TrySetResult(false);
        }
    }

    public override Task<string?> ReadLineAsync()
        => ReadLineAsync(CancellationToken.None).AsTask();

    public override ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        if (_lines.Count > 0)
        {
            return new ValueTask<string?>(_lines.Dequeue());
        }
        if (_closed)
        {
            return new ValueTask<string?>((string?)null);
        }
        if (_pendingRead is not null)
        {
            // Only one pending read at a time; a second concurrent ReadLineAsync would be a
            // user bug under Mono-WASM's single-threaded model. Fail loudly.
            throw new InvalidOperationException(
                "BrowserTerminalReader: a previous ReadLineAsync is still pending. " +
                "Only one concurrent read is supported.");
        }
        // T2.1 — flush stdout before suspending. StreamingStdOutWriter is line-buffered (flushes
        // on `\n`) but interactive prompts like `Console.Write("name? ")` end without a newline.
        // Without this flush, the prompt stays in the buffer and the JS terminal never sees it
        // before the user is expected to respond — the line editor blinks at a blank screen.
        try { Console.Out.Flush(); } catch { /* best-effort */ }
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.None);
        _pendingRead = tcs;
        if (ct.CanBeCanceled)
        {
            ct.Register(static state =>
            {
                var (self, sourceTcs) = ((BrowserTerminalReader, TaskCompletionSource<string?>))state!;
                if (ReferenceEquals(self._pendingRead, sourceTcs))
                {
                    self._pendingRead = null;
                }
                sourceTcs.TrySetCanceled();
            }, (this, tcs));
        }
        return new ValueTask<string?>(tcs.Task);
    }

    public override int Read() => ThrowSync(nameof(Read));
    public override int Read(char[] buffer, int index, int count) => ThrowSync(nameof(Read));
    public override int Read(Span<char> buffer) => ThrowSync(nameof(Read));
    public override int ReadBlock(char[] buffer, int index, int count) => ThrowSync(nameof(ReadBlock));
    public override int ReadBlock(Span<char> buffer) => ThrowSync(nameof(ReadBlock));
    public override string? ReadLine() { ThrowSync(nameof(ReadLine)); return null; }
    public override string ReadToEnd() { ThrowSync(nameof(ReadToEnd)); return string.Empty; }
    public override int Peek() => ThrowSync(nameof(Peek));

    private static int ThrowSync(string member)
        => throw new NotSupportedException(
            $"Console.In.{member}() would block the Mono-WASM main thread and deadlock the xterm event pump. " +
            $"Use Console.In.{member}Async() or Carbide.Terminal.CarbideConsole.{(member == "ReadLine" ? "ReadLineAsync" : member + "Async")}() instead.");
}
