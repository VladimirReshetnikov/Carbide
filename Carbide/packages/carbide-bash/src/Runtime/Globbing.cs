using System.Text;
using CarbideShellCore.Vfs;

namespace CarbideBash.Runtime;

/// <summary>
/// Filesystem globbing. Expands <c>*</c>, <c>?</c>, and <c>[set]</c> patterns against the
/// shared <see cref="VirtualFileSystem"/>. Each path segment of the pattern globs
/// independently against the corresponding level of the tree, so <c>/work/*.txt</c> matches
/// only files directly under <c>/work</c>; deeper recursion needs explicit <c>**</c>
/// (Phase-2 stretch, not implemented).
/// <para>
/// When no matches exist, the original word is returned verbatim — matching bash's default
/// <c>nullglob=off</c> behavior.
/// </para>
/// </summary>
internal static class Globbing
{
    public static List<string> Expand(string word, VirtualFileSystem vfs, string currentLocation)
    {
        if (!HasGlobChars(word)) return new List<string> { word };

        string basePath;
        string rest;
        if (word.StartsWith('/'))
        {
            basePath = "/";
            rest = word.TrimStart('/');
        }
        else
        {
            basePath = currentLocation;
            rest = word;
        }

        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string> { basePath };
        foreach (var seg in segments)
        {
            var next = new List<string>();
            foreach (var parent in results)
            {
                if (!vfs.IsDirectory(parent)) continue;
                if (HasGlobChars(seg))
                {
                    foreach (var child in vfs.List(parent, recursive: false, filter: null))
                    {
                        if (GlobMatch(seg, child.Name))
                        {
                            next.Add(JoinPath(parent, child.Name));
                        }
                    }
                }
                else
                {
                    next.Add(JoinPath(parent, seg));
                }
            }
            results = next;
        }

        if (results.Count == 0 || (results.Count == 1 && !vfs.Exists(results[0])))
            return new List<string> { word };
        return results;
    }

    private static string JoinPath(string parent, string leaf)
        => parent == "/" ? "/" + leaf : parent + "/" + leaf;

    private static bool HasGlobChars(string s)
    {
        foreach (var c in s) if (c == '*' || c == '?' || c == '[') return true;
        return false;
    }

    private static bool GlobMatch(string pattern, string name)
    {
        var rx = "^" + PatternToRegex(pattern) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx);
    }

    private static string PatternToRegex(string pat)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < pat.Length)
        {
            var c = pat[i];
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else if (c == '[')
            {
                int end = pat.IndexOf(']', i + 1);
                if (end < 0) { sb.Append(System.Text.RegularExpressions.Regex.Escape("[")); i++; continue; }
                sb.Append('[');
                for (int j = i + 1; j < end; j++)
                {
                    if (pat[j] == '!') { sb.Append('^'); continue; }
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(pat[j].ToString()));
                }
                sb.Append(']');
                i = end + 1;
                continue;
            }
            else sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
            i++;
        }
        return sb.ToString();
    }
}
