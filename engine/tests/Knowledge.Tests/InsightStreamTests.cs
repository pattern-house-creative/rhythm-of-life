using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// One insight stream (SB-P5-06, migration 066, Core/InsightStream.cs; story 11; D-018, D-031; docs/dev/memory.md sections
// 5, 7.1 and 8): the daily insight reads the pass's candidates and records the ones it declines; what an AI proposed is
// kept apart from what a person confirmed; a snooze ends by the clock; an insight's feedback and reviews are kept; an
// insight drawn from a private memory stays in its scope; a day with no insight says so. Nothing is added beside the
// daily insight: no job, no second place an insight lives. Fictional people only: alex, sam and the household; every
// text is DEMO.
static class StreamCalls
{
    public static readonly MemoryCall Mcp = MemoryLogins.Mcp;

    public static async Task<MemoryToolClient> As(DatabaseFixture fixture, Guid login)
    {
        TestSettings.Use();
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(login)).AddSingleton(fixture.Db)
            .AddSingleton(MemoryToolSettings.On).AddSingleton(new Retrieval(fixture.Db)).AddSingleton(new InsightStore(fixture.Source)).BuildServiceProvider();
        return await MemoryToolClient.Start(services, MemoryToolSettings.On);
    }

    public static InsightDraft Draft(string title, Guid[]? memories = null, Guid? supersedes = null, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        return new("discovery", title, "DEMO · a fictional insight.", "", "", [], "weak", "", [],
            [.. (memories ?? []).Select(m => new InsightSource("memory", "DEMO memory", null, m.ToString(), null, now))], InsightDraft.DefaultLabel, now, now, supersedes);
    }

    // The same insight through the tool, as a client saves it: its id.
    public static async Task<Guid> Save(MemoryToolClient ai, string title, params Guid[] memories)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var r = await ai.Raw("save_insight", new
        {
            kind = "discovery", title, summary = "DEMO · a fictional insight.", whyItMatters = "", nextStep = "", evidenceLevel = "weak", limitations = "", generatedAt = at, sourcesAsOf = at,
            sources = memories.Select(m => new { kind = "memory", title = "DEMO memory", locator = m.ToString(), retrievedAt = at }).ToArray(),
        });
        Assert.True(r.IsError != true, MemoryToolClient.Text(r));
        return Guid.Parse((string)JsonNode.Parse(MemoryToolClient.Text(r))!["id"]!);
    }

    public static Guid Id(JsonNode? item) => Guid.Parse((string)item!["id"]!);
    public static IEnumerable<Guid> Ids(JsonObject answer) => answer["items"]!.AsArray().Select(Id);

    // Every page of one view for a reader: each page's counts agree, and the view's list is as long as its count.
    public static async Task<List<JsonNode>> Walk(MemoryToolClient ai, string view, string? workspace = null)
    {
        var items = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var page = await ai.Ok("list_insight_stream", new { view, n = 2, workspace, cursor });
            Assert.Equal(view, (string)page["view"]!);
            var counts = new[] { "proposed", "older", "confirmed", "snoozed", "closed" }.ToDictionary(k => k, k => (int)page[k]!);
            Assert.Equal(view == "all" ? counts.Values.Sum() : counts[view], (int)page["total"]!);
            items.AddRange(page["items"]!.AsArray()!);
            cursor = (string?)page["next"];
            if (cursor is null) Assert.Equal((int)page["total"]!, items.Count);
        } while (cursor is not null);
        Assert.Equal(items.Count, items.Select(Id).Distinct().Count());
        return items;
    }

    // Each view holds each insight once, and none is confirmed that its review does not call so.
    public static async Task<Dictionary<string, HashSet<Guid>>> Views(MemoryToolClient ai)
    {
        var views = new Dictionary<string, HashSet<Guid>>();
        foreach (var view in new[] { "proposed", "older", "confirmed", "snoozed", "closed" })
        {
            var items = await Walk(ai, view);
            Assert.All(items, i => Assert.Equal(view, (string)i["view"]!));
            if (view == "confirmed") Assert.All(items, i => Assert.Equal("confirmed", (string)i["review"]!));
            if (view is "proposed" or "older") Assert.All(items, i => Assert.Equal("proposed", (string)i["review"]!));
            views[view] = [.. items.Select(Id)];
        }
        var all = (await Walk(ai, "all")).Select(Id).ToHashSet();
        Assert.Equal(all.Count, views.Values.Sum(v => v.Count));
        Assert.All(views.Values, v => Assert.Subset(all, v));
        return views;
    }

    public static Task<object?> Events(NpgsqlDataSource s, Guid insight) => MemoryRows.Scalar(s, """
        SELECT string_agg(e.event||':'||coalesce(e.value,'-')||':'||coalesce(e.note,'-')||':'||coalesce(e.channel,'-'),';' ORDER BY e.id) FROM insight_events e WHERE e.insight_id=@id
        """, ("id", insight));

    public static async Task Clear(NpgsqlDataSource s)
    {
        foreach (var table in new[] { "insight_runs", "insights", "memory_insight_candidates" }) await MemoryRows.Run(s, $"DELETE FROM {table}");
    }
}

// What the stream is before any database: the day's plain words, and the day's start in a zone.
public sealed class InsightStreamShapeTests
{
    [Fact] public void A_day_says_what_it_came_to_in_the_engines_own_words()
    {
        Assert.StartsWith("No insight today", InsightStore.DaySays("quiet"));
        Assert.StartsWith("No insight today", InsightStore.DaySays("blocked"));
        Assert.StartsWith("No insight today", InsightStore.DaySays("none"));
        Assert.Equal("An insight was proposed today.", InsightStore.DaySays("saved"));
        Assert.NotEqual(InsightStore.DaySays("quiet"), InsightStore.DaySays("none"));
        // None covers a saved run naming no insight too, so it does not claim that no run logged.
        Assert.Equal(InsightStore.DayNone, InsightStore.DaySays("none"));
        Assert.DoesNotContain("no run has logged", InsightStore.DayNone);
    }

    // A review's words follow the view it left the insight in: it never says proposed of what the stream shows elsewhere.
    [Theory]
    [InlineData("confirmed", "confirmed", "among the confirmed")]
    [InlineData("confirmed", "snoozed", "among the snoozed until its snooze ends")]
    [InlineData("dismissed", "closed", "leaves what waits")]
    [InlineData("proposed", "proposed", "it is proposed again")]
    [InlineData("proposed", "closed", "stays closed")]
    [InlineData("proposed", "snoozed", "among the snoozed until its snooze ends")]
    public void A_reviews_words_follow_the_view_it_left_the_insight_in(string review, string view, string words)
    {
        Assert.Contains(words, MemoryTools.ReviewSaid(new(Guid.Empty, review, view, null, false)));
        Assert.Equal($"Nothing changed: it was {review} already.", MemoryTools.ReviewSaid(new(Guid.Empty, review, view, null, true)));
    }

    [Fact] public void A_day_starts_at_its_midnight_or_the_first_moment_after_one_the_clocks_skip()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), InsightStore.DayStart(new(2026, 11, 6), TimeZoneInfo.Utc));
        // Clocks that go forward at midnight on 8 March: that day starts at 01:00, an hour past its midnight on the clock.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2100, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 8), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("DEMO", TimeSpan.FromHours(-5), "DEMO", "DEMO", "DEMO", [rule]);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), InsightStore.DayStart(new(2026, 3, 8), zone));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), InsightStore.DayStart(new(2026, 3, 9), zone));
    }

    // The stream adds no job: with memory.tools on the daily turn is exactly the list before it (the staleness, link pruning
    // and insight candidate passes after the four), with it off the four it always had.
    [Fact] public void The_stream_adds_no_job_and_no_second_agent()
    {
        var log = new LoggedLines<Scheduler>();
        Assert.Equal(["ingest-keys", "screens", "memory-trash", "recall-log"], NightlyJobs.Daily(null!, log, TestSettings.Use()).Select(j => j.Id));
        Assert.Equal(["ingest-keys", "screens", "memory-trash", "recall-log", MemoryStaleness.JobId, MemoryLinkPruning.JobId, InsightCandidates.JobId],
            NightlyJobs.Daily(null!, log, MemoryToolSettings.On).Select(j => j.Id));
        TestSettings.Use();
    }
}

// Story 11 whole, through the tools as AI clients reach them, on the corpus's memories of Alex, Sam and Family with
// scopes on: its own database. The pass pairs old and new memories; the daily client reads the pairs, declines those
// that say nothing, writes one insight citing both memories of one, closes that pair and logs the run. The insight is
// proposed, in Alex's scope alone, in the one place insights live; feedback does not confirm it, its own login cannot, and
// a person through another client does; and the history keeps all of it. No memory changes, and a declined pair is never
// offered again.
[Collection(SharedStateCollection.Name)]
public sealed class InsightStreamStoryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    async Task<Dictionary<string, Guid>> Corpus(NpgsqlDataSource s, MemoryLogins who)
    {
        var store = new MemoryStore(s);
        var corpus = ParityCorpus.Load();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var m in corpus.Memories.Where(m => m.Scope is "personal:alex" or "personal:sam" or "family" && InsightSignals.Eligible(corpus, m)))
        {
            var login = m.Scope == "personal:sam" || m.Author == "sam" ? who.Sam : m.Scope == "family" && m.Author is null ? who.House : who.Alex;
            var written = await store.Create(login, new(m.Text, m.Tags, m.Scope == "family" ? "shared" : "private", null, m.Importance, m.Volatility, m.Project), StreamCalls.Mcp, default);
            Assert.Null(written.Hold);
            await Insights.WrittenAt(s, written.Id, m.CreatedAt);
            ids[m.Id] = written.Id;
        }
        return ids;
    }

    [PostgresFact] public async Task Story_11_the_daily_insight_reads_candidates_declines_what_says_nothing_and_proposes_one_in_one_stream()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var who = await MemoryLogins.Make(s, "stream11");
            var ids = await Corpus(s, who);
            Assert.True((await Insights.Pass(s, new MeaningModel())).Counts["candidates added"] > 0);
            var before = await Insights.Memories(s);
            await using var daily = await StreamCalls.As(fixture, who.Alex);
            await using var person = await StreamCalls.As(fixture, await Scoped.Principal(s, "stream11-alex-phone", "harness", "alex"));
            await using var sam = await StreamCalls.As(fixture, who.Sam);
            await using var house = await StreamCalls.As(fixture, who.House);

            // The morning: nothing proposed yet, candidates waiting, and the day says it has no insight so far.
            var first = await daily.Ok("list_insight_stream");
            Assert.Equal((0, 0), ((int)first["proposed"]!, (int)first["total"]!));
            var waiting = (int)first["candidates"]!;
            Assert.Equal(await new MemoryStore(s).InsightCount(who.Alex, null, default), waiting);
            Assert.True(waiting >= 2);
            Assert.Equal(("none", InsightStore.DayNone), ((string)first["day"]!["outcome"]!, (string)first["day"]!["says"]!));

            // It reads the pairs, and declines one with its reason: recorded, kept, gone from what waits.
            var pairs = await Insights.Walk(new MemoryStore(s), who.Alex, "private");
            var sabbatical = pairs.Items.Single(i => (i.Older.Id, i.Newer.Id) == (ids["mem-alex-job-new"], ids["mem-alex-sabbatical"]));
            var other = pairs.Items.First(i => i.Id != sabbatical.Id);
            var declined = await daily.Ok("resolve_insight_candidate", new { id = other.Id, action = "reject", note = " DEMO: together they say nothing new. " });
            Assert.Equal(("rejected", "DEMO: together they say nothing new."), ((string)declined["status"]!, (string)declined["note"]!));
            Assert.Null(declined["insight_id"]);
            Assert.DoesNotContain((Guid)other.Id!, StreamCalls.Ids(await daily.Ok("list_insight_candidates", new { n = 50 })));
            var recorded = await daily.Ok("list_insight_candidates", new { state = "rejected" });
            Assert.Equal(1, (int)recorded["total"]!);
            Assert.Equal(((Guid)other.Id!, "rejected", "DEMO: together they say nothing new."),
                (StreamCalls.Id(recorded["items"]![0]), (string)recorded["items"]![0]!["status"]!, (string)recorded["items"]![0]!["note"]!));
            Assert.Equal(1, await new MemoryStore(s).InsightCount(who.Alex, null, default, "rejected"));

            // It writes one insight from another, citing both memories, closes the pair with it, and logs the run.
            var insight = await StreamCalls.Save(daily, "DEMO · the new job may come too soon for the sabbatical", sabbatical.Older.Id, sabbatical.Newer.Id);
            var used = await daily.Ok("resolve_insight_candidate", new { id = sabbatical.Id, action = "use", insight_id = insight });
            Assert.Equal(("used", insight), ((string)used["status"]!, Guid.Parse((string)used["insight_id"]!)));
            Assert.True((await daily.Raw("log_insight_run", new { outcome = "saved", insightId = insight })).IsError != true);

            // One stream: proposed, in Alex's scope, written from that candidate, in the one place insights live.
            var views = await StreamCalls.Views(daily);
            Assert.Equal([insight], views["proposed"]);
            Assert.Empty(views["confirmed"]);
            var item = (await daily.Ok("list_insight_stream"))["items"]![0]!;
            Assert.Equal(("proposed", "proposed", "private", "alex"), ((string)item["view"]!, (string)item["review"]!, (string)item["scope"]!, (string)item["owner"]!));
            Assert.Equal([(Guid)sabbatical.Id!], item["candidates"]!.AsArray().Select(c => Guid.Parse((string)c!)));
            Assert.Equal([sabbatical.Older.Id.ToString(), sabbatical.Newer.Id.ToString()], item["insight"]!["sources"]!.AsArray().Select(x => (string)x!["locator"]!));
            Assert.Equal(insight, StreamCalls.Id(item["insight"]));
            Assert.Equal(1L, await MemoryRows.Scalar(s, "SELECT count(*) FROM insights"));
            Assert.Contains(insight.ToString(), MemoryToolClient.Text(await daily.Raw("list_insights", new { limit = 5 })));
            var day = (await daily.Ok("list_insight_stream"))["day"]!;
            Assert.Equal(("saved", insight, 1), ((string)day["outcome"]!, Guid.Parse((string)day["insight_id"]!), (int)day["declined"]!));

            // Its scope: Alex's alone. Sam and the household's client neither list it nor know it.
            foreach (var other2 in new[] { sam, house })
            {
                Assert.DoesNotContain(insight, StreamCalls.Ids(await other2.Ok("list_insight_stream", new { view = "all", n = 50 })));
                Assert.Equal("unknown", (string)(await other2.Refused("insight_history", new { id = insight }))["code"]!);
                Assert.Equal("unknown", (string)(await other2.Refused("review_insight", new { id = insight, action = "confirm" }))["code"]!);
                Assert.Equal("none", (string)(await other2.Ok("list_insight_stream"))["day"]!["outcome"]!);
                Assert.DoesNotContain((Guid)other.Id!, StreamCalls.Ids(await other2.Ok("list_insight_candidates", new { state = "rejected" })));
            }

            // A reaction is no confirmation: Alex's viewer finds it interesting, and it stays proposed.
            var viewer = await Scoped.Principal(s, "stream11-viewer", "app", "alex");
            Assert.Contains("\"status\": \"saved\"", (await new InsightStore(s).Record(viewer, insight, "interesting", null, "DEMO: worth a look", default, keep: true))!);
            Assert.Equal([insight], (await StreamCalls.Views(daily))["proposed"]);

            // Its own login cannot call it confirmed, nor can the client that wrote it dismiss it; nothing changes.
            foreach (var action in new[] { "confirm", "dismiss", "reopen" })
                Assert.Equal("forbidden", (string)(await daily.Refused("review_insight", new { id = insight, action }))["code"]!);
            Assert.Empty((await StreamCalls.Views(daily))["confirmed"]);
            Assert.Equal("feedback:interesting:DEMO: worth a look:app", await StreamCalls.Events(s, insight));

            // Alex, through another client, says it holds: only now is it confirmed, apart from what is proposed.
            var confirmed = await person.Ok("review_insight", new { id = insight, action = "confirm", note = "DEMO: Alex says this holds." });
            Assert.Equal(("confirm", "confirmed", false), ((string)confirmed["action"]!, (string)confirmed["review"]!, (bool)confirmed["unchanged"]!));
            Assert.True((bool)(await person.Ok("review_insight", new { id = insight, action = "confirm" }))["unchanged"]!);
            views = await StreamCalls.Views(daily);
            Assert.Empty(views["proposed"]);
            Assert.Equal([insight], views["confirmed"]);

            // The history keeps it, newest first, with who and through what.
            var history = await daily.Ok("insight_history", new { id = insight });
            Assert.Equal(("confirmed", 2), ((string)history["review"]!, (int)history["total"]!));
            Assert.Equal(("confirmed", "alex", "mcp", "DEMO client", "DEMO: Alex says this holds."), ((string)history["items"]![0]!["event"]!, (string)history["items"]![0]!["person"]!,
                (string)history["items"]![0]!["channel"]!, (string)history["items"]![0]!["client"]!, (string)history["items"]![0]!["note"]!));
            Assert.Equal(("feedback", "interesting", "alex", "app"), ((string)history["items"]![1]!["event"]!, (string)history["items"]![1]!["value"]!,
                (string)history["items"]![1]!["person"]!, (string)history["items"]![1]!["channel"]!));

            // No memory changed, and the next night adds nothing: the declined pair stays declined and off every list.
            Assert.Equal(before, await Insights.Memories(s));
            var candidates = (await Insights.Rows(s)).Count;
            var again = await Insights.Pass(s, new MeaningModel());
            Assert.Equal(0L, again.Counts["candidates added"]);
            Assert.Equal(candidates, (await Insights.Rows(s)).Count);
            Assert.Equal("rejected", (await Insights.Rows(s)).Single(r => (r.Older, r.Newer) == (other.Older.Id, other.Newer.Id)).Status);
            Assert.DoesNotContain((Guid)other.Id!, StreamCalls.Ids(await daily.Ok("list_insight_candidates", new { n = 50 })));
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}

// A candidate's decision through the tools, its own database: a declined pair is never proposed again, whatever happens to
// its memories; an insight named with a use must cite the pair and stay no wider than it.
[Collection(SharedStateCollection.Name)]
public sealed class InsightStreamCandidateTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    async Task<(Guid Older, Guid Newer)> Pair(MemoryStore store, Guid login, string tag, string? workspace = null)
    {
        var older = (await store.Create(login, new($"DEMO {tag}: the boiler failed twice in January.", null, workspace, Importance: 4), StreamCalls.Mcp, default)).Id;
        await Insights.Written(fixture.Source, older, 60);
        var newer = (await store.Create(login, new($"DEMO {tag}: the bills fell by a third.", null, workspace, Importance: 3), StreamCalls.Mcp, default)).Id;
        return (older, newer);
    }

    [PostgresFact] public async Task A_declined_candidate_is_never_proposed_again()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await StreamCalls.Clear(s);
        var who = await MemoryLogins.Make(s, "declined");
        var store = new MemoryStore(s);
        var (older, newer) = await Pair(store, who.Alex, "declined");
        await Insights.Pass(s, new MeaningModel());
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        var candidate = (await store.Insights(who.Alex, null, 50, null, 100, default)).Items.Single(i => (i.Older.Id, i.Newer.Id) == (older, newer)).Id!.Value;
        var first = await ai.Ok("resolve_insight_candidate", new { id = candidate, action = "reject", note = "DEMO: nothing new." });
        Assert.Equal("DEMO: nothing new.", (string)first["note"]!);
        // A repeat changes nothing and what it was first told stays; the other decision is refused.
        var repeat = await ai.Ok("resolve_insight_candidate", new { id = candidate, action = "reject", note = "DEMO: another reason." });
        Assert.Equal((true, "DEMO: nothing new."), ((bool)repeat["unchanged"]!, (string)repeat["note"]!));
        // A note is another client's words: both answers that carry one say it is stored data.
        Assert.Equal(MemoryTools.StoredNotice, (string)repeat["notice"]!);
        Assert.Contains("notes", (string)repeat["notice"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("resolve_insight_candidate", new { id = candidate, action = "use" }))["code"]!);
        // Night after night, and after either memory changes, the pair stays declined and is offered to nobody.
        foreach (var round in new[] { 1, 2 })
        {
            await Insights.Pass(s, new MeaningModel());
            await store.Append(who.Alex, newer, $"DEMO more {round}.", null, StreamCalls.Mcp, default);
            await Insights.Pass(s, new MeaningModel());
            Assert.Equal("rejected", (await Insights.Rows(s)).Single(r => (r.Older, r.Newer) == (older, newer)).Status);
            Assert.DoesNotContain(candidate, StreamCalls.Ids(await ai.Ok("list_insight_candidates", new { n = 50 })));
        }
        Assert.Equal(1, (await Insights.Rows(s)).Count(r => (r.Older, r.Newer) == (older, newer)));
        // Nor does a dry run of the next pass name it.
        Assert.DoesNotContain((await store.InsightPreview(who.Alex, null, new MeaningModel(), 0.80, 100, default)), p => (p.Older.Id, p.Newer.Id) == (older, newer));
        // The list of what was declined is as long as its count, and keeps the reason.
        var listed = await ai.Ok("list_insight_candidates", new { state = "rejected", n = 50 });
        Assert.Equal(await store.InsightCount(who.Alex, null, default, "rejected"), (int)listed["total"]!);
        Assert.Contains(listed["items"]!.AsArray(), i => StreamCalls.Id(i) == candidate && (string)i!["note"]! == "DEMO: nothing new.");
        Assert.Equal(MemoryTools.CandidateNotice, (string)listed["notice"]!);
        Assert.Contains("notes", (string)listed["notice"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("list_insight_candidates", new { state = "waited" }))["code"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("list_insight_candidates", new { state = "rejected", dry_run = true }))["code"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("resolve_insight_candidate", new { id = candidate, action = "reject", note = "   " }))["code"]!);
    }

    [PostgresFact] public async Task An_insight_written_from_a_pair_cites_it_and_stays_in_its_scope()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            await StreamCalls.Clear(s);
            var who = await MemoryLogins.Make(s, "written");
            var store = new MemoryStore(s);
            var (older, newer) = await Pair(store, who.Alex, "private pair", "private");
            await Insights.Pass(s, new MeaningModel());
            var candidate = (await store.Insights(who.Alex, "private", 50, null, 100, default)).Items.Single(i => (i.Older.Id, i.Newer.Id) == (older, newer)).Id!.Value;
            Task<object?> Histories(params Guid[] memories) => MemoryRows.Scalar(s, "SELECT count(*) FROM memory_events WHERE memory_id=ANY(@ids)", ("ids", memories));
            var privateHistories = await Histories(older, newer);
            await using var alex = await StreamCalls.As(fixture, who.Alex);
            await using var sam = await StreamCalls.As(fixture, who.Sam);
            await using var house = await StreamCalls.As(fixture, who.House);

            // An insight that does not cite both memories is refused as what the pair came to, and the pair stays waiting.
            var citesOne = await StreamCalls.Save(alex, "DEMO · cites one", older);
            Assert.Equal("invalid", (string)(await alex.Refused("resolve_insight_candidate", new { id = candidate, action = "use", insight_id = citesOne }))["code"]!);
            Assert.Contains(candidate, StreamCalls.Ids(await alex.Ok("list_insight_candidates", new { n = 50 })));
            // One no one wrote, one this login does not read, and with a rejection, likewise.
            Assert.Equal("invalid", (string)(await alex.Refused("resolve_insight_candidate", new { id = candidate, action = "use", insight_id = Guid.NewGuid() }))["code"]!);
            var sams = await StreamCalls.Save(sam, "DEMO · Sam's");
            Assert.Equal("invalid", (string)(await alex.Refused("resolve_insight_candidate", new { id = candidate, action = "use", insight_id = sams }))["code"]!);
            Assert.Equal("invalid", (string)(await alex.Refused("resolve_insight_candidate", new { id = candidate, action = "reject", insight_id = citesOne }))["code"]!);
            Assert.Contains(candidate, StreamCalls.Ids(await alex.Ok("list_insight_candidates", new { n = 50 })));

            // An insight drawn from the private pair stays in that scope, whoever else reads Family.
            var drawn = await StreamCalls.Save(alex, "DEMO · drawn from the private pair", older, newer);
            Assert.Equal(("alex", "private"), ((string)(await MemoryRows.Scalar(s, "SELECT subject_id FROM insights WHERE id=@id", ("id", drawn)))!,
                (string)(await MemoryRows.Scalar(s, "SELECT scope FROM insights WHERE id=@id", ("id", drawn)))!));
            foreach (var other in new[] { sam, house })
                Assert.DoesNotContain(drawn, StreamCalls.Ids(await other.Ok("list_insight_stream", new { view = "all", n = 50 })));

            // A wider one, a Family insight citing the same pair, is refused as what the pair came to: nothing is decided.
            var wide = Guid.NewGuid();
            await MemoryRows.Run(s, """
                INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,generated_by,generated_at,sources_as_of,saved_by,subject_id,scope,sources_checked)
                VALUES(@id,'focus','DEMO wide','DEMO · a fictional insight.','','','[]','weak','','[]',
                  jsonb_build_array(jsonb_build_object('kind','memory','locator',@o::text),jsonb_build_object('kind','memory','locator',@n::text)),'manual',now(),now(),@by,'household','shared','{}')
                """, ("id", wide), ("o", older), ("n", newer), ("by", who.House));
            Assert.Equal("invalid", (string)(await alex.Refused("resolve_insight_candidate", new { id = candidate, action = "use", insight_id = wide }))["code"]!);
            Assert.Contains(candidate, StreamCalls.Ids(await alex.Ok("list_insight_candidates", new { n = 50 })));

            var used = await alex.Ok("resolve_insight_candidate", new { id = candidate, action = "use", insight_id = drawn });
            Assert.Equal(drawn, Guid.Parse((string)used["insight_id"]!));
            var listed = await alex.Ok("list_insight_candidates", new { state = "used" });
            Assert.Equal((candidate, drawn), (StreamCalls.Id(listed["items"]![0]), Guid.Parse((string)listed["items"]![0]!["insight_id"]!)));

            // A Family pair: Alex's client may write its insight narrower, in his own scope; Sam reads the pair as used, and
            // is told no insight it cannot read.
            var (fOlder, fNewer) = await Pair(store, who.Alex, "family pair", "shared");
            await Insights.Pass(s, new MeaningModel());
            var family = (await store.Insights(who.Alex, "shared", 50, null, 100, default)).Items.Single(i => (i.Older.Id, i.Newer.Id) == (fOlder, fNewer)).Id!.Value;
            var familyHistories = await Histories(fOlder, fNewer);
            var narrower = await StreamCalls.Save(alex, "DEMO · from the Family pair", fOlder, fNewer);
            Assert.Equal("alex", await MemoryRows.Scalar(s, "SELECT subject_id FROM insights WHERE id=@id", ("id", narrower)));
            await alex.Ok("resolve_insight_candidate", new { id = family, action = "use", insight_id = narrower });
            Assert.Equal(narrower, Guid.Parse((string)(await alex.Ok("list_insight_candidates", new { state = "used", workspace = "shared" }))["items"]![0]!["insight_id"]!));
            var seen = await sam.Ok("list_insight_candidates", new { state = "used", workspace = "shared" });
            Assert.Equal(family, StreamCalls.Id(seen["items"]![0]));
            Assert.Null(seen["items"]![0]!["insight_id"]);
            // Nor does a repeat name it to Sam, who may repeat the decision: it answers the note and no insight. Alex is told it.
            var repeated = await sam.Ok("resolve_insight_candidate", new { id = family, action = "use" });
            Assert.True((bool)repeated["unchanged"]!);
            Assert.Null(repeated["insight_id"]);
            var own = await alex.Ok("resolve_insight_candidate", new { id = family, action = "use" });
            Assert.Equal((true, narrower), ((bool)own["unchanged"]!, Guid.Parse((string)own["insight_id"]!)));

            // Nothing about any of it reached the memories' histories, by any event.
            Assert.Equal(privateHistories, await Histories(older, newer));
            Assert.Equal(familyHistories, await Histories(fOlder, fNewer));
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}

// Proposed and confirmed kept apart, a snooze that ends, feedback kept, a day that says so: its own database, each test
// from an empty stream.
[Collection(SharedStateCollection.Name)]
public sealed class InsightStreamTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    InsightStore Store => new(fixture.Source);
    Guid App => (Guid)MemoryRows.Scalar(fixture.Source, "SELECT id FROM principals WHERE role='app' AND enabled AND person_id IS NULL").GetAwaiter().GetResult()!;

    async Task<MemoryLogins> Fresh(string tag)
    {
        await Scoped.Mode(fixture.Source, "off");
        await StreamCalls.Clear(fixture.Source);
        return await MemoryLogins.Make(fixture.Source, tag);
    }

    Task Backdate(Guid insight, string sql) => MemoryRows.Run(fixture.Source, $"UPDATE insights SET {sql} WHERE id=@id", ("id", insight));

    // A Family insight as a person or the household's client saved it, with no words of a private record behind it.
    async Task<Guid> Family(Guid savedBy, string title)
    {
        var id = Guid.NewGuid();
        await MemoryRows.Run(fixture.Source, """
            INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,generated_by,generated_at,sources_as_of,saved_by,subject_id,scope,sources_checked)
            VALUES(@id,'focus',@t,'DEMO · a fictional insight.','','','[]','weak','','[]','[]','manual',now(),now(),@by,'household','shared','{}')
            """, ("id", id), ("t", title), ("by", savedBy));
        return id;
    }

    static string[] Statuses(string listed) => [.. JsonDocument.Parse(listed).RootElement.EnumerateArray().Select(r => r.GetProperty("status").GetString()!)];

    // A daily client adds about one a day and an install with one client can't review its own, so what is pending is what came this
    // week, or the count would only grow, and a first turn of memory.tools on would count the whole history. An older insight is
    // still listed, still reviewed by a person, and in the brief's count no more. A snooze that ended starts its week again.
    [PostgresFact] public async Task What_waits_stays_bounded_and_an_older_insight_can_still_be_reviewed()
    {
        var s = fixture.Source;
        var who = await Fresh("older");
        await using var daily = await StreamCalls.As(fixture, who.Alex);
        await using var person = await StreamCalls.As(fixture, who.Sam);
        var insights = new InsightStore(s);
        var today = await StreamCalls.Save(daily, "DEMO · today");
        var week = new List<Guid>();
        for (var d = 1; d <= 6; d++)
        {
            var id = await StreamCalls.Save(daily, $"DEMO · {d} days ago");
            await MemoryRows.Run(s, $"UPDATE insights SET created_at=now()-interval '{d} days' WHERE id=@id", ("id", id));
            week.Add(id);
        }
        var history = new List<Guid>();
        for (var d = 0; d < 30; d++)
        {
            var id = await StreamCalls.Save(daily, $"DEMO · {d + 8} days ago");
            await MemoryRows.Run(s, $"UPDATE insights SET created_at=now()-interval '{d + 8} days' WHERE id=@id", ("id", id));
            history.Add(id);
        }

        // Each insight in one view, every count the length of its list, the brief's the proposed view's.
        var views = await StreamCalls.Views(daily);
        Assert.Equal([today, .. week], views["proposed"].Order().OrderBy(x => x == today ? 0 : 1 + week.IndexOf(x)));
        Assert.Equal(history.Order(), views["older"].Order());
        var brief = await Briefs.Of(s, who.Alex);
        Assert.Equal(7, brief.Insights.Total);
        Assert.Equal("7 pending insights.", brief.Says);

        // A person's review works on one, and it answers the view it is in.
        var kept = history[0];
        var reviewed = await person.Ok("review_insight", new { id = kept, action = "confirm", note = "DEMO · it holds" });
        Assert.Equal(("confirmed", "confirmed"), ((string)reviewed["review"]!, (string)reviewed["view"]!));
        var reopened = await person.Ok("review_insight", new { id = kept, action = "reopen", note = "DEMO · not so sure" });
        Assert.Equal(("proposed", "older"), ((string)reopened["review"]!, (string)reopened["view"]!));
        // Resolve takes it as waiting, once.
        var first = await new MemoryStore(s).ResolveItem(who.Sam, new(history[1], MemoryResolve.ConfirmInsight), insights, Resolves.Mcp, default);
        Assert.False(first.Unchanged);
        Assert.True((await new MemoryStore(s).ResolveItem(who.Sam, new(history[1], MemoryResolve.ConfirmInsight), insights, Resolves.Mcp, default)).Unchanged);

        // A snooze's end brings it back for a week from then.
        await MemoryRows.Run(s, "UPDATE insights SET status='snoozed',feedback=jsonb_build_object('value','later','until',now()-interval '1 day','at',now()-interval '2 days') WHERE id=@id", ("id", history[2]));
        var back = (await insights.Stream(who.Alex, InsightStore.Proposed, null, InsightStore.StreamMax, null, default)).Items.Single(i => i.Id == history[2]);
        Assert.True(back.SnoozeEnded);
    }

    // Every state an insight can be in, by its status, review and feedback, is in one view, and in confirmed only by its review.
    [PostgresFact] public async Task A_proposed_insight_is_never_shown_as_confirmed_whatever_its_status_or_feedback()
    {
        var s = fixture.Source;
        foreach (var status in InsightStore.Statuses)
            foreach (var review in new[] { "proposed", "confirmed", "dismissed" })
                foreach (var until in new[] { "now()+interval '1 hour'", "now()-interval '1 hour'", "NULL" })
                    foreach (var replaced in new[] { false, true })
                    {
                        var view = (string)(await MemoryRows.Scalar(s, $"SELECT insight_stream_view(@s,@r,jsonb_build_object('until',{until}),@x)", ("s", status), ("r", review), ("x", replaced)))!;
                        Assert.Contains(view, new[] { "proposed", "confirmed", "snoozed", "closed" });
                        Assert.Equal(view == "confirmed", review == "confirmed" && status != "superseded" && !replaced && !(status == "snoozed" && until.Contains('+')));
                        if (view == "proposed") Assert.Equal("proposed", review);
                        if (status == "superseded" || review == "dismissed" || replaced) Assert.Equal("closed", view);
                    }

        // One nobody reviewed for a week stops counting as waiting; it stays a proposal, and only one that would be proposed moves.
        foreach (var status in InsightStore.Statuses)
            foreach (var review in new[] { "proposed", "confirmed", "dismissed" })
            {
                var young = (string)(await MemoryRows.Scalar(s, "SELECT insight_stream_view(@s,@r,'{}'::jsonb,false,now())", ("s", status), ("r", review)))!;
                var old = (string)(await MemoryRows.Scalar(s, "SELECT insight_stream_view(@s,@r,'{}'::jsonb,false,now()-interval '8 days')", ("s", status), ("r", review)))!;
                Assert.Equal(young == "proposed" ? "older" : young, old);
                if (old == "older") Assert.Equal("proposed", review);
            }

        var who = await Fresh("kept-apart");
        await using var daily = await StreamCalls.As(fixture, who.Alex);
        await using var other = await StreamCalls.As(fixture, who.Sam);
        var insight = await StreamCalls.Save(daily, "DEMO · proposed");
        // A draft cannot say otherwise: a client's words for the review are read nowhere.
        var named = Guid.Parse((string)(await MemoryRows.Scalar(s, """
            SELECT save_insight(@p,jsonb_build_object('kind','focus','title','DEMO named','summary','DEMO','whyItMatters','','nextStep','','points','[]'::jsonb,'evidenceLevel','weak',
              'limitations','','stars','[]'::jsonb,'sources','[]'::jsonb,'generatedBy','harness-daily','generatedAt',now(),'sourcesAsOf',now(),'review','confirmed','reviewed_at',now()))::text
            """, ("p", who.Alex)))!);
        Assert.Equal("proposed", await MemoryRows.Scalar(s, "SELECT review FROM insights WHERE id=@id", ("id", named)));
        var extra = await daily.Raw("save_insight", new { kind = "focus", title = "DEMO extra", summary = "DEMO", whyItMatters = "", nextStep = "", evidenceLevel = "weak", limitations = "",
            generatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), sourcesAsOf = DateTimeOffset.UtcNow.AddMinutes(-1), review = "confirmed" });
        Assert.True(extra.IsError == true, MemoryToolClient.Text(extra));
        Assert.Contains("review", MemoryToolClient.Text(extra));
        Assert.Empty((await StreamCalls.Views(daily))["confirmed"]);

        // Feedback of every kind leaves it proposed: a reaction is no review. Not useful and handled answer it, without confirming.
        foreach (var value in InsightStore.Feedback)
        {
            Assert.NotNull(await Store.Record(App, insight, value, null, null, default, keep: true));
            Assert.Equal("proposed", await MemoryRows.Scalar(s, "SELECT review FROM insights WHERE id=@id", ("id", insight)));
            Assert.DoesNotContain(insight, (await StreamCalls.Views(daily))["confirmed"]);
        }
        await Store.Record(App, insight, "interesting", null, null, default, keep: true);
        Assert.Contains(insight, (await StreamCalls.Views(daily))["proposed"]);

        // Its own login never reviews it, with scopes off too; another login, on a person's word, does.
        Assert.Equal("forbidden", (string)(await daily.Refused("review_insight", new { id = insight, action = "confirm" }))["code"]!);
        Assert.Equal("confirmed", (string)(await other.Ok("review_insight", new { id = insight, action = "confirm" }))["review"]!);
        Assert.Contains(insight, (await StreamCalls.Views(daily))["confirmed"]);
        // A reaction does not undo it: a confirmed insight someone finds not useful is still confirmed, only a review says otherwise.
        await Store.Record(App, insight, "not_useful", null, null, default, keep: true);
        Assert.Contains(insight, (await StreamCalls.Views(daily))["confirmed"]);
        // Reopened, its review is proposed again, yet someone marked it not useful: it stays closed, and the answer says so.
        var reopened = await other.Ok("review_insight", new { id = insight, action = "reopen" });
        Assert.Null(reopened["reviewed_at"]);
        Assert.Equal(("proposed", "closed"), ((string)reopened["review"]!, (string)reopened["view"]!));
        Assert.Contains("stays closed", (string)reopened["message"]!);
        var afterReopen = await StreamCalls.Views(daily);
        Assert.Contains(insight, afterReopen["closed"]);
        Assert.DoesNotContain(insight, afterReopen["proposed"]);
        // As the answer says, new feedback brings it back.
        await Store.Record(App, insight, "interesting", null, null, default, keep: true);
        Assert.Contains(insight, (await StreamCalls.Views(daily))["proposed"]);
        // Dismissed, it waits on no one; the history keeps each.
        var dismissed = await other.Ok("review_insight", new { id = insight, action = "dismiss", note = "DEMO: it does not hold." });
        Assert.Equal(("dismissed", "closed"), ((string)dismissed["review"]!, (string)dismissed["view"]!));
        Assert.Contains(insight, (await StreamCalls.Views(daily))["closed"]);
        Assert.Equal(["dismissed", "feedback", "reopened"], (await daily.Ok("insight_history", new { id = insight }))["items"]!.AsArray().Take(3).Select(e => (string)e!["event"]!));
        // Reopened with no such mark on it, it is proposed again, in the answer and in the stream.
        var proposedAgain = await other.Ok("review_insight", new { id = insight, action = "reopen" });
        Assert.Equal(("proposed", "proposed"), ((string)proposedAgain["review"]!, (string)proposedAgain["view"]!));
        Assert.Contains("proposed again", (string)proposedAgain["message"]!);
        Assert.Contains(insight, (await StreamCalls.Views(daily))["proposed"]);
        // Snoozed, a review answers where the snooze holds it, and the stream agrees; a repeat too.
        await Store.Record(App, insight, "later", null, null, default, keep: true);
        var held = await other.Ok("review_insight", new { id = insight, action = "confirm" });
        Assert.Equal(("confirmed", "snoozed"), ((string)held["review"]!, (string)held["view"]!));
        Assert.Equal("snoozed", (string)(await other.Ok("review_insight", new { id = insight, action = "confirm" }))["view"]!);
        Assert.Contains(insight, (await StreamCalls.Views(daily))["snoozed"]);
        // A later insight replacing a confirmed one is proposed itself; the replaced one is closed, and no review reopens it.
        Assert.Equal("confirmed", (string)(await other.Ok("review_insight", new { id = named, action = "confirm" }))["review"]!);
        var next = await Store.Save(who.Sam, StreamCalls.Draft("DEMO · replaces it", supersedes: named), default);
        var views = await StreamCalls.Views(daily);
        Assert.Contains(next, views["proposed"]);
        Assert.DoesNotContain(next, views["confirmed"]);
        Assert.Contains(named, views["closed"]);
        Assert.Equal("invalid", (string)(await other.Refused("review_insight", new { id = named, action = "reopen" }))["code"]!);
        Assert.True((await other.Raw("review_insight", new { id = next, action = "maybe" })).IsError == true);
        Assert.Equal("invalid", (string)(await other.Refused("review_insight", new { id = next, action = "confirm", note = new string('x', 501) }))["code"]!);
        Assert.Equal("unknown", (string)(await other.Refused("review_insight", new { id = Guid.NewGuid(), action = "confirm" }))["code"]!);
        // The answers name the review apart, and what every other reader is served is as it was: no field added.
        Assert.DoesNotContain("\"review\"", await Store.List(App, null, null, 50, default));
    }

    // Who may review (memory.md section 5, D-031), with scopes on.
    [PostgresFact] public async Task Only_its_owner_its_saver_or_an_admin_reviews_an_insight()
    {
        var s = fixture.Source;
        var who = await Fresh("who-reviews");
        await MemoryLogins.Family(s);
        await Scoped.Mode(s, "on");
        try
        {
            var alexPhone = await Scoped.Principal(s, "who-reviews-alex-phone", "harness", "alex");
            var viewer = await Scoped.Principal(s, "who-reviews-viewer", "app", "alex");
            var terminal = await MemoryLogins.Terminal(s, "alex");
            async Task<string> Refused(Guid login, Guid id, string channel = "mcp") =>
                await MemoryLogins.Refused(() => Store.Review(login, id, "confirm", null, new(channel, channel == "mcp" ? "DEMO client" : null), default));

            // A private insight: its owner's other logins, whichever channel; never another person, an admin included.
            var mine = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · Alex's"), default);
            Assert.Equal("alex", await MemoryRows.Scalar(s, "SELECT subject_id FROM insights WHERE id=@id", ("id", mine)));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal("unknown", await Refused(who.Sam, mine));
            Assert.Equal("unknown", await Refused(who.House, mine));
            Assert.Equal("forbidden", await Refused(who.Alex, mine));
            Assert.Equal("confirmed", (await Store.Review(alexPhone, mine, "confirm", null, StreamCalls.Mcp, default)).Review);
            Assert.True((await Store.Review(viewer, mine, "confirm", null, new("app"), default)).Unchanged);
            Assert.Equal("dismissed", (await Store.Review(viewer, mine, "dismiss", null, new("app"), default)).Review);
            Assert.Equal("proposed", (await Store.Review(terminal, mine, "reopen", null, new("cli"), default)).Review);
            // A login does not name a channel that is not its own.
            Assert.Equal("invalid", await Refused(who.Alex, mine, "app"));

            // A Family insight no person saved (the daily insight with an unbound token): an admin only. Not its saver, not a
            // member, not the household's view.
            await Scoped.Execute(s, "UPDATE memberships SET role='member'");
            var unbound = await Family(who.House, "DEMO · the household's");
            Assert.Equal("forbidden", await Refused(who.House, unbound));
            Assert.Equal("forbidden", await Refused(who.Alex, unbound));
            Assert.Equal("forbidden", await Refused(who.Sam, unbound));
            Assert.Equal("forbidden", await Refused(App, unbound, "app"));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal("confirmed", (await Store.Review(who.Sam, unbound, "confirm", null, StreamCalls.Mcp, default)).Review);
            Assert.Equal("forbidden", await Refused(who.Alex, unbound));
            await Scoped.Execute(s, "UPDATE memberships SET role='member'");

            // A Family insight a person saved: that person, through another of their logins, or an admin; no other member.
            var theirs = await Family(who.Alex, "DEMO · Alex's for Family");
            Assert.Equal("forbidden", await Refused(who.Alex, theirs));
            Assert.Equal("forbidden", await Refused(who.Sam, theirs));
            Assert.Equal("confirmed", (await Store.Review(alexPhone, theirs, "confirm", null, StreamCalls.Mcp, default)).Review);
        }
        finally { await Scoped.Mode(s, "off"); }

        // With scopes off, any admitted login but its saver over MCP, the app's among them.
        var plain = await Store.Save(who.House, StreamCalls.Draft("DEMO · with scopes off"), default);
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Review(who.House, plain, "confirm", null, StreamCalls.Mcp, default)));
        Assert.Equal("confirmed", (await Store.Review(App, plain, "confirm", null, new("app"), default)).Review);
        Assert.Equal("proposed", (await Store.Review(who.Sam, plain, "reopen", null, StreamCalls.Mcp, default)).Review);
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Review(Guid.NewGuid(), plain, "confirm", null, StreamCalls.Mcp, default)));
        Assert.Equal(true, await MemoryRows.Scalar(s, "SELECT 'insight_events'::regclass=ANY(checked_tables())"));
        // A login of neither role reads no stream and no day: refused, not unavailable.
        var ingest = await Scoped.Principal(s, "who-reviews-system", "system");
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Stream(ingest, InsightStore.Proposed, null, 10, null, default)));
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Today(ingest, TimeZoneInfo.Utc, default)));
    }

    // supersedes is no way round that rule: a confirmed insight is closed only by who may review it, never over MCP by the
    // login that saved it. Refused, nothing is saved and nothing moves; allowed, the history keeps who did.
    [PostgresFact] public async Task A_confirmed_insight_is_replaced_only_by_who_may_review_it()
    {
        var s = fixture.Source;
        var who = await Fresh("replace-rule");
        await MemoryLogins.Family(s);
        await using var reader = await StreamCalls.As(fixture, who.Alex);
        await using var household = await StreamCalls.As(fixture, who.House);
        async Task<string> Refused(Guid login, Guid id) =>
            (await Assert.ThrowsAsync<PostgresException>(() => Store.Save(login, StreamCalls.Draft("DEMO · would replace it", supersedes: id), default))).SqlState;
        Task<object?> Saved() => MemoryRows.Scalar(s, "SELECT count(*) FROM insights");

        await Scoped.Mode(s, "on");
        try
        {
            var alexPhone = await Scoped.Principal(s, "replace-rule-alex-phone", "harness", "alex");
            var family = await Family(who.Alex, "DEMO · Alex's for Family");
            Assert.Equal("confirmed", (await Store.Review(alexPhone, family, "confirm", null, StreamCalls.Mcp, default)).Review);
            var saved = await Saved();

            // The household's client may not review it, nor a member who did not save it, nor the login that did.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Review(who.House, family, "dismiss", null, StreamCalls.Mcp, default)));
            foreach (var login in new[] { who.House, who.Sam, who.Alex }) Assert.Equal("42501", await Refused(login, family));
            // Over the tool, the refusal says what to do instead.
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            var tool = await household.Raw("save_insight", new { kind = "focus", title = "DEMO · over the tool", summary = "DEMO", whyItMatters = "", nextStep = "", evidenceLevel = "weak", limitations = "", generatedAt = at, sourcesAsOf = at, supersedes = family });
            Assert.True(tool.IsError == true, MemoryToolClient.Text(tool));
            Assert.Contains("save it without supersedes", MemoryToolClient.Text(tool));
            // Nothing was saved and nothing moved: still confirmed, and its history is the review alone.
            Assert.Equal(saved, await Saved());
            Assert.Contains(family, (await StreamCalls.Views(reader))["confirmed"]);
            Assert.Equal("confirmed:-:-:mcp", await StreamCalls.Events(s, family));

            // Alex through another login may, as a review may: it closes, and the history says so after the confirmation.
            var theirs = await Store.Save(alexPhone, StreamCalls.Draft("DEMO · Alex's replacement", supersedes: family), default);
            Assert.Contains(family, (await StreamCalls.Views(reader))["closed"]);
            Assert.Contains(theirs, (await StreamCalls.Views(reader))["proposed"]);
            Assert.Equal("confirmed:-:-:mcp;replaced:-:-:mcp", await StreamCalls.Events(s, family));

            // At the terminal a login is a person's own, so its own insight may be replaced there, as reviewed there.
            var terminal = await MemoryLogins.Terminal(s, "alex");
            var atTerminal = await Store.Save(terminal, StreamCalls.Draft("DEMO · at the terminal"), default);
            await Store.Review(alexPhone, atTerminal, "confirm", null, StreamCalls.Mcp, default);
            await Store.Save(terminal, StreamCalls.Draft("DEMO · replaced at the terminal", supersedes: atTerminal), default);
            Assert.Equal("confirmed:-:-:mcp;replaced:-:-:cli", await StreamCalls.Events(s, atTerminal));

            // A private confirmed insight: no one else reads it, so no one else names it.
            var mine = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · Alex's own"), default);
            await Store.Review(alexPhone, mine, "confirm", null, StreamCalls.Mcp, default);
            Assert.Equal("22023", await Refused(who.Sam, mine));
            Assert.Equal("42501", await Refused(who.Alex, mine));
        }
        finally { await Scoped.Mode(s, "off"); }

        // With scopes off any admitted login reviews, so any but its saver replaces, a feedback from before going in ahead.
        var plain = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · with scopes off"), default);
        Assert.Equal("confirmed", (await Store.Review(who.Sam, plain, "confirm", null, StreamCalls.Mcp, default)).Review);
        Assert.Equal("42501", await Refused(who.Alex, plain));
        await Store.Record(App, plain, "interesting", null, "DEMO before", default);
        var next = await Store.Save(who.Sam, StreamCalls.Draft("DEMO · replaces it, scopes off", supersedes: plain), default);
        Assert.Equal("confirmed:-:-:mcp;feedback:interesting:DEMO before:-;replaced:-:-:mcp", await StreamCalls.Events(s, plain));
        var history = await reader.Ok("insight_history", new { id = plain });
        Assert.Equal(("replaced", "sam", "mcp", "confirmed", 3), ((string)history["items"]![0]!["event"]!, (string)history["items"]![0]!["person"]!,
            (string)history["items"]![0]!["channel"]!, (string)history["review"]!, (int)history["total"]!));
        Assert.Contains(next, (await StreamCalls.Views(reader))["proposed"]);

        // Anything not confirmed is replaced as it always was, by its own saver too, and adds no event: only memory.tools on
        // confirms an insight, so an install that never had it on replaces this way alone and insight_events stays empty.
        var unreviewed = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · proposed"), default);
        await Store.Save(who.Alex, StreamCalls.Draft("DEMO · replaces its own", supersedes: unreviewed), default);
        Assert.Null(await StreamCalls.Events(s, unreviewed));
        var declined = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · dismissed"), default);
        await Store.Review(who.Sam, declined, "dismiss", null, StreamCalls.Mcp, default);
        await Store.Save(who.Alex, StreamCalls.Draft("DEMO · replaces what was dismissed", supersedes: declined), default);
        Assert.Equal("dismissed:-:-:mcp", await StreamCalls.Events(s, declined));
    }

    // The link says an insight was replaced, not the status the app's feedback overwrites: it stays closed and unreviewable,
    // beside its replacement, while its status moves as it always did.
    [PostgresFact] public async Task A_replaced_insight_stays_closed_whatever_feedback_it_has_since()
    {
        var s = fixture.Source;
        var who = await Fresh("replaced-feedback");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        await using var other = await StreamCalls.As(fixture, who.Sam);
        var old = await StreamCalls.Save(ai, "DEMO · old");
        await other.Ok("review_insight", new { id = old, action = "confirm" });
        var next = await Store.Save(who.Sam, StreamCalls.Draft("DEMO · new", supersedes: old), default);
        Assert.Equal("superseded", await MemoryRows.Scalar(s, "SELECT status FROM insights WHERE id=@id", ("id", old)));
        foreach (var value in InsightStore.Feedback)
        {
            await Store.Record(App, old, value, null, null, default, keep: true);
            Assert.NotEqual("superseded", await MemoryRows.Scalar(s, "SELECT status FROM insights WHERE id=@id", ("id", old)));
            var views = await StreamCalls.Views(ai);
            Assert.Contains(old, views["closed"]);
            Assert.Contains(next, views["proposed"]);
            foreach (var action in InsightStore.Reviews)
                Assert.Equal("invalid", (string)(await other.Refused("review_insight", new { id = old, action }))["code"]!);
        }

        // One whose replacement was deleted has no link left, and its status still says so.
        var earlier = await StreamCalls.Save(ai, "DEMO · earlier");
        var later = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · later", supersedes: earlier), default);
        await MemoryRows.Run(s, "DELETE FROM insights WHERE id=@id", ("id", later));
        Assert.Contains(earlier, (await StreamCalls.Views(ai))["closed"]);
        Assert.Equal("invalid", (string)(await other.Refused("review_insight", new { id = earlier, action = "confirm" }))["code"]!);
    }

    [PostgresFact] public async Task A_snooze_ends_when_its_time_comes_and_a_read_writes_nothing()
    {
        var s = fixture.Source;
        var who = await Fresh("snooze");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        await using var other = await StreamCalls.As(fixture, who.Sam);
        var older = await StreamCalls.Save(ai, "DEMO · older");
        var newer = await StreamCalls.Save(ai, "DEMO · newer");
        await Backdate(older, "created_at=now()-interval '5 days'");
        await Backdate(newer, "created_at=now()-interval '3 days'");

        // Snoozed: it leaves what waits, into its own view, with its end; the app's reading of it is as it was.
        Assert.NotNull(await Store.Record(App, older, "later", DateTimeOffset.UtcNow.AddHours(2), null, default, keep: true));
        var views = await StreamCalls.Views(ai);
        Assert.Equal([newer], views["proposed"]);
        Assert.Equal([older], views["snoozed"]);
        var held = (await ai.Ok("list_insight_stream", new { view = "snoozed" }))["items"]![0]!;
        Assert.False((bool)held["snooze_ended"]!);
        Assert.NotNull(held["snooze_until"]);
        Assert.Equal("snoozed", (string)held["insight"]!["status"]!);

        // Its time comes, by the clock alone: it is back among the proposed, first by when it came back, though it was older.
        await Backdate(older, "feedback=jsonb_set(feedback,'{until}',to_jsonb(now()-interval '1 minute'))");
        var state = await MemoryRows.Scalar(s, "SELECT updated_at::text||feedback::text FROM insights WHERE id=@id", ("id", older));
        views = await StreamCalls.Views(ai);
        Assert.Equal(2, views["proposed"].Count);
        Assert.Empty(views["snoozed"]);
        Assert.Equal([older, newer], (await ai.Ok("list_insight_stream"))["items"]!.AsArray().Select(StreamCalls.Id));
        var back = (await ai.Ok("list_insight_stream"))["items"]![0]!;
        Assert.True((bool)back["snooze_ended"]!);
        Assert.Equal((string)back["snooze_until"]!, (string)back["shown_at"]!);
        // The read wrote nothing: the row still says snoozed, as list_insights and the app read it.
        Assert.Equal(state, await MemoryRows.Scalar(s, "SELECT updated_at::text||feedback::text FROM insights WHERE id=@id", ("id", older)));
        Assert.Contains("snoozed", Statuses(await Store.List(App, null, null, 50, default)));
        // The next feedback ends the snooze's mark as it always has.
        await Store.Record(App, older, "explore", null, null, default, keep: true);
        Assert.DoesNotContain("snoozed", Statuses(await Store.List(App, null, null, 50, default)));

        // A confirmed insight snoozed comes back confirmed; a snooze naming no end has none to wait for; later's default is a day.
        await other.Ok("review_insight", new { id = older, action = "confirm" });
        await Store.Record(App, older, "later", null, null, default, keep: true);
        Assert.Contains(older, (await StreamCalls.Views(ai))["snoozed"]);
        await Backdate(older, "feedback=jsonb_set(feedback,'{until}',to_jsonb(now()-interval '1 minute'))");
        Assert.Contains(older, (await StreamCalls.Views(ai))["confirmed"]);
        await Store.Record(App, newer, "later", null, null, default, keep: true);
        await Backdate(newer, "feedback=jsonb_set(feedback,'{until}','null')");
        Assert.Contains(newer, (await StreamCalls.Views(ai))["proposed"]);

        // Pages hold: the stream walks newest shown first with no repeats (Walk), one scope at a time as asked.
        Assert.Equal("invalid", (string)(await ai.Refused("list_insight_stream", new { view = "maybe" }))["code"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("list_insight_stream", new { n = 51 }))["code"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("list_insight_stream", new { cursor = "not-a-cursor" }))["code"]!);
        Assert.Equal("invalid", (string)(await ai.Refused("list_insight_stream", new { workspace = "team" }))["code"]!);
        Assert.Equal(2, (await StreamCalls.Walk(ai, "all", "shared")).Count);
        Assert.Empty(await StreamCalls.Walk(ai, "all", "private"));
    }

    [PostgresFact] public async Task Feedback_history_is_kept_and_only_with_memory_tools_on()
    {
        var s = fixture.Source;
        var who = await Fresh("history");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        var insight = await StreamCalls.Save(ai, "DEMO · history");
        var viewer = await Scoped.Principal(s, "history-viewer", "app", "sam");

        // Off, as the app has always called it: the latest alone, no history written, and the same answer shape.
        var off = (await Store.Record(App, insight, "interesting", null, "DEMO first", default))!;
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM insight_events"));
        // A reader is told it anyway, naming no one, without a write.
        var read = await ai.Ok("insight_history", new { id = insight });
        Assert.Equal(1, (int)read["total"]!);
        Assert.Equal(("feedback", "interesting", "DEMO first"), ((string)read["items"]![0]!["event"]!, (string)read["items"]![0]!["value"]!, (string)read["items"]![0]!["note"]!));
        Assert.Null(read["items"]![0]!["person"]);
        Assert.Null(read["items"]![0]!["channel"]);
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM insight_events"));

        // On: each kept; the one from before is carried in first, so nothing is replaced and lost, and the answer is the same shape.
        var on = (await Store.Record(App, insight, "later", DateTimeOffset.UtcNow.AddHours(3), null, default, keep: true))!;
        Assert.Equal(JsonNode.Parse(off)!.AsObject().Select(p => p.Key), JsonNode.Parse(on)!.AsObject().Select(p => p.Key));
        await Store.Record(viewer, insight, "handled", null, "DEMO done", default, keep: true);
        await Store.Record(viewer, insight, "not_useful", null, null, default, keep: true);
        Assert.Equal("feedback:interesting:DEMO first:-;feedback:later:-:app;feedback:handled:DEMO done:app;feedback:not_useful:-:app", await StreamCalls.Events(s, insight));
        var history = await ai.Ok("insight_history", new { id = insight });
        Assert.Equal(4, (int)history["total"]!);
        Assert.Equal(["not_useful", "handled", "later", "interesting"], history["items"]!.AsArray().Select(e => (string)e!["value"]!));
        Assert.Equal(("sam", "app"), ((string)history["items"]![0]!["person"]!, (string)history["items"]![0]!["channel"]!));
        Assert.NotNull(history["items"]![2]!["until"]);
        Assert.Null(history["items"]![3]!["person"]);
        // The insight's own feedback is the latest, as every reader receives it.
        Assert.Contains("not_useful", await Store.List(App, null, null, 50, default));
        // limit cuts the list, newest kept, and total still says how many; an insight no one reads is unknown.
        var cut = await ai.Ok("insight_history", new { id = insight, limit = 1 });
        Assert.Equal((4, "not_useful", 1), ((int)cut["total"]!, (string)cut["items"]![0]!["value"]!, cut["items"]!.AsArray().Count));
        Assert.Equal("invalid", (string)(await ai.Refused("insight_history", new { id = insight, limit = 101 }))["code"]!);
        Assert.Equal("unknown", (string)(await ai.Refused("insight_history", new { id = Guid.NewGuid() }))["code"]!);
        // A feedback on an insight this login does not know keeps nothing and answers as unknown, and an AI client's login gives none.
        Assert.Null(await Store.Record(App, Guid.NewGuid(), "interesting", null, null, default, keep: true));
        await Assert.ThrowsAsync<PostgresException>(() => Store.Record(who.Alex, insight, "interesting", null, null, default, keep: true));
        Assert.Equal(4L, await MemoryRows.Scalar(s, "SELECT count(*) FROM insight_events"));
        // A deleted insight takes its history with it.
        await MemoryRows.Run(s, "DELETE FROM insights WHERE id=@id", ("id", insight));
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM insight_events"));

        // A review keeps the order too: a feedback from before goes in ahead of it, and one given since with memory.tools
        // off, which no event followed, is the newest and names no one.
        var second = await StreamCalls.Save(ai, "DEMO · order");
        await Store.Record(App, second, "interesting", null, null, default);
        await using var other = await StreamCalls.As(fixture, who.Sam);
        await other.Ok("review_insight", new { id = second, action = "confirm" });
        Assert.Equal("feedback:interesting:-:-;confirmed:-:-:mcp", await StreamCalls.Events(s, second));
        await Store.Record(App, second, "explore", null, null, default);
        var order = await ai.Ok("insight_history", new { id = second });
        Assert.Equal(["feedback", "confirmed", "feedback"], order["items"]!.AsArray().Select(e => (string)e!["event"]!));
        Assert.Equal(("explore", "interesting"), ((string)order["items"]![0]!["value"]!, (string)order["items"]![2]!["value"]!));
        Assert.Null(order["items"]![0]!["channel"]);
    }

    // Through the app's endpoint: its feedback answers alike with memory.tools off and on, and only on is it kept.
    [PostgresFact] public async Task The_apps_feedback_answers_alike_and_is_kept_only_with_memory_tools_on()
    {
        var s = fixture.Source;
        var who = await Fresh("endpoint");
        var insight = await Store.Save(who.Alex, StreamCalls.Draft("DEMO · endpoint"), default);
        async Task<JsonNode> Feedback(RhythmConfig config, string value)
        {
            var services = new ServiceCollection().AddSingleton(config).AddSingleton(fixture.Db).AddSingleton(Store).BuildServiceProvider();
            var (status, content) = await AppMemoryCalls.CallTool(services, App, AppMemoryCalls.Body("app_insight_feedback", new { id = insight, value }));
            Assert.Equal(200, status);
            Schemas.Valid(Schemas.Folder("mcp"), "app_insight_feedback.output.schema.json", content!);
            return content!;
        }
        var off = await Feedback(TestSettings.Use(), "interesting");
        Assert.Equal(0L, await MemoryRows.Scalar(s, "SELECT count(*) FROM insight_events"));
        var on = await Feedback(MemoryToolSettings.On, "explore");
        TestSettings.Use();
        Assert.Equal(off.AsObject().Select(p => p.Key), on.AsObject().Select(p => p.Key));
        Assert.Equal("feedback:interesting:-:-;feedback:explore:-:app", await StreamCalls.Events(s, insight));
    }

    [PostgresFact] public async Task A_day_with_no_insight_says_so()
    {
        var s = fixture.Source;
        var who = await Fresh("day");
        await MemoryLogins.Family(s);
        await using var alex = await StreamCalls.As(fixture, who.Alex);
        await using var sam = await StreamCalls.As(fixture, who.Sam);
        async Task<JsonNode> Day(MemoryToolClient client) => (await client.Ok("list_insight_stream"))["day"]!;

        // No run at all: it says so, in the install's zone and today.
        var none = await Day(alex);
        Assert.Equal(("none", InsightStore.DayNone), ((string)none["outcome"]!, (string)none["says"]!));
        Assert.Equal(await MemoryRows.Scalar(s, "SELECT install_timezone()"), (string)none["zone"]!);
        Assert.Equal(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById((string)none["zone"]!)).ToString("yyyy-MM-dd"), (string)none["date"]!);

        // A quiet run: nothing cleared the bar, and its note, in its client's words, comes with it.
        Assert.True((await alex.Raw("log_insight_run", new { outcome = "quiet", note = "DEMO: read 3 pairs, declined them all." })).IsError != true);
        var quiet = await Day(alex);
        Assert.Equal(("quiet", "No insight today: the daily run looked and found nothing worth saying.", "DEMO: read 3 pairs, declined them all."),
            ((string)quiet["outcome"]!, (string)quiet["says"]!, (string)quiet["note"]!));
        Assert.Null(quiet["insight_id"]);

        // A blocked run after it: the newest says, and says what stopped it.
        await MemoryRows.Run(s, "UPDATE insight_runs SET created_at=created_at-interval '1 minute'");
        Assert.True((await alex.Raw("log_insight_run", new { outcome = "blocked", note = "DEMO: a tool was missing." })).IsError != true);
        Assert.Equal(("blocked", "DEMO: a tool was missing."), ((string)(await Day(alex))["outcome"]!, (string)(await Day(alex))["note"]!));

        // Yesterday's runs say nothing about today.
        await MemoryRows.Run(s, "UPDATE insight_runs SET created_at=created_at-interval '2 days'");
        Assert.Equal("none", (string)(await Day(alex))["outcome"]!);

        // A saved insight is the day's, whatever a run says after it.
        var insight = await StreamCalls.Save(alex, "DEMO · today's");
        Assert.True((await alex.Raw("log_insight_run", new { outcome = "quiet" })).IsError != true);
        var saved = await Day(alex);
        Assert.Equal(("saved", insight), ((string)saved["outcome"]!, Guid.Parse((string)saved["insight_id"]!)));
        Assert.Null(saved["note"]);
        await MemoryRows.Run(s, "UPDATE insights SET created_at=created_at-interval '2 days'; UPDATE insight_runs SET created_at=created_at-interval '2 days'");
        Assert.Equal("none", (string)(await Day(alex))["outcome"]!);

        // A run that saved just before midnight and logged just after: the insight is an earlier day's, the run today's, and
        // the day is saved, naming it.
        Assert.True((await alex.Raw("log_insight_run", new { outcome = "saved", insightId = insight })).IsError != true);
        var late = await Day(alex);
        Assert.Equal(("saved", insight, "An insight was proposed today."), ((string)late["outcome"]!, Guid.Parse((string)late["insight_id"]!), (string)late["says"]!));
        await MemoryRows.Run(s, "UPDATE insight_runs SET created_at=created_at-interval '2 days'");
        // A saved run naming no insight: none, in words that do not claim that no run logged.
        Assert.True((await alex.Raw("log_insight_run", new { outcome = "saved" })).IsError != true);
        var unnamed = await Day(alex);
        Assert.Equal(("none", InsightStore.DayNone), ((string)unnamed["outcome"]!, (string)unnamed["says"]!));
        Assert.Null(unnamed["insight_id"]);
        await MemoryRows.Run(s, "UPDATE insight_runs SET created_at=created_at-interval '2 days'");

        // With scopes on a run is its login's: Alex's quiet day is no answer to Sam, whose own day has none yet.
        await Scoped.Mode(s, "on");
        try
        {
            // A run's own insight is the day's only for who reads it: Sam neither reads Alex's run nor is told its insight.
            var privately = await StreamCalls.Save(alex, "DEMO · Alex's from before");
            await MemoryRows.Run(s, "UPDATE insights SET created_at=created_at-interval '2 days' WHERE id=@id", ("id", privately));
            Assert.True((await alex.Raw("log_insight_run", new { outcome = "saved", insightId = privately })).IsError != true);
            Assert.Equal(privately, Guid.Parse((string)(await Day(alex))["insight_id"]!));
            Assert.Equal(("none", null), ((string)(await Day(sam))["outcome"]!, (string?)(await Day(sam))["insight_id"]));
            await MemoryRows.Run(s, "UPDATE insight_runs SET created_at=created_at-interval '2 days'");

            Assert.True((await alex.Raw("log_insight_run", new { outcome = "quiet", note = "DEMO: Alex's own." })).IsError != true);
            Assert.Equal("quiet", (string)(await Day(alex))["outcome"]!);
            Assert.Equal(("none", null), ((string)(await Day(sam))["outcome"]!, (string?)(await Day(sam))["note"]));
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}
