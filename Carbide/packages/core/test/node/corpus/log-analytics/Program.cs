using Corp.Observability;

var lines = new[]
{
    "2026-04-19T10:00:00Z|gateway|/orders|200|85",
    "2026-04-19T10:00:01Z|gateway|/orders|200|110",
    "2026-04-19T10:00:02Z|gateway|/orders|500|460",
    "2026-04-19T10:00:03Z|catalog|/products|200|72",
    "2026-04-19T10:00:04Z|catalog|/products|503|180",
    "2026-04-19T10:00:05Z|catalog|/products|200|95",
    "2026-04-19T10:00:06Z|checkout|/pay|200|130",
    "2026-04-19T10:00:07Z|checkout|/pay|200|150",
};

var parsed = LogAnalyzer.Parse(lines);
var summary = LogAnalyzer.BuildSummary(parsed);
Console.Write(string.Join("\n", summary));
