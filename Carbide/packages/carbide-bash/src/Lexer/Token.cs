namespace CarbideBash.Lexer;

/// <summary>
/// Bash token. <paramref name="Text"/> is the raw source slice for word tokens (including
/// quotes and expansion markers — parameter expansion runs at eval time, not lex time).
/// </summary>
public sealed record Token(TokenKind Kind, string Text, int Line, int Column);
