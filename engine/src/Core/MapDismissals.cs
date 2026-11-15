using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Kept dismissals (SB-P4-05; connection idea 1; D-194; docs/dev/memory.md sections 5 and 8; migration 057): the owner's
// judgement on the map's suggestions, saved and honoured on every read and every rebuild. Discover's Dismiss names a
// suggestion by its id on the reader's own map; the engine finds it there, so no one dismisses a link they are not
// served, and keeps it as a dismissal: between two native memories' stars a row of memory_link_dismissals (052, where an
// AI client's unlink keeps one too), between any other two stars a row of map_link_dismissals (057). A link a person or
// an AI client made (made_links) is no suggestion the map found: Dismiss on one is a person's unlink of that one type
// (052), which removes it, a suggested one kept as a dismissal. A person sees the dismissals between stars on their map,
// newest first, whoever made them (an AI client's unlink included), and takes one back. Every step goes through a
// person's channel (the app's login, Mcp/AppMemories.cs), which 057 checks: an AI client's channel is refused.
//
// Only app_memories calls it, made only with memory.tools on (off by default): with it off no dismissal is written here,
// and with none written every map read answers as before.

// A dismissal as a person is shown it: its id (MapDismissals.Id: memories or stars, the two ids smaller first and the
// type), the two stars on the reader's map with their titles, the link type, when, whether a person's channel made it
// (the app's login or the terminal's) or an AI client's, whether this caller's own login or person made it, and whether
// this caller may take it back.
public sealed record MapDismissal(string Id, string Kind, string Type, (string Id, string Title) First, (string Id, string Title) Second,
    DateTimeOffset At, bool ByPerson, bool Mine, bool MayRestore);

// What a Dismiss did: the suggestion's id as the map named it; the dismissal now kept (null when a link someone stated
// was removed and nothing is kept); whether a made link was removed; unchanged when it was dismissed already.
public sealed record MapDismissed(string Link, string? Dismissal, bool Removed, bool Unchanged, string Scopes);

// What taking one back did: unchanged when it was not there; Back, whether the map finds a suggestion of its type between
// the two stars now, so it is on the map again from the next read. A dismissal kept when a person removed a suggested
// link a person or an AI client had not stated (a made link marked suggested, 052) has none to bring back: the link was
// removed, and taking the dismissal back only lets one of its type be suggested again.
public sealed record MapRestored(string Dismissal, bool Unchanged, string Scopes, bool Back);

// One stored dismissal before the reader's view decides it: its kind, ids, type, time, who, and each end's star key.
public sealed record MapDismissalRow(string Kind, Guid Low, Guid High, string Type, DateTimeOffset At, bool ByPerson, bool Mine, bool MayRestore,
    ResourceKey LowStar, ResourceKey HighStar);

public static partial class MapDismissals
{
    public const int Default = 50, Max = 50;
    // Read before the reader's view decides, so dismissals of other scopes never crowd a reader's own out of its page.
    const int Candidates = 1000;

    [GeneratedRegex("^(memories|stars):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}):([a-z][a-z0-9_-]{0,63})$")]
    private static partial Regex IdPattern();
    // A suggestion's id on the map (DiscoveredLink.Id): the map's own, or a made link's.
    [GeneratedRegex("^(relationship:[0-9a-f]{64}|made-link:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex LinkPattern();

    public static bool IsId(string? id) => id is not null && IdPattern().IsMatch(id);
    public static bool IsLink(string? link) => link is not null && LinkPattern().IsMatch(link);
    public static string Id(string kind, Guid low, Guid high, string type) => $"{kind}:{low:D}:{high:D}:{type}";

    // Discover's Dismiss of the suggestion `link` on this reader's map.
    public static async Task<MapDismissed> Dismiss(RelationshipHost host, MemoryStore store, Guid principal, string link, MemoryCall call,
        CancellationToken ct)
    {
        if (!IsLink(link)) throw new MemoryRefused("invalid", "A suggestion is named by its id on the map");
        var view = await host.ShownView(principal, ct);
        var found = view.Links.FirstOrDefault(l => l.Id == link)
            ?? throw new MemoryRefused("unknown", "No such suggestion on your map: it may have gone since, or be one you cannot read.");
        if (found.Verification == "verified")
            throw new MemoryRefused("invalid", "A confirmed link is not a suggestion, so it is not dismissed.");
        if (found.Made is not null)
        {
            // A link a person or an AI client made: a person's unlink of its one type. A stated one is removed and nothing is
            // kept; one marked suggested is removed and kept as a dismissal of its type (052).
            var (from, to) = (Guid.Parse(found.Source.ExternalId), Guid.Parse(found.Target.ExternalId));
            var unlinked = await store.Unlink(principal, from, to, found.Type, call, ct);
            var kept = unlinked.Kept.Count > 0 || unlinked.Dismissed.Count > 0 || unlinked.Already > 0;
            var (low, high) = Order(from, to);
            return new(link, kept ? Id("memories", low, high, found.Type) : null, unlinked.Removed.Count + unlinked.Kept.Count > 0,
                unlinked.Unchanged, unlinked.Scopes);
        }
        var a = await store.StarRow(found.Source, ct);
        var b = await store.StarRow(found.Target, ct);
        if (a is null || b is null)
            throw new MemoryRefused("unknown", "No such suggestion on your map: it may have gone since, or be one you cannot read.");
        var done = await store.DismissSuggestion(principal, a.Value, b.Value, found.Type, call, ct);
        return new(link, Id(done.Kind, done.Low, done.High, found.Type), false, done.Unchanged, done.Scopes);
    }

    // The dismissals between two stars on this reader's map, newest first, at most `limit`; Truncated when there were more.
    public static async Task<(IReadOnlyList<MapDismissal> Items, bool Truncated)> List(RelationshipHost host, MemoryStore store, Guid principal,
        int limit, CancellationToken ct)
    {
        if (limit is < 1 or > Max) throw new MemoryRefused("invalid", $"limit is 1 to {Max}");
        var view = await host.ShownView(principal, ct);
        var stars = new Dictionary<string, DiscoveryRecord>(StringComparer.Ordinal);
        foreach (var r in view.Records) stars.TryAdd(r.Key.GraphId, r);
        var items = new List<MapDismissal>();
        foreach (var row in await store.DismissalRows(principal, Candidates, ct))
        {
            if (!stars.TryGetValue(row.LowStar.GraphId, out var first) || !stars.TryGetValue(row.HighStar.GraphId, out var second)) continue;
            items.Add(new(Id(row.Kind, row.Low, row.High, row.Type), row.Kind, row.Type, (first.Key.GraphId, first.Title),
                (second.Key.GraphId, second.Title), row.At, row.ByPerson, row.Mine, row.MayRestore));
            if (items.Count > limit) break;
        }
        return ([.. items.Take(limit)], items.Count > limit);
    }

    // A dismissal taken back, named by its id as the list gave it: only one between two stars on this reader's map.
    public static async Task<MapRestored> Restore(RelationshipHost host, MemoryStore store, Guid principal, string dismissal, MemoryCall call,
        CancellationToken ct)
    {
        var m = IdPattern().Match(dismissal ?? "");
        if (!m.Success) throw new MemoryRefused("invalid", "A dismissal is named by its id as the list of dismissals gives it");
        var (kind, low, high, type) = (m.Groups[1].Value, Guid.Parse(m.Groups[2].Value), Guid.Parse(m.Groups[3].Value), m.Groups[4].Value);
        if (Order(low, high) != (low, high)) throw new MemoryRefused("invalid", "A dismissal names its two ids smaller first");
        var view = await host.ShownView(principal, ct);
        var shown = view.Records.Select(r => r.Key.GraphId).ToHashSet(StringComparer.Ordinal);
        bool OnMap(ResourceKey? key) => key is not null && shown.Contains(key.GraphId);
        var (a, b) = kind == "memories"
            ? (MemoryStars.Key("", low), MemoryStars.Key("", high))
            : (await store.StarKey(low, ct), await store.StarKey(high, ct));
        if (!OnMap(a) || !OnMap(b))
            throw new MemoryRefused("unknown", "No such dismissal on your map: one of its stars may have gone, or be one you cannot read.");
        var done = await store.RestoreSuggestion(principal, kind, low, high, type, call, ct);
        return new(dismissal!, done.Unchanged, done.Scopes, await store.Suggested(kind, low, high, type, ct));
    }

    // Two uuids as the database orders them (their text), smaller first.
    static (Guid, Guid) Order(Guid a, Guid b) => string.CompareOrdinal(a.ToString("D"), b.ToString("D")) <= 0 ? (a, b) : (b, a);
}

public sealed record SuggestionJudged(string Kind, Guid Low, Guid High, bool Unchanged, string Scopes);

public sealed partial class MemoryStore
{
    // The map's suggestion of `type` between the star rows a and b, dismissed by a person (057's dismiss_suggestion).
    internal async Task<SuggestionJudged> DismissSuggestion(Guid principal, Guid a, Guid b, string type, MemoryCall call, CancellationToken ct)
    {
        using var o = await Judge("SELECT dismiss_suggestion(@principal,@a,@b,@type,@channel,@client)::text", principal, call,
            "No such suggestion on your map: it may have gone since, or be one you cannot read.", ct,
            ("a", NpgsqlDbType.Uuid, a), ("b", NpgsqlDbType.Uuid, b), ("type", NpgsqlDbType.Text, type));
        var r = o.RootElement;
        return new(r.GetProperty("kind").GetString()!, r.GetProperty("low").GetGuid(), r.GetProperty("high").GetGuid(),
            r.GetProperty("unchanged").GetBoolean(), r.GetProperty("scopes").GetString()!);
    }

    // A dismissal taken back by a person (057's restore_suggestion).
    internal async Task<SuggestionJudged> RestoreSuggestion(Guid principal, string kind, Guid low, Guid high, string type, MemoryCall call,
        CancellationToken ct)
    {
        using var o = await Judge("SELECT restore_suggestion(@principal,@kind,@low,@high,@type,@channel,@client)::text", principal, call,
            "No such dismissal on your map: one of its stars may have gone, or be one you cannot read.", ct,
            ("kind", NpgsqlDbType.Text, kind), ("low", NpgsqlDbType.Uuid, low), ("high", NpgsqlDbType.Uuid, high), ("type", NpgsqlDbType.Text, type));
        var r = o.RootElement;
        return new(r.GetProperty("kind").GetString()!, r.GetProperty("low").GetGuid(), r.GetProperty("high").GetGuid(),
            r.GetProperty("unchanged").GetBoolean(), r.GetProperty("scopes").GetString()!);
    }

    // Whether the map holds an active suggestion of `type` (not a confirmed link) between the dismissal's two stars: two
    // memories' star rows (kind memories) or the two star rows themselves (kind stars).
    internal async Task<bool> Suggested(string kind, Guid low, Guid high, string type, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"""
            WITH a AS (SELECT r.id FROM resources r WHERE CASE WHEN @kind='memories'
                THEN r.provider='{MemoryStars.Provider}' AND r.relationship_snapshot IS NOT NULL AND r.external_id={MemoryStars.StarExternalId("@low::uuid")}
                ELSE r.id=@low END),
              b AS (SELECT r.id FROM resources r WHERE CASE WHEN @kind='memories'
                THEN r.provider='{MemoryStars.Provider}' AND r.relationship_snapshot IS NOT NULL AND r.external_id={MemoryStars.StarExternalId("@high::uuid")}
                ELSE r.id=@high END)
            SELECT EXISTS(SELECT 1 FROM relationships l WHERE l.engine='{RelationshipEngine.Owner}' AND l.lifecycle='active'
              AND l.verification<>'verified' AND l.type=@type
              AND (l.source_id IN (SELECT id FROM a) AND l.target_id IN (SELECT id FROM b) OR l.source_id IN (SELECT id FROM b) AND l.target_id IN (SELECT id FROM a)))
            """);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("low", low);
        cmd.Parameters.AddWithValue("high", high);
        cmd.Parameters.AddWithValue("type", type);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // As Call (MemoryLinks.cs), with an unknown answered in the step's own words rather than as an unknown memory.
    async Task<JsonDocument> Judge(string sql, Guid principal, MemoryCall call, string unknown, CancellationToken ct,
        params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        try { return await Call(sql, principal, call, ct, args); }
        catch (MemoryRefused refused) when (refused.Code == "unknown") { throw new MemoryRefused("unknown", unknown); }
    }

    // The star row that holds `key` on the map (RelationshipEngine.Save: one per subject, provider, account and source id).
    internal async Task<Guid?> StarRow(ResourceKey key, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            SELECT id FROM resources WHERE relationship_snapshot IS NOT NULL AND subject_id=@s AND provider=@p
              AND relationship_snapshot->'Key'->>'Account'=@a AND relationship_snapshot->'Key'->>'ExternalId'=@e
            """);
        cmd.Parameters.AddWithValue("s", key.Subject);
        cmd.Parameters.AddWithValue("p", key.Provider);
        cmd.Parameters.AddWithValue("a", key.Account);
        cmd.Parameters.AddWithValue("e", key.ExternalId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    // The star a star row holds, or null for a row that is no star.
    internal async Task<ResourceKey?> StarKey(Guid star, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            SELECT subject_id,provider,relationship_snapshot->'Key'->>'Account',relationship_snapshot->'Key'->>'ExternalId'
            FROM resources WHERE id=@id AND relationship_snapshot IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("id", star);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && !reader.IsDBNull(2) && !reader.IsDBNull(3)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
    }

    // The stored dismissals whose two ends this caller reads (two memories' subjects; two star rows as the map serves them,
    // 057's map_star_readable, a source share included), newest first, at most `limit`, each with who
    // made it, whether this caller may take it back (057's rule: whoever may dismiss it), and each end's star key; the
    // reader's view of the map then decides which are shown (MapDismissals.List).
    internal async Task<IReadOnlyList<MapDismissalRow>> DismissalRows(Guid principal, int limit, CancellationToken ct)
    {
        const string Who = """
            coalesce(p.role='app' OR p.terminal,false) AS person,
            coalesce(d.dismissed_by=@principal OR p.person_id=(SELECT c.person_id FROM principals c WHERE c.id=@principal),false) AS mine
            """;
        await using var cmd = source.CreateCommand($"""
            SELECT * FROM (
              SELECT 'memories' AS kind,d.low_id,d.high_id,d.type,d.dismissed_at,{Who},
                memory_may_change(@principal,a.subject_id,a.author_id,{Scoped}) OR memory_may_change(@principal,b.subject_id,b.author_id,{Scoped}) AS may,
                a.subject_id AS a_subject,'{MemoryStars.Provider}' AS a_provider,'{MemoryStars.Account}' AS a_account,a.id::text AS a_external,
                b.subject_id AS b_subject,'{MemoryStars.Provider}' AS b_provider,'{MemoryStars.Account}' AS b_account,b.id::text AS b_external
              FROM memory_link_dismissals d JOIN memories a ON a.id=d.low_id JOIN memories b ON b.id=d.high_id JOIN principals p ON p.id=d.dismissed_by
              WHERE reads_subject(@principal,a.subject_id) AND reads_subject(@principal,b.subject_id)
              UNION ALL
              SELECT 'stars',d.low_id,d.high_id,d.type,d.dismissed_at,{Who},
                map_star_may_judge(@principal,d.low_id,{Scoped}) OR map_star_may_judge(@principal,d.high_id,{Scoped}),
                a.subject_id,a.provider,a.relationship_snapshot->'Key'->>'Account',a.relationship_snapshot->'Key'->>'ExternalId',
                b.subject_id,b.provider,b.relationship_snapshot->'Key'->>'Account',b.relationship_snapshot->'Key'->>'ExternalId'
              FROM map_link_dismissals d JOIN resources a ON a.id=d.low_id JOIN resources b ON b.id=d.high_id JOIN principals p ON p.id=d.dismissed_by
              WHERE a.relationship_snapshot IS NOT NULL AND b.relationship_snapshot IS NOT NULL
                AND map_star_readable(@principal,d.low_id) AND map_star_readable(@principal,d.high_id)) x
            ORDER BY x.dismissed_at DESC,x.kind,x.low_id,x.high_id,x.type LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("principal", principal);
        cmd.Parameters.AddWithValue("limit", limit);
        var rows = new List<MapDismissalRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Enumerable.Range(8, 8).Any(reader.IsDBNull)) continue;
            rows.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7),
                new(reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)),
                new(reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15))));
        }
        return rows;
    }
}
