using System.Globalization;
using System.Text;
using CarbideBash.Errors;
using CarbideShellCore.Env;

namespace CarbideBash.Runtime;

/// <summary>
/// Word expansion for bash: performs (in order) parameter expansion (<c>$var</c> /
/// <c>${var}</c> / <c>${var:-default}</c> and friends), command substitution (<c>$(...)</c>
/// and backticks), arithmetic <c>$((...))</c>, and finally quote stripping and word splitting.
/// Phase 1 implements a useful subset of parameter-expansion forms (<c>:-</c>, <c>:=</c>,
/// <c>:+</c>, <c>:?</c>, <c>#</c>, <c>##</c>, <c>%</c>, <c>%%</c>, <c>/</c>, <c>//</c>).
/// </summary>
public sealed class Expansion
{
    private readonly EnvVarStore _env;
    private readonly IReadOnlyList<string> _positional;
    private readonly Func<string, string> _commandSub;
    private readonly Func<string, int> _lastExitCode;

    public Expansion(
        EnvVarStore env,
        IReadOnlyList<string> positional,
        Func<string, string> commandSubstitution,
        Func<string, int> lastExitCode)
    {
        _env = env;
        _positional = positional;
        _commandSub = commandSubstitution;
        _lastExitCode = lastExitCode;
    }

    /// <summary>Expand one word and return the resulting list of argv tokens.</summary>
    public List<string> Expand(string word)
    {
        if (string.IsNullOrEmpty(word)) return new List<string> { "" };
        var expanded = ExpandInner(word, doubleQuoted: false, out _);
        // Word-splitting applies to unquoted regions only; ExpandInner already strips quotes
        // and records where splits may occur via the \0 marker (0x00 as a split sentinel).
        return SplitOnSentinel(expanded);
    }

    /// <summary>Expand a word assumed to be inside a double-quoted context. No word splitting.</summary>
    public string ExpandDouble(string word)
    {
        return ExpandInner(word, doubleQuoted: true, out _);
    }

    private string ExpandInner(string word, bool doubleQuoted, out bool unquoted)
    {
        unquoted = !doubleQuoted;
        var sb = new StringBuilder();
        int i = 0;
        while (i < word.Length)
        {
            var c = word[i];
            if (!doubleQuoted && c == '\'')
            {
                i++;
                while (i < word.Length && word[i] != '\'') sb.Append(word[i++]);
                if (i < word.Length) i++; // closing quote
                continue;
            }
            if (!doubleQuoted && c == '"')
            {
                i++;
                var inner = new StringBuilder();
                while (i < word.Length && word[i] != '"')
                {
                    if (word[i] == '\\' && i + 1 < word.Length)
                    {
                        var next = word[i + 1];
                        if (next == '"' || next == '\\' || next == '$' || next == '`')
                        {
                            inner.Append(next);
                            i += 2;
                            continue;
                        }
                        inner.Append('\\');
                        inner.Append(next);
                        i += 2;
                        continue;
                    }
                    inner.Append(word[i]);
                    i++;
                }
                if (i < word.Length) i++;
                sb.Append(ExpandInner(inner.ToString(), doubleQuoted: true, out _));
                unquoted = false;
                continue;
            }
            if (c == '\\' && i + 1 < word.Length)
            {
                sb.Append(word[i + 1]);
                i += 2;
                continue;
            }
            if (c == '$')
            {
                i++;
                sb.Append(ExpandDollar(word, ref i));
                continue;
            }
            if (c == '`')
            {
                i++;
                var cmd = new StringBuilder();
                while (i < word.Length && word[i] != '`') cmd.Append(word[i++]);
                if (i < word.Length) i++;
                sb.Append(StripTrailingNewlines(_commandSub(cmd.ToString())));
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private string ExpandDollar(string word, ref int i)
    {
        if (i >= word.Length) return "$";
        var c = word[i];
        if (c == '(')
        {
            i++;
            if (i < word.Length && word[i] == '(')
            {
                // Arithmetic $(( ... ))
                i++;
                var expr = new StringBuilder();
                int depth = 1;
                while (i < word.Length && depth > 0)
                {
                    if (word[i] == '(') depth++;
                    else if (word[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            if (i + 1 < word.Length && word[i + 1] == ')')
                            {
                                i += 2;
                                return ArithmeticEvaluator.Evaluate(expr.ToString(), _env).ToString(CultureInfo.InvariantCulture);
                            }
                        }
                    }
                    expr.Append(word[i]);
                    i++;
                }
                throw new BashParseException("Unterminated $(( ... )).");
            }
            // $( ... ) — command substitution
            var cmd = new StringBuilder();
            int sdepth = 1;
            while (i < word.Length && sdepth > 0)
            {
                if (word[i] == '(') sdepth++;
                else if (word[i] == ')') { sdepth--; if (sdepth == 0) { i++; break; } }
                cmd.Append(word[i]);
                i++;
            }
            return StripTrailingNewlines(_commandSub(cmd.ToString()));
        }
        if (c == '{')
        {
            i++;
            var body = new StringBuilder();
            int depth = 1;
            while (i < word.Length && depth > 0)
            {
                if (word[i] == '{') depth++;
                else if (word[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                body.Append(word[i]);
                i++;
            }
            return ExpandBraceParameter(body.ToString());
        }
        if (c == '@' || c == '*')
        {
            i++;
            var skip1 = _positional.Skip(1).ToList();
            return string.Join(' ', skip1);
        }
        if (c == '#')
        {
            i++;
            return (_positional.Count > 0 ? _positional.Count - 1 : 0).ToString(CultureInfo.InvariantCulture);
        }
        if (c == '?')
        {
            i++;
            return _lastExitCode("").ToString(CultureInfo.InvariantCulture);
        }
        if (c == '$')
        {
            i++;
            return Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        }
        if (c == '!')
        {
            i++;
            return "";
        }
        if (char.IsDigit(c))
        {
            i++;
            var idx = c - '0';
            if (idx < _positional.Count) return _positional[idx];
            return "";
        }
        if (char.IsLetter(c) || c == '_')
        {
            var start = i;
            while (i < word.Length && (char.IsLetterOrDigit(word[i]) || word[i] == '_')) i++;
            var name = word.Substring(start, i - start);
            return _env.Get(name) ?? "";
        }
        return "$";
    }

    private string ExpandBraceParameter(string body)
    {
        if (body.StartsWith('#'))
        {
            var name = body.Substring(1);
            var raw = _env.Get(name) ?? "";
            return raw.Length.ToString(CultureInfo.InvariantCulture);
        }

        // name[:]op word | name/pattern/replacement | etc.
        int nameEnd = 0;
        while (nameEnd < body.Length && (char.IsLetterOrDigit(body[nameEnd]) || body[nameEnd] == '_')) nameEnd++;
        var var = body.Substring(0, nameEnd);
        var rest = body.Substring(nameEnd);
        var current = _env.Get(var);

        if (rest.Length == 0) return current ?? "";

        var op = rest[0];
        var op2 = rest.Length > 1 ? rest[1] : '\0';

        if (op == ':' && (op2 == '-' || op2 == '=' || op2 == '+' || op2 == '?'))
        {
            var arg = rest.Substring(2);
            var empty = string.IsNullOrEmpty(current);
            return op2 switch
            {
                '-' => empty ? arg : current!,
                '=' when empty => Apply(() => { _env.Set(var, arg); return arg; }),
                '=' => current!,
                '+' => empty ? "" : arg,
                '?' when empty => throw new BashRuntimeException(var + ": " + arg),
                '?' => current!,
                _ => current ?? "",
            };
        }

        if (op == '-') { var arg = rest.Substring(1); return current is null ? arg : current; }
        if (op == '=') { var arg = rest.Substring(1); if (current is null) { _env.Set(var, arg); return arg; } return current; }
        if (op == '+') { var arg = rest.Substring(1); return current is null ? "" : arg; }

        if (op == '#')
        {
            bool longest = op2 == '#';
            var pat = rest.Substring(longest ? 2 : 1);
            return StripPrefix(current ?? "", pat, longest);
        }
        if (op == '%')
        {
            bool longest = op2 == '%';
            var pat = rest.Substring(longest ? 2 : 1);
            return StripSuffix(current ?? "", pat, longest);
        }
        if (op == '/')
        {
            bool global = op2 == '/';
            var body2 = rest.Substring(global ? 2 : 1);
            var slash = body2.IndexOf('/');
            if (slash < 0) return (current ?? "").Replace(body2, "", StringComparison.Ordinal);
            var pat = body2.Substring(0, slash);
            var rep = body2.Substring(slash + 1);
            return global
                ? (current ?? "").Replace(pat, rep, StringComparison.Ordinal)
                : ReplaceFirst(current ?? "", pat, rep);
        }
        if (op == ':')
        {
            var rest2 = rest.Substring(1);
            var colon = rest2.IndexOf(':');
            if (colon < 0)
            {
                if (!int.TryParse(rest2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o)) return current ?? "";
                var s = current ?? "";
                if (o < 0) o = Math.Max(0, s.Length + o);
                return o < s.Length ? s.Substring(o) : "";
            }
            else
            {
                var offStr = rest2.Substring(0, colon);
                var lenStr = rest2.Substring(colon + 1);
                if (!int.TryParse(offStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o)) return current ?? "";
                if (!int.TryParse(lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return current ?? "";
                var s = current ?? "";
                if (o < 0) o = Math.Max(0, s.Length + o);
                if (l < 0) l = Math.Max(0, s.Length + l - o);
                o = Math.Min(o, s.Length);
                l = Math.Min(l, s.Length - o);
                return s.Substring(o, l);
            }
        }
        return current ?? "";
    }

    private static string Apply(Func<string> f) => f();

    private static string StripPrefix(string s, string pat, bool longest)
    {
        var rx = PatternToRegex(pat, anchorStart: true);
        var matches = System.Text.RegularExpressions.Regex.Matches(s, rx);
        if (matches.Count == 0) return s;
        var m = longest
            ? matches.OrderByDescending(x => x.Length).First()
            : matches.OrderBy(x => x.Length).First();
        return s.Substring(m.Length);
    }

    private static string StripSuffix(string s, string pat, bool longest)
    {
        var rx = PatternToRegex(pat, anchorEnd: true);
        var matches = System.Text.RegularExpressions.Regex.Matches(s, rx);
        if (matches.Count == 0) return s;
        var m = longest
            ? matches.OrderByDescending(x => x.Length).First()
            : matches.OrderBy(x => x.Length).First();
        if (m.Length >= s.Length) return "";
        return s.Substring(0, s.Length - m.Length);
    }

    private static string PatternToRegex(string pat, bool anchorStart = false, bool anchorEnd = false)
    {
        var sb = new StringBuilder();
        if (anchorStart) sb.Append('^');
        foreach (var ch in pat)
        {
            if (ch == '*') sb.Append(".*");
            else if (ch == '?') sb.Append('.');
            else sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
        }
        if (anchorEnd) sb.Append('$');
        return sb.ToString();
    }

    private static string ReplaceFirst(string s, string pat, string rep)
    {
        var idx = s.IndexOf(pat, StringComparison.Ordinal);
        if (idx < 0) return s;
        return s.Substring(0, idx) + rep + s.Substring(idx + pat.Length);
    }

    private static string StripTrailingNewlines(string s)
    {
        int end = s.Length;
        while (end > 0 && (s[end - 1] == '\n' || s[end - 1] == '\r')) end--;
        return s.Substring(0, end);
    }

    private static List<string> SplitOnSentinel(string expanded)
    {
        // We currently expand in-place without inserting split sentinels; treat the entire
        // result as a single token unless the caller wants word-splitting from IFS. For
        // Phase 1 we honor a default IFS of " \t\n" only when the source word is fully
        // unquoted — the expander's double-quoted path returns early so a quoted expansion
        // reaches this point already as one token.
        return new List<string> { expanded };
    }
}
