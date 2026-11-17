using System.Globalization;
using System.Text.Json.Nodes;

namespace Knowledge.Mcp;

// app_memories' ops for the full memory UI's list, projects, AI changes, capsule status and settings (SB-P6-01; the rest of
// the tool is AppMemories.cs, whose header says what every op keeps to: the channel fixed as the app's, a person's, only with
// memory.tools on, each through the store's checked functions).
//
// tags: the tags on the memories a list with these filters holds, each with how many carry it, and the list's whole length
// (MemoryStore.Tags and Count, through the predicates the list takes). changes: an AI client's groups of changes as this
// caller reads the audit trail (MemoryStore.Changes, channel mcp), the brief's rows with a person's preview of each memory,
// a held one without its words; undo_group takes one back through UndoGroup, GroupPage memories a call. forget_all,
// restore_all and delete_all, and with memory.share on share_all: several at once, a person's steps, each its own write
// answered on its own (MemoryBulk.cs). project_create, project_update and project_delete: the registry's checked writers
// (migration 035), which nothing else calls. capsule_status: a capsule as its AI clients are served it, slot by slot, with
// the memories in its slots that wait (PromptCapsules Build and Waiting), and no words. settings: what the install's memory
// settings are, to read; they change in rhythm.yaml.
public static partial class AppMemories
{
    // How much a changes answer carries: the newest groups, and in each its newest memories (the group's counts name them all).
    public const int ChangeGroupsMax = 50, ChangeMemoriesMax = 20, ChangePreview = 120, TagsMax = 200;

    // A filter's tags and the list's length (Tags, Count): one read of each, by the list's own predicates.
    static async Task<JsonObject> Tagged(MemoryStore store, Guid principal, MemoryFilter filter, CancellationToken ct)
    {
        var tags = await store.Tags(principal, filter, ct);
        return new()
        {
            ["tags"] = new JsonArray([.. tags.Take(TagsMax).Select(t => (JsonNode)new JsonObject { ["tag"] = t.Tag, ["count"] = t.Count })]),
            ["truncated"] = tags.Count > TagsMax,
            ["count"] = await store.Count(principal, filter, ct),
        };
    }

    // The AI clients' changes this caller reads, newest group first; a group's key is what undo_group takes. NotHeld: leaves out the
    // changes of a memory held now, as the brief does, so its count is this list's and a group here undoes what it counts.
    static async Task<JsonObject> Changes(MemoryStore store, Guid principal, DateTimeOffset? since, string? scope, bool notHeld, CancellationToken ct)
    {
        var found = await store.Changes(principal, since, MemoryTools.Channel, ct, notHeld, scope);
        var groups = found.Groups.Take(ChangeGroupsMax).ToList();
        var rows = groups.Select(g => (Group: g, Rows: g.Items.GroupBy(i => i.Memory).Take(ChangeMemoriesMax).ToList())).ToList();
        var previews = await store.Previews(principal, [.. rows.SelectMany(g => g.Rows.Select(r => r.Key)).Distinct()], ChangePreview, ct);
        return new()
        {
            ["groups"] = new JsonArray([.. rows.Select(g => (JsonNode)new JsonObject
            {
                ["group"] = g.Group.Group, ["family"] = g.Group.Family, ["person"] = g.Group.Person, ["client"] = g.Group.Client,
                ["changes"] = g.Group.Changes, ["memories"] = g.Group.Memories, ["from"] = g.Group.From, ["until"] = g.Group.Until, ["capped"] = g.Group.Capped,
                ["items"] = new JsonArray([.. g.Rows.Select(r =>
                {
                    var newest = r.First();
                    var preview = previews.GetValueOrDefault(r.Key);
                    return (JsonNode)new JsonObject
                    {
                        ["id"] = r.Key.ToString(), ["revision"] = newest.Revision, ["event"] = newest.Event, ["at"] = newest.At, ["changes"] = r.Count(),
                        ["preview"] = preview?.Content, ["truncated"] = preview?.Truncated ?? false, ["held"] = preview?.Hold is not null,
                    };
                })]),
            })]),
            ["truncated"] = found.Truncated || found.Groups.Count > ChangeGroupsMax,
            ["since"] = found.Since,
            // How many groups the read found, of which the newest are shown: read with notHeld, the brief's count.
            ["count"] = found.Groups.Count,
        };
    }

    // One call's work on a group: each memory it reached and how that went, what is left for the next call with the same key,
    // and the key of the group this caller's own undos make, which undoes them.
    static JsonObject Regrouped(MemoryGroupUndo g) => new()
    {
        ["op"] = "undo_group", ["group"] = g.Group, ["total"] = g.Total, ["remaining"] = g.Remaining, ["done"] = g.Done, ["capped"] = g.Capped,
        ["results"] = new JsonArray([.. g.Results.Select(r => (JsonNode)new JsonObject { ["id"] = r.Memory.ToString(), ["result"] = r.Result })]),
        ["redo"] = g.Redo,
    };

    static JsonObject ForgottenAll(IReadOnlyList<MemoryForgetResult> results) => new()
    {
        ["op"] = "forget_all",
        ["results"] = new JsonArray([.. results.Select(r => (JsonNode)Cascaded(new()
        {
            ["id"] = r.Id.ToString(), ["result"] = r.Result, ["revision"] = r.Revision, ["nonce"] = r.Nonce?.ToString(), ["days"] = r.Days,
        }, r.Cascade))]),
    };

    static JsonObject RestoredAll(IReadOnlyList<MemoryRestoreResult> results) => new()
    {
        ["op"] = "restore_all",
        ["results"] = new JsonArray([.. results.Select(r => (JsonNode)Cascaded(new()
        {
            ["id"] = r.Id.ToString(), ["result"] = r.Result, ["revision"] = r.Revision, ["held"] = Held(r.Hold),
        }, r.Cascade))]),
    };

    static JsonObject DeletedAll(IReadOnlyList<MemoryDeleteResult> results) => new()
    {
        ["op"] = "delete_all",
        ["results"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id.ToString(), ["result"] = r.Result })]),
    };

    // Several shares (MemoryStore.ShareAll), behind the same switch as one (AppEndpoint takes the op only with memory.share
    // on). The map is asked for one rebuild within the share tool's bound, if any moved, as one share asks.
    static async Task<JsonObject> SharedAll(IServiceProvider services, MemoryStore store, Guid principal, IReadOnlyList<(Guid Id, int Revision)> memories, string? scope,
        MemoryCall call, CancellationToken ct)
    {
        if (!MemoryTools.ShareOn(services)) throw new ArgumentException("Sharing is off in this install (memory.share)");
        var results = await store.ShareAll(principal, memories, scope, call, ct);
        if (results.Any(r => r.Result == "moved"))
            try { await new RefreshRequests(services.GetRequiredService<Database>().Source).QueueBounded(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { services.GetService<RequestLog>()?.Failed("app", Name, 503, "rebuild", e); }
        return new()
        {
            ["op"] = ShareAllOp,
            ["results"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id.ToString(), ["result"] = r.Result, ["revision"] = r.Revision, ["projectLeft"] = r.ProjectLeft,
            })]),
        };
    }

    // A project as the registry holds it (the projects read and each project write answer it alike).
    static JsonObject ProjectRow(MemoryProject p) => new()
    {
        ["id"] = p.Id.ToString(), ["slug"] = p.Slug, ["name"] = p.Name, ["scope"] = p.Scope, ["owner"] = p.Subject,
        ["aliases"] = Strings(p.Aliases), ["status"] = p.Status, ["description"] = p.Description, ["author"] = p.Author, ["revision"] = p.Revision,
        ["createdAt"] = p.CreatedAt, ["updatedAt"] = p.UpdatedAt,
    };

    static JsonObject ProjectWritten(string op, MemoryProjectWrite w) => new()
    {
        ["op"] = op, ["project"] = ProjectRow(w.Project), ["scopes"] = w.Scopes, ["changed"] = Strings(w.Changed), ["unchanged"] = w.Unchanged,
    };

    static JsonObject ProjectDeleted(MemoryProjectDeleted d) => new()
    {
        ["op"] = "project_delete", ["id"] = d.Id.ToString(), ["slug"] = d.Slug, ["owner"] = d.Subject, ["memories"] = d.Memories, ["scopes"] = d.Scopes,
    };

    // A capsule slot by slot, in its fixed order, as its AI clients are served it and what waits in each: filled by the one
    // memory that fills it, duplicate when two or more do and none is served, omitted when the slots before it took the
    // budget, empty when no memory fills it; invalid and waiting name the memories in it that it leaves out. No words. Served:
    // whether AI clients are offered capsules at all (memory.capsules); a person reads this either way.
    static async Task<JsonObject> CapsuleStatus(IServiceProvider services, Guid principal, string kind, string? project, string? scope, CancellationToken ct)
    {
        var capsules = new PromptCapsules(services.GetRequiredService<Database>().Source);
        var request = new CapsuleRequest(kind, project, scope);
        var payload = (await capsules.Build(principal, request, ct)).Payload;
        var waiting = await capsules.Waiting(principal, request, ct);
        IEnumerable<JsonNode> Rows(string name) => payload[name]!.AsArray().Select(n => n!);
        var filled = Rows("sections").ToDictionary(s => (string)s["slot"]!, s => (string)s["source_entry_id"]!, StringComparer.Ordinal);
        var both = Rows("duplicate_slots").ToDictionary(d => (string)d["slot"]!, d => d["entry_ids"]!.AsArray().Select(e => (string)e!).ToArray(), StringComparer.Ordinal);
        var omitted = Rows("omitted_slots").Select(s => s.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var invalid = Rows("invalid_entries").ToList();
        return new()
        {
            ["kind"] = kind,
            ["project"] = (string?)payload["project_id"],
            ["scope"] = (string)payload["scope"]!,
            ["complete"] = (bool)payload["complete"]!,
            ["populated"] = (bool)payload["populated"]!,
            ["hash"] = (string)payload["prompt_hash"]!,
            ["chars"] = (int)payload["char_count"]!,
            ["maxChars"] = (int)payload["max_chars"]!,
            ["served"] = CapsuleReply.On(services),
            ["slots"] = new JsonArray([.. CapsuleSlots.For(kind).Select(slot => (JsonNode)new JsonObject
            {
                ["slot"] = slot,
                ["state"] = both.ContainsKey(slot) ? "duplicate" : omitted.Contains(slot) ? "omitted" : filled.ContainsKey(slot) ? "filled" : "empty",
                ["filledBy"] = filled.GetValueOrDefault(slot),
                ["duplicates"] = Strings(both.GetValueOrDefault(slot) ?? []),
                ["invalid"] = new JsonArray([.. invalid.Where(i => (string)i["slot"]! == slot).Select(i => (JsonNode)new JsonObject
                    { ["id"] = (string)i["entry_id"]!, ["reason"] = (string)i["reason"]! })]),
                ["waiting"] = new JsonArray([.. waiting.Where(w => w.Slot == slot).Select(w => (JsonNode)new JsonObject
                    { ["id"] = w.Id.ToString(), ["status"] = w.Status, ["held"] = w.Held, ["expired"] = w.Expired })]),
            })]),
        };
    }

    // The install's memory settings, to read: memory.scopes as the database records it, the switches rhythm.yaml sets, this
    // reader's person (none for the household view), how long the trash keeps a memory and how many of its earlier states are
    // kept. They change in rhythm.yaml, never here; nothing here is a secret, each is what the tool's listing already implies.
    static async Task<JsonObject> Settings(IServiceProvider services, Guid principal, CancellationToken ct)
    {
        var config = services.GetService<RhythmConfig>() ?? (RhythmConfig.Loaded ? RhythmConfig.Current : null);
        var recall = RecallServing.Settings(services);
        var source = services.GetRequiredService<Database>().Source;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT NOT coalesce({Database.ScopesOff},false),(SELECT person_id FROM principals WHERE id=@principal),memory_trash_days(),memory_versions_kept()
            """, ("principal", principal));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new()
        {
            ["scopes"] = r.GetBoolean(0) ? "on" : "off",
            ["person"] = r.IsDBNull(1) ? null : r.GetString(1),
            ["share"] = MemoryTools.ShareOn(services),
            ["work"] = MemoryTools.WorkOn(services),
            ["capsules"] = CapsuleReply.On(services),
            ["capture"] = config is not null && MemorySettings.CaptureOn(config),
            ["secondBrainImport"] = config is not null && MemorySettings.SecondBrainImportOn(config),
            ["recall"] = recall.Tool,
            ["recallLog"] = recall.Log,
            ["nightSummary"] = config is not null && NightlySettings.From(config).Summary,
            ["trashDays"] = r.GetInt32(2),
            ["versionsKept"] = r.GetInt32(3),
            ["maxBytes"] = MemoryInput.MaxBytes,
        };
    }

    // An instant as a call names one; the listing's string, at most 40 characters.
    static bool IsInstant(string? value) =>
        value is { Length: <= 40 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    static DateTimeOffset? Instant(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
