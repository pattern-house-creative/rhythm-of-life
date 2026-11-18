using System.Text.Json;
using Knowledge.Adapters;

namespace Knowledge.Relationships;

// The map's settings, from `graph` in rhythm.yaml. `Principal` is the engine's own system principal that owns the
// scheduled refresh; every harness and app principal reads the one snapshot through the read predicate, which with
// memory.scopes off (every install's mode) is today's: the household shares everything (RelationshipEngine.Served).
// `Catalog` turns the install's own records (measurements and saved documents) into data and document stars. `Map`, with
// graph.similarity.model set (D-256), is the model whose vectors make the similar and copy links and the thresholds they
// read then; a rebuild that finds that model not loaded uses the Similarity* numbers and the live model's vectors, as
// without it, and says so in its notes. `SimilarityMaxLinks` (graph.similarity.maxLinks, D-258) caps the similar links of
// each star with either model's vectors; null, no cap.
public sealed record RelationshipHostConfiguration(Guid Principal, string RefreshStateFile, RelationshipCatalog Catalog,
    int RefreshSeconds = 900, int FreshnessSeconds = 2100, int MaxMemories = 3000, int MaxDocuments = 1000, int GraphNodeLimit = 300,
    double? SimilarityThreshold = null, int SimilarityNeighbors = 3, double? SimilarityDocumentThreshold = null, string Settings = "",
    double? SimilarityMutualThreshold = null, MapSimilarity? Map = null, int? SimilarityMaxLinks = null)
{
    public void Validate()
    {
        if (Principal == Guid.Empty || RefreshSeconds is < 300 or > 86400 || string.IsNullOrWhiteSpace(RefreshStateFile)
            || !Path.IsPathFullyQualified(RefreshStateFile) || !RelationshipCatalog.Valid(Catalog)
            || MaxMemories is < 1 or > 4000 || MaxDocuments is < 0 or > 4000 || MaxMemories + MaxDocuments > 4900
            || GraphNodeLimit is < 10 or > 500 || FreshnessSeconds < RefreshSeconds + 60 || FreshnessSeconds > 86400
            || SimilarityThreshold is < 0.2 or > 0.95 || SimilarityThreshold is double.NaN || SimilarityNeighbors is < 1 or > 10
            || SimilarityDocumentThreshold is < 0.2 or > 0.99 || SimilarityDocumentThreshold is double.NaN
            || SimilarityDocumentThreshold is not null && SimilarityThreshold is null
            || SimilarityMutualThreshold is < 0.2 or > 0.95 || SimilarityMutualThreshold is double.NaN
            || SimilarityMutualThreshold is not null && SimilarityThreshold is null
            || Map is not null && (SimilarityThreshold is null || Map.Threshold is < 0.2 or > 0.95 or double.NaN
                || Map.DocumentThreshold is < 0.2 or > 0.99 or double.NaN || Map.MutualThreshold is < 0.2 or > 0.95 or double.NaN)
            || SimilarityMaxLinks is < 2 or > 50 || SimilarityMaxLinks is not null && SimilarityThreshold is null)
            throw new ArgumentException("Invalid relationship configuration");
    }
    // Embeddings: the settings to read the thresholds from, the config's own when left out (a test gives another model's).
    public static RelationshipHostConfiguration From(RhythmConfig config, Guid principal, string stateDirectory, EmbeddingSettings? embeddings = null)
    {
        var similarity = config.Graph.Similarity;
        // With adapters.embeddings.thresholds `tuned` (SB-P2-13, off by default), or a live model other than MiniLM (which
        // the settings refuse for now, D-025), that model's re-tuned thresholds stand in for graph.similarity's numbers
        // (EmbeddingThresholds.Override); one set to null stays off (similar links, the mutual rule) or stays the main
        // threshold (documents). Otherwise every number is graph.similarity's, as before.
        var tuned = similarity.Threshold is null ? null : EmbeddingThresholds.Override(embeddings ?? EmbeddingSettings.From(config));
        // graph.similarity.model (D-256): that model's own thresholds for its links, whatever graph.similarity's numbers
        // and adapters.embeddings.thresholds say (MapSimilarity). Absent, null, and every number above is as before.
        var map = MapModels.Wanted(config) is { } model ? MapSimilarity.For(model, similarity) : null;
        return new(principal, Path.GetFullPath(Path.Combine(stateDirectory, "graph-refresh.state")), RelationshipCatalog.For(config),
            config.Graph.RefreshSeconds, config.Graph.FreshnessSeconds, config.Graph.MaxMemories, config.Graph.MaxDocuments, config.Graph.NodeLimit,
            tuned?.Similar ?? similarity.Threshold, similarity.Neighbors,
            similarity.Threshold is null || similarity.DocumentThreshold is null ? null : tuned?.SimilarDocuments ?? similarity.DocumentThreshold, config.Fingerprint,
            similarity.Threshold is null || similarity.MutualThreshold is null ? null : tuned?.Mutual ?? similarity.MutualThreshold, map,
            similarity.Threshold is null ? null : similarity.MaxLinks);
    }
}

// The map: a scheduled refresh writes a snapshot of every graph source (the catalog and each enabled Memory- or
// DocumentSource adapter); reads serve that snapshot without calling a source, except to read one star's text when
// it is opened. No writes during graph reads.
public sealed class RelationshipHost(Database db, Func<RelationshipHostConfiguration?> configuration,
    Func<RelationshipHostConfiguration, IRelationshipSource> connect, AdapterRegistry? adapters = null)
{
    readonly AdapterRegistry adapters = adapters ?? AdapterRegistry.Empty;

    // The engine's own principal, or a harness or app principal.
    async Task<bool> Reads(RelationshipHostConfiguration config, Guid principal, CancellationToken ct) =>
        config.Principal == principal || await db.RoleOf(principal, ct) is "harness" or "app" or "system";

    RelationshipHostConfiguration Config() => configuration() ?? throw new UnauthorizedAccessException("Relationship graph unavailable");

    public async Task<GraphPage> Read(Guid principal, IReadOnlyList<string>? knownNodes,
        IReadOnlyList<string>? knownEdges, CancellationToken ct, GraphQuery? query = null)
    {
        var config = Config();
        if (!await Reads(config, principal, ct)) throw new UnauthorizedAccessException("Relationship graph unavailable");
        config.Validate();
        // "content" and "data" are "get" plus that one node's current text or measurements, read when it is opened.
        var one = query is { Operation: "content" or "data" };
        var (page, shown) = await new RelationshipEngine(db, SnapshotOnly.Instance) { Replaced = config.Catalog.Replaced, MadeLinks = config.Catalog.MadeLinks, Work = config.Catalog.Work }.ReadSnapshot(principal,
            TimeSpan.FromSeconds(config.FreshnessSeconds), config.GraphNodeLimit, Unavailable(), adapters.Shows, ct,
            one ? null : knownNodes, one ? null : knownEdges, one ? query! with { Operation = "get" } : query);
        if (query is { Operation: "content" })
            page = page.nodes is not [var node] ? page
                : node.provenance.sourceId == MemoryStars.Provider ? await WithNativeText(principal, page, config.Catalog.Replaced, ct)
                : adapters.Memories.FirstOrDefault(a => a.Providers.ContainsKey(node.provenance.sourceId)) is { } memory
                ? await WithMemoryText(memory, page, shown, ct) : await WithRecordText(principal, config, page, ct);
        if (query is { Operation: "data" }) page = await WithMeasurements(principal, config, page, ct);
        return page;
    }
    // Providers whose adapter is off are not shown at all (AdapterRegistry.Shows), so nothing else is withheld here.
    static HashSet<string> Unavailable() => new(StringComparer.Ordinal);

    // One reader's whole snapshot, exactly as the freshness window allows.
    async Task<SnapshotView> View(Guid principal, CancellationToken ct)
    {
        var config = Config();
        if (!await Reads(config, principal, ct)) throw new UnauthorizedAccessException("Relationship graph unavailable");
        config.Validate();
        return await new RelationshipEngine(db, SnapshotOnly.Instance) { Replaced = config.Catalog.Replaced, MadeLinks = config.Catalog.MadeLinks, Work = config.Catalog.Work }.View(principal,
            TimeSpan.FromSeconds(config.FreshnessSeconds), Unavailable(), ct);
    }
    // One reader's snapshot and the configuration it was read under, for a dashboard's feeds.
    public async Task<(SnapshotView View, RelationshipHostConfiguration Configuration)> Readable(Guid principal, CancellationToken ct) =>
        (await View(principal, ct), Config());
    // One reader's snapshot as the map shows it (AdapterRegistry.Shows): the stars of the providers on the map and the links
    // among them, for the steps that name a star or a link the reader is served (MapDismissals, SB-P4-05).
    public async Task<SnapshotView> ShownView(Guid principal, CancellationToken ct)
    {
        var view = await View(principal, ct);
        var records = view.Records.Where(r => adapters.Shows(r.Key.Provider)).ToArray();
        var keys = records.Select(r => r.Key).ToHashSet();
        return view with { Records = records, Links = [.. view.Links.Where(l => keys.Contains(l.Source) && keys.Contains(l.Target))] };
    }
    public async Task<ExplorePage> Explore(Guid principal, string? query, string? star, int depth, int limit, CancellationToken ct)
    {
        GraphExplorer.Validate(query, star, depth, limit);
        var explorer = new GraphExplorer(await View(principal, ct), adapters.Shows);
        var (stars, links, truncated) = explorer.Explore(query, star, depth, limit);
        var saved = await ResourceIds(principal, GraphExplorer.Saved(stars), ct);
        return new(stars.Select(s => explorer.Star(s, saved)).ToArray(), links.Select(l => explorer.Link(l)).ToArray(), truncated);
    }
    public async Task<ChangesPage> Changes(Guid principal, DateTimeOffset since, int limit, CancellationToken ct)
    {
        GraphExplorer.Validate(since, limit, DateTimeOffset.UtcNow);
        var explorer = new GraphExplorer(await View(principal, ct), adapters.Shows);
        var (stars, links, truncated) = explorer.Changes(since, limit);
        var saved = await ResourceIds(principal, GraphExplorer.Saved(stars.Select(s => s.Record)), ct);
        return new(since, stars.Select(s => explorer.Star(s.Record, saved, s.Change)).ToArray(),
            links.Select(l => explorer.Link(l, true)).ToArray(), truncated);
    }
    // The graph ids among `graphIds` that are stars this principal can read now.
    public async Task<IReadOnlySet<string>> Known(Guid principal, IEnumerable<string> graphIds, CancellationToken ct)
    {
        var shown = await Shown(principal, ct);
        return graphIds.Where(shown.ContainsKey).ToHashSet(StringComparer.Ordinal);
    }
    // Every star this principal can read now, by graph id, with what a derived record citing it takes (an insight, a
    // dashboard, a pin; docs/dev/memory.md section 5, SB-P1-06): the subject whose readers alone the star is served to (a
    // save_document copy's star its narrower Reader) and the provider whose rows it reads, which a source share names (a
    // data star's measuring provider, a catalog document star's provider, else the star's own).
    public async Task<IReadOnlyDictionary<string, (string Subject, string? Provider)>> Shown(Guid principal, CancellationToken ct)
    {
        var shown = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var r in (await View(principal, ct)).Records.Where(r => adapters.Shows(r.Key.Provider)))
            shown[r.Key.GraphId] = r.Reader is { } reader ? (reader, null) : (r.Key.Subject, CatalogStars.SourceProvider(r.Key));
        return shown;
    }
    // The catalog resource behind each saved-record star, for get_resource.
    async Task<IReadOnlyDictionary<ResourceKey, Guid>> ResourceIds(Guid principal, IEnumerable<ResourceKey> keys, CancellationToken ct)
    {
        var wanted = keys.ToDictionary(k => (k.Subject, k.Account, k.ExternalId[(k.Subject.Length + 1)..]));
        var found = new Dictionary<ResourceKey, Guid>();
        if (wanted.Count == 0) return found;
        await using var cmd = db.Source.CreateCommand($"""
            SELECT r.id,r.subject_id,r.provider,r.external_id FROM resources r
            JOIN unnest(@subjects,@providers,@externals) AS k(s,p,e) ON r.subject_id=k.s AND r.provider=k.p AND r.external_id=k.e
            WHERE r.relationship_snapshot IS NULL AND {Database.Visible()}
            """);
        cmd.Parameters.AddWithValue("subjects", wanted.Keys.Select(k => k.Subject).ToArray());
        cmd.Parameters.AddWithValue("providers", wanted.Keys.Select(k => k.Account).ToArray());
        cmd.Parameters.AddWithValue("externals", wanted.Keys.Select(k => k.Item3).ToArray());
        cmd.Parameters.AddWithValue("principal", principal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (wanted.TryGetValue((reader.GetString(1), reader.GetString(2), reader.GetString(3)), out var key)) found[key] = reader.GetGuid(0);
        return found;
    }
    // Only a memory already in the snapshot (the record the page was read from) is read, from its own adapter. The text
    // is returned once and never stored, cached or logged.
    async Task<GraphPage> WithMemoryText(IMemorySource memory, GraphPage page, IReadOnlyList<DiscoveryRecord> shown, CancellationToken ct)
    {
        var record = shown.FirstOrDefault(r => r.Key.GraphId == page.nodes[0].id);
        string? text = null;
        try { if (record is not null) text = await memory.ReadText(record, ct); }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or JsonException) { text = null; }
        if (text is null)
            return page with { issues = [.. page.issues, new(page.nodes[0].provenance.sourceId, "This memory's text is unavailable right now.")] };
        return page with { nodes = [page.nodes[0] with { resource = page.nodes[0].resource with { content = text, retrievedAt = DateTimeOffset.UtcNow } }] };
    }
    // A native memory's text, read from the memory tables when its star is opened, through the reader's own predicate on
    // the memory's own row and never while it is held (MemoryStars.Text); returned once, never stored, cached or logged.
    // A project's star has no text of its own. With memory.tools on a replaced memory's text opens too (SB-P4-11).
    async Task<GraphPage> WithNativeText(Guid principal, GraphPage page, bool replaced, CancellationToken ct)
    {
        var node = page.nodes[0];
        if (node.resource.identity.account != MemoryStars.Account || !Guid.TryParse(node.resource.identity.externalId, out var id)) return page;
        var text = await MemoryStars.Text(db, principal, id, ct, replaced);
        if (text is null)
            return page with { issues = [.. page.issues, new(MemoryStars.Provider, "This memory's text is unavailable right now.")] };
        return page with { nodes = [node with { resource = node.resource with { content = text, retrievedAt = DateTimeOffset.UtcNow } }] };
    }
    // A document star's saved text (its pages in order), read from the catalog row the star stands for, and what its
    // archived original is when it has one. The star must be in the snapshot and its subject configured, and the row is
    // read through the reader's own predicate; nothing is cached.
    async Task<GraphPage> WithRecordText(Guid principal, RelationshipHostConfiguration config, GraphPage page, CancellationToken ct)
    {
        if (page.nodes is not [var node] || !config.Catalog.Subjects.Contains(node.resource.owner)
            || node.resource.identity.provider != CatalogStars.Provider || node.resource.identity.account.StartsWith("data:", StringComparison.Ordinal)
            || !node.resource.identity.externalId.StartsWith(node.resource.owner + ":", StringComparison.Ordinal)) return page;
        var identity = node.resource.identity;
        var read = await CatalogReader.Text(db, principal, node.resource.owner, identity.account, node.resource.access.scope,
            identity.externalId[(node.resource.owner.Length + 1)..], ct);
        if (read is not { } found)
            return page with { issues = [.. page.issues, new(CatalogStars.Provider, "This record's text is unavailable right now.")] };
        return page with {
            nodes = [node with { resource = node.resource with { content = found.Text, retrievedAt = DateTimeOffset.UtcNow,
                sourceModifiedAt = found.Modified ?? node.resource.sourceModifiedAt, original = found.Original } }],
            issues = found.Complete ? page.issues : [.. page.issues, new(CatalogStars.Provider, "Showing the first 200,000 characters of this record.")] };
    }
    // A data star's measurements that the reader reads: each metric's latest reading and recent daily summaries, for
    // this response only.
    async Task<GraphPage> WithMeasurements(Guid principal, RelationshipHostConfiguration config, GraphPage page, CancellationToken ct)
    {
        if (page.nodes is not [var node] || !config.Catalog.Subjects.Contains(node.resource.owner)
            || node.resource.identity.provider != CatalogStars.Provider || !node.resource.identity.account.StartsWith("data:", StringComparison.Ordinal)
            || node.resource.identity.externalId.Split(':') is not [var subject, var group, ..] || subject != node.resource.owner) return page;
        var series = await CatalogReader.Series(db, principal, config.Catalog, subject, node.resource.identity.account["data:".Length..], node.resource.access.scope, group, ct);
        return page with { nodes = [node with { measurements = series }] };
    }
    // The engine build that made the last rebuild: an upgrade can draw the map differently (new star groups, demo
    // data), so its first automatic rebuild runs at once instead of waiting for the timer. Changed settings do too.
    static readonly string Build = typeof(RelationshipHost).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    // What the last rebuild could not read or left out (a source unavailable, the catalog past its budget), for the
    // engine's log: kinds and counts only, never an ID, title or text.
    public IReadOnlyList<string> Notes { get; private set; } = [];
    // Whether the last rebuild wanted a map model (graph.similarity.model) that was not loaded, and so drew its similar
    // links with the live model's vectors (D-256): the scheduler rebuilds once more when that model comes on.
    public bool MapFellBack { get; private set; }
    // The newest failure that stopped the titles model for a rebuild, kept until a later rebuild asks it for titles
    // without one (ModelStatus shows it), also across the engine's restarts: it is kept beside the rebuild's state
    // (RefreshStateFile + ".titles"), its kind and time only (TitleFailures), never a text.
    public TitleFailure? TitleFailure
    {
        get { lock (titleGate) { if (!titleRead) { titleRead = true; titleFailure = StoredTitleFailure(); } return titleFailure; } }
    }
    readonly object titleGate = new();
    bool titleRead;
    TitleFailure? titleFailure;
    static string TitleFile(RelationshipHostConfiguration config) => config.RefreshStateFile + ".titles";
    TitleFailure? StoredTitleFailure()
    {
        try
        {
            var file = configuration() is { } config ? TitleFile(config) : null;
            if (file is null || !File.Exists(file)) return null;
            return File.ReadAllText(file).Split(' ') is [var kind, var at] && TitleFailures.Known(kind)
                && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? new(kind, when) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    void KeepTitleFailure(RelationshipHostConfiguration config, TitleFailure? failure)
    {
        lock (titleGate) { titleRead = true; titleFailure = failure; }
        try
        {
            if (failure is null) File.Delete(TitleFile(config));
            else File.WriteAllText(TitleFile(config), $"{failure.Kind} {failure.At:O}");
        }
        // The state folder unwritable: shown until the engine stops, as before it was kept.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    // One rebuild: complete, busy (another is running), not_due (automatic, and the last was recent) or disabled.
    public async Task<string> Refresh(bool automatic, CancellationToken ct)
    {
        Notes = [];
        MapFellBack = false;
        var config = configuration();
        if (config is null) return "disabled";
        config.Validate();
        // A short local single-flight file lock spans source work, never a DB transaction/lock.
        var path = config.RefreshStateFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream gate;
        try { gate = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return "busy"; }
        await using (gate)
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? previous = null;
            if (File.Exists(path) && DateTimeOffset.TryParse(await File.ReadAllTextAsync(path, ct), out var stored)) previous = stored;
            var built = path + ".build";
            var made = $"{Build} {config.Settings}";
            var sameBuild = File.Exists(built) && await File.ReadAllTextAsync(built, ct) == made;
            if (automatic && sameBuild && previous is { } last && now - last < TimeSpan.FromSeconds(config.RefreshSeconds) - TimeSpan.FromSeconds(30)) return "not_due";
            // Checkpoint attempts, including failures, to bound retries across process restarts.
            // These hold only a time, the engine build and a hash of the settings, never an ID, title, credential or
            // source payload.
            var temporary = path + ".next";
            await File.WriteAllTextAsync(temporary, now.ToString("O"), ct);
            File.Move(temporary, path, true);
            await File.WriteAllTextAsync(built, made, ct);
            // The recorded memory.scopes mode, read once for the pass: with scopes on, its budgets are per scope.
            var scoped = await db.ScopesOn(ct);
            if (scoped) config = config with { Catalog = config.Catalog with { Scoped = true, Shares = await db.SourceShares(ct) } };
            var source = connect(config);
            // With graph.similarity.model set, its vectors and thresholds when this pass's source has that model loaded;
            // otherwise the live model's and graph.similarity's, as without it, noted below (D-256).
            var map = config.Map is { } wanted && source is CatalogRelationshipSource { MapModel: { } loaded } && loaded == wanted.Model.Id ? wanted : null;
            MapFellBack = config.Map is not null && map is null;
            var engine = new RelationshipEngine(db, source,
                config.SimilarityThreshold is not { } threshold ? null
                : map is not null ? new SimilaritySuggestions(map.Threshold, config.SimilarityNeighbors, map.DocumentThreshold, map.MutualThreshold,
                    scoped ? config.Catalog.Subjects : null) { MapModel = map.Model, MaxLinks = config.SimilarityMaxLinks }
                : new SimilaritySuggestions(threshold, config.SimilarityNeighbors, config.SimilarityDocumentThreshold,
                    config.SimilarityMutualThreshold, scoped ? config.Catalog.Subjects : null) { MaxLinks = config.SimilarityMaxLinks },
                RelationshipBudget.Scheduled, scoped ? config.Catalog : null);
            try
            {
                await engine.Refresh(config.Principal, ct);
                return "complete";
            }
            finally
            {
                if (source is CatalogRelationshipSource pass)
                {
                    Notes = [.. pass.Notes];
                    if (pass.Titles is { Failure: { } kind }) KeepTitleFailure(config, new(kind, DateTimeOffset.UtcNow));
                    else if (pass.Titles is { Asked: > 0 }) KeepTitleFailure(config, null);
                }
                if (engine.LinksLeftOut > 0)
                    Notes = [.. Notes, $"{engine.LinksLeftOut} links past their scope's share of the map's {RelationshipDiscovery.MaximumLinks} left out"];
                if (MapFellBack)
                    Notes = [.. Notes, $"graph.similarity.model {config.Map!.Model.Name} is {(source as CatalogRelationshipSource)?.MapStatus ?? "off"}: "
                        + "the similar links used the live embeddings model's vectors and graph.similarity's thresholds (docs/models.md)"];
                if (source is IDisposable disposable) disposable.Dispose();
            }
        }
    }
    // Snapshot reads never consult a source; any accidental use fails closed.
    sealed class SnapshotOnly : IRelationshipSource
    {
        public static readonly SnapshotOnly Instance = new();
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct) => throw new UnauthorizedAccessException("Unavailable");
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => Task.FromResult(SourceCheck.Unavailable);
    }
}

// One refresh pass's sources: every enabled Memory- and DocumentSource adapter, then the catalog's own records. A
// source that fails adds nothing and removes nothing; its stars age out with the snapshot window if failures persist.
// Check answers only from this pass's own fresh observations. Adapted from the private version's scoped source,
// which read one particular memory service and document store directly. `model` names the model `embed` uses; every
// vector the pass lists carries the model that made it (Tagged). With a map model (MapEmbed, graph.similarity.model,
// D-256) each record's vector is given a second one, from the same title and text by that model, read by the map's
// similar and copy links alone.
public sealed class CatalogRelationshipSource(RelationshipHostConfiguration config, AdapterRegistry adapters,
    Func<RelationshipCatalog, CancellationToken, Task<CatalogInventory>> catalog, Func<string, string, float[]?>? embed = null,
    string? model = null)
    : IRelationshipSource, IRelationshipSnapshotSource
{
    static readonly TimeSpan TitleBudget = TimeSpan.FromSeconds(60);
    const int TitlesPerPass = 60;
    readonly Dictionary<ResourceKey, string?> observed = [];
    Dictionary<ResourceKey, DiscoveryRecord> stored = [];
    // The vectors this pass may list, by reference, each with the model that made it: those it made or reused, and
    // those stored last pass, which an adapter that did not read a record again keeps (the Drive adapter does).
    readonly System.Collections.Concurrent.ConcurrentDictionary<float[], string> made = new(ReferenceEqualityComparer.Instance);
    // The map model's vector for each of those live vectors that has one, by the live vector's reference (D-256): made
    // this pass, or stored last pass by the same map model for the same content revision.
    readonly System.Collections.Concurrent.ConcurrentDictionary<float[], float[]> mapped = new(ReferenceEqualityComparer.Instance);
    int mapFailed;
    readonly List<string> notes = [];
    // This pass's stars of replaced memories (SB-P4-11), which Bounded keeps after the current records of every source.
    readonly HashSet<ResourceKey> replacedStars = [];
    // When this pass began, before any source listed: every "missing" notice of the pass is stamped with it (Missing).
    DateTimeOffset started;
    // With a titles model on, the whole text of each record the pass may title, by its key (D-255): kept beside the
    // records, never on them, so nothing that saves, compares, logs or serves a record can carry it; read by Retitle only
    // and dropped when the pass ends. Never stored, logged or exposed.
    readonly Dictionary<ResourceKey, string> titleTexts = [];
    // At most this much of a whole text is kept for the titles model, more than the titles adapter sends (6,000
    // characters, as the evaluation cut its whole-text input), as much as the pass reads of a native memory's text.
    const int MaxTitleText = 8000;
    readonly string? model = !string.IsNullOrWhiteSpace(model) ? model
        : embed is null ? null : throw new ArgumentException("An embedder needs its model's name");
    // The map model this pass makes vectors with (graph.similarity.model, loaded), and its id; null without one. Only
    // used beside the live model: without `embed` no record has a vector, as before.
    public Func<string, string, float[]?>? MapEmbed { get; init; }
    public string? MapEmbedModel { get; init; }
    public string? MapModel => embed is not null && MapEmbed is not null && MapEmbedModel is { Length: > 0 } id ? id : null;
    // The map model's state when the pass began (Embeddings.Status), for the rebuild's note when it is not loaded.
    public string? MapStatus { get; init; }
    // What this pass could not read or left out, by kind and count only.
    public IReadOnlyList<string> Notes => notes;
    // What this pass's titles model did, in counts and a failure kind; null when no titles model is on.
    public TitlePass? Titles { get; private set; }

    public void Prime(IReadOnlyList<DiscoveryRecord> previous)
    {
        stored = previous.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var record in stored.Values)
            if (record.Embedding is { } vector && SimilaritySuggestions.ModelOf(record) is { } by)
            {
                made[vector] = by;
                if (MapEmbedModel is { } wanted && record.MapEmbedding is { } kept && record.MapEmbeddingModel == wanted) mapped[vector] = kept;
            }
    }

    public async Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct)
    {
        if (principal != config.Principal) throw new UnauthorizedAccessException("Unavailable");
        titleTexts.Clear();
        try { return await Listed(ct); }
        finally { titleTexts.Clear(); }
    }

    // A record's whole text for the titles model (SourcePass.TitleText, MemoryStars.Records): kept only while a titles
    // model is on, for a current record whose title is still its excerpt, the only ones Retitle asks for.
    void KeepTitleText(DiscoveryRecord record, string text)
    {
        if (adapters.Titles is null || record.Status != "current" || record.Overview is not { Length: > 0 } excerpt || record.Title != excerpt
            || string.IsNullOrWhiteSpace(text)) return;
        titleTexts[record.Key] = text.Length <= MaxTitleText ? text : text[..MaxTitleText];
    }

    async Task<DiscoveryBatch> Listed(CancellationToken ct)
    {
        observed.Clear(); notes.Clear();
        mapFailed = 0;
        started = DateTimeOffset.UtcNow;
        var records = new List<DiscoveryRecord>();
        // graph.maxMemories and graph.maxDocuments are the install's, shared by its adapters in their order: each gets
        // what the ones before it left, projects and hubs counted. One whose budget is used up is left out of the pass.
        // With scopes on, each subject has its own share of both, used up by the adapters before only with records of
        // that subject (RelationshipCatalog.Scoped); an adapter is left out once every subject's share is.
        var (memories, documents) = (config.MaxMemories, config.MaxDocuments);
        var catalog = config.Catalog;
        var shares = catalog.Scoped ? catalog.Subjects.ToDictionary(s => s,
            s => (Memories: catalog.Share(config.MaxMemories, s), Documents: catalog.Share(config.MaxDocuments, s)), StringComparer.Ordinal) : null;
        // The catalog's inventory, read once for the pass before any adapter lists. Native memories are Rhythm's own
        // records, so their stars come first in graph.maxMemories (Q-141), their projects counted, and the adapters share
        // what they leave; the catalog's own stars are made from the same read after the adapters, as before. A read that
        // fails lists no memory and reports none missing, so their stars stay from the last pass (MemoryStars); one whose
        // catalog part failed (Failure) still lists them. Only a subject whose listing was complete has its stars that are
        // not listed now reported missing (budgets are per scope with scopes on, memory.md 6.2).
        var (inventory, failure) = await Inventory(ct);
        replacedStars.Clear();
        var native = inventory is null ? default : MemoryStars.Records(inventory, memories,
            shares?.ToDictionary(s => s.Key, s => s.Value.Memories, StringComparer.Ordinal), DateTimeOffset.UtcNow, Vector, KeepTitleText);
        if (inventory is not null)
        {
            records.AddRange(native.Records);
            // With replaced memories to list (memory.tools on), their stars come after the adapters' and missing ones are
            // reported then, from everything listed; with none, here, as before.
            if (!native.Pending) records.AddRange(Missing(r => MemoryStars.IsNative(r.Key) && native.Complete(r.Key.Subject), native.Records));
            if (shares is null) memories -= native.Records.Count;
            else foreach (var subject in native.Records.GroupBy(r => r.Key.Subject))
                if (shares.TryGetValue(subject.Key, out var left)) shares[subject.Key] = left with { Memories = left.Memories - subject.Count() };
        }
        foreach (var adapter in adapters.Enabled.Where(a => a is IMemorySource or IDocumentSource))
        {
            var memory = adapter as IMemorySource;
            if (shares is not null) (memories, documents) = (shares.Values.Max(s => s.Memories), shares.Values.Max(s => s.Documents));
            if ((memory is null ? documents : memories) <= 0)
            {
                if ((memory is null ? config.MaxDocuments : config.MaxMemories) > 0)
                    notes.Add($"{adapter.Name} left out: graph.{(memory is null ? "maxDocuments" : "maxMemories")} is used by the adapters before it");
                continue;
            }
            var mine = stored.Where(s => adapter.Providers.ContainsKey(s.Key.Provider)).ToDictionary();
            var pass = new SourcePass(mine, Vector, Math.Max(memories, 0), Math.Max(documents, 0), embed is null ? null : model)
            {
                MapModel = MapModel,
                MemoryShares = shares?.ToDictionary(s => s.Key, s => Math.Max(s.Value.Memories, 0), StringComparer.Ordinal),
                DocumentShares = shares?.ToDictionary(s => s.Key, s => Math.Max(s.Value.Documents, 0), StringComparer.Ordinal),
                // An adapter gives whole texts only for its own providers' records, as it speaks only for them.
                TitleTexts = adapters.Titles is null ? null : (record, text) => { if (adapter.Providers.ContainsKey(record.Key.Provider)) KeepTitleText(record, text); },
            };
            SourceListing listing;
            try { listing = memory is not null ? await memory.List(pass, ct) : await ((IDocumentSource)adapter).List(pass, ct); }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException
                or UnauthorizedAccessException or TimeoutException || e is OperationCanceledException && !ct.IsCancellationRequested)
            { notes.Add($"{adapter.Name} unavailable ({e.GetType().Name})"); continue; }
            if (listing.Note is { Length: > 0 } said) notes.Add(said);
            // An adapter speaks only for its own providers.
            var own = listing.Records.Where(r => adapter.Providers.ContainsKey(r.Key.Provider)).ToList();
            records.AddRange(own);
            if (listing.Complete) records.AddRange(Missing(r => adapter.Providers.ContainsKey(r.Key.Provider), own));
            if (shares is null)
            {
                var listed = own.Count(r => r.Status == "current");
                if (memory is null) documents -= listed; else memories -= listed;
            }
            else foreach (var subject in own.Where(r => r.Status == "current").GroupBy(r => r.Key.Subject))
                if (shares.TryGetValue(subject.Key, out var left))
                    shares[subject.Key] = memory is null ? left with { Documents = left.Documents - subject.Count() } : left with { Memories = left.Memories - subject.Count() };
        }
        if (inventory is not null && native.Pending)
        {
            // Replaced facts (SB-P4-11) in the room the native memories and the adapters left.
            var more = native.Replaced(Math.Max(shares is null ? memories : 0, 0),
                shares?.ToDictionary(s => s.Key, s => Math.Max(s.Value.Memories, 0), StringComparer.Ordinal));
            records.AddRange(more);
            foreach (var r in more) replacedStars.Add(r.Key);
            records.AddRange(Missing(r => MemoryStars.IsNative(r.Key) && native.Complete(r.Key.Subject), [.. native.Records, .. more]));
        }
        records = await Retitle(records, ct);
        records.AddRange(CatalogRecords(records, inventory, failure));
        records = Bounded([.. records.Select(Tagged)]);
        if (mapFailed > 0) notes.Add($"{mapFailed} records without a map model vector this pass, so not compared on the map");
        foreach (var record in records.Where(r => r.Status == "current")) observed[record.Key] = record.Revision;
        return new(records);
    }

    // One pass holds at most RelationshipDiscovery.MaximumRecords records, or the rebuild would fail. Past it the
    // catalog's stars (data stars among them) come first, then the adapters' current records in their order; records
    // reported missing wait for a later pass, and what is left out ages off the map. With scopes on, each subject holds
    // at most its own share of those records, in the same order, so one scope's volume never pushes another's stars off;
    // a subject with no share (one not in the catalog's subjects, such as a former person, whom no reader reads) none. A
    // star served only to a narrower scope than its key's (a household copy of a person's private original, its Reader)
    // counts against that scope's share.
    List<DiscoveryRecord> Bounded(List<DiscoveryRecord> records)
    {
        // Native memories are kept with the catalog's stars: Rhythm's own records (Q-141), first in the list; a replaced
        // memory's star (SB-P4-11) with the adapters' records, after them in the list, so it never displaces a current one.
        int Order(DiscoveryRecord r) => r.Status != "current" ? 2
            : r.Key.Provider == CatalogStars.Provider || MemoryStars.IsNative(r.Key) && !replacedStars.Contains(r.Key) ? 0 : 1;
        if (config.Catalog.Scoped)
        {
            var kept = records.OrderBy(Order).GroupBy(r => r.ScopeSubject, StringComparer.Ordinal)
                .SelectMany(g => g.Take(config.Catalog.Share(RelationshipDiscovery.MaximumRecords, g.Key))).ToList();
            if (kept.Count < records.Count)
                notes.Add($"{records.Count - kept.Count} records past their scope's share of the map's {RelationshipDiscovery.MaximumRecords} left out");
            return kept;
        }
        if (records.Count <= RelationshipDiscovery.MaximumRecords) return records;
        notes.Add($"{records.Count - RelationshipDiscovery.MaximumRecords} records past the map's {RelationshipDiscovery.MaximumRecords} left out");
        return [.. records.OrderBy(Order).Take(RelationshipDiscovery.MaximumRecords)];
    }

    public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) =>
        Task.FromResult(principal == config.Principal && observed.TryGetValue(key, out var current)
            ? current == revision ? SourceCheck.Available : SourceCheck.Stale
            : SourceCheck.Unavailable);

    // Records a source owned last pass that a complete listing no longer contains: reported missing so they leave now.
    // Each notice is stamped with the pass's start, before any source listed, so it is older than every record the pass
    // observed. A record re-attributed between passes (listed now under a new subject, its old key reported missing) is
    // then the newest observation of its star, as the read rule (RelationshipEngine.Served) and the copy rule
    // (copy_readable) take it, from the pass that sees the move (docs/dev/memory.md section 8). The notice is still newer
    // than the old key's last observation, which an earlier pass made, so it replaces that row (RelationshipEngine.Save).
    IEnumerable<DiscoveryRecord> Missing(Func<DiscoveryRecord, bool> owned, IEnumerable<DiscoveryRecord> present)
    {
        var keys = present.Select(r => r.Key).ToHashSet();
        return stored.Values.Where(r => r.Status == "current" && owned(r) && !keys.Contains(r.Key)).ToArray()
            .Select(r => new DiscoveryRecord(r.Key, r.Scope, r.Kind, "Unavailable", null, null, null, started, [], "missing", Reader: r.Reader));
    }

    // The pass's read of the catalog and the memory tables (with the native memories' budget, graph.maxMemories), or why
    // it failed: one read, which the native memories' stars and the catalog's both come from.
    async Task<(CatalogInventory?, string?)> Inventory(CancellationToken ct)
    {
        try { return (await catalog(config.Catalog with { MemoryBudget = config.MaxMemories }, ct), null); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return (null, e.GetType().Name); }
    }

    // Data and document stars for the catalog's own records. The query reads the whole catalog for the configured
    // subjects, so a successful one is complete: stars it no longer yields are reported missing.
    IEnumerable<DiscoveryRecord> CatalogRecords(List<DiscoveryRecord> records, CatalogInventory? inventory, string? failure)
    {
        if (inventory?.Failure is { } failed) failure = failed;
        if (inventory is null || inventory.Failure is not null) { notes.Add($"catalog unavailable ({failure})"); return []; }
        if (inventory.Capped)
            notes.Add(config.Catalog.Scoped
                ? $"saved documents past a scope's share of {CatalogReader.MaxDocuments}; the map shows each scope's most recently changed"
                : $"more than {CatalogReader.MaxDocuments} saved documents; the map shows the {CatalogReader.MaxDocuments} most recently changed");
        if (inventory.MeasuredLeftOut > 0)
            notes.Add($"measurements of {inventory.MeasuredLeftOut} sources past their scope's share of the map's data stars left out");
        // A document an adapter already shows stands for its saved copy.
        var shownFiles = records.Where(r => r.Status == "current" && r.Key.Provider != CatalogStars.Provider)
            .Select(r => r.Key.ExternalId).ToHashSet(StringComparer.Ordinal);
        var stars = CatalogStars.Records(config.Catalog, inventory, shownFiles, DateTimeOffset.UtcNow, Vector, records).ToList();
        return [.. stars, .. Missing(r => r.Key.Provider == CatalogStars.Provider, stars)];
    }

    // Short titles from the TitleModel adapter (when one is on) for records whose title is still their excerpt. The
    // model is given the record's whole text where its source gave one (titleTexts, D-255: a native memory's, drafts and
    // replaced facts included, as every star; a note's; a Second Brain memory's), else its excerpt; a native memory's star
    // only its whole text. Nor is a star asked for with scopes on when no reader is served it (a subject with no share of
    // the map, which Bounded leaves out). A stored title is kept while the model and the text it is sent are unchanged
    // (ITitleModel.Revision); an unavailable model leaves excerpts. A failure stops asking for the rest of the pass and is
    // noted by its kind (TitleFailures), as are unusable answers: counts and kinds only, never a text or a title.
    async Task<List<DiscoveryRecord>> Retitle(List<DiscoveryRecord> records, CancellationToken ct)
    {
        Titles = null;
        if (adapters.Titles is not { } titles) return records;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new List<DiscoveryRecord>(records.Count);
        int asked = 0, titled = 0, unusable = 0;
        string? failure = null;
        foreach (var r in records)
        {
            if (r.Status != "current" || r.Overview is not { Length: > 0 } excerpt || r.Title != excerpt) { result.Add(r); continue; }
            var text = titleTexts.TryGetValue(r.Key, out var whole) ? whole : MemoryStars.IsNative(r.Key) ? null : excerpt;
            if (text is null || config.Catalog.Scoped && config.Catalog.Share(RelationshipDiscovery.MaximumRecords, r.ScopeSubject) <= 0)
            { result.Add(r); continue; }
            var revision = titles.Revision(text);
            if (stored.TryGetValue(r.Key, out var previous) && previous.TitleRevision == revision && previous.Status == "current")
            { result.Add(r with { Title = previous.Title, TitleRevision = revision }); continue; }
            string? title = null;
            if (failure is null && asked < TitlesPerPass && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TitleBudget)
            {
                asked++;
                try
                {
                    title = await titles.Title(text, ct);
                    if (title is { Length: > 0 and <= 1000 }) titled++; else unusable++;
                }
                catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException
                    or InvalidDataException or TimeoutException || e is OperationCanceledException && !ct.IsCancellationRequested)
                { failure = TitleFailures.Kind(e); }
            }
            result.Add(title is { Length: > 0 and <= 1000 } t ? r with { Title = t, TitleRevision = revision } : r);
        }
        Titles = new(asked, titled, unusable, failure);
        if (failure is not null)
            notes.Add($"titles stopped after {asked} asked, {titled} titled: {failure}"
                + (failure == "timeout" ? " (a model loading cold can take longer than the adapter's 30 s)" : "")
                + "; the rest keep their excerpts until a later rebuild");
        if (unusable > 0) notes.Add($"titles: {unusable} unusable answers, their excerpts kept");
        return result;
    }

    // A stored vector for unchanged content that this pass's model made, else a fresh one from the local model (when it
    // is on): a model change embeds every record again as its text is read.
    // With a map model the record's map vector is kept beside it the same way (Mapped).
    float[]? Vector(ResourceKey key, string contentRevision, string title, string text)
    {
        if (stored.TryGetValue(key, out var previous) && previous.ContentRevision == contentRevision && previous.Embedding is { } kept
            && SimilaritySuggestions.ModelOf(previous) == model) return Mapped(kept, title, text);
        if (embed is null) return null;
        try { return string.IsNullOrWhiteSpace(text) ? null : Mapped(Made(embed(title, text)), title, text); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return null; }
    }
    // A live vector's map vector: the one stored for it (Prime: the same content revision and map model), else a new one
    // from the same title and text. A map vector that cannot be made, for whatever reason short of the pass being stopped,
    // leaves the record off the map's similar links, never off the map, and is counted in the notes: the map model never
    // fails a rebuild.
    float[]? Mapped(float[]? live, string title, string text)
    {
        if (live is null || MapModel is null || mapped.ContainsKey(live)) return live;
        try
        {
            if (MapEmbed!(title, text) is { } vector) mapped[live] = vector;
            else Interlocked.Increment(ref mapFailed);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Interlocked.Increment(ref mapFailed); }
        return live;
    }
    float[]? Made(float[]? vector)
    {
        if (vector is not null) made[vector] = model!;
        return vector;
    }
    // Each listed vector names the model that made it. One this pass did not make, reuse or store last pass has no known
    // model, so it is left out rather than compared with another model's.
    // Keep a configured map model's vectors beside their original live vectors even while loading. With the
    // key removed or changed, Prime did not retain the old map vectors. Only a loaded model drives map links.
    DiscoveryRecord Tagged(DiscoveryRecord record) => record.Embedding is null ? record with { EmbeddingModel = null, MapEmbedding = null, MapEmbeddingModel = null }
        : made.TryGetValue(record.Embedding, out var by) ? record with { EmbeddingModel = by,
            MapEmbedding = mapped.TryGetValue(record.Embedding, out var map) ? map : null,
            MapEmbeddingModel = map is null ? null : MapEmbedModel }
        : record with { Embedding = null, EmbeddingModel = null, MapEmbedding = null, MapEmbeddingModel = null };
}
