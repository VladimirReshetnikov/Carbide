namespace Support;

public static class TriageEngine
{
    public static IReadOnlyList<QueueSummary> Summarize(IEnumerable<Ticket> tickets, DateOnly now)
    {
        return tickets
            .GroupBy(t => (t.Queue, Level: Rank(t)))
            .Select(g => new QueueSummary(
                g.Key.Queue,
                g.Key.Level,
                g.Count(),
                g.Count(t => t.Due < now)))
            .ToArray();
    }

    private static string Rank(Ticket ticket) => ticket switch
    {
        { ProductionDown: true } => "P0",
        { HasVipCustomer: true, Severity: >= Severity.High } => "P1",
        { Severity: Severity.Critical } => "P1",
        { Severity: Severity.High } => "P2",
        { Severity: Severity.Medium } => "P3",
        _ => "P4",
    };
}
