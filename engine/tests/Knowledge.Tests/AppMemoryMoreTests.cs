using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// The memory UI's last pieces (SB-P6-01, run 22; Mcp/AppMemoriesStanding.cs, MemoryBulk.cs, MemoryStore.Filtered): words to look for in
// the Memories list and its count, a person's reading and stopping of standing instructions, and a review queue's one-word step for
// several items. Every step goes through app_memories, the app's channel, a person's; none of it is listed or taken with memory.tools
// off, and an AI client's token reaches none of it. Fictional people only (alex, sam, the household); every text is DEMO.
public sealed class AppMemoryMoreTests
{
    static readonly Guid Id = Guid.Parse("6d3c2a10-0000-4000-8000-00000000f301");
    static readonly Guid Other = Guid.Parse("6d3c2a10-0000-4000-8000-00000000f302");

    static bool Takes(object arguments) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true);

    static readonly object[] Steps =
    [
        new { op = "list", query = "ferry" }, new { op = "tags", query = "ferry" }, new { op = "standing" }, new { op = "stop_standing", id = Id },
        new { op = "resolve_all", action = "dismiss_insight", items = new[] { new { id = Id } } },
    ];

    [Fact] public void Each_new_op_takes_exactly_its_arguments()
    {
        foreach (var step in Steps) Assert.True(Takes(step), JsonSerializer.Serialize(step));

        // A list's words, beside every other filter; at most 200 characters as sent, as the listing's maxLength counts them, never only
        // spaces. The store trims what the door takes.
        Assert.True(Takes(new { op = "list", query = "Wendle ferry", kind = "semantic", status = "draft", tag = "rota", project = "wendle", cursor = "abc", limit = 50 }));
        Assert.True(Takes(new { op = "tags", query = new string('x', 200), kind = "episodic" }));
        Assert.True(Takes(new { op = "list", query = " " + new string('x', 199) }));
        foreach (var query in new object[] { "", "   ", new string('x', 201), " " + new string('x', 200), 7, new[] { "ferry" } })
        {
            Assert.False(Takes(new { op = "list", query }), JsonSerializer.Serialize(query));
            Assert.False(Takes(new { op = "tags", query }), JsonSerializer.Serialize(query));
        }
        Assert.False(Takes(new { op = "list", kind = "procedural" }));

        Assert.True(Takes(new { op = "standing", scope = "shared", project = "wendle", cursor = "abc", limit = 50 }));
        Assert.False(Takes(new { op = "standing", limit = 51 }));
        Assert.False(Takes(new { op = "standing", query = "ferry" }));
        Assert.True(Takes(new { op = "stop_standing", id = Id, expected = 2 }));
        Assert.False(Takes(new { op = "stop_standing" }));
        Assert.False(Takes(new { op = "stop_standing", id = Id, key = "stop-standing-key-1" }));

        // Several: only the one-word steps, 1 to 50 items, each its id and at most the revision read.
        foreach (var action in MemoryResolve.BulkActions) Assert.True(Takes(new { op = "resolve_all", action, items = new[] { new { id = Id } } }), action);
        Assert.True(Takes(new { op = "resolve_all", action = "still_true", items = new object[] { new { id = Id, expected = 3 }, new { id = Other, expected = 1 } } }));
        Assert.True(Takes(new { op = "resolve_all", action = "confirm_insight", items = Enumerable.Range(0, 50).Select(_ => new { id = Id }).ToArray() }));
        foreach (var action in new[] { "release", "keep_copy", "settle_keep_both", "settle_replace", "use_candidate", "done", "snooze", "stop_standing", "confirm", "anything" })
            Assert.False(Takes(new { op = "resolve_all", action, items = new[] { new { id = Id } } }), action);
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight", items = Array.Empty<object>() }));
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight", items = Enumerable.Range(0, 51).Select(_ => new { id = Id }).ToArray() }));
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight", items = new[] { new { id = "not-an-id" } } }));
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight", items = new[] { new { id = Id, note = "DEMO" } } }));
        Assert.False(Takes(new { op = "resolve_all", action = "still_true", items = new[] { new { id = Id, expected = 0 } } }));
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight", items = new[] { new { id = Id } }, note = "DEMO" }));
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight" }));
        Assert.False(Takes(new { op = "resolve_all", items = new[] { new { id = Id } } }));
        Assert.False(Takes(new { op = "resolve_all", action = "dismiss_insight", memories = new[] { new { id = Id, expected = 1 } } }));

        // No op names its channel or its client: the handler fixes both.
        foreach (var call in new object[]
                 {
                     new { op = "standing", channel = "app" }, new { op = "stop_standing", id = Id, client = "DEMO" },
                     new { op = "resolve_all", action = "dismiss_insight", items = new[] { new { id = Id } }, channel = "cli" },
                     new { op = "list", query = "ferry", channel = "mcp" },
                 })
            Assert.False(Takes(call));

        // The listing names every new op and the words each takes.
        var listing = JsonSerializer.SerializeToNode(AppMemories.Listing())!["inputSchema"]!["properties"]!;
        var ops = listing["op"]!["enum"]!.AsArray().Select(o => (string)o!).ToArray();
        foreach (var op in new[] { "standing", "stop_standing", "resolve_all" }) Assert.Contains(op, ops);
        Assert.Equal(MemoryStore.QueryMax, (int)listing["query"]!["maxLength"]!);
        Assert.Equal(MemoryStore.ReleaseAllMax, (int)listing["items"]!["maxItems"]!);
        Assert.Equal(["id", "expected"], listing["items"]!["items"]!["properties"]!.AsObject().Select(p => p.Key));
        Assert.Contains("standing", AppMemories.Reads);
        Assert.Contains("resolve_all", AppMemories.Writes);
        Assert.Contains("stop_standing", AppMemories.Writes);
    }

    [Fact] public async Task With_memory_tools_off_the_new_ops_answer_as_before()
    {
        foreach (var config in new[] { TestSettings.Use(), MemoryToolSettings.With("tools: off"), MemoryToolSettings.With(""), MemoryToolSettings.With("capsules: on, share: on") })
            foreach (var step in Steps)
            {
                var (status, content) = await AppMemoryCalls.Call(AppMemoryCalls.Services(config), Guid.NewGuid(), AppMemoryCalls.Body(AppEndpoint.Memories, step));
                Assert.Equal((403, "forbidden"), (status, (string)content!["error"]!["code"]!));
                Assert.False(AppEndpoint.Allowed(AppMemoryCalls.Request(step)));
            }
        TestSettings.Use();
    }
}

public sealed class AppMemoryMoreDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    NpgsqlDataSource S => fixture.Source;
    MemoryStore Store => new(fixture.Source);

    async Task<(int Status, JsonNode? Content)> App(Guid app, object arguments)
    {
        var config = MemoryToolSettings.On;
        var services = new ServiceCollection().AddSingleton<RhythmConfig>(config).AddSingleton(fixture.Db).AddSingleton(new InsightStore(S)).BuildServiceProvider();
        return await AppMemoryCalls.Call(services, app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments), new AppHealth(fixture.Db, config));
    }

    async Task<JsonNode> Ok(Guid app, object arguments)
    {
        var (status, content) = await App(app, arguments);
        Assert.Equal(200, status);
        Assert.True((bool)content!["ok"]!, content.ToJsonString());
        return content;
    }

    async Task<JsonNode> Refused(Guid app, object arguments)
    {
        var (status, content) = await App(app, arguments);
        Assert.Equal(200, status);
        Assert.False((bool)content!["ok"]!, content.ToJsonString());
        return content;
    }

    // A list walked to its end a page of two at a time: its items' ids.
    async Task<List<string>> Walk(Guid app, object arguments, string items)
    {
        var all = new List<string>();
        string? cursor = null;
        var asked = JsonSerializer.SerializeToNode(arguments)!.AsObject();
        do
        {
            var call = (JsonObject)asked.DeepClone();
            call["limit"] = 2;
            if (cursor is not null) call["cursor"] = cursor;
            var page = await Ok(app, call);
            all.AddRange(page[items]!.AsArray().Select(i => (string)i!["id"]!));
            cursor = (string?)page["next"];
        } while (cursor is not null);
        return all;
    }

    // The words a list looks for narrow it and its count alike, whatever the case, with every other filter; a held memory's words
    // never select it, the store refuses words that are only spaces, and the AI client's own list reads as it did.
    [PostgresFact] public async Task Words_narrow_the_list_and_its_count_alike()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "more-query");
        var tag = $"q{Guid.NewGuid():N}"[..12];
        var ferry = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ferry.Add((await Store.Create(who.Alex, new($"DEMO: the {tag} Wendle FERRY {i} runs at dawn.", Kind: i == 0 ? "episodic" : "semantic"), Mcp, default)).Id);
        await Store.Create(who.Alex, new($"DEMO: the {tag} Wendle mill turns slowly."), Mcp, default);
        // Held for review: its words name the same, and never select it.
        var held = (await Store.Create(who.Sam, new($"{Briefs.Held} about the {tag} wendle ferry"), Mcp, default)).Id;
        Assert.NotNull((await Store.Get(who.App, held, default))!.Hold);

        var words = $"{tag} wendle ferry";
        var listed = await Walk(who.App, new { op = "list", query = words.ToUpperInvariant() }, "memories");
        Assert.Equal(ferry.Select(f => f.ToString()).Reverse(), listed);
        Assert.Equal(3, (int)(await Ok(who.App, new { op = "tags", query = $"  {words}  " }))["count"]!);
        Assert.DoesNotContain(held.ToString(), listed);

        // With the kind filter too, each narrowing.
        Assert.Equal([ferry[0].ToString()], await Walk(who.App, new { op = "list", query = words, kind = "episodic" }, "memories"));
        Assert.Equal(1, (int)(await Ok(who.App, new { op = "tags", query = words, kind = "episodic" }))["count"]!);
        Assert.Equal(2, (int)(await Ok(who.App, new { op = "tags", query = words, kind = "semantic" }))["count"]!);
        // Words nobody wrote: an empty list, and a count of none.
        Assert.Empty((await Ok(who.App, new { op = "list", query = $"{tag} nobody wrote this" }))["memories"]!.AsArray());
        Assert.Equal(0, (int)(await Ok(who.App, new { op = "tags", query = $"{tag} nobody wrote this" }))["count"]!);
        // No wildcards: what is typed is what is looked for.
        Assert.Equal(0, (int)(await Ok(who.App, new { op = "tags", query = $"{tag} wendle%" }))["count"]!);
        Assert.Equal(0, (int)(await Ok(who.App, new { op = "tags", query = $"{tag} w_ndle" }))["count"]!);

        // The store refuses only spaces and words past the bound, and every caller naming no words lists as before.
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.List(who.App, new MemoryFilter(Query: "   ", Limit: 5), default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Count(who.App, new MemoryFilter(Query: new string('x', MemoryStore.QueryMax + 1)), default)));
        var all = await Store.Count(who.App, new MemoryFilter(), default);
        Assert.True(all >= 5);
        Assert.Equal(all, await Store.Count(who.App, new MemoryFilter(Query: null), default));
    }

    // With scopes on another person's private words are never found, nor counted, by the household view or the other person.
    [PostgresFact] public async Task With_scopes_on_words_find_only_what_the_reader_reads()
    {
        await Scoped.Mode(S, "on");
        try
        {
            await MemoryLogins.Family(S);
            var who = await MemoryLogins.Make(S, "more-query-scope");
            var tag = $"s{Guid.NewGuid():N}"[..12];
            var own = (await Store.Create(who.Alex, new($"DEMO: Alex's own {tag} sabbatical plan.", Scope: "private"), Mcp, default)).Id;
            var family = (await Store.Create(who.Alex, new($"DEMO: the Family {tag} sabbatical rota.", Scope: "shared"), Mcp, default)).Id;

            Assert.Equal([family.ToString()], await Walk(who.App, new { op = "list", query = $"{tag} sabbatical" }, "memories"));
            Assert.Equal(1, (int)(await Ok(who.App, new { op = "tags", query = $"{tag} sabbatical" }))["count"]!);
            Assert.Equal(0, (int)(await Ok(who.App, new { op = "tags", query = $"own {tag}" }))["count"]!);
            Assert.Equal(0, await Store.Count(who.Sam, new MemoryFilter(Query: $"own {tag}"), default));
            Assert.Equal(1, await Store.Count(who.Alex, new MemoryFilter(Query: $"own {tag}"), default));
            Assert.Equal(own, Assert.Single((await Store.List(who.Alex, new MemoryFilter(Query: $"own {tag}"), default)).Items).Id);
        }
        finally { await Scoped.Mode(S, "off"); }
    }

    // A memory's kind, importance and volatility from its reader: each changed, each cleared, recorded as the app's, a revision read
    // since refused; the AI clients' tools keep their own update, as before.
    [PostgresFact] public async Task A_person_changes_a_memorys_kind_importance_and_volatility_through_the_app()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "more-kind");
        var id = (await Store.Create(who.Alex, new("DEMO: the Wendle bakery opens at eight."), Mcp, default)).Id;
        var set = await Ok(who.App, new { op = "update", id, kind = "semantic", importance = 4, volatility = "state", expected = 1 });
        Assert.Equal(("update", 2), ((string)set["op"]!, (int)set["revision"]!));
        Assert.Equal(["importance", "kind", "volatility"], set["changed"]!.AsArray().Select(c => (string)c!).Order());
        var read = (await Ok(who.App, new { op = "get", id }))["memory"]!;
        Assert.Equal(("semantic", 4, "state"), ((string)read["kind"]!, (int)read["importance"]!, (string)read["volatility"]!));
        Assert.Equal("updated:app", (string?)await MemoryRows.Scalar(S, "SELECT event||':'||channel FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", id)));

        Assert.Equal("conflict", (string)(await Refused(who.App, new { op = "update", id, importance = 2, expected = 1 }))["code"]!);
        var cleared = await Ok(who.App, new { op = "update", id, clear = new[] { "kind", "importance", "volatility" }, expected = 2 });
        Assert.Equal(3, (int)cleared["revision"]!);
        var plain = (await Ok(who.App, new { op = "get", id }))["memory"]!;
        Assert.Equal((null, null, null), ((string?)plain["kind"], (int?)plain["importance"], (string?)plain["volatility"]));
        // Undo puts back the last change, as for any other.
        await Ok(who.App, new { op = "undo", id, expected = 3 });
        Assert.Equal(4, (int)(await Ok(who.App, new { op = "get", id }))["memory"]!["importance"]!);
    }

    // Standing instructions in the app (D-236's proposal): the list its count is the length of, oldest first, a held one without its
    // words; a person's stop through the same checked writer, once, at the revision read, taken back by undo; recorded as the app's.
    [PostgresFact] public async Task A_person_reads_and_stops_standing_instructions_through_the_app()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "more-standing");
        var mine = new List<Guid>();
        for (var i = 0; i < 3; i++)
            mine.Add((await Store.RememberStanding(who.Alex, new($"DEMO: when the Wendle rota {i} comes up, list Sam's evenings first."), Mcp, default)).Id);
        var held = (await Store.RememberStanding(who.Sam, new($"{Briefs.Held} as a standing rota note"), Mcp, default)).Id;

        var first = await Ok(who.App, new { op = "standing", limit = 50 });
        var count = (int)first["count"]!;
        var walked = await Walk(who.App, new { op = "standing" }, "standing");
        Assert.Equal(count, walked.Count);
        Assert.Equal(count, (int)(await Ok(who.App, new { op = "standing" }))["count"]!);
        Assert.Equal(mine.Select(m => m.ToString()), walked.Where(w => mine.Select(m => m.ToString()).Contains(w)));
        var rows = first["standing"]!.AsArray().Select(r => r!).ToList();
        var shut = rows.First(r => (string)r["id"]! == held.ToString());
        Assert.Equal((null, false, "held"), ((string?)shut["content"], (bool)shut["fires"]!, (string)shut["reason"]!));
        Assert.Equal("instruction", (string)shut["held"]!["reason"]!);
        var open = rows.First(r => (string)r["id"]! == mine[0].ToString());
        Assert.Equal((true, "draft"), ((bool)open["fires"]!, (string)open["status"]!));
        Assert.StartsWith("DEMO: when the Wendle rota 0", (string)open["content"]!);
        Assert.Equal((MemoryStanding.MaxInScope, MemoryStanding.MaxFires, false), ((int)first["maxInScope"]!, (int)first["maxFires"]!, (bool)first["served"]!));
        // The same list the AI client's list_standing reads.
        var tool = await Store.Standing(who.App, null, 50, null, default);
        Assert.Equal((tool.Total, tool.Firing, tool.Held, tool.OverLimit), ((int)first["count"]!, (int)first["firing"]!, (int)first["held"]!, (int)first["overLimit"]!));

        // A stop at a revision read since is refused; at the one read, it stops, once.
        Assert.Equal("conflict", (string)(await Refused(who.App, new { op = "stop_standing", id = mine[1], expected = 9 }))["code"]!);
        var stopped = await Ok(who.App, new { op = "stop_standing", id = mine[1], expected = 1 });
        Assert.Equal(("stop_standing", 2, false), ((string)stopped["op"]!, (int)stopped["revision"]!, (bool)stopped["unchanged"]!));
        Assert.Equal(["standing"], stopped["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("standing_stopped:app", (string?)await MemoryRows.Scalar(S, "SELECT event||':'||channel FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", mine[1])));
        Assert.Equal(count - 1, (int)(await Ok(who.App, new { op = "standing" }))["count"]!);
        Assert.DoesNotContain(mine[1].ToString(), await Walk(who.App, new { op = "standing" }, "standing"));
        Assert.True((bool)(await Ok(who.App, new { op = "stop_standing", id = mine[1] }))["unchanged"]!);
        // A held one is refused, and a memory that never was one.
        Assert.Equal("held", (string)(await Refused(who.App, new { op = "stop_standing", id = held }))["code"]!);
        var plain = (await Store.Create(who.Alex, new("DEMO: the Wendle mill turns slowly."), Mcp, default)).Id;
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "stop_standing", id = plain }))["code"]!);
        // Undo starts it again, and the list holds it once more.
        await Ok(who.App, new { op = "undo", id = mine[1], expected = 2 });
        Assert.Equal(count, (int)(await Ok(who.App, new { op = "standing" }))["count"]!);
        Assert.Contains(mine[1].ToString(), await Walk(who.App, new { op = "standing" }, "standing"));
    }

    // A review queue's one-word step for several (upstream's bulk resolve): each item through the one door, each answered on its own, one
    // refused leaving the others done, each counted off its queue once and audited once; a stale revision, an item that is not waiting
    // and one this reader may not touch each say so; a step that needs more than a word is refused before anything is read.
    [PostgresFact] public async Task A_person_resolves_several_at_once_each_answered_on_its_own()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "more-several");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        var insights = new List<Guid>();
        for (var i = 0; i < 3; i++) insights.Add(await StreamCalls.Save(ai, $"DEMO · the Wendle several insight {i}"));
        await Ok(who.App, new { op = "resolve", id = insights[2], action = "confirm_insight" });
        var queues = await Ok(who.App, new { op = "queues" });
        var audit = await Resolves.Audit(S);

        var dismissed = await Ok(who.App, new { op = "resolve_all", action = "dismiss_insight", items = new object[] { new { id = insights[0] }, new { id = insights[1] }, new { id = insights[2] }, new { id = Guid.NewGuid() } } });
        Assert.Equal(("resolve_all", "dismiss_insight"), ((string)dismissed["op"]!, (string)dismissed["action"]!));
        var results = dismissed["results"]!.AsArray().Select(r => r!).ToList();
        Assert.Equal(["resolved", "resolved", "unchanged", "unknown"], results.Select(r => (string)r["result"]!));
        Assert.Equal(("dismissed", "closed"), ((string)results[0]["review"]!, (string)results[0]["view"]!));
        Assert.Equal("confirmed", (string)results[2]["review"]!);
        Assert.Equal(audit + 2, await Resolves.Audit(S));
        Assert.Equal((int)queues["insights"]! - 2, (int)(await Ok(who.App, new { op = "queues" }))["insights"]!);
        foreach (var id in insights.Take(2))
            Assert.Equal("dismissed:app", (string?)await MemoryRows.Scalar(S, "SELECT event||':'||channel FROM insight_events WHERE insight_id=@id ORDER BY id DESC LIMIT 1", ("id", id)));
        // Again: nothing waits, so nothing is written.
        var state = await Resolves.State(S);
        Assert.All((await Ok(who.App, new { op = "resolve_all", action = "confirm_insight", items = new[] { new { id = insights[0] } } }))["results"]!.AsArray(),
            r => Assert.Equal("unchanged", (string)r!["result"]!));
        Assert.Equal(state, await Resolves.State(S));

        // Still true for several, each at the revision read: one read since stays, the others leave the queue.
        var facts = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = (await Store.Create(who.Alex, new($"DEMO: the Wendle bakery {i} several opens at eight."), Mcp, default)).Id;
            await Briefs.Stale(S, id);
            facts.Add(id);
        }
        var stale = (int)(await Ok(who.App, new { op = "queues" }))["stale"]!;
        var checkedAll = await Ok(who.App, new { op = "resolve_all", action = "still_true", items = new object[] { new { id = facts[0], expected = 1 }, new { id = facts[1], expected = 7 }, new { id = facts[2], expected = 1 } } });
        var checks = checkedAll["results"]!.AsArray().Select(r => r!).ToList();
        Assert.Equal(["resolved", "conflict", "resolved"], checks.Select(r => (string)r["result"]!));
        Assert.Equal((1, 1), ((int)checks[0]["revision"]!, (int)checks[1]["revision"]!));
        Assert.Equal(stale - 2, (int)(await Ok(who.App, new { op = "queues" }))["stale"]!);
        Assert.Contains(facts[1].ToString(), await Walk(who.App, new { op = "stale" }, "stale"));

        // Pairs declined at once, for good; a decided one says so.
        var pairs = new List<Guid>();
        for (var i = 0; i < 2; i++) pairs.Add(await Resolves.Candidate(S, Store, who.Alex, $"several{i}"));
        var candidates = (int)(await Ok(who.App, new { op = "queues" }))["candidates"]!;
        var declined = await Ok(who.App, new { op = "resolve_all", action = "reject_candidate", items = pairs.Select(p => new { id = p }).ToArray() });
        Assert.All(declined["results"]!.AsArray(), r => Assert.Equal(("resolved", "rejected"), ((string)r!["result"]!, (string)r["candidate"]!)));
        Assert.Equal(candidates - 2, (int)(await Ok(who.App, new { op = "queues" }))["candidates"]!);
        Assert.Equal("rejected:app", (string?)await MemoryRows.Scalar(S, "SELECT event||':'||channel FROM memory_insight_events WHERE candidate_id=@id ORDER BY id DESC LIMIT 1", ("id", pairs[0])));

        // Refused before anything is read: a revision where an item has none, none where a memory has one, an item named twice, and
        // every step that needs more than a word, through the store, which the app's op never reaches with them.
        state = await Resolves.State(S);
        audit = await Resolves.Audit(S);
        var store = Store;
        var reviews = new InsightStore(S);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.ResolveAll(who.App, "dismiss_insight", [(insights[0], 1)], reviews, Resolves.App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.ResolveAll(who.App, "still_true", [(facts[1], null)], reviews, Resolves.App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.ResolveAll(who.App, "still_true", [(facts[1], 1), (facts[1], 1)], reviews, Resolves.App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => store.ResolveAll(who.App, "still_true", [], reviews, Resolves.App, default)));
        foreach (var action in new[] { "release", "keep_copy", "settle_keep_both", "use_candidate", "done", "stop_standing" })
            Assert.Equal("invalid", await MemoryLogins.Refused(() => store.ResolveAll(who.App, action, [(facts[1], 1)], reviews, Resolves.App, default)));
        Assert.Equal(state, await Resolves.State(S));
        Assert.Equal(audit, await Resolves.Audit(S));
    }

    // With scopes on, the standing list and a step for several are their reader's. The household view and Sam's own session neither
    // list nor count Alex's private instructions, in count, firing, held or over the limit, while Alex's session reads and counts them,
    // a held one without its words or its project; Sam may not stop Alex's Family one (033's author lock), nor may the household view,
    // and neither finds his private one; a step for several from either over Alex's private stale memory, insight and pair answers
    // each unknown and writes nothing, while Alex's own session takes each.
    [PostgresFact] public async Task With_scopes_on_standing_and_several_are_their_readers()
    {
        await Scoped.Mode(S, "on");
        try
        {
            await MemoryLogins.Family(S);
            // Each count below is exact: no standing instruction left by another test of this class.
            await Standing.Clean(S);
            var who = await MemoryLogins.Make(S, "more-scoped");
            var alex = await Scoped.Principal(S, "more-scoped-session-alex", "app", "alex");
            var sam = await Scoped.Principal(S, "more-scoped-session-sam", "app", "sam");

            // Alex's own: 50 in his scope, written in his own session (so many through an AI client's login in minutes would be held as a
            // burst), then one past them, which an undo of a stop allows, so the newest waits for a place; and one held for review, in a
            // project of his.
            var mine = new List<Guid>();
            for (var i = 0; i < MemoryStanding.MaxInScope; i++)
                mine.Add((await Store.RememberStanding(alex, new($"DEMO: when the Ottery rota {i} comes up, it is Alex's own note.", Scope: "private"), Resolves.App, default)).Id);
            await Store.StopStanding(alex, mine[3], null, Resolves.App, default);
            mine.Add((await Store.RememberStanding(alex, new("DEMO: when the Ottery rota is late, it is Alex's own note.", Scope: "private"), Resolves.App, default)).Id);
            await Store.Undo(alex, mine[3], null, null, Resolves.App, default);
            var held = (await Store.RememberStanding(who.Alex, new($"{Briefs.Held} as Alex's own Ottery note", Scope: "private", Project: "ottery-own"), Mcp, default)).Id;
            var family = (await Store.RememberStanding(who.Alex, new("DEMO: when the Ottery rota comes up, list the family's evenings.", Scope: "shared"), Mcp, default)).Id;

            async Task<(int Count, int Firing, int Held, int Over, List<string> Ids)> Read(Guid reader)
            {
                var page = await Ok(reader, new { op = "standing", limit = 50 });
                return ((int)page["count"]!, (int)page["firing"]!, (int)page["held"]!, (int)page["overLimit"]!, await Walk(reader, new { op = "standing" }, "standing"));
            }
            var privates = mine.Append(held).Select(m => m.ToString()).ToArray();
            foreach (var reader in new[] { who.App, sam })
            {
                var (count, firing, heldCount, over, ids) = await Read(reader);
                Assert.Equal((1, 1, 0, 0), (count, firing, heldCount, over));
                Assert.Equal([family.ToString()], ids);
            }
            var own = await Read(alex);
            Assert.Equal((MemoryStanding.MaxInScope + 3, MemoryStanding.MaxInScope + 1, 1, 1), (own.Count, own.Firing, own.Held, own.Over));
            Assert.Equal(privates.Append(family.ToString()).Order(), own.Ids.Order());
            var first = await Ok(alex, new { op = "standing", limit = 50 });
            var second = await Ok(alex, new { op = "standing", limit = 50, cursor = (string)first["next"]! });
            var shut = first["standing"]!.AsArray().Concat(second["standing"]!.AsArray()).Single(r => (string)r!["id"]! == held.ToString())!;
            Assert.Equal((null, null, "held"), ((string?)shut["content"], (JsonNode?)shut["project"], (string)shut["reason"]!));
            Assert.Equal("instruction", (string)shut["held"]!["reason"]!);

            // Stopping: Alex's Family one is his alone to stop, and his private one is no one else's to find.
            var state = await Resolves.State(S);
            Assert.Equal("forbidden", (string)(await Refused(sam, new { op = "stop_standing", id = family, expected = 1 }))["code"]!);
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "stop_standing", id = family, expected = 1 }))["code"]!);
            foreach (var reader in new[] { who.App, sam })
                Assert.Equal("unknown", (string)(await Refused(reader, new { op = "stop_standing", id = mine[0], expected = 1 }))["code"]!);
            Assert.Equal(state, await Resolves.State(S));
            var stopped = await Ok(alex, new { op = "stop_standing", id = family, expected = 1 });
            Assert.Equal(("stop_standing", false), ((string)stopped["op"]!, (bool)stopped["unchanged"]!));
            Assert.DoesNotContain(family.ToString(), await Walk(alex, new { op = "standing" }, "standing"));
            Assert.Equal(0, (int)(await Ok(who.App, new { op = "standing" }))["count"]!);

            // A step for several over Alex's own: each unknown to the household view and to Sam, nothing written.
            var fact = (await Store.Create(who.Alex, new("DEMO: Alex's own Ottery bakery opens at eight.", Scope: "private"), Mcp, default)).Id;
            await Briefs.Stale(S, fact);
            var insight = await new InsightStore(S).Save(who.Alex, StreamCalls.Draft("DEMO · Alex's own Ottery insight"), default);
            var pair = await Resolves.Candidate(S, Store, who.Alex, "ottery", scope: "private", subject: "alex");
            var steps = new (string Action, object Item)[]
            {
                ("still_true", new { id = fact, expected = 1 }), ("dismiss_insight", new { id = insight }), ("confirm_insight", new { id = insight }),
                ("reject_candidate", new { id = pair }),
            };
            (state, var audit) = (await Resolves.State(S), await Resolves.Audit(S));
            foreach (var reader in new[] { who.App, sam })
                foreach (var (action, item) in steps)
                {
                    var answer = await Ok(reader, new { op = "resolve_all", action, items = new[] { item } });
                    Assert.Equal("unknown", (string)Assert.Single(answer["results"]!.AsArray())!["result"]!);
                }
            Assert.Equal(state, await Resolves.State(S));
            Assert.Equal(audit, await Resolves.Audit(S));
            // Alex's own session takes each: they were there to be taken.
            foreach (var (action, item) in steps.Where(s => s.Action != "confirm_insight"))
                Assert.Equal("resolved", (string)(await Ok(alex, new { op = "resolve_all", action, items = new[] { item } }))["results"]![0]!["result"]!);
        }
        finally
        {
            // Its 52 instructions would fill the first page another test of this class reads.
            await Standing.Clean(S);
            await Scoped.Mode(S, "off");
        }
    }

    // A person's pick of the insight a pair became (D-233's not built): use_candidate names it, through the app, and the pair keeps it; one
    // that does not cite both memories is refused in the store's words, nothing written; the stream lists the pair under the insight.
    [PostgresFact] public async Task A_person_names_the_insight_a_pair_became_through_the_app()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "more-pick");
        var pair = await Resolves.Candidate(S, Store, who.Alex, "pick");
        var (older, newer) = ((Guid)(await MemoryRows.Scalar(S, "SELECT older_id FROM memory_insight_candidates WHERE id=@id", ("id", pair)))!,
            (Guid)(await MemoryRows.Scalar(S, "SELECT newer_id FROM memory_insight_candidates WHERE id=@id", ("id", pair)))!);
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        var drawn = await StreamCalls.Save(ai, "DEMO · the Wendle pick ferry, its hours and its fares", older, newer);
        var citesOne = await StreamCalls.Save(ai, "DEMO · the Wendle pick ferry hours", older);

        var state = await Resolves.State(S);
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "resolve", id = pair, action = "use_candidate", insight = citesOne }))["code"]!);
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "resolve", id = pair, action = "reject_candidate", insight = drawn }))["code"]!);
        Assert.Equal(state, await Resolves.State(S));

        var used = await Ok(who.App, new { op = "resolve", id = pair, action = "use_candidate", insight = drawn, note = "DEMO: the fares insight came of it." });
        Assert.Equal(("used", drawn.ToString(), false), ((string)used["candidate"]!, (string)used["insight"]!, (bool)used["unchanged"]!));
        var row = (await Ok(who.App, new { op = "insight_candidates", state = "used" }))["candidates"]!.AsArray().First(c => (string)c!["id"]! == pair.ToString())!;
        Assert.Equal(drawn.ToString(), (string)row["insight"]!);
        var streamed = (await Ok(who.App, new { op = "insight_stream", view = "all", limit = 50 }))["insights"]!.AsArray().First(i => (string)i!["id"]! == drawn.ToString())!;
        Assert.Equal([pair.ToString()], streamed["candidates"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("used:app", (string?)await MemoryRows.Scalar(S, "SELECT event||':'||channel FROM memory_insight_events WHERE candidate_id=@id ORDER BY id DESC LIMIT 1", ("id", pair)));
    }

    // A test per step: an AI client's token reaches none of them. The store refuses a step of several through an AI client's channel before
    // reading anything; through the door with a real token no tool of the app's is listed or answers, and its own tools read as before.
    [PostgresFact] public async Task No_AI_clients_token_reaches_the_new_steps()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "more-ai");
        await using var client = await StreamCalls.As(fixture, who.Alex);
        var insight = await StreamCalls.Save(client, "DEMO · the Wendle insight an AI client saved");
        var standing = (await Store.RememberStanding(who.Alex, new("DEMO: when the Wendle ferry comes up, say it is fictional."), Mcp, default)).Id;
        var fact = (await Store.Create(who.Alex, new("DEMO: the Wendle ai bakery opens at eight."), Mcp, default)).Id;
        await Briefs.Stale(S, fact);
        var (audit, state) = (await Resolves.Audit(S), await Resolves.State(S));

        var reviews = new InsightStore(S);
        foreach (var (action, items) in new (string, (Guid, int?)[])[] { ("dismiss_insight", [(insight, null)]), ("confirm_insight", [(insight, null)]), ("still_true", [(fact, 1)]) })
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.ResolveAll(who.Alex, action, items, reviews, Mcp, default)));
        // The app's handler, if an AI client's login ever got there, writes as the app, which the database refuses it.
        var (status, content) = await App(who.Alex, new { op = "resolve_all", action = "still_true", items = new[] { new { id = fact, expected = 1 } } });
        Assert.Equal((200, true, "invalid"), (status, (bool)content!["ok"]!, (string)content["results"]![0]!["result"]!));
        var (stopStatus, stop) = await App(who.Alex, new { op = "stop_standing", id = standing, expected = 1 });
        Assert.Equal((200, false, "invalid"), (stopStatus, (bool)stop!["ok"]!, (string)stop["code"]!));
        Assert.Equal(audit, await Resolves.Audit(S));
        Assert.Equal(state, await Resolves.State(S));

        var config = MemoryToolSettings.With("tools: on, capsules: on, share: on");
        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config));
        var file = Path.Combine(Path.GetTempPath(), "rhythm-more-" + Guid.NewGuid().ToString("N"), "token");
        await new Principals(S).Create("more-ai-client", "harness", null, "alex", file, default);
        var token = await File.ReadAllTextAsync(file);
        Directory.Delete(Path.GetDirectoryName(file)!, true);
        await using var door = await McpEngine.Connect(engine.Handler(), "Bearer " + token);
        var names = (await door.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.DoesNotContain(AppMemories.Name, names);
        Assert.Contains("list_standing", names);
        foreach (var call in new Dictionary<string, object?>[]
                 {
                     new() { ["op"] = "list", ["query"] = "Wendle" }, new() { ["op"] = "standing" }, new() { ["op"] = "stop_standing", ["id"] = standing },
                     new() { ["op"] = "resolve_all", ["action"] = "dismiss_insight", ["items"] = new[] { new Dictionary<string, object?> { ["id"] = insight } } },
                 })
        {
            var named = await Record.ExceptionAsync(async () => Assert.True((await door.CallToolAsync(AppMemories.Name, call)).IsError));
            Assert.True(named is null || named is ModelContextProtocol.McpException, named?.ToString());
        }
        // Its own list_standing still answers, as before.
        var listed = await door.CallToolAsync("list_standing", new Dictionary<string, object?> { ["n"] = 50 });
        Assert.True(listed.IsError != true, MemoryToolClient.Text(listed));
        Assert.Equal(audit, await Resolves.Audit(S));
        Assert.Equal(state, await Resolves.State(S));
        Assert.True((await Store.Get(who.App, standing, default))!.Standing);
        TestSettings.Use();
    }
}
