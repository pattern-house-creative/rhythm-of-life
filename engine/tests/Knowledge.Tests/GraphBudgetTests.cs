using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// A fictional memory source holding `count` notes of its own provider and one subject's, newest first. It lists what
// its pass allows that subject, as the notes and Second Brain adapters do, unless it ignores the budget.
internal sealed class ManyNotes(string name, int count, bool ignoresBudget = false, string subject = "household") : IMemorySource
{
    public int Offered = -1;
    public string Name => name;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [name] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        Offered = pass.MemoriesOf(subject);
        var now = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, ignoresBudget ? count : Math.Min(count, Offered))
            .Select(i => new DiscoveryRecord(new(subject, name, name, $"note-{i:D5}"), subject == "household" ? "shared" : "private", "note",
                $"Fictional note {i}", null, $"r{i}", null, now, []))
            .ToArray();
        return Task.FromResult(new SourceListing(records, Complete: !ignoresBudget && count <= Offered));
    }
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
}

// Fictional notes of the household, Sam and Alex, linked among themselves: Alex's past their share of the pass's links.
internal sealed class LinkedNotes : IMemorySource
{
    public const string Provider = "notes-linked";
    public bool WithAlex = true;
    public string Name => Provider;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var family = Records(Provider, "household", 10, 9);
        var sam = Records(Provider, "sam", 5, 4, family[0].Key);
        return Task.FromResult(new SourceListing([.. family, .. sam, .. WithAlex ? Records(Provider, "alex", 125, 99, family[1].Key) : []], true));
    }
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);

    // `count` notes of one subject, each referencing the next `links` of them and, given, one record of another scope.
    public static List<DiscoveryRecord> Records(string provider, string subject, int count, int links, ResourceKey? also = null)
    {
        var now = DateTimeOffset.UtcNow;
        ResourceKey Key(int i) => new(subject, provider, provider, $"{subject}-{i:D4}");
        return [.. Enumerable.Range(0, count).Select(i => new DiscoveryRecord(Key(i), subject == "household" ? "shared" : "private", "note",
            $"Fictional note {i}", null, "r1", null, now,
            [.. Enumerable.Range(1, links).Select(n => new Reference(Key((i + n) % count), null, $"text:{n}")),
             .. also is null ? Array.Empty<Reference>() : [new Reference(also, null, "text:0")]]))];
    }
}

// graph.maxMemories and graph.maxDocuments are the install's: two memory sources share them, and one pass never holds
// more records than the engine takes, whatever its sources list, so a rebuild never fails for its size.
public sealed class GraphBudgetTests
{
    static readonly Guid Principal = Guid.NewGuid();
    static CatalogInventory Data(RelationshipCatalog catalog) => new([new("alex", "cgm", "private", "glucose", "mg/dL", 288,
        new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 29), new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.Zero))], []);
    static CatalogRelationshipSource Source(params IAdapter[] adapters)
    {
        var config = TestSettings.Use();
        return new(RelationshipHostConfiguration.From(config, Principal, Path.GetTempPath()), new AdapterRegistry(adapters),
            (catalog, _) => Task.FromResult(Data(catalog)));
    }

    [Fact] public async Task Memory_sources_share_the_install_budget_in_their_order()
    {
        var notes = new ManyNotes("notes-a", 2500); var memories = new ManyNotes("notes-b", 2500); var more = new ManyNotes("notes-c", 10);
        var source = Source(notes, memories, more);
        var batch = await source.Fetch(Principal, default);
        Assert.Equal((3000, 500, -1), (notes.Offered, memories.Offered, more.Offered));
        Assert.Equal(3000, batch.Records.Count(r => r.Kind == "note"));
        Assert.Contains(batch.Records, r => r.Key.Provider == CatalogStars.Provider);
        Assert.Equal(["notes-c left out: graph.maxMemories is used by the adapters before it"], source.Notes);
        RelationshipDiscovery.Validate(batch);
    }

    [Fact] public async Task A_pass_past_the_engine_budget_keeps_the_catalog_stars_and_says_so()
    {
        var catalogOnly = await Source().Fetch(Principal, default);
        var stars = catalogOnly.Records.Select(r => r.Key).ToArray();
        Assert.NotEmpty(stars);
        var source = Source(new ManyNotes("notes-a", RelationshipDiscovery.MaximumRecords + 100, ignoresBudget: true));
        var batch = await source.Fetch(Principal, default);
        RelationshipDiscovery.Validate(batch);
        Assert.Equal(RelationshipDiscovery.MaximumRecords, batch.Records.Count);
        Assert.All(stars, star => Assert.Contains(batch.Records, r => r.Key == star));
        Assert.Equal([$"{100 + stars.Length} records past the map's {RelationshipDiscovery.MaximumRecords} left out"], source.Notes);
    }

    // With memory.scopes on (docs/dev/memory.md section 8, Budgets): the test settings' catalog has alex, sam, the
    // household and the demo's subject, so each holds a quarter of every budget, whatever the others hold.
    static CatalogRelationshipSource Scoped(params IAdapter[] adapters)
    {
        var settings = RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath());
        return new(settings with { Catalog = settings.Catalog with { Scoped = true } }, new AdapterRegistry(adapters),
            (catalog, _) => Task.FromResult(Data(catalog)));
    }
    static Dictionary<string, int> Count(DiscoveryBatch batch, string kind = "note") =>
        batch.Records.Where(r => r.Kind == kind).GroupBy(r => r.Key.Subject).ToDictionary(g => g.Key, g => g.Count());

    [Fact] public async Task With_scopes_on_each_subject_lists_against_its_own_share_of_the_budgets()
    {
        var catalog = RelationshipCatalog.For(TestSettings.Use());
        Assert.Equal([750, 750, 750, 750], catalog.Subjects.Select(s => catalog.Share(3000, s)));
        Assert.Equal([2, 1, 1, 1, 0], catalog.Subjects.Append("jo").Select(s => catalog.Share(5, s)));
        // With scopes off the install's budget is shared in order: Alex's notes leave the household 500 and Sam none.
        var off = await Source(new ManyNotes("notes-a", 2500, subject: "alex"), new ManyNotes("notes-b", 2500), new ManyNotes("notes-c", 10, subject: "sam"))
            .Fetch(Principal, default);
        Assert.Equal(new Dictionary<string, int> { ["alex"] = 2500, ["household"] = 500 }, Count(off));
        // With scopes on each subject's adapters use only that subject's share: Alex's notes take Alex's 750, a second
        // household source gets what the first left of the household's, and Sam's notes are all there.
        var alex = new ManyNotes("notes-a", 2500, subject: "alex"); var family = new ManyNotes("notes-b", 700); var more = new ManyNotes("notes-d", 100);
        var sam = new ManyNotes("notes-c", 10, subject: "sam");
        var source = Scoped(alex, family, more, sam);
        var batch = await source.Fetch(Principal, default);
        Assert.Equal((750, 750, 50, 750), (alex.Offered, family.Offered, more.Offered, sam.Offered));
        Assert.Equal(new Dictionary<string, int> { ["alex"] = 750, ["household"] = 750, ["sam"] = 10 }, Count(batch));
        Assert.Empty(source.Notes);
        RelationshipDiscovery.Validate(batch);
        // What the household and Sam list is the same whatever Alex's scope holds.
        var without = await Scoped(new ManyNotes("notes-a", 0, subject: "alex"), new ManyNotes("notes-b", 700), new ManyNotes("notes-d", 100),
            new ManyNotes("notes-c", 10, subject: "sam")).Fetch(Principal, default);
        static string[] Others(DiscoveryBatch b) => [.. b.Records.Where(r => r.Key.Subject != "alex").Select(r => r.Key.GraphId).Order(StringComparer.Ordinal)];
        Assert.Equal(Others(batch), Others(without));
        // An adapter is left out only once every subject's share is used up.
        var full = Scoped(new ManyNotes("notes-a", 750, subject: "alex"), new ManyNotes("notes-b", 750), new ManyNotes("notes-c", 750, subject: "sam"),
            new ManyNotes("notes-e", 750, subject: "fixture"), new ManyNotes("notes-f", 10, subject: "alex"));
        await full.Fetch(Principal, default);
        Assert.Equal(["notes-f left out: graph.maxMemories is used by the adapters before it"], full.Notes);
    }

    [Fact] public async Task With_scopes_on_each_subject_holds_its_own_share_of_the_pass()
    {
        // 5000 records a pass are 1250 for each subject, the catalog's stars first; a former person, in no share, none.
        var source = Scoped(new ManyNotes("notes-a", 3000, ignoresBudget: true, subject: "alex"), new ManyNotes("notes-b", 100, ignoresBudget: true),
            new ManyNotes("notes-j", 5, ignoresBudget: true, subject: "jo"));
        var batch = await source.Fetch(Principal, default);
        RelationshipDiscovery.Validate(batch);
        Assert.Equal(new Dictionary<string, int> { ["alex"] = 1249, ["household"] = 100 }, Count(batch));
        Assert.Contains(batch.Records, r => CatalogStars.IsData(r.Key) && r.Key.Subject == "alex");
        Assert.Equal([$"{3000 + 1 - 1250 + 5} records past their scope's share of the map's {RelationshipDiscovery.MaximumRecords} left out"], source.Notes);
        // The household's records are kept whatever Alex's volume.
        var less = await Scoped(new ManyNotes("notes-a", 10, ignoresBudget: true, subject: "alex"), new ManyNotes("notes-b", 100, ignoresBudget: true))
            .Fetch(Principal, default);
        Assert.Equal(batch.Records.Where(r => r.Key.Subject == "household").Select(r => r.Key),
            less.Records.Where(r => r.Key.Subject == "household").Select(r => r.Key));
    }

    // A star served only to a narrower scope than its key's (a household save_document copy of Alex's private original,
    // its Reader) counts against that scope's share of the pass, never the household's: the household's records are the
    // same however many such copies there are.
    [Fact] public async Task With_scopes_on_a_copy_only_a_person_is_served_uses_that_persons_share_of_the_pass()
    {
        var settings = RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath());
        CatalogRelationshipSource With(int copies) => new(settings with { Catalog = settings.Catalog with { Scoped = true } },
            new AdapterRegistry([new ManyNotes("notes-b", 1200, ignoresBudget: true)]),
            (_, _) => Task.FromResult(new CatalogInventory([], [.. Enumerable.Range(0, copies).Select(i => new CatalogDocument("household", "documents",
                "shared", $"file-{i:D4}", $"DEMO copy {i}", $"r{i}", null, null, 0, "", Reader: "alex"))])));
        var none = await With(0).Fetch(Principal, default);
        var source = With(1300);
        var many = await source.Fetch(Principal, default);
        RelationshipDiscovery.Validate(many);
        static string[] Household(DiscoveryBatch b) => [.. b.Records.Where(r => r.ScopeSubject == "household").Select(r => r.Key.GraphId).Order(StringComparer.Ordinal)];
        Assert.Equal(1200, Household(many).Length);
        Assert.Equal(Household(none), Household(many));
        // Alex's share holds 1250 of them, each still a household star, its reader named.
        Assert.Equal(1250, many.Records.Count(r => r.Reader == "alex"));
        Assert.All(many.Records.Where(r => r.Reader is not null), r => Assert.Equal("household", r.Key.Subject));
        Assert.Equal(["50 records past their scope's share of the map's 5000 left out"], source.Notes);
        // Only the catalog's own stars name a reader.
        Assert.Throws<ArgumentException>(() => RelationshipDiscovery.Validate(new([many.Records.First(r => r.Kind == "note") with { Reader = "alex" }])));
    }

    // With scopes on a person's data star may be read by Family through a source share, so what Family is served of it
    // never depends on the person's own records: its topic links to the household's records are taken apart from those
    // to the person's own, each up to their own cap, and its revision names the household links only. With scopes off
    // the twelve newest of both are taken, as before.
    [Fact] public void With_scopes_on_a_persons_data_star_serves_Family_nothing_of_their_own_records()
    {
        var off = RelationshipCatalog.For(TestSettings.Use());
        var on = off with { Scoped = true };
        var now = DateTimeOffset.UtcNow;
        var inventory = new CatalogInventory([new("alex", "nightscout", "private", "glucose", "mg/dL", 10,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 29), null)], []);
        DiscoveryRecord Note(string subject, string id, DateTimeOffset at) => new(new(subject, "notes", "notes", id),
            subject == "household" ? "shared" : "private", "note", "DEMO morning glucose check " + id, null, "r", at, now, []);
        var family = Enumerable.Range(0, CatalogStars.MaxTopicLinks).Select(i => Note("household", $"f{i}", now.AddDays(-30 + i))).ToList();
        var mine = Enumerable.Range(0, 3).Select(i => Note("alex", $"a{i}", now.AddHours(-1))).ToList();
        DiscoveryRecord Star(RelationshipCatalog catalog, IReadOnlyList<DiscoveryRecord> mapped) =>
            CatalogStars.Records(catalog, inventory, new HashSet<string>(), now, (_, _, _, _) => null, mapped).Single();
        static string[] Household(DiscoveryRecord star) =>
            [.. star.References.Where(r => r.Target!.Subject == "household").Select(r => r.Target!.ExternalId)];
        // Scopes off: Alex's three newer notes take three of the household's places, and change the revision.
        var shared = Star(off, [.. family, .. mine]);
        Assert.Equal(CatalogStars.MaxTopicLinks, shared.References.Count);
        Assert.Equal(CatalogStars.MaxTopicLinks - 3, Household(shared).Length);
        Assert.NotEqual(Star(off, family).Revision, shared.Revision);
        // Scopes on: the household's twelve whatever Alex holds, her three beside them, and one revision with or without.
        var with = Star(on, [.. family, .. mine]);
        var without = Star(on, family);
        Assert.Equal(CatalogStars.MaxTopicLinks, Household(with).Length);
        Assert.Equal(Household(without), Household(with));
        Assert.Equal(3, with.References.Count(r => r.Target!.Subject == "alex"));
        Assert.Equal(without.Revision, with.Revision);
        // Nor does it move with the household's records: a person outside Family reads her own star, and its revision
        // names no record of a scope she can't read (memory.md section 8).
        Assert.Equal(Star(on, mine).Revision, with.Revision);
    }

    static List<DiscoveryRecord> Linked(string subject, int count, int links, ResourceKey? also = null) =>
        LinkedNotes.Records("notes", subject, count, links, also);

    // The pass's 12 000 links (docs/dev/memory.md section 8, Budgets). With scopes off a pass past them fails for every
    // reader, as before. With scopes on each link counts in the scope whose readers alone can be served it, and each of
    // the catalog's four subjects keeps its own quarter: Alex's scope past its share loses only some of its own links,
    // verified ones kept first, and the household's and Sam's links are the same whatever Alex's scope holds.
    [Fact] public void With_scopes_on_a_pass_past_one_scopes_share_of_links_keeps_the_others_links()
    {
        var catalog = RelationshipCatalog.For(TestSettings.Use()) with { Scoped = true };
        var share = catalog.Share(RelationshipDiscovery.MaximumLinks, "alex");
        Assert.Equal(3000, share);
        var family = Linked("household", 10, 9);
        var sam = Linked("sam", 5, 4, family[0].Key);
        // 125 of Alex's notes, each linked to 99 of the others and to a Family note: far past Alex's share, and with the
        // others' past the pass's 12 000.
        var alex = Linked("alex", 125, 99, family[1].Key);
        var proposed = Enumerable.Range(0, 20).Select(i => new Suggestion(alex[i].Key, alex[(i + 100) % 125].Key, "similar_to",
            "similarity:test", "similarity 0.80", "test")).ToArray();
        IReadOnlyList<DiscoveryRecord> all = [.. family, .. sam, .. alex];
        var links = RelationshipDiscovery.Discover(all, proposed, scoped: true);
        Assert.True(links.Count > RelationshipDiscovery.MaximumLinks);
        Assert.Equal("Graph edge budget exceeded",
            Assert.Throws<InvalidOperationException>(() => RelationshipDiscovery.Bounded(links, all, null, out _)).Message);

        var kept = RelationshipDiscovery.Bounded(links, all, catalog, out var leftOut);
        static int Of(IEnumerable<DiscoveredLink> l, string subject) => l.Count(x => x.Source.Subject == subject || x.Target.Subject == subject);
        Assert.Equal(share, Of(kept, "alex"));
        Assert.Equal(links.Count - kept.Count, leftOut);
        Assert.All(kept.Where(l => l.Source.Subject == "alex"), l => Assert.Equal("verified", l.Verification));
        // Without Alex's records the household's 90 links and Sam's 25 are the very same.
        IReadOnlyList<DiscoveryRecord> others = [.. family, .. sam];
        var alone = RelationshipDiscovery.Bounded(RelationshipDiscovery.Discover(others, scoped: true), others, catalog, out var none);
        Assert.Equal(0, none);
        Assert.Equal((90, 25), (kept.Count(l => l.Source.Subject == "household" && l.Target.Subject == "household"), Of(kept, "sam")));
        Assert.Equal(alone.Select(l => l.Id), kept.Where(l => Of([l], "alex") == 0).Select(l => l.Id));
        // With scopes off a pass within the budget keeps every link, as before.
        Assert.Equal(alone, RelationshipDiscovery.Bounded(alone, others, null, out _));
    }

    // A person's records that a source share lets Family read count as the household's in the pass's link shares, so
    // the links Family is served between them and Family's records are the same however many private links the person
    // holds: here 40 of Alex's notes of a shared provider, each linked to a Family note, beside 125 private notes of hers
    // linked 99 ways (past her share). Without the share they would compete in her share, and lose.
    [Fact] public void Links_Family_reads_through_a_source_share_never_depend_on_the_persons_private_links()
    {
        var catalog = RelationshipCatalog.For(TestSettings.Use()) with { Scoped = true, Shares = new HashSet<(string, string)> { ("alex", "notes-shared") } };
        var family = Linked("household", 10, 9);
        var shared = LinkedNotes.Records("notes-shared", "alex", 40, 0, family[2].Key);
        var mine = Linked("alex", 125, 99, family[1].Key);
        bool FamilyReads(ResourceKey k) => k.Subject == "household" || k.Provider == "notes-shared";
        string[] Served(IReadOnlyList<DiscoveryRecord> records) =>
            [.. RelationshipDiscovery.Bounded(RelationshipDiscovery.Discover(records, scoped: true), records, catalog, out _)
                .Where(l => FamilyReads(l.Source) && FamilyReads(l.Target)).Select(l => l.Id)];
        var without = Served([.. family, .. shared]);
        Assert.Equal(90 + 40, without.Length);
        Assert.Equal(without, Served([.. family, .. shared, .. mine]));
        // With no share recorded the 40 count in Alex's own share (her scope's links), and her private volume decides.
        var unshared = catalog with { Shares = null };
        IReadOnlyList<DiscoveryRecord> all = [.. family, .. shared, .. mine];
        Assert.True(RelationshipDiscovery.Bounded(RelationshipDiscovery.Discover(all, scoped: true), all, unshared, out _)
            .Count(l => l.Source.Provider == "notes-shared") < 40);
    }

    // The same for the catalog's own stars of a shared provider, whose provider is their account (CatalogStars.
    // SourceProvider, as the map's Shown reads it): 40 document stars of Alex's `journal` records and 10 data stars of
    // her `garmin` measurements, each linked to a Family note, keep those links however many private links she holds.
    [Fact] public void Catalog_stars_of_a_shared_provider_count_as_Familys_in_the_link_shares()
    {
        var catalog = RelationshipCatalog.For(TestSettings.Use()) with
            { Scoped = true, Shares = new HashSet<(string, string)> { ("alex", "journal"), ("alex", "garmin") } };
        var family = Linked("household", 10, 9);
        var now = DateTimeOffset.UtcNow;
        DiscoveryRecord Star(string account, string kind, string id) => new(new("alex", CatalogStars.Provider, account, "alex:" + id), "private", kind,
            "Fictional " + id, null, "r1", null, now, [new Reference(family[2].Key, null, "text:0")]);
        List<DiscoveryRecord> stars = [.. Enumerable.Range(0, 40).Select(i => Star("journal", "document", $"j{i:D2}")),
            .. Enumerable.Range(0, 10).Select(i => Star(CatalogStars.DataAccount("garmin"), "record", $"g{i:D2}"))];
        Assert.Equal(["journal", "garmin"], stars.Select(s => CatalogStars.SourceProvider(s.Key)).Distinct());
        var mine = Linked("alex", 125, 99, family[1].Key);
        bool FamilyReads(ResourceKey k) => k.Subject == "household" || k.Provider == CatalogStars.Provider;
        string[] Served(IReadOnlyList<DiscoveryRecord> records, RelationshipCatalog scoping) =>
            [.. RelationshipDiscovery.Bounded(RelationshipDiscovery.Discover(records, scoped: true), records, scoping, out _)
                .Where(l => FamilyReads(l.Source) && FamilyReads(l.Target)).Select(l => l.Id)];
        var without = Served([.. family, .. stars], catalog);
        Assert.Equal(90 + 50, without.Length);
        Assert.Equal(without, Served([.. family, .. stars, .. mine], catalog));
        // With no share recorded they count in Alex's own share of the links, and her private volume decides.
        Assert.True(Served([.. family, .. stars, .. mine], catalog with { Shares = null }).Length < 90 + 50);
    }

    // Whose share a link uses: the narrower of its two ends' scopes, the scope of a household copy of a person's private
    // original being that person's (its Reader); two people's scopes are no one's.
    [Fact] public void A_link_counts_in_the_scope_whose_readers_alone_can_be_served_it()
    {
        ResourceKey Key(string subject, string id, string provider = "notes") => new(subject, provider, provider, id);
        var scopes = new Dictionary<ResourceKey, string>
        {
            [Key("alex", "a")] = "alex", [Key("sam", "s")] = "sam", [Key("household", "h")] = "household", [Key("fixture", "d")] = "fixture",
            [Key("household", "copy-a", CatalogStars.Provider)] = "alex", [Key("household", "copy-s", CatalogStars.Provider)] = "sam",
        };
        string? Scope(ResourceKey a, ResourceKey b) => RelationshipDiscovery.LinkScope(new(a, b, "references", "references", "verified", "test", [], null, null, null), scopes);
        Assert.Equal("alex", Scope(Key("alex", "a"), Key("household", "h")));
        Assert.Equal("alex", Scope(Key("household", "h"), Key("alex", "a")));
        Assert.Equal("household", Scope(Key("household", "h"), Key("fixture", "d")));
        Assert.Equal("fixture", Scope(Key("fixture", "d"), Key("fixture", "d")));
        Assert.Equal("alex", Scope(Key("household", "copy-a", CatalogStars.Provider), Key("household", "h")));
        Assert.Equal("alex", Scope(Key("household", "copy-a", CatalogStars.Provider), Key("fixture", "d")));
        Assert.Null(Scope(Key("household", "copy-a", CatalogStars.Provider), Key("household", "copy-s", CatalogStars.Provider)));
        Assert.Null(Scope(Key("alex", "a"), Key("sam", "s")));
        Assert.Null(Scope(Key("alex", "a"), Key("jo", "j")));
    }
}

// Against a real database, in one of its own: an adapter turned off after it held thousands of stars leaves rows that
// no pass lists again. Beside the next source's, they once took the working set past the engine's budget, so every
// rebuild and every map read failed for good.
public sealed class GraphBudgetDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task Rows_no_pass_lists_any_more_never_stop_the_map()
    {
        var config = TestSettings.Use();
        var first = new ManyNotes("notes-a", 3000); var second = new ManyNotes("notes-b", 3000);
        var db = new Database(fixture.Source, new AdapterRegistry([first, second]).Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), "rhythm-graph-" + Guid.NewGuid().ToString("N")[..10]);
        var on = new AdapterRegistry([first]);
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, on, (_, _) => Task.FromResult(new CatalogInventory([], []))), on);
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            // The first adapter is turned off and another holds as many: 6000 rows are current at once.
            on = new AdapterRegistry([second]);
            Assert.Equal("complete", await host.Refresh(false, default));
            var shown = (await host.Readable(graph, default)).View.Records;
            Assert.Equal(3000, shown.Count(r => r.Key.Provider == "notes-b"));
            Assert.True(shown.Count <= RelationshipDiscovery.MaximumRecords);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }

    // The pass's 12 000 links on two real rebuilds (docs/dev/memory.md section 8, Budgets): with scopes off a pass past
    // them fails for every reader, as before; with scopes on Alex's scope past its share loses some of its own links and
    // the rebuild completes, so Sam is served Family's links and his own, the same as when Alex's scope holds nothing.
    [PostgresFact] public async Task With_scopes_on_a_rebuild_past_one_scopes_link_share_keeps_the_others_links()
    {
        var s = fixture.Source;
        var config = TestSettings.Use();
        var notes = new LinkedNotes();
        var registry = new AdapterRegistry([notes]);
        var db = new Database(s, registry.Policies(config));
        var graph = await new Principals(s).System(Principals.GraphName, default);
        var alex = await Scoped.Principal(s, "links-alex", "harness", "alex");
        var sam = await Scoped.Principal(s, "links-sam", "harness", "sam");
        await Scoped.Members(s, "alex", "sam");
        var state = Path.Combine(Path.GetTempPath(), "rhythm-links-" + Guid.NewGuid().ToString("N")[..10]);
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (_, _) => Task.FromResult(new CatalogInventory([], []))), registry);
        async Task<string[]> Links(Guid reader) => [.. (await host.Readable(reader, default)).View.Links
            .Where(l => l.Source.Provider == LinkedNotes.Provider).Select(l => l.Id).Order(StringComparer.Ordinal)];
        try
        {
            Assert.Equal("Graph edge budget exceeded", (await Assert.ThrowsAsync<InvalidOperationException>(() => host.Refresh(false, default))).Message);
            await Scoped.Mode(s, "on");
            Assert.Equal("complete", await host.Refresh(false, default));
            // Alex's 125 notes link to 99 of each other and to a Family note each: 12 500 links in Alex's scope.
            Assert.Equal([$"{12_500 - 3000} links past their scope's share of the map's {RelationshipDiscovery.MaximumLinks} left out"], host.Notes);
            var with = await Links(sam);
            Assert.Equal(90 + 25, with.Length);
            Assert.Equal(3000 + 90 + 25, (await Links(graph)).Length);
            Assert.Equal(3000 + 90, (await Links(alex)).Length);
            // Alex's scope holds nothing: Sam's links are the very same, and nothing is left out.
            notes.WithAlex = false;
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Empty(host.Notes);
            Assert.Equal(with, await Links(sam));
        }
        finally
        {
            await Scoped.Mode(s, "off");
            await Scoped.Execute(s, "DELETE FROM memberships");
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }
}
