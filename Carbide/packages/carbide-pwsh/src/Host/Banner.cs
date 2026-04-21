namespace CarbidePwsh.Host;

public static class Banner
{
    public static void Write(TextWriter output)
    {
        output.WriteLine("\x1b[36mcarbide-pwsh\x1b[0m — Phase 3 (flow, functions, errors, classes, apps)");
        output.WriteLine("\x1b[2ma PowerShell-flavored shell, compiled and run in the browser by Carbide\x1b[0m");
        output.WriteLine("\x1b[2mtry: foreach (\\$x in 1..5) { \\$x * \\$x }  or  function f { param(\\$n) \\$n * 2 }; f 21\x1b[0m");
        output.WriteLine();
    }
}
