namespace Knowledge;

// What waits on a person, queue by queue (SB-P6-01; docs/dev/memory.md section 5): how many memories are held for review, flagged
// as possible copies, queued as possible contradictions, may be out of date, how many pairs wait as insight candidates and how many
// insights are proposed. Each is the length of that queue's own list for this caller, read through the read that serves the list
// (a page of one, or its own count from the same predicates), so no count here has a definition of its own: every count is the
// length of its list. Only the app's Memories view and the terminal's `rhythm queues` read it, with memory.tools on.
public sealed record MemoryQueueCounts(int Held, int Duplicates, int Contradictions, int Stale, int Candidates, int Insights);

public sealed partial class MemoryStore
{
    public async Task<MemoryQueueCounts> QueueCounts(Guid principal, string? scope, InsightStore insights, MemoryCall call, CancellationToken ct) => new(
        await ReviewCount(principal, new MemoryReviewFilter(scope), ct),
        (await Duplicates(principal, scope, 1, null, 0, ct)).Total,
        (await Contradictions(principal, scope, 1, null, 0, call, ct)).Total,
        (await Stale(principal, scope, 1, null, 0, ct)).Total,
        await InsightCount(principal, scope, ct),
        (await insights.Stream(principal, InsightStore.Proposed, scope, 1, null, ct)).Proposed);
}

public sealed partial class InsightStore
{
    // Which of these insights this login may confirm or dismiss (066's insight_may_review, the rule review_insight applies): what a
    // person's screen offers. The review itself still asks, under its lock.
    public async Task<IReadOnlySet<Guid>> MayReview(Guid principal, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new HashSet<Guid>();
        await using var cmd = writer.CreateCommand("SELECT i.id FROM insights i WHERE i.id=ANY(@ids) AND insight_may_review(@p,i.subject_id,i.saved_by)");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        var may = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) may.Add(r.GetGuid(0));
        return may;
    }
}
