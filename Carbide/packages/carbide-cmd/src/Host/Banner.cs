namespace CarbideCmd.Host;

public static class Banner
{
    public static void Write(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("Carbide Cmd [Version 1.0 — Phase 1 subset]");
        w.WriteLine("(c) Carbide. Type HELP for a list of known commands.");
        w.WriteLine();
    }
}
