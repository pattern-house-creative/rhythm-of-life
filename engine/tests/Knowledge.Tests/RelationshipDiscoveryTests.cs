using System.Text.Json;
using Knowledge;
using Knowledge.Relationships;

namespace Knowledge.Tests;

internal sealed class SyntheticRelationshipSource : IRelationshipSource
{
    public List<DiscoveryRecord> Records { get; } = [];
    public DiscoveryBatch? NextBatch { get; set; }
    public HashSet<ResourceKey> Denied { get; } = [];
    public bool Offline { get; set; }
    public Func<Task>? DuringCheck { get; set; }
    public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct) => Task.FromResult(NextBatch ?? new(Records.ToArray()));
    public async Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct)
    {
        if (DuringCheck is { } callback) { DuringCheck = null; await callback(); }
        if (Offline) throw new HttpRequestException("Synthetic outage");
        if (Denied.Contains(key)) return SourceCheck.Revoked;
        var record = Records.SingleOrDefault(r => r.Key == key);
        return record is null ? SourceCheck.Unavailable : record.Status switch {
            "deleted" => SourceCheck.Deleted, "revoked" => SourceCheck.Revoked,
            "missing" => SourceCheck.Unavailable, _ => record.Revision == revision ? SourceCheck.Available : SourceCheck.Stale };
    }
    public static DiscoveryRecord New(string provider, string kind = "document", string account = "synthetic-workspace")
    {
        var key = new ResourceKey("fixture", provider, account, Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new(key, "private", kind, "Fictional " + kind, $"https://example.invalid/{key.ExternalId}", "1", now, now, []);
    }
    public void Replace(DiscoveryRecord record)
    {
        var index = Records.FindIndex(r => r.Key == record.Key);
        if (index < 0) Records.Add(record); else Records[index] = record;
    }
}
internal sealed class SyntheticSuggestions : IRelationshipSuggestions
{
    public Task<IReadOnlyList<Suggestion>> Suggest(IReadOnlyList<DiscoveryRecord> records, CancellationToken ct)
    {
        var memory = records.SingleOrDefault(r => r.Kind == "memory");
        var structured = records.SingleOrDefault(r => r.Kind == "record");
        return Task.FromResult<IReadOnlyList<Suggestion>>(memory is null || structured is null ? [] :
            [new(memory.Key, structured.Key, "related_to", "synthetic:topic", "Fictional garden planning theme", "synthetic-suggester")]);
    }
}

public sealed class RelationshipDiscoveryTests
{
    [Fact] public async Task Similar_links_skip_linked_pairs_and_data_stars_are_never_copies()
    {
        static float[] Unit(params float[] v) { var norm = MathF.Sqrt(v.Sum(x => x * x)); return [.. v.Select(x => x / norm)]; }
        DiscoveryRecord Star(string title, string kind, float[] vector, string? data = null) =>
            SyntheticRelationshipSource.New(data is null ? "notes" : CatalogStars.Provider, kind, data is null ? "notes" : CatalogStars.DataAccount(data))
                with { Title = title, Embedding = vector };
        var walks = Star("Morning walks", "note", Unit(1, 0.1f, 0));
        var steps = Star("Walking and steps", "note", Unit(1, 0.3f, 0));
        var tea = Star("Evening tea", "note", Unit(0, 0, 1));
        var copy = Star("Copy of Morning walks", "note", Unit(1, 0.1f, 0.01f));
        var archive = Star("Alex · Glucose", "record", Unit(0, 1, 0), "csv");
        var live = Star("Alex · Glucose", "record", Unit(0, 1, 0.02f), "nightscout");
        var linked = Star("Walk log", "note", Unit(1, 0.12f, 0)) with { References = [new(walks.Key, null, "link", "Links to it")] };
        var links = await new SimilaritySuggestions(0.6, 3).Suggest([walks, steps, tea, copy, archive, live, linked], default);
        string Pair(Suggestion s) => string.Join(" + ", new[] { s.Source, s.Target }.Select(k => new[] { walks, steps, tea, copy, archive, live, linked }.Single(r => r.Key == k).Title).Order());
        Assert.Equal(["Copy of Morning walks + Morning walks"], links.Where(l => l.Type == SimilaritySuggestions.CopyType).Select(Pair));
        var similar = links.Where(l => l.Type == SimilaritySuggestions.Type).Select(Pair).ToArray();
        Assert.Contains("Alex · Glucose + Alex · Glucose", similar);
        Assert.Contains("Morning walks + Walking and steps", similar);
        Assert.DoesNotContain(similar, p => p.Contains("Evening tea"));
        Assert.DoesNotContain("Morning walks + Walk log", similar);
    }

    [Fact] public async Task Stars_each_among_the_others_closest_link_below_the_threshold_from_the_mutual_one()
    {
        static float[] Unit(params float[] v) { var norm = MathF.Sqrt(v.Sum(x => x * x)); return [.. v.Select(x => x / norm)]; }
        DiscoveryRecord Star(string title, float[] vector) => SyntheticRelationshipSource.New("notes", "note", "notes") with { Title = title, Embedding = vector };
        static string[] Pairs(IReadOnlyList<Suggestion> links, DiscoveryRecord[] stars) =>
            [.. links.Select(s => string.Join(" + ", new[] { s.Source, s.Target }.Select(k => stars.Single(r => r.Key == k).Title).Order())).Order()];
        // Résumé and Career story score 0.50, below the 0.6 threshold, and each is the other's closest: they link. Team
        // notes' closest is Career story (0.48), but Career story's is Résumé, so Team notes stays apart.
        var resume = Star("Résumé", Unit(1, 0, 0));
        var career = Star("Career story", Unit(0.5f, 0.866f, 0));
        var team = Star("Team notes", Unit(0, 0.554f, 0.8325f));
        DiscoveryRecord[] stars = [resume, career, team];
        var links = await new SimilaritySuggestions(0.6, 1, mutualThreshold: 0.45).Suggest(stars, default);
        Assert.Equal(["Career story + Résumé"], Pairs(links, stars));
        Assert.Equal("similarity 0.50, each among the other's closest", Assert.Single(links).Excerpt);
        Assert.Empty(await new SimilaritySuggestions(0.6, 1).Suggest(stars, default));
        // Mutual, but below the floor.
        DiscoveryRecord[] apart = [Star("Garden plan", Unit(1, 0)), Star("Tax return", Unit(0.4f, 0.9165f))];
        Assert.Empty(await new SimilaritySuggestions(0.6, 1, mutualThreshold: 0.45).Suggest(apart, default));
    }

    // With memory.scopes on, each pair of scopes is compared on its own (docs/dev/memory.md section 8, Budgets): no record
    // of another scope takes a neighbour's place. Two Family notes whose closest record is each Alex's private diary
    // link to each other only once Alex's records stop crowding them out, and the household's links are the same with
    // and without Alex's records.
    [Fact] public async Task With_scopes_on_a_records_closest_are_found_within_each_pair_of_scopes()
    {
        static float[] At(double degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];
        DiscoveryRecord Star(string subject, string title, float[] vector) => SyntheticRelationshipSource.New("notes", "note", "notes") is var r
            ? r with { Key = r.Key with { Subject = subject }, Scope = subject == "household" ? "shared" : "private", Title = title, Embedding = vector } : r;
        var rota = Star("household", "Allotment rota", At(0));
        var keys = Star("household", "Allotment keys", At(30));
        var diary = Star("alex", "Alex's allotment diary", At(10));
        var stranger = Star("jo", "Jo's allotment notes", At(5));
        DiscoveryRecord[] all = [rota, keys, diary, stranger];
        string[] Pairs(IReadOnlyList<Suggestion> links, string? only = null) => [.. links
            .Where(l => only is null || l.Source.Subject == only && l.Target.Subject == only)
            .Select(l => string.Join(" + ", new[] { l.Source, l.Target }.Select(k => all.Single(r => r.Key == k).Title).Order())).Order()];
        // One shared comparison, one neighbour each: both Family notes link only to Alex's diary or Jo's notes.
        var together = await new SimilaritySuggestions(0.5, 1).Suggest(all, default);
        Assert.Empty(Pairs(together, "household"));
        var scopes = RelationshipCatalog.For(TestSettings.Use()).Subjects;
        var apart = await new SimilaritySuggestions(0.5, 1, scopes: scopes).Suggest(all, default);
        Assert.Equal(["Allotment keys + Allotment rota"], Pairs(apart, "household"));
        Assert.Equal(["Alex's allotment diary + Allotment keys", "Alex's allotment diary + Allotment rota", "Allotment keys + Allotment rota"], Pairs(apart));
        Assert.Equal(Pairs(apart, "household"), Pairs(await new SimilaritySuggestions(0.5, 1, scopes: scopes).Suggest([rota, keys], default), "household"));
        // No pair of scopes is compared across two people, and a former person's records, in no scope, with no one.
        Assert.DoesNotContain(apart, l => l.Source.Subject == "jo" || l.Target.Subject == "jo");
    }

    // A star served only to a narrower scope than its key's (a household copy of Alex's private original, its Reader) is
    // compared in that scope's pairs: it never takes a Family record's neighbour's place, and the household's own links
    // are the same with and without it; it links to Family's records only in the pair whose both ends Alex reads.
    [Fact] public async Task With_scopes_on_a_copy_only_a_person_is_served_is_compared_in_that_persons_scope()
    {
        static float[] At(double degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];
        DiscoveryRecord Star(string subject, string title, float[] vector) => SyntheticRelationshipSource.New("notes", "note", "notes") is var r
            ? r with { Key = r.Key with { Subject = subject }, Scope = subject == "household" ? "shared" : "private", Title = title, Embedding = vector } : r;
        var rota = Star("household", "Allotment rota", At(0));
        var keys = Star("household", "Allotment keys", At(30));
        var copy = Star("household", "Alex's allotment diary", At(10)) with
        {
            Key = new("household", CatalogStars.Provider, "documents", "household:alex-diary"), Kind = "document", Reader = "alex",
        };
        DiscoveryRecord[] all = [rota, keys, copy];
        string[] Pairs(IReadOnlyList<Suggestion> links, bool familyOnly = false) => [.. links
            .Where(l => !familyOnly || l.Source != copy.Key && l.Target != copy.Key)
            .Select(l => string.Join(" + ", new[] { l.Source, l.Target }.Select(k => all.Single(r => r.Key == k).Title).Order())).Order()];
        var scopes = RelationshipCatalog.For(TestSettings.Use()).Subjects;
        // Taken as the household's, the copy is both Family notes' closest, and their own link is lost.
        Assert.DoesNotContain("Allotment keys + Allotment rota",
            Pairs(await new SimilaritySuggestions(0.5, 1, scopes: scopes).Suggest([rota, keys, copy with { Reader = null }], default)));
        var apart = await new SimilaritySuggestions(0.5, 1, scopes: scopes).Suggest(all, default);
        Assert.Equal(["Alex's allotment diary + Allotment keys", "Alex's allotment diary + Allotment rota", "Allotment keys + Allotment rota"], Pairs(apart));
        Assert.Equal(Pairs(await new SimilaritySuggestions(0.5, 1, scopes: scopes).Suggest([rota, keys], default)), Pairs(apart, familyOnly: true));
    }

    // A person's data star may be read by Family through a source share, and its closest household records would be
    // ranked against the person's own records too, so with scopes on it is compared with its own scope's records only: no
    // similar_to link joins it to a household record. The household's own data star still links to Family's records.
    [Fact] public async Task With_scopes_on_a_persons_data_star_is_compared_within_its_own_scope_only()
    {
        static float[] At(double degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];
        DiscoveryRecord Note(string subject, string title, float[] vector) => SyntheticRelationshipSource.New("notes", "note", "notes") is var r
            ? r with { Key = r.Key with { Subject = subject }, Scope = subject == "household" ? "shared" : "private", Title = title, Embedding = vector } : r;
        DiscoveryRecord Data(string subject, string title, float[] vector) => new(
            new(subject, CatalogStars.Provider, CatalogStars.DataAccount("cgm"), $"{subject}:glucose"), subject == "household" ? "shared" : "private",
            "record", title, null, "r1", null, DateTimeOffset.UtcNow, [], Embedding: vector);
        var alexData = Data("alex", "Alex glucose", At(0));
        var familyData = Data("household", "Family glucose", At(4));
        var rota = Note("household", "Glucose rota", At(2));
        var diary = Note("alex", "Alex's glucose diary", At(6));
        DiscoveryRecord[] all = [alexData, familyData, rota, diary];
        string[] Pairs(IReadOnlyList<Suggestion> links) => [.. links
            .Select(l => string.Join(" + ", new[] { l.Source, l.Target }.Select(k => all.Single(r => r.Key == k).Title).Order())).Order()];
        var together = Pairs(await new SimilaritySuggestions(0.5, 3).Suggest(all, default));
        Assert.Contains("Alex glucose + Glucose rota", together);
        var scopes = RelationshipCatalog.For(TestSettings.Use()).Subjects;
        var apart = Pairs(await new SimilaritySuggestions(0.5, 3, scopes: scopes).Suggest(all, default));
        Assert.DoesNotContain("Alex glucose + Glucose rota", apart);
        Assert.DoesNotContain("Alex glucose + Family glucose", apart);
        Assert.Contains("Alex glucose + Alex's glucose diary", apart);
        Assert.Contains("Alex's glucose diary + Glucose rota", apart);
        Assert.Contains("Family glucose + Glucose rota", apart);
    }

    // With scopes on a URL is told apart within each subject: a Family note citing a file that Alex's Drive and the
    // household's both list links to each, and its Family link does not depend on Alex's listing.
    [Fact] public void With_scopes_on_a_url_names_one_record_in_each_subject()
    {
        const string Url = "https://drive.google.com/file/d/fx-shared-file/view";
        DiscoveryRecord In(string subject, DiscoveryRecord r) => r with { Key = r.Key with { Subject = subject }, Scope = subject == "household" ? "shared" : "private" };
        var family = In("household", SyntheticRelationshipSource.New("drive") with { Url = Url });
        var alex = In("alex", SyntheticRelationshipSource.New("drive") with { Url = Url });
        var note = In("household", SyntheticRelationshipSource.New("notes", "note") with { References = [new(null, Url, "text:0")] });
        Assert.Empty(RelationshipDiscovery.Discover([note, family, alex]));
        Assert.Equal(new[] { family.Key, alex.Key }.Select(k => k.Subject).Order(), RelationshipDiscovery.Discover([note, family, alex], scoped: true).Select(l => l.Target.Subject).Order());
        Assert.Equal(family.Key, Assert.Single(RelationshipDiscovery.Discover([note, family], scoped: true)).Target);
        Assert.Equal(RelationshipDiscovery.Discover([note, family], scoped: true).Single().Id,
            RelationshipDiscovery.Discover([note, family, alex], scoped: true).Single(l => l.Target == family.Key).Id);
        // Two records of one subject with that URL are still ambiguous.
        var again = In("household", SyntheticRelationshipSource.New("drive", account: "another-workspace") with { Url = Url });
        Assert.Equal([alex.Key], RelationshipDiscovery.Discover([note, family, again, alex], scoped: true).Select(l => l.Target));
    }

    [Fact] public void Exact_ids_links_native_claims_and_suggestions_have_distinct_semantics()
    {
        var a = SyntheticRelationshipSource.New("notes-source", "memory");
        var b = SyntheticRelationshipSource.New("docs-source") with { Url = "https://docs.google.com/document/d/synthetic_doc/edit" };
        a = a with { References = [new(b.Key, null, "references[0]", "Cites fictional design"),
            new(null, "https://drive.google.com/open?id=synthetic_doc", "references[1]"),
            new(b.Key, null, "links[0]", "Source asserts dependency", "depends_on", true)] };
        var links = RelationshipDiscovery.Discover([a, b], [new(a.Key, b.Key, "references", "similarity", "Same theme", "fake-model")]);
        Assert.Equal(2, links.Count);
        var exact = links.Single(l => l.Type == "references");
        Assert.Equal("verified", exact.Verification); Assert.Equal(2, exact.Evidence.Count);
        Assert.Equal("inferred", links.Single(l => l.Type == "depends_on").Verification);
        var changed = RelationshipDiscovery.Discover([a with { Revision = "2" }, b]);
        Assert.Equal(exact.Id, changed.Single(l => l.Type == "references").Id);
        Assert.All(links, l => Assert.Equal(a.Key, l.Source));
    }

    [Fact] public void Url_normalization_is_conservative_and_accounts_and_subjects_do_not_blend()
    {
        Assert.Equal(RelationshipDiscovery.CanonicalUrl("https://docs.google.com/document/d/demo/edit?usp=sharing#heading"),
            RelationshipDiscovery.CanonicalUrl("https://drive.google.com/file/d/demo/view"));
        Assert.NotEqual(RelationshipDiscovery.CanonicalUrl("https://example.invalid/record?id=1"), RelationshipDiscovery.CanonicalUrl("https://example.invalid/record?id=2"));
        Assert.Null(RelationshipDiscovery.CanonicalUrl("javascript:alert(1)"));
        Assert.Null(RelationshipDiscovery.CanonicalUrl("https://secret@example.invalid/a"));
        var b = SyntheticRelationshipSource.New("docs-source");
        var other = b with { Key = b.Key with { Account = "another-workspace" } };
        var a = SyntheticRelationshipSource.New("notes-source") with { References = [new(null, b.Url, "link")] };
        Assert.Empty(RelationshipDiscovery.Discover([a, b, other]));
        a = a with { References = [new(other.Key, null, "exact-id")] };
        Assert.Equal(other.Key, Assert.Single(RelationshipDiscovery.Discover([a, b, other])).Target);
        other = other with { Key = other.Key with { Subject = "another-person" } };
        a = a with { References = [new(other.Key, null, "cross-subject")] };
        Assert.Empty(RelationshipDiscovery.Discover([a, other]));
        Assert.NotEqual(b.Key.CatalogId, other.Key.CatalogId);
        // A household's shared record may cite one person's document, and vice versa.
        var shared = a with { Key = a.Key with { Subject = "household" }, References = [new(null, b.Url, "link")] };
        Assert.Equal(b.Key, Assert.Single(RelationshipDiscovery.Discover([shared, b])).Target);
    }

    [Fact] public void Extraction_is_bounded_and_never_infers_a_dependency_from_an_exact_url()
    {
        var refs = RelationshipDiscovery.ExtractLinks("Fictional plan (https://example.invalid/plans/one). More https://example.invalid/plans/two#notes");
        Assert.Equal(2, refs.Count); Assert.Equal("https://example.invalid/plans/one", refs[0].Url);
        Assert.All(refs, r => Assert.False(r.Native));
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.ExtractLinks(new string('x', 40001)));
    }

    [Fact] public void Conflicting_duplicates_and_unlocated_suggestions_are_rejected()
    {
        var a = SyntheticRelationshipSource.New("docs-source"); var b = SyntheticRelationshipSource.New("notes-source");
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.Discover([a, a with { Revision = "2" }]));
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.Discover([a, b], [new(a.Key, b.Key, "related_to", "", "theme", "model")]));
    }
}

