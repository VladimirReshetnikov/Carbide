using Data;

var byDept = Seed.All
    .GroupBy(e => e.Department)
    .OrderBy(g => g.Key)
    .Select(g => new { Dept = g.Key, Avg = g.Average(x => x.Salary) });

foreach (var row in byDept)
{
    Console.WriteLine($"{row.Dept}: {row.Avg:F0}");
}
