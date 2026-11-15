using Knowledge.Relationships;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// SB-P2-13's settings: each model's re-tuned thresholds are recorded (EmbeddingThresholds.Tuned) and reached only through
// adapters.embeddings.thresholds `tuned`, off by default, or a live model other than MiniLM, which the settings refuse
// (D-025). Off, the map reads graph.similarity's numbers exactly as before. No model files needed.
public sealed class EmbeddingThresholdTests
{
    static RhythmConfig Settings(string extra = "") => RhythmConfig.Parse(TestSettings.Yaml + "\n" + extra);
    static RhythmConfig Embeddings(string lines) => Settings("adapters:\n  embeddings:\n" + string.Concat(lines.Split('\n').Select(l => "    " + l + "\n")));
    static readonly Guid Principal = Guid.NewGuid();
    static RelationshipHostConfiguration Map(RhythmConfig config) => RelationshipHostConfiguration.From(config, Principal, Path.GetTempPath());
    // Everything about the map but its catalog (a list, compared by reference) and its settings' fingerprint.
    static object Shape(RelationshipHostConfiguration c) => (c.Principal, c.RefreshStateFile, c.RefreshSeconds, c.FreshnessSeconds, c.MaxMemories,
        c.MaxDocuments, c.GraphNodeLimit, c.SimilarityNeighbors, c.SimilarityThreshold, c.SimilarityDocumentThreshold, c.SimilarityMutualThreshold);

    [Fact] public void Todays_thresholds_are_the_maps_defaults_and_D028s_starting_points()
    {
        var map = new RhythmConfig.SimilaritySection();
        var today = EmbeddingThresholds.Today;
        Assert.Equal((map.Threshold, map.DocumentThreshold, map.MutualThreshold), ((double?)today.Similar, (double?)today.SimilarDocuments, (double?)today.Mutual));
        Assert.Equal((0.78, 0.95, 0.85, 0.80, 0.67), (today.LinkOnSave, today.DuplicateBlock, today.DuplicateFlag, today.Insight, today.Standing));
    }

    [Fact] public void Every_model_has_tuned_thresholds_the_map_accepts()
    {
        Assert.Equal(EmbeddingModels.All.Select(m => m.Id).Order(), EmbeddingThresholds.Tuned.Keys.Order());
        foreach (var (model, t) in EmbeddingThresholds.Tuned)
        {
            // Within what graph.similarity and the map accept, so `tuned` can never stop an engine or a rebuild.
            Assert.InRange(t.Similar, 0.2, 0.95);
            Assert.InRange(t.SimilarDocuments, 0.2, 0.99);
            Assert.InRange(t.Mutual, 0.2, 0.95);
            foreach (var x in new[] { t.LinkOnSave, t.DuplicateBlock, t.DuplicateFlag, t.Insight, t.Standing }) Assert.InRange(x, 0.2, 1.0);
            Assert.True(t.DuplicateBlock >= t.DuplicateFlag, $"{model}: a refused copy would not be flagged first");
            new RelationshipHostConfiguration(Principal, Path.Combine(Path.GetTempPath(), "state"), RelationshipCatalog.For(Settings()),
                SimilarityThreshold: t.Similar, SimilarityDocumentThreshold: t.SimilarDocuments, SimilarityMutualThreshold: t.Mutual).Validate();
        }
    }

    [Fact] public void Thresholds_are_todays_unless_the_settings_say_tuned()
    {
        foreach (var off in new[] { Settings(), Embeddings("enabled: true"), Embeddings("enabled: true\nthresholds: today"),
            RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml")) })
        {
            Assert.False(EmbeddingSettings.From(off).TunedThresholds);
            Assert.Same(EmbeddingThresholds.Today, EmbeddingThresholds.For(EmbeddingSettings.From(off)));
        }
        var on = EmbeddingSettings.From(Embeddings("enabled: true\nthresholds: tuned"));
        Assert.True(on.TunedThresholds);
        Assert.Equal(EmbeddingModels.MiniLm, on.Model);
        Assert.Same(EmbeddingThresholds.Tuned[EmbeddingModels.MiniLmId], EmbeddingThresholds.For(on));
        // Another live model reads its own (a model that is not live, here as the settings never allow it yet), whatever
        // the key says: today's were set for MiniLM's scores.
        var bge = on with { Model = EmbeddingModels.BgeSmall, TunedThresholds = false };
        Assert.Same(EmbeddingThresholds.Tuned[EmbeddingModels.BgeSmall.Id], EmbeddingThresholds.For(bge));
        // The live model stays MiniLM: tuned thresholds open no way round D-025's start check.
        Assert.StartsWith("adapters.embeddings.model is minilm for now",
            Assert.Throws<ConfigException>(() => Embeddings("enabled: true\nmodel: bge-small\nthresholds: tuned")).Message);
    }

    // One place decides what the map reads (EmbeddingThresholds.Override): MiniLM without tuned, graph.similarity as
    // before; another live model (the settings refuse one for now, D-025) its own thresholds, whatever the key says, since
    // today's were set for MiniLM's scores.
    [Fact] public void The_map_reads_another_live_models_own_thresholds_whatever_the_key_says()
    {
        var settings = Settings();
        var minilm = EmbeddingSettings.From(Embeddings("enabled: true"));
        Assert.Null(EmbeddingThresholds.Override(minilm));
        Assert.Same(EmbeddingThresholds.Tuned[EmbeddingModels.MiniLmId], EmbeddingThresholds.Override(minilm with { TunedThresholds = true }));
        Assert.Equal(Shape(Map(settings)), Shape(RelationshipHostConfiguration.From(settings, Principal, Path.GetTempPath(), minilm)));
        var bge = minilm with { Model = EmbeddingModels.BgeSmall };
        var own = EmbeddingThresholds.Tuned[EmbeddingModels.BgeSmall.Id];
        Assert.Same(own, EmbeddingThresholds.Override(bge));
        var map = RelationshipHostConfiguration.From(settings, Principal, Path.GetTempPath(), bge);
        Assert.Equal(((double?)own.Similar, (double?)own.SimilarDocuments, (double?)own.Mutual),
            (map.SimilarityThreshold, map.SimilarityDocumentThreshold, map.SimilarityMutualThreshold));
        map.Validate();
    }

    [Theory]
    [InlineData("thresholds: on")]
    [InlineData("thresholds: Tuned")]
    [InlineData("thresholds: [tuned]")]
    [InlineData("thresholds: \"\"")]
    public void A_wrong_thresholds_value_stops_the_engine_naming_the_key(string line) =>
        Assert.Equal("adapters.embeddings.thresholds is today or tuned",
            Assert.Throws<ConfigException>(() => Embeddings("enabled: true\n" + line)).Message);

    [Fact] public void Off_the_map_reads_graph_similarity_as_before_and_tuned_reads_the_live_models_own()
    {
        var custom = "graph:\n  similarity: { threshold: 0.7, neighbors: 4, documentThreshold: 0.9, mutualThreshold: 0.5 }\n";
        foreach (var (yaml, expected) in new[] { ("", (0.6, 0.8, 0.45)), (custom, (0.7, 0.9, 0.5)) })
        {
            var off = Map(Settings(yaml));
            Assert.Equal(((double?)expected.Item1, (double?)expected.Item2, (double?)expected.Item3),
                (off.SimilarityThreshold, off.SimilarityDocumentThreshold, off.SimilarityMutualThreshold));
            var today = Map(Settings(yaml + "adapters:\n  embeddings: { enabled: true, thresholds: today }\n"));
            Assert.Equal(Shape(off), Shape(today));
            var tuned = Map(Settings(yaml + "adapters:\n  embeddings: { enabled: true, thresholds: tuned }\n"));
            var mine = EmbeddingThresholds.Tuned[EmbeddingModels.MiniLmId];
            Assert.Equal(((double?)mine.Similar, (double?)mine.SimilarDocuments, (double?)mine.Mutual),
                (tuned.SimilarityThreshold, tuned.SimilarityDocumentThreshold, tuned.SimilarityMutualThreshold));
            // Everything else about the map is the settings' own.
            Assert.Equal(Shape(off with { SimilarityThreshold = mine.Similar, SimilarityDocumentThreshold = mine.SimilarDocuments,
                SimilarityMutualThreshold = mine.Mutual }), Shape(tuned));
            tuned.Validate();
        }
        // A number set to null stays as null means: similar links off, documents on the main threshold, no mutual rule.
        const string Tuned = "adapters:\n  embeddings: { enabled: true, thresholds: tuned }\n";
        var none = Map(Settings("graph:\n  similarity: { threshold: null }\n" + Tuned));
        Assert.Equal(((double?)null, (double?)null, (double?)null), (none.SimilarityThreshold, none.SimilarityDocumentThreshold, none.SimilarityMutualThreshold));
        var noMutual = Map(Settings("graph:\n  similarity: { mutualThreshold: null, documentThreshold: null }\n" + Tuned));
        Assert.Equal(((double?)EmbeddingThresholds.Tuned[EmbeddingModels.MiniLmId].Similar, (double?)null, (double?)null),
            (noMutual.SimilarityThreshold, noMutual.SimilarityDocumentThreshold, noMutual.SimilarityMutualThreshold));
    }

    [Fact] public void The_tuning_rules_choose_as_written()
    {
        // Balanced: the middle of the equal best; here every value from 0.41 to 0.61 separates the two exactly.
        (bool, double)[] apart = [(true, 0.61), (true, 0.8), (false, 0.40), (false, 0.1)];
        Assert.Equal(0.51, ThresholdTuning.Choose(apart, ThresholdTuning.Balanced, 0.9));
        // Precision first: the lowest grid value above every negative, whatever it costs in recall.
        (bool, double)[] mixed = [(true, 0.9), (true, 0.5), (false, 0.72), (false, 0.3)];
        Assert.Equal(0.73, ThresholdTuning.Choose(mixed, ThresholdTuning.PrecisionFirst, 0.9));
        Assert.Equal(new Confusion(1, 0, 1, 2), ThresholdTuning.Count(mixed, 0.73));
        // A negative at 0.99 or more leaves no grid value above it: the starting value stays.
        Assert.Equal(0.95, ThresholdTuning.Choose([(true, 1.0), (false, 0.995)], ThresholdTuning.PrecisionFirst, 0.95));
        // The same share of pairs: 0.8 holds the top quarter of one model's scores, so the lowest value that holds the
        // other's top quarter and no more.
        Assert.Equal(0.61, ThresholdTuning.Transfer(0.8, [0.1, 0.2, 0.5, 0.85], [0.3, 0.4, 0.6, 0.95]));
        var c = new Confusion(3, 1, 1, 5);
        Assert.Equal((0.75, 0.75, 5 / 6.0), (c.Precision, c.Recall, c.Specificity));
    }
}
