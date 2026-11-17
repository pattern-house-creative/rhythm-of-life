using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// A closed selection vocabulary, never a script, selector, function name or URL.
public sealed record DashboardControl(string Id, string Label, string Kind, string[]? Options = null, JsonElement? Initial = null)
{
    public static string? Problem(IReadOnlyList<DashboardControl>? controls)
    {
        if (controls is null) return null;
        if (controls.Any(c => c is null)) return "controls contains a missing entry";
        if (controls.Count > 8 || controls.Select(c => c.Id).Distinct().Count() != controls.Count) return "controls has at most eight distinct ids";
        foreach (var c in controls)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(c.Id ?? "", "^[a-z][a-z0-9_]{0,31}$") || string.IsNullOrWhiteSpace(c.Label)
                || c.Label.Length > 60 || c.Kind is not ("tab" or "filter" or "week" or "date_range")) return "A control needs id, label and kind tab, filter, week or date_range";
            if (c.Kind is "tab" or "filter")
            {
                if (c.Options is null || c.Options.Length is < 1 or > 20 || c.Options.Distinct().Count() != c.Options.Length
                    || c.Options.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 60)) return "tab/filter options lists 1-20 distinct labels of at most 60 characters";
            }
            else if (c.Options is not null) return "Only tabs and filters have options";
            if (c.Initial is not { } initial || !c.Accepts(initial)) return "Each control needs a valid initial selection";
        }
        return null;
    }
    public bool Accepts(JsonElement value) => Kind switch
    {
        "tab" or "filter" => value.ValueKind == JsonValueKind.String && Options?.Contains(value.GetString()) == true,
        "week" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n is >= -520 and <= 520,
        "date_range" => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 2
            && value.TryGetProperty("from", out var from) && value.TryGetProperty("to", out var to)
            && from.ValueKind == JsonValueKind.String && to.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(from.GetString(), "yyyy-MM-dd", out var f) && DateOnly.TryParseExact(to.GetString(), "yyyy-MM-dd", out var t) && f <= t,
        _ => false,
    };
}

public sealed class StudioStore(NpgsqlDataSource source, DashboardStore dashboards, DashboardFeeds feeds, RelationshipHost host)
{
    readonly DerivedScopes scopes = new(source, host);
    public async Task<JsonObject> Call(Guid principal, string op, Guid id, string? screen, Guid? request, int? revision, JsonObject? payload, CancellationToken ct)
    {
        payload ??= [];
        if (op is not ("begin" or "read" or "update" or "progress" or "cancel" or "discard" or "save" or "feedback" or "validate")) throw new ArgumentException("Unknown Studio operation");
        if (op != "read" && request is null) throw new ArgumentException("requestId is required for changes");
        if (screen is not null && !ScreenCommands.Id(screen)) throw new ArgumentException("screen must be a screen id from list_screens");
        if (op is "progress" or "feedback" && (payload[op == "feedback" ? "text" : "message"]?.GetValue<string>() is not { Length: > 0 and <= 1000 }))
            throw new ArgumentException("Feedback and progress contain 1-1000 characters");
        if (op == "update")
        {
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString())));
            payload = await Prepare(principal, payload, ct); payload["fingerprint"] = fingerprint;
        }
        if (op == "save") return await Publish(principal, id, screen, request!.Value, revision, ct);
        await using var cmd = source.CreateCommand("SELECT studio_change(@p,@id,@screen,@op,@request,@revision,@payload::jsonb,@stars)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", id);
        Add(cmd, "screen", NpgsqlDbType.Text, screen); cmd.Parameters.AddWithValue("op", op);
        Add(cmd, "request", NpgsqlDbType.Uuid, request); Add(cmd, "revision", NpgsqlDbType.Integer, revision);
        cmd.Parameters.AddWithValue("payload", payload.ToJsonString()); cmd.Parameters.Add(DerivedScopes.Parameter("stars", await scopes.Stars(principal, ct)));
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!)!.AsObject();
    }

    // Both candidate and saved content go through the existing feed, source and layout policy.
    async Task<JsonObject> Prepare(Guid principal, JsonObject payload, CancellationToken ct)
    {
        if (payload.Any(x => x.Key is not ("draft" or "controls"))) throw new ArgumentException("update takes draft and controls only");
        var input = payload["draft"]?.AsObject() ?? throw new ArgumentException("update needs a complete draft");
        if (input.Any(x => x.Key is not ("title" or "description" or "html" or "note" or "feeds" or "writes" or "layout"))) throw new ArgumentException("Unknown draft member");
        var draft = input.Deserialize<DashboardDraft>(DashboardDraft.Json) ?? throw new ArgumentException("draft is required");
        if (draft.Problem() is { } wrong) throw new ArgumentException(wrong);
        var controls = payload["controls"]?.Deserialize<DashboardControl[]>(DashboardDraft.Json) ?? [];
        if (DashboardControl.Problem(controls) is { } problem) throw new ArgumentException(problem);
        DashboardLayout? layout = null;
        if (input["layout"] is { } raw)
        {
            if (DashboardLayout.Read(JsonSerializer.SerializeToElement(raw), out layout) is { } error) throw new ArgumentException(error);
            if (await layout!.ReferenceProblem(null, null, async stars => (await feeds.Stars(principal, stars, ct)).Keys.ToHashSet(), async d => await dashboards.Exists(principal, d, ct)) is { } bad)
                throw new ArgumentException(bad);
        }
        // As save_dashboard: a memories feed names no star, and a draft that has one needs none (DashboardDraft.Unanchored).
        if (feeds.Unserved(draft.Feeds) is { } off) throw new ArgumentException(off);
        var named = draft.Feeds.Where(f => f.Kind is not ("log" or "memories")).Select(f => f.Star)
            .Concat((draft.Writes ?? []).Where(w => w.Kind != "log").Select(w => w.Star!))
            .Concat((layout?.Parts ?? []).Select(p => p.Focus?.Near?.Star).OfType<string>()).Distinct().ToArray();
        if (named.Length > 12 || DashboardDraft.Unanchored(draft.Feeds, named) is not null) throw new ArgumentException("A draft names 1-12 readable stars");
        if (await feeds.UnknownProjects(principal, draft.Feeds, ct) is { } unknown) throw new ArgumentException(unknown);
        var known = await feeds.Stars(principal, named, ct);
        if (named.Any(n => !known.ContainsKey(n))) throw new ArgumentException("A draft names a star you cannot read");
        var subject = await scopes.LogSubject(principal, ct);
        var result = JsonSerializer.SerializeToNode(draft with {
            Feeds = draft.Feeds.Select(f => f.Normalized(subject)).ToArray(), Writes = (draft.Writes ?? []).Select(w => w.Normalized(subject)).ToArray()
        }, DashboardDraft.Json)!.AsObject();
        result["stars"] = JsonSerializer.SerializeToNode(named.Select(n => new DashboardStar(n, known[n])), DashboardDraft.Json);
        if (layout is not null) result["layout"] = JsonNode.Parse(layout.ToJson());
        var (_, cited) = await scopes.Citing(principal, named, ct);
        if (cited is not null) result["cited"] = cited;
        return new() { ["draft"] = result, ["controls"] = JsonSerializer.SerializeToNode(controls, DashboardDraft.Json) };
    }

    async Task<JsonObject> Publish(Guid principal, Guid id, string? screen, Guid request, int? revision, CancellationToken ct)
    {
        var session = await Call(principal, "read", id, screen, null, null, null, ct);
        if (session["status"]?.GetValue<string>() == "saved") return session["result"]!.DeepClone().AsObject();
        var version = session["candidate"]?.DeepClone().AsObject() ?? throw new ArgumentException("No candidate to save");
        // Recheck the owner's access now, including revoked principals, at publication. The DB locks and compares bases.
        await using var owner = source.CreateCommand("SELECT owner FROM studio_sessions WHERE id=@id");
        owner.Parameters.AddWithValue("id", id);
        var author = (Guid)(await owner.ExecuteScalarAsync(ct))!;
        if (version["layout"] is { } rawLayout)
        {
            var layout = rawLayout.Deserialize<DashboardLayout>(DashboardLayout.Json)!;
            if (await layout.ReferenceProblem(null, session["dashboard"]?.GetValue<Guid>(), async ids => (await feeds.Stars(author, ids, ct)).Keys.ToHashSet(),
                async d => await dashboards.Exists(author, d, ct)) is { } problem) throw new ArgumentException(problem);
        }
        var named = version["stars"]!.AsArray().Select(s => s!["graphId"]!.GetValue<string>()).ToArray();
        var (stars, cited) = await scopes.Citing(author, named, ct);
        if (cited is not null) version["cited"] = cited;
        await using var cmd = source.CreateCommand("SELECT studio_publish(@p,@id,@screen,@request,@revision,@version::jsonb,@stars,@view_stars)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", id);
        Add(cmd, "screen", NpgsqlDbType.Text, screen); cmd.Parameters.AddWithValue("request", request);
        Add(cmd, "revision", NpgsqlDbType.Integer, revision); cmd.Parameters.AddWithValue("version", version.ToJsonString());
        cmd.Parameters.Add(DerivedScopes.Parameter("stars", stars));
        cmd.Parameters.Add(DerivedScopes.Parameter("view_stars", await scopes.Stars(principal, ct)));
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!)!.AsObject();
    }

    public async Task<JsonObject?> Active(Guid principal, string screen, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT id FROM studio_sessions WHERE screen=@screen AND status='active' AND expires_at>now()");
        cmd.Parameters.AddWithValue("screen", screen);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? await Call(principal, "read", id, screen, null, null, null, ct) : null;
    }
    public async Task<JsonObject> Data(Guid principal, Guid id, string screen, int revision, CancellationToken ct)
    {
        var session = await Call(principal, "read", id, screen, null, null, null, ct);
        var stored = session["revision"]!.GetValue<int>() == revision ? session["candidate"]
            : session["valid_revision"]!.GetValue<int>() == revision ? session["preview"] : null;
        var draft = stored?.Deserialize<DashboardDraft>(DashboardDraft.Json) ?? throw new ArgumentException("Draft revision changed");
        return await feeds.Read(principal, draft.Feeds, false, ct);
    }
    public async Task<JsonArray> Controls(Guid principal, Guid dashboard, int version, CancellationToken ct)
    {
        if (await dashboards.Get(principal, dashboard, version, false, ct) is null) throw new ArgumentException("Unknown dashboard version");
        await using var cmd = source.CreateCommand("SELECT manifest::text FROM dashboard_controls WHERE dashboard=@d AND version=@v");
        cmd.Parameters.AddWithValue("d", dashboard); cmd.Parameters.AddWithValue("v", version);
        return await cmd.ExecuteScalarAsync(ct) is string found ? JsonNode.Parse(found)!.AsArray() : [];
    }
    static void Add(NpgsqlCommand cmd, string name, NpgsqlDbType type, object? value) => cmd.Parameters.AddWithValue(name, type, value ?? DBNull.Value);
}
