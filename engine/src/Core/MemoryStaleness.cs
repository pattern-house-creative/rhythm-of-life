using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Staleness for native memories (SB-P4-09; story 12; docs/dev/memory.md sections 5 and 9; migration 053): a memory's
// volatility (the caller's word, D-033) says how soon it may go out of date (volatile 14 days, state 90, durable and
// none never); a bounded nightly pass (MemoryStaleness.Job, on the nightly job runner) marks the memories whose time has
// passed since they were last changed or confirmed, walking each scope from its own cursor; the stale queue is one
// predicate (053's memory_stale_reason) that the count and the list read together; and still true (StillTrue) confirms
// a memory without changing it: its checked time moves, its mark and retraction flags come off, one audit row says so,
// and no revision moves and no version is kept or pruned.
//
// Only the memory tools call it (list_stale and still_true; SB-P3-09), and only with memory.tools on the pass is a
// nightly job at all (NightlyJobs.Daily), off by default. With it off nothing is listed, answered or written.

// One memory in the stale queue, as a reader who may confirm it is served it. Reason: not_confirmed (the pass found its
// volatility's time had passed since Since) or retracted_source (a memory it was built on was marked wrong, 049; it wins).
// Since: when it was last changed or confirmed. FoundAt: when the pass found it (not_confirmed only). ConfirmedAt: its
// last still true, or none. Content: a preview of at most the asked characters (Truncated when longer); a held memory is
// never in the queue. RetractedSources: as MemoryRecord's.
public sealed record MemoryStale(Guid Id, string Subject, string Scope, string? Author, string Status, string? Volatility, string Reason,
    DateTimeOffset Since, DateTimeOffset? FoundAt, DateTimeOffset? ConfirmedAt, string Content, bool Truncated, IReadOnlyList<Guid> RetractedSources,
    int Revision);

public sealed record MemoryStaleCursor(DateTimeOffset Since, Guid Id);

// The queue's length for this reader and one page of it, oldest unconfirmed first, read in one statement: Total is the
// length of the whole list the pages walk.
public sealed record MemoryStalePage(int Total, IReadOnlyList<MemoryStale> Items, MemoryStaleCursor? Next);

// What still true answers: the memory as a write answers it (its revision unchanged), its new checked time, why it was in
// the queue (Was: not_confirmed, retracted_source or none) and the retracted sources whose flags came off. Unchanged: it
// was confirmed at this revision in the last day and is not in the queue, so nothing was written (CheckedAt is that time).
public sealed record MemoryConfirmed(Guid Id, int Revision, string Subject, string Scope, string Status, string Scopes, MemoryProjectRef? Project,
    bool Unchanged, DateTimeOffset CheckedAt, string? Was, IReadOnlyList<Guid> Retracted);

public sealed partial class MemoryStore
{
    public const int StaleMax = 50;

    // The queue a reader acts on: memories it reads (the one read predicate) and may change (033's author lock: still true
    // is a change's step), in the stale queue (053's memory_stale_reason), optionally of one scope. One clause for the
    // count and the list, so the count is the length of the list.
    static (string Where, List<NpgsqlParameter> Args) StaleQueue(Guid principal, string? scope)
    {
        var args = new List<NpgsqlParameter> { new("principal", NpgsqlDbType.Uuid) { Value = principal } };
        var where = $"""
            {Database.Visible("r")} AND memory_stale_reason(m.id) IS NOT NULL
              AND memory_may_change(@principal,m.subject_id,m.author_id,NOT coalesce({Database.ScopesOff},false))
            """;
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        if (named is not null)
        {
            where += " AND m.scope=@scope";
            args.Add(new("scope", NpgsqlDbType.Text) { Value = named });
        }
        return (where, args);
    }

    // The stale queue's length for this reader.
    public async Task<int> StaleCount(Guid principal, string? scope, CancellationToken ct)
    {
        var (where, args) = StaleQueue(principal, scope);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) {From} WHERE {where}", c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // One page of the queue and its whole length, in one statement (one snapshot): the memories longest unchanged and
    // unconfirmed first, After continuing from a page's Next.
    public async Task<MemoryStalePage> Stale(Guid principal, string? scope, int limit, MemoryStaleCursor? after, int previewChars, CancellationToken ct)
    {
        if (limit is < 1 or > StaleMax) throw new MemoryRefused("invalid", $"n is 1 to {StaleMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var (where, args) = StaleQueue(principal, scope);
        args.AddRange([
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.Since.ToUniversalTime() : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value }]);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (
              SELECT m.id,memory_stale_reason(m.id) AS reason,memory_stale_clock(m.id) AS since
              {From} WHERE {where})
            SELECT t.total,p.* FROM (SELECT count(*) AS total FROM q) t
            LEFT JOIN LATERAL (
              SELECT m.id,m.subject_id,m.scope,m.author_id,m.status,m.volatility,q.reason,q.since,
                CASE WHEN q.reason='not_confirmed' THEN k.stale_at END,k.confirmed_at,left(m.content,@chars),length(m.content)>@chars,
                ARRAY(SELECT f.retracted_id FROM memory_retraction_flags f LEFT JOIN memories s ON s.id=f.retracted_id
                  WHERE f.memory_id=m.id AND (s.id IS NULL OR s.subject_id=m.subject_id) ORDER BY f.flagged_at,f.retracted_id
                  LIMIT {RetractedSourcesMax}),m.revision
              FROM q JOIN memories m ON m.id=q.id LEFT JOIN memory_checks k ON k.memory_id=m.id
              WHERE @afterAt::timestamptz IS NULL OR (q.since,q.id)>(@afterAt,@afterId)
              ORDER BY q.since,q.id LIMIT @limit) p ON true
            ORDER BY p.since,p.id
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryStale>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                total = (int)r.GetInt64(0);
                if (r.IsDBNull(1)) continue;
                DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
                items.Add(new(r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), At(9), At(10), r.GetString(11),
                    r.GetBoolean(12), r.GetFieldValue<Guid[]>(13), r.GetInt32(14)));
            }
        var next = items.Count > limit ? new MemoryStaleCursor(items[limit - 1].Since, items[limit - 1].Id) : null;
        return new(total, items.Take(limit).ToList(), next);
    }

    // Still true: the memory stands as it is. Expected: the revision the caller read, refused (conflict) when it moved on.
    // No idempotency key: a repeat within a day answers unchanged.
    public async Task<MemoryConfirmed> StillTrue(Guid principal, Guid memory, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Still true takes no idempotency key: confirming again answers unchanged");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string json;
        await using (var cmd = new NpgsqlCommand("SELECT confirm_memory(@principal,@id,@expected,@channel,@client)::text", c, tx))
        {
            cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
            cmd.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = memory });
            cmd.Parameters.Add(new("expected", NpgsqlDbType.Integer) { Value = (object?)expected ?? DBNull.Value });
            cmd.Parameters.Add(new("channel", NpgsqlDbType.Text) { Value = call.Channel });
            cmd.Parameters.Add(new("client", NpgsqlDbType.Text) { Value = (object?)AdminAudit.Client(call.Client) ?? DBNull.Value });
            try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        }
        await tx.CommitAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return ConfirmedFrom(doc.RootElement);
    }

    static MemoryConfirmed ConfirmedFrom(JsonElement o)
    {
        MemoryProjectRef? project = o.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.Object
            ? new(p.GetProperty("id").GetGuid(), p.GetProperty("slug").GetString()!) : null;
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("revision").GetInt32(), o.GetProperty("subject").GetString()!,
            o.GetProperty("scope").GetString()!, o.GetProperty("status").GetString()!, o.GetProperty("scopes").GetString()!, project,
            o.TryGetProperty("unchanged", out var u) && u.ValueKind == JsonValueKind.True, o.GetProperty("checked_at").GetDateTimeOffset(),
            o.TryGetProperty("was", out var was) && was.ValueKind == JsonValueKind.String ? was.GetString() : null,
            [.. o.GetProperty("retracted").EnumerateArray().Select(x => x.GetGuid())]);
    }
}

// The nightly staleness pass (SB-P4-09, D-017): each scope with native memories walked in id order from its own cursor
// (job_cursors, scope = the subject), a piece at a time, each piece and its cursor in one transaction, at most PerScope
// memories a scope a run; a scope walked to its end starts again from its first memory at its next run. A run stopped by
// its budget or a failure goes on from the last piece it saved. It writes a mark only for a memory whose time has passed
// and takes off a mark that no longer holds (migration 053), and keeps a cursor only while it has one to keep, so an
// install with no native memory, or with none due and every scope walked whole, writes nothing.
public static class MemoryStaleness
{
    public const string JobId = "memory-staleness";
    public const int Piece = 200, PerScope = 1000;

    // The job, added to the daily turn's list only with memory.tools on (NightlyJobs.Daily).
    public static NightlyJob Job(NpgsqlDataSource source, ILogger log, int piece = Piece, int perScope = PerScope) =>
        new(JobId, "memory staleness pass", NightlyJobs.DailyBudget, (context, ct) => Pass(source, context, log, piece, perScope, ct));

    // afterPiece: called with the scope once each of its pieces has committed (a test stops a run there).
    public static async Task<NightlyRun> Pass(NpgsqlDataSource source, NightlyContext context, ILogger log, int piece, int perScope, CancellationToken ct,
        Action<string>? afterPiece = null)
    {
        if (piece is < 1 or > 1000 || perScope < piece) throw new ArgumentException("a piece of 1 to 1000 memories, and a run of at least one piece a scope");
        var subjects = new List<string>();
        await using (var cmd = source.CreateCommand("SELECT DISTINCT subject_id FROM memories ORDER BY subject_id"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) subjects.Add(r.GetString(0));
        long looked = 0, marked = 0, cleared = 0;
        var more = false;
        foreach (var subject in subjects)
        {
            // Where this scope's walk stopped last, as saved; none on its first run and once a walk reached its end.
            Guid? saved = await context.Cursor(subject, ct) is { } place && place.ValueKind == JsonValueKind.Object
                && place.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.String && Guid.TryParse(a.GetString(), out var g) ? g : null;
            var taken = 0;
            while (true)
            {
                if (taken >= perScope) { more = true; break; }
                ct.ThrowIfCancellationRequested();
                var take = Math.Min(piece, perScope - taken);
                await using var c = await source.OpenConnectionAsync(ct);
                await using var tx = await c.BeginTransactionAsync(ct);
                string json;
                await using (var cmd = new NpgsqlCommand("SELECT memory_staleness_pass(@subject,@after,@limit)::text", c, tx))
                {
                    cmd.Parameters.Add(new("subject", NpgsqlDbType.Text) { Value = subject });
                    cmd.Parameters.Add(new("after", NpgsqlDbType.Uuid) { Value = saved is { } at ? at : DBNull.Value });
                    cmd.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = take });
                    json = (string)(await cmd.ExecuteScalarAsync(ct))!;
                }
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                var inspected = o.GetProperty("inspected").GetInt32();
                var full = o.GetProperty("more").GetBoolean();
                Guid? next = full ? o.GetProperty("last").GetGuid() : null;
                // The piece and its place commit together, with no token the budget cancels once the piece is done; a place
                // that has not changed (a scope walked whole again from its start) is not written again.
                if (next != saved)
                    await context.Save(c, tx, next is { } n ? new JsonObject { ["after"] = n.ToString() } : null, subject, CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
                saved = next;
                taken += inspected;
                looked += inspected;
                marked += o.GetProperty("marked").GetInt32();
                cleared += o.GetProperty("cleared").GetInt32();
                afterPiece?.Invoke(subject);
                if (!full) break;
            }
        }
        if (marked + cleared > 0)
            log.LogInformation("Memory staleness: {Checked} memories checked, {Stale} found out of date, {Cleared} no longer out of date", looked, marked, cleared);
        return NightlyRun.Of(("memories checked", looked), ("found out of date", marked), ("no longer out of date", cleared)) with { More = more };
    }
}
