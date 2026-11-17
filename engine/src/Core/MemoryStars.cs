using System.Text.Json;
using Knowledge.Adapters.SecondBrain;

namespace Knowledge.Relationships;

// Native memory stars on the map (SB-P3-11; docs/dev/memory.md sections 8 and 10): every pass reads the native memories
// from the memory tables (CatalogReader.Inventory, in the catalog's own read) and lists each as a star, with a star for
// each project that has one on the map, before any adapter lists: Rhythm is their system of record, so they take
// graph.maxMemories first (Q-141). A held memory (D-039), a deprecated one (D-030), one no longer true (valid_until
// passed) and one in the trash are never stars. A star holds what a Second Brain memory's does (memory.md 8): an excerpt
// title, groups, links and a vector, never the memory's words, which are read from the memory tables when it opens
// (Text) through the reader's own predicate. Each read serves a native star only while its record says so at that read
// (Served, which RelationshipEngine.Served takes in both modes): a memory's while the reader reads the memory's own row
// and it is not held, so a share, an un-share, a hold or the trash reaches every reader at once, with no pass between
// (D-063). Combined retrieval's hops take a memory's star row for its own row (Answering, D-152). With memory.tools on
// (RelationshipCatalog.Replaced; SB-P4-11), a memory no longer true because a newer one of its scope replaced it stays a
// star, listed after every current one and served with what replaced it (ReplacedBy, a node's truth), so the app draws a
// replaced fact faded, never hidden; recall and capsules still leave it out (Recall.Current, Capsules).

// A native memory as the pass reads it: its id, subject and scope, the start of its text (for the title, its Drive
// links, its vector and what a titles model is sent, D-255; never stored), tags, status, project, revisions and times,
// and its kind (episodic or semantic, null while it has none), which its star carries for the episodic rule
// (LinkTypes.Allows). Replaced: a newer memory of its scope replaced it (read only with RelationshipCatalog.Replaced,
// SB-P4-11), so it is listed after every current one.
public sealed record CatalogMemory(Guid Id, string Subject, string Scope, string Text, string[] Tags, string Status, Guid? ProjectId,
    int Revision, int TextRevision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Kind = null, bool Replaced = false);
// A native project of the subjects the pass reads: a star only while a listed memory is in it.
public sealed record CatalogProject(Guid Id, string Subject, string Slug, string Name, string Description, string Status, string[] Aliases,
    int Revision);

public static class MemoryStars
{
    // The native memories' provider (025, ProviderPolicies.Reserved); a memory's star is under its account `memory`, its
    // source id the memory's id, so its graph id comes from the memory id alone (memory.md 10.3); a project's under
    // `project`.
    public const string Provider = "memory", Account = "memory", ProjectAccount = "project";
    const string MembershipLocator = "memory:project-membership";
    const int MaxReferences = 100;

    public static ResourceKey Key(string subject, Guid memory) => new(subject, Provider, Account, memory.ToString());
    public static ResourceKey ProjectKey(string subject, Guid project) => new(subject, Provider, ProjectAccount, project.ToString());
    public static bool IsNative(ResourceKey key) => key.Provider == Provider;

    // A native star row's external id is its key's CatalogId (RelationshipEngine.Save): this prefix, then the memory's
    // or project's id and the closing `"]`. The SQL below reads the id from it, never from the snapshot's JSON.
    static string Prefix(string account) => new ResourceKey("", Provider, account, "").CatalogId[..^2];
    const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\"\\]$";
    // SQL: the id a native star row `alias` stands for under `account` (a memory's or a project's), else NULL.
    static string IdOf(string alias, string account) => $"""
        CASE WHEN starts_with({alias}.external_id,'{Prefix(account)}') AND substr({alias}.external_id,{Prefix(account).Length + 1}) ~ '{UuidPattern}'
          THEN substr({alias}.external_id,{Prefix(account).Length + 1},36)::uuid END
        """;
    // SQL: the row that answers for `alias` in combined retrieval (D-152): for a native memory's star row the memory's own
    // row, whose predicate and hold decide it; none for a native project's star; the row itself for any other row.
    public static string Answering(string alias) =>
        $"CASE WHEN {alias}.provider<>'{Provider}' OR {alias}.relationship_snapshot IS NULL THEN {alias}.id ELSE {IdOf(alias, Account)} END";
    // SQL: the external id of the star row of the native memory whose own row id is `id` (an SQL uuid expression).
    public static string StarExternalId(string id) => $"'{Prefix(Account)}'||{id}::text||'\"]'";

    // SQL: whether @principal is served the native star row `alias` now, judged at each read whatever the pass stamped
    // on it (memory.md 8, D-063): a memory's star while the reader reads the memory's own row (Database.Visible: in the
    // trash it is marked deleted) and the memory is not held (D-039), deprecated (D-030) or no longer true (valid_until
    // passed), so each reaches every reader at once, as a hold does; a project's while the reader reads the project and it
    // is not held (MemoryStore.ProjectListed, SB-P3-21). Every read of the map takes it, in both modes. With `replaced`
    // (RelationshipCatalog.Replaced, memory.tools on; SB-P4-11) a memory past its end is still served while a live memory
    // of its scope replaced it (migration 049's memory_replaced_by), so the app draws it faded rather than hiding it; an
    // ended one (replaced by nothing live) is not. Without it the SQL is exactly as before.
    public static string Served(string alias, bool replaced = false) => $"""
        CASE WHEN starts_with({alias}.external_id,'{Prefix(Account)}') THEN EXISTS(SELECT 1 FROM resources d
            WHERE d.id={IdOf(alias, Account)} AND d.provider='{Provider}' AND d.relationship_snapshot IS NULL AND d.external_id=d.id::text
              AND {Database.Visible("d")} AND NOT EXISTS(SELECT 1 FROM memories m WHERE m.id=d.id
                AND (m.hold IS NOT NULL OR m.status='deprecated' OR {(replaced ? "(m.valid_until<=now() AND memory_replaced_by(m.id) IS NULL)" : "m.valid_until<=now()")})))
          WHEN starts_with({alias}.external_id,'{Prefix(ProjectAccount)}') THEN EXISTS(SELECT 1 FROM memory_projects p
            WHERE p.id={IdOf(alias, ProjectAccount)} AND {MemoryStore.ProjectListed()})
          ELSE false END
        """;

    // SQL: for the native memory star row `alias`, the memory that replaced its memory now (migration 049's state replaced:
    // past its end, not deprecated, and memory_replaced_by names a live one of its scope), else NULL; judged at each read,
    // as Served is, so a supersede or a retraction reaches the map with no pass between (SB-P4-11).
    public static string ReplacedBy(string alias) => $"""
        CASE WHEN starts_with({alias}.external_id,'{Prefix(Account)}') THEN (SELECT memory_replaced_by(m.id) FROM memories m
            WHERE m.id={IdOf(alias, Account)} AND m.valid_until<=now() AND m.status<>'deprecated') END
        """;

    // SQL: for the native memory star row `alias`, its memory's work properties now (migration 079's memory_work_json), else
    // NULL; judged at each read, as ReplacedBy is, so a work item made or moved reaches the map with no pass between.
    public static string Work(string alias) => $"""
        CASE WHEN starts_with({alias}.external_id,'{Prefix(Account)}') THEN (SELECT memory_work_json(w.work_type,w.state,w.priority)::text
            FROM memory_work w WHERE w.memory_id={IdOf(alias, Account)}) END
        """;

    // A native memory's text when its star is opened: read from the memory tables at that moment through the reader's
    // own predicate on the memory's own row, never held, returned once and never stored (memory.md 8). Null when the
    // reader does not read it now. With `replaced` (as Served) a replaced memory's text opens too.
    public static async Task<string?> Text(Database db, Guid principal, Guid memory, CancellationToken ct, bool replaced = false)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT m.content FROM memories m JOIN resources r ON r.id=m.id AND r.provider='{Provider}' AND r.relationship_snapshot IS NULL
              AND r.external_id=m.id::text
            WHERE m.id=@id AND m.hold IS NULL AND m.status<>'deprecated' AND (m.valid_until IS NULL OR m.valid_until>now(){(replaced ? " OR memory_replaced_by(m.id) IS NOT NULL" : "")})
              AND {Database.Visible()}
            """, ("id", memory), ("principal", principal));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // This pass's native stars, newest memory first: each memory with the projects it is in (its own, and those of its
    // scope whose aliases it is tagged with, as MemoryStore.List counts members), within `budget` records in all, or, with
    // scopes on, within each subject's own share of it (`shares`; a subject with none lists none). A memory whose new
    // projects would pass its budget ends that subject's listing. Complete: whether a subject's listing is (the read held
    // every memory of it and all were listed; with scopes off, all subjects' together), so a star of an earlier pass of
    // that subject that is not here may be reported missing; one subject past its share never stops another's. Only
    // current memories are listed here and decide Complete. A memory a newer one of its scope replaced (SB-P4-11, read only
    // with memory.tools on) waits for Replaced, which the pass calls once the adapters have listed theirs, with what they
    // left of the budget (or of each subject's share), so a replaced fact never takes a current memory's place, a native's
    // or an adapter's; its projects already starred count nothing again, and one that does not fit never makes a listing
    // incomplete. Pending: whether there is any replaced memory to list. `titleText`, while a titles model is on, is given
    // each listed star with the start of its memory's text, for the titles model (D-255), never stored: every memory that
    // is a star, a draft and a replaced fact included, as the star itself; one held (D-039), deprecated (D-030), past its
    // end or in the trash is no star, which the read already ensures, so nothing of it is given.
    public static (List<DiscoveryRecord> Records, Func<string, bool> Complete, bool Pending,
        Func<int, IReadOnlyDictionary<string, int>?, List<DiscoveryRecord>> Replaced) Records(CatalogInventory inventory, int budget,
        IReadOnlyDictionary<string, int>? shares, DateTimeOffset now, Func<ResourceKey, string, string, string, float[]?> vector,
        Action<DiscoveryRecord, string>? titleText = null)
    {
        var memories = inventory.Memories ?? [];
        var projects = (inventory.Projects ?? []).ToDictionary(p => p.Id);
        var aliases = (inventory.Projects ?? []).SelectMany(p => p.Aliases.Select(a => (p.Subject, Alias: a, Project: p)))
            .GroupBy(x => (x.Subject, x.Alias)).ToDictionary(g => g.Key, g => g.Select(x => x.Project).ToArray());
        var records = new List<DiscoveryRecord>();
        var listed = new Dictionary<string, (int Count, HashSet<Guid> Projects)>(StringComparer.Ordinal);
        var full = new HashSet<string>(StringComparer.Ordinal);
        // Newest first within `allowed` records for its key, into `into`; a memory that does not fit ends its key's listing.
        void Take(CatalogMemory memory, int allowed, string key, Dictionary<string, (int Count, HashSet<Guid> Projects)> counts,
            HashSet<string> over, List<DiscoveryRecord> into)
        {
            if (!counts.TryGetValue(key, out var used)) counts[key] = used = (0, []);
            var mine = new List<CatalogProject>();
            if (memory.ProjectId is { } id && projects.TryGetValue(id, out var own) && own.Subject == memory.Subject) mine.Add(own);
            foreach (var tag in memory.Tags)
                foreach (var tagged in aliases.GetValueOrDefault((memory.Subject, tag)) ?? [])
                    if (!mine.Contains(tagged)) mine.Add(tagged);
            mine = [.. mine.OrderBy(p => p.Id)];
            var added = mine.Count(p => !used.Projects.Contains(p.Id));
            if (over.Contains(key) || used.Count + 1 + added > allowed) { over.Add(key); return; }
            foreach (var project in mine.Where(p => used.Projects.Add(p.Id)))
                into.Add(Project(project, now));
            counts[key] = (used.Count + 1 + added, used.Projects);
            var star = Memory(memory, mine, now, vector);
            titleText?.Invoke(star, memory.Text);
            into.Add(star);
        }
        static IEnumerable<CatalogMemory> Newest(IEnumerable<CatalogMemory> m) => m.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);
        foreach (var memory in Newest(memories.Where(m => !m.Replaced)))
            Take(memory, shares is null ? budget : shares.GetValueOrDefault(memory.Subject), shares is null ? "" : memory.Subject, listed, full, records);
        var cut = inventory.CappedSubjects?.ToHashSet(StringComparer.Ordinal);
        var waiting = memories.Where(m => m.Replaced).ToArray();
        List<DiscoveryRecord> Replaced(int left, IReadOnlyDictionary<string, int>? lefts)
        {
            var more = new List<DiscoveryRecord>();
            var counts = listed.ToDictionary(p => p.Key, p => (0, new HashSet<Guid>(p.Value.Projects)), StringComparer.Ordinal);
            var over = new HashSet<string>(StringComparer.Ordinal);
            foreach (var memory in Newest(waiting))
                Take(memory, lefts is null ? left : lefts.GetValueOrDefault(memory.Subject), lefts is null ? "" : memory.Subject, counts, over, more);
            return more;
        }
        return (records, shares is null
            ? _ => !inventory.MemoriesCapped && full.Count == 0
            : subject => !(cut?.Contains(subject) ?? inventory.MemoriesCapped) && !full.Contains(subject), waiting.Length > 0, Replaced);
    }

    static DiscoveryRecord Memory(CatalogMemory m, IReadOnlyList<CatalogProject> projects, DateTimeOffset now,
        Func<ResourceKey, string, string, string, float[]?> vector)
    {
        var key = Key(m.Subject, m.Id);
        var title = SecondBrainAdapter.Title(m.Text);
        var groups = new List<RecordGroup> { Scope(m.Subject) };
        groups.AddRange(projects.Take(19).Select(p => new RecordGroup(ProjectGroup(p.Id), Label(p.Name))));
        // Membership is Rhythm's own structure: a verified, directed link to each project's star; then the Drive file and
        // folder addresses in its text, exactly, never the prose around them (as a Second Brain memory's).
        Reference[] inProject = [.. projects.Select(p => new Reference(ProjectKey(p.Subject, p.Id), null, MembershipLocator, "In project",
            "in_project", true, "verified", 1, true))];
        var references = RelationshipDiscovery.ExtractDriveLinks(m.Text, Math.Max(0, MaxReferences - inProject.Length));
        // What the star shows (its title, status and projects) and the memory's revision, which every change moves (its
        // kind's included); only records of its own scope, so a Family star's revision never names a private record
        // (memory.md 8).
        var revision = "memory-sha256:" + Hashing.Hash(JsonSerializer.Serialize(new { id = m.Id, m.Revision, title, m.Status,
            projects = projects.Select(p => new { p.Id, p.Name }) }));
        var content = $"memory-text:{m.TextRevision}";
        return new(key, m.Scope, "memory", title, null, revision, m.UpdatedAt <= now ? m.UpdatedAt : null, now, [.. inProject, .. references],
            CreatedAt: m.CreatedAt <= now ? m.CreatedAt : null, ContentRevision: content, Embedding: vector(key, content, title, m.Text),
            Groups: groups, Overview: title, MemoryKind: m.Kind is LinkTypes.Episodic or LinkTypes.Semantic ? m.Kind : null);
    }

    static DiscoveryRecord Project(CatalogProject p, DateTimeOffset now)
    {
        var group = new RecordGroup(ProjectGroup(p.Id), Label(p.Name));
        var overview = (p.Status == "archived" ? "Archived project" : "Project") + (p.Description.Length > 0 ? ": " + p.Description : "");
        return new(ProjectKey(p.Subject, p.Id), p.Subject == RelationshipDiscovery.Household ? "shared" : "private", "project",
            p.Name.Length <= 1000 ? p.Name : p.Name[..999] + "…", null,
            "memory-sha256:" + Hashing.Hash(JsonSerializer.Serialize(new { id = p.Id, p.Revision, p.Name, p.Status, p.Description })), null, now, [],
            Groups: [group], Overview: overview.Length <= 1000 ? overview : overview[..999] + "…");
    }

    static RecordGroup Scope(string subject) =>
        subject == RelationshipDiscovery.Household ? new("memory:family", "Family") : new("memory:private", "Private");
    static string ProjectGroup(Guid project) => $"memory:project:{project}";
    static string Label(string name) => name.Length <= 200 ? name : name[..200];
}
