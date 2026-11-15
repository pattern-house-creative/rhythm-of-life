using System.Text.Json.Nodes;

namespace Knowledge.Mcp;

// app_memories' ops for the memory UI's last pieces (SB-P6-01, run 22; the rest of the tool is AppMemories.cs, whose header says what
// every op keeps to: the channel fixed as the app's, a person's, only with memory.tools on, each through the store's checked functions).
//
// standing: the standing instructions this caller reads, oldest first (the oldest of a scope are those that may fire), each with whether
// it can fire and why not, a held one without its words, tags or project, and the whole list's length with how many may fire, are held
// and are over the limit, from the one statement list_standing reads (MemoryStore.Standing, 065's memory_standing_rows); served says
// whether any fires at all now (engine.recall on and the embeddings model loaded), as list_standing's model does. stop_standing: the
// same checked writer as the memory tool (065's stop_memory_standing), its author lock and revision check, a write's answer; undo
// starts it again. resolve_all: one of a review queue's one-word steps for several items (MemoryStore.ResolveAll), each answered on
// its own. An AI client's token reaches none of it: the door sends it to the MCP tools, whose own list_standing and stop_standing it
// has, and which have no step of several.
public static partial class AppMemories
{
    public const int StandingDefault = 20;

    static async Task<JsonObject> StandingList(IServiceProvider services, MemoryStore store, Guid principal, string? scope, string? project, string? cursor, int limit,
        CancellationToken ct)
    {
        var at = MemoryTools.Cursor(cursor);
        var page = await store.Standing(principal, scope, limit, at is { } a ? new MemoryStandingCursor(a.At, a.Id) : null, ct, project);
        return new()
        {
            ["standing"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.ToString(), ["revision"] = i.Revision, ["scope"] = i.Scope, ["owner"] = i.Subject, ["author"] = i.Author, ["source"] = i.Source,
                ["status"] = i.Status, ["project"] = i.Project is { } p ? new JsonObject { ["id"] = p.Id.ToString(), ["slug"] = p.Slug } : null,
                ["createdAt"] = i.CreatedAt, ["content"] = i.Content, ["held"] = Held(i.Hold), ["fires"] = i.Reason is null, ["reason"] = i.Reason,
            })]),
            ["next"] = page.Next is { } next ? MemoryTools.Cursor(next.CreatedAt, next.Id) : null,
            ["count"] = page.Total, ["firing"] = page.Firing, ["held"] = page.Held, ["overLimit"] = page.OverLimit,
            ["maxInScope"] = MemoryStanding.MaxInScope, ["maxFires"] = MemoryStanding.MaxFires,
            ["served"] = MemoryTools.StandingServing(services).Model is not null,
        };
    }

    // A step of several: what was asked, and each item as it went, with no one's words.
    static JsonObject ResolvedAll(string action, IReadOnlyList<MemoryResolveResult> results) => new()
    {
        ["op"] = "resolve_all", ["action"] = action,
        ["results"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id.ToString(), ["result"] = r.Result, ["revision"] = r.Revision, ["review"] = r.Review, ["view"] = r.View, ["candidate"] = r.Candidate,
        })]),
    };
}
