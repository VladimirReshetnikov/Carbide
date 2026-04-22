using CarbideCmd.Parser;
using Xunit;

namespace CarbideCmd.Tests;

/// <summary>
/// Phase-2 cmd feature coverage: FOR loops, CALL, SETLOCAL ENABLEDELAYEDEXPANSION,
/// string-substitution and substring expansions, parameter modifiers.
/// </summary>
public class Cmd2Tests
{
    [Fact]
    public void ForInIteratesList()
    {
        var h = new CmdHarness();
        h.Submit("FOR %X IN (a b c) DO ECHO [%X]\n");
        Assert.Contains("[a]", h.Output, StringComparison.Ordinal);
        Assert.Contains("[b]", h.Output, StringComparison.Ordinal);
        Assert.Contains("[c]", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ForLNumericAscending()
    {
        var h = new CmdHarness();
        h.Submit("FOR /L %I IN (1, 1, 3) DO ECHO %I\n");
        Assert.Contains("1", h.Output, StringComparison.Ordinal);
        Assert.Contains("2", h.Output, StringComparison.Ordinal);
        Assert.Contains("3", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ForLNumericStep()
    {
        var h = new CmdHarness();
        h.Submit("FOR /L %I IN (0, 2, 6) DO ECHO %I\n");
        var lines = h.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "0", "2", "4", "6" }, lines);
    }

    [Fact]
    public void ForInGlobsVfsDirectory()
    {
        var h = new CmdHarness();
        h.Vfs.CreateTextFile("/work/a.txt", "", overwrite: false);
        h.Vfs.CreateTextFile("/work/b.txt", "", overwrite: false);
        h.Vfs.CreateTextFile("/work/c.doc", "", overwrite: false);
        h.Submit("CD /work\nFOR %F IN (*.txt) DO ECHO %F\n");
        var lines = h.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("a.txt", lines);
        Assert.Contains("b.txt", lines);
        Assert.DoesNotContain("c.doc", lines);
    }

    [Fact]
    public void CallLabelRunsThenReturns()
    {
        var h = new CmdHarness();
        h.Vfs.CreateTextFile("/work/script.cmd",
            "@echo off\r\nCALL :sub\r\nECHO after\r\nEXIT /B 0\r\n:sub\r\nECHO in-sub\r\nEXIT /B\r\n",
            overwrite: false);
        h.Submit("CALL /work/script.cmd\n");
        Assert.Contains("in-sub", h.Output, StringComparison.Ordinal);
        Assert.Contains("after", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CallScriptFromCmdSession()
    {
        var h = new CmdHarness();
        h.Vfs.CreateTextFile("/work/hello.cmd", "ECHO hello-from-cmd-script\r\nEXIT /B 0\r\n", overwrite: false);
        h.Submit("CALL /work/hello.cmd\n");
        Assert.Contains("hello-from-cmd-script", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionInExpansion()
    {
        var h = new CmdHarness();
        h.Submit("SET NAME=widget-42\nECHO %NAME:-=_%\n");
        Assert.Contains("widget_42", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstringExpansionPositive()
    {
        var h = new CmdHarness();
        h.Submit("SET S=abcdefgh\nECHO %S:~2,3%\n");
        Assert.Contains("cde", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstringExpansionNegative()
    {
        var h = new CmdHarness();
        h.Submit("SET S=abcdefgh\nECHO %S:~-3%\n");
        Assert.Contains("fgh", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DelayedExpansionReadsUpdatedValue()
    {
        var h = new CmdHarness();
        h.Submit("SETLOCAL ENABLEDELAYEDEXPANSION\nSET X=outer\nFOR %I IN (a b) DO (SET X=%I&ECHO !X!)\nENDLOCAL\n");
        Assert.Contains("a", h.Output, StringComparison.Ordinal);
        Assert.Contains("b", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseForInProducesCorrectAst()
    {
        var script = CmdParser.ParseString("FOR %X IN (a b c) DO ECHO %X\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var f = Assert.IsType<ForInStatementAst>(line.Chain.Items[0].Statement);
        Assert.Equal("X", f.Variable);
        Assert.Equal(3, f.Items.Count);
    }

    [Fact]
    public void ParseForLProducesCorrectAst()
    {
        var script = CmdParser.ParseString("FOR /L %I IN (0, 1, 5) DO ECHO %I\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var f = Assert.IsType<ForLStatementAst>(line.Chain.Items[0].Statement);
        Assert.Equal("I", f.Variable);
        Assert.Equal("0", f.Start);
        Assert.Equal("1", f.Step);
        Assert.Equal("5", f.End);
    }

    [Fact]
    public void ParseCallLabel()
    {
        var script = CmdParser.ParseString("CALL :sub arg1 arg2\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var c = Assert.IsType<CallLabelStatementAst>(line.Chain.Items[0].Statement);
        Assert.Equal("sub", c.Label);
        Assert.Equal(new[] { "arg1", "arg2" }, c.Arguments);
    }

    [Fact]
    public void ParseCallScript()
    {
        var script = CmdParser.ParseString("CALL helper.cmd\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var c = Assert.IsType<CallScriptStatementAst>(line.Chain.Items[0].Statement);
        Assert.Equal("helper.cmd", c.Script);
    }
}
