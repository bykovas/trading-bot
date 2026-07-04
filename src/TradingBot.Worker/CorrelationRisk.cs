namespace TradingBot.Worker;

// Pure configuration resolver for the correlation-risk layer. It only translates a
// pair into its correlation group and answers "is this group high-beta?" — it makes
// no decisions and holds no state, so the actual BUY-rejection logic can stay inside
// DryRunPortfolio.Apply next to the other risk checks (no parallel risk engine).
internal static class CorrelationRiskResolver
{
    public const string UngroupedPrefix = "UNGROUPED:";

    // pair -> group name, built once from CorrelationRiskOptions.Groups. Case-insensitive
    // so config casing never silently drops a pair out of its group.
    public static IReadOnlyDictionary<string, string> BuildPairToGroup(CorrelationRiskOptions options)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, pairs) in options.Groups)
        {
            if (pairs is null)
            {
                continue;
            }

            foreach (var pair in pairs)
            {
                if (!string.IsNullOrWhiteSpace(pair))
                {
                    map[pair.Trim()] = group;
                }
            }
        }

        return map;
    }

    public static ISet<string> BuildHighBetaGroups(CorrelationRiskOptions options)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in options.HighBetaGroups)
        {
            if (!string.IsNullOrWhiteSpace(group))
            {
                set.Add(group.Trim());
            }
        }

        return set;
    }

    // A pair absent from every configured group becomes an implicit singleton group
    // "UNGROUPED:<PAIR>" so config drift can never bypass the layer.
    public static string ResolveGroup(IReadOnlyDictionary<string, string> pairToGroup, string pair) =>
        pairToGroup.TryGetValue(pair, out var group)
            ? group
            : $"{UngroupedPrefix}{pair.Trim().ToUpperInvariant()}";

    // Ungrouped singletons are ALWAYS high-beta; a named group is high-beta only when
    // it is listed in HighBetaGroups.
    public static bool IsHighBeta(ISet<string> highBetaGroups, string group) =>
        group.StartsWith(UngroupedPrefix, StringComparison.Ordinal) || highBetaGroups.Contains(group);

    // Universe pairs that are not in any configured group (each treated as a high-beta
    // singleton). Used for the one-time startup warning.
    public static IReadOnlyList<string> UngroupedPairs(CorrelationRiskOptions options, IEnumerable<string> universePairs)
    {
        var map = BuildPairToGroup(options);
        return universePairs
            .Where(pair => !string.IsNullOrWhiteSpace(pair) && !map.ContainsKey(pair.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
