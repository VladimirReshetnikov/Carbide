// Command dispatcher for the carbide-gh REPL. Each command parses its own args against
// a tiny split-on-whitespace tokeniser; richer parsing (nested quotes, -- separator)
// isn't worth it for a demo. Commands are async — they render output incrementally.
//
// We deliberately do not use Spectre's `AnsiConsole.Status()` / `Progress()` APIs because
// their spinner frames tick via `Task.Delay`, which PNS's on Mono-WASM browser (the T2.1
// regression). A plain `[dim]fetching…[/]` line before each network call is enough
// "I'm working" feedback.

using System;
using System.Threading.Tasks;
using Spectre.Console;

namespace CarbideGh;

internal static class Commands
{
    public static Task DispatchAsync(string line, ReplState state)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return Task.CompletedTask;
        var cmd = parts[0].ToLowerInvariant();
        var args = parts[1..];

        return cmd switch
        {
            "help" or "?" => HelpAsync(),
            "repo" => RepoAsync(args, state),
            "token" => TokenAsync(args, state),
            "verbose" => VerboseAsync(args, state),
            "prs" or "pulls" => PrsAsync(args, state),
            "pr" => PrAsync(args, state),
            "issues" => IssuesAsync(args, state),
            "issue" => IssueAsync(args, state),
            "commits" => CommitsAsync(args, state),
            "contributors" or "contrib" => ContributorsAsync(args, state),
            "stars" => StarsAsync(args, state),
            "clear" or "cls" => Task.Run(() => { Console.Clear(); }),
            _ => UnknownAsync(cmd),
        };
    }

    private static Task HelpAsync()
    {
        Render.HelpPanel();
        return Task.CompletedTask;
    }

    private static Task RepoAsync(string[] args, ReplState state)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine(state.CurrentRepo is { } r
                ? $"current repo: [green]{r.Owner}/{r.Name}[/]"
                : "[dim]no repo set. try[/] [cyan]repo anthropics/claude-code[/]");
            return Task.CompletedTask;
        }
        if (!RepoRef.TryParse(args[0], out var repo))
        {
            AnsiConsole.MarkupLine("[red]bad repo; expected[/] [cyan]owner/name[/]");
            return Task.CompletedTask;
        }
        state.CurrentRepo = repo;
        AnsiConsole.MarkupLine($"repo set to [green]{repo.Owner}/{repo.Name}[/]");
        return Task.CompletedTask;
    }

    private static Task TokenAsync(string[] args, ReplState state)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine(state.Github.Token is null
                ? "[dim]no token set (60 req/hr unauthenticated)[/]"
                : "[green]token set[/] [dim](5000 req/hr authenticated)[/]");
            return Task.CompletedTask;
        }
        state.Github.Token = args[0] == "--clear" ? null : args[0];
        AnsiConsole.MarkupLine(state.Github.Token is null ? "[yellow]token cleared[/]" : "[green]token stored[/]");
        return Task.CompletedTask;
    }

    private static Task VerboseAsync(string[] args, ReplState state)
    {
        if (args.Length == 0)
        {
            state.Verbose = !state.Verbose;
        }
        else
        {
            state.Verbose = args[0] is "on" or "true" or "1";
        }
        AnsiConsole.MarkupLine($"verbose = [cyan]{state.Verbose}[/]");
        return Task.CompletedTask;
    }

    private static async Task PrsAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        var stateArg = "open";
        foreach (var a in args)
        {
            if (a.StartsWith("--state=", StringComparison.Ordinal)) stateArg = a["--state=".Length..];
        }
        AnsiConsole.MarkupLine($"[dim]listing PRs ({stateArg})\u2026[/]");
        var json = await state.Github.ListPullRequestsAsync(repo, stateArg);
        Render.PullRequestTable(repo, stateArg, json);
    }

    private static async Task PrAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        if (args.Length == 0 || !int.TryParse(args[0], out var number))
        {
            AnsiConsole.MarkupLine("[red]usage:[/] [cyan]pr <number>[/]");
            return;
        }
        AnsiConsole.MarkupLine($"[dim]fetching PR #{number}\u2026[/]");
        var pr = await state.Github.GetPullRequestAsync(repo, number);
        var files = await state.Github.ListPullRequestFilesAsync(repo, number);
        Render.PullRequestDetail(repo, pr, files);
    }

    private static async Task IssuesAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        var stateArg = "open";
        string? label = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--state=", StringComparison.Ordinal)) stateArg = a["--state=".Length..];
            else if (a.StartsWith("--label=", StringComparison.Ordinal)) label = a["--label=".Length..];
        }
        AnsiConsole.MarkupLine($"[dim]listing issues ({stateArg}{(label is null ? "" : $", label={label}")})\u2026[/]");
        var json = await state.Github.ListIssuesAsync(repo, stateArg, label);
        Render.IssueTable(repo, stateArg, label, json);
    }

    private static async Task IssueAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        if (args.Length == 0 || !int.TryParse(args[0], out var number))
        {
            AnsiConsole.MarkupLine("[red]usage:[/] [cyan]issue <number>[/]");
            return;
        }
        AnsiConsole.MarkupLine($"[dim]fetching issue #{number}\u2026[/]");
        var issue = await state.Github.GetIssueAsync(repo, number);
        Render.IssueDetail(repo, issue);
    }

    private static async Task CommitsAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        int limit = 20;
        foreach (var a in args)
        {
            if (a.StartsWith("--limit=", StringComparison.Ordinal) && int.TryParse(a["--limit=".Length..], out var n)) limit = Math.Clamp(n, 1, 100);
        }
        AnsiConsole.MarkupLine($"[dim]listing last {limit} commits\u2026[/]");
        var json = await state.Github.ListCommitsAsync(repo, limit);
        Render.CommitTable(repo, json);
    }

    private static async Task ContributorsAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        AnsiConsole.MarkupLine("[dim]listing contributors\u2026[/]");
        var json = await state.Github.ListContributorsAsync(repo);
        Render.ContributorsChart(repo, json);
    }

    private static async Task StarsAsync(string[] args, ReplState state)
    {
        var repo = RequireRepo(state);
        int pages = 4;
        foreach (var a in args)
        {
            if (a.StartsWith("--pages=", StringComparison.Ordinal) && int.TryParse(a["--pages=".Length..], out var n)) pages = Math.Clamp(n, 1, 20);
        }
        AnsiConsole.MarkupLine($"[dim]fetching up to {pages * 100} stars\u2026[/]");
        var timestamps = await state.Github.GetStargazerTimestampsAsync(repo, pages);
        Render.StarSparkline(repo, timestamps);
    }

    private static Task UnknownAsync(string cmd)
    {
        AnsiConsole.MarkupLine($"[red]unknown command:[/] [cyan]{Markup.Escape(cmd)}[/]. try [cyan]help[/].");
        return Task.CompletedTask;
    }

    private static RepoRef RequireRepo(ReplState state)
    {
        if (state.CurrentRepo is { } r) return r;
        throw new InvalidOperationException("no repo set. run `repo owner/name` first.");
    }
}
