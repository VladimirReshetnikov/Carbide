namespace CarbideCmd.Errors;

/// <summary>Base type for errors raised by the cmd lexer, parser, and interpreter.</summary>
public abstract class CmdException : Exception
{
    public int Line { get; }
    public int Column { get; }

    protected CmdException(string message, int line = 0, int column = 0, Exception? inner = null)
        : base(message, inner)
    {
        Line = line;
        Column = column;
    }
}

public sealed class CmdParseException : CmdException
{
    public CmdParseException(string message, int line = 0, int column = 0)
        : base(message, line, column) { }
}

public sealed class CmdRuntimeException : CmdException
{
    public CmdRuntimeException(string message, int line = 0, int column = 0, Exception? inner = null)
        : base(message, line, column, inner) { }
}

/// <summary>
/// Raised when the top-level script runner needs to resume execution at a labeled line.
/// Caught by the script runner; never escapes to user code.
/// </summary>
internal sealed class CmdGotoException : Exception
{
    public string Label { get; }
    public CmdGotoException(string label) : base($"goto {label}") { Label = label; }
}

/// <summary>
/// Raised by <c>EXIT /B</c> / <c>GOTO :EOF</c> to exit the current <c>CALL</c> frame or top-level
/// script. The outer exception holds the exit code the caller should observe.
/// </summary>
internal sealed class CmdExitException : Exception
{
    public int Code { get; }
    public bool IsBranch { get; }
    public CmdExitException(int code, bool isBranch) : base($"exit {code}") { Code = code; IsBranch = isBranch; }
}
