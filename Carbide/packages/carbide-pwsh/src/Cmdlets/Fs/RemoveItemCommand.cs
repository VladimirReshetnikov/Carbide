using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class RemoveItemCommand : Cmdlet
{
    public override string Name => "Remove-Item";
    public override IEnumerable<string> Aliases => new[] { "rm", "del", "erase", "ri", "rd", "rmdir" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var paths = new List<string>();
        if (binding.Named.TryGetValue("Path", out var namedPath) && namedPath != null)
        {
            if (namedPath is System.Collections.IEnumerable e && namedPath is not string)
                foreach (var x in e) paths.Add(Coercion.FormatAsString(x));
            else paths.Add(Coercion.FormatAsString(namedPath));
        }
        if (paths.Count == 0)
        {
            foreach (var p in binding.Positional) paths.Add(Coercion.FormatAsString(p));
        }
        if (paths.Count == 0 && input != null)
        {
            foreach (var p in input) paths.Add(Coercion.FormatAsString(p));
        }
        if (paths.Count == 0)
            throw new PwshRuntimeException("Remove-Item requires at least one -Path argument.");

        var recurse = binding.HasSwitch("Recurse");
        var force = binding.HasSwitch("Force");
        var providers = new Providers(context.Interpreter);
        foreach (var path in paths)
        {
            var (drive, sub) = PathQualifier.Parse(path, context.Interpreter.CurrentDrive);
            if (drive != PwshDriveKind.FileSystem)
            {
                if (string.IsNullOrEmpty(sub))
                    throw new PwshRuntimeException($"Remove-Item on the {PathQualifier.DriveName(drive)} drive requires an item name.");
                providers.Remove(drive, sub);
                continue;
            }
            context.Vfs.Delete(path, recurse, force);
        }
        yield break;
    }
}
