using Knowledge.Adapters;
using Knowledge.Cli;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// Offboarding a person (SB-P7-13; migration 072, Core/Offboarding.cs, Cli/OffboardCommand.cs; docs/dev/memory.md section 4):
// the operator's step on the host. With scopes on: the person's access ends at once (suspended, every token and session
// credential bound to them refused, out of Family, their shares ended), their private memories go to the trash with its
// usual grace and nothing is deleted then, the Family memories they wrote stay with them as their author, the purge then
// deletes what waited and the daily turn closes it. A run stopped part way goes on from where it stopped; brought back in
// the grace, the person restores what they want and the offboarding waits until the operator runs it again, even once
// they are suspended or former again. With scopes off it ends the access bound to them and moves nothing, and the
// switch's first start leaves them out of Family, brought back or not. What is bound to no person stays, and the preview
// names it. Each test makes its own throwaway install on the test server. Fictional people only: alex, sam and the
// household; every text is DEMO.
public sealed class OffboardingTests
{
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;

    static async Task<(int Code, string Said)> Offboard(NpgsqlDataSource s, string typed, int batch = Offboardings.Batch, int? batches = null)
    {
        var o = new StringWriter();
        var code = await OffboardCommand.Run(["sam"], new StringReader(typed), o, s, batch, batches);
        return (code, o.ToString());
    }

    static async Task<string> Person(NpgsqlDataSource s, params string[] args)
    {
        var o = new StringWriter();
        Assert.Equal(0, await TokenCommand.Person(args, o, new Principals(s)));
        return o.ToString();
    }

    // A token file for `person`, made as rhythm token create makes it, and its words.
    static async Task<string> Token(NpgsqlDataSource s, string folder, string name, string? person)
    {
        await new Principals(s).Create(name, "harness", null, person, Path.Combine(folder, name), default);
        return File.ReadAllText(Path.Combine(folder, name));
    }

    static async Task<long> Mark(NpgsqlDataSource s) => (long)(await MemoryRows.Scalar(s, "SELECT coalesce(max(id),0) FROM admin_events"))!;
    static async Task<string> AdminSince(NpgsqlDataSource s, long mark) => (string)(await MemoryRows.Scalar(s, """
        SELECT coalesce(string_agg(event||' '||coalesce(person_id,'-')||' '||channel||' '||coalesce(job,'-'),'|' ORDER BY id),'')
        FROM admin_events WHERE id>@m AND event NOT LIKE 'token\_%'
        """, ("m", mark)))!;

    // Story: Sam leaves, with scopes on. Sam is a Family admin with a phone token, a viewer session, a capture connection
    // whose mirror lands in their private scope, the terminal's login, a source share and a one-time viewer code not used
    // yet; three private memories (two linked, one in a project) and the mirror, an empty private project, one Family
    // memory and a nightscout record of their own. Alex has a private memory and a token of her own.
    [PostgresFact] public async Task With_scopes_on_their_access_ends_their_private_memories_go_to_the_trash_and_Familys_stay_attributed()
    {
        await using var install = await MemoryArchiveTests.Install.Make("on");
        var s = install.Source;
        var store = install.Store;
        var who = install.Who;
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-offboard-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var samPhone = await Token(s, folder, "sam-phone", "sam");
            var alexPhone = await Token(s, folder, "alex-phone", "alex");
            // A household token and a plugin's, bound to no person: the offboarding ends neither, and names both.
            var tablet = await Token(s, folder, "household-tablet", null);
            await new Principals(s).Create("plugin:demo-pump", "ingest", "demo-pump", null, Path.Combine(folder, "demo-pump"), default);
            await Scoped.Principal(s, "viewer-sam", "app", "sam");
            var feed = await new Principals(s).CreateCapture("sam-feed", "sam", Path.Combine(folder, "sam-feed"), default);
            Assert.NotEqual(Guid.Empty, await MemoryLogins.Terminal(s, "sam"));
            await install.Run("""
                UPDATE memberships SET role='admin' WHERE person_id='sam';
                INSERT INTO source_shares(person_id,provider) VALUES('sam','nightscout');
                INSERT INTO viewer_codes(code_hash,person_id,expires_at) VALUES('demo-offboard-code','sam',now()+interval '30 minutes');
                INSERT INTO memory_projects(id,subject_id,slug,name,author_id) VALUES(gen_random_uuid(),'sam','empty-box','DEMO empty box','sam')
                """);
            await Scoped.Row(s, "sam", "nightscout", "private", "fx-offboard-ns-1");
            // A copy Sam's AI client saved (save_document, the core's own provider) and a question of Sam's in the recall log.
            await Scoped.Row(s, "sam", "documents", "private", "fx-offboard-doc-1");
            await install.Run("INSERT INTO recall_log(subject_id,channel,query,returned,receipt) VALUES('sam','mcp','DEMO fictional question',ARRAY[]::uuid[],'rc1-00000000000000aa')");
            var diary = (await store.Create(who.Sam, new("DEMO: Sam's fictional diary line."), Mcp, default)).Id;
            var plan = (await store.Create(who.Sam, new("DEMO: Sam's fictional plan for the shed.", Project: "shed"), Mcp, default)).Id;
            var note = (await store.Create(who.Sam, new("DEMO: Sam's fictional note about the plan."), Mcp, default)).Id;
            await install.Run("INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by) VALUES(gen_random_uuid(),@n,@p,'relates_to','verified','DEMO link',@by)",
                ("n", note), ("p", plan), ("by", who.Sam));
            var mirror = (await store.Capture(feed, new("DEMO: a fictional tracker item of Sam's.", Mirror: "tracker:item/1"), "sam-feed", null, default)).Id;
            var family = (await store.Create(who.Sam, new("DEMO: the fictional bins go out on Thursday.", Scope: "shared"), Mcp, default)).Id;
            var alexs = (await store.Create(who.Alex, new("DEMO: Alex's own fictional note."), Mcp, default)).Id;
            Guid[] privates = [diary, plan, note, mirror];
            Assert.Equal("sam", (await store.Get(who.Sam, mirror, default))!.Subject);
            var tokens = (long)(await install.Scalar("SELECT count(*) FROM principals WHERE person_id='sam' AND enabled"))!;
            Assert.Equal(5L, tokens);
            var mark = await Mark(s);

            // Anything but the id typed changes nothing.
            var (code, said) = await Offboard(s, "no\n");
            Assert.Equal(1, code);
            Assert.Contains("Nothing changed: sam is as they were.", said);
            Assert.NotNull(await new Database(s).Authenticate(samPhone, default));
            Assert.Equal("current", await install.Scalar("SELECT status FROM subjects WHERE id='sam'"));
            Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM offboardings"));

            (code, said) = await Offboard(s, "sam\n");
            Assert.Equal(0, code);
            // What it says first, as counts: what it does and what it leaves.
            Assert.Contains("Offboarding sam ends the access bound to them at once and keeps what the household shares; the last lines say what it does not end.", said);
            Assert.Contains("nothing bound to sam reads or writes from now on: suspended, 5 token(s) and session(s) bound to them disabled for good, "
                + "out of Family with their admin role, 1 source share(s) ended.", said);
            Assert.Contains("Their 4 private memories go to the trash, as a forget puts one there: nothing is deleted now. The trash keeps them 14 days, "
                + "then the daily purge deletes them for good, and their 2 private project(s) with them.", said);
            Assert.Contains("the 1 Family memories sam wrote, in Family with sam as their author;", said);
            Assert.Contains("Brought back in that time (./rhythm person unsuspend sam), sam restores what they want with a new token, and stays out of "
                + "Family until ./rhythm family add sam, their source shares ended until ./rhythm share add sam <provider>.", said);
            Assert.Contains("their own records from adapters and plugins (nightscout 1), kept as their source keeps them: nobody reads them with memory scopes on while sam is suspended or former;", said);
            Assert.Contains("the records of theirs saved in Rhythm itself (documents 1: save_document copies, uploads and dashboard log entries), kept as they are: "
                + "nobody reads them with memory scopes on while sam is suspended or former;", said);
            Assert.Contains("the recall log's 1 row(s) of their questions as they asked them (engine.recallLog), kept in their own scope until the daily prune deletes each 30 days after it was asked;", said);
            // What it does not end, last, after what it leaves: the tokens bound to no person (the app's own is not one of
            // them), the app, and the plugin writing Sam's nightscout records.
            Assert.Contains("What it does not end:\n  3 token(s) bound to no person, which stay enabled (archive-house, household-tablet, plugin:demo-pump): "
                + "if sam used one, ./rhythm token revoke <name>, and make whoever else used it a new one;\n"
                + "  the app, which has no login of its own: whoever reaches it reads what the household reads, so keep sam off the network it listens on;\n"
                + "  an adapter or plugin reading sam's own account (nightscout) keeps adding records until you disconnect it: ./rhythm disconnect "
                + "<adapter>, or for a plugin its own settings or ./rhythm token revoke plugin:<name>, which stops it for everyone; and give an adapter "
                + "whose owner is sam another owner before you take sam out of people, or ./rhythm up stops on it.\n", said.Replace("\r\n", "\n"));
            Assert.True(said.IndexOf("What it leaves:", StringComparison.Ordinal) < said.IndexOf("What it does not end:", StringComparison.Ordinal));
            Assert.DoesNotContain("archive-app", said);
            Assert.DoesNotContain("DEMO", said);
            // What it did.
            Assert.Contains("Done at once: sam is suspended; 5 token(s) and session(s) disabled; out of Family (an admin before); 1 source share(s) ended; "
                + "1 one-time viewer code(s) removed.", said);
            Assert.Contains("Moved 4 private memories to the trash: nothing is deleted yet", said);
            Assert.Contains("Deleted 1 private project(s) with no memory left in them.", said);
            Assert.Contains("The rest finishes on its own: the 4 memories of theirs in the trash wait there until", said);
            Assert.Contains("and then the daily jobs delete their 1 private project(s) and record the offboarding finished.", said);
            Assert.Contains("sam is still listed in people (rhythm.yaml): take them out and run ./rhythm up, or they stay listed, suspended. "
                + "Give any adapter whose owner is sam another owner first, or ./rhythm up stops on it.", said);
            Assert.Contains("Family has no admin now", said);

            // Every token and session credential bound to Sam is refused from now on, and none can be made; Alex's works.
            var db = new Database(s);
            Assert.Null(await db.Authenticate(samPhone, default));
            Assert.False(await new Principals(s).Enabled(samPhone, default));
            Assert.NotNull(await db.Authenticate(alexPhone, default));
            // As the preview said, a token bound to no person still works: the operator revokes it if Sam held it.
            Assert.NotNull(await db.Authenticate(tablet, default));
            Assert.Equal(2L, await install.Scalar("SELECT count(*) FROM principals WHERE name IN ('household-tablet','plugin:demo-pump') AND enabled"));
            Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM principals WHERE person_id='sam' AND enabled"));
            Assert.Contains("is suspended", (await Assert.ThrowsAsync<ArgumentException>(() =>
                new Principals(s).Create("sam-laptop", "harness", null, "sam", Path.Combine(folder, "sam-laptop"), default))).Message);
            await Assert.ThrowsAsync<ArgumentException>(() => TerminalLogin.For(s, "sam", default));
            Assert.Equal("suspended|0|0|0", await install.Scalar("""
                SELECT (SELECT status FROM subjects WHERE id='sam')||'|'||(SELECT count(*) FROM memberships WHERE person_id='sam')
                  ||'|'||(SELECT count(*) FROM source_shares WHERE person_id='sam')||'|'||(SELECT count(*) FROM viewer_codes WHERE person_id='sam')
                """));

            // Each private memory is in the trash, reason offboard, the operator's (no principal, channel cli), its row and
            // words kept; no read serves it; its links wait in the trash row; the mirror is an ordinary memory there.
            foreach (var id in privates)
            {
                Assert.Equal("offboard|cli|-|deleted|1", await install.Scalar("""
                    SELECT t.reason||'|'||t.channel||'|'||coalesce(t.trashed_by::text,'-')||'|'||r.status||'|'
                      ||(SELECT count(*) FROM memory_events e WHERE e.memory_id=t.memory_id AND e.event='trashed' AND e.principal_id IS NULL
                        AND e.channel='cli' AND e.detail->>'reason'='offboard')
                    FROM memory_trash t JOIN resources r ON r.id=t.memory_id WHERE t.memory_id=@id
                    """, ("id", id)));
                Assert.StartsWith("DEMO", (string)(await install.Scalar("SELECT content FROM memories WHERE id=@id", ("id", id)))!);
                Assert.Null(await store.Get(who.Alex, id, default));
                Assert.Null(await store.Get(who.House, id, default));
            }
            Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM memory_mirrors"));
            Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM made_links WHERE source_id=ANY(@ids) OR target_id=ANY(@ids)", ("ids", privates)));
            // The link went with the first of its two ends into the trash, as a forget takes it, to come back with that one.
            Assert.Equal(1L, await install.Scalar("SELECT sum(jsonb_array_length(links)) FROM memory_trash WHERE memory_id=ANY(@ids)", ("ids", privates)));
            // The Family memory Sam wrote stays in Family with Sam as its author, read by Alex; Alex's own is untouched.
            var kept = (await store.Get(who.Alex, family, default))!;
            Assert.Equal(("household", "sam", "DEMO: the fictional bins go out on Thursday."), (kept.Subject, kept.Author, kept.Content));
            Assert.Equal("sam", (await store.Get(who.House, family, default))!.Author);
            Assert.NotNull(await store.Get(who.Alex, alexs, default));
            // The admin audit has the operator's changes, the offboarding's start among them.
            Assert.Equal("person_suspended sam cli -|family_left sam cli -|source_unshared sam cli -|offboarding_started sam cli -", await AdminSince(s, mark));
            Assert.Equal(5L, await install.Scalar("SELECT count(*) FROM admin_events WHERE id>@m AND event='token_disabled' AND channel='cli'", ("m", mark)));
            Assert.Contains("sam\tsuspended\tnot in Family\t0 token(s)\toffboarding since ", await Person(s, "list"));
            Assert.Equal("4|1|-", await install.Scalar("SELECT trashed||'|'||projects||'|'||coalesce(finished_at::text,'-') FROM offboardings WHERE person_id='sam'"));

            // Before the trash's retention has passed, the daily turn finds nothing to close.
            var log = new LoggedLines<Scheduler>();
            Assert.Equal((0, 0, 1), await Offboardings.Daily(s, log, default));
            Assert.Empty(log.Lines);
            // Past it, the purge deletes them for good, and the daily turn then deletes the project they were in and records
            // the offboarding finished, as its own job.
            await install.Run("UPDATE memory_trash SET trashed_at=now()-interval '15 days',purge_after=now()-interval '1 day' WHERE memory_id=ANY(@ids)", ("ids", privates));
            Assert.Equal(4, (await MemoryTrash.Purge(s, default)).Purged);
            Assert.Equal((1, 1, 0), await Offboardings.Daily(s, log, default));
            Assert.Contains("Offboarding: 1 finished, 1 emptied private projects deleted, 0 still waiting for the trash's retention", log.Lines);
            Assert.Equal("0|0", await install.Scalar("SELECT (SELECT count(*) FROM memories WHERE subject_id='sam')||'|'||(SELECT count(*) FROM memory_projects WHERE subject_id='sam')"));
            Assert.Equal("deleted cli -|deleted system offboarding", await install.Scalar("""
                SELECT string_agg(event||' '||channel||' '||coalesce(job,'-'),'|' ORDER BY id) FROM memory_events WHERE project_id IS NOT NULL
                  AND memory_id IS NULL AND subject_id='sam' AND event='deleted' AND detail->>'reason'='offboard'
                """));
            Assert.Equal("offboarding_finished sam system offboarding {\"trashed\": 4, \"projects\": 2}", await install.Scalar(
                "SELECT event||' '||person_id||' '||channel||' '||job||' '||detail::text FROM admin_events WHERE event='offboarding_finished'"));
            Assert.Equal("sam", (await store.Get(who.Alex, family, default))!.Author);
            Assert.Contains("sam\tsuspended\tnot in Family\t0 token(s)\toffboarded ", await Person(s, "list"));
            // Running it again finds nothing left to do, and asks nothing.
            (code, said) = await Offboard(s, "");
            Assert.Equal(0, code);
            Assert.StartsWith("sam was offboarded on ", said);
            Assert.Equal((0, 0, 0), await Offboardings.Daily(s, log, default));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // With scopes off, every install's default, every memory is the household's: offboarding ends Sam's access and says
    // plainly that it moves nothing, and the memories Sam wrote stay, read by everyone, with Sam as their author.
    [PostgresFact] public async Task With_scopes_off_it_ends_their_access_and_moves_nothing_to_the_trash()
    {
        await using var install = await MemoryArchiveTests.Install.Make("off");
        var s = install.Source;
        var store = install.Store;
        var who = install.Who;
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-offboard-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var samPhone = await Token(s, folder, "sam-phone", "sam");
            var one = (await store.Create(who.Sam, new("DEMO: Sam's fictional note, the household's."), Mcp, default)).Id;
            var two = (await store.Create(who.Sam, new("DEMO: another fictional note of Sam's."), Mcp, default)).Id;
            Assert.Equal("household", (await store.Get(who.Alex, one, default))!.Subject);

            var (code, said) = await Offboard(s, "sam\n");
            Assert.Equal(0, code);
            Assert.Contains("Memory scopes are off: the household shares everything, so nothing is private to sam and nothing goes to the trash", said);
            Assert.DoesNotContain("go to the trash, as a forget", said);
            Assert.Contains("nothing bound to sam reads or writes from now on: suspended, 2 token(s) and session(s) bound to them disabled for good, not in Family, "
                + "0 source share(s) ended (with memory scopes off, Family and shares change what anyone reads only once they are on, and the first "
                + "start with them on leaves sam out of Family, brought back or not, until ./rhythm family add sam).", said);
            Assert.Contains("  1 token(s) bound to no person, which stay enabled (archive-house): if sam used one, ./rhythm token revoke <name>", said);
            Assert.DoesNotContain("an adapter or plugin reading", said);
            Assert.DoesNotContain("Give any adapter", said);
            Assert.Contains("the 2 memories sam wrote, the household's, read by everyone as today with sam as their author;", said);
            Assert.Contains("their own records from adapters and plugins (none now), kept as their source keeps them and read by everyone as today;", said);
            Assert.DoesNotContain("Moved", said);
            Assert.Contains("Finished: none of sam's private memories or memory projects is left.", said);

            Assert.Null(await new Database(s).Authenticate(samPhone, default));
            Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM memory_trash"));
            foreach (var id in new[] { one, two })
            {
                var kept = (await store.Get(who.Alex, id, default))!;
                Assert.Equal(("household", "sam"), (kept.Subject, kept.Author));
                Assert.Equal("sam", (await store.Get(who.House, id, default))!.Author);
            }
            Assert.Equal("suspended|0|0", await install.Scalar(
                "SELECT (SELECT status FROM subjects WHERE id='sam')||'|'||trashed||'|'||projects FROM offboardings WHERE person_id='sam' AND finished_at IS NOT NULL"));
            Assert.StartsWith("sam was offboarded on ", (await Offboard(s, "")).Said);
            Assert.Contains(": none of their private memories or memory projects is left, and nothing changed now.", (await Offboard(s, "")).Said);

            // Brought back before scopes are switched on, Sam is told they stay out of Family; the first start with them on
            // then adds everyone in people to Family but Sam, whose offboarding ended theirs, brought back or not, and only
            // rhythm family add puts them in.
            var lifted = await Person(s, "unsuspend", "sam");
            Assert.Contains("sam is no longer suspended.", lifted);
            Assert.Contains("sam is out of Family since their offboarding, and nothing puts them back, the first start with memory scopes on included, "
                + "until ./rhythm family add sam; the source shares it ended stay ended until ./rhythm share add sam <provider>", lifted);
            Assert.NotNull(await install.Scalar("SELECT returned_at FROM offboardings WHERE person_id='sam'"));
            var started = await new Principals(s).Install(ScopesOn("alex", "sam"), default);
            Assert.Equal((true, 1), (started.SwitchedOn, started.Joined));
            Assert.Equal("alex member", await install.Scalar("SELECT string_agg(person_id||' '||role,'|' ORDER BY person_id) FROM memberships"));
            Assert.Equal("current", await install.Scalar("SELECT status FROM subjects WHERE id='sam'"));
            var added = new StringWriter();
            Assert.Equal(0, await ScopeCommands.Family(["add", "sam"], added, new Principals(s)));
            Assert.Contains("Added sam to Family", added.ToString());
            Assert.Equal("alex member|sam member", await install.Scalar("SELECT string_agg(person_id||' '||role,'|' ORDER BY person_id) FROM memberships"));
            // Once in Family again, lifting a suspension no longer says they are out.
            Assert.Contains("Suspended sam", await Person(s, "suspend", "sam"));
            Assert.DoesNotContain("out of Family", await Person(s, "unsuspend", "sam"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // Settings with scopes on, as a start reads them, listing `people`.
    static RhythmConfig ScopesOn(params string[] people) => new()
    {
        Timezone = "Europe/London",
        People = [.. people.Select(p => new RhythmConfig.Person { Id = p, Name = char.ToUpperInvariant(p[0]) + p[1..] })],
        Memory = new Dictionary<object, object?> { ["scopes"] = "on" },
    };

    // Stopped after its first batch, the offboarding has ended Sam's access already, and running it again goes on from
    // where it stopped without asking again: every memory goes to the trash once.
    [PostgresFact] public async Task Stopped_part_way_it_goes_on_from_where_it_stopped()
    {
        await using var install = await MemoryArchiveTests.Install.Make("on");
        var s = install.Source;
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-offboard-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var samPhone = await Token(s, folder, "sam-phone", "sam");
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
                ids.Add((await install.Store.Create(install.Who.Sam, new($"DEMO: Sam's fictional note {i}."), Mcp, default)).Id);

            var (code, said) = await Offboard(s, "sam\n", batch: 2, batches: 1);
            Assert.Equal(1, code);
            Assert.Contains("Moved 2 private memories to the trash", said);
            Assert.Contains("Stopped with 3 private memories still out of the trash. Their access has ended already; ./rhythm person offboard sam goes on from here.", said);
            Assert.Null(await new Database(s).Authenticate(samPhone, default));
            Assert.Equal(2L, await install.Scalar("SELECT count(*) FROM memory_trash WHERE reason='offboard'"));
            Assert.Contains("\toffboarding since ", await Person(s, "list"));

            // Again, with nothing typed: it goes on, asking nothing.
            (code, said) = await Offboard(s, "", batch: 2);
            Assert.Equal(0, code);
            Assert.StartsWith("Going on with sam's offboarding, started ", said);
            Assert.DoesNotContain("Type sam", said);
            Assert.Contains("Done at once: sam stays suspended; 0 token(s) and session(s) disabled", said);
            Assert.Contains("Moved 3 private memories to the trash", said);
            Assert.Contains("The rest finishes on its own: the 5 memories of theirs in the trash wait there until", said);
            Assert.Equal(5L, await install.Scalar("SELECT count(*) FROM memory_trash WHERE reason='offboard' AND memory_id=ANY(@ids)", ("ids", ids.ToArray())));
            Assert.Equal(5L, await install.Scalar("SELECT count(*) FROM memory_events WHERE event='trashed' AND memory_id=ANY(@ids)", ("ids", ids.ToArray())));
            Assert.Equal("5|1", await install.Scalar(
                "SELECT (SELECT trashed FROM offboardings WHERE person_id='sam')||'|'||(SELECT count(*) FROM admin_events WHERE event='offboarding_started')"));
            // A third run moves nothing more.
            (code, said) = await Offboard(s, "");
            Assert.Equal(0, code);
            Assert.DoesNotContain("Moved", said);
            Assert.Equal(5L, await install.Scalar("SELECT count(*) FROM memory_events WHERE event='trashed' AND memory_id=ANY(@ids)", ("ids", ids.ToArray())));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // Brought back within the trash's retention, Sam restores what they want with a new token, and the offboarding waits
    // while they are current: no step changes anything, each called on its own (a batch into the trash, the operator's
    // close, the daily turn's close), and the daily turn closes nothing of theirs, an empty project included. Offboarding
    // them again asks again and goes on.
    [PostgresFact] public async Task Brought_back_in_the_grace_they_restore_what_they_want_and_the_offboarding_waits()
    {
        await using var install = await MemoryArchiveTests.Install.Make("on");
        var s = install.Source;
        var store = install.Store;
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-offboard-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var keep = (await store.Create(install.Who.Sam, new("DEMO: a fictional note Sam wants back.", Project: "garden"), Mcp, default)).Id;
            var drop = (await store.Create(install.Who.Sam, new("DEMO: a fictional note Sam leaves."), Mcp, default)).Id;
            Assert.Equal(0, (await Offboard(s, "sam\n")).Code);

            var lifted = await Person(s, "unsuspend", "sam");
            Assert.Contains("sam is no longer suspended.", lifted);
            Assert.Contains("sam is out of Family since their offboarding, and nothing puts them back", lifted);
            Assert.Contains("sam's offboarding, started ", lifted);
            Assert.Contains("waits while they are current and after, even if they are suspended again", lifted);
            Assert.Contains("goes on with it, after showing what it does and asking again.", lifted);
            Assert.Contains("sam\tcurrent\tnot in Family\t0 token(s)\toffboarding since ", await Person(s, "list"));
            Assert.Contains(", waiting while they are current", await Person(s, "list"));
            var laptop = await Token(s, folder, "sam-laptop", "sam");
            var sam = (await new Database(s).Authenticate(laptop, default))!.Id;
            var back = await store.Restore(sam, keep, null, Mcp, default);
            Assert.Equal("sam", back.Subject);
            Assert.Equal("DEMO: a fictional note Sam wants back.", (await store.Get(sam, keep, default))!.Content);
            Assert.Null(await store.Get(sam, drop, default));
            // An empty private project of Sam's, which a close would delete, and a note Sam writes since.
            await install.Run("INSERT INTO memory_projects(id,subject_id,slug,name,author_id) VALUES(gen_random_uuid(),'sam','porch','DEMO porch','sam')");
            var since = (await store.Create(sam, new("DEMO: a fictional note Sam writes after coming back."), Mcp, default)).Id;
            const string State = "SELECT (SELECT count(*) FROM memory_trash)||'|'||(SELECT count(*) FROM memory_projects WHERE subject_id='sam')"
                + "||'|'||(SELECT count(*) FROM memory_events WHERE event='deleted' AND memory_id IS NULL)";
            var before = await install.Scalar(State);
            Assert.Equal("1|2|0", before);
            // While Sam is current every step pauses and changes nothing, each called on its own, past the daily turn's own
            // filter: a batch into the trash, the operator's close and the daily turn's.
            var offboardings = new Offboardings(s);
            Assert.Equal(new OffboardBatch(0, 0, null, true), await OffboardCommand.Trash(offboardings, "sam", Offboardings.Batch, default));
            Assert.True((await offboardings.Close("sam", true, default)).Paused);
            Assert.True((await offboardings.Close("sam", false, default)).Paused);
            Assert.Equal(before, await install.Scalar(State));
            Assert.NotNull(await store.Get(sam, since, default));
            // Nor does the daily turn close anything of theirs: the empty project stays.
            var log = new LoggedLines<Scheduler>();
            Assert.Equal((0, 0, 0), await Offboardings.Daily(s, log, default));
            Assert.Equal(before, await install.Scalar(State));

            // Offboarded again: asked again, it says Sam was brought back, and takes the restored memory too.
            var (code, said) = await Offboard(s, "sam\n");
            Assert.Equal(0, code);
            Assert.Contains("Offboarding sam (offboarded since ", said);
            Assert.Contains("and brought back)", said);
            Assert.Contains("Done at once: sam is suspended; 1 token(s) and session(s) disabled", said);
            Assert.Contains("Moved 2 private memories to the trash", said);
            Assert.Contains("Deleted 1 private project(s) with no memory left in them.", said);
            Assert.Null(await new Database(s).Authenticate(laptop, default));
            Assert.Null(await install.Scalar("SELECT returned_at FROM offboardings WHERE person_id='sam'"));
            Assert.Equal(2L, await install.Scalar("SELECT count(*) FROM memory_events WHERE memory_id=@id AND event='trashed' AND detail->>'reason'='offboard'", ("id", keep)));
            // One offboarding all along: it never finished, so it went on rather than starting again.
            Assert.Equal("offboarding_started", await install.Scalar("SELECT string_agg(event,'|' ORDER BY id) FROM admin_events WHERE event LIKE 'offboarding%'"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // Brought back and then suspended again by an ordinary rhythm person suspend, Sam's offboarding still waits: the
    // return is recorded on it, so neither the daily turn nor a step called on its own changes anything, and the command
    // shows what it does and asks again rather than going on. Only the id typed takes it up again.
    [PostgresFact] public async Task Brought_back_and_suspended_again_the_offboarding_waits_for_the_operator()
    {
        await using var install = await MemoryArchiveTests.Install.Make("on");
        var s = install.Source;
        var store = install.Store;
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-offboard-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            await store.Create(install.Who.Sam, new("DEMO: a fictional note Sam leaves."), Mcp, default);
            Assert.Equal(0, (await Offboard(s, "sam\n")).Code);
            Assert.Null(await install.Scalar("SELECT returned_at FROM offboardings WHERE person_id='sam'"));

            Assert.Contains("sam is no longer suspended.", await Person(s, "unsuspend", "sam"));
            Assert.NotNull(await install.Scalar("SELECT returned_at FROM offboardings WHERE person_id='sam' AND finished_at IS NULL"));
            var laptop = await Token(s, folder, "sam-laptop", "sam");
            var sam = (await new Database(s).Authenticate(laptop, default))!.Id;
            var since = (await store.Create(sam, new("DEMO: a fictional note Sam writes after coming back."), Mcp, default)).Id;
            await install.Run("INSERT INTO memory_projects(id,subject_id,slug,name,author_id) VALUES(gen_random_uuid(),'sam','porch','DEMO porch','sam')");

            // An ordinary suspension: the offboarding does not take itself up again.
            var suspended = await Person(s, "suspend", "sam");
            Assert.Contains("Suspended sam: 1 token(s) disabled.", suspended);
            Assert.Contains("sam's offboarding still waits, since they were brought back: ./rhythm person offboard sam goes on with it, "
                + "after showing what it does and asking again.", suspended);
            Assert.Contains("sam\tsuspended\tnot in Family\t0 token(s)\toffboarding since ", await Person(s, "list"));
            Assert.Contains(", waiting since they were brought back", await Person(s, "list"));
            const string State = "SELECT (SELECT count(*) FROM memory_trash)||'|'||(SELECT count(*) FROM memory_projects WHERE subject_id='sam')"
                + "||'|'||(SELECT count(*) FROM memory_events WHERE event IN ('deleted','trashed'))||'|'||(SELECT count(*) FROM offboardings WHERE finished_at IS NULL)";
            var before = await install.Scalar(State);
            Assert.Equal("1|1|1|1", before);
            var log = new LoggedLines<Scheduler>();
            Assert.Equal((0, 0, 0), await Offboardings.Daily(s, log, default));
            Assert.Empty(log.Lines);
            // Each step on its own pauses too, whoever calls it.
            var offboardings = new Offboardings(s);
            Assert.True((await offboardings.Close("sam", true, default)).Paused);
            Assert.True((await offboardings.Close("sam", false, default)).Paused);
            Assert.True((await OffboardCommand.Trash(offboardings, "sam", Offboardings.Batch, default)).Paused);
            Assert.Equal(before, await install.Scalar(State));

            // The command asks again, and with nothing typed changes nothing.
            var (code, said) = await Offboard(s, "");
            Assert.Equal(1, code);
            Assert.DoesNotContain("Going on", said);
            Assert.Contains("Offboarding sam (offboarded since ", said);
            Assert.Contains("and brought back)", said);
            Assert.Contains("Their 1 private memories go to the trash", said);
            Assert.Contains("Type sam to offboard them; anything else changes nothing: ", said);
            Assert.Contains("Nothing changed: sam is as they were.", said);
            Assert.Equal(before, await install.Scalar(State));
            Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM memory_trash WHERE memory_id=@id", ("id", since)));

            // Typed, it goes on: the return is cleared, the note since goes to the trash and the empty project goes.
            (code, said) = await Offboard(s, "sam\n");
            Assert.Equal(0, code);
            Assert.Contains("Done at once: sam stays suspended; 0 token(s) and session(s) disabled", said);
            Assert.Contains("Moved 1 private memories to the trash", said);
            Assert.Contains("Deleted 1 private project(s) with no memory left in them.", said);
            Assert.Equal("2|0|-", await install.Scalar(
                "SELECT (SELECT count(*) FROM memory_trash WHERE reason='offboard')||'|'||(SELECT count(*) FROM memory_projects WHERE subject_id='sam')||'|'||coalesce((SELECT returned_at::text FROM offboardings WHERE person_id='sam'),'-')"));
            Assert.Equal((0, 0, 1), await Offboardings.Daily(s, log, default));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // A former person (taken out of people before) stays former, and the way back the preview names is listing them in
    // people again. Listed again, the start makes them current, which the offboarding records as a return; taken out
    // again, it still waits for the operator.
    [PostgresFact] public async Task A_former_person_is_brought_back_by_listing_them_again_and_the_offboarding_then_waits()
    {
        await using var install = await MemoryArchiveTests.Install.Make("on");
        var s = install.Source;
        var principals = new Principals(s);
        await install.Store.Create(install.Who.Sam, new("DEMO: a fictional note of Sam's.", Project: "loft"), Mcp, default);
        await principals.Install(ScopesOn("alex"), default);
        Assert.Equal("former", await install.Scalar("SELECT status FROM subjects WHERE id='sam'"));

        var (code, said) = await Offboard(s, "sam\n");
        Assert.Equal(0, code);
        Assert.Contains("nothing bound to sam reads or writes from now on: former already (no longer in people)", said);
        Assert.Contains("Brought back in that time (listed in people again and ./rhythm up), sam restores what they want with a new token, and stays out of Family until ./rhythm family add sam", said);
        Assert.DoesNotContain("unsuspend", said);
        Assert.Contains("Done at once: sam stays former", said);

        // Listed again: current, and the offboarding records the return (the start's re-listing, as unsuspend does).
        await principals.Install(ScopesOn("alex", "sam"), default);
        Assert.Equal("current|true", await install.Scalar(
            "SELECT (SELECT status FROM subjects WHERE id='sam')||'|'||(SELECT (returned_at IS NOT NULL)::text FROM offboardings WHERE person_id='sam')"));
        // The start that lists Sam again does not bring them back into Family either.
        Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM memberships WHERE person_id='sam'"));
        // Taken out again: former, and it still waits.
        await principals.Install(ScopesOn("alex"), default);
        Assert.Equal("former", await install.Scalar("SELECT status FROM subjects WHERE id='sam'"));
        Assert.Equal((0, 0, 0), await Offboardings.Daily(s, new LoggedLines<Scheduler>(), default));
        Assert.True((await new Offboardings(s).Close("sam", true, default)).Paused);
        Assert.Equal(1L, await install.Scalar("SELECT count(*) FROM memory_projects WHERE subject_id='sam'"));
        Assert.Contains(", waiting since they were brought back", await Person(s, "list"));
    }

    // The daily turn closes an offboarding the operator started once the trash's purge, earlier in the same turn, has
    // taken what waited: no step of the operator's is needed after the command.
    [PostgresFact] public async Task The_daily_turn_closes_an_offboarding_once_the_purge_has_taken_what_waited()
    {
        await using var install = await MemoryArchiveTests.Install.Make("on");
        var s = install.Source;
        var id = (await install.Store.Create(install.Who.Sam, new("DEMO: a fictional note in a project.", Project: "attic"), Mcp, default)).Id;
        Assert.Equal(0, (await Offboard(s, "sam\n")).Code);
        await install.Run("UPDATE memory_trash SET trashed_at=now()-interval '15 days',purge_after=now()-interval '1 day' WHERE memory_id=@id", ("id", id));

        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { scopes: on }\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-offboard-scheduler-" + Guid.NewGuid().ToString("N")[..10]);
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new Unread(), registry);
        var log = new LoggedLines<Scheduler>();
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(s), new Principals(s), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, log);
        try
        {
            await scheduler.StartAsync(default);
            bool Closed() => log.Lines.Any(l => l.StartsWith("Offboarding:", StringComparison.Ordinal));
            for (var waited = 0; waited < 300 && !Closed() && !scheduler.ExecuteTask!.IsCompleted; waited++) await Task.Delay(100);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
            Assert.Contains("Memory trash: 1 memories past their retention deleted for good, 0 old idempotency keys pruned", log.Lines);
            Assert.Contains("Offboarding: 1 finished, 1 emptied private projects deleted, 0 still waiting for the trash's retention", log.Lines);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("offboarding", StringComparison.Ordinal));
            Assert.Equal(1L, await install.Scalar("SELECT count(*) FROM offboardings WHERE person_id='sam' AND finished_at IS NOT NULL"));
        }
        finally
        {
            await scheduler.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    // Never a person the install does not know, the household included, and one id only. The usage is checked before the
    // database is reached.
    [PostgresFact] public async Task Only_a_person_the_install_knows_is_offboarded()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["sam", "alex"], ["--yes"] })
            Assert.Equal(OffboardCommand.Usage, (await Assert.ThrowsAsync<ArgumentException>(() =>
                OffboardCommand.Run(args, new StringReader("sam\n"), new StringWriter()))).Message);
        await using var install = await MemoryArchiveTests.Install.Make("on");
        foreach (var id in new[] { "household", "fixture", "nobody" })
            Assert.Contains("not a person this install knows", (await Assert.ThrowsAsync<ArgumentException>(() =>
                OffboardCommand.Run([id], new StringReader(id + "\n"), new StringWriter(), install.Source))).Message);
        Assert.Equal(0L, await install.Scalar("SELECT count(*) FROM offboardings"));
        Assert.Contains("person offboard <id>", CliCommands.Help(false));
        Assert.Contains("person offboard <id>", TokenCommand.PersonUsage);
    }

    sealed class Unread : IRelationshipSource
    {
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct)
        {
            using var budget = new CancellationTokenSource();
            budget.Cancel();
            return Task.FromCanceled<DiscoveryBatch>(budget.Token);
        }
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => Task.FromResult(SourceCheck.Unavailable);
    }
}
