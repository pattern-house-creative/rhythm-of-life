using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge.Relationships;

// Stars for the knowledge catalog's own records (spec section 16), discovered rather than listed: every
// measurement a configured subject has joins a data star, named by the first configured group whose provider
// and metric pattern match it, else its provider's catch-all star; every text record that is neither a
// measurement source nor a page of another record becomes a document star. Group names and provider labels
// are configuration, so a new source or measurement appears on the next refresh with no release.
// `Metrics` entries are exact metric names or prefixes ending in `*`. A group's `Topics` are the words that
// tie its star to the person's records (a memory titled "Morning glucose check" is about Glucose); without
// them the group label's own words are used.
public sealed partial record RelationshipCatalog(string[] Subjects, RelationshipDataGroup[]? Groups = null,
    Dictionary<string, string>? Providers = null)
{
    public static bool Valid(RelationshipCatalog? c) => c is not null
        && c.Subjects is { Length: >= 1 and <= 30 } && c.Subjects.All(s => s is not null && Name().IsMatch(s))
        && c.Subjects.Distinct().Count() == c.Subjects.Length
        && (c.Groups is null || c.Groups.Length <= 100 && c.Groups.All(g => g is not null && Name().IsMatch(g.Id) && g.Id != "other"
            && (g.Provider == "*" || Name().IsMatch(g.Provider)) && !string.IsNullOrWhiteSpace(g.Label) && g.Label.Length <= 100
            && g.Metrics is { Length: >= 1 and <= 100 } && g.Metrics.All(m => m is not null && Metric().IsMatch(m))
            && (g.Topics is null || g.Topics.Length <= 20 && g.Topics.All(t => t is not null && Topic().IsMatch(t))))
            && c.Groups.Select(g => (g.Provider, g.Id)).Distinct().Count() == c.Groups.Length)
        && (c.Providers is null || c.Providers.Count <= 50 && c.Providers.All(p => Name().IsMatch(p.Key)
            && !string.IsNullOrWhiteSpace(p.Value) && p.Value.Length <= 60));

    // With memory.scopes on (the database's recorded mode, read at each rebuild), every budget of a pass is shared out
    // among Subjects in equal, fixed shares, each subject's records counted against its own share (docs/dev/memory.md
    // section 8, Budgets): graph.maxMemories and graph.maxDocuments, the pass's 5000 records, the catalog's documents and
    // measurements, and the similarity links. A share depends only on the settings, never on how much any scope holds,
    // so what one reader sees never depends on another scope's volume, and a subject not in Subjects (a former person)
    // has none. Off, the default, each budget is the install's, as before.
    public bool Scoped { get; init; }
    // With Scoped, the source shares the pass read at its start: (person, provider) pairs of current people, whose rows
    // Family reads (migration 023's readable_subjects). The pass's link shares count such a record as the household's.
    public IReadOnlySet<(string Person, string Provider)>? Shares { get; init; }
    // How many native memories the inventory reads (graph.maxMemories, which they take first, Q-141), shared out among
    // Subjects with Scoped as every budget is; 0 reads none. Set by the pass (CatalogRelationshipSource.Fetch).
    public int MemoryBudget { get; init; }
    // With memory.tools on (MemorySettings.ToolsOn), the native memories that a newer memory of their scope replaced
    // (migration 049's replaced state) are listed too, after every current one, and served as stars the app draws faded
    // rather than hides (SB-P4-11; RelationshipEngine.Replaced, MemoryStars.Served). Off, the default, only memories still
    // true are, as before.
    public bool Replaced { get; init; }
    // With memory.tools on, the map's reads serve the links a person or an AI client made (made_links, SB-P4-02;
    // RelationshipEngine.MadeLinks) beside its own. Off, the default, they are not read, as before run 15.
    public bool MadeLinks { get; init; }
    // With graph.pluginLinks on (SB-P4-13), a plugin's own links between its records (ingest's, evidence ingest:<plugin>,
    // which nothing but get_related_resources served) join the two records' document stars as that plugin's suggestions
    // (CatalogReader.PluginLinks, CatalogStars.Records). Off, the default, the pass reads none, so the map is as before.
    public bool PluginLinks { get; init; }
    // `subject`'s share of `budget`: an equal part, the remainder going one each to the first subjects in order.
    public int Share(int budget, string subject)
    {
        var at = Array.IndexOf(Subjects, subject);
        return at < 0 || budget <= 0 ? 0 : budget / Subjects.Length + (at < budget % Subjects.Length ? 1 : 0);
    }

    public string ProviderLabel(string provider) => Providers?.GetValueOrDefault(provider)
        ?? string.Join(' ', provider.Split('-', '_').Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    // The first configured group naming this metric, else the provider's catch-all; "Other" only when the
    // provider has named groups, so a source nobody has named yet reads as just its data.
    public (string Id, string Label) Group(string provider, string metric)
    {
        var groups = (Groups ?? []).Where(g => g.Provider == provider || g.Provider == "*").OrderBy(g => g.Provider == "*").ToArray();
        foreach (var group in groups)
            if (group.Metrics.Any(m => m.EndsWith('*') ? metric.StartsWith(m[..^1], StringComparison.Ordinal) : metric == m))
                return (group.Id, group.Label);
        return ("other", groups.Length > 0 ? $"Other {ProviderLabel(provider)} data" : $"{ProviderLabel(provider)} data");
    }
    // Configured topics, else the label's distinctive words ("Sleep and recovery" is about sleep and recovery).
    public string[] Topics(string provider, string groupId) =>
        (Groups ?? []).FirstOrDefault(g => (g.Provider == provider || g.Provider == "*") && g.Id == groupId) is { } group
            ? group.Topics ?? group.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && !Generic.Contains(w)).ToArray()
            : [];
    static readonly HashSet<string> Generic = new(["data", "other", "daily", "with", "from", "your"], StringComparer.OrdinalIgnoreCase);
    // With one person, a star is just its label; with several, each star carries whose it is.
    public string Title(string subject, string label)
    {
        var people = Subjects.Count(s => s is not (RhythmConfig.Household or RhythmConfig.Synthetic));
        if (subject == Subjects[0] && people <= 1) return label;
        var name = RhythmConfig.Loaded ? RhythmConfig.Current.DisplayName(subject) : subject;
        if (subject == RhythmConfig.Household) name = "Household";
        if (subject == RhythmConfig.Synthetic) name = "Demo";
        return $"{char.ToUpperInvariant(name[0]) + name[1..]} · {label}";
    }

    // The install's catalog: every person, the household and (with demo data on) the demo subject, with groups
    // that name the common measurements of any provider. A provider-specific group wins over these; graph.catalog in
    // rhythm.yaml adds groups and provider labels (one with a built-in group's id and provider replaces it).
    public static RelationshipCatalog For(RhythmConfig config)
    {
        var configured = config.Graph.Catalog.Groups.Select(g => g.Group).ToArray();
        var labels = new Dictionary<string, string>(DefaultLabels, StringComparer.Ordinal);
        foreach (var (provider, label) in config.Graph.Catalog.Providers) labels[provider] = label;
        return new([.. config.Subjects, .. config.Demo.Enabled ? new[] { RhythmConfig.Synthetic } : []],
            [.. configured, .. DefaultGroups.Where(d => !configured.Any(g => g.Id == d.Id && g.Provider == d.Provider))], labels)
            { Replaced = MemorySettings.ToolsOn(config), MadeLinks = MemorySettings.ToolsOn(config), PluginLinks = config.Graph.PluginLinks is "on" };
    }
    // Provider ids that do not read well capitalized; any other reads as its id ("garmin" is "Garmin").
    public static Dictionary<string, string> DefaultLabels { get; } = new(StringComparer.Ordinal) { ["csv"] = "CSV import" };
    public static RelationshipDataGroup[] DefaultGroups { get; } =
    [
        new("glucose", "Glucose", "*", ["glucose", "sensor_glucose", "blood_glucose"], ["glucose", "blood sugar", "CGM"]),
        new("insulin", "Insulin and carbs", "*", ["insulin", "carbs", "basal_rate", "bolus_delivered", "carbohydrate_input", "auto_insulin_aggregate"], ["insulin", "carbs", "bolus"]),
        new("heart", "Heart", "*", ["heart_rate", "resting_heart_rate", "hrv", "hrv_*"], ["heart", "HRV", "pulse"]),
        new("sleep", "Sleep", "*", ["sleep_minutes", "sleep_*"], ["sleep", "bedtime", "nap"]),
        new("activity", "Activity", "*", ["steps", "intensity_minutes", "intensity_*", "activity_*", "distance"], ["walk", "run", "steps", "workout", "exercise"]),
        new("body", "Body", "*", ["weight", "body_battery", "stress"], ["weight", "stress", "energy"]),
        new("mood", "Mood", "*", ["mood", "mood_*"], ["mood", "feeling"]),
    ];

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")] private static partial Regex Name();
    [GeneratedRegex(@"^[a-z0-9_]{1,100}\*?$")] private static partial Regex Metric();
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} .'+-]{0,39}$")] private static partial Regex Topic();
}
public sealed record RelationshipDataGroup(string Id, string Label, string Provider, string[] Metrics, string[]? Topics = null);

public sealed record CatalogMetric(string Subject, string Provider, string Scope, string Metric, string Unit,
    long Count, DateOnly First, DateOnly Last, DateTimeOffset? Latest);
// Text is the record's readable body (its pages in order when it has them), read only to derive a similarity
// vector during the pass and never stored. Reader, with scopes on only, is the narrower subject whose share a
// save_document copy took, when it is not the copy's own (CatalogReader.Inventory): its star's DiscoveryRecord.Reader.
public sealed record CatalogDocument(string Subject, string Provider, string Scope, string ExternalId, string Title,
    string? Revision, DateTimeOffset? Modified, string? SourceFileId, int Parts, string Text, string? Reader = null)
{
    // With graph.pluginLinks on (RelationshipCatalog.PluginLinks, SB-P4-13), the plugin's own links from this record to
    // other documents of the pass (CatalogReader.PluginLinks); null otherwise.
    public IReadOnlyList<CatalogLink>? Links { get; init; }
}
// A plugin's own link from a document to another of its records (ingest's records' links, migration 001's relationships
// with no engine): the target's external id, the plugin's link type and verification, and its evidence (ingest:<plugin>).
public sealed record CatalogLink(string Target, string Type, string Verification, string Locator);
// Capped: there were more documents than the star budget, and only the most recently changed are listed. With scopes on
// (RelationshipCatalog.Scoped) both are per subject, and MeasuredLeftOut counts the subjects' measuring providers whose
// series were past their subject's share of the data stars, which are left out rather than failing the catalog for everyone.
// Memories: the native memories that may be stars, newest first, within RelationshipCatalog.MemoryBudget (MemoriesCapped:
// there were more; with scopes on, CappedSubjects names the subjects past their own share), and the Projects of their
// subjects (MemoryStars). Failure: the catalog's own records could not be read for the pass (its metrics past the star
// budget), so it makes no stars of its own, as before; the native memories are still read from their own tables, so a
// catalog that outgrows its budget never hides them.
public sealed record CatalogInventory(IReadOnlyList<CatalogMetric> Metrics, IReadOnlyList<CatalogDocument> Documents, bool Capped = false,
    int MeasuredLeftOut = 0, IReadOnlyList<CatalogMemory>? Memories = null, IReadOnlyList<CatalogProject>? Projects = null,
    bool MemoriesCapped = false, IReadOnlyCollection<string>? CappedSubjects = null, string? Failure = null);

public sealed record GraphReading(double value, DateTimeOffset? observedAt, string date);
public sealed record GraphDay(string date, double min, double mean, double max, long count);
public sealed record GraphSeries(string metric, string label, string unit, GraphReading? latest, IReadOnlyList<GraphDay> days);

// Read-only catalog queries, each for one reader through the one read predicate (Database.Visible, docs/dev/memory.md
// section 7.4): the map's rebuild reads as the engine's own principal, which reads every subject, and app_graph's
// content and data as their caller, after the star check. A record's pages take the predicate too. Rows the engine
// itself wrote (a relationship snapshot) are never inputs.
public static partial class CatalogReader
{
    public const int MaxDocuments = 500;
    const int EmbeddingText = 8000, ReadLimit = 200_000, MaxSeries = 40, Days = 90, MaxMetrics = 2000, MemoryText = EmbeddingText;
    static readonly string Own = $"{Database.Visible()} AND r.relationship_snapshot IS NULL";
    static readonly string Page = $"p.status='current' AND {Database.Allowed("p.subject_id", "p.provider", "p.scope")}";

    public static async Task<CatalogInventory> Inventory(Database db, Guid principal, RelationshipCatalog catalog, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
        // With scopes on each subject is read against its own share of both budgets, in the catalog's order of subjects.
        IReadOnlyList<(string[] Subjects, int Metrics, int Documents)> reads = catalog.Scoped
            ? [.. catalog.Subjects.Select(s => (new[] { s }, catalog.Share(MaxMetrics, s), catalog.Share(MaxDocuments, s)))]
            : [(catalog.Subjects, MaxMetrics, MaxDocuments)];
        var metrics = new List<CatalogMetric>();
        var leftOut = 0;
        (string Subject, string Provider)[] measured;
        if (!catalog.Scoped)
        {
            if (await Metrics(c, tx, principal, catalog.Subjects, null, MaxMetrics, ct) is { } all) metrics.AddRange(all);
            else
            {
                // Past the star budget the catalog's own stars fail for the pass, as they always have (the host notes it
                // unavailable, by the exception it used to throw); its native memories are still listed.
                var (alone, theirs, cut, cutFor) = await Native(c, tx, principal, catalog, reads, ct);
                await tx.CommitAsync(ct);
                return new([], [], Memories: alone, Projects: theirs, MemoriesCapped: cut, CappedSubjects: cutFor,
                    Failure: nameof(InvalidOperationException));
            }
            measured = metrics.Select(m => (m.Subject, m.Provider)).Distinct().ToArray();
        }
        else
        {
            // Each of a subject's measuring providers is read against the subject's share on its own, so one provider's
            // series (a person's private ones) never push another's data stars off, such as a provider the person shares
            // with Family. A provider past the share is left out and noted. Its text records are still no documents.
            var pairs = new List<(string, string)>();
            foreach (var read in reads)
                foreach (var provider in await Measuring(c, tx, principal, read.Subjects, ct))
                {
                    pairs.Add((read.Subjects[0], provider));
                    if (await Metrics(c, tx, principal, read.Subjects, null, read.Metrics, ct, provider) is { } some) metrics.AddRange(some);
                    else leftOut++;
                }
            measured = [.. pairs];
        }
        var documents = new List<CatalogDocument>();
        var ids = new List<Guid>();
        var capped = false;
        foreach (var read in reads)
        {
            var rows = await DocumentRows(c, tx, principal, read.Subjects, read.Documents, measured, ct, catalog.Scoped);
            foreach (var (id, document) in rows.Take(read.Documents))
            {
                documents.Add(document.Parts == 0 ? document : document with { Text = await Pages(c, tx, principal, id, EmbeddingText, ct) });
                ids.Add(id);
            }
            capped |= rows.Count > read.Documents;
        }
        if (catalog.PluginLinks && ids.Count > 1) await PluginLinks(c, tx, ids, documents, ct);
        var (memories, projects, memoriesCapped, cappedSubjects) = await Native(c, tx, principal, catalog, reads, ct);
        await tx.CommitAsync(ct);
        return new(metrics, documents, capped, leftOut, memories, projects, memoriesCapped, cappedSubjects);
    }

    // Native memories, in the same snapshot of the database: each subject's newest within its share of the memory budget
    // (all subjects' together with scopes off), and the projects of the subjects they are in; whether any read was past
    // its budget, and with scopes on which subjects' were.
    static async Task<(List<CatalogMemory>, List<CatalogProject>, bool, string[])> Native(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid principal, RelationshipCatalog catalog, IReadOnlyList<(string[] Subjects, int Metrics, int Documents)> reads, CancellationToken ct)
    {
        var memories = new List<CatalogMemory>();
        var cut = new List<string>();
        var memoriesCapped = false;
        if (catalog.MemoryBudget > 0)
            foreach (var read in reads)
            {
                var budget = catalog.Scoped ? catalog.Share(catalog.MemoryBudget, read.Subjects[0]) : catalog.MemoryBudget;
                if (budget <= 0) continue;
                var rows = await NativeMemories(c, tx, principal, read.Subjects, budget + 1, catalog.Replaced, ct);
                // Capped by current memories only: replaced ones (after them in the read) never make a listing incomplete.
                if (rows.Count(r => !r.Replaced) > budget) { memoriesCapped = true; if (catalog.Scoped) cut.Add(read.Subjects[0]); }
                memories.AddRange(rows.Take(budget));
            }
        var projects = memories.Count == 0 ? [] : await NativeProjects(c, tx, principal, [.. memories.Select(m => m.Subject).Distinct()], ct);
        return (memories, projects, memoriesCapped, [.. cut]);
    }

    // The native memories of `subjects` that may be stars, newest first, at most `limit`: their own row current for the
    // engine's principal (Database.Visible: a memory in the trash has its row marked deleted), not held (D-039), canonical
    // or a draft (a deprecated one has left, D-030), still true, and not in the trash. The start of the text only, and the
    // memory's kind, for the episodic rule (LinkTypes.Allows). With `replaced` (RelationshipCatalog.Replaced, memory.tools
    // on) also those a live memory of their scope replaced (memory_replaced_by), each marked, after every current one, so
    // they never take a current memory's place in the budget (SB-P4-11); without it the query is exactly as before.
    static async Task<List<CatalogMemory>> NativeMemories(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string[] subjects,
        int limit, bool replaced, CancellationToken ct)
    {
        var rows = new List<CatalogMemory>();
        const string Ended = "m.valid_until IS NOT NULL AND m.valid_until<=now()";
        await using var cmd = Database.Command(c, tx, $"""
            SELECT m.id,m.subject_id,m.scope,left(m.content,{MemoryText}),m.tags,m.status,m.project_id,m.revision,m.text_revision,m.created_at,m.updated_at,
              m.kind{(replaced ? $",{Ended}" : "")}
            FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            WHERE {Database.Visible()} AND m.subject_id=ANY(@subjects) AND m.hold IS NULL AND m.status IN ('canonical','draft')
              AND (m.valid_until IS NULL OR m.valid_until>now(){(replaced ? " OR memory_replaced_by(m.id) IS NOT NULL" : "")}) AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
            ORDER BY {(replaced ? $"{Ended}," : "")}m.created_at DESC,m.id DESC LIMIT {limit}
            """, ("subjects", subjects), ("principal", principal));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<string[]>(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetInt32(7), reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                replaced && reader.GetBoolean(12)));
        return rows;
    }

    // The native projects of `subjects`, with their aliases, through the same predicate as their memories; never a held
    // one (SB-P3-21).
    static async Task<List<CatalogProject>> NativeProjects(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string[] subjects, CancellationToken ct)
    {
        var rows = new List<CatalogProject>();
        await using var cmd = Database.Command(c, tx, $"""
            SELECT p.id,p.subject_id,p.slug,p.name,p.description,p.status,
              ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id ORDER BY a.alias),p.revision
            FROM memory_projects p WHERE p.subject_id=ANY(@subjects) AND {MemoryStore.ProjectListed()} ORDER BY p.id
            """, ("subjects", subjects), ("principal", principal));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<string[]>(6), reader.GetInt32(7)));
        return rows;
    }

    // With scopes on, the subject a document is budgeted under, as `b.subject`: the one whose readers alone it can be served
    // to (docs/dev/memory.md section 8, Budgets). A record that is not a save_document copy is its own subject's. A copy is
    // served only to readers of its own subject and of the original it copies, as copy_readable (migration 023) finds that
    // original: any record not itself a copy whose source id (a snapshot's Key.ExternalId, or an external id) is the copy's
    // external id or sourceFileId, the newest observation of each provider, a native memory's own row deciding for its
    // provider. So it is the narrowest of them: the person, when one person's scope is among them; no one's (null: two
    // people's, which nobody reads both of); else the household, when it is among them; else its own (the demo's, which
    // everyone reads). An original that a source share lets Family read counts as the household's. A household copy of
    // Alex's private Drive file therefore uses up Alex's share, never Family's. It decides budgets only, never what anyone
    // reads: each read applies the copy rule itself.
    const string CopyBudget = $"""
        CROSS JOIN LATERAL (SELECT CASE WHEN coalesce(r.metadata->>'retrievalMode','') NOT IN ({Database.CopyModes}) THEN r.subject_id ELSE (
          SELECT CASE WHEN count(DISTINCT l.id) FILTER (WHERE l.kind='person')>1 THEN NULL
            ELSE coalesce(min(l.id) FILTER (WHERE l.kind='person'),min(l.id) FILTER (WHERE l.kind='household'),r.subject_id) END
          FROM (SELECT k.id,k.kind FROM subjects k WHERE k.id=r.subject_id
            UNION ALL
            SELECT k.id,k.kind FROM (
              SELECT o.subject_id,o.provider,s.seen,s.own,max(s.seen) OVER (PARTITION BY o.provider) AS newest,
                bool_or(s.own) OVER (PARTITION BY o.provider) AS owned FROM resources o
              CROSS JOIN LATERAL (SELECT coalesce((o.relationship_snapshot->>'RetrievedAt')::timestamptz,o.indexed_at) AS seen,
                o.provider='memory' AND o.relationship_snapshot IS NULL AND o.external_id=o.id::text AS own) s
              WHERE o.id<>r.id AND coalesce(o.metadata->>'retrievalMode','') NOT IN ({Database.CopyModes})
                AND (o.external_id IN (r.external_id,r.metadata->>'sourceFileId')
                  OR o.relationship_snapshot IS NOT NULL AND o.relationship_snapshot->'Key'->>'ExternalId' IN (r.external_id,r.metadata->>'sourceFileId'))) n
            JOIN subjects k ON k.id=CASE WHEN EXISTS(SELECT 1 FROM source_shares x JOIN subjects p ON p.id=x.person_id AND p.status='current'
                WHERE x.person_id=n.subject_id AND x.provider=n.provider) THEN '{RhythmConfig.Household}' ELSE n.subject_id END
            WHERE CASE WHEN n.owned THEN n.own ELSE n.seen=n.newest END) l) END AS subject OFFSET 0) b
        """;

    // A save_document copy of a native memory (its external id, or a legacy copy's sourceFileId, the memory's id): never a
    // document star of its own, whatever the memory's state, so a memory has exactly one star, its own, and a held,
    // deprecated or trashed one never comes back through a copy (ledger RHY-33, SB-P3-11). Who reads the copy is the copy
    // rule's, as before (Database.Visible); only the map leaves it out.
    const string NativeCopy = $"""
        (coalesce(r.metadata->>'retrievalMode','') IN ({Database.CopyModes}) AND EXISTS(SELECT 1 FROM memories n WHERE n.id IN (
          CASE WHEN r.external_id ~ '{Uuid}' THEN r.external_id::uuid END,
          CASE WHEN r.metadata->>'sourceFileId' ~ '{Uuid}' THEN (r.metadata->>'sourceFileId')::uuid END)))
        """;
    const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

    // The text records of `subjects` that are documents, the most recently changed first, at most `budget` + 1. With
    // `scoped`, those budgeted under `subjects` (CopyBudget): a copy whose own subject is wider (the household's, the
    // demo's) may be another subject's.
    static async Task<List<(Guid Id, CatalogDocument Document)>> DocumentRows(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal,
        string[] subjects, int budget, (string Subject, string Provider)[] measured, CancellationToken ct, bool scoped = false)
    {
        var rows = new List<(Guid Id, CatalogDocument Document)>();
        var whose = scoped
            ? $"""
              (r.subject_id=ANY(@subjects) OR coalesce(r.metadata->>'retrievalMode','') IN ({Database.CopyModes})
                AND r.subject_id IN (SELECT k.id FROM subjects k WHERE k.kind<>'person')) AND b.subject=ANY(@subjects)
              """
            : "r.subject_id=ANY(@subjects)";
        // A provider that holds measurements is shown by its data stars; its text records (exports, day records) are not
        // documents and never count toward the budget. Past the budget, the most recently changed documents are kept.
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT r.id,r.subject_id,r.provider,r.scope,r.external_id,r.title,coalesce(r.revision,r.checksum),r.source_updated_at,
              r.metadata->>'sourceFileId',left(r.content,{EmbeddingText}),
              (SELECT count(*) FROM relationships l JOIN resources p ON p.id=l.target_id
                WHERE l.source_id=r.id AND l.type='contains' AND p.status='current')::int{(scoped ? ",b.subject" : "")}
            FROM resources r {(scoped ? CopyBudget + "\n" : "")}WHERE {Own} AND {whose} AND length(r.content)>0
              AND NOT EXISTS(SELECT 1 FROM unnest(@measuredSubjects,@measuredProviders) AS m(subject,provider)
                WHERE m.subject=r.subject_id AND m.provider=r.provider)
              AND NOT EXISTS(SELECT 1 FROM relationships l JOIN resources p ON p.id=l.source_id
                WHERE l.target_id=r.id AND l.type='contains' AND p.status='current')
              AND NOT {NativeCopy}
            ORDER BY coalesce(r.source_updated_at,r.indexed_at) DESC,r.subject_id,r.provider,r.external_id LIMIT {budget + 1}
            """, ("subjects", subjects), ("measuredSubjects", measured.Select(m => m.Subject).ToArray()),
            ("measuredProviders", measured.Select(m => m.Provider).ToArray()), ("principal", principal)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetGuid(0), new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt32(10), reader.GetString(9),
                    scoped && reader.GetString(11) is var budgeted && budgeted != reader.GetString(1) ? budgeted : null)));
        return rows;
    }

    // With graph.pluginLinks on (SB-P4-13): each document's own links to another document of this pass that a plugin made
    // (ingest's records' links: no engine, evidence ingest:<plugin>, active), between two records of one subject and
    // provider, at most CatalogStars.MaxPluginLinks a document, oldest first, and at most `total` in the whole pass
    // (CatalogStars.PluginLinkBudget), the oldest of all, so a plugin's links never push the pass past the map's own link
    // budget (RelationshipDiscovery.MaximumLinks: 500 documents of 20 links each would be 10 000 of its 12 000); `ids` are
    // the documents' rows, in order. Never one of a type the map gives its own meaning (CatalogStars.MapOwnLinkTypes), so
    // no plugin's link is drawn or worded as the engine's similarity, a replacement, a page or a membership. The two ends were read through the predicate already (DocumentRows), and a link
    // joins nothing else.
    internal static async Task PluginLinks(NpgsqlConnection c, NpgsqlTransaction tx, List<Guid> ids, List<CatalogDocument> documents, CancellationToken ct,
        int total = CatalogStars.PluginLinkBudget)
    {
        var at = new Dictionary<Guid, int>();
        for (var i = 0; i < ids.Count; i++) at.TryAdd(ids[i], i);
        var links = new Dictionary<int, List<CatalogLink>>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT x.source_id,x.external_id,x.type,x.verification,x.evidence FROM (
              SELECT l.source_id,t.external_id,l.type,l.verification,l.evidence,l.created_at,
                row_number() OVER (PARTITION BY l.source_id ORDER BY l.created_at,t.external_id,l.type) AS n
              FROM relationships l JOIN resources s ON s.id=l.source_id JOIN resources t ON t.id=l.target_id
              WHERE l.source_id=ANY(@ids) AND l.target_id=ANY(@ids) AND l.source_id<>l.target_id AND l.engine IS NULL
                AND l.lifecycle='active' AND starts_with(l.evidence,'{CatalogStars.PluginLocator}') AND length(l.evidence)<=200
                AND l.type ~ @type AND NOT l.type=ANY(@own) AND t.subject_id=s.subject_id AND t.provider=s.provider) x
            WHERE x.n<=@max ORDER BY x.created_at,x.source_id,x.n LIMIT @total
            """, ("ids", ids.ToArray()), ("max", CatalogStars.MaxPluginLinks), ("total", total), ("type", "^[a-z][a-z0-9_-]{0,63}$"),
            ("own", CatalogStars.MapOwnLinkTypes)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (at.TryGetValue(reader.GetGuid(0), out var i))
                    (links.TryGetValue(i, out var some) ? some : links[i] = []).Add(
                        new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        foreach (var (i, some) in links) documents[i] = documents[i] with { Links = some };
    }

    // One document's readable text, at most `ReadLimit` characters: its pages in order, or its own body; and its archived
    // original, when someone added it as a file. Null when `principal` reads no such record.
    public static async Task<(string Text, bool Complete, DateTimeOffset? Modified, GraphOriginal? Original)?> Text(Database db, Guid principal,
        string subject, string provider, string scope, string externalId, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
        Guid id; string body; int parts; DateTimeOffset? modified; GraphOriginal? original;
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT r.id,left(r.content,{ReadLimit + 1}),r.source_updated_at,
              (SELECT count(*) FROM relationships l JOIN resources p ON p.id=l.target_id
                WHERE l.source_id=r.id AND l.type='contains' AND p.status='current')::int,{DocumentOriginals.Columns}
            FROM resources r WHERE {Own} AND r.subject_id=@s AND r.provider=@p AND r.scope=@scope AND r.external_id=@e
            """, ("s", subject), ("p", provider), ("scope", scope), ("e", externalId), ("principal", principal)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            id = reader.GetGuid(0); body = reader.GetString(1);
            modified = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2); parts = reader.GetInt32(3);
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            original = DocumentOriginals.Describe(id, reader.GetString(4), reader.GetBoolean(5), Text(6), Text(7), Text(8));
        }
        var text = parts == 0 ? body : await Pages(c, tx, principal, id, ReadLimit + 1, ct);
        await tx.CommitAsync(ct);
        return text.Length > ReadLimit ? (text[..ReadLimit], false, modified, original) : (text, true, modified, original);
    }

    // The contained pages in natural order ("page:2" before "page:10"), joined, up to `limit` characters.
    static async Task<string> Pages(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, Guid parent, int limit, CancellationToken ct)
    {
        var pages = new List<(string Id, string Text)>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT p.external_id,left(p.content,{limit}) FROM relationships l JOIN resources p ON p.id=l.target_id
            WHERE l.source_id=@id AND l.type='contains' AND {Page} LIMIT 2000
            """, ("id", parent), ("principal", principal)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) pages.Add((reader.GetString(0), reader.GetString(1)));
        var text = new System.Text.StringBuilder();
        foreach (var page in pages.OrderBy(p => Natural(p.Id), StringComparer.Ordinal))
        {
            if (text.Length >= limit) break;
            if (text.Length > 0) text.Append("\n\n");
            text.Append(page.Text);
        }
        return text.Length > limit ? text.ToString(0, limit) : text.ToString();
    }
    static string Natural(string id) => Digits().Replace(id, m => m.Value.PadLeft(12, '0'));

    // Each metric of `subjects` (or of one star's provider and scope when `only` is given, or of one provider's every
    // scope with `provider`) with its extent; null when there are more than `limit`.
    static async Task<List<CatalogMetric>?> Metrics(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string[] subjects,
        (string Provider, string Scope)? only, int limit, CancellationToken ct, string? provider = null)
    {
        var metrics = new List<CatalogMetric>();
        await using var cmd = Database.Command(c, tx, $"""
            SELECT r.subject_id,r.provider,r.scope,m.metric,m.unit,count(*),min(m.local_date),max(m.local_date),max(m.observed_at)
            FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE {Own} AND r.subject_id=ANY(@subjects) AND (@all OR r.provider=@provider AND r.scope=@scope)
              AND (@anyProvider OR r.provider=@measuring)
            GROUP BY 1,2,3,4,5 ORDER BY 1,2,3,4,5 LIMIT {limit + 1}
            """, ("subjects", subjects), ("all", only is null), ("provider", only?.Provider ?? ""), ("scope", only?.Scope ?? ""),
            ("anyProvider", provider is null), ("measuring", provider ?? ""), ("principal", principal));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            metrics.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetFieldValue<DateOnly>(6), reader.GetFieldValue<DateOnly>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        return metrics.Count > limit ? null : metrics;
    }

    // The providers that hold measurements of `subjects`, by name.
    static async Task<List<string>> Measuring(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string[] subjects, CancellationToken ct)
    {
        var providers = new List<string>();
        await using var cmd = Database.Command(c, tx, $"""
            SELECT DISTINCT r.provider FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE {Own} AND r.subject_id=ANY(@subjects) ORDER BY 1
            """, ("subjects", subjects), ("principal", principal));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) providers.Add(reader.GetString(0));
        return providers;
    }

    // One data star's measurements that `principal` reads: each metric's latest reading and daily minimum, mean and
    // maximum for the last `Days` days, at most `MaxSeries` metrics. Values are read for this response only.
    public static async Task<IReadOnlyList<GraphSeries>> Series(Database db, Guid principal, RelationshipCatalog catalog, string subject,
        string provider, string scope, string group, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
        var members = (await Metrics(c, tx, principal, [subject], (provider, scope), MaxMetrics, ct) ?? throw new InvalidOperationException("Catalog metrics exceed the star budget"))
            .Where(m => catalog.Group(provider, m.Metric).Id == group).Take(MaxSeries).ToArray();
        var names = members.Select(m => m.Metric).Distinct().ToArray();
        var days = new Dictionary<(string, string), List<GraphDay>>();
        var latest = new Dictionary<(string, string), GraphReading>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT m.metric,m.unit,m.local_date,min(m.value),avg(m.value),max(m.value),count(*)
            FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE {Own} AND r.subject_id=@s AND r.provider=@p AND r.scope=@scope AND m.metric=ANY(@metrics)
              AND m.local_date>=current_date-{Days}
            GROUP BY 1,2,3 ORDER BY 1,2,3
            """, ("s", subject), ("p", provider), ("scope", scope), ("metrics", names), ("principal", principal)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!days.TryGetValue(key, out var list)) days[key] = list = [];
                list.Add(new(Date(reader.GetFieldValue<DateOnly>(2)), reader.GetDouble(3), Math.Round(reader.GetDouble(4), 4),
                    reader.GetDouble(5), reader.GetInt64(6)));
            }
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT DISTINCT ON (m.metric,m.unit) m.metric,m.unit,m.value,m.observed_at,m.local_date
            FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE {Own} AND r.subject_id=@s AND r.provider=@p AND r.scope=@scope AND m.metric=ANY(@metrics)
            ORDER BY m.metric,m.unit,m.local_date DESC,m.observed_at DESC NULLS LAST
            """, ("s", subject), ("p", provider), ("scope", scope), ("metrics", names), ("principal", principal)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                latest[(reader.GetString(0), reader.GetString(1))] = new(reader.GetDouble(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), Date(reader.GetFieldValue<DateOnly>(4)));
        await tx.CommitAsync(ct);
        return members.Select(m => new GraphSeries(m.Metric, Label(m.Metric), m.Unit, latest.GetValueOrDefault((m.Metric, m.Unit)),
            days.GetValueOrDefault((m.Metric, m.Unit)) ?? [])).ToArray();
    }

    // "resting_heart_rate" reads as "Resting heart rate"; "hrv" and "spo2" stay recognizable.
    public static string Label(string metric)
    {
        var words = metric.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w is "hrv" or "spo2" ? w.ToUpperInvariant().Replace("SPO2", "SpO2") : w).ToArray();
        var text = string.Join(' ', words);
        return text.Length == 0 ? metric : char.ToUpperInvariant(text[0]) + text[1..];
    }
    static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    [GeneratedRegex("[0-9]+")] private static partial Regex Digits();
}

// Turns one catalog inventory into this pass's stars. Pure: the caller supplies the clock, the Drive
// records already on the map and the (optional) vector function.
public static class CatalogStars
{
    // The provider id of the catalog's own stars (part of every catalog graphId). An install that predates this repo can
    // keep its old ids with RHYTHM_CATALOG_PROVIDER (docs/dev/migration-compat.md). Read once, and never changed while
    // the engine runs.
    public static string Provider { get; } =
        Environment.GetEnvironmentVariable("RHYTHM_CATALOG_PROVIDER") is { Length: > 0 } provider ? provider : "rhythm";
    public static string DataAccount(string provider) => "data:" + provider;
    public static bool IsData(ResourceKey key) => key.Provider == Provider && key.Account.StartsWith("data:", StringComparison.Ordinal);
    // The provider whose rows a star reads, the one a source share names (docs/dev/memory.md section 4): a data star's
    // measuring provider, a catalog document star's own provider (its account), else the star's provider.
    public static string SourceProvider(ResourceKey key) =>
        IsData(key) ? key.Account["data:".Length..] : key.Provider == Provider ? key.Account : key.Provider;
    public const int MaxTopicLinks = 12;
    public const string TopicLocator = "catalog:topic";
    // A plugin's own link's evidence (Ingest.Records: ingest:<plugin>), and how many a document's star carries
    // (graph.pluginLinks, SB-P4-13): ingest takes at most 20 a record.
    public const string PluginLocator = "ingest:";
    public const int MaxPluginLinks = 20;
    // At most this many plugin links in one pass, whatever the plugins hold: the oldest, the rest left off the map.
    public const int PluginLinkBudget = 2000;
    // The link types the map and the app give Rhythm's own meaning (the engine's similarity and copies, a replacement, a
    // document's pages, a project's and a folder's members, a digest's sources), which a plugin's link never takes on
    // the map: one of these stays off it, served by get_related_resources alone, as with graph.pluginLinks off.
    public static readonly string[] MapOwnLinkTypes = ["similar_to", "copy_of", "supersedes", "contains", "in_project", "in_folder", "drawn_from"];

    // `mapped` is the rest of this pass's records (memories, documents, projects): a data star links to those of
    // its own person or the household whose title or summary names one of its topics, newest first.
    public static IEnumerable<DiscoveryRecord> Records(RelationshipCatalog catalog, CatalogInventory inventory,
        ISet<string> driveFiles, DateTimeOffset now, Func<ResourceKey, string, string, string, float[]?> vector,
        IReadOnlyList<DiscoveryRecord>? mapped = null)
    {
        foreach (var star in inventory.Metrics.GroupBy(m => (m.Subject, m.Provider, m.Scope, Group: catalog.Group(m.Provider, m.Metric))))
        {
            var (subject, provider, scope, group) = star.Key;
            var members = star.OrderBy(m => m.Metric, StringComparer.Ordinal).ToArray();
            var key = new ResourceKey(subject, Provider, DataAccount(provider), $"{subject}:{group.Id}" + (scope == "private" ? "" : ":" + scope));
            var title = catalog.Title(subject, group.Label);
            var names = members.Select(m => CatalogReader.Label(m.Metric)).Distinct().ToArray();
            var listed = names.Length <= 6 ? string.Join(", ", names) : string.Join(", ", names.Take(5)) + $" and {names.Length - 5} more";
            var overview = $"{catalog.ProviderLabel(provider)} · {listed} · {Day(members.Min(m => m.First))} to {Day(members.Max(m => m.Last))}";
            var shareable = catalog.Scoped && subject != RelationshipDiscovery.Household;
            var about = TopicLinks(subject, catalog.Topics(provider, group.Id), mapped ?? [], shareable);
            // The links are part of the star's observed state, so a change in them is a new revision. With scopes on a
            // person's data star is read by the person and, through a source share, by Family, and the two may read
            // different records (the person may not be in Family, Family doesn't read the person's own): so its revision
            // names its title and metrics only, never its links, which are still found, retired and served as links. A
            // record's revision then never names a record of another scope (docs/dev/memory.md section 8).
            var revision = "catalog-sha256:" + Hashing.Hash(JsonSerializer.Serialize(new { title, metrics = members.Select(m => new[] { m.Metric, m.Unit }),
                about = about.Where(_ => !shareable).Select(a => a.Target!.GraphId).Order(StringComparer.Ordinal) }));
            var latest = members.Max(m => m.Latest);
            var description = $"{group.Label}. {catalog.ProviderLabel(provider)} health data: "
                + string.Join(", ", members.Select(m => $"{CatalogReader.Label(m.Metric).ToLowerInvariant()} ({m.Unit})")) + ".";
            yield return new(key, scope, "record", title, null, revision, latest is { } time && time <= now ? time : null, now, about,
                ContentRevision: revision, Embedding: vector(key, revision, title, description),
                Groups: [new($"{Provider}:{subject}:{provider}", catalog.ProviderLabel(provider))],
                Overview: overview.Length <= 1000 ? overview : overview[..999] + "…");
        }
        foreach (var document in inventory.Documents)
        {
            // The Drive original already on the map stands for its saved copy.
            if (driveFiles.Contains(document.ExternalId) || document.SourceFileId is { } file && driveFiles.Contains(file)) continue;
            var key = new ResourceKey(document.Subject, Provider, document.Provider, $"{document.Subject}:{document.ExternalId}");
            var title = document.Title.Length <= 1000 ? document.Title : document.Title[..999] + "…";
            var revision = "catalog-sha256:" + Hashing.Hash(JsonSerializer.Serialize(new { title, document.Revision, document.Parts, document.Modified }));
            var label = catalog.ProviderLabel(document.Provider);
            // With graph.pluginLinks on, the plugin's own links to its other records' stars, as its suggestions (SB-P4-13): a
            // source's assertion, kept as stated (D-191). They are not part of the star's revision, so a star is as before.
            Reference[] plugin = [.. (document.Links ?? []).Select(l => new Reference(
                new ResourceKey(document.Subject, Provider, document.Provider, $"{document.Subject}:{l.Target}"), null, l.Locator,
                $"Linked by the {l.Locator[PluginLocator.Length..]} plugin", l.Type, true, l.Verification))];
            yield return new(key, document.Scope, "document", title, null, revision,
                document.Modified is { } modified && modified <= now ? modified : null, now, plugin,
                ContentRevision: revision,
                Embedding: SimilaritySuggestions.Readable(document.Text) ? vector(key, revision, title, document.Text) : null,
                Groups: [new($"{Provider}:{document.Subject}:{document.Provider}", label)],
                Overview: $"{label} record saved in Rhythm" + (document.Parts > 0 ? $", {document.Parts} sections" : ""),
                Reader: document.Reader);
        }
    }
    static string Day(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    // An inferred, undirected "about" link whose evidence is the matched word, never the record's text. With `apart` (a
    // person's data star with scopes on) the household's records and the person's own are each taken newest first up to
    // MaxTopicLinks of their own, so the household links a Family reader of the star is served never depend on how many
    // of the person's own records name a topic.
    static Reference[] TopicLinks(string subject, string[] topics, IReadOnlyList<DiscoveryRecord> mapped, bool apart = false)
    {
        if (topics.Length == 0) return [];
        var words = topics.Select(t => (Topic: t, Pattern: new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(t)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToArray();
        var matches = mapped.Where(r => r.Status == "current" && r.Key.Provider != Provider
                && (r.Key.Subject == subject || r.Key.Subject == RelationshipDiscovery.Household))
            .Select(r => (Record: r, Topic: words.FirstOrDefault(w => w.Pattern.IsMatch(r.Title) || r.Overview is { } o && w.Pattern.IsMatch(o)).Topic))
            .Where(m => m.Topic is not null).ToArray();
        IEnumerable<(DiscoveryRecord Record, string Topic)> Newest(IEnumerable<(DiscoveryRecord Record, string Topic)> some) => some
            .OrderByDescending(m => m.Record.ModifiedAt ?? m.Record.CreatedAt ?? m.Record.RetrievedAt)
            .ThenBy(m => m.Record.Key.GraphId, StringComparer.Ordinal)
            .Take(MaxTopicLinks);
        var chosen = apart
            ? Newest(matches.Where(m => m.Record.Key.Subject == RelationshipDiscovery.Household)).Concat(Newest(matches.Where(m => m.Record.Key.Subject != RelationshipDiscovery.Household)))
            : Newest(matches);
        return chosen.Select(m => new Reference(m.Record.Key, null, TopicLocator, $"Mentions {m.Topic}", "about", true, "inferred", 0.6, false)).ToArray();
    }
}
