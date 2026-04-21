using CarbidePwsh.Parser.Ast;
using Xunit;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Tests;

public class ParserTests
{
    private static ScriptAst Parse(string s) => PwshParser.ParseString(s);

    [Fact]
    public void ParsesNumberLiteral()
    {
        var script = Parse("42");
        var stmt = Assert.IsType<ExpressionStatementAst>(script.Statements.Single());
        var n = Assert.IsType<NumberLiteralAst>(stmt.Expression);
        Assert.Equal(42, n.Value);
    }

    [Fact]
    public void ParsesArithmeticWithPrecedence()
    {
        var script = Parse("2 + 3 * 4");
        var stmt = Assert.IsType<ExpressionStatementAst>(script.Statements.Single());
        var b = Assert.IsType<BinaryExpressionAst>(stmt.Expression);
        Assert.Equal(BinaryOp.Add, b.Op);
        Assert.IsType<NumberLiteralAst>(b.Left);
        var r = Assert.IsType<BinaryExpressionAst>(b.Right);
        Assert.Equal(BinaryOp.Multiply, r.Op);
    }

    [Fact]
    public void ParensOverridePrecedence()
    {
        var script = Parse("(2 + 3) * 4");
        var b = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.Multiply, b.Op);
        Assert.IsType<ParenExpressionAst>(b.Left);
    }

    [Fact]
    public void ParsesAssignment()
    {
        var script = Parse("$x = 5");
        var a = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.IsType<VariableAst>(a.Target);
        Assert.Equal(AssignmentOp.Assign, a.Op);
        Assert.IsType<NumberLiteralAst>(a.Value);
    }

    [Fact]
    public void ParsesCompoundAssignment()
    {
        var script = Parse("$x += 1");
        var a = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.Equal(AssignmentOp.AddAssign, a.Op);
    }

    [Fact]
    public void ParsesStaticMemberInvocation()
    {
        var script = Parse("[System.Math]::Sqrt(2)");
        var m = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.True(m.IsStatic);
        Assert.True(m.IsInvocation);
        Assert.Equal("Sqrt", m.MemberName);
        Assert.IsType<TypeLiteralAst>(m.Target);
    }

    [Fact]
    public void ParsesStaticMemberPropertyGet()
    {
        var script = Parse("[System.Math]::PI");
        var m = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.True(m.IsStatic);
        Assert.False(m.IsInvocation);
        Assert.Equal("PI", m.MemberName);
    }

    [Fact]
    public void ParsesStaticMemberPropertySet()
    {
        var script = Parse("[System.Console]::BackgroundColor = 'Red'");
        var a = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.IsType<MemberAccessAst>(a.Target);
    }

    [Fact]
    public void ParsesTypeCast()
    {
        var script = Parse("[int]'42'");
        var c = Assert.IsType<CastExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal("int", c.TargetType.TypeName);
    }

    [Fact]
    public void ParsesRange()
    {
        var script = Parse("1..5");
        var r = Assert.IsType<RangeExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.IsType<NumberLiteralAst>(r.Start);
        Assert.IsType<NumberLiteralAst>(r.End);
    }

    [Fact]
    public void ParsesArrayLiteral()
    {
        var script = Parse("@(1, 2, 3)");
        var arr = Assert.IsType<ArrayExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(3, arr.Elements.Count);
    }

    [Fact]
    public void ParsesHashtableLiteral()
    {
        var script = Parse("@{ a = 1; b = 2 }");
        var h = Assert.IsType<HashtableExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(2, h.Entries.Count);
    }

    [Fact]
    public void ParsesComparison()
    {
        var script = Parse("1 -eq 1");
        var b = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.Equal, b.Op);
    }

    [Fact]
    public void ParsesLogicalAnd()
    {
        var script = Parse("$true -and $false");
        var b = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.And, b.Op);
    }

    [Fact]
    public void ParsesUnaryNegation()
    {
        var script = Parse("-5");
        var u = Assert.IsType<UnaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(UnaryOp.Negate, u.Op);
    }

    [Fact]
    public void ParsesLogicalNot()
    {
        var script = Parse("!$true");
        var u = Assert.IsType<UnaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(UnaryOp.Not, u.Op);
    }

    [Fact]
    public void ParsesMultipleStatements()
    {
        var script = Parse("$x = 5; $x + 1");
        Assert.Equal(2, script.Statements.Count);
    }

    [Fact]
    public void ParsesIndexer()
    {
        var script = Parse("$a[0]");
        var ix = Assert.IsType<IndexerAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.IsType<VariableAst>(ix.Target);
    }

    [Fact]
    public void ParsesInstanceMethodCall()
    {
        var script = Parse("'hello'.ToUpper()");
        var m = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.False(m.IsStatic);
        Assert.True(m.IsInvocation);
        Assert.Equal("ToUpper", m.MemberName);
    }

    [Fact]
    public void ParsesStringInterpolation()
    {
        var script = Parse("\"hello $name\"");
        Assert.IsType<StringLiteralAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
    }

    [Fact]
    public void ParsesSubexpressionInString()
    {
        var script = Parse("\"sum = $(1 + 2)\"");
        var s = Assert.IsType<StringLiteralAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(2, s.Parts.Count);
    }
}
