namespace CarbideBash.Parser;

public sealed record ScriptAst(IReadOnlyList<StatementAst> Statements);

public abstract record StatementAst;

/// <summary>A <c>; </c>-/newline-separated list of pipelines with &amp;&amp;/|| chaining.</summary>
public sealed record ListAst(IReadOnlyList<ListItemAst> Items) : StatementAst;

public sealed record ListItemAst(ListOperator Op, StatementAst Pipeline);

public enum ListOperator { None, Sequence, And, Or }

public sealed record PipelineAst(IReadOnlyList<StatementAst> Stages) : StatementAst;

public sealed record SimpleCommandAst(
    IReadOnlyList<string> Words,
    IReadOnlyList<RedirectionAst> Redirections,
    IReadOnlyList<AssignmentAst> Assignments) : StatementAst;

public sealed record AssignmentAst(string Name, string Value);

public abstract record RedirectionAst;
public sealed record StdoutRedirection(string Target, bool Append) : RedirectionAst;
public sealed record StdinRedirection(string Target) : RedirectionAst;
public sealed record StderrRedirection(string Target) : RedirectionAst;

/// <summary>A heredoc body (<c>&lt;&lt;EOF ... EOF</c>). The body is substituted as stdin;
/// if <paramref name="Expandable"/> the expander performs parameter/command/arithmetic
/// expansion on it first (the delimiter was unquoted), otherwise the body is literal.</summary>
public sealed record HeredocRedirection(string Body, bool Expandable) : RedirectionAst;

/// <summary>A here-string body (<c>&lt;&lt;&lt;string</c>). The string is always expanded.</summary>
public sealed record HereStringRedirection(string Content) : RedirectionAst;

public sealed record IfStatementAst(
    StatementAst Condition,
    StatementAst Then,
    IReadOnlyList<ElifClauseAst> Elifs,
    StatementAst? Else) : StatementAst;

public sealed record ElifClauseAst(StatementAst Condition, StatementAst Then);

public sealed record WhileStatementAst(StatementAst Condition, StatementAst Body, bool Until) : StatementAst;

public sealed record ForStatementAst(string Variable, IReadOnlyList<string> Words, StatementAst Body) : StatementAst;

public sealed record CaseStatementAst(string Word, IReadOnlyList<CaseClauseAst> Clauses) : StatementAst;
public sealed record CaseClauseAst(IReadOnlyList<string> Patterns, StatementAst Body);

public sealed record FunctionDefAst(string Name, StatementAst Body) : StatementAst;

/// <summary>A braced or parenthesized block grouping one or more statements.</summary>
public sealed record BlockAst(IReadOnlyList<StatementAst> Statements, bool Subshell) : StatementAst;

/// <summary><c>[ ... ]</c> / <c>[[ ... ]]</c> — represented as a simple command; the
/// interpreter branches on the name.</summary>
