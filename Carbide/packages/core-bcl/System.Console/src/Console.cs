// T3 — Carbide's forked `Console` class. Public surface matches the stock BCL's Console
// so user code + pre-compiled libraries link against the same metadata. Behavior diverges
// in ConsolePal.Browser (this fork's custom implementation) where most members used to
// throw `PlatformNotSupportedException` and now route through the Carbide bridge.
//
// This is NOT upstream's Console.cs verbatim. It's a scoped rewrite that skips paths
// Carbide doesn't need on browser-wasm (PosixSignalRegistration, the synchronized-text-
// writer machinery, the Unix/Windows/iOS platform-specific stream wrapping). The public
// API surface matches the stock DLL; internal plumbing is simplified.

using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace System
{
    /// <summary>
    /// Represents the standard input, output, and error streams for console applications.
    /// Carbide-forked version — see the T3 detailed plan for the scope.
    /// </summary>
    public static class Console
    {
        // Default write-buffer size mirrors upstream's. No visible-to-user difference; we
        // auto-flush each Write/WriteLine through our streaming path anyway.
        private const int WriteBufferSize = 256;

        private static readonly object s_syncObject = new object();
        private static TextReader? s_in;
        private static TextWriter? s_out;
        private static TextWriter? s_error;
        private static Encoding? s_inputEncoding;
        private static Encoding? s_outputEncoding;
        private static bool s_isOutTextWriterRedirected;
        private static bool s_isErrorTextWriterRedirected;

        private static ConsoleCancelEventHandler? s_cancelCallbacks;

        // ---- In / Out / Error --------------------------------------------------------

        public static TextReader In
        {
            get
            {
                return Volatile.Read(ref s_in) ?? EnsureInitialized();

                static TextReader EnsureInitialized()
                {
                    ConsolePal.EnsureConsoleInitialized();
                    lock (s_syncObject)
                    {
                        s_in ??= ConsolePal.GetOrCreateReader();
                        return s_in;
                    }
                }
            }
        }

        public static TextWriter Out
        {
            get
            {
                return Volatile.Read(ref s_out) ?? EnsureInitialized();

                static TextWriter EnsureInitialized()
                {
                    ConsolePal.EnsureConsoleInitialized();
                    lock (s_syncObject)
                    {
                        if (s_out is null)
                        {
                            Volatile.Write(ref s_out, CreateOutputWriter(ConsolePal.OpenStandardOutput()));
                        }
                        return s_out;
                    }
                }
            }
        }

        public static TextWriter Error
        {
            get
            {
                return Volatile.Read(ref s_error) ?? EnsureInitialized();

                static TextWriter EnsureInitialized()
                {
                    ConsolePal.EnsureConsoleInitialized();
                    lock (s_syncObject)
                    {
                        if (s_error is null)
                        {
                            Volatile.Write(ref s_error, CreateOutputWriter(ConsolePal.OpenStandardError()));
                        }
                        return s_error;
                    }
                }
            }
        }

        private static TextWriter CreateOutputWriter(Stream outputStream)
        {
            if (outputStream == Stream.Null)
            {
                return TextWriter.Null;
            }
            return TextWriter.Synchronized(
                new StreamWriter(
                    stream: outputStream,
                    encoding: OutputEncoding.RemovePreamble(),
                    bufferSize: WriteBufferSize,
                    leaveOpen: true)
                { AutoFlush = true });
        }

        // ---- Encoding ----------------------------------------------------------------

        public static Encoding InputEncoding
        {
            get
            {
                Encoding? encoding = Volatile.Read(ref s_inputEncoding);
                if (encoding is null)
                {
                    lock (s_syncObject)
                    {
                        s_inputEncoding ??= ConsolePal.InputEncoding;
                        encoding = s_inputEncoding;
                    }
                }
                return encoding;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (s_syncObject)
                {
                    ConsolePal.SetConsoleInputEncoding(value);
                    Volatile.Write(ref s_inputEncoding, (Encoding)value.Clone());
                    s_in = null;
                }
            }
        }

        public static Encoding OutputEncoding
        {
            get
            {
                Encoding? encoding = Volatile.Read(ref s_outputEncoding);
                if (encoding is null)
                {
                    lock (s_syncObject)
                    {
                        s_outputEncoding ??= ConsolePal.OutputEncoding;
                        encoding = s_outputEncoding;
                    }
                }
                return encoding;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (s_syncObject)
                {
                    ConsolePal.SetConsoleOutputEncoding(value);

                    if (s_out is not null && !s_isOutTextWriterRedirected)
                    {
                        s_out.Flush();
                        Volatile.Write(ref s_out, null);
                    }
                    if (s_error is not null && !s_isErrorTextWriterRedirected)
                    {
                        s_error.Flush();
                        Volatile.Write(ref s_error, null);
                    }
                    Volatile.Write(ref s_outputEncoding, (Encoding)value.Clone());
                }
            }
        }

        // ---- Key / Input -------------------------------------------------------------

        public static bool KeyAvailable => ConsolePal.KeyAvailable;

        public static ConsoleKeyInfo ReadKey() => ConsolePal.ReadKey(false);
        public static ConsoleKeyInfo ReadKey(bool intercept) => ConsolePal.ReadKey(intercept);

        public static bool TreatControlCAsInput
        {
            get => ConsolePal.TreatControlCAsInput;
            set => ConsolePal.TreatControlCAsInput = value;
        }

        // ---- SetIn / SetOut / SetError -----------------------------------------------

        public static void SetIn(TextReader newIn)
        {
            ArgumentNullException.ThrowIfNull(newIn);
            newIn = SyncTextReaderHelper.GetSynchronizedTextReader(newIn);
            lock (s_syncObject)
            {
                Volatile.Write(ref s_in, newIn);
            }
        }

        public static void SetOut(TextWriter newOut)
        {
            ArgumentNullException.ThrowIfNull(newOut);
            newOut = TextWriter.Synchronized(newOut);
            lock (s_syncObject)
            {
                s_isOutTextWriterRedirected = true;
                Volatile.Write(ref s_out, newOut);
            }
        }

        public static void SetError(TextWriter newError)
        {
            ArgumentNullException.ThrowIfNull(newError);
            newError = TextWriter.Synchronized(newError);
            lock (s_syncObject)
            {
                s_isErrorTextWriterRedirected = true;
                Volatile.Write(ref s_error, newError);
            }
        }

        // ---- Redirection flags -------------------------------------------------------

        private static volatile StrongBox<bool>? _isStdInRedirected;
        private static volatile StrongBox<bool>? _isStdOutRedirected;
        private static volatile StrongBox<bool>? _isStdErrRedirected;

        public static bool IsInputRedirected
        {
            get
            {
                StrongBox<bool>? redirected = _isStdInRedirected ?? EnsureInitialized();
                return redirected.Value;

                static StrongBox<bool> EnsureInitialized()
                {
                    Volatile.Write(ref _isStdInRedirected, new StrongBox<bool>(ConsolePal.IsInputRedirectedCore()));
                    return _isStdInRedirected!;
                }
            }
        }

        public static bool IsOutputRedirected
        {
            get
            {
                StrongBox<bool>? redirected = _isStdOutRedirected ?? EnsureInitialized();
                return redirected.Value;

                static StrongBox<bool> EnsureInitialized()
                {
                    Volatile.Write(ref _isStdOutRedirected, new StrongBox<bool>(ConsolePal.IsOutputRedirectedCore()));
                    return _isStdOutRedirected!;
                }
            }
        }

        public static bool IsErrorRedirected
        {
            get
            {
                StrongBox<bool>? redirected = _isStdErrRedirected ?? EnsureInitialized();
                return redirected.Value;

                static StrongBox<bool> EnsureInitialized()
                {
                    Volatile.Write(ref _isStdErrRedirected, new StrongBox<bool>(ConsolePal.IsErrorRedirectedCore()));
                    return _isStdErrRedirected!;
                }
            }
        }

        // ---- Cursor / Colors ---------------------------------------------------------

        [SupportedOSPlatform("windows")]
        public static int CursorSize
        {
            get => ConsolePal.CursorSize;
            set => ConsolePal.CursorSize = value;
        }

        [SupportedOSPlatform("windows")]
        public static bool NumberLock => ConsolePal.NumberLock;

        [SupportedOSPlatform("windows")]
        public static bool CapsLock => ConsolePal.CapsLock;

        public static ConsoleColor BackgroundColor
        {
            get => ConsolePal.BackgroundColor;
            set => ConsolePal.BackgroundColor = value;
        }

        public static ConsoleColor ForegroundColor
        {
            get => ConsolePal.ForegroundColor;
            set => ConsolePal.ForegroundColor = value;
        }

        public static void ResetColor() => ConsolePal.ResetColor();

        public static int BufferWidth
        {
            get => ConsolePal.BufferWidth;
            [SupportedOSPlatform("windows")]
            set => ConsolePal.BufferWidth = value;
        }

        public static int BufferHeight
        {
            get => ConsolePal.BufferHeight;
            [SupportedOSPlatform("windows")]
            set => ConsolePal.BufferHeight = value;
        }

        [SupportedOSPlatform("windows")]
        public static void SetBufferSize(int width, int height) => ConsolePal.SetBufferSize(width, height);

        public static int WindowLeft
        {
            get => ConsolePal.WindowLeft;
            [SupportedOSPlatform("windows")]
            set => ConsolePal.WindowLeft = value;
        }

        public static int WindowTop
        {
            get => ConsolePal.WindowTop;
            [SupportedOSPlatform("windows")]
            set => ConsolePal.WindowTop = value;
        }

        public static int WindowWidth
        {
            get => ConsolePal.WindowWidth;
            [SupportedOSPlatform("windows")]
            set => ConsolePal.WindowWidth = value;
        }

        public static int WindowHeight
        {
            get => ConsolePal.WindowHeight;
            [SupportedOSPlatform("windows")]
            set => ConsolePal.WindowHeight = value;
        }

        [SupportedOSPlatform("windows")]
        public static void SetWindowPosition(int left, int top) => ConsolePal.SetWindowPosition(left, top);

        [SupportedOSPlatform("windows")]
        public static void SetWindowSize(int width, int height) => ConsolePal.SetWindowSize(width, height);

        public static int LargestWindowWidth
        {
            [SupportedOSPlatform("windows")]
            get => ConsolePal.LargestWindowWidth;
        }

        public static int LargestWindowHeight
        {
            [SupportedOSPlatform("windows")]
            get => ConsolePal.LargestWindowHeight;
        }

        public static bool CursorVisible
        {
            [SupportedOSPlatform("windows")]
            get => ConsolePal.CursorVisible;
            set => ConsolePal.CursorVisible = value;
        }

        public static int CursorLeft
        {
            get => ConsolePal.GetCursorPosition().Left;
            set => SetCursorPosition(value, CursorTop);
        }

        public static int CursorTop
        {
            get => ConsolePal.GetCursorPosition().Top;
            set => SetCursorPosition(CursorLeft, value);
        }

        public static (int Left, int Top) GetCursorPosition() => ConsolePal.GetCursorPosition();

        public static string Title
        {
            [SupportedOSPlatform("windows")]
            get => ConsolePal.Title;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                ConsolePal.Title = value;
            }
        }

        public static void Beep() => ConsolePal.Beep();

        [SupportedOSPlatform("windows")]
        public static void Beep(int frequency, int duration) => ConsolePal.Beep(frequency, duration);

        [SupportedOSPlatform("windows")]
        public static void MoveBufferArea(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int targetLeft, int targetTop)
            => ConsolePal.MoveBufferArea(sourceLeft, sourceTop, sourceWidth, sourceHeight, targetLeft, targetTop, ' ', ConsoleColor.Black, BackgroundColor);

        [SupportedOSPlatform("windows")]
        public static void MoveBufferArea(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int targetLeft, int targetTop, char sourceChar, ConsoleColor sourceForeColor, ConsoleColor sourceBackColor)
            => ConsolePal.MoveBufferArea(sourceLeft, sourceTop, sourceWidth, sourceHeight, targetLeft, targetTop, sourceChar, sourceForeColor, sourceBackColor);

        public static void Clear() => ConsolePal.Clear();

        public static void SetCursorPosition(int left, int top)
        {
            if (left < 0 || left >= short.MaxValue) throw new ArgumentOutOfRangeException(nameof(left));
            if (top < 0 || top >= short.MaxValue) throw new ArgumentOutOfRangeException(nameof(top));
            ConsolePal.SetCursorPosition(left, top);
        }

        // ---- CancelKeyPress ----------------------------------------------------------

        public static event ConsoleCancelEventHandler? CancelKeyPress
        {
            add
            {
                lock (s_syncObject)
                {
                    s_cancelCallbacks = (ConsoleCancelEventHandler?)Delegate.Combine(s_cancelCallbacks, value);
                }
            }
            remove
            {
                lock (s_syncObject)
                {
                    s_cancelCallbacks = (ConsoleCancelEventHandler?)Delegate.Remove(s_cancelCallbacks, value);
                }
            }
        }

        /// <summary>
        /// Internal entry point the Carbide bridge calls (via reflection from Carbide.Core)
        /// when the JS side delivers a SIGINT. Matches the BCL's HandlePosixSignal shape
        /// so upstream Console.cs could be dropped in later if someone vendors it.
        /// </summary>
        internal static bool HandleCancelKeyPress(ConsoleSpecialKey specialKey)
        {
            ConsoleCancelEventHandler? handler = s_cancelCallbacks;
            if (handler is null) return false;
            var args = new ConsoleCancelEventArgs(specialKey);
            handler(null, args);
            return args.Cancel;
        }

        // ---- OpenStandard* -----------------------------------------------------------

        public static Stream OpenStandardInput() => ConsolePal.OpenStandardInput();
        public static Stream OpenStandardInput(int bufferSize)
        {
            if (bufferSize < 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            return ConsolePal.OpenStandardInput();
        }
        public static Stream OpenStandardOutput() => ConsolePal.OpenStandardOutput();
        public static Stream OpenStandardOutput(int bufferSize)
        {
            if (bufferSize < 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            return ConsolePal.OpenStandardOutput();
        }
        public static Stream OpenStandardError() => ConsolePal.OpenStandardError();
        public static Stream OpenStandardError(int bufferSize)
        {
            if (bufferSize < 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            return ConsolePal.OpenStandardError();
        }

        // ---- Read* -------------------------------------------------------------------

        public static int Read() => In.Read();
        public static string? ReadLine() => In.ReadLine();

        // ---- Write* / WriteLine* -----------------------------------------------------

        public static void WriteLine() => Out.WriteLine();
        public static void WriteLine(string? value) => Out.WriteLine(value);
        public static void WriteLine(object? value) => Out.WriteLine(value);
        public static void WriteLine(bool value) => Out.WriteLine(value);
        public static void WriteLine(char value) => Out.WriteLine(value);
        public static void WriteLine(char[]? buffer) => Out.WriteLine(buffer);
        public static void WriteLine(char[] buffer, int index, int count) => Out.WriteLine(buffer, index, count);
        public static void WriteLine(decimal value) => Out.WriteLine(value);
        public static void WriteLine(double value) => Out.WriteLine(value);
        public static void WriteLine(float value) => Out.WriteLine(value);
        public static void WriteLine(int value) => Out.WriteLine(value);
        [CLSCompliant(false)] public static void WriteLine(uint value) => Out.WriteLine(value);
        public static void WriteLine(long value) => Out.WriteLine(value);
        [CLSCompliant(false)] public static void WriteLine(ulong value) => Out.WriteLine(value);
        public static void WriteLine(string format, object? arg0) => Out.WriteLine(format, arg0);
        public static void WriteLine(string format, object? arg0, object? arg1) => Out.WriteLine(format, arg0, arg1);
        public static void WriteLine(string format, object? arg0, object? arg1, object? arg2) => Out.WriteLine(format, arg0, arg1, arg2);
        public static void WriteLine(string format, params object?[]? arg) => Out.WriteLine(format, arg);
        public static void WriteLine(string format, params ReadOnlySpan<object?> arg) => Out.WriteLine(format, arg);

        public static void Write(string format, object? arg0) => Out.Write(format, arg0);
        public static void Write(string format, object? arg0, object? arg1) => Out.Write(format, arg0, arg1);
        public static void Write(string format, object? arg0, object? arg1, object? arg2) => Out.Write(format, arg0, arg1, arg2);
        public static void Write(string format, params object?[]? arg) => Out.Write(format, arg);
        public static void Write(string format, params ReadOnlySpan<object?> arg) => Out.Write(format, arg);
        public static void Write(bool value) => Out.Write(value);
        public static void Write(char value) => Out.Write(value);
        public static void Write(char[]? buffer) => Out.Write(buffer);
        public static void Write(char[] buffer, int index, int count) => Out.Write(buffer, index, count);
        public static void Write(double value) => Out.Write(value);
        public static void Write(decimal value) => Out.Write(value);
        public static void Write(float value) => Out.Write(value);
        public static void Write(int value) => Out.Write(value);
        [CLSCompliant(false)] public static void Write(uint value) => Out.Write(value);
        public static void Write(long value) => Out.Write(value);
        [CLSCompliant(false)] public static void Write(ulong value) => Out.Write(value);
        public static void Write(object? value) => Out.Write(value);
        public static void Write(string? value) => Out.Write(value);
    }

    /// <summary>
    /// Internal helper for SetIn's TextReader wrapping. Kept separate so we don't have to
    /// re-vendor upstream's SyncTextReader.
    /// </summary>
    internal static class SyncTextReaderHelper
    {
        // Mirrors TextReader.Synchronized; the fork's simple path works for our use.
        public static TextReader GetSynchronizedTextReader(TextReader reader)
            => TextReader.Synchronized(reader);
    }
}

namespace System.Text
{
    internal static class CarbideEncodingExtensions
    {
        public static Encoding RemovePreamble(this Encoding encoding)
        {
            return encoding.Preamble.IsEmpty ? encoding : new ConsolePreambleStrippedEncoding(encoding);
        }

        private sealed class ConsolePreambleStrippedEncoding : Encoding
        {
            private readonly Encoding _inner;
            public ConsolePreambleStrippedEncoding(Encoding inner) { _inner = inner; }
            public override ReadOnlySpan<byte> Preamble => ReadOnlySpan<byte>.Empty;
            public override int GetByteCount(char[] chars, int index, int count) => _inner.GetByteCount(chars, index, count);
            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => _inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            public override int GetCharCount(byte[] bytes, int index, int count) => _inner.GetCharCount(bytes, index, count);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => _inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            public override int GetMaxByteCount(int charCount) => _inner.GetMaxByteCount(charCount);
            public override int GetMaxCharCount(int byteCount) => _inner.GetMaxCharCount(byteCount);
            public override byte[] GetPreamble() => Array.Empty<byte>();
        }
    }
}
