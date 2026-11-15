using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// D-258: the map path (explore_graph) with graph.similarity.maxLinks against the same map without it, both with
// graph.similarity.model: bge-small as installs set it (D-256), in each load mode. Opt-in, never part of an ordinary run:
//   RHYTHM_PARITY_EVAL=1 RHYTHM_PARITY_LINKCAP=6 RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files>
//     sh engine/test-db.sh --filter MapLinkCapRun
// RHYTHM_PARITY_LINKCAP lists the caps to measure (comma separated, 2 to 50), RHYTHM_PARITY_MODE and RHYTHM_PARITY_OUT as
// the gate; the report is map-linkcap.json and .md. Per mode and cap: recall at 5 and 10, MRR and nDCG at 10 over the
// judged queries, overall and per question type (EmbeddingBenchmark.Metrics, the gate's judged set); the gate's verdict
// of the capped map against the uncapped one (ParityGate.Evaluate, its pre-registered rules); and the links each map drew:
// similar links, links per star (most and median, over the stars with one) and the stars with a similar link uncapped and
// none capped. Information for the decision, never a lock: the run fails only when a query errors or the map model did not
// load. Without either model's files it is skipped, saying so (D-041).
public sealed class MapLinkCapFactAttribute : FactAttribute
{
    public MapLinkCapFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_PARITY_EVAL") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_PARITY_LINKCAP")))
        {
            Skip = "NOT RUN: opt-in measurement of graph.similarity.maxLinks (D-258); RHYTHM_PARITY_EVAL=1 RHYTHM_PARITY_LINKCAP=6 "
                + "RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> sh engine/test-db.sh --filter MapLinkCapRun.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
        {
            Skip = "NOT RUN: the link cap measurement needs a disposable Postgres with pgvector; run it through sh engine/test-db.sh.";
            return;
        }
        foreach (var model in new[] { EmbeddingModels.MiniLm, EmbeddingModels.BgeSmall })
            if (EmbeddingModels.Given(model) is not { } folder || !model.Files.All(f => File.Exists(Path.Combine(folder, f.Name))))
            {
                Skip = $"NOT RUN: the link cap measurement needs the pinned {model.Name} files in the folder {model.Variable} names (D-041).";
                return;
            }
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class MapLinkCapRun(ITestOutputHelper output)
{
    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    static string N(JsonNode? x) => x is null ? "-" : x.GetValue<double>().ToString("0.000", CultureInfo.InvariantCulture);
    static string Signed(double x) => (x >= 0 ? "+" : "") + x.ToString("0.000", CultureInfo.InvariantCulture);
    static readonly string[] Columns = ["recall@5", "recall@10", "mrr@10", "ndcg@10"];

    // One map measured: its runs on the map path, the gate's view of them, and the similar links of each star.
    sealed record Measured(GatePath Path, IReadOnlyList<(ParityQuery Query, ParityRun Run)> Runs, IReadOnlyDictionary<string, int> Degrees,
        (int Similar, int Mutual, int Copies) Links, string? Model);

    [MapLinkCapFact] public async Task The_capped_map_against_the_uncapped_one_with_the_map_model()
    {
        var caps = Env("RHYTHM_PARITY_LINKCAP")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 2 and <= 50 ? n
                : throw new ArgumentException($"RHYTHM_PARITY_LINKCAP lists whole numbers from 2 to 50, not {c}")).Distinct().ToArray();
        var modes = Env("RHYTHM_PARITY_MODE") switch
        {
            "copies" => new[] { ParityMode.Copies },
            "installed" => [ParityMode.Installed],
            null or "both" => [ParityMode.Copies, ParityMode.Installed],
            var other => throw new ArgumentException($"RHYTHM_PARITY_MODE is copies, installed or both, not {other}"),
        };
        var corpus = ParityCorpus.Load();
        var golden = ParityGolden.Load();
        var fingerprint = ParityGate.Fingerprint(ParityCorpus.Folder);
        var report = new JsonObject { ["schema"] = "rhythm-map-linkcap.v1", ["label"] = "DEMO: synthetic corpus only", ["mapModel"] = EmbeddingModels.BgeSmall.Id };
        var md = new StringBuilder("# The map's similar links capped a star (D-258)\n\nSynthetic corpus only. Both maps with graph.similarity.model: bge-small; "
            + "the baseline without graph.similarity.maxLinks.\n\n");
        var problems = new List<string>();
        var results = new JsonArray();
        foreach (var mode in modes)
        {
            var modeName = ParityReport.ModeName(mode);
            var baseline = await Measure(mode, null, corpus, golden, problems);
            foreach (var cap in caps)
            {
                var capped = await Measure(mode, cap, corpus, golden, problems);
                var verdict = ParityGate.Evaluate(new GateRun(modeName, golden.K, fingerprint, baseline.Model, baseline.Path),
                    new GateRun(modeName, golden.K, fingerprint, capped.Model, capped.Path));
                var types = golden.Queries.Select(q => q.Type).Distinct().Order(StringComparer.Ordinal).ToArray();
                JsonObject Scores(Measured m) => new()
                {
                    ["overall"] = EmbeddingBenchmark.Metrics(m.Runs),
                    ["types"] = new JsonObject(types.Select(t => KeyValuePair.Create(t, (JsonNode?)EmbeddingBenchmark.Metrics([.. m.Runs.Where(r => r.Query.Type == t)])))),
                };
                JsonObject Links(Measured m)
                {
                    var degrees = m.Degrees.Values.Order().ToArray();
                    return new()
                    {
                        ["similar"] = m.Links.Similar, ["mutual"] = m.Links.Mutual, ["copies"] = m.Links.Copies, ["starsWithOne"] = degrees.Length,
                        ["most"] = degrees.Length == 0 ? 0 : degrees[^1],
                        ["median"] = degrees.Length == 0 ? 0 : degrees.Length % 2 == 1 ? degrees[degrees.Length / 2] : (degrees[degrees.Length / 2 - 1] + degrees[degrees.Length / 2]) / 2.0,
                    };
                }
                var lost = baseline.Degrees.Keys.Where(k => !capped.Degrees.ContainsKey(k)).Count();
                var (before, after) = (Scores(baseline), Scores(capped));
                results.Add(new JsonObject
                {
                    ["mode"] = modeName, ["cap"] = cap, ["verdict"] = verdict.Verdict,
                    ["rules"] = JsonSerializer.SerializeToNode(verdict.Rules, ParityGate.Json),
                    ["deltas"] = JsonSerializer.SerializeToNode(verdict.Deltas, ParityGate.Json),
                    ["losers"] = JsonSerializer.SerializeToNode(verdict.Losers, ParityGate.Json),
                    ["uncapped"] = before, ["capped"] = after,
                    ["linksUncapped"] = Links(baseline), ["linksCapped"] = Links(capped), ["starsLosingEverySimilarLink"] = lost,
                });

                md.AppendLine($"## {modeName}, at most {cap} similar links a star: {verdict.Verdict}").AppendLine();
                md.AppendLine("| Rule | Status | Detail |").AppendLine("|---|---|---|");
                foreach (var r in verdict.Rules) md.AppendLine($"| {r.Name} | {r.Status} | {r.Detail} |");
                md.AppendLine().AppendLine("| Scope | Judged | " + string.Join(" | ", Columns.Select(c => c + " uncapped → capped")) + " |")
                    .AppendLine("|---|---|" + string.Concat(Columns.Select(_ => "---|")));
                void Row(string scope, JsonNode b, JsonNode a) => md.AppendLine($"| {scope} | {a["judged"]} | " + string.Join(" | ", Columns.Select(c =>
                    $"{N(b[c])} → {N(a[c])}" + (b[c] is { } x && a[c] is { } y ? $" ({Signed(y.GetValue<double>() - x.GetValue<double>())})" : ""))) + " |");
                Row("overall", before["overall"]!, after["overall"]!);
                foreach (var t in types) Row(t, before["types"]![t]!, after["types"]![t]!);
                var (lb, la) = (Links(baseline), Links(capped));
                md.AppendLine().AppendLine($"Links: similar {lb["similar"]} → {la["similar"]} (by the mutual rule {lb["mutual"]} → {la["mutual"]}), copies {lb["copies"]} → {la["copies"]}; "
                    + $"stars with a similar link {lb["starsWithOne"]} → {la["starsWithOne"]}, most a star {lb["most"]} → {la["most"]}, median {lb["median"]} → {la["median"]}; "
                    + $"{lost} star(s) lose every similar link.").AppendLine();
                if (verdict.Losers.Count > 0)
                {
                    md.AppendLine($"Queries that lost: {verdict.Losers.Count}.").AppendLine();
                    foreach (var l in verdict.Losers.Take(20)) md.AppendLine($"- `{l.Id}` ({l.Type}): {l.Detail}");
                    md.AppendLine();
                }
            }
        }
        report["results"] = results;
        var folder = Env("RHYTHM_PARITY_OUT") ?? Path.Combine(AppContext.BaseDirectory, "parity-report");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "map-linkcap.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(folder, "map-linkcap.md"), md.ToString());
        output.WriteLine($"Map link cap: {folder}");
        output.WriteLine(md.ToString());
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    async Task<Measured> Measure(ParityMode mode, int? cap, ParityCorpus corpus, ParityGolden golden, List<string> problems)
    {
        var server = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!;
        var name = "rhythm_linkcap_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        try
        {
            await using var load = await ParityLoader.Load(source, corpus, EmbeddingModels.Given(EmbeddingModels.MiniLm), mode,
                mapModel: EmbeddingModels.BgeSmall, mapDirectory: EmbeddingModels.Given(EmbeddingModels.BgeSmall), mapMaxLinks: cap);
            Assert.Equal("complete", load.RefreshResult);
            Assert.True(load.MapModel == EmbeddingModels.BgeSmall, $"the map model did not load: {string.Join("; ", load.RefreshNotes)}");
            var evaluator = new ParityEvaluator(load, golden);
            await evaluator.WarmUp(default);
            var path = evaluator.Paths.Single(p => p.Name == "graph");
            var runs = new List<(ParityQuery Query, ParityRun Run)>();
            foreach (var q in golden.Queries) runs.Add((q, await evaluator.Run(path, q, default)));
            foreach (var r in runs.Where(r => r.Run.Status == "error")) problems.Add($"{ParityReport.ModeName(mode)} cap {cap?.ToString(CultureInfo.InvariantCulture) ?? "none"}: {r.Query.Id}: {r.Run.Error}");
            // Each star by what it stands for, not its row id, so two loads compare: subject, provider and external id.
            var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
            await using (var cmd = source.CreateCommand("""
                WITH links AS (SELECT source_id, target_id FROM relationships WHERE engine=@engine AND lifecycle='active' AND type='similar_to'),
                  ends AS (SELECT source_id AS id FROM links UNION ALL SELECT target_id FROM links)
                SELECT r.subject_id || '/' || r.provider || '/' || r.external_id, count(*)::int FROM ends JOIN resources r ON r.id = ends.id GROUP BY 1
                """))
            {
                cmd.Parameters.AddWithValue("engine", RelationshipEngine.Owner);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) degrees[reader.GetString(0)] = reader.GetInt32(1);
            }
            return new(ParityGate.FromRuns(path, runs, ParityGate.Clusters(golden)), runs, degrees, load.MapLinks, load.Model);
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
