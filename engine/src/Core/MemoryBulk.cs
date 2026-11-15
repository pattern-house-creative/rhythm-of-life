namespace Knowledge;

// Several memories at once, and what a person's list of AI changes reads beside them (SB-P6-01; docs/dev/memory.md section
// 5): a person forgets, brings back, deletes for good or shares up to 50 memories from the app's Memories view, each at the
// revision or the trash row they saw, as ReleaseAll releases several (MemoryReview.cs). Each is its own write through the one
// checked writer it always was (Forget, Restore, DeleteForever, Share), so the author lock, the one scope and the holds decide
// each memory as they decide it alone, and one refused leaves the others done. Through an AI client's channel nothing is read
// or written: no MCP tool calls these, and the store refuses that channel itself; through the app's or the terminal's the
// database's own memory_channel refuses an AI client's login (044). Only app_memories calls them, only with memory.tools on,
// so with it off every existing answer is as it was.

// One memory of a forget of several: forgotten, or why not (unknown, forbidden, conflict). Revision: the one it is at, when
// the caller reads it. Nonce and Days: its trash row, which brings it back, and how long the trash keeps it. Cascade: what
// retracting it did to the memories it had replaced and those built on it, or null.
public sealed record MemoryForgetResult(Guid Id, string Result, int? Revision, Guid? Nonce = null, int? Days = null, MemoryCascade? Cascade = null);

// One memory of a restore of several: restored, or why not (unknown, forbidden, conflict). Hold: the hold it came back with.
public sealed record MemoryRestoreResult(Guid Id, string Result, int? Revision, string? Hold = null, MemoryCascade? Cascade = null);

// One memory of a delete of several: deleted, or why not (unknown, forbidden).
public sealed record MemoryDeleteResult(Guid Id, string Result);

// One memory of a share of several: moved, unchanged (it is there already), or why not (unknown, forbidden, conflict, held,
// no_author, scopes_off, or invalid: 048 refused it with no code of its own, as a take-back that would join two people's
// private records). ProjectLeft: its project could not come with it.
public sealed record MemoryShareResult(Guid Id, string Result, int? Revision, bool ProjectLeft = false);

// What a person reads of a memory that a list of changes names: a preview of its words, none for a held memory (D-039).
public sealed record MemoryPreview(Guid Id, string? Content, bool Truncated, string? Hold);

// One item of a review queue's step taken for several at once (SB-P6-01): resolved, unchanged (it was not waiting, so nothing was
// written: resolved already, in this word or another), or why not (unknown, forbidden, conflict, held, retracted, invalid).
// Revision: a memory's, from still_true or a conflict. Review and View: an insight's review now and the view that puts it in.
// Candidate: a pair's decision now. Nothing of anyone's words.
public sealed record MemoryResolveResult(Guid Id, string Result, int? Revision = null, string? Review = null, string? View = null, string? Candidate = null);

public sealed partial class MemoryStore
{
    // The checks every step of several takes before anything is read: a person's channel, 1 to ReleaseAllMax memories, each
    // named once.
    static void Several(IEnumerable<Guid> memories, MemoryCall call, string what)
    {
        if (call.Channel == "mcp") throw new MemoryRefused("forbidden", $"{what} several at once is a person's step, from the app, never an AI client's");
        var ids = memories.ToList();
        if (ids.Count is 0 or > ReleaseAllMax) throw new MemoryRefused("invalid", $"{what} 1 to {ReleaseAllMax} memories at a time");
        if (ids.Distinct().Count() != ids.Count) throw new MemoryRefused("invalid", "Name each memory once");
    }

    // Into the trash, each at the revision its person saw: one that changed since stays where it is (conflict).
    public async Task<IReadOnlyList<MemoryForgetResult>> ForgetAll(Guid principal, IReadOnlyList<(Guid Id, int Revision)> memories, MemoryCall call, CancellationToken ct)
    {
        Several(memories.Select(m => m.Id), call, "Forgetting");
        var results = new List<MemoryForgetResult>();
        foreach (var (id, revision) in memories)
        {
            try
            {
                var t = await Forget(principal, id, revision, call, ct);
                results.Add(new(id, "forgotten", t.Revision, t.Trash.Nonce, (int)Math.Ceiling((t.Trash.PurgeAfter - t.Trash.TrashedAt).TotalDays), t.Cascade));
            }
            catch (MemoryRefused refused) when (refused.Code is "unknown" or "forbidden" or "conflict")
            {
                results.Add(new(id, refused.Code, refused.Revision));
            }
        }
        return results;
    }

    // Back from the trash, each from the trash row its person saw: one trashed again since is left there (unknown).
    public async Task<IReadOnlyList<MemoryRestoreResult>> RestoreAll(Guid principal, IReadOnlyList<(Guid Id, Guid Nonce)> memories, MemoryCall call, CancellationToken ct)
    {
        Several(memories.Select(m => m.Id), call, "Restoring");
        var results = new List<MemoryRestoreResult>();
        foreach (var (id, nonce) in memories)
        {
            try
            {
                var r = await Restore(principal, id, nonce, call, ct);
                results.Add(new(id, "restored", r.Revision, r.Hold?.Reason, r.Cascade));
            }
            catch (MemoryRefused refused) when (refused.Code is "unknown" or "forbidden" or "conflict")
            {
                results.Add(new(id, refused.Code, refused.Revision));
            }
        }
        return results;
    }

    // For good, each with its trash row's nonce and the confirmation its person gave, its own id, as DeleteForever takes one.
    public async Task<IReadOnlyList<MemoryDeleteResult>> DeleteAll(Guid principal, IReadOnlyList<(Guid Id, Guid Nonce, string Confirm)> memories, MemoryCall call, CancellationToken ct)
    {
        Several(memories.Select(m => m.Id), call, "Deleting");
        var results = new List<MemoryDeleteResult>();
        foreach (var (id, nonce, confirm) in memories)
        {
            try
            {
                await DeleteForever(principal, id, nonce, confirm, call, ct);
                results.Add(new(id, "deleted"));
            }
            catch (MemoryRefused refused) when (refused.Code is "unknown" or "forbidden")
            {
                results.Add(new(id, refused.Code));
            }
        }
        return results;
    }

    // Each moved between its owner's private scope and Family, at the revision its person saw, by Share's own rules. Several's
    // own checks came first, so an invalid here is this memory's: it stays, and the rest are still tried.
    public async Task<IReadOnlyList<MemoryShareResult>> ShareAll(Guid principal, IReadOnlyList<(Guid Id, int Revision)> memories, string? scope, MemoryCall call, CancellationToken ct)
    {
        Several(memories.Select(m => m.Id), call, "Sharing");
        var results = new List<MemoryShareResult>();
        foreach (var (id, revision) in memories)
        {
            try
            {
                var w = await Share(principal, id, scope, revision, call, ct);
                results.Add(new(id, w.Unchanged ? "unchanged" : "moved", w.Revision, w.ProjectLeft));
            }
            catch (MemoryRefused refused) when (refused.Code is "unknown" or "forbidden" or "conflict" or "held" or "no_author" or "scopes_off" or "invalid")
            {
                results.Add(new(id, refused.Code, refused.Revision));
            }
        }
        return results;
    }

    // A review queue's one-word step for several items at once (SB-P6-01; upstream's /patterns/resolve with ids): each through
    // ResolveItem, the one door, so each leaves its queue once with its own step's lock, audit row and answer, and one refused leaves
    // the others done. Only MemoryResolve.BulkActions: still_true names the revision its person read of each memory, and an
    // insight or a pair names none, as one resolve takes them; anything else is refused before anything is read. A person's step:
    // an AI client's channel is refused first (Several), though its tools resolve one item at a time.
    public async Task<IReadOnlyList<MemoryResolveResult>> ResolveAll(Guid principal, string action, IReadOnlyList<(Guid Id, int? Revision)> items, InsightStore insights,
        MemoryCall call, CancellationToken ct)
    {
        Several(items.Select(i => i.Id), call, "Resolving");
        var word = (action ?? "").Trim().ToLowerInvariant();
        if (!MemoryResolve.BulkActions.Contains(word))
            throw new MemoryRefused("invalid", $"Several at once take {string.Join(", ", MemoryResolve.BulkActions)}; every other step is one at a time");
        var memory = word == MemoryResolve.StillTrue;
        if (items.Any(i => i.Revision is null == memory))
            throw new MemoryRefused("invalid", memory ? "Each memory names the revision its person read" : "An insight or a pair has no revision");
        var results = new List<MemoryResolveResult>();
        foreach (var (id, revision) in items)
        {
            try
            {
                var r = await ResolveItem(principal, new(id, word, Expected: revision), insights, call, ct);
                results.Add(new(id, r.Waiting && !r.Unchanged ? "resolved" : "unchanged", r.Write?.Revision, r.Review?.Review, r.Review?.View, r.Candidate?.Status));
            }
            catch (MemoryRefused refused) when (refused.Code is "unknown" or "forbidden" or "conflict" or "held" or "retracted" or "invalid")
            {
                results.Add(new(id, refused.Code, refused.Revision));
            }
        }
        return results;
    }

    // The memories named that the caller reads, through the one read predicate on each memory's own row: at most `chars` of
    // its words, none for a held memory. One it does not read, or one in the trash, is left out.
    public async Task<IReadOnlyDictionary<Guid, MemoryPreview>> Previews(Guid principal, IReadOnlyCollection<Guid> memories, int chars, CancellationToken ct)
    {
        var found = new Dictionary<Guid, MemoryPreview>();
        if (memories.Count == 0) return found;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT m.id,CASE WHEN m.hold IS NULL THEN left(m.content,@chars) END,m.hold IS NULL AND length(m.content)>@chars,m.hold
            {From} WHERE m.id=ANY(@ids) AND {Database.Visible("r")}
            """, ("principal", principal), ("ids", memories.ToArray()), ("chars", chars));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            found[r.GetGuid(0)] = new(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3));
        return found;
    }
}
