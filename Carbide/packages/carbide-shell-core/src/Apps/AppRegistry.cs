namespace CarbideShellCore.Apps;

/// <summary>
/// Session-global name-to-VFS-path mapping for Carbide-compiled apps. Any shell can register
/// an app here; once registered, a plain name lookup through the shell dispatcher finds the
/// app's VFS path so that the host can load and invoke its entry-point assembly. Case-
/// insensitive to match cmd/pwsh conventions (bash dispatch is stricter but looks up the
/// same table through its own resolver).
/// </summary>
public sealed class AppRegistry
{
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
