using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The project registry (docs/dev/memory.md section 5; SB-P3-07): projects per scope, each with a slug that never
// changes, a name, a description, up to 16 aliases and a status; made, renamed, given aliases, archived and deleted with
// their memories kept. Writes go through the checked database functions of migration 035, under the recorded
// memory.scopes mode locked FOR SHARE; reads take the read predicate on the project's scope. A project's members are
// the memories in it and, as upstream, the memories of its scope tagged with one of its aliases (MemoryStore.List).
//
// The MCP tools (SB-P3-09, list_projects) and the app's Memories view (SB-P3-12, its project list) read the registry, only
// with memory.tools on; only the view calls its writers (app_memories project_create, project_update and project_delete,
// SB-P6-01, a person's channel), and no MCP tool does, so every other answer is as it was. Adapted from Second Brain's
// registry (src/projects/registry.ts, MIT).

// What a caller hands the registry, prepared as every write path will: a name trimmed, a description trimmed, aliases as
// tags (lower case, trimmed, once each, none the engine sets itself, the project's own slug left off), and a slug derived
// from the name when the call gives none.
public static partial class MemoryProjectInput
{
    public const int MaxAliases = 16, MaxName = 120, MaxDescription = 1000, MaxAlias = MemoryInput.MaxTagLength;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex SlugPattern();
    public static bool IsSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    // "Canal Walks!" is canal-walks, as upstream's deriveSlug, with Rhythm's slug (no underscore: it becomes a hyphen).
    // Null when nothing valid survives, so the call must give a slug.
    public static string? Slug(string? name)
    {
        var slug = Regex.Replace((name ?? "").Trim().ToLowerInvariant().Replace('_', '-'), @"\s+", "-");
        slug = Regex.Replace(slug, "[^a-z0-9-]", "");
        slug = Regex.Replace(slug, "-{2,}", "-").TrimStart('-');
        slug = (slug.Length > 64 ? slug[..64] : slug).TrimEnd('-');
        return IsSlug(slug) ? slug : null;
    }

    public static string Name(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > MaxName || n.Any(char.IsControl))
            throw new MemoryRefused("invalid", $"A project's name is 1 to {MaxName} characters, with no control characters");
        return n;
    }

    public static string Description(string? description)
    {
        var d = (description ?? "").Trim();
        if (d.Length > MaxDescription || d.Contains('\0')) throw new MemoryRefused("invalid", $"A project's description is at most {MaxDescription} characters");
        return d;
    }

    // Other names the project answers to in its scope, each a plain tag it claims: a memory of its scope tagged with one is
    // a member (as upstream). Never one in a namespace the engine sets itself (MemoryInput.Reserved), as upstream refuses.
    public static IReadOnlyList<string> Aliases(IEnumerable<string>? aliases, string slug)
    {
        var kept = new List<string>();
        foreach (var raw in aliases ?? [])
        {
            var alias = (raw ?? "").Trim().ToLowerInvariant();
            if (alias.Length == 0 || alias == slug || kept.Contains(alias)) continue;
            if (alias.Length > MaxAlias || alias.Any(char.IsControl))
                throw new MemoryRefused("invalid", $"An alias is a tag of at most {MaxAlias} characters, with no control characters");
            if (MemoryInput.Reserved(alias))
                throw new MemoryRefused("invalid", $"The alias {alias} is in a namespace the engine sets itself; an alias claims a plain tag");
            kept.Add(alias);
        }
        if (kept.Count > MaxAliases) throw new MemoryRefused("invalid", $"A project takes at most {MaxAliases} aliases");
        return kept;
    }
}

// A new project. Slug: lower-case letters, digits and hyphens, at most 64, derived from the name when not given, and
// never changed after. Scope: null (the writer's own: private with scopes on, the household's with them off), private or
// shared, or upstream's personal and company, or Family (D-044), as a memory lands.
public sealed record MemoryProjectDraft(string Name, string? Slug = null, string? Description = null, IReadOnlyList<string>? Aliases = null,
    string? Scope = null);

// A change to a project: only what it names. Aliases replace the aliases; Status is active or archived.
public sealed record MemoryProjectChange(string? Name = null, string? Description = null, IReadOnlyList<string>? Aliases = null, string? Status = null);

// A project as the registry holds it. Subject is its scope's owner (a person, or household for Family); Author the
// person who made it, or none (a login with no person, or a memory write's project made by one).
public sealed record MemoryProject(Guid Id, string Subject, string Scope, string Slug, string Name, string Description,
    IReadOnlyList<string> Aliases, string Status, string? Author, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// What a project write answers. Scopes "off": the project is visible to everyone in the household (D-048). Changed: the
// fields a change changed; Unchanged: nothing needed writing.
public sealed record MemoryProjectWrite(MemoryProject Project, string Scopes, IReadOnlyList<string> Changed, bool Unchanged);

// A deleted project's id, slug and scope, and how many memories left it (each keeping the state it had as a version).
public sealed record MemoryProjectDeleted(Guid Id, string Slug, string Subject, int Memories, string Scopes);

// A list's filters, each narrowing what the caller reads: a scope (never widening, memory.md section 2), archived ones
// too, and a name (a slug or an alias), which every scope the caller reads may hold once.
public sealed record MemoryProjectFilter(string? Scope = null, bool IncludeArchived = false, string? Name = null);

public sealed partial class MemoryStore
{
    // A project counts in a scope the caller reads, through the same predicate as the memories in it.
    internal static string ProjectAllowed() =>
        Database.Allowed("p.subject_id", "'memory'", "CASE WHEN p.subject_id='household' THEN 'shared' ELSE 'private' END");
    // A project every list serves (SB-P3-21, migration 046): one the caller reads that is not held, as a project a held
    // write made is until a person releases its memory or a write that is not held names it. Lists, a name a filter or a
    // capsule looks up, and the map take it; a person's change of a project by its id, and the operator's export, take
    // ProjectAllowed.
    internal static string ProjectListed() => $"({ProjectAllowed()} AND NOT p.held)";
    // A project named by its slug or one of its aliases (@name).
    internal const string ProjectNamed = "(p.slug=@name OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=@name))";

    public async Task<MemoryProjectWrite> CreateProject(Guid principal, MemoryProjectDraft draft, MemoryCall call, CancellationToken ct)
    {
        NoKey(call);
        var name = MemoryProjectInput.Name(draft.Name);
        var slug = draft.Slug is null ? MemoryProjectInput.Slug(name)
            ?? throw new MemoryRefused("invalid", "No slug could be made from the name; give one (lower-case letters, digits and hyphens)")
            : draft.Slug.Trim();
        if (!MemoryProjectInput.IsSlug(slug)) throw new MemoryRefused("invalid", "A project's slug is lower-case letters, digits and hyphens, at most 64");
        var project = new JsonObject
        {
            ["slug"] = slug,
            ["name"] = name,
            ["description"] = MemoryProjectInput.Description(draft.Description),
            ["aliases"] = new JsonArray([.. MemoryProjectInput.Aliases(draft.Aliases, slug).Select(a => (JsonNode?)a)]),
            ["scope"] = draft.Scope,
        };
        using var doc = await ProjectWrite("SELECT create_memory_project(@principal,@project::jsonb,@channel,@client)::text", principal, call, ct,
            ("project", NpgsqlDbType.Jsonb, project.ToJsonString()));
        return Written(doc.RootElement);
    }

    // Expected: the revision the caller read, refused (conflict) when the project has moved on; null changes the current.
    public async Task<MemoryProjectWrite> UpdateProject(Guid principal, Guid project, MemoryProjectChange change, int? expected, MemoryCall call,
        CancellationToken ct)
    {
        NoKey(call);
        var body = new JsonObject();
        if (change.Name is not null) body["name"] = MemoryProjectInput.Name(change.Name);
        if (change.Description is not null) body["description"] = MemoryProjectInput.Description(change.Description);
        if (change.Status is not null)
            body["status"] = change.Status is "active" or "archived" ? change.Status : throw new MemoryRefused("invalid", "A project's status is active or archived");
        if (change.Aliases is not null)
        {
            // The project's own slug is left off, so it is read first; an unknown or unreadable project is refused by the
            // database as one, whatever the aliases say.
            var slug = await Slug(principal, project, ct) ?? "";
            body["aliases"] = new JsonArray([.. MemoryProjectInput.Aliases(change.Aliases, slug).Select(a => (JsonNode?)a)]);
        }
        if (body.Count == 0) throw new MemoryRefused("invalid", "A change names name, description, aliases or status");
        using var doc = await ProjectWrite("SELECT update_memory_project(@principal,@id,@change::jsonb,@expected,@channel,@client)::text", principal, call, ct,
            ("id", NpgsqlDbType.Uuid, project), ("change", NpgsqlDbType.Jsonb, body.ToJsonString()), ("expected", NpgsqlDbType.Integer, expected));
        return Written(doc.RootElement);
    }

    // The grouping only: each memory in it leaves it, keeping the state it had as a version; memories tagged with one of
    // its aliases keep their tags (memory.md section 5, as upstream).
    public async Task<MemoryProjectDeleted> DeleteProject(Guid principal, Guid project, int? expected, MemoryCall call, CancellationToken ct)
    {
        NoKey(call);
        using var doc = await ProjectWrite("SELECT delete_memory_project(@principal,@id,@expected,@channel,@client)::text", principal, call, ct,
            ("id", NpgsqlDbType.Uuid, project), ("expected", NpgsqlDbType.Integer, expected));
        var o = doc.RootElement;
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("slug").GetString()!, o.GetProperty("subject").GetString()!,
            o.GetProperty("memories").GetInt32(), o.GetProperty("scopes").GetString()!);
    }

    // The caller's projects, by name (case aside), then slug, then scope, as upstream lists them; archived ones only when
    // asked, held ones never (ProjectListed). A name filter takes a slug or an alias.
    public async Task<IReadOnlyList<MemoryProject>> Projects(Guid principal, MemoryProjectFilter filter, CancellationToken ct)
    {
        var where = new List<string> { ProjectListed() };
        var args = new List<(string, object?)> { ("principal", principal) };
        if (filter.Scope is not null)
        {
            string? scope;
            try { scope = MemorySettings.Named(filter.Scope); }
            catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
            if (scope is not null) { where.Add("CASE WHEN p.subject_id='household' THEN 'shared' ELSE 'private' END=@scope"); args.Add(("scope", scope)); }
        }
        if (!filter.IncludeArchived) where.Add("p.status='active'");
        if (MemoryInput.Project(filter.Name) is { } name) { where.Add(ProjectNamed); args.Add(("name", name)); }
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT p.id,p.subject_id,p.slug,p.name,p.description,
              ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id ORDER BY a.alias),p.status,p.author_id,p.revision,
              p.created_at,p.updated_at
            FROM memory_projects p WHERE {string.Join(" AND ", where)} ORDER BY lower(p.name),p.slug,p.subject_id LIMIT 500
            """, [.. args]);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var projects = new List<MemoryProject>();
        while (await reader.ReadAsync(ct))
            projects.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(1) == RhythmConfig.Household ? "shared" : "private",
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<string[]>(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8), reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        return projects;
    }

    // Up to `limit` active slugs the caller reads, in order, for the answer to a name it does not know (CTX-07); never a
    // held project's.
    public async Task<IReadOnlyList<string>> KnownProjects(Guid principal, int limit, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await KnownProjects(c, principal, limit, ct);
    }
    static async Task<IReadOnlyList<string>> KnownProjects(NpgsqlConnection c, Guid principal, int limit, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, null, $"""
            SELECT DISTINCT p.slug FROM memory_projects p WHERE p.status='active' AND {ProjectListed()} ORDER BY 1 LIMIT {Math.Clamp(limit, 1, 50)}
            """, ("principal", principal));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var known = new List<string>();
        while (await reader.ReadAsync(ct)) known.Add(reader.GetString(0));
        return known;
    }

    // A readable project's slug, or null.
    async Task<string?> Slug(Guid principal, Guid project, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"SELECT p.slug FROM memory_projects p WHERE p.id=@id AND {ProjectAllowed()}",
            ("principal", principal), ("id", project));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // Project writes take no idempotency key: a retried create answers taken, a retried change unchanged.
    static void NoKey(MemoryCall call)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "Project writes take no idempotency key: a repeated create answers taken, a repeated change unchanged");
    }

    static MemoryProjectWrite Written(JsonElement o) => new(Project(o), o.GetProperty("scopes").GetString()!,
        o.TryGetProperty("changed", out var changed) ? [.. changed.EnumerateArray().Select(x => x.GetString()!)] : [],
        o.TryGetProperty("unchanged", out var same) && same.ValueKind == JsonValueKind.True);

    static MemoryProject Project(JsonElement o) => new(o.GetProperty("id").GetGuid(), o.GetProperty("subject").GetString()!,
        o.GetProperty("scope").GetString()!, o.GetProperty("slug").GetString()!, o.GetProperty("name").GetString()!,
        o.GetProperty("description").GetString()!, [.. o.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!)],
        o.GetProperty("status").GetString()!, o.GetProperty("author").ValueKind == JsonValueKind.String ? o.GetProperty("author").GetString() : null,
        o.GetProperty("revision").GetInt32(), o.GetProperty("created_at").GetDateTimeOffset(), o.GetProperty("updated_at").GetDateTimeOffset());

    async Task<JsonDocument> ProjectWrite(string sql, Guid principal, MemoryCall call, CancellationToken ct,
        params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        (string Name, NpgsqlDbType Type, object? Value)[] all =
            [("principal", NpgsqlDbType.Uuid, principal), .. args, ("channel", NpgsqlDbType.Text, call.Channel),
             ("client", NpgsqlDbType.Text, AdminAudit.Client(call.Client))];
        foreach (var (name, type, value) in all)
            cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        try { return JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!); }
        catch (PostgresException e) when (e.SqlState == "P0002") { throw new MemoryRefused("unknown", "Unknown project"); }
        catch (PostgresException e) when (e.SqlState == "23505" && e.ConstraintName == "memory_project_names") { throw new MemoryRefused("taken", e.MessageText); }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
    }
}
