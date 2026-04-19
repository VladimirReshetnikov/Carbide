namespace Corp.Observability;

public sealed record LogEntry(DateTimeOffset Timestamp, string Service, string Route, int StatusCode, int DurationMs);
