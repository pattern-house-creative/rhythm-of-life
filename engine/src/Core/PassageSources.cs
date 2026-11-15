using Knowledge.Adapters;

namespace Knowledge;

// Which texts the embeddings index gives passages to (SB-P2-05; adapters.embeddings.sources, docs/configuration.md), and
// the rows each kind is, as SQL over a resources row aliased `r`. Every kind is passages of one model at the row's
// current revision (resource_chunks.model, .source_revision); the kinds never overlap.
// - Copies: save_document copies that asked for passages, from their own text (the backfill's, D-069; today's rows).
// - Documents: the documents adapter's uploads and dropped files, and their pages, from their own text (Q-044).
// - Memories: native memories' own rows while not held for review, from the memory's text in `memories` (migration 025,
//   inert: nothing writes a memory yet).
// - Records: the star rows of graph sources that read their text on request (ISearchableSource: notes, Drive), keyed by
//   provider. Never a reference-only provider, which may not keep its source's text (ProviderPolicies.Validate).
// Adapters hand the index their text rather than holding the model themselves, so one job embeds every text with the
// registry's model (EmbeddingModels), under one budget and cursor, with one chunker for both models (D-069).
public sealed record PassageSources(bool Copies, bool Documents, bool Memories, IReadOnlyDictionary<string, ISearchableSource> Records)
{
    // Today's index: save_document copies only.
    public static PassageSources CopiesOnly { get; } = new(true, false, false, new Dictionary<string, ISearchableSource>());
    // Whether anything besides the copies is covered.
    public bool Sources => Documents || Memories || Records.Count > 0;
    public string[] Providers => [.. Records.Keys.Order(StringComparer.Ordinal)];

    // What the settings list, over the adapters that are on: `notes` and `drive` name adapters, each covering its
    // providers that may keep text. An adapter that is off, or reads no text, covers nothing.
    public static PassageSources For(EmbeddingSettings settings, IEnumerable<IAdapter> enabled, bool copies) =>
        Of(copies, settings.Sources.Contains(EmbeddingSettings.Documents), settings.Sources.Contains(EmbeddingSettings.Memories),
            enabled.OfType<ISearchableSource>().Where(a => settings.Sources.Contains(a.Name)));

    public static PassageSources Of(bool copies, bool documents, bool memories, IEnumerable<ISearchableSource> sources)
    {
        var records = new Dictionary<string, ISearchableSource>(StringComparer.Ordinal);
        foreach (var source in sources)
            foreach (var (provider, policy) in source.Providers)
                if (policy.Authority != SourceAuthority.ReferenceOnly && !ProviderPolicies.Reserved.Contains(provider)) records[provider] = source;
        return new(copies, documents, memories, records);
    }

    // A save_document copy that asked for passages (the job's rows before SB-P2-05).
    public const string Copy = "r.relationship_snapshot IS NULL AND r.metadata->>'semanticIndex'='true'";
    // A row of the documents adapter: an upload or a dropped file (retrievalMode archived-original) or one of its pages,
    // never a save_document copy (its retrieval mode) and never a legacy one (another provider).
    public static string DocumentRow => $"""
        r.relationship_snapshot IS NULL AND r.provider='{DocumentOriginals.Provider}' AND coalesce(r.metadata->>'semanticIndex','')<>'true'
          AND coalesce(r.metadata->>'retrievalMode','') NOT IN ({Database.CopyModes})
        """;
    // Such a row with text to give passages: not the placeholder of a scan with no text layer.
    public static string Document => $"{DocumentRow} AND r.content<>'' AND coalesce(r.metadata->>'noText','')<>'true'";
    // A native memory's own row (provider memory, no snapshot); the memory's star is a snapshot row of its own.
    public const string MemoryRow = "r.relationship_snapshot IS NULL AND r.provider='memory'";
    // Such a row while its memory is not held for review: a held memory never reaches search (D-050, D-039).
    public const string Memory = MemoryRow + " AND EXISTS(SELECT 1 FROM memories m WHERE m.id=r.id AND m.hold IS NULL)";
    // An adapter record's star row (a memory's star excluded: its memory has passages of its own).
    public const string Record = "r.relationship_snapshot IS NOT NULL AND r.provider<>'memory'";
    // Such a row while the map would show its star: seen by a pass within graph.freshnessSeconds (`@fresh`). Only then is
    // its text read for passages, or its passages served; past it they are removed (EmbeddingBackfill.Step).
    public const string Fresh = "(r.relationship_snapshot->>'RetrievedAt')::timestamptz>now()-make_interval(secs=>@fresh)";
    // The revision a row's passages are made at (resource_chunks.source_revision): the row's own, but for a native memory
    // the revision its text was last written at in `memories` (`memory:` and `text_revision`, migration 033), which its
    // resources row need not follow, so a change of its status, tags or project keeps its passages; migration 026 drops a
    // memory's passages when its text or hold changes all the same.
    public const string Revision = "CASE WHEN r.provider='memory' AND r.relationship_snapshot IS NULL THEN (SELECT 'memory:'||m.text_revision FROM memories m WHERE m.id=r.id) ELSE r.revision END";
}
