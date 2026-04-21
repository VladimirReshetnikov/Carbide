namespace CarbidePwsh.Lexer;

public enum TokenKind
{
    EndOfInput,

    // Trivia-like
    NewLine,
    Semicolon,          // ;

    // Literals
    Number,             // int, long, double payload in Token.Value
    String,             // payload: IReadOnlyList<StringPart>
    Identifier,
    Variable,           // $name or ${complex}; payload: (string? scope, string name)

    // Punctuation
    LParen,             // (
    RParen,             // )
    LBracket,           // [
    RBracket,           // ]
    LBrace,             // {
    RBrace,             // }
    AtLParen,           // @(
    AtLBrace,           // @{
    DollarLParen,       // $(
    Comma,              // ,
    Dot,                // .
    ColonColon,         // ::
    DotDot,             // ..
    Equal,              // =
    PlusEqual,          // +=
    MinusEqual,         // -=
    StarEqual,          // *=
    SlashEqual,         // /=
    PercentEqual,       // %=

    // Binary operators
    Plus,               // +
    Minus,              // -
    Star,               // *
    Slash,              // /
    Percent,            // %
    Bang,               // !

    // Dashed operators — all enumerated for parse-time disambiguation from -Parameter
    OpEq, OpNe, OpLt, OpLe, OpGt, OpGe,
    OpCeq, OpCne, OpClt, OpCle, OpCgt, OpCge,
    OpIeq, OpIne, OpIlt, OpIle, OpIgt, OpIge,
    OpAnd, OpOr, OpXor, OpNot,
    OpBand, OpBor, OpBxor, OpBnot,
    OpIs, OpIsNot, OpAs,
}
