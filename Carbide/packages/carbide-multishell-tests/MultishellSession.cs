using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideMultishell.Tests;

/// <summary>
/// Session facade used by the cross-shell integration tests. Creates one shared
/// <see cref="VirtualFileSystem"/>, <see cref="EnvVarStore"/>, <see cref="AppRegistry"/>, and
/// <see cref="ShellDispatcher"/>, then constructs cmd/bash/pwsh hosts over them. Each host
/// registers its own <see cref="IShellKernel"/> and installs stub <c>.exe</c> files in
/// <c>/usr/bin</c>, so the shared dispatcher resolves <c>pwsh</c> / <c>cmd</c> / <c>bash</c>
/// (and their path-qualified stubs) without any demo-specific adapter.
/// </summary>
internal sealed class MultishellSession
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
}
