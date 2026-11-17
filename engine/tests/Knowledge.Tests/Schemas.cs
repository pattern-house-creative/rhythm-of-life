using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Knowledge.Tests;

// The same small JSON Schema (draft 2020-12) subset as scripts/json-schema.mjs, for checking what the engine returns
// against contracts/mcp/*.output.schema.json. An unknown keyword is an error, so the schemas stay inside the subset
// both checkers implement. $ref is "#/<pointer>" or "<file>#/<pointer>" in the same folder; format is annotation only.
// unevaluatedProperties checks each member no properties or additionalProperties evaluated, here or through $ref, allOf
// and the passing anyOf and oneOf branches (the settings schema gives each adapter section its keys this way).
public static class Schemas
{
    static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description", "$comment", "default", "examples", "$defs", "format"];
    static readonly HashSet<string> Assertions = ["$ref", "type", "enum", "const", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "pattern", "minItems", "maxItems", "uniqueItems", "prefixItems", "items", "required", "properties",
        "additionalProperties", "propertyNames", "minProperties", "maxProperties", "allOf", "anyOf", "oneOf", "not", "unevaluatedProperties"];
    public static string Folder(string name) => Path.Combine(AppContext.BaseDirectory, "contracts", name);

    // Fails the test with every problem found.
    public static void Valid(string folder, string file, JsonNode? value) => Assert.Empty(Check(folder, file, value));
    public static void Valid(string folder, string file, object value) =>
        Valid(folder, file, JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public static IReadOnlyList<string> Check(string folder, string file, JsonNode? value)
    {
        var docs = new Dictionary<string, JsonNode>();
        JsonNode Doc(string name) => docs.TryGetValue(name, out var d) ? d : docs[name] = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, name)))!;
        (JsonNode, string) Resolve(string reference, string from)
        {
            var parts = reference.Split('#', 2);
            var target = parts[0].Length > 0 ? parts[0] : from;
            JsonNode? node = Doc(target);
            foreach (var part in (parts.Length > 1 ? parts[1] : "").Split('/').Skip(1))
                node = node?[part.Replace("~1", "/").Replace("~0", "~")];
            return (node ?? throw new InvalidOperationException($"{from}: $ref {reference} does not resolve"), target);
        }
        List<string> Errors(JsonNode? schema, JsonNode? v, string at, string from)
        {
            var here = at.Length > 0 ? at : "/";
            if (schema is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                return b.GetValue<bool>() ? [] : [$"{here}: not allowed here"];
            var s = schema!.AsObject();
            var output = new List<string>();
            if (s.Select(p => p.Key).FirstOrDefault(k => !Annotations.Contains(k) && !Assertions.Contains(k)) is { } bad)
                throw new InvalidOperationException($"{from}: unsupported keyword {bad}");
            if (s["$ref"] is { } reference)
            {
                var (target, file) = Resolve((string)reference!, from);
                output.AddRange(Errors(target, v, at, file));
            }
            if (s["type"] is { } type)
            {
                var types = type is JsonArray list ? list.Select(t => (string)t!).ToArray() : [(string)type!];
                if (!types.Any(t => Is(t, v))) { output.Add($"{here}: expected {string.Join(" or ", types)}"); return output; }
            }
            if (s.ContainsKey("const") && !JsonNode.DeepEquals(s["const"], v)) output.Add($"{here}: expected {s["const"]?.ToJsonString() ?? "null"}");
            if (s["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, v))) output.Add($"{here}: not one of {choices.ToJsonString()}");
            if (v is JsonValue n && n.GetValueKind() == JsonValueKind.Number)
            {
                var x = n.GetValue<double>();
                if (s["minimum"] is { } min && x < (double)min) output.Add($"{here}: below {min}");
                if (s["maximum"] is { } max && x > (double)max) output.Add($"{here}: above {max}");
                if (s["exclusiveMinimum"] is { } emin && x <= (double)emin) output.Add($"{here}: not above {emin}");
                if (s["exclusiveMaximum"] is { } emax && x >= (double)emax) output.Add($"{here}: not below {emax}");
            }
            if (v is JsonValue t && t.GetValueKind() == JsonValueKind.String)
            {
                var text = t.GetValue<string>();
                var length = text.EnumerateRunes().Count();
                if (s["minLength"] is { } minLength && length < (int)minLength) output.Add($"{here}: shorter than {minLength}");
                if (s["maxLength"] is { } maxLength && length > (int)maxLength) output.Add($"{here}: longer than {maxLength}");
                if (s["pattern"] is { } pattern && !Pattern((string)pattern!).IsMatch(text)) output.Add($"{here}: does not match {pattern}");
            }
            if (v is JsonArray array)
            {
                if (s["minItems"] is { } minItems && array.Count < (int)minItems) output.Add($"{here}: fewer than {minItems} items");
                if (s["maxItems"] is { } maxItems && array.Count > (int)maxItems) output.Add($"{here}: more than {maxItems} items");
                if (s["uniqueItems"] is { } unique && (bool)unique && array.Select((x, i) => array.Take(i).Any(y => JsonNode.DeepEquals(x, y))).Any(r => r))
                    output.Add($"{here}: items repeat");
                var prefix = s["prefixItems"] as JsonArray;
                for (var i = 0; i < array.Count; i++)
                    if ((prefix is not null && i < prefix.Count ? prefix[i] : s["items"]) is { } item) output.AddRange(Errors(item, array[i], $"{at}/{i}", from));
            }
            if (v is JsonObject obj)
            {
                foreach (var name in (s["required"] as JsonArray ?? []).Select(r => (string)r!))
                    if (!obj.ContainsKey(name)) output.Add($"{here}: {name} is required");
                if (s["minProperties"] is { } minP && obj.Count < (int)minP) output.Add($"{here}: fewer than {minP} members");
                if (s["maxProperties"] is { } maxP && obj.Count > (int)maxP) output.Add($"{here}: more than {maxP} members");
                var properties = s["properties"] as JsonObject;
                foreach (var (key, member) in obj)
                {
                    var p = $"{at}/{key.Replace("~", "~0").Replace("/", "~1")}";
                    if (s["propertyNames"] is { } names) output.AddRange(Errors(names, JsonValue.Create(key), $"{p} (name)", from));
                    if (properties is not null && properties.ContainsKey(key)) output.AddRange(Errors(properties[key], member, p, from));
                    else if (s.ContainsKey("additionalProperties")) output.AddRange(Errors(s["additionalProperties"], member, p, from));
                }
            }
            foreach (var each in s["allOf"] as JsonArray ?? []) output.AddRange(Errors(each, v, at, from));
            if (s["anyOf"] is JsonArray any && !any.Any(a => Errors(a, v, at, from).Count == 0)) output.Add($"{here}: matches none of anyOf");
            if (s["oneOf"] is JsonArray one && one.Count(o => Errors(o, v, at, from).Count == 0) is var passing && passing != 1)
                output.Add($"{here}: matches {passing} of oneOf, not exactly one");
            if (s.ContainsKey("not") && Errors(s["not"], v, at, from).Count == 0) output.Add($"{here}: matches what is not allowed");
            if (s.ContainsKey("unevaluatedProperties") && v is JsonObject members)
            {
                var seen = Evaluated(s, members, at, from, asking: true, []);
                foreach (var (key, member) in members)
                    if (!seen.Contains(key)) output.AddRange(Errors(s["unevaluatedProperties"], member, $"{at}/{key.Replace("~", "~0").Replace("/", "~1")}", from));
            }
            return output;
        }
        // The members a schema evaluates: its properties, every member once it has additionalProperties (or, below the
        // schema asking, unevaluatedProperties), and what $ref, allOf and the passing anyOf and oneOf branches reach.
        HashSet<string> Evaluated(JsonNode? schema, JsonObject v, string at, string from, bool asking, HashSet<string> seen)
        {
            if (schema is not JsonObject s) return seen;
            foreach (var (key, _) in s["properties"] as JsonObject ?? new JsonObject()) if (v.ContainsKey(key)) seen.Add(key);
            if (s.ContainsKey("additionalProperties") || !asking && s.ContainsKey("unevaluatedProperties")) seen.UnionWith(v.Select(p => p.Key));
            if (s["$ref"] is { } reference)
            {
                var (target, file) = Resolve((string)reference!, from);
                Evaluated(target, v, at, file, false, seen);
            }
            foreach (var each in s["allOf"] as JsonArray ?? []) Evaluated(each, v, at, from, false, seen);
            foreach (var each in (s["anyOf"] as JsonArray ?? []).Concat(s["oneOf"] as JsonArray ?? []))
                if (Errors(each, v, at, from).Count == 0) Evaluated(each, v, at, from, false, seen);
            return seen;
        }
        // Parsed from text, so every number reads as a number whatever built it.
        return Errors(Doc(file), value is null ? null : JsonNode.Parse(value.ToJsonString()), "", file);
    }

    static bool Is(string type, JsonNode? v) => type switch
    {
        "null" => v is null,
        "object" => v is JsonObject,
        "array" => v is JsonArray,
        "string" => v is JsonValue s && s.GetValueKind() == JsonValueKind.String,
        "boolean" => v is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => v is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => v is JsonValue i && i.GetValueKind() == JsonValueKind.Number && Math.Abs(i.GetValue<double>() % 1) == 0,
        _ => throw new InvalidOperationException($"unknown type {type}"),
    };
    static Regex Pattern(string pattern) => pattern.StartsWith("(?i)", StringComparison.Ordinal)
        ? new(pattern[4..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : new(pattern, RegexOptions.CultureInvariant);
}

public sealed class SchemaTests
{
    static JsonNode Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", name)))!;

    [Theory]
    [InlineData("app_latest_glucose.example.json", "app_latest_glucose.output.schema.json")]
    [InlineData("app_recent_glucose.example.json", "app_recent_glucose.output.schema.json")]
    [InlineData("app_recent_glucose.day.example.json", "app_recent_glucose.output.schema.json")]
    [InlineData("app_graph.example.json", "app_graph.output.schema.json")]
    [InlineData("app_graph.content.example.json", "app_graph.output.schema.json")]
    [InlineData("app_insights.example.json", "app_insights.output.schema.json")]
    [InlineData("app_dashboards.example.json", "app_dashboards.output.schema.json")]
    [InlineData("app_dashboards.dashboard.example.json", "app_dashboards.output.schema.json")]
    [InlineData("app_dashboards.layout.example.json", "app_dashboards.output.schema.json")]
    [InlineData("app_dashboards.map.example.json", "app_dashboards.output.schema.json")]
    [InlineData("app_request_refresh.example.json", "app_request_refresh.output.schema.json")]
    [InlineData("app_garmin_summary.example.json", "app_garmin_summary.output.schema.json")]
    [InlineData("app_request_garmin_sync.example.json", "app_request_garmin_sync.output.schema.json")]
    [InlineData("app_upload_document.example.json", "app_upload_document.output.schema.json")]
    [InlineData("app_upload_document.refused.example.json", "app_upload_document.output.schema.json")]
    [InlineData("app_screens.example.json", "app_screens.output.schema.json")]
    [InlineData("app_screens.send.example.json", "app_screens.output.schema.json")]
    [InlineData("app_screens.list.example.json", "app_screens.output.schema.json")]
    [InlineData("app_screens.move.example.json", "app_screens.output.schema.json")]
    [InlineData("app_sync.example.json", "app_sync.output.schema.json")]
    [InlineData("app_sync.request.example.json", "app_sync.output.schema.json")]
    // The app's Memories (SB-P3-12): each op's answer, a held memory whole from get, and a refusal.
    [InlineData("app_memories.list.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.held.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.version.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.history.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.trash.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.projects.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.capture.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.release.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.forget.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.restore.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.delete.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.refused.example.json", "app_memories.output.schema.json")]
    // A person's own steps (SB-P3-22): a memory in a capsule slot, the slot op, the review queue and a release of several.
    [InlineData("app_memories.slotted.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.capsule.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.review.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.release_all.example.json", "app_memories.output.schema.json")]
    // A person's share (run 14, with memory.share on): a move into Family, and the refusal while memory scopes are off.
    [InlineData("app_memories.share.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.share.refused.example.json", "app_memories.output.schema.json")]
    // A memory's truth state and a write's retraction cascade (SB-P4-11, D-192's app half), and a replaced fact's star.
    [InlineData("app_memories.replaced.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.cascade.example.json", "app_memories.output.schema.json")]
    [InlineData("app_graph.replaced.example.json", "app_graph.output.schema.json")]
    // A work item's star, and a list filtered to a project's work (migration 079, with memory.work on).
    [InlineData("app_graph.work.example.json", "app_graph.output.schema.json")]
    [InlineData("app_memories.work.example.json", "app_memories.output.schema.json")]
    // Kept dismissals (SB-P4-05): the dismissals on a reader's map, Discover's Dismiss and taking one back.
    [InlineData("app_memories.dismissals.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.dismiss.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.undismiss.example.json", "app_memories.output.schema.json")]
    // A person's own link steps (run 17): a memory's connections, the stale queue, a link, an unlink, still true and a flag clear.
    [InlineData("app_memories.connections.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.stale.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.link.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.unlink.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.still_true.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.clear_flags.example.json", "app_memories.output.schema.json")]
    // The full memory UI's list (SB-P6-01): a list's tags and count, an AI client's groups of changes and a group's undo,
    // several at once each answered on its own, a project's writes, a capsule's status and the settings to read.
    [InlineData("app_memories.tags.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.changes.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.undo_group.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.capsule_status.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.settings.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.forget_all.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.restore_all.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.delete_all.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.share_all.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.projects.full.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.project_create.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.project_update.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.project_delete.example.json", "app_memories.output.schema.json")]
    // The brief's lists behind its Home card (SB-P6-01): what a person owes and is owed, what is due, decisions with their outcomes,
    // calibration, and a person's word on each.
    [InlineData("app_memories.loops.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.loops.owed.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.due.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.decisions.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.decisions.resolved.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.calibration.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.calibration.not_ready.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve_loop.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve_due.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve_due.cleared.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.record_outcome.example.json", "app_memories.output.schema.json")]
    // The memory tools (SB-P3-09) and get_prompt_capsule (SB-P3-08): the harness answers they give an AI client.
    [InlineData("remember.example.json", "remember.output.schema.json")]
    [InlineData("remember.held.example.json", "remember.output.schema.json")]
    [InlineData("append.example.json", "append.output.schema.json")]
    [InlineData("update.example.json", "update.output.schema.json")]
    [InlineData("update.refused.example.json", "update.output.schema.json")]
    [InlineData("set_status.example.json", "set_status.output.schema.json")]
    [InlineData("get.example.json", "get.output.schema.json")]
    [InlineData("get.held.example.json", "get.output.schema.json")]
    [InlineData("get.version.example.json", "get.output.schema.json")]
    [InlineData("list_recent.example.json", "list_recent.output.schema.json")]
    [InlineData("list_recent.trash.example.json", "list_recent.output.schema.json")]
    [InlineData("forget.example.json", "forget.output.schema.json")]
    [InlineData("undo.example.json", "undo.output.schema.json")]
    [InlineData("undo.restored.example.json", "undo.output.schema.json")]
    [InlineData("undo.restored.unchanged.example.json", "undo.output.schema.json")]
    [InlineData("undo.group.example.json", "undo.output.schema.json")]
    [InlineData("history.example.json", "history.output.schema.json")]
    [InlineData("list_projects.example.json", "list_projects.output.schema.json")]
    [InlineData("list_teams.example.json", "list_teams.output.schema.json")]
    [InlineData("share.example.json", "share.output.schema.json")]
    [InlineData("share.refused.example.json", "share.output.schema.json")]
    // set_capsule (migration 078, with memory.capsuleWrites on): a project slot set, and a slot another memory fills.
    [InlineData("set_capsule.example.json", "set_capsule.output.schema.json")]
    [InlineData("set_capsule.refused.example.json", "set_capsule.output.schema.json")]
    // The work tools (migration 079, with memory.work on): a new work item, a memory made one, a personal task refused; a
    // page of a project's work; a state changed to done with its summary, a repeat, and done with no summary; and a memory
    // read whole that is a work item.
    [InlineData("create_work.example.json", "create_work.output.schema.json")]
    [InlineData("create_work.made.example.json", "create_work.output.schema.json")]
    [InlineData("create_work.refused.example.json", "create_work.output.schema.json")]
    [InlineData("list_work.example.json", "list_work.output.schema.json")]
    [InlineData("update_work.example.json", "update_work.output.schema.json")]
    [InlineData("update_work.unchanged.example.json", "update_work.output.schema.json")]
    [InlineData("update_work.refused.example.json", "update_work.output.schema.json")]
    [InlineData("get.work.example.json", "get.output.schema.json")]
    // Claims (migration 081): a claim made, one another session holds, and a claim let go.
    [InlineData("claim_work.example.json", "claim_work.output.schema.json")]
    [InlineData("claim_work.refused.example.json", "claim_work.output.schema.json")]
    [InlineData("release_work.example.json", "release_work.output.schema.json")]
    // supersede (SB-P4-06): the plan with its confirmation, the plan applied, and a confirmed memory protected.
    [InlineData("supersede.plan.example.json", "supersede.output.schema.json")]
    [InlineData("supersede.example.json", "supersede.output.schema.json")]
    [InlineData("supersede.refused.example.json", "supersede.output.schema.json")]
    // link, unlink and connections (SB-P4-02): an AI client's link, a refusal across scopes, an unlink that removed a link
    // and dismissed the map's suggestion, and a memory's connections with a held neighbour and a suggestion.
    [InlineData("link.example.json", "link.output.schema.json")]
    [InlineData("link.refused.example.json", "link.output.schema.json")]
    [InlineData("unlink.example.json", "unlink.output.schema.json")]
    [InlineData("connections.example.json", "connections.output.schema.json")]
    // The stale queue (SB-P4-09): a page with the queue's total, a memory flagged by a retracted source, still true
    // confirming one, a repeat that changes nothing, and a memory marked wrong refused.
    [InlineData("list_stale.example.json", "list_stale.output.schema.json")]
    [InlineData("list_stale.flagged.example.json", "list_stale.output.schema.json")]
    [InlineData("still_true.example.json", "still_true.output.schema.json")]
    [InlineData("still_true.unchanged.example.json", "still_true.output.schema.json")]
    [InlineData("still_true.refused.example.json", "still_true.output.schema.json")]
    // Digests (SB-P5-13): a tag's list that is due, a project's waiting for its day, a saved digest, a save again from
    // the same basis, one refused as not due, and the newest digest read, or none.
    [InlineData("digest_sources.example.json", "digest_sources.output.schema.json")]
    [InlineData("digest_sources.waiting.example.json", "digest_sources.output.schema.json")]
    [InlineData("save_digest.example.json", "save_digest.output.schema.json")]
    [InlineData("save_digest.unchanged.example.json", "save_digest.output.schema.json")]
    [InlineData("save_digest.refused.example.json", "save_digest.output.schema.json")]
    [InlineData("digest.example.json", "digest.output.schema.json")]
    [InlineData("digest.none.example.json", "digest.output.schema.json")]
    // Open loops (SB-P5-09): a commitment remembered, both directions' list with its counts, what is owed to the person, a
    // loop marked done, one received, a repeat that changes nothing, and received refused for a loop the person owes.
    [InlineData("remember.commitment.example.json", "remember.output.schema.json")]
    [InlineData("list_loops.example.json", "list_loops.output.schema.json")]
    [InlineData("list_loops.in.example.json", "list_loops.output.schema.json")]
    [InlineData("resolve_loop.example.json", "resolve_loop.output.schema.json")]
    [InlineData("resolve_loop.received.example.json", "resolve_loop.output.schema.json")]
    [InlineData("resolve_loop.unchanged.example.json", "resolve_loop.output.schema.json")]
    [InlineData("resolve_loop.refused.example.json", "resolve_loop.output.schema.json")]
    // The decision ledger (SB-P5-11): a decision remembered, the open list with its counts, the scored list (one replaced
    // since), an outcome right, an unknown asking again, a repeat that changes nothing, a memory that is no decision, and
    // calibration ready (a band shown, the others counts only) and not ready (counts only).
    [InlineData("remember.decision.example.json", "remember.output.schema.json")]
    [InlineData("list_decisions.example.json", "list_decisions.output.schema.json")]
    [InlineData("list_decisions.scored.example.json", "list_decisions.output.schema.json")]
    [InlineData("record_outcome.example.json", "record_outcome.output.schema.json")]
    [InlineData("record_outcome.unknown.example.json", "record_outcome.output.schema.json")]
    [InlineData("record_outcome.unchanged.example.json", "record_outcome.output.schema.json")]
    [InlineData("record_outcome.refused.example.json", "record_outcome.output.schema.json")]
    [InlineData("decision_calibration.example.json", "decision_calibration.output.schema.json")]
    [InlineData("decision_calibration.not_ready.example.json", "decision_calibration.output.schema.json")]
    // Due dates (SB-P5-10): a date read from a memory's words, two dates and none taken, a memory's due date, all three
    // windows and the overdue one, a snooze, a clear, a clear with no date, and an until that has come refused.
    [InlineData("remember.due.example.json", "remember.output.schema.json")]
    [InlineData("remember.due_not_taken.example.json", "remember.output.schema.json")]
    [InlineData("get.due.example.json", "get.output.schema.json")]
    [InlineData("list_due.example.json", "list_due.output.schema.json")]
    [InlineData("list_due.overdue.example.json", "list_due.output.schema.json")]
    [InlineData("resolve_due.example.json", "resolve_due.output.schema.json")]
    [InlineData("resolve_due.cleared.example.json", "resolve_due.output.schema.json")]
    [InlineData("resolve_due.unchanged.example.json", "resolve_due.output.schema.json")]
    [InlineData("resolve_due.refused.example.json", "resolve_due.output.schema.json")]
    // Duplicates on save (SB-P5-02, migration 063) and the protection rules (SB-P5-04).
    [InlineData("remember.duplicate.example.json", "remember.output.schema.json")]
    [InlineData("get.duplicate.example.json", "get.output.schema.json")]
    [InlineData("list_duplicates.example.json", "list_duplicates.output.schema.json")]
    [InlineData("update.duplicate_kept.example.json", "update.output.schema.json")]
    [InlineData("supersede.protected.example.json", "supersede.output.schema.json")]
    [InlineData("app_memories.duplicates.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.keep_copy.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.capture.duplicate.example.json", "app_memories.output.schema.json")]
    // Standing instructions (SB-P5-12): one saved and one refused at its scope's limit; the list, one that may fire, one
    // over the limit and one held without its words; one stopped, stopped again and a memory that never was one; what a
    // question fires in recall; and a memory that is one, as get serves it.
    [InlineData("remember.standing.example.json", "remember.output.schema.json")]
    [InlineData("remember.standing_limit.example.json", "remember.output.schema.json")]
    [InlineData("list_standing.example.json", "list_standing.output.schema.json")]
    [InlineData("stop_standing.example.json", "stop_standing.output.schema.json")]
    [InlineData("stop_standing.unchanged.example.json", "stop_standing.output.schema.json")]
    [InlineData("stop_standing.refused.example.json", "stop_standing.output.schema.json")]
    [InlineData("recall.standing.example.json", "recall.output.schema.json")]
    [InlineData("get.standing.example.json", "get.output.schema.json")]
    // One resolve for every queue (SB-P5-15, migration 071): an item taken off, a snooze the brief still counts, an insight and a
    // candidate, a second resolve that does nothing, and a person's step refused.
    [InlineData("resolve.example.json", "resolve.output.schema.json")]
    [InlineData("resolve.snoozed.example.json", "resolve.output.schema.json")]
    [InlineData("resolve.insight.example.json", "resolve.output.schema.json")]
    [InlineData("resolve.candidate.example.json", "resolve.output.schema.json")]
    [InlineData("resolve.unchanged.example.json", "resolve.output.schema.json")]
    [InlineData("resolve.refused.example.json", "resolve.output.schema.json")]
    // The brief (SB-P5-08, migration 069): each section the head of a list, and one with nothing to say.
    [InlineData("brief.example.json", "brief.output.schema.json")]
    [InlineData("brief.quiet.example.json", "brief.output.schema.json")]
    [InlineData("app_memories.brief.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.brief.quiet.example.json", "app_memories.output.schema.json")]
    // The resurfaced memory (SB-P5-17, migration 070): the day's pick, none eligible, and a dismissal.
    [InlineData("app_memories.resurface.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resurface.none.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resurface_dismiss.example.json", "app_memories.output.schema.json")]
    // Contradiction candidates (SB-P5-03, migration 068): named on a write and a read, the review, a proposal and its refusal,
    // a person's decision, and an AI client's supersede of a queued pair refused.
    [InlineData("remember.contradiction.example.json", "remember.output.schema.json")]
    [InlineData("get.contradiction.example.json", "get.output.schema.json")]
    [InlineData("list_contradictions.example.json", "list_contradictions.output.schema.json")]
    [InlineData("propose_contradiction.example.json", "propose_contradiction.output.schema.json")]
    [InlineData("propose_contradiction.not_offered.example.json", "propose_contradiction.output.schema.json")]
    [InlineData("supersede.needs_person.example.json", "supersede.output.schema.json")]
    [InlineData("app_memories.contradictions.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.settle_contradiction.example.json", "app_memories.output.schema.json")]
    // A person's review queues (SB-P6-01).
    [InlineData("app_memories.queues.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.insight_candidates.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.insight_candidates.rejected.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.insight_stream.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.insight_stream.quiet.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.insight_history.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.review_insight.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.release.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.keep_copy.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.settle.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.unchanged.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.still_true.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.confirm_insight.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve.reject_candidate.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.refused.not_offered.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.refused.protected.example.json", "app_memories.output.schema.json")]
    // The memory UI's last pieces (SB-P6-01, run 22): the standing instructions, a person's stop of one, and a step for several.
    [InlineData("app_memories.standing.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.stop_standing.example.json", "app_memories.output.schema.json")]
    // A person's release of a work item's claim (migration 081).
    [InlineData("app_memories.release_claim.example.json", "app_memories.output.schema.json")]
    [InlineData("app_memories.resolve_all.example.json", "app_memories.output.schema.json")]
    // Insight candidates (SB-P5-05): the waiting list, best first; a dry run (no id, no time found, one page); a candidate
    // used, one rejected, the same decision again, and the other decision on a decided one refused.
    [InlineData("list_insight_candidates.example.json", "list_insight_candidates.output.schema.json")]
    [InlineData("list_insight_candidates.dry_run.example.json", "list_insight_candidates.output.schema.json")]
    [InlineData("resolve_insight_candidate.example.json", "resolve_insight_candidate.output.schema.json")]
    [InlineData("resolve_insight_candidate.rejected.example.json", "resolve_insight_candidate.output.schema.json")]
    [InlineData("resolve_insight_candidate.unchanged.example.json", "resolve_insight_candidate.output.schema.json")]
    [InlineData("resolve_insight_candidate.refused.example.json", "resolve_insight_candidate.output.schema.json")]
    [InlineData("list_insight_candidates.rejected.example.json", "list_insight_candidates.output.schema.json")]
    // The one insight stream (SB-P5-06): a proposed insight on a day that saved one; a day with no insight, and a snooze
    // ended; a review, the same again, a reopen, one that stays closed, and the login that saved it refused; an insight's history.
    [InlineData("list_insight_stream.example.json", "list_insight_stream.output.schema.json")]
    [InlineData("list_insight_stream.quiet.example.json", "list_insight_stream.output.schema.json")]
    [InlineData("review_insight.example.json", "review_insight.output.schema.json")]
    [InlineData("review_insight.unchanged.example.json", "review_insight.output.schema.json")]
    [InlineData("review_insight.reopened.example.json", "review_insight.output.schema.json")]
    [InlineData("review_insight.closed.example.json", "review_insight.output.schema.json")]
    [InlineData("review_insight.refused.example.json", "review_insight.output.schema.json")]
    [InlineData("insight_history.example.json", "insight_history.output.schema.json")]
    [InlineData("get_prompt_capsule.example.json", "get_prompt_capsule.output.schema.json")]
    [InlineData("get_prompt_capsule.unchanged.example.json", "get_prompt_capsule.output.schema.json")]
    [InlineData("get_prompt_capsule.refused.example.json", "get_prompt_capsule.output.schema.json")]
    public void The_contract_fixtures_match_their_schemas(string fixture, string schema) =>
        Schemas.Valid(Schemas.Folder("mcp"), schema, Fixture(fixture));

    [Fact] public void The_checker_refuses_what_the_schemas_refuse()
    {
        var latest = Fixture("app_latest_glucose.example.json");
        latest["subject"] = "Alex";
        Assert.Equal(["/subject: does not match ^[a-z][a-z0-9-]{0,31}$"], Schemas.Check(Schemas.Folder("mcp"), "app_latest_glucose.output.schema.json", latest));
        var fortnight = Fixture("app_recent_glucose.example.json");
        fortnight["sources"]![1]!["timeBasis"] = "device-local";
        Assert.NotEmpty(Schemas.Check(Schemas.Folder("mcp"), "app_recent_glucose.output.schema.json", fortnight));
        // A part with a member the contract doesn't name, a focus with none, a Home grid of four parts or of null.
        var arranged = Fixture("app_dashboards.layout.example.json");
        arranged["layout"]!["parts"]![1]!["colour"] = "teal";
        Assert.NotEmpty(Schemas.Check(Schemas.Folder("mcp"), "app_dashboards.output.schema.json", arranged));
        var unfocused = Fixture("app_dashboards.map.example.json");
        unfocused["map"]!["focus"] = new JsonObject();
        Assert.NotEmpty(Schemas.Check(Schemas.Folder("mcp"), "app_dashboards.output.schema.json", unfocused));
        var heard = Fixture("app_screens.example.json");
        heard["screen"]!["settings"]!["grid"]!["parts"]!.AsArray().RemoveAt(4);
        Assert.NotEmpty(Schemas.Check(Schemas.Folder("mcp"), "app_screens.output.schema.json", heard));
        heard["screen"]!["settings"]!["grid"] = null;
        Assert.NotEmpty(Schemas.Check(Schemas.Folder("mcp"), "app_screens.output.schema.json", heard));
    }

    [Fact] public void Adapter_descriptors_match_the_descriptor_schema()
    {
        var folder = Schemas.Folder("adapters");
        var descriptors = Directory.GetFiles(folder, "*.json").Where(f => !f.EndsWith("adapter.schema.json", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(descriptors);
        foreach (var file in descriptors)
        {
            var descriptor = JsonNode.Parse(File.ReadAllText(file));
            Schemas.Valid(folder, "adapter.schema.json", descriptor);
            Assert.Equal((string?)descriptor!["name"] + ".json", Path.GetFileName(file));
        }
    }
}
