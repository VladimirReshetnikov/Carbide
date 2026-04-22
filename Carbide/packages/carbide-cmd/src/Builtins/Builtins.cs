using System.Globalization;
using System.Text;
using CarbideCmd.Runtime;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Builtins;

/// <summary>
/// Implementations of the Phase 1 cmd built-in catalog. Each static method matches the
/// <see cref="CmdBuiltin"/> delegate; <see cref="BuiltinRegistry"/> binds them by name.
/// </summary>
public static class Builtins
{
    public static int Echo(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0)
        {
            stdout.WriteLine($"ECHO is {(interp.EchoOn ? "on" : "off")}.");
            return 0;
        }
        if (args.Count == 1)
        {
            var arg = args[0];
            if (arg.Equals("ON", StringComparison.OrdinalIgnoreCase)) { interp.EchoOn = true; return 0; }
            if (arg.Equals("OFF", StringComparison.OrdinalIgnoreCase)) { interp.EchoOn = false; return 0; }
            if (arg == ".")
            {
                stdout.WriteLine();
                return 0;
            }
        }
        stdout.WriteLine(string.Join(' ', args.Select(StripOuterQuotes)));
        return 0;
    }

    public static int Set(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0)
        {
            foreach (var kv in interp.Context.Env.All.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                stdout.WriteLine($"{kv.Key}={kv.Value}");
            return 0;
        }
        // SET /A expression — Phase 1 evaluates a tiny integer calculator.
        if (args[0].Equals("/A", StringComparison.OrdinalIgnoreCase))
        {
            var expr = string.Join(' ', args.Skip(1));
            var eqIdx = expr.IndexOf('=');
            string? target = null;
            string exprSrc = expr;
            if (eqIdx > 0)
            {
                target = expr.Substring(0, eqIdx).Trim();
                exprSrc = expr.Substring(eqIdx + 1);
            }
            var value = IntExpression.Evaluate(exprSrc, interp.Context.Env);
            if (target is not null) interp.Context.Env.Set(target, value.ToString(CultureInfo.InvariantCulture));
            stdout.WriteLine(value);
            return 0;
        }

        var combined = string.Join(' ', args);
        var eq = combined.IndexOf('=');
        if (eq < 0)
        {
            var prefix = combined;
            bool found = false;
            foreach (var kv in interp.Context.Env.All.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    stdout.WriteLine($"{kv.Key}={kv.Value}");
                    found = true;
                }
            }
            return found ? 0 : 1;
        }

        var name = combined.Substring(0, eq).Trim();
        var val = combined.Substring(eq + 1);
        if (val.Length == 0)
        {
            interp.Context.Env.Set(name, null);
        }
        else
        {
            interp.Context.Env.Set(name, val);
        }
        return 0;
    }

    public static int ChangeDir(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0)
        {
            stdout.WriteLine(interp.Context.Vfs.CurrentLocation);
            return 0;
        }
        var target = StripOuterQuotes(args[0]);
        interp.Context.Vfs.SetLocation(target);
        return 0;
    }

    public static int Dir(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        // Phase 1 DIR recognizes a small fixed flag set; any other `/`-prefixed word is a
        // VFS path. That disambiguates the real flags (/B, /S, etc., always single-letter
        // after the slash) from VFS-absolute paths like `/work`.
        static bool IsCmdFlag(string a) =>
            a.Length == 2 && a[0] == '/' && char.IsLetter(a[1]);
        var path = args.FirstOrDefault(a => !IsCmdFlag(a)) ?? interp.Context.Vfs.CurrentLocation;
        path = StripOuterQuotes(path);
        bool bare = args.Any(a => a.Equals("/B", StringComparison.OrdinalIgnoreCase));
        var nodes = interp.Context.Vfs.List(path, recursive: false, filter: null).ToList();
        if (bare)
        {
            foreach (var n in nodes) stdout.WriteLine(n.Name);
            return 0;
        }
        var abs = interp.Context.Vfs.Normalize(path);
        stdout.WriteLine();
        stdout.WriteLine($" Directory of {abs}");
        stdout.WriteLine();
        int fileCount = 0, dirCount = 0;
        long totalBytes = 0;
        foreach (var n in nodes)
        {
            var date = n.LastWriteTimeUtc.ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture);
            if (n is VfsFile f)
            {
                stdout.WriteLine($"{date}         {f.Length,14}  {n.Name}");
                fileCount++;
                totalBytes += f.Length;
            }
            else
            {
                stdout.WriteLine($"{date}    <DIR>          {n.Name}");
                dirCount++;
            }
        }
        stdout.WriteLine($"               {fileCount} File(s)  {totalBytes} bytes");
        stdout.WriteLine($"               {dirCount} Dir(s)");
        return 0;
    }

    public static int MakeDir(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0) { stderr.WriteLine("The syntax of the command is incorrect."); return 1; }
        foreach (var p in args)
        {
            interp.Context.Vfs.CreateDirectory(StripOuterQuotes(p));
        }
        return 0;
    }

    public static int RemoveDir(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool recursive = false, quiet = false;
        var paths = new List<string>();
        foreach (var a in args)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)) recursive = true;
            else if (a.Equals("/Q", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else paths.Add(StripOuterQuotes(a));
        }
        foreach (var p in paths) interp.Context.Vfs.Delete(p, recursive, force: quiet);
        return 0;
    }

    public static int Delete(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool quiet = false;
        var paths = new List<string>();
        foreach (var a in args)
        {
            if (a.Equals("/Q", StringComparison.OrdinalIgnoreCase) || a.Equals("/F", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else paths.Add(StripOuterQuotes(a));
        }
        foreach (var p in paths) interp.Context.Vfs.Delete(p, recursive: false, force: quiet);
        return 0;
    }

    public static int Rename(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count != 2) { stderr.WriteLine("The syntax of the command is incorrect."); return 1; }
        interp.Context.Vfs.Move(StripOuterQuotes(args[0]), StripOuterQuotes(args[1]));
        return 0;
    }

    public static int Copy(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count < 2) { stderr.WriteLine("The syntax of the command is incorrect."); return 1; }
        interp.Context.Vfs.Copy(StripOuterQuotes(args[0]), StripOuterQuotes(args[1]), recursive: false);
        stdout.WriteLine("        1 file(s) copied.");
        return 0;
    }

    public static int Move(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count < 2) { stderr.WriteLine("The syntax of the command is incorrect."); return 1; }
        interp.Context.Vfs.Move(StripOuterQuotes(args[0]), StripOuterQuotes(args[1]));
        return 0;
    }

    public static int Type(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0) { stderr.WriteLine("The syntax of the command is incorrect."); return 1; }
        int last = 0;
        foreach (var a in args)
        {
            var path = StripOuterQuotes(a);
            var abs = interp.Context.Vfs.Normalize(path);
            var file = interp.Context.Vfs.Resolve(abs) as VfsFile;
            if (file is null)
            {
                stderr.WriteLine($"The system cannot find the file specified - {abs}");
                last = 1;
                continue;
            }
            stdout.Write(file.ReadText());
            if (!file.ReadText().EndsWith('\n')) stdout.WriteLine();
        }
        return last;
    }

    public static int Cls(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        stdout.Write("\x1b[2J\x1b[H");
        return 0;
    }

    public static int Pause(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        stdout.WriteLine("Press any key to continue . . .");
        stdin.ReadLine();
        return 0;
    }

    public static int Ver(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        stdout.WriteLine();
        stdout.WriteLine("Carbide Cmd [Version 1.0]");
        return 0;
    }

    public static int Title(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var title = string.Join(' ', args.Select(StripOuterQuotes));
        stdout.Write($"\x1b]0;{title}\x07");
        return 0;
    }

    public static int Color(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        // Phase 1: minimal ANSI reset. Real cmd maps a 2-hex-digit FG/BG to console colors;
        // our xterm backend already defaults sensibly, so we reset rather than translate.
        stdout.Write("\x1b[0m");
        return 0;
    }

    public static int Find(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool caseInsensitive = false;
        bool invert = false;
        string? pattern = null;
        var files = new List<string>();
        foreach (var a in args)
        {
            if (a.Equals("/I", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; continue; }
            if (a.Equals("/V", StringComparison.OrdinalIgnoreCase)) { invert = true; continue; }
            if (pattern is null && a.StartsWith('"')) { pattern = StripOuterQuotes(a); continue; }
            if (pattern is null) { pattern = a; continue; }
            files.Add(StripOuterQuotes(a));
        }
        if (pattern is null) { stderr.WriteLine("FIND: Parameter format not correct"); return 2; }
        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (files.Count == 0)
        {
            string? line;
            while ((line = stdin.ReadLine()) is not null)
            {
                if ((line.Contains(pattern, cmp)) ^ invert) stdout.WriteLine(line);
            }
            return 0;
        }
        foreach (var f in files)
        {
            var abs = interp.Context.Vfs.Normalize(f);
            var file = interp.Context.Vfs.Resolve(abs) as VfsFile;
            if (file is null) { stderr.WriteLine($"FIND: File not found - {abs}"); continue; }
            stdout.WriteLine($"---------- {abs}");
            foreach (var l in file.ReadText().Split('\n'))
            {
                if ((l.Contains(pattern, cmp)) ^ invert) stdout.WriteLine(l);
            }
        }
        return 0;
    }

    public static int FindStr(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
        // Phase 1: delegate to FIND's substring behavior. /R regex support is Phase 2.
        => Find(interp, args, stdin, stdout, stderr);

    public static int Sort(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool reverse = args.Any(a => a.Equals("/R", StringComparison.OrdinalIgnoreCase));
        var lines = new List<string>();
        string? l;
        while ((l = stdin.ReadLine()) is not null) lines.Add(l);
        lines.Sort(StringComparer.OrdinalIgnoreCase);
        if (reverse) lines.Reverse();
        foreach (var ln in lines) stdout.WriteLine(ln);
        return 0;
    }

    public static int More(Interpreter interp, IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string? line;
        while ((line = stdin.ReadLine()) is not null) stdout.WriteLine(line);
        return 0;
    }

    public static string StripOuterQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        return s;
    }
}
