using CarbideShellCore.Io;
using Xunit;

namespace CarbideShellCore.Tests;

public class ShellArgTokenizerTests
{
    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("a  b", new[] { "a", "b" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void SplitsWhitespace(string input, string[] expected)
    {
        Assert.Equal(expected, ShellArgTokenizer.Tokenize(input));
    }

    [Fact]
    public void DoubleQuotesPreserveWhitespace()
    {
        Assert.Equal(new[] { "hello world" }, ShellArgTokenizer.Tokenize("\"hello world\""));
    }

    [Fact]
    public void SingleQuotesArePurelyLiteral()
    {
        Assert.Equal(new[] { "$FOO \\n" }, ShellArgTokenizer.Tokenize("'$FOO \\n'"));
    }

    [Fact]
    public void DoubleQuoteEscapesCommonBackslashSequences()
    {
        Assert.Equal(new[] { "a\tb" }, ShellArgTokenizer.Tokenize("\"a\\tb\""));
        Assert.Equal(new[] { "a\"b" }, ShellArgTokenizer.Tokenize("\"a\\\"b\""));
    }

    [Fact]
    public void BackslashOutsideQuotesEscapesNextChar()
    {
        Assert.Equal(new[] { "a b" }, ShellArgTokenizer.Tokenize("a\\ b"));
    }

    [Fact]
    public void AdjacentQuotedAndBarePartsJoin()
    {
        Assert.Equal(new[] { "hello world!" }, ShellArgTokenizer.Tokenize("\"hello \"world\\!"));
    }
}
