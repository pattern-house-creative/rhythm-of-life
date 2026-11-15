using System.Globalization;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// SB-P4-12: each map relevance idea's precision and recall on the labelled pairs the map may join, alone and with today's
// map, and the keep rule's verdict, as docs/dev/second-brain-parity/relevance.md records them (RelevanceIdeas has the
// rules). Shared names and "cited together" read no model and always run; today's similar links, the hub correction and
// the near-miss band need the pinned MiniLM files and are skipped without them (D-041).
public sealed class RelevanceIdeasTests(ITestOutputHelper output)
{
    // (true positives, false positives, false negatives, true negatives) over the 124 pairs the map may join since run
    // 19's links slice labelled 104 more pairs (193 in all), the stars as the map lists them (the corpus's stated links
    // as references, which the map never compares). relevance.md gives these, and before them the slice's first numbers
    // on 56 of 89 pairs with stars without references. No idea is kept on either.
    static readonly Dictionary<string, Confusion> Recorded = new()
    {
        ["similar"] = new(20, 7, 53, 44),
        ["stated"] = new(32, 0, 41, 51),
        ["today"] = new(52, 7, 21, 44),
        ["names"] = new(18, 0, 55, 51),
        ["today+names"] = new(56, 7, 17, 44),
        ["hubs"] = new(22, 7, 51, 44),
        ["today with hubs"] = new(54, 7, 19, 44),
        ["review"] = new(9, 27, 64, 24),
        ["today+review, every pair kept"] = new(61, 34, 12, 17),
        ["today+review, a perfect reviewer"] = new(61, 7, 12, 44),
        ["cited"] = new(1, 0, 72, 51),
        ["today+cited"] = new(52, 7, 21, 44),
    };
    // The same rows on stars without references, as the slice first measured them.
    static readonly Dictionary<string, Confusion> WithoutReferences = new()
    {
        ["similar"] = new(38, 3, 35, 48), ["today"] = new(52, 3, 21, 48), ["hubs"] = new(44, 6, 29, 45), ["today with hubs"] = new(53, 6, 20, 45),
    };
    // How many of the corpus's pairs each set links, or for the review the near misses a reviewer would check
    // (information: the scale each idea works at on the map).
    static readonly Dictionary<string, int> CorpusLinks = new()
    {
        ["similar"] = 50, ["names"] = 45, ["hubs"] = 73, ["review"] = 114, ["cited"] = 3,
    };

    static string N(double? x) => x is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "–";
    void Line(string name, Confusion c) => output.WriteLine(
        $"{name,-34} P {N(c.Precision)} R {N(c.Recall)} ({c.TruePositives}, {c.FalsePositives}, {c.FalseNegatives}, {c.TrueNegatives})");

    [Fact] public void Shared_names_and_cited_together_on_the_labelled_pairs()
    {
        var corpus = ParityCorpus.Load();
        var pairs = RelevanceIdeas.MapPairs(corpus, ParityPairs.Load());
        // The pairs the map may join: stored at both ends, one subject's or one's and the household's.
        Assert.Equal(124, pairs.Count);
        Assert.Equal([("contradiction", 11), ("duplicate", 1), ("insight", 10), ("near-duplicate", 1), ("related", 50), ("unrelated", 51)],
            pairs.GroupBy(p => p.Label).Select(g => (g.Key, g.Count())).Order());
        var stars = RelevanceIdeas.Stars(corpus);
        var names = RelevanceIdeas.Names(corpus, stars);
        var cited = RelevanceIdeas.Cited(corpus);
        foreach (var (name, set) in new[] { ("names", names), ("cited", cited) })
        {
            Line(name, RelevanceIdeas.Count(pairs, set));
            Assert.Equal(Recorded[name], RelevanceIdeas.Count(pairs, set));
            Assert.Equal(CorpusLinks[name], set.Count);
        }
        // The pairs shared names find, and the names; none of the 51 unrelated, the hard ones that share a place's word
        // (Gullhaven, Marram) or a common noun (Pepper and pepper, two plots) included.
        Assert.Equal(["p-con-car", "p-con-choir", "p-con-food", "p-con-plot", "p-con-samjob", "p-ins-breakdown-car", "p-rel-bees-guide-harvest",
            "p-rel-car-guide", "p-rel-car-old-mot", "p-rel-checklist-install", "p-rel-choir-concert", "p-rel-conference-train",
            "p-rel-install-handover", "p-rel-job-new-commute-old", "p-rel-party-deposit", "p-rel-plot-new-rent", "p-rel-plot-old-rent",
            "p-rel-rent-handbook"],
            pairs.Where(p => names.Contains(RelevanceIdeas.Pair(p.A, p.B))).Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["p-rel-party-deposit"], pairs.Where(p => cited.Contains(RelevanceIdeas.Pair(p.A, p.B))).Select(p => p.Id));
    }

    [ModelsFact("minilm")] public async Task Each_idea_against_todays_map_on_the_labelled_pairs()
    {
        using var embeddings = new Embeddings(EmbeddingModels.MiniLm);
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        var corpus = ParityCorpus.Load();
        var pairs = RelevanceIdeas.MapPairs(corpus, ParityPairs.Load());
        // The stars as the map lists them: the stated links are references, so the map never compares a stated pair.
        var stars = RelevanceIdeas.Stars(corpus, embeddings.Model!, EmbeddingModels.MiniLmId, references: true);
        var similar = await RelevanceIdeas.Linked(RelevanceIdeas.Today(), stars);
        var stated = RelevanceIdeas.Stated(corpus);
        var today = RelevanceIdeas.Union(similar, stated);
        var names = RelevanceIdeas.Names(corpus, stars);
        var hubs = RelevanceIdeas.Similar(stars, RelevanceIdeas.HubNeighbors);
        // The harness's similarity rule is the map's own: without the correction it links exactly what SimilaritySuggestions does.
        Assert.Equal(similar.Order(), RelevanceIdeas.Similar(stars, null).Order());
        // The near misses a reviewer would check: pairs today's map leaves out, the stated ones never among them.
        var review = RelevanceIdeas.NearMisses(stars, today, RelevanceIdeas.SimilarThreshold);
        var cited = RelevanceIdeas.Cited(corpus);
        var positives = pairs.Where(p => RelevanceIdeas.Together.Contains(p.Label)).Select(p => RelevanceIdeas.Pair(p.A, p.B)).ToHashSet();
        var sets = new Dictionary<string, HashSet<(string, string)>>
        {
            ["similar"] = similar, ["stated"] = stated, ["today"] = today,
            ["names"] = names, ["today+names"] = RelevanceIdeas.Union(today, names),
            ["hubs"] = hubs, ["today with hubs"] = RelevanceIdeas.Union(hubs, stated),
            ["review"] = review, ["today+review, every pair kept"] = RelevanceIdeas.Union(today, review),
            ["today+review, a perfect reviewer"] = RelevanceIdeas.Union(today, review.Where(positives.Contains)),
            ["cited"] = cited, ["today+cited"] = RelevanceIdeas.Union(today, cited),
        };
        foreach (var (name, set) in sets) Line(name, RelevanceIdeas.Count(pairs, set));
        foreach (var name in CorpusLinks.Keys) output.WriteLine($"{name}: {sets[name].Count} of the corpus's pairs");

        // As the slice first measured them, on stars without references (the similarity signal without the map's
        // exclusion of stated pairs): information beside the rows above.
        var plainStars = RelevanceIdeas.Stars(corpus, embeddings.Model!, EmbeddingModels.MiniLmId);
        var plainSimilar = await RelevanceIdeas.Linked(RelevanceIdeas.Today(), plainStars);
        var plainHubs = RelevanceIdeas.Similar(plainStars, RelevanceIdeas.HubNeighbors);
        var plain = new Dictionary<string, Confusion>
        {
            ["similar"] = RelevanceIdeas.Count(pairs, plainSimilar), ["today"] = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(plainSimilar, stated)),
            ["hubs"] = RelevanceIdeas.Count(pairs, plainHubs), ["today with hubs"] = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(plainHubs, stated)),
        };
        foreach (var (name, c) in plain) Line(name + ", no references", c);

        Assert.All(Recorded, r => Assert.Equal(r.Value, RelevanceIdeas.Count(pairs, sets[r.Key])));
        Assert.All(CorpusLinks, c => Assert.Equal(c.Value, sets[c.Key].Count));
        Assert.Equal(WithoutReferences, plain);

        // The keep rule on the pairs: shared names gains 4 pairs shown together and the hub correction 2, neither adding an
        // unrelated one, so both go on to the gate on the map, which fails both (relevance.md); "cited together" adds a
        // pair today's map links already; a reviewer of the near misses would have to turn every unrelated pair of its band
        // down, and none was run.
        var verdicts = new Dictionary<string, bool>
        {
            ["names"] = RelevanceIdeas.Kept(Recorded["today"], Recorded["today+names"]),
            ["hubs"] = RelevanceIdeas.Kept(Recorded["today"], Recorded["today with hubs"]),
            ["review"] = RelevanceIdeas.Kept(Recorded["today"], Recorded["today+review, every pair kept"]),
            ["cited"] = RelevanceIdeas.Kept(Recorded["today"], Recorded["today+cited"]),
        };
        Assert.Equal([true, true, false, false], RelevanceIdeas.Ideas.Select(i => verdicts[i]));
        Assert.True(RelevanceIdeas.Kept(Recorded["today"], Recorded["today+review, a perfect reviewer"]));

        // Information beside the rule: the hub correction at today's volume of threshold-passing pairs, without the
        // neighbour cap, the mutual rule and the stated pairs' exclusion (the corpus's 21 pairs at or above 0.6, by plain and
        // by corrected score; on the first 56 pairs (13, 0, 32, 11) and (17, 0, 28, 11)).
        var hubScores = RelevanceIdeas.HubScores(stars, RelevanceIdeas.HubNeighbors);
        var plainTop = RelevanceIdeas.TopPairs(stars, (i, j) => ThresholdTuning.Score(stars[i].Embedding!, stars[j].Embedding!), 21);
        var correctedTop = RelevanceIdeas.TopPairs(stars, (i, j) => ThresholdTuning.Score(stars[i].Embedding!, stars[j].Embedding!) - (hubScores[i] + hubScores[j]) / 2, 21);
        Line("plain, top 21 pairs", RelevanceIdeas.Count(pairs, plainTop));
        Line("corrected, top 21 pairs", RelevanceIdeas.Count(pairs, correctedTop));
        Assert.Equal(new Confusion(16, 0, 57, 51), RelevanceIdeas.Count(pairs, plainTop));
        Assert.Equal(new Confusion(18, 0, 55, 51), RelevanceIdeas.Count(pairs, correctedTop));
    }
}
