// carbide-gh — entry point for the Spectre.Console-powered GitHub REPL. Runs entirely
// in the browser on Mono-WASM through Carbide. The `await Console.In.ReadLineAsync()`
// below used to trip T2.1 (`PlatformNotSupportedException: Cannot wait on monitors`);
// since the T2.1 resolution the REPL loops cleanly.

using System;
using System.Threading.Tasks;
using CarbideGh;
using Spectre.Console;

Render.Banner();
Render.UsageHint();

var state = new ReplState();
while (true)
{
    Render.Prompt(state);
    string? line;
    try
    {
        line = await Console.In.ReadLineAsync();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]readline failed: {Markup.Escape(ex.Message)}[/]");
        break;
    }
    if (line is null)
    {
        AnsiConsole.MarkupLine("[dim]\u2014 input closed \u2014[/]");
        break;
    }
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line is "exit" or "quit" or ":q")
    {
        AnsiConsole.MarkupLine("[dim]bye.[/]");
        break;
    }

    try
    {
        await Commands.DispatchAsync(line, state);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
        if (state.Verbose)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
        }
    }
}
