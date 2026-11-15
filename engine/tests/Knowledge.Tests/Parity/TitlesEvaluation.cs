using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Titles;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// Opt-in: the titles model against D-027's rule (SB-P2-14, docs/dev/second-brain-parity/titles-eval.md), never part
// of an ordinary run. engine/titles-eval.sh starts the titles service's own Ollama image in a compose project of its
// own, a fresh server per model, and runs this with:
//   RHYTHM_TITLES_EVAL=1             turns it on
//   RHYTHM_TITLES_EVAL_URL           the server (default http://localhost:11534/; the adapter's own URL rule applies)
//   RHYTHM_TITLES_EVAL_MODELS        models to run now, comma-separated; empty only rebuilds the report
//   RHYTHM_TITLES_EVAL_REPEATS       passes over the set per model and input (default 3); quality is scored on the first
//   RHYTHM_TITLES_EVAL_OUT           the report folder (default titles-report beside the tests)
// Every title comes from the real TitlesAdapter (its prompt, options, 30 s timeout and cleaning), given what the core
// gives it today (the star's excerpt) and, for comparison, the whole text. The report covers every run in the folder,
// with the memory the script measured for each model (titles-memory-<model>.json) and the reviewer's judgements
// (titles-review.json).
public sealed class TitlesEvalFactAttribute : FactAttribute
{
    public TitlesEvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_TITLES_EVAL") != "1")
            Skip = "NOT RUN: opt-in titles-model evaluation; sh engine/titles-eval.sh <model> ... (docs/dev/second-brain-parity/titles-eval.md).";
    }
}

public sealed record TitlesCall(string Item, string Input, int Repeat, string? Title, double Ms, string? Error);
// Cold starts: the first title on a fresh server (Cold), and again once the model is unloaded after the set (Reload), as
// a rebuild meets it whenever the keep-alive has lapsed. Each is the adapter's own call (30 s timeout); when it fails,
// Wait is how much longer the model then took to load (the server gives up a load whose caller has gone, so this is a
// whole load of its own).
public sealed record TitlesRun(string Model, string Server, DateTimeOffset Started, string? Ollama, JsonNode? Details, string? License,
    JsonNode? Loaded, double ColdMs, string? ColdError, int Repeats, TitlesCall[] Calls, double? ColdWaitMs = null,
    double? ReloadMs = null, string? ReloadError = null, double? ReloadWaitMs = null);
// Written by engine/titles-eval.sh from the server container's cgroup: the peak covers loading and every call; anon and
// file are memory.stat's split at the end of the run, with the model still loaded.
public sealed record TitlesMemory(string Model, long IdleBytes, long PeakBytes, long CurrentBytes, long? LimitBytes, string? Stats, int? Cpus,
    long? AnonBytes = null, long? FileBytes = null);

public sealed class TitlesEvaluation(ITestOutputHelper output)
{
    public static readonly string[] Inputs = ["excerpt", "text"];
    public static string Input(TitlesItem item, string input) => input == "excerpt" ? TitlesRubric.Excerpt(item) : item.Text;
    public static string Slug(string model) => new(model.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray());
    static readonly JsonSerializerOptions Indented = new(ParityCorpus.Json) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [TitlesEvalFact] public async Task Titles_model_candidates_against_the_rule_D027()
    {
        var set = TitlesSet.Load();
        var url = Environment.GetEnvironmentVariable("RHYTHM_TITLES_EVAL_URL") is { Length: > 0 } u ? u : "http://localhost:11534/";
        var models = (Environment.GetEnvironmentVariable("RHYTHM_TITLES_EVAL_MODELS") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("RHYTHM_TITLES_EVAL_REPEATS"), out var r) && r is >= 1 and <= 10 ? r : 3;
        var folder = Environment.GetEnvironmentVariable("RHYTHM_TITLES_EVAL_OUT") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "titles-report");
        Directory.CreateDirectory(folder);
        foreach (var model in models)
        {
            var run = await Run(set, url, model, repeats);
            await File.WriteAllTextAsync(Path.Combine(folder, $"titles-run-{Slug(model)}.json"), JsonSerializer.Serialize(run, Indented));
            output.WriteLine($"{model}: {run.Calls.Length} calls, {run.Calls.Count(c => c.Title is null)} without a title, cold start {run.ColdMs:0} ms");
            Assert.Equal(set.Items.Length * Inputs.Length * repeats, run.Calls.Length);
        }
        var runs = Directory.GetFiles(folder, "titles-run-*.json").Order(StringComparer.Ordinal)
            .Select(p => JsonSerializer.Deserialize<TitlesRun>(File.ReadAllText(p), ParityCorpus.Json)!).ToArray();
        var memory = Directory.GetFiles(folder, "titles-memory-*.json")
            .Select(p => JsonSerializer.Deserialize<TitlesMemory>(File.ReadAllText(p), ParityCorpus.Json)!).ToDictionary(m => m.Model);
        var report = TitlesReport.Json(set, runs, memory, TitlesReview.Load());
        await File.WriteAllTextAsync(Path.Combine(folder, "titles-eval.json"), report.ToJsonString(Indented));
        var markdown = TitlesReport.Markdown(report);
        await File.WriteAllTextAsync(Path.Combine(folder, "titles-eval.md"), markdown);
        output.WriteLine($"Titles report: {folder}");
        output.WriteLine(markdown);
        Assert.NotEmpty(runs);
    }

    static async Task<TitlesRun> Run(TitlesSet set, string url, string model, int repeats)
    {
        var titles = new TitlesAdapter();
        titles.Configure(new(new AdapterSettings("titles", new Dictionary<string, object?> { ["enabled"] = "true", ["provider"] = "ollama", ["model"] = model, ["url"] = url }),
            TestSettings.Use(), new Database(NpgsqlDataSource.Create("Host=example.invalid;Database=none")), new Archive(Path.GetTempPath())));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        var started = DateTimeOffset.UtcNow;
        var version = (await Read(http, "api/version", null))?["version"]?.GetValue<string>();
        var show = await Read(http, "api/show", new { model });
        var license = show?["license"]?.GetValue<string>() is { } text ? text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
        // A fresh server: this first call loads the model, so it is timed apart from the set, and the set waits until
        // the model is loaded.
        var (_, coldMs, coldError) = await Timed(titles, Warm);
        var coldWait = await Load(http, model);
        var calls = new List<TitlesCall>();
        for (var repeat = 1; repeat <= repeats; repeat++)
            foreach (var input in Inputs)
                foreach (var item in set.Items)
                {
                    var (title, ms, error) = await Timed(titles, Input(item, input));
                    calls.Add(new(item.Id, input, repeat, title, ms, error));
                }
        var loaded = await Read(http, "api/ps", null);
        // Unloaded, as after the keep-alive, then asked again.
        await Read(http, "api/generate", new { model, keep_alive = 0 });
        for (var i = 0; i < 120 && (await Read(http, "api/ps", null))?["models"]?.AsArray().Count > 0; i++) await Task.Delay(500);
        var (_, reloadMs, reloadError) = await Timed(titles, Warm);
        var reloadWait = await Load(http, model);
        return new(model, url, started, version, show?["details"]?.DeepClone(), license, loaded?.DeepClone(), coldMs, coldError, repeats, [.. calls],
            coldError is null ? null : coldWait, reloadMs, reloadError, reloadError is null ? null : reloadWait);
    }

    const string Warm = "DEMO warm-up: water the plants on Sunday.";

    // Waits for the model to be loaded (an empty prompt only loads it), and says how long that took.
    static async Task<double> Load(HttpClient http, string model)
    {
        var started = Stopwatch.GetTimestamp();
        await Read(http, "api/generate", new { model, prompt = "", stream = false, keep_alive = "5m" });
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // One title as the core asks for it; a failure is recorded by kind, as the core would meet it.
    static async Task<(string? Title, double Ms, string? Error)> Timed(TitlesAdapter titles, string text)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var title = await titles.Title(text, CancellationToken.None);
            return (title, Stopwatch.GetElapsedTime(started).TotalMilliseconds, title is null ? "unusable" : null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
        { return (null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, e is TaskCanceledException ? "timeout" : e.GetType().Name); }
    }

    static async Task<JsonNode?> Read(HttpClient http, string path, object? body)
    {
        try
        {
            using var response = body is null ? await http.GetAsync(path) : await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }
}

// The report: JSON (titles-eval.json) and Markdown (titles-eval.md) from the same numbers, with D-027's verdict for
// every candidate: a model given one input.
public static class TitlesReport
{
    // D-027's pre-registered budgets.
    public const double MinimumMean = 3.0, MinimumMargin = 0.5, MaximumP95Ms = 5000, MaximumFailures = 0.05;
    public const long MaximumMemoryBytes = 3_000_000_000;
    // D-250's successor rule (proposed 9 November 2026, before the re-test): judged on the items whose excerpt names nothing
    // (the rubric's "specific" point is 0 on the excerpt), given the whole text; there a mean of MinimumMean and
    // MinimumMargin above the excerpt's; on the rest at most GuardLoss below the excerpt's; a cold load within the adapter's
    // ColdLimitMs; D-027's latency and failure budgets. Memory is reported against MaximumMemoryBytes, not decided here.
    public const double GuardLoss = 0.25, ColdLimitMs = 30_000;
    const int Resamples = 10_000, Seed = 27, TitlesPerPass = 60;
    const double PassBudgetMs = 60_000;

    static double Round(double x, int digits = 3) => Math.Round(x, digits);

    public static JsonObject Json(TitlesSet set, IReadOnlyList<TitlesRun> runs, IReadOnlyDictionary<string, TitlesMemory> memory, TitlesReview review)
    {
        var items = set.Items;
        var baseline = items.Select(i => TitlesRubric.Score(i, TitlesRubric.Excerpt(i), review)).ToArray();
        var baselineMean = baseline.Average(s => s.Points);
        var candidates = new JsonArray();
        var perItem = items.Select((item, n) => new JsonObject
        {
            ["id"] = item.Id, ["lane"] = item.Lane, ["lang"] = item.Lang, ["reference"] = item.Reference,
            ["excerpt"] = TitlesRubric.Excerpt(item), ["excerptPoints"] = baseline[n].Points, ["excerptMissed"] = baseline[n].Missed,
            ["titles"] = new JsonObject(),
        }).ToArray();
        foreach (var run in runs)
            foreach (var input in TitlesEvaluation.Inputs)
            {
                var calls = run.Calls.Where(c => c.Input == input).ToArray();
                if (calls.Length == 0) continue;
                var first = items.Select(i => calls.Single(c => c.Item == i.Id && c.Repeat == 1)).ToArray();
                // What a person sees: a failed or unusable title leaves the excerpt on the star.
                var shown = items.Select((i, n) => first[n].Title ?? TitlesRubric.Excerpt(i)).ToArray();
                var scores = items.Select((i, n) => TitlesRubric.Score(i, shown[n], review)).ToArray();
                var automatic = items.Select((i, n) => TitlesRubric.Score(i, shown[n])).ToArray();
                var strict = items.Select((i, n) => first[n].Title is null ? 0 : scores[n].Points).ToArray();
                var mean = scores.Average(s => s.Points);
                var diffs = scores.Select((s, n) => (double)(s.Points - baseline[n].Points)).ToArray();
                var latencies = calls.Select(c => c.Ms).Order().ToArray();
                var failed = calls.Count(c => c.Title is null);
                var consistent = items.Count(i => calls.Where(c => c.Item == i.Id).Select(c => c.Title).Distinct().Count() == 1);
                var mem = memory.GetValueOrDefault(run.Model);
                var p95 = Percentile(latencies, 0.95);
                var checks = new JsonObject
                {
                    ["mean"] = mean >= MinimumMean, ["margin"] = mean - baselineMean >= MinimumMargin - 1e-9, ["latency"] = p95 <= MaximumP95Ms,
                    ["memory"] = mem is null ? null : mem.PeakBytes <= MaximumMemoryBytes, ["failures"] = (double)failed / calls.Length <= MaximumFailures,
                };
                var adopt = checks.All(c => c.Value?.GetValue<bool>() == true);
                // D-250: only for the whole text, the input it names.
                JsonObject? successor = null;
                if (input == "text")
                {
                    var judged = Enumerable.Range(0, items.Length).Where(n => !baseline[n].Specific).ToArray();
                    var rest = Enumerable.Range(0, items.Length).Where(n => baseline[n].Specific).ToArray();
                    double Mean(IEnumerable<int> ns, Func<int, double> f) => ns.Select(f).Average();
                    var judgedMean = Mean(judged, n => scores[n].Points);
                    var judgedBase = Mean(judged, n => baseline[n].Points);
                    var restMean = Mean(rest, n => scores[n].Points);
                    var restBase = Mean(rest, n => baseline[n].Points);
                    var cold = run.ColdError is null && run.ColdMs <= ColdLimitMs;
                    var successorChecks = new JsonObject
                    {
                        ["mean"] = judgedMean >= MinimumMean, ["margin"] = judgedMean - judgedBase >= MinimumMargin - 1e-9,
                        ["guard"] = restMean - restBase >= -GuardLoss - 1e-9, ["coldLoad"] = cold, ["latency"] = p95 <= MaximumP95Ms,
                        ["failures"] = (double)failed / calls.Length <= MaximumFailures,
                    };
                    successor = new JsonObject
                    {
                        ["judged"] = judged.Length, ["judgedMean"] = Round(judgedMean), ["judgedExcerptMean"] = Round(judgedBase),
                        ["margin"] = Round(judgedMean - judgedBase), ["marginInterval"] = Interval([.. judged.Select(n => diffs[n])]),
                        ["judgedAutomaticMean"] = Round(Mean(judged, n => automatic[n].Points)),
                        ["rest"] = rest.Length, ["restMean"] = Round(restMean), ["restExcerptMean"] = Round(restBase),
                        ["change"] = Round(restMean - restBase), ["changeInterval"] = Interval([.. rest.Select(n => diffs[n])]),
                        ["restAutomaticMean"] = Round(Mean(rest, n => automatic[n].Points)),
                        ["leaks"] = Enumerable.Range(0, items.Length).Count(n => scores[n].Leaked.Count > 0),
                        ["coldMs"] = Round(run.ColdMs, 0), ["coldError"] = run.ColdError,
                        ["memoryWithinDevelopmentBudget"] = mem is null ? null : mem.PeakBytes <= MaximumMemoryBytes,
                        ["checks"] = successorChecks,
                        ["verdict"] = successorChecks.All(c => c.Value?.GetValue<bool>() == true) ? "passes on this machine" : "fails",
                    };
                }
                candidates.Add(new JsonObject
                {
                    ["model"] = run.Model, ["input"] = input, ["ollama"] = run.Ollama, ["details"] = run.Details?.DeepClone(), ["license"] = run.License,
                    ["mean"] = Round(mean), ["automaticMean"] = Round(automatic.Average(s => s.Points)), ["strictMean"] = Round(strict.Average()),
                    ["margin"] = Round(mean - baselineMean), ["marginInterval"] = Interval(diffs),
                    ["criteria"] = Criteria(scores), ["lanes"] = Lanes(items, scores),
                    ["referenceOverlap"] = Round(items.Select((i, n) => TitlesRubric.Overlap(shown[n], i.Reference)).Average()),
                    ["calls"] = calls.Length, ["failed"] = failed, ["failureRate"] = Round((double)failed / calls.Length),
                    ["failures"] = new JsonObject(calls.Where(c => c.Error is not null).GroupBy(c => c.Error!).OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
                    ["latencyMs"] = new JsonObject
                    {
                        ["p50"] = Round(Percentile(latencies, 0.5), 0), ["p95"] = Round(p95, 0), ["max"] = Round(latencies[^1], 0),
                        ["mean"] = Round(latencies.Average(), 0), ["cold"] = Round(run.ColdMs, 0),
                    },
                    ["coldStart"] = new JsonObject
                    {
                        ["firstMs"] = Round(run.ColdMs, 0), ["firstError"] = run.ColdError, ["firstWaitMs"] = Opt(run.ColdWaitMs),
                        ["reloadMs"] = Opt(run.ReloadMs), ["reloadError"] = run.ReloadError, ["reloadWaitMs"] = Opt(run.ReloadWaitMs),
                    },
                    ["titlesPerPass"] = (int)Math.Min(TitlesPerPass, Math.Floor(PassBudgetMs / latencies.Average())),
                    ["consistency"] = Round((double)consistent / items.Length), ["repeats"] = run.Repeats,
                    ["memory"] = mem is null ? null : new JsonObject
                    {
                        ["peakBytes"] = mem.PeakBytes, ["idleBytes"] = mem.IdleBytes, ["currentBytes"] = mem.CurrentBytes,
                        ["limitBytes"] = mem.LimitBytes, ["stats"] = mem.Stats, ["cpus"] = mem.Cpus,
                        ["anonBytes"] = mem.AnonBytes, ["fileBytes"] = mem.FileBytes,
                    },
                    ["loaded"] = run.Loaded?.DeepClone(),
                    ["checks"] = checks, ["verdict"] = adopt ? "adopt" : "keep the current setting",
                    ["successor"] = successor,
                });
                for (var n = 0; n < items.Length; n++)
                    perItem[n]["titles"]![$"{run.Model} | {input}"] = new JsonObject
                    {
                        ["title"] = first[n].Title, ["error"] = first[n].Error, ["points"] = scores[n].Points, ["missed"] = scores[n].Missed,
                        ["unsupported"] = new JsonArray(scores[n].Unsupported.Select(w => (JsonNode?)w).ToArray()),
                        ["leaked"] = new JsonArray(scores[n].Leaked.Select(w => (JsonNode?)w).ToArray()),
                        ["automaticPoints"] = automatic[n].Points,
                    };
            }
        return new JsonObject
        {
            ["rule"] = "D-027", ["generatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["set"] = new JsonObject
            {
                ["label"] = set.Label, ["items"] = items.Length,
                ["lanes"] = new JsonObject(items.GroupBy(i => i.Lane).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
                ["languages"] = new JsonObject(items.GroupBy(i => i.Lang).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
                ["withPrivateDetails"] = items.Count(i => i.Private.Length > 0),
            },
            ["budgets"] = new JsonObject
            {
                ["minimumMean"] = MinimumMean, ["minimumMargin"] = MinimumMargin, ["maximumP95Ms"] = MaximumP95Ms,
                ["maximumMemoryBytes"] = MaximumMemoryBytes, ["maximumFailureRate"] = MaximumFailures,
            },
            ["review"] = new JsonObject { ["judgements"] = review.Judgements.Length, ["note"] = review.Note },
            ["baseline"] = new JsonObject
            {
                ["what"] = "the Second Brain adapter's excerpt (no titles model)", ["mean"] = Round(baselineMean),
                ["criteria"] = Criteria(baseline), ["lanes"] = Lanes(items, baseline),
                ["referenceOverlap"] = Round(items.Select(i => TitlesRubric.Overlap(TitlesRubric.Excerpt(i), i.Reference)).Average()),
            },
            ["candidates"] = candidates, ["items"] = new JsonArray(perItem),
        };
    }

    static JsonNode? Opt(double? ms) => ms is { } v ? JsonValue.Create(Round(v, 0)) : null;
    static double Percentile(double[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];

    // A paired bootstrap over items of the gain against the excerpt, for information: D-027 decides on the mean.
    static JsonArray Interval(double[] diffs)
    {
        var random = new Random(Seed);
        var means = new double[Resamples];
        for (var b = 0; b < Resamples; b++)
        {
            double sum = 0;
            for (var k = 0; k < diffs.Length; k++) sum += diffs[random.Next(diffs.Length)];
            means[b] = sum / diffs.Length;
        }
        Array.Sort(means);
        return [Round(means[(int)(0.025 * Resamples)]), Round(means[(int)(0.975 * Resamples) - 1])];
    }

    static JsonObject Criteria(TitleScore[] scores) => new()
    {
        ["accurate"] = Round(scores.Average(s => s.Accurate ? 1.0 : 0)), ["specific"] = Round(scores.Average(s => s.Specific ? 1.0 : 0)),
        ["short"] = Round(scores.Average(s => s.Short ? 1.0 : 0)), ["private"] = Round(scores.Average(s => s.Discreet ? 1.0 : 0)),
    };

    static JsonObject Lanes(TitlesItem[] items, TitleScore[] scores) => new(items.Select((i, n) => (i.Lane, scores[n].Points)).GroupBy(x => x.Lane)
        .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)Round(g.Average(x => x.Points)))));

    static string Bytes(long? b) => b is { } v ? (v / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : "not measured";
    static string Num(JsonNode? n, string format = "0.00") => n is null ? "-" : n.GetValue<double>().ToString(format, CultureInfo.InvariantCulture);
    static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");

    public static string Markdown(JsonObject report)
    {
        var b = new StringBuilder();
        var baseline = report["baseline"]!;
        var set = report["set"]!;
        b.AppendLine($"# Titles model against D-027 ({report["generatedAt"]})").AppendLine();
        b.AppendLine($"Set: {set["items"]} fictional items ({string.Join(", ", set["lanes"]!.AsObject().Select(l => $"{l.Value} {l.Key}"))}); "
            + $"{set["withPrivateDetails"]} carry private details beyond their first line. Review judgements applied: {report["review"]!["judgements"]}.").AppendLine();
        b.AppendLine("| Candidate | Input | Mean of 4 | vs excerpt | 95% interval | Accurate | Specific | ≤ 80 | Private | Failed | p50 / p95 / cold ms | Peak memory | Verdict |");
        b.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var bc = baseline["criteria"]!;
        b.AppendLine($"| no model (excerpt) | - | {Num(baseline["mean"])} | - | - | {Num(bc["accurate"])} | {Num(bc["specific"])} | {Num(bc["short"])} | {Num(bc["private"])} | - | - | - | current setting |");
        foreach (var c in report["candidates"]!.AsArray())
        {
            var cr = c!["criteria"]!; var l = c["latencyMs"]!; var i = c["marginInterval"]!.AsArray();
            b.AppendLine($"| `{c["model"]}` | {c["input"]} | {Num(c["mean"])} | {Num(c["margin"], "+0.00;-0.00;0.00")} | {Num(i[0], "+0.00;-0.00;0.00")} to {Num(i[1], "+0.00;-0.00;0.00")} "
                + $"| {Num(cr["accurate"])} | {Num(cr["specific"])} | {Num(cr["short"])} | {Num(cr["private"])} | {c["failed"]}/{c["calls"]} "
                + $"| {Num(l["p50"], "0")} / {Num(l["p95"], "0")} / {Num(l["cold"], "0")} | {Bytes(c["memory"]?["peakBytes"]?.GetValue<long>())} | {c["verdict"]} |");
        }
        var successors = report["candidates"]!.AsArray().Where(c => c!["successor"] is not null).ToArray();
        if (successors.Length > 0)
        {
            b.AppendLine().AppendLine("Under D-250's successor rule (the whole text; judged on the items whose excerpt names nothing, guarded on the rest; "
                + $"memory reported against {Bytes(MaximumMemoryBytes)}, not decided here):").AppendLine();
            b.AppendLine("| Candidate | Judged items | Mean there | vs excerpt (95% interval) | Rest | Mean there | Change (95% interval) | Titles leaking | Cold load | p95 | Failed | Peak memory | Verdict |");
            b.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var c in successors)
            {
                var x = c!["successor"]!; var mi = x["marginInterval"]!.AsArray(); var ci = x["changeInterval"]!.AsArray();
                b.AppendLine($"| `{c["model"]}` | {x["judged"]} | {Num(x["judgedMean"])} | {Num(x["margin"], "+0.00;-0.00;0.00")} ({Num(mi[0], "+0.00;-0.00;0.00")} to {Num(mi[1], "+0.00;-0.00;0.00")}) "
                    + $"| {x["rest"]} | {Num(x["restMean"])} | {Num(x["change"], "+0.00;-0.00;0.00")} ({Num(ci[0], "+0.00;-0.00;0.00")} to {Num(ci[1], "+0.00;-0.00;0.00")}) "
                    + $"| {x["leaks"]} | {Num(x["coldMs"], "0")} ms{(x["coldError"] is { } e ? $" ({e})" : "")} | {Num(c["latencyMs"]!["p95"], "0")} ms | {c["failed"]}/{c["calls"]} "
                    + $"| {Bytes(c["memory"]?["peakBytes"]?.GetValue<long>())} | {x["verdict"]} |");
            }
        }
        b.AppendLine().AppendLine("Mean points by lane:").AppendLine();
        var lanes = baseline["lanes"]!.AsObject().Select(l => l.Key).ToArray();
        b.AppendLine("| Candidate | Input | " + string.Join(" | ", lanes.Select(l => $"{l} ({set["lanes"]![l]})")) + " |");
        b.AppendLine("|---|---|" + string.Concat(lanes.Select(_ => "---|")));
        b.AppendLine("| no model (excerpt) | - | " + string.Join(" | ", lanes.Select(l => Num(baseline["lanes"]![l]))) + " |");
        foreach (var c in report["candidates"]!.AsArray())
            b.AppendLine($"| `{c!["model"]}` | {c["input"]} | " + string.Join(" | ", lanes.Select(l => Num(c["lanes"]![l]))) + " |");
        b.AppendLine().AppendLine("Other measures (information only):").AppendLine();
        b.AppendLine("| Candidate | Input | Mean, automatic check only | Mean, a failure scored 0 | Overlap with reference | Same title every pass | Titles per map pass | Checks |");
        b.AppendLine("|---|---|---|---|---|---|---|---|");
        b.AppendLine($"| no model (excerpt) | - | {Num(baseline["mean"])} | - | {Num(baseline["referenceOverlap"])} | - | - | - |");
        foreach (var c in report["candidates"]!.AsArray())
            b.AppendLine($"| `{c!["model"]}` | {c["input"]} | {Num(c["automaticMean"])} | {Num(c["strictMean"])} | {Num(c["referenceOverlap"])} | {Num(c["consistency"])} "
                + $"| {c["titlesPerPass"]} | {string.Join(", ", c["checks"]!.AsObject().Select(k => k.Key + " " + (k.Value is null ? "n/a" : k.Value.GetValue<bool>() ? "pass" : "fail")))} |");
        b.AppendLine().AppendLine("Cold starts and memory, per model (the adapter's own call has a 30 s timeout):").AppendLine();
        b.AppendLine("| Model | First call, fresh server | Then a whole load took | After unloading | Then a whole load took | Peak memory | Anon / file at end | docker stats | Server's own size |");
        b.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var c in report["candidates"]!.AsArray().Where(c => c!["input"]!.GetValue<string>() == "excerpt"))
        {
            var cs = c!["coldStart"]!; var m = c["memory"];
            string Call(string ms, string error) => cs[ms] is null ? "-" : $"{Num(cs[ms], "0")} ms{(cs[error] is { } e ? $" ({e})" : "")}";
            string Ms(string key) => cs[key] is null ? "-" : $"{Num(cs[key], "0")} ms";
            var size = c["loaded"]?["models"]?.AsArray().FirstOrDefault()?["size"]?.GetValue<long>();
            b.AppendLine($"| `{c["model"]}` | {Call("firstMs", "firstError")} | {Ms("firstWaitMs")} | {Call("reloadMs", "reloadError")} | {Ms("reloadWaitMs")} "
                + $"| {Bytes(m?["peakBytes"]?.GetValue<long>())} | {Bytes(m?["anonBytes"]?.GetValue<long>())} / {Bytes(m?["fileBytes"]?.GetValue<long>())} | {Cell(m?["stats"]?.GetValue<string>() ?? "-")} | {Bytes(size)} |");
        }
        foreach (var model in report["candidates"]!.AsArray().Select(c => c!["model"]!.GetValue<string>()).Distinct())
        {
            b.AppendLine().AppendLine($"Every title from `{model}` (points of 4; missed: a accurate, s specific, l length, p private):").AppendLine();
            b.AppendLine("| Item | Lane | Excerpt today | From the excerpt | From the text |");
            b.AppendLine("|---|---|---|---|---|");
            foreach (var item in report["items"]!.AsArray())
            {
                string Shown(string input) => item!["titles"]![$"{model} | {input}"] is { } t
                    ? (t["title"] is null ? $"*failed: {t["error"]}*" : Cell(t["title"]!.GetValue<string>())) + $" ({t["points"]}{(t["missed"]!.GetValue<string>() is { Length: > 0 } m ? " " + m : "")})"
                    : "-";
                b.AppendLine($"| {item!["id"]} | {item["lane"]} | {Cell(item["excerpt"]!.GetValue<string>())} ({item["excerptPoints"]}{(item["excerptMissed"]!.GetValue<string>() is { Length: > 0 } m ? " " + m : "")}) | {Shown("excerpt")} | {Shown("text")} |");
            }
        }
        return b.ToString();
    }
}
