using CarbidePwsh.Errors;

namespace CarbidePwsh.Parser.Ast;

public sealed record ClassPropertyAst(
    string Name,
    TypeLiteralAst? TypeConstraint,
    ExpressionAst? DefaultValue,
    bool IsStatic,
    SourceLocation Location)
    : AstNode(Location);

public sealed record ClassMethodAst(
    string Name,
    IReadOnlyList<ParameterAst> Parameters,
    TypeLiteralAst? ReturnType,
    ScriptAst Body,
    bool IsStatic,
    bool IsConstructor,
    SourceLocation Location)
    : AstNode(Location);

public sealed record ClassDefinitionAst(
    string Name,
    IReadOnlyList<ClassPropertyAst> Properties,
    IReadOnlyList<ClassMethodAst> Methods,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record EnumMemberAst(
    string Name,
    long? Value,
    SourceLocation Location)
    : AstNode(Location);

public sealed record EnumDefinitionAst(
    string Name,
    IReadOnlyList<EnumMemberAst> Members,
    SourceLocation Location)
    : StatementAst(Location);
