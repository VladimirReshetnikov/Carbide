using CarbidePwsh.Errors;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class NewItemCommand : Cmdlet
{
    public override string Name => "New-Item";
    public override IEnumerable<string> Aliases => new[] { "ni" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("New-Item requires a -Path argument.");
        var type = binding.GetOrDefault<string>("ItemType", "File") ?? "File";
        var force = binding.HasSwitch("Force");

        if (type.Equals("Directory", StringComparison.OrdinalIgnoreCase))
        {
            var dir = context.Vfs.CreateDirectory(path);
            yield return dir;
            yield break;
        }

        // File.
        var value = binding.GetOrDefault<string>("Value", "") ?? "";
        var file = context.Vfs.CreateTextFile(path, value, overwrite: force);
        yield return file;
    }
}

public sealed class MkdirCommand : Cmdlet
{
    public override string Name => "mkdir";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("mkdir requires a path argument.");
        yield return context.Vfs.CreateDirectory(path);
    }
}
