using System.Net;
using System.Text.Json;
using Knowledge;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Knowledge.Tests;

// The engine's door (Core/Mcp/Perimeter.cs, the middleware Program puts before every route) on an in-memory server.
// Fake tokens stand in for the principals table and fake routes for the endpoints, so each access rule is checked
// without a database: what is refused, with which status, and which endpoint a call reaches.
public sealed class PerimeterTests
{
    static readonly Caller App = new(Guid.NewGuid(), "app", "app", null), Plugin = new(Guid.NewGuid(), "plugin:cgm", "ingest", "cgm"),
        Assistant = new(Guid.NewGuid(), "assistant", "harness", null), Older = new(Guid.NewGuid(), "older", "legacy", null);

    sealed class Door : IAsyncDisposable
    {
        public readonly List<string> Reached = [];
        public readonly List<string> Asked = [];
        public readonly List<Guid?> Principals = [];
        public volatile bool Ready = true;
        WebApplication app = null!;
        HttpClient client = null!;

        public static Task<Door> Open(params string[] hosts) =>
            Open(new RhythmConfig.EngineSection { AllowedHosts = hosts.Length == 0 ? ["engine"] : [.. hosts] });

        public static async Task<Door> Open(RhythmConfig.EngineSection settings)
        {
            var door = new Door();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var callers = new Dictionary<string, Caller>(StringComparer.Ordinal)
                { ["app-key"] = App, ["plugin-key"] = Plugin, ["assistant-key"] = Assistant, ["older-key"] = Older };
            app.Use(new Perimeter(settings.AllowedHosts, () => door.Ready, (token, _) =>
            {
                lock (door.Asked) door.Asked.Add(token);
                return Task.FromResult(callers.GetValueOrDefault(token));
            }, new(App: (c, _) => door.Reach(c, "app"), Ingest: (c, _) => door.Reach(c, "ingest"),
                Document: (c, _, id) => door.Reach(c, "document " + id)), hostAcceptsRole: settings.AcceptsRole).Handle);
            app.MapGet("/health", (HttpContext c) => door.Reach(c, "health"));
            app.MapMethods("/mcp", ["GET", "POST", "DELETE"], (HttpContext c) => door.Reach(c, "harness"));
            await app.StartAsync();
            door.app = app;
            door.client = app.GetTestClient();
            return door;
        }

        Task Reach(HttpContext context, string where)
        {
            lock (Reached) { Reached.Add(where); Principals.Add(context.Items["principal"] as Guid?); }
            return context.Response.WriteAsync(where);
        }

        // One request; the token goes in as "Bearer <token>", or `authorization` replaces the whole header.
        public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Send(string method, string url,
            string? token = null, string? authorization = null, string? origin = null, string? forwardedHost = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if ((authorization ?? (token is null ? null : "Bearer " + token)) is { } header) request.Headers.TryAddWithoutValidation("Authorization", header);
            if (forwardedHost is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (method == "POST") request.Content = new StringContent("{}");
            var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
        }

        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }

    static string Code(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString()!;

    [Fact] public async Task A_browser_call_is_refused_before_anything_else_and_nothing_is_cached()
    {
        await using var door = await Door.Open();
        var health = await door.Send("GET", "http://engine/health", origin: "https://example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, health.Status);
        var call = await door.Send("POST", "http://engine/mcp", "app-key", origin: "https://example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, call.Status);
        Assert.Empty(door.Reached);
        Assert.Empty(door.Asked);
        Assert.Equal("no-store", call.Response.Headers.CacheControl?.ToString());
        Assert.Equal("no-store", (await door.Send("GET", "http://engine/health")).Response.Headers.CacheControl?.ToString());
    }

    [Fact] public async Task Only_the_configured_host_names_get_in()
    {
        await using (var door = await Door.Open())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("GET", "http://elsewhere.example.invalid/health")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("POST", "http://elsewhere.example.invalid/mcp", "app-key")).Status);
            // A bare name admits any port; case does not matter.
            Assert.Equal(HttpStatusCode.OK, (await door.Send("GET", "http://ENGINE:8080/health")).Status);
            Assert.Equal(["health"], door.Reached);
            Assert.Empty(door.Asked);
        }
        await using (var door = await Door.Open("engine:8181"))
        {
            Assert.Equal(HttpStatusCode.OK, (await door.Send("GET", "http://engine:8181/health")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("GET", "http://engine:8080/health")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("GET", "http://engine/health")).Status);
        }
    }

    const string PublicHost = "rhythm-mcp.example.invalid";
    static RhythmConfig WithHostRules(string rules) => RhythmConfig.Parse($$"""
        version: 1
        timezone: UTC
        people: [{ id: alex, name: Alex }]
        engine:
          allowedHosts: [engine, {{PublicHost}}, "{{PublicHost}}.", admin.example.invalid:8181]
          hostRoles: {{rules}}
        """);

    [Fact] public async Task Public_host_accepts_harness_only_across_allowed_ports_and_keeps_internal_roles()
    {
        var config = WithHostRules("[{ host: RHYTHM-MCP.EXAMPLE.INVALID, roles: [harness] }]");
        await using var door = await Door.Open(config.Engine);
        foreach (var host in new[] { PublicHost, PublicHost + ":8181", PublicHost + "." })
        {
            Assert.Equal("harness", (await door.Send("POST", $"http://{host}/mcp", "assistant-key")).Body);
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("POST", $"http://{host}/mcp", "plugin-key")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("POST", $"http://{host}/mcp", "app-key", forwardedHost: "engine")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await door.Send("GET", $"http://{host}/app/documents/{Guid.NewGuid()}", "app-key")).Status);
        }
        Assert.Equal("app", (await door.Send("POST", "http://engine/mcp", "app-key")).Body);
        Assert.Equal("ingest", (await door.Send("POST", "http://engine/mcp", "plugin-key")).Body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await door.Send("POST", $"http://{PublicHost}/mcp", "unknown")).Status);
        var portScoped = WithHostRules("[{ host: admin.example.invalid, roles: [app, ingest] }]");
        await using var privateDoor = await Door.Open(portScoped.Engine);
        Assert.Equal(HttpStatusCode.Forbidden, (await privateDoor.Send("POST", "http://admin.example.invalid:8181/mcp", "assistant-key")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await privateDoor.Send("POST", "http://admin.example.invalid:8182/mcp", "app-key")).Status);
    }

    [Theory]
    [InlineData("[{ host: other.example.invalid, roles: [harness] }]")]
    [InlineData("[{ host: engine:8080, roles: [harness] }]")]
    [InlineData("[{ host: engine, roles: [] }]")]
    [InlineData("[{ host: engine, roles: [unknown] }]")]
    [InlineData("[{ host: engine, roles: [harness, harness] }]")]
    [InlineData("[{ host: engine, roles: [app] }, { host: ENGINE, roles: [harness] }]")]
    public void Invalid_host_role_settings_are_refused_at_startup(string rules) =>
        Assert.Contains("engine.hostRoles", Assert.Throws<ConfigException>(() => WithHostRules(rules)).Message);

    [Fact] public async Task Health_needs_no_token_and_any_other_path_is_not_found()
    {
        await using var door = await Door.Open();
        Assert.Equal((HttpStatusCode.OK, "health"), ((await door.Send("GET", "http://engine/health")).Status, door.Reached.Single()));
        foreach (var path in new[] { "/", "/other", "/mcp/more", "/app/documents/", "/app/documents/not-an-id", $"/app/documents/{Guid.NewGuid()}/more" })
            Assert.Equal(HttpStatusCode.NotFound, (await door.Send("POST", "http://engine" + path, "app-key")).Status);
        Assert.Single(door.Reached);
        Assert.Empty(door.Asked);
    }

    [Fact] public async Task Only_health_answers_until_the_engine_is_ready()
    {
        await using var door = await Door.Open();
        door.Ready = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await door.Send("POST", "http://engine/mcp", "app-key")).Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await door.Send("GET", $"http://engine/app/documents/{Guid.NewGuid()}", "app-key")).Status);
        Assert.Empty(door.Asked);
        Assert.Equal(HttpStatusCode.OK, (await door.Send("GET", "http://engine/health")).Status);
        door.Ready = true;
        Assert.Equal(HttpStatusCode.OK, (await door.Send("POST", "http://engine/mcp", "app-key")).Status);
    }

    [Fact] public async Task A_missing_malformed_long_or_unknown_token_is_401_with_a_json_rpc_body()
    {
        await using var door = await Door.Open();
        var refusals = new[]
        {
            await door.Send("POST", "http://engine/mcp"),
            await door.Send("POST", "http://engine/mcp", authorization: "Basic app-key"),
            await door.Send("POST", "http://engine/mcp", authorization: "bearer app-key"),
            // 601 characters in all: refused without a lookup.
            await door.Send("POST", "http://engine/mcp", authorization: "Bearer " + new string('k', Perimeter.MaxAuthorization - 6)),
        };
        Assert.All(refusals, r => Assert.Equal((HttpStatusCode.Unauthorized, "unauthorized"), (r.Status, Code(r.Body))));
        Assert.Empty(door.Asked);
        // Exactly 600 is looked up, and an unknown token is refused the same way.
        var longest = new string('k', Perimeter.MaxAuthorization - 7);
        var unknown = await door.Send("POST", "http://engine/mcp", longest);
        Assert.Equal((HttpStatusCode.Unauthorized, "unauthorized"), (unknown.Status, Code(unknown.Body)));
        Assert.Equal([longest], door.Asked);
        Assert.Empty(door.Reached);
    }

    [Fact] public async Task Each_role_reaches_only_its_own_endpoint_with_its_principal()
    {
        await using var door = await Door.Open();
        Assert.Equal("app", (await door.Send("POST", "http://engine/mcp", "app-key")).Body);
        Assert.Equal("ingest", (await door.Send("POST", "http://engine/mcp", "plugin-key")).Body);
        Assert.Equal("harness", (await door.Send("POST", "http://engine/mcp", "assistant-key")).Body);
        Assert.Equal([App.Id, Plugin.Id, Assistant.Id], door.Principals);
        // A principal from before roles reaches no endpoint.
        var older = await door.Send("POST", "http://engine/mcp", "older-key");
        Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (older.Status, Code(older.Body)));
        Assert.Equal(3, door.Reached.Count);
    }

    [Fact] public async Task Only_the_harness_may_use_another_method_than_POST_on_mcp()
    {
        await using var door = await Door.Open();
        foreach (var method in new[] { "GET", "DELETE" })
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await door.Send(method, "http://engine/mcp", "app-key")).Status);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await door.Send(method, "http://engine/mcp", "plugin-key")).Status);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await door.Send(method, "http://engine/mcp", "older-key")).Status);
            Assert.Equal("harness", (await door.Send(method, "http://engine/mcp", "assistant-key")).Body);
        }
        Assert.Equal(["harness", "harness"], door.Reached);
    }

    [Fact] public async Task A_document_original_is_the_apps_alone()
    {
        await using var door = await Door.Open();
        var id = Guid.NewGuid();
        Assert.Equal($"document {id}", (await door.Send("GET", $"http://engine/app/documents/{id}", "app-key")).Body);
        foreach (var token in new[] { "assistant-key", "plugin-key", "older-key" })
        {
            var refused = await door.Send("GET", $"http://engine/app/documents/{id}", token);
            Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (refused.Status, Code(refused.Body)));
        }
        Assert.Equal([App.Id], door.Principals);
    }

    [Fact] public async Task The_document_route_answers_only_GET()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE" })
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            // Refused before the database or the archive is touched.
            await AppFiles.Handle(context, Guid.NewGuid(), Guid.NewGuid(), null!, null!, null!);
            Assert.Equal((405, "GET"), (context.Response.StatusCode, context.Response.Headers.Allow.ToString()));
        }
    }
}
