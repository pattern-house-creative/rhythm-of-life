using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// Off-map links where they are useful (SB-P4-13; ledger RHY-14): a plugin's own links between its records, which only
// get_related_resources served, join the records' document stars as that plugin's suggestions behind graph.pluginLinks
// (off by default: off, the map is as before); and a document's page the explorer's combined search finds stands as
// the document's star, through the contains link that keeps its pages off the map. Fictional data only.
public sealed class OffMapLinkTests
{
    internal static RhythmConfig Settings(string? pluginLinks, bool tools = false) => RhythmConfig.Parse(TestSettings.Yaml
        + (pluginLinks is null ? "" : $"\ngraph: {{ pluginLinks: {pluginLinks} }}\n") + (tools ? "\nmemory: { tools: on }\n" : ""));

    static DiscoveryRecord Document(string external, params Reference[] references) =>
        new(new("alex", CatalogStars.Provider, "cgm", "alex:" + external), "private", "document", "DEMO", null, "1", null,
            DateTimeOffset.UtcNow.AddMinutes(-1), references);

    [Fact] public void A_plugins_own_link_on_a_catalog_star_is_kept_as_the_plugin_stated_it()
    {
        var rota = Document("rota-b");
        var plugin = Document("rota-a", new Reference(rota.Key, null, CatalogStars.PluginLocator + "cgm", "Linked by the cgm plugin", "follows",
            true, "suggested"));
        var link = Assert.Single(RelationshipDiscovery.Discover([plugin, rota]));
        Assert.Equal(("follows", "suggested", true, "native"), (link.Type, link.Verification, link.Directed, link.Method));
        Assert.Equal(("ingest:cgm", "Linked by the cgm plugin"), (link.Evidence[0].Locator, link.Evidence[0].Excerpt));
        // The same reference stated by the catalog itself is Rhythm's own and keeps the vocabulary (D-191): follows joins
        // two episodic memories only, so it is no link between two documents.
        var own = Document("rota-a", new Reference(rota.Key, null, "catalog:x", "", "follows", true, "suggested"));
        Assert.Empty(RelationshipDiscovery.Discover([own, rota]));
    }

    [Fact] public void Plugin_links_are_off_unless_graph_plugin_links_says_on()
    {
        Assert.False(RelationshipCatalog.For(TestSettings.Use()).PluginLinks);
        Assert.False(RelationshipCatalog.For(Settings("off")).PluginLinks);
        Assert.False(RelationshipCatalog.For(Settings("\"\"")).PluginLinks);
        Assert.True(RelationshipCatalog.For(Settings("on")).PluginLinks);
        Assert.Contains("graph.pluginLinks is off or on", Assert.Throws<ConfigException>(() => Settings("yes")).Message);
        // The settings schema takes it, off by default.
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "contracts", "rhythm.schema.json")));
        var setting = schema.RootElement.GetProperty("properties").GetProperty("graph").GetProperty("properties").GetProperty("pluginLinks");
        Assert.Equal("off", setting.GetProperty("default").GetString());
        TestSettings.Use();
    }
}

[Collection(SharedStateCollection.Name)]
public sealed class OffMapLinkDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    static string Star(string external) => new ResourceKey("alex", CatalogStars.Provider, "cgm", "alex:" + external).GraphId;

    RelationshipHost Host(RhythmConfig config, Guid graph, string state)
    {
        var registry = AdapterRegistry.Empty;
        var db = new Database(fixture.Source, registry.Policies(config));
        return new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
    }

    [PostgresFact] public async Task A_plugins_links_join_its_records_stars_as_its_suggestions_only_with_graph_plugin_links_on()
    {
        var off = TestSettings.Use();
        var on = OffMapLinkTests.Settings("on", tools: true);
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        JsonObject Record(string external, string title, params (string To, string Type)[] links) => new()
        {
            ["externalId"] = external, ["kind"] = "plan", ["title"] = title, ["text"] = $"DEMO fictional {title}", ["fetchedAt"] = Iso(now),
            ["sourceUpdatedAt"] = Iso(now),
            ["links"] = new JsonArray([.. links.Select(l => (JsonNode)new JsonObject { ["externalId"] = l.To, ["relationship"] = l.Type })]),
        };
        using (var call = JsonDocument.Parse(new JsonObject
        {
            ["provider"] = "cgm", ["subject"] = "alex", ["idempotencyKey"] = "offmap:plans",
            ["records"] = new JsonArray(Record("rota-a", "Brenlow rota for March", ("rota-b", "follows"), ("rota-c", "describes")),
                Record("rota-b", "Brenlow rota for February"), Record("rota-c", "Brenlow rota notes")),
        }.ToJsonString()))
            await new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), off).Records("cgm", call.RootElement, default);
        // As ingest keeps them: the plugin's suggestions, off the map, served only by get_related_resources.
        Assert.Equal(2L, await MemoryRows.Scalar(fixture.Source,
            "SELECT count(*) FROM relationships WHERE engine IS NULL AND evidence='ingest:cgm' AND verification='suggested'"));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Predicate<GraphEdge> Between(string a, string b) => e => new[] { e.source, e.target }.Order().SequenceEqual(new[] { Star(a), Star(b) }.Order());
        async Task<GraphEdge[]> Edges(RhythmConfig config)
        {
            var host = Host(config, graph, state);
            Assert.Equal("complete", await host.Refresh(false, default));
            return [.. (await host.Read(app, null, null, default, new GraphQuery())).edges];
        }
        try
        {
            // Off (absent, as on every install whose settings predate it): the stars are there, the plugin's links are not.
            var before = await Edges(off);
            Assert.DoesNotContain(before, Between("rota-a", "rota-b"));
            Assert.DoesNotContain(before, Between("rota-a", "rota-c"));

            // On: each joins the two stars as the plugin stated it, dashed (a suggestion), with the plugin as its evidence.
            var edges = await Edges(on);
            var follows = Assert.Single(edges, Between("rota-a", "rota-b"));
            Assert.Equal(("follows", "suggested", true, Star("rota-a")), (follows.type, follows.verification, follows.directed, follows.source));
            var evidence = Assert.Single(follows.evidence);
            Assert.Equal(("ingest:cgm", "Linked by the cgm plugin"), (evidence.locator, evidence.excerpt));
            Assert.Equal("describes", Assert.Single(edges, Between("rota-a", "rota-c")).type);
            // Through every rebuild while on.
            Assert.Single(await Edges(on), Between("rota-a", "rota-b"));

            // A person's Discover Dismiss keeps it off the map through a rebuild (SB-P4-05), as any suggestion of the map's.
            var host = Host(on, graph, state);
            var kept = await MapDismissals.Dismiss(host, new MemoryStore(fixture.Source), app, follows.id, new(Knowledge.Mcp.AppMemories.Channel), default);
            Assert.StartsWith("stars:", kept.Dismissal);
            var after = await Edges(on);
            Assert.DoesNotContain(after, Between("rota-a", "rota-b"));
            Assert.Single(after, Between("rota-a", "rota-c"));

            // Off again: the next rebuild retires them, and the map is as it was.
            Assert.DoesNotContain(await Edges(off), Between("rota-a", "rota-c"));
            Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source,
                "SELECT count(*) FROM relationships WHERE engine='cross-source-v1' AND lifecycle='active' AND type IN ('follows','describes')"));
        }
        finally
        {
            try { Directory.Delete(state, true); } catch (IOException) { }
            TestSettings.Use();
        }
    }

    // However many links the plugins hold, one pass takes at most its budget of them (CatalogStars.PluginLinkBudget), the
    // oldest first, so they never push the pass past the map's own link budget and stop every reader's map rebuilding.
    [PostgresFact] public async Task A_pass_takes_at_most_its_budget_of_plugin_links_the_oldest_first()
    {
        var s = fixture.Source;
        var ids = new List<Guid>();
        var documents = new List<CatalogDocument>();
        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            await MemoryRows.Run(s, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                VALUES(@id,'alex','cgm',@e,'plan','private',@e,'DEMO','current')
                """, ("id", id), ("e", $"budget-{i}"));
            ids.Add(id);
            documents.Add(new("alex", "cgm", "private", $"budget-{i}", $"budget-{i}", "1", null, null, 0, "DEMO"));
        }
        // Six links, made a minute apart, the first one oldest: each document links to every later one.
        var minute = 0;
        for (var i = 0; i < 4; i++)
            for (var j = i + 1; j < 4; j++)
                await MemoryRows.Run(s, """
                    INSERT INTO relationships(source_id,target_id,type,verification,evidence,lifecycle,created_at)
                    VALUES(@a,@b,'follows','suggested','ingest:cgm','active',now()-make_interval(mins=>@m))
                    """, ("a", ids[i]), ("b", ids[j]), ("m", 60 - minute++));
        await using var c = await s.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var all = new List<CatalogDocument>(documents);
        await CatalogReader.PluginLinks(c, tx, ids, all, default);
        Assert.Equal(6, all.Sum(d => d.Links?.Count ?? 0));
        var three = new List<CatalogDocument>(documents);
        await CatalogReader.PluginLinks(c, tx, ids, three, default, total: 3);
        Assert.Equal(["budget-1", "budget-2", "budget-3"], three[0].Links!.Select(l => l.Target));
        Assert.All(three.Skip(1), d => Assert.Null(d.Links));
        Assert.True(CatalogStars.PluginLinkBudget + 2000 <= RelationshipDiscovery.MaximumLinks);
    }

    // A plugin's link of a type the map gives its own meaning (the engine's similar_to and copy_of, a supersedes, a
    // document's contains, a project's or a folder's membership, a digest's drawn_from) never reaches the map, so the
    // explorer never draws or words it as Rhythm's own: only the plugin's own word (follows) is taken.
    [PostgresFact] public async Task A_plugins_link_never_takes_a_type_the_map_gives_its_own_meaning()
    {
        var s = fixture.Source;
        var ids = new List<Guid>();
        var documents = new List<CatalogDocument>();
        for (var i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid();
            await MemoryRows.Run(s, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                VALUES(@id,'alex','cgm',@e,'plan','private',@e,'DEMO','current')
                """, ("id", id), ("e", $"own-type-{i}-{id:N}"));
            ids.Add(id);
            documents.Add(new("alex", "cgm", "private", $"own-type-{i}-{id:N}", $"own-type-{i}", "1", null, null, 0, "DEMO"));
        }
        try
        {
            foreach (var type in CatalogStars.MapOwnLinkTypes.Append("follows"))
                await MemoryRows.Run(s, """
                    INSERT INTO relationships(source_id,target_id,type,verification,evidence,lifecycle)
                    VALUES(@a,@b,@t,'suggested','ingest:plans','active')
                    """, ("a", ids[0]), ("b", ids[1]), ("t", type));
            Assert.Equal(["similar_to", "copy_of", "supersedes", "contains", "in_project", "in_folder", "drawn_from"], CatalogStars.MapOwnLinkTypes);
            await using var c = await s.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await CatalogReader.PluginLinks(c, tx, ids, documents, default);
            Assert.Equal(["follows"], documents[0].Links!.Select(l => l.Type));
            Assert.Null(documents[1].Links);
        }
        finally { await MemoryRows.Run(s, "DELETE FROM resources WHERE id=ANY(@ids)", ("ids", ids.ToArray())); }
    }

    [PostgresFact] public async Task A_page_the_combined_search_finds_stands_as_its_documents_star()
    {
        var config = TestSettings.Use();
        var db = RecallRows.Db(fixture.Source);
        var manual = await RecallRows.Add(db, fixture.Source, "DEMO Hollin boiler manual", "The Hollin boiler manual has a page for each part of the boiler.");
        var page = await RecallRows.Add(db, fixture.Source, "DEMO Hollin boiler manual, page 2", "The Zarrowby valve sits behind the lower panel.", kind: "page");
        await RecallRows.Link(fixture.Source, manual, page, "contains");
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, AdapterRegistry.Empty, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)),
            AdapterRegistry.Empty);
        try
        {
            Assert.Equal("complete", await host.Refresh(false, default));
            var external = (string)(await MemoryRows.Scalar(fixture.Source, "SELECT external_id FROM resources WHERE id=@id", ("id", manual)))!;
            var star = new ResourceKey("household", CatalogStars.Provider, RecallRows.Provider, "household:" + external).GraphId;
            var recall = new Recall(db, null, PassageSources.Of(false, false, true, []), options: RecallStars.Budget);
            // The page has no star of its own (its document's holds its text): it stands as the document's.
            var found = (await RecallStars.Page(host, db, _ => true, recall, app, "Zarrowby valve", default)).recall!;
            Assert.Equal([star], found.results.Select(r => r.id));
            Assert.Equal(0, found.offMap);
            Assert.Contains("Zarrowby valve", found.results[0].excerpt);
            // A page whose document is not there to contain it has no star: counted, never named, as before.
            await MemoryRows.Run(fixture.Source, "DELETE FROM relationships WHERE source_id=@m AND target_id=@p", ("m", manual), ("p", page));
            var alone = (await RecallStars.Page(host, db, _ => true, recall, app, "Zarrowby valve", default)).recall!;
            Assert.Empty(alone.results);
            Assert.Equal(1, alone.offMap);
        }
        finally { try { Directory.Delete(state, true); } catch (IOException) { } }
    }
}
