using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The nightly job runner (SB-P5-14, D-017; migration 050): the scheduler's daily turn runs its jobs through it, one after
// another in a fixed order, each with its own budget, so one job failing, running out of time or hanging never keeps the
// others from running. A job may keep a cursor (job_cursors), saved as it goes, so a run stopped part way (its budget
// spent, a failure, the engine stopping) goes on from there at its next run; and a job may run once a night, so a second
// daily turn the same night (after a restart) does not run it again once it has finished. With engine.nightSummary on,
// what each job did is kept a row a night (night_runs) and read with `rhythm night`; off, as by default, nothing is kept.
//
// The daily turn keeps its time (the engine's start, then every 24 hours) and its jobs keep their order, their log lines
// and what they delete (NightlyJobs.Daily). Later jobs are added to that list at its end. A job that waited for the
// embeddings model, which the engine's start finds still loading, is run once more as soon as the model is on (Again,
// from the scheduler); the turn keeps its time.

// engine.nightSummary (docs/configuration.md): off (absent) or on. Absent from every rhythm.yaml written before it, so on
// upgrade nothing is kept.
public sealed record NightlySettings(bool Summary)
{
    public static NightlySettings From(RhythmConfig config) => new(config.Engine.NightSummary is "on");

    // Why the setting is refused at start, naming the key, or null.
    public static string? Refusal(RhythmConfig config) =>
        config.Engine.NightSummary is null or "" or "off" or "on" ? null : "engine.nightSummary is off or on";
}

// What one run of a nightly job did: its counts, plain words to numbers, each read as "<number> <words>" (never a record's
// id, title or text), whether it stopped at its own bound with more left (partial), so its next run goes on, and whether
// it waited for the embeddings model, which was not loaded yet, and did nothing (Waited), so the scheduler runs it once
// more as soon as the model is on rather than at the next daily turn.
public sealed record NightlyRun(IReadOnlyDictionary<string, long> Counts, bool More = false, bool Waited = false)
{
    public static readonly NightlyRun Done = new(new Dictionary<string, long>());
    public static NightlyRun Of(params (string Words, long Count)[] counts) => new(counts.ToDictionary(c => c.Words, c => c.Count, StringComparer.Ordinal));
}

// One nightly job: its id (job_cursors, night_runs: lower case letters, digits and '-'), the name its log lines use, its
// budget for one run, and its work. A job run once a night is not started again the same night once it has finished
// (complete, not partial); every other job runs at each daily turn.
public sealed record NightlyJob(string Id, string Name, TimeSpan Budget, Func<NightlyContext, CancellationToken, Task<NightlyRun>> Run,
    bool OncePerNight = false);

// A job's place: the cursor it saved last for a scope ('' for the whole install), null on its first run or once it
// cleared it; Save keeps a new one at once. A job saves after each piece it has finished, with a token its budget does
// not cancel (the default), so a run its budget or a failure stops between pieces never does one twice and never skips
// one. A piece written in a transaction of its own saves its cursor in the same transaction (the overload taking it),
// so the two commit together and a process killed between them repeats nothing; a job that saves apart from its
// piece's transaction must keep each piece safe to do twice.
public sealed class NightlyContext
{
    readonly NpgsqlDataSource source;
    internal NightlyContext(NpgsqlDataSource source, string job, DateOnly night) { this.source = source; Job = job; Night = night; }

    public string Job { get; }
    // The install's date when the daily turn began.
    public DateOnly Night { get; }

    public async Task<JsonElement?> Cursor(string scope = "", CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand("SELECT cursor::text FROM job_cursors WHERE job=@j AND scope=@s");
        cmd.Parameters.AddWithValue("j", Job);
        cmd.Parameters.AddWithValue("s", scope);
        if (await cmd.ExecuteScalarAsync(ct) is not string text) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public async Task Save(JsonNode? cursor, string scope = "", CancellationToken ct = default)
    {
        await using var cmd = source.CreateCommand(SaveSql);
        Bind(cmd, scope, cursor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The cursor saved in the transaction of the piece it follows, so both commit or neither does.
    public async Task Save(NpgsqlConnection connection, NpgsqlTransaction transaction, JsonNode? cursor, string scope = "", CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(SaveSql, connection, transaction);
        Bind(cmd, scope, cursor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    const string SaveSql = """
        INSERT INTO job_cursors(job,scope,cursor,updated_at) VALUES(@j,@s,@c,now())
        ON CONFLICT(job,scope) DO UPDATE SET cursor=EXCLUDED.cursor,updated_at=now()
        """;

    void Bind(NpgsqlCommand cmd, string scope, JsonNode? cursor)
    {
        cmd.Parameters.AddWithValue("j", Job);
        cmd.Parameters.AddWithValue("s", scope);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = cursor?.ToJsonString() ?? (object)DBNull.Value });
    }

    internal async Task<bool> FinishedTonight(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT finished_night=@n FROM job_cursors WHERE job=@j AND scope=''");
        cmd.Parameters.AddWithValue("j", Job);
        cmd.Parameters.AddWithValue("n", Night);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    internal async Task Finished(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            INSERT INTO job_cursors(job,scope,finished_night,updated_at) VALUES(@j,'',@n,now())
            ON CONFLICT(job,scope) DO UPDATE SET finished_night=EXCLUDED.finished_night,updated_at=now()
            """);
        cmd.Parameters.AddWithValue("j", Job);
        cmd.Parameters.AddWithValue("n", Night);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// What a job did at one daily turn: its outcome (complete, partial, failed, timed out, still running), a failure's kind,
// its counts, whether it was started, its budget and its time, and whether it waited for the embeddings model.
public sealed record NightlyOutcome(string Job, string Name, int Position, string Outcome, string? Failure, IReadOnlyDictionary<string, long> Counts,
    bool Started, TimeSpan Budget, TimeSpan Elapsed, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Waited = false);

public sealed class NightlyRunner
{
    // Nights the summary keeps: tonight and the 29 before.
    public const int KeptNights = 30;
    // How long past its budget a job that does not stop is waited for before the runner goes on without it.
    public static TimeSpan Grace(TimeSpan budget) => budget < TimeSpan.FromSeconds(30) ? budget : TimeSpan.FromSeconds(30);

    readonly NpgsqlDataSource source;
    readonly ILogger log;
    readonly bool summary;
    readonly TimeZoneInfo zone;
    // A run that did not stop within its budget and grace, left to end on its own: its job is not started again until it
    // has (still running). In this process only.
    readonly Dictionary<string, Task> running = new(StringComparer.Ordinal);

    public IReadOnlyList<NightlyJob> Jobs { get; }

    public NightlyRunner(NpgsqlDataSource source, IReadOnlyList<NightlyJob> jobs, ILogger log, bool summary, TimeZoneInfo zone)
    {
        foreach (var job in jobs)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(job.Id, "^[a-z][a-z0-9-]{1,47}$")) throw new ArgumentException($"nightly job id {job.Id} is lower case letters, digits or '-'");
            if (job.Name.Length is < 1 or > 80) throw new ArgumentException($"nightly job {job.Id}: a name of 1 to 80 characters");
            if (job.Budget <= TimeSpan.Zero || job.Budget > TimeSpan.FromHours(1)) throw new ArgumentException($"nightly job {job.Id}: a budget over zero and at most an hour");
        }
        if (jobs.Count > 1000) throw new ArgumentException("at most 1000 nightly jobs");
        if (jobs.GroupBy(j => j.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new ArgumentException($"nightly job id {twice.Key} is used twice");
        (this.source, this.log, this.summary, this.zone, Jobs) = (source, log, summary, zone, jobs);
    }

    public static DateOnly Night(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    // One daily turn: each job in order, then the summary's pruning. Only the engine stopping ends it early.
    public async Task<IReadOnlyList<NightlyOutcome>> Run(CancellationToken stop, DateTimeOffset? at = null)
    {
        var night = Night(at ?? DateTimeOffset.UtcNow, zone);
        var outcomes = await Each(_ => true, night, stop);
        await Scheduler.Guard(log, "night summary pruning", () => Prune(night, stop), stop);
        if (summary && outcomes.Count > 0)
            log.LogInformation("Night {Night}: {Jobs} jobs, {Complete} complete, {Partial} partial, {Failed} failed, {TimedOut} timed out, {Running} still running",
                night.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), outcomes.Count, Count("complete"), Count("partial"),
                Count("failed"), Count("timed out"), Count("still running"));
        return outcomes;
        int Count(string outcome) => outcomes.Count(o => o.Outcome == outcome);
    }

    // The jobs named, in the turn's order, run once more between daily turns: the scheduler's catch-up for a job that
    // waited for the embeddings model, once the model has loaded. Each runs as it does in a turn (its budget, its cursor,
    // once a night if it is such a job) and is kept in the night's summary as one more run of the night; no pruning and
    // no night line, which belong to the turn.
    public async Task<IReadOnlyList<NightlyOutcome>> Again(IReadOnlyCollection<string> jobs, CancellationToken stop, DateTimeOffset? at = null) =>
        await Each(job => jobs.Contains(job.Id, StringComparer.Ordinal), Night(at ?? DateTimeOffset.UtcNow, zone), stop);

    // The jobs `which` names, in the turn's order, each kept in the night's summary when that is on.
    async Task<List<NightlyOutcome>> Each(Func<NightlyJob, bool> which, DateOnly night, CancellationToken stop)
    {
        var outcomes = new List<NightlyOutcome>();
        for (var position = 0; position < Jobs.Count; position++)
        {
            stop.ThrowIfCancellationRequested();
            if (!which(Jobs[position]) || await One(Jobs[position], position, night, stop) is not { } outcome) continue;
            outcomes.Add(outcome);
            if (summary) await Scheduler.Guard(log, "night summary", () => Record(night, outcome, stop), stop);
        }
        return outcomes;
    }

    // One job's run, or null when it runs once a night and has finished tonight. Its failure is logged by kind only, as the
    // scheduler's Guard logs one (never a payload, URL or secret); its own time limit is a failure like any other.
    async Task<NightlyOutcome?> One(NightlyJob job, int position, DateOnly night, CancellationToken stop)
    {
        var startedAt = DateTimeOffset.UtcNow;
        if (running.TryGetValue(job.Id, out var earlier))
        {
            if (!earlier.IsCompleted)
            {
                log.LogWarning("{Job} still running from an earlier turn: not started again", job.Name);
                return new(job.Id, job.Name, position, "still running", null, NightlyRun.Done.Counts, false, job.Budget, TimeSpan.Zero, startedAt, startedAt);
            }
            running.Remove(job.Id);
        }
        var context = new NightlyContext(source, job.Id, night);
        var clock = Stopwatch.GetTimestamp();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var abandoned = false;
        string outcome; string? failure = null; NightlyRun? run = null;
        try
        {
            if (job.OncePerNight && await context.FinishedTonight(stop)) return null;
            budget.CancelAfter(job.Budget);
            var work = Task.Run(() => job.Run(context, budget.Token), CancellationToken.None);
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var limit = Task.Delay(job.Budget + Grace(job.Budget), wait.Token);
                var first = await Task.WhenAny(work, limit);
                wait.Cancel();
                if (first != work)
                {
                    stop.ThrowIfCancellationRequested();
                    // It did not stop when its budget ran out: the others go on without it, and it is left to end on its own.
                    abandoned = true;
                    running[job.Id] = work.ContinueWith(t => { _ = t.Exception; budget.Dispose(); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw new OperationCanceledException(budget.Token);
                }
            }
            run = await work;
            if (job.OncePerNight && !run.More) await context.Finished(stop);
            outcome = run.More ? "partial" : "complete";
        }
        // Timed out only when its budget ran out (or it was left running past it); a job's own cancellation, such as a
        // client's timeout inside it, is a failure of its kind.
        catch (OperationCanceledException e) when (!stop.IsCancellationRequested)
        {
            if (abandoned || budget.IsCancellationRequested)
            {
                log.LogWarning("{Job} timed out", job.Name);
                outcome = "timed out";
            }
            else
            {
                log.LogWarning("{Job} failed ({Kind})", job.Name, e.GetType().Name);
                (outcome, failure) = ("failed", e.GetType().Name);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("{Job} failed ({Kind})", job.Name, e is ConfigException ? e.Message : e.GetType().Name);
            (outcome, failure) = ("failed", e.GetType().Name);
        }
        finally { if (!abandoned) budget.Dispose(); }
        return new(job.Id, job.Name, position, outcome, failure, run?.Counts ?? NightlyRun.Done.Counts, true, job.Budget,
            Stopwatch.GetElapsedTime(clock), startedAt, DateTimeOffset.UtcNow, run?.Waited ?? false);
    }

    // The night's row for this job: the latest run's outcome, failure and time; counts summed over the night's runs; how
    // often it was started; when it first came up.
    async Task Record(DateOnly night, NightlyOutcome o, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            INSERT INTO night_runs(night,job,position,name,outcome,failure,counts,runs,budget_ms,elapsed_ms,started_at,finished_at)
            VALUES(@night,@job,@position,@name,@outcome,@failure,@counts,@runs,@budget,@elapsed,@started,@finished)
            ON CONFLICT(night,job) DO UPDATE SET position=EXCLUDED.position,name=EXCLUDED.name,outcome=EXCLUDED.outcome,
              failure=EXCLUDED.failure,runs=night_runs.runs+EXCLUDED.runs,budget_ms=EXCLUDED.budget_ms,elapsed_ms=EXCLUDED.elapsed_ms,
              finished_at=EXCLUDED.finished_at,
              counts=(SELECT coalesce(jsonb_object_agg(k,coalesce((night_runs.counts->>k)::bigint,0)+coalesce((EXCLUDED.counts->>k)::bigint,0)),'{}'::jsonb)
                      FROM (SELECT jsonb_object_keys(night_runs.counts) UNION SELECT jsonb_object_keys(EXCLUDED.counts)) x(k))
            """);
        static int Ms(TimeSpan t) => (int)Math.Min(int.MaxValue, Math.Max(0, Math.Round(t.TotalMilliseconds)));
        cmd.Parameters.AddWithValue("night", night);
        cmd.Parameters.AddWithValue("job", o.Job);
        cmd.Parameters.AddWithValue("position", (short)o.Position);
        cmd.Parameters.AddWithValue("name", o.Name);
        cmd.Parameters.AddWithValue("outcome", o.Outcome);
        cmd.Parameters.AddWithValue("failure", (object?)o.Failure ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(o.Counts) });
        cmd.Parameters.AddWithValue("runs", o.Started ? 1 : 0);
        cmd.Parameters.AddWithValue("budget", Math.Max(1, Ms(o.Budget)));
        cmd.Parameters.AddWithValue("elapsed", Ms(o.Elapsed));
        cmd.Parameters.AddWithValue("started", o.StartedAt);
        cmd.Parameters.AddWithValue("finished", o.FinishedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    async Task Prune(DateOnly night, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("DELETE FROM night_runs WHERE night<=@oldest");
        cmd.Parameters.AddWithValue("oldest", night.AddDays(-KeptNights));
        if (await cmd.ExecuteNonQueryAsync(ct) is var gone and > 0) log.LogInformation("Night summary: {Rows} rows older than {Nights} nights pruned", gone, KeptNights);
    }
}

// The daily turn's jobs, in the order they have always run, each with the log lines it has always written. Their ids
// name them in job_cursors and night_runs; an id once used keeps its meaning. New jobs go at the end of the list, and a
// job behind a setting is in it only while that setting is on (Later), so with each such setting off the list is the
// four it always was.
//
// An offboarding's close (Offboardings.Daily, SB-P7-13) is not one of these jobs: the scheduler runs it right after the
// runner, in the same turn, so it comes after the trash purge it waits for, under the scheduler's own guard (a failure
// logged by kind, never stopping the clock). It walks no scope and keeps no cursor: one small transaction for each
// offboarding under way, of which an install has a handful at most, and with none, every install's state, one query.
// Listed here it would add a night_runs row and a `rhythm night` line every night on every install with
// engine.nightSummary on, offboarding or not, so it has no budget, no night summary row and no partial run of its own;
// what it did is in the engine's log, as counts.
public static class NightlyJobs
{
    // The budget of each of the daily jobs below: each deletes rows past their retention, bounded by its own limits.
    public static readonly TimeSpan DailyBudget = TimeSpan.FromMinutes(15);

    // embeddings: the engine's live model, which the insight candidate pass reads when it runs (none: it waits for one).
    public static IReadOnlyList<NightlyJob> Daily(NpgsqlDataSource source, ILogger log, RhythmConfig? config = null, Embeddings? embeddings = null) =>
    [
        new("ingest-keys", "ingest key pruning", DailyBudget, async (_, ct) =>
        {
            await using var cmd = source.CreateCommand("DELETE FROM ingest_calls WHERE received_at<now()-interval '90 days'");
            return NightlyRun.Of(("keys pruned", await cmd.ExecuteNonQueryAsync(ct)));
        }),
        new("screens", "screen pruning", DailyBudget, async (_, ct) =>
        {
            await ScreenStore.Prune(source, ct);
            return NightlyRun.Done;
        }),
        // The memory trash (SB-P3-06, MemoryTrash.Purge): native memories whose trash retention has passed are deleted for
        // good, a bounded number a run, and idempotency keys older than 30 days pruned. Nothing else: with no memory in the
        // trash, every install's state until a tool trashes one, it deletes nothing. A run that reached its bound is partial.
        new("memory-trash", "memory trash purge", DailyBudget, async (_, ct) =>
        {
            var purge = await MemoryTrash.Purge(source, ct);
            if (purge.Failed > 0)
                log.LogWarning("Memory trash: {Failed} memories past their retention could not be deleted and stay in the trash", purge.Failed);
            if (purge.Purged + purge.Keys > 0)
                log.LogInformation("Memory trash: {Purged} memories past their retention deleted for good, {Keys} old idempotency keys pruned",
                    purge.Purged, purge.Keys);
            return NightlyRun.Of(("deleted for good", purge.Purged), ("could not be deleted", purge.Failed), ("left for the next run", purge.Skipped),
                ("idempotency keys pruned", purge.Keys)) with { More = purge.More };
        }),
        // The opt-in recall log (SB-P2-11, RecallLog.Prune), whatever engine.recallLog says now, so a log turned off empties
        // itself: rows and counts older than 30 days. On an install that never turned it on, nothing. A run that pruned
        // its bound of either is partial.
        new("recall-log", "recall log pruning", DailyBudget, async (_, ct) =>
        {
            var (rows, counts) = await RecallLog.Prune(source, ct);
            if (rows + counts > 0) log.LogInformation("Recall log: {Rows} rows and {Counts} counts older than 30 days pruned", rows, counts);
            return NightlyRun.Of(("rows pruned", rows), ("counts pruned", counts)) with { More = rows >= RecallLog.Batch || counts >= RecallLog.Batch };
        }),
        .. Later(source, log, config, embeddings),
    ];

    // The jobs after the first four, each only while its setting is on. The memory staleness pass (SB-P4-09,
    // MemoryStaleness): only with memory.tools on, whose tools read the queue it keeps; then the link pruning pass
    // (SB-P4-04, MemoryLinkPruning), only with memory.tools on too, whose tools make and read the links it tends; then
    // the insight candidate pass (SB-P5-05, InsightCandidates), with memory.tools on as well, whose tools list and decide
    // the pairs it finds. Off, as by default, none is a job at all, so the daily turn, its log lines and the night
    // summary are as they were.
    static IEnumerable<NightlyJob> Later(NpgsqlDataSource source, ILogger log, RhythmConfig? config, Embeddings? embeddings)
    {
        if (config is null || !MemorySettings.ToolsOn(config)) yield break;
        yield return MemoryStaleness.Job(source, log);
        yield return MemoryLinkPruning.Job(source, log);
        yield return InsightCandidates.Job(source, log, embeddings, config);
    }
}

// One night's summary as `rhythm night` reads it: night_runs' rows for a night, in the night's order.
public sealed record NightRow(DateOnly Night, int Position, string Job, string Name, string Outcome, string? Failure,
    IReadOnlyList<(string Words, long Count)> Counts, int Runs, int BudgetMs, int ElapsedMs);

public static class NightSummary
{
    // The rows of a night, or of the latest night kept when none is named.
    public static async Task<IReadOnlyList<NightRow>> Read(NpgsqlDataSource source, DateOnly? night, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            SELECT night,position,job,name,outcome,failure,counts::text,runs,budget_ms,elapsed_ms FROM night_runs
            WHERE night=coalesce(@night,(SELECT max(night) FROM night_runs)) ORDER BY position,job
            """);
        cmd.Parameters.Add(new NpgsqlParameter("night", NpgsqlDbType.Date) { Value = night is { } n ? n : DBNull.Value });
        var rows = new List<NightRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var counts = JsonDocument.Parse(reader.GetString(6));
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetInt16(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                [.. counts.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).OrderBy(p => p.Name, StringComparer.Ordinal)],
                reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9)));
        }
        return rows;
    }

    // The summary in plain words: a line for the night, then a line a job. Names and words are the engine's own, never a
    // record's; they are cleaned for the terminal all the same.
    public static IReadOnlyList<string> Lines(IReadOnlyList<NightRow> rows)
    {
        if (rows.Count == 0) return ["No night summary is kept yet: the daily jobs keep one from their first turn with engine.nightSummary on."];
        var day = rows[0].Night.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var outcomes = new[] { "complete", "partial", "failed", "timed out", "still running" }
            .Select(o => (o, rows.Count(r => r.Outcome == o))).Where(p => p.Item2 > 0).Select(p => $"{p.Item2} {p.o}");
        var lines = new List<string> { $"Night of {day} (the install's date when the daily jobs began): {rows.Count} jobs, {string.Join(", ", outcomes)}." };
        foreach (var r in rows)
        {
            var what = r.Outcome switch
            {
                "failed" => $"failed ({r.Failure ?? "unknown"}) after {Time(r.ElapsedMs)} of its {Time(r.BudgetMs)}",
                "timed out" => $"timed out: its {Time(r.BudgetMs)} ran out",
                "still running" => "still running from an earlier turn, not started again",
                "partial" => $"partial, more left for its next run: {Time(r.ElapsedMs)} of its {Time(r.BudgetMs)}",
                _ => $"complete in {Time(r.ElapsedMs)} of its {Time(r.BudgetMs)}",
            };
            var runs = r.Runs > 1 ? $" ({r.Runs} runs tonight)" : "";
            // What it did, the counts it kept that are not zero.
            var done = r.Counts.Where(c => c.Count != 0).Select(c => $"{c.Count} {Cli.MemoryCommands.Shown(c.Words, 80)}").ToArray();
            var counts = done.Length == 0 ? "" : "; " + string.Join(", ", done);
            lines.Add($"  {Cli.MemoryCommands.Shown(r.Name, 80)}: {what}{runs}{counts}");
        }
        return lines;
    }

    // A time in plain words: seconds to one decimal under ten, whole seconds under a minute, else minutes.
    public static string Time(int ms) => ms switch
    {
        < 10_000 => (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s",
        < 60_000 => (ms / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s",
        _ => (ms / 60_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " min",
    };
}
