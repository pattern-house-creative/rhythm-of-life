using System.Text.Json;
using Npgsql;

namespace Knowledge.Relationships;

// Map reads (every app, harness and dashboard request) keep short budgets. A rebuild (the engine's scheduler, a
// requested rebuild or the `refresh` command) runs off the request path, so it may take longer, committing intake in
// bounded chunks.
public sealed record RelationshipBudget(TimeSpan Operation, TimeSpan DatabasePhase, int IntakeChunk)
{
    public static RelationshipBudget Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), 250);
    public static RelationshipBudget Scheduled { get; } = new(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(60), 250);
}

// The map's store: Refresh writes the snapshot a rebuild fetched from its source, and the reads (View,
// ReadSnapshot) serve that snapshot without calling a source, each reader what Served gives it. RelationshipHost builds
// one for each rebuild and read; `scopes`, given when a rebuild runs with memory.scopes on, is the pass's catalog, among
// whose subjects its links are shared out (RelationshipDiscovery.Discover and Bounded).
public sealed class RelationshipEngine(Database db, IRelationshipSource source,
    IRelationshipSuggestions? suggestions = null, RelationshipBudget? budget = null, RelationshipCatalog? scopes = null)
{
    public const string Owner = "cross-source-v1";
    internal sealed record Stored(Guid Id, DiscoveryRecord Record, string Version);
    readonly RelationshipBudget limits = budget ?? RelationshipBudget.Default;
    // How many links the last rebuild left out past their scope's share (with scopes on, RelationshipDiscovery.Bounded),
    // for the rebuild's notes: a count only.
    public int LinksLeftOut { get; private set; }
    // Whether the reads serve a native memory a newer one of its scope replaced, with what replaced it (SB-P4-11;
    // RelationshipCatalog.Replaced, memory.tools on). Off, the default, every read is exactly as before.
    public bool Replaced { get; init; }
    // Whether a read says of a native memory's star that its memory is a work item (migration 079; set from
    // RelationshipCatalog.Work, memory.work on). Off, the default, every read is exactly as before.
    public bool Work { get; init; }
    // Whether the reads serve the links a person or an AI client made (made_links, SB-P4-02; RelationshipCatalog.MadeLinks,
    // memory.tools on) beside the map's own. Off, the default, none is read, so an install with every setting off answers
    // as before even after an operator's import brought made links in (043).
    public bool MadeLinks { get; init; }

    public async Task Refresh(Guid principal, CancellationToken ct = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(limits.Operation); ct = operation.Token;
        // Authentication stays with the host; still reject disabled/unknown principals before invoking an adapter.
        await using (var enabled = db.Source.CreateCommand("SELECT EXISTS(SELECT 1 FROM principals WHERE id=@p AND enabled)"))
        {
            enabled.Parameters.AddWithValue("p", principal);
            if (await enabled.ExecuteScalarAsync(ct) is not true) throw new UnauthorizedAccessException("Unavailable");
        }
        // A snapshot-aware source reuses work for unchanged records and reports what a complete listing lost.
        if (source is IRelationshipSnapshotSource snapshotSource)
            snapshotSource.Prime((await Capture(principal, true, ct)).Select(s => s.Record).ToArray());
        var batch = await source.Fetch(principal, ct);
        RelationshipDiscovery.Validate(batch);
        // PostgreSQL timestamps have microsecond precision; normalize the JSON snapshot identically.
        static DateTimeOffset Precision(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, TimeSpan.Zero);
        batch = batch with { Records = batch.Records.Select(r => r with {
            RetrievedAt = Precision(r.RetrievedAt), ModifiedAt = r.ModifiedAt is { } time ? Precision(time) : null }).ToArray() };
        List<Stored> captured = [];
        // Checkpoint source intake and invalidation before any potentially slow provider work.
        // A later failure never restores evidence invalidated by a newer source observation.
        // Large batches commit in chunks; each chunk is its own checkpoint and the working set
        // is captured under the writer lock of the final one, exactly as for a single chunk.
        var chunks = batch.Records.OrderBy(r => r.Key.GraphId, StringComparer.Ordinal).Chunk(limits.IntakeChunk).ToList();
        if (chunks.Count == 0) chunks.Add([]);
        for (var i = 0; i < chunks.Count; i++)
        {
            using var writeBudget = DatabaseBudget(ct);
            await using var c = await db.Source.OpenConnectionAsync(writeBudget.Token);
            await using var tx = await c.BeginTransactionAsync(writeBudget.Token);
            await LockWriter(c, tx, writeBudget.Token);
            foreach (var record in chunks[i]) await Save(principal, record, c, tx, writeBudget.Token);
            if (i == chunks.Count - 1) captured = await Load(principal, c, tx, true, writeBudget.Token);
            await tx.CommitAsync(writeBudget.Token);
        }

        // No database connection, transaction, row lock or advisory lock crosses these awaits.
        var allowed = new List<Stored>();
        foreach (var item in captured)
            if (await Check(principal, item.Record, ct) == SourceCheck.Available) allowed.Add(item);
        var fresh = (await Capture(principal, false, ct)).ToDictionary(s => s.Id);
        allowed.RemoveAll(s => !fresh.TryGetValue(s.Id, out var current) || current.Version != s.Version);
        var records = allowed.Select(s => s.Record).ToArray();
        var proposed = suggestions is null ? Array.Empty<Suggestion>() : await suggestions.Suggest(records, ct);
        // With scopes off a pass past the link budget fails, as before; with them on each scope keeps its own share.
        var links = RelationshipDiscovery.Bounded(RelationshipDiscovery.Discover(records, proposed, scopes is not null), records, scopes, out var leftOut);
        LinksLeftOut = leftOut;
        // Suggestions may take time. Recheck provider authorization/revision after they finish.
        if (suggestions is not null)
            foreach (var item in allowed.ToArray())
                if (await Check(principal, item.Record, ct) != SourceCheck.Available) allowed.Remove(item);

        using var commitBudget = DatabaseBudget(ct);
        var commitToken = commitBudget.Token;
        await using var commitConnection = await db.Source.OpenConnectionAsync(commitToken);
        await using var commit = await commitConnection.BeginTransactionAsync(commitToken);
        await LockWriter(commitConnection, commit, commitToken);
        // xmin is a short-lived optimistic token, not a durable resource identity. Lock in UUID
        // order before checking it so ordinary ingest/revoke/delete writers cannot change an
        // endpoint between the comparison and the edge commit. Never lock all health resources.
        await using (var fence = Database.Command(commitConnection, commit,
            "SELECT id FROM resources WHERE id=ANY(@ids) ORDER BY id FOR UPDATE", ("ids", allowed.Select(s => s.Id).ToArray())))
            await fence.ExecuteNonQueryAsync(commitToken);
        fresh = (await Load(principal, commitConnection, commit, false, commitToken)).ToDictionary(s => s.Id);
        allowed.RemoveAll(s => !fresh.TryGetValue(s.Id, out var current) || current.Version != s.Version);
        var ids = allowed.ToDictionary(s => s.Record.Key, s => s.Id);

        // Only unchanged, authorized, complete source observations may retire absent outgoing links.
        // Invalidated or failed endpoints are left for a fresh retry, never reactivated from old work.
        var complete = batch.Records.Where(r => r.Status == "current" && r.ReferencesComplete)
            .Where(r => allowed.Any(s => s.Record.Key == r.Key && s.Record.RetrievedAt == r.RetrievedAt))
            .Select(r => ids[r.Key]).ToArray();
        // OFFSET 0 keeps the visibility check a per-link check. Without it the planner turns it into a join that starts
        // from every current resource and searches the links once for each, with both id lists in the search: time
        // that grows with the square of the map, and past the writer's statement budget at a few thousand records.
        await using (var retire = Database.Command(commitConnection, commit, $"""
            UPDATE relationships l SET lifecycle='retired',evidence='',details=jsonb_build_object()
            WHERE l.engine=@engine AND l.source_id=ANY(@ids) AND l.target_id=ANY(@stable)
              AND EXISTS(SELECT 1 FROM resources r WHERE r.id=l.source_id AND {Database.Visible()} OFFSET 0)
            """, ("engine", Owner), ("ids", complete), ("stable", ids.Values.ToArray()), ("principal", principal))) await retire.ExecuteNonQueryAsync(commitToken);
        foreach (var link in links.Where(l => ids.ContainsKey(l.Source) && ids.ContainsKey(l.Target)))
        {
            await using var cmd = Database.Command(commitConnection, commit, $"""
                INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine,lifecycle,details,checked_at)
                SELECT @s,@t,@type,@verification,@evidence,@engine,'active',@details::jsonb,now()
                WHERE EXISTS(SELECT 1 FROM resources r WHERE r.id=@s AND {Database.Visible()})
                  AND EXISTS(SELECT 1 FROM resources r WHERE r.id=@t AND {Database.Visible()})
                ON CONFLICT(source_id,target_id,type) DO UPDATE SET verification=excluded.verification,
                  evidence=excluded.evidence,details=excluded.details,lifecycle='active',checked_at=now()
                WHERE relationships.engine=excluded.engine
                """, ("s", ids[link.Source]), ("t", ids[link.Target]), ("type", link.Type),
                ("verification", link.Verification), ("evidence", JsonSerializer.Serialize(link.Evidence)),
                ("engine", Owner), ("details", JsonSerializer.Serialize(link)), ("principal", principal));
            await cmd.ExecuteNonQueryAsync(commitToken);
        }
        await commit.CommitAsync(commitToken);
    }

    // Hard budgets cover whole DB phases, including pool/lock waits and multi-statement writes.
    CancellationTokenSource DatabaseBudget(CancellationToken ct)
    {
        var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(limits.DatabasePhase);
        return phase;
    }
    static Task LockWriter(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct) => Database.Execute(c, tx,
        "SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='4s'; SELECT pg_advisory_xact_lock(61984422)", ct);
    async Task<List<Stored>> Capture(Guid principal, bool vectors, CancellationToken ct)
    {
        await using var c = await db.Source.OpenConnectionAsync(ct);
        return await Load(principal, c, null, vectors, ct);
    }
    async Task Save(Guid principal, DiscoveryRecord record, NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        db.Policies.Validate(new(record.Key.Subject, record.Key.Provider, record.Key.CatalogId,
            record.Kind, record.Scope, record.Title, record.Url));
        var safe = record.Status == "current" ? record : record with { Title = "Unavailable", Url = null, References = [] };
        var external = safe.Key.CatalogId;
        if (safe.ExistingResourceId is { } existing)
        {
            // A trusted adapter can explicitly bind an already-ingested catalog row. Never guess
            // account ownership from a matching title, URL, or provider-local ID alone.
            await using var resolve = Database.Command(c, tx, $"""
                SELECT r.external_id FROM resources r WHERE r.id=@id AND r.subject_id=@subject AND r.provider=@provider
                  AND r.external_id IN (@native,@namespaced)
                  AND {Database.Allowed("r.subject_id", "r.provider", "r.scope")}
                  AND (r.relationship_snapshot IS NULL OR
                    (r.relationship_snapshot->'Key'->>'Account'=@account AND r.relationship_snapshot->'Key'->>'ExternalId'=@native))
                FOR UPDATE
                """, ("id", existing), ("subject", safe.Key.Subject), ("provider", safe.Key.Provider),
                ("native", safe.Key.ExternalId), ("namespaced", external), ("principal", principal), ("account", safe.Key.Account));
            external = await resolve.ExecuteScalarAsync(ct) as string ?? throw new UnauthorizedAccessException("Catalog binding unavailable");
        }
        else
        {
            await using var resolve = Database.Command(c, tx, """
                SELECT external_id FROM resources WHERE subject_id=@subject AND provider=@provider
                  AND relationship_snapshot->'Key'->>'Account'=@account
                  AND relationship_snapshot->'Key'->>'ExternalId'=@native FOR UPDATE
                """, ("subject", safe.Key.Subject), ("provider", safe.Key.Provider), ("account", safe.Key.Account), ("native", safe.Key.ExternalId));
            await using var matches = await resolve.ExecuteReaderAsync(ct);
            if (await matches.ReadAsync(ct)) external = matches.GetString(0);
            if (await matches.ReadAsync(ct)) throw new InvalidOperationException("Ambiguous catalog binding");
        }
        // Partial reference lists must not replace a complete list at the same source revision.
        // A changed revision invalidates old evidence even when its replacement is incomplete.
        // A source that reports no modification time (e.g. a listing) is ordered by observation
        // time alone; a known modification time may never move backwards.
        await using var cmd = Database.Command(c, tx, $"""
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,revision,
              source_updated_at,relationship_snapshot,status,domain)
            SELECT @id,@subject,@provider,@external,@kind,@scope,@title,@url,@revision,@modified,@metadata::jsonb,@status,'unclassified'
            WHERE {Database.Allowed("@subject", "@provider", "@scope")}
            ON CONFLICT(subject_id,provider,external_id) DO UPDATE SET kind=excluded.kind,scope=excluded.scope,
              title=excluded.title,source_url=excluded.source_url,revision=excluded.revision,
              source_updated_at=CASE WHEN excluded.status<>'current' THEN coalesce(excluded.source_updated_at,resources.source_updated_at)
                ELSE excluded.source_updated_at END,
              relationship_snapshot=excluded.relationship_snapshot,status=excluded.status,indexed_at=now()
            WHERE (resources.relationship_snapshot IS NOT NULL OR resources.id=@existing)
              AND (resources.relationship_snapshot IS NULL OR (resources.relationship_snapshot->>'RetrievedAt')::timestamptz < @observed
                OR resources.relationship_snapshot=@metadata::jsonb)
              AND (resources.source_updated_at IS NULL OR @modified>=resources.source_updated_at OR @modified IS NULL)
              AND NOT (@partial AND resources.revision IS NOT DISTINCT FROM excluded.revision
                AND resources.relationship_snapshot->>'ReferencesComplete'='true' AND @status='current')
            """, ("id", Guid.NewGuid()), ("subject", safe.Key.Subject), ("provider", safe.Key.Provider),
            ("external", external), ("kind", safe.Kind), ("scope", safe.Scope), ("title", safe.Title),
            ("url", safe.Url), ("revision", safe.Revision), ("modified", safe.ModifiedAt?.ToUniversalTime()),
            ("metadata", JsonSerializer.Serialize(safe)), ("status", safe.Status),
            ("principal", principal), ("observed", safe.RetrievedAt.ToUniversalTime()), ("partial", !safe.ReferencesComplete),
            ("existing", safe.ExistingResourceId));
        // Explicit types for nullable parameters used in predicates as well as INSERT expressions.
        cmd.Parameters["modified"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
        cmd.Parameters["existing"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // What a reader is served of the snapshot rows (docs/dev/memory.md section 8). With memory.scopes off, exactly today's
    // rule: the rows the one predicate shows (Database.Visible), and a native memory's or project's star only while its
    // record says so now (MemoryStars.Served: a held or trashed memory's star is no one's at once; with no native
    // memories, as before). With scopes on, two things more are judged at each read,
    // so a narrowing reaches every reader at once and nothing waits for a pass:
    // - Of the rows that stand for one star (one graph id: provider, account and source id, with no subject in it), only
    //   the newest observation counts, before the reader's filter and whatever its status (on a tie, the first by row id,
    //   as Readable keeps). A record re-attributed between passes, such as a Second Brain memory moved from Family into
    //   Alex's private scope, is then never served under its old subject while that older row is still fresh, nor once
    //   the newer one is deleted at its source.
    // - A star is served to its record's readers: a catalog data star to readers of its person's measurements from that
    //   provider (a source share included), a catalog document star only while the reader reads the catalog row behind
    //   it (so a save_document copy's star follows the copy rule), and a native memory's star only while the reader reads
    //   the memory's own row, which a share or un-share changes in place and a pass never re-stamps (D-063), and the
    //   memory is not held; a native project's while the reader reads the project (MemoryStars.Served). Each catalog
    //   record is looked up by its own key first (OFFSET 0 keeps the planner from checking every candidate record instead).
    // `@catalog` is the catalog's own provider (CatalogStars.Provider); `k` is the row's star identity, read from its
    // snapshot once (Keys): its account, source id and when it was seen. `replaced` (SB-P4-11) serves a replaced memory's
    // star too (MemoryStars.Served).
    internal static string Served(string alias = "r", string k = "k", bool replaced = false)
    {
        var current = $"{alias}.status='current' AND ({alias}.authorized_until IS NULL OR {alias}.authorized_until>now())";
        return $"""
            CASE WHEN {Database.ScopesOff} THEN {Database.Visible(alias)} AND ({alias}.provider<>'memory' OR {MemoryStars.Served(alias, replaced)})
            ELSE {Newest(alias, k)}
              AND CASE WHEN {alias}.provider=@catalog AND starts_with({k}.account,'data:')
                THEN {current} AND {Database.Allowed($"{alias}.subject_id", $"substr({k}.account,6)", $"{alias}.scope")}
              WHEN {alias}.provider=@catalog THEN {current} AND starts_with({k}.source,{alias}.subject_id||':')
                AND EXISTS(SELECT 1 FROM (SELECT * FROM resources d WHERE d.subject_id={alias}.subject_id AND d.provider={k}.account
                  AND d.external_id=substr({k}.source,length({alias}.subject_id)+2) AND d.relationship_snapshot IS NULL OFFSET 0) d WHERE {Database.Visible("d")})
              WHEN {alias}.provider='memory' THEN {current} AND {MemoryStars.Served(alias, replaced)}
              ELSE {Database.Visible(alias)} END
            END
            """;
    }
    // Served's first rule, with scopes on, on its own: no other snapshot row of the same star (provider, account and source
    // id) is a newer observation than this one (on a tie, the first by row id), whatever its subject or status. Passages
    // of an adapter record take it too (SemanticSearch), so a record re-attributed between passes answers no reader under
    // its old subject.
    internal static string Newest(string alias = "r", string k = "k")
    {
        string Key(string row, string part) => $"{row}.relationship_snapshot->'Key'->>'{part}'";
        string Seen(string row) => $"({row}.relationship_snapshot->>'RetrievedAt')::timestamptz";
        return $"""
            NOT EXISTS(SELECT 1 FROM resources n WHERE n.relationship_snapshot IS NOT NULL AND n.id<>{alias}.id
                AND n.provider={alias}.provider AND {Key("n", "ExternalId")}={k}.source
                AND {Key("n", "Account")} IS NOT DISTINCT FROM {k}.account
                AND ({Seen("n")}>{k}.seen OR {Seen("n")}={k}.seen AND n.id<{alias}.id))
            """;
    }
    // The star identity Served reads, from each row's snapshot once rather than at every comparison (a snapshot with its
    // vector is stored out of line, and each read of it would fetch it again). Nothing is read with scopes off. KeysOf
    // names the identity `k` and reads it from the row `alias`.
    internal const string Keys = $"""
        CROSS JOIN LATERAL (SELECT CASE WHEN {Database.ScopesOff} THEN NULL ELSE r.relationship_snapshot->'Key' END AS key,
          CASE WHEN {Database.ScopesOff} THEN NULL ELSE (r.relationship_snapshot->>'RetrievedAt')::timestamptz END AS seen OFFSET 0) j
        CROSS JOIN LATERAL (SELECT j.key->>'Account' AS account,j.key->>'ExternalId' AS source,j.seen) k
        """;
    internal static string KeysOf(string k, string alias = "r") => $"""
        CROSS JOIN LATERAL (SELECT CASE WHEN {Database.ScopesOff} THEN NULL ELSE {alias}.relationship_snapshot->'Key' END AS key,
          CASE WHEN {Database.ScopesOff} THEN NULL ELSE ({alias}.relationship_snapshot->>'RetrievedAt')::timestamptz END AS seen OFFSET 0) {k}j
        CROSS JOIN LATERAL (SELECT {k}j.key->>'Account' AS account,{k}j.key->>'ExternalId' AS source,{k}j.seen) {k}
        """;

    // The stored snapshot rows: for a rebuild, every row the predicate shows its principal; for a reader (`serving`),
    // what Served gives it. Only a rebuild needs each record's embedding (for similarity, and to reuse it while the
    // record is unchanged); every other read leaves it out (most of a record's stored JSON), and the map model's vector and
    // its tag too (MapEmbedding, D-256), which only a rebuild's similar and copy links read. A reader served replaced
    // memories (`replaced`, SB-P4-11) is told, on each such star's record, the star of the memory that replaced it, read
    // now (MemoryStars.ReplacedBy); never stored, and without `replaced` the read is exactly as before.
    internal static async Task<List<Stored>> Load(Guid principal, NpgsqlConnection c, NpgsqlTransaction? tx, bool vectors, CancellationToken ct,
        bool serving = false, bool replaced = false, bool work = false)
    {
        var snapshot = vectors ? "r.relationship_snapshot" : "r.relationship_snapshot - 'Embedding' - 'MapEmbedding' - 'MapEmbeddingModel'";
        var truth = serving && replaced;
        // A reader served work items (`work`, 079) is told, on each such star's record, its memory's work properties, read
        // now (MemoryStars.Work); never stored, and without `work` the read is exactly as before.
        var worked = serving && work;
        // At most the budget's most recently observed rows. A pass holds no more than the budget, so the latest pass is
        // always whole; the older rows past it are ones no pass has listed lately (an adapter turned off, a listing past
        // its cap), which no reader shows once their freshness lapses. Edges are retired only between rows in the working
        // set, so a row left out keeps its links. A reader's budget is counted over what it is served.
        await using var cmd = Database.Command(c, tx, $"""
            SELECT s.id,s.relationship_snapshot,s.version{(truth ? $",{MemoryStars.ReplacedBy("s")}" : "")}{(worked ? $",{MemoryStars.Work("s")}" : "")} FROM (
              SELECT r.id,{snapshot} AS relationship_snapshot,r.xmin::text AS version{(truth || worked ? ",r.external_id" : "")} FROM resources r {(serving ? Keys : "")}
              WHERE r.relationship_snapshot IS NOT NULL AND {(serving ? Served(replaced: replaced) : Database.Visible())}
                AND r.revision IS NOT DISTINCT FROM r.relationship_snapshot->>'Revision'
                AND r.title=r.relationship_snapshot->>'Title'
                AND r.scope=r.relationship_snapshot->>'Scope'
                AND r.source_url IS NOT DISTINCT FROM r.relationship_snapshot->>'Url'
                AND r.source_updated_at IS NOT DISTINCT FROM (r.relationship_snapshot->>'ModifiedAt')::timestamptz
              ORDER BY (r.relationship_snapshot->>'RetrievedAt')::timestamptz DESC,r.id LIMIT {RelationshipDiscovery.MaximumRecords}) s
            ORDER BY s.id
            """, ("principal", principal), ("catalog", CatalogStars.Provider));
        var rows = new List<Stored>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var record = JsonSerializer.Deserialize<DiscoveryRecord>(reader.GetString(1))!;
            if (truth && !reader.IsDBNull(3)) record = record with { ReplacedBy = MemoryStars.Key(record.Key.Subject, reader.GetGuid(3)).GraphId };
            if (worked && !reader.IsDBNull(truth ? 4 : 3)) record = record with { Work = MemoryWorkProps.From(reader.GetString(truth ? 4 : 3)) };
            rows.Add(new(reader.GetGuid(0), record, reader.GetString(2)));
        }
        return rows;
    }

    async Task<SourceCheck> Check(Guid principal, DiscoveryRecord record, CancellationToken ct)
    {
        try { return await source.Check(principal, record.Key, record.Revision, ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException) { return SourceCheck.Unavailable; }
    }

    static void KnownBudget(IReadOnlyList<string>? knownNodeIds, IReadOnlyList<string>? knownEdgeIds)
    {
        if (knownNodeIds?.Count > 1500 || knownEdgeIds?.Count > 12000
            || (knownNodeIds ?? []).Concat(knownEdgeIds ?? []).Any(id => string.IsNullOrEmpty(id) || id.Length > 1000))
            throw new ArgumentException("Known graph identity budget exceeded");
    }
    // A suggestion between two native memories' stars that a person or an AI client dismissed by unlinking it (SB-P4-02,
    // migration 052): a row of memory_link_dismissals for the two memories and its type (an automatic made link unlink
    // removed is kept there too, never as a link). Served to no one, at every read, whatever later rebuilds write again;
    // with no dismissal, every link as before. SQL over the link `l` and its two star rows `s` and `t` (a native star row
    // answers as its memory, MemoryStars.Answering; no dismissal names any other row).
    internal static string Dismissed(string l = "l", string s = "s", string t = "t") =>
        DismissedPair(MemoryStars.Answering(s), MemoryStars.Answering(t), $"{l}.type");

    // SQL: whether the link type `type` between the memories `a` and `b` (SQL uuid expressions, either order) was
    // dismissed (unlink_memories, migration 052). The map's reads, recall's hops and connections all ask this one thing.
    internal static string DismissedPair(string a, string b, string type) => $"""
        EXISTS(SELECT 1 FROM memory_link_dismissals d WHERE d.low_id=least({a},{b}) AND d.high_id=greatest({a},{b}) AND d.type={type})
        """;

    // SQL: whether the link type `type` between the star rows `a` and `b` (SQL uuid expressions, either order) was dismissed
    // by a person in Discover between two stars that are not both native memories' (SB-P4-05, migration 057's
    // map_link_dismissals). The map's reads ask it of every suggestion (StarDismissed), recall's hops of every link of the
    // map's they follow; with no row, nothing is left out.
    internal static string StarDismissedPair(string a, string b, string type) => $"""
        EXISTS(SELECT 1 FROM map_link_dismissals d WHERE d.low_id=least({a},{b}) AND d.high_id=greatest({a},{b}) AND d.type={type})
        """;

    // SQL over the link `l`: a suggestion (never a confirmed link, which a dismissal never hides) a person dismissed between
    // its two star rows (StarDismissedPair).
    internal static string StarDismissed(string l = "l") =>
        $"({l}.verification<>'verified' AND {StarDismissedPair($"{l}.source_id", $"{l}.target_id", $"{l}.type")})";

    // Active engine edges whose endpoints are both in `visible` at the revisions the edge was derived from, the edge
    // budget counted over the rows this reader is served: with scopes off those the predicate shows it, with scopes on
    // `served`, every row Load gave it (Served); none a person or an AI client dismissed (Dismissed between two native
    // memories' stars, StarDismissed between any others, SB-P4-05). `added`, when given, receives each edge's first-seen time.
    static async Task<List<DiscoveredLink>> ReadEdges(Guid principal, NpgsqlConnection c, NpgsqlTransaction read,
        IReadOnlyDictionary<Guid, Stored> visible, IReadOnlyCollection<Guid> served, CancellationToken ct, Dictionary<string, DateTimeOffset>? added = null)
    {
        var edges = new List<DiscoveredLink>();
        await using var cmd = Database.Command(c, read, $"""
            SELECT l.source_id,l.target_id,l.details,l.created_at FROM relationships l
            JOIN resources s ON s.id=l.source_id JOIN resources t ON t.id=l.target_id
            WHERE l.engine=@engine AND l.lifecycle='active'
              AND CASE WHEN {Database.ScopesOff} THEN {Database.Visible("s")} AND {Database.Visible("t")}
                ELSE l.source_id=ANY(@served) AND l.target_id=ANY(@served) END
              AND NOT {Dismissed()} AND NOT {StarDismissed()}
            ORDER BY l.source_id,l.target_id,l.type LIMIT {RelationshipDiscovery.MaximumLinks + 1}
            """, ("engine", Owner), ("principal", principal), ("served", served.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!visible.TryGetValue(reader.GetGuid(0), out var from) || !visible.TryGetValue(reader.GetGuid(1), out var to)) continue;
            var link = JsonSerializer.Deserialize<DiscoveredLink>(reader.GetString(2))!;
            if (link.SourceRevision != from.Record.Revision || link.TargetRevision != to.Record.Revision) continue;
            edges.Add(link);
            if (added is not null) added[link.Id] = reader.GetFieldValue<DateTimeOffset>(3);
        }
        if (edges.Count > RelationshipDiscovery.MaximumLinks) throw new InvalidOperationException("Graph edge budget exceeded");
        return edges;
    }

    // Person- and AI-made links (SB-P4-02, made_links, D-067) beside the map's own, read only with MadeLinks (memory.tools
    // on): each active one between two native
    // memories whose stars this reader is served (`visible`: a memory's star only while the reader reads the memory's own
    // row and it is not held, deprecated or past its end, MemoryStars.Served), so each is served only to a reader of both
    // ends. The rebuild never writes or retires one, so it is on the map from the read after it is made and through every
    // rebuild. Newest first, within what the map's own links leave of the edge budget. Its evidence names it (its locator
    // `made-link:` and its id) and holds no words: a link carries none (052). `added`, when given, receives when each was
    // made. With no made link, none.
    static async Task<List<DiscoveredLink>> ReadMade(NpgsqlConnection c, NpgsqlTransaction read, IReadOnlyDictionary<Guid, Stored> visible,
        int taken, CancellationToken ct, Dictionary<string, DateTimeOffset>? added = null)
    {
        var stars = new Dictionary<Guid, DiscoveryRecord>();
        foreach (var s in visible.Values)
            if (s.Record.Key.Provider == MemoryStars.Provider && s.Record.Key.Account == MemoryStars.Account
                && Guid.TryParse(s.Record.Key.ExternalId, out var memory))
                stars.TryAdd(memory, s.Record);
        var room = RelationshipDiscovery.MaximumLinks - taken;
        if (stars.Count < 2 || room <= 0) return [];
        await using var cmd = Database.Command(c, read, """
            SELECT l.link_id,l.source_id,l.target_id,l.type,l.verification,l.created_at,l.confidence FROM made_links l
            WHERE l.lifecycle='active' AND l.source_id=ANY(@memories) AND l.target_id=ANY(@memories)
            ORDER BY l.created_at DESC,l.link_id LIMIT @room
            """, ("memories", stars.Keys.ToArray()), ("room", room));
        var links = new List<DiscoveredLink>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (id, from, to, type) = (reader.GetGuid(0), stars[reader.GetGuid(1)], stars[reader.GetGuid(2)], reader.GetString(3));
            var made = reader.GetFieldValue<DateTimeOffset>(5);
            var link = new DiscoveredLink(from.Key, to.Key, type, type, reader.GetString(4), "made",
                [new LinkEvidence(from.Key, $"made-link:{id:D}", "", made)], null, null, null,
                reader.IsDBNull(6) ? null : reader.GetDouble(6), LinkTypes.Directed(type, true)) { Made = id };
            links.Add(link);
            if (added is not null) added[link.Id] = made;
        }
        return links;
    }

    // The readable snapshot in one repeatable-read transaction: records a refresh confirmed within `freshness`,
    // from available providers, and the active links among them. A record re-attributed between passes (e.g.
    // moved into a shared workspace) can briefly have two fresh rows behind one viewer identity; only the most
    // recent observation is kept: of those this reader is served with scopes off, of all of them with scopes on
    // (Served), so a reader who can't read the newer row is not served the older one instead.
    async Task<(Dictionary<Guid, Stored> Fresh, List<DiscoveredLink> Links)> Readable(Guid principal, TimeSpan freshness,
        IReadOnlySet<string> unavailableProviders, CancellationToken ct, Dictionary<string, DateTimeOffset>? added = null)
    {
        var cutoff = DateTimeOffset.UtcNow - freshness;
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var read = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, read, "SET TRANSACTION READ ONLY", ct);
        var served = await Load(principal, c, read, false, ct, serving: true, replaced: Replaced, work: Work);
        var fresh = served
            .Where(s => s.Record.RetrievedAt >= cutoff && !unavailableProviders.Contains(s.Record.Key.Provider))
            .GroupBy(s => s.Record.Key.GraphId).Select(g => g.MaxBy(s => s.Record.RetrievedAt)!)
            .ToDictionary(s => s.Id);
        var links = await ReadEdges(principal, c, read, fresh, [.. served.Select(s => s.Id)], ct, added);
        if (MadeLinks) links.AddRange(await ReadMade(c, read, fresh, links.Count, ct, added));
        await read.CommitAsync(ct);
        return (fresh, links);
    }

    // The whole readable snapshot with each link's first-seen time, for bounded views built elsewhere.
    public async Task<SnapshotView> View(Guid principal, TimeSpan freshness, IReadOnlySet<string> unavailableProviders, CancellationToken ct = default)
    {
        if (freshness <= TimeSpan.Zero) throw new ArgumentException("Invalid snapshot bounds");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(limits.Operation);
        using var readBudget = DatabaseBudget(operation.Token);
        var added = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var (fresh, links) = await Readable(principal, freshness, unavailableProviders, readBudget.Token, added);
        return new(fresh.Values.Select(s => s.Record).ToArray(), links, added);
    }

    // Scheduled-refresh snapshot read: no provider calls. A record is served only while a refresh
    // observed it within `freshness`, so a record a source stops listing, revokes or fails to return
    // disappears once that window lapses. Grants, principal state and the host's configuration and
    // local credential checks (`unavailableProviders`) still apply on every read; the page holds the providers the
    // install's map `shows`. With the page come the records it shows, so opening a memory reads its text without
    // loading the snapshot again.
    public async Task<(GraphPage Page, IReadOnlyList<DiscoveryRecord> Shown)> ReadSnapshot(Guid principal, TimeSpan freshness, int nodeLimit,
        IReadOnlySet<string> unavailableProviders, Func<string, bool> shows, CancellationToken ct = default,
        IReadOnlyList<string>? knownNodeIds = null, IReadOnlyList<string>? knownEdgeIds = null, GraphQuery? query = null)
    {
        KnownBudget(knownNodeIds, knownEdgeIds);
        query?.Validate();
        if (nodeLimit is < 1 or > 1500 || freshness <= TimeSpan.Zero) throw new ArgumentException("Invalid snapshot bounds");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(limits.Operation);
        using var readBudget = DatabaseBudget(operation.Token);
        var (fresh, links) = await Readable(principal, freshness, unavailableProviders, readBudget.Token);
        // Memories and the records they cite first, then other documents, folders, and media/other records;
        // within each, most connected then most recent. Edges only among shown nodes, verified ones first.
        var degree = links.SelectMany(l => new[] { l.Source, l.Target }).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        // Memories and the records they cite first: a verified link from a memory marks its target as cited. A memory a
        // newer one replaced (SB-P4-11, served only with memory.tools on) comes after every current memory and what they
        // cite, so it never takes their place on a bounded page, and what it cites is not cited for it.
        var memories = fresh.Values.Where(s => s.Record.Kind is "memory" or "note" && s.Record.ReplacedBy is null).Select(s => s.Record.Key).ToHashSet();
        var cited = links.Where(l => memories.Contains(l.Source) && l.Verification == "verified").Select(l => l.Target).ToHashSet();
        int Tier(DiscoveryRecord r) => r.ReplacedBy is not null ? 1
            : r.Kind is "memory" or "note" || cited.Contains(r.Key) ? 0 : r.Kind == "document" ? 2 : r.Kind == "theme" ? 3 : 4;
        var ranked = fresh.Values.Select(s => s.Record)
            .OrderBy(Tier)
            .ThenByDescending(r => degree.GetValueOrDefault(r.Key))
            .ThenByDescending(r => r.CreatedAt ?? r.ModifiedAt)
            .ThenBy(r => r.Key.GraphId, StringComparer.Ordinal).ToArray();
        // Operations run over every readable record, so search and neighbours are not limited to one page.
        query ??= new();
        var center = query.Operation == "neighbors" ? ranked.FirstOrDefault(r => r.Key.GraphId == query.Value) : null;
        var around = center is null ? [] : links.Where(l => l.Source == center.Key || l.Target == center.Key)
            .Select(l => l.Source == center.Key ? l.Target : l.Source).ToHashSet();
        IReadOnlyList<DiscoveryRecord> selected = query.Operation switch
        {
            "search" => ranked.Where(r => r.Title.Contains(query.Value!, StringComparison.OrdinalIgnoreCase)
                || r.Overview?.Contains(query.Value!, StringComparison.OrdinalIgnoreCase) == true).ToArray(),
            "get" => ranked.Where(r => r.Key.GraphId == query.Value).ToArray(),
            "neighbors" => center is null ? [] : [center, .. ranked.Where(r => around.Contains(r.Key) && r.Key != center.Key)],
            "project" => Project(ranked, links, query.Value!),
            _ => ranked
        };
        var offset = query.Offset;
        var shown = selected.Skip(offset).Take(nodeLimit).ToArray();
        var keys = shown.Select(r => r.Key).ToHashSet();
        var edges = links.Where(l => keys.Contains(l.Source) && keys.Contains(l.Target))
            .OrderBy(l => l.Verification == "verified" ? 0 : l.Verification == "inferred" ? 1 : 2)
            .ThenByDescending(l => degree[l.Source] + degree[l.Target]).ThenBy(l => l.Id, StringComparer.Ordinal)
            .Take(nodeLimit * 2).ToArray();
        var scope = query.Operation == "overview" ? "readable records, memories and their sources first" : $"records matching '{query.Operation}'";
        var graph = GraphProjection.Create(shown, edges, shows, [new(CatalogStars.Provider,
            $"Scheduled-refresh snapshot, each record confirmed within the last {freshness.TotalMinutes:0} minutes. "
            + $"Showing {shown.Length} of {selected.Count} {scope} ({fresh.Count} readable in all).")]);
        return (graph with {
            nextCursor = offset + nodeLimit < selected.Count ? $"o:{offset + nodeLimit}" : null,
            removedNodeIds = knownNodeIds?.Except(graph.nodes.Select(n => n.id)).ToArray(),
            removedEdgeIds = knownEdgeIds?.Except(graph.edges.Select(e => e.id)).ToArray()
        }, shown);
    }

    // One project's members as their own view (SB-P3-07, API-24): the project's star first, then the stars in it (an
    // in_project link to it: a note in its folder, a memory in its project), then the stars they link to, each group in
    // the map's own order. Adapted from Second Brain's project graph, which seeds from the links with a member at one end
    // (src/graph/traverse.ts buildGraph). Nothing for a star that is not a project, or one the reader is not served.
    static IReadOnlyList<DiscoveryRecord> Project(IReadOnlyList<DiscoveryRecord> ranked, IReadOnlyList<DiscoveredLink> links, string project)
    {
        var center = ranked.FirstOrDefault(r => r.Key.GraphId == project && r.Kind == "project");
        if (center is null) return [];
        var members = links.Where(l => l.Target == center.Key && l.Type == "in_project").Select(l => l.Source).ToHashSet();
        var near = links.Where(l => members.Contains(l.Source) || members.Contains(l.Target))
            .SelectMany(l => new[] { l.Source, l.Target }).Where(k => k != center.Key && !members.Contains(k)).ToHashSet();
        return [center, .. ranked.Where(r => members.Contains(r.Key) && r.Key != center.Key), .. ranked.Where(r => near.Contains(r.Key))];
    }
}

// One server-side graph operation: overview (ranked subset), search (title), neighbors (one node and
// everything linked to it), project (one project's star, its members and what they link to) or get (one node), with an
// opaque offset cursor for further pages. The host serves content and data as get plus that one node's text or
// measurements.
public sealed partial record GraphQuery(string Operation = "overview", string? Value = null, string? Cursor = null)
{
    public int Offset => Cursor is null ? 0 : int.Parse(Cursor[2..]);
    public void Validate()
    {
        if (Operation is not ("overview" or "search" or "neighbors" or "project" or "get" or "content" or "data")
            || (Operation == "overview" ? Value is not null : string.IsNullOrWhiteSpace(Value) || Value.Length > (Operation == "search" ? 200 : 1000))
            || Cursor is not null && !CursorPattern().IsMatch(Cursor))
            throw new ArgumentException("Invalid graph query");
    }
    [System.Text.RegularExpressions.GeneratedRegex("^o:[0-9]{1,5}$")] private static partial System.Text.RegularExpressions.Regex CursorPattern();
}
