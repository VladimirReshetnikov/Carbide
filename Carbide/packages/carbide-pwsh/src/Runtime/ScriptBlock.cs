using CarbidePwsh.Parser.Ast;

namespace CarbidePwsh.Runtime;

/// <summary>
/// A deferred block of script wrapped at runtime. Phase 2 closes over a single live scope;
/// Phase 3 will extend this with a proper scope stack so functions introduce their own
/// variable frames. Invocation temporarily binds <c>$_</c> / <c>$PSItem</c> to the given
/// pipeline item and then evaluates the body.
/// </summary>
public sealed class ScriptBlock
{
    private readonly ScriptAst _body;
    private readonly Interpreter _interpreter;

    public ScriptBlock(ScriptAst body, Interpreter interpreter)
    {
        _body = body;
        _interpreter = interpreter;
    }

    public ScriptAst Body => _body;

    /// <summary>Evaluate with the current scope unchanged.</summary>
    public object? Invoke() => _interpreter.Evaluate(_body);

    /// <summary>Evaluate with <c>$_</c> and <c>$PSItem</c> bound to <paramref name="item"/>.</summary>
    public object? InvokeForPipelineItem(object? item)
    {
        var scope = _interpreter.Scope;
        var savedUnderscore = scope.Get(null, "_");
        var savedPsItem = scope.Get(null, "PSItem");
        scope.Set(null, "_", item);
        scope.Set(null, "PSItem", item);
        try
        {
            return _interpreter.Evaluate(_body);
        }
        finally
        {
            scope.Set(null, "_", savedUnderscore);
            scope.Set(null, "PSItem", savedPsItem);
        }
    }
}
