using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// The pieces of the embedding benchmark that need no model files and no database (SB-P2-06).
public sealed class EmbeddingBenchmarkTests
{
    [Fact] public void The_benchmark_runs_only_when_asked_with_a_database_and_both_models_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-bench-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        try
        {
            var env = new Dictionary<string, string?>();
            string? Why() => EmbeddingBenchmark.NotRun(n => env.GetValueOrDefault(n));
            Assert.StartsWith("NOT RUN: opt-in embedding benchmark", Why());
            env["RHYTHM_EMBEDDING_BENCH"] = "1";
            Assert.StartsWith("NOT RUN: the embedding benchmark needs a disposable Postgres", Why());
            env["RHYTHM_TEST_DB"] = "Host=127.0.0.1";
            Assert.Equal("NOT RUN: the embedding benchmark needs the pinned minilm files (model_quantized.onnx, vocab.txt) in the folder RHYTHM_MODEL_DIR names; it never downloads a model (D-041).", Why());
            env["RHYTHM_MODEL_DIR"] = folder;
            Assert.Contains("minilm files", Why());
            File.WriteAllText(Path.Combine(folder, "model_quantized.onnx"), "DEMO");
            File.WriteAllText(Path.Combine(folder, "vocab.txt"), "DEMO");
            // MiniLM's folder never stands in for bge-small's.
            Assert.Equal("NOT RUN: the embedding benchmark needs the pinned bge-small files (model_quantized.onnx, vocab.txt) in the folder RHYTHM_MODEL_DIR_BGE_SMALL names; it never downloads a model (D-041).", Why());
            env["RHYTHM_MODEL_DIR_BGE_SMALL"] = folder;
            Assert.Null(Why());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact] public void Ranks_are_cut_at_k()
    {
        ParityGrade[] relevant = [new("a", 2), new("b", 1)];
        string[] ranked = ["x", "y", "z", "w", "v", "u", "a", "b"];
        Assert.Equal(1.0 / 7, EmbeddingBenchmark.MrrAt(ranked, relevant, 10));
        Assert.Equal(0, EmbeddingBenchmark.MrrAt(ranked, relevant, 5));
        Assert.Null(EmbeddingBenchmark.MrrAt(ranked, [], 10));
        Assert.Equal(0, ParityMetrics.Recall(ranked, relevant, 5));
        Assert.Equal(1, ParityMetrics.Recall(ranked, relevant, 10));
    }

    [Fact] public void Each_variant_differs_from_minilm_only_where_it_says()
    {
        var byName = EmbeddingBenchmark.Variants.ToDictionary(v => v.Name);
        Assert.Equal(["minilm", "bge-small", "bge-small-instruction", "minilm-tuned", "bge-small-tuned", "bge-base", "bge-base-instruction", "bge-base-tuned"], byName.Keys);
        // Without a list, run 18's five; bge-base's only when named, and then its files, not bge-small's, are needed.
        Assert.Equal(EmbeddingBenchmark.Defaults, EmbeddingBenchmark.Selected(_ => null).Select(v => v.Name));
        Assert.Equal([EmbeddingModels.MiniLm, EmbeddingModels.BgeSmall], EmbeddingBenchmark.Models(_ => null));
        Assert.Equal([EmbeddingModels.MiniLm, EmbeddingModels.BgeBase],
            EmbeddingBenchmark.Models(n => n == "RHYTHM_EMBEDDING_BENCH_VARIANTS" ? "bge-base,bge-base-tuned" : null));
        Assert.Equal(EmbeddingModels.BgeBase.Id + "+query-instruction", byName["bge-base-instruction"].Label);
        var tunedBase = EmbeddingBenchmark.Settings(byName["bge-base-tuned"])!;
        Assert.Equal((double?)EmbeddingThresholds.Tuned[EmbeddingModels.BgeBase.Id].Similar, tunedBase.Graph.Similarity.Threshold);
        Assert.Equal(EmbeddingModels.MiniLmId, byName["minilm"].Label);
        Assert.Null(EmbeddingBenchmark.Settings(byName["minilm"]));
        Assert.Null(EmbeddingBenchmark.Settings(byName["bge-small"]));
        Assert.Equal(EmbeddingModels.BgeSmall.Id + "+query-instruction", byName["bge-small-instruction"].Label);
        // MiniLM's tuned thresholds through the setting itself; bge-small's as the map's own numbers.
        var mini = EmbeddingBenchmark.Settings(byName["minilm-tuned"])!;
        Assert.True(EmbeddingSettings.From(mini).TunedThresholds);
        var bge = EmbeddingBenchmark.Settings(byName["bge-small-tuned"])!;
        var t = EmbeddingThresholds.Tuned[EmbeddingModels.BgeSmall.Id];
        Assert.Equal(((double?)t.Similar, (double?)t.SimilarDocuments, (double?)t.Mutual),
            (bge.Graph.Similarity.Threshold, bge.Graph.Similarity.DocumentThreshold, bge.Graph.Similarity.MutualThreshold));
        Assert.False(EmbeddingSettings.From(bge).TunedThresholds);
    }
}
