using System.Globalization;
using System.Text;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Runtime;

/// <summary>
/// Variable expander for cmd words. Handles:
/// <list type="bullet">
///   <item><c>%VAR%</c> — immediate expansion from <see cref="EnvVarStore"/>.</item>
///   <item><c>%VAR:old=new%</c> — substring replacement (all occurrences).</item>
///   <item><c>%VAR:~start,len%</c> — substring extraction (with optional negative indices).</item>
///   <item><c>%0</c>-<c>%9</c> and <c>%*</c> — positional-parameter references.</item>
///   <item><c>%~f1</c>, <c>%~dp1</c>, <c>%~n1</c>, <c>%~x1</c>, <c>%~nx1</c>, <c>%~z1</c> —
///     parameter modifiers resolved against the session VFS.</item>
///   <item><c>!VAR!</c> — delayed expansion (only when <paramref name="delayedEnabled"/> is set).</item>
/// </list>
/// Quoted sections (<c>"..."</c>) are stripped of their surrounding quotes after expansion,
/// matching the way cmd itself treats quoted args.
/// </summary>
public static class VarExpander
{
    public static string Expand(string word, EnvVarStore env, IReadOnlyList<string> positional, string allArgs, bool delayedEnabled, VirtualFileSystem? vfs = null)
    {
        if (string.IsNullOrEmpty(word)) return word ?? "";
        var sb = new StringBuilder(word.Length);
        int i = 0;
        while (i < word.Length)
        {
            var ch = word[i];
            if (ch == '"')
            {
                i++;
                while (i < word.Length && word[i] != '"')
                {
                    ExpandOne(word, ref i, sb, env, positional, allArgs, delayedEnabled, vfs);
                }
                if (i < word.Length && word[i] == '"') i++;
                continue;
            }
            ExpandOne(word, ref i, sb, env, positional, allArgs, delayedEnabled, vfs);
        }
        return sb.ToString();
    }

    private static void ExpandOne(string word, ref int i, StringBuilder sb,
        EnvVarStore env, IReadOnlyList<string> positional, string allArgs, bool delayedEnabled, VirtualFileSystem? vfs)
    {
        var ch = word[i];
        if (ch == '%' && i + 1 < word.Length)
        {
            var next = word[i + 1];
            if (next == '*') { sb.Append(allArgs); i += 2; return; }
            if (next == '%') { sb.Append('%'); i += 2; return; }
            if (next == '~')
            {
                // Parameter modifier: %~f1, %~dp1, %~n1, ...
                i += 2;
                var modifiers = ReadParameterModifiers(word, ref i);
                if (i < word.Length && char.IsDigit(word[i]))
                {
                    var idx = word[i] - '0';
                    i++;
                    sb.Append(ApplyParameterModifiers(idx < positional.Count ? positional[idx] : "", modifiers, vfs));
                    return;
                }
                // Modifier applied to a named env var (e.g. `%~dpVAR%`).
                int nameStart = i;
                while (i < word.Length && word[i] != '%') i++;
                var name = word.Substring(nameStart, i - nameStart);
                if (i < word.Length) i++;
                var value = env.Get(name) ?? "";
                sb.Append(ApplyParameterModifiers(value, modifiers, vfs));
                return;
            }
            if (next >= '0' && next <= '9')
            {
                var idx = next - '0';
                if (idx < positional.Count) sb.Append(positional[idx]);
                i += 2;
                return;
            }
            // %NAME% with optional substring/substitution form.
            var end = word.IndexOf('%', i + 1);
            if (end > i + 1)
            {
                var inside = word.Substring(i + 1, end - i - 1);
                sb.Append(ExpandNamed(inside, env));
                i = end + 1;
                return;
            }
            // Single-character FOR-loop variable like `%X` (no trailing %).
            if (char.IsLetter(next))
            {
                sb.Append(env.Get(next.ToString()) ?? "");
                i += 2;
                return;
            }
            sb.Append(ch);
            i++;
            return;
        }
        if (ch == '!' && delayedEnabled && i + 1 < word.Length)
        {
            var end = word.IndexOf('!', i + 1);
            if (end > i + 1)
            {
                var inside = word.Substring(i + 1, end - i - 1);
                sb.Append(ExpandNamed(inside, env));
                i = end + 1;
                return;
            }
        }
        sb.Append(ch);
        i++;
    }

    private static string ExpandNamed(string inside, EnvVarStore env)
    {
        // `inside` might be `NAME`, `NAME:old=new`, or `NAME:~start[,len]`.
        int colon = inside.IndexOf(':');
        if (colon < 0) return env.Get(inside) ?? "";

        var name = inside.Substring(0, colon);
        var spec = inside.Substring(colon + 1);
        var value = env.Get(name) ?? "";
        if (spec.StartsWith("~"))
        {
            var args = spec.Substring(1).Split(',');
            if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) return value;
            if (start < 0) start = Math.Max(0, value.Length + start);
            start = Math.Min(start, value.Length);
            if (args.Length == 1) return value.Substring(start);
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var len)) return value.Substring(start);
            if (len < 0) len = Math.Max(0, value.Length - start + len);
            len = Math.Min(len, value.Length - start);
            return value.Substring(start, len);
        }
        // Substitution: `old=new`.
        var eq = spec.IndexOf('=');
        if (eq < 0) return value;
        var oldText = spec.Substring(0, eq);
        var newText = spec.Substring(eq + 1);
        if (oldText.Length == 0) return value;
        return value.Replace(oldText, newText, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadParameterModifiers(string word, ref int i)
    {
        var sb = new StringBuilder();
        while (i < word.Length)
        {
            var c = word[i];
            if (c == 'f' || c == 'd' || c == 'p' || c == 'n' || c == 'x' || c == 's' || c == 't' || c == 'z' || c == 'a')
            {
                sb.Append(c);
                i++;
                continue;
            }
            break;
        }
        return sb.ToString();
    }

    private static string ApplyParameterModifiers(string value, string modifiers, VirtualFileSystem? vfs)
    {
        if (modifiers.Length == 0) return StripOuterQuotes(value);
        var path = StripOuterQuotes(value);
        if (path.Length == 0) return "";
        var abs = vfs?.Normalize(path) ?? path;
        var (parent, leaf) = VfsPath.SplitLeaf(abs);
        var dot = leaf.LastIndexOf('.');
        var name = dot > 0 ? leaf.Substring(0, dot) : leaf;
        var ext = dot > 0 ? leaf.Substring(dot) : "";

        var sb = new StringBuilder();
        bool hasDrive = modifiers.Contains('d', StringComparison.Ordinal);
        bool hasPath = modifiers.Contains('p', StringComparison.Ordinal);
        bool hasName = modifiers.Contains('n', StringComparison.Ordinal);
        bool hasExt = modifiers.Contains('x', StringComparison.Ordinal);
        bool hasFull = modifiers.Contains('f', StringComparison.Ordinal);
        bool hasSize = modifiers.Contains('z', StringComparison.Ordinal);
        bool hasAttr = modifiers.Contains('a', StringComparison.Ordinal);
        bool hasTime = modifiers.Contains('t', StringComparison.Ordinal);

        if (hasFull)
        {
            return abs;
        }
        if (hasDrive) sb.Append("C:");
        if (hasPath) sb.Append(parent == VfsPath.RootPath ? "/" : parent + "/");
        if (hasName) sb.Append(name);
        if (hasExt) sb.Append(ext);
        if (hasSize && vfs?.Resolve(abs) is VfsFile f) sb.Append(f.Length.ToString(CultureInfo.InvariantCulture));
        if (hasTime && vfs?.Resolve(abs) is VfsNode n) sb.Append(n.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (hasAttr) sb.Append(vfs?.IsDirectory(abs) == true ? "d" : "a");
        if (sb.Length == 0) return abs;
        return sb.ToString();
    }

    private static string StripOuterQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        return s;
    }
}
