using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// Opt-in, like the evaluation it builds on, never part of an ordinary run: the golden queries through today's retrieval
// paths in each load mode, each path judged by the gate (ParityGate) against the locked baseline checked in beside the
// corpus (gate-baseline-copies.json, gate-baseline-installed.json):
//   RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<the pinned MiniLM files> sh engine/test-db.sh --filter ParityGateRun
// RHYTHM_PARITY_MODE and RHYTHM_PARITY_OUT as the evaluation; the report is parity-gate.json and .md (copies) and
// parity-gate-installed.json and .md. RHYTHM_PARITY_GATE_TARGET names the golden types a candidate claims to help
// (comma separated), RHYTHM_PARITY_GATE_MODEL_CHANGE=1 says it changes the embedding model on purpose.
// Comparisons judge one path of this run against another of the same run, by the same rules: combined retrieval
// against each arm alone (combined against fulltext, and against semantic), always, and any pair
// RHYTHM_PARITY_GATE_COMPARE lists ("candidate=baseline", comma separated; the variants RHYTHM_PARITY_RECALL adds, for
// example). Their verdicts are recorded in the report, for the decision they inform (whether a path could replace
// another, or a variant become the default); they never fail the run, which the lock comparison and its rules decide.
// The run fails when a path errors, when any path's verdict is FAIL, and when the answers moved from the lock (the
// tripwire): a ranking change is a recorded decision. To record one, rerun with RHYTHM_PARITY_LOCK_REASON="<why, with
// the verdict>": the run then writes gate-baseline-<mode>.json into the report folder instead of failing on the move,
// and the file is copied over the checked-in one in the same commit as the change.
// The locked baselines were taken with the pinned MiniLM model on, which the semantic and graph paths depend on: without
// the model files the run is skipped, saying so, as model tests are (D-041). RHYTHM_PARITY_MODEL=bge-small (with its files
// in RHYTHM_MODEL_DIR_BGE_SMALL) and RHYTHM_PARITY_QUERY_INSTRUCTION judge another model against those locks
// (ParityModelChoice): comparable only with RHYTHM_PARITY_GATE_MODEL_CHANGE=1, and its answers move from the locks by
// design, which the tripwire reports (SB-P2-06; the embedding benchmark records the same verdicts without failing).
public sealed class ParityGateFactAttribute : FactAttribute
{
    public ParityGateFactAttribute()
    {
        // The opt-in first: RHYTHM_PARITY_MODEL and RHYTHM_PARITY_QUERY_INSTRUCTION are read only by a run that opted in,
        // so a value left in a shell never turns an ordinary test run's gate into an error; in an opted-in run a wrong one
        // is an error, as it should be.
        if (Environment.GetEnvironmentVariable("RHYTHM_PARITY_EVAL") != "1")
        {
            Skip = "NOT RUN: opt-in retrieval gate; RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<the pinned MiniLM files> sh engine/test-db.sh --filter ParityGateRun (docs/dev/second-brain-parity/baseline.md).";
            return;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
        {
            Skip = "NOT RUN: the retrieval gate needs a disposable Postgres with pgvector; run it through sh engine/test-db.sh.";
            return;
        }
        var choice = ParityModelChoice.FromEnvironment();
        var models = choice.Directory;
        if (string.IsNullOrWhiteSpace(models) || !choice.Spec.Files.All(f => File.Exists(Path.Combine(models, f.Name))))
            Skip = choice.Spec == EmbeddingModels.MiniLm
                ? "NOT RUN: the retrieval gate compares against baselines locked with the pinned MiniLM model; set RHYTHM_MODEL_DIR to the folder with model_quantized.onnx and vocab.txt (D-041)."
                : $"NOT RUN: RHYTHM_PARITY_MODEL is {choice.Spec.Name}; set {choice.Spec.Variable} to the folder with its {string.Join(" and ", choice.Spec.Files.Select(f => f.Name))} (D-041).";
        else if (choice.Map is { } map && (choice.MapDirectory is not { } folder || !map.Files.All(f => File.Exists(Path.Combine(folder, f.Name)))))
            Skip = $"NOT RUN: RHYTHM_PARITY_MAP_MODEL is {map.Name}; set {map.Variable} to the folder with its {string.Join(" and ", map.Files.Select(f => f.Name))} (D-041).";
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class ParityGateRun(ITestOutputHelper output)
{
    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [ParityGateFact] public async Task Retrieval_gate_against_the_locked_baseline()
    {
        var modes = Env("RHYTHM_PARITY_MODE") switch
        {
            "copies" => new[] { ParityMode.Copies },
            "installed" => [ParityMode.Installed],
            null or "both" => [ParityMode.Copies, ParityMode.Installed],
            var other => throw new ArgumentException($"RHYTHM_PARITY_MODE is copies, installed or both, not {other}"),
        };
        var golden = ParityGolden.Load();
        var targets = (Env("RHYTHM_PARITY_GATE_TARGET") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.FirstOrDefault(t => !golden.Types.ContainsKey(t)) is { } unknown)
            throw new ArgumentException($"RHYTHM_PARITY_GATE_TARGET names {unknown}, which is not a golden query type");
        var options = new GateOptions(TargetTypes: targets, ChangesModel: Env("RHYTHM_PARITY_GATE_MODEL_CHANGE") == "1");
        var problems = new List<string>();
        foreach (var mode in modes) problems.AddRange(await Judge(mode, options, Env("RHYTHM_PARITY_LOCK_REASON")));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    async Task<IReadOnlyList<string>> Judge(ParityMode mode, GateOptions options, string? reason)
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
            var clusters = ParityGate.Clusters(golden);
            var fingerprint = ParityGate.Fingerprint(ParityCorpus.Folder);
            var choice = ParityModelChoice.FromEnvironment();
            await using var load = await ParityLoader.Load(source, corpus, choice.Directory, mode, model: choice.Spec, instruction: choice.Instruction,
                mapModel: choice.Map, mapDirectory: choice.MapDirectory, mapMaxLinks: choice.MaxLinks);
            Assert.Equal("complete", load.RefreshResult);
            // A map model named must be the one the map's links came from, not a fallback (D-256).
            Assert.True(choice.Map is null || load.MapModel == choice.Map, $"the map model {choice.Map?.Name} did not load: {string.Join("; ", load.RefreshNotes)}");
            var evaluator = new ParityEvaluator(load, golden);
            await evaluator.WarmUp(default);
            var runs = new List<(ParityQuery Query, ParityPath Path, ParityRun Run)>();
            foreach (var q in golden.Queries)
                foreach (var path in evaluator.Paths)
                    runs.Add((q, path, await evaluator.Run(path, q, default)));

            var modeName = ParityReport.ModeName(mode);
            var model = load.Model;
            var fresh = evaluator.Paths.Select(p => ParityGate.FromRuns(p, [.. runs.Where(r => r.Path == p).Select(r => (r.Query, r.Run))], clusters)).ToArray();
            var lockFile = ParityGate.LockFile(modeName);
            var lockPath = Path.Combine(ParityCorpus.Folder, lockFile);
            var locked = File.Exists(lockPath) ? ParityGate.ReadLock(await File.ReadAllTextAsync(lockPath)) : null;
            var judged = fresh.Select(p =>
            {
                var candidate = new GateRun(modeName, golden.K, fingerprint, model, p);
                var baseline = locked?.Run(p.Name)
                    ?? ParityGate.NoBaseline(candidate, locked is null ? $"no {lockFile} checked in" : $"no {p.Name} path in {lockFile}");
                return (Path: p, Result: ParityGate.Evaluate(baseline, candidate, options), Diff: ParityGate.Diff(baseline, candidate));
            }).ToArray();
            var compared = ParityGate.Pairs(Env("RHYTHM_PARITY_GATE_COMPARE"), [.. fresh.Select(p => p.Name)]).Select(pair =>
            {
                var (candidate, baseline) = (fresh.First(p => p.Name == pair.Candidate), fresh.First(p => p.Name == pair.Baseline));
                // D-029: a reranker's candidate has 800 ms, the threshold the run that adds one sets (D-249).
                var judging = ParityRecall.Reranks(pair.Candidate)
                    ? options with { Thresholds = (options.Thresholds ?? GateThresholds.Default) with { LatencyMsP95 = 800 } } : options;
                return (pair.Candidate, pair.Baseline, Result: ParityGate.Evaluate(new GateRun(modeName, golden.K, fingerprint, model, baseline),
                    new GateRun(modeName, golden.K, fingerprint, model, candidate), judging));
            }).ToArray();

            var folder = Env("RHYTHM_PARITY_OUT") ?? Path.Combine(AppContext.BaseDirectory, "parity-report");
            Directory.CreateDirectory(folder);
            // Each path's numbers as the embedding benchmark reports them (recall at 5 and 10, MRR and nDCG at 10 over its
            // judged queries, p50 and p95 latency), beside the gate's own: information, never a rule.
            var metrics = evaluator.Paths.ToDictionary(p => p.Name, p => EmbeddingBenchmark.Metrics([.. runs.Where(r => r.Path == p).Select(r => (r.Query, r.Run))]));
            var report = ParityGateReport.Json(modeName, golden.K, fingerprint, model, locked, options, judged, compared, metrics);
            // The map the run was judged on: its model (graph.similarity.model, D-256; none: the live model's), the
            // rebuild's time and the links it drew. Information, never a rule.
            report["map"] = new JsonObject
            {
                ["model"] = load.MapModel?.Id, ["maxLinks"] = choice.MaxLinks, ["refreshMs"] = Math.Round(load.RefreshMs, 1),
                ["similar"] = load.MapLinks.Similar, ["mutual"] = load.MapLinks.Mutual, ["copies"] = load.MapLinks.Copies,
            };
            var stem = mode == ParityMode.Copies ? "parity-gate" : "parity-gate-installed";
            await File.WriteAllTextAsync(Path.Combine(folder, stem + ".json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var markdown = ParityGateReport.Markdown(report)
                + $"\nMap: {load.MapModel?.Name ?? "the live model's vectors"}{(choice.MaxLinks is { } cap ? $", at most {cap} similar links a star" : "")}, "
                + $"rebuild {load.RefreshMs:0} ms, {load.MapLinks.Similar} similar links "
                + $"({load.MapLinks.Mutual} by the mutual rule), {load.MapLinks.Copies} copies.\n";
            await File.WriteAllTextAsync(Path.Combine(folder, stem + ".md"), markdown);
            output.WriteLine($"Parity gate ({modeName}): {folder}");
            output.WriteLine(markdown);

            var problems = new List<string>();
            if (runs.Count != golden.Queries.Length * evaluator.Paths.Count) problems.Add($"{modeName}: {runs.Count} runs for {golden.Queries.Length} queries on {evaluator.Paths.Count} paths");
            foreach (var r in runs.Where(r => r.Run.Status == "error")) problems.Add($"{modeName}: {r.Query.Id} on {r.Path.Name}: {r.Run.Error}");
            // A path that answers exactly as its lock is no candidate: with power its verdict is FAIL for want of a gain,
            // which says nothing is wrong. Every other failed rule counts, moved or not (errors, privacy, latency).
            static bool OnlyNoGain(GateResult r) => r.Rules.Where(x => x.Status == "fail").All(x => x.Name == "improvement");
            foreach (var j in judged.Where(j => j.Result.Verdict == "FAIL" && (j.Diff.Moved || !OnlyNoGain(j.Result))))
                problems.Add($"{modeName} {j.Path.Name}: FAIL, " + string.Join("; ", j.Result.Rules.Where(r => r.Status == "fail").Select(r => $"{r.Name}: {r.Detail}")));
            if (reason is null)
            {
                const string how = "if intended, rerun with RHYTHM_PARITY_LOCK_REASON=\"<why, with the gate's verdict>\" and commit the gate-baseline file it writes to the report folder";
                if (locked is null) problems.Add($"{modeName}: no locked baseline ({lockFile}); {how}");
                else foreach (var j in judged.Where(j => j.Diff.Moved))
                        problems.Add($"{modeName} {j.Path.Name}: the answers moved from {lockFile}" + (j.Diff.FingerprintChanged ? " (the corpus or golden queries changed)" : "")
                            + (j.Diff.ModelChanged ? $" (embedding model {locked.Model ?? "none"} to {model ?? $"none: {load.Embeddings.Status}"})" : "")
                            + (j.Diff.Changed.Count > 0 ? $"; changed: {string.Join(", ", j.Diff.Changed)}" : "")
                            + (j.Diff.Missing.Count + j.Diff.Extra.Count > 0 ? $"; missing {j.Diff.Missing.Count}, new {j.Diff.Extra.Count}" : "") + $"; {how}");
            }
            else if (choice.LockRefusal() is { } refused) problems.Add($"{modeName}: not locked: {refused}");
            else if (ParityGate.LockRefusals(fresh, locked, fingerprint) is { Count: > 0 } refusals)
                problems.AddRange(refusals.Select(r => $"{modeName}: not locked: {r}"));
            else
            {
                var written = Path.Combine(folder, lockFile);
                var next = ParityGate.NewLock(modeName, golden.K, fingerprint, model, fresh, reason, DateOnly.FromDateTime(DateTime.UtcNow), locked);
                await File.WriteAllTextAsync(written, ParityGate.WriteLock(next));
                output.WriteLine($"Locked baseline for {modeName} written to {written}; copy it to engine/tests/Knowledge.Tests/Parity/{lockFile}.");
            }
            return problems;
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

// The gate's report: JSON for machines and Markdown for people, from the same results.
public static class ParityGateReport
{
    static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, ParityGate.Json);
    static string N(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
    static string Signed(double x) => (x >= 0 ? "+" : "") + x.ToString("0.000", CultureInfo.InvariantCulture);

    public static JsonObject Json(string mode, int k, IReadOnlyDictionary<string, string> fingerprint, string? model, GateLock? locked, GateOptions options,
        IReadOnlyList<(GatePath Path, GateResult Result, GateDiff Diff)> judged,
        IReadOnlyList<(string Candidate, string Baseline, GateResult Result)>? compared = null,
        IReadOnlyDictionary<string, JsonObject>? metrics = null) => new()
    {
        ["schema"] = "rhythm-parity-gate.v1",
        ["generatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ["label"] = "DEMO: synthetic corpus only",
        ["mode"] = mode, ["k"] = k, ["fingerprint"] = Node(fingerprint), ["model"] = model,
        ["baseline"] = locked is null ? null : new JsonObject
        {
            ["file"] = ParityGate.LockFile(mode), ["model"] = locked.Model, ["fingerprint"] = Node(locked.Fingerprint),
            ["lockedAt"] = locked.History.LastOrDefault()?.Date, ["reason"] = locked.History.LastOrDefault()?.Reason, ["locks"] = locked.History.Length,
        },
        ["thresholds"] = Node(options.Thresholds ?? GateThresholds.Default),
        ["targetTypes"] = Node(options.TargetTypes ?? []), ["changesModel"] = options.ChangesModel,
        ["paths"] = new JsonArray([.. judged.Select(j => (JsonNode)new JsonObject
        {
            ["name"] = j.Path.Name, ["tool"] = j.Path.Tool, ["verdict"] = j.Result.Verdict, ["informational"] = j.Result.Informational,
            ["judged"] = j.Result.Judged, ["clusters"] = j.Result.Clusters, ["latencyMsP95"] = j.Path.LatencyMsP95,
            ["metrics"] = metrics?.GetValueOrDefault(j.Path.Name)?.DeepClone(),
            ["summary"] = Node(ParityGate.Summary(j.Path)),
            ["rules"] = Node(j.Result.Rules), ["mde"] = Node(j.Result.Mde), ["deltas"] = Node(j.Result.Deltas), ["losers"] = Node(j.Result.Losers),
            ["lock"] = new JsonObject
            {
                ["moved"] = j.Diff.Moved, ["fingerprintChanged"] = j.Diff.FingerprintChanged, ["modelChanged"] = j.Diff.ModelChanged,
                ["changed"] = Node(j.Diff.Changed), ["missing"] = Node(j.Diff.Missing), ["extra"] = Node(j.Diff.Extra),
            },
        })]),
        ["comparisons"] = new JsonArray([.. (compared ?? []).Select(c => (JsonNode)new JsonObject
        {
            ["candidate"] = c.Candidate, ["baseline"] = c.Baseline, ["verdict"] = c.Result.Verdict, ["informational"] = c.Result.Informational,
            ["judged"] = c.Result.Judged, ["clusters"] = c.Result.Clusters,
            ["rules"] = Node(c.Result.Rules), ["deltas"] = Node(c.Result.Deltas), ["losers"] = Node(c.Result.Losers),
        })]),
    };

    public static string Markdown(JsonObject report)
    {
        var md = new StringBuilder();
        md.AppendLine("# Parity retrieval gate").AppendLine();
        md.AppendLine($"Generated {report["generatedAt"]} from the synthetic corpus (DEMO data only). Load mode: **{report["mode"]}**, k = {report["k"]}, "
            + $"embeddings: {report["model"]?.ToString() ?? "off"}.").AppendLine();
        md.AppendLine(report["baseline"] is { } b
            ? $"Baseline: `{b["file"]}`, locked {b["lockedAt"]} ({b["locks"]} lock(s) so far): {b["reason"]}"
            : "Baseline: none checked in for this load mode.").AppendLine();
        var t = report["thresholds"]!;
        md.AppendLine($"Rules (D-028, D-029, D-056): a verdict needs {t["minQueries"]} judged queries in {t["minClusters"]} clusters and {t["minPerType"]} per type, "
            + $"though below that a clear regression (a loss of {t["tolerance"]} with its whole interval below 0) still fails; "
            + $"no metric loses {t["tolerance"]} overall or in a type; a gain of {t["overallGain"]} overall or {t["targetGain"]} in a target type with a "
            + $"{t["iterations"]}-draw paired cluster bootstrap interval above 0 (seed {t["seed"]}); p95 within {t["latencyMsP95"]} ms; any must-hide record "
            + "the candidate reveals beyond the baseline, or on a check its path claims, fails.").AppendLine();

        md.AppendLine("| Path | Verdict | Judged queries | Clusters | p95 ms | Answers against the lock |").AppendLine("|---|---|---|---|---|---|");
        foreach (var p in report["paths"]!.AsArray())
        {
            var l = p!["lock"]!;
            md.AppendLine($"| {p["name"]} | **{p["verdict"]}** | {p["judged"]} | {p["clusters"]} | {p["latencyMsP95"]?.ToString() ?? "–"} | "
                + (l["moved"]!.GetValue<bool>() ? $"moved ({l["changed"]!.AsArray().Count} changed, {l["missing"]!.AsArray().Count} missing, {l["extra"]!.AsArray().Count} new"
                    + (l["fingerprintChanged"]!.GetValue<bool>() ? ", data changed" : "") + (l["modelChanged"]!.GetValue<bool>() ? ", model changed" : "") + ")" : "unchanged") + " |");
        }
        md.AppendLine();
        var unchanged = report["paths"]!.AsArray().Where(p => p!["verdict"]!.GetValue<string>() == "FAIL" && !p["lock"]!["moved"]!.GetValue<bool>()
            && p["rules"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() == "fail").All(r => r!["name"]!.GetValue<string>() == "improvement"))
            .Select(p => p!["name"]!.GetValue<string>()).ToArray();
        if (unchanged.Length > 0)
            md.AppendLine($"Unchanged from the lock, so not a candidate: {string.Join(", ", unchanged)}. Their FAIL is for want of a gain, which a path "
                + "answering exactly as its lock cannot show; the run does not count it (D-056).").AppendLine();

        foreach (var p in report["paths"]!.AsArray())
        {
            md.AppendLine($"## {p!["name"]}: {p["verdict"]}").AppendLine();
            md.AppendLine("| Rule | Status | Detail |").AppendLine("|---|---|---|");
            foreach (var r in p["rules"]!.AsArray()) md.AppendLine($"| {r!["name"]} | {r["status"]} | {r["detail"]} |");
            md.AppendLine();
            var deltas = p["deltas"]!.AsArray();
            if (deltas.Count > 0)
            {
                md.AppendLine(p["informational"]!.GetValue<bool>()
                    ? "Deltas, for information only: the set is below the power floor, so none of them is evidence."
                    : "Deltas (candidate minus baseline) with their 95% paired cluster bootstrap intervals.").AppendLine();
                md.AppendLine("| Scope | Metric | Baseline | Candidate | Delta | Interval | Queries | Clusters |").AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var d in deltas)
                {
                    var i = d!["interval"]!;
                    md.AppendLine($"| {d["scope"]} | {d["metric"]} | {N(d["baseline"]!.GetValue<double>())} | {N(d["candidate"]!.GetValue<double>())} | "
                        + $"{Signed(i["mean"]!.GetValue<double>())} | [{Signed(i["lo"]!.GetValue<double>())}, {Signed(i["hi"]!.GetValue<double>())}] | {i["n"]} | {i["clusters"]} |");
                }
                md.AppendLine();
                md.AppendLine("Minimum detectable effect at 80% power: " + string.Join(", ", p["mde"]!.AsObject().Select(m => $"{m.Key} {m.Value}")) + ".").AppendLine();
            }
            var losers = p["losers"]!.AsArray();
            if (losers.Count > 0)
            {
                md.AppendLine($"Queries that lost (outside the verdict: a mean can hide a loser behind a winner): {losers.Count}.").AppendLine();
                foreach (var l in losers.Take(10)) md.AppendLine($"- `{l!["id"]}` ({l["type"]}): {l["detail"]}");
                md.AppendLine();
            }
            var changed = p["lock"]!["changed"]!.AsArray();
            if (changed.Count > 0) md.AppendLine("Answers that moved from the lock: " + string.Join(", ", changed.Select(c => $"`{c}`")) + ".").AppendLine();
        }
        if (report["paths"]!.AsArray().Any(p => p!["metrics"] is not null))
        {
            md.AppendLine("## Each path's numbers").AppendLine();
            md.AppendLine("Over each path's judged queries (a relevant answer, no capability the path lacks), latency over every measured query "
                + "(each query's median of three calls): information beside the rules, as the embedding benchmark reports them.").AppendLine();
            md.AppendLine("| Path | Judged | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 ms | p95 ms |").AppendLine("|---|---|---|---|---|---|---|---|");
            static string F(JsonNode? n, string format) => n is null ? "–"
                : double.Parse(n.ToJsonString(), CultureInfo.InvariantCulture).ToString(format, CultureInfo.InvariantCulture);
            foreach (var p in report["paths"]!.AsArray().Where(p => p!["metrics"] is not null))
            {
                var m = p!["metrics"]!;
                md.AppendLine($"| {p["name"]} | {m["judged"]} | {F(m["recall@5"], "0.000")} | {F(m["recall@10"], "0.000")} | {F(m["mrr@10"], "0.000")} | "
                    + $"{F(m["ndcg@10"], "0.000")} | {F(m["latencyMsP50"], "0.0")} | {F(m["latencyMsP95"], "0.0")} |");
            }
            md.AppendLine();
        }
        if (report["comparisons"] is JsonArray comparisons && comparisons.Count > 0)
        {
            md.AppendLine("## Comparisons within this run").AppendLine();
            md.AppendLine("One path judged against another of the same run by the same rules: recorded for the decision each informs, never "
                + "failing the run.").AppendLine();
            md.AppendLine("| Candidate | Baseline | Verdict | Failed or inconclusive rules |").AppendLine("|---|---|---|---|");
            foreach (var c in comparisons)
                md.AppendLine($"| {c!["candidate"]} | {c["baseline"]} | **{c["verdict"]}** | "
                    + string.Join("; ", c["rules"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() is "fail" or "inconclusive").Select(r => $"{r!["name"]}: {r["detail"]}")) + " |");
            md.AppendLine();
            foreach (var c in comparisons)
            {
                md.AppendLine($"### {c!["candidate"]} against {c["baseline"]}: {c["verdict"]}").AppendLine();
                md.AppendLine("| Scope | Metric | Baseline | Candidate | Delta | Interval |").AppendLine("|---|---|---|---|---|---|");
                foreach (var d in c["deltas"]!.AsArray())
                {
                    var i = d!["interval"]!;
                    md.AppendLine($"| {d["scope"]} | {d["metric"]} | {N(d["baseline"]!.GetValue<double>())} | {N(d["candidate"]!.GetValue<double>())} | "
                        + $"{Signed(i["mean"]!.GetValue<double>())} | [{Signed(i["lo"]!.GetValue<double>())}, {Signed(i["hi"]!.GetValue<double>())}] |");
                }
                md.AppendLine();
            }
        }
        return md.ToString();
    }
}
