namespace CarbidePwsh.Runtime;

public sealed class FunctionRegistry
{
    private readonly Dictionary<string, ScriptFunction> _byName = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ScriptFunction function) => _byName[function.Name] = function;

    public bool TryGet(string name, out ScriptFunction? function)
    {
        if (_byName.TryGetValue(name, out var f)) { function = f; return true; }
        function = null;
        return false;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public void Remove(string name) => _byName.Remove(name);

    public IReadOnlyCollection<string> Names => _byName.Keys;
}
