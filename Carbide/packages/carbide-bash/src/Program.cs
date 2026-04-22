using CarbideBash.Errors;
using CarbideBash.Host;

Banner.Write(Console.Out);

var shell = new ShellHost();

while (true)
{
    Console.Out.Write(shell.BuildPrompt());
    await Console.Out.FlushAsync();

    string? line;
    try { line = await Console.In.ReadLineAsync(); }
    catch (Exception ex) { Console.Error.WriteLine($"readline failed: {ex.Message}"); break; }
    if (line is null) break;

    var trimmed = line.Trim();
    if (trimmed.Length == 0) continue;
    if (trimmed == "exit" || trimmed == "quit") break;

    try { shell.Submit(line, Console.In, Console.Out, Console.Error); }
    catch (BashException ex) { Console.Error.WriteLine(ex.Message); }
    catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); }
}

Console.Out.WriteLine("\x1b[2mgoodbye.\x1b[0m");
