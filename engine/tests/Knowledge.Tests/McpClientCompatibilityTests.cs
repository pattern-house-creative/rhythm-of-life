using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Knowledge.Tests;

// The engine's /mcp as Program serves it, on an in-memory server: the MCP server Engine.Mcp builds, behind the door
// (Perimeter), with the ingest role's own endpoint and a stand-in for the app's (PerimeterTests covers the app's routes).
// The door checks fake tokens unless a test gives it a database's (McpClientCompatibilityDatabaseTests). Reached records
// which endpoint each request got to past the door.
sealed class McpEngine : IAsyncDisposable
{
    public const string Assistant = "DEMO-assistant-token", Plugin = "DEMO-plugin-token", App = "DEMO-app-token";
    public static readonly Caller AssistantCaller = new(Guid.NewGuid(), "assistant", "harness", null);
    public readonly ConcurrentDictionary<string, Caller> Tokens = new(StringComparer.Ordinal)
    {
        [Assistant] = AssistantCaller,
        [Plugin] = new(Guid.NewGuid(), "plugin:cgm", "ingest", "cgm"),
        [App] = new(Guid.NewGuid(), "app", "app", null),
    };
    public readonly ConcurrentQueue<string> Reached = new();
    public volatile bool Ready = true;
    WebApplication app = null!;

    public static async Task<McpEngine> Start(RhythmConfig config, Database? database = null, Func<string, CancellationToken, Task<Caller?>>? door = null,
        WriteLimit? limit = null, Action<IServiceCollection>? more = null)
    {
        // The tool descriptions name the settings' people (ToolText), as every test's settings do: alex, sam, household.
        TestSettings.Use();
        var engine = new McpEngine();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // Without a database every call here is answered before one is reached: example.invalid never resolves.
        var db = database ?? new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none"));
        builder.Services.AddSingleton(config).AddSingleton(db).AddSingleton(new Retrieval(db)).AddSingleton(new HealthInvestigation(db));
        builder.Services.AddHttpContextAccessor();
        if (limit is not null) builder.Services.AddSingleton(limit);
        more?.Invoke(builder.Services);
        Engine.Mcp(builder.Services, config, []);
        var app = builder.Build();
        var ingest = new Ingest(db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-compat-" + Guid.NewGuid().ToString("N"))), config);
        app.Use(new Perimeter(["engine"], () => engine.Ready, door ?? ((token, _) => Task.FromResult(engine.Tokens.GetValueOrDefault(token))), new(
            App: (c, _) => { engine.Reached.Enqueue("app"); return JsonRpc.Refuse(c, 403, null, "forbidden", "DEMO: the app's endpoint."); },
            Ingest: (c, caller) => { engine.Reached.Enqueue("ingest"); return IngestEndpoint.Handle(c, caller, ingest, config); },
            Document: (c, _, _) => { engine.Reached.Enqueue("document"); return Task.CompletedTask; })).Handle);
        app.Use(async (c, next) => { engine.Reached.Enqueue("harness"); await next(c); });
        app.MapMcp("/mcp");
        await app.StartAsync();
        engine.app = app;
        return engine;
    }

    public HttpMessageHandler Handler() => app.GetTestServer().CreateHandler();

    // An MCP SDK client over Streamable HTTP, as Claude Code, the Inspector and most clients connect. `authorization` is
    // the whole header (null: none), `version` the protocol it asks for (null: the SDK's newest).
    public static async Task<McpClient> Connect(HttpMessageHandler handler, string? authorization = "Bearer " + Assistant, string? version = null,
        HttpTransportMode mode = HttpTransportMode.StreamableHttp, string name = "DEMO client")
    {
        var http = new HttpClient(handler, disposeHandler: false);
        if (authorization is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri("http://engine/mcp"), TransportMode = mode, Name = name }, http, ownsHttpClient: true),
            new McpClientOptions { ClientInfo = new() { Name = name, Version = "1.0.0" }, ProtocolVersion = version }, cancellationToken: timeout.Token);
    }

    public const string Both = "application/json, text/event-stream";

    // One request written by hand, as a client without the SDK sends it; an event stream's JSON-RPC message is read out.
    public async Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> Send(string method, string path, string? body = null,
        string? authorization = "Bearer " + Assistant, string? accept = Both, params (string Name, string Value)[] headers)
    {
        using var http = new HttpClient(Handler());
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://engine" + path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l[6..]).FirstOrDefault() ?? "";
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), response);
    }

    public static string Initialize(string version) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = version, ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "DEMO client", ["version"] = "1.0.0" } },
        }.ToJsonString();

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

// One client's connection to whichever engine runs now: a restart puts a new engine behind it, and the client stays.
sealed class Relay : HttpMessageHandler
{
    public HttpMessageInvoker? Target;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Target!.SendAsync(request, cancellationToken);
}

// SB-P6-05: MCP client compatibility (ledger MCP-21, story 18), with memory.tools off and on. A client on any protocol
// version the engine speaks, from 2024-11-05 to the newest, connects over Streamable HTTP and gets today's tool list and
// answers with memory.tools off, absent or empty, and the same plus the 41 memory tools with it on; a client without
// a good token reaches nothing and is told 401; a connected client carries on after a refusal, a restart or a settings
// change without a new handshake, because /mcp keeps no session; the old names still answer; and what an older or simpler
// client meets is pinned here, so a change of the MCP SDK that moves any of it fails a test. Fictional client names only.
public sealed class McpClientCompatibilityTests
{
    // What tools/list shows on an install with the settings' defaults and no local model: the contract's harness tools
    // (ToolTests) but semantic_search, which needs the model, recall (engine.recall) and get_prompt_capsule
    // (memory.capsules), and with the alias save_household_document hidden (engine.legacyToolNames).
    static readonly string[] Today = ["explore_graph", "graph_changes", "get_relationship_graph", "search_knowledge", "search_resources",
        "get_resource", "get_related_resources", "discover_health_sources", "read_health_source", "investigate_health", "evaluate_health_hypothesis",
        "query_health_metrics", "compare_health_trends", "get_sync_status", "dashboard_data", "save_dashboard", "list_dashboards", "delete_dashboard",
        "dashboard_history", "save_insight", "list_insights", "log_insight_run", "save_document", "list_screens", "control_screen",
        "studio_dashboard", "discover_dashboard_controls", "invoke_dashboard_action"];

    // Every protocol a client may ask for (the SDK's list at 2.2.0), and null for the SDK's newest, 2026-07-28.
    static readonly string?[] Versions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", null];
    const string Newest = "2026-07-28";

    static RhythmConfig Settings(string memory) => memory == "" ? TestSettings.Use() : MemoryToolSettings.With(memory);
    static string Text(CallToolResult r) => string.Join(" ", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
    static Dictionary<string, object?> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
    static string Listing(Tool tool) => JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions);

    // Each of today's tools as tools/list shows it with memory.tools absent, by the same code and SDK at test time: so
    // the comparison with it shows a setting changes nothing, and Todays_tools_are_listed_on_the_wire_as_pinned (below)
    // pins the listing itself against the snapshot taken before run 12.
    static Dictionary<string, string> Before()
    {
        var config = TestSettings.Use();
        var listed = HarnessTools.Filter(new ListToolsResult { Tools = [.. HarnessTools.Create(config, []).Select(t => t.ProtocolTool)] },
            new ServiceCollection().BuildServiceProvider(), config);
        return listed.Tools.ToDictionary(t => t.Name, Listing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tools: off")]
    [InlineData("tools: null")]
    [InlineData("tools: on")]
    public async Task A_client_on_any_protocol_gets_todays_tools_and_answers(string memory)
    {
        var config = Settings(memory);
        var on = MemorySettings.ToolsOn(config);
        var before = Before();
        Assert.Equal(Today.Order(), before.Keys.Order());
        await using var engine = await McpEngine.Start(config);
        foreach (var version in Versions)
        {
            await using var client = await McpEngine.Connect(engine.Handler(), version: version);
            Assert.Equal(version ?? Newest, client.NegotiatedProtocolVersion);
            Assert.Equal("rhythm-engine", client.ServerInfo.Name);
            // ping is gone from the newest protocol (-32601 there); every older one answers it.
            if (version is not null) await client.PingAsync();

            // tools/list: today's tools, each listed byte for byte as before; on, the memory tools besides.
            var tools = await client.ListToolsAsync();
            Assert.Equal((on ? Today.Concat(MemoryTools.Names) : Today).Order(), tools.Select(t => t.Name).Order());
            // On, the map's reads add what a replaced fact's star is to their descriptions, and nothing else.
            foreach (var tool in tools.Where(t => Today.Contains(t.Name)))
                if (on && MapAware.Contains(tool.Name))
                {
                    var was = JsonNode.Parse(before[tool.Name])!.AsObject();
                    Assert.StartsWith((string)was["description"]!, tool.ProtocolTool.Description);
                    Assert.Equal(ToolText.Describe(tool.Name, config), tool.ProtocolTool.Description);
                    var now = JsonNode.Parse(Listing(tool.ProtocolTool))!.AsObject();
                    now["description"] = (string)was["description"]!;
                    Assert.True(JsonNode.DeepEquals(was, now), tool.Name);
                }
                else Assert.Equal(before[tool.Name], Listing(tool.ProtocolTool));

            // Answers that need no database, the same on every protocol: an argument refusal, the old name's, the guide.
            Assert.Equal("since is required: a date and time such as 2026-10-28T12:00:00Z.", Text(await client.CallToolAsync("graph_changes", Args("{}"))));
            var document = Args("""{"title":"DEMO","content":"DEMO","modifiedAt":"2026-10-28T12:00:00Z"}""");
            Assert.Equal("fileId is required: a string.", Text(await client.CallToolAsync("save_document", document)));
            Assert.Equal("fileId is required: a string.", Text(await client.CallToolAsync("save_household_document", document)));
            var guide = await client.ReadResourceAsync(ToolText.DashboardGuideUri);
            Assert.Equal(ToolText.DashboardGuide, Assert.IsType<TextResourceContents>(Assert.Single(guide.Contents)).Text);
            Assert.Equal([ToolText.DashboardGuideUri], (await client.ListPromptsAsync()).Select(p => p.Name));

            // A memory tool: off, answered exactly as a tool that does not exist; on, its own refusal of a bad call.
            if (on)
            {
                var refused = await client.CallToolAsync("remember", Args("{}"));
                Assert.True(refused.IsError);
                var body = JsonNode.Parse(Text(refused))!;
                Assert.Equal((false, "invalid"), ((bool)body["ok"]!, (string)body["code"]!));
                Assert.StartsWith("content", (string)body["error"]!);
            }
            else
            {
                var none = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("no_such_tool", Args("{}")).AsTask());
                foreach (var tool in MemoryTools.Names)
                {
                    var e = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(tool, Args(MemoryToolSettings.Valid[tool])).AsTask());
                    Assert.Equal((none.ErrorCode, none.Message.Replace("no_such_tool", tool)), (e.ErrorCode, e.Message));
                }
            }
        }
        // Nothing past the door but the harness's own endpoint.
        Assert.All(engine.Reached, r => Assert.Equal("harness", r));
    }

    // The listing pinned on the wire (contracts/fixtures/harness_tools.listing.json): tools/list's result.tools as a
    // client gets them, for an install with the settings' defaults and for one with engine.recall and memory.capsules on,
    // so recall and get_prompt_capsule, the two tools whose description memory.tools changes, are listed too. With
    // memory.tools off, absent or empty, every protocol gets the snapshot byte for byte; on, the same but for those two
    // descriptions, each the snapshot's with the memory tools' text after it, recall's as_of argument (SB-P4-07) after
    // its others, and the memory tools besides. The snapshot
    // was written from run 12's code at its integration, and checked at run 12's fix review against what sb/main before
    // run 12 (4f096a9) lists, by building that commit and asking its own server the same on every protocol: equal, both
    // settings. It holds for the test settings (ranked search on), not for every install's own settings, and is written
    // again only on purpose, as a reviewed change: with RHYTHM_TOOL_LISTING_WRITE set to its path, this test writes it
    // instead of comparing.
    const string ListingFile = "harness_tools.listing.json";
    static readonly string[] MemoryAware = ["recall", "get_prompt_capsule"];
    // The map's reads, which with the memory tools on say what a replaced fact's star is (SB-P4-11), and only then.
    static readonly string[] MapAware = ["get_relationship_graph", "explore_graph", "graph_changes"];
    static RhythmConfig Listed(string listing, string memory) => listing == "defaults" ? Settings(memory)
        : RhythmConfig.Parse(TestSettings.Yaml.Replace("engine: { legacySearch: false }", "engine: { legacySearch: false, recall: on }")
            + "\nmemory: { capsules: on" + (memory == "" ? "" : ", " + memory) + " }\n");

    [Theory]
    [InlineData("")]
    [InlineData("tools: off")]
    [InlineData("tools: null")]
    [InlineData("tools: on")]
    public async Task Todays_tools_are_listed_on_the_wire_as_pinned(string memory)
    {
        var write = Environment.GetEnvironmentVariable("RHYTHM_TOOL_LISTING_WRITE");
        var pinned = write is { Length: > 0 } ? null
            : JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", ListingFile)))!.AsObject();
        var written = new JsonObject
        {
            ["description"] = "The engine's harness tools as tools/list lists them on the wire (result.tools) with memory.tools absent, "
                + "on every protocol: for an install with the test settings' defaults, and for one with engine.recall and memory.capsules on. "
                + "Pinned by McpClientCompatibilityTests; written from run 12's code at its integration, and equal to what the trunk before run 12 lists "
                + "(checked at run 12's fix review by building that commit). Fictional install and people only.",
        };
        foreach (var listing in new[] { "defaults", "recall_capsules" })
        {
            var config = Listed(listing, memory);
            var on = MemorySettings.ToolsOn(config);
            await using var engine = await McpEngine.Start(config);
            foreach (var version in Versions.OfType<string>())
            {
                var (status, body, _) = await engine.Send("POST", "/mcp", """{"jsonrpc":"2.0","id":4,"method":"tools/list"}""",
                    headers: ("MCP-Protocol-Version", version));
                Assert.Equal(HttpStatusCode.OK, status);
                var tools = body!["result"]!["tools"]!.AsArray();
                if (pinned is null)
                {
                    if (version == Versions[0]) written[listing] = tools.DeepClone();
                    else Assert.Equal(written[listing]!.ToJsonString(), tools.ToJsonString());
                    continue;
                }
                var snapshot = pinned[listing]!.AsArray();
                if (!on)
                {
                    Assert.Equal(snapshot.ToJsonString(), tools.ToJsonString());
                    continue;
                }
                var named = tools.ToDictionary(t => (string)t!["name"]!);
                Assert.Equal(snapshot.Select(t => (string)t!["name"]!).Concat(MemoryTools.Names).Order(), named.Keys.Order());
                foreach (var tool in snapshot)
                {
                    var name = (string)tool!["name"]!;
                    var now = named[name]!.DeepClone().AsObject();
                    if (MemoryAware.Contains(name) || on && MapAware.Contains(name))
                    {
                        var was = (string)tool["description"]!;
                        var said = (string)now["description"]!;
                        Assert.StartsWith(was, said);
                        Assert.Equal(ToolText.Describe(name, config), said);
                        Assert.Contains("memory", said[was.Length..], StringComparison.Ordinal);
                        now["description"] = was;
                    }
                    // With the memory tools on recall takes as_of (SB-P4-07) and project (SB-P5-12) too, its last two
                    // arguments; the rest of its schema is the snapshot's.
                    if (name == "recall")
                    {
                        var properties = now["inputSchema"]!["properties"]!.AsObject();
                        Assert.Equal("project", properties.Last().Key);
                        Assert.Equal("""{"type":["string","null"],"default":null}""", properties["project"]!.ToJsonString());
                        properties.Remove("project");
                        Assert.Equal("as_of", properties.Last().Key);
                        Assert.Equal("""{"type":["string","null"],"format":"date-time","default":null}""", properties["as_of"]!.ToJsonString());
                        properties.Remove("as_of");
                    }
                    Assert.Equal(tool.ToJsonString(), now.ToJsonString());
                }
            }
        }
        Assert.Equal(["recall", "get_prompt_capsule"], MemoryAware.Where(t => (pinned ?? written)["recall_capsules"]!.AsArray().Any(x => (string)x!["name"]! == t)));
        Assert.DoesNotContain((pinned ?? written)["defaults"]!.AsArray(), t => MemoryAware.Contains((string)t!["name"]!));
        if (pinned is null && memory == "") File.WriteAllText(write!, written.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
    }

    // SB-P3-20: the handshake. With memory.tools off, absent or empty it answers exactly as before, byte for byte: the
    // protocol asked for, the four capabilities, the server's name and version, and no instructions, whatever the other
    // memory settings say. On, the same and the memory instructions, harness/memory.md's block byte for byte
    // (MemoryInstructionsTests). Every client the SDK makes is given them, the newest's server/discover included.
    [Theory]
    [InlineData("")]
    [InlineData("tools: off")]
    [InlineData("tools: null")]
    [InlineData("capsules: on")]
    [InlineData("tools: on")]
    public async Task Initialize_gives_the_memory_instructions_only_with_memory_tools_on(string memory)
    {
        var config = Settings(memory);
        var on = MemorySettings.ToolsOn(config);
        await using var engine = await McpEngine.Start(config);
        foreach (var version in Versions.OfType<string>())
        {
            var (status, body, _) = await engine.Send("POST", "/mcp", McpEngine.Initialize(version));
            Assert.Equal(HttpStatusCode.OK, status);
            var result = body!["result"]!.AsObject();
            var before = new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject { ["logging"] = new JsonObject(), ["prompts"] = new JsonObject(), ["resources"] = new JsonObject(), ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "rhythm-engine", ["version"] = typeof(Engine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
            };
            if (!on) Assert.Equal(before.ToJsonString(), result.ToJsonString());
            else
            {
                Assert.Equal(MemoryInstructions.Text, (string)result["instructions"]!);
                result.Remove("instructions");
                Assert.True(JsonNode.DeepEquals(before, result), result.ToJsonString());
            }
        }
        foreach (var version in Versions)
        {
            await using var client = await McpEngine.Connect(engine.Handler(), version: version);
            Assert.Equal(on ? MemoryInstructions.Text : null, client.ServerInstructions);
        }
    }

    // A client without a good token: no header, an unknown token, the scheme in lower case, another scheme, an empty one,
    // one over 600 characters. The SDK's client fails to connect with the 401 and the engine's words; nothing passes the
    // door. The 401 has no WWW-Authenticate challenge and the engine serves no OAuth metadata (Q-016: bearer tokens
    // only), so a client that looks for OAuth on a 401 finds 404s and asks its person for a token instead.
    [Theory]
    [InlineData("")]
    [InlineData("tools: on")]
    public async Task A_client_without_a_good_token_reaches_nothing_and_is_told_401(string memory)
    {
        await using var engine = await McpEngine.Start(Settings(memory));
        string?[] wrong = [null, "Bearer DEMO-unknown-token", "bearer " + McpEngine.Assistant, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alex:DEMO")),
            "Bearer ", "Bearer " + new string('a', Perimeter.MaxAuthorization)];
        foreach (var authorization in wrong)
        {
            var e = await Assert.ThrowsAsync<HttpRequestException>(() => McpEngine.Connect(engine.Handler(), authorization));
            Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
            Assert.Contains("Token missing or unknown.", e.Message);
            var (status, body, response) = await engine.Send("POST", "/mcp", McpEngine.Initialize("2025-06-18"), authorization);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("unauthorized", (string)body!["result"]!["structuredContent"]!["error"]!["code"]!);
            Assert.Empty(response.Headers.WwwAuthenticate);
            Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        }
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp",
            "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration", "/register", "/authorize", "/token", "/sse", "/mcp/" })
        {
            var (status, body, _) = await engine.Send("GET", path, authorization: null);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("not_found", (string)body!["result"]!["structuredContent"]!["error"]!["code"]!);
        }
        // A plugin's token reaches only the ingest role's own two tools, whatever memory.tools says; the app's token only
        // the app's endpoint. Neither reaches a harness or memory tool. The ingest role speaks 2025-03-26's handshake
        // only, and the SDK's newest client, which asks with server/discover first, falls back to it.
        await using (var plugin = await McpEngine.Connect(engine.Handler(), "Bearer " + McpEngine.Plugin))
        {
            Assert.Equal(JsonRpc.ProtocolVersion, plugin.NegotiatedProtocolVersion);
            Assert.Equal([IngestEndpoint.Measurements, IngestEndpoint.Records], (await plugin.ListToolsAsync()).Select(t => t.Name));
            foreach (var tool in new[] { "get_sync_status", "remember" })
                Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<HttpRequestException>(() => plugin.CallToolAsync(tool, Args("{}")).AsTask())).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await engine.Send("POST", "/mcp", McpEngine.Initialize("2025-06-18"), "Bearer " + McpEngine.App)).Status);
        Assert.DoesNotContain("harness", engine.Reached);
    }

    // A client the door turns away before it reads a token is told why in a JSON-RPC body, as harness/generic-mcp's
    // troubleshooting table says: a host name not in engine.allowedHosts, or a request carrying Origin (a web page or a
    // browser), 403 with the engine's words; a path other than exactly /mcp, 404 (above).
    [Fact] public async Task A_misaddressed_client_is_told_why()
    {
        await using var engine = await McpEngine.Start(TestSettings.Use());
        using var http = new HttpClient(engine.Handler());
        async Task<(HttpStatusCode, string)> Refused(string url, string? origin = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(McpEngine.Initialize("2025-06-18"), Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Accept", McpEngine.Both);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + McpEngine.Assistant);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            using var response = await http.SendAsync(request);
            var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!["structuredContent"]!["error"]!;
            Assert.Equal("forbidden", (string)error["code"]!);
            return (response.StatusCode, (string)error["message"]!);
        }
        Assert.Equal((HttpStatusCode.Forbidden, "The engine does not answer to this host name (engine.allowedHosts)."), await Refused("http://elsewhere.example.invalid/mcp"));
        Assert.Equal((HttpStatusCode.Forbidden, "Browser calls are not accepted."), await Refused("http://engine/mcp", "https://example.invalid"));
        Assert.Empty(engine.Reached);
    }

    // /mcp keeps no session, so a connected client needs no new handshake after a refusal: its token revoked and given
    // back, the engine starting (503) and ready again. Every request is checked at the door on its own, so a revoked
    // token is refused on the very next call.
    [Theory]
    [InlineData("")]
    [InlineData("tools: on")]
    public async Task A_connected_client_carries_on_after_a_refusal_without_a_new_handshake(string memory)
    {
        var config = Settings(memory);
        var on = MemorySettings.ToolsOn(config);
        await using var engine = await McpEngine.Start(config);
        await using var client = await McpEngine.Connect(engine.Handler(), version: "2025-06-18");
        async Task Answers()
        {
            Assert.Equal("since is required: a date and time such as 2026-10-28T12:00:00Z.", Text(await client.CallToolAsync("graph_changes", Args("{}"))));
            if (on) Assert.True((await client.CallToolAsync("remember", Args("{}"))).IsError);
            Assert.Equal(on ? Today.Length + MemoryTools.Names.Count : Today.Length, (await client.ListToolsAsync()).Count);
        }
        await Answers();

        Assert.True(engine.Tokens.TryRemove(McpEngine.Assistant, out var caller));
        foreach (var call in new Func<Task>[] { () => client.CallToolAsync("graph_changes", Args("{}")).AsTask(), () => client.ListToolsAsync().AsTask(),
            () => client.CallToolAsync("remember", Args("{}")).AsTask() })
        {
            var e = await Assert.ThrowsAsync<HttpRequestException>(call);
            Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
        }
        engine.Tokens[McpEngine.Assistant] = caller!;
        await Answers();

        engine.Ready = false;
        var starting = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListToolsAsync().AsTask());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, starting.StatusCode);
        Assert.Contains("The engine is starting; retry shortly.", starting.Message);
        // A client connecting meanwhile is told the same, and connects once the engine is ready.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Assert.ThrowsAsync<HttpRequestException>(() => McpEngine.Connect(engine.Handler()))).StatusCode);
        engine.Ready = true;
        await Answers();
        await using var later = await McpEngine.Connect(engine.Handler());
        Assert.Equal(client.ServerInstructions, later.ServerInstructions);
    }

    // A restart puts a new engine behind the same connection. The client carries on without a new handshake and sees the
    // restarted engine's tools: memory.tools turned on, then off again. A call naming a memory tool after it is turned off
    // is an unknown tool, as before they were built, and a client that connected while it was on keeps the instructions
    // its handshake gave, naming tools gone now, until it reconnects (harness/memory.md says to reconnect it). The engine
    // never hands out a session id, so a client never has one
    // to send back; a request that brings one anyway, a GET for a stream of server messages and a DELETE ending a session
    // are refused, and the SDK's own client needs none of them.
    [Fact] public async Task A_client_carries_on_across_restarts_and_a_settings_change_without_a_session()
    {
        var relay = new Relay();
        var engine = await McpEngine.Start(TestSettings.Use());
        relay.Target = new HttpMessageInvoker(engine.Handler());
        await using var older = await McpEngine.Connect(relay, version: "2025-03-26");
        await using var newest = await McpEngine.Connect(relay);
        McpClient[] clients = [older, newest];
        foreach (var client in clients)
        {
            Assert.Equal(Today.Order(), (await client.ListToolsAsync()).Select(t => t.Name).Order());
            Assert.Null(client.ServerInstructions);
        }
        var (_, _, initialized) = await engine.Send("POST", "/mcp", McpEngine.Initialize("2025-03-26"));
        Assert.False(initialized.Headers.Contains("Mcp-Session-Id"));
        await engine.DisposeAsync();

        McpClient? connectedOn = null;
        foreach (var memory in new[] { "tools: on", "tools: off" })
        {
            engine = await McpEngine.Start(Settings(memory));
            relay.Target = new HttpMessageInvoker(engine.Handler());
            var on = memory == "tools: on";
            if (on) connectedOn = await McpEngine.Connect(relay, version: "2025-06-18");
            else
            {
                Assert.Equal(MemoryInstructions.Text, connectedOn!.ServerInstructions);
                Assert.Equal(McpErrorCode.InvalidParams,
                    (await Assert.ThrowsAsync<McpProtocolException>(() => connectedOn.CallToolAsync("remember", Args("{}")).AsTask())).ErrorCode);
                Assert.Equal(Today.Order(), (await connectedOn.ListToolsAsync()).Select(t => t.Name).Order());
                await connectedOn.DisposeAsync();
            }
            foreach (var client in clients)
            {
                Assert.Equal((on ? Today.Concat(MemoryTools.Names) : Today).Order(), (await client.ListToolsAsync()).Select(t => t.Name).Order());
                if (on) Assert.True((await client.CallToolAsync("remember", Args("{}"))).IsError);
                else Assert.Equal(McpErrorCode.InvalidParams, (await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("remember", Args("{}")).AsTask())).ErrorCode);
                // The instructions are what the handshake gave, so a client reads new ones when it connects again.
                Assert.Null(client.ServerInstructions);
                await using var again = await McpEngine.Connect(relay, version: client.NegotiatedProtocolVersion == Newest ? null : client.NegotiatedProtocolVersion);
                Assert.Equal(on ? MemoryInstructions.Text : null, again.ServerInstructions);
            }
            await engine.DisposeAsync();
        }

        await using (engine = await McpEngine.Start(TestSettings.Use()))
        {
            var (status, body, _) = await engine.Send("POST", "/mcp", """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", headers: ("Mcp-Session-Id", "DEMO-old-session"));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("Mcp-Session-Id header is not supported", (string)body!["error"]!["message"]!);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await engine.Send("GET", "/mcp", accept: "text/event-stream")).Status);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await engine.Send("DELETE", "/mcp")).Status);
            // Written by hand without a handshake, as a client sends after a restart: answered under any older protocol's
            // header. (The newest protocol names itself in each request's _meta, as the SDK's client does above.)
            foreach (var version in Versions.OfType<string>())
            {
                var (listed, tools, _) = await engine.Send("POST", "/mcp", """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", headers: ("MCP-Protocol-Version", version));
                Assert.Equal(HttpStatusCode.OK, listed);
                Assert.Equal(Today.Order(), tools!["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).Order());
            }
        }
    }

    // What an older or simpler client meets. The old HTTP+SSE transport (2024-11-05's GET stream) is refused with 405, not
    // left waiting, and a client that tries Streamable HTTP first connects by it. A client must accept both JSON and an
    // event stream (406 otherwise, naming both). A JSON-RPC batch, which only 2025-03-26 allowed, is refused (400, -32600).
    // A protocol the engine does not speak is told the ones it does. The old names answer as their new ones, listed only
    // with engine.legacyToolNames, and no memory tool takes a name any tool or alias has.
    [Fact] public async Task An_older_client_meets_the_old_names_and_clear_refusals()
    {
        var config = TestSettings.Use();
        await using var engine = await McpEngine.Start(config);
        var sse = await Assert.ThrowsAsync<HttpRequestException>(() => McpEngine.Connect(engine.Handler(), mode: HttpTransportMode.Sse));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, sse.StatusCode);
        await using (var auto = await McpEngine.Connect(engine.Handler(), mode: HttpTransportMode.AutoDetect))
            Assert.Equal(Today.Order(), (await auto.ListToolsAsync()).Select(t => t.Name).Order());

        foreach (var accept in new[] { "application/json", "text/event-stream", null })
        {
            var (status, body, _) = await engine.Send("POST", "/mcp", McpEngine.Initialize("2025-06-18"), accept: accept);
            Assert.Equal(HttpStatusCode.NotAcceptable, status);
            Assert.Contains("must accept both application/json and text/event-stream", (string)body!["error"]!["message"]!);
        }
        Assert.Equal(HttpStatusCode.OK, (await engine.Send("POST", "/mcp", McpEngine.Initialize("2025-06-18"), accept: "*/*")).Status);
        var batch = await engine.Send("POST", "/mcp", """[{"jsonrpc":"2.0","id":4,"method":"ping"},{"jsonrpc":"2.0","id":5,"method":"ping"}]""");
        Assert.Equal((HttpStatusCode.BadRequest, -32600), (batch.Status, (int)batch.Body!["error"]!["code"]!));
        Assert.Equal(HttpStatusCode.Accepted, (await engine.Send("POST", "/mcp", """{"jsonrpc":"2.0","method":"notifications/initialized"}""")).Status);

        var (_, future, _) = await engine.Send("POST", "/mcp", McpEngine.Initialize("2099-01-01"));
        Assert.Equal(Versions.OfType<string>(), future!["error"]!["data"]!["supported"]!.AsArray().Select(v => (string)v!));

        // An old client's protocol answers its own way: the version it asked for, and no member a newer one added.
        foreach (var version in Versions.OfType<string>())
        {
            var (_, initialized, _) = await engine.Send("POST", "/mcp", McpEngine.Initialize(version));
            Assert.Equal(version, (string)initialized!["result"]!["protocolVersion"]!);
        }

        // The old names: with engine.legacyToolNames the alias is listed as the old name of its tool, its arguments the
        // same; without it, hidden but answered (above). carelinkSource, glucoseSource's deprecated alias, still checked.
        var legacy = RhythmConfig.Parse(TestSettings.Yaml.Replace("engine: { legacySearch: false }", "engine: { legacySearch: false, legacyToolNames: true }"));
        await using (var old = await McpEngine.Start(legacy))
        await using (var client = await McpEngine.Connect(old.Handler(), version: "2024-11-05"))
        {
            var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);
            Assert.Equal(Today.Append("save_household_document").Order(), tools.Keys.Order());
            Assert.StartsWith("Old name of save_document; the same tool. ", tools["save_household_document"].Description);
            Assert.Equal(tools["save_document"].ProtocolTool.InputSchema.GetRawText(), tools["save_household_document"].ProtocolTool.InputSchema.GetRawText());
            var glucose = await client.CallToolAsync("query_health_metrics", Args("""{"subject":"alex","metric":"glucose","from":"2026-10-01","until":"2026-10-30","carelinkSource":"pump"}"""));
            Assert.True(glucose.IsError);
            Assert.Contains("carelinkSource is csv or live; it is deprecated: use glucoseSource", Text(glucose));
        }
        var names = HarnessTools.Create(MemoryToolSettings.On, []).Select(t => t.ProtocolTool.Name).Where(n => !MemoryTools.Names.Contains(n))
            .Concat(HarnessTools.Aliases.Keys).Concat(IngestEndpoint.Tools().Select(t => (string)JsonSerializer.SerializeToNode(t)!["name"]!)).ToHashSet();
        Assert.DoesNotContain(MemoryTools.Names, names.Contains);
    }
}

// The same through the real door: tokens made as rhythm token create makes them, checked against the database
// (Engine.Door). A client connects and reads, a revoked token is refused on its next call and a new one connects; with
// memory.tools on, an older client and the newest each save a memory, the older recorded with no client's name (it names
// itself only at initialize) and the newest by name (MemoryToolStatelessTests).
public sealed class McpClientCompatibilityDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    async Task<string> Token(string name, string? person = null)
    {
        var file = Path.Combine(Path.GetTempPath(), "rhythm-compat-" + Guid.NewGuid().ToString("N"), "token");
        await new Principals(fixture.Source).Create(name, "harness", null, person, file, default);
        var token = await File.ReadAllTextAsync(file);
        Directory.Delete(Path.GetDirectoryName(file)!, true);
        return token;
    }

    [PostgresFact] public async Task A_real_token_connects_and_a_revoked_one_is_refused_on_its_next_call()
    {
        await Scoped.Mode(fixture.Source, "off");
        var config = TestSettings.Use();
        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config));
        var token = await Token("compat-client");
        await using var client = await McpEngine.Connect(engine.Handler(), "Bearer " + token, "2025-06-18");
        var status = await client.CallToolAsync("get_sync_status", new Dictionary<string, object?>());
        Assert.True(status.IsError != true, string.Join(" ", status.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(status.Content.OfType<TextContentBlock>().First().Text).RootElement.ValueKind);

        Assert.Equal(1, await new Principals(fixture.Source).Revoke("compat-client", default));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.CallToolAsync("get_sync_status", new Dictionary<string, object?>()).AsTask());
        Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => McpEngine.Connect(engine.Handler(), "Bearer " + token))).StatusCode);
        // A new token for the same client connects; the revoked one stays refused.
        await using var again = await McpEngine.Connect(engine.Handler(), "Bearer " + await Token("compat-client-2"));
        Assert.True((await again.CallToolAsync("get_sync_status", new Dictionary<string, object?>())).IsError != true);
    }

    [PostgresFact] public async Task With_memory_tools_on_an_older_client_and_the_newest_each_save_a_memory()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        TestSettings.Use();
        var config = MemoryToolSettings.On;
        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config));
        var token = await Token("compat-alex", "alex");
        foreach (var (version, client) in new[] { ("2025-06-18", "-"), ("2025-11-25", "-"), ((string?)null, "DEMO client") })
        {
            await using var ai = await McpEngine.Connect(engine.Handler(), "Bearer " + token, version);
            Assert.Equal(MemoryInstructions.Text, ai.ServerInstructions);
            var made = await ai.CallToolAsync("remember", new Dictionary<string, object?> { ["content"] = $"DEMO: the compost goes out on Mondays ({version ?? "newest"})." });
            var answer = JsonNode.Parse(made.Content.OfType<TextContentBlock>().First().Text)!;
            Assert.True(made.IsError != true, answer.ToJsonString());
            var id = Guid.Parse((string)answer["id"]!);
            var got = JsonNode.Parse((await ai.CallToolAsync("get", new Dictionary<string, object?> { ["id"] = id })).Content.OfType<TextContentBlock>().First().Text)!;
            Assert.Equal($"DEMO: the compost goes out on Mondays ({version ?? "newest"}).", (string)got["memory"]!["content"]!);
            Assert.Equal($"created:mcp:{client}", await MemoryRows.Scalar(s,
                "SELECT string_agg(event||':'||channel||':'||coalesce(client,'-'),',' ORDER BY id) FROM memory_events WHERE memory_id=@id", ("id", id)));
        }
    }
}
