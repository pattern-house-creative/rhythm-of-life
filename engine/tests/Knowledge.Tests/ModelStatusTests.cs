using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Titles;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// Fictional notes of one person whose titles are still their excerpts, for the titles model.
internal sealed class UntitledNotes(int count) : IMemorySource
{
    public string Name => "untitled";
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { ["untitled"] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct) => Task.FromResult(new SourceListing([.. Enumerable.Range(0, count).Select(i =>
        new DiscoveryRecord(new("sam", "untitled", "untitled", "m" + i), "private", "memory", $"DEMO fictional note {i}", null, "r" + i, null, DateTimeOffset.UtcNow, [],
            Overview: $"DEMO fictional note {i}"))], true));
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
}

// SB-P2-01: every model's job and state in /health, app_status and rhythm doctor, and a titles failure noted by its kind.
public sealed class ModelStatusTests
{
    // A database nobody answers at: the counts stay unread, as on an engine whose first count has not finished.
    static Database Nowhere() => new(NpgsqlDataSource.Create("Host=example.invalid;Database=none;Timeout=1"));
    static RhythmConfig Settings(string extra = "") => RhythmConfig.Parse(TestSettings.Yaml + "\n" + extra);

    static TitlesAdapter Titles(HttpMessageHandler handler)
    {
        var titles = new TitlesAdapter(handler);
        titles.Configure(new(new AdapterSettings("titles", new Dictionary<string, object?> { ["enabled"] = "true", ["model"] = "fictional-model:1b" }),
            TestSettings.Use(), Nowhere(), new Archive(Path.GetTempPath())));
        return titles;
    }

    [Fact] public async Task Health_keeps_its_fields_and_adds_the_version_the_schema_and_each_model()
    {
        var config = Settings();
        var registry = new AdapterRegistry([new Adapters.Demo.DemoAdapter()]);
        var health = await new ModelStatus(Nowhere(), config, new Embeddings(), registry).Health(default);
        // What /health always said, in the same places.
        Assert.Equal("ok", (string?)health["status"]);
        Assert.Equal("off", (string?)health["embeddings"]);
        Assert.Equal(["demo"], health["adapters"]!.AsArray().Select(a => (string?)a));
        // The engine's version as MCP's serverInfo gives it, and the newest migration this build carries.
        Assert.Equal(typeof(JsonRpc).Assembly.GetName().Version!.ToString(3), (string?)health["version"]);
        Assert.Equal(Database.Migrations().Max(m => int.Parse(m.Version["Knowledge.Migrations.".Length..][..3])), (int)health["schema"]!);
        var models = health["models"]!.AsArray();
        Assert.Equal(2, models.Count);
        // The settings turn MiniLM on and nothing has loaded it yet: starting, its counts not read.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            {"job":"embeddings","role":"live","name":"minilm","id":"{{EmbeddingModels.MiniLmId}}","state":"starting","index":null,"failure":null}
            """), models[0]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"job":"titles","role":"live","name":null,"id":null,"state":"off","index":null,"failure":null}
            """), models[1]));
        // app_status gives the app the same, without the adapters.
        var app = await new ModelStatus(Nowhere(), config, new Embeddings(), registry).App(default);
        Assert.Equal(["version", "schema", "models"], app.Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(models, app["models"]));
        Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_status.output.schema.json", app));
    }

    [Fact] public async Task Embeddings_off_say_off_and_a_shadow_model_is_listed_beside_the_live_one()
    {
        var off = Settings("adapters:\n  embeddings: { enabled: false }\n");
        var models = await new ModelStatus(Nowhere(), off, new Embeddings(), AdapterRegistry.Empty).Models(default);
        Assert.Equal("off", (string?)models[0]!["state"]);
        var shadow = Settings("adapters:\n  embeddings: { enabled: true, shadow: bge-small }\n");
        models = await new ModelStatus(Nowhere(), shadow, new Embeddings(), AdapterRegistry.Empty).Models(default);
        Assert.Equal(["live", "shadow", "live"], models.Select(m => (string?)m!["role"]));
        Assert.Equal(EmbeddingModels.BgeSmall.Id, (string?)models[1]!["id"]);
        Assert.Equal("starting", (string?)models[1]!["state"]);
    }

    // A titles service that fails, or answers, in one fixed way.
    sealed class Service(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(answer()); }
    }
    static HttpResponseMessage Json(string response) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { response, done = true }), Encoding.UTF8, "application/json") };

    public static TheoryData<string, string> Failures => new()
    {
        { "down", "http-503" }, { "missing", "http-404" }, { "unreachable", "unreachable" }, { "timeout", "timeout" },
        { "large", "too-large" }, { "garbled", "unreadable" },
    };

    [Theory, MemberData(nameof(Failures))]
    public async Task A_titles_failure_stops_the_pass_and_is_noted_by_its_kind(string how, string kind)
    {
        var service = new Service(() => how switch
        {
            "down" => new(HttpStatusCode.ServiceUnavailable),
            "missing" => new(HttpStatusCode.NotFound),
            "unreachable" => throw new HttpRequestException("No connection could be made"),
            "timeout" => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            "large" => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70_000), Encoding.UTF8, "application/json") },
            _ => new(HttpStatusCode.OK) { Content = new StringContent("not json", Encoding.UTF8, "application/json") },
        });
        var config = TestSettings.Use();
        var registry = new AdapterRegistry([new UntitledNotes(3), Titles(service)]);
        var principal = Guid.NewGuid();
        var pass = new CatalogRelationshipSource(RelationshipHostConfiguration.From(config, principal, Path.GetTempPath()), registry,
            (_, _) => throw new InvalidOperationException("no catalog in this test"));
        var records = (await pass.Fetch(principal, default)).Records;
        // The first failure stops the pass asking; every star keeps its excerpt.
        Assert.Equal(1, service.Calls);
        Assert.All(records.Where(r => r.Key.Provider == "untitled"), r => Assert.Equal(r.Overview, r.Title));
        Assert.Equal(new TitlePass(1, 0, 0, kind), pass.Titles);
        var note = Assert.Single(pass.Notes, n => n.StartsWith("titles", StringComparison.Ordinal));
        Assert.Contains($": {kind}", note);
        Assert.DoesNotContain("DEMO", note);
    }

    [Fact] public async Task Unusable_answers_are_counted_and_good_ones_titled()
    {
        var answers = new Queue<string>(["<b>unusable</b>", "Allotment rota", "   "]);
        var service = new Service(() => Json(answers.Dequeue()));
        var config = TestSettings.Use();
        var registry = new AdapterRegistry([new UntitledNotes(3), Titles(service)]);
        var principal = Guid.NewGuid();
        var pass = new CatalogRelationshipSource(RelationshipHostConfiguration.From(config, principal, Path.GetTempPath()), registry,
            (_, _) => throw new InvalidOperationException("no catalog in this test"));
        await pass.Fetch(principal, default);
        Assert.Equal(new TitlePass(3, 1, 2, null), pass.Titles);
        Assert.Equal("titles: 2 unusable answers, their excerpts kept", Assert.Single(pass.Notes, n => n.StartsWith("titles", StringComparison.Ordinal)));
        // Without a titles model there is nothing to count.
        var none = new CatalogRelationshipSource(RelationshipHostConfiguration.From(config, principal, Path.GetTempPath()), new AdapterRegistry([new UntitledNotes(1)]),
            (_, _) => throw new InvalidOperationException("no catalog in this test"));
        await none.Fetch(principal, default);
        Assert.Null(none.Titles);
    }

    [Fact] public async Task The_app_reads_the_status_with_app_status_and_nothing_else_is_taken()
    {
        var config = TestSettings.Use();
        var status = new ModelStatus(Nowhere(), config, new Embeddings(), AdapterRegistry.Empty);
        async Task<(int Status, JsonElement Body)> Call(string args)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(status).BuildServiceProvider() };
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"tools/call\",\"params\":{\"name\":\"app_status\",\"arguments\":" + args + "}}"));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(Guid.NewGuid(), "app", "app", null), null!, new AppHealth(Nowhere(), config), AdapterRegistry.Empty);
            return (context.Response.StatusCode, JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray()).RootElement.Clone());
        }
        var (code, body) = await Call("{}");
        Assert.Equal(200, code);
        var content = JsonNode.Parse(body.GetProperty("result").GetProperty("structuredContent").GetRawText())!;
        Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_status.output.schema.json", content));
        Assert.Equal(ModelStatus.Version, (string?)content["version"]);
        Assert.Equal(400, (await Call("""{"verbose":true}""")).Status);
        Assert.True(AppEndpoint.IsCore("app_status"));
    }
}

// The index counts and the titles state from a real database: passages per model, whether the index job covers a model
// and the rows it has left, and a titles model that failed on one rebuild and titled on the next. In the shared collection,
// so its database is made one at a time with others there: a whole run's fixtures migrating at once can exhaust
// Postgres's lock table.
[Collection(SharedStateCollection.Name)]
public sealed class ModelStatusDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    sealed class Ollama : HttpMessageHandler
    {
        public bool Down = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Down
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new(HttpStatusCode.OK) { Content = new StringContent("""{"response":"Allotment rota","done":true}""", Encoding.UTF8, "application/json") });
    }

    [PostgresFact] public async Task Status_counts_each_models_passages_and_rows_left_and_follows_the_titles_model()
    {
        var (_, _, order) = await SavedDocuments.Save(fixture.Source, "status", 4);
        // MiniLM's id on a stand-in, so the passages count as the live model's; two of four rows indexed.
        var live = new StandInModel(EmbeddingModels.MiniLmId, 0);
        Assert.Equal(2, (await new EmbeddingBackfill(fixture.Db).Step(live, default, rows: 2)).Indexed);

        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings: { enabled: true, backfill: true }\n  titles: { enabled: true, model: fictional-model:1b }\n");
        var ollama = new Ollama();
        var titles = new TitlesAdapter(ollama);
        titles.Configure(new(config.Adapter("titles"), config, fixture.Db, new Archive(Path.GetTempPath())));
        var registry = new AdapterRegistry([new UntitledNotes(2), titles]);
        var db = new Database(fixture.Source, new AdapterRegistry([new Adapters.Demo.DemoAdapter(), new UntitledNotes(0)]).Policies(config));
        var state = Path.Combine(Path.GetTempPath(), "rhythm-status-" + Guid.NewGuid().ToString("N")[..10]);
        var principal = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, principal, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        try
        {
            var status = new ModelStatus(db, config, new Embeddings(), registry, host);
            var models = await status.Models(default, wait: true);
            var index = models[0]!["index"]!;
            Assert.Equal(10L, (long)index["passages"]!);
            Assert.True((bool)index["indexing"]!);
            Assert.Equal(2L, (long)index["pending"]!);
            Assert.Null(index["pausedUntil"]);
            // Before any rebuild the titles model is on, and named.
            Assert.Equal(("on", "fictional-model:1b"), ((string?)models[1]!["state"], (string?)models[1]!["name"]));

            // A rebuild that asks the titles service while it is down: failing, by kind, and the rebuild's note says so.
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Contains(host.Notes, n => n.StartsWith("titles stopped after 1 asked, 0 titled: http-503", StringComparison.Ordinal));
            var titled = (await status.Models(default))[1]!;
            Assert.Equal(("failing", "http-503"), ((string?)titled["state"], (string?)titled["failure"]!["kind"]));
            Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_status.output.schema.json", await status.App(default)));
            // The engine started again (a new host over the same state folder) says so too, before any rebuild.
            RelationshipHost Restarted() => new(db, () => RelationshipHostConfiguration.From(config, principal, state),
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
            var again = (await new ModelStatus(db, config, new Embeddings(), registry, Restarted()).Models(default))[1]!;
            Assert.Equal(("failing", "http-503", (string?)titled["failure"]!["at"]),
                ((string?)again["state"], (string?)again["failure"]!["kind"], (string?)again["failure"]!["at"]));
            // The next rebuild titles again: on, the failure gone, after a restart too.
            ollama.Down = false;
            Assert.Equal("complete", await host.Refresh(false, default));
            titled = (await status.Models(default))[1]!;
            Assert.Equal("on", (string?)titled["state"]);
            Assert.Null(titled["failure"]);
            Assert.Null(Restarted().TitleFailure);

            // With the backfill off (every existing install), the job covers nothing and no rows are counted as left.
            var plain = RhythmConfig.Parse(TestSettings.Yaml);
            var quiet = (await new ModelStatus(fixture.Db, plain, new Embeddings(), AdapterRegistry.Empty).Models(default, wait: true))[0]!["index"]!;
            Assert.Equal((10L, false), ((long)quiet["passages"]!, (bool)quiet["indexing"]!));
            Assert.Null(quiet["pending"]);
            Assert.Equal(4, order.Length);

            // With memory scopes on the counts, every scope's rows together, are left out for every caller (memory.md
            // 6.2): /health and app_status answer index null, as before a first count, and the states as before.
            await Scoped.Mode(fixture.Source, "on");
            try
            {
                var scoped = new ModelStatus(db, config, new Embeddings(), registry, host);
                Assert.Null((await scoped.Models(default, wait: true))[0]!["index"]);
                var health = await scoped.Health(default, wait: true);
                Assert.Null(health["models"]![0]!["index"]);
                Assert.Equal("on", (string?)health["models"]![1]!["state"]);
                Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_status.output.schema.json", await scoped.App(default, wait: true)));
            }
            finally { await Scoped.Mode(fixture.Source, "off"); }

            // The app role lists app_status, with no arguments, beside its other core tools (the listing takes its caller
            // since SB-P1-04, for the glucose tools only).
            var tools = JsonSerializer.SerializeToNode(await AppEndpoint.Tools(new AppHealth(db, config), principal, default))!.AsArray();
            var listed = tools.Single(t => (string?)t!["name"] == "app_status")!;
            Assert.Empty(listed["inputSchema"]!["properties"]!.AsObject());
            Assert.True((bool)listed["annotations"]!["readOnlyHint"]!);
        }
        finally
        {
            TestSettings.Use();
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }
}
