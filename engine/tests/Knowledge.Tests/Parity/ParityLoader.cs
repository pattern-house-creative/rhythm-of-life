using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Adapters;
using Knowledge.Adapters.Csv;
using Knowledge.Adapters.Drive;
using Knowledge.Adapters.Notes;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests.Parity;

// How the corpus is put into Rhythm (baseline.md section 3). The two modes answer different questions:
// - Copies: every memory, note and Drive document is also saved as a harness save_document copy, with its full text,
//   chunked and embedded. That is the only path today that feeds both search_knowledge and semantic_search, so this mode
//   measures what today's tools do with the text in hand. An install holds no such copy unless an AI client saved one.
// - Installed: only what the adapters write themselves, snapshot rows with empty content, each under the policy of the
//   adapter it stands for (Second Brain reference-only, notes an authorised archive, Drive a live source), and the
//   passages the engine's index job gives the texts the settings list (adapters.embeddings.sources, SB-P2-05; by default
//   what a new install's settings list, RHYTHM_PARITY_SOURCES to change it, `none` for an existing install's: no
//   passages). This mode measures what an install's tools find.
public enum ParityMode { Copies, Installed }

// How one corpus record landed in Rhythm: in the catalog (a resource row), on the map (the stars that stand for it),
// both or neither, why, and which of its fields Rhythm has nowhere to keep today.
public sealed record ParityRepresentation(string Id, string Kind, string? Catalog, string? Map, string? Note, IReadOnlyList<string> Lost);

// Loads the corpus into a disposable database the way Rhythm can hold it today, through the engine's own write paths:
// - memories, notes and Drive documents, in copies mode only: a harness's save_document copy (provider `documents`;
//   subject alex, sam, or household for Family and the other shared scopes; chunked and embedded for semantic_search
//   when the local model is on). A former person's records get none: save_document takes a subject in people only.
// - a former person (corpus `formerPeople`): installed while still in people, so their subject exists, then gone from
//   the settings; their records reach the map under their own subject, as an adapter keeps listing them.
// - the same records on the map, in both modes, listed by ParityMapSource (and in installed mode ParityDriveSource),
//   which stand in for the adapters that put memories, notes and Drive files there (second-brain, notes, drive):
//   eligible records only, typed links as the source's own references. In copies mode their records share
//   save_document's external ids, so the catalog shows each one once.
// - measurements: archived source rows with samples, as a CSV import stores them (provider `csv`); the catalog turns
//   them into data stars.
// - dashboards: not loaded. They live in the screens area's tables, and no retrieval path reads them.
// - passages, in both modes when the local model is on: the index job run to the end of a sweep over the sources the
//   settings list (notes and Drive standing for their adapters through the stand-ins' SearchText). A reference-only
//   provider gets none, so in copies mode, where every stand-in record is reference-only, only the copies have passages.
public sealed class ParityLoad : IAsyncDisposable
{
    public required ParityMode Mode { get; init; }
    public required ParityCorpus Corpus { get; init; }
    public required Database Db { get; init; }
    public required Guid Harness { get; init; }
    public required RelationshipHost Host { get; init; }
    public required AdapterRegistry Registry { get; init; }
    public required Embeddings Embeddings { get; init; }
    public required string RefreshResult { get; init; }
    public required IReadOnlyList<string> RefreshNotes { get; init; }
    public required IReadOnlyList<ParityRepresentation> Items { get; init; }
    // Catalog external id (save_document's file id, or a measurement source's) to corpus id.
    public required IReadOnlyDictionary<string, string> ByExternalId { get; init; }
    // Corpus id to its catalog resource (for get_resource): the save_document copy in copies mode, the snapshot row in
    // installed mode, the archived source row for a measurement series.
    public required IReadOnlyDictionary<string, Guid> ResourceIds { get; init; }
    // Map graph id to the corpus ids it stands for: one for an adapter star, a person's series for a data star.
    public required IReadOnlyDictionary<string, string[]> ByGraphId { get; init; }
    // The graph ids of the stand-in adapters' own stars.
    public required IReadOnlySet<string> SourceStars { get; init; }
    public required string State { get; init; }
    // The sources the index covered (adapters.embeddings.sources' words), and how many rows and passages it indexed.
    public IReadOnlyList<string> Sources { get; init; } = [];
    public (int Rows, int Passages) Indexed { get; init; }
    // What semantic_search serves, as the engine builds it from the same settings: the copies and those sources.
    public PassageSources Covered { get; init; } = PassageSources.CopiesOnly;
    // The instruction each question is embedded after (ParityModelChoice, SB-P2-06), none by default (D-068).
    public string? QueryInstruction { get; init; }
    // The map model loaded for the map's similar and copy links (graph.similarity.model, D-256), none by default; how long
    // the map's rebuild took, and the links it drew: similar ones, those of them the mutual rule drew, and copies.
    public EmbeddingModel? MapModel { get; init; }
    public double RefreshMs { get; init; }
    public (int Similar, int Mutual, int Copies) MapLinks { get; init; }
    // What embeds a question for semantic_search and combined retrieval: the loaded model, after its instruction when one
    // is given; null without the model.
    public IEmbedder? Query => Embeddings.Model is not { } model ? null : QueryInstruction is { } instruction ? new QueryInstruction(model, instruction) : model;
    // The model as reports and locks name it: its id (and the instruction, ParityModelChoice.Label); null without it.
    public string? Model => Embeddings.Model is { } model ? ParityModelChoice.Name(model.Id, QueryInstruction) : null;
    // The star a seed opens: the adapter star when there is one.
    public string GraphId(string corpusId) => ByGraphId.Where(p => p.Value.Contains(corpusId)).Select(p => p.Key)
        .OrderBy(k => SourceStars.Contains(k) ? 0 : 1).First();

    public ValueTask DisposeAsync()
    {
        Embeddings.Dispose();
        try { Directory.Delete(State, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

public static class ParityLoader
{
    public const string FilePrefix = "parity:";
    // The Second Brain personal workspace whose owner is the household: private rows under the household.
    public const string HouseholdPrivate = "household:private";
    // A personal scope is its person's subject; Family and a team workspace are the household's, as the Second Brain
    // adapter maps a team; its personal workspace with owner household is the household's too, stored private.
    public static string Subject(string scope) => scope.StartsWith("personal:", StringComparison.Ordinal) ? scope["personal:".Length..] : RhythmConfig.Household;
    public static string Visibility(string scope) => scope.StartsWith("personal:", StringComparison.Ordinal) || scope == HouseholdPrivate ? "private" : "shared";
    // The settings while the former people were still in people, as when their records were written. Only installed
    // from: the settings in force (RhythmConfig.Current) stay the tests' own.
    static RhythmConfig Before(ParityCorpus corpus) => corpus.FormerPeople.Length == 0 ? TestSettings.Use() : RhythmConfig.Parse(TestSettings.Yaml.Replace(
        "people:\n", "people:\n" + string.Concat(corpus.FormerPeople.Select(p => $"  - {{ id: {p.Id}, name: {p.Name} }}\n")), StringComparison.Ordinal));

    // The sources the index covers: RHYTHM_PARITY_SOURCES (a comma list of adapters.embeddings.sources' words, or none),
    // by default what the example settings list for a new install.
    public static IReadOnlyList<string> Sources(string? list) => list switch
    {
        null or "" => EmbeddingSettings.NewInstallSources,
        "none" => [],
        _ => [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Select(s =>
            EmbeddingSettings.KnownSources.Contains(s) ? s : throw new ArgumentException($"RHYTHM_PARITY_SOURCES lists {string.Join(", ", EmbeddingSettings.KnownSources)} or none, not {s}"))],
    };

    // A model whose vectors are not 384-dimensional (bge-base, 768) makes passages no install can store: for a measurement
    // only (SB-P2-06, D-249), this throwaway database's passage column takes vectors of any width, and every function that
    // casts a passage's vector to 384 dimensions (save_document) casts it to its own width instead. Never an install's.
    static async Task WidenPassages(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($$"""
            ALTER TABLE resource_chunks ALTER COLUMN embedding TYPE public.vector;
            DO $$ DECLARE f record; BEGIN
              FOR f IN SELECT p.oid FROM pg_proc p WHERE p.prosrc LIKE '%vector({{EmbeddingModels.PassageDimensions}})%' LOOP
                EXECUTE replace(pg_get_functiondef(f.oid), 'public.vector({{EmbeddingModels.PassageDimensions}})', 'public.vector');
              END LOOP;
            END $$;
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // `model` is the embedding model loaded from `modelDirectory` (MiniLM unless given: ParityModelChoice), `instruction`
    // what each question is embedded after, and `hostSettings` the settings the map is built with (the tests' own unless
    // given: a benchmark's similarity thresholds, SB-P2-13). `mapModel` and `mapMaxLinks` set graph.similarity.model and
    // graph.similarity.maxLinks on the tests' own settings (D-256, D-258).
    public static async Task<ParityLoad> Load(NpgsqlDataSource source, ParityCorpus corpus, string? modelDirectory, ParityMode mode = ParityMode.Copies,
        CancellationToken ct = default, IReadOnlyList<string>? sources = null, EmbeddingModel? model = null, string? instruction = null,
        RhythmConfig? hostSettings = null, EmbeddingModel? mapModel = null, string? mapDirectory = null, int? mapMaxLinks = null)
    {
        if ((mapModel is not null || mapMaxLinks is not null) && hostSettings is not null)
            throw new ArgumentException("A map model or a cap takes the tests' own settings with graph.similarity.model and maxLinks");
        // A map model is set as an install sets it, graph.similarity.model, and loaded from its own folder (D-256); a cap as
        // graph.similarity.maxLinks (D-258).
        if (mapModel is not null || mapMaxLinks is not null)
            hostSettings = RhythmConfig.Parse(TestSettings.Yaml + "\ngraph:\n  similarity:\n" + (mapModel is null ? "" : $"    model: {mapModel.Name}\n")
                + (mapMaxLinks is null ? "" : $"    maxLinks: {mapMaxLinks}\n"));
        sources ??= Sources(Environment.GetEnvironmentVariable("RHYTHM_PARITY_SOURCES"));
        var config = TestSettings.Use();
        var mapSource = new ParityMapSource(corpus, mode);
        var driveSource = new ParityDriveSource(corpus, mapSource);
        IAdapter[] adapters = mode == ParityMode.Copies
            ? [mapSource, new CsvAdapter()]
            : [mapSource, driveSource, new CsvAdapter()];
        var registry = new AdapterRegistry(adapters);
        var db = new Database(source, registry.Policies(config));
        await db.Migrate(ct);
        if ((model ?? EmbeddingModels.Default).Dimensions != EmbeddingModels.PassageDimensions) await WidenPassages(source, ct);
        var principals = new Principals(source);
        // Installed while the former people were still listed, so their subjects exist as a real install's would; then
        // they leave people, and the rest of the load runs with today's settings.
        await principals.Install(Before(corpus), ct);
        var formerSubjects = corpus.FormerPeople.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var state = Path.Combine(Path.GetTempPath(), "rhythm-parity-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(state);
        var harness = await principals.Create("parity-eval", "harness", null, Path.Combine(state, "harness.token"), ct);
        File.Delete(Path.Combine(state, "harness.token"));
        var graph = await principals.System(Principals.GraphName, ct);

        var embeddings = new Embeddings(model ?? EmbeddingModels.Default);
        if (!string.IsNullOrWhiteSpace(modelDirectory)) embeddings.TryLoad(modelDirectory);
        if (mapModel is not null && !string.IsNullOrWhiteSpace(mapDirectory)) embeddings.TryLoadMap(mapModel, mapDirectory);

        var byExternal = new Dictionary<string, string>(StringComparer.Ordinal);
        var lost = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode == ParityMode.Copies)
        {
            // Corpus order, one save each: search_knowledge has no ranking and lists the newest indexed first.
            var store = new DocumentStore(source, embeddings);
            foreach (var text in corpus.Texts)
            {
                // save_document takes only a subject in people, which a former person has left: no copy of theirs.
                if (formerSubjects.Contains(Subject(text.Scope)))
                {
                    catalog[text.Id] = "none: save_document takes a subject in people only, and this person has left";
                    continue;
                }
                await store.Save(harness, new DocumentSnapshot(FilePrefix + text.Id, text.Title, text.Url, text.Text, text.Modified,
                    Subject: Subject(text.Scope)), ct);
                byExternal[FilePrefix + text.Id] = text.Id;
                catalog[text.Id] = $"documents ({Subject(text.Scope)}, {Visibility(text.Scope)})";
            }
        }
        foreach (var m in corpus.Memories)
        {
            var missing = new List<string> { "kind", "status", "importance", "volatility", "tags", "author", "source channel", "project", "links" };
            if (m.ValidFrom is not null || m.ValidUntil is not null || m.ReplacedBy is not null) missing.Add("validity window");
            if (m.Standing) missing.Add("standing instruction");
            if (m.Expect is { Held: true }) missing.Add("held for review");
            if (m.Stale is not null) missing.Add("review date");
            if (m.Decision is not null) missing.Add("decision confidence");
            if (m.When is not null) missing.Add("due date");
            if (m.Commitment is not null) missing.Add("commitment");
            if (m.SharedFrom is not null) missing.Add("shared from personal");
            lost[m.Id] = missing;
        }
        foreach (var n in corpus.Notes) lost[n.Id] = ["author", "tags", "folder"];
        foreach (var d in corpus.Documents) lost[d.Id] = d.Revisions.Length > 0 ? ["earlier revisions", "source revision id"] : ["source revision id"];
        // As installed, a star's text is read from its source when it opens and never reaches the catalog.
        if (mode == ParityMode.Installed) foreach (var t in corpus.Texts) lost[t.Id].Insert(0, "text in the catalog");

        // One transaction per series: a row's indexed_at is its transaction's start and search_knowledge lists the newest
        // indexed first, so series written together would tie and come back in a different order on every run.
        foreach (var s in corpus.Measurements)
        {
            await using var c = await source.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            var external = FilePrefix + s.Id;
            await db.Upsert(new ResourceInput(s.Subject, CsvAdapter.Provider, external, "body-readings", Visibility(s.Scope), s.Title, null,
                Revision: Hashing.Hash(JsonSerializer.Serialize(s.Points)), SourceUpdated: s.Points.Max(p => p.Date).ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc),
                Timezone: corpus.Timezone, Domain: "health"),
                s.Points.Select(p => new Measurement(s.Metric, p.Value, s.Unit, p.Date, p.Date.ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc),
                    p.Date.ToString("yyyy-MM-dd"), s.Device)), c, tx, ct);
            byExternal[external] = s.Id;
            catalog[s.Id] = $"csv ({s.Subject}, {Visibility(s.Scope)}), {s.Points.Length} samples";
            lost[s.Id] = [];
            await tx.CommitAsync(ct);
        }

        var host = Engine.Host(db, hostSettings ?? config, registry, () => graph, state, embeddings);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var refreshed = await host.Refresh(false, ct);
        var refreshMs = timer.Elapsed.TotalMilliseconds;
        var mapLinks = (Similar: 0, Mutual: 0, Copies: 0);
        await using (var cmd = source.CreateCommand("""
            SELECT count(*) FILTER (WHERE type='similar_to')::int,
              count(*) FILTER (WHERE type='similar_to' AND evidence LIKE '%each among the other%s closest%')::int,
              count(*) FILTER (WHERE type='copy_of')::int
            FROM relationships WHERE engine=@engine AND lifecycle='active'
            """))
        {
            cmd.Parameters.AddWithValue("engine", RelationshipEngine.Owner);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            mapLinks = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }

        // The index job, as the scheduler runs it each minute, to the end of one sweep: the stand-ins read their records'
        // text as the notes and Drive adapters do (SearchText), each only where its adapter is on and listed.
        var indexed = (Rows: 0, Passages: 0);
        ISearchableSource[] readers = [.. sources.Contains(EmbeddingSettings.Notes) ? [mapSource] : Array.Empty<ISearchableSource>(),
            .. sources.Contains(EmbeddingSettings.Drive) && mode == ParityMode.Installed ? [driveSource] : Array.Empty<ISearchableSource>()];
        var covered = PassageSources.Of(false, sources.Contains(EmbeddingSettings.Documents), sources.Contains(EmbeddingSettings.Memories), readers);
        if (embeddings.Model is { } loaded)
        {
            var job = new EmbeddingBackfill(db);
            for (var run = await job.Step(loaded, ct, sources: covered); !run.Finished; run = await job.Step(loaded, ct, sources: covered))
            {
                if (run.Failed > 0) throw new InvalidOperationException($"The index job failed on {run.Failed} rows");
                indexed = (indexed.Rows + run.Indexed, indexed.Passages + run.Passages);
            }
        }

        // Catalog rows by corpus id: the saved copies and series (source rows), and in installed mode the stand-in
        // adapters' snapshot rows, which the map rebuild has just written.
        var resources = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var cmd = source.CreateCommand("SELECT external_id,id FROM resources WHERE relationship_snapshot IS NULL AND external_id LIKE @prefix"))
        {
            cmd.Parameters.AddWithValue("prefix", FilePrefix + "%");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) resources[byExternal[reader.GetString(0)]] = reader.GetGuid(1);
        }
        if (mode == ParityMode.Installed)
        {
            await using var cmd = source.CreateCommand(
                "SELECT relationship_snapshot->'Key'->>'ExternalId',id FROM resources WHERE relationship_snapshot IS NOT NULL AND provider=ANY(@providers)");
            cmd.Parameters.AddWithValue("providers", ParityMapSource.InstalledProviders);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) resources[reader.GetString(0)[FilePrefix.Length..]] = reader.GetGuid(1);
        }
        var withPassages = new HashSet<Guid>();
        await using (var cmd = source.CreateCommand("SELECT DISTINCT resource_id FROM resource_chunks"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) withPassages.Add(reader.GetGuid(0));

        // Graph ids: the adapter's stars and projects; in copies mode, the catalog's document star for a saved copy the
        // adapter does not list (the catalog shows a copy only when no adapter star stands for it); each person's data
        // stars, one per catalog group. Which of them the map really holds is read back from it.
        var byGraph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceStars = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, kind, scope) in ParityMapSource.Listed(corpus))
        {
            var star = mapSource.Key(scope, id, kind).GraphId;
            byGraph[star] = [id];
            kinds[star] = kind;
            sourceStars.Add(star);
        }
        if (mode == ParityMode.Copies)
            foreach (var t in corpus.Texts)
            {
                var copy = new ResourceKey(Subject(t.Scope), CatalogStars.Provider, DocumentOriginals.Provider, $"{Subject(t.Scope)}:{FilePrefix}{t.Id}").GraphId;
                byGraph[copy] = [t.Id];
                kinds[copy] = "document star of its saved copy";
            }
        var groups = RelationshipCatalog.For(config);
        foreach (var star in corpus.Measurements.GroupBy(s => (s.Subject, s.Scope, Group: groups.Group(CsvAdapter.Provider, s.Metric).Id)))
        {
            var key = new ResourceKey(star.Key.Subject, CatalogStars.Provider, CatalogStars.DataAccount(CsvAdapter.Provider),
                $"{star.Key.Subject}:{star.Key.Group}" + (Visibility(star.Key.Scope) == "private" ? "" : ":shared"));
            byGraph[key.GraphId] = star.Select(s => s.Id).ToArray();
            kinds[key.GraphId] = $"data star {star.Key.Group}";
        }
        var shown = await host.Known(harness, byGraph.Keys, ct);
        var map = byGraph.Where(p => shown.Contains(p.Key)).SelectMany(p => p.Value.Select(id => (id, kind: kinds[p.Key])))
            .GroupBy(x => x.id).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(x => x.kind)), StringComparer.Ordinal);

        var listed = ParityMapSource.Listed(corpus).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var items = new List<ParityRepresentation>();
        foreach (var t in corpus.Texts)
        {
            var left = t.Kind == "memory" && corpus.Memories.Single(m => m.Id == t.Id) is var m && !ParityMapSource.Eligible(m, corpus.Now)
                ? "the adapter leaves it out (" + ParityMapSource.Why(m, corpus.Now) + ")" : null;
            var row = mode == ParityMode.Copies ? catalog[t.Id]
                : listed.ContainsKey(t.Id) ? $"snapshot row ({mapSource.ProviderOf(t.Kind)}, {Subject(t.Scope)}, {Visibility(t.Scope)}), empty content, "
                    + (resources.TryGetValue(t.Id, out var rid) && withPassages.Contains(rid) ? "passages from its source's text" : "no passages") : null;
            items.Add(new(t.Id, t.Kind, row, map.GetValueOrDefault(t.Id), left, lost[t.Id]));
        }
        foreach (var s in corpus.Measurements) items.Add(new(s.Id, "measurements", catalog[s.Id], map.GetValueOrDefault(s.Id), null, lost[s.Id]));
        foreach (var d in corpus.Dashboards)
            items.Add(new(d.Id, "dashboard", null, null, "not loaded: dashboards live outside the resource catalog and no retrieval path reads them", ["all"]));
        // Phase 1's derived records: for the scope tests (SB-P1-06, SB-P1-10), outside every retrieval path.
        foreach (var (id, kind) in corpus.Insights.Select(i => (i.Id, "insight")).Concat(corpus.InsightRuns.Select(r => (r.Id, "insight run")))
            .Concat(corpus.Pins.Select(p => (p.Id, "pin"))).Concat(corpus.Corners.Select(c => (c.Id, "corner card"))).Concat(corpus.Shares.Select(s => (s.Id, "share"))))
            items.Add(new(id, kind, null, null, "not loaded: for phase 1's scope tests; no retrieval path reads it", ["all"]));

        return new ParityLoad
        {
            Mode = mode, Corpus = corpus, Db = db, Harness = harness, Host = host, Registry = registry, Embeddings = embeddings,
            RefreshResult = refreshed, RefreshNotes = host.Notes, Items = items, ByExternalId = byExternal, ResourceIds = resources, ByGraphId = byGraph,
            SourceStars = sourceStars, State = state, Sources = sources, Indexed = indexed, Covered = covered with { Copies = true },
            QueryInstruction = instruction, MapModel = embeddings.Map?.Model is null ? null : mapModel, RefreshMs = refreshMs, MapLinks = mapLinks
        };
    }
}

// Lists the corpus's memories, notes and Drive documents, and the projects they belong to, as stars. Memories follow
// the Second Brain adapter's eligibility (SecondBrainAdapter.Eligible: no drafts, held, retracted or replaced facts)
// and title (SecondBrainAdapter.Title); notes get the notes adapter's plain overview; documents none, as Drive.
// In copies mode every record is this source's, under one reference-only provider. In installed mode memories and
// projects take the Second Brain adapter's policy and notes the notes adapter's, each under its own provider, and the
// Drive documents are listed by ParityDriveSource under the Drive adapter's.
internal sealed class ParityMapSource(ParityCorpus corpus, ParityMode mode = ParityMode.Copies) : IMemorySource, ISearchableSource
{
    public const string Provider = "parity";
    public const string MemoryProvider = "parity-memory", NotesProvider = "parity-notes", DriveProvider = "parity-drive";
    public static readonly string[] InstalledProviders = [MemoryProvider, NotesProvider, DriveProvider];
    const int OverviewText = 300;
    public string Name => "parity";
    public ParityMode Mode { get; } = mode;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = mode == ParityMode.Copies
        ? new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.ReferenceOnly, "household") }
        : new Dictionary<string, ProviderPolicy>
        {
            [MemoryProvider] = new SecondBrainAdapter().Providers[SecondBrainAdapter.Provider],
            [NotesProvider] = new NotesAdapter().Providers[NotesAdapter.Provider],
        };

    public string ProviderOf(string kind) => Mode == ParityMode.Copies ? Provider
        : kind switch { "note" => NotesProvider, "document" => DriveProvider, _ => MemoryProvider };
    public ResourceKey Key(string scope, string id, string kind) =>
        new(ParityLoader.Subject(scope), ProviderOf(kind), ProviderOf(kind), ParityLoader.FilePrefix + id);

    static string Validity(ParityMemory m, DateTimeOffset now) =>
        m.ReplacedBy is not null || m.ValidUntil is { } until && until < DateOnly.FromDateTime(now.UtcDateTime) ? "replaced" : "current";
    static string[] Tags(ParityMemory m) => [.. m.Tags, .. m.Status switch { "draft" => new[] { "status:draft" }, "deprecated" => ["status:retracted"], _ => [] }];
    public static bool Eligible(ParityMemory m, DateTimeOffset now) => SecondBrainAdapter.Eligible(Tags(m), false, Validity(m, now));
    public static string Why(ParityMemory m, DateTimeOffset now) => m.Status == "draft" ? "a draft" : m.Status == "deprecated" ? "retracted" : $"validity {Validity(m, now)}";

    // What the map lists: (corpus id, kind, scope), projects included when a listed record belongs to one.
    public static IReadOnlyList<(string Id, string Kind, string Scope)> Listed(ParityCorpus corpus)
    {
        var texts = corpus.Texts.Where(t => t.Kind != "memory" || Eligible(corpus.Memories.Single(m => m.Id == t.Id), corpus.Now)).ToArray();
        var projects = texts.SelectMany(Projects).Distinct().ToHashSet(StringComparer.Ordinal);
        return [.. texts.Select(t => (t.Id, t.Kind, t.Scope)),
            .. corpus.Projects.Where(p => projects.Contains(p.Id)).Select(p => ("project:" + p.Id, "project", p.Scope))];
    }
    static IEnumerable<string> Projects(ParityText t) =>
        t.Links.Where(l => l.Type == "part_of_project").Select(l => l.Target).Concat(t.Project is { } p ? [p] : []);

    public Task<SourceListing> List(SourcePass pass, CancellationToken ct) =>
        Task.FromResult(new SourceListing(Records(pass).Where(r => Providers.ContainsKey(r.Key.Provider)).ToArray(), true));

    // Every listed record in both modes, whichever stand-in source lists it; references may cross the two sources.
    public IReadOnlyList<DiscoveryRecord> Records(SourcePass pass)
    {
        var now = DateTimeOffset.UtcNow;
        var listed = Listed(corpus).ToDictionary(x => x.Id, StringComparer.Ordinal);
        ResourceKey KeyOf(string id) => Key(listed[id].Scope, id, listed[id].Kind);
        var records = new List<DiscoveryRecord>();
        foreach (var t in corpus.Texts.Where(t => listed.ContainsKey(t.Id)))
        {
            var key = KeyOf(t.Id);
            var references = new List<Reference>();
            foreach (var l in t.Links.Where(l => l.Type != "part_of_project"))
            {
                var target = listed.ContainsKey(l.Target) ? KeyOf(l.Target) : null;
                if (target is null) continue;
                // Second Brain's provenance mapping: a person's link is verified, the system's inferred, the rest suggested.
                var (verification, strength) = l.Provenance switch { "explicit" => ("verified", 1.0), "system" => ("inferred", 1.0), _ => ("suggested", 0.75) };
                references.Add(new(target, null, "parity:" + l.Type, "", l.Type, true, verification, strength, l.Type != "relates_to"));
            }
            foreach (var project in Projects(t).Distinct())
                if (listed.ContainsKey("project:" + project))
                    references.Add(new(KeyOf("project:" + project), null, "parity:project", "", "in_project", true, "verified"));
            // A record's links are part of its observed state, so a change in them is a new revision. With scopes on only
            // its links to records of its own subject count (SourcePass.Scoped): not every reader of a Family record reads
            // a private record it links to, so whether that record is there never shows in the Family record's revision,
            // as a Second Brain memory names only links within its own workspace (SecondBrainAdapter.LinkLayer).
            var fingerprinted = pass.Scoped ? references.Where(r => r.Target!.Subject == key.Subject) : references;
            var revision = Hashing.Hash(JsonSerializer.Serialize(new { t.Title, t.Text, links = fingerprinted.Select(r => new[] { r.Target!.GraphId, r.NativeType }) }));
            var created = corpus.Memories.FirstOrDefault(m => m.Id == t.Id)?.CreatedAt;
            var overview = t.Kind switch
            {
                "memory" => t.Title,
                "note" => Plain(t.Text),
                _ => null
            };
            records.Add(new(key, ParityLoader.Visibility(t.Scope), t.Kind, t.Title, t.Url, revision, t.Modified, now, references,
                CreatedAt: created, ContentRevision: revision, Embedding: pass.Vector(key, revision, t.Title, t.Text),
                Groups: [new("parity:" + t.Scope, corpus.Scope(t.Scope).Name ?? "Personal")], Overview: overview));
        }
        foreach (var p in corpus.Projects.Where(p => listed.ContainsKey("project:" + p.Id)))
            records.Add(new(KeyOf("project:" + p.Id), ParityLoader.Visibility(p.Scope), "project", p.Name, null,
                Hashing.Hash(p.Name + "\n" + p.Description), null, now, []));
        return records;
    }

    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) =>
        Task.FromResult(corpus.Texts.FirstOrDefault(t => ParityLoader.FilePrefix + t.Id == record.Key.ExternalId)?.Text);

    // A note's text for its passages, as the notes adapter reads it (NotesAdapter.SearchText). Only notes: the Second Brain
    // adapter reads no text for the index, and its provider is reference-only, which gets no passages anyway. The corpus
    // never changes during a run, so the text read is always the listed revision's.
    public Task<SourceText?> SearchText(DiscoveryRecord record, CancellationToken ct) =>
        Task.FromResult(record.Kind == "note" && record.Revision is { } revision && corpus.Texts.FirstOrDefault(t => ParityLoader.FilePrefix + t.Id == record.Key.ExternalId) is { } text
            ? new SourceText(text.Text, revision) : null);

    static string Plain(string text)
    {
        var plain = string.Join(' ', text.Replace("[[", "").Replace("]]", "").Split((char[])['\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Trim('#').Length > 0));
        return plain.Length <= OverviewText ? plain : plain[..(OverviewText - 1)] + "…";
    }
}

// Installed mode: the corpus's Drive documents on the map as the Drive adapter lists them, under its policy (a live
// source). Its live-source check answers as DriveAdapter.Current does: only a bare Drive file id at its current
// revision is current. A snapshot row's external id is the engine's namespaced catalog id (ResourceKey.CatalogId),
// never a bare file id, so search_knowledge serves no Drive star, on an install as here. semantic_search checks a
// passage by its star's own source id and revision (the snapshot's Key.ExternalId and Revision), which for the real
// adapter are the file id and Drive's version; this stand-in lists its records under the corpus's ids and revisions
// instead, so it takes a record it listed, at the revision it listed it with, as current too: as Drive would.
internal sealed partial class ParityDriveSource(ParityCorpus corpus, ParityMapSource map) : IDocumentSource, ISearchableSource
{
    public string Name => "parity-drive";
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [ParityMapSource.DriveProvider] = new DriveAdapter().Providers[DriveAdapter.Provider] };

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> listed = new(StringComparer.Ordinal);
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var records = map.Records(pass).Where(r => Providers.ContainsKey(r.Key.Provider)).ToArray();
        foreach (var r in records) if (r.Revision is { } revision) listed[r.Key.ExternalId] = revision;
        return Task.FromResult(new SourceListing(records, true));
    }
    public Task<JsonObject?> Contents(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<JsonObject?>(null);
    // A document's text for its passages, as the Drive adapter reads it (DriveAdapter.SearchText): only when it reads as
    // words, at the listed revision.
    public Task<SourceText?> SearchText(DiscoveryRecord record, CancellationToken ct) =>
        Task.FromResult(record.Kind == "document" && record.Revision is { } revision && corpus.Texts.FirstOrDefault(t => ParityLoader.FilePrefix + t.Id == record.Key.ExternalId) is { } text
            && SimilaritySuggestions.Readable(text.Text) ? new SourceText(text.Text, revision) : null);
    // RHYTHM_PARITY_DRIVE_LATENCY_MS: how long each check takes, standing in for the call to Drive when the read-time check's
    // cost is measured (D-085); none by default. Only a check the Drive adapter would send to Drive waits: one it refuses
    // by the id's form (a catalog id, as search_knowledge asks) answers at once, as the adapter's does.
    public async Task<bool> Current(string provider, string externalId, string? revision, CancellationToken ct)
    {
        if (provider != ParityMapSource.DriveProvider || revision is null) return false;
        var file = DriveId().IsMatch(externalId);
        if (!file && !listed.ContainsKey(externalId)) return false;
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
        return file && corpus.Documents.Any(d => d.Source.FileId == externalId && d.Source.Revision == revision)
            || listed.TryGetValue(externalId, out var at) && at == revision;
    }
    static readonly TimeSpan Latency = int.TryParse(Environment.GetEnvironmentVariable("RHYTHM_PARITY_DRIVE_LATENCY_MS"), out var ms) && ms > 0
        ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    // DriveAdapter's own file-id rule.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$")] private static partial Regex DriveId();
}
