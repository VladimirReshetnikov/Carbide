using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class TestPathCommand : Cmdlet
{
    public override string Name => "Test-Path";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Test-Path requires a -Path argument.");
        var pathType = binding.GetOrDefault<string>("PathType", "Any") ?? "Any";

        var (drive, sub) = PathQualifier.Parse(path, context.Interpreter.CurrentDrive);
        if (drive != PwshDriveKind.FileSystem)
        {
            // Provider drives are flat — any sub-path is a "leaf"; the drive itself is
            // always considered a container. PathType Container/Leaf differentiate.
            bool exists = !string.IsNullOrEmpty(sub)
                ? new Providers(context.Interpreter).Exists(drive, sub)
                : true;
            bool ok = pathType.ToLowerInvariant() switch
            {
                "container" => string.IsNullOrEmpty(sub),
                "leaf" => exists && !string.IsNullOrEmpty(sub),
                _ => exists || string.IsNullOrEmpty(sub),
            };
            yield return ok;
            yield break;
        }

        bool fsOk = pathType.ToLowerInvariant() switch
        {
            "container" => context.Vfs.IsDirectory(path),
            "leaf" => context.Vfs.IsFile(path),
            _ => context.Vfs.Exists(path),
        };
        yield return fsOk;
    }
}
