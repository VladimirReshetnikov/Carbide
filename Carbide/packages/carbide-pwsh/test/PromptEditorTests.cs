using System.Text;
using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class PromptEditorTests
{
    [Fact]
    public async Task EscapeClearsTheCurrentLine()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            Char('a'),
            Char('b'),
            Escape(),
            Char('c'),
            Enter());
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("c", result.Line);
        Assert.Equal(new[] { "c" }, editor.History);
    }

    [Fact]
    public async Task CtrlCInterruptsThePromptAndDoesNotEnterHistory()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            Char('b'),
            Char('a'),
            Char('d'),
            CtrlC());
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Interrupted, result.Kind);
        Assert.Empty(editor.History);
        Assert.Contains("\x1b[31m^C\x1b[0m\r\n", console.Transcript, StringComparison.Ordinal);
        Assert.False(console.TreatControlCAsInput);
    }

    [Fact]
    public async Task ArrowUpAndDownNavigateRecentHistory()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            Char('g'), Char('c'), Enter(),
            Char('l'), Char('s'), Enter(),
            Up(), Up(), Down(), Enter());
        var editor = new PwshPromptEditor(host, console);

        var first = await editor.ReadLineAsync("PS /home/user> ");
        var second = await editor.ReadLineAsync("PS /home/user> ");
        var third = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal("gc", first.Line);
        Assert.Equal("ls", second.Line);
        Assert.Equal("ls", third.Line);
    }

    [Fact]
    public async Task TabCompletesAndCyclesCommandNames()
    {
        var host = new ShellHost();
        host.Submit("function GreetOne { 1 }; function GreetTwo { 2 }");

        var firstConsole = new FakePromptConsole(
            Char('G'), Char('r'), Char('e'),
            Tab(),
            Enter());
        var firstEditor = new PwshPromptEditor(host, firstConsole);
        var first = await firstEditor.ReadLineAsync("PS /home/user> ");
        Assert.Equal("GreetOne", first.Line);

        var secondConsole = new FakePromptConsole(
            Char('G'), Char('r'), Char('e'),
            Tab(), Tab(),
            Enter());
        var secondEditor = new PwshPromptEditor(host, secondConsole);
        var second = await secondEditor.ReadLineAsync("PS /home/user> ");
        Assert.Equal("GreetTwo", second.Line);

        var reverseConsole = new FakePromptConsole(
            Char('G'), Char('r'), Char('e'),
            ShiftTab(),
            Enter());
        var reverseEditor = new PwshPromptEditor(host, reverseConsole);
        var reverse = await reverseEditor.ReadLineAsync("PS /home/user> ");
        Assert.Equal("GreetTwo", reverse.Line);
    }

    [Fact]
    public async Task CtrlAAndCtrlEMoveToStartAndEndOfLine()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            Char('a'), Char('b'), Char('c'),
            CtrlA(),
            Char('Z'),
            CtrlE(),
            Char('Y'),
            Enter());
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("ZabcY", result.Line);
    }

    [Fact]
    public async Task WordEditingDeletesPreviousWord()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            [.. Text("alpha beta"), CtrlLeft(), CtrlBackspace(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("beta", result.Line);
    }

    [Fact]
    public async Task CtrlKDeletesToEndOfLine()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            [.. Text("alpha beta"), CtrlLeft(), CtrlK(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("alpha ", result.Line);
    }

    [Fact]
    public async Task CtrlUDeletesToStartOfLine()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            [.. Text("alpha beta"), CtrlLeft(), CtrlU(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("beta", result.Line);
    }

    [Fact]
    public async Task InsertTogglesOverwriteMode()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            Char('a'), Char('b'), Char('c'),
            CtrlA(),
            Insert(),
            Char('Z'),
            Enter());
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("Zbc", result.Line);
    }

    [Fact]
    public async Task CtrlDDeletesCharacterUnderCursor()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            Char('a'), Char('b'), Char('c'),
            CtrlA(),
            CtrlD(),
            Enter());
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("bc", result.Line);
    }

    [Fact]
    public async Task CtrlDOnEmptyLineEndsInput()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(CtrlD());
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.EndOfInput, result.Kind);
    }

    [Fact]
    public async Task TabCompletesCmdletParameterNames()
    {
        var host = new ShellHost();
        var console = new FakePromptConsole(
            [.. Text("Get-Command -Comm"), Tab(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("Get-Command -CommandType", result.Line);
    }

    [Fact]
    public async Task TabCompletesFunctionParameterNames()
    {
        var host = new ShellHost();
        host.Submit("function Greet { param($Name, [int]$Count) $Name }");
        var console = new FakePromptConsole(
            [.. Text("Greet -Co"), Tab(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("Greet -Count", result.Line);
    }

    [Fact]
    public async Task TabCompletesVariablesAndEnvironmentVariables()
    {
        var host = new ShellHost();
        host.Submit("$customValue = 42");
        host.Env.Set("PATH", "/bin");

        var variableConsole = new FakePromptConsole(
            [.. Text("$cu"), Tab(), Enter()]);
        var variableEditor = new PwshPromptEditor(host, variableConsole);
        var variable = await variableEditor.ReadLineAsync("PS /home/user> ");
        Assert.Equal("$customValue", variable.Line);

        var envConsole = new FakePromptConsole(
            [.. Text("$env:PA"), Tab(), Enter()]);
        var envEditor = new PwshPromptEditor(host, envConsole);
        var env = await envEditor.ReadLineAsync("PS /home/user> ");
        Assert.Equal("$env:PATH", env.Line);
    }

    [Fact]
    public async Task TabCompletesVfsPaths()
    {
        var host = new ShellHost();
        host.Vfs.CreateDirectory("/tmp");
        host.Vfs.CreateTextFile("/tmp/sample.txt", "hello", overwrite: true);
        var console = new FakePromptConsole(
            [.. Text("Get-Content /tmp/sa"), Tab(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("Get-Content /tmp/sample.txt", result.Line);
    }

    [Fact]
    public async Task TabQuotesVfsPathsWithSpaces()
    {
        var host = new ShellHost();
        host.Vfs.CreateDirectory("/Program Files");
        var console = new FakePromptConsole(
            [.. Text("Set-Location /Pro"), Tab(), Enter()]);
        var editor = new PwshPromptEditor(host, console);

        var result = await editor.ReadLineAsync("PS /home/user> ");

        Assert.Equal(PromptReadResultKind.Submitted, result.Kind);
        Assert.Equal("Set-Location '/Program Files/'", result.Line);
    }

    [Fact]
    public void InteractiveCommandNamesIncludeRecognizedBuiltinPlaceholders()
    {
        var host = new ShellHost();

        var names = host.GetInteractiveCommandNames();

        Assert.Contains("Get-Command", names);
        Assert.Contains("Start-Process", names);
        Assert.Contains("gcm", names);
    }

    private sealed class FakePromptConsole(params ConsoleKeyInfo[] keys) : IPwshPromptConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        private readonly StringBuilder _output = new();

        public bool SupportsInteractiveEditing => true;

        public bool TreatControlCAsInput { get; set; }

        public string Transcript => _output.ToString();

        public ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _keys.Count == 0
                ? new ValueTask<ConsoleKeyInfo?>((ConsoleKeyInfo?)null)
                : new ValueTask<ConsoleKeyInfo?>(_keys.Dequeue());
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("FakePromptConsole only supports key-driven reads.");

        public void Write(string text) => _output.Append(text);

        public void Flush()
        {
        }
    }

    private static IEnumerable<ConsoleKeyInfo> Text(string text)
        => text.Select(Char);

    private static ConsoleKeyInfo Char(char ch)
    {
        var upper = char.ToUpperInvariant(ch);
        var key = upper switch
        {
            >= 'A' and <= 'Z' => ConsoleKey.A + (upper - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (upper - '0'),
            _ => ConsoleKey.Oem8,
        };
        return new ConsoleKeyInfo(ch, key, char.IsUpper(ch), false, false);
    }

    private static ConsoleKeyInfo Enter() => new('\r', ConsoleKey.Enter, false, false, false);
    private static ConsoleKeyInfo Escape() => new('\u001b', ConsoleKey.Escape, false, false, false);
    private static ConsoleKeyInfo Up() => new('\0', ConsoleKey.UpArrow, false, false, false);
    private static ConsoleKeyInfo Down() => new('\0', ConsoleKey.DownArrow, false, false, false);
    private static ConsoleKeyInfo Tab() => new('\t', ConsoleKey.Tab, false, false, false);
    private static ConsoleKeyInfo ShiftTab() => new('\t', ConsoleKey.Tab, true, false, false);
    private static ConsoleKeyInfo Insert() => new('\0', ConsoleKey.Insert, false, false, false);
    private static ConsoleKeyInfo CtrlA() => new('\u0001', ConsoleKey.A, false, false, true);
    private static ConsoleKeyInfo CtrlC() => new('\u0003', ConsoleKey.C, false, false, true);
    private static ConsoleKeyInfo CtrlD() => new('\u0004', ConsoleKey.D, false, false, true);
    private static ConsoleKeyInfo CtrlE() => new('\u0005', ConsoleKey.E, false, false, true);
    private static ConsoleKeyInfo CtrlK() => new('\u000b', ConsoleKey.K, false, false, true);
    private static ConsoleKeyInfo CtrlU() => new('\u0015', ConsoleKey.U, false, false, true);
    private static ConsoleKeyInfo CtrlLeft() => new('\0', ConsoleKey.LeftArrow, false, false, true);
    private static ConsoleKeyInfo CtrlBackspace() => new('\b', ConsoleKey.Backspace, false, false, true);
}
