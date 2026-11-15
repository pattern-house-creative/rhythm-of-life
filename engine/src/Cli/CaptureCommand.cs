using System.Text.Json;
using Npgsql;

namespace Knowledge.Cli;

// `rhythm capture` (SB-P7-01; cli/README.md, Capture connections; migration 067): the operator's connections to the /capture
// route, on the host. A connection is a capture token's name: connect makes a token, written once to its file and shown to
// no one, for one scope (made again under a name whose token was revoked, it keeps that connection's mirrors); list shows
// them, those revoked that still keep mirrors too; disconnect removes one, its token disabled and its mirrors moved to the
// trash, where a person restores one or the trash's retention ends it. Only with memory.capture on
// beside memory.tools: off, absent or empty, it answers as a command the engine does not know, before it reads its
// arguments, and help is the list it was. One way out stays: once a capture token has ever been made, list and disconnect
// still answer with the setting off, since a mirror is read-only until its connection is removed and every refusal names this
// command. On an install that never connected one, or with no database to ask, off is as unknown as it was. connect never
// answers off. rhythm token revoke works whatever the setting says.
public static class CaptureCommand
{
    public const string Name = "capture";
    public const string Usage = "capture connect <name> [--person <id>] [--out <file>] | capture list | capture disconnect <name>";

    public static bool Offered(RhythmConfig config) => MemorySettings.CaptureOn(config);

    public static async Task<int> Run(DeployFolder deploy, string[] args, TextWriter o, NpgsqlDataSource? store = null, RhythmConfig? config = null,
        CancellationToken ct = default)
    {
        var on = Offered(MemoryCommands.Settings(config));
        if (!on && (args.FirstOrDefault() is not ("list" or "disconnect") || !await EverConnected(store, ct)))
            throw new ArgumentException(MemoryCommands.UnknownCommand);
        await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var source = store ?? own!;
        switch (args.FirstOrDefault())
        {
            case "connect" when args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal):
            {
                string? person = null, file = null;
                for (var i = 2; i < args.Length; i++)
                    switch (args[i])
                    {
                        case "--person" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && person is null: person = args[++i]; break;
                        case "--out" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && file is null: file = args[++i]; break;
                        default: throw new ArgumentException(Usage);
                    }
                var output = Path.GetFullPath(file ?? deploy.Secret($"capture_{args[1]}_token"));
                await new Principals(source).CreateCapture(args[1], person, output, ct);
                Owners.Apply(output, Owners.Token(deploy, output));
                o.WriteLine($"Capture token for {args[1]} ({Writes(person, await ScopesOn(source, ct))}) written to {deploy.Shown(output)}; it is shown to no one. "
                    + "It saves memories with POST /capture on the engine's port and reads nothing (cli/README.md).");
                return 0;
            }
            case "list" when args.Length == 1:
            {
                var scoped = await ScopesOn(source, ct);
                await using var cmd = source.CreateCommand("""
                    SELECT DISTINCT ON (p.name) p.name,p.person_id,p.enabled,p.created_at,(SELECT count(*) FROM memory_mirrors x WHERE x.connection=p.name)
                    FROM principals p WHERE p.role='capture'
                      AND (p.enabled OR EXISTS(SELECT 1 FROM memory_mirrors x WHERE x.connection=p.name))
                    ORDER BY p.name,p.enabled DESC,p.created_at DESC
                    """);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    o.WriteLine($"{r.GetString(0)}\t{Writes(r.IsDBNull(1) ? null : r.GetString(1), scoped)}\t{(r.GetBoolean(2) ? "enabled" : "revoked")}\t"
                        + $"{r.GetInt64(4)} mirror(s)\t{r.GetFieldValue<DateTimeOffset>(3):yyyy-MM-dd}");
                return 0;
            }
            case "disconnect" when args.Length == 2 && !args[1].StartsWith("--", StringComparison.Ordinal):
            {
                await using var c = await source.OpenConnectionAsync(ct);
                await using var tx = await c.BeginTransactionAsync(ct);
                await AdminAudit.As(c, tx, AdminActor.Host, ct);
                await using var cmd = Database.Command(c, tx, "SELECT disconnect_capture(@n)::text", ("n", args[1]));
                using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
                await tx.CommitAsync(ct);
                var a = doc.RootElement;
                if (!a.GetProperty("known").GetBoolean()) { o.WriteLine($"No capture connection is named {args[1]}."); return 1; }
                var mirrors = a.GetProperty("mirrors").GetInt32();
                o.WriteLine($"{args[1]} is disconnected: {a.GetProperty("tokens").GetInt32()} token(s) disabled, {mirrors} mirror(s) moved to the trash"
                    + (mirrors > 0 ? $"; nothing is deleted, and a person can restore them until {a.GetProperty("purge_after").GetDateTimeOffset():yyyy-MM-dd}." : "."));
                return 0;
            }
            default: throw new ArgumentException(Usage);
        }
    }

    // Whether a capture token was ever made here (a row stays once revoked), read only with the setting off. No database to ask,
    // or none that answers, is no: the command stays as unknown as it was.
    static async Task<bool> EverConnected(NpgsqlDataSource? store, CancellationToken ct)
    {
        try
        {
            await using var own = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
            await using var cmd = (store ?? own!).CreateCommand("SELECT EXISTS(SELECT 1 FROM principals p WHERE p.role='capture')");
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ArgumentException or ConfigException or IOException or UnauthorizedAccessException or TimeoutException) { return false; }
    }

    // Where a connection's captures land, in the mode the install records now: with scopes off every memory is the
    // household's, a token bound to a person included, and the operator is told so (D-048).
    static string Writes(string? person, bool scoped) => person is null ? "writes into Family"
        : scoped ? $"writes into {person}'s scope"
        : $"writes into the household while memory scopes are off (everyone reads it); {person}'s private scope once they are on";

    static async Task<bool> ScopesOn(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT scopes_on()");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
