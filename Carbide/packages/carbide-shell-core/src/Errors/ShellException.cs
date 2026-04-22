namespace CarbideShellCore.Errors;

/// <summary>
/// Base type for exceptions raised by the shared shell infrastructure (VFS, dispatcher,
/// env-var store). Shells wrap these into their own dialect-appropriate error types at
/// interpretation time; the base type exists so downstream code can catch "any shared-core
/// failure" without reaching into the per-shell hierarchies.
/// </summary>
public abstract class ShellException : Exception
{
    protected ShellException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Raised by <see cref="CarbideShellCore.Vfs.VirtualFileSystem"/> operations when a path
/// resolution, creation, or mutation fails. Each shell dialect catches this at a higher
/// layer and re-throws as its own runtime-error type so dialect-specific diagnostics can
/// include source locations.
/// </summary>
public sealed class VfsException : ShellException
{
    public VfsException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Raised by the <see cref="CarbideShellCore.Dispatch.ShellDispatcher"/> when a cross-shell
/// invocation cannot resolve the requested kernel (unknown shell name or unknown extension).
/// Individual shells typically convert this into their own "command not found" shape.
/// </summary>
public sealed class DispatchException : ShellException
{
    public DispatchException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Thrown from a cross-shell launcher when the host has opted into async-driven sub-shell
/// entry (typically because the runtime is WASM single-threaded and cannot block on
/// <c>Console.In.ReadLine</c>). The outer async REPL loop catches the exception, pushes
/// the target kernel on its shell stack, and resumes reading from the terminal. Nesting
/// is achieved by the outer loop, not by recursion inside the synchronous interpreter —
/// which lets cmd / bash / pwsh stay fully synchronous internally.
/// </summary>
public sealed class RequestSubShellException : Exception
{
    public CarbideShellCore.Dispatch.IShellKernel Kernel { get; }

    public RequestSubShellException(CarbideShellCore.Dispatch.IShellKernel kernel)
        : base($"enter subshell: {kernel.Name}")
    {
        Kernel = kernel;
    }
}
