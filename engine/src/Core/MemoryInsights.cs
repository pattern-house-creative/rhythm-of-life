using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The insight candidate queue (SB-P5-05; migration 064; InsightCandidates finds the pairs): what a reader is served of the
// pairs the nightly pass found, and a reader's use or rejection of one. A reader is served the candidates whose two
// memories it reads (the one read predicate on both memories' own rows), listed by one predicate (064's
// memory_insight_listed: waiting, both memories still eligible and of the candidate's scope), so a count is the length
// of its list; a held, trashed, deprecated, ended or moved memory takes its pairs out of every list at once, and a pair
// is never served across scopes.
//
// Only the memory tools call it (list_insight_candidates and resolve_insight_candidate), and only with memory.tools on
// (SB-P3-09); off, nothing is listed, answered or written.

// One side of a candidate: the memory's id, revision, when it was written, its importance and a preview of its words.
public sealed record MemoryInsightSide(Guid Id, int Revision, DateTimeOffset CreatedAt, int? Importance, string Content, bool Truncated);

// A candidate as a reader is served it. Id and FoundAt are null in a dry run, which writes nothing. Subject: the scope's
// owner (a person's id, or household for Family); Scope private or shared.
// Since SB-P5-06 (migration 066) what became of it: Status (pending, used, rejected), when it was decided, why it was
// declined and the insight written from it, when this reader reads that insight's scope.
public sealed record MemoryInsightCandidate(Guid? Id, string Subject, string Scope, double Score, IReadOnlyList<string> Signals, double Similarity,
    string Model, int GapDays, DateTimeOffset? FoundAt, MemoryInsightSide Older, MemoryInsightSide Newer, string? Status = null, DateTimeOffset? DecidedAt = null,
    string? Note = null, Guid? InsightId = null);

public sealed record MemoryInsightCursor(double Score, Guid Id);

// The list's length for this reader and one page of it, best first, read in one statement: Total is the length of the
// whole list the pages walk.
public sealed record MemoryInsightPage(int Total, IReadOnlyList<MemoryInsightCandidate> Items, MemoryInsightCursor? Next);

// A use or rejection: the candidate, its status now (used, rejected), when it was decided, and whether this call changed
// nothing (the same decision again). Since SB-P5-06: why it was declined and the insight written from it, as kept.
public sealed record MemoryInsightResolved(Guid Id, string Status, DateTimeOffset DecidedAt, bool Unchanged, string? Note = null, Guid? InsightId = null);

public sealed partial class MemoryStore
{
    public const int InsightsMax = 50;

    const string InsightFrom = """
        FROM memory_insight_candidates c
        JOIN memories o ON o.id=c.older_id JOIN resources ro ON ro.id=o.id AND ro.provider='memory' AND ro.relationship_snapshot IS NULL AND ro.external_id=o.id::text
        JOIN memories n ON n.id=c.newer_id JOIN resources rn ON rn.id=n.id AND rn.provider='memory' AND rn.relationship_snapshot IS NULL AND rn.external_id=n.id::text
        """;

    // A candidate's state as a reader asks for one (SB-P5-06): waiting (the default, the list a client decides from), or
    // what was decided: used, or rejected (what the daily insight declined). Any of them only while its pair is live.
    public const string InsightWaiting = "waiting";
    public static readonly string[] InsightStates = [InsightWaiting, "used", "rejected"];

    // The list a reader is served: candidates of a state whose two memories it reads, optionally of one scope. One clause
    // for the count and the list, so the count is the length of the list.
    static (string Where, List<NpgsqlParameter> Args) InsightQueue(Guid principal, string? scope, string state = InsightWaiting)
    {
        if (!InsightStates.Contains(state)) throw new MemoryRefused("invalid", $"state is {string.Join(", ", InsightStates)}");
        var args = new List<NpgsqlParameter> { new("principal", NpgsqlDbType.Uuid) { Value = principal } };
        var where = (state == InsightWaiting ? "memory_insight_listed(c.id)" : "c.status=@state AND memory_insight_live(c.id)")
            + $" AND {Database.Visible("ro")} AND {Database.Visible("rn")}";
        if (state != InsightWaiting) args.Add(new("state", NpgsqlDbType.Text) { Value = state });
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        if (named is not null)
        {
            where += " AND o.scope=@scope";
            args.Add(new("scope", NpgsqlDbType.Text) { Value = named });
        }
        return (where, args);
    }

    // The list's length for this reader.
    public async Task<int> InsightCount(Guid principal, string? scope, CancellationToken ct, string state = InsightWaiting)
    {
        var (where, args) = InsightQueue(principal, scope, state);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) {InsightFrom} WHERE {where}", c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // One page of the list and its whole length, in one statement: the best first (score, then id), After continuing from
    // a page's Next.
    public async Task<MemoryInsightPage> Insights(Guid principal, string? scope, int limit, MemoryInsightCursor? after, int previewChars, CancellationToken ct,
        string state = InsightWaiting)
    {
        if (limit is < 1 or > InsightsMax) throw new MemoryRefused("invalid", $"n is 1 to {InsightsMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var (where, args) = InsightQueue(principal, scope, state);
        args.AddRange([
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterScore", NpgsqlDbType.Double) { Value = after is { } a ? a.Score : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value }]);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (SELECT c.id {InsightFrom} WHERE {where})
            SELECT t.total,p.* FROM (SELECT count(*) AS total FROM q) t
            LEFT JOIN LATERAL (
              SELECT c.id AS candidate,c.subject_id,o.scope,c.score,c.signals,c.similarity,c.model,c.gap_days,c.found_at,
                o.id AS older,o.revision,o.created_at,o.importance,left(o.content,@chars),length(o.content)>@chars,
                n.id AS newer,n.revision,n.created_at,n.importance,left(n.content,@chars),length(n.content)>@chars,
                c.status,c.decided_at,c.note,(SELECT i.id FROM insights i WHERE i.id=c.insight_id AND reads_subject(@principal,i.subject_id))
              FROM q JOIN memory_insight_candidates c ON c.id=q.id JOIN memories o ON o.id=c.older_id JOIN memories n ON n.id=c.newer_id
              WHERE @afterScore::double precision IS NULL OR c.score<@afterScore OR c.score=@afterScore AND c.id>@afterId
              ORDER BY c.score DESC,c.id LIMIT @limit) p ON true
            ORDER BY p.score DESC,p.candidate
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryInsightCandidate>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                total = (int)r.GetInt64(0);
                if (r.IsDBNull(1)) continue;
                MemoryInsightSide Side(int i) => new(r.GetGuid(i), r.GetInt32(i + 1), r.GetFieldValue<DateTimeOffset>(i + 2),
                    r.IsDBNull(i + 3) ? null : r.GetInt16(i + 3), r.GetString(i + 4), r.GetBoolean(i + 5));
                items.Add(new(r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetFieldValue<string[]>(5), r.GetDouble(6), r.GetString(7),
                    r.GetInt32(8), r.GetFieldValue<DateTimeOffset>(9), Side(10), Side(16), r.GetString(22),
                    r.IsDBNull(23) ? null : r.GetFieldValue<DateTimeOffset>(23), r.IsDBNull(24) ? null : r.GetString(24), r.IsDBNull(25) ? null : r.GetGuid(25)));
            }
        var next = items.Count > limit ? new MemoryInsightCursor(items[limit - 1].Score, items[limit - 1].Id!.Value) : null;
        return new(total, items.Take(limit).ToList(), next);
    }

    // A dry run for this reader, a sample of the pairs the next nightly pass would add in the scopes it reads (one, with a
    // scope named), from each scope's place, by the live model and the install's insight threshold, each memory compared
    // with fewer earlier ones than the pass compares (InsightCandidates.Preview); nothing is written. Best first, each
    // with its previews.
    public async Task<IReadOnlyList<MemoryInsightCandidate>> InsightPreview(Guid principal, string? scope, IEmbedder model, double threshold,
        int previewChars, CancellationToken ct)
    {
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        var subjects = new List<string>();
        await using (var cmd = source.CreateCommand("""
            SELECT DISTINCT m.subject_id FROM memories m
            WHERE reads_subject(@principal,m.subject_id) AND (@scope::text IS NULL OR m.scope=@scope) ORDER BY 1
            """))
        {
            cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
            cmd.Parameters.Add(new("scope", NpgsqlDbType.Text) { Value = (object?)named ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) subjects.Add(r.GetString(0));
        }
        if (subjects.Count == 0) return [];
        var found = await InsightCandidates.Preview(source, model, threshold, subjects, ct);
        // Cut as the list cuts (the database's left and length count characters, not UTF-16 units), never inside one.
        MemoryInsightSide Side(InsightMemory m) => new(m.Id, m.Revision, m.CreatedAt, m.Importance,
            string.Concat(m.Text.EnumerateRunes().Take(previewChars)), m.Length > previewChars);
        return [.. found.Select(f => new MemoryInsightCandidate(null, f.Subject, f.Subject == "household" ? "shared" : "private", f.Pair.Score,
            f.Pair.Signals, f.Pair.Similarity, model.Id, f.Pair.GapDays, null, Side(f.Pair.Older), Side(f.Pair.Newer)))];
    }

    // Use or reject a candidate this reader is served: use, an insight was written from it; reject, it says nothing worth
    // saying. The same decision again answers unchanged; the other one on a decided candidate is refused.
    public async Task<MemoryInsightResolved> ResolveInsight(Guid principal, Guid candidate, string action, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Deciding a candidate takes no idempotency key: the same decision again answers unchanged");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT resolve_insight_candidate(@principal,@id,@action,@channel,@client)::text", c);
        cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
        cmd.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = candidate });
        cmd.Parameters.Add(new("action", NpgsqlDbType.Text) { Value = action });
        cmd.Parameters.Add(new("channel", NpgsqlDbType.Text) { Value = call.Channel });
        cmd.Parameters.Add(new("client", NpgsqlDbType.Text) { Value = (object?)AdminAudit.Client(call.Client) ?? DBNull.Value });
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (e.SqlState == "P0002") { throw new MemoryRefused("unknown", e.MessageText); }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("status").GetString()!, o.GetProperty("decided_at").GetDateTimeOffset(),
            o.GetProperty("unchanged").GetBoolean());
    }
}
