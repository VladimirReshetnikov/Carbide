using CarbidePwsh.Runtime;
using CarbidePwsh.Vfs;

namespace CarbidePwsh.Cmdlets;

/// <summary>
/// Phase 2 cmdlet base. A cmdlet is a stateless-ish transformer on the pipeline: it receives
/// the upstream stage's output (or <c>null</c> for the first stage) plus its bound parameters,
/// and yields its own output as an enumerable. Downstream stages pull lazily.
/// </summary>
public abstract class Cmdlet
{
    public abstract string Name { get; }

    public virtual IEnumerable<string> Aliases => Array.Empty<string>();

    public abstract IEnumerable<object?> Invoke(
        IEnumerable<object?>? input,
        ParameterBinding binding,
        CmdletContext context);
}

public sealed class CmdletContext
{
    public Interpreter Interpreter { get; }
    public VirtualFileSystem Vfs { get; }
    public TextWriter Output { get; }
    public TextWriter Error { get; }

    public Scope Scope => Interpreter.Scope;
    public TypeBridge Types => Interpreter.Types;

    public CmdletContext(Interpreter interpreter, VirtualFileSystem vfs, TextWriter output, TextWriter error)
    {
        Interpreter = interpreter;
        Vfs = vfs;
        Output = output;
        Error = error;
    }
}
