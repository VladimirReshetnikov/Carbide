namespace CarbideCmd.Lexer;

/// <summary>
/// Kinds of tokens produced by the cmd lexer. cmd.exe is line-oriented: each statement is a
/// command followed by arguments and optional operators, terminated by a newline. The lexer
/// emits a flat stream of tokens; the parser reassembles lines.
/// </summary>
public enum TokenKind
{
    /// <summary>Bare word or quoted string argument. Variable expansion is deferred to eval.</summary>
    Word,

    /// <summary>A label line: <c>:name</c>. The lexer recognizes and emits a single token.</summary>
    Label,

    /// <summary><c>|</c> pipe.</summary>
    Pipe,

    /// <summary><c>&amp;</c> unconditional statement chain.</summary>
    Amp,

    /// <summary><c>&amp;&amp;</c> run-if-previous-succeeded chain.</summary>
    AmpAmp,

    /// <summary><c>||</c> run-if-previous-failed chain.</summary>
    PipePipe,

    /// <summary><c>&gt;</c> redirect stdout (overwrite).</summary>
    RedirOut,

    /// <summary><c>&gt;&gt;</c> redirect stdout (append).</summary>
    RedirAppend,

    /// <summary><c>&lt;</c> redirect stdin.</summary>
    RedirIn,

    /// <summary><c>2&gt;</c> redirect stderr.</summary>
    RedirErr,

    /// <summary><c>2&gt;&amp;1</c> merge stderr into stdout.</summary>
    RedirMerge,

    /// <summary><c>@</c> echo-suppress prefix. Only meaningful at the start of a line.</summary>
    At,

    /// <summary><c>(</c>.</summary>
    LParen,

    /// <summary><c>)</c>.</summary>
    RParen,

    /// <summary>Line separator. cmd semantics are line-oriented.</summary>
    Newline,

    /// <summary>End of input.</summary>
    EndOfFile,
}
