using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The trash for native memories (docs/dev/memory.md section 5; SB-P3-06, D-019, D-036, D-065): forget a memory into the
// trash with its links, list what the caller may restore, restore it with its links, and delete it forever from the app or
// the terminal with its trash row's nonce and a confirmation, never through an AI client's channel. Writes go through
// migration 038's checked functions, which decide who may act (the author lock), under the recorded memory.scopes mode
// locked FOR SHARE; the list reads through the same rule. The engine's daily job (Scheduler, MemoryTrash.Purge) deletes
// for good what has stayed in the trash past its retention, a bounded number a run.
//
// The forget tool (SB-P3-09), the app's Memories view (SB-P3-12, its trash, restore and delete for good) and the
// terminal's `rhythm delete-forever` (SB-P3-22, a person's own terminal login) call it, only with memory.tools on. With it
// off no install holds a trash row, and every existing answer is as it was.

// A trash row as a write answers it: the nonce that names this trip into the trash (a restore or a delete forever names
// it, so a stale view never acts on a later trip), why (forget), when it was trashed and when the purge deletes it for
// good, and how many person- or AI-made links it keeps to restore.
public sealed record MemoryTrashRow(Guid Nonce, string Reason, DateTimeOffset TrashedAt, DateTimeOffset PurgeAfter, int Links);

// What a forget answers: the memory as a write answers it (its revision unchanged: the trash changes no state) and its
// trash row. Replayed: the answer to an earlier request with the same idempotency key. Cascade: what retracting it did
// to the memories it had replaced and those built on it (migration 049); null when it did nothing.
public sealed record MemoryTrashed(Guid Id, int Revision, string Subject, string Scope, string Status, string Scopes,
    MemoryTrashRow Trash, bool Replayed, MemoryCascade? Cascade = null);

// What a restore answers: the memory back at the revision it had; Links, the links that came back; LinksGone, those that
// did not: whose other end was deleted for good meanwhile, or (062) an automatic link whose other end moved to another
// scope meanwhile; when and why it had been trashed. Hold: the hold it came back with (a held
// memory is restored held still), read through the one read predicate after the restore; null when it is not held.
// Cascade: what bringing it back did to the memories it had replaced and those built on it (migration 049), or null.
public sealed record MemoryRestored(Guid Id, int Revision, string Subject, string Scope, string Status, string Scopes, int Links,
    int LinksGone, DateTimeOffset TrashedAt, string Reason, bool Replayed, MemoryHold? Hold = null, MemoryCascade? Cascade = null);

// What a delete forever answers: the memory's id and owner, and how many links and earlier states went with it. Its audit
// trail stays (none of its words).
public sealed record MemoryDeleted(Guid Id, string Subject, int Links, int Versions, string Scopes);

// One memory in the trash, as a reader who may restore it is served it: who owns and wrote it, a preview of its text (at
// most the asked characters, Truncated when longer; none for a held memory, D-039), why and when it was trashed and by
// whom (the person its login is bound to, or none), when the purge deletes it and the days left, its trash row's nonce,
// and whether this reader may also delete it forever (memory.md section 5).
public sealed record MemoryTrashItem(Guid Id, string Subject, string Scope, string? Author, string Source, string Status,
    string? Preview, bool Truncated, string? Hold, string Reason, string Channel, string? TrashedBy, DateTimeOffset TrashedAt,
    DateTimeOffset PurgeAfter, int DaysLeft, Guid Nonce, bool MayDeleteForever, int Revision);

public sealed record MemoryTrashCursor(DateTimeOffset TrashedAt, Guid Id);

public sealed record MemoryTrashPage(IReadOnlyList<MemoryTrashItem> Items, MemoryTrashCursor? Next);

public sealed partial class MemoryStore
{
    // Into the trash (any channel: nothing is deleted). Expected: the revision the caller read, refused (conflict) when the
    // memory has moved on; null forgets it as it stands. One the caller does not read, one in the trash already and an
    // unknown id answer alike (unknown).
    public async Task<MemoryTrashed> Forget(Guid principal, Guid memory, int? expected, MemoryCall call, CancellationToken ct)
    {
        var (doc, cascade) = await Trashing("SELECT forget_memory(@principal,@id,@expected,@channel,@client,@key)::text", principal, call, ct,
            ("id", NpgsqlDbType.Uuid, memory), ("expected", NpgsqlDbType.Integer, expected));
        using var held = doc;
        var o = doc.RootElement;
        var t = o.GetProperty("trash");
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("revision").GetInt32(), o.GetProperty("subject").GetString()!,
            o.GetProperty("scope").GetString()!, o.GetProperty("status").GetString()!, o.GetProperty("scopes").GetString()!,
            new(t.GetProperty("nonce").GetGuid(), t.GetProperty("reason").GetString()!, t.GetProperty("trashed_at").GetDateTimeOffset(),
                t.GetProperty("purge_after").GetDateTimeOffset(), t.GetProperty("links").GetInt32()), Flag(o, "replayed"), cascade);
    }

    // Back from the trash with its links. Nonce: the trash row the caller saw (from Forget or Trash), so a memory trashed
    // again since is not restored from a stale view; null restores whatever trip it is on. A memory not in the trash, a
    // nonce that is not its trash row's, one the caller does not read and an unknown id answer alike (unknown).
    public async Task<MemoryRestored> Restore(Guid principal, Guid memory, Guid? nonce, MemoryCall call, CancellationToken ct)
    {
        var (doc, cascade) = await Trashing("SELECT restore_memory(@principal,@id,@nonce,@channel,@client,@key)::text", principal, call, ct,
            ("id", NpgsqlDbType.Uuid, memory), ("nonce", NpgsqlDbType.Uuid, nonce));
        using var held = doc;
        var o = doc.RootElement;
        var r = o.GetProperty("restored");
        var id = o.GetProperty("id").GetGuid();
        return new(id, o.GetProperty("revision").GetInt32(), o.GetProperty("subject").GetString()!,
            o.GetProperty("scope").GetString()!, o.GetProperty("status").GetString()!, o.GetProperty("scopes").GetString()!,
            r.GetProperty("links").GetInt32(), r.GetProperty("links_gone").GetInt32(), r.GetProperty("trashed_at").GetDateTimeOffset(),
            r.GetProperty("reason").GetString()!, Flag(o, "replayed"), await HeldNow(principal, id, ct), cascade);
    }

    // The hold a memory has now, as its reader may know it: through the one read predicate, so nothing is said of a memory
    // the caller does not read; null when it is not held. A restore screens nothing (the words are those it had), so no
    // signals are named.
    async Task<MemoryHold?> HeldNow(Guid principal, Guid id, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT m.hold,m.hold_score FROM memories m
            JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
            WHERE m.id=@id AND m.hold IS NOT NULL AND {Database.Visible("r")}
            """, ("principal", principal), ("id", id));
        await using var row = await cmd.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new(row.GetString(0), Math.Round(row.IsDBNull(1) ? 0 : row.GetFloat(1), 2), [], false) : null;
    }

    // For good, from the app (channel app) or the terminal (cli: the terminal's own login, bound to a person, migration
    // 044, or the operator on the host, principal null), never an AI client's channel (mcp: forbidden): only a memory
    // in the trash, named with its trash row's nonce, and with confirm equal to the memory's id, the confirmation the
    // app or the terminal asked its person for. No idempotency key: deleting again answers unknown.
    public async Task<MemoryDeleted> DeleteForever(Guid? principal, Guid memory, Guid nonce, string confirm, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A delete forever takes no idempotency key: deleting again answers unknown");
        var (doc, _) = await Trashing("SELECT delete_memory_forever(@principal,@id,@nonce,@confirm,@channel,@client)::text", principal, call, ct,
            ("id", NpgsqlDbType.Uuid, memory), ("nonce", NpgsqlDbType.Uuid, nonce), ("confirm", NpgsqlDbType.Text, confirm.Trim()));
        using var held = doc;
        var o = doc.RootElement;
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("subject").GetString()!, o.GetProperty("links").GetInt32(),
            o.GetProperty("versions").GetInt32(), o.GetProperty("scopes").GetString()!);
    }

    public const int TrashMax = 50;

    // The trash as a reader is served it: only what that reader may restore (memory.md section 5, the same rule as the
    // restore: with scopes off what any admitted harness or app login may change, everything; with them on its own
    // private memories, the Family ones it wrote or, as an admin, any Family one, and those no person wrote), newest
    // trashed first, at most limit (1 to 50), continued from a page's Next. MayDeleteForever is for the caller's channel:
    // never through an AI client's (D-036), and on the terminal only for the terminal's own login, bound to a person
    // (migration 044), as delete_memory_forever and memory_channel refuse the rest.
    public async Task<MemoryTrashPage> Trash(Guid principal, int limit, MemoryTrashCursor? after, int previewChars, MemoryCall call, CancellationToken ct)
    {
        if (limit is < 1 or > TrashMax) throw new MemoryRefused("invalid", $"limit is 1 to {TrashMax}");
        var args = new List<(string, object?)> { ("limit", limit + 1) };
        var page = "";
        if (after is not null)
        {
            page = "AND (t.trashed_at,m.id)<(@afterAt,@afterId)";
            args.AddRange([("afterAt", after.TrashedAt.ToUniversalTime()), ("afterId", after.Id)]);
        }
        var items = await Trashed(principal, page, args, previewChars, call, ct);
        var next = items.Count > limit ? new MemoryTrashCursor(items[limit - 1].TrashedAt, items[limit - 1].Id) : null;
        return new(items.Take(limit).ToList(), next);
    }

    // One memory in the trash as Trash lists it to this reader, by its id, or null when it is not in the trash or not this
    // reader's to restore (an unknown id, one of another scope and one not in the trash answer alike): what the terminal
    // shows its person before deleting it for good (SB-P3-22).
    public async Task<MemoryTrashItem?> TrashEntry(Guid principal, Guid memory, int previewChars, MemoryCall call, CancellationToken ct) =>
        (await Trashed(principal, "AND m.id=@memory", [("limit", 1), ("memory", memory)], previewChars, call, ct)).SingleOrDefault();

    async Task<List<MemoryTrashItem>> Trashed(Guid principal, string page, List<(string, object?)> args, int previewChars, MemoryCall call, CancellationToken ct)
    {
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        await using var c = await source.OpenConnectionAsync(ct);
        args.AddRange([("principal", principal), ("chars", previewChars), ("channel", call.Channel)]);
        await using var cmd = Database.Command(c, null, $"""
            SELECT m.id,m.subject_id,m.scope,m.author_id,m.source,m.status,
              CASE WHEN m.hold IS NULL THEN left(m.content,@chars) END,m.hold IS NULL AND length(m.content)>@chars,m.hold,
              t.reason,t.channel,q.person_id,t.trashed_at,t.purge_after,
              greatest(0,ceil(extract(epoch FROM t.purge_after-now())/86400))::int,t.nonce,
              memory_may_delete(@principal,m.subject_id,m.author_id,scopes_on()) AND EXISTS(SELECT 1 FROM principals c WHERE c.id=@principal
                AND (c.role='app' AND @channel='app' OR c.role='harness' AND c.terminal AND @channel='cli' AND c.person_id IS NOT NULL)),m.revision
            FROM memories m JOIN memory_trash t ON t.memory_id=m.id
            JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            LEFT JOIN principals q ON q.id=t.trashed_by
            WHERE memory_may_change(@principal,m.subject_id,m.author_id,scopes_on()) {page}
            ORDER BY t.trashed_at DESC,m.id DESC LIMIT @limit
            """, [.. args]);
        var items = new List<MemoryTrashItem>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), Text(3), r.GetString(4), r.GetString(5), Text(6), r.GetBoolean(7),
                    Text(8), r.GetString(9), r.GetString(10), Text(11), r.GetFieldValue<DateTimeOffset>(12), r.GetFieldValue<DateTimeOffset>(13),
                    r.GetInt32(14), r.GetGuid(15), r.GetBoolean(16), r.GetInt32(17)));
            }
        return items;
    }

    // The trash's write and, in its own transaction, what its cascade did (a trip into the trash retracts a memory and a
    // restore brings it back, migration 049).
    async Task<(JsonDocument Doc, MemoryCascade? Cascade)> Trashing(string sql, Guid? principal, MemoryCall call, CancellationToken ct,
        params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        JsonDocument doc;
        await using (var cmd = new NpgsqlCommand(sql, c, tx))
        {
            (string Name, NpgsqlDbType Type, object? Value)[] all =
                [("principal", NpgsqlDbType.Uuid, principal), .. args, ("channel", NpgsqlDbType.Text, call.Channel),
                 ("client", NpgsqlDbType.Text, AdminAudit.Client(call.Client)), ("key", NpgsqlDbType.Text, call.Key)];
            foreach (var (name, type, value) in all.Where(a => sql.Contains("@" + a.Name, StringComparison.Ordinal)))
                cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
            try { doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!); }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        }
        var cascade = doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? await Cascaded(c, tx, id.GetGuid(), ct) : null;
        await tx.CommitAsync(ct);
        return (doc, cascade);
    }

    static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

// What one run of the trash's daily job did: memories deleted for good past their retention, those whose deletion failed
// (left in the trash, tried again next run), those a writer held at that moment (left for the next run), and idempotency
// keys older than 30 days pruned. More: the run stopped at its own bound (every batch full, or KeyBatch keys) and may
// have more left for the next run.
public sealed record MemoryPurge(int Purged, int Failed, int Skipped, int Keys, bool More = false)
{
    public bool Worth => Purged + Failed + Skipped + Keys > 0;
}

public static class MemoryTrash
{
    // The bound on one run (D-017): at most Batches batches of Batch memories, each batch its own transaction, so a large
    // trash empties over several days rather than in one long transaction; and at most KeyBatch keys.
    public const int Batch = 100, Batches = 10, KeyBatch = 5000;

    // The engine's daily job: deletes for good each memory whose trash retention has passed (migration 038's
    // purge_memory_trash, the job memory-trash on the audit trail), and prunes old idempotency keys. Nothing else: no row
    // an install had before native memories is touched (MemoryTrashPurgeTests). A run stops at the first batch that took
    // fewer than it could, so an empty trash costs one query.
    public static async Task<MemoryPurge> Purge(NpgsqlDataSource source, CancellationToken ct, int batch = Batch, int batches = Batches)
    {
        int purged = 0, failed = 0, skipped = 0;
        var bound = true;
        for (var i = 0; i < batches; i++)
        {
            await using var cmd = source.CreateCommand("SELECT purge_memory_trash(@limit)::text");
            cmd.Parameters.AddWithValue("limit", batch);
            using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
            var o = doc.RootElement;
            int Get(string name) => o.GetProperty(name).GetInt32();
            purged += Get("purged"); failed += Get("failed"); skipped += Get("skipped");
            if (Get("purged") + Get("failed") + Get("skipped") < batch || Get("purged") == 0) { bound = false; break; }
        }
        await using var keys = source.CreateCommand("SELECT prune_memory_requests(@limit)");
        keys.Parameters.AddWithValue("limit", KeyBatch);
        var pruned = (int)(await keys.ExecuteScalarAsync(ct))!;
        return new(purged, failed, skipped, pruned, bound || pruned >= KeyBatch);
    }
}
