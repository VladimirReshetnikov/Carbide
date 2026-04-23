using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideBash.Host;

public sealed class ShellHost
{
    public VirtualFileSystem Vfs { get; }
    public EnvVarStore Env { get; }
    public AppRegistry Apps { get; }
    public ShellDispatcher Dispatcher { get; }
    public BashKernel Kernel { get; }

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

        Kernel = new BashKernel();
        Dispatcher.Register(Kernel);
        StubInstaller.Install(Vfs, Dispatcher, Kernel, new[]
        {
            "/usr/bin/bash",
            "/usr/bin/bash.exe",
            "/usr/bin/sh",
            "/usr/bin/sh.exe",
            "/bin/bash",
            "/bin/bash.exe",
            "/bin/sh",
            "/bin/sh.exe",
            "/Program Files/Git/usr/bin/bash",
            "/Program Files/Git/usr/bin/bash.exe",
            "/Program Files/Git/usr/bin/sh",
            "/Program Files/Git/usr/bin/sh.exe",
        });
    }

    public int Submit(string source, TextReader? input = null, TextWriter? output = null, TextWriter? error = null)
    {
        var ctx = new ShellExecutionContext
        {
            Args = new[] { "bash" },
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

    public string BuildPrompt() => $"user@carbide:{Vfs.CurrentLocation}$ ";
    public string ContinuationPrompt() => "> ";
}
