using Support;

var tickets = TicketSeed.Load();
var summaries = TriageEngine.Summarize(tickets, DateOnly.Parse("2026-04-01"));

foreach (var summary in summaries.OrderBy(s => s.Queue).ThenBy(s => s.Level))
{
    Console.WriteLine($"{summary.Queue}|{summary.Level}|{summary.Count}|{summary.Breached}");
}
