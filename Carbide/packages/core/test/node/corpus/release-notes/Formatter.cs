namespace Corp.Release;

public static class Formatter
{
    public static string Render(IEnumerable<Change> changes)
    {
        var grouped = changes
            .GroupBy(static change => change.Area)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var priorities = group
                    .OrderBy(static change => Weight(change.Kind))
                    .ThenBy(static change => change.Description, StringComparer.Ordinal)
                    .Select(static change => $"- [{change.Kind}] {change.Description}");

                return $"## {group.Key}\n{string.Join("\n", priorities)}";
            });

        return string.Join("\n\n", grouped);
    }

    private static int Weight(ChangeKind kind) => kind switch
    {
        ChangeKind.Security => 0,
        ChangeKind.Breaking => 1,
        ChangeKind.Feature => 2,
        ChangeKind.Fix => 3,
        _ => 10,
    };
}
