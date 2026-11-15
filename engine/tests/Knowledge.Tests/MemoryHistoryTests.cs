using Npgsql;

namespace Knowledge.Tests;

// A native memory's history and its earlier states (migration 034, Core/MemoryHistory.cs; SB-P3-03,
// docs/dev/memory.md section 5, read its history): every change listed newest first with who made it and how, one
// state read by its revision, the 20 newest earlier states kept, and history visible from the share on. Fictional
// people only: alex, sam and the household; every text is DEMO. Nothing outside these tests reads a memory's history.
public sealed class MemoryHistoryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    // A share or un-share (SB-P3-10, migration 048), which 034's rule (memory_history_floor) was written for: the state
    // it replaces retired, the memory moved (its resources row, which the memory follows, 025), the revision moved, and
    // the audit row under the subject it lands in. Until it was built these tests made the same rows by hand.
    async Task Move(Guid id, string to, Guid by) =>
        Assert.Equal(to, (await Store.Share(by, id, to == "household" ? "shared" : "private", null, MemoryLogins.Mcp, default)).Subject);

    // Every page of a reader's history, oldest page last; null when the reader does not read the memory.
    async Task<(MemoryHistory First, List<MemoryHistoryItem> Items)?> All(Guid reader, Guid id, int limit = MemoryStore.HistoryMax)
    {
        var first = await Store.History(reader, id, null, limit, default);
        if (first is null) return null;
        var items = new List<MemoryHistoryItem>(first.Items);
        for (var page = first; page.Next is { } next;)
        {
            page = (await Store.History(reader, id, next, limit, default))!;
            items.AddRange(page.Items);
        }
        return (first, items);
    }

    static string Line(IEnumerable<MemoryHistoryItem> items) => string.Join(" ", items.Select(i => $"{i.Revision}:{i.Event}:{i.Scope}"));

    [PostgresFact] public async Task Every_change_is_listed_newest_first_with_who_made_it_how_and_the_state_it_wrote()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "every");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO:  the canal\n\nwalk   takes forty fictional minutes."), MemoryLogins.Mcp, default)).Id;
        await Store.Append(who.Sam, id, "DEMO: it rained.", 1, new("mcp"), default);
        await Store.Update(who.House, id, new(Tags: ["walks"]), 2, MemoryLogins.Mcp, default);
        await Store.SetStatus(who.App, id, "canonical", 3, new("app"), default);
        var today = (string)(await MemoryLogins.Scalar(s, "SELECT install_today()::text"))!;
        // With scopes off the household shares everything: every reader sees every change, newest first.
        foreach (var reader in new[] { who.Alex, who.Sam, who.House, who.App })
        {
            var history = (await Store.History(reader, id, null, MemoryStore.HistoryDefault, default))!;
            Assert.Equal("4:status_changed:shared 3:updated:shared 2:appended:shared 1:created:shared", Line(history.Items));
            Assert.Equal((4, (int?)null, false, (string?)null, 20, 1, false),
                (history.Revision, history.Next, history.Cut, history.EarlierBy, history.Kept, history.OldestKept, history.Pruned));
            var (status, updated, appended, created) = (history.Items[0], history.Items[1], history.Items[2], history.Items[3]);
            // Who made each change and how: the person its login is bound to, or none; the channel and the client.
            Assert.Equal(((string?)null, "app", (string?)null), (status.Person, status.Channel, status.Client));
            Assert.Equal(((string?)null, "mcp", "DEMO client"), (updated.Person, updated.Channel, updated.Client));
            Assert.Equal(("sam", "mcp", (string?)null), (appended.Person, appended.Channel, appended.Client));
            Assert.Equal(("alex", "mcp", "DEMO client"), (created.Person, created.Channel, created.Client));
            Assert.Equal(["tags"], updated.Fields);
            Assert.Empty(created.Fields);
            // The state each change wrote, its text folded to one line of at most 160 characters.
            Assert.Equal(("canonical", "draft", "draft"), (status.Status, updated.Status, created.Status));
            Assert.Equal("DEMO: the canal walk takes forty fictional minutes.", created.Preview);
            Assert.Equal($"DEMO: the canal walk takes forty fictional minutes. [Update {today}]: DEMO: it rained.", appended.Preview);
            Assert.All(history.Items, i => Assert.True(i.Kept && !i.Held && i.Subject == "household"));
            Assert.True(created.At <= appended.At && appended.At <= status.At);
        }
        var longer = (await Store.Create(who.Alex, new("DEMO " + new string('a', 400)), MemoryLogins.Mcp, default)).Id;
        Assert.Equal(160, (await Store.History(who.Sam, longer, null, 1, default))!.Items.Single().Preview!.Length);
        // Pages of revisions, newest first, each once.
        var page = (await Store.History(who.Sam, id, null, 3, default))!;
        Assert.Equal(("4:status_changed:shared 3:updated:shared 2:appended:shared", (int?)2), (Line(page.Items), page.Next));
        var rest = (await Store.History(who.Sam, id, page.Next, 3, default))!;
        Assert.Equal(("1:created:shared", (int?)null), (Line(rest.Items), rest.Next));
        Assert.Empty((await Store.History(who.Sam, id, 1, 3, default))!.Items);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.History(who.Sam, id, null, 0, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.History(who.Sam, id, null, MemoryStore.HistoryMax + 1, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.History(who.Sam, id, 0, 3, default)));

        // One state by its revision: what it held then and who wrote it.
        var first = (await Store.GetVersion(who.Sam, id, 1, default))!.Version!;
        Assert.Equal(("DEMO:  the canal\n\nwalk   takes forty fictional minutes.", false, "draft", "household", "shared"),
            (first.Content, first.Current, first.Status, first.Subject, first.Scope));
        Assert.Equal(("alex", "mcp", "DEMO client"), (first.Person, first.Channel, first.Client));
        Assert.Empty(first.Tags);
        Assert.NotNull(first.RetiredAt);
        Assert.Equal(["walks"], (await Store.GetVersion(who.Sam, id, 3, default))!.Version!.Tags);
        var now = (await Store.GetVersion(who.Alex, id, 4, default))!.Version!;
        Assert.Equal((true, "canonical", (DateTimeOffset?)null, (string?)null, "app"), (now.Current, now.Status, now.RetiredAt, now.Person, now.Channel));
        Assert.Equal((await Store.Get(who.Alex, id, default))!.Content, now.Content);
        Assert.Equal(("no_version", (int?)null), ((await Store.GetVersion(who.Alex, id, 5, default))!.Missing, (await Store.GetVersion(who.Alex, id, 5, default))!.OldestKept));
        Assert.Null((await Store.GetVersion(who.Alex, id, 5, default))!.Version);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.GetVersion(who.Alex, id, 0, default)));
        // An unknown memory and one in the trash answer alike: none.
        Assert.Null(await Store.History(who.Alex, Guid.NewGuid(), null, 20, default));
        Assert.Null(await Store.GetVersion(who.Alex, Guid.NewGuid(), 1, default));
        await Scoped.Execute(s, """
            INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@id,'forget',@p,'mcp',now()+interval '14 days');
            UPDATE resources SET status='deleted' WHERE id=@id
            """, ("id", id), ("p", who.Alex));
        Assert.Null(await Store.History(who.Alex, id, null, 20, default));
        Assert.Null(await Store.GetVersion(who.Alex, id, 1, default));
    }

    [PostgresFact] public async Task With_scopes_on_a_reader_sees_history_from_the_share_that_brought_the_memory_to_them()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "share");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            var id = (await Store.Create(who.Alex, new("DEMO: Alex's first draft of the fictional party plan."), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, id, "DEMO: a private worry about the fictional budget.", null, MemoryLogins.Mcp, default);
            await Store.Update(who.Alex, id, new(Content: "DEMO: the fictional party is on Saturday."), null, MemoryLogins.Mcp, default);
            // Private: its history is its owner's alone. Sam (an admin), the household view and the app's login read none.
            Assert.Equal("3:updated:private 2:appended:private 1:created:private", Line((await All(who.Alex, id))!.Value.Items));
            foreach (var reader in new[] { who.Sam, who.House, who.App })
            {
                Assert.Null(await Store.History(reader, id, null, 20, default));
                Assert.Null(await Store.GetVersion(reader, id, 1, default));
            }

            // Shared into Family, then changed there by its author and by an admin.
            await Move(id, "household", who.Alex);
            await Store.Append(who.Alex, id, "DEMO: bring the fictional cake.", null, MemoryLogins.Mcp, default);
            await Store.SetStatus(who.Sam, id, "canonical", null, MemoryLogins.Mcp, default);
            // The author sees all of it.
            var alex = (await All(who.Alex, id))!.Value;
            Assert.Equal("6:status_changed:shared 5:appended:shared 4:shared:shared 3:updated:private 2:appended:private 1:created:private",
                Line(alex.Items));
            Assert.Equal((false, (string?)null, 1), (alex.First.Cut, alex.First.EarlierBy, alex.First.OldestKept));
            Assert.Contains("private worry", alex.Items.Single(i => i.Revision == 2).Preview);
            Assert.Contains("private worry", (await Store.GetVersion(who.Alex, id, 2, default))!.Version!.Content);
            // Everyone else from the share on: the share itself, the state it brought, and what came after; earlier
            // history is the author's, and no word of it is served, by history or by revision.
            foreach (var reader in new[] { who.Sam, who.House, who.App })
            {
                var seen = (await All(reader, id))!.Value;
                Assert.Equal("6:status_changed:shared 5:appended:shared 4:shared:shared", Line(seen.Items));
                Assert.Equal((true, "alex", 4, false), (seen.First.Cut, seen.First.EarlierBy, seen.First.OldestKept, seen.First.Pruned));
                Assert.Equal(("alex", "shared"), (seen.Items[2].Person, seen.Items[2].Event));
                Assert.Equal("DEMO: the fictional party is on Saturday.", seen.Items[2].Preview);
                Assert.DoesNotContain(seen.Items, i => (i.Preview ?? "").Contains("worry") || (i.Preview ?? "").Contains("draft"));
                foreach (var revision in new[] { 1, 2, 3 })
                {
                    var read = (await Store.GetVersion(reader, id, revision, default))!;
                    Assert.Equal(((MemoryVersion?)null, "not_visible", (int?)null), (read.Version, read.Missing, read.OldestKept));
                }
                var shared = (await Store.GetVersion(reader, id, 4, default))!.Version!;
                Assert.Equal(("DEMO: the fictional party is on Saturday.", "household", "alex"), (shared.Content, shared.Subject, shared.Person));
                Assert.True((await Store.GetVersion(reader, id, 6, default))!.Version!.Current);
            }

            // Un-shared: back to its author alone. Shared again: everyone else from the newest share on, the first
            // Family stretch included, since an un-share lies between (as upstream's cut).
            await Move(id, "alex", who.Alex);
            Assert.Null(await Store.History(who.Sam, id, null, 20, default));
            await Move(id, "household", who.Alex);
            var again = (await All(who.Sam, id))!.Value;
            Assert.Equal("8:shared:shared", Line(again.Items));
            Assert.Equal((true, "alex", 8), (again.First.Cut, again.First.EarlierBy, again.First.OldestKept));
            Assert.Equal("not_visible", (await Store.GetVersion(who.Sam, id, 5, default))!.Missing);
            Assert.Equal("8:shared:shared 7:unshared:private 6:status_changed:shared", Line((await All(who.Alex, id))!.Value.Items.Take(3)));
            // Out of Family, Sam reads none of it.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.Null(await Store.History(who.Sam, id, null, 20, default));
            await MemoryLogins.Family(s);

            // A Family memory no person wrote was never private: every reader of Family sees all of it.
            var bins = (await Store.Create(who.House, new("DEMO: the bins go out on Thursday.", Scope: "Family"), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, bins, "DEMO: and the recycling.", null, MemoryLogins.Mcp, default);
            await Store.Append(who.Sam, bins, "DEMO: not on holidays.", null, MemoryLogins.Mcp, default);
            foreach (var reader in new[] { who.Alex, who.Sam, who.House, who.App })
            {
                var all = (await All(reader, bins))!.Value;
                Assert.Equal("3:appended:shared 2:appended:shared 1:created:shared", Line(all.Items));
                Assert.Equal(((string?)null, false), (all.Items[2].Person, all.First.Cut));
            }
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task The_twenty_newest_earlier_states_are_kept_and_every_change_is_still_listed()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "kept");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: day 0 of the fictional diary."), MemoryLogins.Mcp, default)).Id;
        for (var day = 1; day <= 25; day++) await Store.Append(who.Alex, id, $"DEMO: day {day}.", null, MemoryLogins.Mcp, default);
        Assert.Equal("6..25 20", await MemoryLogins.Scalar(s,
            "SELECT min(revision)||'..'||max(revision)||' '||count(*) FROM memory_versions WHERE memory_id=@id", ("id", id)));
        var (first, items) = (await All(who.Sam, id, 7))!.Value;
        Assert.Equal(Enumerable.Range(1, 26).Reverse(), items.Select(i => i.Revision));
        Assert.Equal((26, 20, 6, true, false), (first.Revision, first.Kept, first.OldestKept, first.Pruned, first.Cut));
        // An older change is still listed, from its audit row, without the state it wrote.
        Assert.All(items.Where(i => i.Revision < 6), i => Assert.True(!i.Kept && i.Status is null && i.Preview is null && i.Scope == "shared"));
        Assert.All(items.Where(i => i.Revision >= 6), i => Assert.True(i.Kept && i.Preview is not null));
        Assert.Equal(("created", "alex"), (items[^1].Event, items[^1].Person));
        var pruned = (await Store.GetVersion(who.Sam, id, 3, default))!;
        Assert.Equal(((MemoryVersion?)null, "pruned", (int?)6), (pruned.Version, pruned.Missing, pruned.OldestKept));
        Assert.EndsWith("DEMO: day 5.", (await Store.GetVersion(who.Sam, id, 6, default))!.Version!.Content);

        // Whatever order the states arrive in (a restore's, an import's), one at a time or together, the newest 20 stay.
        foreach (var order in new IEnumerable<int>[] { Enumerable.Range(1, 40), Enumerable.Range(1, 40).Reverse(), [] })
        {
            var many = await MemoryRows.Memory(s, "household", null, "DEMO: many versions.", ",revision", ",41");
            const string Insert = """
                INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,written_at)
                SELECT @m,r,'household','shared','DEMO: version '||r,'draft',now()-interval '1 day' FROM unnest(@r) r
                """;
            if (order.Any()) foreach (var revision in order) await Scoped.Execute(s, Insert, ("m", many), ("r", new[] { revision }));
            else await Scoped.Execute(s, Insert, ("m", many), ("r", Enumerable.Range(1, 40).Reverse().ToArray()));
            Assert.Equal("21..40 20", await MemoryLogins.Scalar(s,
                "SELECT min(revision)||'..'||max(revision)||' '||count(*) FROM memory_versions WHERE memory_id=@id", ("id", many)));
        }
    }

    // History visible from the share on, and older states no longer kept: both at once.
    [PostgresFact] public async Task A_cut_and_a_pruned_stretch_are_told_apart()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "both");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var id = (await Store.Create(who.Alex, new("DEMO: a private fictional list."), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, id, "DEMO: a private addition.", null, MemoryLogins.Mcp, default);
            await Store.Append(who.Alex, id, "DEMO: another.", null, MemoryLogins.Mcp, default);
            await Move(id, "household", who.Alex);
            for (var item = 1; item <= 26; item++) await Store.Append(who.Alex, id, $"DEMO: Family item {item}.", null, MemoryLogins.Mcp, default);
            // Revision 30 now; states 10 to 29 kept. Sam sees from the share (4) on, 4 to 9 no longer kept.
            var (sam, items) = (await All(who.Sam, id, 10))!.Value;
            Assert.Equal(Enumerable.Range(4, 27).Reverse(), items.Select(i => i.Revision));
            Assert.Equal((true, "alex", 10, true), (sam.Cut, sam.EarlierBy, sam.OldestKept, sam.Pruned));
            Assert.Equal("not_visible", (await Store.GetVersion(who.Sam, id, 2, default))!.Missing);
            Assert.Equal(("pruned", (int?)10), ((await Store.GetVersion(who.Sam, id, 5, default))!.Missing, (await Store.GetVersion(who.Sam, id, 5, default))!.OldestKept));
            var (alex, mine) = (await All(who.Alex, id))!.Value;
            Assert.Equal(30, mine.Count);
            Assert.Equal((false, 10, true), (alex.Cut, alex.OldestKept, alex.Pruned));
            Assert.Equal(("pruned", (int?)10), ((await Store.GetVersion(who.Alex, id, 2, default))!.Missing, (await Store.GetVersion(who.Alex, id, 2, default))!.OldestKept));
            Assert.Equal("1:created:private", Line(mine.TakeLast(1)));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // A state that was held is served without its words (D-149), unless a later state the reader sees holds the same
    // text released. The holds are set by hand here, as a person's review will set and release them (SB-P3-18).
    [PostgresFact] public async Task A_held_state_is_served_without_its_words_unless_a_later_one_released_them()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "held");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: fictional text that was held #held", Project: "lockside"), MemoryLogins.Mcp, default)).Id;
        await Store.Update(who.Alex, id, new(Content: "DEMO: fictional text written again."), null, MemoryLogins.Mcp, default);
        await Store.Update(who.Alex, id, new(Tags: ["again"]), null, MemoryLogins.Mcp, default);
        await Scoped.Execute(s, """UPDATE memory_versions SET state=state||'{"hold":"instructions","hold_score":0.9}' WHERE memory_id=@id AND revision IN (1,2)""",
            ("id", id));
        // Revision 1's text is held and no later state holds it released: no words. Revision 2's held text is the
        // current state's, which is not held: released, so its text is served; but the current state's tags are not
        // revision 2's (a change of tags since), so no release vouched for its tags or project: those stay out.
        var history = (await Store.History(who.Sam, id, null, 20, default))!;
        Assert.Equal(((string?)null, true), (history.Items[2].Preview, history.Items[2].Held));
        Assert.Equal(("DEMO: fictional text written again.", false), (history.Items[1].Preview, history.Items[1].Held));
        var held = (await Store.GetVersion(who.Sam, id, 1, default))!.Version!;
        Assert.Equal(((string?)null, true, "instructions", (MemoryProjectRef?)null), (held.Content, held.Held, held.Hold, held.Project));
        Assert.Empty(held.Tags);
        var released = (await Store.GetVersion(who.Sam, id, 2, default))!.Version!;
        Assert.Equal(("DEMO: fictional text written again.", false, "instructions", (MemoryProjectRef?)null), (released.Content, released.Held, released.Hold, released.Project));
        Assert.Empty(released.Tags);
        // The current state held: history and the read by revision serve none of its words; Get is as before.
        await Scoped.Execute(s, "UPDATE memories SET hold='instructions',hold_score=0.9 WHERE id=@id", ("id", id));
        history = (await Store.History(who.Sam, id, null, 20, default))!;
        Assert.Equal(((string?)null, true), (history.Items[0].Preview, history.Items[0].Held));
        Assert.Equal(((string?)null, true), (history.Items[1].Preview, history.Items[1].Held));
        var current = (await Store.GetVersion(who.Sam, id, 3, default))!.Version!;
        Assert.Equal(((string?)null, true, 0), (current.Content, current.Held, current.Tags.Count));
        Assert.Equal("DEMO: fictional text written again.", (await Store.Get(who.Sam, id, default))!.Content);
    }

    // A held state's tags and project come back only with a release that holds them too: the same text, tags and project
    // in a later state that is not held.
    [PostgresFact] public async Task A_released_held_states_tags_and_project_need_the_release_to_hold_them_too()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "held-tags");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: fictional text the review released.", ["cove"], Project: "cove-walks"), MemoryLogins.Mcp, default)).Id;
        await Store.SetStatus(who.Alex, id, "canonical", null, MemoryLogins.Mcp, default);
        await Store.Update(who.Alex, id, new(Tags: ["harbour"]), null, MemoryLogins.Mcp, default);
        Task Hold(string revisions) => Scoped.Execute(s, """UPDATE memory_versions SET state=state||'{"hold":"instructions","hold_score":0.9}' WHERE memory_id=@id AND revision IN (""" + revisions + ")",
            ("id", id));
        // Revision 1 held, revision 2 the same text, tags and project unheld: all of revision 1 is served.
        await Hold("1");
        var one = (await Store.GetVersion(who.Sam, id, 1, default))!.Version!;
        Assert.Equal(("DEMO: fictional text the review released.", false, "cove-walks"), (one.Content, one.Held, one.Project?.Slug));
        Assert.Equal(["cove"], one.Tags);
        // Revision 2 held too: the only later state released holds other tags, so revisions 1 and 2 have their text and
        // neither their tags nor their project.
        await Hold("2");
        foreach (var revision in new[] { 1, 2 })
        {
            var state = (await Store.GetVersion(who.Sam, id, revision, default))!.Version!;
            Assert.Equal(("DEMO: fictional text the review released.", false, (MemoryProjectRef?)null), (state.Content, state.Held, state.Project));
            Assert.Empty(state.Tags);
        }
    }

    // An author who has left Family is cut from the memory's Family stretch, as every reader outside Family is, and the
    // stretch is named as nobody's (EarlierBy names the author only for history in the author's own scope).
    [PostgresFact] public async Task An_author_who_left_Family_is_cut_from_its_Family_stretch_which_is_named_for_no_one()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "left");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var id = (await Store.Create(who.Alex, new("DEMO: a Family fictional list.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, id, "DEMO: a Family addition.", null, MemoryLogins.Mcp, default);
            await Move(id, "alex", who.Alex);
            Assert.Equal((false, (string?)null), ((await All(who.Alex, id))!.Value.First.Cut, (await All(who.Alex, id))!.Value.First.EarlierBy));
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='alex'");
            var (alex, items) = (await All(who.Alex, id))!.Value;
            Assert.Equal((true, (string?)null), (alex.Cut, alex.EarlierBy));
            Assert.Equal([3], items.Select(i => i.Revision));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // Pruning keeps the switch's guard (memory.md 6.2): a memory once private and now in Family refuses rhythm scopes off
    // while its private changes are on the trail, however many later changes pruned its private states.
    [PostgresFact] public async Task Scopes_off_is_refused_while_a_shared_memory_has_private_changes_even_once_pruned()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "guard");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var id = (await Store.Create(who.Alex, new("DEMO: a private fictional start."), MemoryLogins.Mcp, default)).Id;
            await Store.Append(who.Alex, id, "DEMO: a private addition.", null, MemoryLogins.Mcp, default);
            await Move(id, "household", who.Alex);
            for (var item = 1; item <= 22; item++) await Store.Append(who.Alex, id, $"DEMO: Family item {item}.", null, MemoryLogins.Mcp, default);
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_versions WHERE memory_id=@id AND scope='private'", ("id", id)));
            Assert.Contains((await new Principals(s).OffPreview(default)).Private,
                p => p.Person == "alex" && p.What == "earlier private changes of memories now in Family" && p.Count >= 2);
            Assert.NotEmpty(await new Principals(s).SwitchOff(default));
            Assert.Equal("on", await MemoryLogins.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    [PostgresFact] public async Task The_floor_is_the_same_rule_for_every_reader_in_the_database()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "floor");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var id = (await Store.Create(who.Alex, new("DEMO: a private start."), MemoryLogins.Mcp, default)).Id;
            await Move(id, "household", who.Alex);
            async Task<int> Floor(Guid reader) => (int)(await MemoryLogins.Scalar(s, "SELECT memory_history_floor(@p,@id)", ("p", reader), ("id", id)))!;
            Assert.Equal((1, 2, 2, 2), (await Floor(who.Alex), await Floor(who.Sam), await Floor(who.House), await Floor(who.App)));
            // With scopes off every admitted login reads every subject, so everyone sees all of it.
            await Scoped.Mode(s, "off");
            Assert.Equal((1, 1, 1), (await Floor(who.Alex), await Floor(who.Sam), await Floor(who.House)));
            // A login that is not admitted reads none of it.
            var plugin = await Scoped.Principal(s, "floor-plugin", "ingest", plugin: "demo");
            Assert.Equal(3, await Floor(plugin));
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}
