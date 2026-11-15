using System.Globalization;
using Knowledge.Adapters;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// Upkeep of the links between native memories (SB-P4-04; migration 056, Core/MemoryLinkPruning.cs; docs/dev/memory.md
// sections 8 and 9; D-015, D-017, D-067): a change of a memory's words invalidates the automatic links and the map's links
// drawn from them, and never a link someone stated; a nightly pass prunes weak or out of date automatic links once their
// grace period has passed, never inside it, never one with an end in the trash and never a person's or an AI client's,
// which it reports when broken; and a trip through the trash and back keeps every link. Fictional people only: alex, sam
// and the household; every text is DEMO.
static class LinkUpkeep
{
    public static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    public static readonly DateTimeOffset Night = DateTimeOffset.Parse("2026-11-03T02:00:00Z", CultureInfo.InvariantCulture);

    public static NightlyContext Context(NpgsqlDataSource s) => new(s, MemoryLinkPruning.JobId, new DateOnly(2026, 11, 3));

    public static Task<NightlyRun> Pass(NpgsqlDataSource s, int piece = MemoryLinkPruning.Piece, int perScope = MemoryLinkPruning.PerScope,
        CancellationToken ct = default, Action<string>? afterPiece = null, LoggedLines<NightlyRunner>? log = null) =>
        MemoryLinkPruning.Pass(s, Context(s), log ?? new LoggedLines<NightlyRunner>(), piece, perScope, ct, afterPiece);

    // An automatic link, as an import brings one (no maker, imported) or SB-P4-03's links on save will write one:
    // verification suggested, made `days` ago and last checked then (or never, when `checkedDaysAgo` is null).
    public static async Task<Guid> Suggested(NpgsqlDataSource s, Guid from, Guid to, double? confidence, int days, string type = "relates_to",
        string lifecycle = "active", int? checkedDaysAgo = null, string evidence = "DEMO excerpt of the words")
    {
        var id = Guid.NewGuid();
        await MemoryRows.Run(s, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported,confidence,source_revision,checked_at)
            VALUES(@id,@s,@t,@type,'suggested',@evidence,@lifecycle,NULL,now()-make_interval(days=>@d),true,@c,'memory:1',
              CASE WHEN @k::integer IS NULL THEN NULL ELSE now()-make_interval(days=>@k::integer) END)
            """, ("id", id), ("s", from), ("t", to), ("type", type), ("evidence", evidence), ("lifecycle", lifecycle), ("d", days),
            ("c", confidence), ("k", checkedDaysAgo));
        return id;
    }

    // A link someone stated, written as an import writes a verified one (no maker), with its own age and confidence.
    public static async Task<Guid> Stated(NpgsqlDataSource s, Guid from, Guid to, string type, string verification, int days, double? confidence = null,
        string lifecycle = "active", Guid? by = null)
    {
        var id = Guid.NewGuid();
        await MemoryRows.Run(s, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported,confidence,checked_at)
            VALUES(@id,@s,@t,@type,@v,'DEMO quote',@lifecycle,@by,now()-make_interval(days=>@d),@by::uuid IS NULL,@c,now()-make_interval(days=>@d))
            """, ("id", id), ("s", from), ("t", to), ("type", type), ("v", verification), ("lifecycle", lifecycle), ("by", by), ("d", days), ("c", confidence));
        return id;
    }

    // Make a link `days` older: made and last checked that much earlier.
    public static Task Age(NpgsqlDataSource s, Guid link, int days) => MemoryRows.Run(s, """
        UPDATE made_links SET created_at=created_at-make_interval(days=>@d),checked_at=checked_at-make_interval(days=>@d) WHERE link_id=@l
        """, ("l", link), ("d", days));

    // A link as stored, or null when it is gone.
    public static async Task<string?> Row(NpgsqlDataSource s, Guid link) =>
        (string?)await MemoryRows.Scalar(s, "SELECT to_jsonb(l)::text FROM made_links l WHERE link_id=@l", ("l", link));

    public static async Task<Dictionary<Guid, string?>> Rows(NpgsqlDataSource s, params Guid[] links)
    {
        var rows = new Dictionary<Guid, string?>();
        foreach (var l in links) rows[l] = await Row(s, l);
        return rows;
    }

    // Every made link at a memory, wherever it is kept (made_links, or a trash row: 049's memory_links_kept), as stored,
    // in a fixed order; and the dismissals naming it.
    public static async Task<string> Kept(NpgsqlDataSource s, Guid memory) => (string)(await MemoryRows.Scalar(s, """
        SELECT coalesce(jsonb_agg(to_jsonb(k) ORDER BY k.link_id),'[]')::text FROM memory_links_kept(@m) k
        """, ("m", memory)))!;
    public static async Task<string> Dismissals(NpgsqlDataSource s, Guid memory) => (string)(await MemoryRows.Scalar(s, """
        SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d.low_id,d.high_id,d.type),'[]')::text FROM memory_link_dismissals d WHERE @m IN (d.low_id,d.high_id)
        """, ("m", memory)))!;

    public static async Task<long> Count(NpgsqlDataSource s, string sql, params (string, object?)[] args) => (long)(await MemoryRows.Scalar(s, sql, args))!;
}

// The pass is a job of the daily turn only with memory.tools on, after the staleness pass; off, absent or empty, the turn
// is the four jobs it always was, and every job before it keeps its place, name and budget.
public sealed class MemoryLinkPruningJobTests
{
    [Fact] public void The_link_pruning_pass_joins_the_daily_jobs_only_with_memory_tools_on_after_the_staleness_pass()
    {
        var log = new LoggedLines<Scheduler>();
        string[] four = ["ingest-keys", "screens", "memory-trash", "recall-log"];
        foreach (var config in new RhythmConfig?[] { null, TestSettings.Use(), MemoryToolSettings.With("tools: off"), MemoryToolSettings.With("tools: null"),
            MemoryToolSettings.With("capsules: on"), MemoryToolSettings.With("scopes: off") })
            Assert.Equal(four, NightlyJobs.Daily(null!, log, config).Select(j => j.Id));
        var on = NightlyJobs.Daily(null!, log, MemoryToolSettings.On);
        Assert.Equal([.. four, MemoryStaleness.JobId, MemoryLinkPruning.JobId, InsightCandidates.JobId], on.Select(j => j.Id));
        Assert.Equal(NightlyJobs.Daily(null!, log).Select(j => (j.Id, j.Name, j.Budget, j.OncePerNight)), on.Take(4).Select(j => (j.Id, j.Name, j.Budget, j.OncePerNight)));
        Assert.Equal(("memory staleness pass", TimeSpan.FromMinutes(15), false), (on[4].Name, on[4].Budget, on[4].OncePerNight));
        Assert.Equal(("memory link pruning", TimeSpan.FromMinutes(15), false), (on[5].Name, on[5].Budget, on[5].OncePerNight));
        TestSettings.Use();
    }

    [Fact] public async Task A_pass_takes_a_piece_of_1_to_1000_and_at_least_one_piece_a_scope()
    {
        foreach (var (piece, perScope) in new[] { (0, 10), (1001, 2000), (10, 5) })
            await Assert.ThrowsAsync<ArgumentException>(() => MemoryLinkPruning.Pass(null!, null!, new LoggedLines<NightlyRunner>(), piece, perScope, default));
    }
}

// An install with no made link gets nothing from the pass (no cursor, no log line), and a change of a memory's words or
// kind with no link and no star at it writes nothing; through the daily turn with memory.tools on as well. Its own
// database, so nothing else here holds a memory or a link.
public sealed class MemoryLinkUpkeepEmptyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task With_no_made_link_the_pass_and_a_change_of_words_write_nothing()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        const string Written = """
            SELECT (SELECT count(*) FROM made_links)||'|'||(SELECT count(*) FROM relationships)||'|'||(SELECT count(*) FROM job_cursors)
              ||'|'||(SELECT count(*) FROM memory_link_dismissals)
            """;
        var log = new LoggedLines<NightlyRunner>();
        var run = await LinkUpkeep.Pass(s, log: log);
        Assert.Equal((0L, 0L, 0L, 0L, false), (run.Counts["links checked"], run.Counts["automatic links pruned"],
            run.Counts["person- and AI-made links broken"], run.Counts["left for the next run"], run.More));
        Assert.Equal("0|0|0|0", await MemoryRows.Scalar(s, Written));
        Assert.Empty(log.Lines);

        // Memories with no link: every change of their words and kind, through each writer, leaves the link tables empty.
        var who = await MemoryLogins.Make(s, "upkeep-empty");
        var store = new MemoryStore(s);
        var a = (await store.Create(who.Alex, new("DEMO: the Wendle orchard has six trees.", Kind: "episodic"), LinkUpkeep.Mcp, default)).Id;
        await store.Create(who.Sam, new("DEMO: the Wendle orchard needs a ladder."), LinkUpkeep.Mcp, default);
        await store.Update(who.Alex, a, new(Content: "DEMO: the Wendle orchard has seven trees."), null, LinkUpkeep.Mcp, default);
        await store.Append(who.Alex, a, "DEMO: one is a pear.", null, LinkUpkeep.Mcp, default);
        await store.Undo(who.Alex, a, null, null, LinkUpkeep.Mcp, default);
        await store.Update(who.Alex, a, new(Kind: "semantic"), null, LinkUpkeep.Mcp, default);
        Assert.Equal("0|0|0|0", await MemoryRows.Scalar(s, Written));

        // Through the daily turn with memory.tools on: seven jobs (the insight candidate pass after this one), all complete,
        // and still nothing written by the pass.
        var jobs = new LoggedLines<Scheduler>();
        var outcomes = await new NightlyRunner(s, NightlyJobs.Daily(s, jobs, MemoryToolSettings.On), log, false, TimeZoneInfo.Utc).Run(default, LinkUpkeep.Night);
        Assert.Equal(Enumerable.Repeat("complete", 7), outcomes.Select(o => o.Outcome));
        Assert.Equal(MemoryLinkPruning.JobId, outcomes[5].Job);
        Assert.Equal(0L, outcomes[5].Counts["links checked"]);
        Assert.Equal("0|0|0|0", await MemoryRows.Scalar(s, Written));
        Assert.Empty(log.Lines);
        Assert.DoesNotContain(jobs.Lines, l => l.StartsWith("Memory links", StringComparison.Ordinal));
        TestSettings.Use();
    }
}

// A change of a memory's words invalidates the automatic links drawn from it, wherever they wait, and never a link
// someone stated; a change of its kind those the episodic rule no longer allows.
public sealed class MemoryLinkInvalidationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    async Task<Guid> Remember(Guid who, string text, string? kind = null) => (await Store.Create(who, new(text, Kind: kind), Mcp, default)).Id;

    [PostgresFact] public async Task A_change_of_words_invalidates_the_automatic_links_drawn_from_it_and_never_a_stated_one()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-words");
        await Scoped.Mode(s, "off");
        var a = await Remember(who.Alex, "DEMO: the Quenby kiln fires on Mondays.");
        var b = await Remember(who.Alex, "DEMO: the Quenby kiln needs new shelves.");
        var c = await Remember(who.Sam, "DEMO: the Quenby pottery sells mugs.");
        var d = await Remember(who.Sam, "DEMO: the Quenby pottery opens at nine.");
        var toB = await LinkUpkeep.Suggested(s, a, b, 0.6, 2);
        var fromC = await LinkUpkeep.Suggested(s, c, a, 0.4, 2, "caused_by");
        var apart = await LinkUpkeep.Suggested(s, b, c, 0.5, 2);
        var retired = await LinkUpkeep.Suggested(s, d, a, 0.5, 2, lifecycle: "retired");
        var ai = (await Store.Link(who.Alex, a, b, "drawn_from", 0.2, Mcp, default)).Id;
        var terminal = await MemoryLogins.AtTerminal(s, who.Alex);
        var person = (await Store.Link(terminal, a, c, "caused_by", null, new("cli", "rhythm"), default)).Id;
        var quoted = await LinkUpkeep.Stated(s, d, a, "part_of_project", "verified", 2);
        Guid[] all = [toB, fromC, apart, retired, ai, person, quoted];
        Guid[] untouched = [apart, retired, ai, person, quoted];
        var before = await LinkUpkeep.Rows(s, all);

        // A change that leaves the words alone (tags, importance) touches no link.
        await Store.Update(who.Alex, a, new(Tags: ["demo-kiln"], Importance: 3), null, Mcp, default);
        Assert.Equal(before, await LinkUpkeep.Rows(s, all));

        // Its words changed: each automatic link at either end is out of date (stale, its excerpt gone, checked now);
        // an automatic link elsewhere, one already retired and every stated one (an AI client's, a person's, an import's
        // with its own words) are exactly as they were.
        var now = await MemoryLogins.Now(s);
        await Store.Update(who.Alex, a, new(Content: "DEMO: the Quenby kiln fires on Tuesdays."), null, Mcp, default);
        foreach (var l in new[] { toB, fromC })
            Assert.Equal("stale||t", await MemoryRows.Scalar(s, "SELECT concat_ws('|',lifecycle,evidence,checked_at>=@now) FROM made_links WHERE link_id=@l",
                ("l", l), ("now", now.UtcDateTime)));
        var unchanged = untouched.ToDictionary(l => l, l => before[l]);
        Assert.Equal(unchanged, await LinkUpkeep.Rows(s, untouched));
        // No read serves a stale link: connections lists only the stated ones at the memory.
        Assert.Equal(new[] { ai, person, quoted }.Order(), (await Store.Connections(who.Alex, a, null, 20, default))!.Items.Select(i => i.LinkId!.Value).Order());

        // Every writer of words does it: an append and an undo as well as an update.
        foreach (var write in new Func<Task>[] {
            () => Store.Append(who.Alex, a, "DEMO: and on Fridays in summer.", null, Mcp, default),
            () => Store.Undo(who.Alex, a, null, null, Mcp, default) })
        {
            await MemoryRows.Run(s, "UPDATE made_links SET lifecycle='active',evidence='DEMO excerpt',checked_at=NULL WHERE link_id=ANY(@l)", ("l", new[] { toB, fromC }));
            await write();
            Assert.Equal(2L, await LinkUpkeep.Count(s, "SELECT count(*) FROM made_links WHERE link_id=ANY(@l) AND lifecycle='stale' AND evidence='' AND checked_at IS NOT NULL",
                ("l", new[] { toB, fromC })));
            Assert.Equal(unchanged, await LinkUpkeep.Rows(s, untouched));
        }
    }

    [PostgresFact] public async Task A_change_of_kind_invalidates_only_the_automatic_links_the_episodic_rule_no_longer_allows()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-kind");
        await Scoped.Mode(s, "off");
        var saturday = await Remember(who.Alex, "DEMO: the Quenby walk on Saturday.", "episodic");
        var sunday = await Remember(who.Alex, "DEMO: the Quenby walk on Sunday.", "episodic");
        var follows = await LinkUpkeep.Suggested(s, sunday, saturday, 0.7, 1, "follows");
        var related = await LinkUpkeep.Suggested(s, sunday, saturday, 0.7, 1);
        var stated = (await Store.Link(who.Alex, saturday, sunday, "follows", null, Mcp, default)).Id;
        var before = await LinkUpkeep.Rows(s, follows, related, stated);

        // Saturday is no episode any more: an automatic follows link is out of date; relates_to joins any kinds, and the
        // stated follows link stays as stated, broken now (056's made_link_broken says why).
        await Store.Update(who.Alex, saturday, new(Kind: "semantic"), null, Mcp, default);
        Assert.Equal("stale|", await MemoryRows.Scalar(s, "SELECT lifecycle||'|'||evidence FROM made_links WHERE link_id=@l", ("l", follows)));
        Assert.Equal(before[related], await LinkUpkeep.Row(s, related));
        Assert.Equal(before[stated], await LinkUpkeep.Row(s, stated));
        Assert.Equal("kinds", await MemoryRows.Scalar(s, "SELECT made_link_broken(l) FROM made_links l WHERE link_id=@l", ("l", stated)));
    }

    // An automatic link waiting in the trash row of its other end is invalidated there too, so the restore brings it back
    // out of date; a stated one waits exactly as it was.
    [PostgresFact] public async Task An_automatic_link_waiting_in_the_trash_is_invalidated_there_and_comes_back_out_of_date()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-trash");
        await Scoped.Mode(s, "off");
        var gate = await Remember(who.Alex, "DEMO: the Quenby gate sticks in the rain.");
        var shed = await Remember(who.Alex, "DEMO: the Quenby shed key is under the pot.");
        var drawn = await LinkUpkeep.Suggested(s, shed, gate, 0.5, 1);
        var stated = (await Store.Link(who.Alex, shed, gate, "caused_by", null, Mcp, default)).Id;
        var stated0 = await LinkUpkeep.Row(s, stated);
        var trashed = await Store.Forget(who.Alex, shed, null, Mcp, default);
        Assert.Equal(2, trashed.Trash.Links);
        await Store.Update(who.Alex, gate, new(Content: "DEMO: the Quenby gate sticks in the frost."), null, Mcp, default);
        Assert.Equal("stale|", await MemoryRows.Scalar(s, """
            SELECT e->>'lifecycle'||'|'||(e->>'evidence') FROM memory_trash t, jsonb_array_elements(t.links) e WHERE t.memory_id=@m AND e->>'link_id'=@l
            """, ("m", shed), ("l", drawn.ToString())));
        var back = await Store.Restore(who.Alex, shed, trashed.Trash.Nonce, Mcp, default);
        Assert.Equal((2, 0), (back.Links, back.LinksGone));
        Assert.Equal("stale|", await MemoryRows.Scalar(s, "SELECT lifecycle||'|'||evidence FROM made_links WHERE link_id=@l", ("l", drawn)));
        Assert.Equal(stated0, await LinkUpkeep.Row(s, stated));
    }

    // A supersede's link is the record of a replacement whatever its verification: one marked suggested (as an import may
    // bring) is never invalidated by a change of either end's words or kind and never pruned, as made_link_broken already
    // leaves every supersede's link out.
    [PostgresFact] public async Task A_supersedes_link_is_never_invalidated_or_pruned_whatever_its_verification()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-supersede");
        await Scoped.Mode(s, "off");
        var older = await Remember(who.Alex, "DEMO: the Quenby bus leaves at eight.");
        var newer = await Remember(who.Alex, "DEMO: the Quenby bus leaves at half past eight.");
        var replaced = await LinkUpkeep.Suggested(s, newer, older, 0.1, 30, "supersedes");
        var before = await LinkUpkeep.Row(s, replaced);
        await Store.Update(who.Alex, newer, new(Content: "DEMO: the Quenby bus leaves at twenty to nine."), null, Mcp, default);
        await Store.Update(who.Alex, older, new(Kind: "episodic"), null, Mcp, default);
        Assert.Equal(before, await LinkUpkeep.Row(s, replaced));
        var run = await LinkUpkeep.Pass(s);
        Assert.Equal((0L, 0L), (run.Counts["automatic links found out of date"], run.Counts["automatic links pruned"]));
        Assert.Equal(before, await LinkUpkeep.Row(s, replaced));
    }
}

// A change of words the trigger could not reach (the link was locked right then, as a forget moving it into the trash,
// or a restore bringing it back, holds it) leaves an automatic link active with an excerpt of the old words; the nightly
// pass's re-check finds it from the end's audit trail and marks it out of date, as the change would have, and its grace
// period starts then. A link checked after the change, and one someone stated, are left as they are.
public sealed class MemoryLinkRecheckTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    async Task<Guid> Remember(Guid who, string text, string? kind = null) => (await Store.Create(who, new(text, Kind: kind), Mcp, default)).Id;

    [PostgresFact] public async Task The_pass_marks_out_of_date_an_automatic_link_a_change_of_words_missed()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-recheck");
        await Scoped.Mode(s, "off");
        var well = await Remember(who.Alex, "DEMO: the Quenby well is dry in August.");
        var pump = await Remember(who.Alex, "DEMO: the Quenby pump was mended in May.");
        var missed = await LinkUpkeep.Suggested(s, well, pump, 0.9, 2, checkedDaysAgo: 2);
        var stated = await LinkUpkeep.Stated(s, pump, well, "caused_by", "verified", 2);
        var stated0 = await LinkUpkeep.Row(s, stated);

        // The link is held by another transaction while the words change: the trigger skips it (SKIP LOCKED).
        await using (var c = await s.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using (var lockIt = new NpgsqlCommand("SELECT 1 FROM made_links WHERE link_id=@l FOR UPDATE", c, tx))
            {
                lockIt.Parameters.AddWithValue("l", missed);
                await lockIt.ExecuteScalarAsync();
            }
            await Store.Update(who.Alex, well, new(Content: "DEMO: the Quenby well is dry from July."), null, Mcp, default);
            await tx.RollbackAsync();
        }
        Assert.Equal("active|DEMO excerpt of the words", await MemoryRows.Scalar(s, "SELECT lifecycle||'|'||evidence FROM made_links WHERE link_id=@l", ("l", missed)));

        var now = await MemoryLogins.Now(s);
        var log = new LoggedLines<NightlyRunner>();
        var run = await LinkUpkeep.Pass(s, log: log);
        Assert.Equal((2L, 1L, 0L), (run.Counts["links checked"], run.Counts["automatic links found out of date"], run.Counts["automatic links pruned"]));
        Assert.Equal("stale||t", await MemoryRows.Scalar(s, "SELECT concat_ws('|',lifecycle,evidence,checked_at>=@now) FROM made_links WHERE link_id=@l",
            ("l", missed), ("now", now.UtcDateTime)));
        Assert.Equal(stated0, await LinkUpkeep.Row(s, stated));
        Assert.Contains(log.Lines, l => l.EndsWith(", 1 automatic links found out of date", StringComparison.Ordinal));
        // Found once: the next pass finds it checked since, and leaves it for its grace period.
        var again = await LinkUpkeep.Pass(s);
        Assert.Equal((0L, 0L), (again.Counts["automatic links found out of date"], again.Counts["automatic links pruned"]));

        // A link checked after the change, or made after it, is not out of date: nothing is found.
        var later = await LinkUpkeep.Suggested(s, pump, well, 0.9, 0);
        Assert.Equal(0L, (await LinkUpkeep.Pass(s)).Counts["automatic links found out of date"]);
        Assert.Equal("active", await MemoryRows.Scalar(s, "SELECT lifecycle FROM made_links WHERE link_id=@l", ("l", later)));
    }
}

// The map's own links drawn from a memory's words (a similarity the rebuild found between two memories' stars) go off the
// map, recall's hops and connections at once when its words change, and the next rebuild draws them again; its project
// link (drawn from its project) and its star stay.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryLinkMapInvalidationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    sealed record Map(RelationshipHost Host, string State) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(State, true); } catch (IOException) { } }
        public async Task<GraphPage> Page(Guid reader) => await Host.Read(reader, null, null, default, new GraphQuery());
    }

    // A rebuild's source that, once a rebuild has read and saved its records and starts checking them, runs `during`
    // once (a change made while the rebuild is under way), then answers as the catalog's own source.
    sealed class ChangedMeanwhile(IRelationshipSource inner, Func<Task>? during) : IRelationshipSource, IRelationshipSnapshotSource
    {
        Func<Task>? pending = during;
        public void Prime(IReadOnlyList<DiscoveryRecord> stored) { if (inner is IRelationshipSnapshotSource primed) primed.Prime(stored); }
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct) => inner.Fetch(principal, ct);
        public async Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct)
        {
            if (pending is { } change) { pending = null; await change(); }
            return await inner.Check(principal, key, revision, ct);
        }
    }

    // The next rebuild's change made while it runs (ChangedMeanwhile), or none.
    Func<Task>? meanwhile;

    async Task<Map> Built(Func<string, string, float[]?> vector)
    {
        var config = MemoryToolSettings.On;
        var registry = new AdapterRegistry([]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { SimilarityThreshold = 0.5 },
            c =>
            {
                var during = meanwhile;
                meanwhile = null;
                return new ChangedMeanwhile(new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct),
                    vector, "demo-vectors"), during);
            }, registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        return new(host, state);
    }

    static string Star(Guid memory) => MemoryStars.Key("household", memory).GraphId;

    [PostgresFact] public async Task A_change_of_words_takes_the_maps_links_drawn_from_them_off_at_once_until_the_next_rebuild()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-map");
        await Scoped.Mode(s, "off");
        try
        {
            var heron = (await Store.Create(who.Alex, new("DEMO: the Pellingham heron nests by the weir.", Project: "demo-upkeep-birds"), Mcp, default)).Id;
            var weir = (await Store.Create(who.Sam, new("DEMO: the Pellingham heron fishes at dawn."), Mcp, default)).Id;
            static float[]? Vector(string title, string text) =>
                (title + " " + text).Contains("Pellingham", StringComparison.Ordinal) ? [1f, 0f, 0f, 0f] : [0f, 0f, 1f, 0f];
            using var map = await Built(Vector);
            bool Between(GraphEdge e) => new[] { e.source, e.target }.Order().SequenceEqual(new[] { Star(heron), Star(weir) }.Order());
            var page = await map.Page(who.Sam);
            var suggestion = Assert.Single(page.edges, Between);
            Assert.Equal((SimilaritySuggestions.Type, "suggested"), (suggestion.relationship, suggestion.verification));
            Assert.Contains(page.edges, e => e.source == Star(heron) && e.relationship == "in_project");
            var db = RecallRows.Db(s);
            var words = new Recall(db, null, PassageSources.Of(false, false, true, []), options: RecallOptions.Default with { MadeLinks = true });
            Assert.Contains(weir, (await words.Search(who.Alex, new("nests weir", 20, 1), default)).Hits.Select(h => h.Id));
            Assert.Single((await Store.Connections(who.Alex, heron, null, 20, default))!.Items);

            // Heron's words change: the similarity drawn from them leaves the map, the hops and connections at once, its
            // evidence gone; the link to its project and its star stay, and its words are read anew when it opens.
            await Store.Update(who.Alex, heron, new(Content: "DEMO: the Pellingham heron nests by the old weir."), null, Mcp, default);
            page = await map.Page(who.Sam);
            Assert.DoesNotContain(page.edges, Between);
            Assert.Contains(page.edges, e => e.source == Star(heron) && e.relationship == "in_project");
            Assert.Contains(page.nodes, n => n.id == Star(heron));
            Assert.Equal("stale||{}", await MemoryRows.Scalar(s, """
                SELECT string_agg(concat_ws('|',l.lifecycle,l.evidence,l.details::text),',') FROM relationships l
                JOIN resources a ON a.id=l.source_id JOIN resources b ON b.id=l.target_id
                WHERE l.type=@t AND a.external_id IN (@x,@y) AND b.external_id IN (@x,@y)
                """, ("t", SimilaritySuggestions.Type), ("x", MemoryStars.Key("household", heron).CatalogId), ("y", MemoryStars.Key("household", weir).CatalogId)));
            Assert.DoesNotContain(weir, (await words.Search(who.Alex, new("nests weir", 20, 1), default)).Hits.Select(h => h.Id));
            Assert.Empty((await Store.Connections(who.Alex, heron, null, 20, default))!.Items);

            // The next rebuild draws it again from the new words, with its evidence.
            Assert.Equal("complete", await map.Host.Refresh(false, default));
            var again = Assert.Single((await map.Page(who.Sam)).edges, Between);
            Assert.NotEmpty(again.evidence);
            Assert.Contains(weir, (await words.Search(who.Alex, new("nests weir", 20, 1), default)).Hits.Select(h => h.Id));
            Assert.Single((await Store.Connections(who.Alex, heron, null, 20, default))!.Items);

            // A change that leaves the words alone leaves the map's links as they are.
            await Store.Update(who.Alex, heron, new(Importance: 4), null, Mcp, default);
            Assert.Single((await map.Page(who.Sam)).edges, Between);
        }
        finally { TestSettings.Use(); }
    }

    // A rebuild that read a memory's old words and saved its star before the change writes no link drawn from them: the
    // change wrote the star's row again, so the rebuild finds it changed under it. The star stays on the map, and the
    // rebuild after draws its links from the new words.
    [PostgresFact] public async Task A_rebuild_that_read_the_old_words_writes_no_link_from_them()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-race");
        await Scoped.Mode(s, "off");
        try
        {
            var owl = (await Store.Create(who.Alex, new("DEMO: the Quillow owl hoots at dusk."), Mcp, default)).Id;
            var barn = (await Store.Create(who.Sam, new("DEMO: the Quillow owl lives in the barn."), Mcp, default)).Id;
            static float[]? Vector(string title, string text) =>
                (title + " " + text).Contains("Quillow owl", StringComparison.Ordinal) ? [0f, 1f, 0f, 0f] : [0f, 0f, 0f, 1f];
            using var map = await Built(Vector);
            bool Between(GraphEdge e) => new[] { e.source, e.target }.Order().SequenceEqual(new[] { Star(owl), Star(barn) }.Order());
            Assert.Single((await map.Page(who.Sam)).edges, Between);

            // The change lands while a rebuild that read the old words is checking what it saved.
            meanwhile = () => Store.Update(who.Alex, owl, new(Content: "DEMO: the Quillow owl hoots at midnight."), null, Mcp, default);
            Assert.Equal("complete", await map.Host.Refresh(false, default));
            Assert.Null(meanwhile);
            var page = await map.Page(who.Sam);
            Assert.DoesNotContain(page.edges, Between);
            Assert.Contains(page.nodes, n => n.id == Star(owl));
            Assert.Equal("midnight", (string?)await MemoryRows.Scalar(s, "SELECT substring(content from 'midnight') FROM memories WHERE id=@id", ("id", owl)));

            // The rebuild after it reads the new words and draws the link again.
            Assert.Equal("complete", await map.Host.Refresh(false, default));
            Assert.Single((await map.Page(who.Sam)).edges, Between);
        }
        finally { TestSettings.Use(); }
    }
}

// The nightly pass deletes an automatic link once its grace period has passed and it is weak or no longer active, and
// nothing inside the grace period, nothing with an end in the trash and nothing a writer holds right now.
public sealed class MemoryLinkPruningTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    async Task<Guid[]> Remember(Guid who, string topic, int n)
    {
        var ids = new Guid[n];
        for (var i = 0; i < n; i++) ids[i] = (await Store.Create(who, new($"DEMO: {topic} note {i}."), Mcp, default)).Id;
        return ids;
    }

    [PostgresFact] public async Task Weak_and_out_of_date_automatic_links_are_pruned_after_their_grace_period_and_never_inside_it()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-prune");
        await Scoped.Mode(s, "off");
        var m = await Remember(who.Alex, "the Ashby allotment", 9);
        var weakOld = await LinkUpkeep.Suggested(s, m[0], m[1], 0.2, 8);
        var weakYoung = await LinkUpkeep.Suggested(s, m[1], m[2], 0.2, 6);
        var checkedLately = await LinkUpkeep.Suggested(s, m[2], m[3], 0.1, 30, checkedDaysAgo: 2);
        var strongOld = await LinkUpkeep.Suggested(s, m[3], m[4], 0.8, 30);
        var unknownOld = await LinkUpkeep.Suggested(s, m[4], m[5], null, 30);
        var staleOld = await LinkUpkeep.Suggested(s, m[5], m[6], 0.9, 30, lifecycle: "stale", checkedDaysAgo: 8);
        var retiredOld = await LinkUpkeep.Suggested(s, m[6], m[7], 0.9, 10, lifecycle: "retired");
        var outdated = await LinkUpkeep.Suggested(s, m[7], m[8], 0.9, 30);
        // Invalidated just now by a change of its words: its grace period starts again.
        await Store.Update(who.Alex, m[8], new(Content: "DEMO: the Ashby allotment note 8, rewritten."), null, Mcp, default);
        Guid[] kept = [weakYoung, checkedLately, strongOld, unknownOld, outdated];
        var before = await LinkUpkeep.Rows(s, kept);

        var log = new LoggedLines<NightlyRunner>();
        var run = await LinkUpkeep.Pass(s, log: log);
        Assert.Equal((8L, 3L, 0L, 0L, false), (run.Counts["links checked"], run.Counts["automatic links pruned"],
            run.Counts["person- and AI-made links broken"], run.Counts["left for the next run"], run.More));
        Assert.All(await LinkUpkeep.Rows(s, weakOld, staleOld, retiredOld), r => Assert.Null(r.Value));
        // Nothing inside its grace period, nothing strong and active, nothing whose weakness is unknown is touched.
        Assert.Equal(before, await LinkUpkeep.Rows(s, kept));
        Assert.Contains(log.Lines, l => l.StartsWith("Memory links: 8 links checked, 3 weak or out of date automatic links pruned, 0 ", StringComparison.Ordinal));
        // A second pass changes nothing more: still inside their grace periods.
        Assert.Equal(0L, (await LinkUpkeep.Pass(s)).Counts["automatic links pruned"]);
        Assert.Equal(before, await LinkUpkeep.Rows(s, kept));

        // Two days on: the young weak link and the one invalidated by the change of words are past theirs; the checked
        // one is too; the strong and the unknown ones stay as long as they are active.
        foreach (var l in new[] { weakYoung, checkedLately, outdated }) await LinkUpkeep.Age(s, l, 7);
        run = await LinkUpkeep.Pass(s);
        Assert.Equal(3L, run.Counts["automatic links pruned"]);
        Assert.Equal(new Dictionary<Guid, string?> { [strongOld] = before[strongOld], [unknownOld] = before[unknownOld] },
            await LinkUpkeep.Rows(s, strongOld, unknownOld));
        // A pruned link leaves no dismissal: it was no one's judgement, and a link made afterwards is a new one.
        Assert.Equal(0L, await LinkUpkeep.Count(s, "SELECT count(*) FROM memory_link_dismissals"));
    }

}

// The trash keeps links: an automatic link with an end in the trash (a restore brought it back while the other end
// waits there) waits for it, however weak and old; once both ends are back, the next pass prunes it. A link whose end a
// writer holds right now is left for the next run. A database of its own, since it counts every link the pass checks.
public sealed class MemoryLinkPruningWaitTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    async Task<Guid[]> Remember(Guid who, string topic, int n)
    {
        var ids = new Guid[n];
        for (var i = 0; i < n; i++) ids[i] = (await Store.Create(who, new($"DEMO: {topic} note {i}."), Mcp, default)).Id;
        return ids;
    }

    [PostgresFact] public async Task A_link_with_an_end_in_the_trash_or_held_by_a_writer_waits()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-wait");
        await Scoped.Mode(s, "off");
        var m = await Remember(who.Alex, "the Ashby greenhouse", 4);
        var waiting = await LinkUpkeep.Suggested(s, m[0], m[1], 0.1, 30);
        var first = await Store.Forget(who.Alex, m[0], null, Mcp, default);
        var second = await Store.Forget(who.Alex, m[1], null, Mcp, default);
        // In m0's trash row, no pass sees it; m0 back, the link is back while m1 is still in the trash: it waits.
        Assert.Equal(0L, (await LinkUpkeep.Pass(s)).Counts["links checked"]);
        await Store.Restore(who.Alex, m[0], first.Trash.Nonce, Mcp, default);
        var run = await LinkUpkeep.Pass(s);
        Assert.Equal((1L, 0L), (run.Counts["links checked"], run.Counts["automatic links pruned"]));
        Assert.NotNull(await LinkUpkeep.Row(s, waiting));
        await Store.Restore(who.Alex, m[1], second.Trash.Nonce, Mcp, default);
        Assert.Equal(1L, (await LinkUpkeep.Pass(s)).Counts["automatic links pruned"]);
        Assert.Null(await LinkUpkeep.Row(s, waiting));

        // A writer holding an end (a change of that memory in its transaction): the link is left for the next run.
        var held = await LinkUpkeep.Suggested(s, m[2], m[3], 0.1, 30);
        await using (var c = await s.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using (var lockIt = new NpgsqlCommand("SELECT 1 FROM resources WHERE id=@id FOR UPDATE", c, tx))
            {
                lockIt.Parameters.AddWithValue("id", m[3]);
                await lockIt.ExecuteScalarAsync();
            }
            run = await LinkUpkeep.Pass(s);
            Assert.Equal((0L, 1L), (run.Counts["automatic links pruned"], run.Counts["left for the next run"]));
            await tx.RollbackAsync();
        }
        Assert.NotNull(await LinkUpkeep.Row(s, held));
        Assert.Equal(1L, (await LinkUpkeep.Pass(s)).Counts["automatic links pruned"]);
    }
}

// A link a person or an AI client stated is never deleted by any pass, however weak, old or broken; one that is broken
// (an end in the trash, marked wrong or held, or no longer of the kinds its type joins) is counted by the pass, so the
// night summary and the log report it, and a person sees the end's state in connections.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryLinkStatedTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    async Task<Guid> Remember(Guid who, string text, string? kind = null) => (await Store.Create(who, new(text, Kind: kind), Mcp, default)).Id;

    [PostgresFact] public async Task A_person_or_AI_made_link_survives_every_pass_and_is_reported_when_broken()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-stated");
        await Scoped.Mode(s, "off");
        try
        {
            var plan = await Remember(who.Alex, "DEMO: the Ottley shed plan.");
            var wood = await Remember(who.Alex, "DEMO: the Ottley shed needs larch.");
            var roof = await Remember(who.Sam, "DEMO: the Ottley shed roof is felt.");
            var door = await Remember(who.Sam, "DEMO: the Ottley shed door faces east.");
            var monday = await Remember(who.Alex, "DEMO: the Ottley shed went up on Monday.", "episodic");
            var tuesday = await Remember(who.Alex, "DEMO: the Ottley shed was painted on Tuesday.", "episodic");
            var gate = await Remember(who.Alex, "DEMO: the Ottley gate.");
            var post = await Remember(who.Alex, "DEMO: the Ottley gate post.");
            var terminal = await MemoryLogins.AtTerminal(s, who.Alex);
            var person = (await Store.Link(terminal, plan, wood, "relates_to", 0.05, new("cli", "rhythm"), default)).Id;
            var ai = (await Store.Link(who.Alex, wood, plan, "drawn_from", 0.1, Mcp, default)).Id;
            var imported = await LinkUpkeep.Stated(s, roof, door, "caused_by", "verified", 100, 0.05);
            var importedStale = await LinkUpkeep.Stated(s, door, roof, "relates_to", "inferred", 100, 0.05, lifecycle: "stale");
            var follows = (await Store.Link(who.Alex, tuesday, monday, "follows", null, Mcp, default)).Id;
            var toGate = (await Store.Link(who.Alex, post, gate, "part_of_project", null, Mcp, default)).Id;
            Guid[] stated = [person, ai, imported, importedStale, follows, toGate];
            foreach (var l in new[] { person, ai, follows, toGate }) await LinkUpkeep.Age(s, l, 100);

            // Broken, each its own way: wood marked wrong (both links at it), roof held for review, monday no episode any
            // more, and gate in the trash while the link came back with post (both forgotten, post restored first).
            await Store.SetStatus(who.Alex, wood, "deprecated", null, Mcp, default);
            await MemoryRows.Run(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", roof));
            await Store.Update(who.Alex, monday, new(Kind: "semantic"), null, Mcp, default);
            var postGone = await Store.Forget(who.Alex, post, null, Mcp, default);
            await Store.Forget(who.Alex, gate, null, Mcp, default);
            await Store.Restore(who.Alex, post, postGone.Trash.Nonce, Mcp, default);
            Assert.Equal("drawn_from:retracted follows:kinds part_of_project:trash relates_to:retracted caused_by:held relates_to:", await MemoryRows.Scalar(s, """
                SELECT string_agg(l.type||':'||coalesce(made_link_broken(l),''),' ' ORDER BY array_position(@ids,l.link_id)) FROM made_links l WHERE l.link_id=ANY(@ids)
                """, ("ids", new[] { ai, follows, toGate, person, imported, importedStale })));
            var before = await LinkUpkeep.Rows(s, stated);

            // Every pass, twice: the map's rebuild, the staleness pass, the link pruning pass and the whole daily turn with
            // the memory tools on and the night summary kept. Each stated link is exactly as it was.
            var registry = new AdapterRegistry([]);
            var db = new Database(s, registry.Policies(MemoryToolSettings.On));
            var graph = await new Principals(s).System(Principals.GraphName, default);
            var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var log = new LoggedLines<NightlyRunner>();
            var jobs = new LoggedLines<Scheduler>();
            try
            {
                var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(MemoryToolSettings.On, graph, state),
                    c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct), null, null), registry);
                for (var i = 0; i < 2; i++)
                {
                    Assert.Equal("complete", await host.Refresh(false, default));
                    await MemoryStaleness.Pass(s, Staleness.Context(s), log, MemoryStaleness.Piece, MemoryStaleness.PerScope, default);
                    var run = await LinkUpkeep.Pass(s, log: log);
                    Assert.Equal((6L, 0L, 5L), (run.Counts["links checked"], run.Counts["automatic links pruned"], run.Counts["person- and AI-made links broken"]));
                    var night = await new NightlyRunner(s, NightlyJobs.Daily(s, jobs, MemoryToolSettings.On), log, true, TimeZoneInfo.Utc)
                        .Run(default, LinkUpkeep.Night.AddDays(i));
                    Assert.All(night, o => Assert.Equal("complete", o.Outcome));
                    Assert.Equal(before, await LinkUpkeep.Rows(s, stated));
                }
            }
            finally { try { Directory.Delete(state, true); } catch (IOException) { } }

            // Reported: in the night summary (kinds and counts only) and in the engine's log, which names no link or word.
            Assert.Equal(5L, await LinkUpkeep.Count(s, "SELECT (counts->>'person- and AI-made links broken')::bigint FROM night_runs WHERE job=@j AND night=@n",
                ("j", MemoryLinkPruning.JobId), ("n", new DateOnly(2026, 11, 3))));
            var rows = await NightSummary.Read(s, new DateOnly(2026, 11, 3), default);
            Assert.Contains(NightSummary.Lines(rows), l => l.Contains("memory link pruning", StringComparison.Ordinal) && l.Contains("5 person- and AI-made links broken", StringComparison.Ordinal));
            Assert.Contains(jobs.Lines, l => l.Contains("5 person- or AI-made links kept though broken", StringComparison.Ordinal));
            Assert.DoesNotContain(jobs.Lines.Concat(log.Lines), l => l.Contains("DEMO", StringComparison.Ordinal) || l.Contains(plan.ToString(), StringComparison.Ordinal));
            // Where a person looks: the plan's connections name the neighbour marked wrong.
            var connections = (await Store.Connections(who.Alex, plan, null, 20, default))!.Items;
            Assert.All(connections, c => Assert.Equal((wood, "retracted"), (c.Memory.Id, c.Memory.State)));

            // Put right, a link is no longer broken; nothing about it changed meanwhile.
            await Store.SetStatus(who.Alex, wood, "draft", null, Mcp, default);
            Assert.Equal(3L, (await LinkUpkeep.Pass(s)).Counts["person- and AI-made links broken"]);
            Assert.Equal(before, await LinkUpkeep.Rows(s, stated));
        }
        finally { TestSettings.Use(); }
    }
}

// The pass stops at its own bound and at its budget and goes on from each scope's own cursor: each link is looked at
// once, none skipped, each scope from its own place. Its own database, so the scopes walked hold only these links.
public sealed class MemoryLinkPruningResumeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task The_pass_stops_at_its_bound_and_its_budget_and_resumes_from_each_scopes_cursor()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var who = await MemoryLogins.Make(s, "upkeep-resume");
            var store = new MemoryStore(s);
            async Task<List<Guid>> Weak(Guid by, string? scope, string topic, int n)
            {
                var links = new List<Guid>();
                var first = (await store.Create(by, new($"DEMO: {topic} hub.", Scope: scope), LinkUpkeep.Mcp, default)).Id;
                for (var i = 0; i < n; i++)
                {
                    var other = (await store.Create(by, new($"DEMO: {topic} note {i}.", Scope: scope), LinkUpkeep.Mcp, default)).Id;
                    links.Add(await LinkUpkeep.Suggested(s, first, other, 0.1, 30));
                }
                links.Sort();
                return links;
            }
            var alex = await Weak(who.Alex, null, "the Ashby pond", 7);
            var family = await Weak(who.Sam, "shared", "the Ashby fete", 3);
            async Task<string?> Cursor(string scope) => (string?)await MemoryRows.Scalar(s,
                "SELECT cursor->>'after' FROM job_cursors WHERE job=@j AND scope=@s", ("j", MemoryLinkPruning.JobId), ("s", scope));
            async Task<Guid[]> Left(IEnumerable<Guid> links) =>
                [.. (await LinkUpkeep.Rows(s, [.. links])).Where(r => r.Value is not null).Select(r => r.Key).Order()];

            // Stopped by its own budget's token after its first piece: the piece and its place stay.
            using (var budget = new CancellationTokenSource())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinkUpkeep.Pass(s, 2, 4, budget.Token, _ => budget.Cancel()));
                Assert.Equal(alex.Skip(2), await Left(alex));
                Assert.Equal(alex[1].ToString(), await Cursor("alex"));
            }
            // At its own bound, four a scope a run: partial, Alex's place kept, Family's walk ended.
            var run = await LinkUpkeep.Pass(s, 2, 4);
            Assert.Equal((4L + 3L, 4L + 3L, true), (run.Counts["links checked"], run.Counts["automatic links pruned"], run.More));
            Assert.Equal(alex.Skip(6), await Left(alex));
            Assert.Empty(await Left(family));
            Assert.Equal(alex[5].ToString(), await Cursor("alex"));
            Assert.Null(await Cursor("household"));
            // The next run goes on from there to the end of the walk, where the place is cleared.
            run = await LinkUpkeep.Pass(s, 2, 4);
            Assert.Equal((1L, 1L, false), (run.Counts["links checked"], run.Counts["automatic links pruned"], run.More));
            Assert.Empty(await Left(alex));
            Assert.Null(await Cursor("alex"));
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}

// A trip through the trash and back keeps every link: the person's, the AI client's, an import's, the automatic ones
// however weak and old, a supersede's and the dismissals, each exactly as it was, whether the restore is the trash's own,
// an undo, or both ends went and came back in either order; no pass meanwhile touches them.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryLinkTrashTripTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = LinkUpkeep.Mcp;

    async Task<Guid> Remember(Guid who, string text) => (await Store.Create(who, new(text), Mcp, default)).Id;

    [PostgresFact] public async Task A_trip_through_the_trash_and_back_keeps_every_link()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "upkeep-trip");
        await Scoped.Mode(s, "off");
        try
        {
            var older = await Remember(who.Alex, "DEMO: the Ashby bus left at eight.");
            var hub = await Remember(who.Alex, "DEMO: the Ashby bus leaves at nine.");
            var stop = await Remember(who.Alex, "DEMO: the Ashby bus stop moved.");
            var fare = await Remember(who.Sam, "DEMO: the Ashby bus fare is two pounds.");
            var map = await Remember(who.Sam, "DEMO: the Ashby bus map.");
            var depot = await Remember(who.Sam, "DEMO: the Ashby bus depot.");
            var plan = await Store.Supersede(who.Alex, hub, older, null, Mcp, default);
            await Store.Supersede(who.Alex, hub, older, plan.Confirmation, Mcp, default);
            var terminal = await MemoryLogins.AtTerminal(s, who.Alex);
            await Store.Link(terminal, hub, stop, "relates_to", null, new("cli", "rhythm"), default);
            await Store.Link(who.Alex, hub, fare, "drawn_from", 0.7, Mcp, default);
            await LinkUpkeep.Suggested(s, hub, map, 0.1, 30);
            await LinkUpkeep.Suggested(s, depot, hub, 0.9, 30, lifecycle: "stale", checkedDaysAgo: 30);
            await LinkUpkeep.Stated(s, depot, hub, "caused_by", "verified", 30);
            await MemoryRows.Run(s, "INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(least(@a,@b),greatest(@a,@b),'similar_to',@by)",
                ("a", hub), ("b", stop), ("by", who.Alex));
            var links = await LinkUpkeep.Kept(s, hub);
            var dismissals = await LinkUpkeep.Dismissals(s, hub);
            Assert.Equal(6L, await LinkUpkeep.Count(s, "SELECT count(*) FROM memory_links_kept(@m)", ("m", hub)));

            async Task Passes()
            {
                for (var i = 0; i < 2; i++)
                {
                    await LinkUpkeep.Pass(s);
                    var night = await new NightlyRunner(s, NightlyJobs.Daily(s, new LoggedLines<Scheduler>(), MemoryToolSettings.On),
                        new LoggedLines<NightlyRunner>(), false, TimeZoneInfo.Utc).Run(default, LinkUpkeep.Night.AddDays(i));
                    Assert.All(night, o => Assert.Equal("complete", o.Outcome));
                }
            }

            // Forgotten: every link waits in its trash row; the passes meanwhile touch none of them.
            var gone = await Store.Forget(who.Alex, hub, null, Mcp, default);
            Assert.Equal(6, gone.Trash.Links);
            Assert.Equal(0L, await LinkUpkeep.Count(s, "SELECT count(*) FROM made_links WHERE @m IN (source_id,target_id)", ("m", hub)));
            await Passes();
            Assert.Equal(links, await LinkUpkeep.Kept(s, hub));
            // Back through the trash's restore: every link and dismissal exactly as it was.
            var back = await Store.Restore(who.Alex, hub, gone.Trash.Nonce, Mcp, default);
            Assert.Equal((6, 0), (back.Links, back.LinksGone));
            Assert.Equal(links, await LinkUpkeep.Kept(s, hub));
            Assert.Equal(dismissals, await LinkUpkeep.Dismissals(s, hub));

            // Back as the undo tool brings back a forgotten memory (the trash's restore, with no nonce): the same.
            await Store.Forget(who.Alex, hub, null, Mcp, default);
            await Passes();
            await Store.Restore(who.Alex, hub, null, Mcp, default);
            Assert.Equal(links, await LinkUpkeep.Kept(s, hub));
            Assert.Equal(dismissals, await LinkUpkeep.Dismissals(s, hub));

            // Both ends of the automatic links forgotten too and brought back, in either order: every link at each end as
            // it was. The passes run while each is in the trash, and again once the first is back: a link back in made_links
            // while its other end waits in the trash waits for it, however weak and old.
            var depotLinks = await LinkUpkeep.Kept(s, depot);
            var mapLinks = await LinkUpkeep.Kept(s, map);
            foreach (var order in new[] { new[] { hub, depot, map }, new[] { depot, map, hub } })
            {
                var nonces = new Dictionary<Guid, Guid>();
                foreach (var m in new[] { hub, depot, map }) nonces[m] = (await Store.Forget(who.Alex, m, null, Mcp, default)).Trash.Nonce;
                await Passes();
                await Store.Restore(who.Alex, order[0], nonces[order[0]], Mcp, default);
                await Passes();
                foreach (var m in order.Skip(1)) await Store.Restore(who.Alex, m, nonces[m], Mcp, default);
                Assert.Equal(links, await LinkUpkeep.Kept(s, hub));
                Assert.Equal(depotLinks, await LinkUpkeep.Kept(s, depot));
                Assert.Equal(mapLinks, await LinkUpkeep.Kept(s, map));
                Assert.Equal(dismissals, await LinkUpkeep.Dismissals(s, hub));
            }

            // Back among live memories, the weak old automatic one is the pass's again, and the stale one too; every link
            // someone stated stays.
            var run = await LinkUpkeep.Pass(s);
            Assert.Equal(2L, run.Counts["automatic links pruned"]);
            Assert.Equal(4L, await LinkUpkeep.Count(s, "SELECT count(*) FROM memory_links_kept(@m)", ("m", hub)));
            Assert.Equal(0L, await LinkUpkeep.Count(s, "SELECT count(*) FROM memory_links_kept(@m) WHERE verification='suggested'", ("m", hub)));
        }
        finally { TestSettings.Use(); }
    }
}
