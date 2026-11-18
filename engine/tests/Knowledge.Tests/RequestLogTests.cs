using System.Net;
using System.Text;
using System.Text.Json;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knowledge.Tests;

// The lines a RequestLog writes, as the console shows them, and whether any carried an exception.
public sealed class LogLines : ILogger<RequestLog>
{
    public readonly List<string> Lines = [];
    public bool Exceptions { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) { Lines.Add(formatter(state, exception)); Exceptions |= exception is not null; }
    }
}

// A failed or refused request leaves one line in the engine's log (Core/Mcp/RequestLog.cs): fixed fields only.
public sealed class RequestLogTests
{
    [Fact] public void The_same_line_is_written_once_a_minute_then_says_how_many_more_there_were()
    {
        var log = new LogLines();
        var now = DateTimeOffset.Parse("2026-01-05T09:00:00Z");
        var requests = new RequestLog(log, () => now);
        for (var i = 0; i < 5; i++) requests.Refused(401, "unknown_token");
        requests.Refused(403, "host_not_allowed");
        Assert.Equal(["Request refused: 401 unknown_token", "Request refused: 403 host_not_allowed"], log.Lines);
        now += RequestLog.Window;
        requests.Refused(401, "unknown_token");
        requests.Refused(401, "unknown_token");
        Assert.Equal("Request refused: 401 unknown_token; 4 more since the last such line", log.Lines[^1]);
        Assert.Equal(3, log.Lines.Count);
    }

    [Fact] public void A_failure_names_what_threw_by_its_type_and_never_its_message()
    {
        var log = new LogLines();
        new RequestLog(log).Failed("app", AppEndpoint.Graph, 503, "unavailable", new TimeoutException("fictional detail"));
        Assert.Equal("Call failed: app app_graph, 503 unavailable (TimeoutException)", Assert.Single(log.Lines));
        Assert.False(log.Exceptions);
    }

    static DefaultHttpContext Context(LogLines log, object request)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(new RequestLog(log)).BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request));
        context.Response.Body = new MemoryStream();
        return context;
    }

    // One app call against a map whose settings throw, or are missing (not ready, or not this caller's).
    static async Task<(int Status, JsonElement Body)> Call(Func<RelationshipHostConfiguration?> configuration, LogLines log, string tool = AppEndpoint.Graph)
    {
        var context = Context(log, new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments = new { } } });
        var host = new RelationshipHost(null!, configuration, null!);
        await AppEndpoint.Handle(context, new Caller(Guid.NewGuid(), "app", "app", null), host, new AppHealth(null!, TestSettings.Use()), AdapterRegistry.Empty);
        using var body = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        return (context.Response.StatusCode, body.RootElement.Clone());
    }

    static string Code(JsonElement body) => body.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString()!;

    [Fact] public async Task An_app_call_that_throws_gets_the_documented_error_body_and_a_line_in_the_log()
    {
        var log = new LogLines();
        // The database or a source not answering: 503 with the request's id, and the log says which tool and what threw.
        var failed = await Call(() => throw new TimeoutException("fictional detail"), log);
        Assert.Equal((503, "unavailable", 7), (failed.Status, Code(failed.Body), failed.Body.GetProperty("id").GetInt32()));
        Assert.True(failed.Body.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("Call failed: app app_graph, 503 unavailable (TimeoutException)", log.Lines[^1]);
        // The map not readable now: 403, logged under the tool an alias stands for.
        var refused = await Call(() => null, log, "get_relationship_graph");
        Assert.Equal((403, "forbidden"), (refused.Status, Code(refused.Body)));
        Assert.Equal("Call failed: app app_graph, 403 forbidden (UnauthorizedAccessException)", log.Lines[^1]);
        // A tool the app role does not have is named as such, never by the name the caller sent.
        var unknown = await Call(() => null, log, "fictional_tool");
        Assert.Equal((403, "forbidden"), (unknown.Status, Code(unknown.Body)));
        Assert.Equal("Call failed: app unknown_tool, 403 forbidden", log.Lines[^1]);
        Assert.Equal(3, log.Lines.Count);
        Assert.DoesNotContain(log.Lines, l => l.Contains("fictional", StringComparison.Ordinal));
        Assert.False(log.Exceptions);
    }

    [Fact] public async Task A_plugin_that_is_off_and_a_call_that_is_not_ingest_are_logged()
    {
        var log = new LogLines();
        var call = new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "app_graph", arguments = new { } } };
        async Task<int> Ingest(string plugin)
        {
            var context = Context(log, call);
            await IngestEndpoint.Handle(context, new Caller(Guid.NewGuid(), "plugin:" + plugin, "ingest", plugin), null!, TestSettings.Use());
            return context.Response.StatusCode;
        }
        Assert.Equal(403, await Ingest("off"));
        Assert.Equal("Request refused: 403 plugin_off (role ingest)", log.Lines[^1]);
        Assert.Equal(403, await Ingest("cgm"));
        Assert.Equal("Call failed: ingest unknown_tool, 403 forbidden", log.Lines[^1]);
    }

    static async Task<HttpResponseMessage> Send(HttpClient client, string method, string url, string? authorization = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (method == "POST") request.Content = new StringContent("{}");
        return await client.SendAsync(request);
    }

    [Fact] public async Task Each_refusal_at_the_door_is_one_line_naming_why_and_never_the_token()
    {
        var log = new LogLines();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var older = new Caller(Guid.NewGuid(), "older", "legacy", null);
        var ready = false;
        app.Use(new Perimeter(["engine"], () => ready, (token, _) => Task.FromResult(token == "older-key" ? older : null),
            new((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask), new RequestLog(log)).Handle);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var secret = "Bearer fictional-" + "token-value";
        var refusals = new (Func<Task<HttpResponseMessage>> Send, HttpStatusCode Status, string Line)[]
        {
            (() => Send(client, "POST", "http://engine/mcp", secret, "https://example.invalid"), HttpStatusCode.Forbidden, "Request refused: 403 origin_present"),
            (() => Send(client, "POST", "http://elsewhere.example.invalid/mcp", secret), HttpStatusCode.Forbidden, "Request refused: 403 host_not_allowed"),
            (() => Send(client, "POST", "http://engine/other", secret), HttpStatusCode.NotFound, "Request refused: 404 not_found"),
            (() => Send(client, "POST", "http://engine/mcp", secret), HttpStatusCode.ServiceUnavailable, "Request refused: 503 not_ready"),
        };
        foreach (var (send, status, line) in refusals)
        {
            var response = await send();
            Assert.Equal(status, response.StatusCode);
            // The documented JSON-RPC error body, whatever refused it.
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonRpc.Code((int)status), Code(body.RootElement));
            Assert.Equal(line, log.Lines[^1]);
        }
        ready = true;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "POST", "http://engine/mcp", "Basic fictional")).StatusCode);
        Assert.Equal("Request refused: 401 no_token", log.Lines[^1]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "POST", "http://engine/mcp", secret)).StatusCode);
        Assert.Equal("Request refused: 401 unknown_token", log.Lines[^1]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "POST", "http://engine/mcp", "Bearer older-key")).StatusCode);
        Assert.Equal("Request refused: 403 wrong_role (role legacy)", log.Lines[^1]);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(client, "GET", "http://engine/mcp", "Bearer older-key")).StatusCode);
        Assert.Equal("Request refused: 405 method_not_allowed (role legacy)", log.Lines[^1]);
        Assert.Equal(8, log.Lines.Count(l => l.StartsWith("Request refused:", StringComparison.Ordinal)));
        Assert.Equal(2, log.Lines.Count(l => l == $"Authenticated call: {older.Id} legacy mcp"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("fictional", StringComparison.Ordinal) || l.Contains("example.invalid", StringComparison.Ordinal));
    }
}
