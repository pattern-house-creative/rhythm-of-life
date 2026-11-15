namespace Knowledge;

// How many memories a list holds, and the tags they carry with how many carry each (SB-P3-17; upstream's GET /count and
// GET /tags with counts). Both read through the predicates a list takes (MemoryStore.Filtered): the read predicate on
// each memory's own resources row, then the filter. So a count is always the length of the list it summarises, paged
// through to its end, and a tag's count the length of that list filtered by the tag as well; a tag comes only from a
// memory the reader reads, never from another person's private one. A held memory's tags are none of the vocabulary,
// as a list serves it without them and a tag filter never selects it (D-149); it is counted where a list holds it.
//
// The app's Memories view reads them beside its list (app_memories tags, SB-P6-01), only with memory.tools on; the brief (story
// 12) takes each count from the statement that reads its own list instead, so every other answer is as it was.

public sealed record MemoryTagCount(string Tag, int Count);

public sealed partial class MemoryStore
{
    // The memories a list with this filter holds, every page together (its Limit and PreviewChars aside).
    public async Task<int> Count(Guid principal, MemoryFilter filter, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var (where, args) = await Filtered(c, principal, filter, ct);
        await using var cmd = Database.Command(c, null, $"SELECT count(*)::int {From} WHERE {string.Join(" AND ", where)}", [.. args]);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // Every tag on the memories a list with this filter holds, held ones aside, each with how many of them carry it: the
    // most carried first, then by name. A memory carries a tag once (033), so a count is a number of memories.
    public async Task<IReadOnlyList<MemoryTagCount>> Tags(Guid principal, MemoryFilter filter, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var (where, args) = await Filtered(c, principal, filter, ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT t.tag,count(*)::int {From} CROSS JOIN LATERAL unnest(m.tags) t(tag)
            WHERE m.hold IS NULL AND {string.Join(" AND ", where)}
            GROUP BY t.tag ORDER BY count(*) DESC,t.tag COLLATE "C"
            """, [.. args]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var tags = new List<MemoryTagCount>();
        while (await r.ReadAsync(ct)) tags.Add(new(r.GetString(0), r.GetInt32(1)));
        return tags;
    }
}
