using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;
using Xunit;

namespace CarbideMultishell.Tests;

public class VirtualExecutableTests
{
    [Fact]
    public void RepresentativeStubCatalogIsInstalledAcrossRoots()
    {
        var session = new MultishellSession();
        Assert.True(session.Vfs.IsFile("/usr/bin/grep.exe"));
        Assert.True(session.Vfs.IsFile("/bin/grep.exe"));
        Assert.True(session.Vfs.IsFile("/Program Files/Git/usr/bin/grep.exe"));
        Assert.True(session.Vfs.IsFile("/usr/bin/awk.exe"));
        Assert.True(session.Vfs.IsFile("/usr/bin/sed.exe"));
        Assert.True(session.Vfs.IsFile("/Windows/System32/findstr.exe"));
        Assert.True(session.Vfs.IsFile("/Windows/System32/robocopy.exe"));
    }

    [Fact]
    public void CollidingNamesResolveByCallingShellDefaults()
    {
        var session = new MultishellSession();
        var ctx = BuildContext(session);

        var bashFind = session.Dispatcher.Resolve("find", ctx, "bash");
        var cmdFind = session.Dispatcher.Resolve("find", ctx, "cmd");
        var bashSort = session.Dispatcher.Resolve("sort", ctx, "bash");
        var cmdSort = session.Dispatcher.Resolve("sort", ctx, "cmd");
        var bashTar = session.Dispatcher.Resolve("tar", ctx, "bash");
        var cmdTar = session.Dispatcher.Resolve("tar", ctx, "cmd");

        Assert.Equal(ResolutionKind.VirtualExecutable, bashFind.Kind);
        Assert.Equal("/usr/bin/find.exe", bashFind.VirtualExecutablePath);
        Assert.Equal(ResolutionKind.VirtualExecutable, cmdFind.Kind);
        Assert.Equal("/Windows/System32/find.exe", cmdFind.VirtualExecutablePath);
        Assert.Equal("/usr/bin/sort.exe", bashSort.VirtualExecutablePath);
        Assert.Equal("/Windows/System32/sort.exe", cmdSort.VirtualExecutablePath);
        Assert.Equal("/usr/bin/tar.exe", bashTar.VirtualExecutablePath);
        Assert.Equal("/Windows/System32/tar.exe", cmdTar.VirtualExecutablePath);
    }

    [Fact]
    public void PathOverrideCanFlipCollisionResolution()
    {
        var session = new MultishellSession();
        session.Env.Set("PATH", "/Windows/System32;/usr/bin;/bin");
        var ctx = BuildContext(session);
        var bashFind = session.Dispatcher.Resolve("find", ctx, "bash");
        Assert.Equal("/Windows/System32/find.exe", bashFind.VirtualExecutablePath);
    }

    [Fact]
    public void CmdCanRunPosixGrepFromVirtualCatalog()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/data.txt", "alpha\nbeta\ngamma\n", overwrite: false);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = session.Cmd.Submit("grep -n beta /work/data.txt\n", new StringReader(""), stdout, stderr);

        Assert.Equal(0, code);
        Assert.Contains("2:beta", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void CmdFindStrSearchesFilesRecursively()
    {
        var session = new MultishellSession();
        session.Vfs.CreateDirectory("/work/findstr");
        session.Vfs.CreateTextFile("/work/findstr/a.txt", "alpha\n", overwrite: false);
        session.Vfs.CreateTextFile("/work/findstr/b.txt", "beta\n", overwrite: false);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = session.Cmd.Submit("findstr /S /N beta C:\\work\\findstr\n", new StringReader(""), stdout, stderr);

        Assert.Equal(0, code);
        Assert.Contains("b.txt:1:beta", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void WhereReportsVirtualToolPaths()
    {
        var session = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = session.Cmd.Submit("where grep\n", new StringReader(""), stdout, stderr);

        Assert.Equal(0, code);
        Assert.Contains("grep.exe", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("usr\\bin", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void RobocopyCopiesDirectoryTreeInsideVfs()
    {
        var session = new MultishellSession();
        session.Vfs.CreateDirectory("/work/src/sub");
        session.Vfs.CreateTextFile("/work/src/root.txt", "root\n", overwrite: false);
        session.Vfs.CreateTextFile("/work/src/sub/leaf.txt", "leaf\n", overwrite: false);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = session.Cmd.Submit("robocopy C:\\work\\src C:\\work\\dst /E\n", new StringReader(""), stdout, stderr);

        Assert.Equal(0, code);
        Assert.True(session.Vfs.IsFile("/work/dst/root.txt"));
        Assert.True(session.Vfs.IsFile("/work/dst/sub/leaf.txt"));
    }

    [Fact]
    public void SedSubstitutionWorksThroughDispatcher()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/data.txt", "alpha\nbeta\ngamma\n", overwrite: false);

        var (code, stdout, _) = RunVirtual(session, "sed", ["-e", "s/beta/BETA/", "/work/data.txt"], "cmd");

        Assert.Equal(0, code);
        Assert.Contains("BETA", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AwkCanPrintFirstField()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/awk.txt", "alpha beta\ngamma delta\n", overwrite: false);

        var (code, stdout, _) = RunVirtual(session, "awk", ["{print $1}", "/work/awk.txt"], "cmd");

        Assert.Equal(0, code);
        Assert.Equal("alpha\ngamma\n", stdout.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Bzip2AndBunzip2RoundTripFiles()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/archive.txt", "alpha\nbeta\ngamma\n", overwrite: false);

        var (compressCode, _, compressErr) = RunVirtual(session, "bzip2", ["/work/archive.txt"], "bash");
        session.Vfs.Delete("/work/archive.txt", recursive: false, force: false);
        var (decompressCode, _, decompressErr) = RunVirtual(session, "bunzip2", ["/work/archive.txt.bz2"], "bash");

        Assert.Equal(0, compressCode);
        Assert.Equal("", compressErr);
        Assert.Equal(0, decompressCode);
        Assert.Equal("", decompressErr);
        Assert.True(session.Vfs.IsFile("/work/archive.txt.bz2"));
        Assert.Equal(
            "alpha\nbeta\ngamma\n",
            ((VfsFile)session.Vfs.Resolve("/work/archive.txt")!).ReadText().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void PwshPipelineBridgesObjectsIntoVirtualExecutableText()
    {
        var session = new MultishellSession();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            session.Pwsh.SubmitAndRender("@('alpha','beta','gamma') | grep beta", stdout);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.Contains("beta", stdout.ToString(), StringComparison.Ordinal);
    }

    private static ShellExecutionContext BuildContext(MultishellSession session)
        => new()
        {
            Args = Array.Empty<string>(),
            Input = TextReader.Null,
            Output = TextWriter.Null,
            Error = TextWriter.Null,
            Vfs = session.Vfs,
            Env = session.Env,
            Apps = session.Apps,
            Dispatcher = session.Dispatcher,
        };

    private static (int Code, string Stdout, string Stderr) RunVirtual(
        MultishellSession session,
        string commandName,
        IReadOnlyList<string> args,
        string callerShell)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new ShellExecutionContext
        {
            Args = args,
            Input = TextReader.Null,
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
}
