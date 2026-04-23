using CarbidePwsh.Errors;

namespace CarbidePwsh.Parser.Ast;

public sealed record IfStatementAst(
    IReadOnlyList<(ExpressionAst Condition, ScriptAst Body)> Branches,
    ScriptAst? ElseBody,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record WhileStatementAst(
    string? Label,
    ExpressionAst Condition,
    ScriptAst Body,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record DoWhileStatementAst(
    string? Label,
    ScriptAst Body,
    ExpressionAst Condition,
    bool IsUntil,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record ForStatementAst(
    string? Label,
    StatementAst? Init,
    ExpressionAst? Condition,
    StatementAst? Update,
    ScriptAst Body,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record ForEachStatementAst(
    string? Label,
    string? VariableScope,
    string VariableName,
    ExpressionAst Collection,
    ScriptAst Body,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record SwitchStatementAst(
    string? Label,
    bool IsWildcard,
    ExpressionAst Condition,
    IReadOnlyList<(ExpressionAst Pattern, ScriptAst Body)> Cases,
    ScriptAst? DefaultBody,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record BreakStatementAst(
    string? Label,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record ContinueStatementAst(
    string? Label,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record ReturnStatementAst(
    ExpressionAst? Value,
    SourceLocation Location)
    : StatementAst(Location);
