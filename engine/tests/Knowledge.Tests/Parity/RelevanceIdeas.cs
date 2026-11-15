using Knowledge.Relationships;

namespace Knowledge.Tests.Parity;

// SB-P4-12: the map relevance ideas 2, 3, 5 and 6 of docs/dev/connection-ideas.md, each measured alone on the labelled
// pairs (pairs.json, SB-P2-18), and each kept only with evidence (docs/dev/second-brain-parity/relevance.md). None was
// kept, so the two that change how the map links live here (RelevanceRules), as the measurement build (41b6a8c) ran them
// on the map. Written after a first probe and before the numbers relevance.md records were taken:
//
// The stars. Each of the corpus's 120 stored records (memories, notes, documents) is one star, as the map lists a record:
// its title and text as the map's hook reads them (ParityMapSource: a memory's title from its words, a note's and a
// document's own), its vector by the map's recipe (SimilaritySuggestions.Vector) and its candidate names
// (SharedNames.Candidates), and its subject as the parity loader gives it. Every record is a star, a replaced fact
// included (the copies mode lists their copies), so this measures each signal, and the gate then measures the map.
//
// The pairs. The labelled pairs whose two records are stored (an incoming save is never on the map) and that the map may
// join: one subject's two records, or one's and the household's (RelationshipDiscovery's bridging rule; a pair across
// Alex's and Sam's scopes is never linked, pairs.json's own rule). Positive: duplicate, near-duplicate, contradiction,
// related and insight (two records a reader wants shown together, ThresholdTuning's "similar"); negative: unrelated.
//
// Today's map on those pairs: the similarity rule with graph.similarity's defaults (0.6, 3 neighbours, documents 0.8,
// mutual 0.45) over the 120 stars, bridging applied as RelationshipDiscovery applies it, and the links the corpus states
// (a memory's, note's or document's own links, which the loaders turn into references). Since run 19's integration the
// stars carry those references (Stars with references), so the similarity rule never compares a stated pair, as the
// map does, and the near-miss band leaves stated pairs out; the slice first counted stars without them (relevance.md).
// Each idea alone: its own links.
// With the idea: today's links and the idea's, except the hub correction, whose corrected similar links stand in place of
// today's similar ones (the stated links kept).
//
// The ideas as built:
//   names   SharedNames over the stars' candidate names (its rules; the household's people by id and name).
//   hubs    the similarity rule with the hub correction (HubCorrection), HubNeighbors 10 (CSLS's own default) and
//           today's thresholds: each threshold admits as many pairs as it does today, by the corrected score.
//   review  the near misses a reviewer would check (idea 5): pairs today's similar links leave out whose plain score is
//           from 0.40 up to the similar threshold, at most 5 a star, the closest first. No reviewer can be run here (no
//           titles model, D-058; no model is downloaded, D-041), so only the band is measured: what a reviewer that keeps
//           every pair adds (the most it can cost) and what a perfect one adds (the most it can gain).
//   cited   two records cited by one insight (corpus.json's insights, each one not dismissed), bridged as above.
//
// The keep rule on the pairs (Kept): with the idea, today's map finds at least 2 more of the pairs shown together and no
// more unrelated pairs (one pair is one label's say, not evidence). An idea that passes it is then run on the map in
// both load modes and kept only if the gate counts no loss (no type loses 0.01 or more on any path against the locks,
// D-056; ParityGate). The review idea cannot pass: no reviewer was run.
public static class RelevanceIdeas
{
    public const double ReviewFloor = 0.40;
    public const int ReviewPerStar = 5, MinGain = 2, HubNeighbors = 10;
    public static readonly string[] Together = ["duplicate", "near-duplicate", "contradiction", "related", "insight"];
    public static readonly string[] Ideas = ["names", "hubs", "review", "cited"];

    public static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
    static string Id(ResourceKey key) => key.ExternalId;
    static bool Bridges(string a, string b) => a == b || a == RelationshipDiscovery.Household || b == RelationshipDiscovery.Household;

    // The household's people as the map's settings name them (rhythm.yaml's people, the parity tests' alex and sam): each id and name.
    public static IReadOnlySet<string> People(ParityCorpus corpus) =>
        SharedNames.People(corpus.People.SelectMany(p => new[] { p.Id, p.Name }));

    // The 120 stars, with their vectors when a model is given (else none). As measured they carry no references; with
    // `references` each carries the corpus's stated links as the map's loader turns them into references, so the map's
    // rule leaves those pairs to the stated link (information beside the recorded rows, RelevanceIdeasTests).
    public static IReadOnlyList<DiscoveryRecord> Stars(ParityCorpus corpus, IEmbedder? model = null, string? modelId = null, bool references = false)
    {
        var keys = corpus.Texts.ToDictionary(t => t.Id, t => new ResourceKey(ParityLoader.Subject(t.Scope), "parity", "parity", t.Id), StringComparer.Ordinal);
        return [.. corpus.Texts.Select(t => new DiscoveryRecord(keys[t.Id],
            ParityLoader.Visibility(t.Scope), t.Kind, t.Title, null, "r1", t.Modified, corpus.Now,
            references ? [.. t.Links.Where(l => keys.ContainsKey(l.Target)).Select(l => new Reference(keys[l.Target], null, l.Type))] : [],
            ContentRevision: "r1",
            Embedding: model is null ? null : ThresholdTuning.Vector(model, t.Text, t.Title), EmbeddingModel: model is null ? null : modelId))];
    }

    // Idea 2: the pairs that share a name, each star's candidates read from its title and text as the map's hook reads them.
    public static HashSet<(string, string)> Names(ParityCorpus corpus, IReadOnlyList<DiscoveryRecord> stars)
    {
        var texts = corpus.Texts.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var candidates = stars.ToDictionary(s => s.Key, s => SharedNames.Candidates(texts[Id(s.Key)].Title, texts[Id(s.Key)].Text));
        return [.. SharedNames.Links(stars, candidates, People(corpus)).Keys.Select(k => Pair(Id(k.Item1), Id(k.Item2)))];
    }

    // Idea 3: the similar links with the hub correction (or, with `hubNeighbors` null, the map's rule as today), bridged.
    public static HashSet<(string, string)> Similar(IReadOnlyList<DiscoveryRecord> stars, int? hubNeighbors) =>
        [.. HubCorrection.Links(stars, Defaults.Threshold!.Value, Defaults.Neighbors, Defaults.DocumentThreshold, Defaults.MutualThreshold, hubNeighbors)
            .Where(k => Bridges(k.Item1.Subject, k.Item2.Subject)).Select(k => Pair(Id(k.Item1), Id(k.Item2)))];

    // The labelled pairs the map may join.
    public static IReadOnlyList<ParityPair> MapPairs(ParityCorpus corpus, ParityPairs pairs)
    {
        var subjects = corpus.Texts.ToDictionary(t => t.Id, t => ParityLoader.Subject(t.Scope), StringComparer.Ordinal);
        return [.. pairs.Pairs.Where(p => subjects.ContainsKey(p.A) && subjects.ContainsKey(p.B) && Bridges(subjects[p.A], subjects[p.B]))];
    }

    // The pairs a suggestion source links among the stars, bridged as the map bridges them.
    public static async Task<HashSet<(string, string)>> Linked(IRelationshipSuggestions source, IReadOnlyList<DiscoveryRecord> stars) =>
        [.. (await source.Suggest(stars, default)).Where(s => Bridges(s.Source.Subject, s.Target.Subject)).Select(s => Pair(Id(s.Source), Id(s.Target)))];

    // graph.similarity's defaults: every install's similar rule.
    static readonly RhythmConfig.SimilaritySection Defaults = new();
    public static double SimilarThreshold => Defaults.Threshold!.Value;
    public static SimilaritySuggestions Today() =>
        new(Defaults.Threshold!.Value, Defaults.Neighbors, Defaults.DocumentThreshold, Defaults.MutualThreshold);

    // Each star's hub score as the correction reads it (the mean of its k closest scores), on the rounded scores.
    public static double[] HubScores(IReadOnlyList<DiscoveryRecord> stars, int k) => [.. stars.Select((a, i) =>
        stars.Where((_, j) => j != i).Select(b => ThresholdTuning.Score(a.Embedding!, b.Embedding!)).OrderDescending().Take(k).Average())];

    // The n best of every two stars by a score (ties by the lower indexes), whatever their subjects: a threshold's worth
    // of the corpus's pairs.
    public static HashSet<(string, string)> TopPairs(IReadOnlyList<DiscoveryRecord> stars, Func<int, int, double> score, int n) =>
        [.. Enumerable.Range(0, stars.Count).SelectMany(i => Enumerable.Range(i + 1, stars.Count - i - 1).Select(j => (i, j, Score: score(i, j))))
            .OrderByDescending(p => p.Score).ThenBy(p => p.i).ThenBy(p => p.j).Take(n).Select(p => Pair(Id(stars[p.i].Key), Id(stars[p.j].Key)))];

    // The links the corpus states between its records.
    public static HashSet<(string, string)> Stated(ParityCorpus corpus) => [.. corpus.Texts.SelectMany(t => t.Links.Select(l => Pair(t.Id, l.Target)))];

    // Idea 6: two records cited by one insight.
    public static HashSet<(string, string)> Cited(ParityCorpus corpus)
    {
        var subjects = corpus.Texts.ToDictionary(t => t.Id, t => ParityLoader.Subject(t.Scope), StringComparer.Ordinal);
        var pairs = new HashSet<(string, string)>();
        foreach (var insight in corpus.Insights)
        {
            var cited = insight.Cites.Where(subjects.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
            for (var i = 0; i < cited.Length; i++)
            for (var j = i + 1; j < cited.Length; j++)
                if (Bridges(subjects[cited[i]], subjects[cited[j]])) pairs.Add(Pair(cited[i], cited[j]));
        }
        return pairs;
    }

    // Idea 5's band: the pairs not linked today scoring from ReviewFloor up to the similar threshold, at most
    // ReviewPerStar a star (each star's closest first; a pair counts once either star keeps it), bridged.
    public static HashSet<(string, string)> NearMisses(IReadOnlyList<DiscoveryRecord> stars, ISet<(string, string)> today, double threshold)
    {
        var band = new HashSet<(string, string)>();
        foreach (var a in stars)
        {
            var near = stars.Where(b => b != a && Bridges(a.Key.Subject, b.Key.Subject) && !today.Contains(Pair(Id(a.Key), Id(b.Key))))
                .Select(b => (b, Score: ThresholdTuning.Score(a.Embedding!, b.Embedding!))).Where(x => x.Score >= ReviewFloor && x.Score < threshold)
                .OrderByDescending(x => x.Score).ThenBy(x => Id(x.b.Key), StringComparer.Ordinal).Take(ReviewPerStar);
            foreach (var (b, _) in near) band.Add(Pair(Id(a.Key), Id(b.Key)));
        }
        return band;
    }

    public static Confusion Count(IReadOnlyList<ParityPair> pairs, ISet<(string, string)> linked) =>
        ThresholdTuning.Count(pairs.Select(p => (Together.Contains(p.Label), linked.Contains(Pair(p.A, p.B)) ? 1.0 : 0.0)), 0.5);

    // The keep rule on the pairs: at least MinGain more pairs shown together, and no more unrelated ones.
    public static bool Kept(Confusion today, Confusion with) =>
        with.TruePositives - today.TruePositives >= MinGain && with.FalsePositives <= today.FalsePositives;

    public static HashSet<(string, string)> Union(params IEnumerable<(string, string)>[] sets)
    {
        var all = new HashSet<(string, string)>();
        foreach (var s in sets) all.UnionWith(s);
        return all;
    }
}
