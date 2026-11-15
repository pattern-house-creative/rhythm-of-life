using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using ModelContextProtocol.Protocol;

namespace Knowledge.Tests;

// The brief's lists behind its Home card (SB-P6-01, Mcp/AppMemoriesPlans.cs): what a person owes and is owed, what is due and the
// decisions with their outcomes, a count on the card the length of the list it opens; a person's word on each through the app's
// channel, a change like any other that undo takes back; none of it listed or taken with memory.tools off, and an AI client's
// token never reaching it. Fictional people only (alex, sam, the household); every text is DEMO.
public sealed class AppMemoryPlansTests
{
    static readonly Guid Id = Guid.Parse("6d3c2a10-0000-4000-8000-00000000f201");

    static bool Takes(object arguments) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true);

    // Every new op, as the view sends it: each is off with memory.tools off.
    static readonly object[] Steps =
    [
        new { op = "loops" }, new { op = "due" }, new { op = "decisions" }, new { op = "calibration" },
        new { op = "resolve_loop", id = Id, action = "done", expected = 1 },
        new { op = "resolve_due", id = Id, action = "snooze", until = "2026-12-01T09:00", expected = 1 },
        new { op = "resolve_due", id = Id, action = "clear_date", expected = 1 },
        new { op = "record_outcome", id = Id, result = "right", expected = 1 },
        new { op = "changes", notHeld = true },
    ];

    [Fact] public void Each_new_op_takes_exactly_its_arguments()
    {
        foreach (var step in Steps) Assert.True(Takes(step), JsonSerializer.Serialize(step));

        Assert.True(Takes(new { op = "loops", direction = "out", scope = "shared", cursor = "abc", limit = 50 }));
        Assert.True(Takes(new { op = "loops", direction = "in" }));
        Assert.False(Takes(new { op = "loops", direction = "all" }));
        Assert.False(Takes(new { op = "loops", limit = 51 }));
        Assert.False(Takes(new { op = "loops", window = "overdue" }));

        foreach (var window in new[] { "overdue", "upcoming", "later" }) Assert.True(Takes(new { op = "due", window, scope = "private", limit = 1 }));
        Assert.False(Takes(new { op = "due", window = "soon" }));
        Assert.False(Takes(new { op = "due", state = "open" }));

        foreach (var state in new[] { "open", "due", "upcoming", "resolved", "scored", "all" }) Assert.True(Takes(new { op = "decisions", state, cursor = "abc" }));
        Assert.False(Takes(new { op = "decisions", state = "later" }));
        // A state is the op's own: an insight candidate's is no decisions list.
        Assert.False(Takes(new { op = "decisions", state = "waiting" }));
        Assert.False(Takes(new { op = "decisions", direction = "out" }));

        Assert.True(Takes(new { op = "calibration", source = "stated", scope = "shared" }));
        Assert.False(Takes(new { op = "calibration", source = "all" }));
        Assert.False(Takes(new { op = "calibration", limit = 5 }));

        foreach (var action in new[] { "done", "received", "not_a_task" }) Assert.True(Takes(new { op = "resolve_loop", id = Id, action }));
        Assert.False(Takes(new { op = "resolve_loop", id = Id }));
        Assert.False(Takes(new { op = "resolve_loop", action = "done" }));
        Assert.False(Takes(new { op = "resolve_loop", id = Id, action = "snooze" }));
        Assert.False(Takes(new { op = "resolve_loop", id = Id, action = "done", key = "resolve-key-001" }));

        Assert.False(Takes(new { op = "resolve_due", id = Id, action = "done" }));
        // A snooze says when it comes back, and a clear names no date.
        Assert.False(Takes(new { op = "resolve_due", id = Id, action = "snooze" }));
        Assert.False(Takes(new { op = "resolve_due", id = Id, action = "clear_date", until = "2026-12-01" }));
        Assert.False(Takes(new { op = "resolve_due", id = Id, action = "snooze", until = "" }));
        Assert.False(Takes(new { op = "resolve_due", id = Id, action = "snooze", until = new string('2', 41) }));

        foreach (var result in new[] { "right", "wrong", "mixed", "unknown" }) Assert.True(Takes(new { op = "record_outcome", id = Id, result, expected = 2 }));
        Assert.False(Takes(new { op = "record_outcome", id = Id }));
        Assert.False(Takes(new { op = "record_outcome", id = Id, result = "maybe" }));
        Assert.False(Takes(new { op = "record_outcome", id = Id, result = "right", note = "DEMO" }));

        Assert.False(Takes(new { op = "changes", notHeld = "yes" }));

        // No op names its channel or its client: the handler fixes both.
        foreach (var op in new[] { "loops", "due", "decisions", "calibration" })
        {
            Assert.False(Takes(new { op, channel = "cli" }), op);
            Assert.False(Takes(new { op, client = "DEMO terminal" }), op);
        }
        Assert.False(Takes(new { op = "resolve_loop", id = Id, action = "done", channel = "app" }));
        Assert.False(Takes(new { op = "record_outcome", id = Id, result = "right", client = "DEMO" }));
    }

    [Fact] public void The_listing_names_each_new_op_and_each_argument_it_takes()
    {
        var listed = JsonSerializer.SerializeToNode(AppMemories.Listing())!["inputSchema"]!["properties"]!;
        var ops = listed["op"]!["enum"]!.AsArray().Select(o => (string)o!).ToArray();
        foreach (var op in new[] { "loops", "due", "decisions", "calibration", "resolve_loop", "resolve_due", "record_outcome" }) Assert.Contains(op, ops);
        foreach (var argument in new[] { "direction", "window", "state", "source", "action", "result", "until", "notHeld" }) Assert.NotNull(listed[argument]);
    }

    [Fact] public async Task With_memory_tools_off_no_new_op_is_taken()
    {
        foreach (var config in new[] { TestSettings.Use(), MemoryToolSettings.With("tools: off"), MemoryToolSettings.With("") })
            foreach (var step in Steps)
            {
                var (status, content) = await AppMemoryCalls.Call(AppMemoryCalls.Services(config), Guid.NewGuid(), AppMemoryCalls.Body(AppEndpoint.Memories, step));
                Assert.Equal(403, status);
                Assert.Equal("forbidden", (string)content!["error"]!["code"]!);
                Assert.False(AppEndpoint.Allowed(AppMemoryCalls.Request(step)));
            }
        TestSettings.Use();
    }
}

public sealed class AppMemoryPlansDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    static readonly string Held = ParityCorpus.Load().Memories.First(m => m.Expect?.Held == true).Text;
    MemoryStore Store => new(fixture.Source);

    async Task<(int Status, JsonNode? Content)> App(Guid app, object arguments) =>
        await AppMemoryCalls.Call(AppMemoryCalls.Services(MemoryToolSettings.On, fixture.Db), app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments),
            new AppHealth(fixture.Db, MemoryToolSettings.On));

    async Task<JsonNode> Ok(Guid app, object arguments)
    {
        var (status, content) = await App(app, arguments);
        Assert.Equal(200, status);
        Assert.True((bool)content!["ok"]!, content.ToJsonString());
        return content;
    }

    async Task<string> Refused(Guid app, object arguments)
    {
        var (status, content) = await App(app, arguments);
        Assert.Equal(200, status);
        Assert.False((bool)content!["ok"]!, content.ToJsonString());
        return (string)content["code"]!;
    }

    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);

    async Task<(string Channel, string? Client)> Audited(Guid id, string name)
    {
        await using var c = await fixture.Source.OpenConnectionAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT channel,client FROM memory_events WHERE memory_id=@id AND event=@name ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"no {name} row");
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    Task<object?> Events() => Scalar("SELECT count(*) FROM memory_events");

    // A list the view opens, walked to its end a page at a time, with every page's own count and the op's other lengths.
    async Task<(List<JsonNode> Items, HashSet<int> Counts, JsonNode Last)> Walk(Guid app, string op, string rows, string? name = null, string? value = null, string? scope = null)
    {
        var items = new List<JsonNode>();
        var counts = new HashSet<int>();
        string? cursor = null;
        JsonNode page;
        do
        {
            var call = new Dictionary<string, object?> { ["op"] = op, ["limit"] = 2 };
            if (name is not null) call[name] = value;
            if (scope is not null) call["scope"] = scope;
            if (cursor is not null) call["cursor"] = cursor;
            page = await Ok(app, call);
            items.AddRange(page[rows]!.AsArray().Select(i => i!));
            counts.Add((int)page["count"]!);
            cursor = (string?)page["next"];
        } while (cursor is not null);
        return (items, counts, page);
    }

    static IEnumerable<string> Ids(IEnumerable<JsonNode?> rows) => rows.Select(r => (string)r!["id"]!);

    // Every count on the card against the list it opens, each walked to its end: the card's number, the page's number and how many
    // rows there are are one number, and the card's items are that list's head.
    async Task<JsonNode> Agree(Guid app, string? scope = null)
    {
        var card = await Ok(app, scope is null ? new { op = "brief" } : new { op = "brief", scope });

        var owe = await Walk(app, "loops", "loops", "direction", "out", scope);
        Assert.Equal((int)card["youOwe"]!["count"]!, Assert.Single(owe.Counts));
        Assert.Equal((int)card["youOwe"]!["count"]!, owe.Items.Count);
        Assert.Equal(Ids(card["youOwe"]!["items"]!.AsArray()), Ids(owe.Items).Take(MemoryStore.BriefLoops));
        var owed = await Walk(app, "loops", "loops", "direction", "in", scope);
        Assert.Equal((int)card["owedToYou"]!["count"]!, Assert.Single(owed.Counts));
        Assert.Equal((int)card["owedToYou"]!["count"]!, owed.Items.Count);
        Assert.Equal(Ids(card["owedToYou"]!["items"]!.AsArray()), Ids(owed.Items).Take(MemoryStore.BriefLoops));
        // Tasks and commitments are one list, each direction's length beside it from the same read.
        var loops = await Walk(app, "loops", "loops", scope: scope);
        Assert.Equal(owe.Items.Count + owed.Items.Count, loops.Items.Count);
        Assert.Equal((owe.Items.Count, owed.Items.Count), ((int)loops.Last["youOwe"]!, (int)loops.Last["owedToYou"]!));

        var overdue = await Walk(app, "due", "due", "window", "overdue", scope);
        var upcoming = await Walk(app, "due", "due", "window", "upcoming", scope);
        Assert.Equal((overdue.Items.Count, upcoming.Items.Count, overdue.Items.Count + upcoming.Items.Count),
            ((int)card["due"]!["overdue"]!, (int)card["due"]!["upcoming"]!, (int)card["due"]!["count"]!));
        Assert.Equal(Ids(card["due"]!["items"]!.AsArray()), Ids(overdue.Items.Concat(upcoming.Items)).Take(MemoryStore.BriefDue));
        // The whole list soonest first holds the card's two windows ahead of the later ones, so the view reads them from one page.
        var whole = await Walk(app, "due", "due", scope: scope);
        Assert.Equal(Ids(overdue.Items.Concat(upcoming.Items)), Ids(whole.Items.Where(i => (string)i["window"]! != "later")));
        Assert.Equal((overdue.Items.Count, upcoming.Items.Count), ((int)whole.Last["overdue"]!, (int)whole.Last["upcoming"]!));
        Assert.Equal(whole.Items.Count(i => (string)i["window"]! == "later"), (int)whole.Last["later"]!);

        var come = await Walk(app, "decisions", "decisions", "state", "due", scope);
        var soon = await Walk(app, "decisions", "decisions", "state", "upcoming", scope);
        Assert.Equal((come.Items.Count, soon.Items.Count, come.Items.Count + soon.Items.Count),
            ((int)card["decisions"]!["overdue"]!, (int)card["decisions"]!["upcoming"]!, (int)card["decisions"]!["count"]!));
        Assert.Equal(Ids(card["decisions"]!["items"]!.AsArray()), Ids(come.Items.Concat(soon.Items)).Take(MemoryStore.BriefDecisions));
        Assert.All(come.Items, d => Assert.Equal("overdue", (string)d["window"]!));
        Assert.All(soon.Items, d => Assert.Equal("upcoming", (string)d["window"]!));
        Assert.Equal((come.Items.Count, soon.Items.Count), ((int)come.Last["dueNow"]!, (int)come.Last["upcoming"]!));

        var stale = await Walk(app, "stale", "stale", scope: scope);
        Assert.Equal((int)card["stale"]!["count"]!, Assert.Single(stale.Counts));
        Assert.Equal((int)card["stale"]!["count"]!, stale.Items.Count);
        var held = await Walk(app, "review", "review", scope: scope);
        Assert.Equal((int)card["held"]!, Assert.Single(held.Counts));
        Assert.Equal((int)card["held"]!, held.Items.Count);

        var changes = await Ok(app, scope is null ? new { op = "changes", notHeld = true } : new { op = "changes", notHeld = true, scope });
        Assert.Equal((int)card["changes"]!["count"]!, (int)changes["count"]!);
        Assert.Equal((int)card["changes"]!["count"]!, changes["groups"]!.AsArray().Count);
        Assert.Equal(card["changes"]!["groups"]!.AsArray().Select(g => (string)g!["group"]!), changes["groups"]!.AsArray().Select(g => (string)g!["group"]!).Take(MemoryStore.BriefGroups));

        // Calibration's figure rests on the scored list, which is the list a person can read.
        var scored = await Walk(app, "decisions", "decisions", "state", "scored", scope);
        Assert.Equal((int)card["calibration"]!["n"]!, scored.Items.Count);
        var calibration = await Ok(app, scope is null ? new { op = "calibration" } : new { op = "calibration", scope });
        Assert.Equal((int)card["calibration"]!["n"]!, (int)calibration["n"]!);
        Assert.Equal(((bool)card["calibration"]!["ready"]!, (string?)card["calibration"]!["line"]),
            ((bool)calibration["ready"]!, (bool)calibration["ready"]! ? (string?)calibration["line"] : null));
        return card;
    }

    [PostgresFact] public async Task A_count_on_the_card_is_the_length_of_the_list_it_opens()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-card");
        var store = Store;
        (int, int, int, int, int, int, int) Lengths(JsonNode c) => ((int)c["due"]!["overdue"]!, (int)c["due"]!["upcoming"]!, (int)c["decisions"]!["overdue"]!,
            (int)c["decisions"]!["upcoming"]!, (int)c["youOwe"]!["count"]!, (int)c["owedToYou"]!["count"]!, (int)c["stale"]!["count"]!);
        var had = Lengths(await Ok(who.App, new { op = "brief" }));
        foreach (var when in new[] { Dues.Day(-3), Dues.Day(-2), Dues.Day(-1), Dues.Tomorrow, Dues.Tomorrow, Dues.Tomorrow, Dues.Day(9), Dues.Day(10) })
            await Dues.Remember(store, who.Alex, $"DEMO: Hexley errand for {when}.", when);
        foreach (var n in new[] { 1, 2, 3, 4 }) await Ledger.Decide(store, who.Alex, $"DEMO: Hexley chose option {n}.", reviewBy: "2020-01-01");
        await Ledger.Decide(store, who.Alex, "DEMO: Hexley chose the van.", reviewBy: Dues.Tomorrow);
        await Ledger.Decide(store, who.Alex, "DEMO: Hexley chose the canopy.");
        var told = await Ledger.Decide(store, who.Alex, "DEMO: Hexley chose the tent.", reviewBy: "2020-01-02");
        await store.RecordOutcome(who.Alex, told, "right", null, Mcp, default);
        for (var n = 1; n <= 6; n++) await Dues.Remember(store, who.Alex, $"DEMO: Alex mends Hexley gate {n}.", null, ["task"]);
        for (var n = 1; n <= 3; n++) await Dues.Remember(store, who.Alex, $"DEMO: Sam promised Hexley ladder {n}.", null, direction: MemoryLoops.In, counterparty: "Sam Hexley");
        foreach (var n in new[] { 1, 2, 3 }) await Briefs.Stale(s, await Dues.Remember(store, who.Alex, $"DEMO: Hexley bakery fact {n}.", null));

        // What no list holds: held with a date and a task tag, in the trash, marked wrong, finished, and an AI change of a memory held now.
        await Dues.Remember(store, who.Alex, Held, Dues.Day(-1), ["task"]);
        var gone = await Dues.Remember(store, who.Alex, "DEMO: a Hexley errand to forget.", Dues.Day(-1), ["task"]);
        await store.Forget(who.Alex, gone, null, Mcp, default);
        var wrong = await Dues.Remember(store, who.Alex, "DEMO: a Hexley errand marked wrong.", Dues.Day(-1), ["task"]);
        await store.SetStatus(who.Alex, wrong, "deprecated", null, Mcp, default);
        var noted = await Dues.Remember(store, who.Alex, "DEMO: a Hexley notice.", null);
        await store.Append(who.Alex, noted, "DEMO: and another line.", null, Mcp, default);
        var first = await Dues.Remember(store, who.Alex, "DEMO: a Hexley diary.", null);
        await store.Append(who.Alex, first, "DEMO: and another line.", null, Mcp, default);
        await store.Update(who.Alex, noted, new(Content: $"{Held} the Hexley notice."), null, Mcp, default);
        Assert.NotNull((await store.Get(who.Alex, noted, default))!.Hold);

        var card = await Agree(who.App);
        Assert.Equal((had.Item1 + 3, had.Item2 + 3, had.Item3 + 4, had.Item4 + 1, had.Item5 + 6, had.Item6 + 3, had.Item7 + 3), Lengths(card));

        // The full list of AI changes keeps the held row, so the card's read is the one that leaves it out.
        var all = await Ok(who.App, new { op = "changes" });
        Assert.True((int)all["count"]! >= (int)card["changes"]!["count"]!);

        // A list holds no held or trashed memory, and shows no more than a preview.
        foreach (var call in new object[] { new { op = "loops", limit = 50 }, new { op = "due", limit = 50 }, new { op = "decisions", state = "all", limit = 50 } })
        {
            var text = (await Ok(who.App, call)).ToJsonString();
            Assert.DoesNotContain("the Hexley notice", text);
            Assert.DoesNotContain("errand to forget", text);
            Assert.DoesNotContain("marked wrong", text);
            Assert.DoesNotContain(Held, text);
        }
        var long1 = await Dues.Remember(store, who.Alex, "DEMO: a long Hexley task. " + new string('x', 400), null, ["task"]);
        var row = (await Ok(who.App, new { op = "loops", limit = 1 }))["loops"]!.AsArray().Single()!;
        Assert.Equal((long1.ToString(), true, AppMemories.ListPreview), ((string)row["id"]!, (bool)row["truncated"]!, ((string)row["preview"]!).Length));

        // Each scope's card is its own lists' too.
        await Agree(who.App, "shared");
    }

    [PostgresFact] public async Task With_scopes_on_the_cards_counts_are_still_its_lists_and_only_the_readers()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var who = await MemoryLogins.Make(s, "plans-scoped");
            var store = Store;
            // The household's own, the household view's to act on.
            var task = await Dues.Remember(store, who.House, "DEMO: the Marlow household returns the hymn books.", Dues.Day(-1), ["task"], "family");
            await Dues.Remember(store, who.House, "DEMO: Sam promised the Marlow household a ladder.", null, scope: "family", direction: MemoryLoops.In, counterparty: "Sam");
            var chose = await Ledger.Decide(store, who.House, "DEMO: the Marlow household chose the van.", scope: "family", reviewBy: "2020-01-01");
            // A person's own, and a person's in Family: the household view is served neither (058's memory_loop_mine).
            var alexes = await Dues.Remember(store, who.Alex, "DEMO: Alex's private Marlow errand.", Dues.Day(-1), ["task"]);
            var alexFamily = await Dues.Remember(store, who.Alex, "DEMO: Alex's Family Marlow errand.", Dues.Day(-1), ["task"], "family");
            var alexesChoice = await Ledger.Decide(store, who.Alex, "DEMO: Alex's private Marlow choice.", reviewBy: "2020-01-01");
            var alexFamilyChoice = await Ledger.Decide(store, who.Alex, "DEMO: Alex's Family Marlow choice.", scope: "family", reviewBy: "2020-01-01");

            var card = await Agree(who.App);
            Assert.Equal((1, 1, 1, 1), ((int)card["due"]!["count"]!, (int)card["youOwe"]!["count"]!, (int)card["owedToYou"]!["count"]!, (int)card["decisions"]!["count"]!));
            await Agree(who.App, "shared");
            var text = string.Concat((await Ok(who.App, new { op = "loops" })).ToJsonString(), (await Ok(who.App, new { op = "due" })).ToJsonString(),
                (await Ok(who.App, new { op = "decisions", state = "all" })).ToJsonString());
            Assert.DoesNotContain("Alex's", text);

            // Another person's, read or not, keeps the author lock: refused as it is through every channel.
            var events = await Events();
            Assert.Equal("unknown", await Refused(who.App, new { op = "resolve_loop", id = alexes, action = "done" }));
            Assert.Equal("forbidden", await Refused(who.App, new { op = "resolve_due", id = alexFamily, action = "clear_date" }));
            Assert.Equal("unknown", await Refused(who.App, new { op = "record_outcome", id = alexesChoice, result = "right" }));
            Assert.Equal("forbidden", await Refused(who.App, new { op = "record_outcome", id = alexFamilyChoice, result = "right" }));
            Assert.Equal(events, await Events());

            // The household view's own goes through.
            await Ok(who.App, new { op = "resolve_loop", id = task, action = "done", expected = 1 });
            await Ok(who.App, new { op = "record_outcome", id = chose, result = "wrong", expected = 1 });
            card = await Agree(who.App);
            Assert.Equal((0, 0, 0), ((int)card["due"]!["count"]!, (int)card["youOwe"]!["count"]!, (int)card["decisions"]!["count"]!));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    [PostgresFact] public async Task A_person_resolves_a_loop_through_the_apps_channel_and_undo_puts_it_back()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-loop");
        var store = Store;
        var owe = await Dues.Remember(store, who.Alex, "DEMO: Alex posts the Quarry letter.", null, ["task", "quarry"]);
        var owed = await Dues.Remember(store, who.Alex, "DEMO: Sam Quarry owes Alex the ladder.", null, direction: MemoryLoops.In, counterparty: "Sam Quarry");
        var plain = await Dues.Remember(store, who.Alex, "DEMO: this Quarry note was tagged by mistake.", null, ["task"]);

        var before = await Ok(who.App, new { op = "loops" });
        var row = before["loops"]!.AsArray().Single(r => (string)r!["id"]! == owed.ToString())!;
        Assert.Equal(("in", "Sam Quarry", 1), ((string)row["direction"]!, (string?)row["counterparty"], (int)row["revision"]!));

        // Done: at the revision read, recorded as the app's, a person's, and it leaves the list once.
        var done = await Ok(who.App, new { op = "resolve_loop", id = owe, action = "done", expected = 1 });
        Assert.Equal(("resolve_loop", "done", "out", 2, false), ((string)done["op"]!, (string)done["action"]!, (string)done["direction"]!, (int)done["revision"]!, (bool)done["unchanged"]!));
        Assert.Equal(["tags"], done["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(("app", (string?)null), await Audited(owe, "resolved"));
        Assert.DoesNotContain(owe.ToString(), (await Ok(who.App, new { op = "loops" })).ToJsonString());
        var memory = (await Ok(who.App, new { op = "get", id = owe }))["memory"]!;
        Assert.Contains("task:done", memory["tags"]!.AsArray().Select(t => (string)t!));

        // A stale second tap is refused and writes nothing; the same word at the revision now answers unchanged.
        var events = await Events();
        Assert.Equal("conflict", await Refused(who.App, new { op = "resolve_loop", id = owe, action = "done", expected = 1 }));
        Assert.True((bool)(await Ok(who.App, new { op = "resolve_loop", id = owe, action = "done", expected = 2 }))["unchanged"]!);
        Assert.Equal(events, await Events());

        // Undo is a change like any other: it is on the list again, at a new revision.
        await Ok(who.App, new { op = "undo", id = owe, expected = 2 });
        Assert.Contains(owe.ToString(), (await Ok(who.App, new { op = "loops", direction = "out" })).ToJsonString());

        // Received is for what is owed to the person; not a task takes the tag off.
        Assert.Equal("invalid", await Refused(who.App, new { op = "resolve_loop", id = plain, action = "received" }));
        var received = await Ok(who.App, new { op = "resolve_loop", id = owed, action = "received" });
        Assert.Equal(("received", "in", "Sam Quarry"), ((string)received["action"]!, (string)received["direction"]!, (string?)received["counterparty"]));
        await Ok(who.App, new { op = "resolve_loop", id = plain, action = "not_a_task" });
        Assert.DoesNotContain("task", (await Ok(who.App, new { op = "get", id = plain }))["memory"]!["tags"]!.AsArray().Select(t => (string)t!));
    }

    [PostgresFact] public async Task A_person_snoozes_and_clears_a_date_through_the_apps_channel()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-due");
        var store = Store;
        var late = await Dues.Remember(store, who.Alex, "DEMO: Alex pays the Thornby rent.", Dues.Day(-1));
        var clear = await Dues.Remember(store, who.Alex, "DEMO: Alex collects the Thornby keys.", Dues.Day(-1));
        var decided = await Ledger.Decide(store, who.Alex, "DEMO: Alex chose the Thornby van.", reviewBy: "2020-01-01");

        var first = await Ok(who.App, new { op = "due", window = "overdue" });
        var row = first["due"]!.AsArray().Single(r => (string)r!["id"]! == late.ToString())!;
        Assert.Equal(("overdue", "explicit", "wake"), ((string)row["window"]!, (string)row["source"]!, (string)row["kind"]!));
        Assert.Equal("Europe/London", (string)first["zone"]!);
        var (overdue, later) = ((int)first["overdue"]!, (int)first["later"]!);

        // Snooze past the 48 hours: gone from the card's windows, on the later list, recorded as the app's.
        var until = Dues.Day(9) + "T09:00";
        var snoozed = await Ok(who.App, new { op = "resolve_due", id = late, action = "snooze", until, expected = 1 });
        Assert.Equal(("resolve_due", "snooze", until.Replace('T', ' '), 2), ((string)snoozed["op"]!, (string)snoozed["action"]!, (string)snoozed["due"]!["local"]!, (int)snoozed["revision"]!));
        Assert.NotNull(snoozed["before"]);
        Assert.Equal("explicit", (string)snoozed["due"]!["source"]!);
        Assert.Equal(("app", (string?)null), await Audited(late, "due_changed"));
        var now = await Ok(who.App, new { op = "due", window = "later" });
        Assert.Equal((overdue - 1, later + 1), ((int)now["overdue"]!, (int)now["later"]!));
        Assert.Contains(late.ToString(), now.ToJsonString());

        // A person moves a date they already put off, which a resolve would answer unchanged.
        var again = Dues.Day(12) + "T09:00";
        var moved = await Ok(who.App, new { op = "resolve_due", id = late, action = "snooze", until = again, expected = 2 });
        Assert.Equal((again.Replace('T', ' '), false), ((string)moved["due"]!["local"]!, (bool)moved["unchanged"]!));

        // Undo puts the date back.
        await Ok(who.App, new { op = "undo", id = late, expected = 3 });
        Assert.Equal(until.Replace('T', ' '), (string)(await Ok(who.App, new { op = "due", window = "later" }))["due"]!.AsArray().Single(r => (string)r!["id"]! == late.ToString())!["local"]!);

        // Clear: no date, off the list; nothing else in it changes. A second clear at the revision now answers unchanged.
        var cleared = await Ok(who.App, new { op = "resolve_due", id = clear, action = "clear_date", expected = 1 });
        Assert.True(cleared.AsObject().ContainsKey("due"));
        Assert.Null(cleared["due"]);
        Assert.Equal(("app", (string?)null), await Audited(clear, "due_changed"));
        Assert.DoesNotContain(clear.ToString(), (await Ok(who.App, new { op = "due" })).ToJsonString());
        Assert.True((bool)(await Ok(who.App, new { op = "resolve_due", id = clear, action = "clear_date", expected = 2 }))["unchanged"]!);

        // A snooze keeps to its rules; a decision's date is its review date (D-214). Nothing is written.
        var events = await Events();
        Assert.Equal("invalid", await Refused(who.App, new { op = "resolve_due", id = clear, action = "snooze", until = "2020-01-01" }));
        Assert.Equal("invalid", await Refused(who.App, new { op = "resolve_due", id = clear, action = "snooze", until = "next week" }));
        Assert.Equal("invalid", await Refused(who.App, new { op = "resolve_due", id = decided, action = "snooze", until = Dues.Day(9) }));
        Assert.Equal("conflict", await Refused(who.App, new { op = "resolve_due", id = clear, action = "clear_date", expected = 1 }));
        Assert.Equal(events, await Events());
    }

    [PostgresFact] public async Task A_person_records_a_decisions_outcome_and_corrects_it()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-outcome");
        var store = Store;
        var chose = await store.Decide(who.Alex, new("DEMO: Alex chose the Oakhurst allotment."), new(0.7, MemoryDecisions.Stated, "2020-01-01"), Mcp, default);
        var id = chose.Write.Id;
        var unsure = await Ledger.Decide(store, who.Alex, "DEMO: Alex chose the Oakhurst bus.", confidence: null, reviewBy: "2020-01-01");

        var listed = await Ok(who.App, new { op = "decisions", state = "due" });
        var row = listed["decisions"]!.AsArray().Single(r => (string)r!["id"]! == id.ToString())!;
        Assert.Equal((0.7m, "stated", null, "overdue", true, false), ((decimal)row["confidence"]!, (string?)row["confidenceSource"], (string?)row["outcome"], (string)row["window"]!, (bool)row["current"]!, (bool)row["edited"]!));
        Assert.Null((string?)listed["decisions"]!.AsArray().Single(r => (string)r!["id"]! == unsure.ToString())!["confidenceSource"]);
        var (dueNow, resolved, scored) = ((int)listed["dueNow"]!, (int)listed["resolved"]!, (int)listed["scored"]!);

        // Right: off the review list, with its outcome on the resolved list, scored, recorded as the app's.
        var right = await Ok(who.App, new { op = "record_outcome", id, result = "right", expected = 1 });
        Assert.Equal(("record_outcome", "right", null, 2, false), ((string)right["op"]!, (string)right["result"]!, (string?)right["previous"], (int)right["revision"]!, (bool)right["reviewsDone"]!));
        Assert.Null(right["reviewAt"]);
        Assert.Equal(("app", (string?)null), await Audited(id, "outcome"));
        var now = await Ok(who.App, new { op = "decisions", state = "resolved" });
        Assert.Equal((dueNow - 1, resolved + 1, scored + 1), ((int)now["dueNow"]!, (int)now["resolved"]!, (int)now["scored"]!));
        Assert.Equal("right", (string)now["decisions"]!.AsArray().Single(r => (string)r!["id"]! == id.ToString())!["outcome"]!);

        // A repeat answers unchanged; a person corrects it to another word, which a resolve would not.
        Assert.True((bool)(await Ok(who.App, new { op = "record_outcome", id, result = "right", expected = 2 }))["unchanged"]!);
        var wrong = await Ok(who.App, new { op = "record_outcome", id, result = "wrong", expected = 2 });
        Assert.Equal(("right", 3), ((string?)wrong["previous"], (int)wrong["revision"]!));
        Assert.Equal("wrong", (string)(await Ok(who.App, new { op = "decisions", state = "resolved" }))["decisions"]!.AsArray().Single(r => (string)r!["id"]! == id.ToString())!["outcome"]!);

        // Undo puts the outcome back; a stale revision is refused and writes nothing.
        await Ok(who.App, new { op = "undo", id, expected = 3 });
        Assert.Equal("right", (string)(await Ok(who.App, new { op = "decisions", state = "resolved" }))["decisions"]!.AsArray().Single(r => (string)r!["id"]! == id.ToString())!["outcome"]!);
        var events = await Events();
        Assert.Equal("conflict", await Refused(who.App, new { op = "record_outcome", id, result = "mixed", expected = 2 }));
        Assert.Equal(events, await Events());

        // Too early to tell asks again 90 days on, twice, and the third stops asking; it is resolved, never scored.
        var asked = await Ok(who.App, new { op = "record_outcome", id = unsure, result = "unknown" });
        Assert.NotNull(asked["reviewAt"]);
        Assert.Equal((1, false), ((int)asked["rearms"]!, (bool)asked["reviewsDone"]!));
        await Ok(who.App, new { op = "record_outcome", id = unsure, result = "unknown" });
        var stopped = await Ok(who.App, new { op = "record_outcome", id = unsure, result = "unknown" });
        Assert.Null(stopped["reviewAt"]);
        Assert.True((bool)stopped["reviewsDone"]!);
        var after = await Ok(who.App, new { op = "decisions", state = "scored" });
        Assert.DoesNotContain(unsure.ToString(), after.ToJsonString());

        // What is no decision takes no outcome.
        var note = await Dues.Remember(store, who.Alex, "DEMO: an Oakhurst note.", null);
        Assert.Equal("invalid", await Refused(who.App, new { op = "record_outcome", id = note, result = "right" }));
    }

    [PostgresFact] public async Task A_held_or_wrong_memory_takes_none_of_a_persons_steps()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-held");
        var store = Store;
        var held = await Dues.Remember(store, who.Alex, Held, Dues.Day(-1), ["task"]);
        Assert.NotNull((await store.Get(who.Alex, held, default))!.Hold);
        var wrong = await Dues.Remember(store, who.Alex, "DEMO: a Fenwick errand marked wrong.", Dues.Day(-1), ["task"]);
        await store.SetStatus(who.Alex, wrong, "deprecated", null, Mcp, default);
        var heldChoice = await Ledger.Decide(store, who.Alex, $"{Held} the Fenwick choice.", reviewBy: "2020-01-01");
        Assert.NotNull((await store.Get(who.Alex, heldChoice, default))!.Hold);
        var wrongChoice = await Ledger.Decide(store, who.Alex, "DEMO: a Fenwick choice marked wrong.", reviewBy: "2020-01-01");
        await store.SetStatus(who.Alex, wrongChoice, "deprecated", null, Mcp, default);

        var events = await Events();
        foreach (var (id, code) in new[] { (held, "held"), (wrong, "retracted") })
        {
            Assert.Equal(code, await Refused(who.App, new { op = "resolve_loop", id, action = "done" }));
            Assert.Equal(code, await Refused(who.App, new { op = "resolve_due", id, action = "clear_date" }));
            Assert.Equal(code, await Refused(who.App, new { op = "resolve_due", id, action = "snooze", until = Dues.Day(5) }));
        }
        foreach (var (id, code) in new[] { (heldChoice, "held"), (wrongChoice, "retracted") })
            foreach (var result in new[] { "right", "unknown" })
                Assert.Equal(code, await Refused(who.App, new { op = "record_outcome", id, result }));
        Assert.Equal(events, await Events());
    }

    // The channel is the app's and the app's alone: an AI client's login that reached this handler would write as the app, which the
    // database refuses it, and its token never reaches this handler at all. Its own tools keep its own channel.
    [PostgresFact] public async Task No_AI_clients_token_reaches_a_persons_step_through_the_app()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-ai");
        var store = Store;
        var task = await Dues.Remember(store, who.Alex, "DEMO: Alex posts the Ashdown parcel.", Dues.Day(-1), ["task"]);
        var decided = await Ledger.Decide(store, who.Alex, "DEMO: Alex chose the Ashdown van.", reviewBy: "2020-01-01");

        var events = await Events();
        foreach (var ai in new[] { who.Alex, who.House })
            foreach (var step in new object[]
                     {
                         new { op = "resolve_loop", id = task, action = "done" },
                         new { op = "resolve_due", id = task, action = "clear_date" },
                         new { op = "resolve_due", id = task, action = "snooze", until = Dues.Day(5) },
                         new { op = "record_outcome", id = decided, result = "right" },
                     })
                Assert.Equal("invalid", await Refused(ai, step));
        Assert.Equal(events, await Events());

        // Through the door, a real AI client's token: no tool of the app's is listed or answers.
        var config = MemoryToolSettings.With("tools: on, capsules: on, share: on");
        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config));
        var file = Path.Combine(Path.GetTempPath(), "rhythm-plans-" + Guid.NewGuid().ToString("N"), "token");
        await new Principals(s).Create("plans-ai-client", "harness", null, "alex", file, default);
        var token = await File.ReadAllTextAsync(file);
        Directory.Delete(Path.GetDirectoryName(file)!, true);
        await using var client = await McpEngine.Connect(engine.Handler(), "Bearer " + token);
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.DoesNotContain(AppMemories.Name, names);
        var answer = await Record.ExceptionAsync(async () =>
        {
            var called = await client.CallToolAsync(AppMemories.Name, new Dictionary<string, object?> { ["op"] = "resolve_loop", ["id"] = task, ["action"] = "done" });
            Assert.True(called.IsError);
        });
        Assert.True(answer is null || answer is ModelContextProtocol.McpException, answer?.ToString());
        Assert.Equal(events, await Events());

        // The AI client's own tool stays its own, in its own channel, so the audit trail tells the two apart.
        var mine = await Dues.Remember(store, who.Alex, "DEMO: Alex posts the Ashdown letter.", null, ["task"]);
        var done = await client.CallToolAsync("resolve_loop", new Dictionary<string, object?> { ["id"] = mine, ["action"] = "done" });
        Assert.NotEqual(true, done.IsError);
        Assert.Equal("mcp", (await Audited(mine, "resolved")).Channel);
        TestSettings.Use();
    }
}

// Calibration, in a database of its own: the figures depend on every scored decision there is.
public sealed class AppMemoryPlansCalibrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    MemoryStore Store => new(fixture.Source);

    async Task<JsonNode> Ok(Guid app, object arguments)
    {
        var (status, content) = await AppMemoryCalls.Call(AppMemoryCalls.Services(MemoryToolSettings.On, fixture.Db), app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments),
            new AppHealth(fixture.Db, MemoryToolSettings.On));
        Assert.Equal(200, status);
        Assert.True((bool)content!["ok"]!, content.ToJsonString());
        return content;
    }

    [PostgresFact] public async Task Calibration_says_what_it_rests_on_and_shows_no_figure_below_its_minimum()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "plans-calibration");
        var store = Store;
        Assert.Equal(0, (int)(await Ok(who.App, new { op = "calibration" }))["n"]!);

        for (var i = 0; i < MemoryDecisions.MinDecisions - 1; i++)
            await store.RecordOutcome(who.Alex, await Ledger.Decide(store, who.Alex, $"DEMO: Calder choice {i}.", confidence: 0.8), i % 2 == 0 ? "right" : "wrong", null, Mcp, default);
        var below = await Ok(who.App, new { op = "calibration" });
        Assert.False((bool)below["ready"]!);
        Assert.Equal(MemoryDecisions.MinDecisions - 1, (int)below["n"]!);
        Assert.Equal(("not_ready", null, null, null), ((string)below["kind"]!, (double?)below["brier"], (double?)below["gap"], (string?)below["direction"]));
        Assert.All(below["bands"]!.AsArray(), b => Assert.Equal((false, null, null), ((bool)b!["shown"]!, (double?)b["meanConfidence"], (double?)b["hitRate"])));

        await store.RecordOutcome(who.Alex, await Ledger.Decide(store, who.Alex, "DEMO: the last Calder choice.", confidence: 0.8), "right", null, Mcp, default);
        var ready = await Ok(who.App, new { op = "calibration" });
        Assert.True((bool)ready["ready"]!);
        Assert.Equal(MemoryDecisions.MinDecisions, (int)ready["n"]!);
        Assert.NotNull(ready["brier"]);
        var band = ready["bands"]!.AsArray().Single(b => (string)b!["band"]! == "80-89")!;
        Assert.Equal((true, MemoryDecisions.MinDecisions), ((bool)band["shown"]!, (int)band["n"]!));
        Assert.Equal((string?)(await Ok(who.App, new { op = "brief" }))["calibration"]!["line"], (string?)ready["line"]);
        Assert.Equal(MemoryDecisions.MinDecisions, (await Ok(who.App, new { op = "decisions", state = "scored" }))["scored"]!.GetValue<int>());

        // A source narrows what it rests on, and says so.
        var inferred = await Ok(who.App, new { op = "calibration", source = "inferred" });
        Assert.Equal(("inferred", 0, false), ((string)inferred["source"]!, (int)inferred["n"]!, (bool)inferred["ready"]!));
        Assert.Equal("stated", (string)(await Ok(who.App, new { op = "calibration", source = "stated" }))["source"]!);
    }
}
