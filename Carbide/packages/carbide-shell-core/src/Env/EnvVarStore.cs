namespace CarbideShellCore.Env;

/// <summary>
/// Session-global environment-variable map shared across every shell. When a pwsh script
/// writes <c>$env:FOO</c>, the cmd shell observes <c>%FOO%</c> and the bash shell observes
/// <c>$FOO</c>, all pointing at the same slot.
/// <para>
/// Lookup is case-insensitive: cmd and pwsh both treat env-var names case-insensitively on
/// Windows, and keeping bash aligned is a friendlier default than introducing a case
/// mismatch that would surprise someone round-tripping a variable through cmd.
/// </para>
/// <para>
/// Lexical scoping is supported through <see cref="PushScope"/>. Scopes are used by cmd's
/// <c>SETLOCAL</c> / <c>ENDLOCAL</c> and bash's <c>( … )</c> subshell construct; on scope
/// disposal, any mutation made inside the scope is reverted. pwsh, matching real
/// PowerShell, does not push scopes for env-var mutations.
/// </para>
/// </summary>
public sealed class EnvVarStore
{
    private readonly List<Frame> _stack = new();

    public EnvVarStore()
    {
        _stack.Add(new Frame());
    }

    /// <summary>Read a variable or <see langword="null"/> if unset.</summary>
    public string? Get(string name)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Values.TryGetValue(name, out var v)) return v;
        }
        return null;
    }

    /// <summary>Set the variable in the innermost scope. A <see langword="null"/> value removes it.</summary>
    public void Set(string name, string? value)
    {
        var top = _stack[^1];
        if (value is null) top.Values.Remove(name);
        else top.Values[name] = value;
    }

    /// <summary>Remove the variable from the innermost scope that defines it.</summary>
    public void Unset(string name)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Values.Remove(name)) return;
        }
    }

    /// <summary>
    /// Enumerate the merged view, where inner scopes override outer ones. Order is not
    /// guaranteed beyond "inner wins"; callers that need a sorted listing should sort the
    /// returned dictionary themselves.
    /// </summary>
    public IReadOnlyDictionary<string, string> All
    {
        get
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var frame in _stack)
            {
                foreach (var kv in frame.Values)
                {
                    merged[kv.Key] = kv.Value;
                }
            }
            return merged;
        }
    }

    /// <summary>
    /// Push a new frame that copies the current merged view. Mutations stay inside the new
    /// frame until the returned scope is disposed, at which point the frame pops. This
    /// matches bash subshell and cmd <c>SETLOCAL</c> semantics.
    /// </summary>
    public IDisposable PushScope()
    {
        var snapshot = new Frame();
        foreach (var kv in All)
            snapshot.Values[kv.Key] = kv.Value;
        _stack.Add(snapshot);
        return new ScopeToken(this, snapshot);
    }

    private sealed class Frame
    {
        public Dictionary<string, string> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ScopeToken : IDisposable
    {
        private readonly EnvVarStore _owner;
        private readonly Frame _frame;
        private bool _disposed;

        public ScopeToken(EnvVarStore owner, Frame frame)
        {
            _owner = owner;
            _frame = frame;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var idx = _owner._stack.IndexOf(_frame);
            if (idx >= 0) _owner._stack.RemoveAt(idx);
        }
    }
}
