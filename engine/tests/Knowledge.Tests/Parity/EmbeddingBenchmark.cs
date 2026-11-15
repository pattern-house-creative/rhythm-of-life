using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// SB-P2-06, D-025: MiniLM against bge-small-en-v1.5 (and, when named, bge-base-en-v1.5) on the golden set, opt-in and
// never part of an ordinary run:
//   RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> sh engine/bench-embeddings.sh
// which runs this through engine/test-db.sh (RHYTHM_EMBEDDING_BENCH=1, --filter EmbeddingBenchmarkRun). Without either
// model's files it is skipped, saying so: it never downloads a model (D-041). RHYTHM_PARITY_OUT names the report folder
// (default: parity-report beside the tests); the report is embedding-benchmark.json and .md, and each variant's gate
// report embedding-benchmark-gate-<variant>-<mode>.md.
// Each variant (Variants) is loaded into a throwaway database in each load mode exactly as the evaluation loads the
// corpus (ParityLoader), and every golden query runs on every retrieval path (ParityEvaluator), so a variant differs from
// MiniLM only in its model, its questions' instruction and the map's similarity thresholds. Per path it records recall at
// 5 and 10, MRR at 10 and nDCG at 10 over the judged queries (a relevant answer, no capability the path lacks: the
// gate's own), and p50 and p95 latency (each query's median of three calls). Per model, outside any database: its
// files on disk, the extra memory of the process once it has loaded and embedded the corpus's passages and the golden
// questions, and the time to embed one passage and one question. Per load: the passages and the space they take.
// Every variant is judged by the gate (ParityGate, SB-P2-15) against the checked-in MiniLM locks, as
// RHYTHM_PARITY_GATE_MODEL_CHANGE=1 judges it: a run of another model is comparable only as a declared model change. The
// verdicts are recorded, never failing the run; the run fails only when a query errors, or when MiniLM's own run moves
// from the locks (then the benchmark is not measuring what the gate measures). Nothing here changes the live model:
// that is MiniLM, and EmbeddingSettings refuses another until the owner switches it (D-025).
public sealed class EmbeddingBenchmarkFactAttribute : FactAttribute
{
    public EmbeddingBenchmarkFactAttribute()
    {
        if (EmbeddingBenchmark.NotRun(Environment.GetEnvironmentVariable) is { } why) Skip = why;
    }
}

// One configuration measured: a model, the instruction its questions are embedded after (none: as they are), and the
// map's similarity thresholds: `today` (graph.similarity's defaults, every install's) or `tuned` (the model's own,
// SB-P2-13: MiniLM's through adapters.embeddings.thresholds, another model's as graph.similarity numbers, since the
// settings refuse it as the live model).
public sealed record BenchVariant(string Name, EmbeddingModel Model, string? Instruction, string Thresholds, string How)
{
    public string Label => ParityModelChoice.Name(Model.Id, Instruction);
}

public static class EmbeddingBenchmark
{
    public const string Schema = "rhythm-embedding-benchmark.v1";
    public static readonly int[] Cuts = [5, 10];

    public static IReadOnlyList<BenchVariant> Variants { get; } =
    [
        new("minilm", EmbeddingModels.MiniLm, null, "today", "MiniLM, the live model, questions as they are, the map's thresholds as today: every install's"),
        new("bge-small", EmbeddingModels.BgeSmall, null, "today", "bge-small-en-v1.5, questions as they are (D-068), the map's thresholds as today"),
        new("bge-small-instruction", EmbeddingModels.BgeSmall, QueryInstruction.Bge, "today",
            "bge-small-en-v1.5, each question after its retrieval instruction, the map's thresholds as today"),
        new("minilm-tuned", EmbeddingModels.MiniLm, null, "tuned", "MiniLM with adapters.embeddings.thresholds: tuned (SB-P2-13): only the map's similar links differ"),
        new("bge-small-tuned", EmbeddingModels.BgeSmall, null, "tuned", "bge-small-en-v1.5, questions as they are, with its own re-tuned map thresholds (SB-P2-13)"),
        // bge-base-en-v1.5 (768 dimensions), measured as bge-small was (D-249): its passages only in the throwaway databases,
        // whose passage column the loader widens there (ParityLoader). Run when named in RHYTHM_EMBEDDING_BENCH_VARIANTS.
        new("bge-base", EmbeddingModels.BgeBase, null, "today", "bge-base-en-v1.5, questions as they are, the map's thresholds as today"),
        new("bge-base-instruction", EmbeddingModels.BgeBase, QueryInstruction.Bge, "today",
            "bge-base-en-v1.5, each question after its retrieval instruction, the map's thresholds as today"),
        new("bge-base-tuned", EmbeddingModels.BgeBase, null, "tuned", "bge-base-en-v1.5, questions as they are, with its own re-tuned map thresholds (SB-P2-13's rules)"),
    ];

    // What runs without RHYTHM_EMBEDDING_BENCH_VARIANTS: run 18's five, so its command measures what it measured.
    public static IReadOnlyList<string> Defaults { get; } = ["minilm", "bge-small", "bge-small-instruction", "minilm-tuned", "bge-small-tuned"];

    // The variants a run measures: those RHYTHM_EMBEDDING_BENCH_VARIANTS names (a comma list), else the defaults.
    public static IReadOnlyList<BenchVariant> Selected(Func<string, string?> environment)
    {
        var only = environment("RHYTHM_EMBEDDING_BENCH_VARIANTS") is { Length: > 0 } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Defaults;
        return [.. Variants.Where(v => only.Contains(v.Name))];
    }

    // The models a run loads: MiniLM always (its run must answer as the locks hold), and each selected variant's.
    public static IReadOnlyList<EmbeddingModel> Models(Func<string, string?> environment) =>
        [.. new[] { EmbeddingModels.MiniLm }.Concat(Selected(environment).Select(v => v.Model)).Distinct()];

    // Why the benchmark does not run here, or null when it does.
    public static string? NotRun(Func<string, string?> environment)
    {
        if (environment("RHYTHM_EMBEDDING_BENCH") != "1")
            return "NOT RUN: opt-in embedding benchmark (SB-P2-06); RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> sh engine/bench-embeddings.sh (docs/dev/second-brain-parity/benchmarks.md).";
        if (string.IsNullOrWhiteSpace(environment("RHYTHM_TEST_DB")))
            return "NOT RUN: the embedding benchmark needs a disposable Postgres with pgvector; run it through sh engine/bench-embeddings.sh.";
        foreach (var model in Models(environment))
            if (EmbeddingModels.Given(model, environment) is not { } folder || !model.Files.All(f => File.Exists(Path.Combine(folder, f.Name))))
                return $"NOT RUN: the embedding benchmark needs the pinned {model.Name} files ({string.Join(", ", model.Files.Select(f => f.Name))}) "
                    + $"in the folder {model.Variable} names; it never downloads a model (D-041).";
        return null;
    }

    // The settings the map is built with for a variant, or null for the tests' own (today's thresholds).
    public static RhythmConfig? Settings(BenchVariant variant)
    {
        if (variant.Thresholds == "today") return null;
        if (variant.Model == EmbeddingModels.Default)
            return RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings:\n    enabled: true\n    thresholds: tuned\n");
        var t = EmbeddingThresholds.Tuned[variant.Model.Id];
        return RhythmConfig.Parse(TestSettings.Yaml + "\ngraph:\n  similarity:\n" + string.Create(CultureInfo.InvariantCulture,
            $"    threshold: {t.Similar}\n    documentThreshold: {t.SimilarDocuments}\n    mutualThreshold: {t.Mutual}\n"));
    }

    // Reciprocal rank of the first relevant record within the first k, 0 when none is there; null with nothing relevant.
    public static double? MrrAt(IReadOnlyList<string> ranked, IReadOnlyList<ParityGrade> relevant, int k)
    {
        if (relevant.Count == 0) return null;
        for (var i = 0; i < Math.Min(k, ranked.Count); i++) if (relevant.Any(r => r.Id == ranked[i])) return 1.0 / (i + 1);
        return 0;
    }

    // A path's numbers over its judged queries (as the gate judges: measured, a relevant answer, no gap), and its latency
    // over every measured query (as the gate's p95).
    public static JsonObject Metrics(IReadOnlyList<(ParityQuery Query, ParityRun Run)> runs)
    {
        var measured = runs.Where(r => r.Run.Status is not ("unavailable" or "error")).ToArray();
        var judged = measured.Where(r => r.Run.Gaps.Count == 0 && r.Query.Relevant.Length > 0).ToArray();
        double? Mean(Func<(ParityQuery Query, ParityRun Run), double?> f) =>
            judged.Select(f).Where(x => x is not null).Select(x => x!.Value).ToArray() is { Length: > 0 } xs ? Math.Round(xs.Average(), 4) : null;
        var o = new JsonObject
        {
            ["judged"] = judged.Length,
            ["clusters"] = judged.Select(r => r.Query.ClusterKey).Distinct(StringComparer.Ordinal).Count(),
        };
        foreach (var k in Cuts) o[$"recall@{k}"] = Mean(r => ParityMetrics.Recall(r.Run.Ranked, r.Query.Relevant, k));
        o["mrr@10"] = Mean(r => MrrAt(r.Run.Ranked, r.Query.Relevant, 10));
        o["ndcg@10"] = Mean(r => ParityMetrics.Ndcg(r.Run.Ranked, r.Query.Relevant, 10));
        o["latencyMsP50"] = measured.Length > 0 ? Math.Round(ParityMetrics.Percentile(measured.Select(r => r.Run.LatencyMs).ToArray(), 50), 2) : null;
        o["latencyMsP95"] = measured.Length > 0 ? Math.Round(ParityMetrics.Percentile(measured.Select(r => r.Run.LatencyMs).ToArray(), 95), 2) : null;
        return o;
    }

    // One model's cost outside any database: its files, the process's extra memory once it has loaded and embedded every
    // passage of the corpus and every golden question, and the time each took. The process's memory is read after a full
    // collection, so it is the model's native memory (the inference runtime's) more than anything managed; it is
    // approximate, since the test process holds other things too.
    public static JsonObject Cost(EmbeddingModel spec, string folder, IReadOnlyList<string> texts, IReadOnlyList<string> questions, string? instruction)
    {
        static (long WorkingSet, long Private) Memory()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return (process.WorkingSet64, process.PrivateMemorySize64);
        }
        var before = Memory();
        var started = Stopwatch.GetTimestamp();
        using var model = new LocalEmbeddings(folder, spec);
        var loadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        IEmbedder asker = instruction is null ? model : new QueryInstruction(model, instruction);
        var passages = texts.SelectMany(model.Chunks).ToArray();
        var passageMs = Timed(passages, p => model.Embed(p));
        var questionMs = Timed(questions, q => asker.Embed(q));
        var after = Memory();
        const double MiB = 1024 * 1024;
        return new JsonObject
        {
            ["model"] = ParityModelChoice.Name(spec.Id, instruction),
            ["filesBytes"] = spec.Files.Sum(f => new FileInfo(Path.Combine(folder, f.Name)).Length),
            ["weightsBytes"] = new FileInfo(Path.Combine(folder, spec.Weights.Name)).Length,
            ["loadMs"] = Math.Round(loadMs, 1),
            ["passages"] = passages.Length,
            ["passageMsMean"] = Math.Round(passageMs.Average(), 2),
            ["passageMsP95"] = Math.Round(ParityMetrics.Percentile(passageMs, 95), 2),
            ["questions"] = questions.Count,
            ["questionMsP50"] = Math.Round(ParityMetrics.Percentile(questionMs, 50), 2),
            ["questionMsP95"] = Math.Round(ParityMetrics.Percentile(questionMs, 95), 2),
            ["workingSetMiB"] = Math.Round((after.WorkingSet - before.WorkingSet) / MiB, 1),
            ["privateMiB"] = Math.Round((after.Private - before.Private) / MiB, 1),
        };
    }

    static double[] Timed(IReadOnlyList<string> texts, Action<string> embed) => [.. texts.Select(t =>
    {
        var started = Stopwatch.GetTimestamp();
        embed(t);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    })];

    static string F(JsonNode? n, string format = "0.000") =>
        n is null ? "–" : double.Parse(n.ToJsonString(), CultureInfo.InvariantCulture).ToString(format, CultureInfo.InvariantCulture);

    public static string Markdown(JsonObject report)
    {
        var md = new StringBuilder();
        md.AppendLine("# Embedding benchmark").AppendLine();
        md.AppendLine($"Generated {report["generatedAt"]} from the synthetic parity corpus (DEMO data only), {report["queries"]} golden queries; "
            + "metrics over each path's judged queries (a relevant answer, no capability the path lacks), latency over every measured query "
            + "(each query's median of three calls). Gate verdicts are against the checked-in MiniLM locks, a model change declared where the "
            + "model or its questions differ (RHYTHM_PARITY_GATE_MODEL_CHANGE=1).").AppendLine();
        md.AppendLine("## Variants").AppendLine();
        md.AppendLine("| Variant | Model | Map thresholds | How |").AppendLine("|---|---|---|---|");
        foreach (var v in report["variants"]!.AsArray())
            md.AppendLine($"| {v!["name"]} | `{v["model"]}` | {v["thresholds"]} | {v["how"]} |");
        md.AppendLine();
        md.AppendLine("## Each model's cost").AppendLine();
        md.AppendLine("| Model | Files | Load | Passages | Per passage (mean / p95) | Per question (p50 / p95) | Extra working set | Extra private memory |")
          .AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in report["models"]!.AsArray())
            md.AppendLine($"| `{c!["model"]}` | {F(c["filesBytes"], "0") } bytes | {F(c["loadMs"], "0")} ms | {c["passages"]} | {F(c["passageMsMean"], "0.0")} / {F(c["passageMsP95"], "0.0")} ms | "
                + $"{F(c["questionMsP50"], "0.0")} / {F(c["questionMsP95"], "0.0")} ms | {F(c["workingSetMiB"], "0.0")} MiB | {F(c["privateMiB"], "0.0")} MiB |");
        md.AppendLine();
        foreach (var mode in new[] { "copies", "installed" })
        {
            var runs = report["runs"]!.AsArray().Where(r => r!["mode"]!.GetValue<string>() == mode).ToArray();
            if (runs.Length == 0) continue;
            md.AppendLine($"## {mode}").AppendLine();
            md.AppendLine("Answers against the lock: how many queries' rankings differ from the lock's. A path whose answers are the lock's "
                + "is no candidate, and its FAIL for want of a gain says nothing is wrong (D-056).").AppendLine();
            md.AppendLine("| Variant | Path | Judged | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 ms | p95 ms | Gate | Answers against the lock |")
              .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var run in runs)
                foreach (var (path, node) in run!["paths"]!.AsObject())
                {
                    var m = node!["metrics"]!;
                    var g = node["gate"]!;
                    var changed = g["changed"]!.GetValue<int>();
                    md.AppendLine($"| {run["variant"]} | {path} | {m["judged"]} | {F(m["recall@5"])} | {F(m["recall@10"])} | {F(m["mrr@10"])} | {F(m["ndcg@10"])} | "
                        + $"{F(m["latencyMsP50"], "0.0")} | {F(m["latencyMsP95"], "0.0")} | **{g["verdict"]}** | {(changed == 0 ? "the lock's" : $"{changed} changed")} |");
                }
            md.AppendLine();
            md.AppendLine("| Variant | Load ms | Passages | Passage vectors | resource_chunks on disk |").AppendLine("|---|---|---|---|---|");
            foreach (var run in runs)
                md.AppendLine($"| {run!["variant"]} | {F(run["loadMs"], "0")} | {run["disk"]!["passages"]} | {run["disk"]!["vectorBytes"]} bytes | {run["disk"]!["tableBytes"]} bytes |");
            md.AppendLine();
        }
        md.AppendLine("## Why each candidate failed").AppendLine();
        md.AppendLine("The failed rules of every path whose answers differ from the lock's (each variant's gate report has the deltas and "
            + "intervals). Privacy is INCONCLUSIVE on every path, MiniLM's included, for the household model's reveals (no viewer scope), "
            + "which the locks share.").AppendLine();
        foreach (var run in report["runs"]!.AsArray())
            foreach (var (path, node) in run!["paths"]!.AsObject())
            {
                var gate = node!["gate"]!;
                if (gate["changed"]!.GetValue<int>() == 0) continue;
                foreach (var rule in gate["rules"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() == "fail"))
                    md.AppendLine($"- {run["variant"]}, {run["mode"]}, {path}: {rule!["name"]}: {rule["detail"]}");
            }
        return md.ToString();
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class EmbeddingBenchmarkRun(ITestOutputHelper output)
{
    [EmbeddingBenchmarkFact] public async Task MiniLM_against_bge_small_on_the_golden_set()
    {
        var corpus = ParityCorpus.Load();
        var golden = ParityGolden.Load();
        var folders = EmbeddingBenchmark.Models(Environment.GetEnvironmentVariable).ToDictionary(m => m, m => EmbeddingModels.Given(m)!);
        foreach (var (model, files) in folders) Assert.True(EmbeddingModels.Verified(model, files), $"the {model.Name} files in {model.Variable} do not match their pinned checksums");
        var variants = EmbeddingBenchmark.Selected(Environment.GetEnvironmentVariable).ToArray();
        Assert.NotEmpty(variants);

        // Each model's cost, the inference runtime warmed first by a model loaded and let go, so neither pays for its start.
        using (new LocalEmbeddings(folders[EmbeddingModels.MiniLm], EmbeddingModels.MiniLm)) { }
        var questions = golden.Queries.Select(q => q.Query).ToArray();
        var texts = corpus.Texts.Select(t => t.Text).ToArray();
        var costs = new JsonArray();
        foreach (var (model, instruction) in variants.Select(v => (v.Model, v.Instruction)).Distinct())
            costs.Add(EmbeddingBenchmark.Cost(model, folders[model], texts, questions, instruction));

        var problems = new List<string>();
        var runs = new JsonArray();
        var folder = Environment.GetEnvironmentVariable("RHYTHM_PARITY_OUT") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "parity-report");
        Directory.CreateDirectory(folder);
        foreach (var variant in variants)
            foreach (var mode in new[] { ParityMode.Copies, ParityMode.Installed })
                runs.Add(await Measure(variant, mode, folders[variant.Model], corpus, golden, folder, problems));

        var report = new JsonObject
        {
            ["schema"] = EmbeddingBenchmark.Schema,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["label"] = "DEMO: synthetic corpus only",
            ["queries"] = golden.Queries.Length, ["k"] = golden.K,
            ["fingerprint"] = JsonSerializer.SerializeToNode(ParityGate.Fingerprint(ParityCorpus.Folder)),
            ["machine"] = $"{Environment.ProcessorCount} logical processors, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            ["variants"] = new JsonArray([.. variants.Select(v => (JsonNode)new JsonObject
            {
                ["name"] = v.Name, ["model"] = v.Label, ["thresholds"] = v.Thresholds, ["how"] = v.How,
            })]),
            ["models"] = costs,
            ["runs"] = runs,
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "embedding-benchmark.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var markdown = EmbeddingBenchmark.Markdown(report);
        await File.WriteAllTextAsync(Path.Combine(folder, "embedding-benchmark.md"), markdown);
        output.WriteLine($"Embedding benchmark: {folder}");
        output.WriteLine(markdown);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    async Task<JsonObject> Measure(BenchVariant variant, ParityMode mode, string modelFolder, ParityCorpus corpus, ParityGolden golden, string folder, List<string> problems)
    {
        var server = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!;
        var name = "rhythm_bench_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        try
        {
            var modeName = ParityReport.ModeName(mode);
            var started = Stopwatch.GetTimestamp();
            await using var load = await ParityLoader.Load(source, corpus, modelFolder, mode, model: variant.Model, instruction: variant.Instruction,
                hostSettings: EmbeddingBenchmark.Settings(variant));
            var loadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal("complete", load.RefreshResult);
            Assert.True(load.Embeddings.Status == "on", $"{variant.Name}: the {variant.Model.Name} model is {load.Embeddings.Status}");
            var evaluator = new ParityEvaluator(load, golden);
            await evaluator.WarmUp(default);
            var results = new List<(ParityQuery Query, ParityPath Path, ParityRun Run)>();
            foreach (var q in golden.Queries)
                foreach (var path in evaluator.Paths)
                    results.Add((q, path, await evaluator.Run(path, q, default)));
            foreach (var r in results.Where(r => r.Run.Status == "error"))
                problems.Add($"{variant.Name} {modeName}: {r.Query.Id} on {r.Path.Name}: {r.Run.Error}");

            var clusters = ParityGate.Clusters(golden);
            var fingerprint = ParityGate.Fingerprint(ParityCorpus.Folder);
            var lockFile = ParityGate.LockFile(modeName);
            var locked = ParityGate.ReadLock(await File.ReadAllTextAsync(Path.Combine(ParityCorpus.Folder, lockFile)));
            var paths = new JsonObject();
            var judged = new List<(GatePath Path, GateResult Result, GateDiff Diff)>();
            foreach (var path in evaluator.Paths)
            {
                var mine = results.Where(r => r.Path == path).Select(r => (r.Query, r.Run)).ToArray();
                var fresh = ParityGate.FromRuns(path, mine, clusters);
                var candidate = new GateRun(modeName, golden.K, fingerprint, load.Model, fresh);
                var baseline = locked.Run(path.Name) ?? ParityGate.NoBaseline(candidate, $"no {path.Name} path in {lockFile}");
                var result = ParityGate.Evaluate(baseline, candidate, new GateOptions(ChangesModel: load.Model != locked.Model));
                var diff = ParityGate.Diff(baseline, candidate);
                judged.Add((fresh, result, diff));
                // MiniLM with today's thresholds must answer as the locks hold: otherwise this is not the gate's measurement.
                if (variant.Name == "minilm" && diff.Moved)
                    problems.Add($"minilm {modeName} {path.Name}: the answers moved from {lockFile} ({string.Join(", ", diff.Changed.Take(10))}), so the benchmark does not reproduce the gate's run");
                paths[path.Name] = new JsonObject
                {
                    ["metrics"] = EmbeddingBenchmark.Metrics(mine),
                    ["gate"] = new JsonObject
                    {
                        ["verdict"] = result.Verdict, ["moved"] = diff.Moved, ["changed"] = diff.Changed.Count,
                        ["rules"] = JsonSerializer.SerializeToNode(result.Rules, ParityGate.Json),
                        ["deltas"] = JsonSerializer.SerializeToNode(result.Deltas.Where(d => d.Scope.StartsWith("overall", StringComparison.Ordinal)), ParityGate.Json),
                        ["losers"] = result.Losers.Count,
                    },
                };
            }
            var gate = ParityGateReport.Json(modeName, golden.K, fingerprint, load.Model, locked, new GateOptions(ChangesModel: load.Model != locked.Model), judged);
            await File.WriteAllTextAsync(Path.Combine(folder, $"embedding-benchmark-gate-{variant.Name}-{modeName}.md"), ParityGateReport.Markdown(gate));

            JsonObject disk;
            await using (var cmd = source.CreateCommand("""
                SELECT count(*)::int, coalesce(sum(pg_column_size(embedding)),0)::bigint, pg_total_relation_size('resource_chunks')::bigint
                FROM resource_chunks WHERE model=@model
                """))
            {
                cmd.Parameters.AddWithValue("model", variant.Model.Id);
                await using var reader = await cmd.ExecuteReaderAsync();
                await reader.ReadAsync();
                disk = new JsonObject { ["passages"] = reader.GetInt32(0), ["vectorBytes"] = reader.GetInt64(1), ["tableBytes"] = reader.GetInt64(2) };
            }
            output.WriteLine($"{variant.Name} {modeName}: " + string.Join("; ", judged.Select(j => $"{j.Path.Name} {j.Result.Verdict}")));
            return new JsonObject
            {
                ["variant"] = variant.Name, ["mode"] = modeName, ["model"] = load.Model, ["loadMs"] = Math.Round(loadMs, 1),
                ["refresh"] = load.RefreshResult, ["indexed"] = new JsonObject { ["rows"] = load.Indexed.Rows, ["passages"] = load.Indexed.Passages },
                ["disk"] = disk, ["paths"] = paths,
            };
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
