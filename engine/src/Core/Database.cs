using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// An authenticated caller: its principal, role (harness, app, ingest; system for the engine's own jobs) and, for an
// ingest principal, the plugin it writes for.
public sealed record Caller(Guid Id, string Name, string Role, string? Plugin);

public sealed class Database(NpgsqlDataSource source, ProviderPolicies? policies = null)
{
    public NpgsqlDataSource Source => source;
    public ProviderPolicies Policies { get; } = policies ?? ProviderPolicies.Default;

    // The migrations this build carries, in order: resource name (the version recorded in schema_versions) and text.
    public static IReadOnlyList<(string Version, string Sql)> Migrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return (name, reader.ReadToEnd());
            }).ToArray();
    }

    // Applies every migration not yet recorded, in one transaction, and refuses to start when a recorded one changed.
    // `accepted` lists extra checksums an existing install may hold for a version (its private overlay's mapping,
    // docs/dev/migration-compat.md); nothing in this repository names them.
    public async Task<IReadOnlyList<string>> Migrate(CancellationToken ct = default, IReadOnlyDictionary<string, IReadOnlySet<string>>? accepted = null)
    {
        var applied = new List<string>();
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Execute(c, tx, "SELECT pg_advisory_xact_lock(61984421); CREATE TABLE IF NOT EXISTS schema_versions(version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
        foreach (var (name, sql) in Migrations())
        {
            await using var check = Command(c, tx, "SELECT checksum FROM schema_versions WHERE version=@version", ("version", name));
            var existing = await check.ExecuteScalarAsync(ct) as string;
            if (existing is not null)
            {
                if (existing != Hashing.Hash(sql) && accepted?.GetValueOrDefault(name)?.Contains(existing) != true)
                    throw new InvalidOperationException($"Applied migration changed: {name}");
                continue;
            }
            await Execute(c, tx, sql, ct);
            await using var insert = Command(c, tx, "INSERT INTO schema_versions VALUES (@v,@h,now())", ("v", name), ("h", Hashing.Hash(sql)));
            await insert.ExecuteNonQueryAsync(ct);
            applied.Add(name);
        }
        await tx.CommitAsync(ct);
        return applied;
    }

    // Whether every migration this build carries is recorded: the engine waits for the migrate service until it is.
    public async Task<bool> Current(CancellationToken ct)
    {
        try
        {
            await using var cmd = source.CreateCommand("SELECT version FROM schema_versions");
            var recorded = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) recorded.Add(reader.GetString(0));
            return Migrations().All(m => recorded.Contains(m.Version));
        }
        catch (PostgresException e) when (e.SqlState == "42P01") { return false; }
    }

    public static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    public static async Task Execute(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    { await using var cmd = Command(c, tx, sql); await cmd.ExecuteNonQueryAsync(ct); }

    public async Task<Guid> Upsert(ResourceInput r, IEnumerable<Measurement> metrics, NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        var domain = Policies.Validate(r);
        await using var cmd = Command(c, tx, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,content,revision,source_updated_at,authorized_until,local_date,timezone,raw_path,checksum,metadata,status,domain)
            VALUES(@id,@subject,@provider,@external,@kind,@scope,@title,@url,@content,@revision,@updated,@until,@date,@zone,@raw,@checksum,@metadata::jsonb,@status,@domain)
            ON CONFLICT(subject_id,provider,external_id) DO UPDATE SET kind=excluded.kind,scope=excluded.scope,title=excluded.title,
              source_url=excluded.source_url,content=excluded.content,revision=excluded.revision,source_updated_at=excluded.source_updated_at,
              authorized_until=excluded.authorized_until,local_date=excluded.local_date,timezone=excluded.timezone,raw_path=excluded.raw_path,
              checksum=excluded.checksum,metadata=excluded.metadata,status=excluded.status,
              domain=CASE WHEN @explicitDomain OR resources.domain='unclassified' THEN excluded.domain ELSE resources.domain END,indexed_at=now() RETURNING id
            """, ("id", Guid.NewGuid()), ("subject", r.Subject), ("provider", r.Provider), ("external", r.ExternalId), ("kind", r.Kind),
            ("scope", r.Scope), ("title", r.Title), ("url", r.Url), ("content", r.Content), ("revision", r.Revision), ("updated", r.SourceUpdated),
            ("until", r.AuthorizedUntil), ("date", r.Date), ("zone", r.Timezone), ("raw", r.RawPath), ("checksum", r.Checksum), ("metadata", r.Metadata), ("status", r.Status),
            ("domain", domain), ("explicitDomain", r.Domain is not null));
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        if (r.RawPath is not null && r.Checksum is not null)
        {
            await using var v = Command(c, tx, "INSERT INTO resource_versions(resource_id,checksum,raw_path) VALUES(@id,@hash,@path) ON CONFLICT DO NOTHING", ("id", id), ("hash", r.Checksum), ("path", r.RawPath));
            await v.ExecuteNonQueryAsync(ct);
        }
        await ReplaceMeasurements(id, metrics, c, tx, ct);
        return id;
    }
    public static async Task ReplaceMeasurements(Guid id, IEnumerable<Measurement> metrics, NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var delete = Command(c, tx, "DELETE FROM measurements WHERE resource_id=@id", ("id", id));
        await delete.ExecuteNonQueryAsync(ct);
        foreach (var m in metrics)
        {
            if (!double.IsFinite(m.Value)) throw new ArgumentException("Nonfinite measurement");
            await using var mc = Command(c, tx, """
                INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,device_id,quality)
                VALUES(@id,@metric,@key,@at,@date,@value,@unit,@device,@quality)
                """, ("id", id), ("metric", m.Metric), ("key", m.SampleKey), ("at", m.ObservedAt?.ToUniversalTime()), ("date", m.Date),
                ("value", m.Value), ("unit", m.Unit), ("device", m.DeviceId), ("quality", m.Quality));
            await mc.ExecuteNonQueryAsync(ct);
        }
    }

    // Whether @principal reads a row of this subject, provider and scope: the one read predicate
    // (docs/dev/memory.md section 7), defined once in the database as readable_subjects (migration 023). With
    // memory.scopes off, every install's mode, it is today's rule: the household shares everything, so a harness, the
    // app and the engine's own jobs read every subject, and a legacy principal (from an install that predates roles)
    // reads exactly what its grants name. With scopes on, the row's subject decides (section 2).
    public static string Allowed(string subject, string provider, string scope) => $"""
        EXISTS(SELECT 1 FROM readable_subjects(@principal) x WHERE (x.subject_id IS NULL OR x.subject_id={subject})
          AND (x.provider IS NULL OR x.provider={provider}) AND (x.scope IS NULL OR x.scope={scope}))
        """;
    // Every read path shares this predicate, including both sides of relationships and metric joins.
    public static string Visible(string alias = "r") => $"""
        {alias}.status='current' AND ({alias}.authorized_until IS NULL OR {alias}.authorized_until>now())
        AND {Allowed($"{alias}.subject_id", $"{alias}.provider", $"{alias}.scope")}
        AND {Copies(alias)}
        """;
    // True while the recorded memory.scopes mode is exactly 'off' (migration 023): a missing row reads as on, never off.
    public const string ScopesOff = "(SELECT value FROM install_settings WHERE key='memory.scopes')='off'";
    // Whether the recorded mode is anything but 'off', as ScopesOff reads it: the map's rebuild shares its budgets out per
    // scope then (docs/dev/memory.md section 8, Budgets).
    public async Task<bool> ScopesOn(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT value FROM install_settings WHERE key='memory.scopes'");
        return await cmd.ExecuteScalarAsync(ct) as string != "off";
    }
    // The source shares of current people, as readable_subjects reads them (migration 023): with scopes on, Family reads
    // these (person, provider) pairs' rows. Read once at a rebuild's start, for its link shares (RelationshipCatalog.Shares).
    public async Task<IReadOnlySet<(string Person, string Provider)>> SourceShares(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(
            "SELECT x.person_id,x.provider FROM source_shares x JOIN subjects s ON s.id=x.person_id AND s.status='current'");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var shares = new HashSet<(string, string)>();
        while (await reader.ReadAsync(ct)) shares.Add((reader.GetString(0), reader.GetString(1)));
        return shares;
    }
    // The retrieval modes of a save_document copy and of a legacy household copy (migrations 017 and 006).
    public const string CopyModes = "'dated-snapshot','dated-reference-snapshot'";
    // With scopes on, a copy whose original the engine holds is read only by readers of that original's current
    // subject, by a join at read time (memory.md section 8), and of its own subject too, so a copy is never wider than
    // either: a household copy of Alex's private Drive file, saved before the switch, is not Sam's. The original is any
    // record that is not itself a copy and whose source id (a snapshot row's Key.ExternalId, or a catalog row's
    // external id) is the copy's external id or a legacy copy's sourceFileId, its newest observation per provider
    // (copy_readable, migration 023). With scopes off (exactly 'off' recorded) this adds nothing, and copy_readable is
    // called only for a copy otherwise: a missing mode row reads as on, never as off.
    static string Copies(string alias) => $"""
        ({ScopesOff}
          OR coalesce({alias}.metadata->>'retrievalMode','') NOT IN ({CopyModes})
          OR copy_readable(@principal,{alias}.id,{alias}.external_id,{alias}.metadata->>'sourceFileId'))
        """;

    public async Task AddDependency(Guid child, Guid parent, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        // This first lineage adapter covers independently authorized archives. Live-source
        // derivatives need ancestor revision checks before they can use this contract.
        await using (var check = Command(connection, tx, "SELECT c.provider FROM resources c JOIN resources p ON p.id=@parent WHERE c.id=@child AND c.provider=p.provider AND c.subject_id=p.subject_id AND c.scope=p.scope", ("child", child), ("parent", parent)))
        {
            if (await check.ExecuteScalarAsync(ct) is not string provider || Policies.For(provider).Authority != SourceAuthority.AuthorizedArchive)
                throw new ArgumentException("Derived archive must share the parent's provider, subject and scope");
        }
        await using var cmd = Command(connection, tx, "INSERT INTO resource_dependencies(child_id,parent_id) VALUES(@child,@parent) ON CONFLICT DO NOTHING", ("child", child), ("parent", parent));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    // Fails closed: without `viewers`, a viewer session's credential is refused, as the door refuses it while app.viewers
    // is off. A caller that must admit sessions says so (Engine.Door, from the settings).
    public Task<Caller?> Authenticate(string token, CancellationToken ct) => Authenticate(token, false, ct);
    // The door's check. A token bound to a suspended or former person is refused like a revoked one
    // (Principals.PersonCurrent). A viewer session's credential (an app principal bound to a person, D-070) is refused too
    // once it is older than ViewerSessions.Lifetime, as the app's cookie is, and at once while app.viewers is off
    // (`viewers` false, Program.cs), so switching viewer sessions off returns every browser to the household view. A
    // terminal login (migration 044, the rhythm memory commands' own) is never admitted: it has no token anyone holds,
    // and none would make it an AI client's.
    public async Task<Caller?> Authenticate(string token, bool viewers, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"""
            SELECT p.id,p.name,p.role,p.plugin FROM principals p WHERE p.token_hash=@hash AND p.enabled AND NOT p.terminal AND {Principals.PersonCurrent}
              AND (p.role<>'app' OR p.person_id IS NULL OR @viewers AND {ViewerSessions.Current})
            """);
        cmd.Parameters.AddWithValue("hash", Hashing.Hash(token));
        cmd.Parameters.AddWithValue("viewers", viewers);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }
    public async Task<string?> RoleOf(Guid principal, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT role FROM principals WHERE id=@id AND enabled");
        cmd.Parameters.AddWithValue("id", principal);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    public async Task<string> Read(Guid principal, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Command(c, null, sql, args.Append(("principal", (object?)principal)).ToArray());
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return JsonSerializer.Serialize(rows);
    }
    public async Task Revoke(string subject, string provider, string external, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            WITH RECURSIVE affected(id) AS (
              SELECT id FROM resources WHERE subject_id=@s AND provider=@p AND external_id=@e
              UNION SELECT d.child_id FROM resource_dependencies d JOIN affected a ON d.parent_id=a.id
            )
            UPDATE resources SET status='revoked',content='',authorized_until=now(),title='Unavailable',metadata='{}'
            WHERE id IN (SELECT id FROM affected)
            """);
        cmd.Parameters.AddWithValue("s", subject); cmd.Parameters.AddWithValue("p", provider); cmd.Parameters.AddWithValue("e", external);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
