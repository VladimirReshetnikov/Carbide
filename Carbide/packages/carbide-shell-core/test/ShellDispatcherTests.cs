using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;
using Xunit;

namespace CarbideShellCore.Tests;

public class ShellDispatcherTests
{
    private sealed class FakeKernel : IShellKernel
    {
        public string Name { get; }
        public IReadOnlyCollection<string> Aliases { get; }
        public IReadOnlyCollection<string> FileExtensions { get; }
        public int LastExitCode { get; private set; }
        public string? LastSource { get; private set; }
        public string? LastFile { get; private set; }
        public int Returns { get; set; }

        public FakeKernel(string name, string[] aliases, string[] exts)
        {
            Name = name;
            Aliases = aliases;
            FileExtensions = exts;
        }

        public int Execute(string source, ShellExecutionContext ctx)
        {
            LastSource = source;
            return Returns;
        }

        public int ExecuteFile(string absolutePath, ShellExecutionContext ctx)
        {
            LastFile = absolutePath;
            return Returns;
        }

        public bool IsCompleteInput(string source) => true;
        public string BuildPrompt(ShellExecutionContext ctx) => $"{Name}> ";
        public string BuildContinuationPrompt(ShellExecutionContext ctx) => ">> ";
    }

    private static ShellExecutionContext Context(VirtualFileSystem vfs, ShellDispatcher d, AppRegistry? apps = null)
        => new()
        {
            Vfs = vfs,
            Env = new EnvVarStore(),
            Apps = apps ?? new AppRegistry(),
            Dispatcher = d,
        };

    [Fact]
    public void RegisterIndexesByNameAndAlias()
    {
        var d = new ShellDispatcher();
        var pwsh = new FakeKernel("pwsh", new[] { "powershell" }, new[] { ".ps1" });
        d.Register(pwsh);

        Assert.True(d.TryResolveShellByName("pwsh", out var a));
        Assert.Same(pwsh, a);
        Assert.True(d.TryResolveShellByName("PowerShell", out var b));
        Assert.Same(pwsh, b);
    }

    [Fact]
    public void RegisterIndexesByExtension()
    {
        var d = new ShellDispatcher();
        var cmd = new FakeKernel("cmd", new[] { "cmd.exe" }, new[] { ".cmd", ".bat" });
        d.Register(cmd);

        Assert.True(d.TryResolveShellByExtension(".cmd", out var a));
        Assert.Same(cmd, a);
        Assert.True(d.TryResolveShellByExtension(".BAT", out var b));
        Assert.Same(cmd, b);
    }

    [Fact]
    public void ResolveFindsNamedShell()
    {
        var d = new ShellDispatcher();
        var bash = new FakeKernel("bash", new[] { "sh" }, new[] { ".sh" });
        d.Register(bash);

        var res = d.Resolve("sh", Context(new VirtualFileSystem(), d));
        Assert.Equal(ResolutionKind.NamedShell, res.Kind);
        Assert.Same(bash, res.Kernel);
    }

    [Fact]
    public void ResolveFindsScriptByExtension()
    {
        var d = new ShellDispatcher();
        var bash = new FakeKernel("bash", Array.Empty<string>(), new[] { ".sh" });
        d.Register(bash);

        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/work/run.sh", "echo hi", overwrite: false);
        vfs.CurrentLocation = "/work";

        var res = d.Resolve("./run.sh", Context(vfs, d));
        Assert.Equal(ResolutionKind.Script, res.Kind);
        Assert.Same(bash, res.Kernel);
        Assert.Equal("/work/run.sh", res.ScriptPath);
    }

    [Fact]
    public void ResolveFindsAppByRegistry()
    {
        var d = new ShellDispatcher();
        var vfs = new VirtualFileSystem();
        var apps = new AppRegistry();
        apps.Register("greet", "/work/greet.dll");
        var res = d.Resolve("greet", Context(vfs, d, apps));
        Assert.Equal(ResolutionKind.App, res.Kind);
        Assert.Equal("/work/greet.dll", res.AppPath);
    }

    [Fact]
    public void ResolveReturnsUnresolvedForUnknown()
    {
        var d = new ShellDispatcher();
        var res = d.Resolve("nope", Context(new VirtualFileSystem(), d));
        Assert.Equal(ResolutionKind.Unresolved, res.Kind);
    }

    [Fact]
    public void ExecuteInlineUpdatesLastExitCode()
    {
        var d = new ShellDispatcher();
        var k = new FakeKernel("x", Array.Empty<string>(), Array.Empty<string>()) { Returns = 7 };
        d.Register(k);

        var code = d.ExecuteInline(k, "echo", Context(new VirtualFileSystem(), d));
        Assert.Equal(7, code);
        Assert.Equal(7, d.LastExitCode);
    }

    [Fact]
    public void ExecuteScriptPrependsScriptPathToArgs()
    {
        var d = new ShellDispatcher();
        var k = new FakeKernel("x", Array.Empty<string>(), new[] { ".x" });
        d.Register(k);

        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/work/run.x", "body", overwrite: false);
        var baseCtx = new ShellExecutionContext
        {
            Args = new[] { "a", "b" },
            Vfs = vfs,
            Env = new EnvVarStore(),
            Apps = new AppRegistry(),
            Dispatcher = d,
        };
        d.ExecuteScript("/work/run.x", k, baseCtx);
        Assert.Equal("/work/run.x", k.LastFile);
    }

    [Fact]
    public void ExecuteInlineThrowsWhenKernelIsNull()
    {
        var d = new ShellDispatcher();
        Assert.Throws<DispatchException>(() => d.ExecuteInline(null!, "x", Context(new VirtualFileSystem(), d)));
    }
}
