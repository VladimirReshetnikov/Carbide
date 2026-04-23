using CarbidePwsh.Errors;

namespace CarbidePwsh.Lexer;

public readonly record struct Token(
    TokenKind Kind,
    string Text,
    object? Value,
    SourceLocation Location)
{
    public static Token Eof(SourceLocation at)
        => new(TokenKind.EndOfInput, "", null, at);
}

/// <summary>
/// One fragment of a double-quoted string's value. Either a literal run of characters, or an
/// embedded PowerShell expression that must be re-parsed at AST-construction time.
/// </summary>
public abstract record StringPart;

public sealed record LiteralPart(string Text) : StringPart;

public sealed record VariablePart(string? Scope, string Name) : StringPart;

public sealed record ExpressionPart(string Source, SourceLocation Origin) : StringPart;

public readonly record struct RedirectionTokenData(
    int? FromStream,
    bool Append,
    int? MergeToStream);
