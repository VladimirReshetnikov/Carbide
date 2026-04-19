using Monitoring;

var parser = new LogParser();
var entries = SampleLog.Lines.Select(parser.Parse).ToArray();
var report = LogAnalytics.BuildReport(entries);

Console.WriteLine($"Errors={report.ErrorCount};Warnings={report.WarningCount}");
Console.WriteLine($"Slowest={report.SlowestPath};Ms={report.SlowestDurationMs}");
Console.WriteLine($"P95={report.P95DurationMs}");
