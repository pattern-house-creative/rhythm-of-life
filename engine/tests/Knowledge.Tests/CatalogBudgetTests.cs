using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The catalog's star budget against a real database, in a database of its own: text records of a provider that
// holds measurements (a plugin's CSV originals) never count as documents, and more documents than the budget keep the
// most recently changed ones, with a note for the log, instead of emptying the map of every catalog star.
public sealed class CatalogBudgetTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [PostgresFact] public async Task Text_records_past_the_budget_never_take_the_data_stars_with_them()
    {
        var config = TestSettings.Use();
        var ingest = new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config);
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        // The fictional plugins of the test settings: pump writes pump-csv, cgm writes cgm.
        static string Writer(string provider) => provider == "cgm" ? "cgm" : "pump";
        async Task Measurements(JsonObject args)
        {
            using var json = JsonDocument.Parse(args.ToJsonString());
            await ingest.Measurements(Writer((string)args["provider"]!), json.RootElement, default);
        }
        async Task Records(JsonObject args)
        {
            using var json = JsonDocument.Parse(args.ToJsonString());
            await ingest.Records(Writer((string)args["provider"]!), json.RootElement, default);
        }
        // A plugin provider with readings and, beside them, a text record per export it read (as the CareLink plugin
        // keeps its CSV originals): 600 of them, more than the whole budget.
        await Measurements(new JsonObject
        {
            ["provider"] = "pump-csv", ["subject"] = "alex", ["idempotencyKey"] = "budget:readings",
            ["source"] = new JsonObject { ["externalId"] = "export-day", ["kind"] = "csv-day", ["title"] = "Fictional export day", ["fetchedAt"] = Iso(now) },
            ["measurements"] = new JsonArray(new JsonObject { ["metric"] = "sensor_glucose", ["value"] = 112, ["unit"] = "mg/dL", ["observedAt"] = Iso(now), ["key"] = "1" }),
        });
        JsonObject Text(string provider, int i, DateTimeOffset at) => new()
        {
            ["externalId"] = $"{provider}-text-{i:D4}", ["kind"] = "csv-original", ["title"] = $"Fictional text {i}", ["text"] = $"DEMO fictional body {i}",
            ["fetchedAt"] = Iso(now), ["sourceUpdatedAt"] = Iso(at),
        };
        async Task Texts(string provider, int count)
        {
            for (var start = 0; start < count; start += 100)
                await Records(new JsonObject
                {
                    ["provider"] = provider, ["subject"] = "alex", ["idempotencyKey"] = $"budget:{provider}:{start}",
                    ["records"] = new JsonArray(Enumerable.Range(start, Math.Min(100, count - start))
                        .Select(i => (JsonNode)Text(provider, i, now.AddMinutes(-count + i))).ToArray()),
                });
        }
        await Texts("pump-csv", 600);
        // A provider whose text records are documents: five more than the budget, each changed a minute after the last.
        await Texts("cgm", CatalogReader.MaxDocuments + 5);
        // One that names no modified time counts by when it was imported, so it is the newest, not the first to go.
        var untimed = Text("cgm", 9999, now);
        untimed.Remove("sourceUpdatedAt");
        await Records(new JsonObject { ["provider"] = "cgm", ["subject"] = "alex", ["idempotencyKey"] = "budget:cgm:untimed", ["records"] = new JsonArray(untimed) });

        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var registry = AdapterRegistry.Empty;
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Equal([$"more than {CatalogReader.MaxDocuments} saved documents; the map shows the {CatalogReader.MaxDocuments} most recently changed"], host.Notes);
        var stars = (await host.Readable(graph, default)).View.Records.Where(r => r.Status == "current").ToArray();
        // The data star stays; the plugin's text records are not documents.
        Assert.Contains(stars, r => r.Key == new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("pump-csv"), "alex:glucose"));
        Assert.DoesNotContain(stars, r => r.Key.Account == "pump-csv");
        // The documents are the most recently changed, the six oldest left out.
        var documents = stars.Where(r => r.Kind == "document").Select(r => r.Key.ExternalId).ToHashSet();
        Assert.Equal(CatalogReader.MaxDocuments, documents.Count);
        Assert.Contains("alex:cgm-text-9999", documents);
        Assert.Contains("alex:cgm-text-0504", documents);
        Assert.DoesNotContain("alex:cgm-text-0005", documents);
        Assert.Contains("alex:cgm-text-0006", documents);
    }
}

// With memory.scopes on (docs/dev/memory.md section 8, Budgets), in a database of its own: each of a person's measuring
// providers is read against that person's share of the catalog's series on its own. Alex's private cgm series past her
// share leave out cgm's data stars only, never those of the provider she may share with Family, and the text records of
// both providers stay out of the documents.
public sealed class CatalogScopeBudgetTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task A_provider_past_its_scopes_share_never_takes_another_providers_data_stars_with_it()
    {
        var config = TestSettings.Use();
        var catalog = RelationshipCatalog.For(config);
        var share = (catalog with { Scoped = true }).Share(2000, "alex");
        async Task<Guid> Row(string provider, string external, string kind, string content)
        {
            var id = Guid.NewGuid();
            await Scoped.Execute(fixture.Source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                VALUES(@id,'alex',@p,@e,@k,'private','DEMO · fictional export',@c,'current')
                """, ("id", id), ("p", provider), ("e", external), ("k", kind), ("c", content));
            return id;
        }
        // One more cgm series than Alex's share, and one pump-csv series; beside each, a text record of the export.
        var cgm = await Row("cgm", "cgm-day", "csv-day", "");
        await Scoped.Execute(fixture.Source, """
            INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
            SELECT @id,'fictional_series_'||lpad(i::text,4,'0'),'1',now()-interval '1 hour',current_date,1,'count','source-reported'
            FROM generate_series(1,@n) i
            """, ("id", cgm), ("n", share + 1));
        var pump = await Row("pump-csv", "pump-day", "csv-day", "");
        await Scoped.Execute(fixture.Source, """
            INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
            VALUES(@id,'sensor_glucose','1',now()-interval '1 hour',current_date,112,'mg/dL','source-reported')
            """, ("id", pump));
        await Row("cgm", "cgm-text", "csv-original", "DEMO fictional body of a cgm export");
        await Row("pump-csv", "pump-text", "csv-original", "DEMO fictional body of a pump export");

        var system = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var on = await CatalogReader.Inventory(fixture.Db, system, catalog with { Scoped = true }, default);
        Assert.Equal(["sensor_glucose"], on.Metrics.Where(m => m.Subject == "alex").Select(m => m.Metric));
        Assert.Equal(1, on.MeasuredLeftOut);
        Assert.DoesNotContain(on.Documents, d => d.Subject == "alex");
        // With scopes off every series is the install's, within the install's 2000.
        var off = await CatalogReader.Inventory(fixture.Db, system, catalog, default);
        Assert.Equal(share + 2, off.Metrics.Count(m => m.Subject == "alex"));
        Assert.Equal(0, off.MeasuredLeftOut);
        Assert.DoesNotContain(off.Documents, d => d.Subject == "alex");
    }
}
