using System.Globalization;
using System.Text;
using CarbideCmd.Errors;
using CarbideCmd.Parser;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Runtime;

/// <summary>
/// Tree-walking cmd interpreter. Owns an execution context, a positional-parameter list, and
/// a <c>GOTO</c> resumption loop that drives a labeled AST. Invoked by the shell host for
/// each <c>ShellHost.Submit</c> call.
/// </summary>
public sealed class Interpreter
{
    public ShellExecutionContext Context { get; }
    public List<string> Positional { get; set; } = new();
    public int LastExitCode { get; set; }
    public bool EchoOn { get; set; } = true;
    public bool DelayedExpansion { get; set; }
    public TextWriter Echo => Context.Output;

    private readonly Dictionary<string, int> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<IDisposable> _setLocalScopes = new();
    private ScriptAst? _currentScript;

    public Interpreter(ShellExecutionContext context)
    {
        Context = context;
    }

    public int Execute(ScriptAst script)
    {
        _currentScript = script;
        _labels.Clear();
        for (int i = 0; i < script.Lines.Count; i++)
        {
            if (script.Lines[i] is LabelLineAst label)
                _labels[label.Name] = i;
        }

        int idx = 0;
        while (idx < script.Lines.Count)
        {
            var line = script.Lines[idx];
            try
            {
                if (line is CommandLineAst cmd)
                {
                    bool previousEcho = EchoOn;
                    if (cmd.EchoSuppressed) EchoOn = false;
                    try { ExecuteChain(cmd.Chain); }
                    finally { if (cmd.EchoSuppressed) EchoOn = previousEcho; }
                }
                idx++;
            }
            catch (CmdGotoException g)
            {
                if (g.Label.Equals("EOF", StringComparison.OrdinalIgnoreCase))
                {
                    return LastExitCode;
                }
                if (!_labels.TryGetValue(g.Label, out var target))
                    throw new CmdRuntimeException($"The system cannot find the batch label specified - {g.Label}");
                idx = target + 1;
            }
            catch (CmdExitException e)
            {
                LastExitCode = e.Code;
                return LastExitCode;
            }
        }
        return LastExitCode;
    }

    public async ValueTask<int> ExecuteAsync(
        ScriptAst script,
        CancellationToken cancellationToken = default)
    {
        _currentScript = script;
        _labels.Clear();
        for (int i = 0; i < script.Lines.Count; i++)
        {
            if (script.Lines[i] is LabelLineAst label)
                _labels[label.Name] = i;
        }

        int idx = 0;
        while (idx < script.Lines.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = script.Lines[idx];
            try
            {
                if (line is CommandLineAst cmd)
                {
                    bool previousEcho = EchoOn;
                    if (cmd.EchoSuppressed) EchoOn = false;
                    try { await ExecuteChainAsync(cmd.Chain, cancellationToken).ConfigureAwait(false); }
                    finally { if (cmd.EchoSuppressed) EchoOn = previousEcho; }
                }
                idx++;
            }
            catch (CmdGotoException g)
            {
                if (g.Label.Equals("EOF", StringComparison.OrdinalIgnoreCase))
                {
                    return LastExitCode;
                }
                if (!_labels.TryGetValue(g.Label, out var target))
                    throw new CmdRuntimeException($"The system cannot find the batch label specified - {g.Label}");
                idx = target + 1;
            }
            catch (CmdExitException e)
            {
                LastExitCode = e.Code;
                return LastExitCode;
            }
        }
        return LastExitCode;
    }

    private void ExecuteChain(CommandChainAst chain)
    {
        // Collect adjacent pipe segments first so a single buffered run can stitch them.
        var segments = new List<List<ChainedStatementAst>>();
        var current = new List<ChainedStatementAst>();
        for (int i = 0; i < chain.Items.Count; i++)
        {
            var item = chain.Items[i];
            if (item.Op == ChainOperator.Pipe && current.Count > 0)
            {
                current.Add(item);
                continue;
            }
            if (current.Count > 0) segments.Add(current);
            current = new List<ChainedStatementAst> { item };
        }
        if (current.Count > 0) segments.Add(current);

        foreach (var seg in segments)
        {
            var first = seg[0];
            switch (first.Op)
            {
                case ChainOperator.None:
                case ChainOperator.Sequence:
                    ExecutePipeline(seg);
                    break;
                case ChainOperator.And:
                    if (LastExitCode == 0) ExecutePipeline(seg);
                    break;
                case ChainOperator.Or:
                    if (LastExitCode != 0) ExecutePipeline(seg);
                    break;
                case ChainOperator.Pipe:
                    ExecutePipeline(seg);
                    break;
            }
        }
    }

    private async ValueTask ExecuteChainAsync(
        CommandChainAst chain,
        CancellationToken cancellationToken)
    {
        var segments = new List<List<ChainedStatementAst>>();
        var current = new List<ChainedStatementAst>();
        for (int i = 0; i < chain.Items.Count; i++)
        {
            var item = chain.Items[i];
            if (item.Op == ChainOperator.Pipe && current.Count > 0)
            {
                current.Add(item);
                continue;
            }
            if (current.Count > 0) segments.Add(current);
            current = new List<ChainedStatementAst> { item };
        }
        if (current.Count > 0) segments.Add(current);

        foreach (var seg in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = seg[0];
            switch (first.Op)
            {
                case ChainOperator.None:
                case ChainOperator.Sequence:
                    await ExecutePipelineAsync(seg, cancellationToken).ConfigureAwait(false);
                    break;
                case ChainOperator.And:
                    if (LastExitCode == 0) await ExecutePipelineAsync(seg, cancellationToken).ConfigureAwait(false);
                    break;
                case ChainOperator.Or:
                    if (LastExitCode != 0) await ExecutePipelineAsync(seg, cancellationToken).ConfigureAwait(false);
                    break;
                case ChainOperator.Pipe:
                    await ExecutePipelineAsync(seg, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private void ExecutePipeline(List<ChainedStatementAst> segment)
    {
        if (segment.Count == 1)
        {
            ExecuteStatement(segment[0].Statement);
            return;
        }
        TextReader stageIn = Context.Input;
        for (int i = 0; i < segment.Count; i++)
        {
            bool isLast = i == segment.Count - 1;
            if (isLast)
            {
                ExecuteStatementWith(segment[i].Statement, stageIn, Context.Output, Context.Error);
                break;
            }
            var capture = new StringWriter();
            ExecuteStatementWith(segment[i].Statement, stageIn, capture, Context.Error);
            stageIn = new StringReader(capture.ToString());
        }
    }

    private async ValueTask ExecutePipelineAsync(
        List<ChainedStatementAst> segment,
        CancellationToken cancellationToken)
    {
        if (segment.Count == 1)
        {
            await ExecuteStatementAsync(segment[0].Statement, cancellationToken, Context.Input, Context.Output, Context.Error)
                .ConfigureAwait(false);
            return;
        }
        TextReader stageIn = Context.Input;
        for (int i = 0; i < segment.Count; i++)
        {
            bool isLast = i == segment.Count - 1;
            if (isLast)
            {
                await ExecuteStatementAsync(segment[i].Statement, cancellationToken, stageIn, Context.Output, Context.Error)
                    .ConfigureAwait(false);
                break;
            }
            var capture = new StringWriter();
            await ExecuteStatementAsync(segment[i].Statement, cancellationToken, stageIn, capture, Context.Error)
                .ConfigureAwait(false);
            stageIn = new StringReader(capture.ToString());
        }
    }

    private void ExecuteStatement(StatementAst stmt) =>
        ExecuteStatementWith(stmt, Context.Input, Context.Output, Context.Error);

    private void ExecuteStatementWith(StatementAst stmt, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        switch (stmt)
        {
            case SimpleCommandAst sc:
                ExecuteSimple(sc, stdin, stdout, stderr);
                break;
            case IfStatementAst ifs:
                ExecuteIf(ifs, stdin, stdout, stderr);
                break;
            case GotoStatementAst g:
                throw new CmdGotoException(g.Label);
            case ExitStatementAst exit:
                throw new CmdExitException(exit.Code, exit.Branch);
            case SetLocalStatementAst setLocal:
                ExecuteSetLocal(setLocal);
                break;
            case EndLocalStatementAst:
                ExecuteEndLocal();
                break;
            case ForInStatementAst forIn:
                ExecuteForIn(forIn, stdin, stdout, stderr);
                break;
            case ForLStatementAst forL:
                ExecuteForL(forL, stdin, stdout, stderr);
                break;
            case CallLabelStatementAst callLabel:
                ExecuteCallLabel(callLabel, stdin, stdout, stderr);
                break;
            case CallScriptStatementAst callScript:
                ExecuteCallScript(callScript, stdin, stdout, stderr);
                break;
            case ChainStatementWrapperAst chainWrap:
                ExecuteChain(chainWrap.Chain);
                break;
            default:
                throw new CmdRuntimeException($"Unsupported statement shape: {stmt.GetType().Name}");
        }
    }

    private async ValueTask ExecuteStatementAsync(
        StatementAst stmt,
        CancellationToken cancellationToken,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (stmt is SimpleCommandAst sc)
        {
            await ExecuteSimpleAsync(sc, cancellationToken, stdin, stdout, stderr)
                .ConfigureAwait(false);
            return;
        }

        ExecuteStatementWith(stmt, stdin, stdout, stderr);
    }

    private void ExecuteSimple(SimpleCommandAst sc, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        // Redirections override the inherited streams.
        TextWriter finalStdout = stdout;
        TextWriter finalStderr = stderr;
        TextReader finalStdin = stdin;
        List<IDisposable> toDispose = new();

        try
        {
            foreach (var r in sc.Redirections)
            {
                switch (r)
                {
                    case StdoutRedirection sor:
                    {
                        var path = ExpandWord(sor.Target);
                        var writer = new VfsTextWriter(Context.Vfs, path, sor.Append);
                        toDispose.Add(writer);
                        finalStdout = writer;
                        break;
                    }
                    case StdinRedirection sir:
                    {
                        var path = ExpandWord(sir.Target);
                        var abs = Context.Vfs.Normalize(path);
                        var file = Context.Vfs.Resolve(abs) as VfsFile
                            ?? throw new CmdRuntimeException($"The system cannot find the file specified - {abs}");
                        finalStdin = new StringReader(file.ReadText());
                        break;
                    }
                    case StderrRedirection ser:
                    {
                        var path = ExpandWord(ser.Target);
                        var writer = new VfsTextWriter(Context.Vfs, path, append: false);
                        toDispose.Add(writer);
                        finalStderr = writer;
                        break;
                    }
                    case StderrMergeRedirection:
                        finalStderr = finalStdout;
                        break;
                }
            }

            var argv = sc.Arguments.Select(ExpandWord).ToList();
            InvokeCommand(sc.Name, argv, finalStdin, finalStdout, finalStderr);
        }
        finally
        {
            foreach (var d in toDispose) d.Dispose();
        }
    }

    private async ValueTask ExecuteSimpleAsync(
        SimpleCommandAst sc,
        CancellationToken cancellationToken,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        TextWriter finalStdout = stdout;
        TextWriter finalStderr = stderr;
        TextReader finalStdin = stdin;
        List<IDisposable> toDispose = new();

        try
        {
            foreach (var r in sc.Redirections)
            {
                switch (r)
                {
                    case StdoutRedirection sor:
                    {
                        var path = ExpandWord(sor.Target);
                        var writer = new VfsTextWriter(Context.Vfs, path, sor.Append);
                        toDispose.Add(writer);
                        finalStdout = writer;
                        break;
                    }
                    case StdinRedirection sir:
                    {
                        var path = ExpandWord(sir.Target);
                        var abs = Context.Vfs.Normalize(path);
                        var file = Context.Vfs.Resolve(abs) as VfsFile
                            ?? throw new CmdRuntimeException($"The system cannot find the file specified - {abs}");
                        finalStdin = new StringReader(file.ReadText());
                        break;
                    }
                    case StderrRedirection ser:
                    {
                        var path = ExpandWord(ser.Target);
                        var writer = new VfsTextWriter(Context.Vfs, path, append: false);
                        toDispose.Add(writer);
                        finalStderr = writer;
                        break;
                    }
                    case StderrMergeRedirection:
                        finalStderr = finalStdout;
                        break;
                }
            }

            var argv = sc.Arguments.Select(ExpandWord).ToList();
            await InvokeCommandAsync(sc.Name, argv, cancellationToken, finalStdin, finalStdout, finalStderr)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var d in toDispose) d.Dispose();
        }
    }

    private void ExecuteIf(IfStatementAst ifs, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        bool result = ifs.Condition switch
        {
            IfEqualsCondition eq => CompareEquals(ExpandWord(eq.Left), ExpandWord(eq.Right), ifs.CaseInsensitive),
            IfExistCondition ex => Context.Vfs.Exists(ExpandWord(ex.Path)),
            IfDefinedCondition df => Context.Env.Get(df.VarName) is not null,
            IfErrorLevelCondition el => LastExitCode >= el.Threshold,
            _ => throw new CmdRuntimeException("Unsupported IF condition.")
        };
        if (ifs.Negated) result = !result;
        if (result) ExecuteStatementWith(ifs.Body, stdin, stdout, stderr);
        else if (ifs.Else is not null) ExecuteStatementWith(ifs.Else, stdin, stdout, stderr);
    }

    private void ExecuteSetLocal(SetLocalStatementAst setLocal)
    {
        foreach (var opt in setLocal.Options)
        {
            if (opt.Equals("ENABLEDELAYEDEXPANSION", StringComparison.OrdinalIgnoreCase))
                DelayedExpansion = true;
        }
        _setLocalScopes.Push(Context.Env.PushScope());
    }

    private void ExecuteEndLocal()
    {
        if (_setLocalScopes.Count > 0)
            _setLocalScopes.Pop().Dispose();
        DelayedExpansion = false;
    }

    private static bool CompareEquals(string a, string b, bool caseInsensitive)
    {
        var x = StripSurroundingQuotes(a);
        var y = StripSurroundingQuotes(b);
        return string.Equals(x, y, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string StripSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        return s;
    }

    internal string ExpandWord(string word)
    {
        var all = string.Join(" ", Positional.Skip(1));
        return VarExpander.Expand(word, Context.Env, Positional, all, DelayedExpansion, Context.Vfs);
    }

    // ------------------------------------------------------------------
    // Command dispatch.
    // ------------------------------------------------------------------

    private void InvokeCommand(string name, List<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var builtin = Builtins.BuiltinRegistry.TryGet(name);
        if (builtin is not null)
        {
            try
            {
                LastExitCode = builtin(this, argv, stdin, stdout, stderr);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return;
            }
            catch (VfsException ex)
            {
                stderr.WriteLine(ex.Message);
                LastExitCode = 1;
                Context.Dispatcher.LastExitCode = 1;
                return;
            }
            catch (CmdRuntimeException ex)
            {
                stderr.WriteLine(ex.Message);
                LastExitCode = 1;
                Context.Dispatcher.LastExitCode = 1;
                return;
            }
        }

        // Dispatcher fallback: maybe it's another shell or a script path.
        var resolution = Context.Dispatcher.Resolve(name, Context, "cmd");
        switch (resolution.Kind)
        {
            case ResolutionKind.NamedShell when resolution.Kernel is not null:
            {
                LastExitCode = CrossShellLauncher.Launch(resolution.Kernel, argv, Context, stdin, stdout, stderr);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return;
            }
            case ResolutionKind.VirtualExecutable
                when resolution.VirtualExecutable is not null && resolution.VirtualExecutablePath is not null:
            {
                var childCtx = Context.With(args: argv, input: stdin, output: stdout, error: stderr);
                LastExitCode = Context.Dispatcher.ExecuteVirtualExecutable(
                    resolution.VirtualExecutable,
                    resolution.VirtualExecutablePath,
                    name,
                    argv,
                    childCtx);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return;
            }
            case ResolutionKind.Script when resolution.Kernel is not null && resolution.ScriptPath is not null:
            {
                var childCtx = Context.With(args: argv, input: stdin, output: stdout, error: stderr);
                LastExitCode = Context.Dispatcher.ExecuteScript(resolution.ScriptPath, resolution.Kernel, childCtx);
                return;
            }
        }

        stderr.WriteLine($"'{name}' is not recognized as an internal or external command,");
        stderr.WriteLine("operable program or batch file.");
        LastExitCode = 9009; // classic cmd "not found" code
        Context.Dispatcher.LastExitCode = LastExitCode;
    }

    private async ValueTask InvokeCommandAsync(
        string name,
        List<string> argv,
        CancellationToken cancellationToken,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        var builtin = Builtins.BuiltinRegistry.TryGet(name);
        if (builtin is not null)
        {
            try
            {
                LastExitCode = builtin(this, argv, stdin, stdout, stderr);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return;
            }
            catch (VfsException ex)
            {
                stderr.WriteLine(ex.Message);
                LastExitCode = 1;
                Context.Dispatcher.LastExitCode = 1;
                return;
            }
            catch (CmdRuntimeException ex)
            {
                stderr.WriteLine(ex.Message);
                LastExitCode = 1;
                Context.Dispatcher.LastExitCode = 1;
                return;
            }
        }

        var resolution = Context.Dispatcher.Resolve(name, Context, "cmd");
        switch (resolution.Kind)
        {
            case ResolutionKind.NamedShell when resolution.Kernel is not null:
            {
                LastExitCode = CrossShellLauncher.Launch(resolution.Kernel, argv, Context, stdin, stdout, stderr);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return;
            }
            case ResolutionKind.VirtualExecutable
                when resolution.VirtualExecutable is not null && resolution.VirtualExecutablePath is not null:
            {
                var childCtx = Context.With(args: argv, input: stdin, output: stdout, error: stderr);
                LastExitCode = await Context.Dispatcher.ExecuteVirtualExecutableAsync(
                    resolution.VirtualExecutable,
                    resolution.VirtualExecutablePath,
                    name,
                    argv,
                    childCtx,
                    cancellationToken).ConfigureAwait(false);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return;
            }
            case ResolutionKind.Script when resolution.Kernel is not null && resolution.ScriptPath is not null:
            {
                var childCtx = Context.With(args: argv, input: stdin, output: stdout, error: stderr);
                LastExitCode = await Context.Dispatcher.ExecuteScriptAsync(
                    resolution.ScriptPath,
                    resolution.Kernel,
                    childCtx,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        stderr.WriteLine($"'{name}' is not recognized as an internal or external command,");
        stderr.WriteLine("operable program or batch file.");
        LastExitCode = 9009;
        Context.Dispatcher.LastExitCode = LastExitCode;
    }

    internal int ExecuteForEcho(TextWriter stdout, string text)
    {
        stdout.WriteLine(text);
        return 0;
    }

    // ------------------------------------------------------------------
    // Utilities.
    // ------------------------------------------------------------------

    public static string FormatArgvForEcho(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < argv.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(argv[i]);
        }
        return sb.ToString();
    }

    public static int TryParseInt(string s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ------------------------------------------------------------------
    // FOR statements.
    // ------------------------------------------------------------------

    private void ExecuteForIn(ForInStatementAst forIn, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        foreach (var item in forIn.Items)
        {
            var expanded = ExpandWord(item);
            foreach (var value in ExpandGlob(expanded))
            {
                Context.Env.Set(forIn.Variable, value);
                ExecuteStatementWith(forIn.Body, stdin, stdout, stderr);
            }
        }
    }

    private void ExecuteForL(ForLStatementAst forL, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var start = TryParseInt(ExpandWord(forL.Start), 0);
        var step = TryParseInt(ExpandWord(forL.Step), 1);
        var end = TryParseInt(ExpandWord(forL.End), 0);
        if (step == 0)
        {
            stderr.WriteLine("FOR /L step cannot be zero.");
            LastExitCode = 1;
            return;
        }
        int iterationCap = 1_000_000;
        int iterations = 0;
        for (int i = start; (step > 0 && i <= end) || (step < 0 && i >= end); i += step)
        {
            if (++iterations > iterationCap)
                throw new CmdRuntimeException("FOR /L iteration cap exceeded.");
            Context.Env.Set(forL.Variable, i.ToString(CultureInfo.InvariantCulture));
            ExecuteStatementWith(forL.Body, stdin, stdout, stderr);
        }
    }

    private IEnumerable<string> ExpandGlob(string word)
    {
        if (!(word.Contains('*') || word.Contains('?'))) { yield return word; yield break; }
        // Glob against the VFS. Split into directory portion and leaf pattern; treat the
        // leaf as a shell-style filter. cmd's FOR %X IN (*.txt) globs in the current dir;
        // we honor that by splitting on the last `/` or `\`.
        var (parent, leaf) = SplitOnLastSep(word);
        string dir = parent.Length == 0 ? Context.Vfs.CurrentLocation : parent;
        if (!Context.Vfs.IsDirectory(dir)) { yield return word; yield break; }
        foreach (var node in Context.Vfs.List(dir, recursive: false, filter: leaf))
        {
            yield return node.Name;
        }
    }

    private static (string Parent, string Leaf) SplitOnLastSep(string word)
    {
        int sep = -1;
        for (int i = word.Length - 1; i >= 0; i--)
        {
            if (word[i] == '/' || word[i] == '\\') { sep = i; break; }
        }
        if (sep < 0) return ("", word);
        return (word.Substring(0, sep), word.Substring(sep + 1));
    }

    // ------------------------------------------------------------------
    // CALL statements.
    // ------------------------------------------------------------------

    private void ExecuteCallLabel(CallLabelStatementAst call, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (_currentScript is null)
            throw new CmdRuntimeException("CALL :label is valid only during script execution.");
        if (!_labels.TryGetValue(call.Label, out var target))
        {
            stderr.WriteLine($"The system cannot find the batch label specified - {call.Label}");
            LastExitCode = 1;
            return;
        }

        var savedPositional = Positional;
        var newPositional = new List<string> { savedPositional.Count > 0 ? savedPositional[0] : "" };
        foreach (var a in call.Arguments) newPositional.Add(ExpandWord(a));
        Positional = newPositional;

        try
        {
            ExecuteFromLine(_currentScript, target + 1, stdin, stdout, stderr);
        }
        catch (CmdExitException e) when (e.IsBranch)
        {
            LastExitCode = e.Code;
        }
        finally
        {
            Positional = savedPositional;
        }
    }

    private void ExecuteCallScript(CallScriptStatementAst call, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var expandedScript = ExpandWord(call.Script);
        var expandedArgs = call.Arguments.Select(ExpandWord).ToList();
        var abs = Context.Vfs.Normalize(expandedScript);
        var file = Context.Vfs.Resolve(abs) as VfsFile;
        if (file is null)
        {
            stderr.WriteLine($"The system cannot find the file specified - {abs}");
            LastExitCode = 1;
            return;
        }

        var args = new List<string> { abs };
        args.AddRange(expandedArgs);
        var childCtx = Context.With(args: args, input: stdin, output: stdout, error: stderr);
        var childInterp = new Interpreter(childCtx) { Positional = args };
        var script = Parser.CmdParser.ParseString(file.ReadText());
        var code = childInterp.Execute(script);
        LastExitCode = code;
        Context.Dispatcher.LastExitCode = code;
    }

    private void ExecuteFromLine(ScriptAst script, int startLine, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        int idx = startLine;
        while (idx < script.Lines.Count)
        {
            var line = script.Lines[idx];
            try
            {
                if (line is CommandLineAst cmd)
                {
                    bool previousEcho = EchoOn;
                    if (cmd.EchoSuppressed) EchoOn = false;
                    try { ExecuteChain(cmd.Chain); }
                    finally { if (cmd.EchoSuppressed) EchoOn = previousEcho; }
                }
                idx++;
            }
            catch (CmdGotoException g)
            {
                if (g.Label.Equals("EOF", StringComparison.OrdinalIgnoreCase)) return;
                if (!_labels.TryGetValue(g.Label, out var target))
                    throw new CmdRuntimeException($"The system cannot find the batch label specified - {g.Label}");
                idx = target + 1;
            }
        }
    }
}
