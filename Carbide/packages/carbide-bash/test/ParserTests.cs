using CarbideBash.Parser;
using Xunit;

namespace CarbideBash.Tests;

public class ParserTests
{
    [Fact]
    public void SimpleCommand()
    {
        var s = BashParser.ParseString("echo hi");
        var sc = Assert.IsType<SimpleCommandAst>(s.Statements[0]);
        Assert.Equal(new[] { "echo", "hi" }, sc.Words);
    }

    [Fact]
    public void Assignment()
    {
        var s = BashParser.ParseString("FOO=bar\n");
        var sc = Assert.IsType<SimpleCommandAst>(s.Statements[0]);
        Assert.Single(sc.Assignments);
        Assert.Equal("FOO", sc.Assignments[0].Name);
        Assert.Equal("bar", sc.Assignments[0].Value);
    }

    [Fact]
    public void IfThenElseFi()
    {
        var s = BashParser.ParseString("if true; then echo yes; else echo no; fi\n");
        Assert.IsType<IfStatementAst>(s.Statements[0]);
    }

    [Fact]
    public void ForLoop()
    {
        var s = BashParser.ParseString("for x in a b c; do echo $x; done\n");
        var f = Assert.IsType<ForStatementAst>(s.Statements[0]);
        Assert.Equal("x", f.Variable);
        Assert.Equal(3, f.Words.Count);
    }

    [Fact]
    public void WhileLoop()
    {
        var s = BashParser.ParseString("while false; do echo; done\n");
        Assert.IsType<WhileStatementAst>(s.Statements[0]);
    }

    [Fact]
    public void FunctionPosixForm()
    {
        var s = BashParser.ParseString("greet() { echo hi; }\n");
        var fd = Assert.IsType<FunctionDefAst>(s.Statements[0]);
        Assert.Equal("greet", fd.Name);
    }

    [Fact]
    public void PipelineParses()
    {
        var s = BashParser.ParseString("ls | grep x\n");
        Assert.IsType<PipelineAst>(s.Statements[0]);
    }
}
