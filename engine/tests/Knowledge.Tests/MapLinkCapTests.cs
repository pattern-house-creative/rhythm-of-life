using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// D-258, graph.similarity.maxLinks: a similar link is kept only while it is among the cap's strongest similar links of
// both its stars, the mutual rule's ranked with the rest, ties by graph id; copies are neither capped nor counted. Absent,
// nothing changes. No model files needed: the vectors here are fictional.
public sealed class MapLinkCapTests
{
    static readonly Guid Principal = Guid.NewGuid();
    static RelationshipHostConfiguration Map(RhythmConfig config) => RelationshipHostConfiguration.From(config, Principal, Path.GetTempPath());

    // A unit vector `cos` along axis 0 and the rest along its own axis: two such score cos·cos', each scores `cos` with Hub.
    static float[] Spoke(int axis, double cos) => [.. Enumerable.Range(0, 384).Select(j => j == 0 ? (float)cos : j == axis ? (float)Math.Sqrt(1 - cos * cos) : 0f)];
    static readonly float[] Hub = MapModelTests.Axis(0);
    // A unit vector sharing `weights` with others on axes of their own (one per pair) and the rest on a private axis: two
    // stars score exactly the weight their pair shares, 0 with every other.
    static float[] Shared(int own, params (int Axis, double Score)[] weights)
    {
        var v = new float[384];
        foreach (var (axis, score) in weights) v[axis] = (float)Math.Sqrt(score);
        v[own] = (float)Math.Sqrt(1 - weights.Sum(w => w.Score));
        return v;
    }
    static DiscoveryRecord Star(string title, float[] vector, string kind = "note", string? subject = null)
    {
        var r = SyntheticRelationshipSource.New("notes", kind, "notes");
        return r with { Key = subject is null ? r.Key : r.Key with { Subject = subject }, Scope = subject is null or "household" ? "shared" : "private",
            Title = title, Embedding = vector, EmbeddingModel = EmbeddingModels.MiniLmId };
    }
    static string[] Pairs(IReadOnlyList<Suggestion> links, IReadOnlyList<DiscoveryRecord> stars, string type = SimilaritySuggestions.Type) =>
        [.. links.Where(l => l.Type == type).Select(s => string.Join(" + ", new[] { s.Source, s.Target }.Select(k => stars.Single(r => r.Key == k).Title).Order())).Order()];
    static Task<IReadOnlyList<Suggestion>> Draw(IReadOnlyList<DiscoveryRecord> stars, int? cap, double threshold = 0.7, int neighbors = 3,
        double? mutual = null, IReadOnlyList<string>? scopes = null) =>
        new SimilaritySuggestions(threshold, neighbors, mutualThreshold: mutual, scopes: scopes) { MaxLinks = cap }.Suggest(stars, default);

    // A hub everything picks: eight notes each closest to it alone (to each other at most 0.83·0.82, below 0.7).
    static DiscoveryRecord[] HubAndSpokes() =>
        [Star("General how-to", Hub), .. Enumerable.Range(1, 8).Select(i => Star($"Plan {i}", Spoke(i, 0.84 - 0.01 * i)))];

    [Fact] public void Absent_means_no_cap_and_the_setting_takes_2_to_50_beside_either_map_model()
    {
        Assert.Null(Map(MapModelTests.Settings()).SimilarityMaxLinks);
        Assert.Null(new RhythmConfig.SimilaritySection().MaxLinks);
        Assert.Null(Map(MapModelTests.Settings("maxLinks: null")).SimilarityMaxLinks);
        Assert.Equal(6, Map(MapModelTests.Settings("maxLinks: 6")).SimilarityMaxLinks);
        var both = Map(MapModelTests.Settings("model: bge-small\nmaxLinks: 6"));
        Assert.Equal((6, EmbeddingModels.BgeSmall), (both.SimilarityMaxLinks, both.Map!.Model));
        both.Validate();
        foreach (var edge in new[] { 2, 50 }) Map(MapModelTests.Settings($"maxLinks: {edge}")).Validate();
        foreach (var wrong in new[] { "1", "51", "0", "-3" })
            Assert.Equal("graph.similarity is out of range", Assert.Throws<ConfigException>(() => MapModelTests.Settings("maxLinks: " + wrong)).Message);
        // With similar links off it is ignored, as the other numbers are.
        Assert.Null(Map(MapModelTests.Settings("threshold: null\nmaxLinks: 6")).SimilarityMaxLinks);
        var good = Map(MapModelTests.Settings("maxLinks: 6"));
        Assert.Throws<ArgumentException>(() => (good with { SimilarityMaxLinks = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { SimilarityMaxLinks = 51 }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { SimilarityThreshold = null, SimilarityDocumentThreshold = null, SimilarityMutualThreshold = null }).Validate());
        // A changed cap is a changed settings hash, so the next scheduled rebuild draws the map again.
        Assert.NotEqual(MapModelTests.Settings().Fingerprint, MapModelTests.Settings("maxLinks: 6").Fingerprint);
    }

    [Fact] public async Task Without_a_cap_or_with_one_no_star_reaches_the_links_are_exactly_as_before()
    {
        var stars = HubAndSpokes().Concat([Star("Seed order", Shared(20, (21, 0.8))), Star("Seed list", Shared(22, (21, 0.8))),
            Star("Bike repair", Shared(23, (24, 0.5))), Star("Bike lights", Shared(25, (24, 0.5)))]).ToArray();
        string Json(IReadOnlyList<Suggestion> links) => JsonSerializer.Serialize(links);
        foreach (var mutual in new double?[] { null, 0.45 })
        {
            var before = Json(await new SimilaritySuggestions(0.7, 3, mutualThreshold: mutual).Suggest(stars, default));
            Assert.Equal(before, Json(await Draw(stars, null, mutual: mutual)));
            Assert.Equal(before, Json(await Draw(stars, 50, mutual: mutual)));
        }
        // The hub's eight links are the most any star has: a cap of eight keeps every one, in the same order.
        Assert.Equal(Json(await Draw(stars, null, mutual: 0.45)), Json(await Draw(stars, 8, mutual: 0.45)));
        Assert.NotEqual(Json(await Draw(stars, null, mutual: 0.45)), Json(await Draw(stars, 7, mutual: 0.45)));
    }

    [Fact] public async Task A_hub_keeps_only_its_strongest_and_the_stars_it_drops_keep_no_link_to_it()
    {
        var stars = HubAndSpokes();
        Assert.Equal(8, Pairs(await Draw(stars, null), stars).Length);
        Assert.Equal(["General how-to + Plan 1", "General how-to + Plan 2", "General how-to + Plan 3"], Pairs(await Draw(stars, 3), stars));
        var six = await Draw(stars, 6);
        Assert.Equal([.. Enumerable.Range(1, 6).Select(i => $"General how-to + Plan {i}")], Pairs(six, stars));
        // Each dropped link was its other star's strongest, and only: dropped at one end, it is dropped.
        Assert.DoesNotContain(six, l => stars.Where(s => s.Title is "Plan 7" or "Plan 8").Any(s => l.Source == s.Key || l.Target == s.Key));
    }

    [Fact] public async Task A_mutual_link_is_ranked_by_its_score_with_the_rest_and_one_end_is_enough_to_drop_it()
    {
        // The two notes' only shared score, 0.2, is below the 0.3 threshold, so only the mutual rule joins them; each has two
        // stronger links of its own.
        DiscoveryRecord[] stars = [Star("Kitchen plan", Shared(10, (1, 0.4), (2, 0.35), (5, 0.2))), Star("Kitchen budget", Shared(11, (3, 0.4), (4, 0.35), (5, 0.2))),
            Star("Tiles", Shared(12, (1, 0.4))), Star("Taps", Shared(13, (2, 0.35))), Star("Oven", Shared(14, (3, 0.4))), Star("Hob", Shared(15, (4, 0.35)))];
        var all = await Draw(stars, null, threshold: 0.3, mutual: 0.15);
        Assert.Contains("Kitchen budget + Kitchen plan", Pairs(all, stars));
        Assert.Single(all, l => l.Excerpt.EndsWith("each among the other's closest", StringComparison.Ordinal));
        var three = await Draw(stars, 3, threshold: 0.3, mutual: 0.15);
        Assert.Equal(Pairs(all, stars), Pairs(three, stars));
        Assert.Single(three, l => l.Excerpt.EndsWith("each among the other's closest", StringComparison.Ordinal));
        var two = Pairs(await Draw(stars, 2, threshold: 0.3, mutual: 0.15), stars);
        Assert.Equal(["Hob + Kitchen budget", "Kitchen budget + Oven", "Kitchen plan + Taps", "Kitchen plan + Tiles"], two);

        // The kitchen budget's only link is the plan's weakest: kept by the budget's end, dropped by the plan's, so dropped,
        // and the budget loses every similar link; whichever star comes first.
        DiscoveryRecord[] lone = [stars[0], Star("Kitchen budget", Shared(11, (5, 0.2))), stars[2], stars[3]];
        foreach (var order in new DiscoveryRecord[][] { lone, [.. Enumerable.Reverse(lone)] })
        {
            Assert.Contains("Kitchen budget + Kitchen plan", Pairs(await Draw(order, null, threshold: 0.3, mutual: 0.15), order));
            Assert.Equal(["Kitchen plan + Taps", "Kitchen plan + Tiles"], Pairs(await Draw(order, 2, threshold: 0.3, mutual: 0.15), order));
        }
    }

    [Fact] public async Task Ties_are_broken_by_graph_id_so_every_rebuild_keeps_the_same_links()
    {
        DiscoveryRecord[] stars = [Star("General how-to", Hub), .. Enumerable.Range(1, 5).Select(i => Star($"Plan {i}", Spoke(i, 0.8)))];
        // Every plan scores the hub alike; the hub keeps the two of the smallest graph id.
        var expected = stars.Skip(1).OrderBy(s => s.Key.GraphId, StringComparer.Ordinal).Take(2).Select(s => "General how-to + " + s.Title).Order().ToArray();
        var random = new Random(258);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = stars.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, Pairs(await Draw(shuffled, 2), shuffled));
        }
        Assert.Equal(JsonSerializer.Serialize(await Draw(stars, 2)), JsonSerializer.Serialize(await Draw(stars, 2)));
    }

    [Fact] public async Task Copies_are_neither_capped_nor_counted()
    {
        // Three copies of one file (three copy links, two each) and two notes close to all three: each copy keeps both its
        // similar links beside its copies; each note, three alike, keeps the two copies of the smallest graph id.
        DiscoveryRecord[] stars = [Star("Budget", MapModelTests.Axis(0), "document"), Star("Copy of Budget", MapModelTests.Axis(0), "document"),
            Star("Budget (1)", MapModelTests.Axis(0), "document"), Star("Garden budget", Spoke(1, 0.8)), Star("Garden plan", Spoke(2, 0.75))];
        var all = await Draw(stars, null);
        Assert.Equal(3, Pairs(all, stars, SimilaritySuggestions.CopyType).Length);
        Assert.Equal(6, Pairs(all, stars).Length);
        var capped = await Draw(stars, 2);
        Assert.Equal(Pairs(all, stars, SimilaritySuggestions.CopyType), Pairs(capped, stars, SimilaritySuggestions.CopyType));
        var copies = stars.Take(3).OrderBy(s => s.Key.GraphId, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Garden budget", "Garden plan" }.SelectMany(n => copies.Take(2).Select(c => string.Join(" + ", new[] { c.Title, n }.Order()))).Order(),
            Pairs(capped, stars));
        // The copy of the smallest graph id keeps two copy links and two similar links under a cap of two.
        Assert.Equal(4, capped.Count(l => l.Source == copies[0].Key || l.Target == copies[0].Key));
    }

    [Fact] public async Task The_cap_applies_to_whichever_models_vectors_draw_the_links()
    {
        // By the live model the how-to is the hub; by the map model the first plan is, the two swapped.
        var layout = HubAndSpokes().Take(6).ToArray();
        var stars = layout.Select((s, i) => s with { MapEmbedding = layout[i switch { 0 => 1, 1 => 0, _ => i }].Embedding,
            MapEmbeddingModel = EmbeddingModels.BgeSmall.Id }).ToArray();
        var live = await new SimilaritySuggestions(0.7, 3) { MaxLinks = 2 }.Suggest(stars, default);
        Assert.Equal(["General how-to + Plan 1", "General how-to + Plan 2"], Pairs(live, stars));
        Assert.All(live, l => Assert.Equal(SimilaritySuggestions.Method, l.Method));
        var map = await new SimilaritySuggestions(0.7, 3) { MapModel = EmbeddingModels.BgeSmall, MaxLinks = 2 }.Suggest(stars, default);
        Assert.Equal(["General how-to + Plan 1", "Plan 1 + Plan 2"], Pairs(map, stars));
        Assert.All(map, l => Assert.Equal("local-bge-small", l.Method));
        Assert.Equal(5, Pairs(await new SimilaritySuggestions(0.7, 3) { MapModel = EmbeddingModels.BgeSmall }.Suggest(stars, default), stars).Length);
    }

    [Fact] public async Task With_scopes_on_the_cap_is_each_pair_of_scopes_own()
    {
        var scopes = RelationshipCatalog.For(TestSettings.Use()).Subjects;
        DiscoveryRecord[] stars = [Star("Family how-to", Hub, subject: "household"),
            .. Enumerable.Range(1, 3).Select(i => Star($"Family plan {i}", Spoke(i, 0.84 - 0.01 * i), subject: "household")),
            .. Enumerable.Range(4, 3).Select(i => Star($"Alex's plan {i}", Spoke(i, 0.84 - 0.01 * i), subject: "alex"))];
        // The household's own pair keeps the how-to's two strongest household links, Alex's pair with the household its two
        // strongest of Alex's: neither takes the other's places.
        Assert.Equal(["Alex's plan 4 + Family how-to", "Alex's plan 5 + Family how-to", "Family how-to + Family plan 1", "Family how-to + Family plan 2"],
            Pairs(await Draw(stars, 2, scopes: scopes), stars));
        // The household's links are the same without Alex's records.
        var family = stars.Where(s => s.Key.Subject == "household").ToArray();
        Assert.Equal(["Family how-to + Family plan 1", "Family how-to + Family plan 2"], Pairs(await Draw(family, 2, scopes: scopes), family));
        // One comparison, scopes off: one cap over all six.
        Assert.Equal(["Family how-to + Family plan 1", "Family how-to + Family plan 2"], Pairs(await Draw(stars, 2), stars));
    }
}

// Against a real database: the setting reaches the rebuild, with the map model on and off, and removing it draws the map
// as before.
public sealed class MapLinkCapDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_rebuild_keeps_each_stars_strongest_with_either_models_vectors_and_all_of_them_without_the_cap()
    {
        // Four notes alike by both models: six links uncapped, three a star. Capped at two, ties by graph id: the three of
        // the smallest graph id link to each other and the fourth to none.
        var notes = new EmbeddedNotes("capped-notes", "Garden plan", "Seed order", "Allotment rota", "Compost bins");
        TestSettings.Use();
        var registry = new AdapterRegistry([notes]);
        var settings = MapModelTests.Settings("maxLinks: 2");
        var db = new Database(fixture.Source, registry.Policies(settings));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), "rhythm-graph-" + Guid.NewGuid().ToString("N")[..10]);
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(settings, graph, state),
            c => new CatalogRelationshipSource(c, registry, (_, _) => Task.FromResult(new CatalogInventory([], [])),
                (_, _) => EmbeddingModelTests.Same(), EmbeddingModels.MiniLmId)
            {
                MapEmbed = c.Map is not null ? (_, _) => MapModelTests.Axis(0) : null,
                MapEmbedModel = c.Map?.Model.Id,
                MapStatus = c.Map is null ? null : "on",
            }, registry);
        async Task<(string[] Links, string[] Methods, DiscoveryRecord[] Stars)> Map()
        {
            var view = (await host.Readable(graph, default)).View;
            var mine = view.Records.Where(r => r.Key.Provider == "capped-notes").ToArray();
            string Title(ResourceKey k) => mine.Single(r => r.Key == k).Title;
            var links = view.Links.Where(l => l.Type == SimilaritySuggestions.Type && mine.Any(r => r.Key == l.Source)).ToArray();
            return ([.. links.Select(l => string.Join(" + ", new[] { Title(l.Source), Title(l.Target) }.Order())).Order()],
                [.. links.Select(l => l.Method).Distinct().Order()], mine);
        }
        try
        {
            foreach (var (yaml, method) in new[] { ("maxLinks: 2", SimilaritySuggestions.Method), ("model: bge-small\nmaxLinks: 2", "local-bge-small") })
            {
                settings = MapModelTests.Settings(yaml);
                Assert.Equal("complete", await host.Refresh(false, default));
                var (links, methods, stars) = await Map();
                var first = stars.OrderBy(s => s.Key.GraphId, StringComparer.Ordinal).Take(3).Select(s => s.Title).ToArray();
                Assert.Equal(new[] { (0, 1), (0, 2), (1, 2) }.Select(p => string.Join(" + ", new[] { first[p.Item1], first[p.Item2] }.Order())).Order(), links);
                Assert.Equal([method], methods);
            }
            settings = MapModelTests.Settings();
            Assert.Equal("complete", await host.Refresh(false, default));
            Assert.Equal(6, (await Map()).Links.Length);
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
    }
}
