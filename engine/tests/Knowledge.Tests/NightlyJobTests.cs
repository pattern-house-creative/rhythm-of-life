using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Cli;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// The nightly job runner (SB-P5-14, Core/NightlyJobs.cs, migration 050): the scheduler's daily turn, each job under its
// own budget, a cursor kept per job and scope, once-a-night jobs, and the night summary behind engine.nightSummary.
static class NightlyConfig
{
    public static RhythmConfig With(string value, string extra = "") =>
        RhythmConfig.Parse(TestSettings.Yaml.Replace("engine: { legacySearch: false }", "engine: { legacySearch: false, nightSummary: " + value + " }") + extra);
    public static RhythmConfig On => With("on");
    public static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
}

public sealed class NightlyRunnerTests
{
    static Task<NightlyRun> Done(NightlyContext _, CancellationToken __) => Task.FromResult(NightlyRun.Done);

    // Absent, empty or off keeps nothing; on keeps the summary; anything else stops the engine at start naming the key.
    [Fact] public void The_night_summary_is_off_unless_it_says_on()
    {
        Assert.False(NightlySettings.From(TestSettings.Use()).Summary);
        Assert.False(NightlySettings.From(NightlyConfig.With("off")).Summary);
        Assert.False(NightlySettings.From(NightlyConfig.With("''")).Summary);
        Assert.True(NightlySettings.From(NightlyConfig.On).Summary);
        foreach (var value in new[] { "yes", "true", "daily" })
            Assert.Contains("engine.nightSummary is off or on", Assert.Throws<ConfigException>(() => NightlyConfig.With(value)).Message);
        TestSettings.Use();
        // The settings schema takes the key, off or on, so a rhythm.yaml that turns it on still validates.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "contracts", "rhythm.schema.json");
        var key = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("properties").GetProperty("engine")
            .GetProperty("properties").GetProperty("nightSummary");
        Assert.Equal(["off", "on", null], key.GetProperty("enum").EnumerateArray()
            .Select(v => v.ValueKind == System.Text.Json.JsonValueKind.Null ? null : v.GetString()));
        Assert.Equal("off", key.GetProperty("default").GetString());
    }

    // The daily turn's jobs are the ones it always ran, in the order it ran them, under the names their log lines use;
    // none runs once a night, so each runs at every daily turn as before.
    [Fact] public void The_daily_jobs_keep_their_order_and_names()
    {
        var daily = NightlyJobs.Daily(null!, new LoggedLines<Scheduler>());
        Assert.Equal(["ingest-keys", "screens", "memory-trash", "recall-log"], daily.Select(j => j.Id));
        Assert.Equal(["ingest key pruning", "screen pruning", "memory trash purge", "recall log pruning"], daily.Select(j => j.Name));
        Assert.All(daily, j => Assert.False(j.OncePerNight));
        Assert.All(daily, j => Assert.Equal(TimeSpan.FromMinutes(15), j.Budget));
    }

    // A list the summary and the cursors could not keep apart is refused when the runner is made.
    [Fact] public void The_runner_refuses_jobs_it_could_not_keep_apart()
    {
        var log = new LoggedLines<NightlyRunner>();
        NightlyRunner Make(params NightlyJob[] jobs) => new(null!, jobs, log, false, TimeZoneInfo.Utc);
        var second = TimeSpan.FromSeconds(1);
        Assert.Throws<ArgumentException>(() => Make(new NightlyJob("same-id", "one", second, Done), new NightlyJob("same-id", "two", second, Done)));
        Assert.Throws<ArgumentException>(() => Make(new NightlyJob("Not An Id", "one", second, Done)));
        Assert.Throws<ArgumentException>(() => Make(new NightlyJob("no-name", "", second, Done)));
        Assert.Throws<ArgumentException>(() => Make(new NightlyJob("no-budget", "one", TimeSpan.Zero, Done)));
        Assert.Throws<ArgumentException>(() => Make(new NightlyJob("long-budget", "one", TimeSpan.FromHours(2), Done)));
        Assert.Equal(2, Make(new NightlyJob("one-job", "one", second, Done), new NightlyJob("two-job", "two", second, Done)).Jobs.Count);
    }

    // A night is the install's own date when the turn began: half past midnight in London in summer is the next day.
    [Fact] public void A_night_is_the_installs_date_when_the_turn_began()
    {
        var at = DateTimeOffset.Parse("2026-06-30T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new DateOnly(2026, 7, 1), NightlyRunner.Night(at, NightlyConfig.London));
        Assert.Equal(new DateOnly(2026, 6, 30), NightlyRunner.Night(at, TimeZoneInfo.Utc));
    }

    // The summary reads as plain words: a line for the night, then a line a job with its outcome, its time against its
    // budget, how often it ran when more than once, and what it did (counts that are not zero).
    [Fact] public void The_summary_reads_as_plain_words()
    {
        var night = new DateOnly(2026, 11, 3);
        NightRow Row(int position, string name, string outcome, string? failure, int runs, int budget, int elapsed, params (string, long)[] counts) =>
            new(night, position, name.Replace(' ', '-'), name, outcome, failure, counts, runs, budget, elapsed);
        Assert.Equal([
            "Night of 2026-11-03 (the install's date when the daily jobs began): 6 jobs, 2 complete, 1 partial, 1 failed, 1 timed out, 1 still running.",
            "  ingest key pruning: complete in 0.1 s of its 15 min; 3 keys pruned",
            "  screen pruning: complete in 0.0 s of its 15 min (2 runs tonight)",
            "  memory trash purge: failed (NpgsqlException) after 2.5 s of its 15 min",
            "  insight candidates: partial, more left for its next run: 1 min of its 10 min; 250 pairs read, 4 written",
            "  slow job: timed out: its 30 s ran out",
            "  stuck job: still running from an earlier turn, not started again",
        ], NightSummary.Lines([
            Row(0, "ingest key pruning", "complete", null, 1, 900_000, 120, ("keys pruned", 3)),
            Row(1, "screen pruning", "complete", null, 2, 900_000, 40),
            Row(2, "memory trash purge", "failed", "NpgsqlException", 1, 900_000, 2500, ("deleted for good", 0)),
            Row(3, "insight candidates", "partial", null, 1, 600_000, 61_000, ("pairs read", 250), ("written", 4)),
            Row(4, "slow job", "timed out", null, 1, 30_000, 30_000),
            Row(5, "stuck job", "still running", null, 0, 30_000, 0),
        ]));
        Assert.Equal(["No night summary is kept yet: the daily jobs keep one from their first turn with engine.nightSummary on."], NightSummary.Lines([]));
        // What the engine wrote is cleaned for the terminal all the same.
        Assert.Equal("  a job: complete in 0.0 s of its 1.0 s; 1 rows", NightSummary.Lines([Row(0, "a\u0007 job‮", "complete", null, 1, 1000, 0, ("rows\u001b", 1))])[1]);
    }

    // With engine.nightSummary off, absent or empty, `rhythm night` answers exactly as a command the engine does not know,
    // before it reads its arguments or the database, and help is the list it was; on, help lists it.
    [Fact] public async Task With_the_summary_off_the_terminal_offers_no_night_command()
    {
        foreach (var config in new[] { TestSettings.Use(), NightlyConfig.With("off"), NightlyConfig.With("''") })
        {
            Assert.Equal(MemoryCommands.UnknownCommand, (await Assert.ThrowsAsync<ArgumentException>(() => NightCommand.Run([], new StringWriter(), config: config))).Message);
            Assert.Equal(MemoryCommands.UnknownCommand, (await Assert.ThrowsAsync<ArgumentException>(() => NightCommand.Run(["any", "words"], new StringWriter(), config: config))).Message);
        }
        Assert.Equal(CliCommands.Help(false), CliCommands.Help(false, night: false));
        Assert.DoesNotContain("  night ", CliCommands.Help(true));
        Assert.Contains("night", CliCommands.Names);
        // On, the list as it was with the night's line before the engine's own commands, its words in the others' column.
        var on = CliCommands.Help(false, night: true).Split('\n');
        var before = CliCommands.Help(false).Split('\n');
        string[] expected = [.. before[..^1], on[^2], before[^1]];
        Assert.Equal(expected, on);
        Assert.StartsWith("  night [--night <YYYY-MM-DD>] ", on[^2]);
        Assert.EndsWith("what the daily jobs did, a line a job (the latest night kept)", on[^2]);
        Assert.Equal(before.Single(l => l.StartsWith("  doctor ", StringComparison.Ordinal)).IndexOf("check settings", StringComparison.Ordinal),
            on[^2].IndexOf("what the daily jobs did", StringComparison.Ordinal));
        Assert.Equal(CliCommands.Help(true).Split('\n').Length + 1, CliCommands.Help(true, night: true).Split('\n').Length);
        // On, a night that is not a date is refused before the database is read.
        foreach (var args in new[] { new[] { "--night", "yesterday" }, ["--night"], ["2026-11-03"], ["--night", "2026-11-03", "--json"] })
            await Assert.ThrowsAsync<ArgumentException>(() => NightCommand.Run(args, new StringWriter(), config: NightlyConfig.On));
        TestSettings.Use();
    }
}

// Against a real database: every night of these tests is in November 2026, within the 30 nights kept, so no test prunes
// another's rows, and each test's jobs have ids of their own.
public sealed class NightlyRunnerDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);
    NightlyRunner Runner(IReadOnlyList<NightlyJob> jobs, bool summary, LoggedLines<NightlyRunner> log) => new(fixture.Source, jobs, log, summary, NightlyConfig.London);

    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);

    // One job failing, one running out of its budget and one that does not stop at all leave every other job running, at
    // this turn and the next; the one that does not stop is not started again until its run has ended. Failures are
    // logged by kind only. The night's summary keeps each job's latest outcome, its counts summed and how often it ran,
    // and `rhythm night` reads it.
    [PostgresFact] public async Task A_failing_timed_out_or_hanging_job_leaves_the_others_running()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangStarts = 0;
        NightlyJob Job(string id, Func<CancellationToken, Task<NightlyRun>> work, int budgetMs = 10_000) =>
            new(id, id.Replace('-', ' '), TimeSpan.FromMilliseconds(budgetMs), (_, ct) => work(ct));
        NightlyJob[] jobs =
        [
            Job("first-job", _ => Task.FromResult(NightlyRun.Of(("things done", 2)))),
            Job("failing-job", _ => throw new InvalidOperationException("DEMO a message that is never logged")),
            Job("slow-job", async ct => { await Task.Delay(Timeout.Infinite, ct); return NightlyRun.Done; }, 200),
            Job("hanging-job", async _ =>
            {
                Interlocked.Increment(ref hangStarts);
                await hang.Task;
                ended.TrySetResult();
                return NightlyRun.Of(("things done", 1));
            }, 200),
            Job("last-job", _ => Task.FromResult(NightlyRun.Of(("things done", 5)))),
        ];
        var log = new LoggedLines<NightlyRunner>();
        var runner = Runner(jobs, true, log);
        var night = At("2026-11-03T02:00:00Z");

        var first = await runner.Run(default, night);
        Assert.Equal(["complete", "failed", "timed out", "timed out", "complete"], first.Select(o => o.Outcome));
        Assert.Equal([null, "InvalidOperationException", null, null, null], first.Select(o => o.Failure));
        Assert.Equal(["failing job failed (InvalidOperationException)", "slow job timed out", "hanging job timed out",
            "Night 2026-11-03: 5 jobs, 2 complete, 0 partial, 1 failed, 2 timed out, 0 still running"], log.Lines);

        // The next turn the same night: the hanging run has not ended, so it is not started again; every other job runs.
        var second = await runner.Run(default, night.AddHours(1));
        Assert.Equal(["complete", "failed", "timed out", "still running", "complete"], second.Select(o => o.Outcome));
        Assert.Equal(1, hangStarts);
        Assert.Contains("hanging job still running from an earlier turn: not started again", log.Lines);

        // Once it has ended, the next turn starts it again.
        hang.SetResult();
        await ended.Task;
        await Task.Delay(300);
        Assert.Equal("complete", (await runner.Run(default, night.AddHours(2)))[3].Outcome);
        Assert.Equal(2, hangStarts);
        Assert.DoesNotContain(log.Lines, l => l.Contains("DEMO", StringComparison.Ordinal));

        Assert.Equal("first-job complete 3 6|failing-job failed 3 0|slow-job timed out 3 0|hanging-job complete 2 1|last-job complete 3 15", await Scalar("""
            SELECT string_agg(job||' '||outcome||' '||runs||' '||coalesce(counts->>'things done','0'),'|' ORDER BY position)
            FROM night_runs WHERE night='2026-11-03' AND job LIKE '%-job'
            """));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM night_runs WHERE counts::text LIKE '%DEMO%' OR coalesce(failure,'') LIKE '%DEMO%'"));

        // The summary, as `rhythm night` prints it.
        var o = new StringWriter();
        Assert.Equal(0, await NightCommand.Run(["--night", "2026-11-03"], o, fixture.Source, NightlyConfig.On));
        var lines = o.ToString().TrimEnd('\n', '\r').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(6, lines.Length);
        Assert.Equal("Night of 2026-11-03 (the install's date when the daily jobs began): 5 jobs, 3 complete, 1 failed, 1 timed out.", lines[0]);
        Assert.Matches(@"^  first job: complete in \d+\.\d s of its 10 s \(3 runs tonight\); 6 things done$", lines[1]);
        Assert.Matches(@"^  failing job: failed \(InvalidOperationException\) after \d+\.\d s of its 10 s \(3 runs tonight\)$", lines[2]);
        Assert.Equal("  slow job: timed out: its 0.2 s ran out (3 runs tonight)", lines[3]);
        Assert.Matches(@"^  hanging job: complete in \d+\.\d s of its 0\.2 s \(2 runs tonight\); 1 things done$", lines[4]);
        Assert.Matches(@"^  last job: complete in \d+\.\d s of its 10 s \(3 runs tonight\); 15 things done$", lines[5]);
        o = new StringWriter();
        Assert.Equal(1, await NightCommand.Run(["--night", "2026-01-01"], o, fixture.Source, NightlyConfig.On));
        Assert.Equal("No night summary is kept for 2026-01-01: the daily jobs keep the last 30 nights.", o.ToString().TrimEnd());
        TestSettings.Use();
    }

    // A job stopped part way goes on from its cursor: at its own bound (partial), at a failure and when its budget runs
    // out, ten synthetic items are each done once, in order, none skipped; finished, it clears its place. A cursor is kept
    // per scope.
    // A job that gives up on its own (a client's timeout inside it raises TaskCanceledException) well within its budget
    // failed, by that kind: it did not run out of time, and the summary never says it did.
    [PostgresFact] public async Task A_jobs_own_cancellation_is_a_failure_not_a_timeout()
    {
        var log = new LoggedLines<NightlyRunner>();
        NightlyJob[] jobs =
        [
            new("cancelled-job", "cancelled job", TimeSpan.FromSeconds(10), (_, _) => throw new TaskCanceledException()),
            new("after-job", "after job", TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(NightlyRun.Done)),
        ];
        var outcomes = await Runner(jobs, false, log).Run(default, At("2026-11-04T02:00:00Z"));
        Assert.Equal(["failed", "complete"], outcomes.Select(o => o.Outcome));
        Assert.Equal(["TaskCanceledException", null], outcomes.Select(o => o.Failure));
        Assert.Contains("cancelled job failed (TaskCanceledException)", log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains("timed out", StringComparison.Ordinal));
    }

    [PostgresFact] public async Task A_job_stopped_part_way_resumes_from_its_cursor()
    {
        var done = new List<int>();
        var mode = "bound";
        var job = new NightlyJob("item-walk", "item walk", TimeSpan.FromSeconds(10), async (context, ct) =>
        {
            var after = (await context.Cursor(ct: ct))?.GetProperty("after").GetInt32() ?? 0;
            var taken = 0;
            for (var item = after + 1; item <= 10; item++)
            {
                if (mode == "bound" && taken == 3) return NightlyRun.Of(("items done", taken)) with { More = true };
                if (mode == "fail" && item == 6) throw new IOException("DEMO a source went away");
                if (mode == "slow") await Task.Delay(150, ct);
                lock (done) done.Add(item);
                taken++;
                // Saved with no token the budget cancels, so a run stopped between items never does one twice.
                await context.Save(new JsonObject { ["after"] = item });
            }
            await context.Save(new JsonObject { ["seen"] = 10 }, "alex");
            await context.Save(null);
            return NightlyRun.Of(("items done", taken));
        });
        var log = new LoggedLines<NightlyRunner>();
        async Task<string> Turn(string how, int hour, TimeSpan? budget = null)
        {
            mode = how;
            return (await Runner([job with { Budget = budget ?? job.Budget }], false, log).Run(default, At("2026-11-05T00:00:00Z").AddHours(hour))).Single().Outcome;
        }
        async Task<object?> Cursor(string scope = "") => await Scalar("SELECT cursor::text FROM job_cursors WHERE job='item-walk' AND scope=@s", ("s", scope));

        Assert.Equal("partial", await Turn("bound", 1));
        Assert.Equal([1, 2, 3], done);
        Assert.Equal("{\"after\": 3}", await Cursor());
        Assert.Equal("failed", await Turn("fail", 2));
        Assert.Equal([1, 2, 3, 4, 5], done);
        Assert.Equal("{\"after\": 5}", await Cursor());
        Assert.Contains("item walk failed (IOException)", log.Lines);
        Assert.Equal("timed out", await Turn("slow", 3, TimeSpan.FromMilliseconds(400)));
        Assert.InRange(done.Count, 5, 9);
        Assert.Equal($"{{\"after\": {done[^1]}}}", await Cursor());
        Assert.Equal("complete", await Turn("plain", 4));
        Assert.Equal(Enumerable.Range(1, 10), done);
        Assert.Null(await Cursor());
        Assert.Equal("{\"seen\": 10}", await Cursor("alex"));
        // Its next run starts again from the first item.
        done.Clear();
        Assert.Equal("partial", await Turn("bound", 5));
        Assert.Equal([1, 2, 3], done);
    }

    // A job run once a night is not started again the same night once it has finished, but is when it stopped part way;
    // the next night it runs again. The night it last finished is kept on its row.
    [PostgresFact] public async Task A_job_run_once_a_night_runs_again_only_the_next_night_unless_it_stopped_part_way()
    {
        var runs = 0; var more = true;
        var job = new NightlyJob("once-nightly", "once nightly", TimeSpan.FromSeconds(10), (_, _) =>
        {
            runs++;
            return Task.FromResult(NightlyRun.Of(("passes", 1)) with { More = more });
        }, OncePerNight: true);
        var runner = Runner([job], false, new LoggedLines<NightlyRunner>());
        var night = At("2026-11-10T01:00:00Z");
        Assert.Equal("partial", (await runner.Run(default, night)).Single().Outcome);
        more = false;
        Assert.Equal("complete", (await runner.Run(default, night.AddHours(1))).Single().Outcome);
        Assert.Empty(await runner.Run(default, night.AddHours(2)));
        Assert.Equal(2, runs);
        Assert.Equal("2026-11-10", await Scalar("SELECT finished_night::text FROM job_cursors WHERE job='once-nightly' AND scope=''"));
        Assert.Equal("complete", (await runner.Run(default, night.AddDays(1))).Single().Outcome);
        Assert.Equal(3, runs);
        Assert.Equal("2026-11-11", await Scalar("SELECT finished_night::text FROM job_cursors WHERE job='once-nightly' AND scope=''"));
        // A job that never runs once a night or keeps a cursor writes no row.
        var plain = new NightlyJob("plain-nightly", "plain nightly", TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(NightlyRun.Done));
        await Runner([plain], false, new LoggedLines<NightlyRunner>()).Run(default, night);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM job_cursors WHERE job='plain-nightly'"));
    }

    // A job that waited for the embeddings model says so in its outcome, and only it; run again (the scheduler's catch-up
    // once the model is on), it runs alone, at its own place in the turn, and the night's summary counts one more run of it
    // with its counts summed, the turn's other jobs once, and no second night line.
    [PostgresFact] public async Task A_job_that_waited_for_the_model_is_said_to_and_runs_again_alone()
    {
        var waiting = true; var starts = new List<string>();
        NightlyJob Job(string id, Func<NightlyRun> run) => new(id, id.Replace('-', ' '), TimeSpan.FromSeconds(10), (_, _) =>
        {
            lock (starts) starts.Add(id);
            return Task.FromResult(run());
        });
        NightlyJob[] jobs =
        [
            Job("before-wait", () => NightlyRun.Of(("things done", 1))),
            Job("waiting-pass", () => waiting ? NightlyRun.Of(("runs waiting for the embeddings model", 1)) with { Waited = true } : NightlyRun.Of(("pairs found", 3))),
            Job("after-wait", () => NightlyRun.Of(("things done", 2))),
        ];
        var log = new LoggedLines<NightlyRunner>();
        var runner = Runner(jobs, true, log);
        var night = At("2026-11-12T02:00:00Z");
        var turn = await runner.Run(default, night);
        Assert.Equal([false, true, false], turn.Select(o => o.Waited));
        Assert.Equal(["complete", "complete", "complete"], turn.Select(o => o.Outcome));

        waiting = false;
        var again = await runner.Again(["waiting-pass", "not-a-job"], default, night.AddMinutes(1));
        Assert.Equal(("waiting-pass", 1, "complete", false), (again.Single().Job, again.Single().Position, again.Single().Outcome, again.Single().Waited));
        Assert.Equal(["before-wait", "waiting-pass", "after-wait", "waiting-pass"], starts);
        Assert.Equal("before-wait 1 1|waiting-pass 2 {\"pairs found\": 3, \"runs waiting for the embeddings model\": 1}|after-wait 1 2", await Scalar("""
            SELECT string_agg(job||' '||runs||' '||CASE WHEN job='waiting-pass' THEN counts::text ELSE counts->>'things done' END,'|' ORDER BY position)
            FROM night_runs WHERE night='2026-11-12' AND job IN ('before-wait','waiting-pass','after-wait')
            """));
        Assert.Single(log.Lines, l => l.StartsWith("Night ", StringComparison.Ordinal));
        // Nothing named, nothing run.
        Assert.Empty(await runner.Again([], default, night.AddMinutes(2)));
        Assert.Equal(4, starts.Count);
    }

    // Off, the summary keeps nothing; on, a row a night and job. Rows of the 30th night back and older are pruned at each
    // turn whatever the setting says, so a summary turned off empties itself.
    [PostgresFact] public async Task The_summary_is_kept_only_with_it_on_and_pruned_after_thirty_nights_either_way()
    {
        var job = new NightlyJob("summary-job", "summary job", TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(NightlyRun.Of(("rows pruned", 4))));
        var at = At("2026-11-15T03:00:00Z");
        var log = new LoggedLines<NightlyRunner>();
        Assert.Equal("complete", (await Runner([job], false, log).Run(default, at)).Single().Outcome);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM night_runs WHERE job='summary-job'"));
        Assert.Empty(log.Lines);
        await Runner([job], true, log).Run(default, at);
        Assert.Equal("complete 1 4 10000", await Scalar("SELECT outcome||' '||runs||' '||(counts->>'rows pruned')||' '||budget_ms FROM night_runs WHERE job='summary-job' AND night='2026-11-15'"));
        await MemoryRows.Run(fixture.Source, """
            INSERT INTO night_runs(night,job,position,name,outcome,counts,runs,budget_ms,elapsed_ms,started_at,finished_at) VALUES
              ('2026-10-16','summary-old',0,'summary old','complete','{}',1,1000,1,now(),now()),
              ('2026-10-17','summary-old',0,'summary old','complete','{}',1,1000,1,now(),now())
            """);
        await Runner([job], false, log).Run(default, at);
        Assert.Equal("2026-10-17", await Scalar("SELECT string_agg(night::text,',') FROM night_runs WHERE job='summary-old'"));
        Assert.Equal(1, await Scalar("SELECT runs FROM night_runs WHERE job='summary-job' AND night='2026-11-15'"));
        Assert.Contains("Night summary: 1 rows older than 30 nights pruned", log.Lines);
        // What the table refuses: a message for a failure's kind, an outcome it does not know.
        Assert.Equal("23514", await MemoryRows.Fails(fixture.Source,
            "INSERT INTO night_runs(night,job,position,name,outcome,failure,budget_ms,elapsed_ms,started_at,finished_at) VALUES('2026-11-15','bad-row',0,'x','failed','a message: with words',1,0,now(),now())"));
        Assert.Equal("23514", await MemoryRows.Fails(fixture.Source,
            "INSERT INTO night_runs(night,job,position,name,outcome,budget_ms,elapsed_ms,started_at,finished_at) VALUES('2026-11-15','bad-row',0,'x','fine',1,0,now(),now())"));
    }
}

// Through the scheduler, with the summary on: a job failing first leaves the daily jobs running after it, each doing what
// it always did, and the night is summed up.
[Collection(SharedStateCollection.Name)]
public sealed class NightlySchedulerTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    sealed class Empty : IRelationshipSource
    {
        public Task<DiscoveryBatch> Fetch(Guid principal, CancellationToken ct)
        {
            using var budget = new CancellationTokenSource();
            budget.Cancel();
            return Task.FromCanceled<DiscoveryBatch>(budget.Token);
        }
        public Task<SourceCheck> Check(Guid principal, ResourceKey key, string? revision, CancellationToken ct) => Task.FromResult(SourceCheck.Unavailable);
    }

    [PostgresFact] public async Task A_failing_job_leaves_the_daily_jobs_running_and_the_night_is_summed_up()
    {
        TestSettings.Use();
        var s = fixture.Source;
        var config = NightlyConfig.With("on", "\nadapters:\n  embeddings: { enabled: false }\n");
        var data = Path.Combine(Path.GetTempPath(), "rhythm-nightly-" + Guid.NewGuid().ToString("N")[..10]);
        var registry = new AdapterRegistry([]);
        var db = new Database(s, registry.Policies(config));
        var state = new EngineState(data);
        var host = new RelationshipHost(db, () => state.Ready ? RelationshipHostConfiguration.From(config, state.GraphPrincipal, Path.Combine(data, "state")) : null,
            _ => new Empty(), registry);
        await MemoryRows.Run(s, """
            INSERT INTO recall_log(subject_id,channel,query,returned,receipt,at) VALUES
              ('alex','mcp','DEMO old question',ARRAY[gen_random_uuid()],'rc1-00000000000000e1',now()-interval '31 days')
            """);
        var log = new LoggedLines<Scheduler>();
        var failing = new NightlyJob("failing-first", "failing first", TimeSpan.FromSeconds(10), (_, _) => throw new InvalidOperationException("DEMO never logged"));
        using var embeddings = new Embeddings();
        var scheduler = new Scheduler(state, db, host, new RefreshRequests(s), new Principals(s), registry,
            new Ingest(db, new Archive(Path.Combine(data, "archive")), config), embeddings, config, log,
            nightlyJobs: [failing, .. NightlyJobs.Daily(s, log)]);
        try
        {
            await scheduler.StartAsync(default);
            bool Summed() => log.Lines.Any(l => l.StartsWith("Night ", StringComparison.Ordinal));
            for (var waited = 0; waited < 300 && !Summed() && !scheduler.ExecuteTask!.IsCompleted; waited++) await Task.Delay(100);
            Assert.False(scheduler.ExecuteTask!.IsCompleted, "the scheduler stopped: " + scheduler.ExecuteTask.Exception?.InnerException?.GetType().Name);
            var today = config.Today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains("failing first failed (InvalidOperationException)", log.Lines);
            Assert.Contains("Recall log: 1 rows and 0 counts older than 30 days pruned", log.Lines);
            Assert.Contains($"Night {today}: 5 jobs, 4 complete, 0 partial, 1 failed, 0 timed out, 0 still running", log.Lines);
            Assert.DoesNotContain(log.Lines, l => l.Contains("DEMO", StringComparison.Ordinal));
            Assert.Equal("failing-first failed|ingest-keys complete|screens complete|memory-trash complete|recall-log complete", await MemoryRows.Scalar(s,
                "SELECT string_agg(job||' '||outcome,'|' ORDER BY position) FROM night_runs WHERE night=@n", ("n", config.Today())));
            var o = new StringWriter();
            Assert.Equal(0, await NightCommand.Run([], o, s, config));
            var lines = o.ToString().TrimEnd('\n', '\r').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.Equal($"Night of {today} (the install's date when the daily jobs began): 5 jobs, 4 complete, 1 failed.", lines[0]);
            Assert.StartsWith("  failing first: failed (InvalidOperationException) after ", lines[1]);
            Assert.Matches(@"^  ingest key pruning: complete in \d+\.\d s of its 15 min$", lines[2]);
            Assert.Matches(@"^  recall log pruning: complete in \d+\.\d s of its 15 min; 1 rows pruned$", lines[5]);
        }
        finally
        {
            await scheduler.StopAsync(default);
            if (Directory.Exists(data)) Directory.Delete(data, true);
            TestSettings.Use();
        }
    }
}
