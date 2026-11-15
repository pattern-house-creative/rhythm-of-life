using System.Text.Json;

namespace Knowledge.Tests.Parity;

// The synthetic parity corpus (corpus.json) and its golden queries (golden-queries.json): a fictional household,
// alex and sam, each with a personal scope, and a shared Family scope. The records are engine-agnostic, in Second
// Brain's vocabulary (kinds, status, importance, validity, typed links); ParityLoader maps what Rhythm can represent
// today. All of it is DEMO data: invented people, places, numbers and example.invalid hosts.
public sealed record ParityPerson(string Id, string Name);
// A person taken out of `people` (docs/dev/memory.md section 1): their subject and records stay, and no one reads them.
public sealed record ParityFormerPerson(string Id, string Name, DateOnly Left);
// Who a golden query is asked as: a person, or a reader with no person (the app principal behind a shared screen),
// who sees the Family scope only (SB-P1-05, D-046).
public sealed record ParityViewer(string Id, string? Person, string? Note = null);
// Kind says who reads it: `personal` its owner's readers, `shared` the Family readers. Stored is the scope column the
// rows carry when it is not the kind's own ("private" for the Second Brain personal workspace whose owner is the
// household, which reads as Family, memory.md section 2). Team lists a Second Brain team workspace's own members, which
// may be fewer than the Family readers who read it in Rhythm (memory.md 10.2).
public sealed record ParityScope(string Id, string Kind, string? Owner, string? Name, string[] Readers, string[] Writers,
    string? Stored = null, string[]? Team = null, string? Note = null)
{
    public string Column => Stored ?? (Kind == "personal" ? "private" : "shared");
}
// Author null: a project created by a principal with no person, which is the household's (memory.md section 5).
public sealed record ParityProject(string Id, string Scope, string Name, string Description, string[] Aliases, string? Author = null);
// Provenance as Second Brain records it: explicit (a person linked them), inferred (similarity), system (supersede).
public sealed record ParityLink(string Type, string Target, string Provenance);
public sealed record ParitySource(string Channel, string? Client);
// What a correct engine should do with a record that is not ordinary knowledge (the controlled prompt injection).
public sealed record ParityExpect(bool Held, string Reason, string? Note);
public sealed record ParityStale(DateOnly LastConfirmedAt, DateOnly ReviewBy);
// Author null: written by a principal with no person (an unbound token, the app with no viewer session), so it is the
// household's and has no person author (memory.md section 5); every record today is written that way.
public sealed record ParityMemory(string Id, string Scope, string? Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string Kind, string Status, int Importance, string Volatility, string[] Tags, string? Project, ParitySource Source,
    DateOnly? ValidFrom, DateOnly? ValidUntil, string? ReplacedBy, string Text, ParityLink[] Links,
    string? Language = null, string? SharedFrom = null, bool Standing = false, ParityExpect? Expect = null,
    ParityStale? Stale = null, JsonElement? Decision = null, JsonElement? When = null, JsonElement? Commitment = null);
public sealed record ParityNote(string Id, string Scope, string Author, string Path, string Title, DateTimeOffset ModifiedAt,
    string[] Tags, string Text, ParityLink[] Links, string? Language = null);
public sealed record ParityDocumentSource(string Kind, string FileId, string Url, string Revision, DateTimeOffset ModifiedAt, string MimeType);
public sealed record ParityRevision(string Revision, DateTimeOffset ModifiedAt, string Text);
public sealed record ParityDocument(string Id, string Scope, string Owner, string Title, ParityDocumentSource Source,
    string? Project, ParityRevision[] Revisions, string Text, ParityLink[] Links, string? Language = null);
public sealed record ParityPoint(DateOnly Date, double Value);
public sealed record ParitySeries(string Id, string Subject, string Scope, string Metric, string Unit, string Device, string Title, ParityPoint[] Points);
public sealed record ParityCard(string Kind, string Ref);
public sealed record ParityDashboard(string Id, string Scope, string Owner, string Title, ParityCard[] Cards);

// Derived records for phase 1's scope tests (SB-P1-06, SB-P1-10): not loaded into the evaluation, since no retrieval
// path reads them. Each lands in the narrowest scope of what it cites (memory.md section 5). Author null is a principal
// with no person: the daily insight run with an unbound token.
public sealed record ParityInsight(string Id, string Scope, string? Author, string Kind, string Title, string Summary,
    string[] Cites, DateTimeOffset GeneratedAt);
// One run of the daily insight, with its free-text note (insight_runs.note, 012): the note can name what it read.
public sealed record ParityInsightRun(string Id, string Scope, string? Author, string Outcome, string? Insight, string Note,
    string[] Cites, DateTimeOffset At);
// A star pinned on the sky (sky_pins, 016), by a person, and a corner of Home showing a dashboard's card (ambient_cards,
// 015).
public sealed record ParityPin(string Id, string Scope, string By, string Star, DateTimeOffset At);
public sealed record ParityCorner(string Id, string Corner, string Dashboard, string Scope, string By, DateTimeOffset At);
// A memory's versions before a share, and the share itself. Applied false: the corpus holds the memory as it was before
// the share, which a test performs (story 2), so its history before the share is known exactly.
public sealed record ParityVersion(int Version, DateTimeOffset At, string Text);
public sealed record ParityShare(string Id, string Memory, string By, string From, string To, DateTimeOffset At, bool Applied,
    ParityVersion[] History, string? Note = null);

// One searchable text record, whichever kind it is in the corpus.
public sealed record ParityText(string Id, string Kind, string Scope, string Title, string Text, DateTimeOffset Modified,
    string? Project, ParityLink[] Links, string? Url);

public sealed record ParityCorpus(string Schema, string Label, DateTimeOffset Now, string Timezone, ParityPerson[] People,
    ParityScope[] Scopes, ParityProject[] Projects, ParityMemory[] Memories, ParityNote[] Notes, ParityDocument[] Documents,
    ParitySeries[] Measurements, ParityDashboard[] Dashboards, ParityViewer[] Viewers, ParityFormerPerson[] FormerPeople,
    ParityInsight[] Insights, ParityInsightRun[] InsightRuns, ParityPin[] Pins, ParityCorner[] Corners, ParityShare[] Shares)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Parity");
    public static ParityCorpus Load() => JsonSerializer.Deserialize<ParityCorpus>(File.ReadAllText(Path.Combine(Folder, "corpus.json")), Json)!;

    public ParityScope Scope(string id) => Scopes.Single(s => s.Id == id);
    // Whether a viewer may read a record of this scope, as the corpus means it (a personal scope's owner; Family:
    // both people and the Family view). This is the rule the golden visibility labels follow, not a rule Rhythm
    // enforces today.
    public bool CanSee(string viewer, string scope) => Viewers.Single(v => v.Id == viewer).Person is { } person
        ? Scope(scope).Readers.Contains(person) : Scope(scope).Kind == "shared";

    public IEnumerable<ParityText> Texts =>
        Memories.Select(m => new ParityText(m.Id, "memory", m.Scope, Knowledge.Adapters.SecondBrain.SecondBrainAdapter.Title(m.Text),
                m.Text, m.UpdatedAt, m.Project, m.Links, null))
            .Concat(Notes.Select(n => new ParityText(n.Id, "note", n.Scope, n.Title, n.Text, n.ModifiedAt, null, n.Links, null)))
            .Concat(Documents.Select(d => new ParityText(d.Id, "document", d.Scope, d.Title, d.Text, d.Source.ModifiedAt, d.Project, d.Links, d.Source.Url)));

    // Every record a golden query can name, with its scope.
    public IReadOnlyDictionary<string, string> ScopeOf =>
        Texts.Select(t => (t.Id, t.Scope)).Concat(Measurements.Select(m => (m.Id, m.Scope))).Concat(Dashboards.Select(d => (d.Id, d.Scope)))
            .ToDictionary(x => x.Id, x => x.Scope, StringComparer.Ordinal);
}

public sealed record ParityGrade(string Id, int Grade);
// `needs` names the capabilities a query depends on (golden-queries.json "capabilities"). Where a retrieval path lacks
// one, a miss or a leak is recorded as an expected baseline gap rather than a regression.
public sealed record ParityQuery(string Id, string Type, string Viewer, string Query, ParityGrade[] Relevant,
    string[]? Forbidden = null, string[]? Needs = null, string? Seed = null, int? Depth = null, DateOnly? AsOf = null,
    string[]? WrongAtTime = null, string? Note = null, string? Cluster = null)
{
    public string[] Forbids => Forbidden ?? [];
    public string[] Requires => Needs ?? [];
    // The bootstrap cluster, as upstream's clusterKey: queries answered by the same record resample together. It is
    // the first grade-2 record, else the first relevant, else the first forbidden one, unless `cluster` names it; a
    // query whose answer is a replaced fact names its chain's current record, so one chain is one cluster.
    public string ClusterKey => Cluster ?? Relevant.FirstOrDefault(r => r.Grade == 2)?.Id ?? Relevant.FirstOrDefault()?.Id ?? Forbids.FirstOrDefault() ?? Id;
}

public sealed record ParityGolden(string Schema, string Label, int K, Dictionary<string, string> Types,
    Dictionary<string, string> Capabilities, ParityQuery[] Queries)
{
    public static ParityGolden Load() => JsonSerializer.Deserialize<ParityGolden>(File.ReadAllText(Path.Combine(ParityCorpus.Folder, "golden-queries.json")), ParityCorpus.Json)!;
}

// Rank metrics over one ranked list of corpus ids. Gains are 2^grade - 1, so a grade-2 answer counts three times a
// grade-1 one. With nothing relevant (a pure visibility or injection check) recall, reciprocal rank and nDCG are null.
public static class ParityMetrics
{
    public static double? Recall(IReadOnlyList<string> ranked, IReadOnlyList<ParityGrade> relevant, int k) =>
        relevant.Count == 0 ? null : (double)relevant.Count(r => ranked.Take(k).Contains(r.Id)) / relevant.Count;

    public static double? ReciprocalRank(IReadOnlyList<string> ranked, IReadOnlyList<ParityGrade> relevant)
    {
        if (relevant.Count == 0) return null;
        for (var i = 0; i < ranked.Count; i++) if (relevant.Any(r => r.Id == ranked[i])) return 1.0 / (i + 1);
        return 0;
    }

    public static double? Ndcg(IReadOnlyList<string> ranked, IReadOnlyList<ParityGrade> relevant, int k)
    {
        if (relevant.Count == 0) return null;
        static double Gain(int grade) => Math.Pow(2, grade) - 1;
        var dcg = ranked.Take(k).Select((id, i) => (relevant.FirstOrDefault(r => r.Id == id)?.Grade ?? 0, i))
            .Sum(x => Gain(x.Item1) / Math.Log2(x.i + 2));
        var ideal = relevant.Select(r => r.Grade).OrderDescending().Take(k).Select((g, i) => Gain(g) / Math.Log2(i + 2)).Sum();
        return ideal == 0 ? null : dcg / ideal;
    }

    // Forbidden records anywhere in what a path returned, not only its first k: a leak is a leak at any rank.
    public static IReadOnlyList<string> Leaks(IReadOnlyList<string> returned, IEnumerable<string> forbidden) =>
        forbidden.Where(returned.Contains).ToArray();

    // The nearest-rank percentile of a non-empty list.
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
