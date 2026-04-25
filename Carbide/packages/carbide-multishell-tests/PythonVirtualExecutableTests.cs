using CarbideShellCore.Dispatch;
using Xunit;

namespace CarbideMultishell.Tests;

public class PythonVirtualExecutableTests
{
    [Fact]
    public void PythonStubsAreInstalledAcrossLanguageRoots()
    {
        var session = new MultishellSession();

        Assert.True(session.Vfs.IsFile("/usr/bin/python"));
        Assert.True(session.Vfs.IsFile("/usr/bin/python.exe"));
        Assert.True(session.Vfs.IsFile("/usr/bin/python3"));
        Assert.True(session.Vfs.IsFile("/usr/bin/python3.exe"));
        Assert.True(session.Vfs.IsFile("/bin/python.exe"));
        Assert.True(session.Vfs.IsFile("/Program Files/Git/usr/bin/python3.exe"));
    }

    [Fact]
    public void PythonResolvesFromEveryShellFlavor()
    {
        var session = new MultishellSession();

        Assert.Equal("/usr/bin/python", Resolve(session, "python", "bash"));
        Assert.Equal("/usr/bin/python", Resolve(session, "python", "cmd"));
        Assert.Equal("/usr/bin/python", Resolve(session, "python", "pwsh"));
        Assert.Equal("/usr/bin/python3", Resolve(session, "python3", "bash"));
    }

    [Fact]
    public void PythonVersionAndHelpWork()
    {
        var session = new MultishellSession();

        var (versionCode, versionOut, versionErr) = RunVirtual(session, "python", ["-V"], "bash");
        var (helpCode, helpOut, helpErr) = RunVirtual(session, "python", ["--help-env"], "bash");

        Assert.Equal(0, versionCode);
        Assert.Contains("Carbide subset", versionOut, StringComparison.Ordinal);
        Assert.Equal("", versionErr);
        Assert.Equal(0, helpCode);
        Assert.Contains("PYTHONPATH", helpOut, StringComparison.Ordinal);
        Assert.Equal("", helpErr);
    }

    [Fact]
    public void PythonCommandStringCanPrintAndReadArgv()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = RunVirtual(
            session,
            "python",
            ["-c", "import sys; print(sys.argv[1]); print(1 + 2 * 3)", "value"],
            "bash");

        Assert.Equal(0, code);
        Assert.Equal("value\n7\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonCanExecuteStdinSource()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = RunVirtual(
            session,
            "python",
            ["-", "from-argv"],
            "bash",
            input: "import sys\nprint(sys.argv[0])\nprint(sys.argv[1])\n");

        Assert.Equal(0, code);
        Assert.Equal("-\nfrom-argv\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonScriptSupportsBlocksFunctionsAndLoops()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/tool.py",
            """
            def double(x):
                return x * 2

            total = 0
            for item in range(4):
                total = total + double(item)
            if total == 12:
                print("ok")
            else:
                print("bad")
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "python", ["/work/tool.py"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("ok\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonCanUseJsonRegexPathlibAndVfsOpen()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/data.py",
            """
            import json
            import re
            from pathlib import Path

            payload = json.loads('{"name":"beta","count":2}')
            text = re.sub("beta", "BETA", payload["name"])
            Path("/work/out.txt").write_text(text + ":" + str(payload["count"]))
            print(open("/work/out.txt").read())
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "python", ["/work/data.py"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("BETA:2\n", Normalize(stdout));
        Assert.Equal("", stderr);
        Assert.True(session.Vfs.IsFile("/work/out.txt"));
    }

    [Fact]
    public void PythonImportsVfsModulesFromPythonPathUnlessEnvironmentIsIgnored()
    {
        var session = new MultishellSession();
        session.Vfs.CreateDirectory("/work/lib");
        session.Vfs.CreateTextFile("/work/lib/helper.py", "def value():\n    return 5\n", overwrite: false);
        session.Vfs.CreateTextFile("/work/use_helper.py", "import helper\nprint(helper.value())\n", overwrite: false);
        session.Env.Set("PYTHONPATH", "/work/lib");

        var (code, stdout, stderr) = RunVirtual(session, "python", ["/work/use_helper.py"], "bash");
        var (isolatedCode, _, isolatedStderr) = RunVirtual(session, "python", ["-E", "/work/use_helper.py"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("5\n", Normalize(stdout));
        Assert.Equal("", stderr);
        Assert.Equal(1, isolatedCode);
        Assert.Contains("ModuleNotFoundError", isolatedStderr, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonArgparseParsesFlagsPositionalsAndTypes()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/cli.py",
            """
            import argparse

            parser = argparse.ArgumentParser(description="demo")
            parser.add_argument("name")
            parser.add_argument("-c", "--count", type=int, default=1)
            parser.add_argument("--verbose", action="store_true")
            args = parser.parse_args()
            print(args.name + ":" + str(args.count) + ":" + str(args.verbose))
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "python", ["/work/cli.py", "demo", "--count", "3", "--verbose"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("demo:3:True\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonArgparseCanReturnUnknownArguments()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/known.py",
            """
            import argparse

            parser = argparse.ArgumentParser()
            parser.add_argument("--tag", action="append")
            parsed = parser.parse_known_args(["--tag", "one", "--other", "two"])
            args = parsed[0]
            rest = parsed[1]
            print(args.tag[0] + ":" + rest[0] + ":" + rest[1])
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "python", ["/work/known.py"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("one:--other:two\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonSubprocessDispatchesThroughCarbideExecutableCatalog()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/subprocess.py",
            """
            import subprocess

            result = subprocess.run(["python", "-c", "print('child')"], capture_output=True, text=True)
            print(str(result.returncode) + ":" + result.stdout.strip())
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "python", ["/work/subprocess.py"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("0:child\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonModuleJsonToolFormatsJson()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/data.json", "{\"b\":2,\"a\":1}", overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "python", ["-m", "json.tool", "/work/data.json"], "bash");

        Assert.Equal(0, code);
        Assert.Contains("\"a\": 1", stdout, StringComparison.Ordinal);
        Assert.Contains("\"b\": 2", stdout, StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonInteractiveReplPersistsStateAndHonorsQuietInspect()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = RunVirtual(
            session,
            "python",
            ["-q", "-i", "-c", "x = 41"],
            "bash",
            input: "x + 1\nexit()\n");

        Assert.Equal(0, code);
        Assert.DoesNotContain("CarbidePython", stdout, StringComparison.Ordinal);
        Assert.Contains(">>> 42", stdout.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonStartupRunsForBareInteractiveMode()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/home/user/startup.py", "answer = 42\n", overwrite: false);
        session.Env.Set("PYTHONSTARTUP", "/home/user/startup.py");

        var (code, stdout, stderr) = RunVirtual(session, "python", [], "bash", input: "answer\nexit()\n");

        Assert.Equal(0, code);
        Assert.Contains("CarbidePython", stdout, StringComparison.Ordinal);
        Assert.Contains(">>> 42", stdout.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PythonUnsupportedOptionFailsClearly()
    {
        var session = new MultishellSession();

        var (code, _, stderr) = RunVirtual(session, "python", ["-X", "importtime"], "bash");

        Assert.Equal(2, code);
        Assert.Contains("unsupported option", stderr, StringComparison.Ordinal);
    }

    private static string Resolve(MultishellSession session, string command, string shell)
    {
        var resolution = session.Dispatcher.Resolve(command, BuildContext(session, TextReader.Null, TextWriter.Null, TextWriter.Null), shell);
        Assert.Equal(ResolutionKind.VirtualExecutable, resolution.Kind);
        return resolution.VirtualExecutablePath!;
    }

    private static (int Code, string Stdout, string Stderr) RunVirtual(
        MultishellSession session,
        string commandName,
        IReadOnlyList<string> args,
        string callerShell,
        string input = "")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = BuildContext(session, new StringReader(input), stdout, stderr);
        ctx = ctx.With(args: args);

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

    private static ShellExecutionContext BuildContext(
        MultishellSession session,
        TextReader input,
        TextWriter output,
        TextWriter error)
        => new()
        {
            Args = Array.Empty<string>(),
            Input = input,
            Output = output,
            Error = error,
            Vfs = session.Vfs,
            Env = session.Env,
            Apps = session.Apps,
            Dispatcher = session.Dispatcher,
        };

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
