using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Adapters.Drive;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The drive and second-brain adapters through the engine's own paths, against the in-process fakes: a map refresh, a
// memory's text when it opens, a dashboard's Sheet feed, and a declared Sheet write with its history and Undo.
[Collection(SharedStateCollection.Name)]
public sealed class DriveMemoryDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IDisposable
{
    readonly TestSecrets secrets = new TestSecrets().Drive().With("second-brain_token", FakeBrain.Token);
    public void Dispose() => secrets.Dispose();

    [PostgresFact] public async Task Drive_and_Second_Brain_stars_a_Sheet_feed_and_a_write_with_Undo()
    {
        var config = TestSettings.Use();
        var google = new FakeGoogle();
        var brain = new FakeBrain();
        var drive = new DriveAdapter(google.Handler);
        var memories = new SecondBrainAdapter(brain.Handler);
        var archive = new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"));
        drive.Configure(new(new("drive", new Dictionary<string, object?> { ["enabled"] = "true", ["edits"] = "true", ["roots"] = new List<object> { "root-folder" } }),
            config, fixture.Db, archive));
        memories.Configure(new(new("second-brain", new Dictionary<string, object?> { ["enabled"] = "true", ["url"] = "https://brain.example.invalid/", ["owner"] = "alex" }),
            config, fixture.Db, archive));
        var registry = new AdapterRegistry([drive, memories]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));

        // Stars from both sources, Drive folders and Second Brain projects as structure, and links across the two.
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var page = await host.Read(app, null, null, default);
        var nodes = page.nodes.ToDictionary(n => n.id);
        const string Budget = "drive:drive:budget", Plan = "drive:drive:plan", Walks = "second-brain:second-brain:00000000-0000-4000-8000-000000000001";
        Assert.Equal(("document", "Budget 2026", "drive"), (nodes[Budget].type, nodes[Budget].title, nodes[Budget].provenance.sourceId));
        Assert.Equal("https://docs.google.com/spreadsheets/d/budget/edit?usp=drivesdk", nodes[Budget].resource.sourceUrl);
        Assert.Equal("theme", nodes["drive:drive:root-folder"].type);
        Assert.Equal(("memory", "alex"), (nodes[Walks].type, nodes[Walks].resource.owner));
        Assert.Equal("project", nodes["second-brain:second-brain:project%3Apersonal%3Afitness"].type);
        Assert.Contains(page.edges, e => e.source == Walks && e.target == Plan);
        Assert.Contains(page.edges, e => e.source == Plan && e.target == Budget);
        Assert.Contains(page.edges, e => e.source == Budget && e.target == "drive:drive:root-folder" && e.type == "in_folder");
        Assert.All(page.nodes, n => Assert.Null(n.resource.content));
        // Opening a memory reads its text from the service, for this answer only.
        var opened = await host.Read(app, null, null, default, new GraphQuery("content", Walks));
        Assert.StartsWith("Morning walks by the canal", Assert.Single(opened.nodes).resource.content);
        brain.Down = true;
        Assert.Contains((await host.Read(app, null, null, default, new GraphQuery("content", Walks))).issues, i => i.reason.Contains("unavailable"));

        // A dashboard over the Sheet: its feed holds the rows and the revision, and its declared write changes cells.
        var harness = await principals.Create("test-assistant", "harness", null, Path.Combine(state, "harness"), default);
        var store = new DashboardStore(fixture.Source);
        var draft = new DashboardDraft("Budget", "", "<p>Budget</p>", [new("budget", "contents", Budget)], "First version.",
            [new("cells", "sheet", Star: Budget, Range: "'Budget'!B2:C10")]);
        Assert.Null(draft.Problem());
        var (id, version) = await store.Save(harness, null, null, draft, [new(Budget, "Budget 2026")], "First version.", default);
        var feeds = new DashboardFeeds(db, host, registry);
        var feed = (await feeds.Read(app, draft.Feeds, false, default))["feeds"]!["budget"]!;
        Assert.Equal(("ok", "sheet", "7"), ((string?)feed["status"], (string?)feed["format"], (string?)feed["revision"]));
        Assert.Equal(1000, (int)feed["sheets"]![0]!["rows"]![1]![1]!);
        var writer = new DashboardWriter(host, store, registry);
        var change = JsonSerializer.SerializeToElement(new { revision = "7", cells = "B2", values = new[] { new object[] { 1100 } } });
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => writer.Write(app, id, version, "cells", change, default)); // edits are off
        await store.AllowEdits(app, id, version, true, default);
        using var written = JsonDocument.Parse(await writer.Write(app, id, version, "cells", change, default));
        Assert.Equal(("sheet", "Set B2 in Budget 2026 › Budget"), (written.RootElement.GetProperty("kind").GetString(), written.RootElement.GetProperty("summary").GetString()));
        Assert.Equal("1100", google.Sheet[1][1]!.ToJsonString());
        // The page read the old revision: a second write with it is refused and nothing changes.
        Assert.Contains("changed after the dashboard read it",
            (await Assert.ThrowsAsync<DashboardWriteRefused>(() => writer.Write(app, id, version, "cells", change, default))).Message);
        using var undone = JsonDocument.Parse(await writer.Undo(app, written.RootElement.GetProperty("id").GetGuid(), default));
        Assert.NotEqual(JsonValueKind.Null, undone.RootElement.GetProperty("undoneAt").ValueKind);
        Assert.Equal("1000", google.Sheet[1][1]!.ToJsonString());
        using var history = JsonDocument.Parse((await store.Writes(app, id, null, null, default))!);
        Assert.Equal("budget", Assert.Single(history.RootElement.EnumerateArray()).GetProperty("target").GetProperty("file").GetString());
        // A file whose title is longer than the history keeps: the change it made is still recorded, its summary shortened.
        using var recorded = JsonDocument.Parse(await store.Record(app, id, version, "cells", new JsonObject { ["tab"] = "Budget", ["range"] = "'Budget'!B2" },
            $"Set B2 in {new string('x', 400)} › Budget", JsonValue.Create(1000), JsonValue.Create(1100)!, default));
        Assert.Equal(DashboardStore.MaxSummary, recorded.RootElement.GetProperty("summary").GetString()!.Length);
        Directory.Delete(state, true);
    }
}
