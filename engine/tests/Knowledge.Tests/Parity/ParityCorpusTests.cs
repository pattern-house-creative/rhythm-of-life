using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests.Parity;

// The parity corpus and golden queries are test inputs, so they are checked like code: every id resolves, the labels
// agree with the scopes, and nothing in them is real. These run in every ordinary test run; the evaluation itself is
// opt-in (ParityEvaluation).
public sealed partial class ParityCorpusTests
{
    static readonly ParityCorpus Corpus = ParityCorpus.Load();
    static readonly ParityGolden Golden = ParityGolden.Load();

    [Fact] public void Every_id_is_unique_and_every_reference_resolves()
    {
        var ids = Corpus.Texts.Select(t => t.Id).Concat(Corpus.Measurements.Select(m => m.Id)).Concat(Corpus.Dashboards.Select(d => d.Id))
            .Concat(Corpus.Projects.Select(p => p.Id)).Concat(Corpus.Insights.Select(i => i.Id)).Concat(Corpus.InsightRuns.Select(r => r.Id))
            .Concat(Corpus.Pins.Select(p => p.Id)).Concat(Corpus.Corners.Select(c => c.Id)).Concat(Corpus.Shares.Select(s => s.Id)).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var scopes = Corpus.Scopes.Select(s => s.Id).ToHashSet();
        var people = Corpus.People.Select(p => p.Id).ToHashSet();
        var former = Corpus.FormerPeople.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var projects = Corpus.Projects.Select(p => p.Id).ToHashSet();
        var records = Corpus.ScopeOf;
        // Readers and writers are people; an owner is a person or a former person, whose scope nobody reads or writes.
        Assert.All(Corpus.Scopes, s => Assert.All(s.Readers.Concat(s.Writers).Concat(s.Team ?? []), p => Assert.Contains(p, people)));
        Assert.All(Corpus.Scopes.Where(s => s.Owner is not null), s => Assert.True(people.Contains(s.Owner!) || former.ContainsKey(s.Owner!), s.Id));
        Assert.All(Corpus.FormerPeople, f => Assert.DoesNotContain(f.Id, people));
        Assert.All(Corpus.Projects, p => Assert.Contains(p.Scope, scopes));
        Assert.All(records.Values, s => Assert.Contains(s, scopes));
        Assert.All(Corpus.Texts, t =>
        {
            if (t.Project is { } project) Assert.Contains(project, projects);
            Assert.All(t.Links, l =>
            {
                Assert.Contains(l.Type, (string[])["relates_to", "supersedes", "caused_by", "decided", "about_person", "part_of_project", "follows", "drawn_from"]);
                Assert.Contains(l.Provenance, (string[])["explicit", "inferred", "system"]);
                Assert.True(l.Type == "part_of_project" ? projects.Contains(l.Target) : records.ContainsKey(l.Target), $"{t.Id} links to {l.Target}");
            });
        });
        Assert.All(Corpus.Memories, m =>
        {
            var scope = Corpus.Scope(m.Scope);
            if (m.Author is null)
                // No person author: written by a principal with no person, so it is the household's (Family only).
                Assert.True(scope.Kind == "shared", $"{m.Id}: a memory with no person author in {m.Scope}");
            else if (former.TryGetValue(m.Author, out var left))
                // A former person wrote it in their own scope while they were in people.
                Assert.True(scope.Owner == m.Author && DateOnly.FromDateTime(m.CreatedAt.UtcDateTime) <= left.Left, $"{m.Id}: written by {m.Author}");
            else Assert.True(people.Contains(m.Author) && scope.Writers.Contains(m.Author), $"{m.Author} may not write {m.Scope}");
            Assert.Contains(m.Kind, (string[])["episodic", "semantic"]);
            Assert.Contains(m.Status, (string[])["canonical", "draft", "deprecated"]);
            Assert.InRange(m.Importance, 1, 5);
            Assert.True(m.UpdatedAt >= m.CreatedAt && m.UpdatedAt <= Corpus.Now, m.Id);
            // A stated start is never in the future, as Second Brain refuses one.
            Assert.False(m.ValidFrom > DateOnly.FromDateTime(Corpus.Now.UtcDateTime), m.Id);
        });
        Assert.All(Corpus.Measurements, m => Assert.Equal(Corpus.Scope(m.Scope).Owner, m.Subject));
        Assert.All(Corpus.Dashboards, d => Assert.All(d.Cards, c => Assert.True(records.ContainsKey(c.Ref), $"{d.Id} shows {c.Ref}")));
        // A dashboard shows only what every reader of its own scope may read: a Family dashboard holds no private card.
        Assert.All(Corpus.Dashboards, d => Assert.All(d.Cards, c => Assert.All(Corpus.Viewers.Where(v => Corpus.CanSee(v.Id, d.Scope)),
            v => Assert.True(Corpus.CanSee(v.Id, records[c.Ref]), $"{d.Id} shows {c.Ref} to {v.Id}"))));
        // Each person is a viewer; any other viewer is a reader with no person (the Family view).
        var viewers = Corpus.Viewers.Select(v => v.Id).ToHashSet();
        Assert.Equal(Corpus.Viewers.Length, viewers.Count);
        Assert.All(Corpus.People, p => Assert.Contains(Corpus.Viewers, v => v.Id == p.Id && v.Person == p.Id));
        Assert.All(Corpus.Viewers, v => Assert.True(v.Person is null || people.Contains(v.Person), v.Id));
        Assert.All(Golden.Queries, q =>
        {
            Assert.Contains(q.Viewer, viewers);
            Assert.True(Golden.Types.ContainsKey(q.Type), q.Id);
            Assert.All(q.Requires, n => Assert.True(Golden.Capabilities.ContainsKey(n), $"{q.Id} needs {n}"));
            Assert.All(q.Relevant.Select(r => r.Id).Concat(q.Forbids).Concat(q.WrongAtTime ?? []).Concat(q.Seed is { } s ? [s] : []),
                id => Assert.True(records.ContainsKey(id), $"{q.Id} names {id}"));
            Assert.All(q.Relevant, r => Assert.InRange(r.Grade, 1, 2));
            Assert.InRange(q.Query.Length, 1, 200);
        });
        Assert.Equal(Golden.Queries.Length, Golden.Queries.Select(q => q.Id).Distinct().Count());
    }

    [Fact] public void Golden_labels_agree_with_who_may_see_what()
    {
        foreach (var q in Golden.Queries)
        {
            // Nothing is relevant to a viewer who may not see it, and a visibility check forbids only what they may not.
            Assert.All(q.Relevant, r => Assert.True(Corpus.CanSee(q.Viewer, Corpus.ScopeOf[r.Id]), $"{q.Id}: {q.Viewer} cannot see {r.Id}"));
            if (q.Type == "visibility")
            {
                Assert.All(q.Forbids, f => Assert.False(Corpus.CanSee(q.Viewer, Corpus.ScopeOf[f]), $"{q.Id}: {q.Viewer} may see {f}"));
                Assert.True(q.Forbids.Length == 0 || q.Requires.Contains("viewer-scope"), q.Id);
            }
            if (q.Seed is { } seed) Assert.True(Corpus.CanSee(q.Viewer, Corpus.ScopeOf[seed]), q.Id);
        }
        // Sam's surprise is invisible to Alex, and the one memory Alex shared is visible to Sam. The Family view sees
        // the shared scope and no personal one.
        Assert.False(Corpus.CanSee("alex", Corpus.ScopeOf["mem-sam-party"]));
        Assert.True(Corpus.CanSee("sam", Corpus.ScopeOf["mem-family-leeds"]));
        Assert.True(Corpus.CanSee("family", "family"));
        Assert.False(Corpus.CanSee("family", "personal:alex"));
        Assert.False(Corpus.CanSee("family", "personal:sam"));
        // A former person's records are read by no one; the household's Second Brain workspace and a team workspace
        // read as Family, by everyone who reads Family (memory.md sections 2 and 10.2).
        Assert.All(Corpus.Viewers, v => Assert.False(Corpus.CanSee(v.Id, "personal:jo"), v.Id));
        Assert.All(Corpus.Viewers, v => Assert.True(Corpus.CanSee(v.Id, "household:private") && Corpus.CanSee(v.Id, "team:wickmere-bees"), v.Id));
    }

    // The cases the scope contract defines (docs/dev/memory.md sections 2, 5, 8 and 10) are in the corpus before the
    // phase 1 items that test them (SB-P1-03 to SB-P1-10), each with the rule it must follow.
    [Fact] public void The_corpus_holds_the_cases_the_scope_contract_defines()
    {
        var records = Corpus.ScopeOf;
        bool Reads(string scope, string record) => Corpus.Viewers.Where(v => Corpus.CanSee(v.Id, scope)).All(v => Corpus.CanSee(v.Id, records[record]));
        // The narrowest of some scopes: a personal one if any (two people's private records are never cited together).
        string Narrowest(IEnumerable<string> cited)
        {
            var personal = cited.Select(c => records[c]).Where(s => Corpus.Scope(s).Kind == "personal").Distinct().ToArray();
            Assert.True(personal.Length <= 1, "cites two people's private records: " + string.Join(", ", personal));
            return personal.SingleOrDefault() ?? "family";
        }

        // Scopes: how each lands, and the convention the loader maps them by.
        Assert.All(Corpus.Scopes, s =>
        {
            Assert.Equal(s.Column, ParityLoader.Visibility(s.Id));
            Assert.Equal(s.Kind == "personal" ? s.Owner : RhythmConfig.Household, ParityLoader.Subject(s.Id));
        });
        var householdPrivate = Assert.Single(Corpus.Scopes, s => s.Stored == "private");
        Assert.Equal("shared", householdPrivate.Kind);
        Assert.Null(householdPrivate.Owner);
        var team = Assert.Single(Corpus.Scopes, s => s.Team is not null);
        Assert.True(team.Kind == "shared" && team.Readers.ToHashSet().SetEquals(Corpus.Scope("family").Readers) && team.Team!.Length < team.Readers.Length,
            "a team workspace is read by every Family reader, more than its own members");
        Assert.Contains(Corpus.Memories, m => m.Scope == householdPrivate.Id);
        Assert.Contains(Corpus.Memories, m => m.Scope == team.Id);

        // A former person: out of people, with records in their own scope that no golden answer names and a must-hide
        // check forbids.
        var jo = Assert.Single(Corpus.FormerPeople);
        var theirs = Corpus.Memories.Where(m => m.Author == jo.Id).Select(m => m.Id).ToHashSet();
        Assert.NotEmpty(theirs);
        Assert.DoesNotContain(Golden.Queries, q => q.Relevant.Any(r => theirs.Contains(r.Id)));
        Assert.All(Corpus.Viewers, v => Assert.Contains(Golden.Queries, q => q.Viewer == v.Id && q.Forbids.Any(theirs.Contains) && q.Requires.Contains("viewer-scope")));

        // Records with no person author: Family memories and a Family project, and every personal project is its owner's.
        Assert.True(Corpus.Memories.Count(m => m.Author is null) >= 3, "Family memories with no person author");
        Assert.Contains(Corpus.Projects, p => p.Author is null && Corpus.Scope(p.Scope).Kind == "shared");
        Assert.All(Corpus.Projects, p => Assert.True(Corpus.Scope(p.Scope).Kind == "shared" ? p.Author is null || Corpus.Scope(p.Scope).Writers.Contains(p.Author)
            : p.Author == Corpus.Scope(p.Scope).Owner, p.Id));
        Assert.Contains(Corpus.Memories, m => m.Author is null && m.Project is { } project && Corpus.Projects.Single(p => p.Id == project).Author is null);

        // Insights and their runs land in the narrowest scope of what they cite (SB-P1-06), so every reader of one may
        // read all it cites; one cites a private memory, one has no person author although it cites a private record
        // (the daily insight with an unbound token, before scopes).
        Assert.All(Corpus.Insights, i =>
        {
            Assert.NotEmpty(i.Cites);
            Assert.All(i.Cites, c => Assert.True(records.ContainsKey(c), $"{i.Id} cites {c}"));
            Assert.Equal(Narrowest(i.Cites), i.Scope);
            Assert.All(i.Cites, c => Assert.True(Reads(i.Scope, c), $"{i.Id}: a reader of {i.Scope} cannot read {c}"));
            Assert.Contains(i.Kind, (string[])["focus", "discovery", "suggestion"]);
            Assert.True(i.Author is null || Corpus.People.Any(p => p.Id == i.Author), i.Id);
        });
        Assert.Contains(Corpus.Insights, i => i.Cites.Any(c => Corpus.Memories.Any(m => m.Id == c && Corpus.Scope(m.Scope).Kind == "personal")));
        Assert.Contains(Corpus.Insights, i => i.Author is null && Corpus.Scope(i.Scope).Kind == "personal");
        Assert.All(Corpus.InsightRuns, r =>
        {
            Assert.Contains(r.Outcome, (string[])["saved", "quiet", "blocked"]);
            Assert.InRange(r.Note.Length, 1, 500);
            Assert.True((r.Outcome == "saved") == (r.Insight is not null), r.Id);
            var cites = r.Cites.Concat(r.Insight is { } id ? Corpus.Insights.Single(i => i.Id == id).Cites : []).ToArray();
            Assert.Equal(cites.Length == 0 ? "family" : Narrowest(cites), r.Scope);
        });
        Assert.Contains(Corpus.InsightRuns, r => r.Cites.Any(c => Corpus.Scope(records[c]).Kind == "personal"));

        // A pin is no wider than its star; a corner card no wider than its dashboard; one of each is private.
        Assert.All(Corpus.Pins, p =>
        {
            Assert.Equal(records[p.Star], p.Scope);
            Assert.True(Corpus.CanSee(p.By, records[p.Star]), $"{p.By} pinned what they cannot read");
        });
        Assert.Contains(Corpus.Pins, p => Corpus.Scope(p.Scope).Kind == "personal");
        Assert.Equal(Corpus.Corners.Length, Corpus.Corners.Select(c => c.Corner).Distinct().Count());
        Assert.All(Corpus.Corners, c =>
        {
            Assert.Contains(c.Corner, (string[])["glucose", "recovery", "training", "changed"]);
            Assert.Equal(Corpus.Dashboards.Single(d => d.Id == c.Dashboard).Scope, c.Scope);
            Assert.True(Corpus.CanSee(c.By, c.Scope), c.Id);
        });
        Assert.Contains(Corpus.Corners, c => Corpus.Scope(c.Scope).Kind == "personal");

        // The share story 2 performs: mem-alex-cafe, still private, with the versions it had before the share.
        Assert.All(Corpus.Shares, s =>
        {
            var m = Corpus.Memories.Single(x => x.Id == s.Memory);
            Assert.False(s.Applied, s.Id);
            Assert.Equal((m.Author, m.Scope), (s.By, s.From));
            Assert.True(Corpus.Scope(s.From).Kind == "personal" && Corpus.Scope(s.To).Kind == "shared", s.Id);
            Assert.True(s.History.Length >= 2, $"{s.Id}: a share needs history before it");
            Assert.Equal(Enumerable.Range(1, s.History.Length), s.History.Select(v => v.Version));
            Assert.True(s.History.Zip(s.History.Skip(1)).All(p => p.First.At < p.Second.At), s.Id);
            Assert.Equal((m.CreatedAt, m.UpdatedAt, m.Text), (s.History[0].At, s.History[^1].At, s.History[^1].Text));
            Assert.True(s.History[^1].At <= s.At && s.At <= Corpus.Now, s.Id);
        });
        Assert.Contains(Corpus.Shares, s => s.Memory == "mem-alex-cafe");
    }

    // Upstream's gate gives a verdict only on at least 200 queries in 30 or more clusters, with at least 10 queries per
    // category (D-028), and the gate counts judged queries (D-056): a relevant answer and no capability today's paths
    // lack, since an expected gap is no evidence. So the floors hold for the queries judged on today's paths, which
    // support none of the capabilities; every type also has 10 queries in 10 clusters in all, so a targeted gain on any
    // one type can be proven once its capability exists.
    [Fact] public void The_golden_set_is_large_enough_for_a_gate_verdict()
    {
        var judged = Golden.Queries.Where(q => q.Relevant.Length > 0 && q.Requires.Length == 0).ToArray();
        Assert.True(judged.Length >= 200, $"{judged.Length} judged queries of {Golden.Queries.Length}");
        Assert.True(judged.Select(q => q.ClusterKey).Distinct().Count() >= 30, "judged clusters");
        foreach (var group in judged.GroupBy(q => q.Type))
            Assert.True(group.Count() >= 10, $"{group.Key}: {group.Count()} judged queries");
        Assert.True(judged.Count(q => q.Type == "visibility") >= 10, "judged visibility (must-show) queries");
        foreach (var type in Golden.Types.Keys)
        {
            var of = Golden.Queries.Where(q => q.Type == type).ToArray();
            Assert.True(of.Length >= 10, $"{type}: {of.Length} queries");
            var clusters = of.Select(q => q.ClusterKey).Distinct().Count();
            Assert.True(clusters >= 10, $"{type}: {clusters} clusters");
        }
        // Every content type a retrieval path reads is the answer often enough to be measured on its own. Dashboards
        // are not among them: no path reads them yet (baseline.md section 3).
        var kinds = Corpus.Texts.Select(t => (t.Id, t.Kind)).Concat(Corpus.Measurements.Select(m => (m.Id, Kind: "measurements")))
            .ToDictionary(x => x.Id, x => x.Kind, StringComparer.Ordinal);
        foreach (var kind in new[] { "memory", "note", "document", "measurements" })
        {
            var answered = Golden.Queries.Count(q => q.Relevant.Any(r => r.Grade == 2 && kinds.GetValueOrDefault(r.Id) == kind));
            Assert.True(answered >= 10, $"{kind}: {answered} queries");
        }
        // Must-hide and must-show checks for every viewer: Alex, Sam and the Family view.
        foreach (var viewer in Corpus.Viewers)
        {
            var visibility = Golden.Queries.Where(q => q.Type == "visibility" && q.Viewer == viewer.Id).ToArray();
            Assert.True(visibility.Count(q => q.Forbids.Length > 0) >= 5, $"{viewer.Id}: must-hide checks");
            Assert.Contains(visibility, q => q.Relevant.Length > 0);
        }
    }

    // Each type's labels follow the type's own rule (docs/dev/second-brain-parity/fixtures.md), so a label cannot drift
    // away from the records it names when the corpus changes.
    [Fact] public void Golden_labels_follow_the_rule_of_their_type()
    {
        var texts = Corpus.Texts.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var memories = Corpus.Memories.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var series = Corpus.Measurements.Select(m => m.Id).ToHashSet();
        var listed = ParityMapSource.Listed(Corpus).Select(x => x.Id).ToHashSet();
        var today = DateOnly.FromDateTime(Corpus.Now.UtcDateTime);
        foreach (var q in Golden.Queries)
        {
            var answers = q.Relevant.Where(r => r.Grade == 2).Select(r => r.Id).ToArray();
            // One answer, or one supersession chain, is one cluster (ParityQuery.ClusterKey).
            var anchor = q.Relevant.FirstOrDefault(r => r.Grade == 2)?.Id ?? q.Relevant.FirstOrDefault()?.Id ?? q.Forbids.FirstOrDefault() ?? q.Id;
            Assert.True(q.ClusterKey == Head(anchor), $"{q.Id}: cluster {q.ClusterKey}, by the rule {Head(anchor)}");
            Assert.True((q.Type == "as-of") == (q.AsOf is not null), $"{q.Id}: an as-of date belongs to as-of queries");
            Assert.True((q.Type == "graph-chain") == (q.Seed is not null), $"{q.Id}: a seed belongs to graph-chain queries");
            // Forbidden records are what visibility, injection and current-fact checks are about, and nothing else.
            Assert.True(q.Forbids.Length == 0 || q.Type is "visibility" or "injection" or "current", $"{q.Id}: forbidden records in a {q.Type} query");
            switch (q.Type)
            {
                case "exact-id":
                    // Every word with a digit in it appears as written in each answer.
                    var ids = q.Query.Split(' ').Where(w => w.Any(char.IsDigit)).ToArray();
                    Assert.NotEmpty(ids);
                    Assert.All(answers, a => Assert.All(ids, id => Assert.Contains(id, texts[a].Title + " " + texts[a].Text, StringComparison.OrdinalIgnoreCase)));
                    break;
                case "rare-name":
                    // The records this viewer may see that hold every word of the name are exactly the relevant ones.
                    var words = Fold(q.Query).Split(' ');
                    var holding = Corpus.Texts.Where(t => Corpus.CanSee(q.Viewer, t.Scope) && words.All(Fold(t.Title + " " + t.Text).Contains))
                        .Select(t => t.Id).Order(StringComparer.Ordinal);
                    Assert.Equal(holding, q.Relevant.Select(r => r.Id).Order(StringComparer.Ordinal));
                    break;
                case "graph-chain":
                    // The seed opens on the map, and every relevant record is within the query's depth of it along
                    // typed links between records the map lists.
                    Assert.True(listed.Contains(q.Seed!), $"{q.Id}: the seed is not on the map");
                    var reach = Reach(q.Seed!, q.Depth ?? 1, listed);
                    Assert.All(q.Relevant, r => Assert.True(reach.Contains(r.Id), $"{q.Id}: {r.Id} is more than {q.Depth ?? 1} links from the seed"));
                    break;
                case "as-of":
                    Assert.Contains("as-of", q.Requires);
                    Assert.All(answers, a => Assert.True(TrueOn(a, q.AsOf!.Value), $"{q.Id}: {a} was not true on {q.AsOf}"));
                    Assert.All(q.WrongAtTime ?? [], w => Assert.False(TrueOn(w, q.AsOf!.Value), $"{q.Id}: {w} was true on {q.AsOf}"));
                    break;
                case "current":
                    // Only replaced facts are forbidden, and each one's chain ends in an answer.
                    Assert.Contains("validity", q.Requires);
                    Assert.NotEmpty(q.Forbids);
                    Assert.All(q.Forbids, f => { Assert.NotNull(memories[f].ReplacedBy); Assert.Contains(Head(f), answers); });
                    break;
                case "injection":
                    // At least one held record is forbidden; anything else forbidden is hidden from this viewer.
                    Assert.Contains("quarantine", q.Requires);
                    Assert.Contains(q.Forbids, Held);
                    Assert.All(q.Forbids, f => Assert.True(Held(f) || !Corpus.CanSee(q.Viewer, Corpus.ScopeOf[f]) && q.Requires.Contains("viewer-scope"),
                        $"{q.Id}: {f} is neither held nor hidden"));
                    break;
                case "multilingual":
                    Assert.All(answers, a => Assert.True(!q.Query.All(char.IsAscii) || !texts[a].Text.All(char.IsAscii) || Language(a) is not (null or "en"), q.Id));
                    break;
                case "long-text":
                    Assert.All(answers, a => Assert.True(texts[a].Text.Length > 2000, $"{q.Id}: {a} is not a long text"));
                    break;
                case "stale":
                    Assert.All(answers, a => Assert.True(memories[a].Stale is { } stale && stale.ReviewBy < today, $"{q.Id}: {a} is not overdue for review"));
                    break;
                case "commitment":
                    Assert.All(answers, a => Assert.True(memories[a].Commitment is not null, $"{q.Id}: {a} is not a commitment"));
                    break;
                case "health":
                    Assert.All(answers, a => Assert.True(series.Contains(a) || memories.TryGetValue(a, out var m) && m.Tags.Contains("health"), $"{q.Id}: {a}"));
                    break;
            }
        }
    }

    [Fact] public void Every_labelled_pair_resolves_and_each_label_is_well_represented()
    {
        var pairs = ParityPairs.Load();
        Assert.StartsWith("DEMO", pairs.Label);
        Assert.Equal(ParityPairs.Kinds.Order(), pairs.Labels.Keys.Order());
        Assert.NotEmpty(pairs.Rules);
        // Members are corpus text records or incoming saves, with no id shared between the two.
        var members = pairs.Members(Corpus);
        Assert.Equal(Corpus.Texts.Count() + pairs.Incoming.Length, members.Count);
        Assert.All(pairs.Incoming, i =>
        {
            Assert.True(Corpus.Scope(i.Scope).Writers.Contains(i.Author), $"{i.Author} may not write {i.Scope}");
            Assert.True(i.At <= Corpus.Now, i.Id);
            Assert.False(Corpus.ScopeOf.ContainsKey(i.Id), i.Id);
            Assert.Contains(pairs.Pairs, p => p.B == i.Id);
        });
        Assert.Equal(pairs.Pairs.Length, pairs.Pairs.Select(p => p.Id).Distinct().Count());
        Assert.Equal(pairs.Pairs.Length, pairs.Pairs.Select(p => string.Join('|', new[] { p.A, p.B }.Order(StringComparer.Ordinal))).Distinct().Count());
        foreach (var kind in ParityPairs.Kinds) Assert.True(pairs.Pairs.Count(p => p.Label == kind) >= 10, $"{kind}: fewer than 10 pairs");
        Assert.All(pairs.Pairs, p =>
        {
            Assert.Contains(p.Label, ParityPairs.Kinds);
            Assert.NotEqual(p.A, p.B);
            Assert.True(members.ContainsKey(p.A) && members.ContainsKey(p.B), $"{p.Id} names a record that is not there");
            Assert.True(members[p.A].Kind != "incoming", $"{p.Id}: an incoming save is always b");
            Assert.True(members[p.A].Written <= members[p.B].Written, $"{p.Id}: a is later than b");
            Assert.False(string.IsNullOrWhiteSpace(p.Why), p.Id);
            Assert.True((p.Label == "contradiction") == (p.Resolved is not null), $"{p.Id}: only a contradiction says whether it is resolved");
        });
    }

    [Fact] public void Pair_labels_follow_the_written_rules()
    {
        var pairs = ParityPairs.Load();
        var members = pairs.Members(Corpus);
        var memories = Corpus.Memories.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var links = Corpus.Texts.SelectMany(t => t.Links.Where(l => l.Type != "part_of_project").Select(l => (t.Id, l.Target))).ToHashSet();
        bool Linked(string a, string b) => links.Contains((a, b)) || links.Contains((b, a));
        bool Supersedes(string a, string b) => memories.GetValueOrDefault(a)?.ReplacedBy == b || memories.GetValueOrDefault(b)?.ReplacedBy == a;
        bool OnePersonReadsBoth(ParityPair p) => Corpus.People.Any(person => Corpus.CanSee(person.Id, members[p.A].Scope) && Corpus.CanSee(person.Id, members[p.B].Scope));
        foreach (var p in pairs.Pairs)
        {
            var (a, b) = (members[p.A], members[p.B]);
            // A copy in another scope is a share, not a duplicate.
            if (p.Label is "duplicate" or "near-duplicate") Assert.True(a.Scope == b.Scope, $"{p.Id}: across scopes");
            // Resolved means the corpus settles it with a supersedes link; unresolved means nothing does.
            if (p.Label == "contradiction") Assert.True(p.Resolved == Supersedes(p.A, p.B), $"{p.Id}: resolved is {p.Resolved}");
            if (p.Label == "unrelated") Assert.False(Linked(p.A, p.B) || Supersedes(p.A, p.B), $"{p.Id}: linked in the corpus");
            if (p.Label == "insight")
            {
                Assert.True(a.Kind != "incoming" && b.Kind != "incoming", $"{p.Id}: an insight pairs stored records");
                Assert.True(b.Written - a.Written >= ParityPairs.InsightGap, $"{p.Id}: written less than 30 days apart");
            }
            // Across Alex's and Sam's personal scopes nothing may be linked or merged: such a pair is labelled by meaning.
            if (!OnePersonReadsBoth(p)) Assert.True(p.Label is "related" or "unrelated", $"{p.Id}: {p.Label} across personal scopes");
        }
        // Every supersession in the corpus is a resolved contradiction, older record first.
        foreach (var old in Corpus.Memories.Where(m => m.ReplacedBy is not null))
            Assert.Contains(pairs.Pairs, p => p.Label == "contradiction" && p.A == old.Id && p.B == old.ReplacedBy);
        // Stored pairs as well as saves, cross-person pairs, and hard cases on both sides of a threshold.
        Assert.Contains(pairs.Pairs, p => p.Label == "duplicate" && members[p.B].Kind != "incoming");
        Assert.Contains(pairs.Pairs, p => p.Label == "near-duplicate" && members[p.B].Kind != "incoming");
        Assert.Contains(pairs.Pairs, p => p.Label == "contradiction" && p.Resolved == false && members[p.B].Kind != "incoming");
        Assert.Contains(pairs.Pairs, p => !OnePersonReadsBoth(p) && p.Label == "related");
        Assert.Contains(pairs.Pairs, p => !OnePersonReadsBoth(p) && p.Label == "unrelated");
        Assert.True(pairs.Pairs.Count(p => p.Hard && p.Label == "unrelated") >= 5, "hard negatives");
        Assert.True(pairs.Pairs.Count(p => p.Hard && p.Label != "unrelated") >= 5, "hard positives");
    }

    // SB-P5-12: standing-cases.json labels standing instructions and the questions asked of them, by the rules it writes
    // down: enough of each label for every instruction, nothing named that is not there, no question that is a golden query's
    // words (those are labelled by id), and the instructions outside corpus.json, so the gate's answers are untouched.
    [Fact] public void The_standing_cases_keep_their_rules()
    {
        var (cases, corpus, golden) = (ParityStanding.Load(), ParityCorpus.Load(), ParityGolden.Load());
        Assert.StartsWith("DEMO", cases.Label);
        Assert.Equal(["fire", "intent", "near"], cases.Labels.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(cases.Instructions.Length, cases.Instructions.Select(i => i.Id).Distinct().Count());
        Assert.True(cases.Instructions.Length >= 20, "fewer than 20 standing instructions");
        var questions = golden.Queries.Select(q => q.Query.ToLowerInvariant()).ToHashSet();
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in cases.Instructions)
        {
            Assert.True(i.Fire.Length >= 4 && i.Near.Length >= 3 && i.Intent.Length >= 2, $"{i.Id}: too few questions");
            Assert.Contains(corpus.Scopes, s => s.Id == i.Scope);
            // At most 500 characters, as a standing instruction may be; in the corpus only the corpus's own standing memory.
            if (i.Text is null) Assert.True(corpus.Memories.Single(m => m.Id == i.Id).Standing, $"{i.Id}: not the corpus's standing memory");
            else
            {
                Assert.DoesNotContain(corpus.Memories, m => m.Id == i.Id);
                Assert.InRange(i.Text.Length, 1, MemoryStanding.MaxChars);
            }
            foreach (var q in i.Fire.Concat(i.Near).Concat(i.Intent))
            {
                Assert.DoesNotContain(q.ToLowerInvariant(), questions);
                Assert.True(written.Add(q), $"{q}: written twice");
            }
        }
        // Every also names an instruction and a question that is another's, or a golden query, once, with a reason.
        var ids = golden.Queries.Select(q => q.Id).ToHashSet();
        var relevant = golden.Queries.SelectMany(q => q.Relevant.Select(r => (r.Id, q.Id))).ToHashSet();
        Assert.Equal(cases.Also.Length, cases.Also.Select(a => (a.Instruction, a.Golden ?? a.Query)).Distinct().Count());
        foreach (var a in cases.Also)
        {
            var mine = cases.Instructions.Single(i => i.Id == a.Instruction);
            Assert.Contains(a.Label, cases.Labels.Keys);
            Assert.False(string.IsNullOrWhiteSpace(a.Why));
            Assert.True(a.Golden is null != a.Query is null, $"{a.Instruction}: also names a golden query or a question, one of them");
            if (a.Golden is { } g)
            {
                Assert.Contains(g, ids);
                Assert.DoesNotContain((a.Instruction, g), relevant);
            }
            else
            {
                Assert.Contains(a.Query!, written);
                Assert.DoesNotContain(a.Query!, mine.Fire.Concat(mine.Near).Concat(mine.Intent));
            }
        }
    }

    // SB-P5-05: insight-pairs.json labels more pairs for insight candidates, each two eligible native memories of one scope,
    // the newer written 30 or more days after the older, none of them in pairs.json, and follows the written rules.
    [Fact] public void Every_insight_pair_is_two_eligible_memories_of_one_scope_30_days_apart_and_new()
    {
        var pairs = ParityPairs.Insight();
        var labelled = ParityPairs.Load();
        Assert.StartsWith("DEMO", pairs.Label);
        Assert.Equal(["insight", "related", "unrelated"], pairs.Labels.Keys.Order());
        Assert.NotEmpty(pairs.Rules);
        Assert.Empty(pairs.Incoming);
        var memories = Corpus.Memories.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var links = Corpus.Texts.SelectMany(t => t.Links.Select(l => (t.Id, l.Target))).ToHashSet();
        static string Key(string a, string b) => string.Join('|', new[] { a, b }.Order(StringComparer.Ordinal));
        var earlier = labelled.Pairs.Select(p => Key(p.A, p.B)).ToHashSet();
        Assert.Equal(pairs.Pairs.Length, pairs.Pairs.Select(p => p.Id).Distinct().Count());
        Assert.Equal(pairs.Pairs.Length, pairs.Pairs.Select(p => Key(p.A, p.B)).Distinct().Count());
        Assert.Empty(pairs.Pairs.Select(p => p.Id).Intersect(labelled.Pairs.Select(p => p.Id)));
        Assert.All(pairs.Pairs, p =>
        {
            Assert.Contains(p.Label, pairs.Labels.Keys);
            Assert.False(string.IsNullOrWhiteSpace(p.Why), p.Id);
            Assert.Null(p.Resolved);
            Assert.True(memories.TryGetValue(p.A, out var a) & memories.TryGetValue(p.B, out var b), $"{p.Id}: two memories of the corpus");
            Assert.Equal(a!.Scope, b!.Scope);
            Assert.True(InsightSignals.Eligible(Corpus, a) && InsightSignals.Eligible(Corpus, b), $"{p.Id}: both eligible");
            Assert.True(b.CreatedAt - a.CreatedAt >= ParityPairs.InsightGap, $"{p.Id}: a first, 30 or more days before b");
            Assert.DoesNotContain(Key(p.A, p.B), earlier);
            if (p.Label == "unrelated") Assert.False(links.Contains((p.A, p.B)) || links.Contains((p.B, p.A)), $"{p.Id}: linked in the corpus");
        });
        // Each label with hard cases among them, but related: its three hard pairs (the heat pump's remote, the runs and
        // the knee, the plot's rent) were labelled in pairs.json by run 19's links slice too, and are kept there alone,
        // where `hard` means what a similarity threshold gets wrong.
        foreach (var label in pairs.Labels.Keys.Where(l => l != "related")) Assert.Contains(pairs.Pairs, p => p.Label == label && p.Hard);
        Assert.Equal(["p-rel-handover-parcel", "p-rel-plot-new-rent", "p-rel-running-new-knee"],
            labelled.Pairs.Where(p => p.Id is "p-rel-handover-parcel" or "p-rel-running-new-knee" or "p-rel-plot-new-rent" && p.Label == "related")
                .Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact] public void The_corpus_has_what_the_plan_asks_for_and_nothing_real()
    {
        Assert.StartsWith("DEMO", Corpus.Label);
        Assert.StartsWith("DEMO", Golden.Label);
        Assert.Equal(["alex", "sam"], Corpus.People.Select(p => p.Id));
        Assert.Equal("Europe/London", Corpus.Timezone);
        Assert.Contains(Corpus.Scopes, s => s.Kind == "shared" && s.Readers.Length == 2);
        Assert.Contains(Corpus.Memories, m => m.Expect is { Held: true, Reason: "prompt-injection" });
        Assert.Contains(Corpus.Memories, m => m.Stale is not null);
        Assert.Contains(Corpus.Memories, m => m.Status == "draft");
        Assert.Contains(Corpus.Memories, m => m.SharedFrom is not null && m.Scope == "family");
        Assert.Contains(Corpus.Documents, d => d.Source.Kind == "drive" && d.Revisions.Length > 0);
        Assert.Contains(Corpus.Documents, d => d.Text.Length > 2000);
        Assert.NotEmpty(Corpus.Notes);
        Assert.Contains(Corpus.Measurements, m => m.Subject == "alex");
        Assert.Contains(Corpus.Measurements, m => m.Subject == "sam");
        Assert.NotEmpty(Corpus.Dashboards);
        foreach (var type in new[] { "exact-id", "paraphrase", "graph-chain", "as-of", "visibility" })
            Assert.Contains(Golden.Queries, q => q.Type == type);
        // A replaced fact names its successor, which supersedes it and starts after it ended.
        foreach (var old in Corpus.Memories.Where(m => m.ReplacedBy is not null))
        {
            var successor = Corpus.Memories.Single(m => m.Id == old.ReplacedBy);
            Assert.Contains(successor.Links, l => l.Type == "supersedes" && l.Target == old.Id);
            Assert.True(old.ValidUntil < successor.ValidFrom, old.Id);
        }
        Assert.Contains(Corpus.Viewers, v => v.Person is null);
        // Only invented hosts and addresses, as everywhere in this repository's fixtures.
        var text = string.Concat(new[] { "corpus.json", "golden-queries.json", "pairs.json", "insight-pairs.json" }
            .Select(f => File.ReadAllText(Path.Combine(ParityCorpus.Folder, f))));
        Assert.All(Url().Matches(text), m => Assert.EndsWith(".invalid", m.Groups["host"].Value));
        Assert.All(Email().Matches(text), m => Assert.Equal("example.invalid", m.Groups["domain"].Value));
    }

    [Fact] public async Task The_map_lists_what_the_second_brain_adapter_would()
    {
        var source = new ParityMapSource(Corpus);
        var listing = await source.List(new SourcePass(new Dictionary<ResourceKey, DiscoveryRecord>(), (_, _, _, _) => null, 3000, 1000), default);
        RelationshipDiscovery.Validate(new(listing.Records));
        var byId = listing.Records.ToDictionary(r => r.Key.ExternalId[ParityLoader.FilePrefix.Length..]);
        // Drafts and replaced facts are not stars; the injection is, since nothing in Rhythm holds it today.
        Assert.DoesNotContain("mem-alex-draft", byId.Keys);
        Assert.DoesNotContain("mem-alex-job-old", byId.Keys);
        Assert.DoesNotContain("mem-sam-plot-old", byId.Keys);
        Assert.Contains("mem-alex-injection", byId.Keys);
        Assert.Contains("project:home-energy", byId.Keys);
        // Family records are the household's and shared; a personal one is its person's and private.
        Assert.Equal(("household", "shared"), (byId["mem-family-bins"].Key.Subject, byId["mem-family-bins"].Scope));
        Assert.Equal(("sam", "private"), (byId["mem-sam-party"].Key.Subject, byId["mem-sam-party"].Scope));
        // As the Second Brain adapter lands them: its personal workspace with owner household private under the
        // household, a team workspace shared under it; a former person's records keep their subject.
        Assert.Equal(("household", "private"), (byId["mem-household-dentist"].Key.Subject, byId["mem-household-dentist"].Scope));
        Assert.Equal(("household", "shared"), (byId["mem-team-bees-harvest"].Key.Subject, byId["mem-team-bees-harvest"].Scope));
        Assert.Equal(("jo", "private"), (byId["mem-jo-darkroom"].Key.Subject, byId["mem-jo-darkroom"].Scope));
        // Typed links keep their type, direction and Second Brain's provenance mapping.
        var links = RelationshipDiscovery.Discover(listing.Records);
        var decision = byId["mem-alex-heatpump-decision"].Key;
        Assert.Contains(links, l => l.Source == decision && l.Target == byId["mem-alex-boiler-fail"].Key && l.Type == "caused_by" && l.Verification == "verified" && l.Directed);
        Assert.Contains(links, l => l.Source == decision && l.Target == byId["project:home-energy"].Key && l.Type == "in_project");
        Assert.Contains(links, l => l.Source == byId["mem-alex-ticket"].Key && l.Type == "relates_to" && l.Verification == "suggested" && !l.Directed);
        Assert.Equal("Fixed the export crash in RHY-4127 by pinning the encoder to v2.14.3; the…", byId["mem-alex-ticket"].Title);
        Assert.Equal(byId["mem-alex-ticket"].Title, byId["mem-alex-ticket"].Overview);
    }

    [Fact] public async Task As_installed_each_record_sits_under_its_adapters_policy()
    {
        var map = new ParityMapSource(Corpus, ParityMode.Installed);
        var drive = new ParityDriveSource(Corpus, map);
        var pass = new SourcePass(new Dictionary<ResourceKey, DiscoveryRecord>(), (_, _, _, _) => null, 3000, 1000);
        var memories = (await map.List(pass, default)).Records;
        var documents = (await drive.List(pass, default)).Records;
        RelationshipDiscovery.Validate(new([.. memories, .. documents]));
        // The policies are the real adapters' own: the baseline's installed numbers rest on them.
        Assert.Equal(SourceAuthority.ReferenceOnly, map.Providers[ParityMapSource.MemoryProvider].Authority);
        Assert.Equal(SourceAuthority.AuthorizedArchive, map.Providers[ParityMapSource.NotesProvider].Authority);
        Assert.Equal(SourceAuthority.LiveSource, drive.Providers[ParityMapSource.DriveProvider].Authority);
        Assert.Equal(Corpus.Notes.Length, memories.Count(r => r.Key.Provider == ParityMapSource.NotesProvider));
        Assert.All(memories.Where(r => r.Key.Provider == ParityMapSource.MemoryProvider), r => Assert.Contains(r.Kind, (string[])["memory", "project"]));
        Assert.Equal(Corpus.Documents.Select(d => ParityLoader.FilePrefix + d.Id).Order(), documents.Select(r => r.Key.ExternalId).Order());
        // A typed link from a memory to a Drive document still resolves across the two sources.
        var links = RelationshipDiscovery.Discover([.. memories, .. documents]);
        Assert.Contains(links, l => l.Type == "drawn_from" && l.Target.Provider == ParityMapSource.DriveProvider);
        // A snapshot row carries the engine's catalog id, which the Drive check refuses; a bare file id at its
        // current revision passes, as DriveAdapter.Current answers.
        var quote = Corpus.Documents.Single(d => d.Id == "doc-heatpump-quote");
        var star = documents.Single(r => r.Key.ExternalId == ParityLoader.FilePrefix + quote.Id);
        Assert.False(await drive.Current(ParityMapSource.DriveProvider, star.Key.CatalogId, star.Revision, default));
        Assert.True(await drive.Current(ParityMapSource.DriveProvider, quote.Source.FileId, quote.Source.Revision, default));
        Assert.False(await drive.Current(ParityMapSource.DriveProvider, quote.Source.FileId, quote.Revisions[0].Revision, default));
    }

    [Fact] public void The_rank_metrics_match_worked_examples()
    {
        ParityGrade[] relevant = [new("a", 2), new("b", 1)];
        Assert.Equal(0.5, ParityMetrics.Recall(["x", "a", "y"], relevant, 3));
        Assert.Equal(1, ParityMetrics.Recall(["b", "a"], relevant, 2));
        Assert.Equal(0, ParityMetrics.Recall(["x", "y", "a"], relevant, 2));
        Assert.Null(ParityMetrics.Recall(["x"], [], 5));
        Assert.Equal(0.5, ParityMetrics.ReciprocalRank(["x", "b", "a"], relevant));
        Assert.Equal(0, ParityMetrics.ReciprocalRank(["x"], relevant));
        // Ideal order a, b: 3/log2(2) + 1/log2(3). Reversed: 1/log2(2) + 3/log2(3).
        Assert.Equal(1, ParityMetrics.Ndcg(["a", "b"], relevant, 5)!.Value, 6);
        Assert.Equal((1 + 3 / Math.Log2(3)) / (3 + 1 / Math.Log2(3)), ParityMetrics.Ndcg(["b", "a"], relevant, 5)!.Value, 6);
        Assert.Equal(["s"], ParityMetrics.Leaks(["x", "y", "z", "q", "r", "s"], ["s", "t"]));
        Assert.Equal(2, ParityMetrics.Percentile([4, 1, 3, 2], 50));
        Assert.Equal(4, ParityMetrics.Percentile([4, 1, 3, 2], 95));
    }

    // A memory's chain ends at the record that replaced it last.
    static string Head(string id)
    {
        while (Corpus.Memories.FirstOrDefault(m => m.Id == id)?.ReplacedBy is { } next) id = next;
        return id;
    }
    static bool Held(string id) => Corpus.Memories.Any(m => m.Id == id && m.Expect is { Held: true });
    static string? Language(string id) => Corpus.Memories.FirstOrDefault(m => m.Id == id)?.Language
        ?? Corpus.Notes.FirstOrDefault(n => n.Id == id)?.Language ?? Corpus.Documents.FirstOrDefault(d => d.Id == id)?.Language;
    // Whether a record said what was true on a day: written by then and inside its validity window; a document by any
    // revision written by then.
    static bool TrueOn(string id, DateOnly day)
    {
        static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);
        if (Corpus.Memories.FirstOrDefault(m => m.Id == id) is { } memory)
            return Day(memory.CreatedAt) <= day && !(memory.ValidFrom > day) && !(memory.ValidUntil < day);
        if (Corpus.Documents.FirstOrDefault(d => d.Id == id) is { } document)
            return document.Revisions.Select(r => r.ModifiedAt).Append(document.Source.ModifiedAt).Any(t => Day(t) <= day);
        return Corpus.Notes.Any(n => n.Id == id && Day(n.ModifiedAt) <= day);
    }
    // Records within `depth` typed links of a seed, either direction, through records on the map only.
    static HashSet<string> Reach(string seed, int depth, IReadOnlySet<string> onMap)
    {
        var edges = Corpus.Texts.SelectMany(t => t.Links.Where(l => l.Type != "part_of_project").SelectMany(l => new[] { (From: t.Id, To: l.Target), (From: l.Target, To: t.Id) }))
            .Where(e => onMap.Contains(e.From) && onMap.Contains(e.To)).ToLookup(e => e.From, e => e.To);
        var seen = new HashSet<string>(StringComparer.Ordinal) { seed };
        IEnumerable<string> frontier = [seed];
        for (var i = 0; i < depth; i++) frontier = frontier.SelectMany(x => edges[x]).Where(seen.Add).ToArray();
        return seen;
    }
    // Lower case without accents, so "Möwennest" and "mowennest" compare equal.
    static string Fold(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

    [GeneratedRegex(@"https?://(?<host>[^/\s""]+)")] private static partial Regex Url();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9.-]+\.[A-Za-z]+)")] private static partial Regex Email();
}
