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
        // Real pwsh renders floating-point with 15 / 7 significant digits, not the .NET
        // round-trip default of 17 / 9. `10 / 3` prints as `3.33333333333333`.
        if (value is double d) return d.ToString("G15", CultureInfo.InvariantCulture);
        if (value is float f32) return f32.ToString("G7", CultureInfo.InvariantCulture);
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

    // ANSI sequences real pwsh emits for table headers: SGR 32;1 (bold green). The same
    // sequence brackets the header row and the separator row; each column's text is wrapped
    // individually to let the terminal's background flow through the inter-column space.
    private const string HdrOn = "\x1b[32;1m";
    private const string HdrOff = "\x1b[0m";

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
        // Leading blank line matches real pwsh's default table layout.
        sb.AppendLine();
        AppendColoredCell(sb, modeHdr.PadRight(modeW)); sb.Append(' ');
        AppendColoredCell(sb, dateHdr.PadRight(dateW)); sb.Append(' ');
        AppendColoredCell(sb, lenHdr.PadLeft(lenW)); sb.Append(' ');
        AppendColoredCell(sb, nameHdr); sb.AppendLine();
        AppendColoredCell(sb, new string('-', modeW)); sb.Append(' ');
        AppendColoredCell(sb, new string('-', dateW)); sb.Append(' ');
        AppendColoredCell(sb, new string('-', lenW)); sb.Append(' ');
        AppendColoredCell(sb, new string('-', nameHdr.Length)); sb.AppendLine();
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

    private static void AppendColoredCell(StringBuilder sb, string text)
        => sb.Append(HdrOn).Append(text).Append(HdrOff);

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
        // "Name Value" table with a dashed separator, matching PowerShell's default
        // hashtable formatting. Real pwsh pads the Name column to a minimum of 30 chars and
        // emits leading + trailing blank lines; we mirror that so the visual layout matches
        // what scripts and docs using `@{...}` have taught users to expect.
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
        // Real pwsh's default hashtable layout pads the Name column to 30 chars.
        if (nameWidth < 30) nameWidth = 30;

        var sb = new StringBuilder();
        sb.AppendLine();
        // Header row: real pwsh emits two adjacent colored blocks with the inter-column
        // space living INSIDE the second block (`HdrOn Name+padding HdrOff HdrOn  Value
        // HdrOff`), not between them. Matching the byte-exact layout keeps copy-paste from
        // real pwsh docs identical to carbide's output after terminal rendering.
        AppendColoredCell(sb, "Name".PadRight(nameWidth));
        AppendColoredCell(sb, " Value"); sb.AppendLine();
        // Separator row: dashes sized to the header word (NOT the column width), padded
        // with spaces to fill the column, and a literal space BETWEEN the two colored
        // blocks. This is the shape `Format-Table` emits in real pwsh 7.x.
        AppendColoredCell(sb, ("----").PadRight(nameWidth)); sb.Append(' ');
        AppendColoredCell(sb, "-----"); sb.AppendLine();
        for (int i = 0; i < names.Count; i++)
        {
            sb.Append(names[i].PadRight(nameWidth)).Append(' ').Append(values[i]);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
