using Xunit;

namespace CarbideMultishell.Tests;

/// <summary>
/// Phase 3 cross-shell integration. Exercises the two routing mechanisms:
/// explicit launcher (<c>cmd /c "..."</c>, <c>bash -c "..."</c>) and file-extension routing
/// (<c>./script.sh</c> from cmd or pwsh, <c>./script.cmd</c> from bash).
/// </summary>
public class CrossShellTests
{
    [Fact]
    public void BashInvokesCmdWithDashC()
    {
        var s = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Bash.Submit("cmd /c \"echo from-cmd\"\n", new StringReader(""), stdout, stderr);
        Assert.Contains("from-cmd", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CmdInvokesBashWithDashC()
    {
        var s = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Cmd.Submit("bash -c \"echo from-bash\"\n", new StringReader(""), stdout, stderr);
        Assert.Contains("from-bash", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BashInvokesCmdScriptViaExtensionRouting()
    {
        var s = new MultishellSession();
        s.Vfs.CreateTextFile("/work/hello.cmd", "ECHO from-cmd-script\n", overwrite: false);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Bash.Submit("/work/hello.cmd\n", new StringReader(""), stdout, stderr);
        Assert.Contains("from-cmd-script", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CmdInvokesBashScriptViaExtensionRouting()
    {
        var s = new MultishellSession();
        s.Vfs.CreateTextFile("/work/hello.sh", "echo from-bash-script\n", overwrite: false);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Cmd.Submit("/work/hello.sh\n", new StringReader(""), stdout, stderr);
        Assert.Contains("from-bash-script", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnvVarsAreSharedAcrossShells()
    {
        var s = new MultishellSession();
        var noop = new StringWriter();
        s.Bash.Submit("FOO=from-bash\nexport FOO\n", new StringReader(""), noop, noop);
        var captured = new StringWriter();
        s.Cmd.Submit("ECHO %FOO%\n", new StringReader(""), captured, noop);
        Assert.Contains("from-bash", captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PwdIsSharedAcrossShells()
    {
        var s = new MultishellSession();
        var noop = new StringWriter();
        s.Bash.Submit("cd /work\n", new StringReader(""), noop, noop);
        var captured = new StringWriter();
        // cmd's CD without args prints current VFS location.
        s.Cmd.Submit("CD\n", new StringReader(""), captured, noop);
        Assert.Contains("/work", captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExitCodeFromBashSurfacesInDispatcher()
    {
        var s = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Cmd.Submit("bash -c \"exit 7\"\n", new StringReader(""), stdout, stderr);
        // The shared dispatcher slot carries the exit code of the last cross-shell call.
        // Follow-up commands reset it as they record their own exit codes, which matches
        // real OS-process semantics (`$LASTEXITCODE` / `%ERRORLEVEL%` / `$?` always reflect
        // the previous command). Tests therefore inspect the slot immediately after the
        // cross-shell call.
        Assert.Equal(7, s.Dispatcher.LastExitCode);
    }

    [Fact]
    public void DispatcherResolvesAllThreeShellsByName()
    {
        var s = new MultishellSession();
        Assert.True(s.Dispatcher.TryResolveShellByName("bash", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("sh", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("cmd", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("cmd.exe", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("pwsh", out _));
        Assert.True(s.Dispatcher.TryResolveShellByName("powershell", out _));
    }

    [Fact]
    public void ExtensionsResolveToCorrectShell()
    {
        var s = new MultishellSession();
        Assert.True(s.Dispatcher.TryResolveShellByExtension(".sh", out var bash));
        Assert.Equal("bash", bash!.Name);
        Assert.True(s.Dispatcher.TryResolveShellByExtension(".cmd", out var cmd));
        Assert.Equal("cmd", cmd!.Name);
        Assert.True(s.Dispatcher.TryResolveShellByExtension(".BAT", out var bat));
        Assert.Equal("cmd", bat!.Name);
        Assert.True(s.Dispatcher.TryResolveShellByExtension(".ps1", out var pwsh));
        Assert.Equal("pwsh", pwsh!.Name);
    }

    [Fact]
    public void CmdToBashToCmdChain()
    {
        var s = new MultishellSession();
        s.Vfs.CreateTextFile("/work/leaf.cmd", "ECHO leaf-ran\n", overwrite: false);
        s.Vfs.CreateTextFile("/work/middle.sh", "/work/leaf.cmd\n", overwrite: false);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        s.Cmd.Submit("/work/middle.sh\n", new StringReader(""), stdout, stderr);
        Assert.Contains("leaf-ran", stdout.ToString(), StringComparison.Ordinal);
    }
}
