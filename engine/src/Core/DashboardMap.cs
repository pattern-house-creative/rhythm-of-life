using Knowledge.Adapters;

namespace Knowledge.Relationships;

// A map part's focused sky (docs/dev/screens.md 10.4): the stars of one reader's whole map that meet a focus, read on
// request through that reader's own view (RelationshipHost.Readable) and kept nowhere. Before it matches, walks, ranks,
// counts or picks links, it keeps only the stars whose provider the map shows (AdapterRegistry.Shows, as the explorer
// and app_graph do) and the links between two kept stars that aren't disputed, so a focus narrows what the main sky can
// show and never widens it. The answer is app_graph's page, ranked as its overview ranks stars and paged by its cursor,
// with `Matched`, the count of every match.
public sealed class DashboardMap(RelationshipHost host, AdapterRegistry adapters)
{
    public async Task<FocusedMap> Read(Guid principal, MapFocus focus, string? cursor, CancellationToken ct)
    {
        var offset = Offset(cursor);
        var (view, config) = await host.Readable(principal, ct);
        return Focused(view, adapters.Shows, focus, config.GraphNodeLimit, offset, TimeSpan.FromSeconds(config.FreshnessSeconds));
    }

    // app_graph's cursor, o:<offset>; ArgumentException when it is not one.
    public static int Offset(string? cursor)
    {
        var query = new GraphQuery(Cursor: cursor);
        query.Validate();
        return query.Offset;
    }

    public static FocusedMap Focused(SnapshotView view, Func<string, bool> shows, MapFocus focus, int nodeLimit, int offset, TimeSpan freshness)
    {
        var f = focus.Normalized();
        var records = view.Records.Where(r => shows(r.Key.Provider)).GroupBy(r => r.Key.GraphId, StringComparer.Ordinal)
            .Select(g => g.First()).ToDictionary(r => r.Key);
        var links = view.Links.Where(l => l.Verification != "disputed" && records.ContainsKey(l.Source) && records.ContainsKey(l.Target)).ToArray();
        var degree = links.SelectMany(l => new[] { l.Source, l.Target }).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var near = f.Near is { } center ? Near(records.Values, links, center.Star, center.Depth ?? 1) : null;
        bool Contains(string? text) => text?.Contains(f.Query!, StringComparison.OrdinalIgnoreCase) == true;
        bool Meets(DiscoveryRecord r) =>
            (f.Query is null || Contains(r.Title) || Contains(r.Overview) || r.Groups?.Any(g => Contains(g.Label)) == true)
            && (near is null || near.Contains(r.Key))
            && (f.Types is null || f.Types.Contains(GraphProjection.Type(r.Kind)))
            && (f.Providers is null || f.Providers.Contains(Source(r)))
            && (f.Groups is null || r.Groups?.Any(g => f.Groups.Contains(g.Id)) == true)
            && (f.Owners is null || f.Owners.Contains(r.Key.Subject));
        // Ranked as the overview ranks stars: memories and what they cite first, then a replaced memory (SB-P4-11), then
        // documents, then themes, then the rest; within each, most linked, then newest, then by graphId.
        var memories = records.Values.Where(r => r.Kind is "memory" or "note" && r.ReplacedBy is null).Select(r => r.Key).ToHashSet();
        var cited = links.Where(l => memories.Contains(l.Source) && l.Verification == "verified").Select(l => l.Target).ToHashSet();
        int Tier(DiscoveryRecord r) => r.ReplacedBy is not null ? 1
            : r.Kind is "memory" or "note" || cited.Contains(r.Key) ? 0 : r.Kind == "document" ? 2 : r.Kind == "theme" ? 3 : 4;
        var matched = records.Values.Where(Meets).OrderBy(Tier).ThenByDescending(r => degree.GetValueOrDefault(r.Key))
            .ThenByDescending(r => r.CreatedAt ?? r.ModifiedAt).ThenBy(r => r.Key.GraphId, StringComparer.Ordinal).ToArray();
        var shown = matched.Skip(offset).Take(nodeLimit).ToArray();
        var keys = shown.Select(r => r.Key).ToHashSet();
        var drawn = links.Where(l => keys.Contains(l.Source) && keys.Contains(l.Target) && (f.Links is null || f.Links.Contains(l.Verification)))
            .OrderBy(l => l.Verification == "verified" ? 0 : l.Verification == "inferred" ? 1 : 2)
            .ThenByDescending(l => degree[l.Source] + degree[l.Target]).ThenBy(l => l.Id, StringComparer.Ordinal)
            .Take(nodeLimit * 2).ToArray();
        var page = GraphProjection.Create(shown, drawn, shows, [new(CatalogStars.Provider,
            $"Scheduled-refresh snapshot, each record confirmed within the last {freshness.TotalMinutes:0} minutes. "
            + $"Showing {shown.Length} of {matched.Length} records matching the focus ({records.Count} on the map in all).")]);
        return new(page with { nextCursor = offset + nodeLimit < matched.Length ? $"o:{offset + nodeLimit}" : null }, matched.Length);
    }

    // The star and every star within `depth` links of it, in either direction, over the links the sky draws; none when
    // the star isn't on the map.
    static HashSet<ResourceKey> Near(IEnumerable<DiscoveryRecord> records, IReadOnlyList<DiscoveredLink> links, string star, int depth)
    {
        var seen = new HashSet<ResourceKey>();
        if (records.FirstOrDefault(r => r.Key.GraphId == star) is not { } center) return seen;
        var around = new Dictionary<ResourceKey, List<ResourceKey>>();
        foreach (var l in links)
            foreach (var (from, to) in new[] { (l.Source, l.Target), (l.Target, l.Source) })
            {
                if (!around.TryGetValue(from, out var next)) around[from] = next = [];
                next.Add(to);
            }
        seen.Add(center.Key);
        var frontier = new List<ResourceKey> { center.Key };
        for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
            frontier = frontier.SelectMany(k => around.GetValueOrDefault(k) ?? []).Where(seen.Add).ToList();
        return seen;
    }

    // A star's source (10.4), as the app's starSource works it out: a data star's provider (its account is
    // data:<provider>), a record saved in Rhythm's own provider (its account), else the star's own provider.
    public static string Source(DiscoveryRecord r) =>
        CatalogStars.IsData(r.Key) ? r.Key.Account["data:".Length..]
        : r.Key.Provider == CatalogStars.Provider && GraphProjection.Type(r.Kind) == "document" ? r.Key.Account
        : r.Key.Provider;
}

// One page of a focused map, and how many stars match in all.
public sealed record FocusedMap(GraphPage Page, int Matched);
