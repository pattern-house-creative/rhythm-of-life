using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using YamlDotNet.RepresentationModel;

namespace Knowledge.Cli;

// `rhythm doctor`: settings valid, secret files present and private, no secret-shaped value in rhythm.yaml or .env,
// every enabled adapter's required secrets there, and, when the stack is up, the engine healthy, its version and each
// model's state (from /health, docs/models.md), and MCP answering, and the memory scope reports of docs/dev/memory.md
// section 6.4 from the database. It reads and reports; it never changes a file or a row (./rhythm up fixes owners and
// modes). Exit 1 when anything failed; a note never fails.
public static partial class DoctorCommand
{
    public sealed record EngineMounts(string Settings, string DatabaseFile, string AppTokenFile)
    {
        public static EngineMounts? Current() =>
            Environment.GetEnvironmentVariable("RHYTHM_CLI_DATA") is null &&
            Environment.GetEnvironmentVariable("RHYTHM_CONFIG") is { Length: > 0 } settings &&
            Environment.GetEnvironmentVariable("RHYTHM_DB_FILE") is { Length: > 0 } database &&
            Environment.GetEnvironmentVariable("RHYTHM_APP_TOKEN_FILE") is { Length: > 0 } token
                ? new(settings, database, token) : null;
    }

    // Adapters that keep a person's own records as private rows under the household when their owner is the household
    // (the Second Brain personal workspace, docs/dev/memory.md section 2): with scopes on, Family reads them.
    static readonly string[] PrivateUnderHousehold = ["second-brain"];

    public static async Task<int> Run(DeployFolder deploy, TextWriter o, StackCommands.Probe probe, string? descriptors = null, CancellationToken ct = default,
        Func<NpgsqlDataSource>? database = null, StackCommands.Read? read = null, EngineMounts? mounts = null)
    {
        var report = new Report(o);
        var settings = mounts?.Settings ?? deploy.Settings;
        if (mounts is null) o.WriteLine($"Checking {deploy.Shown(settings)}, {deploy.Shown(deploy.Env)} and {deploy.Shown(deploy.Secrets)}/");
        else o.WriteLine("Checking the running engine's mounted settings and credentials.");

        RhythmConfig? config = null;
        var yaml = File.Exists(settings) ? File.ReadAllText(settings) : null;
        if (yaml is null) report.Fail("rhythm.yaml is missing: run ./rhythm init");
        else
            try
            {
                config = RhythmConfig.Parse(yaml);
                report.Ok($"rhythm.yaml is valid: {config.People.Count} people, time zone {config.Timezone}, demo {(config.Demo.Enabled ? "on" : "off")}");
            }
            catch (ConfigException e) { report.Fail($"rhythm.yaml: {e.Message}"); }

        var envText = mounts is null && File.Exists(deploy.Env) ? File.ReadAllText(deploy.Env) : null;
        var env = envText is null ? [] : EnvFile.Parse(envText);
        if (mounts is not null) report.Skip("Compose settings, service secrets and host ports are checked by ./rhythm doctor from the checkout");
        else if (envText is null) report.Fail(".env is missing: run ./rhythm init");
        else if (env.GetValueOrDefault("RHYTHM_CONFIG_FILE") is "./rhythm.yaml" or "rhythm.yaml") report.Ok(".env points the containers at rhythm.yaml");
        else report.Fail(".env does not set RHYTHM_CONFIG_FILE=./rhythm.yaml, so the containers read another settings file");

        var values = new List<string>();
        if (mounts is null)
        {
            foreach (var name in DeployFolder.CoreSecrets)
                Secret(report, deploy, deploy.Secret(name), Owners.CoreSecret(name), true, values, Shape(name));
            foreach (var token in deploy.Tokens) Secret(report, deploy, token, null, true, values, null);
        }
        else
        {
            Secret(report, deploy, mounts.DatabaseFile, Owners.CoreSecret("engine_db"), true, values, Shape("engine_db"));
            Secret(report, deploy, mounts.AppTokenFile, Owners.CoreSecret("app_token"), true, values, Shape("app_token"));
        }

        // Values in the form their adapter's descriptor gives (a tunnel ID, say): settings, not leaked secrets.
        var plain = new HashSet<string>(StringComparer.Ordinal);
        if (config is not null)
        {
            IReadOnlyList<AdapterDescriptor> known = [];
            try { known = AdapterDescriptor.Load(descriptors); }
            catch (ConfigException e) { report.Fail(e.Message); }
            var profiles = (env.GetValueOrDefault("COMPOSE_PROFILES") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in known.Where(d => mounts is null && config.AdapterEnabled(d.Name)))
            {
                var before = report.Failures;
                foreach (var secret in d.Secrets.Where(s => s.Required || File.Exists(deploy.AdapterSecret(d.Name, s.Purpose))))
                    Secret(report, deploy, deploy.AdapterSecret(d.Name, secret.Purpose), Owners.AdapterSecret(d.Name), true, values, null,
                        $"needed by the enabled adapter {d.Name}: ./rhythm connect {d.Name}");
                var token = deploy.Secret($"harness_{d.Name}_token");
                if (d.HarnessToken && !File.Exists(token))
                    report.Fail(Directory.Exists(token)
                        ? $"{deploy.Shown(token)} is a folder Docker made: remove it, then ./rhythm token create {d.Name}"
                        : $"{deploy.Shown(token)} is missing: needed by the enabled adapter {d.Name}: ./rhythm token create {d.Name}");
                if (d.Profile is { } profile && !profiles.Contains(profile))
                    report.Fail($"adapter {d.Name} is on but its profile {profile} is not in COMPOSE_PROFILES (.env): ./rhythm connect {d.Name}");
                if (report.Failures == before) report.Ok($"adapter {d.Name} is on{(d.Secrets.Count > 0 ? " and has its secrets" : "")}");
            }
            foreach (var d in known)
                foreach (var setting in d.Settings.Where(s => s.Pattern is not null))
                    foreach (var value in setting.Type == "list" ? config.Adapter(d.Name).List(setting.Key)
                        : config.Adapter(d.Name).String(setting.Key) is { } one ? [one] : [])
                        if (Regex.IsMatch(value, setting.Pattern!)) plain.Add(value);
            if (mounts is null) Ports(report, config, env);
            foreach (var name in PrivateUnderHousehold.Where(n => config.AdapterEnabled(n) && (config.Adapter(n).Owner ?? RhythmConfig.Household) == RhythmConfig.Household))
                report.Note($"adapter {name} keeps its personal records as private rows under the household: with memory scopes on, Family reads them "
                    + $"(set adapters.{name}.owner to a person for them to be that person's; docs/dev/memory.md section 2)");
        }

        var leaks = 0;
        if (yaml is not null) leaks += Leaks(report, "rhythm.yaml", yaml, values, SettingsKeys(yaml), plain);
        if (envText is not null) leaks += Leaks(report, ".env", envText, values, env.Where(p => p.Value.Length > 0).Select(p => p.Key).Where(k => EnvSecretKey().IsMatch(k)), plain);
        if (leaks == 0 && (yaml is not null || envText is not null)) report.Ok(mounts is null ? "no secret-shaped values in rhythm.yaml or .env" : "no secret-shaped values in rhythm.yaml");

        var health = await probe(HttpMethod.Get, "http://engine:8080/health", ct);
        if (health is null) report.Skip("the stack is not running (or not reachable from here): engine and MCP checks skipped");
        else
        {
            if (health == 200) report.Ok("engine is healthy"); else report.Fail($"engine health answers {health}: ./rhythm logs engine");
            if (health == 200 && read is not null) Models(report, await read("http://engine:8080/health", ct));
            var mcp = await probe(HttpMethod.Post, "http://engine:8080/mcp", ct);
            if (mcp == 401) report.Ok("MCP answers and asks for a token"); else report.Fail($"MCP answers {mcp?.ToString() ?? "nothing"} without a token, not 401");
            var app = await probe(HttpMethod.Get, "http://app:8080/api/health", ct);
            if (app == 200) report.Ok("app is healthy"); else report.Fail($"app health answers {app?.ToString() ?? "nothing"}: ./rhythm logs app");
        }
        if (Uri.TryCreate(config?.Engine.PublicMcpUrl, UriKind.Absolute, out var address))
        {
            if (address.IsLoopback)
                report.Skip("engine.publicMcpUrl uses the host's loopback; check the published port from the host");
            else
            {
                var status = await probe(HttpMethod.Post, address.AbsoluteUri, ct);
                if (status == 401) report.Ok("public MCP answers and asks for a token");
                else report.Fail($"public MCP answers {status?.ToString() ?? "nothing"} without a token; expected 401 (check engine.publicMcpUrl and the public route)");
            }
        }
        // From the database itself, whether or not the engine answers: a recorded mode that does not match rhythm.yaml
        // is just what stops the engine's start, so the stack is then down while Postgres still runs.
        if (config is not null) await Scopes(report, config, database, ct);
        o.WriteLine(report.Failures == 0 ? "No problems found." : $"{report.Failures} problem{(report.Failures == 1 ? "" : "s")} found.");
        return report.Failures == 0 ? 0 : 1;
    }

    // The memory scope reports (docs/dev/memory.md sections 6.3 and 6.4), from the database with the engine's login:
    // the recorded mode against rhythm.yaml's (a mismatch stops the engine's start, so it fails here too); a token bound
    // to a person who is suspended or former (refused at the door); each source share; and with scopes on, each token
    // with no person and a Family without an admin. With scopes off, the preview of switching on: for each token, what
    // it would stop reading, as counts per person and provider, never a title. The preview runs the switch inside a
    // transaction that is rolled back, so nothing is recorded and no other session sees it.
    static async Task Scopes(Report report, RhythmConfig config, Func<NpgsqlDataSource>? database, CancellationToken ct)
    {
        NpgsqlDataSource? owned = null;
        try
        {
            // A short connect timeout: with the whole stack down there is no database to wait for.
            var source = database?.Invoke()
                ?? (owned = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Engine.EngineConnection()) { Timeout = 5 }.ConnectionString));
            await using var c = await source.OpenConnectionAsync(ct);
            async Task<List<object?[]>> Rows(string sql, NpgsqlTransaction? tx = null, params (string, object?)[] args)
            {
                await using var cmd = Database.Command(c, tx, sql, args);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var rows = new List<object?[]>();
                while (await reader.ReadAsync(ct))
                {
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows;
            }
            var recorded = (await Rows("SELECT value FROM install_settings WHERE key='memory.scopes'")).Select(r => r[0] as string).FirstOrDefault();
            var wanted = MemorySettings.From(config).Scopes ? "on" : "off";
            if (recorded is null)
            { report.Fail("the database records no memory.scopes mode, so the engine does not start (docs/dev/memory.md section 6.2)"); return; }
            if (recorded != wanted)
            {
                report.Fail($"the database records memory.scopes {recorded} and rhythm.yaml says {wanted}, so the engine does not start "
                    + "(docs/dev/memory.md section 6.2)");
                return;
            }
            var on = recorded == "on";
            // A viewer session's credential past its 30 days (lapsed) is refused at the door like a revoked token, and
            // every one is while app.viewers is off (Database.Authenticate, Engine.Door). A terminal login (044) is a
            // harness principal no token exists for, made by rhythm remember, recall and list: counted apart from the
            // harness tokens, as rhythm token list names its role terminal.
            var tokens = await Rows($"""
                SELECT p.id,p.name,p.role,p.person_id,s.status,(p.role='app' AND p.person_id IS NOT NULL AND NOT {ViewerSessions.Current}),p.terminal
                FROM principals p LEFT JOIN subjects s ON s.id=p.person_id
                WHERE p.enabled AND p.role IN ('harness','app') ORDER BY p.role,p.name,p.created_at
                """);
            var viewers = ViewerSettings.From(config).On;
            bool Session(object?[] t) => (string)t[2]! == "app" && t[3] is not null;
            bool Terminal(object?[] t) => (bool)t[6]!;
            bool Refused(object?[] t) => t[3] is not null && (string?)t[4] != "current" || (bool)t[5]! || Session(t) && !viewers;
            string Kind(object?[] t) => Terminal(t) ? "terminal login" : $"{t[2]} token";
            var harness = tokens.Where(t => (string)t[2]! == "harness" && !Terminal(t)).ToList();
            var terminals = tokens.Count(Terminal);
            report.Ok($"memory scopes are {recorded} in rhythm.yaml and the database{(on ? "" : ": the household shares everything")}; "
                + $"{harness.Count} harness token(s), {harness.Count(t => t[3] is not null)} bound to a person"
                + (terminals > 0 ? $"; {terminals} terminal login(s) for rhythm remember, recall and list, which hold no token" : ""));
            foreach (var t in tokens.Where(t => t[3] is not null && (string?)t[4] != "current"))
                report.Note($"{(Terminal(t) ? "terminal login" : "token")} {t[1]} speaks for {t[3]}, who is {t[4]}: it is refused; ./rhythm token revoke {t[1]}");
            foreach (var t in tokens.Where(t => (string?)t[4] == "current" && (bool)t[5]!))
                report.Note($"viewer session {t[1]} of {t[3]} is past its 30 days: it is refused; ./rhythm viewer list disables it");
            if (!viewers && tokens.Count(t => Session(t) && (string?)t[4] == "current" && !(bool)t[5]!) is > 0 and var idle)
                report.Note($"{idle} viewer session(s) still enabled: with app.viewers off they are refused at the door; "
                    + "./rhythm viewer revoke --person <id> ends them for good");
            foreach (var share in await Rows("SELECT person_id,provider FROM source_shares ORDER BY 1,2"))
                report.Note($"source share: {share[0]}'s {share[1]} records are read by Family while memory scopes are on");
            if (on)
            {
                foreach (var t in harness.Where(t => t[3] is null))
                    report.Note($"token {t[1]} has no person: it reads Family and DEMO only (./rhythm token create <name> --person <id>)");
                if ((await Rows("SELECT 1 FROM memberships WHERE role='admin' LIMIT 1")).Count == 0)
                    report.Note("Family has no admin: a Family memory can then be changed only by its author (docs/dev/memory.md section 4)");
                // The index counts the engine leaves out of /health and app_status while scopes are on, since they count
                // every scope's rows together (ModelStatus): the operator's, from the database. Passages only: the rows
                // the index job has left depend on the adapters it covers, which the engine alone knows.
                foreach (var row in await Rows("SELECT model,count(*) FROM resource_chunks GROUP BY 1 ORDER BY 1"))
                    report.Ok($"the index holds {row[1]} passages of model {row[0]}, every scope's together (left out of /health and app_status "
                        + "while memory scopes are on)");
                return;
            }
            // The preview: each token's readable catalog rows by person and provider, now and with the next start's switch
            // applied (scopes recorded on, and at the first switch-on everyone in people in Family), then rolled back.
            // Derived records (insights, dashboards, pins, corners) are not counted: which stars a token reads is the
            // map's to say, and they narrow at each read with what they draw on (docs/dev/memory.md section 8).
            var count = $"SELECT r.subject_id,r.provider,count(*) FROM resources r WHERE {Database.Visible()} GROUP BY 1,2";
            async Task<Dictionary<Guid, Dictionary<(string, string), long>>> Readable(NpgsqlTransaction tx)
            {
                var all = new Dictionary<Guid, Dictionary<(string, string), long>>();
                foreach (var t in tokens)
                    all[(Guid)t[0]!] = (await Rows(count, tx, ("principal", t[0]))).ToDictionary(r => ((string)r[0]!, (string)r[1]!), r => (long)r[2]!);
                return all;
            }
            await using var tx = await c.BeginTransactionAsync(ct);
            var before = await Readable(tx);
            await Rows("UPDATE install_settings SET value='on' WHERE key='memory.scopes' RETURNING 1", tx);
            // Only people the engine has made subjects: one listed since its last start joins at its next. And only at the
            // switch's first start: once scopes have been on (Principals.JoinedKey), switching on again adds nobody.
            await Rows("""
                INSERT INTO memberships(person_id) SELECT id FROM subjects WHERE kind='person' AND id=ANY(@p)
                  AND NOT EXISTS(SELECT 1 FROM install_settings WHERE key=@joined) ON CONFLICT DO NOTHING RETURNING 1
                """, tx, ("p", config.People.Select(p => p.Id).ToArray()), ("joined", Principals.JoinedKey));
            var after = await Readable(tx);
            await tx.RollbackAsync(ct);
            var losing = 0;
            // A token refused for its person, and a viewer session refused at the door, reads nothing now, whatever the
            // mode, so it has nothing to lose.
            foreach (var t in tokens.Where(t => !Refused(t)))
            {
                var id = (Guid)t[0]!;
                var lost = before[id].Select(p => (p.Key.Item1, p.Key.Item2, Lost: p.Value - after[id].GetValueOrDefault(p.Key))).Where(p => p.Lost > 0).ToList();
                if (lost.Count == 0) continue;
                losing++;
                var who = t[3] is string person ? $"speaks for {person}" : "household view";
                report.Note($"with memory scopes on, {Kind(t)} {t[1]} ({who}) would stop reading "
                    + string.Join("; ", lost.GroupBy(l => l.Item1).OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => $"{g.Key}: " + string.Join(", ", g.OrderBy(l => l.Item2, StringComparer.Ordinal).Select(l => $"{l.Item2} {l.Lost}")))));
            }
            if (losing == 0) report.Ok("with memory scopes on, no token would stop reading anything it reads now");
            await Dashboards(report, Rows);
        }
        catch (Exception e) when (e is NpgsqlException or ConfigException or TimeoutException or System.Net.Sockets.SocketException)
        { report.Skip($"the database could not be read from here ({e.GetType().Name}): memory scope checks skipped"); }
        finally { if (owned is not null) await owned.DisposeAsync(); }
    }

    // The derived records the preview's counts leave out that narrow at the switch by what they are, not by the stars they
    // cite: a Family dashboard whose newest version reads or writes a log that names no subject, or names a person (with
    // scopes off a log the AI client named no subject for is saved as the first person's). From the switch on such a
    // dashboard is read only by that person's readers, or by no one when its log names none: the household view, its
    // screens and everyone else get it as an unknown one (docs/dev/memory.md sections 5 and 8). Listed with their ids and
    // titles, which are Family's own, so the operator can have the AI client save each again with `subject: household`
    // (docs/security.md, How an install would switch on).
    static async Task Dashboards(Report report, Func<string, NpgsqlTransaction?, (string, object?)[], Task<List<object?[]>>> rows)
    {
        var narrowing = await rows("""
            SELECT d.id,v.title,string_agg(DISTINCT coalesce(f->>'subject','no subject'),', ' ORDER BY coalesce(f->>'subject','no subject'))
            FROM dashboards d
            JOIN dashboard_versions v ON v.dashboard_id=d.id AND v.version=(SELECT max(x.version) FROM dashboard_versions x WHERE x.dashboard_id=d.id)
            CROSS JOIN LATERAL jsonb_array_elements(coalesce(CASE WHEN jsonb_typeof(v.feeds)='array' THEN v.feeds END,'[]'::jsonb)
              || coalesce(CASE WHEN jsonb_typeof(v.writes)='array' THEN v.writes END,'[]'::jsonb)) f
            WHERE d.subject_id='household' AND f->>'kind'='log'
              AND NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=f->>'subject' AND s.kind<>'person')
            GROUP BY d.id,v.title ORDER BY v.title,d.id
            """, null, []);
        if (narrowing.Count == 0)
        {
            report.Ok("with memory scopes on, every Family dashboard's log is the household's: none narrows by its log");
            return;
        }
        report.Note($"with memory scopes on, {narrowing.Count} Family dashboard(s) would be read only by the person their log names, "
            + "or by no one when it names none, and leave the household view and its screens: "
            + string.Join("; ", narrowing.Select(d => $"{d[0]} \"{d[1]}\" (log: {d[2]})"))
            + ". Have the AI client save a new version of each whose log feed and write name subject: household");
    }

    // The engine's version and each model's state, from its /health (ModelStatus; docs/models.md). Notes only, never a
    // failure: every model is optional, and search matches words without one. `ok` when a model is as the settings ask
    // (on, or off by them), `note` otherwise.
    static void Models(Report report, string? body)
    {
        JsonElement root;
        try { using var document = JsonDocument.Parse(body ?? ""); root = document.RootElement.Clone(); }
        catch (JsonException) { report.Note("the engine's health gave no model states"); return; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        { report.Note("the engine reports no model states: it is older than this command (./rhythm upgrade)"); return; }
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Text(root, "version") is { } version && root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number)
            report.Ok($"engine {version}, schema {schema.GetRawText()}");
        foreach (var model in models.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
        {
            var (job, role, name, state) = (Text(model, "job"), Text(model, "role"), Text(model, "name"), Text(model, "state"));
            if (job == "titles")
            {
                if (state == "off") report.Ok("titles model is off: stars keep their excerpts as titles (docs/adapters/titles.md)");
                else if (state == "on") report.Ok($"titles model {name} is on");
                else
                {
                    var failure = model.TryGetProperty("failure", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
                    report.Note($"titles model {name} is {state}" + (failure.ValueKind == JsonValueKind.Object ? $": {Text(failure, "kind")} at {Text(failure, "at")}" : "")
                        + "; stars keep their excerpts until a rebuild titles them (./rhythm logs engine)");
                }
                continue;
            }
            if (job != "embeddings") continue;
            var label = $"embeddings model {name} ({role})";
            // The map model (graph.similarity.model, D-256): only the map's similar links read it, and it has no index.
            if (role == "map")
            {
                if (state == "on") report.Ok($"{label} is on: the map's similar links use its vectors");
                else if (state == "off") report.Ok($"{label} is off: the map draws no similar links (graph.similarity.threshold, adapters.embeddings.enabled)");
                else report.Note($"{label} is {state}: the map's similar links use the live model's vectors until it loads (./rhythm logs engine; docs/models.md)");
                continue;
            }
            var index = model.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            string Index()
            {
                if (index.ValueKind != JsonValueKind.Object) return "index counts not read yet, or left out while memory scopes are on (see below)";
                var passages = index.TryGetProperty("passages", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                var indexing = index.TryGetProperty("indexing", out var x) && x.ValueKind == JsonValueKind.True;
                var pending = index.TryGetProperty("pending", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : (long?)null;
                return $"{passages} passages; " + (!indexing ? "the index job is off"
                    : Text(index, "pausedUntil") is { } until ? $"the index job is paused until {until}"
                    : pending is > 0 ? $"the index job has {pending} rows left" : "the index job is up to date");
            }
            switch (state)
            {
                case "on": report.Ok($"{label} is on: {Index()}"); break;
                case "off": report.Ok($"{label} is off (adapters.embeddings.enabled): search matches words only"); break;
                case "starting" or "downloading": report.Note($"{label} is {state}: search matches words only until it loads"); break;
                case "download-failed": report.Note($"{label} could not be downloaded: search matches words only (./rhythm logs engine; docs/models.md)"); break;
                case "unverified": report.Note($"{label} has files that do not match their pinned checksums: it stays off (docs/models.md)"); break;
                default: report.Note($"{label} is {state}: search matches words only (./rhythm logs engine)"); break;
            }
            if (state == "on" && index.ValueKind == JsonValueKind.Object && Text(index, "pausedUntil") is not null)
                report.Note($"{label}'s index job is paused: a sweep embedded nothing (./rhythm logs engine)");
        }
    }

    sealed class Report(TextWriter o)
    {
        public int Failures { get; private set; }
        public void Ok(string line) => o.WriteLine($"  ok    {line}");
        public void Skip(string line) => o.WriteLine($"  skip  {line}");
        public void Note(string line) => o.WriteLine($"  note  {line}");
        public void Fail(string line) { Failures++; o.WriteLine($"  FAIL  {line}"); }
    }

    // One secret file: there, a file, not empty, the right shape, and private (no access for others; on a host that
    // keeps owners, owned by the user of the service that reads it).
    static void Secret(Report report, DeployFolder deploy, string path, Owners.Rule? rule, bool required, List<string> values,
        Func<string, string?>? shape, string? missingHint = null)
    {
        var shown = deploy.Shown(path);
        if (Directory.Exists(path)) { report.Fail($"{shown} is a folder (Docker makes one when the file is missing at up): run ./rhythm init"); return; }
        if (!File.Exists(path)) { if (required) report.Fail($"{shown} is missing: {missingHint ?? "run ./rhythm init"}"); return; }
        var value = SecretFiles.Read(path);
        if (value.Length == 0) { report.Fail($"{shown} is empty"); return; }
        values.Add(value);
        if (shape?.Invoke(value) is { } wrong) { report.Fail($"{shown} {wrong}"); return; }
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)) != 0)
            { report.Fail($"{shown} is readable or writable by others (mode {Convert.ToString((int)mode, 8)}): ./rhythm up makes it private"); return; }
            if (rule is not null && Environment.IsPrivilegedProcess && Owners.OwnerOf(path) is { } owner && owner != (rule.Uid, rule.Gid))
            { report.Fail($"{shown} is owned by {owner.Uid}:{owner.Gid}, but its reader runs as {rule.Uid}:{rule.Gid}: ./rhythm up fixes it"); return; }
        }
        report.Ok($"{shown} is there and private");
    }

    static Func<string, string?>? Shape(string name) => name switch
    {
        "app_token" => v => v.Length is >= 32 and <= 512 && !v.Any(char.IsWhiteSpace) ? null : "is not one token of 32-512 characters",
        "postgres_password" or "backup_password" => v => v.Length >= 16 ? null : "is shorter than 16 characters",
        "engine_db" => v =>
        {
            try
            {
                var b = new NpgsqlConnectionStringBuilder(v);
                return string.IsNullOrEmpty(b.Host) || string.IsNullOrEmpty(b.Username) || string.IsNullOrEmpty(b.Password)
                    ? "needs Host, Username and Password" : null;
            }
            catch (ArgumentException) { return "is not a connection string"; }
        },
        _ => null,
    };

    // Where a secret could have leaked into a settings file: a secret file's value, a key that names a secret, or a
    // long random-looking value. The output names the file, line and key, never the value.
    static int Leaks(Report report, string file, string text, List<string> secrets, IEnumerable<string> secretKeys, IReadOnlySet<string> plain)
    {
        var found = 0;
        if (secrets.Any(s => s.Length >= 12 && text.Contains(s, StringComparison.Ordinal)))
        { report.Fail($"{file} holds the value of one of the secret files"); found++; }
        foreach (var key in secretKeys) { report.Fail($"{file} sets {key}: secrets are files under secrets/, never values here"); found++; }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Only the value counts: a .env name joined to it by '=' would make a long project name or tag look random.
            var line = lines[i].TrimStart().StartsWith('#') ? "" : EnvName().Replace(lines[i], "");
            if (Candidate().Matches(line).Any(m => RandomLooking(m.Value) && !plain.Contains(m.Value))) { report.Fail($"{file} line {i + 1} holds a value that looks like a secret"); found++; }
        }
        return found;
    }
    static bool RandomLooking(string value)
    {
        if (!value.Any(char.IsAsciiDigit) || !value.Any(char.IsAsciiLetter)) return false;
        if (value.All(Uri.IsHexDigit)) return true;
        var counts = value.GroupBy(c => c).Select(g => (double)g.Count() / value.Length);
        return -counts.Sum(p => p * Math.Log2(p)) >= 4.0;
    }
    // Every mapping key in rhythm.yaml that names a secret (not just adapters: the engine checks those at start).
    static IEnumerable<string> SettingsKeys(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); } catch (YamlDotNet.Core.YamlException) { yield break; }
        var stack = new Stack<(YamlNode Node, string Path)>(stream.Documents.Select(d => (d.RootNode, "")));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            if (node is YamlMappingNode map)
                foreach (var (k, v) in map.Children)
                {
                    var key = (k as YamlScalarNode)?.Value ?? "";
                    var full = path.Length == 0 ? key : $"{path}.{key}";
                    if (SettingSecretKey().IsMatch(key)) yield return full;
                    stack.Push((v, full));
                }
            else if (node is YamlSequenceNode list) foreach (var item in list) stack.Push((item, path));
        }
    }

    // A local address in engine.publicMcpUrl or app.publicUrl should use the port .env publishes.
    static void Ports(Report report, RhythmConfig config, Dictionary<string, string> env)
    {
        foreach (var (key, url, variable, fallback) in new[]
        {
            ("engine.publicMcpUrl", config.Engine.PublicMcpUrl, "RHYTHM_MCP_PORT", "8181"), ("app.publicUrl", config.App.PublicUrl, "RHYTHM_APP_PORT", "8080"),
        })
        {
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback) continue;
            var port = env.GetValueOrDefault(variable) is { Length: > 0 } p ? p : fallback;
            if (uri.Port.ToString() != port) report.Fail($"{key} uses port {uri.Port}, but .env publishes {variable}={port}");
        }
    }

    [GeneratedRegex("[A-Za-z0-9+_=-]{32,}")] private static partial Regex Candidate();
    [GeneratedRegex("^\\s*[A-Za-z_][A-Za-z0-9_]*=")] private static partial Regex EnvName();
    [GeneratedRegex("(?i)(token|password|secret|apikey)$")] private static partial Regex SettingSecretKey();
    [GeneratedRegex("(?i)(^|_)(TOKEN|PASSWORD|SECRET|API_?KEY)$")] private static partial Regex EnvSecretKey();
}
