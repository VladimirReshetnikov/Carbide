using System.Collections;
using System.Text;
using CarbidePwsh.Cmdlets.Discovery;
using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;
using CarbidePwsh.Runtime;
using CarbideShellCore.Dispatch;

namespace CarbidePwsh.Cmdlets;

/// <summary>
/// Thread a <see cref="PipelineAst"/> across its stages. Expression stages are evaluated and
/// their value is wrapped as the initial enumerable; command stages dispatch through the
/// cmdlet registry.
/// </summary>
public static class Pipeline
{
    private sealed record CommandRedirectionPlan(
        bool SuppressSuccessOutput,
        bool SuppressErrorOutput,
        bool MergeErrorToOutput);

    public static object? Run(PipelineAst pipeline, CmdletContext ctx, CmdletRegistry registry)
    {
        IEnumerable<object?>? input = null;

        foreach (var stage in pipeline.Stages)
        {
            input = RunStage(stage, input, ctx, registry);
        }

        if (input == null) return null;
        var list = input.ToList();
        return list.Count switch
        {
            0 => null,
            1 => list[0],
            _ => list.ToArray(),
        };
    }

    private static IEnumerable<object?> RunStage(AstNode stage, IEnumerable<object?>? input, CmdletContext ctx, CmdletRegistry registry)
    {
        switch (stage)
        {
            case CommandAst cmd:
                return RunCommand(cmd, input, ctx, registry);
            case ExpressionAst expr:
                return ExpressionToEnumerable(ctx.Interpreter.Eval(expr));
            default:
                throw new PwshRuntimeException($"Unsupported pipeline stage: {stage.GetType().Name}", stage.Location);
        }
    }

    public static IEnumerable<object?> ExpressionToEnumerable(object? value)
    {
        if (value == null)
        {
            yield break;
        }
        // Strings, dictionaries, and non-collection objects flow through the pipeline as a
        // single item. Arrays and plain IEnumerables (e.g. ranges) flatten — matches
        // PowerShell's "arrays unroll, dictionaries don't" rule.
        if (value is string s) { yield return s; yield break; }
        if (value is IDictionary) { yield return value; yield break; }
        if (value is Array arr)
        {
            foreach (var item in arr) yield return item;
            yield break;
        }
        if (value is IEnumerable en)
        {
            foreach (var item in en) yield return item;
            yield break;
        }
        yield return value;
    }

    private static IEnumerable<object?> RunCommand(CommandAst cmd, IEnumerable<object?>? input, CmdletContext ctx, CmdletRegistry registry)
    {
        var positional = new List<object?>();
        var nativeArguments = new List<object?>();
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var redirections = new List<CommandRedirectionAst>();

        for (int i = 0; i < cmd.Elements.Count; i++)
        {
            var el = cmd.Elements[i];
            if (el is CommandParameterAst param)
            {
                nativeArguments.Add("-" + param.Name);
                if (i + 1 < cmd.Elements.Count && cmd.Elements[i + 1] is CommandArgumentAst valueArg)
                {
                    var value = ctx.Interpreter.Eval(valueArg.Expression);
                    named[param.Name] = value;
                    nativeArguments.Add(value);
                    i++;
                }
                else
                {
                    named[param.Name] = true;
                }
            }
            else if (el is CommandArgumentAst arg)
            {
                var value = ctx.Interpreter.Eval(arg.Expression);
                positional.Add(value);
                nativeArguments.Add(value);
            }
            else if (el is CommandSplatAst splat)
            {
                var value = ctx.Interpreter.Eval(splat.Expression);
                ApplySplat(value, positional, named);
                ApplyNativeSplat(value, nativeArguments);
            }
            else if (el is CommandRedirectionAst redirection)
            {
                redirections.Add(redirection);
            }
        }

        var plan = BuildRedirectionPlan(redirections);
        var effectiveCtx = CreateEffectiveContext(ctx, plan);
        var resolvedName = cmd.Name is "." or "&" ? cmd.Name : registry.ResolveAliasChain(cmd.Name);

        // Dispatch priority: cmdlet → user function → script/app registry → bare path
        // dispatch. This matches the proposal's §8.1 order.
        if (registry.TryResolve(resolvedName, out var cmdlet) && cmdlet != null)
        {
            try
            {
                var result = cmdlet.Invoke(input, new ParameterBinding(positional, named), effectiveCtx).ToList();
                ctx.Scope.Set("global", "?", true);
                return plan.SuppressSuccessOutput ? Array.Empty<object?>() : result;
            }
            catch
            {
                ctx.Scope.Set("global", "?", false);
                throw;
            }
        }

        if (ctx.Interpreter.Functions != null
            && ctx.Interpreter.Functions.TryGet(resolvedName, out var func) && func != null)
        {
            try
            {
                IEnumerable<object?> result;
                if (func.IsPipelineParticipant)
                {
                    result = RunWithInterpreterWriters(ctx.Interpreter, effectiveCtx, () =>
                        func.InvokeAsPipelineStage(input, positional, named, ctx.Interpreter).ToList());
                }
                else
                {
                    var returnValue = RunWithInterpreterWriters(ctx.Interpreter, effectiveCtx, () =>
                        func.Invoke(positional, named, ctx.Interpreter));
                    result = ExpressionToEnumerable(returnValue).ToList();
                }
                ctx.Scope.Set("global", "?", true);
                return plan.SuppressSuccessOutput ? Array.Empty<object?>() : result;
            }
            catch
            {
                ctx.Scope.Set("global", "?", false);
                throw;
            }
        }

        // Dot-source: invoke the first positional as a script in the caller's scope.
        if (resolvedName == "." && positional.Count > 0)
        {
            var scriptPath = Runtime.Coercion.FormatAsString(positional[0]);
            var scriptArgs = positional.Skip(1).ToArray();
            if (ctx.Interpreter.RunScriptFile == null)
                throw new PwshRuntimeException("Script loader is not wired up.", cmd.Location);
            var result = RunWithInterpreterWriters(ctx.Interpreter, effectiveCtx, () =>
                ctx.Interpreter.RunScriptFile(scriptPath, /*dotSource*/ true, scriptArgs));
            return plan.SuppressSuccessOutput ? Array.Empty<object?>() : ExpressionToEnumerable(result).ToList();
        }

        // Call operator `&`: invoke a script block or resolve a path-valued string.
        if (resolvedName == "&" && positional.Count > 0)
        {
            var callTarget = positional[0];
            var callArgs = positional.Skip(1).ToArray();
            if (callTarget is Runtime.ScriptBlock sb)
            {
                var savedArgs = ctx.Scope.Get(null, "args");
                ctx.Scope.Set(null, "args", callArgs);
                try
                {
                    var result = RunWithInterpreterWriters(ctx.Interpreter, effectiveCtx, () => sb.Invoke());
                    return plan.SuppressSuccessOutput ? Array.Empty<object?>() : ExpressionToEnumerable(result).ToList();
                }
                finally
                {
                    ctx.Scope.Set(null, "args", savedArgs);
                }
            }
            if (callTarget is string path)
            {
                return DispatchPath(path, callArgs, effectiveCtx, plan);
            }
            throw new PwshRuntimeException(
                $"Call operator '&' requires a script block or path string; got [{callTarget?.GetType().Name ?? "null"}].",
                cmd.Location);
        }

        if (TryDispatchExternalCommand(resolvedName, nativeArguments, input, effectiveCtx, plan, out var externalResult))
            return externalResult;

        if (BuiltinCommandCatalog.TryGetCmdlet(resolvedName, out var builtin))
        {
            throw new PwshRuntimeException(
                $"The builtin cmdlet '{builtin.Name}' is recognized but not implemented in carbide-pwsh.",
                cmd.Location);
        }

        throw new PwshRuntimeException($"The term '{cmd.Name}' is not recognized as a cmdlet, function, or script.", cmd.Location);
    }

    private static IEnumerable<object?> DispatchPath(string path, IReadOnlyList<object?> args, CmdletContext ctx, CommandRedirectionPlan plan)
    {
        if (TryDispatchExternalCommand(path, args, null, ctx, plan, out var result))
            return result;

        if (ctx.Interpreter.RunScriptFile == null)
            throw new PwshRuntimeException("Script loader is not wired up.", Errors.SourceLocation.None);
        var scriptResult = RunWithInterpreterWriters(ctx.Interpreter, ctx, () =>
            ctx.Interpreter.RunScriptFile(path, /*dotSource*/ false, args));
        return plan.SuppressSuccessOutput ? Array.Empty<object?>() : ExpressionToEnumerable(scriptResult).ToList();
    }

    private static bool TryDispatchExternalCommand(
        string commandName,
        IReadOnlyList<object?> args,
        IEnumerable<object?>? input,
        CmdletContext ctx,
        CommandRedirectionPlan plan,
        out IEnumerable<object?> result)
    {
        result = Array.Empty<object?>();
        if (ctx.Interpreter.Dispatcher == null || ctx.Interpreter.Env == null || ctx.Interpreter.Apps == null)
            return false;

        var stdin = input == null
            ? TextReader.Null
            : new StringReader(StringifyPipelineInput(input));
        var stdout = new StringWriter();
        var stderr = plan.MergeErrorToOutput ? stdout : new StringWriter();
        var stringArgs = args.Select(Runtime.Coercion.FormatAsString).ToArray();
        var shellCtx = new ShellExecutionContext
        {
            Args = stringArgs,
            Input = stdin,
            Output = stdout,
            Error = stderr,
            Vfs = ctx.Vfs,
            Env = ctx.Interpreter.Env,
            Apps = ctx.Interpreter.Apps,
            Dispatcher = ctx.Interpreter.Dispatcher,
        };

        var resolution = ctx.Interpreter.Dispatcher.Resolve(commandName, shellCtx, "pwsh");
        int code;
        switch (resolution.Kind)
        {
            case ResolutionKind.VirtualExecutable
                when resolution.VirtualExecutable is not null && resolution.VirtualExecutablePath is not null:
                code = ctx.Interpreter.Dispatcher.ExecuteVirtualExecutable(
                    resolution.VirtualExecutable,
                    resolution.VirtualExecutablePath,
                    commandName,
                    stringArgs,
                    shellCtx);
                break;
            case ResolutionKind.Script when resolution.Kernel is not null && resolution.ScriptPath is not null:
                code = ctx.Interpreter.Dispatcher.ExecuteScript(resolution.ScriptPath, resolution.Kernel, shellCtx);
                break;
            case ResolutionKind.App when resolution.AppPath is not null:
                if (ctx.Interpreter.RunApp == null)
                    throw new PwshRuntimeException("App invoker is not wired up.", Errors.SourceLocation.None);
                code = ctx.Interpreter.RunApp(resolution.AppPath, args);
                break;
            default:
                return false;
        }

        var stderrText = stderr.ToString();
        if (!plan.SuppressErrorOutput && !plan.MergeErrorToOutput && stderrText.Length > 0)
            ctx.Error.Write(stderrText);

        ctx.Interpreter.Scope.Set("global", "LASTEXITCODE", code);
        ctx.Scope.Set("global", "?", code == 0);
        result = plan.SuppressSuccessOutput
            ? Array.Empty<object?>()
            : ReadOutputLines(stdout.ToString());
        return true;
    }

    private static CmdletContext CreateEffectiveContext(CmdletContext ctx, CommandRedirectionPlan plan)
    {
        var output = plan.SuppressSuccessOutput ? TextWriter.Null : ctx.Output;
        var error = plan.MergeErrorToOutput ? output : plan.SuppressErrorOutput ? TextWriter.Null : ctx.Error;
        if (ReferenceEquals(output, ctx.Output) && ReferenceEquals(error, ctx.Error))
            return ctx;
        return new CmdletContext(ctx.Interpreter, ctx.Vfs, output, error);
    }

    private static CommandRedirectionPlan BuildRedirectionPlan(IEnumerable<CommandRedirectionAst> redirections)
    {
        bool suppressSuccess = false;
        bool suppressError = false;
        bool mergeErrorToOutput = false;

        foreach (var redirection in redirections)
        {
            int from = redirection.FromStream ?? 1;
            bool allStreams = from == -1;
            if (redirection.MergeToStream == 1)
            {
                if (allStreams || from == 2)
                    mergeErrorToOutput = true;
                continue;
            }
            if (redirection.Target is null || !IsNullTarget(redirection.Target))
                continue;

            if (allStreams || from == 1)
                suppressSuccess = true;
            if (allStreams || from == 2)
                suppressError = true;
        }

        return new CommandRedirectionPlan(suppressSuccess, suppressError, mergeErrorToOutput);
    }

    private static bool IsNullTarget(ExpressionAst target)
        => target is NullLiteralAst
        || target is VariableAst { Scope: null, Name: var name } && string.Equals(name, "null", StringComparison.OrdinalIgnoreCase);

    private static void ApplySplat(object? value, List<object?> positional, Dictionary<string, object?> named)
    {
        if (value is null) return;

        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var key = Runtime.Coercion.FormatAsString(entry.Key).TrimStart('-');
                named[key] = entry.Value;
            }
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable.Cast<object?>())
                positional.Add(item);
            return;
        }

        positional.Add(value);
    }

    private static void ApplyNativeSplat(object? value, List<object?> arguments)
    {
        if (value is null) return;

        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                arguments.Add("-" + Runtime.Coercion.FormatAsString(entry.Key).TrimStart('-'));
                if (entry.Value is not null && entry.Value is not true)
                    arguments.Add(entry.Value);
            }
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable.Cast<object?>())
                arguments.Add(item);
            return;
        }

        arguments.Add(value);
    }

    private static T RunWithInterpreterWriters<T>(Interpreter interpreter, CmdletContext ctx, Func<T> action)
    {
        var savedOutput = interpreter.PipelineOutput;
        var savedError = interpreter.PipelineError;
        interpreter.PipelineOutput = ctx.Output;
        interpreter.PipelineError = ctx.Error;
        try
        {
            return action();
        }
        finally
        {
            interpreter.PipelineOutput = savedOutput;
            interpreter.PipelineError = savedError;
        }
    }

    private static string StringifyPipelineInput(IEnumerable<object?> input)
    {
        var sb = new StringBuilder();
        foreach (var item in input)
        {
            sb.Append(Runtime.Coercion.FormatAsString(item));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static IEnumerable<object?> ReadOutputLines(string output)
    {
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }
}
