using System.Text.Json;

namespace Knowledge.Relationships;

// The explorer's combined search (SB-P2-10): app_graph's recall operation, listed and accepted only with engine.recall
// on (RecallSettings). Combined retrieval's results for one question (Recall, for this reader), each shown as the star
// that stands for it on this reader's map, best first, so the explorer lights them in the sky it already shows and lists
// them with why, in its own search. A result's star is, in this order (StarIndex):
//   the map's own row for an adapter's record (its key in its catalog id, ResourceKey.CatalogId, RelationshipEngine);
//   the catalog's document star of a record saved in Rhythm (its subject, provider and external id, CatalogStars);
//   an adapter's star whose source id is the record's external id or sourceFileId: the original the catalog leaves a
//   saved copy to, by the catalog's own rule (RelationshipHost.CatalogRecords);
//   the data star of a series a measurement row holds (its provider, scope and each metric's group, CatalogStars);
//   the star of the document that contains a page (a document's pages are no stars, SB-P4-13), by the rules above;
//   a native memory's own star (MemoryStars.Key, SB-P3-11), and no other.
// Only stars in the reader's own view of the map (RelationshipHost.Readable) are served, so a result never names a star
// the reader cannot open. A result with none (a native memory written since the last pass, a record the map has not
// reached yet) is counted (offMap), never named. Two results one star stands for are one, at the first's place. The receipt is the
// recall tool's, over every result this reader was served (Mcp/RecallReply.cs).
public static class RecallStars
{
    public const int Results = 20, Excerpt = 300, MaxQuestion = 800;
    // Combined retrieval's output budget for the explorer: a passage of up to Excerpt characters for every result, never
    // the recall tool's 4000 for its first two, which the page cuts to Excerpt anyway. Ids, order and the receipt are
    // the same as the recall tool's for the same question with topK 20 (Results), which the receipt hashes too.
    public static readonly RecallOptions Budget = RecallOptions.Default with
    {
        FullMatches = 0, FullChars = Excerpt, Snippet = Excerpt, OutputBudget = Results * Excerpt, Logged = true,
    };

    public static async Task<GraphPage> Page(RelationshipHost host, Database db, Func<string, bool> shows, Recall recall, Guid principal,
        string question, CancellationToken ct)
    {
        var asked = new RecallQuery(question, Results);
        var found = await recall.Search(principal, asked, ct);
        var (view, configuration) = await host.Readable(principal, ct);
        var index = new StarIndex(view.Records.Where(r => shows(r.Key.Provider)));
        var placed = found.Hits.Select(h => index.Of(h.Row)).ToArray();
        // The rest may be data series: the data star of each metric they hold, the first this reader's map shows.
        var series = found.Hits.Where((h, i) => placed[i] is null).Select(h => h.Id).ToArray();
        if (series.Length > 0)
        {
            using var metrics = JsonDocument.Parse(await db.Read(principal, $"""
                SELECT r.id,r.subject_id,r.provider,r.scope,array_agg(DISTINCT m.metric ORDER BY m.metric) AS metrics
                FROM resources r JOIN measurements m ON m.resource_id=r.id
                WHERE r.id=ANY(@ids) AND {Database.Visible()} GROUP BY r.id,r.subject_id,r.provider,r.scope
                """, ct, ("ids", series)));
            var data = new Dictionary<Guid, string>();
            foreach (var row in metrics.RootElement.EnumerateArray())
                if (index.Data(row.GetProperty("subject_id").GetString()!, row.GetProperty("provider").GetString()!, row.GetProperty("scope").GetString()!,
                    row.GetProperty("metrics").EnumerateArray().Select(m => m.GetString()!), configuration.Catalog) is { } star)
                    data[row.GetProperty("id").GetGuid()] = star;
            for (var i = 0; i < placed.Length; i++) placed[i] ??= data.GetValueOrDefault(found.Hits[i].Id);
        }
        // The rest may be a document's pages (SB-P4-13): a page is no star of its own (its document's star holds its text),
        // so it stands as the star of the document that contains it (the documents adapter's contains link, off the map),
        // read through the reader's own predicate on the document's row, as any result is.
        var pages = found.Hits.Where((h, i) => placed[i] is null).Select(h => h.Id).ToArray();
        if (pages.Length > 0)
        {
            using var parents = JsonDocument.Parse(await db.Read(principal, $"""
                SELECT l.target_id AS page,r.subject_id,r.provider,r.external_id,r.metadata::text AS metadata FROM relationships l
                JOIN resources r ON r.id=l.source_id AND r.relationship_snapshot IS NULL
                WHERE l.target_id=ANY(@ids) AND l.type='contains' AND l.lifecycle='active' AND {Database.Visible()}
                ORDER BY l.target_id,r.id
                """, ct, ("ids", pages)));
            var parent = new Dictionary<Guid, string>();
            foreach (var row in parents.RootElement.EnumerateArray())
                if (index.Of(row) is { } star) parent.TryAdd(row.GetProperty("page").GetGuid(), star);
            for (var i = 0; i < placed.Length; i++) placed[i] ??= parent.GetValueOrDefault(found.Hits[i].Id);
        }

        var shown = new List<DiscoveryRecord>();
        var results = new List<GraphRecalled>();
        var offMap = 0;
        for (var i = 0; i < placed.Length; i++)
        {
            if (placed[i] is not { } id) { offMap++; continue; }
            if (results.Any(r => r.id == id)) continue;
            var (hit, why) = (found.Hits[i], found.Hits[i].Why);
            var excerpt = Recall.Cut(hit.Excerpt, Excerpt);
            shown.Add(index.Stars[id]);
            results.Add(new(id, hit.Score, excerpt, hit.Truncated || excerpt.Length < hit.Excerpt.Length, new(results.Count + 1,
                why.KeywordRank is { } k ? new GraphRecallWords(k, why.Terms, why.Exact) : null,
                why.MeaningRank is { } m ? new GraphRecallMeaning(m, why.Similarity) : null, why.Imputed)));
        }
        var keys = shown.Select(r => r.Key).ToHashSet();
        var page = GraphProjection.Create(shown, [.. view.Links.Where(l => keys.Contains(l.Source) && keys.Contains(l.Target))], shows, []);
        return page with
        {
            recall = new(question, results, found.Notices, found.SemanticSkipped, offMap, Knowledge.Mcp.RecallReply.Receipt(asked, found.Hits)),
        };
    }
}

// The stars of one reader's view, and which of them stands for a catalog row (RecallStars).
internal sealed class StarIndex
{
    const string SnapshotId = "relationship-engine:";
    public IReadOnlyDictionary<string, DiscoveryRecord> Stars { get; }
    readonly Dictionary<(string Subject, string Provider, string External), string> documents = [];
    readonly ILookup<string, DiscoveryRecord> originals;

    public StarIndex(IEnumerable<DiscoveryRecord> records)
    {
        var stars = new Dictionary<string, DiscoveryRecord>(StringComparer.Ordinal);
        foreach (var r in records) stars.TryAdd(r.Key.GraphId, r);
        Stars = stars;
        foreach (var r in stars.Values.Where(r => r.Key.Provider == CatalogStars.Provider && !CatalogStars.IsData(r.Key)
            && r.Key.ExternalId.StartsWith(r.Key.Subject + ":", StringComparison.Ordinal)))
            documents.TryAdd((r.Key.Subject, r.Key.Account, r.Key.ExternalId[(r.Key.Subject.Length + 1)..]), r.Key.GraphId);
        originals = stars.Values.Where(r => r.Key.Provider != CatalogStars.Provider).ToLookup(r => r.Key.ExternalId, StringComparer.Ordinal);
    }

    // The star a catalog row (as search_knowledge returns its columns) stands as, before the data series: a native
    // memory's own star, its own map row's, its document star, or the original's an adapter shows; null for none.
    public string? Of(JsonElement row)
    {
        var subject = row.GetProperty("subject_id").GetString()!;
        var provider = row.GetProperty("provider").GetString()!;
        var external = row.GetProperty("external_id").GetString() ?? "";
        // A native memory's own row (its external id is its id) stands as its own star, whose graph id is that id alone,
        // never as another source's.
        if (provider == MemoryStars.Provider)
            return Guid.TryParse(external, out var memory) && MemoryStars.Key(subject, memory).GraphId is var star && Stars.ContainsKey(star) ? star : null;
        if (external.StartsWith(SnapshotId, StringComparison.Ordinal) && Snapshot(external) is { } key
            && new ResourceKey(subject, provider, key.Account, key.External).GraphId is var own && Stars.ContainsKey(own)) return own;
        if (documents.TryGetValue((subject, provider, external), out var document)) return document;
        foreach (var source in new[] { external, SourceFile(row) })
            if (source is { Length: > 0 } && originals[source].OrderBy(r => r.Key.Subject == subject ? 0 : 1).ThenBy(r => r.Key.GraphId, StringComparer.Ordinal)
                .FirstOrDefault() is { } original) return original.Key.GraphId;
        return null;
    }

    // The data star a measurement row of `provider`'s stands as: the first of its metrics' stars that is on the map.
    public string? Data(string subject, string provider, string scope, IEnumerable<string> metrics, RelationshipCatalog catalog) => metrics
        .Select(m => new ResourceKey(subject, CatalogStars.Provider, CatalogStars.DataAccount(provider),
            $"{subject}:{catalog.Group(provider, m).Id}" + (scope == "private" ? "" : ":" + scope)).GraphId)
        .FirstOrDefault(Stars.ContainsKey);

    // A map row's catalog id, relationship-engine:["account","externalId"], read back; null when it is not one.
    static (string Account, string External)? Snapshot(string external)
    {
        try
        {
            using var parts = JsonDocument.Parse(external[SnapshotId.Length..]);
            return parts.RootElement is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 2
                && a[0].ValueKind == JsonValueKind.String && a[1].ValueKind == JsonValueKind.String ? (a[0].GetString()!, a[1].GetString()!) : null;
        }
        catch (JsonException) { return null; }
    }

    // The source id a saved copy names (metadata.sourceFileId), from the row's metadata as JSON text.
    static string? SourceFile(JsonElement row)
    {
        if (!row.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var parsed = JsonDocument.Parse(metadata.GetString()!);
            return parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("sourceFileId", out var file)
                && file.ValueKind == JsonValueKind.String ? file.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
