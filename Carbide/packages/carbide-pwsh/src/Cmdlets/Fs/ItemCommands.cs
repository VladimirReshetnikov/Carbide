using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

/// <summary>
/// <c>Get-Item</c> — retrieve the item at the given path. Dispatches to the matching
/// provider when the path carries a drive qualifier (<c>Env:FOO</c>, <c>Alias:cd</c>,
/// <c>Function:Add</c>, <c>Variable:x</c>); otherwise reads from the VFS.
/// </summary>
public sealed class GetItemCommand : Cmdlet
{
    public override string Name => "Get-Item";
    public override IEnumerable<string> Aliases => new[] { "gi" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Get-Item requires a -Path argument.");
        var (drive, sub) = PathQualifier.Parse(path, context.Interpreter.CurrentDrive);
        if (drive != PwshDriveKind.FileSystem)
        {
            var providers = new Providers(context.Interpreter);
            if (string.IsNullOrEmpty(sub))
            {
                // `Get-Item Env:` returns the drive itself — represent as a container marker.
                yield return new PwshProviderItem(drive, PathQualifier.DriveName(drive), null);
                yield break;
            }
            var item = providers.GetItem(drive, sub);
            if (item is null)
                throw new PwshRuntimeException($"Cannot find item '{PathQualifier.DriveName(drive)}:{sub}'.");
            yield return item;
            yield break;
        }
        var node = context.Vfs.Resolve(path)
            ?? throw new PwshRuntimeException($"Cannot find path '{path}' because it does not exist.");
        yield return node;
    }
}

/// <summary>
/// <c>Set-Item</c> — set the value of a provider item. On <c>Env:</c> and <c>Variable:</c>
/// this is an assignment; <c>Alias:</c> and <c>Function:</c> don't support arbitrary value
/// updates in Phase 1.
/// </summary>
public sealed class SetItemCommand : Cmdlet
{
    public override string Name => "Set-Item";
    public override IEnumerable<string> Aliases => new[] { "si" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Set-Item requires a -Path argument.");
        var value = binding.GetValue<object?>("Value", 1, null);
        var (drive, sub) = PathQualifier.Parse(path, context.Interpreter.CurrentDrive);
        if (drive != PwshDriveKind.FileSystem)
        {
            if (string.IsNullOrEmpty(sub))
                throw new PwshRuntimeException("Set-Item requires an item name under the drive.");
            new Providers(context.Interpreter).SetItem(drive, sub, value);
            yield break;
        }
        // FileSystem Set-Item overwrites a file's contents with the supplied text.
        context.Vfs.CreateTextFile(path, Coercion.FormatAsString(value), overwrite: true);
        yield break;
    }
}
