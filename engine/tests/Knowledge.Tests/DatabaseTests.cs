using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Adapters.Csv;
using Knowledge.Adapters.Demo;
using Knowledge.Adapters.Documents;
using Knowledge.Adapters.Notes;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Npgsql;

namespace Knowledge.Tests;

// Runs only with a disposable Postgres with pgvector: RHYTHM_TEST_DB is an owner connection string to an EMPTY
// database the tests may fill (each test class creates its own schema-free database by name). For example:
//   docker run -d --rm -e POSTGRES_PASSWORD=<pw> -p 127.0.0.1:55432:5432 pgvector/pgvector:0.8.7-pg17-bookworm
//   RHYTHM_TEST_DB="Host=127.0.0.1;Port=55432;Username=postgres;Password=<pw>" dotnet test engine
// engine/test-db.sh (or test-db.ps1) does all of that: it starts one, runs every test against it and removes it.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")))
            Skip = "NOT RUN: needs a disposable Postgres with pgvector; run sh engine/test-db.sh (or set RHYTHM_TEST_DB, see DatabaseTests.cs).";
    }
}

public sealed class DatabaseFixture : IAsyncLifetime
{
    public NpgsqlDataSource Source { get; private set; } = null!;
    public Database Db { get; private set; } = null!;
    public string Name { get; } = "rhythm_test_" + Guid.NewGuid().ToString("N")[..12];
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_TEST_DB") is not { Length: > 0 } server) return;
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {Name}")) await create.ExecuteNonQueryAsync();
        Source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = Name }.ConnectionString);
        var config = TestSettings.Use();
        Db = new Database(Source, new AdapterRegistry([new DemoAdapter()]).Policies(config));
        await Db.Migrate();
        await new Principals(Source).Install(config, default);
    }
    public async Task DisposeAsync()
    {
        if (Source is null) return;
        await Source.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}

// One at a time with other tests that set which providers the map shows.
[Collection(SharedStateCollection.Name)]
public sealed class DatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [PostgresFact] public async Task Migrations_are_recorded_and_a_changed_one_stops_the_start()
    {
        Assert.True(await fixture.Db.Current(default));
        Assert.Empty(await fixture.Db.Migrate());
        await using (var cmd = fixture.Source.CreateCommand("UPDATE schema_versions SET checksum='0' WHERE version LIKE '%002_retry.sql'"))
            await cmd.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.Migrate());
        // An existing install's overlay may accept its own older checksum for a version.
        Assert.Empty(await fixture.Db.Migrate(default, new Dictionary<string, IReadOnlySet<string>>
            { ["Knowledge.Migrations.002_retry.sql"] = new HashSet<string> { "0" } }));
        await using (var restore = fixture.Source.CreateCommand("UPDATE schema_versions SET checksum=@h WHERE version LIKE '%002_retry.sql'"))
        {
            restore.Parameters.AddWithValue("h", Hashing.Hash(Database.Migrations().Single(m => m.Version.EndsWith("002_retry.sql")).Sql));
            await restore.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact] public async Task Every_definer_function_keeps_its_own_search_path_and_nobody_makes_temporary_tables()
    {
        // A SECURITY DEFINER function that takes the caller's search path runs the caller's temporary objects as its
        // owner. CREATE OR REPLACE drops the setting, so every migration that replaces one must set it again (019).
        await using (var cmd = fixture.Source.CreateCommand("""
            SELECT p.oid::regprocedure::text FROM pg_proc p
            WHERE p.pronamespace=current_schema()::regnamespace AND p.prosecdef
              AND NOT EXISTS(SELECT 1 FROM unnest(coalesce(p.proconfig,'{}'::text[])) c
                WHERE c LIKE 'search_path=pg\_catalog, %' AND c LIKE '%, pg\_temp')
            ORDER BY 1
            """))
        {
            var unpinned = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) unpinned.Add(reader.GetString(0));
            Assert.Empty(unpinned);
        }
        await using (var cmd = fixture.Source.CreateCommand("SELECT count(*) FROM pg_proc WHERE pronamespace=current_schema()::regnamespace AND prosecdef"))
            Assert.True((long)(await cmd.ExecuteScalarAsync())! > 20);
        // The engine uses no temporary tables, so its login (and anyone else's) may not make one here.
        await using (var cmd = fixture.Source.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_database d, aclexplode(coalesce(d.datacl,acldefault('d',d.datdba))) a
              WHERE d.datname=current_database() AND a.grantee=0 AND a.privilege_type='TEMPORARY')
            """))
            Assert.Equal(false, await cmd.ExecuteScalarAsync());
    }

    // A rebuild's source that keeps what it was given of the stored snapshot, and a suggester that keeps what it saw.
    sealed class PrimedSource(SyntheticRelationshipSource inner) : IRelationshipSource, IRelationshipSnapshotSource
    {
        public IReadOnlyList<DiscoveryRecord> Primed { get; private set; } = [];
        public void Prime(IReadOnlyList<DiscoveryRecord> stored) => Primed = stored;
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct) => inner.Fetch(principal, ct);
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => inner.Check(principal, key, revision, ct);
    }
    sealed class SeenSuggestions : IRelationshipSuggestions
    {
        public IReadOnlyList<DiscoveryRecord> Seen { get; private set; } = [];
        public Task<IReadOnlyList<Suggestion>> Suggest(IReadOnlyList<DiscoveryRecord> records, CancellationToken ct)
        { Seen = records; return Task.FromResult<IReadOnlyList<Suggestion>>([]); }
    }

    [PostgresFact] public async Task Map_reads_leave_each_records_embedding_out_and_a_rebuild_keeps_it()
    {
        // A vector is most of a record's stored JSON, and only a rebuild uses it: for similarity, and to reuse it while
        // the record is unchanged. Every app, harness and dashboard read loads the snapshot without it.
        var principal = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var vector = Enumerable.Range(0, 384).Select(i => (float)Math.Sin(i + 1) / 20).ToArray();
        var synthetic = new SyntheticRelationshipSource();
        var memory = SyntheticRelationshipSource.New("notes", "memory") with { Embedding = vector };
        synthetic.Records.Add(memory);
        var source = new PrimedSource(synthetic);
        var suggestions = new SeenSuggestions();
        var engine = new RelationshipEngine(fixture.Db, source, suggestions);
        await engine.Refresh(principal);
        Assert.Equal(vector, Assert.Single(suggestions.Seen, r => r.Key == memory.Key).Embedding);
        var view = await engine.View(principal, TimeSpan.FromHours(1), new HashSet<string>());
        Assert.Null(Assert.Single(view.Records, r => r.Key == memory.Key).Embedding);
        var (_, shown) = await engine.ReadSnapshot(principal, TimeSpan.FromHours(1), 300, new HashSet<string>(), _ => true, default,
            query: new GraphQuery("get", memory.Key.GraphId));
        Assert.Equal(memory.Key, Assert.Single(shown).Key);
        Assert.Null(shown[0].Embedding);
        // The next rebuild is given the stored vector, so an unchanged record keeps it without being embedded again.
        await engine.Refresh(principal);
        Assert.Equal(vector, Assert.Single(source.Primed, r => r.Key == memory.Key).Embedding);
    }

    [PostgresFact] public async Task A_data_feed_lists_its_metrics_once_per_revision_of_its_star()
    {
        // Listing a data star's metrics reads its provider's whole history, so a dashboard read every few minutes does it
        // once per revision of the star; a rebuild gives the star a new revision when its metrics change.
        var config = TestSettings.Use();
        var ingest = new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config);
        async Task Measure(string metric, string unit, double value, string key)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            await ingest.Measurements("cgm", Json($$"""
                {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:feed-{{key}}","source":{"externalId":"feed-{{key}}","kind":"body-readings",
                 "title":"Fictional body readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
                 "measurements":[{"metric":"{{metric}}","value":{{value}},"unit":"{{unit}}","observedAt":"{{at}}","key":"{{key}}"}]}
                """), default);
        }
        await Measure("weight", "kg", 71.4, "w1");
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var registry = AdapterRegistry.Empty;
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        var feeds = new DashboardFeeds(fixture.Db, host, registry);
        DashboardFeed[] body = [new("body", "data", new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:body").GraphId)];
        async Task<string[]> Metrics() => (await feeds.Read(app, body, false, default))["feeds"]!["body"]!["metrics"]!.AsArray()
            .Select(m => (string)m!["metric"]!).ToArray();
        Assert.Equal(["weight"], await Metrics());
        // A metric first ingested since the rebuild shows after the next one, as on the map.
        await Measure("stress", "score", 30, "s1");
        Assert.Equal(["weight"], await Metrics());
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Equal(["stress", "weight"], await Metrics());
        Directory.Delete(state, true);
    }

    [PostgresFact] public async Task The_tall_corner_once_called_glucose_keeps_its_card_as_main()
    {
        // As before migration 018: a card chosen for the "glucose" corner. Replayed in a transaction that is rolled back.
        await using var c = await fixture.Source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var principal = Guid.NewGuid(); var dashboard = Guid.NewGuid();
        await Database.Execute(c, tx, $"""
            ALTER TABLE ambient_cards DROP CONSTRAINT ambient_cards_corner_check;
            ALTER TABLE ambient_cards ADD CONSTRAINT ambient_cards_corner_check CHECK(corner IN ('glucose','recovery','training','changed'));
            INSERT INTO principals(id,name,token_hash) VALUES('{principal}','fictional-older','{Guid.NewGuid():N}');
            INSERT INTO dashboards(id,created_by) VALUES('{dashboard}','{principal}');
            INSERT INTO ambient_cards(corner,dashboard_id,set_by) VALUES('glucose','{dashboard}','{principal}');
            """, default);
        await Database.Execute(c, tx, Database.Migrations().Single(m => m.Version.EndsWith("018_main_corner.sql")).Sql, default);
        await using (var cmd = Database.Command(c, tx, "SELECT corner FROM ambient_cards WHERE dashboard_id=@d", ("d", dashboard)))
            Assert.Equal("main", await cmd.ExecuteScalarAsync());
        await using (var cmd = Database.Command(c, tx, "INSERT INTO ambient_cards(corner,dashboard_id,set_by) VALUES('glucose',@d,@p)", ("d", dashboard), ("p", principal)))
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        await tx.RollbackAsync();
    }

    [PostgresFact] public async Task People_become_subjects_and_the_one_app_token_rotates()
    {
        await using (var cmd = fixture.Source.CreateCommand("SELECT array_agg(id ORDER BY id) FROM subjects"))
            Assert.Equal(["alex", "fixture", "household", "sam"], (string[])(await cmd.ExecuteScalarAsync())!);
        var principals = new Principals(fixture.Source);
        var first = await principals.SyncApp(Principals.NewToken(), default);
        Assert.Equal(first, await principals.SyncApp(null, default) ?? first);
        var token = Principals.NewToken();
        var second = await principals.SyncApp(token, default);
        Assert.NotEqual(first, second);
        Assert.Equal("app", (await fixture.Db.Authenticate(token, default))!.Role);
        await using var count = fixture.Source.CreateCommand("SELECT count(*) FROM principals WHERE role='app' AND enabled");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        // A restore's quarantine disables every token; the unchanged app token file brings its own principal back.
        await using (var quarantine = fixture.Source.CreateCommand("UPDATE principals SET enabled=false WHERE role='app'"))
            await quarantine.ExecuteNonQueryAsync();
        Assert.Null(await fixture.Db.Authenticate(token, default));
        Assert.Equal(second, await principals.SyncApp(token, default));
        Assert.Equal("app", (await fixture.Db.Authenticate(token, default))!.Role);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact] public async Task The_app_token_from_its_file_works_again_after_a_restore_quarantine()
    {
        var principals = new Principals(fixture.Source);
        var token = Principals.NewToken();
        var first = await principals.SyncApp(token, default);
        // restore-quarantine.sql disables every token; the token hash stays, and is unique.
        await using (var quarantine = fixture.Source.CreateCommand("UPDATE principals SET enabled=false WHERE role='app'")) await quarantine.ExecuteNonQueryAsync();
        Assert.Equal(first, await principals.SyncApp(token, default));
        Assert.Equal("app", (await fixture.Db.Authenticate(token, default))!.Role);
        await using var count = fixture.Source.CreateCommand("SELECT count(*) FROM principals WHERE role='app' AND enabled");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact] public async Task Roles_gate_the_stores_and_the_household_shares_everything()
    {
        var principals = new Principals(fixture.Source);
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var harness = await principals.Create("test-assistant", "harness", null, Path.Combine(folder, "harness"), default);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var store = new InsightStore(fixture.Source);
        var now = DateTimeOffset.UtcNow;
        var draft = new InsightDraft("focus", "Fictional insight", "Fictional summary", "", "", [], "weak", "", [], [], "harness-daily", now, now, null);
        var id = await store.Save(harness, draft, default);
        // The app may give feedback but not save; a harness may save but not give feedback.
        await Assert.ThrowsAsync<PostgresException>(() => store.Save(app, draft, default));
        await Assert.ThrowsAsync<PostgresException>(() => store.Record(harness, id, "interesting", null, null, default));
        Assert.Contains("\"saved\"", (await store.Record(app, id, "interesting", null, "Useful", default))!);
        Assert.Contains("Fictional insight", await store.List(app, null, null, 5, default));
        Directory.Delete(folder, true);
    }

    [PostgresFact] public async Task Ingest_is_idempotent_and_measurements_update_by_key()
    {
        var ingest = new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), TestSettings.Use());
        var call = """
            {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:test-%KEY%","source":{"externalId":"cgm-test","kind":"glucose-readings",
             "title":"Fictional readings","fetchedAt":"2026-10-28T12:00:05Z","timezone":"Europe/London"},
             "measurements":[{"metric":"glucose","value":%V%,"unit":"mg/dL","observedAt":"2026-10-28T11:55:00Z","key":"2026-10-28T11:55:00Z"},
                             {"metric":"glucose","value":118,"unit":"mg/dL","observedAt":"2026-10-28T12:00:00+01:00","key":"b"}]}
            """;
        var first = await ingest.Measurements("cgm", Json(call.Replace("%KEY%", "1").Replace("%V%", "112")), default);
        Assert.Equal(2, (int)first["inserted"]!);
        var replay = await ingest.Measurements("cgm", Json(call.Replace("%KEY%", "1").Replace("%V%", "112")), default);
        Assert.True((bool)replay["replayed"]!);
        await Assert.ThrowsAsync<Ingest.Refused>(() => ingest.Measurements("cgm", Json(call.Replace("%KEY%", "1").Replace("%V%", "130")), default));
        var changed = await ingest.Measurements("cgm", Json(call.Replace("%KEY%", "2").Replace("%V%", "130")), default);
        Assert.Equal(1, (int)changed["updated"]!); Assert.Equal(1, (int)changed["unchanged"]!);
        await using var cmd = fixture.Source.CreateCommand("SELECT local_date::text FROM measurements WHERE sample_key='b'");
        Assert.Equal("2026-10-28", await cmd.ExecuteScalarAsync());
    }

    [PostgresFact] public async Task A_plugin_archives_originals_only_for_its_archive_provider()
    {
        var ingest = new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), TestSettings.Use());
        var call = """
            {"provider":"%P%","subject":"alex","idempotencyKey":"pump:alex:%P%-1","source":{"externalId":"%P%-day","kind":"glucose-readings",
             "title":"Fictional readings","fetchedAt":"2026-10-28T12:00:05Z","timezone":"Europe/London"},
             "measurements":[{"metric":"sensor_glucose","value":121,"unit":"mg/dL","observedAt":"2026-10-28T11:55:00Z","key":"k1"}]}
            """;
        foreach (var provider in new[] { "pump-live", "pump-csv" })
            Assert.Equal(1, (int)(await ingest.Measurements("pump", Json(call.Replace("%P%", provider)), default))["inserted"]!);
        await using var cmd = fixture.Source.CreateCommand("SELECT provider, raw_path IS NOT NULL, domain FROM resources WHERE provider LIKE 'pump-%' ORDER BY provider");
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, bool, string)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)));
        Assert.Equal([("pump-csv", true, "health"), ("pump-live", false, "health")], rows);
    }

    [PostgresFact] public async Task The_demo_adapter_fills_the_map_with_labelled_fictional_stars()
    {
        var config = TestSettings.Use();
        var registry = new AdapterRegistry([new DemoAdapter()]);
        var archive = new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"));
        registry.Enabled[0].Configure(new(config.Adapter("demo"), config, fixture.Db, archive));
        var health = new Knowledge.Mcp.AppHealth(fixture.Db, config);
        var reader = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var mcp = Schemas.Folder("mcp");
        // An older build's demo wrote a fictional CGM's live feed; its reading was labelled demo, as the facade needs.
        var ingest = new Ingest(fixture.Db, archive, config);
        var now = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        using (var older = JsonDocument.Parse($$"""
            {"provider":"demo-live","subject":"{{RhythmConfig.Synthetic}}","idempotencyKey":"demo:older-glucose",
             "source":{"externalId":"demo-demo-cgm-live-feed","kind":"demo-readings","title":"DEMO · Fictional readings","fetchedAt":"{{now}}"},
             "measurements":[{"metric":"glucose","value":112,"unit":"mg/dL","observedAt":"{{now}}","key":"{{now}}"}]}
            """))
            await ingest.Measurements("demo", older.RootElement, default, ["demo-live"]);
        var latest = await health.Latest(reader, RhythmConfig.Synthetic, default);
        var row = Assert.Single(latest["rows"]!.AsArray())!;
        Assert.Equal((RhythmConfig.Synthetic, "demo-live", true), ((string?)row["subject_id"], (string?)row["provider"], (bool?)row["demo"]));
        Schemas.Valid(mcp, "app_latest_glucose.output.schema.json", latest);
        // Today's demo is everyday wellbeing with no glucose: the first sync retires that feed.
        await ((DemoAdapter)registry.Enabled[0]).Sync(new MeasurementSink(ingest, registry.Enabled[0]), default);
        Assert.Empty((await health.Latest(reader, RhythmConfig.Synthetic, default))["rows"]!.AsArray());
        Assert.Empty((await health.Recent(reader, RhythmConfig.Synthetic, true, default))["sources"]!.AsArray());
        var principals = new Principals(fixture.Source);
        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var page = await host.Read(app, null, null, default);
        Assert.Contains(page.nodes, n => n.type == "note" && n.provenance.demo && n.title.StartsWith("DEMO"));
        var data = page.nodes.Where(n => n.id.StartsWith(CatalogStars.Provider + ":data", StringComparison.Ordinal)).Select(n => n.title).ToArray();
        foreach (var kind in new[] { "Sleep", "Activity", "Heart", "Mood" }) Assert.Contains(data, t => t.Contains(kind));
        Assert.DoesNotContain(data, t => t.Contains("Glucose"));
        Assert.NotEmpty(page.edges);
        // A restore's quarantine (engine/backup/restore-quarantine.sql) revokes the map's rows; the next rebuild brings
        // every star back.
        var quarantine = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "backup", "restore-quarantine.sql");
        var revoke = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(quarantine), @"UPDATE resources [^;]*;").Value;
        await using (var cmd = fixture.Source.CreateCommand(revoke)) Assert.True(await cmd.ExecuteNonQueryAsync() > 0);
        Assert.DoesNotContain((await host.Read(app, null, null, default)).nodes, n => n.type == "note");
        Assert.Equal("complete", await host.Refresh(false, default));
        Assert.Equal(page.nodes.Count, (await host.Read(app, null, null, default)).nodes.Count);
        // The timer waits after a rebuild, except for the first automatic one by another engine build (an upgrade) or
        // with other settings.
        Assert.Equal("not_due", await host.Refresh(true, default));
        var made = Path.Combine(state, "graph-refresh.state.build");
        Assert.EndsWith(" " + config.Fingerprint, File.ReadAllText(made));
        Assert.NotEmpty(config.Fingerprint);
        File.WriteAllText(made, "an older build");
        Assert.Equal("complete", await host.Refresh(true, default));
        Assert.Equal("not_due", await host.Refresh(true, default));
        var store = new DashboardStore(fixture.Source);
        Assert.Contains("DEMO", await store.List(app, default));
        // Two demo dashboards: the week at a glance fills the tall main slot, the mood log a corner.
        var cards = JsonNode.Parse(await store.Cards(app, default))!;
        var glance = Guid.Parse((string)cards["main"]!);
        Assert.Contains("DEMO · Week at a glance", await store.List(app, default));
        Assert.Contains("data-shape=tall", (string)JsonNode.Parse((await store.Get(app, glance, null, true, default))!)!["html"]!);
        Assert.NotNull(cards["recovery"]);
        // A corner someone clears stays clear, and an unchanged seed saves no new version.
        await store.SetCard(app, "main", null, default);
        var again = new AdapterRegistry([new DemoAdapter()]);
        again.Enabled[0].Configure(new(config.Adapter("demo"), config, fixture.Db, archive));
        await ((DemoAdapter)again.Enabled[0]).Sync(new MeasurementSink(ingest, again.Enabled[0]), default);
        Assert.Null(JsonNode.Parse(await store.Cards(app, default))!["main"]);
        Assert.Equal(1, (int)JsonNode.Parse((await store.Get(app, glance, null, false, default))!)!["latest"]!);
        var content = await host.Read(app, null, null, default, new GraphQuery("content", "demo:demo:note-morning-walks"));
        Assert.Contains("canal", Assert.Single(content.nodes).resource.content);
        // Every app tool's answer matches its schema in contracts/mcp (glucose's in GlucoseTests, from fictional readings).
        Schemas.Valid(mcp, "app_graph.output.schema.json", page);
        Schemas.Valid(mcp, "app_graph.output.schema.json", content);
        var moodStar = page.nodes.First(n => CatalogStars.IsData(new(n.resource.owner, n.resource.identity.provider, n.resource.identity.account,
            n.resource.identity.externalId)) && n.title.Contains("Mood"));
        var readings = await host.Read(app, null, null, default, new GraphQuery("data", moodStar.id));
        Assert.NotEmpty(Assert.Single(readings.nodes).measurements!);
        Schemas.Valid(mcp, "app_graph.output.schema.json", readings);
        var insights = new InsightStore(fixture.Source);
        Schemas.Valid(mcp, "app_insights.output.schema.json", new JsonObject
        {
            ["insights"] = JsonNode.Parse(await insights.List(app, null, null, 20, default)),
            ["runs"] = JsonNode.Parse(await insights.Runs(app, 14, default)),
        });
        var listed = JsonNode.Parse(await store.List(app, default))!.AsArray();
        Schemas.Valid(mcp, "app_dashboards.output.schema.json", new JsonObject { ["dashboards"] = listed });
        var one = await store.Get(app, Guid.Parse((string)listed[0]!["id"]!), null, true, default);
        Schemas.Valid(mcp, "app_dashboards.output.schema.json", new JsonObject { ["dashboard"] = JsonNode.Parse(one!) });
        Schemas.Valid(mcp, "app_dashboards.output.schema.json", new JsonObject { ["cards"] = JsonNode.Parse(await store.Cards(app, default)) });
        Schemas.Valid(mcp, "app_dashboards.output.schema.json", new JsonObject { ["pins"] = JsonNode.Parse(await store.Pins(app, default)) });
        var refresh = new RefreshRequests(fixture.Source);
        Schemas.Valid(mcp, "app_request_refresh.output.schema.json", JsonNode.Parse(await refresh.Request(app, default)));
        // With the demo off, the dashboards only the demo saved and its insight nobody reacted to go; a dashboard
        // someone saved a version of stays, with its corner.
        var mood = (string)JsonNode.Parse(await store.Cards(app, default))!["recovery"]!;
        await using (var cmd = fixture.Source.CreateCommand($"UPDATE dashboard_versions SET saved_by='{graph}' WHERE dashboard_id='{mood}'"))
            await cmd.ExecuteNonQueryAsync();
        Assert.True(await DemoAdapter.Retire(fixture.Db, default) > 0);
        Assert.DoesNotContain(glance.ToString(), await store.List(app, default));
        Assert.Contains(mood, await store.List(app, default));
        Assert.Equal(mood, (string)JsonNode.Parse(await store.Cards(app, default))!["recovery"]!);
        Assert.DoesNotContain("Longer sleep", await insights.List(app, null, null, 20, default));
        Assert.Equal(0, await DemoAdapter.Retire(fixture.Db, default));
        // Its insight someone reviewed stays too, with its history and its run, though nobody gave it feedback, and so does
        // one reopened since (migration 066). With memory.tools off no row has a review or an event.
        var seeded = new AdapterRegistry([new DemoAdapter()]);
        seeded.Enabled[0].Configure(new(config.Adapter("demo"), config, fixture.Db, archive));
        await ((DemoAdapter)seeded.Enabled[0]).Sync(new MeasurementSink(ingest, seeded.Enabled[0]), default);
        var showcase = Guid.Parse((string)JsonNode.Parse(await insights.List(app, null, null, 20, default))!.AsArray().Single(i => ((string)i!["title"]!).Contains("Longer sleep"))!["id"]!);
        Task<object?> DemoRows(string table, string column) => MemoryRows.Scalar(fixture.Source, $"SELECT count(*) FROM {table} WHERE {column}=@id", ("id", showcase));
        Assert.Equal("confirmed", (await insights.Review(app, showcase, "confirm", null, new("app"), default)).Review);
        await DemoAdapter.Retire(fixture.Db, default);
        Assert.Contains("Longer sleep", await insights.List(app, null, null, 20, default));
        Assert.Equal((1L, 1L), (await DemoRows("insight_events", "insight_id"), await DemoRows("insight_runs", "insight_id")));
        Assert.Equal("proposed", (await insights.Review(app, showcase, "reopen", null, new("app"), default)).Review);
        await DemoAdapter.Retire(fixture.Db, default);
        Assert.Contains("Longer sleep", await insights.List(app, null, null, 20, default));
        Assert.Equal((2L, 1L), (await DemoRows("insight_events", "insight_id"), await DemoRows("insight_runs", "insight_id")));
    }

    [PostgresFact] public async Task Notes_csv_and_documents_become_stars_from_their_files()
    {
        var config = TestSettings.Use();
        var archive = new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive"));
        var csvText = "time,metric,value,unit,subject,source\n2026-10-20T07:05:00+01:00,weight,71.6,kg,alex,Fictional scale\n"
            + "2026-10-21T07:05:00+01:00,weight,71.4,kg,alex,Fictional scale\n2026-10-20,steps,8100,count,alex,\n2026-10-20,water_glasses,6,count,,\n";
        var import = NotesAdapterTests.Folder(("readings.csv", csvText), ("documents/Allotment rota.txt", "DEMO watering rota: Alex on Mondays, Sam on Fridays."));
        await File.WriteAllBytesAsync(Path.Combine(import, "documents", "Fictional manual.pdf"), FictionalPdf.Build("Fictional boiler manual", "Pressure between one and two bar"));
        var notes = new NotesAdapter(); var csv = new CsvAdapter(); var documents = new DocumentsAdapter();
        var registry = new AdapterRegistry([notes, csv, documents]);
        var db = new Database(fixture.Source, registry.Policies(config));
        AdapterSettings Settings(string name, string path) => new(name, new Dictionary<string, object?> { ["enabled"] = "true", ["path"] = path });
        notes.Configure(new(Settings("notes", Path.Combine(AppContext.BaseDirectory, "samples", "notes")), config, db, archive));
        csv.Configure(new(Settings("csv", import), config, db, archive));
        documents.Configure(new(Settings("documents", Path.Combine(import, "documents")), config, db, archive));
        var ingest = new Ingest(db, archive, config);
        await csv.Sync(new MeasurementSink(ingest, csv), default);
        await documents.Import(default);
        var principals = new Principals(fixture.Source);
        var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
        var uploaded = (await documents.Upload(app, "Fictional manual (copy).pdf", FictionalPdf.Build("Fictional upload", "Second page"), default))["document"]!;
        Assert.Equal("created", (string?)uploaded["status"]);
        Assert.Equal(2, (int?)uploaded["pages"]);
        Assert.Equal("unchanged", (string?)(await documents.Upload(app, "Fictional manual (copy).pdf", FictionalPdf.Build("Fictional upload", "Second page"), default))["document"]!["status"]);
        await Assert.ThrowsAsync<DocumentRefused>(() => documents.Upload(app, "notes.docx", [1, 2, 3], default));
        // What the app receives through app_upload_document, saved and refused, is the contract's shape.
        async Task<JsonNode> Added(string name, byte[] bytes)
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "u", method = "tools/call",
                @params = new { name = "app_upload_document", arguments = new { name, content = Convert.ToBase64String(bytes) } } }));
            context.Response.Body = new MemoryStream();
            await Knowledge.Mcp.AppEndpoint.Handle(context, new Caller(app, "app", "app", null), null!, new Knowledge.Mcp.AppHealth(db, config), registry);
            return JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!;
        }
        var repeated = await Added("Fictional manual (copy).pdf", FictionalPdf.Build("Fictional upload", "Second page"));
        Assert.Equal(("unchanged", (string?)uploaded["graphId"]), ((string?)repeated["document"]!["status"], (string?)repeated["document"]!["graphId"]));
        var refused = await Added("notes.docx", [1, 2, 3]);
        Assert.Equal("unsupported_type", (string?)refused["code"]);
        foreach (var answer in new[] { repeated, refused }) Schemas.Valid(Schemas.Folder("mcp"), "app_upload_document.output.schema.json", answer);

        var graph = await principals.System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        var page = await host.Read(app, null, null, default);
        // Notes: stars, solid links between them, folders and tags as projects; opening one reads the file.
        var welcome = new ResourceKey("household", "notes", "notes", "Welcome.md").GraphId;
        Assert.Contains(page.nodes, n => n.id == welcome && n.type == "note" && n.title.StartsWith("DEMO · Welcome", StringComparison.Ordinal));
        Assert.Contains(page.edges, e => e.source == welcome && e.target == new ResourceKey("household", "notes", "notes", "Weekly review.md").GraphId
            && e.verification == "verified" && e.type == "references");
        Assert.Contains(page.nodes, n => n.type == "project" && n.title == "Home");
        Assert.Contains("fictional sample", Assert.Single((await host.Read(app, null, null, default, new GraphQuery("content", welcome))).nodes).resource.content);
        // CSV: a data star per group with the generic readings; the row without a subject is the household's.
        var body = new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("csv"), "alex:body").GraphId;
        Assert.Contains(page.nodes, n => n.id == body && n.title == "Alex · Body");
        Assert.Contains(page.nodes, n => n.id == new ResourceKey("household", CatalogStars.Provider, CatalogStars.DataAccount("csv"), "household:other:shared").GraphId
            && n.title == "Household · Other CSV import data");
        var weight = Assert.Single(Assert.Single((await host.Read(app, null, null, default, new GraphQuery("data", body))).nodes).measurements!);
        Assert.Equal(("weight", "kg", 71.4), (weight.metric, weight.unit, weight.latest!.value));
        // Documents: the dropped files and the upload are document stars whose text opens in the reader.
        foreach (var (id, text) in new[] { (documents.GraphId("file:Allotment rota.txt"), "watering rota"), (documents.GraphId("file:Fictional manual.pdf"), "between one and two bar"),
            ((string)uploaded["graphId"]!, "Fictional upload") })
        {
            Assert.Contains(page.nodes, n => n.id == id && n.type == "document");
            Assert.Contains(text, Assert.Single((await host.Read(app, null, null, default, new GraphQuery("content", id))).nodes).resource.content);
        }
        Assert.Contains("Allotment", await new Retrieval(db).Search(app, "watering", 5, default));
        // Each added file keeps its original: the content answer names it, and GET /app/documents/<id> serves it as it came.
        var opened = Assert.Single((await host.Read(app, null, null, default, new GraphQuery("content", (string)uploaded["graphId"]!))).nodes);
        var original = Assert.IsType<GraphOriginal>(opened.resource.original);
        Assert.Equal(((string?)uploaded["resourceId"], "pdf", "Fictional manual (copy).pdf"), (original.id, original.format, original.name));
        Schemas.Valid(Schemas.Folder("mcp"), "app_graph.output.schema.json", new GraphPage([opened], [], null, "partial", []));
        async Task<(int Status, Microsoft.AspNetCore.Http.HttpResponse Response, byte[] Body)> Original(string id, Archive? from = null, string method = "GET")
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = method;
            context.Response.Body = new MemoryStream();
            await Knowledge.Mcp.AppFiles.Handle(context, app, Guid.Parse(id), db, from ?? archive, config);
            return (context.Response.StatusCode, context.Response, ((MemoryStream)context.Response.Body).ToArray());
        }
        var pdf = FictionalPdf.Build("Fictional upload", "Second page");
        var served = await Original(original.id);
        Assert.Equal((200, pdf.LongLength), (served.Status, original.bytes));
        Assert.Equal(pdf, served.Body);
        Assert.Equal(("application/pdf", (long?)pdf.Length, "nosniff"), (served.Response.ContentType, served.Response.ContentLength,
            served.Response.Headers.XContentTypeOptions.ToString()));
        Assert.Equal("default-src 'none'; sandbox", served.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.StartsWith("inline; filename=", served.Response.Headers.ContentDisposition.ToString());
        Assert.Contains("filename*=UTF-8''Fictional%20manual%20%28copy%29.pdf", served.Response.Headers.ContentDisposition.ToString());
        var rota = Assert.Single((await host.Read(app, null, null, default, new GraphQuery("content", documents.GraphId("file:Allotment rota.txt")))).nodes).resource.original!;
        Assert.Equal(("text", "Allotment rota.txt"), (rota.format, rota.name));
        Assert.Equal("text/plain; charset=utf-8", (await Original(rota.id)).Response.ContentType);
        Assert.Equal("pdf", Assert.Single((await host.Read(app, null, null, default, new GraphQuery("content", documents.GraphId("file:Fictional manual.pdf")))).nodes)
            .resource.original?.format);
        Assert.Equal(405, (await Original(rota.id, method: "POST")).Status);
        // Nothing else is served: an unknown id, another provider's archived original (a CSV file), a document a
        // harness saved as text (save_document keeps no file), a file that is gone or no longer matches its checksum.
        Assert.Equal(404, (await Original(Guid.NewGuid().ToString())).Status);
        await using (var csvFile = fixture.Source.CreateCommand("SELECT id FROM resources WHERE provider<>'documents' AND raw_path IS NOT NULL LIMIT 1"))
            Assert.Equal(404, (await Original(((Guid)(await csvFile.ExecuteScalarAsync())!).ToString())).Status);
        Guid copy;
        await using (var c = await fixture.Source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            copy = await db.Upsert(new("household", "documents", "saved:fictional-copy", "document", "shared", "Fictional saved copy", null, "Saved text."), [], c, tx, default);
            await tx.CommitAsync();
        }
        Assert.Equal(404, (await Original(copy.ToString())).Status);
        var elsewhere = Path.Combine(Path.GetTempPath(), "rhythm-test-changed-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            Assert.Equal(404, (await Original(original.id, new Archive(elsewhere))).Status);
            var hash = Hashing.Hash(pdf);
            Directory.CreateDirectory(Path.Combine(elsewhere, hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(elsewhere, hash[..2], hash), [.. pdf, 0x20]);
            var changed = await Original(original.id, new Archive(elsewhere));
            Assert.Equal(503, changed.Status);
            Assert.DoesNotContain("%PDF", System.Text.Encoding.ASCII.GetString(changed.Body));
        }
        finally { if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, true); }

        // Reading an unchanged file again changes nothing; a changed one updates, and its bad rows are reported.
        async Task<long> Count(string sql) { await using var cmd = fixture.Source.CreateCommand(sql); return (long)(await cmd.ExecuteScalarAsync())!; }
        const string Csv = "SELECT count(*) FROM measurements m JOIN resources r ON r.id=m.resource_id WHERE r.provider='csv' AND r.status='current'";
        Assert.Equal(4, await Count(Csv));
        var again = new CsvAdapter();
        again.Configure(new(Settings("csv", import), config, db, archive));
        await again.Sync(new MeasurementSink(ingest, again), default);
        Assert.Equal(4, await Count(Csv));
        Assert.Equal(4, await Count("SELECT (metadata->>'unchanged')::bigint FROM sync_jobs WHERE provider='csv' AND metadata->>'file'='readings.csv'"));
        File.WriteAllText(Path.Combine(import, "readings.csv"), "time,metric,value,unit,subject,source\n2026-10-20T07:05:00+01:00,weight,71.9,kg,alex,Fictional scale\n"
            + "yesterday,weight,70,kg,alex,Fictional scale\n2026-10-20,water_glasses,7,count,,\n");
        File.SetLastWriteTimeUtc(Path.Combine(import, "readings.csv"), DateTime.UtcNow.AddMinutes(-1));
        await again.Sync(new MeasurementSink(ingest, again), default);
        Assert.Equal(2, await Count(Csv));
        Assert.Equal(71.9, (await Values("weight")).Single());
        var status = await new Retrieval(db).Status(app, default);
        Assert.Contains("partial", status);
        Assert.Contains("line 3 time_invalid", status);
        // A removed file's numbers and a removed document leave; uploads stay.
        File.Delete(Path.Combine(import, "readings.csv"));
        File.Delete(Path.Combine(import, "documents", "Allotment rota.txt"));
        await again.Sync(new MeasurementSink(ingest, again), default);
        await documents.Import(default);
        Assert.Equal(0, await Count(Csv));
        Assert.Equal(1, await Count("SELECT count(*) FROM resources WHERE provider='documents' AND external_id LIKE 'upload:%' AND status='current' AND position('#part:' IN external_id)=0"));
        Assert.Equal(0, await Count("SELECT count(*) FROM resources WHERE provider='documents' AND external_id='file:Allotment rota.txt' AND status='current'"));
        Assert.Equal("complete", await host.Refresh(false, default));
        page = await host.Read(app, null, null, default);
        Assert.DoesNotContain(page.nodes, n => n.id == body);
        Assert.DoesNotContain(page.nodes, n => n.id == documents.GraphId("file:Allotment rota.txt"));
        Assert.Contains(page.nodes, n => n.id == (string)uploaded["graphId"]!);
        Directory.Delete(import, true);

        async Task<double[]> Values(string metric)
        {
            await using var cmd = fixture.Source.CreateCommand("SELECT m.value FROM measurements m JOIN resources r ON r.id=m.resource_id WHERE r.provider='csv' AND r.status='current' AND m.metric=@m");
            cmd.Parameters.AddWithValue("m", metric);
            var values = new List<double>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) values.Add(reader.GetDouble(0));
            return [.. values];
        }
    }
}

// Composable layouts against a real database (docs/dev/screens.md 10.7): who may arrange a dashboard, the newest version
// only, from the revision a change started from, the app arranging only the parts it has, next versions and restores
// keeping their arrangement, nothing else about the dashboard changing, and its layouts going with it; then the same
// through app_dashboards, with a map part's focused sky.
[Collection(SharedStateCollection.Name)]
public sealed class DashboardLayoutDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static DashboardLayout Layout(string json) => JsonSerializer.Deserialize<DashboardLayout>(json, DashboardLayout.Json)!;
    static readonly string Mcp = Schemas.Folder("mcp");
    async Task<Guid> App() => (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
    async Task<Guid> Harness()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-test-layouts-" + Guid.NewGuid().ToString("N")[..8]);
        try { return await new Principals(fixture.Source).Create("layouts-" + Guid.NewGuid().ToString("N")[..8], "harness", null, Path.Combine(folder, "harness"), default); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    async Task<JsonNode> Get(Guid principal, Guid id, int? version = null) =>
        JsonNode.Parse((await new DashboardStore(fixture.Source).Get(principal, id, version, false, default))!)!;
    async Task<string> Text(string sql, Guid id)
    {
        await using var cmd = fixture.Source.CreateCommand(sql);
        cmd.Parameters.AddWithValue("d", id);
        return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
    }
    // Everything about a dashboard an arrangement must leave as it is.
    Task<string> Untouched(Guid id) => Text("""
        SELECT concat_ws('|',(SELECT updated_at::text||coalesce(edits_allowed::text,'') FROM dashboards WHERE id=@d),
          (SELECT count(*) FROM dashboard_versions WHERE dashboard_id=@d),(SELECT count(*) FROM dashboard_writes WHERE dashboard_id=@d),
          (SELECT string_agg(corner,',' ORDER BY corner) FROM ambient_cards WHERE dashboard_id=@d),(SELECT count(*) FROM sky_pins))
        """, id);
    const string Map = """{"id":"map","kind":"map","x":0,"y":0,"w":7,"h":12,"focus":{"query":"walks"}}""";
    static string Parts(params string[] parts) => "{\"parts\":[" + string.Join(",", parts) + "]}";
    // A map with two data stars from fictional readings, Alex's body and glucose, and the map's host.
    async Task<(RelationshipHost Host, string State, string Body, string Glucose)> Stars(string key)
    {
        var config = TestSettings.Use();
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        using (var call = JsonDocument.Parse($$"""
            {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:{{key}}","source":{"externalId":"{{key}}","kind":"body-readings",
             "title":"Fictional body readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
             "measurements":[{"metric":"weight","value":71.4,"unit":"kg","observedAt":"{{at}}","key":"{{key}}-weight"},
               {"metric":"glucose","value":104,"unit":"mg/dL","observedAt":"{{at}}","key":"{{key}}-glucose"}]}
            """))
            await new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config).Measurements("cgm", call.RootElement, default);
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, AdapterRegistry.Empty, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), AdapterRegistry.Empty);
        Assert.Equal("complete", await host.Refresh(false, default));
        string Star(string group) => new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:" + group).GraphId;
        return (host, state, Star("body"), Star("glucose"));
    }

    [PostgresFact] public async Task The_newest_version_is_arranged_in_place_and_nothing_else_about_it_changes()
    {
        var app = await App(); var harness = await Harness();
        var store = new DashboardStore(fixture.Source);
        var draft = new DashboardDraft("Fictional walks", "", "<p>DEMO</p>", [new("walks", "data", "rhythm:data:walks")], "First version.",
            [new DashboardWrite("mood", "log", Log: "mood")]);
        DashboardStar[] stars = [new("rhythm:data:walks", "DEMO · Walks")];
        var (id, first) = await store.Save(harness, null, null, draft, stars, "First version.", default);
        // Saved without a layout: null (the page alone), revision 0, no note or time.
        var saved = await Get(app, id);
        Assert.Equal((JsonValueKind.Null, 0, JsonValueKind.Null, JsonValueKind.Null), (saved["layout"]?.GetValueKind() ?? JsonValueKind.Null,
            (int)saved["layoutRevision"]!, saved["layoutNote"]?.GetValueKind() ?? JsonValueKind.Null, saved["layoutSavedAt"]?.GetValueKind() ?? JsonValueKind.Null));
        await store.AllowEdits(app, id, first, true, default);
        await store.SetCard(app, "changed", id, default);
        await store.SetPin(app, "rhythm:data:walks", true, default);
        var before = await Untouched(id);

        // The app arranges the page alone; only that version's layout changes, as its first revision.
        var smaller = Layout(Parts("""{"id":"page","kind":"page","x":0,"y":0,"w":6,"h":6}"""));
        var arranged = await store.SaveLayout(app, id, first, 0, smaller, " Made the page smaller ", default);
        Assert.Equal((true, 1), ((bool)arranged["saved"]!, (int)arranged["layoutRevision"]!));
        Assert.Equal(before, await Untouched(id));
        saved = await Get(app, id);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(smaller.ToJson()), saved["layout"]));
        Assert.Equal((1, "Made the page smaller", 1, true), ((int)saved["layoutRevision"]!, (string?)saved["layoutNote"], saved["versions"]!.AsArray().Count,
            (bool)saved["editsAllowed"]!));
        Schemas.Valid(Mcp, "app_dashboards.output.schema.json", new JsonObject { ["dashboard"] = JsonNode.Parse((await store.Get(app, id, null, true, default))!) });
        Schemas.Valid(Mcp, "app_dashboards.output.schema.json", new JsonObject { ["dashboards"] = JsonNode.Parse(await store.List(app, default)) });

        // From a revision that is no longer current: nothing is saved, and the answer is the layout as it stands.
        var stale = await store.SaveLayout(app, id, first, 0, DashboardLayout.PageAlone, "Put the page back", default);
        Assert.Equal((false, 1), ((bool)stale["saved"]!, (int)stale["layoutRevision"]!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(smaller.ToJson()), stale["layout"]));
        // The app only arranges: a part added, removed or changed in kind is refused; a harness may compose any layout.
        async Task<string> Refused(Task task) => (await Assert.ThrowsAsync<PostgresException>(() => task)).SqlState;
        var composed = Layout(Parts(Map, """{"id":"page","kind":"page","x":7,"y":0,"w":5,"h":8}""", """{"id":"glucose","kind":"glucose","x":7,"y":8,"w":5,"h":4}"""));
        Assert.Equal("22023", await Refused(store.SaveLayout(app, id, first, 1, composed, "Added the map", default)));
        // A screen always arranges from the revision it read, so the app can't skip the stale check; a harness may arrange
        // whatever is there.
        Assert.Equal("22023", await Refused(store.SaveLayout(app, id, first, null, smaller, "Made the page smaller", default)));
        Assert.Equal(2, (int)(await store.SaveLayout(harness, id, first, null, composed, "Added the map and glucose", default))["layoutRevision"]!);
        // The app moves parts and changes a map's focus; it never adds a focus member to a part that isn't a map, changes a
        // kind, or drops a part.
        var moved = Layout(Parts("""{"id":"map","kind":"map","x":5,"y":0,"w":7,"h":12,"focus":{"providers":["garmin"]}}""",
            """{"id":"page","kind":"page","x":0,"y":0,"w":5,"h":8}""", """{"id":"glucose","kind":"glucose","x":0,"y":8,"w":5,"h":4}"""));
        Assert.Equal(3, (int)(await store.SaveLayout(app, id, first, 2, moved, "Moved the map right", default))["layoutRevision"]!);
        Assert.Equal("22023", await Refused(store.SaveLayout(app, id, first, 3, Layout(Parts("""{"id":"map","kind":"map","x":5,"y":0,"w":7,"h":12}""",
            """{"id":"page","kind":"page","x":0,"y":0,"w":5,"h":8}""", """{"id":"glucose","kind":"recovery","x":0,"y":8,"w":5,"h":4}""")), "Changed a tile", default)));
        Assert.Equal("22023", await Refused(store.SaveLayout(app, id, first, 3, Layout(Parts("""{"id":"map","kind":"map","x":5,"y":0,"w":7,"h":12}""",
            """{"id":"page","kind":"page","x":0,"y":0,"w":5,"h":12}""")), "Dropped glucose", default)));
        // Roles: a login with no role reaches nothing, and the app still never saves a version.
        Assert.Equal("42501", await Refused(store.SaveLayout(Guid.NewGuid(), id, first, 3, moved, "Moved", default)));
        Assert.Equal("42501", await Refused(store.Save(app, id, null, draft, stars, "Second.", default)));
        // A layout past 8,000 characters as stored is refused.
        var padded = JsonNode.Parse(moved.ToJson())!.AsObject();
        padded["padding"] = new string('a', 8000);
        await using (var cmd = fixture.Source.CreateCommand("SELECT save_dashboard_layout(@p,@d,1,3,@l::jsonb,'Too long')"))
        {
            cmd.Parameters.AddWithValue("p", harness); cmd.Parameters.AddWithValue("d", id); cmd.Parameters.AddWithValue("l", padded.ToJsonString());
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync())).SqlState);
        }
        Assert.Equal(before, await Untouched(id));

        // A next version without a layout keeps the arrangement, at revision 0; only the newest version is arranged.
        var (_, second) = await store.Save(harness, id, null, draft, stars, "Second version.", default);
        saved = await Get(app, id);
        Assert.Equal((2, 0), (second, (int)saved["layoutRevision"]!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(moved.ToJson()), saved["layout"]));
        Assert.Equal("22023", await Refused(store.SaveLayout(app, id, first, 3, moved, "Moved", default)));
        Assert.Equal("P0002", await Refused(store.SaveLayout(app, id, 9, 0, moved, "Moved", default)));
        Assert.Equal("P0002", await Refused(store.SaveLayout(app, Guid.NewGuid(), 1, 0, moved, "Moved", default)));
        Assert.Equal(1, (int)(await store.SaveLayout(app, id, second, 0, Layout(moved.ToJson().Replace("\"providers\":[\"garmin\"]", "\"query\":\"walks\"")),
            "Focused the map on walks", default))["layoutRevision"]!);
        // A next version with its own layout keeps that one; the page alone is a layout like any other.
        var (_, third) = await store.Save(harness, id, null, draft, stars, "Third version.", default, DashboardLayout.PageAlone);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(DashboardLayout.PageAlone.ToJson()), (await Get(app, id))["layout"]));
        // A restore brings a version back as it was last arranged, in the new version's own layout.
        var (_, fourth) = await store.Save(harness, id, 1, null, [], "Back to the first.", default);
        saved = await Get(app, id);
        Assert.Equal((4, 1, 0), (fourth, (int?)saved["restoredFrom"], (int)saved["layoutRevision"]!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(moved.ToJson()), saved["layout"]));
        Assert.Equal(1, (int)(await Get(app, id, 2))["layoutRevision"]!);
        Assert.Equal(3, third);

        // Deleting the dashboard removes its layouts with its versions.
        Assert.NotEqual("0", await Text("SELECT count(*) FROM dashboard_layouts WHERE dashboard_id=@d", id));
        await store.Delete(harness, id, "Fictional walks", default);
        Assert.Equal("0", await Text("SELECT count(*) FROM dashboard_layouts WHERE dashboard_id=@d", id));
    }

    [PostgresFact] public async Task The_app_arranges_through_app_dashboards_and_reads_a_map_parts_focused_sky()
    {
        var config = TestSettings.Use();
        var app = await App(); var harness = await Harness();
        var (host, state, body, _) = await Stars("layouts-1");
        var store = new DashboardStore(fixture.Source);
        var draft = new DashboardDraft("Fictional body", "", "<p>DEMO</p>", [new("body", "data", body)], "First version.");
        var layout = Layout(Parts($$$$"""{"id":"map","kind":"map","x":0,"y":0,"w":7,"h":12,"focus":{"near":{"star":"{{{{body}}}}"}}}""",
            """{"id":"page","kind":"page","x":7,"y":0,"w":5,"h":6}""", """{"id":"sky","kind":"map","x":7,"y":6,"w":5,"h":4}"""));
        var (id, version) = await store.Save(harness, null, null, draft, [new(body, "Alex · Body")], "First version.", default, layout);
        var services = new ServiceCollection().AddSingleton(store).AddSingleton(new DashboardFeeds(fixture.Db, host, AdapterRegistry.Empty)).BuildServiceProvider();
        async Task<(int Status, JsonNode? Content)> Call(object arguments)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "d", method = "tools/call",
                @params = new { name = "app_dashboards", arguments } }));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), host, new AppHealth(fixture.Db, config), AdapterRegistry.Empty);
            var answer = JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!;
            var content = answer["result"]?["structuredContent"];
            if (context.Response.StatusCode == 200) Schemas.Valid(Mcp, "app_dashboards.output.schema.json", content);
            return (context.Response.StatusCode, content);
        }

        // Every summary, version and feed read carries the layout as it stands.
        var (_, listed) = await Call(new { });
        var row = listed!["dashboards"]!.AsArray().Single(d => (string?)d!["id"] == id.ToString())!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(layout.ToJson()), row["layout"]));
        var (_, data) = await Call(new { id, data = true });
        Assert.Equal((version, 0), ((int)data!["dashboard"]!["version"]!, (int)data["dashboard"]!["layoutRevision"]!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(layout.ToJson()), data["dashboard"]!["layout"]));

        // A screen's arrangement, from the revision it started from.
        var moved = JsonNode.Parse(layout.ToJson().Replace("\"x\":0,\"y\":0,\"w\":7", "\"x\":5,\"y\":0,\"w\":7").Replace("\"x\":7,\"y\":0,\"w\":5,\"h\":6", "\"x\":0,\"y\":0,\"w\":5,\"h\":6")
            .Replace("\"x\":7,\"y\":6", "\"x\":0,\"y\":6"))!;
        var (status, saved) = await Call(new { id, version, layout = moved, @base = 0, note = "Moved the map right" });
        Assert.Equal((200, true, 1), (status, (bool)saved!["ok"]!, (int)saved["layoutRevision"]!));
        var (_, stale) = await Call(new { id, version, layout = JsonNode.Parse(layout.ToJson()), @base = 0, note = "Moved the map left" });
        Assert.Equal((false, 1), ((bool)stale!["ok"]!, (int)stale["layoutRevision"]!));
        Assert.True(JsonNode.DeepEquals(moved, stale["layout"]));
        // Refused, each with its reason: more than an arrangement, a layout that breaks the rules, a focus star the map
        // doesn't have, an earlier version.
        async Task<string?> Reason(object arguments) => (string?)(await Call(arguments)).Content!["reason"];
        var added = moved!.DeepClone().AsObject();
        added["parts"]!.AsArray().Add(JsonNode.Parse("""{"id":"glucose","kind":"glucose","x":5,"y":0,"w":2,"h":2}"""));
        Assert.Equal("layout.parts map and glucose overlap at cell 5, 0", await Reason(new { id, version, layout = added, @base = 1, note = "Added glucose" }));
        added["parts"]![3]!["x"] = 0;
        added["parts"]![3]!["y"] = 10;
        Assert.Equal("A screen only moves, resizes or refocuses the parts a dashboard has.", await Reason(new { id, version, layout = added, @base = 1, note = "Added glucose" }));
        var unknown = JsonNode.Parse(moved.ToJsonString().Replace(body, "notes:notes:Nowhere.md"));
        Assert.Equal("layout.parts[0].focus.near.star is not a star you can read; find stars with explore_graph",
            await Reason(new { id, version, layout = unknown, @base = 1, note = "Refocused" }));
        // Moved back: its focus didn't change, so its star isn't checked again.
        Assert.Equal(2, (int)(await Call(new { id, version, layout = JsonNode.Parse(layout.ToJson()), @base = 1, note = "Moved the map left" })).Content!["layoutRevision"]!);
        await store.Save(harness, id, null, draft, [new(body, "Alex · Body")], "Second version.", default);
        Assert.Equal("This is an earlier version; open the newest to arrange it.", await Reason(new { id, version, layout = moved, @base = 2, note = "Moved" }));

        // A map part's focused sky: the stars that meet its focus, as app_graph pages them.
        var (mapStatus, map) = await Call(new { id, map = "map" });
        Assert.Equal(200, mapStatus);
        Assert.Equal("map", (string?)map!["map"]!["part"]);
        Assert.Contains(map["map"]!["nodes"]!.AsArray(), n => (string?)n!["id"] == body);
        Assert.Equal(map["map"]!["nodes"]!.AsArray().Count, (int)map["map"]!["matched"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$$"""{"near":{"star":"{{{body}}}","depth":1}}"""), map["map"]!["focus"]));
        Assert.Equal(200, (await Call(new { id, version, map = "map", cursor = "o:0" })).Status);
        Assert.Equal(400, (await Call(new { id, map = "map", cursor = "page 2" })).Status);
        Assert.Equal(400, (await Call(new { id, map = "page" })).Status);
        Assert.Equal(400, (await Call(new { id, map = "sky" })).Status);
        Assert.Equal(404, (await Call(new { id, map = "glucose" })).Status);
        Assert.Equal(404, (await Call(new { id = Guid.NewGuid(), map = "map" })).Status);

        // The listing names layout and map among app_dashboards' arguments, and grid among a screen's settings: the
        // facade learns from it that the engine has piece 3.
        var tools = JsonSerializer.SerializeToNode(await AppEndpoint.Tools(new AppHealth(fixture.Db, config), app, default))!.AsArray();
        JsonNode Input(string name) => tools.Single(t => (string?)t!["name"] == name)!["inputSchema"]!["properties"]!;
        Assert.NotNull(Input("app_dashboards")["layout"]);
        Assert.NotNull(Input("app_dashboards")["map"]);
        Assert.NotNull(Input("app_screens")["settings"]!["properties"]!["grid"]);
        Directory.Delete(state, true);
    }

    [PostgresFact] public async Task An_ai_client_composes_a_dashboard_and_arranges_it_without_a_new_version()
    {
        var harness = await Harness();
        var (host, state, body, glucose) = await Stars("layouts-2");
        var store = new DashboardStore(fixture.Source);
        var services = new ServiceCollection().AddSingleton(store).AddSingleton(new DashboardFeeds(fixture.Db, host, AdapterRegistry.Empty))
            .AddSingleton(host).BuildServiceProvider();
        var tools = new HarnessTools(new Retrieval(fixture.Db), new HarnessLogin(harness));
        DashboardFeed[] feeds = [new("body", "data", body)];
        string Focused(int x) => $$$$"""{"id":"map","kind":"map","x":{{{{x}}}},"y":0,"w":7,"h":12,"focus":{"near":{"star":"{{{{glucose}}}}","depth":1}}}""";
        var layout = Layout(Parts(Focused(0), """{"id":"page","kind":"page","x":7,"y":0,"w":5,"h":8}""", """{"id":"glucose","kind":"glucose","x":7,"y":8,"w":5,"h":4}"""));
        Guid id;
        using (var saved = JsonDocument.Parse(await tools.SaveDashboard(services, "First version.", title: "Fictional body", html: "<p>DEMO</p>", feeds: feeds, layout: layout)))
            id = saved.RootElement.GetProperty("id").GetGuid();
        // list_dashboards gives each layout as it stands; its star filter keeps a dashboard for its feeds' stars and for a
        // map part focused near a star. A layout's stars don't become the dashboard's stars.
        async Task<JsonNode[]> Listed(string? star) =>
            [.. JsonNode.Parse(await tools.ListDashboards(services, star: star))!["dashboards"]!.AsArray().Select(d => d!)];
        bool Has(JsonNode[] listed) => listed.Any(d => (string?)d["id"] == id.ToString());
        Assert.True(Has(await Listed(glucose)));
        Assert.True(Has(await Listed(body)));
        Assert.False(Has(await Listed("notes:notes:Nowhere.md")));
        var row = (await Listed(null)).Single(d => (string?)d["id"] == id.ToString());
        Assert.Equal((0, body), ((int)row["layoutRevision"]!, (string?)row["stars"]!.AsArray().Single()!["graphId"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(layout.ToJson()), row["layout"]));

        // Refused before anything is saved: a layout that breaks the rules, a focus star it can't read, a card of itself.
        async Task<string> Refused(Func<Task<string>> call) => (await Assert.ThrowsAsync<McpException>(call)).Message;
        Assert.Equal("layout has exactly one page part", await Refused(() => tools.SaveDashboard(services, "Only the map.", dashboardId: id,
            layout: Layout(Parts(Focused(0))))));
        Assert.Equal("layout.parts[0].focus.near.star is not a star you can read; find stars with explore_graph", await Refused(() =>
            tools.SaveDashboard(services, "Refocused.", dashboardId: id, layout: Layout(layout.ToJson().Replace(glucose, "notes:notes:Nowhere.md")))));
        var carded = Layout(Parts(Focused(0), """{"id":"page","kind":"page","x":7,"y":0,"w":5,"h":8}""",
            $$"""{"id":"card","kind":"card","x":7,"y":8,"w":5,"h":4,"dashboard":"{{id}}"}"""));
        Assert.Equal("layout.parts[2].dashboard is this dashboard; a card shows another one", await Refused(() =>
            tools.SaveDashboard(services, "With a card.", title: "Fictional body", html: "<p>DEMO</p>", feeds: feeds, dashboardId: id, layout: carded)));

        // dashboardId, layout and note alone arrange the newest version without a new one.
        var moved = Layout(Parts(Focused(5), """{"id":"page","kind":"page","x":0,"y":0,"w":5,"h":8}""", """{"id":"glucose","kind":"glucose","x":0,"y":8,"w":5,"h":4}"""));
        using (var arranged = JsonDocument.Parse(await tools.SaveDashboard(services, "Moved the map right", dashboardId: id, layout: moved)))
            Assert.Equal((id, 1, 1), (arranged.RootElement.GetProperty("id").GetGuid(), arranged.RootElement.GetProperty("version").GetInt32(),
                arranged.RootElement.GetProperty("layoutRevision").GetInt32()));
        // With layoutRevision, refused when the layout changed since; layoutRevision belongs to that form only.
        Assert.Equal("The layout changed since layoutRevision 0: it is at 1 now; read it again with list_dashboards and arrange that",
            await Refused(() => tools.SaveDashboard(services, "Moved back", dashboardId: id, layout: layout, layoutRevision: 0)));
        using (var back = JsonDocument.Parse(await tools.SaveDashboard(services, "Moved back", dashboardId: id, layout: layout, layoutRevision: 1)))
            Assert.Equal(2, back.RootElement.GetProperty("layoutRevision").GetInt32());
        Assert.StartsWith("layoutRevision is for arranging the newest version", await Refused(() =>
            tools.SaveDashboard(services, "Second.", title: "Fictional body", html: "<p>DEMO</p>", feeds: feeds, dashboardId: id, layoutRevision: 2)));
        // A next version without a layout keeps the arrangement; a restore takes none, since it brings back its own.
        using (var next = JsonDocument.Parse(await tools.SaveDashboard(services, "Second version.", title: "Fictional body", html: "<p>DEMO 2</p>",
            feeds: feeds, dashboardId: id)))
            Assert.Equal(2, next.RootElement.GetProperty("version").GetInt32());
        var newest = JsonNode.Parse(await tools.ListDashboards(services, dashboardId: id))!;
        Assert.Equal((2, 0), ((int)newest["version"]!, (int)newest["layoutRevision"]!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(layout.ToJson()), newest["layout"]));
        Assert.StartsWith("restoreVersion copies an earlier version back, as it was last arranged", await Refused(() =>
            tools.SaveDashboard(services, "Back to the first.", dashboardId: id, restoreVersion: 1, layout: layout)));
        // The tool lists layout and layoutRevision among save_dashboard's arguments, as the SDK binds them.
        var save = HarnessTools.Create(TestSettings.Use(), []).Single(t => t.ProtocolTool.Name == "save_dashboard").ProtocolTool;
        Assert.Equal("layout.parts[0].x is required: a whole number or null.", ToolArguments.Problem(save, new Dictionary<string, JsonElement>
        {
            ["note"] = JsonSerializer.SerializeToElement("Moved"), ["layout"] = JsonDocument.Parse("""{"parts":[{"id":"page","kind":"page","y":0,"w":12,"h":12}]}""").RootElement,
        }));
        Directory.Delete(state, true);
    }
}
