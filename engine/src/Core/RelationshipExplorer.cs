using System.Text.Json.Serialization;

namespace Knowledge.Relationships;

// The readable snapshot and each link's first-seen time, as one reader's grants allow.
public sealed record SnapshotView(IReadOnlyList<DiscoveryRecord> Records, IReadOnlyList<DiscoveredLink> Links,
    IReadOnlyDictionary<string, DateTimeOffset> LinkAdded);

// What opens a star's original: the record an adapter lists (its externalId at that provider, and url when it has one),
// or a record saved in Rhythm (resourceId, for get_resource). A data star names the subject and provider whose
// measurements it gathers.
public sealed record StarOpen(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? externalId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? url = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? resourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? subject = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? provider = null);
public sealed record ExploreStar(string graphId, string title, string type, string provider, string owner, string scope,
    string overview, IReadOnlyList<GraphGroup> groups, DateTimeOffset? modifiedAt, int linkCount, StarOpen open,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? change = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GraphTruth? truth = null);
public sealed record ExploreEvidence(string excerpt, string locator);
public sealed record ExploreLink(string source, string target, string type, string verification, bool directed,
    IReadOnlyList<ExploreEvidence> evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? addedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? sourceTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? targetTitle = null);
public sealed record ExplorePage(IReadOnlyList<ExploreStar> stars, IReadOnlyList<ExploreLink> links, bool truncated);
public sealed record ChangesPage(DateTimeOffset since, IReadOnlyList<ExploreStar> stars, IReadOnlyList<ExploreLink> links, bool truncated);

// Bounded views over one reader's snapshot for harnesses: a neighbourhood or search, and what changed lately.
// Only records the app's map can show (`shows`, AdapterRegistry.Shows) take part, so every graphId here is one the app
// can open.
public sealed class GraphExplorer
{
    public const int MaxStars = 60, MaxChanges = 100, Evidence = 3, TextLength = 300;
    public static readonly TimeSpan ChangeWindow = TimeSpan.FromDays(30);
    readonly SnapshotView view;
    readonly Dictionary<ResourceKey, DiscoveryRecord> records;
    readonly Dictionary<string, DiscoveryRecord> byGraphId;
    readonly Dictionary<ResourceKey, int> degree = [];
    readonly Dictionary<ResourceKey, HashSet<ResourceKey>> around = [];
    readonly DiscoveredLink[] links;

    public GraphExplorer(SnapshotView view, Func<string, bool> shows)
    {
        this.view = view;
        records = view.Records.Where(r => shows(r.Key.Provider)).ToDictionary(r => r.Key);
        byGraphId = records.Values.ToDictionary(r => r.Key.GraphId, StringComparer.Ordinal);
        links = view.Links.Where(l => records.ContainsKey(l.Source) && records.ContainsKey(l.Target)).ToArray();
        foreach (var l in links)
            foreach (var (from, to) in new[] { (l.Source, l.Target), (l.Target, l.Source) })
            {
                degree[from] = degree.GetValueOrDefault(from) + 1;
                if (!around.TryGetValue(from, out var near)) around[from] = near = [];
                near.Add(to);
            }
    }

    public static void Validate(string? query, string? star, int depth, int limit)
    {
        if (query is not null && (query.Trim().Length == 0 || query.Length > 200)) throw new ArgumentException("query is 1–200 characters");
        if (star is not null && (star.Length is 0 or > 1000)) throw new ArgumentException("star is a graphId of at most 1000 characters");
        if (depth is < 1 or > 2) throw new ArgumentException("depth must be 1 or 2");
        if (limit is < 1 or > MaxStars) throw new ArgumentException($"limit must be 1–{MaxStars}");
    }
    public static void Validate(DateTimeOffset since, int limit, DateTimeOffset now)
    {
        if (since < now - ChangeWindow - TimeSpan.FromMinutes(5) || since > now.AddMinutes(5)) throw new ArgumentException("since must be within the last 30 days");
        if (limit is < 1 or > MaxChanges) throw new ArgumentException($"limit must be 1–{MaxChanges}");
    }

    static DateTimeOffset Newest(DiscoveryRecord r) => r.ModifiedAt ?? r.CreatedAt ?? r.RetrievedAt;
    // A replaced memory (SB-P4-11) after every current star, so it never takes one's place within the limit.
    IEnumerable<DiscoveryRecord> Rank(IEnumerable<DiscoveryRecord> rows) => rows.OrderBy(r => r.ReplacedBy is not null)
        .ThenByDescending(r => degree.GetValueOrDefault(r.Key)).ThenByDescending(Newest).ThenBy(r => r.Key.GraphId, StringComparer.Ordinal);
    IEnumerable<DiscoveredLink> Strongest(IEnumerable<DiscoveredLink> rows) => rows
        .OrderBy(l => l.Verification == "verified" ? 0 : l.Verification == "inferred" ? 1 : 2)
        .ThenByDescending(l => degree[l.Source] + degree[l.Target]).ThenBy(l => l.Id, StringComparer.Ordinal);

    // Seeds are `star`, or the stars whose title or overview contains `query`; with neither, every star.
    // Neighbours within `depth` hops (either direction) follow their seeds, nearest first; with both a star and
    // a query, only neighbours matching the query are shown. Within each step, most-linked then newest first.
    public (IReadOnlyList<DiscoveryRecord> Stars, IReadOnlyList<DiscoveredLink> Links, bool Truncated) Explore(
        string? query, string? star, int depth, int limit)
    {
        bool Matches(DiscoveryRecord r) => query is null || r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || r.Overview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        List<DiscoveryRecord> ordered;
        if (star is null && query is null) ordered = Rank(records.Values).ToList();
        else
        {
            ordered = star is not null
                ? byGraphId.TryGetValue(star, out var center) ? [center] : []
                : Rank(records.Values.Where(Matches)).ToList();
            var seen = ordered.Select(r => r.Key).ToHashSet();
            var frontier = seen.ToList();
            for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
            {
                var next = frontier.SelectMany(k => around.GetValueOrDefault(k) ?? []).Where(seen.Add).Select(k => records[k]).ToList();
                ordered.AddRange(Rank(next.Where(r => star is null || Matches(r))));
                frontier = next.Select(r => r.Key).ToList();
            }
        }
        var shown = ordered.Take(limit).ToArray();
        var keys = shown.Select(r => r.Key).ToHashSet();
        var among = Strongest(links.Where(l => keys.Contains(l.Source) && keys.Contains(l.Target))).ToArray();
        return (shown, among.Take(limit * 2).ToArray(), ordered.Count > limit || among.Length > limit * 2);
    }

    // Stars created or modified at their source since `since` ("added" or "changed"), and links first seen since
    // then, each newest first.
    public (IReadOnlyList<(DiscoveryRecord Record, string Change)> Stars, IReadOnlyList<DiscoveredLink> Links, bool Truncated) Changes(
        DateTimeOffset since, int limit)
    {
        static DateTimeOffset? Latest(DiscoveryRecord r) => r.CreatedAt is { } c && (r.ModifiedAt is not { } m || c > m) ? c : r.ModifiedAt;
        var stars = records.Values.Where(r => Latest(r) >= since).OrderByDescending(Latest)
            .ThenBy(r => r.Key.GraphId, StringComparer.Ordinal)
            .Select(r => (r, r.CreatedAt >= since ? "added" : "changed")).ToList();
        var added = links.Where(l => view.LinkAdded.TryGetValue(l.Id, out var at) && at >= since)
            .OrderByDescending(l => view.LinkAdded[l.Id]).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();
        return (stars.Take(limit).ToList(), added.Take(limit).ToList(), stars.Count > limit || added.Count > limit);
    }

    // Saved records among `stars` whose resource id the caller may look up (data stars have none).
    public static IEnumerable<ResourceKey> Saved(IEnumerable<DiscoveryRecord> stars) => stars.Select(r => r.Key)
        .Where(k => k.Provider == CatalogStars.Provider && !CatalogStars.IsData(k) && k.ExternalId.StartsWith(k.Subject + ":", StringComparison.Ordinal));

    public ExploreStar Star(DiscoveryRecord r, IReadOnlyDictionary<ResourceKey, Guid> saved, string? change = null)
    {
        var url = RelationshipDiscovery.CanonicalUrl(r.Url) is not null ? r.Url : null;
        var open = r.Key.Provider != CatalogStars.Provider ? new StarOpen(externalId: r.Key.ExternalId, url: url)
            : CatalogStars.IsData(r.Key) ? new StarOpen(subject: r.Key.Subject, provider: r.Key.Account["data:".Length..])
            : new StarOpen(resourceId: saved.TryGetValue(r.Key, out var id) ? id : null, subject: r.Key.Subject, provider: r.Key.Account);
        return new(r.Key.GraphId, r.Title, GraphProjection.Type(r.Kind), r.Key.Provider, r.Key.Subject, r.Scope, r.Overview ?? "",
            r.Groups?.Select(g => new GraphGroup(g.Id, g.Label)).ToArray() ?? [], r.ModifiedAt ?? r.CreatedAt,
            degree.GetValueOrDefault(r.Key), open, change, r.ReplacedBy is { } by ? new GraphTruth("replaced", by) : null);
    }
    public ExploreLink Link(DiscoveredLink l, bool changes = false) => new(l.Source.GraphId, l.Target.GraphId, l.Type, l.Verification, l.Directed,
        l.Evidence.Take(Evidence).Select(e => new ExploreEvidence(Cut(e.Excerpt), Cut(e.Locator))).ToArray(),
        changes ? view.LinkAdded[l.Id] : null, changes ? records[l.Source].Title : null, changes ? records[l.Target].Title : null);
    static string Cut(string text)
    {
        if (text.Length <= TextLength) return text;
        var end = TextLength - 1;
        return text[..(char.IsHighSurrogate(text[end - 1]) ? end - 1 : end)] + "…";
    }
}
