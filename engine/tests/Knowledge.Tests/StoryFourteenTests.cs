using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;
using Npgsql;

namespace Knowledge.Tests;

// Story 14 (SB-P6-06): graph stars, existing editable dashboards, health and data views and their links to the original
// source keep working, on the synthetic parity corpus (copies mode), as the app reads them: with memory.scopes off, every
// install's mode, exactly as before; and with scopes on (SB-P1-10) the household view the app reads keeps Family's,
// each person's client keeps their own, and a person's health reaches Family only through their source share. The
// app's side, at phone and desktop sizes, is app/tests/story-14.test.tsx; scripts/story-14.mjs runs both with the
// existing tests the suite keeps (SB-P6-06). All of it is DEMO data.
[Collection(SharedStateCollection.Name)]
public sealed class StoryFourteenTests(ScopeCorpusFixture fixture) : IClassFixture<ScopeCorpusFixture>
{
    ParityCorpus Corpus => fixture.Load.Corpus;
    RelationshipHost Host => fixture.Load.Host;
    NpgsqlDataSource Source => fixture.Source;

    async Task Off(Func<Task> check)
    {
        await Scoped.Mode(Source, "off");
        try { await check(); }
        finally { await Scoped.Mode(Source, "on"); }
    }
    // The star a corpus record stands for on the map, when it is on it.
    string? Star(string id) => fixture.Load.ByGraphId.Any(p => p.Value.Contains(id)) ? fixture.Load.GraphId(id) : null;
    // Whether a reader of the fixture reads a corpus scope: the app and the household token read Family's.
    static bool Reads(string reader, string scope) => scope == "family" || scope == "personal:" + ScopeCorpusFixture.Viewer(reader);
    async Task<GraphNode?> Get(Guid reader, string star) =>
        (await Host.Read(reader, null, null, default, new GraphQuery("get", star))).nodes.SingleOrDefault(n => n.id == star);

    // Each Drive document's star carries the link to its original in Drive, and each note's opens with its text: for the
    // app with scopes off (every star), and with scopes on for each reader exactly the records of the scopes it reads.
    [PostgresFact] public async Task Stars_reach_their_readers_with_the_link_to_their_original_and_open()
    {
        var documents = Corpus.Documents.Select(d => (d.Id, d.Scope, d.Source.Url, Star: Star(d.Id))).Where(d => d.Star is not null).ToArray();
        var notes = Corpus.Notes.Select(n => (n.Id, n.Scope, n.Text, Star: Star(n.Id))).Where(n => n.Star is not null).ToArray();
        Assert.Contains(documents, d => d.Scope == "family");
        Assert.Contains(documents, d => d.Scope.StartsWith("personal:", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Scope == "family");
        async Task Check(string reader, Func<string, bool> reads)
        {
            var principal = fixture.Readers[reader];
            foreach (var d in documents)
            {
                var node = await Get(principal, d.Star!);
                Assert.True(reads(d.Scope) == node is not null, $"{reader}: {d.Id} ({d.Scope}) {(node is null ? "is not served" : "is served")}");
                // The app opens a star's original from its canonical link, when the star says it may (safeSourceUrl).
                if (node is null) continue;
                Assert.Equal(d.Url, node.resource.canonicalUrl);
                Assert.True(node.resource.access is { canRead: true, canOpen: true }, $"{reader}: {d.Id} can't be opened");
            }
            foreach (var n in notes)
            {
                var opened = (await Host.Read(principal, null, null, default, new GraphQuery("content", n.Star!))).nodes.SingleOrDefault(x => x.id == n.Star);
                Assert.True(reads(n.Scope) == opened is not null, $"{reader}: {n.Id} ({n.Scope})");
                if (opened is not null) Assert.False(string.IsNullOrWhiteSpace(opened.resource.content), $"{reader}: {n.Id} opens with no text");
            }
        }
        await Off(() => Check("app", _ => true));
        foreach (var reader in new[] { "app", "family", "alex", "sam" }) await Check(reader, scope => Reads(reader, scope));
    }

    // A Family dashboard made by an AI client with no person, as every existing dashboard was, with a declared log write
    // the app allows. One whose log names the household: the app reads it and its feeds, writes a log entry and undoes
    // it, before the switch and after it; after it a Family member's viewer session does too, and it stays the
    // household's. One whose log names no subject, as most were saved: with scopes off its log is the first person's
    // (Alex's), so from the switch on it is Alex's readers' alone (memory.md sections 5 and 8): the household view, its
    // screens and Sam get it as an unknown dashboard, Alex's client reads it, and her viewer keeps its feeds, edits and
    // undo (writing from a dashboard is the app's).
    [PostgresFact] public async Task An_existing_editable_dashboard_keeps_its_feeds_its_edits_and_undo()
    {
        var store = new DashboardStore(Source, Host);
        var feedsReader = new DashboardFeeds(fixture.Load.Db, Host, fixture.Load.Registry);
        var home = Corpus.Dashboards.Single(d => d.Id == "dash-family-home");
        DashboardFeed[] feeds = [.. home.Cards.Select(c => Star(c.Ref)).OfType<string>().Select((s, i) => new DashboardFeed($"card{i}", "contents", s)),
            new DashboardFeed("checks", "log", null!, Log: "home-checks", Subject: "household")];
        Assert.True(feeds.Length > 1);
        DashboardWrite[] writes = [new("add", "log", Log: "home-checks", Subject: "household")];
        DashboardStar[] stars = [.. feeds.Where(f => f.Kind != "log").Select(f => new DashboardStar(f.Star, "DEMO"))];
        DashboardFeed[] unnamed = [.. feeds.Where(f => f.Kind != "log"), new DashboardFeed("checks", "log", null!, Log: "home-checks")];
        DashboardWrite[] unnamedWrites = [new("add", "log", Log: "home-checks")];
        Guid id = default; var version = 0;
        async Task Works(string who)
        {
            var principal = who == "alex viewer" ? await Viewer("alex") : fixture.Readers[who];
            var got = JsonNode.Parse((await store.Get(principal, id, null, true, default))!)!;
            Assert.True((bool)got["editsAllowed"]!, $"{who}: edits are not allowed");
            var read = (await feedsReader.Read(principal, feeds, false, default))["feeds"]!.AsObject();
            foreach (var feed in feeds) Assert.Equal("ok", (string?)read[feed.Name]!["status"]);
            var entry = new JsonObject { ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"), ["check"] = "DEMO boiler pressure" };
            var written = Guid.Parse((string)JsonNode.Parse(await store.Log(principal, id, version, "add", entry, default))!["id"]!);
            Assert.Contains(written.ToString(), (await store.Writes(principal, id, null, null, default))!);
            Assert.NotNull(JsonNode.Parse(await store.MarkUndone(principal, written, default))!["undoneAt"]);
        }
        Guid defaulted = default; var defaultedVersion = 0;
        await Off(async () =>
        {
            (id, version) = await store.Save(fixture.Readers["family"], null, null,
                new DashboardDraft("DEMO · Family home", "DEMO", "<p>DEMO</p>", feeds, "First version.", writes), stars, "First version.", default);
            await store.AllowEdits(fixture.Readers["app"], id, version, true, default);
            await Works("app");
            (defaulted, defaultedVersion) = await store.Save(fixture.Readers["family"], null, null,
                new DashboardDraft("DEMO · Family home, log by default", "DEMO", "<p>DEMO</p>", unnamed, "First version.", unnamedWrites), stars,
                "First version.", default);
            await store.AllowEdits(fixture.Readers["app"], defaulted, defaultedVersion, true, default);
        });
        await Works("app");
        await Works("alex viewer");
        Assert.Equal("household", await Scoped.Scalar(Source, "SELECT subject_id FROM dashboards WHERE id=@id", ("id", id)));
        // Sam's client reads it too (Sam is in Family).
        Assert.NotNull(await store.Get(fixture.Readers["sam"], id, null, false, default));

        // The dashboard whose log named no subject: saved with scopes off, its log feed and write are Alex's.
        Assert.Equal("alex alex", await Scoped.Scalar(Source, """
            SELECT (SELECT f->>'subject' FROM jsonb_array_elements(v.feeds) f WHERE f->>'kind'='log')||' '||(v.writes->0->>'subject')
            FROM dashboard_versions v WHERE v.dashboard_id=@id
            """, ("id", defaulted)));
        // With scopes on it is Alex's readers' alone: the household view (the app and its screens, the household token)
        // and Sam get it as an unknown one; Alex's client reads it, and her viewer reads and edits it as the app did.
        foreach (var other in new[] { "app", "family", "sam" })
            Assert.Null(await store.Get(fixture.Readers[other], defaulted, null, false, default));
        Assert.NotNull(await store.Get(fixture.Readers["alex"], defaulted, null, false, default));
        (id, version, feeds) = (defaulted, defaultedVersion, unnamed);
        await Works("alex viewer");
    }

    // A person's viewer session (D-070): an app principal bound to its person, made once.
    async Task<Guid> Viewer(string person)
    {
        var name = "story14-viewer-" + person;
        return await Scoped.Scalar(Source, "SELECT id FROM principals WHERE name=@n", ("n", name)) is Guid id ? id
            : await Scoped.Principal(Source, name, "app", person);
    }

    // Each person's health series reach their data views (app_graph's data, a dashboard's data feed), each star naming the
    // provider that holds them (its account, data:csv): every one for the app with scopes off; with scopes on only the person's own, and Family's (the
    // household view, a shared screen) only while that person shares the source.
    [PostgresFact] public async Task Health_series_reach_their_data_views_and_Family_only_through_a_share()
    {
        var feedsReader = new DashboardFeeds(fixture.Load.Db, Host, fixture.Load.Registry);
        // Each person's data stars, from the corpus series they stand for.
        var stars = fixture.Load.ByGraphId.Where(p => p.Value.Length > 0 && p.Value.All(id => Corpus.Measurements.Any(m => m.Id == id)))
            .Select(p => (Star: p.Key, Person: Corpus.Measurements.First(m => m.Id == p.Value[0]).Subject)).ToArray();
        Assert.Contains(stars, s => s.Person == "alex");
        Assert.Contains(stars, s => s.Person == "sam");
        async Task Check(string reader, Func<string, bool> reads)
        {
            var principal = fixture.Readers[reader];
            foreach (var (star, person) in stars)
            {
                var node = (await Host.Read(principal, null, null, default, new GraphQuery("data", star))).nodes.SingleOrDefault(n => n.id == star);
                Assert.True(reads(person) == node is not null, $"{reader}: {person}'s {star}");
                var feed = (await feedsReader.Read(principal, [new DashboardFeed("d", "data", star)], false, default))["feeds"]!["d"]!;
                Assert.Equal(reads(person) ? "ok" : "unavailable", (string?)feed["status"]);
                if (node is null) continue;
                Assert.Equal(CatalogStars.DataAccount(Knowledge.Adapters.Csv.CsvAdapter.Provider), node.resource.identity.account);
                Assert.Contains(node.measurements ?? [], s => s.days.Count > 0);
            }
        }
        await Off(() => Check("app", _ => true));
        foreach (var reader in new[] { "app", "alex", "sam" }) await Check(reader, person => person == ScopeCorpusFixture.Viewer(reader));
        await new Principals(Source).Share("alex", Knowledge.Adapters.Csv.CsvAdapter.Provider, default);
        try
        {
            foreach (var reader in new[] { "app", "alex", "sam" })
                await Check(reader, person => person == ScopeCorpusFixture.Viewer(reader) || person == "alex");
        }
        finally { await new Principals(Source).Unshare("alex", Knowledge.Adapters.Csv.CsvAdapter.Provider, default); }
        await Check("app", _ => false);
    }
}
