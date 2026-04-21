// Spectre.Console rendering helpers for carbide-gh. Everything emits through
// `AnsiConsole` which writes to `Console.Out`; Carbide T3's forked `System.Console.dll`
// routes those writes + `Console.WindowWidth` to the JS bridge so Spectre's auto-sizing
// fits the real xterm.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CarbideGh;

internal static class Render
{
    public static void Banner()
    {
        AnsiConsole.Write(
            new FigletText("carbide-gh")
                .LeftJustified()
                .Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]a Spectre.Console GitHub REPL, compiled in the browser by Carbide (T3)[/]");
        AnsiConsole.Write(new Rule { Style = Style.Parse("grey30") });
    }

    public static void UsageHint()
    {
        AnsiConsole.MarkupLine("type [cyan]help[/] for commands, [cyan]repo owner/name[/] to get started, [cyan]exit[/] to quit.");
        AnsiConsole.WriteLine();
    }

    public static void Prompt(ReplState state)
    {
        var r = state.CurrentRepo is { } repo ? $"[grey50] ({repo.Owner}/{repo.Name})[/]" : "";
        AnsiConsole.Markup($"[green]gh[/] [grey50]\u203A[/]{r} ");
    }

    public static void HelpPanel()
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn(new GridColumn());
        grid.AddRow("[cyan]help[/], [cyan]?[/]", "show this panel");
        grid.AddRow("[cyan]repo[/] [grey50]owner/name[/]", "set the current repo (all other commands read it)");
        grid.AddRow("[cyan]token[/] [grey50]ghp_\u2026 | --clear[/]", "set or clear the GitHub PAT (5000 req/hr vs 60)");
        grid.AddRow("[cyan]verbose[/] [grey50][[on|off]][/]", "toggle full exception traces");
        grid.AddRow("[cyan]prs[/] [grey50][[--state=open|closed|all]][/]", "list pull requests");
        grid.AddRow("[cyan]pr[/] [grey50]<n>[/]", "show one PR (panel + file tree)");
        grid.AddRow("[cyan]issues[/] [grey50][[--state=\u2026]] [[--label=\u2026]][/]", "list issues");
        grid.AddRow("[cyan]issue[/] [grey50]<n>[/]", "show one issue");
        grid.AddRow("[cyan]commits[/] [grey50][[--limit=N]][/]", "list recent commits");
        grid.AddRow("[cyan]contributors[/]", "bar chart of top committers");
        grid.AddRow("[cyan]stars[/] [grey50][[--pages=N]][/]", "ASCII sparkline of stargazer growth");
        grid.AddRow("[cyan]beep[/] [grey50][[freq] [ms]][/]", "play a Web Audio tone (default 440 Hz, 200 ms)");
        grid.AddRow("[cyan]fanfare[/]", "play a short C-major arpeggio (C5 E5 G5 C6)");
        grid.AddRow("[cyan]clear[/], [cyan]cls[/]", "clear the screen");
        grid.AddRow("[cyan]exit[/], [cyan]quit[/], [cyan]:q[/]", "exit the REPL");

        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader(" [cyan1]commands[/] ", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("grey35"),
            Padding = new Padding(1, 0, 1, 0),
        });
    }

    public static void PullRequestTable(RepoRef repo, string stateArg, JsonElement json)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded,
            Title = new TableTitle($"[cyan1]pull requests[/] in [green]{repo}[/] [dim]({stateArg})[/]"),
        };
        table.AddColumn("[grey50]#[/]");
        table.AddColumn("[grey50]title[/]");
        table.AddColumn("[grey50]state[/]");
        table.AddColumn("[grey50]author[/]");
        table.AddColumn("[grey50]updated[/]");
        table.AddColumn("[grey50]labels[/]");

        int count = 0;
        foreach (var pr in json.EnumerateArray())
        {
            var num = pr.GetProperty("number").GetInt32();
            var title = pr.GetProperty("title").GetString() ?? "";
            var state = pr.GetProperty("state").GetString() ?? "";
            var author = pr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                ? u.GetProperty("login").GetString() ?? ""
                : "";
            var updated = pr.GetProperty("updated_at").GetDateTime();
            var labels = RenderLabels(pr);

            table.AddRow(
                $"[yellow]{num}[/]",
                Truncate(Markup.Escape(title), 60),
                StateMarkup(state, pr.TryGetProperty("merged_at", out var m) && m.ValueKind != JsonValueKind.Null),
                $"[grey70]{Markup.Escape(author)}[/]",
                $"[grey50]{Ago(updated)}[/]",
                labels);
            count++;
        }
        if (count == 0)
        {
            table.AddRow("[dim]\u2014[/]", "[dim]no pull requests[/]", "", "", "", "");
        }
        AnsiConsole.Write(table);
    }

    public static void PullRequestDetail(RepoRef repo, JsonElement pr, JsonElement files)
    {
        var num = pr.GetProperty("number").GetInt32();
        var title = pr.GetProperty("title").GetString() ?? "";
        var state = pr.GetProperty("state").GetString() ?? "";
        var merged = pr.TryGetProperty("merged_at", out var m) && m.ValueKind != JsonValueKind.Null;
        var author = pr.GetProperty("user").GetProperty("login").GetString() ?? "";
        var baseRef = pr.GetProperty("base").GetProperty("ref").GetString() ?? "";
        var headRef = pr.GetProperty("head").GetProperty("ref").GetString() ?? "";
        var body = pr.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        var additions = pr.GetProperty("additions").GetInt32();
        var deletions = pr.GetProperty("deletions").GetInt32();
        var changedFiles = pr.GetProperty("changed_files").GetInt32();

        var header = new Markup($"[cyan1]PR #{num}[/] {Markup.Escape(Truncate(title, 80))} {StateMarkup(state, merged)}");
        AnsiConsole.Write(header);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey50]{Markup.Escape(author)} \u2192 [/][grey70]{Markup.Escape(baseRef)}[/] [grey50]\u2190\u2014[/] [grey70]{Markup.Escape(headRef)}[/]   [green]+{additions}[/] [red]-{deletions}[/] [dim]across[/] [yellow]{changedFiles}[/] [dim]files[/]");

        if (!string.IsNullOrWhiteSpace(body))
        {
            AnsiConsole.Write(new Panel(new Markup(Markup.Escape(Truncate(body, 2000))))
            {
                Header = new PanelHeader(" [grey50]description[/] ", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("grey35"),
                Padding = new Padding(1, 0, 1, 0),
            });
        }

        var tree = new Tree($"[cyan]changed files[/] [grey50]({changedFiles})[/]")
        {
            Style = Style.Parse("grey50"),
        };
        // Group files by top-level directory for a compact tree.
        var groups = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var f in files.EnumerateArray())
        {
            var filename = f.GetProperty("filename").GetString() ?? "";
            var top = filename.IndexOf('/') is int i and > 0 ? filename[..i] : "\u00B7";
            if (!groups.TryGetValue(top, out var list)) groups[top] = list = new();
            list.Add(f);
        }
        foreach (var kv in groups.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var dirNode = tree.AddNode($"[yellow]{Markup.Escape(kv.Key)}/[/]");
            foreach (var f in kv.Value)
            {
                var filename = f.GetProperty("filename").GetString() ?? "";
                var status = f.GetProperty("status").GetString() ?? "";
                var adds = f.GetProperty("additions").GetInt32();
                var dels = f.GetProperty("deletions").GetInt32();
                var sub = filename.Contains('/') ? filename[(kv.Key.Length + 1)..] : filename;
                var tag = status switch
                {
                    "added" => "[green]+[/]",
                    "removed" => "[red]\u2014[/]",
                    "renamed" => "[yellow]\u21BB[/]",
                    _ => "[cyan]\u00B7[/]",
                };
                dirNode.AddNode($"{tag} [grey70]{Markup.Escape(sub)}[/] [green]+{adds}[/] [red]-{dels}[/]");
            }
        }
        AnsiConsole.Write(tree);
    }

    public static void IssueTable(RepoRef repo, string stateArg, string? label, JsonElement json)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded,
            Title = new TableTitle($"[cyan1]issues[/] in [green]{repo}[/] [dim]({stateArg}{(label is null ? "" : $", label={label}")})[/]"),
        };
        table.AddColumn("[grey50]#[/]");
        table.AddColumn("[grey50]title[/]");
        table.AddColumn("[grey50]state[/]");
        table.AddColumn("[grey50]author[/]");
        table.AddColumn("[grey50]comments[/]");
        table.AddColumn("[grey50]labels[/]");

        int count = 0;
        foreach (var issue in json.EnumerateArray())
        {
            // GitHub mixes PRs into /issues; skip entries that carry pull_request.
            if (issue.TryGetProperty("pull_request", out _)) continue;
            var num = issue.GetProperty("number").GetInt32();
            var title = issue.GetProperty("title").GetString() ?? "";
            var state = issue.GetProperty("state").GetString() ?? "";
            var author = issue.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                ? u.GetProperty("login").GetString() ?? "" : "";
            var comments = issue.GetProperty("comments").GetInt32();
            table.AddRow(
                $"[yellow]{num}[/]",
                Truncate(Markup.Escape(title), 60),
                StateMarkup(state, merged: false),
                $"[grey70]{Markup.Escape(author)}[/]",
                $"[grey50]{comments}[/]",
                RenderLabels(issue));
            count++;
        }
        if (count == 0) table.AddRow("[dim]\u2014[/]", "[dim]no issues[/]", "", "", "", "");
        AnsiConsole.Write(table);
    }

    public static void IssueDetail(RepoRef repo, JsonElement issue)
    {
        var num = issue.GetProperty("number").GetInt32();
        var title = issue.GetProperty("title").GetString() ?? "";
        var state = issue.GetProperty("state").GetString() ?? "";
        var author = issue.GetProperty("user").GetProperty("login").GetString() ?? "";
        var body = issue.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        var comments = issue.GetProperty("comments").GetInt32();
        var updated = issue.GetProperty("updated_at").GetDateTime();

        AnsiConsole.MarkupLine($"[cyan1]issue #{num}[/] {Markup.Escape(Truncate(title, 80))} {StateMarkup(state, merged: false)}");
        AnsiConsole.MarkupLine($"[grey50]by[/] [grey70]{Markup.Escape(author)}[/], [grey50]updated[/] [grey70]{Ago(updated)}[/], [grey70]{comments}[/] [grey50]comments[/] {RenderLabels(issue)}");

        if (!string.IsNullOrWhiteSpace(body))
        {
            AnsiConsole.Write(new Panel(new Markup(Markup.Escape(Truncate(body, 2000))))
            {
                Header = new PanelHeader(" [grey50]body[/] ", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("grey35"),
                Padding = new Padding(1, 0, 1, 0),
            });
        }
    }

    public static void CommitTable(RepoRef repo, JsonElement json)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded,
            Title = new TableTitle($"[cyan1]commits[/] in [green]{repo}[/]"),
        };
        table.AddColumn("[grey50]sha[/]");
        table.AddColumn("[grey50]subject[/]");
        table.AddColumn("[grey50]author[/]");
        table.AddColumn("[grey50]when[/]");

        foreach (var c in json.EnumerateArray())
        {
            var sha = c.GetProperty("sha").GetString() ?? "";
            var commit = c.GetProperty("commit");
            var msg = commit.GetProperty("message").GetString() ?? "";
            var subject = msg.Split('\n', 2)[0];
            var author = commit.GetProperty("author").GetProperty("name").GetString() ?? "";
            var when = commit.GetProperty("author").GetProperty("date").GetDateTime();
            table.AddRow(
                $"[yellow]{sha[..Math.Min(7, sha.Length)]}[/]",
                Truncate(Markup.Escape(subject), 70),
                $"[grey70]{Markup.Escape(author)}[/]",
                $"[grey50]{Ago(when)}[/]");
        }
        AnsiConsole.Write(table);
    }

    public static void ContributorsChart(RepoRef repo, JsonElement json)
    {
        var items = new List<(string Login, int Count)>();
        foreach (var c in json.EnumerateArray())
        {
            var login = c.GetProperty("login").GetString() ?? "";
            var n = c.GetProperty("contributions").GetInt32();
            items.Add((login, n));
        }
        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]no contributors data.[/]");
            return;
        }
        items = items.OrderByDescending(x => x.Count).Take(15).ToList();

        var chart = new BarChart()
            .Width(Math.Min(80, Math.Max(40, Console.WindowWidth - 20)))
            .Label($"[cyan1]top contributors[/] in [green]{repo}[/] [dim](commits)[/]")
            .CenterLabel();
        var palette = new[] { Color.Cyan1, Color.Green, Color.Yellow, Color.Magenta1, Color.Red };
        for (int i = 0; i < items.Count; i++)
        {
            chart.AddItem(items[i].Login, items[i].Count, palette[i % palette.Length]);
        }
        AnsiConsole.Write(chart);
    }

    public static void StarSparkline(RepoRef repo, List<DateTime> timestamps)
    {
        if (timestamps.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]no stars (or all past the fetch window).[/]");
            return;
        }
        timestamps.Sort();
        var first = timestamps[0];
        var last = timestamps[^1];
        var span = last - first;
        if (span.TotalHours < 1) span = TimeSpan.FromHours(1);

        int buckets = Math.Min(Math.Max(20, Console.WindowWidth - 20), 120);
        var counts = new int[buckets];
        foreach (var t in timestamps)
        {
            var frac = (t - first).TotalMilliseconds / span.TotalMilliseconds;
            int idx = Math.Clamp((int)(frac * (buckets - 1)), 0, buckets - 1);
            counts[idx]++;
        }
        // Convert to cumulative so the sparkline shows growth, not per-bucket rate.
        for (int i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];

        int max = counts[^1];
        var blocks = " ▁▂▃▄▅▆▇█";
        var sb = new StringBuilder(buckets);
        foreach (var c in counts)
        {
            int idx = max == 0 ? 0 : (int)Math.Round((c / (double)max) * (blocks.Length - 1));
            sb.Append(blocks[idx]);
        }
        var pct = timestamps.Count == counts[^1] ? "" : " [yellow](sample)[/]";
        AnsiConsole.Write(new Panel(new Markup($"[cyan1]{sb}[/]\n[grey50]{first:yyyy-MM-dd}[/] \u2192 [grey50]{last:yyyy-MM-dd}[/]   [yellow]{timestamps.Count}[/] [grey50]stars fetched[/]{pct}"))
        {
            Header = new PanelHeader($" [cyan1]stargazers[/] [dim]({repo})[/] ", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("grey35"),
            Padding = new Padding(1, 0, 1, 0),
        });
    }

    // ---- helpers ---------------------------------------------------------------------

    private static string RenderLabels(JsonElement obj)
    {
        if (!obj.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var lbl in labels.EnumerateArray())
        {
            var name = lbl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            var color = lbl.TryGetProperty("color", out var c) ? c.GetString() ?? "888" : "888";
            parts.Add($"[#{color}]{Markup.Escape(name)}[/]");
        }
        return string.Join(" ", parts);
    }

    private static string StateMarkup(string state, bool merged)
    {
        if (merged) return "[purple]merged[/]";
        return state switch
        {
            "open" => "[green]open[/]",
            "closed" => "[red]closed[/]",
            _ => $"[grey50]{Markup.Escape(state)}[/]",
        };
    }

    private static string Ago(DateTime when)
    {
        var d = DateTime.UtcNow - when.ToUniversalTime();
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        if (d.TotalDays < 365) return $"{(int)(d.TotalDays / 30)}mo ago";
        return $"{(int)(d.TotalDays / 365)}y ago";
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..(max - 1)] + "\u2026";
    }
}
