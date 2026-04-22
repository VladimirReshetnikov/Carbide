using CarbidePwsh.Errors;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Errors;
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
        // The BCL's `Console.SetIn` / `SetOut` / `SetError` wrap the supplied reader/writer
        // in a synchronization shim that does not override `ReadLineAsync` / `WriteAsync`.
        // Rebinding with an identity swap (ctx.Input == current Console.In, which is the
        // common case when a bare `bash` / `cmd` is invoked from the top-level pwsh REPL)
        // would corrupt the Mono-WASM-friendly `BrowserTerminalReader` into a Synchronized
        // wrapper whose async methods fall back to sync `ReadLine()` and deadlock the
        // xterm event pump. Only swap when the caller really did hand us a different stream
        // (e.g. a StringReader for `Get-Content … | Invoke-Bash`).
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;
        bool swapOut = !ReferenceEquals(ctx.Output, originalOut);
        bool swapErr = !ReferenceEquals(ctx.Error, originalErr);
        bool swapIn = !ReferenceEquals(ctx.Input, originalIn);
        try
        {
            if (swapOut) Console.SetOut(ctx.Output);
            if (swapErr) Console.SetError(ctx.Error);
            if (swapIn) Console.SetIn(ctx.Input);
            try
            {
                _host.SubmitAndRender(source, ctx.Output);
                return 0;
            }
            catch (RequestSubShellException)
            {
                // Propagate — the outer async REPL is the only component that should
                // decide what happens next (push the target kernel on its stack).
                throw;
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
            if (swapOut) Console.SetOut(originalOut);
            if (swapErr) Console.SetError(originalErr);
            if (swapIn) Console.SetIn(originalIn);
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

    public string BuildPrompt(ShellExecutionContext ctx)
    {
        // Reflect the active pwsh drive in the prompt so users know when they're navigating
        // a provider namespace. `PS Env:\>`, `PS Alias:\>`, etc. match real pwsh's default.
        var display = CarbidePwsh.Runtime.PathQualifier.PromptDisplay(
            _host.Interpreter.CurrentDrive, ctx.Vfs.CurrentLocation);
        return $"PS {display}> ";
    }

    public string BuildContinuationPrompt(ShellExecutionContext ctx) => ">> ";
}
