namespace CarbideBash.Host;

public static class Banner
{
    public static void Write(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("Carbide Bash — Phase 1 subset.");
        w.WriteLine();
    }
}
