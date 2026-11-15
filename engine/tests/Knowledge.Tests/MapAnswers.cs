using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// Every answer the map gives one reader, read through the host the tools call and serialized as they serialize it:
// get_relationship_graph and app_graph's operations (overview with each page, search, get, neighbors, content, data,
// and the ids a client already knows), explore_graph, graph_changes, the star check save_insight and save_dashboard
// make, and dashboard_data's star feeds (contents, links, data). The values a run makes (the pass's own times, the ids
// of rows written at load) are replaced by placeholders, so two loads of one corpus, or one load read twice, compare as
// text (docs/dev/memory.md section 8; SB-P1-05).
public static partial class MapAnswers
{
    // `stars` are the graph ids asked about (a reader's own, others' and unknown ones alike), `queries` the search and
    // explore texts; `run` is a time before the load, so every time at or after it is the run's own.
    public static Task<SortedDictionary<string, string>> Of(ParityLoad load, Guid reader, IReadOnlyCollection<string> stars,
        IReadOnlyCollection<string> queries, DateTimeOffset since, DateTimeOffset run, CancellationToken ct = default) =>
        Of(load.Host, load.Db, load.Registry, reader, stars, queries, since, run, ct);
    public static async Task<SortedDictionary<string, string>> Of(RelationshipHost host, Database db, Knowledge.Adapters.AdapterRegistry registry,
        Guid reader, IReadOnlyCollection<string> stars, IReadOnlyCollection<string> queries, DateTimeOffset since, DateTimeOffset run,
        CancellationToken ct = default)
    {
        var feeds = new DashboardFeeds(db, host, registry);
        var reads = new List<(string Name, Func<Task<string>> Read)>();
        void Add(string name, Func<Task<string>> read) => reads.Add((name, read));
        static string Json<T>(T value) => JsonSerializer.Serialize(value);
        // Each page in turn, as a client follows nextCursor.
        async Task<string> Pages(GraphQuery query, IReadOnlyList<string>? known = null)
        {
            var pages = new List<GraphPage>();
            for (var cursor = query.Cursor; pages.Count < 50;)
            {
                var page = await host.Read(reader, known, null, ct, query with { Cursor = cursor });
                pages.Add(page);
                if (page.nextCursor is not { } next) break;
                cursor = next;
            }
            return Json(pages);
        }
        Add("graph overview", () => Pages(new()));
        Add("graph overview, known", () => Pages(new(), [.. stars.Where(s => s.Length <= 1000).Take(1500)]));
        foreach (var q in queries)
        {
            Add($"graph search {q}", () => Pages(new("search", q)));
            Add($"explore {q}", async () => Json(await host.Explore(reader, q, null, 1, 40, ct)));
        }
        foreach (var star in stars)
        {
            Add($"graph get {star}", async () => Json(await host.Read(reader, null, null, ct, new GraphQuery("get", star))));
            Add($"graph neighbors {star}", () => Pages(new("neighbors", star)));
            Add($"graph content {star}", async () => Json(await host.Read(reader, null, null, ct, new GraphQuery("content", star))));
            Add($"graph data {star}", async () => Json(await host.Read(reader, null, null, ct, new GraphQuery("data", star))));
            Add($"explore star {star}", async () => Json(await host.Explore(reader, null, star, 2, GraphExplorer.MaxStars, ct)));
            Add($"feeds {star}", async () => (await feeds.Read(reader,
                [new("c", "contents", star), new("l", "links", star), new("d", "data", star)], true, ct)).ToJsonString());
        }
        Add("explore", async () => Json(await host.Explore(reader, null, null, 1, GraphExplorer.MaxStars, ct)));
        Add("changes", async () => Json(await host.Changes(reader, since, GraphExplorer.MaxChanges, ct)));
        Add("known", async () => Json((await host.Known(reader, stars, ct)).Order(StringComparer.Ordinal)));
        // Several reads at once, as several clients would ask; each answer is its own read.
        using var gate = new SemaphoreSlim(Parallel);
        var answered = await Task.WhenAll(reads.Select(async r =>
        {
            await gate.WaitAsync(ct);
            try { return (r.Name, Text: Normal(await r.Read(), run)); }
            catch (Exception e) when (e is not OperationCanceledException) { return (r.Name, Text: $"error: {e.GetType().Name}: {e.Message}"); }
            finally { gate.Release(); }
        }));
        return new(answered.ToDictionary(a => a.Name, a => a.Text, StringComparer.Ordinal), StringComparer.Ordinal);
    }
    const int Parallel = 8;

    // Every star the engine's own principal reads (every subject's), the corpus's own graph ids, and one nobody has.
    public static Task<string[]> Stars(ParityLoad load, Guid system, CancellationToken ct = default) => Stars(load.Host, load.ByGraphId.Keys, system, ct);
    public static async Task<string[]> Stars(RelationshipHost host, IEnumerable<string> known, Guid system, CancellationToken ct = default)
    {
        var all = new SortedSet<string>(known, StringComparer.Ordinal);
        for (string? cursor = null; ;)
        {
            var page = await host.Read(system, null, null, ct, new GraphQuery(Cursor: cursor));
            foreach (var node in page.nodes) all.Add(node.id);
            if (page.nextCursor is not { } next) break;
            cursor = next;
        }
        all.Add(Unknown);
        return [.. all];
    }
    public const string Unknown = "parity:parity:parity%3Anobody-has-this-star";

    // The run's own times become "<run>", and each row id an answer names becomes "<id N>" in the order it first appears.
    public static string Normal(string text, DateTimeOffset run)
    {
        text = Instant().Replace(text, m => DateTimeOffset.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            && at >= run ? "<run>" : m.Value);
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return Uuid().Replace(text, m => $"<id {(ids.TryGetValue(m.Value, out var n) ? n : ids[m.Value] = ids.Count + 1)}>");
    }

    // The first answers that differ, for a failure message.
    public static string Difference(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual, int limit = 3)
    {
        var lines = new List<string>();
        foreach (var name in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            var a = expected.GetValueOrDefault(name); var b = actual.GetValueOrDefault(name);
            if (a == b) continue;
            lines.Add($"{name}:\n  expected {Cut(a)}\n  actual   {Cut(b)}");
            if (lines.Count >= limit) break;
        }
        return string.Join("\n", lines);
        static string Cut(string? s) => s is null ? "(none)" : s.Length <= 600 ? s : s[..600] + "…";
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})")] private static partial Regex Instant();
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")] private static partial Regex Uuid();
}

// Opt-in: every map answer on the synthetic corpus, in both load modes, written to a folder so the answers of two
// builds can be compared as text (with scopes off they must be byte-for-byte the same):
//   RHYTHM_MAP_ANSWERS=<folder> RHYTHM_MODEL_DIR=<the pinned MiniLM files> sh engine/test-db.sh --filter MapAnswersRun
// One file per mode, map-answers-copies.json and map-answers-installed.json: for the household harness, the app and the
// engine's own principal, each operation's answer.
public sealed class MapAnswersFactAttribute : FactAttribute
{
    public MapAnswersFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_MAP_ANSWERS") is not { Length: > 0 })
            Skip = "NOT RUN: opt-in map answers; RHYTHM_MAP_ANSWERS=<folder> sh engine/test-db.sh --filter MapAnswersRun.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
            Skip = "NOT RUN: the map answers need a disposable Postgres with pgvector; run them through sh engine/test-db.sh.";
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class MapAnswersRun(ITestOutputHelper output)
{
    [MapAnswersFact] public async Task Every_map_answer_on_the_corpus_in_both_modes()
    {
        var folder = Environment.GetEnvironmentVariable("RHYTHM_MAP_ANSWERS")!;
        Directory.CreateDirectory(folder);
        foreach (var mode in new[] { ParityMode.Copies, ParityMode.Installed })
        {
            var server = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!;
            var name = "rhythm_map_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = NpgsqlDataSource.Create(server))
            await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
            var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
            try
            {
                var run = DateTimeOffset.UtcNow.AddMinutes(-1);
                await using var load = await ParityLoader.Load(source, ParityCorpus.Load(), Environment.GetEnvironmentVariable("RHYTHM_MODEL_DIR"), mode);
                Assert.Equal("complete", load.RefreshResult);
                var system = await new Principals(source).System(Principals.GraphName, default);
                var app = (await new Principals(source).SyncApp(Principals.NewToken(), default))!.Value;
                var stars = await MapAnswers.Stars(load, system);
                var queries = ParityGolden.Load().Queries.Select(q => q.Query).Where(q => q.Length <= 200).Distinct().Order(StringComparer.Ordinal).ToArray();
                var since = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-29);
                var result = new JsonObject();
                foreach (var (reader, principal) in new[] { ("household harness", load.Harness), ("app", app), ("system", system) })
                {
                    var answers = await MapAnswers.Of(load, principal, stars, queries, since, run);
                    result[reader] = new JsonObject(answers.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)JsonValue.Create(a.Value))));
                    output.WriteLine($"{mode} {reader}: {answers.Count} answers over {stars.Length} stars and {queries.Length} queries");
                }
                var file = Path.Combine(folder, $"map-answers-{(mode == ParityMode.Copies ? "copies" : "installed")}.json");
                await File.WriteAllTextAsync(file, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                output.WriteLine(file);
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
}
