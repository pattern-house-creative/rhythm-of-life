using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// Kept dismissals (SB-P4-05; migration 057, Core/MapDismissals.cs; docs/dev/memory.md sections 5 and 8; D-194):
// Discover's Dismiss saved and honoured on every read and every rebuild, between two native memories' stars and between
// any other two stars; a person's view of the dismissals, an AI client's unlink's included, and a way to take one back;
// only a person's channel (the app's login, the terminal's) dismisses or takes one back. Fictional people only: alex, sam
// and the household; every text is DEMO.
[Collection(SharedStateCollection.Name)]
public sealed class MapDismissalTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);
    static readonly MemoryCall Mcp = MemoryLogins.Mcp;
    static readonly MemoryCall App = new(AppMemories.Channel);
    static readonly MemoryCall Terminal = new("cli", "rhythm");

    async Task<Guid> Remember(Guid who, string text, string? scope = null) => (await Store.Create(who, new(text, Scope: scope), Mcp, default)).Id;

    // A record saved in Rhythm (a document star of the catalog's), of `subject`.
    async Task Document(string subject, string external, string text) => await MemoryRows.Run(fixture.Source, """
        INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,revision,source_updated_at)
        VALUES(gen_random_uuid(),@s,'documents',@e,'document',CASE WHEN @s='household' THEN 'shared' ELSE 'private' END,@t,@t,'current','1',
          now()-interval '1 hour')
        """, ("s", subject), ("e", external), ("t", text));
    static string DocumentStar(string subject, string external) =>
        new ResourceKey(subject, CatalogStars.Provider, "documents", $"{subject}:{external}").GraphId;
    static string MemoryStar(Guid memory) => MemoryStars.Key("household", memory).GraphId;

    // Stars alike in meaning by a word they share: each test's word its own direction, so the stars of one test (the class
    // shares its database) are alike only to each other; any other star points elsewhere.
    static readonly string[] Words = ["Pellingham", "Quarry", "Tamsin", "Wenlock", "Fetewick", "Brindle", "Marrowby", "Hollinby"];
    static float[]? Vector(string title, string text)
    {
        var all = title + " " + text;
        var vector = new float[Words.Length + 1];
        var at = Array.FindIndex(Words, w => all.Contains(w, StringComparison.Ordinal));
        vector[at < 0 ? Words.Length : at] = 1f;
        return vector;
    }

    sealed record Map(RelationshipHost Host, string State, RhythmConfig Config) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(State, true); } catch (IOException) { } }
        public async Task<GraphPage> Page(Guid reader) => await Host.Read(reader, null, null, default, new GraphQuery());
        public async Task<GraphEdge[]> Edges(Guid reader) => [.. (await Page(reader)).edges];
    }

    // A map over this database with memory.tools on (its made links read), built once.
    async Task<Map> Built()
    {
        TestSettings.Use();
        var config = MemoryToolSettings.On;
        var registry = new AdapterRegistry([]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { SimilarityThreshold = 0.5 },
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct), Vector, "demo-vectors"),
            registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        return new(host, state, config);
    }

    static Predicate<GraphEdge> Between(string a, string b) => e => new[] { e.source, e.target }.Order().SequenceEqual(new[] { a, b }.Order());

    async Task<long> Rows(string table) => (long)(await MemoryLogins.Scalar(fixture.Source, $"SELECT count(*) FROM {table}"))!;

    [PostgresFact] public async Task A_dismissed_suggestion_stays_hidden_after_a_reload_and_a_rebuild_and_comes_back_once_taken_back()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "dismiss-kept");
        await Scoped.Mode(s, "off");
        await Scoped.Execute(s, "DELETE FROM memory_link_dismissals; DELETE FROM map_link_dismissals");
        var heron = await Remember(who.Alex, "DEMO: the Pellingham heron nests by the weir.");
        var weir = await Remember(who.Sam, "DEMO: the Pellingham heron fishes at dawn.");
        await Document("household", "quarry-bus.txt", "DEMO: the Quarry Lane bus stop moved to the corner by the shop.");
        await Document("household", "quarry-path.txt", "DEMO: the Quarry Lane footpath is closed for repairs until spring.");
        using var map = await Built();
        var memories = Between(MemoryStar(heron), MemoryStar(weir));
        var documents = Between(DocumentStar("household", "quarry-bus.txt"), DocumentStar("household", "quarry-path.txt"));
        var pair = Assert.Single(await map.Edges(who.App), memories);
        var docs = Assert.Single(await map.Edges(who.App), documents);
        Assert.Equal((SimilaritySuggestions.Type, "suggested"), (docs.relationship, docs.verification));

        // Discover's Dismiss, as the app: one between two memories' stars is kept by the two memories (052's table), one
        // between two documents' stars by the star rows (057's).
        var first = await MapDismissals.Dismiss(map.Host, Store, who.App, pair.id, App, default);
        Assert.Equal((pair.id, false, false, "off"), (first.Link, first.Removed, first.Unchanged, first.Scopes));
        Assert.StartsWith("memories:", first.Dismissal);
        Assert.EndsWith($":{SimilaritySuggestions.Type}", first.Dismissal);
        var second = await MapDismissals.Dismiss(map.Host, Store, who.App, docs.id, App, default);
        Assert.StartsWith("stars:", second.Dismissal);
        Assert.Equal((1L, 1L), (await Rows("memory_link_dismissals"), await Rows("map_link_dismissals")));

        // Read again (a reload), by every reader: neither is served.
        foreach (var reader in new[] { who.Alex, who.Sam, who.House, who.App })
        {
            Assert.DoesNotContain(await map.Edges(reader), memories);
            Assert.DoesNotContain(await map.Edges(reader), documents);
        }
        // The rebuild finds both again and keeps them as active suggestions of its own; both stay dismissed.
        Assert.Equal("complete", await map.Host.Refresh(false, default));
        string[] ours = [MemoryStars.Key("household", heron).CatalogId, MemoryStars.Key("household", weir).CatalogId,
            new ResourceKey("household", CatalogStars.Provider, "documents", "household:quarry-bus.txt").CatalogId,
            new ResourceKey("household", CatalogStars.Provider, "documents", "household:quarry-path.txt").CatalogId];
        Assert.Equal(2L, await MemoryLogins.Scalar(s, """
            SELECT count(*) FROM relationships l JOIN resources a ON a.id=l.source_id JOIN resources b ON b.id=l.target_id
            WHERE l.lifecycle='active' AND l.type=@t AND l.engine='cross-source-v1' AND a.external_id=ANY(@ours) AND b.external_id=ANY(@ours)
            """, ("t", SimilaritySuggestions.Type), ("ours", ours)));
        Assert.DoesNotContain(await map.Edges(who.App), memories);
        Assert.DoesNotContain(await map.Edges(who.App), documents);
        // Not on the map now, so not dismissed again.
        Assert.Equal("unknown", await MemoryLogins.Refused(() => MapDismissals.Dismiss(map.Host, Store, who.App, docs.id, App, default)));

        // The person's view: both, newest first, by a person, with the stars as the map names them.
        var (listed, truncated) = await MapDismissals.List(map.Host, Store, who.App, 50, default);
        Assert.False(truncated);
        Assert.Equal([second.Dismissal, first.Dismissal], listed.Select(d => d.Id));
        var kept = listed[0];
        Assert.Equal(("stars", SimilaritySuggestions.Type, true, true, true), (kept.Kind, kept.Type, kept.ByPerson, kept.Mine, kept.MayRestore));
        Assert.Equal(new[] { DocumentStar("household", "quarry-bus.txt"), DocumentStar("household", "quarry-path.txt") }.Order(),
            new[] { kept.First.Id, kept.Second.Id }.Order());
        Assert.Contains(kept.First.Title, new[] { "DEMO: the Quarry Lane bus stop moved to the corner by the shop.",
            "DEMO: the Quarry Lane footpath is closed for repairs until spring." });
        // Alex's AI client reads the same map; the dismissals are not its (it did not make them) and it may take them back
        // only through a person's channel (below).
        Assert.DoesNotContain((await MapDismissals.List(map.Host, Store, who.Alex, 50, default)).Items, d => d.Mine);
        Assert.Single((await MapDismissals.List(map.Host, Store, who.App, 1, default)).Items);
        Assert.True((await MapDismissals.List(map.Host, Store, who.App, 1, default)).Truncated);

        // Taken back: on the map again from the next read, with no rebuild, and out of the view; twice changes nothing.
        var back = await MapDismissals.Restore(map.Host, Store, who.App, second.Dismissal!, App, default);
        Assert.Equal((second.Dismissal, false, true), (back.Dismissal, back.Unchanged, back.Back));
        Assert.Single(await map.Edges(who.App), documents);
        Assert.Equal([first.Dismissal], (await MapDismissals.List(map.Host, Store, who.App, 50, default)).Items.Select(d => d.Id));
        Assert.True((await MapDismissals.Restore(map.Host, Store, who.App, second.Dismissal!, App, default)).Unchanged);
        Assert.False((await MapDismissals.Restore(map.Host, Store, who.App, first.Dismissal!, App, default)).Unchanged);
        Assert.Single(await map.Edges(who.Sam), memories);
        Assert.Equal((0L, 0L), (await Rows("memory_link_dismissals"), await Rows("map_link_dismissals")));
        // Dismissed again, it is kept again.
        Assert.False((await MapDismissals.Dismiss(map.Host, Store, who.App, docs.id, App, default)).Unchanged);
        Assert.DoesNotContain(await map.Edges(who.App), documents);

        // Names that are not a suggestion's or a dismissal's are refused before anything is read.
        Assert.Equal("invalid", await MemoryLogins.Refused(() => MapDismissals.Dismiss(map.Host, Store, who.App, "relationship:zz", App, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => MapDismissals.Restore(map.Host, Store, who.App, "stars:x:y:similar_to", App, default)));
        var (high, low) = (Guid.Parse("ffffffff-0000-4000-8000-000000000000"), Guid.Parse("00000000-0000-4000-8000-000000000000"));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => MapDismissals.Restore(map.Host, Store, who.App,
            MapDismissals.Id("stars", high, low, "similar_to"), App, default)));
        // A confirmed link is no suggestion.
        await Scoped.Execute(s, """
            UPDATE relationships SET verification='verified',details=jsonb_set(details,'{Verification}','"verified"') WHERE type=@t
            """, ("t", SimilaritySuggestions.Type));
        var confirmed = Assert.Single(await map.Edges(who.App), memories);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => MapDismissals.Dismiss(map.Host, Store, who.App, confirmed.id, App, default)));
        // A dismissal never hides a confirmed link: the documents' one, dismissed above, is served once confirmed.
        Assert.Single(await map.Edges(who.App), documents);
    }

    [PostgresFact] public async Task Only_a_persons_channel_dismisses_or_takes_one_back_and_sees_an_AI_clients_unlink()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "dismiss-person");
        await Scoped.Mode(s, "off");
        await Scoped.Execute(s, "DELETE FROM memory_link_dismissals; DELETE FROM map_link_dismissals");
        var kite = await Remember(who.Alex, "DEMO: the Tamsin Hill kite festival is in May.");
        var string_ = await Remember(who.Sam, "DEMO: the Tamsin Hill kites need new string.");
        using var map = await Built();
        var between = Between(MemoryStar(kite), MemoryStar(string_));
        var suggestion = Assert.Single(await map.Edges(who.App), between);

        // An AI client's channel neither dismisses on the map nor takes one back; nothing is written.
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => MapDismissals.Dismiss(map.Host, Store, who.Alex, suggestion.id, Mcp, default)));
        Assert.Equal(0L, await Rows("memory_link_dismissals"));
        // Its own step is unlink, which dismisses the suggestion between the two memories (052).
        Assert.Equal([SimilaritySuggestions.Type], (await Store.Unlink(who.Alex, kite, string_, null, Mcp, default)).Dismissed);
        Assert.DoesNotContain(await map.Edges(who.App), between);
        // A person sees it, made by an AI client, and may take it back; the AI client may not.
        var listed = Assert.Single((await MapDismissals.List(map.Host, Store, who.App, 50, default)).Items);
        Assert.Equal(("memories", false, false, true), (listed.Kind, listed.ByPerson, listed.Mine, listed.MayRestore));
        Assert.True(Assert.Single((await MapDismissals.List(map.Host, Store, who.Alex, 50, default)).Items).Mine);
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => MapDismissals.Restore(map.Host, Store, who.Alex, listed.Id, Mcp, default)));
        Assert.Equal(1L, await Rows("memory_link_dismissals"));
        // The terminal is a person's channel too: Alex takes it back there, and the suggestion is on the map again.
        var terminal = await MemoryLogins.AtTerminal(s, who.Alex);
        Assert.False((await MapDismissals.Restore(map.Host, Store, terminal, listed.Id, Terminal, default)).Unchanged);
        Assert.Single(await map.Edges(who.Sam), between);

        // A link an AI client made is no suggestion the map found: Dismiss on one is a person's unlink of its one type, which
        // removes it and keeps nothing to take back, since the client stated it.
        var stated = await Store.Link(who.Alex, kite, string_, "caused_by", null, Mcp, default);
        var made = Assert.Single(await map.Edges(who.App), e => e.id == $"made-link:{stated.Id:D}");
        Assert.Equal("inferred", made.verification);
        var removed = await MapDismissals.Dismiss(map.Host, Store, who.App, made.id, App, default);
        Assert.Equal((true, (string?)null, false), (removed.Removed, removed.Dismissal, removed.Unchanged));
        Assert.DoesNotContain(await map.Edges(who.App), e => e.id == made.id);
        Assert.Equal(0L, await Rows("memory_link_dismissals"));
        Assert.Single(await map.Edges(who.App), between);

        // One marked suggested (as an import brings) is removed and kept as a dismissal of its type, by the person.
        var suggested = Guid.NewGuid();
        var (low, high) = string.CompareOrdinal(kite.ToString(), string_.ToString()) < 0 ? (kite, string_) : (string_, kite);
        await MemoryRows.Run(s, """
            INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by,imported)
            VALUES(@l,@a,@b,'relates_to','suggested','',NULL,true)
            """, ("l", suggested), ("a", low), ("b", high));
        var dismissed = await MapDismissals.Dismiss(map.Host, Store, who.App, $"made-link:{suggested:D}", App, default);
        Assert.Equal((true, MapDismissals.Id("memories", low, high, "relates_to")), (dismissed.Removed, dismissed.Dismissal));
        var person = Assert.Single((await MapDismissals.List(map.Host, Store, who.App, 50, default)).Items);
        Assert.Equal(("relates_to", true, true), (person.Type, person.ByPerson, person.Mine));
        // Taking it back brings nothing back: the suggested link was removed, and nothing suggests one of its type now. The
        // answer says so (Back false); it is kept again for the steps below.
        var nothing = await MapDismissals.Restore(map.Host, Store, who.App, person.Id, App, default);
        Assert.Equal((false, false), (nothing.Unchanged, nothing.Back));
        Assert.DoesNotContain(await map.Edges(who.App), e => e.id == $"made-link:{suggested:D}");
        await MemoryRows.Run(s, "INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(@a,@b,'relates_to',@p)",
            ("a", low), ("b", high), ("p", who.App));
        // An AI client's link of its type is made beside it and takes nothing back: only a person's channel does (057).
        var again = await Store.Link(who.Sam, string_, kite, "relates_to", null, Mcp, default);
        Assert.True(again.Created);
        Assert.Equal(1L, await Rows("memory_link_dismissals"));
        Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.RestoreSuggestion(who.Sam, "memories", low, high, "relates_to", Mcp, default)));
        // A person's link of its type, made anew, takes it back, as 052 made it.
        await Store.Unlink(who.Sam, kite, string_, "relates_to", Mcp, default);
        Assert.Equal(1L, await Rows("memory_link_dismissals"));
        Assert.True((await Store.Link(terminal, kite, string_, "relates_to", null, Terminal, default)).Created);
        Assert.Equal(0L, await Rows("memory_link_dismissals"));
    }

    [PostgresFact] public async Task With_scopes_on_a_suggestion_is_dismissed_by_whoever_may_change_either_end()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "dismiss-scoped");
        var alexApp = await Scoped.Principal(s, "dismiss-scoped-app-alex", "app", "alex");
        var samApp = await Scoped.Principal(s, "dismiss-scoped-app-sam", "app", "sam");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            await Scoped.Execute(s, "DELETE FROM memory_link_dismissals; DELETE FROM map_link_dismissals");
            var walk = await Remember(who.Alex, "DEMO: the Wenlock walk starts at the church.", "shared");
            var map_ = await Remember(who.Sam, "DEMO: the Wenlock walk map is in the porch.", "shared");
            await Document("household", "fetewick-notice.txt", "DEMO: the Fetewick notice board has the fete poster.");
            await Document("household", "fetewick-fete.txt", "DEMO: the Fetewick fete raises money for the hall.");
            var own = await Remember(who.Alex, "DEMO: Alex's private Brindle note about the garden.");
            var other = await Remember(who.Alex, "DEMO: Alex's private Brindle list for the garden.");
            using var map = await Built();
            var memories = Between(MemoryStar(walk), MemoryStar(map_));
            var documents = Between(DocumentStar("household", "fetewick-notice.txt"), DocumentStar("household", "fetewick-fete.txt"));
            var privates = Between(MemoryStar(own), MemoryStar(other));

            // The household view (the app's own login, no person) reads Family: two documents no person wrote are any Family
            // reader's to judge; two memories people wrote are their authors' or an admin's, never its.
            var docs = Assert.Single(await map.Edges(who.App), documents);
            Assert.StartsWith("stars:", (await MapDismissals.Dismiss(map.Host, Store, who.App, docs.id, App, default)).Dismissal);
            var pair = Assert.Single(await map.Edges(who.App), memories);
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => MapDismissals.Dismiss(map.Host, Store, who.App, pair.id, App, default)));
            // A person's app login (a viewer session's): either end's author may.
            Assert.StartsWith("memories:", (await MapDismissals.Dismiss(map.Host, Store, alexApp, pair.id, App, default)).Dismissal);
            Assert.DoesNotContain(await map.Edges(who.Sam), memories);
            // Alex's private memories' suggestion is not on the household view's map, nor Sam's.
            Assert.DoesNotContain(await map.Edges(who.App), privates);
            var mine = Assert.Single(await map.Edges(alexApp), privates);
            Assert.Equal("unknown", await MemoryLogins.Refused(() => MapDismissals.Dismiss(map.Host, Store, samApp, mine.id, App, default)));
            var secret = await MapDismissals.Dismiss(map.Host, Store, alexApp, mine.id, App, default);

            // Each reader is shown the dismissals between stars on its own map, each with whether it may take one back.
            var house = (await MapDismissals.List(map.Host, Store, who.App, 50, default)).Items;
            Assert.Equal(2, house.Count);
            Assert.False(Assert.Single(house, d => d.Kind == "memories").MayRestore);
            Assert.True(Assert.Single(house, d => d.Kind == "stars").MayRestore);
            Assert.DoesNotContain(house, d => d.Id == secret.Dismissal);
            var sams = (await MapDismissals.List(map.Host, Store, samApp, 50, default)).Items;
            Assert.True(Assert.Single(sams, d => d.Kind == "memories").MayRestore);
            Assert.DoesNotContain(sams, d => d.Id == secret.Dismissal);
            Assert.Contains((await MapDismissals.List(map.Host, Store, alexApp, 50, default)).Items, d => d.Id == secret.Dismissal);
            // Taking one back is the same rule: not the household view's for two people's memories; Sam's, the other author.
            var memoryDismissal = Assert.Single(house, d => d.Kind == "memories").Id;
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => MapDismissals.Restore(map.Host, Store, who.App, memoryDismissal, App, default)));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => MapDismissals.Restore(map.Host, Store, samApp, secret.Dismissal!, App, default)));
            Assert.False((await MapDismissals.Restore(map.Host, Store, samApp, memoryDismissal, App, default)).Unchanged);
            Assert.Single(await map.Edges(who.App), memories);
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // The database's own guard (057's map_dismissal_lock), beside the engine's view check: a star a reader reads through a
    // source share counts as read (as the map serves it), a save_document copy's star that names a narrower reader is that
    // reader's alone, and a native memory's star only while its memory is current, out of the trash and not held.
    [PostgresFact] public async Task The_database_judges_a_star_as_the_map_serves_it()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "dismiss-guard");
        var samApp = await Scoped.Principal(s, "dismiss-guard-app-sam", "app", "sam");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            await Scoped.Execute(s, "DELETE FROM memory_link_dismissals; DELETE FROM map_link_dismissals; DELETE FROM source_shares");
            async Task<Guid> Star(string subject, string provider, string account, string external, string? reader = null)
            {
                var id = Guid.NewGuid();
                await Scoped.Execute(s, """
                    INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,relationship_snapshot)
                    VALUES(@id,@s,@p,@x,'document',CASE WHEN @s='household' THEN 'shared' ELSE 'private' END,'DEMO star','','current',
                      jsonb_build_object('Key',jsonb_build_object('Subject',@s,'Provider',@p,'Account',@a,'ExternalId',@e),'Reader',@r::text))
                    """, ("id", id), ("s", subject), ("p", provider), ("x", "dismiss-guard:" + external), ("a", account), ("e", external), ("r", reader));
                return id;
            }
            async Task Suggest(Guid a, Guid b) => await Scoped.Execute(s, """
                INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine,lifecycle) VALUES(@a,@b,'mentions','suggested','',@e,'active')
                """, ("a", a), ("b", b), ("e", RelationshipEngine.Owner));
            var house = await Star("household", CatalogStars.Provider, "documents", "household:guard-notice.txt");
            var glucose = await Star("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:glucose");
            var copy = await Star("household", CatalogStars.Provider, "documents", "household:guard-copy.txt", reader: "alex");
            await Suggest(glucose, house);
            await Suggest(copy, house);

            // Alex's data star is Sam's only once Alex shares the source (048's source shares, 023's predicate): then Sam may
            // judge the pair through the household end, as the map serves it to him.
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DismissSuggestion(samApp, glucose, house, "mentions", App, default)));
            await Scoped.Execute(s, "INSERT INTO source_shares(person_id,provider) VALUES('alex','cgm')");
            Assert.False((await Store.DismissSuggestion(samApp, glucose, house, "mentions", App, default)).Unchanged);
            Assert.Contains(await Store.DismissalRows(samApp, 50, default), r => r.Kind == "stars" && new[] { r.Low, r.High }.Order().SequenceEqual(new[] { glucose, house }.Order()));
            // A copy's star that names Alex as its reader is not Sam's, though its subject is Family's.
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DismissSuggestion(samApp, copy, house, "mentions", App, default)));

            // A memory's star while the memory is held, then in the trash: no suggestion of it to judge.
            var note = await Remember(who.Sam, "DEMO: the Hollinby notice board needs pins.", "shared");
            var star = await Star("household", MemoryStars.Provider, MemoryStars.Account, note.ToString());
            await Scoped.Execute(s, "UPDATE resources SET external_id=@x WHERE id=@id", ("id", star), ("x", MemoryStars.Key("household", note).CatalogId));
            await Suggest(star, house);
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", note));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DismissSuggestion(samApp, star, house, "mentions", App, default)));
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", note));
            await Store.Forget(who.Sam, note, null, Mcp, default);
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.DismissSuggestion(samApp, star, house, "mentions", App, default)));
            Assert.Equal(1L, await Rows("map_link_dismissals"));

            // Taking back a dismissal between two memories: 057 itself refuses it while either memory is held (D-160), as
            // it refuses a held memory's star, so its guard never rests on the engine's view check alone.
            var pins = await Remember(who.Sam, "DEMO: the Quenby kettle descaler is under the sink.", "shared");
            var frame = await Remember(who.Sam, "DEMO: the Quenby bicycle pump hangs in the shed.", "shared");
            var (low, high) = string.CompareOrdinal(pins.ToString(), frame.ToString()) < 0 ? (pins, frame) : (frame, pins);
            await Scoped.Execute(s, "INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(@l,@h,'similar_to',@p)",
                ("l", low), ("h", high), ("p", samApp));
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", pins));
            Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.RestoreSuggestion(samApp, "memories", low, high, "similar_to", App, default)));
            Assert.Equal(1L, await Rows("memory_link_dismissals"));
            await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", pins));
            Assert.False((await Store.RestoreSuggestion(samApp, "memories", low, high, "similar_to", App, default)).Unchanged);
            Assert.Equal(0L, await Rows("memory_link_dismissals"));
            // Into the trash, so no later map or recall of this class finds them.
            await Store.Forget(who.Sam, pins, null, Mcp, default);
            await Store.Forget(who.Sam, frame, null, Mcp, default);
        }
        finally
        {
            await Scoped.Execute(s, "DELETE FROM source_shares; DELETE FROM map_link_dismissals");
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // Recall's hops honour a dismissal kept between two star rows (map_link_dismissals) as the map does: no hop across
    // it, a hop again once it is taken back, and a hop across it once the link is confirmed, since a dismissal never hides
    // a confirmed link. Recall serves no document's star, so the hop is shown on two memories' stars, with the row kept as
    // 057's table keeps one (two star rows and the type), which the hop's filter reads by the link's own rows.
    [PostgresFact] public async Task Recalls_hops_leave_out_a_suggestion_dismissed_between_two_stars_but_never_a_confirmed_link()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "dismiss-recall");
        await Scoped.Mode(s, "off");
        await Scoped.Execute(s, "DELETE FROM memory_link_dismissals; DELETE FROM map_link_dismissals");
        var note = await Remember(who.Alex, "DEMO: the Hollinby orchard ladder is in the barn.");
        var rota = await Remember(who.Sam, "DEMO: the Hollinby orchard rota for the picking season.");
        using var map = await Built();
        Assert.Single(await map.Edges(who.App), Between(MemoryStar(note), MemoryStar(rota)));
        var (a, b) = ((await Store.StarRow(MemoryStars.Key("household", note), default))!.Value, (await Store.StarRow(MemoryStars.Key("household", rota), default))!.Value);
        var (low, high) = string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);
        var recall = new Recall(RecallRows.Db(s), null, PassageSources.Of(false, false, true, []), options: RecallOptions.Default with { MadeLinks = true });
        async Task<Guid[]> Hops() => [.. (await recall.Search(who.Alex, new("ladder barn", 20, 1), default)).Hits
            .Where(h => h.Why.Hop == 1 && h.Why.Link == SimilaritySuggestions.Type).Select(h => h.Id)];
        Assert.Equal([rota], await Hops());

        async Task Keep() => await Scoped.Execute(s, "INSERT INTO map_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(@l,@h,@t,@p)",
            ("l", low), ("h", high), ("t", SimilaritySuggestions.Type), ("p", who.App));
        await Keep();
        Assert.Empty(await Hops());
        await Scoped.Execute(s, "DELETE FROM map_link_dismissals");
        Assert.Equal([rota], await Hops());

        // Kept again, then confirmed: the hop follows it, as the map serves a confirmed link whatever was dismissed.
        await Keep();
        Assert.Empty(await Hops());
        await Scoped.Execute(s, """
            UPDATE relationships SET verification='verified',details=jsonb_set(details,'{Verification}','"verified"')
            WHERE type=@t AND (source_id,target_id) IN ((@l,@h),(@h,@l))
            """, ("t", SimilaritySuggestions.Type), ("l", low), ("h", high));
        Assert.Equal([rota], await Hops());
        await Scoped.Execute(s, "DELETE FROM map_link_dismissals");
    }

    // Through the app's endpoint as the app's server calls it: the three ops behind memory.tools, each answer the contract's.
    [PostgresFact] public async Task The_app_dismisses_lists_and_takes_back_through_its_memories_tool()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "dismiss-app");
        await Scoped.Mode(s, "off");
        await Scoped.Execute(s, "DELETE FROM memory_link_dismissals; DELETE FROM map_link_dismissals");
        await Document("household", "marrowby-gate.txt", "DEMO: the Marrowby gate sticks after the rain.");
        await Document("household", "marrowby-hinge.txt", "DEMO: the Marrowby gate needs a new hinge soon.");
        using var map = await Built();
        var services = new ServiceCollection().AddSingleton(map.Config).AddSingleton(new Database(s, new AdapterRegistry([]).Policies(map.Config)))
            .AddSingleton(map.Host).BuildServiceProvider();
        async Task<JsonNode> Call(object arguments)
        {
            var (status, content) = await AppMemoryCalls.Call(services, who.App, AppMemoryCalls.Body(AppEndpoint.Memories, arguments),
                new AppHealth(fixture.Db, map.Config));
            Assert.Equal(200, status);
            return content!;
        }
        var edge = Assert.Single(await map.Edges(who.App), Between(DocumentStar("household", "marrowby-gate.txt"), DocumentStar("household", "marrowby-hinge.txt")));
        Assert.Empty((await Call(new { op = "dismissals" }))["dismissals"]!.AsArray());
        var dismissed = await Call(new { op = "dismiss", link = edge.id });
        Assert.Equal((true, "dismiss", edge.id, false, false), ((bool)dismissed["ok"]!, (string?)dismissed["op"], (string?)dismissed["link"],
            (bool)dismissed["removed"]!, (bool)dismissed["unchanged"]!));
        var id = (string)dismissed["dismissal"]!;
        var listed = (await Call(new { op = "dismissals", limit = 10 }))["dismissals"]!.AsArray().Single()!;
        Assert.Equal((id, "stars", "person", true), ((string?)listed["id"], (string?)listed["kind"], (string?)listed["by"], (bool)listed["mayRestore"]!));
        Assert.Equal(2, listed["stars"]!.AsArray().Count);
        // A refusal in the store's words: no such suggestion now.
        var refused = await Call(new { op = "dismiss", link = edge.id });
        Assert.Equal((false, "unknown"), ((bool)refused["ok"]!, (string?)refused["code"]));
        var back = await Call(new { op = "undismiss", dismissal = id });
        Assert.Equal((true, "undismiss", id, false), ((bool)back["ok"]!, (string?)back["op"], (string?)back["dismissal"], (bool)back["unchanged"]!));
        Assert.Single(await map.Edges(who.App), e => e.id == edge.id);
        // Arguments outside an op's shape are refused before anything is read.
        bool Takes(object arguments) => AppEndpoint.Allowed(AppMemoryCalls.Request(arguments), memories: true);
        Assert.True(Takes(new { op = "dismiss", link = edge.id }));
        Assert.False(Takes(new { op = "dismiss", link = "not a link" }));
        Assert.False(Takes(new { op = "dismiss" }));
        Assert.False(Takes(new { op = "undismiss", dismissal = "stars:nope" }));
        Assert.False(Takes(new { op = "dismissals", limit = 51 }));
        Assert.False(Takes(new { op = "dismissals", link = edge.id }));

        // While the map is not ready yet (an engine just started), each op answers unavailable, never forbidden, which the
        // app's server would take to mean the memory tools went off.
        // (The host has no configuration until the engine is ready: Program.cs gives it none before then.)
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(map.Config));
        var cold = new RelationshipHost(db, () => null,
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct), Vector, "demo-vectors"), registry);
        var starting = new ServiceCollection().AddSingleton(map.Config).AddSingleton(db).AddSingleton(cold).BuildServiceProvider();
        foreach (var arguments in new object[] { new { op = "dismissals" }, new { op = "dismiss", link = edge.id }, new { op = "undismiss", dismissal = id } })
        {
            var (status, content) = await AppMemoryCalls.Call(starting, who.App, AppMemoryCalls.Body(AppEndpoint.Memories, arguments), new AppHealth(fixture.Db, map.Config));
            Assert.Equal(200, status);
            Assert.Equal((false, "unavailable"), ((bool)content!["ok"]!, (string?)content["code"]));
        }
        TestSettings.Use();
    }
}
