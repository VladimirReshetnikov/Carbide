using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideMultishell.Tests;

/// <summary>
/// Session facade used by the cross-shell integration tests. Creates one shared
/// <see cref="VirtualFileSystem"/>, <see cref="EnvVarStore"/>, <see cref="AppRegistry"/>, and
/// <see cref="ShellDispatcher"/>, then constructs cmd/bash/pwsh hosts over them. Registers
/// a kernel adapter for pwsh so cmd/bash can dispatch <c>powershell -c "..."</c> back into
/// pwsh.
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

        Dispatcher.Register(new PwshKernelAdapter(Pwsh));
    }
}

/// <summary>
/// Adapter exposing <see cref="CarbidePwsh.Host.ShellHost"/> through
/// <see cref="IShellKernel"/>. pwsh's existing <c>SubmitAndRender</c> API takes a
/// <see cref="TextWriter"/> for output but reads/writes <see cref="Console.In"/>/
/// <see cref="Console.Error"/> internally — we temporarily rebind those while the adapter
/// executes, so the invoking shell's streams are honored.
/// </summary>
internal sealed class PwshKernelAdapter : IShellKernel
{
    private readonly CarbidePwsh.Host.ShellHost _host;
    public PwshKernelAdapter(CarbidePwsh.Host.ShellHost host) { _host = host; }

    public string Name => "pwsh";
    public IReadOnlyCollection<string> Aliases { get; } = new[] { "powershell" };
    public IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".ps1", ".psm1" };

    public int Execute(string source, ShellExecutionContext ctx)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;
        try
        {
            Console.SetOut(ctx.Output);
            Console.SetError(ctx.Error);
            Console.SetIn(ctx.Input);
            try { _host.SubmitAndRender(source, ctx.Output); return 0; }
            catch { return 1; }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    public int ExecuteFile(string absolutePath, ShellExecutionContext ctx)
    {
        var file = ctx.Vfs.Resolve(absolutePath) as CarbideShellCore.Vfs.VfsFile;
        if (file is null) return 1;
        return Execute(file.ReadText(), ctx);
    }

    public bool IsCompleteInput(string source) => true;
}
