using System.Text.Json;
using Npgsql;

namespace Knowledge;

// Offboarding a person (SB-P7-13; migration 072; docs/dev/memory.md sections 2, 4 and 5): the operator's step on the host,
// rhythm person offboard <id>, when someone leaves the household for good. At once nothing bound to the person reads or
// writes (suspended unless already former, every token and session credential bound to them disabled, out of Family, their
// source shares ended); then their private memories move to the trash in bounded batches, each its own transaction, so a
// run stopped part way goes on from where it stopped when it is run again; then each emptied private project of theirs is
// deleted and, once nothing of their private memory data is left, the offboarding is finished. What waits in the trash has
// the trash's retention (14 days) as its grace, and the daily purge then deletes it for good; the engine's daily turn
// (Scheduler, Daily below) closes the offboarding once that has happened. The Family memories they wrote stay, with them
// as their author. Never an AI client's step: no MCP tool, app tool or route reaches it, and the daily turn only closes
// what the operator started. With memory.scopes off nothing is private to anyone (D-048), so an offboarding ends the
// access bound to the person and moves nothing to the trash. What is bound to no person does not end (a household, plugin or
// capture token, the app, an adapter reading the person's account): the preview names those (OffboardPlan.Unbound).
//
// The first two steps are the terminal's alone (Cli/OffboardCommand.cs, the one file that names their functions); this
// file holds what the engine also reads or does: the plan, the offboardings as they stand, and the close, which the
// command and the daily turn both run.

// What an offboarding would do and leave, as counts and never a title, read before the operator confirms it: the person's
// status, whether the engine's last start found them in people, the recorded memory.scopes mode, the tokens and session
// credentials bound to them, their Family role and source shares; their private memories out of the trash and in it, and
// their private projects; the Family memories they wrote; their own records from adapters and plugins, by provider; the
// records of theirs the core itself wrote (Saved: save_document copies, uploads and dashboard log entries, by provider);
// their private insights and dashboards; the recall log's rows of their questions (042, only while engine.recallLog was
// on); their offboarding, when one was started; and the names of the enabled tokens bound to no person (Unbound: the
// household's harness tokens, plugins' and capture tokens, each of which `rhythm token revoke <name>` disables), which an
// offboarding leaves enabled since nothing ties one to the person.
public sealed record OffboardPlan(string Person, string Status, bool Listed, string Scopes, int Tokens, string? Family, int Shares,
    long Private, long InTrash, long Projects, long FamilyWritten, IReadOnlyList<(string Provider, long Records)> Own, long Insights,
    long Dashboards, OffboardState? Offboarding)
{
    public bool On => Scopes != "off";
    public IReadOnlyList<(string Provider, long Records)> Saved { get; init; } = [];
    public long Recalls { get; init; }
    public IReadOnlyList<string> Unbound { get; init; } = [];
    // Brought back since the operator last ran it (072's returned_at): it waits, and running it again asks again.
    public bool Returned => Offboarding?.ReturnedAt is not null;
}

// An offboarding as it stands: when it started, when it finished (null while under way), what it moved and deleted, and
// when the person was brought back since the operator last ran it (null otherwise), from when it waits.
public sealed record OffboardState(string Person, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int Trashed, int Projects,
    DateTimeOffset? ReturnedAt = null);

// What the first step did: the person's status before and now, the tokens and session credentials it disabled, the
// one-time viewer codes it removed, the Family role they had (null: not in Family), the shares it ended, whether this
// started the offboarding (false: it goes on with one started before), when that was, and the recorded mode.
public sealed record OffboardStarted(string Was, string Status, int Tokens, int Codes, string? Family, int Shares, bool Started,
    DateTimeOffset StartedAt, string Scopes);

// One batch into the trash: how many it moved, how many are still out of it, until when the trash keeps those, and whether
// it did nothing because the person is current (brought back since the offboarding started).
public sealed record OffboardBatch(int Trashed, long Left, DateTimeOffset? PurgeAfter, bool Paused);

// What a close did: the emptied projects it deleted, the memories still out of the trash and in it, the projects left, the
// latest date the trash keeps one of theirs, whether the offboarding is finished, and whether it did nothing (current).
public sealed record OffboardClosed(int Projects, long Left, long Waiting, long ProjectsLeft, DateTimeOffset? PurgeAfter, bool Finished, bool Paused);

// A run of the trash's batches: how many it moved in all, how many are left out of the trash (more than none only when it
// stopped at its bound, or found the person current), until when the trash keeps the last it moved, and whether it paused.
public sealed record OffboardMoved(int Trashed, long Left, DateTimeOffset? PurgeAfter, bool Paused, bool Stopped);

public sealed class Offboardings(NpgsqlDataSource source)
{
    // A batch's size into the trash, and the daily turn's job name on both audit trails.
    public const int Batch = 100;
    public const string Job = "offboarding";
    static readonly AdminActor DailyTurn = new("system", Job: Job);

    // The plan for one person (unknown: ArgumentException), read in one snapshot.
    public async Task<OffboardPlan> Plan(string person, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        OffboardPlan? plan = null;
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT s.status,
              coalesce(s.id=ANY(string_to_array((SELECT value FROM install_settings WHERE key='{Principals.ListedKey}'),',')),false),
              coalesce((SELECT value FROM install_settings WHERE key='memory.scopes'),'on'),
              (SELECT count(*) FROM principals p WHERE p.person_id=s.id AND p.enabled)::int,
              (SELECT m.role FROM memberships m WHERE m.person_id=s.id),
              (SELECT count(*) FROM source_shares x WHERE x.person_id=s.id)::int,
              (SELECT count(*) FROM memories m WHERE m.subject_id=s.id AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)),
              (SELECT count(*) FROM memories m WHERE m.subject_id=s.id AND EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)),
              (SELECT count(*) FROM memory_projects p WHERE p.subject_id=s.id),
              (SELECT count(*) FROM memories m WHERE m.subject_id='household' AND m.author_id=s.id
                AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)),
              (SELECT count(*) FROM insights i WHERE i.subject_id=s.id),
              (SELECT count(*) FROM dashboards d WHERE d.subject_id=s.id),
              (SELECT count(*) FROM recall_log l WHERE l.subject_id=s.id),
              o.started_at,o.finished_at,o.trashed,o.projects,o.returned_at
            FROM subjects s LEFT JOIN offboardings o ON o.person_id=s.id WHERE s.id=@p AND s.kind='person'
            """, ("p", person)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
                plan = new(person, r.GetString(0), r.GetBoolean(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetInt32(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), [], r.GetInt64(10), r.GetInt64(11),
                    r.IsDBNull(13) ? null : new(person, r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
                        r.GetInt32(15), r.GetInt32(16), r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17))) { Recalls = r.GetInt64(12) };
        if (plan is null)
            throw new ArgumentException($"{person} is not a person this install knows: people become known when the engine starts");
        // Their records by provider: an adapter's or plugin's, whose source keeps them, apart from those the core itself
        // wrote (ProviderPolicies.Core: save_document copies, uploads, dashboard log entries), of which Rhythm is the system
        // of record.
        List<(string, long)> own = [], saved = [];
        await using (var cmd = Database.Command(c, tx, """
            SELECT r.provider,count(*) FROM resources r WHERE r.subject_id=@p AND r.status='current' AND r.provider NOT IN ('memory',@catalog)
            GROUP BY 1 ORDER BY 1
            """, ("p", person), ("catalog", Relationships.CatalogStars.Provider)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                (ProviderPolicies.Core.ContainsKey(r.GetString(0)) ? saved : own).Add((r.GetString(0), r.GetInt64(1)));
        // The enabled tokens bound to no person, by name only: the household's harness tokens (the first one `rhythm up`
        // makes, assistant, among them), plugins' and capture tokens. The offboarding ends none of them, so the preview
        // names them for the operator to revoke any the person held. The app's own token is not one of them.
        List<string> unbound = [];
        await using (var cmd = Database.Command(c, tx, """
            SELECT name FROM principals WHERE enabled AND person_id IS NULL AND role IN ('harness','ingest','capture') ORDER BY name COLLATE "C"
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) unbound.Add(r.GetString(0));
        await tx.CommitAsync(ct);
        return plan with { Own = own, Saved = saved, Unbound = unbound };
    }

    // Every offboarding there is, by person (rhythm person list shows each). None on a database not migrated to 072 yet,
    // so person list and unsuspend answer there as they did before it.
    public async Task<IReadOnlyDictionary<string, OffboardState>> All(CancellationToken ct)
    {
        var all = new Dictionary<string, OffboardState>(StringComparer.Ordinal);
        await using (var known = source.CreateCommand("SELECT to_regclass('offboardings') IS NOT NULL"))
            if (await known.ExecuteScalarAsync(ct) is not true) return all;
        await using var cmd = source.CreateCommand("SELECT person_id,started_at,finished_at,trashed,projects,returned_at FROM offboardings ORDER BY 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            all[r.GetString(0)] = new(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                r.GetInt32(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));
        return all;
    }

    // Whether Family has an admin now (an offboarding takes the person's role with their membership).
    public async Task<bool> Admins(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM memberships WHERE role='admin')");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // The close, as the operator, or with `daily` as the daily turn's job (offboard_close).
    public async Task<OffboardClosed> Close(string person, bool daily, CancellationToken ct)
    {
        using var doc = await Call("SELECT offboard_close(@p,@job)::text", daily ? DailyTurn : AdminActor.Host, ct, ("p", person), ("job", daily ? Job : null));
        var o = doc.RootElement;
        if (o.GetProperty("paused").GetBoolean()) return new(0, 0, 0, 0, null, false, true);
        return new(Int(o, "projects"), o.GetProperty("left").GetInt64(), o.GetProperty("waiting").GetInt64(), o.GetProperty("projects_left").GetInt64(),
            When(o, "purge_after"), o.GetProperty("finished").GetBoolean(), false);
    }

    // The daily turn's part (Scheduler): each offboarding under way whose person is not current and was not brought back
    // since the operator last ran it (072's returned_at: once brought back, it waits for the operator whatever their status
    // since) is closed as far as it can be, its emptied projects deleted and finished once nothing of theirs is left;
    // offboard_close checks the same again for each, so a return meanwhile pauses it. It starts none and moves nothing to
    // the trash. With no offboarding, every install's state, one query. One person's close failing is counted and tried
    // again at the next turn, and the others still run. Counts only in the log, never a person's id. The answer: how many
    // it finished, the projects it deleted, and how many still wait. It runs after the nightly job runner, not as one of
    // its jobs (NightlyJobs.cs says why).
    public static async Task<(int Finished, int Projects, int Waiting)> Daily(NpgsqlDataSource source, ILogger log, CancellationToken ct)
    {
        var people = new List<string>();
        await using (var cmd = source.CreateCommand("""
            SELECT o.person_id FROM offboardings o JOIN subjects s ON s.id=o.person_id AND s.kind='person'
            WHERE o.finished_at IS NULL AND o.returned_at IS NULL AND s.status<>'current' ORDER BY 1
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) people.Add(r.GetString(0));
        int finished = 0, projects = 0, waiting = 0, failed = 0;
        var store = new Offboardings(source);
        foreach (var person in people)
            try
            {
                var closed = await store.Close(person, true, ct);
                projects += closed.Projects;
                if (closed.Finished) finished++;
                else if (!closed.Paused) waiting++;
            }
            catch (Exception e) when (e is NpgsqlException or ArgumentException) { failed++; }
        if (failed > 0) log.LogWarning("Offboarding: {Failed} could not be closed, tried again at the next daily turn", failed);
        if (finished + projects > 0)
            log.LogInformation("Offboarding: {Finished} finished, {Projects} emptied private projects deleted, {Waiting} still waiting for the trash's retention",
                finished, projects, waiting);
        return (finished, projects, waiting);
    }

    // One of 072's functions in a transaction of its own, its writer named for the admin audit (AdminAudit.As), its answer
    // the function's JSON. Unknown (P0002): ArgumentException, in plain words.
    internal async Task<JsonDocument> Call(string sql, AdminActor actor, CancellationToken ct, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, actor, ct);
        JsonDocument doc;
        await using (var cmd = Database.Command(c, tx, sql, args))
        {
            foreach (NpgsqlParameter p in cmd.Parameters)
                if (p.Value is DBNull) p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            try { doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!); }
            catch (PostgresException e) when (e.SqlState == "P0002")
            { throw new ArgumentException(e.MessageText.StartsWith("Not a person", StringComparison.Ordinal)
                ? "not a person this install knows: people become known when the engine starts"
                : "no offboarding of that person is under way: ./rhythm person offboard starts one"); }
        }
        await tx.CommitAsync(ct);
        return doc;
    }

    internal static int Int(JsonElement o, string name) => o.GetProperty(name).GetInt32();
    internal static DateTimeOffset? When(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;
}
