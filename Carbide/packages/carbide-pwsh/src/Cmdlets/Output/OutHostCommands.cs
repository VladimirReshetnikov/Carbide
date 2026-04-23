using CarbidePwsh.Host;

namespace CarbidePwsh.Cmdlets.Output;

public sealed class OutHostCommand : Cmdlet
{
    public override string Name => "Out-Host";
    public override IEnumerable<string> Aliases => new[] { "oh" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input is null)
            yield break;

        foreach (var item in input)
        {
            var text = OutputFormatter.Format(item);
            if (text.Length == 0)
                continue;
            context.Output.WriteLine(text);
        }
    }
}

public sealed class OutDefaultCommand : Cmdlet
{
    public override string Name => "Out-Default";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input is null)
            yield break;

        foreach (var item in input)
        {
            var text = OutputFormatter.Format(item);
            if (text.Length == 0)
                continue;
            context.Output.WriteLine(text);
        }
    }
}
