using System.Collections.Specialized;

namespace CarbidePwsh.Cmdlets.Shape;

public sealed class SelectObjectCommand : Cmdlet
{
    public override string Name => "Select-Object";
    public override IEnumerable<string> Aliases => new[] { "select" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input == null) yield break;

        int? first = binding.GetOrDefault<int?>("First", null);
        int? last = binding.GetOrDefault<int?>("Last", null);
        int? skip = binding.GetOrDefault<int?>("Skip", null);
        string[]? properties = null;
        if (binding.Named.TryGetValue("Property", out var pv) ||
            (binding.Positional.Count > 0 && pv is null))
        {
            pv ??= binding.Positional.Count > 0 ? binding.Positional[0] : null;
        }
        if (pv is string s) properties = new[] { s };
        else if (pv is System.Collections.IEnumerable e && pv is not string)
        {
            var list = new List<string>();
            foreach (var x in e) list.Add(x?.ToString() ?? "");
            properties = list.ToArray();
        }

        IEnumerable<object?> src = input;
        if (skip.HasValue) src = src.Skip(skip.Value);
        if (first.HasValue) src = src.Take(first.Value);
        if (last.HasValue)
        {
            var arr = src.ToArray();
            src = arr.Skip(Math.Max(0, arr.Length - last.Value));
        }

        if (properties == null || properties.Length == 0)
        {
            foreach (var item in src) yield return item;
            yield break;
        }

        foreach (var item in src)
        {
            var projection = new OrderedDictionary();
            foreach (var p in properties)
            {
                try
                {
                    var val = context.Types.GetInstanceMember(item!, p, Errors.SourceLocation.None);
                    projection[p] = val;
                }
                catch
                {
                    projection[p] = null;
                }
            }
            yield return projection;
        }
    }
}
