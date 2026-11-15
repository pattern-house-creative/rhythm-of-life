using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// Fictional notes whose text the pass embeds, as the notes and Second Brain adapters do.
internal sealed class EmbeddedNotes(string name, params string[] titles) : IMemorySource
{
    public string Name => name;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [name] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var records = titles.Select(t =>
        {
            var key = new ResourceKey("household", name, name, t.ToLowerInvariant().Replace(' ', '-'));
            return new DiscoveryRecord(key, "shared", "note", t, null, "r1", null, now, [], ContentRevision: "r1",
                Embedding: pass.Vector(key, "r1", t, "DEMO: a fictional note about " + t));
        }).ToArray();
        return Task.FromResult(new SourceListing(records, Complete: true));
    }
    public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
}

// Fictional files that, like the Drive adapter, read a file's text only when it changed or its vector is outdated, and
// otherwise keep the vector from the last pass. `Rereads` false stands for a file left for a later pass (the Drive
// adapter reads a bounded number per pass); `Invents` lists a vector that no pass made.
internal sealed class KeptFiles(string name, params string[] titles) : IDocumentSource
{
    public bool Rereads { get; set; } = true;
    public bool Invents { get; set; }
    public string Name => name;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [name] = new(SourceAuthority.AuthorizedArchive, "household") };
    public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var records = titles.Select(t =>
        {
            var key = new ResourceKey("household", name, name, t.ToLowerInvariant().Replace(' ', '-'));
            var record = new DiscoveryRecord(key, "shared", "document", t, null, "r1", null, now, [], ContentRevision: "r1");
            if (Invents) return record with { Embedding = EmbeddingModelTests.Same() };
            return pass.Previous.TryGetValue(key, out var previous) && previous.ContentRevision == "r1" && !(Rereads && pass.Outdated(previous))
                ? record with { Embedding = previous.Embedding }
                : record with { Embedding = pass.Vector(key, "r1", t, "DEMO: a fictional file about " + t) };
        }).ToArray();
        return Task.FromResult(new SourceListing(records, Complete: true));
    }
    public Task<System.Text.Json.Nodes.JsonObject?> Contents(DiscoveryRecord record, CancellationToken ct) =>
        Task.FromResult<System.Text.Json.Nodes.JsonObject?>(null);
}

// Every star vector names the model that made it: the tag is stored with the vector, an unchanged record keeps its
// vector only while the model is the same, a model change embeds again, and vectors of two models are never compared.
public sealed class EmbeddingModelTests
{
    static readonly Guid Principal = Guid.NewGuid();
    // One unit vector for everything: any two stars of one model are as close as can be.
    internal static float[] Same() => [.. Enumerable.Range(0, 384).Select(_ => (float)(1 / Math.Sqrt(384)))];
    static DiscoveryRecord Star(string title, string? model, string kind = "note") =>
        SyntheticRelationshipSource.New("notes", kind, "notes") with { Title = title, Embedding = Same(), EmbeddingModel = model };
    static string[] Pairs(IReadOnlyList<Suggestion> links, IReadOnlyList<DiscoveryRecord> stars) =>
        [.. links.Select(s => s.Type + ": " + string.Join(" + ", new[] { s.Source, s.Target }.Select(k => stars.Single(r => r.Key == k).Title).Order())).Order()];

    [Fact] public async Task Vectors_of_two_models_never_pair_even_when_identical()
    {
        DiscoveryRecord[] stars = [Star("Garden plan", "model-a"), Star("Garden plan", "model-b"), Star("Copy of Garden plan", "model-b"),
            Star("Allotment rota", "model-a")];
        var links = await new SimilaritySuggestions(0.6, 3, mutualThreshold: 0.45).Suggest(stars, default);
        Assert.Equal(["copy_of: Copy of Garden plan + Garden plan", "similar_to: Allotment rota + Garden plan"], Pairs(links, stars));
        // Each copy and similar link joins two stars of one model.
        Assert.All(links, l => Assert.Equal(stars.Single(r => r.Key == l.Source).EmbeddingModel, stars.Single(r => r.Key == l.Target).EmbeddingModel));
        // A vector stored before tags is the pinned MiniLM's: it pairs with that model's vectors and with no other.
        DiscoveryRecord[] old = [Star("Garden plan", null), Star("Garden budget", LocalEmbeddings.Model), Star("Garden rota", "model-b")];
        Assert.Equal(["similar_to: Garden budget + Garden plan"], Pairs(await new SimilaritySuggestions(0.6, 3).Suggest(old, default), old));
        Assert.Equal(SimilaritySuggestions.Untagged, SimilaritySuggestions.ModelOf(old[0]));
        Assert.Null(SimilaritySuggestions.ModelOf(old[0] with { Embedding = null }));
    }

    [Fact] public void A_tag_needs_a_vector_and_a_bounded_name()
    {
        static void Check(DiscoveryRecord r) => RelationshipDiscovery.Validate(new([r]));
        Check(Star("Garden plan", "model-a"));
        Check(Star("Garden plan", null));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", "model-a") with { Embedding = null }));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", "")));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", new string('m', 201))));
    }

    [Fact] public async Task A_pass_tags_every_vector_reuses_one_only_for_the_same_model_and_drops_one_it_cannot_name()
    {
        var notes = new EmbeddedNotes("tagged-notes", "Garden plan", "Seed order");
        var files = new KeptFiles("tagged-files", "Garden budget");
        var calls = 0;
        float[]? Embed(string title, string text) { calls++; return Same(); }
        CatalogRelationshipSource Source(string model, IReadOnlyList<DiscoveryRecord>? stored)
        {
            var source = new CatalogRelationshipSource(RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath()),
                new AdapterRegistry([notes, files]), (_, _) => Task.FromResult(new CatalogInventory([], [])), Embed, model);
            source.Prime(stored ?? []);
            return source;
        }
        static IReadOnlyList<DiscoveryRecord> Mine(DiscoveryBatch b) => [.. b.Records.Where(r => r.Key.Provider.StartsWith("tagged-", StringComparison.Ordinal))];
        async Task<IReadOnlyList<DiscoveryRecord>> Pass(string model, IReadOnlyList<DiscoveryRecord>? stored)
        {
            var batch = await Source(model, stored).Fetch(Principal, default);
            RelationshipDiscovery.Validate(batch);
            return Mine(batch);
        }

        var first = await Pass("model-a", null);
        Assert.Equal(3, calls);
        Assert.All(first, r => Assert.Equal("model-a", r.EmbeddingModel));
        // Unchanged records keep their vectors while the model is the same.
        calls = 0;
        var same = await Pass("model-a", first);
        Assert.Equal(0, calls);
        Assert.All(same, r => Assert.Equal("model-a", r.EmbeddingModel));
        // Another model embeds every record again, the kept file included, since it reads outdated vectors again.
        var changed = await Pass("model-b", same);
        Assert.Equal(3, calls);
        Assert.All(changed, r => Assert.Equal("model-b", r.EmbeddingModel));

        // A file not read again this pass keeps its vector and the model that made it, and pairs with no other model's.
        calls = 0;
        files.Rereads = false;
        var mixed = await Pass("model-c", changed);
        Assert.Equal(2, calls);
        Assert.Equal(["model-b", "model-c", "model-c"], mixed.Select(r => r.EmbeddingModel!).Order());
        var links = await new SimilaritySuggestions(0.6, 3).Suggest(mixed, default);
        Assert.Equal(["similar_to: Garden plan + Seed order"], Pairs(links, mixed));

        // Untagged vectors from before tags are reused for MiniLM and embedded again for any other model.
        var untagged = first.Select(r => r with { EmbeddingModel = null }).ToArray();
        files.Rereads = true;
        calls = 0;
        Assert.All(await Pass(LocalEmbeddings.Model, untagged), r => Assert.Equal(LocalEmbeddings.Model, r.EmbeddingModel));
        Assert.Equal(0, calls);
        Assert.All(await Pass("model-b", untagged), r => Assert.Equal("model-b", r.EmbeddingModel));
        Assert.Equal(3, calls);
        // Untagged means the MiniLM id of D-052, not whatever LocalEmbeddings.Model says now: a later revision or pooling
        // of the same model gets a new id and embeds every untagged record again rather than mixing the two.
        Assert.Equal("minilm-l6-v2-quantized-afdb6f1a-mean-l2-v1", SimilaritySuggestions.Untagged);
        Assert.Equal(SimilaritySuggestions.Untagged, SimilaritySuggestions.ModelOf(untagged[0]));
        calls = 0;
        const string NextRevision = "minilm-l6-v2-quantized-afdb6f1a-mean-l2-v2";
        Assert.All(await Pass(NextRevision, untagged), r => Assert.Equal(NextRevision, r.EmbeddingModel));
        Assert.Equal(3, calls);

        // A vector no pass made has no known model: it is left out instead of compared.
        files.Invents = true;
        var invented = await Pass("model-a", first);
        Assert.Null(Assert.Single(invented, r => r.Key.Provider == "tagged-files").Embedding);
        Assert.All(invented.Where(r => r.Key.Provider == "tagged-notes"), r => Assert.Equal("model-a", r.EmbeddingModel));
    }

    [Fact] public async Task Without_embeddings_a_pass_tags_kept_vectors_with_their_own_model()
    {
        var files = new KeptFiles("tagged-files", "Garden budget");
        var source = new CatalogRelationshipSource(RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath()),
            new AdapterRegistry([files]), (_, _) => Task.FromResult(new CatalogInventory([], [])));
        var stored = new DiscoveryRecord(new("household", "tagged-files", "tagged-files", "garden-budget"), "shared", "document",
            "Garden budget", null, "r1", null, DateTimeOffset.UtcNow.AddMinutes(-5), [], ContentRevision: "r1", Embedding: Same(), EmbeddingModel: "model-a");
        source.Prime([stored]);
        var kept = Assert.Single((await source.Fetch(Principal, default)).Records, r => r.Key.Provider == "tagged-files");
        Assert.Equal("model-a", kept.EmbeddingModel);
        Assert.NotNull(kept.Embedding);
        Assert.Throws<ArgumentException>(() => new CatalogRelationshipSource(RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath()),
            new AdapterRegistry([files]), (_, _) => Task.FromResult(new CatalogInventory([], [])), (_, _) => Same()));
    }
}

// Against a real database: the tag is stored in the map's snapshot beside the vector, and a rebuild under another model
// draws no similar link between two models' stars.
public sealed class EmbeddingModelDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task The_tag_is_stored_and_a_model_change_relinks_only_within_one_model()
    {
        var config = TestSettings.Use();
        var notes = new EmbeddedNotes("tagged-notes", "Garden plan", "Seed order");
        var files = new KeptFiles("tagged-files", "Garden budget") { Rereads = false };
        var registry = new AdapterRegistry([notes, files]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), "rhythm-graph-" + Guid.NewGuid().ToString("N")[..10]);
        var model = "model-a";
        var calls = 0;
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (_, _) => Task.FromResult(new CatalogInventory([], [])),
                (_, _) => { calls++; return EmbeddingModelTests.Same(); }, model), registry);
        async Task<(IReadOnlyList<DiscoveryRecord> Records, string[] Links)> Map()
        {
            var view = (await host.Readable(graph, default)).View;
            var mine = view.Records.Where(r => r.Key.Provider.StartsWith("tagged-", StringComparison.Ordinal)).ToArray();
            string Title(ResourceKey k) => mine.Single(r => r.Key == k).Title;
            return (mine, [.. view.Links.Where(l => l.Type is SimilaritySuggestions.Type or SimilaritySuggestions.CopyType)
                .Select(l => string.Join(" + ", new[] { Title(l.Source), Title(l.Target) }.Order())).Order()]);
        }
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            var (records, links) = await Map();
            Assert.Equal(3, calls);
            // Reads leave the vector out but keep its model's name.
            Assert.All(records, r => Assert.Equal(("model-a", (float[]?)null), (r.EmbeddingModel, r.Embedding)));
            Assert.Equal(["Garden budget + Garden plan", "Garden budget + Seed order", "Garden plan + Seed order"], links);

            // The same model reuses every stored vector.
            calls = 0;
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Equal(0, calls);

            // Another model: the notes are embedded again; the file keeps model-a's vector until it is read again, and
            // links to neither note meanwhile.
            model = "model-b";
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Equal(2, calls);
            (records, links) = await Map();
            Assert.Equal(["model-a", "model-b", "model-b"], records.Select(r => r.EmbeddingModel!).Order());
            Assert.Equal(["Garden plan + Seed order"], links);

            // Read again, the file joins the new model and its links come back.
            files.Rereads = true;
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Equal(3, calls);
            (records, links) = await Map();
            Assert.All(records, r => Assert.Equal("model-b", r.EmbeddingModel));
            Assert.Equal(["Garden budget + Garden plan", "Garden budget + Seed order", "Garden plan + Seed order"], links);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }
}
