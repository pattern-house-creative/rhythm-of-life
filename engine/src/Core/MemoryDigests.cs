using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Digests of native memories (SB-P5-13; story 13; docs/dev/memory.md sections 5 and 9; migration 054): one memory that
// sums up a project or a topic tag of one scope from its long tail of small memories. The engine runs no model (D-033):
// it lists, by one rule (054's memory_digest_eligible), the memories a digest would draw from and the list's basis
// (DigestSources); the AI client writes the paragraph and saves it with that basis (SaveDigest), as a new memory
// labelled the engine's own (source system) with a drawn_from link to each of them, and none of them changes (D-032);
// and the newest digest is read (Digest), with nothing written (D-034).
//
// Only the memory tools call it (digest_sources, save_digest and digest; SB-P3-09), made only with memory.tools on, off
// by default: with it off nothing is listed, answered or written.

// One memory a digest would draw from, as digest_sources lists it: a preview of at most the asked characters (Truncated
// when longer); never a held one, which is never eligible.
public sealed record MemoryDigestSource(Guid Id, int Revision, string? Kind, int? Importance, string Status, DateTimeOffset CreatedAt,
    string Content, bool Truncated);

// What digest_sources answers: the scope it is of (Subject, Scope; Scopes the install's mode) and its key (Project or
// Tag); how many memories are eligible (Eligible, every one) and the newest Maximum of them (Items), in the list's order;
// the list's Basis; whether a digest is Due and, when not, why (Waiting: cooldown or too_few); the newest live digest of
// the key in the scope (Latest, LatestAt: none while it is held for review, since a key that named a held memory would
// tell what it holds, D-149, though its day's wait still counts) and when the next is due (NextAt, while it waits for the
// cooldown).
public sealed record MemoryDigestList(string Subject, string Scope, string Scopes, MemoryProjectRef? Project, string? Tag, int Eligible,
    int Minimum, int Maximum, IReadOnlyList<MemoryDigestSource> Items, string Basis, bool Due, string? Waiting, Guid? Latest,
    DateTimeOffset? LatestAt, DateTimeOffset? NextAt);

// What save_digest answers: the digest as a write answers a memory, its key's tag (none for a project's) and the
// memories it was drawn from, in the list's order. Write.Unchanged: a live digest of the scope was saved from this basis
// already, and this is it.
public sealed record MemoryDigestWrite(MemoryWrite Write, string? Tag, IReadOnlyList<Guid> DrawnFrom);

// What digest reads: the newest digest of the key the caller reads, whole, or none; and how many memories it was drawn
// from (its active drawn_from links). Edited: someone changed its words or tags since it was written, so it is no longer
// the engine's own line and the client's paragraph alone (063's memory_system_row: a person's edit marks it, SB-P5-04).
public sealed record MemoryDigestRead(MemoryRecord? Digest, int Sources, bool Edited = false);

public sealed partial class MemoryStore
{
    // The engine's own label on a digest, which no AI client (MemoryTools.SourceProblem), the app or the terminal gives a
    // memory: they record their channel's.
    public const string DigestSource = "system";

    // A digest's key: a project's slug or alias, or a topic tag, exactly one. The tag as a write stores one (trimmed,
    // lower case), never one the engine sets itself.
    internal static (string? Project, string? Tag) DigestKey(string? project, string? tag)
    {
        var slug = MemoryInput.Project(project);
        var topic = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim().ToLowerInvariant();
        if ((slug is null) == (topic is null)) throw new MemoryRefused("invalid", "A digest is of one project or one tag: name exactly one");
        if (topic is not null && (topic.Length > MemoryInput.MaxTagLength || topic.Any(char.IsControl)))
            throw new MemoryRefused("invalid", $"A tag is at most {MemoryInput.MaxTagLength} characters, with no control characters");
        if (topic is not null && MemoryInput.Reserved(topic))
            throw new MemoryRefused("invalid", "That tag is one the engine sets itself, not a topic");
        return (slug, topic);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Basis();

    // The scope a digest of the caller's is of: the one named, or its own, as remember lands a memory (033's
    // memory_landing: with scopes off the household's; on, the caller's person's, or Family when named, only where it
    // reads); and whether scopes are on.
    static async Task<(string Subject, bool Scoped)> DigestScope(NpgsqlConnection c, Guid principal, string? scope, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, null, $"""
            SELECT memory_landing(@principal,(SELECT person_id FROM principals WHERE id=@principal),@scope::text,NOT coalesce({Database.ScopesOff},false)),
              NOT coalesce({Database.ScopesOff},false)
            """, ("principal", principal), ("scope", scope));
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetString(0), r.GetBoolean(1));
        }
        catch (PostgresException e) when (Refusal(e) is { } refused) { throw refused; }
    }

    // A project the caller's lists show in that scope, by slug or alias (SB-P3-21: never a held one); else unknown_project
    // with up to 10 it does know.
    async Task<MemoryProjectRef> DigestProject(NpgsqlConnection c, Guid principal, string subject, string name, CancellationToken ct)
    {
        await using (var cmd = Database.Command(c, null, $"""
            SELECT p.id,p.slug FROM memory_projects p WHERE p.subject_id=@subject AND {ProjectNamed} AND {ProjectListed()}
            ORDER BY p.slug=@name DESC LIMIT 1
            """, ("principal", principal), ("subject", subject), ("name", name)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) return new(r.GetGuid(0), r.GetString(1));
        throw await UnknownProject(principal, ct);
    }

    async Task<MemoryRefused> UnknownProject(Guid principal, CancellationToken ct)
    {
        var names = await KnownProjects(principal, 10, ct);
        return new("unknown_project", names.Count == 0 ? "No project by that name in this scope"
            : $"No project by that name in this scope; known projects: {string.Join(", ", names)}", known: names);
    }

    // The memories a digest of a key in one scope would draw from, the newest first, with the list's basis and whether a
    // digest is due: read in one snapshot, so the basis is the list's. Writes nothing.
    public async Task<MemoryDigestList> DigestSources(Guid principal, string? project, string? tag, string? scope, int previewChars, CancellationToken ct)
    {
        if (previewChars is < 0 or > MemoryInput.PreviewMax) throw new MemoryRefused("invalid", $"A preview is 0 to {MemoryInput.PreviewMax} characters");
        var (slug, topic) = DigestKey(project, tag);
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var (subject, scoped) = await DigestScope(c, principal, scope, ct);
        var key = slug is null ? null : await DigestProject(c, principal, subject, slug, ct);
        (string, object?)[] args = [("subject", subject), ("project", key?.Id), ("tag", topic), ("chars", previewChars)];
        int eligible, minimum, maximum; string basis; Guid? latest = null; DateTimeOffset? latestAt = null, writtenAt = null; TimeSpan cooldown;
        DateTimeOffset now;
        await using (var cmd = Database.Command(c, null, """
            SELECT (SELECT count(*)::int FROM memory_digest_eligible(@subject,@project::uuid,@tag::text)),
              memory_digest_basis(@subject,@project::uuid,@tag::text),d.id,d.created_at,b.minimum,b.maximum,b.cooldown,now(),d.hold IS NOT NULL
            FROM memory_digest_bounds() b LEFT JOIN LATERAL memory_digest_latest(@subject,@project::uuid,@tag::text) d ON d.id IS NOT NULL
            """, args))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (eligible, basis) = (r.GetInt32(0), r.GetString(1));
            if (!r.IsDBNull(2)) writtenAt = r.GetFieldValue<DateTimeOffset>(3);
            if (!r.IsDBNull(2) && !r.GetBoolean(8)) (latest, latestAt) = (r.GetGuid(2), writtenAt);
            (minimum, maximum, cooldown, now) = (r.GetInt32(4), r.GetInt32(5), r.GetFieldValue<TimeSpan>(6), r.GetFieldValue<DateTimeOffset>(7));
        }
        var items = new List<MemoryDigestSource>();
        await using (var cmd = Database.Command(c, null, """
            SELECT m.id,m.revision,m.kind,m.importance,m.status,m.created_at,left(m.content,@chars),length(m.content)>@chars
            FROM (SELECT x.* FROM memory_digest_eligible(@subject,@project::uuid,@tag::text) x ORDER BY x.created_at DESC,x.id DESC
              LIMIT (SELECT b.maximum FROM memory_digest_bounds() b)) e
            JOIN memories m ON m.id=e.id
            ORDER BY e.created_at DESC,e.id DESC
            """, args))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                items.Add(new(r.GetGuid(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt16(3),
                    r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetBoolean(7)));
        await tx.CommitAsync(ct);
        // The write's order: within the day after a live digest of the key, not due; else too few, not due.
        var cooling = writtenAt is { } at && at > now - cooldown;
        var waiting = cooling ? "cooldown" : eligible < minimum ? "too_few" : null;
        return new(subject, subject == RhythmConfig.Household ? "shared" : "private", scoped ? "on" : "off", key, topic, eligible, minimum, maximum,
            items, basis, waiting is null, waiting, latest, latestAt, cooling ? writtenAt + cooldown : null);
    }

    // The digest the AI client wrote from DigestSources' list, saved with that list's basis as a new memory of the scope,
    // a draft labelled the engine's own, with a drawn_from link to each memory of the list (054's save_memory_digest).
    // The same basis again answers the digest saved from it, unchanged.
    public async Task<MemoryDigestWrite> SaveDigest(Guid principal, string? project, string? tag, string? scope, string content, string basis,
        MemoryCall call, CancellationToken ct)
    {
        var (slug, topic) = DigestKey(project, tag);
        var text = MemoryInput.Text(content);
        var given = (basis ?? "").Trim();
        if (!Basis().IsMatch(given)) throw new MemoryRefused("invalid", "basis is the one digest_sources gave, as it was given");
        var digest = new JsonObject
        {
            ["content"] = text,
            ["project"] = slug,
            ["tag"] = topic,
            ["scope"] = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(),
            ["basis"] = given,
        };
        (MemoryWrite Write, JsonElement Answer) saved;
        try
        {
            saved = await WriteAnswer("SELECT save_memory_digest(@principal,@digest::jsonb,@channel,@client,@key)::text", principal, call, [], ct,
                ("digest", NpgsqlDbType.Jsonb, digest.ToJsonString()));
        }
        catch (MemoryRefused refused) when (refused.Code == "unknown_project") { throw await UnknownProject(principal, ct); }
        var o = saved.Answer;
        return new(saved.Write, o.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            o.TryGetProperty("drawnFrom", out var drawn) && drawn.ValueKind == JsonValueKind.Array ? [.. drawn.EnumerateArray().Select(x => x.GetGuid())] : []);
    }

    // The newest digest of a project or tag that the caller reads, in one scope or in every one it reads: live (in the
    // catalog, not in the trash, not marked wrong, its window open), and not held, since a key that selected a held one
    // would tell the caller what it holds (D-149). Reads only: no recall follow-up is marked and nothing is written.
    public async Task<MemoryDigestRead> Digest(Guid principal, string? project, string? tag, string? scope, CancellationToken ct)
    {
        var (slug, topic) = DigestKey(project, tag);
        string? named;
        try { named = MemorySettings.Named(scope); }
        catch (ArgumentException e) { throw new MemoryRefused("invalid", e.Message); }
        await using var c = await source.OpenConnectionAsync(ct);
        var where = new List<string> { Database.Visible("r"), $"m.source='{DigestSource}'", "m.hold IS NULL", "memory_digest_live(m.id)" };
        var args = new List<(string, object?)> { ("principal", principal) };
        if (named is not null) { where.Add("m.scope=@scope"); args.Add(("scope", named)); }
        if (slug is not null)
        {
            var listed = $"memory_projects p WHERE {ProjectNamed} AND {ProjectListed()}" + (named is null ? "" : " AND CASE WHEN p.subject_id='household' THEN 'shared' ELSE 'private' END=@scope");
            await using (var known = Database.Command(c, null, $"SELECT EXISTS(SELECT 1 FROM {listed})", [.. args, ("name", slug)]))
                if (await known.ExecuteScalarAsync(ct) is not true) throw await UnknownProject(principal, ct);
            // The key its created row names, never its tags or project as an update may have left them (054).
            where.Add($"EXISTS(SELECT 1 FROM {listed} AND memory_digest_keyed(m.id,p.id,NULL))");
            args.Add(("name", slug));
        }
        else
        {
            where.Add("memory_digest_keyed(m.id,NULL,@tag)");
            args.Add(("tag", topic));
        }
        MemoryRecord? found = null;
        await using (var cmd = Database.Command(c, null, $"{Select(null)} WHERE {string.Join(" AND ", where)} ORDER BY m.created_at DESC,m.id DESC LIMIT 1", [.. args]))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) found = Record(r);
        if (found is null) return new(null, 0);
        await using var count = Database.Command(c, null, """
            SELECT (SELECT count(*)::int FROM made_links l WHERE l.source_id=@id AND l.type='drawn_from' AND l.lifecycle='active'),
              NOT coalesce(memory_system_row(@id),false)
            """, ("id", found.Id));
        await using var row = await count.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);
        return new(found, row.GetInt32(0), row.GetBoolean(1));
    }
}
