namespace Monitoring;

public enum Severity
{
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(Severity Severity, string Path, int DurationMs);
public sealed record LogReport(int ErrorCount, int WarningCount, string SlowestPath, int SlowestDurationMs, int P95DurationMs);
