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
    private const string PerlVersion = "perl 5-compatible Carbide subset";
    private const string PerlDetailedVersion = "perl 5-compatible Carbide subset (CarbidePerl 0.1)";

    private static int ExecutePerl(VirtualExecutableInvocation invocation)
        => new PerlCommand(invocation).Execute();

    private sealed class PerlCommand
    {
        private readonly VirtualExecutableInvocation _invocation;

        public PerlCommand(VirtualExecutableInvocation invocation)
        {
            _invocation = invocation;
        }

        public int Execute()
        {
            var options = PerlCommandLine.Parse(_invocation.Args);
            if (options.Error is not null)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {options.Error}");
                return 2;
            }

            if (options.ShowHelp)
            {
                WriteHelp();
                return 0;
            }

            if (options.ShowVersion)
            {
                _invocation.Output.WriteLine(PerlDetailedVersion);
                return 0;
            }

            var runtime = new PerlRuntime(_invocation, options);
            try
            {
                return runtime.Run();
            }
            catch (PerlExitException ex)
            {
                return ex.ExitCode;
            }
            catch (PerlException ex)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {ex.Message}");
                return 1;
            }
            catch (VfsException ex)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {ex.Message}");
                return 1;
            }
        }

        private void WriteHelp()
        {
            _invocation.Output.WriteLine("usage: perl [switches] [--] [-e program | programfile] [arguments]");
            _invocation.Output.WriteLine("Carbide Perl is a sandboxed perl 5-compatible subset.");
            _invocation.Output.WriteLine();
            _invocation.Output.WriteLine("Supported switches:");
            _invocation.Output.WriteLine("  -e program       one line of program text; may be repeated");
            _invocation.Output.WriteLine("  -n, -p           implicit input loop; -p prints $_ after each iteration");
            _invocation.Output.WriteLine("  -a, -Fpattern    autosplit input into @F for -n/-p");
            _invocation.Output.WriteLine("  -0[octal], -c, -d, -Ipath, -l, -Mmodule, -mmodule, -s, -v, -w");
            _invocation.Output.WriteLine("  -de 0            enter Carbide's debugger-style Perl pseudo-REPL");
            _invocation.Output.WriteLine();
            _invocation.Output.WriteLine("Modules: strict, warnings, Getopt::Long, JSON::PP, File::Basename,");
            _invocation.Output.WriteLine("File::Spec, Cwd, File::Path, File::Copy, FindBin, POSIX.");
        }
    }

    private sealed record PerlCommandLine(
        IReadOnlyList<string> CodeSegments,
        string? ScriptPath,
        IReadOnlyList<string> ProgramArgs,
        IReadOnlyList<string> IncludePaths,
        IReadOnlyList<string> Modules,
        bool Loop,
        bool PrintLoop,
        bool Autosplit,
        bool LineEnding,
        bool SyntaxCheck,
        bool Debugger,
        bool SwitchParsing,
        bool Warnings,
        bool ShowVersion,
        bool ShowHelp,
        string SplitPattern,
        string? RecordSeparator,
        string? Error)
    {
        public bool HasProgram => CodeSegments.Count > 0 || ScriptPath is not null;
        public string CombinedCode => string.Join("\n", CodeSegments);

        public static PerlCommandLine Parse(IReadOnlyList<string> args)
        {
            var code = new List<string>();
            string? scriptPath = null;
            var programArgs = new List<string>();
            var includePaths = new List<string>();
            var modules = new List<string>();
            bool loop = false;
            bool printLoop = false;
            bool autosplit = false;
            bool lineEnding = false;
            bool syntaxCheck = false;
            bool debugger = false;
            bool switchParsing = false;
            bool warnings = false;
            bool showVersion = false;
            bool showHelp = false;
            var splitPattern = @"\s+";
            string? recordSeparator = "\n";

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg == "--")
                {
                    for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                    break;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal) || arg == "-")
                {
                    if (code.Count == 0)
                    {
                        scriptPath = arg;
                        for (i++; i < args.Count; i++) programArgs.Add(args[i]);
                    }
                    else
                    {
                        for (; i < args.Count; i++) programArgs.Add(args[i]);
                    }
                    break;
                }

                if (arg is "--help" or "-h" or "-?")
                {
                    showHelp = true;
                    continue;
                }

                if (arg == "--version")
                {
                    showVersion = true;
                    continue;
                }

                if (arg is "-T" or "-x" or "-S" or "-i")
                    return ErrorResult($"unsupported switch: {arg}");

                for (var j = 1; j < arg.Length; j++)
                {
                    var sw = arg[j];
                    var rest = j + 1 < arg.Length ? arg[(j + 1)..] : "";
                    switch (sw)
                    {
                        case '0':
                            if (rest.Length == 0)
                            {
                                recordSeparator = "\0";
                            }
                            else if (rest == "777")
                            {
                                recordSeparator = null;
                                j = arg.Length;
                            }
                            else
                            {
                                recordSeparator = "\0";
                                j = arg.Length;
                            }
                            break;
                        case 'a':
                            autosplit = true;
                            break;
                        case 'c':
                            syntaxCheck = true;
                            break;
                        case 'd':
                            debugger = true;
                            break;
                        case 'e':
                            if (rest.Length > 0)
                            {
                                code.Add(rest);
                                j = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Count)
                                    return ErrorResult("argument expected for -e");
                                code.Add(args[++i]);
                            }
                            break;
                        case 'F':
                            if (rest.Length > 0)
                            {
                                splitPattern = rest;
                                j = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Count)
                                    return ErrorResult("argument expected for -F");
                                splitPattern = args[++i];
                            }
                            break;
                        case 'I':
                            if (rest.Length > 0)
                            {
                                includePaths.Add(rest);
                                j = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Count)
                                    return ErrorResult("argument expected for -I");
                                includePaths.Add(args[++i]);
                            }
                            break;
                        case 'l':
                            lineEnding = true;
                            break;
                        case 'M':
                        case 'm':
                            if (rest.Length > 0)
                            {
                                modules.Add(TrimModuleImport(rest));
                                j = arg.Length;
                            }
                            else
                            {
                                if (i + 1 >= args.Count)
                                    return ErrorResult($"argument expected for -{sw}");
                                modules.Add(TrimModuleImport(args[++i]));
                            }
                            break;
                        case 'n':
                            loop = true;
                            break;
                        case 'p':
                            loop = true;
                            printLoop = true;
                            break;
                        case 's':
                            switchParsing = true;
                            break;
                        case 'v':
                            showVersion = true;
                            break;
                        case 'w':
                            warnings = true;
                            break;
                        default:
                            return ErrorResult($"unsupported switch: -{sw}");
                    }
                }
            }

            return new PerlCommandLine(
                code,
                scriptPath,
                programArgs,
                includePaths,
                modules,
                loop,
                printLoop,
                autosplit,
                lineEnding,
                syntaxCheck,
                debugger,
                switchParsing,
                warnings,
                showVersion,
                showHelp,
                splitPattern,
                recordSeparator,
                null);
        }

        private static string TrimModuleImport(string value)
        {
            var equals = value.IndexOf('=');
            if (equals >= 0) value = value[..equals];
            return value.Trim();
        }

        private static PerlCommandLine ErrorResult(string error)
            => new(
                Array.Empty<string>(),
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                @"\s+",
                "\n",
                error);
    }

    private sealed class PerlRuntime
    {
        private readonly VirtualExecutableInvocation _invocation;
        private readonly PerlCommandLine _options;
        private readonly PerlContext _context;

        public PerlRuntime(VirtualExecutableInvocation invocation, PerlCommandLine options)
        {
            _invocation = invocation;
            _options = options;
            _context = new PerlContext(invocation, options);
        }

        public int Run()
        {
            foreach (var module in _options.Modules)
                _context.ImportModule(module);

            if (_options.SyntaxCheck)
            {
                _ = LoadProgramText();
                _invocation.Output.WriteLine($"{(_options.ScriptPath ?? "-e")} syntax OK");
                return 0;
            }

            if (_options.Debugger && _options.CodeSegments.Count > 0 && _options.CombinedCode.Trim() == "0")
                return RunDebugger();

            if (_options.Loop)
            {
                if (_options.CodeSegments.Count == 0)
                    throw new PerlException("implicit loop requires -e program text");
                RunImplicitLoop(_options.CombinedCode);
                return 0;
            }

            if (_options.CodeSegments.Count > 0)
                return ExecuteSource(_options.CombinedCode, "-e");

            if (_options.ScriptPath is not null)
            {
                var source = LoadScript(_options.ScriptPath, out var abs);
                _context.SetScalar("0", abs);
                return ExecuteSource(source, abs);
            }

            return _options.Debugger ? RunDebugger() : 0;
        }

        private int ExecuteSource(string source, string path)
        {
            PerlProgram.Execute(source, path, _context);
            return 0;
        }

        private string LoadProgramText()
        {
            if (_options.CodeSegments.Count > 0)
                return _options.CombinedCode;
            if (_options.ScriptPath is not null)
                return LoadScript(_options.ScriptPath, out _);
            return "";
        }

        private string LoadScript(string path, out string absolutePath)
        {
            absolutePath = _invocation.Vfs.Normalize(path);
            if (_invocation.Vfs.Resolve(absolutePath) is not VfsFile file)
                throw new PerlException($"No such file: '{path}'");
            return file.ReadText();
        }

        private void RunImplicitLoop(string source)
        {
            var recordNumber = 0L;
            foreach (var record in ReadInputRecords())
            {
                recordNumber++;
                var current = record;
                if (_options.LineEnding)
                    current = ChompOne(current);
                _context.SetScalar("_", current);
                _context.SetScalar(".", recordNumber);
                if (_options.Autosplit)
                    _context.SetArray("F", Autosplit(current));

                PerlProgram.Execute(source, "-e", _context);
                if (_options.PrintLoop)
                    _context.Print([_context.GetScalar("_")]);
            }
        }

        private IEnumerable<string> ReadInputRecords()
        {
            if (_options.ProgramArgs.Count == 0)
            {
                foreach (var record in SplitRecords(ReadAllText(_invocation.Input), _options.RecordSeparator))
                    yield return record;
                yield break;
            }

            foreach (var arg in _options.ProgramArgs)
            {
                var abs = _invocation.Vfs.Normalize(arg);
                if (_invocation.Vfs.Resolve(abs) is not VfsFile file)
                    throw new PerlException($"No such file: '{arg}'");
                foreach (var record in SplitRecords(file.ReadText(), _options.RecordSeparator))
                    yield return record;
            }
        }

        private List<object?> Autosplit(string text)
        {
            var pattern = _options.SplitPattern.Length == 0 ? @"\s+" : _options.SplitPattern;
            var normalized = pattern.StartsWith("/", StringComparison.Ordinal) && pattern.EndsWith("/", StringComparison.Ordinal)
                ? pattern[1..^1]
                : pattern;
            return Regex.Split(text.Trim(), normalized)
                .Where(static part => part.Length > 0)
                .Cast<object?>()
                .ToList();
        }

        private int RunDebugger()
        {
            _invocation.Output.WriteLine($"{PerlDetailedVersion} debugger pseudo-REPL");
            var commandNumber = 1;
            while (true)
            {
                _invocation.Output.Write($"DB<{commandNumber}> ");
                _invocation.Output.Flush();
                var line = _invocation.Input.ReadLine();
                if (line is null)
                    return 0;
                var trimmed = line.Trim();
                if (trimmed is "q" or "quit" or "exit")
                    return 0;
                if (trimmed.Length == 0)
                    continue;

                try
                {
                    if (trimmed.StartsWith("p ", StringComparison.Ordinal))
                    {
                        var value = PerlExpression.Evaluate(trimmed[2..], _context);
                        _invocation.Output.WriteLine(PerlContext.Stringify(value));
                    }
                    else
                    {
                        PerlProgram.Execute(trimmed, "(debugger)", _context);
                    }
                }
                catch (PerlExitException ex)
                {
                    return ex.ExitCode;
                }
                catch (PerlException ex)
                {
                    _invocation.Error.WriteLine($"perl debugger: {ex.Message}");
                }
                commandNumber++;
            }
        }

        private static IEnumerable<string> SplitRecords(string text, string? separator)
        {
            if (separator is null)
            {
                yield return text;
                yield break;
            }

            if (separator == "\n")
            {
                using var reader = new StringReader(text);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    yield return line + "\n";
                yield break;
            }

            foreach (var part in text.Split(separator, StringSplitOptions.None))
            {
                if (part.Length > 0)
                    yield return part + separator;
            }
        }

        private static string ChompOne(string text)
            => text.EndsWith("\r\n", StringComparison.Ordinal)
                ? text[..^2]
                : text.EndsWith('\n') || text.EndsWith('\r')
                    ? text[..^1]
                    : text;
    }

    private sealed class PerlContext
    {
        private readonly VirtualExecutableInvocation _invocation;
        private readonly PerlCommandLine _options;
        private readonly Dictionary<string, object?> _scalars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<object?>> _arrays = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object?>> _hashes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PerlFile> _fileHandles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _subs = new(StringComparer.Ordinal);

        public VirtualFileSystem Vfs => _invocation.Vfs;
        public TextReader Input => _invocation.Input;
        public TextWriter Output => _invocation.Output;
        public TextWriter Error => _invocation.Error;
        public string OutputRecordSeparator { get; set; }

        public PerlContext(VirtualExecutableInvocation invocation, PerlCommandLine options)
        {
            _invocation = invocation;
            _options = options;
            OutputRecordSeparator = options.LineEnding ? "\n" : "";

            SetScalar("_", "");
            SetScalar("0", options.ScriptPath ?? "-e");
            SetScalar(".", 0L);
            SetScalar("?", 0L);
            SetScalar("/", options.RecordSeparator ?? "");
            SetArray("ARGV", options.ProgramArgs.Cast<object?>().ToList());
            SetArray("INC", options.IncludePaths.Cast<object?>().Concat(["/usr/lib/carbide-perl"]).ToList());
            SetArray("F", []);
            SetHash("ENV", invocation.Env.All.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal));

            foreach (var arg in options.ProgramArgs.ToList())
            {
                if (!options.SwitchParsing || !arg.StartsWith("-", StringComparison.Ordinal) || arg == "-")
                    break;
                var name = arg.TrimStart('-').Replace('-', '_');
                SetScalar(name, true);
                _arrays["ARGV"].Remove(arg);
            }
        }

        public object? GetScalar(string name)
        {
            if (Regex.IsMatch(name, @"^\d+$") && _scalars.TryGetValue(name, out var capture))
                return capture;
            return _scalars.TryGetValue(name, out var value) ? value : null;
        }

        public void SetScalar(string name, object? value)
        {
            _scalars[name] = value;
        }

        public List<object?> GetArray(string name)
            => _arrays.TryGetValue(name, out var value) ? value : new List<object?>();

        public void SetArray(string name, List<object?> value)
        {
            _arrays[name] = value;
        }

        public Dictionary<string, object?> GetHash(string name)
            => _hashes.TryGetValue(name, out var value) ? value : new Dictionary<string, object?>(StringComparer.Ordinal);

        public void SetHash(string name, Dictionary<string, object?> value)
        {
            _hashes[name] = value;
        }

        public void RegisterSub(string name, string body)
        {
            _subs[name] = body;
        }

        public bool TryInvokeSub(string name, IReadOnlyList<object?> args, out object? result)
        {
            if (!_subs.TryGetValue(name, out var body))
            {
                result = null;
                return false;
            }

            var oldUnderscore = GetArray("_");
            SetArray("_", args.ToList());
            try
            {
                PerlProgram.Execute(body, $"sub {name}", this);
                result = null;
            }
            catch (PerlReturnException ex)
            {
                result = ex.Value;
            }
            finally
            {
                SetArray("_", oldUnderscore);
            }
            return true;
        }

        public void ImportModule(string module)
        {
            module = module.Trim();
            if (module is "" or "strict" or "warnings")
                return;
            if (module is "Getopt::Long" or "JSON::PP" or "File::Basename" or "File::Spec" or "Cwd" or "File::Path" or "File::Copy" or "FindBin" or "POSIX")
            {
                if (module == "FindBin")
                    SetScalar("FindBin::Bin", Vfs.CurrentLocation);
                return;
            }
            throw new PerlException($"unsupported module: {module}");
        }

        public object? CallFunction(string name, IReadOnlyList<object?> args)
        {
            if (TryInvokeSub(name, args, out var subResult))
                return subResult;

            var shortName = name.Contains("::", StringComparison.Ordinal)
                ? name[(name.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
                : name;

            return shortName switch
            {
                "print" => Print(args),
                "say" => Say(args),
                "chomp" => Chomp(args),
                "chop" => Chop(args),
                "split" => Split(args),
                "join" => string.Join(args.Count == 0 ? "" : Stringify(args[0]), Flatten(args.Skip(1)).Select(Stringify)),
                "length" => Stringify(Arg(args, 0, "length")).Length,
                "lc" => Stringify(Arg(args, 0, "lc")).ToLowerInvariant(),
                "uc" => Stringify(Arg(args, 0, "uc")).ToUpperInvariant(),
                "lcfirst" => ChangeFirst(Stringify(Arg(args, 0, "lcfirst")), upper: false),
                "ucfirst" => ChangeFirst(Stringify(Arg(args, 0, "ucfirst")), upper: true),
                "substr" => Substr(args),
                "index" => Stringify(Arg(args, 0, "index")).IndexOf(Stringify(Arg(args, 1, "index")), StringComparison.Ordinal),
                "rindex" => Stringify(Arg(args, 0, "rindex")).LastIndexOf(Stringify(Arg(args, 1, "rindex")), StringComparison.Ordinal),
                "sprintf" => Sprintf(args),
                "printf" => Print([Sprintf(args)]),
                "push" => Push(args),
                "pop" => Pop(args),
                "shift" => Shift(args),
                "unshift" => Unshift(args),
                "keys" => HashArg(args, "keys").Keys.Cast<object?>().ToList(),
                "values" => HashArg(args, "values").Values.ToList(),
                "exists" => Exists(args),
                "delete" => Delete(args),
                "open" => Open(args),
                "close" => Close(args),
                "readline" => ReadLine(args),
                "glob" => Glob(args),
                "sort" => Flatten(args).OrderBy(Stringify, StringComparer.Ordinal).ToList(),
                "reverse" => Flatten(args).Reverse().ToList(),
                "decode_json" => PerlJson.FromJson(JsonSerializer.Deserialize<JsonElement>(Stringify(Arg(args, 0, "decode_json")))),
                "encode_json" => PerlJson.ToJson(Arg(args, 0, "encode_json"), indented: false),
                "to_json" => PerlJson.ToJson(Arg(args, 0, "to_json"), indented: false),
                "from_json" => PerlJson.FromJson(JsonSerializer.Deserialize<JsonElement>(Stringify(Arg(args, 0, "from_json")))),
                "basename" => VfsPath.SplitLeaf(Vfs.Normalize(Stringify(Arg(args, 0, "basename")))).Leaf,
                "dirname" => VfsPath.SplitLeaf(Vfs.Normalize(Stringify(Arg(args, 0, "dirname")))).Parent,
                "catfile" => args.Select(Stringify).Aggregate(VfsPath.Join),
                "catdir" => args.Select(Stringify).Aggregate(VfsPath.Join),
                "getcwd" => Vfs.CurrentLocation,
                "cwd" => Vfs.CurrentLocation,
                "make_path" => MakePath(args),
                "copy" => Copy(args),
                "system" => System(args),
                "exit" => throw new PerlExitException(args.Count == 0 ? 0 : (int)ToLong(args[0])),
                "die" => throw new PerlException(args.Count == 0 ? "Died" : string.Join("", args.Select(Stringify))),
                "warn" => Warn(args),
                "strftime" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                _ => throw new PerlException($"unsupported function: {name}"),
            };
        }

        public object? Print(IReadOnlyList<object?> args)
        {
            if (args.Count == 0)
            {
                Output.Write(Stringify(GetScalar("_")));
                Output.Write(OutputRecordSeparator);
                return true;
            }

            if (args[0] is PerlFile file)
            {
                file.Write(string.Join("", args.Skip(1).Select(Stringify)));
                return true;
            }

            Output.Write(string.Join("", args.Select(Stringify)));
            Output.Write(OutputRecordSeparator);
            return true;
        }

        private object? Say(IReadOnlyList<object?> args)
        {
            Output.Write(string.Join("", args.Select(Stringify)));
            Output.WriteLine();
            return true;
        }

        private object? Warn(IReadOnlyList<object?> args)
        {
            Error.WriteLine(args.Count == 0 ? "Warning" : string.Join("", args.Select(Stringify)));
            return true;
        }

        public void SetCaptures(Match match)
        {
            for (var i = 1; i < match.Groups.Count; i++)
                SetScalar(i.ToString(CultureInfo.InvariantCulture), match.Groups[i].Value);
        }

        public bool MatchRegex(string text, string pattern, string flags = "")
        {
            var options = RegexOptions.None;
            if (flags.Contains('i', StringComparison.Ordinal)) options |= RegexOptions.IgnoreCase;
            if (flags.Contains('m', StringComparison.Ordinal)) options |= RegexOptions.Multiline;
            if (flags.Contains('s', StringComparison.Ordinal)) options |= RegexOptions.Singleline;
            var match = Regex.Match(text, pattern, options);
            if (match.Success) SetCaptures(match);
            return match.Success;
        }

        public bool Substitute(string target, string pattern, string replacement, string flags)
        {
            var current = Stringify(PerlExpression.Evaluate(target, this));
            var options = RegexOptions.None;
            if (flags.Contains('i', StringComparison.Ordinal)) options |= RegexOptions.IgnoreCase;
            if (flags.Contains('m', StringComparison.Ordinal)) options |= RegexOptions.Multiline;
            if (flags.Contains('s', StringComparison.Ordinal)) options |= RegexOptions.Singleline;
            var count = flags.Contains('g', StringComparison.Ordinal) ? int.MaxValue : 1;
            var changed = Regex.Replace(current, pattern, replacement, options, TimeSpan.FromSeconds(1));
            if (count == 1)
                changed = Regex.Replace(current, pattern, replacement, options, TimeSpan.FromSeconds(1));
            Assign(target, changed);
            return !string.Equals(current, changed, StringComparison.Ordinal);
        }

        public void Assign(string target, object? value)
        {
            target = StripDeclaration(target.Trim());
            if (target.StartsWith("$", StringComparison.Ordinal))
            {
                if (TryParseArrayElement(target, out var arrayName, out var arrayIndexExpression))
                {
                    var array = GetArray(arrayName);
                    var index = (int)ToLong(PerlExpression.Evaluate(arrayIndexExpression, this));
                    while (array.Count <= index) array.Add(null);
                    array[index] = value;
                    SetArray(arrayName, array);
                    return;
                }

                if (TryParseHashElement(target, out var hashName, out var hashKeyExpression))
                {
                    var hash = GetHash(hashName);
                    hash[Stringify(PerlExpression.Evaluate(hashKeyExpression, this))] = value;
                    SetHash(hashName, hash);
                    return;
                }

                if (TryParseHashRefElement(target, out var scalarName, out var refKey))
                {
                    if (GetScalar(scalarName) is not Dictionary<string, object?> dict)
                        dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    dict[refKey] = value;
                    SetScalar(scalarName, dict);
                    return;
                }

                SetScalar(target[1..], value);
                return;
            }

            if (target.StartsWith("@", StringComparison.Ordinal))
            {
                SetArray(target[1..], FlattenValue(value).ToList());
                return;
            }

            if (target.StartsWith("%", StringComparison.Ordinal))
            {
                if (value is Dictionary<string, object?> dict)
                    SetHash(target[1..], dict);
                else
                    throw new PerlException($"cannot assign non-hash to {target}");
                return;
            }

            throw new PerlException($"unsupported assignment target: {target}");
        }

        private object? Chomp(IReadOnlyList<object?> args)
        {
            if (args.Count == 0)
            {
                var value = ChompOne(Stringify(GetScalar("_")));
                SetScalar("_", value);
                return 1L;
            }

            foreach (var raw in args)
            {
                if (raw is PerlLValue lvalue)
                    Assign(lvalue.Target, ChompOne(Stringify(PerlExpression.Evaluate(lvalue.Target, this))));
            }
            return args.Count;
        }

        private object? Chop(IReadOnlyList<object?> args)
        {
            var target = args.Count == 0 ? "$_" : args[0] is PerlLValue lvalue ? lvalue.Target : null;
            if (target is null) return null;
            var value = Stringify(PerlExpression.Evaluate(target, this));
            var chopped = value.Length == 0 ? "" : value[..^1];
            Assign(target, chopped);
            return value.Length == 0 ? "" : value[^1].ToString();
        }

        private object? Split(IReadOnlyList<object?> args)
        {
            var pattern = args.Count > 0 ? Stringify(args[0]) : @"\s+";
            var text = args.Count > 1 ? Stringify(args[1]) : Stringify(GetScalar("_"));
            pattern = StripRegexDelimiters(pattern);
            return Regex.Split(text.Trim(), pattern).Where(static part => part.Length > 0).Cast<object?>().ToList();
        }

        private object? Substr(IReadOnlyList<object?> args)
        {
            var text = Stringify(Arg(args, 0, "substr"));
            var start = (int)ToLong(Arg(args, 1, "substr"));
            if (start < 0) start = Math.Max(0, text.Length + start);
            if (args.Count < 3) return start >= text.Length ? "" : text[start..];
            var length = Math.Max(0, (int)ToLong(args[2]));
            return start >= text.Length ? "" : text.Substring(start, Math.Min(length, text.Length - start));
        }

        private object? Sprintf(IReadOnlyList<object?> args)
        {
            var format = Stringify(Arg(args, 0, "sprintf"));
            var values = args.Skip(1).Select(v => v is long or int or double ? v : Stringify(v)).ToArray();
            try { return string.Format(CultureInfo.InvariantCulture, ConvertPerlFormat(format), values); }
            catch (FormatException) { return format; }
        }

        private object? Push(IReadOnlyList<object?> args)
        {
            if (args.Count == 0 || args[0] is not PerlLValue lvalue || !lvalue.Target.StartsWith("@", StringComparison.Ordinal))
                throw new PerlException("push expects an array variable");
            var array = GetArray(lvalue.Target[1..]);
            array.AddRange(args.Skip(1));
            return array.Count;
        }

        private object? Pop(IReadOnlyList<object?> args)
        {
            var name = args.Count > 0 && args[0] is PerlLValue lvalue ? lvalue.Target.TrimStart('@') : "ARGV";
            var array = GetArray(name);
            if (array.Count == 0) return null;
            var value = array[^1];
            array.RemoveAt(array.Count - 1);
            return value;
        }

        private object? Shift(IReadOnlyList<object?> args)
        {
            var name = args.Count > 0 && args[0] is PerlLValue lvalue ? lvalue.Target.TrimStart('@') : "ARGV";
            var array = GetArray(name);
            if (array.Count == 0) return null;
            var value = array[0];
            array.RemoveAt(0);
            return value;
        }

        private object? Unshift(IReadOnlyList<object?> args)
        {
            if (args.Count == 0 || args[0] is not PerlLValue lvalue || !lvalue.Target.StartsWith("@", StringComparison.Ordinal))
                throw new PerlException("unshift expects an array variable");
            var array = GetArray(lvalue.Target[1..]);
            array.InsertRange(0, args.Skip(1));
            return array.Count;
        }

        private object? Exists(IReadOnlyList<object?> args)
        {
            if (args.Count == 0 || args[0] is not PerlLValue lvalue)
                return false;
            if (!TryParseHashElement(lvalue.Target, out var hashName, out var keyExpression))
                return false;
            return GetHash(hashName).ContainsKey(Stringify(PerlExpression.Evaluate(keyExpression, this)));
        }

        private object? Delete(IReadOnlyList<object?> args)
        {
            if (args.Count == 0 || args[0] is not PerlLValue lvalue)
                return null;
            if (!TryParseHashElement(lvalue.Target, out var hashName, out var keyExpression))
                return null;
            var hash = GetHash(hashName);
            var key = Stringify(PerlExpression.Evaluate(keyExpression, this));
            if (!hash.TryGetValue(key, out var value))
                return null;
            hash.Remove(key);
            return value;
        }

        private object? Open(IReadOnlyList<object?> args)
        {
            if (args.Count < 3 || args[0] is not PerlLValue lvalue)
                throw new PerlException("open expects filehandle, mode, path");
            if (!lvalue.Target.StartsWith("$", StringComparison.Ordinal))
                throw new PerlException("open filehandle must be scalar");
            var name = lvalue.Target[1..];
            var mode = Stringify(args[1]);
            var path = Stringify(args[2]);
            if (path.StartsWith("|", StringComparison.Ordinal) || mode.Contains('|', StringComparison.Ordinal))
                throw new PerlException("pipe open is not supported in Carbide's VFS-only Perl stub");
            var file = PerlFile.Open(this, path, mode);
            _fileHandles[name] = file;
            SetScalar(name, file);
            return true;
        }

        private object? Close(IReadOnlyList<object?> args)
        {
            var file = ResolveFile(args.Count == 0 ? null : args[0]);
            file?.Close();
            return true;
        }

        private object? ReadLine(IReadOnlyList<object?> args)
        {
            var file = ResolveFile(args.Count == 0 ? null : args[0]);
            return file is null ? Input.ReadLine() : file.ReadLine();
        }

        private object? Glob(IReadOnlyList<object?> args)
        {
            var pattern = Stringify(Arg(args, 0, "glob"));
            var normalized = Vfs.Normalize(pattern);
            var split = VfsPath.SplitLeaf(normalized);
            if (Vfs.Resolve(split.Parent) is not VfsDirectory)
                return new List<object?>();
            var regex = WildcardToRegex(split.Leaf);
            return Vfs.List(split.Parent, false, null)
                .Where(node => Regex.IsMatch(node.Name, regex, RegexOptions.IgnoreCase))
                .Select(node => (object?)node.AbsolutePath)
                .ToList();
        }

        private object? MakePath(IReadOnlyList<object?> args)
        {
            foreach (var arg in args)
                Vfs.GetOrCreateDirectory(Stringify(arg));
            return true;
        }

        private object? Copy(IReadOnlyList<object?> args)
        {
            var source = Vfs.Normalize(Stringify(Arg(args, 0, "copy")));
            var dest = Vfs.Normalize(Stringify(Arg(args, 1, "copy")));
            if (Vfs.Resolve(source) is not VfsFile file)
                throw new PerlException($"No such file: '{source}'");
            Vfs.CreateTextFile(dest, file.ReadText(), overwrite: true);
            return true;
        }

        private object? System(IReadOnlyList<object?> args)
        {
            var argv = Flatten(args).Select(Stringify).ToArray();
            if (argv.Length == 0)
                return 0L;
            var code = DispatchCommand(_invocation, argv[0], argv.Skip(1).ToArray(), "perl");
            SetScalar("?", code);
            return code;
        }

        public bool GetOptions(string rawArguments)
        {
            var entries = SplitTopLevel(rawArguments, ',')
                .Select(static part => part.Trim())
                .Where(static part => part.Length > 0)
                .ToArray();
            var argv = GetArray("ARGV").Select(Stringify).ToList();
            foreach (var entry in entries)
            {
                var arrow = entry.IndexOf("=>", StringComparison.Ordinal);
                if (arrow < 0)
                    continue;
                var spec = TrimQuotes(entry[..arrow].Trim()).Split('|')[0];
                var target = entry[(arrow + 2)..].Trim();
                if (!target.StartsWith("\\$", StringComparison.Ordinal))
                    continue;
                var variable = target[1..];
                var isInt = spec.EndsWith("=i", StringComparison.Ordinal);
                var requiresValue = spec.Contains('=', StringComparison.Ordinal);
                var name = spec.Split('=')[0];
                var option = "--" + name;
                for (var argIndex = 0; argIndex < argv.Count; argIndex++)
                {
                    if (argv[argIndex] != option && argv[argIndex] != "-" + name)
                        continue;
                    object? value = true;
                    argv.RemoveAt(argIndex);
                    if (requiresValue)
                    {
                        if (argIndex >= argv.Count)
                            throw new PerlException($"option requires an argument: {option}");
                        value = isInt ? ToLong(argv[argIndex]) : argv[argIndex];
                        argv.RemoveAt(argIndex);
                    }
                    Assign(variable, value);
                    break;
                }
            }
            SetArray("ARGV", argv.Cast<object?>().ToList());
            return true;
        }

        private PerlFile? ResolveFile(object? value)
        {
            if (value is PerlFile file)
                return file;
            if (value is PerlLValue lvalue && lvalue.Target.StartsWith("$", StringComparison.Ordinal))
                value = GetScalar(lvalue.Target[1..]);
            return value as PerlFile;
        }

        private static IEnumerable<object?> Flatten(IEnumerable<object?> values)
        {
            foreach (var value in values)
            {
                foreach (var item in FlattenValue(value))
                    yield return item;
            }
        }

        private static IEnumerable<object?> FlattenValue(object? value)
        {
            return value switch
            {
                null => [],
                List<object?> list => list,
                PerlArrayRef array => array.Values,
                PerlHashRef => [value],
                string => [value],
                IEnumerable<object?> items => items,
                _ => [value],
            };
        }

        private static Dictionary<string, object?> HashArg(IReadOnlyList<object?> args, string function)
        {
            var value = Arg(args, 0, function);
            return value switch
            {
                Dictionary<string, object?> dict => dict,
                PerlHashRef hash => hash.Values,
                _ => throw new PerlException($"{function} expects a hash"),
            };
        }

        private static string ChangeFirst(string text, bool upper)
            => text.Length == 0
                ? text
                : (upper ? char.ToUpperInvariant(text[0]) : char.ToLowerInvariant(text[0])) + text[1..];

        private static string ConvertPerlFormat(string format)
        {
            var index = 0;
            return Regex.Replace(format, "%[sdif]", _ => "{" + index++ + "}");
        }

        public static string Stringify(object? value)
            => value switch
            {
                null => "",
                bool b => b ? "1" : "",
                string s => s,
                char c => c.ToString(),
                long l => l.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                double d => d.ToString(CultureInfo.InvariantCulture),
                PerlFile file => file.ToString(),
                PerlArrayRef array => string.Join(" ", array.Values.Select(Stringify)),
                PerlHashRef hash => string.Join(" ", hash.Values.Select(kv => kv.Key + " " + Stringify(kv.Value))),
                List<object?> list => string.Join(" ", list.Select(Stringify)),
                Dictionary<string, object?> dict => string.Join(" ", dict.Select(kv => kv.Key + " " + Stringify(kv.Value))),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };

        public static bool Truthy(object? value)
        {
            var text = Stringify(value);
            return text.Length > 0 && text != "0";
        }

        public static long ToLong(object? value)
        {
            return value switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                bool b => b ? 1 : 0,
                string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
                null => 0,
                _ => long.TryParse(Stringify(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            };
        }

        public static double ToDouble(object? value)
            => value switch
            {
                double d => d,
                float f => f,
                long l => l,
                int i => i,
                bool b => b ? 1 : 0,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                null => 0,
                _ => double.TryParse(Stringify(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            };

        public static object? Arg(IReadOnlyList<object?> args, int index, string function)
            => index < args.Count ? args[index] : throw new PerlException($"{function} missing required argument");
    }

    private static class PerlProgram
    {
        public static void Execute(string source, string path, PerlContext context)
        {
            source = RemoveShebang(source);
            source = RegisterSubs(source, context);
            foreach (var statement in SplitStatements(source))
                ExecuteStatement(statement, context);
        }

        private static void ExecuteStatement(string statement, PerlContext context)
        {
            statement = statement.Trim();
            if (statement.Length == 0)
                return;

            if (TryExecutePostfix(statement, context))
                return;

            if (TryExecuteBlock(statement, context))
                return;

            if (statement is "use strict" or "use warnings" or "no strict" or "no warnings")
                return;

            if (statement.StartsWith("use ", StringComparison.Ordinal))
            {
                var module = statement[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd(';');
                context.ImportModule(module);
                return;
            }

            if (statement.StartsWith("require ", StringComparison.Ordinal))
            {
                context.ImportModule(TrimQuotes(statement[8..].Trim()));
                return;
            }

            if (statement is "last" or "break")
                throw new PerlBreakException();
            if (statement is "next" or "continue")
                throw new PerlNextException();

            if (statement.StartsWith("return", StringComparison.Ordinal))
            {
                var expr = statement.Length == 6 ? "" : statement[6..].Trim();
                throw new PerlReturnException(expr.Length == 0 ? null : PerlExpression.Evaluate(expr, context));
            }

            if (statement.StartsWith("die ", StringComparison.Ordinal))
                throw new PerlException(PerlContext.Stringify(PerlExpression.Evaluate(statement[4..], context)));
            if (statement.StartsWith("warn ", StringComparison.Ordinal))
            {
                context.Error.WriteLine(PerlContext.Stringify(PerlExpression.Evaluate(statement[5..], context)));
                return;
            }
            if (statement.StartsWith("exit", StringComparison.Ordinal))
            {
                var expr = statement.Length == 4 ? "" : statement[4..].Trim();
                throw new PerlExitException(expr.Length == 0 ? 0 : (int)PerlContext.ToLong(PerlExpression.Evaluate(expr, context)));
            }

            if (TryExecutePrint(statement, context))
                return;

            if (TryExecuteOpen(statement, context))
                return;

            if (TryExecuteBareFunction(statement, context))
                return;

            if (TryExecuteSubstitution(statement, context))
                return;

            if (statement.StartsWith("GetOptions", StringComparison.Ordinal))
            {
                var args = ExtractCallArguments(statement, "GetOptions");
                context.GetOptions(args);
                return;
            }

            if (TryExecuteAssignment(statement, context))
                return;

            _ = PerlExpression.Evaluate(statement, context);
        }

        private static bool TryExecuteBareFunction(string statement, PerlContext context)
        {
            var match = Regex.Match(statement, @"^(close|chomp|chop|push|pop|shift|unshift|system)\s+(.+)$", RegexOptions.Singleline);
            if (!match.Success)
                return false;
            _ = context.CallFunction(match.Groups[1].Value, PerlExpression.ParseArguments(match.Groups[2].Value, context));
            return true;
        }

        private static bool TryExecutePostfix(string statement, PerlContext context)
        {
            var match = Regex.Match(statement, @"^(.+?)\s+if\s+(.+)$", RegexOptions.Singleline);
            if (!match.Success)
                return false;
            if (PerlContext.Truthy(PerlExpression.Evaluate(match.Groups[2].Value, context)))
                ExecuteStatement(match.Groups[1].Value, context);
            return true;
        }

        private static bool TryExecuteBlock(string statement, PerlContext context)
        {
            if (TryParseIf(statement, out var condition, out var thenBody, out var elseBody))
            {
                if (PerlContext.Truthy(PerlExpression.Evaluate(condition, context)))
                    Execute(thenBody, "(if)", context);
                else if (elseBody is not null)
                    Execute(elseBody, "(else)", context);
                return true;
            }

            if (TryParseForeach(statement, out var variable, out var listExpression, out var body))
            {
                foreach (var item in PerlExpression.Iterate(PerlExpression.Evaluate(listExpression, context)))
                {
                    context.Assign(variable, item);
                    try { Execute(body, "(foreach)", context); }
                    catch (PerlNextException) { continue; }
                    catch (PerlBreakException) { break; }
                }
                return true;
            }

            if (TryParseWhileDiamond(statement, out var whileTarget, out var whileBody))
            {
                while (true)
                {
                    var value = whileTarget is null
                        ? context.Input.ReadLine()
                        : PerlExpression.Evaluate("<" + whileTarget + ">", context);
                    if (value is null)
                        break;
                    context.Assign(whileTarget is null ? "$_" : whileTarget, PerlContext.Stringify(value));
                    try { Execute(whileBody, "(while)", context); }
                    catch (PerlNextException) { continue; }
                    catch (PerlBreakException) { break; }
                }
                return true;
            }

            return false;
        }

        private static bool TryExecutePrint(string statement, PerlContext context)
        {
            if (!statement.StartsWith("print", StringComparison.Ordinal) && !statement.StartsWith("say", StringComparison.Ordinal))
                return false;

            var say = statement.StartsWith("say", StringComparison.Ordinal);
            var rest = statement[(say ? 3 : 5)..].Trim();
            if (rest.Length == 0)
            {
                _ = say ? context.CallFunction("say", []) : context.Print([]);
                return true;
            }

            var fileHandleMatch = Regex.Match(rest, @"^(\$[A-Za-z_][A-Za-z0-9_]*)\s+(.+)$", RegexOptions.Singleline);
            if (!say
                && fileHandleMatch.Success
                && context.GetScalar(fileHandleMatch.Groups[1].Value[1..]) is PerlFile file)
            {
                var fileArgs = new List<object?> { file };
                fileArgs.AddRange(PerlExpression.ParseArguments(fileHandleMatch.Groups[2].Value, context));
                _ = context.Print(fileArgs);
                return true;
            }

            var args = PerlExpression.ParseArguments(rest, context);
            _ = say ? context.CallFunction("say", args) : context.Print(args);
            return true;
        }

        private static bool TryExecuteOpen(string statement, PerlContext context)
        {
            if (!statement.StartsWith("open ", StringComparison.Ordinal) && !statement.StartsWith("open(", StringComparison.Ordinal))
                return false;

            var argsText = statement.StartsWith("open(", StringComparison.Ordinal)
                ? ExtractCallArguments(statement, "open")
                : statement[5..];
            var raw = SplitTopLevel(argsText, ',').Select(static p => p.Trim()).ToArray();
            if (raw.Length < 3)
                throw new PerlException("open expects filehandle, mode, path");
            var handle = StripDeclaration(raw[0]);
            var args = new List<object?> { new PerlLValue(handle) };
            args.AddRange(raw.Skip(1).Select(arg => PerlExpression.Evaluate(arg, context)));
            _ = context.CallFunction("open", args);
            return true;
        }

        private static bool TryExecuteSubstitution(string statement, PerlContext context)
        {
            var target = "$_";
            var text = statement;
            var bind = IndexOfTopLevel(text, "=~");
            if (bind >= 0)
            {
                target = text[..bind].Trim();
                text = text[(bind + 2)..].Trim();
            }

            if (!text.StartsWith("s", StringComparison.Ordinal) || text.Length < 2)
                return false;
            if (!TryParseSubstitution(text, out var pattern, out var replacement, out var flags))
                return false;
            context.Substitute(target, pattern, replacement, flags);
            return true;
        }

        private static bool TryExecuteAssignment(string statement, PerlContext context)
        {
            foreach (var op in new[] { ".=", "+=", "-=", "=" })
            {
                var index = IndexOfTopLevel(statement, op);
                if (index < 0)
                    continue;
                if (op == "=" && (statement.Contains("=~", StringComparison.Ordinal) || statement.Contains("==", StringComparison.Ordinal)))
                    continue;
                var target = statement[..index].Trim();
                var expr = statement[(index + op.Length)..].Trim();
                if (target.StartsWith("my ", StringComparison.Ordinal) && target.Contains(',', StringComparison.Ordinal))
                    return TryExecuteListAssignment(target, expr, context);
                object? value = PerlExpression.Evaluate(expr, context);
                if (op != "=")
                {
                    var current = PerlExpression.Evaluate(target, context);
                    value = op switch
                    {
                        ".=" => PerlContext.Stringify(current) + PerlContext.Stringify(value),
                        "+=" => PerlContext.ToDouble(current) + PerlContext.ToDouble(value),
                        "-=" => PerlContext.ToDouble(current) - PerlContext.ToDouble(value),
                        _ => value,
                    };
                }
                context.Assign(target, value);
                return true;
            }
            return false;
        }

        private static bool TryExecuteListAssignment(string target, string expr, PerlContext context)
        {
            var trimmed = StripDeclaration(target);
            if (!trimmed.StartsWith("(", StringComparison.Ordinal) || !trimmed.EndsWith(")", StringComparison.Ordinal))
                return false;
            var targets = SplitTopLevel(trimmed[1..^1], ',').Select(static part => part.Trim()).ToArray();
            var values = PerlExpression.Iterate(PerlExpression.Evaluate(expr, context)).ToArray();
            for (var i = 0; i < targets.Length; i++)
                context.Assign(targets[i], i < values.Length ? values[i] : null);
            return true;
        }

        private static bool TryParseIf(string statement, out string condition, out string thenBody, out string? elseBody)
        {
            condition = "";
            thenBody = "";
            elseBody = null;
            if (!statement.StartsWith("if", StringComparison.Ordinal))
                return false;
            var paren = statement.IndexOf('(');
            if (paren < 0 || !TryReadBalanced(statement, paren, '(', ')', out condition, out var afterCondition))
                return false;
            var brace = statement.IndexOf('{', afterCondition);
            if (brace < 0 || !TryReadBalanced(statement, brace, '{', '}', out thenBody, out var afterThen))
                return false;
            var rest = statement[afterThen..].Trim();
            if (rest.StartsWith("else", StringComparison.Ordinal))
            {
                var elseBrace = rest.IndexOf('{');
                if (elseBrace >= 0 && TryReadBalanced(rest, elseBrace, '{', '}', out var parsedElse, out _))
                    elseBody = parsedElse;
            }
            return true;
        }

        private static bool TryParseForeach(string statement, out string variable, out string listExpression, out string body)
        {
            variable = "";
            listExpression = "";
            body = "";
            var match = Regex.Match(statement, @"^(?:for|foreach)\s+(?:my\s+)?(\$[A-Za-z_][A-Za-z0-9_:]*)\s*", RegexOptions.Singleline);
            if (!match.Success)
                return false;
            variable = match.Groups[1].Value;
            var paren = statement.IndexOf('(', match.Length);
            if (paren < 0 || !TryReadBalanced(statement, paren, '(', ')', out listExpression, out var afterList))
                return false;
            var brace = statement.IndexOf('{', afterList);
            return brace >= 0 && TryReadBalanced(statement, brace, '{', '}', out body, out _);
        }

        private static bool TryParseWhileDiamond(string statement, out string? target, out string body)
        {
            target = null;
            body = "";
            if (!statement.StartsWith("while", StringComparison.Ordinal))
                return false;
            var paren = statement.IndexOf('(');
            if (paren < 0 || !TryReadBalanced(statement, paren, '(', ')', out var condition, out var afterCondition))
                return false;
            var brace = statement.IndexOf('{', afterCondition);
            if (brace < 0 || !TryReadBalanced(statement, brace, '{', '}', out body, out _))
                return false;
            condition = condition.Trim();
            if (condition == "<>")
                return true;
            var match = Regex.Match(condition, @"^(?:my\s+)?(\$[A-Za-z_][A-Za-z0-9_]*)\s*=\s*<([^>]+)>$");
            if (!match.Success)
                return false;
            target = match.Groups[1].Value;
            return true;
        }

        private static string RegisterSubs(string source, PerlContext context)
        {
            var sb = new StringBuilder(source);
            var cursor = 0;
            while (cursor < sb.Length)
            {
                var match = Regex.Match(sb.ToString(cursor, sb.Length - cursor), @"\bsub\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.Singleline);
                if (!match.Success)
                    break;
                var start = cursor + match.Index;
                var brace = start + match.Value.LastIndexOf('{');
                if (!TryReadBalanced(sb.ToString(), brace, '{', '}', out var body, out var after))
                    break;
                context.RegisterSub(match.Groups[1].Value, body);
                sb.Remove(start, after - start);
                cursor = start;
            }
            return sb.ToString();
        }

        private static IEnumerable<string> SplitStatements(string source)
        {
            var current = new StringBuilder();
            var depth = 0;
            var inString = false;
            var quote = '\0';
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                if (inString)
                {
                    current.Append(ch);
                    if (ch == '\\' && i + 1 < source.Length)
                        current.Append(source[++i]);
                    else if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inString = true;
                    quote = ch;
                    current.Append(ch);
                    continue;
                }

                if (ch == '#')
                {
                    while (i + 1 < source.Length && source[i + 1] != '\n') i++;
                    continue;
                }

                if (ch is '(' or '[' or '{') depth++;
                if (ch is ')' or ']' or '}') depth--;

                if ((ch == ';' || ch == '\n') && depth <= 0)
                {
                    var statement = current.ToString().Trim();
                    if (statement.Length > 0)
                        yield return statement;
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }
            var tail = current.ToString().Trim();
            if (tail.Length > 0)
                yield return tail;
        }

        private static string RemoveShebang(string source)
            => source.StartsWith("#!", StringComparison.Ordinal)
                ? string.Join("\n", source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Skip(1))
                : source;
    }

    private static class PerlExpression
    {
        public static object? Evaluate(string expression, PerlContext context)
        {
            expression = expression.Trim().TrimEnd(';').Trim();
            if (expression.Length == 0)
                return null;
            expression = StripOuterParens(expression);

            if (expression.StartsWith("\\", StringComparison.Ordinal)
                && expression.Length > 1
                && expression[1] is '$' or '@' or '%')
                return new PerlLValue(expression[1..]);

            if (expression.StartsWith("<", StringComparison.Ordinal) && expression.EndsWith(">", StringComparison.Ordinal))
                return EvaluateDiamond(expression[1..^1].Trim(), context);

            if (TryParseSubstitutionExpression(expression, context, out var substitutionResult))
                return substitutionResult;

            if (expression.StartsWith("/", StringComparison.Ordinal) && TryParseRegexLiteral(expression, out var barePattern, out var bareFlags))
                return context.MatchRegex(PerlContext.Stringify(context.GetScalar("_")), barePattern, bareFlags);

            if (TryEvaluateLiteral(expression, context, out var earlyLiteral))
                return earlyLiteral;

            if (TryEvaluateVariable(expression, context, out var earlyVariable))
                return earlyVariable;

            foreach (var op in new[] { "||", "&&", "=~", "!~", " eq ", " ne ", "==", "!=", ">=", "<=", ">", "<" })
            {
                var index = IndexOfTopLevel(expression, op);
                if (index < 0)
                    continue;
                var left = expression[..index].Trim();
                var right = expression[(index + op.Length)..].Trim();
                return EvaluateBinary(op.Trim(), left, right, context);
            }

            foreach (var op in new[] { ".", "+", "-" })
            {
                var index = LastIndexOfTopLevel(expression, op);
                if (index <= 0)
                    continue;
                var left = expression[..index].Trim();
                var right = expression[(index + op.Length)..].Trim();
                if (op == "." && (char.IsDigit(expression[index - 1]) || index + 1 < expression.Length && char.IsDigit(expression[index + 1])))
                    continue;
                var l = Evaluate(left, context);
                var r = Evaluate(right, context);
                return op switch
                {
                    "." => PerlContext.Stringify(l) + PerlContext.Stringify(r),
                    "+" => PerlContext.ToDouble(l) + PerlContext.ToDouble(r),
                    "-" => PerlContext.ToDouble(l) - PerlContext.ToDouble(r),
                    _ => null,
                };
            }

            foreach (var op in new[] { "*", "/", "%" })
            {
                var index = LastIndexOfTopLevel(expression, op);
                if (index <= 0)
                    continue;
                var l = PerlContext.ToDouble(Evaluate(expression[..index], context));
                var r = PerlContext.ToDouble(Evaluate(expression[(index + 1)..], context));
                return op switch
                {
                    "*" => l * r,
                    "/" => l / r,
                    "%" => l % r,
                    _ => null,
                };
            }

            if (expression.StartsWith("!", StringComparison.Ordinal))
                return !PerlContext.Truthy(Evaluate(expression[1..], context));

            if (TryEvaluateFunction(expression, context, out var call))
                return call;

            throw new PerlException($"unsupported expression: {expression}");
        }

        public static IReadOnlyList<object?> ParseArguments(string text, PerlContext context)
            => SplitTopLevel(text, ',')
                .Select(part => part.Trim())
                .Where(static part => part.Length > 0)
                .Select(part => Evaluate(part, context))
                .ToArray();

        public static IEnumerable<object?> Iterate(object? value)
        {
            return value switch
            {
                null => [],
                PerlArrayRef array => array.Values,
                PerlHashRef hash => hash.Values.Select(kv => (object?)kv.Key),
                List<object?> list => list,
                Dictionary<string, object?> dict => dict.Keys.Cast<object?>(),
                string s => [s],
                IEnumerable<object?> items => items,
                _ => [value],
            };
        }

        private static object? EvaluateBinary(string op, string left, string right, PerlContext context)
        {
            if (op is "||")
            {
                var l = Evaluate(left, context);
                return PerlContext.Truthy(l) ? l : Evaluate(right, context);
            }
            if (op is "&&")
            {
                var l = Evaluate(left, context);
                return PerlContext.Truthy(l) ? Evaluate(right, context) : l;
            }
            if (op is "=~" or "!~")
            {
                var text = PerlContext.Stringify(Evaluate(left, context));
                bool matched;
                if (TryParseRegexLiteral(right, out var pattern, out var flags))
                    matched = context.MatchRegex(text, pattern, flags);
                else if (TryParseSubstitution(right, out pattern, out var replacement, out flags))
                    matched = context.Substitute(left, pattern, replacement, flags);
                else
                    throw new PerlException($"unsupported regex expression: {right}");
                return op == "=~" ? matched : !matched;
            }

            var lValue = Evaluate(left, context);
            var rValue = Evaluate(right, context);
            return op switch
            {
                "eq" => string.Equals(PerlContext.Stringify(lValue), PerlContext.Stringify(rValue), StringComparison.Ordinal),
                "ne" => !string.Equals(PerlContext.Stringify(lValue), PerlContext.Stringify(rValue), StringComparison.Ordinal),
                "==" => Math.Abs(PerlContext.ToDouble(lValue) - PerlContext.ToDouble(rValue)) < 0.0000001,
                "!=" => Math.Abs(PerlContext.ToDouble(lValue) - PerlContext.ToDouble(rValue)) >= 0.0000001,
                ">" => PerlContext.ToDouble(lValue) > PerlContext.ToDouble(rValue),
                "<" => PerlContext.ToDouble(lValue) < PerlContext.ToDouble(rValue),
                ">=" => PerlContext.ToDouble(lValue) >= PerlContext.ToDouble(rValue),
                "<=" => PerlContext.ToDouble(lValue) <= PerlContext.ToDouble(rValue),
                _ => throw new PerlException($"unsupported operator: {op}"),
            };
        }

        private static bool TryEvaluateLiteral(string expression, PerlContext context, out object? value)
        {
            value = null;
            if (expression is "undef")
                return true;
            if (long.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                value = l;
                return true;
            }
            if (double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && expression.Contains('.', StringComparison.Ordinal))
            {
                value = d;
                return true;
            }
            if ((expression.StartsWith("\"", StringComparison.Ordinal) && expression.EndsWith("\"", StringComparison.Ordinal))
                || (expression.StartsWith("'", StringComparison.Ordinal) && expression.EndsWith("'", StringComparison.Ordinal)))
            {
                value = expression[0] == '"'
                    ? Interpolate(Unescape(expression[1..^1]), context)
                    : expression[1..^1];
                return true;
            }
            if ((expression.StartsWith("qq(", StringComparison.Ordinal) || expression.StartsWith("q(", StringComparison.Ordinal)) && expression.EndsWith(")", StringComparison.Ordinal))
            {
                var inner = expression[(expression.StartsWith("qq(", StringComparison.Ordinal) ? 3 : 2)..^1];
                value = expression.StartsWith("qq(", StringComparison.Ordinal) ? Interpolate(Unescape(inner), context) : inner;
                return true;
            }
            if (expression.StartsWith("qw(", StringComparison.Ordinal) && expression.EndsWith(")", StringComparison.Ordinal))
            {
                value = expression[3..^1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Cast<object?>().ToList();
                return true;
            }
            if (expression.StartsWith("[", StringComparison.Ordinal) && expression.EndsWith("]", StringComparison.Ordinal))
            {
                value = new PerlArrayRef(ParseArguments(expression[1..^1], context).SelectMany(Iterate).ToList());
                return true;
            }
            if (expression.StartsWith("{", StringComparison.Ordinal) && expression.EndsWith("}", StringComparison.Ordinal))
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in SplitTopLevel(expression[1..^1], ','))
                {
                    var parts = SplitTopLevel(pair, '=').ToArray();
                    if (parts.Length >= 2)
                        dict[TrimQuotes(parts[0].Trim()).TrimStart('-')] = Evaluate(parts[^1], context);
                }
                value = new PerlHashRef(dict);
                return true;
            }
            return false;
        }

        private static bool TryEvaluateVariable(string expression, PerlContext context, out object? value)
        {
            value = null;
            if (expression == "$_") { value = context.GetScalar("_"); return true; }
            if (expression == "$.") { value = context.GetScalar("."); return true; }
            if (expression == "$?") { value = context.GetScalar("?"); return true; }
            if (expression == "$/") { value = context.GetScalar("/"); return true; }
            if (expression == "$0") { value = context.GetScalar("0"); return true; }
            if (Regex.IsMatch(expression, @"^\$\d+$"))
            {
                value = context.GetScalar(expression[1..]);
                return true;
            }
            if (TryParseArrayElement(expression, out var arrayName, out var indexExpression))
            {
                var array = context.GetArray(arrayName);
                var index = (int)PerlContext.ToLong(Evaluate(indexExpression, context));
                value = index >= 0 && index < array.Count ? array[index] : null;
                return true;
            }
            if (TryParseHashElement(expression, out var hashName, out var keyExpression))
            {
                var hash = context.GetHash(hashName);
                hash.TryGetValue(PerlContext.Stringify(Evaluate(keyExpression, context)), out value);
                return true;
            }
            if (TryParseHashRefElement(expression, out var scalarName, out var refKey))
            {
                value = context.GetScalar(scalarName) is Dictionary<string, object?> dict && dict.TryGetValue(refKey, out var found) ? found : null;
                return true;
            }
            if (Regex.IsMatch(expression, @"^\$[A-Za-z_][A-Za-z0-9_:]*$"))
            {
                value = context.GetScalar(expression[1..]);
                return true;
            }
            if (Regex.IsMatch(expression, @"^@[A-Za-z_][A-Za-z0-9_:]*$"))
            {
                value = new PerlArrayRef(context.GetArray(expression[1..]));
                return true;
            }
            if (Regex.IsMatch(expression, @"^%[A-Za-z_][A-Za-z0-9_:]*$"))
            {
                value = new PerlHashRef(context.GetHash(expression[1..]));
                return true;
            }
            return false;
        }

        private static bool TryEvaluateFunction(string expression, PerlContext context, out object? value)
        {
            value = null;
            var match = Regex.Match(expression, @"^([A-Za-z_][A-Za-z0-9_:]*)\s*\((.*)\)$", RegexOptions.Singleline);
            if (match.Success)
            {
                value = context.CallFunction(match.Groups[1].Value, ParseArguments(match.Groups[2].Value, context));
                return true;
            }

            match = Regex.Match(expression, @"^(split|join|length|lc|uc|basename|dirname|catfile|system)\s+(.+)$", RegexOptions.Singleline);
            if (match.Success)
            {
                value = context.CallFunction(match.Groups[1].Value, ParseArguments(match.Groups[2].Value, context));
                return true;
            }
            return false;
        }

        private static object? EvaluateDiamond(string handle, PerlContext context)
        {
            if (handle.Length == 0)
                return context.Input.ReadLine();
            var value = Evaluate(handle.StartsWith("$", StringComparison.Ordinal) ? handle : "$" + handle, context);
            return context.CallFunction("readline", [value]);
        }

        private static bool TryParseSubstitutionExpression(string expression, PerlContext context, out object? value)
        {
            value = null;
            if (!TryParseSubstitution(expression, out var pattern, out var replacement, out var flags))
                return false;
            value = context.Substitute("$_", pattern, replacement, flags);
            return true;
        }

        private static string Interpolate(string text, PerlContext context)
        {
            return Regex.Replace(
                text,
                @"\$(?:([A-Za-z_][A-Za-z0-9_:]*)\{([^}]+)\}|([A-Za-z_][A-Za-z0-9_:]*|\d+|[_.?/0]))(?:->\{([^}]+)\}|\[([^\]]+)\])?",
                match =>
                {
                    var expr = "$" + match.Value[1..];
                    return PerlContext.Stringify(Evaluate(expr, context));
                });
        }

        private static string Unescape(string text)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\' || i + 1 >= text.Length)
                {
                    sb.Append(text[i]);
                    continue;
                }
                var esc = text[++i];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => esc,
                });
            }
            return sb.ToString();
        }
    }

    private sealed class PerlFile
    {
        private readonly PerlContext _context;
        private readonly string _path;
        private readonly string _mode;
        private bool _closed;

        private PerlFile(PerlContext context, string path, string mode)
        {
            _context = context;
            _path = context.Vfs.Normalize(path);
            _mode = mode;
            if (mode.Contains('>', StringComparison.Ordinal))
                context.Vfs.CreateTextFile(_path, "", overwrite: true);
            else if (mode.Contains(">>", StringComparison.Ordinal) && context.Vfs.Resolve(_path) is not VfsFile)
                context.Vfs.CreateTextFile(_path, "", overwrite: false);
        }

        public static PerlFile Open(PerlContext context, string path, string mode)
        {
            if (!mode.Contains('>', StringComparison.Ordinal) && context.Vfs.Resolve(context.Vfs.Normalize(path)) is not VfsFile)
                throw new PerlException($"No such file: '{path}'");
            return new PerlFile(context, path, mode);
        }

        public void Write(string text)
        {
            EnsureOpen();
            if (!_mode.Contains('>', StringComparison.Ordinal))
                throw new PerlException("filehandle is not open for writing");
            if (_context.Vfs.Resolve(_path) is VfsFile file)
                file.AppendText(text);
            else
                _context.Vfs.CreateTextFile(_path, text, overwrite: false);
        }

        public string Read()
        {
            EnsureOpen();
            return _context.Vfs.Resolve(_path) is VfsFile file ? file.ReadText() : "";
        }

        public string? ReadLine()
        {
            EnsureOpen();
            var text = Read();
            if (text.Length == 0)
                return null;
            var newline = text.IndexOf('\n');
            if (newline < 0)
            {
                _context.Vfs.CreateTextFile(_path, "", overwrite: true);
                return text;
            }
            var line = text[..(newline + 1)];
            _context.Vfs.CreateTextFile(_path, text[(newline + 1)..], overwrite: true);
            return line;
        }

        public void Close() => _closed = true;
        public override string ToString() => $"GLOB({_path})";
        private void EnsureOpen()
        {
            if (_closed) throw new PerlException("I/O operation on closed filehandle");
        }
    }

    private static class PerlJson
    {
        public static object? FromJson(JsonElement element)
            => element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Array => new PerlArrayRef(element.EnumerateArray().Select(FromJson).ToList()),
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
                _ => null,
            };

        public static string ToJson(object? value, bool indented)
            => JsonSerializer.Serialize(ToJsonValue(value), new JsonSerializerOptions { WriteIndented = indented });

        private static object? ToJsonValue(object? value)
            => value switch
            {
                PerlArrayRef array => array.Values.Select(ToJsonValue).ToList(),
                PerlHashRef hash => hash.Values.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value), StringComparer.Ordinal),
                List<object?> list => list.Select(ToJsonValue).ToList(),
                Dictionary<string, object?> dict => dict.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value), StringComparer.Ordinal),
                _ => value,
            };
    }

    private sealed record PerlArrayRef(List<object?> Values);
    private sealed record PerlHashRef(Dictionary<string, object?> Values);
    private sealed record PerlLValue(string Target);

    private class PerlException : Exception
    {
        public PerlException(string message) : base(message) { }
    }

    private sealed class PerlExitException : Exception
    {
        public int ExitCode { get; }
        public PerlExitException(int exitCode) { ExitCode = exitCode; }
    }

    private sealed class PerlReturnException : Exception
    {
        public object? Value { get; }
        public PerlReturnException(object? value) { Value = value; }
    }

    private sealed class PerlBreakException : Exception { }
    private sealed class PerlNextException : Exception { }

    private static string StripDeclaration(string target)
    {
        target = target.Trim();
        foreach (var prefix in new[] { "my ", "our ", "local " })
        {
            if (target.StartsWith(prefix, StringComparison.Ordinal))
                return target[prefix.Length..].Trim();
        }
        return target;
    }

    private static bool TryParseArrayElement(string text, out string arrayName, out string indexExpression)
    {
        var match = Regex.Match(text, @"^\$([A-Za-z_][A-Za-z0-9_:]*)\[([^\]]+)\]$");
        arrayName = match.Success ? match.Groups[1].Value : "";
        indexExpression = match.Success ? match.Groups[2].Value : "";
        return match.Success;
    }

    private static bool TryParseHashElement(string text, out string hashName, out string keyExpression)
    {
        var match = Regex.Match(text, @"^\$([A-Za-z_][A-Za-z0-9_:]*)\{([^}]+)\}$");
        hashName = match.Success ? match.Groups[1].Value : "";
        keyExpression = match.Success ? QuoteBareHashKey(match.Groups[2].Value) : "";
        return match.Success;
    }

    private static bool TryParseHashRefElement(string text, out string scalarName, out string key)
    {
        var match = Regex.Match(text, @"^\$([A-Za-z_][A-Za-z0-9_:]*)->\{([^}]+)\}$");
        scalarName = match.Success ? match.Groups[1].Value : "";
        key = match.Success ? TrimQuotes(match.Groups[2].Value.Trim()) : "";
        return match.Success;
    }

    private static string QuoteBareHashKey(string text)
    {
        text = text.Trim();
        return Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$") ? "'" + text + "'" : text;
    }

    private static bool TryParseRegexLiteral(string text, out string pattern, out string flags)
    {
        pattern = "";
        flags = "";
        if (!text.StartsWith("/", StringComparison.Ordinal))
            return false;
        if (TryReadDelimited(text, 0, out pattern, out flags))
            return true;
        pattern = text[1..];
        return pattern.Length > 0;
    }

    private static bool TryParseSubstitution(string text, out string pattern, out string replacement, out string flags)
    {
        pattern = "";
        replacement = "";
        flags = "";
        text = text.Trim();
        if (!text.StartsWith("s", StringComparison.Ordinal) || text.Length < 2)
            return false;
        var delimiter = text[1];
        var first = ReadUntilDelimiter(text, 2, delimiter, out pattern);
        if (first < 0)
            return false;
        var second = ReadUntilDelimiter(text, first, delimiter, out replacement);
        if (second < 0)
            return false;
        flags = text[second..].Trim();
        return true;
    }

    private static bool TryReadDelimited(string text, int start, out string body, out string suffix)
    {
        var delimiter = text[start];
        var end = ReadUntilDelimiter(text, start + 1, delimiter, out body);
        if (end < 0)
        {
            suffix = "";
            return false;
        }
        suffix = text[end..].Trim();
        return true;
    }

    private static int ReadUntilDelimiter(string text, int start, char delimiter, out string body)
    {
        var sb = new StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                sb.Append(text[i++]);
                sb.Append(text[i]);
                continue;
            }
            if (text[i] == delimiter)
            {
                body = sb.ToString();
                return i + 1;
            }
            sb.Append(text[i]);
        }
        body = "";
        return -1;
    }

    private static string StripRegexDelimiters(string pattern)
        => pattern.StartsWith("/", StringComparison.Ordinal) && pattern.EndsWith("/", StringComparison.Ordinal)
            ? pattern[1..^1]
            : pattern;

    private static string ExtractCallArguments(string statement, string name)
    {
        var start = statement.IndexOf('(');
        if (start < 0 || !TryReadBalanced(statement, start, '(', ')', out var body, out _))
            throw new PerlException($"{name} expects parenthesized arguments");
        return body;
    }

    private static bool TryReadBalanced(string text, int start, char open, char close, out string body, out int after)
    {
        body = "";
        after = start;
        if (start >= text.Length || text[start] != open)
            return false;
        var depth = 0;
        var inString = false;
        var quote = '\0';
        var sb = new StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (i != start) sb.Append(ch);
                if (ch == '\\' && i + 1 < text.Length)
                    sb.Append(text[++i]);
                else if (ch == quote)
                    inString = false;
                continue;
            }
            if (ch is '\'' or '"')
            {
                inString = true;
                quote = ch;
                if (depth > 0) sb.Append(ch);
                continue;
            }
            if (ch == open)
            {
                if (depth++ > 0) sb.Append(ch);
                continue;
            }
            if (ch == close)
            {
                if (--depth == 0)
                {
                    body = sb.ToString();
                    after = i + 1;
                    return true;
                }
                sb.Append(ch);
                continue;
            }
            if (depth > 0) sb.Append(ch);
        }
        return false;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                current.Append(ch);
                if (ch == '\\' && i + 1 < text.Length)
                    current.Append(text[++i]);
                else if (ch == quote)
                    inString = false;
                continue;
            }
            if (ch is '\'' or '"')
            {
                inString = true;
                quote = ch;
                current.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': brace--; break;
            }
            if (ch == separator && paren == 0 && bracket == 0 && brace == 0)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        result.Add(current.ToString());
        return result;
    }

    private static int IndexOfTopLevel(string text, string op)
    {
        for (var i = 0; i <= text.Length - op.Length; i++)
        {
            if (op == ">" && i > 0 && text[i - 1] == '-')
                continue;
            if (op == "-" && i + 1 < text.Length && text[i + 1] == '>')
                continue;
            if (IsTopLevelAt(text, i) && string.CompareOrdinal(text, i, op, 0, op.Length) == 0)
                return i;
        }
        return -1;
    }

    private static int LastIndexOfTopLevel(string text, string op)
    {
        for (var i = text.Length - op.Length; i >= 0; i--)
        {
            if (op == ">" && i > 0 && text[i - 1] == '-')
                continue;
            if (op == "-" && i + 1 < text.Length && text[i + 1] == '>')
                continue;
            if (IsTopLevelAt(text, i) && string.CompareOrdinal(text, i, op, 0, op.Length) == 0)
                return i;
        }
        return -1;
    }

    private static bool IsTopLevelAt(string text, int index)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < index; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == quote) inString = false;
                continue;
            }
            if (ch is '\'' or '"') { inString = true; quote = ch; continue; }
            switch (ch)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': brace--; break;
            }
        }
        return paren == 0 && bracket == 0 && brace == 0 && !inString;
    }

    private static string StripOuterParens(string text)
    {
        while (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal)
            && TryReadBalanced(text, 0, '(', ')', out var body, out var after) && after == text.Length)
        {
            text = body.Trim();
        }
        return text;
    }

    private static string TrimQuotes(string text)
    {
        text = text.Trim();
        if ((text.StartsWith("\"", StringComparison.Ordinal) && text.EndsWith("\"", StringComparison.Ordinal))
            || (text.StartsWith("'", StringComparison.Ordinal) && text.EndsWith("'", StringComparison.Ordinal)))
            return text[1..^1];
        return text;
    }

    private static string ChompOne(string text)
        => text.EndsWith("\r\n", StringComparison.Ordinal)
            ? text[..^2]
            : text.EndsWith('\n') || text.EndsWith('\r')
                ? text[..^1]
                : text;
}
