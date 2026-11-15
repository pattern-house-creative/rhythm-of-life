using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Knowledge.Adapters;

namespace Knowledge;

// Sync now (SB-P6-09, API-47): the app's Sources view may ask an enabled adapter that syncs or imports on the engine's
// clock (a MeasurementSource or an ImportSource) for one bounded batch now, instead of at its next turn. A batch is one
// call of the adapter's own Sync or Import (or its SyncNow, ISyncNow), under the adapter's own bounds and backoff, cut
// off after Budget. The scheduler runs it between its own jobs, so it never runs beside another batch of any adapter.
// A request is refused while that adapter is running a batch (its own or an asked-for one), while an asked-for one is
// waiting, and inside the adapter's own limit since the last one asked for (Limit). With memory.scopes on, an adapter is
// listed, and may be asked, only by a caller that reads its owner's rows (IAdapter.Owner, docs/dev/memory.md 7.2); to
// anyone else it answers as an unknown adapter. Nothing here changes what an install that never asks does: the scheduler
// keeps its own clock. The state is the engine's own, in memory: a restart forgets the last outcome, and the scheduler
// runs every adapter at its start anyway.
public sealed class SyncRequests
{
    public static readonly TimeSpan MinLimit = TimeSpan.FromMinutes(1), Budget = TimeSpan.FromMinutes(5);
    // How an asked-for batch ended, and why a request was refused.
    public const string Complete = "complete", Failed = "failed", TimedOut = "timed-out";
    public const string Running = "running", Pending = "pending", TooSoon = "too-soon";

    sealed class State
    {
        public int Running;
        public DateTimeOffset? Pending, AskedAt, FinishedAt;
        public Guid AskedBy;
        public string? Outcome;
    }
    // An asked-for batch the scheduler has claimed: its adapter and the principal that asked.
    public sealed record Asked(IAdapter Adapter, Guid Principal);

    readonly IReadOnlyList<IAdapter> clocked;
    readonly Dictionary<string, State> states;
    readonly Queue<string> queue = new();
    readonly Lock gate = new();
    readonly SemaphoreSlim signal = new(0);
    readonly Func<Guid, IAdapter, CancellationToken, Task<bool>> reads;
    readonly TimeProvider time;

    public SyncRequests(AdapterRegistry adapters, Database db) : this(adapters, (principal, adapter, ct) => Reads(db, principal, adapter, ct)) { }
    // For tests: who reads which adapter, and the clock that stamps requests and outcomes.
    internal SyncRequests(AdapterRegistry adapters, Func<Guid, IAdapter, CancellationToken, Task<bool>> reads, TimeProvider? clock = null)
    {
        clocked = [.. adapters.Enabled.Where(a => a is IMeasurementSource or IImportSource).OrderBy(a => a.Name, StringComparer.Ordinal)];
        states = clocked.ToDictionary(a => a.Name, _ => new State(), StringComparer.Ordinal);
        this.reads = reads;
        time = clock ?? TimeProvider.System;
    }

    // The adapters that sync or import on the engine's clock, by name.
    public IReadOnlyList<IAdapter> Clocked => clocked;
    public static string Kind(IAdapter adapter) => adapter is IMeasurementSource ? "measurements" : "imports";
    // How often the adapter runs on its own, and how often it may be asked for a batch: its own limit (ISyncNow), else its
    // own interval, never under a minute.
    public static TimeSpan Every(IAdapter adapter) => adapter is IMeasurementSource m ? m.Interval : ((IImportSource)adapter).Interval;
    public static TimeSpan Limit(IAdapter adapter)
    {
        var limit = adapter is ISyncNow own ? own.SyncLimit : Every(adapter);
        return limit < MinLimit ? MinLimit : limit;
    }

    // {adapters: [...]}: each adapter on the clock this caller reads, by name, as Entry describes.
    public async Task<JsonObject> List(Guid principal, CancellationToken ct)
    {
        var shown = new JsonArray();
        foreach (var adapter in clocked)
            if (await reads(principal, adapter, ct))
                lock (gate) shown.Add(Entry(adapter, states[adapter.Name], time.GetUtcNow()));
        return new JsonObject { ["adapters"] = shown };
    }

    // {accepted, refused, adapter}: whether one batch now was accepted, why not (running, pending or too-soon), and the
    // adapter's state as it stands. Null for an adapter that is not on the clock or that this caller does not read.
    public async Task<JsonObject?> Request(Guid principal, string name, CancellationToken ct)
    {
        if (clocked.FirstOrDefault(a => a.Name == name) is not { } adapter || !await reads(principal, adapter, ct)) return null;
        string? refused;
        JsonObject entry;
        lock (gate)
        {
            var state = states[name];
            var now = time.GetUtcNow();
            refused = state.Running > 0 ? Running : state.Pending is not null ? Pending
                : state.AskedAt is { } asked && now < asked + Limit(adapter) ? TooSoon : null;
            if (refused is null)
            {
                (state.Pending, state.AskedAt, state.AskedBy) = (now, now, principal);
                queue.Enqueue(name);
            }
            entry = Entry(adapter, state, now);
        }
        if (refused is null) signal.Release();
        return new JsonObject { ["accepted"] = refused is null, ["refused"] = refused, ["adapter"] = entry };
    }

    // One adapter's state: its kind, its own interval and its limit in seconds, whether a batch of it runs now (its own
    // or an asked-for one), since when an asked-for one waits, when the next may be asked for (null while one waits or
    // runs, or before any was asked for; it may have passed), and the last asked-for batch: when it was asked for, when
    // it finished and how (complete, failed or timed-out). Times and states only: never a row, a count or an error's words.
    JsonObject Entry(IAdapter adapter, State state, DateTimeOffset now) => new()
    {
        ["adapter"] = adapter.Name, ["kind"] = Kind(adapter),
        ["interval"] = (long)Every(adapter).TotalSeconds, ["limit"] = (long)Limit(adapter).TotalSeconds,
        ["running"] = state.Running > 0, ["pendingSince"] = Iso(state.Pending),
        ["nextAllowedAt"] = state.Running == 0 && state.Pending is null ? Iso(state.AskedAt + Limit(adapter)) : null,
        ["lastAskedAt"] = Iso(state.AskedAt), ["lastFinishedAt"] = Iso(state.FinishedAt), ["outcome"] = state.Outcome,
    };
    static string? Iso(DateTimeOffset? at) => at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // The scheduler's side. A batch of the adapter's own, on its interval: running until disposed, so a request meanwhile
    // is refused.
    public IDisposable Runs(string name)
    {
        if (!states.TryGetValue(name, out var state)) return Nothing.Instance;
        lock (gate) state.Running++;
        return new Done(this, state);
    }
    sealed class Done(SyncRequests requests, State state) : IDisposable
    {
        int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) lock (requests.gate) state.Running--; }
    }
    sealed class Nothing : IDisposable { public static readonly Nothing Instance = new(); public void Dispose() { } }

    // The next asked-for batch, marked running, as soon as one is asked for within `wait`; null once `wait` is over, and
    // at once when it already is: once the scheduler's minute is spent (a batch ran past it), what is still waiting runs in
    // the next minute's wait, after the clock's own jobs, so requests for one adapter after another never keep those
    // jobs from running. Throws OperationCanceledException when the engine stops.
    public async Task<Asked?> Next(TimeSpan wait, CancellationToken stop)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var left = wait - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return null;
            if (Claim() is { } asked) return asked;
            if (!await signal.WaitAsync(left, stop)) return null;
        }
    }
    Asked? Claim()
    {
        lock (gate)
            while (queue.TryDequeue(out var name))
            {
                var state = states[name];
                if (state.Pending is null) continue;
                state.Pending = null;
                state.Running++;
                return new(clocked.First(a => a.Name == name), state.AskedBy);
            }
        return null;
    }
    // How an asked-for batch ended (Complete, Failed or TimedOut).
    public void Finish(Asked asked, string outcome)
    {
        var state = states[asked.Adapter.Name];
        lock (gate)
        {
            state.Running--;
            (state.FinishedAt, state.Outcome) = (time.GetUtcNow(), outcome);
        }
    }

    // One asked-for batch, as the scheduler runs it: the adapter's own SyncNow, else its Sync, then its Import, within
    // Budget. A failure is logged by its kind only, as the scheduler's own jobs are (Scheduler.Guard), and never stops
    // the clock; only the engine stopping ends it.
    public static async Task<string> Run(Asked asked, MeasurementSink sink, ILogger log, CancellationToken stop, TimeSpan? budget = null)
    {
        var adapter = asked.Adapter;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(budget ?? Budget);
        try
        {
            if (adapter is ISyncNow own)
            {
                if (!await own.SyncNow(asked.Principal, sink, limit.Token))
                { log.LogWarning("{Adapter} sync now not run: the source's own limit refused it", adapter.Name); return Failed; }
            }
            else
            {
                if (adapter is IMeasurementSource measurements) await measurements.Sync(sink, limit.Token);
                if (adapter is IImportSource imports) await imports.Import(limit.Token);
            }
            log.LogInformation("{Adapter} sync now: complete", adapter.Name);
            return Complete;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { log.LogWarning("{Adapter} sync now timed out", adapter.Name); return TimedOut; }
        catch (Exception e) when (e is not OperationCanceledException)
        { log.LogWarning("{Adapter} sync now failed ({Kind})", adapter.Name, e is ConfigException ? e.Message : e.GetType().Name); return Failed; }
    }

    // Whether a principal reads the adapter's owner's rows of every provider it writes (of the owner's rows at all, for
    // one that writes none), through the one read predicate: always for an app principal while memory.scopes is off. An
    // adapter that states no owner is read by no one while scopes are on (it fails closed, IAdapter.Owner), and as the
    // household's while they are off.
    static async Task<bool> Reads(Database db, Guid principal, IAdapter adapter, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"""
            SELECT (@stated OR NOT scopes_on())
              AND coalesce(bool_and({Database.Allowed("@s", "u.provider", "NULL::text")}),false) FROM unnest(@providers::text[]) AS u(provider)
            """);
        cmd.Parameters.AddWithValue("principal", principal);
        cmd.Parameters.AddWithValue("stated", adapter.Owner is not null);
        cmd.Parameters.AddWithValue("s", adapter.Owner ?? RhythmConfig.Household);
        cmd.Parameters.AddWithValue("providers", adapter.Providers.Count == 0 ? new string?[] { null } : adapter.Providers.Keys.Order(StringComparer.Ordinal).ToArray<string?>());
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
