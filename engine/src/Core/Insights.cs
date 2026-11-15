using System.Text.Json;
using System.Text.Json.Serialization;
using Knowledge.Relationships;
using Npgsql;

namespace Knowledge;

// Daily insights. The fields a harness supplies when it saves one;
// the store adds the id, status, feedback and timestamps. Text is untrusted data wherever it is shown.
public sealed record InsightPoint(string Text, string Type, int? Source = null);
public sealed record InsightStar(string GraphId, string Title);
public sealed record InsightSource(string Kind, string Title, string? Url = null, string? Locator = null,
    DateTimeOffset? PublishedAt = null, DateTimeOffset? RetrievedAt = null);
public sealed record InsightDraft(string Kind, string Title, string Summary, string WhyItMatters, string NextStep,
    IReadOnlyList<InsightPoint> Points, string EvidenceLevel, string Limitations, IReadOnlyList<InsightStar> Stars,
    IReadOnlyList<InsightSource> Sources, string GeneratedBy, DateTimeOffset GeneratedAt, DateTimeOffset SourcesAsOf, Guid? Supersedes)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    // The first problem found, in words a harness can act on; null when the draft is valid.
    public string? Problem(DateTimeOffset now)
    {
        static bool Text(string? value, int max, bool required = true) => value is not null && value.Length <= max && (!required || value.Trim().Length > 0);
        if (Kind is not ("focus" or "discovery" or "suggestion")) return "kind must be focus, discovery or suggestion";
        if (!Text(Title, 120)) return "title is required, at most 120 characters";
        if (!Text(Summary, 600)) return "summary is required, at most 600 characters";
        if (!Text(WhyItMatters, 600, false)) return "whyItMatters is at most 600 characters";
        if (!Text(NextStep, 400, false)) return "nextStep is at most 400 characters";
        if (!Text(Limitations, 400, false)) return "limitations is at most 400 characters";
        if (EvidenceLevel is not ("strong" or "moderate" or "weak")) return "evidenceLevel must be strong, moderate or weak";
        if (!Label(GeneratedBy)) return "generatedBy is a short label: a lowercase letter, then up to 39 lowercase letters, digits or hyphens (default harness-daily)";
        if (Points is null || Points.Count > 12) return "points holds at most 12 entries";
        if (Stars is null || Stars.Count > 20) return "stars holds at most 20 entries";
        if (Sources is null || Sources.Count > 20) return "sources holds at most 20 entries";
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p is null || !Text(p.Text, 400)) return $"points[{i}].text is required, at most 400 characters";
            if (p.Type is not ("observed" or "sourced" or "inferred" or "hypothesis")) return $"points[{i}].type must be observed, sourced, inferred or hypothesis";
            if (p.Source is { } s && (s < 0 || s >= Sources.Count)) return $"points[{i}].source must be an index into sources";
        }
        for (var i = 0; i < Stars.Count; i++)
            if (Stars[i] is not { } s || !Text(s.GraphId, 1000) || !Text(s.Title, 200)) return $"stars[{i}] needs graphId (at most 1000 characters) and title (at most 200)";
        for (var i = 0; i < Sources.Count; i++)
        {
            var s = Sources[i];
            if (s is null || !SourceKinds.Contains(s.Kind)) return $"sources[{i}].kind must be graph, record, document, memory or web";
            if (!Text(s.Title, 200)) return $"sources[{i}].title is required, at most 200 characters";
            if (s.Url is not null ? !Https(s.Url) : s.Kind is "web" or "drive") return $"sources[{i}].url must be an https URL" + (s.Kind is "web" ? " for web sources" : "");
            if (s.Locator is not null && !Text(s.Locator, 300)) return $"sources[{i}].locator is at most 300 characters";
            if (s.RetrievedAt is not { } retrieved || retrieved > now.AddMinutes(5)) return $"sources[{i}].retrievedAt is required and not in the future";
            if (s.PublishedAt > now.AddMinutes(5)) return $"sources[{i}].publishedAt is in the future";
        }
        if (GeneratedAt > now.AddMinutes(5)) return "generatedAt is in the future";
        if (SourcesAsOf > now.AddMinutes(5)) return "sourcesAsOf is in the future";
        return null;
    }
    // Where a point's evidence came from. The last three are the private version's names, still accepted.
    public static readonly HashSet<string> SourceKinds = ["graph", "record", "document", "memory", "web", "nas", "drive", "second-brain"];
    public const string DefaultLabel = "harness-daily";
    public static bool Label(string? value) => value is { Length: >= 1 and <= 40 }
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,39}$");
    static bool Https(string url) => url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo == "";
}

// Saves, lists and records feedback through the bounded database functions, which check the caller's role and, from
// migration 027, the insight's subject and with memory.scopes on the stars it cites (DerivedScopes; docs/dev/memory.md
// sections 5 and 8). `host` is the map that says which stars a caller reads; without it nothing citing a star is read
// with scopes on.
public sealed partial class InsightStore(NpgsqlDataSource writer, RelationshipHost? host = null)
{
    readonly DerivedScopes scopes = new(writer, host);
    public static readonly string[] Feedback = ["interesting", "explore", "not_useful", "later", "handled"];
    public static readonly string[] Statuses = ["new", "saved", "explored", "snoozed", "dismissed", "superseded", "handled"];
    public static readonly string[] Outcomes = ["saved", "quiet", "blocked"];
    // A note as stored, trimmed; false when one was given but is not 1-500 characters once trimmed.
    public static bool Note(string? value, out string? note) { note = value?.Trim(); return note is null or { Length: >= 1 and <= 500 }; }
    // With scopes on it lands in the narrowest scope of what it draws on (memory.md section 5): the stars it cites (each
    // one its saver reads now), and its sources. A source not on the web counts as the star its locator names, when the
    // saver reads that star; as the record its locator names, when the saver reads that record; and otherwise as the
    // saver's own scope, as an insight citing no star does. So a private record cited only among the sources narrows it.
    // The stars and records its sources named are kept (`checked`), and every read checks them again, as it checks the
    // stars (insight_readable, memory.md section 8): the insight narrows when one of them does.
    public async Task<Guid> Save(Guid principal, InsightDraft draft, CancellationToken ct)
    {
        var located = draft.Sources.Where(s => s.Kind != "web" && s.Locator is not null).Select(s => s.Locator!);
        var (stars, cited) = await scopes.Citing(principal, draft.Stars.Select(s => s.GraphId).Concat(located), ct);
        System.Text.Json.Nodes.JsonArray? named = null;
        if (stars is not null)
        {
            named = [];
            var shown = stars.ToHashSet(StringComparer.Ordinal);
            var person = (await scopes.Caller(principal, ct)).Person;
            foreach (var source in draft.Sources.Where(s => s.Kind != "web"))
            {
                if (source.Locator is { } star && shown.Contains(star)) { named.Add(star); continue; }
                cited ??= [];
                if (source.Locator is { } id && Guid.TryParse(id, out var record) && await scopes.Record(principal, record, person, ct) is { } row)
                {
                    cited.Add(row);
                    named.Add(record.ToString());
                }
                else cited.Add(new System.Text.Json.Nodes.JsonObject { ["subject"] = person ?? DerivedScopes.Household });
            }
        }
        var insight = JsonSerializer.SerializeToNode(draft, InsightDraft.Json)!.AsObject();
        if (cited is not null) insight["cited"] = cited;
        if (named is not null) insight["checked"] = named;
        await using var cmd = writer.CreateCommand("SELECT save_insight(@p,@i::jsonb,@s)");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("i", insight.ToJsonString());
        // The insight it supersedes is checked as a read is.
        cmd.Parameters.Add(DerivedScopes.Parameter("s", draft.Supersedes is null ? stars : await Readable(principal, stars, ct)));
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // With scopes on, what insight_readable checks an insight against (migration 027): the stars the caller reads now
    // and, beside them, the ids of the records it reads (Database.Visible) among those the insights' sources name, by
    // what each named at save or, for one saved with scopes off, by its every locator. Null with scopes off.
    async Task<string[]?> Readable(Guid principal, string[]? stars, CancellationToken ct)
    {
        if (stars is null) return null;
        await using var cmd = writer.CreateCommand($$"""
            SELECT r.id::text FROM resources r WHERE r.id IN (
              SELECT CASE WHEN l ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN l::uuid END
              FROM insights i CROSS JOIN LATERAL unnest(coalesce(i.sources_checked,insight_locators(i.sources))) l)
            AND {{Database.Visible()}}
            """);
        cmd.Parameters.AddWithValue("principal", principal);
        var readable = new List<string>(stars);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) readable.Add(reader.GetString(0));
        return [.. readable];
    }
    async Task<string[]?> Readable(Guid principal, CancellationToken ct) => await Readable(principal, await scopes.Stars(principal, ct), ct);
    // A JSON array of insight records, newest first.
    public async Task<string> List(Guid principal, string? status, DateTimeOffset? since, int limit, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT read_insights(@p,@status,@since,@limit,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("since", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)since?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await Readable(principal, ct)));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    // The updated record, or null when no insight this caller reads has this id. keep: also in the insight's history
    // (migration 066), which only memory.tools on asks for; the answer is the same.
    public async Task<string?> Record(Guid principal, Guid id, string value, DateTimeOffset? until, string? note, CancellationToken ct, bool keep = false)
    {
        await using var cmd = writer.CreateCommand(keep ? "SELECT insight_feedback_kept(@p,@id,@value,@until,@note,@s,'app',NULL)::text"
            : "SELECT insight_feedback(@p,@id,@value,@until,@note,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("value", value);
        cmd.Parameters.AddWithValue("until", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)until?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("note", NpgsqlTypes.NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await Readable(principal, ct)));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    // A harness's record of how one run ended; the run's id.
    public async Task<Guid> Log(Guid principal, string generatedBy, string outcome, string? note, Guid? insight, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT log_insight_run(@p,@by,@outcome,@note,@insight,@s)");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("by", generatedBy); cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("note", NpgsqlTypes.NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("insight", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)insight ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", insight is null ? null : await Readable(principal, ct)));
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }
    // A JSON array of the newest runs, newest first.
    public async Task<string> Runs(Guid principal, int limit, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT read_insight_runs(@p,@limit,@s)::text");
        cmd.Parameters.AddWithValue("p", principal); cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await Readable(principal, ct)));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
