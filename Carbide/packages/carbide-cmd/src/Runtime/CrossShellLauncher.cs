using CarbideShellCore.Dispatch;
using CarbideShellCore.Io;

namespace CarbideCmd.Runtime;

/// <summary>
/// Translates a named-shell invocation (<c>powershell</c>, <c>pwsh</c>, <c>bash</c>,
/// <c>sh</c>) from inside a cmd script into an <see cref="IShellKernel"/> call. Parses the
/// common argv shapes (<c>-c "..."</c>, <c>-Command "..."</c>, <c>-File path</c>, bare
/// script path) and dispatches.
/// </summary>
internal static class CrossShellLauncher
{
    public static int Launch(
        IShellKernel kernel,
        IReadOnlyList<string> args,
        ShellExecutionContext parent,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        string? inlineSource = null;
        string? scriptFile = null;
        var forwarded = new List<string>();
        bool forwardOnly = false;

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (forwardOnly) { forwarded.Add(a); continue; }

            if (a.Equals("/C", StringComparison.OrdinalIgnoreCase) || a.Equals("/K", StringComparison.OrdinalIgnoreCase))
            {
                // cmd-style "run this string".
                if (i + 1 < args.Count)
                {
                    inlineSource = Unquote(args[i + 1]);
                    i++;
                }
                continue;
            }
            if (a.Equals("-c", StringComparison.OrdinalIgnoreCase)
                || a.Equals("-Command", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    inlineSource = Unquote(args[i + 1]);
                    i++;
                }
                continue;
            }
            if (a.Equals("-File", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    scriptFile = Unquote(args[i + 1]);
                    i++;
                }
                continue;
            }
            if (a == "--")
            {
                forwardOnly = true;
                continue;
            }
            if (a.StartsWith('-') || a.StartsWith('/'))
            {
                // Unknown switch — swallow it quietly for Phase 1 rather than faulting.
                continue;
            }
            if (inlineSource is null && scriptFile is null)
            {
                // First positional argument is a script path.
                scriptFile = Unquote(a);
            }
            else
            {
                forwarded.Add(Unquote(a));
            }
        }

        var childCtx = parent.With(args: forwarded, input: stdin, output: stdout, error: stderr);

        if (inlineSource is not null)
        {
            var tokens = ShellArgTokenizer.Tokenize(inlineSource);
            // For inline sources we keep argv empty (matches pwsh -c / bash -c semantics).
            _ = tokens;
            return parent.Dispatcher.ExecuteInline(kernel, inlineSource, childCtx);
        }
        if (scriptFile is not null)
        {
            var abs = parent.Vfs.Normalize(scriptFile);
            return parent.Dispatcher.ExecuteScript(abs, kernel, childCtx);
        }
        stderr.WriteLine("No inline source or script file provided.");
        return 1;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        return s;
    }
}
