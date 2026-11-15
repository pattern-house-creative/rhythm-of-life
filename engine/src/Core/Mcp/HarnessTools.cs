using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Knowledge.Relationships;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knowledge.Mcp;

// The harness role's tools (contracts/README.md 2.1), served by the MCP SDK. Each description is a template filled
// from rhythm.yaml at start ({people}: "Alex, Sam and the household"; {subjects}: "alex, sam or household"), so no
// description names anyone. The dashboard design guidance is the resource and prompt rhythm://guides/dashboards.
// Adapted from the private version's tool class, whose descriptions named one household and one chat client.
public sealed partial class HarnessTools(Retrieval reader, IHttpContextAccessor accessor)
{
    Guid Principal() => accessor.HttpContext?.Items["principal"] is Guid id ? id : throw new McpException("Unavailable");
    // The arguments as the call wrote them; null when the tool is called directly, and then only values that are not
    // null count as given. The SDK binds arguments leniently (member names in any case, numbers written as strings), so
    // a layout and a focus are read again from what was written, by the engine's own rules (docs/dev/screens.md 10.7).
    internal IReadOnlyDictionary<string, JsonElement>? Arguments { get; set; }
    JsonElement? Written(string name) => Arguments is { } given && given.TryGetValue(name, out var value) ? value : null;
    // A null given counts only where it means something: move's, resize's and focus_map's dashboard null is the screen's
    // default Home, told from one left out (which the engine fills in from what the screen shows). Any other null is
    // left out, as it was before piece 3.
    bool Gave(string name, object? value, string action) =>
        value is not null || name == "dashboard" && ScreenCommands.Arranges.Contains(action) && Arguments?.ContainsKey(name) == true;
    // The health tools choose sources with two vendor-neutral arguments: provider keeps one source for every metric, and
    // glucoseSource (a provider id) keeps glucose readings to one provider and leaves the other metrics as they are. The
    // older carelinkSource (csv | live) is a deprecated alias of glucoseSource naming one of the CareLink plugin's two
    // providers; glucoseSource wins when both are given.
    const string GlucoseText = "Optional provider id (for example nightscout, csv or carelink-live): glucose readings from that provider only, never combined with another glucose source; other metrics are unaffected.";
    const string CarelinkText = "Deprecated: use glucoseSource. csv or live, the same as glucoseSource carelink-csv or carelink-live.";
    internal static string GlucoseSource(string glucoseSource, string? carelinkSource) => glucoseSource != "" ? glucoseSource
        : carelinkSource switch
        {
            null => "",
            "csv" or "live" => "carelink-" + carelinkSource,
            _ => throw new McpException("carelinkSource is csv or live; it is deprecated: use glucoseSource with a provider id"),
        };

    [McpServerTool(Name = "get_relationship_graph", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> GetRelationshipGraph(IServiceProvider services, string[]? knownNodeIds = null, string[]? knownEdgeIds = null, CancellationToken cancellationToken = default)
    {
        try { return JsonSerializer.Serialize(await services.GetRequiredService<RelationshipHost>().Read(Principal(), knownNodeIds, knownEdgeIds, cancellationToken)); }
        catch (Exception e) when (e is not OperationCanceledException) { throw Unavailable(services, "get_relationship_graph", e); }
    }

    [McpServerTool(Name = "explore_graph", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> ExploreGraph(IServiceProvider services, string? query = null, string? star = null, int depth = 1, int limit = 40, CancellationToken cancellationToken = default)
        => Graph(services, "explore_graph", () => services.GetRequiredService<RelationshipHost>().Explore(Principal(), query, star, depth, limit, cancellationToken));

    [McpServerTool(Name = "graph_changes", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> GraphChanges(IServiceProvider services, DateTimeOffset since, int limit = 50, CancellationToken cancellationToken = default)
        => Graph(services, "graph_changes", () => services.GetRequiredService<RelationshipHost>().Changes(Principal(), since, limit, cancellationToken));

    static async Task<string> Graph<T>(IServiceProvider services, string tool, Func<Task<T>> read)
    {
        try { return JsonSerializer.Serialize(await read()); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        catch (Exception e) when (e is not OperationCanceledException) { throw Unavailable(services, tool, e); }
    }

    // The map or the database did not answer: the caller hears that, and the log gets the tool and the type of what
    // threw (RequestLog). The exception itself stays out of the answer and the SDK's log, as its message can carry a value.
    // A refusal by the scope rules of the derived records (migration 027: the author rule, a log's subject, what a
    // record may cite) says in its own words what to change; the role check's (dashboard_reader: "... is not
    // authorized") says the tool is not this login's.
    static string Refusal(Npgsql.PostgresException e, string role) =>
        e.MessageText.EndsWith(" is not authorized", StringComparison.Ordinal) ? role : e.MessageText;

    static McpException Unavailable(IServiceProvider services, string tool, Exception e, string message = "Relationship graph unavailable")
    {
        services.GetService<RequestLog>()?.Failed("harness", tool, 503, "unavailable", e);
        return new McpException(message);
    }

    [McpServerTool(Name = "save_insight", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public async Task<string> SaveInsight(IServiceProvider services, string kind, string title, string summary, string whyItMatters, string nextStep, string evidenceLevel, string limitations, DateTimeOffset generatedAt, DateTimeOffset sourcesAsOf, string generatedBy = InsightDraft.DefaultLabel, InsightPoint[]? points = null, InsightStar[]? stars = null, InsightSource[]? sources = null, Guid? supersedes = null, CancellationToken cancellationToken = default)
    {
        var principal = Principal();
        var draft = new InsightDraft(kind, title, summary, whyItMatters, nextStep, points ?? [], evidenceLevel, limitations, stars ?? [], sources ?? [], generatedBy, generatedAt, sourcesAsOf, supersedes);
        if (draft.Problem(DateTimeOffset.UtcNow) is { } problem) throw new McpException(problem);
        IReadOnlySet<string> known = new HashSet<string>();
        if (draft.Stars.Count > 0)
            try { known = await services.GetRequiredService<RelationshipHost>().Known(principal, draft.Stars.Select(s => s.GraphId), cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { throw Unavailable(services, "save_insight", e, "Relationship graph unavailable; the insight was not saved"); }
        try
        {
            var id = await services.GetRequiredService<InsightStore>().Save(principal, draft with { Stars = draft.Stars.Where(s => known.Contains(s.GraphId)).ToArray() }, cancellationToken);
            return JsonSerializer.Serialize(new { id, droppedStars = draft.Stars.Where(s => !known.Contains(s.GraphId)).ToArray() }, InsightDraft.Json);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException(Refusal(e, "Saving insights is not available to this login")); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "22023")
        { throw new McpException(e.MessageText == "Unknown superseded insight" ? "supersedes names no saved insight" : e.MessageText); }
    }

    [McpServerTool(Name = "list_insights", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> ListInsights(IServiceProvider services, string? status = null, DateTimeOffset? since = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (status is not null && !InsightStore.Statuses.Contains(status)) throw new McpException("status must be new, saved, explored, snoozed, dismissed, superseded or handled");
        if (limit is < 1 or > 50) throw new McpException("limit must be 1–50");
        var store = services.GetRequiredService<InsightStore>(); var principal = Principal();
        try { return "{\"insights\":" + await store.List(principal, status, since, limit, cancellationToken) + ",\"runs\":" + await store.Runs(principal, 7, cancellationToken) + "}"; }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Insights are not available to this login"); }
    }

    [McpServerTool(Name = "log_insight_run", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public async Task<string> LogInsightRun(IServiceProvider services, string outcome, string? note = null, Guid? insightId = null, string generatedBy = InsightDraft.DefaultLabel, CancellationToken cancellationToken = default)
    {
        if (!InsightStore.Outcomes.Contains(outcome)) throw new McpException("outcome must be saved, quiet or blocked");
        if (!InsightDraft.Label(generatedBy)) throw new McpException("generatedBy is a short label such as harness-daily: a lowercase letter, then up to 39 lowercase letters, digits or hyphens");
        if (!InsightStore.Note(note, out var trimmed)) throw new McpException("note is 1–500 characters once trimmed");
        try { return JsonSerializer.Serialize(new { id = await services.GetRequiredService<InsightStore>().Log(Principal(), generatedBy, outcome, trimmed, insightId, cancellationToken) }); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Logging runs is not available to this login"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "22023") { throw new McpException("insightId names no saved insight"); }
    }

    [McpServerTool(Name = "dashboard_data", ReadOnly = true, Destructive = false, OpenWorld = true)]
    public async Task<string> DashboardData(IServiceProvider services, DashboardFeed[]? feeds = null, Guid? dashboardId = null, int? version = null, bool full = false, CancellationToken cancellationToken = default)
    {
        var principal = Principal();
        if ((feeds is null) == (dashboardId is null)) throw new McpException("Give feeds, or dashboardId for a saved dashboard's feeds");
        if (version is not null && dashboardId is null) throw new McpException("version needs dashboardId");
        if (dashboardId is { } id)
            try { feeds = (await services.GetRequiredService<DashboardStore>().Feeds(principal, id, version, cancellationToken))?.Feeds.ToArray() ?? throw new McpException("dashboardId or version names no saved dashboard"); }
            catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Dashboards are not available to this login"); }
        if (DashboardDraft.FeedsProblem(feeds) is { } problem) throw new McpException(problem);
        try { return (await services.GetRequiredService<DashboardFeeds>().Read(principal, feeds!, !full, cancellationToken)).ToJsonString(); }
        catch (Exception e) when (e is not OperationCanceledException and not McpException) { throw Unavailable(services, "dashboard_data", e); }
    }

    [McpServerTool(Name = "save_dashboard", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public async Task<string> SaveDashboard(IServiceProvider services, string note, string? title = null, string? html = null, DashboardFeed[]? feeds = null, string? description = null, Guid? dashboardId = null, int? restoreVersion = null, DashboardWrite[]? writes = null, DashboardLayout? layout = null, int? layoutRevision = null, CancellationToken cancellationToken = default)
    {
        var principal = Principal();
        if (DashboardDraft.NoteProblem(note) is { } wrong) throw new McpException(wrong);
        if (Written("layout") is { ValueKind: JsonValueKind.Object } written && DashboardLayout.Read(written, out layout) is { } misread)
            throw new McpException(misread);
        // dashboardId, layout and note alone arrange the newest version in place, without a new version.
        var arranging = layout is not null && dashboardId is not null && title is null && html is null && feeds is null && description is null
            && writes is null && restoreVersion is null;
        if (layoutRevision is not null && !arranging)
            throw new McpException("layoutRevision is for arranging the newest version: send only dashboardId, layout, note and layoutRevision");
        if (layoutRevision is < 0) throw new McpException("layoutRevision is the layoutRevision list_dashboards shows, 0 or more");
        if (layout?.Problem() is { } unfit) throw new McpException(unfit);
        if (arranging) return await Arrange(services, principal, dashboardId!.Value, layout!, layoutRevision, note.Trim(), cancellationToken);
        DashboardDraft? draft = null; IReadOnlyList<DashboardStar> stars = [];
        if (restoreVersion is not null)
        {
            if (dashboardId is null || title is not null || html is not null || feeds is not null || description is not null || writes is not null || layout is not null)
                throw new McpException("restoreVersion copies an earlier version back, as it was last arranged: send only dashboardId, restoreVersion and note");
        }
        else
        {
            draft = new(title?.Trim() ?? "", description?.Trim() ?? "", html ?? "", feeds ?? [], note.Trim(), writes);
            if (draft.Problem() is { } problem) throw new McpException(problem);
            // The stars it reads and the sources it writes; a log has no star of its own until it has numbers.
            var named = draft.Feeds.Where(f => f.Kind != "log").Select(f => f.Star).Concat((draft.Writes ?? []).Where(w => w.Kind != "log").Select(w => w.Star!)).Distinct().ToArray();
            if (named.Length == 0) throw new McpException("At least one feed or write names a star; add a feed for a star the dashboard is about");
            if (named.Length > 12) throw new McpException("A dashboard names at most 12 stars across its feeds and writes");
            IReadOnlyDictionary<string, string> known;
            try { known = await services.GetRequiredService<DashboardFeeds>().Stars(principal, named, cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { throw Unavailable(services, "save_dashboard", e, "Relationship graph unavailable; the dashboard was not saved"); }
            for (var i = 0; i < draft.Feeds.Count; i++)
                if (draft.Feeds[i].Kind != "log" && !known.ContainsKey(draft.Feeds[i].Star)) throw new McpException($"feeds[{i}].star is not a star you can read; find stars with explore_graph");
            for (var i = 0; i < (draft.Writes?.Count ?? 0); i++)
                if (draft.Writes![i].Kind != "log" && !known.ContainsKey(draft.Writes[i].Star!)) throw new McpException($"writes[{i}].star is not a star you can read; find the document with explore_graph");
            stars = named.Select(s => new DashboardStar(s, known[s])).ToArray();
        }
        if (layout is not null && await LayoutProblem(services, principal, dashboardId, layout, null, cancellationToken) is { } refused) throw new McpException(refused);
        try
        {
            var (id, saved) = await services.GetRequiredService<DashboardStore>().Save(principal, dashboardId, restoreVersion, draft, stars, note.Trim(), cancellationToken, layout);
            return JsonSerializer.Serialize(new { id, version = saved });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException(Refusal(e, "Saving dashboards is not available to this login")); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "P0002") { throw new McpException("dashboardId or restoreVersion names no saved dashboard"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "22023") { throw new McpException("The dashboard was not saved: " + e.MessageText); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23514") { throw new McpException("The dashboard does not fit the store's limits"); }
    }

    // A layout's references (docs/dev/screens.md 10.7), against the dashboard's newest layout when it has one (`current`,
    // or read here): the focus stars of new or refocused map parts must be stars this login can read, and new or changed
    // cards must name another saved dashboard. The reason, or null.
    static async Task<string?> LayoutProblem(IServiceProvider services, Guid principal, Guid? dashboard, DashboardLayout layout, DashboardLayout? current,
        CancellationToken ct)
    {
        var store = services.GetRequiredService<DashboardStore>();
        try
        {
            if (current is null && dashboard is { } id && await store.Get(principal, id, null, false, ct) is { } found)
            {
                using var read = JsonDocument.Parse(found);
                current = DashboardLayout.Of(read.RootElement).Layout;
            }
            return await layout.ReferenceProblem(current, dashboard,
                async stars => (await services.GetRequiredService<DashboardFeeds>().Stars(principal, stars, ct)).Keys.ToHashSet(StringComparer.Ordinal),
                async other => await store.Exists(principal, other, ct));
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Saving dashboards is not available to this login"); }
        catch (Exception e) when (e is not OperationCanceledException and not McpException and not Npgsql.PostgresException)
        { throw Unavailable(services, "save_dashboard", e, "Relationship graph unavailable; the dashboard was not saved"); }
    }

    // save_dashboard's third form: the newest version arranged in place, as its next layout revision. With
    // layoutRevision, refused when the layout changed since; without, it arranges whatever is there now (read again when
    // another change lands meanwhile).
    static async Task<string> Arrange(IServiceProvider services, Guid principal, Guid dashboard, DashboardLayout layout, int? layoutRevision, string note,
        CancellationToken ct)
    {
        var store = services.GetRequiredService<DashboardStore>();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var found = await store.Get(principal, dashboard, null, false, ct) ?? throw new McpException("dashboardId names no saved dashboard");
                int version, revision; DashboardLayout current;
                using (var read = JsonDocument.Parse(found))
                {
                    version = read.RootElement.GetProperty("version").GetInt32();
                    (current, revision) = DashboardLayout.Of(read.RootElement);
                }
                if (layoutRevision is { } asked && asked != revision)
                    throw new McpException($"The layout changed since layoutRevision {asked}: it is at {revision} now; read it again with list_dashboards and arrange that");
                if (await LayoutProblem(services, principal, dashboard, layout, current, ct) is { } refused) throw new McpException(refused);
                var saved = await store.SaveLayout(principal, dashboard, version, revision, layout, note, ct);
                if (saved["saved"]?.GetValue<bool>() == true)
                    return JsonSerializer.Serialize(new { id = dashboard, version, layoutRevision = saved["layoutRevision"]!.GetValue<int>() });
                if (layoutRevision is not null || attempt == 3)
                    throw new McpException("The layout changed meanwhile; read it again with list_dashboards and arrange that");
            }
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException(Refusal(e, "Arranging dashboards is not available to this login")); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "P0002") { throw new McpException("dashboardId names no saved dashboard"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "22023") { throw new McpException(e.MessageText + "; read the dashboard again with list_dashboards"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23514") { throw new McpException("The layout does not fit the store's limits"); }
    }

    [McpServerTool(Name = "list_dashboards", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> ListDashboards(IServiceProvider services, Guid? dashboardId = null, int? version = null, string? star = null, CancellationToken cancellationToken = default)
    {
        var store = services.GetRequiredService<DashboardStore>(); var principal = Principal();
        if (version is not null && dashboardId is null) throw new McpException("version needs dashboardId");
        try
        {
            if (dashboardId is { } id) return await store.Get(principal, id, version, true, cancellationToken) ?? throw new McpException("dashboardId or version names no saved dashboard");
            using var listed = JsonDocument.Parse(await store.List(principal, cancellationToken));
            // star keeps the dashboards that read it, and those with a map part focused near it.
            static bool Near(JsonElement d, string star) => d.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object
                && layout.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array
                && parts.EnumerateArray().Any(p => p.TryGetProperty("focus", out var focus) && focus.ValueKind == JsonValueKind.Object
                    && focus.TryGetProperty("near", out var near) && near.ValueKind == JsonValueKind.Object
                    && near.TryGetProperty("star", out var center) && center.ValueKind == JsonValueKind.String && center.GetString() == star);
            var dashboards = listed.RootElement.EnumerateArray().Where(d => star is null || Near(d, star)
                || d.GetProperty("stars").EnumerateArray().Any(s => s.GetProperty("graphId").GetString() == star)).ToArray();
            return JsonSerializer.Serialize(new { dashboards });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Dashboards are not available to this login"); }
    }

    [McpServerTool(Name = "delete_dashboard", ReadOnly = false, Destructive = true, OpenWorld = false)]
    public async Task<string> DeleteDashboard(IServiceProvider services, Guid dashboardId, string title, CancellationToken cancellationToken = default)
    {
        try { return await services.GetRequiredService<DashboardStore>().Delete(Principal(), dashboardId, title, cancellationToken); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException(Refusal(e, "Deleting dashboards is not available to this login")); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "P0002") { throw new McpException("dashboardId names no saved dashboard"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "22023") { throw new McpException("title doesn't match that dashboard's current title; nothing was deleted"); }
    }

    [McpServerTool(Name = "dashboard_history", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> DashboardHistory(IServiceProvider services, Guid? dashboardId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new McpException("limit is 1-200");
        try
        {
            using var writes = JsonDocument.Parse((await services.GetRequiredService<DashboardStore>().Writes(Principal(), dashboardId, null, limit, cancellationToken))!);
            return JsonSerializer.Serialize(new { writes = writes.RootElement });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Dashboards are not available to this login"); }
    }

    [McpServerTool(Name = "list_screens", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> ListScreens(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // Most recently seen first, which puts every open screen (heard from in the last minute) before the others; each
        // with its Home as it reports it.
        try
        {
            var screens = await services.GetRequiredService<ScreenStore>().List(Principal(), cancellationToken);
            foreach (var listed in screens) listed!["home"] = ScreenCommands.Home(listed["showing"]);
            return JsonSerializer.Serialize(new { screens });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Screens are not available to this login"); }
    }

    [McpServerTool(Name = "control_screen", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public async Task<string> ControlScreen(IServiceProvider services, ScreenAction action, string? screen = null, string? star = null, Guid? dashboard = null,
        string? layout = null, string? tile = null, string? to = null, string? corner = null, string? query = null, string? part = null, string? place = null,
        string? @with = null, int? x = null, int? y = null, string? size = null, int? w = null, int? h = null, MapFocus? focus = null,
        CancellationToken cancellationToken = default)
    {
        var principal = Principal();
        var named = JsonSerializer.Serialize(action).Trim('"');
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in new (string, object?)[] { ("star", star), ("dashboard", dashboard?.ToString()), ("layout", layout), ("tile", tile),
            ("to", to), ("corner", corner), ("query", query), ("part", part), ("place", place), ("with", @with), ("x", x), ("y", y), ("size", size),
            ("w", w), ("h", h), ("focus", focus) })
            if (Gave(name, value, named))
                given[name] = name == "focus" && Written(name) is { } written ? written
                    : value is MapFocus f ? JsonSerializer.SerializeToElement(f, DashboardLayout.Json) : JsonSerializer.SerializeToElement(value);
        if (ScreenCommands.Problem(named, given, out var args) is { } problem) throw new McpException(problem);
        var store = services.GetRequiredService<ScreenStore>();
        try
        {
            var (target, refused) = ScreenCommands.Target(await store.Seen(principal, cancellationToken), screen);
            if (target is null) throw new McpException(refused!);
            // With memory.scopes on, the target's login, against which what the command carries is checked too.
            var scope = await store.Scope(principal, target.Id, cancellationToken);
            (string Action, System.Text.Json.Nodes.JsonObject Args)? resolved;
            try
            {
                resolved = await ScreenCommands.Resolve(services.GetRequiredService<RelationshipHost>(), services.GetService<DashboardStore>(), principal, named,
                    args, target.Showing, cancellationToken, scope);
            }
            catch (ScreenRefused e) { throw new McpException(e.Message); }
            catch (Exception e) when (e is not OperationCanceledException) { throw Unavailable(services, "control_screen", e, "Relationship graph unavailable; nothing was sent"); }
            if (resolved is not { } command) throw new McpException("star is not a star on the map now; find stars with explore_graph");
            var sent = await store.Send(principal, target.Id, command.Action, command.Args, null, ScreenCommands.Outcome, cancellationToken);
            // Sent but not reported yet (still waiting to be taken, or taken and being shown) is waiting.
            var result = (string?)sent["result"] is "shown" or "failed" ? (string)sent["result"]! : "waiting";
            return JsonSerializer.Serialize(new { screen = new { id = target.Id, name = target.Name }, action = (string)sent["action"]!, result,
                detail = sent["detail"], ms = sent["ms"] });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Controlling screens is not available to this login"); }
        catch (Npgsql.PostgresException e) when (ScreenRefused.Is(e)) { throw new McpException(ScreenRefused.From(e).Message); }
    }

    [McpServerTool(Name = "discover_health_sources", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> DiscoverHealthSources(IServiceProvider services, string subject, DateOnly from, DateOnly until, string query = "", int offset = 0, CancellationToken cancellationToken = default)
        => Bounded(() => services.GetRequiredService<HealthInvestigation>().Discover(Principal(), Subject(subject), from, until, query, offset, cancellationToken));

    [McpServerTool(Name = "read_health_source", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> ReadHealthSource(IServiceProvider services, Guid resourceId, string pointer = "", int offset = 0, int limit = 20, bool inventory = false, CancellationToken cancellationToken = default)
        => Bounded(() => services.GetRequiredService<HealthInvestigation>().ReadSource(Principal(), resourceId, pointer, offset, limit, inventory, cancellationToken));

    [McpServerTool(Name = "investigate_health", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> InvestigateHealth(IServiceProvider services, string subject, DateOnly from, DateOnly until, string metrics = "", int limit = 3, string provider = "",
        [Description(GlucoseText)] string glucoseSource = "", [Description(CarelinkText)] string? carelinkSource = null, CancellationToken cancellationToken = default)
        => Bounded(() => services.GetRequiredService<HealthInvestigation>().Investigate(Principal(), Subject(subject), from, until, metrics, limit, cancellationToken, provider, GlucoseSource(glucoseSource, carelinkSource)));

    [McpServerTool(Name = "evaluate_health_hypothesis", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> EvaluateHealthHypothesis(IServiceProvider services, string subject, DateOnly from, DateOnly until, string exposure, string outcome, string mode = "daily", int lagDays = 0, int windowMinutes = 60, int delayMinutes = 0, string provider = "",
        [Description(GlucoseText)] string glucoseSource = "", [Description(CarelinkText)] string? carelinkSource = null, CancellationToken cancellationToken = default)
        => Bounded(() => services.GetRequiredService<HealthInvestigation>().Hypothesis(Principal(), Subject(subject), from, until, exposure, outcome, mode, lagDays, windowMinutes, delayMinutes, cancellationToken, provider, GlucoseSource(glucoseSource, carelinkSource)));

    [McpServerTool(Name = "save_document", ReadOnly = false, Destructive = false, OpenWorld = false)]
    public async Task<string> SaveDocument(IServiceProvider services, string fileId, string title, string content, DateTimeOffset modifiedAt, string? url = null, DateTimeOffset? checkedAt = null, string domain = "household", bool semanticIndex = true, string? subject = null, CancellationToken cancellationToken = default)
    {
        try { return await services.GetRequiredService<DocumentStore>().Save(Principal(), new(fileId, title, url, content, modifiedAt, checkedAt, domain, semanticIndex, subject), cancellationToken); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        // With memory.scopes on, a subject that is neither the caller's own person nor (for a Family reader) the household.
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501" && e.MessageText == DocumentStore.Elsewhere) { throw new McpException("The document was not saved: " + e.MessageText); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501") { throw new McpException("Saving documents is not available to this login"); }
        catch (Npgsql.PostgresException e) when (e.SqlState == "22023") { throw new McpException("The document was not saved: " + e.MessageText); }
    }

    // With engine.searchKnowledge combined (on an install with legacySearch false, RecallSettings) a question is answered
    // from the combined list, in the same rows and bounds; without it, and for an empty query, as before.
    [McpServerTool(Name = "search_knowledge", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> SearchKnowledge(IServiceProvider services, string query = "", int limit = 20, CancellationToken cancellationToken = default) =>
        RecallServing.Settings(services).CombinedSearchKnowledge && !string.IsNullOrWhiteSpace(query)
            ? Bounded(() => CombinedSearch(services, query, limit, cancellationToken))
            : Bounded(() => reader.Search(Principal(), query, limit, cancellationToken));

    // search_knowledge from the combined list: its bounds and their words first (Retrieval.Search), then up to 50 results,
    // each with search_knowledge's excerpt of up to 400 characters, as before: its own output budget, never the recall
    // tool's (whose first two strong results take 4000 characters each). Ranking and the receipt read none of these. It
    // stays a catalog tool: no native memory's row, its own or its star's, is a candidate (D-165), as in its other mode.
    static readonly RecallOptions SearchBudget = RecallOptions.Default with
    {
        MaxTopK = 50, FullMatches = 0, FullChars = RecallReply.SearchExcerpt, Snippet = RecallReply.SearchExcerpt,
        OutputBudget = 50 * RecallReply.SearchExcerpt, Memories = false,
    };
    async Task<string> CombinedSearch(IServiceProvider services, string query, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 50) throw new ArgumentException("Limit must be 1–50");
        if (query.Length > 200) throw new ArgumentException("Query too long");
        var found = await RecallServing.For(services, SearchBudget).Search(Principal(), new(query, limit), ct);
        return RecallReply.SearchRows(found);
    }

    // Combined retrieval (SB-P2-09): words and meaning in one ranked list, with explain and a receipt (RecallReply).
    // Listed and answered only with engine.recall on (RecallSettings).
    public const string RecallTool = "recall";
    [McpServerTool(Name = RecallTool, ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> RecallSearch(IServiceProvider services, string query, int topK = 5, int hops = 0, string domain = "", string provider = "",
        string kind = "", string subject = "", DateTimeOffset? after = null, DateTimeOffset? before = null, bool explain = false,
        CancellationToken cancellationToken = default)
    {
        if (!RecallServing.Settings(services).Tool)
            throw new McpException("recall is off in this install (engine.recall); search_knowledge and semantic_search answer instead");
        var asked = new RecallQuery(query, topK, hops, domain, provider, kind, subject, after, before);
        var principal = Principal();
        try { return RecallReply.Answer(await RecallServing.For(services).Search(principal, asked, cancellationToken), asked, explain).ToJsonString(); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    // recall with as_of (SB-P4-07, Core/RecallAsOf.cs): the same tool and the same answer, and with as_of what was true at
    // that moment instead of now. Made in RecallSearch's place only with memory.tools on (Create), so with it off the tool,
    // its listing and every answer are RecallSearch's, and a call naming as_of is told it is not an argument of recall.
    // And standing instructions (SB-P5-12, Core/MemoryStanding.cs): without as_of the answer shows those the question
    // fires, at most 2, each with where it came from; project is the project the question is about, which lets that
    // project's instructions fire and narrows nothing else. Fired or not, the receipt is RecallSearch's, over the results;
    // none fired, so is the whole answer. As of a moment nothing fires, and project asks nothing of it.
    [McpServerTool(Name = RecallTool, ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<string> RecallAsOf(IServiceProvider services, string query, int topK = 5, int hops = 0, string domain = "", string provider = "",
        string kind = "", string subject = "", DateTimeOffset? after = null, DateTimeOffset? before = null, bool explain = false,
        DateTimeOffset? as_of = null, string? project = null, CancellationToken cancellationToken = default)
    {
        if ((as_of is not null || project is not null) && !MemoryTools.On(services))
            throw new McpException("as_of and project are answered only with the memory tools on (memory.tools)");
        if (as_of is null && !MemoryTools.On(services)) return await RecallSearch(services, query, topK, hops, domain, provider, kind, subject, after, before, explain, cancellationToken);
        if (!RecallServing.Settings(services).Tool)
            throw new McpException("recall is off in this install (engine.recall); search_knowledge and semantic_search answer instead");
        var asked = new RecallQuery(query, topK, hops, domain, provider, kind, subject, after, before, as_of);
        var principal = Principal();
        try
        {
            var recall = RecallServing.For(services);
            var found = await recall.Search(principal, asked, cancellationToken);
            if (as_of is not null) return RecallReply.Answer(found, asked, explain).ToJsonString();
            var (standing, said) = await MemoryTools.Standing(services, principal, recall.Embedder, query, project, cancellationToken);
            return RecallReply.Answer(said is null ? found : found with { Notices = [.. found.Notices, said] }, asked, explain, standing).ToJsonString();
        }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    // A prompt capsule (SB-P3-08, Core/Capsules.cs): one scope's core or project capsule and its strong ETag, upstream's
    // names and shape (CapsuleReply). Listed and answered only with memory.capsules on (MemorySettings.CapsulesOn).
    public const string CapsuleTool = "get_prompt_capsule";
    [McpServerTool(Name = CapsuleTool, ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<CallToolResult> GetPromptCapsule(IServiceProvider services, string kind, string project_id = "", string workspace = "",
        string if_none_match = "", CancellationToken cancellationToken = default)
    {
        if (!CapsuleReply.On(services))
            throw new McpException("get_prompt_capsule is off in this install (memory.capsules)");
        return CapsuleReply.Tool(new PromptCapsules(services.GetRequiredService<Database>().Source), Principal(),
            new(kind, project_id, workspace), if_none_match, cancellationToken);
    }

    [McpServerTool(Name = "search_resources", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> SearchResources(string query = "", string domain = "", string provider = "", string kind = "", string subject = "", int limit = 20, CancellationToken cancellationToken = default) => Bounded(() => reader.SearchResources(Principal(), query, domain, provider, kind, subject, limit, cancellationToken));

    [McpServerTool(Name = "semantic_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public async Task<CallToolResult> SemanticSearch(IServiceProvider services, string query, string domain = "", int limit = 5, CancellationToken cancellationToken = default)
    {
        var model = services.GetRequiredService<Embeddings>().Model ?? throw new McpException("Semantic search is off: the local model is not available in this install");
        // Only the texts the settings list now (adapters.embeddings.sources) answer, besides the copies; a live source's
        // passages only while the source still holds their revision.
        var serving = PassageSources.For(EmbeddingSettings.From(services.GetRequiredService<RhythmConfig>()),
            services.GetRequiredService<Adapters.AdapterRegistry>().Enabled, true);
        var search = new SemanticSearch(services.GetRequiredService<Database>(), model, serving, services.GetRequiredService<ISourceValidation>());
        var rows = await Bounded(() => search.Search(Principal(), query, domain, limit, cancellationToken));
        // The passages, as before; when a live source's checks ran out of time, a second text says so (a count, never a
        // title), so a client knows the answer may be short rather than taking it as all there is.
        return new CallToolResult
        {
            Content = search.Unchecked == 0 ? [new TextContentBlock { Text = rows }]
                : [new TextContentBlock { Text = rows }, new TextContentBlock { Text = UncheckedNotice(search.Unchecked) }],
        };
    }
    internal static string UncheckedNotice(int files) =>
        $"{files} connected file{(files == 1 ? "" : "s")} left out: the source did not confirm within {Knowledge.SemanticSearch.CheckBudget.TotalSeconds:0} s "
        + "that the passages found are of its current version. Ask again, or open the file at its source.";

    [McpServerTool(Name = "get_resource", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> GetResource(Guid resourceId, CancellationToken cancellationToken = default) => reader.Resource(Principal(), resourceId, cancellationToken);

    [McpServerTool(Name = "get_related_resources", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> GetRelatedResources(Guid resourceId, int limit = 20, CancellationToken cancellationToken = default) => Bounded(() => reader.Related(Principal(), resourceId, limit, cancellationToken));

    [McpServerTool(Name = "query_health_metrics", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> QueryHealthMetrics(string subject, string metric, DateOnly from, DateOnly until, int limit = 200, string provider = "",
        [Description(GlucoseText)] string glucoseSource = "", [Description(CarelinkText)] string? carelinkSource = null, CancellationToken cancellationToken = default)
        => Bounded(() => reader.Metrics(Principal(), Subject(subject), metric, from, until, limit, cancellationToken, provider, GlucoseSource(glucoseSource, carelinkSource)));

    [McpServerTool(Name = "compare_health_trends", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> CompareHealthTrends(string subject, string firstMetric, string secondMetric, DateOnly from, DateOnly until, string provider = "",
        [Description(GlucoseText)] string glucoseSource = "", [Description(CarelinkText)] string? carelinkSource = null, CancellationToken cancellationToken = default)
        => Bounded(() => reader.Trends(Principal(), Subject(subject), firstMetric, secondMetric, from, until, cancellationToken, provider, GlucoseSource(glucoseSource, carelinkSource)));

    [McpServerTool(Name = "get_sync_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    public Task<string> GetSyncStatus(CancellationToken cancellationToken = default) => reader.Status(Principal(), cancellationToken);

    // A bound the tool cannot take (a limit, more than 365 days, a pointer) is the caller's to fix, so its reason is the
    // answer: the SDK answers an ArgumentException with its generic "An error occurred invoking" and drops the reason.
    static async Task<string> Bounded(Func<Task<string>> read)
    {
        try { return await read(); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    static string Subject(string subject) => RhythmConfig.Current.IsSubject(subject) || subject == RhythmConfig.Synthetic ? subject
        : throw new McpException($"subject is {DashboardWrite.SubjectList}");

    // ---------------------------------------------------------------------------------------------------------------

    // Old names accepted on tools/call (and listed with engine.legacyToolNames): contracts/README.md 2.3.
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["save_household_document"] = "save_document",
    };
    // Tools that exist only while their gate is open: semantic_search needs the local model.
    public static bool Open(string tool, IServiceProvider services) =>
        tool != "semantic_search" || services.GetService<Embeddings>()?.Model is not null;

    // Every harness tool, with its description filled from the install's settings, plus aliases and enabled adapters' tools,
    // each with its arguments checked against its schema first (CheckedTool).
    public static IReadOnlyList<McpServerTool> Create(RhythmConfig config, IEnumerable<McpServerTool> adapterTools)
    {
        var tools = new List<McpServerTool>();
        foreach (var method in typeof(HarnessTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { } name }) continue;
            // recall is RecallSearch, or with memory.tools on RecallAsOf, which takes as_of besides (SB-P4-07).
            if (method.Name == (MemorySettings.ToolsOn(config) ? nameof(RecallSearch) : nameof(RecallAsOf))) continue;
            var description = ToolText.Describe(name, config);
            McpServerTool Make(string toolName, string text) => McpServerTool.Create(method, ctx =>
            {
                var tools = ActivatorUtilities.CreateInstance<HarnessTools>(ctx.Services!);
                tools.Arguments = ctx.Params?.Arguments is { } written ? new Dictionary<string, JsonElement>(written, StringComparer.Ordinal) : null;
                return tools;
            }, new McpServerToolCreateOptions { Name = toolName, Description = text });
            tools.Add(Make(name, description));
            foreach (var (alias, target) in Aliases.Where(a => a.Value == name))
                tools.Add(Make(alias, $"Old name of {target}; the same tool. {description}"));
        }
        // The memory tools (SB-P3-09), made only with memory.tools on: off, none exists, so a call naming one is an
        // unknown tool as before.
        if (MemorySettings.ToolsOn(config)) tools.AddRange(MemoryTools.Create(config));
        tools.AddRange(adapterTools);
        return [.. tools.Select(t => new CheckedTool(t))];
    }

    // tools/list: aliases only with engine.legacyToolNames, gated tools only while open, recall only with engine.recall on,
    // get_prompt_capsule only with memory.capsules on, the memory tools only with memory.tools on, and share only with
    // memory.share on as well (SB-P3-10).
    public static ListToolsResult Filter(ListToolsResult result, IServiceProvider services, RhythmConfig config)
    {
        result.Tools = result.Tools.Where(t => (config.Engine.LegacyToolNames || !Aliases.ContainsKey(t.Name)) && Open(t.Name, services)
            && (t.Name != RecallTool || RecallSettings.From(config).Tool)
            && (t.Name != CapsuleTool || MemorySettings.CapsulesOn(config))
            && (!MemoryTools.Names.Contains(t.Name) || MemorySettings.ToolsOn(config))
            && (t.Name != MemoryTools.ShareTool || MemorySettings.ShareOn(config))).ToList();
        return result;
    }
}
