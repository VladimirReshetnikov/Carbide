using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FeatureFlags;

public sealed record FlagState(string Name, bool Enabled, string Ring);

public static class FlagService
{
    public static IReadOnlyList<FlagState> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .EnumerateArray()
            .Select(static element => new FlagState(
                element.GetProperty("name").GetString()!,
                element.GetProperty("enabled").GetBoolean(),
                element.GetProperty("ring").GetString()!))
            .ToArray();
    }

    public static string Evaluate(IReadOnlyList<FlagState> flags, string environment)
    {
        var active = flags
            .Where(flag => flag.Enabled && (flag.Ring == "all" || flag.Ring == environment))
            .OrderBy(static flag => flag.Name)
            .Select(static flag => flag.Name)
            .ToArray();

        return active.Length == 0 ? "(none)" : string.Join("+", active);
    }
}
