namespace Knowledge.Tests.Parity;

// Which embedding model a parity run loads (SB-P2-06): by default the live one, MiniLM, from RHYTHM_MODEL_DIR, as every
// run before. RHYTHM_PARITY_MODEL names another registry model (bge-small, read from RHYTHM_MODEL_DIR_BGE_SMALL), and
// RHYTHM_PARITY_QUERY_INSTRUCTION=on embeds each question after the model's retrieval instruction (D-068: off, as
// upstream embeds them). The evaluation (ParityEvaluation) and the gate (ParityGateRun) read it; a gate run with another
// model is comparable with the MiniLM locks only with RHYTHM_PARITY_GATE_MODEL_CHANGE=1, and its answers move from them
// by design. Nothing here is an engine setting: the engine's live model stays MiniLM (D-025). RHYTHM_PARITY_MAP_MODEL
// names a map model (graph.similarity.model, D-256: bge-small, from RHYTHM_MODEL_DIR_BGE_SMALL), loaded beside the live
// one for the map's similar and copy links alone; the label and the lock comparison stay the live model's, so every path
// but the map's should answer as its lock, and no lock is written from such a run. RHYTHM_PARITY_MAP_MAXLINKS caps the map's
// similar links a star (graph.similarity.maxLinks, D-258: 2 to 50), with or without a map model; likewise never locked.
public sealed record ParityModelChoice(EmbeddingModel Spec, string? Directory, string? Instruction)
{
    public EmbeddingModel? Map { get; init; }
    public string? MapDirectory { get; init; }
    public int? MaxLinks { get; init; }

    public static ParityModelChoice Default(Func<string, string?>? environment = null) =>
        new(EmbeddingModels.Default, (environment ?? Environment.GetEnvironmentVariable)(EmbeddingModels.Default.Variable), null);

    public static ParityModelChoice FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var spec = environment("RHYTHM_PARITY_MODEL") switch
        {
            null or "" => EmbeddingModels.Default,
            var name => EmbeddingModels.Named(name)
                ?? throw new ArgumentException($"RHYTHM_PARITY_MODEL is one of {string.Join(", ", EmbeddingModels.All.Select(m => m.Name))}, not {name}"),
        };
        var instruction = environment("RHYTHM_PARITY_QUERY_INSTRUCTION") switch
        {
            null or "" or "off" => null,
            "on" => QueryInstruction.For(spec)
                ?? throw new ArgumentException($"RHYTHM_PARITY_QUERY_INSTRUCTION: {spec.Name} has no query instruction"),
            var other => throw new ArgumentException($"RHYTHM_PARITY_QUERY_INSTRUCTION is on or off, not {other}"),
        };
        EmbeddingModel? map;
        try { map = MapModels.Named(environment("RHYTHM_PARITY_MAP_MODEL")); }
        catch (ConfigException e) { throw new ArgumentException("RHYTHM_PARITY_MAP_MODEL: " + e.Message); }
        var maxLinks = environment("RHYTHM_PARITY_MAP_MAXLINKS") switch
        {
            null or "" => (int?)null,
            var cap when int.TryParse(cap, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 2 and <= 50 => n,
            var other => throw new ArgumentException($"RHYTHM_PARITY_MAP_MAXLINKS is a whole number from 2 to 50, not {other}"),
        };
        return new(spec, environment(spec.Variable), instruction) { Map = map, MapDirectory = map is null ? null : environment(map.Variable), MaxLinks = maxLinks };
    }

    // What a report and a lock call the model: its id, and that questions carry its instruction when they do. MiniLM's
    // without an instruction is its id alone, as every lock so far records it.
    public string Label => Name(Spec.Id, Instruction);
    public static string Name(string id, string? instruction) => instruction is null ? id : id + "+query-instruction";

    // Why a gate run with this choice may not write a locked baseline, or null when it may: a lock holds the answers an
    // install gives, so only the live model, MiniLM (D-025: the settings refuse another), with questions as they are
    // (D-068: the engine never embeds an instruction).
    public string? LockRefusal() =>
        Instruction is not null ? "its questions were embedded after a retrieval instruction, which the engine never does (D-068)"
        : Spec != EmbeddingModels.Default ? $"{Spec.Name} is not the live model, which stays MiniLM until the owner switches it (D-025)"
        : Map is not null ? $"the map's similar links came from {Map.Name} (graph.similarity.model), which the locks' install does not set (D-256)"
        : MaxLinks is not null ? "the map's similar links were capped a star (graph.similarity.maxLinks), which the locks' install does not set (D-258)"
        : null;
}

// Questions embedded after a retrieval instruction, passages as they are: the asymmetric use the bge family was trained
// for ("Represent this sentence for searching relevant passages: ", BAAI's model card for bge-*-en-v1.5). Its id is the
// model's, since the passages it is compared with are that model's own; only the question's vector differs. Used by the
// benchmark alone (D-068: the engine embeds every question as it is).
public sealed class QueryInstruction(IEmbedder model, string instruction) : IEmbedder
{
    public const string Bge = "Represent this sentence for searching relevant passages: ";
    public static string? For(EmbeddingModel model) => model.Pooling == Pooling.Cls ? Bge : null;

    public string Id => model.Id;
    public string Instruction => instruction;
    public float[] Embed(string text) => model.Embed(instruction + text);
    public IEnumerable<string> Chunks(string content) => model.Chunks(content);
}
