using System.Text.Json;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using ModelContextProtocol.Server;
using Npgsql;

// The engine: one process that serves MCP at /mcp for three roles (harness, app, ingest), and the app alone a document's
// archived original at GET /app/documents/<id>; it keeps its own clock and runs the operator commands below. Settings
// come from rhythm.yaml (RHYTHM_CONFIG), secrets from *_FILE variables.
//
//   serve (default)                       the MCP server and scheduler
//   migrate                               apply migrations (admin login) and set up the engine's database login
//   refresh                               rebuild the map once now
//   check-config                          load rhythm.yaml and report what the engine would do
//   init | connect | disconnect | doctor | preflight | ready | token | person
//                                         the install commands (engine/src/Cli/, cli/README.md)
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "serve";
try
{
    if (Knowledge.Cli.CliCommands.Names.Contains(command)) { Environment.ExitCode = await Knowledge.Cli.CliCommands.Run(command, args[1..], stop.Token); return; }
    var config = RhythmConfig.Load(Environment.GetEnvironmentVariable("RHYTHM_CONFIG") ?? "/etc/rhythm/rhythm.yaml");
    RhythmConfig.Current = config;
    var data = Environment.GetEnvironmentVariable("RHYTHM_DATA") ?? "/var/lib/rhythm";
    switch (command)
    {
        case "serve": await Serve(config, data, args, stop.Token); return;
        case "migrate": await Engine.Migrate(stop.Token); return;
        case "check-config":
            Console.WriteLine($"Settings valid: {config.People.Count} people, time zone {config.Timezone}, demo {(config.Demo.Enabled ? "on" : "off")}, "
                + $"adapters on: {string.Join(", ", config.Adapters.Where(a => config.AdapterEnabled(a.Key)).Select(a => a.Key).DefaultIfEmpty("none"))}, "
                + $"plugins on: {string.Join(", ", config.Plugins.Where(p => p.Value.Enabled).Select(p => p.Key).DefaultIfEmpty("none"))}.");
            return;
        case "refresh":
        {
            var registry = AdapterRegistry.For(config);
            var db = Engine.Database(config, registry);
            registry.Configure(config, db, new Archive(Path.Combine(data, "archive")));
            var principal = await new Principals(db.Source).System(Principals.GraphName, stop.Token);
            using var embeddings = new Embeddings(EmbeddingSettings.From(config).Model);
            await embeddings.Start(config, data, stop.Token);
            var host = Engine.Host(db, config, registry, () => principal, data, embeddings);
            Console.WriteLine(await host.Refresh(false, stop.Token));
            foreach (var note in host.Notes) Console.WriteLine(note);
            return;
        }
        default: throw new ArgumentException("Unknown command; see the engine README");
    }
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine(ex switch
    {
        ConfigException c => $"Settings problem: {c.Message}",
        ArgumentException a => a.Message,
        PostgresException pg => $"Database operation failed (SQLSTATE {pg.SqlState}); check migrations and configuration.",
        NpgsqlException => "The database is not reachable; is the stack up (./rhythm up)?",
        IOException or UnauthorizedAccessException => $"File problem: {ex.Message}",
        InvalidOperationException i when i.Message.StartsWith("Applied migration changed", StringComparison.Ordinal) => i.Message + " (see docs/dev/migration-compat.md)",
        _ => $"Operation failed ({ex.GetType().Name}); check configuration and command arguments. No sensitive details logged."
    });
    Environment.ExitCode = 1;
}

static async Task Serve(RhythmConfig config, string data, string[] args, CancellationToken stopping)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = IngestEndpoint.MaxBody);
    // No source payloads, URLs with credentials, SQL parameters or request headers in logs: only our own lines.
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ "; o.UseUtcTimestamp = true; });
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning).AddFilter("System", LogLevel.Warning).AddFilter("ModelContextProtocol", LogLevel.Warning);
    builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
    var archive = new Archive(Environment.GetEnvironmentVariable("RHYTHM_ARCHIVE") ?? Path.Combine(data, "archive"));
    // One database and pool, whose provider policies include every enabled adapter's, for the adapters and everything else.
    var registry = AdapterRegistry.For(config);
    var db = Engine.Database(config, registry);
    if (OAuthSettings.From(config) is not null) OAuthServer.Add(builder.Services, config, db, data);
    registry.Configure(config, db, archive);
    var state = new EngineState(data);
    var embeddings = new Embeddings(EmbeddingSettings.From(config).Model);
    var host = Engine.Host(db, config, registry, () => state.GraphPrincipal, data, embeddings, () => state.Ready);

    builder.Services.AddSingleton(config).AddSingleton(db).AddSingleton(archive).AddSingleton(registry).AddSingleton(state).AddSingleton(embeddings);
    // engine.rerank's cross-encoder (off by default, SB-P2-12): nothing is loaded or downloaded while it is off; on, it loads
    // in the background and searches keep the combined order until its readiness probe passes (Reranking).
    var reranking = new Reranking(RecallSettings.From(config).Rerank);
    builder.Services.AddSingleton(reranking);
    if (reranking.Spec is not null) _ = Task.Run(() => reranking.Start(data, stopping), stopping);
    builder.Services.AddSingleton(host);
    builder.Services.AddSingleton(db.Source);
    builder.Services.AddSingleton<InsightStore>().AddSingleton<DashboardStore>().AddSingleton<RefreshRequests>().AddSingleton<Principals>();
    builder.Services.AddSingleton<DocumentStore>().AddSingleton<Ingest>().AddSingleton<AppHealth>();
    builder.Services.AddSingleton<DashboardFeeds>().AddSingleton<DashboardWriter>();
    // Each model's job and state, for /health, app_status and rhythm doctor (SB-P2-01, docs/models.md).
    builder.Services.AddSingleton(new ModelStatus(db, config, embeddings, registry, host));
    // The screens' waits live in this one process; the store reads and writes the screens and their commands.
    builder.Services.AddSingleton<ScreenWaits>().AddSingleton<ScreenStore>().AddSingleton<StudioStore>();
    // Sync now from the app's Sources view (SB-P6-09): the requests app_sync records, which the scheduler runs.
    builder.Services.AddSingleton<SyncRequests>();
    // Viewer sessions in the app only while app.viewers is on, which this version refuses when the settings load
    // (ViewerSettings, D-046): without the service app_viewer is neither listed nor accepted, and the door refuses every
    // session's credential (Database.Authenticate), so a session opened while they were on reads nothing once they are off.
    var viewers = ViewerSettings.From(config).On;
    if (viewers) builder.Services.AddSingleton(new ViewerSessions(db.Source));
    builder.Services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<ILogger<RequestLog>>()));
    builder.Services.AddSingleton<ISourceValidation, SourceValidation>().AddSingleton<Retrieval>().AddSingleton(sp => new HealthInvestigation(sp.GetRequiredService<Database>()));
    builder.Services.AddHostedService<Scheduler>();
    builder.Services.AddHttpContextAccessor();
    Engine.Mcp(builder.Services, config, registry.Enabled.SelectMany(a => a.HarnessTools()));

    var app = builder.Build();
    var oauth = app.Services.GetService<OAuthServer>();
    if (oauth is not null) { await oauth.Initialize(app.Services, stopping); oauth.Use(app, () => state.Ready); }
    // Origin, Host, readiness, the bearer token and its role, before any route (Mcp/Perimeter.cs); refusals are logged.
    app.Use(new Perimeter(config.Engine.AllowedHosts, () => state.Ready, Engine.Door(db, config), new(
        App: (context, caller) => AppEndpoint.Handle(context, caller, host, context.RequestServices.GetRequiredService<AppHealth>(), registry),
        Ingest: (context, caller) => IngestEndpoint.Handle(context, caller, context.RequestServices.GetRequiredService<Ingest>(), config),
        Document: (context, caller, document) => AppFiles.Handle(context, caller.Id, document, db, archive, config),
        Capture: Engine.CaptureRoute(config, db)),
        app.Services.GetRequiredService<RequestLog>(), oauth).Handle);
    if (oauth is not null) oauth.Map(app);
    app.MapGet("/health", async (HttpContext context) =>
    {
        try
        {
            if (!state.Ready || !await db.Current(context.RequestAborted)) return Results.Json(new { status = "starting" }, statusCode: 503);
            // The fields it always had, then the engine's version and schema and each model's state (ModelStatus).
            return Results.Json(await app.Services.GetRequiredService<ModelStatus>().Health(context.RequestAborted));
        }
        catch { return Results.StatusCode(503); }
    });
    app.MapMcp("/mcp");
    await app.RunAsync(stopping);
}

public static class Engine
{
    // The engine's own login: RHYTHM_DB_FILE (a connection string), else built from RHYTHM_DB_HOST, RHYTHM_DB_NAME,
    // RHYTHM_DB_USER and RHYTHM_DB_PASSWORD_FILE.
    public static string EngineConnection() =>
        SecretFiles.TryRead("RHYTHM_DB_FILE") ?? Built("RHYTHM_DB_USER", "RHYTHM_DB_PASSWORD_FILE", "rhythm_admin", "POSTGRES_PASSWORD_FILE");
    // The owner login for migrations: RHYTHM_ADMIN_DB_FILE, else the Postgres image's own user and password file.
    public static string AdminConnection() =>
        SecretFiles.TryRead("RHYTHM_ADMIN_DB_FILE") ?? Built("RHYTHM_DB_ADMIN_USER", "POSTGRES_PASSWORD_FILE", "rhythm_admin", null);
    static string Built(string userVariable, string passwordVariable, string defaultUser, string? fallbackPassword)
    {
        var password = SecretFiles.TryRead(passwordVariable) ?? (fallbackPassword is null ? null : SecretFiles.TryRead(fallbackPassword))
            ?? throw new ConfigException($"no database credentials: set RHYTHM_DB_FILE, or {passwordVariable} to a password file");
        return new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("RHYTHM_DB_HOST") ?? "postgres",
            Database = Environment.GetEnvironmentVariable("RHYTHM_DB_NAME") ?? "rhythm",
            Username = Environment.GetEnvironmentVariable(userVariable) ?? defaultUser,
            Password = password,
        }.ConnectionString;
    }

    public static Database Database(RhythmConfig config, AdapterRegistry registry) =>
        new(new NpgsqlDataSourceBuilder(EngineConnection()) { Name = "rhythm-engine" }.Build(), registry.Policies(config));

    // The one MCP server at /mcp (contracts/README.md 2): Streamable HTTP, stateless, the harness tools with their aliases,
    // the enabled adapters' tools and the gated ones, the dashboard guide as a resource and a prompt, the prompt capsules,
    // and tools/list filtered by the settings (HarnessTools.Filter). With memory.tools on, a client is also given the memory
    // instructions when it connects (SB-P3-20); off there are none, so the handshake answers exactly as before. Serve maps
    // it behind the door (Perimeter); the client compatibility tests build the same server (McpClientCompatibilityTests).
    // With engine.writeLimit on the services hold the one per-token write limit its tools and the other doors ask (WriteLimit).
    public static IMcpServerBuilder Mcp(IServiceCollection services, RhythmConfig config, IEnumerable<McpServerTool> adapterTools) =>
        WriteLimit.Serve(services, config).AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "rhythm-engine", Version = typeof(Engine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
                o.ServerInstructions = MemoryInstructions.For(config);
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(HarnessTools.Create(config, adapterTools))
            .WithResources([McpServerResource.Create(() => ToolText.DashboardGuide, new McpServerResourceCreateOptions
                { UriTemplate = ToolText.DashboardGuideUri, Name = "dashboards", Description = "How a dashboard should look and behave in Rhythm of Life.", MimeType = "text/markdown" }),
                // The prompt capsules (SB-P3-08), only with memory.capsules on: none otherwise, so resources/list is as before.
                .. CapsuleResources.Create(config)])
            .WithPrompts([McpServerPrompt.Create(() => ToolText.DashboardGuide, new McpServerPromptCreateOptions
                { Name = ToolText.DashboardGuideUri, Description = "The dashboard design guide, as a prompt to read before save_dashboard." })])
            .WithRequestFilters(f => f.AddListToolsFilter(next => async (request, ct) =>
                HarnessTools.Filter(await next(request, ct), request.Services!, config)));

    // The door's check of a bearer token (Perimeter): a viewer session's credential is admitted only while the settings
    // have app.viewers on, so with it off every session reads nothing and its browser gets the household view.
    public static Func<string, CancellationToken, Task<Caller?>> Door(Database db, RhythmConfig config)
    {
        var viewers = ViewerSettings.From(config).On;
        return (token, ct) => db.Authenticate(token, viewers, ct);
    }

    // The capture route only with memory.capture on beside memory.tools: otherwise it is no route at all (SB-P7-01).
    public static Func<HttpContext, Caller, Task>? CaptureRoute(RhythmConfig config, Database db) => MemorySettings.CaptureOn(config)
        ? (context, caller) => CaptureEndpoint.Handle(context, caller, new MemoryStore(db.Source))
        : null;

    public static RelationshipHost Host(Database db, RhythmConfig config, AdapterRegistry registry, Func<Guid> principal, string data,
        Embeddings embeddings, Func<bool>? ready = null)
    {
        var state = Path.Combine(data, "state");
        // With graph.similarity.model set, the map model's vectors too, once it has loaded (D-256); before then, or when it
        // cannot load, the pass makes none and the map's similar links use the live model's (RelationshipHost.Refresh).
        return new RelationshipHost(db, () => ready is null || ready() ? RelationshipHostConfiguration.From(config, principal(), state) : null,
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct),
                embeddings.Model is null ? null : embeddings.Vector, embeddings.Id)
            {
                MapEmbed = c.Map is not null && embeddings.Map is { Model: not null } map && map.Spec == c.Map.Model ? map.Vector : null,
                MapEmbedModel = c.Map?.Model.Id,
                // Not loaded yet since the start ("off" until its holder is told to load) reads as starting, as ModelStatus does.
                MapStatus = c.Map is null ? null : embeddings.Map?.Status is { } status && status != "off" ? status : "starting",
            }, registry);
    }

    // The one-shot migrate service: migrations with the owner login, then the engine's own login (when RHYTHM_DB_FILE
    // names a different user) created or given its password, with data rights but no schema rights and, like every
    // login, no temporary tables (again at every run, which covers the new database a restore makes).
    public static async Task Migrate(CancellationToken ct)
    {
        await using var admin = NpgsqlDataSource.Create(AdminConnection());
        var accepted = Compatibility(Environment.GetEnvironmentVariable("RHYTHM_MIGRATION_COMPAT_FILE"));
        var applied = await new Database(admin).Migrate(ct, accepted);
        Console.WriteLine(applied.Count == 0 ? "Migrations: already current." : $"Migrations applied: {applied.Count}.");
        if (SecretFiles.TryRead("RHYTHM_DB_FILE") is not { } engine) return;
        var login = new NpgsqlConnectionStringBuilder(engine);
        var owner = new NpgsqlConnectionStringBuilder(AdminConnection());
        if (string.IsNullOrEmpty(login.Username) || login.Username == owner.Username) return;
        await using var c = await admin.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var exists = Knowledge.Database.Command(c, tx, "SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@r)", ("r", login.Username)))
        {
            var verb = await exists.ExecuteScalarAsync(ct) is true ? "ALTER" : "CREATE";
            // PostgreSQL quotes the identifier and the password literal; neither is interpolated by hand or logged.
            await using var format = Knowledge.Database.Command(c, tx, $"SELECT format('{verb} ROLE %I LOGIN PASSWORD %L',@r,@p)", ("r", login.Username), ("p", login.Password ?? ""));
            await Knowledge.Database.Execute(c, tx, (string)(await format.ExecuteScalarAsync(ct))!, ct);
        }
        await GrantLogin(c, tx, login.Username, ct);
        await tx.CommitAsync(ct);
        Console.WriteLine("Engine database login is set up.");
    }

    // The engine login's rights (docs/dev/engine.md, Database login): every table read and written, except that
    // schema_versions and the tables checked_tables() lists (the memory tables, written only through their checked
    // functions) are read only; the sequences and every function; no schema rights and no temporary tables.
    internal static async Task GrantLogin(NpgsqlConnection c, NpgsqlTransaction tx, string login, CancellationToken ct)
    {
        await using (var grant = Knowledge.Database.Command(c, tx, """
            SELECT format('GRANT CONNECT ON DATABASE %I TO %I; GRANT USAGE ON SCHEMA public TO %2$I;
              GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO %2$I;
              REVOKE INSERT,UPDATE,DELETE ON schema_versions FROM %2$I;
              GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO %2$I;
              GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO %2$I;
              REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE TEMPORARY ON DATABASE %1$I FROM PUBLIC',current_database(),@r)
            """, ("r", login)))
            await Knowledge.Database.Execute(c, tx, (string)(await grant.ExecuteScalarAsync(ct))!, ct);
        await using var restrict = Knowledge.Database.Command(c, tx, """
            SELECT format('REVOKE INSERT,UPDATE,DELETE,TRUNCATE ON %s FROM %I',string_agg(t::text,','),@r)
            FROM unnest(checked_tables()) t HAVING count(*)>0
            """, ("r", login));
        if (await restrict.ExecuteScalarAsync(ct) is string revoke) await Knowledge.Database.Execute(c, tx, revoke, ct);
    }

    // An existing install's accepted checksums for migrations this repository rewrote: lines of
    // "<version> <sha256>", from its private overlay. No such file is part of this repository.
    public static IReadOnlyDictionary<string, IReadOnlySet<string>>? Compatibility(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[0-9a-f]{64}$"))
                throw new ConfigException("the migration compatibility file holds lines of '<version> <sha256>'");
            (map.TryGetValue(parts[0], out var set) ? set : map[parts[0]] = []).Add(parts[1]);
        }
        return map.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value);
    }
}

public partial class Program { }
