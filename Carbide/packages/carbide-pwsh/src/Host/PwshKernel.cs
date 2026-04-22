using CarbidePwsh.Errors;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Host;

/// <summary>
/// <see cref="IShellKernel"/> implementation for the pwsh dialect. Delegates to the owning
/// <see cref="ShellHost"/> for source evaluation and multi-line-completeness detection.
/// <see cref="Execute"/> temporarily rebinds <see cref="Console.Out"/> /
/// <see cref="Console.Error"/> / <see cref="Console.In"/> to the caller's streams so pwsh
/// output lands where the invoking shell expects — pwsh's interpreter writes to
/// <c>Console.*</c> for its pipeline streams and we have to honor that inside a cross-shell
/// invocation.
/// </summary>
public sealed class PwshKernel : IShellKernel
{
    private readonly ShellHost _host;

    public PwshKernel(ShellHost host) { _host = host; }

    public string Name => "pwsh";
    public IReadOnlyCollection<string> Aliases { get; } = new[] { "powershell" };
    public IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".ps1", ".psm1" };

    public int Execute(string source, ShellExecutionContext ctx)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;
        try
        {
            Console.SetOut(ctx.Output);
            Console.SetError(ctx.Error);
            Console.SetIn(ctx.Input);
            try
            {
                _host.SubmitAndRender(source, ctx.Output);
                return 0;
            }
            catch (PwshException ex)
            {
                ctx.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                ctx.Error.WriteLine($"pwsh: {ex.Message}");
                return 1;
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    public int ExecuteFile(string absolutePath, ShellExecutionContext ctx)
    {
        var file = ctx.Vfs.Resolve(absolutePath) as VfsFile;
        if (file is null) { ctx.Error.WriteLine($"pwsh: {absolutePath}: not found"); return 1; }
        return Execute(file.ReadText(), ctx);
    }

    public bool IsCompleteInput(string source)
    {
        try
        {
            _ = Parser.Parser.ParseString(source);
            return true;
        }
        catch (PwshIncompleteInputException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    public string BuildPrompt(ShellExecutionContext ctx) => $"PS {ctx.Vfs.CurrentLocation}> ";

    public string BuildContinuationPrompt(ShellExecutionContext ctx) => ">> ";
}
