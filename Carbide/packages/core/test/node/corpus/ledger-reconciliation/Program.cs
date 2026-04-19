using Accounting;

var csv = new[]
{
    "2026-03-01,Sales,1200.40",
    "2026-03-01,Refund,-200.10",
    "2026-03-02,Sales,500.00",
    "2026-03-02,Hosting,-150.00",
    "2026-03-03,Payroll,-800.25",
    "2026-03-03,Sales,900.00",
};

var ledger = JournalParser.Parse(csv);
var report = Reconciliation.BuildDailyBalances(ledger);

foreach (var row in report)
{
    Console.WriteLine($"{row.Day:yyyy-MM-dd}|{row.Income:0.00}|{row.Expense:0.00}|{row.Net:0.00}|{row.Running:0.00}");
}
