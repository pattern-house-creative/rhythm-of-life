using System.Data;
using Npgsql;

namespace Knowledge;

// A native memory's history and its earlier states (docs/dev/memory.md section 5, read its history; SB-P3-03): each
// change newest first, with who made it, through which channel and client, and the state it wrote; and one state read
// by its revision. Every read takes the read predicate on the memory's own resources row first, as Get does, so a
// memory the caller does not read, one in the trash and an unknown id answer alike (null). Then migration 034's rule
// (memory_history_floor): a reader sees history from the change that brought the memory into a scope it reads, as
// upstream; its author sees all of it while the author reads every scope it has been in (an author who has left Family
// is cut from its Family stretch, as every reader outside Family is). A state that was held is served without its words
// (no text, tags or project, as a list serves a held memory, D-149), unless a later state this reader sees holds the same
// text released; its tags and project then only when that later state holds them too, since a release vouched for
// nothing else (a caller's tags can carry the held text's words). The 20
// newest earlier states are kept (D-036, 034); every change's audit row is kept, so an older change is still listed,
// without its words.
//
// The history tool and get's version argument (SB-P3-09) and the app's Memories view (SB-P3-12, SB-P3-13) read it, only
// with memory.tools on, so with it off every existing answer is as it was.

// One change, newest first. Event: what its audit row records (created, appended, updated, status_changed, released,
// reverted, trashed, restored, capsule_changed, imported, and what later items add). Person: who made it, the person
// its login is bound to, or none (a login with no person, or the operator on the host, whose channel is cli). Fields:
// what an update or an undo changed. To: the revision an undo went back to. Subject and Scope: where the memory was
// after it. Kept: whether the state it wrote is still kept; then Status and a Preview of its text (at most 160
// characters, white space folded), no preview when that state was held. A change at the same revision as another
// carries the same state: a trip through the trash (038's trashed and restored, which move no revision) shares the
// revision of the change before it.
public sealed record MemoryHistoryItem(int Revision, string Event, DateTimeOffset At, string? Person, string Channel, string? Client,
    IReadOnlyList<string> Fields, string Subject, string Scope, bool Kept, string? Status, string? Preview, bool Held, int? To = null);

// A memory's history as one reader sees it. Revision: the current one. Next: the revision to pass as before for the
// next, older page; none at the end. Cut: earlier history lies in a scope this reader does not read; EarlierBy: its
// author, when that earlier history was in the author's own scope (none when it was Family's). Kept: how many earlier states a memory keeps; OldestKept: the oldest revision this reader sees
// whose state is still kept; Pruned: states older than that, which this reader would see, are no longer kept (their
// changes are still listed).
public sealed record MemoryHistory(Guid Id, int Revision, IReadOnlyList<MemoryHistoryItem> Items, int? Next, bool Cut,
    string? EarlierBy, int Kept, int OldestKept, bool Pruned);

// One state of a memory, by its revision: what it held then, and who wrote it (Person, Channel and Client, as in
// history). Current: the memory's current state. Held: that state was held, so its words are left out (no Content,
// tags or project; Hold says why). A held state a later one released has its text, and its tags and project only when
// the releasing state holds the same ones. RetiredAt: when a later change replaced it.
public sealed record MemoryVersion(Guid Id, int Revision, bool Current, string Subject, string Scope, string Status, string? Kind,
    int? Importance, string? Volatility, IReadOnlyList<string> Tags, MemoryProjectRef? Project, string? Content, bool Held, string? Hold,
    DateTimeOffset? ValidFrom, bool UnknownStart, DateTimeOffset? ValidUntil, DateTimeOffset WrittenAt, DateTimeOffset? RetiredAt,
    string? Person, string? Channel, string? Client);

// A read by revision: the state, or why there is none. Missing: not_visible (from before the change that brought the
// memory to this reader, whatever is kept there, as upstream answers it), pruned (no longer kept; OldestKept is the
// oldest revision this reader can still read) or no_version (above the current revision).
public sealed record MemoryVersionRead(MemoryVersion? Version, string? Missing = null, int? OldestKept = null);

public sealed partial class MemoryStore
{
    public const int HistoryPreview = 160, HistoryDefault = 20, HistoryMax = 100;

    public async Task<MemoryHistory?> History(Guid principal, Guid id, int? before, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > HistoryMax) throw new MemoryRefused("invalid", $"limit is 1 to {HistoryMax}");
        if (before is < 1) throw new MemoryRefused("invalid", "before is a revision, 1 or more");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await Seen(c, tx, principal, id, ct) is not { } seen) return null;
        // The newest limit revisions below before that this reader sees, one more to know whether a page follows; each
        // with every audit row at it and the state it wrote, when kept.
        await using var cmd = Database.Command(c, tx, $"""
            WITH {States}, revs AS (
              SELECT DISTINCT e.revision FROM memory_events e WHERE e.memory_id=@id AND e.revision>=@floor AND e.revision<@before
              ORDER BY e.revision DESC LIMIT @take)
            SELECT e.revision,e.event,e.at,q.person_id,e.channel,e.client,
              CASE WHEN jsonb_typeof(e.detail->'fields')='array' THEN ARRAY(SELECT jsonb_array_elements_text(e.detail->'fields')) ELSE ARRAY[]::text[] END,
              coalesce(s.subject_id,e.subject_id),coalesce(s.scope,CASE WHEN e.subject_id='household' THEN 'shared' ELSE 'private' END),
              s.revision IS NOT NULL,s.status,
              CASE WHEN NOT s.held THEN left(btrim(regexp_replace(s.content,'\s+',' ','g')),{HistoryPreview}) END,coalesce(s.held,false),
              CASE WHEN e.event='reverted' AND jsonb_typeof(e.detail->'to')='number' THEN (e.detail->>'to')::int END
            FROM memory_events e JOIN revs ON revs.revision=e.revision
            LEFT JOIN principals q ON q.id=e.principal_id
            LEFT JOIN states s ON s.revision=e.revision
            WHERE e.memory_id=@id
            ORDER BY e.revision DESC,e.id DESC
            """, ("id", id), ("floor", seen.Floor), ("before", before ?? int.MaxValue), ("take", limit + 1));
        var items = new List<MemoryHistoryItem>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                items.Add(new(r.GetInt32(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), Text(r, 3), r.GetString(4), Text(r, 5),
                    r.GetFieldValue<string[]>(6), r.GetString(7), r.GetString(8), r.GetBoolean(9), Text(r, 10), Text(r, 11), r.GetBoolean(12),
                    r.IsDBNull(13) ? null : r.GetInt32(13)));
        await tx.CommitAsync(ct);
        var revisions = items.Select(i => i.Revision).Distinct().ToList();
        int? next = null;
        if (revisions.Count > limit)
        {
            next = revisions[limit - 1];
            items.RemoveAll(i => i.Revision < next);
        }
        return new(id, seen.Revision, items, next, seen.Floor > 1, seen.Floor > 1 && seen.AuthorsBelow ? seen.Author : null, seen.Kept,
            seen.OldestKept, seen.OldestKept > seen.Floor);
    }

    // One state by its revision, the current one included; null when the caller does not read the memory at all.
    public async Task<MemoryVersionRead?> GetVersion(Guid principal, Guid id, int revision, CancellationToken ct)
    {
        if (revision < 1) throw new MemoryRefused("invalid", "A revision is 1 or more");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await Seen(c, tx, principal, id, ct) is not { } seen) return null;
        if (revision > seen.Revision) return new(null, "no_version");
        if (revision < seen.Floor) return new(null, "not_visible");
        await using var cmd = Database.Command(c, tx, $"""
            WITH {States}
            SELECT s.current,s.subject_id,s.scope,s.status,s.kind,s.importance,s.volatility,s.held,
              CASE WHEN s.veiled THEN ARRAY[]::text[] ELSE s.tags END,CASE WHEN NOT s.veiled THEN p.id END,
              CASE WHEN NOT s.veiled THEN p.slug END,CASE WHEN NOT s.veiled THEN p.name END,CASE WHEN NOT s.held THEN s.content END,s.hold,
              CASE WHEN isfinite(s.valid_from) THEN s.valid_from END,coalesce(s.valid_from='-infinity',false),
              CASE WHEN isfinite(s.valid_until) THEN s.valid_until END,s.written_at,s.retired_at,w.person_id,w.channel,w.client
            FROM states s LEFT JOIN memory_projects p ON p.id=s.project_id
            LEFT JOIN LATERAL (SELECT q.person_id,e.channel,e.client FROM memory_events e LEFT JOIN principals q ON q.id=e.principal_id
              WHERE e.memory_id=@id AND e.revision=s.revision ORDER BY e.id LIMIT 1) w ON true
            WHERE s.revision=@revision
            """, ("id", id), ("floor", seen.Floor), ("revision", revision));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new(null, "pruned", seen.OldestKept);
        DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return new(new(id, revision, r.GetBoolean(0), r.GetString(1), r.GetString(2), r.GetString(3), Text(r, 4),
            r.IsDBNull(5) ? null : r.GetInt16(5), Text(r, 6), r.GetFieldValue<string[]>(8),
            r.IsDBNull(9) ? null : new(r.GetGuid(9), r.GetString(10), r.GetString(11)), Text(r, 12), r.GetBoolean(7), Text(r, 13),
            At(14), r.GetBoolean(15), At(16), r.GetFieldValue<DateTimeOffset>(17), At(18), Text(r, 19), Text(r, 20), Text(r, 21)));
    }

    // What a reader is served of a memory's history: the current revision, the author, the oldest revision it sees (034's
    // floor), the oldest of those whose state is kept, how many a memory keeps, and whether any change below the floor was
    // in the author's own scope; null when it does not read the memory.
    sealed record HistorySpan(int Revision, string? Author, int Floor, int OldestKept, int Kept, bool AuthorsBelow);

    static async Task<HistorySpan?> Seen(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, Guid id, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, tx, $"""
            SELECT m.revision,m.author_id,f.floor,
              coalesce((SELECT min(v.revision) FROM memory_versions v WHERE v.memory_id=m.id AND v.revision>=f.floor),m.revision),
              memory_versions_kept(),
              EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.revision<f.floor AND e.subject_id=m.author_id)
            {From} CROSS JOIN LATERAL (SELECT memory_history_floor(@principal,m.id) AS floor) f
            WHERE m.id=@id AND {Database.Visible("r")}
            """, ("principal", principal), ("id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetInt32(0), Text(r, 1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5)) : null;
    }

    // The memory's current state and its kept earlier states from the floor on, each marked held when it was held and no
    // later one of them holds the same text released, and veiled (its tags and project left out) unless a later one
    // released holds the same text, tags and project.
    const string States = """
        st AS (
          SELECT m.revision,true AS current,m.subject_id,m.scope,m.status,m.kind,m.importance,m.volatility,m.tags,m.project_id,
            m.content,m.hold,m.valid_from,m.valid_until,m.updated_at AS written_at,NULL::timestamptz AS retired_at
          FROM memories m WHERE m.id=@id
          UNION ALL
          SELECT v.revision,false,v.subject_id,v.scope,v.status,v.state->>'kind',(v.state->>'importance')::smallint,v.state->>'volatility',
            v.tags,(v.state->>'project_id')::uuid,v.content,v.state->>'hold',v.valid_from,v.valid_until,v.written_at,v.retired_at
          FROM memory_versions v WHERE v.memory_id=@id AND v.revision>=@floor),
        states AS (
          SELECT st.*,st.hold IS NOT NULL AND NOT EXISTS(SELECT 1 FROM st n WHERE n.revision>st.revision AND n.hold IS NULL AND n.content=st.content) AS held,
            st.hold IS NOT NULL AND NOT EXISTS(SELECT 1 FROM st n WHERE n.revision>st.revision AND n.hold IS NULL AND n.content=st.content
              AND n.tags=st.tags AND n.project_id IS NOT DISTINCT FROM st.project_id) AS veiled
          FROM st)
        """;

    static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
