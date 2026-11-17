using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Knowledge.Mcp;

// The app role's MCP surface (contracts/README.md 1 and 2.2): stateless JSON-RPC over POST /mcp with the one app
// token. Besides the graph, the app reads saved insights and the harness's runs, records feedback on one, reads saved
// dashboards and what their feeds hold now (and makes their declared writes), arranges a dashboard's parts and reads a
// map part's focused sky, asks for a map rebuild, reads glucose while a glucose source has data, hands over a document
// someone added while the documents adapter is on, keeps its screens listening for commands (docs/dev/screens.md), and
// reads the engine's version and each model's state for its Sources view (app_status, ModelStatus). There too it lists
// the adapters that sync or import on the engine's clock and asks one for a batch now (app_sync, SyncRequests).
// Enabled adapters may add tools of their own (AppTool), such as Garmin's summary, listed and accepted only while they
// are on. With app.viewers on, which this version refuses (ViewerSettings, D-046), the app's server also registers and
// ends viewer sessions (app_viewer, ViewerSessions); with it off the tool is neither listed nor accepted, answered as one
// this role does not have. Argument validation is strict: unknown or duplicate members, or values
// outside the ranges below, are refused before any work. Adapted from the private version's graph reader endpoint.
public static class AppEndpoint
{
    public const string Graph = "app_graph", Insights = "app_insights", Feedback = "app_insight_feedback", Dashboards = "app_dashboards",
        Rebuild = "app_request_refresh", LatestGlucose = "app_latest_glucose", RecentGlucose = "app_recent_glucose",
        Upload = "app_upload_document", Screens = "app_screens", EngineStatus = "app_status", Viewer = "app_viewer";
    // Sync now from the Sources view (SB-P6-09).
    public const string Sync = "app_sync";
    // The app's Memories view (SB-P3-12, AppMemories): listed and taken only with memory.tools on.
    public const string Memories = AppMemories.Name;
    // Every call fits in 32 KiB except an upload, which carries its file (base64) and may take 7 MiB.
    public const int MaxBody = 32 * 1024, UploadBody = 7 * 1024 * 1024;
    static readonly HashSet<string> Known = [Graph, Insights, Feedback, Dashboards, Rebuild, LatestGlucose, RecentGlucose, Upload, Screens, EngineStatus, Viewer, Sync, Memories, AppStudio.Name];
    static readonly string[] ScreenOps = ["listen", "done", "send", "list", "rename"];
    static readonly string[] ViewerOps = ["enrol", "whoami", "end"];
    // Viewer sessions, registered as a service only while app.viewers is on (Program.cs); null otherwise, and in tests that
    // build a context without services.
    static ViewerSessions? Sessions(HttpContext context) => context.RequestServices?.GetService<ViewerSessions>();

    // Whether the database records memory scopes on (install_settings, 6.2), read only to list app_memories' share op
    // with memory.share on: with scopes off a share is refused before anything is read, so the view is not offered one.
    // No database to ask counts as off.
    static async Task<bool> ScopesOn(IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<Database>() is not { } db) return false;
        await using var cmd = db.Source.CreateCommand($"SELECT coalesce(NOT {Database.ScopesOff},false)");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // Whether the calling app login speaks for a person (a viewer session, 028), read only to list app_memories' share
    // op: the app's own token (the household view) has none, reads no private memory and may take back no person's, so
    // every share it asked for would be refused, and it is not offered one. The op is still taken from any app login
    // while the switches are on, and answered by the engine's own rules. No database to ask counts as no person.
    static async Task<bool> PersonBound(IServiceProvider services, Guid caller, CancellationToken ct)
    {
        if (services.GetService<Database>() is not { } db) return false;
        await using var cmd = db.Source.CreateCommand("SELECT person_id IS NOT NULL FROM principals WHERE id=@id");
        cmd.Parameters.AddWithValue("id", caller);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
    public static readonly string[] Corners = ["main", "recovery", "training", "changed"];
    // The private version's names, accepted on tools/call (and listed with engine.legacyToolNames).
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["get_relationship_graph"] = Graph, ["observatory_insights"] = Insights, ["observatory_insight_feedback"] = Feedback,
        ["observatory_dashboards"] = Dashboards, ["observatory_request_refresh"] = Rebuild,
        ["observatory_latest_glucose"] = LatestGlucose, ["observatory_recent_glucose"] = RecentGlucose,
    };

    // A core tool name or alias; adapters may not take these.
    public static bool IsCore(string name) => Known.Contains(name) || Aliases.ContainsKey(name);

    public static async Task Handle(HttpContext context, Caller caller, RelationshipHost host, AppHealth health, AdapterRegistry adapters)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        var uploads = adapters.Uploads;
        // Every answer that is not a result is logged (RequestLog): the role, the tool or method by the engine's own
        // name, the status and code, and what threw; never the arguments.
        JsonElement? read = null; string? label = null;
        Task Refuse(int status, string code, string message, Exception? error = null)
        {
            RequestLog.Of(context)?.Failed(caller.Role, label, status, code, error);
            return JsonRpc.Refuse(context, status, read, code, message);
        }
        try
        {
            // memory.tools: on also makes app_memories, whose calls may carry a memory's whole text (AppMemories.MaxBody).
            var memories = MemoryTools.On(context.RequestServices);
            var limit = uploads is not null ? UploadBody : memories ? AppMemories.MaxBody : MaxBody;
            if (uploads is not null && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = UploadBody;
            if (await JsonRpc.ReadBody(context, limit, ct) is not { } request)
            { RequestLog.Of(context)?.Failed(caller.Role, null, context.Response.StatusCode, JsonRpc.Code(context.Response.StatusCode)); return; }
            (read, label) = (request, Label(request, adapters.AppTools));
            // A tool that is not the app role's is refused as forbidden (contracts/README.md 2.4), never described.
            if (JsonRpc.Envelope(request, out var asking, out var named, out _) && asking == "tools/call"
                && !Known.Contains(Aliases.GetValueOrDefault(named!, named!)) && !adapters.AppTools.ContainsKey(named!))
            { await Refuse(403, "forbidden", "Not available to this caller."); return; }
            // Viewer sessions are off (app.viewers): answered as a tool this role does not have.
            if (named == Viewer && asking == "tools/call" && Sessions(context) is null)
            { await Refuse(403, "forbidden", "Not available to this caller."); return; }
            // Memories are off (memory.tools): answered as a tool this role does not have.
            if (named == Memories && asking == "tools/call" && !memories)
            { await Refuse(403, "forbidden", "Not available to this caller."); return; }
            if (limit > MaxBody && Encoding.UTF8.GetByteCount(request.GetRawText()) > (asking != "tools/call" ? MaxBody
                : named == Upload ? UploadBody : named == Memories ? AppMemories.MaxBody : MaxBody))
            { await Refuse(413, "too_large", "Request body over the limit."); return; }
            if (named == Upload && asking == "tools/call" && uploads is null)
            { await Refuse(403, "forbidden", "Document uploads are off in this install."); return; }
            var recall = RecallServing.Settings(context.RequestServices).Tool;
            // memory.share: on beside memory.tools also takes app_memories' share op (AppMemories, SB-P3-10); off, as before.
            var share = memories && MemoryTools.ShareOn(context.RequestServices);
            if (!Allowed(request, adapters.AppTools, Sessions(context) is not null, recall, memories, share)) { await Refuse(400, "invalid_arguments", "Arguments are outside the tool's schema."); return; }
            var method = request.GetProperty("method").GetString();
            if (method == "notifications/initialized") { context.Response.StatusCode = 202; return; }
            object result;
            switch (method)
            {
                case "initialize": result = JsonRpc.Initialize("rhythm-engine-app"); break;
                case "ping": result = new { }; break;
                case "tools/list": result = new { tools = await Tools(health, caller.Id, ct, uploads, adapters.AppTools, Sessions(context) is not null, recall, memories,
                    share && await ScopesOn(context.RequestServices, ct) && await PersonBound(context.RequestServices, caller.Id, ct)) }; break;
                default:
                    var args = request.GetProperty("params").GetProperty("arguments");
                    var asked = request.GetProperty("params").GetProperty("name").GetString()!;
                    var tool = Aliases.GetValueOrDefault(asked, asked);
                    if (tool is LatestGlucose or RecentGlucose && !await health.HasGlucose(caller.Id, ct))
                    { await Refuse(403, "forbidden", "No glucose source has data in this install."); return; }
                    var (status, answer) = adapters.AppTools.TryGetValue(tool, out var added) ? await AdapterTool(added, caller.Id, args, ct) : tool switch
                    {
                        Dashboards => await Dashboard(context, caller.Id, host, adapters, args, ct),
                        Screens => await Screen(context, caller.Id, host, args, ct),
                        AppStudio.Name => await AppStudio.Call(context.RequestServices, caller.Id, args, ct),
                        Rebuild => await RebuildNow(context, caller.Id, ct),
                        Sync => await SyncNow(context, caller.Id, args, ct),
                        Insights or Feedback => await Insight(context, caller.Id, tool, args, ct),
                        Upload => await AddDocument(uploads!, caller.Id, args, ct, Sessions(context)),
                        Viewer => await ViewerSession(Sessions(context)!, caller.Id, args, ct),
                        Memories => await AppMemories.Call(context.RequestServices, caller.Id, args, ct),
                        EngineStatus => (200, JsonRpc.Result(await context.RequestServices.GetRequiredService<ModelStatus>().App(ct),
                            "The engine's version and each model's state are in structuredContent.")),
                        LatestGlucose => (200, JsonRpc.Result(await health.Latest(caller.Id, Subject(args), ct), "The latest glucose reading is in structuredContent.")),
                        RecentGlucose => (200, JsonRpc.Result(await health.Recent(caller.Id, Subject(args), args.TryGetProperty("days", out _), ct), "Recent glucose is in structuredContent.")),
                        _ => await GraphRead(context, host, adapters, caller.Id, args, ct),
                    };
                    if (answer is null) { await Refuse(status, JsonRpc.Code(status), JsonRpc.Message(status)); return; }
                    result = answer; break;
            }
            await JsonRpc.Write(context, request, result, ct);
        }
        // A screen command the rules refuse says why, in the same error shape.
        catch (ScreenRefused e) { await Refuse(e.Status, JsonRpc.Code(e.Status), e.Message); }
        // Anything else is the documented JSON-RPC error too: forbidden for a principal check, else unavailable.
        catch (UnauthorizedAccessException e) { await Refuse(403, "forbidden", JsonRpc.Message(403), e); }
        catch (Exception e) when (e is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        { await Refuse(503, "unavailable", JsonRpc.Message(503), e); }
    }

    // A request's tool by its own name (an alias by the tool it stands for), unknown_tool for a name this role does not
    // have, or its method; never a name the caller made up.
    internal static string? Label(JsonElement request, IReadOnlyDictionary<string, AppTool> added)
    {
        JsonRpc.Envelope(request, out var method, out var named, out _);
        if (method != "tools/call") return method is "initialize" or "ping" or "tools/list" or "notifications/initialized" ? method : null;
        named ??= request.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n)
            && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var tool = Aliases.GetValueOrDefault(named ?? "", named ?? "");
        return Known.Contains(tool) ? tool : added.TryGetValue(tool, out var adapterTool) ? adapterTool.Name : "unknown_tool";
    }

    static async Task<(int, object?)> AdapterTool(AppTool tool, Guid principal, JsonElement args, CancellationToken ct)
    {
        try { return (200, JsonRpc.Result(await tool.Call(principal, args, ct), tool.Summary)); }
        catch (PostgresException e) when (e.SqlState == "42501") { return (403, null); }
        catch (PostgresException e) when (e.SqlState == "22023") { return (400, null); }
    }

    static string? Subject(JsonElement args) => args.TryGetProperty("subject", out var s) ? s.GetString() : null;

    // app_viewer (ViewerSessions): enrol registers a new session's credential with an operator's one-time code and
    // answers its person, or null for any code that does not work now; whoami answers the caller's person (null for the
    // shared app token's household view); end signs a session's credential out. Who may call each is checked there.
    static async Task<(int, object?)> ViewerSession(ViewerSessions sessions, Guid principal, JsonElement args, CancellationToken ct)
    {
        switch (args.GetProperty("op").GetString())
        {
            case "enrol":
            {
                var person = await sessions.Enrol(principal, args.GetProperty("code").GetString()!, args.GetProperty("credential").GetString()!, ct);
                return (200, JsonRpc.Result(new JsonObject { ["person"] = person },
                    person is null ? "The code was not accepted." : "The viewer session is registered; its person is in structuredContent."));
            }
            case "whoami":
                return (200, JsonRpc.Result(new JsonObject { ["person"] = await sessions.Person(principal, ct) },
                    "The person this credential speaks for, or null for the household view, is in structuredContent."));
            default:
                return (200, JsonRpc.Result(new JsonObject { ["ended"] = await sessions.End(principal, ct) }, "The viewer session is signed out."));
        }
    }

    static async Task<(int, object?)> GraphRead(HttpContext context, RelationshipHost host, AdapterRegistry adapters, Guid principal, JsonElement args,
        CancellationToken ct)
    {
        // The explorer's combined search: the stars of what combined retrieval finds, best first, each with why.
        if (Text(args, "operation") == Recalled)
        {
            if (Text(args, "value") is not { } question || string.IsNullOrWhiteSpace(question) || question.Length > RecallStars.MaxQuestion
                || args.TryGetProperty("cursor", out _) || args.TryGetProperty("knownNodeIds", out _) || args.TryGetProperty("knownEdgeIds", out _))
                return (400, null);
            var services = context.RequestServices;
            var found = await RecallStars.Page(host, services.GetRequiredService<Database>(), adapters.Shows,
                RecallServing.For(services, RecallStars.Budget), principal, question, ct);
            return (200, JsonRpc.Result(found, $"{found.recall!.results.Count} stars found by words and meaning; the graph and why are in structuredContent."));
        }
        var query = new GraphQuery(Text(args, "operation") ?? "overview", Text(args, "value"), Text(args, "cursor"));
        var graph = await host.Read(principal, Ids(args, "knownNodeIds"), Ids(args, "knownEdgeIds"), ct, query);
        // The graph travels once, in structuredContent; the text block only summarizes it.
        return (200, JsonRpc.Result(graph, $"{graph.nodes.Count} nodes and {graph.edges.Count} edges; the graph is in structuredContent."));
    }

    public static async Task<object[]> Tools(AppHealth health, Guid principal, CancellationToken ct, IDocumentUpload? uploads = null,
        IReadOnlyDictionary<string, AppTool>? added = null, bool viewers = false, bool recall = false, bool memories = false, bool share = false)
    {
        var tools = new List<(string Name, object Tool)>
        {
            (Graph, new { name = Graph,
                description = "Read the map: the scheduled refresh's snapshot of every star and link, with server-side overview, title search, neighbors, project (value: a project star's id; that project's star, the stars in it and what they link to) and get, paged by cursor; content is get plus one star's current text, and data is get plus one data star's recent daily measurements, both read on request and never stored. No discovery or writes."
                    + (recall ? " recall (a question as value, at most 800 characters) is the stars of what combined retrieval finds by words and meaning, best first, with why each is there and a receipt in recall." : ""),
                inputSchema = new { type = "object", additionalProperties = false,
                    properties = new { knownNodeIds = IdSchema(), knownEdgeIds = IdSchema(),
                        operation = new { type = "string", @enum = recall ? [.. Operations, Recalled] : Operations },
                        value = new { type = "string", minLength = 1, maxLength = 1000 },
                        cursor = new { type = "string", minLength = 1, maxLength = 20 } } },
                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } }),
            (Insights, new { name = Insights, description = "Saved insights, newest first, every status, each with its feedback, and the newest 14 runs a harness logged. limit 1–50 insights, default 20.",
                inputSchema = new { type = "object", additionalProperties = false,
                    properties = new { limit = new { type = "integer", minimum = 1, maximum = 50 } } },
                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } }),
            (Feedback, new { name = Feedback, description = "Record feedback on one insight and return it updated: interesting sets status saved, explore explored, not_useful dismissed, handled handled, and later snoozed until `until` (default one day, at most 90 days). Any value may carry a note, 1–500 characters once trimmed, kept with it; the latest feedback replaces the last.",
                inputSchema = new { type = "object", additionalProperties = false, required = new[] { "id", "value" },
                    properties = new { id = new { type = "string", format = "uuid" }, value = new { type = "string", @enum = InsightStore.Feedback },
                        until = new { type = "string", format = "date-time" }, note = new { type = "string", minLength = 1, maxLength = 500 } } },
                annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false } }),
            (Dashboards, new { name = Dashboards, description = "Saved dashboards. With no arguments, every dashboard's latest version (title, stars, notes, writes, editsAllowed, corners, layout; no page), most recently changed first. With id, that dashboard's version (the latest unless version is given) with its page and feeds. Every version carries its layout ({parts: [{id, kind, x, y, w, h, focus?, dashboard?}]} on a 12 by 12 grid, or null for the page alone), layoutRevision, layoutNote and layoutSavedAt. With id and data true, what that version's feeds hold now, read on request and never stored, with the version's layout. With id, version, layout, base (the layoutRevision the change started from) and note, arranges the newest version in place: the same parts, moved, resized or refocused; answers {ok, layout, layoutRevision}, or {ok false, reason}, with the layout as it stands when it changed since base (apply the change to it and save again; never retried). With id, map (a map part's id), optional version and cursor, that part's focused sky as app_graph pages it, with matched, the count of every match. With id, version and write {name, change}, one of that version's declared writes, once someone has allowed edits; answers {ok, write} or {ok false, reason}. undo (a write's id) puts back what it replaced while nobody has changed it since. id, version and allowEdits turn edits on or off for exactly that version's writes. history true (and optional id) lists the newest writes. cards true lists the corners showing a dashboard's card; corner with dashboard (an id, or null for the stat card) sets one. pins true lists the stars pinned to the sky; pin (a graphId) with pinned true or false pins or unpins one, at most 24.",
                inputSchema = new { type = "object", additionalProperties = false,
                    properties = new { id = new { type = "string", format = "uuid" }, version = new { type = "integer", minimum = 1, maximum = 10000 }, data = new { type = "boolean", @const = true },
                        write = new { type = "object", additionalProperties = false, required = new[] { "name", "change" },
                            properties = new { name = new { type = "string", maxLength = 40 }, change = new { type = "object" } } },
                        undo = new { type = "string", format = "uuid" }, allowEdits = new { type = "boolean" }, history = new { type = "boolean", @const = true },
                        cards = new { type = "boolean", @const = true }, corner = new { type = "string", @enum = Corners },
                        pins = new { type = "boolean", @const = true }, pin = new { type = "string", minLength = 1, maxLength = 1000 }, pinned = new { type = "boolean" },
                        dashboard = new { type = new[] { "string", "null" }, format = "uuid" },
                        layout = new { type = "object" }, @base = new { type = "integer", minimum = 0 }, note = new { type = "string", minLength = 1, maxLength = 500 },
                        map = PartIdSchema(), cursor = new { type = "string", minLength = 1, maxLength = 20 } } },
                annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = true } }),
            (Rebuild, new { name = Rebuild, description = "Ask the engine to rebuild the map now, as the scheduled tick does, so changes at the sources show without waiting for it. At most one request every three minutes, and none while one is waiting or running. Returns accepted, pendingSince, running, nextAllowedAt and lastFinishedAt.",
                inputSchema = new { type = "object", additionalProperties = false, properties = new { } },
                annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false } }),
            (Sync, new { name = Sync, description = "Sync now. With no arguments, {adapters}: each enabled adapter that syncs or imports on the engine's own clock, by name, with its kind (measurements or imports), how often it runs on its own and how often it may be asked (interval and limit, in seconds), whether a batch of it is running now (its own or one asked for), since when one asked for is waiting (pendingSince), when the next may be asked for (nextAllowedAt, null while one waits or runs), and the last one asked for: lastAskedAt, lastFinishedAt and its outcome (complete, failed or timed-out). With adapter, asks that adapter for one batch now: one call of its own sync or import, under its own limits, run by the engine's scheduler between its own jobs; refused while one of its batches is running (running), while one asked for is waiting (pending), and inside its limit since the last one asked for (too-soon). Answers {accepted, refused, adapter}. States and times only.",
                inputSchema = new { type = "object", additionalProperties = false,
                    properties = new { adapter = new { type = "string", pattern = "^[a-z][a-z0-9-]{1,30}$" } } },
                annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false } }),
            (EngineStatus, new { name = EngineStatus, description = "The engine's version and schema (its newest migration's number), and each model in the stack with its job and state: the embeddings model that search, saved documents and the map's similar links use (and a shadow one being built beside it, when the settings name one), with its index (passages stored, whether the index job covers it, rows left, a pause), and the titles model, with the kind of the failure that last stopped it. States and counts only. No arguments.",
                inputSchema = new { type = "object", additionalProperties = false, properties = new { } },
                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } }),
            (Screens, new { name = Screens, description = "Live screens: each open copy of the app is a screen with its own id, which listens for commands that change what it shows. op listen (screen, name, hold 0-25 seconds, default 25, showing, settings {layout, tiles, home, grid}) records the screen and answers {command, screen}: its oldest pending command, waiting up to hold seconds for one (command null when none came), and the screen with its name as stored: a new screen whose name another screen seen in the last 30 days has gets a number after it (2, 3...), and a later listen never replaces that numbered name with its bare one (rename sets any name). op done (screen, command, result shown or failed, detail, showing) reports how one went: {ok}. op send (screen: the sender's own id, to: an open screen, action, and the action's arguments beside them or as args, which move_tile needs; move, resize and focus_map without dashboard take the one the screen last reported showing) sends one and waits up to 8 seconds for its outcome: {command}. op list (screen): the screens seen in the last 24 hours, most recent first. op rename (screen, name): {screen}. A command not taken within 30 seconds expires.",
                inputSchema = new { type = "object", additionalProperties = false, required = new[] { "op", "screen" },
                    properties = new { op = new { type = "string", @enum = ScreenOps }, screen = ScreenIdSchema(), name = new { type = "string", minLength = 1, maxLength = 60 },
                        hold = new { type = "integer", minimum = 0, maximum = ScreenCommands.MaxHold }, showing = new { type = "object" },
                        settings = new { type = "object", additionalProperties = false, properties = new { layout = new { type = "string", @enum = ScreenCommands.Layouts },
                            tiles = new { type = "object" }, home = new { type = new[] { "object", "null" } }, grid = new { type = "object" } } },
                        command = new { type = "string", format = "uuid" }, result = new { type = "string", @enum = new[] { "shown", "failed" } }, detail = new { type = "object" },
                        to = ScreenIdSchema(), action = new { type = "string", @enum = ScreenCommands.Actions }, args = new { type = "object" },
                        star = new { type = "string", minLength = 1, maxLength = ScreenCommands.MaxStar }, dashboard = new { type = new[] { "string", "null" }, format = "uuid" },
                        layout = new { type = "string", @enum = ScreenCommands.Layouts }, tile = new { type = "string", @enum = ScreenCommands.Places },
                        corner = new { type = "string", @enum = ScreenCommands.Places }, query = new { type = "string", minLength = 1, maxLength = ScreenCommands.MaxQuery },
                        part = PartIdSchema(), @with = PartIdSchema(), place = new { type = "string", @enum = ScreenCommands.MovePlaces },
                        size = new { type = "string", @enum = ScreenCommands.Sizes },
                        x = new { type = "integer", minimum = 0, maximum = DashboardLayout.Cells - 1 }, y = new { type = "integer", minimum = 0, maximum = DashboardLayout.Cells - 1 },
                        w = new { type = "integer", minimum = 1, maximum = DashboardLayout.Cells }, h = new { type = "integer", minimum = 1, maximum = DashboardLayout.Cells },
                        focus = new { type = new[] { "object", "null" } } } },
                annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false } }),
        };
        if (await health.HasGlucose(principal, ct))
        {
            var subject = new { type = "string", pattern = "^[a-z][a-z0-9-]{0,31}$" };
            tools.Add((LatestGlucose, new { name = LatestGlucose, description = "The latest glucose reading of one person (default: the first person with glucose data) from a live feed, in mg/dL, with its provenance, age and whether it is stale.",
                inputSchema = new { type = "object", additionalProperties = false, properties = new { subject } },
                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } }));
            tools.Add((RecentGlucose, new { name = RecentGlucose, description = "Recent glucose of one person in mg/dL: the last 24 hours of live readings, or with days 14 a compact 14-day series in 5-minute slots (their archive, then their live feeds), with coverage, provenance and the archive's 14-day range totals.",
                inputSchema = new { type = "object", additionalProperties = false, properties = new { subject, days = new { type = "integer", @const = 14 } } },
                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } }));
        }
        if (uploads is not null)
        {
            // With app.viewers on, a viewer session's upload is its person's own unless it chooses Family (SB-P1-20,
            // ViewerSessions.Lands); with it off the tool is listed and taken exactly as before.
            var name = new { type = "string", minLength = 1, maxLength = 200 };
            var content = new { type = "string", contentEncoding = "base64", minLength = 4, maxLength = Base64Length(uploads.MaxUploadBytes) };
            object properties = viewers ? new { name, content, family = new { type = "boolean" } } : new { name, content };
            tools.Add((Upload, new { name = Upload,
                description = $"Add a document someone chose in the app: a PDF, .txt or .md file of at most {uploads.MaxUploadBytes / 1024 / 1024} MiB, as base64. It is archived as it came, its text read, and saved as a document star that opens in the reader; the map shows it after the rebuild it asks for. The same file again changes nothing. Answers {{ok, document: {{graphId, resourceId, title, format, pages, characters, status}}}} or {{ok: false, code, reason}}."
                    + (viewers ? " family true puts it in Family, the household's; without it a viewer session's document is its person's own while memory scopes are on, and any other lands where the documents adapter puts it. The document's scope (private or shared) says where it is stored." : ""),
                inputSchema = new { type = "object", additionalProperties = false, required = new[] { "name", "content" }, properties },
                annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false } }));
        }
        if (viewers)
            tools.Add((Viewer, new { name = Viewer,
                description = "Viewer sessions in the app (app.viewers). op enrol (code, credential): with the app's own token only, registers credential (base64url, 43-512 characters) for one new browser session with a one-time code the operator made on the host for one person, and answers {person}, the code's person, or {person: null} for a code that is unknown, used, expired or whose person is suspended or gone. op whoami: {person}, whom the calling credential speaks for, null for the app's own token (the household view). op end: a session's credential signs itself out for good: {ended}.",
                inputSchema = new { type = "object", additionalProperties = false, required = new[] { "op" },
                    properties = new { op = new { type = "string", @enum = ViewerOps }, code = new { type = "string", minLength = 1, maxLength = 40 },
                        credential = new { type = "string", minLength = 43, maxLength = 512, pattern = "^[A-Za-z0-9_-]+$" } } },
                annotations = new { readOnlyHint = false, destructiveHint = false, openWorldHint = false } }));
        // memory.tools: on lists the app's Memories (AppMemories); off, the list is exactly as before. `share`: memory.share
        // is on beside it, memory scopes are on (ScopesOn) and the caller speaks for a person (PersonBound), so its share
        // op is listed; else the listing is as before.
        if (memories) tools.Add((Memories, AppMemories.Listing(share)));
        tools.Add((AppStudio.Name, AppStudio.Listing));
        foreach (var tool in (added ?? new Dictionary<string, AppTool>()).Values.Distinct()) tools.Add((tool.Name, tool.Listing));
        var listed = tools.Select(t => t.Tool).ToList();
        if (RhythmConfig.Current.Engine.LegacyToolNames)
        {
            foreach (var (alias, target) in Aliases)
                if (tools.FirstOrDefault(t => t.Name == target).Tool is { } original)
                    listed.Add(JsonRpc.Renamed(original, alias));
            foreach (var (alias, tool) in added ?? new Dictionary<string, AppTool>())
                if (alias != tool.Name) listed.Add(JsonRpc.Renamed(tool.Listing, alias));
        }
        return listed.ToArray();
    }

    // One document from the app. A file the adapter does not accept answers {ok: false, code, reason}, the reason fit to
    // show; nothing about the file is logged. With app.viewers on (sessions given) it lands as ViewerSessions.Lands says
    // and its answer names the scope it is stored in; with it off, where the documents adapter puts every upload.
    static async Task<(int Status, object? Result)> AddDocument(IDocumentUpload uploads, Guid principal, JsonElement args, CancellationToken ct,
        ViewerSessions? sessions = null)
    {
        var text = args.GetProperty("content").GetString()!;
        var content = new byte[text.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(text, content, out var length)) return (400, null);
        var landing = sessions is null ? null
            : await sessions.Landing(principal, args.TryGetProperty("family", out var family) && family.ValueKind == JsonValueKind.True, ct);
        try
        {
            var saved = await uploads.Upload(principal, args.GetProperty("name").GetString()!, content[..length], ct, landing);
            saved["ok"] = true;
            return (200, JsonRpc.Result(saved, "The document is saved; it is in structuredContent."));
        }
        catch (DocumentRefused refused)
        { return (200, JsonRpc.Result(new { ok = false, code = refused.Code, reason = refused.Message }, refused.Message)); }
    }
    static int Base64Length(long bytes) => (int)((bytes + 2) / 3 * 4);

    // Saved insights, or one feedback write. Status codes say why nothing came back.
    static async Task<(int Status, object? Result)> Insight(HttpContext context, Guid principal, string tool, JsonElement args, CancellationToken ct)
    {
        var store = context.RequestServices.GetRequiredService<InsightStore>();
        try
        {
            if (tool == Insights)
            {
                using var list = JsonDocument.Parse(await store.List(principal, null, null, args.TryGetProperty("limit", out var l) ? l.GetInt32() : 20, ct));
                using var logged = JsonDocument.Parse(await store.Runs(principal, 14, ct));
                var (insights, runs) = (list.RootElement.Clone(), logged.RootElement.Clone());
                return (200, JsonRpc.Result(new { insights, runs }, $"{insights.GetArrayLength()} insights and {runs.GetArrayLength()} runs; they are in structuredContent."));
            }
            var until = args.TryGetProperty("until", out var u) ? Instant(u.GetString()!) : null;
            InsightStore.Note(args.TryGetProperty("note", out var n) ? n.GetString() : null, out var note);
            // With memory.tools on an insight's feedback is kept, not only replaced (SB-P5-06, migration 066); the answer is the same.
            var updated = await store.Record(principal, Guid.Parse(args.GetProperty("id").GetString()!), args.GetProperty("value").GetString()!, until, note, ct,
                keep: MemoryTools.On(context.RequestServices));
            if (updated is null) return (404, null);
            using var record = JsonDocument.Parse(updated);
            return (200, JsonRpc.Result(record.RootElement.Clone(), "The updated insight is in structuredContent."));
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return (403, null); }
        catch (PostgresException e) when (e.SqlState == "22023") { return (400, null); }
    }

    // The dashboard list, one dashboard's version with its page, what that version's feeds hold now, or one of the
    // dashboard tool's other shapes.
    static async Task<(int Status, object? Result)> Dashboard(HttpContext context, Guid principal, RelationshipHost host, AdapterRegistry adapters,
        JsonElement args, CancellationToken ct)
    {
        var store = context.RequestServices.GetRequiredService<DashboardStore>();
        static (int, object?) Answer(string text, object content) => (200, JsonRpc.Result(content, text));
        static JsonElement Parsed(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        try
        {
            // Writes and undo answer {ok, write} or {ok false, reason}: the page shows the reason as it is.
            if (args.TryGetProperty("write", out var asked) || args.TryGetProperty("undo", out _))
            {
                var writer = context.RequestServices.GetRequiredService<DashboardWriter>();
                try
                {
                    var written = asked.ValueKind == JsonValueKind.Object
                        ? await writer.Write(principal, Guid.Parse(args.GetProperty("id").GetString()!), args.GetProperty("version").GetInt32(),
                            asked.GetProperty("name").GetString()!, asked.GetProperty("change"), ct)
                        : await writer.Undo(principal, Guid.Parse(args.GetProperty("undo").GetString()!), ct);
                    return Answer("The write is in structuredContent.", new { ok = true, write = Parsed(written) });
                }
                catch (DashboardWriteRefused e) { return Answer(e.Message, new { ok = false, reason = e.Message }); }
                catch (PostgresException e) when (e.SqlState is "42501" or "P0002" or "22023" or "22P02" or "22007" or "22008")
                {
                    var reason = e.SqlState is "42501" or "P0002" ? e.MessageText + "." : "The engine refused that entry.";
                    return Answer(reason, new { ok = false, reason });
                }
            }
            if (args.TryGetProperty("layout", out var arranged))
                return await Arrange(store, host, principal, Guid.Parse(args.GetProperty("id").GetString()!), args.GetProperty("version").GetInt32(),
                    args.GetProperty("base").GetInt32(), arranged, args.GetProperty("note").GetString()!, ct);
            if (args.TryGetProperty("map", out var part))
                return await MapRead(store, new DashboardMap(host, adapters), principal, Guid.Parse(args.GetProperty("id").GetString()!),
                    args.TryGetProperty("version", out var at) ? at.GetInt32() : null, part.GetString()!,
                    args.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null, ct);
            if (args.TryGetProperty("allowEdits", out var allow))
                return Answer("Edits are set.", Parsed(await store.AllowEdits(principal, Guid.Parse(args.GetProperty("id").GetString()!),
                    args.GetProperty("version").GetInt32(), allow.GetBoolean(), ct)));
            if (args.TryGetProperty("history", out _))
            {
                Guid? of = args.TryGetProperty("id", out var h) ? Guid.Parse(h.GetString()!) : null;
                return Answer("The writes are in structuredContent.", new { writes = Parsed((await store.Writes(principal, of, null, 50, ct))!) });
            }
            if (args.TryGetProperty("cards", out _)) return Answer("The corner cards are in structuredContent.", new { cards = Parsed(await store.Cards(principal, ct)) });
            if (args.TryGetProperty("pins", out _)) return Answer("The pinned stars are in structuredContent.", new { pins = Parsed(await store.Pins(principal, ct)) });
            if (args.TryGetProperty("pin", out var pin))
                return Answer("The pinned stars are in structuredContent.", new { pins = Parsed(await store.SetPin(principal, pin.GetString()!, args.GetProperty("pinned").GetBoolean(), ct)) });
            if (args.TryGetProperty("corner", out var corner))
            {
                Guid? chosen = args.GetProperty("dashboard").ValueKind == JsonValueKind.Null ? null : Guid.Parse(args.GetProperty("dashboard").GetString()!);
                return Answer("The corner cards are in structuredContent.", new { cards = Parsed(await store.SetCard(principal, corner.GetString()!, chosen, ct)) });
            }
            if (!args.TryGetProperty("id", out var given))
            {
                var dashboards = JsonNode.Parse(await store.List(principal, ct))!.AsArray();
                // A version that names no star reads memories by filter, and the app places it beside the project stars its
                // feeds name (docs/dev/screens.md 10.12), which it gets as `projects`; with none it is a star of its own.
                DashboardFeeds? feeds = null;
                foreach (var listed in dashboards.OfType<JsonObject>())
                    if (listed["stars"] is JsonArray { Count: 0 }
                        && await store.Feeds(principal, Guid.Parse((string)listed["id"]!), (int)listed["version"]!, ct) is { } saved)
                        listed["projects"] = await (feeds ??= context.RequestServices.GetRequiredService<DashboardFeeds>())
                            .ProjectStars(principal, saved.Feeds, ct);
                return Answer($"{dashboards.Count} dashboards; they are in structuredContent.", new { dashboards });
            }
            var id = Guid.Parse(given.GetString()!);
            int? version = args.TryGetProperty("version", out var v) ? v.GetInt32() : null;
            if (args.TryGetProperty("data", out _))
            {
                var feeds = context.RequestServices.GetRequiredService<DashboardFeeds>();
                if (await store.Feeds(principal, id, version, ct) is not { } saved) return (404, null);
                var read = await feeds.Read(principal, saved.Feeds, false, ct);
                var shown = new JsonObject { ["id"] = id.ToString(), ["version"] = saved.Version };
                foreach (var (name, value) in saved.Layout) shown[name] = value?.DeepClone();
                read["dashboard"] = shown;
                return Answer("What the dashboard's feeds hold now is in structuredContent.", read);
            }
            if (await store.Get(principal, id, version, true, ct) is not { } found) return (404, null);
            return Answer("The dashboard is in structuredContent.", new { dashboard = Parsed(found) });
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return (403, null); }
        catch (PostgresException e) when (e.SqlState == "22023") { return (400, null); }
        catch (PostgresException e) when (e.SqlState == "P0002") { return (404, null); }
    }

    // A screen's change to how a dashboard is arranged (docs/dev/screens.md 10.7): the newest version's layout, saved as
    // its next revision when nobody changed it since `base` and it only arranges the parts it has. Every refusal is
    // {ok: false, reason}, the reason fit to show; a layout changed since `base` also gives the layout as it stands, so
    // the screen can apply its change to it once more. A part whose focus or card changed has its reference checked:
    // the focus star on the map now, the card's dashboard saved and not this one.
    static async Task<(int Status, object? Result)> Arrange(DashboardStore store, RelationshipHost host, Guid principal, Guid id, int version,
        int baseRevision, JsonElement given, string note, CancellationToken ct)
    {
        static (int, object?) Refused(string reason) => (200, JsonRpc.Result(new { ok = false, reason }, reason));
        static (int, object?) Stale(JsonNode? layout, int revision) => (200, JsonRpc.Result(new JsonObject { ["ok"] = false,
            ["reason"] = "The layout changed since; apply the change to it and save again.", ["layout"] = layout?.DeepClone(), ["layoutRevision"] = revision },
            "The layout changed since; it is in structuredContent."));
        if (await store.Get(principal, id, version, false, ct) is not { } found) return Refused("That dashboard or version is unknown.");
        using var read = JsonDocument.Parse(found);
        if (read.RootElement.GetProperty("latest").GetInt32() != version) return Refused("This is an earlier version; open the newest to arrange it.");
        var (current, revision) = DashboardLayout.Of(read.RootElement);
        if (revision != baseRevision) return Stale(JsonNode.Parse(current.ToJson()), revision);
        if (DashboardLayout.Read(given, out var layout) is { } problem) return Refused(problem);
        string? wrong;
        try { wrong = await layout!.ReferenceProblem(current, id, async stars => await host.Known(principal, stars, ct), async other => await store.Exists(principal, other, ct)); }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or NpgsqlException or TimeoutException)
        { return Refused("The map can't be read right now, so the new focus wasn't saved."); }
        if (wrong is not null) return Refused(wrong);
        try
        {
            var saved = await store.SaveLayout(principal, id, version, baseRevision, layout, note.Trim(), ct);
            if (saved["saved"]?.GetValue<bool>() != true) return Stale(saved["layout"], saved["layoutRevision"]!.GetValue<int>());
            return (200, JsonRpc.Result(new JsonObject { ["ok"] = true, ["layout"] = saved["layout"]?.DeepClone(), ["layoutRevision"] = saved["layoutRevision"]!.GetValue<int>() },
                "The layout is saved; it is in structuredContent."));
        }
        catch (PostgresException e) when (e.SqlState is "22023" or "P0002") { return Refused(e.MessageText + "."); }
        catch (PostgresException e) when (e.SqlState == "23514") { return Refused("The engine refused that layout."); }
    }

    // A map part's focused sky (docs/dev/screens.md 10.4): 404 when the version has no such part, 400 when the part isn't
    // a map with a focus or the cursor isn't one.
    static async Task<(int Status, object? Result)> MapRead(DashboardStore store, DashboardMap map, Guid principal, Guid id, int? version, string part,
        string? cursor, CancellationToken ct)
    {
        if (await store.Get(principal, id, version, false, ct) is not { } found) return (404, null);
        using var read = JsonDocument.Parse(found);
        if (DashboardLayout.Of(read.RootElement).Layout.Parts.FirstOrDefault(p => p.Id == part) is not { } shown) return (404, null);
        if (shown is not { Kind: "map", Focus: { } focus }) return (400, null);
        FocusedMap page;
        try { page = await map.Read(principal, focus, cursor, ct); }
        catch (ArgumentException) { return (400, null); }
        var answer = JsonSerializer.SerializeToNode(page.Page, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var result = new JsonObject { ["part"] = part, ["focus"] = JsonSerializer.SerializeToNode(focus.Normalized(), DashboardLayout.Json),
            ["matched"] = page.Matched };
        foreach (var name in new[] { "nodes", "edges", "nextCursor", "completeness", "issues" }) result[name] = answer[name]?.DeepClone();
        return (200, JsonRpc.Result(new JsonObject { ["map"] = result }, $"{page.Page.nodes.Count} of {page.Matched} stars; the map is in structuredContent."));
    }

    // A request for a rebuild now; the answer says whether it was accepted and what is already under way.
    static async Task<(int Status, object? Result)> RebuildNow(HttpContext context, Guid principal, CancellationToken ct)
    {
        var requests = context.RequestServices.GetRequiredService<RefreshRequests>();
        try
        {
            using var answer = JsonDocument.Parse(await requests.Request(principal, ct));
            return (200, JsonRpc.Result(answer.RootElement.Clone(), "Whether the rebuild was accepted is in structuredContent."));
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return (403, null); }
    }

    // Sync now (SyncRequests): the adapters on the clock this caller reads, or one batch now asked of one of them. An
    // adapter that is not on the clock, or whose owner's rows the caller does not read (only with memory.scopes on),
    // answers 404, as an unknown one.
    static async Task<(int Status, object? Result)> SyncNow(HttpContext context, Guid principal, JsonElement args, CancellationToken ct)
    {
        var syncs = context.RequestServices.GetRequiredService<SyncRequests>();
        if (!args.TryGetProperty("adapter", out var named))
        {
            var listed = await syncs.List(principal, ct);
            return (200, JsonRpc.Result(listed, $"{listed["adapters"]!.AsArray().Count} adapters sync on the engine's clock; they are in structuredContent."));
        }
        if (await syncs.Request(principal, named.GetString()!, ct) is not { } answer) return (404, null);
        return (200, JsonRpc.Result(answer, answer["accepted"]!.GetValue<bool>()
            ? "The sync was accepted; the adapter's state is in structuredContent." : "The sync was not accepted; why is in structuredContent."));
    }

    // One of the screens' operations (docs/dev/screens.md 3.4). A listen may wait up to its hold for a command, and a
    // send up to 8 seconds for the outcome, without a database connection; either ends when the request does. What the
    // rules refuse (a closed screen, an unknown star or dashboard, a full queue) is 400 or 404 with the reason.
    static async Task<(int Status, object? Result)> Screen(HttpContext context, Guid principal, RelationshipHost host, JsonElement args, CancellationToken ct)
    {
        var store = context.RequestServices.GetRequiredService<ScreenStore>();
        var screen = args.GetProperty("screen").GetString()!;
        JsonElement? Given(string name) => args.TryGetProperty(name, out var value) ? value : null;
        static (int, object?) Answer(string text, object content) => (200, JsonRpc.Result(content, text));
        try
        {
            switch (args.GetProperty("op").GetString())
            {
                case "listen":
                    var hold = args.TryGetProperty("hold", out var h) ? h.GetInt32() : ScreenCommands.MaxHold;
                    var heard = await store.Listen(principal, screen, args.GetProperty("name").GetString()!, TimeSpan.FromSeconds(hold), Given("showing"), Given("settings"), ct);
                    return Answer(heard["command"] is null ? "No command came for this screen." : "A command for this screen is in structuredContent.", heard);
                case "done":
                    return Answer("The outcome is recorded.", await store.Done(principal, screen, Guid.Parse(args.GetProperty("command").GetString()!),
                        args.GetProperty("result").GetString()!, Given("detail"), Given("showing"), ct));
                case "send":
                    var action = args.GetProperty("action").GetString()!;
                    if (ScreenCommands.Problem(action, CommandArgs(args)!, out var checkedArgs) is not null) return (400, null);
                    var to = args.GetProperty("to").GetString()!;
                    // A command that arranges parts, sent without its dashboard, is checked against what the target last reported.
                    var showing = ScreenCommands.Arranges.Contains(action) && !checkedArgs.ContainsKey("dashboard")
                        ? (await store.Seen(principal, ct)).FirstOrDefault(s => s.Id == to)?.Showing : null;
                    // With memory.scopes on, what it carries is checked against the target's login too.
                    var (sending, sendArgs) = await ScreenCommands.Resolve(host, context.RequestServices.GetService<DashboardStore>(), principal, action,
                        checkedArgs, showing, ct, await store.Scope(principal, to, ct)) ?? throw new ScreenRefused(404, "That star is not on the map now.");
                    var sent = await store.Send(principal, to, sending, sendArgs, screen, ScreenCommands.Outcome, ct);
                    return Answer($"The command is {(string?)sent["result"]}; it is in structuredContent.", new JsonObject { ["command"] = sent });
                case "list":
                    var screens = await store.List(principal, ct);
                    foreach (var listed in screens) listed!["self"] = (string?)listed["id"] == screen;
                    return Answer($"{screens.Count} screens; they are in structuredContent.", new JsonObject { ["screens"] = screens });
                default:
                    return Answer("The screen is renamed.", new JsonObject
                        { ["screen"] = await store.Rename(principal, screen, args.GetProperty("name").GetString()!, ct) });
            }
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return (403, null); }
        catch (PostgresException e) when (ScreenRefused.Is(e)) { throw ScreenRefused.From(e); }
    }

    // send's command arguments: beside op, screen, to and action, or as one args object (which move_tile needs, since its
    // own `to` would be taken for the target screen). Null when args is not an object of distinct members alone with those.
    internal static Dictionary<string, JsonElement>? CommandArgs(JsonElement args)
    {
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!args.TryGetProperty("args", out var nested))
        {
            foreach (var x in args.EnumerateObject().Where(x => x.Name is not ("op" or "screen" or "to" or "action"))) given[x.Name] = x.Value;
            return given;
        }
        if (nested.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(x => x.Name is not ("op" or "screen" or "to" or "action" or "args"))) return null;
        foreach (var x in nested.EnumerateObject()) if (!given.TryAdd(x.Name, x.Value)) return null;
        return given;
    }

    static DateTimeOffset? Instant(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    static readonly string[] Operations = ["overview", "search", "neighbors", "project", "get", "content", "data"];
    // app_graph's combined search, only with engine.recall on (RecallSettings, RecallStars).
    const string Recalled = "recall";
    static object IdSchema() => new { type = "array", maxItems = 500, items = new { type = "string", minLength = 1, maxLength = 200 } };
    static object ScreenIdSchema() => new { type = "string", pattern = "^[A-Za-z0-9_-]{16,64}$" };
    static object PartIdSchema() => new { type = "string", pattern = "^[a-z][a-z0-9_]{0,31}$" };
    static string? Text(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
    static string[]? Ids(JsonElement args, string name) => args.TryGetProperty(name, out var value)
        ? value.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;

    // The request is one of this role's methods with exactly the arguments its tool takes. `viewers`: app.viewers is on,
    // and an upload may also carry its Family choice (family, a boolean). `recall`: engine.recall is on, and app_graph
    // also takes the operation recall (RecallStars). `memories`: memory.tools is on, and app_memories takes its ops (AppMemories).
    // `share`: memory.share is on beside it, and app_memories takes its share op too.
    public static bool Allowed(JsonElement request, IReadOnlyDictionary<string, AppTool>? added = null, bool viewers = false, bool recall = false,
        bool memories = false, bool share = false)
    {
        if (!JsonRpc.Envelope(request, out var method, out var name, out var args)) return false;
        if (method != "tools/call") return true;
        if (added is not null && added.TryGetValue(name!, out var adapterTool)) return adapterTool.Allowed(args);
        var tool = Aliases.GetValueOrDefault(name!, name!);
        return tool switch
        {
            Insights => args.EnumerateObject().All(x => x.Name == "limit" && x.Value.ValueKind == JsonValueKind.Number && x.Value.TryGetInt32(out var n) && n is >= 1 and <= 50),
            Feedback => args.TryGetProperty("id", out var insight) && insight.ValueKind == JsonValueKind.String && Guid.TryParseExact(insight.GetString(), "D", out _)
                && args.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String && InsightStore.Feedback.Contains(value.GetString())
                && args.EnumerateObject().All(x => x.Name is "id" or "value" || x.Name == "until" && value.GetString() == "later"
                    && x.Value.ValueKind == JsonValueKind.String && x.Value.GetString()!.Length <= 40 && Instant(x.Value.GetString()!) is not null
                    || x.Name == "note" && x.Value.ValueKind == JsonValueKind.String && InsightStore.Note(x.Value.GetString(), out _)),
            Rebuild or EngineStatus => !args.EnumerateObject().Any(),
            // Nothing (the list), or exactly an adapter's name.
            Sync => args.EnumerateObject().All(x => x.Name == "adapter" && x.Value.ValueKind == JsonValueKind.String
                && RhythmConfig.AdapterPattern().IsMatch(x.Value.GetString()!)) && args.EnumerateObject().Count() <= 1,
            Viewer => ViewerArgs(args),
            Memories => memories && AppMemories.Allowed(args, share),
            Upload => args.EnumerateObject().Count() == (viewers && args.TryGetProperty("family", out var family)
                    && family.ValueKind is JsonValueKind.True or JsonValueKind.False ? 3 : 2)
                && args.TryGetProperty("name", out var file) && file.ValueKind == JsonValueKind.String && file.GetString()!.Length is > 0 and <= 200
                && args.TryGetProperty("content", out var body) && body.ValueKind == JsonValueKind.String && body.GetString()!.Length is >= 4 and <= UploadBody,
            Dashboards => DashboardArgs(args),
            Screens => ScreenArgs(args),
            AppStudio.Name => AppStudio.Allowed(args),
            LatestGlucose => args.EnumerateObject().All(x => x.Name == "subject" && SubjectArg(x.Value)),
            RecentGlucose => args.EnumerateObject().All(x => x.Name == "subject" && SubjectArg(x.Value)
                || x.Name == "days" && x.Value.ValueKind == JsonValueKind.Number && x.Value.GetRawText() == "14"),
            Graph => args.EnumerateObject().All(x => x.Name is "knownNodeIds" or "knownEdgeIds"
                    && x.Value.ValueKind == JsonValueKind.Array && x.Value.GetArrayLength() <= 500
                    && x.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Length is > 0 and <= 200)
                || x.Name == "operation" && x.Value.ValueKind == JsonValueKind.String && (Operations.Contains(x.Value.GetString()) || recall && x.Value.GetString() == Recalled)
                || x.Name == "value" && x.Value.ValueKind == JsonValueKind.String && x.Value.GetString()!.Length is > 0 and <= 1000
                || x.Name == "cursor" && x.Value.ValueKind == JsonValueKind.String && x.Value.GetString()!.Length is > 0 and <= 20),
            _ => false
        };
    }
    // app_viewer: enrol with exactly its code and credential, whoami and end with nothing beside op.
    static bool ViewerArgs(JsonElement args)
    {
        var names = args.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != names.Distinct().Count()) return false;
        string? Text(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return Text("op") switch
        {
            "enrol" => names.Length == 3 && Text("code") is { Length: >= 1 and <= 40 } && ViewerSessions.Fits(Text("credential")),
            "whoami" or "end" => names.Length == 1,
            _ => false,
        };
    }
    static bool SubjectArg(JsonElement v) => v.ValueKind == JsonValueKind.String && (RhythmConfig.Current.IsSubject(v.GetString())
        || RhythmConfig.Current.Demo.Enabled && v.GetString() == RhythmConfig.Synthetic);

    // One of the dashboard tool's shapes, and nothing else alongside it.
    static bool DashboardArgs(JsonElement args)
    {
        var names = args.EnumerateObject().Select(x => x.Name).ToHashSet();
        bool Id(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParseExact(v.GetString(), "D", out _);
        bool Version() => args.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n is >= 1 and <= 10000;
        bool True(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        bool Only(params string[] allowed) => names.All(allowed.Contains);
        if (names.Count == 0) return true;
        if (names.Contains("write"))
            return Only("id", "version", "write") && Id("id") && Version() && args.GetProperty("write") is { ValueKind: JsonValueKind.Object } w
                && w.EnumerateObject().Count() == 2 && w.TryGetProperty("name", out var wn) && wn.ValueKind == JsonValueKind.String && wn.GetString()!.Length is > 0 and <= 40
                && w.TryGetProperty("change", out var c) && c.ValueKind == JsonValueKind.Object;
        if (names.Contains("undo")) return Only("undo") && Id("undo");
        if (names.Contains("allowEdits"))
            return Only("id", "version", "allowEdits") && Id("id") && Version() && args.GetProperty("allowEdits").ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (names.Contains("history")) return Only("history", "id") && True("history") && (!names.Contains("id") || Id("id"));
        if (names.Contains("cards")) return Only("cards") && True("cards");
        if (names.Contains("pins")) return Only("pins") && True("pins");
        if (names.Contains("pin"))
            return Only("pin", "pinned") && args.GetProperty("pin") is { ValueKind: JsonValueKind.String } star && star.GetString()!.Length is > 0 and <= 1000
                && args.TryGetProperty("pinned", out var pinned) && pinned.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (names.Contains("corner"))
            return Only("corner", "dashboard") && names.Contains("dashboard") && args.GetProperty("corner") is { ValueKind: JsonValueKind.String } corner
                && Corners.Contains(corner.GetString()) && (args.GetProperty("dashboard").ValueKind == JsonValueKind.Null || Id("dashboard"));
        // A screen's arrangement of the newest version, from the layout revision it started from, and a map part's sky.
        if (names.Contains("layout"))
            return names.SetEquals(["id", "version", "layout", "base", "note"]) && Id("id") && Version()
                && args.GetProperty("layout").ValueKind == JsonValueKind.Object
                && args.GetProperty("base") is { ValueKind: JsonValueKind.Number } b && b.TryGetInt32(out var revision) && revision >= 0
                && args.GetProperty("note") is { ValueKind: JsonValueKind.String } note && note.GetString()!.Trim().Length is >= 1 and <= 500;
        if (names.Contains("map"))
            return Only("id", "version", "map", "cursor") && Id("id") && (!names.Contains("version") || Version())
                && args.GetProperty("map") is { ValueKind: JsonValueKind.String } map && DashboardPart.IdFits(map.GetString())
                && (!names.Contains("cursor") || args.GetProperty("cursor") is { ValueKind: JsonValueKind.String } c && c.GetString()!.Length is > 0 and <= 20);
        return Only("id", "version", "data") && Id("id") && (!names.Contains("version") || Version()) && (!names.Contains("data") || True("data"));
    }

    // One of the screens' operations with exactly its arguments; send's command as control_screen checks it, and the
    // settings a screen keeps as ScreenCommands.Settings does. What a screen shows and keeps are objects the database
    // measures again in its own form.
    static bool ScreenArgs(JsonElement args)
    {
        var names = args.EnumerateObject().Select(x => x.Name).ToHashSet();
        string? Text(string n) => args.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Only(params string[] allowed) => names.All(allowed.Contains);
        bool Object(string n, int max) => !names.Contains(n) || args.GetProperty(n) is { ValueKind: JsonValueKind.Object } o && o.GetRawText().Length <= max;
        if (!ScreenCommands.Id(Text("screen"))) return false;
        return Text("op") switch
        {
            "listen" => Only("op", "screen", "name", "hold", "showing", "settings") && ScreenCommands.Name(Text("name"))
                && (!names.Contains("hold") || args.GetProperty("hold") is { ValueKind: JsonValueKind.Number } h && h.TryGetInt32(out var hold)
                    && hold is >= 0 and <= ScreenCommands.MaxHold)
                && Object("showing", 4000) && Object("settings", 2000) && (!names.Contains("settings") || ScreenCommands.Settings(args.GetProperty("settings"))),
            "done" => Only("op", "screen", "command", "result", "detail", "showing") && Guid.TryParseExact(Text("command"), "D", out _)
                && Text("result") is "shown" or "failed" && Object("detail", 4000) && Object("showing", 4000),
            "send" => ScreenCommands.Id(Text("to")) && Text("action") is { } action && CommandArgs(args) is { } command
                && ScreenCommands.Problem(action, command, out _) is null,
            "list" => Only("op", "screen"),
            "rename" => Only("op", "screen", "name") && ScreenCommands.Name(Text("name")),
            _ => false,
        };
    }
}
