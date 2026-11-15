namespace Knowledge;

// The local semantic model, optional, one registry entry (EmbeddingModels): MiniLM unless the settings say otherwise.
// With `adapters.embeddings.enabled` (the default) the engine uses the folder the model's environment variable names
// (RHYTHM_MODEL_DIR for MiniLM) when it holds the pinned files, or downloads them once into its data folder
// (<data>/models/<name>) from the model's public repository at a pinned revision. Either way every file must match its
// SHA-256; if the download fails or a checksum differs, what needs the model is simply off. A shadow model
// (`adapters.embeddings.shadow`, off by default) is loaded the same way beside it; only the index job uses it (SB-P2-04).
// A map model (`graph.similarity.model`, absent by default, D-256) is loaded the same way too; only the map's similar and
// copy links read its vectors (CatalogRelationshipSource, SimilaritySuggestions).
public sealed class Embeddings(EmbeddingModel spec) : IDisposable
{
    // MiniLM's revision and files, as before the registry.
    public const string Revision = "751bff37182d3f1213fa05d7196b954e230abad9";
    public static IReadOnlyList<(string Remote, string File, string Sha256)> Files { get; } =
        [.. EmbeddingModels.MiniLm.Files.Select(f => (f.Remote, f.Name, f.Sha256))];
    readonly object gate = new();
    LocalEmbeddings? model;
    public Embeddings() : this(EmbeddingModels.Default) { }
    public EmbeddingModel Spec { get; } = spec;
    // The id every vector this model makes is tagged with.
    public string Id => Spec.Id;
    public string Status { get; private set; } = "off";
    public LocalEmbeddings? Model { get { lock (gate) return model; } }
    // The shadow model, when the settings name one: its index is built beside the live one and read by no search.
    public Embeddings? Shadow { get; private set; }
    // The map model, when graph.similarity.model names one (MapModels.Wanted): its vectors make the map's similar and copy
    // links alone. The shadow's holder when both name the same model, so it is loaded once.
    public Embeddings? Map { get; private set; }
    public static Embeddings Off { get; } = new();
    // Whether adapters.embeddings.backfill is on (Start reads it; off by default): only then is a document saved while
    // the model is off kept for the index job, which gives it its passages later (DocumentStore.Save, D-069).
    bool backfill;
    public bool Backfill { get => backfill; init => backfill = value; }

    // Loads the model when its files are present and verified; returns whether it is on.
    public bool TryLoad(string directory)
    {
        try
        {
            var loaded = new LocalEmbeddings(directory, Spec);
            lock (gate) { model?.Dispose(); model = loaded; Status = "on"; }
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or ArgumentException) { Status = "unverified"; return false; }
    }

    // Fetches missing files into `directory`, keeping only files whose checksum matches. Never throws.
    public async Task<bool> Fetch(string directory, HttpClient http, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Spec.Files)
            {
                var target = Path.Combine(directory, file.Name);
                if (File.Exists(target) && EmbeddingModels.Checksum(target) == file.Sha256) continue;
                var bytes = await http.GetByteArrayAsync(Spec.Url(file), ct);
                if (Hashing.Hash(bytes) != file.Sha256) return false;
                var temporary = target + ".download";
                await File.WriteAllBytesAsync(temporary, bytes, ct);
                File.Move(temporary, target, true);
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException) { return false; }
    }

    // Loads the live model, then the shadow one if the settings name it. Each from the folder its environment variable
    // names, else <data>/models/<name>, downloaded when missing.
    public async Task Start(RhythmConfig config, string dataDirectory, CancellationToken ct, Func<string, string?>? environment = null)
    {
        var settings = EmbeddingSettings.From(config);
        backfill = settings.Backfill;
        if (!settings.Enabled) { Status = "off"; return; }
        // The engine makes this holder for the settings' model (Program.cs); another one would tag vectors wrongly.
        if (settings.Model != Spec) { Status = "wrong-model"; return; }
        await Open(dataDirectory, environment, ct);
        if (settings.Shadow is { } shadow)
        {
            var second = new Embeddings(shadow);
            lock (gate) Shadow = second;
            await second.Open(dataDirectory, environment, ct);
        }
        if (MapModels.Wanted(config) is { } mapModel)
        {
            if (Shadow?.Spec == mapModel) { lock (gate) Map = Shadow; return; }
            var map = new Embeddings(mapModel);
            lock (gate) Map = map;
            await map.Open(dataDirectory, environment, ct);
        }
    }

    // Loads a map model from `directory` beside this one, as Start does for graph.similarity.model, for a test or a
    // measurement that names its folder; returns whether it is on.
    public bool TryLoadMap(EmbeddingModel model, string directory)
    {
        var map = new Embeddings(model);
        lock (gate) { if (Map != Shadow) Map?.Dispose(); Map = map; }
        return map.TryLoad(directory);
    }

    async Task Open(string dataDirectory, Func<string, string?>? environment, CancellationToken ct)
    {
        var given = EmbeddingModels.Given(Spec, environment);
        var directory = given ?? Path.Combine(dataDirectory, "models", Spec.Name);
        if (TryLoad(directory)) return;
        if (given is not null) return;
        Status = "downloading";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (await Fetch(directory, http, ct)) TryLoad(directory);
        else Status = "download-failed";
    }

    public float[]? Vector(string title, string text) => Model is { } m ? Relationships.SimilaritySuggestions.Vector(m.Embed, m.Chunks, title, text) : null;
    public void Dispose() { lock (gate) { model?.Dispose(); model = null; Shadow?.Dispose(); Map?.Dispose(); } }
}
