using System.Collections.Specialized;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Shape;

public sealed class MeasureObjectCommand : Cmdlet
{
    public override string Name => "Measure-Object";
    public override IEnumerable<string> Aliases => new[] { "measure" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input == null) yield break;

        var wantSum = binding.HasSwitch("Sum");
        var wantAverage = binding.HasSwitch("Average");
        var wantMin = binding.HasSwitch("Minimum");
        var wantMax = binding.HasSwitch("Maximum");
        var property = binding.GetValue<string>("Property", 0, null);

        int count = 0;
        double sum = 0;
        double? min = null;
        double? max = null;

        foreach (var item in input)
        {
            count++;
            if (!wantSum && !wantAverage && !wantMin && !wantMax) continue;

            double value;
            object? source = item;
            if (property != null)
            {
                try { source = context.Types.GetInstanceMember(item!, property, Errors.SourceLocation.None); }
                catch { source = null; }
            }
            try { value = Coercion.ToDouble(source); }
            catch { continue; }
            sum += value;
            if (!min.HasValue || value < min.Value) min = value;
            if (!max.HasValue || value > max.Value) max = value;
        }

        var result = new OrderedDictionary();
        result["Count"] = count;
        if (wantSum) result["Sum"] = sum;
        if (wantAverage) result["Average"] = count == 0 ? 0 : sum / count;
        if (wantMin) result["Minimum"] = min;
        if (wantMax) result["Maximum"] = max;
        if (property != null) result["Property"] = property;

        yield return result;
    }
}
