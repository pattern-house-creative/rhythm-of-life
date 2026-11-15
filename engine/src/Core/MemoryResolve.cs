using System.Text.Json;
using NpgsqlTypes;

namespace Knowledge;

// One resolve for every review queue (SB-P5-15; story 12; docs/dev/memory.md sections 5 and 7.1; migration 071; adapted from
// Second Brain's resolve tool, src/memory/actions.ts at the pinned commit, MIT; NOTICE). Each action is one of a queue's
// existing steps, with that step's author lock, audit row and answer. What this adds is once: an item is taken off its queue
// by one resolve, and a second, in the same word or another, finds it no longer waiting (071's memory_resolve_waiting, each
// queue's own predicate), writes nothing and says so, so each count drops by exactly one, but for a snooze into the 48 hours and
// a copy with a further flag (docs/dev/memory.md section 5). The steps' own tools stay for what is no resolve: correcting an outcome
// (record_outcome), moving a date a person already put off (resolve_due), confirming a fact that is not in the stale queue yet
// (still_true), taking a review back (review_insight's reopen), taking the mark off an instruction whose time has ended
// (stop_standing).
//
//   stale                still_true                          confirm_memory (053)
//   loops                done, received, not_a_task          resolve_memory_loop (058)
//   decisions            outcome, with right, wrong, mixed or unknown    record_memory_outcome (060)
//   due                  snooze, clear_date                  resolve_memory_due (061)
//   standing             stop_standing                       stop_memory_standing (065)
//   duplicates           keep_copy                           clear_memory_duplicates (063, 068)
//   insight_candidates   use_candidate, reject_candidate     resolve_insight_candidate (064, 066)
//   insights             confirm_insight, dismiss_insight    review_insight (066)
//   held                 release                             release_memory_hold (036): a person's only
//   contradictions       settle_keep_both, settle_replace    settle_memory_contradiction (068): a person's only
//
// The person-only steps are answered needs_person on an AI client's channel before anything is read, whatever the id names, so
// the call says nothing about it. On any other channel they go to their own functions, which tell a person from an AI client by
// the channel (memory_channel, 044): an AI client's login naming the app's or the terminal's channel is refused there. No
// other step reaches them either: an AI client's keep_copy takes the flags off and leaves the pair queued for a person (068), and
// no action on a held memory lifts its hold (refused held).
//
// The memory tool resolve calls it, always on an AI client's channel; a person's review queues call it through app_memories' resolve
// op and `rhythm resolve`, on the app's and the terminal's (SB-P6-01). All of them only with memory.tools on, off by default. With it
// off nothing is read or written.

public static class MemoryResolve
{
    public const string StillTrue = "still_true", Outcome = "outcome", StopStanding = "stop_standing", KeepCopy = "keep_copy", UseCandidate = "use_candidate",
        RejectCandidate = "reject_candidate", ConfirmInsight = "confirm_insight", DismissInsight = "dismiss_insight", Release = "release",
        SettleKeepBoth = "settle_keep_both", SettleReplace = "settle_replace";

    public const string Stale = "stale", Loops = "loops", Decisions = "decisions", DueList = "due", Standing = "standing", Duplicates = "duplicates",
        Candidates = "insight_candidates", Insights = "insights", Held = "held", Contradictions = "contradictions";

    // Which queue each action resolves: every action there is.
    public static readonly IReadOnlyDictionary<string, string> Queues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [StillTrue] = Stale, [MemoryLoops.Done] = Loops, [MemoryLoops.Received] = Loops, [MemoryLoops.NotATask] = Loops, [Outcome] = Decisions,
        [DueDates.Snooze] = DueList, [DueDates.ClearDate] = DueList, [StopStanding] = Standing, [KeepCopy] = Duplicates,
        [UseCandidate] = Candidates, [RejectCandidate] = Candidates, [ConfirmInsight] = Insights, [DismissInsight] = Insights,
        [Release] = Held, [SettleKeepBoth] = Contradictions, [SettleReplace] = Contradictions,
    };

    // The steps only a person takes (memory.md section 5).
    public static bool PersonOnly(string action) => Queues[action] is Held or Contradictions;

    // What a person's review queues take through this one door, in the app and at the terminal (SB-P6-01): the held, the copies, the
    // contradictions, the stale, the candidates and the insights. Loops, decisions, dates and standing instructions have screens of
    // their own.
    public static readonly string[] ReviewActions = [Release, KeepCopy, SettleKeepBoth, SettleReplace, StillTrue, UseCandidate, RejectCandidate, ConfirmInsight, DismissInsight];

    // What a person's review queues take for several at once (MemoryStore.ResolveAll, SB-P6-01): the steps that need nothing but the
    // item and, for a memory, the revision its person read. Release has its own several (ReleaseAll); a copy's keep both is asked
    // once its close memories are read whole, a settling names the other memory and a candidate's use the insight written from it,
    // so each of those stays one at a time.
    public static readonly string[] BulkActions = [StillTrue, RejectCandidate, ConfirmInsight, DismissInsight];

    public const string NeedsPerson = "Only a person does this, from the app or the terminal, never an AI client: tell the person, and leave it as it is.";

    // The most memories a keep both names as read: the duplicate review shows at most 3 a flagged memory is close to (068).
    public const int CopiesMax = 3;
}

// One memory a flagged copy is close to, at the revision the person read it (073's resolve_memory_copy).
public sealed record MemoryCopyRead(Guid Id, int Revision);

// What a resolve is asked. Result: with outcome. Until: with snooze. Note: with a candidate or an insight. Insight: with
// use_candidate, the insight written from the pair. Conflict: with a settling, the other memory, and ConflictExpected the revision
// of it the person read. Expected: the revision the caller read of a memory. Copies: with keep_copy, each memory it is close to at
// the revision read, so a keep both is refused, nothing changed, when one the review shows changed since or was not read (073);
// without them keep_copy checks the flagged memory's revision alone, as before: an AI client's, and an older app's. The terminal
// always names them, and the view names them whenever it offers keep both.
public sealed record MemoryResolveAsk(Guid Id, string Action, string? Result = null, string? Until = null, string? Note = null, Guid? Insight = null,
    Guid? Conflict = null, int? Expected = null, int? ConflictExpected = null, IReadOnlyList<MemoryCopyRead>? Copies = null);

// What a resolve did. Waiting: the item was waiting when the call came; false, nothing was written and only the memory's place is
// here, with Unchanged true. StillCounted: the brief's section for this queue counted it and still does (a snooze to within 48
// hours, a copy with a further flag). NewlyCounted: it did not and now does (a snooze from past the 48 hours into them). One step's
// answer beside it, or none: Write alone for stop_standing, keep_copy and release.
public sealed record MemoryResolved(string Queue, string Action, Guid Id, bool Unchanged, bool Waiting, bool StillCounted, MemoryWrite? Write = null,
    MemoryLoopResolved? Loop = null, MemoryOutcome? Outcome = null, MemoryDueResolved? Due = null, MemoryConfirmed? Confirmed = null,
    InsightReviewed? Review = null, MemoryInsightResolved? Candidate = null, MemorySettled? Settled = null, bool NewlyCounted = false);

public sealed partial class MemoryStore
{
    public async Task<MemoryResolved> ResolveItem(Guid principal, MemoryResolveAsk ask, InsightStore insights, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A resolve takes no idempotency key: resolving again answers unchanged");
        var action = (ask.Action ?? "").Trim().ToLowerInvariant();
        if (!MemoryResolve.Queues.TryGetValue(action, out var queue)) throw new MemoryRefused("invalid", $"action is {string.Join(", ", MemoryResolve.Queues.Keys)}");
        if (MemoryResolve.PersonOnly(action) && call.Channel == "mcp") throw new MemoryRefused("needs_person", MemoryResolve.NeedsPerson);
        if (ask.Result is not null != (action == MemoryResolve.Outcome)) throw new MemoryRefused("invalid", action == MemoryResolve.Outcome
            ? "outcome takes result: right, wrong, mixed or unknown" : "result goes with action outcome");
        if (ask.Until is not null != (action == DueDates.Snooze)) throw new MemoryRefused("invalid", action == DueDates.Snooze
            ? "snooze takes until: the date it should come back" : "until goes with action snooze");
        if (ask.Note is not null && queue is not (MemoryResolve.Candidates or MemoryResolve.Insights)) throw new MemoryRefused("invalid", "note goes with an insight or a candidate");
        if (ask.Insight is not null && action != MemoryResolve.UseCandidate) throw new MemoryRefused("invalid", "insight_id goes with action use_candidate");
        if (ask.Expected is not null && queue is MemoryResolve.Candidates or MemoryResolve.Insights) throw new MemoryRefused("invalid", "expected_revision goes with a memory: an insight or a candidate has no revision");
        if (ask.Conflict is not null != (queue == MemoryResolve.Contradictions)) throw new MemoryRefused("invalid", queue == MemoryResolve.Contradictions
            ? "A settling names the other memory" : "The other memory goes with a settling");
        if (ask.Copies is not null && action != MemoryResolve.KeepCopy) throw new MemoryRefused("invalid", "The memories read go with action keep_copy");
        if (ask.Copies is { } named && (named.Count is < 1 or > MemoryResolve.CopiesMax || named.Select(c => c.Id).Distinct().Count() != named.Count
            || named.Any(c => c.Revision < 1)))
            throw new MemoryRefused("invalid", $"A keep both names 1 to {MemoryResolve.CopiesMax} memories it is close to, each once at the revision read");

        switch (action)
        {
            case MemoryResolve.Release:
            {
                var w = await Release(principal, ask.Id, ask.Expected, call, ct);
                return new(queue, action, ask.Id, w.Unchanged, !w.Unchanged, false, w);
            }
            case MemoryResolve.SettleKeepBoth or MemoryResolve.SettleReplace:
            {
                var s = await Settle(principal, ask.Id, ask.Conflict!.Value, action == MemoryResolve.SettleReplace ? MemoryContradictions.Replace : MemoryContradictions.KeepBoth,
                    ask.Expected, ask.ConflictExpected, call, ct);
                return new(queue, action, ask.Id, s.Write.Unchanged, !s.Write.Unchanged, false, s.Write, Settled: s);
            }
            case MemoryResolve.ConfirmInsight or MemoryResolve.DismissInsight:
            {
                var (r, waiting) = await insights.ResolveReview(principal, ask.Id, action == MemoryResolve.ConfirmInsight ? "confirm" : "dismiss", ask.Note, call, ct);
                return new(queue, action, r.Id, r.Unchanged, waiting, false, Review: r);
            }
            case MemoryResolve.UseCandidate or MemoryResolve.RejectCandidate:
            {
                var (r, waiting) = await insights.ResolveCandidate(principal, ask.Id, action == MemoryResolve.UseCandidate ? "use" : "reject", ask.Note, ask.Insight, call, ct);
                return new(queue, action, r.Id, r.Unchanged, waiting, false, Candidate: r);
            }
        }

        var result = (ask.Result ?? "").Trim().ToLowerInvariant();
        if (action == MemoryResolve.Outcome && !MemoryDecisions.Results.Contains(result)) throw new MemoryRefused("invalid", "result is right, wrong, mixed or unknown");
        DateTimeOffset? until = action == DueDates.Snooze ? await SnoozeUntil(ask.Until, ct) : null;
        var (write, answer) = await WriteAnswer(ask.Copies is null
                ? "SELECT resolve_memory_item(@principal,@id,@action,@result,@until,@expected,@channel,@client)::text"
                : "SELECT resolve_memory_copy(@principal,@id,@expected,@copies::jsonb,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, ask.Id), ("action", NpgsqlDbType.Text, action), ("result", NpgsqlDbType.Text, action == MemoryResolve.Outcome ? result : null),
            ("until", NpgsqlDbType.TimestampTz, until), ("expected", NpgsqlDbType.Integer, ask.Expected),
            ("copies", NpgsqlDbType.Text, ask.Copies is { } copies ? JsonSerializer.Serialize(copies.Select(c => new { id = c.Id, revision = c.Revision })) : null));
        var was = answer.GetProperty("waiting").GetBoolean();
        var counted = answer.GetProperty("counted").GetBoolean();
        // A date past the 48 hours was in no count of the brief, so a snooze into them brings it in; it never stays in.
        var entered = counted && answer.GetProperty("window_before").ValueKind == JsonValueKind.String && answer.GetProperty("window_before").GetString() == DueDates.Later;
        MemoryResolved Answered(MemoryLoopResolved? loop = null, MemoryOutcome? outcome = null, MemoryDueResolved? due = null, MemoryConfirmed? confirmed = null) =>
            new(queue, action, write.Id, write.Unchanged, was, counted && !entered, write, loop, outcome, due, confirmed, NewlyCounted: entered);
        if (!was) return Answered();
        return action switch
        {
            MemoryResolve.StillTrue => Answered(confirmed: ConfirmedFrom(answer)),
            MemoryResolve.Outcome => Answered(outcome: OutcomeFrom(write, answer, result)),
            DueDates.Snooze or DueDates.ClearDate => Answered(due: DueFrom(write, answer)),
            MemoryLoops.Done or MemoryLoops.Received or MemoryLoops.NotATask => Answered(loop: LoopFrom(write, answer)),
            // Keeping both lets the automatic links be drawn again, as KeepCopy does.
            MemoryResolve.KeepCopy => new(queue, action, write.Id, write.Unchanged, was, counted, await Redrawn(principal, write, call, ct)),
            _ => Answered(),
        };
    }
}
