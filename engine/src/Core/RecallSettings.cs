using Knowledge.Adapters;

namespace Knowledge;

// engine.recall and engine.searchKnowledge (SB-P2-09, SB-P2-10; docs/configuration.md): where combined retrieval
// (Recall, docs/dev/second-brain-parity/combined-retrieval.md) is served. Both are absent from every rhythm.yaml written
// before them, and absent means off, so on upgrade every search answers as it did:
//   recall           off (absent): no recall tool, and the app's explorer searches star titles only, as before;
//                    on: AI clients get the recall tool, and the explorer's search adds the stars of what combined
//                    retrieval finds, best first, each with why (app_graph operation recall).
//   searchKnowledge  fulltext (absent): search_knowledge as before; combined: search_knowledge answers its rows, in its
//                    own shape, in the combined list's order. Only on an install with legacySearch: false, refused at
//                    start otherwise: the gate judged the combined list against ranked keyword search, where no
//                    category regresses, and against the legacy query it loses rare names with copies (D-151).
//   recallLog        off (absent): nothing is logged (D-022); on: each recall the recall tool or the explorer's search
//                    answers is kept in its reader's scope, bounded, for 30 days, with implicit feedback (RecallLog,
//                    SB-P2-11). No answer changes either way, and with recall off nothing is asked that it could keep.
//   rerank           off (absent): the combined list as fused; bge-reranker-base or ms-marco-minilm: that cross-encoder
//                    reorders its first results (SB-P2-12, D-026; Reranking, RecallRerank), loaded at start behind a
//                    readiness probe and a circuit breaker, the fused order whenever it is not ready or fails. Off unless
//                    the gate passes it (D-026; benchmarks.md).
public sealed record RecallSettings(bool Tool, bool CombinedSearchKnowledge, bool Log = false)
{
    public static readonly RecallSettings Off = new(false, false);
    public RerankerModel? Rerank { get; init; }

    public static RecallSettings From(RhythmConfig config) =>
        new(config.Engine.Recall is "on", config.Engine.SearchKnowledge is "combined", config.Engine.RecallLog is "on")
        { Rerank = config.Engine.Rerank is null or "" or "off" ? null : RerankerModels.Named(config.Engine.Rerank) };

    // Why the settings are refused at start, naming the key, or null.
    public static string? Refusal(RhythmConfig config)
    {
        if (config.Engine.Recall is not (null or "" or "off" or "on")) return "engine.recall is off or on";
        if (config.Engine.SearchKnowledge is not (null or "" or "fulltext" or "combined")) return "engine.searchKnowledge is fulltext or combined";
        if (config.Engine.RecallLog is not (null or "" or "off" or "on")) return "engine.recallLog is off or on";
        if (config.Engine.Rerank is not (null or "" or "off") && RerankerModels.Named(config.Engine.Rerank) is null)
            return $"engine.rerank is off, {string.Join(" or ", RerankerModels.All.Select(m => m.Name))}";
        if (config.Engine.SearchKnowledge is "combined" && config.Engine.LegacySearch)
            return "engine.searchKnowledge: combined needs engine.legacySearch: false (the combined list is judged against ranked keyword search, not the search before ranking)";
        return null;
    }
}

// Combined retrieval for one call, built from the install's model and settings as semantic_search's search is: the
// local model when it is on (else keyword results and a notice), the passages the settings list now
// (adapters.embeddings.sources), and the live-source checks. A test registers its own RecallFactory with a stand-in; it
// is handed the recall log For decides on, so a test through the factory runs the same log decision an install does.
public delegate Recall RecallFactory(RecallOptions options, RecallLog? log);

public static class RecallServing
{
    // Without options, the recall tool's (RecallOptions.Tool). With engine.recallLog on, a search its options say is logged
    // is kept by the recall log (RecallLog); off, no log is made at all.
    public static Recall For(IServiceProvider services, RecallOptions? options = null)
    {
        options ??= RecallOptions.Tool;
        var config = services.GetRequiredService<RhythmConfig>();
        // The hops follow made links only with the memory tools on (SB-P4-02).
        options = options with { MadeLinks = MemorySettings.ToolsOn(config) };
        var db = services.GetRequiredService<Database>();
        var log = Log(config, options, db.Source);
        if (services.GetService<RecallFactory>() is { } factory) return factory(options, log);
        var embeddings = services.GetRequiredService<Embeddings>();
        var serving = PassageSources.For(EmbeddingSettings.From(config), services.GetRequiredService<AdapterRegistry>().Enabled, true);
        // engine.rerank's cross-encoder (off by default): the engine's one Reranking holds it while it loads and once it is ready.
        var rerank = RecallSettings.From(config).Rerank is null ? null : services.GetService<Reranking>()?.Step();
        return new Recall(db, embeddings.Model, serving, services.GetRequiredService<ISourceValidation>(), options, embeddings.Status, log,
            rerank: rerank);
    }

    // The recall log a search is kept by: one only with engine.recallLog on and for a search its options say is logged.
    public static RecallLog? Log(RhythmConfig config, RecallOptions options, Npgsql.NpgsqlDataSource source) =>
        options.Logged && RecallSettings.From(config).Log ? new RecallLog(source) : null;

    // The settings in force for a call: the ones the services hold, else the loaded ones, else none (off).
    public static RecallSettings Settings(IServiceProvider? services) =>
        services?.GetService<RhythmConfig>() is { } config ? RecallSettings.From(config)
        : RhythmConfig.Loaded ? RecallSettings.From(RhythmConfig.Current) : RecallSettings.Off;
}
