using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Prompt capsules (SB-P3-08; ledger rows CTX-01 to CTX-05, MCP-12; docs/dev/memory.md section 7.1): a "start here"
// context an AI client reads before anything else, built from a scope's canonical native memories, one per fixed slot.
// Adapted from Second Brain's prompt capsules (src/prompt-capsule at the pinned commit, MIT; NOTICE): the same slots in
// the same order, canonical only, a validation report instead of a guess, a 12 000-character budget, byte-stable text
// with no ids or times in it, a SHA-256 hash of the text and a strong ETag of the whole answer.
//
// What Rhythm does its own way: a memory's slot is a typed column (025's capsule and capsule_slot, written only by
// set_memory_capsule, migration 039, and only by a person: never through an AI client's channel, D-172), never a tag,
// so a malformed slot, a second status or a capsule tag of the wrong project can't exist and the report names only
// what can: a slot two memories fill (both left out), a slot no memory fills (missing), and a memory too long for the
// budget on its own (invalid). One scope per capsule (private or Family, D-044's words), read through the one read
// predicate on each memory's own row, never a held, deprecated, expired or trashed one; no cache, so nothing is shared
// between people (CTX-05: Postgres reads the capsule rows by their index, as D-164 reasons for counts). Complete means
// every slot is filled once and nothing was left out.
//
// A person writes a slot (SB-P3-22), with memory.tools on: the app's Memories view (app_memories capsule) and the
// terminal's rhythm capsule, through a person's own terminal login; the MCP tools refuse one (D-172). The tool and
// resources that read a capsule are served only with memory.capsules on (MemorySettings.CapsulesOn), so with both
// switches off every existing answer is as it was.
public static class CapsuleSlots
{
    public const string Schema = "prompt-capsule.v1", McpSchema = "prompt-capsule-mcp.v1", EtagNamespace = "pcv1";
    public const int MaxChars = 12_000, MaxCandidates = 200;
    public const string Core = "core", Project = "project";

    // The fixed slots in their fixed order (upstream's src/prompt-capsule/types.ts); 025's checks list the same names.
    public static readonly IReadOnlyList<string> CoreSlots = ["identity", "preferences", "constraints", "principles"];
    public static readonly IReadOnlyList<string> ProjectSlots = ["current-state", "decisions", "open-questions"];

    public static IReadOnlyList<string> For(string kind) => kind == Core ? CoreSlots : ProjectSlots;
}

// A canonical memory in one of a capsule's slots, as the read finds it; a section is one that fills its slot.
public sealed record CapsuleCandidate(Guid Id, string Slot, string Content);
public sealed record CapsuleSection(string Slot, Guid Source, string Content);
// Why a memory in a slot was left out: content-too-large (it can't fit the budget on its own) or empty-content.
public sealed record CapsuleInvalid(Guid Entry, string Slot, string Reason);
public sealed record CapsuleDuplicate(string Slot, IReadOnlyList<Guid> Entries);
public sealed record CapsuleSelection(IReadOnlyList<CapsuleSection> Sections, IReadOnlyList<CapsuleInvalid> Invalid,
    IReadOnlyList<CapsuleDuplicate> Duplicates, IReadOnlyList<string> Missing);
public sealed record CapsuleText(string Text, IReadOnlyList<CapsuleSection> Sections, IReadOnlyList<string> Omitted);

// A memory in a capsule's slot that the capsule does not serve, as a person's view of it reads it: its status (a draft
// or one marked wrong), whether it is held for review, and whether it is past its validity.
public sealed record CapsuleWaiting(Guid Id, string Slot, string Status, bool Held, bool Expired);

// A capsule as read: its payload (the answer), the payload's text exactly as served, and its strong ETag.
public sealed record BuiltCapsule(JsonObject Payload, string Body, string Etag);

// Which capsule: core or project (a project's slug or alias), in a scope named as a memory write names one (private or
// shared, or upstream's personal and company, or Family; D-044), else the caller's own: its person's private scope with
// scopes on, else Family.
public sealed record CapsuleRequest(string Kind, string? Project = null, string? Scope = null);

public static class Capsule
{
    // The pure part (upstream's select.ts and serialize.ts): no reads, no clock, no ranking and no summarising. Each slot
    // in its fixed order: one memory fills it; two or more are reported and none is chosen; a memory that can't fit the
    // budget on its own is reported invalid and leaves its slot to the others; a slot nobody names is missing.
    public static CapsuleSelection Select(string kind, IEnumerable<CapsuleCandidate> candidates, int maxChars = CapsuleSlots.MaxChars)
    {
        var slots = CapsuleSlots.For(kind);
        var all = candidates.ToList();
        var invalid = new List<CapsuleInvalid>();
        var bySlot = slots.ToDictionary(s => s, _ => new List<CapsuleSection>(), StringComparer.Ordinal);
        foreach (var c in all)
        {
            if (!bySlot.TryGetValue(c.Slot, out var filled)) throw new ArgumentException($"{c.Slot} is not a slot of a {kind} capsule");
            if (c.Content.Trim().Length == 0) { invalid.Add(new(c.Id, c.Slot, "empty-content")); continue; }
            var section = new CapsuleSection(c.Slot, c.Id, c.Content);
            if (Serialize(kind, [section], maxChars).Sections.Count == 0) { invalid.Add(new(c.Id, c.Slot, "content-too-large")); continue; }
            filled.Add(section);
        }
        var named = all.Select(c => c.Slot).ToHashSet(StringComparer.Ordinal);
        return new(
            [.. slots.Where(s => bySlot[s].Count == 1).Select(s => bySlot[s][0])],
            [.. invalid.OrderBy(i => i.Entry.ToString(), StringComparer.Ordinal)],
            [.. slots.Where(s => bySlot[s].Count > 1).Select(s => new CapsuleDuplicate(s,
                [.. bySlot[s].Select(x => x.Source).OrderBy(id => id.ToString(), StringComparer.Ordinal)]))],
            [.. slots.Where(s => !named.Contains(s))]);
    }

    // Byte-stable text: the schema, the kind and each section's slot and words, in the fixed slot order, laid out as
    // upstream's JSON.stringify(value, null, 2) lays it out (so the same words give upstream's text). Line endings and
    // trailing spaces are made one way. A section is in whole or left out whole: once one does not fit the budget, it and
    // every later one are left out (Omitted), so the text is always a prefix of the full order.
    public static CapsuleText Serialize(string kind, IReadOnlyList<CapsuleSection> sections, int maxChars = CapsuleSlots.MaxChars)
    {
        var order = CapsuleSlots.For(kind);
        if (sections.Select(s => s.Slot).Distinct(StringComparer.Ordinal).Count() != sections.Count) throw new ArgumentException("A slot appears more than once");
        if (sections.Any(s => !order.Contains(s.Slot))) throw new ArgumentException($"A slot is not a slot of a {kind} capsule");
        var normalized = sections.Select(s => s with { Content = Normalize(s.Content) }).OrderBy(s => IndexOf(order, s.Slot)).ToList();
        if (Render(kind, []).Length > maxChars) throw new ArgumentException("The budget is smaller than an empty capsule");
        var included = new List<CapsuleSection>();
        IReadOnlyList<string> omitted = [];
        for (var i = 0; i < normalized.Count; i++)
        {
            if (Render(kind, [.. included, normalized[i]]).Length > maxChars) { omitted = [.. normalized.Skip(i).Select(s => s.Slot)]; break; }
            included.Add(normalized[i]);
        }
        return new(Render(kind, included), included, omitted);
    }

    static int IndexOf(IReadOnlyList<string> order, string slot)
    {
        for (var i = 0; i < order.Count; i++) if (order[i] == slot) return i;
        return -1;
    }

    // CRLF and CR made LF, trailing spaces and tabs off each line, empty lines off both ends (upstream's normalizeContent).
    public static string Normalize(string value)
    {
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd(' ', '\t')).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    static string Render(string kind, IReadOnlyList<CapsuleSection> sections) => CapsuleJson.Write(new JsonObject
    {
        ["schema"] = CapsuleSlots.Schema,
        ["kind"] = kind,
        ["sections"] = new JsonArray([.. sections.Select(s => (JsonNode)new JsonObject { ["slot"] = s.Slot, ["content"] = s.Content })]),
    });

    // SHA-256 of a text's UTF-8, in lower-case hex.
    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // A strong ETag of a representation (upstream's strongEtag): the namespace, then SHA-256 of the namespace, a NUL and
    // the representation, quoted.
    public static string Etag(string representation) =>
        $"\"{CapsuleSlots.EtagNamespace}-{Sha256(CapsuleSlots.EtagNamespace + "\0" + representation)}\"";

    // Whether an If-None-Match value (a list, a weak tag or *) names this ETag, compared weakly (upstream's
    // ifNoneMatchMatches).
    public static bool Matches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        static string Weak(string v) => v.StartsWith("W/", StringComparison.Ordinal) ? v[2..] : v;
        return ifNoneMatch.Split(',').Select(v => v.Trim()).Any(v => v == "*" || Weak(v) == Weak(etag));
    }

    // The answer for a selection read in one scope: the text and its hash, which memory fills each slot, what was left
    // out and why, and the counts. Its text is the body the ETag is of.
    public static BuiltCapsule Answer(string kind, string? project, string scope, CapsuleSelection selection)
    {
        var text = Serialize(kind, selection.Sections);
        var complete = text.Omitted.Count == 0 && text.Sections.Count > 0 && selection.Invalid.Count == 0
            && selection.Duplicates.Count == 0 && selection.Missing.Count == 0;
        var payload = new JsonObject
        {
            ["ok"] = true,
            ["schema"] = CapsuleSlots.Schema,
            ["kind"] = kind,
        };
        if (project is not null) payload["project_id"] = project;
        payload["scope"] = scope;
        payload["workspace"] = scope == "shared" ? "company" : "personal";
        payload["prompt_hash"] = "sha256:" + Sha256(text.Text);
        payload["text"] = text.Text;
        payload["sections"] = new JsonArray([.. text.Sections.Select(s => (JsonNode)new JsonObject
            { ["slot"] = s.Slot, ["source_entry_id"] = s.Source.ToString() })]);
        payload["omitted_slots"] = Strings(text.Omitted);
        payload["missing_slots"] = Strings(selection.Missing);
        payload["complete"] = complete;
        payload["populated"] = text.Sections.Count > 0;
        payload["invalid_entries"] = new JsonArray([.. selection.Invalid.Select(i => (JsonNode)new JsonObject
            { ["entry_id"] = i.Entry.ToString(), ["slot"] = i.Slot, ["reason"] = i.Reason })]);
        payload["duplicate_slots"] = new JsonArray([.. selection.Duplicates.Select(d => (JsonNode)new JsonObject
            { ["slot"] = d.Slot, ["entry_ids"] = Strings(d.Entries.Select(e => e.ToString())) })]);
        payload["char_count"] = text.Text.Length;
        payload["max_chars"] = CapsuleSlots.MaxChars;
        var body = CapsuleJson.Write(payload);
        return new(payload, body, Etag(body));
    }

    static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
}

// JSON laid out exactly as JavaScript's JSON.stringify(value, null, 2): two-space indents, LF line ends, `"key": value`,
// empty arrays and objects as [] and {}, and in strings only the quote, the backslash, control characters and lone
// surrogates escaped (short forms where JSON has them, else \u00xx in lower-case hex). The same on every platform, which
// System.Text.Json's writer is not (its line end and escaping follow its options and the platform).
public static class CapsuleJson
{
    public static string Write(JsonNode? node)
    {
        var b = new StringBuilder();
        Write(b, node, 0);
        return b.ToString();
    }

    static void Write(StringBuilder b, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null: b.Append("null"); return;
            case JsonObject o:
                if (o.Count == 0) { b.Append("{}"); return; }
                b.Append("{\n");
                var first = true;
                foreach (var (key, value) in o)
                {
                    if (!first) b.Append(",\n");
                    first = false;
                    b.Append(' ', 2 * (depth + 1));
                    String(b, key);
                    b.Append(": ");
                    Write(b, value, depth + 1);
                }
                b.Append('\n').Append(' ', 2 * depth).Append('}');
                return;
            case JsonArray a:
                if (a.Count == 0) { b.Append("[]"); return; }
                b.Append("[\n");
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) b.Append(",\n");
                    b.Append(' ', 2 * (depth + 1));
                    Write(b, a[i], depth + 1);
                }
                b.Append('\n').Append(' ', 2 * depth).Append(']');
                return;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) String(b, s);
                else if (v.TryGetValue<bool>(out var flag)) b.Append(flag ? "true" : "false");
                else if (v.TryGetValue<int>(out var i32)) b.Append(i32.ToString(CultureInfo.InvariantCulture));
                else if (v.TryGetValue<long>(out var i64)) b.Append(i64.ToString(CultureInfo.InvariantCulture));
                else throw new ArgumentException("A capsule holds strings, whole numbers, true, false and null only");
                return;
        }
    }

    static void String(StringBuilder b, string s)
    {
        b.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    var lone = char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])
                        : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));
                    if (c < ' ' || lone) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }
}

// Reading a capsule for one caller: the scope it names (or its own), which it must read; the project by slug or alias in
// that scope; then the canonical memories in that capsule's slots that the caller reads now, through the one read
// predicate on each memory's own row, never one held (D-039), deprecated (D-030), past its valid_until or in the trash.
// Refusals are MemoryRefused: invalid, forbidden (a scope this login does not read), unknown_project (Known lists up to
// 10 of that scope's projects) and too_many_candidates (over 200 canonical memories in the capsule's slots: upstream's
// bound, so a capsule never reads an unbounded set).
public sealed class PromptCapsules(NpgsqlDataSource source)
{
    public async Task<BuiltCapsule> Build(Guid principal, CapsuleRequest request, CancellationToken ct)
    {
        var (kind, named, wanted) = Checked(request);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
        var (subject, scope, project, slug) = await Resolve(c, tx, principal, named, wanted, ct);

        var candidates = new List<CapsuleCandidate>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT m.id,m.capsule_slot,m.content
            FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            WHERE m.subject_id=@subject AND m.capsule=@kind AND (@project::uuid IS NULL OR m.project_id=@project)
              AND m.status='canonical' AND m.hold IS NULL AND (m.valid_until IS NULL OR m.valid_until>now())
              AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) AND {Database.Visible("r")}
            ORDER BY m.id LIMIT @take
            """, ("principal", principal), ("subject", subject), ("kind", kind), ("take", CapsuleSlots.MaxCandidates + 1)))
        {
            cmd.Parameters.Add(new NpgsqlParameter("project", NpgsqlDbType.Uuid) { Value = (object?)project ?? DBNull.Value });
            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) candidates.Add(new(row.GetGuid(0), row.GetString(1), row.GetString(2)));
        }
        await tx.CommitAsync(ct);
        if (candidates.Count > CapsuleSlots.MaxCandidates)
            throw new MemoryRefused("too_many_candidates",
                $"This capsule has more than {CapsuleSlots.MaxCandidates} canonical memories in its slots; take some out of their slots and ask again");
        return Capsule.Answer(kind, slug, scope, Capsule.Select(kind, candidates));
    }

    // The memories in the same capsule's slots that the caller reads and the capsule does not serve (a person's view of
    // their capsule at the terminal, SB-P3-22): a draft, one marked wrong, one held for review or one past its validity,
    // each with what it waits for; at most 200, by slot. The same scope, project and refusals as Build; a person's
    // channel reads it, which is why a held memory's slot is named here and never in an AI client's capsule.
    public async Task<IReadOnlyList<CapsuleWaiting>> Waiting(Guid principal, CapsuleRequest request, CancellationToken ct)
    {
        var (kind, named, wanted) = Checked(request);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
        var (subject, _, project, _) = await Resolve(c, tx, principal, named, wanted, ct);
        var waiting = new List<CapsuleWaiting>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT m.id,m.capsule_slot,m.status,m.hold IS NOT NULL,m.valid_until IS NOT NULL AND m.valid_until<=now()
            FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            WHERE m.subject_id=@subject AND m.capsule=@kind AND (@project::uuid IS NULL OR m.project_id=@project)
              AND NOT (m.status='canonical' AND m.hold IS NULL AND (m.valid_until IS NULL OR m.valid_until>now()))
              AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) AND {Database.Visible("r")}
            ORDER BY m.capsule_slot,m.id LIMIT @take
            """, ("principal", principal), ("subject", subject), ("kind", kind), ("take", CapsuleSlots.MaxCandidates)))
        {
            cmd.Parameters.Add(new NpgsqlParameter("project", NpgsqlDbType.Uuid) { Value = (object?)project ?? DBNull.Value });
            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) waiting.Add(new(row.GetGuid(0), row.GetString(1), row.GetString(2), row.GetBoolean(3), row.GetBoolean(4)));
        }
        await tx.CommitAsync(ct);
        return waiting;
    }

    // A request's kind, project and scope, checked before anything is read.
    static (string Kind, string? Project, string? Scope) Checked(CapsuleRequest request)
    {
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not (CapsuleSlots.Core or CapsuleSlots.Project)) throw new MemoryRefused("invalid", "kind is core or project");
        var named = MemoryInput.Project(request.Project);
        if (kind == CapsuleSlots.Project && named is null) throw new MemoryRefused("invalid", "project_id is required for a project capsule");
        if (kind == CapsuleSlots.Core && named is not null) throw new MemoryRefused("invalid", "project_id is only for a project capsule");
        if (named is { Length: > 128 }) throw new MemoryRefused("invalid", "project_id is a project's slug or alias, at most 128 characters");
        string? wanted;
        try { wanted = MemorySettings.Named(request.Scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message.Replace("scope is", "workspace is", StringComparison.Ordinal)); }
        return (kind, named, wanted);
    }

    // The capsule's subject and scope, which the caller must read, and its project by slug or alias in that scope.
    static async Task<(string Subject, string Scope, Guid? Project, string? Slug)> Resolve(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string? named,
        string? wanted, CancellationToken ct)
    {
        bool scoped; string? person;
        await using (var who = Database.Command(c, tx,
            "SELECT (SELECT value FROM install_settings WHERE key='memory.scopes') IS DISTINCT FROM 'off',(SELECT person_id FROM principals WHERE id=@principal)",
            ("principal", principal)))
        await using (var row = await who.ExecuteReaderAsync(ct))
        {
            await row.ReadAsync(ct);
            scoped = row.GetBoolean(0);
            person = row.IsDBNull(1) ? null : row.GetString(1);
        }
        var subject = Subject(scoped, person, wanted);
        var scope = subject == RhythmConfig.Household ? "shared" : "private";
        await using (var reads = Database.Command(c, tx, $"SELECT {Database.Allowed("@subject", "'memory'", "@scope")}",
            ("principal", principal), ("subject", subject), ("scope", scope)))
            if (await reads.ExecuteScalarAsync(ct) is not true)
                throw new MemoryRefused("forbidden", scope == "shared" ? "This login does not read Family's memories" : "This login does not read that private scope");

        Guid? project = null; string? slug = null;
        if (named is not null)
        {
            await using (var find = Database.Command(c, tx, $"SELECT p.id,p.slug FROM memory_projects p WHERE p.subject_id=@subject AND NOT p.held AND {MemoryStore.ProjectNamed}",
                ("subject", subject), ("name", named)))
            await using (var row = await find.ExecuteReaderAsync(ct))
                if (await row.ReadAsync(ct)) { project = row.GetGuid(0); slug = row.GetString(1); }
            if (project is null)
            {
                var known = new List<string>();
                await using (var list = Database.Command(c, tx, "SELECT p.slug FROM memory_projects p WHERE p.subject_id=@subject AND p.status='active' AND NOT p.held ORDER BY 1 LIMIT 10",
                    ("subject", subject)))
                await using (var row = await list.ExecuteReaderAsync(ct))
                    while (await row.ReadAsync(ct)) known.Add(row.GetString(0));
                throw new MemoryRefused("unknown_project", known.Count == 0 ? "No project by that name in this scope"
                    : $"No project by that name in this scope; known projects: {string.Join(", ", known)}", known: known);
            }
        }
        return (subject, scope, project, slug);
    }

    // The capsule's subject: a named scope, else the caller's own. With scopes off every memory is the household's, so a
    // private capsule is refused as a private memory is (D-048); with them on a login with no person has no private scope.
    static string Subject(bool scoped, string? person, string? wanted)
    {
        if (!scoped)
            return wanted == "private"
                ? throw new MemoryRefused("invalid", "Private capsules wait until per-person scopes are on; with them off every memory is the household's")
                : RhythmConfig.Household;
        if (wanted == "shared") return RhythmConfig.Household;
        if (wanted == "private")
            return person ?? throw new MemoryRefused("invalid", "This client has no person, so it has no private scope: ask for the shared scope (Family)");
        return person ?? RhythmConfig.Household;
    }
}

public sealed partial class MemoryStore
{
    // Put a memory into one capsule slot, or take it out (capsule and slot both null): core identity, preferences,
    // constraints or principles; project current-state, decisions or open-questions, for a memory in a project (that
    // project's capsule). Only a canonical memory fills its slot when the capsule is read. A change like any other
    // (migration 039): the author lock, an expected revision, a version and an audit row; no idempotency key, since
    // setting the slot a memory has answers unchanged. A person's step: through an AI client's channel (mcp) it is
    // refused (forbidden) before anything is read; the app or the terminal sets or clears a slot.
    public Task<MemoryWrite> SetCapsule(Guid principal, Guid memory, string? capsule, string? slot, int? expected, MemoryCall call, CancellationToken ct) =>
        call.Key is not null
            ? throw new MemoryRefused("invalid", "A capsule slot takes no idempotency key: setting it again answers unchanged")
            : Write("SELECT set_memory_capsule(@principal,@id,@capsule,@slot,@expected,@channel,@client)::text", principal, call, [], ct,
                ("id", NpgsqlDbType.Uuid, memory), ("capsule", NpgsqlDbType.Text, capsule?.Trim().ToLowerInvariant()),
                ("slot", NpgsqlDbType.Text, slot?.Trim().ToLowerInvariant()), ("expected", NpgsqlDbType.Integer, expected));
}
