namespace Knowledge;

// The scores a model's similarities are read against (SB-P2-13). Each is a dot product of two unit vectors of one model
// (SimilaritySuggestions.Vector's recipe), and one model's scores mean nothing against another's threshold:
//   Similar, SimilarDocuments, Mutual  the map's dashed "similar" links (graph.similarity's threshold, documentThreshold
//                                      and mutualThreshold)
//   LinkOnSave                         an automatic link written when a memory is saved (SB-P4-03, MemoryLinkInference: a
//                                      save's words against a stored memory's best passage)
//   DuplicateFlag                      a save flagged for review (SB-P5-02, MemoryDuplicates.cs)
//   DuplicateBlock                     recorded only: no save is refused as a copy (D-211)
//   Insight                            a candidate pair for the nightly insight, 30 or more days apart (SB-P5-05, InsightCandidates.cs)
//   Standing                           a standing instruction that fires for a question (SB-P5-12, MemoryStanding.cs)
//   Contradiction                      a close memory offered to the calling AI as a possible contradiction and queued for a
//                                      person (SB-P5-03, MemoryDuplicates.cs: a write's words against a stored memory's)
public sealed record SimilarityThresholds(double Similar, double SimilarDocuments, double Mutual, double LinkOnSave,
    double DuplicateBlock, double DuplicateFlag, double Insight, double Standing, double Contradiction);

public static class EmbeddingThresholds
{
    // Today's, whatever the model: the map's defaults (graph.similarity), and the starting points D-028 takes from Second
    // Brain for the rest. Every install reads these unless adapters.embeddings.thresholds is `tuned`. Contradiction has no
    // number of its own today: the duplicate flag (0.85) is all that names a contradiction before SB-P5-03 (D-211), so it
    // reads the same and is no looser. Upstream's own candidate score, 0.45, is for its model and far looser.
    public static SimilarityThresholds Today { get; } = new(0.6, 0.8, 0.45, 0.78, 0.95, 0.85, 0.80, 0.67, 0.85);

    // Re-tuned per model on the synthetic labelled pairs (SB-P2-18), by the rules fixed before the first run and with the
    // precision and recall each gives (docs/dev/second-brain-parity/benchmarks.md; ThresholdTuningTests recomputes them
    // from the model files). Keyed by the model's id, so a new revision of a model has none until it is tuned.
    // The map's document and mutual thresholds have too few labelled pairs to tune: MiniLM keeps today's, and bge-small
    // takes the scores with the same share of the corpus's pairs at or above them. Run 19 (SB-P4-03) labelled every pair of
    // memories of one scope a link on save reads at 0.43 or more under MiniLM or 0.62 or more under bge-small, 104 more
    // pairs, and every value was recomputed from them: LinkOnSave rose to 0.58 and 0.67 (no unrelated pair at or above
    // either), and MiniLM's Similar to 0.56, above its mutual 0.45, so the mutual rule links two records that are each
    // other's closest from 0.45 up, as today's 0.6 does. SB-P5-12 labelled 21 more standing instructions and 88 questions
    // that should fire, and took Standing by precision first, just above the highest negative (D-211 asked for more labels
    // before any value was adopted): 0.59 and 0.72 in place of run 18's 0.31 and 0.51.
    public static IReadOnlyDictionary<string, SimilarityThresholds> Tuned { get; } = new Dictionary<string, SimilarityThresholds>(StringComparer.Ordinal)
    {
        [EmbeddingModels.MiniLmId] = new(Similar: 0.56, SimilarDocuments: 0.80, Mutual: 0.45, LinkOnSave: 0.58,
            DuplicateBlock: 0.96, DuplicateFlag: 0.63, Insight: 0.52, Standing: 0.59,
            Contradiction: 0.58),
        [EmbeddingModels.BgeSmall.Id] = new(Similar: 0.68, SimilarDocuments: 0.88, Mutual: 0.73, LinkOnSave: 0.67,
            DuplicateBlock: 0.96, DuplicateFlag: 0.75, Insight: 0.33, Standing: 0.72,
            Contradiction: 0.66),
        // bge-base-en-v1.5 (D-249), by the same rules on the same 193 pairs, recorded before the benchmark read them. Its
        // precision-first values (LinkOnSave, Standing) rest on pairs labelled where MiniLM and bge-small scored high, so
        // an unlabelled pair could reach them; no install reads any of these (D-025).
        [EmbeddingModels.BgeBase.Id] = new(Similar: 0.63, SimilarDocuments: 0.83, Mutual: 0.68, LinkOnSave: 0.68,
            DuplicateBlock: 0.97, DuplicateFlag: 0.78, Insight: 0.68, Standing: 0.69,
            Contradiction: 0.66),
    };

    // What an install reads: today's, unless the settings ask for the live model's tuned ones (adapters.embeddings.
    // thresholds: tuned, off by default) or the live model is not MiniLM, whose scores today's were never meant for. The
    // second cannot happen yet: another live model is refused at start until it passes the retrieval gate (D-025).
    public static SimilarityThresholds For(EmbeddingSettings settings) =>
        settings.TunedThresholds || settings.Model != EmbeddingModels.Default ? Tuned.GetValueOrDefault(settings.Model.Id, Today) : Today;

    // What a reader takes in place of its own settings' numbers (the map's graph.similarity), decided here once: null while
    // today's apply (MiniLM, thresholds not tuned), else what For reads, so another live model's own thresholds stand in
    // whatever the key says.
    public static SimilarityThresholds? Override(EmbeddingSettings settings) =>
        settings.TunedThresholds || settings.Model != EmbeddingModels.Default ? For(settings) : null;
}

// The map's similar and copy links with graph.similarity.model set (D-256): the model whose vectors they compare and the
// thresholds they read, its own recorded ones (EmbeddingThresholds.Tuned), never graph.similarity's numbers, which sit on
// the live model's scale; a number set to null still turns its rule off (DocumentThreshold: documents read the main one;
// MutualThreshold: no mutual rule), and graph.similarity.neighbors is kept as set.
public sealed record MapSimilarity(EmbeddingModel Model, double Threshold, double? DocumentThreshold, double? MutualThreshold)
{
    public static MapSimilarity For(EmbeddingModel model, RhythmConfig.SimilaritySection similarity)
    {
        var t = EmbeddingThresholds.Tuned[model.Id];
        return new(model, t.Similar, similarity.DocumentThreshold is null ? null : t.SimilarDocuments, similarity.MutualThreshold is null ? null : t.Mutual);
    }
}

// graph.similarity.model (D-256): a second model whose vectors make the map's similar and copy links and nothing else.
// The live model (MiniLM, D-025) keeps every other job: search, recall, passages, saved documents, memory writes (links on
// save, duplicates, contradictions, standing instructions) and the insight candidates. Only bge-small is accepted: the one
// model with recorded thresholds whose map links were measured (benchmarks.md, the map model). Absent, nothing changes.
public static class MapModels
{
    public static IReadOnlyList<EmbeddingModel> Allowed { get; } = [EmbeddingModels.BgeSmall];

    // The model the key names, or null when it is absent; any other value stops the engine at start naming the key.
    public static EmbeddingModel? Named(string? name) => name is null or "" ? null
        : Allowed.FirstOrDefault(m => m.Name == name)
            ?? throw new ConfigException($"graph.similarity.model is {string.Join(" or ", Allowed.Select(m => m.Name))}, or absent for the live embeddings model's vectors");

    // The map model the engine loads and the map reads: none while the map draws no similar links (graph.similarity.
    // threshold null) or the embeddings adapter is off, when no star has a vector at all.
    public static EmbeddingModel? Wanted(RhythmConfig config) =>
        config.Graph.Similarity.Threshold is null || !EmbeddingSettings.From(config).Enabled ? null : Named(config.Graph.Similarity.Model);
}
