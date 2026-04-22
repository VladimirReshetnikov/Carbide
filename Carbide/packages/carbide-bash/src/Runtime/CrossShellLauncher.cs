using CarbideShellCore.Dispatch;

namespace CarbideBash.Runtime;

/// <summary>
/// Bash-side shim that dispatches <c>powershell</c>/<c>pwsh</c>/<c>cmd</c> invocations into
/// other registered shell kernels via the shared <see cref="ShellDispatcher"/>.
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
            if (a == "--") { forwardOnly = true; continue; }

            if (a == "-c" || a.Equals("-Command", StringComparison.OrdinalIgnoreCase)
                || a.Equals("/C", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count) { inlineSource = Unquote(args[i + 1]); i++; }
                continue;
            }
            if (a.Equals("-File", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count) { scriptFile = Unquote(args[i + 1]); i++; }
                continue;
            }
            if (a.StartsWith('-') || a.StartsWith('/'))
            {
                // Quietly swallow dialect-specific switches we don't model.
                continue;
            }
            if (inlineSource is null && scriptFile is null) scriptFile = Unquote(a);
            else forwarded.Add(Unquote(a));
        }

        var childCtx = parent.With(args: forwarded, input: stdin, output: stdout, error: stderr);

        if (inlineSource is not null)
            return parent.Dispatcher.ExecuteInline(kernel, inlineSource, childCtx);
        if (scriptFile is not null)
        {
            var abs = parent.Vfs.Normalize(scriptFile);
            return parent.Dispatcher.ExecuteScript(abs, kernel, childCtx);
        }
        stderr.WriteLine("bash: no inline source or script file.");
        return 1;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s.Substring(1, s.Length - 2);
        return s;
    }
}
