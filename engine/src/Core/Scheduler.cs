using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Relationships;

namespace Knowledge;

// The engine's own clock, replacing host cron: the map rebuild every graph.refreshSeconds, the "refresh now" requests
// within a minute, each MeasurementSource adapter's sync and each ImportSource adapter's import on its interval, the
// app token's file, the embeddings index when it is turned on, and the daily turn's jobs through the nightly job runner
// (NightlyRunner, SB-P5-14): pruning of old ingest keys, screen commands and screens, of native memories in the trash
// past their retention and of the opt-in recall log, and with memory.tools on the memory staleness pass (SB-P4-09), the
// memory link pruning pass (SB-P4-04) and the insight candidate pass (SB-P5-05, which reads the live embeddings model)
// after them, each under its own budget, so one failing never keeps the next from running, a job of the turn that
// waited for the embeddings model running once more as soon as the model is on; between them, the one batch
// now that someone asked an adapter for from the app (SyncRequests). Waits for the migrate service before doing
// anything. One process, no queue.
public sealed class Scheduler(EngineState state, Database db, RelationshipHost host, RefreshRequests requests, Principals principals,
    AdapterRegistry adapters, Ingest ingest, Embeddings embeddings, RhythmConfig config, ILogger<Scheduler> log,
    SyncRequests? syncRequests = null, TimeSpan? minute = null, IReadOnlyList<NightlyJob>? nightlyJobs = null) : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);
    readonly SyncRequests syncs = syncRequests ?? new SyncRequests(adapters, db);
    // The clock's turn: Poll, shorter only in tests.
    readonly TimeSpan poll = minute ?? Poll;
    // The daily turn's jobs (NightlyJobs.Daily, with those its settings turn on; a test gives its own list), and whether
    // the night summary is kept.
    readonly NightlyRunner nightly = new(db.Source, nightlyJobs ?? NightlyJobs.Daily(db.Source, log, config, embeddings), log,
        NightlySettings.From(config).Summary, config.Zone);

    // The daily turn's jobs in their order, as this scheduler runs them (a test reads the list its settings make).
    internal IReadOnlyList<NightlyJob> DailyJobs => nightly.Jobs;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Task Guard(string job, Func<Task> work) => Scheduler.Guard(log, job, work, stop);
        // What a rebuild could not read or left out (a source down, the catalog past its budget) is never silent.
        void Noted() { foreach (var note in host.Notes) log.LogWarning("Map rebuild: {Note}", note); }
        while (!stop.IsCancellationRequested && !await Ready(stop)) await Task.Delay(TimeSpan.FromSeconds(5), stop);
        if (stop.IsCancellationRequested) return;
        if (!config.Demo.Enabled)
            await Guard("demo retirement", async () =>
            {
                if (await Adapters.Demo.DemoAdapter.Retire(db, stop) > 0) log.LogInformation("Demo off: removed its unchanged dashboards and insight");
            });
        _ = Task.Run(() => embeddings.Start(config, state.DataDirectory, stop), stop);
        var nextGraph = DateTimeOffset.UtcNow; var nextPrune = DateTimeOffset.UtcNow;
        // A rebuild while the embeddings model is still loading (or downloading, on a first start) has no vectors, so
        // no similar links: once the model is on, one more rebuild is queued rather than waiting for the timer.
        var vectorless = false; var similar = config.Graph.Similarity.Threshold is not null;
        // The same for a map model (graph.similarity.model, D-256): a rebuild that drew its similar links with the live
        // model's vectors because the map model was not loaded yet is followed by one more once it is.
        var mapless = false;
        // Likewise the daily turn's jobs that waited for the model (the insight candidate pass, which the engine's start
        // finds the model still loading for): each runs once more as soon as the model is on, rather than at the next turn
        // a day later, so an engine restarted every day still runs them. The turn keeps its time.
        string[] waited = [];
        var nextSync = adapters.Measurements.ToDictionary(a => a.Name, _ => DateTimeOffset.UtcNow);
        var nextImport = adapters.Imports.ToDictionary(a => a.Name, _ => DateTimeOffset.UtcNow);
        // The embeddings index (adapters.embeddings.backfill, .sources and .shadow, all off for an existing install): one
        // bounded run per model and minute, once that model has loaded (EmbeddingBackfill). Passages of texts the settings
        // no longer list are removed first, tried again each minute until that has finished (counts only in the log);
        // search serves none of them meanwhile.
        var indexing = EmbeddingSettings.From(config);
        var index = new EmbeddingBackfill(db);
        var forgotten = false;
        async Task Forget() => await Guard("embeddings index", async () =>
        {
            if (await EmbeddingBackfill.Forget(db, PassageSources.For(indexing, adapters.Enabled, false), stop) is var gone and > 0)
                log.LogInformation("Embeddings index: removed {Passages} passages of texts the settings no longer list", gone);
            forgotten = true;
        });
        await Forget();
        while (!stop.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            await Guard("app token", () => principals.SyncApp(SecretFiles.TryRead("RHYTHM_APP_TOKEN_FILE"), stop));
            foreach (var adapter in adapters.Measurements.Where(a => nextSync[a.Name] <= now))
            {
                nextSync[adapter.Name] = now + adapter.Interval;
                using (syncs.Runs(adapter.Name))
                    await Guard($"{adapter.Name} sync", () => adapter.Sync(new MeasurementSink(ingest, adapter), stop));
            }
            foreach (var adapter in adapters.Imports.Where(a => nextImport[a.Name] <= now))
            {
                nextImport[adapter.Name] = now + adapter.Interval;
                using (syncs.Runs(adapter.Name))
                    await Guard($"{adapter.Name} import", () => adapter.Import(stop));
            }
            if (vectorless && embeddings.Model is not null || mapless && embeddings.Map?.Model is not null && embeddings.Model is not null)
            {
                vectorless = false; mapless = false;
                await Guard("rebuild with the embeddings model", () => requests.Queue(stop));
            }
            if (await Claim(stop) is { } request)
            {
                vectorless |= similar && embeddings.Model is null;
                var outcome = "failed";
                await Guard("requested rebuild", async () => outcome = await host.Refresh(false, stop));
                mapless |= host.MapFellBack;
                Noted();
                await Guard("requested rebuild", () => requests.Finish(request, outcome, stop));
                if (outcome == "complete") nextGraph = now + TimeSpan.FromSeconds(config.Graph.RefreshSeconds);
            }
            else if (nextGraph <= now)
            {
                nextGraph = now + TimeSpan.FromSeconds(config.Graph.RefreshSeconds);
                vectorless |= similar && embeddings.Model is null;
                await Guard("scheduled rebuild", async () => log.LogInformation("Map rebuild: {Outcome}", await host.Refresh(true, stop)));
                mapless |= host.MapFellBack;
                Noted();
            }
            if (!forgotten) await Forget();
            // The index runs only once the removal has finished, so it never works beside passages that are going.
            foreach (var (role, model, covered) in forgotten ? EmbeddingBackfill.Targets(indexing, embeddings.Model, embeddings.Shadow?.Model, adapters.Enabled) : [])
                await Guard($"embeddings {role} index", async () =>
                {
                    var run = await index.Step(model, stop, sources: covered);
                    if (run.Worth) log.LogInformation("Embeddings {Role} index ({Model}): {Indexed} indexed, {Failed} failed, {Left} left",
                        role, run.Model, run.Indexed, run.Failed, run.Left);
                });
            // The daily turn, at the engine's start and every 24 hours after, as it always was: its jobs in order
            // (NightlyJobs.Daily: ingest keys, screens, the memory trash, the recall log, and with memory.tools on the memory
            // staleness pass, the memory link pruning pass and the insight candidate pass after them), each under its own
            // budget, their failures logged by kind and never stopping the clock (NightlyRunner). A job that waited for the
            // embeddings model runs again, alone, at the first minute the model is on (NightlyRunner.Again).
            if (nextPrune <= now)
            {
                nextPrune = now + TimeSpan.FromHours(24);
                waited = [.. (await nightly.Run(stop)).Where(o => o.Waited).Select(o => o.Job)];
                // Then an offboarding the operator started (rhythm person offboard, SB-P7-13) is closed as far as it can be,
                // once the trash's purge above has taken what it moved there: the person's emptied private projects go, and
                // it is recorded finished when nothing of theirs is left. It starts none and moves nothing to the trash;
                // with no offboarding, every install's state, it reads one query and logs nothing.
                await Guard("offboarding", () => Offboardings.Daily(db.Source, log, stop));
            }
            if (waited.Length > 0 && embeddings.Model is not null)
                waited = [.. (await nightly.Again(waited, stop)).Where(o => o.Waited).Select(o => o.Job)];
            // The rest of the minute: a batch someone asked an adapter for from the app runs as soon as it is asked for,
            // here between the clock's own jobs and never beside one (SyncRequests). With none asked for, this is the
            // minute's wait as it always was. Once a batch has run past the minute, the next waits for the next minute,
            // after the clock's own jobs, so asked-for batches never keep those from running.
            try
            {
                var waiting = System.Diagnostics.Stopwatch.GetTimestamp();
                while (await syncs.Next(poll - System.Diagnostics.Stopwatch.GetElapsedTime(waiting), stop) is { } asked)
                    syncs.Finish(asked, await SyncRequests.Run(asked, new MeasurementSink(ingest, asked.Adapter), log, stop));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
    }

    async Task<bool> Ready(CancellationToken stop)
    {
        try
        {
            if (!await db.Current(stop)) { log.LogWarning("Waiting for the migrate service: the database schema is not current"); return false; }
            var started = await principals.Install(config, stop);
            var (former, memberships, shares) = started;
            // Counts only: who left is in rhythm.yaml's history, never in the log.
            if (former + memberships + shares + started.Tokens > 0)
                log.LogInformation("People taken out of rhythm.yaml: {Former} marked former, {Memberships} Family memberships, {Shares} source shares and {Tokens} tokens ended",
                    former, memberships, shares, started.Tokens);
            if (started.SwitchedOn)
                log.LogInformation("Memory scopes switched on: recorded on in the database, {Joined} people added to Family", started.Joined);
            state.GraphPrincipal = await principals.System(Principals.GraphName, stop);
            await principals.SyncApp(SecretFiles.TryRead("RHYTHM_APP_TOKEN_FILE"), stop);
            state.Ready = true;
            log.LogInformation("Engine ready: {People} people, time zone {Zone}, {Adapters} adapters on", config.People.Count, config.Timezone, adapters.Enabled.Count);
            return true;
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException
            || e is OperationCanceledException && !stop.IsCancellationRequested)
        { log.LogWarning("Waiting for the database ({Kind})", e.GetType().Name); return false; }
    }
    async Task<Guid?> Claim(CancellationToken stop)
    {
        try { return await requests.Claim(stop); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException
            || e is OperationCanceledException && !stop.IsCancellationRequested) { return null; }
    }
    // One job's failure is logged by kind only (never a payload, URL or secret) and never stops the clock. Only the
    // engine stopping ends it: a job's own time limit (an HTTP timeout, the map rebuild's budget) is a failure like
    // any other, never taken for shutdown.
    internal static async Task Guard(ILogger log, string job, Func<Task> work, CancellationToken stop)
    {
        try { await work(); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { log.LogWarning("{Job} timed out", job); }
        catch (Exception e) when (e is not OperationCanceledException)
        { log.LogWarning("{Job} failed ({Kind})", job, e is ConfigException ? e.Message : e.GetType().Name); }
    }
}

// What the HTTP side needs to know about the engine's start.
public sealed class EngineState(string dataDirectory)
{
    public string DataDirectory { get; } = dataDirectory;
    public volatile bool Ready;
    public Guid GraphPrincipal { get; set; }
}
