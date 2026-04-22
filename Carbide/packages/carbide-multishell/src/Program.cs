// carbide-multishell — top-level interactive session spanning pwsh, cmd, and bash.
// Boots into pwsh as the default prompt; typing `cmd`, `bash`, `/usr/bin/bash`, or any
// other registered shell stub enters that shell's interactive sub-REPL, and `exit` pops
// back to the caller. Nested invocations stack arbitrarily.
//
// The heavy lifting lives in CarbideShellCore.Dispatch.ShellDispatcher.RunInteractive —
// this entry point is a few lines of wiring around Console.In / Console.Out / Console.Error.

using CarbideMultishell;

var session = new MultishellSession();

Banner(Console.Out);

var kernel = session.ResolveKernel(StartingShell());
var ctx = session.BuildContext(Console.In, Console.Out, Console.Error);
var exitCode = session.Dispatcher.RunInteractive(kernel, ctx);

Console.Out.WriteLine($"\x1b[2mgoodbye (exit {exitCode}).\x1b[0m");
Environment.ExitCode = exitCode;

static string StartingShell()
{
    // Honor --shell pwsh | cmd | bash as the first-argument override so the launcher
    // page can change the default without recompiling the WASM assembly.
    var args = Environment.GetCommandLineArgs();
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--shell" || args[i] == "-s") return args[i + 1];
    }
    return "pwsh";
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
