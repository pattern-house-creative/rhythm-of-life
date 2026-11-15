using System.Text.RegularExpressions;
using Knowledge.Relationships;

namespace Knowledge.Tests.Parity;

// SB-P4-12: the two relevance ideas that change how the map links, as they were measured, kept here so RelevanceIdeasTests
// recomputes their numbers. Neither was kept (docs/dev/second-brain-parity/relevance.md), so neither is in the engine; the
// measurement build that ran them on the map behind graph.relevance.names and graph.relevance.hubs is commit 41b6a8c,
// whose engine code these are. It was measured with scopes off only, and its scoped name path is not scope-safe: it
// decides which words are names across every scope in the pass, so with memory.scopes on a private record's capitalised
// word could change whether two Family stars are joined. A retry decides name words inside each scope-pair group (or
// from the records the pair's readers read), with a test where a private record's word would change a Family join.

// Shared names (idea 2 in docs/dev/connection-ideas.md): two stars whose texts name the same specific person,
// organisation, place or event. No model. The rules, written after a first probe of the labelled pairs and before the
// numbers relevance.md records (see its "How far to trust the numbers"):
// - A word is letters (with apostrophes and hyphens inside); a possessive's "'s" and a closing apostrophe are dropped.
//   Digits make a word a code, never a name (HP-2291 is the keyword search's).
// - A capitalised word starts with a capital, has at least two letters and is not all capitals (GBP, MOT and shouting
//   are no names). Weekdays and months, and their plurals, are calendar words, never names.
// - Candidates are the runs of capitalised words with nothing but a space between them. A run that opens a segment (the
//   text's start, a line's, or a sentence's after . ! ? : or ;, a heading's or a list item's) is marked, since its first
//   word may be capitalised only for its place.
// - Across the pass, a name word is a capitalised word seen at least once where its place does not explain it (inside a
//   run, or opening an unmarked one). Each run keeps its name words, split where another word stood; a part that opens a
//   segment and is one word long is dropped ("Home insurance is…"), and so is one word that is a person of the
//   household's own (rhythm.yaml's people, by id or name: everyone's records name them).
// - A name joins stars when at least MinStars and at most MaxStars of the compared stars carry it: a name in more is a
//   common word, not evidence. With scopes off (every install's mode, and the measurement's) every star is compared.
public static partial class SharedNames
{
    public const int PerRecord = 30, MaxLength = 100, MinStars = 2, MaxStars = 5;
    // The mark of a candidate that opens a segment.
    public const char Opens = '^';

    static readonly HashSet<string> Calendar = new([
        "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], StringComparer.OrdinalIgnoreCase);

    // A text's candidate runs, each its capitalised words joined by one space, marked when it opens a segment; distinct,
    // in the order met (the title first, then the text), at most PerRecord, each at most MaxLength characters.
    public static IReadOnlyList<string> Candidates(string title, string text)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in new[] { title, text })
        {
            if (string.IsNullOrEmpty(part)) continue;
            var run = new List<string>();
            var opens = false;
            var initial = true;
            void Flush()
            {
                if (run.Count > 0 && found.Count < PerRecord)
                {
                    var name = (opens ? Opens.ToString() : "") + string.Join(' ', run);
                    if (name.Length <= MaxLength && seen.Add(name)) found.Add(name);
                }
                run.Clear();
            }
            foreach (Match m in Token().Matches(part))
            {
                if (m.Groups["line"].Success) { Flush(); initial = true; continue; }
                if (m.Groups["mark"].Success)
                {
                    Flush();
                    if (m.Value.IndexOfAny(['.', '!', '?', ':', ';', '#', '-', '*', '•', '–']) >= 0) initial = true;
                    continue;
                }
                var word = Word(m.Value);
                var wasInitial = initial;
                initial = false;
                if (word is null) { Flush(); continue; }
                if (run.Count == 0) opens = wasInitial;
                run.Add(word);
            }
            Flush();
        }
        return found;
    }

    // A capitalised word as a candidate takes it, or null for any other word.
    static string? Word(string token)
    {
        var word = token.TrimEnd('\'', '’', '-');
        if (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("’s", StringComparison.Ordinal)) word = word[..^2];
        if (word.Length < 2 || !char.IsUpper(word[0]) || word.Any(char.IsDigit) || !word.Any(char.IsLower)) return null;
        if (Calendar.Contains(word) || word.EndsWith('s') && Calendar.Contains(word[..^1])) return null;
        return word;
    }

    // The words a pass's candidates show are names: those seen where their place does not explain a capital.
    public static HashSet<string> NameWords(IEnumerable<IReadOnlyList<string>> candidates)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in candidates)
        foreach (var candidate in list)
        {
            var parts = candidate.TrimStart(Opens).Split(' ');
            for (var i = candidate[0] == Opens ? 1 : 0; i < parts.Length; i++) words.Add(parts[i]);
        }
        return words;
    }

    // One star's names, lower case, from its candidates and the pass's name words, leaving out the household's people.
    public static HashSet<string> Names(IReadOnlyList<string> candidates, IReadOnlySet<string> nameWords, IReadOnlySet<string> people)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var opens = candidate[0] == Opens;
            var parts = candidate.TrimStart(Opens).Split(' ');
            var start = 0;
            for (var i = 0; i <= parts.Length; i++)
            {
                if (i < parts.Length && nameWords.Contains(parts[i])) continue;
                var length = i - start;
                if (length > 0 && !(length == 1 && (opens && start == 0 || people.Contains(parts[start]))))
                    names.Add(string.Join(' ', parts[start..i]).ToLowerInvariant());
                start = i + 1;
            }
        }
        return names;
    }

    // The household's people as the rules compare them: each id and name, any case.
    public static IReadOnlySet<string> People(IEnumerable<string> idsAndNames) =>
        new HashSet<string>(idsAndNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);

    // Every two stars that share a name carried by MinStars to MaxStars of them, with the names they share; each star's
    // candidates by its key. Stars of two people are never joined (RelationshipDiscovery's bridging rule).
    public static Dictionary<(ResourceKey, ResourceKey), string[]> Links(IReadOnlyList<DiscoveryRecord> stars,
        IReadOnlyDictionary<ResourceKey, IReadOnlyList<string>> candidates, IReadOnlySet<string> people)
    {
        var named = stars.Where(r => r.Status == "current" && candidates.TryGetValue(r.Key, out var c) && c.Count > 0).ToArray();
        var words = NameWords(named.Select(r => candidates[r.Key]));
        var names = named.ToDictionary(r => r.Key, r => Names(candidates[r.Key], words, people));
        var counts = named.SelectMany(r => names[r.Key]).GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var found = new Dictionary<(ResourceKey, ResourceKey), string[]>();
        for (var i = 0; i < named.Length; i++)
        for (var j = i + 1; j < named.Length; j++)
        {
            var (a, b) = (named[i].Key, named[j].Key);
            if (!(a.Subject == b.Subject || a.Subject == RelationshipDiscovery.Household || b.Subject == RelationshipDiscovery.Household)) continue;
            var shared = names[a].Intersect(names[b]).Where(n => counts[n] is >= MinStars and <= MaxStars).Order(StringComparer.Ordinal).ToArray();
            if (shared.Length > 0) found[(a, b)] = shared;
        }
        return found;
    }

    [GeneratedRegex(@"(?<word>\p{L}[\p{L}\p{M}\p{Nd}'’\-]*)|(?<line>\n)|(?<mark>[^\p{L}\s]+)")] private static partial Regex Token();
}

// The map's similarity rule (SimilaritySuggestions, scopes off) with the hub correction (idea 3, CSLS, Conneau and others
// 2018): a pair's closeness less half the sum of each end's hub score, the mean of its `hubNeighbors` closest scores. The
// corrected score chooses which pairs pass, ranks each star's closest and decides the mutual rule; each threshold admits
// as many pairs as it admits by the plain score, those with the highest corrected scores, so the correction changes which
// pairs pass, never how many. Copies (one title, a plain score of 0.95 or more) as the map makes them, and two records
// one references never compared (the map leaves them to the stated link). Without the correction (`hubNeighbors` null)
// it is the map's own rule, which RelevanceRulesTests checks against SimilaritySuggestions on stars with and without
// references, and RelevanceIdeasTests on the corpus's stars. Those were built without references (RelevanceIdeas.Stars),
// so the "alone" rows relevance.md records for the similar links and the hub correction are the similarity signal
// without the map's exclusion of stated pairs; with the stated links and the exclusion every combined row is the same
// (relevance.md, "How far to trust the numbers").
public static class HubCorrection
{
    public static HashSet<(ResourceKey, ResourceKey)> Links(IReadOnlyList<DiscoveryRecord> stars, double threshold, int neighbors,
        double? documentThreshold, double? mutualThreshold, int? hubNeighbors)
    {
        var v = stars.Where(r => r.Status == "current" && r.Embedding is { Length: > 0 }).ToArray();
        var n = v.Length;
        // Two records one references are never compared, as SimilaritySuggestions.Comparable leaves them to the stated link.
        var linked = new HashSet<(ResourceKey, ResourceKey)>();
        foreach (var record in stars)
        foreach (var to in record.References.Where(r => r.Target is not null).Select(r => r.Target!)) { linked.Add((record.Key, to)); linked.Add((to, record.Key)); }
        var dot = new double[n, n];
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) dot[i, j] = i == j ? 0 : Dot(v[i].Embedding!, v[j].Embedding!);
        var copies = new HashSet<(int, int)>();
        foreach (var group in Enumerable.Range(0, n).Where(i => !CatalogStars.IsData(v[i].Key))
            .GroupBy(i => SimilaritySuggestions.NormalizedTitle(v[i].Title)).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var members = group.ToArray();
            for (var a = 0; a < members.Length; a++)
            for (var b = a + 1; b < members.Length; b++)
                if (!linked.Contains((v[members[a]].Key, v[members[b]].Key)) && dot[members[a], members[b]] >= SimilaritySuggestions.CopyThreshold)
                    copies.Add((Math.Min(members[a], members[b]), Math.Max(members[a], members[b])));
        }
        bool Eligible(int i, int j) => i != j && !copies.Contains((Math.Min(i, j), Math.Max(i, j))) && !linked.Contains((v[i].Key, v[j].Key));
        bool Documents(int i, int j) => v[i].Kind == "document" && v[j].Kind == "document";
        var hubs = hubNeighbors is { } k
            ? Enumerable.Range(0, n).Select(i => Enumerable.Range(0, n).Where(j => j != i).Select(j => dot[i, j]).OrderDescending().Take(k).DefaultIfEmpty(0).Average()).ToArray()
            : null;
        double Score(int i, int j) => hubs is null ? dot[i, j] : dot[i, j] - (hubs[i] + hubs[j]) / 2;
        // The pairs each threshold admits: today's by the plain score; corrected, as many by the corrected score (ties by
        // the plain score, then the lower indexes).
        Func<int, int, bool> similar, mutual;
        if (hubs is null)
        {
            similar = (i, j) => dot[i, j] >= (Documents(i, j) ? documentThreshold ?? threshold : threshold);
            mutual = (i, j) => mutualThreshold is { } m && dot[i, j] >= m;
        }
        else
        {
            var pairs = Enumerable.Range(0, n).SelectMany(i => Enumerable.Range(i + 1, n - i - 1).Where(j => Eligible(i, j)).Select(j => (i, j))).ToArray();
            HashSet<(int, int)> Best(IEnumerable<(int i, int j)> from, Func<(int i, int j), bool> plainPasses)
            {
                var list = from.ToArray();
                return [.. list.OrderByDescending(p => Score(p.i, p.j)).ThenByDescending(p => dot[p.i, p.j]).ThenBy(p => p.i).ThenBy(p => p.j)
                    .Take(list.Count(plainPasses))];
            }
            var admitted = Best(pairs.Where(p => !Documents(p.i, p.j)), p => dot[p.i, p.j] >= threshold);
            admitted.UnionWith(Best(pairs.Where(p => Documents(p.i, p.j)), p => dot[p.i, p.j] >= (documentThreshold ?? threshold)));
            var admittedMutual = Best(pairs, p => mutualThreshold is { } m && dot[p.i, p.j] >= m);
            similar = (i, j) => admitted.Contains((Math.Min(i, j), Math.Max(i, j)));
            mutual = (i, j) => admittedMutual.Contains((Math.Min(i, j), Math.Max(i, j)));
        }
        var links = new HashSet<(int, int)>(copies);
        var closest = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            var others = Enumerable.Range(0, n).Where(j => Eligible(i, j)).OrderByDescending(j => Score(i, j)).ThenBy(j => j).ToArray();
            foreach (var j in others.Where(j => similar(i, j)).Take(neighbors)) links.Add((Math.Min(i, j), Math.Max(i, j)));
            closest[i] = [.. others.Take(neighbors)];
        }
        if (mutualThreshold is not null)
            for (var i = 0; i < n; i++)
            foreach (var j in closest[i])
                if (i < j && mutual(i, j) && closest[j].Contains(i)) links.Add((i, j));
        return [.. links.Select(p => (v[p.Item1].Key, v[p.Item2].Key))];
    }

    static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
