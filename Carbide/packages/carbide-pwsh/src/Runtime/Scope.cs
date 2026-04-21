namespace CarbidePwsh.Runtime;

public enum ScopeKind
{
    Global,
    Script,
    Function,
    Local,
}

/// <summary>
/// A single frame in the variable scope stack. Frames chain via <see cref="Parent"/> to form
/// the lookup path: variable resolution walks innermost → outermost.
/// </summary>
public sealed class ScopeFrame
{
    public ScopeKind Kind { get; }
    public ScopeFrame? Parent { get; }
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ScopeFrame(ScopeKind kind, ScopeFrame? parent)
    {
        Kind = kind;
        Parent = parent;
    }
}

/// <summary>
/// Phase 3 scope: a stack of frames with PowerShell-flavored qualifier routing. <c>$x</c>
/// walks the chain for reads; writes default to the innermost frame. <c>$script:x</c> routes
/// to the enclosing script scope. <c>$global:x</c> routes to the always-present global frame.
/// <c>$env:NAME</c> is delegated to environment variables.
/// </summary>
public sealed class Scope
{
    public ScopeFrame GlobalFrame { get; }
    public ScopeFrame ScriptFrame { get; private set; }
    public ScopeFrame CurrentFrame { get; private set; }

    public Scope()
    {
        GlobalFrame = new ScopeFrame(ScopeKind.Global, null);
        ScriptFrame = GlobalFrame;
        CurrentFrame = GlobalFrame;
    }

    public object? Get(string? qualifier, string name)
    {
        if (qualifier != null)
        {
            if (string.Equals(qualifier, "env", StringComparison.OrdinalIgnoreCase))
                return Environment.GetEnvironmentVariable(name);
            if (string.Equals(qualifier, "global", StringComparison.OrdinalIgnoreCase))
                return GlobalFrame.Variables.TryGetValue(name, out var gv) ? gv : null;
            if (string.Equals(qualifier, "script", StringComparison.OrdinalIgnoreCase))
                return ScriptFrame.Variables.TryGetValue(name, out var sv) ? sv : null;
            if (string.Equals(qualifier, "local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, "private", StringComparison.OrdinalIgnoreCase))
                return CurrentFrame.Variables.TryGetValue(name, out var lv) ? lv : null;
        }

        var frame = CurrentFrame;
        while (frame != null)
        {
            if (frame.Variables.TryGetValue(name, out var v)) return v;
            frame = frame.Parent;
        }
        return null;
    }

    public void Set(string? qualifier, string name, object? value)
    {
        if (qualifier != null)
        {
            if (string.Equals(qualifier, "env", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(name, value?.ToString());
                return;
            }
            if (string.Equals(qualifier, "global", StringComparison.OrdinalIgnoreCase))
            {
                GlobalFrame.Variables[name] = value;
                return;
            }
            if (string.Equals(qualifier, "script", StringComparison.OrdinalIgnoreCase))
            {
                ScriptFrame.Variables[name] = value;
                return;
            }
            if (string.Equals(qualifier, "local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, "private", StringComparison.OrdinalIgnoreCase))
            {
                CurrentFrame.Variables[name] = value;
                return;
            }
        }

        // Default write: innermost frame.
        CurrentFrame.Variables[name] = value;
    }

    /// <summary>Push a new frame onto the stack. Returns a handle that pops it on dispose.</summary>
    public IDisposable Push(ScopeKind kind)
    {
        var frame = new ScopeFrame(kind, CurrentFrame);
        var previousCurrent = CurrentFrame;
        CurrentFrame = frame;

        // A Script push also updates the current Script frame for $script: routing.
        var previousScript = ScriptFrame;
        if (kind == ScopeKind.Script)
        {
            ScriptFrame = frame;
        }

        return new Popper(() =>
        {
            CurrentFrame = previousCurrent;
            ScriptFrame = previousScript;
        });
    }

    private sealed class Popper : IDisposable
    {
        private Action? _onDispose;
        public Popper(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
    }

    public bool Contains(string? qualifier, string name)
    {
        if (qualifier != null)
        {
            if (string.Equals(qualifier, "global", StringComparison.OrdinalIgnoreCase))
                return GlobalFrame.Variables.ContainsKey(name);
            if (string.Equals(qualifier, "script", StringComparison.OrdinalIgnoreCase))
                return ScriptFrame.Variables.ContainsKey(name);
            if (string.Equals(qualifier, "local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, "private", StringComparison.OrdinalIgnoreCase))
                return CurrentFrame.Variables.ContainsKey(name);
        }
        var frame = CurrentFrame;
        while (frame != null)
        {
            if (frame.Variables.ContainsKey(name)) return true;
            frame = frame.Parent;
        }
        return false;
    }
}
