using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// A resurfaced old memory on Home (SB-P5-17; docs/dev/memory.md sections 5 and 7.2; migration 070; adapted from Second Brain's
// resurface pick, src/brief/compute.ts and src/runtime/resurface-state.ts at the pinned commit, MIT; NOTICE): one memory a day
// worth reading again. Of what the reader reads (the one read predicate on each memory's own row), 070's eligible and not
// dismissed: the one whose md5 of the install's day and its id comes first, so a reload shows the same one all day and a read
// writes nothing. A dismissal is the person's for good (070's dismiss_resurfaced), and the next read picks among the rest.
//
// Only the app's ops resurface and resurface_dismiss call it (its Home card), with memory.tools on, off by default. With it off
// nothing is read or written. It is no tool of an AI client's.

// The day's pick as a card reads it: at most ResurfacePreview characters of its words (Truncated when longer). A held memory is
// never picked, so it always has its words.
public sealed record MemoryResurfaced(Guid Id, int Revision, string Subject, string Scope, DateTimeOffset CreatedAt, string Content, bool Truncated);

// The install's day, YYYY-MM-DD, and that day's pick for this reader, or none.
public sealed record MemoryResurfacePick(string Day, MemoryResurfaced? Pick);

// A dismissal: Unchanged when this reader's was there already.
public sealed record MemoryResurfaceDismissed(Guid Id, bool Unchanged);

public sealed partial class MemoryStore
{
    public const int ResurfacePreview = 280;

    // This reader's pick for the day holding At, by default now, which only a test names.
    public async Task<MemoryResurfacePick> Resurface(Guid principal, CancellationToken ct, DateTimeOffset? at = null)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            WITH today AS (SELECT d AS day,to_char(d,'YYYY-MM-DD') AS named FROM (SELECT (coalesce(@at,now()) AT TIME ZONE install_timezone())::date AS d) x)
            SELECT today.named,x.* FROM today LEFT JOIN LATERAL (
              SELECT m.id,m.revision,m.subject_id,m.scope,m.created_at,left(m.content,@chars),length(m.content)>@chars
              {From}
              WHERE {Database.Visible("r")} AND memory_resurface_eligible(m.id,memory_resurface_before(today.day))
                AND NOT memory_resurface_dismissed(@principal,m.id)
              ORDER BY md5(today.named||m.id::text),m.id LIMIT 1) x ON true
            """, c);
        cmd.Parameters.Add(new("principal", NpgsqlDbType.Uuid) { Value = principal });
        cmd.Parameters.Add(new("at", NpgsqlDbType.TimestampTz) { Value = at is { } moment ? moment.ToUniversalTime() : DBNull.Value });
        cmd.Parameters.Add(new("chars", NpgsqlDbType.Integer) { Value = ResurfacePreview });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new(r.GetString(0), r.IsDBNull(1) ? null : new(r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
            r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetBoolean(7)));
    }

    // This reader retires a memory for good (070's dismiss_resurfaced): a person's step.
    public async Task<MemoryResurfaceDismissed> DismissResurfaced(Guid principal, Guid id, MemoryCall call, CancellationToken ct)
    {
        using var o = await Call("SELECT dismiss_resurfaced(@principal,@id,@channel,@client)::text", principal, call, ct, ("id", NpgsqlDbType.Uuid, id));
        return new(o.RootElement.GetProperty("id").GetGuid(), o.RootElement.GetProperty("unchanged").GetBoolean());
    }
}
