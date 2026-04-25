// carbide-multishell — top-level interactive session spanning pwsh, cmd, and bash.
//
// Boots into pwsh as the default prompt. Typing `cmd`, `bash`, `/usr/bin/bash`, or any
// other registered shell stub enters that shell's interactive sub-REPL, and `exit` pops
// back to the caller. Nested invocations stack arbitrarily.
//
// In WASM single-threaded contexts `Console.In.ReadLine()` would deadlock the xterm
// event pump, so this REPL reads lines with `Console.In.ReadLineAsync()` and manages
// the shell stack itself. When the active kernel's cross-shell launcher wants to enter
// a sub-REPL, it throws a `RequestSubShellException` (via Dispatcher.EnterSubShell with
// `ThrowOnSubShellEntry=true`) and this loop catches it and pushes the target kernel on
// the stack. That keeps every kernel's interpreter fully synchronous internally.

using System.Text;
using CarbideMultishell;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Errors;

var session = new MultishellSession();
// Async outer loop mode — required under Mono-WASM single-threaded.
session.Dispatcher.ThrowOnSubShellEntry = true;

Banner(Console.Out);

var ctx = session.BuildContext(Console.In, Console.Out, Console.Error);
var stack = new Stack<IShellKernel>();
stack.Push(session.ResolveKernel(StartingShell()));

int lastExit = 0;
var pending = new StringBuilder();

while (stack.Count > 0)
{
    var active = stack.Peek();
    var prompt = pending.Length == 0
        ? active.BuildPrompt(ctx)
        : active.BuildContinuationPrompt(ctx);
    Console.Out.Write(prompt);
    await Console.Out.FlushAsync();

    string? line;
    try { line = await Console.In.ReadLineAsync(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{active.Name}: readline failed: {ex.Message}");
        break;
    }
    if (line is null)
    {
        // EOF pops the active shell. Outer-most pop ends the session.
        stack.Pop();
        pending.Clear();
        continue;
    }

    if (pending.Length == 0)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) continue;
        if (IsExitLine(trimmed, out var exitCode))
        {
            lastExit = exitCode;
            stack.Pop();
            continue;
        }
    }

    if (pending.Length > 0) pending.Append('\n');
    pending.Append(line);

    var source = pending.ToString();
    if (!active.IsCompleteInput(source)) continue;

    try
    {
        lastExit = await active.ExecuteAsync(source, ctx);
    }
    catch (RequestSubShellException req)
    {
        // Bare cross-shell invocation — push and resume at the new shell's prompt.
        stack.Push(req.Kernel);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{active.Name}: {ex.Message}");
        lastExit = 1;
    }
    pending.Clear();
}

Console.Out.WriteLine($"\x1b[2mgoodbye (exit {lastExit}).\x1b[0m");
Environment.ExitCode = lastExit;

static string StartingShell()
{
    // Honor --shell pwsh | cmd | bash (first-argument override) so the launcher page can
    // change the default without recompiling the WASM assembly.
    var args = Environment.GetCommandLineArgs();
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--shell" || args[i] == "-s") return args[i + 1];
    }
    return "pwsh";
}

static bool IsExitLine(string line, out int exitCode)
{
    exitCode = 0;
    if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)
        || line.Equals("quit", StringComparison.OrdinalIgnoreCase)
        || line == ":q")
        return true;
    if (line.StartsWith("exit ", StringComparison.OrdinalIgnoreCase))
    {
        var rest = line.Substring(5).Trim();
        if (int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out exitCode))
            return true;
    }
    return false;
}

static void Banner(TextWriter w)
{
    w.WriteLine();
    w.WriteLine("\x1b[36mCarbide Multishell\x1b[0m — pwsh / cmd / bash in one Mono-WASM session.");
    w.WriteLine("  Type \x1b[1mcmd\x1b[0m, \x1b[1mbash\x1b[0m, or \x1b[1mpwsh\x1b[0m at any prompt to enter that shell.");
    w.WriteLine("  Type \x1b[1mexit\x1b[0m to leave the current shell; exiting the outermost shell ends the session.");
    w.WriteLine("  All three dialects share one VFS, one env, one $PWD.");
    w.WriteLine();
}
