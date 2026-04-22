using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;

namespace CarbideShellCore.Apps;

/// <summary>
/// Helper that creates zero-byte stub "executables" in the VFS for a registered
/// <see cref="IShellKernel"/> and wires them through the dispatcher's stub-path lookup.
/// The stubs let <c>ls /usr/bin</c>, <c>dir</c>, and <c>Get-ChildItem</c> list the
/// registered shells as files that the user can invoke by path
/// (e.g. <c>/usr/bin/cmd.exe</c>).
/// </summary>
public static class StubInstaller
{
    /// <summary>
    /// Drop one stub file per supplied path, each claimed by <paramref name="kernel"/>.
    /// If a file already exists the stub is not overwritten, but the dispatcher's
    /// stub-path registry is updated so the existing file resolves to the kernel.
    /// </summary>
    public static void Install(
        VirtualFileSystem vfs,
        ShellDispatcher dispatcher,
        IShellKernel kernel,
        IReadOnlyCollection<string> stubPaths)
    {
        foreach (var path in stubPaths)
        {
            var abs = vfs.Normalize(path);
            if (vfs.Resolve(abs) is not VfsFile)
            {
                // `CreateTextFile` also materializes the parent directory chain.
                var banner = $"#!carbide:{kernel.Name}\n";
                vfs.CreateTextFile(abs, banner, overwrite: false);
            }
            dispatcher.RegisterStubPath(abs, kernel);
        }
    }
}
