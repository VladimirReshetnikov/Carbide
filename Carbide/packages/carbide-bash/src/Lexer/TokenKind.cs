namespace CarbideBash.Lexer;

public enum TokenKind
{
    /// <summary>A bare or quoted word. Kept lexically unexpanded; the expansion stage does parameter/command/arithmetic substitution.</summary>
    Word,

    /// <summary><c>|</c> pipe.</summary>
    Pipe,
    /// <summary><c>&amp;&amp;</c>.</summary>
    AndIf,
    /// <summary><c>||</c>.</summary>
    OrIf,
    /// <summary><c>;</c> statement separator.</summary>
    Semicolon,
    /// <summary><c>&amp;</c> (Phase 1: treated as sequence, not background).</summary>
    Ampersand,
    /// <summary>Line terminator (also a statement separator in bash).</summary>
    Newline,

    /// <summary><c>&lt;</c> redirect stdin.</summary>
    RedirIn,
    /// <summary><c>&gt;</c> redirect stdout.</summary>
    RedirOut,
    /// <summary><c>&gt;&gt;</c> append stdout.</summary>
    RedirAppend,
    /// <summary><c>2&gt;</c> redirect stderr.</summary>
    RedirErr,
    /// <summary><c>&amp;&gt;</c> redirect stdout+stderr.</summary>
    RedirAll,
    /// <summary><c>&lt;&lt;</c> heredoc start. The lexer captures the body as a synthesized
    /// <see cref="Word"/> token immediately following the <see cref="Heredoc"/> token.</summary>
    Heredoc,
    /// <summary><c>&lt;&lt;-</c> heredoc that strips leading tabs from each body line.</summary>
    HeredocDash,
    /// <summary><c>&lt;&lt;&lt;</c> here-string.</summary>
    HereString,
    /// <summary>Synthesized token containing a heredoc body. Emitted by the lexer after a
    /// <see cref="Heredoc"/> / <see cref="HeredocDash"/> and its delimiter word.</summary>
    HeredocBody,

    /// <summary><c>(</c>.</summary>
    LParen,
    /// <summary><c>)</c>.</summary>
    RParen,
    /// <summary><c>{</c>.</summary>
    LBrace,
    /// <summary><c>}</c>.</summary>
    RBrace,

    /// <summary>End of input.</summary>
    EndOfFile,
}
