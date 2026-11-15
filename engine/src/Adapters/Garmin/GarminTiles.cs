using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Knowledge.Adapters.Garmin;

// The app's Garmin tiles (contracts/README.md 2.2), registered only while the adapter is on:
//   app_garmin_summary       the owner's last 14 days in the private version's reader shape, which the app's facade
//                            reads (app/server/README.md): {subject, selection: "garmin", complete, rows, today,
//                            startDate, endDate, start, end, coverageComplete, activityCount, lastSyncedAt, summary}
//   app_request_garmin_sync  a "sync now" request (request_garmin_sync: one two-day job, at most every 15 minutes)
// Both take no arguments, or `subject` naming the adapter's owner. The summary reads the owner's rows as the caller reads
// them, through the one read predicate (Database.Visible, docs/dev/memory.md section 7.4), and their sync state as
// get_sync_status does. Ported from the private version's Garmin reader, which was bound to one principal, one person
// and one time zone.
public static class GarminTiles
{
    public const string Summary = "app_garmin_summary", SyncNow = "app_request_garmin_sync";
    // What the tiles read. Heart rates are stored in bpm (the ingest contract's unit) and handed over as beats/min,
    // the unit the facade reads.
    static readonly string[] Metrics = ["steps", "sleep_duration", "resting_heart_rate", "hrv_overnight", "intensity_moderate", "intensity_vigorous",
        "activity_duration", "activity_distance", "activity_training_load", "training_effect_aerobic", "training_effect_anaerobic", "activity_heart_rate"];
    static readonly string[] Units = ["count", "s", "bpm", "ms", "min", "min", "s", "m", "score", "score", "score", "bpm"];
    const int MaxRows = 2000;

    public static IEnumerable<AppTool> Tools(GarminAdapter garmin)
    {
        var schema = new { type = "object", additionalProperties = false, properties = new { subject = new { type = "string", @const = garmin.Owner } } };
        bool Allowed(JsonElement args) => args.EnumerateObject().All(x => x.Name == "subject" && x.Value.ValueKind == JsonValueKind.String && x.Value.GetString() == garmin.Owner);
        yield return new(Summary,
            "Garmin's last 14 days for the person whose Garmin this is (the default subject): daily steps, sleep, resting heart rate, overnight HRV, intensity minutes, and recorded activities with duration, distance, heart rate, training load and effect. Each row carries its source record, calendar day and time zone; coverageComplete says whether this week was synced through today, and lastSyncedAt when Garmin last synced.",
            schema, true, Allowed, (principal, _, ct) => Read(garmin, principal, ct), "Garmin's 14-day summary is in structuredContent.", ["observatory_garmin_summary"]);
        yield return new(SyncNow,
            "Ask for a Garmin sync of yesterday and today now instead of at the next regular poll. At most one request every 15 minutes, and none while a Garmin job is unfinished. Returns accepted, pendingSince, nextAllowedAt and lastSyncedAt.",
            schema, false, Allowed, (principal, _, ct) => Request(garmin, principal, ct), "Whether the Garmin sync was accepted is in structuredContent.",
            ["observatory_request_garmin_sync"]);
    }

    public static async Task<JsonObject> Read(GarminAdapter garmin, Guid principal, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var today = garmin.Today(now);
        var first = today.AddDays(-13);
        var week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        await using var cmd = garmin.Source.CreateCommand($$"""
            WITH samples AS (
              SELECT r.id AS resource_id,r.subject_id,r.provider,r.kind AS source_kind,r.timezone,r.source_updated_at,m.metric,m.sample_key,
                m.observed_at,m.local_date,m.value,CASE WHEN m.unit='bpm' THEN 'beats/min' ELSE m.unit END AS unit,m.quality,
                CASE WHEN r.kind='activity' AND (r.metadata->'details'->>'activityType') ~ '^[a-z0-9_]{1,40}$' THEN r.metadata->'details'->>'activityType' END AS activity_type,
                CASE WHEN r.kind='activity' AND r.metadata->'details'->>'poolUnit' IN ('meter','yard') THEN r.metadata->'details'->>'poolUnit' END AS pool_unit,
                CASE WHEN r.kind='activity' AND r.metadata->'details'->>'multisport' IN ('parent','leg') THEN r.metadata->'details'->>'multisport' END AS multisport
              FROM measurements m JOIN resources r ON r.id=m.resource_id
              WHERE r.subject_id=@s AND r.provider='garmin' AND {{Database.Visible()}}
                AND r.relationship_snapshot IS NULL AND r.kind IN ('metric','activity') AND m.local_date BETWEEN @first AND @today
                AND (m.metric,m.unit) IN (SELECT * FROM unnest(@metrics::text[],@units::text[]))
            ), bounded AS (SELECT * FROM samples ORDER BY local_date DESC,observed_at DESC NULLS LAST,resource_id,metric LIMIT {{MaxRows + 1}})
            SELECT (SELECT count(*)<={{MaxRows}} FROM bounded),
              coalesce((SELECT jsonb_agg(x) FROM (SELECT * FROM bounded LIMIT {{MaxRows}}) x),'[]'::jsonb)::text,
              (SELECT count(DISTINCT resource_id) FROM samples WHERE source_kind='activity'),
              EXISTS(SELECT 1 FROM sync_jobs j WHERE j.subject_id=@s AND j.provider='garmin' AND j.from_date<=@week AND j.next_date>@today
                AND {{Database.Allowed("j.subject_id", "j.provider", "'private'")}}),
              greatest((SELECT max(j.updated_at) FROM sync_jobs j WHERE j.subject_id=@s AND j.provider='garmin' AND j.status='complete'
                  AND j.last_error IS NULL AND {{Database.Allowed("j.subject_id", "j.provider", "'private'")}}),
                (SELECT max(f.last_accepted_at) FROM ingest_freshness f WHERE f.plugin='garmin' AND f.provider='garmin' AND f.subject_id=@s
                  AND {{Database.Allowed("f.subject_id", "f.provider", "CASE WHEN f.subject_id='household' THEN 'shared' ELSE 'private' END")}}))
            """);
        cmd.Parameters.AddWithValue("s", garmin.Owner); cmd.Parameters.AddWithValue("first", first); cmd.Parameters.AddWithValue("today", today);
        cmd.Parameters.AddWithValue("week", week); cmd.Parameters.AddWithValue("metrics", Metrics); cmd.Parameters.AddWithValue("units", Units);
        cmd.Parameters.AddWithValue("principal", principal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["subject"] = garmin.Owner, ["selection"] = "garmin", ["complete"] = reader.GetBoolean(0), ["rows"] = JsonNode.Parse(reader.GetString(1)),
            ["today"] = Day(today), ["startDate"] = Day(first), ["endDate"] = Day(today.AddDays(1)),
            ["start"] = GarminAdapter.Iso(now.AddHours(-24)), ["end"] = GarminAdapter.Iso(now),
            ["coverageComplete"] = reader.GetBoolean(3), ["activityCount"] = reader.GetInt64(2),
            ["lastSyncedAt"] = reader.IsDBNull(4) ? null : GarminAdapter.Iso(reader.GetFieldValue<DateTimeOffset>(4)),
            ["summary"] = null,
        };
    }

    // {accepted, pendingSince, nextAllowedAt, lastSyncedAt}; the database function checks the caller's role. Its answer
    // names the owner's sync jobs, so it takes the predicate as the summary does (memory.md 7.2): a caller who can't
    // read the owner's Garmin rows (only with memory.scopes on) queues nothing and is refused, as the role check refuses.
    public static async Task<JsonObject> Request(GarminAdapter garmin, Guid principal, CancellationToken ct)
    {
        await using var cmd = garmin.Source.CreateCommand($"""
            SELECT CASE WHEN {Database.Allowed("@s", "'garmin'", "'private'")} THEN request_garmin_sync(@principal,@s)::text END
            """);
        cmd.Parameters.AddWithValue("principal", principal); cmd.Parameters.AddWithValue("s", garmin.Owner);
        return await cmd.ExecuteScalarAsync(ct) is string answer ? (JsonObject)JsonNode.Parse(answer)!
            : throw new PostgresException("A Garmin sync is not available to this login", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege);
    }
}
