using Npgsql;

namespace Knowledge;

// On-demand map rebuilds: the app asks for one through the bounded database function; the engine's scheduler
// claims it within a minute and runs the scheduled tick's rebuild.
public sealed class RefreshRequests(NpgsqlDataSource writer)
{
    // `{accepted, pendingSince, running, nextAllowedAt, lastFinishedAt}` as JSON.
    public async Task<string> Request(Guid principal, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT request_relationship_refresh(@p)::text");
        cmd.Parameters.AddWithValue("p", principal);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    // The engine's own request, after an import or an upload changed what the map shows: one is queued unless one is
    // already waiting to start. The three-minute limit is for people pressing refresh, not for this.
    public Task Queue(CancellationToken ct) => Queue(false, ct);

    // The engine's own request after a step an AI client may repeat as fast as it likes (a share, Mcp/MemoryTools.cs):
    // as Queue, and none either when one was asked for in the last three minutes, the three minutes people pressing
    // refresh wait (014), so a client looping over a step never keeps the map rebuilding back to back. A rebuild running
    // now does not count: one asked for more than three minutes ago may have read what the step changed before it, so the
    // next one waits behind it (Claim takes it once that one is done). What it changed is listed by the next rebuild,
    // the scheduled one at the latest.
    public Task QueueBounded(CancellationToken ct) => Queue(true, ct);

    async Task<bool> Queue(bool bounded, CancellationToken ct)
    {
        var principal = await new Principals(writer).System(Principals.GraphName, ct);
        await using var c = await writer.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Database.Execute(c, tx, "SELECT pg_advisory_xact_lock(hashtext('relationship_refresh_requests'))", ct);
        int queued;
        await using (var cmd = Database.Command(c, tx, """
            INSERT INTO relationship_refresh_requests(id,requested_by) SELECT gen_random_uuid(),@p
            WHERE NOT EXISTS(SELECT 1 FROM relationship_refresh_requests WHERE started_at IS NULL AND finished_at IS NULL)
              AND NOT (@bounded AND EXISTS(SELECT 1 FROM relationship_refresh_requests WHERE requested_at>now()-interval '3 minutes'))
            """, ("p", principal), ("bounded", bounded)))
            queued = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return queued == 1;
    }
    // The oldest waiting request, now marked running; null when none waits or one is already running.
    public async Task<Guid?> Claim(CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT claim_relationship_refresh()");
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }
    // outcome: complete, busy, not_due, disabled or failed.
    public async Task Finish(Guid request, string outcome, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT finish_relationship_refresh(@id,@outcome)");
        cmd.Parameters.AddWithValue("id", request); cmd.Parameters.AddWithValue("outcome", outcome);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
