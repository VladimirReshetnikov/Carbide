using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LogAnalyzer;

public enum Level
{
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, string Service, Level Level, string Message);

public static class Parser
{
    private static readonly Regex LinePattern =
        new(@"^(?<ts>\S+)\s+(?<svc>\w+)\s+(?<lvl>INFO|WARN|ERROR)\s+(?<msg>.+)$", RegexOptions.Compiled);

    public static LogEntry Parse(string line)
    {
        var match = LinePattern.Match(line);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Malformed log line: {line}");
        }

        var timestamp = DateTimeOffset.Parse(match.Groups["ts"].Value);
        var service = match.Groups["svc"].Value;
        var level = match.Groups["lvl"].Value switch
        {
            "INFO" => Level.Info,
            "WARN" => Level.Warn,
            _ => Level.Error,
        };

        return new LogEntry(timestamp, service, level, match.Groups["msg"].Value);
    }

    public static IReadOnlyList<LogEntry> ParseAll(IEnumerable<string> lines) => lines.Select(Parse).ToArray();
}
