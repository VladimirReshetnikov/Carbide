using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class GetChildItemCommand : Cmdlet
{
    public override string Name => "Get-ChildItem";
    public override IEnumerable<string> Aliases => new[] { "dir", "ls", "gci" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null) ?? context.Vfs.CurrentLocation;
        var filter = binding.GetOrDefault<string>("Filter", null);
        var recurse = binding.HasSwitch("Recurse");
        var filesOnly = binding.HasSwitch("File");
        var directoriesOnly = binding.HasSwitch("Directory");

        foreach (var node in context.Vfs.List(path, recurse, filter, filesOnly, directoriesOnly))
        {
            yield return node;
        }
    }
}
