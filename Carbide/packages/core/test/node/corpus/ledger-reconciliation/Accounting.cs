using System.Globalization;

namespace Accounting;

public sealed record JournalEntry(DateOnly Day, string Category, decimal Amount);
public sealed record DailyBalance(DateOnly Day, decimal Income, decimal Expense, decimal Net, decimal Running);

public static class JournalParser
{
    public static IReadOnlyList<JournalEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<JournalEntry>();
        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            list.Add(new JournalEntry(
                DateOnly.Parse(parts[0], CultureInfo.InvariantCulture),
                parts[1],
                decimal.Parse(parts[2], CultureInfo.InvariantCulture)));
        }

        return list;
    }
}

public static class Reconciliation
{
    public static IReadOnlyList<DailyBalance> BuildDailyBalances(IEnumerable<JournalEntry> entries)
    {
        decimal running = 0m;
        return entries
            .GroupBy(e => e.Day)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var income = g.Where(x => x.Amount > 0m).Sum(x => x.Amount);
                var expense = -g.Where(x => x.Amount < 0m).Sum(x => x.Amount);
                var net = income - expense;
                running += net;
                return new DailyBalance(g.Key, income, expense, net, running);
            })
            .ToArray();
    }
}
