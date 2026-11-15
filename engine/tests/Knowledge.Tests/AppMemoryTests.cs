using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// app_memories (SB-P3-12, Mcp/AppMemories.cs): the app's own reads and writes of native memories for its Memories view,
// behind memory.tools as the MCP memory tools are; with it off the app's role answers exactly as before. Every op reads
// and writes as the app (channel app), a person's channel, whatever the call says. Fictional people only (alex, sam, the
// household); every text is DEMO.
static class AppMemoryCalls
{
    public static IServiceProvider Services(RhythmConfig config, Database? db = null)
    {
        var services = new ServiceCollection().AddSingleton(config);
        if (db is not null) services.AddSingleton(db);
        return services.BuildServiceProvider();
    }

    public static string Body(string tool, object arguments) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "m", method = "tools/call", @params = new { name = tool, arguments } });

    // One request through the app's endpoint as the login `app`, its answer's status and structured content; a 200's
    // content checked against the contract's schema.
    public static async Task<(int Status, JsonNode? Content)> Call(IServiceProvider services, Guid app, string body, AppHealth? health = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, health ?? new AppHealth(null!, TestSettings.Use()), AdapterRegistry.Empty);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        var answer = bytes.Length == 0 ? null : JsonNode.Parse(bytes);
        var content = answer?["result"]?["structuredContent"];
        if (context.Response.StatusCode == 200 && content is not null)
            Schemas.Valid(Schemas.Folder("mcp"), "app_memories.output.schema.json", content);
        return (context.Response.StatusCode, content);
    }

    // One request through the app's endpoint as the login `app`, any tool: its status and structured content, unchecked.
    public static async Task<(int Status, JsonNode? Content)> CallTool(IServiceProvider services, Guid app, string body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, new AppHealth(null!, TestSettings.Use()), AdapterRegistry.Empty);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        return (context.Response.StatusCode, bytes.Length == 0 ? null : JsonNode.Parse(bytes)?["result"]?["structuredContent"]);
    }

    public static JsonElement Request(object arguments) =>
        JsonDocument.Parse(Body(AppEndpoint.Memories, arguments)).RootElement.Clone();
}

public sealed class AppMemoryTests
{
    static readonly Guid Id = Guid.Parse("6d3c2a10-0000-4000-8000-00000000a001");

    [Fact] public async Task With_memory_tools_off_the_app_has_no_memories_tool()
    {
        foreach (var config in new[] { TestSettings.Use(), MemoryToolSettings.With("tools: off"), MemoryToolSettings.With("") })
        {
            var services = AppMemoryCalls.Services(config);
            // Refused as a tool this role does not have, before anything is read (there is no database here to read).
            var (status, content) = await AppMemoryCalls.Call(services, Guid.NewGuid(), AppMemoryCalls.Body(AppEndpoint.Memories, new { op = "list" }));
            Assert.Equal(403, status);
            Assert.Equal("forbidden", (string)content!["error"]!["code"]!);
            Assert.False(AppEndpoint.Allowed(AppMemoryCalls.Request(new { op = "list" })));
        }
        TestSettings.Use();
    }

    [Fact] public void Each_op_takes_exactly_its_arguments()
    {
        bool Takes(object arguments) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true);
        Assert.True(Takes(new { op = "list" }));
        Assert.True(Takes(new { op = "list", scope = "shared", tag = "rota", kind = "semantic", status = "draft", project = "brindle", cursor = "abc", limit = 50 }));
        Assert.False(Takes(new { op = "list", limit = 51 }));
        Assert.False(Takes(new { op = "list", scope = "company" }));
        Assert.True(Takes(new { op = "trash", limit = 10 }));
        Assert.False(Takes(new { op = "trash", tag = "rota" }));
        Assert.True(Takes(new { op = "get", id = Id }));
        Assert.True(Takes(new { op = "get", id = Id, version = 2 }));
        Assert.False(Takes(new { op = "get" }));
        Assert.False(Takes(new { op = "get", id = "not-a-uuid" }));
        Assert.True(Takes(new { op = "history", id = Id, before = 4, limit = 100 }));
        Assert.False(Takes(new { op = "history", id = Id, limit = 101 }));
        // The listing's bound is one every op keeps to: a call within it is never refused for its limit.
        var listed = System.Text.Json.JsonSerializer.SerializeToNode(AppMemories.Listing())!["inputSchema"]!["properties"]!["limit"]!;
        Assert.Equal((1, MemoryStore.TrashMax), ((int)listed["minimum"]!, (int)listed["maximum"]!));
        foreach (var op in new[] { "list", "trash" }) Assert.True(Takes(new { op, limit = (int)listed["maximum"]! }));
        Assert.True(Takes(new { op = "history", id = Id, limit = (int)listed["maximum"]! }));
        Assert.True(Takes(new { op = "projects", scope = "private", archived = true }));
        Assert.True(Takes(new { op = "capture", content = "DEMO: the bins go out on Thursday.", scope = "shared", tags = new[] { "bins" },
            project = "house", kind = "semantic", importance = 3, volatility = "state", key = "capture-key-0001" }));
        Assert.False(Takes(new { op = "capture" }));
        Assert.False(Takes(new { op = "capture", content = "" }));
        Assert.False(Takes(new { op = "capture", content = "DEMO", key = "short" }));
        Assert.False(Takes(new { op = "capture", content = "DEMO", importance = 6 }));
        Assert.True(Takes(new { op = "append", id = Id, text = "DEMO: and Fridays.", expected = 1, key = "append-key-0001" }));
        Assert.True(Takes(new { op = "update", id = Id, content = "DEMO: Wednesdays.", expected = 2 }));
        Assert.True(Takes(new { op = "update", id = Id, clear = new[] { "project", "kind" } }));
        Assert.False(Takes(new { op = "update", id = Id }));
        Assert.False(Takes(new { op = "update", id = Id, clear = new[] { "content" } }));
        Assert.True(Takes(new { op = "status", id = Id, status = "canonical", expected = 3 }));
        Assert.False(Takes(new { op = "status", id = Id, status = "held" }));
        Assert.True(Takes(new { op = "release", id = Id, expected = 3 }));
        Assert.False(Takes(new { op = "release", id = Id, key = "release-key-0001" }));
        Assert.True(Takes(new { op = "forget", id = Id }));
        Assert.True(Takes(new { op = "restore", id = Id, nonce = Id }));
        Assert.True(Takes(new { op = "undo", id = Id, to = 1, expected = 4 }));
        Assert.False(Takes(new { op = "undo", id = Id, group = "abc" }));
        Assert.True(Takes(new { op = "delete", id = Id, nonce = Id, confirm = Id }));
        Assert.False(Takes(new { op = "delete", id = Id, nonce = Id }));
        Assert.False(Takes(new { op = "dance" }));
        Assert.False(Takes(new { id = Id }));
        // No op names its channel or its client: the handler fixes both (memory.md section 5).
        foreach (var op in new[] { "list", "get", "capture", "release", "delete" })
        {
            Assert.False(Takes(new { op, id = Id, channel = "cli" }), op);
            Assert.False(Takes(new { op, id = Id, client = "DEMO terminal" }), op);
        }
        // A member twice is refused, as every app tool refuses it.
        Assert.False(AppEndpoint.Allowed(JsonDocument.Parse(
            """{"jsonrpc":"2.0","id":"m","method":"tools/call","params":{"name":"app_memories","arguments":{"op":"list","op":"trash"}}}""").RootElement, memories: true));
    }

    [Fact] public async Task A_call_may_carry_a_memorys_whole_text_only_to_the_memories_tool()
    {
        var on = MemoryToolSettings.On;
        var services = AppMemoryCalls.Services(on);
        var app = Guid.NewGuid();
        // A capture of 100 000 characters passes the size check (with no database it then fails to reach the store).
        var (whole, _) = await AppMemoryCalls.Call(services, app, AppMemoryCalls.Body(AppEndpoint.Memories, new { op = "capture", content = "DEMO " + new string('x', 100_000) }));
        Assert.Equal(503, whole);
        // Larger than the tool's own limit, and any other tool's call over the app's 32 KiB, are refused before anything is read.
        var (over, _) = await AppMemoryCalls.Call(services, app, AppMemoryCalls.Body(AppEndpoint.Memories, new { op = "capture", content = new string('x', AppMemories.MaxBody) }));
        Assert.Equal(413, over);
        var (graph, _) = await AppMemoryCalls.Call(services, app, AppMemoryCalls.Body(AppEndpoint.Graph, new { operation = "search", value = new string('x', 900), knownNodeIds = Enumerable.Repeat(new string('y', 100), 400) }));
        Assert.Equal(413, graph);
        // With memory.tools off the app's limit is as before.
        var (off, _) = await AppMemoryCalls.Call(AppMemoryCalls.Services(TestSettings.Use()), app, AppMemoryCalls.Body(AppEndpoint.Memories, new { op = "capture", content = new string('x', 40_000) }));
        Assert.Equal(413, off);
        TestSettings.Use();
    }
}

public sealed class AppMemoryDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly IReadOnlyList<ParityMemory> Planted = [.. ParityCorpus.Load().Memories.Where(m => m.Expect?.Held == true)];
    MemoryStore Store => new(fixture.Source);

    async Task<(int Status, JsonNode? Content)> App(Guid app, object arguments, RhythmConfig? config = null)
    {
        var settings = config ?? MemoryToolSettings.On;
        return await AppMemoryCalls.Call(AppMemoryCalls.Services(settings, fixture.Db), app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments), new AppHealth(fixture.Db, settings));
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

    static Guid Id(JsonNode answer) => Guid.Parse((string)answer["id"]!);

    Task<object?> Trail(Guid id) => MemoryRows.Scalar(fixture.Source,
        "SELECT string_agg(event||':'||channel||':'||coalesce(client,'-'),',' ORDER BY id) FROM memory_events WHERE memory_id=@id", ("id", id));

    [PostgresFact] public async Task The_app_lists_its_memories_tool_only_with_memory_tools_on()
    {
        var who = await MemoryLogins.Make(fixture.Source, "app-listing");
        var config = TestSettings.Use();
        string[] Names(object[] tools) => [.. JsonSerializer.SerializeToNode(tools)!.AsArray().Select(t => (string)t!["name"]!)];
        var off = Names(await AppEndpoint.Tools(new AppHealth(fixture.Db, config), who.App, default));
        var on = Names(await AppEndpoint.Tools(new AppHealth(fixture.Db, config), who.App, default, memories: true));
        Assert.DoesNotContain(AppEndpoint.Memories, off);
        // On, the list is the same tools in the same order with the memories tool added; where it sits among them is the
        // endpoint's (Studio's tool comes after it).
        Assert.Contains(AppEndpoint.Memories, on);
        Assert.Equal(off, on.Where(name => name != AppEndpoint.Memories));
        // Through the endpoint: tools/list with memory.tools off is the list as before, byte for byte; on, it adds the tool.
        async Task<string> Listed(RhythmConfig settings)
        {
            var context = new DefaultHttpContext { RequestServices = AppMemoryCalls.Services(settings, fixture.Db) };
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream("""{"jsonrpc":"2.0","id":"l","method":"tools/list"}"""u8.ToArray());
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(who.App, "app", "app", null), null!, new AppHealth(fixture.Db, settings), AdapterRegistry.Empty);
            return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        }
        var before = await Listed(TestSettings.Use());
        Assert.Equal(before, await Listed(MemoryToolSettings.With("tools: off")));
        var listed = JsonNode.Parse(await Listed(MemoryToolSettings.On))!["result"]!["tools"]!.AsArray();
        var tool = listed.Single(t => (string)t!["name"]! == AppEndpoint.Memories)!;
        Assert.Equal([.. AppMemories.Reads, .. AppMemories.Writes], tool["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray().Select(o => (string)o!));
        Assert.Equal(JsonNode.Parse(before)!["result"]!["tools"]!.AsArray().Count + 1, listed.Count);
    }

    // Story 3 through the app, scopes off: capture, browse, read, edit, append, confirm, history, undo to a version and
    // back, forget, the trash, restore, and delete for good with the trash nonce and the person's confirmation; every
    // change recorded as the app's, and each answer in the contract's shape.
    [PostgresFact] public async Task A_person_completes_a_memory_lifecycle_through_the_app()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var app = (await MemoryLogins.Make(s, "app-life")).App;

        var keyed = new { op = "capture", content = "DEMO: the Wendle allotment gate code changes on Mondays. #wendle", scope = "shared", project = "wendle-plot",
            kind = "semantic", importance = 3, key = "app-life-capture-01" };
        var made = await Ok(app, keyed);
        var id = Id(made);
        Assert.Equal(("capture", "draft", "shared", "household", "off", 1), ((string)made["op"]!, (string)made["status"]!, (string)made["scope"]!,
            (string)made["owner"]!, (string)made["scopes"]!, (int)made["revision"]!));
        Assert.Equal(("wendle-plot", true), ((string)made["project"]!["slug"]!, (bool)made["project"]!["created"]!));
        Assert.Null(made["held"]);
        // A retry with the same key stores once.
        var again = await Ok(app, keyed);
        Assert.Equal((id, true), (Id(again), (bool)again["replayed"]!));

        var listed = await Ok(app, new { op = "list", tag = "wendle", project = "wendle-plot" });
        var row = Assert.Single(listed["memories"]!.AsArray())!;
        Assert.Equal((id, false), (Id(row), (bool)row["truncated"]!));
        var unknown = await Refused(app, new { op = "list", project = "no-such-plot" });
        Assert.Equal("unknown_project", (string)unknown["code"]!);
        Assert.Contains("wendle-plot", unknown["known"]!.AsArray().Select(k => (string)k!));
        Assert.Contains("wendle-plot", (await Ok(app, new { op = "projects" }))["projects"]!.AsArray().Select(p => (string)p!["slug"]!));

        var got = (await Ok(app, new { op = "get", id }))["memory"]!;
        Assert.Equal(("DEMO: the Wendle allotment gate code changes on Mondays. #wendle", "app", null), ((string)got["content"]!, (string)got["source"]!, (string?)got["author"]));

        var edited = await Ok(app, new { op = "update", id, content = "DEMO: the Wendle allotment gate code changes on Tuesdays now.", expected = 1 });
        Assert.Equal((2, "update"), ((int)edited["revision"]!, (string)edited["op"]!));
        var stale = await Refused(app, new { op = "append", id, text = "DEMO: late.", expected = 1 });
        Assert.Equal(("conflict", 2), ((string)stale["code"]!, (int)stale["revision"]!));
        Assert.Equal(3, (int)(await Ok(app, new { op = "append", id, text = "DEMO: Sam has the new code.", expected = 2 }))["revision"]!);
        var confirmed = await Ok(app, new { op = "status", id, status = "canonical", expected = 3 });
        Assert.Equal(("canonical", 4), ((string)confirmed["status"]!, (int)confirmed["revision"]!));

        var history = await Ok(app, new { op = "history", id });
        Assert.Equal(["status_changed", "appended", "updated", "created"], history["changes"]!.AsArray().Select(c => (string)c!["event"]!));
        Assert.All(history["changes"]!.AsArray(), c => Assert.Equal(("app", null, null), ((string)c!["channel"]!, (string?)c["client"], (string?)c["person"])));
        var first = (await Ok(app, new { op = "get", id, version = 1 }))["version"]!;
        Assert.Equal(("DEMO: the Wendle allotment gate code changes on Mondays. #wendle", false), ((string)first["content"]!, (bool)first["current"]!));
        Assert.Equal("no_version", (string)(await Refused(app, new { op = "get", id, version = 99 }))["code"]!);

        var back = await Ok(app, new { op = "undo", id, to = 1, expected = 4 });
        Assert.Equal(("undo", 1, 5, "draft"), ((string)back["op"]!, (int)back["to"]!, (int)back["revision"]!, (string)back["status"]!));
        var redo = await Ok(app, new { op = "undo", id, expected = 5 });
        Assert.Equal((4, 6, "canonical"), ((int)redo["to"]!, (int)redo["revision"]!, (string)redo["status"]!));

        var forgot = await Ok(app, new { op = "forget", id, expected = 6 });
        Assert.Equal(("forget", 14), ((string)forgot["op"]!, (int)forgot["trash"]!["days"]!));
        Assert.Equal("unknown", (string)(await Refused(app, new { op = "get", id }))["code"]!);
        var trash = await Ok(app, new { op = "trash", limit = 50 });
        var trashed = trash["trash"]!.AsArray().Single(t => Id(t!) == id)!;
        // A person's channel: the app may delete it for good, with its nonce.
        Assert.Equal((true, (string)forgot["trash"]!["nonce"]!, "app"), ((bool)trashed["mayDeleteForever"]!, (string)trashed["nonce"]!, (string)trashed["channel"]!));
        var restored = await Ok(app, new { op = "restore", id, nonce = (string)forgot["trash"]!["nonce"]! });
        Assert.Equal(("restore", 6, false), ((string)restored["op"]!, (int)restored["revision"]!, (bool)restored["replayed"]!));
        // A retry with the same nonce restores nothing: that trip is over.
        Assert.Equal("unknown", (string)(await Refused(app, new { op = "restore", id, nonce = (string)forgot["trash"]!["nonce"]! }))["code"]!);
        Assert.Equal("canonical", (string)(await Ok(app, new { op = "get", id }))["memory"]!["status"]!);

        var second = await Ok(app, new { op = "forget", id });
        var nonce = (string)second["trash"]!["nonce"]!;
        // Delete for good takes the trash row's nonce and the person's confirmation, the memory's id; anything else deletes nothing.
        Assert.Equal("invalid", (string)(await Refused(app, new { op = "delete", id, nonce, confirm = Guid.NewGuid() }))["code"]!);
        Assert.Equal("unknown", (string)(await Refused(app, new { op = "delete", id, nonce = Guid.NewGuid(), confirm = id }))["code"]!);
        var deleted = await Ok(app, new { op = "delete", id, nonce, confirm = id });
        Assert.Equal(("delete", "household"), ((string)deleted["op"]!, (string)deleted["owner"]!));
        Assert.Equal("unknown", (string)(await Refused(app, new { op = "restore", id }))["code"]!);
        Assert.DoesNotContain(id, (await Ok(app, new { op = "trash", limit = 50 }))["trash"]!.AsArray().Select(t => Id(t!)));
        Assert.Equal(0L, (long)(await MemoryRows.Scalar(s, "SELECT count(*) FROM memories WHERE id=@id", ("id", id)))!);
        var trail = (string)(await Trail(id))!;
        Assert.StartsWith("created:app:-,updated:app:-,appended:app:-,status_changed:app:-,reverted:app:-,reverted:app:-,trashed:app:-,restored:app:-,trashed:app:-", trail);
        Assert.EndsWith("purged:app:-", trail);
    }

    // A memory an AI client wrote that reads like instructions is held (036). The app serves it whole with its hold, for a
    // person to review, and a person releases it there; an AI client cannot (D-039). A list serves it without its words.
    [PostgresFact] public async Task A_person_reviews_and_releases_a_hold_in_the_app_and_no_AI_client_can()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "app-held");
        var planted = Planted[0].Text;
        var written = await Store.Create(who.Alex, new(planted, ["wendle-held"]), MemoryLogins.Mcp, default);
        var id = written.Id;
        Assert.Equal(MemoryHolds.Instruction, written.Hold!.Reason);

        var got = (await Ok(who.App, new { op = "get", id }))["memory"]!;
        Assert.Equal((planted, MemoryHolds.Instruction), ((string)got["content"]!, (string)got["held"]!["reason"]!));
        Assert.Equal(["wendle-held"], got["tags"]!.AsArray().Select(t => (string)t!));
        var listed = (await Ok(who.App, new { op = "list", limit = 50 }))["memories"]!.AsArray().Single(m => Id(m!) == id)!;
        Assert.Equal((null, MemoryHolds.Instruction), ((string?)listed["content"], (string)listed["held"]!["reason"]!));
        Assert.Empty(listed["tags"]!.AsArray());

        // An AI client's channel never releases it, whoever its login speaks for.
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Release(who.Alex, id, null, MemoryLogins.Mcp, default)));
        var stale = await Refused(who.App, new { op = "release", id, expected = 9 });
        Assert.Equal("conflict", (string)stale["code"]!);
        var released = await Ok(who.App, new { op = "release", id, expected = 1 });
        Assert.Equal(("release", 2), ((string)released["op"]!, (int)released["revision"]!));
        Assert.Null(released["held"]);
        Assert.True((bool)(await Ok(who.App, new { op = "release", id }))["unchanged"]!);
        Assert.Null((await Ok(who.App, new { op = "get", id }))["memory"]!["held"]);
        var history = (await Ok(who.App, new { op = "history", id }))["changes"]!.AsArray();
        Assert.Equal(("released", "app"), ((string)history[0]!["event"]!, (string)history[0]!["channel"]!));
        // The held state is kept as a version; the release vouches for the same text, so history and a version read of it
        // now give the words a person released (034).
        Assert.False((bool)history[1]!["held"]!);
        Assert.NotNull(history[1]!["preview"]);
        Assert.Equal(planted, (string)(await Ok(who.App, new { op = "get", id, version = 1 }))["version"]!["content"]!);
        // A person may undo the release in the app, holding it again; so may the undo of that undo release it again.
        var undone = await Ok(who.App, new { op = "undo", id, expected = 2 });
        Assert.Equal(MemoryHolds.Instruction, (string)undone["held"]!["reason"]!);
        Assert.Null((await Ok(who.App, new { op = "undo", id, expected = 3 }))["held"]);
    }

    // Whatever a call carries beside its arguments (a channel or client named in a header, an AI client's name), every
    // change is recorded as the app's: the handler fixes the channel, and an argument naming one is refused.
    [PostgresFact] public async Task Every_op_reads_and_writes_as_the_app_whatever_the_call_says()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var app = (await MemoryLogins.Make(s, "app-channel")).App;
        {
            var services = AppMemoryCalls.Services(MemoryToolSettings.On, fixture.Db);
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "POST";
            context.Request.Headers["X-Rhythm-Channel"] = "cli";
            context.Request.Headers["User-Agent"] = "DEMO terminal";
            context.Items["channel"] = "cli";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(AppMemoryCalls.Body(AppEndpoint.Memories,
                new { op = "capture", content = "DEMO: the Wendle shed key is under the blue pot.", scope = "shared" })));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, new AppHealth(fixture.Db, MemoryToolSettings.On), AdapterRegistry.Empty);
            var id = Guid.Parse((string)JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!["id"]!);
            Assert.Equal("created:app:-", (string)(await Trail(id))!);
            var (refused, _) = await App(app, new { op = "append", id, text = "DEMO: or the red one.", channel = "cli" });
            Assert.Equal(400, refused);
            Assert.Equal("created:app:-", (string)(await Trail(id))!);
            Assert.Equal("app", (string)(await MemoryRows.Scalar(s, "SELECT source FROM memories WHERE id=@id", ("id", id)))!);
        }
    }

    // Truth over time in the app (SB-P4-11, D-192's app half): get and a list name a memory's state, what replaced it and
    // the retracted sources that flagged it; a status, an undo, a forget and a restore whose retraction cascade did
    // something name what it did, and a write whose cascade did nothing names none, as before. Each answer is checked
    // against the contract's schema (AppMemoryCalls.Call).
    [PostgresFact] public async Task The_app_names_a_memorys_truth_and_what_its_cascade_did()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "app-truth");
        var old = (await Store.Create(who.Alex, new("DEMO: the Wendle choir sings on Tuesdays."), MemoryLogins.Mcp, default)).Id;
        var fresh = (await Store.Create(who.Alex, new("DEMO: the Wendle choir sings on Wednesdays now."), MemoryLogins.Mcp, default)).Id;
        var built = (await Store.Create(who.Alex, new("DEMO: so the Wendle lift-share runs on Wednesdays."), MemoryLogins.Mcp, default)).Id;
        // The lift-share was drawn from the newer fact (a made link, as SB-P4-02's functions will write one).
        await MemoryRows.Run(s, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by) VALUES(gen_random_uuid(),@s,@t,'drawn_from','verified','DEMO link',@by)
            """, ("s", built), ("t", fresh), ("by", who.Alex));
        var plan = await Store.Supersede(who.Alex, fresh, old, null, MemoryLogins.Mcp, default);
        Assert.Equal("superseded", (await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, MemoryLogins.Mcp, default)).Action);

        async Task<JsonNode> Got(Guid id) => (await Ok(who.App, new { op = "get", id }))["memory"]!;
        var replaced = await Got(old);
        Assert.Equal(("replaced", fresh.ToString(), 0), ((string)replaced["state"]!, (string?)replaced["replacedBy"], replaced["retractedSources"]!.AsArray().Count));
        Assert.NotNull(replaced["validUntil"]);
        var current = await Got(fresh);
        Assert.Equal(("current", (string?)null), ((string)current["state"]!, (string?)current["replacedBy"]));
        var listed = (await Ok(who.App, new { op = "list", limit = 50 }))["memories"]!.AsArray().ToDictionary(m => Id(m!), m => m!);
        Assert.Equal(("replaced", fresh.ToString()), ((string)listed[old]["state"]!, (string?)listed[old]["replacedBy"]));
        Assert.Equal(("current", (string?)null), ((string)listed[built]["state"]!, (string?)listed[built]["replacedBy"]));

        // Marked wrong: what it had replaced is current again, and what was built on it flagged for a check.
        var wrong = await Ok(who.App, new { op = "status", id = fresh, status = "deprecated" });
        Assert.Equal($"reopened {old}; reclosed ; flagged {built}; unflagged ", Said(wrong["cascade"]!));
        Assert.Equal("retracted", (string)(await Got(fresh))["state"]!);
        Assert.Equal(("current", (string?)null), ((string)(await Got(old))["state"]!, (string?)(await Got(old))["replacedBy"]));
        Assert.Equal([fresh.ToString()], (await Got(built))["retractedSources"]!.AsArray().Select(x => (string)x!));
        // Brought back by an undo: replaced again, and the flag off.
        var back = await Ok(who.App, new { op = "undo", id = fresh });
        Assert.Equal($"reopened ; reclosed {old}; flagged ; unflagged {built}", Said(back["cascade"]!));
        Assert.Equal(("replaced", fresh.ToString()), ((string)(await Got(old))["state"]!, (string?)(await Got(old))["replacedBy"]));
        Assert.Empty((await Got(built))["retractedSources"]!.AsArray());
        // Into the trash and back: the same, named by the forget and the restore.
        var forgot = await Ok(who.App, new { op = "forget", id = fresh });
        Assert.Equal($"reopened {old}; reclosed ; flagged {built}; unflagged ", Said(forgot["cascade"]!));
        var restored = await Ok(who.App, new { op = "restore", id = fresh, nonce = (string)forgot["trash"]!["nonce"]! });
        Assert.Equal($"reopened ; reclosed {old}; flagged ; unflagged {built}", Said(restored["cascade"]!));
        // A write whose cascade did nothing names none, as before.
        Assert.Null((await Ok(who.App, new { op = "append", id = built, text = "DEMO: Sam drives first." }))["cascade"]);
        Assert.Null((await Ok(who.App, new { op = "status", id = built, status = "canonical" }))["cascade"]);

        // What a cascade did, in one line: each list's ids, in the contract's order.
        static string Said(JsonNode c) => string.Join("; ", new[] { "reopened", "reclosed", "flagged", "unflagged" }
            .Select(name => $"{name} {string.Join(",", c[name]!.AsArray().Select(x => (string)x!))}"));
    }

    // With scopes on the app's login is the household view: it reads Family and DEMO only, writes a memory only into
    // Family and only when the call names it, and changes a Family memory a person wrote only as the author lock allows
    // (memory.md section 5): never, since the household view is no person and no admin.
    [PostgresFact] public async Task With_scopes_on_the_app_reads_and_writes_as_the_household_view()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        try
        {
            await MemoryLogins.Family(s);
            var who = await MemoryLogins.Make(s, "app-scoped");
            var alexOwn = (await Store.Create(who.Alex, new("DEMO: Alex's own Wendle seed list.", ["wendle-scoped"]), MemoryLogins.Mcp, default)).Id;
            var alexFamily = (await Store.Create(who.Alex, new("DEMO: the Wendle plot rota for the family.", ["wendle-scoped"], "shared"), MemoryLogins.Mcp, default)).Id;

            Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "get", id = alexOwn }))["code"]!);
            var listed = (await Ok(who.App, new { op = "list", tag = "wendle-scoped" }))["memories"]!.AsArray().Select(m => Id(m!)).ToArray();
            Assert.Equal([alexFamily], listed);
            Assert.Empty((await Ok(who.App, new { op = "list", scope = "private" }))["memories"]!.AsArray());
            Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "history", id = alexOwn }))["code"]!);
            Assert.Equal("unknown", (string)(await Refused(who.App, new { op = "forget", id = alexOwn }))["code"]!);

            // The household view has no private scope; a capture names Family.
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "capture", content = "DEMO: nowhere to go." }))["code"]!);
            var made = await Ok(who.App, new { op = "capture", content = "DEMO: the Wendle water butt is full.", scope = "shared" });
            Assert.Equal(("shared", "household", "on"), ((string)made["scope"]!, (string)made["owner"]!, (string)made["scopes"]!));
            Assert.Equal(2, (int)(await Ok(who.App, new { op = "append", id = Id(made), text = "DEMO: empty it on Sunday." }))["revision"]!);

            // A Family memory Alex wrote is his or an admin's to change.
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "update", id = alexFamily, content = "DEMO: changed by the house." }))["code"]!);
            Assert.Equal("forbidden", (string)(await Refused(who.App, new { op = "forget", id = alexFamily }))["code"]!);
            Assert.Equal(1, (await Store.Get(who.Alex, alexFamily, default))!.Revision);
            // The trash lists only what this caller may restore.
            await Store.Forget(who.Alex, alexFamily, null, MemoryLogins.Mcp, default);
            Assert.DoesNotContain(alexFamily, (await Ok(who.App, new { op = "trash", limit = 50 }))["trash"]!.AsArray().Select(t => Id(t!)));
            await Ok(who.App, new { op = "forget", id = Id(made) });
            Assert.Contains(Id(made), (await Ok(who.App, new { op = "trash", limit = 50 }))["trash"]!.AsArray().Select(t => Id(t!)));
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}
