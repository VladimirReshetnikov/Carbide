using CarbideCmd.Lexer;
using Xunit;

namespace CarbideCmd.Tests;

public class LexerTests
{
    [Fact]
    public void TokenizesSimpleEcho()
    {
        var tokens = CmdLexer.Tokenize("ECHO hello world");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenKind.Word, tokens[0].Kind);
        Assert.Equal("ECHO", tokens[0].Text);
        Assert.Equal("hello", tokens[1].Text);
        Assert.Equal("world", tokens[2].Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[3].Kind);
    }

    [Fact]
    public void RecognizesPipeAndChainOps()
    {
        var tokens = CmdLexer.Tokenize("a | b && c || d & e");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Contains(TokenKind.Pipe, kinds);
        Assert.Contains(TokenKind.AmpAmp, kinds);
        Assert.Contains(TokenKind.PipePipe, kinds);
        Assert.Contains(TokenKind.Amp, kinds);
    }

    [Fact]
    public void Stderr2GtMergeRecognized()
    {
        var tokens = CmdLexer.Tokenize("cmd 2>&1 other 2> file");
        Assert.Contains(tokens, t => t.Kind == TokenKind.RedirMerge);
        Assert.Contains(tokens, t => t.Kind == TokenKind.RedirErr);
    }

    [Fact]
    public void AtSuppressorOnlyAtLineStart()
    {
        var tokens = CmdLexer.Tokenize("@echo off");
        Assert.Equal(TokenKind.At, tokens[0].Kind);
        Assert.Equal("echo", tokens[1].Text);
    }

    [Fact]
    public void RemConsumesLine()
    {
        var tokens = CmdLexer.Tokenize("REM this is a comment\nECHO real");
        var words = tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text).ToArray();
        Assert.Equal(new[] { "ECHO", "real" }, words);
    }

    [Fact]
    public void DoubleColonCommentSkipped()
    {
        var tokens = CmdLexer.Tokenize(":: this is a comment\nECHO still");
        var words = tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text).ToArray();
        Assert.Equal(new[] { "ECHO", "still" }, words);
    }

    [Fact]
    public void LabelTokenizedAtLineStart()
    {
        var tokens = CmdLexer.Tokenize(":start\nECHO body");
        Assert.Equal(TokenKind.Label, tokens[0].Kind);
        Assert.Equal("start", tokens[0].Text);
    }

    [Fact]
    public void QuotedStringPreservesQuotes()
    {
        var tokens = CmdLexer.Tokenize("ECHO \"hello world\"");
        Assert.Equal("\"hello world\"", tokens[1].Text);
    }

    [Fact]
    public void CaretEscapesOperator()
    {
        var tokens = CmdLexer.Tokenize("ECHO ^|pipe");
        Assert.Equal("|pipe", tokens[1].Text);
    }
}
