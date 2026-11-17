using System.Text.Json;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Links a person or an AI client makes between two native memories (SB-P4-02; docs/dev/memory.md sections 5 and 8;
// D-067, D-191, D-031, D-152): link, unlink and a memory's connections. Writes go through migration 052's checked
// functions (link_memories, unlink_memories), which decide who may under the recorded memory.scopes mode locked FOR
// SHARE: both ends read by the caller and in one scope, the source end the caller's to change (033's author lock), no
// held memory, the episodic rule (051); removing one is its maker's, or whoever may change its source end; an automatic
// link (a suggestion: one the map's rebuild found, or a made link marked suggested, which goes) is dismissed, a row of
// memory_link_dismissals kept so that it is never suggested again. The links live in made_links, beside the map's own: the
// rebuild never touches one, and with memory.tools on the map's reads serve each to a reader of both ends
// (RelationshipEngine.Readable, MadeLinks) and combined retrieval's hops follow them, each end through the reader's
// predicate, a held memory never (Recall.Expand, RecallOptions.MadeLinks).
//
// Only the memory tools call it (link, unlink, connections; Mcp/MemoryTools.cs), made only with memory.tools on, off by
// default: with it off no install holds a link these make, and every existing answer is as it was.

// A link as made or found: its id, the two memories as named (Source, Target) and as stored (From, To: relates_to has no
// direction and is stored with the smaller id first), its type and verification (inferred for an AI client's, verified
// for a person's, D-015), whether this call made it or had nothing to do, and the source end's scope.
public sealed record MemoryLinked(Guid Id, Guid Source, Guid Target, Guid From, Guid To, string Type, bool Directed, string Verification,
    bool Created, bool Unchanged, string Subject, string Scope, string Scopes);

public sealed record MemoryLinkRef(Guid Id, string Type);

// What an unlink did: the made links it removed, those it removed and kept as dismissals (an automatic one), and the
// types of the map's suggestions between the two it dismissed; Already, how many of those had been dismissed before;
// Unchanged when it did nothing.
public sealed record MemoryUnlinked(Guid Source, Guid Target, IReadOnlyList<MemoryLinkRef> Removed, IReadOnlyList<MemoryLinkRef> Kept,
    IReadOnlyList<string> Dismissed, int Already, bool Unchanged, string Subject, string Scope, string Scopes);

// A neighbour of a memory, as its reader is served it: a held one without its words (D-160), with its hold.
public sealed record MemoryNeighbour(Guid Id, string Subject, string Scope, string Status, string State, string? Kind, string? Preview,
    bool Truncated, string? Hold);

// One connection: a made link (LinkId, Made) or the map's suggestion between the two memories' stars (no LinkId); its type,
// its direction from the memory asked about (outgoing, incoming; for a link with no direction, none), its verification,
// whether the caller's own login or person made it (Mine), whether an import brought it (Imported: a made link with no
// maker here, from a Second Brain export or another install's Rhythm export, its verification as the file gave it; a
// person's confirmation raises that and leaves it imported, with no maker, 057) and whether the caller may unlink it (a made link: its maker,
// or whoever may change its source end, either end for relates_to; never a supersede's, which unlink leaves; a
// suggestion: whoever may change either memory, which dismisses it); when it was made (or first found) and its confidence
// (a suggestion's strength).
public sealed record MemoryConnection(Guid? LinkId, bool Made, string Type, string Direction, string Verification, bool Mine, bool Imported,
    bool MayUnlink, DateTimeOffset At, double? Confidence, MemoryNeighbour Memory);

public sealed record MemoryConnections(Guid Id, IReadOnlyList<MemoryConnection> Items, bool Truncated, string Scopes);

public sealed partial class MemoryStore
{
    public const int ConnectionsDefault = 20, ConnectionsMax = 50, NeighbourPreview = 120;

    // Source links to Target. Type: one of Second Brain's types but supersedes (LinkTypes, upstream), or with `work`
    // (memory.work on, migration 080) depends_on, from a work item to one it waits on, both work items, closing no loop.
    // Confidence: 0 to 1, or none. No idempotency key: linking again answers unchanged.
    public async Task<MemoryLinked> Link(Guid principal, Guid source, Guid target, string type, double? confidence, MemoryCall call, CancellationToken ct,
        bool work = false)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A link takes no idempotency key: linking again answers unchanged");
        if (confidence is { } c && (double.IsNaN(c) || c is < 0 or > 1)) throw new MemoryRefused("invalid", "A confidence is a number from 0 to 1");
        var named = (type ?? "").Trim().ToLowerInvariant();
        if (named == MemoryWork.DependsOn && !work)
            throw new MemoryRefused("off", "depends_on joins two work items, and work items are off in this install (memory.work)");
        using var o = await Call("SELECT link_memories(@principal,@source,@target,@type,@confidence,@channel,@client,@work)::text", principal, call, ct,
            ("source", NpgsqlDbType.Uuid, source), ("target", NpgsqlDbType.Uuid, target), ("type", NpgsqlDbType.Text, named),
            ("confidence", NpgsqlDbType.Double, confidence), ("work", NpgsqlDbType.Boolean, work));
        var r = o.RootElement;
        return new(r.GetProperty("id").GetGuid(), r.GetProperty("source").GetGuid(), r.GetProperty("target").GetGuid(), r.GetProperty("from").GetGuid(),
            r.GetProperty("to").GetGuid(), r.GetProperty("type").GetString()!, r.GetProperty("directed").GetBoolean(), r.GetProperty("verification").GetString()!,
            r.GetProperty("created").GetBoolean(), r.GetProperty("unchanged").GetBoolean(), r.GetProperty("subject").GetString()!,
            r.GetProperty("scope").GetString()!, r.GetProperty("scopes").GetString()!);
    }

    // The links between Source and Target, either way, of Type or of every type but supersedes when it is null (a
    // supersede's link is undone with undo or a retraction, never unlinked; naming it is refused). No idempotency key:
    // unlinking again answers unchanged.
    public async Task<MemoryUnlinked> Unlink(Guid principal, Guid source, Guid target, string? type, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "An unlink takes no idempotency key: unlinking again answers unchanged");
        var named = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();
        using var o = await Call("SELECT unlink_memories(@principal,@source,@target,@type,@channel,@client)::text", principal, call, ct,
            ("source", NpgsqlDbType.Uuid, source), ("target", NpgsqlDbType.Uuid, target), ("type", NpgsqlDbType.Text, named));
        var r = o.RootElement;
        static IReadOnlyList<MemoryLinkRef> Refs(JsonElement list) =>
            [.. list.EnumerateArray().Select(x => new MemoryLinkRef(x.GetProperty("id").GetGuid(), x.GetProperty("type").GetString()!))];
        return new(r.GetProperty("source").GetGuid(), r.GetProperty("target").GetGuid(), Refs(r.GetProperty("removed")), Refs(r.GetProperty("kept")),
            [.. r.GetProperty("dismissed").EnumerateArray().Select(x => x.GetString()!)], r.GetProperty("already").GetInt32(),
            r.GetProperty("unchanged").GetBoolean(), r.GetProperty("subject").GetString()!, r.GetProperty("scope").GetString()!,
            r.GetProperty("scopes").GetString()!);
    }

    async Task<JsonDocument> Call(string sql, Guid principal, MemoryCall call, CancellationToken ct, params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string json;
        await using (var cmd = new NpgsqlCommand(sql, c, tx))
        {
            (string Name, NpgsqlDbType Type, object? Value)[] all =
                [("principal", NpgsqlDbType.Uuid, principal), .. args, ("channel", NpgsqlDbType.Text, call.Channel),
                 ("client", NpgsqlDbType.Text, AdminAudit.Client(call.Client))];
            foreach (var (name, kind, value) in all) cmd.Parameters.Add(new NpgsqlParameter(name, kind) { Value = value ?? DBNull.Value });
            try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        }
        await tx.CommitAsync(ct);
        return JsonDocument.Parse(json);
    }

    // The scope mode as the checked functions read it (lock_memory_scopes): anything but off is on.
    const string Scoped = "coalesce(NOT " + Database.ScopesOff + ",true)";

    // A memory's connections, one step away: its made links, newest first, then the map's suggestions between its star and
    // another memory's (a similarity the rebuild found, at the stars' current revisions, none dismissed), strongest first;
    // of Type only, when given; at most Limit (1 to 50), Truncated when there were more. The memory and every neighbour
    // are read through the one predicate on their own rows (Database.Visible), so a neighbour in a scope the caller does
    // not read, one in the trash and one that never was are all absent; a held neighbour is served without its words.
    // Null when the caller does not read the memory itself.
    public async Task<MemoryConnections?> Connections(Guid principal, Guid id, string? type, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > ConnectionsMax) throw new MemoryRefused("invalid", $"limit is 1 to {ConnectionsMax}");
        var named = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();
        if (named is not null && !System.Text.RegularExpressions.Regex.IsMatch(named, "^[a-z][a-z0-9_-]{0,63}$"))
            throw new MemoryRefused("invalid", "A link's type is a lower-case word such as relates_to");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        string scopes;
        await using (var self = Database.Command(c, tx, $"""
            SELECT CASE WHEN {Scoped} THEN 'on' ELSE 'off' END FROM memories m
            JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            WHERE m.id=@id AND {Database.Visible("r")}
            """, ("principal", principal), ("id", id)))
            if (await self.ExecuteScalarAsync(ct) is string mode) scopes = mode;
            else return null;
        // A neighbour's columns, n its memory and r its own resources row.
        const string Neighbour = """
            n.id AS n_id,n.subject_id AS n_subject,n.scope AS n_scope,n.status AS n_status,memory_validity_state(n.id) AS n_state,n.kind AS n_kind,
            CASE WHEN n.hold IS NULL THEN left(n.content,@chars) END AS n_preview,n.hold IS NULL AND length(n.content)>@chars AS n_truncated,n.hold AS n_hold
            """;
        const string Own = "JOIN resources r ON r.id=n.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=n.id::text";
        var items = new List<MemoryConnection>();
        await using (var made = Database.Command(c, tx, $"""
            SELECT l.link_id,l.type,l.source_id=@id AS outgoing,l.verification,l.created_at,l.confidence,
              coalesce(l.made_by=@principal OR (SELECT a.person_id FROM principals a WHERE a.id=l.made_by)=(SELECT p.person_id FROM principals p WHERE p.id=@principal),false) AS mine,
              l.type<>'supersedes' AND memory_link_may_remove(@principal,l,{Scoped}) AS may,l.imported,{Neighbour}
            FROM made_links l JOIN memories n ON n.id=CASE WHEN l.source_id=@id THEN l.target_id ELSE l.source_id END {Own}
            WHERE l.lifecycle='active' AND (l.source_id=@id OR l.target_id=@id) AND (@type::text IS NULL OR l.type=@type::text) AND {Database.Visible("r")}
            ORDER BY l.created_at DESC,l.link_id LIMIT @limit
            """, ("principal", principal), ("id", id), ("type", named), ("chars", NeighbourPreview), ("limit", limit + 1)))
        await using (var reader = await made.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                items.Add(new(reader.GetGuid(0), true, reader.GetString(1), Direction(reader.GetString(1), reader.GetBoolean(2)), reader.GetString(3),
                    reader.GetBoolean(6), reader.GetBoolean(8), reader.GetBoolean(7), reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5), Neighbour9(reader)));
        if (items.Count <= limit)
        {
            // The map's suggestions between this memory's star and another memory's, as the map serves them: active, of its
            // own engine, not verified (a similarity), derived at both stars' current revisions, none dismissed
            // (RelationshipEngine.DismissedPair, unlink_memories). One per neighbour and type.
            await using var suggested = Database.Command(c, tx, $"""
                SELECT DISTINCT ON (n.id,l.type) l.type,a.id=star.id AS outgoing,l.verification,l.created_at,
                  (l.details->>'Strength')::double precision AS strength,
                  memory_may_change(@principal,self.subject_id,self.author_id,{Scoped}) OR memory_may_change(@principal,n.subject_id,n.author_id,{Scoped}) AS may,
                  {Neighbour}
                FROM memories self
                JOIN resources star ON star.provider='memory' AND star.relationship_snapshot IS NOT NULL AND star.external_id={MemoryStars.StarExternalId("self.id")}
                JOIN relationships l ON (l.source_id=star.id OR l.target_id=star.id) AND l.engine=@engine AND l.lifecycle='active' AND l.verification<>'verified'
                JOIN resources a ON a.id=l.source_id JOIN resources b ON b.id=l.target_id
                JOIN resources o ON o.id=CASE WHEN l.source_id=star.id THEN l.target_id ELSE l.source_id END
                  AND o.provider='memory' AND o.relationship_snapshot IS NOT NULL
                JOIN memories n ON n.id={MemoryStars.Answering("o")} {Own}
                WHERE self.id=@id AND n.id<>self.id AND (@type::text IS NULL OR l.type=@type::text)
                  AND l.details->>'SourceRevision' IS NOT DISTINCT FROM a.revision AND l.details->>'TargetRevision' IS NOT DISTINCT FROM b.revision
                  AND NOT {RelationshipEngine.DismissedPair("self.id", "n.id", "l.type")}
                  AND {Database.Visible("r")}
                ORDER BY n.id,l.type,(l.details->>'Strength')::double precision DESC NULLS LAST
                """, ("principal", principal), ("id", id), ("type", named), ("chars", NeighbourPreview), ("engine", RelationshipEngine.Owner));
            var found = new List<MemoryConnection>();
            await using var reader = await suggested.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                found.Add(new(null, false, reader.GetString(0), Direction(reader.GetString(0), reader.GetBoolean(1)), reader.GetString(2), false, false,
                    reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), Neighbour6(reader)));
            items.AddRange(found.OrderByDescending(f => f.Confidence ?? 0).ThenBy(f => f.Memory.Id).ThenBy(f => f.Type, StringComparer.Ordinal));
        }
        await tx.CommitAsync(ct);
        return new(id, [.. items.Take(limit)], items.Count > limit, scopes);
    }

    static string Direction(string type, bool outgoing) => !LinkTypes.Directed(type, true) ? "none" : outgoing ? "outgoing" : "incoming";
    static MemoryNeighbour Neighbour9(NpgsqlDataReader r) => Neighbour(r, 9);
    static MemoryNeighbour Neighbour6(NpgsqlDataReader r) => Neighbour(r, 6);
    static MemoryNeighbour Neighbour(NpgsqlDataReader r, int at)
    {
        string? Text(int i) => r.IsDBNull(at + i) ? null : r.GetString(at + i);
        return new(r.GetGuid(at), r.GetString(at + 1), r.GetString(at + 2), r.GetString(at + 3), r.GetString(at + 4), Text(5), Text(6),
            r.GetBoolean(at + 7), Text(8));
    }
}
