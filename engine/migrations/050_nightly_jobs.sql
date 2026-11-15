-- The nightly job runner (SB-P5-14 in docs/dev/second-brain-parity/backlog.md; D-017; ledger rows JOB-01, INT-18, DAT-09,
-- API-63): where each nightly job stopped, and what each night's jobs did (Core/NightlyJobs.cs, the scheduler's daily turn).
--
-- job_cursors: one row per job and scope ('' for the whole install), each holding the place a job saved as it went, so a
-- job stopped part way (its budget spent, a failure, the engine stopping) goes on from there at its next run; and, for a
-- job run once a night, the night it last finished. Written only by a job that keeps a cursor or runs once a night. The
-- daily jobs before this one do neither: each deletes what has passed its retention, oldest first, at each daily turn as
-- before, so its own queue is its place. Nothing writes a row here until a later job registers such a one.
--
-- night_runs: the night summary, one row per night and job, kept only with engine.nightSummary on (off by default and
-- when absent): the latest run's outcome, a failure's kind and its time against its budget; its counts summed over the
-- night's runs and how often it was started that night (0 when it only came up still running). Kinds and counts only:
-- never a record's id, title or text, nor an error's message. A night is the install's date (its time zone) when the
-- daily turn began. Rows of the 30th night back and older are pruned at each daily turn whatever the setting says now,
-- so a summary turned off empties itself; on an install that never turned it on there is nothing to prune.
--
-- Plain tables the engine's login reads and writes (Program.GrantLogin), like sync_jobs: no memory text reaches them and
-- no tool or route serves them. The operator reads the summary with `rhythm night`. The number is provisional.

CREATE TABLE job_cursors(
 job text NOT NULL CHECK(job ~ '^[a-z][a-z0-9-]{1,47}$'),
 -- '' for the whole install; a subject's id for a job that keeps one place per scope.
 scope text NOT NULL DEFAULT '' CHECK(scope='' OR scope ~ '^[a-z][a-z0-9-]{0,63}$'),
 cursor jsonb CHECK(cursor IS NULL OR length(cursor::text)<=4000),
 -- The night a job run once a night last finished (on the install's own row, scope '').
 finished_night date,
 updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(job,scope)
);

CREATE TABLE night_runs(
 night date NOT NULL,
 job text NOT NULL CHECK(job ~ '^[a-z][a-z0-9-]{1,47}$'),
 -- Where the job came in the night's order, and the name its log lines use.
 position smallint NOT NULL CHECK(position BETWEEN 0 AND 999),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
 -- The latest run's outcome: complete; partial (it stopped at its own bound with more left); failed (failure names the
 -- kind); timed out (its budget ran out); still running (an earlier run had not ended, so it was not started again).
 outcome text NOT NULL CHECK(outcome IN ('complete','partial','failed','timed out','still running')),
 failure text CHECK(failure IS NULL OR failure ~ '^[A-Za-z][A-Za-z0-9]{0,79}$'),
 -- What it did, summed over the night's runs: plain words to numbers.
 counts jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(counts)='object' AND length(counts::text)<=2000),
 runs integer NOT NULL DEFAULT 1 CHECK(runs>=0),
 budget_ms integer NOT NULL CHECK(budget_ms>0),
 elapsed_ms integer NOT NULL CHECK(elapsed_ms>=0),
 -- When it first came up that night, and when its latest run ended.
 started_at timestamptz NOT NULL,
 finished_at timestamptz NOT NULL,
 PRIMARY KEY(night,job)
);
REVOKE ALL ON job_cursors,night_runs FROM PUBLIC;
