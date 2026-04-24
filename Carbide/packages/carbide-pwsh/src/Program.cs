// carbide-pwsh — public browser and local entry point for the shared shell session.
//
// Starts in pwsh with the richer pwsh-specific prompt editor, but cmd, bash, and the full
// virtual executable catalog are all present in the same shared VFS/env/dispatcher session.
// When pwsh launches `cmd` or `bash`, the dispatcher throws RequestSubShellException and
// this outer loop pushes the target kernel on its stack, exactly like carbide-multishell's
// generic runner — except that whenever pwsh is active we keep the pwsh-first editing UX.

using System.Text;
#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
using CarbidePwsh.SharedMultishell;
#else
using CarbideMultishell;
#endif
using CarbidePwsh.Errors;
using CarbidePwsh.Host;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Errors;

Banner.Write(Console.Out);

var session = new MultishellSession();
session.Dispatcher.ThrowOnSubShellEntry = true;

var promptEditor = new PwshPromptEditor(session.Pwsh);
var ctx = session.BuildContext(Console.In, Console.Out, Console.Error);
var stack = new Stack<IShellKernel>();
stack.Push(session.Pwsh.Kernel);

var pending = new StringBuilder();
int lastExit = 0;

while (stack.Count > 0)
{
    var active = stack.Peek();
    var prompt = pending.Length == 0
        ? active.BuildPrompt(ctx)
        : active.BuildContinuationPrompt(ctx);

    string? line;
    if (ReferenceEquals(active, session.Pwsh.Kernel))
    {
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

        if (readResult.Kind == PromptReadResultKind.EndOfInput)
        {
            stack.Pop();
            pending.Clear();
            continue;
        }

        if (readResult.Kind == PromptReadResultKind.Interrupted)
        {
            pending.Clear();
            continue;
        }

        line = readResult.Line ?? "";

        if (pending.Length == 0)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (IsExitLine(trimmed, out var exitCode))
            {
                lastExit = exitCode;
                stack.Pop();
                pending.Clear();
                continue;
            }
        }
        else if (line.Trim().Length == 0)
        {
            // Match the standalone pwsh UX: a blank continuation line abandons the pending
            // multi-line buffer instead of submitting it to the parser.
            pending.Clear();
            continue;
        }
    }
    else
    {
        Console.Out.Write(prompt);
        await Console.Out.FlushAsync();

        try
        {
            line = await Console.In.ReadLineAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{active.Name}: readline failed: {ex.Message}");
            break;
        }

        if (line is null)
        {
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
                pending.Clear();
                continue;
            }
        }
    }

    if (pending.Length > 0) pending.Append('\n');
    pending.Append(line);

    var source = pending.ToString();
    if (!active.IsCompleteInput(source)) continue;

    try
    {
        lastExit = active.Execute(source, ctx);
    }
    catch (RequestSubShellException req)
    {
        stack.Push(req.Kernel);
    }
    catch (Exception ex)
    {
        RenderKernelError(session, active, ex);
        lastExit = 1;
    }
    pending.Clear();
}

Console.Out.WriteLine($"\x1b[2mgoodbye (exit {lastExit}).\x1b[0m");
Environment.ExitCode = lastExit;

static bool IsExitLine(string line, out int exitCode)
{
    exitCode = 0;
    if (line.Equals("exit", StringComparison.OrdinalIgnoreCase)
        || line.Equals("quit", StringComparison.OrdinalIgnoreCase)
        || line == ":q")
    {
        return true;
    }

    if (!line.StartsWith("exit ", StringComparison.OrdinalIgnoreCase))
        return false;

    var rest = line.Substring(5).Trim();
    return int.TryParse(
        rest,
        System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture,
        out exitCode);
}

static void RenderKernelError(MultishellSession session, IShellKernel kernel, Exception ex)
{
    if (ReferenceEquals(kernel, session.Pwsh.Kernel))
    {
        session.Pwsh.RenderError(ex, Console.Error);
        return;
    }

    Console.Error.WriteLine($"{kernel.Name}: {ex.Message}");
}
