using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge.Cli;

// Each template and its installation receipt commit together. The receipt lives in the backed-up
// database, so restarts and restores also preserve renamed, edited or deliberately removed content.
public static class StarterWorkspace
{
    public static bool Available(RhythmConfig config) => MemorySettings.WorkOn(config) && MemorySettings.CapsulesOn(config);

    public static async Task<int> Run(DeployFolder deploy, string[] args, TextWriter output, CancellationToken ct)
    {
        if (args is not (["workspace"] or ["groceries"] or ["project", _]))
            throw new ArgumentException("Use starter workspace, starter groceries, or starter project <slug>.");
        var config = RhythmConfig.Load(deploy.Settings);
        if (!RhythmConfig.Loaded) RhythmConfig.Current = config;
        var project = args.Length == 2 ? args[1] : null;
        if (project is not null && !Regex.IsMatch(project, @"\A[a-z0-9][a-z0-9-]{0,63}\z"))
            throw new ArgumentException("Use a project slug of up to 64 lowercase letters, digits and hyphens.");
        if (args[0] == "groceries" ? !MemorySettings.ToolsOn(config) : !Available(config))
            throw new ArgumentException(args[0] == "groceries"
                ? "Enable memory.tools in rhythm.yaml and run rhythm up before adding Groceries."
                : "Enable memory.tools, memory.work and memory.capsules in rhythm.yaml and run rhythm up before adding this template.");
        await using var db = NpgsqlDataSource.Create(Engine.EngineConnection());
        await Install(db, config, args[0], project, output, ct);
        return 0;
    }

    internal static async Task Install(NpgsqlDataSource db, RhythmConfig config, string template, string? project, TextWriter output, CancellationToken ct)
    {
        var key = "starter." + (project is null ? template : "project." +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project)))[..32]);
        if (await new Principals(db).HasSetting(key, ct)) return;
        var person = await TerminalLogin.For(db, config.FirstPerson, ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        async Task<object?> Query(string sql, params (string, object?)[] args)
        {
            await using var cmd = Database.Command(conn, tx, sql, args);
            return await cmd.ExecuteScalarAsync(ct);
        }
        await Query("SELECT pg_advisory_xact_lock(hashtextextended('rhythm.starter',0))");
        if (await Query("INSERT INTO install_settings(key,value) VALUES(@k,'1') ON CONFLICT DO NOTHING RETURNING 1", ("k", key)) is null)
            return;

        async Task Note(string? slug, string tag, string content, string? slot = null)
        {
            var kind = slug is null ? "core" : "project";
            // Respect the operator's records, including capsule sources still awaiting confirmation.
            if (await Query("""
                SELECT 1 FROM memories m LEFT JOIN memory_projects p ON p.id=m.project_id
                WHERE m.subject_id='household' AND (coalesce(p.slug,'')=@slug OR EXISTS
                    (SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=@slug))
                  AND NOT EXISTS (SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
                  AND ((@slot<>'' AND m.capsule=@kind AND m.capsule_slot=@slot)
                    OR (@slot='' AND (@tag=ANY(m.tags) OR (@tag='agent-activity-log' AND m.content LIKE 'ACTIVITY log of coding agents%'))))
                LIMIT 1
                """, ("slug", slug ?? ""), ("tag", tag), ("slot", slot ?? ""), ("kind", kind)) is not null) return;
            var draft = JsonSerializer.Serialize(new { content, tags = new[] { tag }, scope = "shared", kind = "semantic", project = slug, source = "rhythm-starter" });
            var id = (Guid)(await Query("SELECT (create_memory(@p,@draft::jsonb,'cli','rhythm')->>'id')::uuid",
                ("p", person), ("draft", draft)))!;
            await Query("SELECT set_memory_status(@p,@id,'canonical',NULL,'cli','rhythm')", ("p", person), ("id", id));
            if (slot is not null)
                await Query("SELECT set_memory_capsule(@p,@id,@kind,@slot,NULL,'cli','rhythm')",
                    ("p", person), ("id", id), ("kind", kind), ("slot", slot));
        }

        async Task ProjectCapsule(string slug)
        {
            await Note(slug, "project-guide", "Project context: record the outcome, source of truth and how to verify the work here. The Projects dashboard shows this project's live work.", "current-state");
            await Note(slug, "project-guide", "Decisions: record agreed choices and why they were made here.", "decisions");
            await Note(slug, "project-guide", "Open questions: record choices that need an owner's answer here.", "open-questions");
        }

        async Task Board(string asset)
        {
            var draft = JsonSerializer.Deserialize<DashboardDraft>(Asset(asset + ".json"), DashboardDraft.Json)! with
                { Html = Asset("style.html") + Asset(asset + ".html") };
            if (draft.Problem() is { } problem) throw new ConfigException("Invalid starter dashboard: " + problem);
            if (await Query("""
                SELECT 1 FROM dashboards d JOIN LATERAL
                  (SELECT title FROM dashboard_versions WHERE dashboard_id=d.id ORDER BY version DESC LIMIT 1) v ON true
                WHERE d.subject_id='household' AND lower(v.title)=lower(@title) LIMIT 1
                """, ("title", draft.Title)) is not null) return;
            var version = JsonSerializer.Serialize(new { draft.Title, draft.Description, draft.Html, draft.Note,
                feeds = draft.Feeds.Select(f => f.Normalized()), writes = (draft.Writes ?? []).Select(w => w.Normalized()),
                stars = Array.Empty<DashboardStar>() }, DashboardDraft.Json);
            await Query("SELECT save_dashboard(@p,NULL,NULL,@version::jsonb,ARRAY[]::text[])", ("p", person), ("version", version));
        }

        switch (template)
        {
            case "workspace":
                await Note(null, "workspace-guide", "Use Projects for work and ideas. Agents shows claims, reviews, activity and handoffs. Read the project's capsule before starting; keep the work item current and leave verification there.", "principles");
                await Note("agent-handoffs", "handoff-guide", "Read open handoffs tagged handoff in this project. For verified shared work, append a short session, agent and result to the ACTIVITY log of coding agents. Keep private work in its own scope. Put unfinished ticket work on its own item.", "current-state");
                await Note("agent-handoffs", "agent-activity-log", "ACTIVITY log of coding agents");
                await Note("project-ideas", "inbox-guide", "Save potential projects here. When an idea has an outcome and a way to verify it, create a work item for it.");
                await ProjectCapsule("first-project");
                await Board("projects");
                await Board("agents");
                break;
            case "project":
                await ProjectCapsule(project!);
                break;
            case "groceries":
                await Note("groceries", "grocery-list-guide", "Add what you need and mark items purchased as you shop. This shared list is stored in Rhythm.");
                await Board("groceries");
                break;
            default: throw new ArgumentException("Unknown starter template.");
        }
        await tx.CommitAsync(ct);
        output.WriteLine(template == "workspace" ? "Projects (including Ideas) and Agents are available in Dashboards."
            : template == "groceries" ? "Groceries is available in Dashboards. Select Allow edits to use the shared list."
            : $"The project capsule for {project} is ready.");
    }

    static string Asset(string filename)
    {
        using var stream = typeof(StarterWorkspace).Assembly.GetManifestResourceStream("Knowledge.Starter." + filename)
            ?? throw new ConfigException($"Missing packaged starter template {filename}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
