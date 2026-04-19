namespace Monitoring;

public sealed class LogParser
{
    public LogEntry Parse(string line)
    {
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        var severity = parts[0] switch
        {
            "INFO" => Severity.Info,
            "WARN" => Severity.Warning,
            "ERR" => Severity.Error,
            _ => throw new InvalidOperationException($"Unknown severity: {parts[0]}")
        };

        return new LogEntry(severity, parts[1], int.Parse(parts[2]));
    }
}
