using System.Globalization;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// A test that needs every listed model's pinned files (each from the folder its variable names), skipped, saying so,
// without them: tests never download a model (D-041).
public sealed class ModelsFactAttribute : FactAttribute
{
    public ModelsFactAttribute(params string[] names)
    {
        foreach (var name in names)
        {
            var model = EmbeddingModels.Named(name) ?? throw new ArgumentException("Unknown model " + name);
            if (EmbeddingModels.Given(model) is not { } folder || !model.Files.All(f => File.Exists(Path.Combine(folder, f.Name))))
            {
                Skip = $"NOT RUN: needs the pinned {name} files ({string.Join(", ", model.Files.Select(f => f.Name))}) in the folder {model.Variable} names; tests never download a model (D-041).";
                return;
            }
        }
    }
}

// A database test that also needs the pinned model files: skipped, saying which is missing, without either (D-041).
public sealed class ModelsPostgresFactAttribute : FactAttribute
{
    public ModelsPostgresFactAttribute(params string[] names)
    {
        Skip = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB"))
            ? "NOT RUN: needs a disposable Postgres with pgvector; run sh engine/test-db.sh."
            : new ModelsFactAttribute(names).Skip;
    }
}

// SB-P2-13: each model's recorded thresholds (EmbeddingThresholds.Tuned) are what the rules written before the first run
// give on the labelled pairs, and each gives the precision and recall benchmarks.md records, at today's value and at the
// chosen one. Recomputed from the pinned model files; skipped without them (D-041).
public sealed class ThresholdTuningTests(ITestOutputHelper output)
{
    // Per kind: the counts (true positives, false positives, false negatives, true negatives) at today's threshold and at
    // the chosen one, as benchmarks.md gives them. Since run 19 over 193 pairs (SB-P4-03's 104 added), link on save over
    // the 155 pairs of two memories of one scope it compares (LinkPairs).
    static readonly Dictionary<string, Dictionary<string, (Confusion Today, Confusion Chosen)>> Recorded = new()
    {
        [EmbeddingModels.MiniLmId] = new()
        {
            ["similar"] = (new(49, 1, 68, 75), new(55, 3, 62, 73)),
            ["similarDocuments"] = (new(0, 0, 0, 2), new(0, 0, 0, 2)),
            ["mutual"] = (new(82, 31, 35, 45), new(82, 31, 35, 45)),
            ["linkOnSave"] = (new(28, 0, 65, 62), new(47, 0, 46, 62)),
            ["duplicateBlock"] = (new(6, 1, 7, 179), new(6, 0, 7, 180)),
            ["duplicateFlag"] = (new(16, 2, 10, 165), new(26, 21, 0, 146)),
            ["insight"] = (new(0, 0, 10, 77), new(2, 13, 8, 64)),
            ["standing"] = (new(6, 0, 84, 10179), new(15, 0, 75, 10179)),
            ["contradiction"] = (new(2, 0, 19, 108), new(16, 3, 5, 105)),
        },
        [EmbeddingModels.BgeSmall.Id] = new()
        {
            ["similar"] = (new(110, 48, 7, 28), new(94, 8, 23, 68)),
            ["similarDocuments"] = (new(0, 0, 0, 2), new(0, 0, 0, 2)),
            ["mutual"] = (new(117, 76, 0, 0), new(65, 3, 52, 73)),
            ["linkOnSave"] = (new(31, 0, 62, 62), new(56, 0, 37, 62)),
            ["duplicateBlock"] = (new(8, 1, 5, 179), new(7, 0, 6, 180)),
            ["duplicateFlag"] = (new(19, 5, 7, 162), new(26, 15, 0, 152)),
            ["insight"] = (new(0, 1, 10, 76), new(10, 77, 0, 0)),
            ["standing"] = (new(33, 9, 57, 10170), new(19, 0, 71, 10179)),
            ["contradiction"] = (new(4, 0, 17, 108), new(18, 17, 3, 91)),
        },
        [EmbeddingModels.BgeBase.Id] = new()
        {
            ["similar"] = (new(102, 13, 15, 63), new(93, 4, 24, 72)),
            ["similarDocuments"] = (new(0, 0, 0, 2), new(0, 0, 0, 2)),
            ["mutual"] = (new(116, 75, 1, 1), new(77, 2, 40, 74)),
            ["linkOnSave"] = (new(32, 0, 61, 62), new(53, 0, 40, 62)),
            ["duplicateBlock"] = (new(7, 1, 6, 179), new(7, 0, 6, 180)),
            ["duplicateFlag"] = (new(20, 5, 6, 162), new(24, 9, 2, 158)),
            ["insight"] = (new(0, 1, 10, 76), new(2, 5, 8, 72)),
            ["standing"] = (new(22, 1, 68, 10178), new(17, 0, 73, 10179)),
            ["contradiction"] = (new(4, 0, 17, 108), new(20, 18, 1, 90)),
        },
    };

    static string N(double? x) => x is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "–";

    [ModelsFact("minilm")] public void MiniLMs_thresholds_are_what_the_labelled_pairs_give() => Check(EmbeddingModels.MiniLm, null);

    [ModelsFact("minilm", "bge-small")] public void Bge_smalls_thresholds_are_what_the_labelled_pairs_give()
    {
        using var minilm = new Embeddings();
        Assert.True(minilm.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        Check(EmbeddingModels.BgeSmall, ThresholdTuning.Corpus(minilm.Model!, ParityCorpus.Load(), ParityPairs.Load()));
    }

    // bge-base-en-v1.5 (D-249): its thresholds by the same rules, recorded before the benchmark read them. Its link on save
    // and standing values rest on pairs labelled where MiniLM and bge-small scored them, so unlike theirs no test shows
    // every pair above its floor labelled.
    [ModelsFact("minilm", "bge-base")] public void Bge_bases_thresholds_are_what_the_labelled_pairs_give()
    {
        using var minilm = new Embeddings();
        Assert.True(minilm.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        Check(EmbeddingModels.BgeBase, ThresholdTuning.Corpus(minilm.Model!, ParityCorpus.Load(), ParityPairs.Load()));
    }

    // SB-P5-12: the standing threshold rests on the standing cases, not on one memory's two questions. Every pair no case
    // wrote that scores at or above where its reading started is labelled by meaning, so the negatives above the chosen
    // value are the whole of the corpus's, not a sample; no negative reaches it (the rule's own promise, on the recorded
    // value); the corpus's own standing memory fires for no golden query but those that name it relevant; every other golden
    // query that reaches the value is a fire or the instruction's subject asked another way (intent), counted here and in
    // benchmarks.md, as are the intent questions of the cases that fire. Today's value and the chosen one, in that order.
    static readonly Dictionary<string, (double Floor, int IntentToday, int IntentChosen, int Instructions, int GoldenIntent)> StandingRecorded = new()
    {
        [EmbeddingModels.MiniLmId] = (0.50, 2, 10, 7, 5),
        [EmbeddingModels.BgeSmall.Id] = (0.65, 19, 5, 8, 4),
    };

    [ModelsFact("minilm")] public void MiniLMs_standing_threshold_fires_only_for_what_is_labelled() => StandingLabelled(EmbeddingModels.MiniLm);
    [ModelsFact("bge-small")] public void Bge_smalls_standing_threshold_fires_only_for_what_is_labelled() => StandingLabelled(EmbeddingModels.BgeSmall);

    void StandingLabelled(EmbeddingModel spec)
    {
        using var embeddings = new Embeddings(spec);
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(spec)!), $"the pinned {spec.Name} files did not verify");
        var (cases, golden) = (ParityStanding.Load(), ParityGolden.Load());
        var points = ThresholdTuning.Standing(embeddings.Model!, ParityCorpus.Load(), golden, cases);
        var recorded = StandingRecorded[spec.Id];
        var chosen = EmbeddingThresholds.Tuned[spec.Id].Standing;
        // Read above the floor: a pair no case wrote is listed under also, or a golden query names the memory relevant.
        var labels = cases.Also.ToDictionary(a => (a.Instruction, a.Golden ?? a.Query!), a => a.Label);
        Assert.True(chosen >= recorded.Floor, $"{spec.Name}: the standing threshold {chosen} is below where its reading started");
        Assert.All(points.Where(p => p.Kind is "other" or "golden" && p.Positive != true && p.Score >= recorded.Floor),
            p => Assert.True(labels.ContainsKey((p.Instruction, p.Question)), $"{p.Instruction} and {p.Question} score {p.Score} and are not labelled"));
        // No negative reaches the chosen value.
        Assert.DoesNotContain(points, p => p.Positive == false && p.Score >= chosen);
        // The corpus's own standing memory, for no golden query but those that name it relevant.
        var relevant = golden.Queries.Where(q => q.Relevant.Any(r => r.Id == "mem-alex-standing")).Select(q => q.Id).ToHashSet();
        Assert.Subset(relevant, points.Where(p => p.Instruction == "mem-alex-standing" && p.Kind == "golden" && p.Score >= chosen).Select(p => p.Question).ToHashSet());
        // The intent questions that fire, at today's value and the chosen one, and the instructions that fire at all.
        var fired = points.Where(p => p.Score >= chosen).ToArray();
        output.WriteLine($"{spec.Name}: {fired.Count(p => p.Positive == true)} fires over {fired.Where(p => p.Positive == true).Select(p => p.Instruction).Distinct().Count()} of {cases.Instructions.Length} instructions at {chosen:0.00}");
        foreach (var p in fired.Where(p => p.Positive != true && p.Kind == "golden")) output.WriteLine($"  intent {p.Score:0.0000} {p.Instruction} {p.Question}");
        Assert.Equal(recorded.IntentToday, points.Count(p => p.Positive is null && p.Score >= EmbeddingThresholds.Today.Standing));
        Assert.Equal(recorded.IntentChosen, fired.Count(p => p.Positive is null));
        Assert.Equal(recorded.Instructions, fired.Where(p => p.Positive == true).Select(p => p.Instruction).Distinct().Count());
        Assert.Equal(recorded.GoldenIntent, fired.Count(p => p.Kind == "golden" && p.Positive is null));
    }

    // The engine's own check reads what the thresholds were tuned on: for every written question of every instruction, the
    // score StandingCheck gives is the one recorded, and it answers at the threshold exactly as the count does.
    [ModelsFact("minilm")] public void The_engines_standing_score_is_the_one_tuned()
    {
        using var embeddings = new Embeddings();
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        var model = embeddings.Model!;
        var (cases, corpus) = (ParityStanding.Load(), ParityCorpus.Load());
        var points = ThresholdTuning.Standing(model, corpus, ParityGolden.Load(), cases);
        var ids = cases.Instructions.ToDictionary(i => i.Id, _ => Guid.NewGuid());
        // A score below 0 is one no threshold reads (the grid starts at 0.20), and the engine's stops at 0.
        foreach (var p in points.Where(p => p.Kind is "fire" or "near" or "intent" && p.Score >= 0))
        {
            var i = cases.Instructions.Single(x => x.Id == p.Instruction);
            var words = i.Text ?? corpus.Memories.Single(m => m.Id == i.Id).Text;
            var at = new StandingCheck(model, p.Score).Scored(p.Question, [(ids[i.Id], 1, words, DateTimeOffset.UnixEpoch)], default);
            Assert.Equal(p.Score, Assert.Single(at).Score);
            Assert.Empty(new StandingCheck(model, p.Score + 0.0001).Scored(p.Question, [(ids[i.Id], 1, words, DateTimeOffset.UnixEpoch)], default));
        }
    }

    // SB-P4-03 (run 19): every pair a link on save compares in the corpus that scores at or above where its labelling
    // started (0.43 under MiniLM, 0.62 under bge-small: MiniLM's link on save threshold before it, and bge-small's) is
    // labelled, so the precision link on save records at its chosen threshold, above both, is over every such pair of
    // the corpus rather than a sample; and none at or above it is unrelated.
    static readonly Dictionary<string, double> LabelledFrom = new() { [EmbeddingModels.MiniLmId] = 0.43, [EmbeddingModels.BgeSmall.Id] = 0.62 };

    [ModelsFact("minilm")] public void Every_pair_a_save_compares_above_MiniLMs_floor_is_labelled() => Labelled(EmbeddingModels.MiniLm);
    [ModelsFact("bge-small")] public void Every_pair_a_save_compares_above_bge_smalls_floor_is_labelled() => Labelled(EmbeddingModels.BgeSmall);

    void Labelled(EmbeddingModel spec)
    {
        using var embeddings = new Embeddings(spec);
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(spec)!), $"the pinned {spec.Name} files did not verify");
        var pairs = ParityPairs.Load();
        var labels = pairs.Pairs.ToDictionary(p => (p.A, p.B), p => p.Label);
        var all = ThresholdTuning.SavePairs(embeddings.Model!, ParityCorpus.Load(), pairs);
        var floor = LabelledFrom[spec.Id];
        var chosen = EmbeddingThresholds.Tuned[spec.Id].LinkOnSave;
        Assert.True(chosen >= floor, $"{spec.Name}: link on save's threshold {chosen} is below the floor its labels were taken from");
        var above = all.Where(p => p.Score >= floor).ToArray();
        output.WriteLine($"{spec.Name}: {all.Count} pairs a save compares, {above.Length} at or above {floor:0.00}, {all.Count(p => p.Score >= chosen)} at or above {chosen:0.00}");
        Assert.All(above, p => Assert.True(labels.ContainsKey((p.A, p.B)), $"{p.A} and {p.B} score {p.Score} and are not labelled"));
        Assert.All(all.Where(p => p.Score >= chosen), p => Assert.NotEqual("unrelated", labels[(p.A, p.B)]));
    }

    void Check(EmbeddingModel spec, double[]? reference)
    {
        using var embeddings = new Embeddings(spec);
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(spec)!), $"the pinned {spec.Name} files did not verify");
        var tuned = ThresholdTuning.Tune(embeddings.Model!, ParityCorpus.Load(), ParityPairs.Load(), ParityGolden.Load(), reference);
        foreach (var t in tuned)
            output.WriteLine($"{t.Kind,-16} {t.Rule,-19} today {t.Today:0.00}: P {N(t.AtToday.Precision)} R {N(t.AtToday.Recall)} ({t.AtToday.TruePositives}, {t.AtToday.FalsePositives}, {t.AtToday.FalseNegatives}, {t.AtToday.TrueNegatives})"
                + $" | chosen {t.Chosen:0.00}: P {N(t.AtChosen.Precision)} R {N(t.AtChosen.Recall)} ({t.AtChosen.TruePositives}, {t.AtChosen.FalsePositives}, {t.AtChosen.FalseNegatives}, {t.AtChosen.TrueNegatives})");
        // How many of the corpus's pairs each map threshold lets through, today and chosen: the labelled pairs over-represent
        // pairs shown together, so this is the scale the map works at (information, not a rule).
        var corpus = ThresholdTuning.Corpus(embeddings.Model!, ParityCorpus.Load(), ParityPairs.Load());
        foreach (var t in tuned.Where(t => t.Kind is "similar" or "similarDocuments" or "mutual"))
            output.WriteLine($"{t.Kind}: {corpus.Count(x => x >= t.Today)} of {corpus.Length} corpus pairs at or above today's {t.Today:0.00}, "
                + $"{corpus.Count(x => x >= t.Chosen)} at or above {t.Chosen:0.00}; median {ParityMetrics.Percentile(corpus, 50):0.00}, 99th percentile {ParityMetrics.Percentile(corpus, 99):0.00}");
        // And how often the map's cap of three neighbours a star, not the threshold, decides a star's links: the stored
        // records whose third closest scores at or above the similar threshold, today's and chosen.
        var members = ThresholdTuning.MapVectors(embeddings.Model!, ParityCorpus.Load(), ParityPairs.Load());
        var third = members.Select((v, i) => members.Where((_, j) => j != i).Select(w => ThresholdTuning.Score(v, w)).OrderDescending().ElementAt(2)).ToArray();
        var similar = tuned.Single(t => t.Kind == "similar");
        output.WriteLine($"third closest at or above {similar.Today:0.00}: {third.Count(x => x >= similar.Today)} of {third.Length}; at or above {similar.Chosen:0.00}: {third.Count(x => x >= similar.Chosen)}");
        Assert.Equal(ThresholdTuning.Kinds, tuned.Select(t => t.Kind));
        Assert.Equal(EmbeddingThresholds.Today, new SimilarityThresholds(tuned[0].Today, tuned[1].Today, tuned[2].Today, tuned[3].Today,
            tuned[4].Today, tuned[5].Today, tuned[6].Today, tuned[7].Today, tuned[8].Today));
        // The recorded thresholds are the rules' own, and so are the counts benchmarks.md gives.
        Assert.Equal(EmbeddingThresholds.Tuned[spec.Id], ThresholdTuning.Thresholds(tuned));
        Assert.All(tuned, t => Assert.Equal(Recorded[spec.Id][t.Kind], (t.AtToday, t.AtChosen)));
    }
}

// SB-P5-05: the signals the insight candidate pass reads, and those it does not, counted on the labelled pairs it can see
// (InsightSignals: pairs.json's and insight-pairs.json's pairs of two eligible memories of one scope, 30 or more days
// apart; 8 insight, 50 related or unrelated since run 19 joined the two files), as benchmarks.md records them. The counts are (true positives, false
// positives, false negatives, true negatives). Those that need no model run always; similar and the pass's rule are
// recomputed from the pinned model files, skipped without them (D-041).
public sealed class InsightSignalTests(ITestOutputHelper output)
{
    static readonly Dictionary<string, Confusion> WithoutModel = new()
    {
        ["weighty"] = new(7, 12, 1, 38),
        ["sharedTag"] = new(2, 32, 6, 18),
        ["sameProject"] = new(2, 13, 6, 37),
        ["rareWord"] = new(5, 23, 3, 27),
        ["floor"] = new(6, 42, 2, 8),
    };

    // Per model: similar and the rule (similar or weighty) at today's threshold (0.80) and at the model's tuned one.
    static readonly Dictionary<string, Dictionary<string, Confusion>> WithModel = new()
    {
        [EmbeddingModels.MiniLmId] = new()
        {
            ["similar today"] = new(0, 0, 8, 50), ["rule today"] = new(7, 12, 1, 38),
            ["similar tuned"] = new(1, 5, 7, 45), ["rule tuned"] = new(8, 17, 0, 33),
        },
        [EmbeddingModels.BgeSmall.Id] = new()
        {
            ["similar today"] = new(0, 0, 8, 50), ["rule today"] = new(7, 12, 1, 38),
            ["similar tuned"] = new(8, 50, 0, 0), ["rule tuned"] = new(8, 50, 0, 0),
        },
    };

    static string N(double? x) => x is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "–";
    void Show(string name, Confusion c) => output.WriteLine($"{name,-14} P {N(c.Precision)} R {N(c.Recall)} balanced {c.Balanced:0.000} ({c.TruePositives}, {c.FalsePositives}, {c.FalseNegatives}, {c.TrueNegatives})");

    static IReadOnlyList<InsightLabelled> Labelled(ParityCorpus corpus) => InsightSignals.Pairs(corpus, ParityPairs.Load(), ParityPairs.Insight());

    [Fact] public void The_signals_that_need_no_model_are_what_the_labelled_pairs_give()
    {
        var corpus = ParityCorpus.Load();
        var pairs = Labelled(corpus);
        var counted = InsightSignals.WithoutModel(corpus).ToDictionary(s => s.Key, s => InsightSignals.Count(pairs, s.Value));
        foreach (var (name, c) in counted) Show(name, c);
        Assert.Equal((8, 50), (pairs.Count(p => p.Positive), pairs.Count(p => !p.Positive)));
        Assert.Equal(WithoutModel, counted);
    }

    [ModelsFact("minilm")] public void MiniLMs_insight_signals_are_what_the_labelled_pairs_give() => Check(EmbeddingModels.MiniLm);

    [ModelsFact("bge-small")] public void Bge_smalls_insight_signals_are_what_the_labelled_pairs_give() => Check(EmbeddingModels.BgeSmall);

    void Check(EmbeddingModel spec)
    {
        using var embeddings = new Embeddings(spec);
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(spec)!), $"the pinned {spec.Name} files did not verify");
        var pairs = Labelled(ParityCorpus.Load());
        var scores = InsightSignals.Similarities(embeddings.Model!, pairs);
        foreach (var p in pairs.OrderBy(p => p.Label).ThenByDescending(p => scores[p.Id]))
            output.WriteLine($"{p.Id,-26} {p.Label,-9} {scores[p.Id]:0.0000} importance {p.Older.Importance}/{p.Newer.Importance}");
        var counted = new Dictionary<string, Confusion>();
        foreach (var (when, threshold) in new[] { ("today", EmbeddingThresholds.Today.Insight), ("tuned", EmbeddingThresholds.Tuned[spec.Id].Insight) })
        {
            counted[$"similar {when}"] = InsightSignals.Count(pairs, p => InsightSignals.Similar(p, scores[p.Id], threshold));
            counted[$"rule {when}"] = InsightSignals.Count(pairs, p => InsightSignals.Rule(p, scores[p.Id], threshold));
        }
        foreach (var (name, c) in counted) Show(name, c);
        Pass(spec, embeddings.Model!);
        Assert.Equal(WithModel[spec.Id], counted);
    }

    // The whole pass over the corpus's eligible memories of Alex, Sam and Family (one scope at a time, each memory with its
    // best few, InsightCandidates.Of): how many candidates it keeps, and how many of the labelled pairs among them, at
    // today's threshold and the model's tuned one, at the pass's own PerSeed and at others for comparison (printed).
    static readonly Dictionary<string, Dictionary<string, (int Candidates, int Insight, int Other)>> Passes = new()
    {
        [EmbeddingModels.MiniLmId] = new() { ["today"] = (86, 6, 9), ["tuned"] = (91, 7, 13) },
        [EmbeddingModels.BgeSmall.Id] = new() { ["today"] = (86, 6, 6), ["tuned"] = (206, 6, 20) },
    };

    void Pass(EmbeddingModel spec, IEmbedder model)
    {
        var corpus = ParityCorpus.Load();
        var labelled = Labelled(corpus).ToDictionary(p => (p.Older.Id, p.Newer.Id), p => p.Positive);
        var ids = corpus.Memories.ToDictionary(m => m.Id, _ => Guid.NewGuid(), StringComparer.Ordinal);
        var names = ids.ToDictionary(p => p.Value, p => p.Key);
        var vectors = new Dictionary<Guid, float[]?>();
        float[]? Vector(InsightMemory m) => vectors.TryGetValue(m.Id, out var v) ? v : vectors[m.Id] = InsightCandidates.Vector(model, m.Text);
        var scopes = new[] { "personal:alex", "personal:sam", "family" }.Select(scope => corpus.Memories
            .Where(m => m.Scope == scope && InsightSignals.Eligible(corpus, m)).OrderBy(m => m.CreatedAt).ThenBy(m => ids[m.Id].ToString(), StringComparer.Ordinal)
            .Select(m => new InsightMemory(ids[m.Id], 1, m.CreatedAt, m.Importance, m.Volatility, m.Tags, null, m.Text, m.Text.Length)).ToList()).ToArray();
        var counted = new Dictionary<string, (int, int, int)>();
        foreach (var (when, threshold) in new[] { ("today", EmbeddingThresholds.Today.Insight), ("tuned", EmbeddingThresholds.Tuned[spec.Id].Insight) })
            foreach (var perSeed in new[] { 1, 2, InsightCandidates.PerSeed, 5, 10 })
            {
                var found = scopes.SelectMany(memories => memories.SelectMany(seed => InsightCandidates.Of(seed, memories, Vector, threshold, InsightCandidates.Partners, perSeed)))
                    .Select(p => (names[p.Older.Id], names[p.Newer.Id])).ToArray();
                var result = (found.Length, found.Count(p => labelled.TryGetValue(p, out var positive) && positive), found.Count(p => labelled.TryGetValue(p, out var positive) && !positive));
                output.WriteLine($"{when} at {threshold:0.00}, {perSeed} a memory: {result.Item1} candidates, {result.Item2} of 8 labelled insight pairs, {result.Item3} of 50 labelled others");
                if (perSeed == InsightCandidates.PerSeed) counted[when] = result;
            }
        Assert.Equal(Passes[spec.Id], counted);
    }
}
