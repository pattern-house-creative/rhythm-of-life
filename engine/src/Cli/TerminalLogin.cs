using System.Security.Cryptography;
using Npgsql;

namespace Knowledge.Cli;

// The terminal's own login for one person (migration 044): role harness, bound to that person, marked terminal, made by
// the operator on the host the first time a memory command speaks for them, one enabled at a time. Its token hash is of
// random bytes nobody keeps, so no token for it exists; the door refuses it anyway (Database.Authenticate), and its mark,
// person and hash never change. Suspending the person, rhythm token revoke --all and a restore's quarantine disable it
// as they do every token; the next command then makes a new one, for a current person only. rhythm token list shows it
// with the role terminal, and the admin audit records it made as the operator's.
public static class TerminalLogin
{
    // Why `person` cannot speak through the terminal, or null when they can: a current person of this install.
    public static async Task<string?> Refusal(NpgsqlDataSource source, string person, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT kind,status FROM subjects WHERE id=@p");
        cmd.Parameters.AddWithValue("p", person);
        await using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.GetString(0) != "person") return $"{person} is not a person of this install (people in rhythm.yaml)";
        return row.GetString(1) switch
        {
            "current" => null,
            "suspended" => $"{person} is suspended, so they read and write nothing (./rhythm person unsuspend {person})",
            _ => $"{person} is no longer in people (rhythm.yaml), so they read and write nothing",
        };
    }

    // The person's enabled terminal login, made now if they have none. Refused (ArgumentException) unless they are a
    // current person; a person suspended meanwhile is refused at the write, as their every login is.
    public static async Task<Guid> For(NpgsqlDataSource source, string person, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AdminAudit.As(c, tx, AdminActor.Host, ct);
        // One maker at a time per person, and the person's row held while it is made, so a suspension that starts now
        // disables what this makes.
        await using (var gate = Database.Command(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended('terminal-login:'||@p,0))", ("p", person)))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var known = Database.Command(c, tx, "SELECT status FROM subjects WHERE id=@p AND kind='person' FOR SHARE", ("p", person)))
            if (await known.ExecuteScalarAsync(ct) as string != "current")
                throw new ArgumentException($"{person} is not a current person of this install, so the terminal cannot speak for them");
        await using (var find = Database.Command(c, tx, "SELECT id FROM principals WHERE terminal AND enabled AND person_id=@p", ("p", person)))
            if (await find.ExecuteScalarAsync(ct) is Guid existing) { await tx.CommitAsync(ct); return existing; }
        // Named terminal-<person>, unless an enabled token has that name already: then with a few random letters after it.
        var name = $"terminal-{person}";
        await using (var taken = Database.Command(c, tx, "SELECT EXISTS(SELECT 1 FROM principals WHERE name=@n AND enabled AND role<>'legacy')", ("n", name)))
            if (await taken.ExecuteScalarAsync(ct) is true) name += "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var id = Guid.NewGuid();
        await using (var insert = Database.Command(c, tx, "INSERT INTO principals(id,name,token_hash,role,person_id,terminal) VALUES(@id,@n,@h,'harness',@p,true)",
            ("id", id), ("n", name), ("h", Hashing.Hash(RandomNumberGenerator.GetBytes(32))), ("p", person)))
            await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return id;
    }
}
