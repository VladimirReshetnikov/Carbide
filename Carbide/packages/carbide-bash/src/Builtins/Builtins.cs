using System.Globalization;
using System.Text;
using CarbideBash.Errors;
using CarbideBash.Runtime;
using CarbideShellCore.Vfs;

namespace CarbideBash.Builtins;

/// <summary>
/// Phase 1 bash built-ins. Each method implements the <see cref="BashBuiltin"/> delegate
/// and lives on the static class so <see cref="BuiltinRegistry"/> can bind it by name.
/// </summary>
public static class Builtins
{
    public static int Echo(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool newline = true;
        bool interpretEscapes = false;
        int start = 0;
        while (start < args.Count && args[start].StartsWith('-'))
        {
            var flag = args[start];
            if (flag == "-n") { newline = false; start++; continue; }
            if (flag == "-e") { interpretEscapes = true; start++; continue; }
            if (flag == "-E") { interpretEscapes = false; start++; continue; }
            break;
        }
        var joined = string.Join(' ', args.Skip(start));
        if (interpretEscapes) joined = InterpretEscapes(joined);
        if (newline) stdout.WriteLine(joined);
        else stdout.Write(joined);
        return 0;
    }

    private static string InterpretEscapes(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case 'n': sb.Append('\n'); i++; continue;
                    case 't': sb.Append('\t'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static int Printf(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0) return 2;
        var fmt = args[0];
        var rest = args.Skip(1).ToList();
        int argIdx = 0;
        do
        {
            for (int i = 0; i < fmt.Length; i++)
            {
                if (fmt[i] == '\\' && i + 1 < fmt.Length)
                {
                    switch (fmt[i + 1])
                    {
                        case 'n': stdout.Write('\n'); i++; continue;
                        case 't': stdout.Write('\t'); i++; continue;
                        case 'r': stdout.Write('\r'); i++; continue;
                    }
                }
                if (fmt[i] == '%' && i + 1 < fmt.Length)
                {
                    switch (fmt[i + 1])
                    {
                        case 's':
                            stdout.Write(argIdx < rest.Count ? rest[argIdx++] : "");
                            i++; continue;
                        case 'd':
                            stdout.Write(argIdx < rest.Count && long.TryParse(rest[argIdx], out var v) ? v : 0);
                            argIdx++; i++; continue;
                        case 'x':
                            stdout.Write((argIdx < rest.Count && long.TryParse(rest[argIdx], out var hx) ? hx : 0).ToString("x", CultureInfo.InvariantCulture));
                            argIdx++; i++; continue;
                        case '%':
                            stdout.Write('%');
                            i++; continue;
                    }
                }
                stdout.Write(fmt[i]);
            }
        } while (argIdx < rest.Count);
        return 0;
    }

    public static int Cd(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var target = args.Count > 0 ? args[0] : CarbideShellCore.Vfs.VfsPath.HomePath;
        interp.Context.Vfs.SetLocation(target);
        return 0;
    }

    public static int Pwd(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        stdout.WriteLine(interp.Context.Vfs.CurrentLocation);
        return 0;
    }

    public static int Ls(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool longFmt = false, all = false;
        var paths = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith('-'))
            {
                foreach (var c in a.Skip(1))
                {
                    if (c == 'l') longFmt = true;
                    if (c == 'a') all = true;
                }
            }
            else paths.Add(a);
        }
        if (paths.Count == 0) paths.Add(interp.Context.Vfs.CurrentLocation);
        foreach (var p in paths)
        {
            foreach (var n in interp.Context.Vfs.List(p, recursive: false, filter: null))
            {
                if (!all && n.Name.StartsWith('.')) continue;
                if (longFmt)
                {
                    var date = n.LastWriteTimeUtc.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
                    var size = n is VfsFile f ? f.Length : 0;
                    var mode = n.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--";
                    stdout.WriteLine($"{mode} 1 user user {size,8} {date} {n.Name}");
                }
                else
                {
                    stdout.WriteLine(n.Name);
                }
            }
        }
        return 0;
    }

    public static int Cat(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0)
        {
            string? line;
            while ((line = stdin.ReadLine()) is not null) stdout.WriteLine(line);
            return 0;
        }
        int code = 0;
        foreach (var a in args)
        {
            var abs = interp.Context.Vfs.Normalize(a);
            if (interp.Context.Vfs.Resolve(abs) is VfsFile f)
            {
                stdout.Write(f.ReadText());
                if (!f.ReadText().EndsWith('\n')) stdout.WriteLine();
            }
            else
            {
                stderr.WriteLine($"cat: {abs}: No such file or directory");
                code = 1;
            }
        }
        return code;
    }

    public static int Cp(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool recursive = false;
        var paths = new List<string>();
        foreach (var a in args)
        {
            if (a == "-r" || a == "-R") recursive = true;
            else paths.Add(a);
        }
        if (paths.Count < 2) { stderr.WriteLine("cp: missing operand"); return 1; }
        var dst = paths[^1];
        for (int i = 0; i < paths.Count - 1; i++)
            interp.Context.Vfs.Copy(paths[i], dst, recursive);
        return 0;
    }

    public static int Mv(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count < 2) { stderr.WriteLine("mv: missing operand"); return 1; }
        var dst = args[^1];
        for (int i = 0; i < args.Count - 1; i++)
            interp.Context.Vfs.Move(args[i], dst);
        return 0;
    }

    public static int Rm(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool recursive = false, force = false;
        var paths = new List<string>();
        foreach (var a in args)
        {
            if (a == "-r" || a == "-R") recursive = true;
            else if (a == "-f") force = true;
            else if (a == "-rf" || a == "-fr") { recursive = true; force = true; }
            else paths.Add(a);
        }
        foreach (var p in paths) interp.Context.Vfs.Delete(p, recursive, force);
        return 0;
    }

    public static int Mkdir(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool parents = args.Any(a => a == "-p");
        foreach (var p in args.Where(a => !a.StartsWith('-')))
        {
            if (parents) interp.Context.Vfs.GetOrCreateDirectory(p);
            else interp.Context.Vfs.CreateDirectory(p);
        }
        return 0;
    }

    public static int Rmdir(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var a in args) interp.Context.Vfs.Delete(a, recursive: false, force: false);
        return 0;
    }

    public static int Touch(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var p in args)
        {
            var abs = interp.Context.Vfs.Normalize(p);
            if (interp.Context.Vfs.Resolve(abs) is VfsFile existing)
            {
                existing.LastWriteTimeUtc = DateTime.UtcNow;
            }
            else
            {
                interp.Context.Vfs.CreateTextFile(abs, "", overwrite: false);
            }
        }
        return 0;
    }

    public static int Head(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        int? n = 10;
        int? bytes = null;
        var files = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "-n" && i + 1 < args.Count) { n = int.Parse(args[i + 1], CultureInfo.InvariantCulture); bytes = null; i++; }
            else if (args[i] == "-c" && i + 1 < args.Count) { bytes = int.Parse(args[i + 1], CultureInfo.InvariantCulture); n = null; i++; }
            else if (args[i].StartsWith("-") && int.TryParse(args[i][1..], out var v)) { n = v; bytes = null; }
            else files.Add(args[i]);
        }

        void EmitText(string text)
        {
            if (bytes is int byteCount)
            {
                var rendered = text[..Math.Min(byteCount, text.Length)];
                stdout.Write(rendered);
                if (!rendered.EndsWith('\n')) stdout.WriteLine();
                return;
            }
            using var reader = new StringReader(text);
            for (int i = 0; i < (n ?? 10); i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                stdout.WriteLine(line);
            }
        }
        if (files.Count == 0) { EmitText(ReadAllText(stdin)); return 0; }
        foreach (var f in files)
        {
            var abs = interp.Context.Vfs.Normalize(f);
            if (interp.Context.Vfs.Resolve(abs) is VfsFile vf) EmitText(vf.ReadText());
        }
        return 0;
    }

    public static int Tail(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        int? n = 10;
        int? bytes = null;
        var files = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "-n" && i + 1 < args.Count) { n = int.Parse(args[i + 1], CultureInfo.InvariantCulture); bytes = null; i++; }
            else if (args[i] == "-c" && i + 1 < args.Count) { bytes = int.Parse(args[i + 1], CultureInfo.InvariantCulture); n = null; i++; }
            else if (args[i].StartsWith("-") && int.TryParse(args[i][1..], out var v)) { n = v; bytes = null; }
            else files.Add(args[i]);
        }

        void EmitText(string text)
        {
            if (bytes is int byteCount)
            {
                var rendered = text[Math.Max(0, text.Length - byteCount)..];
                stdout.Write(rendered);
                if (!rendered.EndsWith('\n')) stdout.WriteLine();
                return;
            }
            var lines = SplitLines(text);
            foreach (var line in lines.Skip(Math.Max(0, lines.Count - (n ?? 10)))) stdout.WriteLine(line);
        }
        if (files.Count == 0) { EmitText(ReadAllText(stdin)); return 0; }
        foreach (var f in files)
        {
            var abs = interp.Context.Vfs.Normalize(f);
            if (interp.Context.Vfs.Resolve(abs) is VfsFile vf) EmitText(vf.ReadText());
        }
        return 0;
    }

    public static int Wc(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool linesOnly = args.Contains("-l");
        bool wordsOnly = args.Contains("-w");
        bool charsOnly = args.Contains("-c");
        bool unicodeCharsOnly = args.Contains("-m");
        bool longest = args.Contains("-L");
        var files = args.Where(a => !a.StartsWith('-')).ToList();
        int totalLines = 0, totalWords = 0, totalChars = 0;
        void Count(TextReader r, string label)
        {
            var text = ReadAllText(r);
            var splitLines = SplitLines(text);
            int lines = splitLines.Count;
            int words = splitLines.Sum(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            int chars = text.Length;
            int bytes = Encoding.UTF8.GetByteCount(text);
            int longestLine = splitLines.Count == 0 ? 0 : splitLines.Max(line => line.Length);
            if (linesOnly) stdout.WriteLine($"{lines,7} {label}".TrimEnd());
            else if (wordsOnly) stdout.WriteLine($"{words,7} {label}".TrimEnd());
            else if (charsOnly) stdout.WriteLine($"{bytes,7} {label}".TrimEnd());
            else if (unicodeCharsOnly) stdout.WriteLine($"{chars,7} {label}".TrimEnd());
            else if (longest) stdout.WriteLine($"{longestLine,7} {label}".TrimEnd());
            else stdout.WriteLine($"{lines,7} {words,7} {chars,7} {label}".TrimEnd());
            totalLines += lines; totalWords += words; totalChars += chars;
        }
        if (files.Count == 0) { Count(stdin, ""); return 0; }
        foreach (var f in files)
        {
            var abs = interp.Context.Vfs.Normalize(f);
            if (interp.Context.Vfs.Resolve(abs) is VfsFile vf) Count(new StringReader(vf.ReadText()), f);
        }
        return 0;
    }

    public static int Grep(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool caseInsensitive = false;
        bool invert = false;
        bool withLineNumbers = false;
        bool countOnly = false;
        bool listFiles = false;
        bool recursive = false;
        bool fixedStrings = false;
        string? pattern = null;
        var files = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "-i") { caseInsensitive = true; continue; }
            if (a == "-v") { invert = true; continue; }
            if (a == "-n") { withLineNumbers = true; continue; }
            if (a == "-c") { countOnly = true; continue; }
            if (a == "-l") { listFiles = true; continue; }
            if (a == "-r" || a == "-R") { recursive = true; continue; }
            if (a == "-F") { fixedStrings = true; continue; }
            if (a == "-e" && i + 1 < args.Count) { pattern = args[++i]; continue; }
            if (a.StartsWith('-') && a.Length > 1) continue;
            if (pattern is null) { pattern = a; continue; }
            files.Add(a);
        }
        if (pattern is null) { stderr.WriteLine("grep: usage: grep PATTERN [FILE...]"); return 2; }
        var opts = caseInsensitive ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None;
        var rx = fixedStrings ? null : new System.Text.RegularExpressions.Regex(pattern, opts);
        IEnumerable<(string Label, string Text)> sources;
        if (recursive)
        {
            sources = files.Count == 0
                ? interp.Context.Vfs.List(interp.Context.Vfs.CurrentLocation, recursive: true, filter: null, filesOnly: true)
                    .OfType<VfsFile>()
                    .Select(file => (file.AbsolutePath, file.ReadText()))
                : files.SelectMany(root =>
                    interp.Context.Vfs.List(root, recursive: true, filter: null, filesOnly: true)
                        .OfType<VfsFile>()
                        .Select(file => (file.AbsolutePath, file.ReadText())));
        }
        else if (files.Count == 0)
        {
            sources = new[] { ("", ReadAllText(stdin)) };
        }
        else
        {
            sources = files.Select(file =>
            {
                var abs = interp.Context.Vfs.Normalize(file);
                var vf = interp.Context.Vfs.Resolve(abs) as VfsFile;
                return (file, vf?.ReadText() ?? "");
            });
        }

        int code = 1;
        foreach (var (label, text) in sources)
        {
            if (text.Length == 0 && label.Length > 0 && !interp.Context.Vfs.Exists(label))
            {
                stderr.WriteLine($"grep: {label}: No such file or directory");
                continue;
            }
            int matches = 0;
            var lines = SplitLines(text);
            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                bool match = fixedStrings
                    ? line.Contains(pattern, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    : rx!.IsMatch(line);
                if (match ^ invert)
                {
                    matches++;
                    if (!countOnly && !listFiles)
                    {
                        var prefix = files.Count > 1 || recursive ? label + ":" : "";
                        if (withLineNumbers) prefix += (lineIndex + 1).ToString(CultureInfo.InvariantCulture) + ":";
                        stdout.WriteLine(prefix + line);
                    }
                }
            }
            if (matches == 0) continue;
            code = 0;
            if (listFiles) stdout.WriteLine(label);
            else if (countOnly) stdout.WriteLine(matches.ToString(CultureInfo.InvariantCulture));
        }
        return code;
    }

    public static int Sort(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool reverse = args.Contains("-r");
        bool numeric = args.Contains("-n");
        bool unique = args.Contains("-u");
        string? fieldSeparator = null;
        int? keyField = null;
        var files = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "-t" && i + 1 < args.Count) { fieldSeparator = args[++i]; continue; }
            if (args[i] == "-k" && i + 1 < args.Count) { keyField = int.Parse(args[++i].Split(',')[0], CultureInfo.InvariantCulture); continue; }
            if (args[i].StartsWith('-')) continue;
            files.Add(args[i]);
        }

        var lines = new List<string>();
        if (files.Count == 0) lines.AddRange(SplitLines(ReadAllText(stdin)));
        else
            foreach (var file in files)
                if (interp.Context.Vfs.Resolve(interp.Context.Vfs.Normalize(file)) is VfsFile vf)
                    lines.AddRange(SplitLines(vf.ReadText()));

        Func<string, string> keySelector = line =>
        {
            if (!string.IsNullOrEmpty(fieldSeparator) && keyField is int field)
            {
                var parts = line.Split(fieldSeparator);
                return field >= 1 && field <= parts.Length ? parts[field - 1] : "";
            }
            return line;
        };

        if (numeric) lines = lines.OrderBy(s => decimal.TryParse(keySelector(s).Trim(), out var v) ? v : 0m).ToList();
        else lines = lines.OrderBy(keySelector, StringComparer.Ordinal).ToList();
        if (unique) lines = lines.Distinct(StringComparer.Ordinal).ToList();
        if (reverse) lines.Reverse();
        foreach (var ln in lines) stdout.WriteLine(ln);
        return 0;
    }

    public static int Uniq(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool counts = args.Contains("-c");
        bool duplicatesOnly = args.Contains("-d");
        bool uniqueOnly = args.Contains("-u");
        string? last = null;
        int count = 0;
        void Flush()
        {
            if (last is null) return;
            if (duplicatesOnly && count < 2) return;
            if (uniqueOnly && count != 1) return;
            if (counts) stdout.WriteLine($"      {count} {last}");
            else stdout.WriteLine(last);
        }
        foreach (var l in SplitLines(ReadAllText(stdin)))
        {
            if (last == l) count++;
            else { Flush(); last = l; count = 1; }
        }
        Flush();
        return 0;
    }

    public static int Tr(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool delete = args.Contains("-d");
        bool squeeze = args.Contains("-s");
        bool complement = args.Contains("-c");
        var sets = args.Where(a => !a.StartsWith('-')).ToArray();
        if ((!delete && sets.Length < 2) || (delete && sets.Length < 1)) { stderr.WriteLine("tr: usage: tr SET1 SET2"); return 2; }
        var set1 = ExpandCharacterSet(sets[0]);
        var set2 = sets.Length > 1 ? ExpandCharacterSet(sets[1]) : "";
        char? previous = null;
        foreach (var ch in ReadAllText(stdin))
        {
            bool inSet = set1.Contains(ch, StringComparison.Ordinal);
            if (complement) inSet = !inSet;
            if (delete && inSet) continue;
            char mapped = ch;
            if (!delete && set2.Length > 0 && inSet)
            {
                int idx = set1.IndexOf(ch);
                if (idx >= 0) mapped = set2[Math.Min(idx, set2.Length - 1)];
            }
            if (squeeze && previous == mapped) continue;
            stdout.Write(mapped);
            previous = mapped;
        }
        return 0;
    }

    public static int Export(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                interp.Context.Env.Set(a.Substring(0, eq), a.Substring(eq + 1));
            }
            // bash's "export NAME" is a no-op for our shared-env model; we don't track export-ness separately.
        }
        return 0;
    }

    public static int Unset(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var a in args) interp.Context.Env.Unset(a);
        return 0;
    }

    public static int Env(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var kv in interp.Context.Env.All.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            stdout.WriteLine($"{kv.Key}={kv.Value}");
        return 0;
    }

    public static int Read(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var names = args.Where(a => !a.StartsWith('-')).ToList();
        var line = stdin.ReadLine();
        if (line is null) return 1;
        if (names.Count == 0) { interp.Context.Env.Set("REPLY", line); return 0; }
        var parts = line.Split((char[]?)null, names.Count, StringSplitOptions.None);
        for (int i = 0; i < names.Count; i++)
            interp.Context.Env.Set(names[i], i < parts.Length ? parts[i] : "");
        return 0;
    }

    public static int Test(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        return BashTest.Evaluate(args, interp) ? 0 : 1;
    }

    public static int TestSquare(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var trimmed = args.ToList();
        if (trimmed.Count > 0 && (trimmed[^1] == "]" || trimmed[^1] == "]]"))
            trimmed.RemoveAt(trimmed.Count - 1);
        return BashTest.Evaluate(trimmed, interp) ? 0 : 1;
    }

    public static int True(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) => 0;
    public static int False(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) => 1;

    public static int Exit(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var code = args.Count == 0 ? interp.LastExitCode : ParseIntOr(args[0], 0);
        throw new BashExitException(code);
    }

    public static int Return(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var code = args.Count == 0 ? interp.LastExitCode : ParseIntOr(args[0], 0);
        throw new BashReturnException(code);
    }

    public static int Break(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var n = args.Count == 0 ? 1 : ParseIntOr(args[0], 1);
        throw new BashBreakException(n);
    }

    public static int Continue(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var n = args.Count == 0 ? 1 : ParseIntOr(args[0], 1);
        throw new BashContinueException(n);
    }

    public static int Shift(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var n = args.Count == 0 ? 1 : ParseIntOr(args[0], 1);
        if (interp.Positional.Count <= 1) return 1;
        for (int i = 0; i < n && interp.Positional.Count > 1; i++)
            interp.Positional.RemoveAt(1);
        return 0;
    }

    public static int Source(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0) { stderr.WriteLine("source: filename argument required"); return 2; }
        var abs = interp.Context.Vfs.Normalize(args[0]);
        var file = interp.Context.Vfs.Resolve(abs) as VfsFile;
        if (file is null) { stderr.WriteLine($"source: {abs}: No such file"); return 1; }
        var script = CarbideBash.Parser.BashParser.ParseString(file.ReadText());
        foreach (var s in script.Statements) interp.ExecuteStatement(s, stdin, stdout, stderr);
        return interp.LastExitCode;
    }

    public static int Eval(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var src = string.Join(' ', args);
        var script = CarbideBash.Parser.BashParser.ParseString(src);
        foreach (var s in script.Statements) interp.ExecuteStatement(s, stdin, stdout, stderr);
        return interp.LastExitCode;
    }

    public static int Type(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var a in args)
        {
            if (interp.Functions.ContainsKey(a)) stdout.WriteLine($"{a} is a function");
            else if (BuiltinRegistry.TryGet(a) is not null) stdout.WriteLine($"{a} is a shell builtin");
            else if (interp.Aliases.TryGetValue(a, out var t)) stdout.WriteLine($"{a} is aliased to `{t}'");
            else stdout.WriteLine($"{a}: not found");
        }
        return 0;
    }

    public static int Alias(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0)
        {
            foreach (var kv in interp.Aliases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                stdout.WriteLine($"alias {kv.Key}='{kv.Value}'");
            return 0;
        }
        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                var name = a.Substring(0, eq);
                var val = a.Substring(eq + 1);
                if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'') val = val.Substring(1, val.Length - 2);
                interp.Aliases[name] = val;
            }
        }
        return 0;
    }

    public static int Declare(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var a in args)
        {
            if (a.StartsWith('-')) continue;
            var eq = a.IndexOf('=');
            if (eq > 0) interp.Context.Env.Set(a.Substring(0, eq), a.Substring(eq + 1));
        }
        return 0;
    }

    public static int Clear(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        stdout.Write("\x1b[2J\x1b[H");
        return 0;
    }

    public static int SetBuiltin(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        // Phase 1: honor -e / +e as a no-op (flag recorded but untracked), and replace
        // positional parameters if non-flag args are given.
        var positional = args.Where(a => !(a.StartsWith('-') || a.StartsWith('+'))).ToList();
        if (positional.Count == 0 && !args.Any(a => a.StartsWith('-') || a.StartsWith('+')))
        {
            foreach (var kv in interp.Context.Env.All.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                stdout.WriteLine($"{kv.Key}={kv.Value}");
            return 0;
        }
        if (positional.Count > 0)
        {
            var name = interp.Positional.Count > 0 ? interp.Positional[0] : "bash";
            interp.Positional = new List<string> { name };
            interp.Positional.AddRange(positional);
        }
        return 0;
    }

    private static int ParseIntOr(string s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string ReadAllText(TextReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

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
}
