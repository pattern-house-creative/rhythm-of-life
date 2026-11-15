using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// One insight stream (SB-P5-06; story 11; migration 066; D-018, D-031; docs/dev/memory.md sections 5, 7.1 and 8): the
// daily insight as a reader's stream, with what an AI proposed kept apart from what a person confirmed, a snooze that
// ends by the clock, each insight's feedback and reviews kept, and what the day came to, a day with no insight saying so.
// Nothing here writes an insight and no job is added: the outside AI client's daily prompt still does
// (harness/chatgpt/daily-insight.md), reading the candidates of InsightCandidates.cs.
//
// Only the memory tools call it (list_insight_stream, review_insight, insight_history, resolve_insight_candidate), and
// only with memory.tools on; off, none of it is reached and every insight answer is as it was (insight_json is unchanged).

// What a page of the stream holds for one insight: which view it is in, its review, a snooze's end and whether it has
// passed, when it became one to show, its scope and owner, the candidates it was written from, and the insight as every
// reader receives it (insight_json).
public sealed record InsightStreamItem(Guid Id, string View, string Review, DateTimeOffset? ReviewedAt, DateTimeOffset? SnoozeUntil, bool SnoozeEnded,
    DateTimeOffset ShownAt, string Scope, string Owner, IReadOnlyList<Guid> Candidates, JsonElement Insight);

// Total: the asked view's whole length; each view's length beside it, read in one statement with the page.
public sealed record InsightStreamPage(int Total, int Proposed, int Older, int Confirmed, int Snoozed, int Closed, IReadOnlyList<InsightStreamItem> Items,
    (DateTimeOffset At, Guid Id)? Next);

// What today came to for this reader: Outcome saved (an insight it reads was saved today, or the newest run it reads says
// it saved one it reads), quiet or blocked (the newest run it reads said so), or none (neither: no run it reads has logged,
// or one said saved and named no insight); the run's note, in its client's words; how many candidates of the scopes it
// reads were declined today; and Says, the same in plain words.
public sealed record InsightDay(string Date, string Zone, string Outcome, string? Note, Guid? InsightId, int Declined, string Says);

// A review: the insight, its review now, the view of the stream that puts it in (a reopened insight marked not useful or
// handled is closed, one with a snooze in force snoozed), when it was reviewed (none once reopened), and whether this call
// changed nothing.
public sealed record InsightReviewed(Guid Id, string Review, string View, DateTimeOffset? ReviewedAt, bool Unchanged);

// One feedback or review of an insight. Person: who, when its login speaks for one; Channel none for a feedback given
// before the history began or with memory.tools off, when who gave it was not recorded.
public sealed record InsightHistoryItem(string Event, string? Value, DateTimeOffset? Until, string? Note, string? Person, string? Channel, string? Client, DateTimeOffset At);

public sealed record InsightHistoryPage(Guid Id, string Review, int Total, IReadOnlyList<InsightHistoryItem> Items);

public sealed partial class InsightStore
{
    public const int StreamMax = 50, HistoryMax = 100;
    public const string Proposed = "proposed", Older = "older", Confirmed = "confirmed", Dismissed = "dismissed", Snoozed = "snoozed", Closed = "closed", All = "all";
    public static readonly string[] Views = [Proposed, Older, Confirmed, Snoozed, Closed, All];
    public static readonly string[] Reviews = ["confirm", "dismiss", "reopen"];

    // A refusal the memory tools answer with: an insight this caller does not read is unknown, as one that does not exist.
    static MemoryRefused? Refusal(PostgresException e) => e.SqlState switch
    {
        "P0002" => new("unknown", e.MessageText),
        "42501" => new("forbidden", e.MessageText),
        "22023" => new("invalid", e.MessageText),
        _ => null,
    };

    // One view of the stream, newest shown first, with every view's length read in the same statement.
    public async Task<InsightStreamPage> Stream(Guid principal, string view, string? scope, int limit, (DateTimeOffset At, Guid Id)? after, CancellationToken ct)
    {
        if (limit is < 1 or > StreamMax) throw new MemoryRefused("invalid", $"n is 1 to {StreamMax}");
        if (!Views.Contains(view)) throw new MemoryRefused("invalid", $"view is {string.Join(", ", Views)}");
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        await using var cmd = writer.CreateCommand("SELECT read_insight_stream(@p,@view,@scope,@limit,@at,@id,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("view", view);
        cmd.Parameters.AddWithValue("scope", NpgsqlDbType.Text, (object?)named ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit + 1);
        cmd.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, (object?)after?.At.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, (object?)after?.Id ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await Readable(principal, ct)));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        var items = o.GetProperty("items").EnumerateArray().Select(Item).ToList();
        var (proposed, older, confirmed, snoozed, closed) = (o.GetProperty(Proposed).GetInt32(), o.GetProperty(Older).GetInt32(), o.GetProperty(Confirmed).GetInt32(),
            o.GetProperty(Snoozed).GetInt32(), o.GetProperty(Closed).GetInt32());
        var total = view switch { Proposed => proposed, Older => older, Confirmed => confirmed, Snoozed => snoozed, Closed => closed, _ => proposed + older + confirmed + snoozed + closed };
        (DateTimeOffset, Guid)? next = items.Count > limit ? (items[limit - 1].ShownAt, items[limit - 1].Id) : null;
        return new(total, proposed, older, confirmed, snoozed, closed, items.Take(limit).ToList(), next);
    }

    static InsightStreamItem Item(JsonElement i) => new(i.GetProperty("id").GetGuid(), i.GetProperty("view").GetString()!, i.GetProperty("review").GetString()!,
        Instant(i, "reviewed_at"), Instant(i, "snooze_until"), i.GetProperty("snooze_ended").GetBoolean(), i.GetProperty("shown_at").GetDateTimeOffset(),
        i.GetProperty("scope").GetString()!, i.GetProperty("owner").GetString()!, [.. i.GetProperty("candidates").EnumerateArray().Select(c => c.GetGuid())],
        i.GetProperty("insight").Clone());

    static DateTimeOffset? Instant(JsonElement o, string name) => o.GetProperty(name) is { ValueKind: JsonValueKind.String } v ? v.GetDateTimeOffset() : null;
    static string? Text(JsonElement o, string name) => o.GetProperty(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    // The start of a local day as a moment; a midnight the clocks skip is the first moment after it.
    public static DateTimeOffset DayStart(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    // What a day came to, in plain words: an AI client's note is data, so it is told apart from these.
    public static string DaySays(string outcome) => outcome switch
    {
        "saved" => "An insight was proposed today.",
        "quiet" => "No insight today: the daily run looked and found nothing worth saying.",
        "blocked" => "No insight today: the daily run was blocked.",
        _ => DayNone,
    };

    // What a none says: true whether no run it reads has logged, or one said saved and named no insight it reads.
    public const string DayNone = "No insight today: none you read was saved, and no run says why.";

    // What today came to for this reader, in the install's zone: a saved insight first, whatever the run after it said; the
    // database answers one the newest run says it saved as saved too, and a saved run naming none falls through to none.
    public async Task<InsightDay> Today(Guid principal, TimeZoneInfo zone, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        await using var cmd = writer.CreateCommand("SELECT read_insight_day(@p,@from,@to,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, DayStart(day, zone));
        cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, DayStart(day.AddDays(1), zone));
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await Readable(principal, ct)));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        Guid? saved = o.GetProperty("saved") is { ValueKind: JsonValueKind.String } s ? s.GetGuid() : null;
        var outcome = saved is not null ? "saved" : Text(o, "outcome") is "quiet" or "blocked" ? Text(o, "outcome")! : "none";
        return new(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), zone.Id, outcome,
            outcome is "quiet" or "blocked" ? Text(o, "note") : null, saved, o.GetProperty("declined").GetInt32(), DaySays(outcome));
    }

    // Confirm, dismiss or reopen one insight this caller reads, under memory.md section 5's rule and never, over MCP, one
    // its own login saved. The same review again answers unchanged. Words given stay in the history.
    public async Task<InsightReviewed> Review(Guid principal, Guid id, string action, string? note, MemoryCall call, CancellationToken ct)
    {
        if (!Reviews.Contains(action)) throw new MemoryRefused("invalid", $"action is {string.Join(", ", Reviews)}");
        if (!Note(note, out var trimmed)) throw new MemoryRefused("invalid", "note is 1 to 500 characters once trimmed");
        var stars = await Readable(principal, ct);
        await using var cmd = writer.CreateCommand("SELECT review_insight(@p,@id,@action,@note,@channel,@client,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)trimmed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("channel", call.Channel);
        cmd.Parameters.AddWithValue("client", NpgsqlDbType.Text, (object?)AdminAudit.Client(call.Client) ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", stars));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("review").GetString()!, o.GetProperty("view").GetString()!, Instant(o, "reviewed_at"), o.GetProperty("unchanged").GetBoolean());
    }

    // A resolve's confirm or dismiss (SB-P5-15, migration 071): review_insight's rule and answer, once. An insight no longer
    // waiting is answered as it is and nothing is written, in either word.
    public async Task<(InsightReviewed Reviewed, bool Waiting)> ResolveReview(Guid principal, Guid id, string action, string? note, MemoryCall call, CancellationToken ct)
    {
        if (!Note(note, out var trimmed)) throw new MemoryRefused("invalid", "note is 1 to 500 characters once trimmed");
        var stars = await Readable(principal, ct);
        await using var cmd = writer.CreateCommand("SELECT resolve_insight_review(@p,@id,@action,@note,@channel,@client,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)trimmed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("channel", call.Channel);
        cmd.Parameters.AddWithValue("client", NpgsqlDbType.Text, (object?)AdminAudit.Client(call.Client) ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", stars));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        return (new(o.GetProperty("id").GetGuid(), o.GetProperty("review").GetString()!, o.GetProperty("view").GetString()!, Instant(o, "reviewed_at"), o.GetProperty("unchanged").GetBoolean()),
            o.GetProperty("waiting").GetBoolean());
    }

    // An insight's feedback and reviews, newest first, for a reader of the insight.
    public async Task<InsightHistoryPage> History(Guid principal, Guid id, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > HistoryMax) throw new MemoryRefused("invalid", $"limit is 1 to {HistoryMax}");
        var stars = await Readable(principal, ct);
        await using var cmd = writer.CreateCommand("SELECT read_insight_history(@p,@id,@limit,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", stars));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        return new(id, o.GetProperty("review").GetString()!, o.GetProperty("total").GetInt32(), [.. o.GetProperty("items").EnumerateArray().Select(e =>
            new InsightHistoryItem(e.GetProperty("event").GetString()!, Text(e, "value"), Instant(e, "until"), Text(e, "note"), Text(e, "person"), Text(e, "channel"),
                Text(e, "client"), e.GetProperty("at").GetDateTimeOffset()))]);
    }

    // A candidate's decision with why it was declined or the insight written from it (migration 066's longer form of 064's
    // function; MemoryInsights.cs lists the candidates). The stars are an insight's read check. A repeat answers what was
    // kept, the insight only to a reader of its subject.
    public async Task<MemoryInsightResolved> Decide(Guid principal, Guid candidate, string action, string? note, Guid? insight, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Deciding a candidate takes no idempotency key: the same decision again answers unchanged");
        if (!Note(note, out var trimmed)) throw new MemoryRefused("invalid", "note is 1 to 500 characters once trimmed");
        var stars = insight is null ? null : await Readable(principal, ct);
        await using var cmd = writer.CreateCommand("SELECT resolve_insight_candidate(@p,@id,@action,@channel,@client,@note,@insight,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", candidate); cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("channel", call.Channel);
        cmd.Parameters.AddWithValue("client", NpgsqlDbType.Text, (object?)AdminAudit.Client(call.Client) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)trimmed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("insight", NpgsqlDbType.Uuid, (object?)insight ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", stars));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        return Decided(doc.RootElement);
    }

    // A resolve's use or reject (SB-P5-15, migration 071): Decide's rule and answer, once. A candidate decided already is answered
    // as it was decided and nothing is written, in either word.
    public async Task<(MemoryInsightResolved Decided, bool Waiting)> ResolveCandidate(Guid principal, Guid candidate, string action, string? note, Guid? insight, MemoryCall call, CancellationToken ct)
    {
        if (!Note(note, out var trimmed)) throw new MemoryRefused("invalid", "note is 1 to 500 characters once trimmed");
        var stars = insight is null ? null : await Readable(principal, ct);
        await using var cmd = writer.CreateCommand("SELECT resolve_insight_candidate_once(@p,@id,@action,@note,@insight,@channel,@client,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", candidate); cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)trimmed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("insight", NpgsqlDbType.Uuid, (object?)insight ?? DBNull.Value);
        cmd.Parameters.AddWithValue("channel", call.Channel);
        cmd.Parameters.AddWithValue("client", NpgsqlDbType.Text, (object?)AdminAudit.Client(call.Client) ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", stars));
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        return (Decided(doc.RootElement), doc.RootElement.GetProperty("waiting").GetBoolean());
    }

    static MemoryInsightResolved Decided(JsonElement o) =>
        new(o.GetProperty("id").GetGuid(), o.GetProperty("status").GetString()!, o.GetProperty("decided_at").GetDateTimeOffset(),
            o.GetProperty("unchanged").GetBoolean(), Text(o, "note"), o.GetProperty("insight_id") is { ValueKind: JsonValueKind.String } w ? w.GetGuid() : null);
}
