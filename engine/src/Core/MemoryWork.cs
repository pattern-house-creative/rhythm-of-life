using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Work items for native memories (memory.work, off by default; docs/dev/memory.md sections 5, 7.1 and 9; migration 079):
// a project's backlog, kept as memories. A work item is a memory, with its one id, words, project, links and history,
// that also has work properties: what kind of work it is (task, bug, feature or improvement), where it stands (backlog,
// ready, active, review, done or cancelled) and maybe a priority (1 first to 4 last). What it must do stays in the
// memory's words, which the review hold screens; no work property holds words. It is no personal commitment: it never
// carries the tag task, so it is never an open loop (MemoryLoops.cs), and it is no digest's source (MemoryDigests.cs).
//
// The list a reader is served is the work items it reads (the one read predicate on each memory's own row) that 079's
// memory_work_listed lists: live, not marked wrong, their window open. A backlog is its scope's, not one person's, so
// every reader of a memory is served it; only who may change the memory changes its properties (033's author lock).
// One clause makes every count and the list, so a count is its list's length.
//
// The memory tools create_work, list_work, update_work, claim_work and release_work call it, only with memory.work on beside
// memory.tools, and the app's release_claim. With it off nothing is listed, answered or written.
//
// Claims (migration 081): every AI client session of a household uses one login, so a claim id the engine makes and gives
// the caller is what tells one session's claim from another's. Only that id renews the claim, releases it through an AI
// client, or moves the item's state while it holds; a person releases any claim from the app. A claim lasts ClaimMinutes
// from when it was made or renewed, and is no change of the memory. Its label is for people to read and is never checked.

// Whose the work is (migration 081): a person of the install (kind person, their id) or an agent's label (kind agent).
public sealed record MemoryWorkAssignee(string Kind, string Name);

// A live claim as it is listed: its label and when it was made and ends; never its id.
public sealed record MemoryWorkClaim(string Label, DateTimeOffset ClaimedAt, DateTimeOffset ExpiresAt)
{
    public static MemoryWorkClaim? From(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null
        : new(e.GetProperty("label").GetString()!, e.GetProperty("claimedAt").GetDateTimeOffset(), e.GetProperty("expiresAt").GetDateTimeOffset());
}

// A claim as claim_work answers it to its holder: with its id, and whether this call renewed it.
public sealed record MemoryWorkClaimed(Guid Id, string Label, DateTimeOffset ClaimedAt, DateTimeOffset ExpiresAt, bool Renewed);

// What a work item is: its type, its state and its priority, 1 first to 4 last, or none; whose it is (081), and the claim
// that holds it now, when a read names them.
public sealed record MemoryWorkProps(string Type, string State, int? Priority, MemoryWorkAssignee? Assignee = null, MemoryWorkClaim? Claim = null)
{
    // 079's memory_work_json, with 081's assignee and claim beside it when the read names them; or none.
    public static MemoryWorkProps? From(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null
        : new(e.GetProperty("type").GetString()!, e.GetProperty("state").GetString()!,
            e.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null,
            e.TryGetProperty("assignee", out var a) ? Assigned(a) : null,
            e.TryGetProperty("claim", out var c) ? MemoryWorkClaim.From(c) : null);

    public static MemoryWorkAssignee? Assigned(JsonElement a) => a.ValueKind != JsonValueKind.Object ? null
        : new(a.GetProperty("kind").GetString()!, a.GetProperty("name").GetString()!);

    public static MemoryWorkProps? From(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return From(doc.RootElement);
    }
}

// A work item another waits on, by a depends_on link (migration 080): its id and its properties now. No words.
public sealed record MemoryWorkDependency(Guid Id, string Type, string State, int? Priority);

// How far the work items one depends on have come (migration 084's memory_work_depends): how many in all, whatever their
// state, and how many of those are done and cancelled; the rest are the ones it waits on. A milestone that depends on
// each piece of a step says with it how many pieces are done. Read at each read; nothing stores it.
public sealed record MemoryWorkDepends(int Total, int Done, int Cancelled)
{
    public static readonly MemoryWorkDepends None = new(0, 0, 0);

    // The counts as MemoryStore.DependsCounted answers them.
    public static MemoryWorkDepends From(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        return new(e.GetProperty("total").GetInt32(), e.GetProperty("done").GetInt32(), e.GetProperty("cancelled").GetInt32());
    }
}

// One work item as its reader is served it. Due: the memory's own due date (061), or none. Summary: what was said when
// it became done or cancelled, only while it is one of those and that change is its newest change of work properties.
// Content: a preview of at most the asked characters (Truncated when longer). One held for review (Hold says why) comes
// without its words, tags, project, due date and summary, as a list serves any held memory. BlockedBy: the unfinished work
// items it waits on (080's memory_work_waiting), first by priority, at most MemoryStore.BlockedByMax; Blocked: it is open
// (neither done nor cancelled) and waits on any. Depends (migration 084): how many work items it depends on in all, and how
// many of those are done and cancelled. All are read at each read; nothing stores them.
public sealed record MemoryWorkItem(Guid Id, int Revision, string Subject, string Scope, string? Author, string Status, MemoryWorkProps Work,
    MemoryDue? Due, string? Summary, IReadOnlyList<string> Tags, MemoryProjectRef? Project, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Content, bool Truncated, string? Hold, bool Blocked = false, IReadOnlyList<MemoryWorkDependency>? BlockedBy = null,
    MemoryWorkDepends? Depends = null);

// A page's place: its last item's priority as the list orders it (none after 4), when it was written and its id.
public sealed record MemoryWorkCursor(int Rank, DateTimeOffset CreatedAt, Guid Id);

// A list's filters, each exact and each narrowing what the caller reads already. Project: a slug or alias the caller
// reads, else unknown_project. State: one state, or open (every state but done and cancelled); none for every state.
// Type: one type. Priority: 1 to 4, or 0 for the items nobody ranked. Blocked (migration 080): true, only the blocked
// items; false, only those that are not. Assignee (081): a person's id or an agent's label, exactly. Claimed (081): true,
// only those a live claim holds; false, only the others.
public sealed record MemoryWorkFilter(string? Scope = null, string? Project = null, string? State = null, string? Type = null, int? Priority = null,
    bool? Blocked = null, string? Assignee = null, bool? Claimed = null);

// The asked list's whole length (Total), how many items each state holds under the other filters (States: all six, so
// a count is the length of the list that names its state), and one page, by priority (1 first, unranked last) and then
// oldest first: all read in one statement.
public sealed record MemoryWorkPage(int Total, IReadOnlyDictionary<string, int> States, IReadOnlyList<MemoryWorkItem> Items, MemoryWorkCursor? Next);

// What a work write answers: the memory as a write answers it, its properties after it and, for a change, before it; the
// summary a finishing change took; and the due date a new item was saved with. Claimed: the claim claim_work made or
// renewed, with its id; Released: the claim release_work ended; ClaimEnded: a finishing change ended the item's claim.
public sealed record MemoryWorkWritten(MemoryWrite Write, MemoryWorkProps Work, MemoryWorkProps? Before = null, string? Summary = null, MemoryDue? Due = null,
    MemoryWorkClaimed? Claimed = null, MemoryWorkClaim? Released = null, bool ClaimEnded = false);

public static class MemoryWork
{
    public const string Backlog = "backlog", Done = "done", Cancelled = "cancelled", Open = "open";
    // The link type one work item waits on another with (migration 080; LinkTypes.All).
    public const string DependsOn = "depends_on";
    public static readonly IReadOnlyList<string> Types = ["task", "bug", "feature", "improvement"];
    public static readonly IReadOnlyList<string> States = [Backlog, "ready", "active", "review", Done, Cancelled];
    public const int PriorityFirst = 1, PriorityLast = 4;
    // The longest summary a finished item takes: 079's memory_work_summary_chars(), which a test keeps equal.
    public const int SummaryMax = 280;
    // How long a claim holds (081's memory_work_claim_minutes()), and the longest label (081's memory_work_label), which a
    // test keeps equal.
    public const int ClaimMinutes = 120, LabelMax = 120;

    public static bool Finished(string state) => state is Done or Cancelled;

    public static string Type(string? named) => (named ?? "").Trim().ToLowerInvariant() is var t && Types.Contains(t) ? t
        : throw new MemoryRefused("invalid", "work_type is task, bug, feature or improvement");

    public static string State(string? named) => (named ?? "").Trim().ToLowerInvariant() is var s && States.Contains(s) ? s
        : throw new MemoryRefused("invalid", "state is backlog, ready, active, review, done or cancelled");

    public static int Priority(int priority) => priority is >= PriorityFirst and <= PriorityLast ? priority
        : throw new MemoryRefused("invalid", "priority is 1 (first) to 4 (last)");

    // A state a list names: one of the six, open (not done or cancelled), or all (none: every state).
    public static string? Listed(string? named) => (named ?? "all").Trim().ToLowerInvariant() switch
    {
        "all" or "" => null,
        Open => Open,
        var s when States.Contains(s) => s,
        _ => throw new MemoryRefused("invalid", "state is backlog, ready, active, review, done, cancelled, open (not done or cancelled) or all"),
    };
}

public sealed partial class MemoryStore
{
    public const int WorkMax = 50, BlockedByMax = 20;

    // How far the work items memory m depends on have come (084's memory_work_depends), as JSON text: {total, done,
    // cancelled}, all 0 for an item that depends on none. list_work and a memories feed read it alike.
    internal const string DependsCounted = """
        (SELECT jsonb_build_object('total',count(*),'done',count(*) FILTER (WHERE d.state='done'),
          'cancelled',count(*) FILTER (WHERE d.state='cancelled'))::text FROM memory_work_depends(m.id) d)
        """;

    // One page of the work list under a filter, with its whole length and each state's count, in one statement (one
    // snapshot). After continues from a page's Next.
    public async Task<MemoryWorkPage> Work(Guid principal, MemoryWorkFilter filter, int limit, MemoryWorkCursor? after, int previewChars, CancellationToken ct)
    {
        if (limit is < 1 or > WorkMax) throw new MemoryRefused("invalid", $"n is 1 to {WorkMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var state = MemoryWork.Listed(filter.State);
        var type = filter.Type is null ? null : MemoryWork.Type(filter.Type);
        if (filter.Priority is { } rank && rank != 0) MemoryWork.Priority(rank);
        string? scope;
        try { scope = MemorySettings.Named(filter.Scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        var where = $"{Database.Visible("r")} AND memory_work_listed(m.id)";
        var args = new List<NpgsqlParameter>
        {
            new("principal", NpgsqlDbType.Uuid) { Value = principal },
            new("state", NpgsqlDbType.Text) { Value = (object?)state ?? DBNull.Value },
            new("type", NpgsqlDbType.Text) { Value = (object?)type ?? DBNull.Value },
            new("priority", NpgsqlDbType.Integer) { Value = (object?)filter.Priority ?? DBNull.Value },
            new("blocked", NpgsqlDbType.Boolean) { Value = (object?)filter.Blocked ?? DBNull.Value },
            new("blockedMax", NpgsqlDbType.Integer) { Value = BlockedByMax },
            new("assignee", NpgsqlDbType.Text) { Value = (object?)(string.IsNullOrWhiteSpace(filter.Assignee) ? null : filter.Assignee.Trim()) ?? DBNull.Value },
            new("claimed", NpgsqlDbType.Boolean) { Value = (object?)filter.Claimed ?? DBNull.Value },
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterRank", NpgsqlDbType.Integer) { Value = after is { } a ? a.Rank : DBNull.Value },
            new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } b ? b.CreatedAt.ToUniversalTime() : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } d ? d.Id : DBNull.Value },
        };
        if (scope is not null)
        {
            where += " AND m.scope=@scope";
            args.Add(new("scope", NpgsqlDbType.Text) { Value = scope });
        }
        await using var c = await source.OpenConnectionAsync(ct);
        if (MemoryInput.Project(filter.Project) is { } project)
        {
            // A project counts only in a scope the caller reads and never while it is held, as a list's filter (Filtered);
            // one the caller does not know is answered with up to 10 it does.
            await using (var known = Database.Command(c, null, $"SELECT EXISTS(SELECT 1 FROM memory_projects p WHERE {ProjectNamed} AND {ProjectListed()})",
                ("principal", principal), ("name", project)))
                if (await known.ExecuteScalarAsync(ct) is not true)
                {
                    var names = await KnownProjects(c, principal, 10, ct);
                    throw new MemoryRefused("unknown_project", names.Count == 0 ? "No project by that name"
                        : $"No project by that name; known projects: {string.Join(", ", names)}", known: names);
                }
            // Its members, as a list reads them: the memories in it, and those of its scope tagged with one of its aliases.
            // A held memory is listed without its project, so a project never selects it.
            where += $"""
                 AND (m.hold IS NULL AND EXISTS(SELECT 1 FROM memory_projects p WHERE {ProjectNamed} AND NOT p.held AND (p.id=m.project_id
                  OR p.subject_id=m.subject_id AND m.tags && ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id))))
                """;
            args.Add(new("name", NpgsqlDbType.Text) { Value = project });
        }
        const string Asked = "(@state::text IS NULL OR q.state=@state OR @state='open' AND q.state NOT IN ('done','cancelled'))";
        await using var cmd = new NpgsqlCommand($"""
            WITH a AS (
              SELECT m.id,w.work_type,w.state,w.priority,coalesce(w.priority,5)::int AS rank,m.created_at,
                w.state NOT IN ('done','cancelled') AND EXISTS(SELECT 1 FROM memory_work_waiting(m.id)) AS blocked,
                w.assignee_person,w.assignee_label,EXISTS(SELECT 1 FROM memory_work_claims c WHERE c.memory_id=m.id AND c.expires_at>now()) AS claimed
              {From} JOIN memory_work w ON w.memory_id=m.id
              WHERE {where} AND (@type::text IS NULL OR w.work_type=@type) AND (@priority::int IS NULL OR coalesce(w.priority,0)=@priority)
                AND (@assignee::text IS NULL OR w.assignee_person=@assignee OR w.assignee_label=@assignee)),
            q AS (SELECT * FROM a WHERE (@blocked::boolean IS NULL OR a.blocked=@blocked) AND (@claimed::boolean IS NULL OR a.claimed=@claimed))
            SELECT t.total,t.backlog,t.ready,t.active,t.review,t.done,t.cancelled,x.* FROM (
              SELECT count(*) FILTER (WHERE {Asked}) AS total,
                count(*) FILTER (WHERE q.state='backlog') AS backlog,count(*) FILTER (WHERE q.state='ready') AS ready,
                count(*) FILTER (WHERE q.state='active') AS active,count(*) FILTER (WHERE q.state='review') AS review,
                count(*) FILTER (WHERE q.state='done') AS done,count(*) FILTER (WHERE q.state='cancelled') AS cancelled FROM q) t
            LEFT JOIN LATERAL (
              SELECT m.id,m.revision,m.subject_id,m.scope,m.author_id,m.status,(memory_work_json(q.work_type,q.state,q.priority)
                ||jsonb_strip_nulls(jsonb_build_object('assignee',memory_work_assignee_json(q.assignee_person,q.assignee_label),
                  'claim',memory_work_claim_json(memory_work_claim(m.id)))))::text,
                CASE WHEN m.hold IS NULL THEN memory_due_json(m.due_at,m.due_kind,m.due_source)::text END,
                CASE WHEN m.hold IS NULL AND q.state IN ('done','cancelled') THEN (SELECT e.detail->>'summary' FROM memory_events e
                  WHERE e.memory_id=m.id AND e.event='work_changed' AND e.revision=(SELECT max(s.revision) FROM memory_work_states s
                    WHERE s.memory_id=m.id AND s.revision<=m.revision) ORDER BY e.id DESC LIMIT 1) END,
                CASE WHEN m.hold IS NULL THEN m.tags ELSE ARRAY[]::text[] END,
                CASE WHEN m.hold IS NULL THEN pr.id END AS project,CASE WHEN m.hold IS NULL THEN pr.slug END,
                m.created_at,m.updated_at,CASE WHEN m.hold IS NULL THEN left(m.content,@chars) END,
                m.hold IS NULL AND length(m.content)>@chars,m.hold,q.rank,q.blocked,
                (SELECT coalesce(jsonb_agg(jsonb_build_object('id',d.id,'type',d.work_type,'state',d.state,'priority',d.priority)
                  ORDER BY coalesce(d.priority,5),d.created_at,d.id),'[]'::jsonb)::text
                 FROM (SELECT * FROM memory_work_waiting(m.id) x ORDER BY coalesce(x.priority,5),x.created_at,x.id LIMIT @blockedMax) d),
                {DependsCounted}
              FROM q JOIN memories m ON m.id=q.id LEFT JOIN memory_projects pr ON pr.id=m.project_id
              WHERE {Asked} AND (@afterRank::int IS NULL OR (q.rank,q.created_at,q.id)>(@afterRank,@afterAt,@afterId))
              ORDER BY q.rank,q.created_at,q.id LIMIT @limit) x ON true
            ORDER BY x.rank,x.created_at,x.id
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryWorkItem>();
        var ranks = new List<int>();
        var total = 0;
        var states = MemoryWork.States.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                total = (int)r.GetInt64(0);
                for (var i = 0; i < MemoryWork.States.Count; i++) states[MemoryWork.States[i]] = (int)r.GetInt64(1 + i);
                if (r.IsDBNull(7)) continue;
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new(r.GetGuid(7), r.GetInt32(8), r.GetString(9), r.GetString(10), Text(11), r.GetString(12), MemoryWorkProps.From(r.GetString(13))!,
                    MemoryDue.From(Text(14)), Text(15), r.GetFieldValue<string[]>(16), r.IsDBNull(17) ? null : new(r.GetGuid(17), r.GetString(18)),
                    r.GetFieldValue<DateTimeOffset>(19), r.GetFieldValue<DateTimeOffset>(20), Text(21), r.GetBoolean(22), Text(23),
                    r.GetBoolean(25), Dependencies(r.GetString(26)), MemoryWorkDepends.From(r.GetString(27))));
                ranks.Add(r.GetInt32(24));
            }
        var next = items.Count > limit ? new MemoryWorkCursor(ranks[limit - 1], items[limit - 1].CreatedAt, items[limit - 1].Id) : null;
        return new(total, states, items.Take(limit).ToList(), next);
    }

    // The work items one waits on, as 080's memory_work_waiting answers them for the list.
    static IReadOnlyList<MemoryWorkDependency> Dependencies(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(d => new MemoryWorkDependency(d.GetProperty("id").GetGuid(), d.GetProperty("type").GetString()!,
            d.GetProperty("state").GetString()!, d.GetProperty("priority").ValueKind == JsonValueKind.Number ? d.GetProperty("priority").GetInt32() : null))];
    }

    // A new memory that is a work item of the project its draft names (079's remember_memory_work): written as Create
    // writes a memory, or as Remember writes one with a due date when `when` is given (an ISO date or date and time, read
    // in the install's zone when it has no offset; a deadline, kind due), with its properties in the same transaction. A
    // date in its words is not read: a work item is dated only when the call says so. The idempotency key covers the
    // properties and the due date as asked. Its links on save and its check for copies run as for any new memory.
    public async Task<MemoryWorkWritten> CreateWork(Guid principal, MemoryDraft draft, string type, string? state, int? priority, string? when,
        MemoryCall call, CancellationToken ct, string? assignee = null)
    {
        var work = Props(type, state, priority, assignee);
        if (MemoryInput.Project(draft.Project) is null)
            throw new MemoryRefused("invalid", "A work item belongs to a project: name its project, a slug from list_projects or a new one. Nothing was saved");
        var (memory, dropped) = Drafted(draft);
        JsonObject? due = null, asked = null;
        if (when is not null)
        {
            var at = DueDates.When(when, await InstallZone(ct), DateTimeOffset.UtcNow);
            due = new() { ["at"] = Utc(at), ["kind"] = DueDates.Due, ["source"] = DueDates.Explicit };
            asked = new() { ["when"] = when.Trim() };
        }
        var (write, answer) = await WriteAnswer(
            "SELECT remember_memory_work(@principal,@memory::jsonb,@work::jsonb,@due::jsonb,@asked::jsonb,@channel,@client,@key)::text", principal, call, dropped, ct,
            ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString()), ("work", NpgsqlDbType.Jsonb, work.ToJsonString()),
            ("due", NpgsqlDbType.Jsonb, due?.ToJsonString()), ("asked", NpgsqlDbType.Jsonb, asked?.ToJsonString()));
        return WorkFrom(await Linked(principal, write, true, call, ct), answer);
    }

    // A memory that is here already becomes a work item (079's set_memory_work), by whoever may change it, as a change of
    // it: it must be in a project, and no personal task, decision or standing instruction. Expected: the revision the
    // caller read. No idempotency key: a repeat with the same properties answers unchanged.
    public async Task<MemoryWorkWritten> MakeWork(Guid principal, Guid memory, string type, string? state, int? priority, int? expected, MemoryCall call,
        CancellationToken ct, string? assignee = null)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Making a memory a work item takes no idempotency key: doing it again answers unchanged");
        var (write, answer) = await WriteAnswer("SELECT set_memory_work(@principal,@id,@work::jsonb,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("work", NpgsqlDbType.Jsonb, Props(type, state, priority, assignee).ToJsonString()), ("expected", NpgsqlDbType.Integer, expected));
        return WorkFrom(write, answer);
    }

    // A work item's state, type, priority or assignee changed (079's update_memory_work as 081 replaced it), each only when
    // given; noPriority and noAssignee take them off. A state that becomes done or cancelled takes a summary, kept in the
    // change's audit row. While a live claim holds the item, a change of state needs that claim's id (claim), and done or
    // cancelled with it ends the claim. Expected: the revision the caller read. No idempotency key: a repeat answers unchanged.
    public async Task<MemoryWorkWritten> UpdateWork(Guid principal, Guid memory, string? type, string? state, int? priority, bool noPriority, string? summary,
        int? expected, MemoryCall call, CancellationToken ct, string? assignee = null, bool noAssignee = false, Guid? claim = null)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A work change takes no idempotency key: making it again answers unchanged");
        if (priority is not null && noPriority) throw new MemoryRefused("invalid", "A priority is either given or cleared, not both");
        var change = new JsonObject();
        if (type is not null) change["type"] = MemoryWork.Type(type);
        if (state is not null) change["state"] = MemoryWork.State(state);
        if (priority is { } rank) change["priority"] = MemoryWork.Priority(rank);
        else if (noPriority) change["priority"] = null;
        if (assignee is not null && noAssignee) throw new MemoryRefused("invalid", "An assignee is either given or cleared, not both");
        if (assignee is not null) change["assignee"] = Assignee(assignee);
        else if (noAssignee) change["assignee"] = null;
        if (change.Count == 0) throw new MemoryRefused("invalid", "A work change names a state, a type, a priority or an assignee");
        if (claim is { } held) change["claim"] = held.ToString("D");
        if (summary is not null)
        {
            var said = summary.Trim();
            if (said.Length is 0 or > MemoryWork.SummaryMax || said.Any(char.IsControl))
                throw new MemoryRefused("invalid", $"A summary is 1 to {MemoryWork.SummaryMax} characters on one line: nothing was changed. Put the detail in the memory itself (append)");
            change["summary"] = said;
        }
        var (write, answer) = await WriteAnswer("SELECT update_memory_work(@principal,@id,@change::jsonb,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("change", NpgsqlDbType.Jsonb, change.ToJsonString()), ("expected", NpgsqlDbType.Integer, expected));
        return WorkFrom(write, answer);
    }

    // claim_work (081's claim_memory_work): the caller claims an open work item, or with `claim` renews the live claim that
    // id names. Label: who holds it, for people to read, such as "<session title> - <agent>"; left out, the client's name.
    // The answer's Claimed holds the claim id, the caller's to keep. No idempotency key: a repeat with the id renews.
    public async Task<MemoryWorkWritten> ClaimWork(Guid principal, Guid memory, Guid? claim, string? label, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A claim takes no idempotency key: claiming again with its claim_id renews it");
        var (write, answer) = await WriteAnswer("SELECT claim_memory_work(@principal,@id,@claim,@label,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("claim", NpgsqlDbType.Uuid, claim), ("label", NpgsqlDbType.Text, label is null ? null : Label(label)),
            ("expected", NpgsqlDbType.Integer, expected));
        return WorkFrom(write, answer);
    }

    // release_work (081's release_memory_work): the live claim on the item ends. Through an AI client only with its id; through
    // a person's channel (the app's release_claim) with or without it. With no live claim it answers unchanged.
    public async Task<MemoryWorkWritten> ReleaseWork(Guid principal, Guid memory, Guid? claim, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A release takes no idempotency key: releasing again answers unchanged");
        var (write, answer) = await WriteAnswer("SELECT release_memory_work(@principal,@id,@claim,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("claim", NpgsqlDbType.Uuid, claim));
        return WorkFrom(write, answer);
    }

    // Work properties as 079's writers take them, checked here first so a wrong word is refused before anything is read;
    // an assignee (081) only when given, so a call without one asks what it asked before.
    static JsonObject Props(string type, string? state, int? priority, string? assignee = null)
    {
        var props = new JsonObject
        {
            ["type"] = MemoryWork.Type(type),
            ["state"] = state is null ? MemoryWork.Backlog : MemoryWork.State(state),
            ["priority"] = priority is { } rank ? MemoryWork.Priority(rank) : null,
        };
        if (assignee is not null) props["assignee"] = Assignee(assignee);
        return props;
    }

    // A label or an assignee as a call gives it, trimmed: 1 to LabelMax characters on one line. The database screens it
    // and decides whether an assignee names a person.
    static string Label(string label)
    {
        var t = label.Trim();
        if (t.Length is 0 or > MemoryWork.LabelMax || t.Any(char.IsControl))
            throw new MemoryRefused("invalid", $"A label is 1 to {MemoryWork.LabelMax} characters on one line, such as \"<session title> - <agent>\": nothing was changed");
        return t;
    }
    static string Assignee(string assignee) => Label(assignee);

    static MemoryWorkWritten WorkFrom(MemoryWrite write, JsonElement answer)
    {
        var work = MemoryWorkProps.From(answer.GetProperty("work"))!;
        if (answer.TryGetProperty("assignee", out var a)) work = work with { Assignee = MemoryWorkProps.Assigned(a) };
        var claim = answer.TryGetProperty("claim", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        return new(write, work, answer.TryGetProperty("before", out var before) ? MemoryWorkProps.From(before) : null,
            answer.TryGetProperty("summary", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null,
            answer.TryGetProperty("due", out var due) ? MemoryDue.From(due) : null,
            claim.ValueKind == JsonValueKind.Object && claim.TryGetProperty("id", out var id)
                ? new(id.GetGuid(), claim.GetProperty("label").GetString()!, claim.GetProperty("claimedAt").GetDateTimeOffset(),
                    claim.GetProperty("expiresAt").GetDateTimeOffset(), claim.GetProperty("renewed").GetBoolean()) : null,
            claim.ValueKind == JsonValueKind.Object && !claim.TryGetProperty("id", out _) ? MemoryWorkClaim.From(claim) : null,
            answer.TryGetProperty("claimEnded", out var ended) && ended.ValueKind == JsonValueKind.True);
    }
}
