using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge;

// Principals and tokens (contracts/README.md 1.2 and 2). The database keeps only a token's SHA-256; the token itself
// is written once to a file and shown to no one. Roles: harness (an AI client), app (the app's facade: exactly one
// with no person, the shared app token's; a viewer session's credential, still to come, is one bound to its person,
// D-070), ingest (an external plugin, scoped by rhythm.yaml), capture (a webhook's, a CI job's or a shortcut's, which
// saves a memory into one scope and reads nothing, SB-P7-01, migration 067) and system (the engine's own jobs, which have
// no usable token).
public sealed partial class Principals(NpgsqlDataSource source)
{
    public const string AppName = "app", GraphName = "graph";

    // 32 random bytes, base64url without padding: 43 characters.
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The engine's own principal for `name`, created on first use. Its token hash is of random bytes nobody keeps.
    public async Task<Guid> System(string name, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Database.Execute(c, tx, "SELECT pg_advisory_xact_lock(61984423)", ct);
        await using (var find = Database.Command(c, tx, "SELECT id FROM principals WHERE name=@n AND role='system' AND enabled", ("n", name)))
            if (await find.ExecuteScalarAsync(ct) is Guid existing) { await tx.CommitAsync(ct); return existing; }
        var id = Guid.NewGuid();
        await using (var insert = Database.Command(c, tx, "INSERT INTO principals(id,name,token_hash,role) VALUES(@id,@n,@h,'system')",
            ("id", id), ("n", name), ("h", Hashing.Hash(RandomNumberGenerator.GetBytes(32)))))
            await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return id;
    }

    // Exactly one enabled app principal with no person, with this token's hash; any older one is disabled. Null token:
    // none changes. An app principal bound to a person (a viewer session's credential, D-070) is never this one; its
    // credential was derived from the app token (app/server/viewerScope.ts), so a token that is not the enabled one's
    // ends every session too: replacing the app token's file ends them all, as docs/security.md says.
    public async Task<Guid?> SyncApp(string? token, CancellationToken ct)
    {
        if (token is null) return null;
        if (token.Length is < 32 or > 512) throw new ConfigException("the app token file must hold one token of 32-512 characters");
        var hash = Hashing.Hash(token);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Database.Execute(c, tx, "SELECT pg_advisory_xact_lock(61984423)", ct);
        await AdminAudit.As(c, tx, AdminActor.AppToken, ct);
        await using (var find = Database.Command(c, tx, "SELECT id FROM principals WHERE role='app' AND person_id IS NULL AND enabled AND token_hash=@h", ("h", hash)))
            if (await find.ExecuteScalarAsync(ct) is Guid same) { await tx.CommitAsync(ct); return same; }
        await using (var off = Database.Command(c, tx, "UPDATE principals SET enabled=false WHERE role='app' AND enabled")) await off.ExecuteNonQueryAsync(ct);
        // The file is the authority: a restore's quarantine disables every token, and the app's comes back from its file.
        await using (var back = Database.Command(c, tx, "UPDATE principals SET enabled=true WHERE role='app' AND person_id IS NULL AND token_hash=@h RETURNING id", ("h", hash)))
            if (await back.ExecuteScalarAsync(ct) is Guid again) { await tx.CommitAsync(ct); return again; }
        var id = Guid.NewGuid();
        await using (var insert = Database.Command(c, tx, "INSERT INTO principals(id,name,token_hash,role) VALUES(@id,@n,@h,'app')", ("id", id), ("n", AppName), ("h", hash)))
            await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return id;
    }

    // A new harness or ingest principal; the token goes to `output` (created 0600, never overwritten). A harness token
    // may speak for one person (rhythm token create <name> --person <id>, docs/dev/memory.md section 3): a current person
    // the engine knows, set now and never changed (migration 023's trigger). Moving a client to another person is a new
    // token. With scopes off the person only records who wrote what; with them on it decides what the client reads.
    public Task<Guid> Create(string name, string role, string? plugin, string output, CancellationToken ct) => Create(name, role, plugin, null, output, ct);

    public async Task<Guid> Create(string name, string role, string? plugin, string? person, string output, CancellationToken ct,
        DateTimeOffset? expiresAt = null)
    {
        if (!NamePattern().IsMatch(name) || name is AppName or GraphName) throw new ArgumentException("name is 1-40 lower case letters, digits, '-', '_' or ':', and not app or graph");
        if (role is not ("harness" or "ingest")) throw new ArgumentException("role is harness or ingest");
        if (role == "ingest" != (plugin is not null) || plugin is not null && !RhythmConfig.AdapterPattern().IsMatch(plugin))
            throw new ArgumentException("an ingest token names its plugin (--plugin <name>), and only an ingest token does");
        if (person is not null && role != "harness")
            throw new ArgumentException("--person is for a harness token (an AI client); an ingest token writes for its plugin, never as a person");
        return await Make(name, role, plugin, person, output, expiresAt, ct);
    }

    // A capture token (rhythm capture connect, SB-P7-01): it writes into one scope, a person's private scope once scopes are
    // on, or with no person Family, and reads nothing (migration 067). A person is set when it is made, as for every token.
    public Task<Guid> CreateCapture(string name, string? person, string output, CancellationToken ct, DateTimeOffset? expiresAt = null)
    {
        if (!NamePattern().IsMatch(name) || name is AppName or GraphName) throw new ArgumentException("name is 1-40 lower case letters, digits, '-', '_' or ':', and not app or graph");
        return Make(name, "capture", null, person, output, expiresAt, ct);
    }

    async Task<Guid> Make(string name, string role, string? plugin, string? person, string output, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("The token expiry must be in the future");
        var full = Path.GetFullPath(output);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("Token file already exists");
        var token = NewToken();
        var id = Guid.NewGuid();
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        if (person is not null)
            await using (var known = Database.Command(c, tx, "SELECT status FROM subjects WHERE id=@p AND kind='person' FOR SHARE", ("p", person)))
                switch (await known.ExecuteScalarAsync(ct))
                {
                    case "current": break;
                    case "suspended": throw new ArgumentException($"{person} is suspended; lift it first (rhythm person unsuspend {person})");
                    case "former": throw new ArgumentException($"{person} is no longer in people (rhythm.yaml), so a token cannot speak for them");
                    default:
                        throw new ArgumentException($"{person} is not a person this install knows: people become known when the engine starts, "
                            + "so list them in rhythm.yaml and run ./rhythm up first");
                }
        await using (var insert = Database.Command(c, tx, "INSERT INTO principals(id,name,token_hash,role,plugin,person_id,expires_at) VALUES(@id,@n,@h,@r,@p,@person,@expires)",
            ("id", id), ("n", name), ("h", Hashing.Hash(token)), ("r", role), ("p", plugin), ("person", person), ("expires", expiresAt?.ToUniversalTime())))
        {
            insert.Parameters["p"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            insert.Parameters["person"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            insert.Parameters["expires"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
            await insert.ExecuteNonQueryAsync(ct);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await file.WriteAsync(global::System.Text.Encoding.UTF8.GetBytes(token), ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    // A principal's person, when it has one, must be current: a token bound to a suspended or former person is refused
    // like a revoked one, whatever its enabled flag says (docs/dev/memory.md section 3). Database.Authenticate and
    // Enabled take this condition on `p`.
    public const string PersonCurrent = "(p.person_id IS NULL OR EXISTS(SELECT 1 FROM subjects s WHERE s.id=p.person_id AND s.status='current'))";
    public const string TokenCurrent = "p.enabled AND NOT p.terminal AND (p.expires_at IS NULL OR p.expires_at>clock_timestamp())";

    // Whether a token still opens the engine as a harness (`rhythm up` checks the token files it points people to).
    public async Task<bool> Enabled(string token, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"SELECT EXISTS(SELECT 1 FROM principals p WHERE p.token_hash=@h AND p.role='harness' AND {TokenCurrent} AND {PersonCurrent})");
        cmd.Parameters.AddWithValue("h", Hashing.Hash(token));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // rhythm token revoke <name>: a harness, plugin or capture token, or a viewer session's credential (an app principal bound
    // to a person, D-070), which token list shows; never the shared app token's principal, whose file is its authority. A
    // capture token's mirrors stay, read-only, until rhythm capture disconnect moves them to the trash (migration 067).
    public Task<int> Revoke(string name, CancellationToken ct) => AsHost(
        "UPDATE principals SET enabled=false WHERE name=@n AND (role IN ('harness','ingest','capture') OR role='app' AND person_id IS NOT NULL) AND enabled",
        ct, ("n", name));

    // After a leak (rhythm token revoke --all): every token anyone was given is disabled at once, for good: each harness,
    // plugin and capture token, a viewer session's credential (an app principal bound to a person, D-070) and any legacy one.
    // The shared app token's principal is left alone: its file is the authority and the engine takes it back from there
    // every minute (SyncApp), so it is replaced by writing a new file, not here. The engine's own principals have no
    // usable token. Nothing turns a disabled token on again: new ones are made with token create (capture connect for a
    // capture token).
    public Task<int> RevokeAll(CancellationToken ct) => AsHost(
        "UPDATE principals SET enabled=false WHERE enabled AND (role IN ('harness','ingest','legacy','capture') OR role='app' AND person_id IS NOT NULL)", ct);

    // Whether any capture token is enabled now, which is what token revoke --all's message says.
    public async Task<bool> AnyCapture(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM principals WHERE role='capture' AND enabled)");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // One statement the operator runs on the host, in a transaction that names them for the admin audit (migration 037:
    // every host command here is recorded as the operator's, channel cli). The rows it changed.
    async Task<int> AsHost(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        var changed = await Change(c, tx, sql, ct, args);
        await tx.CommitAsync(ct);
        return changed;
    }

    // The operator suspends a person on the host (rhythm person suspend, docs/dev/memory.md sections 3 and 4): every
    // token and session credential bound to them is disabled at once and stays disabled when the suspension is lifted
    // (new ones are made then); with scopes on, their rows are read by nobody but the engine's own jobs meanwhile. Their
    // rows, memberships and shares stay. rhythm.yaml never lifts a suspension (Install). Only a current person becomes
    // suspended: a former one (no longer in people) stays former, their tokens disabled all the same (the start that
    // found them gone already did). A person suspended and then taken out of people stays suspended, and lifting it
    // makes them former, not current (Unsuspend). The answer: the person's status before (current,
    // suspended or former) and how many tokens were disabled.
    public async Task<(string Was, int Tokens)> Suspend(string person, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        string was;
        await using (var known = Database.Command(c, tx, "SELECT status FROM subjects WHERE id=@p AND kind='person' FOR UPDATE", ("p", person)))
            was = await known.ExecuteScalarAsync(ct) as string ?? throw new ArgumentException($"{person} is not a person this install knows");
        if (was == "current")
            await using (var mark = Database.Command(c, tx, "UPDATE subjects SET status='suspended' WHERE id=@p AND kind='person' AND status='current'", ("p", person)))
                await mark.ExecuteNonQueryAsync(ct);
        int tokens;
        await using (var off = Database.Command(c, tx, "UPDATE principals SET enabled=false WHERE person_id=@p AND enabled", ("p", person)))
            tokens = await off.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (was, tokens);
    }

    // Lifts a suspension (rhythm person unsuspend), and their disabled tokens stay disabled. The person is current again
    // only while the engine's last start found them in people (Install records whom it found, as people.listed): someone
    // suspended and then taken out of people is former instead, so lifting it never makes them current, nor lets a token
    // be made for them, while they are not listed (with no record, as before any start of this version, former too). The
    // answer: their status now (current or former), or null when they were not suspended.
    public async Task<string?> Unsuspend(string person, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var known = Database.Command(c, null, "SELECT status FROM subjects WHERE id=@p AND kind='person'", ("p", person)))
            if (await known.ExecuteScalarAsync(ct) is null) throw new ArgumentException($"{person} is not a person this install knows");
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        await using var lift = Database.Command(c, tx, $"""
            UPDATE subjects SET status=CASE WHEN coalesce(id=ANY(string_to_array((SELECT value FROM install_settings WHERE key='{ListedKey}'),',')),false)
              THEN 'current' ELSE 'former' END
            WHERE id=@p AND kind='person' AND status='suspended' RETURNING status
            """, ("p", person));
        var status = await lift.ExecuteScalarAsync(ct) as string;
        await tx.CommitAsync(ct);
        return status;
    }
    // The people the engine's last start found in rhythm.yaml, comma-separated (install_settings; Install writes it).
    public const string ListedKey = "people.listed";
    // Recorded at the switch's first start, when everyone then in people joined Family (Install); never removed.
    public const string JoinedKey = "memory.scopes.joined";

    // An enabled token as `rhythm token list` shows it: its person (null for the household view, or for a plugin's) and
    // that person's status, which is current unless the token is refused for it; and for a viewer session's credential
    // whether it is past its 30 days (Lapsed), so refused at the door (ViewerSessions.Current) until `viewer list` or
    // `invite` disables it. A terminal login (migration 044, made by the rhythm memory commands for one person) is listed
    // with the role terminal.
    public sealed record Token(string Name, string Role, string? Plugin, string? Person, string? PersonStatus, DateTimeOffset Created, bool Lapsed = false)
    {
        public Guid Id { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    public async Task<IReadOnlyList<Token>> List(CancellationToken ct)
    {
        var rows = new List<Token>();
        await using var cmd = source.CreateCommand($"""
            SELECT p.name,CASE WHEN p.terminal THEN 'terminal' ELSE p.role END,p.plugin,p.person_id,s.status,p.created_at,
              (p.role='app' AND p.person_id IS NOT NULL AND NOT {ViewerSessions.Current}),p.id,p.last_used_at,p.expires_at
            FROM principals p LEFT JOIN subjects s ON s.id=p.person_id
            WHERE p.enabled AND p.role IN ('harness','app','ingest','capture') ORDER BY p.role,p.name,p.created_at
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0), reader.GetString(1), Text(2), Text(3), Text(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetBoolean(6))
            {
                Id = reader.GetGuid(7),
                LastUsedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                ExpiresAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)
            });
        return rows;
    }

    // The offboardings on the same database (SB-P7-13, Core/Offboarding.cs), which rhythm person list and unsuspend name.
    public Offboardings Offboardings => new(source);

    // The people this install knows, with their status, Family membership and enabled tokens (rhythm person list). A
    // terminal login (044) holds no token anyone has, so it is not counted among them.
    public async Task<IReadOnlyList<(string Id, string Status, string? Family, int Tokens)>> People(CancellationToken ct)
    {
        var rows = new List<(string, string, string?, int)>();
        await using var cmd = source.CreateCommand("""
            SELECT s.id,s.status,m.role,(SELECT count(*) FROM principals p WHERE p.person_id=s.id AND p.enabled AND NOT p.terminal)::int
            FROM subjects s LEFT JOIN memberships m ON m.person_id=s.id WHERE s.kind='person' ORDER BY s.id
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3)));
        return rows;
    }

    // ---- What the operator records with memory scopes, on the host (rhythm family, rhythm share and rhythm scopes off;
    // docs/dev/memory.md sections 4 and 6.2). Never over MCP or in the app, and nothing here joins anyone to Family, shares
    // a source or names an admin on its own. Memberships and source shares stay writable by the engine's login (D-064).

    // The memory.scopes mode the database records, "off" or "on", or null when it records none (the start refuses that).
    public async Task<string?> Mode(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT value FROM install_settings WHERE key='memory.scopes'");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    // Whether the switch's first start has been (JoinedKey): from then on no start adds anyone to Family.
    public async Task<bool> Joined(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM install_settings WHERE key=@k)");
        cmd.Parameters.AddWithValue("k", JoinedKey);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // `person`'s status when the engine's last start found them in people: current, or suspended while still listed
    // (ListedKey). Refused for an id the install does not know and for someone taken out of people (former, or suspended
    // and no longer listed): they `cannot` what the caller asks.
    static async Task<string> InPeople(NpgsqlConnection c, NpgsqlTransaction tx, string person, string cannot, CancellationToken ct)
    {
        // A start (Install) takes the mode's row FOR UPDATE before it changes anyone; taking it FOR SHARE first orders this
        // change and a start: a start after it sees what it records (and ends it for someone it drops), and one before it
        // has made a dropped person former, which is refused below.
        await Change(c, tx, "SELECT 1 FROM install_settings WHERE key='memory.scopes' FOR SHARE", ct);
        var (status, listed) = await Person(c, tx, person, ct);
        if (status == "former" || status == "suspended" && !(listed ?? "").Split(',').Contains(person, StringComparer.Ordinal))
            throw new ArgumentException($"{person} is no longer in people (rhythm.yaml), so they {cannot}");
        return status;
    }
    static async Task<(string Status, string? Listed)> Person(NpgsqlConnection c, NpgsqlTransaction tx, string person, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, tx, $"""
            SELECT s.status,(SELECT value FROM install_settings WHERE key='{ListedKey}') FROM subjects s
            WHERE s.id=@p AND s.kind='person' FOR SHARE OF s
            """, ("p", person));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new ArgumentException($"{person} is not a person this install knows: people become known when the engine starts, "
                + "so list them in rhythm.yaml and run ./rhythm up first");
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
    static async Task<int> Change(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = Database.Command(c, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    static async Task<bool> Admins(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, tx, "SELECT EXISTS(SELECT 1 FROM memberships WHERE role='admin')");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // rhythm family add: `person`, someone in people, joins Family as a member (section 4: after the switch's first start
    // nobody joins but by this). A suspended member reads nothing until the suspension is lifted. The answer: whether
    // they were added (false: already in Family) and their status.
    public async Task<(bool Added, string Status)> Join(string person, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        var status = await InPeople(c, tx, person, "cannot join Family", ct);
        var added = await Change(c, tx, "INSERT INTO memberships(person_id) VALUES(@p) ON CONFLICT DO NOTHING", ct, ("p", person));
        await tx.CommitAsync(ct);
        return (added == 1, status);
    }

    // rhythm family remove: `person` leaves Family at once, an admin role with it. Their private records, the Family
    // records they wrote and their source shares stay. The answer: the role they had (null: they were not in Family) and
    // whether Family still has an admin.
    public async Task<(string? Role, bool Admins)> Leave(string person, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        await Person(c, tx, person, ct);
        string? role;
        await using (var cmd = Database.Command(c, tx, "DELETE FROM memberships WHERE person_id=@p RETURNING role", ("p", person)))
            role = await cmd.ExecuteScalarAsync(ct) as string;
        var admins = await Admins(c, tx, ct);
        await tx.CommitAsync(ct);
        return (role, admins);
    }

    // rhythm family admin and unadmin: the operator gives or takes Family's admin role (section 4), which lets a member
    // change Family records others wrote and never lets anyone read a private scope. An admin is a member with the role,
    // so giving it to someone not in Family adds them; taking it leaves them a member. The answer: their role before
    // (null: not in Family) and whether Family has an admin now.
    public async Task<(string? Was, bool Admins)> Admin(string person, bool admin, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        if (admin) await InPeople(c, tx, person, "cannot be a Family admin", ct);
        else await Person(c, tx, person, ct);
        string? was;
        await using (var cmd = Database.Command(c, tx, "SELECT role FROM memberships WHERE person_id=@p FOR UPDATE", ("p", person)))
            was = await cmd.ExecuteScalarAsync(ct) as string;
        if (admin)
            await Change(c, tx, "INSERT INTO memberships(person_id,role) VALUES(@p,'admin') ON CONFLICT(person_id) DO UPDATE SET role='admin'", ct, ("p", person));
        else if (was == "admin")
            await Change(c, tx, "UPDATE memberships SET role='member' WHERE person_id=@p", ct, ("p", person));
        var admins = await Admins(c, tx, ct);
        await tx.CommitAsync(ct);
        return (was, admins);
    }

    // Providers whose rows are no source a person could share wholesale: the native memories' (shared one by one), the
    // core's own (save_document copies, uploads and dashboard log entries) and the map's catalog stars.
    public static bool Shareable(string provider) => ProviderPattern().IsMatch(provider)
        && !ProviderPolicies.Reserved.Contains(provider) && !ProviderPolicies.Core.ContainsKey(provider) && provider != Relationships.CatalogStars.Provider;

    // rhythm share add: the operator records a source share (section 4): one adapter's or plugin's rows of `person`, someone
    // in people, which Family then reads while memory scopes are on, such as a glucose feed a household shows on a shared
    // screen. Recorded by the operator (shared_by null, migration 023), never by an admin for someone else, never on its
    // own. The answer: whether it was added (false: already shared), the person's status and how many current records of
    // that provider they have now (a share covers the ones that come later too).
    public async Task<(bool Added, string Status, long Records)> Share(string person, string provider, CancellationToken ct)
    {
        if (!Shareable(provider))
            throw new ArgumentException($"{provider} is not a source a person shares: a source share is one adapter's or plugin's records "
                + "(nightscout, garmin, csv); memories, saved documents, uploads and log entries are shared one by one");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        var status = await InPeople(c, tx, person, "cannot share a source", ct);
        var added = await Change(c, tx, "INSERT INTO source_shares(person_id,provider) VALUES(@p,@provider) ON CONFLICT DO NOTHING", ct,
            ("p", person), ("provider", provider));
        long records;
        await using (var count = Database.Command(c, tx, "SELECT count(*) FROM resources WHERE subject_id=@p AND provider=@provider AND status='current'",
            ("p", person), ("provider", provider)))
            records = (long)(await count.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return (added == 1, status, records);
    }

    // rhythm share revoke: the share ends at once (deleted, never rewritten). Whether there was one.
    public async Task<bool> Unshare(string person, string provider, CancellationToken ct) =>
        await AsHost("DELETE FROM source_shares WHERE person_id=@p AND provider=@provider", ct, ("p", person), ("provider", provider)) > 0;

    // rhythm share list: each source share, who recorded it (the operator, or the token of the person who shared) and when.
    public async Task<IReadOnlyList<(string Person, string Provider, string By, DateTimeOffset At)>> Shares(CancellationToken ct)
    {
        var rows = new List<(string, string, string, DateTimeOffset)>();
        await using var cmd = source.CreateCommand("""
            SELECT x.person_id,x.provider,coalesce(p.name,'operator'),x.shared_at FROM source_shares x LEFT JOIN principals p ON p.id=x.shared_by
            ORDER BY 1,2
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return rows;
    }

    // Switching memory scopes off on purpose (rhythm scopes off, section 6.2): with them off every token and session reads
    // every record, so it is refused while anything private of Rhythm's own exists. Private native memories (trashed ones
    // included), the private earlier versions of memories since shared into Family, and private memory projects: with
    // scopes off a private memory is refused (D-048), so none may outlive the switch. A save_document copy, insight or
    // dashboard can cite a private memory only while that memory exists, which refuses already. Adapter rows go back to
    // the household model they had before, and the preview counts them.
    const string PrivateNative = """
        SELECT subject_id,'private memories',count(*) FROM memories WHERE scope='private' GROUP BY 1
        UNION ALL SELECT v.subject_id,'earlier private versions of memories now in Family',count(*) FROM memory_versions v
          JOIN memories m ON m.id=v.memory_id WHERE v.scope='private' AND m.scope<>'private' GROUP BY 1
        UNION ALL SELECT e.subject_id,'earlier private changes of memories now in Family',count(*) FROM memory_events e
          JOIN memories m ON m.id=e.memory_id WHERE e.subject_id<>'household' AND m.scope<>'private' GROUP BY 1
        UNION ALL SELECT subject_id,'private memory projects',count(*) FROM memory_projects WHERE subject_id<>'household' GROUP BY 1
        ORDER BY 1,2
        """;
    public sealed record SwitchOffPreview(IReadOnlyList<(string Person, string What, long Count)> Private,
        IReadOnlyList<(string Person, string Status, string Provider, long Records, bool Shared)> Visible, long Family, IReadOnlyList<string> NotInFamily);

    // What switching off would show everyone, as counts and never a title: each person's current records by provider
    // (whether a source share lets Family read them already), and their derived records (migration 027: dashboards, the
    // writes made from dashboards with their before and after values, insights, insight runs, an insight's feedback and
    // reviews with their notes (migration 066) and pins of their own scope, which narrowed to it by what they draw on and
    // go back to the household's model with the rows they cite; counted, not refused, though an insight's text or a write's
    // values may be about a private memory deleted since),
    // and Family's records with the current people not in Family, who read them only once scopes are off; and the private
    // records that refuse the switch. In ordinal order of person, then of provider or kind.
    public async Task<SwitchOffPreview> OffPreview(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(global::System.Data.IsolationLevel.RepeatableRead, ct);
        var current = "r.status='current' AND (r.authorized_until IS NULL OR r.authorized_until>now()) AND r.provider<>@catalog";
        var visible = new List<(string, string, string, long, bool)>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT r.subject_id,s.status,r.provider,count(*),EXISTS(SELECT 1 FROM source_shares x WHERE x.person_id=r.subject_id AND x.provider=r.provider)
            FROM resources r JOIN subjects s ON s.id=r.subject_id AND s.kind='person' WHERE {current} GROUP BY 1,2,3
            UNION ALL SELECT d.subject_id,s.status,d.kind,d.n,false FROM (
              SELECT subject_id,'dashboards' AS kind,count(*) AS n FROM dashboards GROUP BY 1
              UNION ALL SELECT subject_id,'dashboard writes',count(*) FROM dashboard_writes GROUP BY 1
              UNION ALL SELECT subject_id,'insights',count(*) FROM insights GROUP BY 1
              UNION ALL SELECT subject_id,'insight runs',count(*) FROM insight_runs GROUP BY 1
              UNION ALL SELECT subject_id,'insight feedback and reviews',count(*) FROM insight_events GROUP BY 1
              UNION ALL SELECT subject_id,'pins',count(*) FROM sky_pins GROUP BY 1) d
            JOIN subjects s ON s.id=d.subject_id AND s.kind='person'
            ORDER BY 1,3
            """, ("catalog", (object?)Relationships.CatalogStars.Provider)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                visible.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetBoolean(4)));
        visible.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1) is var c and not 0 ? c : string.CompareOrdinal(a.Item3, b.Item3));
        long family;
        await using (var cmd = Database.Command(c, tx, $"SELECT count(*) FROM resources r WHERE r.subject_id='household' AND {current}",
            ("catalog", (object?)Relationships.CatalogStars.Provider)))
            family = (long)(await cmd.ExecuteScalarAsync(ct))!;
        var outside = new List<string>();
        await using (var cmd = Database.Command(c, tx, """
            SELECT id FROM subjects s WHERE kind='person' AND status='current' AND NOT EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=s.id) ORDER BY 1
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) outside.Add(reader.GetString(0));
        var refusing = await PrivateRecords(c, tx, ct);
        await tx.CommitAsync(ct);
        return new(refusing, visible, family, outside);
    }
    static async Task<List<(string, string, long)>> PrivateRecords(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        var rows = new List<(string, string, long)>();
        await using var cmd = Database.Command(c, tx, PrivateNative);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return rows;
    }

    // Records memory.scopes off, after the same check as the preview in the transaction that records it, the mode's row
    // locked first (FOR UPDATE). That keeps a private memory from landing between the check and the switch only because
    // every checked memory writer of phase 3 locks the mode's row FOR SHARE in its own transaction before it writes
    // (memory.md 6.2, the SB-P3-01 backlog row; migration 033's writers do, lock_memory_scopes); a plain read of the mode
    // would not wait. Memberships, admin roles and source shares stay, for if scopes are switched on again;
    // bound tokens keep their person, which records who wrote what. The answer: the private records that refused it, none
    // when it is switched off.
    public async Task<IReadOnlyList<(string Person, string What, long Count)>> SwitchOff(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        await Change(c, tx, "SELECT 1 FROM install_settings WHERE key='memory.scopes' FOR UPDATE", ct);
        var refusing = await PrivateRecords(c, tx, ct);
        if (refusing.Count > 0) { await tx.RollbackAsync(ct); return refusing; }
        await Change(c, tx, """
            INSERT INTO install_settings(key,value) VALUES('memory.scopes','off') ON CONFLICT(key) DO UPDATE SET value='off',updated_at=now()
            """, ct);
        await tx.CommitAsync(ct);
        return refusing;
    }

    // Every person in rhythm.yaml exists as a subject (the household always does), and the functions that need the
    // install's time zone can read it. People removed from rhythm.yaml keep their rows: their data stays. Such a
    // person is former (docs/dev/memory.md section 1) and has left Family, with their source shares, and their tokens are
    // disabled for good: someone listed again later is current again but joins Family, shares a source, and has a token
    // that works, only when that is done again (sections 3 and 4). Whom it found in people is recorded (ListedKey).
    //
    // The start-time checks of section 6.4, before anything is written: the scope tables (migration 023) must be there;
    // memory.scopes on is accepted since phase 1's exit (MemorySettings.ScopesAccepted; a test passes false to see the
    // refusal a version before it gave); a
    // database that records on is refused while rhythm.yaml says off, so a lost or older rhythm.yaml, or a restore,
    // never turns scopes off; and so is one that records no mode (migration 023 records off): reads take a missing mode
    // as on, and only the operator puts it back. The switch's first start (section 6.3), rhythm.yaml on against a
    // database that records off, records on and adds everyone then in people to Family, suspended people included
    // (they read nothing while suspended); nobody joins at a later start. The answer counts the people marked former at
    // this start and the memberships, source shares and tokens of people not listed that ended, and says whether this
    // start switched scopes on and how many people it added to Family, for the start's log.
    public async Task<Started> Install(RhythmConfig config, CancellationToken ct, bool acceptScopesOn = MemorySettings.ScopesAccepted)
    {
        var memory = MemorySettings.From(config);
        if (memory.Refusal(acceptScopesOn) is { } refused) throw new ConfigException(refused);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Start, ct);
        await using (var tables = Database.Command(c, tx,
            "SELECT to_regclass('install_settings') IS NOT NULL AND to_regclass('memberships') IS NOT NULL AND to_regclass('source_shares') IS NOT NULL"))
            if (await tables.ExecuteScalarAsync(ct) is not true)
                throw new ConfigException("memory.scopes: the database has no scope tables yet (migration 023); the migrate service "
                    + "brings it up to date before the engine starts (./rhythm up)");
        var switchOn = false;
        await using (var mode = Database.Command(c, tx, "SELECT value FROM install_settings WHERE key='memory.scopes' FOR UPDATE"))
            switch (await mode.ExecuteScalarAsync(ct))
            {
                case "off": switchOn = memory.Scopes; break;
                case "on" when memory.Scopes: break;
                case "on" when !acceptScopesOn:
                    throw new ConfigException("memory.scopes: the database records on, and this version of the engine runs with scopes off "
                        + "only (docs/dev/memory.md section 6.4); starting it would show everyone what scopes keep private, and scopes are "
                        + "never turned off by starting without them. Run the engine version that switched them on");
                case "on":
                    throw new ConfigException("memory.scopes: the database records on, and rhythm.yaml says off or leaves the key out; "
                        + "scopes are never turned off by starting without them. Set memory.scopes: on again (turning them off on purpose "
                        + "is a host command that first shows what would become visible, docs/dev/memory.md section 6.2)");
                default:
                    throw new ConfigException("memory.scopes: the database records no mode (migration 023 records off); reads take a missing "
                        + "mode as on, and the engine does not start until the operator records it again (docs/dev/memory.md section 6.2)");
            }
        foreach (var person in config.People)
        {
            await using var cmd = Database.Command(c, tx, "INSERT INTO subjects VALUES(@id,'person') ON CONFLICT(id) DO NOTHING", ("id", person.Id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var listed = config.People.Select(p => p.Id).ToArray();
        int former, memberships, shares, tokens;
        // Only a current person becomes former. A suspension is the operator's (rhythm person suspend, SB-P1-12), not
        // rhythm.yaml's: a suspended person taken out of people stays suspended, and listed again is still suspended,
        // until the operator lifts it. Everyone not listed, suspended or not, leaves Family and their shares end, and every
        // token and session credential bound to them is disabled for good, as a suspension does: listed again, or an id
        // given to someone new, they work only with tokens made then (rhythm token create --person), never the old ones.
        await using (var leave = Database.Command(c, tx, """
            WITH gone AS (UPDATE subjects SET status='former' WHERE kind='person' AND status='current' AND NOT id=ANY(@listed) RETURNING id),
              members AS (DELETE FROM memberships WHERE NOT person_id=ANY(@listed) RETURNING 1),
              shares AS (DELETE FROM source_shares WHERE NOT person_id=ANY(@listed) RETURNING 1),
              tokens AS (UPDATE principals SET enabled=false WHERE enabled AND person_id IS NOT NULL AND NOT person_id=ANY(@listed) RETURNING 1)
            SELECT (SELECT count(*) FROM gone)::int,(SELECT count(*) FROM members)::int,(SELECT count(*) FROM shares)::int,
              (SELECT count(*) FROM tokens)::int
            """, ("listed", listed)))
        await using (var reader = await leave.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            (former, memberships, shares, tokens) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }
        await using (var back = Database.Command(c, tx, "UPDATE subjects SET status='current' WHERE kind='person' AND status='former' AND id=ANY(@listed)", ("listed", listed)))
            await back.ExecuteNonQueryAsync(ct);
        await using (var house = Database.Command(c, tx, "INSERT INTO subjects VALUES('household','household') ON CONFLICT(id) DO NOTHING"))
            await house.ExecuteNonQueryAsync(ct);
        var joined = 0;
        if (switchOn)
        {
            await using (var on = Database.Command(c, tx, "UPDATE install_settings SET value='on',updated_at=now() WHERE key='memory.scopes'"))
                await on.ExecuteNonQueryAsync(ct);
            // Everyone then in people joins Family at the switch's first start only (section 6.3, D-072): JoinedKey records
            // that it happened, so switching on again after rhythm scopes off keeps Family as the operator left it. A person
            // the operator offboarded (SB-P7-13, migration 072) is left out, brought back since or not (any offboardings row,
            // returned_at set or not): their offboarding ended their membership, as it does with scopes on, where unsuspend
            // gives it back neither, and only rhythm family add gives it back (unsuspend and the offboarding's preview say
            // so). With no offboarding, every install's state, everyone listed joins.
            var joining = listed;
            await using (var known = Database.Command(c, tx, "SELECT to_regclass('offboardings') IS NOT NULL"))
                if (await known.ExecuteScalarAsync(ct) is true)
                {
                    var offboarded = new HashSet<string>(StringComparer.Ordinal);
                    await using var them = Database.Command(c, tx, "SELECT person_id FROM offboardings");
                    await using (var r = await them.ExecuteReaderAsync(ct))
                        while (await r.ReadAsync(ct)) offboarded.Add(r.GetString(0));
                    joining = [.. listed.Where(p => !offboarded.Contains(p))];
                }
            await using var join = Database.Command(c, tx, """
                WITH first AS (INSERT INTO install_settings(key,value) VALUES(@key,'yes') ON CONFLICT(key) DO NOTHING RETURNING 1)
                INSERT INTO memberships(person_id) SELECT unnest(@listed) WHERE EXISTS(SELECT 1 FROM first) ON CONFLICT DO NOTHING
                """, ("listed", joining), ("key", JoinedKey));
            joined = await join.ExecuteNonQueryAsync(ct);
        }
        foreach (var (key, value) in new[] { ("timezone", config.Timezone), ("instance.name", config.Instance.Name), (ListedKey, string.Join(',', listed)) })
        {
            await using var cmd = Database.Command(c, tx, """
                INSERT INTO install_settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=now()
                WHERE install_settings.value IS DISTINCT FROM excluded.value
                """, ("k", key), ("v", value));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(former, memberships, shares, switchOn, joined, tokens);
    }

    // What a start did, for its log (counts only, never an id): people marked former, their Family memberships and
    // source shares that ended, whether it was the switch's first start and how many people that added to Family, and the
    // tokens bound to people not listed that it disabled.
    public sealed record Started(int Former, int Memberships, int Shares, bool SwitchedOn, int Joined, int Tokens)
    {
        public void Deconstruct(out int former, out int memberships, out int shares) => (former, memberships, shares) = (Former, Memberships, Shares);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_:-]{0,39}$")] private static partial Regex NamePattern();
    // A provider name as source_shares takes it (migration 023).
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")] private static partial Regex ProviderPattern();
}
