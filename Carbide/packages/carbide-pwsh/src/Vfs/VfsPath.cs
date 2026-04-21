namespace CarbidePwsh.Vfs;

/// <summary>
/// Path utilities for the virtualized filesystem. All paths are forward-slash normalized,
/// absolute paths start with <c>/</c>, and resolution is case-insensitive. This is a VFS
/// convention for the shell, not a property of any real OS the runtime is hosted on.
/// </summary>
public static class VfsPath
{
    public const char Separator = '/';
    public const string RootPath = "/";
    public const string HomePath = "/home/user";

    public static string[] Split(string path)
        => path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    public static bool IsAbsolute(string path)
        => path.Length > 0 && (path[0] == '/' || path[0] == '\\' || path.StartsWith("~"));

    /// <summary>
    /// Normalize a path relative to a current working location. Resolves <c>.</c>, <c>..</c>,
    /// and <c>~</c>; collapses duplicated separators; returns an absolute path starting with
    /// <c>/</c>. Returns <c>/</c> for empty input.
    /// </summary>
    public static string Normalize(string path, string currentLocation)
    {
        if (string.IsNullOrEmpty(path)) return RootPath;

        // Expand tilde.
        if (path == "~") path = HomePath;
        else if (path.StartsWith("~/") || path.StartsWith("~\\"))
            path = HomePath + "/" + path.Substring(2);

        // Combine with current location if relative.
        if (!IsAbsolute(path))
        {
            var cur = currentLocation.TrimEnd('/');
            if (cur.Length == 0) cur = RootPath;
            path = cur + "/" + path;
        }

        var segments = Split(path);
        var stack = new List<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        return stack.Count == 0 ? RootPath : "/" + string.Join('/', stack);
    }

    public static string Join(string a, string b)
    {
        if (string.IsNullOrEmpty(b)) return a;
        if (IsAbsolute(b)) return b;
        if (string.IsNullOrEmpty(a) || a == RootPath) return "/" + b.TrimStart('/', '\\');
        return a.TrimEnd('/') + "/" + b.TrimStart('/', '\\');
    }

    /// <summary>
    /// Split the normalized absolute path into (parent, leaf). Returns (<c>/</c>, <c></c>)
    /// for the root itself.
    /// </summary>
    public static (string Parent, string Leaf) SplitLeaf(string absolutePath)
    {
        if (absolutePath == RootPath) return (RootPath, "");
        var idx = absolutePath.LastIndexOf('/');
        if (idx <= 0) return (RootPath, absolutePath.Substring(1));
        return (absolutePath.Substring(0, idx), absolutePath.Substring(idx + 1));
    }
}
