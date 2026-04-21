namespace CarbidePwsh.Runtime;

/// <summary>Thrown by a <c>break</c> statement; caught by the innermost loop body.</summary>
public sealed class PwshBreakException : Exception
{
    public string? Label { get; }
    public PwshBreakException(string? label = null) : base("break") { Label = label; }
}

/// <summary>Thrown by a <c>continue</c> statement; caught by the innermost loop body.</summary>
public sealed class PwshContinueException : Exception
{
    public string? Label { get; }
    public PwshContinueException(string? label = null) : base("continue") { Label = label; }
}

/// <summary>Thrown by a <c>return</c> statement; caught by the innermost function body.</summary>
public sealed class PwshReturnException : Exception
{
    public object? Value { get; }
    public PwshReturnException(object? value) : base("return") { Value = value; }
}
