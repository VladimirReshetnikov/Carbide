using System.Collections;
using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;
using CarbidePwsh.Runtime;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Host;

/// <summary>
/// Owns the REPL's persistent state: one <see cref="Scope"/>, one <see cref="Interpreter"/>.
/// Submissions parse + evaluate one script text per call and render the final result.
/// </summary>
public sealed class ShellHost
{
    public Interpreter Interpreter { get; }
    public bool Verbose { get; set; }

    public ShellHost()
    {
        Interpreter = new Interpreter();
        Interpreter.Scope.Set(null, "PSVersionTable", BuildVersionTable());
    }

    public string BuildPrompt() => "PS > ";

    public object? Submit(string source)
    {
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
        // An assignment statement produces null; an expression statement's result is what we print.
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
        dict["Phase"] = 1;
        return dict;
    }
}
