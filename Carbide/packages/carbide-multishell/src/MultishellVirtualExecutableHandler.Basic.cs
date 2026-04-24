using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;

#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
namespace CarbidePwsh.SharedMultishell;
#else
namespace CarbideMultishell;
#endif

internal sealed partial class MultishellVirtualExecutableHandler
{
    private static int ExecuteGnuCat(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Cat);
    private static int ExecuteGnuCp(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Cp);
    private static int ExecuteGnuLs(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Ls);
    private static int ExecuteGnuMkdir(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Mkdir);
    private static int ExecuteGnuMv(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Mv);
    private static int ExecuteGnuPrintf(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Printf);
    private static int ExecuteGnuPwd(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Pwd);
    private static int ExecuteGnuRm(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Rm);
    private static int ExecuteGnuRmdir(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Rmdir);
    private static int ExecuteGnuTouch(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Touch);
    private static int ExecuteGnuTest(VirtualExecutableInvocation invocation) => RunBashBuiltin(invocation, CarbideBash.Builtins.Builtins.Test);

    private static int ExecuteWindowsFind(VirtualExecutableInvocation invocation) => RunCmdBuiltin(invocation, CarbideCmd.Builtins.Builtins.Find);
    private static int ExecuteWindowsSort(VirtualExecutableInvocation invocation) => RunCmdBuiltin(invocation, CarbideCmd.Builtins.Builtins.Sort);
    private static int ExecuteWindowsMore(VirtualExecutableInvocation invocation) => RunCmdBuiltin(invocation, CarbideCmd.Builtins.Builtins.More);

    private static int ExecuteGnuBasename(VirtualExecutableInvocation invocation)
    {
        bool all = false;
        string? suffix = null;
        var values = new List<string>();

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            if (arg == "-a")
            {
                all = true;
                continue;
            }
            if (arg == "-s" && i + 1 < invocation.Args.Count)
            {
                suffix = invocation.Args[++i];
                continue;
            }
            values.Add(arg);
        }

        if (values.Count == 0)
            return Unsupported(invocation, "missing operand");

        if (!all && values.Count > 1)
            values = [values[0]];

        foreach (var value in values)
        {
            var leaf = VfsPath.SplitLeaf(invocation.Vfs.Normalize(value)).Leaf;
            if (!string.IsNullOrEmpty(suffix) && leaf.EndsWith(suffix, StringComparison.Ordinal))
                leaf = leaf[..^suffix.Length];
            invocation.Output.WriteLine(leaf);
        }
        return 0;
    }

    private static int ExecuteGnuDirname(VirtualExecutableInvocation invocation)
    {
        if (invocation.Args.Count == 0)
            return Unsupported(invocation, "missing operand");

        foreach (var value in invocation.Args)
        {
            var normalized = invocation.Vfs.Normalize(value);
            var parent = VfsPath.SplitLeaf(normalized).Parent;
            invocation.Output.WriteLine(parent);
        }
        return 0;
    }

    private static int ExecuteGnuEnv(VirtualExecutableInvocation invocation)
    {
        using var scope = invocation.Env.PushScope();
        int index = 0;
        while (index < invocation.Args.Count && invocation.Args[index].Contains('='))
        {
            var arg = invocation.Args[index];
            var equals = arg.IndexOf('=');
            invocation.Env.Set(arg[..equals], arg[(equals + 1)..]);
            index++;
        }

        if (index >= invocation.Args.Count)
        {
            foreach (var kv in invocation.Env.All.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                invocation.Output.WriteLine($"{kv.Key}={kv.Value}");
            return 0;
        }

        var commandName = invocation.Args[index];
        var forwarded = invocation.Args.Skip(index + 1).ToArray();
        return DispatchCommand(invocation, commandName, forwarded, "bash");
    }

    private static int ExecuteGnuPrintenv(VirtualExecutableInvocation invocation)
    {
        if (invocation.Args.Count == 0)
        {
            foreach (var kv in invocation.Env.All.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                invocation.Output.WriteLine($"{kv.Key}={kv.Value}");
            return 0;
        }

        int code = 0;
        foreach (var name in invocation.Args)
        {
            var value = invocation.Env.Get(name);
            if (value is null)
            {
                code = 1;
                continue;
            }
            invocation.Output.WriteLine(value);
        }
        return code;
    }

    private static int ExecuteGnuHostname(VirtualExecutableInvocation invocation)
    {
        invocation.Output.WriteLine(GetHostName(invocation.Env));
        return 0;
    }

    private static int ExecuteGnuUname(VirtualExecutableInvocation invocation)
    {
        bool all = invocation.Args.Contains("-a");
        var parts = new List<string>();
        if (all || invocation.Args.Count == 0 || invocation.Args.Contains("-s")) parts.Add("Carbide");
        if (all || invocation.Args.Contains("-r")) parts.Add("0.1");
        if (all || invocation.Args.Contains("-m")) parts.Add("browser-node");
        invocation.Output.WriteLine(string.Join(" ", parts.Distinct(StringComparer.Ordinal)));
        return 0;
    }

    private static int ExecuteGnuWhoami(VirtualExecutableInvocation invocation)
    {
        invocation.Output.WriteLine(GetUserName(invocation.Env));
        return 0;
    }

    private static int ExecuteWindowsWhoAmI(VirtualExecutableInvocation invocation)
    {
        var user = GetUserName(invocation.Env);
        if (invocation.Args.Any(a => a.Equals("/USER", StringComparison.OrdinalIgnoreCase)))
        {
            invocation.Output.WriteLine($"USER INFORMATION");
            invocation.Output.WriteLine($"----------------");
            invocation.Output.WriteLine($"carbide\\{user}");
            return 0;
        }

        if (invocation.Args.Any(a => a.Equals("/GROUPS", StringComparison.OrdinalIgnoreCase)))
        {
            invocation.Output.WriteLine($"GROUP INFORMATION");
            invocation.Output.WriteLine($"-----------------");
            invocation.Output.WriteLine($"carbide\\users");
            invocation.Output.WriteLine($"carbide\\developers");
            return 0;
        }

        invocation.Output.WriteLine($"carbide\\{user}");
        return 0;
    }

    private static int ExecuteGnuWhich(VirtualExecutableInvocation invocation)
    {
        bool all = invocation.Args.Contains("-a");
        var names = invocation.Args.Where(a => a != "-a").ToArray();
        if (names.Length == 0)
            return Unsupported(invocation, "missing command name");

        int code = 0;
        foreach (var name in names)
        {
            var matches = SearchVisiblePaths(invocation, name, "bash", all);
            if (matches.Count == 0)
            {
                code = 1;
                continue;
            }
            foreach (var match in matches)
                invocation.Output.WriteLine(match);
        }
        return code;
    }

    private static int ExecuteWindowsWhere(VirtualExecutableInvocation invocation)
    {
        bool quote = invocation.Args.Any(a => a.Equals("/F", StringComparison.OrdinalIgnoreCase));
        bool quiet = invocation.Args.Any(a => a.Equals("/Q", StringComparison.OrdinalIgnoreCase));
        bool timestamps = invocation.Args.Any(a => a.Equals("/T", StringComparison.OrdinalIgnoreCase));
        bool recursive = invocation.Args.Any(a => a.Equals("/R", StringComparison.OrdinalIgnoreCase));
        string? recursiveRoot = null;
        string? pattern = null;

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            if (arg.Equals("/R", StringComparison.OrdinalIgnoreCase) && i + 1 < invocation.Args.Count)
            {
                recursiveRoot = invocation.Args[++i];
                continue;
            }
            if (arg.StartsWith("/", StringComparison.Ordinal))
                continue;
            pattern ??= arg;
        }

        if (pattern is null)
            return Unsupported(invocation, "missing pattern");

        var matches = new List<string>();
        if (recursive)
        {
            var root = invocation.Vfs.Normalize(recursiveRoot ?? invocation.Vfs.CurrentLocation);
            var rx = new Regex(WildcardToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (var node in Walk(invocation.Vfs, root, 0, int.MaxValue))
            {
                if (node is not VfsFile || !rx.IsMatch(node.Name))
                    continue;
                matches.Add(node.AbsolutePath);
            }
        }
        else
        {
            matches.AddRange(SearchVisiblePaths(invocation, pattern, "cmd", allMatches: true));
        }

        if (matches.Count == 0)
            return quiet ? 1 : 0;

        if (quiet)
            return 0;

        foreach (var match in matches.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (invocation.Vfs.Resolve(match) is not VfsFile file)
                continue;

            var display = FormatWindowsPath(match);
            if (quote)
                display = $"\"{display}\"";

            if (timestamps)
                invocation.Output.WriteLine($"{display} {file.LastWriteTimeUtc:u} {file.Length}");
            else
                invocation.Output.WriteLine(display);
        }
        return 0;
    }

    private static int ExecuteGnuReadlink(VirtualExecutableInvocation invocation)
        => ExecuteRealPathLike(invocation, strictExisting: invocation.Args.Contains("-e"), allowMissingLeaf: invocation.Args.Contains("-f") || invocation.Args.Contains("-m"));

    private static int ExecuteGnuRealpath(VirtualExecutableInvocation invocation)
    {
        bool strict = invocation.Args.Contains("-e");
        bool allowMissing = invocation.Args.Contains("-m") || !strict;
        string? relativeTo = null;
        var paths = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "--relative-to" && i + 1 < invocation.Args.Count)
            {
                relativeTo = invocation.Vfs.Normalize(invocation.Args[++i]);
                continue;
            }
            if (invocation.Args[i].StartsWith("-", StringComparison.Ordinal))
                continue;
            paths.Add(invocation.Args[i]);
        }

        if (paths.Count == 0)
            return Unsupported(invocation, "missing path");

        int code = 0;
        foreach (var path in paths)
        {
            var normalized = invocation.Vfs.Normalize(path);
            if (!allowMissing && !invocation.Vfs.Exists(normalized))
            {
                invocation.Error.WriteLine($"realpath: {path}: No such file or directory");
                code = 1;
                continue;
            }

            if (relativeTo is not null)
            {
                var relative = MakeRelativePath(relativeTo, normalized);
                invocation.Output.WriteLine(relative);
            }
            else
            {
                invocation.Output.WriteLine(normalized);
            }
        }
        return code;
    }

    private static int ExecuteRealPathLike(VirtualExecutableInvocation invocation, bool strictExisting, bool allowMissingLeaf)
    {
        var paths = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (paths.Length == 0)
            return Unsupported(invocation, "missing path");

        int code = 0;
        foreach (var path in paths)
        {
            var normalized = invocation.Vfs.Normalize(path);
            if (strictExisting && !invocation.Vfs.Exists(normalized))
            {
                invocation.Error.WriteLine($"readlink: {path}: No such file or directory");
                code = 1;
                continue;
            }
            if (!allowMissingLeaf && !invocation.Vfs.Exists(normalized))
            {
                invocation.Error.WriteLine($"readlink: {path}: No such file or directory");
                code = 1;
                continue;
            }
            invocation.Output.WriteLine(normalized);
        }
        return code;
    }

    private static string MakeRelativePath(string fromAbsolute, string toAbsolute)
    {
        var from = VfsPath.Split(fromAbsolute);
        var to = VfsPath.Split(toAbsolute);
        int common = 0;
        while (common < from.Length && common < to.Length && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
            common++;

        var parts = new List<string>();
        for (int i = common; i < from.Length; i++)
            parts.Add("..");
        for (int i = common; i < to.Length; i++)
            parts.Add(to[i]);
        return parts.Count == 0 ? "." : string.Join("/", parts);
    }

    private static int ExecuteGnuSeq(VirtualExecutableInvocation invocation)
    {
        string separator = "\n";
        string? format = null;
        bool equalWidth = false;
        var values = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            if (arg == "-s" && i + 1 < invocation.Args.Count)
            {
                separator = invocation.Args[++i];
                continue;
            }
            if (arg == "-f" && i + 1 < invocation.Args.Count)
            {
                format = invocation.Args[++i];
                continue;
            }
            if (arg == "-w")
            {
                equalWidth = true;
                continue;
            }
            values.Add(arg);
        }

        if (values.Count is < 1 or > 3)
            return Unsupported(invocation, "expected 1, 2, or 3 numeric operands");

        decimal start, step, end;
        if (values.Count == 1)
        {
            start = 1;
            step = 1;
            end = decimal.Parse(values[0], CultureInfo.InvariantCulture);
        }
        else if (values.Count == 2)
        {
            start = decimal.Parse(values[0], CultureInfo.InvariantCulture);
            step = 1;
            end = decimal.Parse(values[1], CultureInfo.InvariantCulture);
        }
        else
        {
            start = decimal.Parse(values[0], CultureInfo.InvariantCulture);
            step = decimal.Parse(values[1], CultureInfo.InvariantCulture);
            end = decimal.Parse(values[2], CultureInfo.InvariantCulture);
        }

        var items = new List<string>();
        for (decimal value = start;
             step >= 0 ? value <= end : value >= end;
             value += step)
        {
            string rendered = format is not null
                ? string.Format(CultureInfo.InvariantCulture, ConvertSeqFormat(format), value)
                : value.ToString(CultureInfo.InvariantCulture);
            items.Add(rendered);
            if (items.Count > 100000)
                break;
        }

        if (equalWidth && items.Count > 0)
        {
            int width = items.Max(item => item.Length);
            for (int i = 0; i < items.Count; i++)
                items[i] = items[i].PadLeft(width, '0');
        }

        invocation.Output.Write(string.Join(separator, items));
        if (separator == "\n")
            invocation.Output.WriteLine();
        return 0;
    }

    private static string ConvertSeqFormat(string format)
        => format.Replace("%g", "{0}", StringComparison.Ordinal)
            .Replace("%f", "{0:F}", StringComparison.Ordinal)
            .Replace("%d", "{0:0}", StringComparison.Ordinal);

    private static int ExecuteGnuSleep(VirtualExecutableInvocation invocation)
    {
        if (invocation.Args.Count == 0)
            return Unsupported(invocation, "missing operand");

        double totalSeconds = 0;
        foreach (var arg in invocation.Args)
        {
            totalSeconds += ParseDurationSeconds(arg);
        }

        if (totalSeconds < 0)
            totalSeconds = 0;

        Thread.Sleep(TimeSpan.FromSeconds(totalSeconds));
        return 0;
    }

    private static double ParseDurationSeconds(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
            return 0;
        double multiplier = 1;
        if (char.IsLetter(token[^1]))
        {
            multiplier = char.ToLowerInvariant(token[^1]) switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                'd' => 86400,
                _ => 1,
            };
            token = token[..^1];
        }
        return double.Parse(token, CultureInfo.InvariantCulture) * multiplier;
    }

    private static int ExecuteGnuMktemp(VirtualExecutableInvocation invocation)
    {
        bool directory = invocation.Args.Contains("-d");
        var template = invocation.Args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? "tmp.XXXXXXXXXX";
        var parent = template.Contains('/') || template.Contains('\\')
            ? invocation.Vfs.Normalize(VfsPath.SplitLeaf(template).Parent)
            : "/tmp";
        invocation.Vfs.GetOrCreateDirectory(parent);

        string leafTemplate = VfsPath.SplitLeaf(template).Leaf;
        if (leafTemplate.Length == 0)
            leafTemplate = "tmp.XXXXXXXXXX";

        string candidatePath;
        do
        {
            var random = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
            var replaced = ReplaceTemplateXs(leafTemplate, random);
            candidatePath = invocation.Vfs.Normalize(VfsPath.Join(parent, replaced));
        }
        while (invocation.Vfs.Exists(candidatePath));

        if (directory)
            invocation.Vfs.CreateDirectory(candidatePath);
        else
            invocation.Vfs.CreateTextFile(candidatePath, "", overwrite: false);
        invocation.Output.WriteLine(candidatePath);
        return 0;
    }

    private static string ReplaceTemplateXs(string template, string replacement)
    {
        int xCount = template.Count(c => c == 'X');
        if (xCount == 0)
            return template + replacement[..6];
        int replaceLength = Math.Min(xCount, replacement.Length);
        return template.Replace(new string('X', xCount), replacement[..replaceLength], StringComparison.Ordinal);
    }

    private static int ExecuteGnuDate(VirtualExecutableInvocation invocation)
    {
        bool utc = invocation.Args.Contains("-u");
        var now = utc ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        string? iso = null;
        string? format = null;
        string? dateExpression = null;

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            if (arg == "-I")
            {
                iso = "date";
                continue;
            }
            if (arg == "-d" && i + 1 < invocation.Args.Count)
            {
                dateExpression = invocation.Args[++i];
                continue;
            }
            if (arg.StartsWith("+", StringComparison.Ordinal))
            {
                format = arg[1..];
            }
        }

        if (!string.IsNullOrEmpty(dateExpression))
            now = ParseDateExpression(dateExpression!, now);

        if (format is not null)
        {
            invocation.Output.WriteLine(RenderDateFormat(now, format));
            return 0;
        }

        if (iso is not null)
        {
            invocation.Output.WriteLine(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return 0;
        }

        invocation.Output.WriteLine(now.ToString("ddd MMM dd HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture));
        return 0;
    }

    private static DateTimeOffset ParseDateExpression(string expression, DateTimeOffset basis)
    {
        expression = expression.Trim();
        if (DateTimeOffset.TryParse(expression, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;
        if (expression.StartsWith('@') && long.TryParse(expression[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (expression.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
            return basis.AddDays(1);
        if (expression.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            return basis.AddDays(-1);
        if (expression.Equals("now", StringComparison.OrdinalIgnoreCase))
            return basis;

        var parts = TokenizeWhitespace(expression);
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return parts[1].ToLowerInvariant() switch
            {
                "second" or "seconds" => basis.AddSeconds(amount),
                "minute" or "minutes" => basis.AddMinutes(amount),
                "hour" or "hours" => basis.AddHours(amount),
                "day" or "days" => basis.AddDays(amount),
                _ => basis,
            };
        }
        return basis;
    }

    private static string RenderDateFormat(DateTimeOffset value, string format)
    {
        return format
            .Replace("%Y", value.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%m", value.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%d", value.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%H", value.ToString("HH", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%M", value.ToString("mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%S", value.ToString("ss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%F", value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%T", value.ToString("HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%z", value.ToString("zzz", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static int ExecuteGnuYes(VirtualExecutableInvocation invocation)
    {
        var text = invocation.Args.Count == 0 ? "y" : string.Join(" ", invocation.Args);
        for (int i = 0; i < 1024; i++)
            invocation.Output.WriteLine(text);
        return 0;
    }

    private static int ExecuteWindowsTimeout(VirtualExecutableInvocation invocation)
    {
        int seconds = 0;
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i].Equals("/T", StringComparison.OrdinalIgnoreCase) && i + 1 < invocation.Args.Count)
            {
                seconds = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
            }
        }

        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        return 0;
    }

    private static int ExecuteWindowsTree(VirtualExecutableInvocation invocation)
    {
        bool files = invocation.Args.Any(a => a.Equals("/F", StringComparison.OrdinalIgnoreCase));
        string path = invocation.Args.FirstOrDefault(a => !a.StartsWith('/')) ?? invocation.Vfs.CurrentLocation;
        var root = invocation.Vfs.Normalize(path);
        if (invocation.Vfs.Resolve(root) is not VfsDirectory)
            return Unsupported(invocation, $"'{path}' is not a directory");

        invocation.Output.WriteLine(FormatWindowsPath(root));
        RenderTree(invocation, root, "", files);
        return 0;
    }

    private static void RenderTree(VirtualExecutableInvocation invocation, string path, string prefix, bool includeFiles)
    {
        if (invocation.Vfs.Resolve(path) is not VfsDirectory directory)
            return;

        var children = directory.Children.Values
            .Where(node => includeFiles || node.IsDirectory)
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            bool last = i == children.Count - 1;
            invocation.Output.WriteLine($"{prefix}{(last ? "└── " : "├── ")}{child.Name}");
            if (child is VfsDirectory)
                RenderTree(invocation, child.AbsolutePath, prefix + (last ? "    " : "│   "), includeFiles);
        }
    }

    private static int ExecuteWindowsFc(VirtualExecutableInvocation invocation)
    {
        bool binary = invocation.Args.Any(a => a.Equals("/B", StringComparison.OrdinalIgnoreCase));
        bool lineNumbers = invocation.Args.Any(a => a.Equals("/N", StringComparison.OrdinalIgnoreCase));
        var files = invocation.Args.Where(a => !a.StartsWith('/')).ToArray();
        if (files.Length != 2)
            return Unsupported(invocation, "expected two input files");

        var left = ReadRequiredFile(invocation, files[0]);
        var right = ReadRequiredFile(invocation, files[1]);

        if (binary)
        {
            if (left.Content.SequenceEqual(right.Content))
                return 0;
            invocation.Output.WriteLine($"FC: {files[0]} and {files[1]} differ");
            return 1;
        }

        var leftLines = SplitLinesPreserveTrailingEmpty(left.ReadText());
        var rightLines = SplitLinesPreserveTrailingEmpty(right.ReadText());
        if (leftLines.SequenceEqual(rightLines, StringComparer.Ordinal))
            return 0;

        int max = Math.Max(leftLines.Length, rightLines.Length);
        for (int i = 0; i < max; i++)
        {
            var leftLine = i < leftLines.Length ? leftLines[i] : null;
            var rightLine = i < rightLines.Length ? rightLines[i] : null;
            if (string.Equals(leftLine, rightLine, StringComparison.Ordinal))
                continue;
            if (lineNumbers)
                invocation.Output.WriteLine($"***** {i + 1}");
            invocation.Output.WriteLine(leftLine ?? "");
            invocation.Output.WriteLine(rightLine ?? "");
        }
        return 1;
    }

    private static int ExecuteWindowsXCopy(VirtualExecutableInvocation invocation)
    {
        bool recursive = invocation.Args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/E", StringComparison.OrdinalIgnoreCase));
        bool dryRun = invocation.Args.Any(a => a.Equals("/L", StringComparison.OrdinalIgnoreCase));
        var paths = invocation.Args.Where(a => !a.StartsWith('/')).ToArray();
        if (paths.Length < 2)
            return Unsupported(invocation, "expected source and destination");

        return CopyTree(invocation, paths[0], paths[1], recursive, dryRun, windowsStyleOutput: true);
    }

    private static int ExecuteWindowsRobocopy(VirtualExecutableInvocation invocation)
    {
        bool recursive = invocation.Args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/E", StringComparison.OrdinalIgnoreCase) || a.Equals("/MIR", StringComparison.OrdinalIgnoreCase));
        bool dryRun = invocation.Args.Any(a => a.Equals("/L", StringComparison.OrdinalIgnoreCase));
        var excludesFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludesDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            if (arg.Equals("/XF", StringComparison.OrdinalIgnoreCase))
            {
                while (i + 1 < invocation.Args.Count && !invocation.Args[i + 1].StartsWith('/'))
                    excludesFiles.Add(invocation.Args[++i]);
                continue;
            }
            if (arg.Equals("/XD", StringComparison.OrdinalIgnoreCase))
            {
                while (i + 1 < invocation.Args.Count && !invocation.Args[i + 1].StartsWith('/'))
                    excludesDirs.Add(invocation.Args[++i]);
                continue;
            }
            if (arg.StartsWith("/", StringComparison.Ordinal))
                continue;
            positionals.Add(arg);
        }

        if (positionals.Count < 2)
            return Unsupported(invocation, "expected source and destination");

        return CopyTree(invocation, positionals[0], positionals[1], recursive, dryRun, windowsStyleOutput: false, excludesFiles, excludesDirs);
    }

    private static int CopyTree(
        VirtualExecutableInvocation invocation,
        string source,
        string destination,
        bool recursive,
        bool dryRun,
        bool windowsStyleOutput,
        ISet<string>? excludesFiles = null,
        ISet<string>? excludesDirs = null)
    {
        excludesFiles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        excludesDirs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var src = invocation.Vfs.Resolve(invocation.Vfs.Normalize(source));
        if (src is null)
            return Unsupported(invocation, $"source '{source}' does not exist");

        if (src is VfsFile file)
        {
            if (!dryRun)
                invocation.Vfs.Copy(file.AbsolutePath, destination, recursive: false);
            if (windowsStyleOutput)
                invocation.Output.WriteLine("        1 File(s) copied");
            return 0;
        }

        if (!recursive)
            return Unsupported(invocation, "directory copy requires recursive mode");

        var directory = (VfsDirectory)src;
        if (!dryRun)
            invocation.Vfs.GetOrCreateDirectory(destination);

        foreach (var child in directory.Children.Values)
        {
            if (child is VfsDirectory subDirectory)
            {
                if (excludesDirs.Contains(subDirectory.Name))
                    continue;
                CopyTree(invocation, subDirectory.AbsolutePath, VfsPath.Join(destination, subDirectory.Name), recursive: true, dryRun, windowsStyleOutput, excludesFiles, excludesDirs);
            }
            else if (child is VfsFile childFile)
            {
                if (excludesFiles.Contains(childFile.Name))
                    continue;
                if (!dryRun)
                    invocation.Vfs.Copy(childFile.AbsolutePath, VfsPath.Join(destination, childFile.Name), recursive: false);
            }
        }

        return 0;
    }

    private static VfsFile ReadRequiredFile(VirtualExecutableInvocation invocation, string path)
    {
        var absolute = invocation.Vfs.Normalize(path);
        return invocation.Vfs.Resolve(absolute) as VfsFile
            ?? throw new VfsException($"Cannot find path '{absolute}' because it does not exist.");
    }

    private static int ExecuteGnuCut(VirtualExecutableInvocation invocation)
    {
        string? byteSpec = null;
        string? charSpec = null;
        string? fieldSpec = null;
        string delimiter = "\t";
        var files = new List<string>();

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            switch (invocation.Args[i])
            {
                case "-b" when i + 1 < invocation.Args.Count:
                    byteSpec = invocation.Args[++i];
                    break;
                case "-c" when i + 1 < invocation.Args.Count:
                    charSpec = invocation.Args[++i];
                    break;
                case "-f" when i + 1 < invocation.Args.Count:
                    fieldSpec = invocation.Args[++i];
                    break;
                case "-d" when i + 1 < invocation.Args.Count:
                    delimiter = invocation.Args[++i];
                    break;
                default:
                    files.Add(invocation.Args[i]);
                    break;
            }
        }

        if (byteSpec is null && charSpec is null && fieldSpec is null)
            return Unsupported(invocation, "one of -b, -c, or -f is required");

        foreach (var (_, _, text) in EnumerateTexts(invocation, files))
        {
            foreach (var line in SplitLinesPreserveTrailingEmpty(text).SkipLast(1))
            {
                invocation.Output.WriteLine(fieldSpec is not null
                    ? CutFields(line, delimiter, fieldSpec)
                    : CutCharacters(line, charSpec ?? byteSpec!));
            }
        }
        return 0;
    }

    private static string CutCharacters(string line, string spec)
    {
        var indices = ParseSelectionSpec(spec, line.Length);
        var sb = new StringBuilder();
        foreach (var index in indices)
        {
            if (index >= 0 && index < line.Length)
                sb.Append(line[index]);
        }
        return sb.ToString();
    }

    private static string CutFields(string line, string delimiter, string spec)
    {
        var parts = line.Split(delimiter);
        var indices = ParseSelectionSpec(spec, parts.Length);
        return string.Join(delimiter, indices.Where(i => i >= 0 && i < parts.Length).Select(i => parts[i]));
    }

    private static IReadOnlyList<int> ParseSelectionSpec(string spec, int maxLength)
    {
        var indices = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
                    indices.Add(single - 1);
                continue;
            }

            var startText = part[..dash];
            var endText = part[(dash + 1)..];
            int start = string.IsNullOrEmpty(startText) ? 1 : int.Parse(startText, CultureInfo.InvariantCulture);
            int end = string.IsNullOrEmpty(endText) ? maxLength : int.Parse(endText, CultureInfo.InvariantCulture);
            for (int index = start; index <= end; index++)
                indices.Add(index - 1);
        }
        return indices.Distinct().ToArray();
    }

    private static int ExecuteGnuPaste(VirtualExecutableInvocation invocation)
    {
        string delimiters = "\t";
        bool serial = false;
        var files = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "-d" && i + 1 < invocation.Args.Count)
            {
                delimiters = invocation.Args[++i];
                continue;
            }
            if (invocation.Args[i] == "-s")
            {
                serial = true;
                continue;
            }
            files.Add(invocation.Args[i]);
        }

        var texts = EnumerateTexts(invocation, files).Select(tuple => SplitLinesPreserveTrailingEmpty(tuple.Text).SkipLast(1).ToArray()).ToList();
        if (serial)
        {
            foreach (var lines in texts)
                invocation.Output.WriteLine(string.Join(delimiters[0], lines));
            return 0;
        }

        int max = texts.Count == 0 ? 0 : texts.Max(lines => lines.Length);
        for (int row = 0; row < max; row++)
        {
            var parts = texts.Select(lines => row < lines.Length ? lines[row] : "").ToArray();
            invocation.Output.WriteLine(string.Join(delimiters[0], parts));
        }
        return 0;
    }

    private static int ExecuteGnuTee(VirtualExecutableInvocation invocation)
    {
        bool append = invocation.Args.Contains("-a");
        var files = invocation.Args.Where(a => a != "-a").ToArray();
        var text = ReadAllText(invocation.Input);
        invocation.Output.Write(text);
        foreach (var filePath in files)
        {
            var abs = invocation.Vfs.Normalize(filePath);
            if (append && invocation.Vfs.Resolve(abs) is VfsFile file)
                file.AppendText(text);
            else
                invocation.Vfs.CreateTextFile(abs, text, overwrite: true);
        }
        return 0;
    }

    private static int ExecuteGnuHead(VirtualExecutableInvocation invocation)
        => ExecuteHeadTail(invocation, takeHead: true);

    private static int ExecuteGnuTail(VirtualExecutableInvocation invocation)
        => ExecuteHeadTail(invocation, takeHead: false);

    private static int ExecuteHeadTail(VirtualExecutableInvocation invocation, bool takeHead)
    {
        int? lines = 10;
        int? bytes = null;
        var files = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "-n" && i + 1 < invocation.Args.Count)
            {
                lines = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
                bytes = null;
                continue;
            }
            if (invocation.Args[i] == "-c" && i + 1 < invocation.Args.Count)
            {
                bytes = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
                lines = null;
                continue;
            }
            files.Add(invocation.Args[i]);
        }

        foreach (var (_, _, text) in EnumerateTexts(invocation, files))
        {
            if (bytes is int byteCount)
            {
                var rendered = takeHead
                    ? text[..Math.Min(byteCount, text.Length)]
                    : text[Math.Max(0, text.Length - byteCount)..];
                invocation.Output.Write(rendered);
                if (!rendered.EndsWith('\n'))
                    invocation.Output.WriteLine();
                continue;
            }

            var lineArray = SplitLinesPreserveTrailingEmpty(text).SkipLast(1).ToArray();
            var slice = takeHead ? lineArray.Take(lines ?? 10) : lineArray.Skip(Math.Max(0, lineArray.Length - (lines ?? 10)));
            foreach (var line in slice)
                invocation.Output.WriteLine(line);
        }
        return 0;
    }

    private static int ExecuteGnuWc(VirtualExecutableInvocation invocation)
    {
        bool countLines = invocation.Args.Contains("-l");
        bool countWords = invocation.Args.Contains("-w");
        bool countBytes = invocation.Args.Contains("-c");
        bool countChars = invocation.Args.Contains("-m");
        bool longest = invocation.Args.Contains("-L");
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (!countLines && !countWords && !countBytes && !countChars && !longest)
        {
            countLines = countWords = countBytes = true;
        }

        foreach (var (label, _, text) in EnumerateTexts(invocation, files))
        {
            var lines = SplitLinesPreserveTrailingEmpty(text).SkipLast(1).ToArray();
            int words = TokenizeWhitespace(string.Join('\n', lines)).Length;
            int bytes = Encoding.UTF8.GetByteCount(text);
            int chars = text.Length;
            int longestLine = lines.Length == 0 ? 0 : lines.Max(line => line.Length);
            var fields = new List<string>();
            if (countLines) fields.Add(lines.Length.ToString(CultureInfo.InvariantCulture));
            if (countWords) fields.Add(words.ToString(CultureInfo.InvariantCulture));
            if (countBytes) fields.Add(bytes.ToString(CultureInfo.InvariantCulture));
            if (countChars) fields.Add(chars.ToString(CultureInfo.InvariantCulture));
            if (longest) fields.Add(longestLine.ToString(CultureInfo.InvariantCulture));
            if (label.Length > 0) fields.Add(label);
            invocation.Output.WriteLine(string.Join(" ", fields));
        }
        return 0;
    }

    private static int ExecuteGnuGrep(VirtualExecutableInvocation invocation)
    {
        bool caseInsensitive = false;
        bool invert = false;
        bool withLineNumbers = false;
        bool countOnly = false;
        bool listFiles = false;
        bool recursive = false;
        bool fixedStrings = LeafName(invocation.ResolvedPath).Equals("fgrep", StringComparison.OrdinalIgnoreCase);
        bool extended = LeafName(invocation.ResolvedPath).Equals("egrep", StringComparison.OrdinalIgnoreCase);
        string? pattern = null;
        var files = new List<string>();

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            switch (arg)
            {
                case "-i": caseInsensitive = true; break;
                case "-v": invert = true; break;
                case "-n": withLineNumbers = true; break;
                case "-c": countOnly = true; break;
                case "-l": listFiles = true; break;
                case "-r":
                case "-R":
                    recursive = true;
                    break;
                case "-F":
                    fixedStrings = true;
                    break;
                case "-E":
                    extended = true;
                    break;
                case "-e" when i + 1 < invocation.Args.Count:
                    pattern = invocation.Args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        break;
                    if (pattern is null)
                        pattern = arg;
                    else
                        files.Add(arg);
                    break;
            }
        }

        if (pattern is null)
            return Unsupported(invocation, "missing search pattern");

        var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
        Regex? regex = fixedStrings
            ? null
            : new Regex(extended ? pattern : pattern, options | RegexOptions.Compiled);
        int exitCode = 1;

        IEnumerable<(string Label, string Path, string Text)> sources;
        if (recursive)
        {
            var roots = files.Count == 0 ? [invocation.Vfs.CurrentLocation] : files.ToArray();
            var recursiveSources = new List<(string Label, string Path, string Text)>();
            foreach (var rootArg in roots)
            {
                var root = invocation.Vfs.Normalize(rootArg);
                foreach (var node in Walk(invocation.Vfs, root, 0, int.MaxValue))
                {
                    if (node is VfsFile file)
                        recursiveSources.Add((file.AbsolutePath, file.AbsolutePath, file.ReadText()));
                }
            }
            sources = recursiveSources;
        }
        else
        {
            sources = EnumerateTexts(invocation, files);
        }

        foreach (var source in sources)
        {
            int matches = 0;
            var rendered = new List<string>();
            var lines = SplitLinesPreserveTrailingEmpty(source.Text).SkipLast(1).ToArray();
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                bool match = fixedStrings
                    ? line.Contains(pattern, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    : regex!.IsMatch(line);
                if (invert)
                    match = !match;
                if (!match)
                    continue;

                matches++;
                if (!countOnly && !listFiles)
                {
                    var prefix = files.Count > 1 || recursive ? source.Label + ":" : "";
                    if (withLineNumbers)
                        prefix += (lineIndex + 1).ToString(CultureInfo.InvariantCulture) + ":";
                    rendered.Add(prefix + line);
                }
            }

            if (matches == 0)
                continue;

            exitCode = 0;
            if (listFiles)
            {
                invocation.Output.WriteLine(source.Label);
            }
            else if (countOnly)
            {
                invocation.Output.WriteLine(matches.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                foreach (var line in rendered)
                    invocation.Output.WriteLine(line);
            }
        }
        return exitCode;
    }

    private static int ExecuteGnuSort(VirtualExecutableInvocation invocation)
    {
        bool reverse = invocation.Args.Contains("-r");
        bool numeric = invocation.Args.Contains("-n");
        bool unique = invocation.Args.Contains("-u");
        string? fieldSeparator = null;
        int? keyField = null;
        var files = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "-t" && i + 1 < invocation.Args.Count)
            {
                fieldSeparator = invocation.Args[++i];
                continue;
            }
            if (invocation.Args[i] == "-k" && i + 1 < invocation.Args.Count)
            {
                keyField = int.Parse(invocation.Args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
                continue;
            }
            if (invocation.Args[i].StartsWith("-", StringComparison.Ordinal))
                continue;
            files.Add(invocation.Args[i]);
        }

        var lines = new List<string>();
        foreach (var (_, _, text) in EnumerateTexts(invocation, files))
            lines.AddRange(SplitLinesPreserveTrailingEmpty(text).SkipLast(1));

        Func<string, object> selector = line =>
        {
            var key = line;
            if (!string.IsNullOrEmpty(fieldSeparator) && keyField is int field)
            {
                var parts = line.Split(fieldSeparator);
                key = field >= 1 && field <= parts.Length ? parts[field - 1] : "";
            }
            if (numeric && decimal.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
            return key;
        };

        var ordered = numeric
            ? lines.OrderBy(line =>
            {
                var key = selector(line);
                return key is decimal decimalKey ? decimalKey : 0m;
            }).ToList()
            : lines.OrderBy(line => selector(line).ToString(), StringComparer.Ordinal).ToList();
        if (unique)
            ordered = ordered.Distinct(StringComparer.Ordinal).ToList();
        if (reverse)
            ordered.Reverse();
        foreach (var line in ordered)
            invocation.Output.WriteLine(line);
        return 0;
    }

    private static int ExecuteGnuUniq(VirtualExecutableInvocation invocation)
    {
        bool counts = invocation.Args.Contains("-c");
        bool duplicatesOnly = invocation.Args.Contains("-d");
        bool uniqueOnly = invocation.Args.Contains("-u");
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        var lines = new List<string>();
        foreach (var (_, _, text) in EnumerateTexts(invocation, files))
            lines.AddRange(SplitLinesPreserveTrailingEmpty(text).SkipLast(1));

        string? current = null;
        int count = 0;
        void Flush()
        {
            if (current is null)
                return;
            if (duplicatesOnly && count < 2)
                return;
            if (uniqueOnly && count != 1)
                return;
            invocation.Output.WriteLine(counts ? $"{count,7} {current}" : current);
        }

        foreach (var line in lines)
        {
            if (string.Equals(current, line, StringComparison.Ordinal))
            {
                count++;
                continue;
            }
            Flush();
            current = line;
            count = 1;
        }
        Flush();
        return 0;
    }

    private static int ExecuteGnuTr(VirtualExecutableInvocation invocation)
    {
        bool delete = invocation.Args.Contains("-d");
        bool squeeze = invocation.Args.Contains("-s");
        bool complement = invocation.Args.Contains("-c");
        var sets = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (sets.Length == 0)
            return Unsupported(invocation, "missing character set");

        var input = ReadAllText(invocation.Input);
        var set1 = ExpandCharacterSet(sets[0]);
        var set2 = sets.Length > 1 ? ExpandCharacterSet(sets[1]) : "";
        var output = new StringBuilder(input.Length);
        char? previous = null;

        foreach (var ch in input)
        {
            bool inSet = set1.Contains(ch, StringComparison.Ordinal);
            if (complement)
                inSet = !inSet;

            if (delete && inSet)
                continue;

            char mapped = ch;
            if (!delete && set2.Length > 0 && inSet)
            {
                var index = Math.Min(set1.IndexOf(ch), set2.Length - 1);
                if (index >= 0)
                    mapped = set2[index];
            }

            if (squeeze && previous == mapped)
                continue;

            output.Append(mapped);
            previous = mapped;
        }

        invocation.Output.Write(output.ToString());
        return 0;
    }

    private static string ExpandCharacterSet(string set)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < set.Length; i++)
        {
            if (i + 2 < set.Length && set[i + 1] == '-')
            {
                for (char ch = set[i]; ch <= set[i + 2]; ch++)
                    sb.Append(ch);
                i += 2;
            }
            else
            {
                sb.Append(set[i]);
            }
        }
        return sb.ToString();
    }

    private static int ExecuteWindowsFindStr(VirtualExecutableInvocation invocation)
    {
        bool caseInsensitive = false;
        bool recursive = false;
        bool withLineNumbers = false;
        bool namesOnly = false;
        bool invert = false;
        bool regexMode = false;
        bool literalMode = false;
        string? pattern = null;
        var files = new List<string>();

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            if (arg.Equals("/I", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; continue; }
            if (arg.Equals("/S", StringComparison.OrdinalIgnoreCase)) { recursive = true; continue; }
            if (arg.Equals("/N", StringComparison.OrdinalIgnoreCase)) { withLineNumbers = true; continue; }
            if (arg.Equals("/M", StringComparison.OrdinalIgnoreCase)) { namesOnly = true; continue; }
            if (arg.Equals("/V", StringComparison.OrdinalIgnoreCase)) { invert = true; continue; }
            if (arg.Equals("/R", StringComparison.OrdinalIgnoreCase)) { regexMode = true; continue; }
            if (arg.Equals("/L", StringComparison.OrdinalIgnoreCase)) { literalMode = true; continue; }
            if (arg.StartsWith("/C:", StringComparison.OrdinalIgnoreCase))
            {
                pattern = Unquote(arg[3..]);
                literalMode = true;
                continue;
            }

            if (arg.StartsWith("/", StringComparison.Ordinal))
                continue;

            if (pattern is null)
                pattern = Unquote(arg);
            else
                files.Add(arg);
        }

        if (pattern is null)
            return Unsupported(invocation, "missing search string");

        var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
        Regex? regex = literalMode && !regexMode ? null : new Regex(pattern, options | RegexOptions.Compiled);
        var sources = recursive
            ? files.SelectMany(rootArg => Walk(invocation.Vfs, invocation.Vfs.Normalize(rootArg), 0, int.MaxValue).OfType<VfsFile>().Select(file => (file.AbsolutePath, file.AbsolutePath, file.ReadText()))).ToArray()
            : EnumerateTexts(invocation, files.Count == 0 ? [] : files).ToArray();

        int exitCode = 1;
        foreach (var source in sources)
        {
            bool matchedFile = false;
            var lines = SplitLinesPreserveTrailingEmpty(source.Item3).SkipLast(1).ToArray();
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                bool match = literalMode && !regexMode
                    ? line.Contains(pattern, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    : regex!.IsMatch(line);
                if (invert)
                    match = !match;
                if (!match)
                    continue;
                exitCode = 0;
                matchedFile = true;
                if (namesOnly)
                    break;
                var prefix = recursive || files.Count > 1 ? source.Item1 + ":" : "";
                if (withLineNumbers)
                    prefix += (lineIndex + 1).ToString(CultureInfo.InvariantCulture) + ":";
                invocation.Output.WriteLine(prefix + line);
            }
            if (matchedFile && namesOnly)
                invocation.Output.WriteLine(source.Item1);
        }
        return exitCode;
    }

    private static int ExecuteGnuFind(VirtualExecutableInvocation invocation)
    {
        var roots = new List<string>();
        string? namePattern = null;
        string? pathPattern = null;
        bool ignoreCase = false;
        char? type = null;
        int minDepth = 0;
        int maxDepth = int.MaxValue;
        bool printNull = false;
        bool explicitPrint = false;
        List<string>? execCommand = null;

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            var arg = invocation.Args[i];
            switch (arg)
            {
                case "-name" when i + 1 < invocation.Args.Count:
                    namePattern = invocation.Args[++i];
                    break;
                case "-iname" when i + 1 < invocation.Args.Count:
                    namePattern = invocation.Args[++i];
                    ignoreCase = true;
                    break;
                case "-path" when i + 1 < invocation.Args.Count:
                    pathPattern = invocation.Args[++i];
                    break;
                case "-type" when i + 1 < invocation.Args.Count:
                    type = invocation.Args[++i][0];
                    break;
                case "-maxdepth" when i + 1 < invocation.Args.Count:
                    maxDepth = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-mindepth" when i + 1 < invocation.Args.Count:
                    minDepth = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-print":
                    explicitPrint = true;
                    break;
                case "-print0":
                    explicitPrint = true;
                    printNull = true;
                    break;
                case "-exec":
                    execCommand = new List<string>();
                    while (i + 1 < invocation.Args.Count && invocation.Args[i + 1] != ";")
                        execCommand.Add(invocation.Args[++i]);
                    if (i + 1 < invocation.Args.Count && invocation.Args[i + 1] == ";")
                        i++;
                    break;
                default:
                    if (!arg.StartsWith("-", StringComparison.Ordinal))
                        roots.Add(arg);
                    break;
            }
        }

        if (roots.Count == 0)
            roots.Add(invocation.Vfs.CurrentLocation);

        var nameRegex = namePattern is null ? null : new Regex(WildcardToRegex(namePattern), ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var pathRegex = pathPattern is null ? null : new Regex(WildcardToRegex(pathPattern), ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        int exitCode = 0;

        foreach (var rootArg in roots)
        {
            var root = invocation.Vfs.Normalize(rootArg);
            foreach (var node in Walk(invocation.Vfs, root, 0, maxDepth))
            {
                int depth = Math.Max(0, VfsPath.Split(node.AbsolutePath).Length - VfsPath.Split(root).Length);
                if (depth < minDepth)
                    continue;
                if (type is 'f' && node is not VfsFile)
                    continue;
                if (type is 'd' && node is not VfsDirectory)
                    continue;
                if (nameRegex is not null && !nameRegex.IsMatch(node.Name))
                    continue;
                if (pathRegex is not null && !pathRegex.IsMatch(node.AbsolutePath))
                    continue;

                if (execCommand is not null && execCommand.Count > 0)
                {
                    var expanded = execCommand.Select(part => part == "{}" ? node.AbsolutePath : part).ToArray();
                    var code = DispatchCommand(invocation, expanded[0], expanded.Skip(1).ToArray(), "bash");
                    exitCode = Math.Max(exitCode, code);
                }

                if (!explicitPrint && execCommand is not null)
                    continue;

                if (printNull)
                    invocation.Output.Write(node.AbsolutePath + "\0");
                else
                    invocation.Output.WriteLine(node.AbsolutePath);
            }
        }
        return exitCode;
    }
}
