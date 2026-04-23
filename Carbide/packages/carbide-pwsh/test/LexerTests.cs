using CarbidePwsh.Lexer;
using Xunit;
using PwshLexer = CarbidePwsh.Lexer.Lexer;

namespace CarbidePwsh.Tests;

public class LexerTests
{
    private static List<Token> Tokens(string src)
    {
        var t = new PwshLexer(src).Tokenize();
        return t.Where(x => x.Kind != TokenKind.EndOfInput).ToList();
    }

    private static TokenKind[] Kinds(string src) => Tokens(src).Select(t => t.Kind).ToArray();

    [Fact]
    public void LexesIntegerLiteral()
    {
        var t = Tokens("42");
        Assert.Single(t);
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal(42, t[0].Value);
    }

    [Fact]
    public void LexesHexLiteral()
    {
        var t = Tokens("0x2A");
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal(42, t[0].Value);
    }

    [Fact]
    public void LexesDoubleLiteral()
    {
        var t = Tokens("3.14");
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal(3.14, (double)t[0].Value!);
    }

    [Fact]
    public void LexesExponentLiteral()
    {
        var t = Tokens("1.5e-3");
        Assert.Equal(0.0015, (double)t[0].Value!);
    }

    [Fact]
    public void LexesLongIntegerSuffix()
    {
        var t = Tokens("1000L");
        Assert.Single(t);
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal("1000L", t[0].Text);
        Assert.Equal(1000L, Assert.IsType<long>(t[0].Value));
    }

    [Fact]
    public void LexesNumericSizeSuffix()
    {
        var t = Tokens("1MB");
        Assert.Single(t);
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal("1MB", t[0].Text);
        Assert.Equal(1024L * 1024L, Assert.IsType<long>(t[0].Value));
    }

    [Fact]
    public void LexesVersionLiteral()
    {
        var t = Tokens("6.3.7600");
        Assert.Single(t);
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal(new Version(6, 3, 7600), Assert.IsType<Version>(t[0].Value));
    }

    [Fact]
    public void LexesSingleQuotedString()
    {
        var t = Tokens("'hello world'");
        Assert.Single(t);
        Assert.Equal(TokenKind.String, t[0].Kind);
        var parts = (IReadOnlyList<StringPart>)t[0].Value!;
        var lp = Assert.IsType<LiteralPart>(parts[0]);
        Assert.Equal("hello world", lp.Text);
    }

    [Fact]
    public void SingleQuotedIsNotInterpolated()
    {
        var t = Tokens("'$name'");
        var parts = (IReadOnlyList<StringPart>)t[0].Value!;
        var lp = Assert.IsType<LiteralPart>(parts[0]);
        Assert.Equal("$name", lp.Text);
    }

    [Fact]
    public void DoubleQuotedContainsVariablePart()
    {
        var t = Tokens("\"hello, $name\"");
        var parts = (IReadOnlyList<StringPart>)t[0].Value!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("hello, ", Assert.IsType<LiteralPart>(parts[0]).Text);
        Assert.Equal("name", Assert.IsType<VariablePart>(parts[1]).Name);
    }

    [Fact]
    public void DoubleQuotedContainsExpressionPart()
    {
        var t = Tokens("\"sum = $(1 + 2)\"");
        var parts = (IReadOnlyList<StringPart>)t[0].Value!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("sum = ", Assert.IsType<LiteralPart>(parts[0]).Text);
        Assert.Equal("1 + 2", Assert.IsType<ExpressionPart>(parts[1]).Source);
    }

    [Fact]
    public void BacktickEscape()
    {
        var t = Tokens("\"line1`nline2\"");
        var parts = (IReadOnlyList<StringPart>)t[0].Value!;
        Assert.Equal("line1\nline2", Assert.IsType<LiteralPart>(parts[0]).Text);
    }

    [Fact]
    public void LexesVariable()
    {
        var t = Tokens("$name");
        Assert.Equal(TokenKind.Variable, t[0].Kind);
        var (scope, name) = ((string? Scope, string Name))t[0].Value!;
        Assert.Null(scope);
        Assert.Equal("name", name);
    }

    [Fact]
    public void LexesScopedVariable()
    {
        var t = Tokens("$env:PATH");
        var (scope, name) = ((string? Scope, string Name))t[0].Value!;
        Assert.Equal("env", scope);
        Assert.Equal("PATH", name);
    }

    [Fact]
    public void LexesBracedVariable()
    {
        var t = Tokens("${my name}");
        var (scope, name) = ((string? Scope, string Name))t[0].Value!;
        Assert.Null(scope);
        Assert.Equal("my name", name);
    }

    [Theory]
    [InlineData("-eq", TokenKind.OpEq)]
    [InlineData("-ne", TokenKind.OpNe)]
    [InlineData("-lt", TokenKind.OpLt)]
    [InlineData("-le", TokenKind.OpLe)]
    [InlineData("-gt", TokenKind.OpGt)]
    [InlineData("-ge", TokenKind.OpGe)]
    [InlineData("-ceq", TokenKind.OpCeq)]
    [InlineData("-and", TokenKind.OpAnd)]
    [InlineData("-or", TokenKind.OpOr)]
    [InlineData("-not", TokenKind.OpNot)]
    public void LexesDashedOperators(string input, TokenKind expected)
    {
        Assert.Equal(expected, Tokens(input).Single().Kind);
    }

    [Fact]
    public void LexesCompoundPunctuation()
    {
        Assert.Equal(
            new[] { TokenKind.ColonColon, TokenKind.DotDot, TokenKind.AtLParen, TokenKind.AtLBrace, TokenKind.DollarLParen },
            Kinds(":: .. @( @{ $("));
    }

    [Fact]
    public void NewLineEmittedBetweenStatements()
    {
        var kinds = Kinds("1\n2");
        Assert.Equal(new[] { TokenKind.Number, TokenKind.NewLine, TokenKind.Number }, kinds);
    }

    [Fact]
    public void SemicolonEmittedBetweenStatements()
    {
        var kinds = Kinds("1;2");
        Assert.Equal(new[] { TokenKind.Number, TokenKind.Semicolon, TokenKind.Number }, kinds);
    }

    [Fact]
    public void LineCommentSkipped()
    {
        var kinds = Kinds("1 # comment\n2");
        Assert.Equal(new[] { TokenKind.Number, TokenKind.NewLine, TokenKind.Number }, kinds);
    }

    [Fact]
    public void BlockCommentSkipped()
    {
        var kinds = Kinds("<# skip #>42");
        Assert.Equal(new[] { TokenKind.Number }, kinds);
    }

    [Fact]
    public void HereStringDoubleQuoted()
    {
        var src = "@\"\nline one\nline two\n\"@";
        var t = Tokens(src);
        var parts = (IReadOnlyList<StringPart>)t[0].Value!;
        Assert.Equal("line one\nline two\n", Assert.IsType<LiteralPart>(parts[0]).Text);
    }

    [Fact]
    public void UnterminatedStringThrows()
    {
        Assert.ThrowsAny<Exception>(() => Tokens("\"not closed"));
    }

    [Fact]
    public void MinusIsMinusWhenNotFollowedByDashedOp()
    {
        var kinds = Kinds("5 - 3");
        Assert.Equal(new[] { TokenKind.Number, TokenKind.Minus, TokenKind.Number }, kinds);
    }
}
