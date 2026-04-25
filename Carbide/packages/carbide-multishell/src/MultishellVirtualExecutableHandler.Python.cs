using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private const string PythonVersion = "Python 3-compatible Carbide subset";
    private const string PythonDetailedVersion = "Python 3-compatible Carbide subset (CarbidePython 0.1)";

    private static int ExecutePython(VirtualExecutableInvocation invocation)
        => new PythonCommand(invocation).Execute();

    private sealed class PythonCommand
    {
        private readonly VirtualExecutableInvocation _invocation;

        public PythonCommand(VirtualExecutableInvocation invocation)
        {
            _invocation = invocation;
        }

        public int Execute()
        {
            var parse = PythonCommandLine.Parse(_invocation.Args, _invocation.Env);
            if (parse.Error is not null)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {parse.Error}");
                return 2;
            }

            if (parse.ShowHelp)
            {
                WriteHelp(parse.HelpKind);
                return 0;
            }

            if (parse.ShowVersion)
            {
                _invocation.Output.WriteLine(parse.ShowDetailedVersion ? PythonDetailedVersion : PythonVersion);
                return 0;
            }

            var runtime = new PythonRuntime(_invocation, parse);
            try
            {
                return runtime.Run();
            }
            catch (PythonSystemExit ex)
            {
                return ex.ExitCode;
            }
            catch (PythonSyntaxException ex)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {ex.Message}");
                return 1;
            }
            catch (PythonException ex)
            {
                _invocation.Error.WriteLine($"{ex.Name}: {ex.Message}");
                return 1;
            }
            catch (VfsException ex)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {ex.Message}");
                return 1;
            }
        }

        private void WriteHelp(string? helpKind)
        {
            _invocation.Output.WriteLine("usage: python [option] ... [-c cmd | -m module | file | -] [arg] ...");
            _invocation.Output.WriteLine("Carbide Python is a sandboxed Python 3-compatible subset.");
            _invocation.Output.WriteLine();
            _invocation.Output.WriteLine("Supported options:");
            _invocation.Output.WriteLine("  -c cmd       program passed in as string");
            _invocation.Output.WriteLine("  -m module    run an allow-listed module as __main__");
            _invocation.Output.WriteLine("  -i           inspect interactively after running source");
            _invocation.Output.WriteLine("  -q           suppress the interactive startup banner");
            _invocation.Output.WriteLine("  -V, --version, -VV");
            _invocation.Output.WriteLine("  -B -E -I -O -OO -P -S -s -u -v -W arg -x");
            if (helpKind is "--help-env" or "--help-all")
            {
                _invocation.Output.WriteLine();
                _invocation.Output.WriteLine("Recognized environment variables:");
                _invocation.Output.WriteLine("  PYTHONPATH PYTHONINSPECT PYTHONSTARTUP PYTHONUNBUFFERED");
                _invocation.Output.WriteLine("  PYTHONDONTWRITEBYTECODE PYTHONSAFEPATH PYTHONUTF8");
            }
            if (helpKind == "--help-all")
            {
                _invocation.Output.WriteLine();
                _invocation.Output.WriteLine("Unsupported: native extensions, host filesystem/process access, sockets,");
                _invocation.Output.WriteLine("async syntax, pattern matching, pyc caches, pip, venv, and arbitrary imports.");
            }
        }
    }

    private sealed record PythonCommandLine(
        string? SourceKind,
        string? Source,
        IReadOnlyList<string> ProgramArgs,
        bool Inspect,
        bool Quiet,
        bool Isolated,
        bool IgnoreEnvironment,
        bool SafePath,
        bool Optimize,
        bool ShowVersion,
        bool ShowDetailedVersion,
        bool ShowHelp,
        string? HelpKind,
        string? Error)
    {
        public static PythonCommandLine Parse(IReadOnlyList<string> args, CarbideShellCore.Env.EnvVarStore env)
        {
            string? sourceKind = null;
            string? source = null;
            var programArgs = new List<string>();
            bool inspect = false;
            bool quiet = false;
            bool isolated = false;
            bool ignoreEnvironment = false;
            bool safePath = false;
            bool optimize = false;
            bool showVersion = false;
            bool showDetailedVersion = false;
            bool showHelp = false;
            string? helpKind = null;

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (sourceKind is not null)
                {
                    programArgs.Add(arg);
                    continue;
                }

                if (arg == "--")
                {
                    if (i + 1 < args.Count)
                    {
                        sourceKind = "file";
                        source = args[++i];
                        for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                    }
                    break;
                }

                if (arg == "-")
                {
                    sourceKind = "stdin";
                    source = "-";
                    for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                    break;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal) || arg == "-")
                {
                    sourceKind = "file";
                    source = arg;
                    for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                    break;
                }

                switch (arg)
                {
                    case "-c":
                        if (i + 1 >= args.Count) return ErrorResult("argument expected for -c");
                        sourceKind = "command";
                        source = args[++i];
                        for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                        break;
                    case "-m":
                        if (i + 1 >= args.Count) return ErrorResult("argument expected for -m");
                        sourceKind = "module";
                        source = args[++i];
                        for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                        break;
                    case "-h":
                    case "-?":
                    case "--help":
                    case "--help-env":
                    case "--help-all":
                        showHelp = true;
                        helpKind = arg;
                        break;
                    case "-V":
                    case "--version":
                        showVersion = true;
                        break;
                    case "-VV":
                        showVersion = true;
                        showDetailedVersion = true;
                        break;
                    case "-i":
                        inspect = true;
                        break;
                    case "-q":
                        quiet = true;
                        break;
                    case "-E":
                        ignoreEnvironment = true;
                        break;
                    case "-I":
                        isolated = true;
                        ignoreEnvironment = true;
                        safePath = true;
                        break;
                    case "-P":
                        safePath = true;
                        break;
                    case "-O":
                    case "-OO":
                        optimize = true;
                        break;
                    case "-B":
                    case "-S":
                    case "-s":
                    case "-u":
                    case "-v":
                    case "-x":
                        break;
                    case "-W":
                        if (i + 1 >= args.Count) return ErrorResult("argument expected for -W");
                        i++;
                        break;
                    default:
                        if (arg.StartsWith("-W", StringComparison.Ordinal) && arg.Length > 2)
                            break;
                        if (arg.StartsWith("-X", StringComparison.Ordinal))
                            return ErrorResult($"unsupported option: {arg}");
                        if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 2 && IsShortOptionCluster(arg))
                        {
                            foreach (var ch in arg.AsSpan(1))
                            {
                                switch (ch)
                                {
                                    case 'i': inspect = true; break;
                                    case 'q': quiet = true; break;
                                    case 'B':
                                    case 'S':
                                    case 's':
                                    case 'u':
                                    case 'v':
                                    case 'O':
                                        if (ch == 'O') optimize = true;
                                        break;
                                    default:
                                        return ErrorResult($"unsupported option: -{ch}");
                                }
                            }
                            break;
                        }
                        return ErrorResult($"unsupported option: {arg}");
                }
            }

            if (!ignoreEnvironment && !isolated)
            {
                if (env.Get("PYTHONINSPECT") is { Length: > 0 })
                    inspect = true;
                if (env.Get("PYTHONSAFEPATH") is { Length: > 0 })
                    safePath = true;
            }

            return new PythonCommandLine(
                sourceKind,
                source,
                programArgs,
                inspect,
                quiet,
                isolated,
                ignoreEnvironment,
                safePath,
                optimize,
                showVersion,
                showDetailedVersion,
                showHelp,
                helpKind,
                null);
        }

        private static bool IsShortOptionCluster(string arg)
            => arg.Skip(1).All(ch => ch is 'i' or 'q' or 'B' or 'S' or 's' or 'u' or 'v' or 'O');

        private static PythonCommandLine ErrorResult(string error)
            => new(null, null, Array.Empty<string>(), false, false, false, false, false, false, false, false, false, null, error);
    }

    private sealed class PythonRuntime
    {
        private readonly VirtualExecutableInvocation _invocation;
        private readonly PythonCommandLine _options;
        private readonly PythonContext _context;

        public PythonRuntime(VirtualExecutableInvocation invocation, PythonCommandLine options)
        {
            _invocation = invocation;
            _options = options;
            _context = new PythonContext(invocation, options);
        }

        public int Run()
        {
            if (_options.SourceKind is null)
                return RunInteractive(runStartup: true);

            var code = _options.SourceKind switch
            {
                "command" => ExecuteSource(_options.Source ?? "", "<string>", interactiveEcho: false),
                "stdin" => ExecuteSource(ReadAllText(_invocation.Input), "<stdin>", interactiveEcho: false),
                "file" => ExecuteFile(_options.Source ?? ""),
                "module" => ExecuteModule(_options.Source ?? ""),
                _ => 0,
            };

            if (code == 0 && _options.Inspect)
                return RunInteractive(runStartup: false);

            return code;
        }

        private int ExecuteFile(string path)
        {
            var abs = _invocation.Vfs.Normalize(path);
            if (_invocation.Vfs.Resolve(abs) is not VfsFile file)
                throw new PythonException("FileNotFoundError", $"No such file: '{path}'");

            _context.SetMainFile(abs);
            return ExecuteSource(file.ReadText(), abs, interactiveEcho: false);
        }

        private int ExecuteModule(string moduleName)
        {
            if (moduleName == "json.tool")
            {
                var input = _options.ProgramArgs.Count > 0
                    ? ReadVfsText(_options.ProgramArgs[0])
                    : ReadAllText(_invocation.Input);
                var value = JsonSerializer.Deserialize<object?>(input);
                _invocation.Output.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            var module = _context.ImportModule(moduleName);
            if (module.Members.TryGetValue("__main__", out var main) && main is IPythonCallable callable)
                _ = callable.Invoke(_context, Array.Empty<object?>(), new Dictionary<string, object?>(StringComparer.Ordinal));
            return 0;
        }

        private string ReadVfsText(string path)
        {
            var abs = _invocation.Vfs.Normalize(path);
            if (_invocation.Vfs.Resolve(abs) is not VfsFile file)
                throw new PythonException("FileNotFoundError", $"No such file: '{path}'");
            return file.ReadText();
        }

        private int RunInteractive(bool runStartup)
        {
            if (!_options.Quiet)
                _invocation.Output.WriteLine($"{PythonDetailedVersion} on Carbide");

            if (runStartup && !_options.IgnoreEnvironment && !_options.Isolated)
            {
                var startup = _invocation.Env.Get("PYTHONSTARTUP");
                if (!string.IsNullOrWhiteSpace(startup))
                {
                    try { ExecuteFile(startup!); }
                    catch (PythonSystemExit) { throw; }
                    catch (Exception ex) { _invocation.Error.WriteLine($"PYTHONSTARTUP: {ex.Message}"); }
                }
            }

            var pending = new StringBuilder();
            while (true)
            {
                _invocation.Output.Write(pending.Length == 0 ? _context.Ps1 : _context.Ps2);
                _invocation.Output.Flush();
                var line = _invocation.Input.ReadLine();
                if (line is null)
                    return 0;

                if (pending.Length == 0 && IsInteractiveExit(line.Trim(), out var exitCode))
                    return exitCode;

                if (pending.Length > 0) pending.Append('\n');
                pending.Append(line);
                var source = pending.ToString();
                if (!IsCompleteInteractiveInput(source))
                    continue;

                try
                {
                    ExecuteSource(source, "<stdin>", interactiveEcho: true);
                }
                catch (PythonSystemExit ex)
                {
                    return ex.ExitCode;
                }
                catch (PythonSyntaxException ex)
                {
                    _invocation.Error.WriteLine($"SyntaxError: {ex.Message}");
                }
                catch (PythonException ex)
                {
                    _invocation.Error.WriteLine($"{ex.Name}: {ex.Message}");
                }
                pending.Clear();
            }
        }

        private int ExecuteSource(string source, string path, bool interactiveEcho)
        {
            var program = PythonProgram.Parse(source, path);
            if (interactiveEcho && program.IsSingleExpression)
            {
                var result = new PythonExpressionParser(program.SingleExpression!, _context).ParseExpression();
                if (result is not null)
                    _invocation.Output.WriteLine(PythonOps.Repr(result));
                return 0;
            }

            program.Execute(_context);
            return 0;
        }

        private static bool IsInteractiveExit(string text, out int code)
        {
            code = 0;
            if (text is "exit" or "quit" or "exit()" or "quit()")
                return true;
            var match = Regex.Match(text, @"^(?:exit|quit)\(([-+]?\d+)\)$");
            if (match.Success)
            {
                code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        private static bool IsCompleteInteractiveInput(string source)
        {
            var trimmed = source.TrimEnd();
            if (trimmed.Length == 0)
                return true;
            if (trimmed.EndsWith(":", StringComparison.Ordinal))
                return false;
            var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (lines.Length > 1)
            {
                var last = lines[^1];
                if (last.Trim().Length == 0)
                    return true;
                if (lines.Skip(1).Any(line => CountIndent(line) > 0))
                    return false;
            }

            var balance = 0;
            var inString = false;
            var quote = '\0';
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                if (inString)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == quote) inString = false;
                    continue;
                }
                if (ch is '\'' or '"') { inString = true; quote = ch; continue; }
                if (ch is '(' or '[' or '{') balance++;
                else if (ch is ')' or ']' or '}') balance--;
            }
            return balance <= 0;
        }
    }

    private sealed class PythonContext
    {
        private readonly VirtualExecutableInvocation _invocation;
        private readonly PythonCommandLine _options;
        private readonly Dictionary<string, object?> _builtins = new(StringComparer.Ordinal);
        private readonly Stack<Dictionary<string, object?>> _locals = new();
        private string? _mainFile;

        public Dictionary<string, object?> Globals { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PythonModule> Modules { get; } = new(StringComparer.Ordinal);

        public TextWriter Output => _invocation.Output;
        public TextWriter Error => _invocation.Error;
        public TextReader Input => _invocation.Input;
        public VirtualFileSystem Vfs => _invocation.Vfs;
        public VirtualExecutableInvocation Invocation => _invocation;
        public bool Optimize => _options.Optimize;

        public string Ps1
        {
            get => GetModule("sys").Members.TryGetValue("ps1", out var value) ? PythonOps.ToString(value) : ">>> ";
            set => GetModule("sys").Members["ps1"] = value;
        }

        public string Ps2
        {
            get => GetModule("sys").Members.TryGetValue("ps2", out var value) ? PythonOps.ToString(value) : "... ";
            set => GetModule("sys").Members["ps2"] = value;
        }

        public PythonContext(VirtualExecutableInvocation invocation, PythonCommandLine options)
        {
            _invocation = invocation;
            _options = options;
            RegisterBuiltins();
            RegisterModules();
            Globals["__name__"] = "__main__";
            Globals["__package__"] = null;
            Globals["__builtins__"] = _builtins;
        }

        public void SetMainFile(string path)
        {
            _mainFile = path;
            Globals["__file__"] = path;
        }

        public object? GetName(string name)
        {
            if (_locals.Count > 0 && _locals.Peek().TryGetValue(name, out var local))
                return local;
            if (Globals.TryGetValue(name, out var global))
                return global;
            if (_builtins.TryGetValue(name, out var builtin))
                return builtin;
            throw new PythonException("NameError", $"name '{name}' is not defined");
        }

        public void SetName(string name, object? value)
        {
            if (_locals.Count > 0)
                _locals.Peek()[name] = value;
            else
                Globals[name] = value;
        }

        public void PushLocals(Dictionary<string, object?> locals) => _locals.Push(locals);
        public void PopLocals() => _locals.Pop();

        public PythonModule GetModule(string name)
            => Modules.TryGetValue(name, out var module)
                ? module
                : throw new PythonException("ModuleNotFoundError", $"No module named '{name}'");

        public PythonModule ImportModule(string name)
        {
            if (Modules.TryGetValue(name, out var existing))
                return existing;

            var loaded = TryLoadVfsModule(name);
            if (loaded is not null)
                return loaded;

            throw new PythonException("ModuleNotFoundError", $"No module named '{name}'");
        }

        private PythonModule? TryLoadVfsModule(string name)
        {
            if (name.Contains('.', StringComparison.Ordinal))
                return null;

            var sysPath = GetModule("sys").Members["path"] as List<object?> ?? new List<object?>();
            foreach (var entry in sysPath.Select(PythonOps.ToString))
            {
                var candidate = VfsPath.Join(entry, name + ".py");
                var abs = Vfs.Normalize(candidate);
                if (Vfs.Resolve(abs) is not VfsFile file)
                    continue;

                var module = new PythonModule(name);
                module.Members["__name__"] = name;
                module.Members["__file__"] = abs;
                module.Members["__package__"] = null;
                Modules[name] = module;

                var oldGlobals = new Dictionary<string, object?>(Globals, StringComparer.Ordinal);
                try
                {
                    Globals.Clear();
                    foreach (var kv in module.Members) Globals[kv.Key] = kv.Value;
                    Globals["__builtins__"] = _builtins;
                    PythonProgram.Parse(file.ReadText(), abs).Execute(this);
                    module.Members.Clear();
                    foreach (var kv in Globals) module.Members[kv.Key] = kv.Value;
                    return module;
                }
                finally
                {
                    Globals.Clear();
                    foreach (var kv in oldGlobals) Globals[kv.Key] = kv.Value;
                }
            }
            return null;
        }

        private void RegisterBuiltins()
        {
            _builtins["None"] = null;
            _builtins["True"] = true;
            _builtins["False"] = false;
            _builtins["print"] = Native("print", (ctx, args, kwargs) =>
            {
                var sep = kwargs.TryGetValue("sep", out var sepValue) ? PythonOps.ToString(sepValue) : " ";
                var end = kwargs.TryGetValue("end", out var endValue) ? PythonOps.ToString(endValue) : "\n";
                ctx.Output.Write(string.Join(sep, args.Select(PythonOps.ToString)));
                ctx.Output.Write(end);
                return null;
            });
            _builtins["len"] = Native("len", (_, args, _) => PythonOps.Length(Arg(args, 0, "len")));
            _builtins["range"] = Native("range", (_, args, _) => PythonRange.FromArgs(args));
            _builtins["str"] = Native("str", (_, args, _) => args.Count == 0 ? "" : PythonOps.ToString(args[0]));
            _builtins["repr"] = Native("repr", (_, args, _) => PythonOps.Repr(Arg(args, 0, "repr")));
            _builtins["int"] = Native("int", (_, args, _) => args.Count == 0 ? 0L : PythonOps.ToLong(args[0]));
            _builtins["float"] = Native("float", (_, args, _) => args.Count == 0 ? 0d : PythonOps.ToDouble(args[0]));
            _builtins["bool"] = Native("bool", (_, args, _) => args.Count != 0 && PythonOps.IsTruthy(args[0]));
            _builtins["list"] = Native("list", (_, args, _) => args.Count == 0 ? new List<object?>() : PythonOps.Iterate(args[0]).ToList());
            _builtins["dict"] = Native("dict", (_, args, _) => args.Count == 0 ? new Dictionary<string, object?>(StringComparer.Ordinal) : PythonOps.ToDictionary(args[0]));
            _builtins["set"] = Native("set", (_, args, _) => new HashSet<object?>(args.Count == 0 ? [] : PythonOps.Iterate(args[0])));
            _builtins["tuple"] = Native("tuple", (_, args, _) => args.Count == 0 ? new PythonTuple([]) : new PythonTuple(PythonOps.Iterate(args[0]).ToList()));
            _builtins["sum"] = Native("sum", (_, args, _) => PythonOps.Iterate(Arg(args, 0, "sum")).Aggregate(0d, (acc, value) => acc + PythonOps.ToDouble(value)));
            _builtins["min"] = Native("min", (_, args, _) => PythonOps.MinMax(args, min: true));
            _builtins["max"] = Native("max", (_, args, _) => PythonOps.MinMax(args, min: false));
            _builtins["sorted"] = Native("sorted", (_, args, _) => PythonOps.Iterate(Arg(args, 0, "sorted")).OrderBy(PythonOps.ToString, StringComparer.Ordinal).ToList());
            _builtins["enumerate"] = Native("enumerate", (_, args, _) => PythonOps.Iterate(Arg(args, 0, "enumerate")).Select((value, index) => new PythonTuple([index, value])).ToList());
            _builtins["zip"] = Native("zip", (_, args, _) => PythonOps.Zip(args));
            _builtins["any"] = Native("any", (_, args, _) => PythonOps.Iterate(Arg(args, 0, "any")).Any(PythonOps.IsTruthy));
            _builtins["all"] = Native("all", (_, args, _) => PythonOps.Iterate(Arg(args, 0, "all")).All(PythonOps.IsTruthy));
            _builtins["abs"] = Native("abs", (_, args, _) =>
            {
                var value = Arg(args, 0, "abs");
                return value is double or float ? Math.Abs(PythonOps.ToDouble(value)) : Math.Abs(PythonOps.ToLong(value));
            });
            _builtins["open"] = Native("open", (ctx, args, kwargs) =>
            {
                var path = PythonOps.ToString(Arg(args, 0, "open"));
                var mode = args.Count > 1 ? PythonOps.ToString(args[1]) : kwargs.TryGetValue("mode", out var modeValue) ? PythonOps.ToString(modeValue) : "r";
                return PythonFile.Open(ctx, path, mode);
            });
            _builtins["input"] = Native("input", (ctx, args, _) =>
            {
                if (args.Count > 0) ctx.Output.Write(PythonOps.ToString(args[0]));
                return ctx.Input.ReadLine() ?? "";
            });
            _builtins["isinstance"] = Native("isinstance", (_, args, _) => PythonOps.IsInstance(Arg(args, 0, "isinstance"), Arg(args, 1, "isinstance")));
            _builtins["getattr"] = Native("getattr", (_, args, _) =>
            {
                try { return PythonOps.GetAttr(Arg(args, 0, "getattr"), PythonOps.ToString(Arg(args, 1, "getattr"))); }
                catch (PythonException) when (args.Count > 2) { return args[2]; }
            });
            _builtins["hasattr"] = Native("hasattr", (_, args, _) =>
            {
                try { _ = PythonOps.GetAttr(Arg(args, 0, "hasattr"), PythonOps.ToString(Arg(args, 1, "hasattr"))); return true; }
                catch (PythonException) { return false; }
            });
            _builtins["exit"] = Native("exit", (_, args, _) => throw new PythonSystemExit(args.Count == 0 ? 0 : (int)PythonOps.ToLong(args[0])));
            _builtins["quit"] = _builtins["exit"];
            foreach (var typeName in new[] { "Exception", "ValueError", "TypeError", "RuntimeError", "SystemExit", "FileNotFoundError", "ImportError", "ModuleNotFoundError" })
                _builtins[typeName] = new PythonType(typeName);
        }

        private void RegisterModules()
        {
            var argv = new List<object?>();
            argv.Add(_options.SourceKind switch
            {
                "command" => "-c",
                "module" => "-m",
                "stdin" => "-",
                "file" => _options.Source ?? "",
                _ => "",
            });
            argv.AddRange(_options.ProgramArgs.Cast<object?>());

            var sysPath = new List<object?>();
            if (!_options.SafePath)
            {
                if (_options.SourceKind == "file" && !string.IsNullOrEmpty(_options.Source))
                    sysPath.Add(VfsPath.SplitLeaf(Vfs.Normalize(_options.Source!)).Parent);
                else
                    sysPath.Add(Vfs.CurrentLocation);
            }
            if (!_options.IgnoreEnvironment && !_options.Isolated && _invocation.Env.Get("PYTHONPATH") is { } pythonPath)
            {
                var separator = pythonPath.Contains(';', StringComparison.Ordinal) ? ';' : ':';
                foreach (var part in pythonPath.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    sysPath.Add(Vfs.Normalize(part));
            }
            sysPath.Add("/usr/lib/carbide-python");

            var sys = Module("sys");
            sys.Members["argv"] = argv;
            sys.Members["path"] = sysPath;
            sys.Members["executable"] = _invocation.ResolvedPath;
            sys.Members["version"] = PythonDetailedVersion;
            sys.Members["platform"] = "carbide";
            sys.Members["ps1"] = ">>> ";
            sys.Members["ps2"] = "... ";
            sys.Members["stdin"] = new PythonTextStream(_invocation.Input);
            sys.Members["stdout"] = new PythonTextWriter(_invocation.Output);
            sys.Members["stderr"] = new PythonTextWriter(_invocation.Error);
            sys.Members["exit"] = _builtins["exit"];

            var os = Module("os");
            os.Members["name"] = "posix";
            os.Members["sep"] = "/";
            os.Members["pathsep"] = ":";
            os.Members["linesep"] = "\n";
            os.Members["environ"] = new PythonEnvDict(_invocation.Env);
            os.Members["getcwd"] = Native("getcwd", (ctx, _, _) => ctx.Vfs.CurrentLocation);
            os.Members["chdir"] = Native("chdir", (ctx, args, _) => { ctx.Vfs.SetLocation(PythonOps.ToString(Arg(args, 0, "chdir"))); return null; });
            os.Members["listdir"] = Native("listdir", (ctx, args, _) => ctx.Vfs.List(args.Count == 0 ? "." : PythonOps.ToString(args[0]), false, null).Select(n => (object?)n.Name).ToList());
            os.Members["makedirs"] = Native("makedirs", (ctx, args, _) => { ctx.Vfs.GetOrCreateDirectory(PythonOps.ToString(Arg(args, 0, "makedirs"))); return null; });
            os.Members["mkdir"] = Native("mkdir", (ctx, args, _) => { ctx.Vfs.CreateDirectory(PythonOps.ToString(Arg(args, 0, "mkdir"))); return null; });
            os.Members["remove"] = Native("remove", (ctx, args, _) => { ctx.Vfs.Delete(PythonOps.ToString(Arg(args, 0, "remove")), false, false); return null; });
            os.Members["unlink"] = os.Members["remove"];
            os.Members["getenv"] = Native("getenv", (ctx, args, _) => ctx._invocation.Env.Get(PythonOps.ToString(Arg(args, 0, "getenv"))) ?? (args.Count > 1 ? args[1] : null));
            os.Members["walk"] = Native("walk", (ctx, args, _) => PythonOps.Walk(ctx.Vfs, PythonOps.ToString(Arg(args, 0, "walk"))));

            var ospath = Module("os.path");
            ospath.Members["join"] = Native("join", (_, args, _) => args.Select(PythonOps.ToString).Aggregate(VfsPath.Join));
            ospath.Members["exists"] = Native("exists", (ctx, args, _) => ctx.Vfs.Exists(PythonOps.ToString(Arg(args, 0, "exists"))));
            ospath.Members["isfile"] = Native("isfile", (ctx, args, _) => ctx.Vfs.IsFile(PythonOps.ToString(Arg(args, 0, "isfile"))));
            ospath.Members["isdir"] = Native("isdir", (ctx, args, _) => ctx.Vfs.IsDirectory(PythonOps.ToString(Arg(args, 0, "isdir"))));
            ospath.Members["abspath"] = Native("abspath", (ctx, args, _) => ctx.Vfs.Normalize(PythonOps.ToString(Arg(args, 0, "abspath"))));
            ospath.Members["basename"] = Native("basename", (ctx, args, _) => VfsPath.SplitLeaf(ctx.Vfs.Normalize(PythonOps.ToString(Arg(args, 0, "basename")))).Leaf);
            ospath.Members["dirname"] = Native("dirname", (ctx, args, _) => VfsPath.SplitLeaf(ctx.Vfs.Normalize(PythonOps.ToString(Arg(args, 0, "dirname")))).Parent);
            ospath.Members["splitext"] = Native("splitext", (ctx, args, _) =>
            {
                var path = PythonOps.ToString(Arg(args, 0, "splitext"));
                var ext = VfsPath.GetExtension(path);
                return new PythonTuple([ext.Length == 0 ? path : path[..^ext.Length], ext]);
            });
            os.Members["path"] = ospath;

            var json = Module("json");
            json.Members["loads"] = Native("loads", (_, args, _) => PythonJson.FromJson(JsonSerializer.Deserialize<JsonElement>(PythonOps.ToString(Arg(args, 0, "loads")))));
            json.Members["dumps"] = Native("dumps", (_, args, kwargs) => PythonJson.ToJson(Arg(args, 0, "dumps"), kwargs.ContainsKey("indent")));
            json.Members["load"] = Native("load", (_, args, _) => PythonJson.FromJson(JsonSerializer.Deserialize<JsonElement>(PythonOps.ToString(PythonOps.CallMethod(Arg(args, 0, "load"), "read", [], new())))));
            json.Members["dump"] = Native("dump", (_, args, kwargs) =>
            {
                var text = PythonJson.ToJson(Arg(args, 0, "dump"), kwargs.ContainsKey("indent"));
                PythonOps.CallMethod(Arg(args, 1, "dump"), "write", [text], new());
                return null;
            });

            var re = Module("re");
            re.Members["I"] = RegexOptions.IgnoreCase;
            re.Members["IGNORECASE"] = RegexOptions.IgnoreCase;
            re.Members["M"] = RegexOptions.Multiline;
            re.Members["MULTILINE"] = RegexOptions.Multiline;
            re.Members["S"] = RegexOptions.Singleline;
            re.Members["DOTALL"] = RegexOptions.Singleline;
            re.Members["search"] = Native("search", (_, args, _) => PythonRegex.Search(args));
            re.Members["match"] = Native("match", (_, args, _) => PythonRegex.Match(args));
            re.Members["fullmatch"] = Native("fullmatch", (_, args, _) => PythonRegex.FullMatch(args));
            re.Members["sub"] = Native("sub", (_, args, _) => Regex.Replace(PythonOps.ToString(Arg(args, 2, "sub")), PythonOps.ToString(Arg(args, 0, "sub")), PythonOps.ToString(Arg(args, 1, "sub"))));
            re.Members["findall"] = Native("findall", (_, args, _) => Regex.Matches(PythonOps.ToString(Arg(args, 1, "findall")), PythonOps.ToString(Arg(args, 0, "findall"))).Select(m => (object?)m.Value).ToList());
            re.Members["split"] = Native("split", (_, args, _) => Regex.Split(PythonOps.ToString(Arg(args, 1, "split")), PythonOps.ToString(Arg(args, 0, "split"))).Cast<object?>().ToList());
            re.Members["escape"] = Native("escape", (_, args, _) => Regex.Escape(PythonOps.ToString(Arg(args, 0, "escape"))));

            var pathlib = Module("pathlib");
            pathlib.Members["Path"] = Native("Path", (ctx, args, _) => new PythonPath(ctx, args.Count == 0 ? "." : PythonOps.ToString(args[0])));
            pathlib.Members["PurePath"] = pathlib.Members["Path"];
            pathlib.Members["PurePosixPath"] = pathlib.Members["Path"];
            pathlib.Members["PureWindowsPath"] = pathlib.Members["Path"];

            var glob = Module("glob");
            glob.Members["glob"] = Native("glob", (ctx, args, _) => PythonGlob.Glob(ctx.Vfs, PythonOps.ToString(Arg(args, 0, "glob"))).Cast<object?>().ToList());
            glob.Members["iglob"] = glob.Members["glob"];

            var fnmatch = Module("fnmatch");
            fnmatch.Members["fnmatch"] = Native("fnmatch", (_, args, _) => PythonGlob.Match(PythonOps.ToString(Arg(args, 0, "fnmatch")), PythonOps.ToString(Arg(args, 1, "fnmatch"))));
            fnmatch.Members["fnmatchcase"] = fnmatch.Members["fnmatch"];
            fnmatch.Members["filter"] = Native("filter", (_, args, _) => PythonOps.Iterate(Arg(args, 0, "filter")).Where(item => PythonGlob.Match(PythonOps.ToString(item), PythonOps.ToString(Arg(args, 1, "filter")))).ToList());

            var textwrap = Module("textwrap");
            textwrap.Members["dedent"] = Native("dedent", (_, args, _) => PythonText.Dedent(PythonOps.ToString(Arg(args, 0, "dedent"))));
            textwrap.Members["indent"] = Native("indent", (_, args, _) => string.Join("\n", PythonOps.ToString(Arg(args, 0, "indent")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => PythonOps.ToString(Arg(args, 1, "indent")) + line)));

            var argparse = Module("argparse");
            argparse.Members["ArgumentParser"] = Native("ArgumentParser", (ctx, _, kwargs) => new PythonArgumentParser(ctx, kwargs));
            argparse.Members["Namespace"] = Native("Namespace", (_, _, kwargs) => new PythonNamespace(new Dictionary<string, object?>(kwargs, StringComparer.Ordinal)));

            var subprocess = Module("subprocess");
            subprocess.Members["PIPE"] = -1L;
            subprocess.Members["STDOUT"] = -2L;
            subprocess.Members["DEVNULL"] = -3L;
            subprocess.Members["run"] = Native("run", RunSubprocess);
            subprocess.Members["check_output"] = Native("check_output", (ctx, args, kwargs) =>
            {
                kwargs["capture_output"] = true;
                kwargs["text"] = true;
                var result = (PythonCompletedProcess)RunSubprocess(ctx, args, kwargs)!;
                if (result.ReturnCode != 0) throw new PythonException("CalledProcessError", $"Command returned non-zero exit status {result.ReturnCode}.");
                return result.Stdout;
            });

            Globals["sys"] = sys;
        }

        private object? RunSubprocess(PythonContext ctx, IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
        {
            var commandValue = Arg(args, 0, "run");
            var command = PythonOps.Iterate(commandValue).Select(PythonOps.ToString).ToArray();
            if (command.Length == 0)
                throw new PythonException("ValueError", "empty command");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var stdin = TextReader.Null;
            var shellCtx = new ShellExecutionContext
            {
                Args = command.Skip(1).ToArray(),
                Input = stdin,
                Output = stdout,
                Error = stderr,
                Vfs = ctx.Vfs,
                Env = _invocation.Env,
                Apps = _invocation.Apps,
                Dispatcher = _invocation.Dispatcher,
            };
            var resolution = _invocation.Dispatcher.Resolve(command[0], shellCtx, "python");
            var code = resolution.Kind switch
            {
                ResolutionKind.VirtualExecutable when resolution.VirtualExecutable is not null && resolution.VirtualExecutablePath is not null
                    => _invocation.Dispatcher.ExecuteVirtualExecutable(resolution.VirtualExecutable, resolution.VirtualExecutablePath, command[0], command.Skip(1).ToArray(), shellCtx),
                ResolutionKind.Script when resolution.Kernel is not null && resolution.ScriptPath is not null
                    => _invocation.Dispatcher.ExecuteScript(resolution.ScriptPath, resolution.Kernel, shellCtx),
                ResolutionKind.NamedShell when resolution.Kernel is not null
                    => _invocation.Dispatcher.EnterSubShell(resolution.Kernel, shellCtx),
                _ => throw new PythonException("FileNotFoundError", $"No such executable: '{command[0]}'"),
            };
            if (kwargs.TryGetValue("check", out var check) && PythonOps.IsTruthy(check) && code != 0)
                throw new PythonException("CalledProcessError", $"Command returned non-zero exit status {code}.");
            return new PythonCompletedProcess(command.ToList<object?>(), code, stdout.ToString(), stderr.ToString());
        }

        private PythonModule Module(string name)
        {
            var module = new PythonModule(name);
            Modules[name] = module;
            return module;
        }

        private static NativePythonFunction Native(string name, Func<PythonContext, IReadOnlyList<object?>, Dictionary<string, object?>, object?> body)
            => new(name, body);
    }

    private sealed class PythonProgram
    {
        private readonly IReadOnlyList<PythonLine> _lines;
        private readonly string _path;

        public bool IsSingleExpression { get; }
        public string? SingleExpression { get; }

        private PythonProgram(IReadOnlyList<PythonLine> lines, string path, bool singleExpression, string? singleExpressionText)
        {
            _lines = lines;
            _path = path;
            IsSingleExpression = singleExpression;
            SingleExpression = singleExpressionText;
        }

        public static PythonProgram Parse(string source, string path)
        {
            var lines = BuildLines(source, path);
            var meaningful = lines.Where(line => line.Trimmed.Length > 0).ToArray();
            var singleExpression = meaningful.Length == 1 && IsExpressionOnly(meaningful[0].Trimmed);
            return new PythonProgram(lines, path, singleExpression, singleExpression ? meaningful[0].Trimmed : null);
        }

        public void Execute(PythonContext context)
        {
            ExecuteBlock(context, 0, _lines.Count, 0);
        }

        private static IReadOnlyList<PythonLine> BuildLines(string source, string path)
        {
            var result = new List<PythonLine>();
            var physical = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            for (var i = 0; i < physical.Length; i++)
            {
                var baseIndent = CountIndent(physical[i]);
                var segmentIndex = 0;
                foreach (var segment in SplitSemicolons(RemoveComment(physical[i])))
                {
                    var logical = segmentIndex++ == 0
                        ? segment
                        : new string(' ', baseIndent) + segment.TrimStart();
                    var indent = CountIndent(logical);
                    result.Add(new PythonLine(logical, logical.Trim(), indent, i + 1, path));
                }
            }
            return result;
        }

        private static IEnumerable<string> SplitSemicolons(string line)
        {
            var start = 0;
            var inString = false;
            var quote = '\0';
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (inString)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == quote) inString = false;
                    continue;
                }
                if (ch is '\'' or '"') { inString = true; quote = ch; continue; }
                if (ch == ';')
                {
                    yield return line[start..i];
                    start = i + 1;
                }
            }
            yield return line[start..];
        }

        private static string RemoveComment(string line)
        {
            var inString = false;
            var quote = '\0';
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (inString)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == quote) inString = false;
                    continue;
                }
                if (ch is '\'' or '"') { inString = true; quote = ch; continue; }
                if (ch == '#') return line[..i];
            }
            return line;
        }

        private static bool IsExpressionOnly(string text)
            => !(text.StartsWith("import ", StringComparison.Ordinal)
                || text.StartsWith("from ", StringComparison.Ordinal)
                || text.StartsWith("return", StringComparison.Ordinal)
                || text.StartsWith("if ", StringComparison.Ordinal)
                || text.StartsWith("for ", StringComparison.Ordinal)
                || text.StartsWith("while ", StringComparison.Ordinal)
                || text.StartsWith("def ", StringComparison.Ordinal)
                || text.StartsWith("with ", StringComparison.Ordinal)
                || text.Contains('=', StringComparison.Ordinal) && !Regex.IsMatch(text, @"[!<>=]="));

        private void ExecuteBlock(PythonContext context, int start, int end, int indent)
        {
            var i = start;
            while (i < end)
            {
                var line = _lines[i];
                if (line.Trimmed.Length == 0) { i++; continue; }
                if (line.Indent < indent) return;
                if (line.Indent > indent) throw new PythonSyntaxException(line, "unexpected indent");
                ExecuteLine(context, ref i, end, indent);
            }
        }

        private void ExecuteLine(PythonContext context, ref int i, int end, int indent)
        {
            var line = _lines[i];
            var text = line.Trimmed;
            if (text.StartsWith("if ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
            {
                ExecuteIf(context, ref i, end, indent);
                return;
            }
            if (text.StartsWith("for ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
            {
                var match = Regex.Match(text, @"^for\s+([A-Za-z_][A-Za-z0-9_]*)\s+in\s+(.+):$");
                if (!match.Success) throw new PythonSyntaxException(line, "invalid for statement");
                var blockStart = i + 1;
                var blockEnd = FindBlockEnd(blockStart, end, indent);
                var childIndent = ChildIndent(blockStart, blockEnd, indent);
                foreach (var item in PythonOps.Iterate(Eval(context, match.Groups[2].Value)))
                {
                    context.SetName(match.Groups[1].Value, item);
                    try { ExecuteBlock(context, blockStart, blockEnd, childIndent); }
                    catch (PythonContinue) { continue; }
                    catch (PythonBreak) { break; }
                }
                i = blockEnd;
                return;
            }
            if (text.StartsWith("while ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
            {
                var condition = text[6..^1].Trim();
                var blockStart = i + 1;
                var blockEnd = FindBlockEnd(blockStart, end, indent);
                var childIndent = ChildIndent(blockStart, blockEnd, indent);
                var guard = 0;
                while (PythonOps.IsTruthy(Eval(context, condition)))
                {
                    if (++guard > 100000) throw new PythonException("RuntimeError", "loop exceeded Carbide Python iteration limit");
                    try { ExecuteBlock(context, blockStart, blockEnd, childIndent); }
                    catch (PythonContinue) { continue; }
                    catch (PythonBreak) { break; }
                }
                i = blockEnd;
                return;
            }
            if (text.StartsWith("def ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
            {
                var match = Regex.Match(text, @"^def\s+([A-Za-z_][A-Za-z0-9_]*)\s*\((.*)\):$");
                if (!match.Success) throw new PythonSyntaxException(line, "invalid function definition");
                var blockStart = i + 1;
                var blockEnd = FindBlockEnd(blockStart, end, indent);
                var parameters = match.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => part.Split('=')[0].Trim())
                    .Where(part => part.Length > 0)
                    .ToArray();
                context.SetName(match.Groups[1].Value, new UserPythonFunction(match.Groups[1].Value, parameters, this, blockStart, blockEnd, ChildIndent(blockStart, blockEnd, indent)));
                i = blockEnd;
                return;
            }
            if (text.StartsWith("with ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
            {
                var match = Regex.Match(text, @"^with\s+(.+)\s+as\s+([A-Za-z_][A-Za-z0-9_]*)\s*:$");
                if (!match.Success) throw new PythonSyntaxException(line, "invalid with statement");
                var resource = Eval(context, match.Groups[1].Value);
                context.SetName(match.Groups[2].Value, resource);
                var blockStart = i + 1;
                var blockEnd = FindBlockEnd(blockStart, end, indent);
                try { ExecuteBlock(context, blockStart, blockEnd, ChildIndent(blockStart, blockEnd, indent)); }
                finally
                {
                    if (resource is PythonFile file) file.Close();
                }
                i = blockEnd;
                return;
            }

            ExecuteSimple(context, line);
            i++;
        }

        private void ExecuteIf(PythonContext context, ref int i, int end, int indent)
        {
            var cursor = i;
            while (cursor < end)
            {
                var head = _lines[cursor];
                if (head.Trimmed.Length == 0) { cursor++; continue; }
                if (head.Indent != indent) break;
                var text = head.Trimmed;
                string? condition = null;
                if (text.StartsWith("if ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
                    condition = text[3..^1].Trim();
                else if (text.StartsWith("elif ", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal))
                    condition = text[5..^1].Trim();
                else if (text == "else:")
                    condition = null;
                else
                    break;

                var blockStart = cursor + 1;
                var blockEnd = FindBlockEnd(blockStart, end, indent);
                if (condition is null || PythonOps.IsTruthy(Eval(context, condition)))
                {
                    ExecuteBlock(context, blockStart, blockEnd, ChildIndent(blockStart, blockEnd, indent));
                    i = SkipElifElseChain(blockEnd, end, indent);
                    return;
                }
                cursor = blockEnd;
            }
            i = cursor;
        }

        private int SkipElifElseChain(int start, int end, int indent)
        {
            var cursor = start;
            while (cursor < end)
            {
                var line = _lines[cursor];
                if (line.Trimmed.Length == 0) { cursor++; continue; }
                if (line.Indent != indent) break;
                if (!(line.Trimmed.StartsWith("elif ", StringComparison.Ordinal) || line.Trimmed == "else:")) break;
                cursor = FindBlockEnd(cursor + 1, end, indent);
            }
            return cursor;
        }

        private int FindBlockEnd(int start, int end, int parentIndent)
        {
            var cursor = start;
            while (cursor < end)
            {
                var line = _lines[cursor];
                if (line.Trimmed.Length == 0) { cursor++; continue; }
                if (line.Indent <= parentIndent) break;
                cursor++;
            }
            return cursor;
        }

        private int ChildIndent(int start, int end, int parentIndent)
        {
            for (var index = start; index < end; index++)
            {
                var line = _lines[index];
                if (line.Trimmed.Length == 0)
                    continue;
                if (line.Indent <= parentIndent)
                    throw new PythonSyntaxException(line, "expected an indented block");
                return line.Indent;
            }

            return parentIndent + 1;
        }

        private void ExecuteSimple(PythonContext context, PythonLine line)
        {
            var text = line.Trimmed;
            if (text.Length == 0) return;
            if (text == "pass") return;
            if (text == "break") throw new PythonBreak();
            if (text == "continue") throw new PythonContinue();
            if (text.StartsWith("return", StringComparison.Ordinal))
            {
                var expr = text.Length == 6 ? "" : text[6..].Trim();
                throw new PythonReturn(expr.Length == 0 ? null : Eval(context, expr));
            }
            if (text.StartsWith("assert ", StringComparison.Ordinal))
            {
                if (!context.Optimize && !PythonOps.IsTruthy(Eval(context, text[7..].Trim())))
                    throw new PythonException("AssertionError", "");
                return;
            }
            if (text.StartsWith("import ", StringComparison.Ordinal))
            {
                foreach (var part in text[7..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var pieces = part.Split(" as ", StringSplitOptions.TrimEntries);
                    var module = context.ImportModule(pieces[0]);
                    context.SetName(pieces.Length > 1 ? pieces[1] : pieces[0].Split('.')[0], module);
                }
                return;
            }
            if (text.StartsWith("from ", StringComparison.Ordinal))
            {
                var match = Regex.Match(text, @"^from\s+([A-Za-z0-9_.]+)\s+import\s+(.+)$");
                if (!match.Success) throw new PythonSyntaxException(line, "invalid import statement");
                var module = context.ImportModule(match.Groups[1].Value);
                foreach (var name in match.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var pieces = name.Split(" as ", StringSplitOptions.TrimEntries);
                    if (!module.Members.TryGetValue(pieces[0], out var value))
                        throw new PythonException("ImportError", $"cannot import name '{pieces[0]}' from '{module.Name}'");
                    context.SetName(pieces.Length > 1 ? pieces[1] : pieces[0], value);
                }
                return;
            }
            if (text.StartsWith("raise ", StringComparison.Ordinal))
                throw new PythonException("RuntimeError", PythonOps.ToString(Eval(context, text[6..].Trim())));

            var assignment = FindAssignment(text);
            if (assignment >= 0)
            {
                var left = text[..assignment].Trim();
                var right = text[(assignment + 1)..].Trim();
                var value = Eval(context, right);
                if (Regex.IsMatch(left, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    context.SetName(left, value);
                else
                    PythonOps.AssignTarget(context, left, value);
                return;
            }

            _ = Eval(context, text);
        }

        private static int FindAssignment(string text)
        {
            var depth = 0;
            var inString = false;
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == quote) inString = false;
                    continue;
                }
                if (ch is '\'' or '"') { inString = true; quote = ch; continue; }
                if (ch is '(' or '[' or '{') depth++;
                else if (ch is ')' or ']' or '}') depth--;
                else if (ch == '=' && depth == 0)
                {
                    var prev = i > 0 ? text[i - 1] : '\0';
                    var next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (prev is '!' or '<' or '>' or '=' || next == '=') continue;
                    return i;
                }
            }
            return -1;
        }

        private object? Eval(PythonContext context, string expression)
            => new PythonExpressionParser(expression, context).ParseExpression();

        private sealed class UserPythonFunction : IPythonCallable
        {
            private readonly string _name;
            private readonly IReadOnlyList<string> _parameters;
            private readonly PythonProgram _program;
            private readonly int _start;
            private readonly int _end;
            private readonly int _indent;

            public UserPythonFunction(string name, IReadOnlyList<string> parameters, PythonProgram program, int start, int end, int indent)
            {
                _name = name;
                _parameters = parameters;
                _program = program;
                _start = start;
                _end = end;
                _indent = indent;
            }

            public object? Invoke(PythonContext context, IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
            {
                var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < _parameters.Count; i++)
                    locals[_parameters[i]] = i < args.Count ? args[i] : kwargs.TryGetValue(_parameters[i], out var value) ? value : null;
                context.PushLocals(locals);
                try
                {
                    _program.ExecuteBlock(context, _start, _end, _indent);
                    return null;
                }
                catch (PythonReturn ret)
                {
                    return ret.Value;
                }
                finally
                {
                    context.PopLocals();
                }
            }

            public override string ToString() => $"<function {_name}>";
        }
    }

    private sealed record PythonLine(string Raw, string Trimmed, int Indent, int Number, string Path);

    private sealed class PythonExpressionParser
    {
        private readonly IReadOnlyList<PythonToken> _tokens;
        private readonly PythonContext _context;
        private int _pos;

        public PythonExpressionParser(string source, PythonContext context)
        {
            _tokens = PythonTokenizer.Tokenize(source);
            _context = context;
        }

        public object? ParseExpression()
        {
            var value = ParseOr();
            return value;
        }

        private object? ParseOr()
        {
            var left = ParseAnd();
            while (Match("or"))
            {
                if (PythonOps.IsTruthy(left)) { _ = ParseAnd(); left = true; }
                else left = ParseAnd();
            }
            return left;
        }

        private object? ParseAnd()
        {
            var left = ParseNot();
            while (Match("and"))
            {
                if (!PythonOps.IsTruthy(left)) { _ = ParseNot(); left = false; }
                else left = ParseNot();
            }
            return left;
        }

        private object? ParseNot()
        {
            if (Match("not")) return !PythonOps.IsTruthy(ParseNot());
            return ParseComparison();
        }

        private object? ParseComparison()
        {
            var left = ParseAdd();
            while (true)
            {
                if (Match("==")) left = PythonOps.CompareEqual(left, ParseAdd());
                else if (Match("!=")) left = !PythonOps.CompareEqual(left, ParseAdd());
                else if (Match("<")) left = PythonOps.Compare(left, ParseAdd()) < 0;
                else if (Match("<=")) left = PythonOps.Compare(left, ParseAdd()) <= 0;
                else if (Match(">")) left = PythonOps.Compare(left, ParseAdd()) > 0;
                else if (Match(">=")) left = PythonOps.Compare(left, ParseAdd()) >= 0;
                else if (Match("in")) left = PythonOps.Contains(ParseAdd(), left);
                else break;
            }
            return left;
        }

        private object? ParseAdd()
        {
            var left = ParseMul();
            while (true)
            {
                if (Match("+")) left = PythonOps.Add(left, ParseMul());
                else if (Match("-")) left = PythonOps.Subtract(left, ParseMul());
                else break;
            }
            return left;
        }

        private object? ParseMul()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("*")) left = PythonOps.Multiply(left, ParseUnary());
                else if (Match("/")) left = PythonOps.Divide(left, ParseUnary());
                else if (Match("//")) left = Math.Floor(PythonOps.Divide(left, ParseUnary()));
                else if (Match("%")) left = PythonOps.Mod(left, ParseUnary());
                else break;
            }
            return left;
        }

        private object? ParseUnary()
        {
            if (Match("+")) return ParseUnary();
            if (Match("-"))
            {
                var value = ParseUnary();
                return value is double or float ? -PythonOps.ToDouble(value) : -PythonOps.ToLong(value);
            }
            return ParsePostfix();
        }

        private object? ParsePostfix()
        {
            var value = ParsePrimary();
            while (true)
            {
                if (Match("."))
                {
                    var name = ExpectIdentifier();
                    value = PythonOps.GetAttr(value, name);
                }
                else if (Match("("))
                {
                    var args = new List<object?>();
                    var kwargs = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (!Check(")"))
                    {
                        do
                        {
                            if (CheckIdentifier() && Peek(1).Text == "=")
                            {
                                var name = Advance().Text;
                                Expect("=");
                                kwargs[name] = ParseExpression();
                            }
                            else
                            {
                                args.Add(ParseExpression());
                            }
                        } while (Match(","));
                    }
                    Expect(")");
                    value = PythonOps.Call(value, _context, args, kwargs);
                }
                else if (Match("["))
                {
                    var index = ParseExpression();
                    Expect("]");
                    value = PythonOps.GetIndex(value, index);
                }
                else break;
            }
            return value;
        }

        private object? ParsePrimary()
        {
            var token = Advance();
            if (token.Kind == PythonTokenKind.Number)
                return token.Text.Contains('.', StringComparison.Ordinal)
                    ? double.Parse(token.Text, CultureInfo.InvariantCulture)
                    : long.Parse(token.Text, CultureInfo.InvariantCulture);
            if (token.Kind == PythonTokenKind.String)
                return token.Value;
            if (token.Kind == PythonTokenKind.Identifier)
            {
                if (token.Text == "True") return true;
                if (token.Text == "False") return false;
                if (token.Text == "None") return null;
                return _context.GetName(token.Text);
            }
            if (token.Text == "(")
            {
                if (Match(")")) return new PythonTuple([]);
                var first = ParseExpression();
                if (Match(","))
                {
                    var values = new List<object?> { first };
                    do { values.Add(ParseExpression()); } while (Match(","));
                    Expect(")");
                    return new PythonTuple(values);
                }
                Expect(")");
                return first;
            }
            if (token.Text == "[")
            {
                var values = new List<object?>();
                if (!Check("]"))
                {
                    do { values.Add(ParseExpression()); } while (Match(","));
                }
                Expect("]");
                return values;
            }
            if (token.Text == "{")
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (!Check("}"))
                {
                    do
                    {
                        var key = PythonOps.ToString(ParseExpression());
                        Expect(":");
                        dict[key] = ParseExpression();
                    } while (Match(","));
                }
                Expect("}");
                return dict;
            }
            throw new PythonException("SyntaxError", $"unexpected token '{token.Text}'");
        }

        private bool Match(string text)
        {
            if (!Check(text)) return false;
            _pos++;
            return true;
        }

        private bool Check(string text) => Peek().Text == text;
        private bool CheckIdentifier() => Peek().Kind == PythonTokenKind.Identifier;
        private PythonToken Peek(int offset = 0) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : PythonToken.End;
        private PythonToken Advance() => _pos < _tokens.Count ? _tokens[_pos++] : PythonToken.End;

        private void Expect(string text)
        {
            if (!Match(text)) throw new PythonException("SyntaxError", $"expected '{text}'");
        }

        private string ExpectIdentifier()
        {
            var token = Advance();
            if (token.Kind != PythonTokenKind.Identifier)
                throw new PythonException("SyntaxError", "expected identifier");
            return token.Text;
        }
    }

    private static class PythonTokenizer
    {
        public static IReadOnlyList<PythonToken> Tokenize(string source)
        {
            var tokens = new List<PythonToken>();
            for (var i = 0; i < source.Length;)
            {
                var ch = source[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (char.IsLetter(ch) || ch == '_')
                {
                    var start = i++;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    tokens.Add(new PythonToken(PythonTokenKind.Identifier, source[start..i], null));
                    continue;
                }
                if (char.IsDigit(ch))
                {
                    var start = i++;
                    while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) i++;
                    tokens.Add(new PythonToken(PythonTokenKind.Number, source[start..i], null));
                    continue;
                }
                if (ch is '\'' or '"' || (ch is 'r' or 'R' or 'f' or 'F' && i + 1 < source.Length && source[i + 1] is '\'' or '"'))
                {
                    var isF = false;
                    if (ch is 'r' or 'R' or 'f' or 'F')
                    {
                        isF = ch is 'f' or 'F';
                        i++;
                        ch = source[i];
                    }
                    var quote = ch;
                    i++;
                    var sb = new StringBuilder();
                    while (i < source.Length && source[i] != quote)
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            var esc = source[++i];
                            sb.Append(esc switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '\'' => '\'', '"' => '"', _ => esc });
                            i++;
                            continue;
                        }
                        sb.Append(source[i++]);
                    }
                    if (i < source.Length) i++;
                    tokens.Add(new PythonToken(PythonTokenKind.String, source, isF ? sb.ToString() : sb.ToString()));
                    continue;
                }
                var two = i + 1 < source.Length ? source.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<=" or ">=" or "//" or "**" or ":=")
                {
                    tokens.Add(new PythonToken(PythonTokenKind.Operator, two, null));
                    i += 2;
                    continue;
                }
                tokens.Add(new PythonToken(PythonTokenKind.Operator, ch.ToString(), null));
                i++;
            }
            tokens.Add(PythonToken.End);
            return tokens;
        }
    }

    private enum PythonTokenKind { Identifier, Number, String, Operator, End }
    private sealed record PythonToken(PythonTokenKind Kind, string Text, object? Value)
    {
        public static readonly PythonToken End = new(PythonTokenKind.End, "<eof>", null);
    }

    private interface IPythonCallable
    {
        object? Invoke(PythonContext context, IReadOnlyList<object?> args, Dictionary<string, object?> kwargs);
    }

    private sealed class NativePythonFunction : IPythonCallable
    {
        private readonly Func<PythonContext, IReadOnlyList<object?>, Dictionary<string, object?>, object?> _body;
        public string Name { get; }
        public NativePythonFunction(string name, Func<PythonContext, IReadOnlyList<object?>, Dictionary<string, object?>, object?> body)
        {
            Name = name;
            _body = body;
        }
        public object? Invoke(PythonContext context, IReadOnlyList<object?> args, Dictionary<string, object?> kwargs) => _body(context, args, kwargs);
        public override string ToString() => $"<built-in function {Name}>";
    }

    private sealed class PythonModule
    {
        public string Name { get; }
        public Dictionary<string, object?> Members { get; } = new(StringComparer.Ordinal);
        public PythonModule(string name)
        {
            Name = name;
            Members["__name__"] = name;
        }
        public override string ToString() => $"<module '{Name}'>";
    }

    private sealed record PythonTuple(IReadOnlyList<object?> Values);
    private sealed record PythonRange(long Start, long Stop, long Step)
    {
        public IEnumerable<object?> Values()
        {
            if (Step == 0) yield break;
            if (Step > 0)
            {
                for (var i = Start; i < Stop; i += Step) yield return i;
            }
            else
            {
                for (var i = Start; i > Stop; i += Step) yield return i;
            }
        }

        public static PythonRange FromArgs(IReadOnlyList<object?> args)
            => args.Count switch
            {
                1 => new PythonRange(0, PythonOps.ToLong(args[0]), 1),
                2 => new PythonRange(PythonOps.ToLong(args[0]), PythonOps.ToLong(args[1]), 1),
                >= 3 => new PythonRange(PythonOps.ToLong(args[0]), PythonOps.ToLong(args[1]), PythonOps.ToLong(args[2])),
                _ => new PythonRange(0, 0, 1),
            };
    }

    private sealed class PythonFile
    {
        private readonly PythonContext _context;
        private readonly string _path;
        private readonly string _mode;
        private bool _closed;

        private PythonFile(PythonContext context, string path, string mode)
        {
            _context = context;
            _path = context.Vfs.Normalize(path);
            _mode = mode;
        }

        public static PythonFile Open(PythonContext context, string path, string mode)
        {
            var file = new PythonFile(context, path, mode);
            if (mode.Contains('x', StringComparison.Ordinal) && context.Vfs.Exists(file._path))
                throw new PythonException("FileExistsError", $"File exists: '{path}'");
            if (mode.Contains('w', StringComparison.Ordinal))
                context.Vfs.CreateTextFile(file._path, "", overwrite: true);
            else if (mode.Contains('x', StringComparison.Ordinal))
                context.Vfs.CreateTextFile(file._path, "", overwrite: false);
            else if (!mode.Contains('a', StringComparison.Ordinal) && context.Vfs.Resolve(file._path) is not VfsFile)
                throw new PythonException("FileNotFoundError", $"No such file: '{path}'");
            return file;
        }

        public string Read()
        {
            EnsureOpen();
            if (_context.Vfs.Resolve(_path) is not VfsFile file)
                throw new PythonException("FileNotFoundError", $"No such file: '{_path}'");
            return file.ReadText();
        }

        public object? Write(string text)
        {
            EnsureOpen();
            if (_mode.Contains('a', StringComparison.Ordinal))
            {
                if (_context.Vfs.Resolve(_path) is VfsFile existing) existing.AppendText(text);
                else _context.Vfs.CreateTextFile(_path, text, overwrite: false);
            }
            else
            {
                if (_context.Vfs.Resolve(_path) is VfsFile existing) existing.AppendText(text);
                else _context.Vfs.CreateTextFile(_path, text, overwrite: false);
            }
            return text.Length;
        }

        public void Close() => _closed = true;
        private void EnsureOpen()
        {
            if (_closed) throw new PythonException("ValueError", "I/O operation on closed file.");
        }
    }

    private sealed class PythonPath
    {
        private readonly PythonContext _context;
        private readonly string _path;
        public PythonContext Context => _context;
        public PythonPath(PythonContext context, string path)
        {
            _context = context;
            _path = path;
        }
        public string Normalized => _context.Vfs.Normalize(_path);
        public override string ToString() => _path;
        public string ReadText() => _context.Vfs.Resolve(Normalized) is VfsFile file ? file.ReadText() : throw new PythonException("FileNotFoundError", $"No such file: '{_path}'");
        public object? WriteText(string text) { _context.Vfs.CreateTextFile(Normalized, text, overwrite: true); return text.Length; }
        public bool Exists() => _context.Vfs.Exists(Normalized);
        public bool IsFile() => _context.Vfs.IsFile(Normalized);
        public bool IsDir() => _context.Vfs.IsDirectory(Normalized);
        public List<object?> IterDir() => _context.Vfs.List(Normalized, false, null).Select(n => (object?)new PythonPath(_context, n.AbsolutePath)).ToList();
    }

    private sealed class PythonTextStream
    {
        private readonly TextReader _reader;
        public PythonTextStream(TextReader reader) { _reader = reader; }
        public string Read() => ReadAllText(_reader);
        public string? ReadLine() => _reader.ReadLine();
    }

    private sealed class PythonTextWriter
    {
        private readonly TextWriter _writer;
        public PythonTextWriter(TextWriter writer) { _writer = writer; }
        public object? Write(string text) { _writer.Write(text); return null; }
        public object? Flush() { _writer.Flush(); return null; }
    }

    private sealed class PythonEnvDict
    {
        private readonly CarbideShellCore.Env.EnvVarStore _env;
        public PythonEnvDict(CarbideShellCore.Env.EnvVarStore env) { _env = env; }
        public object? Get(string name, object? fallback = null) => _env.Get(name) ?? fallback;
        public List<object?> Keys() => _env.All.Keys.Cast<object?>().ToList();
    }

    private sealed class PythonArgumentParser
    {
        private readonly PythonContext _context;
        private readonly List<PythonArgumentSpec> _arguments = new();
        private readonly string _prog;
        private readonly string? _description;

        public PythonArgumentParser(PythonContext context, Dictionary<string, object?> kwargs)
        {
            _context = context;
            _prog = kwargs.TryGetValue("prog", out var prog) ? PythonOps.ToString(prog) : "python";
            _description = kwargs.TryGetValue("description", out var description) ? PythonOps.ToString(description) : null;
        }

        public object? AddArgument(IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
        {
            if (args.Count == 0)
                throw new PythonException("TypeError", "add_argument() missing argument name");

            var names = args.Select(PythonOps.ToString).ToArray();
            var isOption = names.Any(static name => name.StartsWith("-", StringComparison.Ordinal));
            var dest = kwargs.TryGetValue("dest", out var destValue)
                ? PythonOps.ToString(destValue)
                : InferDest(names, isOption);
            var action = kwargs.TryGetValue("action", out var actionValue) ? PythonOps.ToString(actionValue) : "store";
            var nargs = kwargs.TryGetValue("nargs", out var nargsValue) ? PythonOps.ToString(nargsValue) : null;
            var required = kwargs.TryGetValue("required", out var requiredValue) && PythonOps.IsTruthy(requiredValue);
            var hasDefault = kwargs.ContainsKey("default");
            var defaultValue = hasDefault ? kwargs["default"] : null;
            kwargs.TryGetValue("type", out var type);
            kwargs.TryGetValue("choices", out var choices);

            var spec = new PythonArgumentSpec(names, dest, isOption, action, nargs, required, hasDefault, defaultValue, type, choices);
            _arguments.Add(spec);
            return spec;
        }

        public PythonNamespace ParseArgs(IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
        {
            var result = Parse(args, allowUnknown: false);
            return result.Namespace;
        }

        public PythonTuple ParseKnownArgs(IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
        {
            var result = Parse(args, allowUnknown: true);
            return new PythonTuple([result.Namespace, result.Unknown.Cast<object?>().ToList()]);
        }

        public string FormatHelp()
        {
            var sb = new StringBuilder();
            sb.Append("usage: ").Append(_prog);
            foreach (var arg in _arguments)
                sb.Append(arg.IsOption ? $" [{arg.DisplayName}]" : $" {arg.Dest}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(_description))
                sb.AppendLine(_description);
            return sb.ToString();
        }

        public object? PrintHelp()
        {
            _context.Output.Write(FormatHelp());
            return null;
        }

        public object? Error(IReadOnlyList<object?> args)
        {
            Fail(PythonOps.ToString(Arg(args, 0, "error")));
            return null;
        }

        private (PythonNamespace Namespace, List<string> Unknown) Parse(IReadOnlyList<object?> methodArgs, bool allowUnknown)
        {
            var input = methodArgs.Count > 0 && methodArgs[0] is not null
                ? PythonOps.Iterate(methodArgs[0]).Select(PythonOps.ToString).ToList()
                : CurrentArgvTail();
            var values = BuildDefaults();
            var unknown = new List<string>();
            var positionals = _arguments.Where(static arg => !arg.IsOption).ToList();
            var positionalIndex = 0;

            for (var index = 0; index < input.Count;)
            {
                var token = input[index];
                if (token == "--")
                {
                    index++;
                    while (index < input.Count)
                        ConsumePositional(input[index++], values, positionals, ref positionalIndex, unknown, allowUnknown);
                    break;
                }

                if (token.StartsWith("-", StringComparison.Ordinal) && token != "-")
                {
                    var optionName = token;
                    string? inlineValue = null;
                    var equals = token.IndexOf('=');
                    if (equals > 0)
                    {
                        optionName = token[..equals];
                        inlineValue = token[(equals + 1)..];
                    }

                    var spec = _arguments.FirstOrDefault(arg => arg.IsOption && arg.Names.Contains(optionName, StringComparer.Ordinal));
                    if (spec is null)
                    {
                        if (!allowUnknown)
                            Fail($"unrecognized arguments: {token}");
                        unknown.Add(token);
                        index++;
                        if (inlineValue is null && index < input.Count && !input[index].StartsWith("-", StringComparison.Ordinal))
                            unknown.Add(input[index++]);
                        continue;
                    }

                    index++;
                    ApplyOption(spec, inlineValue, input, ref index, values);
                    continue;
                }

                ConsumePositional(token, values, positionals, ref positionalIndex, unknown, allowUnknown);
                index++;
            }

            foreach (var positional in positionals.Skip(positionalIndex))
            {
                if (positional.Nargs is "?" or "*")
                    continue;
                Fail($"the following arguments are required: {positional.Dest}");
            }

            foreach (var option in _arguments.Where(static arg => arg.IsOption && arg.Required))
            {
                if (!values.ContainsKey(option.Dest) || values[option.Dest] is null)
                    Fail($"the following arguments are required: {option.DisplayName}");
            }

            return (new PythonNamespace(values), unknown);
        }

        private List<string> CurrentArgvTail()
            => (_context.GetModule("sys").Members["argv"] as List<object?> ?? new List<object?>())
                .Skip(1)
                .Select(PythonOps.ToString)
                .ToList();

        private Dictionary<string, object?> BuildDefaults()
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var arg in _arguments)
            {
                if (arg.HasDefault)
                    values[arg.Dest] = arg.DefaultValue;
                else if (arg.Action == "store_true")
                    values[arg.Dest] = false;
                else if (arg.Action == "store_false")
                    values[arg.Dest] = true;
                else if (arg.Action == "count")
                    values[arg.Dest] = 0L;
                else
                    values[arg.Dest] = null;
            }

            return values;
        }

        private void ApplyOption(PythonArgumentSpec spec, string? inlineValue, IReadOnlyList<string> input, ref int index, Dictionary<string, object?> values)
        {
            switch (spec.Action)
            {
                case "store_true":
                    values[spec.Dest] = true;
                    return;
                case "store_false":
                    values[spec.Dest] = false;
                    return;
                case "count":
                    values[spec.Dest] = PythonOps.ToLong(values.TryGetValue(spec.Dest, out var current) ? current : 0L) + 1;
                    return;
            }

            var consumed = ConsumeValues(spec, inlineValue, input, ref index, option: true);
            var value = ShapeValue(spec, consumed);
            if (spec.Action == "append")
            {
                if (!values.TryGetValue(spec.Dest, out var existing) || existing is not List<object?> list)
                {
                    list = new List<object?>();
                    values[spec.Dest] = list;
                }
                list.Add(value);
                return;
            }

            if (spec.Action != "store")
                Fail($"unsupported action: {spec.Action}");
            values[spec.Dest] = value;
        }

        private void ConsumePositional(
            string token,
            Dictionary<string, object?> values,
            IReadOnlyList<PythonArgumentSpec> positionals,
            ref int positionalIndex,
            List<string> unknown,
            bool allowUnknown)
        {
            if (positionalIndex >= positionals.Count)
            {
                if (!allowUnknown)
                    Fail($"unrecognized arguments: {token}");
                unknown.Add(token);
                return;
            }

            var spec = positionals[positionalIndex];
            var value = ConvertValue(spec, token);
            if (spec.Nargs is "*" or "+")
            {
                if (values[spec.Dest] is not List<object?> list)
                {
                    list = new List<object?>();
                    values[spec.Dest] = list;
                }
                list.Add(value);
                return;
            }

            values[spec.Dest] = value;
            positionalIndex++;
        }

        private IReadOnlyList<object?> ConsumeValues(PythonArgumentSpec spec, string? inlineValue, IReadOnlyList<string> input, ref int index, bool option)
        {
            if (inlineValue is not null)
                return [ConvertValue(spec, inlineValue)];

            var result = new List<object?>();
            var nargs = spec.Nargs;
            var minimum = nargs switch
            {
                "?" or "*" => 0,
                "+" => 1,
                null => 1,
                _ when int.TryParse(nargs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 1,
            };
            var maximum = nargs switch
            {
                "?" => 1,
                "*" or "+" => int.MaxValue,
                null => 1,
                _ when int.TryParse(nargs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 1,
            };

            while (index < input.Count && result.Count < maximum)
            {
                if (option && input[index].StartsWith("-", StringComparison.Ordinal) && result.Count >= minimum)
                    break;
                result.Add(ConvertValue(spec, input[index++]));
                if (nargs is null)
                    break;
            }

            if (result.Count < minimum)
                Fail($"argument {spec.DisplayName}: expected at least {minimum} value(s)");
            return result;
        }

        private object? ShapeValue(PythonArgumentSpec spec, IReadOnlyList<object?> values)
        {
            if (spec.Nargs is "*" or "+" || int.TryParse(spec.Nargs, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return values.ToList();
            if (spec.Nargs == "?")
                return values.Count == 0 ? spec.DefaultValue : values[0];
            return values.Count == 0 ? null : values[0];
        }

        private object? ConvertValue(PythonArgumentSpec spec, string value)
        {
            object? converted = value;
            if (spec.Type is not null)
                converted = PythonOps.Call(spec.Type, _context, [value], new Dictionary<string, object?>(StringComparer.Ordinal));
            if (spec.Choices is not null && !PythonOps.Iterate(spec.Choices).Any(choice => PythonOps.CompareEqual(choice, converted)))
                Fail($"argument {spec.DisplayName}: invalid choice: {value}");
            return converted;
        }

        private void Fail(string message)
        {
            _context.Error.Write(FormatHelp());
            _context.Error.WriteLine($"{_prog}: error: {message}");
            throw new PythonSystemExit(2);
        }

        private static string InferDest(IReadOnlyList<string> names, bool isOption)
        {
            if (!isOption)
                return names[0].Replace('-', '_');
            var longName = names.Where(static name => name.StartsWith("--", StringComparison.Ordinal)).OrderByDescending(static name => name.Length).FirstOrDefault();
            var chosen = longName ?? names[0];
            return chosen.TrimStart('-').Replace('-', '_');
        }
    }

    private sealed record PythonArgumentSpec(
        IReadOnlyList<string> Names,
        string Dest,
        bool IsOption,
        string Action,
        string? Nargs,
        bool Required,
        bool HasDefault,
        object? DefaultValue,
        object? Type,
        object? Choices)
    {
        public string DisplayName => IsOption ? string.Join("/", Names) : Dest;
    }

    private sealed class PythonNamespace
    {
        public Dictionary<string, object?> Values { get; }
        public PythonNamespace(Dictionary<string, object?> values) { Values = values; }
        public override string ToString()
            => "Namespace(" + string.Join(", ", Values.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={PythonOps.Repr(kv.Value)}")) + ")";
    }

    private sealed record PythonCompletedProcess(IReadOnlyList<object?> Args, int ReturnCode, string Stdout, string Stderr);
    private sealed record PythonType(string Name);
    private sealed record PythonMatch(Match Match)
    {
        public string Group(int index = 0) => Match.Groups[index].Value;
        public List<object?> Groups() => Match.Groups.Cast<Group>().Skip(1).Select(g => (object?)g.Value).ToList();
        public override string ToString() => Match.Success ? $"<re.Match object; span=({Match.Index}, {Match.Index + Match.Length}), match='{Match.Value}'>" : "None";
    }

    private static class PythonOps
    {
        public static object? Call(object? callable, PythonContext context, IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
        {
            if (callable is IPythonCallable fn) return fn.Invoke(context, args, kwargs);
            if (callable is BoundMethod bound) return bound.Invoke(args, kwargs);
            throw new PythonException("TypeError", $"object is not callable: {Repr(callable)}");
        }

        public static object? CallMethod(object? target, string method, IReadOnlyList<object?> args, Dictionary<string, object?> kwargs)
        {
            var attr = GetAttr(target, method);
            if (attr is BoundMethod bound) return bound.Invoke(args, kwargs);
            if (attr is IPythonCallable fn) return fn.Invoke(null!, args, kwargs);
            throw new PythonException("TypeError", $"attribute '{method}' is not callable");
        }

        public static object? GetAttr(object? target, string name)
        {
            if (target is PythonModule module && module.Members.TryGetValue(name, out var value)) return value;
            if (target is PythonFile file)
            {
                return name switch
                {
                    "read" => new BoundMethod((args, _) => file.Read()),
                    "write" => new BoundMethod((args, _) => file.Write(ToString(Arg(args, 0, "write")))),
                    "close" => new BoundMethod((_, _) => { file.Close(); return null; }),
                    "__enter__" => new BoundMethod((_, _) => file),
                    "__exit__" => new BoundMethod((_, _) => { file.Close(); return null; }),
                    _ => throw Attr(name),
                };
            }
            if (target is PythonPath path)
            {
                return name switch
                {
                    "read_text" => new BoundMethod((_, _) => path.ReadText()),
                    "write_text" => new BoundMethod((args, _) => path.WriteText(ToString(Arg(args, 0, "write_text")))),
                    "exists" => new BoundMethod((_, _) => path.Exists()),
                    "is_file" => new BoundMethod((_, _) => path.IsFile()),
                    "is_dir" => new BoundMethod((_, _) => path.IsDir()),
                    "iterdir" => new BoundMethod((_, _) => path.IterDir()),
                    "name" => VfsPath.SplitLeaf(path.Normalized).Leaf,
                    "parent" => new PythonPath(path.Context, VfsPath.SplitLeaf(path.Normalized).Parent),
                    _ => throw Attr(name),
                };
            }
            if (target is PythonCompletedProcess completed)
            {
                return name switch
                {
                    "args" => completed.Args,
                    "returncode" => completed.ReturnCode,
                    "stdout" => completed.Stdout,
                    "stderr" => completed.Stderr,
                    _ => throw Attr(name),
                };
            }
            if (target is PythonArgumentParser parser)
            {
                return name switch
                {
                    "add_argument" => new BoundMethod(parser.AddArgument),
                    "parse_args" => new BoundMethod(parser.ParseArgs),
                    "parse_known_args" => new BoundMethod(parser.ParseKnownArgs),
                    "format_help" => new BoundMethod((_, _) => parser.FormatHelp()),
                    "print_help" => new BoundMethod((_, _) => parser.PrintHelp()),
                    "error" => new BoundMethod((args, _) => parser.Error(args)),
                    _ => throw Attr(name),
                };
            }
            if (target is PythonNamespace ns)
            {
                if (ns.Values.TryGetValue(name, out var nsValue)) return nsValue;
                throw Attr(name);
            }
            if (target is PythonMatch match)
            {
                return name switch
                {
                    "group" => new BoundMethod((args, _) => match.Group(args.Count == 0 ? 0 : (int)ToLong(args[0]))),
                    "groups" => new BoundMethod((_, _) => match.Groups()),
                    _ => throw Attr(name),
                };
            }
            if (target is string s)
            {
                return name switch
                {
                    "strip" => new BoundMethod((_, _) => s.Trim()),
                    "lower" => new BoundMethod((_, _) => s.ToLowerInvariant()),
                    "upper" => new BoundMethod((_, _) => s.ToUpperInvariant()),
                    "split" => new BoundMethod((args, _) => SplitString(s, args)),
                    "replace" => new BoundMethod((args, _) => s.Replace(ToString(Arg(args, 0, "replace")), ToString(Arg(args, 1, "replace")), StringComparison.Ordinal)),
                    "startswith" => new BoundMethod((args, _) => s.StartsWith(ToString(Arg(args, 0, "startswith")), StringComparison.Ordinal)),
                    "endswith" => new BoundMethod((args, _) => s.EndsWith(ToString(Arg(args, 0, "endswith")), StringComparison.Ordinal)),
                    "join" => new BoundMethod((args, _) => string.Join(s, Iterate(Arg(args, 0, "join")).Select(ToString))),
                    _ => throw Attr(name),
                };
            }
            if (target is List<object?> list)
            {
                return name switch
                {
                    "append" => new BoundMethod((args, _) => { list.Add(args.Count > 0 ? args[0] : null); return null; }),
                    "extend" => new BoundMethod((args, _) => { list.AddRange(Iterate(Arg(args, 0, "extend"))); return null; }),
                    "pop" => new BoundMethod((_, _) => { var value = list[^1]; list.RemoveAt(list.Count - 1); return value; }),
                    _ => throw Attr(name),
                };
            }
            if (target is Dictionary<string, object?> dict)
            {
                return name switch
                {
                    "get" => new BoundMethod((args, _) => dict.TryGetValue(ToString(Arg(args, 0, "get")), out var value) ? value : args.Count > 1 ? args[1] : null),
                    "keys" => new BoundMethod((_, _) => dict.Keys.Cast<object?>().ToList()),
                    "values" => new BoundMethod((_, _) => dict.Values.ToList()),
                    "items" => new BoundMethod((_, _) => dict.Select(kv => (object?)new PythonTuple([kv.Key, kv.Value])).ToList()),
                    _ => throw Attr(name),
                };
            }
            throw Attr(name);

            static PythonException Attr(string attr) => new("AttributeError", $"object has no attribute '{attr}'");
        }

        private static List<object?> SplitString(string s, IReadOnlyList<object?> args)
            => args.Count == 0
                ? Regex.Split(s.Trim(), @"\s+").Where(part => part.Length > 0).Cast<object?>().ToList()
                : s.Split(ToString(args[0]), StringSplitOptions.None).Cast<object?>().ToList();

        public static object? GetIndex(object? target, object? index)
        {
            if (target is List<object?> list) return list[(int)ToLong(index)];
            if (target is PythonTuple tuple) return tuple.Values[(int)ToLong(index)];
            if (target is string s) return s[(int)ToLong(index)].ToString();
            if (target is Dictionary<string, object?> dict && dict.TryGetValue(ToString(index), out var value)) return value;
            throw new PythonException("TypeError", "object is not subscriptable");
        }

        public static void AssignTarget(PythonContext context, string target, object? value)
        {
            if (target.Contains('.', StringComparison.Ordinal))
            {
                var parts = target.Split('.', 2);
                var obj = context.GetName(parts[0]);
                if (obj is PythonModule module) { module.Members[parts[1]] = value; return; }
            }
            throw new PythonException("SyntaxError", $"unsupported assignment target: {target}");
        }

        public static IEnumerable<object?> Iterate(object? value)
        {
            return value switch
            {
                null => [],
                PythonRange range => range.Values(),
                PythonTuple tuple => tuple.Values,
                List<object?> list => list,
                string s => s.Select(ch => (object?)ch.ToString()),
                Dictionary<string, object?> dict => dict.Keys.Cast<object?>(),
                HashSet<object?> set => set,
                IEnumerable<object?> enumerable => enumerable,
                _ => throw new PythonException("TypeError", $"object is not iterable: {Repr(value)}"),
            };
        }

        public static bool IsTruthy(object? value)
            => value switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                long l => l != 0,
                int i => i != 0,
                double d => d != 0,
                List<object?> list => list.Count > 0,
                Dictionary<string, object?> dict => dict.Count > 0,
                PythonTuple tuple => tuple.Values.Count > 0,
                _ => true,
            };

        public static object? Add(object? a, object? b)
        {
            if (a is string || b is string) return ToString(a) + ToString(b);
            if (a is List<object?> la && b is List<object?> lb) return la.Concat(lb).ToList();
            return IsFloaty(a) || IsFloaty(b) ? ToDouble(a) + ToDouble(b) : ToLong(a) + ToLong(b);
        }
        public static object? Subtract(object? a, object? b) => IsFloaty(a) || IsFloaty(b) ? ToDouble(a) - ToDouble(b) : ToLong(a) - ToLong(b);
        public static object? Multiply(object? a, object? b)
        {
            if (a is string s && b is not string) return string.Concat(Enumerable.Repeat(s, (int)ToLong(b)));
            if (a is List<object?> list && b is not string) return Enumerable.Range(0, (int)ToLong(b)).SelectMany(_ => list).ToList();
            return IsFloaty(a) || IsFloaty(b) ? ToDouble(a) * ToDouble(b) : ToLong(a) * ToLong(b);
        }
        public static double Divide(object? a, object? b) => ToDouble(a) / ToDouble(b);
        public static object? Mod(object? a, object? b) => IsFloaty(a) || IsFloaty(b) ? ToDouble(a) % ToDouble(b) : ToLong(a) % ToLong(b);
        public static bool IsFloaty(object? value) => value is double or float or decimal;
        public static long ToLong(object? value) => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            bool b => b ? 1 : 0,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
            null => 0,
            _ => throw new PythonException("TypeError", $"cannot convert to int: {Repr(value)}"),
        };
        public static double ToDouble(object? value) => value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            null => 0,
            _ => throw new PythonException("TypeError", $"cannot convert to float: {Repr(value)}"),
        };
        public static string ToString(object? value) => value switch
        {
            null => "None",
            bool b => b ? "True" : "False",
            string s => s,
            PythonModule m => m.ToString(),
            PythonPath p => p.ToString(),
            PythonNamespace ns => ns.ToString(),
            PythonTuple t => "(" + string.Join(", ", t.Values.Select(Repr)) + (t.Values.Count == 1 ? "," : "") + ")",
            List<object?> list => "[" + string.Join(", ", list.Select(Repr)) + "]",
            Dictionary<string, object?> dict => "{" + string.Join(", ", dict.Select(kv => Repr(kv.Key) + ": " + Repr(kv.Value))) + "}",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        public static string Repr(object? value) => value switch
        {
            null => "None",
            bool b => b ? "True" : "False",
            string s => "'" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "'",
            _ => ToString(value),
        };
        public static int Length(object? value) => value switch
        {
            string s => s.Length,
            List<object?> list => list.Count,
            Dictionary<string, object?> dict => dict.Count,
            PythonTuple tuple => tuple.Values.Count,
            PythonRange range => range.Values().Count(),
            _ => throw new PythonException("TypeError", "object has no len()"),
        };
        public static int Compare(object? a, object? b)
        {
            if (a is string || b is string) return string.CompareOrdinal(ToString(a), ToString(b));
            return ToDouble(a).CompareTo(ToDouble(b));
        }
        public static bool CompareEqual(object? a, object? b)
        {
            if (a is null || b is null) return a is null && b is null;
            if (IsFloaty(a) || IsFloaty(b) || a is long or int || b is long or int) return Math.Abs(ToDouble(a) - ToDouble(b)) < 0.0000001;
            return a.Equals(b);
        }
        public static bool Contains(object? container, object? item)
        {
            if (container is string s) return s.Contains(ToString(item), StringComparison.Ordinal);
            return Iterate(container).Any(value => CompareEqual(value, item));
        }
        public static object? MinMax(IReadOnlyList<object?> args, bool min)
        {
            var values = args.Count == 1 ? Iterate(args[0]).ToList() : args.ToList();
            if (values.Count == 0) throw new PythonException("ValueError", "empty sequence");
            return min ? values.OrderBy(ToString, StringComparer.Ordinal).First() : values.OrderBy(ToString, StringComparer.Ordinal).Last();
        }
        public static List<object?> Zip(IReadOnlyList<object?> args)
        {
            var lists = args.Select(arg => Iterate(arg).ToList()).ToList();
            var count = lists.Count == 0 ? 0 : lists.Min(list => list.Count);
            var result = new List<object?>();
            for (var i = 0; i < count; i++)
                result.Add(new PythonTuple(lists.Select(list => list[i]).ToList()));
            return result;
        }
        public static Dictionary<string, object?> ToDictionary(object? value)
        {
            if (value is Dictionary<string, object?> dict) return new Dictionary<string, object?>(dict, StringComparer.Ordinal);
            throw new PythonException("TypeError", "cannot convert object to dict");
        }
        public static bool IsInstance(object? value, object? type)
        {
            if (type is PythonType pyType)
            {
                return pyType.Name switch
                {
                    "str" => value is string,
                    "int" => value is int or long,
                    "float" => value is double or float,
                    "list" => value is List<object?>,
                    "dict" => value is Dictionary<string, object?>,
                    _ => value is PythonException,
                };
            }
            return false;
        }
        public static List<object?> Walk(VirtualFileSystem vfs, string root)
        {
            var result = new List<object?>();
            WalkDirectory(vfs, vfs.Normalize(root), result);
            return result;
        }
        private static void WalkDirectory(VirtualFileSystem vfs, string path, List<object?> result)
        {
            var dirs = new List<object?>();
            var files = new List<object?>();
            foreach (var child in vfs.List(path, false, null))
            {
                if (child is VfsDirectory) dirs.Add(child.Name);
                else files.Add(child.Name);
            }
            result.Add(new PythonTuple([path, dirs, files]));
            foreach (var dir in dirs.Cast<string>())
                WalkDirectory(vfs, VfsPath.Join(path, dir), result);
        }
    }

    private sealed class BoundMethod
    {
        private readonly Func<IReadOnlyList<object?>, Dictionary<string, object?>, object?> _body;
        public BoundMethod(Func<IReadOnlyList<object?>, Dictionary<string, object?>, object?> body) { _body = body; }
        public object? Invoke(IReadOnlyList<object?> args, Dictionary<string, object?> kwargs) => _body(args, kwargs);
    }

    private static class PythonJson
    {
        public static object? FromJson(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
                _ => null,
            };
        }

        public static string ToJson(object? value, bool indented)
            => JsonSerializer.Serialize(ToJsonValue(value), new JsonSerializerOptions { WriteIndented = indented });

        private static object? ToJsonValue(object? value)
            => value switch
            {
                PythonTuple t => t.Values.Select(ToJsonValue).ToList(),
                List<object?> list => list.Select(ToJsonValue).ToList(),
                Dictionary<string, object?> dict => dict.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value), StringComparer.Ordinal),
                _ => value,
            };
    }

    private static class PythonRegex
    {
        public static object? Search(IReadOnlyList<object?> args)
        {
            var match = Regex.Match(PythonOps.ToString(Arg(args, 1, "search")), PythonOps.ToString(Arg(args, 0, "search")));
            return match.Success ? new PythonMatch(match) : null;
        }
        public static object? Match(IReadOnlyList<object?> args)
        {
            var match = Regex.Match(PythonOps.ToString(Arg(args, 1, "match")), "^" + PythonOps.ToString(Arg(args, 0, "match")));
            return match.Success ? new PythonMatch(match) : null;
        }
        public static object? FullMatch(IReadOnlyList<object?> args)
        {
            var match = Regex.Match(PythonOps.ToString(Arg(args, 1, "fullmatch")), "^(?:" + PythonOps.ToString(Arg(args, 0, "fullmatch")) + ")$");
            return match.Success ? new PythonMatch(match) : null;
        }
    }

    private static class PythonGlob
    {
        public static IEnumerable<string> Glob(VirtualFileSystem vfs, string pattern)
        {
            var normalized = vfs.Normalize(pattern);
            var parent = VfsPath.SplitLeaf(normalized).Parent;
            var leaf = VfsPath.SplitLeaf(normalized).Leaf;
            if (leaf.Contains("**", StringComparison.Ordinal))
                return vfs.List(parent, true, null, filesOnly: false).Where(n => Match(n.Name, leaf.Replace("**/", "", StringComparison.Ordinal))).Select(n => n.AbsolutePath);
            return vfs.Resolve(parent) is VfsDirectory
                ? vfs.List(parent, false, null).Where(n => Match(n.Name, leaf)).Select(n => n.AbsolutePath)
                : [];
        }
        public static bool Match(string name, string pattern)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
            return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
        }
    }

    private static class PythonText
    {
        public static string Dedent(string text)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var nonBlank = lines.Where(line => line.Trim().Length > 0).ToArray();
            var indent = nonBlank.Length == 0 ? 0 : nonBlank.Min(CountIndent);
            return string.Join("\n", lines.Select(line => line.Length >= indent ? line[indent..] : line));
        }
    }

    private class PythonException : Exception
    {
        public string Name { get; }
        public PythonException(string name, string message) : base(message) { Name = name; }
    }
    private sealed class PythonSyntaxException : PythonException
    {
        public PythonSyntaxException(PythonLine line, string message) : base("SyntaxError", $"{line.Path}:{line.Number}: {message}") { }
    }
    private sealed class PythonSystemExit : Exception
    {
        public int ExitCode { get; }
        public PythonSystemExit(int exitCode) { ExitCode = exitCode; }
    }
    private sealed class PythonReturn : Exception
    {
        public object? Value { get; }
        public PythonReturn(object? value) { Value = value; }
    }
    private sealed class PythonBreak : Exception { }
    private sealed class PythonContinue : Exception { }

    private static object? Arg(IReadOnlyList<object?> args, int index, string function)
        => index < args.Count ? args[index] : throw new PythonException("TypeError", $"{function}() missing required argument");

    private static int CountIndent(string line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') count++;
            else if (ch == '\t') count += 4;
            else break;
        }
        return count;
    }
}
