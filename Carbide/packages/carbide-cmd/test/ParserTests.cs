using CarbideCmd.Parser;
using Xunit;

namespace CarbideCmd.Tests;

public class ParserTests
{
    [Fact]
    public void ParsesSimpleEcho()
    {
        var script = CmdParser.ParseString("ECHO hi\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var stmt = Assert.IsType<SimpleCommandAst>(line.Chain.Items[0].Statement);
        Assert.Equal("ECHO", stmt.Name);
        Assert.Equal(new[] { "hi" }, stmt.Arguments);
    }

    [Fact]
    public void ParsesLabel()
    {
        var script = CmdParser.ParseString(":start\nECHO body\n");
        var label = Assert.IsType<LabelLineAst>(script.Lines[0]);
        Assert.Equal("start", label.Name);
    }

    [Fact]
    public void ParsesIfEqualsWithElse()
    {
        var script = CmdParser.ParseString("IF a == b (ECHO yes) ELSE (ECHO no)\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var ifs = Assert.IsType<IfStatementAst>(line.Chain.Items[0].Statement);
        Assert.IsType<IfEqualsCondition>(ifs.Condition);
        Assert.NotNull(ifs.Else);
    }

    [Fact]
    public void ParsesIfExist()
    {
        var script = CmdParser.ParseString("IF EXIST /work/file.txt ECHO found\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var ifs = Assert.IsType<IfStatementAst>(line.Chain.Items[0].Statement);
        var cond = Assert.IsType<IfExistCondition>(ifs.Condition);
        Assert.Equal("/work/file.txt", cond.Path);
    }

    [Fact]
    public void ParsesGoto()
    {
        var script = CmdParser.ParseString("GOTO :start\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var g = Assert.IsType<GotoStatementAst>(line.Chain.Items[0].Statement);
        Assert.Equal("start", g.Label);
    }

    [Fact]
    public void ParsesExit()
    {
        var script = CmdParser.ParseString("EXIT /B 7\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var e = Assert.IsType<ExitStatementAst>(line.Chain.Items[0].Statement);
        Assert.True(e.Branch);
        Assert.Equal(7, e.Code);
    }

    [Fact]
    public void ParsesChainOperators()
    {
        var script = CmdParser.ParseString("ECHO a && ECHO b || ECHO c & ECHO d | ECHO e\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var ops = line.Chain.Items.Select(i => i.Op).ToArray();
        Assert.Equal(
            new[] { ChainOperator.None, ChainOperator.And, ChainOperator.Or, ChainOperator.Sequence, ChainOperator.Pipe },
            ops);
    }

    [Fact]
    public void ParsesRedirections()
    {
        var script = CmdParser.ParseString("ECHO hi > /work/out.txt\n");
        var line = Assert.IsType<CommandLineAst>(script.Lines[0]);
        var stmt = Assert.IsType<SimpleCommandAst>(line.Chain.Items[0].Statement);
        var redir = Assert.IsType<StdoutRedirection>(stmt.Redirections[0]);
        Assert.False(redir.Append);
        Assert.Equal("/work/out.txt", redir.Target);
    }
}
