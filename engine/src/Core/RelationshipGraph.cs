namespace Knowledge.Relationships;

// Wire names match the app's GraphPage contract (app/src/model.ts).
public sealed record GraphIdentity(string provider, string account, string externalId);
public sealed record GraphAccess(string scope, bool canRead, bool canOpen);
public sealed record GraphResource(GraphIdentity identity, string? canonicalUrl, string? sourceUrl,
    DateTimeOffset? sourceModifiedAt, DateTimeOffset retrievedAt, string? revision, string owner,
    GraphAccess access, string availability, string? content, string representation = "index",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GraphOriginal? original = null);
// A document's archived original file, with content only: its resource id (GET /app/documents/<id> serves it), format,
// size and a name safe to save it under.
public sealed record GraphOriginal(string id, string format, long bytes, string name);
public sealed record GraphProvenance(string sourceId, string recordId, DateTimeOffset observedAt, bool demo);
public sealed record GraphGroup(string id, string label);
public sealed record GraphNode(string id, string title, string type, string overview,
    GraphProvenance provenance, GraphResource resource,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GraphGroup>? groups = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GraphSeries>? measurements = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GraphTruth? truth = null);
// A native memory's star that a newer memory of its scope replaced (SB-P4-11, served only with memory.tools on): state
// replaced and the replacing memory's star id, for the app to draw it faded, never hidden. Absent on every other star.
public sealed record GraphTruth(string state, string replacedBy);
public sealed record GraphEvidence(string resourceId, string excerpt, string locator, DateTimeOffset retrievedAt);
public sealed record GraphEdge(string id, string source, string target, string relationship, double strength,
    string type, GraphIdentity origin, bool directed, string verification, IReadOnlyList<GraphEvidence> evidence,
    DateTimeOffset? sourceModifiedAt, DateTimeOffset retrievedAt);
public sealed record GraphIssue(string provider, string reason);
public sealed record GraphPage(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges,
    string? nextCursor, string completeness, IReadOnlyList<GraphIssue> issues,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? removedNodeIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? removedEdgeIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GraphRecall? recall = null);

// app_graph's recall operation (SB-P2-10, RecallStars): what combined retrieval found for one question, as the stars
// that stand for it on the reader's map, best first, each with why; how many records it found that have no star the
// reader can open (offMap, a count only), the notices, and the receipt (Mcp/RecallReply.cs). No text but the excerpts.
public sealed record GraphRecall(string query, IReadOnlyList<GraphRecalled> results, IReadOnlyList<string> notices, bool semanticSkipped,
    int offMap, string receipt);
public sealed record GraphRecalled(string id, double score, string excerpt, bool truncated, GraphRecallWhy why);
// Why a star is here: the keyword arm's rank, the query's words it holds and its exact id hits; the meaning arm's rank
// and similarity; imputed when it has no passages and was given the meaning arm's share at its keyword rank.
public sealed record GraphRecallWhy(int rank, GraphRecallWords? words, GraphRecallMeaning? meaning, bool imputed);
public sealed record GraphRecallWords(int rank, IReadOnlyList<string> terms, int exact);
public sealed record GraphRecallMeaning(int rank, double? similarity);

public static class GraphProjection
{
    static readonly HashSet<string> Kinds = ["theme", "project", "note", "record", "document", "memory", "decision"];
    static GraphIdentity Identity(ResourceKey key) => new(key.Provider, key.Account, key.ExternalId);
    // The viewer's record types, shared with the bounded views for harnesses.
    internal static string Type(string kind) => Kinds.Contains(kind) ? kind : "record";
    // `shows` names the providers on the map (AdapterRegistry.Shows): the catalog's and every enabled graph source's.
    public static GraphPage Create(IReadOnlyList<DiscoveryRecord> records, IReadOnlyList<DiscoveredLink> links,
        Func<string, bool> shows, IReadOnlyList<GraphIssue>? issues = null)
    {
        // The engine supports arbitrary adapters; this particular viewer has a finite provider enum.
        var supported = records.Where(r => shows(r.Key.Provider)).ToDictionary(r => r.Key);
        if (supported.Values.Select(r => r.Key.GraphId).Distinct().Count() != supported.Count)
            throw new InvalidOperationException("Viewer identity collision across subjects; use distinct source accounts");
        var nodes = supported.Values.Select(r => {
            var url = RelationshipDiscovery.CanonicalUrl(r.Url) is not null ? r.Url : null;
            return new GraphNode(r.Key.GraphId, r.Title, Kinds.Contains(r.Kind) ? r.Kind : "record", r.Overview ?? "",
                new(r.Key.Provider, r.Key.ExternalId, r.ModifiedAt ?? r.RetrievedAt, r.Key.Subject == RhythmConfig.Synthetic),
                new(Identity(r.Key), url, url, r.ModifiedAt, r.RetrievedAt, r.Revision, r.Key.Subject,
                    new(r.Scope, true, url is not null), "available", null),
                r.Groups is { Count: > 0 } groups ? groups.Select(g => new GraphGroup(g.Id, g.Label)).ToArray() : null,
                truth: r.ReplacedBy is { } by ? new GraphTruth("replaced", by) : null);
        }).ToArray();
        var edges = links.Where(l => supported.ContainsKey(l.Source) && supported.ContainsKey(l.Target))
            .Select(l => new GraphEdge(l.Id, l.Source.GraphId, l.Target.GraphId, l.Type,
                l.Strength ?? (l.Verification == "verified" ? 1 : 0.5), l.NativeType,
                new(CatalogStars.Provider, l.Source.Account, l.Id), l.Directed, l.Verification,
                l.Evidence.Select(e => new GraphEvidence(e.Resource.GraphId, e.Excerpt, e.Locator, e.RetrievedAt)).ToArray(),
                l.SourceModifiedAt, l.Evidence.Max(e => e.RetrievedAt))).ToArray();
        return new(nodes, edges, null, "partial", issues ?? [new(CatalogStars.Provider, "Bounded authorized catalog; outgoing references only. Unavailable sources are omitted; retry refresh for current state.")]);
    }
}
