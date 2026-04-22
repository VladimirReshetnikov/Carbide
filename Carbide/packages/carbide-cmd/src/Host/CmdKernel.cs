using CarbideCmd.Parser;
using CarbideCmd.Runtime;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Host;

/// <summary>
/// <see cref="IShellKernel"/> implementation for the cmd dialect. Registered with the shared
/// <see cref="ShellDispatcher"/> so other shells (pwsh, bash) can dispatch into cmd via
/// explicit launcher (<c>cmd /c</c>) or extension-based routing (<c>./script.cmd</c>).
/// </summary>
public sealed class CmdKernel : IShellKernel
{
    public string Name => "cmd";

    public IReadOnlyCollection<string> Aliases { get; } =
        new[] { "cmd.exe" };

    public IReadOnlyCollection<string> FileExtensions { get; } =
        new[] { ".cmd", ".bat" };

    public int Execute(string source, ShellExecutionContext ctx)
    {
        var script = Parser.CmdParser.ParseString(source);
        var interp = new Interpreter(ctx) { Positional = new List<string>(ctx.Args) };
        return interp.Execute(script);
    }

    public int ExecuteFile(string absolutePath, ShellExecutionContext ctx)
    {
        var file = ctx.Vfs.Resolve(absolutePath) as VfsFile
            ?? throw new Errors.CmdRuntimeException($"The system cannot find the file specified - {absolutePath}");
        var args = new List<string> { absolutePath };
        args.AddRange(ctx.Args.Skip(1));
        var scoped = ctx.With(args: args);
        var script = Parser.CmdParser.ParseString(file.ReadText());
        var interp = new Interpreter(scoped) { Positional = args };
        return interp.Execute(script);
    }

    public bool IsCompleteInput(string source)
    {
        // cmd has no multi-line constructs in our Phase 1 subset, so every accepted input
        // is complete once it lexes.
        try
        {
            _ = Parser.CmdParser.ParseString(source);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string BuildPrompt(ShellExecutionContext ctx)
    {
        // cmd presents VFS paths with a synthetic C: drive letter and backslashes on
        // display; the VFS internally is forward-slash-normalized.
        var loc = ctx.Vfs.CurrentLocation;
        var display = loc == "/" ? "C:\\" : "C:" + loc.Replace('/', '\\');
        return display + ">";
    }

    public string BuildContinuationPrompt(ShellExecutionContext ctx) => "More? ";
}
