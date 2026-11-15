namespace Knowledge.Tests.Parity;

// The retrieval gate's own rules, on synthetic runs: these run in every ordinary test run, since they need no database
// and no model. The gate against real retrieval runs is opt-in (ParityGateRun), as the evaluation is.
public sealed class ParityGateTests
{
    static readonly IReadOnlyDictionary<string, string> Data = new Dictionary<string, string> { ["corpus.json"] = "c1", ["golden-queries.json"] = "g1" };
    // These assert a rule's status, not an interval's width, so fewer draws do.
    static readonly GateOptions Fast = new(new GateThresholds { Iterations = 2000 });

    // A synthetic golden set: `types` types of `perType` queries each. Query i is in cluster i / clusterSize, so with the
    // default four a cluster holds one row of four types and each type spreads over perType clusters. The baseline
    // misses every third row.
    static GateQuery[] Set(int types = 12, int perType = 20, int clusterSize = 4) => [.. Enumerable.Range(0, types * perType).Select(i =>
    {
        var hit = i / types % 3 != 0;
        return new GateQuery($"q{i:000}", $"t{i % types}", $"c{i / clusterSize}", hit ? "pass" : "miss", [], false,
            hit ? 1 : 0, hit ? 1 : 0, hit ? 1 : 0, hit ? 1 : null, hit ? [$"r{i}"] : [], [], []);
    })];
    static GateQuery Hit(GateQuery q) => q with { Status = "pass", Recall = 1, ReciprocalRank = 1, Ndcg = 1, FirstRelevantRank = 1 };
    static GateQuery Miss(GateQuery q) => q with { Status = "miss", Recall = 0, ReciprocalRank = 0, Ndcg = 0, FirstRelevantRank = null };
    // A must-hide check (no relevant answer): Alex must not see Sam's private record. By default the path lacks the
    // viewer scope the check needs, as every path does today.
    static GateQuery Check(string id, string[]? leaks = null, string[]? exposed = null, bool gap = true) => new(id, "visibility", "v-" + id,
        gap ? "expected-gap" : "pass", gap ? ["viewer-scope"] : [], true, null, null, null, null, [], leaks ?? [], exposed ?? []);
    static GateRun Run(IEnumerable<GateQuery> queries, double? p95 = 4, string? model = "minilm") =>
        new("installed", 5, Data, model, new GatePath("fulltext", "search_knowledge", "test", [], null, p95, [.. queries]));
    static GateRule Rule(GateResult r, string name) => r.Rules.Single(x => x.Name == name);

    [Fact] public void The_gate_catches_a_planted_regression()
    {
        var baseline = Set();
        // Every miss outside t3 fixed, a large gain overall, while t3 loses three of its thirteen answers.
        var lost = baseline.Where(q => q.Type == "t3" && q.Recall == 1).Take(3).Select(q => q.Id).ToHashSet();
        var candidate = baseline.Select(q => lost.Contains(q.Id) ? Miss(q) : q.Type == "t3" ? q : Hit(q)).ToArray();
        var result = ParityGate.Evaluate(Run(baseline), Run(candidate), Fast);
        Assert.Equal("FAIL", result.Verdict);
        Assert.Equal("fail", Rule(result, "regression").Status);
        Assert.Contains("t3 recall -0.1500", Rule(result, "regression").Detail);
        Assert.Contains("t3 mrr -0.1500", Rule(result, "regression").Detail);
        // The gain is real and proven; it still does not buy the loss.
        Assert.Equal("pass", Rule(result, "improvement").Status);
        Assert.Contains(result.Losers, l => lost.Contains(l.Id) && l.Detail.Contains("recall 1.000 to 0.000"));

        // One lost answer in a type of twenty is a loss of 0.05: over the 0.01 tolerance on its own.
        var one = baseline.Select(q => q.Id == lost.First() ? Miss(q) : q).ToArray();
        var single = ParityGate.Evaluate(Run(baseline), Run(one), Fast);
        Assert.Equal("FAIL", single.Verdict);
        Assert.Contains("t3 recall -0.0500", Rule(single, "regression").Detail);
    }

    [Fact] public void A_proven_gain_passes()
    {
        var baseline = Set();
        var result = ParityGate.Evaluate(Run(baseline), Run(baseline.Select(Hit)), Fast);
        Assert.Equal("PASS", result.Verdict);
        Assert.All(result.Rules, r => Assert.Equal("pass", r.Status));
        Assert.False(result.Informational);
        Assert.Equal((240, 60), (result.Judged, result.Clusters));
        var recall = result.Deltas.Single(d => d.Scope == "overall" && d.Metric == "recall");
        // 7 of every 20 answers fixed.
        Assert.Equal(0.35, recall.Interval.Mean, 9);
        Assert.Equal((0.65, 1.0), (Math.Round(recall.Baseline, 9), Math.Round(recall.Candidate, 9)));
        Assert.True(recall.Interval.Lo > 0);
        Assert.Equal((240, 60), (recall.Interval.N, recall.Interval.Clusters));
    }

    [Fact] public void Below_the_minimum_size_the_gate_answers_inconclusive_whatever_the_numbers()
    {
        // The phase 0 set's size: 34 queries in 13 types. Every miss fixed is still no verdict, and the deltas are
        // reported as information only.
        var small = Set(types: 13, perType: 2).Concat(Set(types: 8, perType: 1).Select(q => q with { Id = "x" + q.Id, Cluster = "x" + q.Cluster })).ToArray();
        Assert.Equal(34, small.Length);
        var gain = ParityGate.Evaluate(Run(small.Select(Miss)), Run(small.Select(Hit)), Fast);
        Assert.Equal("INCONCLUSIVE", gain.Verdict);
        Assert.True(gain.Informational);
        Assert.Contains("34 judged queries is below the 200-query floor", Rule(gain, "power").Detail);
        Assert.Contains("clusters is below the 30-cluster floor", Rule(gain, "power").Detail);
        Assert.Contains("types below 10 judged queries", Rule(gain, "power").Detail);
        Assert.DoesNotContain(gain.Rules, r => r.Name is "regression" or "improvement" or "latency");
        Assert.Equal(1, gain.Deltas.Single(d => d.Scope == "overall" && d.Metric == "recall").Interval.Mean, 9);
        // One lost answer on a set this small is not judged: a single query is too much of the mean, and the clusters
        // that kept theirs leave the interval reaching 0.
        var lostOne = small.Select(Hit).ToArray();
        var oneMiss = lostOne.Select((q, i) => i == 0 ? Miss(q) : q).ToArray();
        var one = ParityGate.Evaluate(Run(lostOne), Run(oneMiss), Fast);
        Assert.Equal("INCONCLUSIVE", one.Verdict);
        Assert.DoesNotContain(one.Rules, r => r.Name == "regression");

        // But a clear regression fails however small the set (D-056, amended): losing every answer it had is a loss of
        // the tolerance with the whole interval below 0, overall and in every type.
        var collapse = ParityGate.Evaluate(Run(small.Select(Hit)), Run(small.Select(Miss)), Fast);
        Assert.Equal("FAIL", collapse.Verdict);
        Assert.True(collapse.Informational);
        Assert.Equal("inconclusive", Rule(collapse, "power").Status);
        Assert.Equal("fail", Rule(collapse, "regression").Status);
        Assert.Contains("a clear regression below the power floor", Rule(collapse, "regression").Detail);
        Assert.Contains("overall recall -1.0000", Rule(collapse, "regression").Detail);
        // A loss of 0.3 overall with the interval wholly below 0 fails too: the case the floor used to let through.
        var big = Set(types: 6, perType: 20);
        var lost = big.Select((q, i) => q.Recall == 1 && i % 2 == 0 ? Miss(q) : q).ToArray();
        var partial = ParityGate.Evaluate(Run(big), Run(lost), Fast);
        Assert.True(partial.Informational);
        var recall = partial.Deltas.Single(d => d.Scope == "overall" && d.Metric == "recall");
        Assert.True(recall.Interval.Mean <= -0.3 && recall.Interval.Hi < 0, $"{recall.Interval.Mean} [{recall.Interval.Lo}, {recall.Interval.Hi}]");
        Assert.Equal("FAIL", partial.Verdict);

        // Each floor on its own.
        var tenTypes = Set(types: 10);
        var short1 = tenTypes.Skip(1).ToArray();
        Assert.Contains("199 judged queries is below the 200-query floor", Rule(ParityGate.Evaluate(Run(short1), Run(short1.Select(Hit)), Fast), "power").Detail);
        Assert.Equal("PASS", ParityGate.Evaluate(Run(tenTypes), Run(tenTypes.Select(Hit)), Fast).Verdict);
        var clumped = Set(clusterSize: 12);
        Assert.Contains("20 clusters is below the 30-cluster floor", Rule(ParityGate.Evaluate(Run(clumped), Run(clumped.Select(Hit)), Fast), "power").Detail);
        var thin = Set().Concat(Set(types: 1, perType: 9).Select(q => q with { Id = "thin" + q.Id, Type = "thin" })).ToArray();
        var thinResult = ParityGate.Evaluate(Run(thin), Run(thin.Select(Hit)), Fast);
        Assert.Equal("INCONCLUSIVE", thinResult.Verdict);
        Assert.Contains("types below 10 judged queries: thin 9", Rule(thinResult, "power").Detail);
    }

    [Fact] public void No_gain_fails_and_a_comparison_too_noisy_to_see_one_is_inconclusive()
    {
        var baseline = Set();
        var same = ParityGate.Evaluate(Run(baseline), Run(baseline), Fast);
        Assert.Equal("FAIL", same.Verdict);
        Assert.Equal("fail", Rule(same, "improvement").Status);
        Assert.Equal(0, same.Mde["recall"]);
        // In every type three answers gained and three lost, in different clusters: no change on average, and an
        // interval far wider than the 0.02 margin.
        var swapped = baseline.Select(q => (int.Parse(q.Id[1..]) / 12) switch { 0 or 3 or 6 => Hit(q), 1 or 2 or 4 => Miss(q), _ => q }).ToArray();
        var noisy = ParityGate.Evaluate(Run(baseline), Run(swapped), Fast);
        Assert.Equal("INCONCLUSIVE", noisy.Verdict);
        Assert.Equal("pass", Rule(noisy, "regression").Status);
        Assert.Equal("inconclusive", Rule(noisy, "improvement").Status);
        Assert.Contains("minimum detectable effect", Rule(noisy, "improvement").Detail);
        Assert.True(noisy.Mde["recall"] > 0.02);
    }

    [Fact] public void Expected_gaps_never_count_as_gains_but_what_the_baseline_answered_cannot_be_lost()
    {
        // t0 needs a capability the path lacks (say validity): its queries are expected gaps on both sides.
        var baseline = Set().Select(q => q.Type == "t0" ? q with { Gaps = ["validity"], Status = "expected-gap" } : q).ToArray();
        var gapGain = ParityGate.Evaluate(Run(baseline), Run(baseline.Select(q => q.Type == "t0" ? Hit(q) with { Status = "expected-gap" } : q)), Fast);
        Assert.Equal("FAIL", gapGain.Verdict);
        Assert.Equal("fail", Rule(gapGain, "improvement").Status);
        Assert.Equal(220, gapGain.Judged);
        var gapLoss = ParityGate.Evaluate(Run(baseline), Run(baseline.Select(q => q.Type == "t0" ? Miss(q) with { Status = "expected-gap" } : Hit(q))), Fast);
        Assert.Equal("FAIL", gapLoss.Verdict);
        Assert.Contains("t0 recall -1.0000", Rule(gapLoss, "regression").Detail);
        Assert.Equal("pass", Rule(gapLoss, "improvement").Status);
    }

    [Fact] public void A_must_hide_leak_fails_the_gate_whatever_the_quality()
    {
        var baseline = Set().Append(Check("q-vis-party")).ToArray();
        var better = baseline.Select(q => q.Answered ? Hit(q) : q).ToArray();
        Assert.Equal("PASS", ParityGate.Evaluate(Run(baseline), Run(better), Fast).Verdict);

        // The candidate returns Sam's private party to Alex where the baseline did not: a fail beside a proven gain.
        GateQuery[] Revealing(IEnumerable<GateQuery> set, string[]? leaks = null, string[]? exposed = null, bool gap = true) =>
            [.. set.Select(q => q.Id == "q-vis-party" ? Check(q.Id, leaks, exposed, gap) : q)];
        var leaked = ParityGate.Evaluate(Run(baseline), Run(Revealing(better, leaks: ["mem-sam-party"])), Fast);
        Assert.Equal("FAIL", leaked.Verdict);
        Assert.Equal("fail", Rule(leaked, "privacy").Status);
        Assert.Contains("q-vis-party: mem-sam-party", Rule(leaked, "privacy").Detail);
        Assert.Equal("pass", Rule(leaked, "improvement").Status);
        // A record that is not returned but opens for the reader anyway counts the same.
        Assert.Equal("FAIL", ParityGate.Evaluate(Run(baseline), Run(Revealing(better, exposed: ["mem-sam-party"])), Fast).Verdict);

        // The same leak in the baseline, through the viewer scope the path lacks: inherited, and still never a pass.
        var inherited = ParityGate.Evaluate(Run(Revealing(baseline, leaks: ["mem-sam-party"])), Run(Revealing(better, leaks: ["mem-sam-party"])), Fast);
        Assert.Equal("INCONCLUSIVE", inherited.Verdict);
        Assert.Equal("inconclusive", Rule(inherited, "privacy").Status);
        Assert.Contains("a leaking path never passes", Rule(inherited, "privacy").Detail);
        // A second record revealed beside the inherited one is new.
        Assert.Equal("FAIL", ParityGate.Evaluate(Run(Revealing(baseline, leaks: ["mem-sam-party"])),
            Run(Revealing(better, leaks: ["mem-sam-party"], exposed: ["note-alex-coastpath"])), Fast).Verdict);

        // A path that claims the viewer scope and still reveals the record fails, inherited or not.
        var claimed = ParityGate.Evaluate(Run(Revealing(baseline, leaks: ["mem-sam-party"], gap: false)),
            Run(Revealing(better, leaks: ["mem-sam-party"], gap: false)), Fast);
        Assert.Equal("FAIL", claimed.Verdict);
        Assert.Contains("although the path claims the capability", Rule(claimed, "privacy").Detail);

        // With no locked baseline nothing is compared, but a breach still fails, and a gap leak is not a pass.
        var none = ParityGate.NoBaseline(Run(better), "no lock");
        Assert.Equal("FAIL", ParityGate.Evaluate(none, Run(Revealing(better, leaks: ["mem-sam-party"], gap: false)), Fast).Verdict);
        var unlocked = ParityGate.Evaluate(none, Run(Revealing(better, leaks: ["mem-sam-party"])), Fast);
        Assert.Equal("INCONCLUSIVE", unlocked.Verdict);
        Assert.Contains("with no baseline to compare", Rule(unlocked, "privacy").Detail);

        // A baseline on other data (another fingerprint) has no reveals to compare: a gap reveal is inconclusive, as
        // the data change itself is, and a breach still fails.
        var grown = new Dictionary<string, string> { ["corpus.json"] = "c2", ["golden-queries.json"] = "g2" };
        var renewed = ParityGate.Evaluate(Run(baseline), Run(Revealing(better, leaks: ["mem-sam-party"])) with { Fingerprint = grown }, Fast);
        Assert.Equal("INCONCLUSIVE", renewed.Verdict);
        Assert.Contains("with no baseline on this data to compare", Rule(renewed, "privacy").Detail);
        Assert.Equal("FAIL", ParityGate.Evaluate(Run(baseline), Run(Revealing(better, leaks: ["mem-sam-party"], gap: false)) with { Fingerprint = grown }, Fast).Verdict);
    }

    [Fact] public void Errors_fail_and_runs_that_differ_are_not_compared()
    {
        var baseline = Set();
        var better = Run(baseline.Select(Hit));
        // An errored baseline scores zeros and would flatter any candidate.
        var errored = Run(baseline.Select((q, i) => i == 0 ? q with { Status = "error", Recall = null, ReciprocalRank = null, Ndcg = null } : q));
        Assert.Equal("fail", Rule(ParityGate.Evaluate(errored, better, Fast), "errors").Status);
        Assert.Equal("FAIL", ParityGate.Evaluate(Run(baseline), errored, Fast).Verdict);

        void Incomparable(GateRun candidate, string why, GateOptions? options = null)
        {
            var result = ParityGate.Evaluate(Run(baseline), candidate, options ?? Fast);
            Assert.Equal("INCONCLUSIVE", result.Verdict);
            Assert.Contains(why, Rule(result, "comparable").Detail);
            Assert.DoesNotContain(result.Rules, r => r.Name is "power" or "regression" or "improvement");
        }
        Incomparable(better with { Mode = "copies" }, "load mode differs");
        Incomparable(better with { K = 10 }, "k differs");
        Incomparable(better with { Fingerprint = new Dictionary<string, string> { ["corpus.json"] = "c2", ["golden-queries.json"] = "g1" } }, "fingerprint");
        Incomparable(better with { Path = better.Path with { Queries = better.Path.Queries[1..] } }, "query sets differ");
        Incomparable(better with { Path = better.Path with { Queries = [better.Path.Queries[0], .. better.Path.Queries] } }, "duplicate query ids");
        Incomparable(better with { Path = better.Path with { Queries = [.. better.Path.Queries.Select((q, i) => i == 5 ? q with { Cluster = "elsewhere" } : q)] } }, "cluster differs for q005");
        Incomparable(better with { Path = better.Path with { Unavailable = "embeddings off" } }, "the candidate did not run");
        Incomparable(better with { Model = "bge-small" }, "embedding model differs");
        // A model change on purpose (SB-P2-06) is compared, and says so.
        var swapped = ParityGate.Evaluate(Run(baseline), better with { Model = "bge-small" }, Fast with { ChangesModel = true });
        Assert.Equal("PASS", swapped.Verdict);
        Assert.Contains("changes the embedding model on purpose", Rule(swapped, "comparable").Detail);
    }

    [Fact] public void A_target_type_gain_needs_its_own_clusters()
    {
        var baseline = Set();
        // Four of t5's seven misses fixed: +0.2 in t5, under 0.02 overall.
        var fixedIds = baseline.Where(q => q.Type == "t5" && q.Recall == 0).Take(4).Select(q => q.Id).ToHashSet();
        var candidate = baseline.Select(q => fixedIds.Contains(q.Id) ? Hit(q) : q).ToArray();
        var untargeted = ParityGate.Evaluate(Run(baseline), Run(candidate), Fast);
        Assert.NotEqual("PASS", untargeted.Verdict);
        Assert.NotEqual("pass", Rule(untargeted, "improvement").Status);
        var targeted = ParityGate.Evaluate(Run(baseline), Run(candidate), Fast with { TargetTypes = ["t5"] });
        Assert.Equal("PASS", targeted.Verdict);
        Assert.Contains("t5 recall +0.2000", Rule(targeted, "improvement").Detail);
        // The same gain with t5's twenty queries in five clusters cannot be proven.
        GateQuery Clump(GateQuery q) => q.Type == "t5" ? q with { Cluster = "t5-" + int.Parse(q.Id[1..]) / 12 % 5 } : q;
        var clumped = ParityGate.Evaluate(Run(baseline.Select(Clump)), Run(candidate.Select(Clump)), Fast with { TargetTypes = ["t5"] });
        Assert.Equal("INCONCLUSIVE", clumped.Verdict);
        Assert.Contains("t5 has 20 judged queries in 5 clusters", Rule(clumped, "improvement").Detail);
    }

    [Fact] public void A_candidate_over_the_latency_budget_fails()
    {
        var baseline = Set();
        var slow = ParityGate.Evaluate(Run(baseline), Run(baseline.Select(Hit), p95: 650), Fast);
        Assert.Equal("FAIL", slow.Verdict);
        Assert.Contains("650.0 ms, budget 500 ms", Rule(slow, "latency").Detail);
        // A reranker's budget (D-029) is a threshold the candidate's own run declares.
        Assert.Equal("PASS", ParityGate.Evaluate(Run(baseline), Run(baseline.Select(Hit), p95: 650),
            new(new GateThresholds { Iterations = 2000, LatencyMsP95 = 800 })).Verdict);
    }

    [Fact] public void The_paired_bootstrap_matches_worked_examples()
    {
        // No spread: the interval is the point, and nothing is uncertain.
        var flat = GateBootstrap.Paired([0.25, 0.25, 0.25, 0.25], ["a", "b", "c", "d"]);
        Assert.Equal((0.25, 0.25, 0.25, 0.0, 4, 4), (flat.Mean, Math.Round(flat.Lo, 12), Math.Round(flat.Hi, 12), flat.Se, flat.N, flat.Clusters));
        // Clusters are drawn whole, and the mean weighs every query: three gains in cluster a, one loss in b. A draw
        // is a,a (1), a,b or b,a (0.5) or b,b (-1), so the 95% interval spans -1 to 1.
        var weighted = GateBootstrap.Paired([1, 1, 1, -1], ["a", "a", "a", "b"]);
        Assert.Equal((0.5, -1.0, 1.0, 2), (weighted.Mean, weighted.Lo, weighted.Hi, weighted.Clusters));
        Assert.Equal(GateBootstrap.Paired([], []), new GateInterval(0, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => GateBootstrap.Paired([1], []));

        // A fixed seed: the same interval every run, and whatever order the queries come in.
        var deltas = Enumerable.Range(0, 50).Select(i => (i % 7 - 3) / 10.0).ToArray();
        var clusters = Enumerable.Range(0, 50).Select(i => "c" + i / 5).ToArray();
        var first = GateBootstrap.Paired(deltas, clusters);
        Assert.Equal(first, GateBootstrap.Paired(deltas, clusters));
        var reversed = GateBootstrap.Paired([.. deltas.Reverse()], [.. clusters.Reverse()]);
        Assert.Equal(first.Lo, reversed.Lo, 12);
        Assert.Equal(first.Hi, reversed.Hi, 12);
        Assert.InRange(first.Mean, first.Lo, first.Hi);
        Assert.Equal(10, first.Clusters);

        // Over many synthetic comparisons with a known true gain, the interval holds it about 95% of the time.
        var random = new Random(7);
        var covered = 0;
        for (var trial = 0; trial < 200; trial++)
        {
            var d = Enumerable.Range(0, 120).Select(_ => 0.05 + (random.NextDouble() - 0.5)).ToArray();
            var interval = GateBootstrap.Paired(d, [.. Enumerable.Range(0, 120).Select(i => "c" + i / 3)], 2000, (ulong)trial + 1);
            if (interval.Lo <= 0.05 && 0.05 <= interval.Hi) covered++;
        }
        Assert.InRange(covered, 176, 198);
    }

    [Fact] public void Clusters_are_the_golden_sets_own_keys()
    {
        ParityGolden Golden(params ParityQuery[] queries) => new("test", "DEMO", 5, new() { ["t"] = "test" }, new(), queries);
        var golden = Golden(
            new("a", "t", "alex", "x", [new("r1", 2)]),
            new("b", "t", "alex", "x", [new("r2", 1), new("r1", 2)]),
            new("c", "t", "alex", "x", [new("r2", 1)]),
            new("d", "t", "sam", "x", [], ["r3"], ["viewer-scope"]),
            new("e", "t", "sam", "x", [new("r4", 2)], WrongAtTime: ["r5"], Cluster: "r5"));
        var clusters = ParityGate.Clusters(golden);
        // The first grade-2 record, else the first relevant, else the first forbidden one; a stated cluster wins.
        Assert.Equal(["r1", "r1", "r2", "r3", "r5"], golden.Queries.Select(q => clusters[q.Id]));

        // Every golden query gets one, and they are the clusters the fixture's own size test counts.
        var real = ParityGolden.Load();
        var realClusters = ParityGate.Clusters(real);
        Assert.Equal(real.Queries.Select(q => q.Id).Order(), realClusters.Keys.Order());
        Assert.Equal(real.Queries.Select(q => q.ClusterKey).Distinct().Count(), realClusters.Values.Distinct().Count());
    }

    [Fact] public void A_run_becomes_gate_queries_naming_corpus_records_only()
    {
        var path = new ParityPath("graph", "explore_graph", "test", new HashSet<string> { "validity" }, null);
        var visibility = new ParityQuery("q-vis", "visibility", "alex", "x", [], ["mem-sam-party"], ["viewer-scope"]);
        var current = new ParityQuery("q-cur", "current", "alex", "x", [new("mem-alex-job-new", 2)], ["mem-alex-job-old"], ["validity"]);
        var p = ParityGate.FromRuns(path,
        [
            (visibility, new ParityRun("expected-gap", "exposed", ["?0f3a", .. Enumerable.Range(0, 12).Select(i => $"r{i}")], 3, null, null, null, null,
                [], ["mem-sam-party"], ["viewer-scope"], [], null)),
            (current, new ParityRun("leak", "leak", ["mem-alex-job-old", "mem-alex-job-new"], 5, 1, 0.5, 0.6309297535714575, 2,
                ["mem-alex-job-old"], [], [], [], null)),
        ], new Dictionary<string, string> { ["q-vis"] = "a", ["q-cur"] = "b" });
        Assert.Equal(["validity"], p.Supports);
        Assert.Equal(5, p.LatencyMsP95);
        // An engine id the corpus does not know is kept as "?" alone, and the ranking is cut at ten.
        Assert.Equal(["?", "r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7", "r8"], p.Queries[0].Top);
        Assert.True(p.Queries[0].MustHide);
        Assert.Equal(["mem-sam-party"], p.Queries[0].Revealed);
        // A replaced fact under "current" is a ranking error, not a must-hide leak.
        Assert.False(p.Queries[1].MustHide);
        Assert.Empty(p.Queries[1].Revealed);
        // Scores are kept to six places.
        Assert.Equal((1.0, 0.5, 0.63093), (p.Queries[1].Recall!.Value, p.Queries[1].ReciprocalRank!.Value, p.Queries[1].Ndcg!.Value));
    }

    [Fact] public void A_lock_reads_back_and_the_tripwire_names_what_moved()
    {
        var set = Set(types: 2, perType: 3);
        var path = new GatePath("fulltext", "search_knowledge", "test", [], null, 4.2, set);
        var first = ParityGate.NewLock("installed", 5, Data, "minilm", [path], "the first lock's reason", new DateOnly(2026, 10, 31), null);
        var text = ParityGate.WriteLock(first);
        var back = ParityGate.ReadLock(text);
        Assert.Equal(text, ParityGate.WriteLock(back));
        // One query per line, no latency, and the history says why.
        Assert.Equal(set.Length, text.Split('\n').Count(line => line.TrimStart().StartsWith("{\"id\":", StringComparison.Ordinal)));
        Assert.Null(back.Paths.Single().LatencyMsP95);
        Assert.Equal(("2026-10-31", "the first lock's reason", 6), (back.History.Single().Date, back.History.Single().Reason, back.History.Single().Paths["fulltext"].Judged));
        Assert.Throws<InvalidDataException>(() => ParityGate.ReadLock(text.Replace(ParityGate.LockSchema, "rhythm-parity-gate-baseline.v0")));

        var locked = back.Run("fulltext")!;
        var fresh = new GateRun("installed", 5, Data, "minilm", path);
        Assert.False(ParityGate.Diff(locked, fresh).Moved);
        Assert.Null(back.Run("semantic"));
        // A re-ranked query, a new query and a changed model are each a move.
        var reranked = path with { Queries = [.. set.Select((q, i) => i == 1 ? q with { Top = ["r9", .. q.Top], FirstRelevantRank = 2, ReciprocalRank = 0.5 } : q)] };
        Assert.Equal([set[1].Id], ParityGate.Diff(locked, fresh with { Path = reranked }).Changed);
        var added = ParityGate.Diff(locked, fresh with { Path = path with { Queries = [.. set, set[0] with { Id = "q-new" }] } });
        Assert.Equal(["q-new"], added.Extra);
        Assert.True(added.Moved);
        Assert.True(ParityGate.Diff(locked, fresh with { Model = "bge-small" }).ModelChanged);

        // A later lock keeps the earlier ones in its history.
        var second = ParityGate.NewLock("installed", 5, Data, "minilm", [reranked], "the second", new DateOnly(2026, 11, 2), back);
        Assert.Equal(["the first lock's reason", "the second"], second.History.Select(h => h.Reason));
        // A run that errored, or that reveals a must-hide record the lock did not, never becomes a lock.
        Assert.Empty(ParityGate.LockRefusals([path], back));
        Assert.Contains("query error", ParityGate.LockRefusals([path with { Queries = [.. set.Select((q, i) => i == 0 ? q with { Status = "error" } : q)] }], back).Single());
        var withCheck = ParityGate.NewLock("installed", 5, Data, "minilm", [path with { Queries = [.. set, Check("q-vis")] }], "with a check", new DateOnly(2026, 11, 3), null);
        var leaking = path with { Queries = [.. set, Check("q-vis", leaks: ["mem-sam-party"])] };
        Assert.Contains("the locked baseline did not", ParityGate.LockRefusals([leaking], withCheck).Single());
        Assert.Contains("claims the capability", ParityGate.LockRefusals([path with { Queries = [.. set, Check("q-vis", leaks: ["mem-sam-party"], gap: false)] }], null).Single());
        // On new data (another fingerprint) the old lock's reveals are not compared: the corpus may have gained the record.
        var grown = new Dictionary<string, string> { ["corpus.json"] = "c2", ["golden-queries.json"] = "g2" };
        Assert.Contains("the locked baseline did not", ParityGate.LockRefusals([leaking], withCheck, Data).Single());
        Assert.Empty(ParityGate.LockRefusals([leaking], withCheck, grown));
        Assert.Contains("claims the capability", ParityGate.LockRefusals([path with { Queries = [.. set, Check("q-vis", leaks: ["mem-sam-party"], gap: false)] }], withCheck, grown).Single());
    }

    // Privacy guards on the checked-in baselines: present for both load modes, whole (no error, nothing missing), and
    // holding nothing but corpus ids, scores and the harness's own words, so no text, address or path can leak into git
    // through a re-lock. Whether they still match the golden data is the opt-in run's tripwire, since a fixture change
    // must be able to land before its re-lock.
    [Fact] public void The_checked_in_baselines_are_synthetic_and_whole()
    {
        var corpus = ParityCorpus.Load();
        var golden = ParityGolden.Load();
        var records = corpus.ScopeOf.Keys.Concat(corpus.Projects.Select(p => "project:" + p.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var mode in new[] { "copies", "installed" })
        {
            var file = Path.Combine(ParityCorpus.Folder, ParityGate.LockFile(mode));
            Assert.True(File.Exists(file), $"{ParityGate.LockFile(mode)} is missing: lock one as baseline.md section 6 says");
            var text = File.ReadAllText(file);
            var l = ParityGate.ReadLock(text);
            Assert.StartsWith("DEMO", l.Label);
            Assert.Equal((mode, golden.K), (l.Mode, l.K));
            Assert.NotNull(l.Model);
            Assert.Equal(["corpus.json", "golden-queries.json"], l.Fingerprint.Keys.Order(StringComparer.Ordinal));
            Assert.NotEmpty(l.History);
            Assert.All(l.History, h => Assert.False(string.IsNullOrWhiteSpace(h.Reason)));
            Assert.Equal(l.Fingerprint, l.History[^1].Fingerprint);
            Assert.Equal(l.Paths.Length, l.Paths.Select(p => p.Name).Distinct().Count());
            Assert.All(l.Paths, p =>
            {
                Assert.Null(p.Unavailable);
                Assert.Null(p.LatencyMsP95);
                Assert.Equal(p.Queries.Length, p.Queries.Select(q => q.Id).Distinct().Count());
                Assert.All(p.Queries, q =>
                {
                    Assert.NotEqual("error", q.Status);
                    Assert.All(q.Top.Concat(q.Leaks).Concat(q.Exposed), id => Assert.Contains(id, records));
                    // A lock never makes a breach look inherited.
                    Assert.False(!q.Gap && q.Revealed.Length > 0, $"{mode} {p.Name} {q.Id} reveals {string.Join(", ", q.Revealed)} without a gap");
                });
            });
            Assert.DoesNotMatch(@"https?://|@|[A-Za-z]:[\\/]|/(Users|home)/|\\\\", text);
        }
    }

    // The comparisons within one opt-in run (ParityGateRun): combined retrieval against each arm alone, then the pairs
    // RHYTHM_PARITY_GATE_COMPARE names, each two different paths of the run.
    [Fact] public void Gate_comparisons_are_combined_against_each_arm_then_the_pairs_asked_for()
    {
        string[] paths = ["fulltext", "semantic", "graph", "combined", "combined:diversity"];
        Assert.Equal([("combined", "fulltext"), ("combined", "semantic")], ParityGate.Pairs(null, paths));
        Assert.Equal([("combined", "fulltext"), ("combined", "semantic"), ("combined:diversity", "combined")],
            ParityGate.Pairs("combined:diversity=combined, combined=fulltext", paths));
        Assert.Empty(ParityGate.Pairs("", ["fulltext", "semantic"]));
        foreach (var bad in new[] { "combined", "combined=combined", "combined=nothing", "a=b=c" })
            Assert.Throws<ArgumentException>(() => ParityGate.Pairs(bad, paths));
    }
}
