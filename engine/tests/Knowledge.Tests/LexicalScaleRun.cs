using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Knowledge.Adapters;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// Opt-in, never part of an ordinary run: what ranked keyword search costs on rows as long as save_document accepts
// (40 000 characters, Documents), against D-029's 500 ms (lexical-search.md, D-054, SB-P2-21). Ranked search scores at
// most 800 candidates (AllWords + max(PerWord, Words / words) per word); since migration 047 it matches and scores each
// on the row's stored text vector, parsed once when the row was written, where before it parsed each candidate's text
// at every search, so its cost grew with their length.
//   RHYTHM_LEXICAL_SCALE=1 sh engine/test-db.sh --filter LexicalScaleRun
// RHYTHM_LEXICAL_SCALE_ROWS (default 1000, more than the candidate budget) and RHYTHM_LEXICAL_SCALE_CHARS (default
// 40000) size the synthetic catalog; RHYTHM_PARITY_OUT names a folder for lexical-scale.md. The rows are DEMO text of
// made-up words, cut at a word's end, a few words in every row and the rest in ever fewer (down to about one row in
// twenty), with an id and a name planted in a few. Each query runs once to warm up and then five times, through
// search_knowledge's own path (Retrieval.Search, limit 20) in both modes, the same ranked query parsing each row at the
// search as before 047, and combined retrieval's keyword arm (Recall, no model); the report gives the median and the
// slowest. Last it times migration 047 itself on those rows: the column dropped, 021's index put back, and the
// migration run again, as an install that upgrades runs it. It fails when a query errors, a planted record is not found,
// the catalog lacks the words its queries name (the "third of the rows" word between a quarter and nearly half of them),
// the ranked query parsing at the search answers other rows or another order than the one reading the column, or a
// ranked or combined query's slowest call is over 500 ms.
public sealed class LexicalScaleFactAttribute : FactAttribute
{
    public LexicalScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_LEXICAL_SCALE") != "1")
            Skip = "NOT RUN: opt-in measurement of ranked keyword search on long rows; RHYTHM_LEXICAL_SCALE=1 sh engine/test-db.sh --filter LexicalScaleRun (docs/dev/second-brain-parity/lexical-search.md).";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
            Skip = "NOT RUN: needs a disposable Postgres with pgvector; run it through sh engine/test-db.sh.";
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class LexicalScaleRun(DatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    const string Provider = "lexical-scale";
    sealed class Documents : IAdapter
    {
        public string Name => Provider;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
            new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    }
    // search_knowledge's own conditions (Retrieval.Search), for the queries this measures beside it.
    static readonly string NoMemory = $"r.provider<>'{Knowledge.Relationships.MemoryStars.Provider}'";
    static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;

    [LexicalScaleFact] public async Task Ranked_search_on_rows_as_long_as_a_saved_document()
    {
        var rows = Setting("RHYTHM_LEXICAL_SCALE_ROWS", 1000);
        var chars = Setting("RHYTHM_LEXICAL_SCALE_CHARS", 40000);
        var config = TestSettings.Use();
        var db = new Database(fixture.Source, new AdapterRegistry([new Documents()]).Policies(config));
        var reader = await new Principals(fixture.Source).System("lexical-scale-test", default);

        // 20 736 made-up words of four syllables, drawn with a strong skew (a word's chance falls with its place), so a
        // few are in every row and the rest in ever fewer, the rarest in about one row in twenty. Each row is cut at a
        // word's end, so no fragment of a word is left at its end to count as one. Planted in a few rows each: an id and
        // a name.
        var load = Stopwatch.StartNew();
        await using (var fill = fixture.Source.CreateCommand($"""
            WITH s AS (SELECT array['{string.Join("','", Syllables)}'] AS syl),
            vocab AS (SELECT array_agg(a || b || c || d ORDER BY a || b || c || d) AS w FROM s, unnest(s.syl) a, unnest(s.syl) b, unnest(s.syl) c, unnest(s.syl) d)
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
            SELECT gen_random_uuid(),'household','{Provider}','scale-' || i,'document','shared','Scale document ' || i,
              regexp_replace(left('DEMO: ' || CASE WHEN i % 331 = 7 THEN 'RHY-9001 ' ELSE '' END || CASE WHEN i % 211 = 5 THEN 'Quillon ' ELSE '' END
                || (SELECT string_agg(v.w[1 + floor(power(random(), 4) * 20736)::int], ' ') FROM vocab v, generate_series(1, @words) g WHERE i > 0),
                @chars + 1), '\s*\S*$', ''),
              'current','unclassified'
            FROM generate_series(1,@rows) i
            """))
        {
            fill.CommandTimeout = 600;
            fill.Parameters.AddWithValue("rows", rows);
            fill.Parameters.AddWithValue("chars", chars);
            fill.Parameters.AddWithValue("words", chars / 9 + 10);
            await fill.ExecuteNonQueryAsync();
        }
        await using (var analyze = fixture.Source.CreateCommand("ANALYZE resources")) await analyze.ExecuteNonQueryAsync();
        load.Stop();

        // The catalog's two commonest words, and one about a third of the rows hold, to ask for: words as written (the
        // simple configuration, unstemmed), each one of the vocabulary's.
        var common = new List<string>();
        (string Word, int Rows)? middle = null;
        var vocabulary = $"^({string.Join("|", Syllables)}){{4}}$";
        await using (var stat = fixture.Source.CreateCommand($"""
            WITH w AS (SELECT word,ndoc FROM ts_stat($q$SELECT to_tsvector('simple',content) FROM resources WHERE provider='{Provider}'$q$)
              WHERE word ~ '{vocabulary}')
            (SELECT word,ndoc,0 AS k FROM w ORDER BY ndoc DESC,word LIMIT 2)
            UNION ALL (SELECT word,ndoc,1 FROM w ORDER BY abs(ndoc - @rows / 3),word LIMIT 1)
            """))
        {
            stat.CommandTimeout = 600;
            stat.Parameters.AddWithValue("rows", rows);
            await using var r = await stat.ExecuteReaderAsync();
            while (await r.ReadAsync()) if (r.GetInt32(2) == 0) common.Add(r.GetString(0)); else middle = (r.GetString(0), r.GetInt32(1));
        }
        Assert.True(common.Count == 2 && middle is not null, "the catalog has the words the queries need");
        Assert.InRange(middle!.Value.Rows, rows / 4, rows * 45 / 100);

        (string Name, string Query, bool Planted)[] queries =
        [
            ("an id a few rows hold", "RHY-9001", true),
            ("an id no row holds", "RHY-0000", false),
            ("a name a few rows hold", "Quillon", true),
            ("a word a third of the rows hold", middle.Value.Word, false),
            ("the two commonest words", $"{common[0]} {common[1]}", false),
            ("a question", $"when is the {common[0]} {middle.Value.Word} report from Quillon due?", true),
        ];
        var report = new StringBuilder()
            .AppendLine("# Ranked keyword search on long rows (DEMO data only)")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{rows} rows of {chars} characters, loaded in {load.Elapsed.TotalSeconds:0.0} s; limit 20; one warm-up, then five calls each.")
            .AppendLine("ranked reads the stored text vector (migration 047); ranked, parsed is the same query parsing each row's text at the search, as before 047;")
            .AppendLine("combined is combined retrieval's keyword arm (Recall, no model), its catalog tier reading the vector.")
            .AppendLine()
            .AppendLine("| Query | Mode | Median ms | Slowest ms | Rows |")
            .AppendLine("|---|---|---|---|---|");
        // One call to warm up, then five timed: the first answer, the median and the slowest.
        static async Task<(string First, double Median, double Slowest)> Timed(Func<Task<string>> call)
        {
            var first = await call();
            var times = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                await call();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return (first, times[2], times[4]);
        }
        var slow = new List<string>();
        var columns = $"{Retrieval.Columns},left(r.content,400) AS excerpt";
        var recall = new Recall(db, null, null, null, RecallOptions.Default);
        foreach (var (name, query, planted) in queries)
        {
            var (legacy, legacyMedian, legacySlowest) = await Timed(() => new Retrieval(db, null, LexicalSearch.Legacy).Search(reader, query, 20, default));
            var (ranked, rankedMedian, rankedSlowest) = await Timed(() => new Retrieval(db, null, LexicalSearch.Standard).Search(reader, query, 20, default));
            // The same ranked query parsing each row's text at the search, as before 047: it answers the same rows in the
            // same order, so the column changed only the time.
            var (sql, args) = LexicalSearch.Standard.Sql(query, columns, $"{Database.Visible()} AND {NoMemory}", "20");
            Assert.Contains($"r.{LexicalSearch.Stored}", sql);
            Assert.DoesNotContain($"to_tsvector('{LexicalSearch.Chosen}',r.title", sql);
            var before = sql.Replace($"r.{LexicalSearch.Stored}", $"to_tsvector('{LexicalSearch.Chosen}',r.title || ' ' || r.content)", StringComparison.Ordinal);
            var (parsed, parsedMedian, parsedSlowest) = await Timed(() => db.Read(reader, before, default, args));
            Assert.Equal(Ids(await db.Read(reader, sql, default, args)), Ids(parsed));
            var (combined, combinedMedian, combinedSlowest) = await Timed(async () =>
                JsonSerializer.Serialize((await recall.Search(reader, new(query, 20), default)).Hits.Select(h => new { id = h.Id.ToString() })));
            var found = JsonDocument.Parse(ranked).RootElement.GetArrayLength();
            if (planted)
            {
                Assert.True(found > 0, $"{name}: ranked search found nothing");
                Assert.True(JsonDocument.Parse(combined).RootElement.GetArrayLength() > 0, $"{name}: combined retrieval found nothing");
            }
            foreach (var (mode, first, median, slowest) in new[] { ("legacy", legacy, legacyMedian, legacySlowest), ("ranked", ranked, rankedMedian, rankedSlowest),
                         ("ranked, parsed", parsed, parsedMedian, parsedSlowest), ("combined", combined, combinedMedian, combinedSlowest) })
                report.AppendLine(CultureInfo.InvariantCulture, $"| {name} (`{query}`) | {mode} | {median:0.0} | {slowest:0.0} | {JsonDocument.Parse(first).RootElement.GetArrayLength()} |");
            foreach (var (mode, slowest) in new[] { ("ranked", rankedSlowest), ("combined", combinedSlowest) })
                if (slowest > 500) slow.Add($"{name} ({mode}): {slowest:0} ms");
        }

        // Where the time goes: the plans of both modes for the id no row holds.
        foreach (var (mode, search) in new[] { ("legacy", LexicalSearch.Legacy), ("ranked", LexicalSearch.Standard) })
        {
            var (sql, args) = search.Sql("RHY-0000", Retrieval.Columns, $"{Database.Visible()} AND {NoMemory}", "20");
            var plan = new StringBuilder();
            await using (var c = await fixture.Source.OpenConnectionAsync())
            await using (var explain = Database.Command(c, null, "EXPLAIN (ANALYZE, COSTS OFF) " + sql, [.. args, ("principal", reader)]))
            await using (var r = await explain.ExecuteReaderAsync())
                while (await r.ReadAsync()) plan.AppendLine(r.GetString(0));
            output.WriteLine($"Plan, {mode}, an id no row holds:{Environment.NewLine}{plan}");
        }

        // Migration 047 on these rows, as an install that upgrades runs it: the column and its index taken away and 021's
        // expression index put back (the schema before 047), then the migration's own file run again, whose statements
        // add the column (a rewrite of the table, every row parsed once), index it and drop 021's index.
        await using (var undo = fixture.Source.CreateCommand($"""
            ALTER TABLE resources DROP COLUMN {LexicalSearch.Stored};
            CREATE INDEX resources_search_english ON resources USING gin(to_tsvector('english',title || ' ' || content));
            """)) { undo.CommandTimeout = 600; await undo.ExecuteNonQueryAsync(); }
        var migration = Database.Migrations().Single(m => m.Version.EndsWith("_search_vector.sql", StringComparison.Ordinal)).Sql;
        var rewrite = Stopwatch.StartNew();
        await using (var again = fixture.Source.CreateCommand(migration)) { again.CommandTimeout = 600; await again.ExecuteNonQueryAsync(); }
        rewrite.Stop();
        Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source,
            $"SELECT count(*) FROM resources WHERE {LexicalSearch.Stored} IS DISTINCT FROM to_tsvector('english'::regconfig,title || ' ' || content)"));
        var size = (string)(await MemoryRows.Scalar(fixture.Source,
            "SELECT pg_size_pretty(pg_total_relation_size('resources'))||' in all, the new index '||pg_size_pretty(pg_relation_size('resources_search_vector'))"))!;
        report.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Migration 047 on these {rows} rows (the table rewritten, every row parsed once, its index built, 021's dropped): {rewrite.Elapsed.TotalSeconds:0.0} s; resources then {size}.");
        output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("RHYTHM_PARITY_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "lexical-scale.md"), report.ToString());
        }
        Assert.True(slow.Count == 0, "over D-029's 500 ms: " + string.Join("; ", slow));
    }

    static readonly string[] Syllables = ["ka", "lo", "mi", "ne", "ru", "sa", "te", "vo", "bi", "du", "fe", "go"];

    static string[] Ids(string rows) => [.. JsonDocument.Parse(rows).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];
}
