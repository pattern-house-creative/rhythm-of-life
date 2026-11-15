using System.Text.RegularExpressions;

namespace Knowledge.Relationships;

// Local similarity: each memory or readable document carries one normalized vector derived in the engine
// from its text (the text itself is never stored). Two records with the same title (ignoring "Copy of",
// "(1)" and file extensions) whose vectors are near-identical become an undirected `copy_of` link,
// inferred rather than verified. Otherwise up to `neighbors` nearest records above the threshold become
// undirected `similar_to` suggestions; document↔document pairs use their own, usually higher, threshold. With a
// `mutualThreshold`, two records that are each among the other's `neighbors` closest are similar too from that lower
// score up: the scores of a small model bunch together, and a record's own closest stand out where one bar can't.
// Pairs already linked by any reference are skipped. Neither kind is ever verified. Data stars are never copies: two
// with one title are one person's numbers from two providers (an archive and a live feed), at most similar. Only
// vectors of one model are compared (ModelOf): two models' scores mean different things even at the same length.
// With memory.scopes on, `scopes` lists the catalog's subjects, and every pair of scopes a link may join (one subject's
// own records, or one subject's and the household's, RelationshipDiscovery's bridging rule) is compared on its own:
// neighbours, mutual closest and the caps on how many links are kept are each that pair's, over its two scopes'
// records only, so no record of another scope takes a neighbour's place or a share of the caps (docs/dev/memory.md
// section 8, Budgets). A subject not in `scopes` (a former person, whom no reader reads) is compared with no one, and a
// person's data star, which a source share may let Family read, with its own subject's records only. With MapModel set
// (graph.similarity.model, D-256) every pair is compared by that model's vectors (MapEmbedding) in place of the live
// model's, and a record without one of that model is compared with no one; the thresholds are that model's
// (MapSimilarity), and nothing else about the links changes but their method's name. With MaxLinks set
// (graph.similarity.maxLinks, D-258) a similar link is kept only while it is among the MaxLinks strongest similar links of
// both its stars, the mutual rule's ranked by score with the rest: `neighbors` caps what a star picks, this what it keeps,
// so a general record close to everything links to its own closest rather than to everything that picked it. Copies are
// neither capped nor counted.
public sealed partial class SimilaritySuggestions(double threshold, int neighbors, double? documentThreshold = null,
    double? mutualThreshold = null, IReadOnlyList<string>? scopes = null) : IRelationshipSuggestions
{
    public const string Type = "similar_to", CopyType = "copy_of", Method = "local-minilm";
    // The map model whose vectors these links compare (D-256), or null for the live model's (Embedding), as before.
    public EmbeddingModel? MapModel { get; init; }
    // The method the links name: today's, or the map model's.
    string Made => MapModel is null ? Method : "local-" + MapModel.Name;
    // The most similar links a star keeps (D-258), or null for no cap, as before.
    public int? MaxLinks { get; init; }
    public const double CopyThreshold = 0.95;
    // At most this many copy_of and this many similar_to links in a pass (with scopes on, shared out equally among the
    // pairs of scopes); RelationshipDiscovery takes 2000 suggestions in all.
    const int Kept = 1000;

    // The model of every vector stored before tags existed (D-052). Until then the engine could load one model only, the
    // pinned MiniLM whose checksum it checks before loading, at this id. Fixed here, not read from LocalEmbeddings.Model:
    // that id names the current revision and pooling and changes with them, while an untagged vector stays this one.
    public const string Untagged = "minilm-l6-v2-quantized-afdb6f1a-mean-l2-v1";

    // The model that made a record's vector: its tag, else Untagged, so an old vector is reused and compared only as
    // that model's. Null without a vector.
    public static string? ModelOf(DiscoveryRecord record) =>
        record.Embedding is null ? null : record.EmbeddingModel ?? Untagged;

    public Task<IReadOnlyList<Suggestion>> Suggest(IReadOnlyList<DiscoveryRecord> records, CancellationToken ct)
    {
        if (scopes is null) return Task.FromResult(Pairs(records, null, Kept, ct));
        var household = RelationshipDiscovery.Household;
        var pairs = scopes.SelectMany((a, i) => scopes.Skip(i).Where(b => a == b || a == household || b == household).Select(b => (a, b))).ToArray();
        var result = new List<Suggestion>();
        foreach (var (a, b) in pairs)
        {
            // A person's data star may be read by Family (a source share), but its closest household records would be
            // ranked against the person's own records too, so it is compared with its own scope's records only: no link
            // of it that Family can see depends on the person's private rows (docs/dev/memory.md section 8). A star is in
            // the scope of its Reader when it has one (a household copy of a person's private original, served only to
            // that person's readers), so it never takes a Family record's neighbour's place.
            bool Ours(DiscoveryRecord x, DiscoveryRecord y) => (x.ScopeSubject == a && y.ScopeSubject == b || x.ScopeSubject == b && y.ScopeSubject == a)
                && (a == b || !CatalogStars.IsData((x.ScopeSubject == household ? y : x).Key));
            result.AddRange(Pairs([.. records.Where(r => r.ScopeSubject == a || r.ScopeSubject == b)], Ours, Kept / pairs.Length, ct));
        }
        return Task.FromResult<IReadOnlyList<Suggestion>>(result);
    }

    // The links among `records`: every copy pair and each record's closest, as described above; `ours`, when given,
    // keeps only the pairs it names, before at most `kept` of each kind are taken, strongest first.
    IReadOnlyList<Suggestion> Pairs(IReadOnlyList<DiscoveryRecord> records, Func<DiscoveryRecord, DiscoveryRecord, bool>? ours, int kept, CancellationToken ct)
    {
        // Each compared record and its vector: the live model's (Embedding), or with a map model only that model's.
        var vectors = records.Where(r => r.Status == "current" && Of(r) is { Length: > 0 }).ToArray();
        var embedding = vectors.Select(r => Of(r)!).ToArray();
        var models = vectors.Select(r => MapModel is null ? ModelOf(r) : r.MapEmbeddingModel).ToArray();
        var linked = new HashSet<(ResourceKey, ResourceKey)>();
        // With scopes on a URL is told apart within each subject, as RelationshipDiscovery resolves it.
        var byUrl = records.Where(r => RelationshipDiscovery.CanonicalUrl(r.Url) is not null)
            .GroupBy(r => RelationshipDiscovery.CanonicalUrl(r.Url)!).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var record in records)
        foreach (var reference in record.References)
        {
            ResourceKey[] targets = reference.Target is { } target ? [target] : RelationshipDiscovery.CanonicalUrl(reference.Url) is { } url
                && byUrl.TryGetValue(url, out var matches) ? RelationshipDiscovery.UrlTargets(matches, scopes is not null).Select(m => m.Key).ToArray() : [];
            foreach (var to in targets) { linked.Add((record.Key, to)); linked.Add((to, record.Key)); }
        }
        bool Comparable(int i, int j) => i != j && embedding[i].Length == embedding[j].Length
            && string.Equals(models[i], models[j], StringComparison.Ordinal) && !linked.Contains((vectors[i].Key, vectors[j].Key));

        // Identical copies first, across every same-title group, so no copy pair is lost to the neighbour limit.
        var copies = new Dictionary<(int, int), double>();
        foreach (var group in Enumerable.Range(0, vectors.Length).Where(i => !CatalogStars.IsData(vectors[i].Key))
            .GroupBy(i => NormalizedTitle(vectors[i].Title)).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var members = group.ToArray();
            for (var a = 0; a < members.Length; a++)
            for (var b = a + 1; b < members.Length; b++)
                if (Comparable(members[a], members[b]) && Dot(embedding[members[a]], embedding[members[b]]) is var score && score >= CopyThreshold)
                    copies[(Math.Min(members[a], members[b]), Math.Max(members[a], members[b]))] = score;
        }
        var similar = new Dictionary<(int, int), double>();
        // Each record's `neighbors` closest at any score, best first, while mutual pairs are wanted.
        var closest = mutualThreshold is null ? null : new List<(int Index, double Score)>[vectors.Length];
        for (var i = 0; i < vectors.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var nearest = new List<(int Index, double Score)>();
            List<(int Index, double Score)>? best = null;
            if (closest is not null) best = closest[i] = new(neighbors + 1);
            for (var j = 0; j < vectors.Length; j++)
            {
                if (!Comparable(i, j) || copies.ContainsKey((Math.Min(i, j), Math.Max(i, j)))) continue;
                var score = Dot(embedding[i], embedding[j]);
                var bothDocuments = vectors[i].Kind == "document" && vectors[j].Kind == "document";
                if (score >= (bothDocuments ? documentThreshold ?? threshold : threshold)) nearest.Add((j, score));
                if (best is not null && (best.Count < neighbors || score > best[^1].Score))
                {
                    var at = best.FindIndex(n => n.Score < score);
                    best.Insert(at < 0 ? best.Count : at, (j, score));
                    if (best.Count > neighbors) best.RemoveAt(neighbors);
                }
            }
            foreach (var (j, score) in nearest.OrderByDescending(n => n.Score).Take(neighbors))
                similar[(Math.Min(i, j), Math.Max(i, j))] = score;
        }
        var mutual = new HashSet<(int, int)>();
        if (closest is not null)
            for (var i = 0; i < vectors.Length; i++)
            foreach (var (j, score) in closest[i])
                if (i < j && score >= mutualThreshold && closest[j].Exists(n => n.Index == i) && similar.TryAdd((i, j), score))
                    mutual.Add((i, j));
        // One undirected link per pair, oriented by graph identity so its edge ID is stable.
        Suggestion Link((int, int) pair, double score, bool copy)
        {
            var (a, b) = (vectors[pair.Item1].Key, vectors[pair.Item2].Key);
            if (string.CompareOrdinal(a.GraphId, b.GraphId) > 0) (a, b) = (b, a);
            return copy
                ? new(a, b, CopyType, "similarity:" + Made + "+title", $"same title, similarity {score:0.00}", Made, Math.Round(score, 2), false, "inferred")
                : new(a, b, Type, "similarity:" + Made, mutual.Contains(pair) ? $"similarity {score:0.00}, each among the other's closest"
                    : $"similarity {score:0.00}", Made, Math.Round(score, 2), Directed: false);
        }
        bool Included((int, int) pair) => ours is null || ours(vectors[pair.Item1], vectors[pair.Item2]);
        var chosen = similar.Where(p => Included(p.Key));
        if (MaxLinks is { } most) chosen = Capped([.. chosen], most);
        return [.. copies.Where(p => Included(p.Key)).OrderByDescending(p => p.Value).Take(kept).Select(p => Link(p.Key, p.Value, true)),
            .. chosen.OrderByDescending(p => p.Value).Take(kept).Select(p => Link(p.Key, p.Value, false))];

        // The cap (D-258), over the links this comparison would draw, before the pass's own cap: each star's links ranked
        // strongest first, a tie by the other star's graph id, so a rebuild keeps the same ones; a link stays only while it
        // is among the first `most` of both its stars. One pass: a slot a star's link loses at the other end is not refilled.
        IEnumerable<KeyValuePair<(int, int), double>> Capped(KeyValuePair<(int, int), double>[] links, int most)
        {
            var top = links.SelectMany(l => new (int Star, int Other, double Score)[] { (l.Key.Item1, l.Key.Item2, l.Value), (l.Key.Item2, l.Key.Item1, l.Value) })
                .GroupBy(x => x.Star)
                .SelectMany(g => g.OrderByDescending(x => x.Score).ThenBy(x => vectors[x.Other].Key.GraphId, StringComparer.Ordinal).Take(most))
                .Select(x => (x.Star, x.Other)).ToHashSet();
            return links.Where(l => top.Contains((l.Key.Item1, l.Key.Item2)) && top.Contains((l.Key.Item2, l.Key.Item1)));
        }
    }

    // The vector a record is compared by: the live model's, or the map model's when that model made it.
    float[]? Of(DiscoveryRecord r) => MapModel is null ? r.Embedding
        : string.Equals(r.MapEmbeddingModel, MapModel.Id, StringComparison.Ordinal) ? r.MapEmbedding : null;

    static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }

    // "Copy of Budget (1).xlsx" and "budget" name the same thing.
    public static string NormalizedTitle(string title)
    {
        var normalized = title.Trim().ToLowerInvariant();
        if (normalized.StartsWith("copy of ", StringComparison.Ordinal)) normalized = normalized[8..];
        normalized = Extension().Replace(normalized, "");
        return Numbered().Replace(normalized, "").Trim();
    }

    // Text worth embedding: enough real words, and mostly words rather than numbers, codes or the
    // undecodable glyph strings some PDFs yield. Mostly numeric spreadsheets are not compared.
    public static bool Readable(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2000).ToArray();
        var words = tokens.Count(t => t.Trim('.', ',', ';', ':', '!', '?', '(', ')', '"', '\'') is { Length: >= 2 and <= 20 } w
            && w.All(char.IsLetter) && w.Any(c => "aeiouyAEIOUY".Contains(c) || c > 127));
        return words >= 5 && words >= tokens.Length * 0.5;
    }

    // Mean of the first chunks' unit vectors, renormalized and rounded for compact storage.
    public static float[]? Vector(Func<string, float[]> embed, Func<string, IEnumerable<string>> chunks, string title, string text)
    {
        var content = (title + "\n" + text).Trim();
        if (content.Length > 8000) content = content[..8000];
        if (content.Length == 0) return null;
        double[]? sum = null;
        foreach (var part in chunks(content).Take(4))
        {
            var vector = embed(part);
            sum ??= new double[vector.Length];
            for (var i = 0; i < vector.Length; i++) sum[i] += vector[i];
        }
        if (sum is null) return null;
        var norm = Math.Sqrt(sum.Sum(x => x * x));
        return norm is 0 or double.NaN ? null : sum.Select(x => (float)Math.Round(x / norm, 4)).ToArray();
    }

    [GeneratedRegex(@"\.(pdf|docx?|xlsx?|pptx?|txt|md|csv|odt|ods)$")] private static partial Regex Extension();
    [GeneratedRegex(@"\s*\(\d+\)$")] private static partial Regex Numbered();
}
