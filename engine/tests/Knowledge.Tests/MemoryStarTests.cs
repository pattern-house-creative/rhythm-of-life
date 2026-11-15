using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// Native memory stars on the map (SB-P3-11, docs/dev/memory.md sections 8 and 10): re-emitted at every pass from the
// memory tables, first in graph.maxMemories (Q-141), served per reader by the memory's own row at each read (a held or
// trashed one at once to no one), their text read from the memory tables when opened, a failed rebuild hiding none, one
// star per memory (never a save_document copy's beside it); the map's links reaching a memory through combined
// retrieval's hops (D-152) are MemoryStarRecallTests'. Fictional people only: alex, sam and the household; every text
// is DEMO.

public sealed class MemoryStarPassTests
{
    static readonly Guid Principal = Guid.NewGuid();
    static readonly DateTimeOffset Then = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    static CatalogMemory Memory(int i, string subject = "household", Guid? project = null, string[]? tags = null, string? text = null) =>
        new(new Guid($"00000000-0000-4000-8000-{i:D12}"), subject, subject == "household" ? "shared" : "private",
            text ?? $"DEMO: fictional memory {i}.\nA second line.", tags ?? [], "draft", project, 1, 1, Then.AddMinutes(-i), Then.AddMinutes(-i));
    static readonly Guid Garden = new("00000000-0000-4000-8000-0000000000aa");
    static CatalogProject GardenProject(string subject = "household") => new(Garden, subject, "demo-garden", "DEMO Garden", "", "active", ["demo-beans"], 1);

    static CatalogRelationshipSource Source(Func<RelationshipCatalog, CatalogInventory> inventory, bool scoped = false, int? maxMemories = null,
        params IAdapter[] adapters)
    {
        var settings = RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath());
        if (scoped) settings = settings with { Catalog = settings.Catalog with { Scoped = true } };
        if (maxMemories is { } max) settings = settings with { MaxMemories = max };
        return new(settings, new AdapterRegistry(adapters), (catalog, _) => Task.FromResult(inventory(catalog)));
    }
    static CatalogInventory With(IReadOnlyList<CatalogMemory> memories, IReadOnlyList<CatalogProject>? projects = null, bool capped = false) =>
        new([], [], Memories: memories, Projects: projects ?? [], MemoriesCapped: capped);

    [Fact] public async Task A_memory_is_one_star_with_its_projects_and_the_pass_reads_it_with_the_memory_budget()
    {
        var asked = 0;
        var source = Source(catalog => { asked = catalog.MemoryBudget; return With([
            Memory(1, project: Garden, text: "DEMO: beans along the fence.\nSee https://docs.google.com/document/d/plan123/edit for the plan."),
            Memory(2, tags: ["demo-beans"]), Memory(3)], [GardenProject(), new(Guid.NewGuid(), "household", "demo-empty", "DEMO Empty", "", "active", [], 1)]); });
        var batch = await source.Fetch(Principal, default);
        RelationshipDiscovery.Validate(batch);
        Assert.Equal(3000, asked);
        var stars = batch.Records.Where(r => r.Key.Provider == MemoryStars.Provider).ToDictionary(r => r.Key.ExternalId);
        // Three memory stars and the one project with members; a project with none has no star.
        Assert.Equal(4, stars.Count);
        var one = stars[Memory(1).Id.ToString()];
        Assert.Equal((new ResourceKey("household", "memory", "memory", Memory(1).Id.ToString()), "shared", "memory", "DEMO: beans along the fence."),
            (one.Key, one.Scope, one.Kind, one.Title));
        Assert.Equal(one.Title, one.Overview);
        Assert.Equal(["memory:family", $"memory:project:{Garden}"], one.Groups!.Select(g => g.Id));
        var project = stars[Garden.ToString()];
        Assert.Equal((MemoryStars.ProjectKey("household", Garden), "project", "DEMO Garden"), (project.Key, project.Kind, project.Title));
        // Membership: in the project, or tagged with one of its aliases; and a Drive address in the text, exactly.
        Assert.Contains(one.References, r => r.Target == project.Key && r.NativeType == "in_project" && r.Verification == "verified");
        Assert.Contains(one.References, r => r.Url == "https://docs.google.com/document/d/plan123/edit");
        Assert.Contains(stars[Memory(2).Id.ToString()].References, r => r.Target == project.Key);
        Assert.DoesNotContain(stars[Memory(3).Id.ToString()].References, r => r.Target == project.Key);
        Assert.Contains(RelationshipDiscovery.Discover(batch.Records), l => l.Source == one.Key && l.Target == project.Key && l.Type == "in_project");
    }

    // Q-141, amended: native memories are Rhythm's own records, so they take graph.maxMemories first, their projects
    // counted, and the adapters share what is left in their order; past the pass's 5000 records they are kept with the
    // catalog's stars, before any adapter's.
    [Fact] public async Task Native_memories_take_the_memory_budget_first_and_the_pass_keeps_them()
    {
        var notes = new ManyNotes("notes-a", 3000);
        var batch = await Source(_ => With([Memory(1, project: Garden), Memory(2), Memory(3)], [GardenProject()]), adapters: [notes]).Fetch(Principal, default);
        Assert.Equal(3000 - 4, notes.Offered);
        Assert.Equal(4, batch.Records.Count(r => r.Key.Provider == MemoryStars.Provider));
        // A budget smaller than the memories: the newest first, each with its project, and the listing incomplete, so no
        // star of an earlier pass is reported missing for it.
        var capped = await Source(_ => With([Memory(1, project: Garden), Memory(2), Memory(3)], [GardenProject()]), maxMemories: 3).Fetch(Principal, default);
        Assert.Equal([Memory(1).Id.ToString(), Memory(2).Id.ToString(), Garden.ToString()],
            capped.Records.Where(r => r.Key.Provider == MemoryStars.Provider).Select(r => r.Key.ExternalId).Order(StringComparer.Ordinal));
        // Past the engine's 5000 a pass keeps the memories with the catalog's stars, whatever an adapter lists.
        var flood = await Source(_ => With([.. Enumerable.Range(1, 40).Select(i => Memory(i))]),
            adapters: new ManyNotes("notes-a", RelationshipDiscovery.MaximumRecords + 100, ignoresBudget: true)).Fetch(Principal, default);
        Assert.Equal(RelationshipDiscovery.MaximumRecords, flood.Records.Count);
        Assert.Equal(40, flood.Records.Count(r => r.Key.Provider == MemoryStars.Provider));
    }

    // A replaced memory (SB-P4-11, read only with memory.tools on) is listed after every current one, so a budget too
    // small for both keeps the current facts; with room it is a star like any other.
    [Fact] public async Task A_replaced_memory_comes_after_every_current_one_in_the_budget()
    {
        var replaced = Memory(1) with { Replaced = true };
        var capped = await Source(_ => With([replaced, Memory(2), Memory(3)]), maxMemories: 2).Fetch(Principal, default);
        Assert.Equal([Memory(2).Id.ToString(), Memory(3).Id.ToString()],
            capped.Records.Where(r => r.Key.Provider == MemoryStars.Provider).Select(r => r.Key.ExternalId).Order(StringComparer.Ordinal));
        var all = await Source(_ => With([replaced, Memory(2), Memory(3)])).Fetch(Principal, default);
        Assert.Equal([Memory(2).Id.ToString(), Memory(3).Id.ToString(), Memory(1).Id.ToString()],
            all.Records.Where(r => r.Key.Provider == MemoryStars.Provider).Select(r => r.Key.ExternalId));
        // The pass reads replaced memories only with memory.tools on (RelationshipCatalog.Replaced); off by default.
        Assert.False(RelationshipCatalog.For(TestSettings.Use()).Replaced);
        Assert.True(RelationshipCatalog.For(MemoryToolSettings.On).Replaced);
        TestSettings.Use();
    }

    // A replaced memory takes only the room the adapters leave: a budget that fits the current native memories and an
    // adapter's keeps them all, and a replaced fact that does not fit never makes the native listing incomplete, so a star
    // of an earlier pass that is gone now is still reported missing.
    [Fact] public async Task A_replaced_memory_takes_only_the_room_the_adapters_leave()
    {
        var replaced = Memory(1) with { Replaced = true };
        var notes = new ManyNotes("notes-a", 3);
        var tight = await Source(_ => With([replaced, Memory(2), Memory(3)]), maxMemories: 5, adapters: [notes]).Fetch(Principal, default);
        Assert.Equal(3, notes.Offered);
        Assert.Equal(3, tight.Records.Count(r => r.Key.Provider == "notes-a"));
        Assert.Equal([Memory(2).Id.ToString(), Memory(3).Id.ToString()],
            tight.Records.Where(r => r.Key.Provider == MemoryStars.Provider).Select(r => r.Key.ExternalId).Order(StringComparer.Ordinal));
        var roomy = new ManyNotes("notes-a", 3);
        var room = await Source(_ => With([replaced, Memory(2), Memory(3)]), maxMemories: 6, adapters: [roomy]).Fetch(Principal, default);
        Assert.Equal((4, 3), (roomy.Offered, room.Records.Count(r => r.Key.Provider == "notes-a")));
        Assert.Contains(room.Records, r => r.Key.ExternalId == Memory(1).Id.ToString() && r.Status == "current");

        var first = (await Source(_ => With([Memory(2), Memory(9)])).Fetch(Principal, default)).Records.Where(r => r.Key.Provider == MemoryStars.Provider).ToArray();
        var later = Source(_ => With([replaced, Memory(2)]), maxMemories: 1);
        later.Prime(first);
        var moved = await later.Fetch(Principal, default);
        Assert.Equal("missing", moved.Records.Single(r => r.Key.ExternalId == Memory(9).Id.ToString()).Status);
        Assert.DoesNotContain(moved.Records, r => r.Key.ExternalId == Memory(1).Id.ToString());
    }

    // With scopes on each subject's memories use its own share of graph.maxMemories, as an adapter's do.
    [Fact] public async Task With_scopes_on_each_scopes_memories_use_its_own_share()
    {
        var family = new ManyNotes("notes-b", 2000);
        var alex = new ManyNotes("notes-a", 2000, subject: "alex");
        var batch = await Source(_ => With([Memory(1, "alex"), Memory(2, "alex"), Memory(3)]), scoped: true, adapters: [family, alex]).Fetch(Principal, default);
        Assert.Equal((750 - 1, 750 - 2), (family.Offered, alex.Offered));
        Assert.Equal(["alex", "alex", "household"], batch.Records.Where(r => r.Key.Provider == MemoryStars.Provider).Select(r => r.Key.Subject).Order());
        // A former person, outside the catalog's subjects, has no share: none of theirs is a star.
        var former = await Source(_ => With([Memory(4, "jo")]), scoped: true).Fetch(Principal, default);
        Assert.DoesNotContain(former.Records, r => r.Key.Provider == MemoryStars.Provider && r.Status == "current");
    }

    // A pass whose read of the memory tables fails lists none and reports none missing: the stars of the last pass stay
    // (a failed rebuild hides no memory). A complete read that no longer holds one reports it missing, as a source does.
    [Fact] public async Task A_failed_read_of_the_memory_tables_reports_no_memory_missing()
    {
        var previous = Source(_ => With([Memory(1), Memory(2)]));
        var first = (await previous.Fetch(Principal, default)).Records.Where(r => r.Key.Provider == MemoryStars.Provider).ToArray();
        var failing = new CatalogRelationshipSource(RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath()),
            AdapterRegistry.Empty, (_, _) => throw new TimeoutException("DEMO: the database is slow"));
        failing.Prime(first);
        var failed = await failing.Fetch(Principal, default);
        Assert.DoesNotContain(failed.Records, r => r.Key.Provider == MemoryStars.Provider);
        Assert.Equal(["catalog unavailable (TimeoutException)"], failing.Notes);
        var later = Source(_ => With([Memory(2)]));
        later.Prime(first);
        var moved = await later.Fetch(Principal, default);
        Assert.Equal("missing", moved.Records.Single(r => r.Key.ExternalId == Memory(1).Id.ToString()).Status);
        Assert.Equal("current", moved.Records.Single(r => r.Key.ExternalId == Memory(2).Id.ToString()).Status);
        // A capped read is incomplete: nothing it left out is reported missing.
        var capped = Source(_ => With([Memory(2)], capped: true));
        capped.Prime(first);
        Assert.DoesNotContain((await capped.Fetch(Principal, default)).Records, r => r.Status == "missing");
    }

    // A catalog past its star budget makes no stars of its own for the pass, as before, but its native memories, read
    // from their own tables, are still listed: a catalog that outgrows its budget hides no memory.
    [Fact] public async Task A_catalog_past_its_budget_still_lists_its_native_memories()
    {
        var source = Source(_ => With([Memory(1), Memory(2)]) with { Failure = nameof(InvalidOperationException) });
        var batch = await source.Fetch(Principal, default);
        Assert.Equal(2, batch.Records.Count(r => r.Key.Provider == MemoryStars.Provider && r.Status == "current"));
        Assert.Equal(["catalog unavailable (InvalidOperationException)"], source.Notes);
    }

    // With scopes on, a subject past its own share stops only its own stars being reported missing (budgets are per
    // scope, memory.md 6.2): another subject's complete listing still reports what it no longer holds.
    [Fact] public async Task With_scopes_on_one_scope_past_its_share_never_stops_anothers_missing_stars()
    {
        var first = (await Source(_ => With([Memory(1, "alex"), Memory(2), Memory(3)]), scoped: true).Fetch(Principal, default)).Records
            .Where(r => r.Key.Provider == MemoryStars.Provider).ToArray();
        var later = Source(_ => With([Memory(2)], capped: true) with { CappedSubjects = ["alex"] }, scoped: true);
        later.Prime(first);
        var batch = await later.Fetch(Principal, default);
        Assert.Equal("missing", batch.Records.Single(r => r.Key.ExternalId == Memory(3).Id.ToString()).Status);
        Assert.DoesNotContain(batch.Records, r => r.Key.ExternalId == Memory(1).Id.ToString());
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class MemoryStarTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    sealed record Map(RelationshipHost Host, string State, Guid App) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(State, true); } catch (IOException) { } }
        public async Task<GraphNode[]> Nodes(Guid reader)
        {
            var nodes = new List<GraphNode>();
            for (string? cursor = null; ;)
            {
                var page = await Host.Read(reader, null, null, default, new GraphQuery(Cursor: cursor));
                nodes.AddRange(page.nodes);
                if (page.nextCursor is not { } next) return [.. nodes];
                cursor = next;
            }
        }
        public async Task<bool> Sees(Guid reader, string star) => (await Host.Read(reader, null, null, default, new GraphQuery("get", star))).nodes.Count == 1;
        public async Task<bool[]> Seen(string star, params Guid[] readers)
        {
            var seen = new bool[readers.Length];
            for (var i = 0; i < readers.Length; i++) seen[i] = await Sees(readers[i], star);
            return seen;
        }
        public async Task<string?> Text(Guid reader, string star) =>
            (await Host.Read(reader, null, null, default, new GraphQuery("content", star))).nodes.SingleOrDefault()?.resource.content;
    }

    // A map rebuilt once over the database; `failing`, when given and true at a rebuild, fails its read of the catalog and
    // the memory tables.
    Task<Map> Rebuilt(Func<bool>? failing = null, params IAdapter[] adapters) => Rebuilt(TestSettings.Use(), failing, adapters);
    async Task<Map> Rebuilt(RhythmConfig config, Func<bool>? failing = null, params IAdapter[] adapters)
    {
        var registry = new AdapterRegistry(adapters);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => failing?.Invoke() == true ? throw new TimeoutException("DEMO: the database is slow")
                : CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        return new(host, state, app);
    }
    static string Star(Guid memory, string subject = "household") => MemoryStars.Key(subject, memory).GraphId;

    [PostgresFact] public async Task With_scopes_off_every_reader_has_every_native_memorys_star_and_its_text()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-off");
        await Scoped.Mode(s, "off");
        var walk = (await Store.Create(who.Alex, new("DEMO: the canal walk at dawn.\nThe heron was there.", Project: "demo-canal"), MemoryLogins.Mcp, default));
        var deprecated = (await Store.Create(who.Alex, new("DEMO: an old note."), MemoryLogins.Mcp, default)).Id;
        await Store.SetStatus(who.Alex, deprecated, "deprecated", null, MemoryLogins.Mcp, default);
        using var map = await Rebuilt();
        var node = (await map.Nodes(who.Sam)).Single(n => n.id == Star(walk.Id));
        Assert.Equal(("memory", "DEMO: the canal walk at dawn.", "household", "shared", "memory"),
            (node.type, node.title, node.resource.owner, node.resource.access.scope, node.provenance.sourceId));
        Assert.Null(node.resource.content);
        var project = MemoryStars.ProjectKey("household", walk.Project!.Id).GraphId;
        Assert.Equal("project", (await map.Nodes(map.App)).Single(n => n.id == project).type);
        // Its text from the memory tables, for this answer only; nobody's map holds a deprecated memory.
        Assert.Equal("DEMO: the canal walk at dawn.\nThe heron was there.", await map.Text(who.House, Star(walk.Id)));
        Assert.Equal("DEMO: the canal walk at dawn.\nThe heron was there.", await map.Text(map.App, Star(walk.Id)));
        Assert.False(await map.Sees(who.Sam, Star(deprecated)));
        // The project's view holds the memory.
        Assert.Contains(Star(walk.Id), (await map.Host.Read(who.Sam, null, null, default, new GraphQuery("project", project))).nodes.Select(n => n.id));
        // Held after the pass: its star is no one's at once, and its text unavailable, with no pass between.
        await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", walk.Id));
        Assert.False(await map.Sees(who.Sam, Star(walk.Id)));
        Assert.Null(await map.Text(who.Alex, Star(walk.Id)));
        await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", walk.Id));
        Assert.True(await map.Sees(who.Sam, Star(walk.Id)));
        // In the trash (its resources row marked deleted): no one's either.
        await Scoped.Execute(s, "UPDATE resources SET status='deleted' WHERE id=@id", ("id", walk.Id));
        Assert.False(await map.Sees(who.Sam, Star(walk.Id)));
        await Scoped.Execute(s, "UPDATE resources SET status='current' WHERE id=@id", ("id", walk.Id));
        // Deprecated, or no longer true, after the pass: no one's at once too, and its text unavailable.
        await Scoped.Execute(s, "UPDATE memories SET status='deprecated' WHERE id=@id", ("id", walk.Id));
        Assert.False(await map.Sees(who.Sam, Star(walk.Id)));
        Assert.Null(await map.Text(who.Alex, Star(walk.Id)));
        await Scoped.Execute(s, "UPDATE memories SET status='draft',valid_from=now()-interval '1 hour',valid_until=now()-interval '1 minute' WHERE id=@id", ("id", walk.Id));
        Assert.False(await map.Sees(who.Sam, Star(walk.Id)));
        Assert.Null(await map.Text(who.Alex, Star(walk.Id)));
        await Scoped.Execute(s, "UPDATE memories SET valid_from=NULL,valid_until=NULL WHERE id=@id", ("id", walk.Id));
        Assert.True(await map.Sees(who.Sam, Star(walk.Id)));
    }

    // SB-P4-11: with memory.tools on, a fact a newer memory of its scope replaced stays a star, faded rather than hidden:
    // served with what replaced it (truth), judged at each read, its text opening, and named so in the harness's bounded
    // views; a retraction of its replacement makes it current again at once, and bringing that back replaces it again.
    // An ended memory (replaced by nothing live) is still no one's star. With memory.tools off the map is as before.
    [PostgresFact] public async Task With_memory_tools_on_a_replaced_fact_stays_a_star_naming_what_replaced_it()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-replaced");
        await Scoped.Mode(s, "off");
        var old = (await Store.Create(who.Alex, new("DEMO: the Pellow bus leaves at eight."), MemoryLogins.Mcp, default)).Id;
        var fresh = (await Store.Create(who.Alex, new("DEMO: the Pellow bus leaves at half past eight now."), MemoryLogins.Mcp, default)).Id;
        var ended = (await Store.Create(who.Alex, new("DEMO: the Pellow ferry ran until October."), MemoryLogins.Mcp, default)).Id;
        var plan = await Store.Supersede(who.Alex, fresh, old, null, MemoryLogins.Mcp, default);
        Assert.Equal("superseded", (await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, MemoryLogins.Mcp, default)).Action);
        await Scoped.Execute(s, "UPDATE memories SET valid_from=now()-interval '1 day',valid_until=now()-interval '1 minute' WHERE id=@id", ("id", ended));

        // Off, as every install is by default: the replaced fact is no one's star, as any memory past its end, and no star
        // carries truth.
        using (var off = await Rebuilt())
        {
            Assert.Equal(new[] { false, false, false }, await off.Seen(Star(old), who.Sam, who.House, off.App));
            Assert.Null(await off.Text(who.Alex, Star(old)));
            Assert.All(await off.Nodes(who.Sam), n => Assert.Null(n.truth));
        }

        using var map = await Rebuilt(MemoryToolSettings.On);
        Assert.Equal(new[] { true, true, true }, await map.Seen(Star(old), who.Sam, who.House, map.App));
        var nodes = (await map.Nodes(who.Sam)).ToDictionary(n => n.id);
        Assert.Equal(new GraphTruth("replaced", Star(fresh)), nodes[Star(old)].truth);
        Assert.Null(nodes[Star(fresh)].truth);
        Assert.DoesNotContain(Star(ended), nodes.Keys);
        Assert.Equal("DEMO: the Pellow bus leaves at eight.", await map.Text(who.Sam, Star(old)));
        Assert.Null(await map.Text(who.Sam, Star(ended)));
        // A get and the harness's bounded view name it too.
        Assert.Equal("replaced", (await map.Host.Read(who.Sam, null, null, default, new GraphQuery("get", Star(old)))).nodes.Single().truth!.state);
        var explored = (await map.Host.Explore(who.Sam, null, Star(old), 1, GraphExplorer.MaxStars, default)).stars.ToDictionary(x => x.graphId);
        Assert.Equal(new GraphTruth("replaced", Star(fresh)), explored[Star(old)].truth);
        Assert.Null(explored.GetValueOrDefault(Star(fresh))?.truth);

        // Its replacement marked wrong: current again at once, with no pass between; back, replaced again.
        await Store.SetStatus(who.Alex, fresh, "deprecated", null, MemoryLogins.Mcp, default);
        var reopened = (await map.Nodes(who.Sam)).ToDictionary(n => n.id);
        Assert.Null(reopened[Star(old)].truth);
        Assert.DoesNotContain(Star(fresh), reopened.Keys);
        await Store.Undo(who.Alex, fresh, null, null, MemoryLogins.Mcp, default);
        Assert.Equal(new GraphTruth("replaced", Star(fresh)), (await map.Nodes(who.Sam)).Single(n => n.id == Star(old)).truth);
        // Its replacement held for review: nothing live replaces it, so it has ended, and is no one's star until released.
        await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", fresh));
        Assert.False(await map.Sees(who.Sam, Star(old)));
        await Scoped.Execute(s, "UPDATE memories SET hold=NULL WHERE id=@id", ("id", fresh));
        Assert.True(await map.Sees(who.Sam, Star(old)));
    }

    // A replaced fact never takes a current memory's place in a bounded view: on the overview's page, in a search's and in
    // explore_graph's limit it comes after every current memory, however many links it has.
    [PostgresFact] public async Task A_replaced_fact_comes_after_every_current_memory_on_a_bounded_page()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-ranked");
        await Scoped.Mode(s, "off");
        var current = new List<Guid>();
        for (var i = 1; i <= 10; i++) current.Add((await Store.Create(who.Alex, new($"DEMO: the Quarle choir note {i}."), MemoryLogins.Mcp, default)).Id);
        var old = (await Store.Create(who.Alex, new("DEMO: the Quarle choir meets at the old hall."), MemoryLogins.Mcp, default)).Id;
        var fresh = (await Store.Create(who.Alex, new("DEMO: the Quarle choir meets at the new hall now."), MemoryLogins.Mcp, default)).Id;
        current.Add(fresh);
        var plan = await Store.Supersede(who.Alex, fresh, old, null, MemoryLogins.Mcp, default);
        await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, MemoryLogins.Mcp, default);
        // The replaced fact is the most linked of them all.
        foreach (var other in current.Take(4))
            await MemoryRows.Run(s, "INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by) VALUES(gen_random_uuid(),@a,@b,'relates_to','verified','DEMO',@p)",
                ("a", old), ("b", other), ("p", who.Alex));

        var config = MemoryToolSettings.On;
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(config));
        var graph = await new Principals(s).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { GraphNodeLimit = 10 },
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            var searched = await host.Read(who.Sam, null, null, default, new GraphQuery("search", "Quarle"));
            Assert.Equal(10, searched.nodes.Count);
            Assert.DoesNotContain(searched.nodes, n => n.id == Star(old));
            var next = await host.Read(who.Sam, null, null, default, new GraphQuery("search", "Quarle", searched.nextCursor));
            Assert.Equal(Star(old), next.nodes.Last().id);
            Assert.Equal("replaced", next.nodes.Last().truth!.state);
            var explored = await host.Explore(who.Sam, "Quarle", null, 1, 5, default);
            Assert.DoesNotContain(explored.stars, x => x.graphId == Star(old));
        }
        finally { try { Directory.Delete(state, true); } catch (IOException) { } }
    }

    // With scopes on a replaced private fact is its owner's alone, as any private star is (SB-P4-11).
    [PostgresFact] public async Task With_scopes_on_a_replaced_private_fact_is_its_owners_alone()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-replaced-on");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var old = (await Store.Create(who.Alex, new("DEMO: Alex's fictional gym day is Monday."), MemoryLogins.Mcp, default)).Id;
            var fresh = (await Store.Create(who.Alex, new("DEMO: Alex's fictional gym day is Thursday now."), MemoryLogins.Mcp, default)).Id;
            var plan = await Store.Supersede(who.Alex, fresh, old, null, MemoryLogins.Mcp, default);
            await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, MemoryLogins.Mcp, default);
            using var map = await Rebuilt(MemoryToolSettings.On);
            Assert.Equal(new[] { true, false, false, false }, await map.Seen(Star(old, "alex"), who.Alex, who.Sam, who.House, map.App));
            Assert.Equal(new GraphTruth("replaced", Star(fresh, "alex")),
                (await map.Nodes(who.Alex)).Single(n => n.id == Star(old, "alex")).truth);
            // Another class's test may leave a replaced Family fact in this database: Sam's map names none of Alex's.
            Assert.DoesNotContain(await map.Nodes(who.Sam), n => n.id == Star(old, "alex") || n.truth?.replacedBy == Star(fresh, "alex"));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task With_scopes_on_a_native_memorys_star_is_served_to_its_memorys_readers_alone()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-on");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var mine = (await Store.Create(who.Alex, new("DEMO: Alex's private fictional plan for the shed.", Project: "demo-shed-plan"), MemoryLogins.Mcp, default));
            var family = (await Store.Create(who.Sam, new("DEMO: the family shed is painted green.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            var held = (await Store.Create(who.Sam, new("DEMO: a held fictional note.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", held));
            using var map = await Rebuilt();
            var alexStar = Star(mine.Id, "alex");
            var alexProject = MemoryStars.ProjectKey("alex", mine.Project!.Id).GraphId;
            Assert.Equal(new[] { true, false, false, false }, await map.Seen(alexStar, who.Alex, who.Sam, who.House, map.App));
            Assert.Equal(new[] { true, false, false }, await map.Seen(alexProject, who.Alex, who.Sam, map.App));
            Assert.Equal(new[] { true, true, true, true }, await map.Seen(Star(family), who.Alex, who.Sam, who.House, map.App));
            // A held memory is no one's star; its text is no one's either.
            Assert.All((await map.Nodes(who.Sam)).Concat(await map.Nodes(who.Alex)), n => Assert.NotEqual(Star(held), n.id));
            // Sam's map, read whole, holds nothing of Alex's, and Sam's view of Alex's project is as of an unknown star.
            Assert.DoesNotContain(await map.Nodes(who.Sam), n => n.resource.owner == "alex");
            Assert.Empty((await map.Host.Read(who.Sam, null, null, default, new GraphQuery("project", alexProject))).nodes);
            Assert.Null(await map.Text(who.Sam, alexStar));
            Assert.Equal("DEMO: Alex's private fictional plan for the shed.", await map.Text(who.Alex, alexStar));
            // Alex leaves Family: the Family memory's star is Sam's and the household view's, no longer Alex's, at once.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='alex'");
            Assert.Equal(new[] { false, true, true }, await map.Seen(Star(family), who.Alex, who.Sam, who.House));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // The catalog's search tools never serve a native memory's rows (memory.md 10.1), and those rows take no result's
    // place: search_knowledge and search_resources with a limit get that many catalog records while star rows titled
    // with the same words exist, and so does a related list.
    [PostgresFact] public async Task The_catalog_search_tools_leave_native_star_rows_out_before_their_limit()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-search");
        await Scoped.Mode(s, "off");
        var db = RecallRows.Db(s);
        var notes = new List<Guid>();
        for (var i = 0; i < 3; i++) notes.Add(await RecallRows.Add(db, s, $"Quillby note {i}", $"the Quillby hedge, part {i}."));
        for (var i = 0; i < 6; i++) await Store.Create(who.Alex, new($"DEMO: Quillby Quillby hedge {i}."), MemoryLogins.Mcp, default);
        using var map = await Rebuilt(null, new RecallRows.Notes());
        Assert.True((long)(await MemoryRows.Scalar(s,
            "SELECT count(*) FROM resources WHERE provider='memory' AND relationship_snapshot IS NOT NULL AND title LIKE '%Quillby%'"))! >= 6);
        var retrieval = new Retrieval(db);
        foreach (var answer in new[] { await retrieval.Search(map.App, "Quillby", 3, default),
                     await retrieval.SearchResources(map.App, "Quillby", "", "", "", "", 3, default) })
        {
            using var found = JsonDocument.Parse(answer);
            Assert.Equal(3, found.RootElement.GetArrayLength());
            Assert.All(found.RootElement.EnumerateArray(), r => Assert.Equal(RecallRows.Provider, r.GetProperty("provider").GetString()));
        }
    }

    // A rebuild whose read of the memory tables fails, or whose every source fails, hides no memory: the last pass's
    // stars stay while they are fresh, and the next good pass lists them again.
    [PostgresFact] public async Task A_failed_rebuild_hides_no_memory()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-failed");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: the failed-pass fictional note."), MemoryLogins.Mcp, default)).Id;
        var failing = false;
        using var map = await Rebuilt(() => failing);
        Assert.True(await map.Sees(who.Sam, Star(id)));
        failing = true;
        Assert.Equal("complete", await map.Host.Refresh(false, default));
        Assert.Contains("catalog unavailable (TimeoutException)", map.Host.Notes);
        Assert.True(await map.Sees(who.Sam, Star(id)));
        failing = false;
        Assert.Equal("complete", await map.Host.Refresh(false, default));
        Assert.True(await map.Sees(who.Sam, Star(id)));
    }

    // One memory, one star (RHY-33): a save_document copy of a native memory is no document star beside it, and never
    // comes back as one while the memory is held, deprecated or in a draft.
    [PostgresFact] public async Task A_save_document_copy_of_a_memory_is_no_star_of_its_own()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-copy");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: the copied fictional memory."), MemoryLogins.Mcp, default)).Id;
        // A household save_document copy of it, with its text, as an AI client saves one (017): its external id the memory's.
        var copy = Guid.NewGuid();
        await Scoped.Execute(s, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,metadata)
            VALUES(@id,'household','documents',@e,'document','shared','DEMO copy of a memory','DEMO: the copied fictional memory.','current',
              '{"retrievalMode":"dated-snapshot"}')
            """, ("id", copy), ("e", id.ToString()));
        try
        {
            using var map = await Rebuilt();
            var nodes = await map.Nodes(who.Sam);
            Assert.Single(nodes, n => n.id == Star(id));
            Assert.DoesNotContain(nodes, n => n.resource.identity.provider == CatalogStars.Provider && n.resource.identity.externalId.EndsWith(id.ToString()));
            await Store.SetStatus(who.Alex, id, "deprecated", null, MemoryLogins.Mcp, default);
            Assert.Equal("complete", await map.Host.Refresh(false, default));
            Assert.DoesNotContain(await map.Nodes(who.Sam), n => n.id == Star(id) || n.resource.identity.externalId.EndsWith(id.ToString()));
        }
        finally { await Scoped.Execute(s, "DELETE FROM resources WHERE id=@id", ("id", copy)); }
    }

    // Native projects sit beside note and Second Brain projects: a note folder named as a Family project keeps its own
    // star and members, and the native project's star is another, with its own.
    [PostgresFact] public async Task Note_and_Second_Brain_project_stars_are_unchanged_beside_native_ones()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-projects");
        await Scoped.Mode(s, "off");
        // The Second Brain adapter against its fictional service, whose personal workspace has a Kitchen project.
        using var secrets = new TestSecrets().With("second-brain_token", FakeBrain.Token);
        var brain = new SecondBrainAdapter(new FakeBrain().Handler);
        brain.Configure(new(new("second-brain", new Dictionary<string, object?> { ["enabled"] = "true", ["url"] = "https://brain.example.invalid/", ["owner"] = "alex" }),
            TestSettings.Use(), fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"))));
        var kitchen = new ResourceKey("alex", "second-brain", "second-brain", "project:personal:kitchen").GraphId;
        var before = await Rebuilt(null, new ProjectNotes(), brain);
        string Folder(string id) => ProjectNotes.Key(id).GraphId;
        var notesView = (await before.Host.Read(who.Sam, null, null, default, new GraphQuery("project", Folder("folder:garden")))).nodes.Select(n => n.id).ToArray();
        var brainView = (await before.Host.Read(who.Sam, null, null, default, new GraphQuery("project", kitchen))).nodes.Select(n => n.id).ToArray();
        Assert.Contains(kitchen, brainView);
        before.Dispose();
        var garden = (await Store.CreateProject(who.Alex, new("garden", Slug: "garden"), MemoryLogins.Mcp, default)).Project;
        var bean = (await Store.Create(who.Alex, new("DEMO: native beans.", Project: "garden"), MemoryLogins.Mcp, default)).Id;
        // A native project of the Second Brain project's own name, with a member.
        await Store.CreateProject(who.Alex, new("Kitchen", Slug: "kitchen"), MemoryLogins.Mcp, default);
        await Store.Create(who.Alex, new("DEMO: native batch cooking on Sundays.", Project: "kitchen"), MemoryLogins.Mcp, default);
        using var map = await Rebuilt(null, new ProjectNotes(), brain);
        Assert.Equal(notesView, (await map.Host.Read(who.Sam, null, null, default, new GraphQuery("project", Folder("folder:garden")))).nodes.Select(n => n.id));
        Assert.Equal(brainView, (await map.Host.Read(who.Sam, null, null, default, new GraphQuery("project", kitchen))).nodes.Select(n => n.id));
        var native = MemoryStars.ProjectKey("household", garden.Id).GraphId;
        Assert.NotEqual(Folder("folder:garden"), native);
        Assert.Equal([native, Star(bean)], (await map.Host.Read(who.Sam, null, null, default, new GraphQuery("project", native))).nodes.Select(n => n.id));
    }
}

// The pass's read of native memories in a budget (SB-P4-11), in a database of its own so only its memories count: with
// memory.tools on a replaced memory comes after every current one in the read's LIMIT, even when it is the newest, and
// is read as replaced, so an adapter's memories keep their room and the replaced fact takes only what is left.
public sealed class MemoryStarBudgetTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    async Task<GraphNode[]> Rebuilt(int budget, Guid reader, params IAdapter[] adapters)
    {
        var config = MemoryToolSettings.On;
        var registry = new AdapterRegistry(adapters);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { MaxMemories = budget },
                c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
            Assert.Equal("complete", await host.Refresh(false, default));
            return [.. (await host.Read(reader, null, null, default, new GraphQuery())).nodes];
        }
        finally { try { Directory.Delete(state, true); } catch (IOException) { } }
    }

    static string Star(Guid memory) => MemoryStars.Key("household", memory).GraphId;

    [PostgresFact] public async Task The_read_keeps_current_memories_first_and_a_replaced_one_in_the_room_left()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-budget");
        await Scoped.Mode(s, "off");
        async Task<Guid> Remember(string text) => (await Store.Create(who.Alex, new(text), MemoryLogins.Mcp, default)).Id;
        var a = await Remember("DEMO: the Ruddle bins go out on Thursday.");
        var b = await Remember("DEMO: the Ruddle library shuts at six.");
        var old = await Remember("DEMO: the Ruddle bus leaves at eight.");
        var fresh = await Remember("DEMO: the Ruddle bus leaves at nine now.");
        var plan = await Store.Supersede(who.Alex, fresh, old, null, MemoryLogins.Mcp, default);
        await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, MemoryLogins.Mcp, default);
        // The replaced fact was written after two of the current ones, so newest first alone would take it before them.
        string[] current = [Star(a), Star(b), Star(fresh)];

        // A budget of the three current memories: all three, never the replaced one in a current one's place.
        var three = (await Rebuilt(3, who.Sam)).Select(n => n.id).ToArray();
        Assert.All(current, id => Assert.Contains(id, three));
        Assert.DoesNotContain(Star(old), three);
        // One more, and an adapter's memory first: the adapter keeps its room.
        var notes = new ManyNotes("notes-a", 1);
        var four = await Rebuilt(4, who.Sam, notes);
        Assert.Equal(1, notes.Offered);
        Assert.Contains(four, n => n.resource.identity.provider == "notes-a");
        Assert.DoesNotContain(four, n => n.id == Star(old));
        // Room for it too: a star, named replaced.
        var five = await Rebuilt(5, who.Sam, new ManyNotes("notes-a", 1));
        Assert.Equal(new GraphTruth("replaced", Star(fresh)), five.Single(n => n.id == Star(old)).truth);
    }
}
