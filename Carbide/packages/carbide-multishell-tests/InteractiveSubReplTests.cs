using Xunit;

namespace CarbideMultishell.Tests;

/// <summary>
/// Exercises <see cref="CarbideShellCore.Dispatch.ShellDispatcher.RunInteractive"/> and the
/// bare-invocation subshell flow. Each test feeds a scripted stdin and asserts on the
/// resulting stdout — verifying that typing <c>cmd</c> / <c>bash</c> / <c>pwsh</c> at any
/// prompt enters an interactive session of the target shell, and that <c>exit</c> pops
/// back to the caller.
/// </summary>
public class InteractiveSubReplTests
{
    [Fact]
    public void StubExecutablesExistInVfs()
    {
        var s = new MultishellSession();
        Assert.True(s.Vfs.IsFile("/usr/bin/cmd.exe"));
        Assert.True(s.Vfs.IsFile("/usr/bin/bash"));
        Assert.True(s.Vfs.IsFile("/usr/bin/pwsh.exe"));
    }

    [Fact]
    public void StubPathResolvesToKernel()
    {
        var s = new MultishellSession();
        var ctx = new CarbideShellCore.Dispatch.ShellExecutionContext
        {
            Vfs = s.Vfs,
            Env = s.Env,
            Apps = s.Apps,
            Dispatcher = s.Dispatcher,
        };
        var res = s.Dispatcher.Resolve("/usr/bin/bash", ctx);
        Assert.Equal(CarbideShellCore.Dispatch.ResolutionKind.NamedShell, res.Kind);
        Assert.Equal("bash", res.Kernel!.Name);
    }

    [Fact]
    public void BareBashFromCmdEntersInteractiveMode()
    {
        var s = new MultishellSession();
        var stdin = new StringReader("echo hi-from-bash\nexit\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Cmd.Submit("bash\n", stdin, stdout, stderr);
        var output = stdout.ToString();
        // Bash's prompt should have been shown at least once.
        Assert.Contains("user@carbide", output, StringComparison.Ordinal);
        // The bash command we typed should have produced its output.
        Assert.Contains("hi-from-bash", output, StringComparison.Ordinal);
    }

    [Fact]
    public void BareCmdFromBashEntersInteractiveMode()
    {
        var s = new MultishellSession();
        var stdin = new StringReader("ECHO from-cmd-subshell\nexit\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Bash.Submit("cmd\n", stdin, stdout, stderr);
        var output = stdout.ToString();
        // cmd's prompt contains "C:\".
        Assert.Contains("C:\\", output, StringComparison.Ordinal);
        Assert.Contains("from-cmd-subshell", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PathInvocationOfStubEntersInteractiveMode()
    {
        var s = new MultishellSession();
        var stdin = new StringReader("ECHO via-stub-path\nexit\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Bash.Submit("/usr/bin/cmd.exe\n", stdin, stdout, stderr);
        Assert.Contains("via-stub-path", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExitWithCodeReturnsThatCodeFromDispatcher()
    {
        var s = new MultishellSession();
        var ctx = new CarbideShellCore.Dispatch.ShellExecutionContext
        {
            Args = Array.Empty<string>(),
            Input = new StringReader("exit 42\n"),
            Output = new StringWriter(),
            Error = new StringWriter(),
            Vfs = s.Vfs,
            Env = s.Env,
            Apps = s.Apps,
            Dispatcher = s.Dispatcher,
        };
        var kernel = ResolveKernel(s.Dispatcher, "bash");
        var code = s.Dispatcher.RunInteractive(kernel, ctx);
        Assert.Equal(42, code);
    }

    [Fact]
    public void EofEndsInteractiveLoop()
    {
        var s = new MultishellSession();
        var ctx = new CarbideShellCore.Dispatch.ShellExecutionContext
        {
            Args = Array.Empty<string>(),
            Input = new StringReader(""),
            Output = new StringWriter(),
            Error = new StringWriter(),
            Vfs = s.Vfs,
            Env = s.Env,
            Apps = s.Apps,
            Dispatcher = s.Dispatcher,
        };
        var kernel = ResolveKernel(s.Dispatcher, "bash");
        var code = s.Dispatcher.RunInteractive(kernel, ctx);
        Assert.Equal(0, code);
    }

    [Fact]
    public void NestedSubshellsUnwindInOrder()
    {
        var s = new MultishellSession();
        // Input: at cmd level type "bash", then at bash level type "cmd", then inside that
        // inner cmd print "deep", then exit twice to pop both subshells.
        var stdin = new StringReader("bash\ncmd\nECHO deep\nexit\nexit\nexit\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CarbideShellCore.Dispatch.ShellExecutionContext
        {
            Args = Array.Empty<string>(),
            Input = stdin,
            Output = stdout,
            Error = stderr,
            Vfs = s.Vfs,
            Env = s.Env,
            Apps = s.Apps,
            Dispatcher = s.Dispatcher,
        };
        var cmd = ResolveKernel(s.Dispatcher, "cmd");
        s.Dispatcher.RunInteractive(cmd, ctx);
        Assert.Contains("deep", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCwdPersistsAcrossSubshell()
    {
        var s = new MultishellSession();
        var stdin = new StringReader("cd /work\nexit\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Cmd.Submit("bash\n", stdin, stdout, stderr);
        // After the bash subshell exits, the cmd side inherits bash's cwd change.
        Assert.Equal("/work", s.Vfs.CurrentLocation);
    }

    private static CarbideShellCore.Dispatch.IShellKernel ResolveKernel(CarbideShellCore.Dispatch.ShellDispatcher d, string name)
    {
        Assert.True(d.TryResolveShellByName(name, out var k));
        return k!;
    }
}
