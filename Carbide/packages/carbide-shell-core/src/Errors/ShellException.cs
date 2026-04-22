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
