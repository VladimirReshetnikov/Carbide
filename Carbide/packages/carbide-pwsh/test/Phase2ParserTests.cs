using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;
using Xunit;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Tests;

public class Phase2ParserTests
{
    [Fact]
    public void HyphenatedCommandName()
    {
        var script = PwshParser.ParseString("Get-ChildItem");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var cmd = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        Assert.Equal("Get-ChildItem", cmd.Name);
    }

    [Fact]
    public void CommandWithPositionalArgument()
    {
        var script = PwshParser.ParseString("Get-Content foo.json");
        var cmd = (CommandAst)((PipelineAst)script.Statements[0]).Stages[0];
        Assert.Single(cmd.Elements);
        var arg = Assert.IsType<CommandArgumentAst>(cmd.Elements[0]);
        Assert.IsType<StringLiteralAst>(arg.Expression);
    }

    [Fact]
    public void CommandWithNamedParameter()
    {
        var script = PwshParser.ParseString("Set-Content -Path foo -Value 'hi'");
        var cmd = (CommandAst)((PipelineAst)script.Statements[0]).Stages[0];
        Assert.Equal(4, cmd.Elements.Count);
        Assert.IsType<CommandParameterAst>(cmd.Elements[0]);
        Assert.IsType<CommandArgumentAst>(cmd.Elements[1]);
        Assert.Equal("Path", ((CommandParameterAst)cmd.Elements[0]).Name);
    }

    [Fact]
    public void SwitchParameter()
    {
        var script = PwshParser.ParseString("Get-ChildItem -Recurse");
        var cmd = (CommandAst)((PipelineAst)script.Statements[0]).Stages[0];
        Assert.Single(cmd.Elements);
        Assert.IsType<CommandParameterAst>(cmd.Elements[0]);
    }

    [Fact]
    public void PipelineMultipleStages()
    {
        var script = PwshParser.ParseString("Get-ChildItem | Where-Object { $_ -eq 'x' } | Sort-Object");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        Assert.Equal(3, pipeline.Stages.Count);
    }

    [Fact]
    public void PipelineStartingWithExpression()
    {
        var script = PwshParser.ParseString("@(1,2,3) | Where-Object { $_ -gt 1 }");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        Assert.Equal(2, pipeline.Stages.Count);
        Assert.IsType<ArrayExpressionAst>(pipeline.Stages[0]);
        Assert.IsType<CommandAst>(pipeline.Stages[1]);
    }

    [Fact]
    public void ScriptBlockInCommandArg()
    {
        var script = PwshParser.ParseString("ForEach-Object { $_ * 2 }");
        var cmd = (CommandAst)((PipelineAst)script.Statements[0]).Stages[0];
        var arg = Assert.IsType<CommandArgumentAst>(cmd.Elements.Single());
        Assert.IsType<ScriptBlockAst>(arg.Expression);
    }

    [Fact]
    public void BareWordPath()
    {
        var script = PwshParser.ParseString("Set-Location /tmp");
        var cmd = (CommandAst)((PipelineAst)script.Statements[0]).Stages[0];
        var arg = Assert.IsType<CommandArgumentAst>(cmd.Elements.Single());
        var lit = Assert.IsType<StringLiteralAst>(arg.Expression);
        // Bare words produce a literal (single-quoted-equivalent) part.
        Assert.True(lit.IsSingleQuoted);
    }

    [Fact]
    public void BareWordWithDots()
    {
        var script = PwshParser.ParseString("Set-Content foo.json -Value hi");
        var cmd = (CommandAst)((PipelineAst)script.Statements[0]).Stages[0];
        Assert.Equal(3, cmd.Elements.Count);
    }

    [Fact]
    public void MemberAccessAfterDotNotFoldedAsCommand()
    {
        // `$a.foo-bar` should parse as `$a.foo - bar`, not `$a."foo-bar"`.
        var script = PwshParser.ParseString("$a.foo");
        var expr = ((ExpressionStatementAst)script.Statements.Single()).Expression;
        var m = Assert.IsType<MemberAccessAst>(expr);
        Assert.Equal("foo", m.MemberName);
    }

    [Fact]
    public void IncompleteScriptBlockThrowsIncomplete()
    {
        Assert.Throws<PwshIncompleteInputException>(() => PwshParser.ParseString("{ 1 + 2"));
    }

    [Fact]
    public void IncompleteStringThrowsIncomplete()
    {
        Assert.Throws<PwshIncompleteInputException>(() => PwshParser.ParseString("\"hello"));
    }

    [Fact]
    public void IncompleteArrayThrowsIncomplete()
    {
        Assert.Throws<PwshIncompleteInputException>(() => PwshParser.ParseString("@(1, 2,"));
    }

    [Fact]
    public void IncompleteHashtableThrowsIncomplete()
    {
        Assert.Throws<PwshIncompleteInputException>(() => PwshParser.ParseString("@{ a = 1"));
    }
}
