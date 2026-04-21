using System.Collections;
using System.Globalization;
using System.Text;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Host;

/// <summary>
/// Turns an evaluated value into a display string for the REPL. Phase 1 handles the shapes we
/// actually produce: primitives, strings, arrays, hashtables, and arbitrary objects via
/// <c>ToString()</c>. Rich formatting (Format-Table, Format-List) lands in Phase 2.
/// </summary>
public static class OutputFormatter
{
    public static string Format(object? value)
    {
        if (value == null) return "";
        if (value is bool b) return b ? "True" : "False";
        if (value is string s) return s;
        if (value is char c) return c.ToString();
        if (value is IDictionary dict) return FormatDictionary(dict);
        if (value is Array arr) return FormatEnumerable(arr);
        if (value is IEnumerable en && value is not string) return FormatEnumerable(en);
        if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }

    private static string FormatEnumerable(IEnumerable en)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var item in en)
        {
            if (!first) sb.Append('\n');
            sb.Append(Format(item));
            first = false;
        }
        return sb.ToString();
    }

    private static string FormatDictionary(IDictionary dict)
    {
        // "Name Value" table with a dashed separator, matching PowerShell's default hashtable
        // formatting in the common case.
        var names = new List<string>();
        var values = new List<string>();
        int nameWidth = "Name".Length;
        int valueWidth = "Value".Length;

        foreach (DictionaryEntry entry in dict)
        {
            var n = Format(entry.Key);
            var v = Format(entry.Value);
            names.Add(n); values.Add(v);
            if (n.Length > nameWidth) nameWidth = n.Length;
            if (v.Length > valueWidth) valueWidth = v.Length;
        }

        var sb = new StringBuilder();
        sb.Append("Name".PadRight(nameWidth)).Append(' ').Append("Value").AppendLine();
        sb.Append(new string('-', nameWidth)).Append(' ').Append(new string('-', "Value".Length)).AppendLine();
        for (int i = 0; i < names.Count; i++)
        {
            sb.Append(names[i].PadRight(nameWidth)).Append(' ').Append(values[i]);
            if (i + 1 < names.Count) sb.AppendLine();
        }
        return sb.ToString();
    }
}
