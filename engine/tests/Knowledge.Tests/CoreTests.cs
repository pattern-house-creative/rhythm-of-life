using System.IO.Pipelines;
using System.Text.Json;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knowledge.Tests;

// Settings shared by the tests: the fictional people alex and sam, Europe/London.
public static class TestSettings
{
    public const string Yaml = """
        version: 1
        instance: { name: Test install }
        timezone: Europe/London
        people:
          - { id: alex, name: Alex }
          - { id: sam, name: Sam }
        demo: { enabled: true }
        engine: { legacySearch: false }
        plugins:
          cgm:
            enabled: true
            providers: [cgm]
            subjects: [alex]
            policy: archive
            domain: health
          pump:
            enabled: true
            providers: [pump-live, pump-csv]
            subjects: [alex]
            policy: live
            policies: { pump-csv: archive }
            domain: health
        """;
    static readonly Lazy<RhythmConfig> config = new(() => RhythmConfig.Parse(Yaml));
    public static RhythmConfig Use() { RhythmConfig.Current = config.Value; return config.Value; }
}

public sealed class ConfigTests
{
    [Fact] public void The_example_settings_load_with_people_household_and_time_zone()
    {
        var config = RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"));
        Assert.Equal(["alex", "sam", "household"], config.Subjects);
        Assert.Equal("Europe/London", config.Timezone);
        Assert.True(config.Demo.Enabled);
        Assert.True(config.AdapterEnabled("notes"));
        Assert.False(config.AdapterEnabled("garmin"));
        Assert.Contains("engine", config.Engine.AllowedHosts);
    }

    [Theory]
    [InlineData("timezone: Mars/Olympus", "timezone")]
    [InlineData("people: []", "people")]
    [InlineData("people: [{ id: household, name: Shared }]", "people[0].id")]
    [InlineData("adapters: { drive: { enabled: true, refreshToken: abc } }", "never hold a secret")]
    [InlineData("plugins: { cgm: { enabled: true, providers: [cgm], subjects: [nobody], policy: archive } }", "subjects")]
    [InlineData("plugins: { cgm: { enabled: true, providers: [cgm], subjects: [alex], policy: live, policies: { other: archive } } }", "policies.other")]
    [InlineData("plugins: { cgm: { enabled: true, providers: [cgm], subjects: [alex], policy: live, policies: { cgm: cached } } }", "policies.cgm")]
    public void Wrong_settings_fail_at_start_naming_the_key(string line, string expected)
    {
        var yaml = $"version: 1\ntimezone: UTC\npeople: [{{ id: alex, name: Alex }}]\n{line}\n";
        var e = Assert.Throws<ConfigException>(() => RhythmConfig.Parse(yaml));
        Assert.Contains(expected, e.Message);
    }

    // A key the settings schema does not name is refused with its place and the key it most likely meant. Left out
    // quietly, as it used to be, `engine.writelimit: on` kept the write limit off and `doctor` called the file valid.
    [Theory]
    [InlineData("engine: { writelimit: on }", "engine.writelimit (line 4; did you mean engine.writeLimit?)")]
    [InlineData("timezon: Europe/London", "timezon (line 4; did you mean timezone?)")]
    [InlineData("graph: { similarity: { treshold: 0.5 } }", "graph.similarity.treshold (line 4; did you mean graph.similarity.threshold?)")]
    [InlineData("adapters: { cloudflare: { enabled: false, mcpHostName: rhythm-mcp.example.invalid } }", "adapters.cloudflare.mcpHostName (line 4; did you mean adapters.cloudflare.mcpHostname?)")]
    [InlineData("memory: { tools: on }\nengine: { recal: on }", "engine.recal (line 5; did you mean engine.recall?)")]
    [InlineData("household: { pets: 2 }", "household (line 4)")]
    public void A_setting_this_version_does_not_know_is_refused_naming_it(string line, string expected)
    {
        var yaml = $"version: 1\ntimezone: UTC\npeople: [{{ id: alex, name: Alex }}]\n{line}\n";
        var e = Assert.Throws<ConfigException>(() => RhythmConfig.Parse(yaml));
        Assert.Contains("does not know: " + expected, e.Message);
        Assert.Contains("docs/configuration.md", e.Message);
    }

    [Fact] public void Several_unknown_settings_are_named_together_and_a_persons_own_keys_are_checked_too()
    {
        var e = Assert.Throws<ConfigException>(() => RhythmConfig.Parse(
            "version: 1\ntimezone: UTC\npeople: [{ id: alex, name: Alex, nickname: Al }]\ndemo: { enable: false }\n"));
        Assert.Contains("settings this version does not know: people[].nickname (line 3), demo.enable (line 4; did you mean demo.enabled?)", e.Message);
        // Two sections that read alike are two places: a key known in one is still unknown in the other.
        var twice = SettingsKeys.Find("instance: { name: Home }\ndemo: { name: Home }\nadapters: { notes: { enabled: false, pth: a }, csv: { enabled: false, pth: a } }\n");
        Assert.Equal(["demo.name", "adapters.notes.pth", "adapters.csv.pth"], twice.Select(u => u.Path));
    }

    // What stays allowed: every key of the shipped example and of the tests' own settings, an adapter this build does not
    // describe with keys of its own (a plugin's), and any provider or plugin name.
    [Fact] public void Known_settings_and_an_outside_adapters_own_keys_are_not_refused()
    {
        Assert.Empty(SettingsKeys.Find(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"))));
        Assert.Empty(SettingsKeys.Find(TestSettings.Yaml));
        Assert.Empty(SettingsKeys.Find("adapters: { weather: { enabled: true, station: north, pollSeconds: 600 } }\nplugins: { anything: { enabled: false } }\n"));
        Assert.Empty(SettingsKeys.Find("not: [valid"));
        Assert.Null(SettingsKeys.Problem("version: 1\n"));
    }

    [Fact] public void Similar_links_are_on_by_default_and_null_turns_them_off()
    {
        const string Head = "version: 1\ntimezone: UTC\npeople: [{ id: alex, name: Alex }]\n";
        foreach (var on in new[] { RhythmConfig.Parse(Head), RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml")) })
            Assert.Equal((0.6, 3, 0.8, 0.45), (on.Graph.Similarity.Threshold, on.Graph.Similarity.Neighbors,
                on.Graph.Similarity.DocumentThreshold, on.Graph.Similarity.MutualThreshold));
        // threshold null alone turns every similar link off; the document and mutual thresholds then do nothing.
        var off = RhythmConfig.Parse(Head + "graph: { similarity: { threshold: null } }\n");
        Assert.Null(off.Graph.Similarity.Threshold);
        var host = RelationshipHostConfiguration.From(off, Guid.NewGuid(), Path.GetTempPath());
        Assert.Equal((null, null, null), (host.SimilarityThreshold, host.SimilarityDocumentThreshold, host.SimilarityMutualThreshold));
        host.Validate();
        Assert.Equal(0.45, RelationshipHostConfiguration.From(RhythmConfig.Parse(Head), Guid.NewGuid(), Path.GetTempPath()).SimilarityMutualThreshold);
        Assert.Null(RhythmConfig.Parse(Head + "graph: { similarity: { documentThreshold: null } }\n").Graph.Similarity.DocumentThreshold);
        Assert.Null(RhythmConfig.Parse(Head + "graph: { similarity: { mutualThreshold: null } }\n").Graph.Similarity.MutualThreshold);
        Assert.Throws<ConfigException>(() => RhythmConfig.Parse(Head + "graph: { similarity: { threshold: 0.1 } }\n"));
        Assert.Throws<ConfigException>(() => RhythmConfig.Parse(Head + "graph: { similarity: { mutualThreshold: 0.1 } }\n"));
    }

    [Fact] public void A_plugin_may_give_each_of_its_providers_its_own_policy()
    {
        var config = TestSettings.Use();
        var policies = AdapterRegistry.Empty.Policies(config);
        Assert.Equal(SourceAuthority.LiveSource, policies.For("pump-live").Authority);
        Assert.Equal(SourceAuthority.AuthorizedArchive, policies.For("pump-csv").Authority);
        Assert.Equal(SourceAuthority.AuthorizedArchive, policies.For("cgm").Authority);
        Assert.Equal("health", policies.For("pump-csv").DefaultDomain);
    }

    [Fact] public void Providers_an_older_engine_wrote_keep_the_policy_settings_give_them()
    {
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nproviders:\n  clinic-notes: { policy: archive, domain: health }\n  documents: { policy: live }\n");
        var policies = AdapterRegistry.Empty.Policies(config);
        Assert.Equal((SourceAuthority.AuthorizedArchive, "health"), (policies.For("clinic-notes").Authority, policies.For("clinic-notes").DefaultDomain));
        // The core's own providers keep their policy; an unlisted provider is still live.
        Assert.Equal(SourceAuthority.AuthorizedArchive, policies.For("documents").Authority);
        Assert.Equal(SourceAuthority.LiveSource, policies.For("unknown-source").Authority);
        Assert.Throws<ConfigException>(() => RhythmConfig.Parse(TestSettings.Yaml + "\nproviders:\n  clinic-notes: { policy: trusted }\n"));
    }

    [Fact] public void The_health_tools_take_a_glucose_source_and_carelinkSource_is_its_deprecated_alias()
    {
        // The older carelinkSource argument picks one of the CareLink plugin's providers; glucoseSource wins.
        Assert.Equal("carelink-csv", HarnessTools.GlucoseSource("", "csv"));
        Assert.Equal("carelink-live", HarnessTools.GlucoseSource("", "live"));
        Assert.Equal("", HarnessTools.GlucoseSource("", null));
        Assert.Equal("nightscout", HarnessTools.GlucoseSource("nightscout", "live"));
        Assert.Contains("glucoseSource", Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.GlucoseSource("", "pump")).Message);
        // Both are listed with what they do, the old one as deprecated.
        foreach (var tool in HarnessTools.Create(TestSettings.Use(), []).Where(t => t.ProtocolTool.Name is "investigate_health" or "evaluate_health_hypothesis"
            or "query_health_metrics" or "compare_health_trends"))
        {
            var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
            Assert.Contains("glucose readings from that provider only", properties.GetProperty("glucoseSource").GetProperty("description").GetString());
            Assert.StartsWith("Deprecated: use glucoseSource", properties.GetProperty("carelinkSource").GetProperty("description").GetString());
            Assert.Contains("glucoseSource", tool.ProtocolTool.Description);
        }
    }

    [Fact] public void Subjects_and_defaults_come_from_people_not_from_code()
    {
        TestSettings.Use();
        Assert.Equal("alex", DashboardWrite.DefaultSubject);
        Assert.Equal("alex, sam or household", DashboardWrite.SubjectList);
        Assert.Equal("feeds[0].subject is alex, sam or household", new DashboardFeed("x", "log", null!, Log: "walks", Subject: "nobody").Problem(0));
        Assert.Equal("sam", new DashboardWrite("w", "log", Log: "walks", Subject: "sam").Normalized().Subject);
        Assert.Equal("alex", new DashboardWrite("w", "log", Log: "walks").Normalized().Subject);
    }
}

// The harness role's login as the endpoint sets it, for tools called in process.
internal sealed class HarnessLogin(Guid principal) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = new DefaultHttpContext { Items = { ["principal"] = principal } };
}

public sealed class ToolTests
{
    [Fact] public void Harness_tools_match_the_contract_and_name_no_one()
    {
        var config = TestSettings.Use();
        var tools = HarnessTools.Create(config, []);
        var names = tools.Select(t => t.ProtocolTool.Name).ToHashSet();
        string[] contract = ["explore_graph", "graph_changes", "get_relationship_graph", "search_knowledge", "search_resources", "semantic_search",
            "get_resource", "get_related_resources", "discover_health_sources", "read_health_source", "investigate_health", "evaluate_health_hypothesis",
            "query_health_metrics", "compare_health_trends", "get_sync_status", "dashboard_data", "save_dashboard", "list_dashboards", "delete_dashboard",
            "dashboard_history", "save_insight", "list_insights", "log_insight_run", "save_document", "list_screens", "control_screen",
            "studio_dashboard", "discover_dashboard_controls", "invoke_dashboard_action",
            // Listed only with engine.recall on (HarnessTools.Filter), as semantic_search only with the model.
            "recall",
            // Listed only with memory.capsules on (HarnessTools.Filter).
            "get_prompt_capsule"];
        Assert.All(contract, c => Assert.Contains(c, names));
        Assert.Contains("save_household_document", names);
        Assert.Equal(contract.Length + 1, names.Count);
        // The memory tools (SB-P3-09) are made only with memory.tools on (MemoryToolTests): with it off, as here, none.
        Assert.DoesNotContain(names, MemoryTools.Names.Contains);
        foreach (var tool in tools)
        {
            var text = tool.ProtocolTool.Description!;
            foreach (var token in new[] { "{people}", "{subjects}", "{first}", "{instance}" }) Assert.DoesNotContain(token, text);
            Assert.DoesNotContain("ChatGPT", text);
            Assert.DoesNotContain("NAS", text);
        }
        Assert.Contains("Alex, Sam and the household", tools.Single(t => t.ProtocolTool.Name == "explore_graph").ProtocolTool.Description);
    }

    // Through the MCP SDK, in process: a call outside a tool's schema answers which argument and what it takes, never the
    // SDK's generic "An error occurred invoking", and never the value that was sent.
    [Fact] public async Task A_wrong_argument_is_named_with_what_it_takes()
    {
        var config = TestSettings.Use();
        var echo = new CheckedTool(McpServerTool.Create((int depth) => $"depth {depth}", new McpServerToolCreateOptions { Name = "echo" }));
        var toServer = new Pipe(); var toClient = new Pipe();
        // A harness login for the calls that reach a tool. Its database is never reached: each bound is checked first.
        var database = new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none"));
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(Guid.NewGuid()))
            .AddSingleton(database).AddSingleton(new Retrieval(database)).AddSingleton(new HealthInvestigation(database)).BuildServiceProvider();
        await using var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [.. HarnessTools.Create(config, []), echo] }, null, services);
        var running = server.RunAsync();
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        async Task<string> Call(string tool, string args)
        {
            var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(args)!);
            var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            Assert.DoesNotContain("An error occurred", text);
            return text;
        }
        async Task<string> Refused(string tool, string args)
        {
            var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(args)!);
            Assert.True(result.IsError);
            return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        }
        Assert.Equal("since is required: a date and time such as 2026-10-28T12:00:00Z.", await Call("graph_changes", "{}"));
        Assert.Equal("since must be a date and time such as 2026-10-28T12:00:00Z.", await Call("graph_changes", """{"since":"last tuesday"}"""));
        Assert.Equal("depth must be a whole number.", await Call("explore_graph", """{"depth":"two"}"""));
        Assert.Equal("bogus is not an argument of explore_graph; it takes query, star, depth, limit.", await Call("explore_graph", """{"bogus":1}"""));
        Assert.Equal("That argument is not an argument of get_sync_status; it takes none.", await Call("get_sync_status", """{"a private note":1}"""));
        Assert.Equal("dashboardId must be a UUID.", await Call("delete_dashboard", """{"dashboardId":"my dashboard","title":"Walks"}"""));
        Assert.Equal("points[1].text is required: a string or null.", await Call("save_insight", """
            {"kind":"discovery","title":"t","summary":"s","whyItMatters":"w","nextStep":"n","evidenceLevel":"weak","limitations":"l",
             "generatedAt":"2026-10-28T12:00:00Z","sourcesAsOf":"2026-10-28T12:00:00Z","points":[{"text":"a","type":"finding"},{"type":"finding"}]}
            """));
        Assert.Equal("fileId is required: a string.", await Call("save_household_document", """{"title":"t","content":"c","modifiedAt":"2026-10-28T12:00:00Z"}"""));
        Assert.Equal("feeds must be a list or null.", await Call("dashboard_data", """{"feeds":"walks"}"""));
        Assert.Equal("full must be true or false.", await Call("dashboard_data", """{"full":"yes"}"""));
        // The SDK binds a layout and a focus with member names in any case and numbers written as strings; the tools read
        // them again as written, by the engine's rules, so a member named in another case is refused before anything is
        // read or saved.
        Assert.EndsWith("layout.parts[0] takes only id, kind, x, y, w and h, with focus for a map or dashboard for a card", await Refused("save_dashboard", """
            {"note":"Moved","dashboardId":"6d3c2a10-0000-4000-8000-00000000d001","layout":{"parts":[{"id":"page","kind":"page","x":0,"X":"5","y":0,"w":12,"h":12}]}}
            """));
        Assert.EndsWith("focus takes query, near {star, depth}, types, providers, groups, owners and links", await Refused("control_screen", """
            {"action":"focus_map","focus":{"near":{"star":"notes:notes:Plan.md","Depth":"2"}}}
            """));
        // A call that fits the schema but not the tool's bounds is told the bound, not just "An error occurred invoking".
        Assert.EndsWith(": Range must be 0–365 days", await Refused("query_health_metrics", """{"subject":"alex","metric":"glucose","from":"2025-01-01","until":"2026-10-30"}"""));
        Assert.EndsWith(": Limit must be 1–500", await Refused("query_health_metrics", """{"subject":"alex","metric":"glucose","from":"2026-10-01","until":"2026-10-30","limit":1000}"""));
        Assert.EndsWith(": Explicit subject and 0–365 day range required", await Refused("discover_health_sources", """{"subject":"alex","from":"2025-01-01","until":"2026-10-30"}"""));
        Assert.EndsWith(": At most 12 selected metrics", await Refused("investigate_health", """{"subject":"alex","from":"2026-10-01","until":"2026-10-30","metrics":"a,b,c,d,e,f,g,h,i,j,k,l,m"}"""));
        Assert.EndsWith(": Limit must be 1–50", await Refused("search_knowledge", """{"limit":0}"""));
        // A call that fits goes through to the tool unchanged.
        Assert.Equal("depth 2", await Call("echo", """{"depth":2}"""));
        Assert.Equal("depth is required: a whole number.", await Call("echo", "{}"));
    }

    [Fact] public void App_role_validation_is_strict_and_old_names_are_aliases()
    {
        TestSettings.Use();
        JsonElement Call(string tool, string args) => JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + "}}").RootElement;
        Assert.True(AppEndpoint.Allowed(Call("app_graph", """{"operation":"search","value":"walk"}""")));
        Assert.True(AppEndpoint.Allowed(Call("app_graph", """{"operation":"project","value":"memory:project:6d3c2a10-0000-4000-8000-00000000e001"}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_graph", """{"operation":"projects","value":"walks"}""")));
        Assert.True(AppEndpoint.Allowed(Call("get_relationship_graph", "{}")));
        Assert.True(AppEndpoint.Allowed(Call("observatory_insights", """{"limit":5}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_insights", """{"limit":51}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_graph", """{"bogus":1}""")));
        Assert.False(AppEndpoint.Allowed(Call("save_dashboard", "{}")));
        Assert.True(AppEndpoint.Allowed(Call("app_recent_glucose", """{"days":14,"subject":"sam"}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_recent_glucose", """{"days":7}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_latest_glucose", """{"subject":"nobody"}""")));
        Assert.True(AppEndpoint.Allowed(Call("app_dashboards", """{"pin":"rhythm:x","pinned":true}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_dashboards", """{"pin":"rhythm:x","pinned":true,"cards":true}""")));
        Assert.True(AppEndpoint.Allowed(Call("app_insight_feedback", """{"id":"6d3c2a10-0000-4000-8000-00000000e001","value":"later","until":"2026-11-01T00:00:00Z"}""")));
        Assert.False(AppEndpoint.Allowed(Call("app_insight_feedback", """{"id":"6d3c2a10-0000-4000-8000-00000000e001","value":"handled","until":"2026-11-01T00:00:00Z"}""")));
        // app_screens: each op with exactly its arguments, and send's command as control_screen checks it.
        const string Kitchen = "\"screen\":\"kitchen-0123456789ab\"";
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\"," + Kitchen + ",\"name\":\"Kitchen iPad\",\"hold\":25,\"showing\":{\"view\":\"home\"},\"settings\":{}}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\"," + Kitchen + ",\"name\":\"Kitchen iPad\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\"," + Kitchen + "}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\"," + Kitchen + ",\"name\":\"Kitchen iPad\",\"hold\":26}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\"," + Kitchen + ",\"name\":\"Kitchen iPad\",\"showing\":\"home\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\",\"screen\":\"short\",\"name\":\"Kitchen iPad\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"listen\"," + Kitchen + ",\"name\":\"   \"}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"done\"," + Kitchen + ",\"command\":\"6d3c2a10-0000-4000-8000-00000000c001\",\"result\":\"failed\",\"detail\":{\"summary\":\"Nothing to undo\"}}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"done\"," + Kitchen + ",\"command\":\"6d3c2a10-0000-4000-8000-00000000c001\",\"result\":\"expired\"}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"open_star\",\"star\":\"rhythm:x\"}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"move_tile\",\"args\":{\"tile\":\"main\",\"to\":\"changed\"}}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"corner_card\",\"corner\":\"main\",\"dashboard\":null}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"home\",\"star\":\"rhythm:x\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"open_star\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"dance\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"to\":\"hallway-0123456789ab\",\"action\":\"home\",\"args\":{},\"star\":\"rhythm:x\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"send\"," + Kitchen + ",\"action\":\"home\"}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"list\"," + Kitchen + "}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"list\"," + Kitchen + ",\"name\":\"Kitchen iPad\"}")));
        Assert.True(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"rename\"," + Kitchen + ",\"name\":\"Kitchen\"}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"forget\"," + Kitchen + "}")));
        Assert.False(AppEndpoint.Allowed(Call("app_screens", "{\"op\":\"list\"}")));
        Assert.Equal(AppEndpoint.Graph, AppEndpoint.Aliases["get_relationship_graph"]);
        Assert.All(AppEndpoint.Aliases.Values, v => Assert.StartsWith("app_", v));
    }

    [Fact] public void Insight_labels_are_short_labels_not_one_vendors_names()
    {
        var now = DateTimeOffset.UtcNow;
        InsightDraft Draft(string by) => new("discovery", "Fictional title", "Fictional summary", "", "", [], "weak", "", [], [], by, now, now, null);
        Assert.Null(Draft("harness-daily").Problem(now));
        Assert.Null(Draft("weekly").Problem(now));
        Assert.NotNull(Draft("Not A Label").Problem(now));
    }
}

public sealed class IngestValidationTests
{
    // Validation happens before any database work, so these never open a connection.
    static readonly Ingest ingest = new(new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none")), new Archive(Path.GetTempPath()), TestSettings.Use());
    static JsonElement Fixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", name))).RootElement;

    [Fact] public async Task A_plugin_writes_only_its_providers_and_subjects()
    {
        var e = await Assert.ThrowsAsync<Ingest.Refused>(() => ingest.Measurements("cgm", Fixture("ingest_measurements.example.json"), default));
        // The contract's example writes provider carelink; this test plugin may write only cgm.
        Assert.Equal("provider_not_allowed", e.Code);
        Assert.Contains(e.Issues, i => (string?)i["path"] == "/provider");
    }

    [Fact] public async Task Invalid_items_are_all_reported_and_nothing_is_written()
    {
        var args = JsonDocument.Parse("""
            {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:bad-1","source":{"externalId":"s","kind":"glucose-readings","title":"Readings","fetchedAt":"2026-10-28T12:00:00Z"},
             "measurements":[{"metric":"glucose","value":112,"unit":"bpm","observedAt":"2026-10-28T11:55:00Z","key":"a"},
                             {"metric":"Glucose","value":1,"unit":"mg/dL","observedAt":"2026-10-28T11:55:00","key":"b"},
                             {"metric":"steps","value":1,"unit":"count","observedAt":"2026-10-28T11:55:00Z","key":"c","extra":true}]}
            """).RootElement;
        var e = await Assert.ThrowsAsync<Ingest.Refused>(() => ingest.Measurements("cgm", args, default));
        Assert.Equal("invalid_arguments", e.Code);
        var paths = e.Issues.Select(i => (string)i["path"]!).ToArray();
        Assert.Contains("/measurements/0/unit", paths);
        Assert.Contains("/measurements/1/metric", paths);
        Assert.Contains("/measurements/1/observedAt", paths);
        Assert.Contains("/measurements/2/extra", paths);
        Assert.True(e.Issues.Count <= Ingest.MaxIssues);
    }

    [Fact] public async Task An_unconfigured_plugin_is_refused()
    {
        var e = await Assert.ThrowsAsync<Ingest.Refused>(() => ingest.Records("nobody", Fixture("ingest_records.example.json"), default));
        Assert.Equal("forbidden", e.Code);
    }

    [Fact] public void The_payload_hash_ignores_member_order()
    {
        var a = JsonDocument.Parse("""{"b":1,"a":{"y":2,"x":[1,{"q":1,"p":2}]}}""").RootElement;
        var b = JsonDocument.Parse("""{"a":{"x":[1,{"p":2,"q":1}],"y":2},"b":1}""").RootElement;
        Assert.Equal(Ingest.PayloadHash(a), Ingest.PayloadHash(b));
    }
}

public sealed class MigrationTests
{
    [Fact] public void Migrations_keep_their_recorded_names_and_order()
    {
        var names = Database.Migrations().Select(m => m.Version).ToArray();
        Assert.Equal(86, names.Length);
        Assert.Equal("Knowledge.Migrations.077_oauth.sql", names[76]);
        Assert.Equal("Knowledge.Migrations.078_capsule_clients.sql", names[77]);
        Assert.Equal("Knowledge.Migrations.079_work.sql", names[78]);
        Assert.Equal("Knowledge.Migrations.080_work_dependencies.sql", names[79]);
        Assert.Equal("Knowledge.Migrations.081_work_claims.sql", names[80]);
        Assert.Equal("Knowledge.Migrations.082_update_keeps_volatility.sql", names[81]);
        Assert.Equal("Knowledge.Migrations.083_memory_feeds.sql", names[82]);
        Assert.Equal("Knowledge.Migrations.084_work_dependency_counts.sql", names[83]);
        Assert.Equal("Knowledge.Migrations.085_memory_history_retention.sql", names[84]);
        Assert.Equal("Knowledge.Migrations.086_token_use_and_expiry.sql", names[85]);
        Assert.Equal("Knowledge.Migrations.001_foundation.sql", names[0]);
        Assert.Equal("Knowledge.Migrations.021_lexical_search.sql", names[20]);
        Assert.Equal("Knowledge.Migrations.022_composable_layouts.sql", names[21]);
        Assert.Equal("Knowledge.Migrations.023_scopes.sql", names[22]);
        Assert.Equal("Knowledge.Migrations.024_models.sql", names[23]);
        Assert.Equal("Knowledge.Migrations.025_memories.sql", names[24]);
        Assert.Equal("Knowledge.Migrations.026_passages.sql", names[25]);
        Assert.Equal("Knowledge.Migrations.027_derived_scopes.sql", names[26]);
        Assert.Equal("Knowledge.Migrations.028_viewer_sessions.sql", names[27]);
        Assert.Equal("Knowledge.Migrations.029_screen_binding.sql", names[28]);
        Assert.Equal("Knowledge.Migrations.030_person_corners.sql", names[29]);
        Assert.Equal("Knowledge.Migrations.031_document_scopes.sql", names[30]);
        Assert.Equal("Knowledge.Migrations.032_own_corners.sql", names[31]);
        Assert.Equal("Knowledge.Migrations.033_memstore.sql", names[32]);
        Assert.Equal("Knowledge.Migrations.034_memory_history.sql", names[33]);
        Assert.Equal("Knowledge.Migrations.035_memory_projects.sql", names[34]);
        Assert.Equal("Knowledge.Migrations.036_holds.sql", names[35]);
        Assert.Equal("Knowledge.Migrations.037_admin_audit.sql", names[36]);
        Assert.Equal("Knowledge.Migrations.038_memory_trash.sql", names[37]);
        Assert.Equal("Knowledge.Migrations.039_capsules.sql", names[38]);
        Assert.Equal("Knowledge.Migrations.040_keyword_text.sql", names[39]);
        Assert.Equal("Knowledge.Migrations.041_memory_undo.sql", names[40]);
        Assert.Equal("Knowledge.Migrations.042_recall_log.sql", names[41]);
        Assert.Equal("Knowledge.Migrations.043_export.sql", names[42]);
        Assert.Equal("Knowledge.Migrations.044_terminal_logins.sql", names[43]);
        Assert.Equal("Knowledge.Migrations.045_quarantine.sql", names[44]);
        Assert.Equal("Knowledge.Migrations.046_held_projects.sql", names[45]);
        Assert.Equal("Knowledge.Migrations.047_search_vector.sql", names[46]);
        Assert.Equal("Knowledge.Migrations.048_memory_share.sql", names[47]);
        Assert.Equal("Knowledge.Migrations.049_truth.sql", names[48]);
        Assert.Equal("Knowledge.Migrations.050_nightly_jobs.sql", names[49]);
        Assert.Equal("Knowledge.Migrations.051_typed_links.sql", names[50]);
        Assert.Equal("Knowledge.Migrations.052_made_links.sql", names[51]);
        Assert.Equal("Knowledge.Migrations.053_staleness.sql", names[52]);
        Assert.Equal("Knowledge.Migrations.054_digests.sql", names[53]);
        Assert.Equal("Knowledge.Migrations.055_citations.sql", names[54]);
        Assert.Equal("Knowledge.Migrations.056_link_upkeep.sql", names[55]);
        Assert.Equal("Knowledge.Migrations.057_kept_dismissals.sql", names[56]);
        Assert.Equal("Knowledge.Migrations.058_loops.sql", names[57]);
        Assert.Equal("Knowledge.Migrations.059_second_brain_import.sql", names[58]);
        Assert.Equal("Knowledge.Migrations.060_decisions.sql", names[59]);
        Assert.Equal("Knowledge.Migrations.061_due_dates.sql", names[60]);
        Assert.Equal("Knowledge.Migrations.062_links_on_save.sql", names[61]);
        Assert.Equal("Knowledge.Migrations.063_duplicates.sql", names[62]);
        Assert.Equal("Knowledge.Migrations.064_insight_candidates.sql", names[63]);
        Assert.Equal("Knowledge.Migrations.065_standing.sql", names[64]);
        Assert.Equal("Knowledge.Migrations.066_insight_stream.sql", names[65]);
        Assert.Equal("Knowledge.Migrations.067_capture_endpoint.sql", names[66]);
        Assert.Equal("Knowledge.Migrations.068_contradictions.sql", names[67]);
        Assert.Equal("Knowledge.Migrations.069_brief.sql", names[68]);
        Assert.Equal("Knowledge.Migrations.070_resurface.sql", names[69]);
        Assert.Equal("Knowledge.Migrations.071_resolve.sql", names[70]);
        Assert.Equal("Knowledge.Migrations.072_offboarding.sql", names[71]);
        Assert.Equal("Knowledge.Migrations.073_keep_copy_revisions.sql", names[72]);
        Assert.Equal("Knowledge.Migrations.074_household_import.sql", names[73]);
        Assert.Equal("Knowledge.Migrations.075_studio.sql", names[74]);
        Assert.Equal("Knowledge.Migrations.076_studio_search_path.sql", names[75]);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact] public void No_migration_names_a_person_or_a_home_time_zone()
    {
        foreach (var (name, sql) in Database.Migrations())
        {
            Assert.DoesNotContain("America/", sql);
            Assert.DoesNotContain("\r", sql);
            Assert.DoesNotContain("INSERT INTO subjects VALUES ('household','household'),('fixture','synthetic'),", sql);
        }
    }

    [Fact] public void The_compatibility_file_maps_versions_to_extra_checksums()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "# old checksums\nKnowledge.Migrations.001_foundation.sql " + new string('a', 64) + "\n");
        var map = Engine.Compatibility(path)!;
        Assert.Contains(new string('a', 64), map["Knowledge.Migrations.001_foundation.sql"]);
        File.WriteAllText(path, "Knowledge.Migrations.001_foundation.sql not-a-checksum\n");
        Assert.Throws<ConfigException>(() => Engine.Compatibility(path));
        File.Delete(path);
        Assert.Null(Engine.Compatibility(null));
    }
}

public sealed class AdapterRegistryTests
{
    // An adapter that keeps what Configure gave it. Its one provider is an archive.
    sealed class Kept(string name) : IAdapter
    {
        public string Name => name;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
            new Dictionary<string, ProviderPolicy> { [name + "-archive"] = new(SourceAuthority.AuthorizedArchive, "health") };
        public AdapterContext? Context { get; private set; }
        public void Configure(AdapterContext context) => Context = context;
    }

    [Fact] public void Adapters_are_configured_with_the_one_database_that_knows_their_providers()
    {
        // A database without an adapter's own policies would take its archive for a live feed ("unregistered means
        // live"): it would keep no originals and count in glucose selection as live.
        var config = TestSettings.Use();
        var kept = new Kept(Knowledge.Adapters.Demo.DemoAdapter.AdapterName);
        var off = new Kept("notes");
        var registry = AdapterRegistry.For(config, [() => kept, () => off]);
        Assert.Equal([kept], registry.Enabled);
        Assert.Null(kept.Context);
        var db = new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none"), registry.Policies(config));
        registry.Configure(config, db, new Archive(Path.GetTempPath()));
        Assert.Same(db, kept.Context!.Db);
        Assert.Equal(SourceAuthority.AuthorizedArchive, kept.Context.Db.Policies.For("demo-archive").Authority);
        Assert.Null(off.Context);
        Assert.Throws<ConfigException>(() => AdapterRegistry.For(config, [() => kept, () => new Kept("demo")]));
    }

    [Fact] public void Each_registry_decides_which_providers_its_map_shows()
    {
        // Two registries side by side, as two tests (or a later per-viewer map) would use them: the providers on the
        // map belong to a registry, not to the process, so neither changes what the other shows.
        TestSettings.Use();
        var notes = new AdapterRegistry([new Knowledge.Adapters.Notes.NotesAdapter()]);
        var memory = SyntheticRelationshipSource.New(Knowledge.Adapters.Notes.NotesAdapter.Provider, "memory");
        var star = SyntheticRelationshipSource.New(CatalogStars.Provider, "theme", CatalogStars.DataAccount("csv"));
        var off = SyntheticRelationshipSource.New("garmin", "memory");
        DiscoveryRecord[] records = [memory, star, off];
        Assert.Equal([memory.Key.GraphId, star.Key.GraphId], GraphProjection.Create(records, [], notes.Shows).nodes.Select(n => n.id));
        Assert.Equal([star.Key.GraphId], GraphProjection.Create(records, [], AdapterRegistry.Empty.Shows).nodes.Select(n => n.id));
        var view = new SnapshotView(records, [], new Dictionary<string, DateTimeOffset>());
        Assert.Equal(2, new GraphExplorer(view, notes.Shows).Explore(null, null, 1, 10).Stars.Count);
        Assert.Single(new GraphExplorer(view, AdapterRegistry.Empty.Shows).Explore(null, null, 1, 10).Stars);
    }

    [Fact] public void Settings_once_loaded_are_never_replaced()
    {
        var config = TestSettings.Use();
        RhythmConfig.Current = config;
        Assert.Throws<InvalidOperationException>(() => RhythmConfig.Current = RhythmConfig.Parse(TestSettings.Yaml));
        Assert.Same(config, RhythmConfig.Current);
        // The catalog's provider id is read once, from RHYTHM_CATALOG_PROVIDER, and nothing can change it after.
        Assert.Null(typeof(CatalogStars).GetProperty(nameof(CatalogStars.Provider))!.SetMethod);
    }
}

public sealed class CatalogTests
{
    [Fact] public void Common_metrics_of_any_provider_group_into_named_data_stars()
    {
        var config = TestSettings.Use();
        var catalog = RelationshipCatalog.For(config);
        Assert.Contains(RhythmConfig.Synthetic, catalog.Subjects);
        Assert.Equal(("glucose", "Glucose"), catalog.Group("cgm", "glucose"));
        Assert.Equal(("activity", "Activity"), catalog.Group("tracker", "steps"));
        Assert.Equal(("mood", "Mood"), catalog.Group("tracker", "mood_score"));
        Assert.Equal("other", catalog.Group("tracker", "water_glasses").Id);
        var now = DateTimeOffset.UtcNow;
        var inventory = new CatalogInventory([new("alex", "cgm", "private", "glucose", "mg/dL", 10, new(2026, 10, 1), new(2026, 10, 2), now.AddHours(-1))], []);
        var note = new DiscoveryRecord(new("alex", "notes-source", "notes", "n1"), "private", "note", "Glucose after a run", null, "1", now, now, []);
        var star = Assert.Single(CatalogStars.Records(catalog, inventory, new HashSet<string>(), now, (_, _, _, _) => null, [note]));
        Assert.Equal("Alex · Glucose", star.Title);
        Assert.Equal(note.Key, Assert.Single(star.References).Target);
        Assert.True(CatalogStars.IsData(star.Key));
        Assert.StartsWith(CatalogStars.Provider + ":", star.Key.GraphId);
    }

    [Fact] public void Settings_add_groups_and_provider_labels_before_the_built_in_ones()
    {
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\n" + """
            graph:
              catalog:
                groups:
                  - { id: training, label: Training, provider: tracker, metrics: [activity_*, vo2_max], topics: [run, swim] }
                  - { id: glucose, label: Blood sugar, metrics: [glucose, sensor_glucose] }
                providers: { tracker: Wrist tracker }
            """);
        var catalog = RelationshipCatalog.For(config);
        // A provider's own group wins over the built-in ones; its other metrics still fall into those.
        Assert.Equal(("training", "Training"), catalog.Group("tracker", "activity_distance"));
        Assert.Equal(("activity", "Activity"), catalog.Group("tracker", "steps"));
        Assert.Equal(("activity", "Activity"), catalog.Group("other-tracker", "activity_distance"));
        Assert.Equal(["run", "swim"], catalog.Topics("tracker", "training"));
        // A group with a built-in group's id and provider replaces it.
        Assert.Equal(("glucose", "Blood sugar"), catalog.Group("cgm", "glucose"));
        Assert.Single(catalog.Groups!, g => g.Id == "glucose");
        Assert.Equal(("Wrist tracker", "CSV import"), (catalog.ProviderLabel("tracker"), catalog.ProviderLabel("csv")));
        Assert.Throws<ConfigException>(() => RhythmConfig.Parse(TestSettings.Yaml + "\ngraph: { catalog: { groups: [{ id: other, label: Other, metrics: [x] }] } }\n"));
        Assert.Throws<ConfigException>(() => RhythmConfig.Parse(TestSettings.Yaml + "\ngraph: { catalog: { groups: [{ id: sleep, label: Sleep, metrics: [] }] } }\n"));
    }
}
