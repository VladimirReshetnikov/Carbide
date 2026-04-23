using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;

namespace CarbideShellCore.Dispatch;

/// <summary>
/// Session-global resolver that routes cross-shell invocations. Every registered
/// <see cref="IShellKernel"/> is indexed by its canonical name, its aliases, and the file
/// extensions it claims; lookup honors those three axes in order.
/// <para>
/// The dispatcher also keeps the last cross-shell exit code in <see cref="LastExitCode"/>,
/// which each shell projects into its own automatic variable (pwsh's <c>$LASTEXITCODE</c>,
/// cmd's <c>%ERRORLEVEL%</c>, bash's <c>$?</c>).
/// </para>
/// </summary>
public sealed class ShellDispatcher
{
    private readonly Dictionary<string, IShellKernel> _byName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IShellKernel> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IShellKernel> _byStubPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly VirtualExecutableRegistry _virtualExecutables = new();
    private readonly Dictionary<string, IVirtualExecutableHandler> _virtualHandlers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exit code from the most recent cross-shell invocation (0 initially).</summary>
    public int LastExitCode { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="EnterSubShell"/> throws a
    /// <see cref="CarbideShellCore.Errors.RequestSubShellException"/> instead of running
    /// <see cref="RunInteractive"/> inline. The outer async REPL loop catches the
    /// exception and pushes the target kernel on its shell stack. Must be set in WASM
    /// single-threaded contexts because the synchronous REPL loop would otherwise block
    /// on <c>Console.In.ReadLine</c>. Default <see langword="false"/> keeps the
    /// synchronous fall-through that the xunit suite relies on.
    /// </summary>
    public bool ThrowOnSubShellEntry { get; set; }

    /// <summary>Register a kernel under its name, aliases, and file extensions.</summary>
    public void Register(IShellKernel kernel)
    {
        _byName[kernel.Name] = kernel;
        foreach (var alias in kernel.Aliases) _byName[alias] = kernel;
        foreach (var ext in kernel.FileExtensions) _byExtension[ext] = kernel;
    }

    /// <summary>
    /// Register an absolute VFS path (e.g. <c>/usr/bin/bash</c>) that, when invoked by
    /// path, resolves to the given kernel. Multiple paths may point to the same kernel.
    /// Used by each shell to materialize stub executables so <c>./pwsh.exe</c>,
    /// <c>/usr/bin/cmd</c>, and friends enter the corresponding interactive sub-REPL.
    /// </summary>
    public void RegisterStubPath(string absolutePath, IShellKernel kernel)
    {
        _byStubPath[absolutePath] = kernel;
    }

    /// <summary>
    /// Register a virtual executable definition in the dispatcher's catalog. Callers
    /// supply the handler separately through <see cref="RegisterVirtualExecutableHandler"/>.
    /// </summary>
    public void RegisterVirtualExecutable(VirtualExecutableDefinition definition)
        => _virtualExecutables.Register(definition);

    /// <summary>
    /// Register the handler that executes the given virtual executable definition.
    /// </summary>
    public void RegisterVirtualExecutableHandler(string handlerKey, IVirtualExecutableHandler handler)
        => _virtualHandlers[handlerKey] = handler;

    /// <summary>
    /// Register a definition and its handler together.
    /// </summary>
    public void RegisterVirtualExecutable(VirtualExecutableDefinition definition, IVirtualExecutableHandler handler)
    {
        RegisterVirtualExecutable(definition);
        RegisterVirtualExecutableHandler(definition.HandlerKey, handler);
    }

    /// <summary>
    /// Resolve all virtual executable matches visible for the supplied command from the
    /// perspective of the calling shell.
    /// </summary>
    public IReadOnlyList<VirtualExecutableMatch> ResolveVirtualExecutableMatches(
        string commandName,
        ShellExecutionContext ctx,
        string? callerShellName)
        => _virtualExecutables.ResolveMatches(commandName, ctx, callerShellName);

    /// <summary>Look up a kernel by its canonical name or alias.</summary>
    public bool TryResolveShellByName(string shellNameOrAlias, out IShellKernel? kernel)
    {
        if (_byName.TryGetValue(shellNameOrAlias, out var k)) { kernel = k; return true; }
        kernel = null;
        return false;
    }

    /// <summary>Look up a kernel by file extension (including the leading dot).</summary>
    public bool TryResolveShellByExtension(string extension, out IShellKernel? kernel)
    {
        if (_byExtension.TryGetValue(extension, out var k)) { kernel = k; return true; }
        kernel = null;
        return false;
    }

    /// <summary>
    /// Resolve a free-form command name (as typed in a shell) to either a registered
    /// Carbide app (returned via <paramref name="appPath"/>), a shell script in the VFS
    /// (returned via <paramref name="scriptPath"/> with its claiming kernel), an explicit
    /// shell alias (returned via <paramref name="namedKernel"/>), or nothing.
    /// </summary>
    public DispatchResolution Resolve(string commandName, ShellExecutionContext ctx, string? callerShellName = null)
    {
        if (TryResolveShellByName(commandName, out var named))
            return new DispatchResolution(ResolutionKind.NamedShell, null, null, named);

        // Path-like command? Look it up in the VFS and use its extension.
        if (LooksLikePath(commandName))
        {
            foreach (var abs in _virtualExecutables.BuildPathCandidates(commandName, ctx, callerShellName))
            {
                if (ctx.Vfs.Resolve(abs) is not VfsFile) continue;
                if (_byStubPath.TryGetValue(abs, out var stubKernel))
                    return new DispatchResolution(ResolutionKind.NamedShell, null, null, stubKernel);
                if (_virtualExecutables.TryResolveByPath(abs, out var definition) && definition is not null)
                    return new DispatchResolution(ResolutionKind.VirtualExecutable, null, null, null, definition, abs);
                var ext = VfsPath.GetExtension(abs);
                if (TryResolveShellByExtension(ext, out var byExt))
                    return new DispatchResolution(ResolutionKind.Script, null, abs, byExt);
                if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
                    return new DispatchResolution(ResolutionKind.App, abs, null, null);
            }
            return new DispatchResolution(ResolutionKind.Unresolved, null, null, null);
        }

        if (string.Equals(callerShellName, "pwsh", StringComparison.OrdinalIgnoreCase)
            && ctx.Apps.TryGetPath(commandName, out var pwshAppPath))
            return new DispatchResolution(ResolutionKind.App, pwshAppPath, null, null);

        if (_virtualExecutables.ResolveMatches(commandName, ctx, callerShellName).FirstOrDefault() is { } match)
            return new DispatchResolution(ResolutionKind.VirtualExecutable, null, null, null, match.Definition, match.ResolvedPath);

        if (ctx.Apps.TryGetPath(commandName, out var appPath))
            return new DispatchResolution(ResolutionKind.App, appPath, null, null);

        return new DispatchResolution(ResolutionKind.Unresolved, null, null, null);
    }

    /// <summary>
    /// Invoke <paramref name="kernel"/> with the given inline source and context. Updates
    /// <see cref="LastExitCode"/> with the return value.
    /// </summary>
    public int ExecuteInline(IShellKernel kernel, string source, ShellExecutionContext ctx)
    {
        if (kernel is null) throw new DispatchException("No kernel supplied to ExecuteInline.");
        var code = kernel.Execute(source, ctx);
        LastExitCode = code;
        return code;
    }

    /// <summary>
    /// Execute a VFS script file with the given kernel and context. The script's absolute
    /// VFS path is prepended to <c>ctx.Args</c> at index 0 so that shells observing their
    /// own <c>$0</c> / <c>%0</c> / <c>$PSCommandPath</c> see the script path.
    /// </summary>
    public int ExecuteScript(string absolutePath, IShellKernel kernel, ShellExecutionContext ctx)
    {
        if (kernel is null) throw new DispatchException("No kernel supplied to ExecuteScript.");
        var args = new List<string> { absolutePath };
        args.AddRange(ctx.Args);
        var scoped = ctx.With(args: args);
        var code = kernel.ExecuteFile(absolutePath, scoped);
        LastExitCode = code;
        return code;
    }

    /// <summary>
    /// Execute a registered virtual executable handler against the shared VFS/env/session
    /// state and update <see cref="LastExitCode"/> with the returned process-like exit code.
    /// </summary>
    public int ExecuteVirtualExecutable(
        VirtualExecutableDefinition definition,
        string resolvedPath,
        string invokedAs,
        IReadOnlyList<string> args,
        ShellExecutionContext ctx)
    {
        if (!_virtualHandlers.TryGetValue(definition.HandlerKey, out var handler))
            throw new DispatchException($"No virtual executable handler is registered for '{definition.HandlerKey}'.");

        var invocation = new VirtualExecutableInvocation
        {
            Definition = definition,
            ResolvedPath = resolvedPath,
            InvokedAs = invokedAs,
            Args = args,
            Input = ctx.Input,
            Output = ctx.Output,
            Error = ctx.Error,
            Vfs = ctx.Vfs,
            Env = ctx.Env,
            Apps = ctx.Apps,
            Dispatcher = this,
        };

        var code = handler.Execute(invocation);
        LastExitCode = code;
        return code;
    }

    /// <summary>
    /// Entry point used by cross-shell launchers when the user invokes a bare shell name
    /// (<c>cmd</c>, <c>bash</c>, <c>pwsh</c>, or a registered stub path). When
    /// <see cref="ThrowOnSubShellEntry"/> is set, throws
    /// <see cref="CarbideShellCore.Errors.RequestSubShellException"/> so the async outer
    /// REPL (Program.cs in WASM contexts) can stack the kernel without recursing through
    /// the synchronous interpreter. Otherwise falls through to the synchronous
    /// <see cref="RunInteractive"/> loop — the behavior the xunit suite relies on.
    /// </summary>
    public int EnterSubShell(IShellKernel kernel, ShellExecutionContext ctx)
    {
        if (ThrowOnSubShellEntry) throw new CarbideShellCore.Errors.RequestSubShellException(kernel);
        return RunInteractive(kernel, ctx);
    }

    /// <summary>
    /// Drive an interactive REPL over <paramref name="kernel"/>, reading lines from
    /// <c>ctx.Input</c> and writing prompts + output to <c>ctx.Output</c>. The loop:
    /// <list type="number">
    ///   <item>shows the kernel's primary prompt,</item>
    ///   <item>accumulates lines until <see cref="IShellKernel.IsCompleteInput"/> returns
    ///     <see langword="true"/> (showing the continuation prompt in between),</item>
    ///   <item>submits the accumulated source via <see cref="IShellKernel.Execute"/>,</item>
    ///   <item>intercepts <c>exit</c> / <c>quit</c> / <c>:q</c> and EOF to return from the
    ///     loop (with an optional exit code parsed from <c>exit N</c>).</item>
    /// </list>
    /// This is what a bare <c>cmd</c>, <c>bash</c>, or <c>pwsh</c> invocation from another
    /// shell resolves to: a nested REPL that unwinds when the user exits, leaving the
    /// caller's REPL intact. Nesting depth is unbounded.
    /// </summary>
    public int RunInteractive(IShellKernel kernel, ShellExecutionContext ctx)
    {
        if (kernel is null) throw new DispatchException("No kernel supplied to RunInteractive.");
        var pending = new System.Text.StringBuilder();
        int lastCode = 0;
        while (true)
        {
            var prompt = pending.Length == 0
                ? kernel.BuildPrompt(ctx)
                : kernel.BuildContinuationPrompt(ctx);
            ctx.Output.Write(prompt);
            ctx.Output.Flush();

            string? line;
            try { line = ctx.Input.ReadLine(); }
            catch (Exception ex)
            {
                ctx.Error.WriteLine($"{kernel.Name}: readline failed: {ex.Message}");
                break;
            }
            if (line is null) break;

            if (pending.Length == 0)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (IsExitLine(trimmed, out var exitCode))
                {
                    lastCode = exitCode;
                    break;
                }
            }

            if (pending.Length > 0) pending.Append('\n');
            pending.Append(line);

            var source = pending.ToString();
            if (!kernel.IsCompleteInput(source)) continue;

            try
            {
                lastCode = kernel.Execute(source, ctx);
            }
            catch (Exception ex)
            {
                ctx.Error.WriteLine($"{kernel.Name}: {ex.Message}");
                lastCode = 1;
            }
            pending.Clear();
        }
        LastExitCode = lastCode;
        return lastCode;
    }

    private static bool IsExitLine(string line, out int exitCode)
    {
        exitCode = 0;
        if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)
            || line.Equals("quit", StringComparison.OrdinalIgnoreCase)
            || line == ":q")
            return true;
        if (line.StartsWith("exit ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line.Substring(5).Trim();
            if (int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out exitCode))
                return true;
        }
        return false;
    }

    private static bool LooksLikePath(string commandName)
        => commandName.Contains('/')
        || commandName.Contains('\\')
        || commandName.StartsWith(".", StringComparison.Ordinal)
        || commandName.StartsWith("~", StringComparison.Ordinal)
        || (commandName.Length >= 3
            && char.IsLetter(commandName[0])
            && commandName[1] == ':'
            && (commandName[2] == '/' || commandName[2] == '\\'));
}

/// <summary>
/// Kind of artifact a dispatcher resolution found. Consumers switch on this to pick
/// between inline evaluation, file execution, and app invocation.
/// </summary>
public enum ResolutionKind
{
    Unresolved,
    NamedShell,
    VirtualExecutable,
    Script,
    App,
}

public sealed record DispatchResolution(
    ResolutionKind Kind,
    string? AppPath = null,
    string? ScriptPath = null,
    IShellKernel? Kernel = null,
    VirtualExecutableDefinition? VirtualExecutable = null,
    string? VirtualExecutablePath = null);
