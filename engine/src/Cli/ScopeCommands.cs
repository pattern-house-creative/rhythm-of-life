using Npgsql;

namespace Knowledge.Cli;

// `rhythm family`, `rhythm share` and `rhythm scopes off`: what the operator records on the host for memory scopes
// (docs/dev/memory.md sections 4 and 6.2), straight against the database with the engine's login, as `rhythm person`
// does. Family's members and admins and the source shares are recorded only here (a share also by its own person through
// their own client, once that exists), never by an admin over MCP or in the app, and nothing joins anyone, shares a
// source or names an admin on its own.
// Like `token` and `person`, `family` and `share` do not read rhythm.yaml; `scopes off` does, since the next start must
// not switch scopes on again. Each prints counts and ids, never a record's title or text.
public static class ScopeCommands
{
    public const string FamilyUsage = "family add <id> | family remove <id> | family admin <id> | family unadmin <id>";
    public const string ShareUsage = "share list | share add <person> <provider> | share revoke <person> <provider>";
    public const string ScopesUsage = "scopes off";

    const string NoAdmin = "Family has no admin now: a Family memory can then be changed only by its author (docs/dev/memory.md section 4).";
    const string OffNow = "With memory scopes off the household shares everything, so this changes nothing anyone reads yet; it counts once they are on.";

    public static async Task<int> Family(string[] args, TextWriter o, Principals? store = null, CancellationToken ct = default)
    {
        if (args is not [var verb, var id] || verb is not ("add" or "remove" or "admin" or "unadmin")) throw new ArgumentException(FamilyUsage);
        await using var source = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var principals = store ?? new Principals(source!);
        var off = await principals.Mode(ct) == "off";
        switch (verb)
        {
            case "add":
            {
                var (added, status) = await principals.Join(id, ct);
                o.WriteLine(added ? $"Added {id} to Family: with memory scopes on, their tokens and sessions read Family's records." : $"{id} was already in Family.");
                if (status == "suspended") o.WriteLine($"{id} is suspended: they read nothing until ./rhythm person unsuspend {id}.");
                if (off && added) o.WriteLine(OffNow);
                return 0;
            }
            case "remove":
            {
                // Everyone reads Family while scopes are off, and the switch's first start adds everyone then in people
                // (section 6.3), so taking someone out before it would be undone. Once it has been (JoinedKey, kept
                // through rhythm scopes off), no later start adds anyone, so the removal stands.
                var joined = off && await principals.Joined(ct);
                if (off && !joined)
                {
                    o.WriteLine($"Memory scopes are off: the household shares everything, and the first start with scopes on adds everyone in people "
                        + $"to Family (docs/dev/memory.md section 6.3), so taking {id} out now would be undone. Take them out once scopes are on.");
                    return 1;
                }
                var (role, anyAdmin) = await principals.Leave(id, ct);
                if (role is null) { o.WriteLine($"{id} was not in Family."); return 0; }
                o.WriteLine($"Took {id} out of Family{(role == "admin" ? ", their admin role with it" : "")}: their tokens and sessions read their own records "
                    + $"and DEMO only {(off ? "once memory scopes are on again" : "from now on")}. Their private records, the Family records they wrote "
                    + "and their source shares stay.");
                if (joined) o.WriteLine("With memory scopes off the household shares everything until then; scopes were on before, so switching them "
                    + "on again adds nobody to Family (docs/dev/memory.md section 6.3).");
                if (!anyAdmin) o.WriteLine(NoAdmin);
                return 0;
            }
            default:
            {
                var give = verb == "admin";
                var (was, admins) = await principals.Admin(id, give, ct);
                o.WriteLine(give
                    ? was == "admin" ? $"{id} was already a Family admin."
                        : $"{id} is a Family admin{(was is null ? " (added to Family)" : "")}: they may change Family records others wrote, never anyone's private records."
                    : was == "admin" ? $"{id} is no longer a Family admin, and stays in Family." : $"{id} was not a Family admin.");
                if (give && off && was != "admin") o.WriteLine(OffNow);
                if (!admins) o.WriteLine(NoAdmin);
                return 0;
            }
        }
    }

    public static async Task<int> Share(string[] args, TextWriter o, Principals? store = null, CancellationToken ct = default)
    {
        if (args is not (["list"] or [("add" or "revoke"), _, _])) throw new ArgumentException(ShareUsage);
        await using var source = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var principals = store ?? new Principals(source!);
        switch (args)
        {
            case ["list"]:
                foreach (var (person, provider, by, at) in await principals.Shares(ct))
                    o.WriteLine($"{person}\t{provider}\t{by}\t{at:yyyy-MM-dd}");
                return 0;
            case ["add", var person, var provider]:
            {
                var (added, status, records) = await principals.Share(person, provider, ct);
                o.WriteLine(added ? $"Shared {person}'s {provider} records with Family: with memory scopes on, Family reads them ({records} now)."
                    : $"{person}'s {provider} records were already shared with Family ({records} now).");
                if (records == 0) o.WriteLine($"{person} has no {provider} records yet: the share covers those that come.");
                if (status == "suspended") o.WriteLine($"{person} is suspended: Family reads nothing of theirs until ./rhythm person unsuspend {person}.");
                if (added && await principals.Mode(ct) == "off") o.WriteLine(OffNow);
                return 0;
            }
            default:
            {
                var (person, provider) = (args[1], args[2]);
                o.WriteLine(await principals.Unshare(person, provider, ct)
                    ? $"Ended the share of {person}'s {provider} records: with memory scopes on, Family stops reading them at its next read."
                    : $"There was no share of {person}'s {provider} records.");
                return 0;
            }
        }
    }

    // `rhythm scopes off` (section 6.2): only on the host, only with rhythm.yaml saying off (else the next start would switch
    // them on again), refused while anything private of Rhythm's own exists, and only after a preview of what becomes
    // visible to everyone (counts per person and provider, never a title) and the operator typing off.
    public static async Task<int> Scopes(DeployFolder deploy, string[] args, TextReader input, TextWriter o, Principals? store = null, CancellationToken ct = default)
    {
        if (args is not ["off"]) throw new ArgumentException(ScopesUsage);
        await using var source = store is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var principals = store ?? new Principals(source!);
        if (await principals.Mode(ct) == "off") { o.WriteLine("Memory scopes are already off: the household shares everything."); return 0; }
        string? settings = null;
        if (!File.Exists(deploy.Settings)) settings = "rhythm.yaml is missing";
        // The engine starts from the file .env names (RHYTHM_CONFIG_FILE), so the check below reads the file it reads.
        else if (EnvFile.Read(deploy.Env).GetValueOrDefault("RHYTHM_CONFIG_FILE") is not ("./rhythm.yaml" or "rhythm.yaml"))
            settings = ".env does not set RHYTHM_CONFIG_FILE=./rhythm.yaml, so the engine may start from another settings file";
        else
            try { if (MemorySettings.From(RhythmConfig.Parse(File.ReadAllText(deploy.Settings))).Scopes) settings = "rhythm.yaml says memory.scopes: on"; }
            catch (ConfigException e) { settings = $"rhythm.yaml does not load ({e.Message})"; }
        if (settings is not null)
        {
            o.WriteLine($"Memory scopes stay on: {settings}. Set memory.scopes: off in {deploy.Shown(deploy.Settings)} first, so that the next start "
                + "does not switch them on again; then run this again.");
            return 1;
        }
        var preview = await principals.OffPreview(ct);
        if (Refused(o, preview.Private)) return 1;
        o.WriteLine("With memory scopes off every token and session reads every record again: the household shares everything. "
            + "What everyone would read then, as counts:");
        foreach (var person in preview.Visible.GroupBy(v => (v.Person, v.Status)))
            o.WriteLine($"  {person.Key.Person}{(person.Key.Status == "current" ? "" : $" ({person.Key.Status})")}: "
                + string.Join(", ", person.Select(v => $"{v.Provider} {v.Records}{(v.Shared ? " (Family reads these already: a source share)" : "")}")));
        if (preview.Visible.Count == 0) o.WriteLine("  no person has records of their own");
        if (preview.NotInFamily.Count > 0)
            o.WriteLine($"  Family's {preview.Family} records, to {string.Join(", ", preview.NotInFamily)}, not in Family now");
        o.Write("Type off to switch memory scopes off; anything else changes nothing: ");
        if (input.ReadLine()?.Trim() != "off") { o.WriteLine(); o.WriteLine("Nothing changed: memory scopes stay on."); return 1; }
        if (Refused(o, await principals.SwitchOff(ct))) return 1;
        o.WriteLine("Memory scopes are off: the household shares everything again. Family's members and admins and the source shares are kept "
            + "for if scopes are switched on again. Run ./rhythm up.");
        return 0;
    }

    static bool Refused(TextWriter o, IReadOnlyList<(string Person, string What, long Count)> records)
    {
        if (records.Count == 0) return false;
        o.WriteLine("Memory scopes stay on: Rhythm holds private records, which everyone would read with scopes off, and a private memory "
            + "cannot exist with them off (docs/dev/memory.md section 6.2):");
        foreach (var person in records.GroupBy(r => r.Person))
            o.WriteLine($"  {person.Key}: {string.Join(", ", person.Select(r => $"{r.Count} {r.What}"))}");
        o.WriteLine("Each must be shared into Family by its owner, or deleted, first.");
        return true;
    }
}
