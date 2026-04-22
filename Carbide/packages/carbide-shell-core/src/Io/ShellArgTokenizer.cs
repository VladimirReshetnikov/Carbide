using System.Text;

namespace CarbideShellCore.Io;

/// <summary>
/// Cross-shell argv tokenizer used by the <c>bash -c</c>, <c>cmd /c</c>, and
/// <c>powershell -Command</c> launcher shims. Each shell's own lexer handles its own
/// syntax, but when one shell invokes another through the dispatcher and the target's
/// command string is already partially lexed (e.g. a single quoted argv string), we need
/// a uniform way to split it into tokens with the quoting conventions that real shells
/// mostly agree on:
/// <list type="bullet">
///   <item>whitespace is a separator,</item>
///   <item><c>'…'</c> preserves everything literal,</item>
///   <item><c>"…"</c> preserves whitespace and processes a conservative escape subset,</item>
///   <item><c>\\</c> keeps a literal backslash outside quotes.</item>
/// </list>
/// This is intentionally simple: it is not a bash-faithful word-splitter. Any shell that
/// needs dialect-faithful tokenization uses its own lexer.
/// </summary>
public static class ShellArgTokenizer
{
    public static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(input)) return tokens;

        var sb = new StringBuilder();
        bool inToken = false;
        char quote = '\0';
        int i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (quote == '\0')
            {
                if (char.IsWhiteSpace(c))
                {
                    if (inToken)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                        inToken = false;
                    }
                    i++;
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    quote = c;
                    inToken = true;
                    i++;
                    continue;
                }
                if (c == '\\' && i + 1 < input.Length)
                {
                    sb.Append(input[i + 1]);
                    inToken = true;
                    i += 2;
                    continue;
                }
                sb.Append(c);
                inToken = true;
                i++;
            }
            else if (quote == '\'')
            {
                if (c == '\'') { quote = '\0'; i++; continue; }
                sb.Append(c); i++;
            }
            else // quote == '"'
            {
                if (c == '"') { quote = '\0'; i++; continue; }
                if (c == '\\' && i + 1 < input.Length)
                {
                    var next = input[i + 1];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => next,
                    });
                    i += 2;
                    continue;
                }
                sb.Append(c); i++;
            }
        }
        if (inToken) tokens.Add(sb.ToString());
        return tokens;
    }
}
