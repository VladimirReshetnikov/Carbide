using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;
using Xunit;

namespace CarbideShellCore.Tests;

/// <summary>
/// Unit coverage for <see cref="ShellDispatcher.RunInteractive"/> with a fake kernel,
/// so the dispatcher's loop shape is exercised without pulling in a full dialect.
/// </summary>
public class RunInteractiveTests
{
    private sealed class RecordingKernel : IShellKernel
    {
        public string Name => "fake";
        public IReadOnlyCollection<string> Aliases { get; } = Array.Empty<string>();
        public IReadOnlyCollection<string> FileExtensions { get; } = Array.Empty<string>();

        public List<string> Submissions { get; } = new();
        public Func<string, int> Returns { get; set; } = _ => 0;
        public Func<string, bool> Complete { get; set; } = _ => true;

        public int Execute(string source, ShellExecutionContext ctx) { Submissions.Add(source); return Returns(source); }
        public int ExecuteFile(string absolutePath, ShellExecutionContext ctx) => 0;
        public bool IsCompleteInput(string source) => Complete(source);
        public string BuildPrompt(ShellExecutionContext ctx) => "$ ";
        public string BuildContinuationPrompt(ShellExecutionContext ctx) => "> ";
    }

    private static ShellExecutionContext Ctx(string input, out StringWriter stdout, out StringWriter stderr, ShellDispatcher d)
    {
        stdout = new StringWriter();
        stderr = new StringWriter();
        return new ShellExecutionContext
        {
            Args = Array.Empty<string>(),
            Input = new StringReader(input),
            Output = stdout,
            Error = stderr,
            Vfs = new VirtualFileSystem(),
            Env = new EnvVarStore(),
            Apps = new AppRegistry(),
            Dispatcher = d,
        };
    }

    [Fact]
    public void SubmitsOneLine()
    {
        var d = new ShellDispatcher();
        var k = new RecordingKernel();
        var ctx = Ctx("echo hi\nexit\n", out _, out _, d);
        var code = d.RunInteractive(k, ctx);
        Assert.Single(k.Submissions);
        Assert.Equal("echo hi", k.Submissions[0]);
        Assert.Equal(0, code);
    }

    [Fact]
    public void ExitWithCodeReturns()
    {
        var d = new ShellDispatcher();
        var k = new RecordingKernel();
        var ctx = Ctx("exit 7\n", out _, out _, d);
        var code = d.RunInteractive(k, ctx);
        Assert.Equal(7, code);
    }

    [Fact]
    public void AccumulatesMultiLineUntilComplete()
    {
        var d = new ShellDispatcher();
        var k = new RecordingKernel
        {
            Complete = s => s.EndsWith("done", StringComparison.Ordinal),
        };
        var ctx = Ctx("line1\nline2\ndone\nexit\n", out _, out _, d);
        d.RunInteractive(k, ctx);
        Assert.Single(k.Submissions);
        Assert.Equal("line1\nline2\ndone", k.Submissions[0]);
    }

    [Fact]
    public void ContinuationPromptShownForIncompleteInput()
    {
        var d = new ShellDispatcher();
        var k = new RecordingKernel
        {
            Complete = s => s.EndsWith("done", StringComparison.Ordinal),
        };
        var ctx = Ctx("partial\ndone\nexit\n", out var stdout, out _, d);
        d.RunInteractive(k, ctx);
        var lines = stdout.ToString();
        // Primary prompt for the first line, continuation prompt for the second, primary again for exit.
        Assert.Contains("$ ", lines, StringComparison.Ordinal);
        Assert.Contains("> ", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void EofReturnsLastExitCode()
    {
        var d = new ShellDispatcher();
        var k = new RecordingKernel { Returns = _ => 13 };
        var ctx = Ctx("x\n", out _, out _, d);  // no exit line, just EOF after one submission
        var code = d.RunInteractive(k, ctx);
        Assert.Equal(13, code);
    }

    [Fact]
    public void QuitIsRecognized()
    {
        var d = new ShellDispatcher();
        var k = new RecordingKernel();
        var ctx = Ctx("quit\n", out _, out _, d);
        var code = d.RunInteractive(k, ctx);
        Assert.Equal(0, code);
        Assert.Empty(k.Submissions);
    }
}
