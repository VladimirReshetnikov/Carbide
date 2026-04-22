using CarbidePwsh.Errors;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

public sealed class GetContentCommand : Cmdlet
{
    public override string Name => "Get-Content";
    public override IEnumerable<string> Aliases => new[] { "cat", "gc", "type" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Get-Content requires a -Path argument.");
        var raw = binding.HasSwitch("Raw");

        var node = context.Vfs.Resolve(path)
            ?? throw new PwshRuntimeException($"Cannot find path '{path}'.");
        if (node is not VfsFile f)
            throw new PwshRuntimeException($"'{node.AbsolutePath}' is not a file.");

        var text = f.ReadText();
        if (raw) { yield return text; yield break; }

        // Default: split lines.
        var lines = text.Split('\n');
        // Trim trailing empty line if the text ended with a newline.
        int stop = lines.Length;
        if (stop > 0 && lines[stop - 1].Length == 0) stop--;
        for (int i = 0; i < stop; i++)
        {
            var line = lines[i];
            if (line.EndsWith('\r')) line = line[..^1];
            yield return line;
        }
    }
}
