using Knowledge.Adapters;
using Knowledge.Relationships;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// Links a person or an AI client makes between two native memories (SB-P4-02; migration 052, Core/MemoryLinks.cs;
// docs/dev/memory.md sections 5 and 8; D-067, D-191, D-152): one scope only, the source end's author lock, no held
// memory, the episodic rule; removed only by its maker or whoever may change its source end; an automatic link dismissed,
// kept as a dismissal and never as a link; with memory.tools on, on the map for a reader of both ends through every
// rebuild and followed by combined retrieval's hops under the predicate, a held memory never; with it off, neither.
// Fictional people only: alex, sam and the household; every text is DEMO.
[Collection(SharedStateCollection.Name)]
public sealed class MadeLinkTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    // Recall's options as RecallServing gives them with memory.tools on: its hops follow made links.
    static readonly RecallOptions Tools = RecallOptions.Default with { MadeLinks = true };

    async Task<Guid> Remember(Guid who, string text, string? scope = null, string? kind = null) =>
        (await Store.Create(who, new(text, Scope: scope, Kind: kind), Mcp, default)).Id;

    // The end a link with no direction is stored from: the smaller id as the database orders uuids (their text).
    static Guid First(Guid a, Guid b) => string.CompareOrdinal(a.ToString("D"), b.ToString("D")) < 0 ? a : b;

    async Task<long> Links(Guid a, Guid b, string lifecycle = "active") => (long)(await MemoryLogins.Scalar(fixture.Source,
        "SELECT count(*) FROM made_links WHERE lifecycle=@l AND (source_id=@a AND target_id=@b OR source_id=@b AND target_id=@a)",
        ("l", lifecycle), ("a", a), ("b", b)))!;

    // The map's suggestions dismissed between two memories (memory_link_dismissals, either order).
    async Task<long> Dismissals(Guid a, Guid b) => (long)(await MemoryLogins.Scalar(fixture.Source,
        "SELECT count(*) FROM memory_link_dismissals WHERE low_id=least(@a,@b) AND high_id=greatest(@a,@b)", ("a", a), ("b", b)))!;

    async Task Scoped_on()
    {
        await Scoped.Mode(fixture.Source, "on");
        await MemoryLogins.Family(fixture.Source);
    }

    async Task Scoped_off()
    {
        await MemoryLogins.Family(fixture.Source);
        await Scoped.Mode(fixture.Source, "off");
    }

    [PostgresFact] public async Task A_link_joins_two_memories_of_one_scope_from_one_the_caller_may_change()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-make");
        await Scoped_on();
        try
        {
            var walk = await Remember(who.Alex, "DEMO: Alex's private note: the Harlow canal walk.");
            var map = await Remember(who.Alex, "DEMO: Alex's private note: the Harlow canal map.");
            var family = await Remember(who.Alex, "DEMO: the Harlow canal picnic, for everyone.", "shared");
            var sams = await Remember(who.Sam, "DEMO: Sam's Family note: bring the Harlow kite.", "shared");

            // An AI client's link is inferred (D-015), with the source end's revision it was made from and when.
            var made = await Store.Link(who.Alex, walk, map, "drawn_from", 0.7, Mcp, default);
            Assert.Equal((walk, map, walk, map, "drawn_from", true, "inferred", true, false, "alex", "private", "on"),
                (made.Source, made.Target, made.From, made.To, made.Type, made.Directed, made.Verification, made.Created, made.Unchanged,
                 made.Subject, made.Scope, made.Scopes));
            Assert.Equal($"inferred|0.7|memory:1|{who.Alex}|t|f", await MemoryLogins.Scalar(s, """
                SELECT concat_ws('|',verification,confidence,source_revision,made_by,checked_at IS NOT NULL,imported) FROM made_links WHERE link_id=@l
                """, ("l", made.Id)));
            // Linking again changes nothing; relates_to has no direction and is stored smaller id first (D-191).
            var again = await Store.Link(who.Alex, walk, map, "drawn_from", null, Mcp, default);
            Assert.Equal((made.Id, false, true), (again.Id, again.Created, again.Unchanged));
            var related = await Store.Link(who.Alex, map, walk, "relates_to", null, Mcp, default);
            Assert.Equal((false, First(map, walk)), (related.Directed, related.From));
            Assert.Equal(related.Id, (await Store.Link(who.Alex, walk, map, "relates_to", null, Mcp, default)).Id);
            Assert.Equal(2L, await Links(walk, map));

            // Two scopes never link, either way; nothing is written.
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, family, "relates_to", null, Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, family, walk, "drawn_from", null, Mcp, default)));
            Assert.Equal(0L, await Links(walk, family));
            // A memory the caller does not read answers as one that never was.
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Link(who.Sam, sams, walk, "relates_to", null, Mcp, default)));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Link(who.Sam, walk, map, "relates_to", null, Mcp, default)));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, Guid.NewGuid(), "relates_to", null, Mcp, default)));
            // In Family, from a memory the caller may change: Sam from his own to Alex's, never from Alex's (the author lock).
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Link(who.Sam, family, sams, "relates_to", null, Mcp, default)));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Link(who.Sam, family, sams, "caused_by", null, Mcp, default)));
            Assert.True((await Store.Link(who.Sam, sams, family, "caused_by", null, Mcp, default)).Created);
            // The household view reads Family, and changes a Family memory a person wrote only as an admin may: never.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Link(who.House, family, sams, "about_person", null, Mcp, default)));
            // A Family admin may.
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.True((await Store.Link(who.Sam, family, sams, "part_of_project", null, Mcp, default)).Created);

            // The vocabulary: upstream's types but supersedes (supersede's), and decided and follows only between two
            // episodic memories (051's rule).
            foreach (var wrong in new[] { "supersedes", "similar_to", "in_project", "", "no such type" })
                Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, map, wrong, null, Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, map, "follows", null, Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, map, "relates_to", 1.5, Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, walk, "relates_to", null, Mcp, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, map, "relates_to", null, MemoryLogins.Key("links-key-0001"), default)));
            var day = await Remember(who.Alex, "DEMO: Alex's private note: the Harlow walk on Saturday.", kind: "episodic");
            var after = await Remember(who.Alex, "DEMO: Alex's private note: the Harlow walk on Sunday.", kind: "episodic");
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Link(who.Alex, after, map, "follows", null, Mcp, default)));
            Assert.True((await Store.Link(who.Alex, after, day, "follows", null, Mcp, default)).Created);

            // A memory held for review is linked only once a person releases it; nothing is written.
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", map));
            Assert.Equal("held", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, map, "caused_by", null, Mcp, default)));
            Assert.Equal("held", await MemoryLogins.Refused(() => Store.Link(who.Alex, map, walk, "caused_by", null, Mcp, default)));
            Assert.Equal(2L, await Links(walk, map));
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", map));
            // In the trash: unknown, as for any memory the caller does not read now.
            await Store.Forget(who.Alex, day, null, Mcp, default);
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Link(who.Alex, walk, day, "relates_to", null, Mcp, default)));

            // A person's own channel makes a verified link (the terminal's login).
            var terminal = await MemoryLogins.AtTerminal(s, who.Alex);
            var person = await Store.Link(terminal, walk, map, "caused_by", null, new("cli", "rhythm"), default);
            Assert.Equal("verified", person.Verification);
            // And a person confirms an AI client's link: inferred becomes verified, the same link.
            var confirmed = await Store.Link(terminal, walk, map, "drawn_from", null, new("cli", "rhythm"), default);
            Assert.Equal((made.Id, "verified", false), (confirmed.Id, confirmed.Verification, confirmed.Unchanged));
            // Through an AI client it never goes back down.
            Assert.True((await Store.Link(who.Alex, walk, map, "drawn_from", null, Mcp, default)).Unchanged);
            Assert.Equal("verified", await MemoryLogins.Scalar(s, "SELECT verification FROM made_links WHERE link_id=@l", ("l", made.Id)));
        }
        finally { await Scoped_off(); }
    }

    // With scopes off every admitted login may link and unlink, as the household shares everything (memory.md section 5,
    // D-048); one scope, the household's.
    [PostgresFact] public async Task With_scopes_off_every_login_links_and_unlinks_household_memories()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-off");
        await Scoped.Mode(s, "off");
        var a = await Remember(who.Alex, "DEMO: the Tolley bins go out on Thursday.");
        var b = await Remember(who.Alex, "DEMO: the Tolley recycling goes out on Friday.");
        var link = await Store.Link(who.Sam, a, b, "relates_to", null, Mcp, default);
        Assert.Equal(("household", "shared", "off"), (link.Subject, link.Scope, link.Scopes));
        var gone = await Store.Unlink(who.House, b, a, null, Mcp, default);
        Assert.Equal([new MemoryLinkRef(link.Id, "relates_to")], gone.Removed);
        Assert.Equal(0L, await Links(a, b));
        Assert.True((await Store.Unlink(who.House, b, a, null, Mcp, default)).Unchanged);
    }

    [PostgresFact] public async Task Only_its_maker_or_whoever_may_change_its_source_end_removes_a_made_link()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-remove");
        await Scoped_on();
        try
        {
            var picnic = await Remember(who.Alex, "DEMO: the Ottery picnic is on the green.", "shared");
            var rain = await Remember(who.Alex, "DEMO: the Ottery picnic moves indoors if it rains.", "shared");
            var kite = await Remember(who.Sam, "DEMO: Sam brings the Ottery kite.", "shared");
            var link = await Store.Link(who.Alex, picnic, rain, "relates_to", null, Mcp, default);
            var sams = await Store.Link(who.Sam, kite, picnic, "drawn_from", null, Mcp, default);

            // Sam, a member but no admin, made neither end of Alex's link and wrote neither: refused, nothing changes.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Unlink(who.Sam, picnic, rain, null, Mcp, default)));
            // Alex wrote the target end of Sam's link, not its source, and did not make it: refused too.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Unlink(who.Alex, kite, picnic, null, Mcp, default)));
            // The household view reads Family and is no admin: refused.
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Unlink(who.House, picnic, rain, null, Mcp, default)));
            Assert.Equal(1L, await Links(picnic, rain));
            Assert.Equal(1L, await Links(kite, picnic));
            // Unlinking a type there is none of changes nothing; the order the ends are named in does not matter.
            Assert.True((await Store.Unlink(who.Alex, rain, picnic, "caused_by", Mcp, default)).Unchanged);
            // Another login of the link's maker removes it (the maker is a person, not one token).
            var other = await Scoped.Principal(s, "links-remove-alex-2", "harness", "alex");
            var removed = await Store.Unlink(other, rain, picnic, "relates_to", Mcp, default);
            Assert.Equal([new MemoryLinkRef(link.Id, "relates_to")], removed.Removed);
            Assert.False(removed.Unchanged);
            Assert.Empty(removed.Kept);
            Assert.Equal(0L, await Links(picnic, rain));
            Assert.Equal(0L, await Links(picnic, rain, "revoked"));
            // A Family admin removes anyone's link in Family.
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='alex'");
            Assert.Single((await Store.Unlink(who.Alex, picnic, kite, null, Mcp, default)).Removed, r => r.Id == sams.Id);
            await Scoped.Execute(s, "UPDATE memberships SET role='member'");

            // A link an import brought has no maker: the rest of the rule, its source end's author here (caused_by has a direction).
            var imported = Guid.NewGuid();
            await MemoryRows.Run(s, """
                INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by,imported)
                VALUES(@l,@a,@b,'caused_by','verified','DEMO',NULL,true)
                """, ("l", imported), ("a", kite), ("b", rain));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Unlink(who.Alex, kite, rain, null, Mcp, default)));
            Assert.Single((await Store.Unlink(who.Sam, kite, rain, null, Mcp, default)).Removed);

            // An automatic link (one marked suggested, as an import may bring and links on save will make) is dismissed:
            // the link goes and a dismissal of its type is kept (never a revoked link, which a link check would count), so
            // it is never suggested again; an AI client's explicit link of its type is a new link beside the dismissal, which
            // only a person's channel takes back (057).
            var suggested = Guid.NewGuid();
            await MemoryRows.Run(s, """
                INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by)
                VALUES(@l,@a,@b,'relates_to','suggested','',@p)
                """, ("l", suggested), ("a", First(picnic, rain)), ("b", First(picnic, rain) == picnic ? rain : picnic), ("p", who.Alex));
            var dismissed = await Store.Unlink(who.Alex, picnic, rain, null, Mcp, default);
            Assert.Empty(dismissed.Removed);
            Assert.Equal([new MemoryLinkRef(suggested, "relates_to")], dismissed.Kept);
            Assert.Equal((0L, 0L, 1L), (await Links(picnic, rain), await Links(picnic, rain, "revoked"), await Dismissals(picnic, rain)));
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM made_links WHERE link_id=@l", ("l", suggested)));
            Assert.True((await Store.Unlink(who.Alex, picnic, rain, null, Mcp, default)).Unchanged);
            var anew = await Store.Link(who.Alex, picnic, rain, "relates_to", null, Mcp, default);
            Assert.NotEqual(suggested, anew.Id);
            Assert.Equal((true, "inferred"), (anew.Created, anew.Verification));
            Assert.Equal((1L, 0L, 1L), (await Links(picnic, rain), await Links(picnic, rain, "revoked"), await Dismissals(picnic, rain)));

            // A memory in another person's private scope: unknown, whoever made a link.
            var mine = await Remember(who.Alex, "DEMO: Alex's private Ottery list.");
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Unlink(who.Sam, mine, picnic, null, Mcp, default)));
        }
        finally { await Scoped_off(); }
    }

    // A map built over this database, its memory stars' vectors from `vector` so the rebuild's similarity suggests links
    // between memories that share a word.
    sealed record Map(RelationshipHost Host, string State) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(State, true); } catch (IOException) { } }
        public async Task<GraphPage> Page(Guid reader) => await Host.Read(reader, null, null, default, new GraphQuery());
        public async Task<string[]> Edges(Guid reader) => [.. (await Page(reader)).edges.Select(e => e.id)];
    }

    // With memory.tools on unless `tools` says off (RelationshipCatalog.MadeLinks): only then does the map read made links.
    async Task<Map> Built(Func<string, string, float[]?>? vector = null, bool tools = true)
    {
        TestSettings.Use();
        var config = tools ? MemoryToolSettings.On : TestSettings.Use();
        var registry = new AdapterRegistry([]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { SimilarityThreshold = 0.5 },
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct),
                vector, vector is null ? null : "demo-vectors"), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        return new(host, state);
    }

    static string Star(Guid memory, string subject) => MemoryStars.Key(subject, memory).GraphId;

    [PostgresFact] public async Task A_made_link_is_on_the_map_for_a_reader_of_both_ends_through_every_rebuild()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-map");
        await Scoped_on();
        try
        {
            var walk = await Remember(who.Alex, "DEMO: Alex's private note: the Brackley walk.");
            var boots = await Remember(who.Alex, "DEMO: Alex's private note: the Brackley boots.");
            var fete = await Remember(who.Alex, "DEMO: the Brackley fete is on Saturday.", "shared");
            var stall = await Remember(who.Alex, "DEMO: the Brackley fete's cake stall.", "shared");
            var mine = await Store.Link(who.Alex, walk, boots, "caused_by", null, Mcp, default);
            var ours = await Store.Link(who.Alex, stall, fete, "part_of_project", 0.9, Mcp, default);
            using var map = await Built();
            var made = $"made-link:{mine.Id:D}";
            var shared = $"made-link:{ours.Id:D}";

            // Alex reads both ends of both; Sam and the household view only the Family one.
            var alex = await map.Page(who.Alex);
            var edge = Assert.Single(alex.edges, e => e.id == made);
            Assert.Equal((Star(walk, "alex"), Star(boots, "alex"), "caused_by", "inferred", true),
                (edge.source, edge.target, edge.relationship, edge.verification, edge.directed));
            Assert.Equal($"made-link:{mine.Id:D}", Assert.Single(edge.evidence).locator);
            Assert.Equal("", edge.evidence[0].excerpt);
            Assert.Equal(0.9, Assert.Single(alex.edges, e => e.id == shared).strength);
            foreach (var reader in new[] { who.Sam, who.House })
            {
                var edges = await map.Edges(reader);
                Assert.Contains(shared, edges);
                Assert.DoesNotContain(made, edges);
            }
            // explore_graph and graph_changes serve it too, with when it was made.
            var around = await map.Host.Explore(who.Alex, null, Star(walk, "alex"), 1, 60, default);
            Assert.Single(around.links, l => l.target == Star(boots, "alex") && l.type == "caused_by");
            var changes = await map.Host.Changes(who.Alex, DateTimeOffset.UtcNow.AddDays(-1), 100, default);
            Assert.Contains(changes.links, l => l.source == Star(walk, "alex") && l.addedAt is not null);

            // Rebuilds never touch a made link: the same row, still on the map.
            var row = await MemoryLogins.Scalar(s, "SELECT to_jsonb(l)::text FROM made_links l WHERE link_id=@l", ("l", mine.Id));
            Assert.Equal("complete", await map.Host.Refresh(false, default));
            Assert.Equal("complete", await map.Host.Refresh(false, default));
            Assert.Equal(row, await MemoryLogins.Scalar(s, "SELECT to_jsonb(l)::text FROM made_links l WHERE link_id=@l", ("l", mine.Id)));
            Assert.Contains(made, await map.Edges(who.Alex));
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM relationships WHERE type='caused_by'"));

            // A held end takes it off the map at once, with no rebuild; released, it is back.
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", boots));
            Assert.DoesNotContain(made, await map.Edges(who.Alex));
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", boots));
            Assert.Contains(made, await map.Edges(who.Alex));
            // Unlinked, it is gone at the next read.
            await Store.Unlink(who.Alex, boots, walk, null, Mcp, default);
            Assert.DoesNotContain(made, await map.Edges(who.Alex));
        }
        finally { await Scoped_off(); }
    }

    // The map's own suggestion between two memories (a similarity the rebuild found) is dismissed by unlinking it: kept as
    // a row of memory_link_dismissals, so no read of the map, no hop and no rebuild after serves it again.
    [PostgresFact] public async Task Unlinking_a_suggestion_dismisses_it_through_every_rebuild()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-dismiss");
        await Scoped.Mode(s, "off");
        var heron = await Remember(who.Alex, "DEMO: the Pellingham heron nests by the weir.");
        var weir = await Remember(who.Sam, "DEMO: the Pellingham heron fishes at dawn.");
        static float[]? Vector(string title, string text) =>
            (title + " " + text).Contains("Pellingham", StringComparison.Ordinal) ? [1f, 0f, 0f, 0f] : [0f, 0f, 1f, 0f];
        using var map = await Built(Vector);
        bool Between(GraphEdge e) => new[] { e.source, e.target }.Order().SequenceEqual(new[] { Star(heron, "household"), Star(weir, "household") }.Order());
        var suggestion = Assert.Single((await map.Page(who.Sam)).edges, e => Between(e));
        Assert.Equal((SimilaritySuggestions.Type, "suggested"), (suggestion.relationship, suggestion.verification));

        // Its connections list the suggestion, and whoever may change either memory may dismiss it.
        var listed = Assert.Single((await Store.Connections(who.Alex, heron, null, 20, default))!.Items);
        Assert.Equal((null, false, SimilaritySuggestions.Type, "none", "suggested", true, weir),
            (listed.LinkId, listed.Made, listed.Type, listed.Direction, listed.Verification, listed.MayUnlink, listed.Memory.Id));
        var db = RecallRows.Db(s);
        var words = new Recall(db, null, PassageSources.Of(false, false, true, []), options: Tools);
        Assert.Contains(weir, (await words.Search(who.Alex, new("nests weir", 20, 1), default)).Hits.Select(h => h.Id));

        // A made link beside it: one unlink removes the link and dismisses the suggestion, as upstream's removes links of
        // every provenance.
        // Unlinking another type leaves the suggestion as it is.
        Assert.True((await Store.Unlink(who.Alex, heron, weir, "relates_to", Mcp, default)).Unchanged);
        Assert.Single((await map.Page(who.Sam)).edges, e => Between(e));
        var beside = await Store.Link(who.Alex, heron, weir, "relates_to", null, Mcp, default);
        var gone = await Store.Unlink(who.Alex, heron, weir, null, Mcp, default);
        Assert.Equal([new MemoryLinkRef(beside.Id, "relates_to")], gone.Removed);
        Assert.Empty(gone.Kept);
        Assert.Equal([SimilaritySuggestions.Type], gone.Dismissed);
        Assert.Equal((0, false), (gone.Already, gone.Unchanged));
        Assert.Equal(1L, await Dismissals(heron, weir));
        Assert.Equal(0L, await Links(heron, weir, "revoked"));
        Assert.Equal(0L, await Links(heron, weir));
        foreach (var reader in new[] { who.Alex, who.Sam, who.House })
            Assert.DoesNotContain((await map.Page(reader)).edges, Between);
        Assert.Empty((await Store.Connections(who.Sam, weir, null, 20, default))!.Items);
        Assert.DoesNotContain(weir, (await words.Search(who.Alex, new("nests weir", 20, 1), default)).Hits.Select(h => h.Id));
        // The rebuild finds it again and keeps it in its own table; it stays dismissed.
        Assert.Equal("complete", await map.Host.Refresh(false, default));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, """
            SELECT count(*) FROM relationships l JOIN resources a ON a.id=l.source_id JOIN resources b ON b.id=l.target_id
            WHERE l.lifecycle='active' AND l.type=@t AND a.external_id IN (@x,@y) AND b.external_id IN (@x,@y)
            """, ("t", SimilaritySuggestions.Type), ("x", MemoryStars.Key("household", heron).CatalogId), ("y", MemoryStars.Key("household", weir).CatalogId)));
        Assert.DoesNotContain((await map.Page(who.Sam)).edges, Between);
        var twice = await Store.Unlink(who.Sam, weir, heron, null, Mcp, default);
        Assert.Equal((true, 1), (twice.Unchanged, twice.Already));
        // A link of another type stated afterwards is the caller's own, beside the dismissal, which it leaves.
        var stated = await Store.Link(who.Alex, heron, weir, "relates_to", null, Mcp, default);
        Assert.Contains($"made-link:{stated.Id:D}", await map.Edges(who.Sam));
        Assert.Equal(1L, await Dismissals(heron, weir));
    }

    // A supersede's link (049) says the earlier memory is replaced and the retraction cascade follows it: unlinking every
    // type leaves it, naming it is refused, and connections never offers it, so the replaced memory stays replaced and
    // marking the newer one wrong still reopens it.
    [PostgresFact] public async Task Unlinking_every_type_leaves_a_supersede_and_naming_it_is_refused()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-supersede");
        await Scoped.Mode(s, "off");
        var old = await Remember(who.Alex, "DEMO: the Merrow surgery opens at eight.");
        var fresh = await Remember(who.Alex, "DEMO: the Merrow surgery opens at nine now.");
        var plan = await Store.Supersede(who.Alex, fresh, old, null, Mcp, default);
        await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, Mcp, default);
        var related = await Store.Link(who.Alex, fresh, old, "relates_to", null, Mcp, default);
        Assert.Equal(("replaced", (Guid?)fresh), ((await Store.Get(who.Alex, old, default))!.State, (await Store.Get(who.Alex, old, default))!.ReplacedBy));

        var listed = (await Store.Connections(who.Alex, fresh, null, 20, default))!.Items;
        Assert.False(Assert.Single(listed, i => i.Type == "supersedes").MayUnlink);
        Assert.True(Assert.Single(listed, i => i.Type == "relates_to").MayUnlink);

        var gone = await Store.Unlink(who.Alex, old, fresh, null, Mcp, default);
        Assert.Equal([new MemoryLinkRef(related.Id, "relates_to")], gone.Removed);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Unlink(who.Alex, fresh, old, "supersedes", Mcp, default)));
        Assert.Equal(1L, await Links(fresh, old));
        Assert.True((await Store.Unlink(who.Alex, fresh, old, null, Mcp, default)).Unchanged);
        var replaced = (await Store.Get(who.Alex, old, default))!;
        Assert.Equal(("replaced", (Guid?)fresh), (replaced.State, replaced.ReplacedBy));

        // The cascade still finds it: marked wrong, the newer memory gives the earlier one back.
        var wrong = await Store.SetStatus(who.Alex, fresh, "deprecated", null, Mcp, default);
        Assert.Equal([old], wrong.Cascade!.Reopened);
        Assert.Equal("current", (await Store.Get(who.Alex, old, default))!.State);
    }

    // relates_to has no direction and is stored smaller id first, so who may remove one is whoever may change either end,
    // as either end's author could have made it: the same for both orders of the ids.
    [PostgresFact] public async Task Either_ends_author_removes_a_relates_to_link_whichever_id_is_smaller()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-either");
        await Scoped_on();
        try
        {
            // Twelve memories each, so that one pair has Sam's stored first and another Alex's: the ids are random, and one of
            // Alex's against forty of Sam's left every one of them on one side about one run in twenty (2/41), while twelve
            // against twelve leave none on either side about once in a million runs.
            var alexes = new List<Guid>();
            var samses = new List<Guid>();
            for (var i = 0; i < 12; i++)
            {
                alexes.Add(await Remember(who.Alex, $"DEMO: the Selby fair's tea tent, note {i}.", "shared"));
                samses.Add(await Remember(who.Sam, $"DEMO: Sam's Selby fair note {i}.", "shared"));
            }
            var before = alexes.SelectMany(a => samses.Select(m => (Alex: a, Sam: m))).First(p => First(p.Alex, p.Sam) == p.Sam);
            var after = alexes.SelectMany(a => samses.Select(m => (Alex: a, Sam: m))).First(p => p.Sam != before.Sam && First(p.Alex, p.Sam) == p.Alex);
            foreach (var (alexs, sams) in new[] { before, after })
            {
                var link = await Store.Link(who.Alex, alexs, sams, "relates_to", null, Mcp, default);
                Assert.Equal(First(alexs, sams), link.From);
                // A directed link from Alex's memory is still the source end's: Sam may not remove it.
                await Store.Link(who.Alex, alexs, sams, "caused_by", null, Mcp, default);
                Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Unlink(who.Sam, sams, alexs, "caused_by", Mcp, default)));
                Assert.True(Assert.Single((await Store.Connections(who.Sam, sams, "relates_to", 20, default))!.Items).MayUnlink);
                Assert.Equal([new MemoryLinkRef(link.Id, "relates_to")], (await Store.Unlink(who.Sam, sams, alexs, "relates_to", Mcp, default)).Removed);
            }
            // Someone who may change neither end still may not.
            var third = await Remember(who.Alex, "DEMO: the Selby fair's raffle.", "shared");
            await Store.Link(who.Alex, before.Alex, third, "relates_to", null, Mcp, default);
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Unlink(who.Sam, third, before.Alex, null, Mcp, default)));
        }
        finally { await Scoped_off(); }
    }

    // A dismissal is no link: one between a person's private memory and a Family memory (a suggestion the map's bridges
    // found) never keeps the Family memory's author from taking it back, which 048 refuses for a made link to another
    // person's private record.
    [PostgresFact] public async Task Dismissing_a_suggestion_across_two_scopes_never_stops_a_share_being_taken_back()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-bridge");
        await Scoped_on();
        try
        {
            var alexs = await Remember(who.Alex, "DEMO: Alex's private note: the Tamsby lantern walk.");
            var sams = await Remember(who.Sam, "DEMO: Sam's Family note: the Tamsby lantern parade.", "shared");
            var other = await Remember(who.Sam, "DEMO: Sam's Family note: the Ardley bake sale.", "shared");
            static float[]? Vector(string title, string text) =>
                (title + " " + text).Contains("Tamsby", StringComparison.Ordinal) ? [1f, 0f, 0f, 0f] : [0f, 0f, 1f, 0f];
            using var map = await Built(Vector);
            bool Between(GraphEdge e) => new[] { e.source, e.target }.Order().SequenceEqual(new[] { Star(alexs, "alex"), Star(sams, "household") }.Order());
            Assert.Single((await map.Page(who.Alex)).edges, e => Between(e));

            var dismissed = await Store.Unlink(who.Alex, alexs, sams, null, Mcp, default);
            Assert.NotEmpty(dismissed.Dismissed);
            Assert.Equal((1L, 0L), (await Dismissals(alexs, sams), await Links(alexs, sams, "revoked")));
            Assert.DoesNotContain((await map.Page(who.Alex)).edges, Between);

            // Sam takes his memory back, as he does the one with no dismissal; the dismissal stays and keeps holding.
            Assert.Equal("private", (await Store.Share(who.Sam, sams, "private", null, Mcp, default)).Scope);
            Assert.Equal("private", (await Store.Share(who.Sam, other, "private", null, Mcp, default)).Scope);
            Assert.Equal(1L, await Dismissals(alexs, sams));
        }
        finally { await Scoped_off(); }
    }

    // D-152 closed for made links: a link the made-link function writes reaches its memory through Recall's hops, its own
    // text the excerpt, only for a reader of both ends, and a held memory never.
    [PostgresFact] public async Task A_made_link_reaches_its_memory_through_recall_hops_under_the_predicate_and_a_held_one_never()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-recall");
        await Scoped_on();
        try
        {
            const string far = "DEMO: Alex's private note: the spare key is under the blue pot.";
            var near = await Remember(who.Alex, "DEMO: Alex's private note: the Quillon shed lock sticks.");
            var key = await Remember(who.Alex, far);
            var db = RecallRows.Db(s);
            var words = new Recall(db, null, PassageSources.Of(false, false, true, []), options: Tools);
            async Task<RecallHit[]> Ask(Guid reader, int hops) => [.. (await words.Search(reader, new("Quillon shed", 20, hops), default)).Hits];
            Assert.DoesNotContain(key, (await Ask(who.Alex, 3)).Select(h => h.Id));

            var link = await Store.Link(who.Alex, near, key, "relates_to", null, Mcp, default);
            Assert.DoesNotContain(key, (await Ask(who.Alex, 0)).Select(h => h.Id));
            foreach (var hops in new[] { 1, 2, 3 })
            {
                var reached = Assert.Single(await Ask(who.Alex, hops), h => h.Id == key);
                Assert.Equal((1, "relates_to", near), (reached.Why.Hop, reached.Why.Link, reached.Why.From));
                Assert.Equal(far, reached.Excerpt);
            }

            // Shared into Family, the memory found is Sam's to read; the one it links to is still Alex's alone, so the hop
            // reaches it for her and for no one else.
            await Store.Share(who.Alex, near, "shared", null, Mcp, default);
            foreach (var reader in new[] { who.Sam, who.House })
                foreach (var hops in new[] { 0, 1, 2, 3 })
                {
                    var hits = await Ask(reader, hops);
                    Assert.Contains(near, hits.Select(h => h.Id));
                    Assert.DoesNotContain(key, hits.Select(h => h.Id));
                    Assert.All(hits, h => Assert.DoesNotContain("spare key", h.Excerpt));
                }
            Assert.Contains(key, (await Ask(who.Alex, 1)).Select(h => h.Id));
            Assert.Equal(1L, await Links(near, key));

            // Held: never reached, the link still there.
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", key));
            foreach (var hops in new[] { 0, 1, 2, 3 }) Assert.DoesNotContain(key, (await Ask(who.Alex, hops)).Select(h => h.Id));
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", key));
            // Unlinked: no longer reached.
            Assert.Single((await Store.Unlink(who.Alex, key, near, null, Mcp, default)).Removed, r => r.Id == link.Id);
            Assert.DoesNotContain(key, (await Ask(who.Alex, 1)).Select(h => h.Id));
        }
        finally { await Scoped_off(); }
    }

    // With memory.tools off (every install's default) the map and recall's hops read no made link, so an operator's import
    // that brought some in (043, deployed, behind no setting) changes neither; with it on, both serve the link.
    [PostgresFact] public async Task With_the_memory_tools_off_an_imported_made_link_leaves_the_map_and_the_hops_as_before()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-tools-off");
        await Scoped.Mode(s, "off");
        const string far = "DEMO: the spare padlock code is written on the jar.";
        var near = await Remember(who.Alex, "DEMO: the Brenlow allotment shed is by the gate.");
        var key = await Remember(who.Alex, far);
        // As `rhythm import` writes one: no maker, marked imported.
        var imported = Guid.NewGuid();
        await MemoryRows.Run(s, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by,imported)
            VALUES(@l,@a,@b,'caused_by','verified','',NULL,true)
            """, ("l", imported), ("a", near), ("b", key));
        var made = $"made-link:{imported:D}";

        Assert.False(RelationshipCatalog.For(TestSettings.Use()).MadeLinks);
        Assert.True(RelationshipCatalog.For(MemoryToolSettings.On).MadeLinks);
        using (var off = await Built(tools: false))
        {
            foreach (var reader in new[] { who.Alex, who.Sam, who.House })
                Assert.DoesNotContain(made, await off.Edges(reader));
            var around = await off.Host.Explore(who.Alex, null, Star(near, "household"), 1, 60, default);
            Assert.DoesNotContain(around.links, l => l.type == "caused_by");
        }
        using (var on = await Built())
            Assert.Contains(made, await on.Edges(who.Alex));

        var db = RecallRows.Db(s);
        IServiceProvider Services(RhythmConfig config) => new ServiceCollection().AddSingleton(db).AddSingleton(config)
            .AddSingleton<RecallFactory>((o, log) => new Recall(db, null, PassageSources.Of(false, false, true, []), options: o, log: log))
            .BuildServiceProvider();
        async Task<Guid[]> Ask(RhythmConfig config, int hops) =>
            [.. (await RecallServing.For(Services(config)).Search(who.Alex, new("Brenlow allotment", 20, hops), default)).Hits.Select(h => h.Id)];
        foreach (var hops in new[] { 0, 1, 2, 3 })
        {
            var offHits = await Ask(TestSettings.Use(), hops);
            Assert.Contains(near, offHits);
            Assert.DoesNotContain(key, offHits);
        }
        Assert.Contains(key, await Ask(MemoryToolSettings.On, 1));
        TestSettings.Use();
    }

    // As of a moment, a hop follows only a made link made by then: one made after it did not exist then (D-195), while one
    // made before it is followed, and asked now both are.
    [PostgresFact] public async Task As_of_a_moment_a_hop_follows_no_made_link_made_after_it()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-asof");
        await Scoped.Mode(s, "off");
        var near = await Remember(who.Alex, "DEMO: the Tolland choir sings on Thursdays.");
        var before = await Remember(who.Alex, "DEMO: the hall's side door sticks in the rain.");
        var after = await Remember(who.Alex, "DEMO: the new organ arrives in spring.");
        await Store.Link(who.Alex, near, before, "relates_to", null, Mcp, default);
        var at = await MemoryLogins.Now(s);
        await Store.Link(who.Alex, near, after, "relates_to", null, Mcp, default);
        // One link made by the moment, one after it.
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM made_links WHERE @n IN (source_id,target_id) AND created_at<=@at",
            ("n", near), ("at", at)));
        var recall = new Recall(RecallRows.Db(s), null, PassageSources.Of(false, false, true, []), options: Tools);

        Guid[] Found(RecallResult r) => [.. r.Hits.Select(h => h.Id)];
        var then = Found(await recall.Search(who.Alex, new("Tolland choir", 20, Hops: 1, AsOf: at), default));
        Assert.Contains(near, then);
        Assert.Contains(before, then);
        Assert.DoesNotContain(after, then);
        var now = Found(await recall.Search(who.Alex, new("Tolland choir", 20, Hops: 1), default));
        Assert.Contains(before, now);
        Assert.Contains(after, now);
    }

    // A suggested made link between two Family memories goes when one of them moves to its author's private scope (062:
    // an automatic link never joins two scopes), so it never keeps the other in Family; a stated one stays and does, as
    // 048 refuses while a link to another person's private record stands, until a person unlinks it.
    [PostgresFact] public async Task A_suggested_made_link_never_stops_a_share_being_taken_back()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-unshare");
        await Scoped_on();
        try
        {
            var alexs = await Remember(who.Alex, "DEMO: the Darnell street party is on Sunday.", "shared");
            var sams = await Remember(who.Sam, "DEMO: Sam's note: the Darnell street party's bunting.", "shared");
            var other = await Remember(who.Alex, "DEMO: the Darnell bin day moved.", "shared");
            var stated = await Remember(who.Sam, "DEMO: Sam's note: the Darnell bin rota.", "shared");
            // Imported links, as `rhythm import` writes them in Family: one marked suggested, one stated.
            var suggested = Guid.NewGuid();
            var kept = Guid.NewGuid();
            await MemoryRows.Run(s, """
                INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by,imported)
                VALUES(@l,@a,@b,'relates_to','suggested','',NULL,true),(@k,@c,@d,'relates_to','verified','',NULL,true)
                """, ("l", suggested), ("a", First(alexs, sams)), ("b", First(alexs, sams) == alexs ? sams : alexs),
                ("k", kept), ("c", First(other, stated)), ("d", First(other, stated) == other ? stated : other));
            // Sam takes his memories back to his private scope: the suggested link goes with the move, no dismissal kept;
            // the stated one now joins Sam's private memory to a Family memory of Alex's.
            Assert.Equal("private", (await Store.Share(who.Sam, sams, "private", null, Mcp, default)).Scope);
            Assert.Equal("private", (await Store.Share(who.Sam, stated, "private", null, Mcp, default)).Scope);
            Assert.Equal((0L, 0L), (await Links(alexs, sams), await Dismissals(alexs, sams)));
            Assert.Equal("private", (await Store.Share(who.Alex, alexs, "private", null, Mcp, default)).Scope);
            // While the stated link joins Alex's other memory to Sam's private one, she may not take that one back (048);
            // once Sam unlinks it, she may.
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Share(who.Alex, other, "private", null, Mcp, default)));
            var removed = await Store.Unlink(who.Sam, stated, other, null, Mcp, default);
            Assert.Equal([new MemoryLinkRef(kept, "relates_to")], removed.Removed);
            Assert.Equal("private", (await Store.Share(who.Alex, other, "private", null, Mcp, default)).Scope);
        }
        finally { await Scoped_off(); }
    }

    // A memory's connections: its made links and the map's suggestions, each neighbour through the reader's predicate, a
    // held one without its words; and whether the caller may unlink each.
    [PostgresFact] public async Task A_memorys_connections_are_its_readers_with_who_may_unlink_each()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "links-connections");
        await Scoped_on();
        try
        {
            var fair = await Remember(who.Alex, "DEMO: the Wenlow fair opens at ten.", "shared");
            var stall = await Remember(who.Sam, "DEMO: Sam runs the Wenlow fair's book stall.", "shared");
            var plan = await Remember(who.Alex, "DEMO: the Wenlow fair's parking plan, held for a person.", "shared");
            var secret = await Remember(who.Alex, "DEMO: Alex's private Wenlow note.");
            await Store.Link(who.Alex, fair, plan, "relates_to", null, Mcp, default);
            var sams = await Store.Link(who.Sam, stall, fair, "part_of_project", 0.6, Mcp, default);
            // A link from a Family memory to a private one that a share left (both ends Alex's when it was made).
            await Store.Link(who.Alex, secret, await Remember(who.Alex, "DEMO: Alex's private Wenlow list."), "relates_to", null, Mcp, default);
            var moved = await Remember(who.Alex, "DEMO: Alex's Wenlow note, later shared.");
            await Store.Link(who.Alex, moved, secret, "caused_by", null, Mcp, default);
            await Store.Share(who.Alex, moved, "shared", null, Mcp, default);
            await Store.Link(who.Alex, fair, moved, "relates_to", null, Mcp, default);
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", plan));

            var forAlex = (await Store.Connections(who.Alex, fair, null, 20, default))!;
            Assert.Equal(3, forAlex.Items.Count);
            Assert.False(forAlex.Truncated);
            Assert.Equal("on", forAlex.Scopes);
            var held = Assert.Single(forAlex.Items, i => i.Memory.Id == plan);
            Assert.Equal(("instructions", (string?)null, true, true), (held.Memory.Hold, held.Memory.Preview, held.Mine, held.MayUnlink));
            var incoming = Assert.Single(forAlex.Items, i => i.Memory.Id == stall);
            Assert.Equal((sams.Id, "part_of_project", "incoming", "inferred", false, false, 0.6, "DEMO: Sam runs the Wenlow fair's book stall."),
                ((Guid)incoming.LinkId!, incoming.Type, incoming.Direction, incoming.Verification, incoming.Mine, incoming.MayUnlink, incoming.Confidence,
                 incoming.Memory.Preview));
            Assert.Equal(("shared", "household", "draft", "current"), (incoming.Memory.Scope, incoming.Memory.Subject, incoming.Memory.Status, incoming.Memory.State));
            // Sam: his own link he may unlink; Alex's he may not.
            var forSam = (await Store.Connections(who.Sam, fair, null, 20, default))!;
            Assert.True(Assert.Single(forSam.Items, i => i.Memory.Id == stall).MayUnlink);
            Assert.False(Assert.Single(forSam.Items, i => i.Memory.Id == plan).MayUnlink);
            // The moved memory's link to Alex's private one is hers alone to see.
            Assert.Single((await Store.Connections(who.Alex, moved, null, 20, default))!.Items, i => i.Memory.Id == secret);
            Assert.DoesNotContain((await Store.Connections(who.Sam, moved, null, 20, default))!.Items, i => i.Memory.Id == secret);
            // A memory the caller does not read has no connections it is told of.
            Assert.Null(await Store.Connections(who.Sam, secret, null, 20, default));
            // Of one type, and within a limit.
            Assert.Single((await Store.Connections(who.Alex, fair, "part_of_project", 20, default))!.Items);
            var one = (await Store.Connections(who.Alex, fair, null, 1, default))!;
            Assert.Equal((1, true), (one.Items.Count, one.Truncated));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Connections(who.Alex, fair, null, 0, default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Connections(who.Alex, fair, "Not A Type", 20, default)));
        }
        finally { await Scoped_off(); }
    }
}
