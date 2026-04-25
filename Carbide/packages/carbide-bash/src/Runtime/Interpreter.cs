using System.Text;
using CarbideBash.Errors;
using CarbideBash.Lexer;
using CarbideBash.Parser;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;

namespace CarbideBash.Runtime;

/// <summary>
/// Tree-walking bash interpreter. Owns an execution context, a positional-parameter list,
/// a table of user-defined functions, and a last-exit-code slot. Control flow uses
/// exception-based unwinding for <c>break</c> / <c>continue</c> / <c>return</c> / <c>exit</c>.
/// </summary>
public sealed class Interpreter
{
    public ShellExecutionContext Context { get; }
    public List<string> Positional { get; set; } = new();
    public int LastExitCode { get; set; }
    public Dictionary<string, FunctionDefAst> Functions { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    public Interpreter(ShellExecutionContext context) { Context = context; }

    public int Execute(ScriptAst script)
    {
        try
        {
            foreach (var s in script.Statements) ExecuteStatement(s, Context.Input, Context.Output, Context.Error);
        }
        catch (BashExitException e)
        {
            LastExitCode = e.Code;
        }
        return LastExitCode;
    }

    public async ValueTask<int> ExecuteAsync(
        ScriptAst script,
        CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var s in script.Statements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteStatementAsync(s, Context.Input, Context.Output, Context.Error, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (BashExitException e)
        {
            LastExitCode = e.Code;
        }
        return LastExitCode;
    }

    internal int ExecuteStatement(StatementAst stmt, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        switch (stmt)
        {
            case SimpleCommandAst sc: return ExecuteSimple(sc, stdin, stdout, stderr);
            case PipelineAst pipe: return ExecutePipeline(pipe, stdin, stdout, stderr);
            case ListAst list: return ExecuteList(list, stdin, stdout, stderr);
            case IfStatementAst ifs: return ExecuteIf(ifs, stdin, stdout, stderr);
            case WhileStatementAst w: return ExecuteWhile(w, stdin, stdout, stderr);
            case ForStatementAst f: return ExecuteFor(f, stdin, stdout, stderr);
            case CaseStatementAst cs: return ExecuteCase(cs, stdin, stdout, stderr);
            case FunctionDefAst fd:
                Functions[fd.Name] = fd;
                LastExitCode = 0;
                return 0;
            case BlockAst block: return ExecuteBlock(block, stdin, stdout, stderr);
            default: throw new BashRuntimeException($"Unsupported statement type: {stmt.GetType().Name}");
        }
    }

    internal async ValueTask<int> ExecuteStatementAsync(
        StatementAst stmt,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        return stmt switch
        {
            SimpleCommandAst sc => await ExecuteSimpleAsync(sc, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false),
            PipelineAst pipe => await ExecutePipelineAsync(pipe, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false),
            ListAst list => await ExecuteListAsync(list, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false),
            _ => ExecuteStatement(stmt, stdin, stdout, stderr),
        };
    }

    private int ExecuteList(ListAst list, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        int last = 0;
        foreach (var item in list.Items)
        {
            switch (item.Op)
            {
                case ListOperator.None:
                case ListOperator.Sequence:
                    last = ExecuteStatement(item.Pipeline, stdin, stdout, stderr);
                    break;
                case ListOperator.And:
                    if (last == 0) last = ExecuteStatement(item.Pipeline, stdin, stdout, stderr);
                    break;
                case ListOperator.Or:
                    if (last != 0) last = ExecuteStatement(item.Pipeline, stdin, stdout, stderr);
                    break;
            }
        }
        LastExitCode = last;
        return last;
    }

    private async ValueTask<int> ExecuteListAsync(
        ListAst list,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        int last = 0;
        foreach (var item in list.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item.Op)
            {
                case ListOperator.None:
                case ListOperator.Sequence:
                    last = await ExecuteStatementAsync(item.Pipeline, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false);
                    break;
                case ListOperator.And:
                    if (last == 0) last = await ExecuteStatementAsync(item.Pipeline, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false);
                    break;
                case ListOperator.Or:
                    if (last != 0) last = await ExecuteStatementAsync(item.Pipeline, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        LastExitCode = last;
        return last;
    }

    private int ExecutePipeline(PipelineAst pipe, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (pipe.Stages.Count == 1) return ExecuteStatement(pipe.Stages[0], stdin, stdout, stderr);
        TextReader cur = stdin;
        int last = 0;
        for (int i = 0; i < pipe.Stages.Count; i++)
        {
            bool isLast = i == pipe.Stages.Count - 1;
            if (isLast)
            {
                last = ExecuteStatement(pipe.Stages[i], cur, stdout, stderr);
                break;
            }
            var capture = new StringWriter();
            ExecuteStatement(pipe.Stages[i], cur, capture, stderr);
            cur = new StringReader(capture.ToString());
        }
        LastExitCode = last;
        return last;
    }

    private async ValueTask<int> ExecutePipelineAsync(
        PipelineAst pipe,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (pipe.Stages.Count == 1)
        {
            return await ExecuteStatementAsync(pipe.Stages[0], stdin, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);
        }
        TextReader cur = stdin;
        int last = 0;
        for (int i = 0; i < pipe.Stages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isLast = i == pipe.Stages.Count - 1;
            if (isLast)
            {
                last = await ExecuteStatementAsync(pipe.Stages[i], cur, stdout, stderr, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            var capture = new StringWriter();
            await ExecuteStatementAsync(pipe.Stages[i], cur, capture, stderr, cancellationToken)
                .ConfigureAwait(false);
            cur = new StringReader(capture.ToString());
        }
        LastExitCode = last;
        return last;
    }

    private int ExecuteBlock(BlockAst block, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (block.Subshell)
        {
            using var scope = Context.Env.PushScope();
            int code = 0;
            foreach (var s in block.Statements) code = ExecuteStatement(s, stdin, stdout, stderr);
            return code;
        }
        int last = 0;
        foreach (var s in block.Statements) last = ExecuteStatement(s, stdin, stdout, stderr);
        return last;
    }

    private int ExecuteIf(IfStatementAst ifs, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var condCode = ExecuteStatement(ifs.Condition, stdin, stdout, stderr);
        if (condCode == 0)
            return ExecuteStatement(ifs.Then, stdin, stdout, stderr);
        foreach (var e in ifs.Elifs)
        {
            if (ExecuteStatement(e.Condition, stdin, stdout, stderr) == 0)
                return ExecuteStatement(e.Then, stdin, stdout, stderr);
        }
        if (ifs.Else is not null) return ExecuteStatement(ifs.Else, stdin, stdout, stderr);
        return 0;
    }

    private int ExecuteWhile(WhileStatementAst w, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        int last = 0;
        while (true)
        {
            var cond = ExecuteStatement(w.Condition, stdin, stdout, stderr);
            bool enter = w.Until ? cond != 0 : cond == 0;
            if (!enter) break;
            try { last = ExecuteStatement(w.Body, stdin, stdout, stderr); }
            catch (BashBreakException b) { if (b.Levels > 1) throw new BashBreakException(b.Levels - 1); break; }
            catch (BashContinueException cont) { if (cont.Levels > 1) throw new BashContinueException(cont.Levels - 1); continue; }
        }
        return last;
    }

    private int ExecuteFor(ForStatementAst f, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        int last = 0;
        IEnumerable<string> iter;
        if (f.Words.Count == 0)
        {
            iter = Positional.Skip(1);
        }
        else
        {
            var expander = MakeExpansion(stdin, stdout, stderr);
            var expanded = new List<string>();
            foreach (var w in f.Words)
            {
                foreach (var braceExpanded in BraceExpansion.Expand(w))
                {
                    foreach (var parameterExpanded in expander.Expand(braceExpanded))
                    {
                        expanded.AddRange(Globbing.Expand(parameterExpanded, Context.Vfs, Context.Vfs.CurrentLocation));
                    }
                }
            }
            iter = expanded;
        }
        foreach (var val in iter)
        {
            Context.Env.Set(f.Variable, val);
            try { last = ExecuteStatement(f.Body, stdin, stdout, stderr); }
            catch (BashBreakException b) { if (b.Levels > 1) throw new BashBreakException(b.Levels - 1); break; }
            catch (BashContinueException cont) { if (cont.Levels > 1) throw new BashContinueException(cont.Levels - 1); continue; }
        }
        return last;
    }

    private int ExecuteCase(CaseStatementAst cs, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var expander = MakeExpansion(stdin, stdout, stderr);
        var word = string.Join("", expander.Expand(cs.Word));
        foreach (var clause in cs.Clauses)
        {
            foreach (var pat in clause.Patterns)
            {
                var expandedPat = string.Join("", expander.Expand(pat));
                if (PatternMatch(expandedPat, word))
                    return ExecuteStatement(clause.Body, stdin, stdout, stderr);
            }
        }
        return 0;
    }

    private static bool PatternMatch(string pattern, string value)
    {
        var rx = "^" + PatternToRegex(pattern) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, rx);
    }

    private static string PatternToRegex(string pat)
    {
        var sb = new StringBuilder();
        foreach (var ch in pat)
        {
            if (ch == '*') sb.Append(".*");
            else if (ch == '?') sb.Append('.');
            else sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
        }
        return sb.ToString();
    }

    private int ExecuteSimple(SimpleCommandAst sc, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var expander = MakeExpansion(stdin, stdout, stderr);

        // Apply redirections.
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
                        var path = string.Join("", expander.Expand(sor.Target));
                        var writer = new VfsTextWriter(Context.Vfs, path, sor.Append);
                        toDispose.Add(writer);
                        finalStdout = writer;
                        break;
                    }
                    case StdinRedirection sir:
                    {
                        var path = string.Join("", expander.Expand(sir.Target));
                        var abs = Context.Vfs.Normalize(path);
                        var file = Context.Vfs.Resolve(abs) as VfsFile
                            ?? throw new BashRuntimeException($"{abs}: No such file or directory");
                        finalStdin = new StringReader(file.ReadText());
                        break;
                    }
                    case StderrRedirection ser:
                    {
                        var path = string.Join("", expander.Expand(ser.Target));
                        var writer = new VfsTextWriter(Context.Vfs, path, append: false);
                        toDispose.Add(writer);
                        finalStderr = writer;
                        break;
                    }
                    case HeredocRedirection hd:
                    {
                        var body = hd.Expandable
                            ? expander.ExpandDouble(hd.Body)
                            : hd.Body;
                        if (!body.EndsWith('\n')) body += "\n";
                        finalStdin = new StringReader(body);
                        break;
                    }
                    case HereStringRedirection hs:
                    {
                        var content = expander.ExpandDouble(hs.Content);
                        finalStdin = new StringReader(content + "\n");
                        break;
                    }
                }
            }

            // No command name? This is pure assignment(s).
            if (sc.Words.Count == 0)
            {
                foreach (var a in sc.Assignments)
                {
                    var val = string.Join("", expander.Expand(a.Value));
                    Context.Env.Set(a.Name, val);
                }
                LastExitCode = 0;
                return 0;
            }

            // Assignments preceding a command are scoped to the command invocation.
            var priorValues = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var a in sc.Assignments)
            {
                priorValues[a.Name] = Context.Env.Get(a.Name);
                Context.Env.Set(a.Name, string.Join("", expander.Expand(a.Value)));
            }

            try
            {
                var argv = new List<string>();
                foreach (var w in sc.Words)
                {
                    // Brace expansion runs before parameter expansion, per bash order.
                    foreach (var braceExpanded in BraceExpansion.Expand(w))
                    {
                        foreach (var parameterExpanded in expander.Expand(braceExpanded))
                        {
                            // Glob expansion runs last, against the VFS.
                            argv.AddRange(Globbing.Expand(parameterExpanded, Context.Vfs, Context.Vfs.CurrentLocation));
                        }
                    }
                }
                if (argv.Count == 0) { LastExitCode = 0; return 0; }

                LastExitCode = InvokeCommand(argv, finalStdin, finalStdout, finalStderr);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return LastExitCode;
            }
            finally
            {
                if (sc.Assignments.Count > 0 && sc.Words.Count > 0)
                {
                    foreach (var kv in priorValues) Context.Env.Set(kv.Key, kv.Value);
                }
            }
        }
        finally
        {
            foreach (var d in toDispose) d.Dispose();
        }
    }

    private async ValueTask<int> ExecuteSimpleAsync(
        SimpleCommandAst sc,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var expander = MakeExpansion(stdin, stdout, stderr);

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
                        var path = string.Join("", expander.Expand(sor.Target));
                        var writer = new VfsTextWriter(Context.Vfs, path, sor.Append);
                        toDispose.Add(writer);
                        finalStdout = writer;
                        break;
                    }
                    case StdinRedirection sir:
                    {
                        var path = string.Join("", expander.Expand(sir.Target));
                        var abs = Context.Vfs.Normalize(path);
                        var file = Context.Vfs.Resolve(abs) as VfsFile
                            ?? throw new BashRuntimeException($"{abs}: No such file or directory");
                        finalStdin = new StringReader(file.ReadText());
                        break;
                    }
                    case StderrRedirection ser:
                    {
                        var path = string.Join("", expander.Expand(ser.Target));
                        var writer = new VfsTextWriter(Context.Vfs, path, append: false);
                        toDispose.Add(writer);
                        finalStderr = writer;
                        break;
                    }
                    case HeredocRedirection hd:
                    {
                        var body = hd.Expandable
                            ? expander.ExpandDouble(hd.Body)
                            : hd.Body;
                        if (!body.EndsWith('\n')) body += "\n";
                        finalStdin = new StringReader(body);
                        break;
                    }
                    case HereStringRedirection hs:
                    {
                        var content = expander.ExpandDouble(hs.Content);
                        finalStdin = new StringReader(content + "\n");
                        break;
                    }
                }
            }

            if (sc.Words.Count == 0)
            {
                foreach (var a in sc.Assignments)
                {
                    var val = string.Join("", expander.Expand(a.Value));
                    Context.Env.Set(a.Name, val);
                }
                LastExitCode = 0;
                return 0;
            }

            var priorValues = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var a in sc.Assignments)
            {
                priorValues[a.Name] = Context.Env.Get(a.Name);
                Context.Env.Set(a.Name, string.Join("", expander.Expand(a.Value)));
            }

            try
            {
                var argv = new List<string>();
                foreach (var w in sc.Words)
                {
                    foreach (var braceExpanded in BraceExpansion.Expand(w))
                    {
                        foreach (var parameterExpanded in expander.Expand(braceExpanded))
                        {
                            argv.AddRange(Globbing.Expand(parameterExpanded, Context.Vfs, Context.Vfs.CurrentLocation));
                        }
                    }
                }
                if (argv.Count == 0) { LastExitCode = 0; return 0; }

                LastExitCode = await InvokeCommandAsync(
                    argv,
                    finalStdin,
                    finalStdout,
                    finalStderr,
                    cancellationToken).ConfigureAwait(false);
                Context.Dispatcher.LastExitCode = LastExitCode;
                return LastExitCode;
            }
            finally
            {
                if (sc.Assignments.Count > 0 && sc.Words.Count > 0)
                {
                    foreach (var kv in priorValues) Context.Env.Set(kv.Key, kv.Value);
                }
            }
        }
        finally
        {
            foreach (var d in toDispose) d.Dispose();
        }
    }

    private Expansion MakeExpansion(TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        return new Expansion(
            Context.Env,
            Positional,
            source => CaptureCommandSub(source, stdin, stderr),
            _ => LastExitCode);
    }

    private string CaptureCommandSub(string source, TextReader stdin, TextWriter stderr)
    {
        var capture = new StringWriter();
        try
        {
            var script = BashParser.ParseString(source);
            var childInterp = new Interpreter(Context) { Positional = Positional };
            foreach (var kv in Functions) childInterp.Functions[kv.Key] = kv.Value;
            childInterp.LastExitCode = LastExitCode;
            foreach (var s in script.Statements)
                childInterp.ExecuteStatement(s, stdin, capture, stderr);
            LastExitCode = childInterp.LastExitCode;
        }
        catch (BashException)
        {
            throw;
        }
        return capture.ToString();
    }

    private int InvokeCommand(List<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var name = argv[0];
        var rest = argv.Skip(1).ToList();

        // Alias expansion (very simple).
        if (Aliases.TryGetValue(name, out var aliasTarget))
        {
            var expanded = aliasTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            expanded.AddRange(rest);
            argv = expanded;
            name = argv[0];
            rest = argv.Skip(1).ToList();
        }

        // User-defined function.
        if (Functions.TryGetValue(name, out var func))
        {
            var savedPositional = Positional;
            Positional = new List<string> { name };
            Positional.AddRange(rest);
            try
            {
                try { return ExecuteStatement(func.Body, stdin, stdout, stderr); }
                catch (BashReturnException ret) { return ret.Code; }
            }
            finally
            {
                Positional = savedPositional;
            }
        }

        // Built-in?
        var builtin = Builtins.BuiltinRegistry.TryGet(name);
        if (builtin is not null)
        {
            try
            {
                return builtin(this, rest, stdin, stdout, stderr);
            }
            catch (VfsException ex)
            {
                stderr.WriteLine($"{name}: {ex.Message}");
                return 1;
            }
            catch (BashRuntimeException ex)
            {
                stderr.WriteLine($"{name}: {ex.Message}");
                return 1;
            }
        }

        // Dispatcher.
        var resolution = Context.Dispatcher.Resolve(name, Context, "bash");
        if (resolution.Kind == ResolutionKind.NamedShell && resolution.Kernel is not null)
            return CrossShellLauncher.Launch(resolution.Kernel, rest, Context, stdin, stdout, stderr);
        if (resolution.Kind == ResolutionKind.VirtualExecutable
            && resolution.VirtualExecutable is not null
            && resolution.VirtualExecutablePath is not null)
        {
            var child = Context.With(args: rest, input: stdin, output: stdout, error: stderr);
            return Context.Dispatcher.ExecuteVirtualExecutable(
                resolution.VirtualExecutable,
                resolution.VirtualExecutablePath,
                name,
                rest,
                child);
        }
        if (resolution.Kind == ResolutionKind.Script && resolution.Kernel is not null && resolution.ScriptPath is not null)
        {
            var child = Context.With(args: rest, input: stdin, output: stdout, error: stderr);
            return Context.Dispatcher.ExecuteScript(resolution.ScriptPath, resolution.Kernel, child);
        }

        stderr.WriteLine($"bash: {name}: command not found");
        return 127;
    }

    private async ValueTask<int> InvokeCommandAsync(
        List<string> argv,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var name = argv[0];
        var rest = argv.Skip(1).ToList();

        if (Aliases.TryGetValue(name, out var aliasTarget))
        {
            var expanded = aliasTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            expanded.AddRange(rest);
            argv = expanded;
            name = argv[0];
            rest = argv.Skip(1).ToList();
        }

        if (Functions.TryGetValue(name, out var func))
        {
            var savedPositional = Positional;
            Positional = new List<string> { name };
            Positional.AddRange(rest);
            try
            {
                try { return ExecuteStatement(func.Body, stdin, stdout, stderr); }
                catch (BashReturnException ret) { return ret.Code; }
            }
            finally
            {
                Positional = savedPositional;
            }
        }

        var builtin = Builtins.BuiltinRegistry.TryGet(name);
        if (builtin is not null)
        {
            try
            {
                return builtin(this, rest, stdin, stdout, stderr);
            }
            catch (VfsException ex)
            {
                stderr.WriteLine($"{name}: {ex.Message}");
                return 1;
            }
            catch (BashRuntimeException ex)
            {
                stderr.WriteLine($"{name}: {ex.Message}");
                return 1;
            }
        }

        var resolution = Context.Dispatcher.Resolve(name, Context, "bash");
        if (resolution.Kind == ResolutionKind.NamedShell && resolution.Kernel is not null)
            return CrossShellLauncher.Launch(resolution.Kernel, rest, Context, stdin, stdout, stderr);
        if (resolution.Kind == ResolutionKind.VirtualExecutable
            && resolution.VirtualExecutable is not null
            && resolution.VirtualExecutablePath is not null)
        {
            var child = Context.With(args: rest, input: stdin, output: stdout, error: stderr);
            return await Context.Dispatcher.ExecuteVirtualExecutableAsync(
                resolution.VirtualExecutable,
                resolution.VirtualExecutablePath,
                name,
                rest,
                child,
                cancellationToken).ConfigureAwait(false);
        }
        if (resolution.Kind == ResolutionKind.Script && resolution.Kernel is not null && resolution.ScriptPath is not null)
        {
            var child = Context.With(args: rest, input: stdin, output: stdout, error: stderr);
            return await Context.Dispatcher.ExecuteScriptAsync(
                resolution.ScriptPath,
                resolution.Kernel,
                child,
                cancellationToken).ConfigureAwait(false);
        }

        stderr.WriteLine($"bash: {name}: command not found");
        return 127;
    }
}
