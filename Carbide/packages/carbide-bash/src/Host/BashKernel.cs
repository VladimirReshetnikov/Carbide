using CarbideBash.Parser;
using CarbideBash.Runtime;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;

namespace CarbideBash.Host;

public sealed class BashKernel : IShellKernel
{
    public string Name => "bash";

    public IReadOnlyCollection<string> Aliases { get; } = new[] { "sh" };

    public IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".sh" };

    public int Execute(string source, ShellExecutionContext ctx)
    {
        var script = BashParser.ParseString(source);
        var interp = new Interpreter(ctx) { Positional = new List<string>(ctx.Args) };
        return interp.Execute(script);
    }

    public int ExecuteFile(string absolutePath, ShellExecutionContext ctx)
    {
        var file = ctx.Vfs.Resolve(absolutePath) as VfsFile
            ?? throw new Errors.BashRuntimeException($"{absolutePath}: No such file or directory");
        var args = new List<string> { absolutePath };
        args.AddRange(ctx.Args.Skip(1));
        var scoped = ctx.With(args: args);
        var script = BashParser.ParseString(file.ReadText());
        var interp = new Interpreter(scoped) { Positional = args };
        return interp.Execute(script);
    }

    public bool IsCompleteInput(string source)
    {
        try { _ = BashParser.ParseString(source); return true; }
        catch { return false; }
    }

    public string BuildPrompt(ShellExecutionContext ctx) => $"user@carbide:{ctx.Vfs.CurrentLocation}$ ";

    public string BuildContinuationPrompt(ShellExecutionContext ctx) => "> ";
}
