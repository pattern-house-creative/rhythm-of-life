using System.Security.Cryptography;

namespace Knowledge;

// How a model turns a passage's token states into one vector: the mean of every token's (MiniLM, as sentence-transformers
// trained it) or the first token's, [CLS] (the bge family). Either way the vector is then scaled to unit length.
public enum Pooling { Mean, Cls }

// One file of a pinned model: its path in the model's public repository, its name in the local model folder, and the
// SHA-256 it must have before the engine loads it.
public sealed record ModelFile(string Remote, string Name, string Sha256);

// A local embedding model the engine knows (SB-P2-03). `Id` is the tag every vector it makes is stored with, in passages
// (`resource_chunks.model`) and on star vectors (`EmbeddingModel`): the model, its variant, the first eight hex digits of
// its weights' checksum, its pooling, its normalisation and a version of that recipe. Two vectors are compared only when
// their ids are equal, so anything that changes what a vector means (other weights, pooling, a new revision) gets a new
// id. `Name` is the short name the settings use (`adapters.embeddings.model`, `.shadow`). `Variable` names the
// environment variable for a folder already holding the files. Every model here is an uncased BERT wordpiece model with
// the same vocabulary file, so all cut a text into the same passages.
public sealed record EmbeddingModel(string Name, string Id, string Repository, string Revision, Pooling Pooling,
    int Dimensions, int MaxTokens, string Variable, IReadOnlyList<ModelFile> Files)
{
    public ModelFile Weights => Files[0];
    public ModelFile Vocabulary => Files[1];
    // Where a file is fetched from: the repository at the pinned revision, never a moving branch.
    public string Url(ModelFile file) => $"https://huggingface.co/{Repository}/resolve/{Revision}/{file.Remote}";
}

public static class EmbeddingModels
{
    public const string MiniLmId = "minilm-l6-v2-quantized-afdb6f1a-mean-l2-v1";

    // all-MiniLM-L6-v2, int8 quantized ONNX: the model every install has run so far, and the live model.
    public static readonly EmbeddingModel MiniLm = new("minilm", MiniLmId, "Xenova/all-MiniLM-L6-v2",
        "751bff37182d3f1213fa05d7196b954e230abad9", Pooling.Mean, 384, 256, "RHYTHM_MODEL_DIR",
        [new("onnx/model_quantized.onnx", "model_quantized.onnx", "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1"),
         new("vocab.txt", "vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3")]);

    // bge-small-en-v1.5, int8 quantized ONNX (Xenova's export of BAAI's model), CLS pooling: the candidate D-025 measures
    // against MiniLM (SB-P2-06). Same size of vector (384), so it fits the passage table as it is. The weights' SHA-256 is
    // the one the repository's file listing records for the file at the pinned revision (its large-file object id): no
    // copy was downloaded to check it, and the engine checks it before loading. Its vocabulary is byte for byte MiniLM's
    // (the listing names the same git object), so it has MiniLM's checksum. Queries are embedded as they are, without the
    // "Represent this sentence" instruction, as Second Brain embeds them; whether an instruction helps is for the benchmark.
    public static readonly EmbeddingModel BgeSmall = new("bge-small", "bge-small-en-v1.5-quantized-6c9c6101-cls-l2-v1",
        "Xenova/bge-small-en-v1.5", "ea104dacec62c0de699686887e3f920caeb4f3e3", Pooling.Cls, 384, 512, "RHYTHM_MODEL_DIR_BGE_SMALL",
        [new("onnx/model_quantized.onnx", "model_quantized.onnx", "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4"),
         new("vocab.txt", "vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3")]);

    // bge-base-en-v1.5, the fp32 ONNX file BAAI publishes (no quantized copy at its own repository), CLS pooling, 768
    // dimensions: the larger model D-025 allows only if the benchmark shows a benefit worth its cost (SB-P2-06). Fetched
    // once with the owner's OK (D-248). The passage table holds 384-dimensional vectors, so it can be neither the live
    // model nor the shadow (EmbeddingSettings refuses both); the benchmark measures it in throwaway databases whose passage
    // column it widens there. Its vocabulary is byte for byte MiniLM's.
    public static readonly EmbeddingModel BgeBase = new("bge-base", "bge-base-en-v1.5-fp32-9bc579ac-cls-l2-v1",
        "BAAI/bge-base-en-v1.5", "a5beb1e3e68b9ab74eb54cfd186867f64f240e1a", Pooling.Cls, 768, 512, "RHYTHM_MODEL_DIR_BGE_BASE",
        [new("onnx/model.onnx", "model.onnx", "9bc579acdba21c253c62a9bf866891355a63ffa3442b52c8a37d75b2ccb91848"),
         new("vocab.txt", "vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3")]);

    public static IReadOnlyList<EmbeddingModel> All { get; } = [MiniLm, BgeSmall, BgeBase];

    // The width of resource_chunks.embedding (migration 005): a model of another width can make no passage an install stores.
    public const int PassageDimensions = 384;

    // The live model until another one passes the retrieval gate (D-025).
    public static EmbeddingModel Default => MiniLm;

    public static EmbeddingModel? Named(string? name) => All.FirstOrDefault(m => m.Name == name);

    // A folder named for this model in the environment (its `Variable`), else null.
    public static string? Given(EmbeddingModel model, Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(model.Variable) is { } path && !string.IsNullOrWhiteSpace(path) ? path : null;

    // Whether `directory` holds every file of the model with its pinned checksum. Reads the files; never throws for a
    // missing or unreadable one.
    public static bool Verified(EmbeddingModel model, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try { return model.Files.All(f => File.Exists(Path.Combine(directory, f.Name)) && Checksum(Path.Combine(directory, f.Name)) == f.Sha256); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static string Checksum(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

// adapters.embeddings: the live model, an optional shadow model, the backfill job and the texts the index gives passages
// to (docs/configuration.md). Nothing here changes an install that does not ask: absent keys mean MiniLM, no shadow, no
// backfill and no sources, so only save_document copies have passages, as before, and today's thresholds.
public sealed record EmbeddingSettings(bool Enabled, EmbeddingModel Model, EmbeddingModel? Shadow, bool Backfill, IReadOnlyList<string> Sources)
{
    // adapters.embeddings.thresholds (SB-P2-13): `today` (absent: every install's) reads today's similarity thresholds,
    // `tuned` the live model's re-tuned ones (EmbeddingThresholds).
    public bool TunedThresholds { get; init; }

    // adapters.embeddings.sources (SB-P2-05): the texts besides save_document copies that the index job gives passages to
    // for semantic_search. `documents`: the documents adapter's uploads and dropped files, and their pages; `notes` and
    // `drive`: the records those adapters list, their text read on request (a Drive file only when it reads as words);
    // `memories`: native memories not held for review (none is written yet).
    public const string Documents = "documents", Notes = "notes", Drive = "drive", Memories = "memories";
    public static IReadOnlyList<string> KnownSources { get; } = [Documents, Notes, Drive, Memories];
    // What the example settings, and so `rhythm init`, list for a new install: every text it holds but Drive's, whose
    // passages would keep a copy of a live source's text in Rhythm, which nothing did before (Q-097). An existing
    // install's settings have no such line and keep only the copies' passages.
    public static IReadOnlyList<string> NewInstallSources { get; } = [Documents, Notes, Memories];

    public static EmbeddingSettings From(RhythmConfig config)
    {
        var section = config.Adapters is null ? new AdapterSettings("embeddings", new Dictionary<string, object?>()) : config.Adapter("embeddings");
        // On unless the section exists and does not say enabled: true (the adapter's default since the first release).
        var enabled = section.Bool("enabled", true) && (config.Adapters?.ContainsKey("embeddings") != true || section.Enabled);
        var names = string.Join(", ", EmbeddingModels.All.Select(m => m.Name));
        EmbeddingModel? Model(string key, EmbeddingModel? absent) =>
            section.String(key) is not { Length: > 0 } name ? absent
            : EmbeddingModels.Named(name) ?? throw new ConfigException($"adapters.embeddings.{key} is one of {names}");
        var model = Model("model", EmbeddingModels.Default)!;
        // Search, saved documents and the map stay on MiniLM until another model passes the retrieval gate and its
        // thresholds are tuned (D-025; SB-P2-06, SB-P2-13). Until then another model can only be built as the shadow.
        if (model != EmbeddingModels.Default)
            throw new ConfigException($"adapters.embeddings.model is {EmbeddingModels.Default.Name} for now: another model serves search only after it is measured against it; build its index with adapters.embeddings.shadow");
        var shadow = Model("shadow", null);
        if (shadow == model) throw new ConfigException("adapters.embeddings.shadow names a model other than adapters.embeddings.model");
        if (shadow is not null && shadow.Dimensions != EmbeddingModels.PassageDimensions)
            throw new ConfigException($"adapters.embeddings.shadow cannot be {shadow.Name}: its {shadow.Dimensions}-dimensional vectors do not fit the passage table's {EmbeddingModels.PassageDimensions}");
        var backfill = section.String("backfill") switch
        {
            null => false,
            var v when v.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new ConfigException("adapters.embeddings.backfill is true or false"),
        };
        var sources = section.List("sources");
        if (section.NotList("sources") || sources.Any(s => !KnownSources.Contains(s)) || sources.Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ConfigException($"adapters.embeddings.sources is a list of {string.Join(", ", KnownSources)}, each at most once");
        var tuned = section.String("thresholds") switch
        {
            null or "today" => false,
            "tuned" => true,
            _ => throw new ConfigException("adapters.embeddings.thresholds is today or tuned"),
        };
        return new(enabled, model, shadow, backfill, sources) { TunedThresholds = tuned };
    }
}
