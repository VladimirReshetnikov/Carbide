// carbide-pwsh — entry point for the interactive shell. Phase 3 adds control flow,
// functions, scripts, classes/enums, app invocation, and a lightweight prompt editor on top
// of the earlier pipeline/VFS/cmdlet shell. Compiles and runs in the browser on Mono-WASM
// through Carbide (the same way carbide-gh does) and standalone via `dotnet run` on
// Windows/Linux for local smoke testing.

using System.Text;
using CarbidePwsh.Errors;
using CarbidePwsh.Host;

Banner.Write(Console.Out);

var shell = new ShellHost();
var promptEditor = new PwshPromptEditor(shell);
var pending = new StringBuilder();

while (true)
{
    var prompt = pending.Length == 0 ? shell.BuildPrompt() : shell.ContinuationPrompt();
    PromptReadResult readResult;
    try
    {
        readResult = await promptEditor.ReadLineAsync(prompt, allowHistory: pending.Length == 0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"readline failed: {ex.Message}");
        break;
    }

    if (readResult.Kind == PromptReadResultKind.EndOfInput) break;
    if (readResult.Kind == PromptReadResultKind.Interrupted)
    {
        pending.Clear();
        continue;
    }

    var line = readResult.Line ?? "";

    if (pending.Length == 0)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) continue;
        if (trimmed is "exit" or "quit" or ":q") break;
    }
    else
    {
        // Inside a multi-line submission, a blank line cancels the pending buffer.
        if (line.Trim().Length == 0)
        {
            pending.Clear();
            continue;
        }
    }

    if (pending.Length > 0) pending.Append('\n');
    pending.Append(line);

    var source = pending.ToString();

    try
    {
        shell.SubmitAndRender(source, Console.Out);
        pending.Clear();
    }
    catch (PwshIncompleteInputException)
    {
        // Keep accumulating; the next iteration shows the continuation prompt.
    }
    catch (Exception ex)
    {
        shell.RenderError(ex, Console.Error);
        pending.Clear();
    }
}

Console.Out.WriteLine("\x1b[2mgoodbye.\x1b[0m");
