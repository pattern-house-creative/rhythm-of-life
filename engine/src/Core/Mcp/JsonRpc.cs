using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowledge.Mcp;

// The small, strict JSON-RPC 2.0 framing the app and ingest roles use: one request per POST, stateless, answered
// with application/json (contracts/README.md 1.1 and 4.2).
public static class JsonRpc
{
    public const string ProtocolVersion = "2025-03-26";

    // The parsed request, or null after answering 413 (too large) or 400 (not one JSON-RPC object).
    public static async Task<JsonElement?> ReadBody(HttpContext context, int limit, CancellationToken ct, string invalid = "The body is not one JSON-RPC request.")
    {
        if (context.Request.ContentLength > limit) { await Refuse(context, 413, null, "too_large", "Request body over the limit."); return null; }
        using var body = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > limit) { await Refuse(context, 413, null, "too_large", "Request body over the limit."); return null; }
            body.Write(buffer, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(body.ToArray());
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json.RootElement.Clone();
        }
        catch (JsonException) { }
        await Refuse(context, 400, null, "invalid_arguments", invalid);
        return null;
    }

    internal static bool Unique(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().GroupBy(x => x.Name).All(g => g.Count() == 1);

    // A well-formed request: jsonrpc 2.0, an id (but none on notifications/initialized), one of initialize, ping,
    // tools/list or tools/call, and for tools/call exactly {name, arguments} with an object of unique members.
    public static bool Envelope(JsonElement request, out string? method, out string? tool, out JsonElement arguments)
    {
        method = null; tool = null; arguments = default;
        if (!Unique(request) || request.EnumerateObject().Any(x => x.Name is not ("jsonrpc" or "id" or "method" or "params"))
            || !request.TryGetProperty("jsonrpc", out var rpc) || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0"
            || !request.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String) return false;
        method = m.GetString();
        if (method == "notifications/initialized") return !request.TryGetProperty("id", out _)
            && (!request.TryGetProperty("params", out var notification) || Unique(notification) && !notification.EnumerateObject().Any());
        if (!request.TryGetProperty("id", out var id) || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return false;
        if (method == "initialize") return !request.TryGetProperty("params", out var init) || Unique(init);
        if (method is "ping" or "tools/list") return !request.TryGetProperty("params", out var empty) || Unique(empty) && !empty.EnumerateObject().Any();
        if (method != "tools/call" || !request.TryGetProperty("params", out var p) || !Unique(p)
            || p.EnumerateObject().Count() != 2 || !p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !p.TryGetProperty("arguments", out var args) || !Unique(args)) return false;
        tool = name.GetString(); arguments = args;
        return true;
    }

    public static object Initialize(string server) => new
    {
        protocolVersion = ProtocolVersion, capabilities = new { tools = new { } },
        serverInfo = new { name = server, version = typeof(JsonRpc).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" }
    };
    public static object Result(object structured, string summary) =>
        new { structuredContent = structured, content = new[] { new { type = "text", text = summary } }, isError = false };
    public static object Failure(JsonObject error) =>
        new { structuredContent = new JsonObject { ["error"] = error }, content = new[] { new { type = "text", text = (string?)error["message"] ?? "" } }, isError = true };

    public static async Task Write(HttpContext context, JsonElement request, object result, CancellationToken ct)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = request.GetProperty("id").Clone(), result }, ct);
    }
    // An HTTP status with an isError result naming the contract's error code, so logs and plugins can see why.
    public static async Task Refuse(HttpContext context, int status, JsonElement? request, string code, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        object? id = request is { } r && r.TryGetProperty("id", out var given) ? given.Clone() : null;
        await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result = Failure(new JsonObject { ["code"] = code, ["message"] = message }) });
    }
    public static string Code(int status) => status switch
    {
        400 => "invalid_arguments", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found", 413 => "too_large", _ => "unavailable"
    };
    public static string Message(int status) => status switch
    {
        400 => "Arguments are outside the tool's schema.", 401 => "Token missing or unknown.", 403 => "Not available to this caller.",
        404 => "That item does not exist.", 413 => "Request body over the limit.", _ => "A source or the database is not answering."
    };
    // A tool description under another name, for listing aliases.
    public static object Renamed(object tool, string name)
    {
        var node = JsonSerializer.SerializeToNode(tool)!.AsObject();
        node["name"] = name;
        node["description"] = $"Old name of {(string?)JsonSerializer.SerializeToNode(tool)!["name"]}; the same tool. " + (string?)node["description"];
        return node;
    }
}
