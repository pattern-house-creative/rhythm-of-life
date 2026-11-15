using System.Globalization;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// A test that needs a model's pinned files runs only when the folder its variable names holds them (RHYTHM_MODEL_DIR for
// MiniLM, RHYTHM_MODEL_DIR_BGE_SMALL for bge-small), and is skipped, saying so, otherwise: tests never download a model
// (D-041). A folder whose files are there but differ fails the test rather than skipping it.
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute(string name)
    {
        var model = EmbeddingModels.Named(name) ?? throw new ArgumentException("Unknown model " + name);
        var folder = EmbeddingModels.Given(model);
        if (folder is null || !model.Files.All(f => File.Exists(Path.Combine(folder, f.Name))))
            Skip = $"NOT RUN: needs the pinned {name} files ({string.Join(", ", model.Files.Select(f => f.Name))}) in the folder {model.Variable} names; tests never download a model (D-041).";
    }
}

// The embedding model registry (SB-P2-03): every model the engine can load is pinned to a commit and checksummed, has an
// id of its own that every vector it makes is tagged with, and is off unless the settings ask for it. MiniLM stays the
// live model, exactly as before (D-025); bge-small (CLS pooling) loads beside it, from local files only in tests.
public sealed class EmbeddingRegistryTests
{
    static readonly string[] Texts = ["DEMO: a fictional plan for the allotment in spring", "DEMO: sam waters the tomatoes every evening",
        "DEMO: the boiler service is booked for March"];
    static double Dot(float[] a, float[] b) => a.Zip(b).Sum(p => (double)p.First * p.Second);
    static string Folder(EmbeddingModel model) => EmbeddingModels.Given(model)!;
    static RhythmConfig Settings(string embeddings) => RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings:\n" +
        string.Concat(embeddings.Split('\n').Select(l => "    " + l + "\n")));

    [Fact] public void Every_model_is_pinned_and_checksummed_under_an_id_of_its_own()
    {
        Assert.Equal(["minilm", "bge-small", "bge-base"], EmbeddingModels.All.Select(m => m.Name));
        Assert.Equal(EmbeddingModels.All.Count, EmbeddingModels.All.Select(m => m.Id).Distinct().Count());
        Assert.Equal(EmbeddingModels.All.Count, EmbeddingModels.All.Select(m => m.Variable).Distinct().Count());
        foreach (var m in EmbeddingModels.All)
        {
            Assert.Same(m, EmbeddingModels.Named(m.Name));
            Assert.Matches("^[0-9a-f]{40}$", m.Revision);
            Assert.Equal([m == EmbeddingModels.BgeBase ? "model.onnx" : "model_quantized.onnx", "vocab.txt"], m.Files.Select(f => f.Name));
            Assert.All(m.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
            // The id names the weights and the pooling, so other weights or another pooling can never share it.
            Assert.Contains("-" + m.Weights.Sha256[..8] + "-", m.Id);
            Assert.Contains(m.Pooling == Pooling.Cls ? "-cls-l2-" : "-mean-l2-", m.Id);
            // The passage table holds 384-D vectors (bge-base's 768 only in the benchmark's throwaway databases, which is why
            // the settings refuse it as the shadow), and a passage's model tag is at most 100 characters (save_document).
            Assert.Equal(m == EmbeddingModels.BgeBase ? 768 : EmbeddingModels.PassageDimensions, m.Dimensions);
            Assert.InRange(m.Id.Length, 1, 100);
            // Fetched at the pinned commit only, never a branch.
            Assert.Equal($"https://huggingface.co/{m.Repository}/resolve/{m.Revision}/{m.Weights.Remote}", m.Url(m.Weights));
            Assert.StartsWith("onnx/model", m.Weights.Remote);
        }
        Assert.Null(EmbeddingModels.Named("bge-large"));
        // bge-base-en-v1.5: BAAI's own fp32 file at a pinned commit, CLS pooling, MiniLM's vocabulary file (the same bytes).
        Assert.Equal(("bge-base-en-v1.5-fp32-9bc579ac-cls-l2-v1", Pooling.Cls, 512, "RHYTHM_MODEL_DIR_BGE_BASE"),
            (EmbeddingModels.BgeBase.Id, EmbeddingModels.BgeBase.Pooling, EmbeddingModels.BgeBase.MaxTokens, EmbeddingModels.BgeBase.Variable));
        Assert.Equal(EmbeddingModels.MiniLm.Vocabulary.Sha256, EmbeddingModels.BgeBase.Vocabulary.Sha256);

        // MiniLM is what every install has run: its id, files and revision are exactly the ones before the registry, and
        // vectors stored without a tag are read as its (D-052).
        var minilm = EmbeddingModels.MiniLm;
        Assert.Same(minilm, EmbeddingModels.Default);
        Assert.Equal(SimilaritySuggestions.Untagged, minilm.Id);
        Assert.Equal(LocalEmbeddings.Model, minilm.Id);
        Assert.Equal((LocalEmbeddings.ModelSha256, LocalEmbeddings.VocabSha256), (minilm.Weights.Sha256, minilm.Vocabulary.Sha256));
        Assert.Equal(Embeddings.Revision, minilm.Revision);
        Assert.Equal(Embeddings.Files, minilm.Files.Select(f => (f.Remote, f.Name, f.Sha256)));
        Assert.Equal((Pooling.Mean, 256, "RHYTHM_MODEL_DIR"), (minilm.Pooling, minilm.MaxTokens, minilm.Variable));

        // bge-small-en-v1.5: CLS pooling, its own weights, MiniLM's vocabulary file (the same bytes), so the two models cut a
        // text into the same passages.
        var bge = EmbeddingModels.BgeSmall;
        Assert.Equal("bge-small-en-v1.5-quantized-6c9c6101-cls-l2-v1", bge.Id);
        Assert.Equal((Pooling.Cls, 512, "RHYTHM_MODEL_DIR_BGE_SMALL"), (bge.Pooling, bge.MaxTokens, bge.Variable));
        Assert.Equal(minilm.Vocabulary.Sha256, bge.Vocabulary.Sha256);
        Assert.NotEqual(minilm.Weights.Sha256, bge.Weights.Sha256);
    }

    [Fact] public void Without_settings_an_install_keeps_minilm_with_no_shadow_and_no_backfill()
    {
        foreach (var config in new[] { TestSettings.Use(), RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml")),
            Settings("enabled: true") })
        {
            var s = EmbeddingSettings.From(config);
            Assert.Equal((true, EmbeddingModels.MiniLm, (EmbeddingModel?)null, false), (s.Enabled, s.Model, s.Shadow, s.Backfill));
        }
        var on = EmbeddingSettings.From(Settings("enabled: true\nmodel: minilm\nshadow: bge-small\nbackfill: true"));
        Assert.Equal((true, EmbeddingModels.MiniLm, (EmbeddingModel?)EmbeddingModels.BgeSmall, true), (on.Enabled, on.Model, on.Shadow, on.Backfill));
        Assert.False(EmbeddingSettings.From(Settings("enabled: false\nshadow: bge-small")).Enabled);
        // A section without enabled: true is off, as the adapter has always read it.
        Assert.False(EmbeddingSettings.From(Settings("shadow: bge-small")).Enabled);
        Assert.False(EmbeddingSettings.From(Settings("enabled: true\nbackfill: false")).Backfill);
    }

    [Theory]
    [InlineData("model: bge-small", "adapters.embeddings.model is minilm for now")]
    [InlineData("model: mpnet", "adapters.embeddings.model is one of minilm, bge-small, bge-base")]
    [InlineData("model: bge-base", "adapters.embeddings.model is minilm for now")]
    [InlineData("shadow: minilm", "adapters.embeddings.shadow names a model other than adapters.embeddings.model")]
    [InlineData("shadow: bge-base", "adapters.embeddings.shadow cannot be bge-base: its 768-dimensional vectors do not fit the passage table's 384")]
    [InlineData("shadow: bge-large", "adapters.embeddings.shadow is one of minilm, bge-small, bge-base")]
    [InlineData("backfill: yes", "adapters.embeddings.backfill is true or false")]
    public void Wrong_model_settings_fail_at_start_with_the_key(string line, string message) =>
        Assert.StartsWith(message, Assert.Throws<ConfigException>(() => Settings("enabled: true\n" + line)).Message);

    [Fact] public async Task Files_that_are_not_the_pinned_ones_never_load_and_a_given_folder_is_never_downloaded_into()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-models-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "model_quantized.onnx"), "DEMO: not a model");
            await File.WriteAllTextAsync(Path.Combine(folder, "vocab.txt"), "[PAD]\n[UNK]\n");
            foreach (var model in EmbeddingModels.All)
            {
                using var embeddings = new Embeddings(model);
                Assert.False(embeddings.TryLoad(folder));
                Assert.Equal(("unverified", (LocalEmbeddings?)null), (embeddings.Status, embeddings.Model));
                Assert.False(EmbeddingModels.Verified(model, folder));
                Assert.False(EmbeddingModels.Verified(model, Path.Combine(folder, "missing")));
                Assert.False(EmbeddingModels.Verified(model, null));
            }
            // Each model's folder comes from its own variable: MiniLM's never stands in for bge-small's.
            Assert.Equal("x", EmbeddingModels.Given(EmbeddingModels.BgeSmall, n => n == "RHYTHM_MODEL_DIR_BGE_SMALL" ? "x" : null));
            Assert.Null(EmbeddingModels.Given(EmbeddingModels.BgeSmall, n => n == "RHYTHM_MODEL_DIR" ? "x" : null));
            Assert.Null(EmbeddingModels.Given(EmbeddingModels.MiniLm, _ => " "));

            // The engine's start loads the shadow beside the live model, each from its given folder: wrong files leave
            // each one off ("unverified"), and nothing is fetched into a folder the environment names.
            var data = Path.Combine(folder, "data");
            using var live = new Embeddings();
            await live.Start(Settings("enabled: true\nshadow: bge-small"), data, default, _ => folder);
            Assert.Equal("unverified", live.Status);
            Assert.Equal((EmbeddingModels.BgeSmall, "unverified"), (live.Shadow!.Spec, live.Shadow.Status));
            Assert.False(Directory.Exists(data));
            // Off, or without a shadow, nothing more is loaded.
            using var off = new Embeddings();
            await off.Start(Settings("enabled: false\nshadow: bge-small"), data, default, _ => folder);
            Assert.Equal(("off", (Embeddings?)null), (off.Status, off.Shadow));
            using var alone = new Embeddings();
            await alone.Start(Settings("enabled: true"), data, default, _ => folder);
            Assert.Null(alone.Shadow);
            Assert.False(Directory.Exists(data));
            // The start takes the backfill setting, off unless it says true, which decides whether a document saved while
            // the model is off waits for the index job (DocumentStore.Save).
            Assert.False(alone.Backfill);
            using var waiting = new Embeddings();
            await waiting.Start(Settings("enabled: true\nbackfill: true"), data, default, _ => folder);
            Assert.True(waiting.Backfill);
            Assert.False(Directory.Exists(data));
        }
        finally { Directory.Delete(folder, true); }
    }

    // The refactor into the registry changed nothing MiniLM makes: these are the vectors, scores and passages of the code
    // before it, on this machine (to within the runtime's rounding on another one).
    [ModelFact("minilm")] public void MiniLM_through_the_registry_embeds_and_cuts_text_exactly_as_before()
    {
        using var minilm = new Embeddings();
        Assert.True(minilm.TryLoad(Folder(EmbeddingModels.MiniLm)));
        var model = minilm.Model!;
        Assert.Equal(EmbeddingModels.MiniLm.Id, model.Id);
        float[][] before =
        [
            [-0.017389446f, 0.035992965f, 0.044624664f, -0.038031258f, 0.08589264f, 0.0119424f],
            [-0.10803949f, 0.018252933f, -0.02999f, 0.03069657f, 0.03916789f, 0.009657166f],
            [-0.04597166f, -0.003896614f, 0.0036936374f, 0.04295294f, 0.057333156f, 0.05781716f],
        ];
        var vectors = Texts.Select(model.Embed).ToArray();
        foreach (var (vector, expected) in vectors.Zip(before))
        {
            Assert.Equal(384, vector.Length);
            for (var j = 0; j < expected.Length; j++) Assert.Equal(expected[j], (double)vector[j], 1e-5);
        }
        Assert.Equal(0.41633212147856236, Dot(vectors[0], vectors[1]), 1e-5);
        Assert.Equal(0.32333165711662465, Dot(vectors[0], vectors[2]), 1e-5);
        Assert.Equal([496, 496, 499, 496, 497, 755],
            model.Chunks(string.Join(" ", Enumerable.Repeat("DEMO: the allotment rota for the fictional household.", 60))).Select(c => c.Length));
        // Another model's id never loads MiniLM's files: the checksum is the model's own.
        using var bge = new Embeddings(EmbeddingModels.BgeSmall);
        Assert.False(bge.TryLoad(Folder(EmbeddingModels.MiniLm)));
        Assert.Equal("unverified", bge.Status);
    }

    // bge-small-en-v1.5 from local files, beside MiniLM when MiniLM's files are there too: a 384-D unit vector from the
    // [CLS] token, the same each time, closer for a paraphrase than for an unrelated text, tagged with its own id, and
    // never MiniLM's vector. Nothing here is a measurement of quality: that is SB-P2-06's benchmark.
    [ModelFact("bge-small")] public void Bge_small_loads_from_local_files_beside_minilm()
    {
        using var bge = new Embeddings(EmbeddingModels.BgeSmall);
        Assert.True(bge.TryLoad(Folder(EmbeddingModels.BgeSmall)), "the pinned bge-small files did not verify");
        var model = bge.Model!;
        Assert.Equal(("on", EmbeddingModels.BgeSmall.Id), (bge.Status, model.Id));
        var vectors = Texts.Select(model.Embed).ToArray();
        Assert.All(vectors, v => Assert.Equal(384, v.Length));
        Assert.All(vectors, v => Assert.Equal(1, Dot(v, v), 1e-4));
        Assert.Equal(vectors[0], model.Embed(Texts[0]));
        var paraphrase = model.Embed("DEMO: the spring planting plan for the allotment");
        Assert.True(Dot(vectors[0], paraphrase) > Dot(vectors[0], vectors[2]));
        // A query longer than MiniLM's 256 wordpieces fits bge's 512; past that it is refused, never cut.
        var long300 = string.Join(" ", Enumerable.Repeat("garden", 300));
        Assert.Equal(384, model.Embed(long300).Length);
        Assert.Throws<ArgumentException>(() => model.Embed(string.Join(" ", Enumerable.Repeat("garden", 600))));

        if (EmbeddingModels.Given(EmbeddingModels.MiniLm) is not { } miniFolder || !EmbeddingModels.Verified(EmbeddingModels.MiniLm, miniFolder)) return;
        using var minilm = new Embeddings();
        Assert.True(minilm.TryLoad(miniFolder));
        Assert.NotEqual(minilm.Model!.Embed(Texts[0]), vectors[0]);
        Assert.Throws<ArgumentException>(() => minilm.Model.Embed(long300));
        var text = string.Join(" ", Enumerable.Repeat("DEMO: the allotment rota for the fictional household.", 60));
        Assert.Equal(minilm.Model.Chunks(text), model.Chunks(text));
    }
}
