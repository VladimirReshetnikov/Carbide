namespace CarbidePwsh.Cmdlets;

public sealed class CmdletRegistry
{
    private readonly Dictionary<string, Func<Cmdlet>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public void Register(Func<Cmdlet> factory)
    {
        var instance = factory();
        _byName[instance.Name] = factory;
        foreach (var alias in instance.Aliases)
        {
            _byName[alias] = factory;
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
}
