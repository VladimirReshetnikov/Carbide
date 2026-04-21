// T2 — user-facing static surface for the interactive terminal. Mirrors the parts of
// `System.Console` that throw `PlatformNotSupportedException` on browser-targeted Mono-WASM
// today: color, cursor, window geometry, title, clear, ReadLine/ReadKey, Ctrl+C.
//
// State lives on the thread-static TerminalInputState.Current, set by
// ProjectCompiler.RunInteractiveAsync on entry and cleared on exit. All SGR / cursor / OSC
// emission goes through Console.Out (which T1's RunInteractiveAsync already pointed at a
// StreamingStdOutWriter), so byte-level flushing and xterm routing are reused.

using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Carbide.Terminal;

/// <summary>
/// User-facing static shim that exposes a useful subset of <see cref="System.Console"/> to
/// Carbide-compiled source running in an interactive terminal. The pre-T3 scope: APIs here
/// cover code Vladimir compiles from source. Pre-compiled NuGet libraries that call
/// <c>Console.ReadKey</c> / <c>Console.ForegroundColor</c> directly still throw
/// <see cref="PlatformNotSupportedException"/>; T3's forked <c>System.Console.dll</c> closes
/// that gap.
/// </summary>
public static class CarbideConsole
{
    // ---- Input -------------------------------------------------------------------------

    /// <summary>
    /// Asynchronously reads a line from the interactive terminal's stdin. Equivalent to
    /// <c>Console.In.ReadLineAsync(ct)</c> — the C# run path installs a
    /// <see cref="BrowserTerminalReader"/> into <c>Console.In</c> on entry, so both surfaces
    /// resolve from the same queue.
    /// </summary>
    public static Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        var state = RequireState(nameof(ReadLineAsync));
        return state.Reader.ReadLineAsync(ct).AsTask();
    }

    /// <summary>
    /// Asynchronously reads a single key press. Switches the JS line editor into key mode
    /// for the duration of the await; restores line mode when the returned task completes.
    ///
    /// <para>When <paramref name="intercept"/> is <c>false</c> (default), the decoded key
    /// char is echoed back to the terminal. When <c>true</c>, nothing is echoed — useful
    /// for password prompts or REPLs that want total control over display.</para>
    /// </summary>
    public static async Task<ConsoleKeyInfo> ReadKeyAsync(bool intercept = false, CancellationToken ct = default)
    {
        var state = RequireState(nameof(ReadKeyAsync));

        CarbideTerminalInterop.NotifyKeyMode(true);
        try
        {
            var buffer = new char[64];
            int bufferEnd = 0;

            // Pull one or more JS-side deliveries into the local buffer until KeyParser
            // recognises a complete key sequence. Most keys arrive in a single delivery
            // (~1-6 chars), but modifier-encoded forms and the Alt-Escape prefix case can
            // span two deliveries — the first carries `\x1b` alone, the second carries the
            // rest. Loop until something parses.
            while (true)
            {
                if (state.Reader.HasPartialBytes)
                {
                    var chunk = state.Reader.DrainPartialBufferAsLine();
                    AppendToBuffer(chunk, ref buffer, ref bufferEnd);
                }

                if (bufferEnd > 0 && TryParse(buffer, ref bufferEnd, out var key))
                {
                    if (!intercept && key.KeyChar != '\0')
                    {
                        Console.Out.Write(key.KeyChar);
                    }
                    return key;
                }

                // Nothing parsed yet. Wait for the next delivery. Do NOT ConfigureAwait(false)
                // — on Mono-WASM browser the default TaskScheduler has no thread pool, so
                // continuing without a captured context falls through to Monitor.Wait.
                // Staying on the default (implicit) sync context keeps the continuation on
                // the main thread where it belongs.
                await WaitForByteAsync(state, ct);
            }
        }
        finally
        {
            CarbideTerminalInterop.NotifyKeyMode(false);
        }
    }

    // ---- Color (SGR) -------------------------------------------------------------------

    /// <summary>
    /// The foreground color used for subsequent writes. Setting emits an SGR escape on
    /// <see cref="Console.Out"/>; the xterm buffer sees the color change before the next
    /// character is written.
    /// </summary>
    [Obsolete("T3: stock System.Console.ForegroundColor works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static ConsoleColor ForegroundColor
    {
        get => RequireState(nameof(ForegroundColor)).ForegroundColor;
        set
        {
            var state = RequireState(nameof(ForegroundColor));
            state.ForegroundColor = value;
            Console.Out.Write(AnsiSgr.Foreground(value));
        }
    }

    /// <summary>The background color used for subsequent writes.</summary>
    [Obsolete("T3: stock System.Console.BackgroundColor works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static ConsoleColor BackgroundColor
    {
        get => RequireState(nameof(BackgroundColor)).BackgroundColor;
        set
        {
            var state = RequireState(nameof(BackgroundColor));
            state.BackgroundColor = value;
            Console.Out.Write(AnsiSgr.Background(value));
        }
    }

    /// <summary>Reset foreground and background to the terminal defaults (<c>\x1b[39;49m</c>).</summary>
    [Obsolete("T3: stock System.Console.ResetColor() works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static void ResetColor()
    {
        var state = RequireState(nameof(ResetColor));
        state.ForegroundColor = ConsoleColor.Gray;
        state.BackgroundColor = ConsoleColor.Black;
        Console.Out.Write("\x1b[39;49m");
    }

    // ---- Cursor ------------------------------------------------------------------------

    /// <summary>
    /// Move the cursor to the given 0-based coordinates by emitting CUP
    /// (<c>\x1b[&lt;top+1&gt;;&lt;left+1&gt;H</c>).
    /// </summary>
    [Obsolete("T3: stock System.Console.SetCursorPosition(left, top) works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static void SetCursorPosition(int left, int top)
    {
        RequireState(nameof(SetCursorPosition));
        if (left < 0) throw new ArgumentOutOfRangeException(nameof(left));
        if (top < 0) throw new ArgumentOutOfRangeException(nameof(top));
        Console.Out.Write($"\x1b[{(top + 1).ToString(CultureInfo.InvariantCulture)};{(left + 1).ToString(CultureInfo.InvariantCulture)}H");
    }

    /// <summary>
    /// Whether the cursor is shown. Setting emits DECTCEM (<c>\x1b[?25h</c> / <c>\x1b[?25l</c>).
    /// </summary>
    [Obsolete("T3: stock System.Console.CursorVisible works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static bool CursorVisible
    {
        get => RequireState(nameof(CursorVisible)).CursorVisible;
        set
        {
            var state = RequireState(nameof(CursorVisible));
            state.CursorVisible = value;
            Console.Out.Write(value ? "\x1b[?25h" : "\x1b[?25l");
        }
    }

    /// <summary>
    /// The cursor's current (left, top) position.
    ///
    /// <para>Not implemented in T2. The DSR reply (<c>\x1b[&lt;row&gt;;&lt;col&gt;R</c>)
    /// arrives on the input stream and needs a pre-filter in the JS bridge that routes it
    /// around the user-facing reader queue. T3's forked <c>StdInReader</c> already has the
    /// machinery; until then, callers get a pointed <see cref="NotSupportedException"/>.</para>
    /// </summary>
    public static (int Left, int Top) GetCursorPosition()
        => throw new NotSupportedException(
            "GetCursorPosition is not implemented in T2 — it requires DSR-reply pre-filtering " +
            "that lands with T3's forked System.Console.dll. Track the cursor yourself or " +
            "emit the raw DSR query via CarbideConsole.WriteRaw(\"\\x1b[6n\") and parse the " +
            "reply from Console.In.");

    // ---- Title + Clear -----------------------------------------------------------------

    /// <summary>
    /// Sets the terminal title via OSC 0 (<c>\x1b]0;&lt;value&gt;\x07</c>). No getter —
    /// there's no portable way to read the title back, so it's write-only to mirror what
    /// user code can portably do.
    /// </summary>
    [Obsolete("T3: stock System.Console.Title setter works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static string Title
    {
        set
        {
            RequireState(nameof(Title));
            ArgumentNullException.ThrowIfNull(value);
            Console.Out.Write($"\x1b]0;{value}\x07");
        }
    }

    /// <summary>Clear the terminal (ED + CUP home): <c>\x1b[2J\x1b[H</c>.</summary>
    [Obsolete("T3: stock System.Console.Clear() works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static void Clear()
    {
        RequireState(nameof(Clear));
        Console.Out.Write("\x1b[2J\x1b[H");
    }

    // ---- Window geometry ---------------------------------------------------------------

    /// <summary>Cached terminal width (columns). Updated by JS-side <c>onResize</c> deliveries.</summary>
    [Obsolete("T3: stock System.Console.WindowWidth works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static int WindowWidth => RequireState(nameof(WindowWidth)).Cols;

    /// <summary>Cached terminal height (rows).</summary>
    [Obsolete("T3: stock System.Console.WindowHeight works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static int WindowHeight => RequireState(nameof(WindowHeight)).Rows;

    /// <summary>
    /// Browser terminals don't have a separate buffer from the window; this is an alias for
    /// <see cref="WindowWidth"/>. Kept for conhost-parity so ported code doesn't have to
    /// decide which to call.
    /// </summary>
    [Obsolete("T3: stock System.Console.BufferWidth works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
#pragma warning disable CS0618
    public static int BufferWidth => WindowWidth;
#pragma warning restore CS0618

    /// <summary>Alias for <see cref="WindowHeight"/>.</summary>
    [Obsolete("T3: stock System.Console.BufferHeight works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
#pragma warning disable CS0618
    public static int BufferHeight => WindowHeight;
#pragma warning restore CS0618

    /// <summary>Fired on every JS-side terminal resize. Payload carries the new (cols, rows).</summary>
    public static event EventHandler<(int Cols, int Rows)>? TerminalResized
    {
        add
        {
            var state = RequireState(nameof(TerminalResized));
            state.Resized += value;
        }
        remove
        {
            // Silent no-op when the session has ended; matches .NET's usual event semantics.
            var state = TerminalInputState.Current;
            if (state is not null) state.Resized -= value;
        }
    }

    // ---- Signals -----------------------------------------------------------------------

    /// <summary>
    /// Whether Ctrl+C is delivered as a stdin byte (<c>true</c>) or fires
    /// <see cref="CancelKeyPress"/> (<c>false</c>, default). Mirrors
    /// <see cref="System.Console.TreatControlCAsInput"/>.
    /// </summary>
    [Obsolete("T3: stock System.Console.TreatControlCAsInput works in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static bool TreatControlCAsInput
    {
        get => RequireState(nameof(TreatControlCAsInput)).TreatControlCAsInput;
        set
        {
            var state = RequireState(nameof(TreatControlCAsInput));
            state.TreatControlCAsInput = value;
            // Push the flag to the JS side so the line editor routes the byte correctly
            // without a round-trip on every keystroke.
            CarbideTerminalInterop.NotifyTreatControlCAsInput(value);
        }
    }

    /// <summary>
    /// Fires when the user presses Ctrl+C and <see cref="TreatControlCAsInput"/> is false.
    /// Matches <see cref="System.Console.CancelKeyPress"/>: handlers run synchronously on
    /// the signal-delivery thread; setting <c>args.Cancel = true</c> suppresses the
    /// subsequent <see cref="CancellationToken"/> trip.
    /// </summary>
    [Obsolete("T3: stock System.Console.CancelKeyPress fires in interactive runs now. Prefer it; this shim remains for pre-T3 source compatibility.")]
    public static event ConsoleCancelEventHandler? CancelKeyPress
    {
        add
        {
            var state = RequireState(nameof(CancelKeyPress));
            state.AddCancelKeyPressHandler(value);
        }
        remove
        {
            var state = TerminalInputState.Current;
            state?.RemoveCancelKeyPressHandler(value);
        }
    }

    /// <summary>
    /// The cancellation token tripped by an unintercepted Ctrl+C. Await-points in user code
    /// that pass this token observe cancellation when the signal fires.
    /// </summary>
    public static CancellationToken RunCancellationToken
        => RequireState(nameof(RunCancellationToken)).CancellationTokenSource.Token;

    // ---- Escape hatch ------------------------------------------------------------------

    /// <summary>
    /// Bypass the streaming writer's buffering and emit <paramref name="sequence"/> directly
    /// through the JS bridge. Use for advanced VT sequences <see cref="CarbideConsole"/>
    /// doesn't cover (alt screen, bracketed paste, mouse tracking).
    /// </summary>
    public static void WriteRaw(string sequence)
    {
        RequireState(nameof(WriteRaw));
        ArgumentNullException.ThrowIfNull(sequence);
        // Flush any buffered output first so the raw bytes appear in the right order
        // relative to preceding Console.Write calls.
        Console.Out.Flush();
        CarbideTerminalInterop.WriteStdOut(sequence);
    }

    /// <summary>
    /// Async delay that works on Mono-WASM browser. Plain <see cref="Task.Delay(int)"/>
    /// trips <c>PlatformNotSupportedException: Cannot wait on monitors</c> because the
    /// task scheduler falls through to an absent thread pool. T2.1: this routes via a
    /// callback-based JSImport (<see cref="CarbideTerminalInterop.DelayCallback"/>) that
    /// completes a locally-owned TCS with <see cref="TaskCreationOptions.None"/> — so the
    /// user-code await continuation runs synchronously on the setTimeout tick instead of
    /// queuing via ThreadPool. Earlier (broken) T2 version used the JSImport's
    /// Promise-to-Task marshaler, which forces async continuations via
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>.
    /// </summary>
    public static Task DelayAsync(int milliseconds, CancellationToken ct = default)
    {
        RequireState(nameof(DelayAsync));
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        // T2.1 — flush stdout before suspending so any newline-less output (e.g. a spinner
        // frame or progress marker) reaches the JS terminal before the delay starts.
        try { Console.Out.Flush(); } catch { /* best-effort */ }
        var tcs = new TaskCompletionSource(TaskCreationOptions.None);
        CancellationTokenRegistration reg = default;
        if (ct.CanBeCanceled)
        {
            reg = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
        }
        CarbideTerminalInterop.DelayCallback(milliseconds, () =>
        {
            reg.Dispose();
            tcs.TrySetResult();
        });
        return tcs.Task;
    }

    /// <summary>
    /// T3.1 — awaitable single-tone beep through Web Audio. Plays a sine wave at
    /// <paramref name="frequency"/> Hz for <paramref name="durationMs"/> milliseconds and
    /// resolves the returned task when playback ends. Sequential <c>BeepAsync</c> calls
    /// queue in audio time on the JS side, so tones don't overlap even if the awaits
    /// resume fast.
    /// <para>
    /// Silent and completes on schedule when Web Audio is unavailable (Node, JSDOM) or
    /// the AudioContext is still suspended waiting on a first user gesture (browser
    /// autoplay policy). That matches the contract of stock <see cref="Console.Beep()"/>
    /// which also silently no-ops on muted systems.
    /// </para>
    /// <para>
    /// For fire-and-forget semantics matching stock sync <c>Console.Beep(freq, duration)</c>,
    /// call that directly — the forked <c>System.Console.dll</c> routes to the same
    /// JS bridge without an awaitable.
    /// </para>
    /// <example>
    /// <code>
    /// await CarbideConsole.BeepAsync(440, 200);
    /// await CarbideConsole.BeepAsync(880, 200);
    /// // The second tone starts after the first completes.
    /// </code>
    /// </example>
    /// </summary>
    public static Task BeepAsync(int frequency, int durationMs, CancellationToken ct = default)
    {
        RequireState(nameof(BeepAsync));
        if (frequency is < 37 or > 32767)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequency), frequency,
                "Frequency must be between 37 and 32767 Hz (matching stock Console.Beep).");
        }
        if (durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMs), durationMs, "Duration must be positive.");
        }
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        // Flush stdout before suspending so any newline-less output (e.g. "beeping:")
        // reaches the terminal before the tone starts.
        try { Console.Out.Flush(); } catch { /* best-effort */ }

        var tcs = new TaskCompletionSource(TaskCreationOptions.None);
        CancellationTokenRegistration reg = default;
        if (ct.CanBeCanceled)
        {
            // We can't actually stop an in-flight Web Audio oscillator from here (the JS
            // side doesn't expose a handle), but we can resolve the Task as cancelled so
            // user code unblocks. The tone finishes in audio time regardless; for most
            // REPL/TUI uses that's acceptable.
            reg = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
        }
        CarbideTerminalInterop.BeepCallback(frequency, durationMs, () =>
        {
            reg.Dispose();
            tcs.TrySetResult();
        });
        return tcs.Task;
    }

    /// <summary>
    /// Await the next <see cref="TerminalResized"/> event. Resolves on the first resize
    /// that fires after the call; cancellation via <paramref name="ct"/> fails the task
    /// with <see cref="OperationCanceledException"/>. Uses the same TCS pattern as
    /// <see cref="ReadLineAsync"/>, which is known to work on Mono-WASM browser.
    /// </summary>
    public static Task<bool> WaitForResizeAsync(CancellationToken ct = default)
    {
        var state = RequireState(nameof(WaitForResizeAsync));
        if (ct.IsCancellationRequested) return Task.FromCanceled<bool>(ct);

        // T2.1 — flush stdout before suspending; same rationale as ReadLineAsync.
        try { Console.Out.Flush(); } catch { /* best-effort */ }

        // Match the readline pattern: `TaskCompletionSource<bool>` (generic) + sync
        // continuations. The non-generic `TaskCompletionSource` / `Task` path turns out to
        // exhibit different scheduler behaviour on Mono-WASM browser; the generic shape is
        // the one we've proven works (via `BrowserTerminalReader.ReadLineAsync`).
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.None);
        EventHandler<(int Cols, int Rows)>? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null) state.Resized -= handler;
            tcs.TrySetResult(true);
        };
        state.Resized += handler;
        if (ct.CanBeCanceled)
        {
            ct.Register(static o =>
            {
                var (s, h, t) = ((TerminalInputState, EventHandler<(int, int)>, TaskCompletionSource<bool>))o!;
                s.Resized -= h;
                t.TrySetCanceled();
            }, (state, handler!, tcs));
        }
        return tcs.Task;
    }

    // ---- Internals ---------------------------------------------------------------------

    private static TerminalInputState RequireState(string member)
        => TerminalInputState.Current
            ?? throw new InvalidOperationException(
                $"CarbideConsole.{member} is only available inside an active interactive terminal session. " +
                "Call Project.runInteractive(...) from TypeScript to start one.");

    private static void AppendToBuffer(string chunk, ref char[] buffer, ref int bufferEnd)
    {
        if (bufferEnd + chunk.Length > buffer.Length)
        {
            var bigger = new char[Math.Max(buffer.Length * 2, bufferEnd + chunk.Length + 16)];
            Array.Copy(buffer, bigger, bufferEnd);
            buffer = bigger;
        }
        chunk.AsSpan().CopyTo(buffer.AsSpan(bufferEnd));
        bufferEnd += chunk.Length;
    }

    private static bool TryParse(char[] buffer, ref int bufferEnd, out ConsoleKeyInfo key)
    {
        if (bufferEnd == 0)
        {
            key = default;
            return false;
        }
        int startIndex = 0;
        // KeyParser.Parse works on a forward-advancing startIndex. If the buffer contains
        // only a lone ESC, Parse returns ConsoleKey.Escape (single-char path). That's the
        // behaviour user code expects when Escape is pressed by itself.
        key = KeyParser.Parse(buffer, XtermTerminfo.Shared, posixDisableValue: 0xFF, veraseCharacter: 0x7F, ref startIndex, bufferEnd);
        // Consume the parsed prefix and shift remaining bytes to the front.
        var consumed = startIndex;
        if (consumed == 0)
        {
            key = default;
            return false;
        }
        var remaining = bufferEnd - consumed;
        if (remaining > 0)
        {
            Array.Copy(buffer, consumed, buffer, 0, remaining);
        }
        bufferEnd = remaining;
        return true;
    }

    private static Task WaitForByteAsync(TerminalInputState state, CancellationToken ct)
    {
        // Use a TCS-backed key waiter rather than a `Task.Delay`-based polling loop —
        // `Task.Delay` on Mono-WASM browser dispatches its continuation through a path
        // that tries `Monitor.Wait` on a missing thread pool, throwing
        // `PlatformNotSupportedException: Cannot wait on monitors`. The TCS completes when
        // `DeliverStdIn` enqueues key-mode bytes.
        return state.Reader.WaitForBytesAsync(ct);
    }
}

// ---- Helpers ---------------------------------------------------------------------------

/// <summary>
/// Translates <see cref="ConsoleColor"/> values into ANSI SGR parameters.
/// </summary>
internal static class AnsiSgr
{
    public static string Foreground(ConsoleColor color) => $"\x1b[{ToFgCode(color)}m";
    public static string Background(ConsoleColor color) => $"\x1b[{ToBgCode(color)}m";

    private static int ToFgCode(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => 30,
        ConsoleColor.DarkRed => 31,
        ConsoleColor.DarkGreen => 32,
        ConsoleColor.DarkYellow => 33,
        ConsoleColor.DarkBlue => 34,
        ConsoleColor.DarkMagenta => 35,
        ConsoleColor.DarkCyan => 36,
        ConsoleColor.Gray => 37,
        ConsoleColor.DarkGray => 90,
        ConsoleColor.Red => 91,
        ConsoleColor.Green => 92,
        ConsoleColor.Yellow => 93,
        ConsoleColor.Blue => 94,
        ConsoleColor.Magenta => 95,
        ConsoleColor.Cyan => 96,
        ConsoleColor.White => 97,
        _ => 39,
    };

    private static int ToBgCode(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => 40,
        ConsoleColor.DarkRed => 41,
        ConsoleColor.DarkGreen => 42,
        ConsoleColor.DarkYellow => 43,
        ConsoleColor.DarkBlue => 44,
        ConsoleColor.DarkMagenta => 45,
        ConsoleColor.DarkCyan => 46,
        ConsoleColor.Gray => 47,
        ConsoleColor.DarkGray => 100,
        ConsoleColor.Red => 101,
        ConsoleColor.Green => 102,
        ConsoleColor.Yellow => 103,
        ConsoleColor.Blue => 104,
        ConsoleColor.Magenta => 105,
        ConsoleColor.Cyan => 106,
        ConsoleColor.White => 107,
        _ => 49,
    };
}
