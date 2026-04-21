using System.Collections.Specialized;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Shape;

public sealed class GroupObjectCommand : Cmdlet
{
    public override string Name => "Group-Object";
    public override IEnumerable<string> Aliases => new[] { "group" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input == null) yield break;

        var property = binding.GetValue<string>("Property", 0, null);

        object? KeyOf(object? item)
        {
            if (property == null) return item;
            try { return context.Types.GetInstanceMember(item!, property, Errors.SourceLocation.None); }
            catch { return null; }
        }

        var groups = new Dictionary<string, (object? key, List<object?> items)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in input)
        {
            var key = KeyOf(item);
            var keyText = Coercion.FormatAsString(key);
            if (!groups.TryGetValue(keyText, out var g))
            {
                g = (key, new List<object?>());
                groups[keyText] = g;
            }
            g.items.Add(item);
        }

        foreach (var (keyText, (key, items)) in groups.OrderByDescending(kv => kv.Value.items.Count))
        {
            var o = new OrderedDictionary();
            o["Count"] = items.Count;
            o["Name"] = keyText;
            o["Group"] = items.ToArray();
            yield return o;
        }
    }
}
