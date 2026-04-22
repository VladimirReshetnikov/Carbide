using System.Collections;
using System.Globalization;
using System.Text;
using CarbidePwsh.Runtime;
using CarbideShellCore.Vfs;

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
        if (value is VfsNode[] vfsArr) return FormatVfsTable(vfsArr);
        if (value is IDictionary dict) return FormatDictionary(dict);
        if (value is Array arr)
        {
            // Homogeneous VfsNode arrays get the table treatment.
            if (arr.Length > 0 && arr.GetValue(0) is VfsNode)
            {
                var list = new List<VfsNode>();
                foreach (var item in arr) if (item is VfsNode n) list.Add(n);
                if (list.Count == arr.Length) return FormatVfsTable(list.ToArray());
            }
            return FormatEnumerable(arr);
        }
        if (value is VfsNode single) return FormatVfsTable(new[] { single });
        if (value is IEnumerable en && value is not string) return FormatEnumerable(en);
        if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }

    private static string FormatVfsTable(VfsNode[] nodes)
    {
        if (nodes.Length == 0) return "";
        // Columns: Mode LastWriteTimeUtc Length Name
        var modeHdr = "Mode"; var dateHdr = "LastWriteTimeUtc"; var lenHdr = "Length"; var nameHdr = "Name";
        int modeW = modeHdr.Length, dateW = dateHdr.Length, lenW = lenHdr.Length;
        var rows = new List<(string mode, string date, string len, string name)>();
        foreach (var n in nodes)
        {
            var mode = n.Mode;
            var date = n.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var len = n is VfsFile f ? f.Length.ToString(CultureInfo.InvariantCulture) : "";
            rows.Add((mode, date, len, n.Name));
            if (mode.Length > modeW) modeW = mode.Length;
            if (date.Length > dateW) dateW = date.Length;
            if (len.Length > lenW) lenW = len.Length;
        }
        var sb = new StringBuilder();
        sb.Append(modeHdr.PadRight(modeW)).Append(' ')
          .Append(dateHdr.PadRight(dateW)).Append(' ')
          .Append(lenHdr.PadLeft(lenW)).Append(' ')
          .Append(nameHdr).AppendLine();
        sb.Append(new string('-', modeW)).Append(' ')
          .Append(new string('-', dateW)).Append(' ')
          .Append(new string('-', lenW)).Append(' ')
          .Append(new string('-', nameHdr.Length)).AppendLine();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            sb.Append(r.mode.PadRight(modeW)).Append(' ')
              .Append(r.date.PadRight(dateW)).Append(' ')
              .Append(r.len.PadLeft(lenW)).Append(' ')
              .Append(r.name);
            if (i + 1 < rows.Count) sb.AppendLine();
        }
        return sb.ToString();
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
