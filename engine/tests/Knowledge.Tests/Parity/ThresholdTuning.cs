using Knowledge.Relationships;

namespace Knowledge.Tests.Parity;

// SB-P2-13: the similarity thresholds re-tuned per model from the labelled pairs (pairs.json, SB-P2-18), by rules fixed
// before the first run (docs/dev/second-brain-parity/benchmarks.md, "How the thresholds were chosen"). Each kind asks one
// question of a pair's score: is it at least the threshold? The score is the dot product of the two texts' vectors made
// by the map's own recipe (SimilaritySuggestions.Vector: the mean of a text's first four passages, unit length, rounded).
// The map's kinds (similar, and the document and mutual thresholds' corpus) embed each stored record as the map does, its
// title and its text (ParityMapSource: a memory's title is SecondBrainAdapter.Title of its words, a note's its own, a
// document's in its text already; an incoming save has none), so a map threshold means the same here as on the map. The
// kinds read on a save (linkOnSave, the duplicates, insight, standing) embed the words alone, as a save compares a new
// memory's words, which have no title, with what is stored (run 18's review). The kinds and their labels:
//   similar         the map's dashed "similar" links (graph.similarity.threshold): positives duplicate, near-duplicate,
//                   contradiction, related and insight (two records a reader wants shown together), negatives unrelated.
//                   Balanced: the threshold that best tells the two apart (below).
//   linkOnSave      an automatic link written on save (SB-P4-03): the same labels over the pairs a link on save compares
//                   (LinkPairs: two memories of one scope, or a memory and an incoming save, neither held for review;
//                   SB-P4-03 links only those, so a pair across scopes, or with a note or a document, is never
//                   compared), scored as a save reads them (the saved words alone against the stored memory's passages,
//                   the best; run 19), and precision first, since a written link is kept: the lowest threshold no
//                   unrelated pair reaches.
//   duplicateBlock  a save refused as a copy (SB-P5-02): positives duplicate, every other pair negative; precision first.
//   duplicateFlag   a save flagged for review (SB-P5-02): positives duplicate and near-duplicate, every other pair
//                   negative; balanced.
//   insight         a candidate pair for the nightly insight (SB-P5-05): stored pairs written 30 or more days apart;
//                   positives insight, negatives related and unrelated (the labels' order puts insight first, so neither
//                   is one); duplicates and contradictions, which have their own review, are left out. Balanced.
//   standing        a standing instruction that fires for a question (SB-P5-12): the standing cases' instructions
//                   (standing-cases.json) against their own questions, one another's and every golden query, each question
//                   embedded as the model embeds questions (no instruction, D-068), positive where it should fire, an intent
//                   question reported apart (Standing), and precision first since SB-P5-12: a wrong instruction applied is
//                   worse than one not applied, and run 18's balanced rule on two positives fired for 31 other questions.
//   contradiction   a close memory offered to the calling AI and queued for a person (SB-P5-03): the pairs a write compares
//                   (WriteCompared: two memories of one scope, or a memory and an incoming save, neither held), the words
//                   alone on both sides, as DuplicateCheck scores them: positives contradiction, negatives insight, related
//                   and unrelated; a duplicate and a near-duplicate are left out, since no score tells them from a
//                   contradiction (D-211) and they have their own review. Balanced.
// Balanced is the largest mean of recall on the positives and recall on the negatives (so neither the many related
// pairs nor the few unrelated ones decide it alone), on a 0.01 grid from 0.20 to 0.99; among equals, the middle one
// (the lower of two middles), so the threshold sits between the scores it separates rather than on one. Precision first
// is the lowest grid value above every negative's score; with a negative at 0.99 or more no grid value is, and the kind
// keeps its starting value. The map's document and mutual thresholds have too few labelled pairs to tune (two
// document pairs, and mutual closeness depends on every record's neighbours): MiniLM keeps today's, and another model
// takes the value with the same share of the corpus's pairs at or above it as today's has under MiniLM.
// Score: the pair's dot product as the kind reads it (the map's vectors or the words alone). SameScope: both records in
// one scope.
public sealed record PairScore(string Id, string Label, bool Hard, double Score, bool Stored, double Days, bool SameScope);

// One labelled pair of a standing instruction and a question, as ThresholdTuning.Standing scores it. Kind: fire, near,
// intent, other (another instruction's question) or golden; Positive: whether it should fire, null for what is reported apart.
public sealed record StandingPoint(string Instruction, string Question, string Kind, bool? Positive, double Score);

public sealed record Confusion(int TruePositives, int FalsePositives, int FalseNegatives, int TrueNegatives)
{
    public int Positives => TruePositives + FalseNegatives;
    public int Negatives => FalsePositives + TrueNegatives;
    public double? Precision => TruePositives + FalsePositives == 0 ? null : (double)TruePositives / (TruePositives + FalsePositives);
    public double? Recall => Positives == 0 ? null : (double)TruePositives / Positives;
    public double? Specificity => Negatives == 0 ? null : (double)TrueNegatives / Negatives;
    public double Balanced => ((Recall ?? 0) + (Specificity ?? 0)) / 2;
}

// One kind's threshold for one model: today's (EmbeddingThresholds.Today), the chosen one, and both counts.
public sealed record TunedThreshold(string Kind, string Rule, double Today, double Chosen, Confusion AtToday, Confusion AtChosen);

public static class ThresholdTuning
{
    public const string Balanced = "balanced", PrecisionFirst = "precision first", Transferred = "same share of pairs";
    public static readonly string[] Kinds = ["similar", "similarDocuments", "mutual", "linkOnSave", "duplicateBlock", "duplicateFlag", "insight", "standing", "contradiction"];
    static readonly string[] Together = ["duplicate", "near-duplicate", "contradiction", "related", "insight"];
    public static IReadOnlyList<double> Grid { get; } = [.. Enumerable.Range(20, 80).Select(i => i / 100.0)];

    public static float[] Vector(IEmbedder model, string text, string title = "") =>
        SimilaritySuggestions.Vector(model.Embed, model.Chunks, title, text) ?? throw new ArgumentException("no vector for an empty text");

    // A record's title as the map gives it: a memory's from its words, a note's own; none for a document (its title is in
    // its member text already) or an incoming save.
    public static IReadOnlyDictionary<string, string> MapTitles(ParityCorpus corpus) =>
        corpus.Texts.Where(t => t.Kind != "document").ToDictionary(t => t.Id, t => t.Title, StringComparer.Ordinal);

    // A pair member's vector, made once: with its map title when titles names one, else its words alone.
    static Func<string, float[]> Vectors(IEmbedder model, IReadOnlyDictionary<string, ParityMember> members, IReadOnlyDictionary<string, string>? titles)
    {
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        return id => vectors.TryGetValue(id, out var v) ? v
            : vectors[id] = Vector(model, members[id].Text, titles is not null && titles.TryGetValue(id, out var title) ? title : "");
    }
    public static double Score(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return Math.Round(sum, 4);
    }

    // Every labelled pair's score under this model: with the map's vectors (map) or the words alone.
    public static IReadOnlyList<PairScore> Pairs(IEmbedder model, ParityCorpus corpus, ParityPairs pairs, bool map = false)
    {
        var members = pairs.Members(corpus);
        var of = Vectors(model, members, map ? MapTitles(corpus) : null);
        return [.. pairs.Pairs.Select(p => new PairScore(p.Id, p.Label, p.Hard, Score(of(p.A), of(p.B)),
            members[p.A].Kind != "incoming" && members[p.B].Kind != "incoming", Math.Abs((members[p.B].Written - members[p.A].Written).TotalDays),
            members[p.A].Scope == members[p.B].Scope))];
    }

    // Every labelled pair of a standing instruction and a question (SB-P5-12): each instruction of the standing cases, with
    // the questions written for it and those written for the others and every golden query, the question embedded as the
    // model embeds questions (no instruction, D-068) and the instruction by the map's recipe from its words alone, as a save
    // reads them. Positive: fire (written for it, an also labelled fire, or a golden query that names it relevant). Null:
    // intent (its subject, another purpose), reported apart and counted neither way. False: every other pair, a near miss,
    // another instruction's question or a golden query, whether also labels it near or it was not read.
    public static IReadOnlyList<StandingPoint> Standing(IEmbedder model, ParityCorpus corpus, ParityGolden golden, ParityStanding cases)
    {
        var questions = new Dictionary<string, float[]>(StringComparer.Ordinal);
        float[] Question(string text) => questions.TryGetValue(text, out var v) ? v : questions[text] = model.Embed(text);
        var also = cases.Also.ToDictionary(a => (a.Instruction, a.Golden ?? a.Query!), a => a.Label);
        var points = new List<StandingPoint>();
        foreach (var i in cases.Instructions)
        {
            var words = i.Text ?? corpus.Memories.Single(m => m.Id == i.Id).Text;
            var vector = Vector(model, words);
            double Scored(string question) => Score(Question(question), vector);
            bool? Labelled(string key) => also.TryGetValue((i.Id, key), out var label) && label != "near" ? label == "fire" ? true : null : false;
            foreach (var q in i.Fire) points.Add(new(i.Id, q, "fire", true, Scored(q)));
            foreach (var q in i.Near) points.Add(new(i.Id, q, "near", false, Scored(q)));
            foreach (var q in i.Intent) points.Add(new(i.Id, q, "intent", null, Scored(q)));
            foreach (var other in cases.Instructions.Where(o => o.Id != i.Id))
                foreach (var q in other.Fire.Concat(other.Near).Concat(other.Intent))
                    points.Add(new(i.Id, q, "other", Labelled(q), Scored(q)));
            foreach (var q in golden.Queries)
                points.Add(new(i.Id, q.Id, "golden", q.Relevant.Any(r => r.Id == i.Id) ? true : Labelled(q.Id), Scored(q.Query)));
        }
        return points;
    }

    // The labelled pairs a link on save compares (SB-P4-03; run 19, narrowing run 18's pairs in one scope to these): two
    // memories of one scope, or a memory and an incoming save (a memory being saved), neither held for review (the
    // corpus's controlled samples, which a save holds: a held memory has no passages and is never linked); b is the save
    // and a what is stored. Each scored as MemoryLinkInference reads it: the saved words alone (MemoryLinkInference.Query,
    // the map's recipe with no title) against each of the stored memory's passages as the index keeps them (its chunks,
    // each embedded), the best, rounded to 4 places.
    public static IReadOnlyList<PairScore> LinkPairs(IEmbedder model, ParityCorpus corpus, ParityPairs pairs)
    {
        var members = pairs.Members(corpus);
        var saved = Saved(corpus);
        var score = SaveScorer(model);
        return [.. pairs.Pairs.Where(p => members[p.A].Scope == members[p.B].Scope && saved(members[p.A]) && saved(members[p.B])).Select(p =>
            new PairScore(p.Id, p.Label, p.Hard, score(members[p.A], members[p.B]), members[p.B].Kind != "incoming",
                Math.Abs((members[p.B].Written - members[p.A].Written).TotalDays), true))];
    }

    // The labelled pairs a write's check compares (SB-P5-03): two memories of one scope, or a memory and an incoming save,
    // neither held for review; each scored as DuplicateCheck reads it, the words alone on both sides.
    public static IReadOnlyList<PairScore> WriteCompared(IEmbedder model, ParityCorpus corpus, ParityPairs pairs)
    {
        var members = pairs.Members(corpus);
        var saved = Saved(corpus);
        var of = Vectors(model, members, null);
        return [.. pairs.Pairs.Where(p => members[p.A].Scope == members[p.B].Scope && saved(members[p.A]) && saved(members[p.B])).Select(p =>
            new PairScore(p.Id, p.Label, p.Hard, Score(of(p.A), of(p.B)), members[p.B].Kind != "incoming",
                Math.Abs((members[p.B].Written - members[p.A].Written).TotalDays), true))];
    }

    // Every pair a link on save compares in the corpus, labelled or not: two members Saved finds in one scope, the earlier
    // stored (a) and the later its save (b), never two incoming saves (neither is stored), each scored as the save reads it.
    public static IReadOnlyList<(string A, string B, double Score)> SavePairs(IEmbedder model, ParityCorpus corpus, ParityPairs pairs)
    {
        var saved = Saved(corpus);
        var members = pairs.Members(corpus).Values.Where(saved).OrderBy(m => m.Written).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var score = SaveScorer(model);
        var found = new List<(string, string, double)>();
        for (var i = 0; i < members.Length; i++)
            for (var j = i + 1; j < members.Length; j++)
            {
                var (a, b) = members[i].Kind == "incoming" ? (members[j], members[i]) : (members[i], members[j]);
                if (a.Scope == b.Scope && a.Kind != "incoming") found.Add((a.Id, b.Id, score(a, b)));
            }
        return found;
    }

    // What a link on save compares: a memory, stored or an incoming save, that a save does not hold for review.
    static Func<ParityMember, bool> Saved(ParityCorpus corpus)
    {
        var held = corpus.Memories.Where(m => m.Expect is { Held: true }).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return m => m.Kind is "memory" or "incoming" && !held.Contains(m.Id);
    }

    // A save's score against a stored record: its words alone against the stored one's passages, the best.
    static Func<ParityMember, ParityMember, double> SaveScorer(IEmbedder model)
    {
        var queries = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var passages = new Dictionary<string, float[][]>(StringComparer.Ordinal);
        return (stored, save) =>
        {
            var query = queries.TryGetValue(save.Id, out var q) ? q
                : queries[save.Id] = MemoryLinkInference.Query(model, save.Text) ?? throw new ArgumentException("no vector for an empty text");
            var kept = passages.TryGetValue(stored.Id, out var k) ? k : passages[stored.Id] = [.. model.Chunks(stored.Text).Select(model.Embed)];
            return kept.Max(c => Score(query, c));
        };
    }

    // The vectors of every stored record of the corpus (memories, notes, documents) as the map embeds them.
    public static float[][] MapVectors(IEmbedder model, ParityCorpus corpus, ParityPairs pairs)
    {
        var members = pairs.Members(corpus);
        var of = Vectors(model, members, MapTitles(corpus));
        return [.. members.Values.Where(m => m.Kind != "incoming").OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => of(m.Id))];
    }

    // The scores of every two stored records of the corpus as the map scores them, for the document and mutual
    // thresholds' share.
    public static double[] Corpus(IEmbedder model, ParityCorpus corpus, ParityPairs pairs)
    {
        var vectors = MapVectors(model, corpus, pairs);
        var scores = new List<double>();
        for (var i = 0; i < vectors.Length; i++) for (var j = i + 1; j < vectors.Length; j++) scores.Add(Score(vectors[i], vectors[j]));
        return [.. scores];
    }

    public static Confusion Count(IEnumerable<(bool Positive, double Score)> scored, double threshold)
    {
        int tp = 0, fp = 0, fn = 0, tn = 0;
        foreach (var (positive, score) in scored)
            if (score >= threshold) { if (positive) tp++; else fp++; }
            else if (positive) fn++; else tn++;
        return new(tp, fp, fn, tn);
    }

    public static double Choose(IReadOnlyList<(bool Positive, double Score)> scored, string rule, double fallback)
    {
        if (rule == PrecisionFirst)
        {
            var highest = scored.Where(s => !s.Positive).Select(s => s.Score).DefaultIfEmpty(double.NegativeInfinity).Max();
            return Grid.Where(t => t > highest).Select(t => (double?)t).FirstOrDefault() ?? fallback;
        }
        var balanced = Grid.Select(t => (t, Count(scored, t).Balanced)).ToArray();
        var best = balanced.Max(b => b.Balanced);
        var equal = balanced.Where(b => Math.Abs(b.Balanced - best) < 1e-12).Select(b => b.t).ToArray();
        return equal[(equal.Length - 1) / 2];
    }

    // The value of `model`'s scores with the same share of `scores` at or above it as `today` has in `reference`'s: the
    // lowest grid value whose share is no larger.
    public static double Transfer(double today, double[] reference, double[] scores)
    {
        var share = reference.Count(s => s >= today) / (double)reference.Length;
        return Grid.Where(t => scores.Count(s => s >= t) / (double)scores.Length <= share).Select(t => (double?)t).FirstOrDefault() ?? Grid[^1];
    }

    // Every kind's threshold for `model`, with `reference` MiniLM's corpus scores (null when `model` is MiniLM).
    public static IReadOnlyList<TunedThreshold> Tune(IEmbedder model, ParityCorpus corpus, ParityPairs pairs, ParityGolden golden, double[]? reference = null)
    {
        var today = EmbeddingThresholds.Today;
        // The map's kinds score each stored record as the map does; the kinds read on a save, the words alone.
        var mapped = Pairs(model, corpus, pairs, map: true);
        var scores = Pairs(model, corpus, pairs);
        (bool, double)[] Labelled(Func<PairScore, bool> eligible, Func<PairScore, bool> positive, IReadOnlyList<PairScore>? from = null) =>
            [.. (from ?? scores).Where(eligible).Select(s => (positive(s), s.Score))];
        var shown = Labelled(_ => true, s => Together.Contains(s.Label), mapped);
        // SB-P4-03 links a saved memory only to memories of its own scope, as a save reads them (LinkPairs).
        var linked = Labelled(_ => true, s => Together.Contains(s.Label), LinkPairs(model, corpus, pairs));
        var block = Labelled(_ => true, s => s.Label == "duplicate");
        var flag = Labelled(_ => true, s => s.Label is "duplicate" or "near-duplicate");
        var insight = Labelled(s => s.Stored && s.Days >= ParityPairs.InsightGap.TotalDays && s.Label is "insight" or "related" or "unrelated", s => s.Label == "insight");
        var standing = Standing(model, corpus, golden, ParityStanding.Load()).Where(s => s.Positive is not null).Select(s => (s.Positive!.Value, s.Score)).ToArray();
        var contradiction = Labelled(s => s.Label is not ("duplicate" or "near-duplicate"), s => s.Label == "contradiction", WriteCompared(model, corpus, pairs));
        var mine = reference is null ? null : Corpus(model, corpus, pairs);
        TunedThreshold Tuned(string kind, string rule, double start, (bool, double)[] scored)
        {
            var chosen = Choose(scored, rule, start);
            return new(kind, rule, start, chosen, Count(scored, start), Count(scored, chosen));
        }
        TunedThreshold Shared(string kind, double start, (bool, double)[] scored)
        {
            var chosen = reference is null ? start : Transfer(start, reference, mine!);
            return new(kind, Transferred, start, chosen, Count(scored, start), Count(scored, chosen));
        }
        return
        [
            Tuned("similar", Balanced, today.Similar, shown),
            // Counted on the pairs whose two records are both documents (the only ones this threshold reads): too few to
            // tune (benchmarks.md), so the counts are information.
            Shared("similarDocuments", today.SimilarDocuments, [.. mapped.Zip(pairs.Pairs).Where(x => Kind(corpus, x.Second.A) == "document" && Kind(corpus, x.Second.B) == "document")
                .Select(x => (Together.Contains(x.First.Label), x.First.Score))]),
            Shared("mutual", today.Mutual, shown),
            Tuned("linkOnSave", PrecisionFirst, today.LinkOnSave, linked),
            Tuned("duplicateBlock", PrecisionFirst, today.DuplicateBlock, block),
            Tuned("duplicateFlag", Balanced, today.DuplicateFlag, flag),
            Tuned("insight", Balanced, today.Insight, insight),
            Tuned("standing", PrecisionFirst, today.Standing, standing),
            Tuned("contradiction", Balanced, today.Contradiction, contradiction),
        ];
    }

    static string Kind(ParityCorpus corpus, string id) =>
        corpus.Documents.Any(d => d.Id == id) ? "document" : corpus.Notes.Any(n => n.Id == id) ? "note" : "memory";

    // The tuned set as the engine records it (EmbeddingThresholds.Tuned).
    public static SimilarityThresholds Thresholds(IReadOnlyList<TunedThreshold> tuned)
    {
        double Of(string kind) => tuned.Single(t => t.Kind == kind).Chosen;
        return new(Of("similar"), Of("similarDocuments"), Of("mutual"), Of("linkOnSave"), Of("duplicateBlock"), Of("duplicateFlag"),
            Of("insight"), Of("standing"), Of("contradiction"));
    }
}
