using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Due dates for native memories (SB-P5-10; story 12; docs/dev/memory.md sections 5 and 9; migration 061): when a memory
// should come back to the person. remember takes an explicit when (and its kind), or, with none, reads the memory's words
// for the one date they name (DueDates.cs: an ambiguous one is never guessed, and the answer says why); either is anchored
// in the install's time zone (D-043) and written with the memory in one transaction (061's remember_memory_due). The due
// list is one predicate (061's memory_due: a due date, not task:done, live, not held, not marked wrong, its window open)
// that every count and list reads, since 069 never a decision, whose one date is its review date (D-214), of the memories
// a reader reads, may change and owns (058's memory_loop_mine, as the
// open loops), so each count is the length of its list; a page says the lengths of its three windows (overdue,
// upcoming within 48 hours, later) from the same statement. snooze moves a due date to a moment to come and clear_date
// takes it off, each a change of the memory: a version kept, one audit row (due_changed), undone by undo; a repeat
// answers unchanged.
//
// The memory tools (list_due, resolve_due and remember's when and when_kind; SB-P3-09) and the app's app_memories (due,
// resolve_due; SB-P6-01) call it, with memory.tools on, off by default. With it off nothing is read for a date, listed,
// answered or written.

// A due date as a reader is served it: the moment (At), how it reads on the install's clocks (Local) and that zone (Zone),
// its kind (due, event or wake) and where it came from (explicit: the caller gave it; text: read from the words).
public sealed record MemoryDue(DateTimeOffset At, string Local, string Zone, string Kind, string Source)
{
    // 061's memory_due_json, or none.
    public static MemoryDue? From(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null
        : new(DateTimeOffset.Parse(e.GetProperty("at").GetString()!, CultureInfo.InvariantCulture), e.GetProperty("local").GetString()!,
            e.GetProperty("zone").GetString()!, e.GetProperty("kind").GetString()!, e.GetProperty("source").GetString()!);

    public static MemoryDue? From(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return From(doc.RootElement);
    }
}

// One memory on a reader's due list. Window: overdue (its moment has come), upcoming (within 48 hours) or later. Loop: the
// open loop's direction when it is one (058: out, the person owes it; in, owed to them), else none. Content: a preview of
// at most the asked characters (Truncated when longer); a held memory is never on the list, so every item has its words.
public sealed record MemoryDueItem(Guid Id, int Revision, string Subject, string Scope, string? Author, string Status, MemoryDue Due,
    string Window, string? Loop, IReadOnlyList<string> Tags, MemoryProjectRef? Project, DateTimeOffset CreatedAt, string Content, bool Truncated);

public sealed record MemoryDueCursor(DateTimeOffset At, Guid Id);

// The asked window's length for this reader (Total; the whole list when none is asked), the lengths of the three windows
// under the same filters (Overdue, Upcoming, Later), the install's zone, and one page of the asked list, soonest first:
// all read in one statement.
public sealed record MemoryDuePage(int Total, int Overdue, int Upcoming, int Later, string Zone, IReadOnlyList<MemoryDueItem> Items,
    MemoryDueCursor? Next);

// What remember wrote: the write; the due date it took (none when it took none); when it read the words and took no date
// they name, why (NotTaken: its Reason and Why, the Why without the words for a memory held for review); and the
// commitment it recorded, if any (058).
public sealed record MemoryRemembered(MemoryWrite Write, MemoryDue? Due, DueDates.Reading? NotTaken, string? Direction, string? Counterparty);

// What a snooze or a clear answers: the write, the action, the due date after it (none after a clear) and before it.
// Write.Unchanged when it was so already: nothing was written.
public sealed record MemoryDueResolved(MemoryWrite Write, string Action, MemoryDue? Due, MemoryDue? Before);

public sealed partial class MemoryStore
{
    public const int DueMax = 50;

    // The install's time zone (D-043), as the database records it (install_settings, 017's install_timezone()).
    public async Task<TimeZoneInfo> InstallZone(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT install_timezone()");
        var name = (string)(await cmd.ExecuteScalarAsync(ct))!;
        if (name == "UTC") return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new MemoryRefused("invalid", $"The install's time zone ({name}) is not known on this system");
        }
    }

    // The due list a reader is served: memories it reads (the one read predicate on each one's own row) on the due list
    // (061's memory_due; a due date first, which its index answers), of those it owns and may change (058's
    // memory_loop_mine), optionally of one scope. One clause for every count and list, so a count is its list's length.
    static (string Where, List<NpgsqlParameter> Args) DueQueue(Guid principal, string? scope)
    {
        var args = new List<NpgsqlParameter> { new("principal", NpgsqlDbType.Uuid) { Value = principal } };
        var where = $"""
            {Database.Visible("r")} AND m.due_at IS NOT NULL AND memory_due(m.id) IS NOT NULL
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

    static string? DueWindow(string? named) => named is null ? null
        : DueDates.Windows.Contains(named) ? named
        : throw new MemoryRefused("invalid", "window is overdue, upcoming (within 48 hours), later or all");

    // The length of a reader's due list in one window (overdue, upcoming, later, or null: the whole list).
    public async Task<int> DueCount(Guid principal, string? scope, string? window, CancellationToken ct)
    {
        var (where, args) = DueQueue(principal, scope);
        args.Add(new("window", NpgsqlDbType.Text) { Value = (object?)DueWindow(window) ?? DBNull.Value });
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT count(*) {From} WHERE {where} AND (@window::text IS NULL OR memory_due_window(memory_due(m.id))=@window)
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // One page of the due list in one window (null: the whole list), soonest first, with its whole length and the three
    // windows' lengths, in one statement (one snapshot, one clock). After continues from a page's Next.
    public async Task<MemoryDuePage> Due(Guid principal, string? scope, string? window, int limit, MemoryDueCursor? after, int previewChars,
        CancellationToken ct)
    {
        if (limit is < 1 or > DueMax) throw new MemoryRefused("invalid", $"n is 1 to {DueMax}");
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var (where, args) = DueQueue(principal, scope);
        args.AddRange([
            new("window", NpgsqlDbType.Text) { Value = (object?)DueWindow(window) ?? DBNull.Value },
            new("limit", NpgsqlDbType.Integer) { Value = limit + 1 }, new("chars", NpgsqlDbType.Integer) { Value = previewChars },
            new("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.At.ToUniversalTime() : DBNull.Value },
            new("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.Id : DBNull.Value }]);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH q AS (
              SELECT m.id,memory_due(m.id) AS at {From} WHERE {where}),
            w AS (SELECT q.id,q.at,memory_due_window(q.at) AS win FROM q)
            SELECT t.total,t.overdue,t.upcoming,t.later,install_timezone(),x.* FROM (
              SELECT count(*) FILTER (WHERE @window::text IS NULL OR w.win=@window) AS total,
                count(*) FILTER (WHERE w.win='overdue') AS overdue,count(*) FILTER (WHERE w.win='upcoming') AS upcoming,
                count(*) FILTER (WHERE w.win='later') AS later FROM w) t
            LEFT JOIN LATERAL (
              SELECT m.id,m.revision,m.subject_id,m.scope,m.author_id,m.status,memory_due_json(w.at,m.due_kind,m.due_source)::text,w.win,
                memory_open_loop(m.id),m.tags,pr.id AS project,pr.slug,m.created_at,left(m.content,@chars),length(m.content)>@chars,w.at AS due_at
              FROM w JOIN memories m ON m.id=w.id LEFT JOIN memory_projects pr ON pr.id=m.project_id
              WHERE (@window::text IS NULL OR w.win=@window)
                AND (@afterAt::timestamptz IS NULL OR (w.at,w.id)>(@afterAt,@afterId))
              ORDER BY w.at,w.id LIMIT @limit) x ON true
            ORDER BY x.due_at,x.id
            """, c);
        foreach (var arg in args) cmd.Parameters.Add(arg);
        var items = new List<MemoryDueItem>();
        // Each item's moment as the database pages on it (column 20), not as its answer cuts it to the second.
        var moments = new List<DateTimeOffset>();
        int total = 0, overdue = 0, upcoming = 0, later = 0;
        var zone = "UTC";
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                (total, overdue, upcoming, later, zone) = ((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), r.GetString(4));
                if (r.IsDBNull(5)) continue;
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new(r.GetGuid(5), r.GetInt32(6), r.GetString(7), r.GetString(8), Text(9), r.GetString(10), MemoryDue.From(r.GetString(11))!,
                    r.GetString(12), Text(13), r.GetFieldValue<string[]>(14), r.IsDBNull(15) ? null : new(r.GetGuid(15), r.GetString(16)),
                    r.GetFieldValue<DateTimeOffset>(17), r.GetString(18), r.GetBoolean(19)));
                moments.Add(r.GetFieldValue<DateTimeOffset>(20));
            }
        var next = items.Count > limit ? new MemoryDueCursor(moments[limit - 1], items[limit - 1].Id) : null;
        return new(total, overdue, upcoming, later, zone, items.Take(limit).ToList(), next);
    }

    // remember: a new memory, with a due date when the caller gives one (when: an ISO date or date and time, read in the
    // install's zone when it has no offset; kind: due, event or wake, wake by default, due for a commitment, as upstream)
    // or, with none, when its words name one date that cannot be read two ways (kind due). A commitment (direction out or
    // in, and the other person's name; 058) is written with it. Without a due date it is Create's or Commit's write, and
    // NotTaken says why when the words named a date that was not taken.
    public async Task<MemoryRemembered> Remember(Guid principal, MemoryDraft draft, string? when, string? kind, string? direction, string? counterparty,
        MemoryCall call, CancellationToken ct)
    {
        if (kind is not null && when is null) throw new MemoryRefused("invalid", "when_kind requires when: nothing was saved");
        if (kind is not null && !DueDates.Kinds.Contains(kind)) throw new MemoryRefused("invalid", "when_kind is due, event or wake");
        if (direction is not (null or MemoryLoops.Out or MemoryLoops.In)) throw new MemoryRefused("invalid", "A commitment is owed by someone (in) or to someone (out)");
        var name = direction is null ? null : (counterparty ?? "").Trim();
        if (name is { Length: 0 or > MemoryLoops.NameMax } || name is not null && name.Any(char.IsControl))
            throw new MemoryRefused("invalid", $"The other person's name is 1 to {MemoryLoops.NameMax} characters");
        var (memory, dropped) = Drafted(draft);
        var zone = await InstallZone(ct);
        var now = DateTimeOffset.UtcNow;
        JsonObject? due = null, asked = null;
        DueDates.Reading? reading = null;
        if (when is not null)
        {
            var at = DueDates.When(when, zone, now);
            due = new() { ["at"] = Utc(at), ["kind"] = kind ?? (direction is null ? DueDates.Wake : DueDates.Due), ["source"] = DueDates.Explicit };
            asked = new() { ["when"] = when.Trim(), ["when_kind"] = kind };
        }
        else
        {
            reading = DueDates.FromText((string)memory["content"]!, zone, now);
            if (reading.At is { } at) due = new() { ["at"] = Utc(at), ["kind"] = DueDates.Due, ["source"] = DueDates.Text };
        }
        // A write's answer as remember gives it: the due date the database answered (a replayed answer is the first call's,
        // with its due date when that call took one), the commitment it holds, and why the words gave none.
        static MemoryRemembered Answered(MemoryWrite write, JsonElement answer, DueDates.Reading? notTaken)
        {
            var commitment = answer.TryGetProperty("commitment", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            return new(write, answer.TryGetProperty("due", out var d) ? MemoryDue.From(d) : null, notTaken,
                commitment.ValueKind == JsonValueKind.Object ? commitment.GetProperty("direction").GetString() : null,
                commitment.ValueKind == JsonValueKind.Object && commitment.TryGetProperty("counterparty", out var who) && who.ValueKind == JsonValueKind.String
                    ? who.GetString() : null);
        }
        if (due is null)
        {
            // Create's or Commit's write, with the database's whole answer: a keyed retry replays the first call's answer,
            // which answers for it whatever the words give on the day of the retry (across midnight, a date the first
            // call took has passed), so a replay says nothing of the words now.
            var notTaken = reading?.Reason is null ? null : reading;
            var (write, first) = direction is null
                ? await WriteAnswer("SELECT create_memory(@principal,@memory::jsonb,@channel,@client,@key)::text", principal, call, dropped, ct,
                    ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString()))
                : await WriteAnswer("SELECT remember_memory_commitment(@principal,@memory::jsonb,@direction,@counterparty,@channel,@client,@key)::text",
                    principal, call, dropped, ct, ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString()), ("direction", NpgsqlDbType.Text, direction),
                    ("counterparty", NpgsqlDbType.Text, name));
            return Answered(await Linked(principal, write, true, call, ct), first, write.Replayed ? null : Veiled(notTaken, write));
        }
        var (written, answer) = await WriteAnswer(
            "SELECT remember_memory_due(@principal,@memory::jsonb,@due::jsonb,@asked::jsonb,@direction,@counterparty,@channel,@client,@key)::text",
            principal, call, dropped, ct,
            ("memory", NpgsqlDbType.Jsonb, memory.ToJsonString()), ("due", NpgsqlDbType.Jsonb, due.ToJsonString()),
            ("asked", NpgsqlDbType.Jsonb, asked?.ToJsonString()), ("direction", NpgsqlDbType.Text, direction), ("counterparty", NpgsqlDbType.Text, name));
        return Answered(await Linked(principal, written, true, call, ct), answer, null);
    }

    static string Utc(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // Why no date was taken, without the words for a memory held for review (D-160): its words wait for a person.
    static DueDates.Reading? Veiled(DueDates.Reading? reading, MemoryWrite write) =>
        reading is { Reason: { } reason } && write.Hold is not null ? reading with { Why = DueDates.Unquoted(reason), Days = [] } : reading;

    // snooze (to until: an ISO date or date and time, read in the install's zone when it has no offset, still to come and
    // at most five years ahead) or clear_date, by whoever may change the memory, as a change of its due date (061's
    // resolve_memory_due). Expected: the revision the caller read, refused (conflict) when it moved on. No idempotency key: a
    // repeat answers unchanged. A decision is refused (invalid, the database's hint decision): it takes no due date, its
    // review date is its own (D-214). So is a snooze of a standing instruction (invalid, 065's constraint on the row): it
    // fires by topic.
    public async Task<MemoryDueResolved> ResolveDue(Guid principal, Guid memory, string action, string? until, int? expected, MemoryCall call,
        CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A snooze or clear takes no idempotency key: doing it again answers unchanged");
        var named = (action ?? "").Trim().ToLowerInvariant();
        if (!DueDates.Actions.Contains(named)) throw new MemoryRefused("invalid", "action is snooze or clear_date");
        DateTimeOffset? at = null;
        if (named == DueDates.Snooze) at = await SnoozeUntil(until, ct);
        else if (until is not null) throw new MemoryRefused("invalid", "clear_date takes no until: it takes the date off");
        var (write, answer) = await WriteAnswer("SELECT resolve_memory_due(@principal,@id,@action,@until,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("action", NpgsqlDbType.Text, named), ("until", NpgsqlDbType.TimestampTz, at),
            ("expected", NpgsqlDbType.Integer, expected));
        return DueFrom(write, answer);
    }

    // When a snooze's until comes, on the install's clocks, refused unless it is still to come.
    async Task<DateTimeOffset> SnoozeUntil(string? until, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(until)) throw new MemoryRefused("invalid", "snooze takes until: the date it should come back");
        var now = DateTimeOffset.UtcNow;
        var at = DueDates.When(until, await InstallZone(ct), now, "until");
        if (at <= now) throw new MemoryRefused("invalid", $"until must be in the future: {Utc(at)} has come already");
        return at;
    }

    static MemoryDueResolved DueFrom(MemoryWrite write, JsonElement answer) =>
        new(write, answer.GetProperty("action").GetString()!, MemoryDue.From(answer.GetProperty("due")), MemoryDue.From(answer.GetProperty("before")));
}
