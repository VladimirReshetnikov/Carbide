using CarbidePwsh.Errors;
using CarbidePwsh.Lexer;

namespace CarbidePwsh.Parser.Ast;

public sealed record ScriptAst(IReadOnlyList<StatementAst> Statements, SourceLocation Location)
    : AstNode(Location);

public sealed record ExpressionStatementAst(ExpressionAst Expression, SourceLocation Location)
    : StatementAst(Location);

/// <summary>
/// A statement that is really a sequence of sub-statements executed in order. Used for
/// top-level <c>param(...)</c> blocks (which expand to a run of default-value assignments)
/// and other places where one <see cref="StatementAst"/> slot needs to hold multiple
/// logical statements.
/// </summary>
public sealed record BlockStatementAst(IReadOnlyList<StatementAst> Statements, SourceLocation Location)
    : StatementAst(Location);

public sealed record AssignmentStatementAst(
    ExpressionAst Target,
    AssignmentOp Op,
    ExpressionAst Value,
    SourceLocation Location)
    : StatementAst(Location);

public sealed record NumberLiteralAst(object Value, SourceLocation Location)
    : ExpressionAst(Location);

public sealed record StringLiteralAst(
    IReadOnlyList<StringPart> Parts,
    bool IsSingleQuoted,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record BooleanLiteralAst(bool Value, SourceLocation Location)
    : ExpressionAst(Location);

public sealed record NullLiteralAst(SourceLocation Location)
    : ExpressionAst(Location);

public sealed record VariableAst(string? Scope, string Name, SourceLocation Location)
    : ExpressionAst(Location);

public sealed record ArrayExpressionAst(IReadOnlyList<ExpressionAst> Elements, SourceLocation Location)
    : ExpressionAst(Location);

public sealed record HashtableExpressionAst(
    IReadOnlyList<(ExpressionAst Key, ExpressionAst Value)> Entries,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record BinaryExpressionAst(
    ExpressionAst Left,
    BinaryOp Op,
    ExpressionAst Right,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record UnaryExpressionAst(
    UnaryOp Op,
    ExpressionAst Operand,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record RangeExpressionAst(
    ExpressionAst Start,
    ExpressionAst End,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record ParenExpressionAst(ExpressionAst Inner, SourceLocation Location)
    : ExpressionAst(Location);

public sealed record SubExpressionAst(ScriptAst Body, SourceLocation Location)
    : ExpressionAst(Location);

public sealed record TypeLiteralAst(
    string TypeName,
    IReadOnlyList<TypeLiteralAst> GenericArguments,
    int ArrayRank,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record CastExpressionAst(
    TypeLiteralAst TargetType,
    ExpressionAst Value,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record MemberAccessAst(
    ExpressionAst Target,
    string MemberName,
    bool IsStatic,
    bool IsInvocation,
    IReadOnlyList<ExpressionAst>? Arguments,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record IndexerAst(
    ExpressionAst Target,
    ExpressionAst Index,
    SourceLocation Location)
    : ExpressionAst(Location);

public sealed record CommaExpressionAst(
    IReadOnlyList<ExpressionAst> Elements,
    SourceLocation Location)
    : ExpressionAst(Location);

// ---- Phase 2: pipelines, commands, script blocks ----

/// <summary>A sequence of pipeline stages separated by <c>|</c>.</summary>
public sealed record PipelineAst(
    IReadOnlyList<AstNode> Stages,
    SourceLocation Location)
    : StatementAst(Location);

/// <summary>Invocation of a cmdlet or named command.</summary>
public sealed record CommandAst(
    string Name,
    IReadOnlyList<CommandElementAst> Elements,
    SourceLocation Location)
    : AstNode(Location);

public abstract record CommandElementAst(SourceLocation Location) : AstNode(Location);

/// <summary>A <c>-Name</c> parameter introducer with an optional value element following it.</summary>
public sealed record CommandParameterAst(
    string Name,
    SourceLocation Location)
    : CommandElementAst(Location);

/// <summary>A positional or value argument.</summary>
public sealed record CommandArgumentAst(
    ExpressionAst Expression,
    SourceLocation Location)
    : CommandElementAst(Location);

/// <summary><c>{ … }</c> — deferred script, closed over the live scope at evaluation time.</summary>
public sealed record ScriptBlockAst(
    ScriptAst Body,
    SourceLocation Location)
    : ExpressionAst(Location);
