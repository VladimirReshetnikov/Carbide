using CarbideShellCore.Dispatch;
using Xunit;

namespace CarbideMultishell.Tests;

public class CScriptVirtualExecutableTests
{
    [Fact]
    public void CScriptReportsHostErrorsAndHelp()
    {
        var session = new MultishellSession();

        var missing = RunVirtual(session, "cscript", [], "cmd");
        var help = RunVirtual(session, "cscript", ["//?"], "cmd");
        var unsupported = RunVirtual(session, "cscript", ["//wat"], "cmd");

        Assert.Equal(1, missing.Code);
        Assert.Contains("no script file specified", missing.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, help.Code);
        Assert.Contains("Carbide cscript", help.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, unsupported.Code);
        Assert.Contains("unsupported host option", unsupported.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JScriptCanEchoArgumentsAndReturnExitCode()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/hello.js",
            """
            WScript.Echo("hello " + WScript.Arguments(0));
            WScript.Echo(WScript.Arguments(1));
            WScript.Quit(7);
            """,
            overwrite: false);

        var result = RunVirtual(session, "cscript", ["//NOLOGO", "/work/hello.js", "value", "/mode:copy"], "pwsh");

        Assert.Equal(7, result.Code);
        Assert.Equal("hello value\n/mode:copy\n", NormalizeLines(result.Stdout));
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void JScriptCanUseStdStreamsAndFileSystemObjectAgainstVfs()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/io.js",
            """
            var fso = WScript.CreateObject("Scripting.FileSystemObject");
            var file = fso.CreateTextFile("/work/out.txt", true);
            file.WriteLine("alpha");
            file.Write("beta");
            file.Close();
            var input = fso.OpenTextFile("/work/out.txt", 1);
            WScript.StdOut.Write(input.ReadAll());
            input.Close();
            """,
            overwrite: false);

        var result = RunVirtual(session, "cscript.exe", ["//nologo", "/work/io.js"], "cmd");

        Assert.Equal(0, result.Code);
        Assert.Equal("alpha\nbeta", NormalizeLines(result.Stdout));
        Assert.Equal("alpha\nbeta", NormalizeLines(session.Vfs.GetRequired("/work/out.txt").AsFile().ReadText()));
    }

    [Fact]
    public void JScriptReadsPipedStdIn()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/stdin.js",
            """WScript.StdOut.WriteLine(WScript.StdIn.ReadLine());""",
            overwrite: false);

        var result = RunVirtual(session, "cscript", ["//nologo", "/work/stdin.js"], "pwsh", new StringReader("piped\nignored\n"));

        Assert.Equal(0, result.Code);
        Assert.Equal("piped\n", NormalizeLines(result.Stdout));
    }

    [Fact]
    public void JScriptShellRunDispatchesThroughVirtualCatalog()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/data.txt", "alpha\nbeta\ngamma\n", overwrite: false);
        session.Vfs.CreateTextFile(
            "/work/run.js",
            """
            var shell = WScript.CreateObject("WScript.Shell");
            var code = shell.Run("grep beta /work/data.txt", 0, true);
            WScript.Echo("code=" + code);
            """,
            overwrite: false);

        var result = RunVirtual(session, "cscript", ["//nologo", "/work/run.js"], "cmd");

        Assert.Equal(0, result.Code);
        Assert.Equal("beta\ncode=0\n", NormalizeLines(result.Stdout));
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void JScriptShellEnvironmentUsesSharedEnvStore()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/env.js",
            """
            var shell = WScript.CreateObject("WScript.Shell");
            var env = shell.Environment("Process");
            env.Set("WSH_TEST", "ok");
            WScript.Echo(shell.ExpandEnvironmentStrings("%WSH_TEST%"));
            """,
            overwrite: false);

        var result = RunVirtual(session, "cscript", ["//nologo", "/work/env.js"], "cmd");

        Assert.Equal(0, result.Code);
        Assert.Equal("ok\n", NormalizeLines(result.Stdout));
        Assert.Equal("ok", session.Env.Get("WSH_TEST"));
    }

    [Fact]
    public void VBScriptCanEchoEnumerateArgumentsAndReturnExitCode()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/hello.vbs",
            """
            WScript.Echo "hello " & WScript.Arguments(0)
            For Each arg In WScript.Arguments
                WScript.StdOut.WriteLine arg
            Next
            WScript.Quit 5
            """,
            overwrite: false);

        var result = RunVirtual(session, "cscript", ["//nologo", "/work/hello.vbs", "value"], "cmd");

        Assert.Equal(5, result.Code);
        Assert.Equal("hello value\nvalue\n", NormalizeLines(result.Stdout));
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void ExplicitEngineCanRunNonstandardExtension()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/hello.txt", """WScript.Echo("txt script");""", overwrite: false);

        var result = RunVirtual(session, "cscript", ["//nologo", "//E:JScript", "/work/hello.txt"], "cmd");

        Assert.Equal(0, result.Code);
        Assert.Equal("txt script\n", NormalizeLines(result.Stdout));
    }

    [Fact]
    public void UnsupportedAutomationObjectFailsClearly()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/unsupported.js",
            """WScript.CreateObject("WbemScripting.SWbemLocator");""",
            overwrite: false);

        var result = RunVirtual(session, "cscript", ["//nologo", "/work/unsupported.js"], "cmd");

        Assert.Equal(1, result.Code);
        Assert.Contains("Unsupported automation object", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Code, string Stdout, string Stderr) RunVirtual(
        MultishellSession session,
        string commandName,
        IReadOnlyList<string> args,
        string callerShell,
        TextReader? input = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new ShellExecutionContext
        {
            Args = args,
            Input = input ?? TextReader.Null,
            Output = stdout,
            Error = stderr,
            Vfs = session.Vfs,
            Env = session.Env,
            Apps = session.Apps,
            Dispatcher = session.Dispatcher,
        };

        var resolution = session.Dispatcher.Resolve(commandName, ctx, callerShell);
        Assert.Equal(ResolutionKind.VirtualExecutable, resolution.Kind);
        var code = session.Dispatcher.ExecuteVirtualExecutable(
            resolution.VirtualExecutable!,
            resolution.VirtualExecutablePath!,
            commandName,
            args,
            ctx);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string NormalizeLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal static class VfsNodeTestExtensions
{
    public static CarbideShellCore.Vfs.VfsFile AsFile(this CarbideShellCore.Vfs.VfsNode node)
        => Assert.IsType<CarbideShellCore.Vfs.VfsFile>(node);
}
