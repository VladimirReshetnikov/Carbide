namespace CarbideCmd.Parser;

/// <summary>Root of the parsed script — an ordered list of lines.</summary>
public sealed record ScriptAst(IReadOnlyList<LineAst> Lines);

/// <summary>One parsed source line. Blank lines and comments do not appear.</summary>
public abstract record LineAst;

/// <summary>A labeled line: <c>:name</c>. Interpreter uses these for <c>GOTO</c> resolution.</summary>
public sealed record LabelLineAst(string Name) : LineAst;

/// <summary>A non-label line carrying a command chain. <paramref name="EchoSuppressed"/> is true
/// when the line was prefixed with <c>@</c>.</summary>
public sealed record CommandLineAst(CommandChainAst Chain, bool EchoSuppressed) : LineAst;

/// <summary>A horizontal chain of statements joined with <c>&amp;</c>, <c>&amp;&amp;</c>,
/// <c>||</c>, or <c>|</c> operators.</summary>
public sealed record CommandChainAst(IReadOnlyList<ChainedStatementAst> Items);

public sealed record ChainedStatementAst(ChainOperator Op, StatementAst Statement);

public enum ChainOperator
{
    /// <summary>First item in a chain has no preceding operator.</summary>
    None,
    /// <summary><c>&amp;</c> — unconditional next.</summary>
    Sequence,
    /// <summary><c>&amp;&amp;</c> — run only if previous succeeded.</summary>
    And,
    /// <summary><c>||</c> — run only if previous failed.</summary>
    Or,
    /// <summary><c>|</c> — pipe previous's stdout into next's stdin.</summary>
    Pipe,
}

public abstract record StatementAst;

/// <summary>A plain command (<c>ECHO hi</c>, <c>DIR /b</c>, etc.) with optional redirections.</summary>
public sealed record SimpleCommandAst(
    string Name,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<RedirectionAst> Redirections) : StatementAst;

public abstract record RedirectionAst;
public sealed record StdoutRedirection(string Target, bool Append) : RedirectionAst;
public sealed record StdinRedirection(string Target) : RedirectionAst;
public sealed record StderrRedirection(string Target) : RedirectionAst;
public sealed record StderrMergeRedirection : RedirectionAst;

/// <summary><c>IF [NOT] condition body [ELSE otherBody]</c>.</summary>
public sealed record IfStatementAst(
    IfConditionAst Condition,
    bool Negated,
    bool CaseInsensitive,
    StatementAst Body,
    StatementAst? Else) : StatementAst;

public abstract record IfConditionAst;
public sealed record IfEqualsCondition(string Left, string Right) : IfConditionAst;
public sealed record IfExistCondition(string Path) : IfConditionAst;
public sealed record IfDefinedCondition(string VarName) : IfConditionAst;
public sealed record IfErrorLevelCondition(int Threshold) : IfConditionAst;

/// <summary><c>GOTO :label</c>. <c>:EOF</c> is a special label meaning "exit current CALL frame".</summary>
public sealed record GotoStatementAst(string Label) : StatementAst;

/// <summary><c>EXIT [/B] [code]</c>.</summary>
public sealed record ExitStatementAst(int Code, bool Branch) : StatementAst;

/// <summary><c>SETLOCAL [options]</c> — pushes an env-var scope.</summary>
public sealed record SetLocalStatementAst(IReadOnlyList<string> Options) : StatementAst;

/// <summary><c>ENDLOCAL</c> — pops the most recent <c>SETLOCAL</c> scope.</summary>
public sealed record EndLocalStatementAst : StatementAst;

/// <summary>
/// <c>FOR %X IN (set) DO body</c> — iterate a list of items.
/// <paramref name="Items"/> is the raw (unexpanded) token list between the parens; the
/// interpreter expands each item (including globs) before running <paramref name="Body"/>.
/// </summary>
public sealed record ForInStatementAst(
    string Variable,
    IReadOnlyList<string> Items,
    StatementAst Body) : StatementAst;

/// <summary>
/// <c>FOR /L %X IN (start, step, end) DO body</c> — numeric counting loop.
/// </summary>
public sealed record ForLStatementAst(
    string Variable,
    string Start,
    string Step,
    string End,
    StatementAst Body) : StatementAst;

/// <summary>
/// <c>CALL :label arg1 arg2</c> — invoke a labeled section with new positional parameters.
/// <c>GOTO :EOF</c> or <c>EXIT /B</c> inside the called section returns to the caller.
/// </summary>
public sealed record CallLabelStatementAst(string Label, IReadOnlyList<string> Arguments) : StatementAst;

/// <summary>
/// <c>CALL script.cmd arg1 arg2</c> — invoke another script file. The script runs in the
/// same session (shared VFS, env, apps) but with its own positional parameters.
/// </summary>
public sealed record CallScriptStatementAst(string Script, IReadOnlyList<string> Arguments) : StatementAst;

/// <summary>
/// Wraps a parenthesized <see cref="CommandChainAst"/> so it can appear as a
/// <see cref="StatementAst"/> in slots (e.g. FOR body, IF body) that expect a single
/// statement. The interpreter unwraps the chain and executes it inline.
/// </summary>
public sealed record ChainStatementWrapperAst(CommandChainAst Chain) : StatementAst;
