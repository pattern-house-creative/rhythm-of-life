using Npgsql;

namespace Knowledge.Cli;

// The two steps around `rhythm up`: `preflight` before compose starts anything, `ready` once every service is healthy.
public static class StackCommands
{
    // Probes a URL on the stack's internal network: the HTTP status, or null when nothing answers there.
    public delegate Task<int?> Probe(HttpMethod method, string url, CancellationToken ct);
    // Reads a URL on the stack's internal network: its body when it answers 200, else null.
    public delegate Task<string?> Read(string url, CancellationToken ct);

    // GET, at most 64 KiB, no redirects; null for anything but a 200 or when nothing answers.
    public static async Task<string?> Body(string url, CancellationToken ct)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 65536 };
        try
        {
            using var response = await client.GetAsync(url, ct);
            return response.StatusCode == System.Net.HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    public static async Task<int?> Http(HttpMethod method, string url, CancellationToken ct)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post)
            request.Content = new StringContent("""{"jsonrpc":"2.0","id":"probe","method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json");
        try { using var response = await client.SendAsync(request, ct); return (int)response.StatusCode; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    // Stops `up` with one clear line when init has not run, then gives every secret, token and empty data folder the
    // owner and mode its reader needs (files written by hand, or folders Docker made, start out wrong).
    public static int Preflight(DeployFolder deploy, TextWriter o, string? dataMounts = null)
    {
        var missing = new[] { deploy.Settings, deploy.Env }.Concat(DeployFolder.CoreSecrets.Select(deploy.Secret)).Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            o.WriteLine($"Not set up yet ({string.Join(", ", missing.Select(deploy.Shown))} missing): run ./rhythm init first.");
            return 1;
        }
        RhythmConfig.Load(deploy.Settings);
        Owners.Apply(deploy.Settings, Owners.Person(deploy, Owners.Readable));
        Owners.Apply(deploy.Env, Owners.Person(deploy, Owners.Readable));
        Owners.Apply(deploy.Secrets, Owners.Person(deploy, Owners.PrivateFolder));
        foreach (var secret in DeployFolder.CoreSecrets) Owners.Apply(deploy.Secret(secret), Owners.CoreSecret(secret));
        foreach (var token in deploy.Tokens) Owners.Apply(token, Owners.Token(deploy, token));
        Owners.Folder(deploy.AdapterSecrets, Owners.AdapterFolder);
        foreach (var file in Directory.GetFiles(deploy.AdapterSecrets)) Owners.Apply(file, Owners.AdapterSecret(file));
        // The cli service mounts the install's data folders here (deploy/compose/engine.compose.yaml).
        var mounts = dataMounts ?? Environment.GetEnvironmentVariable("RHYTHM_CLI_DATA");
        if (mounts is { Length: > 0 } && Directory.Exists(mounts)) PrepareFolders(mounts, deploy, true);
        return 0;
    }

    // notes and import belong to the person (they put files there; the engine only reads), backups and backup-staging
    // to the backup container. Only a missing or empty folder is changed: a folder with files in it is someone's.
    public static void PrepareFolders(string data, DeployFolder deploy, bool notes)
    {
        var person = Owners.Person(deploy, Owners.OpenFolder);
        var backup = new Owners.Rule(Owners.Backup, Owners.Backup, Owners.PrivateFolder);
        foreach (var (name, rule) in new[] { ("notes", person), ("import", person), ("backups", backup), ("backup-staging", backup) })
        {
            if (name == "notes" && !notes) continue;
            var path = Path.Combine(data, name);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) continue;
            Owners.Folder(path, rule);
        }
    }

    // After `up`: checks the engine answers, makes the first harness token when there is none, and prints where to go.
    // Never prints a token: only the path of its file.
    public static async Task<int> Ready(DeployFolder deploy, TextWriter o, Probe probe, Func<Principals>? principals = null, CancellationToken ct = default)
    {
        var config = RhythmConfig.Load(deploy.Settings);
        var env = EnvFile.Read(deploy.Env);
        if (await probe(HttpMethod.Get, "http://engine:8080/health", ct) != 200 || await probe(HttpMethod.Post, "http://engine:8080/mcp", ct) != 401)
        {
            o.WriteLine("The engine is not answering as it should yet; see ./rhythm logs engine.");
            return 1;
        }
        await using var source = principals is null ? NpgsqlDataSource.Create(Engine.EngineConnection()) : null;
        var store = principals?.Invoke() ?? new Principals(source!);
        var tokens = Directory.GetFiles(deploy.Secrets, "harness_*_token").Order(StringComparer.Ordinal).ToList();
        if (tokens.Count == 0)
        {
            var first = deploy.Secret("harness_assistant_token");
            await store.Create("assistant", "harness", null, first, ct);
            Owners.Apply(first, Owners.Person(deploy, Owners.Private));
            tokens.Add(first);
            o.WriteLine("Made a first harness token, named assistant, for your AI client.");
        }
        if (env.GetValueOrDefault("RHYTHM_STARTER_WORKSPACE") == "yes" && StarterWorkspace.Available(config))
            await StarterWorkspace.Install(store.Source, config, "workspace", null, o, ct);
        string Port(string variable, string fallback) => env.GetValueOrDefault(variable) is { Length: > 0 } port ? port : fallback;
        o.WriteLine($"{config.Instance.Name} is running.");
        o.WriteLine($"  App:  {config.App.PublicUrl ?? "http://127.0.0.1:" + Port("RHYTHM_APP_PORT", "8080")}");
        o.WriteLine($"  MCP:  {config.Engine.PublicMcpUrl ?? "http://127.0.0.1:" + Port("RHYTHM_MCP_PORT", "8181") + "/mcp"}");
        foreach (var token in tokens)
        {
            var valid = SecretFiles.Read(token) is { Length: > 0 } value && await store.Enabled(value, ct);
            o.WriteLine($"  Harness token file: {deploy.Shown(token)}"
                + (Path.GetFileName(token) == Owners.TunnelToken ? " (the OpenAI tunnel's own: its container sends it)" : "")
                + (valid ? "" : " (no longer valid: revoked, or disabled by a restore; delete it, then make a new one with ./rhythm token create <name>)"));
        }
        o.WriteLine("Give your MCP client the MCP address, and the token file's contents as a Bearer token (harness/README.md).");
        return 0;
    }
}
