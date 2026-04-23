using System.Text;
using System.Text.Json;
using CarbidePwsh.Errors;

namespace CarbidePwsh.Parity;

internal static class BatchParseAudit
{
    public static int Run(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.Error.WriteLine("usage: audit <file-list.txt> [--write-json <results.json>]");
            return 2;
        }

        var listPath = argv[1];
        string? writeJsonPath = null;
        for (int i = 2; i < argv.Length; i++)
        {
            if ((argv[i] == "--write-json" || argv[i] == "-w") && i + 1 < argv.Length)
            {
                writeJsonPath = argv[i + 1];
                i++;
            }
        }

        if (!File.Exists(listPath))
        {
            Console.Error.WriteLine($"file list not found: {listPath}");
            return 2;
        }

        var files = File.ReadAllLines(listPath)
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .ToArray();

        var results = new List<BatchParseResult>(files.Length);
        int parsedOk = 0;
        int failed = 0;

        foreach (var path in files)
        {
            var result = ParseOne(path);
            results.Add(result);
            if (result.CarbideOk) parsedOk++;
            else failed++;
        }

        var payload = new BatchParseAuditPayload(
            Files: results,
            Summary: new BatchParseSummary(
                TotalFiles: results.Count,
                ParsedOk: parsedOk,
                Failed: failed));

        if (writeJsonPath is not null)
        {
            var dir = Path.GetDirectoryName(writeJsonPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(writeJsonPath, json);
        }

        Console.WriteLine($"Audited {results.Count} files. Parsed OK: {parsedOk}. Failed: {failed}.");
        return 0;
    }

    private static BatchParseResult ParseOne(string path)
    {
        string src;
        try
        {
            src = ParseFile.ReadPowerShellSource(path);
        }
        catch (Exception ex)
        {
            return new BatchParseResult(
                Path: path,
                CarbideOk: false,
                StatementCount: null,
                ErrorKind: "ReadError",
                ErrorMessage: ex.Message,
                Line: null,
                Column: null,
                Offset: null,
                Context: null);
        }

        try
        {
            var script = CarbidePwsh.Parser.Parser.ParseString(src);
            return new BatchParseResult(
                Path: path,
                CarbideOk: true,
                StatementCount: script.Statements.Count,
                ErrorKind: null,
                ErrorMessage: null,
                Line: null,
                Column: null,
                Offset: null,
                Context: null);
        }
        catch (PwshParseException ex)
        {
            var loc = ex.Location;
            return new BatchParseResult(
                Path: path,
                CarbideOk: false,
                StatementCount: null,
                ErrorKind: nameof(PwshParseException),
                ErrorMessage: ex.Message,
                Line: loc.Line,
                Column: loc.Column,
                Offset: loc.Offset,
                Context: BuildContext(src, loc.Offset));
        }
        catch (Exception ex)
        {
            return new BatchParseResult(
                Path: path,
                CarbideOk: false,
                StatementCount: null,
                ErrorKind: ex.GetType().Name,
                ErrorMessage: ex.Message,
                Line: null,
                Column: null,
                Offset: null,
                Context: null);
        }
    }

    private static string BuildContext(string src, int offset)
    {
        int start = Math.Max(0, offset - 40);
        int end = Math.Min(src.Length, offset + 40);
        return src.Substring(start, end - start)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}

internal sealed record BatchParseAuditPayload(
    IReadOnlyList<BatchParseResult> Files,
    BatchParseSummary Summary);

internal sealed record BatchParseSummary(
    int TotalFiles,
    int ParsedOk,
    int Failed);

internal sealed record BatchParseResult(
    string Path,
    bool CarbideOk,
    int? StatementCount,
    string? ErrorKind,
    string? ErrorMessage,
    int? Line,
    int? Column,
    int? Offset,
    string? Context);
