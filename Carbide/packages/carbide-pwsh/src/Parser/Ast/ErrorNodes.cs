using CarbidePwsh.Errors;

namespace CarbidePwsh.Parser.Ast;

public sealed record CatchClauseAst(
    IReadOnlyList<TypeLiteralAst> TypeFilters,
    ScriptAst Body,
    SourceLocation Location)
    : AstNode(Location);

public sealed record TryStatementAst(
    ScriptAst TryBody,
    IReadOnlyList<CatchClauseAst> CatchClauses,
    ScriptAst? FinallyBody,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record ThrowStatementAst(
    ExpressionAst? Value,
    SourceLocation Location)
    : StatementAst(Location);
