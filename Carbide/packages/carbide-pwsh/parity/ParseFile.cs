// Optional parse-only entry: `dotnet run --project parity -- parse path/to/script.ps1`.
// Prints the first parse error with 80-char context, or "Parsed OK" + statement count.
// Intended for quickly surfacing gaps when we aim carbide-pwsh at a real-world script.

using System;
using System.IO;
using System.Text;
using CarbidePwsh.Errors;

namespace CarbidePwsh.Parity;

internal static class ParseFile
{
    static ParseFile()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static int Run(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.Error.WriteLine("usage: parse <script.ps1>");
            return 2;
        }
        var path = argv[1];
        string src;
        try { src = ReadPowerShellSource(path); }
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

    private static string ReadPowerShellSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }
}
