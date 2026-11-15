using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The opt-in recall log and implicit feedback (SB-P2-11; D-022, D-034; migration 042): with engine.recallLog on
// (RecallSettings), each recall a reader asks through the recall tool or the app's explorer search is kept in that
// reader's scope (its person's, or the household's for a login with no person) with the question, its arguments, the
// records found in order and the receipt they were answered with, at most 200 a day a scope, for 30 days; a get, append or
// update of one of those records within 30 minutes marks it followed (MemoryStore, for native memories); and a count per
// scope and record of how often it was recalled and followed, apart from the record (a read never changes a memory).
//
// Off, the default and every existing install's state, no RecallLog is made (RecallServing), so nothing is written or
// read. On, no answer changes: nothing ranks by the log or the counts (they are kept to measure real use and to grow the
// golden set, as upstream's are; ranking by them would need the gate's evidence, D-028), the receipt is what it was, and
// a failure to log is never the answer's failure. search_knowledge's combined mode and the evaluation are never logged:
// they give no receipt to trace a row by (RecallOptions.Logged).
public sealed class RecallLog(NpgsqlDataSource source)
{
    // Keeps one answered recall; whether it was kept (false over the day's cap, for a login that logs nothing, or on a
    // failure, which the answer never sees).
    public async Task<bool> Write(Guid principal, RecallQuery q, IReadOnlyList<RecallHit> hits, string receipt, CancellationToken ct)
    {
        var args = new JsonObject { ["topK"] = q.TopK, ["hops"] = q.Hops };
        foreach (var (name, value) in new[] { ("domain", q.Domain), ("provider", q.Provider), ("kind", q.Kind), ("subject", q.Subject) })
            if (value.Length > 0) args[name] = value;
        if (q.After is { } after) args["after"] = after.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (q.Before is { } before) args["before"] = before.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        // A question asked as of a moment (SB-P4-07) keeps the moment; its beliefs, listed under the results, are no result.
        if (q.AsOf is { } asOf) args["as_of"] = asOf.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        try
        {
            await using var cmd = source.CreateCommand("SELECT log_recall(@principal,@query,@params::jsonb,@returned,@direct,@receipt)");
            cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
            cmd.Parameters.Add(new("query", NpgsqlDbType.Text) { Value = q.Query });
            cmd.Parameters.Add(new("params", NpgsqlDbType.Text) { Value = args.ToJsonString() });
            cmd.Parameters.Add(new("returned", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = hits.Select(h => h.Id).ToArray() });
            cmd.Parameters.Add(new("direct", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = hits.Where(h => h.Why.Hop is null).Select(h => h.Id).ToArray() });
            cmd.Parameters.Add(new("receipt", NpgsqlDbType.Text) { Value = receipt });
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is NpgsqlException or TimeoutException or InvalidOperationException) { return false; }
    }

    // Implicit feedback: the reader went on to read or change some of what its latest recall returned. How many were
    // marked; never a reason for the read or the change to fail.
    public async Task<int> Followed(Guid principal, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        try
        {
            await using var cmd = source.CreateCommand("SELECT recall_followed(@principal,@ids)");
            cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
            cmd.Parameters.Add(new("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() });
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is NpgsqlException or TimeoutException or InvalidOperationException) { return 0; }
    }

    // The engine's daily job, whatever the setting says now, so a log turned off empties itself within 30 days: rows older
    // than 30 days and counts untouched as long, at most Batch of each a run. On an install that never turned the log on
    // both tables are empty and it deletes nothing.
    public const int Batch = 5000;
    public static async Task<(int Rows, int Counts)> Prune(NpgsqlDataSource source, CancellationToken ct, int batch = Batch)
    {
        await using var cmd = source.CreateCommand("SELECT prune_recall_log(@limit)::text");
        cmd.Parameters.AddWithValue("limit", batch);
        using var doc = System.Text.Json.JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        return (doc.RootElement.GetProperty("rows").GetInt32(), doc.RootElement.GetProperty("counts").GetInt32());
    }
}
