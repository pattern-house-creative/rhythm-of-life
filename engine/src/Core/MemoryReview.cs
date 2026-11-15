using Npgsql;

namespace Knowledge;

// The review queue for held memories (SB-P5-01; D-039, D-160; docs/dev/memory.md section 5): what a person looks at
// before releasing a hold, newest held first. A memory is in it while it is held and its reader reads its own resources
// row (the one read predicate, Database.Visible, so never one in the trash or of a scope the reader does not read). An
// item names the memory, why it is held (its reason, score and the signals the holding write's audit row recorded),
// when and by which login, channel and client that write came, and whether this caller may release it; never its words,
// tags or project, which Get serves a person whole for review. ReviewCount counts with the list's own predicates, so a
// count is the length of the list it summarises. Release and ReleaseAll lift holds only through a person's channel (the
// app, or the terminal through a person's own terminal login, 044), never an AI client's (mcp), as release_memory_hold
// (036) decides for each; ReleaseAll takes the revision the person reviewed for each memory, so one that changed since is
// answered conflict and stays held.
//
// A person's channels serve it, with memory.tools on (SB-P3-22): the app's Memories view lists the queue and releases
// several at the revisions its person read (app_memories review and release_all), and the terminal lists it and
// releases one at the revision read (rhythm release, a person's own terminal login). No MCP tool lists it: list_recent
// lists a held memory without its words already.

// One held memory as the queue serves it. HeldAt: when the write that held it came (the memory's newest audit row in its
// own scope marked held), else when it last changed (a hold an undo or an import brought). HeldBy: the person that
// write's login speaks for, or none; Channel and Client: how it came. MayRelease: whether this caller may release it
// through the channel it named.
public sealed record MemoryReviewItem(Guid Id, string Subject, string Scope, string? Author, string Source, string Status, string Reason,
    double Score, IReadOnlyList<string> Signals, DateTimeOffset HeldAt, string? HeldBy, string? Channel, string? Client, bool MayRelease,
    int Revision);

public sealed record MemoryReviewCursor(DateTimeOffset HeldAt, Guid Id);

// A queue's filters, each narrowing what the reader reads: a scope (private or shared, never widening) and a reason
// (instruction, hidden, too_long, burst or held_project). Limit 1 to 50; After continues from a page's Next.
public sealed record MemoryReviewFilter(string? Scope = null, string? Reason = null, int Limit = 20, MemoryReviewCursor? After = null);

public sealed record MemoryReviewPage(IReadOnlyList<MemoryReviewItem> Items, MemoryReviewCursor? Next);

// One memory ReleaseAll was asked to release, and what came of it: released, unchanged (not held), unknown, forbidden or
// conflict (it changed since the revision given; Revision is the one it is at).
public sealed record MemoryReleaseResult(Guid Id, string Result, int? Revision = null);

public sealed partial class MemoryStore
{
    public const int ReviewMax = 50, ReleaseAllMax = 50;
    static readonly string[] Reasons = [MemoryHolds.Instruction, MemoryHolds.Hidden, MemoryHolds.TooLong, MemoryHolds.Burst, MemoryHolds.HeldProject];

    // The held memories the reader reads, each once, with the write that held it: its newest audit row marked held
    // (036, 041, 045) from the reader's history floor on (034's memory_history_floor, as History cuts), so nothing from a
    // stretch the reader's history does not reach; when the floor hides it, the memory's last change and no signals.
    const string HeldRows = """
        SELECT m.id,m.subject_id,m.scope,m.author_id,m.source,m.status,m.hold,coalesce(m.hold_score,0)::float8 AS score,
          coalesce(h.signals,'[]'::jsonb) AS signals,coalesce(h.at,m.updated_at) AS held_at,q.person_id AS held_by,h.channel,h.client,
          m.revision
        FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
        LEFT JOIN LATERAL (SELECT e.at,e.principal_id,e.channel,e.client,e.detail->'held'->'signals' AS signals FROM memory_events e
          WHERE e.memory_id=m.id AND e.subject_id=m.subject_id AND e.detail ? 'held' AND e.revision>=memory_history_floor(@principal,m.id)
          ORDER BY e.id DESC LIMIT 1) h ON true
        LEFT JOIN principals q ON q.id=h.principal_id
        """;

    (List<string> Where, List<(string, object?)> Args) Reviewed(Guid principal, MemoryReviewFilter filter)
    {
        var where = new List<string> { "m.hold IS NOT NULL", Database.Visible("r") };
        var args = new List<(string, object?)> { ("principal", principal) };
        if (filter.Scope is not null)
        {
            string? scope;
            try { scope = MemorySettings.Named(filter.Scope); }
            catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
            if (scope is not null) { where.Add("m.scope=@scope"); args.Add(("scope", scope)); }
        }
        if (filter.Reason is { } reason)
        {
            var r = reason.Trim().ToLowerInvariant();
            if (!Reasons.Contains(r)) throw new MemoryRefused("invalid", $"reason is {string.Join(", ", Reasons)}");
            where.Add("m.hold=@reason"); args.Add(("reason", r));
        }
        return (where, args);
    }

    // The queue as the reader is served it. call: the channel the reader would release through, which MayRelease is for
    // (through an AI client's channel, false for every item).
    public async Task<MemoryReviewPage> Review(Guid principal, MemoryReviewFilter filter, MemoryCall call, CancellationToken ct)
    {
        if (filter.Limit is < 1 or > ReviewMax) throw new MemoryRefused("invalid", $"limit is 1 to {ReviewMax}");
        var (where, args) = Reviewed(principal, filter);
        args.AddRange([("limit", filter.Limit + 1), ("channel", call.Channel)]);
        var page = "";
        if (filter.After is { } after)
        {
            page = "WHERE (q.held_at,q.id)<(@afterAt,@afterId)";
            args.AddRange([("afterAt", after.HeldAt.ToUniversalTime()), ("afterId", after.Id)]);
        }
        await using var c = await source.OpenConnectionAsync(ct);
        // MayRelease: release_memory_hold's rule (036) for this caller, as the trash list's MayDeleteForever is delete's.
        await using var cmd = Database.Command(c, null, $"""
            SELECT q.*,memory_may_change(@principal,q.subject_id,q.author_id,scopes_on()) AND EXISTS(SELECT 1 FROM principals c WHERE c.id=@principal
              AND (c.role='app' AND @channel='app' OR c.role='harness' AND @channel='cli' AND c.terminal AND c.person_id IS NOT NULL))
            FROM ({HeldRows} WHERE {string.Join(" AND ", where)}) q {page}
            ORDER BY q.held_at DESC,q.id DESC LIMIT @limit
            """, [.. args]);
        var items = new List<MemoryReviewItem>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                using var signals = System.Text.Json.JsonDocument.Parse(r.GetString(8));
                items.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), Text(3), r.GetString(4), r.GetString(5), r.GetString(6),
                    Math.Round(r.GetDouble(7), 2), [.. signals.RootElement.EnumerateArray().Select(s => s.GetString()!)],
                    r.GetFieldValue<DateTimeOffset>(9), Text(10), Text(11), Text(12), r.GetBoolean(14), r.GetInt32(13)));
            }
        var next = items.Count > filter.Limit ? new MemoryReviewCursor(items[filter.Limit - 1].HeldAt, items[filter.Limit - 1].Id) : null;
        return new(items.Take(filter.Limit).ToList(), next);
    }

    // How many held memories the reader reads, by the queue's own predicates (its Limit and After aside).
    public async Task<int> ReviewCount(Guid principal, MemoryReviewFilter filter, CancellationToken ct)
    {
        var (where, args) = Reviewed(principal, filter);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"SELECT count(*) FROM ({HeldRows} WHERE {string.Join(" AND ", where)}) q", [.. args]);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // A person releases several held memories at once (a burst's, say), each at the revision they reviewed, at most
    // ReleaseAllMax a call, each its own release (Release): one that fails leaves the others released. Through an AI
    // client's channel nothing is read or written.
    public async Task<IReadOnlyList<MemoryReleaseResult>> ReleaseAll(Guid principal, IReadOnlyList<(Guid Id, int Revision)> memories, MemoryCall call,
        CancellationToken ct)
    {
        if (call.Channel == "mcp") throw new MemoryRefused("forbidden", "A held memory is released only by a person, from the app or the terminal, never by an AI client");
        if (memories.Count is 0 or > ReleaseAllMax) throw new MemoryRefused("invalid", $"Release 1 to {ReleaseAllMax} memories at a time");
        if (memories.Select(m => m.Id).Distinct().Count() != memories.Count) throw new MemoryRefused("invalid", "Name each memory once");
        var results = new List<MemoryReleaseResult>();
        foreach (var (id, revision) in memories)
        {
            try
            {
                var w = await Release(principal, id, revision, call, ct);
                results.Add(new(id, w.Unchanged ? "unchanged" : "released", w.Revision));
            }
            catch (MemoryRefused refused) when (refused.Code is "unknown" or "forbidden" or "conflict")
            {
                results.Add(new(id, refused.Code, refused.Revision));
            }
        }
        return results;
    }
}
