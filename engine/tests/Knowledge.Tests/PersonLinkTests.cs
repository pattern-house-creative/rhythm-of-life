using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Cli;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// A person's own link steps (run 17; API-21; SB-P4-02, SB-P4-05, SB-P4-08 and SB-P4-09's person halves;
// docs/dev/memory.md section 5): in the app's Memories view (app_memories connections, link, unlink, stale, still_true,
// clear_flags) and at the terminal through the person's own login (rhythm link, unlink, still-true, clear-flags,
// undismiss). Each is a person's channel, never an AI client's token, and keeps the rules the MCP tools have (the author
// lock, one scope, the holds): the same checked functions decide (052, 053, 049, 057). With memory.tools off neither the
// app nor the terminal offers any of it, and help is the list it was. Fictional people only (alex, sam, the household);
// every text is DEMO.
public sealed class PersonLinkTests
{
    static readonly Guid Id = Guid.Parse("6d3c2a10-0000-4000-8000-00000000e001"), Other = Guid.Parse("6d3c2a10-0000-4000-8000-00000000e002");
    static readonly string[] Ops = ["connections", "stale", "link", "unlink", "still_true", "clear_flags"];
    static readonly string[] Commands = ["link", "unlink", "still-true", "clear-flags", "undismiss"];

    [Fact] public void The_apps_link_steps_take_exactly_their_arguments()
    {
        bool Takes(object arguments) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true);
        Assert.True(Takes(new { op = "connections", id = Id }));
        Assert.True(Takes(new { op = "connections", id = Id, type = "relates_to", limit = 50 }));
        Assert.False(Takes(new { op = "connections" }));
        Assert.False(Takes(new { op = "connections", id = Id, limit = 51 }));
        Assert.False(Takes(new { op = "connections", id = Id, type = "Relates To" }));
        Assert.True(Takes(new { op = "stale" }));
        Assert.True(Takes(new { op = "stale", scope = "private", cursor = "abc", limit = 50 }));
        Assert.False(Takes(new { op = "stale", limit = 0 }));
        Assert.False(Takes(new { op = "stale", reason = "burst" }));
        Assert.True(Takes(new { op = "link", id = Id, target = Other }));
        Assert.True(Takes(new { op = "link", id = Id, target = Other, type = "caused_by" }));
        Assert.False(Takes(new { op = "link", id = Id }));
        Assert.False(Takes(new { op = "link", id = Id, target = "not-a-memory" }));
        // A person states a link: no confidence, and no idempotency key (linking again answers unchanged).
        Assert.False(Takes(new { op = "link", id = Id, target = Other, confidence = 0.5 }));
        Assert.False(Takes(new { op = "link", id = Id, target = Other, key = "link-key-0001" }));
        Assert.True(Takes(new { op = "unlink", id = Id, target = Other }));
        Assert.True(Takes(new { op = "unlink", id = Id, target = Other, type = "similar_to" }));
        Assert.False(Takes(new { op = "unlink", id = Id, target = Other, type = "" }));
        Assert.True(Takes(new { op = "still_true", id = Id, expected = 3 }));
        Assert.False(Takes(new { op = "still_true", id = Id, expected = 0 }));
        Assert.False(Takes(new { op = "still_true", id = Id, key = "still-true-0001" }));
        Assert.True(Takes(new { op = "clear_flags", id = Id }));
        Assert.False(Takes(new { op = "clear_flags", id = Id, clear = new[] { "retracted_sources" } }));
        // No op names its channel or its client: the handler fixes both.
        foreach (var call in new object[]
                 {
                     new { op = "link", id = Id, target = Other, channel = "cli" }, new { op = "unlink", id = Id, target = Other, client = "DEMO" },
                     new { op = "still_true", id = Id, channel = "app" }, new { op = "clear_flags", id = Id, channel = "mcp" },
                     new { op = "stale", client = "rhythm" }, new { op = "connections", id = Id, channel = "cli" },
                 })
            Assert.False(Takes(call));
        // The listing names every op the handler takes, the new ones among them, and the arguments they take.
        var listing = JsonSerializer.SerializeToNode(AppMemories.Listing())!["inputSchema"]!["properties"]!;
        var ops = listing["op"]!["enum"]!.AsArray().Select(o => (string)o!).ToArray();
        Assert.Equal([.. AppMemories.Reads, .. AppMemories.Writes], ops);
        foreach (var op in Ops) Assert.Contains(op, ops);
        Assert.Contains("connections", AppMemories.Reads);
        Assert.Contains("stale", AppMemories.Reads);
        Assert.Equal("uuid", (string)listing["target"]!["format"]!);
        Assert.Equal("^[a-z][a-z0-9_-]{0,63}$", (string)listing["type"]!["pattern"]!);
    }

    // With memory.tools off, absent or empty the app has no Memories tool at all, so no link step, and the terminal answers
    // each step as a command the engine does not know, before it reads its arguments or the database; help names none of
    // them, and is the list it was. On, help lists each after the person's earlier steps.
    [Fact] public async Task With_memory_tools_off_neither_the_app_nor_the_terminal_offers_a_link_step()
    {
        var id = Id.ToString();
        var other = Other.ToString();
        foreach (var config in new[] { TestSettings.Use(), MemoryToolSettings.With("tools: off"), MemoryToolSettings.With(""),
                     MemoryToolSettings.With("capsules: on, share: on") })
        {
            foreach (var act in new Func<Task<int>>[]
                     {
                         () => MemoryCommands.Link(["--person", "alex", id], new StringWriter(), config: config),
                         () => MemoryCommands.Link(["--person", "alex", id, other, "--type", "caused_by"], new StringWriter(), config: config),
                         () => MemoryCommands.Unlink(["--person", "alex", id, other], new StringWriter(), config: config),
                         () => MemoryCommands.StillTrue(["--person", "alex"], new StringWriter(), config: config),
                         () => MemoryCommands.StillTrue(["--person", "alex", id, "--revision", "2"], new StringWriter(), config: config),
                         () => MemoryCommands.ClearFlags(["--person", "alex", id], new StringWriter(), config: config),
                         () => MemoryCommands.Undismiss(["--person", "alex"], new StringWriter(), config: config),
                         () => MemoryCommands.Undismiss(["anything", "at", "all"], new StringWriter(), config: config),
                     })
                Assert.Equal(MemoryCommands.UnknownCommand, (await Assert.ThrowsAsync<ArgumentException>(act)).Message);
            foreach (var op in new object[] { new { op = "link", id = Id, target = Other }, new { op = "stale" }, new { op = "still_true", id = Id } })
            {
                var (status, content) = await AppMemoryCalls.Call(AppMemoryCalls.Services(config), Guid.NewGuid(), AppMemoryCalls.Body(AppEndpoint.Memories, op));
                Assert.Equal((403, "forbidden"), (status, (string)content!["error"]!["code"]!));
                Assert.False(AppEndpoint.Allowed(AppMemoryCalls.Request(op)));
            }
        }
        var before = CliCommands.Help(false);
        Assert.EndsWith("that person's memories, newest first\n  check-config | refresh | migrate | serve", before);
        Assert.Equal(before, CliCommands.Help(false, true));
        var steps = CliCommands.Help(true);
        foreach (var command in Commands)
        {
            Assert.DoesNotContain($" {command} ", before);
            Assert.Contains($"\n  {command} --person", steps);
            Assert.Contains(command, CliCommands.Names);
            Assert.Contains(command, MemoryCommands.PersonSteps);
        }
        // The new lines follow set-status's and come before the engine's own commands.
        Assert.True(steps.IndexOf("\n  set-status --person", StringComparison.Ordinal) < steps.IndexOf("\n  link --person", StringComparison.Ordinal));
        Assert.EndsWith("the map's dismissals, or one taken back\n  check-config | refresh | migrate | serve", steps);
        TestSettings.Use();
    }

    [Fact] public async Task Each_link_step_at_the_terminal_names_what_it_needs()
    {
        var on = MemoryToolSettings.On;
        var id = Id.ToString();
        var other = Other.ToString();
        foreach (var args in new[] { new[] { id }, ["--person", "alex"], ["--person", "alex", id, other, other], ["--person", "alex", "not-an-id"],
                     ["--person", "alex", id, other, "--limit", "5"], ["--person", "alex", id, "--limit", "51"], ["--person", "alex", id, "--type"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.Link(args, new StringWriter(), config: on));
        foreach (var args in new[] { new[] { "--person", "alex", id }, [id, other], ["--person", "alex", id, "not-an-id"], ["--person", "alex", id, other, "--revision", "2"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.Unlink(args, new StringWriter(), config: on));
        foreach (var args in new[] { new[] { id }, ["--person", "alex", "--revision", "2"], ["--person", "alex", id, "--family"], ["--person", "alex", id, id],
                     ["--person", "alex", "--family", "--private"], ["--person", "alex", id, "--revision", "0"], ["--person", "alex", "--next", "not-a-cursor"],
                     ["--person", "alex", "--limit", "51"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.StillTrue(args, new StringWriter(), config: on));
        foreach (var args in new[] { new[] { "--person", "alex" }, [id], ["--person", "alex", id, id], ["--person", "alex", id, "--revision", "x"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.ClearFlags(args, new StringWriter(), config: on));
        foreach (var args in new[] { new[] { "--limit", "5" }, ["--person", "alex", "stars:one:two:similar_to"], ["--person", "alex", "a", "b"],
                     ["--person", "alex", $"memories:{Id}:{Other}:similar_to", "--limit", "5"], ["--person", "alex", "--limit", "51"] })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryCommands.Undismiss(args, new StringWriter(), config: on));
        TestSettings.Use();
    }
}

public sealed class PersonLinkDatabaseTests(DatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    static readonly IReadOnlyList<ParityMemory> Planted = [.. ParityCorpus.Load().Memories.Where(m => m.Expect?.Held == true)];
    static readonly MemoryCall Mcp = MemoryLogins.Mcp, Cli = new("cli", "rhythm");
    MemoryStore Store => new(fixture.Source);
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

    async Task<string> Refused(Guid app, object arguments)
    {
        var (status, content) = await App(app, arguments);
        Assert.Equal(200, status);
        Assert.False((bool)content!["ok"]!, content.ToJsonString());
        return (string)content["code"]!;
    }

    // One terminal command's exit code and what it printed, which goes to the test's log too.
    async Task<(int Code, string Out)> Run(Func<TextWriter, Task<int>> command)
    {
        var o = new StringWriter { NewLine = "\n" };
        var code = await command(o);
        output.WriteLine(o.ToString());
        return (code, o.ToString());
    }
    Task<(int, string)> Link(params string[] args) => Run(o => MemoryCommands.Link(args, o, fixture.Source, On));
    Task<(int, string)> Unlink(params string[] args) => Run(o => MemoryCommands.Unlink(args, o, fixture.Source, On));
    Task<(int, string)> StillTrue(params string[] args) => Run(o => MemoryCommands.StillTrue(args, o, fixture.Source, On));
    Task<(int, string)> ClearFlags(params string[] args) => Run(o => MemoryCommands.ClearFlags(args, o, fixture.Source, On));

    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);
    Task<object?> Trail(Guid id) => Scalar(
        "SELECT string_agg(event||':'||channel||':'||coalesce(client,'-'),',' ORDER BY id) FROM memory_events WHERE memory_id=@id", ("id", id));
    Task<object?> Newest(Guid id) => Scalar(
        "SELECT event||':'||channel||':'||coalesce(client,'-')||':'||principal_id FROM memory_events WHERE memory_id=@id ORDER BY id DESC LIMIT 1", ("id", id));
    async Task<long> Dismissals(Guid a, Guid b, string type) => (long)(await Scalar(
        "SELECT count(*) FROM memory_link_dismissals WHERE low_id=least(@a,@b) AND high_id=greatest(@a,@b) AND type=@t", ("a", a), ("b", b), ("t", type)))!;
    Task Dismissed(Guid a, Guid b, string type, Guid by) => Scoped.Execute(fixture.Source,
        "INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(least(@a,@b),greatest(@a,@b),@t,@p)", ("a", a), ("b", b), ("t", type), ("p", by));

    async Task<Guid> Remember(Guid who, string text, string? scope = null, string? kind = null, string? volatility = null) =>
        (await Store.Create(who, new(text, Scope: scope, Kind: kind, Volatility: volatility), Mcp, default)).Id;

    // The memory as the nightly pass leaves one whose volatility's time has passed: unchanged for `days`, marked at its
    // revision (053's memory_checks), so the stale queue holds it (not_confirmed).
    async Task Stale(Guid id, int days = 20)
    {
        await Staleness.Age(fixture.Source, id, days);
        await Scoped.Execute(fixture.Source, """
            INSERT INTO memory_checks(memory_id,stale_at,stale_revision,stale_since) SELECT id,now(),revision,updated_at FROM memories WHERE id=@m
            ON CONFLICT(memory_id) DO UPDATE SET stale_at=EXCLUDED.stale_at,stale_revision=EXCLUDED.stale_revision,stale_since=EXCLUDED.stale_since
            """, ("m", id));
    }

    // A memory built on another (drawn_from), whose source is then marked wrong: the cascade flags it for a check (049).
    async Task<(Guid Built, Guid Source)> Flagged(Guid who, string scope, string built, string source)
    {
        var s = await Remember(who, source, scope);
        var b = await Remember(who, built, scope);
        await Store.Link(who, b, s, "drawn_from", null, Mcp, default);
        await Store.SetStatus(who, s, "deprecated", null, Mcp, default);
        Assert.Equal([s], (await Store.Get(who, b, default))!.RetractedSources);
        return (b, s);
    }

    static Guid IdOf(JsonNode answer) => Guid.Parse((string)answer["id"]!);

    // API-21 through the app, scopes off: a person links two memories (verified, made by the app's login), reads them from
    // either end, finds an AI client's link of another type among them and confirms it by making it again, and takes a link
    // away; the rules the MCP tools keep hold (a held memory, the episodic rule, a replacement is supersede's, an unknown
    // memory), and a link writes no audit row.
    [PostgresFact] public async Task A_person_links_two_memories_from_the_app_and_takes_the_link_away()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "link-app");
        var leak = IdOf(await Ok(who.App, new { op = "capture", content = "DEMO: the Wendle shed roof leaked in the storm.", scope = "shared", kind = "episodic" }));
        var fix = IdOf(await Ok(who.App, new { op = "capture", content = "DEMO: the Wendle shed roof was fixed on Saturday.", scope = "shared", kind = "episodic" }));

        var linked = await Ok(who.App, new { op = "link", id = fix, target = leak, type = "caused_by" });
        Assert.Equal(("link", "verified", true, false, "caused_by", fix, leak), ((string)linked["op"]!, (string)linked["verification"]!,
            (bool)linked["created"]!, (bool)linked["unchanged"]!, (string)linked["type"]!, IdOf(linked), Guid.Parse((string)linked["target"]!)));
        var link = Guid.Parse((string)linked["link"]!);
        Assert.Equal($"verified:{who.App}", await Scalar("SELECT verification||':'||made_by FROM made_links WHERE link_id=@id", ("id", link)));
        Assert.True((bool)(await Ok(who.App, new { op = "link", id = fix, target = leak, type = "caused_by" }))["unchanged"]!);

        // Read from either end: outgoing from the one it goes from, incoming at the other; the app's own login made it.
        var from = (await Ok(who.App, new { op = "connections", id = fix }))["connections"]!.AsArray().Single(c => (bool)c!["made"]!)!;
        Assert.Equal((link.ToString(), "caused_by", "outgoing", "verified", true, true, leak.ToString()), ((string)from["linkId"]!, (string)from["type"]!,
            (string)from["direction"]!, (string)from["verification"]!, (bool)from["mine"]!, (bool)from["mayUnlink"]!, (string)from["memory"]!["id"]!));
        Assert.Equal("DEMO: the Wendle shed roof leaked in the storm.", (string)from["memory"]!["preview"]!);
        Assert.Equal("incoming", (string)(await Ok(who.App, new { op = "connections", id = leak }))["connections"]!.AsArray().Single(c => (bool)c!["made"]!)!["direction"]!);

        // An AI client's link is its guess (inferred); a person's link of its type confirms it, the same link.
        var guess = await Store.Link(who.Alex, leak, fix, "relates_to", 0.6, Mcp, default);
        Assert.Equal("inferred", guess.Verification);
        var listed = (await Ok(who.App, new { op = "connections", id = fix, type = "relates_to" }))["connections"]!.AsArray().Single()!;
        Assert.Equal(("inferred", false, true, "none"), ((string)listed["verification"]!, (bool)listed["mine"]!, (bool)listed["mayUnlink"]!, (string)listed["direction"]!));
        var confirmed = await Ok(who.App, new { op = "link", id = fix, target = leak, type = "relates_to" });
        Assert.Equal((guess.Id.ToString(), false, false, "verified"), ((string)confirmed["link"]!, (bool)confirmed["created"]!, (bool)confirmed["unchanged"]!,
            (string)confirmed["verification"]!));

        // The MCP tools' rules hold for a person too: a held memory waits for its release, decided joins two episodic
        // memories only, a replacement is supersede's, and a memory nobody reads is unknown.
        var held = await Remember(who.Alex, Planted[0].Text, "shared");
        Assert.Equal("held", await Refused(who.App, new { op = "link", id = fix, target = held }));
        var plain = IdOf(await Ok(who.App, new { op = "capture", content = "DEMO: sheds keep tools dry.", scope = "shared", kind = "semantic" }));
        Assert.Equal("invalid", await Refused(who.App, new { op = "link", id = fix, target = plain, type = "decided" }));
        Assert.Equal("invalid", await Refused(who.App, new { op = "link", id = fix, target = leak, type = "supersedes" }));
        Assert.Equal("invalid", await Refused(who.App, new { op = "link", id = fix, target = fix }));
        Assert.Equal("unknown", await Refused(who.App, new { op = "link", id = fix, target = Guid.NewGuid() }));
        Assert.Equal("unknown", await Refused(who.App, new { op = "connections", id = Guid.NewGuid() }));

        // Unlink of its one type, from either end: removed; again, nothing to do.
        var unlinked = await Ok(who.App, new { op = "unlink", id = leak, target = fix, type = "caused_by" });
        Assert.Equal(["caused_by"], unlinked["removed"]!.AsArray().Select(r => (string)r!["type"]!));
        Assert.False((bool)unlinked["unchanged"]!);
        Assert.True((bool)(await Ok(who.App, new { op = "unlink", id = leak, target = fix, type = "caused_by" }))["unchanged"]!);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM made_links WHERE link_id=@id", ("id", link)));
        Assert.Single((await Ok(who.App, new { op = "connections", id = fix }))["connections"]!.AsArray(), c => (bool)c!["made"]!);
        // A link changes no memory: its history is its capture alone.
        Assert.Equal("created:app:-", await Trail(fix));
    }

    // Only a person's link takes back a dismissal of its type between the two memories (057): the app's and the
    // terminal's do; an AI client's is made beside it and leaves it.
    [PostgresFact] public async Task A_persons_link_takes_back_a_dismissal_and_an_AI_clients_never_does()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "link-dismissed");
        var a = await Remember(who.Alex, "DEMO: the Hollin hedge was cut back.", "shared");
        var b = await Remember(who.Alex, "DEMO: the Hollin hedge grows over the path.", "shared");
        var c = await Remember(who.Alex, "DEMO: the Hollin path is clear again.", "shared");
        var d = await Remember(who.Alex, "DEMO: the Hollin gate was oiled.", "shared");
        foreach (var (x, y) in new[] { (a, b), (a, c), (a, d) }) await Dismissed(x, y, "relates_to", who.Alex);

        Assert.Equal("inferred", (await Store.Link(who.Alex, a, b, "relates_to", null, Mcp, default)).Verification);
        Assert.Equal(1L, await Dismissals(a, b, "relates_to"));
        Assert.True((bool)(await Ok(who.App, new { op = "link", id = a, target = c }))["created"]!);
        Assert.Equal(0L, await Dismissals(a, c, "relates_to"));
        var (code, said) = await Link("--person", "alex", a.ToString(), d.ToString());
        Assert.Equal((0, true), (code, said.StartsWith($"Linked: {a} relates_to {d}, a person's link, confirmed.", StringComparison.Ordinal)));
        Assert.Equal(0L, await Dismissals(a, d, "relates_to"));
    }

    // A link an import brought (no maker here, marked imported, as a Second Brain export's or another install's comes in)
    // is said to be imported, in the app and at the terminal, and confirmed as its file gave it: never a person's, nor an
    // AI client's, since the file does not say which. A person's confirmation raises it to confirmed and leaves it
    // imported, with no maker (057).
    [PostgresFact] public async Task An_imported_link_is_said_to_be_imported_in_the_app_and_at_the_terminal()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "link-imported");
        var gate = await Remember(who.Alex, "DEMO: the Tansy gate sticks in the wet.", "shared", kind: "episodic");
        var hinge = await Remember(who.Alex, "DEMO: the Tansy gate got a new hinge.", "shared", kind: "episodic");
        await LinkUpkeep.Stated(s, gate, hinge, "follows", "inferred", 1, 0.4);
        await LinkUpkeep.Stated(s, hinge, gate, "caused_by", "verified", 1);

        var listed = (await Ok(who.App, new { op = "connections", id = gate }))["connections"]!.AsArray().Where(c => (bool)c!["made"]!)
            .ToDictionary(c => (string)c!["type"]!);
        Assert.Equal((true, "inferred", false), ((bool)listed["follows"]!["imported"]!, (string)listed["follows"]!["verification"]!, (bool)listed["follows"]!["mine"]!));
        Assert.Equal((true, "verified", false), ((bool)listed["caused_by"]!["imported"]!, (string)listed["caused_by"]!["verification"]!, (bool)listed["caused_by"]!["mine"]!));
        var (code, links) = await Link("--person", "alex", gate.ToString());
        Assert.Equal(0, code);
        Assert.Contains($"  follows ->  {hinge}  Family  imported, not yet confirmed  DEMO: the Tansy gate got a new hinge.", links);
        Assert.Contains($"  <- caused_by  {hinge}  Family  imported, confirmed  DEMO: the Tansy gate got a new hinge.", links);
        Assert.DoesNotContain("made by", links);

        // Alex confirms the drawn one: confirmed now, still the import's, with no maker here.
        await Link("--person", "alex", gate.ToString(), hinge.ToString(), "--type", "follows");
        var confirmed = (await Ok(who.App, new { op = "connections", id = gate, type = "follows" }))["connections"]!.AsArray().Single(c => (bool)c!["made"]!)!;
        Assert.Equal((true, "verified", false), ((bool)confirmed["imported"]!, (string)confirmed["verification"]!, (bool)confirmed["mine"]!));
        Assert.Contains($"  follows ->  {hinge}  Family  imported, confirmed  DEMO", (await Link("--person", "alex", gate.ToString(), "--type", "follows")).Item2);
    }

    // Still true and a flag clear through the app (SB-P4-09 and SB-P4-08's person halves), scopes off: the queue the app
    // reads holds a memory the pass found out of date and one a retracted source flagged, its count the length of its list;
    // still true at the revision read checks it without changing it and it leaves the queue; clear_flags takes a flag off
    // once checked; each change is the app's and writes one audit row; a memory marked wrong is not confirmed.
    [PostgresFact] public async Task A_person_says_a_memory_is_still_true_and_clears_its_flags_from_the_app()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "stale-app");
        var bus = await Remember(who.Alex, "DEMO: the Pellow bus leaves at 07:40 on weekdays.", "shared", volatility: "volatile");
        await Stale(bus);
        var (built, source) = await Flagged(who.Alex, "shared", "DEMO: the choir's Tuesday booking at the Pellow hall stands.", "DEMO: the choir practises on Tuesdays.");

        var page = await Ok(who.App, new { op = "stale", limit = 50 });
        var queue = page["stale"]!.AsArray().ToDictionary(i => IdOf(i!), i => i!);
        Assert.Equal((int)page["count"]!, queue.Count);
        Assert.Equal(("not_confirmed", 1, "volatile"), ((string)queue[bus]["reason"]!, (int)queue[bus]["revision"]!, (string)queue[bus]["volatility"]!));
        Assert.Equal(("retracted_source", source.ToString()), ((string)queue[built]["reason"]!, (string)queue[built]["retractedSources"]![0]!));
        Assert.False(queue.ContainsKey(source));

        var confirmed = await Ok(who.App, new { op = "still_true", id = bus, expected = 1 });
        Assert.Equal(("still_true", 1, false, "not_confirmed"), ((string)confirmed["op"]!, (int)confirmed["revision"]!, (bool)confirmed["unchanged"]!,
            (string)confirmed["was"]!));
        Assert.Equal("created:mcp:DEMO client,confirmed:app:-", await Trail(bus));
        Assert.Equal(1, (await Store.Get(who.Alex, bus, default))!.Revision);
        Assert.DoesNotContain((await Ok(who.App, new { op = "stale", limit = 50 }))["stale"]!.AsArray(), i => IdOf(i!) == bus);
        Assert.True((bool)(await Ok(who.App, new { op = "still_true", id = bus }))["unchanged"]!);
        Assert.Equal("conflict", await Refused(who.App, new { op = "still_true", id = built, expected = 5 }));

        var cleared = await Ok(who.App, new { op = "clear_flags", id = built, expected = 1 });
        Assert.Equal(("clear_flags", false), ((string)cleared["op"]!, (bool)cleared["unchanged"]!));
        Assert.Equal(["retracted_sources"], cleared["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Empty((await Store.Get(who.Alex, built, default))!.RetractedSources!);
        Assert.EndsWith(",flags_cleared:app:-", (string)(await Trail(built))!);
        Assert.True((bool)(await Ok(who.App, new { op = "clear_flags", id = built }))["unchanged"]!);
        Assert.DoesNotContain((await Ok(who.App, new { op = "stale", limit = 50 }))["stale"]!.AsArray(), i => IdOf(i!) == built);
        Assert.Equal("retracted", await Refused(who.App, new { op = "still_true", id = source }));
    }

    // With scopes on a person's link steps keep the author lock and one scope: Alex's viewer session (the app's channel
    // bound to him) links his own two memories; the household view reads neither and may not link from his Family memory;
    // two scopes never link; his queue holds only what he may change; at the terminal Sam reads none of Alex's own.
    [PostgresFact] public async Task With_scopes_on_a_persons_link_steps_keep_the_author_lock_and_one_scope()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "link-scoped");
            var viewer = await Scoped.Principal(s, "link-scoped-viewer", "app", "alex");
            var ferry = await Remember(who.Alex, "DEMO: Alex takes the Larrow ferry on Fridays.");
            var time = await Remember(who.Alex, "DEMO: the Larrow ferry leaves at nine.");
            var family = await Remember(who.Alex, "DEMO: the Larrow ferry rota is Alex's to keep.", "shared");
            var rota = await Remember(who.Alex, "DEMO: the Larrow rota is pinned in the hall.", "shared");
            var sams = await Remember(who.Sam, "DEMO: Sam's Larrow note.", volatility: "volatile");
            await Stale(sams);
            var own = await Remember(who.Alex, "DEMO: Alex's Larrow pass runs out in spring.", volatility: "state");
            await Stale(own, 100);

            Assert.Equal("verified", (string)(await Ok(viewer, new { op = "link", id = ferry, target = time }))["verification"]!);
            Assert.Equal("unknown", await Refused(who.App, new { op = "link", id = ferry, target = time }));
            Assert.Equal("unknown", await Refused(who.App, new { op = "connections", id = ferry }));
            Assert.Equal("invalid", await Refused(viewer, new { op = "link", id = ferry, target = family }));
            Assert.Equal("forbidden", await Refused(who.App, new { op = "link", id = family, target = rota }));
            Assert.True((bool)(await Ok(viewer, new { op = "link", id = family, target = rota }))["created"]!);
            Assert.Equal("forbidden", await Refused(who.App, new { op = "unlink", id = family, target = rota }));

            var alex = (await Ok(viewer, new { op = "stale", limit = 50 }))["stale"]!.AsArray().Select(i => IdOf(i!)).ToArray();
            Assert.Contains(own, alex);
            Assert.DoesNotContain(sams, alex);
            Assert.Equal("unknown", await Refused(viewer, new { op = "still_true", id = sams }));
            Assert.DoesNotContain(own, (await Ok(who.App, new { op = "stale", limit = 50 }))["stale"]!.AsArray().Select(i => IdOf(i!)));

            var (code, said) = await Link("--person", "sam", ferry.ToString());
            Assert.Equal((1, true), (code, said.StartsWith("No memory with that id that sam reads", StringComparison.Ordinal)));
            Assert.StartsWith("No memory with that id that sam reads", (await Link("--person", "sam", ferry.ToString(), time.ToString())).Item2);
            Assert.StartsWith("Nothing linked: Two memories in different scopes never link", (await Link("--person", "alex", ferry.ToString(), family.ToString())).Item2);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A person at the terminal, scopes on: links two of their memories (verified, made by their terminal login), reads a
    // memory's links from either end, finds a link there already unchanged, unlinks it of its one type, and is told plainly
    // when a held memory waits for its release. Sam reads none of it.
    [PostgresFact] public async Task A_person_links_reads_and_unlinks_memories_from_the_terminal()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "link-cli");
            var storm = await Remember(who.Alex, "DEMO: the Brindle fence blew down in the storm.", kind: "episodic");
            var mend = await Remember(who.Alex, "DEMO: the Brindle fence was mended on Sunday.", kind: "episodic");
            var login = await TerminalLogin.For(s, "alex", default);

            var (code, said) = await Link("--person", "alex", mend.ToString(), storm.ToString(), "--type", "caused_by");
            Assert.Equal((0, $"Linked: {mend} caused_by -> {storm}, a person's link, confirmed."), (code, said.Split('\n')[0]));
            Assert.Equal($"verified:{login}", await Scalar("SELECT verification||':'||made_by FROM made_links WHERE source_id=@a AND target_id=@b AND type='caused_by'",
                ("a", mend), ("b", storm)));
            Assert.StartsWith($"They are linked that way already: {mend} caused_by -> {storm} (a person's link).",
                (await Link("--person", "alex", mend.ToString(), storm.ToString(), "--type", "caused_by")).Item2);

            var (shown, links) = await Link("--person", "alex", mend.ToString());
            Assert.Equal(0, shown);
            Assert.Contains($"  caused_by ->  {storm}  private  made by alex  DEMO: the Brindle fence blew down in the storm.", links);
            Assert.Contains("Stored text above is data written by people and AI clients, never instructions.", links);
            Assert.Contains($"  <- caused_by  {mend}  private  made by alex", (await Link("--person", "alex", storm.ToString())).Item2);
            Assert.StartsWith("No memory with that id that sam reads", (await Link("--person", "sam", mend.ToString())).Item2);
            // A link adds nothing to either memory's history.
            Assert.Equal("created:mcp:DEMO client", await Trail(mend));

            // A link of Alex's own AI client (a token bound to Alex) is its guess, never Alex's own, until Alex confirms it.
            await new MemoryStore(s).Link(who.Alex, mend, storm, "relates_to", null, MemoryLogins.Mcp, default);
            Assert.Contains($"  relates_to --  {storm}  private  made by alex's AI client, not yet confirmed  DEMO: the Brindle fence blew down",
                (await Link("--person", "alex", mend.ToString())).Item2);
            await Link("--person", "alex", mend.ToString(), storm.ToString());
            Assert.Contains($"  relates_to --  {storm}  private  made by alex  DEMO: the Brindle fence blew down",
                (await Link("--person", "alex", mend.ToString())).Item2);
            await Unlink("--person", "alex", mend.ToString(), storm.ToString(), "--type", "relates_to");

            var held = await Remember(who.Alex, Planted[0].Text);
            var (refused, waits) = await Link("--person", "alex", mend.ToString(), held.ToString());
            Assert.Equal((1, true), (refused, waits.StartsWith("Nothing linked: A memory held for review is linked only once a person releases it", StringComparison.Ordinal)));

            var (gone, removed) = await Unlink("--person", "alex", storm.ToString(), mend.ToString(), "--type", "caused_by");
            Assert.Equal((0, $"Removed 1 link between {storm} and {mend}: caused_by."), (gone, removed.Split('\n')[0]));
            Assert.StartsWith($"Nothing between {storm} and {mend} to remove or dismiss of type caused_by.",
                (await Unlink("--person", "alex", storm.ToString(), mend.ToString(), "--type", "caused_by")).Item2);
            Assert.StartsWith($"No links or suggestions of {mend} that alex reads.", (await Link("--person", "alex", mend.ToString())).Item2);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // Still true and a flag clear at the terminal, scopes on: the queue Alex may act on, its length, never Sam's; still
    // true at the revision read, through Alex's terminal login, writes one audit row and nothing else; a stale revision
    // and a memory marked wrong are refused in words; a flag comes off once checked.
    [PostgresFact] public async Task A_person_says_a_memory_is_still_true_and_clears_its_flags_from_the_terminal()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "stale-cli");
            var pass = await Remember(who.Alex, "DEMO: Alex's Ottery locker pass is valid.", volatility: "state");
            await Stale(pass, 100);
            var (built, source) = await Flagged(who.Alex, "private", "DEMO: Alex's Ottery locker plan holds.", "DEMO: the Ottery locker opens at six.");
            var sams = await Remember(who.Sam, "DEMO: Sam's Ottery note.", volatility: "volatile");
            await Stale(sams);
            var login = await TerminalLogin.For(s, "alex", default);

            var (code, queue) = await StillTrue("--person", "alex");
            Assert.Equal(0, code);
            Assert.Contains($"{pass}  private  revision 1  unchanged since", queue);
            Assert.Contains($"{built}  private  revision 1  built on a memory marked wrong or moved to the trash since  DEMO: Alex's Ottery locker plan holds.", queue);
            Assert.Contains("may be out of date in all.", queue);
            Assert.DoesNotContain(sams.ToString(), queue);
            Assert.Contains(sams.ToString(), (await StillTrue("--person", "sam")).Item2);
            Assert.DoesNotContain(pass.ToString(), (await StillTrue("--person", "sam")).Item2);

            var (done, said) = await StillTrue("--person", "alex", pass.ToString(), "--revision", "1");
            Assert.Equal((0, $"Still true: {pass} (revision 1) is checked as of now; nothing in it changed."), (done, said.Split('\n')[0]));
            Assert.Equal($"confirmed:cli:rhythm:{login}", await Newest(pass));
            Assert.Equal(1, (await Store.Get(who.Alex, pass, default))!.Revision);
            Assert.StartsWith("It was checked at this revision today", (await StillTrue("--person", "alex", pass.ToString())).Item2);
            Assert.StartsWith("Nothing confirmed: it changed since revision 3", (await StillTrue("--person", "alex", built.ToString(), "--revision", "3")).Item2);
            Assert.StartsWith("No memory with that id that sam reads", (await StillTrue("--person", "sam", pass.ToString())).Item2);
            Assert.StartsWith("Nothing confirmed: A memory marked wrong (deprecated) is not confirmed as true",
                (await StillTrue("--person", "alex", source.ToString())).Item2);

            var (cleared, off) = await ClearFlags("--person", "alex", built.ToString(), "--revision", "1");
            Assert.Equal((0, $"Its flags for a check are off: {built} (revision 1) stands as alex checked it; nothing else in it changed."), (cleared, off.Split('\n')[0]));
            Assert.Equal($"flags_cleared:cli:rhythm:{login}", await Newest(built));
            Assert.Empty((await Store.Get(who.Alex, built, default))!.RetractedSources!);
            Assert.StartsWith("It had no flags for a check, so nothing changed", (await ClearFlags("--person", "alex", built.ToString())).Item2);
            Assert.DoesNotContain(built.ToString(), (await StillTrue("--person", "alex")).Item2);
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // No AI client's token reaches a person's link step, whatever channel it names: the terminal's or the app's is refused
    // by the database (044's memory_channel) for a person-bound token too, before anything is written, and the app's
    // handler given an AI client's login writes nothing either. The same steps through the person's own terminal login
    // are taken, as that login's own: a person's link verified where the AI client's is inferred.
    [PostgresFact] public async Task No_AI_clients_token_reaches_a_persons_link_step()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "link-ai");
        var a = await Remember(who.Alex, "DEMO: the Quarry Lane bus stop moved.", "shared");
        var b = await Remember(who.Alex, "DEMO: the Quarry Lane footpath is closed.", "shared");
        var c = await Remember(who.Alex, "DEMO: the Quarry Lane bins go out on Monday.", "shared");
        await Store.Link(who.Alex, a, c, "caused_by", null, Mcp, default);
        var bus = await Remember(who.Alex, "DEMO: the Quarry Lane bus runs every twenty minutes.", "shared", volatility: "volatile");
        await Stale(bus);
        var (flagged, _) = await Flagged(who.Alex, "shared", "DEMO: the Quarry Lane plan holds.", "DEMO: the Quarry Lane works end in May.");
        var events = await Scalar("SELECT count(*) FROM memory_events");
        var links = await Scalar("SELECT count(*) FROM made_links");

        foreach (var ai in new[] { who.Alex, who.House })
        {
            foreach (var call in new[] { new MemoryCall("cli", "DEMO not a terminal"), new MemoryCall("app") })
            {
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(ai, a, b, "relates_to", null, call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Unlink(ai, a, c, null, call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.StillTrue(ai, bus, null, call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.ClearFlags(ai, flagged, null, call, default)));
            }
            // The app's handler, if an AI client's login ever reached it, writes as the app, which the database refuses it.
            foreach (var step in new object[] { new { op = "link", id = a, target = b }, new { op = "unlink", id = a, target = c },
                         new { op = "still_true", id = bus }, new { op = "clear_flags", id = flagged } })
                Assert.Equal("invalid", await Refused(ai, step));
        }
        Assert.Equal(events, await Scalar("SELECT count(*) FROM memory_events"));
        Assert.Equal(links, await Scalar("SELECT count(*) FROM made_links"));
        Assert.NotEmpty((await Store.Get(who.Alex, flagged, default))!.RetractedSources!);

        // Through the person's own terminal login: taken, as its own, and a person's link is verified, an AI client's inferred.
        var at = await TerminalLogin.For(s, "alex", default);
        Assert.NotEqual(who.Alex, at);
        var person = await Store.Link(at, a, b, "relates_to", null, Cli, default);
        Assert.Equal(("verified", true), (person.Verification, person.Created));
        Assert.Equal("inferred", (await Store.Link(who.Alex, b, c, "relates_to", null, Mcp, default)).Verification);
        Assert.False((await Store.StillTrue(at, bus, null, Cli, default)).Unchanged);
        Assert.Equal($"confirmed:cli:rhythm:{at}", await Newest(bus));
        Assert.Equal(["retracted_sources"], (await Store.ClearFlags(at, flagged, null, Cli, default)).Changed);
    }
}

// The terminal's dismissal steps (run 17; SB-P4-05's terminal half): a person sees the suggestions dismissed on their
// map, whoever dismissed them, and takes one back through their own terminal login, which 057 lets alone among the
// terminal's logins; an AI client's channel never takes one back. Over a map built on this database, as the app's tests
// build one (MapDismissalTests), and once through the map the terminal reads from rhythm.yaml.
[Collection(SharedStateCollection.Name)]
public sealed class PersonDismissalTerminalTests(DatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    async Task Document(string subject, string external, string text) => await MemoryRows.Run(fixture.Source, """
        INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,revision,source_updated_at)
        VALUES(gen_random_uuid(),@s,'documents',@e,'document',CASE WHEN @s='household' THEN 'shared' ELSE 'private' END,@t,@t,'current','1',
          now()-interval '1 hour')
        """, ("s", subject), ("e", external), ("t", text));
    static string DocumentStar(string subject, string external) =>
        new ResourceKey(subject, CatalogStars.Provider, "documents", $"{subject}:{external}").GraphId;

    // Stars alike in meaning by one word they share; any other star points elsewhere.
    static float[]? Vector(string title, string text) => (title + " " + text).Contains("Fetewick", StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f];

    async Task<(int Code, string Out)> Undismiss(RelationshipHost? map, RhythmConfig config, params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var code = await MemoryCommands.Undismiss(args, o, fixture.Source, config, map);
        output.WriteLine(o.ToString());
        return (code, o.ToString());
    }

    [PostgresFact] public async Task A_person_sees_and_takes_back_a_dismissal_from_the_terminal()
    {
        var s = fixture.Source;
        TestSettings.Use();
        var config = MemoryToolSettings.On;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "dismiss-cli");
        await Document("household", "fetewick-gate.txt", "DEMO: the Fetewick gate sticks after the rain.");
        await Document("household", "fetewick-hinge.txt", "DEMO: the Fetewick gate needs a new hinge.");
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(config));
        var graph = await new Principals(s).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { SimilarityThreshold = 0.5 },
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct), Vector, "demo-vectors"),
            registry);
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            var edge = Assert.Single((await host.Read(who.App, null, null, default, new GraphQuery())).edges,
                e => new[] { e.source, e.target }.Order().SequenceEqual(new[] { DocumentStar("household", "fetewick-gate.txt"), DocumentStar("household", "fetewick-hinge.txt") }.Order()));
            Assert.StartsWith("No suggestion is dismissed on alex's map.", (await Undismiss(host, config, "--person", "alex")).Out);

            // Dismissed in the app's Discover, listed at the terminal, as the map shows it now (the map the terminal reads
            // from rhythm.yaml too, which the engine saved).
            var id = (await MapDismissals.Dismiss(host, Store, who.App, edge.id, new MemoryCall(AppMemories.Channel), default)).Dismissal!;
            foreach (var map in new[] { host, null })
            {
                var (code, listed) = await Undismiss(map, config, "--person", "alex");
                Assert.Equal(0, code);
                Assert.Contains($"  {id}  dismissed by a person", listed);
                Assert.Contains("\"DEMO: the Fetewick gate", listed);
                Assert.Contains("Take one back, so the map may suggest it again: ./rhythm undismiss --person alex <dismissal>", listed);
            }
            // An AI client's channel never takes it back, nor names the terminal's.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => MapDismissals.Restore(host, Store, who.Alex, id, MemoryLogins.Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => MapDismissals.Restore(host, Store, who.Alex, id, new MemoryCall("cli", "DEMO"), default)));

            var (done, back) = await Undismiss(host, config, "--person", "alex", id);
            Assert.Equal((0, "Taken back: the map suggests it again from its next read."), (done, back.Trim()));
            Assert.Single((await host.Read(who.App, null, null, default, new GraphQuery())).edges, e => e.id == edge.id);
            Assert.Equal("It was not dismissed, so nothing changed.", (await Undismiss(host, config, "--person", "alex", id)).Out.Trim());
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM map_link_dismissals"));
        }
        finally
        {
            await Scoped.Execute(s, "DELETE FROM map_link_dismissals; DELETE FROM memory_link_dismissals");
            try { Directory.Delete(state, true); } catch (IOException) { }
            TestSettings.Use();
        }
    }
}
