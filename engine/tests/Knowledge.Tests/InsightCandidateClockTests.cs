using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The insight candidate pass on the engine's own clock (Scheduler, SB-P5-05): the engine's start runs the daily turn at
// once, while the embeddings model is still loading, so the pass waits; once the model is on, the scheduler runs that pass
// once more within the minute, and only it, rather than at the next turn a day later, so an engine restarted every day
// still writes its pairs. The model's loading is the test's own (adapters.embeddings off: the engine loads and downloads
// nothing), from the pinned MiniLM files (D-041: skipped without them). Fictional people only; every text is DEMO. Its
// own database.
[Collection(SharedStateCollection.Name)]
public sealed class InsightCandidateClockTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The map: nothing to list, its budget spent at once.
    sealed class NoMap : IRelationshipSource
    {
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct)
        {
            using var budget = new CancellationTokenSource();
            budget.Cancel();
            return Task.FromCanceled<DiscoveryBatch>(budget.Token);
        }
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => Task.FromResult(SourceCheck.Unavailable);
    }

    [ScopeModelFact] public async Task A_pass_that_waited_for_the_model_at_the_start_runs_once_the_model_is_on()
    {
        TestSettings.Use();
        var s = fixture.Source;
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "insight-clock");
        var store = new MemoryStore(s);
        // Two weighty memories of one scope, 60 days apart: a pair whatever the model scores their words.
        var old = (await store.Create(who.Alex, new("DEMO: the Pellow boiler failed twice in January.", Importance: 4), Insights.Mcp, default)).Id;
        await Insights.Written(s, old, 60);
        var fresh = (await store.Create(who.Alex, new("DEMO: the Pellow energy bills fell by a third.", Importance: 3), Insights.Mcp, default)).Id;

        // memory.tools on, the night summary kept, and the model left to the test.
        var config = NightlyConfig.With("on", "\nmemory: { tools: on }\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-insight-clock-" + Guid.NewGuid().ToString("N")[..10]);
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new NoMap(), registry);
        var log = new LoggedLines<Scheduler>();
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(s), new Principals(s), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, log, minute: TimeSpan.FromMilliseconds(200));
        const string Waiting = "Insight candidates: waiting for the embeddings model";
        const string Added = "Insight candidates: 2 memories reached, 1 candidates added, 0 no longer eligible";
        // Each job's runs and the insight pass's counts, summed over the nights the test saw (a turn just before the
        // install's midnight and its catch-up just after are two nights' rows).
        Task<object?> Night() => MemoryRows.Scalar(s, """
            SELECT (SELECT string_agg(job||' '||runs,'|' ORDER BY position) FROM (SELECT job,min(position) position,sum(runs) runs FROM night_runs GROUP BY job) x)
              ||'|'||(SELECT sum((counts->>'runs waiting for the embeddings model')::int)||' waited, '||coalesce(sum((counts->>'candidates added')::int),0)||' added'
                      FROM night_runs WHERE job='insight-candidates')
            """);
        try
        {
            await scheduler.StartAsync(default);
            bool Turned() => log.Lines.Any(l => l.StartsWith("Night ", StringComparison.Ordinal));
            for (var waited = 0; waited < 300 && !Turned() && !scheduler.ExecuteTask!.IsCompleted; waited++) await Task.Delay(100);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
            Assert.Contains(Waiting, log.Lines);
            // Without the model the clock turns on and the pass is not run again: nothing is written.
            await Task.Delay(1000);
            Assert.Empty(await Insights.Rows(s));
            Assert.Single(log.Lines, l => l == Waiting);
            var jobs = scheduler.DailyJobs.Select(j => j.Id).ToArray();
            Assert.Equal(InsightCandidates.JobId, jobs[^1]);
            Assert.Equal(string.Join("|", jobs.Select(j => j + " 1")) + "|1 waited, 0 added", await Night());

            // The model is on: the pass runs at the clock's next turn, alone, and writes its pair.
            Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
            for (var waited = 0; waited < 300 && (await Insights.Rows(s)).Count == 0; waited++) await Task.Delay(100);
            var pair = Assert.Single(await Insights.Rows(s));
            Assert.Equal((old, fresh, "pending"), (pair.Older, pair.Newer, pair.Status));
            Assert.Contains(InsightCandidates.Weighty, pair.Signals);
            // Once, and the turn keeps its time: every other job ran once, the pass twice, a single night line.
            await Task.Delay(1000);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
            Assert.Equal(string.Join("|", jobs.Select(j => j + (j == InsightCandidates.JobId ? " 2" : " 1"))) + "|1 waited, 1 added", await Night());
            Assert.Single(log.Lines, l => l == Added);
            Assert.Single(log.Lines, l => l == Waiting);
            Assert.Single(log.Lines, l => l.StartsWith("Night ", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Lines, l => l.Contains("DEMO", StringComparison.Ordinal) || l.StartsWith("insight candidate accrual", StringComparison.Ordinal));
        }
        finally
        {
            await scheduler.StopAsync(default);
            await Scoped.Mode(s, "off");
            if (Directory.Exists(data)) Directory.Delete(data, true);
            TestSettings.Use();
        }
    }
}
