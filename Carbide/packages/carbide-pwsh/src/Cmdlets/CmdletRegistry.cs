namespace CarbidePwsh.Cmdlets;

public sealed class CmdletRegistry
{
    private readonly Dictionary<string, Func<Cmdlet>> _byName = new(StringComparer.OrdinalIgnoreCase);
    // alias -> canonical cmdlet name. Used by the `Alias:` provider to emit CommandType /
    // Name / Target triples, and by Remove-Item to unhook an alias without touching its
    // underlying cmdlet.
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public void Register(Func<Cmdlet> factory)
    {
        var instance = factory();
        _byName[instance.Name] = factory;
        foreach (var alias in instance.Aliases)
        {
            _byName[alias] = factory;
            _aliases[alias] = instance.Name;
        }
    }

    public bool TryResolve(string name, out Cmdlet? cmdlet)
    {
        if (_byName.TryGetValue(name, out var factory))
        {
            cmdlet = factory();
            return true;
        }
        cmdlet = null;
        return false;
    }

    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>Enumerate alias → canonical-name pairs. Order is unspecified.</summary>
    public IEnumerable<(string Alias, string Target)> Aliases
        => _aliases.Select(kv => (kv.Key, kv.Value));

    /// <summary>Try to resolve an alias to the canonical cmdlet name it points at.</summary>
    public bool TryResolveAliasTarget(string alias, out string target)
    {
        if (_aliases.TryGetValue(alias, out var t)) { target = t; return true; }
        target = "";
        return false;
    }

    /// <summary>Unregister an alias (leaves the target cmdlet registered under its
    /// canonical name). Returns <see langword="true"/> when an alias was removed.</summary>
    public bool RemoveAlias(string alias)
    {
        _byName.Remove(alias);
        return _aliases.Remove(alias);
    }
}
