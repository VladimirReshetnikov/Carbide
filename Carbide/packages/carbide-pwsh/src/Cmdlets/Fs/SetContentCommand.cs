using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class SetContentCommand : Cmdlet
{
    public override string Name => "Set-Content";
    public override IEnumerable<string> Aliases => new[] { "sc" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Set-Content requires a -Path argument.");
        var encoding = binding.GetOrDefault<string>("Encoding", "utf-8") ?? "utf-8";

        string text;
        if (binding.Named.TryGetValue("Value", out var v) && v is not null)
        {
            text = ValueToString(v);
        }
        else if (input != null)
        {
            text = string.Join("\n", input.Select(ValueToString));
        }
        else
        {
            text = "";
        }

        context.Vfs.CreateTextFile(path, text, overwrite: true, encoding: encoding);
        yield break;
    }

    private static string ValueToString(object? v)
    {
        if (v is System.Collections.IEnumerable e && v is not string)
        {
            var parts = new List<string>();
            foreach (var item in e) parts.Add(Coercion.FormatAsString(item));
            return string.Join("\n", parts);
        }
        return Coercion.FormatAsString(v);
    }
}
