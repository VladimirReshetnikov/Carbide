using System.Text;

namespace CarbideBash.Runtime;

/// <summary>
/// Pre-expansion pass that materializes bash-flavored brace expansions:
/// <list type="bullet">
///   <item><c>{a,b,c}</c> → <c>a b c</c> (comma-separated list).</item>
///   <item><c>{1..5}</c> and <c>{5..1}</c> → inclusive numeric ranges with implicit step.</item>
///   <item>Nested and cross-product (e.g. <c>{a,b}{1,2}</c>) are expanded left-to-right.</item>
/// </list>
/// Brace expansion runs before parameter expansion — it is purely textual. Quoted regions
/// (<c>'...'</c>, <c>"..."</c>) are skipped so that <c>"{a,b}"</c> stays a single token.
/// </summary>
internal static class BraceExpansion
{
    public static List<string> Expand(string word)
    {
        if (!HasBraces(word)) return new List<string> { word };
        var result = new List<string>();
        ExpandInto(word, result);
        if (result.Count == 0) result.Add(word);
        return result;
    }

    private static bool HasBraces(string s)
    {
        bool inSingle = false, inDouble = false;
        int i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (!inDouble && c == '\'') inSingle = !inSingle;
            else if (!inSingle && c == '"') inDouble = !inDouble;
            else if (!inSingle && !inDouble && c == '{' && ContainsCloser(s, i)) return true;
            i++;
        }
        return false;
    }

    private static bool ContainsCloser(string s, int open)
    {
        int depth = 1;
        for (int i = open + 1; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return true; }
        }
        return false;
    }

    private static void ExpandInto(string word, List<string> acc)
    {
        // Find the first top-level `{..}` group outside quotes.
        int start = FindTopLevelBrace(word);
        if (start < 0) { acc.Add(word); return; }
        int end = FindClosingBrace(word, start);
        if (end < 0) { acc.Add(word); return; }

        var prefix = word.Substring(0, start);
        var body = word.Substring(start + 1, end - start - 1);
        var suffix = word.Substring(end + 1);

        var pieces = SplitTopLevelCommas(body);
        if (pieces.Count == 1)
        {
            // Maybe a range `a..b`.
            var range = TryRange(body);
            if (range is null) { acc.Add(word); return; }
            pieces = range;
        }

        foreach (var piece in pieces)
        {
            var recursivePrefix = prefix + piece + suffix;
            ExpandInto(recursivePrefix, acc);
        }
    }

    private static int FindTopLevelBrace(string s)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!inDouble && c == '\'') inSingle = !inSingle;
            else if (!inSingle && c == '"') inDouble = !inDouble;
            else if (!inSingle && !inDouble && c == '{' && ContainsCloser(s, i)) return i;
        }
        return -1;
    }

    private static int FindClosingBrace(string s, int open)
    {
        int depth = 1;
        for (int i = open + 1; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static List<string> SplitTopLevelCommas(string body)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (var c in body)
        {
            if (c == '{') { depth++; sb.Append(c); }
            else if (c == '}') { depth--; sb.Append(c); }
            else if (c == ',' && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    private static List<string>? TryRange(string body)
    {
        var idx = body.IndexOf("..", StringComparison.Ordinal);
        if (idx < 0) return null;
        var left = body.Substring(0, idx);
        var right = body.Substring(idx + 2);
        if (int.TryParse(left, out var a) && int.TryParse(right, out var b))
        {
            var list = new List<string>();
            if (a <= b) for (int v = a; v <= b; v++) list.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else for (int v = a; v >= b; v--) list.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return list;
        }
        if (left.Length == 1 && right.Length == 1 && char.IsLetter(left[0]) && char.IsLetter(right[0]))
        {
            var list = new List<string>();
            char from = left[0];
            char to = right[0];
            if (from <= to) for (char c = from; c <= to; c++) list.Add(c.ToString());
            else for (char c = from; c >= to; c--) list.Add(c.ToString());
            return list;
        }
        return null;
    }
}
