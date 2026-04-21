using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets;

/// <summary>
/// A cmdlet's already-bound parameters. Populated by the interpreter from the command AST's
/// positional arguments and <c>-Name value</c> pairs. Each cmdlet pulls typed values out with
/// <see cref="GetOrDefault{T}"/> / <see cref="HasSwitch"/> rather than declaring a reflected
/// parameter schema — matches the Phase 2 plan's "manual binding" design decision.
/// </summary>
public sealed class ParameterBinding
{
    public IReadOnlyList<object?> Positional { get; }
    public IReadOnlyDictionary<string, object?> Named { get; }

    public ParameterBinding(IReadOnlyList<object?> positional, IReadOnlyDictionary<string, object?> named)
    {
        Positional = positional;
        Named = new Dictionary<string, object?>(named, StringComparer.OrdinalIgnoreCase);
    }

    public bool HasNamed(string name) => Named.ContainsKey(name);

    public bool HasSwitch(string name)
    {
        if (!Named.TryGetValue(name, out var v)) return false;
        return v is bool b ? b : Coercion.CoerceToBool(v);
    }

    public object? GetNamedRaw(string name)
    {
        return Named.TryGetValue(name, out var v) ? v : null;
    }

    public T? GetOrDefault<T>(string name, T? fallback = default)
    {
        if (!Named.TryGetValue(name, out var v) || v is null) return fallback;
        try { return Coercion.To<T>(v); }
        catch { return fallback; }
    }

    public T? GetPositionalOrDefault<T>(int index, T? fallback = default)
    {
        if (index < 0 || index >= Positional.Count) return fallback;
        var v = Positional[index];
        if (v is null) return fallback;
        try { return Coercion.To<T>(v); }
        catch { return fallback; }
    }

    public bool TryGetPositional(int index, out object? value)
    {
        if (index < 0 || index >= Positional.Count) { value = null; return false; }
        value = Positional[index];
        return true;
    }

    /// <summary>
    /// Get the first bound value by looking up the named parameter first (for `-Path foo`),
    /// then falling back to the positional index. Covers the common cmdlet shape where the
    /// primary argument has a canonical name but is also positional.
    /// </summary>
    public T? GetValue<T>(string name, int positionalIndex, T? fallback = default)
    {
        if (Named.TryGetValue(name, out var v) && v is not null)
        {
            try { return Coercion.To<T>(v); }
            catch { return fallback; }
        }
        return GetPositionalOrDefault(positionalIndex, fallback);
    }
}
