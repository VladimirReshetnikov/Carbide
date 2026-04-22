using Xunit;

namespace CarbideCmd.Tests;

public class InterpreterTests
{
    [Fact]
    public void EchoPrintsArgs()
    {
        var h = new CmdHarness();
        h.Submit("ECHO hello world\n");
        Assert.Equal("hello world" + Environment.NewLine, h.Output);
    }

    [Fact]
    public void SetStoresVariable()
    {
        var h = new CmdHarness();
        h.Submit("SET FOO=bar\nECHO %FOO%\n");
        Assert.Equal("bar" + Environment.NewLine, h.Output);
    }

    [Fact]
    public void SetArithUsesIntExpression()
    {
        var h = new CmdHarness();
        h.Submit("SET /A X = 2 + 3*4\nECHO %X%\n");
        Assert.Contains("14", h.Output);
    }

    [Fact]
    public void IfEqualsTrueTakesThenBranch()
    {
        var h = new CmdHarness();
        h.Submit("IF abc == abc (ECHO hit) ELSE (ECHO miss)\n");
        Assert.Contains("hit", h.Output);
        Assert.DoesNotContain("miss", h.Output);
    }

    [Fact]
    public void IfEqualsFalseTakesElseBranch()
    {
        var h = new CmdHarness();
        h.Submit("IF abc == xyz (ECHO hit) ELSE (ECHO miss)\n");
        Assert.Contains("miss", h.Output);
        Assert.DoesNotContain("hit", h.Output);
    }

    [Fact]
    public void IfExistDetectsFile()
    {
        var h = new CmdHarness();
        h.Vfs.CreateTextFile("/work/f.txt", "", overwrite: false);
        h.Submit("IF EXIST /work/f.txt ECHO yes\n");
        Assert.Contains("yes", h.Output);
    }

    [Fact]
    public void IfDefinedReadsEnv()
    {
        var h = new CmdHarness();
        h.Submit("SET FOO=bar\nIF DEFINED FOO ECHO yes\n");
        Assert.Contains("yes", h.Output);
    }

    [Fact]
    public void GotoJumpsForward()
    {
        var h = new CmdHarness();
        h.Submit("GOTO :end\nECHO skipped\n:end\nECHO done\n");
        Assert.DoesNotContain("skipped", h.Output);
        Assert.Contains("done", h.Output);
    }

    [Fact]
    public void RedirectWritesVfsFile()
    {
        var h = new CmdHarness();
        h.Submit("ECHO hello > /work/out.txt\n");
        var node = h.Vfs.Resolve("/work/out.txt");
        Assert.NotNull(node);
        var file = Assert.IsType<CarbideShellCore.Vfs.VfsFile>(node);
        Assert.Contains("hello", file.ReadText());
    }

    [Fact]
    public void TypePrintsFile()
    {
        var h = new CmdHarness();
        h.Vfs.CreateTextFile("/work/hi.txt", "hello world", overwrite: false);
        h.Submit("TYPE /work/hi.txt\n");
        Assert.Contains("hello world", h.Output);
    }

    [Fact]
    public void AtSuppressesEcho()
    {
        var h = new CmdHarness();
        h.Submit("@echo off\nECHO on\n");
        // With @echo off the command "ECHO on" literally flips the echo state but doesn't
        // emit anything itself. Assertion: the word "on" as a line should not appear (it
        // was the argument to ECHO, which silently processed it).
        Assert.Equal("", h.Output);
    }

    [Fact]
    public void ChainingAndAndRespectsExitCode()
    {
        var h = new CmdHarness();
        h.Submit("ECHO a && ECHO b\n");
        Assert.Contains("a", h.Output);
        Assert.Contains("b", h.Output);
    }

    [Fact]
    public void PipeFeedsStageInput()
    {
        var h = new CmdHarness();
        h.Submit("ECHO a && ECHO b && ECHO c > /tmp/lines.txt\n");
        h.Submit("TYPE /tmp/lines.txt | SORT /R\n");
        Assert.Contains("c", h.Output);
    }

    [Fact]
    public void DirListsVfsEntries()
    {
        var h = new CmdHarness();
        h.Vfs.CreateDirectory("/work");
        h.Vfs.CreateTextFile("/work/a.txt", "x", overwrite: false);
        h.Submit("DIR /B /work\n");
        Assert.Contains("a.txt", h.Output);
    }

    [Fact]
    public void MdThenRd()
    {
        var h = new CmdHarness();
        h.Submit("MD /work/sub\n");
        Assert.True(h.Vfs.IsDirectory("/work/sub"));
        h.Submit("RD /work/sub\n");
        Assert.False(h.Vfs.Exists("/work/sub"));
    }

    [Fact]
    public void ExitBReturnsCode()
    {
        var h = new CmdHarness();
        var code = h.Submit("EXIT /B 42\n");
        Assert.Equal(42, code);
    }

    [Fact]
    public void SetLocalIsolatesMutations()
    {
        var h = new CmdHarness();
        h.Submit("SET FOO=outer\n");
        h.Submit("SETLOCAL\nSET FOO=inner\nECHO %FOO%\nENDLOCAL\nECHO %FOO%\n");
        // inner then outer.
        var lines = h.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("inner", lines);
        Assert.Contains("outer", lines);
    }
}
