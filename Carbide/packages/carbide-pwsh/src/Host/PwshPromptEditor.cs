using CarbidePwsh.Cmdlets.Discovery;
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
    private readonly List<string> _history = [];

    public PwshPromptEditor(ShellHost host, IPwshPromptConsole? console = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _console = console ?? new SystemPwshPromptConsole();
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
                        completion = null;
                        if (cursor == 0) continue;
                        buffer = buffer.Remove(cursor - 1, 1);
                        cursor--;
                        historyIndex = null;
                        historyDraft = buffer;
                        RedrawLine(prompt, buffer, cursor);
                        continue;

                    case ConsoleKey.Delete:
                        completion = null;
                        if (cursor >= buffer.Length) continue;
                        buffer = buffer.Remove(cursor, 1);
                        historyIndex = null;
                        historyDraft = buffer;
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

                buffer = buffer.Insert(cursor, ch.ToString());
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
        if (!TryGetCommandCompletionQuery(buffer, cursor, out var query))
            return false;

        var matches = _host.GetInteractiveCommandNames()
            .Where(name => name.StartsWith(query.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length == 0)
            return false;

        completion = new CompletionSession(
            query.Before,
            query.After,
            matches,
            Index: reverse ? matches.Length - 1 : 0);
        buffer = completion.ComposeBuffer();
        cursor = completion.Cursor;
        return true;
    }

    private static bool TryGetCommandCompletionQuery(
        string buffer,
        int cursor,
        out CommandCompletionQuery query)
    {
        query = default;
        if (cursor < 0 || cursor > buffer.Length)
            return false;

        int tokenStart = cursor;
        while (tokenStart > 0 && !IsCompletionDelimiter(buffer[tokenStart - 1]))
            tokenStart--;

        int tokenEnd = cursor;
        while (tokenEnd < buffer.Length && !IsCompletionDelimiter(buffer[tokenEnd]))
            tokenEnd++;

        if (tokenStart == tokenEnd || cursor != tokenEnd)
            return false;

        int segmentStart = 0;
        for (int i = tokenStart - 1; i >= 0; i--)
        {
            if (buffer[i] is '|' or ';' or '\n' or '\r')
            {
                segmentStart = i + 1;
                break;
            }
        }

        while (segmentStart < tokenStart && char.IsWhiteSpace(buffer[segmentStart]))
            segmentStart++;

        if (segmentStart != tokenStart)
            return false;

        var prefix = buffer[tokenStart..cursor];
        if (prefix.Length == 0
            || prefix[0] is '-' or '$' or '\'' or '"' or '.')
            return false;

        query = new CommandCompletionQuery(
            buffer[..tokenStart],
            prefix,
            buffer[tokenEnd..]);
        return true;
    }

    private static bool IsCompletionDelimiter(char ch)
        => char.IsWhiteSpace(ch) || ch is '|' or ';' or '(' or ')' or '{' or '}' or '[' or ']' or ',';

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

    private void RememberHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
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

    private static bool IsReverseTab(ConsoleKeyInfo key)
        => key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift);

    private readonly record struct CommandCompletionQuery(string Before, string Prefix, string After);

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
