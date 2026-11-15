using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// The nightly link pruning pass (SB-P4-04, D-017; migration 056; docs/dev/memory.md sections 8 and 9): each scope with
// person- and AI-made links (made_links) walked in link id order from its own cursor (job_cursors, scope = the
// subject), a piece at a time, each piece and its cursor in one transaction, at most PerScope links a scope a run; a
// scope walked to its end starts again from its first link at its next run. A run stopped by its budget or a failure
// goes on from the last piece it saved. It marks out of date an active automatic link a change of an end's words or kind
// missed (056's made_link_missed: the link was moving through the trash at that moment), as the change itself marks one.
// It deletes an automatic link (one no person or AI client stated) past its grace
// period that is weak or no longer active (056's made_link_prunable), never one with an end in the trash, and never a
// link someone stated: such a one that is broken (an end in the trash, marked wrong or held, or no longer of the kinds
// its type joins; 056's made_link_broken) is kept and counted, so the night summary and the log report it. With no made
// link it reads no scope and writes nothing: no cursor, no log line.
//
// A job of the daily turn only with memory.tools on (NightlyJobs.Daily), after the staleness pass; off, as by default,
// it is no job at all.
public static class MemoryLinkPruning
{
    public const string JobId = "memory-link-pruning";
    public const int Piece = 200, PerScope = 1000;

    public static NightlyJob Job(NpgsqlDataSource source, ILogger log, int piece = Piece, int perScope = PerScope) =>
        new(JobId, "memory link pruning", NightlyJobs.DailyBudget, (context, ct) => Pass(source, context, log, piece, perScope, ct));

    // afterPiece: called with the scope once each of its pieces has committed (a test stops a run there).
    public static async Task<NightlyRun> Pass(NpgsqlDataSource source, NightlyContext context, ILogger log, int piece, int perScope, CancellationToken ct,
        Action<string>? afterPiece = null)
    {
        if (piece is < 1 or > 1000 || perScope < piece) throw new ArgumentException("a piece of 1 to 1000 links, and a run of at least one piece a scope");
        // The scopes that hold a made link: a link's two ends are in one scope (052), read from its source end.
        var subjects = new List<string>();
        await using (var cmd = source.CreateCommand("SELECT DISTINCT m.subject_id FROM made_links l JOIN memories m ON m.id=l.source_id ORDER BY 1"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) subjects.Add(r.GetString(0));
        long looked = 0, outdated = 0, pruned = 0, broken = 0, left = 0;
        var more = false;
        foreach (var subject in subjects)
        {
            // Where this scope's walk stopped last, as saved; none on its first run and once a walk reached its end.
            Guid? saved = await context.Cursor(subject, ct) is { } place && place.ValueKind == JsonValueKind.Object
                && place.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.String && Guid.TryParse(a.GetString(), out var g) ? g : null;
            var taken = 0;
            while (true)
            {
                if (taken >= perScope) { more = true; break; }
                ct.ThrowIfCancellationRequested();
                var take = Math.Min(piece, perScope - taken);
                await using var c = await source.OpenConnectionAsync(ct);
                await using var tx = await c.BeginTransactionAsync(ct);
                string json;
                await using (var cmd = new NpgsqlCommand("SELECT prune_made_links(@subject,@after,@limit)::text", c, tx))
                {
                    cmd.Parameters.Add(new("subject", NpgsqlDbType.Text) { Value = subject });
                    cmd.Parameters.Add(new("after", NpgsqlDbType.Uuid) { Value = saved is { } at ? at : DBNull.Value });
                    cmd.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = take });
                    json = (string)(await cmd.ExecuteScalarAsync(ct))!;
                }
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                var inspected = o.GetProperty("inspected").GetInt32();
                var full = o.GetProperty("more").GetBoolean();
                Guid? next = full ? o.GetProperty("last").GetGuid() : null;
                // The piece and its place commit together, with no token the budget cancels once the piece is done; a place
                // that has not changed (a scope walked whole again from its start) is not written again.
                if (next != saved)
                    await context.Save(c, tx, next is { } n ? new JsonObject { ["after"] = n.ToString() } : null, subject, CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
                saved = next;
                taken += inspected;
                looked += inspected;
                outdated += o.GetProperty("outdated").GetInt32();
                pruned += o.GetProperty("pruned").GetInt32();
                broken += o.GetProperty("broken").GetInt32();
                left += o.GetProperty("left").GetInt32();
                afterPiece?.Invoke(subject);
                if (!full) break;
            }
        }
        // Kinds and counts only, never a link's id or a memory's words.
        if (pruned + broken + outdated > 0)
            log.LogInformation("Memory links: {Checked} links checked, {Pruned} weak or out of date automatic links pruned, {Broken} person- or AI-made links kept though broken (an end in the trash, marked wrong or held, or no longer of the kinds their type joins), {Outdated} automatic links found out of date",
                looked, pruned, broken, outdated);
        return NightlyRun.Of(("links checked", looked), ("automatic links found out of date", outdated), ("automatic links pruned", pruned),
            ("person- and AI-made links broken", broken), ("left for the next run", left)) with { More = more };
    }
}
