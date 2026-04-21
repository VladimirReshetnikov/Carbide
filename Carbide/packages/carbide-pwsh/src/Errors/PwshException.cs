namespace CarbidePwsh.Errors;

public abstract class PwshException : Exception
{
    public SourceLocation Location { get; }

    protected PwshException(string message, SourceLocation location, Exception? inner = null)
        : base(message, inner)
    {
        Location = location;
    }

    public override string ToString()
        => Location == SourceLocation.None ? Message : $"{Message} at {Location}";
}

public sealed class PwshParseException : PwshException
{
    public PwshParseException(string message, SourceLocation location)
        : base(message, location) { }
}

public sealed class PwshRuntimeException : PwshException
{
    public PwshRuntimeException(string message, SourceLocation location = default, Exception? inner = null)
        : base(message, location, inner) { }
}

public sealed class PwshCoercionException : PwshException
{
    public PwshCoercionException(string message, SourceLocation location = default)
        : base(message, location) { }
}

public sealed class PwshTypeNotFoundException : PwshException
{
    public string TypeName { get; }

    public PwshTypeNotFoundException(string typeName, SourceLocation location = default)
        : base($"Unable to find type [{typeName}].", location)
    {
        TypeName = typeName;
    }
}

public sealed class PwshMemberNotFoundException : PwshException
{
    public string MemberName { get; }
    public string TypeName { get; }
    public IReadOnlyList<string> NearestMatches { get; }

    public PwshMemberNotFoundException(
        string typeName,
        string memberName,
        IReadOnlyList<string> nearestMatches,
        SourceLocation location = default)
        : base(BuildMessage(typeName, memberName, nearestMatches), location)
    {
        TypeName = typeName;
        MemberName = memberName;
        NearestMatches = nearestMatches;
    }

    private static string BuildMessage(string typeName, string memberName, IReadOnlyList<string> nearest)
    {
        var head = $"Method/property '{memberName}' not found on [{typeName}].";
        if (nearest.Count == 0) return head;
        return $"{head} Did you mean: {string.Join(", ", nearest)}?";
    }
}

public sealed class PwshMethodBindingException : PwshException
{
    public PwshMethodBindingException(string message, SourceLocation location = default)
        : base(message, location) { }
}
