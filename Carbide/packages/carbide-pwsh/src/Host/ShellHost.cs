using CarbidePwsh.Cmdlets;
using CarbidePwsh.Cmdlets.Fs;
using CarbidePwsh.Cmdlets.Json;
using CarbidePwsh.Cmdlets.Output;
using CarbidePwsh.Cmdlets.Shape;
using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;
using CarbidePwsh.Runtime;
using CarbidePwsh.Vfs;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Host;

/// <summary>
/// Owns the REPL's persistent state: one <see cref="Scope"/>, one <see cref="Interpreter"/>,
/// one <see cref="VirtualFileSystem"/>, and the registered <see cref="CmdletRegistry"/>.
/// Submissions parse + evaluate one script text per call and render the final result.
/// </summary>
public sealed class ShellHost
{
    public Interpreter Interpreter { get; }
    public VirtualFileSystem Vfs { get; }
    public CmdletRegistry Registry { get; }
    public bool Verbose { get; set; }

    public ShellHost()
    {
        Vfs = new VirtualFileSystem();
        Vfs.CreateDirectory("/tmp");
        Vfs.CreateDirectory("/home/user");
        Vfs.CurrentLocation = "/home/user";

        Registry = new CmdletRegistry();
        RegisterBuiltinCmdlets(Registry);

        Interpreter = new Interpreter
        {
            Vfs = Vfs,
            Registry = Registry,
            PipelineOutput = Console.Out,
            PipelineError = Console.Error,
        };

        Interpreter.Scope.Set(null, "PSVersionTable", BuildVersionTable());
        Interpreter.Scope.Set(null, "HOME", VfsPath.HomePath);
    }

    public string BuildPrompt()
    {
        var pwd = Vfs.CurrentLocation;
        return $"PS {pwd}> ";
    }

    public string ContinuationPrompt() => ">> ";

    /// <summary>Submit source for parse + evaluate. Throws on parse errors or runtime errors.</summary>
    public object? Submit(string source)
    {
        // Thread the pipeline output at submission time to pick up any current Console.Out
        // redirection (e.g., tests capture stdout).
        Interpreter.PipelineOutput = Console.Out;
        Interpreter.PipelineError = Console.Error;
        var script = PwshParser.ParseString(source);
        return Interpreter.Evaluate(script);
    }

    public void SubmitAndRender(string source, TextWriter output)
    {
        var result = Submit(source);
        RenderResult(result, output);
    }

    public static void RenderResult(object? result, TextWriter output)
    {
        if (result == null) return;
        var text = OutputFormatter.Format(result);
        if (text.Length == 0) return;
        output.WriteLine(text);
    }

    public void RenderError(Exception ex, TextWriter errorOutput)
    {
        var message = ex is PwshException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        errorOutput.Write("\x1b[31merror:\x1b[0m ");
        errorOutput.WriteLine(message);
        if (Verbose)
        {
            errorOutput.WriteLine(ex.StackTrace);
        }
    }

    private static System.Collections.Specialized.OrderedDictionary BuildVersionTable()
    {
        var dict = new System.Collections.Specialized.OrderedDictionary();
        dict["PSVersion"] = "7.5-carbide-subset";
        dict["Edition"] = "CarbidePwsh";
        dict["Phase"] = 2;
        return dict;
    }

    private static void RegisterBuiltinCmdlets(CmdletRegistry r)
    {
        // Output.
        r.Register(() => new WriteOutputCommand());
        r.Register(() => new WriteHostCommand());
        r.Register(() => new WriteErrorCommand());
        r.Register(() => new OutStringCommand());
        r.Register(() => new ReadHostCommand());

        // Shape.
        r.Register(() => new WhereObjectCommand());
        r.Register(() => new ForEachObjectCommand());
        r.Register(() => new SelectObjectCommand());
        r.Register(() => new SortObjectCommand());
        r.Register(() => new GroupObjectCommand());
        r.Register(() => new MeasureObjectCommand());

        // JSON.
        r.Register(() => new ConvertToJsonCommand());
        r.Register(() => new ConvertFromJsonCommand());

        // FS.
        r.Register(() => new GetChildItemCommand());
        r.Register(() => new GetContentCommand());
        r.Register(() => new SetContentCommand());
        r.Register(() => new AddContentCommand());
        r.Register(() => new NewItemCommand());
        r.Register(() => new RemoveItemCommand());
        r.Register(() => new TestPathCommand());
        r.Register(() => new SetLocationCommand());
        r.Register(() => new GetLocationCommand());
        r.Register(() => new ResolvePathCommand());
        r.Register(() => new JoinPathCommand());
        r.Register(() => new CopyItemCommand());
        r.Register(() => new MoveItemCommand());
    }
}
