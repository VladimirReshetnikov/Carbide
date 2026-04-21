namespace CarbidePwsh.Runtime;

public sealed class AppRegistry
{
    // Command-name → VFS path pairing.
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, string vfsPath) => _byName[name] = vfsPath;

    public bool TryGetPath(string name, out string vfsPath)
    {
        if (_byName.TryGetValue(name, out var p)) { vfsPath = p; return true; }
        vfsPath = ""; return false;
    }

    public void Remove(string name) => _byName.Remove(name);

    public IReadOnlyDictionary<string, string> All => _byName;
}
