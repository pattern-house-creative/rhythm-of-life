using NpgsqlTypes;

namespace Knowledge;

// Share a native memory between its owner's private scope and Family, and take it back (SB-P3-10; docs/dev/memory.md
// sections 5, 8 and 11, story 2; migration 048's share_memory, which decides who may, where it lands and what follows
// it). A share moves the memory, it is no copy: its subject and scope change in place, so its passages, vectors and
// person- and AI-made links go with it; its author stays; its project follows by slug where one may take it; it leaves
// its capsule slot at a person's step, and through an AI client's channel one in a slot is refused (a slot is a
// person's); and the map's star rows for it, whose titles are excerpts, are dropped with their links, so nothing of it
// stays cached under the scope it left (but for a pass saving across the move, memory.md section 8). Taking a memory
// back is its author's or a Family admin's, and it goes to its author's private scope only, while that author is a
// current person and never so that two people's private records link. With memory.scopes off every memory is the
// household's already, and a share is refused (scopes_off) with nothing changed.
//
// The MCP tool `share` (Mcp/MemoryTools.cs) calls it, made only with memory.tools and memory.share on, both off by
// default (Q-157); real use waits for the owner's per-person scopes (D-013).
public sealed partial class MemoryStore
{
    // Scope: shared (Family, upstream's company), the default, or private (upstream's personal: back to the author's
    // private scope). Expected: the revision the caller read. A share takes no idempotency key: sharing again answers
    // unchanged.
    public Task<MemoryWrite> Share(Guid principal, Guid memory, string? scope, int? expected, MemoryCall call, CancellationToken ct)
    {
        string? to;
        try { to = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        if (call.Key is not null) throw new MemoryRefused("invalid", "A share takes no idempotency key: sharing again answers unchanged");
        return Write("SELECT share_memory(@principal,@id,@to,@expected,@channel,@client)::text", principal, call, [], ct,
            ("id", NpgsqlDbType.Uuid, memory), ("to", NpgsqlDbType.Text, to ?? "shared"), ("expected", NpgsqlDbType.Integer, expected));
    }
}
