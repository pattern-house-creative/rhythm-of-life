using System.Text.Json;

namespace Knowledge;

// As-of recall (SB-P4-07; story 6; docs/dev/memory.md section 5): what was true at a past moment T, with every correction
// made since applied, in the words of that time. Only the recall tool asks it (its as_of argument, made only with
// memory.tools on, off by default); without as_of combined retrieval runs exactly as before.
//
// A native memory is true at T when it is not marked wrong now and its window, as it stands now (migration 049: a
// supersede, an end, a moved start, a reopening by the retraction cascade), covers T: its start (its stated one, else
// when it was written) at or before T, and no end, or one after T. Its words are those of its state at T: of its current
// state and the earlier states it keeps (memory_versions, the newest 20, 034), the one written by then and not yet
// replaced; for a memory written after T whose stated start is earlier, its first state, marked recorded after. That
// state must be one this reader is served: at or above the revision its history starts at for this reader (034's
// memory_history_floor, the cut history and get(version) keep), so a memory in a scope the reader did not read then is
// left out, and not held (a held state's words never reach a reader, D-160), so a memory held then is left out too. A
// memory whose state at T is no longer kept is left out: its words, scope and hold then cannot be checked.
//
// A belief is a memory marked wrong now (deprecated) that was believed at T: written by then, its window covering T, and
// its state at T served to this reader and not marked wrong then, so it was retracted after T. Beliefs are never among
// the results: at most BeliefsMax are listed under them (Retracted), newest retraction first, each with the result it
// had replaced (a supersedes made link from it, which the cascade reopened), found by the arms or through that link.
//
// Any other record (the catalog) keeps no earlier words, so it answers only when it has not changed since T (its
// source's time, else when Rhythm indexed it, at or before T): its words now are its words then.
//
// Every read keeps the read predicate on the memory's own row (Recall's filters, Database.Visible) and NotHeld: a memory
// held now, in the trash, or of a scope the reader does not read now is never in either list. Adapted from Second Brain's
// as-of recall (src/recall/as-of.ts at the pinned commit, MIT; NOTICE): the predicate's two branches, the words at T from
// the kept versions, beliefs under the true results, at most three, newest retraction first, found through supersedes
// links too.
public static class RecallAsOf
{
    public const int BeliefsMax = 3;

    // The memory alias m's state at @asof, or for one written after it, its first: of its current state and its kept
    // states, the one written by then and not replaced by then. One row: revision, content, status, hold, written_at and
    // retired_at (none for the current state). For CROSS JOIN LATERAL.
    public static string State(string m) => $"""
        (SELECT x.* FROM (
            SELECT {m}.revision,{m}.content,{m}.status,{m}.hold,{m}.updated_at AS written_at,NULL::timestamptz AS retired_at
            UNION ALL
            SELECT v.revision,v.content,v.status,v.state->>'hold',v.written_at,v.retired_at FROM memory_versions v WHERE v.memory_id={m}.id) x
          WHERE x.written_at<=greatest(@asof,{m}.created_at) AND (x.retired_at IS NULL OR x.retired_at>greatest(@asof,{m}.created_at))
          ORDER BY x.revision DESC LIMIT 1)
        """;

    // Whether memory m's window, as it stands now, covers @asof.
    static string Covers(string m) => $"(coalesce({m}.valid_from,{m}.created_at)<=@asof AND ({m}.valid_until IS NULL OR {m}.valid_until>@asof))";

    // Whether memory m's state s at @asof is served to @principal: not held then, and at or above the revision its history
    // starts at for this reader.
    static string Served(string m, string s) => $"({s}.hold IS NULL AND {s}.revision>=memory_history_floor(@principal,{m}.id))";

    // A belief: marked wrong now, written by @asof, and not marked wrong in its state then.
    static string Belief(string m, string s) => $"({m}.status='deprecated' AND {m}.created_at<=@asof AND {s}.status<>'deprecated')";

    // The as-of condition on a resources row r, in place of Recall's current one: a memory row (its own, or its map star,
    // which stands for it) true at @asof, or with `beliefs` a belief; any other row unchanged since @asof.
    public static string Filter(bool beliefs) => $"""
        (CASE WHEN r.provider='{Relationships.MemoryStars.Provider}' THEN EXISTS(SELECT 1 FROM memories m CROSS JOIN LATERAL {State("m")} s
            WHERE m.id={Relationships.MemoryStars.Answering("r")} AND {Covers("m")} AND {Served("m", "s")}
              AND (m.status<>'deprecated'{(beliefs ? $" OR {Belief("m", "s")}" : "")}))
          ELSE coalesce(r.source_updated_at,r.indexed_at)<=@asof END)
        """;

    // The start of a native memory's words at @asof (Recall's excerpt), or its length: its state then, never a held one's.
    public static string Head(int chars) =>
        $"(SELECT left(s.content,{chars}) FROM memories m CROSS JOIN LATERAL {State("m")} s WHERE m.id=r.id AND m.hold IS NULL AND s.hold IS NULL)";
    public static string Length =>
        $"coalesce((SELECT length(s.content) FROM memories m CROSS JOIN LATERAL {State("m")} s WHERE m.id=r.id AND m.hold IS NULL AND s.hold IS NULL),0)";

    // What each of these native memories was at @asof, for this reader: the state's revision, status and times, whether it
    // was written after @asof, and for one marked wrong now (a belief) when it was retracted: the newest replacement of a
    // state not marked wrong. Only memories the reader reads now (the read predicate on their own rows).
    public static async Task<IReadOnlyDictionary<Guid, RecallThen>> Resolve(Database db, Guid principal, DateTimeOffset asOf, IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, RecallThen>();
        using var json = JsonDocument.Parse(await db.Read(principal, $"""
            SELECT m.id,s.revision,s.status,s.written_at,s.retired_at,(m.created_at>@asof) AS later,(m.status='deprecated') AS retracted,
              (SELECT max(v.retired_at) FROM memory_versions v WHERE v.memory_id=m.id AND v.status<>'deprecated') AS retracted_at
            FROM resources r JOIN memories m ON m.id=r.id CROSS JOIN LATERAL {State("m")} s
            WHERE r.id=ANY(@ids) AND r.provider='{Relationships.MemoryStars.Provider}' AND r.relationship_snapshot IS NULL AND {Database.Visible()}
            """, ct, ("ids", ids.ToArray()), ("asof", asOf.ToUniversalTime())));
        var then = new Dictionary<Guid, RecallThen>();
        foreach (var r in json.RootElement.EnumerateArray())
        {
            static DateTimeOffset? At(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDateTimeOffset();
            then[r.GetProperty("id").GetGuid()] = new(r.GetProperty("revision").GetInt32(), r.GetProperty("status").GetString()!,
                At(r.GetProperty("written_at"))!.Value, At(r.GetProperty("retired_at")), r.GetProperty("later").GetBoolean(),
                r.GetProperty("retracted").GetBoolean() ? At(r.GetProperty("retracted_at")) ?? asOf : null);
        }
        return then;
    }

    // The beliefs that had replaced one of these true results (an active supersedes made link from a memory marked wrong
    // now onto it), as the filter admits them for this reader at @asof, with the result each had replaced; whether or not
    // an arm found them.
    public static async Task<IReadOnlyDictionary<Guid, Guid>> Replacing(Database db, Guid principal, IReadOnlyCollection<Guid> results, string filter,
        (string, object?)[] args, CancellationToken ct)
    {
        if (results.Count == 0) return new Dictionary<Guid, Guid>();
        using var json = JsonDocument.Parse(await db.Read(principal, $"""
            SELECT DISTINCT ON (r.id) r.id,l.target_id
            FROM made_links l JOIN resources r ON r.id=l.source_id
            WHERE l.type='supersedes' AND l.lifecycle='active' AND l.target_id=ANY(@as_of_results)
              AND r.provider='{Relationships.MemoryStars.Provider}' AND r.relationship_snapshot IS NULL
              AND EXISTS(SELECT 1 FROM memories b WHERE b.id=r.id AND b.status='deprecated')
              AND {Database.Visible()} AND {filter}
            ORDER BY r.id,l.target_id
            LIMIT 100
            """, ct, [("as_of_results", results.ToArray()), .. args]));
        return json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("target_id").GetGuid());
    }
}

// What a native memory was at the moment asked (as-of recall): the revision of the state its words are taken from, that
// state's status, when it was written and when a later change replaced it (none: those are its words now), whether the
// memory was written after that moment (true from its stated start), and for one marked wrong now, when it was retracted.
public sealed record RecallThen(int Revision, string Status, DateTimeOffset WrittenAt, DateTimeOffset? ChangedAt, bool RecordedAfter,
    DateTimeOffset? RetractedAt = null);

// A belief listed under the true results of an as-of recall: the memory's row as a result's, its words then (Excerpt,
// within the output budget), when it was retracted, the result it had replaced (current again since) and its state then.
public sealed record RecallBelief(Guid Id, JsonElement Row, string Excerpt, bool Truncated, int FullLength, DateTimeOffset RetractedAt,
    Guid? InsteadOf, RecallThen Then);
