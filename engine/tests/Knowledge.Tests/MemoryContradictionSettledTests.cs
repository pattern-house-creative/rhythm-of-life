using Knowledge.Tests.Parity;
using Npgsql;

namespace Knowledge.Tests;

// What an AI client may not do to a pair a person has to see or has seen (SB-P5-03, migration 068's guard; D-222, Q-161): the
// earlier memory of a named pair keeps its words, and a person's settling holds against an AI client whichever way it went, while
// a person's channels stay free to change it. A settled row outlives a move, so the pair is not offered again. Fictional people
// only: alex, sam and the household; every text is DEMO.
public sealed class MemoryContradictionSettledTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static readonly MemoryCall Mcp = MemoryContradictionTests.Mcp, Cli = MemoryContradictionTests.Cli;
    MemoryStore Checking(ContradictionStory w) => new(fixture.Source) { Check = new DuplicateCheck(w, 0.85, offer: 0.5) };
    MemoryStore Plain => new(fixture.Source);

    async Task<string> Row(Guid id) => (string)(await MemoryLogins.Scalar(fixture.Source,
        "SELECT revision||'|'||content||'|'||status||'|'||coalesce(valid_until::text,'-') FROM memories WHERE id=@id", ("id", id)))!;

    Task<string> Refused(Func<Task> call) => MemoryLogins.Refused(async () => await call());

    // A row's state as it is stored, past every read.
    async Task<string?> Decision(Guid a, Guid b) => (string?)await MemoryLogins.Scalar(fixture.Source,
        "SELECT coalesce(decision,'-') FROM memory_contradictions WHERE least(memory_id,conflict_id)=least(@a,@b) AND greatest(memory_id,conflict_id)=greatest(@a,@b)", ("a", a), ("b", b));

    // Once somebody has named a pair, an AI client that makes the earlier say what the later says has settled it with no person. An
    // offer nobody has named takes an edit (Q-161), the later memory stays the client's to change, and a person changes either.
    [PostgresFact] public async Task An_AI_client_does_not_rewrite_the_earlier_memory_of_a_named_pair()
    {
        await Scoped.Mode(fixture.Source, "off");
        var who = await MemoryLogins.Make(fixture.Source, "rewrite");
        var w = new ContradictionStory(60);
        var at = await MemoryLogins.AtTerminal(fixture.Source, who.Alex);
        var earlier = await Checking(w).Create(who.Alex, new(w.Tuesday), Mcp, default);
        var later = await Checking(w).Create(who.Alex, new(w.Thursday), Mcp, default);
        Assert.Equal([earlier.Id], later.Candidates!.Select(c => c.Id));

        // Not named yet: an edit is its own step.
        var edited = await Plain.Update(who.Alex, earlier.Id, new(Content: w.Tuesday + " again"), null, Mcp, default);
        Assert.Equal(2, edited.Revision);

        await Plain.Propose(who.Alex, later.Id, earlier.Id, "another day", Mcp, default);
        var kept = await Row(earlier.Id);
        Assert.Equal("needs_person", await Refused(() => Plain.Update(who.Alex, earlier.Id, new(Content: w.Thursday), null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.Append(who.Alex, earlier.Id, "or so they say", null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.Undo(who.Alex, earlier.Id, null, null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.Undo(who.Alex, earlier.Id, 1, null, Mcp, default)));
        Assert.Equal(kept, await Row(earlier.Id));

        // What is no word of it may change; the later memory's words too.
        Assert.Equal(3, (await Plain.Update(who.Alex, earlier.Id, new(Tags: ["demo-bins"]), null, Mcp, default)).Revision);
        Assert.Equal(2, (await Plain.Update(who.Alex, later.Id, new(Content: w.Thursday + " at noon"), null, Mcp, default)).Revision);

        // A person's edit is a person's.
        Assert.Equal(4, (await Plain.Update(at, earlier.Id, new(Content: w.Tuesday + " still"), null, Cli, default)).Revision);

        // Settled, the review is over and the words are the memory's again.
        await Plain.Settle(at, later.Id, earlier.Id, MemoryContradictions.KeepBoth, null, null, Cli, default);
        Assert.Equal(5, (await Plain.Update(who.Alex, earlier.Id, new(Content: w.Tuesday + " once more"), null, Mcp, default)).Revision);
    }

    // A person's replace: an AI client neither brings back what they replaced nor replaces what they kept by it. A retry of the same
    // replacement answers as any retry does, and a person may take it back.
    [PostgresFact] public async Task An_AI_client_does_not_reopen_a_memory_a_person_replaced()
    {
        await Scoped.Mode(fixture.Source, "off");
        var who = await MemoryLogins.Make(fixture.Source, "reopen");
        var w = new ContradictionStory(61);
        var at = await MemoryLogins.AtTerminal(fixture.Source, who.Alex);
        var replaced = await Checking(w).Create(who.Alex, new(w.Tuesday), Mcp, default);
        var stands = await Checking(w).Create(who.Alex, new(w.Thursday), Mcp, default);
        await Plain.Settle(at, stands.Id, replaced.Id, MemoryContradictions.Replace, null, null, Cli, default);
        Assert.Equal("replaced", await Decision(replaced.Id, stands.Id));
        var row = await Row(replaced.Id);
        Assert.NotEqual("-", row.Split('|')[3]);

        Assert.Equal("needs_person", await Refused(() => Plain.Undo(who.Alex, replaced.Id, null, null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.Undo(who.Alex, replaced.Id, 1, null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.SetValidity(who.Alex, replaced.Id, new(false, null, true, null), null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.Supersede(who.Alex, replaced.Id, stands.Id, null, Mcp, default)));
        Assert.Equal(row, await Row(replaced.Id));
        Assert.Empty((await Plain.Get(who.Alex, stands.Id, default))!.Contradictions!);

        // The replacement the person settled on is no change: a retry answers as before.
        var retry = await Plain.Supersede(who.Alex, stands.Id, replaced.Id, null, Mcp, default);
        Assert.Equal("replaced", retry.Reason);

        // A person may take it back.
        var back = await Plain.Undo(at, replaced.Id, null, null, Cli, default);
        Assert.Contains("validity", back.Changed);
        Assert.Equal("-", (await Row(replaced.Id)).Split('|')[3]);
    }

    // A person's keep both: both stand. An AI client neither ends one while the other is live, by any way there is, nor replaces one
    // by the other or by a third. Once the person has ended one, the other is an ordinary memory again.
    [PostgresFact] public async Task An_AI_client_does_not_end_either_side_of_a_pair_a_person_kept()
    {
        await Scoped.Mode(fixture.Source, "off");
        var who = await MemoryLogins.Make(fixture.Source, "kept");
        var w = new ContradictionStory(62);
        var at = await MemoryLogins.AtTerminal(fixture.Source, who.Alex);
        var one = await Checking(w).Create(who.Alex, new(w.Tuesday), Mcp, default);
        var two = await Checking(w).Create(who.Alex, new(w.Thursday), Mcp, default);
        await Plain.Settle(at, two.Id, one.Id, MemoryContradictions.KeepBoth, null, null, Cli, default);
        Assert.Equal("keep_both", await Decision(one.Id, two.Id));
        var (rowOne, rowTwo) = (await Row(one.Id), await Row(two.Id));

        foreach (var (mine, other) in new[] { (one.Id, two.Id), (two.Id, one.Id) })
        {
            Assert.Equal("needs_person", await Refused(() => Plain.Supersede(who.Alex, other, mine, null, Mcp, default)));
            Assert.Equal("needs_person", await Refused(() => Plain.SetStatus(who.Alex, mine, "deprecated", null, Mcp, default)));
            Assert.Equal("needs_person", await Refused(() => Plain.Forget(who.Alex, mine, null, Mcp, default)));
        }
        Assert.Equal((rowOne, rowTwo), (await Row(one.Id), await Row(two.Id)));

        // The confirmation is the plan's, which a person's channel can read, and the guard still refuses it through an AI client's.
        var plan = await Plain.Supersede(at, two.Id, one.Id, null, Cli, default);
        Assert.Equal("plan", plan.Action);
        await using (var c = await fixture.Source.OpenConnectionAsync())
        await using (var cmd = Database.Command(c, null, "SELECT supersede_memory(@p,@x,@y,@code,'mcp',NULL)", ("p", who.Alex), ("x", two.Id), ("y", one.Id), ("code", plan.Confirmation)))
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
            Assert.Equal(("22023", "needs_person"), (e.SqlState, e.Hint));
        }

        // An end needs a start before it: a person's channel gives one, which ends nothing.
        await Plain.SetValidity(at, one.Id, new(true, DateTimeOffset.UtcNow.AddDays(-2), false, null), null, Cli, default);
        Assert.Equal("needs_person", await Refused(() => Plain.SetValidity(who.Alex, one.Id, new(false, null, true, DateTimeOffset.UtcNow.AddDays(-1)), null, Mcp, default)));

        // A third memory replaces neither: the plan is an ordinary one's, and the guard stops it at the change.
        var third = await Plain.Create(who.Alex, new("DEMO: the Wendle bins moved to a new collection round."), Mcp, default);
        var thirdPlan = await Plain.Supersede(who.Alex, third.Id, one.Id, null, Mcp, default);
        Assert.Equal("plan", thirdPlan.Action);
        Assert.Equal("needs_person", await Refused(() => Plain.Supersede(who.Alex, third.Id, one.Id, thirdPlan.Confirmation, Mcp, default)));

        // An undo that would end one, after a person gave it a start: the state it goes back to has no end either way, so it is
        // let through; one that would mark it wrong again is not.
        await Plain.SetStatus(at, two.Id, "deprecated", null, Cli, default);
        await Plain.SetStatus(at, two.Id, "draft", null, Cli, default);
        Assert.Equal("needs_person", await Refused(() => Plain.Undo(who.Alex, two.Id, null, null, Mcp, default)));

        // An edit of the words is an ordinary edit: a settling decides which stand, not what they say for ever.
        Assert.Equal(w.Tuesday + " again", (await Row((await Plain.Update(who.Alex, one.Id, new(Content: w.Tuesday + " again"), null, Mcp, default)).Id)).Split('|')[1]);

        // A person may do any of it, and once one has gone the other is free.
        await Plain.SetStatus(at, two.Id, "deprecated", null, Cli, default);
        Assert.Equal("deprecated", (await Plain.SetStatus(who.Alex, one.Id, "deprecated", null, Mcp, default)).Status);
    }

    // A settled row stays through a move, so a pair a person looked at is not queued again when both reach one scope. Across scopes it
    // names nothing: a proposal answers not_offered, as for any other pair of two scopes, and the guard still holds what they kept.
    // Deleted for good, every row goes with the memory.
    [PostgresFact] public async Task A_persons_settling_survives_a_move_and_the_pair_is_not_queued_again()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "moved");
        var w = new ContradictionStory(63);
        var at = await MemoryLogins.AtTerminal(s, who.Alex);
        var one = await Checking(w).Create(who.Alex, new(w.Tuesday, Scope: "private"), Mcp, default);
        var two = await Checking(w).Create(who.Alex, new(w.Thursday, Scope: "private"), Mcp, default);
        await Plain.Settle(at, two.Id, one.Id, MemoryContradictions.KeepBoth, null, null, Cli, default);

        // Settled, there is nothing to take off a review, so an AI client's move goes ahead, and the row stays.
        await Plain.Share(who.Alex, two.Id, "shared", null, Mcp, default);
        Assert.Equal("keep_both", await Decision(one.Id, two.Id));
        Assert.Equal("not_offered", await Refused(() => Plain.Propose(who.Alex, two.Id, one.Id, "another day", Mcp, default)));
        // Moving one is no way round the person.
        Assert.Equal("needs_person", await Refused(() => Plain.SetStatus(who.Alex, two.Id, "deprecated", null, Mcp, default)));
        Assert.Equal("needs_person", await Refused(() => Plain.SetStatus(who.Alex, one.Id, "deprecated", null, Mcp, default)));

        // Both in Family: not offered again, not queued, and settled to whoever names it.
        await Plain.Share(at, one.Id, "shared", null, Cli, default);
        var edit = await Checking(w).Append(who.Alex, two.Id, $"and on {w.Day("tuesday")}", null, Mcp, default);
        Assert.Empty(edit.Candidates!);
        Assert.DoesNotContain((await Plain.Contradictions(who.Alex, null, 50, null, 200, Mcp, default)).Items, k => k.Memory.Id == two.Id || k.Conflict.Id == two.Id);
        Assert.Empty((await Plain.Get(who.Alex, two.Id, default))!.Contradictions!);
        Assert.Equal("settled", await Refused(() => Plain.Propose(who.Alex, two.Id, one.Id, "another day", Mcp, default)));
        Assert.Equal("keep_both", await Decision(one.Id, two.Id));
        Assert.Equal("keep_both", (await Plain.Settle(at, one.Id, two.Id, MemoryContradictions.KeepBoth, null, null, Cli, default)).Decision);
        Assert.True((await Plain.Settle(at, one.Id, two.Id, MemoryContradictions.KeepBoth, null, null, Cli, default)).Write.Unchanged);

        // Deleted for good, the row goes, a reason it may hold included.
        var gone = await Plain.Forget(at, two.Id, null, Cli, default);
        await Plain.DeleteForever(at, two.Id, gone.Trash.Nonce, two.Id.ToString(), Cli, default);
        Assert.Null(await Decision(one.Id, two.Id));
    }
}
