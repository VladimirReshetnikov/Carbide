using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarbidePwsh.Cmdlets.Json;

public sealed class ConvertFromJsonCommand : Cmdlet
{
    public override string Name => "ConvertFrom-Json";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        string? text = binding.GetValue<string>("InputObject", 0, null);
        if (text == null && input != null)
        {
            text = string.Join("\n", input.Select(Runtime.Coercion.FormatAsString));
        }
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var node = JsonNode.Parse(text);
        yield return FromJsonNode(node);
    }

    public static object? FromJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonArray arr:
            {
                var list = new List<object?>();
                foreach (var el in arr) list.Add(FromJsonNode(el));
                return list.ToArray();
            }
            case JsonObject obj:
            {
                var dict = new OrderedDictionary();
                foreach (var kv in obj) dict[kv.Key] = FromJsonNode(kv.Value);
                return dict;
            }
            case JsonValue val:
            {
                if (val.TryGetValue<bool>(out var b)) return b;
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<long>(out var l)) return l;
                if (val.TryGetValue<double>(out var d)) return d;
                if (val.TryGetValue<string>(out var s)) return s;
                return val.ToString();
            }
            default:
                return null;
        }
    }
}
