using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// SB-P2-10: the explorer's combined search, app_graph's recall operation behind engine.recall. Each result is shown as
// the star that stands for it on the reader's map; a result with none is counted, never named.
public sealed class RecallStarsTests
{
    static DiscoveryRecord Record(string subject, string provider, string account, string external) =>
        new(new(subject, provider, account, external), subject == "household" ? "shared" : "private", "document", "DEMO", null, "1", null,
            DateTimeOffset.UtcNow, []);
    static JsonElement Row(string subject, string provider, string external, string metadata = "{}") => JsonSerializer.SerializeToElement(
        new Dictionary<string, object?> { ["subject_id"] = subject, ["provider"] = provider, ["external_id"] = external, ["metadata"] = metadata });

    [Fact] public void A_result_stands_as_its_map_row_its_document_star_or_the_original_a_source_shows()
    {
        var note = Record("household", "notes", "notes", "boiler.md");
        var saved = Record("household", CatalogStars.Provider, "documents", "household:quote.txt");
        var drive = Record("alex", "drive", "drive", "file-123");
        var index = new StarIndex([note, saved, drive]);
        // The map's own row for an adapter's record: its key is in its catalog id.
        Assert.Equal(note.Key.GraphId, index.Of(Row("household", "notes", note.Key.CatalogId)));
        // A record saved in Rhythm: the catalog's document star.
        Assert.Equal(saved.Key.GraphId, index.Of(Row("household", "documents", "quote.txt")));
        // A saved copy the catalog leaves to its original's star: by its external id, or the sourceFileId it names.
        Assert.Equal(drive.Key.GraphId, index.Of(Row("household", "documents", "file-123")));
        Assert.Equal(drive.Key.GraphId, index.Of(Row("household", "documents", "copy-1", """{"sourceFileId":"file-123"}""")));
        // A native memory's own row: its star (its graph id is the memory's id alone, whatever its subject, SB-P3-11), and
        // never another source's star that shares its id.
        var memory = Guid.NewGuid();
        var star = MemoryStars.Key("household", memory);
        var withMemory = new StarIndex([Record("household", MemoryStars.Provider, MemoryStars.Account, memory.ToString()),
            Record("household", "notes", "notes", memory.ToString())]);
        Assert.Equal(star.GraphId, withMemory.Of(Row("household", "memory", memory.ToString())));
        Assert.Null(new StarIndex([Record("household", "notes", "notes", memory.ToString())]).Of(Row("household", "memory", memory.ToString())));
        // None: a star that is not in this reader's view, a malformed catalog id, a native memory with no star.
        Assert.Null(index.Of(Row("household", "notes", new ResourceKey("household", "notes", "notes", "elsewhere.md").CatalogId)));
        Assert.Null(index.Of(Row("household", "notes", "relationship-engine:[broken")));
        Assert.Null(index.Of(Row("household", "memory", Guid.NewGuid().ToString())));
        // A data series: the data star of its first metric this map shows, its scope in the key as the catalog writes it.
        var catalog = RelationshipCatalog.For(TestSettings.Use());
        var glucose = Record("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:glucose");
        var withData = new StarIndex([glucose]);
        Assert.Equal(glucose.Key.GraphId, withData.Data("alex", "cgm", "private", ["glucose", "weight"], catalog));
        Assert.Equal(glucose.Key.GraphId, withData.Data("alex", "cgm", "private", ["weight", "glucose"], catalog));
        Assert.Null(withData.Data("alex", "cgm", "shared", ["glucose"], catalog));
        Assert.Null(withData.Data("sam", "cgm", "private", ["glucose"], catalog));
    }

    [Fact] public void App_graph_takes_the_recall_operation_only_with_engine_recall_on()
    {
        TestSettings.Use();
        JsonElement Call(string args) => JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"tools/call\",\"params\":{\"name\":\"app_graph\",\"arguments\":" + args + "}}").RootElement;
        const string Asked = """{"operation":"recall","value":"what keeps the house warm?"}""";
        Assert.False(AppEndpoint.Allowed(Call(Asked)));
        Assert.True(AppEndpoint.Allowed(Call(Asked), recall: true));
        // The operations it always took, either way.
        Assert.True(AppEndpoint.Allowed(Call("""{"operation":"search","value":"walk"}"""), recall: true));
        Assert.True(AppEndpoint.Allowed(Call("""{"operation":"search","value":"walk"}""")));
        Assert.False(AppEndpoint.Allowed(Call("""{"operation":"remember","value":"walk"}"""), recall: true));
    }
}

// Against a real database, through the app endpoint as the app's server calls it.
public sealed class RecallStarsDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The listing reads whether glucose has data, so it needs a database.
    [PostgresFact] public async Task The_listing_names_recall_only_with_it_on_and_is_unchanged_without()
    {
        var config = TestSettings.Use();
        var health = new AppHealth(fixture.Db, config);
        JsonElement Graph(object[] tools) => JsonSerializer.SerializeToElement(tools).EnumerateArray().Single(t => t.GetProperty("name").GetString() == AppEndpoint.Graph);
        var off = Graph(await AppEndpoint.Tools(health, Guid.NewGuid(), default));
        var on = Graph(await AppEndpoint.Tools(health, Guid.NewGuid(), default, recall: true));
        static string[] Operations(JsonElement tool) => [.. tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("operation")
            .GetProperty("enum").EnumerateArray().Select(o => o.GetString()!)];
        Assert.Equal(["overview", "search", "neighbors", "project", "get", "content", "data"], Operations(off));
        Assert.Equal(["overview", "search", "neighbors", "project", "get", "content", "data", "recall"], Operations(on));
        Assert.DoesNotContain("recall", off.GetProperty("description").GetString());
        Assert.StartsWith(off.GetProperty("description").GetString()!, on.GetProperty("description").GetString());
    }

    [PostgresFact] public async Task The_explorer_gets_the_stars_of_what_combined_retrieval_finds_best_first_with_why()
    {
        var config = TestSettings.Use();
        var db = RecallRows.Db(fixture.Source);
        var model = new MeaningModel();
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        // Records saved in Rhythm (the catalog's document stars), one with passages; a data series by its title's words; a
        // native memory with passages, on the map as its own star after the pass (SB-P3-11).
        var manual = await RecallRows.Add(db, fixture.Source, "Pellow lamp manual", "The Pellow lamp needs a warm white bulb.", model);
        var receipt = await RecallRows.Add(db, fixture.Source, "Pellow lamp receipt PL-3317", "Receipt PL-3317 for the Pellow lamp.");
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        using (var call = JsonDocument.Parse($$"""
            {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:pellow","source":{"externalId":"pellow","kind":"body-readings",
             "title":"Pellow lamp readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
             "measurements":[{"metric":"glucose","value":104,"unit":"mg/dL","observedAt":"{{at}}","key":"pellow-glucose"}]}
            """))
            await new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config).Measurements("cgm", call.RootElement, default);
        var memory = await MemoryRows.Memory(fixture.Source, "household", null, "DEMO: the Pellow lamp stays in the hall.");
        await Passages.Sweep(new EmbeddingBackfill(db), model, PassageSources.Of(false, false, true, []));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, AdapterRegistry.Empty, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), AdapterRegistry.Empty);
        Assert.Equal("complete", await host.Refresh(false, default));
        string Document(Guid id, string external) => new ResourceKey("household", CatalogStars.Provider, RecallRows.Provider, "household:" + external).GraphId;
        var externals = new Dictionary<Guid, string>();
        foreach (var id in new[] { manual, receipt })
            externals[id] = (string)(await MemoryRows.Scalar(fixture.Source, "SELECT external_id FROM resources WHERE id=@id", ("id", id)))!;
        var glucose = new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:glucose").GraphId;

        async Task<(int Status, JsonNode? Content)> Call(RhythmConfig settings, object arguments)
        {
            var services = new ServiceCollection().AddSingleton(db).AddSingleton(settings)
                .AddSingleton<RecallFactory>((o, log) => new Recall(db, model, PassageSources.Of(false, false, true, []), options: o, log: log)).BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "r", method = "tools/call",
                @params = new { name = "app_graph", arguments } }));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), host, new AppHealth(fixture.Db, config), AdapterRegistry.Empty);
            var answer = JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!;
            var content = answer["result"]?["structuredContent"];
            if (context.Response.StatusCode == 200) Schemas.Valid(Schemas.Folder("mcp"), "app_graph.output.schema.json", content);
            return (context.Response.StatusCode, content);
        }

        // Off (absent, as on every install whose settings predate it): refused as outside the tool's schema, and a search
        // answers as it always did, with no recall member.
        Assert.Equal(400, (await Call(config, new { operation = "recall", value = "Pellow lamp" })).Status);
        var (searched, search) = await Call(RecallSettingsYaml.On, new { operation = "search", value = "Pellow" });
        Assert.Equal(200, searched);
        Assert.Null(search!["recall"]);
        Assert.Equal(search.ToJsonString(), (await Call(config, new { operation = "search", value = "Pellow" })).Content!.ToJsonString());

        // On: the stars, best first, and why each is there.
        var (status, page) = await Call(RecallSettingsYaml.On, new { operation = "recall", value = "Pellow lamp PL-3317" });
        Assert.Equal(200, status);
        var recall = page!["recall"]!;
        var ids = recall["results"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(ids, page["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal(Document(receipt, externals[receipt]), ids[0]);
        Assert.Contains(Document(manual, externals[manual]), ids);
        Assert.Contains(glucose, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var first = recall["results"]!.AsArray()[0]!["why"]!;
        Assert.Equal((1, 2), (first["rank"]!.GetValue<int>(), first["words"]!["exact"]!.GetValue<int>()));
        Assert.True(first["imputed"]!.GetValue<bool>());
        var series = recall["results"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == glucose)!;
        Assert.Null(series["why"]!["meaning"]);
        // The native memory was found by meaning and stands as its own star.
        Assert.Contains(MemoryStars.Key("household", memory).GraphId, ids);
        // One written since the pass has no star yet: counted, never named or quoted.
        var later = await MemoryRows.Memory(fixture.Source, "household", null, "DEMO: the Pellow lamp's spare bulb is in the drawer.");
        await Passages.Sweep(new EmbeddingBackfill(db), model, PassageSources.Of(false, false, true, []));
        var again = (await Call(RecallSettingsYaml.On, new { operation = "recall", value = "Pellow lamp spare bulb drawer" })).Content!;
        Assert.True(again["recall"]!["offMap"]!.GetValue<int>() >= 1);
        Assert.DoesNotContain(later.ToString(), again.ToJsonString());
        Assert.DoesNotContain("spare bulb is in the drawer", again.ToJsonString());
        Assert.Matches("^rc1-[0-9a-f]{16}$", recall["receipt"]!.GetValue<string>());
        Assert.Empty(recall["notices"]!.AsArray());
        // A question longer than combined retrieval takes, a cursor or known ids beside it: refused.
        Assert.Equal(400, (await Call(RecallSettingsYaml.On, new { operation = "recall", value = new string('a', 801) })).Status);
        Assert.Equal(400, (await Call(RecallSettingsYaml.On, new { operation = "recall", value = "Pellow", cursor = "o:20" })).Status);

        // However long the texts, every star the explorer lists has its passage: the explorer's own budget, never the
        // recall tool's (whose first two results take 4000 characters each, which the page would cut to 300).
        for (var i = 0; i < 20; i++)
            await RecallRows.Add(db, fixture.Source, $"Quorrel ledger {i}",
                $"Quorrel ledger page {i}. " + string.Concat(Enumerable.Repeat($"Quorrel line {i} of the ledger. ", 160)));
        Assert.Equal("complete", await host.Refresh(false, default));
        var listed = (await Call(RecallSettingsYaml.On, new { operation = "recall", value = "Quorrel ledger" })).Content!["recall"]!["results"]!.AsArray();
        Assert.Equal(RecallStars.Results, listed.Count);
        Assert.All(listed, r => Assert.InRange(r!["excerpt"]!.GetValue<string>().Length, RecallStars.Excerpt - 79, RecallStars.Excerpt));
    }
}

// With scopes on (memory.md 7.2, app_graph recall): the explorer's combined search names only stars in its reader's own
// view and counts only its reader's own results. Alex's private document and data series are Alex's app session's to
// see, and neither the household view nor Sam's session learns their stars, titles or words.
[Collection(SharedStateCollection.Name)]
public sealed class RecallStarsScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task With_scopes_on_the_explorers_combined_search_serves_each_reader_only_its_scopes()
    {
        var config = TestSettings.Use();
        var s = fixture.Source;
        var db = RecallRows.Db(s);
        var model = new MeaningModel();
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var house = (await new Principals(s).SyncApp(Principals.NewToken(), default))!.Value;
            var alex = await Scoped.Principal(s, "recall-scope-alex", "app", "alex");
            var sam = await Scoped.Principal(s, "recall-scope-sam", "app", "sam");
            var plan = await RecallRows.Add(db, s, "Wendle shed plan", "The Wendle shed plan is Alex's own.", model, subject: "alex");
            var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            using (var call = JsonDocument.Parse($$"""
                {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:wendle","source":{"externalId":"wendle","kind":"body-readings",
                 "title":"Wendle shed readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
                 "measurements":[{"metric":"glucose","value":101,"unit":"mg/dL","observedAt":"{{at}}","key":"wendle-glucose"}]}
                """))
                await new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config).Measurements("cgm", call.RootElement, default);
            await Passages.Sweep(new EmbeddingBackfill(db), model, PassageSources.Of(false, false, true, []));
            var graph = await new Principals(s).System(Principals.GraphName, default);
            var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
                c => new CatalogRelationshipSource(c, AdapterRegistry.Empty, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), AdapterRegistry.Empty);
            Assert.Equal("complete", await host.Refresh(false, default));
            var external = (string)(await MemoryRows.Scalar(s, "SELECT external_id FROM resources WHERE id=@id", ("id", plan)))!;
            var document = new ResourceKey("alex", CatalogStars.Provider, RecallRows.Provider, "alex:" + external).GraphId;
            var glucose = new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:glucose").GraphId;

            async Task<JsonNode> Ask(Guid reader)
            {
                var services = new ServiceCollection().AddSingleton(db).AddSingleton(RecallSettingsYaml.On)
                    .AddSingleton<RecallFactory>((o, log) => new Recall(db, model, PassageSources.Of(false, false, true, []), options: o, log: log)).BuildServiceProvider();
                var context = new DefaultHttpContext { RequestServices = services };
                context.Request.Method = "POST";
                context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "r", method = "tools/call",
                    @params = new { name = "app_graph", arguments = new { operation = "recall", value = "Wendle shed plan readings" } } }));
                context.Response.Body = new MemoryStream();
                await AppEndpoint.Handle(context, new Caller(reader, "app", "app", null), host, new AppHealth(fixture.Db, config), AdapterRegistry.Empty);
                Assert.Equal(200, context.Response.StatusCode);
                var content = JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!;
                Schemas.Valid(Schemas.Folder("mcp"), "app_graph.output.schema.json", content);
                return content;
            }

            var mine = await Ask(alex);
            var ids = mine["recall"]!["results"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToArray();
            Assert.Contains(document, ids);
            Assert.Contains(glucose, ids);
            Assert.Contains(mine["recall"]!["results"]!.AsArray(), r => r!["excerpt"]!.GetValue<string>().Contains("Alex's own"));
            foreach (var other in new[] { house, sam })
            {
                var theirs = await Ask(other);
                // Everything but the question it echoes.
                var text = theirs["nodes"]!.ToJsonString() + theirs["edges"]?.ToJsonString() + theirs["recall"]!["results"]!.ToJsonString();
                Assert.DoesNotContain(document, text);
                Assert.DoesNotContain(glucose, text);
                Assert.DoesNotContain("Wendle shed plan", text);
                Assert.DoesNotContain("Wendle shed readings", text);
                Assert.DoesNotContain(theirs["recall"]!["results"]!.AsArray(), r => r!["excerpt"]!.GetValue<string>().Contains("Wendle"));
                // Nothing of Alex's is found for them, so nothing of Alex's is counted either.
                Assert.Equal(0, theirs["recall"]!["offMap"]!.GetValue<int>());
            }
        }
        finally
        {
            await Scoped.Mode(s, "off");
        }
    }
}
