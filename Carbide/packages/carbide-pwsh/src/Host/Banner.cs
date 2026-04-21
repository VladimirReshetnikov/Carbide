namespace CarbidePwsh.Host;

public static class Banner
{
    public static void Write(TextWriter output)
    {
        output.WriteLine("\x1b[36mcarbide-pwsh\x1b[0m — Phase 1 expression evaluator");
        output.WriteLine("\x1b[2ma PowerShell-flavored shell, compiled and run in the browser by Carbide\x1b[0m");
        output.WriteLine("\x1b[2mtype an expression and press Enter. 'exit' to quit.\x1b[0m");
        output.WriteLine();
    }
}
