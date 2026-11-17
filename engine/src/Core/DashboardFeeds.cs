using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;

namespace Knowledge.Relationships;

// What a dashboard's feeds hold right now, read for one reader through its own view of the snapshot and kept nowhere:
// a data star's measurement rows as stored, a star's contents (a memory's or saved record's text, or a document an
// adapter reads from its source in that source's own structure), or the stars linked to one. A star the reader can't
// see, a log of a subject the reader doesn't read, or a source that fails, gives that feed status "unavailable" with a
// reason and leaves the others as they are. Rows are read through the one read predicate (Database.Visible,
// docs/dev/memory.md section 7.3), as the reader. A memories feed names no star: it reads the native memories its reader
// reads that match its filters, through the Memories view's own predicates (MemoryStore.Matching), only with memory.tools
// on, and its work filters and fields only with memory.work on (`settings`, the install's, else the loaded ones).
public sealed class DashboardFeeds(Database db, RelationshipHost host, AdapterRegistry adapters, RhythmConfig? settings = null)
{
    public const int MaxBytes = 3_000_000, MaxRows = 10_000, MaxDayRows = 20_000, MaxText = 200_000;
    public const int PreviewRows = 200, PreviewText = 4_000, PreviewSheetRows = 30, PreviewMemories = 20;
    RhythmConfig Settings => settings ?? RhythmConfig.Current;
    // A memory's preview in a memories feed: the length the app's Memories list cuts it to.
    public const int MemoryPreview = Mcp.AppMemories.ListPreview;
    static readonly string Own = $"{Database.Visible()} AND r.relationship_snapshot IS NULL";
    // Each data star's metrics and units, listed once per revision of the star for each reader: listing them reads the
    // provider's whole history, and a rebuild gives the star a new revision whenever they change. A metric first ingested
    // since the last rebuild shows after the next one, as on the map.
    readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Reader, string Star), (string Revision, (string Metric, string Unit)[] Measured)> measuredBy =
        new();

    // The titles of the stars among `graphIds` this reader can see now.
    public async Task<IReadOnlyDictionary<string, string>> Stars(Guid principal, IEnumerable<string> graphIds, CancellationToken ct)
    {
        var records = Shown((await host.Readable(principal, ct)).View);
        return graphIds.Distinct(StringComparer.Ordinal).Where(records.ContainsKey).ToDictionary(id => id, id => records[id].Title, StringComparer.Ordinal);
    }

    // `{asOf, feeds: {name: payload}}`. A preview keeps each payload's exact shape with fewer rows and less text.
    public async Task<JsonObject> Read(Guid principal, IReadOnlyList<DashboardFeed> feeds, bool preview, CancellationToken ct)
    {
        // The map is read only for feeds that name a star: a log and a memories feed read none.
        RelationshipHostConfiguration? config = null;
        var records = new Dictionary<string, DiscoveryRecord>(StringComparer.Ordinal);
        if (feeds.Any(f => f.Kind is not ("log" or "memories")))
        {
            (var view, config) = await host.Readable(principal, ct);
            records = Shown(view);
        }
        var result = new JsonObject(); var used = 0;
        // Every feed is read at once; the size budget then applies in the dashboard's own order.
        var read = await Task.WhenAll(feeds.Select(f => f.Normalized()).Select(async feed =>
        {
            var record = feed.Kind is "log" or "memories" ? null : records.GetValueOrDefault(feed.Star);
            var payload = feed.Kind == "log" ? await LogFeed(principal, feed, ct)
                : feed.Kind == "memories" ? await MemoriesFeed(principal, feed, ct)
                : record is null ? Unavailable(feed, null, "This star isn't on the map now.")
                : await One(principal, feed, record, config!, ct);
            return (feed, record, payload: preview ? Preview(payload) : payload);
        }));
        foreach (var (feed, record, read1) in read)
        {
            var payload = read1;
            var size = Encoding.UTF8.GetByteCount(payload.ToJsonString());
            if (used + size > MaxBytes)
            {
                payload = feed.Kind == "memories" ? Memories(feed, "unavailable", "Too much data for one dashboard: ask for fewer memories (limit) or narrow the filters.")
                    : Unavailable(feed, record, "Too much data for one dashboard: ask for by day, fewer days or fewer metrics.");
                size = Encoding.UTF8.GetByteCount(payload.ToJsonString());
            }
            used += size;
            result[feed.Name] = payload;
        }
        return new JsonObject { ["asOf"] = Instant(DateTimeOffset.UtcNow), ["feeds"] = result };
    }

    Dictionary<string, DiscoveryRecord> Shown(SnapshotView view) => view.Records.Where(r => adapters.Shows(r.Key.Provider))
        .GroupBy(r => r.Key.GraphId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    async Task<JsonObject> One(Guid principal, DashboardFeed feed, DiscoveryRecord record, RelationshipHostConfiguration config, CancellationToken ct)
    {
        try
        {
            return feed.Kind switch
            {
                "data" => await Data(principal, feed, record, config, ct),
                "contents" => CatalogStars.IsData(record.Key) ? Unavailable(feed, record, "A data star's readings come through a data feed.")
                    : adapters.Documents.FirstOrDefault(a => a.Providers.ContainsKey(record.Key.Provider)) is { } source
                        ? await SourceContents(feed, record, source, ct)
                        : await Text(principal, feed, record, ct),
                _ => await Links(principal, feed, record, ct)
            };
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        { return Unavailable(feed, record, "This feed can't be read right now."); }
    }

    static JsonObject Base(DashboardFeed feed, DiscoveryRecord? record, string status) => new()
    {
        ["kind"] = feed.Kind, ["star"] = new JsonObject { ["graphId"] = feed.Star, ["title"] = record?.Title },
        ["status"] = status, ["asOf"] = Instant(DateTimeOffset.UtcNow)
    };
    static JsonObject Unavailable(DashboardFeed feed, DiscoveryRecord? record, string reason)
    {
        var payload = Base(feed, record, "unavailable"); payload["reason"] = reason; return payload;
    }
    static string Instant(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // A data star's rows: each reading as stored (by "reading"), or each day's low, average, high, total and count (by
    // "day"). A sample re-sent under the same key counts once, in its newest revision.
    async Task<JsonObject> Data(Guid principal, DashboardFeed feed, DiscoveryRecord record, RelationshipHostConfiguration config, CancellationToken ct)
    {
        var key = record.Key;
        var catalog = config.Catalog;
        if (!CatalogStars.IsData(key) || key.ExternalId.Split(':') is not [var subject, var group, ..]
            || subject != key.Subject || !catalog.Subjects.Contains(subject))
            return Unavailable(feed, record, "This star has no measurements; read it with contents or links.");
        var provider = key.Account["data:".Length..];
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
        if (record.Revision is not { } revision || !measuredBy.TryGetValue((principal, key.GraphId), out var known) || known.Revision != revision)
        {
            var found = new List<(string Metric, string Unit)>();
            await using (var cmd = Database.Command(c, tx, $"""
                SELECT DISTINCT m.metric,m.unit FROM measurements m JOIN resources r ON r.id=m.resource_id
                WHERE {Own} AND r.subject_id=@s AND r.provider=@p AND r.scope=@scope ORDER BY 1,2 LIMIT 2001
                """, ("s", subject), ("p", provider), ("scope", record.Scope), ("principal", principal)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) found.Add((reader.GetString(0), reader.GetString(1)));
            known = (record.Revision ?? "", [.. found]);
            if (record.Revision is not null) { if (measuredBy.Count >= 4000) measuredBy.Clear(); measuredBy[(principal, key.GraphId)] = known; }
        }
        var measured = known.Measured;
        var wanted = measured.Where(m => catalog.Group(provider, m.Metric).Id == group && (feed.Metrics is null || feed.Metrics.Contains(m.Metric))).ToArray();
        var today = RhythmConfig.Current.Today();
        var days = feed.Days!.Value;
        var payload = Base(feed, record, "ok");
        payload["by"] = feed.By; payload["days"] = days;
        payload["from"] = Day(today.AddDays(1 - days)); payload["to"] = Day(today);
        payload["metrics"] = new JsonArray(wanted.Select(m => (JsonNode)new JsonObject {
            ["metric"] = m.Metric, ["label"] = CatalogReader.Label(m.Metric), ["unit"] = m.Unit }).ToArray());
        var rows = new List<JsonObject>(); var records = new JsonObject();
        var reading = feed.By == "reading"; var limit = reading ? MaxRows : MaxDayRows;
        var names = wanted.Select(m => m.Metric).Distinct().ToArray();
        if (names.Length > 0)
        {
            var sql = Picked + (reading ? """
                SELECT p.record,p.kind,p.title,p.record_date,p.metric,p.unit,p.value,p.observed_at,p.local_date FROM picked p
                ORDER BY p.local_date DESC,p.observed_at DESC NULLS LAST,p.metric,p.record LIMIT @limit+1
                """ : """
                SELECT metric,unit,local_date,min(value),avg(value),max(value),sum(value),count(*) FROM picked
                GROUP BY metric,unit,local_date ORDER BY local_date DESC,metric,unit LIMIT @limit+1
                """);
            await using var cmd = Database.Command(c, tx, sql, ("s", subject), ("p", provider), ("scope", record.Scope), ("metrics", names),
                ("from", today.AddDays(1 - days)), ("limit", limit), ("principal", principal));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reading)
                {
                    var id = reader.GetGuid(0).ToString();
                    if (!records.ContainsKey(id))
                        records[id] = new JsonObject { ["kind"] = reader.GetString(1), ["title"] = reader.GetString(2),
                            ["date"] = reader.IsDBNull(3) ? null : Day(reader.GetFieldValue<DateOnly>(3)) };
                    rows.Add(new JsonObject { ["metric"] = reader.GetString(4), ["unit"] = reader.GetString(5), ["value"] = reader.GetDouble(6),
                        ["at"] = reader.IsDBNull(7) ? null : Instant(reader.GetFieldValue<DateTimeOffset>(7)),
                        ["date"] = Day(reader.GetFieldValue<DateOnly>(8)), ["record"] = id });
                }
                else rows.Add(new JsonObject { ["metric"] = reader.GetString(0), ["unit"] = reader.GetString(1),
                    ["date"] = Day(reader.GetFieldValue<DateOnly>(2)), ["min"] = reader.GetDouble(3), ["mean"] = Math.Round(reader.GetDouble(4), 4),
                    ["max"] = reader.GetDouble(5), ["sum"] = Math.Round(reader.GetDouble(6), 4), ["count"] = reader.GetInt64(7) });
            }
        }
        await tx.CommitAsync(ct);
        // The newest rows are kept when there are too many; the page receives them oldest first.
        var truncated = rows.Count > limit;
        var kept = rows.Take(limit).Reverse().ToArray();
        payload["rows"] = new JsonArray(kept.Select(r => (JsonNode)r).ToArray());
        if (reading)
        {
            var used = kept.Select(r => (string)r["record"]!).ToHashSet(StringComparer.Ordinal);
            foreach (var id in records.Select(r => r.Key).Where(id => !used.Contains(id)).ToArray()) records.Remove(id);
            payload["records"] = records;
        }
        payload["truncated"] = truncated;
        return payload;
    }
    static readonly string Picked = $"""
        WITH picked AS (
          SELECT DISTINCT ON (m.metric,m.unit,r.id,m.sample_key)
            r.id AS record,r.kind,r.title,r.local_date AS record_date,r.checksum,m.metric,m.unit,m.value,m.observed_at,m.local_date
          FROM measurements m JOIN resources r ON r.id=m.resource_id
          WHERE {Own} AND r.subject_id=@s AND r.provider=@p AND r.scope=@scope AND m.metric=ANY(@metrics) AND m.local_date>=@from
          ORDER BY m.metric,m.unit,r.id,m.sample_key,r.source_updated_at DESC NULLS LAST,r.indexed_at DESC
        )

        """;

    // A log's entries, oldest first: numbers with their unit, check-offs and notes, each with its day and when it was
    // written. Entries undone from a dashboard are gone. A log names no star, so the check every other feed makes on its
    // star is made on the log's subject: a reader who doesn't read that subject's log entries gets "unavailable", whether
    // or not the log has any.
    async Task<JsonObject> LogFeed(Guid principal, DashboardFeed feed, CancellationToken ct)
    {
        try
        {
            await using var c = await db.Source.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await Database.Execute(c, tx, "SET TRANSACTION READ ONLY", ct);
            await using (var check = Database.Command(c, tx, $"""
                SELECT {Database.Allowed("@s", "'log'", "CASE WHEN @s='household' THEN 'shared' ELSE 'private' END")}
                """, ("s", feed.Subject!), ("principal", principal)))
                if (await check.ExecuteScalarAsync(ct) is not true)
                    return new JsonObject { ["kind"] = "log", ["log"] = feed.Log, ["subject"] = feed.Subject, ["status"] = "unavailable",
                        ["asOf"] = Instant(DateTimeOffset.UtcNow), ["reason"] = "This log belongs to a scope you can't read." };
            var entries = new List<JsonObject>();
            var today = RhythmConfig.Current.Today();
            await using (var cmd = Database.Command(c, tx, $"""
                SELECT r.id,r.local_date,r.metadata->>'type',coalesce(r.metadata->>'note',''),r.indexed_at,m.metric,m.unit,m.value FROM resources r
                  LEFT JOIN measurements m ON m.resource_id=r.id AND m.sample_key='value'
                WHERE {Own} AND r.provider='log' AND r.subject_id=@s AND r.metadata->>'log'=@log AND r.local_date>=@from
                ORDER BY r.local_date DESC,r.indexed_at DESC,r.id LIMIT @limit+1
                """, ("s", feed.Subject!), ("log", feed.Log!), ("from", today.AddDays(1 - feed.Days!.Value)), ("limit", MaxRows), ("principal", principal)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                {
                    var entry = new JsonObject { ["id"] = reader.GetGuid(0).ToString(), ["date"] = Day(reader.GetFieldValue<DateOnly>(1)),
                        ["type"] = reader.IsDBNull(2) ? "note" : reader.GetString(2), ["at"] = Instant(reader.GetFieldValue<DateTimeOffset>(4)) };
                    if (!reader.IsDBNull(5))
                    {
                        if ((string?)entry["type"] == "check") entry["check"] = reader.GetString(5);
                        else { entry["metric"] = reader.GetString(5); entry["unit"] = reader.GetString(6); entry["value"] = reader.GetDouble(7); }
                    }
                    if (reader.GetString(3) is { Length: > 0 } note) entry["note"] = note;
                    entries.Add(entry);
                }
            await tx.CommitAsync(ct);
            var payload = new JsonObject { ["kind"] = "log", ["log"] = feed.Log, ["subject"] = feed.Subject, ["status"] = "ok",
                ["asOf"] = Instant(DateTimeOffset.UtcNow), ["days"] = feed.Days,
                ["from"] = Day(today.AddDays(1 - feed.Days!.Value)), ["to"] = Day(today) };
            var truncated = entries.Count > MaxRows;
            payload["entries"] = new JsonArray(entries.Take(MaxRows).Reverse().Select(e => (JsonNode)e).ToArray());
            payload["truncated"] = truncated;
            return payload;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new JsonObject { ["kind"] = "log", ["log"] = feed.Log, ["subject"] = feed.Subject, ["status"] = "unavailable",
                ["asOf"] = Instant(DateTimeOffset.UtcNow), ["reason"] = "This log can't be read right now." };
        }
    }

    // The native memories its reader reads that match a memories feed's filters (MemoryMatch, as the app's Memories view
    // lists them), newest first, never one held for review or in the trash, nor, unless its status filter names them, one
    // marked wrong: count (how many match in all), memories (at most limit) and truncated (more match than it gives).
    async Task<JsonObject> MemoriesFeed(Guid principal, DashboardFeed feed, CancellationToken ct)
    {
        if (!MemorySettings.ToolsOn(Settings)) return Memories(feed, "unavailable", "Memories are off in this install (memory.tools).");
        var match = feed.Matched();
        var work = MemorySettings.WorkOn(Settings);
        if (match.Worked && !work) return Memories(feed, "unavailable", "Work items are off in this install (memory.work).");
        try
        {
            var found = await new MemoryStore(db.Source).Matching(principal, match, feed.Limit!.Value, MemoryPreview, ct);
            if (found.UnknownProjects.Count > 0)
                return Memories(feed, "unavailable", Reason($"This feed names a project you don't read: {string.Join(", ", found.UnknownProjects)}."));
            var payload = Memories(feed, "ok", null);
            payload["count"] = found.Count;
            payload["memories"] = new JsonArray([.. found.Items.Select(m => (JsonNode)Memory(m, work))]);
            payload["truncated"] = found.Count > found.Items.Count;
            return payload;
        }
        catch (MemoryRefused e) { return Memories(feed, "unavailable", Reason(e.Message)); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        { return Memories(feed, "unavailable", "These memories can't be read right now."); }
    }
    // A memories feed's payload before its memories: its filters as it reads them and its limit.
    static JsonObject Memories(DashboardFeed feed, string status, string? reason)
    {
        var payload = new JsonObject { ["kind"] = "memories", ["status"] = status, ["asOf"] = Instant(DateTimeOffset.UtcNow),
            ["where"] = feed.Matched().ToJson(), ["limit"] = feed.Limit };
        if (reason is not null) payload["reason"] = reason;
        return payload;
    }
    static string Reason(string text) => text.Length <= 300 ? text : text[..299] + "…";

    // One memory as a memories feed gives it: its id and its star's, scope and owner, project (with its star), tags, kind,
    // status, what it is now (current, replaced, ended or retracted: MemoryRecord.State, as the Memories list says it), when
    // it was written and last changed, its due date, a preview of its words as the Memories list cuts it, and
    // for a work item (with memory.work on) its type, state, priority, assignee, claim (who holds it and until when; never
    // the claim's id), whether it is blocked and what it waits on. Never a hold: a feed gives no held memory.
    static JsonObject Memory(MemoryMatched found, bool work)
    {
        var m = found.Memory;
        return new JsonObject
        {
            ["id"] = m.Id.ToString(), ["graphId"] = MemoryStars.Key(m.Subject, m.Id).GraphId, ["scope"] = m.Scope, ["owner"] = m.Subject,
            ["project"] = m.Project is { } p ? new JsonObject { ["id"] = p.Id.ToString(), ["slug"] = p.Slug, ["name"] = p.Name,
                ["graphId"] = MemoryStars.ProjectKey(m.Subject, p.Id).GraphId } : null,
            ["tags"] = new JsonArray([.. m.Tags.Select(t => (JsonNode)t)]), ["kind"] = m.Kind, ["status"] = m.Status, ["state"] = m.State,
            ["createdAt"] = Instant(m.CreatedAt), ["updatedAt"] = Instant(m.UpdatedAt),
            ["due"] = m.Due is { } d ? new JsonObject { ["at"] = Instant(d.At), ["local"] = d.Local, ["kind"] = d.Kind } : null,
            ["preview"] = m.Content, ["truncated"] = m.Truncated,
            ["work"] = work && m.Work is { } w ? new JsonObject
            {
                ["type"] = w.Type, ["state"] = w.State, ["priority"] = w.Priority,
                ["assignee"] = w.Assignee is { } a ? new JsonObject { ["kind"] = a.Kind, ["name"] = a.Name } : null,
                ["claim"] = w.Claim is { } c ? new JsonObject { ["label"] = c.Label, ["claimedAt"] = Instant(c.ClaimedAt), ["expiresAt"] = Instant(c.ExpiresAt) } : null,
                ["blocked"] = found.Blocked,
                ["blockedBy"] = new JsonArray([.. found.BlockedBy.Select(b => (JsonNode)new JsonObject { ["id"] = b.Id.ToString(),
                    ["graphId"] = MemoryStars.Key(m.Subject, b.Id).GraphId, ["type"] = b.Type, ["state"] = b.State, ["priority"] = b.Priority })]),
            } : null,
        };
    }

    // Why feeds a harness gives can't be served in this install, or null: a memories feed with memory.tools off, or one
    // with a work filter with memory.work off. save_dashboard, dashboard_data and Studio refuse them; a saved feed read
    // after the setting changed is unavailable instead.
    public string? Unserved(IReadOnlyList<DashboardFeed> feeds)
    {
        for (var i = 0; i < feeds.Count; i++)
        {
            if (feeds[i].Kind != "memories") continue;
            if (!MemorySettings.ToolsOn(Settings)) return $"feeds[{i}]: a memories feed reads native memories, which are off in this install (memory.tools)";
            if (feeds[i].Matched().Worked && !MemorySettings.WorkOn(Settings)) return $"feeds[{i}].where: work items are off in this install (memory.work)";
        }
        return null;
    }

    // The first memories feed naming a project `principal` doesn't read, in words a save refuses it with, or null.
    public async Task<string?> UnknownProjects(Guid principal, IReadOnlyList<DashboardFeed> feeds, CancellationToken ct)
    {
        var store = new MemoryStore(db.Source);
        for (var i = 0; i < feeds.Count; i++)
        {
            if (feeds[i].Kind != "memories" || feeds[i].Matched().Projects is not { Count: > 0 } named) continue;
            if (await store.UnknownProjects(principal, named, ct) is { Count: > 0 } unknown)
            {
                var known = await store.KnownProjects(principal, 10, ct);
                return $"feeds[{i}].where.project names no project you read: {string.Join(", ", unknown)}"
                    + (known.Count > 0 ? $"; known projects: {string.Join(", ", known)}" : "");
            }
        }
        return null;
    }

    // A memory's current text (read from its source now) or a saved record's text, through the graph's content read.
    async Task<JsonObject> Text(Guid principal, DashboardFeed feed, DiscoveryRecord record, CancellationToken ct)
    {
        var page = await host.Read(principal, null, null, ct, new GraphQuery("content", feed.Star));
        if (page.nodes is not [var node] || node.resource.content is not { } text)
            return Unavailable(feed, record, record.Kind == "memory"
                ? "This memory's text is unavailable right now." : "This star has no text of its own; read it with links.");
        var payload = Base(feed, record, "ok");
        payload["format"] = "text"; payload["title"] = record.Title;
        payload["modifiedAt"] = node.resource.sourceModifiedAt is { } modified ? Instant(modified) : null;
        return Cut(payload, text, page.issues.Any(i => i.reason.StartsWith("Showing the first", StringComparison.Ordinal)));
    }
    static JsonObject Cut(JsonObject payload, string text, bool truncated = false)
    {
        payload["text"] = text.Length > MaxText ? text[..MaxText] : text;
        payload["truncated"] = truncated || text.Length > MaxText;
        return payload;
    }

    // A document an adapter reads from its source: its own format, title and text or sheets, merged into the feed's shape.
    static async Task<JsonObject> SourceContents(DashboardFeed feed, DiscoveryRecord record, IDocumentSource source, CancellationToken ct)
    {
        if (await source.Contents(record, ct) is not { } read) return Unavailable(feed, record, "This document's contents can't be read right now.");
        var payload = Base(feed, record, read["status"]?.GetValue<string>() ?? "ok");
        foreach (var (name, value) in read.ToArray())
            if (name is not ("kind" or "star" or "asOf" or "status")) payload[name] = value?.DeepClone();
        if (payload["text"] is JsonValue text && text.GetValue<string>() is { } body) return Cut(payload, body, payload["truncated"]?.GetValue<bool>() == true);
        return payload;
    }

    // The stars around one, as explore_graph shows them, and the links among them.
    async Task<JsonObject> Links(Guid principal, DashboardFeed feed, DiscoveryRecord record, CancellationToken ct)
    {
        // The star itself comes first and counts toward explore_graph's limit.
        var page = await host.Explore(principal, null, feed.Star, 1, Math.Min(feed.Limit!.Value + 1, GraphExplorer.MaxStars), ct);
        var payload = Base(feed, record, "ok");
        payload["stars"] = JsonSerializer.SerializeToNode(page.stars.Where(s => s.graphId != feed.Star).Take(feed.Limit.Value).ToArray());
        payload["links"] = JsonSerializer.SerializeToNode(page.links);
        payload["truncated"] = page.truncated;
        return payload;
    }

    // The same payload with fewer rows and less text, saying how much there is.
    static JsonObject Preview(JsonObject full)
    {
        var payload = (JsonObject)full.DeepClone();
        if (payload["rows"] is JsonArray rows && rows.Count > PreviewRows)
        {
            payload["previewOf"] = rows.Count;
            var kept = rows.Skip(rows.Count - PreviewRows).Select(r => r!.DeepClone()).ToArray();
            payload["rows"] = new JsonArray(kept);
            if (payload["records"] is JsonObject records)
            {
                var used = kept.Select(r => (string?)r!["record"]).ToHashSet(StringComparer.Ordinal);
                foreach (var id in records.Select(r => r.Key).Where(id => !used.Contains(id)).ToArray()) records.Remove(id);
            }
        }
        // A memories feed's newest memories, the first it gives.
        if (payload["memories"] is JsonArray memories && memories.Count > PreviewMemories)
        {
            payload["previewOf"] = memories.Count;
            payload["memories"] = new JsonArray([.. memories.Take(PreviewMemories).Select(m => m!.DeepClone())]);
        }
        if (payload["text"] is JsonValue value && value.GetValue<string>() is { Length: > PreviewText } text)
        { payload["previewOf"] = text.Length; payload["text"] = text[..PreviewText]; }
        if (payload["sheets"] is JsonArray sheets)
            foreach (var sheet in sheets.OfType<JsonObject>())
                if (sheet["rows"] is JsonArray tab && tab.Count > PreviewSheetRows)
                { sheet["previewOf"] = tab.Count; sheet["rows"] = new JsonArray(tab.Take(PreviewSheetRows).Select(r => r!.DeepClone()).ToArray()); }
        return payload;
    }
}
