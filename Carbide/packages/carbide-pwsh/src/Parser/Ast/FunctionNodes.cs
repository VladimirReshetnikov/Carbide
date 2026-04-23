using CarbidePwsh.Errors;

namespace CarbidePwsh.Parser.Ast;

public sealed record ParameterAst(
    string Name,
    TypeLiteralAst? TypeConstraint,
    ExpressionAst? DefaultValue,
    SourceLocation Location)
    : AstNode(Location);

public sealed record FunctionDefinitionAst(
    string Name,
    IReadOnlyList<ParameterAst> Parameters,
    ScriptAst? BeginBlock,
    ScriptAst? ProcessBlock,
    ScriptAst? EndBlock,
    ScriptAst? CleanBlock,
    ScriptAst? SimpleBody,
    SourceLocation Location)
    : StatementAst(Location);
