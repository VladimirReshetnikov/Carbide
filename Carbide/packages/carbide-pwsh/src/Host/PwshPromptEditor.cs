using System.Reflection;

namespace CarbidePwsh.Host;

public enum PromptReadResultKind
{
    Submitted,
    Interrupted,
    EndOfInput,
}

public readonly record struct PromptReadResult(PromptReadResultKind Kind, string? Line)
{
    public static PromptReadResult Submitted(string line) => new(PromptReadResultKind.Submitted, line);
    public static PromptReadResult Interrupted() => new(PromptReadResultKind.Interrupted, null);
    public static PromptReadResult EndOfInput() => new(PromptReadResultKind.EndOfInput, null);
}

public sealed class PwshPromptEditor
{
    private const int MaxHistoryEntries = 512;
    private readonly ShellHost _host;
    private readonly IPwshPromptConsole _console;
    private readonly PromptCompletionService _completionService;
    private readonly List<string> _history = [];

    public PwshPromptEditor(ShellHost host, IPwshPromptConsole? console = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _console = console ?? new SystemPwshPromptConsole();
        _completionService = new PromptCompletionService(_host);
    }

    public IReadOnlyList<string> History => _history;

    public async Task<PromptReadResult> ReadLineAsync(
        string prompt,
        bool allowHistory = true,
        CancellationToken cancellationToken = default)
    {
        if (!_console.SupportsInteractiveEditing)
        {
            _console.Write(prompt);
            _console.Flush();
            var line = await _console.ReadLineAsync(cancellationToken);
            if (line is null)
                return PromptReadResult.EndOfInput();
            RememberHistory(line);
            return PromptReadResult.Submitted(line);
        }

        _console.Write(prompt);
        _console.Flush();

        string buffer = "";
        int cursor = 0;
        string historyDraft = "";
        int? historyIndex = null;
        CompletionSession? completion = null;
        bool overwrite = false;

        void MarkEdited()
        {
            historyIndex = null;
            historyDraft = buffer;
            completion = null;
        }

        var originalTreatControlCAsInput = _console.TreatControlCAsInput;
        _console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                var key = await _console.ReadKeyAsync(cancellationToken);
                if (key is null)
                {
                    _console.Write("\r\n");
                    _console.Flush();
                    return PromptReadResult.EndOfInput();
                }

                if (IsCtrlC(key.Value))
                {
                    buffer = "";
                    cursor = 0;
                    historyIndex = null;
                    historyDraft = "";
                    completion = null;

                    _console.Write("\r\x1b[2K");
                    _console.Write("\x1b[31m^C\x1b[0m\r\n");
                    _console.Flush();
                    return PromptReadResult.Interrupted();
                }

                if (IsCtrlL(key.Value))
                {
                    completion = null;
                    RedrawLine(prompt, buffer, cursor, clearScreenFirst: true);
                    continue;
                }

                if (IsCtrlA(key.Value))
                {
                    completion = null;
                    if (cursor == 0) continue;
                    MoveCursorLeft(cursor);
                    cursor = 0;
                    continue;
                }

                if (IsCtrlE(key.Value))
                {
                    completion = null;
                    if (cursor >= buffer.Length) continue;
                    MoveCursorRight(buffer.Length - cursor);
                    cursor = buffer.Length;
                    continue;
                }

                if (IsMovePreviousWord(key.Value))
                {
                    completion = null;
                    MoveCursorTo(ref cursor, FindPreviousWordBoundary(buffer, cursor));
                    continue;
                }

                if (IsMoveNextWord(key.Value))
                {
                    completion = null;
                    MoveCursorTo(ref cursor, FindNextWordBoundary(buffer, cursor));
                    continue;
                }

                if (IsDeletePreviousWord(key.Value))
                {
                    if (cursor == 0) continue;
                    var start = FindPreviousWordBoundary(buffer, cursor);
                    buffer = buffer.Remove(start, cursor - start);
                    cursor = start;
                    MarkEdited();
                    RedrawLine(prompt, buffer, cursor);
                    continue;
                }

                if (IsDeleteNextWord(key.Value))
                {
                    if (cursor >= buffer.Length) continue;
                    var end = FindNextWordBoundary(buffer, cursor);
                    buffer = buffer.Remove(cursor, end - cursor);
                    MarkEdited();
                    RedrawLine(prompt, buffer, cursor);
                    continue;
                }

                if (IsCtrlU(key.Value))
                {
                    if (cursor == 0) continue;
                    buffer = buffer.Remove(0, cursor);
                    cursor = 0;
                    MarkEdited();
                    RedrawLine(prompt, buffer, cursor);
                    continue;
                }

                if (IsCtrlK(key.Value))
                {
                    if (cursor >= buffer.Length) continue;
                    buffer = buffer[..cursor];
                    MarkEdited();
                    RedrawLine(prompt, buffer, cursor);
                    continue;
                }

                if (IsCtrlD(key.Value))
                {
                    completion = null;
                    if (buffer.Length == 0)
                    {
                        _console.Write("\r\n");
                        _console.Flush();
                        return PromptReadResult.EndOfInput();
                    }

                    if (cursor >= buffer.Length) continue;
                    buffer = buffer.Remove(cursor, 1);
                    MarkEdited();
                    RedrawLine(prompt, buffer, cursor);
                    continue;
                }

                switch (key.Value.Key)
                {
                    case ConsoleKey.Enter:
                        _console.Write("\r\n");
                        _console.Flush();
                        RememberHistory(buffer);
                        return PromptReadResult.Submitted(buffer);

                    case ConsoleKey.Escape:
                        if (buffer.Length == 0) continue;
                        buffer = "";
                        cursor = 0;
                        historyIndex = null;
                        historyDraft = "";
                        completion = null;
                        RedrawLine(prompt, buffer, cursor);
                        continue;

                    case ConsoleKey.Backspace:
                        if (cursor == 0) continue;
                        buffer = buffer.Remove(cursor - 1, 1);
                        cursor--;
                        MarkEdited();
                        RedrawLine(prompt, buffer, cursor);
                        continue;

                    case ConsoleKey.Delete:
                        if (cursor >= buffer.Length) continue;
                        buffer = buffer.Remove(cursor, 1);
                        MarkEdited();
                        RedrawLine(prompt, buffer, cursor);
                        continue;

                    case ConsoleKey.LeftArrow:
                        completion = null;
                        if (cursor == 0) continue;
                        cursor--;
                        MoveCursorLeft(1);
                        continue;

                    case ConsoleKey.RightArrow:
                        completion = null;
                        if (cursor >= buffer.Length) continue;
                        cursor++;
                        MoveCursorRight(1);
                        continue;

                    case ConsoleKey.Home:
                        completion = null;
                        if (cursor == 0) continue;
                        MoveCursorLeft(cursor);
                        cursor = 0;
                        continue;

                    case ConsoleKey.End:
                        completion = null;
                        if (cursor >= buffer.Length) continue;
                        MoveCursorRight(buffer.Length - cursor);
                        cursor = buffer.Length;
                        continue;

                    case ConsoleKey.Insert:
                        completion = null;
                        overwrite = !overwrite;
                        continue;

                    case ConsoleKey.UpArrow:
                        completion = null;
                        if (!allowHistory || _history.Count == 0) continue;
                        if (historyIndex is null)
                        {
                            historyDraft = buffer;
                            historyIndex = _history.Count - 1;
                        }
                        else
                        {
                            historyIndex = historyIndex.Value == 0
                                ? _history.Count - 1
                                : historyIndex.Value - 1;
                        }
                        buffer = _history[historyIndex.Value];
                        cursor = buffer.Length;
                        RedrawLine(prompt, buffer, cursor);
                        continue;

                    case ConsoleKey.DownArrow:
                        completion = null;
                        if (!allowHistory || _history.Count == 0) continue;
                        if (historyIndex is null)
                        {
                            historyDraft = buffer;
                            historyIndex = 0;
                            buffer = _history[0];
                        }
                        else if (historyIndex.Value >= _history.Count - 1)
                        {
                            historyIndex = null;
                            buffer = historyDraft;
                        }
                        else
                        {
                            historyIndex++;
                            buffer = _history[historyIndex.Value];
                        }
                        cursor = buffer.Length;
                        RedrawLine(prompt, buffer, cursor);
                        continue;

                    case ConsoleKey.Tab:
                        if (TryApplyCompletion(
                            ref buffer,
                            ref cursor,
                            ref completion,
                            reverse: IsReverseTab(key.Value)))
                        {
                            historyIndex = null;
                            historyDraft = buffer;
                            RedrawLine(prompt, buffer, cursor);
                        }
                        else
                        {
                            _console.Write("\a");
                            _console.Flush();
                        }
                        continue;
                }

                var ch = key.Value.KeyChar;
                if (char.IsControl(ch)) continue;

                completion = null;
                historyIndex = null;

                if (cursor == buffer.Length)
                {
                    buffer += ch;
                    cursor++;
                    historyDraft = buffer;
                    _console.Write(ch.ToString());
                    _console.Flush();
                    continue;
                }

                buffer = overwrite
                    ? buffer.Remove(cursor, 1).Insert(cursor, ch.ToString())
                    : buffer.Insert(cursor, ch.ToString());
                cursor++;
                historyDraft = buffer;
                RedrawLine(prompt, buffer, cursor);
            }
        }
        finally
        {
            _console.TreatControlCAsInput = originalTreatControlCAsInput;
        }
    }

    internal void AddHistoryEntry(string line) => RememberHistory(line);

    private bool TryApplyCompletion(
        ref string buffer,
        ref int cursor,
        ref CompletionSession? completion,
        bool reverse)
    {
        if (completion is not null && completion.IsActive(buffer, cursor))
        {
            completion = reverse ? completion.Previous() : completion.Next();
            buffer = completion.ComposeBuffer();
            cursor = completion.Cursor;
            return true;
        }

        completion = null;
        var result = _completionService.Complete(buffer, cursor);
        if (result is null || result.Matches.Count == 0)
            return false;

        completion = new CompletionSession(
            result.Before,
            result.After,
            result.Matches,
            Index: reverse ? result.Matches.Count - 1 : 0);
        buffer = completion.ComposeBuffer();
        cursor = completion.Cursor;
        return true;
    }

    private void RedrawLine(string prompt, string buffer, int cursor, bool clearScreenFirst = false)
    {
        if (clearScreenFirst)
            _console.Write("\x1b[2J\x1b[H");

        _console.Write("\r\x1b[2K");
        _console.Write(prompt);
        _console.Write(buffer);

        var tail = buffer.Length - cursor;
        if (tail > 0)
            MoveCursorLeft(tail);

        _console.Flush();
    }

    private void MoveCursorLeft(int count)
    {
        if (count <= 0) return;
        _console.Write($"\x1b[{count}D");
        _console.Flush();
    }

    private void MoveCursorRight(int count)
    {
        if (count <= 0) return;
        _console.Write($"\x1b[{count}C");
        _console.Flush();
    }

    private void MoveCursorTo(ref int cursor, int target)
    {
        target = Math.Clamp(target, 0, cursor < 0 ? 0 : int.MaxValue);
        if (target == cursor)
            return;

        if (target < cursor)
            MoveCursorLeft(cursor - target);
        else
            MoveCursorRight(target - cursor);

        cursor = target;
    }

    private void RememberHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (_history.Count > 0 && string.Equals(_history[^1], line, StringComparison.Ordinal))
            return;

        if (_history.Count == MaxHistoryEntries)
            _history.RemoveAt(0);

        _history.Add(line);
    }

    private static bool IsCtrlC(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C
        || key.KeyChar == '\u0003';

    private static bool IsCtrlL(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.L
        || key.KeyChar == '\f';

    private static bool IsCtrlA(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.A
        || key.KeyChar == '\u0001';

    private static bool IsCtrlE(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.E
        || key.KeyChar == '\u0005';

    private static bool IsCtrlD(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.D
        || key.KeyChar == '\u0004';

    private static bool IsCtrlK(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.K
        || key.KeyChar == '\u000b';

    private static bool IsCtrlU(ConsoleKeyInfo key)
        => key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.U
        || key.KeyChar == '\u0015';

    private static bool IsMovePreviousWord(ConsoleKeyInfo key)
        => key.Key == ConsoleKey.LeftArrow && key.Modifiers.HasFlag(ConsoleModifiers.Control)
        || key.Key == ConsoleKey.B && key.Modifiers.HasFlag(ConsoleModifiers.Alt);

    private static bool IsMoveNextWord(ConsoleKeyInfo key)
        => key.Key == ConsoleKey.RightArrow && key.Modifiers.HasFlag(ConsoleModifiers.Control)
        || key.Key == ConsoleKey.F && key.Modifiers.HasFlag(ConsoleModifiers.Alt);

    private static bool IsDeletePreviousWord(ConsoleKeyInfo key)
        => key.Key == ConsoleKey.Backspace && key.Modifiers.HasFlag(ConsoleModifiers.Control)
        || key.Key == ConsoleKey.Backspace && key.Modifiers.HasFlag(ConsoleModifiers.Alt)
        || key.Key == ConsoleKey.W && key.Modifiers.HasFlag(ConsoleModifiers.Control)
        || key.KeyChar == '\u0017';

    private static bool IsDeleteNextWord(ConsoleKeyInfo key)
        => key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Alt);

    private static int FindPreviousWordBoundary(string text, int cursor)
    {
        var i = Math.Clamp(cursor, 0, text.Length);
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
            i--;

        if (i == 0)
            return 0;

        var kind = GetWordKind(text[i - 1]);
        while (i > 0 && !char.IsWhiteSpace(text[i - 1]) && GetWordKind(text[i - 1]) == kind)
            i--;

        return i;
    }

    private static int FindNextWordBoundary(string text, int cursor)
    {
        var i = Math.Clamp(cursor, 0, text.Length);
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        if (i >= text.Length)
            return text.Length;

        var kind = GetWordKind(text[i]);
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && GetWordKind(text[i]) == kind)
            i++;

        return i;
    }

    private static WordKind GetWordKind(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_' ? WordKind.Word : WordKind.Symbol;

    private static bool IsReverseTab(ConsoleKeyInfo key)
        => key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift);

    private enum WordKind
    {
        Word,
        Symbol,
    }

    private sealed record CompletionSession(
        string Before,
        string After,
        IReadOnlyList<string> Matches,
        int Index)
    {
        public int Cursor => Before.Length + Matches[Index].Length;

        public string ComposeBuffer() => Before + Matches[Index] + After;

        public bool IsActive(string buffer, int cursor)
            => cursor == Cursor
            && string.Equals(buffer, ComposeBuffer(), StringComparison.Ordinal);

        public CompletionSession Next()
            => this with { Index = (Index + 1) % Matches.Count };

        public CompletionSession Previous()
            => this with { Index = (Index + Matches.Count - 1) % Matches.Count };
    }
}

public interface IPwshPromptConsole
{
    bool SupportsInteractiveEditing { get; }
    bool TreatControlCAsInput { get; set; }
    ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken = default);
    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);
    void Write(string text);
    void Flush();
}

internal sealed class SystemPwshPromptConsole : IPwshPromptConsole
{
    public bool SupportsInteractiveEditing
        => OperatingSystem.IsBrowser()
        || (!Console.IsInputRedirected && !Console.IsOutputRedirected);

    public bool TreatControlCAsInput
    {
        get => Console.TreatControlCAsInput;
        set => Console.TreatControlCAsInput = value;
    }

    public async ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsBrowser())
            return await BrowserPromptConsoleBridge.ReadKeyAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return Console.ReadKey(intercept: true);
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        => Console.In.ReadLineAsync(cancellationToken).AsTask();

    public void Write(string text) => Console.Out.Write(text);

    public void Flush() => Console.Out.Flush();
}

internal static class BrowserPromptConsoleBridge
{
    private static readonly MethodInfo? s_readKeyAsync = ResolveReadKeyAsync();

    public static async ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        if (s_readKeyAsync is null)
            throw new InvalidOperationException(
                "Browser prompt editing requires Carbide.Terminal.CarbideConsole.ReadKeyAsync(bool, CancellationToken).");

        var result = s_readKeyAsync.Invoke(null, [true, cancellationToken]);
        return result switch
        {
            Task<ConsoleKeyInfo> task => await task.ConfigureAwait(true),
            ValueTask<ConsoleKeyInfo> valueTask => await valueTask.ConfigureAwait(true),
            _ => throw new InvalidOperationException(
                "Browser prompt editing resolved CarbideConsole.ReadKeyAsync, but it returned an unexpected result shape."),
        };
    }

    private static MethodInfo? ResolveReadKeyAsync()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("Carbide.Terminal.CarbideConsole", throwOnError: false, ignoreCase: false);
            var method = type?.GetMethod(
                "ReadKeyAsync",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(bool), typeof(CancellationToken)],
                modifiers: null);
            if (method is not null)
                return method;
        }

        return null;
    }
}
