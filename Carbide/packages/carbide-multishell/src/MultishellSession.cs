using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideMultishell;

/// <summary>
/// Canonical multishell session wiring. Creates one shared
/// <see cref="VirtualFileSystem"/>, <see cref="EnvVarStore"/>, <see cref="AppRegistry"/>, and
/// <see cref="ShellDispatcher"/>; constructs one <see cref="CarbidePwsh.Host.ShellHost"/>,
/// <see cref="CarbideCmd.Host.ShellHost"/>, and <see cref="CarbideBash.Host.ShellHost"/>
/// over them. Each host registers its own <see cref="IShellKernel"/> and installs its stub
/// <c>.exe</c> files in <c>/usr/bin</c>, so the shared dispatcher resolves
/// <c>pwsh</c> / <c>cmd</c> / <c>bash</c> (and path-qualified stubs) without any
/// per-embedder adapter code.
/// <para>
/// This is the configuration any multishell host should boot: the demo REPL, the smoke
/// tests, and the cross-shell integration tests all construct one of these and dispatch
/// straight through <see cref="ShellDispatcher.RunInteractive"/>.
/// </para>
/// </summary>
public sealed class MultishellSession
{
    public VirtualFileSystem Vfs { get; } = new();
    public EnvVarStore Env { get; } = new();
    public AppRegistry Apps { get; } = new();
    public ShellDispatcher Dispatcher { get; } = new();

    public CarbidePwsh.Host.ShellHost Pwsh { get; }
    public CarbideCmd.Host.ShellHost Cmd { get; }
    public CarbideBash.Host.ShellHost Bash { get; }

    public MultishellSession()
    {
        Vfs.CreateDirectory("/tmp");
        Vfs.CreateDirectory("/home/user");
        Vfs.CreateDirectory("/work");
        Vfs.CurrentLocation = "/home/user";

        Pwsh = new CarbidePwsh.Host.ShellHost(Vfs, Env, Apps, Dispatcher);
        Cmd = new CarbideCmd.Host.ShellHost(Vfs, Env, Apps, Dispatcher);
        Bash = new CarbideBash.Host.ShellHost(Vfs, Env, Apps, Dispatcher);
    }

    /// <summary>
    /// Build a <see cref="ShellExecutionContext"/> for the given streams with this
    /// session's shared state. Used by the REPL entry point to launch a top-level
    /// interactive shell.
    /// </summary>
    public ShellExecutionContext BuildContext(TextReader input, TextWriter output, TextWriter error)
        => new()
        {
            Args = Array.Empty<string>(),
            Input = input,
            Output = output,
            Error = error,
            Vfs = Vfs,
            Env = Env,
            Apps = Apps,
            Dispatcher = Dispatcher,
        };

    /// <summary>
    /// Resolve a shell by name (<c>pwsh</c>, <c>powershell</c>, <c>cmd</c>, <c>cmd.exe</c>,
    /// <c>bash</c>, <c>sh</c>). Throws <see cref="CarbideShellCore.Errors.DispatchException"/>
    /// if the name is unknown.
    /// </summary>
    public IShellKernel ResolveKernel(string shellName)
    {
        if (!Dispatcher.TryResolveShellByName(shellName, out var kernel) || kernel is null)
            throw new CarbideShellCore.Errors.DispatchException($"Unknown shell '{shellName}'.");
        return kernel;
    }
}
