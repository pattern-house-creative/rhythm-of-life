using Knowledge.Adapters;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// SB-P3-06 (docs/dev/memory.md section 5; D-019, D-036, D-065): a forgotten memory leaves every read at once and comes
// back with its links and passages; the author lock on the trash, its list and delete forever; delete forever only from
// the app or the terminal, with the trash row's nonce and a confirmation; and the idempotency keys of a forget and a
// restore. Fictional people only: alex, sam and the household.
public sealed class MemoryTrashTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall App = new("app", "DEMO app");
    static readonly MemoryCall Terminal = new("cli", "DEMO terminal");

    // A person- or AI-made link (D-067) between two catalog rows, as SB-P4-02's functions will write one.
    async Task<Guid> Link(Guid from, Guid to, Guid by, string type = "relates_to")
    {
        var id = Guid.NewGuid();
        await MemoryRows.Run(fixture.Source, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by) VALUES(@id,@s,@t,@type,'verified','DEMO link',@by)
            """, ("id", id), ("s", from), ("t", to), ("type", type), ("by", by));
        return id;
    }

    async Task<long> Links(Guid memory) => (long)(await MemoryLogins.Scalar(fixture.Source,
        "SELECT count(*) FROM made_links WHERE source_id=@m OR target_id=@m", ("m", memory)))!;

    // A memory's star row as the map writes it (RelationshipEngine.Save: the key's catalog id, a snapshot).
    async Task<Guid> Star(Guid memory, string subject, string title)
    {
        var id = Guid.NewGuid();
        await MemoryRows.Run(fixture.Source, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status,relationship_snapshot)
            VALUES(@id,@s,'memory',@e,'memory',CASE WHEN @s='household' THEN 'shared' ELSE 'private' END,@t,'current',jsonb_build_object('Title',@t::text))
            """, ("id", id), ("s", subject), ("e", MemoryStars.Key(subject, memory).CatalogId), ("t", title));
        return id;
    }

    async Task<string> Events(Guid memory) => (string)(await MemoryLogins.Scalar(fixture.Source,
        "SELECT string_agg(event||':'||revision,' ' ORDER BY id) FROM memory_events WHERE memory_id=@m", ("m", memory)))!;

    [PostgresFact] public async Task A_forgotten_memory_leaves_every_read_at_once_and_comes_back_with_its_links_and_passages()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "trip");
        var at = await MemoryLogins.Terminals(s);
        await Scoped.Mode(s, "off");
        var db = RecallRows.Db(s);
        var model = new MeaningModel();
        var covered = PassageSources.Of(false, false, true, []);
        var cat = (await Store.Create(who.Alex, new("DEMO: the Pellow kitten sees the vet on Friday. #pets", Project: "pellow"), MemoryLogins.Mcp, default)).Id;
        var bins = (await Store.Create(who.Sam, new("DEMO: the Pellow bins go out on Thursday."), MemoryLogins.Mcp, default)).Id;
        var note = await Scoped.Row(s, "household", "documents", "shared", "trash-note-" + Guid.NewGuid().ToString("N")[..8]);
        var fromCat = await Link(cat, bins, who.Alex);
        await Link(note, cat, who.Sam, "mentions");
        var unrelated = await Link(bins, note, who.Sam);
        await Passages.Sweep(new EmbeddingBackfill(db), model, covered);
        Assert.Contains(cat, (await Passages.All(s)).Select(p => p.Row));
        var recall = new Recall(db, model, covered);
        async Task<Guid[]> Found(Guid reader) => [.. (await recall.Search(reader, new("Pellow kitten", 20), default)).Hits.Select(h => h.Id)];
        Assert.Contains(cat, await Found(who.Sam));

        // Into the trash, by Sam (scopes off: any admitted login, today's model), at the revision he read.
        Assert.Equal(("conflict", (int?)1), await Refusal(() => Store.Forget(who.Sam, cat, 2, MemoryLogins.Mcp, default)));
        var trashed = await Store.Forget(who.Sam, cat, 1, MemoryLogins.Mcp, default);
        Assert.Equal((cat, 1, "household", "off", "forget", 2, false), (trashed.Id, trashed.Revision, trashed.Subject, trashed.Scopes,
            trashed.Trash.Reason, trashed.Trash.Links, trashed.Replayed));
        Assert.Equal(TimeSpan.FromDays(14), trashed.Trash.PurgeAfter - trashed.Trash.TrashedAt);
        // Every read leaves it at once: get, list, recall, history, the map's star and its passages.
        Assert.Null(await Store.Get(who.Alex, cat, default));
        Assert.DoesNotContain(cat, (await Store.List(who.Alex, new(Limit: 50), default)).Items.Select(i => i.Id));
        Assert.Null(await Store.History(who.Alex, cat, null, 20, default));
        Assert.DoesNotContain(cat, await Found(who.Sam));
        Assert.DoesNotContain(cat, (await Passages.All(s)).Select(p => p.Row));
        Assert.Equal("deleted", await MemoryLogins.Scalar(s, "SELECT status FROM resources WHERE id=@m", ("m", cat)));
        // Its text, versions and project stay in place; its made links wait in the trash row, the others stay.
        Assert.Equal("DEMO: the Pellow kitten sees the vet on Friday. #pets|1", await MemoryLogins.Scalar(s,
            "SELECT m.content||'|'||(m.project_id IS NOT NULL)::int FROM memories m WHERE m.id=@m", ("m", cat)));
        Assert.Equal(0L, await Links(cat));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM made_links WHERE link_id=@l", ("l", unrelated)));
        // A trashed memory is unknown to every change, a second forget included.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Append(who.Alex, cat, "DEMO: more.", null, MemoryLogins.Mcp, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Update(who.Alex, cat, new(Tags: ["x"]), null, MemoryLogins.Mcp, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.SetStatus(who.Alex, cat, "canonical", null, MemoryLogins.Mcp, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Forget(who.Alex, cat, null, MemoryLogins.Mcp, default)));
        // The trash lists it, with its nonce and the days left, for anyone who may restore it.
        var listed = Assert.Single((await Store.Trash(at.Alex, 50, null, 20, Terminal, default)).Items, i => i.Id == cat);
        Assert.Equal((trashed.Trash.Nonce, 14, "forget", "mcp", "sam", "DEMO: the Pellow kit", true, true), (listed.Nonce, listed.DaysLeft,
            listed.Reason, listed.Channel, listed.TrashedBy, listed.Preview, listed.Truncated, listed.MayDeleteForever));
        // Delete forever is offered for the caller's channel: never through an AI client's (D-036), nor on the terminal to a
        // login with no person; the app's login from the app.
        Assert.False(Assert.Single((await Store.Trash(who.Alex, 50, null, 20, MemoryLogins.Mcp, default)).Items, i => i.Id == cat).MayDeleteForever);
        Assert.False(Assert.Single((await Store.Trash(who.House, 50, null, 20, Terminal, default)).Items, i => i.Id == cat).MayDeleteForever);
        Assert.True(Assert.Single((await Store.Trash(who.App, 50, null, 20, App, default)).Items, i => i.Id == cat).MayDeleteForever);

        // A nonce that is not its trash row's restores nothing; its own brings it back with its links.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Restore(who.App, cat, Guid.NewGuid(), App, default)));
        var back = await Store.Restore(who.App, cat, trashed.Trash.Nonce, App, default);
        Assert.Equal((cat, 1, 2, 0, "forget", false), (back.Id, back.Revision, back.Links, back.LinksGone, back.Reason, back.Replayed));
        Assert.Equal("DEMO: the Pellow kitten sees the vet on Friday. #pets", (await Store.Get(who.Sam, cat, default))!.Content);
        Assert.Equal("pellow", (await Store.Get(who.Sam, cat, default))!.Project!.Slug);
        Assert.Equal(2L, await Links(cat));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM made_links WHERE link_id=@l AND source_id=@c", ("l", fromCat), ("c", cat)));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_trash WHERE memory_id=@m", ("m", cat)));
        Assert.DoesNotContain(cat, (await Store.Trash(at.Alex, 50, null, 20, Terminal, default)).Items.Select(i => i.Id));
        // The trip changed no state, so it moved no revision and kept no version; it is on the audit trail and in history.
        Assert.Equal("created:1 trashed:1 restored:1", await Events(cat));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_versions WHERE memory_id=@m", ("m", cat)));
        var history = (await Store.History(who.Alex, cat, null, 20, default))!;
        Assert.Equal(["restored", "trashed", "created"], history.Items.Select(i => i.Event));
        Assert.Equal(("sam", "mcp"), (history.Items[1].Person, history.Items[1].Channel));
        // The index makes its passages again, and search finds it.
        await Passages.Sweep(new EmbeddingBackfill(db), model, covered);
        Assert.Contains(cat, (await Passages.All(s)).Select(p => p.Row));
        Assert.Contains(cat, await Found(who.Sam));
        // A restore of a memory not in the trash is unknown.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Restore(who.App, cat, null, App, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Restore(who.App, Guid.NewGuid(), null, App, default)));
    }

    [PostgresFact] public async Task A_restore_brings_back_each_link_whose_other_end_is_still_there()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links");
        var at = await MemoryLogins.Terminals(s);
        await Scoped.Mode(s, "off");
        async Task<Guid> Memory(string text) => (await Store.Create(who.Alex, new(text), MemoryLogins.Mcp, default)).Id;
        var a = await Memory("DEMO: a fictional plan.");
        var b = await Memory("DEMO: its fictional budget.");
        var c = await Memory("DEMO: a fictional supplier.");
        await Link(a, b, who.Alex);
        await Link(c, a, who.Alex);
        // A trashed, then B: the link between them waits in A's trash row, so B's keeps none.
        var first = await Store.Forget(who.Alex, a, null, MemoryLogins.Mcp, default);
        Assert.Equal(2, first.Trash.Links);
        Assert.Equal(0, (await Store.Forget(who.Alex, b, null, MemoryLogins.Mcp, default)).Trash.Links);
        // C trashed and deleted for good: its link to A can never come back.
        var gone = await Store.Forget(who.Alex, c, null, MemoryLogins.Mcp, default);
        await Store.DeleteForever(at.Alex, c, gone.Trash.Nonce, c.ToString(), Terminal, default);
        // A back: its link to B, still in the trash, returns (served once both ends are); C's does not, and is counted.
        var back = await Store.Restore(who.Alex, a, first.Trash.Nonce, MemoryLogins.Mcp, default);
        Assert.Equal((1, 1), (back.Links, back.LinksGone));
        Assert.Equal(1L, await Links(b));
        Assert.Equal(0, (await Store.Restore(who.Alex, b, null, MemoryLogins.Mcp, default)).Links);
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM made_links WHERE source_id=@a AND target_id=@b", ("a", a), ("b", b)));
    }

    [PostgresFact] public async Task With_scopes_on_the_trash_follows_the_author_lock()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "lock");
        var at = await MemoryLogins.Terminals(s);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            async Task<Guid[]> Listed(Guid reader) => [.. (await Store.Trash(reader, 50, null, 0, MemoryLogins.Mcp, default)).Items.Select(i => i.Id)];
            // Private: its owner only. Sam, made an admin, and the household view: as for an unknown memory.
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            var mine = (await Store.Create(who.Alex, new("DEMO: Alex's private fictional plan."), MemoryLogins.Mcp, default)).Id;
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Forget(who.Sam, mine, null, MemoryLogins.Mcp, default)));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Forget(who.App, mine, null, App, default)));
            var trashed = await Store.Forget(who.Alex, mine, null, MemoryLogins.Mcp, default);
            Assert.Contains(mine, await Listed(who.Alex));
            Assert.DoesNotContain(mine, await Listed(who.Sam));
            Assert.DoesNotContain(mine, await Listed(who.House));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Restore(who.Sam, mine, trashed.Trash.Nonce, MemoryLogins.Mcp, default)));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteForever(at.Sam, mine, trashed.Trash.Nonce, mine.ToString(), Terminal, default)));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, mine, trashed.Trash.Nonce, mine.ToString(), App, default)));
            Assert.Equal(1, (await Store.Restore(who.Alex, mine, trashed.Trash.Nonce, MemoryLogins.Mcp, default)).Revision);
            await Scoped.Execute(s, "UPDATE memberships SET role='member' WHERE person_id='sam'");

            // Family, written by Alex: its author or an admin.
            var cafe = (await Store.Create(who.Alex, new("DEMO: Alex shared the fictional cafe.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Forget(who.Sam, cafe, null, MemoryLogins.Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Forget(who.House, cafe, null, MemoryLogins.Mcp, default)));
            var shared = await Store.Forget(who.Alex, cafe, null, MemoryLogins.Mcp, default);
            Assert.DoesNotContain(cafe, await Listed(who.Sam));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Restore(who.Sam, cafe, null, MemoryLogins.Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteForever(at.Sam, cafe, shared.Trash.Nonce, cafe.ToString(), Terminal, default)));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Contains(cafe, await Listed(who.Sam));
            Assert.Equal(0, (await Store.Restore(who.Sam, cafe, shared.Trash.Nonce, MemoryLogins.Mcp, default)).Links);
            await Scoped.Execute(s, "UPDATE memberships SET role='member' WHERE person_id='sam'");

            // Family, written by no person: any reader of Family trashes and restores it, but only an admin deletes it forever.
            var bins = (await Store.Create(who.House, new("DEMO: the bins go out on Thursday.", Scope: "Family"), MemoryLogins.Mcp, default)).Id;
            var binned = await Store.Forget(who.Sam, bins, null, MemoryLogins.Mcp, default);
            var item = Assert.Single((await Store.Trash(at.Alex, 50, null, 0, Terminal, default)).Items, i => i.Id == bins);
            Assert.False(item.MayDeleteForever);
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteForever(at.Alex, bins, binned.Trash.Nonce, bins.ToString(), Terminal, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, bins, binned.Trash.Nonce, bins.ToString(), App, default)));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='alex'");
            Assert.True(Assert.Single((await Store.Trash(at.Alex, 50, null, 0, Terminal, default)).Items, i => i.Id == bins).MayDeleteForever);
            Assert.Equal(bins, (await Store.DeleteForever(at.Alex, bins, binned.Trash.Nonce, bins.ToString(), Terminal, default)).Id);
            // Out of Family, a person reads none of Family's trash.
            var lunch = (await Store.Create(who.House, new("DEMO: lunch is at one.", Scope: "Family"), MemoryLogins.Mcp, default)).Id;
            await Store.Forget(who.Alex, lunch, null, MemoryLogins.Mcp, default);
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.DoesNotContain(lunch, await Listed(who.Sam));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Restore(who.Sam, lunch, null, MemoryLogins.Mcp, default)));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task Delete_forever_works_only_from_the_app_or_the_terminal_with_the_nonce_and_a_confirmation()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "forever");
        var at = await MemoryLogins.Terminals(s);
        await Scoped.Mode(s, "off");
        var plan = (await Store.Create(who.Alex, new("DEMO: the fictional Tamsin plan, first draft. #plans"), MemoryLogins.Mcp, default)).Id;
        await Store.Append(who.Alex, plan, "DEMO: second thoughts.", null, MemoryLogins.Mcp, default);
        var other = (await Store.Create(who.Alex, new("DEMO: another fictional note."), MemoryLogins.Mcp, default)).Id;
        await Link(plan, other, who.Alex);
        var star = await Star(plan, "household", "DEMO: the fictional Tamsin plan");
        Assert.Equal(MemoryStars.Key("household", plan).CatalogId, await MemoryLogins.Scalar(s, "SELECT memory_star_external(@m)", ("m", plan)));
        // A live memory is forgotten first: no nonce reaches it.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, plan, Guid.NewGuid(), plan.ToString(), App, default)));
        var first = await Store.Forget(who.Alex, plan, null, MemoryLogins.Mcp, default);
        // Back and into the trash again: the first trip's nonce names nothing now.
        await Store.Restore(who.Alex, plan, first.Trash.Nonce, MemoryLogins.Mcp, default);
        var trashed = await Store.Forget(who.App, plan, null, App, default);
        Assert.NotEqual(first.Trash.Nonce, trashed.Trash.Nonce);
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, plan, first.Trash.Nonce, plan.ToString(), App, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, other, trashed.Trash.Nonce, other.ToString(), App, default)));
        // Never through an AI client's channel, nor without the confirmation, nor with an idempotency key.
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteForever(who.Alex, plan, trashed.Trash.Nonce, plan.ToString(), MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, plan, trashed.Trash.Nonce, "yes", App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, plan, trashed.Trash.Nonce, plan.ToString(), new("app", null, "forever-key-01"), default)));
        // Without a login only the operator on the host acts, on the terminal's channel.
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.DeleteForever(null, plan, trashed.Trash.Nonce, plan.ToString(), App, default)));
        // Nothing was deleted by any of those.
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE id=@m", ("m", plan)));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_versions WHERE memory_id=@m", ("m", plan)));

        var deleted = await Store.DeleteForever(who.App, plan, trashed.Trash.Nonce, $" {plan} ", App, default);
        Assert.Equal((plan, "household", 1, 1, "off"), (deleted.Id, deleted.Subject, deleted.Links, deleted.Versions, deleted.Scopes));
        // Gone: the memory, its versions and trash row, its catalog row, its star row, its passages and its links.
        foreach (var (sql, what) in new[] {
            ("SELECT count(*) FROM memories WHERE id=@m", "memories"), ("SELECT count(*) FROM memory_versions WHERE memory_id=@m", "versions"),
            ("SELECT count(*) FROM memory_trash WHERE memory_id=@m", "trash"), ("SELECT count(*) FROM resources WHERE id=@m OR id=@star", "rows"),
            ("SELECT count(*) FROM resource_chunks WHERE resource_id=@m", "passages"), ("SELECT count(*) FROM made_links WHERE source_id=@m OR target_id=@m", "links") })
            Assert.Equal((what, 0L), (what, (long)(await MemoryLogins.Scalar(s, sql, ("m", plan), ("star", star)))!));
        // The audit trail stays, with the delete named and none of the memory's words.
        Assert.Equal("created:1 appended:2 trashed:2 restored:2 trashed:2 purged:2", await Events(plan));
        Assert.Equal("app|DEMO app|permanent|1|1", await MemoryLogins.Scalar(s, """
            SELECT channel||'|'||client||'|'||(detail->>'reason')||'|'||(detail->>'links')||'|'||(detail->>'versions')
            FROM memory_events WHERE memory_id=@m AND event='purged'
            """, ("m", plan)));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_events WHERE memory_id=@m AND (detail::text ILIKE '%tamsin%' OR detail::text ILIKE '%thoughts%')",
            ("m", plan)));
        Assert.Null(await Store.Get(who.Alex, plan, default));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Restore(who.App, plan, null, App, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DeleteForever(who.App, plan, trashed.Trash.Nonce, plan.ToString(), App, default)));

        // From the terminal by its person (the terminal's own login, 044), and by the operator on the host. Never with an AI
        // client's token through the terminal's channel, a person-bound one included.
        var refused = (await Store.Create(who.Sam, new("DEMO: a fictional note to keep."), MemoryLogins.Mcp, default)).Id;
        var kept = await Store.Forget(who.Sam, refused, null, MemoryLogins.Mcp, default);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.DeleteForever(who.Sam, refused, kept.Trash.Nonce, refused.ToString(), Terminal, default)));
        foreach (Guid? by in new Guid?[] { at.Sam, null })
        {
            var id = (await Store.Create(who.Sam, new("DEMO: a fictional note to delete."), MemoryLogins.Mcp, default)).Id;
            var bin = await Store.Forget(who.Sam, id, null, MemoryLogins.Mcp, default);
            Assert.Equal(id, (await Store.DeleteForever(by, id, bin.Trash.Nonce, id.ToString(), Terminal, default)).Id);
            Assert.Equal((by is null ? "" : by.ToString()) + "|cli", await MemoryLogins.Scalar(s,
                "SELECT coalesce(principal_id::text,'')||'|'||channel FROM memory_events WHERE memory_id=@m AND event='purged'", ("m", id)));
        }
        // A terminal login with no person is no person's terminal.
        var bound = (await Store.Create(who.Sam, new("DEMO: one more."), MemoryLogins.Mcp, default)).Id;
        var bound_ = await Store.Forget(who.Sam, bound, null, MemoryLogins.Mcp, default);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.DeleteForever(who.House, bound, bound_.Trash.Nonce, bound.ToString(), Terminal, default)));
    }

    [PostgresFact] public async Task A_retried_forget_or_restore_with_its_idempotency_key_is_answered_again()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "keys");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: a fictional note."), MemoryLogins.Mcp, default)).Id;
        var forget = await Store.Forget(who.Alex, id, 1, MemoryLogins.Key("trash-key-0001"), default);
        var again = await Store.Forget(who.Alex, id, 1, MemoryLogins.Key("trash-key-0001"), default);
        Assert.True(again.Replayed);
        Assert.Equal(forget.Trash.Nonce, again.Trash.Nonce);
        Assert.Equal("trashed:1", (await Events(id)).Split(' ')[1]);
        Assert.Equal(2, (await Events(id)).Split(' ').Length);
        // The same key for another request is refused; another login's key is its own.
        Assert.Equal("key_reused", await MemoryLogins.Refused(() => Store.Restore(who.Alex, id, null, MemoryLogins.Key("trash-key-0001"), default)));
        var restore = await Store.Restore(who.Alex, id, forget.Trash.Nonce, MemoryLogins.Key("trash-key-0002"), default);
        Assert.True((await Store.Restore(who.Alex, id, forget.Trash.Nonce, MemoryLogins.Key("trash-key-0002"), default)).Replayed);
        Assert.False(restore.Replayed);
        Assert.Equal("created:1 trashed:1 restored:1", await Events(id));
        Assert.Equal("forget,restore", await MemoryLogins.Scalar(s,
            "SELECT string_agg(operation,',' ORDER BY idempotency_key) FROM memory_requests WHERE principal_id=@p", ("p", who.Alex)));
    }

    // The engine's login trashes, restores and deletes only through 038's functions, and runs the purge.
    [PostgresFact] public async Task The_engine_login_writes_the_trash_only_through_the_functions()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "trash-login");
        await Scoped.Mode(s, "off");
        await using var login = await TestLogin.Make(s, fixture.Name);
        var store = new MemoryStore(login.Source);
        var id = (await store.Create(who.Alex, new("DEMO: through the engine's login."), MemoryLogins.Mcp, default)).Id;
        var trashed = await store.Forget(who.Alex, id, null, MemoryLogins.Mcp, default);
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "DELETE FROM memory_trash WHERE memory_id=@m", ("m", id)));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE memory_trash SET purge_after=now() WHERE memory_id=@m", ("m", id)));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE resources SET status='current' WHERE id=@m", ("m", id)));
        // A helper called directly runs with the login's own rights, so it deletes nothing.
        Assert.Equal("42501", await MemoryRows.Fails(login.Source,
            "SELECT memory_purge(m,t,NULL,'system',NULL,'memory-trash','retention') FROM memories m JOIN memory_trash t ON t.memory_id=m.id WHERE m.id=@m", ("m", id)));
        Assert.Equal(0, (await MemoryTrash.Purge(login.Source, default)).Purged);
        await store.Restore(who.App, id, trashed.Trash.Nonce, App, default);
        var again = await store.Forget(who.Alex, id, null, MemoryLogins.Mcp, default);
        Assert.Equal(id, (await store.DeleteForever(who.App, id, again.Trash.Nonce, id.ToString(), App, default)).Id);
    }

    static async Task<(string, int?)> Refusal(Func<Task> act)
    {
        var refused = await Assert.ThrowsAsync<MemoryRefused>(act);
        return (refused.Code, refused.Revision);
    }
}

// The daily jobs this program adds (Scheduler: MemoryTrash.Purge, and RecallLog.Prune of SB-P2-11), in a database of its
// own: the purge deletes for good only native memories whose trash retention has passed, a bounded number a run, oldest
// first, as the job memory-trash on the audit trail, and prunes idempotency keys older than 30 days; the recall log's prune
// only its rows and counts older than 30 days, once the log has been on. Neither deletes anything an existing install has
// today: a migrated install with the demo's records, a map pass, documents, links and the start's audit rows is the same,
// table for table and row for row, after both run.
public sealed class MemoryTrashPurgeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    // Every table of the schema, each as a hash of its rows.
    async Task<Dictionary<string, string>> Tables()
    {
        var names = new List<string>();
        await using (var cmd = fixture.Source.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname=current_schema() ORDER BY 1"))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
            hashes[name] = (string)(await MemoryLogins.Scalar(fixture.Source,
                $"SELECT count(*)||':'||coalesce(md5(string_agg(x::text,E'\\n' ORDER BY x::text)),'') FROM \"{name}\" x"))!;
        return hashes;
    }

    static string[] Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        [.. after.Keys.Union(before.Keys).Where(t => before.GetValueOrDefault(t) != after.GetValueOrDefault(t)).Order(StringComparer.Ordinal)];

    // Ages a trash row: trashed `days` ago with the 14 days' retention, so its purge date is past when days > 14.
    Task Age(Guid memory, int days) => MemoryRows.Run(fixture.Source,
        "UPDATE memory_trash SET trashed_at=now()-make_interval(days=>@d),purge_after=now()-make_interval(days=>@d)+interval '14 days' WHERE memory_id=@m",
        ("m", memory), ("d", days));

    [PostgresFact] public async Task The_purge_deletes_only_memories_past_their_retention_and_nothing_an_install_had_before()
    {
        var s = fixture.Source;
        // An existing install: the demo's records and the map's pass over them, a document of each scope, a link the map
        // found and one ingest made, an old ingest key, and the start's audit rows.
        var config = TestSettings.Use();
        var registry = new Adapters.AdapterRegistry([new Adapters.Demo.DemoAdapter()]);
        var archive = new Archive(Path.Combine(Path.GetTempPath(), "rhythm-trash-" + Guid.NewGuid().ToString("N")[..8]));
        registry.Enabled[0].Configure(new(config.Adapter("demo"), config, fixture.Db, archive));
        var ingest = new Ingest(fixture.Db, archive, config);
        await ((Adapters.Demo.DemoAdapter)registry.Enabled[0]).Sync(new MeasurementSink(ingest, registry.Enabled[0]), default);
        var principals = new Principals(s);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        var family = await Scoped.Row(s, "household", "documents", "shared", "purge-family-doc");
        var alex = await Scoped.Row(s, "alex", "drive", "private", "purge-alex-doc", status: "deleted");
        await MemoryRows.Run(s, """
            INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine) VALUES(@a,@b,'references','verified','DEMO','cross-source-v1');
            INSERT INTO ingest_calls(plugin,idempotency_key,tool,payload_hash,provider,subject_id,result,received_at)
            VALUES('demo-plugin','purge-old-ingest','ingest_records',repeat('0',64),'documents','household','{}',now()-interval '200 days')
            """, ("a", family), ("b", alex));
        Assert.True((long)(await MemoryLogins.Scalar(s, "SELECT count(*) FROM resources"))! > 5);
        var before = await Tables();
        var run = await MemoryTrash.Purge(s, default);
        Assert.Equal(new MemoryPurge(0, 0, 0, 0), run);
        Assert.False(run.Worth);
        Assert.Equal((0, 0), await RecallLog.Prune(s, default));
        Assert.Empty(Changed(before, await Tables()));

        // Now native memories: one in the trash for a day, two past their retention, one live, and two idempotency keys.
        var who = await MemoryLogins.Make(s, "purge");
        await Scoped.Mode(s, "off");
        async Task<Guid> Trashed(string text, int days)
        {
            var id = (await Store.Create(who.Alex, new(text), MemoryLogins.Key("purge-key-" + Guid.NewGuid().ToString("N")[..8]), default)).Id;
            await Store.Update(who.Alex, id, new(Tags: ["kept-version"]), null, MemoryLogins.Mcp, default);
            await Store.Forget(who.Alex, id, null, MemoryLogins.Mcp, default);
            await Age(id, days);
            return id;
        }
        var recent = await Trashed("DEMO: trashed yesterday.", 1);
        var older = await Trashed("DEMO: trashed twenty days ago.", 20);
        var oldest = await Trashed("DEMO: trashed thirty days ago.", 30);
        var live = (await Store.Create(who.Alex, new("DEMO: still here."), MemoryLogins.Mcp, default)).Id;
        var star = Guid.NewGuid();
        await MemoryRows.Run(s, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status,relationship_snapshot)
            VALUES(@id,'household','memory',@e,'memory','shared','DEMO: trashed thirty days ago','missing',jsonb_build_object('Title','DEMO'))
            """, ("id", star), ("e", MemoryStars.Key("household", oldest).CatalogId));
        await MemoryRows.Run(s, "UPDATE memory_requests SET received_at=now()-interval '31 days' WHERE memory_id=@m", ("m", recent));
        before = await Tables();

        // Bounded: one batch of one takes the oldest only, and says it stopped at its bound with more left.
        Assert.Equal(new MemoryPurge(1, 0, 0, 1, More: true), await MemoryTrash.Purge(s, default, batch: 1, batches: 1));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE id=@m", ("m", oldest)));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE id=@m", ("m", older)));
        // Then the rest past retention; the one trashed yesterday, the live one and every other row stay.
        Assert.Equal(new MemoryPurge(1, 0, 0, 0), await MemoryTrash.Purge(s, default));
        Assert.Equal(new MemoryPurge(0, 0, 0, 0), await MemoryTrash.Purge(s, default));
        Assert.Equal(["memories", "memory_events", "memory_requests", "memory_trash", "memory_versions", "resources"], Changed(before, await Tables()));
        Assert.Equal(2L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE id=ANY(@m)", ("m", new[] { recent, live })));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM resources WHERE id=ANY(@m)", ("m", new[] { older, oldest, star })));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_trash WHERE memory_id=@m", ("m", recent)));
        Assert.Equal("0|1", await MemoryLogins.Scalar(s, """
            SELECT (SELECT count(*) FROM memory_requests WHERE memory_id=@r)||'|'||(SELECT count(*) FROM memory_requests WHERE memory_id=@o)
            """, ("r", recent), ("o", older)));
        // Each on the audit trail as the job's, with no principal and none of its words.
        Assert.Equal("purged|system|memory-trash||retention|1", await MemoryLogins.Scalar(s, """
            SELECT event||'|'||channel||'|'||job||'|'||coalesce(principal_id::text,'')||'|'||(detail->>'reason')||'|'||(detail->>'versions')
            FROM memory_events WHERE memory_id=@m AND event='purged'
            """, ("m", oldest)));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_events WHERE detail::text LIKE '%days ago%'"));
        // A memory a writer holds right now is left for the next run.
        await Age(recent, 15);
        await using (var c = await s.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await Database.Execute(c, tx, $"SELECT 1 FROM resources WHERE id='{recent}' FOR UPDATE", default);
            Assert.Equal(new MemoryPurge(0, 0, 1, 0), await MemoryTrash.Purge(s, default));
            await tx.RollbackAsync();
        }
        Assert.Equal(new MemoryPurge(1, 0, 0, 0), await MemoryTrash.Purge(s, default));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE id=@m", ("m", live)));
        Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT purge_memory_trash(0)"));
        Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT prune_memory_requests(10001)"));

        // The recall log, once turned on (SB-P2-11): its daily prune drops rows older than 30 days and counts untouched as
        // long, and nothing else.
        await MemoryRows.Run(s, """
            INSERT INTO recall_log(subject_id,channel,query,returned,receipt,at) VALUES
              ('alex','mcp','DEMO old question',ARRAY[@m]::uuid[],'rc1-0000000000000010',now()-interval '31 days'),
              ('alex','mcp','DEMO new question',ARRAY[@m]::uuid[],'rc1-0000000000000011',now()-interval '1 day');
            INSERT INTO recall_counts(subject_id,resource_id,recalled,last_at) VALUES('alex',@m,1,now()-interval '31 days'),('sam',@m,1,now())
            """, ("m", live));
        before = await Tables();
        Assert.Equal((1, 1), await RecallLog.Prune(s, default));
        Assert.Equal((0, 0), await RecallLog.Prune(s, default));
        Assert.Equal(["recall_counts", "recall_log"], Changed(before, await Tables()));
        Assert.Equal("DEMO new question|sam", await MemoryLogins.Scalar(s,
            "SELECT (SELECT string_agg(query,',') FROM recall_log)||'|'||(SELECT string_agg(subject_id,',') FROM recall_counts)"));
        Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT prune_recall_log(0)"));
    }
}
