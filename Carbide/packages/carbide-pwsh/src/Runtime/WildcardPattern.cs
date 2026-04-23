using System.Text.RegularExpressions;

namespace CarbidePwsh.Runtime;

internal static class WildcardPattern
{
    public static bool IsMatch(string value, string pattern, bool ignoreCase = true)
    {
        if (pattern.Length == 0) return value.Length == 0;
        if (!ContainsWildcard(pattern))
        {
            return string.Equals(
                value,
                pattern,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        var regex = "^"
            + Regex.Escape(pattern)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal)
            + "$";
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.IsMatch(value, regex, options);
    }

    public static bool IsMatchAny(string value, IEnumerable<string> patterns, bool ignoreCase = true)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(value, pattern, ignoreCase))
                return true;
        }

        return false;
    }

    private static bool ContainsWildcard(string pattern)
        => pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
}
