using Xunit;

namespace CarbideMultishell.Tests;

/// <summary>
/// Exercises pwsh's new <c>Invoke-Cmd</c> / <c>Invoke-Bash</c> cmdlets. These round-trip
/// through the shared <see cref="CarbideShellCore.Dispatch.ShellDispatcher"/> like any
/// cross-shell call.
/// </summary>
public class PwshCrossShellTests
{
    [Fact]
    public void PwshInvokeBashInline()
    {
        var s = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var origOut = Console.Out;
        var origErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            s.Pwsh.SubmitAndRender("Invoke-Bash -Command 'echo hello-from-bash'", stdout);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
        Assert.Contains("hello-from-bash", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PwshInvokeCmdInline()
    {
        var s = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var origOut = Console.Out;
        var origErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            s.Pwsh.SubmitAndRender("Invoke-Cmd -Command 'ECHO hello-from-cmd'", stdout);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
        Assert.Contains("hello-from-cmd", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedVfsVisibleToPwsh()
    {
        var s = new MultishellSession();
        var writerNoop = new StringWriter();
        s.Bash.Submit("echo hello > /work/fromtouch.txt\n", new StringReader(""), writerNoop, writerNoop);
        Assert.True(s.Vfs.Exists("/work/fromtouch.txt"));
        // pwsh's Get-ChildItem reads the same VFS.
        Assert.True(s.Pwsh.Vfs.Exists("/work/fromtouch.txt"));
    }

    [Fact]
    public void SharedEnvVisibleToPwsh()
    {
        var s = new MultishellSession();
        var writerNoop = new StringWriter();
        s.Bash.Submit("export FOO=from-bash\n", new StringReader(""), writerNoop, writerNoop);
        Assert.Equal("from-bash", s.Env.Get("FOO"));
        Assert.Equal("from-bash", s.Pwsh.Env.Get("FOO"));
    }

    [Fact]
    public void DispatcherResolvesAllAliasesInMultishellSession()
    {
        var s = new MultishellSession();
        Assert.True(s.Dispatcher.TryResolveShellByName("powershell", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("pwsh", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("bash", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("sh", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("cmd", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("cmd.exe", out _));
    }
}
