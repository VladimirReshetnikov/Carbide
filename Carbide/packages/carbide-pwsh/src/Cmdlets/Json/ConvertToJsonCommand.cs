using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarbidePwsh.Cmdlets.Json;

public sealed class ConvertToJsonCommand : Cmdlet
{
    public override string Name => "ConvertTo-Json";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var compress = binding.HasSwitch("Compress");
        var inputObject = binding.GetNamedRaw("InputObject");
        object? payload;
        if (inputObject is not null) payload = inputObject;
        else if (input != null)
        {
            var items = input.ToArray();
            payload = items.Length switch
            {
                0 => null,
                1 => items[0],
                _ => items,
            };
        }
        else payload = null;

        var opts = new JsonSerializerOptions { WriteIndented = !compress };
        var jsonNode = ToJsonNode(payload);
        var json = jsonNode?.ToJsonString(opts) ?? "null";
        yield return json;
    }

    public static JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case int i: return JsonValue.Create(i);
            case long l: return JsonValue.Create(l);
            case double d: return JsonValue.Create(d);
            case float f: return JsonValue.Create(f);
            case decimal m: return JsonValue.Create(m);
            case char c: return JsonValue.Create(c.ToString());
            case DateTime dt: return JsonValue.Create(dt.ToString("O"));
            case IDictionary dict:
            {
                var obj = new JsonObject();
                foreach (DictionaryEntry kv in dict)
                {
                    obj[kv.Key?.ToString() ?? ""] = ToJsonNode(kv.Value);
                }
                return obj;
            }
            case IEnumerable en when value is not string:
            {
                var arr = new JsonArray();
                foreach (var item in en) arr.Add(ToJsonNode(item));
                return arr;
            }
            default:
            {
                // POCO: reflect public readable properties.
                var t = value.GetType();
                if (t.IsPrimitive) return JsonValue.Create(value.ToString());
                var obj = new JsonObject();
                foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    try { obj[prop.Name] = ToJsonNode(prop.GetValue(value)); }
                    catch { /* skip */ }
                }
                return obj;
            }
        }
    }
}
