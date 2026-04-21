namespace CarbidePwsh.Runtime;

/// <summary>
/// Phase 1 scope — a single flat variable table. Scope qualifiers (<c>$script:</c>,
/// <c>$global:</c>, <c>$env:</c>) are parsed but folded onto the same table; <c>$env:NAME</c>
/// additionally falls through to <see cref="Environment.GetEnvironmentVariable"/> on read.
/// Phase 2 will replace this with a stack of scopes once functions land.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);

    public object? Get(string? scope, string name)
    {
        if (string.Equals(scope, "env", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(name);
        var key = Qualify(scope, name);
        return _variables.TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string? scope, string name, object? value)
    {
        if (string.Equals(scope, "env", StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable(name, value?.ToString());
            return;
        }
        _variables[Qualify(scope, name)] = value;
    }

    public bool Contains(string? scope, string name) => _variables.ContainsKey(Qualify(scope, name));

    public IReadOnlyDictionary<string, object?> Snapshot() => _variables;

    private static string Qualify(string? scope, string name)
        => scope is null ? name : $"{scope}:{name}";
}
