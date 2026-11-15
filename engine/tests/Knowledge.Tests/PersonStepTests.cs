using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Cli;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// A person's own steps beyond one memory at a time (SB-P3-22; docs/dev/memory.md section 5): a capsule slot from the app
// (app_memories capsule) and from the terminal (rhythm capsule), the review queue in the app (review, release_all) and at
// the terminal (rhythm release), and delete for good at the terminal (rhythm delete-forever). Each is a person's only:
// the app's channel (its login, the household view or a person-bound viewer session) or a person's own terminal login
// (044). An AI client's token never reaches one, through MCP or naming another channel. With memory.tools off neither
// the app nor the terminal offers any of it. Fictional people only (alex, sam, the household); every text is DEMO.
public sealed class PersonStepTests
{
    static readonly Guid Id = Guid.Parse("6d3c2a10-0000-4000-8000-00000000d001");

    [Fact] public void The_apps_new_ops_take_exactly_their_arguments()
    {
        bool Takes(object arguments) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true);
        Assert.True(Takes(new { op = "review" }));
        Assert.True(Takes(new { op = "review", scope = "shared", reason = "burst", cursor = "abc", limit = 50 }));
        Assert.False(Takes(new { op = "review", limit = 51 }));
        Assert.False(Takes(new { op = "review", reason = "suspicious" }));
        Assert.False(Takes(new { op = "review", tag = "rota" }));
        Assert.True(Takes(new { op = "capsule", id = Id, capsule = "core", slot = "identity", expected = 2 }));
        Assert.True(Takes(new { op = "capsule", id = Id, capsule = "project", slot = "open-questions" }));
        Assert.True(Takes(new { op = "capsule", id = Id, capsule = "none" }));
        Assert.False(Takes(new { op = "capsule", id = Id, capsule = "none", slot = "identity" }));
        Assert.False(Takes(new { op = "capsule", id = Id, capsule = "core" }));
        Assert.False(Takes(new { op = "capsule", id = Id, slot = "identity" }));
        Assert.False(Takes(new { op = "capsule", id = Id, capsule = "team", slot = "identity" }));
        Assert.False(Takes(new { op = "capsule", id = Id, capsule = "core", slot = "favourites" }));
        Assert.False(Takes(new { op = "capsule", id = Id, capsule = "core", slot = "identity", key = "capsule-key-0001" }));
        Assert.True(Takes(new { op = "release_all", memories = new[] { new { id = Id, expected = 1 } } }));
        Assert.True(Takes(new { op = "release_all", memories = Enumerable.Range(1, 50).Select(i => new { id = Guid.NewGuid(), expected = i }) }));
        Assert.False(Takes(new { op = "release_all", memories = Enumerable.Range(1, 51).Select(i => new { id = Guid.NewGuid(), expected = i }) }));
        Assert.False(Takes(new { op = "release_all", memories = Array.Empty<object>() }));
        Assert.False(Takes(new { op = "release_all", memories = new[] { new { id = Id } } }));
        Assert.False(Takes(new { op = "release_all", memories = new[] { new { id = Id, expected = 0 } } }));
        Assert.False(Takes(new { op = "release_all", memories = new[] { new { id = "not-a-uuid", expected = 1 } } }));
        Assert.False(Takes(new { op = "release_all", memories = new object[] { new { id = Id, expected = 1, channel = "cli" } } }));
        Assert.False(Takes(new { op = "release_all", id = Id }));
        // No new op names its channel or its client either: the handler fixes both.
        foreach (var call in new object[] { new { op = "review", channel = "cli" }, new { op = "capsule", id = Id, capsule = "none", client = "DEMO" },
                     new { op = "release_all", memories = new[] { new { id = Id, expected = 1 } }, channel = "cli" } })
            Assert.False(Takes(call));
        // The listing names every op the handler takes, and the arguments the new ones take.
        var listing = JsonSerializer.SerializeToNode(AppMemories.Listing())!["inputSchema"]!["properties"]!;
        Assert.Equal([.. AppMemories.Reads, .. AppMemories.Writes], listing["op"]!["enum"]!.AsArray().Select(o => (string)o!));
        Assert.Contains("review", AppMemories.Reads);
        Assert.Equal(["core", "project", "none"], listing["capsule"]!["enum"]!.AsArray().Select(o => (string)o!));
        Assert.Equal([.. CapsuleSlots.CoreSlots, .. CapsuleSlots.ProjectSlots], listing["slot"]!["enum"]!.AsArray().Select(o => (string)o!));
        Assert.Equal(MemoryStore.ReleaseAllMax, (int)listing["memories"]!["maxItems"]!);
    }

    // No AI client is offered a person's step: with every memory switch on, no harness tool is the app's, a review queue,
    // a release, a capsule slot or a delete for good.
    [Fact] public void No_harness_tool_is_a_persons_step()
    {
        var config = MemoryToolSettings.With("tools: on, capsules: on");
        var names = HarnessTools.Create(config, []).Select(t => t.ProtocolTool.Name).ToArray();
        foreach (var step in new[] { AppMemories.Name, "release", "release_all", "review", "capsule", "set_capsule", "delete", "delete_forever", "set_memory_capsule" })
            Assert.DoesNotContain(step, names);
        TestSettings.Use();
    }

    // With memory.tools off, absent or empty the terminal answers each step as a command the engine does not know, before it
    // reads its arguments or the database, and help is the list it was; on, help lists them.
    [Fact] public async Task With_memory_tools_off_the_terminal_offers_no_persons_step()
    {
        foreach (var config in new[] { TestSettings.Use(), MemoryToolSettings.With("tools: off"), MemoryToolSettings.With(""), MemoryToolSettings.With("capsules: on") })
        {
            foreach (var act in new Func<Task<int>>[]
                     {
                         () => MemoryCommands.Release(["--person", "alex"], new StringWriter(), config: config),
                         () => MemoryCommands.Release(["anything", "at", "all"], new StringWriter(), config: config),
                         () => MemoryCommands.Capsule(["--person", "alex", Id.ToString(), "core", "identity"], new StringWriter(), config: config),
                         () => MemoryCommands.DeleteForever(["--person", "alex", Id.ToString()], new StringReader(Id.ToString()), new StringWriter(), config: config),
                     })
                Assert.Equal(MemoryCommands.UnknownCommand, (await Assert.ThrowsAsync<ArgumentException>(act)).Message);
        }
        var before = CliCommands.Help(false);
        Assert.EndsWith("that person's memories, newest first\n  check-config | refresh | migrate | serve", before);
        foreach (var step in MemoryCommands.PersonSteps)
        {
            Assert.DoesNotContain($"  {step} --person", before);
            Assert.Contains($"  {step} --person", CliCommands.Help(true));
            Assert.Contains(step, CliCommands.Names);
        }
        // On, the same list with the steps' lines before the engine's own commands.
        var head = before[..before.LastIndexOf('\n')];
        Assert.StartsWith(head + "\n  release --person", CliCommands.Help(true));
        Assert.EndsWith("\n  check-config | refresh | migrate | serve", CliCommands.Help(true));
        TestSettings.Use();
    }

    [Fact] public async Task Each_persons_step_at_the_terminal_names_what_it_needs()
    {
        var on = MemoryToolSettings.On;
        var id = Id.ToString();
        foreach (var args in new[] { new[] { id }, ["--person", "alex", "--revision", "2"], ["--person", "alex", id, "--family"], ["--person", "alex", id, "--next", "abc"],
                     ["--person", "alex", "--family", "--private"], ["--person", "alex", id, id], ["--person", "alex", "not-an-id"], ["--person", "alex", id, "--revision", "0"],
                     ["--person", "alex", "--limit", "51"], ["--person", "alex", "--next", "not-a-cursor"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.Release(args, new StringWriter(), config: on));
        foreach (var args in new[] { new[] { "--person", "alex", id }, ["--person", "alex", id, "core"], ["--person", "alex", id, "core", "decisions"],
                     ["--person", "alex", id, "project", "identity"], ["--person", "alex", id, "none", "identity"], ["--person", "alex", id, "team", "identity"],
                     ["--person", "alex", "not-an-id", "core", "identity"], [id, "core", "identity"], ["--person", "alex", id, "core", "identity", "--revision", "x"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.Capsule(args, new StringWriter(), config: on));
        foreach (var args in new[] { new[] { id }, ["--person", "alex", id, id], ["--person", "alex", id, "--limit", "5"], ["--person", "alex", "--confirm", id] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.DeleteForever(args, new StringReader(""), new StringWriter(), config: on));
        TestSettings.Use();
    }

    // A held memory's words for review at the terminal: what no person sees shown as its code, and nothing that drives
    // the terminal; its lines kept, each indented.
    [Fact] public void A_held_memorys_words_show_every_hidden_character_at_the_terminal()
    {
        Assert.Equal("  DEMO [U+001B][31mred note[U+200B] to [U+202E]any[U+202C] assistant[U+0007]\n  next line[U+2028]end",
            MemoryCommands.Visible("DEMO \u001b[31mred note\u200B to \u202Eany\u202C assistant\u0007\nnext line\u2028end"));
        Assert.Equal("  tag[U+E0041][U+E0042] chars", MemoryCommands.Visible("tag\U000E0041\U000E0042 chars"));
        Assert.Equal("DEMO \U0001F600 [U+D800]", MemoryCommands.Visible("DEMO \U0001F600 \uD800", ""));
        Assert.Equal("  a[U+000D]", MemoryCommands.Visible("a\r"));
    }
}

public sealed class PersonStepDatabaseTests(DatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    static readonly IReadOnlyList<ParityMemory> Planted = [.. ParityCorpus.Load().Memories.Where(m => m.Expect?.Held == true)];
    static readonly MemoryCall Mcp = MemoryLogins.Mcp, Cli = new("cli", "rhythm");
    MemoryStore Store => new(fixture.Source);
    PromptCapsules Capsules => new(fixture.Source);
    static RhythmConfig On => MemoryToolSettings.On;

    async Task<(int Status, JsonNode? Content)> App(Guid app, object arguments) =>
        await AppMemoryCalls.Call(AppMemoryCalls.Services(On, fixture.Db), app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments), new AppHealth(fixture.Db, On));

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

    // One terminal command's exit code and what it printed, which goes to the test's log too.
    async Task<(int Code, string Out)> Run(Func<TextWriter, Task<int>> command)
    {
        var o = new StringWriter();
        var code = await command(o);
        output.WriteLine(o.ToString());
        return (code, o.ToString());
    }
    Task<(int, string)> Release(params string[] args) => Run(o => MemoryCommands.Release(args, o, fixture.Source, On));
    Task<(int, string)> Capsule(params string[] args) => Run(o => MemoryCommands.Capsule(args, o, fixture.Source, On));
    Task<(int, string)> DeleteForever(string typed, params string[] args) =>
        Run(o => MemoryCommands.DeleteForever(args, new StringReader(typed), o, fixture.Source, On));

    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);
    Task Clear() => Scoped.Execute(fixture.Source, "UPDATE memories SET capsule=NULL,capsule_slot=NULL WHERE capsule IS NOT NULL");
    Task<object?> Trail(Guid id) => Scalar(
        "SELECT string_agg(event||':'||channel||':'||coalesce(client,'-'),',' ORDER BY id) FROM memory_events WHERE memory_id=@id", ("id", id));

    // Which memory fills each slot of a capsule as a reader is served it: slot=id, in the capsule's order.
    async Task<string[]> Served(Guid reader, CapsuleRequest request) =>
        [.. (await Capsules.Build(reader, request, default)).Payload["sections"]!.AsArray()
            .Select(x => $"{(string)x!["slot"]!}={(string)x["source_entry_id"]!}")];

    static Guid IdOf(JsonNode answer) => Guid.Parse((string)answer["id"]!);
    static Guid IdIn(string printed) => Guid.Parse(Regex.Match(printed, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}").Value);

    // Story 10 through the app, scopes off: a person puts a confirmed memory into the core capsule's identity slot and a
    // project's memory into that project's decisions slot, and the capsule every AI client reads first serves both; a
    // draft or a held memory waits; taking one out empties its slot. Each slot change is the app's, a version and an audit
    // row like any change.
    [PostgresFact] public async Task A_person_fills_a_core_and_a_project_slot_from_the_app_and_the_capsule_serves_it()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        await Clear();
        var who = await MemoryLogins.Make(s, "slot-app");
        var identity = IdOf(await Ok(who.App, new { op = "capture", content = "DEMO: the household is a family of two who garden at the Wendle plot.", scope = "shared" }));
        await Ok(who.App, new { op = "status", id = identity, status = "canonical" });
        var decision = IdOf(await Ok(who.App, new { op = "capture", content = "DEMO: the Wendle shed gets a new roof in spring.", scope = "shared", project = "wendle-shed" }));
        await Ok(who.App, new { op = "status", id = decision, status = "canonical" });

        var slotted = await Ok(who.App, new { op = "capsule", id = identity, capsule = "core", slot = "identity", expected = 2 });
        Assert.Equal(("capsule", 3, "core", "identity"), ((string)slotted["op"]!, (int)slotted["revision"]!, (string)slotted["capsule"]!["kind"]!,
            (string)slotted["capsule"]!["slot"]!));
        Assert.Equal(["capsule"], slotted["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(("core", "identity"), ((string)(await Ok(who.App, new { op = "get", id = identity }))["memory"]!["capsule"]!["kind"]!,
            (string)(await Ok(who.App, new { op = "get", id = identity }))["memory"]!["capsule"]!["slot"]!));
        // The same slot again changes nothing; a stale revision is a conflict.
        Assert.True((bool)(await Ok(who.App, new { op = "capsule", id = identity, capsule = "core", slot = "identity" }))["unchanged"]!);
        Assert.Equal("conflict", (string)(await Refused(who.App, new { op = "capsule", id = decision, capsule = "project", slot = "decisions", expected = 1 }))["code"]!);
        await Ok(who.App, new { op = "capsule", id = decision, capsule = "project", slot = "decisions", expected = 2 });
        // A project slot is its project's: a memory in no project is refused one, in the store's words.
        var loose = IdOf(await Ok(who.App, new { op = "capture", content = "DEMO: a loose thought about the Wendle hedge.", scope = "shared" }));
        var refused = await Refused(who.App, new { op = "capsule", id = loose, capsule = "project", slot = "decisions" });
        Assert.Equal(("invalid", "A project slot is in its project's capsule: give the memory a project first"), ((string)refused["code"]!, (string)refused["reason"]!));

        // The capsule serves both, to an AI client of the household as to anyone admitted (scopes off: one Family capsule).
        Assert.Equal([$"identity={identity}"], await Served(who.Alex, new("core")));
        Assert.Equal([$"decisions={decision}"], await Served(who.Sam, new("project", "wendle-shed")));
        // A draft's slot waits for its confirmation, a held memory's for a person's release.
        await Ok(who.App, new { op = "capsule", id = loose, capsule = "core", slot = "preferences" });
        Assert.Equal([$"identity={identity}"], await Served(who.Alex, new("core")));
        var held = (await Store.Create(who.Alex, new(Planted[0].Text, Scope: "shared"), Mcp, default)).Id;
        await Store.SetStatus(who.Alex, held, "canonical", null, Mcp, default);
        var heldSlot = await Ok(who.App, new { op = "capsule", id = held, capsule = "core", slot = "constraints" });
        Assert.Equal(MemoryHolds.Instruction, (string)heldSlot["held"]!["reason"]!);
        Assert.Equal([$"identity={identity}"], await Served(who.Alex, new("core")));
        // A list serves a held memory without its slot, as without its project; get serves it whole.
        var listed = (await Ok(who.App, new { op = "list", limit = 50 }))["memories"]!.AsArray();
        Assert.Null(listed.Single(m => IdOf(m!) == held)!["capsule"]);
        Assert.Equal("identity", (string)listed.Single(m => IdOf(m!) == identity)!["capsule"]!["slot"]!);
        Assert.Equal("constraints", (string)(await Ok(who.App, new { op = "get", id = held }))["memory"]!["capsule"]!["slot"]!);

        // Out of its slot: the capsule no longer serves it, and the next slot change is recorded as a change of its own.
        var taken = await Ok(who.App, new { op = "capsule", id = identity, capsule = "none" });
        Assert.Null(taken["capsule"]);
        Assert.Null((await Ok(who.App, new { op = "get", id = identity }))["memory"]!["capsule"]);
        Assert.Empty(await Served(who.Alex, new("core")));
        Assert.True((bool)(await Ok(who.App, new { op = "capsule", id = identity, capsule = "none" }))["unchanged"]!);
        Assert.Equal("created:app:-,status_changed:app:-,capsule_changed:app:-,capsule_changed:app:-", await Trail(identity));
        var history = (await Ok(who.App, new { op = "history", id = identity }))["changes"]!.AsArray();
        Assert.Equal(("capsule_changed", "app"), ((string)history[0]!["event"]!, (string)history[0]!["channel"]!));
        await Clear();
    }

    // With scopes on: the household view slots only what the author lock lets it change (never a person's Family memory),
    // and a viewer session bound to a person, the app's other person's channel, slots that person's own memory into their
    // own capsule, which serves it to their AI client and to nobody else.
    [PostgresFact] public async Task With_scopes_on_the_app_slots_only_what_its_login_may_change()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await Clear();
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "slot-scoped");
            var viewer = await Scoped.Principal(s, "slot-scoped-viewer", "app", "alex");
            var own = (await Store.Create(who.Alex, new("DEMO: Alex prefers the early ferry to Larrow."), Mcp, default)).Id;
            await Store.SetStatus(who.Alex, own, "canonical", null, Mcp, default);
            var family = (await Store.Create(who.Alex, new("DEMO: the Larrow ferry rota is Alex's to keep.", Scope: "shared"), Mcp, default)).Id;
            await Store.SetStatus(who.Alex, family, "canonical", null, Mcp, default);

            // The household view reads no private memory and may not change Alex's Family one.
            Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "capsule", id = own, capsule = "core", slot = "preferences" }))["code"]!);
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "capsule", id = family, capsule = "core", slot = "principles" }))["code"]!);
            // Alex's viewer session slots both: his own into his capsule, his Family one into Family's.
            await Ok(viewer, new { op = "capsule", id = own, capsule = "core", slot = "preferences" });
            await Ok(viewer, new { op = "capsule", id = family, capsule = "core", slot = "principles" });
            Assert.Equal([$"preferences={own}"], await Served(who.Alex, new("core")));
            Assert.Equal([$"principles={family}"], await Served(who.Sam, new("core", Scope: "shared")));
            Assert.Empty(await Served(who.Sam, new("core")));
            Assert.Equal("capsule_changed:app", (string)(await Scalar(
                "SELECT event||':'||channel FROM memory_events WHERE memory_id=@id AND principal_id=@p", ("id", own), ("p", viewer)))!);
        }
        finally { await Clear(); await Scoped.Mode(s, "off"); }
    }

    // Story 8 through the app: the review queue lists the held memories its reader reads, newest held first, with why and
    // how each came and never their words; its count is its length; a person releases several at once at the revisions
    // they read, one changed since staying held.
    [PostgresFact] public async Task The_apps_review_queue_lists_what_its_reader_reads_and_releases_several_at_the_revisions_read()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "review-app");
        var since = await MemoryLogins.Now(s);
        var held = new List<Guid>();
        foreach (var planted in Planted.Take(3)) held.Add((await Store.Create(who.Alex, new(planted.Text, ["review-app"]), Mcp, default)).Id);
        var clean = (await Store.Create(who.Alex, new("DEMO: the Pellow kettle needs descaling."), Mcp, default)).Id;

        async Task<List<JsonNode>> Queue()
        {
            var all = new List<JsonNode>();
            string? cursor = null;
            do
            {
                var page = await Ok(who.App, cursor is null ? new { op = "review", limit = 2 } : new { op = "review", limit = 2, cursor });
                all.AddRange(page["review"]!.AsArray().Select(i => i!));
                cursor = (string?)page["next"];
                Assert.Equal(all.Count < (int)page["count"]!, cursor is not null);
            } while (cursor is not null);
            return all;
        }
        var queue = await Queue();
        var count = (int)(await Ok(who.App, new { op = "review" }))["count"]!;
        Assert.Equal(count, queue.Count);
        var mine = queue.Where(i => held.Contains(IdOf(i))).ToList();
        Assert.Equal(held.AsEnumerable().Reverse(), mine.Select(IdOf));
        Assert.DoesNotContain(queue, i => IdOf(i) == clean);
        foreach (var item in mine)
        {
            Assert.Equal(("instruction", "mcp", "DEMO client", "alex", true, 1), ((string)item["reason"]!, (string)item["channel"]!, (string)item["client"]!,
                (string)item["heldBy"]!, (bool)item["mayRelease"]!, (int)item["revision"]!));
            foreach (var words in new[] { "content", "tags", "project", "preview" }) Assert.False(item.AsObject().ContainsKey(words), words);
            Assert.True(DateTimeOffset.Parse((string)item["heldAt"]!) >= since);
        }
        Assert.Equal(count, (int)(await Ok(who.App, new { op = "review", reason = "instruction" }))["count"]! + (int)(await Ok(who.App, new { op = "review", reason = "hidden" }))["count"]!
            + (int)(await Ok(who.App, new { op = "review", reason = "too_long" }))["count"]! + (int)(await Ok(who.App, new { op = "review", reason = "burst" }))["count"]!
            + (int)(await Ok(who.App, new { op = "review", reason = "held_project" }))["count"]!);

        // The third changed since it was read: it stays held. An unknown one is answered on its own.
        await Store.Append(who.Alex, held[2], "DEMO: more words.", null, Mcp, default);
        var stranger = Guid.NewGuid();
        var released = await Ok(who.App, new { op = "release_all", memories = new object[]
        {
            new { id = held[0], expected = 1 }, new { id = held[1], expected = 1 }, new { id = held[2], expected = 1 }, new { id = stranger, expected = 1 },
        } });
        Assert.Equal("release_all", (string)released["op"]!);
        Assert.Equal([$"{held[0]}:released:2", $"{held[1]}:released:2", $"{held[2]}:conflict:2", $"{stranger}:unknown:"],
            released["results"]!.AsArray().Select(r => $"{(string)r!["id"]!}:{(string)r["result"]!}:{(int?)r["revision"]}"));
        Assert.Null((await Store.Get(who.Alex, held[0], default))!.Hold);
        Assert.Equal(MemoryHolds.Instruction, (await Store.Get(who.Alex, held[2], default))!.Hold);
        Assert.Equal([held[2]], (await Queue()).Select(IdOf).Where(held.Contains));
        // Released again: unchanged. Each memory once a call.
        Assert.Equal("unchanged", (string)(await Ok(who.App, new { op = "release_all", memories = new[] { new { id = held[0], expected = 2 } } }))["results"]![0]!["result"]!);
        Assert.Equal("invalid", (string)(await Refused(who.App, new { op = "release_all", memories = new[] { new { id = held[2], expected = 2 }, new { id = held[2], expected = 2 } } }))["code"]!);
        Assert.Equal("released:app:-", ((string)(await Trail(held[0]))!).Split(',')[1]);
    }

    // With scopes on the queue is its reader's: the household view lists Family's held memories only, and may release
    // none a person wrote (the author lock); a viewer session bound to Alex lists his own and Family's and releases his.
    [PostgresFact] public async Task With_scopes_on_the_apps_review_queue_is_its_readers()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "review-scoped");
            var viewer = await Scoped.Principal(s, "review-scoped-viewer", "app", "alex");
            var own = (await Store.Create(who.Alex, new(Planted[0].Text), Mcp, default)).Id;
            var family = (await Store.Create(who.Alex, new(Planted[1].Text, Scope: "shared"), Mcp, default)).Id;
            var sams = (await Store.Create(who.Sam, new(Planted[2].Text), Mcp, default)).Id;

            var house = (await Ok(who.App, new { op = "review", limit = 50 }))["review"]!.AsArray().ToDictionary(i => IdOf(i!), i => i!);
            Assert.False(house.ContainsKey(own));
            Assert.False(house.ContainsKey(sams));
            Assert.False((bool)house[family]["mayRelease"]!);
            Assert.Equal("forbidden", (string)(await Ok(who.App, new { op = "release_all", memories = new[] { new { id = family, expected = 1 } } }))["results"]![0]!["result"]!);
            Assert.Equal("unknown", (string)(await Ok(who.App, new { op = "release_all", memories = new[] { new { id = own, expected = 1 } } }))["results"]![0]!["result"]!);

            var alex = (await Ok(viewer, new { op = "review", limit = 50 }))["review"]!.AsArray().ToDictionary(i => IdOf(i!), i => i!);
            Assert.True((bool)alex[own]["mayRelease"]!);
            Assert.True((bool)alex[family]["mayRelease"]!);
            Assert.False(alex.ContainsKey(sams));
            var privately = await Ok(viewer, new { op = "review", scope = "private", limit = 50 });
            Assert.Equal((int)privately["count"]!, privately["review"]!.AsArray().Count);
            Assert.Contains(own, privately["review"]!.AsArray().Select(i => IdOf(i!)));
            Assert.DoesNotContain(family, privately["review"]!.AsArray().Select(i => IdOf(i!)));
            var results = (await Ok(viewer, new { op = "release_all", memories = new[] { new { id = own, expected = 1 }, new { id = family, expected = 1 }, new { id = sams, expected = 1 } } }))["results"]!.AsArray();
            Assert.Equal(["released", "released", "unknown"], results.Select(r => (string)r!["result"]!));
            Assert.Equal(MemoryHolds.Instruction, (await Store.Get(who.Sam, sams, default))!.Hold);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A person at the terminal, scopes on: Alex puts his confirmed memory into his own core capsule and a project's into
    // its capsule, which serve them to his AI client and never to Sam; a draft waits and the answer says so; Sam may not
    // slot what he does not read, nor Alex's Family memory (the author lock). Each is the terminal's, through Alex's
    // terminal login.
    [PostgresFact] public async Task A_person_fills_a_core_and_a_project_slot_from_the_terminal_and_the_capsule_serves_it()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await Clear();
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "slot-cli");
            var preference = (await Store.Create(who.Alex, new("DEMO: Alex likes answers short, with the reasoning last."), Mcp, default)).Id;
            await Store.SetStatus(who.Alex, preference, "canonical", null, Mcp, default);
            var state = (await Store.Create(who.Alex, new("DEMO: the Brindle seed order is half done.", Project: "brindle-seeds"), Mcp, default)).Id;
            await Store.SetStatus(who.Alex, state, "canonical", null, Mcp, default);

            var (code, said) = await Capsule("--person", "alex", preference.ToString(), "core", "preferences");
            Assert.Equal(0, code);
            Assert.StartsWith($"In alex's own core capsule as preferences: {preference} (revision 3).", said);
            var (_, project) = await Capsule("--person", "alex", state.ToString(), "project", "current-state", "--revision", "2");
            Assert.StartsWith($"In the brindle-seeds project's capsule (alex's own) as current-state: {state} (revision 3).", project);
            Assert.Equal([$"preferences={preference}"], await Served(who.Alex, new("core")));
            Assert.Equal([$"current-state={state}"], await Served(who.Alex, new("project", "brindle-seeds")));
            Assert.Empty(await Served(who.Sam, new("core")));
            Assert.Contains("already, so nothing changed", (await Capsule("--person", "alex", preference.ToString(), "core", "preferences")).Item2);
            // The terminal's, through Alex's own terminal login.
            var login = await TerminalLogin.For(s, "alex", default);
            Assert.Equal($"capsule_changed:cli:rhythm:{login}", await Scalar(
                "SELECT event||':'||channel||':'||client||':'||principal_id FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", preference)));

            // A draft is slotted, and waits for its confirmation; the answer says so.
            var draft = (await Store.Create(who.Alex, new("DEMO: Alex never books trains before nine."), Mcp, default)).Id;
            var (_, waits) = await Capsule("--person", "alex", draft.ToString(), "core", "constraints");
            Assert.Contains("It is a draft: a capsule serves confirmed memories only", waits);
            Assert.Equal([$"preferences={preference}"], await Served(who.Alex, new("core")));
            // A stale revision, a project slot for a memory in no project, and an unknown memory: nothing changes.
            Assert.Contains("Nothing changed: it changed since revision 1", (await Capsule("--person", "alex", preference.ToString(), "core", "identity", "--revision", "1")).Item2);
            Assert.Contains("give the memory a project first", (await Capsule("--person", "alex", draft.ToString(), "project", "decisions")).Item2);
            // Sam reads none of Alex's own, and may not slot Alex's Family memory.
            var (refused, unread) = await Capsule("--person", "sam", preference.ToString(), "core", "identity");
            Assert.Equal((1, true), (refused, unread.StartsWith("No memory with that id that sam reads", StringComparison.Ordinal)));
            var family = (await Store.Create(who.Alex, new("DEMO: the household's Brindle plot is shared.", Scope: "shared"), Mcp, default)).Id;
            Assert.StartsWith("Nothing changed: ", (await Capsule("--person", "sam", family.ToString(), "core", "identity")).Item2);
            Assert.Null(await Scalar("SELECT capsule FROM memories WHERE id=@id", ("id", family)));
            // Out of its slot.
            Assert.StartsWith($"Taken out of its capsule slot: {preference}", (await Capsule("--person", "alex", preference.ToString(), "none")).Item2);
            Assert.Empty(await Served(who.Alex, new("core")));
        }
        finally { await Clear(); await Scoped.Mode(s, "off"); }
    }

    // A person at the terminal reviews and releases: the queue they read, never with words; one held memory whole, every
    // hidden character shown as its code; its release only at the revision read, so one that changed since stays held.
    [PostgresFact] public async Task A_person_reviews_and_releases_a_hold_from_the_terminal()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "release-cli");
            var hidden = (await Store.Create(who.Alex, new("DEMO: the Larrow gate\u200B\u200B\u200B code is 4\u202E21.", ["larrow"]), Mcp, default)).Id;
            var told = (await Store.Create(who.Alex, new(Planted[0].Text), Mcp, default)).Id;
            var sams = (await Store.Create(who.Sam, new(Planted[1].Text), Mcp, default)).Id;

            var (code, queue) = await Release("--person", "alex");
            Assert.Equal(0, code);
            Assert.Contains($"{hidden}  private  held: it holds hidden characters", queue);
            Assert.Contains($"{told}  private  held: it reads like instructions", queue);
            Assert.Contains("written by an AI client (DEMO client) for alex", queue);
            Assert.DoesNotContain(sams.ToString(), queue);
            Assert.DoesNotContain("Larrow", queue);
            // Review means reading: the queue names no revision, so the one a release needs is shown only beside the
            // memory whole, with the command that releases it.
            Assert.DoesNotContain("revision", queue);
            Assert.Contains("held for review in all. Nothing here shows their words", queue);
            Assert.Contains($"{hidden}", (await Release("--person", "alex", "--private", "--reason", "hidden")).Item2);
            Assert.DoesNotContain($"{told}", (await Release("--person", "alex", "--private", "--reason", "hidden")).Item2);

            var (_, read) = await Release("--person", "alex", hidden.ToString());
            Assert.Contains("Held for review: it holds hidden characters", read);
            Assert.Contains("  DEMO: the Larrow gate[U+200B][U+200B][U+200B] code is 4[U+202E]21.", read);
            Assert.Contains("Tags: #larrow", read);
            Assert.Contains($"./rhythm release --person alex {hidden} --revision 1", read);
            Assert.False(read.Contains('\u202E'), "a direction control reached the terminal");
            // Sam reads neither of Alex's own.
            Assert.StartsWith("No memory with that id that sam reads", (await Release("--person", "sam", hidden.ToString())).Item2);
            Assert.Equal(1, (await Release("--person", "sam", hidden.ToString(), "--revision", "1")).Item1);
            Assert.Equal(MemoryHolds.Hidden, (await Store.Get(who.Alex, hidden, default))!.Hold);

            // Changed since it was read: it stays held. At the revision read, it is released, through Alex's terminal login.
            await Store.Append(who.Alex, told, "DEMO: and more.", null, Mcp, default);
            var (stale, notYet) = await Release("--person", "alex", told.ToString(), "--revision", "1");
            Assert.Equal((1, true), (stale, notYet.StartsWith("Not released: it changed since revision 1 and is at revision 2 now.", StringComparison.Ordinal)));
            Assert.Equal(MemoryHolds.Instruction, (await Store.Get(who.Alex, told, default))!.Hold);
            var (done, released) = await Release("--person", "alex", hidden.ToString(), "--revision", "1");
            Assert.Equal((0, $"Released: {hidden} is no longer held (revision 2)."), (done, released.Split('\n')[0].Split(" Search")[0]));
            Assert.Null((await Store.Get(who.Alex, hidden, default))!.Hold);
            var login = await TerminalLogin.For(s, "alex", default);
            Assert.Equal($"released:cli:rhythm:{login}", await Scalar(
                "SELECT event||':'||channel||':'||client||':'||principal_id FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", hidden)));
            Assert.StartsWith("It was not held, so nothing changed", (await Release("--person", "alex", hidden.ToString(), "--revision", "2")).Item2);
            Assert.StartsWith("It is not held for review", (await Release("--person", "alex", hidden.ToString())).Item2);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A person deletes for good from the terminal: what their trash holds; one memory, shown, deleted only once they type
    // its id, with the nonce of the trip into the trash shown; never one outside the trash or another person's.
    [PostgresFact] public async Task A_person_deletes_a_memory_forever_from_the_terminal()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "delete-cli");
            var id = (await Store.Create(who.Alex, new("DEMO: the old Ottery locker code, now changed."), Mcp, default)).Id;
            var kept = (await Store.Create(who.Alex, new("DEMO: the Ottery locker is number nine."), Mcp, default)).Id;
            Assert.StartsWith("No memory with that id in the trash that alex may restore", (await DeleteForever(id.ToString(), "--person", "alex", id.ToString())).Item2);
            await Store.Update(who.Alex, id, new(Content: "DEMO: the old Ottery locker code, changed twice."), null, Mcp, default);
            await Store.Forget(who.Alex, id, null, Mcp, default);

            var (code, listed) = await DeleteForever("", "--person", "alex");
            Assert.Equal(0, code);
            Assert.Contains($"{id}  private  14 days left  DEMO: the old Ottery locker code, changed twice.", listed);
            Assert.DoesNotContain(kept.ToString(), listed);
            Assert.DoesNotContain(id.ToString(), (await DeleteForever("", "--person", "sam")).Item2);
            // Sam may not, typing its id or not; Alex keeps it by typing anything else.
            Assert.StartsWith("No memory with that id in the trash that sam may restore", (await DeleteForever(id.ToString(), "--person", "sam", id.ToString())).Item2);
            var (keep, kept2) = await DeleteForever("yes", "--person", "alex", id.ToString());
            Assert.Equal((1, true), (keep, kept2.Contains("Nothing deleted: it stays in the trash.")));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM memories WHERE id=@id", ("id", id)));
            Assert.Equal(1, (await DeleteForever("", "--person", "alex", id.ToString())).Item1);

            var (done, gone) = await DeleteForever(id + "\n", "--person", "alex", id.ToString());
            Assert.Equal(0, done);
            Assert.Contains("Type the memory's id to delete it for good", gone);
            Assert.Contains($"Deleted for good: {id}, with 1 earlier version and 0 links.", gone);
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM memories WHERE id=@id", ("id", id)));
            var login = await TerminalLogin.For(s, "alex", default);
            Assert.Equal($"purged:cli:rhythm:{login}", await Scalar(
                "SELECT event||':'||channel||':'||client||':'||principal_id FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", id)));
            Assert.StartsWith("No memory with that id in the trash", (await DeleteForever(id.ToString(), "--person", "alex", id.ToString())).Item2);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // No AI client's token reaches a person's step, whatever it names: through MCP each is refused before anything is
    // written (a slot, a release, several releases, a delete for good), the queue offers it no release, and naming the
    // app's or the terminal's channel is refused by the database (044's memory_channel), for a person-bound token too.
    // The app's handler, given an AI client's login, writes nothing either.
    [PostgresFact] public async Task No_AI_clients_token_reaches_a_persons_step()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "steps-ai");
        var held = (await Store.Create(who.Alex, new(Planted[0].Text), Mcp, default)).Id;
        var plain = (await Store.Create(who.Alex, new("DEMO: the Hollin bins go out on Monday."), Mcp, default)).Id;
        await Store.SetStatus(who.Alex, plain, "canonical", null, Mcp, default);
        var trashed = (await Store.Create(who.Alex, new("DEMO: a Hollin note to forget."), Mcp, default)).Id;
        var trip = await Store.Forget(who.Alex, trashed, null, Mcp, default);
        var events = await Scalar("SELECT count(*) FROM memory_events");

        foreach (var ai in new[] { who.Alex, who.House })
        {
            foreach (var call in new[] { Mcp, new MemoryCall("cli", "DEMO not a terminal"), new MemoryCall("app") })
            {
                var expected = call.Channel == "mcp" ? "forbidden" : "invalid";
                Assert.Equal(expected, await MemoryLogins.Refused(() => Store.SetCapsule(ai, plain, "core", "identity", null, call, default)));
                Assert.Equal(expected, await MemoryLogins.Refused(() => Store.SetCapsule(ai, plain, null, null, null, call, default)));
                Assert.Equal(expected, await MemoryLogins.Refused(() => Store.Release(ai, held, null, call, default)));
                Assert.Equal(expected, await MemoryLogins.Refused(() => Store.DeleteForever(ai, trashed, trip.Trash.Nonce, trashed.ToString(), call, default)));
                if (call.Channel == "mcp")
                    Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.ReleaseAll(ai, [(held, 1)], call, default)));
                else
                    Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.ReleaseAll(ai, [(held, 1)], call, default)));
                Assert.All((await Store.Review(ai, new(Limit: 50), call, default)).Items, i => Assert.False(i.MayRelease));
                Assert.All((await Store.Trash(ai, 50, null, 0, call, default)).Items, t => Assert.False(t.MayDeleteForever));
            }
            // The app's handler, if an AI client's login ever reached it, writes as the app, which the database refuses it.
            foreach (var step in new object[] { new { op = "capsule", id = plain, capsule = "core", slot = "identity" }, new { op = "release", id = held },
                         new { op = "release_all", memories = new[] { new { id = held, expected = 1 } } },
                         new { op = "delete", id = trashed, nonce = trip.Trash.Nonce, confirm = trashed } })
            {
                var (status, content) = await App(ai, step);
                Assert.Equal(200, status);
                Assert.Equal((false, "invalid"), ((bool)content!["ok"]!, (string)content["code"]!));
            }
        }
        Assert.Equal(events, await Scalar("SELECT count(*) FROM memory_events"));
        Assert.Equal(MemoryHolds.Instruction, (await Store.Get(who.Alex, held, default))!.Hold);
        Assert.Null(await Scalar("SELECT capsule FROM memories WHERE id=@id", ("id", plain)));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM memories WHERE id=@id", ("id", trashed)));

        // The same steps through the app's own login and the person's terminal login are taken (each test above takes them
        // whole); here, that the terminal's are its own login's and never the AI client's, whose person it shares.
        var at = await TerminalLogin.For(s, "alex", default);
        Assert.NotEqual(who.Alex, at);
        Assert.Equal(["capsule"], (await Store.SetCapsule(at, plain, "core", "identity", null, Cli, default)).Changed);
        Assert.Null((await Store.Release(at, held, 1, Cli, default)).Hold);
        await Clear();
    }
}
