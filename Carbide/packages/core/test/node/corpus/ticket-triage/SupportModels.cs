namespace Support;

public enum Severity
{
    Low,
    Medium,
    High,
    Critical,
}

public sealed record Ticket(
    string Queue,
    Severity Severity,
    DateOnly Opened,
    DateOnly Due,
    bool HasVipCustomer,
    bool ProductionDown);

public sealed record QueueSummary(string Queue, string Level, int Count, int Breached);

public static class TicketSeed
{
    public static IReadOnlyList<Ticket> Load() =>
    [
        new("Billing", Severity.Medium, DateOnly.Parse("2026-03-20"), DateOnly.Parse("2026-03-29"), false, false),
        new("Billing", Severity.Critical, DateOnly.Parse("2026-03-30"), DateOnly.Parse("2026-03-31"), true, true),
        new("Platform", Severity.High, DateOnly.Parse("2026-03-28"), DateOnly.Parse("2026-03-30"), false, true),
        new("Platform", Severity.Low, DateOnly.Parse("2026-03-15"), DateOnly.Parse("2026-03-17"), false, false),
        new("Fulfillment", Severity.High, DateOnly.Parse("2026-03-31"), DateOnly.Parse("2026-04-05"), true, false),
    ];
}
