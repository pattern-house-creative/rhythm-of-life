using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters.Demo;
using Npgsql;

namespace Knowledge.Mcp;

// The app's glucose reads (app_latest_glucose, app_recent_glucose), over any source that writes metric `glucose`
// (or the older `sensor_glucose`): a plugin through ingest_measurements, a MeasurementSource adapter, or imported
// rows. One person's readings, as the caller reads them: every read takes the one read predicate (Database.Visible,
// docs/dev/memory.md section 7.4), so with memory.scopes off, where the household shares everything, the app may ask
// for anyone in people, and with it on a caller reads only the subjects its scopes hold. Values are stored as the
// source sent them; mmol/L is converted to mg/dL for everything returned here.
// Each provider's readings are its archive or its live feed by the provider's policy (AuthorizedArchive or
// LiveSource, contracts/README.md 3.2), never by its name: the current reading and the 24-hour rows come from live
// feeds, the 14-day series joins one person's archive with their live feeds, and the 14-day totals are the archive's.
// Shapes: contracts/mcp/app_latest_glucose.output.schema.json and app_recent_glucose.output.schema.json.
// Generalized from the private version's reader, which was bound to one device's live and export formats.
// `clock` is the time HasGlucose keeps its answers by; a test steps it, and the engine takes the default, now.
public sealed class AppHealth(Database db, RhythmConfig config, Func<DateTimeOffset>? clock = null)
{
    static readonly string Own = $"{Database.Visible()} AND r.relationship_snapshot IS NULL";
    const string Glucose = "m.metric IN ('glucose','sensor_glucose') AND m.unit IN ('mg/dL','mmol/L') AND m.observed_at IS NOT NULL";
    const string Mgdl = "CASE WHEN m.unit='mmol/L' THEN round((m.value*18.0182)::numeric,1)::double precision ELSE m.value END";
    const string Slot = "date_bin('5 minutes',m.observed_at,timestamptz '2000-01-01')";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public const int Limit = 4200;
    const int Cached = 1000;
    NpgsqlDataSource source => db.Source;
    // Both lookups are each caller's own: with scopes on, what one caller reads is not another's.
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset At, bool Has)> cache = new();
    sealed record Chosen(DateTimeOffset At, string Subject);
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Chosen> chosen = new();

    // A provider's stretch: "archive" or "live" by its policy; null for a reference provider, which holds no readings.
    // A provider nobody registered is live (ProviderPolicies.For).
    public string? Stretch(string provider) => db.Policies.For(provider).Authority switch
    {
        SourceAuthority.AuthorizedArchive => "archive",
        SourceAuthority.LiveSource => "live",
        _ => null,
    };
    // Registered providers that are not live feeds, and those that are archives; every other provider is live.
    string[] NotLive => db.Policies.Providers.Where(p => Stretch(p) != "live").ToArray();
    string[] Archives => db.Policies.Providers.Where(p => Stretch(p) == "archive").ToArray();

    // Whether any glucose source has data this caller reads. Once it has, that is kept for a minute; a "none" is kept for
    // Absent only, the app's server's own wait between listings while no glucose tool is listed (absentTtl in
    // app/server/engine.ts), so a source's first reading, a plugin's or a feed's, is listed at the app's first read after
    // that rather than up to a minute on. Kept at all because the look is not always fast: with memory scopes on, a caller
    // who reads none of the install's glucose rows (another person's private readings) has the read rule checked on each
    // of them before the answer is none, and the app may ask at every listing and every glucose call.
    public static readonly TimeSpan Present = TimeSpan.FromMinutes(1), Absent = TimeSpan.FromSeconds(10);
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    public async Task<bool> HasGlucose(Guid principal, CancellationToken ct)
    {
        if (cache.TryGetValue(principal, out var last) && now() - last.At < (last.Has ? Present : Absent)) return last.Has;
        await using var cmd = source.CreateCommand($"SELECT EXISTS(SELECT 1 FROM measurements m JOIN resources r ON r.id=m.resource_id WHERE {Own} AND {Glucose})");
        cmd.Parameters.AddWithValue("principal", principal);
        var has = await cmd.ExecuteScalarAsync(ct) is true;
        if (cache.Count >= Cached) cache.Clear();
        cache[principal] = (now(), has);
        return has;
    }

    // The person asked for; else the first person (in people's order) with glucose data the caller reads; else, while
    // demo data is on and only it has glucose, the demo subject (its readings say demo: true); else the first person.
    // The app always asks for the default, and finding it reads every glucose reading, so it is looked up at most once
    // a minute for each caller.
    async Task<string> Subject(Guid principal, string? asked, CancellationToken ct)
    {
        if (asked is not null) return asked;
        if (chosen.TryGetValue(principal, out var last) && DateTimeOffset.UtcNow - last.At < TimeSpan.FromMinutes(1)) return last.Subject;
        await using var cmd = source.CreateCommand($"SELECT DISTINCT r.subject_id FROM measurements m JOIN resources r ON r.id=m.resource_id WHERE {Own} AND {Glucose}");
        cmd.Parameters.AddWithValue("principal", principal);
        var with = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) with.Add(reader.GetString(0));
        var subject = config.People.Select(p => p.Id).FirstOrDefault(with.Contains)
            ?? (config.Demo.Enabled && with.Contains(RhythmConfig.Synthetic) ? RhythmConfig.Synthetic : config.FirstPerson);
        if (chosen.Count >= Cached) chosen.Clear();
        chosen[principal] = new(DateTimeOffset.UtcNow, subject);
        return subject;
    }
    static bool Demo(string subject, string? provider) =>
        subject == RhythmConfig.Synthetic || provider is DemoAdapter.Provider;

    // The latest live reading in the private version's shape, which the app's facade reads (app/server/README.md):
    // {selection: "latest", complete: true, subject, rows: [at most one row]}. The row is in mg/dL; `demo` marks the
    // fictional seed, and `stale` says whether it is older than StaleAfter. An archive is never the current reading.
    public async Task<JsonObject> Latest(Guid principal, string? asked, CancellationToken ct)
    {
        var subject = await Subject(principal, asked, ct);
        await using var cmd = source.CreateCommand($"""
            SELECT r.id,r.provider,r.kind,r.source_updated_at,r.timezone,m.sample_key,m.observed_at,m.quality,{Mgdl}
            FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE {Own} AND {Glucose} AND r.subject_id=@s AND m.observed_at<=now()+interval '5 minutes' AND NOT r.provider=ANY(@archived)
            ORDER BY m.observed_at DESC,r.source_updated_at DESC NULLS LAST LIMIT 1
            """);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.AddWithValue("archived", NotLive);
        cmd.Parameters.AddWithValue("principal", principal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new JsonArray();
        var result = new JsonObject { ["selection"] = "latest", ["complete"] = true, ["subject"] = subject, ["rows"] = rows };
        if (!await reader.ReadAsync(ct)) return result;
        var at = reader.GetFieldValue<DateTimeOffset>(6);
        var provider = reader.GetString(1);
        rows.Add(new JsonObject
        {
            ["resource_id"] = reader.GetGuid(0).ToString(), ["subject_id"] = subject, ["provider"] = provider, ["source_kind"] = reader.GetString(2),
            ["timezone"] = reader.IsDBNull(4) ? null : reader.GetString(4),
            ["source_updated_at"] = reader.IsDBNull(3) ? null : Iso(reader.GetFieldValue<DateTimeOffset>(3)),
            ["metric"] = "glucose", ["sample_key"] = reader.GetString(5), ["observed_at"] = Iso(at),
            ["value"] = reader.GetDouble(8), ["unit"] = "mg/dL", ["quality"] = reader.GetString(7), ["total_samples"] = 1,
            ["demo"] = Demo(subject, provider),
            ["stale"] = DateTimeOffset.UtcNow - at > StaleAfter,
        });
        return result;
    }

    sealed record Reading(DateTimeOffset At, double Mgdl, string Quality, Guid Id, string Provider, string Kind,
        string? Zone, DateTimeOffset? Updated, DateTimeOffset Indexed, string Key, DateOnly Date, string Stretch);

    // 24 hours of live readings as rows, or (fortnight) 14 days as [epochSeconds, mg/dL(, -1 below or 1 above the
    // sensor's range)] in 5-minute slots, both in mg/dL. The 14 days are one person's archive up to the first live reading in the
    // window, then their live feeds: never interleaved, one reading per slot (the earliest, in its newest revision).
    // Both forms carry the archive's 14-day totals.
    public async Task<JsonObject> Recent(Guid principal, string? asked, bool fortnight, CancellationToken ct)
    {
        var subject = await Subject(principal, asked, ct);
        // Whole seconds, so every reading before `end` has an epoch second before it too.
        var end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var start = end.AddHours(fortnight ? -336 : -24);
        var result = new JsonObject
        {
            ["subject"] = subject, ["selection"] = "history", ["today"] = config.Today().ToString("yyyy-MM-dd"),
            ["start"] = Iso(start), ["end"] = Iso(end), ["timezone"] = config.Timezone,
            ["range"] = new JsonObject { ["low"] = config.Health.Glucose.Low, ["high"] = config.Health.Glucose.High, ["unit"] = "mg/dL" },
            ["displayUnit"] = config.Health.Glucose.DisplayUnit,
        };
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var rows = new List<Reading>();
        await using (var cmd = Database.Command(c, tx, $"""
            SELECT DISTINCT ON (r.provider,{Slot})
              m.observed_at,{Mgdl},m.quality,r.id,r.provider,r.kind,r.timezone,r.source_updated_at,r.indexed_at,m.sample_key,m.local_date
            FROM measurements m JOIN resources r ON r.id=m.resource_id
            WHERE {Own} AND {Glucose} AND r.subject_id=@s AND m.observed_at>=@start AND m.observed_at<@end AND m.value>0
            ORDER BY r.provider,{Slot},m.observed_at,r.source_updated_at DESC NULLS LAST,r.indexed_at DESC
            """, ("s", subject), ("start", start), ("end", end), ("principal", principal)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (Stretch(reader.GetString(4)) is { } stretch)
                    rows.Add(new(reader.GetFieldValue<DateTimeOffset>(0), reader.GetDouble(1), reader.GetString(2), reader.GetGuid(3),
                        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8),
                        reader.GetString(9), reader.GetFieldValue<DateOnly>(10), stretch));
        var live = OnePerSlot(rows.Where(r => r.Stretch == "live"));
        // The archive ends a second before the slot the live feed starts in, so the two never share a slot or a second.
        var cutoff = live.Count > 0 ? Bin(live[0].At).AddSeconds(-1) : end;
        var archive = OnePerSlot(rows.Where(r => r.Stretch == "archive" && r.At < cutoff));
        var joined = fortnight ? archive.Concat(live).ToList() : live;
        var kept = joined.TakeLast(Limit).ToArray();
        result["complete"] = joined.Count <= Limit;
        result["demo"] = Demo(subject, null) || kept.Length > 0 && kept.All(r => Demo(subject, r.Provider));
        var newest = kept.LastOrDefault(r => r.Stretch == "live");
        result["provider"] = (newest ?? kept.LastOrDefault())?.Provider;
        if (fortnight)
        {
            result["series"] = new JsonArray(kept.Select(r => (JsonNode)(Censored(r.Quality) is var flag && flag != 0
                ? new JsonArray(r.At.ToUnixTimeSeconds(), r.Mgdl, flag) : new JsonArray(r.At.ToUnixTimeSeconds(), r.Mgdl))).ToArray());
            result["intervalMinutes"] = 5;
            result["sources"] = new JsonArray(kept.GroupBy(r => r.Stretch).OrderBy(g => g.Key == "live").Select(g => (JsonNode)new JsonObject
            {
                ["kind"] = g.Key, ["provider"] = g.Last().Provider, ["start"] = Iso(g.First().At), ["end"] = Iso(g.Last().At), ["readings"] = g.Count(),
                ["timeBasis"] = g.Key == "archive" ? "device-local" : "source-zone",
            }).ToArray());
            result["latest"] = newest is null ? null : new JsonObject
            {
                ["resourceId"] = newest.Id.ToString(), ["observedAt"] = Iso(newest.At),
                ["sourceUpdatedAt"] = newest.Updated is { } u ? Iso(u) : null
            };
        }
        else
            result["rows"] = new JsonArray(kept.Select(r => (JsonNode)new JsonObject
            {
                ["resource_id"] = r.Id.ToString(), ["subject_id"] = subject, ["provider"] = r.Provider, ["source_kind"] = r.Kind, ["timezone"] = r.Zone,
                ["source_updated_at"] = r.Updated is { } u ? Iso(u) : null, ["metric"] = "glucose", ["sample_key"] = r.Key,
                ["observed_at"] = Iso(r.At), ["local_date"] = r.Date.ToString("yyyy-MM-dd"), ["value"] = r.Mgdl, ["unit"] = "mg/dL",
                ["value_mgdl"] = r.Mgdl, ["quality"] = r.Quality
            }).ToArray());
        result["lastSyncedAt"] = await LastAccepted(c, tx, principal, subject, ct);
        result["summary"] = await Totals(c, tx, principal, subject, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    // One reading per 5-minute slot across providers: the earliest, in its newest revision. Ascending by time.
    static List<Reading> OnePerSlot(IEnumerable<Reading> readings) => readings.GroupBy(r => Bin(r.At))
        .Select(g => g.OrderBy(r => r.At).ThenByDescending(r => r.Updated ?? DateTimeOffset.MinValue).ThenByDescending(r => r.Indexed).First())
        .OrderBy(r => r.At).ToList();
    static DateTimeOffset Bin(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero);

    // When a glucose provider of the subject last had a batch accepted, as get_sync_status reads ingest freshness: only
    // of providers whose rows the caller reads.
    async Task<string?> LastAccepted(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string subject, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, tx, $"""
            SELECT max(f.last_accepted_at) FROM ingest_freshness f WHERE f.subject_id=@s
              AND {Database.Allowed("f.subject_id", "f.provider", "CASE WHEN f.subject_id='household' THEN 'shared' ELSE 'private' END")}
              AND EXISTS(SELECT 1 FROM resources r JOIN measurements m ON m.resource_id=r.id
                WHERE r.subject_id=f.subject_id AND r.provider=f.provider AND m.metric IN ('glucose','sensor_glucose')
                  AND {Database.Allowed("r.subject_id", "r.provider", "r.scope")})
            """, ("s", subject), ("principal", principal));
        return await cmd.ExecuteScalarAsync(ct) is DateTime at ? Iso(new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))) : null;
    }

    // The archive's latest 14 complete local days within the last 60: one reading per 5-minute slot, banded with the
    // published thresholds (54, low, high, 250 mg/dL; low and high from health.glucose). Censored readings count in
    // the outer bands. Live feeds never count here; with no archive there are no totals (null).
    async Task<JsonNode?> Totals(NpgsqlConnection c, NpgsqlTransaction tx, Guid principal, string subject, CancellationToken ct)
    {
        var today = config.Today();
        await using var cmd = Database.Command(c, tx, $"""
            WITH archive AS (
              SELECT m.local_date,m.observed_at,{Mgdl} AS v,m.quality,coalesce(m.quality ~ 'comparison=[<>]',false) AS b,r.indexed_at
              FROM measurements m JOIN resources r ON r.id=m.resource_id
              WHERE {Own} AND {Glucose} AND r.subject_id=@s AND r.provider=ANY(@archives) AND m.value>0
                AND m.local_date<=@last AND m.local_date>=@oldest),
            latest AS (SELECT max(local_date) AS d FROM archive),
            slots AS (
              SELECT DISTINCT ON (date_bin('5 minutes',a.observed_at,timestamptz '2000-01-01')) a.v,a.quality,a.b
              FROM archive a,latest WHERE a.local_date>latest.d-14 AND a.local_date<=latest.d
              ORDER BY date_bin('5 minutes',a.observed_at,timestamptz '2000-01-01'),a.observed_at,a.indexed_at DESC)
            SELECT (SELECT d FROM latest),(SELECT max(indexed_at) FROM archive),count(*),count(*) FILTER (WHERE NOT b),coalesce(sum(v) FILTER (WHERE NOT b),0),
              count(*) FILTER (WHERE (NOT b AND v<54) OR quality ~ 'comparison=<'),
              count(*) FILTER (WHERE NOT b AND v>=54 AND v<@low),
              count(*) FILTER (WHERE NOT b AND v>=@low AND v<=@high),
              count(*) FILTER (WHERE NOT b AND v>@high AND v<=250),
              count(*) FILTER (WHERE (NOT b AND v>250) OR quality ~ 'comparison=>')
            FROM slots
            """, ("s", subject), ("archives", Archives), ("last", today.AddDays(-1)), ("oldest", today.AddDays(-60)),
            ("low", config.Health.Glucose.Low), ("high", config.Health.Glucose.High), ("principal", principal));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.GetInt64(2) == 0) return null;
        var last = reader.GetFieldValue<DateOnly>(0);
        return new JsonObject
        {
            ["startDate"] = last.AddDays(-13).ToString("yyyy-MM-dd"), ["endDate"] = last.ToString("yyyy-MM-dd"), ["intervalMinutes"] = 5,
            ["sourceKind"] = "archive", ["timeBasis"] = "device-local-unverified",
            ["lastImportedAt"] = reader.IsDBNull(1) ? null : Iso(reader.GetFieldValue<DateTimeOffset>(1)),
            ["readings"] = reader.GetInt64(2), ["exactReadings"] = reader.GetInt64(3), ["exactSum"] = reader.GetDouble(4),
            ["bands"] = new JsonObject { ["veryLow"] = reader.GetInt64(5), ["low"] = reader.GetInt64(6), ["inRange"] = reader.GetInt64(7),
                ["high"] = reader.GetInt64(8), ["veryHigh"] = reader.GetInt64(9) },
            ["gmi"] = new JsonObject { ["minDays"] = config.Health.Gmi.MinDays, ["minCoverage"] = config.Health.Gmi.MinCoverage },
        };
    }

    static int Censored(string quality) => quality.Contains("comparison=<", StringComparison.Ordinal) ? -1
        : quality.Contains("comparison=>", StringComparison.Ordinal) ? 1 : 0;
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
