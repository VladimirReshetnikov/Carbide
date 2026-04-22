using CarbideCmd.Runtime;
using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Host;

/// <summary>
/// Session facade for the cmd dialect. Bundles the shared VFS, env store, app registry, and
/// dispatcher, and exposes a <see cref="Submit"/> that parses and executes a source string in
/// the session's persistent state.
/// </summary>
public sealed class ShellHost
{
    public VirtualFileSystem Vfs { get; }
    public EnvVarStore Env { get; }
    public AppRegistry Apps { get; }
    public ShellDispatcher Dispatcher { get; }
    public CmdKernel Kernel { get; }

    public ShellHost(
        VirtualFileSystem? vfs = null,
        EnvVarStore? env = null,
        AppRegistry? apps = null,
        ShellDispatcher? dispatcher = null)
    {
        Vfs = vfs ?? new VirtualFileSystem();
        Env = env ?? new EnvVarStore();
        Apps = apps ?? new AppRegistry();
        Dispatcher = dispatcher ?? new ShellDispatcher();

        if (vfs is null)
        {
            Vfs.CreateDirectory("/tmp");
            Vfs.CreateDirectory("/home/user");
            Vfs.CurrentLocation = "/home/user";
        }

        Kernel = new CmdKernel();
        Dispatcher.Register(Kernel);
        StubInstaller.Install(Vfs, Dispatcher, Kernel, new[]
        {
            "/usr/bin/cmd",
            "/usr/bin/cmd.exe",
            "/bin/cmd",
            "/bin/cmd.exe",
            "/Windows/System32/cmd.exe",
        });
    }

    public int Submit(string source, TextReader? input = null, TextWriter? output = null, TextWriter? error = null)
    {
        var ctx = new ShellExecutionContext
        {
            Args = Array.Empty<string>(),
            Input = input ?? Console.In,
            Output = output ?? Console.Out,
            Error = error ?? Console.Error,
            Vfs = Vfs,
            Env = Env,
            Apps = Apps,
            Dispatcher = Dispatcher,
        };
        return Kernel.Execute(source, ctx);
    }

    public string BuildPrompt() => $"{DisplayLocation()}> ";
    public string ContinuationPrompt() => "More? ";

    private string DisplayLocation()
    {
        // Present VFS paths as cmd-flavored with a synthetic C: drive letter and backslashes
        // on display. The underlying VFS remains forward-slash-normalized.
        var loc = Vfs.CurrentLocation;
        if (loc == "/") return "C:\\";
        return "C:" + loc.Replace('/', '\\');
    }
}
