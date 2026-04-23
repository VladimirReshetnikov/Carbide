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
    private static ConsoleKeyInfo CtrlA() => new('\u0001', ConsoleKey.A, false, false, true);
    private static ConsoleKeyInfo CtrlC() => new('\u0003', ConsoleKey.C, false, false, true);
    private static ConsoleKeyInfo CtrlE() => new('\u0005', ConsoleKey.E, false, false, true);
}
