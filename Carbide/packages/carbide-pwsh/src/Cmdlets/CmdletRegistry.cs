using CarbidePwsh.Cmdlets.Discovery;

namespace CarbidePwsh.Cmdlets;

public sealed class CmdletRegistry
{
    private readonly Dictionary<string, Func<Cmdlet>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuiltinAliasDefinition> _implementedAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuiltinAliasDefinition> _sessionAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedAliases = new(StringComparer.OrdinalIgnoreCase);

    public void Register(Func<Cmdlet> factory)
    {
        var instance = factory();
        _byName[instance.Name] = factory;
        foreach (var alias in instance.Aliases)
        {
            _implementedAliases[alias] = new BuiltinAliasDefinition(alias, instance.Name, PwshAliasOptions.None);
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
    public IReadOnlyCollection<string> CanonicalCmdletNames => _byName.Keys;

    /// <summary>Enumerate alias → canonical-name pairs. Order is unspecified.</summary>
    public IEnumerable<(string Alias, string Target)> Aliases
        => AliasDefinitions.Select(static alias => (alias.Name, alias.Definition));

    public IEnumerable<BuiltinAliasDefinition> AliasDefinitions => EnumerateAliases();

    public void SetAlias(string alias, string target, PwshAliasOptions options = PwshAliasOptions.None, string source = "")
    {
        _removedAliases.Remove(alias);
        _sessionAliases[alias] = new BuiltinAliasDefinition(alias, target, options, source);
    }

    public string ResolveAliasChain(string name)
    {
        var current = name;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (TryGetAliasDefinition(current, out var alias) && seen.Add(current))
            current = alias.Definition;
        return current;
    }

    /// <summary>Try to resolve an alias to the canonical cmdlet name it points at.</summary>
    public bool TryResolveAliasTarget(string alias, out string target)
    {
        if (TryGetAliasDefinition(alias, out var definition))
        {
            target = definition.Definition;
            return true;
        }

        target = "";
        return false;
    }

    public bool TryGetAliasDefinition(string alias, out BuiltinAliasDefinition definition)
    {
        if (_removedAliases.Contains(alias))
        {
            definition = null!;
            return false;
        }

        if (_sessionAliases.TryGetValue(alias, out definition!))
            return true;

        if (_implementedAliases.TryGetValue(alias, out definition!))
            return true;

        return BuiltinCommandCatalog.TryGetAlias(alias, out definition!);
    }

    /// <summary>Unregister an alias (leaves the target cmdlet registered under its
    /// canonical name). Returns <see langword="true"/> when an alias was removed.</summary>
    public bool RemoveAlias(string alias)
    {
        if (_sessionAliases.Remove(alias))
            return true;

        if (_implementedAliases.ContainsKey(alias) || BuiltinCommandCatalog.TryGetAlias(alias, out _))
        {
            _removedAliases.Add(alias);
            return true;
        }

        return false;
    }

    private IEnumerable<BuiltinAliasDefinition> EnumerateAliases()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in _sessionAliases.Values)
        {
            if (_removedAliases.Contains(alias.Name)) continue;
            if (yielded.Add(alias.Name)) yield return alias;
        }

        foreach (var alias in _implementedAliases.Values)
        {
            if (_removedAliases.Contains(alias.Name)) continue;
            if (yielded.Add(alias.Name)) yield return alias;
        }

        foreach (var alias in BuiltinCommandCatalog.Aliases)
        {
            if (_removedAliases.Contains(alias.Name)) continue;
            if (yielded.Add(alias.Name)) yield return alias;
        }
    }
}
