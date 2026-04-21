// carbide-pwsh — entry point for the Phase 1 expression-evaluator REPL. Compiles and runs in
// the browser on Mono-WASM through Carbide (the same way carbide-gh does) and standalone via
// `dotnet run` on Windows/Linux for local smoke testing.

using CarbidePwsh.Host;

Banner.Write(Console.Out);

var shell = new ShellHost();
while (true)
{
    Console.Out.Write(shell.BuildPrompt());
    await Console.Out.FlushAsync();

    string? line;
    try
    {
        line = await Console.In.ReadLineAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"readline failed: {ex.Message}");
        break;
    }

    if (line is null) break;
    var trimmed = line.Trim();
    if (trimmed.Length == 0) continue;

    if (trimmed is "exit" or "quit" or ":q") break;

    try
    {
        shell.SubmitAndRender(trimmed, Console.Out);
    }
    catch (Exception ex)
    {
        shell.RenderError(ex, Console.Error);
    }
}

Console.Out.WriteLine("\x1b[2mgoodbye.\x1b[0m");
