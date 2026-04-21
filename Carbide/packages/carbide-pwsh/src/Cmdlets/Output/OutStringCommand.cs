using CarbidePwsh.Host;

namespace CarbidePwsh.Cmdlets.Output;

public sealed class OutStringCommand : Cmdlet
{
    public override string Name => "Out-String";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var pieces = new List<string>();
        if (input != null)
        {
            foreach (var item in input)
                pieces.Add(OutputFormatter.Format(item));
        }
        if (binding.HasSwitch("Stream"))
        {
            foreach (var p in pieces) yield return p;
            yield break;
        }
        yield return string.Join("\n", pieces);
    }
}
