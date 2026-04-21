namespace CarbidePwsh.Cmdlets.Shape;

public sealed class SortObjectCommand : Cmdlet
{
    public override string Name => "Sort-Object";
    public override IEnumerable<string> Aliases => new[] { "sort" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input == null) return Enumerable.Empty<object?>();

        var descending = binding.HasSwitch("Descending");
        var property = binding.GetOrDefault<string>("Property", null);
        var unique = binding.HasSwitch("Unique");

        object? KeySelector(object? item)
        {
            if (property == null) return item;
            try { return context.Types.GetInstanceMember(item!, property, Errors.SourceLocation.None); }
            catch { return null; }
        }

        var items = input.ToList();
        items.Sort((a, b) =>
        {
            var ka = KeySelector(a);
            var kb = KeySelector(b);
            var cmp = Compare(ka, kb);
            return descending ? -cmp : cmp;
        });

        if (!unique) return items;
        var seen = new HashSet<object?>(new KeyEqualityComparer());
        var uniq = new List<object?>();
        foreach (var item in items)
        {
            if (seen.Add(KeySelector(item))) uniq.Add(item);
        }
        return uniq;
    }

    private static int Compare(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a is IComparable ca && a.GetType() == b.GetType()) return ca.CompareTo(b);
        return string.Compare(
            Runtime.Coercion.FormatAsString(a),
            Runtime.Coercion.FormatAsString(b),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class KeyEqualityComparer : IEqualityComparer<object?>
    {
        public new bool Equals(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return x.Equals(y);
        }
        public int GetHashCode(object? obj) => obj?.GetHashCode() ?? 0;
    }
}
