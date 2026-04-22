using CarbidePwsh.Runtime;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class GetChildItemCommand : Cmdlet
{
    public override string Name => "Get-ChildItem";
    public override IEnumerable<string> Aliases => new[] { "dir", "ls", "gci" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var explicitPath = binding.GetValue<string>("Path", 0, null);
        var (drive, sub) = explicitPath is null
            ? (context.Interpreter.CurrentDrive, "")
            : PathQualifier.Parse(explicitPath, context.Interpreter.CurrentDrive);

        if (drive != PwshDriveKind.FileSystem)
        {
            // Provider dispatch: Env / Alias / Function / Variable. A sub-path on a flat
            // provider means "get one item"; empty means "list all".
            var providers = new Providers(context.Interpreter);
            if (!string.IsNullOrEmpty(sub))
            {
                var item = providers.GetItem(drive, sub);
                if (item is not null) yield return item;
                yield break;
            }
            foreach (var item in providers.GetChildren(drive))
                yield return item;
            yield break;
        }

        var filter = binding.GetOrDefault<string>("Filter", null);
        var recurse = binding.HasSwitch("Recurse");
        var filesOnly = binding.HasSwitch("File");
        var directoriesOnly = binding.HasSwitch("Directory");
        var path = explicitPath ?? context.Vfs.CurrentLocation;

        foreach (var node in context.Vfs.List(path, recurse, filter, filesOnly, directoriesOnly))
        {
            yield return node;
        }
    }
}
