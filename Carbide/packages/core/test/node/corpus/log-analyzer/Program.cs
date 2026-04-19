using LogAnalyzer;

var raw = new[]
{
    "2026-04-18T16:00:00Z auth INFO login accepted user=alice",
    "2026-04-18T16:00:01Z auth WARN password retry user=bob",
    "2026-04-18T16:00:02Z api ERROR database timeout operation=GetInvoice",
    "2026-04-18T16:00:03Z api INFO request completed status=200",
    "2026-04-18T16:00:04Z auth ERROR token validation failed user=carol",
};

var parsed = Parser.ParseAll(raw);

var summary = parsed
    .GroupBy(static entry => entry.Level)
    .OrderBy(static group => group.Key)
    .Select(static group => $"{group.Key}:{group.Count()}");

Console.WriteLine(string.Join(",", summary));
Console.WriteLine(parsed.Where(static e => e.Level == Level.Error).Select(static e => e.Service).Distinct().Count());
