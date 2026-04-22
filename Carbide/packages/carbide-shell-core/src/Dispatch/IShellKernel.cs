namespace CarbideShellCore.Dispatch;

/// <summary>
/// A per-dialect shell kernel. Each Carbide shell package (<c>carbide-pwsh</c>,
/// <c>carbide-cmd</c>, <c>carbide-bash</c>) exports exactly one implementation and
/// registers it with the session's <see cref="ShellDispatcher"/>. The dispatcher uses the
/// kernel to route cross-shell invocations and file-extension-based script execution.
/// </summary>
public interface IShellKernel
{
    /// <summary>Canonical shell name: <c>pwsh</c>, <c>cmd</c>, or <c>bash</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Alternate names accepted by the dispatcher (e.g. <c>powershell</c> for pwsh,
    /// <c>sh</c> for bash, <c>cmd.exe</c> for cmd). Always lower-case; the dispatcher
    /// normalizes incoming names before comparing.
    /// </summary>
    IReadOnlyCollection<string> Aliases { get; }

    /// <summary>
    /// File extensions this kernel claims. The dispatcher's extension-based routing
    /// consults this set: <c>.ps1</c> / <c>.psm1</c> map to pwsh, <c>.cmd</c> / <c>.bat</c>
    /// map to cmd, <c>.sh</c> maps to bash. Extensions include the leading dot and are
    /// compared case-insensitively.
    /// </summary>
    IReadOnlyCollection<string> FileExtensions { get; }

    /// <summary>
    /// Evaluate a script source string. <paramref name="ctx"/> supplies argv, streams,
    /// the shared VFS, env, and dispatcher. Returns the exit code the script should
    /// propagate to its caller (0 on success).
    /// </summary>
    int Execute(string source, ShellExecutionContext ctx);

    /// <summary>
    /// Evaluate a script file by absolute VFS path. Default behavior reads the file and
    /// calls <see cref="Execute"/>; kernels that want shebang handling or other preprocessing
    /// override this method.
    /// </summary>
    int ExecuteFile(string absolutePath, ShellExecutionContext ctx);

    /// <summary>
    /// Return <see langword="true"/> if the given source is syntactically complete enough
    /// to be executed. Used by interactive REPLs to drive multi-line input: incomplete
    /// input accumulates, complete input submits.
    /// </summary>
    bool IsCompleteInput(string source);

    /// <summary>
    /// Build the primary interactive prompt for this kernel against the supplied session
    /// state. Implementations typically render the current VFS location plus a dialect-
    /// specific suffix (<c>PS /path&gt; </c>, <c>C:\path&gt; </c>, <c>user@host:/path$ </c>).
    /// </summary>
    string BuildPrompt(ShellExecutionContext ctx);

    /// <summary>
    /// Build the continuation prompt shown when the previous input was syntactically
    /// incomplete (open brace, unterminated quote, heredoc, etc.). A short sentinel like
    /// <c>&gt;&gt; </c> (pwsh), <c>More? </c> (cmd), or <c>&gt; </c> (bash) is conventional.
    /// </summary>
    string BuildContinuationPrompt(ShellExecutionContext ctx);
}
