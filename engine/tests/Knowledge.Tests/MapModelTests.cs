using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// D-256, graph.similarity.model: a second, model-tagged vector per star (MapEmbedding) made by the map model and read by
// the map's similar and copy links alone, with that model's own recorded thresholds. Absent, nothing changes; a map model
// that is not loaded leaves the links on the live model's vectors and graph.similarity's numbers, and says so. No model
// files needed: the vectors here are fictional.
public sealed class MapModelTests
{
    static readonly Guid Principal = Guid.NewGuid();
    internal static RhythmConfig Settings(string similarity = "", string extra = "") => RhythmConfig.Parse(TestSettings.Yaml + "\n"
        + (similarity.Length == 0 ? "" : "graph:\n  similarity:\n" + string.Concat(similarity.Split('\n').Select(l => "    " + l + "\n"))) + extra);
    static RelationshipHostConfiguration Map(RhythmConfig config) => RelationshipHostConfiguration.From(config, Principal, Path.GetTempPath());
    // A unit vector along one axis: two stars on one axis score 1, on two axes 0.
    internal static float[] Axis(int i) => [.. Enumerable.Range(0, 384).Select(j => j == i ? 1f : 0f)];
    static string[] Pairs(IReadOnlyList<Suggestion> links, IReadOnlyList<DiscoveryRecord> stars) =>
        [.. links.Select(s => s.Type + ": " + string.Join(" + ", new[] { s.Source, s.Target }.Select(k => stars.Single(r => r.Key == k).Title).Order())).Order()];
    static DiscoveryRecord Star(string title, float[]? map, string? model = null) =>
        SyntheticRelationshipSource.New("notes", "note", "notes") with { Title = title, Embedding = EmbeddingModelTests.Same(),
            EmbeddingModel = EmbeddingModels.MiniLmId, MapEmbedding = map, MapEmbeddingModel = map is null ? null : model ?? EmbeddingModels.BgeSmall.Id };

    [Fact] public void Absent_the_map_reads_graph_similarity_as_before_and_the_key_takes_bge_small_with_its_own_thresholds()
    {
        var before = Map(Settings());
        Assert.Null(before.Map);
        Assert.Equal(((double?)0.6, 3, (double?)0.8, (double?)0.45),
            (before.SimilarityThreshold, before.SimilarityNeighbors, before.SimilarityDocumentThreshold, before.SimilarityMutualThreshold));
        Assert.Null(MapModels.Wanted(Settings()));

        var own = EmbeddingThresholds.Tuned[EmbeddingModels.BgeSmall.Id];
        var on = Map(Settings("model: bge-small"));
        Assert.Equal(new MapSimilarity(EmbeddingModels.BgeSmall, own.Similar, own.SimilarDocuments, own.Mutual), on.Map);
        Assert.Equal((0.68, (double?)0.88, (double?)0.73), (on.Map!.Threshold, on.Map.DocumentThreshold, on.Map.MutualThreshold));
        // The numbers a fallback pass reads are graph.similarity's, untouched.
        Assert.Equal(((double?)0.6, (double?)0.8, (double?)0.45), (on.SimilarityThreshold, on.SimilarityDocumentThreshold, on.SimilarityMutualThreshold));
        on.Validate();
        // An install's own graph.similarity numbers are the live model's scale and never stand in for the map model's
        // (the safe reading): only null keeps its meaning, and neighbors is kept as set.
        var mine = Map(Settings("threshold: 0.5\nneighbors: 5\ndocumentThreshold: 0.7\nmutualThreshold: 0.3\nmodel: bge-small"));
        Assert.Equal((0.68, (double?)0.88, (double?)0.73, 5), (mine.Map!.Threshold, mine.Map.DocumentThreshold, mine.Map.MutualThreshold, mine.SimilarityNeighbors));
        var nulls = Map(Settings("documentThreshold: null\nmutualThreshold: null\nmodel: bge-small"));
        Assert.Equal((0.68, (double?)null, (double?)null), (nulls.Map!.Threshold, nulls.Map.DocumentThreshold, nulls.Map.MutualThreshold));
        // With similar links off, or the embeddings adapter off, no star has a vector: no map model either.
        Assert.Null(Map(Settings("threshold: null\nmodel: bge-small")).Map);
        Assert.Null(Map(Settings("model: bge-small", "adapters:\n  embeddings:\n    enabled: false\n")).Map);
        // adapters.embeddings.thresholds moves the live model's numbers only.
        var tuned = Map(Settings("model: bge-small", "adapters:\n  embeddings:\n    enabled: true\n    thresholds: tuned\n"));
        Assert.Equal((0.68, (double?)0.56), (tuned.Map!.Threshold, tuned.SimilarityThreshold));

        foreach (var wrong in new[] { "bge-base", "minilm", "mpnet" })
            Assert.StartsWith("graph.similarity.model is bge-small, or absent",
                Assert.Throws<ConfigException>(() => Settings("model: " + wrong)).Message);
        // Every model the key takes has recorded thresholds the map accepts.
        foreach (var model in MapModels.Allowed)
            Map(Settings("model: " + model.Name)).Validate();
    }

    [Fact] public async Task With_a_map_model_the_links_compare_only_its_vectors_and_without_one_only_the_live_models()
    {
        // Every star's live vector is the same; by the map model only the garden plan and the seed order are alike.
        DiscoveryRecord[] stars = [Star("Garden plan", Axis(0)), Star("Seed order", Axis(0)), Star("Allotment rota", Axis(1)),
            Star("Bike repair", null), Star("Bus pass", Axis(0), "another-map-model")];
        var map = await new SimilaritySuggestions(0.68, 3, 0.88, 0.73) { MapModel = EmbeddingModels.BgeSmall }.Suggest(stars, default);
        Assert.Equal(["similar_to: Garden plan + Seed order"], Pairs(map, stars));
        Assert.All(map, l => Assert.Equal(("local-bge-small", "similarity:local-bge-small"), (l.Method, l.Locator)));
        // Without it the map vectors are never read: every pair of live vectors, as before.
        var live = await new SimilaritySuggestions(0.6, 4, 0.8, 0.45).Suggest(stars, default);
        Assert.Equal(10, live.Count);
        Assert.All(live, l => Assert.Equal(SimilaritySuggestions.Method, l.Method));
        var plain = stars.Select(s => s with { MapEmbedding = null, MapEmbeddingModel = null }).ToArray();
        Assert.Equal(Pairs(live, stars), Pairs(await new SimilaritySuggestions(0.6, 4, 0.8, 0.45).Suggest(plain, default), plain));
        // Copies too: two files of one title are copies only when the map model finds them near identical.
        DiscoveryRecord[] files = [Star("Budget", Axis(2)) with { Kind = "document" }, Star("Copy of Budget", Axis(3)) with { Kind = "document" }];
        Assert.Empty(await new SimilaritySuggestions(0.68, 3, 0.88, 0.73) { MapModel = EmbeddingModels.BgeSmall }.Suggest(files, default));
        Assert.Equal(["copy_of: Budget + Copy of Budget"], Pairs(await new SimilaritySuggestions(0.6, 3, 0.8, 0.45).Suggest(files, default), files));
    }

    [Fact] public void A_map_vector_needs_its_tag_a_live_vector_and_bounds_and_is_never_written_without_one()
    {
        static void Check(DiscoveryRecord r) => RelationshipDiscovery.Validate(new([r]));
        Check(Star("Garden plan", Axis(0)));
        Check(Star("Garden plan", null));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", Axis(0)) with { MapEmbeddingModel = null }));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", null) with { MapEmbeddingModel = EmbeddingModels.BgeSmall.Id }));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", Axis(0)) with { Embedding = null, EmbeddingModel = null }));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", [])));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", [float.NaN])));
        Assert.Throws<ArgumentException>(() => Check(Star("Garden plan", Axis(0), new string('m', 201))));
        // Without one the stored snapshot is exactly as before: no key is written.
        var json = JsonSerializer.Serialize(Star("Garden plan", null));
        Assert.DoesNotContain("MapEmbedding", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<DiscoveryRecord>(json)));
        var with = JsonSerializer.Deserialize<DiscoveryRecord>(JsonSerializer.Serialize(Star("Garden plan", Axis(0))))!;
        Assert.Equal((EmbeddingModels.BgeSmall.Id, 1f), (with.MapEmbeddingModel, with.MapEmbedding![0]));
    }

    [Fact] public void A_record_is_read_again_for_a_missing_map_vector_only_while_a_map_model_is_on()
    {
        var previous = Star("Garden plan", null);
        var pass = new SourcePass(new Dictionary<ResourceKey, DiscoveryRecord>(), (_, _, _, _) => null, 10, 10, EmbeddingModels.MiniLmId);
        Assert.False(pass.Outdated(previous));
        Assert.True((pass with { MapModel = EmbeddingModels.BgeSmall.Id }).Outdated(previous));
        Assert.False((pass with { MapModel = EmbeddingModels.BgeSmall.Id }).Outdated(Star("Garden plan", Axis(0))));
        Assert.True((pass with { MapModel = "another-map-model" }).Outdated(Star("Garden plan", Axis(0))));
        // Embeddings off: nothing is read again for either vector.
        Assert.False((pass with { Model = null, MapModel = EmbeddingModels.BgeSmall.Id }).Outdated(previous));
    }

    [Fact] public async Task A_pass_makes_each_map_vector_once_keeps_it_while_unchanged_and_lists_none_without_the_model()
    {
        var notes = new EmbeddedNotes("tagged-notes", "Garden plan", "Seed order");
        var files = new KeptFiles("tagged-files", "Garden budget");
        int live = 0, mapped = 0;
        Func<string, string, float[]?>? throwing = null;
        CatalogRelationshipSource Source(IReadOnlyList<DiscoveryRecord> stored, string? mapModel)
        {
            var source = new CatalogRelationshipSource(Map(Settings("model: bge-small")), new AdapterRegistry([notes, files]),
                (_, _) => Task.FromResult(new CatalogInventory([], [])), (_, _) => { live++; return EmbeddingModelTests.Same(); }, EmbeddingModels.MiniLmId)
            {
                MapEmbed = mapModel is null ? null : throwing ?? ((_, _) => { mapped++; return Axis(0); }),
                MapEmbedModel = mapModel ?? EmbeddingModels.BgeSmall.Id,
            };
            source.Prime(stored);
            return source;
        }
        async Task<(IReadOnlyList<DiscoveryRecord> Records, IReadOnlyList<string> Notes)> Pass(IReadOnlyList<DiscoveryRecord> stored, string? mapModel)
        {
            var source = Source(stored, mapModel);
            var batch = await source.Fetch(Principal, default);
            RelationshipDiscovery.Validate(batch);
            return ([.. batch.Records.Where(r => r.Key.Provider.StartsWith("tagged-", StringComparison.Ordinal))], source.Notes);
        }
        var bge = EmbeddingModels.BgeSmall.Id;

        var (first, _) = await Pass([], bge);
        Assert.Equal((3, 3), (live, mapped));
        Assert.All(first, r => Assert.Equal((EmbeddingModels.MiniLmId, bge, 1f), (r.EmbeddingModel, r.MapEmbeddingModel, r.MapEmbedding![0])));
        // Unchanged content and the same map model: both vectors are reused, nothing is embedded.
        (live, mapped) = (0, 0);
        var (second, _) = await Pass(first, bge);
        Assert.Equal((0, 0), (live, mapped));
        Assert.All(second, r => Assert.Same(first.Single(f => f.Key == r.Key).MapEmbedding, r.MapEmbedding));
        // The map model not loaded: no map vector is listed, so the stored snapshots lose theirs and are as without the key.
        var (fallback, _) = await Pass(second, null);
        Assert.Equal((0, 0), (live, mapped));
        Assert.All(fallback, r => Assert.Equal(((float[]?)null, (string?)null, EmbeddingModels.MiniLmId), (r.MapEmbedding, r.MapEmbeddingModel, r.EmbeddingModel)));
        Assert.All(fallback, r => Assert.DoesNotContain("MapEmbedding", JsonSerializer.Serialize(r), StringComparison.Ordinal));
        // Loaded again, each map vector is made once more from the same text; the live vectors stay as they are (the file is
        // read again for its map vector: Outdated).
        var (again, _) = await Pass(fallback, bge);
        Assert.Equal((0, 3), (live, mapped));
        Assert.All(again, r => Assert.Equal(bge, r.MapEmbeddingModel));
        // Another map model makes its own; one never stands in for another's.
        mapped = 0;
        var (other, _) = await Pass(again, "another-map-model");
        Assert.Equal((0, 3), (live, mapped));
        Assert.All(other, r => Assert.Equal("another-map-model", r.MapEmbeddingModel));
        // A file left for a later pass keeps its live vector and has no map vector meanwhile: on the map, off its links.
        files.Rereads = false;
        mapped = 0;
        var (waiting, _) = await Pass(fallback, bge);
        Assert.Equal(2, mapped);
        Assert.Null(waiting.Single(r => r.Title == "Garden budget").MapEmbedding);
        Assert.NotNull(waiting.Single(r => r.Title == "Garden budget").Embedding);
        // A map vector that cannot be made leaves the star without one, counted in the notes; its live vector stays.
        files.Rereads = true;
        throwing = (_, _) => throw new ArgumentException("DEMO: too long");
        var (failed, said) = await Pass([], bge);
        Assert.All(failed, r => Assert.Equal(((float[]?)null, EmbeddingModels.MiniLmId), (r.MapEmbedding, r.EmbeddingModel)));
        Assert.Contains("3 records without a map model vector this pass, so not compared on the map", said);
    }

    [Fact] public async Task The_engine_loads_the_map_model_from_its_folder_beside_the_live_one_and_never_downloads_into_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-map-models-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "model_quantized.onnx"), "DEMO: not a model");
            await File.WriteAllTextAsync(Path.Combine(folder, "vocab.txt"), "[PAD]\n[UNK]\n");
            var data = Path.Combine(folder, "data");
            using var on = new Embeddings();
            await on.Start(Settings("model: bge-small"), data, default, _ => folder);
            Assert.Equal((EmbeddingModels.BgeSmall, "unverified", (LocalEmbeddings?)null), (on.Map!.Spec, on.Map.Status, on.Map.Model));
            Assert.False(Directory.Exists(data));
            using var absent = new Embeddings();
            await absent.Start(Settings(), data, default, _ => folder);
            Assert.Null(absent.Map);
            using var off = new Embeddings();
            await off.Start(Settings("model: bge-small", "adapters:\n  embeddings:\n    enabled: false\n"), data, default, _ => folder);
            Assert.Null(off.Map);
            using var noLinks = new Embeddings();
            await noLinks.Start(Settings("threshold: null\nmodel: bge-small"), data, default, _ => folder);
            Assert.Null(noLinks.Map);
            // The shadow and the map naming one model share its holder, so it is loaded once.
            using var both = new Embeddings();
            await both.Start(Settings("model: bge-small", "adapters:\n  embeddings:\n    enabled: true\n    shadow: bge-small\n"), data, default, _ => folder);
            Assert.Same(both.Shadow, both.Map);
            Assert.False(Directory.Exists(data));
        }
        finally { Directory.Delete(folder, true); }
    }

    // A database nobody answers at: the counts stay unread.
    static Database Nowhere() => new(NpgsqlDataSource.Create("Host=example.invalid;Database=none;Timeout=1"));

    [Fact] public async Task The_model_status_lists_the_map_model_only_when_the_key_names_one()
    {
        var before = await new ModelStatus(Nowhere(), Settings(), new Embeddings(), AdapterRegistry.Empty).Models(default);
        Assert.DoesNotContain(before, m => (string?)m!["role"] == "map");
        var models = await new ModelStatus(Nowhere(), Settings("model: bge-small"), new Embeddings(), AdapterRegistry.Empty).Models(default);
        var map = Assert.Single(models, m => (string?)m!["role"] == "map")!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            {"job":"embeddings","role":"map","name":"bge-small","id":"{{EmbeddingModels.BgeSmall.Id}}","state":"starting","index":null,"failure":null}
            """), map));
        Assert.Equal(["live", "map", "live"], models.Select(m => (string?)m!["role"]));
        var off = await new ModelStatus(Nowhere(), Settings("threshold: null\nmodel: bge-small"), new Embeddings(), AdapterRegistry.Empty).Models(default);
        Assert.Equal("off", (string?)off.Single(m => (string?)m!["role"] == "map")!["state"]);
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-map-status-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        try
        {
            using var embeddings = new Embeddings();
            Assert.False(embeddings.TryLoadMap(EmbeddingModels.BgeSmall, folder));
            var unverified = await new ModelStatus(Nowhere(), Settings("model: bge-small"), embeddings, AdapterRegistry.Empty).Models(default);
            Assert.Equal("unverified", (string?)unverified.Single(m => (string?)m!["role"] == "map")!["state"]);
        }
        finally { Directory.Delete(folder, true); }
    }
}

// Against a real database: the map vector is stored beside the live one, read by the map's links alone, never served,
// and a map model that is not loaded leaves the map exactly as without the key, said in the rebuild's notes.
public sealed class MapModelDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task The_map_vector_is_stored_read_by_the_links_alone_and_a_missing_model_falls_back()
    {
        var notes = new EmbeddedNotes("tagged-notes", "Garden plan", "Seed order");
        var files = new KeptFiles("tagged-files", "Garden budget");
        var config = MapModelTests.Settings("model: bge-small");
        TestSettings.Use();
        var registry = new AdapterRegistry([notes, files]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), "rhythm-graph-" + Guid.NewGuid().ToString("N")[..10]);
        var settings = config;
        string? mapStatus = "on";
        // As Engine.Host builds it: the map model's vectors only while the key names one and it is on. The live model finds
        // every star alike; the map model only the two notes.
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(settings, graph, state),
            c => new CatalogRelationshipSource(c, registry, (_, _) => Task.FromResult(new CatalogInventory([], [])),
                (_, _) => EmbeddingModelTests.Same(), EmbeddingModels.MiniLmId)
            {
                MapEmbed = c.Map is not null && mapStatus == "on" ? (title, _) => MapModelTests.Axis(title == "Garden budget" ? 1 : 0) : null,
                MapEmbedModel = c.Map?.Model.Id,
                MapStatus = c.Map is null ? null : mapStatus,
            }, registry);
        async Task<(IReadOnlyList<DiscoveryRecord> Records, string[] Links, string[] Methods)> Map()
        {
            var view = (await host.Readable(graph, default)).View;
            var mine = view.Records.Where(r => r.Key.Provider.StartsWith("tagged-", StringComparison.Ordinal)).ToArray();
            string Title(ResourceKey k) => mine.Single(r => r.Key == k).Title;
            var links = view.Links.Where(l => l.Type is SimilaritySuggestions.Type or SimilaritySuggestions.CopyType).ToArray();
            return (mine, [.. links.Select(l => string.Join(" + ", new[] { Title(l.Source), Title(l.Target) }.Order())).Order()],
                [.. links.Select(l => l.Method).Distinct().Order()]);
        }
        async Task<string[]> Stored()
        {
            await using var cmd = fixture.Source.CreateCommand("""
                SELECT coalesce(relationship_snapshot->>'MapEmbeddingModel','none') || ' ' || (relationship_snapshot ? 'MapEmbedding')::text
                FROM resources WHERE provider LIKE 'tagged-%' AND relationship_snapshot IS NOT NULL ORDER BY 1
                """);
            var rows = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return [.. rows];
        }
        string[] all = ["Garden budget + Garden plan", "Garden budget + Seed order", "Garden plan + Seed order"];
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            var (records, links, methods) = await Map();
            Assert.Equal(["Garden plan + Seed order"], links);
            Assert.Equal(["local-bge-small"], methods);
            Assert.False(host.MapFellBack);
            Assert.DoesNotContain(host.Notes, n => n.Contains("graph.similarity.model", StringComparison.Ordinal));
            // Stored beside the live vector and tagged, never served: a read has neither vector nor the map's tag.
            Assert.Equal(Enumerable.Repeat($"{EmbeddingModels.BgeSmall.Id} true", 3), await Stored());
            Assert.All(records, r => Assert.Equal(((float[]?)null, (float[]?)null, (string?)null, EmbeddingModels.MiniLmId),
                (r.Embedding, r.MapEmbedding, r.MapEmbeddingModel, r.EmbeddingModel)));

            // The map model's files missing: the live model's vectors and graph.similarity's numbers, as without the key,
            // said in the notes; the stored map vectors go with the pass.
            mapStatus = "download-failed";
            Assert.Equal("complete", await host.Refresh(false, default));
            (_, links, methods) = await Map();
            Assert.Equal(all, links);
            Assert.Equal([SimilaritySuggestions.Method], methods);
            Assert.True(host.MapFellBack);
            Assert.Contains("graph.similarity.model bge-small is download-failed: the similar links used the live embeddings model's vectors "
                + "and graph.similarity's thresholds (docs/models.md)", host.Notes);
            Assert.Equal(Enumerable.Repeat("none false", 3), await Stored());

            // Back on: the map vectors are made again and its links return.
            mapStatus = "on";
            Assert.Equal("complete", await host.Refresh(false, default));
            (_, links, _) = await Map();
            Assert.Equal(["Garden plan + Seed order"], links);

            // The key removed: exactly as before it, no map vector stored and nothing noted.
            settings = MapModelTests.Settings();
            Assert.Equal("complete", await host.Refresh(false, default));
            (_, links, methods) = await Map();
            Assert.Equal(all, links);
            Assert.Equal([SimilaritySuggestions.Method], methods);
            Assert.False(host.MapFellBack);
            Assert.Equal(Enumerable.Repeat("none false", 3), await Stored());
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }
}
