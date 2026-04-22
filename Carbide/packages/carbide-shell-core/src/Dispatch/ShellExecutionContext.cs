using CarbideShellCore.Apps;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideShellCore.Dispatch;

/// <summary>
/// Per-invocation plumbing passed into an <see cref="IShellKernel"/>. A shell that hands
/// execution to another shell (cross-shell launcher, file-extension routing, explicit
/// <c>powershell -c</c> / <c>bash -c</c> / <c>cmd /c</c> calls) clones its own context and
/// substitutes the argv, streams, or scoped env it wants the child to see.
/// </summary>
public sealed class ShellExecutionContext
{
    /// <summary>Positional arguments. Index 0 is conventionally the script path when a file is executed.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Standard input. Each shell treats <c>null</c> and <see cref="TextReader.Null"/> interchangeably.</summary>
    public TextReader Input { get; init; } = TextReader.Null;

    /// <summary>Standard output.</summary>
    public TextWriter Output { get; init; } = TextWriter.Null;

    /// <summary>Standard error.</summary>
    public TextWriter Error { get; init; } = TextWriter.Null;

    /// <summary>Session-shared virtualized filesystem.</summary>
    public VirtualFileSystem Vfs { get; init; } = null!;

    /// <summary>Session-shared environment-variable store.</summary>
    public EnvVarStore Env { get; init; } = null!;

    /// <summary>Session-shared Carbide-app name→VFS-path registry.</summary>
    public AppRegistry Apps { get; init; } = null!;

    /// <summary>Dispatcher, so child invocations can resolve further cross-shell calls.</summary>
    public ShellDispatcher Dispatcher { get; init; } = null!;

    /// <summary>
    /// Return a shallow clone with the supplied overrides. Used by cross-shell launchers
    /// that forward most of the context but substitute argv, input, or error streams.
    /// </summary>
    public ShellExecutionContext With(
        IReadOnlyList<string>? args = null,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null)
        => new()
        {
            Args = args ?? Args,
            Input = input ?? Input,
            Output = output ?? Output,
            Error = error ?? Error,
            Vfs = Vfs,
            Env = Env,
            Apps = Apps,
            Dispatcher = Dispatcher,
        };
}
