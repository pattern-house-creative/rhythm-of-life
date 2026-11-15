using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// Opt-in: the golden queries through today's retrieval paths, never part of an ordinary run. Needs RHYTHM_PARITY_EVAL=1
// and a disposable Postgres (RHYTHM_TEST_DB, as the database tests):
//   RHYTHM_PARITY_EVAL=1 sh engine/test-db.sh --filter ParityEvaluation
// RHYTHM_MODEL_DIR (the engine's own setting) points at the pinned MiniLM files for semantic_search; without it that
// path is reported unavailable. RHYTHM_PARITY_OUT names the report folder (default: parity-report beside the tests).
// RHYTHM_PARITY_MODE picks the load mode (ParityMode): copies, installed or both (the default), each in its own
// database: parity-eval.json and .md for copies, parity-eval-installed.json and .md for installed.
// RHYTHM_PARITY_LEXICAL (a comma list, ParityLexical) adds full-text variants as paths of their own, to choose the text
// search configuration by measurement: legacy (before ranking), or ranked in simple, english, simple_unaccent or
// english_unaccent.
// RHYTHM_PARITY_SCOPES=on measures the program's own leak check (D-127): memory.scopes on in the throwaway database (as
// ScopeCorpusFixture turns it on: the test accepts what this version refuses), the map rebuilt with it on, and each query
// asked as its viewer: a harness token bound to alex, one bound to sam, and the household view's with no person for
// family. Every path then has the viewer-scope capability, and the run fails unless the visibility checks reveal nothing
// and no path returns, or would open, a record its query's viewer may not see. Its reports are parity-eval-scopes and
// parity-eval-installed-scopes; off (the default) is today's run, the one the gate's locks hold.
public sealed class ParityEvalFactAttribute : FactAttribute
{
    public ParityEvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_PARITY_EVAL") != "1")
            Skip = "NOT RUN: opt-in parity evaluation; RHYTHM_PARITY_EVAL=1 sh engine/test-db.sh --filter ParityEvaluation (docs/dev/second-brain-parity/baseline.md).";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
            Skip = "NOT RUN: the parity evaluation needs a disposable Postgres with pgvector; run it through sh engine/test-db.sh.";
    }
}

// One retrieval path as a harness reaches it today: the tool, and the capabilities it has of those a query may need.
public sealed record ParityPath(string Name, string Tool, string How, IReadOnlySet<string> Supports, string? Unavailable);

// Leaks are forbidden records in what the path returned; Exposed are those it would open for this reader anyway
// (get_resource for the catalog paths, the map snapshot for explore_graph), checked when a query needs viewer scope or
// quarantine, so a path that simply finds nothing does not pass a visibility check by accident. Outcome is what the
// results alone say (pass, partial, miss, leak, exposed); Status is "expected-gap" instead when the path lacks a
// capability the query needs, since then even a pass is luck, not evidence.
public sealed record ParityRun(string Status, string Outcome, IReadOnlyList<string> Ranked, double LatencyMs, double? Recall, double? ReciprocalRank,
    double? Ndcg, int? FirstRelevantRank, IReadOnlyList<string> Leaks, IReadOnlyList<string> Exposed, IReadOnlyList<string> Gaps,
    IReadOnlyList<string> WrongAtTime, string? Error);

// With `readers` (the scopes-on run, RHYTHM_PARITY_SCOPES=on), each query is asked as its viewer's principal and every
// path has the viewer-scope capability; without, every query is asked as the one household harness token, as before.
public sealed class ParityEvaluator(ParityLoad load, ParityGolden golden, IReadOnlyDictionary<string, Guid>? readers = null)
{
    public const int Repeats = 3;
    readonly Retrieval retrieval = new(load.Db, new SourceValidation(load.Registry));
    readonly Dictionary<Guid, string> byResource = load.ResourceIds.ToDictionary(p => p.Value, p => p.Key);
    readonly IReadOnlySet<string> capabilities = readers is null ? new HashSet<string>() : new HashSet<string> { "viewer-scope" };
    Guid Reader(ParityQuery q) => readers is null ? load.Harness : readers[q.Viewer];

    public IReadOnlyList<ParityPath> Paths => paths ??=
    [
        new("fulltext", "search_knowledge", "Retrieval.Search, limit 50", capabilities, null),
        new("semantic", "semantic_search", "SemanticSearch.Search over passages (save_document copies, and the texts the index sources cover), limit 20, deduplicated by resource",
            capabilities, load.Embeddings.Model is null ? $"embeddings {load.Embeddings.Status}: set {load.Embeddings.Spec.Variable} to the pinned "
                + (load.Embeddings.Spec == EmbeddingModels.MiniLm ? "MiniLM" : load.Embeddings.Spec.Name) + " files" : null),
        // The adapter leaves replaced facts out as the Second Brain adapter does, but in copies mode their save_document
        // copies still become catalog document stars, and nothing on the map says a fact was replaced: no validity.
        new("graph", "explore_graph", "RelationshipHost.Explore, limit 40: the seed star when the query has one (seed left out of the ranking), else the query text, depth 1",
            capabilities, null),
        // Combined retrieval (SB-P2-08): no tool reaches it yet, so it stands for what the recall tool (SB-P2-09) will serve.
        new("combined", "recall", "Recall.Search, topK 20, hops 0: ranked full text and passages fused by reciprocal rank (k 60, keyword weight the rarity of the words a row holds, a row without passages imputed), no diversity step; full text alone without the model",
            capabilities, null),
        .. ParityLexical.Variants(Environment.GetEnvironmentVariable("RHYTHM_PARITY_LEXICAL")).Select(v =>
            new ParityPath("fulltext:" + v.Name, "search_knowledge", $"Retrieval.Search, limit 50, as {v.Name}: {v.How}", capabilities, null)),
        .. ParityRecall.Variants(Environment.GetEnvironmentVariable("RHYTHM_PARITY_RECALL")).Select(v =>
            new ParityPath("combined:" + v.Name, "recall", $"Recall.Search, topK 20, {v.How}", capabilities, ParityRerankers.Unavailable(v.Reranker))),
    ];
    IReadOnlyList<ParityPath>? paths;
    readonly Dictionary<string, Retrieval> variants = ParityLexical.Variants(Environment.GetEnvironmentVariable("RHYTHM_PARITY_LEXICAL"))
        .ToDictionary(v => "fulltext:" + v.Name, v => new Retrieval(load.Db, new SourceValidation(load.Registry), v.Search));
    readonly Dictionary<string, (Recall Recall, int Hops)> recalls = new[] { ("combined", RecallOptions.Default, 0, (RerankerModel?)null, 0) }
        .Concat(ParityRecall.Variants(Environment.GetEnvironmentVariable("RHYTHM_PARITY_RECALL")).Select(v => ("combined:" + v.Name, v.Options, v.Hops, v.Reranker, v.Depth)))
        .ToDictionary(v => v.Item1, v => (new Recall(load.Db, load.Query, load.Covered, new SourceValidation(load.Registry),
            v.Item2.TimePhrases ? v.Item2 with { Now = load.Corpus.Now, Zone = load.Corpus.Timezone } : v.Item2, load.Embeddings.Status,
            rerank: v.Item4 is null ? null : ParityRerankers.Step(v.Item4, v.Item5)), v.Item3));

    // A combined path's results for this query, at a given topK: the corpus records, and the engine's rows as returned
    // (the prefix check compares those, so two rows of one record still count apart).
    public async Task<IReadOnlyList<string>> Recalled(string path, ParityQuery q, int topK, CancellationToken ct) =>
        [.. (await Hits(path, q, topK, ct)).Select(h => byResource.TryGetValue(h, out var id) ? id : "?" + h).Distinct()];
    public async Task<IReadOnlyList<Guid>> Hits(string path, ParityQuery q, int topK, CancellationToken ct)
    {
        var (recall, hops) = recalls[path];
        var result = await recall.Search(Reader(q), new RecallQuery(q.Query, topK, hops), ct);
        // A reranker variant that fell back to the fused order measured nothing: an error, not a quiet answer.
        if (result.Notices.FirstOrDefault(n => n.StartsWith("Reranking was skipped", StringComparison.Ordinal)) is { } skipped)
            throw new InvalidOperationException(skipped);
        return [.. result.Hits.Select(h => h.Id)];
    }
    public IEnumerable<string> RecallPaths => recalls.Keys;

    async Task<IReadOnlyList<string>> Ranked(string path, ParityQuery q, CancellationToken ct)
    {
        var reader = Reader(q);
        switch (path)
        {
            case "fulltext":
                return FromRows(await retrieval.Search(reader, q.Query, 50, ct));
            case var name when variants.TryGetValue(name, out var variant):
                return FromRows(await variant.Search(reader, q.Query, 50, ct));
            case var name when recalls.ContainsKey(name):
                return await Recalled(name, q, RecallOptions.Default.MaxTopK, ct);
            case "semantic":
                return FromRows(await new SemanticSearch(load.Db, load.Query!, load.Covered, new SourceValidation(load.Registry))
                    .Search(reader, q.Query, "", 20, ct));
            default:
                var seed = q.Seed is null ? null : load.GraphId(q.Seed);
                var page = await load.Host.Explore(reader, seed is null ? q.Query : null, seed, q.Depth ?? 1, 40, ct);
                return page.stars.Where(s => s.graphId != seed)
                    .SelectMany(s => load.ByGraphId.TryGetValue(s.graphId, out var ids) ? ids : ["?" + s.graphId]).Distinct().ToArray();
        }
    }
    async Task<bool> Opens(string path, string id, Guid reader, CancellationToken ct) => path == "graph"
        ? load.ByGraphId.Where(p => p.Value.Contains(id)).Select(p => p.Key).ToArray() is { Length: > 0 } stars && (await load.Host.Known(reader, stars, ct)).Count > 0
        : load.ResourceIds.TryGetValue(id, out var resource) && await retrieval.Resource(reader, resource, ct) != "[]";
    // The corpus records a path returned, or would open for this query's reader, that its viewer may not see by the
    // corpus's own rule (ParityCorpus.CanSee): the scopes-on run's check over every query, the forbidden ones and all.
    public async Task<IReadOnlyList<string>> Unseeable(ParityPath path, ParityQuery q, ParityRun run, CancellationToken ct)
    {
        var scopes = load.Corpus.ScopeOf;
        bool Hidden(string id) => scopes.TryGetValue(id, out var scope) && !load.Corpus.CanSee(q.Viewer, scope);
        var found = run.Ranked.Where(Hidden).ToList();
        foreach (var id in q.Forbids.Where(Hidden).Except(found))
            if (await Opens(path.Name, id, Reader(q), ct)) found.Add(id);
        return found;
    }
    // Both tools answer the resource id (semantic_search has no external id); chunks of one resource count once.
    IReadOnlyList<string> FromRows(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray()
        .Select(r => r.GetProperty("id").GetGuid())
        .Select(g => byResource.TryGetValue(g, out var id) ? id : "?" + g).Distinct().ToArray();

    public async Task<ParityRun> Run(ParityPath path, ParityQuery q, CancellationToken ct)
    {
        if (path.Unavailable is not null) return new("unavailable", "unavailable", [], 0, null, null, null, null, [], [], [], [], path.Unavailable);
        IReadOnlyList<string> ranked = [];
        var times = new List<double>();
        try
        {
            for (var i = 0; i < Repeats; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var result = await Ranked(path.Name, q, ct);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (i == 0) ranked = result;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or PostgresException or UnauthorizedAccessException)
        { return new("error", "error", [], 0, null, null, null, null, [], [], [], [], e.GetType().Name + ": " + e.Message); }
        var k = golden.K;
        var recall = ParityMetrics.Recall(ranked, q.Relevant, k);
        var leaks = ParityMetrics.Leaks(ranked, q.Forbids);
        var first = ranked.ToList().FindIndex(id => q.Relevant.Any(r => r.Id == id));
        var wrong = (q.WrongAtTime ?? []).Where(ranked.Contains).ToArray();
        var gaps = q.Requires.Where(c => !path.Supports.Contains(c)).ToArray();
        var exposed = new List<string>();
        if (q.Requires.Any(c => c is "viewer-scope" or "quarantine"))
            foreach (var id in q.Forbids.Except(leaks)) if (await Opens(path.Name, id, Reader(q), ct)) exposed.Add(id);
        var outcome = leaks.Count > 0 ? "leak" : exposed.Count > 0 ? "exposed" : recall is null or 1.0 ? "pass" : recall > 0 ? "partial" : "miss";
        return new(gaps.Length > 0 ? "expected-gap" : outcome, outcome, ranked, times.Order().ElementAt(times.Count / 2), recall, ParityMetrics.ReciprocalRank(ranked, q.Relevant),
            ParityMetrics.Ndcg(ranked, q.Relevant, k), first < 0 ? null : first + 1, leaks, exposed, gaps, wrong, null);
    }

    // A first call per path pays for JIT, connections and the model's first run; it is not measured.
    public async Task WarmUp(CancellationToken ct)
    {
        var probe = new ParityQuery("warm-up", "warm-up", "alex", "warm up", []);
        foreach (var path in Paths.Where(p => p.Unavailable is null)) await Ranked(path.Name, probe, ct);
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class ParityEvaluation(ITestOutputHelper output)
{
    [ParityEvalFact] public async Task Golden_queries_through_todays_retrieval_paths()
    {
        var modes = Environment.GetEnvironmentVariable("RHYTHM_PARITY_MODE") switch
        {
            "copies" => new[] { ParityMode.Copies },
            "installed" => [ParityMode.Installed],
            null or "" or "both" => [ParityMode.Copies, ParityMode.Installed],
            var other => throw new ArgumentException($"RHYTHM_PARITY_MODE is copies, installed or both, not {other}"),
        };
        var scopes = Environment.GetEnvironmentVariable("RHYTHM_PARITY_SCOPES") switch
        {
            null or "" or "off" => false,
            "on" => true,
            var other => throw new ArgumentException($"RHYTHM_PARITY_SCOPES is on or off, not {other}"),
        };
        foreach (var mode in modes) await Evaluate(mode, scopes);
    }

    // The scopes-on run's readers, made as ScopeCorpusFixture makes them: a client bound to alex, one to sam and the
    // household view's with no person; Alex and Sam in Family, the former people gone from people at the engine's start
    // with today's settings, scopes recorded on, and the map rebuilt with them on (budgets and similarity per scope).
    static async Task<IReadOnlyDictionary<string, Guid>> ScopesOn(NpgsqlDataSource source, ParityLoad load)
    {
        var readers = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["alex"] = await Scoped.Principal(source, "parity-alex", "harness", "alex"),
            ["sam"] = await Scoped.Principal(source, "parity-sam", "harness", "sam"),
            ["family"] = await Scoped.Principal(source, "parity-household", "harness"),
        };
        Assert.Equal(load.Corpus.Viewers.Select(v => v.Id).Order(StringComparer.Ordinal), readers.Keys.Order(StringComparer.Ordinal));
        await Scoped.Members(source, "alex", "sam");
        await new Principals(source).Install(TestSettings.Use(), default);
        await Scoped.Mode(source, "on");
        Assert.Equal("complete", await load.Host.Refresh(false, default));
        return readers;
    }

    async Task Evaluate(ParityMode mode, bool scopes)
    {
        var server = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!;
        var name = "rhythm_parity_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        try
        {
            var corpus = ParityCorpus.Load();
            var golden = ParityGolden.Load();
            var started = Stopwatch.GetTimestamp();
            // The live model, MiniLM, unless RHYTHM_PARITY_MODEL names another (ParityModelChoice, SB-P2-06).
            var choice = ParityModelChoice.FromEnvironment();
            await using var load = await ParityLoader.Load(source, corpus, choice.Directory, mode, model: choice.Spec, instruction: choice.Instruction,
                mapModel: choice.Map, mapDirectory: choice.MapDirectory, mapMaxLinks: choice.MaxLinks);
            var loadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal("complete", load.RefreshResult);
            await ParityLexical.Prepare(source, Environment.GetEnvironmentVariable("RHYTHM_PARITY_LEXICAL"));

            var readers = scopes ? await ScopesOn(source, load) : null;
            var evaluator = new ParityEvaluator(load, golden, readers);
            await evaluator.WarmUp(default);
            var runs = new List<(ParityQuery Query, ParityPath Path, ParityRun Run)>();
            foreach (var q in golden.Queries)
                foreach (var path in evaluator.Paths)
                    runs.Add((q, path, await evaluator.Run(path, q, default)));

            var folder = Environment.GetEnvironmentVariable("RHYTHM_PARITY_OUT") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "parity-report");
            Directory.CreateDirectory(folder);
            var report = ParityReport.Json(corpus, golden, load, evaluator.Paths, runs, loadMs, scopes);
            var stem = (mode == ParityMode.Copies ? "parity-eval" : "parity-eval-installed") + (scopes ? "-scopes" : "");
            await File.WriteAllTextAsync(Path.Combine(folder, stem + ".json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var markdown = ParityReport.Markdown(report);
            await File.WriteAllTextAsync(Path.Combine(folder, stem + ".md"), markdown);
            output.WriteLine($"Parity report ({ParityReport.ModeName(mode)}{(scopes ? ", scopes on" : "")}): {folder}");
            output.WriteLine(markdown);

            // Every query ran on every available path; an error is the harness's or the query's fault, not a baseline.
            Assert.Equal(golden.Queries.Length * evaluator.Paths.Count, runs.Count);
            Assert.DoesNotContain(runs, r => r.Run.Status == "error");

            // Combined retrieval on every golden query (RET-15, RET-13): its top 5 and top 10 are the start of its top 20,
            // and no row is a result twice, so a long text is never more than one of them.
            foreach (var path in evaluator.RecallPaths)
                foreach (var q in golden.Queries)
                {
                    var twenty = await evaluator.Hits(path, q, 20, default);
                    Assert.Equal(twenty.Count, twenty.Distinct().Count());
                    foreach (var k in new[] { 5, 10 })
                        Assert.True(twenty.Take(k).SequenceEqual(await evaluator.Hits(path, q, k, default)), $"{path}: {q.Id}'s top {k} is not the start of its top 20");
                }
            output.WriteLine($"Combined retrieval: top 5 and top 10 the start of top 20, each row once, on {golden.Queries.Length} queries and {evaluator.RecallPaths.Count()} path(s)");
            if (!scopes) return;

            // Scopes on: every visibility check that needs only a viewer's scope (the program's must-hide measure) reveals
            // nothing on any path, neither returned nor open to its reader; and over every query, whatever its type, no
            // path returns or would open a record its viewer may not see. A quarantine is still a capability no path has,
            // so a check that also needs one stays an expected gap, and its held records are not counted here.
            var visibility = runs.Where(r => r.Query.Type == "visibility" && r.Query.Forbids.Length > 0
                && r.Query.Requires.SequenceEqual(["viewer-scope"]) && r.Run.Status != "unavailable").ToArray();
            Assert.NotEmpty(visibility);
            output.WriteLine($"Scopes on: {visibility.Select(r => r.Query.Id).Distinct().Count()} visibility checks on "
                + $"{visibility.Select(r => r.Path.Name).Distinct().Count()} paths, {visibility.Length} runs");
            Assert.All(visibility, r => Assert.True(r.Run.Status is "pass" or "partial" or "miss" && r.Run.Leaks.Count == 0 && r.Run.Exposed.Count == 0,
                $"{r.Query.Id} on {r.Path.Name}: {r.Run.Status}, leaked [{string.Join(", ", r.Run.Leaks)}], exposed [{string.Join(", ", r.Run.Exposed)}]"));
            var unseen = new List<string>();
            foreach (var (q, path, run) in runs.Where(r => r.Run.Status != "unavailable"))
                if (await evaluator.Unseeable(path, q, run, default) is { Count: > 0 } found)
                    unseen.Add($"{q.Id} ({q.Viewer}) on {path.Name}: {string.Join(", ", found)}");
            Assert.True(unseen.Count == 0, "returned or open to a reader who may not see it: " + string.Join("; ", unseen));
        }
        finally
        {
            await source.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = NpgsqlDataSource.Create(server);
            await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
            TestSettings.Use();
        }
    }
}

// Full-text variants to measure beside the engine's own search (RHYTHM_PARITY_LEXICAL). The unaccent ones are text
// search configurations the evaluation creates in its own throwaway database when the migrations have not.
public static class ParityLexical
{
    static readonly Dictionary<string, (LexicalSearch Search, string How, string? Create)> Known = new(StringComparer.Ordinal)
    {
        ["legacy"] = (LexicalSearch.Legacy, "every word required, simple configuration, newest first", null),
        ["simple"] = (new("simple", true), "ranked, simple configuration (no stemming, no accent folding)", null),
        ["english"] = (new("english", true), "ranked, english configuration (stemming, its stop words)", null),
        ["simple_unaccent"] = (new("public.rhythm_eval_simple_unaccent", true), "ranked, simple with accents folded", """
            CREATE TEXT SEARCH CONFIGURATION public.rhythm_eval_simple_unaccent (COPY = pg_catalog.simple);
            ALTER TEXT SEARCH CONFIGURATION public.rhythm_eval_simple_unaccent
              ALTER MAPPING FOR word, hword, hword_part WITH public.unaccent, pg_catalog.simple;
            """),
        ["english_unaccent"] = (new("public.rhythm_eval_english_unaccent", true), "ranked, english with accents folded", """
            CREATE TEXT SEARCH CONFIGURATION public.rhythm_eval_english_unaccent (COPY = pg_catalog.english);
            ALTER TEXT SEARCH CONFIGURATION public.rhythm_eval_english_unaccent
              ALTER MAPPING FOR word, hword, hword_part WITH public.unaccent, pg_catalog.english_stem;
            """),
    };
    public static IReadOnlyList<(string Name, LexicalSearch Search, string How)> Variants(string? list) =>
        [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Select(name =>
            Known.TryGetValue(name, out var v) ? (name, v.Search, v.How)
                : throw new ArgumentException($"RHYTHM_PARITY_LEXICAL lists {string.Join(", ", Known.Keys)}, not {name}"))];
    public static async Task Prepare(NpgsqlDataSource source, string? list)
    {
        foreach (var (name, _, _) in Variants(list))
        {
            if (Known[name].Create is not { } create) continue;
            await using var cmd = source.CreateCommand("CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;\n" + create);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

// Combined retrieval's variants to measure beside its defaults (RHYTHM_PARITY_RECALL, a comma list): each a path of its
// own, so a default is chosen, or kept, by measurement (combined-retrieval.md).
public static class ParityRecall
{
    static readonly Dictionary<string, (RecallOptions Options, int Hops, string How)> Known = new(StringComparer.Ordinal)
    {
        // Second Brain's numbers as they stand: rarity weights, diversity 0.7, no imputation.
        ["upstream"] = (RecallOptions.Default with { Lambda = 0.7, Impute = false }, 0, "Second Brain's numbers: diversity 0.7, no imputation"),
        ["diversity"] = (RecallOptions.Default with { Lambda = 0.7 }, 0, "diversity 0.7"),
        ["lambda05"] = (RecallOptions.Default with { Lambda = 0.5 }, 0, "diversity 0.5"),
        ["noimpute"] = (RecallOptions.Default with { Impute = false }, 0, "no imputation for rows without passages"),
        ["flat"] = (RecallOptions.Default with { KeywordWeight = RecallWeight.Flat }, 0, "every keyword result weighted 1, as the meaning arm"),
        ["coverage"] = (RecallOptions.Default with { KeywordWeight = RecallWeight.Coverage }, 0, "keyword weight the share of the query's rarity a row holds"),
        ["coverage-exact"] = (RecallOptions.Default with { KeywordWeight = RecallWeight.CoverageExact }, 0, "keyword weight the share of the query's rarity a row holds, 1 more for an identifier hit"),
        ["k20"] = (RecallOptions.Default with { K = 20 }, 0, "fusion constant 20"),
        ["pool15"] = (RecallOptions.Default with { DensePool = 15 }, 0, "15 passages for the meaning arm"),
        ["hops1"] = (RecallOptions.Default, 1, "one graph hop from the first five"),
        ["hops2"] = (RecallOptions.Default, 2, "two graph hops from the first five"),
        // SB-P2-19: the keyword arm before it (the catalog's own rows alone), and with a passage counting for any word.
        ["catalog-words"] = (RecallOptions.Default with { KeywordTexts = false }, 0, "the keyword arm over the catalog's own rows alone: no memory text, no passages"),
        // SB-P2-16: time phrases as a window on the source's own time, read at the corpus's own clock and zone.
        ["time"] = (RecallOptions.Default with { TimePhrases = true }, 0, "time phrases in the question become a window on the source's own time, at the corpus's clock and zone"),
        ["passages-any"] = (RecallOptions.Default with { AnyPassageWord = true }, 0, "the keyword arm counting a passage that holds any of the words, not only the whole question or an identifier"),
    };
    // SB-P2-12, D-249: a cross-encoder reordering the first `depth` results (RecallRerank), its files from the folder its
    // variable names (RHYTHM_RERANKER_DIR_BGE, RHYTHM_RERANKER_DIR_MSMARCO); without them the path is unavailable.
    static readonly Dictionary<string, (RerankerModel Model, int Depth)> Reranked = new(StringComparer.Ordinal)
    {
        ["rerank-bge-20"] = (RerankerModels.BgeRerankerBase, 20),
        ["rerank-bge-10"] = (RerankerModels.BgeRerankerBase, 10),
        ["rerank-msmarco-20"] = (RerankerModels.MsMarcoMiniLm, 20),
        ["rerank-msmarco-10"] = (RerankerModels.MsMarcoMiniLm, 10),
    };
    public static IReadOnlyList<(string Name, RecallOptions Options, int Hops, string How, RerankerModel? Reranker, int Depth)> Variants(string? list) =>
        [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Select(name =>
            Known.TryGetValue(name, out var v) ? (name, v.Options, v.Hops, v.How, (RerankerModel?)null, 0)
            : Reranked.TryGetValue(name, out var r) ? (name, RecallOptions.Default, 0,
                $"the defaults, then {r.Model.Name} reordering the first {r.Depth} by its scores ({LocalReranker.Threads} threads)", r.Model, r.Depth)
            : throw new ArgumentException($"RHYTHM_PARITY_RECALL lists {string.Join(", ", Known.Keys.Concat(Reranked.Keys))}, not {name}"))];
    public static bool Reranks(string path) => Reranked.Keys.Any(k => path == "combined:" + k);
}

// The rerankers a parity run measures, each loaded once from its pinned files (D-041: never downloaded), with no time
// budget and a breaker that never opens, so a run measures the model rather than the safety net; a failure is an error.
public static class ParityRerankers
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<LocalReranker?>> Loaded = new(StringComparer.Ordinal);

    public static string? Unavailable(RerankerModel? model) => model is null ? null
        : RerankerModels.Given(model) is { } folder && model.Files.All(f => File.Exists(Path.Combine(folder, f.Name))) ? null
        : $"reranker {model.Name}: set {model.Variable} to the folder with its {string.Join(" and ", model.Files.Select(f => f.Name))} (D-041)";

    public static LocalReranker? Model(RerankerModel model) => Loaded.GetOrAdd(model.Name, _ => new(() =>
        Unavailable(model) is null ? new LocalReranker(RerankerModels.Given(model)!, model) : null)).Value;

    public static RecallRerank Step(RerankerModel model, int depth) =>
        new(() => Model(model), () => Unavailable(model) ?? "on", RerankBreaker.Never, depth, Timeout.InfiniteTimeSpan);
}

// The report: JSON for machines (parity-eval.json) and Markdown for people (parity-eval.md), from the same numbers.
public static class ParityReport
{
    static double Round(double x) => Math.Round(x, 3);
    static JsonNode? Num(double? x) => x is { } v ? JsonValue.Create(Round(v)) : null;
    public static string ModeName(ParityMode mode) => mode == ParityMode.Copies ? "copies" : "installed";
    static string ModeText(ParityMode mode) => mode == ParityMode.Copies
        ? "every memory, note and Drive document also saved as a save_document copy (full text, chunks); an install holds no such copy unless an AI client saved one"
        : "only what the adapters and the engine's index job write: snapshot rows with empty content, under each adapter's own policy, with passages for the texts the index sources cover (adapters.embeddings.sources); no save_document copies";

    // Relevant records found in the top k, by the kind of record they are, over the queries the path has every
    // capability for: which content types a path can find at all.
    static JsonObject ByKind(IEnumerable<(ParityQuery Query, ParityRun Run)> runs, IReadOnlyDictionary<string, string> kindOf, int k)
    {
        var counts = new SortedDictionary<string, (int Relevant, int Found)>(StringComparer.Ordinal);
        foreach (var (q, r) in runs.Where(x => x.Run.Status is not ("unavailable" or "error") && x.Run.Gaps.Count == 0))
            foreach (var g in q.Relevant)
            {
                var kind = kindOf.GetValueOrDefault(g.Id, "?");
                var (relevant, found) = counts.GetValueOrDefault(kind);
                counts[kind] = (relevant + 1, found + (r.Ranked.Take(k).Contains(g.Id) ? 1 : 0));
            }
        var o = new JsonObject();
        foreach (var (kind, (relevant, found)) in counts)
            o[kind] = new JsonObject { ["relevant"] = relevant, ["found"] = found, ["recallAtK"] = Num((double)found / relevant) };
        return o;
    }

    static JsonObject Aggregate(IReadOnlyList<ParityRun> runs)
    {
        var measured = runs.Where(r => r.Status is not ("unavailable" or "error")).ToArray();
        double? Mean(Func<ParityRun, double?> f) => measured.Select(f).Where(x => x is not null).Select(x => x!.Value).ToArray() is { Length: > 0 } xs ? xs.Average() : null;
        var clean = measured.Where(r => r.Gaps.Count == 0).ToArray();
        return new JsonObject
        {
            ["queries"] = runs.Count, ["measured"] = measured.Length,
            ["recallAtK"] = Num(Mean(r => r.Recall)), ["mrr"] = Num(Mean(r => r.ReciprocalRank)), ["ndcgAtK"] = Num(Mean(r => r.Ndcg)),
            ["recallAtKWithoutGaps"] = Num(clean.Select(r => r.Recall).Where(x => x is not null).Select(x => x!.Value).ToArray() is { Length: > 0 } c ? c.Average() : null),
            ["pass"] = measured.Count(r => r.Status == "pass"), ["partial"] = measured.Count(r => r.Status == "partial"),
            ["miss"] = measured.Count(r => r.Status == "miss"), ["leak"] = measured.Count(r => r.Status == "leak"),
            ["exposed"] = measured.Count(r => r.Status == "exposed"),
            ["leakedOrExposed"] = measured.Count(r => r.Leaks.Count > 0 || r.Exposed.Count > 0),
            ["expectedGap"] = measured.Count(r => r.Status == "expected-gap"),
            ["unavailable"] = runs.Count(r => r.Status == "unavailable"), ["error"] = runs.Count(r => r.Status == "error"),
            ["latencyMsP50"] = Num(measured.Length > 0 ? ParityMetrics.Percentile(measured.Select(r => r.LatencyMs).ToArray(), 50) : null),
            ["latencyMsP95"] = Num(measured.Length > 0 ? ParityMetrics.Percentile(measured.Select(r => r.LatencyMs).ToArray(), 95) : null),
            ["latencyMsMax"] = Num(measured.Length > 0 ? measured.Max(r => r.LatencyMs) : null),
        };
    }

    public static JsonObject Json(ParityCorpus corpus, ParityGolden golden, ParityLoad load, IReadOnlyList<ParityPath> paths,
        IReadOnlyList<(ParityQuery Query, ParityPath Path, ParityRun Run)> runs, double loadMs, bool scopes = false)
    {
        var byPath = new JsonObject();
        var kindOf = load.Items.ToDictionary(i => i.Id, i => i.Kind, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var mine = runs.Where(r => r.Path.Name == path.Name).ToArray();
            var types = new JsonObject();
            foreach (var type in golden.Types.Keys.Where(t => mine.Any(r => r.Query.Type == t)))
                types[type] = Aggregate(mine.Where(r => r.Query.Type == type).Select(r => r.Run).ToArray());
            byPath[path.Name] = new JsonObject
            {
                ["overall"] = Aggregate(mine.Select(r => r.Run).ToArray()), ["byType"] = types,
                ["byKind"] = ByKind(mine.Select(r => (r.Query, r.Run)), kindOf, golden.K)
            };
        }
        return new JsonObject
        {
            ["schema"] = "rhythm-parity-eval.v1",
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["label"] = "DEMO: synthetic corpus only",
            ["mode"] = ModeName(load.Mode),
            ["modeText"] = ModeText(load.Mode),
            ["scopes"] = scopes ? "on" : "off",
            ["k"] = golden.K,
            ["corpus"] = new JsonObject
            {
                ["memories"] = corpus.Memories.Length, ["notes"] = corpus.Notes.Length, ["documents"] = corpus.Documents.Length,
                ["measurementSeries"] = corpus.Measurements.Length, ["dashboards"] = corpus.Dashboards.Length,
                ["projects"] = corpus.Projects.Length, ["queries"] = golden.Queries.Length
            },
            ["embeddings"] = new JsonObject { ["status"] = load.Embeddings.Status, ["model"] = load.Model },
            ["load"] = new JsonObject
            {
                ["milliseconds"] = Round(loadMs), ["refresh"] = load.RefreshResult, ["refreshNotes"] = new JsonArray([.. load.RefreshNotes.Select(n => (JsonNode)n)]),
                ["sources"] = new JsonArray([.. load.Sources.Select(s => (JsonNode)s)]),
                ["indexed"] = new JsonObject { ["rows"] = load.Indexed.Rows, ["passages"] = load.Indexed.Passages },
                ["items"] = new JsonArray([.. load.Items.Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i.Id, ["kind"] = i.Kind, ["catalog"] = i.Catalog, ["map"] = i.Map, ["note"] = i.Note,
                    ["lost"] = new JsonArray([.. i.Lost.Select(l => (JsonNode)l)])
                })])
            },
            ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["tool"] = p.Tool, ["how"] = p.How, ["supports"] = new JsonArray([.. p.Supports.Order().Select(s => (JsonNode)s)]),
                ["unavailable"] = p.Unavailable
            })]),
            ["summary"] = byPath,
            ["queries"] = new JsonArray([.. golden.Queries.Select(q => (JsonNode)new JsonObject
            {
                ["id"] = q.Id, ["type"] = q.Type, ["viewer"] = q.Viewer, ["query"] = q.Query, ["seed"] = q.Seed,
                ["asOf"] = q.AsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["needs"] = new JsonArray([.. q.Requires.Select(n => (JsonNode)n)]),
                ["relevant"] = new JsonArray([.. q.Relevant.Select(r => (JsonNode)r.Id)]),
                ["forbidden"] = new JsonArray([.. q.Forbids.Select(f => (JsonNode)f)]),
                ["results"] = new JsonObject([.. runs.Where(r => r.Query.Id == q.Id).Select(r => new KeyValuePair<string, JsonNode?>(r.Path.Name, new JsonObject
                {
                    ["status"] = r.Run.Status, ["outcome"] = r.Run.Outcome, ["recallAtK"] = Num(r.Run.Recall), ["reciprocalRank"] = Num(r.Run.ReciprocalRank),
                    ["ndcgAtK"] = Num(r.Run.Ndcg), ["firstRelevantRank"] = r.Run.FirstRelevantRank, ["latencyMs"] = Num(r.Run.LatencyMs),
                    ["returned"] = r.Run.Ranked.Count, ["top"] = new JsonArray([.. r.Run.Ranked.Take(golden.K).Select(x => (JsonNode)x)]),
                    ["leaks"] = new JsonArray([.. r.Run.Leaks.Select(x => (JsonNode)x)]), ["exposed"] = new JsonArray([.. r.Run.Exposed.Select(x => (JsonNode)x)]), ["gaps"] = new JsonArray([.. r.Run.Gaps.Select(x => (JsonNode)x)]),
                    ["wrongAtTime"] = new JsonArray([.. r.Run.WrongAtTime.Select(x => (JsonNode)x)]), ["error"] = r.Run.Error
                }))])
            })])
        };
    }

    static string F(JsonNode? n) => n?.ToString() ?? "–";

    public static string Markdown(JsonObject report)
    {
        var k = (int)report["k"]!;
        var md = new StringBuilder();
        md.AppendLine("# Parity retrieval evaluation").AppendLine();
        md.AppendLine($"Generated {report["generatedAt"]} from the synthetic corpus (DEMO data only). k = {k}.").AppendLine();
        md.AppendLine($"Load mode: **{report["mode"]}**, {report["modeText"]}.").AppendLine();
        if ((string?)report["scopes"] == "on")
            md.AppendLine("Memory scopes: **on**, each query asked as its viewer (a client bound to alex or sam, or the household view's with no person "
                + "for family); every path has the viewer-scope capability.").AppendLine();
        var c = report["corpus"]!;
        md.AppendLine($"Corpus: {c["memories"]} memories, {c["notes"]} notes, {c["documents"]} documents, {c["measurementSeries"]} measurement series, "
            + $"{c["dashboards"]} dashboards, {c["projects"]} projects; {c["queries"]} golden queries.");
        md.AppendLine($"Embeddings: {report["embeddings"]!["status"]}" + (report["embeddings"]!["model"] is { } m ? $" ({m})." : ".")
            + $" Load and map rebuild: {F(report["load"]!["milliseconds"])} ms, refresh {report["load"]!["refresh"]}.");
        md.AppendLine($"Index sources (adapters.embeddings.sources): {(report["load"]!["sources"]!.AsArray().Count == 0 ? "none" : string.Join(", ", report["load"]!["sources"]!.AsArray()))};"
            + $" the index job gave {report["load"]!["indexed"]!["rows"]} rows {report["load"]!["indexed"]!["passages"]} passages beyond the copies'.").AppendLine();

        md.AppendLine("## Paths").AppendLine();
        md.AppendLine("| Path | Tool | How | Capabilities it has | Available |").AppendLine("|---|---|---|---|---|");
        foreach (var p in report["paths"]!.AsArray())
            md.AppendLine($"| {p!["name"]} | `{p["tool"]}` | {p["how"]} | {(p["supports"]!.AsArray().Count == 0 ? "none" : string.Join(", ", p["supports"]!.AsArray()))} | "
                + (p["unavailable"] is { } why ? $"no: {why}" : "yes") + " |");
        md.AppendLine();

        md.AppendLine("## Summary").AppendLine();
        md.AppendLine("Means over measured queries with a relevant answer; \"without gaps\" leaves out queries that need a capability the path lacks. "
            + "Pass, partial, miss, leak and exposed count the queries a path can answer; expected gap counts the rest. "
            + "Leaked or exposed counts every query, gaps included, where a forbidden record was returned or would open for this reader.").AppendLine();
        md.AppendLine($"| Path | Measured | Recall@{k} | Recall@{k} without gaps | MRR | nDCG@{k} | Pass | Partial | Miss | Leak | Exposed | Expected gap | Leaked or exposed | p50 ms | p95 ms |")
          .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, node) in report["summary"]!.AsObject())
        {
            var o = node!["overall"]!;
            md.AppendLine($"| {name} | {o["measured"]}/{o["queries"]} | {F(o["recallAtK"])} | {F(o["recallAtKWithoutGaps"])} | {F(o["mrr"])} | {F(o["ndcgAtK"])} | {o["pass"]} | {o["partial"]} | {o["miss"]} | {o["leak"]} | {o["exposed"]} | {o["expectedGap"]} | {o["leakedOrExposed"]} | {F(o["latencyMsP50"])} | {F(o["latencyMsP95"])} |");
        }
        md.AppendLine();

        md.AppendLine("## By query type").AppendLine();
        md.AppendLine($"Recall@{k} / MRR, and pass-partial-miss-leak-exposed-gap counts, per path.").AppendLine();
        var paths = report["summary"]!.AsObject().Select(p => p.Key).ToArray();
        md.AppendLine("| Type | " + string.Join(" | ", paths) + " |").AppendLine("|---|" + string.Concat(paths.Select(_ => "---|")));
        var types = report["summary"]!.AsObject().SelectMany(p => p.Value!["byType"]!.AsObject().Select(t => t.Key)).Distinct().ToArray();
        foreach (var type in types)
            md.AppendLine($"| {type} | " + string.Join(" | ", paths.Select(p => report["summary"]![p]!["byType"]![type] is { } t
                ? (t["measured"]!.GetValue<int>() == 0 ? "unavailable" : $"{F(t["recallAtK"])} / {F(t["mrr"])} ({t["pass"]}-{t["partial"]}-{t["miss"]}-{t["leak"]}-{t["exposed"]}-{t["expectedGap"]})")
                : "–")) + " |");
        md.AppendLine();

        md.AppendLine("## By the relevant record's kind").AppendLine();
        md.AppendLine($"Relevant records found in the top {k} / relevant records, over the queries the path has every capability for.").AppendLine();
        var recordKinds = report["summary"]!.AsObject().SelectMany(p => p.Value!["byKind"]!.AsObject().Select(t => t.Key)).Distinct().Order(StringComparer.Ordinal).ToArray();
        md.AppendLine("| Kind | " + string.Join(" | ", paths) + " |").AppendLine("|---|" + string.Concat(paths.Select(_ => "---|")));
        foreach (var kind in recordKinds)
            md.AppendLine($"| {kind} | " + string.Join(" | ", paths.Select(p => report["summary"]![p]!["byKind"]![kind] is { } t
                ? $"{t["found"]}/{t["relevant"]} ({F(t["recallAtK"])})" : report["summary"]![p]!["overall"]!["measured"]!.GetValue<int>() == 0 ? "unavailable" : "–")) + " |");
        md.AppendLine();

        md.AppendLine("## Every query").AppendLine();
        md.AppendLine("Each cell: status, then the rank of the first relevant record (– when none was returned). A gap shows in brackets what the results alone would have said.").AppendLine();
        md.AppendLine("| Query | Type | Viewer | Needs | " + string.Join(" | ", paths) + " |").AppendLine("|---|---|---|---|" + string.Concat(paths.Select(_ => "---|")));
        foreach (var q in report["queries"]!.AsArray())
        {
            var needs = q!["needs"]!.AsArray();
            md.AppendLine($"| `{q["id"]}` | {q["type"]} | {q["viewer"]} | {(needs.Count == 0 ? "" : string.Join(", ", needs))} | "
                + string.Join(" | ", paths.Select(p => q["results"]![p] is { } r ? (r["status"]!.ToString() == "expected-gap" ? $"gap ({r["outcome"]})" : r["status"]!.ToString()) + $" {F(r["firstRelevantRank"])}" : "–")) + " |");
        }
        md.AppendLine();

        md.AppendLine("## Misses, leaks and gaps").AppendLine();
        foreach (var q in report["queries"]!.AsArray())
            foreach (var p in paths)
            {
                var r = q!["results"]![p]!;
                var status = r["status"]!.ToString();
                if (r["outcome"]!.ToString() is "pass" or "unavailable" && r["wrongAtTime"]!.AsArray().Count == 0) continue;
                var detail = new List<string>();
                if (r["leaks"]!.AsArray().Count > 0) detail.Add("leaked " + string.Join(", ", r["leaks"]!.AsArray()));
                if (r["exposed"]!.AsArray().Count > 0) detail.Add("not returned but open to this reader: " + string.Join(", ", r["exposed"]!.AsArray()));
                if (r["gaps"]!.AsArray().Count > 0) detail.Add("needs " + string.Join(", ", r["gaps"]!.AsArray()));
                if (r["wrongAtTime"]!.AsArray().Count > 0) detail.Add("also returned the later fact " + string.Join(", ", r["wrongAtTime"]!.AsArray()));
                var top = r["top"]!.AsArray();
                detail.Add(top.Count == 0 ? "returned nothing" : "top: " + string.Join(", ", top));
                md.AppendLine($"- `{q["id"]}` on {p}: {(status == "expected-gap" ? $"expected gap ({r["outcome"]})" : status)}; {string.Join("; ", detail)}" + (r["error"] is { } e ? $"; {e}" : ""));
            }
        md.AppendLine();

        md.AppendLine("## How the corpus was loaded").AppendLine();
        md.AppendLine("| Record | Kind | Catalog | Map | Note | Not represented |").AppendLine("|---|---|---|---|---|---|");
        foreach (var i in report["load"]!["items"]!.AsArray())
            md.AppendLine($"| `{i!["id"]}` | {i["kind"]} | {F(i["catalog"])} | {F(i["map"])} | {F(i["note"])} | {string.Join(", ", i["lost"]!.AsArray())} |");
        if (report["load"]!["refreshNotes"]!.AsArray().Count > 0)
            md.AppendLine().AppendLine("Map rebuild notes: " + string.Join("; ", report["load"]!["refreshNotes"]!.AsArray()));
        return md.ToString();
    }
}
