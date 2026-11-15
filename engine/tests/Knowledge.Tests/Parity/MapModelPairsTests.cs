using System.Globalization;
using Knowledge.Relationships;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// D-256: the map's own links with graph.similarity.model: bge-small, on the labelled pairs the map may join (124 of 193,
// RelevanceIdeas.MapPairs), against today's map, with the stars as the map lists them (the corpus's stated links as
// references). Each star keeps MiniLM's vector and gets bge-small's beside it, as a pass gives it, and the links are
// SimilaritySuggestions with the map model and its recorded thresholds, exactly as a rebuild draws them. Needs both models'
// files; skipped without them (D-041). Since D-258 also the map model's links capped a star (graph.similarity.maxLinks: 6, the
// value asked for, and 4, the value the map path measured no worse at), and how the similar links spread over the stars.
public sealed class MapModelPairsTests(ITestOutputHelper output)
{
    // (true positives, false positives, false negatives, true negatives) over the 124 pairs; a positive is a pair a reader
    // wants shown together (RelevanceIdeas.Together), a link the map draws or the corpus states.
    static readonly Dictionary<string, Confusion> Recorded = new()
    {
        ["similar, MiniLM"] = new(20, 7, 53, 44),
        ["today, MiniLM"] = new(52, 7, 21, 44),
        ["similar, bge-small map"] = new(26, 7, 47, 44),
        ["map, bge-small"] = new(58, 7, 15, 44),
        ["similar, bge-small map, cap 4"] = new(21, 6, 52, 45),
        ["map, bge-small, cap 4"] = new(53, 6, 20, 45),
        ["similar, bge-small map, cap 6"] = new(23, 7, 50, 44),
        ["map, bge-small, cap 6"] = new(55, 7, 18, 44),
    };
    // How many of the corpus's pairs each draws (all scopes the map bridges), and of them by the mutual rule.
    static readonly Dictionary<string, (int Links, int Mutual)> Drawn = new()
    {
        ["MiniLM"] = (58, 46),
        ["bge-small map"] = (155, 4),
        ["bge-small map, cap 4"] = (77, 4),
        ["bge-small map, cap 6"] = (103, 4),
    };
    // Per map: the stars with a similar link, the most and the median a star has among them, and the stars that have one
    // without the cap and none with it.
    static readonly Dictionary<string, (int Stars, int Most, double Median, int Lost)> Spread = new()
    {
        ["bge-small map"] = (89, 17, 3, 0),
        ["bge-small map, cap 4"] = (76, 4, 2, 13),
        ["bge-small map, cap 6"] = (80, 6, 2, 9),
    };
    static readonly int[] Caps = [4, 6];

    static string N(double? x) => x is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "-";

    [ModelsFact("minilm", "bge-small")] public async Task The_map_models_links_on_the_labelled_pairs_against_todays()
    {
        using var minilm = new Embeddings(EmbeddingModels.MiniLm);
        using var bge = new Embeddings(EmbeddingModels.BgeSmall);
        Assert.True(minilm.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        Assert.True(bge.TryLoad(EmbeddingModels.Given(EmbeddingModels.BgeSmall)!), "the pinned bge-small files did not verify");
        var corpus = ParityCorpus.Load();
        var pairs = RelevanceIdeas.MapPairs(corpus, ParityPairs.Load());
        Assert.Equal(124, pairs.Count);
        var stars = RelevanceIdeas.Stars(corpus, minilm.Model!, EmbeddingModels.MiniLmId, references: true);
        var other = RelevanceIdeas.Stars(corpus, bge.Model!, EmbeddingModels.BgeSmall.Id, references: true);
        Assert.Equal(stars.Select(s => s.Key), other.Select(s => s.Key));
        var both = stars.Select((s, i) => s with { MapEmbedding = other[i].Embedding, MapEmbeddingModel = EmbeddingModels.BgeSmall.Id }).ToArray();

        var similarity = new RhythmConfig.SimilaritySection();
        var map = MapSimilarity.For(EmbeddingModels.BgeSmall, similarity);
        var today = RelevanceIdeas.Today();
        var mapped = new SimilaritySuggestions(map.Threshold, similarity.Neighbors, map.DocumentThreshold, map.MutualThreshold) { MapModel = EmbeddingModels.BgeSmall };
        var stated = RelevanceIdeas.Stated(corpus);
        var miniLinks = await RelevanceIdeas.Linked(today, both);
        var bgeLinks = await RelevanceIdeas.Linked(mapped, both);
        // The live model's links are today's whatever the map vectors beside them: the harness's stars without them link the same.
        Assert.Equal((await RelevanceIdeas.Linked(today, stars)).Order(), miniLinks.Order());
        var counted = new Dictionary<string, Confusion>
        {
            ["similar, MiniLM"] = RelevanceIdeas.Count(pairs, miniLinks),
            ["today, MiniLM"] = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(miniLinks, stated)),
            ["similar, bge-small map"] = RelevanceIdeas.Count(pairs, bgeLinks),
            ["map, bge-small"] = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(bgeLinks, stated)),
        };
        async Task<(int, int)> Draw(SimilaritySuggestions source)
        {
            var links = (await source.Suggest(both, default)).Where(s => s.Type == SimilaritySuggestions.Type).ToArray();
            return (links.Length, links.Count(l => l.Excerpt.EndsWith("each among the other's closest", StringComparison.Ordinal)));
        }
        var drawn = new Dictionary<string, (int, int)> { ["MiniLM"] = await Draw(today), ["bge-small map"] = await Draw(mapped) };
        async Task<Dictionary<ResourceKey, int>> Degrees(SimilaritySuggestions source) =>
            (await source.Suggest(both, default)).Where(s => s.Type == SimilaritySuggestions.Type).SelectMany(s => new[] { s.Source, s.Target })
                .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        static (int, int, double, int) Of(Dictionary<ResourceKey, int> d, int lost)
        {
            var n = d.Values.Order().ToArray();
            return (n.Length, n[^1], n.Length % 2 == 1 ? n[n.Length / 2] : (n[n.Length / 2 - 1] + n[n.Length / 2]) / 2.0, lost);
        }
        var uncappedDegrees = await Degrees(mapped);
        var spread = new Dictionary<string, (int, int, double, int)> { ["bge-small map"] = Of(uncappedDegrees, 0) };
        foreach (var cap in Caps)
        {
            var capped = new SimilaritySuggestions(map.Threshold, similarity.Neighbors, map.DocumentThreshold, map.MutualThreshold)
                { MapModel = EmbeddingModels.BgeSmall, MaxLinks = cap };
            var cappedLinks = await RelevanceIdeas.Linked(capped, both);
            // The cap only takes links away (D-258).
            Assert.Subset(bgeLinks, cappedLinks);
            counted[$"similar, bge-small map, cap {cap}"] = RelevanceIdeas.Count(pairs, cappedLinks);
            counted[$"map, bge-small, cap {cap}"] = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(cappedLinks, stated));
            drawn[$"bge-small map, cap {cap}"] = await Draw(capped);
            var cappedDegrees = await Degrees(capped);
            Assert.All(cappedDegrees.Values, n => Assert.InRange(n, 1, cap));
            spread[$"bge-small map, cap {cap}"] = Of(cappedDegrees, uncappedDegrees.Keys.Count(k => !cappedDegrees.ContainsKey(k)));
        }
        foreach (var (name, c) in counted)
            output.WriteLine($"{name,-24} P {N(c.Precision)} R {N(c.Recall)} ({c.TruePositives}, {c.FalsePositives}, {c.FalseNegatives}, {c.TrueNegatives})");
        foreach (var (name, d) in drawn) output.WriteLine($"{name}: {d.Item1} similar links over the corpus, {d.Item2} by the mutual rule");
        foreach (var (name, d) in spread) output.WriteLine($"{name}: {d.Item1} stars with a similar link, most {d.Item2}, median {d.Item3}, {d.Item4} lose every one");
        Assert.Equal(Recorded, counted);
        Assert.Equal(Drawn, drawn);
        Assert.Equal(Spread, spread);
    }
}
