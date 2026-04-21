using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using CarbidePwsh.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class AddContentCommand : Cmdlet
{
    public override string Name => "Add-Content";
    public override IEnumerable<string> Aliases => new[] { "ac" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Add-Content requires a -Path argument.");
        string text;
        if (binding.Named.TryGetValue("Value", out var v) && v is not null)
            text = Coercion.FormatAsString(v);
        else if (input != null)
            text = string.Join("\n", input.Select(Coercion.FormatAsString));
        else
            text = "";

        var resolved = context.Vfs.Resolve(path);
        if (resolved is VfsFile existing)
        {
            existing.AppendText(text);
        }
        else
        {
            context.Vfs.CreateTextFile(path, text, overwrite: true);
        }
        yield break;
    }
}
