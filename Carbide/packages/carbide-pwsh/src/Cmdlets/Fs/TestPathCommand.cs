using CarbidePwsh.Errors;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class TestPathCommand : Cmdlet
{
    public override string Name => "Test-Path";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Test-Path requires a -Path argument.");
        var pathType = binding.GetOrDefault<string>("PathType", "Any") ?? "Any";

        bool ok = pathType.ToLowerInvariant() switch
        {
            "container" => context.Vfs.IsDirectory(path),
            "leaf" => context.Vfs.IsFile(path),
            _ => context.Vfs.Exists(path),
        };
        yield return ok;
    }
}
