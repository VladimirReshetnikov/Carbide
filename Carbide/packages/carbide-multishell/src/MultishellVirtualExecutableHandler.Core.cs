using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CarbideBash.Builtins;
using BashBuiltinDelegate = CarbideBash.Builtins.BashBuiltin;
using BashInterpreter = CarbideBash.Runtime.Interpreter;
using CarbideCmd.Builtins;
using CmdBuiltinDelegate = CarbideCmd.Builtins.CmdBuiltin;
using CmdInterpreter = CarbideCmd.Runtime.Interpreter;
using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;

#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
namespace CarbidePwsh.SharedMultishell;
#else
namespace CarbideMultishell;
#endif

internal sealed partial class MultishellVirtualExecutableHandler : IVirtualExecutableHandler
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public int Execute(VirtualExecutableInvocation invocation) => invocation.Definition.CommandId switch
    {
        "gnu-awk" => ExecuteGnuAwk(invocation),
        "gnu-basename" => ExecuteGnuBasename(invocation),
        "gnu-bunzip2" => ExecuteGnuBunzip2(invocation),
        "gnu-bzip2" => ExecuteGnuBzip2(invocation),
        "gnu-cat" => ExecuteGnuCat(invocation),
        "gnu-cmp" => ExecuteGnuCmp(invocation),
        "gnu-comm" => ExecuteGnuComm(invocation),
        "gnu-cp" => ExecuteGnuCp(invocation),
        "gnu-cut" => ExecuteGnuCut(invocation),
        "gnu-date" => ExecuteGnuDate(invocation),
        "gnu-diff" => ExecuteGnuDiff(invocation),
        "gnu-diff3" => ExecuteGnuDiff3(invocation),
        "gnu-dirname" => ExecuteGnuDirname(invocation),
        "gnu-env" => ExecuteGnuEnv(invocation),
        "gnu-find" => ExecuteGnuFind(invocation),
        "gnu-grep" => ExecuteGnuGrep(invocation),
        "gnu-gunzip" => ExecuteGnuGunzip(invocation),
        "gnu-gzip" => ExecuteGnuGzip(invocation),
        "gnu-head" => ExecuteGnuHead(invocation),
        "gnu-hostname" => ExecuteGnuHostname(invocation),
        "gnu-ls" => ExecuteGnuLs(invocation),
        "gnu-mkdir" => ExecuteGnuMkdir(invocation),
        "gnu-mktemp" => ExecuteGnuMktemp(invocation),
        "gnu-mv" => ExecuteGnuMv(invocation),
        "gnu-paste" => ExecuteGnuPaste(invocation),
        "gnu-patch" => ExecuteGnuPatch(invocation),
        "gnu-printenv" => ExecuteGnuPrintenv(invocation),
        "gnu-printf" => ExecuteGnuPrintf(invocation),
        "gnu-pwd" => ExecuteGnuPwd(invocation),
        "gnu-readlink" => ExecuteGnuReadlink(invocation),
        "gnu-realpath" => ExecuteGnuRealpath(invocation),
        "gnu-rm" => ExecuteGnuRm(invocation),
        "gnu-rmdir" => ExecuteGnuRmdir(invocation),
        "gnu-sed" => ExecuteGnuSed(invocation),
        "gnu-seq" => ExecuteGnuSeq(invocation),
        "gnu-sleep" => ExecuteGnuSleep(invocation),
        "gnu-sort" => ExecuteGnuSort(invocation),
        "gnu-tail" => ExecuteGnuTail(invocation),
        "gnu-tar" => ExecuteGnuTar(invocation),
        "gnu-tee" => ExecuteGnuTee(invocation),
        "gnu-test" => ExecuteGnuTest(invocation),
        "gnu-touch" => ExecuteGnuTouch(invocation),
        "gnu-tr" => ExecuteGnuTr(invocation),
        "gnu-uname" => ExecuteGnuUname(invocation),
        "gnu-uniq" => ExecuteGnuUniq(invocation),
        "gnu-unzip" => ExecuteGnuUnzip(invocation),
        "gnu-wc" => ExecuteGnuWc(invocation),
        "gnu-which" => ExecuteGnuWhich(invocation),
        "gnu-whoami" => ExecuteGnuWhoami(invocation),
        "gnu-xargs" => ExecuteGnuXargs(invocation),
        "gnu-yes" => ExecuteGnuYes(invocation),
        "perl" => ExecutePerl(invocation),
        "python" => ExecutePython(invocation),
        "windows-fc" => ExecuteWindowsFc(invocation),
        "windows-find" => ExecuteWindowsFind(invocation),
        "windows-findstr" => ExecuteWindowsFindStr(invocation),
        "windows-more" => ExecuteWindowsMore(invocation),
        "windows-robocopy" => ExecuteWindowsRobocopy(invocation),
        "windows-sort" => ExecuteWindowsSort(invocation),
        "windows-tar" => ExecuteWindowsTar(invocation),
        "windows-timeout" => ExecuteWindowsTimeout(invocation),
        "windows-tree" => ExecuteWindowsTree(invocation),
        "windows-where" => ExecuteWindowsWhere(invocation),
        "windows-whoami" => ExecuteWindowsWhoAmI(invocation),
        "windows-xcopy" => ExecuteWindowsXCopy(invocation),
        _ => Unsupported(invocation, $"Unsupported virtual executable id '{invocation.Definition.CommandId}'."),
    };

    private static int RunBashBuiltin(VirtualExecutableInvocation invocation, BashBuiltinDelegate builtin, IReadOnlyList<string>? args = null)
    {
        var effectiveArgs = args ?? invocation.Args;
        var ctx = BuildContext(invocation, effectiveArgs);
        var interpreter = new BashInterpreter(ctx)
        {
            Positional = [LeafName(invocation.ResolvedPath), .. effectiveArgs],
        };

        try
        {
            return builtin(interpreter, effectiveArgs, invocation.Input, invocation.Output, invocation.Error);
        }
        catch (Exception ex) when (ex is VfsException or CarbideBash.Errors.BashRuntimeException)
        {
            invocation.Error.WriteLine($"{LeafName(invocation.ResolvedPath)}: {ex.Message}");
            return 1;
        }
    }

    private static int RunCmdBuiltin(VirtualExecutableInvocation invocation, CmdBuiltinDelegate builtin, IReadOnlyList<string>? args = null)
    {
        var effectiveArgs = args ?? invocation.Args;
        var ctx = BuildContext(invocation, effectiveArgs);
        var interpreter = new CmdInterpreter(ctx)
        {
            Positional = [LeafName(invocation.ResolvedPath), .. effectiveArgs],
        };

        try
        {
            return builtin(interpreter, effectiveArgs, invocation.Input, invocation.Output, invocation.Error);
        }
        catch (Exception ex) when (ex is VfsException or CarbideCmd.Errors.CmdRuntimeException)
        {
            invocation.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static ShellExecutionContext BuildContext(
        VirtualExecutableInvocation invocation,
        IReadOnlyList<string>? args = null,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null)
        => new()
        {
            Args = args ?? invocation.Args,
            Input = input ?? invocation.Input,
            Output = output ?? invocation.Output,
            Error = error ?? invocation.Error,
            Vfs = invocation.Vfs,
            Env = invocation.Env,
            Apps = invocation.Apps,
            Dispatcher = invocation.Dispatcher,
        };

    private static string LeafName(string path) => VfsPath.SplitLeaf(path).Leaf;

    private static string CommandDisplayName(VirtualExecutableInvocation invocation)
        => invocation.InvokedAs.Contains('/') || invocation.InvokedAs.Contains('\\')
            ? LeafName(invocation.InvokedAs)
            : invocation.InvokedAs;

    private static IEnumerable<(string Label, string Path, string Text)> EnumerateTexts(
        VirtualExecutableInvocation invocation,
        IReadOnlyList<string> fileArgs,
        bool allowMissing = false)
    {
        if (fileArgs.Count == 0)
        {
            yield return ("", "-", ReadAllText(invocation.Input));
            yield break;
        }

        foreach (var arg in fileArgs)
        {
            var abs = invocation.Vfs.Normalize(arg);
            if (invocation.Vfs.Resolve(abs) is not VfsFile file)
            {
                if (!allowMissing)
                    throw new VfsException($"Cannot find path '{abs}' because it does not exist.");
                continue;
            }
            yield return (arg, abs, file.ReadText());
        }
    }

    private static string ReadAllText(TextReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static string[] SplitLinesPreserveTrailingEmpty(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static IEnumerable<VfsNode> Walk(VirtualFileSystem vfs, string start, int depth, int maxDepth)
    {
        if (vfs.Resolve(start) is not VfsNode node)
            yield break;

        if (depth > maxDepth)
            yield break;

        yield return node;
        if (node is not VfsDirectory dir || depth == maxDepth)
            yield break;

        foreach (var child in dir.Children.Values)
        {
            foreach (var nested in Walk(vfs, child.AbsolutePath, depth + 1, maxDepth))
                yield return nested;
        }
    }

    private static string FormatWindowsPath(string path)
        => path == "/"
            ? "C:\\"
            : "C:" + path.Replace('/', '\\');

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string[] TokenizeWhitespace(string text)
        => WhitespaceRegex.Split(text.Trim()).Where(static part => part.Length > 0).ToArray();

    private static string WildcardToRegex(string pattern)
        => "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

    private static int Unsupported(VirtualExecutableInvocation invocation, string message)
    {
        invocation.Error.WriteLine($"{CommandDisplayName(invocation)}: {message}");
        return 2;
    }

    private static int DispatchCommand(
        VirtualExecutableInvocation invocation,
        string commandName,
        IReadOnlyList<string> args,
        string callerShellName,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var ctx = BuildContext(invocation, args, input, output, error);
        var resolution = invocation.Dispatcher.Resolve(commandName, ctx, callerShellName);
        return resolution.Kind switch
        {
            ResolutionKind.VirtualExecutable when resolution.VirtualExecutable is not null && resolution.VirtualExecutablePath is not null
                => invocation.Dispatcher.ExecuteVirtualExecutable(
                    resolution.VirtualExecutable,
                    resolution.VirtualExecutablePath,
                    commandName,
                    args,
                    ctx),
            ResolutionKind.Script when resolution.Kernel is not null && resolution.ScriptPath is not null
                => invocation.Dispatcher.ExecuteScript(resolution.ScriptPath, resolution.Kernel, ctx),
            ResolutionKind.App when resolution.AppPath is not null
                => ExecuteApp(invocation, resolution.AppPath, args, ctx),
            ResolutionKind.NamedShell when resolution.Kernel is not null
                => LaunchShell(resolution.Kernel, args, ctx),
            _ => 127,
        };
    }

    private static int LaunchShell(IShellKernel kernel, IReadOnlyList<string> args, ShellExecutionContext parent)
    {
        string? inlineSource = null;
        string? scriptFile = null;
        var forwarded = new List<string>();
        bool forwardOnly = false;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (forwardOnly)
            {
                forwarded.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                forwardOnly = true;
                continue;
            }

            if (arg.Equals("/C", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("/K", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("-c", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("-Command", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    inlineSource = Unquote(args[i + 1]);
                    i++;
                }
                continue;
            }

            if (arg.Equals("-File", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    scriptFile = Unquote(args[i + 1]);
                    i++;
                }
                continue;
            }

            if (arg.StartsWith('-') || arg.StartsWith('/'))
                continue;

            if (inlineSource is null && scriptFile is null)
                scriptFile = Unquote(arg);
            else
                forwarded.Add(Unquote(arg));
        }

        var childCtx = parent.With(args: forwarded);
        if (inlineSource is not null)
            return parent.Dispatcher.ExecuteInline(kernel, inlineSource, childCtx);
        if (scriptFile is not null)
            return parent.Dispatcher.ExecuteScript(parent.Vfs.Normalize(scriptFile), kernel, childCtx);
        return parent.Dispatcher.EnterSubShell(kernel, childCtx);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value.Substring(1, value.Length - 2);
        return value;
    }

    private static IReadOnlyList<string> EffectivePathRoots(EnvVarStore env, string callerShellName)
    {
        var path = env.Get("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var separator = path.Contains(';') ? ';' : ':';
            return path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return callerShellName switch
        {
            "cmd" => ["/Windows/System32", "/usr/bin", "/bin"],
            "pwsh" => ["/Windows/System32", "/Windows/System32/WindowsPowerShell/v1.0", "/usr/bin", "/bin"],
            _ => ["/usr/bin", "/bin", "/Windows/System32"],
        };
    }

    private static IReadOnlyList<string> EffectiveLeafCandidates(EnvVarStore env, string callerShellName, string commandName)
    {
        if (VfsPath.GetExtension(commandName).Length > 0)
            return [commandName];

        var candidates = new List<string> { commandName };
        if (callerShellName == "bash")
        {
            candidates.Add(commandName + ".exe");
            candidates.Add(commandName + ".com");
            return candidates;
        }

        var pathext = env.Get("PATHEXT") ?? ".COM;.EXE;.CMD;.BAT";
        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = ext.StartsWith(".", StringComparison.Ordinal) ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
            candidates.Add(commandName + normalized);
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> SearchVisiblePaths(VirtualExecutableInvocation invocation, string commandName, string callerShellName, bool allMatches)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EffectivePathRoots(invocation.Env, callerShellName))
        {
            foreach (var leaf in EffectiveLeafCandidates(invocation.Env, callerShellName, commandName))
            {
                var abs = invocation.Vfs.Normalize(VfsPath.Join(root, leaf));
                if (invocation.Vfs.Resolve(abs) is not VfsFile || !seen.Add(abs))
                    continue;
                results.Add(abs);
                if (!allMatches)
                    return results;
            }
        }

        if (invocation.Apps.TryGetPath(commandName, out var appPath) && seen.Add(appPath))
            results.Add(appPath);
        return results;
    }

    private static string GetUserName(EnvVarStore env)
        => env.Get("USER")
            ?? env.Get("USERNAME")
            ?? "user";

    private static string GetHostName(EnvVarStore env)
        => env.Get("HOSTNAME")
            ?? "carbide";
}
