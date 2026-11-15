using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Npgsql;

namespace Knowledge.Tests;

// Live screens with memory.scopes on (SB-P1-15; docs/dev/memory.md 7.5 and docs/dev/screens.md), on the synthetic parity
// corpus with scopes on (its own database, as MemoryScopeTests'). What a screen shows, and what it reports about a
// command, is the view of the app login it listens with: a viewer session's (an app login bound to its person, D-070)
// or the household view's. Another principal is told them only as far as it reads them, and never a viewer session's
// query; a command names only what its sender reads, a dashboard it can't read answering exactly as an unknown one; and
// what a command carries to another principal's screen is only what that principal reads. With scopes off every answer
// is as before, which ScreenTests and DashboardLayoutTests prove by passing unchanged, and each test here checks again.
// All of it is DEMO data.
[Collection(SharedStateCollection.Name)]
public sealed class ScreenScopeTests(ScopeCorpusFixture fixture) : IClassFixture<ScopeCorpusFixture>
{
    readonly ScreenWaits waits = new();
    ParityCorpus Corpus => fixture.Load.Corpus;
    NpgsqlDataSource Source => fixture.Source;
    DashboardStore Dashboards => new(Source, fixture.Load.Host);
    ScreenStore Store() => new(Source, waits, fixture.Load.Host);
    IServiceProvider Services() => new ServiceCollection().AddSingleton(Store()).AddSingleton(fixture.Load.Host).AddSingleton(Dashboards).BuildServiceProvider();
    HarnessTools Tools(Guid principal) => new(new Retrieval(fixture.Load.Db), new HarnessLogin(principal));
    static string NewScreen(string label) => label + "-" + Guid.NewGuid().ToString("N")[..16];

    // The star a corpus record stands for on the map, the first of a scope's memories that is on it.
    string Star(string scope) => Corpus.Memories.Where(m => m.Scope == scope)
        .Select(m => fixture.Load.ByGraphId.Any(p => p.Value.Contains(m.Id)) ? fixture.Load.GraphId(m.Id) : null).OfType<string>().First();
    // A person's viewer session (D-070): an app login bound to its person, a new one each time.
    Task<Guid> ViewerOf(string person) => Scoped.Principal(Source, $"screen-viewer-{person}-{Guid.NewGuid():N}", "app", person);
    async Task<Guid> Board(string by, string title, string star, DashboardLayout? layout = null)
    {
        DashboardFeed[] feeds = [new("about", "contents", star)];
        var (id, _) = await Dashboards.Save(fixture.Readers[by], null, null, new DashboardDraft(title, "DEMO · screen scope fixture", "<p>DEMO</p>", feeds,
            "First version."), [new DashboardStar(star, "DEMO")], "First version.", default, layout);
        return id;
    }

    // app_screens through the app's endpoint, as the app server calls it with that login: the status and the result.
    async Task<(int Status, JsonNode Content)> AppScreens(Guid app, object arguments)
    {
        var context = new DefaultHttpContext { RequestServices = Services() };
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "s", method = "tools/call",
            @params = new { name = "app_screens", arguments } }));
        context.Response.Body = new MemoryStream();
        await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), fixture.Load.Host, new AppHealth(fixture.Load.Db, TestSettings.Use()), AdapterRegistry.Empty);
        return (context.Response.StatusCode, JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!);
    }
    static void Valid(JsonNode content) => Schemas.Valid(Schemas.Folder("mcp"), "app_screens.output.schema.json", content);

    // A screen reports what it shows, listening with `by`'s login.
    Task Shows(Guid by, string screen, object showing) =>
        Store().Listen(by, screen, "DEMO screen", TimeSpan.Zero, JsonSerializer.SerializeToElement(showing), null, default);
    // What `reader` is told of `screen`: list_screens for a harness login, app_screens list for an app one. The whole
    // answer too, to look for text it must not hold.
    async Task<(JsonNode Screen, string Answer)> Listed(Guid reader, string screen)
    {
        var app = (string?)await Scoped.Scalar(Source, "SELECT role FROM principals WHERE id=@p", ("p", reader)) == "app";
        string answer;
        if (app)
        {
            var (status, content) = await AppScreens(reader, new { op = "list", screen });
            Assert.Equal(200, status);
            Valid(content);
            answer = content.ToJsonString();
        }
        else answer = await Tools(reader).ListScreens(Services());
        return (JsonNode.Parse(answer)!["screens"]!.AsArray().Single(s => (string?)s!["id"] == screen)!, answer);
    }
    async Task<JsonNode> Stored(string screen) =>
        JsonNode.Parse((string)(await Scoped.Scalar(Source, "SELECT showing::text FROM screens WHERE id=@s", ("s", screen)))!)!;
    static JsonNode Without(JsonNode showing, params string[] members)
    {
        var left = showing.DeepClone().AsObject();
        foreach (var member in members) left.Remove(member);
        return left;
    }

    [PostgresFact] public async Task Another_principal_is_told_what_a_viewer_sessions_screen_shows_only_as_far_as_it_reads_it()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"]; var app = fixture.Readers["app"];
        var alexViewer = await ViewerOf("alex"); var alexTablet = await ViewerOf("alex");
        var alexsOwn = Star("personal:alex"); var home = Star("family");
        var mine = await Board("alex", "DEMO · Alex's own board", alexsOwn);
        var ours = await Board("alex", "DEMO · Family board", home);
        Assert.Equal(("alex", "household"), ((string?)await Scoped.Scalar(Source, "SELECT subject_id FROM dashboards WHERE id=@d", ("d", mine)),
            (string?)await Scoped.Scalar(Source, "SELECT subject_id FROM dashboards WHERE id=@d", ("d", ours))));
        var phone = NewScreen("alex-phone");
        const string Query = "Sam surprise party guests", Title = "DEMO · Alex's own note";
        var tiles = new { main = "main", recovery = "recovery", training = "training", changed = "changed" };
        // Who is told the screen whole (the same person's logins: her client and her other session), and who only as far
        // as they read it (Sam, the household view's client and the app's shared login).
        Guid[] hers = [alex, alexViewer, alexTablet];
        Guid[] others = [sam, family, app];

        // A search: the query reaches no one but Alex.
        await Shows(alexViewer, phone, new { view = "search", query = Query, layout = "hero", tiles, narrow = false, parts = Array.Empty<object>(),
            activeAt = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O") });
        var reported = await Stored(phone);
        foreach (var reader in hers) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, phone)).Screen["showing"]), $"{reader}");
        foreach (var reader in others)
        {
            var (listed, answer) = await Listed(reader, phone);
            Assert.True(JsonNode.DeepEquals(Without(reported, "query"), listed["showing"]), $"{reader}: {listed["showing"]}");
            Assert.DoesNotContain(Query, answer);
        }

        // Her private star: unknown to the others, as if nothing were open; a Family star: told to everyone.
        await Shows(alexViewer, phone, new { view = "star", star = new { id = alexsOwn, title = Title }, layout = "hero", tiles, parts = Array.Empty<object>() });
        reported = await Stored(phone);
        foreach (var reader in hers) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, phone)).Screen["showing"]));
        foreach (var reader in others)
        {
            var (listed, answer) = await Listed(reader, phone);
            Assert.True(JsonNode.DeepEquals(Without(reported, "star"), listed["showing"]), $"{reader}: {listed["showing"]}");
            Assert.DoesNotContain(Title, answer);
            Assert.DoesNotContain(alexsOwn, answer);
        }
        await Shows(alexViewer, phone, new { view = "star", star = new { id = home, title = "DEMO · Family note" }, layout = "hero", tiles });
        reported = await Stored(phone);
        foreach (var reader in hers.Concat(others)) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, phone)).Screen["showing"]), $"{reader}");

        // Her private dashboard, full screen and her Home: its id, title and parts (whose focus names her star) reach no
        // one else, and list_screens tells them no Home; her Family dashboard reaches everyone who reads Family.
        object[] parts = [new { id = "map", kind = "map", x = 0, y = 0, w = 8, h = 12, focus = "near " + Title }, new { id = "page", kind = "page", x = 8, y = 0, w = 4, h = 12 }];
        await Shows(alexViewer, phone, new { view = "full_screen", dashboard = new { id = mine.ToString(), title = "DEMO · Alex's own board" }, layout = "hero", tiles,
            parts, home = new { dashboard = new { id = mine.ToString(), title = "DEMO · Alex's own board" } } });
        reported = await Stored(phone);
        foreach (var reader in hers)
        {
            var (listed, _) = await Listed(reader, phone);
            Assert.True(JsonNode.DeepEquals(reported, listed["showing"]));
            if (reader == alex) Assert.Equal(mine.ToString(), (string?)listed["home"]!["dashboard"]!["id"]);
        }
        foreach (var reader in others)
        {
            var (listed, answer) = await Listed(reader, phone);
            Assert.True(JsonNode.DeepEquals(Without(reported, "dashboard", "parts", "home"), listed["showing"]), $"{reader}: {listed["showing"]}");
            if (reader != app) Assert.Null(listed["home"]);
            Assert.DoesNotContain(mine.ToString(), answer);
            Assert.DoesNotContain(Title, answer);
        }
        await Shows(alexViewer, phone, new { view = "dashboard", dashboard = new { id = ours.ToString(), title = "DEMO · Family board" }, layout = "hero", tiles,
            parts, home = new { dashboard = new { id = ours.ToString(), title = "DEMO · Family board" } } });
        reported = await Stored(phone);
        foreach (var reader in hers.Concat(others)) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, phone)).Screen["showing"]), $"{reader}");
        Assert.Equal(ours.ToString(), (string?)(await Listed(sam, phone)).Screen["home"]!["dashboard"]!["id"]);

        // The household view's screen (the app's shared login) shows Family's: everyone who reads Family is told it whole,
        // its query included.
        var kitchen = NewScreen("kitchen");
        await Shows(app, kitchen, new { view = "search", query = "DEMO garden", layout = "corners", tiles });
        reported = await Stored(kitchen);
        foreach (var reader in new[] { alex, sam, family, app, alexViewer }) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, kitchen)).Screen["showing"]), $"{reader}");
        // A record that has left a reader's scopes since the screen reported it is not told to that reader either, from
        // its next read: the kitchen's Family star moves into Alex's private scope (a pass sees it there).
        await Shows(app, kitchen, new { view = "star", star = new { id = home, title = "DEMO · Family note" }, layout = "corners", tiles });
        var record = await Scoped.Snapshot(fixture.Load.Db, fixture.Readers["system"], home);
        var moved = await Scoped.Star(Source, record with { Key = record.Key with { Subject = "alex" }, Scope = "private", RetrievedAt = DateTimeOffset.UtcNow });
        try
        {
            Assert.Null((await Listed(sam, kitchen)).Screen["showing"]!["star"]);
            Assert.Equal(home, (string?)(await Listed(alex, kitchen)).Screen["showing"]!["star"]!["id"]);
        }
        finally { await Scoped.Execute(Source, "DELETE FROM resources WHERE id=@id", ("id", moved)); }
        Assert.Equal(home, (string?)(await Listed(sam, kitchen)).Screen["showing"]!["star"]!["id"]);

        // A screen nobody is known to have reported (a row from before 027) is told whole to no one until it reports again.
        await Shows(alexViewer, phone, new { view = "search", query = Query, layout = "hero", tiles });
        await Scoped.Execute(Source, "UPDATE screens SET shown_by=NULL WHERE id=@s", ("s", phone));
        foreach (var reader in hers.Concat(others)) Assert.DoesNotContain(Query, (await Listed(reader, phone)).Answer);
        await Shows(alexViewer, phone, new { view = "search", query = Query, layout = "hero", tiles });
        Assert.Contains(Query, (await Listed(alex, phone)).Answer);

        // A key the app does not report today (the engine takes any object as a showing, and a browser may send one): told
        // to her logins, and to no one else, who are told only the keys that name no record.
        const string Note = "DEMO secret note of Alex's";
        await Shows(alexViewer, phone, new { view = "search", query = Query, layout = "hero", tiles, narrow = false, note = Note, selection = new { star = alexsOwn } });
        reported = await Stored(phone);
        Assert.Equal(Note, (string?)reported["note"]);
        foreach (var reader in hers) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, phone)).Screen["showing"]), $"{reader}");
        foreach (var reader in others)
        {
            var (listed, answer) = await Listed(reader, phone);
            Assert.True(JsonNode.DeepEquals(Without(reported, "query", "note", "selection"), listed["showing"]), $"{reader}: {listed["showing"]}");
            Assert.DoesNotContain(Note, answer);
            Assert.DoesNotContain(alexsOwn, answer);
        }

        // With scopes off everyone is told every screen as it reported, as today.
        await Scoped.Mode(Source, "off");
        try
        {
            await Shows(alexViewer, phone, new { view = "full_screen", dashboard = new { id = mine.ToString(), title = "DEMO · Alex's own board" }, layout = "hero",
                tiles, parts, query = Query, home = new { dashboard = new { id = mine.ToString(), title = "DEMO · Alex's own board" } } });
            reported = await Stored(phone);
            foreach (var reader in hers.Concat(others)) Assert.True(JsonNode.DeepEquals(reported, (await Listed(reader, phone)).Screen["showing"]), $"{reader}");
        }
        finally { await Scoped.Mode(Source, "on"); }
    }

    // A screen as the app runs one, listening with `by`'s login: it takes each command and reports it shown with `detail`
    // and what it shows, until stopped.
    async Task Run(Guid by, string screen, object detail, object showing, CancellationToken stop)
    {
        var store = Store();
        while (!stop.IsCancellationRequested)
            try
            {
                var heard = await store.Listen(by, screen, "DEMO screen", TimeSpan.FromSeconds(25), null, null, stop);
                if (heard["command"] is { } command)
                    await store.Done(by, screen, Guid.Parse((string)command["id"]!), "shown", JsonSerializer.SerializeToElement(detail),
                        JsonSerializer.SerializeToElement(showing), default);
            }
            catch (OperationCanceledException) { return; }
    }

    [PostgresFact] public async Task A_command_names_only_what_its_sender_reads_and_its_reply_nothing_its_sender_cant_read()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"]; var app = fixture.Readers["app"];
        var alexViewer = await ViewerOf("alex"); var samViewer = await ViewerOf("sam");
        var alexsOwn = Star("personal:alex"); var samsOwn = Star("personal:sam"); var home = Star("family");
        var mine = await Board("alex", "DEMO · Alex's own board", alexsOwn);
        var ours = await Board("alex", "DEMO · Family board", home);
        const string Secret = "DEMO · Alex's own note";
        var tiles = new { main = "main", recovery = "recovery", training = "training", changed = "changed" };
        // Alex's phone shows her private board; its every report names her note. The kitchen (the household view) and
        // Sam's phone report Family's.
        var (phone, kitchen, his) = (NewScreen("alex-phone"), NewScreen("kitchen"), NewScreen("sam-phone"));
        var board = new { view = "dashboard", dashboard = new { id = mine.ToString(), title = "DEMO · Alex's own board" }, layout = "hero", tiles,
            parts = new object[] { new { id = "page", kind = "page", x = 0, y = 0, w = 12, h = 12 } } };
        var searched = new { summary = $"Searched for “party”: 2 stars", matches = 2, titles = new[] { Secret, "DEMO · Family note" } };
        await Shows(alexViewer, phone, board);
        await Shows(app, kitchen, new { view = "home", layout = "corners", tiles });
        await Shows(samViewer, his, new { view = "home", layout = "corners", tiles });
        using var stop = new CancellationTokenSource();
        Task[] running = [Run(alexViewer, phone, searched, board, stop.Token), Run(app, kitchen, new { summary = "Searched for “party”: 1 star", matches = 1,
            titles = new[] { "DEMO · Family note" } }, new { view = "search", query = "party", layout = "corners", tiles }, stop.Token),
            Run(samViewer, his, new { summary = "Opened DEMO · Family note" }, new { view = "home", layout = "corners", tiles }, stop.Token)];
        var services = Services();
        async Task<JsonNode> Control(Guid by, ScreenAction action, string screen, string? star = null, Guid? dashboard = null, string? query = null,
            string? corner = null, string? part = null, string? place = null, MapFocus? focus = null) =>
            JsonNode.Parse(await Tools(by).ControlScreen(services, action, screen: screen, star: star, dashboard: dashboard, query: query, corner: corner,
                part: part, place: place, focus: focus))!;
        async Task<string> Refused(Guid by, ScreenAction action, string screen, string? star = null, Guid? dashboard = null, string? query = null,
            string? corner = null, string? part = null, string? place = null, MapFocus? focus = null) =>
            (await Assert.ThrowsAsync<McpException>(() => Control(by, action, screen, star, dashboard, query, corner, part, place, focus))).Message;
        try
        {
            // A search on Alex's phone reports its matches to Alex alone: Sam and the household view's client are told it
            // was shown, and nothing it found.
            var told = await Control(alex, ScreenAction.Search, phone, query: "party");
            Assert.Equal("shown", (string?)told["result"]);
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(searched), told["detail"]));
            foreach (var other in new[] { sam, family })
            {
                var withheld = await Control(other, ScreenAction.Search, phone, query: "party");
                Assert.Equal("shown", (string?)withheld["result"]);
                Assert.Equal(["summary"], withheld["detail"]!.AsObject().Select(p => p.Key));
                Assert.DoesNotContain(Secret, withheld.ToJsonString());
                Assert.DoesNotContain("2 stars", withheld.ToJsonString());
            }
            // The kitchen reports Family's, which Sam reads: he is told it.
            Assert.Equal(1, (int)(await Control(sam, ScreenAction.Search, kitchen, query: "party"))["detail"]!["matches"]!);

            // Alex's private star is not on Sam's map: open_star answers as for an unknown star. Her board, and its star,
            // answer as an unknown dashboard does, through every action that names one.
            var unknownStar = await Refused(sam, ScreenAction.OpenStar, kitchen, star: "notes:notes:Nowhere.md");
            Assert.Equal(unknownStar, await Refused(sam, ScreenAction.OpenStar, kitchen, star: alexsOwn));
            var nobodys = Guid.NewGuid();
            foreach (var (action, corner) in new[] { (ScreenAction.OpenDashboard, (string?)null), (ScreenAction.FullScreen, null), (ScreenAction.SetHome, null),
                (ScreenAction.CornerCard, "main") })
            {
                var unknown = await Refused(sam, action, kitchen, dashboard: nobodys, corner: corner);
                Assert.Equal("Unknown dashboard.", unknown);
                Assert.Equal(unknown, await Refused(sam, action, kitchen, dashboard: mine, corner: corner));
            }
            Assert.Equal("Unknown dashboard.", await Refused(sam, ScreenAction.OpenStar, kitchen, star: $"{CatalogStars.Provider}:dashboard:{mine}"));
            // Arranging what Alex's phone shows: Sam can't read her board, so neither naming it nor leaving it to the screen
            // moves it.
            Assert.Equal("Unknown dashboard.", await Refused(sam, ScreenAction.Move, phone, dashboard: mine, part: "page", place: "left"));
            Assert.Equal("Unknown dashboard.", await Refused(sam, ScreenAction.Move, phone, part: "page", place: "left"));
            Assert.Equal("shown", (string?)(await Control(alex, ScreenAction.Move, phone, part: "page", place: "left"))["result"]);
            // Undo names nothing, but the phone would save her board's earlier layout back through her session: only a
            // sender that reads all her session reads sends it. The kitchen's undo is anyone's who reads Family.
            const string NotYours = "That screen's last change is in another scope than yours, so it can't be undone from here.";
            Assert.Equal(NotYours, await Refused(sam, ScreenAction.Undo, phone));
            Assert.Equal(NotYours, await Refused(family, ScreenAction.Undo, phone));
            Assert.Equal("shown", (string?)(await Control(alex, ScreenAction.Undo, phone))["result"]);
            Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.Undo, kitchen))["result"]);
            var (undone, _) = await AppScreens(samViewer, new { op = "send", screen = his, to = phone, action = "undo" });
            Assert.Equal(404, undone);

            // What a command carries to another principal's screen is what that screen reads: Sam's own star reaches his
            // phone, not Alex's or the kitchen; a Family star and a Family board reach every one of them.
            const string Elsewhere = "That screen can't show that: it reads another scope than yours.";
            Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.OpenStar, his, star: samsOwn))["result"]);
            Assert.Equal(Elsewhere, await Refused(sam, ScreenAction.OpenStar, phone, star: samsOwn));
            Assert.Equal(Elsewhere, await Refused(sam, ScreenAction.OpenStar, kitchen, star: samsOwn));
            Assert.Equal(Elsewhere, await Refused(alex, ScreenAction.OpenDashboard, kitchen, dashboard: mine));
            foreach (var screen in new[] { phone, kitchen, his })
            {
                Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.OpenStar, screen, star: home))["result"]);
                Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.OpenDashboard, screen, dashboard: ours))["result"]);
            }
            // A focus star is carried as a star is: Sam refocuses a Family map on Alex's phone onto a Family star, not
            // onto his own.
            var mapped = await Board("alex", "DEMO · Family map", home,
                new DashboardLayout([new("map", "map", 0, 0, 8, 12, new MapFocus(Near: new(home))), new("page", "page", 8, 0, 4, 12)]));
            Assert.Equal(Elsewhere, await Refused(sam, ScreenAction.FocusMap, phone, dashboard: mapped, focus: new MapFocus(Near: new(samsOwn))));
            Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.FocusMap, phone, dashboard: mapped, focus: new MapFocus(Near: new(home))))["result"]);
            // From Alex's phone (app_screens send, as her session): her star and her board reach neither the kitchen nor
            // Sam's phone, and a search typed in her session goes to her own screens only; a Family star goes anywhere.
            async Task<(int Status, JsonNode Content)> Send(Guid by, string from, string to, object command) =>
                await AppScreens(by, JsonSerializer.SerializeToNode(command)!.AsObject().Concat(new JsonObject { ["op"] = "send", ["screen"] = from, ["to"] = to })
                    .ToDictionary(p => p.Key, p => p.Value?.DeepClone()));
            foreach (var to in new[] { kitchen, his })
            {
                var (status, refused) = await Send(alexViewer, phone, to, new { action = "open_star", star = alexsOwn });
                Assert.Equal((404, Elsewhere), (status, (string?)refused["error"]!["message"]));
                Assert.Equal(404, (await Send(alexViewer, phone, to, new { action = "open_dashboard", dashboard = mine })).Status);
                Assert.Equal(400, (await Send(alexViewer, phone, to, new { action = "search", query = "party" })).Status);
                var (sent, shown) = await Send(alexViewer, phone, to, new { action = "open_star", star = home });
                Assert.Equal((200, "shown"), (sent, (string?)shown["command"]!["result"]));
                Valid(shown);
            }
            // The household view's screens send Family's anywhere it is read, their searches included.
            Assert.Equal(200, (await Send(app, kitchen, his, new { action = "search", query = "party" })).Status);
            // Its reply, read back as the sender, is the sender's to read: Alex's phone tells the kitchen nothing it found.
            var (_, back) = await Send(app, kitchen, phone, new { action = "search", query = "party" });
            Assert.Equal(["summary"], back["command"]!["detail"]!.AsObject().Select(p => p.Key));
            Assert.DoesNotContain(Secret, back.ToJsonString());

            // A screen whose login is not known (a row from before 027, until it reports again) is sent nothing that
            // names a record, not even a Family star, no viewer session's search and no undo.
            await Scoped.Execute(Source, "UPDATE screens SET shown_by=NULL WHERE id=@s", ("s", his));
            try
            {
                Assert.Equal(Elsewhere, await Refused(sam, ScreenAction.OpenStar, his, star: home));
                Assert.Equal(Elsewhere, await Refused(sam, ScreenAction.OpenDashboard, his, dashboard: ours));
                Assert.Equal(400, (await Send(alexViewer, phone, his, new { action = "search", query = "party" })).Status);
                Assert.Equal(NotYours, await Refused(sam, ScreenAction.Undo, his));
            }
            finally { await Scoped.Execute(Source, "UPDATE screens SET shown_by=@v WHERE id=@s", ("v", samViewer), ("s", his)); }

            // With scopes off, every sender names anything to any screen and is told every reply, as today.
            await Scoped.Mode(Source, "off");
            try
            {
                Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(searched), (await Control(sam, ScreenAction.Search, phone, query: "party"))["detail"]));
                Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.OpenStar, phone, star: alexsOwn))["result"]);
                Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.OpenDashboard, kitchen, dashboard: mine))["result"]);
                Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.Move, phone, part: "page", place: "left"))["result"]);
                Assert.Equal("shown", (string?)(await Control(sam, ScreenAction.Undo, phone))["result"]);
                Assert.Equal(200, (await Send(alexViewer, phone, kitchen, new { action = "search", query = "party" })).Status);
                Assert.Equal("Unknown dashboard.", await Refused(sam, ScreenAction.OpenDashboard, kitchen, dashboard: Guid.NewGuid()));
            }
            finally { await Scoped.Mode(Source, "on"); }
        }
        finally { stop.Cancel(); await Task.WhenAll(running); }
    }
}
