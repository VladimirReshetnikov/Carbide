using CarbidePwsh.Errors;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class RemoveItemCommand : Cmdlet
{
    public override string Name => "Remove-Item";
    public override IEnumerable<string> Aliases => new[] { "rm", "del", "erase", "ri" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var paths = new List<string>();
        if (binding.Named.TryGetValue("Path", out var namedPath) && namedPath != null)
        {
            if (namedPath is System.Collections.IEnumerable e && namedPath is not string)
                foreach (var x in e) paths.Add(Runtime.Coercion.FormatAsString(x));
            else paths.Add(Runtime.Coercion.FormatAsString(namedPath));
        }
        if (paths.Count == 0)
        {
            foreach (var p in binding.Positional) paths.Add(Runtime.Coercion.FormatAsString(p));
        }
        if (paths.Count == 0 && input != null)
        {
            foreach (var p in input) paths.Add(Runtime.Coercion.FormatAsString(p));
        }
        if (paths.Count == 0)
            throw new PwshRuntimeException("Remove-Item requires at least one -Path argument.");

        var recurse = binding.HasSwitch("Recurse");
        var force = binding.HasSwitch("Force");
        foreach (var path in paths)
        {
            context.Vfs.Delete(path, recurse, force);
        }
        yield break;
    }
}
