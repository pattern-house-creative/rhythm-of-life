using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The map's links reach a native memory through combined retrieval's hops (D-152): a memory's star row stands for its
// own row, both as a hop's start and as its end, and a held memory is never reached; with scopes on, only its readers.
internal sealed class VetNotes : IMemorySource
{
    public const string Provider = "notes-vet";
    public string Name => Provider;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var key = new ResourceKey("household", Provider, Provider, "vet-bill.md");
        return Task.FromResult(new SourceListing([new(key, "shared", "note", "DEMO Quorrel vet bill", null, "r1", null, DateTimeOffset.UtcNow, [],
            ContentRevision: "r1", Embedding: pass.Vector(key, "r1", "DEMO Quorrel vet bill", "DEMO Quorrel vet bill paid"))], true));
    }
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
    // The stand-in model of the pass: every Quorrel text one direction, everything else another, so the rebuild's
    // similarity suggests a link between the note and a memory about Quorrel.
    public static float[] Vector(string title, string text) =>
        (title + " " + text).Contains("Quorrel", StringComparison.Ordinal) ? [1f, 0f, 0f, 0f] : [0f, 1f, 0f, 0f];
}

[Collection(SharedStateCollection.Name)]
public sealed class MemoryStarRecallTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_link_the_rebuild_writes_reaches_a_memory_through_recall_hops_and_a_held_one_never()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "stars-recall");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var config = TestSettings.Use();
        var registry = new AdapterRegistry([new VetNotes()]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state) with { SimilarityThreshold = 0.5 },
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct),
                (title, text) => VetNotes.Vector(title, text), "demo-vectors"), registry);
        try
        {
            const string text = "DEMO: Alex's private note: the Quorrel kitten sees the vet on Friday.";
            var memory = (await new MemoryStore(s).Create(who.Alex, new(text), MemoryLogins.Mcp, default)).Id;
            var model = new MeaningModel();
            var covered = PassageSources.Of(false, false, true, []);
            await Passages.Sweep(new EmbeddingBackfill(db), model, covered);
            Assert.Equal("complete", await host.Refresh(false, default));
            // The rebuild linked the note's star and the memory's star (a similarity it found), and nothing by hand.
            var note = (Guid)(await MemoryLogins.Scalar(s, "SELECT id FROM resources WHERE provider=@p AND relationship_snapshot IS NOT NULL", ("p", VetNotes.Provider)))!;
            Assert.Equal(1L, await MemoryLogins.Scalar(s, """
                SELECT count(*) FROM relationships l JOIN resources m ON m.id IN (l.source_id,l.target_id)
                WHERE @note IN (l.source_id,l.target_id) AND m.provider='memory' AND m.relationship_snapshot->'Key'->>'ExternalId'=@memory AND l.engine=@engine
                """, ("note", note), ("memory", memory.ToString()), ("engine", RelationshipEngine.Owner)));
            var wordsOnly = new Recall(db, null, covered);
            var withModel = new Recall(db, model, covered);
            // Alex: from the note, by a word only the note holds, through the rebuild's link to her memory, its own text the
            // excerpt; and by its own words (SB-P2-19), with the model off.
            Assert.DoesNotContain(memory, (await wordsOnly.Search(who.Alex, new("bill", 20), default)).Hits.Select(h => h.Id));
            Assert.Equal(text, Assert.Single((await wordsOnly.Search(who.Alex, new("Quorrel kitten", 20), default)).Hits, h => h.Id == memory).Excerpt);
            foreach (var hops in new[] { 1, 2, 3 })
            {
                var reached = Assert.Single((await wordsOnly.Search(who.Alex, new("bill", 20, hops), default)).Hits, h => h.Id == memory);
                Assert.Equal((1, SimilaritySuggestions.Type, note), (reached.Why.Hop, reached.Why.Link, reached.Why.From));
                Assert.Equal(text, reached.Excerpt);
            }
            // And from the memory, found by meaning, through its star's link to the note.
            var back = Assert.Single((await withModel.Search(who.Alex, new("kitten Friday", 20, 1), default)).Hits, h => h.Id == note);
            Assert.Equal((1, memory), (back.Why.Hop, back.Why.From));
            // Sam and the household view never reach Alex's private memory, by its words or by link, at any number of hops.
            foreach (var reader in new[] { who.Sam, who.House })
                foreach (var hops in new[] { 0, 1, 2, 3 })
                    foreach (var recall in new[] { withModel, wordsOnly })
                    {
                        var hits = (await recall.Search(reader, new("Quorrel vet bill kitten", 20, hops), default)).Hits;
                        Assert.DoesNotContain(memory, hits.Select(h => h.Id));
                        Assert.All(hits, h => Assert.DoesNotContain("Quorrel kitten", h.Excerpt));
                    }
            // Held: never reached, by meaning, by its words or by the link, its star's link still in the table.
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions' WHERE id=@id", ("id", memory));
            foreach (var hops in new[] { 0, 1, 2, 3 })
            {
                Assert.DoesNotContain(memory, (await wordsOnly.Search(who.Alex, new("Quorrel vet bill", 20, hops), default)).Hits.Select(h => h.Id));
                Assert.DoesNotContain(memory, (await wordsOnly.Search(who.Alex, new("Quorrel kitten", 20, hops), default)).Hits.Select(h => h.Id));
                Assert.DoesNotContain(memory, (await withModel.Search(who.Alex, new("Quorrel kitten", 20, hops), default)).Hits.Select(h => h.Id));
            }
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
            Directory.Delete(state, true);
        }
    }
}
