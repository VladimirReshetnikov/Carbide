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
    Pipe,               // |

    // Dashed operators — all enumerated for parse-time disambiguation from -Parameter
    OpEq, OpNe, OpLt, OpLe, OpGt, OpGe,
    OpCeq, OpCne, OpClt, OpCle, OpCgt, OpCge,
    OpIeq, OpIne, OpIlt, OpIle, OpIgt, OpIge,
    OpAnd, OpOr, OpXor, OpNot,
    OpBand, OpBor, OpBxor, OpBnot,
    OpIs, OpIsNot, OpAs,

    // Phase 3 — regex/glob/format/collection operators
    OpMatch, OpIMatch, OpCMatch, OpNotMatch, OpINotMatch, OpCNotMatch,
    OpReplace, OpIReplace, OpCReplace,
    OpLike, OpILike, OpCLike, OpNotLike, OpINotLike, OpCNotLike,
    OpContains, OpICContains, OpCContains, OpNotContains, OpINotContains, OpCNotContains,
    OpIn, OpNotIn, OpCIn, OpCNotIn, OpIIn, OpINotIn,
    OpFormat,    // -f
    OpJoin,      // -join
    OpSplit,     // -split

    // Phase 3 — increment/decrement
    PlusPlus,    // ++
    MinusMinus,  // --

    Ampersand,   // & — call operator
}
