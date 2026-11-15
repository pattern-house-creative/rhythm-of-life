using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using ModelContextProtocol.Protocol;

namespace Knowledge.Tests;

// The full memory UI's list (SB-P6-01, Mcp/AppMemoriesManage.cs, MemoryBulk.cs): tags and counts beside a list's filters, an
// AI client's groups of changes and a person's undo of one, several memories forgotten, brought back, deleted for good or
// shared at once, the project registry's writes, a capsule's status and the memory settings to read. Every step goes through
// app_memories, the app's channel, a person's; none of it is listed or taken with memory.tools off, and an AI client's token
// never reaches a person's step. Fictional people only (alex, sam, the household); every text is DEMO.
public sealed class AppMemoryListTests
{
    static readonly Guid Id = Guid.Parse("6d3c2a10-0000-4000-8000-00000000f101");
    static readonly Guid Other = Guid.Parse("6d3c2a10-0000-4000-8000-00000000f102");
    static readonly object[] Reviewed = [new { id = Id, expected = 1 }];

    static bool Takes(object arguments, bool share = false) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true, share: share);

    // Every new op, as the view sends it: each is off with memory.tools off.
    static readonly object[] Steps =
    [
        new { op = "tags" }, new { op = "changes" }, new { op = "capsule_status", capsule = "core" }, new { op = "settings" },
        new { op = "undo_group", group = "abc" }, new { op = "forget_all", memories = Reviewed },
        new { op = "restore_all", memories = new[] { new { id = Id, nonce = Other } } },
        new { op = "delete_all", memories = new[] { new { id = Id, nonce = Other, confirm = Id } } },
        new { op = "project_create", name = "DEMO: Canal Walks" }, new { op = "project_update", id = Id, name = "DEMO: Canal" }, new { op = "project_delete", id = Id },
    ];

    [Fact] public void Each_new_op_takes_exactly_its_arguments()
    {
        foreach (var step in Steps) Assert.True(Takes(step), JsonSerializer.Serialize(step));

        Assert.True(Takes(new { op = "tags", scope = "shared", tag = "rota", kind = "semantic", status = "draft", project = "wendle" }));
        Assert.False(Takes(new { op = "tags", limit = 5 }));
        Assert.False(Takes(new { op = "tags", cursor = "abc" }));

        Assert.True(Takes(new { op = "changes", since = "2026-11-01T09:30:00Z", scope = "private" }));
        Assert.False(Takes(new { op = "changes", since = "last week" }));
        Assert.False(Takes(new { op = "changes", since = new string('2', 41) }));
        Assert.False(Takes(new { op = "changes", channel = "app" }));

        Assert.True(Takes(new { op = "capsule_status", capsule = "project", project = "wendle", scope = "shared" }));
        Assert.False(Takes(new { op = "capsule_status" }));
        Assert.False(Takes(new { op = "capsule_status", capsule = "none" }));
        Assert.False(Takes(new { op = "capsule_status", capsule = "project" }));
        Assert.False(Takes(new { op = "capsule_status", capsule = "core", project = "wendle" }));

        Assert.False(Takes(new { op = "settings", scope = "shared" }));

        Assert.True(Takes(new { op = "undo_group", group = "djEuNmQzYzJhMTA_-" }));
        Assert.False(Takes(new { op = "undo_group" }));
        Assert.False(Takes(new { op = "undo_group", group = "not a key" }));
        Assert.False(Takes(new { op = "undo_group", group = new string('a', 201) }));
        Assert.False(Takes(new { op = "undo_group", group = "abc", key = "undo-group-key-1" }));

        // Several at once: each memory exactly as its one step takes it, 1 to 50 of them.
        Assert.False(Takes(new { op = "forget_all", memories = Array.Empty<object>() }));
        Assert.False(Takes(new { op = "forget_all", memories = Enumerable.Range(0, 51).Select(_ => new { id = Id, expected = 1 }).ToArray() }));
        Assert.False(Takes(new { op = "forget_all", memories = new[] { new { id = Id } } }));
        Assert.False(Takes(new { op = "forget_all", memories = new[] { new { id = Id, nonce = Other } } }));
        Assert.False(Takes(new { op = "forget_all", memories = Reviewed, key = "forget-all-key-1" }));
        Assert.False(Takes(new { op = "restore_all", memories = Reviewed }));
        Assert.False(Takes(new { op = "restore_all", memories = new[] { new { id = Id } } }));
        // A delete's confirmation is the memory's own id, each one, before anything is read.
        Assert.False(Takes(new { op = "delete_all", memories = new[] { new { id = Id, nonce = Other, confirm = Other } } }));
        Assert.False(Takes(new { op = "delete_all", memories = new[] { new { id = Id, nonce = Other } } }));

        Assert.True(Takes(new { op = "project_create", name = "DEMO", slug = "canal-walks", description = "", aliases = new[] { "towpath" }, scope = "shared" }));
        Assert.False(Takes(new { op = "project_create" }));
        Assert.False(Takes(new { op = "project_create", name = "" }));
        Assert.False(Takes(new { op = "project_create", name = new string('x', 121) }));
        Assert.False(Takes(new { op = "project_create", name = "DEMO", slug = "Canal Walks" }));
        Assert.False(Takes(new { op = "project_create", name = "DEMO", description = new string('x', 1001) }));
        Assert.False(Takes(new { op = "project_create", name = "DEMO", aliases = Enumerable.Repeat("a", 17).ToArray() }));
        Assert.True(Takes(new { op = "project_update", id = Id, name = "DEMO", description = "", aliases = Array.Empty<string>(), archived = true, expected = 3 }));
        Assert.False(Takes(new { op = "project_update", id = Id }));
        Assert.False(Takes(new { op = "project_update", id = Id, expected = 3 }));
        Assert.False(Takes(new { op = "project_update", id = Id, slug = "another" }));
        Assert.True(Takes(new { op = "project_delete", id = Id, expected = 2 }));
        Assert.False(Takes(new { op = "project_delete" }));

        // No op names its channel or its client: the handler fixes both.
        foreach (var op in new[] { "tags", "changes", "settings", "project_delete" })
        {
            Assert.False(Takes(new { op, id = Id, channel = "cli" }), op);
            Assert.False(Takes(new { op, id = Id, client = "DEMO terminal" }), op);
        }
    }

    // Off, absent or empty, the app answers each as a tool its role does not have, before anything is read.
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

    // share_all is a share's: outside the schema with memory.share off, as the share is, and listed only beside it.
    [Fact] public async Task Sharing_several_is_taken_and_listed_only_with_memory_share_on()
    {
        var call = new { op = "share_all", memories = Reviewed, scope = "shared" };
        Assert.True(Takes(call, share: true));
        Assert.False(Takes(call));
        Assert.False(Takes(new { op = "share_all", memories = Reviewed, scope = "company" }, share: true));
        Assert.False(Takes(new { op = "share_all", scope = "shared" }, share: true));
        var (status, _) = await AppMemoryCalls.Call(AppMemoryCalls.Services(MemoryToolSettings.On), Guid.NewGuid(), AppMemoryCalls.Body(AppEndpoint.Memories, call));
        Assert.Equal(400, status);
        Assert.DoesNotContain(AppMemories.ShareAllOp, JsonSerializer.SerializeToNode(AppMemories.Listing())!["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray().Select(o => (string)o!));
        Assert.Contains(AppMemories.ShareAllOp, JsonSerializer.SerializeToNode(AppMemories.Listing(true))!["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray().Select(o => (string)o!));
        TestSettings.Use();
    }

    // No AI client is offered a person's step: with every memory switch on, no harness tool is one of them.
    [Fact] public void No_harness_tool_is_a_step_of_the_persons_list()
    {
        var config = MemoryToolSettings.With("tools: on, capsules: on, share: on");
        var names = HarnessTools.Create(config, []).Select(t => t.ProtocolTool.Name).ToArray();
        foreach (var step in new[] { AppMemories.Name, "forget_all", "restore_all", "delete_all", "share_all", "undo_group", "project_create", "project_update", "project_delete",
                     "create_project", "update_project", "delete_project", "capsule_status", "settings", "tags", "changes" })
            Assert.DoesNotContain(step, names);
        TestSettings.Use();
    }
}

public sealed class AppMemoryListDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly IReadOnlyList<ParityMemory> Planted = [.. ParityCorpus.Load().Memories.Where(m => m.Expect?.Held == true)];
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    MemoryStore Store => new(fixture.Source);

    async Task<(int Status, JsonNode? Content)> App(Guid app, object arguments, RhythmConfig? config = null)
    {
        var settings = config ?? MemoryToolSettings.On;
        return await AppMemoryCalls.Call(AppMemoryCalls.Services(settings, fixture.Db), app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments), new AppHealth(fixture.Db, settings));
    }

    async Task<JsonNode> Ok(Guid app, object arguments, RhythmConfig? config = null)
    {
        var (status, content) = await App(app, arguments, config);
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

    static string Results(JsonNode answer) =>
        string.Join(",", answer["results"]!.AsArray().Select(r => (string)r!["result"]!));

    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);

    async Task<Guid> Make(Guid by, string text, string? scope = null, string? project = null, string[]? tags = null) =>
        (await Store.Create(by, new(text, tags, scope, Project: project), Mcp, default)).Id;

    // A list's filters and the tags beside them read through the one set of predicates: a count is the list's whole length,
    // a tag's the length of the list filtered by it too; a held memory is counted, but its tags are none of them.
    [PostgresFact] public async Task A_lists_tags_and_count_agree_with_the_list_they_summarise()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "list-tags");
        var a = await Make(who.Alex, "DEMO: the Ambleway rota starts on Monday.", "shared", "ambleway", ["ambleway-rota", "ambleway-all"]);
        var b = await Make(who.Alex, "DEMO: the Ambleway rota moved to Tuesday.", "shared", "ambleway", ["ambleway-rota", "ambleway-all"]);
        await Make(who.Alex, "DEMO: the Ambleway gate needs oil.", "shared", null, ["ambleway-all"]);
        await Store.Create(who.Alex, new(Planted[0].Text, ["ambleway-held"], "shared"), Mcp, default);
        await Store.SetStatus(who.Alex, a, "canonical", null, Mcp, default);

        async Task<int> Paged(object filter)
        {
            var total = 0;
            string? cursor = null;
            do
            {
                var page = await Ok(who.App, Merged(new { op = "list", limit = 2 }, filter, cursor));
                total += page["memories"]!.AsArray().Count;
                cursor = (string?)page["next"];
            } while (cursor is not null);
            return total;
        }
        foreach (var filter in new object[] { new { project = "ambleway" }, new { tag = "ambleway-all" }, new { status = "canonical", tag = "ambleway-all" }, new { project = "ambleway", tag = "ambleway-rota" } })
        {
            var answer = await Ok(who.App, Merged(new { op = "tags" }, filter, null));
            Assert.Equal(await Paged(filter), (int)answer["count"]!);
        }
        var inProject = await Ok(who.App, new { op = "tags", project = "ambleway" });
        Assert.Equal(("ambleway-all:2,ambleway-rota:2", false), (Said(inProject), (bool)inProject["truncated"]!));
        Assert.Equal("ambleway-all:1,ambleway-rota:1", Said(await Ok(who.App, new { op = "tags", project = "ambleway", status = "canonical" })));
        Assert.Equal("ambleway-all:2,ambleway-rota:2", Said(await Ok(who.App, new { op = "tags", tag = "ambleway-rota" })));
        Assert.Equal(1, (int)(await Ok(who.App, new { op = "tags", tag = "ambleway-rota", status = "canonical" }))["count"]!);
        // A held memory's tags are in no vocabulary and select nothing.
        Assert.DoesNotContain("ambleway-held", (await Ok(who.App, new { op = "tags" }))["tags"]!.AsArray().Select(t => (string)t!["tag"]!));
        Assert.Equal(0, (int)(await Ok(who.App, new { op = "tags", tag = "ambleway-held" }))["count"]!);
        // A project nobody knows is refused as the list refuses it, with what is known.
        var unknown = await Refused(who.App, new { op = "tags", project = "no-such-plot" });
        Assert.Equal("unknown_project", (string)unknown["code"]!);
        Assert.Contains("ambleway", unknown["known"]!.AsArray().Select(k => (string)k!));

        static string Said(JsonNode answer) => string.Join(",", answer["tags"]!.AsArray().Where(t => ((string)t!["tag"]!).StartsWith("ambleway-", StringComparison.Ordinal))
            .Select(t => $"{(string)t!["tag"]!}:{(int)t["count"]!}"));
        static object Merged(object first, object filter, string? cursor)
        {
            var merged = JsonSerializer.SerializeToNode(first)!.AsObject();
            foreach (var (name, value) in JsonSerializer.SerializeToNode(filter)!.AsObject()) merged[name] = value?.DeepClone();
            if (cursor is not null) merged["cursor"] = cursor;
            return merged;
        }
    }

    // With scopes on a tag and a count come only from what the reader reads: no tag of another person's private memory, and
    // no count of it.
    [PostgresFact] public async Task A_tag_or_a_count_of_a_private_memory_reaches_no_other_reader()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "list-scoped");
            var viewer = await Scoped.Principal(s, "list-scoped-session-sam", "app", "sam");
            await Make(who.Alex, "DEMO: Alex's own Thornhay seed list.", null, null, ["thornhay-secret"]);
            await Make(who.Alex, "DEMO: the Thornhay plot rota for the family.", "shared", null, ["thornhay-family"]);
            foreach (var reader in new[] { who.App, viewer })
            {
                var tags = (await Ok(reader, new { op = "tags" }))["tags"]!.AsArray().Select(t => (string)t!["tag"]!).ToArray();
                Assert.DoesNotContain("thornhay-secret", tags);
                Assert.Contains("thornhay-family", tags);
                Assert.Equal(0, (int)(await Ok(reader, new { op = "tags", tag = "thornhay-secret" }))["count"]!);
            }
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // Several memories at once, each at the revision its person saw: one that changed since stays, one not this login's stays,
    // and every other goes; each goes back from the trash row given; delete for good takes each one's nonce and confirmation.
    [PostgresFact] public async Task A_person_forgets_brings_back_and_deletes_several_each_answered_on_its_own()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "bulk-app");
        var one = await Make(who.Alex, "DEMO: the Quillby bins go out on Monday.", "shared");
        var two = await Make(who.Alex, "DEMO: the Quillby gate sticks in the rain.", "shared");
        var moved = await Make(who.Alex, "DEMO: the Quillby shed needs a new lock.", "shared");
        var held = (await Store.Create(who.Alex, new(Planted[0].Text, Scope: "shared"), Mcp, default)).Id;
        await Store.Append(who.Alex, moved, "DEMO: bought one.", null, Mcp, default);
        var nobody = Guid.NewGuid();

        var forgot = await Ok(who.App, new { op = "forget_all", memories = new object[] {
            new { id = one, expected = 1 }, new { id = two, expected = 1 }, new { id = moved, expected = 1 }, new { id = held, expected = 1 }, new { id = nobody, expected = 1 } } });
        Assert.Equal("forgotten,forgotten,conflict,forgotten,unknown", Results(forgot));
        var rows = forgot["results"]!.AsArray();
        Assert.Equal(2, (int)rows[2]!["revision"]!);
        Assert.Null(rows[2]!["nonce"]);
        Assert.Equal(14, (int)rows[0]!["days"]!);
        Assert.NotNull(await Store.Get(who.Alex, moved, default));
        Assert.Equal("trashed:app:-", await Scalar("SELECT event||':'||channel||':'||coalesce(client,'-') FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", one)));

        // A name twice, or an AI client's channel, reads and writes nothing.
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "forget_all", memories = new object[] { new { id = moved, expected = 2 }, new { id = moved, expected = 2 } } }))["code"]!);
        Assert.NotNull(await Store.Get(who.Alex, moved, default));

        var nonce = (string)rows[0]!["nonce"]!;
        var heldNonce = (string)rows[3]!["nonce"]!;
        var twoNonce = (string)rows[1]!["nonce"]!;
        var back = await Ok(who.App, new { op = "restore_all", memories = new object[] {
            new { id = one, nonce }, new { id = held, nonce = heldNonce }, new { id = two, nonce = Guid.NewGuid() } } });
        Assert.Equal("restored,restored,unknown", Results(back));
        // A held memory comes back held still.
        Assert.Null(back["results"]![0]!["held"]);
        Assert.Equal(MemoryHolds.Instruction, (string)back["results"]![1]!["held"]!["reason"]!);
        Assert.Equal("DEMO: the Quillby bins go out on Monday.", (await Store.Get(who.Alex, one, default))!.Content);

        var gone = await Ok(who.App, new { op = "delete_all", memories = new object[] { new { id = two, nonce = twoNonce, confirm = two }, new { id = one, nonce, confirm = one } } });
        Assert.Equal("deleted,unknown", Results(gone));
        Assert.Equal(0L, (long)(await Scalar("SELECT count(*) FROM memories WHERE id=@id", ("id", two)))!);
        Assert.NotNull(await Store.Get(who.Alex, one, default));
    }

    // With scopes on each memory of several keeps the author lock and the one scope: the household view takes none of Alex's;
    // Alex's own viewer session takes hers and is refused Sam's; a held memory is shared only once a person releases it.
    [PostgresFact] public async Task With_scopes_on_each_of_several_keeps_the_author_lock_the_one_scope_and_its_hold()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "bulk-scoped");
            var alex = await Scoped.Principal(s, "bulk-scoped-session-alex", "app", "alex");
            var config = MemoryToolSettings.With("tools: on, share: on");
            var own = await Make(who.Alex, "DEMO: Alex's own Brackley notes.");
            var ownToo = await Make(who.Alex, "DEMO: Alex's own Brackley seed list.");
            var family = await Make(who.Alex, "DEMO: the Brackley plot rota.", "shared");
            var sams = await Make(who.Sam, "DEMO: Sam's own Brackley notes.");
            var samsFamily = await Make(who.Sam, "DEMO: Sam's Brackley rota for the family.", "shared");
            var held = (await Store.Create(who.Alex, new(Planted[0].Text), Mcp, default)).Id;

            // The household view reads Family only, and may change none of a person's.
            var house = await Ok(who.App, new { op = "forget_all", memories = new object[] { new { id = own, expected = 1 }, new { id = family, expected = 1 } } });
            Assert.Equal("unknown,forbidden", Results(house));
            Assert.NotNull(await Store.Get(who.Alex, family, default));

            // Alex's session: her own, and not Sam's, private or in Family.
            var forgot = await Ok(alex, new { op = "forget_all", memories = new object[] { new { id = ownToo, expected = 1 }, new { id = sams, expected = 1 }, new { id = samsFamily, expected = 1 } } });
            Assert.Equal("forgotten,unknown,forbidden", Results(forgot));
            Assert.NotNull(await Store.Get(who.Sam, samsFamily, default));

            // A call where nothing moved asks the map for no rebuild; one where any moved asks for one.
            await Scoped.Execute(s, "DELETE FROM relationship_refresh_requests");
            Assert.Equal("unchanged", Results(await Ok(alex, new { op = "share_all", scope = "shared", memories = new object[] { new { id = family, expected = 1 } } }, config)));
            Assert.Equal(0L, (long)(await Scalar("SELECT count(*) FROM relationship_refresh_requests"))!);
            var shared = await Ok(alex, new { op = "share_all", scope = "shared", memories = new object[] {
                new { id = own, expected = 1 }, new { id = family, expected = 1 }, new { id = held, expected = 1 }, new { id = sams, expected = 1 } } }, config);
            Assert.Equal("moved,unchanged,held,unknown", Results(shared));
            Assert.Equal(1L, (long)(await Scalar("SELECT count(*) FROM relationship_refresh_requests"))!);
            Assert.Equal(("household", "alex"), ((await Store.Get(who.Sam, own, default))!.Subject, (await Store.Get(who.Sam, own, default))!.Author));
            Assert.Equal("private", (await Store.Get(who.Alex, held, default))!.Scope);
            Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "get", id = held }))["code"]!);

            var back = await Ok(alex, new { op = "share_all", scope = "private", memories = new object[] { new { id = own, expected = 2 } } }, config);
            Assert.Equal("moved", Results(back));
            Assert.Null(await Store.Get(who.Sam, own, default));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A take-back that would join two people's private records is refused with no code of its own: that memory stays in Family,
    // those before and after it move, and the map is asked for its rebuild, since one moved.
    [PostgresFact] public async Task A_take_back_refused_for_its_link_leaves_the_others_of_several_done()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "bulk-linked");
            var sam = await Scoped.Principal(s, "bulk-linked-session-sam", "app", "sam");
            var first = await Make(who.Sam, "DEMO: Sam's Tolland note, first.", "shared");
            var linked = await Make(who.Sam, "DEMO: Sam's Tolland note, linked.", "shared");
            var after = await Make(who.Sam, "DEMO: Sam's Tolland note, after.", "shared");
            var hers = await Make(who.Alex, "DEMO: Alex's Tolland note.", "shared");
            // A stated link, as `rhythm import` writes one in Family; Alex takes hers back, so it joins Sam's memory to her private one.
            var (low, high) = string.CompareOrdinal(linked.ToString("D"), hers.ToString("D")) < 0 ? (linked, hers) : (hers, linked);
            await MemoryRows.Run(s, "INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by,imported) VALUES(@l,@a,@b,'relates_to','verified','',NULL,true)",
                ("l", Guid.NewGuid()), ("a", low), ("b", high));
            Assert.Equal("private", (await Store.Share(who.Alex, hers, "private", null, Mcp, default)).Scope);
            await Scoped.Execute(s, "DELETE FROM relationship_refresh_requests");

            var back = await Ok(sam, new { op = "share_all", scope = "private", memories = new object[] {
                new { id = first, expected = 1 }, new { id = linked, expected = 1 }, new { id = after, expected = 1 } } }, MemoryToolSettings.With("tools: on, share: on"));
            Assert.Equal("moved,invalid,moved", Results(back));
            Assert.Null(back["results"]![1]!["revision"]);
            Assert.Equal(("sam", "private"), ((await Store.Get(who.Sam, first, default))!.Subject, (await Store.Get(who.Sam, first, default))!.Scope));
            Assert.Equal(("sam", "private"), ((await Store.Get(who.Sam, after, default))!.Subject, (await Store.Get(who.Sam, after, default))!.Scope));
            Assert.Equal(("household", "shared"), ((await Store.Get(who.Sam, linked, default))!.Subject, (await Store.Get(who.Sam, linked, default))!.Scope));
            Assert.Equal(1L, (long)(await Scalar("SELECT count(*) FROM relationship_refresh_requests"))!);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // The key of the group holding a memory, as a person lists the AI clients' changes (48 hours; each test's logins are its own).
    async Task<string> GroupOf(Guid app, Guid memory) =>
        (string)(await Ok(app, new { op = "changes" }))["groups"]!.AsArray()
            .Single(g => g!["items"]!.AsArray().Any(i => (string)i!["id"]! == memory.ToString()))!["group"]!;

    // A redo takes back its own group's undos and nothing else its person undid within the same ten minutes: not another
    // group's, and not one memory's undone apart. A redo's own key takes the redo back again.
    [PostgresFact] public async Task A_redo_puts_back_only_its_own_groups_undos()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "redo-own");
        var edited = await Make(who.Alex, "DEMO: the Wistow gate sticks.", "shared");
        var marked = await Make(who.Alex, "DEMO: the Wistow rota.", "shared");
        var apart = await Make(who.Alex, "DEMO: the Wistow shed.", "shared");
        await Store.Update(who.Alex, edited, new("DEMO: the Wistow gate, as the client edited it."), null, Mcp, default);
        await Store.SetStatus(who.Alex, marked, "canonical", null, Mcp, default);
        await Store.Update(who.Alex, apart, new("DEMO: the Wistow shed, as another client edited it."), null, new MemoryCall("mcp", "DEMO other"), default);
        async Task<string> Status() => (await Store.Get(who.Alex, marked, default))!.Status;
        async Task<string> Words(Guid id) => (await Store.Get(who.Alex, id, default))!.Content!;

        var undoEdit = await Ok(who.App, new { op = "undo_group", group = await GroupOf(who.App, edited) });
        var undoStatus = await Ok(who.App, new { op = "undo_group", group = await GroupOf(who.App, marked) });
        Assert.Equal(("undone", "undone"), (Results(undoEdit), Results(undoStatus)));
        await Ok(who.App, new { op = "undo", id = apart, expected = 2 });
        Assert.Equal(("DEMO: the Wistow gate sticks.", "draft", "DEMO: the Wistow shed."), (await Words(edited), await Status(), await Words(apart)));

        // The second group's redo: its status comes back, and the first group's edit and the one undone apart stay undone.
        var redo = await Ok(who.App, new { op = "undo_group", group = (string)undoStatus["redo"]! });
        Assert.Equal((1, "undone", true), ((int)redo["total"]!, Results(redo), (bool)redo["done"]!));
        Assert.Equal(("DEMO: the Wistow gate sticks.", "canonical", "DEMO: the Wistow shed."), (await Words(edited), await Status(), await Words(apart)));

        // The redo's own key takes back the redo alone, so the status is undone again.
        var again = await Ok(who.App, new { op = "undo_group", group = (string)redo["redo"]! });
        Assert.Equal((1, "undone"), ((int)again["total"]!, Results(again)));
        Assert.Equal("draft", await Status());

        // The first group's redo, given before the others were undone, still names its own.
        var first = await Ok(who.App, new { op = "undo_group", group = (string)undoEdit["redo"]! });
        Assert.Equal((1, "undone"), ((int)first["total"]!, Results(first)));
        Assert.Equal(("DEMO: the Wistow gate, as the client edited it.", "draft", "DEMO: the Wistow shed."), (await Words(edited), await Status(), await Words(apart)));
    }

    // An undo of something else in the middle of a group's calls ends what the redo reaches: it never sweeps that undo in, and
    // what of the group lies before it stays undone.
    [PostgresFact] public async Task A_redo_stops_at_an_undo_of_something_else()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "redo-apart");
        var many = new List<Guid>();
        for (var i = 0; i < 6; i++) many.Add(await Make(who.Alex, $"DEMO: Lindale note {i}.", "shared"));
        var apart = await Make(who.Alex, "DEMO: the Lindale rota.", "shared");
        foreach (var id in many) await Store.Update(who.Alex, id, new($"DEMO: Lindale note, as the client edited it: {id}."), null, Mcp, default);
        await Store.SetStatus(who.Alex, apart, "canonical", null, Mcp, default);
        var key = await GroupOf(who.App, many[0]);
        var apartKey = await GroupOf(who.App, apart);

        Assert.False((bool)(await Ok(who.App, new { op = "undo_group", group = key }))["done"]!);
        Assert.Equal("undone", Results(await Ok(who.App, new { op = "undo_group", group = apartKey })));
        var rest = await Ok(who.App, new { op = "undo_group", group = key });
        Assert.Equal(("undone", true), (Results(rest), (bool)rest["done"]!));

        var redo = await Ok(who.App, new { op = "undo_group", group = (string)rest["redo"]! });
        Assert.Equal((1, "undone"), ((int)redo["total"]!, Results(redo)));
        Assert.Contains("as the client edited it", (await Store.Get(who.Alex, many[5], default))!.Content);
        Assert.Equal("draft", (await Store.Get(who.Alex, apart, default))!.Status);
        foreach (var id in many.Take(5)) Assert.StartsWith("DEMO: Lindale note ", (await Store.Get(who.Alex, id, default))!.Content);
    }

    // An AI client's edits read as one group; one tap (and as many calls as the group takes) puts every memory back, a memory
    // changed since stays as it is, a held one is listed without its words, and the undo's own group undoes it again. Every
    // undo is the app's on the audit trail.
    [PostgresFact] public async Task A_person_reads_an_AI_clients_changes_and_undoes_the_group()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "ai-changes");
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++) ids.Add(await Make(who.Alex, $"DEMO: Sedgewick note {i}.", "shared"));
        var since = await MemoryLogins.Now(s);
        foreach (var id in ids) await Store.Update(who.Alex, id, new($"DEMO: Sedgewick note, edited by the client: {id}."), null, Mcp, default);
        await Store.Append(who.Alex, ids[0], "DEMO: and again.", null, Mcp, default);
        // A person's own edit through the app is no AI change.
        var own = await Make(who.Alex, "DEMO: Sedgewick note of a person's.", "shared");
        await Ok(who.App, new { op = "update", id = own, content = "DEMO: Sedgewick, edited by a person." });

        var read = await Ok(who.App, new { op = "changes", since = since.ToString("O") });
        var group = Assert.Single(read["groups"]!.AsArray(), g => g!["items"]!.AsArray().Any(i => ids.Contains(Guid.Parse((string)i!["id"]!))))!;
        Assert.Equal(("edit", "alex", "DEMO client", 8, 7, false), ((string)group["family"]!, (string?)group["person"], (string?)group["client"], (int)group["changes"]!, (int)group["memories"]!, (bool)group["capped"]!));
        Assert.DoesNotContain(own.ToString(), read.ToJsonString());
        var newest = group["items"]!.AsArray()[0]!;
        Assert.Equal((ids[0].ToString(), 2, "appended"), ((string)newest["id"]!, (int)newest["changes"]!, (string)newest["event"]!));
        Assert.Contains("and again", (string)newest["preview"]!);
        Assert.False((bool)newest["held"]!);

        // Before the undo, one memory changes again: it stays as it is, and every other goes back.
        var later = ids[3];
        await Ok(who.App, new { op = "append", id = later, text = "DEMO: a person's later word." });
        string? key = (string)group["group"]!;
        // A memory left as it is is answered again by each call that passes it: one result a memory, the newest.
        var results = new Dictionary<string, string>();
        string? redo = null;
        for (var call = 0; key is not null && call < 10; call++)
        {
            var undone = await Ok(who.App, new { op = "undo_group", group = key });
            foreach (var r in undone["results"]!.AsArray()) results[(string)r!["id"]!] = (string)r["result"]!;
            Assert.Equal((7, false), ((int)undone["total"]!, (bool)undone["capped"]!));
            redo = (string?)undone["redo"] ?? redo;
            if ((bool)undone["done"]!) break;
        }
        Assert.Equal(6, results.Values.Count(r => r == "undone"));
        Assert.Equal("changed_since", results[later.ToString()]);
        foreach (var id in ids.Where(i => i != later))
            Assert.StartsWith("DEMO: Sedgewick note ", (await Store.Get(who.Alex, id, default))!.Content);
        Assert.Contains("a person's later word", (await Store.Get(who.Alex, later, default))!.Content);
        Assert.Equal("reverted:app:-", await Scalar("SELECT event||':'||channel||':'||coalesce(client,'-') FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", ids[1])));

        // The group's key again passes over what is undone; the undo's own key takes it back.
        Assert.NotNull(redo);
        Assert.DoesNotContain("undone", Results(await Ok(who.App, new { op = "undo_group", group = (string)group["group"]! })));
        var again = await Ok(who.App, new { op = "undo_group", group = redo! });
        Assert.Contains("undone", Results(again));
        Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "undo_group", group = "bm90IGEga2V5" }))["code"]!);

        // A held memory's change is listed as any other's, without its words.
        var held = await Make(who.Alex, "DEMO: Sedgewick to be held.", "shared");
        await Store.Update(who.Alex, held, new(Planted[0].Text), null, Mcp, default);
        Assert.NotNull((await Store.Get(who.Alex, held, default))!.Hold);
        var row = (await Ok(who.App, new { op = "changes", since = since.ToString("O") }))["groups"]!.AsArray()
            .SelectMany(g => g!["items"]!.AsArray()).Single(i => (string)i!["id"]! == held.ToString())!;
        Assert.Equal((null, true), ((string?)row["preview"], (bool)row["held"]!));
    }

    // With scopes on a reader sees only the changes of what it reads, and undoes only what it may change.
    [PostgresFact] public async Task With_scopes_on_AI_changes_are_their_readers_and_keep_the_author_lock()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "ai-scoped");
            var alex = await Scoped.Principal(s, "ai-scoped-session-alex", "app", "alex");
            var own = await Make(who.Alex, "DEMO: Alex's own Ottermere notes.");
            var family = await Make(who.Alex, "DEMO: the Ottermere rota.", "shared");
            var since = await MemoryLogins.Now(s);
            await Store.Update(who.Alex, own, new("DEMO: Alex's own Ottermere notes, edited."), null, Mcp, default);
            await Store.Update(who.Alex, family, new("DEMO: the Ottermere rota, edited."), null, Mcp, default);

            // The household view: Family's change, no word of Alex's own.
            var house = await Ok(who.App, new { op = "changes", since = since.ToString("O") });
            Assert.DoesNotContain(own.ToString(), house.ToJsonString());
            Assert.DoesNotContain("own Ottermere", house.ToJsonString());
            var seen = house["groups"]!.AsArray().Single(g => g!["items"]!.AsArray().Any(i => (string)i!["id"]! == family.ToString()))!;
            // A key the household view was given undoes nothing of a person's Family memory: the author lock.
            var refused = await Ok(who.App, new { op = "undo_group", group = (string)seen["group"]! });
            Assert.Equal("forbidden", Results(refused));
            Assert.Null(refused["redo"]);
            Assert.Contains("edited", (await Store.Get(who.Alex, family, default))!.Content);

            // Alex's own session reads both, and names the scope it asks for.
            var mine = await Ok(alex, new { op = "changes", since = since.ToString("O"), scope = "private" });
            Assert.Contains(own.ToString(), mine.ToJsonString());
            Assert.DoesNotContain(family.ToString(), mine.ToJsonString());
            var hers = await Ok(alex, new { op = "changes", since = since.ToString("O") });
            var key = (string)hers["groups"]!.AsArray().Single(g => g!["items"]!.AsArray().Any(i => (string)i!["id"]! == family.ToString()))!["group"]!;
            Assert.Equal("undone,undone", Results(await Ok(alex, new { op = "undo_group", group = key })));
            Assert.Equal("DEMO: the Ottermere rota.", (await Store.Get(who.Alex, family, default))!.Content);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A person makes a project, renames it, gives it aliases, archives it and deletes it, its memories staying; the slug never
    // changes; another project's name is refused, a stale revision too, and every write is the app's on the trail.
    [PostgresFact] public async Task A_person_makes_changes_archives_and_deletes_a_project_and_its_memories_stay()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "projects-app");
        var made = await Ok(who.App, new { op = "project_create", name = "Harrowfield Walks!", description = "DEMO: walks.", aliases = new[] { "Harrow-Towpath" }, scope = "shared" });
        var project = made["project"]!;
        var id = Guid.Parse((string)project["id"]!);
        Assert.Equal(("project_create", "harrowfield-walks", "Harrowfield Walks!", "active", 1, "off"), ((string)made["op"]!, (string)project["slug"]!, (string)project["name"]!, (string)project["status"]!, (int)project["revision"]!, (string)made["scopes"]!));
        Assert.Equal(["harrow-towpath"], project["aliases"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("taken", (string)(await Refused(who.App, new { op = "project_create", name = "Other", slug = "harrow-towpath" }))["code"]!);
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "project_create", name = "!!!" }))["code"]!);

        var member = await Make(who.Alex, "DEMO: the Harrowfield towpath is muddy.", "shared", "harrowfield-walks", ["harrowfield-note"]);
        var tagged = await Make(who.Alex, "DEMO: a tagged Harrowfield note.", "shared", null, ["harrow-towpath"]);
        Assert.Equal(2, (int)(await Ok(who.App, new { op = "tags", project = "harrowfield-walks" }))["count"]!);

        var renamed = await Ok(who.App, new { op = "project_update", id, name = "Harrowfield Rambles", expected = 1 });
        Assert.Equal(("project_update", "harrowfield-walks", "Harrowfield Rambles", 2), ((string)renamed["op"]!, (string)renamed["project"]!["slug"]!, (string)renamed["project"]!["name"]!, (int)renamed["project"]!["revision"]!));
        Assert.Equal(["name"], renamed["changed"]!.AsArray().Select(c => (string)c!));
        Assert.True((bool)(await Ok(who.App, new { op = "project_update", id, name = "Harrowfield Rambles" }))["unchanged"]!);
        var stale = await Refused(who.App, new { op = "project_update", id, description = "DEMO: late.", expected = 1 });
        Assert.Equal(("conflict", 2), ((string)stale["code"]!, (int)stale["revision"]!));
        // A description may be emptied; an alias answers as the project does.
        Assert.Equal("", (string)(await Ok(who.App, new { op = "project_update", id, description = "", aliases = new[] { "harrow-towpath", "harrow-rambles" }, expected = 2 }))["project"]!["description"]!);

        var archived = await Ok(who.App, new { op = "project_update", id, archived = true });
        Assert.Equal("archived", (string)archived["project"]!["status"]!);
        string[] Listed(JsonNode answer) => [.. answer["projects"]!.AsArray().Select(p => (string)p!["slug"]!)];
        Assert.DoesNotContain("harrowfield-walks", Listed(await Ok(who.App, new { op = "projects" })));
        var all = await Ok(who.App, new { op = "projects", archived = true });
        Assert.Contains("harrowfield-walks", Listed(all));
        var row = all["projects"]!.AsArray().Single(p => (string)p!["slug"]! == "harrowfield-walks")!;
        Assert.Equal(("", "archived", 4), ((string)row["description"]!, (string)row["status"]!, (int)row["revision"]!));
        Assert.Equal("active", (string)(await Ok(who.App, new { op = "project_update", id, archived = false }))["project"]!["status"]!);

        var deleted = await Ok(who.App, new { op = "project_delete", id, expected = 5 });
        Assert.Equal(("project_delete", "harrowfield-walks", "household", 1), ((string)deleted["op"]!, (string)deleted["slug"]!, (string)deleted["owner"]!, (int)deleted["memories"]!));
        Assert.Null((await Store.Get(who.Alex, member, default))!.Project);
        Assert.Equal(["harrow-towpath"], (await Store.Get(who.Alex, tagged, default))!.Tags);
        Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "project_delete", id }))["code"]!);
        // Its memory leaving it is recorded with the rest (035).
        Assert.Equal("created:app,updated:app,updated:app,archived:app,restored:app,updated:app,deleted:app", await Scalar(
            "SELECT string_agg(event||':'||channel,',' ORDER BY id) FROM memory_events WHERE project_id=@id", ("id", id)));
    }

    // With scopes on a project keeps the author lock and the one scope: the household view reads Family's and changes none a
    // person made, has no private scope, and hears nothing of a person's own project.
    [PostgresFact] public async Task With_scopes_on_a_project_keeps_the_author_lock_and_the_one_scope()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "projects-scoped");
            var alex = await Scoped.Principal(s, "projects-scoped-session-alex", "app", "alex");
            var own = (await Ok(alex, new { op = "project_create", name = "Alex's Nettlecombe", slug = "nettlecombe-own" }))["project"]!;
            var family = (await Ok(alex, new { op = "project_create", name = "Nettlecombe", slug = "nettlecombe", scope = "shared" }))["project"]!;
            Assert.Equal(("private", "alex", "alex"), ((string)own["scope"]!, (string)own["owner"]!, (string?)own["author"]));
            Assert.Equal(("shared", "household", "alex"), ((string)family["scope"]!, (string)family["owner"]!, (string?)family["author"]));

            var slugs = (await Ok(who.App, new { op = "projects" }))["projects"]!.AsArray().Select(p => (string)p!["slug"]!).ToArray();
            Assert.Contains("nettlecombe", slugs);
            Assert.DoesNotContain("nettlecombe-own", slugs);
            Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "project_update", id = Guid.Parse((string)own["id"]!), name = "Taken over" }))["code"]!);
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "project_update", id = Guid.Parse((string)family["id"]!), name = "Taken over" }))["code"]!);
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "project_delete", id = Guid.Parse((string)family["id"]!) }))["code"]!);
            // The household view has no private scope; it makes a project in Family, and only when it says so.
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "project_create", name = "Nowhere" }))["code"]!);
            Assert.Equal("household", (string)(await Ok(who.App, new { op = "project_create", name = "House Nettlecombe", scope = "shared" }))["project"]!["owner"]!);
            // A memory of a person's own stays out of a project the household view can read: under the same slug it lands in
            // a project of her own scope, and Family's holds none of it.
            var note = await Make(who.Alex, "DEMO: Alex's own Nettlecombe note.", null, "nettlecombe");
            Assert.NotEqual((string)family["id"]!, (await Store.Get(who.Alex, note, default))!.Project!.Id.ToString());
            Assert.Equal(0, (int)(await Ok(who.App, new { op = "tags", project = "nettlecombe", scope = "shared" }))["count"]!);
            Assert.DoesNotContain(note.ToString(), (await Ok(who.App, new { op = "list", project = "nettlecombe", scope = "shared" })).ToJsonString());
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A capsule's status: each slot as its AI clients are served it, what waits in it and why, and never its words.
    [PostgresFact] public async Task A_capsules_status_names_each_slot_and_what_waits_without_its_words()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Scoped.Execute(s, "UPDATE memories SET capsule=NULL,capsule_slot=NULL WHERE capsule IS NOT NULL");
        var who = await MemoryLogins.Make(s, "capsule-status");
        async Task<Guid> Slotted(string text, string kind, string slot, string status = "canonical", string? project = null)
        {
            var id = await Make(who.Alex, text, "shared", project);
            if (status != "draft") await Store.SetStatus(who.Alex, id, status, null, Mcp, default);
            await Ok(who.App, new { op = "capsule", id, capsule = kind, slot });
            return id;
        }
        var identity = await Slotted("DEMO: the Larkspur household is two people.", "core", "identity");
        var one = await Slotted("DEMO: the Larkspur preferences, first.", "core", "preferences");
        var two = await Slotted("DEMO: the Larkspur preferences, second.", "core", "preferences");
        var draft = await Slotted("DEMO: the Larkspur constraints, a draft.", "core", "constraints", "draft");
        var held = (await Store.Create(who.Alex, new(Planted[0].Text, Scope: "shared"), Mcp, default)).Id;
        await Ok(who.App, new { op = "capsule", id = held, capsule = "core", slot = "principles" });

        var status = await Ok(who.App, new { op = "capsule_status", capsule = "core", scope = "shared" });
        Assert.Equal(("core", "shared", false, true, false), ((string)status["kind"]!, (string)status["scope"]!, (bool)status["complete"]!, (bool)status["populated"]!, (bool)status["served"]!));
        Assert.Null(status["project"]);
        Assert.StartsWith("sha256:", (string)status["hash"]!);
        JsonNode Slot(JsonNode answer, string name) => answer["slots"]!.AsArray().Single(x => (string)x!["slot"]! == name)!;
        Assert.Equal(["identity", "preferences", "constraints", "principles"], status["slots"]!.AsArray().Select(x => (string)x!["slot"]!));
        Assert.Equal(("filled", identity.ToString()), ((string)Slot(status, "identity")["state"]!, (string?)Slot(status, "identity")["filledBy"]));
        Assert.Equal(("duplicate", null), ((string)Slot(status, "preferences")["state"]!, (string?)Slot(status, "preferences")["filledBy"]));
        Assert.Equal(new[] { one.ToString(), two.ToString() }.Order(StringComparer.Ordinal), Slot(status, "preferences")["duplicates"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("empty", (string)Slot(status, "constraints")["state"]!);
        var waits = Assert.Single(Slot(status, "constraints")["waiting"]!.AsArray())!;
        Assert.Equal((draft.ToString(), "draft", false, false), ((string)waits["id"]!, (string)waits["status"]!, (bool)waits["held"]!, (bool)waits["expired"]!));
        Assert.True((bool)Assert.Single(Slot(status, "principles")["waiting"]!.AsArray())!["held"]!);
        // No words of any memory, held or not.
        Assert.DoesNotContain("Larkspur", status.ToJsonString());
        Assert.DoesNotContain(Planted[0].Text, status.ToJsonString());

        var capsules = MemoryToolSettings.With("tools: on, capsules: on");
        Assert.True((bool)(await Ok(who.App, new { op = "capsule_status", capsule = "core", scope = "shared" }, capsules))["served"]!);

        // A project's capsule, by its slug, its slots its own.
        await Slotted("DEMO: the Larkspur plot stands fallow.", "project", "current-state", project: "larkspur-plot");
        var plot = await Ok(who.App, new { op = "capsule_status", capsule = "project", project = "larkspur-plot", scope = "shared" });
        Assert.Equal(("project", "larkspur-plot"), ((string)plot["kind"]!, (string?)plot["project"]));
        Assert.Equal(["current-state", "decisions", "open-questions"], plot["slots"]!.AsArray().Select(x => (string)x!["slot"]!));
        Assert.Equal("filled", (string)Slot(plot, "current-state")["state"]!);
        var unknown = await Refused(who.App, new { op = "capsule_status", capsule = "project", project = "no-such-plot", scope = "shared" });
        Assert.Equal("unknown_project", (string)unknown["code"]!);
        // With scopes off a private capsule is refused as a private memory is.
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "capsule_status", capsule = "core", scope = "private" }))["code"]!);
        await Scoped.Execute(s, "UPDATE memories SET capsule=NULL,capsule_slot=NULL WHERE capsule IS NOT NULL");
    }

    // With scopes on a capsule's status is its reader's: another person's private capsule is refused, its project unknown.
    [PostgresFact] public async Task With_scopes_on_a_capsules_status_is_its_readers()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "capsule-scoped");
            var alex = await Scoped.Principal(s, "capsule-scoped-session-alex", "app", "alex");
            var sam = await Scoped.Principal(s, "capsule-scoped-session-sam", "app", "sam");
            await Make(who.Alex, "DEMO: Alex's own Fernley notes.", null, "fernley-own");
            await Ok(alex, new { op = "capsule_status", capsule = "project", project = "fernley-own", scope = "private" });
            Assert.Equal("unknown_project", (string)(await Refused(sam, new { op = "capsule_status", capsule = "project", project = "fernley-own", scope = "private" }))["code"]!);
            Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "capsule_status", capsule = "core", scope = "private" }))["code"]!);
            Assert.Equal("shared", (string)(await Ok(who.App, new { op = "capsule_status", capsule = "core" }))["scope"]!);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // The settings say what the install is, to read: scopes as the database records them, each switch as rhythm.yaml sets it.
    [PostgresFact] public async Task The_memory_settings_say_what_the_install_is()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "settings-app");
        var plain = await Ok(who.App, new { op = "settings" });
        Assert.Equal(("off", null, false, false, false, false, 14, 20, MemoryInput.MaxBytes), ((string)plain["scopes"]!, (string?)plain["person"], (bool)plain["share"]!, (bool)plain["capsules"]!,
            (bool)plain["capture"]!, (bool)plain["secondBrainImport"]!, (int)plain["trashDays"]!, (int)plain["versionsKept"]!, (int)plain["maxBytes"]!));
        var all = await Ok(who.App, new { op = "settings" }, MemoryToolSettings.With("tools: on, share: on, capsules: on, capture: on, secondBrainImport: on"));
        Assert.Equal((true, true, true, true), ((bool)all["share"]!, (bool)all["capsules"]!, (bool)all["capture"]!, (bool)all["secondBrainImport"]!));
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var viewer = await Scoped.Principal(s, "settings-app-session-sam", "app", "sam");
            var mine = await Ok(viewer, new { op = "settings" });
            Assert.Equal(("on", "sam"), ((string)mine["scopes"]!, (string?)mine["person"]));
            Assert.Null((await Ok(who.App, new { op = "settings" }))["person"]);
            // A missing row reads as on, never off, as every other reading of the mode takes it.
            await Scoped.Execute(s, "DELETE FROM install_settings WHERE key='memory.scopes'");
            Assert.Equal("on", (string)(await Ok(who.App, new { op = "settings" }))["scopes"]!);
            Assert.True(await fixture.Db.ScopesOn(default));
        }
        finally
        {
            await Scoped.Execute(s, "INSERT INTO install_settings(key,value) VALUES('memory.scopes','off') ON CONFLICT(key) DO UPDATE SET value='off'");
        }
    }

    // No AI client's token reaches a person's step: through its own channel the store refuses several at once before anything is
    // read; naming the app's or the terminal's channel is refused by the database (044's memory_channel), for a person-bound
    // token too; and the app's handler, given an AI client's login, writes nothing. Over MCP no such tool answers.
    [PostgresFact] public async Task No_AI_clients_token_reaches_a_persons_list_step()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "list-ai");
        var plain = await Make(who.Alex, "DEMO: the Ryecroft bins go out on Monday.", "shared");
        var edited = await Make(who.Alex, "DEMO: the Ryecroft gate sticks.", "shared");
        await Store.Update(who.Alex, edited, new("DEMO: the Ryecroft gate sticks in the rain."), null, Mcp, default);
        var gone = await Make(who.Alex, "DEMO: a Ryecroft note to forget.", "shared");
        var trip = await Store.Forget(who.Alex, gone, null, Mcp, default);
        var project = (await Store.CreateProject(who.Alex, new("DEMO Ryecroft", Scope: "shared"), new MemoryCall("mcp", "DEMO client"), default)).Project.Id;
        var key = (await Store.Changes(who.Alex, null, "mcp", default)).Groups.First(g => g.Items.Any(i => i.Memory == edited)).Group;
        var events = await Scalar("SELECT count(*) FROM memory_events");

        foreach (var ai in new[] { who.Alex, who.House })
        {
            // Its own channel: several at once is no step of an AI client's.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.ForgetAll(ai, [(plain, 1)], Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.RestoreAll(ai, [(gone, trip.Trash.Nonce)], Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteAll(ai, [(gone, trip.Trash.Nonce, gone.ToString())], Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.ShareAll(ai, [(plain, 1)], "shared", Mcp, default)));
            foreach (var call in new[] { new MemoryCall("cli", "DEMO not a terminal"), new MemoryCall("app") })
            {
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.ForgetAll(ai, [(plain, 1)], call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.RestoreAll(ai, [(gone, trip.Trash.Nonce)], call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.DeleteAll(ai, [(gone, trip.Trash.Nonce, gone.ToString())], call, default)));
                // A share of several answers each memory on its own, since 048's refusal may be that memory's: nothing moves.
                Assert.Equal(["invalid"], (await Store.ShareAll(ai, [(plain, 1)], "shared", call, default)).Select(r => r.Result));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.CreateProject(ai, new("DEMO Not Yours", Scope: "shared"), call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.UpdateProject(ai, project, new(Name: "DEMO Not Yours"), null, call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.DeleteProject(ai, project, null, call, default)));
            }
            // The app's handler, if an AI client's login ever reached it, writes as the app, which the database refuses it.
            foreach (var step in new object[]
                     {
                         new { op = "forget_all", memories = new[] { new { id = plain, expected = 1 } } },
                         new { op = "restore_all", memories = new[] { new { id = gone, nonce = trip.Trash.Nonce } } },
                         new { op = "delete_all", memories = new[] { new { id = gone, nonce = trip.Trash.Nonce, confirm = gone } } },
                         new { op = "undo_group", group = key },
                         new { op = "project_create", name = "DEMO Not Yours", scope = "shared" },
                         new { op = "project_update", id = project, name = "DEMO Not Yours" },
                         new { op = "project_delete", id = project },
                     })
            {
                var (status, content) = await App(ai, step);
                Assert.Equal(200, status);
                // A group's undo is answered member by member; each is refused and nothing is written.
                if ((string?)JsonSerializer.SerializeToNode(step)!["op"] == "undo_group") Assert.DoesNotContain("undone", Results(content!));
                else Assert.Equal((false, "invalid"), ((bool)content!["ok"]!, (string)content["code"]!));
            }
        }
        Assert.Equal(events, await Scalar("SELECT count(*) FROM memory_events"));
        Assert.Equal("DEMO Ryecroft", (string)(await Ok(who.App, new { op = "projects" }))["projects"]!.AsArray().First(p => (string)p!["id"]! == project.ToString())!["name"]!);

        // Through the door, a real AI client's token: no tool of the app's is listed or answers.
        var config = MemoryToolSettings.With("tools: on, capsules: on, share: on");
        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config));
        var file = Path.Combine(Path.GetTempPath(), "rhythm-list-" + Guid.NewGuid().ToString("N"), "token");
        await new Principals(s).Create("list-ai-client", "harness", null, "alex", file, default);
        var token = await File.ReadAllTextAsync(file);
        Directory.Delete(Path.GetDirectoryName(file)!, true);
        await using var client = await McpEngine.Connect(engine.Handler(), "Bearer " + token);
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.DoesNotContain(AppMemories.Name, names);
        foreach (var named in new[] { AppMemories.Name, "forget_all", "undo_group", "project_create", "capsule_status" })
        {
            var answer = await Record.ExceptionAsync(async () =>
            {
                var called = await client.CallToolAsync(named, new Dictionary<string, object?> { ["op"] = "forget_all" });
                Assert.True(called.IsError, named);
            });
            Assert.True(answer is null || answer is ModelContextProtocol.McpException, answer?.ToString());
        }
        Assert.Equal(events, await Scalar("SELECT count(*) FROM memory_events"));
        TestSettings.Use();
    }

    // The trail says who: with memory.tools on the app's project writes and several at once are recorded as the app's, never as
    // an AI client's, whatever the group's own changes were.
    [PostgresFact] public async Task A_project_made_through_the_app_is_recorded_as_the_apps()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "list-trail");
        var made = await Ok(who.App, new { op = "project_create", name = "Ashcombe", scope = "shared" });
        Assert.Equal("created:app:-", await Scalar("SELECT string_agg(event||':'||channel||':'||coalesce(client,'-'),',' ORDER BY id) FROM memory_events WHERE project_id=@id",
            ("id", Guid.Parse((string)made["project"]!["id"]!))));
        Assert.Null(made["project"]!["author"]);
    }
}
