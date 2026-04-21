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
        if (!registry.TryResolve(cmd.Name, out var cmdlet) || cmdlet is null)
            throw new PwshRuntimeException($"The term '{cmd.Name}' is not recognized as a cmdlet.", cmd.Location);

        var positional = new List<object?>();
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < cmd.Elements.Count; i++)
        {
            var el = cmd.Elements[i];
            if (el is CommandParameterAst param)
            {
                // See if the following element is a value, otherwise it's a switch.
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

        var binding = new ParameterBinding(positional, named);
        return cmdlet.Invoke(input, binding, ctx);
    }
}
