using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Native memories that cite Drive documents (SB-P4-10, story 7; docs/dev/memory.md 10.4; D-014): a citation joins a
// memory to the original by the source's own id and the revision the memory was written against, and keeps none of its
// words. Writes go through migration 055's checked functions (cite_memory_source, revalidate_memory_citations), which
// decide who may under the recorded memory.scopes mode: whoever may change the memory (033's author lock), not held, a
// document the caller reads on the map, no wider than the document. Reads serve a citation only to a reader of its
// memory who reads the document too (memory_citation_readable), and a held memory's citations to no one (D-160).
//
// The engine checks each document at its source itself (CitationChecks, through the adapter's ICitedSource): at a cite,
// which records what the check found, never a revision a caller names; at every read, whose answer says what the check
// found now and stores nothing; and at a revalidation, which moves a citation to the document's current revision and
// says what moved. A check is one metadata read: no content is downloaded, and no map pass, sync or stored row of the
// map is touched.
//
// Only the memory tools call it (cite, revalidate, get; Mcp/MemoryTools.cs), made only with memory.tools on, off by
// default: with it off no install holds a citation, and every existing answer is as it was.

// A citation as its reader is served it: the document (provider, the map's star account, the source's id, its star id)
// and what the memory cites (the revision, when it was modified then, its name and folders then), when it was cited and
// last checked (CheckedState: current, trashed, gone, unreadable or unavailable, as that check found it), and the last
// revalidation (when, and the revision it moved from). MapStatus: the status of the map's newest row for the document,
// since MapSince (when the pass that wrote it began). OnMap: that row is current, so the document is among those Rhythm
// is given to read now. One that left the map is never checked at its source and never moved to what it holds now: a
// missing row (the source's sync no longer finds it in the folders Rhythm is given: moved out, trashed, deleted or no
// longer shared) is unavailable; a revoked one (held back since a restore) is unchecked.
public sealed record MemoryCitation(Guid Id, string Provider, string Account, string Document, string Revision, DateTimeOffset? ModifiedAt,
    string Title, IReadOnlyList<string> Parents, DateTimeOffset CitedAt, DateTimeOffset CheckedAt, string CheckedState, string? EarlierRevision,
    DateTimeOffset? RevalidatedAt, string MapStatus = "current", DateTimeOffset? MapSince = null)
{
    public string Star => new ResourceKey("", Provider, Account, Document).GraphId;
    public bool OnMap => MapStatus == "current";
}

// What a cite did: the memory as a write answers it, the citation's id, and whether this call made it.
public sealed record MemoryCited(Guid Id, int Revision, string Subject, string Scope, string Status, string Scopes, Guid Citation, bool Created);

// What a revalidation did to one citation it checked: whether it moved the citation (Revalidated), and the citation before.
public sealed record MemoryRevalidation(Guid Citation, bool Revalidated, string Revision, string CheckedState, DateTimeOffset CheckedAt);

public sealed record MemoryRevalidated(Guid Id, int Revision, string Subject, string Scope, string Status, string Scopes,
    IReadOnlyList<MemoryRevalidation> Results);

// A citation as a check sees it now, in words: State (current, changed, trashed, gone, unreadable, unavailable or
// unchecked), what changed (edited, moved, renamed, revised), and a message that says so plainly. Never the document's
// words.
public sealed record CitationNow(string State, IReadOnlyList<string> Changes, string Message);

public sealed partial class MemoryStore
{
    public const int MaxCitations = 20;

    // The citations of a memory its reader is served: the memory read through the one predicate on its own row, not held,
    // and each document read by the reader too (memory_citation_readable). Oldest first. Empty for a memory the reader
    // does not read, one in the trash or held, and one that cites nothing.
    public async Task<IReadOnlyList<MemoryCitation>> Citations(Guid principal, Guid memory, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, $"""
            SELECT x.citation_id,x.provider,x.account,x.source_id,x.revision,x.modified_at,x.title,x.parents,x.cited_at,x.checked_at,
              x.checked_state,x.earlier_revision,x.revalidated_at,d.status,
              coalesce((d.relationship_snapshot->>'RetrievedAt')::timestamptz,d.indexed_at)
            FROM memory_citations x JOIN memories m ON m.id=x.memory_id
            CROSS JOIN LATERAL memory_cited_row(x.provider,x.source_id) d
            JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.external_id=m.id::text
            WHERE x.memory_id=@id AND m.hold IS NULL AND {Database.Visible("r")}
              AND memory_citation_readable(@principal,x.provider,x.source_id,x.subject_id,x.scope)
            ORDER BY x.cited_at,x.citation_id
            """, ("principal", principal), ("id", memory));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var found = new List<MemoryCitation>();
        while (await reader.ReadAsync(ct))
            found.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetFieldValue<string[]>(7),
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? "none" : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14)));
        return found;
    }

    // Whether the map holds the document now for this reader, as a cite needs it (055's own check, before it locks
    // anything): its newest row current, a document, read through the one predicate. Asked before any source is, so a
    // document the caller does not read is never looked up at its source on the caller's word.
    public async Task<bool> OnMap(Guid principal, string provider, string document, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Database.Command(c, null, """
            SELECT coalesce(d.id IS NOT NULL AND d.status='current' AND d.kind='document'
              AND EXISTS(SELECT 1 FROM readable_subjects(@principal) x WHERE (x.subject_id IS NULL OR x.subject_id=d.subject_id)
                AND (x.provider IS NULL OR x.provider=d.provider) AND (x.scope IS NULL OR x.scope=d.scope)),false)
            FROM memory_cited_row(@provider,@document) d
            """, ("principal", principal), ("provider", provider), ("document", document));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // The memory cites the document (Provider, Document: the source's id) as a check found it just now (Seen, readable).
    // A document cited already answers unchanged (Created false) and moves nothing. No idempotency key: citing again
    // answers unchanged.
    public async Task<MemoryCited> Cite(Guid principal, Guid memory, string provider, string document, CitedCheck seen, MemoryCall call,
        CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A citation takes no idempotency key: citing again answers unchanged");
        using var o = await CitationCall("SELECT cite_memory_source(@principal,@memory,@provider,@document,@seen::jsonb,@channel,@client)::text",
            principal, call, ct, ("memory", NpgsqlDbType.Uuid, memory), ("provider", NpgsqlDbType.Text, provider),
            ("document", NpgsqlDbType.Text, document), ("seen", NpgsqlDbType.Text, Seen(seen).ToJsonString()));
        var r = o.RootElement;
        return new(r.GetProperty("id").GetGuid(), r.GetProperty("revision").GetInt32(), r.GetProperty("subject").GetString()!,
            r.GetProperty("scope").GetString()!, r.GetProperty("status").GetString()!, r.GetProperty("scopes").GetString()!,
            r.GetProperty("citation").GetGuid(), r.GetProperty("created").GetBoolean());
    }

    // Records what checks found now for citations of the memory (an unchecked one is left out: nothing is known of it).
    // Each check carries the citation's checked time as it was read before the check (CheckedAt), and is recorded only
    // while the citation still has it: a check made earlier never overwrites what a later one recorded meanwhile.
    public async Task<MemoryRevalidated> Revalidate(Guid principal, Guid memory, IReadOnlyList<(Guid Citation, DateTimeOffset CheckedAt, CitedCheck Seen)> seen,
        MemoryCall call, CancellationToken ct)
    {
        if (call.Key is not null) throw new MemoryRefused("invalid", "A revalidation takes no idempotency key: revalidating again records the same");
        var checks = new JsonArray([.. seen.Where(s => s.Seen.State != CitedCheck.Unchecked).Select(s =>
        {
            var one = Seen(s.Seen);
            one["citation"] = s.Citation.ToString();
            one["checked_at"] = s.CheckedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            return (JsonNode)one;
        })]);
        using var o = await CitationCall("SELECT revalidate_memory_citations(@principal,@memory,@seen::jsonb,@channel,@client)::text",
            principal, call, ct, ("memory", NpgsqlDbType.Uuid, memory), ("seen", NpgsqlDbType.Text, checks.ToJsonString()));
        var r = o.RootElement;
        return new(r.GetProperty("id").GetGuid(), r.GetProperty("revision").GetInt32(), r.GetProperty("subject").GetString()!,
            r.GetProperty("scope").GetString()!, r.GetProperty("status").GetString()!, r.GetProperty("scopes").GetString()!,
            [.. r.GetProperty("results").EnumerateArray().Select(x => new MemoryRevalidation(x.GetProperty("citation").GetGuid(),
                x.GetProperty("revalidated").GetBoolean(), x.GetProperty("revision").GetString()!, x.GetProperty("checked_state").GetString()!,
                x.GetProperty("checked_at").GetDateTimeOffset()))]);
    }

    static JsonObject Seen(CitedCheck s) => new()
    {
        ["state"] = s.State, ["revision"] = s.Revision, ["title"] = s.Title,
        ["modified_at"] = s.ModifiedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["parents"] = new JsonArray([.. (s.Parents ?? []).Select(p => (JsonNode)p)]),
    };

    // A call of 055's functions in a transaction of its own; its own refusals (a hint: unknown_document, no document the
    // caller reads by that id; source_unavailable, no check found it readable) and then the store's.
    async Task<JsonDocument> CitationCall(string sql, Guid principal, MemoryCall call, CancellationToken ct,
        params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string json;
        await using (var cmd = new NpgsqlCommand(sql, c, tx))
        {
            (string Name, NpgsqlDbType Type, object? Value)[] all =
                [("principal", NpgsqlDbType.Uuid, principal), .. args, ("channel", NpgsqlDbType.Text, call.Channel),
                 ("client", NpgsqlDbType.Text, AdminAudit.Client(call.Client))];
            foreach (var (name, kind, value) in all) cmd.Parameters.Add(new NpgsqlParameter(name, kind) { Value = value ?? DBNull.Value });
            try { json = (string)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (e.SqlState == "22023" && e.Hint is "unknown_document" or "source_unavailable")
            { throw new MemoryRefused(e.Hint, e.MessageText); }
            catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
        }
        await tx.CommitAsync(ct);
        return JsonDocument.Parse(json);
    }
}

// The checks citations take: each document as its source holds it now, through the enabled adapter that owns its provider
// (ICitedSource), four at a time within five seconds a call. A check not done in time, one that failed and one whose
// adapter is off are unchecked, which says nothing about now; a check never retries and never waits past the budget.
public sealed class CitationChecks(AdapterRegistry adapters, TimeSpan? budget = null)
{
    public const int Concurrency = 4;
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public async Task<CitedCheck> Check(string provider, string document, CancellationToken ct) => (await Check([(provider, document)], ct))[0];

    public async Task<IReadOnlyList<CitedCheck>> Check(IReadOnlyList<(string Provider, string Document)> documents, CancellationToken ct)
    {
        var found = new CitedCheck[documents.Count];
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(budget ?? Budget);
        using var gate = new SemaphoreSlim(Concurrency);
        await Task.WhenAll(documents.Select(async (d, i) =>
        {
            if (adapters.Enabled.OfType<ICitedSource>().FirstOrDefault(a => a.Providers.ContainsKey(d.Provider)) is not { } cited)
            {
                found[i] = CitedCheck.Not(CitedCheck.Unchecked, $"The {d.Provider} adapter is off in this install, so the document was not checked.");
                return;
            }
            try
            {
                await gate.WaitAsync(window.Token);
                try { found[i] = await cited.Check(d.Provider, d.Document, window.Token); }
                finally { gate.Release(); }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                found[i] = CitedCheck.Not(CitedCheck.Unchecked, window.IsCancellationRequested
                    ? "The source did not answer in time." : "The source did not answer just now.");
            }
        }));
        return found;
    }

    // A citation's document that left the map (MemoryCitation.OnMap false) is not checked at its source: Rhythm reads only
    // what the map holds now. One whose newest row is missing (or deleted) is unavailable: the source's own sync no longer
    // finds it where Rhythm is given to read, which a revalidation records. One held back since a restore (revoked, until
    // the next rebuild) is unchecked, and says why: that says nothing about the document itself.
    public const string OffMap = "The document is held back on the map Rhythm reads since a restore, until the next map rebuild, so it was not "
        + "checked at Drive.";

    public static string NotFound(DateTimeOffset? since) =>
        "Rhythm's Drive sync no longer finds it in the folders Rhythm is given" + (since is { } at ? $" since {Instant(at)}" : "")
        + ": it was moved out of them, put in the Drive trash, deleted or no longer shared with Rhythm, so it was not checked at Drive.";

    public async Task<IReadOnlyList<CitedCheck>> Check(IReadOnlyList<MemoryCitation> citations, CancellationToken ct)
    {
        var on = citations.Where(c => c.OnMap).ToList();
        var seen = new Queue<CitedCheck>(await Check([.. on.Select(c => (c.Provider, c.Document))], ct));
        return [.. citations.Select(c => c.OnMap ? seen.Dequeue()
            : c.MapStatus is "missing" or "deleted" ? CitedCheck.Not(CitedCheck.Unavailable, NotFound(c.MapSince))
            : CitedCheck.Not(CitedCheck.Unchecked, OffMap))];
    }

    // A citation and what a check found now, in plain words. A readable document is current when it is at the revision,
    // name and folders the memory cites, else changed: edited (modified since), moved (other folders), renamed, or revised
    // (another revision with none of those: a change Drive records, such as its sharing). Titles and ids stay in the
    // answer's fields; the message names neither, so no stored text is put in it.
    public static CitationNow Describe(MemoryCitation c, CitedCheck seen)
    {
        var checkedThen = $"checked {Instant(c.CheckedAt)}";
        switch (seen.State)
        {
            case CitedCheck.Ok:
                var changes = new List<string>();
                if (seen.ModifiedAt is { } now && (c.ModifiedAt is not { } then || now.ToUniversalTime() != then.ToUniversalTime())) changes.Add("edited");
                if (!(seen.Parents ?? []).Order(StringComparer.Ordinal).SequenceEqual(c.Parents.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    changes.Add("moved");
                if (seen.Title is { } title && title != c.Title) changes.Add("renamed");
                if (seen.Revision != c.Revision && changes.Count == 0) changes.Add("revised");
                if (changes.Count == 0)
                    return new("current", [], $"Drive holds the revision this memory cites ({c.Revision}), as checked just now.");
                var what = string.Join(", ", changes.Select(x => x switch
                {
                    "edited" => seen.ModifiedAt is { } at ? $"edited {Instant(at)}" : "edited",
                    "moved" => "moved to other folders",
                    "renamed" => "renamed",
                    _ => "revised",
                }));
                return new("changed", changes,
                    $"The document changed since this memory cited it: Drive holds revision {seen.Revision} now ({what}); the memory cites "
                    + $"revision {c.Revision}, {checkedThen}. Read the document before relying on this memory: it may no longer agree. "
                    + "revalidate records the current revision once the memory still agrees with it; update the memory first when it does not.");
            case CitedCheck.Trashed or CitedCheck.Gone or CitedCheck.Unreadable:
                var reason = seen.Reason ?? "";
                return new(seen.State, [], seen.State switch
                {
                    CitedCheck.Trashed => $"The document this memory cites is in the Drive trash now. {reason} The memory cites revision {c.Revision}, {checkedThen}; nothing in Rhythm stands in for it.",
                    CitedCheck.Gone => $"The document this memory cites is gone from Drive now. {reason} The memory cites revision {c.Revision}, {checkedThen}; nothing in Rhythm stands in for it.",
                    _ => $"The document this memory cites can't be read now. {reason} The memory cites revision {c.Revision}, {checkedThen}; nothing in Rhythm stands in for it.",
                });
            case CitedCheck.Unavailable:
                return new(seen.State, [], $"The document this memory cites is no longer where Rhythm reads Drive. {seen.Reason ?? NotFound(null)} "
                    + $"The memory cites revision {c.Revision}, {checkedThen}; nothing in Rhythm stands in for it.");
            default:
                var last = c.CheckedState switch
                {
                    "trashed" => "it was in the Drive trash then",
                    "gone" => "it was gone from Drive then",
                    "unreadable" => "it could not be read then",
                    "unavailable" => "Rhythm's Drive sync no longer found it then",
                    _ => $"Drive held revision {c.Revision} then",
                };
                return new("unchecked", [], $"Not checked now: {seen.Reason ?? "the source did not answer."} Last {checkedThen}: {last}. "
                    + "Treat this citation as unconfirmed until a check answers.");
        }
    }

    static string Instant(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
