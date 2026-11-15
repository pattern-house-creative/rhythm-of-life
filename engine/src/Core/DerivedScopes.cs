using System.Text.Json.Nodes;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// What the database functions of derived records need from the map (docs/dev/memory.md sections 5 and 8; SB-P1-06,
// migration 027): an insight, an insight run, a dashboard, a dashboard write, a pin or a corner card is read only by
// readers of its subject and, with memory.scopes on, of every star it cites now. Which stars a caller reads now is the
// map's to say (RelationshipHost, the reader's own view), so the stores hand each function those stars, and the
// subjects of the stars a new record cites. With scopes off nothing is read from the map: the functions then take no
// stars and answer as before 027.
public sealed class DerivedScopes(NpgsqlDataSource source, RelationshipHost? host)
{
    public const string Household = "household";

    // Whether the recorded mode is on (anything but exactly 'off', as Database.ScopesOff reads it), and the person the
    // principal speaks for.
    public async Task<(bool Scoped, string? Person)> Caller(Guid principal, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand(
            "SELECT (SELECT value FROM install_settings WHERE key='memory.scopes') IS DISTINCT FROM 'off',(SELECT person_id FROM principals WHERE id=@p)");
        cmd.Parameters.AddWithValue("p", principal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    // The subject a log feed or write that names none reads or writes: with scopes on the caller's own person, or the
    // household for a caller with no person (memory.md section 5); null with scopes off, where it stays the first person.
    public async Task<string?> LogSubject(Guid principal, CancellationToken ct) =>
        await Caller(principal, ct) is (true, var person) ? person ?? Household : null;

    // Null with scopes off: the functions check nothing more than before. With scopes on, the graph id of every star the
    // principal reads now; none when the map can't be read for it (not ready yet, or a principal that reads no map), so a
    // record that cites a star answers as unknown rather than unchecked.
    public async Task<string[]?> Stars(Guid principal, CancellationToken ct) => (await Citing(principal, [], ct)).Stars;

    // Stars, and with scopes on {subject, provider} for each of `graphIds` the principal reads now, the scope a new record
    // citing them lands in (derived_subject, migration 027); null with scopes off or for no stars. A star it can't read
    // is left out: the caller's own check refuses or drops it first.
    public async Task<(string[]? Stars, JsonArray? Cited)> Citing(Guid principal, IEnumerable<string> graphIds, CancellationToken ct)
    {
        if (!(await Caller(principal, ct)).Scoped) return (null, null);
        IReadOnlyDictionary<string, (string Subject, string? Provider)> shown;
        try { shown = host is null ? new Dictionary<string, (string, string?)>() : await host.Shown(principal, ct); }
        catch (UnauthorizedAccessException) { shown = new Dictionary<string, (string, string?)>(); }
        var wanted = graphIds.Distinct(StringComparer.Ordinal).Where(shown.ContainsKey).ToArray();
        return ([.. shown.Keys], wanted.Length == 0 ? null : new JsonArray([.. wanted.Select(id => (JsonNode)new JsonObject
            { ["subject"] = shown[id].Subject, ["provider"] = shown[id].Provider })]));
    }

    // With scopes on, what a record the principal reads cites for derived_subject: its {subject, provider}, but for a
    // save_document copy the principal's own scope (`person`, or the household's), since a copy may be of a private
    // original (memory.md section 8). Null when the principal can't read it, as when there is none.
    public async Task<JsonObject?> Record(Guid principal, Guid id, string? person, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"""
            SELECT r.subject_id,r.provider,coalesce(r.metadata->>'retrievalMode','') IN ({Database.CopyModes})
            FROM resources r WHERE r.id=@id AND {Database.Visible()}
            """);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("principal", principal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return reader.GetBoolean(2) ? new JsonObject { ["subject"] = person ?? Household }
            : new JsonObject { ["subject"] = reader.GetString(0), ["provider"] = reader.GetString(1) };
    }

    // A command's text[] parameter for Stars: null with scopes off.
    public static NpgsqlParameter Parameter(string name, string[]? stars) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)stars ?? DBNull.Value };
}
