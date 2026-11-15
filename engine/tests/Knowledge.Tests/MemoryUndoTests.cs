using System.Text;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// Undo for native memories (migration 041, Core/MemoryUndo.cs; SB-P3-05, docs/dev/memory.md section 5, undo a change;
// acceptance story 3): the newest change undone, a memory put back as it was at an earlier revision, an AI client's
// group of changes undone together five memories a call and only where its changes are still the newest, and every undo
// itself undone the same way. Fictional people only: alex, sam and the household; every text is DEMO or the corpus's own
// synthetic sample. Nothing outside these tests undoes a memory yet.

public sealed class MemoryGroupKeyTests
{
    static readonly Guid Alex = Guid.NewGuid(), Other = Guid.NewGuid();
    static readonly DateTimeOffset Then = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    static MemoryStore.ChangeRow Row(long id, int minute, string change = "appended", Guid? principal = null, string? client = "DEMO client",
        string channel = "mcp", Guid? memory = null, int revision = 2) =>
        new(id, memory ?? Guid.NewGuid(), revision, change, Then.AddMinutes(minute), principal ?? Alex, "alex", channel, client);

    static string Base64Url(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // One login's changes of one kind through one channel and client, each within ten minutes of the one before.
    [Fact] public void A_group_is_one_logins_changes_of_one_kind_each_within_ten_minutes_of_the_one_before()
    {
        var runs = MemoryStore.Runs([
            Row(1, 0), Row(2, 10), Row(3, 19), Row(4, 30), Row(5, 31, principal: Other), Row(6, 32, "status_changed"), Row(7, 33, "updated"),
            Row(8, 34, client: null), Row(9, 35, channel: "app"), Row(10, 36, "reverted"), Row(11, 37, "released", channel: "app"),
            Row(12, 38, "status_changed")]);
        Assert.Equal(["1 2 3", "4 7", "5", "6 12", "8", "9", "10", "11"], runs.Select(r => string.Join(" ", r.Select(x => x.Id))));
        Assert.Equal(["edit", "edit", "status", "release", "revert"],
            new[] { "appended", "updated", "status_changed", "released", "reverted" }.Select(MemoryStore.Family));
    }

    // A change the answer leaves out ends its own kind's group and no other's, and is in none.
    [Fact] public void A_change_left_out_ends_its_group_and_is_in_none()
    {
        var runs = MemoryStore.Runs([
            Row(1, 0), Row(2, 1) with { Shown = false }, Row(3, 2), Row(4, 3, "status_changed"), Row(5, 4), Row(6, 5, principal: Other) with { Shown = false }, Row(7, 6)]);
        Assert.Equal(["1", "3 5 7", "4"], runs.Select(r => string.Join(" ", r.Select(x => x.Id))));
    }

    // A key names the memory and revision of a group's first and last change, nothing else, and anything that is not
    // such a key names no group.
    [Fact] public void A_key_names_its_first_and_last_change_and_nothing_else_reads_as_one()
    {
        var first = Row(1, 0, revision: 3);
        var last = Row(9, 5, revision: 12);
        var key = MemoryStore.Key(first, last);
        Assert.Matches("^[A-Za-z0-9_-]+$", key);
        Assert.Equal(((first.Memory, 3), (last.Memory, 12)), MemoryStore.Decode(key)!.Value);
        foreach (var bad in new[] { null, "", " ", key + "=", key + "!", "djEu", key.ToUpperInvariant(), "not a key", new string('a', 300),
            Base64Url($"v2.{first.Memory:N}.3.{last.Memory:N}.12"), Base64Url($"v1.{first.Memory:N}.0.{last.Memory:N}.12"),
            Base64Url($"v1.{first.Memory}.3.{last.Memory:N}.12"), Base64Url($"v1.{first.Memory:N}.3.{last.Memory:N}.12 ") })
            Assert.Null(MemoryStore.Decode(bad));
    }
}

public sealed class MemoryUndoTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall App = new("app", "DEMO app");
    static readonly IReadOnlyList<ParityMemory> Planted = [.. ParityCorpus.Load().Memories.Where(m => m.Expect?.Held == true)];

    // A memory's audit trail, oldest first: each change's event, channel, the revision an undo went back to and the hold
    // its row records.
    Task<object?> Trail(Guid id) => MemoryRows.Scalar(fixture.Source, """
        SELECT string_agg(event||':'||channel||coalesce(':'||(detail->>'to'),'')||coalesce(':'||(detail->'held'->>'reason'),'')
          ||coalesce(':unheld-'||(detail->>'unheld'),''),',' ORDER BY id) FROM memory_events WHERE memory_id=@id
        """, ("id", id));
    Task<object?> Count(string table, Guid id) => MemoryRows.Scalar(fixture.Source, $"SELECT count(*) FROM {table} WHERE memory_id=@id", ("id", id));

    // A share or un-share (SB-P3-10, migration 048), as MemoryHistoryTests makes one.
    async Task Move(Guid id, string to, Guid by) =>
        Assert.Equal(to, (await Store.Share(by, id, to == "household" ? "shared" : "private", null, MemoryLogins.Mcp, default)).Subject);

    [PostgresFact] public async Task The_newest_change_is_undone_as_a_change_of_its_own_and_an_undo_is_undone_the_same_way()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-newest");
        await Scoped.Mode(s, "off");
        const string text = "DEMO: the canal walk takes forty fictional minutes.";
        var id = (await Store.Create(who.Alex, new(text, ["walks"]), MemoryLogins.Mcp, default)).Id;
        // Writing a memory is no change to undo (forgetting it is the trash's).
        Assert.Equal("nothing_to_undo", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default)));
        await Store.Append(who.Sam, id, "DEMO: it rained.", 1, MemoryLogins.Mcp, default);
        await Store.Update(who.House, id, new(Tags: ["walks", "canal"], Kind: "semantic", Importance: 4), 2, MemoryLogins.Mcp, default);
        await Store.SetStatus(who.App, id, "canonical", 3, App, default);
        var appended = (await Store.GetVersion(who.Alex, id, 2, default))!.Version!.Content;

        // The newest change undone: the memory as it was before it, at a revision of its own.
        var undone = await Store.Undo(who.Sam, id, null, 4, MemoryLogins.Mcp, default);
        Assert.Equal((5, "draft", (int?)3, false), (undone.Revision, undone.Status, undone.To, undone.Unchanged));
        Assert.Equal(["status"], undone.Changed);
        var now = (await Store.Get(who.Alex, id, default))!;
        Assert.Equal((appended, "semantic", (int?)4, "draft"), (now.Content, now.Kind, now.Importance, now.Status));
        Assert.Equal(["walks", "canal"], now.Tags);
        // The undo undone the same way: as at revision 4, canonical again.
        var redone = await Store.Undo(who.Alex, id, null, 5, MemoryLogins.Mcp, default);
        Assert.Equal((6, "canonical", (int?)4), (redone.Revision, redone.Status, redone.To));
        // Back to a version: revision 1's text, tags, status, kind and importance, from the app.
        var back = await Store.Undo(who.App, id, 1, null, App, default);
        Assert.Equal((7, (int?)1), (back.Revision, back.To));
        Assert.Equal(["content", "tags", "status", "kind", "importance"], back.Changed);
        now = (await Store.Get(who.Alex, id, default))!;
        Assert.Equal((text, (string?)null, (int?)null, "draft"), (now.Content, now.Kind, now.Importance, now.Status));
        Assert.Equal(["walks"], now.Tags);
        // And that undone too: as at revision 6. A memory's scope, author and source are never an undo's to change.
        var again = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal((8, (int?)6), (again.Revision, again.To));
        now = (await Store.Get(who.Sam, id, default))!;
        Assert.Equal((appended, "canonical", "household", "alex", "mcp"), (now.Content, now.Status, now.Subject, now.Author, now.Source));

        // History lists every undo as a change, with who made it, the revision it went back to and the fields it changed.
        var history = (await Store.History(who.Sam, id, null, MemoryStore.HistoryDefault, default))!;
        Assert.Equal("8:reverted:6 7:reverted:1 6:reverted:4 5:reverted:3 4:status_changed: 3:updated: 2:appended: 1:created:",
            string.Join(" ", history.Items.Select(i => $"{i.Revision}:{i.Event}:{i.To}")));
        Assert.Equal(["content", "tags", "status", "kind", "importance"], history.Items[0].Fields);
        Assert.Equal(("alex", "mcp", "DEMO client"), (history.Items[0].Person, history.Items[0].Channel, history.Items[0].Client));
        Assert.Equal(((string?)null, "app", "DEMO app"), (history.Items[1].Person, history.Items[1].Channel, history.Items[1].Client));
        Assert.Equal(("sam", "draft"), (history.Items[3].Person, history.Items[3].Status));
        Assert.Equal(text, (await Store.GetVersion(who.Sam, id, 7, default))!.Version!.Content);
        // One version and one audit row per undo, and nothing else.
        Assert.Equal((7L, 8L), ((long)(await Count("memory_versions", id))!, (long)(await Count("memory_events", id))!));

        // Back to the revision it is at, or to one that holds what it holds now: nothing is written.
        var same = await Store.Undo(who.Alex, id, 8, null, MemoryLogins.Mcp, default);
        Assert.Equal((8, true, (int?)8), (same.Revision, same.Unchanged, same.To));
        var equal = await Store.Undo(who.Alex, id, 6, null, MemoryLogins.Mcp, default);
        Assert.Equal((8, true, (int?)6), (equal.Revision, equal.Unchanged, equal.To));
        Assert.Empty(equal.Changed);
        // Refused, writing nothing: a stale revision (with the current one), a revision above the current one or below 1,
        // an idempotency key, the terminal for a login with no person, an unknown memory and one in the trash.
        var stale = await Assert.ThrowsAsync<MemoryRefused>(() => Store.Undo(who.Alex, id, null, 7, MemoryLogins.Mcp, default));
        Assert.Equal(("conflict", (int?)8), (stale.Code, stale.Revision));
        Assert.Equal("no_version", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, 9, null, MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, 0, null, MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, null, null, MemoryLogins.Key("undo-key-0001"), default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Undo(who.House, id, null, null, new("cli"), default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Undo(who.Alex, Guid.NewGuid(), null, null, MemoryLogins.Mcp, default)));
        Assert.Equal(8L, (long)(await Count("memory_events", id))!);
        await Scoped.Execute(s, """
            INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@id,'forget',@p,'mcp',now()+interval '14 days');
            UPDATE resources SET status='deleted' WHERE id=@id
            """, ("id", id), ("p", who.Alex));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default)));
        Assert.Equal(8, (int)(await MemoryRows.Scalar(s, "SELECT revision FROM memories WHERE id=@id", ("id", id)))!);
    }

    [PostgresFact] public async Task An_undo_brings_back_its_revisions_project_and_validity_but_never_a_project_deleted_since()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-fields");
        await Scoped.Mode(s, "off");
        var garden = (await Store.CreateProject(who.Alex, new("DEMO Garden", Slug: "demo-undo-garden"), MemoryLogins.Mcp, default)).Project;
        var id = (await Store.Create(who.Alex, new("DEMO: beans along the fence.", Project: "demo-undo-garden"), MemoryLogins.Mcp, default)).Id;
        await Store.Update(who.Alex, id, new(Project: "demo-undo-shed"), null, MemoryLogins.Mcp, default);
        var undone = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal(["project"], undone.Changed);
        Assert.Equal(garden.Id, (await Store.Get(who.Sam, id, default))!.Project!.Id);
        // A project deleted since is not made again: undoing the change its delete made changes nothing, so writes nothing.
        await Store.DeleteProject(who.Alex, garden.Id, null, MemoryLogins.Mcp, default);
        var gone = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal((4, true, (int?)3), (gone.Revision, gone.Unchanged, gone.To));
        Assert.Null((await Store.Get(who.Sam, id, default))!.Project);
        // Validity comes back with its revision (set by hand here, until SB-P4-06 writes it).
        await Scoped.Execute(s, "UPDATE memories SET valid_from=now()-interval '2 days',valid_until=now()+interval '30 days' WHERE id=@id", ("id", id));
        await Store.Append(who.Alex, id, "DEMO: and peas.", null, MemoryLogins.Mcp, default);
        await Scoped.Execute(s, "UPDATE memories SET valid_from=NULL,valid_until=NULL WHERE id=@id", ("id", id));
        var valid = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal(["content", "validity"], valid.Changed);
        var now = (await Store.Get(who.Sam, id, default))!;
        Assert.Equal(("DEMO: beans along the fence.", true), (now.Content, now.ValidUntil > DateTimeOffset.UtcNow.AddDays(29)));
    }

    // An undo never lets an AI client adopt held words: a held revision comes back held (an undone release holds the
    // memory again), the same words keep their hold unless a person undoes, and words an undo brings back are screened
    // like any change's (D-039, D-161).
    [PostgresFact] public async Task An_undo_puts_holds_back_and_never_releases_one_for_an_AI_client()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-holds");
        await Scoped.Mode(s, "off");
        const string clean = "DEMO: the allotment shed key is under the blue pot.";
        var id = (await Store.Create(who.Alex, new(clean), MemoryLogins.Mcp, default)).Id;
        Assert.True((await Store.Append(who.Alex, id, Planted[0].Text, null, MemoryLogins.Mcp, default)).Hold!.Now);
        // Undoing the change that brought held words takes them away: what is left is what was there, never held.
        var removed = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Null(removed.Hold);
        Assert.Equal(["content", "hold"], removed.Changed);
        Assert.Equal((clean, (string?)null), ((await Store.Get(who.Alex, id, default))!.Content, (await Store.Get(who.Alex, id, default))!.Hold));
        // Undoing that undo brings the held words back held, and says so.
        var back = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal((MemoryHolds.Instruction, true), (back.Hold!.Reason, back.Hold.Now));
        Assert.Equal(["content", "hold"], back.Changed);
        Assert.Equal(true, await MemoryRows.Scalar(s, "SELECT (detail->'held'->>'restored')::boolean FROM memory_events WHERE memory_id=@id AND revision=4", ("id", id)));
        // A person releases it; an AI client's undo of the release holds it again.
        Assert.Null((await Store.Release(who.App, id, null, App, default)).Hold);
        var held = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal((MemoryHolds.Instruction, true), (held.Hold!.Reason, held.Hold.Now));
        Assert.Equal(["hold"], held.Changed);
        // An AI client never undoes its way to a release: back to the released revision, the same words keep their hold
        // and nothing is written.
        var stays = await Store.Undo(who.Alex, id, 5, null, MemoryLogins.Mcp, default);
        Assert.Equal((6, true, MemoryHolds.Instruction, false), (stays.Revision, stays.Unchanged, stays.Hold!.Reason, stays.Hold.Now));
        // A person may: from the app, undoing the undo is the release again.
        var person = await Store.Undo(who.App, id, null, null, App, default);
        Assert.Null(person.Hold);
        Assert.Equal(["hold"], person.Changed);
        // Words a person released, brought back by an undo after a change took them away, are screened against the words
        // they replace, as any change's are: held again, until a person releases them again.
        Assert.Null((await Store.Update(who.Alex, id, new(Content: clean), null, MemoryLogins.Mcp, default)).Hold);
        var screened = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal((MemoryHolds.Instruction, true), (screened.Hold!.Reason, screened.Hold.Now));
        Assert.NotEmpty(screened.Hold.Signals);
        Assert.Equal("created:mcp,appended:mcp:instruction,reverted:mcp:1:unheld-instruction,reverted:mcp:2:instruction,released:app,"
            + "reverted:mcp:4:instruction,reverted:app:5:unheld-instruction,updated:mcp,reverted:mcp:7:instruction", await Trail(id));
        // History serves a held revision without its words, as before.
        var history = (await Store.History(who.Sam, id, null, MemoryStore.HistoryDefault, default))!;
        Assert.True(history.Items[0].Held);
        Assert.Null(history.Items[0].Preview);
    }

    [PostgresFact] public async Task With_scopes_on_an_undo_is_the_owners_the_authors_or_an_admins_and_anyones_own_newest_changes()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-scoped");
        await MemoryLogins.Family(s);
        await Scoped.Mode(s, "on");
        try
        {
            Task<string> Refused(Guid reader, Guid id, int? to = null) =>
                MemoryLogins.Refused(() => Store.Undo(reader, id, to, null, reader == who.App ? App : MemoryLogins.Mcp, default));
            // Alex's private memory: hers alone; to everyone else it is unknown.
            var mine = (await Store.Create(who.Alex, new("DEMO: Alex's private fictional plan."), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, mine, "DEMO: a second private thought.", null, MemoryLogins.Mcp, default);
            foreach (var reader in new[] { who.Sam, who.House, who.App }) Assert.Equal("unknown", await Refused(reader, mine));
            Assert.Equal(1, (await Store.Undo(who.Alex, mine, null, null, MemoryLogins.Mcp, default)).To);
            // A Family memory Alex wrote: not Sam's (no admin) nor the household view's to undo; an admin's.
            var family = (await Store.Create(who.Alex, new("DEMO: the family shed is painted green.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, family, "DEMO: and the door is blue.", null, MemoryLogins.Mcp, default);
            foreach (var reader in new[] { who.Sam, who.House, who.App }) Assert.Equal("forbidden", await Refused(reader, family));
            // Refused before anything else is answered, as every checked writer refuses (033): an undo to the revision it
            // is at, one above it or with a stale revision, never unchanged, no_version or conflict to one who may not undo.
            Assert.Equal("forbidden", await Refused(who.Sam, family, 2));
            Assert.Equal("forbidden", await Refused(who.Sam, family, 3));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Undo(who.Sam, family, null, 1, MemoryLogins.Mcp, default)));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal(1, (await Store.Undo(who.Sam, family, null, null, MemoryLogins.Mcp, default)).To);
            await Scoped.Execute(s, "UPDATE memberships SET role='member' WHERE person_id='sam'");
            // A Family memory no person wrote: any reader of Family.
            var house = (await Store.Create(who.House, new("DEMO: the bins go out on Tuesday.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.House, house, "DEMO: and the recycling on Friday.", null, MemoryLogins.Mcp, default);
            await Store.Append(who.House, house, "DEMO: not on bank holidays.", null, MemoryLogins.Mcp, default);
            Assert.Equal(2, (await Store.Undo(who.Sam, house, null, null, MemoryLogins.Mcp, default)).To);
            Assert.Equal(3, (await Store.Undo(who.App, house, null, null, App, default)).To);

            // Whoever made a change may undo it while it is the newest: Sam's two changes to Alex's Family memory, made
            // while scopes were off, are his to undo, together or one by one, until someone else changes it after him.
            var shed = (await Store.Create(who.Alex, new("DEMO: the shed roof needs felt.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            await Scoped.Mode(s, "off");
            await Store.Append(who.Sam, shed, "DEMO: Sam bought the felt.", null, MemoryLogins.Mcp, default);
            await Store.Append(who.Sam, shed, "DEMO: and the tacks.", null, MemoryLogins.Mcp, default);
            await Scoped.Mode(s, "on");
            Assert.Equal(1, (await Store.Undo(who.Sam, shed, 1, null, MemoryLogins.Mcp, default)).To);
            Assert.Equal(3, (await Store.Undo(who.Sam, shed, null, null, MemoryLogins.Mcp, default)).To);
            await Store.Append(who.Alex, shed, "DEMO: Alex fixed it.", null, MemoryLogins.Mcp, default);
            Assert.Equal("forbidden", await Refused(who.Sam, shed));
            Assert.Equal("forbidden", await Refused(who.Sam, shed, 2));
            Assert.Equal("forbidden", await Refused(who.House, shed, 4));

            // History as the reader sees it (034): shared into Family by hand, then changed there. The admin's history
            // starts at the share, so a revision before it is not there for him; to its author, an undo back to her
            // private stretch would move it out of Family, which an undo never does.
            var moved = (await Store.Create(who.Alex, new("DEMO: Alex's first draft of the fictional party."), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, moved, "DEMO: a private worry.", null, MemoryLogins.Mcp, default);
            await Move(moved, "household", who.Alex);
            await Store.Append(who.Alex, moved, "DEMO: bring the fictional cake.", null, MemoryLogins.Mcp, default);
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal("not_visible", await Refused(who.Sam, moved, 2));
            Assert.Equal("moved", await Refused(who.Alex, moved, 2));
            Assert.Equal(3, (await Store.Undo(who.Sam, moved, 3, null, MemoryLogins.Mcp, default)).To);
            Assert.DoesNotContain("fictional cake", (await Store.Get(who.Sam, moved, default))!.Content);
            Assert.Equal("household", (await Store.Get(who.Alex, moved, default))!.Subject);
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task A_revision_no_longer_kept_is_refused_naming_the_oldest_kept()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-pruned");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: the fictional rota, week 0."), MemoryLogins.Mcp, default)).Id;
        for (var week = 1; week <= 22; week++)
            await Store.Update(who.Alex, id, new(Content: $"DEMO: the fictional rota, week {week}."), null, MemoryLogins.Mcp, default);
        var pruned = await Assert.ThrowsAsync<MemoryRefused>(() => Store.Undo(who.Alex, id, 2, null, MemoryLogins.Mcp, default));
        Assert.Equal(("pruned", (int?)3), (pruned.Code, pruned.Revision));
        Assert.Equal("DEMO: the fictional rota, week 2.", (await Store.GetVersion(who.Alex, id, 3, default))!.Version!.Content);
        Assert.Equal(3, (await Store.Undo(who.Alex, id, 3, null, MemoryLogins.Mcp, default)).To);
        Assert.Equal("DEMO: the fictional rota, week 2.", (await Store.Get(who.Alex, id, default))!.Content);
    }

    // An AI client's burst of changes, undone together: five memories a call, each only while the group's changes are its
    // newest, the rest left as they are; calling again passes over what is undone; and the group undo is itself undone
    // the same way, with the key its own undos make.
    [PostgresFact] public async Task An_AI_clients_group_of_changes_is_undone_five_memories_a_call_where_they_are_still_the_newest()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-group");
        await Scoped.Mode(s, "off");
        var start = await MemoryLogins.Now(s);
        var samClient = new MemoryCall("mcp", "DEMO sam");
        var ids = new List<Guid>();
        for (var i = 0; i < 9; i++) ids.Add((await Store.Create(who.Sam, new($"DEMO: fictional allotment plot {i}."), samClient, default)).Id);
        // Alex's client weeds every plot in one burst, and waters the last one too.
        for (var i = 0; i < 9; i++) await Store.Append(who.Alex, ids[i], $"DEMO: plot {i} weeded.", null, MemoryLogins.Mcp, default);
        await Store.Append(who.Alex, ids[8], "DEMO: and watered.", null, MemoryLogins.Mcp, default);
        // After the burst: the same client confirms plot 0 (a group of status changes), another of Alex's clients edits
        // plot 1, and Sam's client edits plot 2. Those three are no longer the burst's to undo.
        await Store.SetStatus(who.Alex, ids[0], "canonical", null, MemoryLogins.Mcp, default);
        await Store.Append(who.Alex, ids[1], "DEMO: from the other client.", null, new("mcp", "DEMO other"), default);
        await Store.Append(who.Sam, ids[2], "DEMO: Sam's own line.", null, samClient, default);
        var noted = (await Store.Create(who.App, new("DEMO: a note from the app."), App, default)).Id;
        await Store.Update(who.App, noted, new(Tags: ["plots"]), null, App, default);

        // The AI changes as Sam reads them, newest first: no creation (that is no change to undo), and no app change.
        var changes = await Store.Changes(who.Sam, start, "mcp", default);
        Assert.False(changes.Truncated);
        Assert.Equal(["edit:sam:DEMO sam:1:1", "edit:alex:DEMO other:1:1", "status:alex:DEMO client:1:1", "edit:alex:DEMO client:10:9"],
            changes.Groups.Select(g => $"{g.Family}:{g.Person}:{g.Client}:{g.Changes}:{g.Memories}"));
        var burst = changes.Groups[^1];
        Assert.Equal(("mcp", false), (burst.Channel, burst.Capped));
        Assert.Equal((ids[8], 3, "appended"), (burst.Items[0].Memory, burst.Items[0].Revision, burst.Items[0].Event));
        Assert.Equal((ids[0], 2), (burst.Items[^1].Memory, burst.Items[^1].Revision));
        Assert.True(burst.From <= burst.Until);
        Assert.Empty((await Store.Changes(who.Sam, DateTimeOffset.UtcNow.AddMinutes(5), "mcp", default)).Groups);

        // Undone by the AI client that made it, five memories a call: plots 3 to 7 the first time, plot 8 (both its
        // changes) the second; plots 0 to 2 changed after the burst, so each call leaves them as they are and says so.
        string Line(MemoryGroupUndo u) => string.Join(" ", u.Results.Select(r => $"{ids.IndexOf(r.Memory)}:{r.Result}"));
        var first = await Store.UndoGroup(who.Alex, burst.Group, MemoryLogins.Mcp, default);
        Assert.Equal((9, 1, false, false), (first.Total, first.Remaining, first.Done, first.Capped));
        Assert.Equal("0:changed_since 1:changed_since 2:changed_since 3:undone 4:undone 5:undone 6:undone 7:undone", Line(first));
        Assert.NotNull(first.Redo);
        var second = await Store.UndoGroup(who.Alex, burst.Group, MemoryLogins.Mcp, default);
        Assert.Equal((0, true), (second.Remaining, second.Done));
        Assert.Equal("0:changed_since 1:changed_since 2:changed_since 8:undone", Line(second));
        for (var i = 3; i < 9; i++) Assert.Equal($"DEMO: fictional allotment plot {i}.", (await Store.Get(who.Sam, ids[i], default))!.Content);
        // What changed after the burst is as it was left, the burst's change included.
        Assert.Contains("plot 0 weeded", (await Store.Get(who.Sam, ids[0], default))!.Content);
        Assert.Equal("canonical", (await Store.Get(who.Sam, ids[0], default))!.Status);
        Assert.Contains("Sam's own line", (await Store.Get(who.Sam, ids[2], default))!.Content);
        Assert.Contains("plot 2 weeded", (await Store.Get(who.Sam, ids[2], default))!.Content);
        // Calling again passes over what is undone, and undoes nothing.
        var third = await Store.UndoGroup(who.Alex, burst.Group, MemoryLogins.Mcp, default);
        Assert.Equal(("0:changed_since 1:changed_since 2:changed_since", 0, true, (string?)null), (Line(third), third.Remaining, third.Done, third.Redo));

        // The group undo is a group of the client's own changes, its undos, which the AI changes list and whose key the
        // undo gave; undone from the app, it puts the burst back, five memories a call.
        var undos = (await Store.Changes(who.Sam, start, "mcp", default)).Groups[0];
        Assert.Equal(("revert:alex:DEMO client:6:6", second.Redo), ($"{undos.Family}:{undos.Person}:{undos.Client}:{undos.Changes}:{undos.Memories}", undos.Group));
        var redo = await Store.UndoGroup(who.App, undos.Group, App, default);
        Assert.Equal((6, 1, false), (redo.Total, redo.Remaining, redo.Done));
        Assert.Equal("3:undone 4:undone 5:undone 6:undone 7:undone", Line(redo));
        Assert.Equal("8:undone", Line(await Store.UndoGroup(who.App, undos.Group, App, default)));
        for (var i = 3; i < 9; i++) Assert.Contains($"plot {i} weeded", (await Store.Get(who.Sam, ids[i], default))!.Content);
        Assert.Contains("and watered", (await Store.Get(who.Sam, ids[8], default))!.Content);
        // The burst itself has changed since everywhere now: its key undoes nothing.
        var stale = await Store.UndoGroup(who.Alex, burst.Group, MemoryLogins.Mcp, default);
        Assert.Equal((9, 0, 9), (stale.Results.Count(r => r.Result == "changed_since"), stale.Remaining, stale.Results.Count));
        Assert.Equal(["plots"], (await Store.Get(who.Sam, noted, default))!.Tags);

        // A key that names no group: anything not built by the engine, two changes of different kinds, and a key read
        // with nothing undoable in it any more.
        foreach (var bad in new[] { "not-a-group", "", MemoryStore.Key(Row(ids[4], 2), Row(ids[0], 3)), MemoryStore.Key(Row(ids[8], 3), Row(ids[0], 2)),
            MemoryStore.Key(Row(ids[0], 9), Row(ids[0], 9)) })
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.UndoGroup(who.App, bad, App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.UndoGroup(who.App, burst.Group, App with { Key = "group-key-0001" }, default)));
        static MemoryStore.ChangeRow Row(Guid memory, int revision) => new(0, memory, revision, "appended", default, Guid.Empty, null, "mcp", null);
    }

    // With scopes on, a reader's groups hold only the changes it reads, and a key built from what one reader reads names
    // no group for a reader who does not read it; each member is undone only by those who may undo it.
    [PostgresFact] public async Task With_scopes_on_a_group_holds_only_what_its_reader_reads()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-group-scoped");
        var at = await MemoryLogins.Terminals(s);
        await MemoryLogins.Family(s);
        await Scoped.Mode(s, "on");
        try
        {
            var start = await MemoryLogins.Now(s);
            var mine = (await Store.Create(who.Alex, new("DEMO: Alex's private fictional diary."), MemoryLogins.Mcp, default)).Id;
            var hers = (await Store.Create(who.Alex, new("DEMO: the family fictional calendar.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            var ours = (await Store.Create(who.House, new("DEMO: the household fictional rota.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            foreach (var id in new[] { mine, hers, ours }) await Store.Append(who.Alex, id, "DEMO: changed by Alex's client.", null, MemoryLogins.Mcp, default);
            var alex = (await Store.Changes(who.Alex, start, "mcp", default)).Groups.Single();
            Assert.Equal((3, 3, "alex"), (alex.Changes, alex.Memories, alex.Person));
            var sam = (await Store.Changes(who.Sam, start, "mcp", default)).Groups.Single();
            Assert.Equal((2, 2), (sam.Changes, sam.Memories));
            Assert.DoesNotContain(sam.Items, i => i.Memory == mine);
            Assert.Equal(2, (await Store.Changes(who.House, start, "mcp", default)).Groups.Single().Changes);
            // Alex's key names her private memory: no group to Sam. Sam's own key: the household's memory is his to undo,
            // Alex's Family memory is not (he is no admin).
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.UndoGroup(who.Sam, alex.Group, MemoryLogins.Mcp, default)));
            var undone = await Store.UndoGroup(who.Sam, sam.Group, MemoryLogins.Mcp, default);
            Assert.Equal(new[] { (hers, "forbidden"), (ours, "undone") }, undone.Results.Select(r => (r.Memory, r.Result)));
            // Alex, at the terminal, undoes the rest of her client's group; the household's memory is undone already.
            var rest = await Store.UndoGroup(at.Alex, alex.Group, new("cli", "rhythm"), default);
            Assert.Equal(new[] { (mine, "undone"), (hers, "undone") }, rest.Results.Select(r => (r.Memory, r.Result)));
            foreach (var id in new[] { mine, hers, ours }) Assert.DoesNotContain("changed by", (await Store.Get(who.Alex, id, default))!.Content);
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // A member the caller may not undo, or whose undo would write nothing, takes none of a call's five: a group with more
    // than five such members ahead of one that can be undone still reaches it, and is done in that call.
    [PostgresFact] public async Task A_group_undo_passes_over_members_it_cannot_change_and_reaches_the_rest()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-group-pass");
        await Scoped.Mode(s, "off");
        var start = await MemoryLogins.Now(s);
        var samClient = new MemoryCall("mcp", "DEMO sam");
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++) ids.Add((await Store.Create(who.Sam, new($"DEMO: fictional seed tray {i}."), samClient, default)).Id);
        // Alex's client edits six trays and puts each text back as it was, so undoing them writes nothing; then it notes
        // the seventh.
        for (var i = 0; i < 6; i++)
        {
            await Store.Update(who.Alex, ids[i], new(Content: $"DEMO: tray {i} sown."), null, MemoryLogins.Mcp, default);
            await Store.Update(who.Alex, ids[i], new(Content: $"DEMO: fictional seed tray {i}."), null, MemoryLogins.Mcp, default);
        }
        await Store.Append(who.Alex, ids[6], "DEMO: tray 6 watered.", null, MemoryLogins.Mcp, default);
        var burst = (await Store.Changes(who.Sam, start, "mcp", default)).Groups.Single(g => g.Person == "alex");
        Assert.Equal((13, 7), (burst.Changes, burst.Memories));
        string Line(MemoryGroupUndo u) => string.Join(" ", u.Results.Select(r => $"{ids.IndexOf(r.Memory)}:{r.Result}"));
        var undone = await Store.UndoGroup(who.Alex, burst.Group, MemoryLogins.Mcp, default);
        Assert.Equal("0:unchanged 1:unchanged 2:unchanged 3:unchanged 4:unchanged 5:unchanged 6:undone", Line(undone));
        Assert.Equal((7, 0, true), (undone.Total, undone.Remaining, undone.Done));
        Assert.Equal("DEMO: fictional seed tray 6.", (await Store.Get(who.Sam, ids[6], default))!.Content);
    }

    // With scopes on, the change listing starts each memory at its reader's history (034's floor): an author's change
    // from a memory's private stretch is in her own listing, never in a Family reader's, and a key anchored on it names no
    // group for him. His own group's members he may not undo are reported and passed, and the one he may is undone.
    [PostgresFact] public async Task With_scopes_on_the_change_listing_starts_each_memory_at_its_readers_history()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-group-floor");
        await MemoryLogins.Family(s);
        await Scoped.Mode(s, "on");
        try
        {
            var start = await MemoryLogins.Now(s);
            var draft = (await Store.Create(who.Alex, new("DEMO: Alex's fictional party plan, private for now."), MemoryLogins.Mcp, default)).Id;
            var family = new List<Guid>();
            for (var i = 0; i < 5; i++)
                family.Add((await Store.Create(who.Alex, new($"DEMO: family fictional chore {i}.", Scope: "shared"), MemoryLogins.Mcp, default)).Id);
            var house = (await Store.Create(who.House, new("DEMO: the household fictional key hook.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            // Alex's client: a change while the plan is private, the share, then a change to each chore, the plan and the
            // household's memory.
            await Store.Append(who.Alex, draft, "DEMO: a private worry about the cake.", null, MemoryLogins.Mcp, default);
            await Move(draft, "household", who.Alex);
            foreach (var id in family) await Store.Append(who.Alex, id, "DEMO: done by Alex's client.", null, MemoryLogins.Mcp, default);
            await Store.Append(who.Alex, draft, "DEMO: now for everyone.", null, MemoryLogins.Mcp, default);
            await Store.Append(who.Alex, house, "DEMO: done by Alex's client.", null, MemoryLogins.Mcp, default);

            var alex = (await Store.Changes(who.Alex, start, "mcp", default)).Groups.Single();
            Assert.Equal((8, 7), (alex.Changes, alex.Memories));
            Assert.Contains(alex.Items, i => i.Memory == draft && i.Revision == 2);
            var sam = (await Store.Changes(who.Sam, start, "mcp", default)).Groups.Single();
            Assert.Equal((7, 7), (sam.Changes, sam.Memories));
            Assert.Equal([4], sam.Items.Where(i => i.Memory == draft).Select(i => i.Revision));
            // Alex's key starts at the plan's private change: no group to Sam, nor to the household view.
            foreach (var reader in new[] { who.Sam, who.House })
                Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.UndoGroup(reader, alex.Group, MemoryLogins.Mcp, default)));
            // Sam's own key: Alex's six Family memories are not his to undo (he is no admin), the household's is, in one call.
            var undone = await Store.UndoGroup(who.Sam, sam.Group, MemoryLogins.Mcp, default);
            Assert.Equal(6, undone.Results.Count(r => r.Result == "forbidden"));
            Assert.Equal((house, "undone"), undone.Results.Select(r => (r.Memory, r.Result)).Last());
            Assert.Equal((7, 0, true), (undone.Total, undone.Remaining, undone.Done));
            Assert.Equal("DEMO: the household fictional key hook.", (await Store.Get(who.Sam, house, default))!.Content);
            Assert.Contains("now for everyone", (await Store.Get(who.Sam, draft, default))!.Content);
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // 039 keeps a project slot with its project; an undo back to a revision whose slot was in another project brings that
    // project back with the slot, in one change, and is undone the same way. A slot is a person's step (039): the same
    // undo through an AI client's channel is refused and writes nothing.
    [PostgresFact] public async Task An_undo_brings_a_project_slot_back_with_its_project()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "undo-slot");
        var at = await MemoryLogins.Terminals(s);
        await Scoped.Mode(s, "off");
        var cli = new MemoryCall("cli", "DEMO terminal");
        Task<object?> Slot(Guid id) => MemoryRows.Scalar(s, """
            SELECT p.slug||':'||m.capsule||':'||m.capsule_slot FROM memories m JOIN memory_projects p ON p.id=m.project_id WHERE m.id=@id
            """, ("id", id));
        await Store.CreateProject(who.Alex, new("DEMO Pond", Slug: "demo-undo-pond"), MemoryLogins.Mcp, default);
        var id = (await Store.Create(who.Alex, new("DEMO: the pond pump runs at dawn.", Project: "demo-undo-pond"), MemoryLogins.Mcp, default)).Id;
        await Store.SetCapsule(at.Alex, id, "project", "decisions", null, cli, default);
        await Store.SetCapsule(at.Alex, id, null, null, null, cli, default);
        await Store.Update(who.Alex, id, new(Project: "demo-undo-orchard"), null, MemoryLogins.Mcp, default);
        await Store.SetCapsule(at.Alex, id, "project", "current-state", null, cli, default);
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, 2, null, MemoryLogins.Mcp, default)));
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default)));
        Assert.Equal(5, (await Store.Get(who.Alex, id, default))!.Revision);
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_events WHERE memory_id=@id AND event='reverted'", ("id", id)));
        var back = await Store.Undo(at.Alex, id, 2, null, cli, default);
        Assert.Equal((6, (int?)2), (back.Revision, back.To));
        Assert.Equal(["project", "capsule"], back.Changed);
        Assert.Equal("demo-undo-pond:project:decisions", await Slot(id));
        Assert.Equal(5, (await Store.Undo(at.Alex, id, null, null, cli, default)).To);
        Assert.Equal("demo-undo-orchard:project:current-state", await Slot(id));
        // An AI client's undo that leaves the slot as it is passes: undoing an edit of a slotted memory's words.
        await Store.Update(who.Alex, id, new(Content: "DEMO: the pond pump runs at dusk."), null, MemoryLogins.Mcp, default);
        var words = await Store.Undo(who.Alex, id, null, null, MemoryLogins.Mcp, default);
        Assert.Equal(["content"], words.Changed);
        Assert.Equal("demo-undo-orchard:project:current-state", await Slot(id));
    }
}
