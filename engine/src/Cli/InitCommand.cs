using System.Text.RegularExpressions;

namespace Knowledge.Cli;

// `rhythm init`: the questions of a new install, then rhythm.yaml, .env and every secret, generated here and shown to
// no one (plan section 5.3, docs/configuration.md). Every question has a flag, so scripts and tests run it with
// --no-input. It never replaces an existing secret: the database already uses the passwords it finds. .env records the
// Compose project name and the image tag, so every later command in this folder acts on this install's own project.
public static partial class InitCommand
{
    static readonly string[] Options = ["--name", "--people", "--timezone", "--data", "--exposure", "--host", "--mcp-host", "--demo", "--notes", "--app-port", "--mcp-port", "--project-name"];
    static readonly string[] Switches = ["--no-input", "--force"];
    public static readonly (string Name, string Says)[] Exposures =
    [
        ("local", "this computer only"), ("lan", "your home network: anyone on it can open the app"), ("tailscale", "devices on your tailnet"),
        ("cloudflare", "Cloudflare Tunnel with Access in front"), ("proxy", "your own reverse proxy"),
    ];
    public sealed record Person(string Id, string Name);

    // environment reads COMPOSE_PROJECT_NAME and RHYTHM_VERSION, which the cli service passes in from the host's compose
    // run (their defaults there are rhythm and dev).
    public static int Run(DeployFolder deploy, IReadOnlyList<string> args, Prompter? prompter = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var a = new CliArgs(args, Options, Switches);
        var ask = prompter ?? Prompter.Console(a.Has("--no-input"));
        var o = ask.Output;
        if (File.Exists(deploy.Settings) && !a.Has("--force"))
            throw new ArgumentException("This install is already set up (rhythm.yaml exists). `rhythm doctor` checks it; `rhythm init --force` writes new settings and keeps every secret.");
        var oldEnv = a.Has("--force") && File.Exists(deploy.Env) ? File.ReadAllText(deploy.Env) : null;
        var previous = oldEnv is null ? [] : EnvFile.Parse(oldEnv);
        var answered = new HashSet<string>();
        string Answer(string flag, string question, string? fallback, Func<string, string?> problem)
        {
            if (a[flag] is { } given)
            {
                answered.Add(flag);
                return problem(given) is { } wrong ? throw new ArgumentException($"{flag}: {wrong}") : given;
            }
            var result = ask.Ask(question, fallback, flag, problem);
            if (result != fallback) answered.Add(flag);
            return result;
        }
        string Previous(string key, string fallback) => previous.GetValueOrDefault(key) is { Length: > 0 } value ? value : fallback;

        o.WriteLine("Setting up Rhythm of Life. Enter takes the value in [brackets].");
        var name = Answer("--name", "Name of this install", "Rhythm of Life", v => v.Length is >= 1 and <= 60 ? null : "1-60 characters").Trim();
        var people = ParsePeople(Answer("--people", "Who uses it? Names, separated by commas (everyone listed sees everything)", null,
            v => PeopleProblem(ParsePeople(v))));
        var zone = Answer("--timezone", "Time zone, an IANA name such as Europe/London", "UTC", ZoneProblem);
        var data = Answer("--data", "Folder for notes, imports and backups (relative to deploy/compose, or absolute)", "./data", HostPathProblem);
        if (a["--exposure"] is null && !a.Has("--no-input"))
        {
            o.WriteLine("How will people and AI clients reach it? There is no login inside Rhythm of Life, so this is the gate.");
            foreach (var (key, says) in Exposures) o.WriteLine($"  {key,-11} {says}");
        }
        var exposure = Answer("--exposure", "Reach", "local", v => Exposures.Any(e => e.Name == v) ? null : "one of " + string.Join(", ", Exposures.Select(e => e.Name)));
        var host = exposure == "local" ? null : Answer("--host", exposure == "lan"
            ? "This computer's name or address on your network (for example rhythm.local)" : "The host name people will use (for example rhythm.example.invalid)",
            null, v => HostPattern().IsMatch(v) ? null : "a host name or address, without http:// or a path");
        // Cloudflare puts a login in front of the app's host name, which no AI client can pass, so /mcp gets a host name of
        // its own (deploy/exposure/cloudflare): by default the app's first label with -mcp, one level below the same domain.
        var mcpHost = exposure != "cloudflare" ? host : Answer("--mcp-host",
            "The host name for /mcp only, which AI clients will use (for example rhythm-mcp.example.invalid)", McpHostDefault(host!),
            v => !HostPattern().IsMatch(v) ? "a host name, without http:// or a path"
                : string.Equals(v, host, StringComparison.OrdinalIgnoreCase) ? "a host name other than the app's: the app's has a login in front, which AI clients cannot pass" : null);
        var demo = a["--demo"] is { } d ? Prompter.Yes(d) ?? throw new ArgumentException("--demo is yes or no")
            : ask.Confirm("Show the fictional demo data (always labelled DEMO)?", true, "--demo");
        var notes = Answer("--notes", "A folder of Markdown notes to show as stars (optional, Enter for none)", "", v => v.Length == 0 ? null : HostPathProblem(v));
        var project = Answer("--project-name", "Docker Compose project name (a second install on this computer needs its own)",
            environment("COMPOSE_PROJECT_NAME") is { Length: > 0 } current ? current : Previous("COMPOSE_PROJECT_NAME", "rhythm"),
            v => ProjectName().IsMatch(v) ? null : "lower case letters, digits, '-' or '_', starting with a letter or digit, up to 63");
        var version = environment("RHYTHM_VERSION") is { Length: > 0 } tag ? tag : Previous("RHYTHM_VERSION", "dev");
        if (!ImageTag().IsMatch(version)) throw new ArgumentException("RHYTHM_VERSION is not an image tag: letters, digits, '_', '.' or '-'");
        // Compose resolves quoted/interpolated host ports before passing them to the CLI container.
        string PublishedPort(string key, string fallback) =>
            oldEnv is not null && environment(key) is { Length: > 0 } effective ? effective : Previous(key, fallback);
        var appPort = Port(a["--app-port"] ?? PublishedPort("RHYTHM_APP_PORT", "8080"), "--app-port");
        var mcpPort = Port(a["--mcp-port"] ?? PublishedPort("RHYTHM_MCP_PORT", "8181"), "--mcp-port");

        var (appUrl, mcpUrl) = exposure switch
        {
            "local" => ($"http://127.0.0.1:{appPort}", $"http://127.0.0.1:{mcpPort}/mcp"),
            "lan" => ($"http://{host}:{appPort}", $"http://{host}:{mcpPort}/mcp"),
            _ => ($"https://{host}", $"https://{mcpHost}/mcp"),
        };
        var yaml = Header(Template(deploy, "rhythm.example.yaml", "rhythm.example.yaml"),
            "# rhythm.yaml: the settings of this install, written by `rhythm init`. Nothing here is secret.",
            "# Edit freely: `rhythm doctor` checks it, `rhythm up` applies it. Reference: docs/configuration.md;",
            "# schema: contracts/rhythm.schema.json. Secrets are files under secrets/, never values in here.");
        var expected = new List<(string Path, string Value)>
        {
            ("instance.name", name), ("timezone", zone), ("demo.enabled", demo ? "true" : "false"),
            ("engine.publicMcpUrl", mcpUrl), ("app.publicUrl", appUrl), ("adapters.notes.enabled", notes.Length > 0 ? "true" : "false"),
        };
        if (host is not null) expected.Add(("engine.allowedHosts", string.Join(',', new[] { "localhost", "127.0.0.1", "[::1]", "engine", mcpHost! }.Distinct())));
        if (exposure == "cloudflare") { expected.Add(("adapters.cloudflare.hostname", host!)); expected.Add(("adapters.cloudflare.mcpHostname", mcpHost!)); }
        foreach (var (path, value) in expected)
            yaml = YamlText.Set(yaml, path, path == "engine.allowedHosts" ? YamlText.List(value.Split(',')) : value is "true" or "false" ? value : YamlText.Scalar(value));
        yaml = YamlText.SetBlock(yaml, "people", [.. people.SelectMany(p => new[] { $"- id: {p.Id}", $"  name: {YamlText.Scalar(p.Name)}" })]);
        // The example's personal adapters belong to its example person; with other people they go to the first one listed.
        yaml = OwnerLine().Replace(yaml, m => m.Groups["id"].Value == RhythmConfig.Household || people.Any(p => p.Id == m.Groups["id"].Value)
            ? m.Value : m.Groups["key"].Value + people[0].Id);
        Verify(yaml, expected);

        var env = oldEnv ?? Header(Template(deploy, "env.example", ".env.example"),
            "# .env: the compose-level settings of this install, written by `rhythm init`. Nothing here is secret:",
            "# secrets are files under secrets/ (docs/configuration.md). An empty value means the default in the compose files.");
        string Under(string sub) => data.Contains('\\') && !data.Contains('/') ? $"{data.TrimEnd('\\')}\\{sub}" : $"{data.TrimEnd('/')}/{sub}";
        void Setting(string key, string value, bool requested = false)
        {
            if (!previous.ContainsKey(key) || requested) env = EnvFile.Set(env, key, value);
        }
        Setting("COMPOSE_PROJECT_NAME", project, answered.Contains("--project-name") || project != Previous("COMPOSE_PROJECT_NAME", "rhythm"));
        Setting("RHYTHM_VERSION", version, version != Previous("RHYTHM_VERSION", "dev"));
        Setting("RHYTHM_CONFIG_FILE", "./rhythm.yaml");
        Setting("RHYTHM_BIND", exposure == "lan" ? "0.0.0.0" : "127.0.0.1", answered.Contains("--exposure"));
        Setting("RHYTHM_APP_PORT", appPort.ToString(), a["--app-port"] is not null);
        Setting("RHYTHM_MCP_PORT", mcpPort.ToString(), a["--mcp-port"] is not null);
        var changeData = answered.Contains("--data");
        Setting("RHYTHM_NOTES_DIR", notes.Length > 0 ? notes : Under("notes"), answered.Contains("--notes") || changeData);
        Setting("RHYTHM_IMPORT_DIR", Under("import"), changeData);
        Setting("RHYTHM_BACKUP_DIR", Under("backups"), changeData);
        Setting("RHYTHM_BACKUP_STAGING_DIR", Under("backup-staging"), changeData);

        // Secrets first: a failure here leaves no settings file pointing at secrets that are not there.
        Owners.Folder(deploy.Secrets, Owners.Person(deploy, Owners.PrivateFolder));
        Owners.Folder(deploy.AdapterSecrets, Owners.AdapterFolder);
        int made = 0, kept = 0, cleared = 0;
        var newBackupPassword = !File.Exists(deploy.Secret("backup_password"));
        foreach (var secret in DeployFolder.CoreSecrets)
        {
            var path = deploy.Secret(secret);
            if (Directory.Exists(path))
            {
                // Docker makes an empty folder where a secret file was missing at `up` (open question Q-017).
                if (Directory.EnumerateFileSystemEntries(path).Any())
                    throw new ArgumentException($"{deploy.Shown(path)} is a folder with files in it; move it away and run init again");
                Directory.Delete(path);
                cleared++;
            }
            if (File.Exists(path)) { Owners.Apply(path, Owners.CoreSecret(secret)); kept++; continue; }
            Owners.WriteNew(path, Generate(secret), Owners.CoreSecret(secret));
            made++;
        }
        Owners.Rewrite(deploy.Settings, yaml, Owners.Person(deploy, Owners.Readable));
        Owners.Rewrite(deploy.Env, env, Owners.Person(deploy, Owners.Readable));
        // A data folder inside the deploy folder is made now; one elsewhere on the host by `rhythm up` (preflight).
        var inside = Path.GetFullPath(Path.Combine(deploy.Root, data));
        if ((oldEnv is null || changeData) && !Path.IsPathRooted(data) && !data.Contains(':') && inside.StartsWith(deploy.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            StackCommands.PrepareFolders(inside, deploy, notes.Length == 0);

        o.WriteLine();
        o.WriteLine($"Written: {deploy.Shown(deploy.Settings)}, {deploy.Shown(deploy.Env)} and {deploy.Shown(deploy.Secrets)}/"
            + $" ({made} secrets generated{(kept > 0 ? $", {kept} kept" : "")}; none is ever printed).");
        if (cleared > 0) o.WriteLine($"Replaced {cleared} empty folders Docker had made for missing secrets: run ./rhythm down, then ./rhythm up.");
        if (newBackupPassword)
            o.WriteLine($"Keep a copy of {deploy.Shown(deploy.Secret("backup_password"))} somewhere safe and offline: backups cannot be read without it.");
        if (exposure == "lan") o.WriteLine("Anyone on your network can open the app, and AI clients there can reach MCP with a token.");
        if (exposure is "tailscale" or "proxy")
            o.WriteLine($"Set up {exposure} next (deploy/exposure/README.md); the app and MCP addresses above assume it serves https://{host}.");
        if (exposure == "cloudflare")
            o.WriteLine($"Set up cloudflare next (deploy/exposure/cloudflare/README.md): the app at https://{host} behind Access, and /mcp alone at https://{mcpHost}.");
        if (project != "rhythm") o.WriteLine($"Compose project {project}: every ./rhythm command in this folder acts on it (an external plugin joins {project}_internal).");
        o.WriteLine("Next: ./rhythm up");
        return 0;
    }

    // rhythm.example.invalid gives rhythm-mcp.example.invalid: Cloudflare's free certificate covers one level below the domain.
    public static string McpHostDefault(string host)
    {
        var dot = host.IndexOf('.');
        return dot < 1 ? host + "-mcp" : host[..dot] + "-mcp" + host[dot..];
    }

    public static IReadOnlyList<Person> ParsePeople(string text)
    {
        var people = new List<Person>();
        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (id, name) = entry.IndexOf('=') is var eq and > 0 ? (entry[..eq].Trim(), entry[(eq + 1)..].Trim()) : (Slug(entry), entry);
            var unique = id;
            for (var n = 2; people.Any(p => p.Id == unique); n++) unique = $"{id}-{n}";
            people.Add(new(unique, name));
        }
        return people;
    }
    static string? PeopleProblem(IReadOnlyList<Person> people) =>
        people.Count is < 1 or > 20 ? "list 1-20 people"
        : people.FirstOrDefault(p => !PersonId().IsMatch(p.Id) || p.Id is RhythmConfig.Household or RhythmConfig.Synthetic) is { } bad
            ? $"{bad.Id} is not a usable id: write it as id=Name, the id in lower case letters, digits or '-'"
        : people.FirstOrDefault(p => p.Name.Length is < 1 or > 60) is { } longName ? $"the name of {longName.Id} is 1-60 characters" : null;
    // A person's id from their name: lower case ASCII letters, digits and '-'.
    static string Slug(string name)
    {
        var plain = new string(name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = NotIdChars().Replace(plain.ToLowerInvariant(), "-").Trim('-');
        slug = slug.Length > 0 && char.IsAsciiLetterLower(slug[0]) ? slug : "p-" + slug;
        return slug.Length > 32 ? slug[..32].TrimEnd('-') : slug;
    }
    static string? ZoneProblem(string zone)
    {
        if (!ZonePattern().IsMatch(zone)) return "an IANA name such as Europe/London, or UTC";
        try { TimeZoneInfo.FindSystemTimeZoneById(zone); return null; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return $"{zone} is not a known time zone"; }
    }
    static string? HostPathProblem(string path) =>
        path.Length is 0 or > 300 || path.IndexOfAny(['\n', '\r', '\'', '"', '$']) >= 0 ? "a folder path without quotes or $" : null;
    static int Port(string text, string flag) =>
        int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : throw new ArgumentException($"{flag} is a port number");

    static string Generate(string secret) => secret == "engine_db"
        ? $"Host=postgres;Database=rhythm;Username=rhythm_engine;Password={Principals.NewToken()}"
        : Principals.NewToken();

    // The examples are the templates, so a new install starts from the same defaults and comments. The engine image
    // carries copies (templates/); a checkout has them next to the settings.
    static string Template(DeployFolder deploy, string packaged, string beside)
    {
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "templates", packaged), Path.Combine(deploy.Root, beside) })
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");
        throw new ConfigException($"{beside} not found; run init from deploy/compose");
    }
    // Swaps the template's opening comment for this file's own.
    static string Header(string template, params string[] header)
    {
        var lines = template.Split('\n');
        var skip = lines.TakeWhile(l => l.StartsWith('#')).Count();
        return string.Join('\n', header.Concat(lines.Skip(skip)));
    }
    static void Verify(string yaml, IEnumerable<(string Path, string Value)> expected)
    {
        RhythmConfig.Parse(yaml);
        foreach (var (path, value) in expected)
            if (YamlText.Get(yaml, path) != value) throw new ConfigException($"could not write {path} into rhythm.yaml safely; edit it by hand");
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")] private static partial Regex PersonId();
    [GeneratedRegex(@"^(?<key>[ \t]+owner:[ \t]*)(?<id>[a-z][a-z0-9-]*)", RegexOptions.Multiline)] private static partial Regex OwnerLine();
    // Compose's own rule for project names, and Docker's for image tags.
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")] private static partial Regex ProjectName();
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")] private static partial Regex ImageTag();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NotIdChars();
    [GeneratedRegex("^(UTC|[A-Za-z_]+(/[A-Za-z0-9_+-]+){1,2})$")] private static partial Regex ZonePattern();
    [GeneratedRegex(@"^(\[[0-9A-Fa-f:]+\]|[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?)$")] private static partial Regex HostPattern();
}
