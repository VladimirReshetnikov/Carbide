using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;

namespace CarbidePwsh.Runtime;

public sealed class ScriptFunction
{
    public string Name { get; }
    public IReadOnlyList<ParameterAst> Parameters { get; }
    public ScriptAst? BeginBlock { get; }
    public ScriptAst? ProcessBlock { get; }
    public ScriptAst? EndBlock { get; }
    public ScriptAst? CleanBlock { get; }
    public ScriptAst? SimpleBody { get; }
    public bool IsPipelineParticipant => ProcessBlock != null || BeginBlock != null || EndBlock != null || CleanBlock != null;

    public ScriptFunction(FunctionDefinitionAst ast)
    {
        Name = ast.Name;
        Parameters = ast.Parameters;
        BeginBlock = ast.BeginBlock;
        ProcessBlock = ast.ProcessBlock;
        EndBlock = ast.EndBlock;
        CleanBlock = ast.CleanBlock;
        SimpleBody = ast.SimpleBody;
    }

    /// <summary>
    /// Invoke as a single non-pipeline call. Binds parameters, runs the body (simple or
    /// begin+process(once)+end), and returns the function's final result.
    /// </summary>
    public object? Invoke(
        IReadOnlyList<object?> positional,
        IReadOnlyDictionary<string, object?> named,
        Interpreter interpreter)
    {
        using (interpreter.Scope.Push(ScopeKind.Function))
        {
            BindParameters(positional, named, interpreter);
            interpreter.Scope.Set(null, "args", ExtractExtraArgs(positional).ToArray());

            if (SimpleBody != null)
            {
                return RunBlock(SimpleBody, interpreter);
            }
            // Pipeline-participating function called without a pipeline: run begin, then end.
            object? last = null;
            bool executedNamedBlock = false;
            try
            {
                if (BeginBlock != null)
                {
                    last = RunBlock(BeginBlock, interpreter) ?? last;
                    executedNamedBlock = true;
                }
                if (EndBlock != null)
                {
                    last = RunBlock(EndBlock, interpreter) ?? last;
                    executedNamedBlock = true;
                }
                return last;
            }
            finally
            {
                if (executedNamedBlock && CleanBlock != null)
                    _ = RunBlock(CleanBlock, interpreter);
            }
        }
    }

    /// <summary>Invoke as a pipeline stage: runs begin, then process per item, then end.</summary>
    public IEnumerable<object?> InvokeAsPipelineStage(
        IEnumerable<object?>? input,
        IReadOnlyList<object?> positional,
        IReadOnlyDictionary<string, object?> named,
        Interpreter interpreter)
    {
        var results = new List<object?>();
        using (interpreter.Scope.Push(ScopeKind.Function))
        {
            BindParameters(positional, named, interpreter);
            bool executedNamedBlock = false;

            try
            {
                if (BeginBlock != null)
                {
                    AppendFrom(RunBlock(BeginBlock, interpreter), results);
                    executedNamedBlock = true;
                }

                if (ProcessBlock != null && input != null)
                {
                    foreach (var item in input)
                    {
                        interpreter.Scope.Set(null, "_", item);
                        interpreter.Scope.Set(null, "PSItem", item);
                        AppendFrom(RunBlock(ProcessBlock, interpreter), results);
                        executedNamedBlock = true;
                    }
                }
                else if (SimpleBody != null)
                {
                    // Simple function in a pipeline: ignore input and call once.
                    AppendFrom(RunBlock(SimpleBody, interpreter), results);
                }

                if (EndBlock != null)
                {
                    AppendFrom(RunBlock(EndBlock, interpreter), results);
                    executedNamedBlock = true;
                }
            }
            finally
            {
                if (executedNamedBlock && CleanBlock != null)
                    AppendFrom(RunBlock(CleanBlock, interpreter), results);
            }
        }
        return results;
    }

    private static void AppendFrom(object? value, List<object?> results)
    {
        if (value == null) return;
        if (value is string) { results.Add(value); return; }
        if (value is System.Collections.IDictionary) { results.Add(value); return; }
        if (value is System.Collections.IEnumerable en)
        {
            foreach (var it in en) results.Add(it);
            return;
        }
        results.Add(value);
    }

    private static object? RunBlock(ScriptAst body, Interpreter interpreter)
    {
        try
        {
            return interpreter.Evaluate(body);
        }
        catch (PwshReturnException ret)
        {
            return ret.Value;
        }
    }

    private void BindParameters(
        IReadOnlyList<object?> positional,
        IReadOnlyDictionary<string, object?> named,
        Interpreter interpreter)
    {
        int posIndex = 0;
        foreach (var p in Parameters)
        {
            object? value;
            if (named.TryGetValue(p.Name, out var namedValue))
            {
                value = namedValue;
            }
            else if (posIndex < positional.Count)
            {
                value = positional[posIndex++];
            }
            else if (p.DefaultValue != null)
            {
                value = interpreter.Eval(p.DefaultValue);
            }
            else
            {
                value = null;
            }

            if (p.TypeConstraint != null && value != null)
            {
                var targetType = interpreter.Types.ResolveType(p.TypeConstraint.TypeName, p.TypeConstraint.Location);
                try { value = Coercion.To(value, targetType); }
                catch (PwshCoercionException)
                {
                    throw new PwshRuntimeException(
                        $"Cannot convert argument for parameter '{p.Name}' to type [{p.TypeConstraint.TypeName}].",
                        p.Location);
                }
            }

            interpreter.Scope.Set(null, p.Name, value);
        }
    }

    private IEnumerable<object?> ExtractExtraArgs(IReadOnlyList<object?> positional)
    {
        for (int i = Parameters.Count; i < positional.Count; i++) yield return positional[i];
    }
}
