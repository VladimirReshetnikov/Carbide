namespace CarbideCmd.Lexer;

/// <summary>
/// Lexer output. <paramref name="Text"/> is the raw slice from the source for <see cref="TokenKind.Word"/>
/// tokens and the label name (without the leading colon) for <see cref="TokenKind.Label"/> tokens.
/// Operator tokens leave <paramref name="Text"/> as an empty string.
/// </summary>
public sealed record Token(TokenKind Kind, string Text, int Line, int Column);
