using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge;

// app.viewers in rhythm.yaml: whether the app may open viewer sessions (SB-P1-11; docs/security.md, Viewer sessions).
// Off, the default and what every existing install has (a rhythm.yaml without the key), or on. Any other value fails at
// start naming it.
//
// On is refused by this version: a viewer session is an identity in the app, which the owner has not approved yet
// (D-046, Q-153; Q-006 says the app has no login). It is built and tested on synthetic people
// only; Accepted becomes true in the reviewed change that follows the owner's approval, never by a setting.
public sealed record ViewerSettings(bool On)
{
    // False until the owner approves D-046. Changing it is its own reviewed change.
    public const bool Accepted = false;

    public static readonly ViewerSettings Off = new(false);

    public static ViewerSettings From(RhythmConfig config) => config.App?.Viewers switch
    {
        null or "" or "off" => Off,
        "on" => new(true),
        _ => throw new ConfigException("app.viewers is off or on (docs/configuration.md)"),
    };

    // Why this version will not run with these settings, or null. Checked when the settings load (so check-config,
    // doctor and every start say it before anything runs) and again by rhythm viewer invite.
    public string? Refusal(bool accepted = Accepted) => On && !accepted
        ? "app.viewers: on is refused by this version of the engine: viewer sessions in the app wait for the owner's approval "
          + "(docs/security.md, Viewer sessions). Set app.viewers: off"
        : null;
}

// Viewer sessions (docs/dev/memory.md section 3, D-070): the operator makes a one-time code for one person
// on the host (rhythm viewer invite); someone at the app types it, and the app's server registers, with the shared app
// token and that code, one credential for that one browser session. The engine makes the session's principal, an app
// principal bound to the code's person, and never trusts a person sent beside the shared token: the code is the only
// thing that names one, and only the engine reads it. The credential's secret is the app server's to derive and keep to
// itself (app/server/viewer.ts); the browser holds only a handle the engine never sees. The engine keeps the
// credential's SHA-256, like every token's.
//
// A session's credential is checked at the door like any token (Database.Authenticate): disabled when the person signs
// out (End), when the operator revokes it (rhythm viewer revoke, rhythm token revoke, revoke --all), when the person is
// suspended or taken out of people (Principals.Suspend, Principals.Install), when the app token's file is replaced
// (Principals.SyncApp: the credential was derived from it), and refused with 401 meanwhile whatever the app's server
// still holds. It lasts 30 days from its enrol at most, as the app's cookie does (Lifetime), and is refused at once while
// app.viewers is off (Database.Authenticate), so switching viewer sessions off returns every browser to the household
// view.
public sealed partial class ViewerSessions(NpgsqlDataSource source)
{
    public const string Prefix = "viewer-";
    // A session's lifetime at the engine, from its enrol (app/server/viewerScope.ts maxAgeSeconds is the cookie's), and
    // the condition on principals p that keeps one within it.
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    public const string Current = "p.created_at>now()-interval '30 days'";
    const string Lapse = "UPDATE principals p SET enabled=false WHERE p.enabled AND p.role='app' AND p.person_id IS NOT NULL AND NOT " + Current;
    public const int DefaultMinutes = 15, MaxMinutes = 60;
    // Crockford's base32 alphabet: no I, L, O or U, so a code read aloud or typed from a screen is not misread.
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int CodeLength = 12;

    // 12 characters of 5 bits, 60 bits in all, shown in three groups of four. Within an hour at most and once only.
    public static string NewCode()
    {
        var code = new char[CodeLength];
        for (var i = 0; i < code.Length; i++) code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{new string(code, 0, 4)}-{new string(code, 4, 4)}-{new string(code, 8, 4)}";
    }

    // A code as someone typed it: case, spaces and hyphens ignored, and O, I and L read as the digits they look like.
    // Null when it can't be one.
    public static string? Normal(string? typed)
    {
        if (typed is null || typed.Length > 40) return null;
        var kept = new System.Text.StringBuilder();
        foreach (var ch in typed.ToUpperInvariant())
        {
            if (ch is ' ' or '-' or '\t') continue;
            var c = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            if (!Alphabet.Contains(c)) return null;
            kept.Append(c);
        }
        return kept.Length == CodeLength ? kept.ToString() : null;
    }

    // A session's credential as the app's server sends it: base64url, 43 to 512 characters, as a token is.
    public static bool Fits(string? credential) => credential is { Length: >= 43 and <= 512 } && CredentialPattern().IsMatch(credential);

    // rhythm viewer invite <person>: a one-time code for a current person, valid for `minutes` (1-60). Codes that
    // expired unused are removed first. The code is the caller's to show once; only its SHA-256 is kept.
    public async Task<(string Code, DateTimeOffset Expires)> Invite(string person, int minutes, CancellationToken ct)
    {
        if (minutes is < 1 or > MaxMinutes) throw new ArgumentException($"--minutes is 1-{MaxMinutes}");
        var code = NewCode();
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var known = Database.Command(c, tx, "SELECT status FROM subjects WHERE id=@p AND kind='person' FOR SHARE", ("p", person)))
            switch (await known.ExecuteScalarAsync(ct))
            {
                case "current": break;
                case "suspended": throw new ArgumentException($"{person} is suspended; lift it first (rhythm person unsuspend {person})");
                case "former": throw new ArgumentException($"{person} is no longer in people (rhythm.yaml), so no session can speak for them");
                default:
                    throw new ArgumentException($"{person} is not a person this install knows: people become known when the engine starts, "
                        + "so list them in rhythm.yaml and run ./rhythm up first");
            }
        await Database.Execute(c, tx, "DELETE FROM viewer_codes WHERE used_at IS NULL AND expires_at<=now()", ct);
        // Sessions past their lifetime end first: the admin audit records that as the lifetime's, not the operator's.
        await AdminAudit.As(c, tx, AdminActor.Lifetime, ct);
        await Database.Execute(c, tx, Lapse, ct);
        DateTimeOffset expires;
        await using (var insert = Database.Command(c, tx, """
            INSERT INTO viewer_codes(code_hash,person_id,created_at,expires_at) VALUES(@h,@p,now(),now()+make_interval(mins=>@m)) RETURNING expires_at
            """, ("h", Hashing.Hash(Normal(code)!)), ("p", person), ("m", minutes)))
            expires = new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await insert.ExecuteScalarAsync(ct))!, DateTimeKind.Utc));
        await tx.CommitAsync(ct);
        return (code, expires);
    }

    // app_viewer enrol: the shared app token's principal (the one enabled app principal with no person) registers
    // `credential` for a new session with an unused, unexpired code. The answer is the code's person, or null for a code
    // that is unknown, used, expired or whose person is not current now, alike, so a guess learns nothing. Any other
    // caller is refused: a session's credential never makes another, and no harness or plugin token reaches this.
    public async Task<string?> Enrol(Guid caller, string code, string credential, CancellationToken ct)
    {
        if (!Fits(credential)) throw new ArgumentException("credential is base64url, 43-512 characters");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var app = Database.Command(c, tx, "SELECT EXISTS(SELECT 1 FROM principals WHERE id=@c AND enabled AND role='app' AND person_id IS NULL)", ("c", caller)))
            if (await app.ExecuteScalarAsync(ct) is not true) throw new UnauthorizedAccessException("Only the app's own token registers a viewer session");
        await AdminAudit.As(c, tx, AdminActor.App(caller), ct);
        if (Normal(code) is not { } typed) return null;
        string? person;
        await using (var find = Database.Command(c, tx, """
            SELECT v.person_id FROM viewer_codes v JOIN subjects s ON s.id=v.person_id AND s.kind='person'
            WHERE v.code_hash=@h AND v.used_at IS NULL AND v.expires_at>now() AND s.status='current' FOR UPDATE OF v FOR SHARE OF s
            """, ("h", Hashing.Hash(typed))))
            person = await find.ExecuteScalarAsync(ct) as string;
        if (person is null) return null;
        var id = Guid.NewGuid();
        var name = $"{Prefix}{person}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        await using (var insert = Database.Command(c, tx, "INSERT INTO principals(id,name,token_hash,role,person_id) VALUES(@id,@n,@h,'app',@p) ON CONFLICT DO NOTHING",
            ("id", id), ("n", name), ("h", Hashing.Hash(credential)), ("p", person)))
            // A credential already registered (its hash is unique) is never taken over; the session is not made.
            if (await insert.ExecuteNonQueryAsync(ct) != 1) return null;
        await using (var used = Database.Command(c, tx, "UPDATE viewer_codes SET used_at=now(),principal_id=@id WHERE code_hash=@h", ("id", id), ("h", Hashing.Hash(typed))))
            await used.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return person;
    }

    // app_viewer whoami: the person an app principal speaks for, or null for the shared app token's (the household view).
    public async Task<string?> Person(Guid caller, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT person_id FROM principals WHERE id=@c AND enabled AND role='app'");
        cmd.Parameters.AddWithValue("c", caller);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // Where a document added in the app lands while app.viewers is on (docs/dev/memory.md section 5, Where writes land;
    // SB-P1-20): in Family, the household's, when the call chooses it; with memory scopes on, in a viewer session's
    // person's private scope; otherwise where the documents adapter's settings put it, as every upload does today
    // (null): the household view's, and every upload with scopes off, when a binding only records who saved it (6.1).
    public static string? Lands(bool scopesOn, string? person, bool family) =>
        family ? RhythmConfig.Household : scopesOn ? person : null;

    // The same for one caller now: its person (none for the shared app token's household view) and the recorded mode.
    // With scopes on, a Family choice from a login that does not read Family (a session whose person is not a member) is
    // refused (UnauthorizedAccessException, a 403), as a derived record citing what its writer does not read is (027):
    // it would add to a scope its caller can't read, and its answer would say whether that file is there already.
    public async Task<Adapters.UploadLanding> Landing(Guid caller, bool family, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(
            "SELECT (SELECT person_id FROM principals WHERE id=@c AND enabled AND role='app'),scopes_on(),reads_subject(@c,@h)");
        cmd.Parameters.AddWithValue("c", caller);
        cmd.Parameters.AddWithValue("h", RhythmConfig.Household);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var (person, scopesOn, readsFamily) = (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2));
        if (family && scopesOn && !readsFamily) throw new UnauthorizedAccessException("This login does not read Family, so it does not add to it");
        var lands = Lands(scopesOn, person, family);
        return new(lands, scopesOn, Own: lands is not null && lands == person);
    }

    // app_viewer end: a session's credential signs itself out, for good. The shared app token's principal can't: it
    // comes back from its file, and ending it would end every front end's household view.
    public async Task<bool> End(Guid caller, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var bound = Database.Command(c, null, "SELECT person_id IS NOT NULL FROM principals WHERE id=@c AND role='app'", ("c", caller)))
            if (await bound.ExecuteScalarAsync(ct) is not true) throw new UnauthorizedAccessException("Only a viewer session signs itself out");
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.App(caller), ct);
        int ended;
        await using (var off = Database.Command(c, tx, "UPDATE principals SET enabled=false WHERE id=@c AND role='app' AND person_id IS NOT NULL AND enabled", ("c", caller)))
            ended = await off.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return ended == 1;
    }

    // An enabled session's credential as rhythm viewer list shows it: its name, its person and their status (current
    // unless the door refuses it for them), when it was made, and how many unused codes wait for that person. Sessions
    // past their lifetime are disabled first, so only open ones are listed.
    public sealed record Session(string Name, string Person, string PersonStatus, DateTimeOffset Created);

    public async Task<(IReadOnlyList<Session> Sessions, IReadOnlyList<(string Person, DateTimeOffset Expires)> Codes)> List(CancellationToken ct)
    {
        var sessions = new List<Session>();
        var codes = new List<(string, DateTimeOffset)>();
        await using var c = await source.OpenConnectionAsync(ct);
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await AdminAudit.As(c, tx, AdminActor.Lifetime, ct);
            await Database.Execute(c, tx, Lapse, ct);
            await tx.CommitAsync(ct);
        }
        await using (var cmd = Database.Command(c, null, """
            SELECT p.name,p.person_id,s.status,p.created_at FROM principals p JOIN subjects s ON s.id=p.person_id
            WHERE p.enabled AND p.role='app' AND p.person_id IS NOT NULL ORDER BY p.person_id,p.created_at,p.name
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                sessions.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        await using (var cmd = Database.Command(c, null, "SELECT person_id,expires_at FROM viewer_codes WHERE used_at IS NULL AND expires_at>now() ORDER BY person_id,expires_at"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) codes.Add((reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
        return (sessions, codes);
    }

    // rhythm viewer revoke <name>: one session's credential. rhythm viewer revoke --person <id>: every session of that
    // person, and their unused codes. Never the shared app token's principal, nor a harness or plugin token. The answer:
    // how many sessions were disabled and how many codes removed.
    public async Task<(int Sessions, int Codes)> Revoke(string? name, string? person, CancellationToken ct)
    {
        if (name is null == (person is null)) throw new ArgumentException("name one session, or --person <id>");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        int sessions, codes = 0;
        await using (var off = Database.Command(c, tx, name is not null
            ? "UPDATE principals SET enabled=false WHERE name=@v AND role='app' AND person_id IS NOT NULL AND enabled"
            : "UPDATE principals SET enabled=false WHERE person_id=@v AND role='app' AND enabled", ("v", name ?? person)))
            sessions = await off.ExecuteNonQueryAsync(ct);
        if (person is not null)
            await using (var gone = Database.Command(c, tx, "DELETE FROM viewer_codes WHERE person_id=@p AND used_at IS NULL", ("p", person)))
                codes = await gone.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (sessions, codes);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex CredentialPattern();
}
