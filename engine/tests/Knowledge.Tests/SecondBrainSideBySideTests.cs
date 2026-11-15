using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The Second Brain adapter side by side with native memories (SB-P3-15, D-047; docs/dev/memory.md section 10.4, ledger
// RHY-29; acceptance story 18): the adapter reads as it did, read-only, beside native memories, and a comparison check
// pairs each Second Brain memory on the map with the native memory of the same words. The two are two stars: each with
// its own provider, id, owner and links, its text read from its own source, never merged by title. Native writes never
// reach the service. A fictional service (FakeBrain, which refuses anything but a GET) and fictional people only; every
// text is DEMO or the fake service's own synthetic sample.
[Collection(SharedStateCollection.Name)]
public sealed class SecondBrainSideBySideTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    sealed record Map(RelationshipHost Host, string State) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(State, true); } catch (IOException) { } }
        public async Task<(GraphNode[] Nodes, GraphEdge[] Edges)> Read(Guid reader)
        {
            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            for (string? cursor = null; ;)
            {
                var page = await Host.Read(reader, null, null, default, new GraphQuery(Cursor: cursor));
                nodes.AddRange(page.nodes);
                edges.AddRange(page.edges);
                if (page.nextCursor is not { } next) return ([.. nodes], [.. edges]);
                cursor = next;
            }
        }
        public async Task<string?> Text(Guid reader, string star) =>
            (await Host.Read(reader, null, null, default, new GraphQuery("content", star))).nodes.SingleOrDefault()?.resource.content;
    }

    // The map rebuilt once over the database, with the adapters given.
    async Task<Map> Rebuilt(params IAdapter[] adapters)
    {
        var config = TestSettings.Use();
        var registry = new AdapterRegistry(adapters);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        return new(host, state);
    }

    // The adapter as an install configures it: the token's personal workspace, owned by alex, and one team workspace.
    SecondBrainAdapter Adapter(FakeBrain service)
    {
        var adapter = new SecondBrainAdapter(service.Handler);
        adapter.Configure(new(new("second-brain", new Dictionary<string, object?>
            {
                ["enabled"] = "true", ["url"] = "https://brain.example.invalid/", ["owner"] = "alex", ["teams"] = new List<object> { FakeBrain.Team },
            }),
            TestSettings.Use(), fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"))));
        return adapter;
    }

    static bool FromAdapter(GraphNode n) => n.provenance.sourceId == "second-brain";
    static bool Native(GraphNode n) => n.provenance.sourceId == MemoryStars.Provider;

    // What a reader's map shows of the adapter: its stars and the links between them, each pass's times aside (a pass
    // stamps them anew), in a fixed order.
    static async Task<string[]> AdapterView(Map map, Guid reader)
    {
        var (nodes, edges) = await map.Read(reader);
        var mine = nodes.Where(FromAdapter).ToDictionary(n => n.id);
        return
        [
            .. mine.Values.Select(n => JsonSerializer.Serialize(new { n.id, n.title, n.type, n.overview, n.provenance.recordId, n.resource.identity,
                n.resource.canonicalUrl, n.resource.sourceUrl, n.resource.revision, n.resource.owner, n.resource.access, n.resource.availability,
                n.resource.content, n.groups })).Order(StringComparer.Ordinal),
            .. edges.Where(e => mine.ContainsKey(e.source) && mine.ContainsKey(e.target))
                .Select(e => JsonSerializer.Serialize(new { e.id, e.source, e.target, e.relationship, e.strength, e.type, e.origin, e.directed, e.verification }))
                .Order(StringComparer.Ordinal),
        ];
    }

    // The comparison check: each memory star of the adapter's paired with the native memory star of the same words (both
    // titled by one rule, SecondBrainAdapter.Title), and what each side has that the other does not; of the native
    // memories, those a test wrote (a class's tests share one database).
    sealed record Comparison(IReadOnlyList<(GraphNode Native, GraphNode Adapter)> Pairs, IReadOnlyList<GraphNode> NativeOnly, IReadOnlyList<GraphNode> AdapterOnly);
    static Comparison Compare(IEnumerable<GraphNode> nodes, IEnumerable<Guid> written)
    {
        var mine = written.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        var memories = nodes.Where(n => n.type == "memory").ToList();
        var native = memories.Where(n => Native(n) && mine.Contains(n.resource.identity.externalId)).ToList();
        var adapter = memories.Where(FromAdapter).ToList();
        var pairs = native.SelectMany(n => adapter.Where(a => a.title == n.title).Select(a => (n, a))).ToList();
        return new(pairs, [.. native.Where(n => pairs.All(p => p.n != n))], [.. adapter.Where(a => pairs.All(p => p.a != a))]);
    }

    // With scopes off: the adapter's stars, links and text are as before beside native memories of the same words and a
    // native project of the service's project's name; each pair is two stars, and nothing native calls the service.
    [PostgresFact] public async Task Beside_native_memories_of_the_same_words_the_adapter_reads_as_before_and_each_is_its_own_star()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "side-off");
        await Scoped.Mode(s, "off");
        using var secrets = new TestSecrets().With("second-brain_token", FakeBrain.Token);
        var service = new FakeBrain();
        var brain = Adapter(service);
        var readers = new[] { who.Alex, who.Sam, who.House };
        var before = new Dictionary<Guid, string[]>();
        using (var map = await Rebuilt(brain))
        {
            foreach (var reader in readers) before[reader] = await AdapterView(map, reader);
            Assert.Equal(3, (await map.Read(who.Sam)).Nodes.Count(n => FromAdapter(n) && n.type == "memory"));
        }
        Assert.Contains(before[who.Sam], line => line.Contains("Kitchen"));

        // Native memories with the words of the service's three eligible memories, one in a native project of its
        // project's name, one changed and the change undone: none of it calls the service.
        var calls = service.Handler.Requests.Count;
        var twins = new Dictionary<string, Guid>();
        foreach (var row in service.Rows.Where(r => r.Id == FakeBrain.Id(1) || r.Id == FakeBrain.Id(2) || r.Id == FakeBrain.Id(5)))
            twins[row.Id] = (await Store.Create(who.Alex, new(row.Content, Project: row.Id == FakeBrain.Id(2) ? "kitchen" : null), MemoryLogins.Mcp, default)).Id;
        await Store.Update(who.Alex, twins[FakeBrain.Id(5)], new(Tags: ["fridge"]), null, MemoryLogins.Mcp, default);
        await Store.Undo(who.Alex, twins[FakeBrain.Id(5)], null, null, MemoryLogins.Mcp, default);
        Assert.Equal(calls, service.Handler.Requests.Count);

        using var after = await Rebuilt(brain);
        foreach (var reader in readers) Assert.Equal(before[reader], await AdapterView(after, reader));
        // The comparison: every one of the adapter's memories has its native twin, by its words; each pair is two stars.
        var nodes = (await after.Read(who.Sam)).Nodes;
        var compared = Compare(nodes, twins.Values);
        Assert.Equal(3, compared.Pairs.Count);
        Assert.Empty(compared.NativeOnly);
        Assert.Empty(compared.AdapterOnly);
        foreach (var (native, adapter) in compared.Pairs)
        {
            Assert.NotEqual(native.id, adapter.id);
            Assert.Equal((MemoryStars.Provider, "second-brain"), (native.resource.identity.provider, adapter.resource.identity.provider));
            Assert.Single(nodes, n => n.id == native.id);
            Assert.Single(nodes, n => n.id == adapter.id);
        }
        // Their owners as each source files them: the native ones the household's (scopes off), the service's personal
        // workspace alex's and its team the household's.
        var owners = compared.Pairs.ToDictionary(p => p.Adapter.resource.identity.externalId, p => (p.Native.resource.owner, p.Adapter.resource.owner));
        Assert.Equal(("household", "alex"), owners[FakeBrain.Id(1)]);
        Assert.Equal(("household", "alex"), owners[FakeBrain.Id(2)]);
        Assert.Equal(("household", "household"), owners[FakeBrain.Id(5)]);
        // A native project of the service's project's name is a star of its own beside the service's.
        Assert.Single(nodes, n => n.type == "project" && Native(n) && n.title == "kitchen");
        Assert.Contains(nodes, n => n.type == "project" && FromAdapter(n) && n.title == "Kitchen");

        // Each star's text from its own source, read when it is opened: the native one from the memory tables, without a
        // call to the service; the adapter's from the service, with one.
        foreach (var (native, adapter) in compared.Pairs)
        {
            var words = service.Rows.Single(r => r.Id == adapter.resource.identity.externalId).Content;
            var asked = service.Handler.Requests.Count;
            Assert.Equal(words, await after.Text(who.Sam, native.id));
            Assert.Equal(asked, service.Handler.Requests.Count);
            Assert.Equal(words, await after.Text(who.Sam, adapter.id));
            Assert.Contains("GET brain.example.invalid/entry", service.Handler.Requests.Skip(asked));
        }
        // Read-only throughout: every request the adapter made was a GET (the fake service fails a test on anything else).
        Assert.All(service.Handler.Requests, r => Assert.StartsWith("GET brain.example.invalid/", r));
    }

    // With scopes on, each side is served to its own readers: the service's personal workspace and Alex's private
    // memories to Alex alone, its team and Family's memories to Family, and the adapter's stars to each reader as before.
    [PostgresFact] public async Task With_scopes_on_each_side_is_served_to_its_own_readers_and_the_adapter_reads_as_before()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "side-on");
        await MemoryLogins.Family(s);
        await Scoped.Mode(s, "on");
        try
        {
            using var secrets = new TestSecrets().With("second-brain_token", FakeBrain.Token);
            var service = new FakeBrain();
            var brain = Adapter(service);
            var readers = new[] { who.Alex, who.Sam, who.House };
            var before = new Dictionary<Guid, string[]>();
            using (var map = await Rebuilt(brain))
                foreach (var reader in readers) before[reader] = await AdapterView(map, reader);
            // The personal workspace is Alex's: Sam's and the household view's maps hold only the team's memory.
            Assert.True(before[who.Alex].Length > before[who.Sam].Length);
            Assert.Equal(before[who.Sam], before[who.House]);

            var walks = service.Rows.Single(r => r.Id == FakeBrain.Id(1)).Content;
            var fridge = service.Rows.Single(r => r.Id == FakeBrain.Id(5)).Content;
            Guid[] written = [(await Store.Create(who.Alex, new(walks), MemoryLogins.Mcp, default)).Id,
                (await Store.Create(who.Alex, new(fridge, Scope: "shared"), MemoryLogins.Mcp, default)).Id];
            // A native write and an undo meanwhile, as with scopes off: the Family twin changed and put back by its author.
            await Store.Update(who.Alex, written[1], new(Tags: ["fridge"]), null, MemoryLogins.Mcp, default);
            await Store.Undo(who.Alex, written[1], null, null, MemoryLogins.Mcp, default);
            using var after = await Rebuilt(brain);
            foreach (var reader in readers) Assert.Equal(before[reader], await AdapterView(after, reader));
            // Alex: both twins, and the service's one memory she has no native copy of.
            var alex = Compare((await after.Read(who.Alex)).Nodes, written);
            Assert.Equal([FakeBrain.Id(1), FakeBrain.Id(5)], alex.Pairs.Select(p => p.Adapter.resource.identity.externalId).Order(StringComparer.Ordinal));
            Assert.Equal([FakeBrain.Id(2)], alex.AdapterOnly.Select(a => a.resource.identity.externalId));
            Assert.Equal(("alex", "alex"), alex.Pairs.Where(p => p.Adapter.resource.identity.externalId == FakeBrain.Id(1))
                .Select(p => (p.Native.resource.owner, p.Adapter.resource.owner)).Single());
            // Sam and the household view: only Family's pair, never Alex's private memory or her personal workspace.
            foreach (var reader in new[] { who.Sam, who.House })
            {
                var seen = Compare((await after.Read(reader)).Nodes, written);
                Assert.Equal([FakeBrain.Id(5)], seen.Pairs.Select(p => p.Adapter.resource.identity.externalId));
                Assert.Empty(seen.NativeOnly);
                Assert.Empty(seen.AdapterOnly);
                Assert.Equal(("household", "household"), (seen.Pairs[0].Native.resource.owner, seen.Pairs[0].Adapter.resource.owner));
            }
            Assert.All(service.Handler.Requests, r => Assert.StartsWith("GET brain.example.invalid/", r));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }
}
