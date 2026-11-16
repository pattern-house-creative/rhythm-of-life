namespace Knowledge.Mcp;

// The engine's door, in front of every route (contracts/README.md 1.4). No browser calls (no CORS: any Origin is
// refused) and only the host names in engine.allowedHosts; /health then needs nothing more. Every other request is
// POST /mcp, or for the app role alone GET /app/documents/<id>, or for the capture role alone POST /capture while the
// settings open it (a route they do not is no route at all: Routes.Capture null), and waits for the engine's start work;
// its bearer token (at most 600 characters) names a principal, whose role picks the endpoint: app and ingest have their
// own JSON-RPC endpoints, harness goes on to the MCP SDK (the only role that may use another method there). Each refusal
// writes a line in the log naming why (RequestLog) and, but for a 405, has a JSON-RPC body. Tests drive it with fake
// routes and tokens (PerimeterTests).
public sealed class Perimeter(IEnumerable<string> allowedHosts, Func<bool> ready,
    Func<string, CancellationToken, Task<Caller?>> authenticate, Perimeter.Routes routes, RequestLog? log = null, OAuthServer? oauth = null)
{
    // Where an authenticated call goes: the app's and ingest's endpoints, the app's document originals, and the capture
    // endpoint when the settings open it (MemorySettings.CaptureOn).
    public sealed record Routes(Func<HttpContext, Caller, Task> App, Func<HttpContext, Caller, Task> Ingest,
        Func<HttpContext, Caller, Guid, Task> Document, Func<HttpContext, Caller, Task>? Capture = null);

    public const int MaxAuthorization = 600;
    readonly HashSet<string> allowed = allowedHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task Handle(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (oauth is not null && OAuthServer.ProtocolPath(context.Request.Path))
        {
            if (!ready()) { context.Response.StatusCode = 503; return; }
            await oauth.ProtocolGate(context, next); return;
        }
        if (context.Request.Headers.ContainsKey("Origin")) { await Refuse(context, 403, "origin_present", "Browser calls are not accepted."); return; }
        if (!allowed.Contains(context.Request.Host.Host) && !allowed.Contains(context.Request.Host.Value ?? ""))
        { await Refuse(context, 403, "host_not_allowed", "The engine does not answer to this host name (engine.allowedHosts)."); return; }
        if (context.Request.Path == "/health") { await next(context); return; }
        var file = AppFiles.Matches(context.Request.Path, out var document);
        var capture = routes.Capture is not null && context.Request.Path == CaptureEndpoint.Route;
        if (context.Request.Path != "/mcp" && !file && !capture) { await Refuse(context, 404, "not_found", "No such route."); return; }
        if (!ready()) { await Refuse(context, 503, "not_ready", "The engine is starting; retry shortly."); return; }
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || auth.Length > MaxAuthorization)
        { await Refuse(context, 401, "no_token", "Token missing or unknown."); return; }
        var caller = await authenticate(auth[7..].Trim(), context.RequestAborted);
        if (caller is null && oauth is not null && context.Request.Path == "/mcp") caller = await oauth.Authenticate(context);
        if (caller is null) { await Refuse(context, 401, "unknown_token", "Token missing or unknown."); return; }
        context.Items["principal"] = caller.Id;
        // A document's original file is the app's alone, read with GET; everything else is POST /mcp.
        if (file)
        {
            if (caller.Role != "app") { await Refuse(context, 403, "wrong_role", "Not available to this caller.", caller.Role); return; }
            await routes.Document(context, caller, document); return;
        }
        // POST /capture is the capture role's alone, and that role reaches nothing else.
        if (capture != (caller.Role == "capture")) { await Refuse(context, 403, "wrong_role", "Not available to this caller.", caller.Role); return; }
        if (context.Request.Method != "POST" && caller.Role != "harness")
        { log?.Refused(405, "method_not_allowed", caller.Role); context.Response.StatusCode = 405; return; }
        if (capture) { await routes.Capture!(context, caller); return; }
        switch (caller.Role)
        {
            case "app": await routes.App(context, caller); return;
            case "ingest": await routes.Ingest(context, caller); return;
            case "harness": await next(context); return;
            default: await Refuse(context, 403, "wrong_role", "This token has no role; give it one or create a new token.", caller.Role); return;
        }
    }

    Task Refuse(HttpContext context, int status, string reason, string message, string? role = null)
    {
        log?.Refused(status, reason, role);
        if (status == 401 && oauth is not null && context.Request.Path == "/mcp") context.Response.Headers.WWWAuthenticate = oauth.Challenge;
        return JsonRpc.Refuse(context, status, null, JsonRpc.Code(status), message);
    }
}
