using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;

#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
namespace CarbidePwsh.SharedMultishell;
#else
namespace CarbideMultishell;
#endif

internal static class VirtualExecutableCatalog
{
    private const string HandlerKey = "carbide-multishell";
    private static readonly string[] PosixRoots = ["/usr/bin", "/bin", "/Program Files/Git/usr/bin"];
    private static readonly string[] WindowsRoots = ["/Windows/System32"];
    private static readonly string[] SdkPosixRoots = ["/usr/bin", "/bin", "/Program Files/Git/usr/bin"];

    public static void Install(VirtualFileSystem vfs, ShellDispatcher dispatcher)
    {
        dispatcher.RegisterVirtualExecutableHandler(HandlerKey, new MultishellVirtualExecutableHandler());
        foreach (var definition in BuildDefinitions())
            StubInstaller.Install(vfs, dispatcher, definition);
    }

    private static IEnumerable<VirtualExecutableDefinition> BuildDefinitions()
    {
        yield return Posix("gnu-awk", "awk.exe", "gawk.exe");
        yield return Posix("gnu-basename", "basename.exe");
        yield return Posix("gnu-bunzip2", "bunzip2.exe");
        yield return Posix("gnu-bzip2", "bzip2.exe");
        yield return Posix("gnu-cat", "cat.exe");
        yield return Posix("gnu-cmp", "cmp.exe");
        yield return Posix("gnu-comm", "comm.exe");
        yield return Posix("gnu-cp", "cp.exe");
        yield return Posix("gnu-cut", "cut.exe");
        yield return Posix("gnu-date", "date.exe");
        yield return Posix("gnu-diff", "diff.exe");
        yield return Posix("gnu-diff3", "diff3.exe");
        yield return Posix("gnu-dirname", "dirname.exe");
        yield return Posix("gnu-grep", "grep.exe", "egrep", "fgrep");
        yield return Posix("gnu-env", "env.exe");
        yield return Posix("gnu-find", "find.exe");
        yield return Posix("gnu-gunzip", "gunzip");
        yield return Posix("gnu-gzip", "gzip.exe");
        yield return Posix("gnu-head", "head.exe");
        yield return Posix("gnu-hostname", "hostname.exe");
        yield return Posix("gnu-ls", "ls.exe");
        yield return Posix("gnu-mkdir", "mkdir.exe");
        yield return Posix("gnu-mktemp", "mktemp.exe");
        yield return Posix("gnu-mv", "mv.exe");
        yield return Posix("gnu-paste", "paste.exe");
        yield return Posix("gnu-patch", "patch.exe");
        yield return Posix("gnu-printenv", "printenv.exe");
        yield return Posix("gnu-printf", "printf.exe");
        yield return Posix("gnu-pwd", "pwd.exe");
        yield return Posix("gnu-readlink", "readlink.exe");
        yield return Posix("gnu-realpath", "realpath.exe");
        yield return Posix("gnu-rm", "rm.exe");
        yield return Posix("gnu-rmdir", "rmdir.exe");
        yield return Posix("gnu-sed", "sed.exe");
        yield return Posix("gnu-seq", "seq.exe");
        yield return Posix("gnu-sleep", "sleep.exe");
        yield return Posix("gnu-sort", "sort.exe");
        yield return Posix("gnu-tail", "tail.exe");
        yield return Posix("gnu-tar", "tar.exe");
        yield return Posix("gnu-tee", "tee.exe");
        yield return Posix("gnu-test", "test.exe");
        yield return Posix("gnu-touch", "touch.exe");
        yield return Posix("gnu-tr", "tr.exe");
        yield return Posix("gnu-uname", "uname.exe");
        yield return Posix("gnu-uniq", "uniq.exe");
        yield return Posix("gnu-unzip", "unzip.exe");
        yield return Posix("gnu-wc", "wc.exe");
        yield return Posix("gnu-which", "which.exe");
        yield return Posix("gnu-whoami", "whoami.exe");
        yield return Posix("gnu-xargs", "xargs.exe");
        yield return Posix("gnu-yes", "yes.exe");

        yield return Language("python", "python", "python.exe", "python3", "python3.exe");
        yield return Language("perl", "perl", "perl.exe");
        yield return Sdk("dotnet", "dotnet", "dotnet.exe");

        yield return Windows("windows-cscript", "cscript.exe");
        yield return Windows("windows-fc", "fc.exe");
        yield return Windows("windows-find", "find.exe");
        yield return Windows("windows-findstr", "findstr.exe");
        yield return Windows("windows-more", "more.com");
        yield return Windows("windows-robocopy", "robocopy.exe");
        yield return Windows("windows-sort", "sort.exe");
        yield return Windows("windows-tar", "tar.exe");
        yield return Windows("windows-timeout", "timeout.exe");
        yield return Windows("windows-tree", "tree.com");
        yield return Windows("windows-where", "where.exe");
        yield return Windows("windows-whoami", "whoami.exe");
        yield return Windows("windows-xcopy", "xcopy.exe");
    }

    private static VirtualExecutableDefinition Posix(string commandId, params string[] basenames)
        => new(
            commandId,
            VirtualExecutablePersonality.Gnu,
            BuildPaths(PosixRoots, basenames),
            basenames,
            HandlerKey);

    private static VirtualExecutableDefinition Windows(string commandId, params string[] basenames)
        => new(
            commandId,
            VirtualExecutablePersonality.Windows,
            BuildPaths(WindowsRoots, basenames),
            basenames,
            HandlerKey);

    private static VirtualExecutableDefinition Language(string commandId, params string[] basenames)
        => new(
            commandId,
            VirtualExecutablePersonality.Language,
            BuildPaths(PosixRoots, basenames),
            basenames,
            HandlerKey);

    private static VirtualExecutableDefinition Sdk(string commandId, params string[] basenames)
        => new(
            commandId,
            VirtualExecutablePersonality.Sdk,
            BuildPaths(SdkPosixRoots, basenames)
                .Concat(["/Program Files/dotnet/dotnet.exe"])
                .ToArray(),
            basenames,
            HandlerKey);

    private static IReadOnlyList<string> BuildPaths(IEnumerable<string> roots, IEnumerable<string> basenames)
        => roots.SelectMany(root => basenames.Select(name => VfsPath.Join(root, name))).ToArray();
}
