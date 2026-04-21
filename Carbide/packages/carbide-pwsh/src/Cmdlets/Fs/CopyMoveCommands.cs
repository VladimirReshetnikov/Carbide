using CarbidePwsh.Errors;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class CopyItemCommand : Cmdlet
{
    public override string Name => "Copy-Item";
    public override IEnumerable<string> Aliases => new[] { "cp", "copy", "cpi" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var src = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Copy-Item requires a -Path argument.");
        var dst = binding.GetValue<string>("Destination", 1, null)
            ?? throw new PwshRuntimeException("Copy-Item requires a -Destination argument.");
        var recurse = binding.HasSwitch("Recurse");
        context.Vfs.Copy(src, dst, recurse);
        yield break;
    }
}

public sealed class MoveItemCommand : Cmdlet
{
    public override string Name => "Move-Item";
    public override IEnumerable<string> Aliases => new[] { "mv", "move", "mi" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var src = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Move-Item requires a -Path argument.");
        var dst = binding.GetValue<string>("Destination", 1, null)
            ?? throw new PwshRuntimeException("Move-Item requires a -Destination argument.");
        context.Vfs.Move(src, dst);
        yield break;
    }
}
