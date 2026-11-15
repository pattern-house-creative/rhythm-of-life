using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Truth over time for native memories (SB-P4-06 and SB-P4-08; story 6; docs/dev/memory.md section 5): when a memory was
// true, one memory replacing another with an explicit confirmation, and the retraction cascade. Writes go through
// migration 049's checked functions (set_memory_validity, supersede_memory, clear_memory_flags), which decide who may act
// (033's author lock) under the recorded memory.scopes mode locked FOR SHARE; the states a reader is told come from the
// same migration's one definition (memory_validity_state, memory_replaced_by), read with every memory (MemoryStore.Get and
// List). The cascade runs in the database, from the audit row of the change that retracts a memory or brings it back
// (set_status, undo, forget, restore), and the change's answer says what it did (Cascade).
//
// Only the memory tools call it (update's validity, supersede; SB-P3-09), made only with memory.tools on, off by default.
// With it off no install holds a window, a supersede or a flag, and every existing answer is as it was.

// When a memory was true, as a change names it: each end given (set, or null to clear: since it was written; still true)
// or left as it is.
public sealed record MemoryWindow(bool FromGiven, DateTimeOffset? From, bool UntilGiven, DateTimeOffset? Until)
{
    public bool Any => FromGiven || UntilGiven;
}

// What a change's cascade did (SB-P4-08): the memories it had replaced that are current again (Reopened) or replaced by
// it again (Reclosed), and those built on it that were flagged for a check (Flagged) or whose flag came off (Unflagged).
// Each is a memory of its own scope, so its readers read them all.
public sealed record MemoryCascade(IReadOnlyList<Guid> Reopened, IReadOnlyList<Guid> Reclosed, IReadOnlyList<Guid> Flagged,
    IReadOnlyList<Guid> Unflagged);

// What a supersede answers. Action: plan (nothing written yet: Confirmation applies exactly this plan), superseded
// (Replaces is history now, true until At, replaced by Id) or unchanged (Reason: replaced, Id replaces it already; ended,
// it had stopped being true at At, before Id began; enclosed, Id is told as over already, until Until, within its time).
// Revision, Status, Subject and Scope are the replaced memory's.
public sealed record MemorySuperseded(string Action, Guid Id, Guid Replaces, DateTimeOffset? At, DateTimeOffset? Until, string? Reason,
    string? Confirmation, int Revision, string Subject, string Scope, string Status, string Scopes);

public sealed partial class MemoryStore
{
    public const int CascadeMax = 25, PropagateMax = 10;

    // When it was true, alone: a change of window writes no other field (a new fact is a memory of its own, then a
    // supersede). Expected and the key as for any change.
    public Task<MemoryWrite> SetValidity(Guid principal, Guid memory, MemoryWindow window, int? expected, MemoryCall call, CancellationToken ct)
    {
        if (!window.Any) throw new MemoryRefused("invalid", "A change of when a memory was true names valid_from, valid_until or both");
        var change = new System.Text.Json.Nodes.JsonObject();
        if (window.FromGiven) change["valid_from"] = window.From?.ToUniversalTime().ToString("O");
        if (window.UntilGiven) change["valid_until"] = window.Until?.ToUniversalTime().ToString("O");
        return Write("SELECT set_memory_validity(@principal,@id,@change::jsonb,@expected,@channel,@client,@key)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("change", NpgsqlDbType.Jsonb, change.ToJsonString()), ("expected", NpgsqlDbType.Integer, expected));
    }

    // Memory replaces an earlier fact of its scope. Without a confirmation the plan, writing nothing; with the plan's own,
    // applied. No idempotency key: a retry with the confirmation answers unchanged (replaced) once it has applied.
    public async Task<MemorySuperseded> Supersede(Guid principal, Guid memory, Guid replaces, string? confirmation, MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A supersede takes no idempotency key: its confirmation makes a retry safe");
        var code = string.IsNullOrWhiteSpace(confirmation) ? null : confirmation.Trim().ToLowerInvariant();
        await using var c = await source.OpenConnectionAsync(ct);
        // A contradiction queued between them is a person's to settle, and one settled is a person's to change (068): an AI client
        // is told so before any plan, and the database's guard refuses the change itself all the same.
        if (call.Channel == "mcp")
        {
            await using var queued = Database.Command(c, null, "SELECT memory_contradiction_between(@principal,@id,@replaces)",
                ("principal", principal), ("id", memory), ("replaces", replaces));
            if (await queued.ExecuteScalarAsync(ct) is true)
                throw new MemoryRefused("needs_person", "These two are queued for a person's review as a possible contradiction, or a person has settled them, so a person decides whether one replaces the other, from the app or the terminal: tell the person and leave them as they are");
        }
        await using var tx = await c.BeginTransactionAsync(ct);
        string json;
        await using (var cmd = new NpgsqlCommand("SELECT supersede_memory(@principal,@id,@replaces,@code,@channel,@client)::text", c, tx))
        {
            cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
            cmd.Parameters.Add(new("id", NpgsqlDbType.Uuid) { Value = memory });
            cmd.Parameters.Add(new("replaces", NpgsqlDbType.Uuid) { Value = replaces });
            cmd.Parameters.Add(new("code", NpgsqlDbType.Text) { Value = (object?)code ?? DBNull.Value });
            cmd.Parameters.Add(new("channel", NpgsqlDbType.Text) { Value = call.Channel });
            cmd.Parameters.Add(new("client", NpgsqlDbType.Text) { Value = (object?)AdminAudit.Client(call.Client) ?? DBNull.Value });
            try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        }
        await tx.CommitAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        DateTimeOffset? At(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;
        string? Word(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new(o.GetProperty("action").GetString()!, o.GetProperty("id").GetGuid(), o.GetProperty("replaces").GetGuid(), At("at"), At("until"),
            Word("reason"), Word("confirmation"), o.GetProperty("revision").GetInt32(), o.GetProperty("subject").GetString()!,
            o.GetProperty("scope").GetString()!, o.GetProperty("status").GetString()!, o.GetProperty("scopes").GetString()!);
    }

    // A memory's retraction flags taken off once it has been checked and still stands (upstream's keep), by whoever may
    // change it. One with none answers unchanged. No idempotency key: clearing again answers unchanged.
    public Task<MemoryWrite> ClearFlags(Guid principal, Guid memory, int? expected, MemoryCall call, CancellationToken ct) =>
        call.Key is not null
            ? throw new MemoryRefused("invalid", "Clearing flags takes no idempotency key: clearing again answers unchanged")
            : Write("SELECT clear_memory_flags(@principal,@id,@expected,@channel,@client)::text", principal, call, [], ct,
                ("id", NpgsqlDbType.Uuid, memory), ("expected", NpgsqlDbType.Integer, expected));

    // What the cascade of this transaction's change to a memory did, as migration 049 notes it (rhythm.memory_cascade);
    // null when it did nothing.
    internal static async Task<MemoryCascade?> Cascaded(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT nullif(current_setting('rhythm.memory_cascade',true),'')::jsonb->>@id", c, tx);
        cmd.Parameters.Add(new("id", NpgsqlDbType.Text) { Value = id.ToString() });
        if (await cmd.ExecuteScalarAsync(ct) is not string json) return null;
        using var doc = JsonDocument.Parse(json);
        IReadOnlyList<Guid> Ids(string name) => doc.RootElement.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(x => Guid.Parse(x.GetString()!))] : [];
        return new(Ids("reopened"), Ids("reclosed"), Ids("flagged"), Ids("unflagged"));
    }
}
