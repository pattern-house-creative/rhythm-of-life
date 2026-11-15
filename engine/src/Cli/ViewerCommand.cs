using Npgsql;

namespace Knowledge.Cli;

// `rhythm viewer`: viewer sessions in the app (docs/security.md, Viewer sessions; SB-P1-11), on the host, straight
// against the database with the engine's login, as `rhythm token` does. invite makes a one-time code for one person and
// prints it once; someone at the app types it in, and the app's server registers a session's credential with it
// (ViewerSessions). invite works only while rhythm.yaml says app.viewers: on, which this version refuses, so it reads
// rhythm.yaml; list and revoke do not, so a broken settings file never blocks ending a session.
public static class ViewerCommand
{
    public const string Usage = "viewer invite <person> [--minutes <1-60>] | viewer list | viewer revoke <name> | viewer revoke --person <id>";

    public static async Task<int> Run(DeployFolder deploy, string[] args, TextWriter o, ViewerSessions? store = null, ViewerSettings? settings = null,
        bool accepted = ViewerSettings.Accepted, CancellationToken ct = default)
    {
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        await using var source = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var sessions = store ?? new ViewerSessions(source!);
        switch (args.FirstOrDefault())
        {
            case "invite" when args.Length is 2 or 4 && !args[1].StartsWith("--", StringComparison.Ordinal) && (args.Length == 2 || args[2] == "--minutes"):
            {
                if (settings is null)
                    try { settings = ViewerSettings.From(RhythmConfig.Load(deploy.Settings)); }
                    catch (ConfigException e) { o.WriteLine($"No code made: rhythm.yaml does not load ({e.Message})."); return 1; }
                if (settings.Refusal(accepted) is { } refused) { o.WriteLine($"No code made: {refused}."); return 1; }
                if (!settings.On)
                {
                    o.WriteLine("No code made: viewer sessions are off (app.viewers in rhythm.yaml). Everyone at the app sees the household view; "
                        + "see docs/security.md, Viewer sessions.");
                    return 1;
                }
                var minutes = ViewerSessions.DefaultMinutes;
                if (args.Length == 4 && !int.TryParse(Option("--minutes"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes))
                    throw new ArgumentException($"--minutes is 1-{ViewerSessions.MaxMinutes}");
                var (code, expires) = await sessions.Invite(args[1], minutes, ct);
                o.WriteLine($"Code for {args[1]}: {code}");
                o.WriteLine($"It opens one viewer session in the app, once, until {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Give it to {args[1]} alone: "
                    + "whoever types it in sees what they see. ./rhythm viewer revoke --person " + args[1] + " ends their sessions and unused codes.");
                return 0;
            }
            case "list" when args.Length == 1:
            {
                var (list, codes) = await sessions.List(ct);
                foreach (var s in list)
                    o.WriteLine($"{s.Name}\t{s.Person}{(s.PersonStatus == "current" ? "" : $" ({s.PersonStatus}: refused)")}\t{s.Created:yyyy-MM-dd}");
                if (list.Count == 0) o.WriteLine("No viewer sessions.");
                foreach (var (person, until) in codes) o.WriteLine($"code waiting\t{person}\tuntil {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
                return 0;
            }
            case "revoke" when args is [_, "--person", var person] && !person.StartsWith("--", StringComparison.Ordinal):
            {
                var (ended, removed) = await sessions.Revoke(null, person, ct);
                o.WriteLine($"Ended {ended} viewer session(s) of {person} and removed {removed} unused code(s). Their browsers see the household view "
                    + "from their next request.");
                return 0;
            }
            case "revoke" when args is [_, var name] && !name.StartsWith("--", StringComparison.Ordinal):
                o.WriteLine($"Ended {(await sessions.Revoke(name, null, ct)).Sessions} viewer session(s).");
                return 0;
            default: throw new ArgumentException(Usage);
        }
    }
}
