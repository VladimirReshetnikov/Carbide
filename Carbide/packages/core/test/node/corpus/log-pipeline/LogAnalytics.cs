namespace Monitoring;

public static class LogAnalytics
{
    public static LogReport BuildReport(IReadOnlyList<LogEntry> entries)
    {
        var orderedDurations = entries.Select(e => e.DurationMs).OrderBy(x => x).ToArray();
        var p95Index = (int)Math.Ceiling(orderedDurations.Length * 0.95) - 1;
        var slowest = entries.OrderByDescending(e => e.DurationMs).First();

        return new LogReport(
            ErrorCount: entries.Count(e => e.Severity == Severity.Error),
            WarningCount: entries.Count(e => e.Severity == Severity.Warning),
            SlowestPath: slowest.Path,
            SlowestDurationMs: slowest.DurationMs,
            P95DurationMs: orderedDurations[p95Index]);
    }
}
