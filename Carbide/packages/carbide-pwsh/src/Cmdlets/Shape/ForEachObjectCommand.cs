using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Shape;

public sealed class ForEachObjectCommand : Cmdlet
{
    public override string Name => "ForEach-Object";
    public override IEnumerable<string> Aliases => new[] { "foreach", "%" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input == null) yield break;

        var sb = binding.GetValue<object?>("Process", 0, null) as ScriptBlock
            ?? throw new PwshRuntimeException("ForEach-Object requires a script block argument.");

        foreach (var item in input)
        {
            var result = sb.InvokeForPipelineItem(item);
            if (result is null) continue;
            foreach (var emitted in Pipeline.ExpressionToEnumerable(result))
                yield return emitted;
        }
    }
}
