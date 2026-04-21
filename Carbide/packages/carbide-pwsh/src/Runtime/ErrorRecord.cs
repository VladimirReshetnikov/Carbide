using CarbidePwsh.Errors;

namespace CarbidePwsh.Runtime;

/// <summary>
/// PowerShell-flavored error envelope. Wraps an underlying exception plus some contextual
/// metadata (target object, category, source location). Inside <c>catch</c>, <c>$_</c> is
/// bound to the <c>ErrorRecord</c>.
/// </summary>
public sealed class ErrorRecord
{
    public Exception Exception { get; }
    public object? TargetObject { get; }
    public string FullyQualifiedErrorId { get; }
    public string CategoryInfo { get; }
    public SourceLocation Location { get; }

    public ErrorRecord(Exception exception, object? targetObject = null, string? errorId = null,
        string? category = null, SourceLocation? location = null)
    {
        Exception = exception;
        TargetObject = targetObject;
        FullyQualifiedErrorId = errorId ?? exception.GetType().Name;
        CategoryInfo = category ?? InferCategory(exception);
        Location = location ?? SourceLocation.None;
    }

    public override string ToString() => Exception.Message;

    private static string InferCategory(Exception ex) => ex switch
    {
        PwshParseException => "ParserError",
        PwshRuntimeException => "InvalidOperation",
        PwshTypeNotFoundException => "ObjectNotFound",
        PwshMemberNotFoundException => "InvalidArgument",
        PwshCoercionException => "InvalidType",
        ArgumentException => "InvalidArgument",
        FormatException => "InvalidType",
        _ => "NotSpecified",
    };
}

/// <summary>
/// Terminating-error exception. Emitted by <c>throw</c> and by errors that the runtime decides
/// are terminating (either explicit <c>PwshTerminating</c> classification or
/// <c>$ErrorActionPreference = 'Stop'</c>).
/// </summary>
public sealed class PwshTerminatingException : Exception
{
    public ErrorRecord Error { get; }
    public PwshTerminatingException(ErrorRecord error)
        : base(error.Exception.Message, error.Exception)
    {
        Error = error;
    }
}
