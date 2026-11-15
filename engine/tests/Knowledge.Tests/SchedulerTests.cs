using Knowledge.Adapters;
using Knowledge.Relationships;
using Microsoft.Extensions.Logging;

namespace Knowledge.Tests;

// What the scheduler logged: its own lines only, as the engine writes them.
internal sealed class LoggedLines<T> : ILogger<T>
{
    readonly List<string> lines = [];
    public string[] Lines { get { lock (lines) return [.. lines]; } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { lock (lines) lines.Add(formatter(state, exception)); }
}

public sealed class SchedulerGuardTests
{
    // An HTTP client's timeout and a rebuild's own budget both throw OperationCanceledException while the engine runs
    // on: each is one job's failure. Only the engine stopping ends the clock.
    [Fact] public async Task A_job_that_runs_out_of_time_is_a_failure_and_only_stopping_ends_the_clock()
    {
        var log = new LoggedLines<Scheduler>();
        await Scheduler.Guard(log, "garmin sync", () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"), default);
        using (var budget = new CancellationTokenSource())
        {
            budget.Cancel();
            await Scheduler.Guard(log, "scheduled rebuild", () => Task.FromCanceled(budget.Token), default);
        }
        await Scheduler.Guard(log, "csv sync", () => throw new IOException("a file in use"), default);
        Assert.Equal(["garmin sync timed out", "scheduled rebuild timed out", "csv sync failed (IOException)"], log.Lines);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.Guard(log, "garmin sync", () => Task.FromCanceled(stop.Token), stop.Token));
        Assert.Equal(3, log.Lines.Length);
    }
}

// The engine's clock against a real database: a sync that times out and a requested rebuild that runs past its budget
// are logged, the request is finished as failed (so the next one can be claimed), and the clock keeps running.
[Collection(SharedStateCollection.Name)]
public sealed class SchedulerTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    sealed class TimingOut : IMeasurementSource
    {
        public int Calls;
        public string Name => "timing-out";
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
        public TimeSpan Interval => TimeSpan.FromMinutes(1);
        public Task Sync(MeasurementSink sink, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        }
    }
    sealed class Counting : IImportSource
    {
        public int Calls;
        public string Name => "counting";
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
        public TimeSpan Interval => TimeSpan.FromMinutes(1);
        public Task Import(CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }
    // A rebuild whose own budget runs out while listing.
    sealed class OverBudget : IRelationshipSource
    {
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct)
        {
            using var budget = new CancellationTokenSource();
            budget.Cancel();
            return Task.FromCanceled<DiscoveryBatch>(budget.Token);
        }
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => Task.FromResult(SourceCheck.Unavailable);
    }

    [PostgresFact] public async Task A_timed_out_sync_and_an_over_budget_rebuild_leave_the_engine_running()
    {
        TestSettings.Use();
        // The local model stays off: nothing is downloaded.
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-scheduler-" + Guid.NewGuid().ToString("N")[..10]);
        var timing = new TimingOut(); var counting = new Counting();
        var registry = new AdapterRegistry([timing, counting]);
        var db = new Database(fixture.Source, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new OverBudget(), registry);
        var requests = new RefreshRequests(fixture.Source);
        await requests.Queue(default);
        var log = new LoggedLines<Scheduler>();
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, requests, new Principals(fixture.Source), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, log);
        try
        {
            await scheduler.StartAsync(default);
            async Task<string?> Outcome()
            {
                await using var cmd = fixture.Source.CreateCommand("SELECT outcome FROM relationship_refresh_requests WHERE finished_at IS NOT NULL ORDER BY requested_at DESC LIMIT 1");
                return await cmd.ExecuteScalarAsync() as string;
            }
            for (var waited = 0; waited < 300 && await Outcome() is null && !scheduler.ExecuteTask!.IsCompleted; waited++) await Task.Delay(100);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
            Assert.Equal("failed", await Outcome());
            Assert.Equal((1, 1), (timing.Calls, counting.Calls));
            Assert.Contains("timing-out sync timed out", log.Lines);
            Assert.Contains("requested rebuild timed out", log.Lines);
        }
        finally
        {
            await scheduler.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        Assert.True(scheduler.ExecuteTask!.IsCompletedSuccessfully || scheduler.ExecuteTask.IsCanceled);
    }

    // The daily block runs this program's jobs on the engine's clock, from its first turn (MemoryTrash.Purge of SB-P3-06
    // and RecallLog.Prune of SB-P2-11, each tested on its own in MemoryTrashPurgeTests and RecallLogTests): a memory in
    // the trash past its retention is deleted for good and a recall log row older than 30 days dropped, while one trashed
    // yesterday, a live memory and a fresh log row stay, and neither job logs a failure.
    [PostgresFact] public async Task The_daily_block_purges_the_memory_trash_and_prunes_the_recall_log()
    {
        TestSettings.Use();
        var s = fixture.Source;
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-scheduler-" + Guid.NewGuid().ToString("N")[..10]);
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new OverBudget(), registry);

        // Native memories, scopes off: one trashed 20 days ago (past the 14 days), one trashed yesterday, one live; and the
        // opt-in log's rows, one from 31 days ago and one from an hour ago.
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "scheduler");
        var store = new MemoryStore(s);
        async Task<Guid> Trashed(string text, int days)
        {
            var id = (await store.Create(who.Alex, new(text), MemoryLogins.Mcp, default)).Id;
            await store.Forget(who.Alex, id, null, MemoryLogins.Mcp, default);
            await MemoryRows.Run(s, "UPDATE memory_trash SET trashed_at=now()-make_interval(days=>@d),purge_after=now()-make_interval(days=>@d)+interval '14 days' WHERE memory_id=@m",
                ("m", id), ("d", days));
            return id;
        }
        var expired = await Trashed("DEMO: trashed twenty days ago.", 20);
        var recent = await Trashed("DEMO: trashed yesterday.", 1);
        var live = (await store.Create(who.Alex, new("DEMO: still here."), MemoryLogins.Mcp, default)).Id;
        await MemoryRows.Run(s, """
            INSERT INTO recall_log(subject_id,channel,query,returned,receipt,at) VALUES
              ('alex','mcp','DEMO old question',ARRAY[@m]::uuid[],'rc1-00000000000000d1',now()-interval '31 days'),
              ('alex','mcp','DEMO new question',ARRAY[@m]::uuid[],'rc1-00000000000000d2',now()-interval '1 hour')
            """, ("m", live));
        async Task<string> Left() => (string)(await MemoryLogins.Scalar(s, """
            SELECT (SELECT count(*) FROM memories WHERE id=@e)||'|'||(SELECT count(*) FROM memories WHERE id=ANY(@k))
              ||'|'||(SELECT string_agg(query,',' ORDER BY at) FROM recall_log WHERE receipt IN ('rc1-00000000000000d1','rc1-00000000000000d2'))
            """, ("e", expired), ("k", new[] { recent, live })))!;
        Assert.Equal("1|2|DEMO old question,DEMO new question", await Left());

        var log = new LoggedLines<Scheduler>();
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(s), new Principals(s), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, log);
        try
        {
            await scheduler.StartAsync(default);
            bool Pruned() => log.Lines.Any(l => l.StartsWith("Recall log:", StringComparison.Ordinal));
            for (var waited = 0; waited < 300 && !Pruned() && !scheduler.ExecuteTask!.IsCompleted; waited++) await Task.Delay(100);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
            Assert.Equal("0|2|DEMO new question", await Left());
            Assert.Contains("Memory trash: 1 memories past their retention deleted for good, 0 old idempotency keys pruned", log.Lines);
            Assert.Contains("Recall log: 1 rows and 0 counts older than 30 days pruned", log.Lines);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("memory trash purge", StringComparison.Ordinal)
                || l.StartsWith("recall log pruning", StringComparison.Ordinal) || l.StartsWith("Memory trash: ", StringComparison.Ordinal) && l.Contains("could not"));
            Assert.Equal("purged|system|memory-trash", await MemoryLogins.Scalar(s,
                "SELECT event||'|'||channel||'|'||job FROM memory_events WHERE memory_id=@m AND event='purged'", ("m", expired)));
            // Through the nightly job runner (SB-P5-14) with engine.nightSummary absent: no summary is kept and, since none
            // of these jobs keeps a cursor or runs once a night, no cursor either; no summary line is logged.
            Assert.Equal("0|0", await MemoryLogins.Scalar(s, "SELECT (SELECT count(*) FROM night_runs)||'|'||(SELECT count(*) FROM job_cursors)"));
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("Night", StringComparison.Ordinal));
        }
        finally
        {
            await scheduler.StopAsync(default);
            await Scoped.Mode(s, "off");
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
