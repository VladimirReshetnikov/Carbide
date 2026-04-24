namespace CarbidePwsh.Host;

public static class Banner
{
    public static void Write(TextWriter output)
    {
        output.WriteLine("\x1b[36mcarbide-pwsh\x1b[0m — Phase 3 pwsh plus shared cmd/bash/tool session");
        output.WriteLine("\x1b[2ma PowerShell-flavored shell, compiled and run in the browser by Carbide\x1b[0m");
        output.WriteLine("\x1b[2mtype `cmd` or `bash` to enter nested shells; all shells share one VFS, env, and executable catalog\x1b[0m");
        output.WriteLine("\x1b[2mtry: foreach (\\$x in 1..5) { \\$x * \\$x }  or  grep beta /work/data.txt  or  cmd\x1b[0m");
        output.WriteLine();
    }
}
