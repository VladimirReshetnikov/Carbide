using CarbideCmd.Errors;
using CarbideCmd.Host;

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
    if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    try
    {
        shell.Submit(line, Console.In, Console.Out, Console.Error);
    }
    catch (CmdException ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
    }
}

Console.Out.WriteLine("\x1b[2mgoodbye.\x1b[0m");
