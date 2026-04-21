using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Shape;

public sealed class WhereObjectCommand : Cmdlet
{
    public override string Name => "Where-Object";
    public override IEnumerable<string> Aliases => new[] { "where", "?" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input == null) yield break;

        var filter = binding.GetValue<object?>("FilterScript", 0, null);
        if (filter is not ScriptBlock sb)
            throw new PwshRuntimeException("Where-Object requires a script block argument.");

        foreach (var item in input)
        {
            var result = sb.InvokeForPipelineItem(item);
            if (Coercion.CoerceToBool(result))
                yield return item;
        }
    }
}
