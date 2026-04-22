// Optional parse-only entry: `dotnet run --project parity -- parse path/to/script.ps1`.
// Prints the first parse error with 80-char context, or "Parsed OK" + statement count.
// Intended for quickly surfacing gaps when we aim carbide-pwsh at a real-world script.

using System;
using System.IO;
using CarbidePwsh.Errors;

namespace CarbidePwsh.Parity;

internal static class ParseFile
{
    public static int Run(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.Error.WriteLine("usage: parse <script.ps1>");
            return 2;
        }
        var path = argv[1];
        string src;
        try { src = File.ReadAllText(path); }
        catch (Exception ex) { Console.Error.WriteLine($"could not read {path}: {ex.Message}"); return 2; }

        try
        {
            var script = CarbidePwsh.Parser.Parser.ParseString(src);
            Console.WriteLine($"Parsed OK: {script.Statements.Count} top-level statements, {src.Length} chars.");
            return 0;
        }
        catch (PwshParseException ex)
        {
            var loc = ex.Location;
            Console.WriteLine($"ParseException at line {loc.Line} col {loc.Column} (offset {loc.Offset}):");
            Console.WriteLine($"  {ex.Message}");
            int start = Math.Max(0, loc.Offset - 40);
            int end = Math.Min(src.Length, loc.Offset + 40);
            var context = src.Substring(start, end - start)
                             .Replace("\r", "\\r", StringComparison.Ordinal)
                             .Replace("\n", "\\n", StringComparison.Ordinal);
            Console.WriteLine($"  Context: {context}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
