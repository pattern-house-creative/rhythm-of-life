using System.Globalization;
using System.Net;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Adapters.Calendar;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The calendar adapter through the engine's own paths, against a fake feed and a real database: a map rebuild, a star's
// text when it opens, a cancelled event leaving, and the link in no stored row. The feed's dates are near today, as the
// map's own clock is real.
[Collection(SharedStateCollection.Name)]
public sealed class CalendarDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IDisposable
{
    readonly TestSecrets secrets = new TestSecrets().With("calendar_link", FakeFeed.Link);
    readonly FakeFeed feed = new();
    public void Dispose() => secrets.Dispose();

    static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    [PostgresFact] public async Task Events_are_stars_on_the_map_open_when_read_leave_when_cancelled_and_the_link_is_stored_nowhere()
    {
        var config = TestSettings.Use();
        // Its own clock only says when a feed may be read again; the map's times are real.
        var ahead = TimeSpan.Zero;
        var calendar = new CalendarAdapter(feed.Handler, () => DateTimeOffset.UtcNow + ahead);
        calendar.Configure(new(new("calendar", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = "alex", ["pollSeconds"] = "300" }),
            config, fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"))));
        var registry = new AdapterRegistry([calendar]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);

        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);
        string Feed(params string[] extra) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n"
            + $"BEGIN:VEVENT\r\nUID:one@fictional\r\nDTSTART:{Stamp(tomorrow)}\r\nSUMMARY:Dentist\r\nLOCATION:Clinic\r\nDESCRIPTION:Bring the form\r\n{string.Join("", extra.Select(e => e + "\r\n"))}END:VEVENT\r\n"
            + $"BEGIN:VEVENT\r\nUID:two@fictional\r\nDTSTART:{Stamp(tomorrow.AddHours(1))}\r\nSUMMARY:Standup\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        feed.Body = Feed();
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Empty(host.Notes);

        // Three stars of provider calendar, the owner's, read as a memory source's are: no text until opened.
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var page = await host.Read(app, null, null, default);
        var stars = page.nodes.Where(n => n.provenance.sourceId == CalendarAdapter.Provider).ToArray();
        Assert.Equal(3, stars.Length);
        Assert.All(stars, s => Assert.Equal(("memory", "alex"), (s.type, s.resource.owner)));
        Assert.All(stars, s => Assert.Null(s.resource.content));
        Assert.All(stars, s => Assert.Null(s.resource.sourceUrl));
        var dentist = Assert.Single(stars, s => s.title == "Dentist");
        Assert.Contains("Clinic", dentist.overview);

        // An AI client finds them through the map's own tools, and their overview says when.
        var harness = await principals.Create("calendar-assistant", "harness", null, Path.Combine(state, "harness"), default);
        var explored = await host.Explore(harness, "Standup", null, 1, 10, default);
        Assert.Equal(2, explored.stars.Count);
        Assert.All(explored.stars, s => Assert.Equal(CalendarAdapter.Provider, s.provider));
        Assert.All(explored.stars, s => Assert.Null(s.open.url));
        var byDate = await host.Explore(harness, TimeZoneInfo.ConvertTime(tomorrow, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, 1, 10, default);
        Assert.Contains(byDate.stars, s => s.title == "Dentist");

        var opened = await host.Read(app, null, null, default, new GraphQuery("content", dentist.id));
        Assert.Contains("Bring the form", Assert.Single(opened.nodes).resource.content);

        // No stored row holds the link, or the description.
        foreach (var words in new[] { FakeFeed.Secret, FakeFeed.Host, "Bring the form" })
        {
            await using var cmd = fixture.Source.CreateCommand("SELECT count(*) FROM resources WHERE resources::text LIKE @words");
            cmd.Parameters.AddWithValue("words", "%" + words + "%");
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }

        // Down: nothing leaves, and the map says so by kind.
        feed.Status = HttpStatusCode.InternalServerError;
        ahead += TimeSpan.FromMinutes(10);
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Contains(host.Notes, n => n.Contains("calendar", StringComparison.Ordinal) && n.Contains("server error", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Notes, n => n.Contains(FakeFeed.Host, StringComparison.Ordinal));
        Assert.Equal(3, (await host.Read(app, null, null, default)).nodes.Count(n => n.provenance.sourceId == CalendarAdapter.Provider));

        // Back, with the dentist cancelled: its star leaves at this rebuild, and the series' stay under their ids.
        feed.Status = HttpStatusCode.OK;
        feed.Body = Feed("STATUS:CANCELLED");
        ahead += TimeSpan.FromHours(1);
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Empty(host.Notes);
        var left = (await host.Read(app, null, null, default)).nodes.Where(n => n.provenance.sourceId == CalendarAdapter.Provider).ToArray();
        Assert.Equal(["Standup", "Standup"], left.Select(n => n.title));
        Assert.Equal(explored.stars.Select(s => s.graphId).Order(StringComparer.Ordinal), left.Select(n => n.id).Order(StringComparer.Ordinal));
        Assert.Empty((await host.Read(app, null, null, default, new GraphQuery("content", dentist.id))).nodes);
        Directory.Delete(state, true);
    }

    // A reference provider's rows are never a catalog result (Retrieval.Validate), so what a turn of the adapter left reaches
    // no answer once it is off: the map shows no provider whose adapter is off, and no other tool reads them.
    [PostgresFact] public async Task Turned_off_what_an_earlier_turn_left_is_in_no_answer_and_on_again_its_stars_come_back()
    {
        var config = TestSettings.Use();
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        (RelationshipHost Host, Database Db) Stack(params IAdapter[] enabled)
        {
            var registry = new AdapterRegistry(enabled);
            var db = new Database(fixture.Source, registry.Policies(config));
            return (new(db, () => RelationshipHostConfiguration.From(config, graph, state),
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry), db);
        }
        CalendarAdapter Calendar()
        {
            var calendar = new CalendarAdapter(feed.Handler);
            calendar.Configure(new(new("calendar", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = "alex" }), config, fixture.Db, null!));
            return calendar;
        }
        feed.Body = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:retire@fictional\r\nDTSTART:{Stamp(DateTimeOffset.UtcNow.AddDays(1))}\r\nSUMMARY:Grandmother visit\r\nLOCATION:Harbour Cafe\r\nCREATED:{Stamp(DateTimeOffset.UtcNow.AddDays(-1))}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var (on, onDb) = Stack(Calendar());
        Assert.Equal("complete", await on.Refresh(false, default));
        var harness = await principals.Create("calendar-off", "harness", null, Path.Combine(state, "harness"), default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var star = Assert.Single((await on.Explore(harness, "Grandmother", null, 1, 5, default)).stars);
        Assert.Contains((await on.Changes(harness, DateTimeOffset.UtcNow.AddDays(-2), 100, default)).stars, s => s.graphId == star.graphId);
        // Even on, the catalog's tools do not serve it: an AI client asks the map.
        Assert.DoesNotContain("Grandmother", await new Retrieval(onDb).Search(harness, "grandmother", 5, default));

        var (off, offDb) = Stack();
        Assert.Equal("complete", await off.Refresh(false, default));
        Assert.DoesNotContain((await off.Read(app, null, null, default)).nodes, n => n.provenance.sourceId == CalendarAdapter.Provider);
        Assert.Empty((await off.Read(app, null, null, default, new GraphQuery("search", "Grandmother"))).nodes);
        Assert.Empty((await off.Explore(harness, "Grandmother", null, 1, 5, default)).stars);
        Assert.DoesNotContain((await off.Changes(harness, DateTimeOffset.UtcNow.AddDays(-2), 100, default)).stars, s => s.graphId == star.graphId);
        Assert.False((await off.Known(harness, [star.graphId], default)).Contains(star.graphId));
        var offSearch = new Retrieval(offDb);
        Assert.DoesNotContain("Grandmother", await offSearch.Search(harness, "grandmother", 5, default));
        Assert.DoesNotContain("Grandmother", await offSearch.SearchResources(harness, "", "", CalendarAdapter.Provider, "", "", 50, default));

        var (again, _) = Stack(Calendar());
        Assert.Equal("complete", await again.Refresh(false, default));
        Assert.Equal(star.graphId, Assert.Single((await again.Explore(harness, "Grandmother", null, 1, 5, default)).stars).graphId);
        Directory.Delete(state, true);
    }

    [PostgresFact] public async Task With_scopes_on_a_persons_calendar_reaches_that_person_and_a_households_all_of_them()
    {
        await Scoped.Mode(fixture.Source, "on");
        await Scoped.Members(fixture.Source, "alex", "sam");
        try
        {
            var config = TestSettings.Use();
            var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var principals = new Principals(fixture.Source);
            var graph = await principals.System(Principals.GraphName, default);
            var (alex, sam) = (await Scoped.Principal(fixture.Source, "calendar-alex", "harness", "alex"), await Scoped.Principal(fixture.Source, "calendar-sam", "harness", "sam"));
            var tomorrow = Stamp(DateTimeOffset.UtcNow.AddDays(1));
            foreach (var (owner, summary, alexReads, samReads) in new[] { ("alex", "Alex's appointment", 1, 0), (RhythmConfig.Household, "Family lunch", 1, 1) })
            {
                feed.Body = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:{owner}@fictional\r\nDTSTART:{tomorrow}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
                var calendar = new CalendarAdapter(feed.Handler);
                calendar.Configure(new(new("calendar", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = owner }), config, fixture.Db, null!));
                var registry = new AdapterRegistry([calendar]);
                var db = new Database(fixture.Source, registry.Policies(config));
                var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
                    c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
                Assert.Equal("complete", await host.Refresh(false, default));
                Assert.Equal(alexReads, (await host.Explore(alex, summary, null, 1, 10, default)).stars.Count);
                Assert.Equal(samReads, (await host.Explore(sam, summary, null, 1, 10, default)).stars.Count);
                if (owner == RhythmConfig.Household) continue;
                // A source share opens a person's calendar to Family, as it does any adapter's rows, until it is revoked.
                await principals.Share("alex", CalendarAdapter.Provider, default);
                try
                {
                    Assert.Equal("complete", await host.Refresh(false, default));
                    Assert.Single((await host.Explore(sam, summary, null, 1, 10, default)).stars);
                }
                finally { await principals.Unshare("alex", CalendarAdapter.Provider, default); }
                Assert.Equal("complete", await host.Refresh(false, default));
                Assert.Empty((await host.Explore(sam, summary, null, 1, 10, default)).stars);
            }
            Directory.Delete(state, true);
        }
        finally { await Scoped.Mode(fixture.Source, "off"); }
    }

    [PostgresFact] public async Task Off_the_map_shows_no_calendar_star_with_a_link_on_disk_whatever_an_earlier_pass_left()
    {
        var config = TestSettings.Use();
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        (RelationshipHost Host, AdapterRegistry Registry) Stack(AdapterRegistry registry)
        {
            var db = new Database(fixture.Source, registry.Policies(config));
            return (new(db, () => RelationshipHostConfiguration.From(config, graph, state),
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry), registry);
        }
        // An earlier pass, on, leaves a star.
        feed.Body = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:left@fictional\r\nDTSTART:{Stamp(DateTimeOffset.UtcNow.AddDays(1))}\r\nSUMMARY:Left behind\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var calendar = new CalendarAdapter(feed.Handler);
        calendar.Configure(new(new("calendar", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = "alex" }), config, fixture.Db, null!));
        var (on, _) = Stack(new AdapterRegistry([calendar]));
        Assert.Equal("complete", await on.Refresh(false, default));
        Assert.Contains((await on.Read(app, null, null, default)).nodes, n => n.title == "Left behind");
        Assert.NotEmpty(feed.Requests);

        // Off, with the link still on disk: the registry the settings make has no calendar in it, though the adapter it would
        // make reaches this same feed, and the pass asks it nothing and shows nothing.
        feed.Requests.Clear();
        var (off, registry) = Stack(AdapterRegistry.For(config, [() => new CalendarAdapter(feed.Handler)]));
        Assert.DoesNotContain(registry.Enabled, a => a.Name == CalendarAdapter.AdapterName);
        Assert.Equal("complete", await off.Refresh(false, default));
        Assert.DoesNotContain((await off.Read(app, null, null, default)).nodes, n => n.provenance.sourceId == CalendarAdapter.Provider);
        Assert.Empty(feed.Requests);
        Directory.Delete(state, true);
    }

    // graph_changes is about a star's own changes at its source, so it shows what was lately booked or changed, not what is
    // near. What is near is found by its date (harness/chatgpt/daily-insight.md, step 1c; Q-162), however long ago it was booked.
    [PostgresFact] public async Task Graph_changes_shows_what_was_booked_lately_and_a_date_finds_what_is_coming_up()
    {
        var config = TestSettings.Use();
        var calendar = new CalendarAdapter(feed.Handler);
        calendar.Configure(new(new("calendar", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = "alex" }), config, fixture.Db, null!));
        var registry = new AdapterRegistry([calendar]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        var harness = await principals.Create("calendar-coming-up", "harness", null, Path.Combine(state, "harness"), default);
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);
        string Event(string uid, string summary, params string[] lines) => $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:{Stamp(tomorrow)}\r\nSUMMARY:{summary}\r\n{string.Concat(lines.Select(l => l + "\r\n"))}END:VEVENT\r\n";
        var longAgo = $"CREATED:{Stamp(DateTimeOffset.UtcNow.AddDays(-60))}";
        feed.Body = "BEGIN:VCALENDAR\r\n" + Event("lately@fictional", "Booked lately", $"CREATED:{Stamp(DateTimeOffset.UtcNow.AddDays(-1))}")
            + Event("ago@fictional", "Booked long ago", longAgo) + Event("silent@fictional", "Says nothing of when") + Event("weekly@fictional", "Every week", longAgo, "RRULE:FREQ=WEEKLY") + "END:VCALENDAR\r\n";
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Equal(["Booked lately"], (await host.Changes(harness, DateTimeOffset.UtcNow.AddDays(-2), 100, default)).stars.Where(s => s.provider == CalendarAdapter.Provider).Select(s => s.title));
        var date = TimeZoneInfo.ConvertTime(tomorrow, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var found = (await host.Explore(harness, date, null, 1, 60, default)).stars.Where(s => s.provider == CalendarAdapter.Provider).Select(s => s.title).Order(StringComparer.Ordinal);
        Assert.Equal(["Booked lately", "Booked long ago", "Every week", "Says nothing of when"], found);
        Directory.Delete(state, true);
    }

    // What docs/adapters/calendar.md says: an AI client reads the stored title and overview from the map, and a star's description
    // through dashboard_data's contents feed.
    [PostgresFact] public async Task An_ai_client_reads_a_description_through_a_dashboard_feed()
    {
        var config = TestSettings.Use();
        var calendar = new CalendarAdapter(feed.Handler);
        calendar.Configure(new(new("calendar", new Dictionary<string, object?> { ["enabled"] = "true", ["owner"] = "alex" }), config, fixture.Db, null!));
        var registry = new AdapterRegistry([calendar]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        var harness = await principals.Create("calendar-feed", "harness", null, Path.Combine(state, "harness"), default);
        feed.Body = $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:words@fictional\r\nDTSTART:{Stamp(DateTimeOffset.UtcNow.AddDays(1))}\r\nSUMMARY:Opened by a feed\r\nDESCRIPTION:Words only an opening reads\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        Assert.Equal("complete", await host.Refresh(false, default));
        var star = Assert.Single((await host.Explore(harness, "Opened by a feed", null, 1, 5, default)).stars);
        var read = await new DashboardFeeds(db, host, registry).Read(harness, [new("words", "contents", star.graphId)], false, default);
        Assert.Equal("ok", (string)read["feeds"]!["words"]!["status"]!);
        Assert.Contains("Words only an opening reads", (string)read["feeds"]!["words"]!["text"]!);
        Directory.Delete(state, true);
    }
}
