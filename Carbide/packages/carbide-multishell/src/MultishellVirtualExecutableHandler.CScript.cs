using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Errors;
using CarbideShellCore.Io;
using CarbideShellCore.Vfs;

#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
namespace CarbidePwsh.SharedMultishell;
#else
namespace CarbideMultishell;
#endif

internal sealed partial class MultishellVirtualExecutableHandler
{
    private const string CScriptBanner = "Carbide Windows Script Host compatible subset";

    private static int ExecuteWindowsCscript(VirtualExecutableInvocation invocation)
        => new CScriptCommand(invocation).Execute();

    private sealed class CScriptCommand
    {
        private readonly VirtualExecutableInvocation _invocation;

        public CScriptCommand(VirtualExecutableInvocation invocation)
        {
            _invocation = invocation;
        }

        public int Execute()
        {
            var options = CScriptCommandLine.Parse(_invocation.Args);
            if (options.Error is not null)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: {options.Error}");
                return 1;
            }

            if (options.ShowHelp)
            {
                WriteHelp();
                return 0;
            }

            if (options.ScriptPath is null)
            {
                if (options.PersistDefaultsRequested || options.DefaultHostRequested is not null)
                    return 0;

                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: no script file specified.");
                _invocation.Error.WriteLine("Use cscript //? for supported options.");
                return 1;
            }

            if (options.JobName is not null)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: /job is only supported for .wsf files, which are not implemented yet.");
                return 1;
            }

            if (options.DebuggerRequested)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: script debugging switches are not supported in Carbide.");
                return 1;
            }

            var scriptPath = _invocation.Vfs.Normalize(options.ScriptPath);
            if (_invocation.Vfs.Resolve(scriptPath) is not VfsFile scriptFile)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: cannot find script file '{options.ScriptPath}'.");
                return 1;
            }

            var engine = SelectEngine(options.EngineName, scriptPath);
            if (engine is null)
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: no script engine for '{options.ScriptPath}'. Use //E:JScript or //E:VBScript.");
                return 1;
            }

            if (engine == "wsf")
            {
                _invocation.Error.WriteLine($"{CommandDisplayName(_invocation)}: .wsf jobs are not implemented in Carbide cscript.");
                return 1;
            }

            if (options.Logo)
                _invocation.Output.WriteLine(CScriptBanner);

            var context = new CScriptContext(_invocation, options, scriptPath);
            try
            {
                return engine switch
                {
                    "jscript" => CScriptJScriptEngine.Execute(scriptFile.ReadText(), context),
                    "vbscript" => CScriptVBScriptEngine.Execute(scriptFile.ReadText(), context),
                    _ => throw new CScriptRuntimeException($"Unsupported script engine '{engine}'."),
                };
            }
            catch (CScriptQuitException ex)
            {
                return ex.ExitCode;
            }
            catch (CScriptRuntimeException ex)
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
            _invocation.Output.WriteLine("usage: cscript [//B] [//I] [//E:engine] [//JOB:id] [//LOGO|//NOLOGO] [//T:seconds] [//U] script [args]");
            _invocation.Output.WriteLine("Carbide cscript is a VFS-backed Windows Script Host subset.");
            _invocation.Output.WriteLine();
            _invocation.Output.WriteLine("Supported engines:");
            _invocation.Output.WriteLine("  JScript     .js files, or //E:JScript");
            _invocation.Output.WriteLine("  VBScript    .vbs files, or //E:VBScript");
            _invocation.Output.WriteLine();
            _invocation.Output.WriteLine("Supported WSH objects:");
            _invocation.Output.WriteLine("  WScript, WScript.Arguments, StdIn/StdOut/StdErr");
            _invocation.Output.WriteLine("  Scripting.FileSystemObject over the Carbide VFS");
            _invocation.Output.WriteLine("  WScript.Shell environment expansion and dispatcher-backed Run");
        }

        private static string? SelectEngine(string? explicitEngine, string scriptPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitEngine))
                return NormalizeEngine(explicitEngine!);

            return VfsPath.GetExtension(scriptPath).ToLowerInvariant() switch
            {
                ".js" => "jscript",
                ".vbs" => "vbscript",
                ".wsf" => "wsf",
                _ => null,
            };
        }

        private static string? NormalizeEngine(string engine)
            => engine.Trim().ToLowerInvariant() switch
            {
                "js" or "javascript" or "jscript" or "ecmascript" => "jscript",
                "vbs" or "vbscript" => "vbscript",
                "wsf" => "wsf",
                _ => null,
            };
    }

    private sealed record CScriptCommandLine(
        string? ScriptPath,
        IReadOnlyList<string> ScriptArgs,
        string? EngineName,
        string? JobName,
        int? TimeoutSeconds,
        bool Logo,
        bool BatchMode,
        bool InteractiveMode,
        bool UnicodeRedirectedIo,
        bool DebuggerRequested,
        bool PersistDefaultsRequested,
        string? DefaultHostRequested,
        bool ShowHelp,
        string? Error)
    {
        public static CScriptCommandLine Parse(IReadOnlyList<string> args)
        {
            string? scriptPath = null;
            var scriptArgs = new List<string>();
            string? engineName = null;
            string? jobName = null;
            int? timeoutSeconds = null;
            bool logo = true;
            bool batchMode = false;
            bool interactiveMode = true;
            bool unicodeRedirectedIo = false;
            bool debuggerRequested = false;
            bool persistDefaultsRequested = false;
            string? defaultHostRequested = null;
            bool showHelp = false;

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (scriptPath is not null)
                {
                    scriptArgs.Add(arg);
                    continue;
                }

                if (TryParseHostOption(arg, out var name, out var value))
                {
                    switch (name)
                    {
                        case "?":
                            showHelp = true;
                            break;
                        case "logo":
                            logo = true;
                            break;
                        case "nologo":
                            logo = false;
                            break;
                        case "b":
                            batchMode = true;
                            interactiveMode = false;
                            break;
                        case "i":
                            interactiveMode = true;
                            batchMode = false;
                            break;
                        case "e":
                            if (string.IsNullOrWhiteSpace(value))
                                return ErrorResult("missing script engine after //E:");
                            engineName = value;
                            break;
                        case "job":
                            if (string.IsNullOrWhiteSpace(value))
                                return ErrorResult("missing job identifier after //JOB:");
                            jobName = value;
                            break;
                        case "t":
                            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
                                || timeout < 0 || timeout > 32767)
                                return ErrorResult("//T expects a timeout from 0 through 32767 seconds.");
                            timeoutSeconds = timeout;
                            break;
                        case "u":
                            unicodeRedirectedIo = true;
                            break;
                        case "d":
                        case "x":
                            debuggerRequested = true;
                            break;
                        case "s":
                            persistDefaultsRequested = true;
                            break;
                        case "h":
                            if (!string.Equals(value, "cscript", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(value, "wscript", StringComparison.OrdinalIgnoreCase))
                                return ErrorResult("//H expects cscript or wscript.");
                            defaultHostRequested = value;
                            break;
                        default:
                            scriptPath = arg;
                            break;
                    }
                    continue;
                }

                if (arg.StartsWith("//", StringComparison.Ordinal))
                    return ErrorResult($"unsupported host option: {arg}");

                scriptPath = arg;
            }

            return new CScriptCommandLine(
                scriptPath,
                scriptArgs,
                engineName,
                jobName,
                timeoutSeconds,
                logo,
                batchMode,
                interactiveMode,
                unicodeRedirectedIo,
                debuggerRequested,
                persistDefaultsRequested,
                defaultHostRequested,
                showHelp,
                null);
        }

        private static bool TryParseHostOption(string arg, out string name, out string? value)
        {
            name = "";
            value = null;
            if (arg.Length < 2)
                return false;

            var text = arg.StartsWith("//", StringComparison.Ordinal)
                ? arg[2..]
                : arg.StartsWith("/", StringComparison.Ordinal) && !LooksLikeVfsScriptPath(arg)
                    ? arg[1..]
                    : "";

            if (text.Length == 0)
                return false;

            var colon = text.IndexOf(':');
            if (colon >= 0)
            {
                name = text[..colon].ToLowerInvariant();
                value = text[(colon + 1)..];
            }
            else
            {
                name = text.ToLowerInvariant();
            }

            return name is "?" or "logo" or "nologo" or "b" or "i" or "e" or "job" or "t" or "u" or "d" or "x" or "s" or "h";
        }

        private static bool LooksLikeVfsScriptPath(string arg)
        {
            if (!arg.StartsWith("/", StringComparison.Ordinal) || arg.StartsWith("//", StringComparison.Ordinal))
                return false;

            var ext = VfsPath.GetExtension(arg).ToLowerInvariant();
            return ext is ".js" or ".vbs" or ".wsf";
        }

        private static CScriptCommandLine ErrorResult(string error)
            => new(null, Array.Empty<string>(), null, null, null, true, false, true, false, false, false, null, false, error);
    }

    private sealed class CScriptContext
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _steps;

        public CScriptContext(VirtualExecutableInvocation invocation, CScriptCommandLine options, string scriptFullName)
        {
            Invocation = invocation;
            Options = options;
            ScriptFullName = scriptFullName;
            var split = VfsPath.SplitLeaf(scriptFullName);
            ScriptName = split.Leaf;
            ScriptDirectory = split.Parent;
            Globals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            WScript = new CScriptWScriptObject(this);
            Globals["WScript"] = WScript;
            Globals["CreateObject"] = new CScriptBoundMethod(args => WScript.CreateObject(CScriptOps.ToString(CScriptArg(args, 0, "CreateObject"))));
            Globals["Enumerator"] = new CScriptBoundMethod(args => new CScriptEnumeratorObject(CScriptOps.Enumerate(CScriptArg(args, 0, "Enumerator")).ToList()));
        }

        public VirtualExecutableInvocation Invocation { get; }
        public CScriptCommandLine Options { get; }
        public string ScriptFullName { get; }
        public string ScriptName { get; }
        public string ScriptDirectory { get; }
        public Dictionary<string, object?> Globals { get; }
        public CScriptWScriptObject WScript { get; }

        public void Step()
        {
            _steps++;
            if (_steps > 1_000_000)
                throw new CScriptRuntimeException("script exceeded the Carbide execution step limit.");

            if (Options.TimeoutSeconds is { } seconds && _stopwatch.Elapsed.TotalSeconds > seconds)
                throw new CScriptRuntimeException("script execution timed out.");
        }

        public object? GetName(string name)
        {
            if (Globals.TryGetValue(name, out var value))
                return value;
            throw new CScriptRuntimeException($"Undefined name '{name}'.");
        }

        public void SetName(string name, object? value)
        {
            Globals[name] = value;
        }
    }

    private static class CScriptJScriptEngine
    {
        public static int Execute(string source, CScriptContext context)
        {
            var normalized = CScriptStripBlockComments(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
            ExecuteBlock(normalized, context);
            return 0;
        }

        private static void ExecuteBlock(string source, CScriptContext context)
        {
            var index = 0;
            while (index < source.Length)
            {
                context.Step();
                index = CScriptSkipTrivia(source, index);
                if (index >= source.Length)
                    return;

                if (CScriptStartsKeyword(source, index, "if"))
                {
                    ExecuteIf(source, ref index, context);
                    continue;
                }

                if (CScriptStartsKeyword(source, index, "while"))
                {
                    ExecuteWhile(source, ref index, context);
                    continue;
                }

                if (CScriptStartsKeyword(source, index, "for"))
                {
                    ExecuteFor(source, ref index, context);
                    continue;
                }

                var statement = CScriptReadSimpleStatement(source, ref index);
                ExecuteSimple(statement, context);
            }
        }

        private static void ExecuteIf(string source, ref int index, CScriptContext context)
        {
            index += 2;
            index = CScriptSkipTrivia(source, index);
            var condition = CScriptReadBalancedRequired(source, ref index, '(', ')', "if");
            var trueBody = CScriptReadBody(source, ref index);
            index = CScriptSkipTrivia(source, index);
            string? falseBody = null;
            if (CScriptStartsKeyword(source, index, "else"))
            {
                index += 4;
                falseBody = CScriptReadBody(source, ref index);
            }

            if (CScriptOps.IsTruthy(CScriptExpression.Evaluate(condition, context)))
                ExecuteBlock(trueBody, context);
            else if (falseBody is not null)
                ExecuteBlock(falseBody, context);
        }

        private static void ExecuteWhile(string source, ref int index, CScriptContext context)
        {
            index += 5;
            index = CScriptSkipTrivia(source, index);
            var condition = CScriptReadBalancedRequired(source, ref index, '(', ')', "while");
            var body = CScriptReadBody(source, ref index);
            while (CScriptOps.IsTruthy(CScriptExpression.Evaluate(condition, context)))
            {
                context.Step();
                try
                {
                    ExecuteBlock(body, context);
                }
                catch (CScriptContinueException)
                {
                    continue;
                }
                catch (CScriptBreakException)
                {
                    break;
                }
            }
        }

        private static void ExecuteFor(string source, ref int index, CScriptContext context)
        {
            index += 3;
            index = CScriptSkipTrivia(source, index);
            var header = CScriptReadBalancedRequired(source, ref index, '(', ')', "for");
            var body = CScriptReadBody(source, ref index);
            var parts = CScriptSplitTopLevel(header, ';');
            if (parts.Count != 3)
                throw new CScriptRuntimeException("for statement expects init; condition; increment.");

            ExecuteSimple(parts[0], context);
            while (parts[1].Trim().Length == 0 || CScriptOps.IsTruthy(CScriptExpression.Evaluate(parts[1], context)))
            {
                context.Step();
                try
                {
                    ExecuteBlock(body, context);
                }
                catch (CScriptContinueException)
                {
                }
                catch (CScriptBreakException)
                {
                    break;
                }
                ExecuteSimple(parts[2], context);
            }
        }

        private static void ExecuteSimple(string statement, CScriptContext context)
        {
            statement = CScriptStripLineComment(statement).Trim().TrimEnd(';').Trim();
            if (statement.Length == 0)
                return;

            if (statement == "break")
                throw new CScriptBreakException();
            if (statement == "continue")
                throw new CScriptContinueException();

            foreach (var prefix in new[] { "var ", "let ", "const " })
            {
                if (statement.StartsWith(prefix, StringComparison.Ordinal))
                {
                    foreach (var declaration in CScriptSplitTopLevel(statement[prefix.Length..], ','))
                    {
                        var part = declaration.Trim();
                        if (part.Length == 0)
                            continue;
                        var eq = CScriptFindAssignment(part);
                        if (eq < 0)
                            context.SetName(part, null);
                        else
                            context.SetName(part[..eq].Trim(), CScriptExpression.Evaluate(part[(eq + 1)..], context));
                    }
                    return;
                }
            }

            if (statement.EndsWith("++", StringComparison.Ordinal) || statement.EndsWith("--", StringComparison.Ordinal))
            {
                var delta = statement.EndsWith("++", StringComparison.Ordinal) ? 1 : -1;
                var target = statement[..^2].Trim();
                var current = CScriptExpression.Evaluate(target, context);
                Assign(target, CScriptOps.ToLong(current) + delta, context);
                return;
            }

            var assignment = CScriptFindAssignment(statement);
            if (assignment >= 0)
            {
                Assign(statement[..assignment].Trim(), CScriptExpression.Evaluate(statement[(assignment + 1)..], context), context);
                return;
            }

            _ = CScriptExpression.Evaluate(statement, context);
        }

        private static void Assign(string target, object? value, CScriptContext context)
        {
            target = target.Trim();
            if (Regex.IsMatch(target, @"^[A-Za-z_$][A-Za-z0-9_$]*$"))
            {
                context.SetName(target, value);
                return;
            }

            if (CScriptTrySplitMemberTarget(target, out var ownerExpression, out var member))
            {
                var owner = CScriptExpression.Evaluate(ownerExpression, context);
                CScriptOps.SetMember(owner, member, value);
                return;
            }

            throw new CScriptRuntimeException($"Unsupported assignment target '{target}'.");
        }
    }

    private static class CScriptVBScriptEngine
    {
        public static int Execute(string source, CScriptContext context)
        {
            var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            ExecuteLines(lines, 0, lines.Length, context);
            return 0;
        }

        private static void ExecuteLines(string[] lines, int start, int end, CScriptContext context)
        {
            for (var i = start; i < end; i++)
            {
                context.Step();
                var line = CScriptStripVbComment(lines[i]).Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("If ", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteIf(lines, ref i, end, line, context);
                    continue;
                }

                if (line.StartsWith("For Each ", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteForEach(lines, ref i, end, line, context);
                    continue;
                }

                if (line.Equals("Else", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("End If", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Next", StringComparison.OrdinalIgnoreCase))
                    return;

                ExecuteSimple(line, context);
            }
        }

        private static void ExecuteIf(string[] lines, ref int index, int end, string firstLine, CScriptContext context)
        {
            var thenIndex = firstLine.IndexOf(" Then", StringComparison.OrdinalIgnoreCase);
            if (thenIndex < 0)
                throw new CScriptRuntimeException("VBScript If statement expects Then.");

            var condition = firstLine[2..thenIndex].Trim();
            var rest = firstLine[(thenIndex + 5)..].Trim();
            if (rest.Length > 0)
            {
                var elseIndex = CScriptIndexOfWord(rest, "Else");
                if (CScriptOps.IsTruthy(CScriptVBExpression.Evaluate(condition, context)))
                    ExecuteSimple(elseIndex < 0 ? rest : rest[..elseIndex].Trim(), context);
                else if (elseIndex >= 0)
                    ExecuteSimple(rest[(elseIndex + 4)..].Trim(), context);
                return;
            }

            var elseLine = -1;
            var endIfLine = FindVbBlockEnd(lines, index + 1, end, "If ", "End If", "Else", out elseLine);
            var truthy = CScriptOps.IsTruthy(CScriptVBExpression.Evaluate(condition, context));
            if (truthy)
                ExecuteLines(lines, index + 1, elseLine >= 0 ? elseLine : endIfLine, context);
            else if (elseLine >= 0)
                ExecuteLines(lines, elseLine + 1, endIfLine, context);
            index = endIfLine;
        }

        private static void ExecuteForEach(string[] lines, ref int index, int end, string firstLine, CScriptContext context)
        {
            var match = Regex.Match(firstLine, @"^For\s+Each\s+([A-Za-z_][A-Za-z0-9_]*)\s+In\s+(.+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new CScriptRuntimeException("VBScript For Each expects 'For Each name In expression'.");

            var nextLine = FindVbBlockEnd(lines, index + 1, end, "For Each ", "Next", null, out _);
            foreach (var item in CScriptOps.Enumerate(CScriptVBExpression.Evaluate(match.Groups[2].Value, context)))
            {
                context.SetName(match.Groups[1].Value, item);
                ExecuteLines(lines, index + 1, nextLine, context);
            }
            index = nextLine;
        }

        private static int FindVbBlockEnd(string[] lines, int start, int end, string startPrefix, string endText, string? alternateText, out int alternateLine)
        {
            var depth = 0;
            alternateLine = -1;
            for (var i = start; i < end; i++)
            {
                var line = CScriptStripVbComment(lines[i]).Trim();
                if (line.StartsWith(startPrefix, StringComparison.OrdinalIgnoreCase))
                    depth++;
                if (depth == 0 && alternateText is not null && line.Equals(alternateText, StringComparison.OrdinalIgnoreCase))
                {
                    alternateLine = i;
                    continue;
                }
                if (line.Equals(endText, StringComparison.OrdinalIgnoreCase) || line.StartsWith(endText + " ", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth == 0)
                        return i;
                    depth--;
                }
            }
            throw new CScriptRuntimeException($"VBScript block missing '{endText}'.");
        }

        private static void ExecuteSimple(string line, CScriptContext context)
        {
            if (line.Length == 0)
                return;

            if (line.StartsWith("Dim ", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in CScriptSplitTopLevel(line[4..], ',').Select(static part => part.Trim()).Where(static part => part.Length > 0))
                    context.SetName(name, null);
                return;
            }

            if (line.StartsWith("Const ", StringComparison.OrdinalIgnoreCase))
                line = line[6..].Trim();

            if (line.StartsWith("Set ", StringComparison.OrdinalIgnoreCase))
                line = line[4..].Trim();

            if (line.StartsWith("WScript.Echo", StringComparison.OrdinalIgnoreCase))
            {
                var argsText = line["WScript.Echo".Length..].Trim();
                if (argsText.StartsWith("(", StringComparison.Ordinal) && argsText.EndsWith(")", StringComparison.Ordinal))
                    argsText = argsText[1..^1];
                var args = argsText.Length == 0
                    ? Array.Empty<object?>()
                    : CScriptSplitTopLevel(argsText, ',').Select(arg => CScriptVBExpression.Evaluate(arg, context)).ToArray();
                context.WScript.Echo(args);
                return;
            }

            if (line.StartsWith("WScript.Quit", StringComparison.OrdinalIgnoreCase))
            {
                var arg = line["WScript.Quit".Length..].Trim();
                throw new CScriptQuitException(arg.Length == 0 ? 0 : (int)CScriptOps.ToLong(CScriptVBExpression.Evaluate(arg, context)));
            }

            var assignment = CScriptFindVbAssignment(line);
            if (assignment >= 0)
            {
                var target = line[..assignment].Trim();
                var value = CScriptVBExpression.Evaluate(line[(assignment + 1)..], context);
                if (Regex.IsMatch(target, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    context.SetName(target, value);
                    return;
                }
                if (CScriptTrySplitMemberTarget(target, out var ownerExpression, out var member))
                {
                    CScriptOps.SetMember(CScriptVBExpression.Evaluate(ownerExpression, context), member, value);
                    return;
                }
                throw new CScriptRuntimeException($"Unsupported VBScript assignment target '{target}'.");
            }

            if (TryExecuteVbCall(line, context))
                return;

            _ = CScriptVBExpression.Evaluate(line, context);
        }

        private static bool TryExecuteVbCall(string line, CScriptContext context)
        {
            var split = CScriptFindFirstTopLevelWhitespace(line);
            if (split < 0)
                return false;

            var target = line[..split].Trim();
            var argsText = line[(split + 1)..].Trim();
            if (!target.Contains('.', StringComparison.Ordinal) || argsText.Length == 0)
                return false;

            var callable = CScriptVBExpression.Evaluate(target, context);
            var args = CScriptSplitTopLevel(argsText, ',').Select(arg => CScriptVBExpression.Evaluate(arg, context)).ToArray();
            _ = CScriptOps.Call(callable, args);
            return true;
        }
    }

    private static class CScriptExpression
    {
        public static object? Evaluate(string expression, CScriptContext context)
        {
            expression = CScriptStripOuterParens(expression.Trim());
            if (expression.Length == 0)
                return null;

            foreach (var ops in new[]
            {
                new[] { "||" },
                new[] { "&&" },
                new[] { "===", "!==", "==", "!=" },
                new[] { "<=", ">=", "<", ">" },
                new[] { "+", "-" },
                new[] { "*", "/", "%" },
            })
            {
                var opIndex = CScriptLastTopLevelOperator(expression, ops, allowUnary: ops[0] is "+" or "-");
                if (opIndex >= 0)
                {
                    var op = ops.First(candidate => string.CompareOrdinal(expression, opIndex, candidate, 0, candidate.Length) == 0);
                    var left = Evaluate(expression[..opIndex], context);
                    if (op == "||")
                        return CScriptOps.IsTruthy(left) ? left : Evaluate(expression[(opIndex + op.Length)..], context);
                    if (op == "&&")
                        return CScriptOps.IsTruthy(left) ? Evaluate(expression[(opIndex + op.Length)..], context) : left;
                    var right = Evaluate(expression[(opIndex + op.Length)..], context);
                    return CScriptOps.ApplyBinary(op, left, right);
                }
            }

            if (expression.StartsWith("!", StringComparison.Ordinal))
                return !CScriptOps.IsTruthy(Evaluate(expression[1..], context));
            if (expression.StartsWith("-", StringComparison.Ordinal))
                return -CScriptOps.ToDouble(Evaluate(expression[1..], context));

            return EvaluatePostfix(expression, context);
        }

        private static object? EvaluatePostfix(string expression, CScriptContext context)
        {
            var index = 0;
            var value = ParsePrimary(expression, ref index, context);
            while (true)
            {
                index = CScriptSkipWhitespaceOnly(expression, index);
                if (index >= expression.Length)
                    return value;

                if (expression[index] == '.')
                {
                    index++;
                    var member = CScriptReadIdentifier(expression, ref index);
                    value = CScriptOps.GetMember(value, member);
                    continue;
                }

                if (expression[index] == '(')
                {
                    var argsText = CScriptReadBalancedRequired(expression, ref index, '(', ')', "call");
                    var args = argsText.Trim().Length == 0
                        ? Array.Empty<object?>()
                        : CScriptSplitTopLevel(argsText, ',').Select(arg => Evaluate(arg, context)).ToArray();
                    value = CScriptOps.Call(value, args);
                    continue;
                }

                if (expression[index] == '[')
                {
                    var indexText = CScriptReadBalancedRequired(expression, ref index, '[', ']', "index");
                    value = CScriptOps.GetIndex(value, Evaluate(indexText, context));
                    continue;
                }

                throw new CScriptRuntimeException($"Cannot parse expression near '{expression[index..]}'.");
            }
        }

        private static object? ParsePrimary(string expression, ref int index, CScriptContext context)
        {
            index = CScriptSkipWhitespaceOnly(expression, index);
            if (index >= expression.Length)
                return null;

            if (expression[index] is '"' or '\'')
                return CScriptReadStringLiteral(expression, ref index);

            if (expression[index] == '(')
                return Evaluate(CScriptReadBalancedRequired(expression, ref index, '(', ')', "expression"), context);

            if (expression[index] == '[')
            {
                var arrayText = CScriptReadBalancedRequired(expression, ref index, '[', ']', "array");
                return arrayText.Trim().Length == 0
                    ? new List<object?>()
                    : CScriptSplitTopLevel(arrayText, ',').Select(arg => Evaluate(arg, context)).ToList();
            }

            if (char.IsDigit(expression[index]))
            {
                var start = index;
                while (index < expression.Length && (char.IsDigit(expression[index]) || expression[index] == '.')) index++;
                var text = expression[start..index];
                return text.Contains('.', StringComparison.Ordinal)
                    ? double.Parse(text, CultureInfo.InvariantCulture)
                    : long.Parse(text, CultureInfo.InvariantCulture);
            }

            var identifier = CScriptReadIdentifier(expression, ref index);
            if (identifier.Equals("new", StringComparison.Ordinal))
            {
                var ctor = CScriptReadIdentifier(expression, ref index);
                index = CScriptSkipWhitespaceOnly(expression, index);
                var argsText = index < expression.Length && expression[index] == '('
                    ? CScriptReadBalancedRequired(expression, ref index, '(', ')', "new")
                    : "";
                var args = argsText.Trim().Length == 0
                    ? Array.Empty<object?>()
                    : CScriptSplitTopLevel(argsText, ',').Select(arg => Evaluate(arg, context)).ToArray();
                if (ctor.Equals("Enumerator", StringComparison.OrdinalIgnoreCase))
                    return new CScriptEnumeratorObject(CScriptOps.Enumerate(CScriptArg(args, 0, "Enumerator")).ToList());
                throw new CScriptRuntimeException($"Unsupported constructor '{ctor}'.");
            }

            return identifier switch
            {
                "true" => true,
                "false" => false,
                "null" or "undefined" => null,
                _ => context.GetName(identifier),
            };
        }
    }

    private static class CScriptVBExpression
    {
        public static object? Evaluate(string expression, CScriptContext context)
        {
            expression = CScriptStripOuterParens(expression.Trim());
            if (expression.Length == 0)
                return null;

            var concat = CScriptLastTopLevelOperator(expression, ["&"], allowUnary: false);
            if (concat >= 0)
                return CScriptOps.ToString(Evaluate(expression[..concat], context)) + CScriptOps.ToString(Evaluate(expression[(concat + 1)..], context));

            foreach (var ops in new[]
            {
                new[] { "Or" },
                new[] { "And" },
                new[] { "<>", ">=", "<=", "=", "<", ">" },
                new[] { "+", "-" },
                new[] { "*", "/", "Mod" },
            })
            {
                var opIndex = CScriptLastTopLevelVbOperator(expression, ops);
                if (opIndex >= 0)
                {
                    var op = ops.First(candidate => CScriptVbOperatorAt(expression, opIndex, candidate));
                    var left = Evaluate(expression[..opIndex], context);
                    var right = Evaluate(expression[(opIndex + op.Length)..], context);
                    return CScriptOps.ApplyVbBinary(op, left, right);
                }
            }

            if (expression.StartsWith("Not ", StringComparison.OrdinalIgnoreCase))
                return !CScriptOps.IsTruthy(Evaluate(expression[4..], context));

            var jsExpression = Regex.Replace(expression, @"\bNothing\b", "null", RegexOptions.IgnoreCase);
            jsExpression = Regex.Replace(jsExpression, @"\bTrue\b", "true", RegexOptions.IgnoreCase);
            jsExpression = Regex.Replace(jsExpression, @"\bFalse\b", "false", RegexOptions.IgnoreCase);
            return CScriptExpression.Evaluate(jsExpression, context);
        }
    }

    private abstract class CScriptObject
    {
        public virtual object? GetMember(string name) => throw new CScriptRuntimeException($"Object has no member '{name}'.");
        public virtual void SetMember(string name, object? value) => throw new CScriptRuntimeException($"Object member '{name}' is read-only or unsupported.");
        public virtual object? Invoke(IReadOnlyList<object?> args) => throw new CScriptRuntimeException("Object is not callable.");
        public virtual IEnumerable<object?> Enumerate() => throw new CScriptRuntimeException("Object is not enumerable.");
        public virtual object? GetIndex(object? index) => throw new CScriptRuntimeException("Object is not indexable.");
    }

    private sealed class CScriptBoundMethod : CScriptObject
    {
        private readonly Func<IReadOnlyList<object?>, object?> _body;
        public CScriptBoundMethod(Func<IReadOnlyList<object?>, object?> body) { _body = body; }
        public override object? Invoke(IReadOnlyList<object?> args) => _body(args);
    }

    private sealed class CScriptWScriptObject : CScriptObject
    {
        private readonly CScriptContext _context;
        private readonly CScriptArgumentsObject _arguments;
        private readonly CScriptTextStreamObject _stdIn;
        private readonly CScriptTextStreamObject _stdOut;
        private readonly CScriptTextStreamObject _stdErr;

        public CScriptWScriptObject(CScriptContext context)
        {
            _context = context;
            _arguments = new CScriptArgumentsObject(context.Options.ScriptArgs);
            _stdIn = CScriptTextStreamObject.ForReader(context.Invocation.Input);
            _stdOut = CScriptTextStreamObject.ForWriter(context.Invocation.Output);
            _stdErr = CScriptTextStreamObject.ForWriter(context.Invocation.Error);
        }

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "arguments" => _arguments,
                "stdin" => _stdIn,
                "stdout" => _stdOut,
                "stderr" => _stdErr,
                "scriptname" => _context.ScriptName,
                "scriptfullname" => _context.ScriptFullName,
                "fullname" => "/Windows/System32/cscript.exe",
                "path" => "/Windows/System32",
                "echo" => new CScriptBoundMethod(args => { Echo(args); return null; }),
                "quit" => new CScriptBoundMethod(args => throw new CScriptQuitException(args.Count == 0 ? 0 : (int)CScriptOps.ToLong(args[0]))),
                "sleep" => new CScriptBoundMethod(_ => null),
                "createobject" => new CScriptBoundMethod(args => CreateObject(CScriptOps.ToString(CScriptArg(args, 0, "CreateObject")))),
                _ => base.GetMember(name),
            };

        public void Echo(IReadOnlyList<object?> args)
        {
            _context.Invocation.Output.WriteLine(string.Join(" ", args.Select(CScriptOps.ToString)));
        }

        public object? CreateObject(string progId)
            => progId.Trim().ToLowerInvariant() switch
            {
                "scripting.filesystemobject" => new CScriptFileSystemObject(_context),
                "wscript.shell" => new CScriptShellObject(_context),
                _ => throw new CScriptRuntimeException($"Unsupported automation object '{progId}'."),
            };
    }

    private sealed class CScriptArgumentsObject : CScriptObject
    {
        private readonly IReadOnlyList<string> _args;
        public CScriptArgumentsObject(IReadOnlyList<string> args) { _args = args; }

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "count" or "length" => _args.Count,
                "item" => new CScriptBoundMethod(args => _args[(int)CScriptOps.ToLong(CScriptArg(args, 0, "Item"))]),
                "named" => new CScriptNamedArgumentsObject(_args),
                "unnamed" => this,
                _ => base.GetMember(name),
            };

        public override object? Invoke(IReadOnlyList<object?> args)
            => _args[(int)CScriptOps.ToLong(CScriptArg(args, 0, "Arguments"))];

        public override object? GetIndex(object? index)
            => _args[(int)CScriptOps.ToLong(index)];

        public override IEnumerable<object?> Enumerate()
            => _args.Cast<object?>();
    }

    private sealed class CScriptNamedArgumentsObject : CScriptObject
    {
        private readonly Dictionary<string, string> _values;

        public CScriptNamedArgumentsObject(IEnumerable<string> args)
        {
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in args)
            {
                var text = arg.TrimStart('/', '-');
                var split = text.IndexOfAny([':', '=']);
                if (split > 0)
                    _values[text[..split]] = text[(split + 1)..];
            }
        }

        public override object? GetMember(string name)
            => name.Equals("exists", StringComparison.OrdinalIgnoreCase)
                ? new CScriptBoundMethod(args => _values.ContainsKey(CScriptOps.ToString(CScriptArg(args, 0, "Exists"))))
                : base.GetMember(name);

        public override object? Invoke(IReadOnlyList<object?> args)
            => _values.TryGetValue(CScriptOps.ToString(CScriptArg(args, 0, "Named")), out var value) ? value : "";
    }

    private sealed class CScriptEnumeratorObject : CScriptObject
    {
        private readonly IReadOnlyList<object?> _values;
        private int _index;

        public CScriptEnumeratorObject(IReadOnlyList<object?> values)
        {
            _values = values;
        }

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "atend" => new CScriptBoundMethod(_ => _index >= _values.Count),
                "movenext" => new CScriptBoundMethod(_ => { _index++; return null; }),
                "item" => new CScriptBoundMethod(_ => _index < _values.Count ? _values[_index] : null),
                _ => base.GetMember(name),
            };
    }

    private sealed class CScriptTextStreamObject : CScriptObject
    {
        private readonly TextReader? _reader;
        private readonly TextWriter? _writer;
        private readonly VirtualFileSystem? _vfs;
        private readonly string? _path;
        private string _buffer = "";
        private int _position;
        private bool _closed;

        private CScriptTextStreamObject(TextReader reader) { _reader = reader; }
        private CScriptTextStreamObject(TextWriter writer) { _writer = writer; }

        private CScriptTextStreamObject(VirtualFileSystem vfs, string path, int mode, bool create)
        {
            _vfs = vfs;
            _path = vfs.Normalize(path);
            if (mode == 1)
            {
                if (vfs.Resolve(_path) is not VfsFile file)
                    throw new CScriptRuntimeException($"File not found: '{path}'.");
                _buffer = file.ReadText();
            }
            else if (mode == 2)
            {
                if (vfs.Resolve(_path) is not VfsFile && !create)
                    throw new CScriptRuntimeException($"File not found: '{path}'.");
                vfs.CreateTextFile(_path, "", overwrite: true);
                _writer = new StringWriter(CultureInfo.InvariantCulture);
            }
            else if (mode == 8)
            {
                if (vfs.Resolve(_path) is not VfsFile)
                {
                    if (!create)
                        throw new CScriptRuntimeException($"File not found: '{path}'.");
                    vfs.CreateTextFile(_path, "", overwrite: false);
                }
                _writer = new StringWriter(CultureInfo.InvariantCulture);
            }
            else
            {
                throw new CScriptRuntimeException($"Unsupported OpenTextFile mode '{mode}'.");
            }
        }

        public static CScriptTextStreamObject ForReader(TextReader reader) => new(reader);
        public static CScriptTextStreamObject ForWriter(TextWriter writer) => new(writer);
        public static CScriptTextStreamObject ForFile(VirtualFileSystem vfs, string path, int mode, bool create) => new(vfs, path, mode, create);

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "readline" => new CScriptBoundMethod(_ => ReadLine()),
                "read" => new CScriptBoundMethod(args => Read((int)CScriptOps.ToLong(CScriptArg(args, 0, "Read")))),
                "readall" => new CScriptBoundMethod(_ => ReadAll()),
                "write" => new CScriptBoundMethod(args => { Write(string.Concat(args.Select(CScriptOps.ToString))); return null; }),
                "writeline" => new CScriptBoundMethod(args => { WriteLine(string.Concat(args.Select(CScriptOps.ToString))); return null; }),
                "writeblanklines" => new CScriptBoundMethod(args => { Write(new string('\n', (int)CScriptOps.ToLong(CScriptArg(args, 0, "WriteBlankLines")))); return null; }),
                "close" => new CScriptBoundMethod(_ => { Close(); return null; }),
                "atendofstream" => AtEndOfStream(),
                _ => base.GetMember(name),
            };

        private string ReadLine()
        {
            EnsureOpen();
            if (_reader is not null)
                return _reader.ReadLine() ?? "";
            var newline = _buffer.IndexOf('\n', _position);
            if (newline < 0)
            {
                var tail = _buffer[_position..];
                _position = _buffer.Length;
                return tail.TrimEnd('\r');
            }
            var line = _buffer[_position..newline].TrimEnd('\r');
            _position = newline + 1;
            return line;
        }

        private string Read(int count)
        {
            EnsureOpen();
            if (_reader is not null)
            {
                var buffer = new char[count];
                var read = _reader.Read(buffer, 0, count);
                return new string(buffer, 0, read);
            }
            var length = Math.Min(count, _buffer.Length - _position);
            var result = _buffer.Substring(_position, length);
            _position += length;
            return result;
        }

        private string ReadAll()
        {
            EnsureOpen();
            if (_reader is not null)
                return ReadAllText(_reader);
            var result = _buffer[_position..];
            _position = _buffer.Length;
            return result;
        }

        private void Write(string text)
        {
            EnsureOpen();
            if (_writer is not null && _vfs is null)
            {
                _writer.Write(text);
                return;
            }
            if (_vfs is null || _path is null)
                throw new CScriptRuntimeException("TextStream is not writable.");
            if (_vfs.Resolve(_path) is VfsFile file)
                file.AppendText(text);
            else
                _vfs.CreateTextFile(_path, text, overwrite: false);
        }

        private void WriteLine(string text) => Write(text + Environment.NewLine);

        private bool AtEndOfStream()
        {
            EnsureOpen();
            if (_reader is not null)
                return _reader.Peek() < 0;
            return _position >= _buffer.Length;
        }

        private void Close() => _closed = true;

        private void EnsureOpen()
        {
            if (_closed)
                throw new CScriptRuntimeException("TextStream is closed.");
        }
    }

    private sealed class CScriptFileSystemObject : CScriptObject
    {
        private readonly CScriptContext _context;
        private VirtualFileSystem Vfs => _context.Invocation.Vfs;

        public CScriptFileSystemObject(CScriptContext context)
        {
            _context = context;
        }

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "fileexists" => new CScriptBoundMethod(args => Vfs.IsFile(CScriptOps.ToString(CScriptArg(args, 0, "FileExists")))),
                "folderexists" => new CScriptBoundMethod(args => Vfs.IsDirectory(CScriptOps.ToString(CScriptArg(args, 0, "FolderExists")))),
                "createfolder" => new CScriptBoundMethod(args => Vfs.CreateDirectory(CScriptOps.ToString(CScriptArg(args, 0, "CreateFolder")))),
                "deletefile" => new CScriptBoundMethod(args => { Vfs.Delete(CScriptOps.ToString(CScriptArg(args, 0, "DeleteFile")), false, ArgBool(args, 1, false)); return null; }),
                "deletefolder" => new CScriptBoundMethod(args => { Vfs.Delete(CScriptOps.ToString(CScriptArg(args, 0, "DeleteFolder")), true, ArgBool(args, 1, false)); return null; }),
                "copyfile" => new CScriptBoundMethod(args => { Copy(CScriptOps.ToString(CScriptArg(args, 0, "CopyFile")), CScriptOps.ToString(CScriptArg(args, 1, "CopyFile")), false, ArgBool(args, 2, true)); return null; }),
                "copyfolder" => new CScriptBoundMethod(args => { Copy(CScriptOps.ToString(CScriptArg(args, 0, "CopyFolder")), CScriptOps.ToString(CScriptArg(args, 1, "CopyFolder")), true, ArgBool(args, 2, true)); return null; }),
                "movefile" => new CScriptBoundMethod(args => { Vfs.Move(CScriptOps.ToString(CScriptArg(args, 0, "MoveFile")), CScriptOps.ToString(CScriptArg(args, 1, "MoveFile"))); return null; }),
                "movefolder" => new CScriptBoundMethod(args => { Vfs.Move(CScriptOps.ToString(CScriptArg(args, 0, "MoveFolder")), CScriptOps.ToString(CScriptArg(args, 1, "MoveFolder"))); return null; }),
                "opentextfile" => new CScriptBoundMethod(args => CScriptTextStreamObject.ForFile(Vfs, CScriptOps.ToString(CScriptArg(args, 0, "OpenTextFile")), args.Count > 1 ? (int)CScriptOps.ToLong(args[1]) : 1, ArgBool(args, 2, false))),
                "createtextfile" => new CScriptBoundMethod(args => CreateTextFile(args)),
                "getfilename" => new CScriptBoundMethod(args => VfsPath.SplitLeaf(Vfs.Normalize(CScriptOps.ToString(CScriptArg(args, 0, "GetFileName")))).Leaf),
                "getbasename" => new CScriptBoundMethod(args => GetBaseName(CScriptOps.ToString(CScriptArg(args, 0, "GetBaseName")))),
                "getextensionname" => new CScriptBoundMethod(args => VfsPath.GetExtension(Vfs.Normalize(CScriptOps.ToString(CScriptArg(args, 0, "GetExtensionName")))).TrimStart('.')),
                "getparentfoldername" => new CScriptBoundMethod(args => VfsPath.SplitLeaf(Vfs.Normalize(CScriptOps.ToString(CScriptArg(args, 0, "GetParentFolderName")))).Parent),
                "buildpath" => new CScriptBoundMethod(args => VfsPath.Join(CScriptOps.ToString(CScriptArg(args, 0, "BuildPath")), CScriptOps.ToString(CScriptArg(args, 1, "BuildPath")))),
                "getabsolutepathname" => new CScriptBoundMethod(args => Vfs.Normalize(CScriptOps.ToString(CScriptArg(args, 0, "GetAbsolutePathName")))),
                _ => base.GetMember(name),
            };

        private object? CreateTextFile(IReadOnlyList<object?> args)
        {
            var path = CScriptOps.ToString(CScriptArg(args, 0, "CreateTextFile"));
            var overwrite = ArgBool(args, 1, true);
            Vfs.CreateTextFile(path, "", overwrite);
            return CScriptTextStreamObject.ForFile(Vfs, path, 8, create: true);
        }

        private void Copy(string source, string destination, bool recursive, bool overwrite)
        {
            if (!overwrite && Vfs.Exists(destination))
                throw new CScriptRuntimeException($"Destination already exists: '{destination}'.");
            Vfs.Copy(source, destination, recursive);
        }

        private string GetBaseName(string path)
        {
            var leaf = VfsPath.SplitLeaf(Vfs.Normalize(path)).Leaf;
            var dot = leaf.LastIndexOf('.');
            return dot <= 0 ? leaf : leaf[..dot];
        }
    }

    private sealed class CScriptShellObject : CScriptObject
    {
        private readonly CScriptContext _context;

        public CScriptShellObject(CScriptContext context)
        {
            _context = context;
        }

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "environment" => new CScriptBoundMethod(args => new CScriptEnvironmentObject(_context, args.Count == 0 ? "Process" : CScriptOps.ToString(args[0]))),
                "expandenvironmentstrings" => new CScriptBoundMethod(args => ExpandEnvironmentStrings(CScriptOps.ToString(CScriptArg(args, 0, "ExpandEnvironmentStrings")))),
                "run" => new CScriptBoundMethod(Run),
                "exec" => new CScriptBoundMethod(_ => throw new CScriptRuntimeException("WScript.Shell.Exec is not implemented because Carbide has no async process object model.")),
                "regread" or "regwrite" or "regdelete" => new CScriptBoundMethod(_ => throw new CScriptRuntimeException("Registry access is outside the Carbide VFS sandbox.")),
                _ => base.GetMember(name),
            };

        private string ExpandEnvironmentStrings(string text)
            => Regex.Replace(text, "%([^%]+)%", match => _context.Invocation.Env.Get(match.Groups[1].Value) ?? match.Value);

        private object? Run(IReadOnlyList<object?> args)
        {
            var command = CScriptOps.ToString(CScriptArg(args, 0, "Run"));
            var wait = args.Count < 3 || CScriptOps.IsTruthy(args[2]);
            var tokens = ShellArgTokenizer.Tokenize(command);
            if (tokens.Count == 0)
                return 0;

            var code = DispatchCommand(
                _context.Invocation,
                tokens[0],
                tokens.Skip(1).ToArray(),
                "cmd",
                _context.Invocation.Input,
                _context.Invocation.Output,
                _context.Invocation.Error);
            return wait ? code : 0;
        }
    }

    private sealed class CScriptEnvironmentObject : CScriptObject
    {
        private readonly CScriptContext _context;
        private readonly string _scope;

        public CScriptEnvironmentObject(CScriptContext context, string scope)
        {
            _context = context;
            _scope = scope;
            if (!_scope.Equals("Process", StringComparison.OrdinalIgnoreCase))
                throw new CScriptRuntimeException("Only WScript.Shell.Environment(\"Process\") is implemented.");
        }

        public override object? GetMember(string name)
            => name.ToLowerInvariant() switch
            {
                "item" => new CScriptBoundMethod(args => _context.Invocation.Env.Get(CScriptOps.ToString(CScriptArg(args, 0, "Environment"))) ?? ""),
                "set" => new CScriptBoundMethod(args => { _context.Invocation.Env.Set(CScriptOps.ToString(CScriptArg(args, 0, "Environment.Set")), CScriptOps.ToString(CScriptArg(args, 1, "Environment.Set"))); return null; }),
                "remove" => new CScriptBoundMethod(args => { _context.Invocation.Env.Unset(CScriptOps.ToString(CScriptArg(args, 0, "Environment.Remove"))); return null; }),
                _ => base.GetMember(name),
            };

        public override object? Invoke(IReadOnlyList<object?> args)
            => _context.Invocation.Env.Get(CScriptOps.ToString(CScriptArg(args, 0, "Environment"))) ?? "";
    }

    private static class CScriptOps
    {
        public static object? GetMember(object? target, string name)
        {
            if (target is CScriptObject obj) return obj.GetMember(name);
            if (target is string s && name.Equals("length", StringComparison.OrdinalIgnoreCase)) return s.Length;
            if (target is List<object?> list && name.Equals("length", StringComparison.OrdinalIgnoreCase)) return list.Count;
            if (target is Dictionary<string, object?> dict && dict.TryGetValue(name, out var value)) return value;
            throw new CScriptRuntimeException($"Object has no member '{name}'.");
        }

        public static void SetMember(object? target, string name, object? value)
        {
            if (target is CScriptObject obj)
            {
                obj.SetMember(name, value);
                return;
            }
            if (target is Dictionary<string, object?> dict)
            {
                dict[name] = value;
                return;
            }
            throw new CScriptRuntimeException($"Object member '{name}' is read-only or unsupported.");
        }

        public static object? Call(object? target, IReadOnlyList<object?> args)
        {
            if (target is CScriptObject obj) return obj.Invoke(args);
            throw new CScriptRuntimeException("Expression is not callable.");
        }

        public static object? GetIndex(object? target, object? index)
            => target switch
            {
                CScriptObject obj => obj.GetIndex(index),
                string s => s[(int)ToLong(index)].ToString(),
                List<object?> list => list[(int)ToLong(index)],
                Dictionary<string, object?> dict => dict.TryGetValue(ToString(index), out var value) ? value : null,
                _ => throw new CScriptRuntimeException("Expression is not indexable."),
            };

        public static IEnumerable<object?> Enumerate(object? value)
            => value switch
            {
                null => [],
                CScriptObject obj => obj.Enumerate(),
                List<object?> list => list,
                string s => s.Select(ch => (object?)ch.ToString()),
                IEnumerable<object?> enumerable => enumerable,
                _ => throw new CScriptRuntimeException("Expression is not enumerable."),
            };

        public static object? ApplyBinary(string op, object? left, object? right)
            => op switch
            {
                "+" => Add(left, right),
                "-" => ToDouble(left) - ToDouble(right),
                "*" => ToDouble(left) * ToDouble(right),
                "/" => ToDouble(left) / ToDouble(right),
                "%" => ToLong(left) % ToLong(right),
                "==" or "===" => CompareEqual(left, right),
                "!=" or "!==" => !CompareEqual(left, right),
                "<" => Compare(left, right) < 0,
                ">" => Compare(left, right) > 0,
                "<=" => Compare(left, right) <= 0,
                ">=" => Compare(left, right) >= 0,
                _ => throw new CScriptRuntimeException($"Unsupported operator '{op}'."),
            };

        public static object? ApplyVbBinary(string op, object? left, object? right)
            => op.ToLowerInvariant() switch
            {
                "or" => IsTruthy(left) || IsTruthy(right),
                "and" => IsTruthy(left) && IsTruthy(right),
                "=" => CompareEqual(left, right),
                "<>" => !CompareEqual(left, right),
                "<" => Compare(left, right) < 0,
                ">" => Compare(left, right) > 0,
                "<=" => Compare(left, right) <= 0,
                ">=" => Compare(left, right) >= 0,
                "+" => Add(left, right),
                "-" => ToDouble(left) - ToDouble(right),
                "*" => ToDouble(left) * ToDouble(right),
                "/" => ToDouble(left) / ToDouble(right),
                "mod" => ToLong(left) % ToLong(right),
                _ => throw new CScriptRuntimeException($"Unsupported operator '{op}'."),
            };

        public static object? Add(object? left, object? right)
        {
            if (left is string || right is string)
                return ToString(left) + ToString(right);
            return IsFloaty(left) || IsFloaty(right)
                ? ToDouble(left) + ToDouble(right)
                : ToLong(left) + ToLong(right);
        }

        public static bool IsTruthy(object? value)
            => value switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                int i => i != 0,
                long l => l != 0,
                double d => Math.Abs(d) > 0.0000001,
                _ => true,
            };

        public static bool CompareEqual(object? left, object? right)
        {
            if (left is null || right is null) return left is null && right is null;
            if (left is string || right is string) return string.Equals(ToString(left), ToString(right), StringComparison.Ordinal);
            if (left is bool or int or long or double || right is bool or int or long or double)
                return Math.Abs(ToDouble(left) - ToDouble(right)) < 0.0000001;
            return left.Equals(right);
        }

        public static int Compare(object? left, object? right)
        {
            if (left is string || right is string)
                return string.CompareOrdinal(ToString(left), ToString(right));
            return ToDouble(left).CompareTo(ToDouble(right));
        }

        public static string ToString(object? value)
            => value switch
            {
                null => "",
                bool b => b ? "True" : "False",
                double d => d.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };

        public static long ToLong(object? value)
            => value switch
            {
                null => 0,
                bool b => b ? 1 : 0,
                int i => i,
                long l => l,
                double d => (long)d,
                string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
                _ => throw new CScriptRuntimeException($"Cannot convert '{ToString(value)}' to integer."),
            };

        public static double ToDouble(object? value)
            => value switch
            {
                null => 0,
                bool b => b ? 1 : 0,
                int i => i,
                long l => l,
                double d => d,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => throw new CScriptRuntimeException($"Cannot convert '{ToString(value)}' to number."),
            };

        private static bool IsFloaty(object? value) => value is double or float or decimal;
    }

    private static object? CScriptArg(IReadOnlyList<object?> args, int index, string function)
        => index < args.Count ? args[index] : throw new CScriptRuntimeException($"{function} missing required argument.");

    private static bool ArgBool(IReadOnlyList<object?> args, int index, bool defaultValue)
        => index < args.Count ? CScriptOps.IsTruthy(args[index]) : defaultValue;

    private static int CScriptSkipTrivia(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] == ';') { index++; continue; }
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
            {
                index = text.IndexOf('\n', index);
                if (index < 0) return text.Length;
                continue;
            }
            break;
        }
        return index;
    }

    private static int CScriptSkipWhitespaceOnly(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private static bool CScriptStartsKeyword(string text, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;
        if (!string.Equals(text.Substring(index, keyword.Length), keyword, StringComparison.Ordinal))
            return false;
        var beforeOk = index == 0 || !CScriptIsIdentifierChar(text[index - 1]);
        var after = index + keyword.Length;
        var afterOk = after >= text.Length || !CScriptIsIdentifierChar(text[after]);
        return beforeOk && afterOk;
    }

    private static string CScriptReadBody(string source, ref int index)
    {
        index = CScriptSkipTrivia(source, index);
        if (index < source.Length && source[index] == '{')
            return CScriptReadBalancedRequired(source, ref index, '{', '}', "block");
        return CScriptReadSimpleStatement(source, ref index);
    }

    private static string CScriptReadSimpleStatement(string source, ref int index)
    {
        var start = index;
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var quote = '\0';
        for (; index < source.Length; index++)
        {
            var ch = source[index];
            if (quote != '\0')
            {
                if (ch == '\\') { index++; continue; }
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; continue; }
            switch (ch)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': if (brace == 0) return source[start..index]; brace--; break;
            }
            if ((ch == ';' || ch == '\n') && paren == 0 && bracket == 0 && brace == 0)
            {
                var result = source[start..index];
                index++;
                return result;
            }
        }
        return source[start..index];
    }

    private static string CScriptReadBalancedRequired(string text, ref int index, char open, char close, string construct)
    {
        if (!CScriptTryReadBalanced(text, index, open, close, out var body, out var after))
            throw new CScriptRuntimeException($"{construct} expects balanced {open}{close}.");
        index = after;
        return body;
    }

    private static bool CScriptTryReadBalanced(string text, int start, char open, char close, out string body, out int after)
    {
        body = "";
        after = start;
        start = CScriptSkipWhitespaceOnly(text, start);
        if (start >= text.Length || text[start] != open)
            return false;

        var sb = new StringBuilder();
        var depth = 0;
        var quote = '\0';
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < text.Length)
                {
                    if (depth > 0) sb.Append(ch).Append(text[++i]);
                    continue;
                }
                if (ch == quote) quote = '\0';
                if (depth > 0) sb.Append(ch);
                continue;
            }
            if (ch is '"' or '\'')
            {
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

    private static IReadOnlyList<string> CScriptSplitTopLevel(string text, char separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                current.Append(ch);
                if (ch == '\\' && i + 1 < text.Length)
                    current.Append(text[++i]);
                else if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
            {
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

    private static int CScriptFindAssignment(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!CScriptIsTopLevelAt(text, i) || text[i] != '=')
                continue;
            var prev = i > 0 ? text[i - 1] : '\0';
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (prev is '<' or '>' or '!' or '=' || next == '=')
                continue;
            return i;
        }
        return -1;
    }

    private static int CScriptFindVbAssignment(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && CScriptIsTopLevelAt(text, i))
                return i;
        }
        return -1;
    }

    private static int CScriptLastTopLevelOperator(string text, IReadOnlyList<string> operators, bool allowUnary)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!CScriptIsTopLevelAt(text, i))
                continue;
            foreach (var op in operators.OrderByDescending(static op => op.Length))
            {
                if (i + op.Length > text.Length || string.CompareOrdinal(text, i, op, 0, op.Length) != 0)
                    continue;
                if (!allowUnary || op is not ("+" or "-") || !CScriptIsUnaryOperatorPosition(text, i))
                    return i;
            }
        }
        return -1;
    }

    private static int CScriptLastTopLevelVbOperator(string text, IReadOnlyList<string> operators)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!CScriptIsTopLevelAt(text, i))
                continue;
            foreach (var op in operators.OrderByDescending(static op => op.Length))
            {
                if (CScriptVbOperatorAt(text, i, op))
                    return i;
            }
        }
        return -1;
    }

    private static bool CScriptVbOperatorAt(string text, int index, string op)
    {
        if (index + op.Length > text.Length)
            return false;
        if (!string.Equals(text.Substring(index, op.Length), op, StringComparison.OrdinalIgnoreCase))
            return false;
        if (char.IsLetter(op[0]))
        {
            var beforeOk = index == 0 || !CScriptIsIdentifierChar(text[index - 1]);
            var after = index + op.Length;
            var afterOk = after >= text.Length || !CScriptIsIdentifierChar(text[after]);
            return beforeOk && afterOk;
        }
        return true;
    }

    private static bool CScriptIsTopLevelAt(string text, int index)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var quote = '\0';
        for (var i = 0; i < index; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\') i++;
                else if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; continue; }
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
        return paren == 0 && bracket == 0 && brace == 0 && quote == '\0';
    }

    private static bool CScriptIsUnaryOperatorPosition(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                continue;
            return "([{:;,=+-*/%!<>|&".Contains(text[i], StringComparison.Ordinal);
        }
        return true;
    }

    private static bool CScriptTrySplitMemberTarget(string target, out string ownerExpression, out string member)
    {
        ownerExpression = "";
        member = "";
        for (var i = target.Length - 1; i >= 0; i--)
        {
            if (target[i] != '.' || !CScriptIsTopLevelAt(target, i))
                continue;
            ownerExpression = target[..i].Trim();
            member = target[(i + 1)..].Trim();
            return ownerExpression.Length > 0 && member.Length > 0;
        }
        return false;
    }

    private static int CScriptFindFirstTopLevelWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) && CScriptIsTopLevelAt(text, i))
                return i;
        }
        return -1;
    }

    private static int CScriptIndexOfWord(string text, string word)
    {
        for (var i = 0; i <= text.Length - word.Length; i++)
        {
            if (!CScriptIsTopLevelAt(text, i))
                continue;
            if (string.Equals(text.Substring(i, word.Length), word, StringComparison.OrdinalIgnoreCase)
                && (i == 0 || !CScriptIsIdentifierChar(text[i - 1]))
                && (i + word.Length >= text.Length || !CScriptIsIdentifierChar(text[i + word.Length])))
                return i;
        }
        return -1;
    }

    private static string CScriptStripOuterParens(string text)
    {
        while (text.StartsWith("(", StringComparison.Ordinal)
            && CScriptTryReadBalanced(text, 0, '(', ')', out var body, out var after)
            && after == text.Length)
        {
            text = body.Trim();
        }
        return text;
    }

    private static string CScriptReadIdentifier(string text, ref int index)
    {
        index = CScriptSkipWhitespaceOnly(text, index);
        if (index >= text.Length || !CScriptIsIdentifierStart(text[index]))
            throw new CScriptRuntimeException($"Expected identifier near '{(index < text.Length ? text[index..] : "")}'.");
        var start = index++;
        while (index < text.Length && CScriptIsIdentifierChar(text[index])) index++;
        return text[start..index];
    }

    private static bool CScriptIsIdentifierStart(char ch)
        => char.IsLetter(ch) || ch is '_' or '$';

    private static bool CScriptIsIdentifierChar(char ch)
        => char.IsLetterOrDigit(ch) || ch is '_' or '$';

    private static string CScriptReadStringLiteral(string text, ref int index)
    {
        var quote = text[index++];
        var sb = new StringBuilder();
        while (index < text.Length)
        {
            var ch = text[index++];
            if (ch == quote)
                return sb.ToString();
            if (ch == '\\' && index < text.Length)
            {
                var esc = text[index++];
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
                continue;
            }
            sb.Append(ch);
        }
        throw new CScriptRuntimeException("Unterminated string literal.");
    }

    private static string CScriptStripLineComment(string text)
    {
        var quote = '\0';
        for (var i = 0; i + 1 < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\') i++;
                else if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == '/' && text[i + 1] == '/')
                return text[..i];
        }
        return text;
    }

    private static string CScriptStripVbComment(string text)
    {
        var quote = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quote = !quote;
            if (!quote && text[i] == '\'')
                return text[..i];
        }
        return text;
    }

    private static string CScriptStripBlockComments(string text)
    {
        var sb = new StringBuilder();
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < text.Length)
                    sb.Append(text[++i]);
                else if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                sb.Append(ch);
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private sealed class CScriptRuntimeException : Exception
    {
        public CScriptRuntimeException(string message) : base(message) { }
    }

    private sealed class CScriptQuitException : Exception
    {
        public int ExitCode { get; }
        public CScriptQuitException(int exitCode) { ExitCode = exitCode; }
    }

    private sealed class CScriptBreakException : Exception { }
    private sealed class CScriptContinueException : Exception { }
}
