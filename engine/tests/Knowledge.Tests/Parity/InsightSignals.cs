using System.Text.RegularExpressions;

namespace Knowledge.Tests.Parity;

// SB-P5-05: what says a pair of memories is worth a reasoning client's look, measured on the labelled pairs the insight
// candidate pass can see: two native memories of one scope, both eligible (current, not replaced, not deprecated, not a
// planted instruction), the newer written 30 or more days after the older, from pairs.json and insight-pairs.json;
// insight is positive, related and unrelated negative (duplicates and contradictions have their own review). Each signal
// is counted as the engine decides it (InsightCandidates.Signals, InsightCandidates.Similarity of InsightCandidates.Vector),
// and the ones the engine does not use are counted here only, so a later item can see what they would have done:
//   similar     the words alone at or above an insight threshold (today's 0.80, or a model's tuned one; needs the model)
//   weighty     both of importance 3 or more
//   rule        similar or weighty: what the pass writes
//   sharedTag   a tag both carry                       (not used: it finds related pairs, not insights)
//   sameProject the same project                       (not used: the same)
//   rareWord    a word of four letters or more that at most three of the scope's eligible memories use, a name or a thing
//               only they mention                      (not used: it finds about as many negatives as positives)
//   floor       both at least 80 characters, upstream's eligibility floor (not used: it leaves insight pairs out)
public sealed record InsightLabelled(string Id, string Label, ParityMemory Older, ParityMemory Newer)
{
    public bool Positive => Label == "insight";
}

public static partial class InsightSignals
{
    public static readonly string[] Measured = ["weighty", "sharedTag", "sameProject", "rareWord", "floor"];
    public const int RareMost = 3, FloorChars = 80;

    // Eligible as 064 sees a corpus memory: current, not replaced, not deprecated, not one 036 would hold.
    public static bool Eligible(ParityCorpus corpus, ParityMemory m) =>
        m.Status != "deprecated" && m.Expect is null && m.ReplacedBy is null && (m.ValidUntil is null || m.ValidUntil > DateOnly.FromDateTime(corpus.Now.UtcDateTime));

    // Every labelled pair the pass can see, from the given files, older memory first.
    public static IReadOnlyList<InsightLabelled> Pairs(ParityCorpus corpus, params ParityPairs[] files)
    {
        var memories = corpus.Memories.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var pairs = new List<InsightLabelled>();
        foreach (var p in files.SelectMany(f => f.Pairs).Where(p => p.Label is "insight" or "related" or "unrelated"))
        {
            if (!memories.TryGetValue(p.A, out var a) || !memories.TryGetValue(p.B, out var b)) continue;
            var (older, newer) = a.CreatedAt <= b.CreatedAt ? (a, b) : (b, a);
            if (a.Scope != b.Scope || !Eligible(corpus, a) || !Eligible(corpus, b) || newer.CreatedAt - older.CreatedAt < InsightCandidates.Gap) continue;
            pairs.Add(new(p.Id, p.Label, older, newer));
        }
        return pairs;
    }

    public static bool Weighty(InsightLabelled p) =>
        InsightCandidates.Signals(double.NegativeInfinity, double.PositiveInfinity, p.Older.Importance, p.Newer.Importance).Contains(InsightCandidates.Weighty);

    public static bool SharedTag(InsightLabelled p) => p.Older.Tags.Intersect(p.Newer.Tags, StringComparer.Ordinal).Any();
    public static bool SameProject(InsightLabelled p) => p.Older.Project is { } project && project == p.Newer.Project;
    public static bool Floor(InsightLabelled p) => p.Older.Text.Length >= FloorChars && p.Newer.Text.Length >= FloorChars;

    [GeneratedRegex(@"\p{L}{4,}")] private static partial Regex Word();
    static HashSet<string> Words(string text) => [.. Word().Matches(text.ToLowerInvariant()).Select(m => m.Value)];

    // A word both use that at most RareMost of the scope's eligible memories use.
    public static Func<InsightLabelled, bool> RareWord(ParityCorpus corpus)
    {
        var counts = corpus.Memories.Where(m => Eligible(corpus, m)).GroupBy(m => m.Scope)
            .ToDictionary(g => g.Key, g => g.SelectMany(m => Words(m.Text)).GroupBy(w => w).ToDictionary(w => w.Key, w => w.Count()));
        return p => Words(p.Older.Text).Intersect(Words(p.Newer.Text)).Any(w => counts[p.Older.Scope][w] <= RareMost);
    }

    // Each measured signal that needs no model, by name.
    public static IReadOnlyDictionary<string, Func<InsightLabelled, bool>> WithoutModel(ParityCorpus corpus) => new Dictionary<string, Func<InsightLabelled, bool>>
    {
        ["weighty"] = Weighty, ["sharedTag"] = SharedTag, ["sameProject"] = SameProject, ["rareWord"] = RareWord(corpus), ["floor"] = Floor,
    };

    // Each pair's similarity under this model, the words alone, as the pass scores it.
    public static IReadOnlyDictionary<string, double> Similarities(IEmbedder model, IReadOnlyList<InsightLabelled> pairs) =>
        pairs.ToDictionary(p => p.Id, p => InsightCandidates.Similarity(InsightCandidates.Vector(model, p.Older.Text), InsightCandidates.Vector(model, p.Newer.Text)));

    // similar and the pass's rule at a threshold, as the engine decides them.
    public static bool Similar(InsightLabelled p, double similarity, double threshold) =>
        InsightCandidates.Signals(similarity, threshold, null, null).Contains(InsightCandidates.Similar);
    public static bool Rule(InsightLabelled p, double similarity, double threshold) =>
        InsightCandidates.Signals(similarity, threshold, p.Older.Importance, p.Newer.Importance).Count > 0;

    public static Confusion Count(IEnumerable<InsightLabelled> pairs, Func<InsightLabelled, bool> fires) =>
        ThresholdTuning.Count(pairs.Select(p => (p.Positive, fires(p) ? 1.0 : 0.0)), 1.0);
}
