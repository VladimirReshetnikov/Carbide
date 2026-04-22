namespace CarbideBash.Errors;

public abstract class BashException : Exception
{
    public int Line { get; }
    public int Column { get; }
    protected BashException(string message, int line = 0, int column = 0, Exception? inner = null)
        : base(message, inner) { Line = line; Column = column; }
}

public sealed class BashParseException : BashException
{
    public BashParseException(string message, int line = 0, int column = 0)
        : base(message, line, column) { }
}

public sealed class BashRuntimeException : BashException
{
    public BashRuntimeException(string message, int line = 0, int column = 0, Exception? inner = null)
        : base(message, line, column, inner) { }
}

/// <summary>Raised by <c>break [n]</c>.</summary>
internal sealed class BashBreakException : Exception
{
    public int Levels { get; }
    public BashBreakException(int levels) { Levels = Math.Max(1, levels); }
}

/// <summary>Raised by <c>continue [n]</c>.</summary>
internal sealed class BashContinueException : Exception
{
    public int Levels { get; }
    public BashContinueException(int levels) { Levels = Math.Max(1, levels); }
}

/// <summary>Raised by <c>return [n]</c> from a function.</summary>
internal sealed class BashReturnException : Exception
{
    public int Code { get; }
    public BashReturnException(int code) { Code = code; }
}

/// <summary>Raised by <c>exit [n]</c> — escapes to the top-level script runner.</summary>
internal sealed class BashExitException : Exception
{
    public int Code { get; }
    public BashExitException(int code) { Code = code; }
}
