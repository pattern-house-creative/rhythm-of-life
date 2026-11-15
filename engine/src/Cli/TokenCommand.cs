using Npgsql;

namespace Knowledge.Cli;

// `rhythm token` and `rhythm person`: the operator's tokens and people, on the host, straight against the database with
// the engine's login (cli/README.md). Like the other install commands they do not read rhythm.yaml, so they work while it
// is broken: revoking every token after a leak must never wait for a settings fix. A token is written once to its file
// and printed nowhere; the database keeps its SHA-256 (Principals).
public static class TokenCommand
{
    public const string TokenUsage =
        "token create <name> [--person <id>] [--out <file>] [--role harness|ingest] [--plugin <name>] | token list | token revoke <name> | token revoke --all";
    public const string PersonUsage = "person list | person suspend <id> | person unsuspend <id> | person offboard <id>";

    public static async Task<int> Token(DeployFolder deploy, string[] args, TextWriter o, Principals? store = null, CancellationToken ct = default)
    {
        await using var source = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var principals = store ?? new Principals(source!);
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        switch (args.FirstOrDefault())
        {
            case "create":
            {
                var role = Option("--role") ?? "harness";
                var plugin = Option("--plugin");
                var person = Option("--person");
                if (Array.IndexOf(args, "--person") >= 0 && person is null or "" || person?.StartsWith("--", StringComparison.Ordinal) == true)
                    throw new ArgumentException("--person names a person id from people in rhythm.yaml");
                // An ingest token may leave out its name: it is plugin:<plugin> (contracts/README.md 4.1).
                var who = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) && args[1] != "-" ? args[1]
                    : role == "ingest" && plugin is not null ? $"plugin:{plugin}"
                    : throw new ArgumentException(TokenUsage);
                // By default the token goes to the install's secrets folder, where `rhythm up` and the docs look for it.
                var output = Path.GetFullPath(Option("--out") ?? deploy.Secret(role == "ingest" ? $"plugin_{plugin}_token" : $"harness_{who}_token"));
                await principals.Create(who, role, plugin, person, output, ct);
                // The person reads a harness token to give it to their client; a plugin's container reads its own.
                Owners.Apply(output, Owners.Token(deploy, output));
                o.WriteLine($"Token for {who} ({role}{(person is null ? role == "harness" ? ", household view" : "" : $", speaks for {person}")}) "
                    + $"written to {deploy.Shown(output)}; it is shown to no one.");
                return 0;
            }
            case "list":
                foreach (var t in await principals.List(ct))
                    o.WriteLine($"{t.Name}\t{t.Role}\t{Who(t)}\t{t.Created:yyyy-MM-dd}");
                return 0;
            case "revoke" when args.Length == 2 && args[1] == "--all":
            {
                // Capture tokens are named only on an install that has some, so with memory.capture never on this says what it said.
                var capture = await principals.AnyCapture(ct);
                var revoked = await principals.RevokeAll(ct);
                o.WriteLine($"Revoked {revoked} token(s): every {(capture ? "harness, plugin and capture" : "harness and plugin")} token and every viewer credential. Nothing turns them on again.");
                o.WriteLine("Make new ones with ./rhythm token create <name> (delete the old token files first: a file is never overwritten)"
                    + (capture ? "; capture tokens with ./rhythm capture connect <name>, which keeps that connection's mirrors." : "."));
                o.WriteLine($"The app's own token ({deploy.Shown(deploy.Secret("app_token"))}) is not one of them: the engine takes it from its file. "
                    + "If it leaked too, replace that file with a new random token of 32 to 512 characters, then ./rhythm up.");
                return 0;
            }
            case "revoke":
                if (args.Length != 2 || args[1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("token revoke <name> | token revoke --all");
                o.WriteLine($"Revoked {await principals.Revoke(args[1], ct)} token(s).");
                return 0;
            default: throw new ArgumentException(TokenUsage);
        }
    }

    // Whom a listed token speaks for: its person, the household view (no person: once scopes are on it reads Family and
    // DEMO only, docs/dev/memory.md section 3) or its plugin; and why it is refused when its person is not current.
    static string Who(Principals.Token t) => (t.Person, t.Role) switch
    {
        ({ } person, _) when t.PersonStatus == "current" && !t.Lapsed => person,
        // A viewer session past its 30 days: refused at the door; viewer list or invite disables it.
        ({ } person, _) when t.PersonStatus == "current" => $"{person} (lapsed: refused)",
        ({ } person, _) => $"{person} ({t.PersonStatus}: refused)",
        (null, "ingest") => $"plugin {t.Plugin}",
        (null, "capture") => "writes into Family",
        _ => "household view",
    };

    public static async Task<int> Person(string[] args, TextWriter o, Principals? store = null, CancellationToken ct = default)
    {
        await using var source = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var principals = store ?? new Principals(source!);
        var id = args.Length == 2 ? args[1] : null;
        switch (args.FirstOrDefault())
        {
            case "list" when args.Length == 1:
            {
                // An offboarding (SB-P7-13) adds a column to its own person's line only: every other line is as it was.
                var offboarded = await principals.Offboardings.All(ct);
                foreach (var (person, status, family, tokens) in await principals.People(ct))
                    o.WriteLine($"{person}\t{status}\t{family switch { "admin" => "Family admin", "member" => "Family member", _ => "not in Family" }}\t{tokens} token(s)"
                        + offboarded.GetValueOrDefault(person) switch
                        {
                            null => "",
                            { FinishedAt: { } done } => $"\toffboarded {done:yyyy-MM-dd}",
                            { } going when status == "current" => $"\toffboarding since {going.StartedAt:yyyy-MM-dd}, waiting while they are current",
                            // Brought back and suspended or former again since: it waits for the operator all the same.
                            { ReturnedAt: not null } going => $"\toffboarding since {going.StartedAt:yyyy-MM-dd}, waiting since they were brought back",
                            { } going => $"\toffboarding since {going.StartedAt:yyyy-MM-dd}",
                        });
                return 0;
            }
            case "suspend" when id is not null:
            {
                var (was, tokens) = await principals.Suspend(id, ct);
                if (was == "former")
                {
                    // Someone taken out of people stays former: a suspension would let unsuspend make them current again.
                    o.WriteLine($"{id} is no longer in people (rhythm.yaml), so they stay former; {tokens} token(s) bound to them disabled.");
                    return 0;
                }
                o.WriteLine(was == "current" ? $"Suspended {id}: {tokens} token(s) disabled." : $"{id} was already suspended; {tokens} more token(s) disabled.");
                o.WriteLine($"Their records stay, and with memory scopes on nobody reads them meanwhile. ./rhythm person unsuspend {id} lifts it; "
                    + "their old tokens stay disabled.");
                // Brought back part way through an offboarding (SB-P7-13): a suspension does not take it up again.
                if ((await principals.Offboardings.All(ct)).GetValueOrDefault(id) is { FinishedAt: null, ReturnedAt: not null })
                    o.WriteLine($"{id}'s offboarding still waits, since they were brought back: ./rhythm person offboard {id} goes on with it, "
                        + "after showing what it does and asking again.");
                return 0;
            }
            case "unsuspend" when id is not null:
            {
                var lifted = await principals.Unsuspend(id, ct);
                o.WriteLine(lifted switch
                {
                    "current" => $"{id} is no longer suspended. Their old tokens stay disabled: ./rhythm token create <name> --person {id} makes a new one.",
                    // Taken out of people while suspended: lifting it never makes them current while they are not listed.
                    "former" => $"{id} is no longer suspended, but the engine's last start did not find them in people (rhythm.yaml), so they are "
                        + "former: no token can be made for them. List them in people and run ./rhythm up first; their old tokens stay disabled.",
                    _ => $"{id} was not suspended.",
                });
                // Brought back part way through an offboarding (SB-P7-13): it waits from now on, even once they are suspended
                // again, until the operator runs it again (072's returned_at).
                var offboarding = lifted == "current" ? (await principals.Offboardings.All(ct)).GetValueOrDefault(id) : null;
                if (offboarding is { FinishedAt: null } going)
                    o.WriteLine($"{id}'s offboarding, started {going.StartedAt:yyyy-MM-dd}, waits while they are current and after, even if they "
                        + $"are suspended again: what it moved to the trash stays there for the trash's retention, from where {id} restores "
                        + $"what they want with a new token; ./rhythm person offboard {id} goes on with it, after showing what it does and asking again.");
                // An offboarding, under way or finished, ended their Family membership and source shares, and lifting the
                // suspension gives back neither; nor does the first start with memory scopes on, which leaves anyone
                // offboarded out of Family. Said only while they are still out of Family.
                if (offboarding is not null && (await principals.People(ct)).FirstOrDefault(x => x.Id == id) is { Family: null })
                    o.WriteLine($"{id} is out of Family since their offboarding, and nothing puts them back, the first start with memory scopes on "
                        + $"included, until ./rhythm family add {id}; the source shares it ended stay ended until ./rhythm share add {id} <provider> "
                        + "(./rhythm share list shows what is shared now).");
                return 0;
            }
            default: throw new ArgumentException(PersonUsage);
        }
    }
}
