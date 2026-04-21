using System.Collections;
using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets;

/// <summary>
/// Thread a <see cref="PipelineAst"/> across its stages. Expression stages are evaluated and
/// their value is wrapped as the initial enumerable; command stages dispatch through the
/// cmdlet registry.
/// </summary>
public static class Pipeline
{
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
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < cmd.Elements.Count; i++)
        {
            var el = cmd.Elements[i];
            if (el is CommandParameterAst param)
            {
                if (i + 1 < cmd.Elements.Count && cmd.Elements[i + 1] is CommandArgumentAst valueArg)
                {
                    named[param.Name] = ctx.Interpreter.Eval(valueArg.Expression);
                    i++;
                }
                else
                {
                    named[param.Name] = true;
                }
            }
            else if (el is CommandArgumentAst arg)
            {
                positional.Add(ctx.Interpreter.Eval(arg.Expression));
            }
        }

        // Dispatch priority: cmdlet → user function → script/app registry → bare path
        // dispatch. This matches the proposal's §8.1 order.
        if (registry.TryResolve(cmd.Name, out var cmdlet) && cmdlet != null)
        {
            try
            {
                var result = cmdlet.Invoke(input, new ParameterBinding(positional, named), ctx).ToList();
                ctx.Scope.Set("global", "?", true);
                return result;
            }
            catch
            {
                ctx.Scope.Set("global", "?", false);
                throw;
            }
        }

        if (ctx.Interpreter.Functions != null
            && ctx.Interpreter.Functions.TryGet(cmd.Name, out var func) && func != null)
        {
            try
            {
                IEnumerable<object?> result;
                if (func.IsPipelineParticipant)
                {
                    result = func.InvokeAsPipelineStage(input, positional, named, ctx.Interpreter).ToList();
                }
                else
                {
                    var returnValue = func.Invoke(positional, named, ctx.Interpreter);
                    result = ExpressionToEnumerable(returnValue).ToList();
                }
                ctx.Scope.Set("global", "?", true);
                return result;
            }
            catch
            {
                ctx.Scope.Set("global", "?", false);
                throw;
            }
        }

        // Dot-source: invoke the first positional as a script in the caller's scope.
        if (cmd.Name == "." && positional.Count > 0)
        {
            var scriptPath = Runtime.Coercion.FormatAsString(positional[0]);
            var scriptArgs = positional.Skip(1).ToArray();
            if (ctx.Interpreter.RunScriptFile == null)
                throw new PwshRuntimeException("Script loader is not wired up.", cmd.Location);
            var result = ctx.Interpreter.RunScriptFile(scriptPath, /*dotSource*/ true, scriptArgs);
            return ExpressionToEnumerable(result).ToList();
        }

        // Call operator `&`: invoke a script block or resolve a path-valued string.
        if (cmd.Name == "&" && positional.Count > 0)
        {
            var callTarget = positional[0];
            var callArgs = positional.Skip(1).ToArray();
            if (callTarget is Runtime.ScriptBlock sb)
            {
                var savedArgs = ctx.Scope.Get(null, "args");
                ctx.Scope.Set(null, "args", callArgs);
                try { return ExpressionToEnumerable(sb.Invoke()).ToList(); }
                finally { ctx.Scope.Set(null, "args", savedArgs); }
            }
            if (callTarget is string path)
            {
                return DispatchPath(path, callArgs, ctx);
            }
            throw new PwshRuntimeException(
                $"Call operator '&' requires a script block or path string; got [{callTarget?.GetType().Name ?? "null"}].",
                cmd.Location);
        }

        // Path-like or registered-app dispatch.
        var name = cmd.Name;
        if (LooksLikePath(name))
        {
            return DispatchPath(name, positional, ctx);
        }

        if (ctx.Interpreter.Apps != null && ctx.Interpreter.Apps.TryGetPath(name, out var appPath))
        {
            return DispatchPath(appPath, positional, ctx);
        }

        throw new PwshRuntimeException($"The term '{cmd.Name}' is not recognized as a cmdlet, function, or script.", cmd.Location);
    }

    private static bool LooksLikePath(string name)
        => name.Contains('/') || name.StartsWith(".", StringComparison.Ordinal)
        || name.StartsWith("~", StringComparison.Ordinal);

    private static IEnumerable<object?> DispatchPath(string path, IReadOnlyList<object?> args, CmdletContext ctx)
    {
        var isDll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (isDll)
        {
            if (ctx.Interpreter.RunApp == null)
                throw new PwshRuntimeException("App invoker is not wired up.", Errors.SourceLocation.None);
            var code = ctx.Interpreter.RunApp(path, args);
            return ExpressionToEnumerable(null).ToList();
        }
        if (ctx.Interpreter.RunScriptFile == null)
            throw new PwshRuntimeException("Script loader is not wired up.", Errors.SourceLocation.None);
        var result = ctx.Interpreter.RunScriptFile(path, /*dotSource*/ false, args);
        return ExpressionToEnumerable(result).ToList();
    }
}
