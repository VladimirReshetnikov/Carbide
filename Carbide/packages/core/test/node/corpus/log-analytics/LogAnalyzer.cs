using System.Globalization;

namespace Corp.Observability;

public static class LogAnalyzer
{
    public static IReadOnlyList<LogEntry> Parse(IEnumerable<string> lines)
    {
        return lines
            .Select((line, index) => ParseLine(line, index + 1))
            .ToArray();
    }

    public static IReadOnlyList<string> BuildSummary(IReadOnlyList<LogEntry> logs)
    {
        var total = logs.Count;
        var byService = logs
            .GroupBy(static entry => entry.Service)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var errorCount = group.Count(static entry => entry.StatusCode >= 500);
                var p95 = Percentile(group.Select(static entry => entry.DurationMs).OrderBy(static ms => ms).ToArray(), 0.95);
                return $"service={group.Key};count={group.Count()};errors={errorCount};p95={p95}";
            });

        var slowRoutes = logs
            .Where(static entry => entry.DurationMs >= 100)
            .GroupBy(static entry => entry.Route)
            .OrderByDescending(static group => group.Average(static entry => entry.DurationMs))
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
                $"route={group.Key};avg={Math.Round(group.Average(static entry => entry.DurationMs), 1):0.0};count={group.Count()}")
            .ToArray();

        return [
            $"total={total}",
            ..byService,
            ..slowRoutes,
        ];
    }

    private static LogEntry ParseLine(string line, int lineNumber)
    {
        var parts = line.Split('|');
        if (parts.Length != 5)
        {
            throw new InvalidOperationException($"line {lineNumber}: expected 5 fields, got {parts.Length}.");
        }

        return new LogEntry(
            Timestamp: DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture),
            Service: parts[1],
            Route: parts[2],
            StatusCode: int.Parse(parts[3], CultureInfo.InvariantCulture),
            DurationMs: int.Parse(parts[4], CultureInfo.InvariantCulture));
    }

    private static int Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var rawRank = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(rawRank);
        var upper = (int)Math.Ceiling(rawRank);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = rawRank - lower;
        return (int)Math.Round(sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight);
    }
}
