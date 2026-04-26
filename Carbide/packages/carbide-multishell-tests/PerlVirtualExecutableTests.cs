using CarbideShellCore.Dispatch;
using Xunit;

namespace CarbideMultishell.Tests;

public class PerlVirtualExecutableTests
{
    [Fact]
    public void PerlStubsAreInstalledAcrossLanguageRoots()
    {
        var session = new MultishellSession();

        Assert.True(session.Vfs.IsFile("/usr/bin/perl"));
        Assert.True(session.Vfs.IsFile("/usr/bin/perl.exe"));
        Assert.True(session.Vfs.IsFile("/bin/perl"));
        Assert.True(session.Vfs.IsFile("/bin/perl.exe"));
        Assert.True(session.Vfs.IsFile("/Program Files/Git/usr/bin/perl"));
        Assert.True(session.Vfs.IsFile("/Program Files/Git/usr/bin/perl.exe"));
    }

    [Fact]
    public void PerlResolvesFromEveryShellFlavor()
    {
        var session = new MultishellSession();

        Assert.Equal("/usr/bin/perl", Resolve(session, "perl", "bash"));
        Assert.Equal("/usr/bin/perl", Resolve(session, "perl", "cmd"));
        Assert.Equal("/usr/bin/perl", Resolve(session, "perl", "pwsh"));
        Assert.Equal("/usr/bin/perl.exe", Resolve(session, "perl.exe", "bash"));
    }

    [Fact]
    public void PerlVersionAndHelpWork()
    {
        var session = new MultishellSession();

        var (versionCode, versionOut, versionErr) = RunVirtual(session, "perl", ["-v"], "bash");
        var (helpCode, helpOut, helpErr) = RunVirtual(session, "perl", ["--help"], "bash");

        Assert.Equal(0, versionCode);
        Assert.Contains("Carbide", versionOut, StringComparison.Ordinal);
        Assert.Equal("", versionErr);
        Assert.Equal(0, helpCode);
        Assert.Contains("-de 0", helpOut, StringComparison.Ordinal);
        Assert.Equal("", helpErr);
    }

    [Fact]
    public void PerlCommandStringCanPrintAndReadArgv()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = RunVirtual(
            session,
            "perl",
            ["-e", "print $ARGV[0], qq(\\n); print 1 + 2 * 3, qq(\\n)", "value"],
            "bash");

        Assert.Equal(0, code);
        Assert.Equal("value\n7\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PerlScriptSupportsBlocksSubroutinesArgvAndEnvironment()
    {
        var session = new MultishellSession();
        session.Env.Set("MODE", "test");
        session.Vfs.CreateTextFile(
            "/work/tool.pl",
            """
            use strict;
            use warnings;

            sub double { return $_[0] * 2; }

            my $total = 0;
            foreach my $item (@ARGV) {
                $total += double($item);
            }

            if ($total == 12) {
                print $ENV{MODE}, qq(:ok\n);
            } else {
                print qq(bad\n);
            }
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "perl", ["/work/tool.pl", "1", "2", "3"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("test:ok\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PerlLineFiltersSupportNePeAutosplitAndRegex()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile("/work/data.txt", "alpha 1\nbeta 2\nfood 3\n", overwrite: false);

        var (filterCode, filterOut, filterErr) = RunVirtual(session, "perl", ["-ne", "print if /beta/", "/work/data.txt"], "bash");
        var (replaceCode, replaceOut, replaceErr) = RunVirtual(session, "perl", ["-pe", "s/foo/bar/g", "/work/data.txt"], "bash");
        var (splitCode, splitOut, splitErr) = RunVirtual(session, "perl", ["-ane", "print $F[0], qq(\\n)", "/work/data.txt"], "bash");

        Assert.Equal(0, filterCode);
        Assert.Equal("beta 2\n", Normalize(filterOut));
        Assert.Equal("", filterErr);
        Assert.Equal(0, replaceCode);
        Assert.Equal("alpha 1\nbeta 2\nbard 3\n", Normalize(replaceOut));
        Assert.Equal("", replaceErr);
        Assert.Equal(0, splitCode);
        Assert.Equal("alpha\nbeta\nfood\n", Normalize(splitOut));
        Assert.Equal("", splitErr);
    }

    [Fact]
    public void PerlCanUseJsonModulesAndVfsOpen()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/data.pl",
            """
            use JSON::PP;
            use File::Basename;
            use File::Spec;

            my $payload = decode_json('{"name":"beta","count":2}');
            my $path = File::Spec::catfile("/work", basename("out.txt"));
            open my $fh, ">", $path;
            print $fh uc($payload->{name}) . ":" . $payload->{count};
            close $fh;
            open my $in, "<", $path;
            print <$in>, qq(\n);
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "perl", ["/work/data.pl"], "bash");

        Assert.True(code == 0, stderr);
        Assert.Equal("BETA:2\n", Normalize(stdout));
        Assert.Equal("", stderr);
        Assert.True(session.Vfs.IsFile("/work/out.txt"));
    }

    [Fact]
    public void PerlGetoptLongParsesOptionsAndLeavesPositionals()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/cli.pl",
            """
            use Getopt::Long;

            my $count = 1;
            my $verbose = 0;
            GetOptions("count=i" => \$count, "verbose" => \$verbose);
            print $count . ":" . $verbose . ":" . join(",", @ARGV) . qq(\n);
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "perl", ["/work/cli.pl", "--count", "3", "--verbose", "demo"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("3:1:demo\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PerlSystemDispatchesThroughCarbideExecutableCatalog()
    {
        var session = new MultishellSession();
        session.Vfs.CreateTextFile(
            "/work/system.pl",
            """
            system("python", "-c", "print('child')");
            print "code=" . $? . qq(\n);
            """,
            overwrite: false);

        var (code, stdout, stderr) = RunVirtual(session, "perl", ["/work/system.pl"], "bash");

        Assert.Equal(0, code);
        Assert.Equal("child\ncode=0\n", Normalize(stdout));
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PerlDebuggerPseudoReplSupportsDeZero()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = RunVirtual(
            session,
            "perl",
            ["-de", "0"],
            "bash",
            input: "$x = 41;\np $x + 1\nq\n");

        Assert.Equal(0, code);
        Assert.Contains("CarbidePerl", stdout, StringComparison.Ordinal);
        Assert.Contains("DB<1>", stdout, StringComparison.Ordinal);
        Assert.Contains("42", stdout, StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task PerlDebuggerPseudoReplAsyncSystemCanDispatchDotnetFacade()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = await RunVirtualAsync(
            session,
            "perl",
            ["-de", "0"],
            "pwsh",
            input: "system(\"dotnet\", \"--version\")\np $?\nq\n");

        Assert.Equal(0, code);
        Assert.Contains("CarbidePerl", stdout, StringComparison.Ordinal);
        Assert.Contains("Carbide dotnet facade 0.1", stdout, StringComparison.Ordinal);
        Assert.Contains("DB<2> 0", stdout, StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task PerlAsyncSystemCanDispatchDotnetFacadeFromProgramText()
    {
        var session = new MultishellSession();

        var (code, stdout, stderr) = await RunVirtualAsync(
            session,
            "perl",
            ["-e", "system(\"dotnet\", \"--version\"); print \"code=\" . $? . qq(\\n);"],
            "pwsh");

        Assert.Equal(0, code);
        Assert.Contains("Carbide dotnet facade 0.1", stdout, StringComparison.Ordinal);
        Assert.Contains("code=0", Normalize(stdout), StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void PerlUnsupportedSwitchFailsClearly()
    {
        var session = new MultishellSession();

        var (code, _, stderr) = RunVirtual(session, "perl", ["-T"], "bash");

        Assert.Equal(2, code);
        Assert.Contains("unsupported switch", stderr, StringComparison.Ordinal);
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

    private static async Task<(int Code, string Stdout, string Stderr)> RunVirtualAsync(
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
        var code = await session.Dispatcher.ExecuteVirtualExecutableAsync(
            resolution.VirtualExecutable!,
            resolution.VirtualExecutablePath!,
            commandName,
            args,
            ctx).ConfigureAwait(true);
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
