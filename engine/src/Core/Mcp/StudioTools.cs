using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Knowledge.Mcp;

public sealed partial class HarnessTools
{
    [McpServerTool(Name = "studio_dashboard", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public async Task<string> StudioDashboard(IServiceProvider services, string op, Guid sessionId, Guid? requestId = null,
        string? screen = null, int? revision = null, Guid? dashboardId = null, DashboardDraft? draft = null,
        DashboardLayout? layout = null, DashboardControl[]? controls = null, string? message = null, CancellationToken cancellationToken = default)
    {
        if (op is not ("begin" or "read" or "update" or "progress" or "cancel" or "discard" or "save")) throw new McpException("op is begin, read, update, progress, cancel, discard or save");
        if (op == "begin" && !ScreenCommands.Id(screen)) throw new McpException("begin needs a screen id from list_screens");
        if (dashboardId is not null && op != "begin" || draft is not null && op != "update" || layout is not null && op != "update"
            || controls is not null && op != "update" || message is not null && op != "progress") throw new McpException("Send only this operation's arguments");
        var payload = new JsonObject();
        if (op == "begin") payload["dashboard"] = dashboardId;
        if (op == "update")
        {
            if (draft is null) throw new McpException("update needs a complete renderable draft");
            payload["draft"] = JsonSerializer.SerializeToNode(draft, DashboardDraft.Json);
            if (layout is not null) payload["draft"]!["layout"] = JsonSerializer.SerializeToNode(layout, DashboardLayout.Json);
            payload["controls"] = JsonSerializer.SerializeToNode(controls ?? [], DashboardDraft.Json);
        }
        if (op == "progress") payload["message"] = message;
        try
        {
            if (op == "begin" && await services.GetRequiredService<ScreenStore>().Scope(Principal(), screen!, cancellationToken) is { ReadsAll: false })
                throw new McpException("The target screen cannot read this agent's sources; choose a screen with matching access");
            var result = await services.GetRequiredService<StudioStore>().Call(Principal(), op, sessionId, screen, requestId, revision, payload, cancellationToken);
            if (op == "begin" && result["replayed"]?.GetValue<bool>() != true) result["delivery"] = await services.GetRequiredService<ScreenStore>().Send(Principal(), screen!, "open_studio",
                new JsonObject { ["session"] = sessionId }, null, ScreenCommands.Outcome, cancellationToken);
            return result.ToJsonString();
        }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "42501" or "22023" or "P0002") { throw new McpException(e.MessageText); }
    }

    [McpServerTool(Name = "discover_dashboard_controls", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> DiscoverDashboardControls(IServiceProvider services, string screen, Guid dashboardId, int version, CancellationToken cancellationToken = default) =>
        DashboardSelection(services, screen, dashboardId, version, null, null, null, cancellationToken);

    [McpServerTool(Name = "invoke_dashboard_action", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public Task<string> InvokeDashboardAction(IServiceProvider services, string screen, Guid dashboardId, int version, int selectionRevision,
        string control, JsonElement value, CancellationToken cancellationToken = default) =>
        DashboardSelection(services, screen, dashboardId, version, selectionRevision, control, value, cancellationToken);

    async Task<string> DashboardSelection(IServiceProvider services, string screen, Guid dashboardId, int version, int? selectionRevision,
        string? control, JsonElement? value, CancellationToken ct)
    {
        if (!ScreenCommands.Id(screen) || version is < 1 or > 10000 || selectionRevision is < 0) throw new McpException("Give a screen id and current dashboard version/revision");
        try
        {
            var manifest = await services.GetRequiredService<StudioStore>().Controls(Principal(), dashboardId, version, ct);
            if (control is not null)
            {
                var c = manifest.Deserialize<DashboardControl[]>(DashboardDraft.Json)!.FirstOrDefault(c => c.Id == control);
                if (c is null || value is null || !c.Accepts(value.Value)) throw new McpException("The control or typed value is unavailable; discover controls first");
            }
            var args = new JsonObject { ["dashboard"] = dashboardId, ["version"] = version };
            if (control is not null) { args["control"] = control; args["value"] = JsonNode.Parse(value!.Value.GetRawText()); args["base"] = selectionRevision; }
            return (await services.GetRequiredService<ScreenStore>().Send(Principal(), screen, control is null ? "dashboard_controls" : "dashboard_action",
                args, null, ScreenCommands.Outcome, ct)).ToJsonString();
        }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "42501" or "22023" or "P0002") { throw new McpException(e.MessageText); }
    }
}

public static class AppStudio
{
    public const string Name = "app_studio";
    public static object Listing => new { name = Name, description = "Studio for this bound screen: active, read, data, validate, feedback, cancel, discard or explicit save. controls reads a saved version's typed manifest.",
        inputSchema = new { type = "object", additionalProperties = false, required = new[] { "op", "screen" },
            properties = new { op = new { type = "string" }, screen = new { type = "string" }, sessionId = new { type = "string", format = "uuid" },
                requestId = new { type = "string", format = "uuid" }, revision = new { type = "integer" }, payload = new { type = "object" }, dashboardId = new { type = "string", format = "uuid" }, version = new { type = "integer" } } } };
    public static bool Allowed(JsonElement args)
    {
        if (!args.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || !args.TryGetProperty("screen", out var s)
            || s.ValueKind != JsonValueKind.String || !ScreenCommands.Id(s.GetString())) return false;
        var operation = op.GetString();
        string[] keys = operation switch {
            "active" => ["op", "screen"], "controls" => ["op", "screen", "dashboardId", "version"],
            "read" => ["op", "screen", "sessionId"], "data" => ["op", "screen", "sessionId", "revision"],
            "validate" or "feedback" or "cancel" or "discard" or "save" => ["op", "screen", "sessionId", "requestId", "revision", "payload"], _ => [] };
        if (keys.Length == 0 || args.EnumerateObject().Any(x => !keys.Contains(x.Name))) return false;
        foreach (var name in new[] { "sessionId", "requestId", "dashboardId" })
            if (keys.Contains(name) && (!args.TryGetProperty(name, out var g) || g.ValueKind != JsonValueKind.String || !Guid.TryParseExact(g.GetString(), "D", out _))) return false;
        foreach (var name in new[] { "version", "revision" })
            if (keys.Contains(name) && (!args.TryGetProperty(name, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var v) || v < 0)) return false;
        if (operation is "validate" or "feedback")
        {
            if (!args.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return false;
            if (operation == "validate") return p.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                && p.EnumerateObject().All(x => x.Name == "ok" || x.Name == "message" && x.Value.ValueKind == JsonValueKind.String && x.Value.GetString()!.Length <= 1000);
            return p.EnumerateObject().Count() == 1 && p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString()!.Length is > 0 and <= 1000;
        }
        return !args.TryGetProperty("payload", out var payload) || payload.ValueKind == JsonValueKind.Object && !payload.EnumerateObject().Any();
    }
    public static async Task<(int, object?)> Call(IServiceProvider services, Guid principal, JsonElement args, CancellationToken ct)
    {
        var store = services.GetRequiredService<StudioStore>();
        var op = args.GetProperty("op").GetString()!; var screen = args.GetProperty("screen").GetString()!;
        try
        {
            JsonNode? result = op switch {
                "active" => await store.Active(principal, screen, ct),
                "controls" => await store.Controls(principal, args.GetProperty("dashboardId").GetGuid(), args.GetProperty("version").GetInt32(), ct),
                "data" => await store.Data(principal, args.GetProperty("sessionId").GetGuid(), screen, args.GetProperty("revision").GetInt32(), ct),
                _ => await store.Call(principal, op, args.GetProperty("sessionId").GetGuid(), screen,
                    args.TryGetProperty("requestId", out var request) ? request.GetGuid() : null,
                    args.TryGetProperty("revision", out var revision) ? revision.GetInt32() : null,
                    args.TryGetProperty("payload", out var payload) ? JsonNode.Parse(payload.GetRawText())!.AsObject() : null, ct) };
            return (200, JsonRpc.Result(new JsonObject { ["value"] = result }, "Studio state is in structuredContent."));
        }
        catch (ArgumentException e) { return (400, JsonRpc.Result(new { error = e.Message }, e.Message)); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "42501" or "22023" or "P0002")
        { return (200, JsonRpc.Result(new { error = e.MessageText, code = e.SqlState == "42501" ? "forbidden" : "conflict" }, e.MessageText)); }
    }
}
