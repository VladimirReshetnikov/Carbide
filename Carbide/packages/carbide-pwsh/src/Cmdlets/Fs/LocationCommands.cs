using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class SetLocationCommand : Cmdlet
{
    public override string Name => "Set-Location";
    public override IEnumerable<string> Aliases => new[] { "cd", "sl", "chdir" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null) ?? "~";
        // Drive-qualified path (`Env:`, `Alias:…`, etc.) switches the active drive. A bare
        // path routes to the FileSystem VFS as before.
        var (drive, sub) = PathQualifier.Parse(path, PwshDriveKind.FileSystem);
        if (drive != PwshDriveKind.FileSystem)
        {
            if (!string.IsNullOrEmpty(sub))
                throw new PwshRuntimeException(
                    $"The {PathQualifier.DriveName(drive)} provider does not support sub-paths; `cd {PathQualifier.DriveName(drive)}:` only.");
            context.Interpreter.CurrentDrive = drive;
            yield break;
        }
        context.Interpreter.CurrentDrive = PwshDriveKind.FileSystem;
        context.Vfs.SetLocation(sub);
        yield break;
    }
}

public sealed class GetLocationCommand : Cmdlet
{
    public override string Name => "Get-Location";
    public override IEnumerable<string> Aliases => new[] { "pwd", "gl" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        yield return PathQualifier.PromptDisplay(context.Interpreter.CurrentDrive, context.Vfs.CurrentLocation);
    }
}

public sealed class ResolvePathCommand : Cmdlet
{
    public override string Name => "Resolve-Path";
    public override IEnumerable<string> Aliases => new[] { "rvpa" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Resolve-Path requires a -Path argument.");
        yield return context.Vfs.Normalize(path);
    }
}

public sealed class JoinPathCommand : Cmdlet
{
    public override string Name => "Join-Path";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var parent = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Join-Path requires a -Path argument.");
        var child = binding.GetValue<string>("ChildPath", 1, null)
            ?? throw new PwshRuntimeException("Join-Path requires a -ChildPath argument.");
        yield return VfsPath.Join(parent, child);
    }
}
