using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Open loops and two-way commitments for native memories (SB-P5-09; story 12; docs/dev/memory.md sections 5 and 9;
// migration 058): what a person said they would do (out: you owe) and what someone else promised them (in: owed to
// you). An open loop is a memory tagged task and not task:done, live, not held, not marked wrong, its window open
// (058's memory_open_loop, the one definition); a commitment's direction and counterparty are the engine's, written once
// with the memory (remember's owed_to and owed_by), and a task-tagged memory without one is a loop the person owes. The
// queue a reader is served is the open loops it reads, may change and owns (058's memory_loop_mine: an admin is not
// served another member's), in one clause that the count and the list read together, so each count is the length of
// its list; and a page says the lengths of both directions' lists from the same statement. Resolve closes one, done,
// received (something owed to the person arrived) or not_a_task, as a change of its tags: a version kept, one audit row
// (resolved), undone by undo; a repeat answers unchanged, so a loop leaves the queue once.
//
// The memory tools (list_loops, resolve_loop and remember's owed_by and owed_to; SB-P3-09) and the app's app_memories (loops,
// resolve_loop; SB-P6-01) call it, with memory.tools on, off by default. With it off nothing is listed, answered or written.

// One open loop as its reader is served it. Direction: out (the person owes it) or in (owed to the person); Counterparty:
// the other person's name as its slug, or none. Content: a preview of at most the asked characters (Truncated when
// longer); a held memory is never in the queue, so every item has its words and tags.
public sealed record MemoryLoop(Guid Id, int Revision, string Subject, string Scope, string? Author, string Status, string Direction,
    string? Counterparty, IReadOnlyList<string> Tags, MemoryProjectRef? Project, DateTimeOffset CreatedAt, string Content, bool Truncated);

public sealed record MemoryLoopCursor(DateTimeOffset CreatedAt, Guid Id);

// The queue's length for this reader in the direction asked (Total; every open loop when none is asked), the lengths of
// its two directions' lists (YouOwe, OwedToYou) under the same filters, and one page of the asked list, newest first:
// all read in one statement.
public sealed record MemoryLoopPage(int Total, int YouOwe, int OwedToYou, IReadOnlyList<MemoryLoop> Items, MemoryLoopCursor? Next);

// What a resolve answers: the memory as a write answers it, the action, the loop's direction and counterparty, the fields
// it changed (tags), or Unchanged when it was resolved already (or, for not_a_task, never a task): nothing was written.
public sealed record MemoryLoopResolved(MemoryWrite Write, string Action, string Direction, string? Counterparty);

// What remember with owed_by or owed_to answers: the write, and the commitment it recorded.
public sealed record MemoryCommitted(MemoryWrite Write, string Direction, string? Counterparty);

public static class MemoryLoops
{
    public const string Out = "out", In = "in";
    public const string Done = "done", Received = "received", NotATask = "not_a_task";
    public static readonly IReadOnlyList<string> Actions = [Done, Received, NotATask];
    // The longest name of the other person a commitment takes (upstream's COUNTERPARTY_NAME_MAX_CHARS).
    public const int NameMax = 64;

    // A direction a list names: out, in, or all (none: every open loop).
    public static string? Direction(string? named) => (named ?? "all").Trim().ToLowerInvariant() switch
    {
        Out => Out,
        In => In,
        "all" or "" => null,
        _ => throw new MemoryRefused("invalid", "direction is out (you owe), in (owed to you) or all"),
    };

    // A counterparty's slug in words, as upstream shows it: "sam-pellow" is Sam Pellow.
    public static string? Name(string? slug) => slug is null ? null : string.Join(' ', slug.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

public sealed partial class MemoryStore
{
    public const int LoopsMax = 50;

    // The queue a reader is served: memories it reads (the one read predicate on each one's own row) that are open loops
    // (058's memory_open_loop; the task tag first, which the tags index answers), of those it owns and may change (058's
    // memory_loop_mine), optionally of one scope. One clause for every count and list, so a count is its list's length.
    static (string Where, List<NpgsqlParameter> Args) LoopQueue(Guid principal, string? scope)
    {
        var args = new List<NpgsqlParameter> { new("principal", NpgsqlDbType.Uuid) { Value = principal } };
        var where = $"""
            {Database.Visible("r")} AND m.tags @> ARRAY['task']::text[] AND memory_open_loop(m.id) IS NOT NULL
              AND memory_loop_mine(@principal,m.subject_id,m.author_id,scopes_on())
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

    // The length of a reader's list in one direction (out, in, or null: every open loop).
    public async Task<int> LoopCount(Guid principal, string? scope, string? direction, CancellationToken ct)
    {
        var (where, args) = LoopQueue(principal, scope);
        args.Add(new("direction", NpgsqlDbType.Text) { Value = (object?)direction ?? DBNull.Value });
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT count(*) {From} WHERE {where} AND (@direction::text IS NULL OR memory_open_loop(m.id)=@direction)
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // One page of the list in one direction (null: every open loop), newest first, with its whole length and both
    // directions' lengths, in one statement (one snapshot). After continues from a page's Next.
    public async Task<MemoryLoopPage> Loops(Guid principal, string? scope, string? direction, int limit, MemoryLoopCursor? after, int previewChars,
        CancellationToken ct)
    {
        if (limit is < 1 or > LoopsMax) throw new MemoryRefused("invalid", $"n is 1 to {LoopsMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        if (direction is not (null or MemoryLoops.Out or MemoryLoops.In)) throw new MemoryRefused("invalid", "direction is out, in or none");
        var (where, args) = LoopQueue(principal, scope);
        args.AddRange([
            new("direction", NpgsqlDbType.Text) { Value = (object?)direction ?? DBNull.Value },
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.CreatedAt.ToUniversalTime() : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value }]);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (
              SELECT m.id,memory_open_loop(m.id) AS direction,m.created_at {From} WHERE {where})
            SELECT t.total,t.owe,t.owed,x.* FROM (
              SELECT count(*) FILTER (WHERE @direction::text IS NULL OR q.direction=@direction) AS total,
                count(*) FILTER (WHERE q.direction='out') AS owe,count(*) FILTER (WHERE q.direction='in') AS owed FROM q) t
            LEFT JOIN LATERAL (
              SELECT m.id,m.revision,m.subject_id,m.scope,m.author_id,m.status,q.direction,k.counterparty,m.tags,pr.id AS project,pr.slug,
                m.created_at,left(m.content,@chars),length(m.content)>@chars
              FROM q JOIN memories m ON m.id=q.id LEFT JOIN memory_commitments k ON k.memory_id=m.id
              LEFT JOIN memory_projects pr ON pr.id=m.project_id
              WHERE (@direction::text IS NULL OR q.direction=@direction)
                AND (@afterAt::timestamptz IS NULL OR (q.created_at,q.id)<(@afterAt,@afterId))
              ORDER BY q.created_at DESC,q.id DESC LIMIT @limit) x ON true
            ORDER BY x.created_at DESC,x.id DESC
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryLoop>();
        int total = 0, owe = 0, owed = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                (total, owe, owed) = ((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2));
                if (r.IsDBNull(3)) continue;
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new(r.GetGuid(3), r.GetInt32(4), r.GetString(5), r.GetString(6), Text(7), r.GetString(8), r.GetString(9), Text(10),
                    r.GetFieldValue<string[]>(11), r.IsDBNull(12) ? null : new(r.GetGuid(12), r.GetString(13)), r.GetFieldValue<DateTimeOffset>(14),
                    r.GetString(15), r.GetBoolean(16)));
            }
        var next = items.Count > limit ? new MemoryLoopCursor(items[limit - 1].CreatedAt, items[limit - 1].Id) : null;
        return new(total, owe, owed, items.Take(limit).ToList(), next);
    }

    // A new memory that is a commitment: direction out (the person owes `counterparty`) or in (`counterparty` owes the
    // person), tagged task (added when the draft leaves it out), written as Create writes a memory with its commitment
    // in the same transaction (058's remember_memory_commitment). The idempotency key covers the commitment too.
    public async Task<MemoryCommitted> Commit(Guid principal, MemoryDraft draft, string direction, string counterparty, MemoryCall call, CancellationToken ct)
    {
        if (direction is not (MemoryLoops.Out or MemoryLoops.In)) throw new MemoryRefused("invalid", "A commitment is owed by someone (in) or to someone (out)");
        var name = (counterparty ?? "").Trim();
        if (name.Length is 0 or > MemoryLoops.NameMax || name.Any(char.IsControl))
            throw new MemoryRefused("invalid", $"The other person's name is 1 to {MemoryLoops.NameMax} characters");
        var (memory, dropped) = Drafted(draft);
        var (write, answer) = await WriteAnswer(
            "SELECT remember_memory_commitment(@principal,@memory::jsonb,@direction,@counterparty,@channel,@client,@key)::text", principal, call, dropped, ct,
            ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString()), ("direction", NpgsqlDbType.Text, direction), ("counterparty", NpgsqlDbType.Text, name));
        var made = answer.GetProperty("commitment");
        return new(await Linked(principal, write, true, call, ct), made.GetProperty("direction").GetString()!,
            made.TryGetProperty("counterparty", out var who) && who.ValueKind == JsonValueKind.String ? who.GetString() : null);
    }

    // Closes an open loop: done, received (an owed-to-you loop only) or not_a_task, by whoever may change the memory, as a
    // change of its tags (058's resolve_memory_loop). Expected: the revision the caller read, refused (conflict) when it
    // moved on. No idempotency key: a repeat answers unchanged.
    public async Task<MemoryLoopResolved> Resolve(Guid principal, Guid memory, string action, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A resolve takes no idempotency key: resolving again answers unchanged");
        var named = (action ?? "").Trim().ToLowerInvariant();
        if (!MemoryLoops.Actions.Contains(named)) throw new MemoryRefused("invalid", "action is done, received or not_a_task");
        var (write, answer) = await WriteAnswer("SELECT resolve_memory_loop(@principal,@id,@action,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("action", NpgsqlDbType.Text, named), ("expected", NpgsqlDbType.Integer, expected));
        return LoopFrom(write, answer);
    }

    static MemoryLoopResolved LoopFrom(MemoryWrite write, JsonElement answer) =>
        new(write, answer.GetProperty("action").GetString()!, answer.GetProperty("direction").GetString()!,
            answer.TryGetProperty("counterparty", out var who) && who.ValueKind == JsonValueKind.String ? who.GetString() : null);
}
