namespace CarbidePwsh.Host;

public static class Banner
{
    public static void Write(TextWriter output)
    {
        output.WriteLine("\x1b[36mcarbide-pwsh\x1b[0m — Phase 2 (pipelines, VFS, cmdlets)");
        output.WriteLine("\x1b[2ma PowerShell-flavored shell, compiled and run in the browser by Carbide\x1b[0m");
        output.WriteLine("\x1b[2mtry: Get-ChildItem / | New-Item -ItemType Directory /work | cd /work. 'exit' to quit.\x1b[0m");
        output.WriteLine();
    }
}
