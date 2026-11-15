using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// SB-P6-09 (API-47): sync now from the app's Sources view. Every adapter here is a stand-in: nothing reaches Garmin,
// CareLink, Drive or any other service.
internal static class SyncStandIns
{
    // A wait that is not spent yet: a request that waits is claimed at once within it, and none waiting leaves it empty.
    public static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(100);

    // A MeasurementSource that counts its calls, or fails or hangs when told to.
    public sealed class Feed(string name, TimeSpan? interval = null, string? owner = null, params string[] providers) : IMeasurementSource
    {
        public int Calls;
        public Func<CancellationToken, Task>? Work;
        public string Name => name;
        public string? Owner => owner;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
            providers.ToDictionary(p => p, _ => new ProviderPolicy(SourceAuthority.AuthorizedArchive, "health"));
        public TimeSpan Interval => interval ?? TimeSpan.FromMinutes(1);
        public Task Sync(MeasurementSink sink, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Work?.Invoke(ct) ?? Task.CompletedTask;
        }
    }
    // An ImportSource that counts its calls.
    public sealed class Folder(string name, string? owner = null) : IImportSource
    {
        public int Calls;
        public string Name => name;
        public string? Owner => owner;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
        public TimeSpan Interval => TimeSpan.FromMinutes(1);
        public Task Import(CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }
    // A source whose asked-for batch is its own (ISyncNow): it records who asked, and its regular call is never made.
    public sealed class Own(string name, TimeSpan? limit = null) : IMeasurementSource, ISyncNow
    {
        public int Regular;
        public readonly List<Guid> AskedBy = [];
        public string Name => name;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
        public TimeSpan Interval => TimeSpan.FromMinutes(1);
        public TimeSpan SyncLimit => limit ?? TimeSpan.FromMinutes(15);
        // False: the source's own rule turns the batch away.
        public bool Reaches = true;
        public Task Sync(MeasurementSink sink, CancellationToken ct) { Interlocked.Increment(ref Regular); return Task.CompletedTask; }
        public Task<bool> SyncNow(Guid principal, MeasurementSink sink, CancellationToken ct) { lock (AskedBy) AskedBy.Add(principal); return Task.FromResult(Reaches); }
    }
    // An adapter that is not on the clock (a graph source is read by the map's rebuild, not synced).
    public sealed class Elsewhere(string name) : IAdapter
    {
        public string Name => name;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
    }
    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class SyncRequestsTests
{
    static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid Alex = Guid.NewGuid(), Sam = Guid.NewGuid();

    static (SyncRequests Syncs, SyncStandIns.Clock Clock, SyncStandIns.Feed Feed, SyncStandIns.Folder Folder, SyncStandIns.Own Own) Make(
        Func<Guid, IAdapter, bool>? reads = null)
    {
        var clock = new SyncStandIns.Clock(Noon);
        var feed = new SyncStandIns.Feed("feed", TimeSpan.FromMinutes(5));
        var folder = new SyncStandIns.Folder("folder");
        var own = new SyncStandIns.Own("own");
        var registry = new AdapterRegistry([new SyncStandIns.Elsewhere("map-only"), own, folder, feed]);
        return (new SyncRequests(registry, (p, a, _) => Task.FromResult(reads?.Invoke(p, a) ?? true), clock), clock, feed, folder, own);
    }
    static JsonNode Of(JsonObject list, string name) => list["adapters"]!.AsArray().Single(a => (string?)a!["adapter"] == name)!;
    static string Iso(DateTimeOffset at) => at.ToString("O");

    [Fact] public async Task The_list_names_each_adapter_on_the_clock_with_its_own_interval_and_limit()
    {
        var (syncs, _, _, _, _) = Make();
        var list = await syncs.List(Alex, default);
        Assert.Equal(["feed", "folder", "own"], list["adapters"]!.AsArray().Select(a => (string?)a!["adapter"]));
        Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_sync.output.schema.json", list));
        // Its own interval, and a limit of its own interval (a minute at least) unless it states one.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"adapter":"feed","kind":"measurements","interval":300,"limit":300,"running":false,"pendingSince":null,
             "nextAllowedAt":null,"lastAskedAt":null,"lastFinishedAt":null,"outcome":null}
            """), Of(list, "feed")));
        Assert.Equal(("imports", 60L, 60L), ((string?)Of(list, "folder")["kind"], (long)Of(list, "folder")["interval"]!, (long)Of(list, "folder")["limit"]!));
        Assert.Equal(900L, (long)Of(list, "own")["limit"]!);
        Assert.Equal(TimeSpan.FromMinutes(1), SyncRequests.Limit(new SyncStandIns.Feed("quick", TimeSpan.FromSeconds(20))));
        // A limit of its own is never under a minute either (the contract's limit is 60 at least).
        Assert.Equal(TimeSpan.FromMinutes(1), SyncRequests.Limit(new SyncStandIns.Own("eager", TimeSpan.FromSeconds(30))));
        // An adapter that is not on the clock, or one this build doesn't have, answers as unknown.
        Assert.Null(await syncs.Request(Alex, "map-only", default));
        Assert.Null(await syncs.Request(Alex, "garmin", default));
    }

    [Fact] public async Task A_request_runs_one_batch_and_a_second_inside_the_limit_is_refused()
    {
        var (syncs, clock, feed, _, _) = Make();
        var asked = (await syncs.Request(Alex, "feed", default))!;
        Assert.True((bool)asked["accepted"]!);
        Assert.Null(asked["refused"]);
        Assert.Equal(Iso(Noon), (string?)asked["adapter"]!["pendingSince"]);
        Assert.Null(asked["adapter"]!["nextAllowedAt"]);
        Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_sync.output.schema.json", asked));
        // While it waits, a second is refused: pending.
        Assert.Equal("pending", (string?)(await syncs.Request(Sam, "feed", default))!["refused"]);

        // The scheduler claims it at once, for the principal that asked, and it runs: refused, running.
        var claimed = (await syncs.Next(TimeSpan.FromSeconds(5), default))!;
        Assert.Equal(("feed", Alex), (claimed.Adapter.Name, claimed.Principal));
        Assert.Null(await syncs.Next(SyncStandIns.Soon, default));
        var running = (await syncs.Request(Alex, "feed", default))!;
        Assert.Equal((false, "running", true), ((bool)running["accepted"]!, (string?)running["refused"], (bool)running["adapter"]!["running"]!));
        clock.Now = Noon.AddSeconds(20);
        Assert.Equal(SyncRequests.Complete, await SyncRequests.Run(claimed, new MeasurementSink(null!, claimed.Adapter), new LoggedLines<Scheduler>(), default));
        syncs.Finish(claimed, SyncRequests.Complete);
        Assert.Equal(1, feed.Calls);

        // Done: its outcome, and inside the adapter's own limit (its 5-minute interval) another is refused, too-soon.
        clock.Now = Noon.AddMinutes(4);
        var soon = (await syncs.Request(Sam, "feed", default))!;
        Assert.Equal((false, "too-soon"), ((bool)soon["accepted"]!, (string?)soon["refused"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            {"adapter":"feed","kind":"measurements","interval":300,"limit":300,"running":false,"pendingSince":null,
             "nextAllowedAt":"{{Iso(Noon.AddMinutes(5))}}","lastAskedAt":"{{Iso(Noon)}}","lastFinishedAt":"{{Iso(Noon.AddSeconds(20))}}",
             "outcome":"complete"}
            """), soon["adapter"]));
        Assert.Null(await syncs.Next(SyncStandIns.Soon, default));
        Assert.Equal(1, feed.Calls);
        // Once the limit has passed, one more is accepted.
        clock.Now = Noon.AddMinutes(5);
        Assert.True((bool)(await syncs.Request(Sam, "feed", default))!["accepted"]!);
        Assert.Equal(Sam, (await syncs.Next(SyncStandIns.Soon, default))!.Principal);
    }

    [Fact] public async Task A_batch_of_the_adapters_own_clock_refuses_a_request_until_it_ends()
    {
        var (syncs, _, _, _, _) = Make();
        using (syncs.Runs("folder"))
        {
            var refused = (await syncs.Request(Alex, "folder", default))!;
            Assert.Equal("running", (string?)refused["refused"]);
            Assert.True((bool)refused["adapter"]!["running"]!);
            // Another adapter is not held up.
            Assert.True((bool)(await syncs.Request(Alex, "feed", default))!["accepted"]!);
        }
        Assert.True((bool)(await syncs.Request(Alex, "folder", default))!["accepted"]!);
        // A refused request is not a request: nothing waits for it, and the limit counts from the accepted one.
        Assert.Equal(["feed", "folder"], [(await syncs.Next(SyncStandIns.Soon, default))!.Adapter.Name, (await syncs.Next(SyncStandIns.Soon, default))!.Adapter.Name]);
        Assert.Null(await syncs.Next(SyncStandIns.Soon, default));
        using (syncs.Runs("not-on-the-clock")) { }
    }

    [Fact] public async Task The_scheduler_wakes_for_a_request_and_waits_out_the_minute_without_one()
    {
        var (syncs, _, _, _, _) = Make();
        var started = DateTimeOffset.UtcNow;
        Assert.Null(await syncs.Next(TimeSpan.FromMilliseconds(200), default));
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(150));
        var waiting = syncs.Next(TimeSpan.FromSeconds(30), default);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        await syncs.Request(Alex, "own", default);
        var woke = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("own", woke!.Adapter.Name);
        using var stop = new CancellationTokenSource();
        var stopping = syncs.Next(TimeSpan.FromSeconds(30), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
    }

    // Once the scheduler's minute is spent (a batch ran past it), a request that waits is left for the next minute's wait,
    // after the clock's own jobs: it stays pending, and is claimed as soon as a wait has time left.
    [Fact] public async Task A_waiting_request_is_left_for_the_next_minute_once_this_one_is_spent()
    {
        var (syncs, _, _, _, _) = Make();
        Assert.True((bool)(await syncs.Request(Alex, "feed", default))!["accepted"]!);
        Assert.Null(await syncs.Next(TimeSpan.Zero, default));
        Assert.Null(await syncs.Next(TimeSpan.FromSeconds(-30), default));
        Assert.Equal("pending", (string?)(await syncs.Request(Sam, "feed", default))!["refused"]);
        var claimed = (await syncs.Next(SyncStandIns.Soon, default))!;
        Assert.Equal(("feed", Alex), (claimed.Adapter.Name, claimed.Principal));
    }

    [Fact] public async Task An_asked_batch_is_one_call_of_the_adapters_own_and_its_failures_are_outcomes()
    {
        var log = new LoggedLines<Scheduler>();
        var (_, _, feed, folder, own) = Make();
        Task<string> Run(IAdapter adapter, TimeSpan? budget = null, CancellationToken stop = default) =>
            SyncRequests.Run(new(adapter, Alex), new MeasurementSink(null!, adapter), log, stop, budget);
        Assert.Equal("complete", await Run(folder));
        Assert.Equal(1, folder.Calls);
        // An adapter with a batch of its own is asked for it, as the principal that asked, and its regular call is not made.
        Assert.Equal("complete", await Run(own));
        Assert.Equal([Alex], own.AskedBy);
        Assert.Equal(0, own.Regular);
        // One its source's own rule turned away reached nothing: failed, and the log says why.
        own.Reaches = false;
        Assert.Equal("failed", await Run(own));
        own.Reaches = true;
        feed.Work = _ => throw new IOException("a file in use");
        Assert.Equal("failed", await Run(feed));
        // Bounded: a batch that runs past its budget is cut off and timed out; the engine stopping is not an outcome.
        feed.Work = ct => Task.Delay(Timeout.Infinite, ct);
        Assert.Equal("timed-out", await Run(feed, TimeSpan.FromMilliseconds(100)));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(feed, stop: stop.Token));
        Assert.Equal(["folder sync now: complete", "own sync now: complete", "own sync now not run: the source's own limit refused it",
            "feed sync now failed (IOException)", "feed sync now timed out"], log.Lines);
    }

    // Every adapter on the engine's clock says whose rows it writes, in a member of its own: one that relied on
    // IAdapter.Owner's default would be shown to no one with memory.scopes on (it fails closed), and deleting an
    // override fails here first.
    [Fact] public void Every_adapter_on_the_clock_states_its_owner_itself()
    {
        var clocked = typeof(AppEndpoint).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && (typeof(IMeasurementSource).IsAssignableFrom(t) || typeof(IImportSource).IsAssignableFrom(t)))
            .ToArray();
        Assert.Equal(["CsvAdapter", "DemoAdapter", "DocumentsAdapter", "GarminAdapter", "NightscoutAdapter"], clocked.Select(t => t.Name).Order());
        foreach (var type in clocked)
        {
            var map = type.GetInterfaceMap(typeof(IAdapter));
            var owner = map.TargetMethods[Array.FindIndex(map.InterfaceMethods, m => m.Name == "get_" + nameof(IAdapter.Owner))];
            Assert.True(owner.DeclaringType == type, type.Name + " takes IAdapter.Owner's default");
        }
    }

    [Fact] public async Task A_caller_sees_and_asks_only_the_adapters_it_reads()
    {
        var (syncs, _, _, _, _) = Make((principal, adapter) => principal == Alex || adapter.Name == "folder");
        Assert.Equal(["folder"], (await syncs.List(Sam, default))["adapters"]!.AsArray().Select(a => (string?)a!["adapter"]));
        Assert.Null(await syncs.Request(Sam, "feed", default));
        Assert.NotNull(await syncs.Request(Sam, "folder", default));
        Assert.Equal(3, (await syncs.List(Alex, default))["adapters"]!.AsArray().Count);
    }

    // The app role's tool: listed beside the others, strict about its arguments, 404 for an adapter it can't ask.
    [Fact] public async Task The_app_asks_with_app_sync_and_nothing_else_is_taken()
    {
        var config = TestSettings.Use();
        var (syncs, _, _, _, _) = Make();
        async Task<(int Status, JsonElement Body)> Call(string args)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(syncs).BuildServiceProvider() };
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"method\":\"tools/call\",\"params\":{\"name\":\"app_sync\",\"arguments\":" + args + "}}"));
            context.Response.Body = new MemoryStream();
            await AppEndpoint.Handle(context, new Caller(Alex, "app", "app", null), null!, new AppHealth(Nowhere(), config), AdapterRegistry.Empty);
            var text = ((MemoryStream)context.Response.Body).ToArray();
            return (context.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
        JsonNode Content(JsonElement body) => JsonNode.Parse(body.GetProperty("result").GetProperty("structuredContent").GetRawText())!;
        var (code, listed) = await Call("{}");
        Assert.Equal(200, code);
        Assert.Equal(3, Content(listed)["adapters"]!.AsArray().Count);
        var (asked, accepted) = await Call("""{"adapter":"feed"}""");
        Assert.Equal(200, asked);
        Assert.True((bool)Content(accepted)["accepted"]!);
        Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_sync.output.schema.json", Content(accepted)));
        Assert.Equal("pending", (string?)Content((await Call("""{"adapter":"feed"}""")).Body)["refused"]);
        Assert.Equal(404, (await Call("""{"adapter":"map-only"}""")).Status);
        Assert.Equal(404, (await Call("""{"adapter":"nightscout"}""")).Status);
        foreach (var wrong in new[] { """{"adapter":"Feed"}""", """{"adapter":""}""", """{"adapter":5}""", """{"adapter":"feed","now":true}""",
            """{"adapters":"feed"}""", """{"adapter":"feed","adapter":"own"}""" })
            Assert.Equal(400, (await Call(wrong)).Status);
        Assert.True(AppEndpoint.IsCore("app_sync"));
    }
    static Database Nowhere() => new(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none;Timeout=1"));
}

// The same against a real database and the engine's own scheduler: a request wakes the clock and its batch runs at once,
// not at the next minute; a second inside the limit is refused; a failure is an outcome and the clock runs on. With
// memory.scopes on, each caller sees and asks only the adapters whose owner's rows it reads (docs/dev/memory.md 7.2).
[Collection(SharedStateCollection.Name)]
public sealed class SyncNowDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The map's source, unavailable: the rebuilds the clock runs beside the syncs do nothing here.
    sealed class NoMap : IRelationshipSource
    {
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct) => throw new HttpRequestException("no map in this test");
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => Task.FromResult(SourceCheck.Unavailable);
    }

    static async Task Until(Func<Task<bool>> done, int seconds = 30)
    {
        for (var waited = 0; waited < seconds * 10 && !await done(); waited++) await Task.Delay(100);
        Assert.True(await done(), "waited " + seconds + " s");
    }

    [PostgresFact] public async Task The_scheduler_runs_an_asked_batch_at_once_and_refuses_a_second_inside_its_limit()
    {
        TestSettings.Use();
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-syncnow-" + Guid.NewGuid().ToString("N")[..10]);
        var feed = new SyncStandIns.Feed("feed");
        var broken = new SyncStandIns.Feed("broken");
        var folder = new SyncStandIns.Folder("folder");
        var registry = new AdapterRegistry([feed, broken, folder]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new NoMap(), registry);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var syncs = new SyncRequests(registry, db);
        var log = new LoggedLines<Scheduler>();
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(fixture.Source), new Principals(fixture.Source), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, log, syncs);
        JsonNode Of(JsonObject list, string name) => list["adapters"]!.AsArray().Single(a => (string?)a!["adapter"] == name)!;
        async Task<string?> Outcome(string name) => (string?)Of(await syncs.List(app, default), name)["outcome"];
        try
        {
            await scheduler.StartAsync(default);
            // The start's own batches, on the adapters' own clock.
            await Until(() => Task.FromResult((feed.Calls, broken.Calls, folder.Calls) == (1, 1, 1)));
            // The answer as app_sync gives it, with the scopes off: every adapter on the clock, for the app.
            var listed = await syncs.List(app, default);
            Assert.Equal(["broken", "feed", "folder"], listed["adapters"]!.AsArray().Select(a => (string?)a!["adapter"]));
            Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_sync.output.schema.json", listed));

            var asked = DateTimeOffset.UtcNow;
            Assert.True((bool)(await syncs.Request(app, "feed", default))!["accepted"]!);
            broken.Work = _ => throw new IOException("a file in use");
            Assert.True((bool)(await syncs.Request(app, "broken", default))!["accepted"]!);
            await Until(async () => await Outcome("feed") is not null && await Outcome("broken") is not null);
            // At once, not at the minute's next tick, and one batch each.
            Assert.True(DateTimeOffset.UtcNow - asked < TimeSpan.FromSeconds(20));
            Assert.Equal((2, 2, 1), (feed.Calls, broken.Calls, folder.Calls));
            Assert.Equal(("complete", "failed"), (await Outcome("feed"), await Outcome("broken")));
            Assert.Contains("feed sync now: complete", log.Lines);
            Assert.Contains("broken sync now failed (IOException)", log.Lines);
            // A second inside the limit is refused and runs nothing.
            var again = (await syncs.Request(app, "feed", default))!;
            Assert.Equal((false, "too-soon"), ((bool)again["accepted"]!, (string?)again["refused"]));
            Assert.Empty(Schemas.Check(Schemas.Folder("mcp"), "app_sync.output.schema.json", again));
            await Task.Delay(500);
            Assert.Equal(2, feed.Calls);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);

            // The app role lists app_sync beside its other tools: no arguments for the list, or one adapter's name.
            var tools = JsonSerializer.SerializeToNode(await AppEndpoint.Tools(new AppHealth(db, config), app, default))!.AsArray();
            var tool = tools.Single(t => (string?)t!["name"] == "app_sync")!;
            Assert.Equal(["adapter"], tool["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key));
            Assert.False((bool)tool["annotations"]!["readOnlyHint"]!);
        }
        finally
        {
            await scheduler.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        Assert.True(scheduler.ExecuteTask!.IsCompletedSuccessfully || scheduler.ExecuteTask.IsCanceled);
    }

    // A batch on the adapter's own clock, held up in the scheduler: a request meanwhile is refused as running, and the
    // list says it runs, until the batch ends; then one is accepted.
    [PostgresFact] public async Task A_scheduled_batch_refuses_a_request_while_it_runs()
    {
        TestSettings.Use();
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-syncnow-" + Guid.NewGuid().ToString("N")[..10]);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new SyncStandIns.Feed("slow") { Work = ct => held.Task.WaitAsync(ct) };
        var registry = new AdapterRegistry([slow]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new NoMap(), registry);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var syncs = new SyncRequests(registry, db);
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(fixture.Source), new Principals(fixture.Source), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, new LoggedLines<Scheduler>(), syncs);
        try
        {
            await scheduler.StartAsync(default);
            // The start's own batch, held.
            await Until(() => Task.FromResult(slow.Calls == 1));
            var refused = (await syncs.Request(app, "slow", default))!;
            Assert.Equal((false, "running", true), ((bool)refused["accepted"]!, (string?)refused["refused"], (bool)refused["adapter"]!["running"]!));
            Assert.True((bool)(await syncs.List(app, default))["adapters"]![0]!["running"]!);
            held.SetResult();
            await Until(async () => (bool?)(await syncs.List(app, default))["adapters"]![0]!["running"] == false);
            Assert.True((bool)(await syncs.Request(app, "slow", default))!["accepted"]!);
            await Until(() => Task.FromResult(slow.Calls == 2));
        }
        finally
        {
            held.TrySetResult();
            await scheduler.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    // A request clock that moves on an hour at a time, so no adapter's own limit refuses the next request.
    sealed class Hours : TimeProvider
    {
        long hours;
        public void Next() => Interlocked.Increment(ref hours);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(Interlocked.Read(ref hours));
    }

    // Requests for one adapter after another, each batch running past the clock's minute, never keep the clock's own jobs
    // from running: once a batch has run past the minute, what waits runs in the next minute's wait, after them. Here the
    // minute is a second, two adapters' batches take a second and a half each, and someone asks for whichever of them is
    // free all the while; a third adapter's own sync, due every second, still runs, and so do the asked-for batches.
    [PostgresFact] public async Task Asked_batches_one_after_another_never_keep_the_clocks_own_jobs_from_running()
    {
        TestSettings.Use();
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-syncnow-" + Guid.NewGuid().ToString("N")[..10]);
        var first = new SyncStandIns.Feed("first") { Work = ct => Task.Delay(1500, ct) };
        var second = new SyncStandIns.Feed("second") { Work = ct => Task.Delay(1500, ct) };
        var tick = new SyncStandIns.Feed("tick", TimeSpan.FromSeconds(1));
        var registry = new AdapterRegistry([first, second, tick]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new NoMap(), registry);
        var app = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var hours = new Hours();
        var syncs = new SyncRequests(registry, (_, _, _) => Task.FromResult(true), hours);
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(fixture.Source), new Principals(fixture.Source), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, new LoggedLines<Scheduler>(), syncs,
            TimeSpan.FromSeconds(1));
        using var stopAsking = new CancellationTokenSource();
        var asking = Task.CompletedTask;
        try
        {
            await scheduler.StartAsync(default);
            await Until(() => Task.FromResult(first.Calls >= 1 && second.Calls >= 1 && tick.Calls >= 1));
            asking = Task.Run(async () =>
            {
                while (!stopAsking.IsCancellationRequested)
                {
                    hours.Next();
                    await syncs.Request(app, "first", default);
                    await syncs.Request(app, "second", default);
                    await Task.Delay(50);
                }
            });
            var batches = first.Calls + second.Calls;
            await Until(() => Task.FromResult(first.Calls + second.Calls >= batches + 2));
            var ticks = tick.Calls;
            batches = first.Calls + second.Calls;
            await Until(() => Task.FromResult(tick.Calls >= ticks + 2), 20);
            await Until(() => Task.FromResult(first.Calls + second.Calls >= batches + 2));
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
        }
        finally
        {
            stopAsking.Cancel();
            await asking;
            await scheduler.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [PostgresFact] public async Task With_scopes_on_a_caller_sees_and_asks_only_the_adapters_whose_owners_rows_it_reads()
    {
        var config = TestSettings.Use();
        var registry = new AdapterRegistry([
            new SyncStandIns.Feed("alex-feed", owner: "alex", providers: "cgm"),
            new SyncStandIns.Folder("family-folder", RhythmConfig.Household),
            new SyncStandIns.Feed("fictional", owner: RhythmConfig.Synthetic, providers: "demo"),
            new SyncStandIns.Folder("unstated")]);
        var db = new Database(fixture.Source, registry.Policies(config));
        // The shared app token's principal (the household view) first: a new token ends every bound one.
        var household = (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
        var alex = await Scoped.Principal(fixture.Source, "sync-alex", "app", "alex");
        var sam = await Scoped.Principal(fixture.Source, "sync-sam", "app", "sam");
        async Task<string[]> Seen(Guid principal) =>
            [.. (await new SyncRequests(registry, db).List(principal, default))["adapters"]!.AsArray().Select(a => (string)a!["adapter"]!)];
        string[] all = ["alex-feed", "family-folder", "fictional"];
        // Off, as every install is today: the app reads everything, so every login sees every adapter, one that states
        // no owner included.
        string[] offList = [.. all, "unstated"];
        foreach (var reader in new[] { household, alex, sam }) Assert.Equal(offList, await Seen(reader));
        try
        {
            await Scoped.Mode(fixture.Source, "on");
            await Scoped.Members(fixture.Source, "alex", "sam");
            // With scopes on, one that states no owner is shown to no one: it fails closed.
            Assert.Equal(all, await Seen(alex));
            Assert.Equal(["family-folder", "fictional"], await Seen(sam));
            Assert.Equal(["family-folder", "fictional"], await Seen(household));
            // Asking for an adapter the caller doesn't read answers as an unknown one, and queues nothing.
            var syncs = new SyncRequests(registry, db);
            Assert.Null(await syncs.Request(sam, "alex-feed", default));
            Assert.Null(await syncs.Request(household, "unstated", default));
            Assert.Null(await syncs.Request(alex, "unstated", default));
            Assert.Null(await syncs.Next(SyncStandIns.Soon, default));
            Assert.True((bool)(await syncs.Request(alex, "alex-feed", default))!["accepted"]!);
            // A source share of Alex's provider lets Family read those rows, so Family's readers see the adapter too.
            await Scoped.Execute(fixture.Source, "INSERT INTO source_shares(person_id,provider) VALUES('alex','cgm')");
            Assert.Equal(all, await Seen(sam));
            Assert.Equal(all, await Seen(household));
            // A disabled login sees nothing.
            await Scoped.Execute(fixture.Source, "UPDATE principals SET enabled=false WHERE id=@p", ("p", sam));
            Assert.Empty(await Seen(sam));
        }
        finally
        {
            await Scoped.Execute(fixture.Source, "DELETE FROM source_shares WHERE person_id='alex'");
            await Scoped.Mode(fixture.Source, "off");
        }
    }
}
