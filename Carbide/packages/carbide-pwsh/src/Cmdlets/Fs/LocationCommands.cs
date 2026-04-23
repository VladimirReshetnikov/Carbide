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
        LocationCommandHelpers.ApplyLocationChange(context, path);
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

public sealed class ConvertPathCommand : Cmdlet
{
    public override string Name => "Convert-Path";
    public override IEnumerable<string> Aliases => new[] { "cvpa" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Convert-Path requires a -Path argument.");
        var (drive, sub) = PathQualifier.Parse(path, context.Interpreter.CurrentDrive);
        if (drive == PwshDriveKind.FileSystem)
        {
            yield return context.Vfs.Normalize(sub);
            yield break;
        }

        yield return string.IsNullOrEmpty(sub)
            ? PathQualifier.DriveName(drive) + ":\\"
            : PathQualifier.DriveName(drive) + ":\\" + sub;
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

public sealed class PushLocationCommand : Cmdlet
{
    public override string Name => "Push-Location";
    public override IEnumerable<string> Aliases => new[] { "pushd" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        context.Interpreter.LocationStack.Push((context.Interpreter.CurrentDrive, context.Vfs.CurrentLocation));
        var path = binding.GetValue<string>("Path", 0, null);
        if (!string.IsNullOrEmpty(path))
            LocationCommandHelpers.ApplyLocationChange(context, path);
        yield break;
    }
}

public sealed class PopLocationCommand : Cmdlet
{
    public override string Name => "Pop-Location";
    public override IEnumerable<string> Aliases => new[] { "popd" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (context.Interpreter.LocationStack.Count == 0)
            throw new PwshRuntimeException("The location stack is empty.");

        var (drive, path) = context.Interpreter.LocationStack.Pop();
        context.Interpreter.CurrentDrive = drive;
        if (drive == PwshDriveKind.FileSystem)
            context.Vfs.SetLocation(path);
        yield break;
    }
}

internal static class LocationCommandHelpers
{
    public static void ApplyLocationChange(CmdletContext context, string path)
    {
        var (drive, sub) = PathQualifier.Parse(path, PwshDriveKind.FileSystem);
        if (drive != PwshDriveKind.FileSystem)
        {
            if (!string.IsNullOrEmpty(sub))
                throw new PwshRuntimeException(
                    $"The {PathQualifier.DriveName(drive)} provider does not support sub-paths; `cd {PathQualifier.DriveName(drive)}:` only.");
            context.Interpreter.CurrentDrive = drive;
            return;
        }

        context.Interpreter.CurrentDrive = PwshDriveKind.FileSystem;
        context.Vfs.SetLocation(sub);
    }
}
