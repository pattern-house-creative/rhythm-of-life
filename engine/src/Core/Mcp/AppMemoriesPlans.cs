using System.Text.Json.Nodes;

namespace Knowledge.Mcp;

// app_memories' ops for the brief's lists behind its Home card (SB-P6-01; the rest of the tool is AppMemories.cs, whose header
// says what every op keeps to: the channel fixed as the app's, a person's, only with memory.tools on, each through the store's
// checked functions).
//
// loops, due and decisions read the very lists the brief heads (MemoryStore.Loops, Due and Decisions: 058's memory_open_loop,
// 061's memory_due and 060's memory_decision_listed), so a count on the card is the length of the list it opens: you owe and owed
// to you are loops' two directions, due is its overdue and upcoming windows, and decisions to review is decisions' due and upcoming
// lists. Each page says the lengths beside it from the one statement that read it. calibration is the arithmetic the card's line
// comes from (MemoryDecisions.Calibrate), with how many decisions each figure rests on and no figure below its minimum.
//
// resolve_loop, resolve_due and record_outcome are a person's own word on one of them, through the same checked functions as the
// MCP tools of those names (resolve_memory_loop, resolve_memory_due, record_memory_outcome), so the author lock, the holds and the
// audit row decide as they do there; only the channel differs. They are the steps themselves and not MemoryStore.ResolveItem, whose
// "once" is for a client that may repeat itself: a person corrects an outcome or moves a date they already put off, which a
// resolve answers unchanged, and a stale second tap is refused by the revision the person read (expected). An AI client's token
// never reaches any of this: the door sends it to the MCP tools (Perimeter), where it has its own channel, and the database
// refuses the app's channel from any login but the app's.
public static partial class AppMemories
{
    public const int PlansDefault = 20;
    static readonly string[] CalibrationSources = [MemoryDecisions.Stated, MemoryDecisions.Inferred], Directions = [MemoryLoops.Out, MemoryLoops.In];

    static async Task<JsonObject> LoopList(MemoryStore store, Guid principal, string? scope, string? direction, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Loops(principal, scope, direction, limit, at is { } a ? new MemoryLoopCursor(a.At, a.Id) : null, ListPreview, ct);
        return new()
        {
            ["loops"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["revision"] = i.Revision, ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["status"] = i.Status,
                ["direction"] = i.Direction, ["counterparty"] = MemoryLoops.Name(i.Counterparty), ["tags"] = Strings(i.Tags), ["project"] = Project(i.Project),
                ["createdAt"] = i.CreatedAt, ["preview"] = i.Content, ["truncated"] = i.Truncated,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.CreatedAt, next.Id) : null,
            ["count"] = page.Total, ["youOwe"] = page.YouOwe, ["owedToYou"] = page.OwedToYou,
        };
    }

    static async Task<JsonObject> DueList(MemoryStore store, Guid principal, string? scope, string? window, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Due(principal, scope, window, limit, at is { } a ? new MemoryDueCursor(a.At, a.Id) : null, ListPreview, ct);
        return new()
        {
            ["due"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["revision"] = i.Revision, ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["status"] = i.Status,
                ["window"] = i.Window, ["at"] = i.Due.At, ["local"] = i.Due.Local, ["kind"] = i.Due.Kind, ["source"] = i.Due.Source, ["loop"] = i.Loop,
                ["tags"] = Strings(i.Tags), ["project"] = Project(i.Project), ["createdAt"] = i.CreatedAt, ["preview"] = i.Content, ["truncated"] = i.Truncated,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.At, next.Id) : null,
            ["count"] = page.Total, ["overdue"] = page.Overdue, ["upcoming"] = page.Upcoming, ["later"] = page.Later, ["zone"] = page.Zone,
        };
    }

    static async Task<JsonObject> DecisionList(MemoryStore store, Guid principal, string? scope, string? state, string? cursor, int limit, CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Decisions(principal, scope, state ?? MemoryDecisions.Open, limit, at is { } a ? new MemoryDecisionCursor(a.At, a.Id) : null, ListPreview, ct);
        return new()
        {
            ["decisions"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["revision"] = i.Revision, ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["status"] = i.Status,
                ["confidence"] = i.Confidence, ["confidenceSource"] = i.ConfidenceSource, ["outcome"] = i.Outcome, ["rearms"] = i.Rearms, ["reviewAt"] = i.ReviewAt,
                ["window"] = i.Window, ["current"] = i.Current, ["edited"] = i.Edited, ["tags"] = Strings(i.Tags), ["project"] = Project(i.Project),
                ["createdAt"] = i.CreatedAt, ["preview"] = i.Content, ["truncated"] = i.Truncated,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.CreatedAt, next.Id) : null,
            ["count"] = page.Total, ["open"] = page.Open, ["dueNow"] = page.Due, ["upcoming"] = page.Upcoming, ["resolved"] = page.Resolved, ["scored"] = page.Scored,
        };
    }

    // How sure the person was against how things turned out, each figure with what it rests on; below a minimum, counts only.
    static JsonObject Calibrated(DecisionCalibration c, string? source) => new()
    {
        ["source"] = source ?? "all", ["ready"] = c.Ready, ["n"] = c.N, ["nStated"] = c.NStated, ["nInferred"] = c.NInferred,
        ["minDecisions"] = c.MinDecisions, ["minBand"] = c.MinBand, ["minTopic"] = c.MinTopic, ["brier"] = c.Brier, ["gap"] = c.Gap, ["direction"] = c.Direction,
        ["bands"] = new JsonArray([.. c.Bands.Select(b => (JsonNode)new JsonObject
        {
            ["band"] = b.Band, ["n"] = b.N, ["nStated"] = b.NStated, ["nInferred"] = b.NInferred, ["shown"] = b.Shown,
            ["meanConfidence"] = b.MeanConfidence, ["hitRate"] = b.HitRate, ["low"] = b.Low, ["high"] = b.High,
        })]),
        ["headline"] = c.Headline,
        ["topic"] = c.Topic is { } t ? new JsonObject { ["name"] = t.Name, ["n"] = t.N, ["gap"] = t.Gap, ["direction"] = t.Direction } : null,
        ["kind"] = c.Kind, ["line"] = c.Line, ["topicLine"] = c.TopicLine, ["stated"] = c.Stated, ["hit"] = c.Hit,
    };

    // A write's answer (never the memory's words), with what its step said: the loop's direction and the other person's name.
    static JsonObject LoopResolved(MemoryLoopResolved r)
    {
        var answer = Written("resolve_loop", r.Write);
        answer["action"] = r.Action;
        answer["direction"] = r.Direction;
        answer["counterparty"] = MemoryLoops.Name(r.Counterparty);
        return answer;
    }

    // A due date as a page gives it, or none.
    static JsonObject? DueAt(MemoryDue? d) => d is null ? null
        : new JsonObject { ["at"] = d.At, ["local"] = d.Local, ["zone"] = d.Zone, ["kind"] = d.Kind, ["source"] = d.Source };

    static JsonObject DueResolved(MemoryDueResolved r)
    {
        var answer = Written("resolve_due", r.Write);
        answer["action"] = r.Action;
        answer["due"] = DueAt(r.Due);
        answer["before"] = DueAt(r.Before);
        return answer;
    }

    static JsonObject OutcomeRecorded(MemoryOutcome o)
    {
        var answer = Written("record_outcome", o.Write);
        answer["result"] = o.Result;
        answer["previous"] = o.Previous;
        answer["rearms"] = o.Rearms;
        answer["reviewAt"] = o.ReviewAt;
        answer["reviewsDone"] = o.ReviewsDone;
        return answer;
    }
}
