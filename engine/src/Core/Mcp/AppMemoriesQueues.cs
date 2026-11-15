using System.Text.Json.Nodes;

namespace Knowledge.Mcp;

// app_memories' ops for a person's review queues (SB-P6-01, D-218, D-222, D-224; the rest of the tool is AppMemories.cs, whose header
// says what every op keeps to: the channel fixed as the app's, a person's, only with memory.tools on, each through the store's checked
// functions).
//
// queues: how many wait in each queue, each the length of that queue's own list (MemoryStore.QueueCounts). insight_candidates: the
// pairs the nightly pass found, in one state at a time, best first, with both previews and that list's whole length
// (MemoryStore.Insights, 064's one predicate). insight_stream: the one insight stream, one view at a time, each view's length read
// with its page, what today came to and which of them this login may review (InsightStore.Stream, Today, MayReview, 066's one
// definition); an insight's words are served as insight_json gives every reader. insight_history: one insight's feedback and
// reviews. review_insight: a person's confirm, dismiss or reopen, which is how a review is taken back. resolve: one door to a
// person's review queues, MemoryStore.ResolveItem through the app's channel, so an item leaves its queue once whatever the word:
// release and the settlings, each only a person's, keep_copy, still_true, a candidate's use or reject, an insight's confirm or
// dismiss. Loops, decisions, dates and standing instructions are not taken here: their screens have their own. An AI client's token
// reaches none of it: the door sends it to the MCP tools, where resolve answers release and a settling needs_person, and if its login
// ever reached this handler the database refuses it the app's channel (memory_channel, 044).
public static partial class AppMemories
{
    public const int PairsDefault = 20, StreamDefault = 20, InsightHistoryDefault = 50;

    // What a person's review of an insight takes. Confirm and dismiss through resolve leave its queue once; reopen is no resolve.
    static readonly string[] ReviewWords = InsightStore.Reviews;

    static async Task<JsonObject> QueueCounts(IServiceProvider services, MemoryStore store, Guid principal, string? scope, MemoryCall call, CancellationToken ct)
    {
        var c = await store.QueueCounts(principal, scope, MemoryTools.Insights(services), call, ct);
        return new()
        {
            ["held"] = c.Held, ["duplicates"] = c.Duplicates, ["contradictions"] = c.Contradictions, ["stale"] = c.Stale, ["candidates"] = c.Candidates, ["insights"] = c.Insights,
        };
    }

    static async Task<JsonObject> Pairs(MemoryStore store, Guid principal, string? scope, string? state, string? cursor, int limit, CancellationToken ct)
    {
        var page = await store.Insights(principal, scope, limit, MemoryTools.InsightCursor(cursor), ListPreview, ct, state ?? MemoryStore.InsightWaiting);
        static JsonObject Side(MemoryInsightSide s) => new()
        {
            ["id"] = s.Id.ToString(), ["revision"] = s.Revision, ["createdAt"] = s.CreatedAt, ["importance"] = s.Importance, ["preview"] = s.Content, ["truncated"] = s.Truncated,
        };
        return new()
        {
            ["candidates"] = new JsonArray([.. page.Items.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id!.Value.ToString(), ["scope"] = c.Scope, ["owner"] = c.Subject, ["score"] = c.Score, ["signals"] = Strings(c.Signals), ["similarity"] = c.Similarity,
                ["gapDays"] = c.GapDays, ["foundAt"] = c.FoundAt, ["older"] = Side(c.Older), ["newer"] = Side(c.Newer), ["status"] = c.Status, ["decidedAt"] = c.DecidedAt,
                ["note"] = c.Note, ["insight"] = c.InsightId?.ToString(),
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.InsightCursor(next) : null,
            ["count"] = page.Total,
        };
    }

    static async Task<JsonObject> Stream(IServiceProvider services, MemoryStore store, Guid principal, string? view, string? scope, string? cursor, int limit, CancellationToken ct)
    {
        var insights = MemoryTools.Insights(services);
        var page = await insights.Stream(principal, view ?? InsightStore.Proposed, scope, limit, MemoryTools.Cursor(cursor), ct);
        var day = await insights.Today(principal, await store.InstallZone(ct), ct);
        var may = await insights.MayReview(principal, [.. page.Items.Select(i => i.Id)], ct);
        return new()
        {
            ["view"] = view ?? InsightStore.Proposed, ["count"] = page.Total, ["proposed"] = page.Proposed, ["older"] = page.Older, ["confirmed"] = page.Confirmed,
            ["snoozed"] = page.Snoozed, ["closed"] = page.Closed, ["candidates"] = await store.InsightCount(principal, scope, ct),
            ["day"] = new JsonObject
            {
                ["date"] = day.Date, ["zone"] = day.Zone, ["outcome"] = day.Outcome, ["note"] = day.Note, ["insight"] = day.InsightId?.ToString(), ["declined"] = day.Declined, ["says"] = day.Says,
            },
            ["insights"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["view"] = i.View, ["review"] = i.Review, ["reviewedAt"] = i.ReviewedAt, ["snoozeUntil"] = i.SnoozeUntil, ["snoozeEnded"] = i.SnoozeEnded,
                ["shownAt"] = i.ShownAt, ["scope"] = i.Scope, ["owner"] = i.Owner, ["candidates"] = Strings(i.Candidates.Select(c => c.ToString())), ["mayReview"] = may.Contains(i.Id),
                ["insight"] = JsonNode.Parse(i.Insight.GetRawText()),
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.At, next.Id) : null,
        };
    }

    static async Task<JsonObject> InsightEvents(IServiceProvider services, Guid principal, Guid id, int limit, CancellationToken ct)
    {
        var page = await MemoryTools.Insights(services).History(principal, id, limit, ct);
        return new()
        {
            ["id"] = page.Id.ToString(), ["review"] = page.Review, ["count"] = page.Total,
            ["events"] = new JsonArray([.. page.Items.Select(e => (JsonNode)new JsonObject
            {
                ["event"] = e.Event, ["value"] = e.Value, ["until"] = e.Until, ["note"] = e.Note, ["person"] = e.Person, ["channel"] = e.Channel, ["client"] = e.Client, ["at"] = e.At,
            })]),
        };
    }

    // A review (InsightStore.Review): the insight, its review now, the view that puts it in, when, and whether this call changed nothing.
    static JsonObject ReviewedInsight(InsightReviewed r) => new()
    {
        ["op"] = "review_insight", ["id"] = r.Id.ToString(), ["review"] = r.Review, ["view"] = r.View, ["reviewedAt"] = r.ReviewedAt, ["unchanged"] = r.Unchanged,
    };

    // What a resolve did: which queue, whether the item was waiting and this call changed anything, and whether its queue still counts
    // it (a copy with a further flag). One step's answer beside it: a memory's place, what stood against it, a settling's other memory and
    // decision, a review, a candidate's decision and a still true's check. No memory's words.
    static JsonObject ResolvedItem(MemoryResolved r)
    {
        var w = r.Write;
        var answer = new JsonObject
        {
            ["op"] = "resolve", ["queue"] = r.Queue, ["action"] = r.Action, ["id"] = r.Id.ToString(), ["unchanged"] = r.Unchanged, ["waiting"] = r.Waiting, ["stillCounted"] = r.StillCounted,
            ["revision"] = w?.Revision, ["scope"] = w?.Scope, ["owner"] = w?.Subject, ["status"] = w?.Status, ["scopes"] = w?.Scopes, ["changed"] = Strings(w?.Changed ?? []),
            ["held"] = w?.Hold is { } h ? Held(h.Reason, h.Signals) : null,
            ["target"] = r.Settled?.Conflict.ToString(), ["decision"] = r.Settled?.Decision,
            ["review"] = r.Review?.Review, ["view"] = r.Review?.View, ["reviewedAt"] = r.Review?.ReviewedAt,
            ["candidate"] = r.Candidate?.Status, ["decidedAt"] = r.Candidate?.DecidedAt, ["note"] = r.Candidate?.Note, ["insight"] = r.Candidate?.InsightId?.ToString(),
            ["checkedAt"] = r.Confirmed?.CheckedAt, ["was"] = r.Confirmed?.Was, ["retracted"] = Strings((r.Confirmed?.Retracted ?? []).Select(x => x.ToString())),
        };
        return w is null ? answer : Standing(Copies(Cascaded(answer, w.Cascade), "duplicates", w.Hold is null ? w.Duplicates : null),
            "contradictionCandidates", w.Hold is null ? w.Candidates : null);
    }
}
