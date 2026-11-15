using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters.Csv;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;
using Npgsql;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// A subject and a scope on derived records (SB-P1-06, migration 027; docs/dev/memory.md sections 5 and 8): insights and
// their runs, dashboards with their versions, layouts and writes, pins and corner cards, on the synthetic parity corpus
// with memory.scopes on (its own database, as MemoryScopeTests'). Each lands in the narrowest scope of what it cites, is
// served only to readers of that scope and of every star it cites now, and is changed under memory.md section 5's author
// rules; one its reader can't read answers as an unknown one. With scopes off every answer is today's, which the dashboard,
// pin, card and layout tests (DatabaseTests, DashboardLayoutTests, ScreenTests, DriveMemoryDatabaseTests) prove by
// passing unchanged, and the last part of each test here checks again. All of it is DEMO data.
[Collection(SharedStateCollection.Name)]
public sealed class DerivedScopeTests(ScopeCorpusFixture fixture, ITestOutputHelper output) : IClassFixture<ScopeCorpusFixture>
{
    static readonly string[] Readers = ["alex", "sam", "family", "app"];
    ParityCorpus Corpus => fixture.Load.Corpus;
    NpgsqlDataSource Source => fixture.Source;
    DashboardStore Dashboards => new(Source, fixture.Load.Host);
    InsightStore Insights => new(Source, fixture.Load.Host);

    // The star a corpus record stands for on the map, when it is on it.
    string? Star(string id) => fixture.Load.ByGraphId.Any(p => p.Value.Contains(id)) ? fixture.Load.GraphId(id) : null;
    string Viewer(string reader) => ScopeCorpusFixture.Viewer(reader);
    // The principal a corpus author writes with: a person's own client, or one with no person (the household's).
    Guid Author(string? person) => fixture.Readers[person is null or "household" ? "family" : person];
    // A person's viewer session (D-070): an app principal bound to its person, made once per person.
    async Task<Guid> ViewerOf(string person)
    {
        var name = "derived-viewer-" + person;
        return await Scoped.Scalar(Source, "SELECT id FROM principals WHERE name=@n", ("n", name)) is Guid id ? id
            : await Scoped.Principal(Source, name, "app", person);
    }
    static DashboardDraft Draft(string title, IEnumerable<DashboardFeed> feeds, IReadOnlyList<DashboardWrite>? writes = null) =>
        new(title, "DEMO · scope fixture", "<p>DEMO</p>", [.. feeds], "First version.", writes);
    static IReadOnlyList<DashboardStar> Named(IEnumerable<DashboardFeed> feeds) =>
        [.. feeds.Where(f => f.Kind != "log").Select(f => new DashboardStar(f.Star, "DEMO"))];
    static InsightDraft Insight(string title, IEnumerable<string> stars, Guid? supersedes = null) =>
        new("discovery", title, "DEMO · a fictional insight.", "", "", [], "weak", "", [.. stars.Select(s => new InsightStar(s, "DEMO"))],
            [], InsightDraft.DefaultLabel, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), supersedes);
    async Task<string> Subject(string table, Guid id) => (string)(await Scoped.Scalar(Source, $"SELECT subject_id FROM {table} WHERE id=@id", ("id", id)))!;
    static JsonElement[] Rows(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => r.Clone())];
    static bool Lists(string json, Guid id) => Rows(json).Any(r => r.GetProperty("id").GetGuid() == id);

    // The corpus's derived records (D-061), each made the way the contract lets its author make it: with scopes on by
    // the author's own principal, or, for one the author's principal could not make now (a household token's insight on
    // Sam's private party, the corner showing Alex's private dashboard), before the switch, as every record is today.
    // Each reader is then served exactly the records whose scope the corpus says it reads.
    [PostgresFact] public async Task Each_reader_is_served_exactly_the_corpus_derived_records_its_scopes_hold()
    {
        var dashboards = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var insights = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var runs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var d in Corpus.Dashboards)
        {
            var feeds = d.Cards.Select(c => (c, star: Star(c.Ref))).Where(x => x.star is not null)
                .Select((x, i) => new DashboardFeed($"card{i}", x.c.Kind == "series" ? "data" : "contents", x.star!)).ToArray();
            Assert.NotEmpty(feeds);
            var (id, _) = await Dashboards.Save(Author(d.Owner), null, null, Draft(d.Title, feeds), Named(feeds), "First version.", default);
            dashboards[d.Id] = id;
            Assert.Equal(d.Scope == "family" ? "household" : d.Owner, await Subject("dashboards", id));
        }
        // Made before the switch: whoever the corpus says, with today's rule.
        bool Before(string? author, string scope) => author is null && scope != "family";
        foreach (var i in Corpus.Insights.OrderBy(i => Before(i.Author, i.Scope)))
        {
            if (Before(i.Author, i.Scope)) await Scoped.Mode(Source, "off");
            try { insights[i.Id] = await Insights.Save(Author(i.Author), Insight(i.Title, i.Cites.Select(Star).OfType<string>()), default); }
            finally { await Scoped.Mode(Source, "on"); }
            Assert.Equal(Before(i.Author, i.Scope) || i.Scope == "family" ? "household" : i.Author, await Subject("insights", insights[i.Id]));
        }
        foreach (var r in Corpus.InsightRuns)
        {
            if (Before(r.Author, r.Scope)) await Scoped.Mode(Source, "off");
            try { runs[r.Id] = await Insights.Log(Author(r.Author), InsightDraft.DefaultLabel, r.Outcome, r.Note, r.Insight is { } of ? insights[of] : null, default); }
            finally { await Scoped.Mode(Source, "on"); }
        }
        foreach (var p in Corpus.Pins) await Dashboards.SetPin(await ViewerOf(p.By), Star(p.Star)!, true, default);
        foreach (var c in Corpus.Corners)
        {
            var scope = Corpus.Dashboards.Single(d => d.Id == c.Dashboard).Scope;
            if (scope != "family") await Scoped.Mode(Source, "off");
            try { await Dashboards.SetCard(fixture.Readers["app"], c.Corner, dashboards[c.Dashboard], default); }
            finally { await Scoped.Mode(Source, "on"); }
        }
        foreach (var reader in Readers)
        {
            var principal = fixture.Readers[reader];
            var viewer = Viewer(reader);
            var listed = await Dashboards.List(principal, default);
            foreach (var d in Corpus.Dashboards)
            {
                var sees = Corpus.CanSee(viewer, d.Scope);
                Assert.True(sees == Lists(listed, dashboards[d.Id]), $"{reader} list {d.Id}");
                Assert.True(sees == (await Dashboards.Get(principal, dashboards[d.Id], null, true, default) is not null), $"{reader} get {d.Id}");
                Assert.True(sees == (await Dashboards.Feeds(principal, dashboards[d.Id], null, default) is not null), $"{reader} feeds {d.Id}");
            }
            var read = await Insights.List(principal, null, null, 50, default);
            foreach (var i in Corpus.Insights) Assert.True(Corpus.CanSee(viewer, i.Scope) == Lists(read, insights[i.Id]), $"{reader} insight {i.Id}");
            var logged = await Insights.Runs(principal, 14, default);
            foreach (var r in Corpus.InsightRuns) Assert.True(Corpus.CanSee(viewer, r.Scope) == Lists(logged, runs[r.Id]), $"{reader} run {r.Id}");
            var pins = Rows(await Dashboards.Pins(principal, default)).Select(p => p.GetString()).ToHashSet();
            foreach (var p in Corpus.Pins) Assert.True(Corpus.CanSee(viewer, p.Scope) == pins.Contains(Star(p.Star)), $"{reader} pin {p.Id}");
            using var cards = JsonDocument.Parse(await Dashboards.Cards(principal, default));
            foreach (var c in Corpus.Corners) Assert.True(Corpus.CanSee(viewer, c.Scope) == cards.RootElement.TryGetProperty(c.Corner, out _), $"{reader} corner {c.Id}");
            output.WriteLine($"{reader}: {Rows(listed).Length} dashboards, {Rows(read).Length} insights, {Rows(logged).Length} runs, {pins.Count} pins");
        }
        // An unreadable dashboard is unknown to every change, as one nobody saved is.
        var alexs = dashboards["dash-alex-recovery"];
        var sam = fixture.Readers["sam"]; var samViewer = await ViewerOf("sam");
        foreach (var refused in new Func<Task>[]
        {
            () => Dashboards.Save(sam, alexs, null, Draft("Taken", [new("x", "contents", Star("mem-sam-party")!)]), [new(Star("mem-sam-party")!, "DEMO")], "Next.", default),
            () => Dashboards.Save(sam, alexs, 1, null, [], "Back.", default),
            () => Dashboards.Delete(sam, alexs, "Alex recovery", default),
            async () => await Dashboards.SaveLayout(sam, alexs, 1, 0, DashboardLayout.PageAlone, "Arranged.", default),
            () => Dashboards.AllowEdits(samViewer, alexs, 1, true, default),
            () => Dashboards.SetCard(fixture.Readers["app"], "training", alexs, default),
        })
            Assert.Equal("P0002", await Scoped.Refused(refused));
        Assert.Null(await Insights.Record(fixture.Readers["app"], insights["ins-alex-knee-load"], "interesting", null, null, default));
        Assert.NotNull(await Insights.Record(await ViewerOf("alex"), insights["ins-alex-knee-load"], "interesting", null, null, default));
        Assert.Equal("22023", await Scoped.Refused(() => Insights.Log(sam, InsightDraft.DefaultLabel, "saved", null, insights["ins-alex-knee-load"], default)));
        Assert.Equal("22023", await Scoped.Refused(() => Insights.Save(sam, Insight("Taken over", [], insights["ins-alex-knee-load"]), default)));

        // With scopes off every reader is served every record, as today.
        await Scoped.Mode(Source, "off");
        try
        {
            foreach (var reader in Readers)
            {
                var principal = fixture.Readers[reader];
                var listed = await Dashboards.List(principal, default);
                Assert.All(dashboards.Values, id => Assert.True(Lists(listed, id)));
                var read = await Insights.List(principal, null, null, 50, default);
                Assert.All(insights.Values, id => Assert.True(Lists(read, id)));
                var pins = Rows(await Dashboards.Pins(principal, default)).Select(p => p.GetString()).ToHashSet();
                Assert.All(Corpus.Pins, p => Assert.Contains(Star(p.Star), pins));
                using var cards = JsonDocument.Parse(await Dashboards.Cards(principal, default));
                Assert.All(Corpus.Corners, c => Assert.Equal(dashboards[c.Dashboard].ToString(), cards.RootElement.GetProperty(c.Corner).GetString()));
            }
        }
        finally { await Scoped.Mode(Source, "on"); }
    }

    // Story 1 (memory.md section 11): a dashboard or insight citing Alex's private memory is invisible to Sam, made now
    // by Alex's client or made before the switch by a household one, and it narrows the moment a star it cites does: a
    // card part naming it, in a Family dashboard Sam reads, names no id or title for Sam.
    [PostgresFact] public async Task A_dashboard_or_insight_citing_Alexs_private_memory_is_invisible_to_Sam()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"];
        var memory = Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First();
        var home = Corpus.Memories.Where(m => m.Scope == "family").Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] Of(string star) => [new("about", "contents", star)];
        var (made, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Alex's own", Of(memory)), Named(Of(memory)), "First.", default);
        var insight = await Insights.Save(alex, Insight("DEMO · Alex's own insight", [memory]), default);
        Assert.Equal(("alex", "alex"), (await Subject("dashboards", made), await Subject("insights", insight)));
        Guid before, beforeInsight;
        await Scoped.Mode(Source, "off");
        try
        {
            (before, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Saved before", Of(memory)), Named(Of(memory)), "First.", default);
            beforeInsight = await Insights.Save(family, Insight("DEMO · Insight before", [memory]), default);
        }
        finally { await Scoped.Mode(Source, "on"); }
        Assert.Equal(("household", "household"), (await Subject("dashboards", before), await Subject("insights", beforeInsight)));
        foreach (var reader in Readers)
        {
            var sees = reader == "alex";
            var principal = fixture.Readers[reader];
            var listed = await Dashboards.List(principal, default);
            var read = await Insights.List(principal, null, null, 50, default);
            foreach (var id in new[] { made, before }) Assert.True(sees == Lists(listed, id), $"{reader} {id}");
            foreach (var id in new[] { insight, beforeInsight }) Assert.True(sees == Lists(read, id), $"{reader} {id}");
        }
        // A Family dashboard Alex composes, with a card of her own dashboard: Sam reads the Family dashboard, and its card
        // part as unavailable, with no id.
        var layout = new DashboardLayout([new("page", "page", 0, 0, 8, 12), new("mine", "card", 8, 0, 4, 4, Dashboard: made)]);
        var (shared, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Family, with a card", Of(home)), Named(Of(home)), "First.", default, layout);
        Assert.Equal("household", await Subject("dashboards", shared));
        JsonNode Card(string json) => JsonNode.Parse(json)!["layout"]!["parts"]!.AsArray().Single(p => (string)p!["id"]! == "mine")!;
        Assert.Equal(made, (Guid)Card((await Dashboards.Get(alex, shared, null, false, default))!)["dashboard"]!);
        var hidden = Card((await Dashboards.Get(sam, shared, null, false, default))!);
        Assert.Null(hidden["dashboard"]);
        Assert.True((bool)hidden["unavailable"]!);
        Assert.DoesNotContain(made.ToString(), await Dashboards.Get(sam, shared, null, true, default));
        Schemas.Valid(Schemas.Folder("mcp"), "app_dashboards.output.schema.json",
            new JsonObject { ["dashboard"] = JsonNode.Parse((await Dashboards.Get(sam, shared, null, true, default))!) });
        // Story 1's narrowing: the Family memory the Family dashboard cites moves into Alex's private scope (a pass sees
        // it there); at Sam's next read the dashboard is gone, with no pass in between, and back once it is Family again.
        var system = fixture.Readers["system"];
        var record = await Scoped.Snapshot(fixture.Load.Db, system, home);
        var moved = await Scoped.Star(Source, record with { Key = record.Key with { Subject = "alex" }, Scope = "private", RetrievedAt = DateTimeOffset.UtcNow });
        try
        {
            Assert.False(Lists(await Dashboards.List(sam, default), shared));
            Assert.Null(await Dashboards.Get(sam, shared, 1, false, default));
            Assert.True(Lists(await Dashboards.List(alex, default), shared));
        }
        finally { await Scoped.Execute(Source, "DELETE FROM resources WHERE id=@id", ("id", moved)); }
        Assert.True(Lists(await Dashboards.List(sam, default), shared));
        // With scopes off Sam reads all of them, as today, the card's id included.
        await Scoped.Mode(Source, "off");
        try
        {
            foreach (var id in new[] { made, before, shared }) Assert.True(Lists(await Dashboards.List(sam, default), id));
            Assert.Equal(made, (Guid)Card((await Dashboards.Get(sam, shared, null, false, default))!)["dashboard"]!);
            var read = await Insights.List(sam, null, null, 50, default);
            Assert.True(Lists(read, insight) && Lists(read, beforeInsight));
        }
        finally { await Scoped.Mode(Source, "on"); }
    }

    // Where a new derived record lands (memory.md section 5): the narrowest scope of what it cites, a person's star that
    // a source share lets Family read narrowing nothing; never two people's; a log it reads or writes the saver's own or
    // the household's, and a private dashboard's log writes its owner's; an insight citing no star, and a run of no
    // insight, in the saver's own scope.
    [PostgresFact] public async Task A_new_derived_record_lands_in_the_narrowest_scope_it_cites()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"];
        var samMemory = Corpus.Memories.Where(m => m.Scope == "personal:sam").Select(m => Star(m.Id)).OfType<string>().First();
        var heart = Corpus.Measurements.Where(m => m.Subject == "alex").Select(m => Star(m.Id)).OfType<string>().First();
        await Scoped.Execute(Source, "INSERT INTO source_shares(person_id,provider) VALUES('alex',@p)", ("p", CsvAdapter.Provider));
        Guid household;
        try
        {
            DashboardFeed[] feeds = [new("heart", "data", heart), new("plot", "contents", samMemory)];
            var (mixed, _) = await Dashboards.Save(sam, null, null, Draft("DEMO · Shared heart and Sam's plot", feeds), Named(feeds), "First.", default);
            Assert.Equal("sam", await Subject("dashboards", mixed));
            (household, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Shared heart", [feeds[0]]), Named([feeds[0]]), "First.", default);
            Assert.Equal("household", await Subject("dashboards", household));
            Assert.True(Lists(await Dashboards.List(sam, default), household));
        }
        finally { await Scoped.Execute(Source, "DELETE FROM source_shares WHERE person_id='alex'"); }
        // The share revoked, the household dashboard reads Alex's private data: Alex's alone now.
        Assert.False(Lists(await Dashboards.List(sam, default), household));
        Assert.False(Lists(await Dashboards.List(fixture.Readers["app"], default), household));
        Assert.True(Lists(await Dashboards.List(alex, default), household));
        // Two people's private records, which no reader can cite through the tools, are refused all the same.
        Assert.Equal("22023", await Scoped.Refused(() => Scoped.Scalar(Source,
            """SELECT derived_subject(@p,'[{"subject":"alex"},{"subject":"sam"}]'::jsonb,NULL)""", ("p", alex))));
        Assert.Equal("42501", await Scoped.Refused(() => Scoped.Scalar(Source, """SELECT derived_subject(@p,'[{"subject":"alex"}]'::jsonb,NULL)""", ("p", sam))));
        // Logs: one that names no subject is the saver's own; another person's is refused, and so is a household log
        // write on a private dashboard.
        var mine = Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] about = [new("about", "contents", mine), new("runs", "log", null!, Log: "runs")];
        var (logged, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Alex's runs", about, [new("add", "log", Log: "runs")]), Named(about), "First.", default);
        using (var saved = JsonDocument.Parse((await Dashboards.Get(alex, logged, null, true, default))!))
        {
            Assert.Equal("alex", saved.RootElement.GetProperty("feeds")[1].GetProperty("subject").GetString());
            Assert.Equal("alex", saved.RootElement.GetProperty("writes")[0].GetProperty("subject").GetString());
        }
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(alex, null, null,
            Draft("DEMO · Sam's log", about, [new("add", "log", Log: "runs", Subject: "sam")]), Named(about), "First.", default)));
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(alex, null, null,
            Draft("DEMO · A household log", about, [new("add", "log", Log: "runs", Subject: "household")]), Named(about), "First.", default)));
        // A household log on a Family dashboard is anyone's in Family; with no person, the log is the household's.
        var home = Corpus.Memories.Where(m => m.Scope == "family").Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] chores = [new("about", "contents", home), new("done", "log", null!, Log: "chores")];
        var (choresBoard, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Chores", chores, [new("add", "log", Log: "chores")]), Named(chores), "First.", default);
        using (var saved = JsonDocument.Parse((await Dashboards.Get(family, choresBoard, null, true, default))!))
            Assert.Equal("household", saved.RootElement.GetProperty("writes")[0].GetProperty("subject").GetString());
        // An insight citing no star, and a run of no insight, are their saver's own; the household's for no person.
        Assert.Equal("alex", await Subject("insights", await Insights.Save(alex, Insight("DEMO · Nothing cited", []), default)));
        Assert.Equal("household", await Subject("insights", await Insights.Save(family, Insight("DEMO · Nothing cited either", []), default)));
        Assert.Equal("alex", await Subject("insight_runs", await Insights.Log(alex, InsightDraft.DefaultLabel, "quiet", null, null, default)));
        // A star the saver can't read is never cited, whatever the engine is handed.
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(sam, null, null, Draft("DEMO · Not Sam's", Of(mine)), Named(Of(mine)), "First.", default)));
        Assert.Equal("22023", await Scoped.Refused(() => Insights.Save(sam, Insight("DEMO · Not Sam's either", [mine]), default)));
        static DashboardFeed[] Of(string star) => [new("about", "contents", star)];
        // With scopes off, every one lands in the household and the logs are the first person's, as today.
        await Scoped.Mode(Source, "off");
        try
        {
            var (off, _) = await Dashboards.Save(sam, null, null, Draft("DEMO · Off", about, [new("add", "log", Log: "runs")]), Named(about), "First.", default);
            Assert.Equal("household", await Subject("dashboards", off));
            using var saved = JsonDocument.Parse((await Dashboards.Get(sam, off, null, true, default))!);
            Assert.Equal(DashboardWrite.DefaultSubject, saved.RootElement.GetProperty("writes")[0].GetProperty("subject").GetString());
            Assert.Equal("household", await Subject("insights", await Insights.Save(alex, Insight("DEMO · Off insight", [mine]), default)));
            Assert.Equal("household", await Subject("insight_runs", await Insights.Log(alex, InsightDraft.DefaultLabel, "quiet", null, null, default)));
        }
        finally { await Scoped.Mode(Source, "on"); }
    }

    // An engine of the release before calls each function as it did (the arguments 027 adds are last, with defaults),
    // and with scopes off is answered as it was: every dashboard, insight, run, pin and corner, Alex's own included.
    [PostgresFact] public async Task The_release_before_calls_every_function_as_it_did()
    {
        var alex = fixture.Readers["alex"]; var app = fixture.Readers["app"]; var sam = fixture.Readers["sam"];
        var mine = Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] feeds = [new("about", "contents", mine)];
        var (id, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Called as before", feeds, [new("add", "log", Log: "before")]), Named(feeds), "First.", default);
        var insight = await Insights.Save(alex, Insight("DEMO · Read as before", [mine]), default);
        // Two of Alex's own insights, private, saved while scopes were on: with them off again (rhythm scopes off allows
        // it while no memory is private), superseding either refuses no one, the household's client or her own.
        var replaced = await Insights.Save(alex, Insight("DEMO · Replaced as before", []), default);
        var hers = await Insights.Save(alex, Insight("DEMO · Replaced by her", []), default);
        Assert.Equal(("alex", "alex"), (await Subject("insights", replaced), await Subject("insights", hers)));
        await Scoped.Mode(Source, "off");
        try
        {
            string? Text(object? value) => value as string;
            async Task<string?> Old(string sql, params (string, object?)[] args) => Text(await Scoped.Scalar(Source, sql, args));
            Assert.Contains(id.ToString(), await Old("SELECT read_dashboards(@p,NULL,NULL,false)::text", ("p", sam)));
            Assert.NotNull(await Old("SELECT read_dashboards(@p,@d,NULL,true)::text", ("p", sam), ("d", id)));
            Assert.Contains(insight.ToString(), await Old("SELECT read_insights(@p,NULL,NULL,50)::text", ("p", sam)));
            Assert.NotNull(await Old("SELECT insight_feedback(@p,@i,'explore',NULL)::text", ("p", app), ("i", insight)));
            Assert.NotNull(await Old("SELECT insight_feedback(@p,@i,'interesting',NULL,'DEMO · noted')::text", ("p", app), ("i", insight)));
            Assert.NotNull(await Old("SELECT log_insight_run(@p,'harness-daily','saved',NULL,@i)::text", ("p", sam), ("i", insight)));
            Assert.NotNull(await Old("SELECT read_insight_runs(@p,14)::text", ("p", sam)));
            Assert.NotNull(await Old("SELECT save_insight(@p,@i::jsonb)::text", ("p", sam),
                ("i", JsonSerializer.Serialize(Insight("DEMO · Saved as before", [mine]), InsightDraft.Json))));
            var instead = Guid.Parse((await Old("SELECT save_insight(@p,@i::jsonb)::text", ("p", fixture.Readers["family"]),
                ("i", JsonSerializer.Serialize(Insight("DEMO · Superseded as before", [], replaced), InsightDraft.Json))))!);
            var hersInstead = await Insights.Save(alex, Insight("DEMO · Superseded by her", [], hers), default);
            Assert.Equal(("household", "household"), (await Subject("insights", instead), await Subject("insights", hersInstead)));
            Assert.Equal(2L, await Scoped.Scalar(Source, "SELECT count(*) FROM insights WHERE id IN (@a,@b) AND status='superseded'", ("a", replaced), ("b", hers)));
            Assert.Contains(mine, await Old("SELECT set_sky_pin(@p,@s,true)::text", ("p", app), ("s", mine)));
            Assert.Contains(mine, await Old("SELECT read_sky_pins(@p)::text", ("p", sam)));
            Assert.DoesNotContain(mine, await Old("SELECT set_sky_pin(@p,@s,false)::text", ("p", app), ("s", mine)));
            Assert.Contains(id.ToString(), await Old("SELECT set_ambient_card(@p,'training',@d)::text", ("p", app), ("d", id)));
            Assert.Contains(id.ToString(), await Old("SELECT read_ambient_cards(@p)::text", ("p", sam)));
            await Old("SELECT set_ambient_card(@p,'training',NULL)::text", ("p", app));
            Assert.NotNull(await Old("SELECT allow_dashboard_edits(@p,@d,1,true)::text", ("p", app), ("d", id)));
            Assert.NotNull(await Old("SELECT begin_dashboard_write(@p,@d,1,'add')::text", ("p", app), ("d", id)));
            var entry = new JsonObject { ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"), ["note"] = "DEMO · as before" }.ToJsonString();
            var written = JsonNode.Parse((await Old("SELECT write_dashboard_log(@p,@d,1,'add',@e::jsonb)::text", ("p", app), ("d", id), ("e", entry)))!)!;
            Assert.Contains((string)written["id"]!, await Old("SELECT read_dashboard_writes(@p,@d,NULL,50)::text", ("p", sam), ("d", id)));
            Assert.NotNull(await Old("SELECT undo_dashboard_write(@p,@w)::text", ("p", app), ("w", Guid.Parse((string)written["id"]!))));
            var version = JsonSerializer.Serialize(new { note = "Second.", title = "DEMO · Called as before", html = "<p>DEMO</p>", feeds, stars = Named(feeds) },
                DashboardDraft.Json);
            Assert.NotNull(await Old("SELECT save_dashboard(@p,@d,NULL,@v::jsonb)::text", ("p", sam), ("d", id), ("v", version)));
            Assert.NotNull(await Old("SELECT save_dashboard_layout(@p,@d,2,0,@l::jsonb,'Arranged.')::text", ("p", app), ("d", id),
                ("l", DashboardLayout.PageAlone.ToJson())));
            Assert.NotNull(await Old("SELECT delete_dashboard(@p,@d,'DEMO · Called as before')::text", ("p", sam), ("d", id)));
        }
        finally { await Scoped.Mode(Source, "on"); }
    }

    // memory.md section 5's author rules on dashboards: a Family dashboard a person's client made is changed by that
    // person or an admin, arranged (moved, resized, refocused) by any reader, and written from by its author's viewer;
    // one no person made by any reader of Family, as every dashboard today. A write is served to readers of its dashboard
    // and of its own subject, and a log entry is the writer's own or the household's.
    [PostgresFact] public async Task The_author_rule_guards_changes_and_a_write_is_served_to_readers_of_its_subject()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"];
        var alexViewer = await ViewerOf("alex"); var samViewer = await ViewerOf("sam"); var app = fixture.Readers["app"];
        var home = Corpus.Memories.Where(m => m.Scope == "family").Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] feeds = [new("about", "contents", home)];
        DashboardWrite[] writes = [new("add", "log", Log: "garden", Subject: "household")];
        var layout = new DashboardLayout([new("page", "page", 0, 0, 8, 12)]);
        var (alexs, first) = await Dashboards.Save(alex, null, null, Draft("DEMO · Garden by Alex", feeds, writes), Named(feeds), "First.", default, layout);
        Assert.Equal("household", await Subject("dashboards", alexs));
        Assert.True(Lists(await Dashboards.List(sam, default), alexs));
        // Sam reads it but doesn't change it: no next version, restore, parts or delete, edits or writes.
        Assert.Equal("42501", await Scoped.Refused(() => Dashboards.Save(sam, alexs, null, Draft("DEMO · Garden by Sam", feeds, writes), Named(feeds), "Next.", default)));
        Assert.Equal("42501", await Scoped.Refused(() => Dashboards.Save(sam, alexs, 1, null, [], "Back.", default)));
        Assert.Equal("42501", await Scoped.Refused(() => Dashboards.Delete(sam, alexs, "DEMO · Garden by Alex", default)));
        var composed = new DashboardLayout([new("page", "page", 0, 0, 8, 12), new("glucose", "glucose", 8, 0, 4, 4)]);
        Assert.Equal("42501", await Scoped.Refused(() => Dashboards.SaveLayout(sam, alexs, first, 0, composed, "Added a tile.", default)));
        foreach (var other in new[] { samViewer, app })
            Assert.Equal("42501", await Scoped.Refused(() => Dashboards.AllowEdits(other, alexs, first, true, default)));
        // Arranging is presentation: any reader moves its parts.
        var moved = new DashboardLayout([new("page", "page", 2, 0, 8, 12)]);
        Assert.True((bool)(await Dashboards.SaveLayout(samViewer, alexs, first, 0, moved, "Moved the page.", default))["saved"]!);
        Assert.True((bool)(await Dashboards.SaveLayout(sam, alexs, first, 1, layout, "Moved it back.", default))["saved"]!);
        // Alex allows edits and writes from her viewer; Sam's viewer and the household view can't.
        await Dashboards.AllowEdits(alexViewer, alexs, first, true, default);
        var entry = new JsonObject { ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"), ["check"] = "watering" };
        var written = JsonNode.Parse(await Dashboards.Log(alexViewer, alexs, first, "add", entry, default))!;
        foreach (var other in new[] { samViewer, app })
            Assert.Equal("42501", await Scoped.Refused(() => Dashboards.Log(other, alexs, first, "add", entry, default)));
        Assert.Contains((string)written["id"]!, await Dashboards.Writes(sam, alexs, null, null, default));
        // Made Sam an admin, he changes it; that taken away, he doesn't.
        await Scoped.Execute(Source, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
        try { await Dashboards.Save(sam, alexs, null, Draft("DEMO · Garden by Alex", feeds, writes), Named(feeds), "An admin's version.", default); }
        finally { await Scoped.Execute(Source, "UPDATE memberships SET role='member' WHERE person_id='sam'"); }
        Assert.Equal("42501", await Scoped.Refused(() => Dashboards.Save(sam, alexs, null, Draft("DEMO · Garden by Sam", feeds, writes), Named(feeds), "Next.", default)));
        // A Family dashboard no person made is any Family reader's to change, as every dashboard is today.
        var (households, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Garden by nobody", feeds, writes), Named(feeds), "First.", default);
        await Dashboards.Save(sam, households, null, Draft("DEMO · Garden by nobody", feeds, writes), Named(feeds), "Sam's version.", default);
        await Dashboards.AllowEdits(app, households, 2, true, default);
        await Dashboards.Log(samViewer, households, 2, "add", entry, default);
        await Dashboards.Delete(alex, households, "DEMO · Garden by nobody", default);
        // A Family dashboard made before the switch whose log write names Alex (a log write's default then): its page
        // draws on her log, so it is her readers' alone (memory.md section 8). Her viewer reads it and writes to it;
        // to Sam and the household view it is unknown, and her write is served to her alone.
        Guid before;
        await Scoped.Mode(Source, "off");
        try
        {
            (before, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Before", feeds, [new("add", "log", Log: "garden")]), Named(feeds), "First.", default);
            await Dashboards.AllowEdits(app, before, 1, true, default);
        }
        finally { await Scoped.Mode(Source, "on"); }
        foreach (var other in new[] { sam, app })
        {
            Assert.False(Lists(await Dashboards.List(other, default), before));
            Assert.Null(await Dashboards.Get(other, before, null, false, default));
        }
        Assert.True(Lists(await Dashboards.List(alex, default), before));
        Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.Log(samViewer, before, 1, "add", entry, default)));
        Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.Log(app, before, 1, "add", entry, default)));
        var hers = (string)JsonNode.Parse(await Dashboards.Log(alexViewer, before, 1, "add", entry, default))!["id"]!;
        Assert.Equal("alex", await Subject("dashboard_writes", Guid.Parse(hers)));
        Assert.Contains(hers, await Dashboards.Writes(alex, before, null, null, default));
        Assert.DoesNotContain(hers, await Dashboards.Writes(sam, before, null, null, default));
        Assert.Null(await Dashboards.Writes(sam, null, Guid.Parse(hers), null, default));
        Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.MarkUndone(samViewer, Guid.Parse(hers), default)));
        // The household's corners show a Family dashboard, never Alex's own, and not one the setter can't read; her
        // own corners are hers (Corner_cards_are_each_persons_own_and_the_household_views_stay_the_households).
        var (private1, _) = await Dashboards.Save(alex, null, null,
            Draft("DEMO · Alex's corner", [new("about", "contents", Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First())]),
            [new(Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First(), "DEMO")], "First.", default);
        Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.SetCard(app, "training", private1, default)));
        await Dashboards.SetCard(app, "training", alexs, default);
        try { Assert.Equal(alexs.ToString(), (string?)JsonNode.Parse(await Dashboards.Cards(sam, default))!["training"]); }
        finally { await Dashboards.SetCard(app, "training", null, default); }
        // With scopes off the household view changes every one of them, as today.
        await Scoped.Mode(Source, "off");
        try
        {
            await Dashboards.AllowEdits(app, alexs, first, false, default);
            await Dashboards.Save(sam, alexs, null, Draft("DEMO · Garden by Sam", feeds, writes), Named(feeds), "Sam's version, scopes off.", default);
            await Dashboards.SetCard(app, "training", private1, default);
            await Dashboards.SetCard(app, "training", null, default);
            Assert.Contains(hers, await Dashboards.Writes(sam, before, null, null, default));
        }
        finally { await Scoped.Mode(Source, "on"); }
    }
    // Corner cards per person (SB-P1-20, migration 030; memory.md section 5): with scopes on a viewer session's choice is its
    // person's own, a Family dashboard or one of theirs, or the stat card; each reader is shown its own person's choice
    // where it made one, else the household's, and nobody else is told it, by the corners, a dashboard's corners or a
    // deletion's count. The household view's choices are the household's, as before. With scopes off every corner is the
    // household's: a choice from a session is too, and a person's own is not read.
    [PostgresFact] public async Task Corner_cards_are_each_persons_own_and_the_household_views_stay_the_households()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"]; var app = fixture.Readers["app"];
        var alexViewer = await ViewerOf("alex"); var samViewer = await ViewerOf("sam");
        string StarOf(string scope) => Corpus.Memories.Where(m => m.Scope == scope).Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] Feeds(string scope) => [new("about", "contents", StarOf(scope))];
        var (shared, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Corner for everyone", Feeds("family")), Named(Feeds("family")), "First.", default);
        var (other, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Another corner", Feeds("family")), Named(Feeds("family")), "First.", default);
        var (hers, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Alex's own corner", Feeds("personal:alex")), Named(Feeds("personal:alex")), "First.", default);
        var (his, _) = await Dashboards.Save(sam, null, null, Draft("DEMO · Sam's own corner", Feeds("personal:sam")), Named(Feeds("personal:sam")), "First.", default);
        Assert.Equal(("alex", "sam"), (await Subject("dashboards", hers), await Subject("dashboards", his)));
        static JsonObject Cards(string json) => JsonNode.Parse(json)!.AsObject();
        async Task<string[]> Corners(Guid reader, Guid id) =>
            [.. JsonNode.Parse((await Dashboards.Get(reader, id, null, false, default))!)!["corners"]!.AsArray().Select(c => (string)c!)];
        async Task<string?> Row(string corner, string subject) => (await Scoped.Scalar(Source,
            "SELECT coalesce(dashboard_id::text,'stat card') FROM ambient_cards WHERE corner=@c AND subject_id=@s", ("c", corner), ("s", subject))) as string;
        try
        {
            foreach (var corner in new[] { "main", "training" }) await Dashboards.SetCard(app, corner, null, default);
            // The household view's choice is the household's.
            await Dashboards.SetCard(app, "main", shared, default);
            Assert.Equal(shared.ToString(), await Row("main", "household"));
            // Alex's session chooses her own: her private dashboard, the stat card where the household has a card, and a
            // Family dashboard of her own choosing; each is her row, and the household's stays as it was.
            Assert.Equal(hers.ToString(), (string?)Cards(await Dashboards.SetCard(alexViewer, "training", hers, default))["training"]);
            Assert.False(Cards(await Dashboards.SetCard(alexViewer, "main", null, default)).ContainsKey("main"));
            Assert.Equal(("stat card", shared.ToString()), (await Row("main", "alex"), await Row("main", "household")));
            Assert.Equal(other.ToString(), (string?)Cards(await Dashboards.SetCard(alexViewer, "main", other, default))["main"]);
            Assert.Equal((other.ToString(), shared.ToString()), (await Row("main", "alex"), await Row("main", "household")));
            Assert.Equal(hers.ToString(), await Row("training", "alex"));
            Assert.Null(await Row("training", "household"));
            // Sam's dashboard is unknown to her corners, as it is to her.
            Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.SetCard(alexViewer, "recovery", his, default)));
            // Alex's logins are shown her choices; everyone else the household's, never hers.
            foreach (var reader in new[] { alex, alexViewer })
            {
                var cards = Cards(await Dashboards.Cards(reader, default));
                Assert.Equal((other.ToString(), hers.ToString()), ((string?)cards["main"], (string?)cards["training"]));
            }
            foreach (var reader in new[] { sam, samViewer, family, app })
            {
                var cards = Cards(await Dashboards.Cards(reader, default));
                Assert.Equal(shared.ToString(), (string?)cards["main"]);
                Assert.False(cards.ContainsKey("training"));
            }
            // A dashboard's corners are those it shows in for that reader.
            Assert.Equal(["main"], await Corners(sam, shared));
            Assert.Empty(await Corners(alex, shared));
            Assert.Equal(["main"], await Corners(alexViewer, other));
            Assert.Empty(await Corners(sam, other));
            Assert.Equal(["training"], await Corners(alex, hers));
            // The household view's next choice changes the household's corner, not hers.
            await Dashboards.SetCard(app, "main", null, default);
            Assert.Equal(other.ToString(), (string?)Cards(await Dashboards.Cards(alexViewer, default))["main"]);
            Assert.False(Cards(await Dashboards.Cards(sam, default)).ContainsKey("main"));
            await Dashboards.SetCard(app, "main", shared, default);
            // Sam deletes the Family dashboard only Alex's corner shows: the count tells him nothing of hers, and her corner
            // goes back to the household's.
            Assert.Equal(0, (int)JsonNode.Parse(await Dashboards.Delete(sam, other, "DEMO · Another corner", default))!["corners"]!);
            Assert.Equal(shared.ToString(), (string?)Cards(await Dashboards.Cards(alexViewer, default))["main"]);
            Assert.Equal(["main"], await Corners(alex, shared));

            // Scopes off: every corner is the household's. Alex's own are not read, and her session's choice is the household's.
            await Scoped.Mode(Source, "off");
            try
            {
                foreach (var reader in new[] { alex, alexViewer, sam, app })
                {
                    var cards = Cards(await Dashboards.Cards(reader, default));
                    Assert.Equal(shared.ToString(), (string?)cards["main"]);
                    Assert.False(cards.ContainsKey("training"));
                }
                Assert.Empty(await Corners(alex, hers));
                await Dashboards.SetCard(alexViewer, "training", shared, default);
                Assert.Equal(shared.ToString(), await Row("training", "household"));
                Assert.Equal(shared.ToString(), (string?)Cards(await Dashboards.Cards(sam, default))["training"]);
                await Dashboards.SetCard(alexViewer, "training", null, default);
                Assert.Null(await Row("training", "household"));
                Assert.Equal(hers.ToString(), await Row("training", "alex"));
            }
            finally { await Scoped.Mode(Source, "on"); }
            // Back on, her choices are hers again.
            Assert.Equal(hers.ToString(), (string?)Cards(await Dashboards.Cards(alex, default))["training"]);
        }
        finally
        {
            await Scoped.Execute(Source, "DELETE FROM ambient_cards WHERE subject_id<>'household'");
            await Scoped.Execute(Source, "DELETE FROM dashboards WHERE id=ANY(@d)", ("d", new[] { shared, other, hers, his }));
        }
    }

    // A person's own corner choice is dropped again with the nil uuid (migration 032; SB-P1-20's open point, D-139), so a
    // screen's Undo of corner_card puts a session's corner back to following the household's, and the corners tell a
    // reader bound to a person which of them are its own person's choice (`own`, the stat card included). From the
    // household view, and from every login with scopes off, the nil uuid is an unknown dashboard as before, and no answer
    // says `own`.
    [PostgresFact] public async Task A_persons_own_corner_choice_is_dropped_again_and_the_corners_say_which_are_their_own()
    {
        var alex = fixture.Readers["alex"]; var family = fixture.Readers["family"]; var app = fixture.Readers["app"];
        var alexViewer = await ViewerOf("alex"); var samViewer = await ViewerOf("sam");
        string StarOf(string scope) => Corpus.Memories.Where(m => m.Scope == scope).Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] Feeds(string scope) => [new("about", "contents", StarOf(scope))];
        var (shared, _) = await Dashboards.Save(family, null, null, Draft("DEMO · The household's corner", Feeds("family")), Named(Feeds("family")), "First.", default);
        var (hers, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Alex's corner to drop", Feeds("personal:alex")), Named(Feeds("personal:alex")), "First.", default);
        var none = Guid.Empty;
        static JsonObject Cards(string json) => JsonNode.Parse(json)!.AsObject();
        // The corners a reader is told are its own; a reader told none has `own` empty, one never told it has no `own`.
        static string[] Own(JsonObject cards) => [.. Assert.IsType<JsonArray>(cards["own"]).Select(c => (string)c!)];
        static bool Told(string json) => Cards(json).ContainsKey("own");
        async Task<string?> Row(string corner, string subject) => (await Scoped.Scalar(Source,
            "SELECT coalesce(dashboard_id::text,'stat card') FROM ambient_cards WHERE corner=@c AND subject_id=@s", ("c", corner), ("s", subject))) as string;
        // The app's own call reaches the function: the nil uuid is a dashboard id to the endpoint's check.
        Assert.True(AppEndpoint.Allowed(JsonDocument.Parse("""{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"app_dashboards","arguments":{"corner":"main","dashboard":"00000000-0000-0000-0000-000000000000"}}}""").RootElement));
        try
        {
            foreach (var corner in new[] { "main", "recovery", "training" }) await Dashboards.SetCard(app, corner, null, default);
            await Dashboards.SetCard(app, "main", shared, default);
            // No choice of her own yet: her session follows the household's and is told none is hers.
            var cards = Cards(await Dashboards.Cards(alexViewer, default));
            Assert.Equal(shared.ToString(), (string?)cards["main"]);
            Assert.Empty(Own(cards));
            // Her own: a dashboard in one corner, the stat card in another.
            await Dashboards.SetCard(alexViewer, "training", hers, default);
            cards = Cards(await Dashboards.SetCard(alexViewer, "main", null, default));
            Assert.False(cards.ContainsKey("main"));
            Assert.Equal(["main", "training"], Own(cards));
            Schemas.Valid(Schemas.Folder("mcp"), "app_dashboards.output.schema.json", new JsonObject { ["cards"] = cards.DeepClone() });
            // `own` is sorted by name, as the contract says, not in the screen's corner order (main, recovery, training,
            // changed): with `changed` hers too, it comes first. Dropped again, the rest is as it was.
            Assert.Equal(["changed", "main", "training"], Own(Cards(await Dashboards.SetCard(alexViewer, "changed", hers, default))));
            Assert.Equal(["main", "training"], Own(Cards(await Dashboards.SetCard(alexViewer, "changed", none, default))));
            Assert.Null(await Row("changed", "alex"));
            // Dropped: the corner follows the household's again, and her other choice stays hers.
            cards = Cards(await Dashboards.SetCard(alexViewer, "main", none, default));
            Assert.Equal((shared.ToString(), hers.ToString()), ((string?)cards["main"], (string?)cards["training"]));
            Assert.Equal(["training"], Own(cards));
            Assert.Null(await Row("main", "alex"));
            Assert.Equal(shared.ToString(), await Row("main", "household"));
            // Dropping a choice she never made changes nothing.
            Assert.Equal(["training"], Own(Cards(await Dashboards.SetCard(alexViewer, "recovery", none, default))));
            Assert.Null(await Row("recovery", "alex"));
            // Her own client is told her own; Sam's session only his (none); a login with no person is never told `own`.
            Assert.Equal(["training"], Own(Cards(await Dashboards.Cards(alex, default))));
            cards = Cards(await Dashboards.Cards(samViewer, default));
            Assert.Empty(Own(cards));
            Assert.False(cards.ContainsKey("training"));
            foreach (var reader in new[] { family, app }) Assert.False(Told(await Dashboards.Cards(reader, default)));
            // From the household view the nil uuid is an unknown dashboard, as it was, and changes nothing.
            Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.SetCard(app, "main", none, default)));
            Assert.Equal(shared.ToString(), await Row("main", "household"));

            // Scopes off: every login's nil uuid is an unknown dashboard, her choice stays, and no answer says `own`.
            await Scoped.Mode(Source, "off");
            try
            {
                Assert.Equal("P0002", await Scoped.Refused(() => Dashboards.SetCard(alexViewer, "training", none, default)));
                Assert.Equal(hers.ToString(), await Row("training", "alex"));
                foreach (var reader in new[] { alex, alexViewer, samViewer, app }) Assert.False(Told(await Dashboards.Cards(reader, default)));
            }
            finally { await Scoped.Mode(Source, "on"); }

            // Undo's drop, then the household's next choice: her corner shows it, as it did before she chose.
            await Dashboards.SetCard(alexViewer, "training", none, default);
            await Dashboards.SetCard(app, "training", shared, default);
            cards = Cards(await Dashboards.Cards(alexViewer, default));
            Assert.Equal(shared.ToString(), (string?)cards["training"]);
            Assert.Empty(Own(cards));
        }
        finally
        {
            await Scoped.Execute(Source, "DELETE FROM ambient_cards WHERE subject_id<>'household'");
            await Scoped.Execute(Source, "DELETE FROM dashboards WHERE id=ANY(@d)", ("d", new[] { shared, hers }));
        }
    }

    // What an insight's sources name narrows it at each read, as its stars do (memory.md section 8): a reader who can't
    // read a record or a star one of its sources names now is not served it. One saved with scopes on is checked by what
    // its sources named at save (a locator naming nothing its saver read landed it in the saver's own scope and is not
    // checked again); one saved with scopes off, as every insight 027 found was, by its every locator, so one naming
    // nothing the engine can tell is unknown to everyone. With scopes off every reader is served every one, as today.
    [PostgresFact] public async Task An_insight_narrows_when_what_its_sources_name_does()
    {
        var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"]; var app = fixture.Readers["app"];
        var home = Corpus.Memories.Where(m => m.Scope == "family").Select(m => Star(m.Id)).OfType<string>().First();
        var alexsStar = Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First();
        var shared = await Scoped.Row(Source, "household", "notes", "shared", "insight-source-family");
        var hers = await Scoped.Row(Source, "alex", "csv", "private", "insight-source-alex");
        static InsightSource Cited(string kind, string locator) => new(kind, "DEMO · a source", null, locator, null, DateTimeOffset.UtcNow.AddMinutes(-1));
        InsightDraft Sourced(string title, params InsightSource[] sources) => Insight(title, [home]) with { Sources = sources };
        var saved = new List<Guid>();
        async Task<Guid> Save(Guid by, InsightDraft draft) { var id = await Insights.Save(by, draft, default); saved.Add(id); return id; }
        async Task<string[]> ReadBy(Guid insight)
        {
            var found = new List<string>();
            foreach (var reader in Readers)
                if (Lists(await Insights.List(fixture.Readers[reader], null, null, 50, default), insight)) found.Add(reader);
            return [.. found];
        }
        string[] everyone = [.. Readers], alexAlone = ["alex"];
        try
        {
            // Saved before the switch by the household's client: each the household's.
            Guid beforeHers, beforeStar, beforeFamily, beforeUpper, beforeNothing;
            await Scoped.Mode(Source, "off");
            try
            {
                beforeHers = await Save(family, Sourced("DEMO · Before, Alex's record", Cited("record", hers.ToString())));
                beforeStar = await Save(family, Sourced("DEMO · Before, Alex's star", Cited("graph", alexsStar)));
                beforeFamily = await Save(family, Sourced("DEMO · Before, Family's", Cited("record", shared.ToString()), Cited("graph", home)));
                beforeUpper = await Save(family, Sourced("DEMO · Before, Family's in capitals", Cited("record", shared.ToString().ToUpperInvariant())));
                beforeNothing = await Save(family, Sourced("DEMO · Before, nothing known", Cited("document", "nowhere:DEMO")));
            }
            finally { await Scoped.Mode(Source, "on"); }
            foreach (var id in saved) Assert.Equal("household", await Subject("insights", id));
            Assert.Equal(alexAlone, await ReadBy(beforeHers));
            Assert.Equal(alexAlone, await ReadBy(beforeStar));
            Assert.Equal(everyone, await ReadBy(beforeFamily));
            Assert.Equal(everyone, await ReadBy(beforeUpper));
            Assert.Empty(await ReadBy(beforeNothing));
            // Unknown to every other use as well: feedback, a run, superseding.
            Assert.Null(await Insights.Record(app, beforeHers, "interesting", null, null, default));
            Assert.Equal("22023", await Scoped.Refused(() => Insights.Log(sam, InsightDraft.DefaultLabel, "saved", null, beforeHers, default)));
            Assert.Equal("22023", await Scoped.Refused(() => Insights.Save(sam, Insight("DEMO · Sam's instead", [home], beforeHers), default)));

            // Saved with scopes on: by what its sources named then. Sam's insight drawing on a Family record is Family's,
            // and the household's naming nothing it reads lands in its own scope, the household's, and stays Family.
            var onFamily = await Save(sam, Sourced("DEMO · Now, Family's", Cited("record", shared.ToString())));
            var onNothing = await Save(family, Sourced("DEMO · Now, nothing known", Cited("document", "nowhere:DEMO")));
            Assert.Equal(("household", "household"), (await Subject("insights", onFamily), await Subject("insights", onNothing)));
            Assert.Equal(new[] { shared.ToString() }, (string[])(await Scoped.Scalar(Source, "SELECT sources_checked FROM insights WHERE id=@id", ("id", onFamily)))!);
            Assert.Equal(everyone, await ReadBy(onFamily));
            Assert.Equal(everyone, await ReadBy(onNothing));
            // The Family record narrows into Alex's private scope: at the next read both insights drawing on it are
            // hers alone, with no pass in between; and Family's again once it is.
            await Scoped.Execute(Source, "UPDATE resources SET subject_id='alex',scope='private' WHERE id=@id", ("id", shared));
            try
            {
                Assert.Equal(alexAlone, await ReadBy(onFamily));
                Assert.Equal(alexAlone, await ReadBy(beforeFamily));
                Assert.Null(await Insights.Record(await ViewerOf("sam"), onFamily, "interesting", null, null, default));
            }
            finally { await Scoped.Execute(Source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", shared)); }
            Assert.Equal(everyone, await ReadBy(onFamily));

            // With scopes off every reader is served every one, as today.
            await Scoped.Mode(Source, "off");
            try { foreach (var id in saved) Assert.Equal(everyone, await ReadBy(id)); }
            finally { await Scoped.Mode(Source, "on"); }
        }
        finally
        {
            await Scoped.Execute(Source, "DELETE FROM insights WHERE id=ANY(@ids)", ("ids", saved.ToArray()));
            await Scoped.Execute(Source, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", new[] { shared, hers }));
        }
    }

    // No one but its author's person takes a Family dashboard or insight private (memory.md section 5): not a member
    // through a next version, a log feed naming no subject or a restore, not an admin, not a superseding insight. An
    // arrangement by a person never refocuses a Family dashboard onto a star it doesn't name (section 8); a private
    // record cited only among an insight's sources narrows it; a log write from a version saved before its dashboard
    // went private is no wider than the dashboard; and the pin limit counts only the pins the pinner is served.
    [PostgresFact] public async Task No_one_but_its_author_takes_a_Family_record_private()
    {
        var alex = fixture.Readers["alex"]; var sam = fixture.Readers["sam"]; var family = fixture.Readers["family"];
        var alexViewer = await ViewerOf("alex"); var samViewer = await ViewerOf("sam"); var app = fixture.Readers["app"];
        var homes = Corpus.Memories.Where(m => m.Scope == "family").Select(m => Star(m.Id)).OfType<string>().Take(2).ToArray();
        var home = homes[0];
        var samsOwn = Corpus.Memories.Where(m => m.Scope == "personal:sam").Select(m => Star(m.Id)).OfType<string>().First();
        var alexsOwn = Corpus.Memories.Where(m => m.Scope == "personal:alex").Select(m => Star(m.Id)).OfType<string>().First();
        DashboardFeed[] feeds = [new("about", "contents", home)];
        DashboardFeed[] his = [new("about", "contents", home), new("mine", "contents", samsOwn)];
        DashboardFeed[] hers = [new("about", "contents", home), new("mine", "contents", alexsOwn)];
        // A Family dashboard no person made: Sam's next version citing his own star, or adding a log feed that names
        // no subject (his own, with scopes on), is refused; it stays Family, for Alex too.
        var (nobodys, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Chores by nobody", feeds), Named(feeds), "First.", default);
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(sam, nobodys, null, Draft("DEMO · Chores", his), Named(his), "Sam's.", default)));
        DashboardFeed[] logged = [new("about", "contents", home), new("done", "log", null!, Log: "chores")];
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(sam, nobodys, null, Draft("DEMO · Chores", logged), Named(feeds), "Sam's log.", default)));
        Assert.Equal("household", await Subject("dashboards", nobodys));
        Assert.True(Lists(await Dashboards.List(alex, default), nobodys));
        // A restore of a version saved before the switch whose log was Alex's (a log's default then) would take it
        // private: refused to Alex too, who did not make it.
        DashboardFeed[] runs = [new("about", "contents", home), new("runs", "log", null!, Log: "runs")];
        Guid before;
        await Scoped.Mode(Source, "off");
        try { (before, _) = await Dashboards.Save(family, null, null, Draft("DEMO · Runs before", runs), Named(feeds), "First.", default); }
        finally { await Scoped.Mode(Source, "on"); }
        // Its log is Alex's, so with scopes on it is her readers' alone (memory.md section 8): the household view can't
        // read it, and Alex, who reads Family, saves its next version without the log.
        Assert.False(Lists(await Dashboards.List(family, default), before));
        await Dashboards.Save(alex, before, null, Draft("DEMO · Runs before", feeds), Named(feeds), "Second.", default);
        Assert.True(Lists(await Dashboards.List(family, default), before));
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(alex, before, 1, null, [], "Back.", default)));
        Assert.Equal("household", await Subject("dashboards", before));
        // A Family dashboard Alex made: Sam, an admin, changes it but does not take it private; Alex herself does.
        var (alexs, _) = await Dashboards.Save(alex, null, null, Draft("DEMO · Garden by Alex", feeds), Named(feeds), "First.", default);
        await Scoped.Execute(Source, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
        try
        {
            Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Save(sam, alexs, null, Draft("DEMO · Garden", his), Named(his), "An admin's.", default)));
            await Dashboards.Save(sam, alexs, null, Draft("DEMO · Garden by Alex", feeds), Named(feeds), "An admin's, still Family.", default);
        }
        finally { await Scoped.Execute(Source, "UPDATE memberships SET role='member' WHERE person_id='sam'"); }
        Assert.Equal("household", await Subject("dashboards", alexs));
        Assert.True(Lists(await Dashboards.List(alex, default), alexs));
        // Arranging never narrows: Sam's viewer can't refocus Alex's Family dashboard's map onto his own star, nor can
        // Alex onto hers, though she made it; Sam's viewer refocuses it onto another Family star, which the engine tells
        // the database is Family's (DashboardStore.SaveLayout passes the focus stars' subjects), and the household view's
        // screen onto one more.
        var map = new DashboardLayout([new("map", "map", 0, 0, 8, 12, new MapFocus(Near: new(home))), new("page", "page", 8, 0, 4, 8)]);
        var (mapped, version) = await Dashboards.Save(alex, null, null, Draft("DEMO · Map by Alex", feeds), Named(feeds), "First.", default, map);
        DashboardLayout Focus(string star) => new([new("map", "map", 0, 0, 8, 12, new MapFocus(Near: new(star))), new("page", "page", 8, 0, 4, 8)]);
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.SaveLayout(samViewer, mapped, version, 0, Focus(samsOwn), "Mine.", default)));
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.SaveLayout(alexViewer, mapped, version, 0, Focus(alexsOwn), "Hers.", default)));
        Assert.True((bool)(await Dashboards.SaveLayout(samViewer, mapped, version, 0, Focus(homes[1]), "Another.", default))["saved"]!);
        Assert.True((bool)(await Dashboards.SaveLayout(app, mapped, version, 1, Focus(home), "Back, from the screen.", default))["saved"]!);
        Assert.Equal("household", await Subject("dashboards", mapped));
        Assert.True(Lists(await Dashboards.List(alex, default), mapped) && Lists(await Dashboards.List(sam, default), mapped));
        // Without the subjects (an engine that passes none), a person refocuses a Family dashboard only onto stars it names.
        async Task<string> Unsubjected(Guid by, int revision, string star)
        {
            await using var cmd = Source.CreateCommand("SELECT save_dashboard_layout(@p,@d,@v,@b,@l::jsonb,'Unsubjected.',@s)::text");
            cmd.Parameters.AddWithValue("p", by); cmd.Parameters.AddWithValue("d", mapped); cmd.Parameters.AddWithValue("v", version);
            cmd.Parameters.AddWithValue("b", revision); cmd.Parameters.AddWithValue("l", Focus(star).ToJson());
            cmd.Parameters.Add(DerivedScopes.Parameter("s", [.. (await fixture.Load.Host.Shown(by, default)).Keys]));
            return (string)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Equal("22023", await Scoped.Refused(() => Unsubjected(samViewer, 2, homes[1])));
        Assert.Contains("\"saved\": true", await Unsubjected(samViewer, 2, home));
        // Alex takes her garden private herself; a household log write declared by its first version, edits still
        // allowed for it, is refused from then on.
        DashboardWrite[] household = [new("add", "log", Log: "garden", Subject: "household")];
        var (garden, first) = await Dashboards.Save(alex, null, null, Draft("DEMO · Alex's garden log", feeds, household), Named(feeds), "First.", default);
        await Dashboards.AllowEdits(alexViewer, garden, first, true, default);
        await Dashboards.Save(alex, garden, null, Draft("DEMO · Alex's garden log", hers), Named(hers), "Alex's own now.", default);
        Assert.Equal("alex", await Subject("dashboards", garden));
        var entry = new JsonObject { ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"), ["check"] = "watering" };
        Assert.Equal("22023", await Scoped.Refused(() => Dashboards.Log(alexViewer, garden, first, "add", entry, default)));
        // Insights: Sam does not supersede Alex's Family insight with a private one; Alex does.
        var shared = await Insights.Save(alex, Insight("DEMO · Garden insight", [home]), default);
        Assert.Equal("household", await Subject("insights", shared));
        Assert.Equal("22023", await Scoped.Refused(() => Insights.Save(sam, Insight("DEMO · Sam's instead", [samsOwn], shared), default)));
        Assert.True(Lists(await Insights.List(family, "new", null, 50, default), shared));
        Assert.Equal("alex", await Subject("insights", await Insights.Save(alex, Insight("DEMO · Alex's instead", [alexsOwn], shared), default)));
        // A private record cited only among the sources narrows an insight: Alex's, never Sam's.
        var record = fixture.Load.ResourceIds.First(r => Corpus.ScopeOf[r.Key] == "personal:alex").Value;
        static InsightSource Cited(string kind, string? locator, string? url = null) =>
            new(kind, "DEMO · a source", url, locator, null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var sourced = await Insights.Save(alex, Insight("DEMO · Sourced", [home]) with { Sources = [Cited("record", record.ToString())] }, default);
        Assert.Equal("alex", await Subject("insights", sourced));
        Assert.False(Lists(await Insights.List(sam, null, null, 50, default), sourced));
        Assert.Equal("household", await Subject("insights", await Insights.Save(alex, Insight("DEMO · Sourced on the map", [home]) with
            { Sources = [Cited("graph", home), Cited("web", null, "https://example.invalid/demo")] }, default)));
        Assert.Equal("sam", await Subject("insights", await Insights.Save(sam, Insight("DEMO · Sourced by name", [home]) with { Sources = [Cited("document", null)] }, default)));
        // Pins: 24 Family pins of stars Sam isn't served don't count against him, and his unpinning leaves them.
        await Scoped.Execute(Source, "INSERT INTO sky_pins(subject_id,graph_id,pinned_by) SELECT 'household','derived-hidden-'||i,@p FROM generate_series(1,24) i", ("p", app));
        try
        {
            Assert.Contains(home, await Dashboards.SetPin(samViewer, home, true, default));
            await Dashboards.SetPin(samViewer, "derived-hidden-1", false, default);
            Assert.Equal(24L, (long)(await Scoped.Scalar(Source, "SELECT count(*) FROM sky_pins WHERE graph_id LIKE 'derived-hidden-%'"))!);
        }
        finally { await Scoped.Execute(Source, "DELETE FROM sky_pins WHERE graph_id LIKE 'derived-hidden-%' OR (graph_id=@s AND pinned_by=@p)", ("s", home), ("p", samViewer)); }
    }
}
