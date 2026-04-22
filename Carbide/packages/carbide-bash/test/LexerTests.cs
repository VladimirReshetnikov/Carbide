using CarbideBash.Lexer;
using Xunit;

namespace CarbideBash.Tests;

public class LexerTests
{
    [Fact]
    public void SimpleCommand()
    {
        var tokens = BashLexer.Tokenize("echo hello");
        Assert.Equal(TokenKind.Word, tokens[0].Kind);
        Assert.Equal("echo", tokens[0].Text);
        Assert.Equal("hello", tokens[1].Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void PipeAndChainOps()
    {
        var tokens = BashLexer.Tokenize("ls | grep x && echo done");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Contains(TokenKind.Pipe, kinds);
        Assert.Contains(TokenKind.AndIf, kinds);
    }

    [Fact]
    public void PreservesSingleQuotes()
    {
        var tokens = BashLexer.Tokenize("echo '$x'");
        Assert.Equal("'$x'", tokens[1].Text);
    }

    [Fact]
    public void PreservesDoubleQuotes()
    {
        var tokens = BashLexer.Tokenize("echo \"$x\"");
        Assert.Equal("\"$x\"", tokens[1].Text);
    }

    [Fact]
    public void HashIsComment()
    {
        var tokens = BashLexer.Tokenize("echo # a comment\nls");
        var words = tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text).ToArray();
        Assert.Equal(new[] { "echo", "ls" }, words);
    }

    [Fact]
    public void CommandSubstitutionPreserved()
    {
        var tokens = BashLexer.Tokenize("echo $(ls /work)");
        Assert.Equal("$(ls /work)", tokens[1].Text);
    }

    [Fact]
    public void HeredocAndHereStringRecognized()
    {
        var tokens = BashLexer.Tokenize("cat <<EOF\nhello\nEOF\n");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Heredoc);
        var tokens2 = BashLexer.Tokenize("cat <<<hello");
        Assert.Contains(tokens2, t => t.Kind == TokenKind.HereString);
    }
}
