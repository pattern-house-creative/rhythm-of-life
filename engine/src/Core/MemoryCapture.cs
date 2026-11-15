using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// What a capture token saves (SB-P7-01, migration 067; docs/dev/memory.md section 5): one memory through the checked
// writer capture_memory, which decides where it lands (the token's one scope) and whether it may change a mirror, screened
// and held as any write through a channel scored as an AI client's. The route is Mcp/CaptureEndpoint.cs.

// A capture: its text as given (MemoryInput prepares it and its tags), the scope it names (only the token's own is taken),
// a project's slug, a short source name, and Mirror, the id of the outside item this memory mirrors.
public sealed record CaptureDraft(string Text, IReadOnlyList<string>? Tags = null, string? Scope = null, string? Project = null,
    string? Source = null, string? Mirror = null);

// What a capture answers: nothing of the scope it wrote into, since the token reads nothing. Scopes "off": everyone in the
// household reads it (D-048). Held: it waits for a person's review. Unchanged: a mirror with nothing new. Replayed: the
// answer to an earlier request with the same key. DroppedTags: the caller's tags in a namespace the engine sets itself, and
// task, which puts a memory on what the person owes (058) and which capture_memory (067) takes from no outside source.
public sealed record Captured(Guid Id, int Revision, string Scope, string Scopes, string Status, bool Held, bool Mirror, bool Unchanged,
    bool Replayed, IReadOnlyList<string> DroppedTags);

public sealed partial class MemoryStore
{
    public async Task<Captured> Capture(Guid principal, CaptureDraft draft, string? client, string? key, CancellationToken ct)
    {
        var (memory, dropped) = Drafted(new(draft.Text, draft.Tags, draft.Scope, Project: draft.Project, Source: draft.Source));
        if (memory["tags"]!.AsArray().Any(t => t?.GetValue<string>() == "task")) dropped = [.. dropped, "task"];
        memory["mirror"] = draft.Mirror;
        await using var cmd = source.CreateCommand("SELECT capture_memory(@principal,@capture::jsonb,@client,@key)::text");
        cmd.Parameters.Add(new NpgsqlParameter("principal", NpgsqlDbType.Uuid) { Value = principal });
        cmd.Parameters.Add(new NpgsqlParameter("capture", NpgsqlDbType.Jsonb) { Value = memory.ToJsonString() });
        cmd.Parameters.Add(new NpgsqlParameter("client", NpgsqlDbType.Text) { Value = (object?)AdminAudit.Client(client) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)key ?? DBNull.Value });
        string json;
        try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        bool Flag(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new(o.GetProperty("id").GetGuid(), o.GetProperty("revision").GetInt32(), o.GetProperty("scope").GetString()!,
            o.GetProperty("scopes").GetString()!, o.GetProperty("status").GetString()!, Flag("held"), Flag("mirror"), Flag("unchanged"),
            Flag("replayed"), dropped);
    }
}
