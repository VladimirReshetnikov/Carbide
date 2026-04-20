// ⚠ This is the REPL shape the demo was supposed to run. It fails on Mono-WASM
// single-threaded browser because every `await` that genuinely suspends trips
// `PlatformNotSupportedException: Cannot wait on monitors`. See the sibling T2.1
// investigation report for the full story. The code is preserved as a reference
// artifact, not as a working demo.

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
        // ⚠ This `await` is what trips T2.1. The reader's TCS is TaskCreationOptions.None
        // (synchronous continuations) but `await` on an incomplete Task still reaches
        // `Monitor.Wait(INFINITE)` in the state-machine suspension path on single-
        // threaded browser-wasm.
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
