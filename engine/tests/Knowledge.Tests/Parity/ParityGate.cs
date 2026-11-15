using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.FormattableString;

namespace Knowledge.Tests.Parity;

// The retrieval gate (SB-P2-15, D-028): whether a candidate run of a retrieval path may replace a baseline
// run, decided by rules fixed before any run. The shape follows Second Brain's locked evaluation (a pre-registered gate,
// paired cluster bootstrap intervals, a locked baseline), adapted; the numbers are D-028's and D-029's. The rules, in
// the order they are judged:
//   privacy      a must-hide record (another person's private record, a held injection) returned to the reader or open
//                to them fails the gate whatever the quality: always on a check the path claims the capability for, and
//                on any record the baseline did not reveal. A leak the baseline has too, through a capability the path
//                lacks (today: no viewer scope, no quarantine), keeps the verdict INCONCLUSIVE: a leaking path never
//                passes.
//   errors       an error on either side fails: an errored baseline scores zeros and would flatter any candidate.
//   comparable   the same load mode, k, corpus and golden data, query ids and labels, and embedding model (unless the
//                candidate changes the model on purpose); otherwise INCONCLUSIVE.
//   power        at least 200 judged queries in 30 clusters, and 10 judged queries in every type; on a smaller set the
//                verdict is INCONCLUSIVE unless a clear regression fails it (below), and the other deltas are reported
//                as information only.
//   regression   no metric overall, and no recall or MRR within a type, loses 0.01 or more, and none falls with its
//                whole interval below 0. Below the power floor only a clear regression is judged, and it fails: a loss
//                of 0.01 or more whose whole interval is below 0, overall or in a type (D-056, amended), so a small set
//                still guards against a change that loses answers it had.
//   improvement  a gain of 0.02 overall, or 0.05 in a declared target type with 10 clusters, with the interval's lower
//                bound above 0. No gain is INCONCLUSIVE when the comparison could not have seen one of the margin's
//                size (its minimum detectable effect is larger), FAIL otherwise.
//   latency      the candidate's p95 within D-029's budget.
// A judged query has a relevant answer and needs no capability either path lacks. A query that needs one is an expected
// gap: left out of the gains, but kept in the regression rule where the baseline already answered it.
public sealed record GateQuery(string Id, string Type, string Cluster, string Status, string[] Gaps, bool MustHide,
    double? Recall, double? ReciprocalRank, double? Ndcg, int? FirstRelevantRank, string[] Top, string[] Leaks, string[] Exposed,
    string? Error = null)
{
    [JsonIgnore] public bool Gap => Gaps.Length > 0;
    // Measured, with a relevant answer: a pure must-hide check has no score.
    [JsonIgnore] public bool Answered => Recall is not null;
    // The must-hide records this reader was given or could open.
    [JsonIgnore] public string[] Revealed => MustHide ? [.. Leaks.Concat(Exposed).Distinct(StringComparer.Ordinal)] : [];
    public double Score(string metric) => metric switch
    {
        "recall" => Recall ?? 0, "mrr" => ReciprocalRank ?? 0, "ndcg" => Ndcg ?? 0,
        _ => throw new ArgumentException($"no gate metric {metric}", nameof(metric)),
    };
}

// One path's run in one load mode. A lock keeps no latency: that belongs to the machine, not to the ranking.
public sealed record GatePath(string Name, string Tool, string How, string[] Supports, string? Unavailable, double? LatencyMsP95, GateQuery[] Queries);

public sealed record GateRun(string Mode, int K, IReadOnlyDictionary<string, string> Fingerprint, string? Model, GatePath Path)
{
    [JsonIgnore] public string FingerprintKey => string.Join(",", Fingerprint.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
}

// The pre-registered numbers. Changing one is a decision, made with its reason, not a tuning step.
public sealed record GateThresholds
{
    public static readonly GateThresholds Default = new();
    public int MinQueries { get; init; } = 200;
    public int MinClusters { get; init; } = 30;
    public int MinPerType { get; init; } = 10;
    // Distinct clusters a target type needs before its interval can prove a targeted gain (upstream's calibration).
    public int MinTypeClusters { get; init; } = 10;
    public double Tolerance { get; init; } = 0.01;
    public double OverallGain { get; init; } = 0.02;
    public double TargetGain { get; init; } = 0.05;
    // D-029: combined recall without a reranker; a reranker candidate passes 800.
    public double LatencyMsP95 { get; init; } = 500;
    public int Iterations { get; init; } = 10_000;
    public ulong Seed { get; init; } = 20261031;
    public double Alpha { get; init; } = 0.05;
}

// TargetTypes are the golden types a candidate claims to help (D-028's target category). ChangesModel says the candidate
// swaps the embedding model on purpose (SB-P2-06); otherwise runs of different models are not compared.
public sealed record GateOptions(GateThresholds? Thresholds = null, IReadOnlyList<string>? TargetTypes = null, bool ChangesModel = false);

public sealed record GateRule(string Name, string Status, string Detail);
public sealed record GateInterval(double Mean, double Lo, double Hi, int N, int Clusters, double Se);
public sealed record GateDelta(string Scope, string Metric, double Baseline, double Candidate, GateInterval Interval);
public sealed record GateLoser(string Id, string Type, double Drop, string Detail);
// Informational: the set is below the power floor, so the deltas are reported and only a clear regression was judged.
public sealed record GateResult(string Verdict, IReadOnlyList<GateRule> Rules, IReadOnlyList<GateDelta> Deltas,
    IReadOnlyDictionary<string, double> Mde, IReadOnlyList<GateLoser> Losers, int Judged, int Clusters, bool Informational);

// What moved between a locked run and a fresh one: the tripwire that makes a ranking change a recorded decision.
public sealed record GateDiff(bool FingerprintChanged, bool ModelChanged, IReadOnlyList<string> Changed, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
{
    [JsonIgnore] public bool Moved => FingerprintChanged || ModelChanged || Changed.Count + Missing.Count + Extra.Count > 0;
}

public sealed record GateSummary(int Queries, int Judged, double? Recall, double? Mrr, double? Ndcg, int Revealed, int Errors);
public sealed record GateLockEntry(string Date, string Reason, IReadOnlyDictionary<string, string> Fingerprint, string? Model, IReadOnlyDictionary<string, GateSummary> Paths);
// The locked baseline of one load mode, checked in as Parity/gate-baseline-<mode>.json. History lists every lock of the
// file, oldest first; the last entry is this one.
public sealed record GateLock(string Schema, string Label, string Mode, int K, IReadOnlyDictionary<string, string> Fingerprint, string? Model,
    GateLockEntry[] History, GatePath[] Paths)
{
    public GateRun? Run(string path) => Paths.FirstOrDefault(p => p.Name == path) is { } p ? new(Mode, K, Fingerprint, Model, p) : null;
}

public static class ParityGate
{
    public const string LockSchema = "rhythm-parity-gate-baseline.v1";
    public const string LockLabel = "DEMO: the locked retrieval baseline over the synthetic parity corpus; ids and scores only";
    // The ranking a lock keeps per query, deeper than k so a change just below the cut is still seen.
    public const int TopKept = 10;
    public static readonly string[] Metrics = ["recall", "mrr", "ndcg"];
    static readonly string[] TypeMetrics = ["recall", "mrr"];
    static readonly string[] FingerprintFiles = ["corpus.json", "golden-queries.json"];
    const double Eps = 1e-9;
    // Readable text in the checked-in lock: nothing in it is ever served to a browser.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string LockFile(string mode) => $"gate-baseline-{mode}.json";

    // The comparisons within one run (ParityGateRun): combined retrieval against each arm alone, whenever the run has
    // them, then each "candidate=baseline" pair `list` names (comma separated), every name a path of the run.
    public static IReadOnlyList<(string Candidate, string Baseline)> Pairs(string? list, IReadOnlyList<string> paths)
    {
        var pairs = new List<(string, string)>();
        foreach (var arm in new[] { "fulltext", "semantic" })
            if (paths.Contains("combined") && paths.Contains(arm)) pairs.Add(("combined", arm));
        foreach (var item in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0] == parts[1] || parts.Any(p => !paths.Contains(p)))
                throw new ArgumentException($"RHYTHM_PARITY_GATE_COMPARE pairs are candidate=baseline, two different paths of this run ({string.Join(", ", paths)}), not {item}");
            if (!pairs.Contains((parts[0], parts[1]))) pairs.Add((parts[0], parts[1]));
        }
        return pairs;
    }

    // A must-hide check: the query forbids records that a viewer scope or a quarantine should keep from this reader.
    // Forbidden records of other queries (a replaced fact under "current") are ranking errors, not privacy.
    public static bool MustHide(ParityQuery q) => q.Forbids.Length > 0 && q.Requires.Any(c => c is "viewer-scope" or "quarantine");

    // The data a run is tied to: SHA-256 of the corpus and the golden queries, line endings normalised.
    public static IReadOnlyDictionary<string, string> Fingerprint(string folder) => FingerprintFiles.ToDictionary(f => f,
        f => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(folder, f)).ReplaceLineEndings("\n")))),
        StringComparer.Ordinal);

    // Queries that move together share a cluster, and the bootstrap resamples whole clusters. The cluster is the golden
    // set's own (ParityQuery.ClusterKey, as upstream's clusterKey): the query's first grade-2 record, or the chain's
    // current record a replaced-fact query names in `cluster`, so queries answered by one record resample together.
    public static IReadOnlyDictionary<string, string> Clusters(ParityGolden golden) =>
        golden.Queries.ToDictionary(q => q.Id, q => q.ClusterKey, StringComparer.Ordinal);

    // One path's evaluation as the gate reads it. A record the corpus does not know ("?" and an engine id) is kept as
    // "?" alone, so a lock never holds an id that changes with every load.
    public static GatePath FromRuns(ParityPath path, IReadOnlyList<(ParityQuery Query, ParityRun Run)> runs, IReadOnlyDictionary<string, string> clusters)
    {
        static string Known(string id) => id.StartsWith('?') ? "?" : id;
        static double? Round(double? x) => x is { } v ? Math.Round(v, 6) : null;
        var measured = runs.Where(r => r.Run.Status is not ("unavailable" or "error")).Select(r => r.Run.LatencyMs).ToArray();
        return new(path.Name, path.Tool, path.How, [.. path.Supports.Order(StringComparer.Ordinal)], path.Unavailable,
            measured.Length > 0 ? Math.Round(ParityMetrics.Percentile(measured, 95), 3) : null,
            [.. runs.Select(r => new GateQuery(r.Query.Id, r.Query.Type, clusters[r.Query.Id], r.Run.Status, [.. r.Run.Gaps], MustHide(r.Query),
                Round(r.Run.Recall), Round(r.Run.ReciprocalRank), Round(r.Run.Ndcg), r.Run.FirstRelevantRank,
                [.. r.Run.Ranked.Take(TopKept).Select(Known)], [.. r.Run.Leaks.Select(Known)], [.. r.Run.Exposed.Select(Known)],
                r.Run.Status == "error" ? r.Run.Error : null))]);
    }

    // A stand-in baseline for a path or mode with no lock: every query unmeasured, so the comparison is inconclusive and
    // only the hard rules (privacy, errors) can decide.
    public static GateRun NoBaseline(GateRun candidate, string why) => candidate with
    {
        Path = candidate.Path with
        {
            Unavailable = why, LatencyMsP95 = null,
            Queries = [.. candidate.Path.Queries.Select(q => q with { Status = "unavailable", Recall = null, ReciprocalRank = null, Ndcg = null,
                FirstRelevantRank = null, Top = [], Leaks = [], Exposed = [], Error = null })]
        }
    };

    public static GateResult Evaluate(GateRun baseline, GateRun candidate, GateOptions? options = null)
    {
        options ??= new();
        var t = options.Thresholds ?? GateThresholds.Default;
        var rules = new List<GateRule>();
        var deltas = new List<GateDelta>();
        var mde = new Dictionary<string, double>(StringComparer.Ordinal);
        var (judgedCount, clusterCount) = (0, 0);
        void Add(string rule, string status, string detail) => rules.Add(new(rule, status, detail));
        GateResult Finish(bool informational) => new(
            rules.Any(r => r.Status == "fail") ? "FAIL" : rules.Any(r => r.Status == "inconclusive") ? "INCONCLUSIVE" : "PASS",
            rules, deltas, mde, Losers(baseline.Path, candidate.Path), judgedCount, clusterCount, informational);

        // The hard rules first: they decide however small or incomparable the sample.
        var (privacy, privacyDetail) = Privacy(baseline.Path, candidate.Path, baseline.FingerprintKey == candidate.FingerprintKey);
        Add("privacy", privacy, privacyDetail);
        static int Errors(GatePath p) => p.Queries.Count(q => q.Status == "error");
        var (baseErrors, candidateErrors) = (Errors(baseline.Path), Errors(candidate.Path));
        Add("errors", baseErrors + candidateErrors == 0 ? "pass" : "fail", $"{candidateErrors} query error(s) in the candidate, {baseErrors} in the baseline");
        var problems = Comparability(baseline, candidate, options);
        if (problems.Count > 0)
        {
            Add("comparable", "inconclusive", string.Join("; ", problems));
            return Finish(false);
        }
        Add("comparable", "pass", baseline.Model != candidate.Model
            ? $"the candidate changes the embedding model on purpose ({baseline.Model ?? "none"} to {candidate.Model ?? "none"})"
            : $"same load mode ({candidate.Mode}), golden data, queries, labels and model");

        var byId = baseline.Path.Queries.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var pairs = candidate.Path.Queries.Select(c => (B: byId[c.Id], C: c)).Where(p => p.B.Answered && p.C.Answered).ToArray();
        var judged = pairs.Where(p => !p.B.Gap && !p.C.Gap).ToArray();
        // An expected gap the baseline already answers can still be lost; one it scores 0 on cannot regress.
        var protectedGaps = pairs.Where(p => (p.B.Gap || p.C.Gap) && Metrics.Any(m => p.B.Score(m) > 0)).ToArray();
        var regression = judged.Concat(protectedGaps).ToArray();
        judgedCount = judged.Length;
        clusterCount = judged.Select(p => p.C.Cluster).Distinct(StringComparer.Ordinal).Count();

        GateDelta Delta(string scope, IReadOnlyList<(GateQuery B, GateQuery C)> subset, string metric)
        {
            var interval = GateBootstrap.Paired([.. subset.Select(p => p.C.Score(metric) - p.B.Score(metric))], [.. subset.Select(p => p.C.Cluster)],
                t.Iterations, t.Seed, t.Alpha);
            var row = new GateDelta(scope, metric, Mean(subset.Select(p => p.B.Score(metric))), Mean(subset.Select(p => p.C.Score(metric))), interval);
            deltas.Add(row);
            return row;
        }

        var power = new List<string>();
        if (judged.Length < t.MinQueries) power.Add($"{judged.Length} judged queries is below the {t.MinQueries}-query floor");
        if (clusterCount < t.MinClusters) power.Add($"{clusterCount} clusters is below the {t.MinClusters}-cluster floor");
        var thin = judged.GroupBy(p => p.C.Type).Where(g => g.Count() < t.MinPerType).Select(g => $"{g.Key} {g.Count()}").ToArray();
        if (thin.Length > 0) power.Add($"types below {t.MinPerType} judged queries: {string.Join(", ", thin)}");
        var informational = power.Count > 0;
        Add("power", informational ? "inconclusive" : "pass", informational
            ? string.Join("; ", power) + " (D-028): the deltas are information, not evidence, and only a clear regression is judged"
            : $"{judged.Length} judged queries in {clusterCount} clusters, every type at least {t.MinPerType}");

        // Regression: every metric overall, recall and MRR within each type. With power, a loss of the tolerance or an
        // interval wholly below 0 fails; without it, only both at once (a clear regression, D-056 amended).
        var regressions = new List<string>();
        foreach (var metric in Metrics)
        {
            var row = Delta("overall", regression, metric);
            if (Lost(row)) regressions.Add($"overall {metric} {Signed(row.Interval.Mean)}");
        }
        var skipped = new List<string>();
        foreach (var type in regression.Select(p => p.C.Type).Distinct())
        {
            var subset = regression.Where(p => p.C.Type == type).ToArray();
            // Reached only through protected gaps once the judged set has its power: too few to judge on their own.
            if (!informational && subset.Length < t.MinPerType) { skipped.Add($"{type} ({subset.Length})"); continue; }
            foreach (var metric in TypeMetrics)
            {
                var row = Delta(type, subset, metric);
                if (Lost(row)) regressions.Add($"{type} {metric} {Signed(row.Interval.Mean)}");
            }
        }
        bool Lost(GateDelta row) => informational
            ? row.Interval.Mean <= -t.Tolerance + Eps && row.Interval.Hi < 0
            : row.Interval.Mean <= -t.Tolerance + Eps || row.Interval.Hi < 0;

        // Improvement, over the judged queries only: an expected gap's gain is not evidence for a ranking change.
        var wins = new List<string>();
        foreach (var metric in Metrics)
        {
            var row = protectedGaps.Length > 0 ? Delta("overall (judged)", judged, metric) : deltas.First(d => d.Scope == "overall" && d.Metric == metric);
            // 80% power for "the interval's lower bound is above 0": (1.96 + 0.84) standard errors.
            mde[metric] = Math.Round(2.8 * row.Interval.Se, 6);
            if (row.Interval.Mean >= t.OverallGain - Eps && row.Interval.Lo > 0) wins.Add($"overall {metric} {Signed(row.Interval.Mean)}");
        }
        if (informational)
        {
            // Below the floor a clear regression still fails; anything less stays information.
            if (regressions.Count > 0) Add("regression", "fail", "a clear regression below the power floor (D-056): " + string.Join("; ", regressions));
            return Finish(true);
        }
        Add("regression", regressions.Count > 0 ? "fail" : "pass", regressions.Count > 0
            ? string.Join("; ", regressions)
            : Invariant($"no metric lost {t.Tolerance} overall or in any type") + (skipped.Count > 0 ? $"; too few to judge: {string.Join(", ", skipped)}" : ""));

        var underpowered = new List<string>();
        foreach (var type in options.TargetTypes ?? [])
        {
            var subset = judged.Where(p => p.C.Type == type).ToArray();
            var typeClusters = subset.Select(p => p.C.Cluster).Distinct(StringComparer.Ordinal).Count();
            if (subset.Length < t.MinPerType || typeClusters < t.MinTypeClusters)
            {
                underpowered.Add($"{type} has {subset.Length} judged queries in {typeClusters} clusters, below {t.MinPerType} in {t.MinTypeClusters}");
                continue;
            }
            foreach (var metric in TypeMetrics)
            {
                var row = Delta(type + " (target)", subset, metric);
                if (row.Interval.Mean >= t.TargetGain - Eps && row.Interval.Lo > 0) wins.Add($"{type} {metric} {Signed(row.Interval.Mean)}");
            }
        }
        var noisy = Metrics.Where(m => mde[m] > t.OverallGain).Select(m => Invariant($"{m} {mde[m]:0.0000}")).ToArray();
        if (wins.Count > 0) Add("improvement", "pass", string.Join("; ", wins));
        else if (underpowered.Count > 0) Add("improvement", "inconclusive", "a target gain cannot be proven: " + string.Join("; ", underpowered));
        else if (noisy.Length > 0)
            Add("improvement", "inconclusive", Invariant($"no gain shown, and this comparison could not have seen one of {t.OverallGain}: minimum detectable effect {string.Join(", ", noisy)}"));
        else Add("improvement", "fail", Invariant($"no metric gained {t.OverallGain} overall (or {t.TargetGain} in a target type) with an interval above 0"));

        if (candidate.Path.LatencyMsP95 is { } p95)
            Add("latency", p95 <= t.LatencyMsP95 ? "pass" : "fail", Invariant($"candidate p95 {p95:0.0} ms, budget {t.LatencyMsP95} ms (D-029)"));
        else Add("latency", "skipped", "the candidate has no measured latency");
        return Finish(false);
    }

    // Reveals are compared with the baseline's only on the same data: a corpus or golden-set change adds records and
    // checks the baseline never saw, so nothing is inherited from it and a gap reveal stays inconclusive.
    static (string Status, string Detail) Privacy(GatePath baseline, GatePath candidate, bool sameData)
    {
        if (candidate.Unavailable is { } why) return ("inconclusive", $"the candidate did not run ({why}): nothing to judge");
        var compared = baseline.Unavailable is null && sameData;
        var before = baseline.Queries.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First().Revealed.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var checks = candidate.Queries.Count(q => q.MustHide);
        var (breaches, added, inherited) = (new List<string>(), new List<string>(), new List<string>());
        foreach (var q in candidate.Queries.Where(q => q.Revealed.Length > 0))
        {
            var shown = $"{q.Id}: {string.Join(", ", q.Revealed)}";
            if (!q.Gap) breaches.Add(shown);
            else if (compared && q.Revealed.Except(before.GetValueOrDefault(q.Id) ?? []).ToArray() is { Length: > 0 } extra)
                added.Add($"{q.Id}: {string.Join(", ", extra)}");
            else inherited.Add(shown);
        }
        if (breaches.Count + added.Count > 0)
            return ("fail", string.Join("; ", new[]
            {
                breaches.Count > 0 ? "revealed although the path claims the capability: " + string.Join("; ", breaches) : null,
                added.Count > 0 ? "revealed where the baseline did not: " + string.Join("; ", added) : null,
            }.Where(s => s is not null)));
        if (inherited.Count > 0)
            return ("inconclusive", $"{inherited.Count} of {checks} must-hide checks still reveal records through a capability the path lacks"
                + (compared ? ", as the baseline does" : baseline.Unavailable is null ? ", with no baseline on this data to compare" : ", with no baseline to compare")
                + $" ({string.Join("; ", inherited)}): a leaking path never passes");
        return ("pass", $"no must-hide record returned or open to the reader in {checks} checks");
    }

    static List<string> Comparability(GateRun b, GateRun c, GateOptions options)
    {
        var problems = new List<string>();
        if (b.Path.Unavailable is { } bu) problems.Add($"the baseline did not run ({bu})");
        if (c.Path.Unavailable is { } cu) problems.Add($"the candidate did not run ({cu})");
        if (b.Mode != c.Mode) problems.Add($"load mode differs ({b.Mode} vs {c.Mode})");
        if (b.K != c.K) problems.Add($"k differs ({b.K} vs {c.K})");
        if (b.FingerprintKey != c.FingerprintKey) problems.Add("the corpus or golden queries differ from the baseline's (fingerprint): lock a baseline on this data first");
        if (b.Model != c.Model && !options.ChangesModel)
            problems.Add($"embedding model differs ({b.Model ?? "none"} vs {c.Model ?? "none"}); declare a model change to compare them");
        static string[] Duplicates(GatePath p) => [.. p.Queries.GroupBy(q => q.Id).Where(g => g.Count() > 1).Select(g => g.Key)];
        if (Duplicates(b.Path) is { Length: > 0 } bd) problems.Add("duplicate query ids in the baseline: " + List(bd));
        if (Duplicates(c.Path) is { Length: > 0 } cd) problems.Add("duplicate query ids in the candidate: " + List(cd));
        if (problems.Any(p => p.StartsWith("duplicate", StringComparison.Ordinal))) return problems;
        var (before, after) = (b.Path.Queries.ToDictionary(q => q.Id), c.Path.Queries.ToDictionary(q => q.Id));
        var (onlyBase, onlyCandidate) = (before.Keys.Except(after.Keys).ToArray(), after.Keys.Except(before.Keys).ToArray());
        if (onlyBase.Length + onlyCandidate.Length > 0)
        {
            problems.Add($"query sets differ (only in the baseline: {List(onlyBase)}; only in the candidate: {List(onlyCandidate)})");
            return problems;
        }
        foreach (var (label, differs) in new (string, Func<GateQuery, GateQuery, bool>)[]
        {
            ("type", (x, y) => x.Type != y.Type), ("cluster", (x, y) => x.Cluster != y.Cluster), ("must-hide", (x, y) => x.MustHide != y.MustHide),
        })
            if (after.Values.Where(q => differs(before[q.Id], q)).Select(q => q.Id).ToArray() is { Length: > 0 } ids)
                problems.Add($"{label} differs for {List(ids)}");
        return problems;
    }

    static IReadOnlyList<GateLoser> Losers(GatePath baseline, GatePath candidate)
    {
        var before = baseline.Queries.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return [.. candidate.Queries
            .Where(q => q.Answered && before.TryGetValue(q.Id, out var b) && b.Answered)
            .Select(q => (Q: q, B: before[q.Id], Drop: Metrics.Sum(m => Math.Max(0, before[q.Id].Score(m) - q.Score(m)))))
            .Where(x => x.Drop > Eps)
            .OrderByDescending(x => x.Drop).ThenBy(x => x.Q.Id, StringComparer.Ordinal)
            .Select(x => new GateLoser(x.Q.Id, x.Q.Type, Math.Round(x.Drop, 6), string.Join(", ", Metrics.Where(m => x.Q.Score(m) < x.B.Score(m) - Eps)
                .Select(m => $"{m} {x.B.Score(m).ToString("0.000", CultureInfo.InvariantCulture)} to {x.Q.Score(m).ToString("0.000", CultureInfo.InvariantCulture)}"))))];
    }

    // The tripwire: which queries a fresh run answers differently from the lock (status, ranking, scores, leaks, gaps).
    public static GateDiff Diff(GateRun locked, GateRun fresh)
    {
        var before = locked.Path.Queries.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var after = fresh.Path.Queries.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        static bool Near(double? x, double? y) => x is null ? y is null : y is not null && Math.Abs(x.Value - y.Value) < 1e-6;
        static bool Same(GateQuery x, GateQuery y) => x.Status == y.Status && x.Type == y.Type && x.Cluster == y.Cluster && x.MustHide == y.MustHide
            && x.FirstRelevantRank == y.FirstRelevantRank && Near(x.Recall, y.Recall) && Near(x.ReciprocalRank, y.ReciprocalRank) && Near(x.Ndcg, y.Ndcg)
            && x.Top.SequenceEqual(y.Top) && x.Leaks.SequenceEqual(y.Leaks) && x.Exposed.SequenceEqual(y.Exposed) && x.Gaps.SequenceEqual(y.Gaps);
        return new(locked.FingerprintKey != fresh.FingerprintKey, locked.Model != fresh.Model,
            [.. after.Where(p => before.TryGetValue(p.Key, out var b) && !Same(b, p.Value)).Select(p => p.Key)],
            [.. before.Keys.Except(after.Keys)], [.. after.Keys.Except(before.Keys)]);
    }

    public static GateSummary Summary(GatePath path)
    {
        var answered = path.Queries.Where(q => q.Answered).ToArray();
        double? Avg(string metric) => answered.Length > 0 ? Math.Round(answered.Average(q => q.Score(metric)), 6) : null;
        return new(path.Queries.Length, answered.Count(q => !q.Gap), Avg("recall"), Avg("mrr"), Avg("ndcg"),
            path.Queries.Count(q => q.Revealed.Length > 0), path.Queries.Count(q => q.Status == "error"));
    }

    // Why a fresh run may not become a lock: a broken baseline would flatter every later candidate, and a lock that
    // reveals more than the old one would make a new leak look inherited. Reveals are compared with the old lock only on
    // the same data: a corpus or golden-set change (another fingerprint) adds records and checks the old lock never saw,
    // so that lock is taken on the code of the one before it and starts the comparison afresh.
    public static IReadOnlyList<string> LockRefusals(IReadOnlyList<GatePath> fresh, GateLock? previous, IReadOnlyDictionary<string, string>? fingerprint = null)
    {
        var refusals = new List<string>();
        if (previous is not null && fingerprint is not null && !SameData(previous.Fingerprint, fingerprint)) previous = null;
        foreach (var p in fresh)
        {
            if (p.Unavailable is { } why) refusals.Add($"{p.Name} did not run ({why})");
            if (p.Queries.Count(q => q.Status == "error") is > 0 and var n) refusals.Add($"{p.Name} has {n} query error(s)");
            if (p.Queries.Where(q => !q.Gap && q.Revealed.Length > 0).Select(q => q.Id).ToArray() is { Length: > 0 } breached)
                refusals.Add($"{p.Name} reveals must-hide records although it claims the capability: {List(breached)}");
            var old = previous?.Paths.FirstOrDefault(o => o.Name == p.Name)?.Queries.ToDictionary(q => q.Id, q => q.Revealed);
            if (old is not null && p.Queries.Where(q => q.Revealed.Except(old.GetValueOrDefault(q.Id) ?? []).Any()).Select(q => q.Id).ToArray() is { Length: > 0 } added)
                refusals.Add($"{p.Name} reveals must-hide records the locked baseline did not: {List(added)}");
        }
        return refusals;
    }

    public static GateLock NewLock(string mode, int k, IReadOnlyDictionary<string, string> fingerprint, string? model, IReadOnlyList<GatePath> paths,
        string reason, DateOnly date, GateLock? previous) => new(LockSchema, LockLabel, mode, k, fingerprint, model,
        [.. previous?.History ?? [], new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason, fingerprint, model,
            paths.ToDictionary(p => p.Name, Summary, StringComparer.Ordinal))],
        [.. paths.Select(p => p with { LatencyMsP95 = null })]);

    public static GateLock ReadLock(string json)
    {
        var l = JsonSerializer.Deserialize<GateLock>(json, Json) ?? throw new InvalidDataException("empty gate baseline");
        if (l.Schema != LockSchema) throw new InvalidDataException($"gate baseline schema {l.Schema}, expected {LockSchema}");
        return l;
    }

    // One query per line, so a re-lock reads as a diff of the queries that moved.
    public static string WriteLock(GateLock l)
    {
        string S<T>(T value) => JsonSerializer.Serialize(value, Json);
        var json = new StringBuilder("{\n");
        json.Append($"  \"schema\": {S(l.Schema)},\n  \"label\": {S(l.Label)},\n  \"mode\": {S(l.Mode)},\n  \"k\": {l.K},\n");
        json.Append($"  \"fingerprint\": {S(l.Fingerprint)},\n  \"model\": {(l.Model is null ? "null" : S(l.Model))},\n  \"history\": [\n");
        json.AppendJoin(",\n", l.History.Select(h => "    " + S(h))).Append("\n  ],\n  \"paths\": [\n");
        json.AppendJoin(",\n", l.Paths.Select(p =>
            $"    {{\n      \"name\": {S(p.Name)}, \"tool\": {S(p.Tool)}, \"how\": {S(p.How)}, \"supports\": {S(p.Supports)},"
            + (p.Unavailable is null ? "" : $" \"unavailable\": {S(p.Unavailable)},") + "\n      \"queries\": [\n"
            + string.Join(",\n", p.Queries.Select(q => "        " + S(q))) + "\n      ]\n    }"));
        return json.Append("\n  ]\n}\n").ToString();
    }

    static bool SameData(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    static double Mean(IEnumerable<double> xs) => xs.ToArray() is { Length: > 0 } a ? a.Average() : 0;
    static string Signed(double x) => (x >= 0 ? "+" : "") + x.ToString("0.0000", CultureInfo.InvariantCulture);
    static string List(IReadOnlyList<string> ids) => ids.Count == 0 ? "none" : ids.Count > 5 ? string.Join(", ", ids.Take(5)) + $" (+{ids.Count - 5} more)" : string.Join(", ", ids);
}

// The paired cluster bootstrap: an interval for the mean per-query difference between two runs on the same queries,
// resampling whole clusters, so queries that move together count as one draw. Percentile interval; the standard error
// is the replicates' spread, which the minimum detectable effect uses too.
public static class GateBootstrap
{
    // SplitMix64: small and seedable, so a verdict is the same on every machine and every run.
    sealed class SplitMix(ulong seed)
    {
        ulong state = seed;
        public double Next()
        {
            var z = state += 0x9E3779B97F4A7C15;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return ((z ^ (z >> 31)) >> 11) * (1.0 / (1UL << 53));
        }
    }

    public static GateInterval Paired(IReadOnlyList<double> deltas, IReadOnlyList<string> clusters, int iterations = 10_000, ulong seed = 20261031, double alpha = 0.05)
    {
        if (deltas.Count != clusters.Count) throw new ArgumentException("every delta needs its cluster", nameof(clusters));
        if (deltas.Count == 0) return new(0, 0, 0, 0, 0, 0);
        // Grouped in key order, so the interval does not depend on the order the queries came in.
        var groups = deltas.Zip(clusters).GroupBy(x => x.Second, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Sum: g.Sum(x => x.First), Count: g.Count())).ToArray();
        var random = new SplitMix(seed);
        var replicates = new double[iterations];
        for (var b = 0; b < iterations; b++)
        {
            var (sum, count) = (0.0, 0);
            for (var g = 0; g < groups.Length; g++)
            {
                var pick = groups[(int)(random.Next() * groups.Length)];
                sum += pick.Sum;
                count += pick.Count;
            }
            replicates[b] = sum / count;
        }
        // No spread, no uncertainty (and no rounding noise).
        double se = 0;
        if (deltas.Count > 1 && deltas.Any(d => d != deltas[0]))
        {
            var m = replicates.Average();
            se = Math.Sqrt(replicates.Sum(x => (x - m) * (x - m)) / (iterations - 1));
        }
        Array.Sort(replicates);
        return new(deltas.Average(), replicates[(int)Math.Floor(alpha / 2 * iterations)],
            replicates[Math.Min(iterations - 1, (int)Math.Ceiling((1 - alpha / 2) * iterations) - 1)], deltas.Count, groups.Length, se);
    }
}
