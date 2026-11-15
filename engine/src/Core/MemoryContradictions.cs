using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Contradiction candidates (SB-P5-03; story 9's contradiction case; docs/dev/memory.md section 5; migration 068), adapted from
// Second Brain's contradiction check (src/capture/duplicate.ts at the pinned commit, MIT; NOTICE). The engine runs no model
// that decides (D-033) and no score tells a duplicate from a contradiction that changes one number or day (D-211), so a
// write's check (DuplicateCheck) names the memories of its scope close enough to conflict, at or above the contradiction
// threshold (EmbeddingThresholds, read through Override: today's 0.85, the duplicate flag's, unless tuned), and each is queued
// for a person from that moment. The calling AI reads them and may say which conflict (Propose), and an id it was not offered
// is refused (not_offered). Only a person settles one (Settle, through the app's or the terminal's channel): neither memory
// changes until then, an AI client can't end the earlier one, supersede the pair or move either (needs_person, 068's triggers), and
// a person keeping both of a flagged copy settles the pairs that review showed.
//
// The review is one predicate, 068's memory_contradiction_listed, over the rows the reader reads whose memories it may change
// (either one): its count is the length of its list. Memories are never named across scopes (a row joins one scope, and a
// move takes it off), and a held memory is never named.
//
// Who calls it: the memory tools list_contradictions and propose_contradiction, get and every write that saves words; the
// app's contradictions and settle_contradiction ops. Each exists only with memory.tools on, off by default.

// A memory that stands against another as a possible contradiction, how close, and whether somebody has named it one.
public sealed record MemoryCandidate(Guid Id, double Score, bool Proposed);

// One side of a row in the review: a preview of its words of at most the asked characters (Truncated when longer), and when it began
// being true (valid_from, else when it was written; none when its start is unknown, which counts as before every other), which says
// which of the two may replace the other: only the later (049's rule).
public sealed record MemoryContradictionSide(Guid Id, int Revision, string? Author, string Status, string Content, bool Truncated, DateTimeOffset? StartedAt);

// One row of the review: the memory whose words were checked and the one they were close to, one scope. ProposedAt, ProposedBy
// (the person the login speaks for, or none), ProposedClient and Reason when an AI client has named it a contradiction. MaySettle:
// whether this caller may settle it through the channel it named (never an AI client's).
public sealed record MemoryContradiction(Guid Row, string Subject, string Scope, double Score, DateTimeOffset OfferedAt, MemoryContradictionSide Memory,
    MemoryContradictionSide Conflict, DateTimeOffset? ProposedAt, string? ProposedBy, string? ProposedClient, string? Reason, bool MaySettle);

// The review's length for this reader and one page of it, newest first, read in one statement: Total is the length of the
// whole list the pages walk.
public sealed record MemoryContradictionPage(int Total, IReadOnlyList<MemoryContradiction> Items, MemoryCursor? Next);

// A proposal: the memory as a write answers it, the other memory, how close, and the reason kept (the first one's, when asked
// again: Write.Unchanged).
public sealed record MemoryProposed(MemoryWrite Write, Guid Conflict, double Score, string? Reason);

// A person's decision: the memory named as a write answers it, the other memory, and keep_both or replaced.
public sealed record MemorySettled(MemoryWrite Write, Guid Conflict, string Decision);

public static class MemoryContradictions
{
    public const string KeepBoth = "keep_both", Replace = "replace", Replaced = "replaced";
    public const int ReasonMax = 200;

    // 049's words for a replacement by a memory that did not begin later (063's supersede_memory), and what a person is told instead.
    public const string NotLater = "began being true at or before";
    public const string NotLaterToAPerson = "This memory began being true at or before the other did, so it can't replace it: only the one that began later replaces the other. "
        + "If this one stands, mark the other wrong or forget it. If the other stands, it can replace this one. Or both stand.";

    // A reason as it is kept: what a person sees (no control or formatting characters, white space folded), at most ReasonMax
    // characters, never cut inside a pair; none when nothing is left.
    public static string? Reason(string? text)
    {
        if (text is null) return null;
        var b = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var kind = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) { if (b.Length > 0 && b[^1] != ' ') b.Append(' '); continue; }
            if (kind is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            if (b.Length + rune.Utf16SequenceLength > ReasonMax) break;
            b.Append(rune.ToString());
        }
        var kept = b.ToString().Trim();
        return kept.Length == 0 ? null : kept;
    }
}

public sealed partial class MemoryStore
{
    public const int ContradictionsMax = 50;

    // What stands against a memory, as its reader is served it (none for one it does not read).
    public async Task<IReadOnlyList<MemoryCandidate>> CandidatesOf(Guid principal, Guid memory, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"SELECT {CandidatesColumn} {From} WHERE m.id=@id AND {Database.Visible("r")}",
            ("principal", principal), ("id", memory));
        return Candidates(await cmd.ExecuteScalarAsync(ct) as string);
    }

    // A memory's standing candidates as one JSON array, or none while it takes no part in the review.
    const string CandidatesColumn = "CASE WHEN memory_duplicate_live(m.id) THEN memory_open_contradictions(m.id) END::text";

    static IReadOnlyList<MemoryCandidate> Candidates(string? json)
    {
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(x => new MemoryCandidate(x.GetProperty("id").GetGuid(), x.GetProperty("score").GetDouble(),
            x.GetProperty("proposed").GetBoolean()))];
    }

    // One page of the review a reader acts on and its whole length, in one statement: rows it reads (the one read predicate
    // on the memory's own row; the other is of one scope with it) that 068's memory_contradiction_listed lists, whose
    // memories it may change (either one), optionally of one scope. Newest first, After continuing from a page's Next.
    // call: the channel it would settle through, which MaySettle is for.
    public async Task<MemoryContradictionPage> Contradictions(Guid principal, string? scope, int limit, MemoryCursor? after, int previewChars, MemoryCall call, CancellationToken ct)
    {
        if (limit is < 1 or > ContradictionsMax) throw new MemoryRefused("invalid", $"n is 1 to {ContradictionsMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (
              SELECT k.id,k.offered_at,k.memory_id,k.conflict_id FROM memory_contradictions k
              JOIN memories m ON m.id=k.memory_id
              JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
              JOIN memories o ON o.id=k.conflict_id
              WHERE {Database.Visible("r")} AND memory_contradiction_listed(k)
                AND (memory_may_change(@principal,m.subject_id,m.author_id,NOT coalesce({Database.ScopesOff},false))
                  OR memory_may_change(@principal,o.subject_id,o.author_id,NOT coalesce({Database.ScopesOff},false)))
                AND (@scope::text IS NULL OR m.scope=@scope))
            SELECT t.total,p.* FROM (SELECT count(*) AS total FROM q) t
            LEFT JOIN LATERAL (
              SELECT k.id,m.subject_id,m.scope,k.score::float8 AS score,k.offered_at,
                m.id AS m_id,m.revision AS m_revision,m.author_id AS m_author,m.status AS m_status,left(m.content,@chars) AS m_content,length(m.content)>@chars AS m_cut,
                o.id AS o_id,o.revision AS o_revision,o.author_id AS o_author,o.status AS o_status,left(o.content,@chars) AS o_content,length(o.content)>@chars AS o_cut,
                k.proposed_at,(SELECT b.person_id FROM principals b WHERE b.id=k.proposed_by) AS proposed_person,k.proposed_client,k.reason,
                (memory_may_change(@principal,m.subject_id,m.author_id,NOT coalesce({Database.ScopesOff},false))
                  OR memory_may_change(@principal,o.subject_id,o.author_id,NOT coalesce({Database.ScopesOff},false)))
                  AND EXISTS(SELECT 1 FROM principals w WHERE w.id=@principal
                    AND (w.role='app' AND @channel='app' OR w.role='harness' AND @channel='cli' AND w.terminal AND w.person_id IS NOT NULL)) AS may_settle,
                CASE WHEN isfinite(coalesce(m.valid_from,m.created_at)) THEN coalesce(m.valid_from,m.created_at) END AS m_started,
                CASE WHEN isfinite(coalesce(o.valid_from,o.created_at)) THEN coalesce(o.valid_from,o.created_at) END AS o_started
              FROM q JOIN memory_contradictions k ON k.id=q.id JOIN memories m ON m.id=q.memory_id JOIN memories o ON o.id=q.conflict_id
              WHERE @afterAt::timestamptz IS NULL OR (q.offered_at,q.id)<(@afterAt,@afterId)
              ORDER BY q.offered_at DESC,q.id DESC LIMIT @limit) p ON true
            ORDER BY p.offered_at DESC,p.id DESC
            """, c);
        cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
        cmd.Parameters.Add(new("scope", NpgsqlDbType.Text) { Value = (object?)named ?? DBNull.Value });
        cmd.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = limit + 1 });
        cmd.Parameters.Add(new("chars", NpgsqlDbType.Integer) { Value = previewChars });
        cmd.Parameters.Add(new("channel", NpgsqlDbType.Text) { Value = call.Channel });
        cmd.Parameters.Add(new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.CreatedAt.ToUniversalTime() : DBNull.Value });
        cmd.Parameters.Add(new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value });
        var items = new List<MemoryContradiction>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                total = (int)r.GetInt64(0);
                if (r.IsDBNull(1)) continue;
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new(r.GetGuid(1), r.GetString(2), r.GetString(3), Math.Round(r.GetDouble(4), 4), r.GetFieldValue<DateTimeOffset>(5),
                    new(r.GetGuid(6), r.GetInt32(7), Text(8), r.GetString(9), r.GetString(10), r.GetBoolean(11), r.IsDBNull(23) ? null : r.GetFieldValue<DateTimeOffset>(23)),
                    new(r.GetGuid(12), r.GetInt32(13), Text(14), r.GetString(15), r.GetString(16), r.GetBoolean(17), r.IsDBNull(24) ? null : r.GetFieldValue<DateTimeOffset>(24)),
                    r.IsDBNull(18) ? null : r.GetFieldValue<DateTimeOffset>(18), Text(19), Text(20), Text(21), r.GetBoolean(22)));
            }
        var next = items.Count > limit ? new MemoryCursor(items[limit - 1].OfferedAt, items[limit - 1].Row) : null;
        return new(total, items.Take(limit).ToList(), next);
    }

    // The calling AI says a memory it was offered a candidate for conflicts with that candidate. An id it was not offered is
    // refused (not_offered), whatever it names; a pair a person settled is refused (settled). Asking again answers unchanged.
    // It settles nothing: the pair stays queued for a person, in the contradiction review from now on. No idempotency key.
    public async Task<MemoryProposed> Propose(Guid principal, Guid memory, Guid conflict, string? reason, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A proposal takes no idempotency key: asking again answers unchanged");
        var (write, answer) = await WriteAnswer("SELECT propose_memory_contradiction(@principal,@id,@conflict,@reason,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("conflict", NpgsqlDbType.Uuid, conflict), ("reason", NpgsqlDbType.Text, MemoryContradictions.Reason(reason)));
        return new(write, answer.GetProperty("conflict").GetGuid(), answer.GetProperty("score").GetDouble(),
            answer.TryGetProperty("reason", out var kept) && kept.ValueKind == JsonValueKind.String ? kept.GetString() : null);
    }

    // A person decides, through the app's or the terminal's channel (never an AI client's: needs_person): both stand, or
    // `memory` replaces `conflict` (049's supersede, planned and applied at once, its rules as they are there, and refused
    // (protected) when an end still to come means it would change nothing). Each expected revision is the one the person read,
    // refused (conflict) when it moved on. Settled already answers unchanged. No
    // idempotency key. Only a person reaches a replacement here, and 049's refusal of a memory that did not begin later speaks to
    // an AI client, of a field neither the app nor the terminal sets: a person is told what they can do (protected, as the
    // other replacement a settling rules out is).
    public async Task<MemorySettled> Settle(Guid principal, Guid memory, Guid conflict, string decision, int? expected, int? conflictExpected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Settling takes no idempotency key: asking again answers unchanged");
        MemoryWrite write;
        JsonElement answer;
        try
        {
            (write, answer) = await WriteAnswer("SELECT settle_memory_contradiction(@principal,@id,@conflict,@decision,@expected,@other,@channel,@client)::text", principal, call, [], ct,
                ("id", NpgsqlDbType.Uuid, memory), ("conflict", NpgsqlDbType.Uuid, conflict), ("decision", NpgsqlDbType.Text, decision),
                ("expected", NpgsqlDbType.Integer, expected), ("other", NpgsqlDbType.Integer, conflictExpected));
        }
        catch (MemoryRefused e) when (e.Code == "invalid" && e.Message.Contains(MemoryContradictions.NotLater, StringComparison.Ordinal))
        {
            throw new MemoryRefused("protected", MemoryContradictions.NotLaterToAPerson);
        }
        var settled = answer.GetProperty("decision").GetString()!;
        if (settled == MemoryContradictions.KeepBoth) write = await Redrawn(principal, write, call, ct);
        return new(write, answer.GetProperty("conflict").GetGuid(), settled);
    }
}
