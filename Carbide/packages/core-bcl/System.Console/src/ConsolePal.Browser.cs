// T3 — Carbide's replacement ConsolePal for the browser target. Routes stock `Console.*`
// calls through the T1/T2 JS bridge (`globalThis.Carbide.Terminal.*`). Pre-compiled
// libraries that set `Console.ForegroundColor`, move the cursor, query `Console.WindowWidth`,
// clear the screen, set the title, or register a `CancelKeyPress` handler now work
// unmodified.
//
// What stays PNS (with pointed messages):
//  - Synchronous `Console.ReadKey(bool)` and blocking reads from `Console.In` — Mono-WASM
//    single-threaded browser has no sync-over-async primitive; T3.1 or a worker+SAB
//    effort is the proper fix.
//  - `Console.GetCursorPosition` — needs DSR reply pre-filtering (T3.1).
//  - `Console.Beep(freq, duration)` — no portable browser equivalent.
//  - `Console.MoveBufferArea` — browser terminal has no separate scroll-back buffer.
//  - `Console.SetBufferSize` / `Set{Window}Size` / `Set{Window}Position` — xterm.js doesn't
//    expose programmatic resize.

using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace System
{
    /// <summary>
    /// Internal marker type the Carbide publish-time smoke reflects on to confirm the
    /// forked <c>System.Console.dll</c> replaced the stock one in <c>_framework/</c>.
    /// </summary>
    internal static class CarbideForkedConsoleMarker
    {
        public const string Marker = "Carbide-forked System.Console.dll (T3)";
    }

    internal static partial class ConsolePal
    {
        private static ConsoleColor s_foregroundColor = ConsoleColor.Gray;
        private static ConsoleColor s_backgroundColor = ConsoleColor.Black;
        private static bool s_cursorVisible = true;
        private static bool s_treatControlCAsInput;
        private static Encoding? s_outputEncoding;

        internal static void EnsureConsoleInitialized() { }

        /// <summary>
        /// True when an interactive Carbide session has installed the
        /// <c>globalThis.Carbide.Terminal.*</c> bridge. Decided via an
        /// <see cref="AppContext"/> flag that <c>Carbide.Core</c> flips inside the
        /// <c>runInteractive</c> run region. Outside an interactive run the fork's cosmetic
        /// emitters degrade to <see cref="PlatformNotSupportedException"/> with the same
        /// "use runInteractive" message as pre-T3 did, so non-interactive runs that call
        /// <see cref="Console.ForegroundColor"/> fail loudly instead of silently leaking
        /// ANSI into the captured stdOut buffer.
        /// </summary>
        private static bool IsInteractiveBridgeLive()
            => AppContext.GetData("Carbide.InteractiveBridge") is true;

        private static void EnsureInteractive(string memberDescription)
        {
            if (!IsInteractiveBridgeLive())
            {
                throw new PlatformNotSupportedException(
                    $"{memberDescription} requires an interactive Carbide run. " +
                    "Start one with Project.runInteractive(...) or remove the call.");
            }
        }

        // ---- Streams (minimal; real output goes through Console.Out → StreamWriter → stream) ----

        internal static Stream OpenStandardInput()
            => throw new PlatformNotSupportedException(
                "Console.OpenStandardInput() is not wired on Carbide's browser-wasm fork. " +
                "Use Console.In.ReadLineAsync() or Carbide.Terminal.CarbideConsole.ReadLineAsync() instead.");

        internal static Stream OpenStandardOutput() => new CarbideStdWriteStream(isError: false);
        internal static Stream OpenStandardError() => new CarbideStdWriteStream(isError: true);

        internal static SafeFileHandle OpenStandardInputHandle()
            => throw new PlatformNotSupportedException();
        internal static SafeFileHandle OpenStandardOutputHandle() => new SafeFileHandle((IntPtr)1, ownsHandle: false);
        internal static SafeFileHandle OpenStandardErrorHandle() => new SafeFileHandle((IntPtr)2, ownsHandle: false);

        // ---- Encoding ---------------------------------------------------------------------

        internal static Encoding InputEncoding
            => throw new PlatformNotSupportedException(
                "Console.InputEncoding is not supported on Carbide's browser-wasm fork.");

        internal static void SetConsoleInputEncoding(Encoding enc)
            => throw new PlatformNotSupportedException(
                "Console.InputEncoding is not supported on Carbide's browser-wasm fork.");

        internal static Encoding OutputEncoding => s_outputEncoding ??= Encoding.UTF8;

        internal static void SetConsoleOutputEncoding(Encoding enc) => s_outputEncoding = enc;

        // ---- Redirection ------------------------------------------------------------------

        internal static bool IsInputRedirectedCore() => false;
        internal static bool IsOutputRedirectedCore() => false;
        internal static bool IsErrorRedirectedCore() => false;

        // ---- Reader -----------------------------------------------------------------------

        internal static TextReader GetOrCreateReader()
        {
            // If the Carbide interactive path installed its BrowserTerminalReader into
            // Console._in via reflection (T2), the Console.In getter never reaches here.
            // This fallback path is hit only from non-interactive runs (project.run())
            // or outside an active `runInteractive`; synchronous reads throw PNS with a
            // pointed message (same policy as T2's BrowserTerminalReader).
            return new PointedThrowReader();
        }

        private sealed class PointedThrowReader : TextReader
        {
            public override int Read() => Throw();
            public override int Read(char[] buffer, int index, int count) => Throw();
            public override int Read(Span<char> buffer) => Throw();
            public override int ReadBlock(char[] buffer, int index, int count) => Throw();
            public override int ReadBlock(Span<char> buffer) => Throw();
            public override string? ReadLine() { Throw(); return null; }
            public override string ReadToEnd() { Throw(); return string.Empty; }
            public override int Peek() => Throw();

            private static int Throw() => throw new PlatformNotSupportedException(
                "Synchronous reads from Console.In would deadlock the Mono-WASM main thread. " +
                "Start an interactive run via Project.runInteractive() and use " +
                "Console.In.ReadLineAsync() or Carbide.Terminal.CarbideConsole.ReadLineAsync() instead.");
        }

        // ---- Key state --------------------------------------------------------------------

        internal static bool KeyAvailable => false;

        internal static ConsoleKeyInfo ReadKey(bool intercept)
            => throw new PlatformNotSupportedException(
                "Console.ReadKey() is synchronous and would deadlock the Mono-WASM main thread. " +
                "Use Carbide.Terminal.CarbideConsole.ReadKeyAsync(intercept) in an interactive " +
                "run instead.");

        internal static bool TreatControlCAsInput
        {
            get => s_treatControlCAsInput;
            set
            {
                s_treatControlCAsInput = value;
                if (IsInteractiveBridgeLive())
                {
                    CarbideBridge.NotifyTreatControlCAsInput(value);
                }
            }
        }

        // ---- Colors (SGR emitters) --------------------------------------------------------

        internal static ConsoleColor ForegroundColor
        {
            get => s_foregroundColor;
            set
            {
                EnsureInteractive("Console.ForegroundColor setter");
                s_foregroundColor = value;
                WriteAnsi(AnsiFg(value));
            }
        }

        internal static ConsoleColor BackgroundColor
        {
            get => s_backgroundColor;
            set
            {
                EnsureInteractive("Console.BackgroundColor setter");
                s_backgroundColor = value;
                WriteAnsi(AnsiBg(value));
            }
        }

        internal static void ResetColor()
        {
            EnsureInteractive("Console.ResetColor()");
            s_foregroundColor = ConsoleColor.Gray;
            s_backgroundColor = ConsoleColor.Black;
            WriteAnsi("\x1b[39;49m");
        }

        // ---- Cursor -----------------------------------------------------------------------

        internal static int CursorSize
        {
            get => 100;
            set { /* no-op; xterm.js doesn't expose a programmatic cursor-size API */ }
        }

        internal static bool CursorVisible
        {
            get => s_cursorVisible;
            set
            {
                EnsureInteractive("Console.CursorVisible setter");
                s_cursorVisible = value;
                WriteAnsi(value ? "\x1b[?25h" : "\x1b[?25l");
            }
        }

        internal static (int Left, int Top) GetCursorPosition()
            => throw new PlatformNotSupportedException(
                "Console.GetCursorPosition() requires DSR reply pre-filtering that lands with " +
                "Carbide's T3.1 phase. Track cursor yourself or emit \\x1b[6n via Console.Write " +
                "and parse the reply from Console.In.");

        internal static void SetCursorPosition(int left, int top)
        {
            EnsureInteractive("Console.SetCursorPosition");
            // CUP is 1-based on the wire; the public `Console.SetCursorPosition` takes 0-based
            // coords per its signature, so add 1 here.
            var esc = "\x1b["
                + (top + 1).ToString(CultureInfo.InvariantCulture)
                + ";"
                + (left + 1).ToString(CultureInfo.InvariantCulture)
                + "H";
            WriteAnsi(esc);
        }

        // ---- Title ------------------------------------------------------------------------

        internal static string Title
        {
            get => throw new PlatformNotSupportedException(
                "Console.Title getter is not supported on Carbide's browser-wasm fork. Setter works.");
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                EnsureInteractive("Console.Title setter");
                WriteAnsi("\x1b]0;" + value + "\x07");
            }
        }

        // ---- Beep / buffer-area / clear ---------------------------------------------------

        internal static void Beep()
        {
            EnsureInteractive("Console.Beep()");
            // BEL (\x07) on xterm rings the terminal bell if the host page has it enabled.
            WriteAnsi("\x07");
        }

        internal static void Beep(int frequency, int duration)
            => throw new PlatformNotSupportedException(
                "Console.Beep(frequency, duration) has no browser equivalent; use Console.Beep() " +
                "for the single-tone BEL or omit it entirely.");

        internal static void MoveBufferArea(int sourceLeft, int sourceTop, int sourceWidth,
                                            int sourceHeight, int targetLeft, int targetTop,
                                            char sourceChar, ConsoleColor sourceForeColor,
                                            ConsoleColor sourceBackColor)
            => throw new PlatformNotSupportedException(
                "Console.MoveBufferArea is not supported on Carbide's browser-wasm fork — the " +
                "terminal has no separate scroll-back buffer to move from.");

        internal static void Clear()
        {
            EnsureInteractive("Console.Clear()");
            WriteAnsi("\x1b[2J\x1b[H");
        }

        // ---- Buffer / window geometry -----------------------------------------------------

        internal static int BufferWidth
        {
            get => WindowWidth;
            set => throw new PlatformNotSupportedException(
                "Console.BufferWidth setter is not supported on Carbide's browser-wasm fork.");
        }

        internal static int BufferHeight
        {
            get => WindowHeight;
            set => throw new PlatformNotSupportedException(
                "Console.BufferHeight setter is not supported on Carbide's browser-wasm fork.");
        }

        internal static void SetBufferSize(int width, int height)
            => throw new PlatformNotSupportedException(
                "Console.SetBufferSize is not supported on Carbide's browser-wasm fork.");

        internal static int LargestWindowWidth => WindowWidth;
        internal static int LargestWindowHeight => WindowHeight;

        internal static int WindowLeft
        {
            get => 0;
            set => throw new PlatformNotSupportedException();
        }

        internal static int WindowTop
        {
            get => 0;
            set => throw new PlatformNotSupportedException();
        }

        internal static int WindowWidth
        {
            get => IsInteractiveBridgeLive() ? CarbideBridge.GetCols() : 80;
            set => throw new PlatformNotSupportedException();
        }

        internal static int WindowHeight
        {
            get => IsInteractiveBridgeLive() ? CarbideBridge.GetRows() : 24;
            set => throw new PlatformNotSupportedException();
        }

        internal static void SetWindowPosition(int left, int top)
            => throw new PlatformNotSupportedException();
        internal static void SetWindowSize(int width, int height)
            => throw new PlatformNotSupportedException();

        // ---- Numeric / caps lock ----------------------------------------------------------

        internal static bool NumberLock
            => throw new PlatformNotSupportedException(
                "Console.NumberLock is not supported on Carbide's browser-wasm fork.");
        internal static bool CapsLock
            => throw new PlatformNotSupportedException(
                "Console.CapsLock is not supported on Carbide's browser-wasm fork.");

        // ---- Helpers ----------------------------------------------------------------------

        private static void WriteAnsi(string ansi)
        {
            // Route through Console.Out so SGR interleaves with buffered `Console.Write` output
            // in the correct order. When Console.Out has been redirected (e.g. the user called
            // Console.SetOut), the SGR still lands there — matches stock Unix behavior.
            Console.Out.Write(ansi);
        }

        private static string AnsiFg(ConsoleColor c) => "\x1b[" + FgCode(c) + "m";
        private static string AnsiBg(ConsoleColor c) => "\x1b[" + BgCode(c) + "m";

        private static string FgCode(ConsoleColor c) => c switch
        {
            ConsoleColor.Black => "30",
            ConsoleColor.DarkRed => "31",
            ConsoleColor.DarkGreen => "32",
            ConsoleColor.DarkYellow => "33",
            ConsoleColor.DarkBlue => "34",
            ConsoleColor.DarkMagenta => "35",
            ConsoleColor.DarkCyan => "36",
            ConsoleColor.Gray => "37",
            ConsoleColor.DarkGray => "90",
            ConsoleColor.Red => "91",
            ConsoleColor.Green => "92",
            ConsoleColor.Yellow => "93",
            ConsoleColor.Blue => "94",
            ConsoleColor.Magenta => "95",
            ConsoleColor.Cyan => "96",
            ConsoleColor.White => "97",
            _ => "39",
        };

        private static string BgCode(ConsoleColor c) => c switch
        {
            ConsoleColor.Black => "40",
            ConsoleColor.DarkRed => "41",
            ConsoleColor.DarkGreen => "42",
            ConsoleColor.DarkYellow => "43",
            ConsoleColor.DarkBlue => "44",
            ConsoleColor.DarkMagenta => "45",
            ConsoleColor.DarkCyan => "46",
            ConsoleColor.Gray => "47",
            ConsoleColor.DarkGray => "100",
            ConsoleColor.Red => "101",
            ConsoleColor.Green => "102",
            ConsoleColor.Yellow => "103",
            ConsoleColor.Blue => "104",
            ConsoleColor.Magenta => "105",
            ConsoleColor.Cyan => "106",
            ConsoleColor.White => "107",
            _ => "49",
        };
    }

    /// <summary>
    /// Stream wrapping around Carbide's JS bridge write path. `Console.OpenStandardOutput()`
    /// and `Console.OpenStandardError()` return instances of this; bytes written through
    /// <c>Console.Out</c> / <c>Console.Error</c> (after the T1 run path installs a
    /// <see cref="Carbide.Terminal.StreamingStdOutWriter"/> over them) land at the JS terminal
    /// bridge. Outside an active interactive run, <see cref="CarbideBridge.WriteStdOut"/>
    /// falls through to <c>console.log</c> via the T1 host-adapter overlay.
    /// </summary>
    internal sealed class CarbideStdWriteStream : Stream
    {
        private readonly bool _isError;

        public CarbideStdWriteStream(bool isError) { _isError = isError; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { /* JS bridge is fire-and-forget; nothing to flush. */ }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty) return;
            var text = Encoding.UTF8.GetString(buffer);
            if (_isError) CarbideBridge.WriteStdErr(text);
            else CarbideBridge.WriteStdOut(text);
        }
    }
}

namespace System
{
    /// <summary>
    /// JSImport declarations targeting the T1/T2 `globalThis.Carbide.Terminal.*` bridge.
    /// Lives in the forked <c>System.Console.dll</c> so we don't take an assembly dependency
    /// on <c>Carbide.Core.dll</c>; the JS targets are installed by Carbide's session shell
    /// before any `Console.*` call lands here.
    ///
    /// Outside an interactive run the JS targets are absent and the JSImports throw; the T1
    /// emscripten `print` overlay (`BrowserHostAdapter.resolveRuntimeConfigOverlays`) makes
    /// non-interactive writes reach `console.log` instead of the bridge, so this code path
    /// is only hit inside `runInteractive`.
    /// </summary>
    internal static partial class CarbideBridge
    {
        [JSImport("globalThis.Carbide.Terminal.write")]
        internal static partial void WriteStdOut(string text);

        [JSImport("globalThis.Carbide.Terminal.writeErr")]
        internal static partial void WriteStdErr(string text);

        [JSImport("globalThis.Carbide.Terminal.setTreatControlCAsInput")]
        internal static partial void NotifyTreatControlCAsInput(bool value);

        [JSImport("globalThis.Carbide.Terminal.getCols")]
        internal static partial int GetCols();

        [JSImport("globalThis.Carbide.Terminal.getRows")]
        internal static partial int GetRows();
    }
}
