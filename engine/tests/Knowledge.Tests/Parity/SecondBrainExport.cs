using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Knowledge.Tests.Parity;

// A synthetic Second Brain deployment (sb-export-brain.json) and the export files taken from it (sb-export-*.json), for
// the Second Brain importer (SB-P8-01) and its comparison harness (SB-P8-03). The brain holds upstream's own rows
// (db/schema.sql and the runtime columns of src/db/init.ts at the pinned commit), with times written as ISO strings; each
// export file is what upstream's GET /export (src/routes/entries.ts) returns for one member at one moment, which
// UpstreamExport.Take derives and SecondBrainExportTests checks. `key`, `note` and `expect` are this fixture's own. All of
// it is DEMO data: invented people, places, numbers and hosts.
public sealed record SbUser(string Id, string Name, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset? RemovedAt);
// Kind is personal or company; a company workspace is a team, the first of them the deployment's own (here "Family").
public sealed record SbWorkspace(string Id, string Kind, string Name, DateTimeOffset CreatedAt);
public sealed record SbMembership(string UserId, string WorkspaceId, string Role, DateTimeOffset CreatedAt);
public sealed record SbProject(string Id, string WorkspaceId, string Name, string Description, string[] Aliases, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);
// What upstream does with a row (held or served) and what Rhythm's import should do: held, served, or open where SB-P8-01
// still decides. Reason is upstream's hold reason (src/quarantine/tags.ts), which Rhythm keeps at its boundary (D-030).
public sealed record SbExpect(string Upstream, string Rhythm, string? Reason, string Note);
public sealed record SbEntry(string Key, string Id, string Content, string[] Tags, string Source, DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt, string[] VectorIds, int RecallCount, int ImportanceScore, int ContradictionWins, int ContradictionLosses,
    string WorkspaceId, string ActorId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, DateTimeOffset? WhenAt, string? WhenKind,
    string? WhenSource, string? WhenLabel, DateTimeOffset? StalenessCheckedAt, SbExpect? Expect = null, string? Note = null)
{
    public string? HeldReason => SbTags.HeldReason(Tags);
    public string? Kind => Tags.FirstOrDefault(t => t.StartsWith("kind:", StringComparison.Ordinal))?[5..];
    public string? Status => Tags.FirstOrDefault(t => t.StartsWith("status:", StringComparison.Ordinal))?[7..];
}
public sealed record SbEdge(string Id, string SourceId, string TargetId, string Type, double Weight, string Provenance, JsonObject Metadata,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string WorkspaceId);
// One retired state of a memory (entry_versions): its text, tags and non-text state before the change at CreatedAt.
public sealed record SbVersion(string EntryId, string WorkspaceId, int Seq, string? Content, int? PriorLength, string[] Tags, JsonObject State,
    string ActorId, string Channel, string Reason, JsonObject Meta, DateTimeOffset? ValidFrom, DateTimeOffset CreatedAt);
public sealed record SbEvent(string Id, string EntryId, string ActorId, string Event, JsonObject Payload, DateTimeOffset CreatedAt);
// A forgotten memory (entries_trash): Row holds every entries column but content and vector_ids, Edges its edges then.
public sealed record SbTrash(string Key, string Id, string WorkspaceId, string ActorId, string Content, JsonObject Row, SbEdge[] Edges,
    string[] VectorIds, DateTimeOffset DeletedAt, string DeletedBy, string Channel, string Reason, string Nonce)
{
    public SbEntry Entry
    {
        get
        {
            var row = (JsonObject)Row.DeepClone();
            row["key"] = Key;
            row["content"] = Content;
            row["vector_ids"] = new JsonArray([.. VectorIds.Select(v => (JsonNode?)v)]);
            return row.Deserialize<SbEntry>(SbBrain.Json)!;
        }
    }
}
public sealed record SbRelease(string Release, DateTimeOffset From);
public sealed record SbExportFile(string File, string User, string Release, int Version, DateTimeOffset At, string? Note = null);
public sealed record SbUpstream(string Repo, string Commit, string Version, string Export, string Import);

public sealed record SbBrain(string Schema, string Label, SbUpstream Upstream, DateTimeOffset Now, string Timezone, SbRelease[] Releases,
    string[] Notes, Dictionary<string, Dictionary<string, string>> NotExported, SbUser[] Users, SbWorkspace[] Workspaces,
    SbMembership[] Memberships, SbProject[] Projects, SbEntry[] Entries, SbEdge[] Edges, SbVersion[] Versions, SbEvent[] Events,
    SbTrash[] Trash, SbExportFile[] Exports)
{
    public const string File = "sb-export-brain.json";
    // Snake case, as upstream's columns; an unknown field fails the load, so the brain carries no column upstream lacks.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
    public static SbBrain Load() => JsonSerializer.Deserialize<SbBrain>(System.IO.File.ReadAllText(Path.Combine(ParityCorpus.Folder, File)), Json)!;
    public static JsonObject ReadExport(string file) => (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(ParityCorpus.Folder, file)))!;

    public SbUser User(string id) => Users.Single(u => u.Id == id);
    public SbWorkspace Workspace(string id) => Workspaces.Single(w => w.Id == id);
    public SbEntry Entry(string key) => AllEntries.Single(e => e.Key == key);
    // Live memories and the ones in the trash, as they stood when forgotten.
    public IEnumerable<SbEntry> AllEntries => Entries.Concat(Trash.Select(t => t.Entry));
    public DateTimeOffset Upgrade => Releases.Single(r => r.Release == "4.0.0").From;

    // A member's readable set (src/lib/scope.ts readableWorkspaces): the workspaces they belong to at that moment, their
    // personal one and every team; an admin also reads the unscoped legacy rows ('').
    public IReadOnlyList<string> Readable(SbUser user, DateTimeOffset at)
    {
        var mine = Memberships.Where(m => m.UserId == user.Id && m.CreatedAt <= at).Select(m => m.WorkspaceId).ToList();
        if (user.Role == "admin") mine.Add("");
        return mine;
    }
    public string PersonalWorkspace(SbUser user) => Memberships.Where(m => m.UserId == user.Id)
        .Select(m => Workspace(m.WorkspaceId)).Single(w => w.Kind == "personal").Id;

    IEnumerable<SbEvent> Moves(string entryId) => Events.Where(e => e.EntryId == entryId && e.Event is "shared" or "unshared");
    static string Payload(SbEvent e, string name) => e.Payload[name]!.GetValue<string>();

    // Where a memory was at a moment: a share or unshare after it moved the row from the payload's fromWorkspaceId.
    public string WorkspaceAt(SbEntry entry, DateTimeOffset at) =>
        Moves(entry.Id).Where(e => e.CreatedAt > at).MinBy(e => e.CreatedAt) is { } move ? Payload(move, "fromWorkspaceId") : entry.WorkspaceId;

    // Where an edge was at a moment: a share moves every edge of the memory that was in its old workspace
    // (src/capture/share.ts), so undo the later moves of either end, newest first.
    public string EdgeWorkspaceAt(SbEdge edge, DateTimeOffset at)
    {
        var ws = edge.WorkspaceId;
        foreach (var move in Moves(edge.SourceId).Concat(Moves(edge.TargetId)).Where(e => e.CreatedAt > at && e.CreatedAt >= edge.CreatedAt)
                     .OrderByDescending(e => e.CreatedAt))
            if (Payload(move, "workspaceId") == ws) ws = Payload(move, "fromWorkspaceId");
        return ws;
    }

    static DateTimeOffset? StateTime(JsonObject state, string name) =>
        state[name] is JsonValue v ? DateTimeOffset.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture) : null;
    static string? StateText(JsonObject state, string name) => state[name] is JsonValue v ? v.GetValue<string>() : null;

    // A memory as it stood at a moment, from its versions: the oldest state retired after that moment was the current one.
    // Counters (recalls, contradictions, importance) have no history upstream, so they stay as they are now.
    public SbEntry At(SbEntry entry, DateTimeOffset at)
    {
        var ws = WorkspaceAt(entry, at);
        if (Versions.Where(v => v.EntryId == entry.Id && v.CreatedAt > at).MinBy(v => v.Seq) is not { } v) return entry with { WorkspaceId = ws };
        return entry with
        {
            Content = v.Content!, Tags = v.Tags, WorkspaceId = ws, UpdatedAt = v.ValidFrom,
            ValidFrom = StateTime(v.State, "valid_from"), ValidUntil = StateTime(v.State, "valid_until"), WhenAt = StateTime(v.State, "when_at"),
            WhenKind = StateText(v.State, "when_kind"), WhenSource = StateText(v.State, "when_source"), WhenLabel = StateText(v.State, "when_label"),
        };
    }

    // Every memory that existed at a moment, in the state it had: forgotten ones too, if forgotten later.
    public IEnumerable<SbEntry> EntriesAt(DateTimeOffset at) =>
        Entries.Concat(Trash.Where(t => t.DeletedAt > at).Select(t => t.Entry)).Where(e => e.CreatedAt <= at).Select(e => At(e, at));
    public IEnumerable<SbEdge> EdgesAt(DateTimeOffset at) =>
        Edges.Concat(Trash.Where(t => t.DeletedAt > at).SelectMany(t => t.Edges)).Where(e => e.CreatedAt <= at);
}

// What each release's GET /export carries. 4.0.1 is the pinned code (version 3: entries with updated_at and the validity
// window, edges, projects). 3.3's shape (version 2: no projects, no updated_at, no validity window) is inferred from what
// upstream's importer still treats as optional (ExportPayload and ExportEntry in src/entries/import.ts) and from its
// CHANGELOG (projects reached the export in 3.4, validity windows in 4.0), not from a captured file of that release.
public sealed record SbShape(string Release, int Version, bool UpdatedAt, bool Validity, bool Projects)
{
    public static SbShape Of(string release) => release switch
    {
        "4.0.1" or "4.0.0" => new(release, 3, true, true, true),
        "3.3" => new(release, 2, false, false, false),
        _ => throw new ArgumentException($"no export shape recorded for release {release}"),
    };
    public string[] TopKeys => ["ok", "exported_at", "version", "entries", "edges", .. Projects ? ["projects"] : Array.Empty<string>()];
    public string[] EntryKeys => ["id", "content", "tags", "source", "created_at", .. UpdatedAt ? ["updated_at"] : Array.Empty<string>(),
        "recall_count", "importance_score", "contradiction_wins", "contradiction_losses", .. Validity ? ["valid_from", "valid_until"] : Array.Empty<string>()];
    public static readonly string[] EdgeKeys = ["source_id", "target_id", "type", "weight", "provenance", "created_at"];
    public static readonly string[] ProjectKeys = ["id", "name", "description", "aliases", "status", "created_at", "updated_at"];
}

public static class UpstreamExport
{
    static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    static JsonNode? Ms(DateTimeOffset? t) => t is { } v ? JsonValue.Create(Ms(v)) : null;

    // GET /export for one member at one moment: the readable set's memories oldest first, the edges whose own workspace
    // is readable (in table order: upstream gives them none), the readable projects by created_at, workspace and id;
    // vector ids, workspaces and authors left out.
    public static JsonObject Take(SbBrain brain, SbExportFile file)
    {
        var shape = SbShape.Of(file.Release);
        var readable = brain.Readable(brain.User(file.User), file.At).ToHashSet(StringComparer.Ordinal);
        var entries = brain.EntriesAt(file.At).Where(e => readable.Contains(e.WorkspaceId)).OrderBy(e => e.CreatedAt).Select(e =>
        {
            var o = new JsonObject
            {
                ["id"] = e.Id, ["content"] = e.Content, ["tags"] = new JsonArray([.. e.Tags.Select(t => (JsonNode?)t)]), ["source"] = e.Source,
                ["created_at"] = Ms(e.CreatedAt),
            };
            if (shape.UpdatedAt) o["updated_at"] = Ms(e.UpdatedAt ?? e.CreatedAt);
            o["recall_count"] = e.RecallCount;
            o["importance_score"] = e.ImportanceScore;
            o["contradiction_wins"] = e.ContradictionWins;
            o["contradiction_losses"] = e.ContradictionLosses;
            if (shape.Validity)
            {
                o["valid_from"] = Ms(e.ValidFrom);
                o["valid_until"] = Ms(e.ValidUntil);
            }
            return (JsonNode?)o;
        });
        var edges = brain.EdgesAt(file.At).Where(e => readable.Contains(brain.EdgeWorkspaceAt(e, file.At))).Select(e => (JsonNode?)new JsonObject
        {
            ["source_id"] = e.SourceId, ["target_id"] = e.TargetId, ["type"] = e.Type, ["weight"] = e.Weight, ["provenance"] = e.Provenance,
            ["created_at"] = Ms(e.CreatedAt),
        });
        var export = new JsonObject
        {
            ["ok"] = true, ["exported_at"] = Ms(file.At), ["version"] = shape.Version,
            ["entries"] = new JsonArray([.. entries]), ["edges"] = new JsonArray([.. edges]),
        };
        if (shape.Projects)
            export["projects"] = new JsonArray([.. brain.Projects.Where(p => p.CreatedAt <= file.At && readable.Contains(p.WorkspaceId))
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.WorkspaceId, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => (JsonNode?)new JsonObject
                {
                    ["id"] = p.Id, ["name"] = p.Name, ["description"] = p.Description,
                    ["aliases"] = new JsonArray([.. p.Aliases.Select(a => (JsonNode?)a)]), ["status"] = p.Status,
                    ["created_at"] = Ms(p.CreatedAt), ["updated_at"] = Ms(p.UpdatedAt),
                })]);
        return export;
    }

    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // One row per line, so a diff names the row. Invisible characters are written escaped, so a reader sees them.
    public static string Write(JsonObject export)
    {
        var parts = export.Select(p => p.Value is JsonArray rows
            ? $"  \"{p.Key}\": " + (rows.Count == 0 ? "[]" : "[\n" + string.Join(",\n", rows.Select(r => "    " + r!.ToJsonString(Compact))) + "\n  ]")
            : $"  \"{p.Key}\": " + (p.Value?.ToJsonString(Compact) ?? "null"));
        return Invisible.Replace("{\n" + string.Join(",\n", parts) + "\n}\n", m => $"\\u{(int)m.Value[0]:x4}");
    }

    // Two JSON values with the same meaning: keys in any order, numbers by value.
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    static readonly Regex Invisible = new("[\u200b-\u200f\u2060\ufeff]");
}

// Upstream's tag rules (src/tags/system.ts, src/tags/t7.ts, src/quarantine/tags.ts at the pinned commit), restated.
public static partial class SbTags
{
    public static readonly string[] HoldReasons = ["instruction", "hidden", "burst", "capsule", "too_long"];
    public static readonly string[] EdgeTypes = ["relates_to", "supersedes", "caused_by", "decided", "about_person", "part_of_project", "follows", "drawn_from"];
    // Types whose meaning depends on the kinds they join (src/graph/types.ts EDGE_TYPES allowedKinds): both ends episodic.
    public static readonly string[] EpisodicOnly = ["decided", "follows"];
    public static readonly string[] Provenances = ["explicit", "inferred", "system"];
    public static readonly string[] Statuses = ["canonical", "draft", "deprecated"];
    public static readonly string[] Kinds = ["episodic", "semantic"];
    public static readonly string[] Volatilities = ["durable", "state", "volatile"];
    public static readonly string[] VersionReasons = ["update", "append", "merge", "replace", "rollup", "status", "due", "mirror", "revert", "validity"];
    public static readonly string[] Events = ["created", "updated", "appended", "deleted", "status_changed", "shared", "unshared", "held",
        "insight_confirmed", "insight_dismissed"];
    public static readonly string[] AliasReservedPrefixes = ["kind:", "status:", "volatility:", "stale:", "capsule:", "capsule-slot:", "project:",
        "standing:", "ledger:", "confidence:", "confidence-source:", "outcome:", "review-rearms:", "counterparty:"];

    // A row is held only for one of the five reasons upstream writes; a `quarantine:review` from before 4.0 is an ordinary tag.
    public static string? HeldReason(IEnumerable<string> tags) => tags.Select(t => t.Trim().ToLowerInvariant())
        .Where(t => t.StartsWith("quarantine:", StringComparison.Ordinal)).Select(t => t["quarantine:".Length..])
        .FirstOrDefault(r => HoldReasons.Contains(r));

    // A tag in a namespace 4.0 reserved, in the exact format the system writes (isNewReservedTag): upstream's own importer
    // strips these from a file's tags, after reading a hold reason, and no 3.x export can carry one.
    public static bool IsNewReserved(string tag)
    {
        var t = tag.Trim().ToLowerInvariant();
        return t == "owed-to-me"
            || t.StartsWith("quarantine:", StringComparison.Ordinal) && HoldReasons.Contains(t["quarantine:".Length..])
            || t.StartsWith("edited-canonical:", StringComparison.Ordinal) && DateValue().IsMatch(t["edited-canonical:".Length..])
            || t == "standing:active" || t == "ledger:decision"
            || t is "confidence-source:stated" or "confidence-source:inferred"
            || t.StartsWith("confidence:", StringComparison.Ordinal) && ConfidenceValue().IsMatch(t["confidence:".Length..])
                && int.Parse(t[^2..], CultureInfo.InvariantCulture) is >= 5 and <= 95 and var step && step % 5 == 0
            || t is "outcome:right" or "outcome:wrong" or "outcome:mixed" or "outcome:unknown"
            || t is "review-rearms:1" or "review-rearms:2"
            || t.StartsWith("counterparty:", StringComparison.Ordinal) && Slug().IsMatch(t["counterparty:".Length..]);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")] public static partial Regex Slug();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")] private static partial Regex DateValue();
    [GeneratedRegex(@"^0\.\d{2}$")] private static partial Regex ConfidenceValue();
}

// The checks upstream's POST /import makes on a file (parseImportBody, parseEntryRow, parseEdgeRow and
// parseImportedProject in src/entries/import.ts and src/projects/registry.ts at the pinned commit), restated so a fixture
// that upstream would refuse fails here. Each problem names the row and upstream's reason.
public static class UpstreamImportRules
{
    static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
    static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;
    static bool Absent(JsonNode? n) => n is null;
    static string? Text(JsonNode? n) => IsString(n) ? n!.GetValue<string>() : null;

    public static IReadOnlyList<string> Problems(JsonNode? body)
    {
        var problems = new List<string>();
        if (body is not JsonObject o) return ["body must be an object"];
        if (o["version"] is { } version && !(IsNumber(version) && double.Parse(version.ToJsonString(), CultureInfo.InvariantCulture) is 2 or 3))
            problems.Add("version must be 2 or 3");
        if (o["entries"] is not JsonArray entries) return [.. problems, "entries must be an array"];
        if (o.ContainsKey("projects") && o["projects"] is not JsonArray) problems.Add("projects must be an array");
        for (var i = 0; i < entries.Count; i++)
            if (Entry(entries[i]) is { } reason) problems.Add($"entries[{i}] {Text(entries[i]?["id"])}: {reason}");
        if (o["edges"] is JsonArray edges)
            for (var i = 0; i < edges.Count; i++)
                if (Edge(edges[i]) is { } reason) problems.Add($"edges[{i}]: {reason}");
        if (o["projects"] is JsonArray projects)
            for (var i = 0; i < projects.Count; i++)
                if (Project(projects[i]) is { } reason) problems.Add($"projects[{i}] {Text(projects[i]?["id"])}: {reason}");
        return problems;
    }

    static string? Required(JsonNode? n, string missing, string invalid) =>
        Absent(n) || Text(n) == "" ? missing : !IsString(n) ? invalid : Text(n)!.Trim() == "" ? missing : null;

    static string? Entry(JsonNode? node)
    {
        if (node is not JsonObject e) return "invalid_entry";
        if (Required(e["id"], "missing_id", "invalid_id") is { } id) return id;
        if (Required(e["content"], "missing_content", "invalid_content") is { } content) return content;
        if (!Absent(e["tags"]) && (e["tags"] is not JsonArray tags || !tags.All(IsString))) return "invalid_tag";
        if (!Absent(e["source"]) && !IsString(e["source"])) return "invalid_source";
        if (!Absent(e["created_at"]) && !IsNumber(e["created_at"])) return "invalid_created_at";
        if (!Absent(e["updated_at"]) && !IsNumber(e["updated_at"])) return "invalid_updated_at";
        foreach (var name in new[] { "recall_count", "importance_score", "contradiction_wins", "contradiction_losses" })
            if (!Absent(e[name]) && !IsNumber(e[name])) return "invalid_" + name;
        return null;
    }

    static string? Edge(JsonNode? node)
    {
        if (node is not JsonObject e) return "invalid_edge";
        if ((Required(e["source_id"], "missing_endpoint", "invalid_endpoint") ?? Required(e["target_id"], "missing_endpoint", "invalid_endpoint")) is { } end) return end;
        var type = Text(e["type"])?.Trim() is { Length: > 0 } t ? t : "relates_to";
        if (!SbTags.EdgeTypes.Contains(type)) return "invalid_type";
        if (Text(e["source_id"])!.Trim() == Text(e["target_id"])!.Trim()) return "self_edge";
        if (!Absent(e["weight"]) && !IsNumber(e["weight"])) return "invalid_weight";
        return null;
    }

    static string? Project(JsonNode? node)
    {
        if (node is not JsonObject p) return "invalid_project: project must be an object";
        var status = Absent(p["status"]) ? "active" : Text(p["status"]);
        if (status is not ("active" or "archived")) return "invalid_project: status";
        if (!Absent(p["created_at"]) && !IsNumber(p["created_at"])) return "invalid_project: created_at";
        if (!Absent(p["updated_at"]) && !IsNumber(p["updated_at"])) return "invalid_project: updated_at";
        if (Text(p["id"]) is not { } id || !SbTags.Slug().IsMatch(id)) return "invalid_project: id";
        if (Text(p["name"]) is not { } name || name.Contains('\0') || name.Trim() == "" || name.Trim().Length > 120) return "invalid_project: name";
        if (!Absent(p["description"]) && (Text(p["description"]) is not { } d || d.Contains('\0') || d.Trim().Length > 1000)) return "invalid_project: description";
        if (!Absent(p["aliases"]))
        {
            if (p["aliases"] is not JsonArray aliases || !aliases.All(IsString)) return "invalid_project: aliases";
            var clean = aliases.Select(a => Text(a)!.Trim().ToLowerInvariant()).ToArray();
            if (clean.Any(a => a == "" || a.Length > 128 || a.Contains('\0') || a.Contains('"') || a.Contains('\\')
                               || SbTags.AliasReservedPrefixes.Any(r => a.StartsWith(r, StringComparison.Ordinal)))) return "invalid_project: aliases";
            if (clean.Distinct(StringComparer.Ordinal).Count() > 16) return "invalid_project: aliases";
        }
        return null;
    }
}
