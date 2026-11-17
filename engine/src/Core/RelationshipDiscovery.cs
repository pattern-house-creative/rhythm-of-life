using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knowledge.Relationships;

// Account/workspace is a source identity, not another authorization hierarchy.
public sealed record ResourceKey(string Subject, string Provider, string Account, string ExternalId)
{
    public string GraphId => string.Join(":", new[] { Provider, Account, ExternalId }.Select(Uri.EscapeDataString));
    // Namespaced provider IDs coexist with legacy ingest keys without changing their unique constraint.
    public string CatalogId => "relationship-engine:" + JsonSerializer.Serialize(new[] { Account, ExternalId });
}
// A source-native reference may state its own verification (e.g. a link the person made vs one the
// source inferred), strength and direction; otherwise exact references are verified and directed.
public sealed record Reference(ResourceKey? Target, string? Url, string Locator,
    string Excerpt = "", string NativeType = "references", bool Native = false,
    string? Verification = null, double? Strength = null, bool Directed = true);
// ContentRevision is the source revision whose body produced the content references and Embedding,
// so unchanged files are not re-read. Embedding is a derived similarity vector, never text, and EmbeddingModel
// names the model that made it (SimilaritySuggestions.ModelOf). Overview is a short excerpt shown beside the title;
// TitleRevision says which model and text produced a generated title. Reader, set only by the catalog with memory.scopes
// on, is the one subject whose readers alone can be served a star whose key names a wider one: a household save_document
// copy of a person's private original is served only to that person's readers (the copy rule, docs/dev/memory.md section
// 8), so its budgets and similarity are that person's scope's, never the household's. Null for every other star, and
// then left out of the stored snapshot, so a snapshot with scopes off is as before. MemoryKind, set only on a native
// memory's star (MemoryStars), is the memory's kind (episodic or semantic; null while it has none), which the episodic rule
// reads (LinkTypes.Allows). Null on every other star, and then left out of the stored snapshot, so no other star's
// snapshot changes.
public sealed record DiscoveryRecord(ResourceKey Key, string Scope, string Kind, string Title,
    string? Url, string? Revision, DateTimeOffset? ModifiedAt, DateTimeOffset RetrievedAt,
    IReadOnlyList<Reference> References, string Status = "current", bool ReferencesComplete = true,
    Guid? ExistingResourceId = null, DateTimeOffset? CreatedAt = null,
    string? ContentRevision = null, float[]? Embedding = null, IReadOnlyList<RecordGroup>? Groups = null,
    string? Overview = null, string? TitleRevision = null, string? EmbeddingModel = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Reader = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? MemoryKind = null)
{
    // The subject whose scope a star is budgeted and compared in: its Reader, else its key's subject. Never stored.
    [System.Text.Json.Serialization.JsonIgnore] public string ScopeSubject => Reader ?? Key.Subject;
    // On a native memory's star that a newer memory of its scope replaced, that memory's star id, set by a read that
    // serves replaced memories (RelationshipEngine.Replaced, SB-P4-11) at that read; never set by a pass, never stored.
    [System.Text.Json.Serialization.JsonIgnore] public string? ReplacedBy { get; init; }
    // On a native memory's star whose memory is a work item (migration 079), its work properties, set by a read that serves
    // them (RelationshipEngine.Work, memory.work on) at that read; never set by a pass, never stored.
    [System.Text.Json.Serialization.JsonIgnore] public MemoryWorkProps? Work { get; init; }
    // With graph.similarity.model set (D-256), a second vector made by that model with Embedding's recipe from the same
    // text, and the model's id: read by the map's similar and copy links alone (SimilaritySuggestions.MapModel), never by
    // a reader that serves stars (RelationshipEngine.Load leaves it out) or by anything else that reads Embedding. Set by
    // the pass beside the record's own vector (CatalogRelationshipSource.Tagged), kept while the content revision and the
    // map model are unchanged. Null without the setting, and then left out of the stored snapshot, so no snapshot changes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float[]? MapEmbedding { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MapEmbeddingModel { get; init; }
}
// A grouping the source itself states for a record (a workspace, folder or project), for display only.
public sealed record RecordGroup(string Id, string Label);
public sealed record DiscoveryBatch(IReadOnlyList<DiscoveryRecord> Records, bool Complete = false);
public enum SourceCheck { Available, Stale, Deleted, Revoked, Unavailable }

// Trusted, requester-bound adapters only; credentials and approved selections belong to the host.
public interface IRelationshipSource
{
    Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct);
    Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct);
}
// Optional: a source given the currently stored snapshots before Fetch can reuse work for unchanged
// records and report records that a complete listing no longer contains.
public interface IRelationshipSnapshotSource
{
    void Prime(IReadOnlyList<DiscoveryRecord> stored);
}
// A suggestion is never verified: `suggested` by default, or `inferred` when the evidence is strong
// enough to state as a source-level assertion (e.g. an identical copy).
public sealed record Suggestion(ResourceKey Source, ResourceKey Target, string Type,
    string Locator, string Excerpt, string Method, double Strength = 0.5, bool Directed = true,
    string Verification = "suggested");
public interface IRelationshipSuggestions
{
    // Only authorized bounded metadata is supplied. Implementations must be explicitly configured.
    Task<IReadOnlyList<Suggestion>> Suggest(IReadOnlyList<DiscoveryRecord> records, CancellationToken ct);
}
public sealed record LinkEvidence(ResourceKey Resource, string Locator, string Excerpt, DateTimeOffset RetrievedAt);
// Made, set only on a person- or AI-made link the map serves beside its own (made_links, SB-P4-02, D-067;
// RelationshipEngine.Readable), is that link's own id, which names it on the map, so it never shares an id with a map
// link of the same ends and type. Null on every link the rebuild writes, and then left out of the stored details, so no
// stored link changes.
public sealed record DiscoveredLink(ResourceKey Source, ResourceKey Target, string Type, string NativeType,
    string Verification, string Method, IReadOnlyList<LinkEvidence> Evidence,
    string? SourceRevision, string? TargetRevision, DateTimeOffset? SourceModifiedAt,
    double? Strength = null, bool Directed = true)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Made { get; init; }
    public string Id => Made is { } made ? "made-link:" + made.ToString("D")
        : "relationship:" + Hashing.Hash(JsonSerializer.Serialize(new[] { Source.Subject, Source.GraphId, Target.Subject, Target.GraphId, Type }));
}

public static partial class RelationshipDiscovery
{
    public const int MaximumRecords = 5000, MaximumLinks = 12000;
    public const string Household = "household";
    // A household's shared records may cite one person's documents and vice versa. Records of two
    // different people never bridge. Both endpoints are still authorized separately on every read.
    static bool Bridges(ResourceKey a, ResourceKey b) => a.Subject == b.Subject || a.Subject == Household || b.Subject == Household;
    public static void Validate(DiscoveryBatch batch)
    {
        if (batch.Records.Count > MaximumRecords) throw new ArgumentException($"Discovery batch exceeds {MaximumRecords} records");
        foreach (var r in batch.Records)
        {
            if (new[] { r.Key.Subject, r.Key.Provider, r.Key.Account, r.Key.ExternalId }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 500)
                || r.Scope is not ("shared" or "private") || r.Status is not ("current" or "deleted" or "revoked" or "missing")
                || r.Title.Length > 1000 || r.References.Count > 100 || r.RetrievedAt > DateTimeOffset.UtcNow.AddMinutes(1)
                || r.ModifiedAt > r.RetrievedAt || r.CreatedAt > r.RetrievedAt || r.Revision?.Length > 500 || r.Url?.Length > 4096
                || r.ContentRevision?.Length > 500 || r.TitleRevision?.Length > 500 || r.Overview?.Length > 1000 || r.Embedding is { Length: 0 or > 1024 } || r.Embedding?.Any(x => !float.IsFinite(x)) == true
                || r.EmbeddingModel is { Length: 0 or > 200 } || r.EmbeddingModel is not null && r.Embedding is null
                || r.MapEmbedding is { Length: 0 or > 1024 } || r.MapEmbedding?.Any(x => !float.IsFinite(x)) == true
                || r.MapEmbeddingModel is { Length: 0 or > 200 } || (r.MapEmbeddingModel is null) != (r.MapEmbedding is null)
                || r.MapEmbedding is not null && r.Embedding is null
                || r.Reader is { Length: 0 or > 500 } || r.Reader is not null && r.Key.Provider != CatalogStars.Provider
                || r.MemoryKind is not (null or LinkTypes.Episodic or LinkTypes.Semantic)
                || r.MemoryKind is not null && !(MemoryStars.IsNative(r.Key) && r.Kind == "memory")
                || r.Groups is { Count: > 20 } || r.Groups?.Any(g => g is null || string.IsNullOrWhiteSpace(g.Id) || g.Id.Length > 300
                    || string.IsNullOrWhiteSpace(g.Label) || g.Label.Length > 200) == true)
                throw new ArgumentException("Invalid discovery record");
            if (r.Key.GraphId.Length > 1000) throw new ArgumentException("Graph identity exceeds viewer budget");
            foreach (var reference in r.References)
                if (string.IsNullOrWhiteSpace(reference.Locator) || reference.Locator.Length > 1000
                    || reference.Excerpt.Length > 1000 || reference.Url?.Length > 4096 || !TypePattern().IsMatch(reference.NativeType)
                    || reference.Verification is not (null or "verified" or "inferred" or "suggested")
                    || reference.Strength is < 0 or > 1 || reference.Strength is double.NaN)
                    throw new ArgumentException("Invalid reference evidence");
        }
        // Conflicting snapshots in one batch are a connector error, not arbitrary last-writer-wins.
        if (batch.Records.Select(r => r.Key).Distinct().Count() != batch.Records.Count)
            throw new ArgumentException("Duplicate resource identity in batch");
    }

    // Whether a reference of `source` is a link Rhythm states itself: one of its own records' (a native memory's or
    // project's star, or a catalog star). Such a link keeps the vocabulary (LinkTypes), as a suggestion does: a listed
    // type's direction, and the episodic rule by the kinds of its two ends (DiscoveryRecord.MemoryKind), so a record of no
    // kind never takes a type that needs one. A source's own link is the source's assertion, kept as stated, as its
    // verification says: Second Brain keeps the same rule for its own links, by kinds Rhythm is not sent.
    static bool Rhythms(ResourceKey source) => MemoryStars.IsNative(source) || source.Provider == CatalogStars.Provider;

    // `scoped`: the pass runs with memory.scopes on (UrlTargets).
    public static IReadOnlyList<DiscoveredLink> Discover(IReadOnlyList<DiscoveryRecord> records,
        IReadOnlyList<Suggestion>? suggestions = null, bool scoped = false)
    {
        Validate(new(records));
        var current = records.Where(r => r.Status == "current").ToDictionary(r => r.Key);
        var urls = current.Values.Where(r => CanonicalUrl(r.Url) is not null)
            .GroupBy(r => CanonicalUrl(r.Url)!).ToDictionary(g => g.Key, g => g.ToArray());
        IReadOnlyList<DiscoveryRecord> Targets(Reference reference) =>
            reference.Target is not null ? current.TryGetValue(reference.Target, out var known) ? [known] : []
            : CanonicalUrl(reference.Url) is { } url && urls.TryGetValue(url, out var matches) ? UrlTargets(matches, scoped) : [];
        var links = new List<DiscoveredLink>();
        foreach (var source in current.Values)
        foreach (var reference in source.References)
        foreach (var target in Targets(reference))
        {
            if (source.Key == target.Key || !Bridges(source.Key, target.Key)) continue;
            // Identity/link existence is verified. Native dependency/causality claims are only source assertions
            // unless the source says otherwise (a link the person made; a structural parent folder).
            var type = reference.Native ? reference.NativeType : "references";
            var directed = reference.Directed;
            // A plugin's own link on a catalog star (graph.pluginLinks, SB-P4-13) is the plugin's assertion, kept as stated.
            if (Rhythms(source.Key) && !reference.Locator.StartsWith(CatalogStars.PluginLocator, StringComparison.Ordinal))
            {
                if (!LinkTypes.Allows(type, source.MemoryKind, target.MemoryKind)) continue;
                directed = LinkTypes.Directed(type, directed);
            }
            links.Add(new(source.Key, target.Key, type, reference.NativeType,
                reference.Verification ?? (type == "references" ? "verified" : "inferred"), reference.Native ? "native" : "explicit-reference",
                [new(source.Key, reference.Locator, reference.Excerpt, source.RetrievedAt)],
                source.Revision, target.Revision, source.ModifiedAt, reference.Strength, directed));
        }
        if (suggestions?.Count > 2000) throw new ArgumentException("Too many suggestions");
        foreach (var s in suggestions ?? [])
        {
            if (!current.TryGetValue(s.Source, out var source) || !current.TryGetValue(s.Target, out var target)
                || s.Source == s.Target || !Bridges(s.Source, s.Target)) continue;
            if (!TypePattern().IsMatch(s.Type) || string.IsNullOrWhiteSpace(s.Locator) || s.Locator.Length > 1000
                || string.IsNullOrWhiteSpace(s.Excerpt) || s.Excerpt.Length > 1000 || string.IsNullOrWhiteSpace(s.Method) || s.Method.Length > 100
                || s.Verification is not ("suggested" or "inferred"))
                throw new ArgumentException("Suggestion requires bounded, located evidence and method");
            // A suggestion is Rhythm's own: the vocabulary's direction and the episodic rule, as for its records' links.
            if (!LinkTypes.Allows(s.Type, source.MemoryKind, target.MemoryKind)) continue;
            links.Add(new(s.Source, s.Target, s.Type, s.Type, s.Verification, s.Method,
                [new(s.Source, s.Locator, s.Excerpt, source.RetrievedAt)], source.Revision, target.Revision, source.ModifiedAt,
                Math.Clamp(s.Strength, 0, 1), LinkTypes.Directed(s.Type, s.Directed)));
        }
        // Deterministic evidence wins over a suggestion for the same directed key.
        return links.GroupBy(l => l.Id).Select(g => {
            var best = g.OrderBy(l => l.Verification == "verified" ? 0 : l.Verification == "inferred" ? 1 : 2).First();
            return best with { Evidence = g.Where(l => l.Verification == best.Verification)
                .SelectMany(l => l.Evidence).Distinct().OrderBy(e => e.Locator, StringComparer.Ordinal).ToArray() };
        }).OrderBy(l => l.Id, StringComparer.Ordinal).ToArray();
    }

    // The links one pass writes, at most MaximumLinks (docs/dev/memory.md section 8, Budgets). With scopes off (`catalog`
    // null) a pass past them fails for every reader, as before. With scopes on (the pass's catalog, RelationshipCatalog.
    // Scoped) each link counts in the scope whose readers alone can be served it (LinkScope), and each subject keeps at
    // most its own equal share of them (RelationshipCatalog.Share): verified links first, then inferred, then suggested,
    // each kind by id. The rest are left out and counted in `leftOut`, so a scope past its share loses only some of its own
    // links, never the rebuild, and what any reader is served never depends on how many links another scope has. A link
    // no reader can be served (LinkScope null) is left out too, uncounted: nobody loses it. A person's record that a
    // source share lets Family read (an adapter row of a shared provider, or a catalog data or document star of one,
    // whose provider CatalogStars.SourceProvider says, as the map's Shown does: catalog.Shares) counts as the
    // household's, as a copy of a shared original does (D-089), so the person's own link volume never decides which of
    // the links Family reads are kept.
    public static IReadOnlyList<DiscoveredLink> Bounded(IReadOnlyList<DiscoveredLink> links, IReadOnlyList<DiscoveryRecord> records,
        RelationshipCatalog? catalog, out int leftOut)
    {
        leftOut = 0;
        if (catalog is null)
            return links.Count <= MaximumLinks ? links : throw new InvalidOperationException("Graph edge budget exceeded");
        var scopes = new Dictionary<ResourceKey, string>();
        bool Shared(DiscoveryRecord r) => r.Reader is null && catalog.Shares is { Count: > 0 } shares
            && shares.Contains((r.Key.Subject, CatalogStars.SourceProvider(r.Key)));
        foreach (var record in records) scopes.TryAdd(record.Key, Shared(record) ? Household : record.ScopeSubject);
        var servable = links.Select(l => (Link: l, Scope: LinkScope(l, scopes))).Where(x => x.Scope is not null).ToArray();
        static int Rank(string verification) => verification == "verified" ? 0 : verification == "inferred" ? 1 : 2;
        var kept = servable.GroupBy(x => x.Scope!, StringComparer.Ordinal)
            .SelectMany(g => g.Select(x => x.Link).OrderBy(l => Rank(l.Verification)).ThenBy(l => l.Id, StringComparer.Ordinal)
                .Take(catalog.Share(MaximumLinks, g.Key)))
            .OrderBy(l => l.Id, StringComparer.Ordinal).ToArray();
        leftOut = servable.Length - kept.Length;
        return kept;
    }

    // The one scope whose readers alone can be served a link, a reader of both its ends: each end's ScopeSubject (a
    // household save_document copy of a person's private original is that person's), and of two different ones the
    // narrower, a person's before the household's (only the person's readers read both) and the household's before the
    // demo's (every reader reads DEMO rows). Two people's scopes, or an end not among `scopes`, is no one's: null.
    internal static string? LinkScope(DiscoveredLink link, IReadOnlyDictionary<ResourceKey, string> scopes)
    {
        if (!scopes.TryGetValue(link.Source, out var a) || !scopes.TryGetValue(link.Target, out var b)) return null;
        static int Width(string subject) => subject == RhythmConfig.Synthetic ? 2 : subject == Household ? 1 : 0;
        return a == b ? a : Width(a) == 0 && Width(b) == 0 ? null : Width(a) <= Width(b) ? a : b;
    }

    // The records a URL reference names. Ambiguous URLs never bridge accounts by accident: one only resolves to the one
    // record with that URL. With scopes on it is told apart within each subject instead (the one record of a subject
    // with that URL), so whether a reference links two records of one scope never depends on what another scope holds.
    public static IReadOnlyList<DiscoveryRecord> UrlTargets(IReadOnlyList<DiscoveryRecord> matches, bool scoped) =>
        !scoped ? matches.Count == 1 ? [matches[0]] : []
        : [.. matches.GroupBy(m => m.Key.Subject, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())];

    // Adapters may extract exact links without storing whole document/memory bodies.
    public static IReadOnlyList<Reference> ExtractLinks(string text)
    {
        if (text.Length > 40000) throw new ArgumentException("Bounded source text required");
        var result = UrlPattern().Matches(text).Select(m => new Reference(null,
            m.Value.TrimEnd('.', ',', ';', ')', ']', '}'), $"text:{m.Index}")).ToArray();
        if (result.Length > 100) throw new ArgumentException("Too many explicit references");
        return result;
    }
    public static string? CanonicalUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || !uri.IsDefaultPort) return null;
        // Only known Drive URL forms drop UI/query decorations. Arbitrary query strings may identify different records.
        if (uri.Host is "docs.google.com" or "drive.google.com")
        {
            var match = DrivePath().Match(uri.AbsolutePath);
            if (match.Success) return "https://drive.google.com/file/d/" + match.Groups[1].Value;
            var folder = FolderPath().Match(uri.AbsolutePath);
            if (uri.Host == "drive.google.com" && folder.Success) return DriveFolderPrefix + folder.Groups[1].Value;
            if (uri.Host == "drive.google.com" && uri.AbsolutePath == "/open")
            {
                var ids = uri.Query.TrimStart('?').Split('&').Where(p => p.StartsWith("id=", StringComparison.Ordinal)).ToArray();
                if (ids.Length == 1 && DriveId().IsMatch(ids[0][3..])) return "https://drive.google.com/file/d/" + ids[0][3..];
            }
        }
        return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    }
    const string DriveFilePrefix = "https://drive.google.com/file/d/";
    // The Drive file ID behind any recognized Docs/Sheets/Slides/file link, else null.
    public static string? DriveFileId(string? raw) => CanonicalUrl(raw) is { } url && url.StartsWith(DriveFilePrefix, StringComparison.Ordinal)
        && DriveId().IsMatch(url[DriveFilePrefix.Length..]) ? url[DriveFilePrefix.Length..] : null;
    public const string DriveFolderPrefix = "https://drive.google.com/drive/folders/";
    public static string? DriveFolderId(string? raw) => CanonicalUrl(raw) is { } url && url.StartsWith(DriveFolderPrefix, StringComparison.Ordinal)
        && DriveId().IsMatch(url[DriveFolderPrefix.Length..]) ? url[DriveFolderPrefix.Length..] : null;
    // Drive file and folder links in a document body of any size: first `max` distinct targets, in order.
    public static IReadOnlyList<Reference> ExtractDriveLinks(string text, int max)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Reference>();
        foreach (Match m in UrlPattern().Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            if (url.Length > 4096 || (DriveFileId(url) is null && DriveFolderId(url) is null) || !seen.Add(CanonicalUrl(url)!)) continue;
            result.Add(new(null, url, $"content:{m.Index}", url.Length <= 1000 ? url : ""));
            if (result.Count >= max) break;
        }
        return result;
    }
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")] private static partial Regex TypePattern();
    [GeneratedRegex("https://[^\\s<>\\\"']+")] private static partial Regex UrlPattern();
    // Docs, Sheets, Slides, Forms, Drawings and uploaded files; a published form's /d/e/ is not a file ID.
    [GeneratedRegex("^/(?:document|spreadsheets|presentation|forms|drawings|file)/(?:u/\\d+/)?d/(?!e/)([A-Za-z0-9_-]+)(?:/|$)")] private static partial Regex DrivePath();
    [GeneratedRegex("^/drive/(?:u/\\d+/)?folders/([A-Za-z0-9_-]+)(?:/|$)")] private static partial Regex FolderPath();
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex DriveId();
}
