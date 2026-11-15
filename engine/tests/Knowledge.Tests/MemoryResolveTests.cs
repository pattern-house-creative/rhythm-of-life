using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Knowledge.Tests;

// One resolve for every review queue (SB-P5-15, migration 071, Core/MemoryResolve.cs; story 12; docs/dev/memory.md sections 5 and
// 7.1): every queue's actions through the tool, each its queue's own step; an item leaves once, so its count drops by exactly one,
// and a second resolve, in the same word or another, writes nothing and says so; a person's steps refused to an AI principal from
// every channel; scope. Fictional people only: alex, sam and the household; every text is DEMO.
static class Resolves
{
    public static readonly MemoryCall Mcp = MemoryLogins.Mcp, App = new("app"), Cli = new("cli", "rhythm");

    // What any queue's step leaves behind: an audit row, in whichever trail the queue keeps.
    public static async Task<long> Audit(NpgsqlDataSource s) => (long)(await MemoryRows.Scalar(s, """
        SELECT (SELECT count(*) FROM memory_events)+(SELECT count(*) FROM insight_events)+(SELECT count(*) FROM memory_insight_events)
        """))!;

    // Everything a step changes that an audit row might not say: each memory's revision, tags, status and date, its flags and
    // checks, its kept versions, each insight's review and each candidate's decision.
    public static async Task<string> State(NpgsqlDataSource s) => (string)(await MemoryRows.Scalar(s, """
        SELECT concat_ws('|',(SELECT string_agg(concat_ws(':',id,revision,tags,status,due_at,standing,updated_at),',' ORDER BY id) FROM memories),
          (SELECT count(*) FROM memory_versions),(SELECT count(*) FROM memory_duplicate_flags),
          (SELECT string_agg(concat_ws(':',memory_id,confirmed_at,stale_revision),',' ORDER BY memory_id) FROM memory_checks),
          (SELECT string_agg(concat_ws(':',id,review,reviewed_at),',' ORDER BY id) FROM insights),
          (SELECT string_agg(concat_ws(':',id,status,decided_at,note,insight_id),',' ORDER BY id) FROM memory_insight_candidates))
        """))!;

    // The resolve: takes the item off once. Its count drops by exactly `drops`, one audit row is written, and the second call, and any
    // other words given in others, write nothing, say so, and leave the count where it is. Answers the first answer.
    public static async Task<JsonObject> Once(NpgsqlDataSource s, MemoryToolClient ai, string queue, object args, Func<Task<int>> count, int drops = 1, params object[] others)
    {
        var before = await count();
        var audit = await Audit(s);
        var first = await ai.Ok("resolve", args);
        Assert.Equal(queue, (string)first["queue"]!);
        Assert.False((bool)first["unchanged"]!, (string)first["message"]!);
        Assert.Equal(before - drops, await count());
        Assert.Equal(audit + 1, await Audit(s));

        var state = await State(s);
        foreach (var again in others.Prepend(args))
        {
            var second = await ai.Ok("resolve", again);
            Assert.True((bool)second["unchanged"]!, (string)second["message"]!);
            Assert.Equal(queue, (string)second["queue"]!);
            Assert.Empty(second["changed"]!.AsArray());
            Assert.StartsWith("Nothing changed", (string)second["message"]!);
            Assert.Equal(before - drops, await count());
            Assert.Equal(audit + 1, await Audit(s));
            Assert.Equal(state, await State(s));
        }
        return first;
    }

    public static async Task<JsonObject> Brief(MemoryToolClient ai, string? project = null) => await ai.Ok("brief", project is null ? new { } : new { project });
    public static Func<Task<int>> Section(MemoryToolClient ai, string section, string? project = null) => async () => Briefs.Total((await Brief(ai, project))[section]);

    // A candidate the nightly pass would have kept: two weighty memories 90 days apart.
    public static async Task<Guid> Candidate(NpgsqlDataSource s, MemoryStore store, Guid by, string tag, string? scope = null, string subject = "household")
    {
        async Task<Guid> Make(string text, int days)
        {
            var id = (await store.Create(by, new(text, null, scope, Importance: 4), Mcp, default)).Id;
            await Insights.Written(s, id, days);
            return id;
        }
        var older = await Make($"DEMO: the Wendle {tag} ferry runs hourly.", 90);
        var newer = await Make($"DEMO: the Wendle {tag} ferry fares rose.", 0);
        var pair = $$"""{"older":"{{older}}","newer":"{{newer}}","similarity":0.2,"score":1.5,"signals":["weighty"]}""";
        Assert.Equal("""{"added": 1, "known": 0, "refused": 0}""", await MemoryRows.Scalar(s,
            "SELECT memory_insight_accrue(@subject,'recall-meaning-model',@p::jsonb)::text", ("subject", subject), ("p", "[" + pair + "]")));
        return (Guid)(await MemoryRows.Scalar(s, "SELECT id FROM memory_insight_candidates WHERE older_id=@a AND newer_id=@b", ("a", older), ("b", newer)))!;
    }

    public static async Task<int> Candidates(MemoryToolClient ai) => (int)(await ai.Ok("list_insight_candidates", new { n = 1 }))["total"]!;
    public static async Task<int> Standings(MemoryToolClient ai) => (int)(await ai.Ok("list_standing", new { n = 1 }))["total"]!;
}

// Story 12, through the one tool.
public sealed class MemoryResolveStoryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static Guid Id(JsonObject a) => Guid.Parse((string)a["id"]!);

    [PostgresFact] public async Task Story_12_each_item_leaves_the_brief_once_through_resolve_and_leaves_only_what_the_AI_changed()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "resolve-story");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        await using var person = await StreamCalls.As(fixture, who.Sam);

        var commitment = Id(await ai.Ok("remember", new { content = "DEMO: Alex will take the Pellow ladder back to Sam.", owed_to = "Sam", when = Dues.Tomorrow }));
        var fact = Id(await ai.Ok("remember", new { content = "DEMO: the Pellow bakery opens at eight." }));
        await Briefs.Stale(s, fact);
        var insight = await StreamCalls.Save(ai, "DEMO · the Pellow bakery and the ladder");
        Assert.Equal("1 due, 1 you owe, 1 may be out of date, 1 pending insight.", (string)(await Resolves.Brief(ai))["says"]!);

        // Done: one resolve takes it off the due list and the loops together, each by one.
        await Resolves.Once(s, ai, MemoryResolve.Loops, new { id = commitment, action = "done" }, Resolves.Section(ai, "you_owe"));
        // What an AI client resolved is an AI change the person can see and undo, like any other.
        var done = await Resolves.Brief(ai);
        Assert.Equal("1 may be out of date, 1 pending insight, 1 group of AI changes.", (string)done["says"]!);
        Assert.Equal(1, (int)done["changes"]!["changes"]!);

        await Resolves.Once(s, ai, MemoryResolve.Stale, new { id = fact, action = "still_true" }, Resolves.Section(ai, "stale"));
        // A client never reviews what it saved: the person's other client does.
        Assert.Equal("forbidden", (string)(await ai.Refused("resolve", new { id = insight, action = "confirm_insight" }))["code"]!);
        await Resolves.Once(s, person, MemoryResolve.Insights, new { id = insight, action = "confirm_insight", note = "DEMO: Alex says it holds." }, Resolves.Section(ai, "insights"));
        Assert.Equal("1 group of AI changes.", (string)(await Resolves.Brief(ai))["says"]!);
    }
}

// Every queue's actions: each takes its item off once, in its step's words, and a second does nothing and says so.
public sealed class MemoryResolveQueueTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    NpgsqlDataSource S => fixture.Source;
    MemoryStore Store => new(fixture.Source);

    async Task<(MemoryLogins Who, MemoryToolClient Ai)> Start(string tag)
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, tag);
        return (who, await StreamCalls.As(fixture, who.Alex));
    }

    [PostgresFact] public async Task A_loop_is_done_received_or_not_a_task_once()
    {
        var (who, c) = await Start("resolve-loops");
        await using var ai = c;
        var mine = await Dues.Remember(Store, who.Alex, "DEMO: Alex will paint the Pellow gate.", null, ["task"]);
        var first = await Resolves.Once(S, ai, MemoryResolve.Loops, new { id = mine, action = "done" }, Resolves.Section(ai, "you_owe"),
            others: [new { id = mine, action = "not_a_task" }, new { id = mine, action = "received" }]);
        Assert.Equal(["tags"], first["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Contains("Marked done", (string)first["message"]!);
        Assert.Equal((false, "shared", "household"), ((bool)first["still_counted"]!, (string)first["scope"]!, (string)first["owner"]!));
        Assert.Contains("resolved", (string)(await MemoryRows.Scalar(S, "SELECT string_agg(event,',') FROM memory_events WHERE memory_id=@id", ("id", mine)))!);

        var owed = await Dues.Remember(Store, who.Alex, "DEMO: Sam promised Alex the Pellow ladder.", null, direction: MemoryLoops.In, counterparty: "Sam");
        // received is for what is owed to the person, and this is; what they owe is refused it.
        var other = await Dues.Remember(Store, who.Alex, "DEMO: Alex owes Sam the Pellow paint.", null, ["task"]);
        Assert.Equal("invalid", (string)(await ai.Refused("resolve", new { id = other, action = "received" }))["code"]!);
        await Resolves.Once(S, ai, MemoryResolve.Loops, new { id = owed, action = "received" }, Resolves.Section(ai, "owed_to_you"),
            others: [new { id = owed, action = "done" }]);

        // not_a_task takes its task tag off, so done has nothing to close and, as the same word, nothing to do; its date, a queue of
        // its own, stays on the due list.
        var dated = await Dues.Remember(Store, who.Alex, "DEMO: Alex thought of the Pellow gate.", Dues.Tomorrow, ["task"]);
        var due = await Resolves.Section(ai, "due")();
        await Resolves.Once(S, ai, MemoryResolve.Loops, new { id = dated, action = "not_a_task" }, Resolves.Section(ai, "you_owe"),
            others: [new { id = dated, action = "done" }]);
        Assert.Equal(due, await Resolves.Section(ai, "due")());
        // Done closes both of a loop with a date, each by one.
        var both = await Dues.Remember(Store, who.Alex, "DEMO: Alex will post the Pellow form.", Dues.Tomorrow, ["task"]);
        var (dueBefore, oweBefore) = (await Resolves.Section(ai, "due")(), await Resolves.Section(ai, "you_owe")());
        await Resolves.Once(S, ai, MemoryResolve.Loops, new { id = both, action = "done" }, Resolves.Section(ai, "due"));
        Assert.Equal((dueBefore - 1, oweBefore - 1), (await Resolves.Section(ai, "due")(), await Resolves.Section(ai, "you_owe")()));
    }

    [PostgresFact] public async Task A_decision_takes_one_outcome_and_an_unknown_asks_only_when_its_time_comes()
    {
        var (who, c) = await Start("resolve-decisions");
        await using var ai = c;
        foreach (var result in new[] { "right", "wrong", "mixed" })
        {
            var id = await Ledger.Decide(Store, who.Alex, $"DEMO: the household chose the Pellow boiler, {result}.", reviewBy: "2020-01-01");
            var first = await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id, action = "outcome", result }, Resolves.Section(ai, "decisions"),
                others: [new { id, action = "outcome", result = "unknown" }, new { id, action = "outcome", result = result == "right" ? "wrong" : "right" }]);
            Assert.Equal((result, (string?)null, (string?)null), ((string)first["result"]!, (string?)first["previous"], (string?)first["review_at"]));
            // Its own tool still corrects it: that is no resolve.
            var corrected = await ai.Ok("record_outcome", new { id, result = result == "right" ? "wrong" : "right" });
            Assert.False((bool)corrected["unchanged"]!);
        }

        // Unknown: too early to tell. Once, not again until its review date comes, when it waits again; a third stops the asking.
        var open = await Ledger.Decide(Store, who.Alex, "DEMO: the household chose the Pellow tank.", reviewBy: "2020-01-01");
        var asked = await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id = open, action = "outcome", result = "unknown" }, Resolves.Section(ai, "decisions"));
        Assert.NotNull((string?)asked["review_at"]);
        async Task Come() => await MemoryRows.Run(S, """
            UPDATE memory_decision_states SET review_at=now()-interval '1 hour' WHERE memory_id=@id AND revision=(SELECT revision FROM memories WHERE id=@id)
            """, ("id", open));
        await Come();
        var again = await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id = open, action = "outcome", result = "unknown" }, Resolves.Section(ai, "decisions"));
        Assert.NotNull((string?)again["review_at"]);
        await Come();
        var stopped = await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id = open, action = "outcome", result = "unknown" }, Resolves.Section(ai, "decisions"));
        Assert.Null((string?)stopped["review_at"]);

        // One not yet up for review is on the open list: an outcome takes it off that, and the brief, which never counted it, is as it was.
        var early = await Ledger.Decide(Store, who.Alex, "DEMO: the household chose the Pellow canopy.");
        var section = await Resolves.Section(ai, "decisions")();
        await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id = early, action = "outcome", result = "right" },
            async () => (int)(await ai.Ok("list_decisions", new { state = "open", n = 1 }))["total"]!);
        Assert.Equal(section, await Resolves.Section(ai, "decisions")());

        // A memory that is no decision is not waiting.
        var plain = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow boiler is green.", null);
        Assert.True((bool)(await ai.Ok("resolve", new { id = plain, action = "outcome", result = "right" }))["unchanged"]!);
    }

    [PostgresFact] public async Task A_date_is_snoozed_or_cleared_once_and_the_answer_says_when_the_brief_still_counts_it()
    {
        var (who, c) = await Start("resolve-due");
        await using var ai = c;
        // Past the 48 hours: it leaves the brief. A second snooze, to that moment or another, does nothing; resolve_due still moves it.
        var far = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow gas bill.", Dues.Day(-1));
        var first = await Resolves.Once(S, ai, MemoryResolve.DueList, new { id = far, action = "snooze", until = Dues.Day(9) }, Resolves.Section(ai, "due"),
            others: [new { id = far, action = "snooze", until = Dues.Day(20) }]);
        Assert.Equal((false, false), ((bool)first["still_counted"]!, (bool)first["newly_counted"]!));
        Assert.NotNull(first["due"]);
        Assert.NotNull(first["before"]);
        Assert.False((bool)(await ai.Ok("resolve_due", new { id = far, action = "snooze", until = Dues.Day(30) }))["unchanged"]!);

        // Within the 48 hours: it moves from overdue to upcoming, so the brief's total stays and its overdue drops by one. The answer
        // says so, and it is resolved all the same: a second does nothing.
        var soon = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow water bill.", Dues.Day(-1));
        var overdue = async () => (int)(await Resolves.Brief(ai))["due"]!["overdue"]!;
        var total = await Resolves.Section(ai, "due")();
        var near = await Resolves.Once(S, ai, MemoryResolve.DueList, new { id = soon, action = "snooze", until = Dues.Tomorrow }, overdue,
            others: [new { id = soon, action = "snooze", until = Dues.Day(9) }]);
        Assert.Equal((true, false), ((bool)near["still_counted"]!, (bool)near["newly_counted"]!));
        Assert.Contains("within 48 hours, so the brief still counts it", (string)near["message"]!);
        Assert.Equal(total, await Resolves.Section(ai, "due")());

        // From past the 48 hours into them: the brief never counted it, so this is the one resolve that raises a count, and the answer
        // says it comes in, never that it stays. A second does nothing, and the count stays where the first put it.
        var coming = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow coal bill.", Dues.Day(30));
        var upcoming = async () => (int)(await Resolves.Brief(ai))["due"]!["upcoming"]!;
        var (wasTotal, wasUpcoming) = (await Resolves.Section(ai, "due")(), await upcoming());
        var entered = await ai.Ok("resolve", new { id = coming, action = "snooze", until = Dues.Tomorrow });
        Assert.Equal((false, false, true), ((bool)entered["unchanged"]!, (bool)entered["still_counted"]!, (bool)entered["newly_counted"]!));
        Assert.Contains("within 48 hours, so the brief now counts it", (string)entered["message"]!);
        Assert.DoesNotContain("still counts", (string)entered["message"]!);
        Assert.Equal((wasTotal + 1, wasUpcoming + 1), (await Resolves.Section(ai, "due")(), await upcoming()));
        var enteredAgain = await ai.Ok("resolve", new { id = coming, action = "snooze", until = Dues.Day(9) });
        Assert.Equal((true, false), ((bool)enteredAgain["unchanged"]!, (bool)enteredAgain["newly_counted"]!));
        Assert.Equal((wasTotal + 1, wasUpcoming + 1), (await Resolves.Section(ai, "due")(), await upcoming()));

        // Within the 48 hours and staying there: counted before and after. Past them and staying there: counted neither time.
        var close = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow oil bill.", Dues.Tomorrow);
        (wasTotal, wasUpcoming) = (await Resolves.Section(ai, "due")(), await upcoming());
        var stays = await ai.Ok("resolve", new { id = close, action = "snooze", until = Dues.Day(1) + "T13:00" });
        Assert.Equal((false, true, false), ((bool)stays["unchanged"]!, (bool)stays["still_counted"]!, (bool)stays["newly_counted"]!));
        Assert.Contains("within 48 hours, so the brief still counts it", (string)stays["message"]!);
        Assert.Equal((wasTotal, wasUpcoming), (await Resolves.Section(ai, "due")(), await upcoming()));
        var distant = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow wood bill.", Dues.Day(30));
        var later = await ai.Ok("resolve", new { id = distant, action = "snooze", until = Dues.Day(40) });
        Assert.Equal((false, false, false), ((bool)later["unchanged"]!, (bool)later["still_counted"]!, (bool)later["newly_counted"]!));
        Assert.DoesNotContain("48 hours", (string)later["message"]!);
        Assert.Equal((wasTotal, wasUpcoming), (await Resolves.Section(ai, "due")(), await upcoming()));

        // When the moment comes it is due again, and may be put off again.
        await MemoryRows.Run(S, "UPDATE memories SET due_at=now()-interval '1 hour' WHERE id=@id", ("id", soon));
        await Resolves.Once(S, ai, MemoryResolve.DueList, new { id = soon, action = "snooze", until = Dues.Day(9) }, Resolves.Section(ai, "due"));

        // A clear ends it, snoozed or not: a snooze puts a date off, and the count drops once whichever comes.
        var cleared = await Resolves.Once(S, ai, MemoryResolve.DueList, new { id = soon, action = "clear_date" }, async () => (int)(await ai.Ok("list_due", new { n = 1 }))["total"]!,
            others: [new { id = soon, action = "snooze", until = Dues.Day(3) }]);
        Assert.Null(cleared["due"]);
        var plain = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow water rates.", Dues.Tomorrow);
        await Resolves.Once(S, ai, MemoryResolve.DueList, new { id = plain, action = "clear_date" }, Resolves.Section(ai, "due"));

        // An undone snooze is no snooze in force: its date is back, and it waits.
        var undone = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow rates.", Dues.Day(-1));
        await ai.Ok("resolve", new { id = undone, action = "snooze", until = Dues.Day(9) });
        await ai.Ok("undo", new { id = undone });
        Assert.False((bool)(await ai.Ok("resolve", new { id = undone, action = "snooze", until = Dues.Day(9) }))["unchanged"]!);
    }

    [PostgresFact] public async Task A_stale_fact_is_still_true_once_even_a_day_later()
    {
        var (who, c) = await Start("resolve-stale");
        await using var ai = c;
        var fact = await Dues.Remember(Store, who.Alex, "DEMO: Pellow bins go out on Monday.", null);
        await Briefs.Stale(S, fact);
        var revision = await MemoryRows.Scalar(S, "SELECT revision FROM memories WHERE id=@id", ("id", fact));
        var first = await Resolves.Once(S, ai, MemoryResolve.Stale, new { id = fact, action = "still_true" }, Resolves.Section(ai, "stale"));
        Assert.Equal(["checked"], first["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(revision, await MemoryRows.Scalar(S, "SELECT revision FROM memories WHERE id=@id", ("id", fact)));
        // A day on, the step alone would move the clock again; the queue's item left once, so a resolve does not. still_true still does.
        await MemoryRows.Run(S, "UPDATE memory_checks SET confirmed_at=now()-interval '2 days' WHERE memory_id=@id", ("id", fact));
        var state = await Resolves.State(S);
        Assert.True((bool)(await ai.Ok("resolve", new { id = fact, action = "still_true" }))["unchanged"]!);
        Assert.Equal(state, await Resolves.State(S));
        Assert.False((bool)(await ai.Ok("still_true", new { id = fact }))["unchanged"]!);
        // Marked again by the nightly pass, it is a new item.
        await MemoryRows.Run(S, "UPDATE memory_checks SET stale_at=now(),stale_revision=@r,stale_since=now()-interval '100 days' WHERE memory_id=@id", ("id", fact), ("r", revision));
        await Resolves.Once(S, ai, MemoryResolve.Stale, new { id = fact, action = "still_true" }, Resolves.Section(ai, "stale"));
        // So is one a retracted source flagged: its flags come off with the one resolve.
        var source = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow rota said Monday.", null);
        await MemoryRows.Run(S, "INSERT INTO memory_retraction_flags(memory_id,retracted_id) VALUES(@id,@source)", ("id", fact), ("source", source));
        var flagged = await Resolves.Once(S, ai, MemoryResolve.Stale, new { id = fact, action = "still_true" }, Resolves.Section(ai, "stale"));
        Assert.Contains("no longer flag it", (string)flagged["message"]!);
        Assert.Equal(0L, await MemoryRows.Scalar(S, "SELECT count(*) FROM memory_retraction_flags WHERE memory_id=@id", ("id", fact)));
    }

    [PostgresFact] public async Task A_standing_instruction_stops_once()
    {
        var (who, c) = await Start("resolve-standing");
        await using var ai = c;
        await Standing.Clean(S);
        var instruction = await Standing.Set(Store, who.Alex, "DEMO: When the Pellow fair comes up, book the hall first.", project: "fair");
        await Resolves.Once(S, ai, MemoryResolve.Standing, new { id = instruction, action = "stop_standing" }, () => Resolves.Standings(ai));
        var again = await Standing.Set(Store, who.Alex, "DEMO: When the Pellow fair comes up, hire the marquee too.", project: "fair");
        var first = await Resolves.Once(S, ai, MemoryResolve.Standing, new { id = again, action = "stop_standing" }, Resolves.Section(ai, "standing", "fair"));
        Assert.Equal(["standing"], first["changed"]!.AsArray().Select(c => (string)c!));
        // A memory that never was one has nothing to stop.
        var plain = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow fair is in June.", null);
        Assert.True((bool)(await ai.Ok("resolve", new { id = plain, action = "stop_standing" }))["unchanged"]!);

        // One whose time has ended is on no list and fires for nobody, so it does not wait: nothing is written and the list's count,
        // which never held it, stays. Its own tool still takes off its mark, as what is no resolve.
        var ended = await Standing.Set(Store, who.Alex, "DEMO: When the Pellow fair comes up, return the old urn.");
        await ai.Ok("update", new { id = ended, valid_from = "2020-01-01T00:00:00Z", valid_until = "2020-06-01T00:00:00Z" });
        var (standings, audit, state) = (await Resolves.Standings(ai), await Resolves.Audit(S), await Resolves.State(S));
        var over = await ai.Ok("resolve", new { id = ended, action = "stop_standing" });
        Assert.True((bool)over["unchanged"]!, (string)over["message"]!);
        Assert.Empty(over["changed"]!.AsArray());
        Assert.Contains("its time has ended", (string)over["message"]!);
        Assert.Equal((standings, audit, state), (await Resolves.Standings(ai), await Resolves.Audit(S), await Resolves.State(S)));
        Assert.False((bool)(await ai.Ok("stop_standing", new { id = ended }))["unchanged"]!);
    }

    [PostgresFact] public async Task A_copy_is_kept_once_and_a_pair_also_queued_stays_for_the_person()
    {
        var (who, c) = await Start("resolve-copies");
        await using var ai = c;
        // The same words: a copy, and no candidate, since they can't contradict each other.
        var w = new ContradictionStory(61);
        var checking = new MemoryStore(S) { Check = new DuplicateCheck(w, 0.85, offer: 0.5) };
        var words = "DEMO: the Pellow copy is the same words.";
        var original = await checking.Create(who.Alex, new(words), Resolves.Mcp, default);
        var copy = await checking.Create(who.Alex, new(words), Resolves.Mcp, default);
        var id = copy.Id;
        Assert.Equal([original.Id], copy.Duplicates!.Select(d => d.Id));
        var first = await Resolves.Once(S, ai, MemoryResolve.Duplicates, new { id, action = "keep_copy" }, Resolves.Section(ai, "duplicates"));
        Assert.Equal(["duplicate_of"], first["changed"]!.AsArray().Select(c => (string)c!));
        Assert.Contains("list_contradictions", (string)first["message"]!);

        // A pair a person must settle: an AI client's keep_copy moves it to their review, unsettled, and goes no further.
        var earlier = await checking.Create(who.Alex, new(w.Tuesday), Resolves.Mcp, default);
        var close = await checking.Create(who.Alex, new(w.Wednesday), Resolves.Mcp, default);
        Assert.Equal([earlier.Id], close.Duplicates!.Select(d => d.Id));
        var contradictions = await Resolves.Section(ai, "contradictions")();
        await Resolves.Once(S, ai, MemoryResolve.Duplicates, new { id = close.Id, action = "keep_copy" }, Resolves.Section(ai, "duplicates"));
        Assert.Equal(contradictions + 1, await Resolves.Section(ai, "contradictions")());
        Assert.Null(await MemoryRows.Scalar(S, "SELECT settled_at FROM memory_contradictions WHERE memory_id=@id", ("id", close.Id)));

        // Past the three a review shows, a fourth flag stands once they come off: the answer says it stays, and it waits again.
        var many = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow memory with four flags.", null);
        var named = new List<Guid>();
        for (var n = 0; n < 4; n++) named.Add(await Dues.Remember(Store, who.Alex, $"DEMO: the Pellow copy {n}.", null));
        await MemoryRows.Run(S, """
            INSERT INTO memory_duplicate_flags(memory_id,copy_id,score,model,threshold) SELECT @id,x,0.9,'recall-meaning-model',0.85 FROM unnest(@copies) x
            """, ("id", many), ("copies", named.ToArray()));
        var kept = await ai.Ok("resolve", new { id = many, action = "keep_copy" });
        Assert.Equal((false, true), ((bool)kept["unchanged"]!, (bool)kept["still_counted"]!));
        Assert.Contains("stays on list_duplicates", (string)kept["message"]!);
        var last = await ai.Ok("resolve", new { id = many, action = "keep_copy" });
        Assert.Equal((false, false), ((bool)last["unchanged"]!, (bool)last["still_counted"]!));
        Assert.True((bool)(await ai.Ok("resolve", new { id = many, action = "keep_copy" }))["unchanged"]!);
    }

    [PostgresFact] public async Task A_candidate_is_decided_once_and_stays_as_it_was_decided()
    {
        var (who, c) = await Start("resolve-candidates");
        await using var ai = c;
        var rejected = await Resolves.Candidate(S, Store, who.Alex, "reject");
        var memories = await Insights.Memories(S);
        var first = await Resolves.Once(S, ai, MemoryResolve.Candidates, new { id = rejected, action = "reject_candidate", note = " DEMO: together they say nothing new. " },
            () => Resolves.Candidates(ai), others: [new { id = rejected, action = "use_candidate" }, new { id = rejected, action = "reject_candidate", note = "DEMO: another reason." }]);
        Assert.Equal(("rejected", "DEMO: together they say nothing new.", MemoryTools.StoredNotice), ((string)first["decision"]!, (string)first["note"]!, (string)first["notice"]!));
        // A repeat answers what was kept, and no memory changed.
        var repeat = await ai.Ok("resolve", new { id = rejected, action = "use_candidate" });
        Assert.Equal(("rejected", "DEMO: together they say nothing new."), ((string)repeat["decision"]!, (string)repeat["note"]!));
        Assert.Equal(memories, await Insights.Memories(S));

        var used = await Resolves.Candidate(S, Store, who.Alex, "use");
        var (older, newer) = ((Guid)(await MemoryRows.Scalar(S, "SELECT older_id FROM memory_insight_candidates WHERE id=@id", ("id", used)))!,
            (Guid)(await MemoryRows.Scalar(S, "SELECT newer_id FROM memory_insight_candidates WHERE id=@id", ("id", used)))!);
        var insight = await StreamCalls.Save(ai, "DEMO · the Wendle ferry, its hours and its fares", older, newer);
        var written = await Resolves.Once(S, ai, MemoryResolve.Candidates, new { id = used, action = "use_candidate", insight_id = insight }, () => Resolves.Candidates(ai),
            others: [new { id = used, action = "reject_candidate", note = "DEMO: changed my mind." }]);
        Assert.Equal(("used", insight), ((string)written["decision"]!, Guid.Parse((string)written["insight_id"]!)));
        Assert.Equal("unknown", (string)(await ai.Refused("resolve", new { id = Guid.NewGuid(), action = "reject_candidate" }))["code"]!);
    }

    [PostgresFact] public async Task An_insight_is_confirmed_or_dismissed_once_and_a_person_who_closed_it_is_not_overruled()
    {
        var (who, c) = await Start("resolve-insights");
        await using var ai = c;
        await using var person = await StreamCalls.As(fixture, who.Sam);
        var confirmed = await StreamCalls.Save(ai, "DEMO · a Pellow insight that holds");
        var first = await Resolves.Once(S, person, MemoryResolve.Insights, new { id = confirmed, action = "confirm_insight", note = "DEMO: Alex says it holds." }, Resolves.Section(ai, "insights"),
            others: [new { id = confirmed, action = "dismiss_insight" }]);
        Assert.Equal(("confirmed", "confirmed"), ((string)first["review"]!, (string)first["view"]!));
        var repeat = await person.Ok("resolve", new { id = confirmed, action = "dismiss_insight" });
        Assert.Equal(("confirmed", "confirmed"), ((string)repeat["review"]!, (string)repeat["view"]!));
        Assert.Contains("reopen", (string)repeat["message"]!);

        var dismissed = await StreamCalls.Save(ai, "DEMO · a Pellow insight that does not");
        var gone = await Resolves.Once(S, person, MemoryResolve.Insights, new { id = dismissed, action = "dismiss_insight" }, Resolves.Section(ai, "insights"),
            others: [new { id = dismissed, action = "confirm_insight" }]);
        Assert.Equal(("dismissed", "closed"), ((string)gone["review"]!, (string)gone["view"]!));

        // A person's not useful closed it with no review: a resolve leaves it closed, and says so.
        var closed = await StreamCalls.Save(ai, "DEMO · a Pellow insight a person closed");
        await MemoryRows.Run(S, "UPDATE insights SET status='dismissed' WHERE id=@id", ("id", closed));
        var state = await Resolves.State(S);
        var left = await person.Ok("resolve", new { id = closed, action = "confirm_insight" });
        Assert.Equal((true, "proposed", "closed"), ((bool)left["unchanged"]!, (string)left["review"]!, (string)left["view"]!));
        Assert.Contains("not useful or handled", (string)left["message"]!);
        Assert.Equal(state, await Resolves.State(S));

        // A snoozed one waits, and its snooze still holds it back.
        var snoozed = await StreamCalls.Save(ai, "DEMO · a Pellow insight for later");
        var until = DateTimeOffset.UtcNow.AddDays(3);
        await MemoryRows.Run(S, "UPDATE insights SET status='snoozed',feedback=jsonb_build_object('value','later','until',@u::text,'at',now()) WHERE id=@id", ("id", snoozed), ("u", until.ToString("o")));
        var held = await person.Ok("resolve", new { id = snoozed, action = "confirm_insight" });
        Assert.Equal((false, "confirmed", "snoozed"), ((bool)held["unchanged"]!, (string)held["review"]!, (string)held["view"]!));

        // A replaced one is reviewed through what replaced it, as review_insight says.
        var replaced = await StreamCalls.Save(ai, "DEMO · a Pellow insight replaced");
        await MemoryRows.Run(S, "UPDATE insights SET status='superseded' WHERE id=@id", ("id", replaced));
        Assert.Equal("invalid", (string)(await person.Refused("resolve", new { id = replaced, action = "confirm_insight" }))["code"]!);
        Assert.Equal("unknown", (string)(await person.Refused("resolve", new { id = Guid.NewGuid(), action = "dismiss_insight" }))["code"]!);
    }

    // Undo brings an item back to its queue, so a resolve acts again, once. A still true and a kept copy move no revision, so there
    // is nothing of theirs for undo to take back; a candidate's decision stays and a review is taken back by reopen.
    [PostgresFact] public async Task An_undo_puts_an_item_back_and_it_can_be_resolved_once_more()
    {
        var (who, c) = await Start("resolve-undo");
        await using var ai = c;
        var loop = await Dues.Remember(Store, who.Alex, "DEMO: Alex will sweep the Pellow yard.", null, ["task"]);
        var owe = Resolves.Section(ai, "you_owe");
        await Resolves.Once(S, ai, MemoryResolve.Loops, new { id = loop, action = "done" }, owe);
        var before = await owe();
        await ai.Ok("undo", new { id = loop });
        Assert.Equal(before + 1, await owe());
        await Resolves.Once(S, ai, MemoryResolve.Loops, new { id = loop, action = "not_a_task" }, owe);

        var decision = await Ledger.Decide(Store, who.Alex, "DEMO: the household chose the Pellow bench.", reviewBy: "2020-01-01");
        var decisions = Resolves.Section(ai, "decisions");
        await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id = decision, action = "outcome", result = "right" }, decisions);
        await ai.Ok("undo", new { id = decision });
        await Resolves.Once(S, ai, MemoryResolve.Decisions, new { id = decision, action = "outcome", result = "wrong" }, decisions);

        await Standing.Clean(S);
        var standing = await Standing.Set(Store, who.Alex, "DEMO: When the Pellow bench comes up, mention the varnish.");
        await Resolves.Once(S, ai, MemoryResolve.Standing, new { id = standing, action = "stop_standing" }, () => Resolves.Standings(ai));
        await ai.Ok("undo", new { id = standing });
        await Resolves.Once(S, ai, MemoryResolve.Standing, new { id = standing, action = "stop_standing" }, () => Resolves.Standings(ai));

        // reopen takes a review back: the insight waits again.
        await using var person = await StreamCalls.As(fixture, who.Sam);
        var insight = await StreamCalls.Save(ai, "DEMO · the Pellow bench");
        var insights = Resolves.Section(ai, "insights");
        await Resolves.Once(S, person, MemoryResolve.Insights, new { id = insight, action = "confirm_insight" }, insights);
        await person.Ok("review_insight", new { id = insight, action = "reopen" });
        await Resolves.Once(S, person, MemoryResolve.Insights, new { id = insight, action = "dismiss_insight" }, insights);

        // Every memory action's audit row names who, by which channel: an AI client's.
        Assert.Equal(0L, await MemoryRows.Scalar(S, """
            SELECT count(*) FROM memory_events WHERE memory_id IN (@a,@b,@c) AND event IN ('resolved','outcome','standing_stopped') AND (channel<>'mcp' OR principal_id<>@p)
            """, ("a", loop), ("b", decision), ("c", standing), ("p", who.Alex)));
    }

    // Two at once, in different words: one takes the item off, and the other finds it gone, whichever comes first.
    [PostgresFact] public async Task Two_resolves_at_once_take_an_item_off_once()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "resolve-race");
        var insights = new InsightStore(S);
        async Task<MemoryResolved[]> Race(params MemoryResolveAsk[] asks) =>
            await Task.WhenAll(asks.Select(a => Task.Run(() => Store.ResolveItem(who.Alex, a, insights, Resolves.Mcp, default))));
        for (var round = 0; round < 5; round++)
        {
            var loop = await Dues.Remember(Store, who.Alex, $"DEMO: Alex will rake the Pellow lawn, round {round}.", null, ["task"]);
            var audit = await Resolves.Audit(S);
            Assert.Single(await Race(new(loop, MemoryLoops.Done), new(loop, MemoryLoops.NotATask)), r => !r.Unchanged);
            Assert.Equal(audit + 1, await Resolves.Audit(S));

            var decision = await Ledger.Decide(Store, who.Alex, $"DEMO: the household chose the Pellow rake, round {round}.", reviewBy: "2020-01-01");
            audit = await Resolves.Audit(S);
            Assert.Single(await Race(new(decision, MemoryResolve.Outcome, Result: "right"), new(decision, MemoryResolve.Outcome, Result: "wrong")), r => !r.Unchanged);
            Assert.Equal(audit + 1, await Resolves.Audit(S));

            var dated = await Dues.Remember(Store, who.Alex, $"DEMO: the Pellow rake's bill, round {round}.", Dues.Day(-1));
            audit = await Resolves.Audit(S);
            Assert.Single(await Race(new(dated, DueDates.Snooze, Until: Dues.Day(9)), new(dated, DueDates.Snooze, Until: Dues.Day(10))), r => !r.Unchanged);
            Assert.Equal(audit + 1, await Resolves.Audit(S));

            var candidate = await Resolves.Candidate(S, Store, who.Alex, $"race{round}");
            audit = await Resolves.Audit(S);
            Assert.Single(await Race(new(candidate, MemoryResolve.UseCandidate), new(candidate, MemoryResolve.RejectCandidate)), r => !r.Unchanged);
            Assert.Equal(audit + 1, await Resolves.Audit(S));

            var insight = await insights.Save(who.Sam, StreamCalls.Draft($"DEMO · the Pellow rake, round {round}"), default);
            audit = await Resolves.Audit(S);
            Assert.Single(await Race(new(insight, MemoryResolve.ConfirmInsight), new(insight, MemoryResolve.DismissInsight)), r => !r.Unchanged);
            Assert.Equal(audit + 1, await Resolves.Audit(S));
        }
    }

    [PostgresFact] public async Task A_stale_expected_revision_and_a_wrong_argument_change_nothing()
    {
        var (who, c) = await Start("resolve-args");
        await using var ai = c;
        var loop = await Dues.Remember(Store, who.Alex, "DEMO: Alex will oil the Pellow hinge.", null, ["task"]);
        var state = await Resolves.State(S);
        var stale = await ai.Refused("resolve", new { id = loop, action = "done", expected_revision = 9 });
        Assert.Equal(("conflict", 1), ((string)stale["code"]!, (int)stale["revision"]!));
        Assert.Equal(state, await Resolves.State(S));
        // An unknown id answers as one in the trash does.
        Assert.Equal("unknown", (string)(await ai.Refused("resolve", new { id = Guid.NewGuid(), action = "done" }))["code"]!);
        var trashed = await Dues.Remember(Store, who.Alex, "DEMO: Alex will oil the Pellow latch.", null, ["task"]);
        var trip = await ai.Ok("forget", new { id = trashed });
        Assert.Equal("unknown", (string)(await ai.Refused("resolve", new { id = trashed, action = "done" }))["code"]!);
        await ai.Ok("undo", new { id = trashed, nonce = (string)trip["trash"]!["nonce"]! });
        // A memory marked wrong is in no review: the steps' refusal.
        var wrong = await Dues.Remember(Store, who.Alex, "DEMO: Alex will oil the Pellow bolt.", null, ["task"]);
        await ai.Ok("set_status", new { id = wrong, status = "deprecated" });
        Assert.Equal("retracted", (string)(await ai.Refused("resolve", new { id = wrong, action = "done" }))["code"]!);
        Assert.NotEmpty((await ai.Ok("resolve", new { id = loop, action = "done", expected_revision = 1 }))["changed"]!.AsArray());
        // A snooze still to come: a moment gone is refused, and nothing moved.
        var dated = await Dues.Remember(Store, who.Alex, "DEMO: the Pellow hinge's bill.", Dues.Tomorrow);
        var (audit, before) = (await Resolves.Audit(S), await Resolves.State(S));
        Assert.Equal("invalid", (string)(await ai.Refused("resolve", new { id = dated, action = "snooze", until = "2020-01-01" }))["code"]!);
        Assert.Equal((audit, before), (await Resolves.Audit(S), await Resolves.State(S)));
    }

    // The migration's functions: no definer without its checks.
    [PostgresFact] public async Task The_functions_refuse_a_login_of_no_role_and_a_word_of_no_queue()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "resolve-fn");
        var loop = await Dues.Remember(Store, who.Alex, "DEMO: Alex will fix the Pellow step.", null, ["task"]);
        var reader = await Scoped.Principal(S, "resolve-fn-reader", "system");
        Assert.Equal("42501", await MemoryRows.Fails(S, "SELECT resolve_memory_item(@p,@id,'done',NULL,NULL,NULL,'mcp')", ("p", reader), ("id", loop)));
        Assert.Equal("42501", await MemoryRows.Fails(S, "SELECT resolve_insight_review(@p,@id,'confirm',NULL,'mcp',NULL,NULL)", ("p", reader), ("id", loop)));
        Assert.Equal("42501", await MemoryRows.Fails(S, "SELECT resolve_insight_candidate_once(@p,@id,'use',NULL,NULL,'mcp',NULL,NULL)", ("p", reader), ("id", loop)));
        foreach (var word in new[] { "release", "settle_keep_both", "settle_replace", "use_candidate", "maybe" })
            Assert.Equal("22023", await MemoryRows.Fails(S, "SELECT resolve_memory_item(@p,@id,@w,NULL,NULL,NULL,'mcp')", ("p", who.Alex), ("id", loop), ("w", word)));
        Assert.Equal("22023", await MemoryRows.Fails(S, "SELECT resolve_insight_review(@p,@id,'reopen',NULL,'mcp',NULL,NULL)", ("p", who.Alex), ("id", loop)));
        Assert.Equal("22023", await MemoryRows.Fails(S, "SELECT resolve_insight_candidate_once(@p,@id,'maybe',NULL,NULL,'mcp',NULL,NULL)", ("p", who.Alex), ("id", loop)));
        // Each is pinned and closed to everyone but its owner and the logins the engine grants.
        foreach (var name in new[] { "memory_snooze_in_force", "memory_resolve_waiting", "memory_resolve_counted", "resolve_memory_item", "resolve_insight_review", "resolve_insight_candidate_once" })
        {
            Assert.Contains("search_path=", (string)(await MemoryRows.Scalar(S, "SELECT array_to_string(proconfig,',') FROM pg_proc WHERE proname=@n", ("n", name)))!);
            Assert.False((bool)(await MemoryRows.Scalar(S, "SELECT has_function_privilege('public',p.oid,'EXECUTE') FROM pg_proc p WHERE proname=@n", ("n", name)))!);
        }
    }
}

// A person's steps: an AI principal never reaches one, by the tool or any channel, and a person's channel takes each once.
public sealed class MemoryResolvePersonTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    NpgsqlDataSource S => fixture.Source;
    MemoryStore Store => new(fixture.Source);
    MemoryStore Checking(ContradictionStory w) => new(fixture.Source) { Check = new DuplicateCheck(w, 0.85, offer: 0.5) };

    [PostgresFact] public async Task Person_only_steps_are_refused_to_AI_principals_from_every_channel()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "resolve-ai");
        var held = (await Store.Create(who.Alex, new(Briefs.Held), Resolves.Mcp, default)).Id;
        var w = new ContradictionStory(62);
        var store = Checking(w);
        var first = (await store.Create(who.Alex, new(w.Tuesday), Resolves.Mcp, default)).Id;
        var second = (await store.Create(who.Alex, new(w.Thursday), Resolves.Mcp, default)).Id;
        var insights = new InsightStore(S);
        var (audit, state) = (await Resolves.Audit(S), await Resolves.State(S));
        var queued = await MemoryRows.Scalar(S, "SELECT count(*) FROM memory_contradictions WHERE settled_at IS NULL");

        foreach (var ai in new[] { who.Alex, who.Sam, who.House })
        {
            // By the tool: needs_person, the same whatever the id names, a held memory, a queued pair, one in no queue, one of no memory.
            await using var client = await StreamCalls.As(fixture, ai);
            var answers = new HashSet<string>();
            foreach (var action in new[] { "release", "settle_keep_both", "settle_replace" })
                foreach (var id in new[] { held, first, second, Guid.NewGuid() })
                {
                    var refused = await client.Refused("resolve", new { id, action });
                    Assert.Equal("needs_person", (string)refused["code"]!);
                    answers.Add(refused.ToJsonString());
                    // A channel named by the call is refused before anything is read.
                    Assert.Equal("invalid", (string)(await client.Refused("resolve", new { id, action, channel = "app" }))["code"]!);
                    Assert.Equal("invalid", (string)(await client.Refused("resolve", new { id, action, client = "app" }))["code"]!);
                }
            Assert.Single(answers);

            // By the store, naming every channel there is: a person's by name, an AI client's, and none that exists.
            foreach (var channel in new[] { "app", "cli", "system", "capture", "Mcp", "" })
            {
                var call = new MemoryCall(channel, "DEMO client");
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.ResolveItem(ai, new(held, MemoryResolve.Release), insights, call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.ResolveItem(ai, new(second, MemoryResolve.SettleKeepBoth, Conflict: first), insights, call, default)));
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.ResolveItem(ai, new(second, MemoryResolve.SettleReplace, Conflict: first), insights, call, default)));
            }
            Assert.Equal("needs_person", await MemoryLogins.Refused(() => Store.ResolveItem(ai, new(held, MemoryResolve.Release), insights, Resolves.Mcp, default)));
            Assert.Equal("needs_person", await MemoryLogins.Refused(() => Store.ResolveItem(ai, new(second, MemoryResolve.SettleKeepBoth, Conflict: first), insights, Resolves.Mcp, default)));

            // The step's own functions, called through the channel an AI client's login is allowed.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Release(ai, held, null, Resolves.Mcp, default)));
            Assert.Equal("needs_person", await MemoryLogins.Refused(() => Store.Settle(ai, second, first, MemoryContradictions.KeepBoth, null, null, Resolves.Mcp, default)));

            // The app's door, if an AI client's login ever reached it, writes as the app, which the database refuses it.
            foreach (var step in new object[] { new { op = "release", id = held }, new { op = "release_all", memories = new[] { new { id = held, expected = 1 } } },
                new { op = "settle_contradiction", id = second, target = first, decision = "keep_both" }, new { op = "settle_contradiction", id = second, target = first, decision = "replace" } })
            {
                var (status, content) = await AppMemoryCalls.Call(AppMemoryCalls.Services(MemoryToolSettings.On, fixture.Db), ai, AppMemoryCalls.Body(AppEndpoint.Memories, step),
                    new AppHealth(fixture.Db, MemoryToolSettings.On));
                Assert.Equal(200, status);
                Assert.Equal((false, "invalid"), ((bool)content!["ok"]!, (string)content["code"]!));
            }
        }
        Assert.Equal((audit, state), (await Resolves.Audit(S), await Resolves.State(S)));
        Assert.Equal(MemoryHolds.Instruction, (await Store.Get(who.Alex, held, default))!.Hold);
        Assert.Equal(queued, await MemoryRows.Scalar(S, "SELECT count(*) FROM memory_contradictions WHERE settled_at IS NULL"));
    }

    // Nothing else in the tool lifts a hold or settles a pair: each action on a held memory is refused, without its words.
    [PostgresFact] public async Task No_action_on_a_held_memory_lifts_its_hold_or_says_its_words()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "resolve-held");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        var held = (await Store.Create(who.Alex, new(Briefs.Held + " #task"), Resolves.Mcp, default)).Id;
        var state = await Resolves.State(S);
        object[] calls =
        [
            new { id = held, action = "still_true" }, new { id = held, action = "done" }, new { id = held, action = "received" }, new { id = held, action = "not_a_task" },
            new { id = held, action = "outcome", result = "right" }, new { id = held, action = "snooze", until = Dues.Day(3) }, new { id = held, action = "clear_date" },
            new { id = held, action = "stop_standing" }, new { id = held, action = "keep_copy" },
        ];
        foreach (var call in calls)
        {
            var refused = await ai.Refused("resolve", call);
            Assert.Equal("held", (string)refused["code"]!);
            Assert.DoesNotContain("ignore all previous", refused.ToJsonString());
        }
        Assert.Equal(state, await Resolves.State(S));
        Assert.Equal(MemoryHolds.Instruction, (await Store.Get(who.Alex, held, default))!.Hold);
    }

    // A person's channels take the same steps once, the app's login and the terminal's. An AI client's brief drops by one.
    [PostgresFact] public async Task A_person_takes_each_of_their_steps_once()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "resolve-person");
        var at = await MemoryLogins.AtTerminal(S, who.Alex);
        var insights = new InsightStore(S);
        await using var ai = await StreamCalls.As(fixture, who.Alex);

        foreach (var (login, call, tag) in new[] { (who.App, Resolves.App, "app"), (at, Resolves.Cli, "cli") })
        {
            var held = (await Store.Create(who.Alex, new($"{Briefs.Held} {tag}"), Resolves.Mcp, default)).Id;
            var audit = await Resolves.Audit(S);
            var released = await Store.ResolveItem(login, new(held, MemoryResolve.Release), insights, call, default);
            Assert.Equal((MemoryResolve.Held, false, true), (released.Queue, released.Unchanged, released.Waiting));
            Assert.Null((await Store.Get(who.Alex, held, default))!.Hold);
            Assert.Equal(audit + 1, await Resolves.Audit(S));
            var again = await Store.ResolveItem(login, new(held, MemoryResolve.Release), insights, call, default);
            Assert.Equal((true, false), (again.Unchanged, again.Waiting));
            Assert.Equal(audit + 1, await Resolves.Audit(S));

            var w = new ContradictionStory(tag == "app" ? 63 : 64);
            var store = Checking(w);
            var a = (await store.Create(who.Alex, new(w.Tuesday), Resolves.Mcp, default)).Id;
            var b = (await store.Create(who.Alex, new(w.Thursday), Resolves.Mcp, default)).Id;
            var count = Resolves.Section(ai, "contradictions");
            var before = await count();
            var settled = await Store.ResolveItem(login, new(b, MemoryResolve.SettleKeepBoth, Conflict: a), insights, call, default);
            Assert.Equal((MemoryResolve.Contradictions, false, MemoryContradictions.KeepBoth), (settled.Queue, settled.Unchanged, settled.Settled!.Decision));
            Assert.Equal(before - 1, await count());
            var repeat = await Store.ResolveItem(login, new(b, MemoryResolve.SettleReplace, Conflict: a), insights, call, default);
            Assert.Equal((true, MemoryContradictions.KeepBoth), (repeat.Unchanged, repeat.Settled!.Decision));
            Assert.Equal(before - 1, await count());
        }

        // Replace: one stands and the other is history; a person's alone.
        var x = new ContradictionStory(65);
        var replacing = Checking(x);
        var older = (await replacing.Create(who.Alex, new(x.Tuesday), Resolves.Mcp, default)).Id;
        var newer = (await replacing.Create(who.Alex, new(x.Thursday), Resolves.Mcp, default)).Id;
        var section = Resolves.Section(ai, "contradictions");
        var was = await section();
        var replaced = await Store.ResolveItem(at, new(newer, MemoryResolve.SettleReplace, Conflict: older), insights, Resolves.Cli, default);
        Assert.Equal(MemoryContradictions.Replaced, replaced.Settled!.Decision);
        Assert.Equal(was - 1, await section());
        Assert.True((await Store.ResolveItem(at, new(newer, MemoryResolve.SettleKeepBoth, Conflict: older), insights, Resolves.Cli, default)).Unchanged);
        Assert.Equal(was - 1, await section());
    }
}

// Scope: an item of another's private scope answers as an unknown id; a Family one, as its step does. Nothing changes and no
// other reader's count moves.
public sealed class MemoryResolveScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    NpgsqlDataSource S => fixture.Source;
    MemoryStore Store => new(fixture.Source);

    [PostgresFact] public async Task Scopes_on_nobody_resolves_what_they_do_not_read_or_may_not_change()
    {
        await Scoped.Mode(S, "on");
        await MemoryLogins.Family(S);
        try
        {
            var who = await MemoryLogins.Make(S, "resolve-scope");
            await using var alex = await StreamCalls.As(fixture, who.Alex);
            await using var sam = await StreamCalls.As(fixture, who.Sam);
            await using var house = await StreamCalls.As(fixture, who.House);

            var loop = await Dues.Remember(Store, who.Alex, "DEMO: Alex will post the Wendle letter.", null, ["task"], "private");
            var dated = await Dues.Remember(Store, who.Alex, "DEMO: Alex's Wendle dentist.", Dues.Day(-1), scope: "private");
            var decision = await Ledger.Decide(Store, who.Alex, "DEMO: Alex chose the Wendle dentist.", scope: "private", reviewBy: "2020-01-01");
            var fact = await Dues.Remember(Store, who.Alex, "DEMO: Alex's Wendle flat has a blue door.", null, scope: "private");
            await Briefs.Stale(S, fact);
            await Standing.Clean(S);
            var standing = await Standing.Set(Store, who.Alex, "DEMO: When the Wendle flat comes up, mention the door.", scope: "private");
            var checking = new MemoryStore(S) { Check = new DuplicateCheck(new ContradictionStory(66), 0.85, offer: 0.5) };
            var words = "DEMO: Alex's Wendle flat key is under the mat.";
            await checking.Create(who.Alex, new(words, null, "private"), Resolves.Mcp, default);
            var copy = (await checking.Create(who.Alex, new(words, null, "private"), Resolves.Mcp, default)).Id;
            var candidate = await Resolves.Candidate(S, Store, who.Alex, "private", "private", "alex");
            var insight = await StreamCalls.Save(alex, "DEMO · Alex's Wendle flat");
            object[] calls =
            [
                new { id = loop, action = "done" }, new { id = loop, action = "not_a_task" }, new { id = dated, action = "snooze", until = Dues.Day(9) }, new { id = dated, action = "clear_date" },
                new { id = decision, action = "outcome", result = "right" }, new { id = fact, action = "still_true" }, new { id = standing, action = "stop_standing" },
                new { id = copy, action = "keep_copy" }, new { id = candidate, action = "reject_candidate" }, new { id = candidate, action = "use_candidate" },
                new { id = insight, action = "confirm_insight" }, new { id = insight, action = "dismiss_insight" },
            ];
            var (audit, state) = (await Resolves.Audit(S), await Resolves.State(S));
            foreach (var other in new[] { sam, house })
                foreach (var call in calls)
                {
                    var refused = await other.Refused("resolve", call);
                    // As an id that names nothing: nothing says it is there.
                    Assert.Equal("unknown", (string)refused["code"]!);
                }
            var strange = await sam.Refused("resolve", new { id = Guid.NewGuid(), action = "done" });
            Assert.Equal(strange.ToJsonString(), (await sam.Refused("resolve", new { id = loop, action = "done" })).ToJsonString());
            Assert.Equal((audit, state), (await Resolves.Audit(S), await Resolves.State(S)));

            // Alex's own: each once, and Sam's brief never moved.
            var says = (string)(await Resolves.Brief(sam))["says"]!;
            await Resolves.Once(S, alex, MemoryResolve.Loops, new { id = loop, action = "done" }, Resolves.Section(alex, "you_owe"));
            await Resolves.Once(S, alex, MemoryResolve.DueList, new { id = dated, action = "clear_date" }, Resolves.Section(alex, "due"));
            await Resolves.Once(S, alex, MemoryResolve.Decisions, new { id = decision, action = "outcome", result = "right" }, Resolves.Section(alex, "decisions"));
            await Resolves.Once(S, alex, MemoryResolve.Stale, new { id = fact, action = "still_true" }, Resolves.Section(alex, "stale"));
            await Resolves.Once(S, alex, MemoryResolve.Standing, new { id = standing, action = "stop_standing" }, () => Resolves.Standings(alex));
            await Resolves.Once(S, alex, MemoryResolve.Duplicates, new { id = copy, action = "keep_copy" }, Resolves.Section(alex, "duplicates"));
            await Resolves.Once(S, alex, MemoryResolve.Candidates, new { id = candidate, action = "reject_candidate" }, () => Resolves.Candidates(alex));
            Assert.Equal(says, (string)(await Resolves.Brief(sam))["says"]!);

            // A Family memory by a person: Sam reads it and may not change it, so the step's refusal, whether it waits or not. The
            // reader who may (its author) takes it off, and a second by the reader who may not is still refused, never told unchanged.
            var family = await Dues.Remember(Store, who.Alex, "DEMO: the household will mend the Wendle fence.", null, ["task"], "family");
            Assert.Equal("forbidden", (string)(await sam.Refused("resolve", new { id = family, action = "done" }))["code"]!);
            await Resolves.Once(S, alex, MemoryResolve.Loops, new { id = family, action = "done" }, Resolves.Section(alex, "you_owe"));
            Assert.Equal("forbidden", (string)(await sam.Refused("resolve", new { id = family, action = "done" }))["code"]!);

            // An admin changes a Family memory and never reads a private one.
            await Scoped.Execute(S, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            var another = await Dues.Remember(Store, who.Alex, "DEMO: the household will mend the Wendle stile.", null, ["task"], "family");
            Assert.False((bool)(await sam.Ok("resolve", new { id = another, action = "done" }))["unchanged"]!);
            var secret = await Dues.Remember(Store, who.Alex, "DEMO: Alex will post another Wendle letter.", null, ["task"], "private");
            Assert.Equal("unknown", (string)(await sam.Refused("resolve", new { id = secret, action = "done" }))["code"]!);
            Assert.Equal("unknown", (string)(await sam.Refused("resolve", new { id = insight, action = "confirm_insight" }))["code"]!);
        }
        finally
        {
            await Scoped.Mode(S, "off");
        }
    }

    // A resolve answers the scope it found, and the client that saved an insight never reviews it, in any mode.
    [PostgresFact] public async Task The_login_that_saved_an_insight_never_resolves_it()
    {
        await Scoped.Mode(S, "off");
        var who = await MemoryLogins.Make(S, "resolve-own");
        await using var ai = await StreamCalls.As(fixture, who.Alex);
        var insight = await StreamCalls.Save(ai, "DEMO · a Wendle insight of Alex's client");
        var state = await Resolves.State(S);
        foreach (var action in new[] { "confirm_insight", "dismiss_insight" })
            Assert.Equal("forbidden", (string)(await ai.Refused("resolve", new { id = insight, action }))["code"]!);
        Assert.Equal(state, await Resolves.State(S));
        // Once reviewed by someone else, it says the same: no word reaches it through the one that proposed it.
        await using var person = await StreamCalls.As(fixture, who.Sam);
        await person.Ok("resolve", new { id = insight, action = "confirm_insight" });
        Assert.Equal("forbidden", (string)(await ai.Refused("resolve", new { id = insight, action = "dismiss_insight" }))["code"]!);
    }
}

public sealed class MemoryResolveToolTests
{
    // Off: nothing lists or answers it, and nothing is read.
    [Fact] public async Task Off_nothing_lists_or_answers_resolve()
    {
        var off = TestSettings.Use();
        Assert.DoesNotContain("resolve", HarnessTools.Create(off, []).Select(t => t.ProtocolTool.Name));
        Assert.Contains("resolve", HarnessTools.Create(MemoryToolSettings.On, []).Select(t => t.ProtocolTool.Name));
        var database = new Database(NpgsqlDataSource.Create("Host=example.invalid;Database=none"));
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(Guid.NewGuid())).AddSingleton(database)
            .AddSingleton(off).AddSingleton(new Retrieval(database)).BuildServiceProvider();
        await using var client = await MemoryToolClient.Start(services, off);
        Assert.DoesNotContain("resolve", (await client.Client.ListToolsAsync()).Select(t => t.Name));
        var none = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(() => client.Raw("no_such_tool", "{}"));
        var named = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(() => client.Raw("resolve", new { id = Guid.NewGuid(), action = "done" }));
        Assert.Equal((none.ErrorCode, none.Message.Replace("no_such_tool", "resolve")), (named.ErrorCode, named.Message));
        // Called directly with the setting off it says so, reading nothing.
        var direct = await new MemoryTools(new HarnessLogin(Guid.NewGuid())).Resolve(services, Guid.NewGuid(), MemoryResolveActionArg.Done);
        Assert.True(direct.IsError);
        Assert.Contains("memory.tools", MemoryToolClient.Text(direct));
    }

    // Every word the tool takes is a queue's, and only a person's queues are a person's: nothing here can reach a database.
    [Fact] public void Every_action_belongs_to_a_queue_and_only_the_held_and_contradiction_ones_are_a_persons()
    {
        var words = Enum.GetValues<MemoryResolveActionArg>().Select(a => System.Text.Json.JsonSerializer.Serialize(a).Trim('"')).ToArray();
        Assert.Equal(MemoryResolve.Queues.Keys.Order(), words.Order());
        Assert.Equal([MemoryResolve.Release, MemoryResolve.SettleKeepBoth, MemoryResolve.SettleReplace], words.Where(MemoryResolve.PersonOnly));
        Assert.Equal(10, MemoryResolve.Queues.Values.Distinct().Count());
        Assert.Contains("resolve(id: ..., action: ...)", MemoryInstructions.Text);
        Assert.Contains("release and settling a contradiction are the person's own, always refused to you", MemoryInstructions.Text);
    }

    // An AI client's channel never reads anything for a person's step, and a word in the wrong place is refused before a read: the
    // store here reaches no database.
    [Fact] public async Task A_persons_step_and_a_wrong_argument_are_refused_before_anything_is_read()
    {
        await using var nowhere = NpgsqlDataSource.Create("Host=example.invalid;Database=none");
        var (store, insights, id) = (new MemoryStore(nowhere), new InsightStore(nowhere), Guid.NewGuid());
        Task<string> Refused(MemoryResolveAsk ask, MemoryCall? call = null) => MemoryLogins.Refused(() => store.ResolveItem(id, ask, insights, call ?? Resolves.Mcp, default));
        foreach (var action in new[] { MemoryResolve.Release, MemoryResolve.SettleKeepBoth, MemoryResolve.SettleReplace })
            Assert.Equal("needs_person", await Refused(new(id, action)));
        Assert.Equal("invalid", await Refused(new(id, "maybe")));
        Assert.Equal("invalid", await Refused(new(id, "")));
        Assert.Equal("invalid", await Refused(new(id, MemoryLoops.Done), MemoryLogins.Key("resolve-key-1")));
        foreach (var ask in new MemoryResolveAsk[]
        {
            new(id, MemoryResolve.Outcome), new(id, MemoryLoops.Done, Result: "right"), new(id, MemoryResolve.Outcome, Result: "perhaps"),
            new(id, DueDates.Snooze), new(id, DueDates.ClearDate, Until: "2030-01-01"), new(id, MemoryLoops.Done, Until: "2030-01-01"),
            new(id, MemoryLoops.Done, Note: "DEMO"), new(id, MemoryResolve.StillTrue, Note: "DEMO"), new(id, MemoryResolve.RejectCandidate, Insight: id),
            new(id, MemoryLoops.Done, Insight: id), new(id, MemoryResolve.ConfirmInsight, Expected: 1), new(id, MemoryResolve.UseCandidate, Expected: 1),
            new(id, MemoryLoops.Done, Conflict: id), new(id, MemoryResolve.Release, Conflict: id),
        })
            Assert.Equal("invalid", await Refused(ask, ask.Action == MemoryResolve.Release ? new("app") : null));
        Assert.Equal("invalid", await Refused(new(id, MemoryResolve.SettleKeepBoth), new("app")));
    }
}
