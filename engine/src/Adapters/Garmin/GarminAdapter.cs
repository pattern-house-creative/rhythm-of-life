using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Knowledge.Adapters.Garmin;

// Garmin as a MeasurementSource (docs/adapters/garmin.md). One person's numbers, provider `garmin` (archive, health):
// sleep, HRV, heart rate, body battery, daily totals, and activities with their training load and effect.
//   Personal API: with the person's own key (secret garmin_api_key), a sync job reads each day of its range and
//                 writes it through the ingest rules. A regular job covers the last 7 days every pollSeconds (the
//                 first one historyDays); the app's "sync now" adds a two-day job (request_garmin_sync). Jobs keep a
//                 checkpoint per day, so a job cut short (a time budget per tick, a restart, Garmin asking to wait)
//                 resumes where it stopped. Their state is what get_sync_status shows.
//   Export files: Garmin Connect's own exports, read from importPath (GarminExport), with no developer access.
// Runs on the engine's scheduler: Sync is called every minute and does nothing until a file, a job or the poll is due.
// Ported from the private version's Garmin worker, which ran as its own container for one fixed person.
public sealed class GarminAdapter : IMeasurementSource, ISyncNow
{
    public const string AdapterName = "garmin", Provider = "garmin";
    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "health") };
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    // Each tick works at most this long, a day at a time, so the scheduler's other jobs keep their clock.
    static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);
    const int RegularDays = 7, MaxAttempts = 5;

    public string Owner { get; private set; } = "";
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;
    public string ZoneName { get; private set; } = "UTC";
    NpgsqlDataSource source = null!;
    internal NpgsqlDataSource Source => source;
    GarminClient client = null!;
    GarminExport export = null!;
    AdapterSettings settings = null!;
    TimeSpan poll;
    int historyDays;
    DateTimeOffset nextRegular = DateTimeOffset.MinValue;
    readonly HttpMessageHandler? handler;
    readonly Func<TimeSpan, CancellationToken, Task>? wait;

    public GarminAdapter() { }
    // For tests: a fake Garmin behind the handler, and no real waiting between requests.
    public GarminAdapter(HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? wait = null) { this.handler = handler; this.wait = wait; }

    public void Configure(AdapterContext context)
    {
        settings = context.Settings;
        var config = context.Config;
        Owner = settings.Owner ?? config.FirstPerson;
        if (!config.People.Any(p => p.Id == Owner)) throw new ConfigException("adapters.garmin.owner must be a person id: Garmin data is one person's");
        var seconds = settings.Int("pollSeconds", 21600);
        if (seconds is < 3600 or > 86400) throw new ConfigException("adapters.garmin.pollSeconds is 3600-86400");
        poll = TimeSpan.FromSeconds(seconds);
        historyDays = settings.Int("historyDays", 14);
        if (historyDays is < 1 or > 365) throw new ConfigException("adapters.garmin.historyDays is 1-365");
        ZoneName = settings.String("deviceTimezone") is { Length: > 0 } zone ? zone : config.Timezone;
        try { Zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneName); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ConfigException("adapters.garmin.deviceTimezone is not a known IANA time zone"); }
        var unit = settings.String("distanceUnit") ?? "km";
        if (unit is not ("km" or "mi")) throw new ConfigException("adapters.garmin.distanceUnit is km or mi");
        source = context.Db.Source;
        var http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient(handler, false);
        client = new GarminClient(http, Key, wait is null ? null : TimeSpan.Zero, wait);
        export = new GarminExport(this, settings.String("importPath") is { Length: > 0 } path ? path : "/data/import/garmin", unit);
    }

    // The person's key, read from secrets/adapters/garmin_api_key (or RHYTHM_GARMIN_API_KEY_FILE) each time it is needed.
    string? Key()
    {
        var path = settings.SecretFile("api_key");
        if (path is null || Directory.Exists(path) || !File.Exists(path)) return null;
        try { return SecretFiles.Read(path) is { Length: > 0 } key ? key : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public IEnumerable<AppTool> AppTools() => GarminTiles.Tools(this);

    // For tests: the regular poll is due now.
    internal void PollNow() => nextRegular = DateTimeOffset.MinValue;

    public DateOnly Today(DateTimeOffset? now = null) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, Zone).DateTime);

    public async Task Sync(MeasurementSink sink, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        bool Late() => Stopwatch.GetElapsedTime(started) > Budget;
        await export.Import(sink, Late, ct);
        if (Key() is null) { await CloseRequests(ct); return; }
        if (await NextJob(ct) is { } job) await Run(job, sink, Late, ct);
    }

    // Sync now from the Sources view (SB-P6-09): the regular tick alone reads Garmin only when the poll is due, so an
    // asked-for batch first asks for the two-day job the Garmin tiles' "sync now" asks for (request_garmin_sync, as the
    // caller: at most one every 15 minutes, none while a job is unfinished), then works one tick, which runs it (or the
    // unfinished job) within its budget. When Garmin's own rule refuses the job and none is unfinished (one was asked for
    // in the last 15 minutes, by the tiles too, which the Sources view's limit knows nothing of), the batch reaches
    // Garmin for nothing: it does no tick and says so, so the Sources view never says a sync ran.
    public TimeSpan SyncLimit => TimeSpan.FromMinutes(15);
    public async Task<bool> SyncNow(Guid principal, MeasurementSink sink, CancellationToken ct)
    {
        var asked = await GarminTiles.Request(this, principal, ct);
        if (asked["accepted"]?.GetValue<bool>() != true && asked["pendingSince"] is null) return false;
        await Sync(sink, ct);
        return true;
    }

    // Without a key there is nothing to fetch: a "sync now" request is answered by the folder scan just made. Its days
    // stay unchecked (next_date is left), so the tiles never claim Garmin coverage an export cannot give.
    async Task CloseRequests(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            WITH done AS (UPDATE sync_jobs SET status='complete',updated_at=now(),last_error=NULL
              WHERE subject_id=@s AND provider='garmin' AND status IN ('pending','running') RETURNING id)
            INSERT INTO sync_events(job_id,event) SELECT id,'export_scanned' FROM done
            """);
        cmd.Parameters.AddWithValue("s", Owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // A job to work on now: an unfinished one (a "sync now" request first), else, when the poll is due, a failed one
    // whose wait is over, else a new regular job. A job that failed MaxAttempts times (attempts counts failures) is
    // given up (complete, its error kept) so it stops blocking "sync now".
    async Task<Guid?> NextJob(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        async Task<Guid?> One(string sql)
        {
            await using var cmd = Database.Command(c, null, sql, ("s", Owner));
            return await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        if (await One("""
            SELECT id FROM sync_jobs WHERE subject_id=@s AND provider='garmin' AND status IN ('pending','running')
            ORDER BY coalesce(metadata->>'reason','')='on-demand' DESC,updated_at LIMIT 1
            """) is { } open) return open;
        var now = DateTimeOffset.UtcNow;
        if (now < nextRegular) return null;
        nextRegular = now + poll;
        await using (var giveUp = Database.Command(c, null, """
            WITH gone AS (UPDATE sync_jobs SET status='complete',updated_at=now()
              WHERE subject_id=@s AND provider='garmin' AND status='failed' AND attempts>=@max RETURNING id)
            INSERT INTO sync_events(job_id,event) SELECT id,'abandoned' FROM gone
            """, ("s", Owner), ("max", MaxAttempts)))
            await giveUp.ExecuteNonQueryAsync(ct);
        if (await One("""
            SELECT id FROM sync_jobs WHERE subject_id=@s AND provider='garmin' AND status='failed' AND (not_before IS NULL OR not_before<=now())
            ORDER BY updated_at LIMIT 1
            """) is { } retry) return retry;
        // Garmin asked to wait: the next regular round is when it said, and nothing new is started meanwhile.
        await using (var deferred = Database.Command(c, null, "SELECT min(not_before) FROM sync_jobs WHERE subject_id=@s AND provider='garmin' AND status='failed'", ("s", Owner)))
            if (await deferred.ExecuteScalarAsync(ct) is DateTime until)
            {
                var at = new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
                if (at < nextRegular) nextRegular = at;
                return null;
            }
        var today = Today(now);
        // The first job that reads Garmin itself (not a "sync now" answered from files) reaches back historyDays.
        var first = await One("SELECT id FROM sync_jobs WHERE subject_id=@s AND provider='garmin' AND coalesce(metadata->>'reason','')<>'on-demand' LIMIT 1") is null;
        var from = today.AddDays(-(first ? historyDays : RegularDays) + 1);
        var id = Guid.NewGuid();
        await using var create = Database.Command(c, null, """
            INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date,metadata) VALUES(@id,@s,'garmin',@from,@until,@from,
              jsonb_build_object('reason',@reason))
            """, ("id", id), ("s", Owner), ("from", from), ("until", today), ("reason", first ? "history" : "regular"));
        await create.ExecuteNonQueryAsync(ct);
        return id;
    }

    // One job, a day at a time from its checkpoint. Each day is fetched whole before anything is written, then
    // checkpointed; a later failure leaves the committed days and records why (a code, never a payload).
    async Task Run(Guid id, MeasurementSink sink, Func<bool> late, CancellationToken ct)
    {
        DateOnly next, until;
        await using (var claim = source.CreateCommand("""
            UPDATE sync_jobs SET status='running',updated_at=now() WHERE id=@id AND provider='garmin' AND status<>'complete'
            RETURNING next_date,until_date
            """))
        {
            claim.Parameters.AddWithValue("id", id);
            await using var reader = await claim.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return;
            (next, until) = (reader.GetFieldValue<DateOnly>(0), reader.GetFieldValue<DateOnly>(1));
        }
        try
        {
            for (var date = next; date <= until; date = date.AddDays(1))
            {
                var day = await Fetch(date, ct);
                foreach (var write in day) await Write(sink, write, ct);
                await Reconcile(date, day.Where(w => w.Kind == "activity").Select(w => w.ExternalId).ToArray(), ct);
                await Update(id, "running", null, null, date.AddDays(1), "day_committed", ct);
                if (late() && date < until) { await Update(id, "pending", null, null, null, "paused", ct); return; }
            }
            await Update(id, "complete", null, null, null, "complete", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await Update(id, "pending", null, null, null, "interrupted", CancellationToken.None); throw; }
        // Anything else fails the job, an unexpected kind included, so its attempts count and a day that keeps failing
        // is given up after MaxAttempts instead of being fetched again every minute.
        catch (Exception e) when (e is GarminException or HttpRequestException or JsonException or Ingest.Refused || !ct.IsCancellationRequested)
        {
            var code = e switch
            {
                GarminException g => g.Code, HttpRequestException => "garmin_unreachable", JsonException => "garmin_contract_mismatch",
                Ingest.Refused => "garmin_sample_refused", OperationCanceledException => "garmin_timeout", _ => "garmin_unexpected"
            };
            await Update(id, "failed", code, (e as GarminException)?.RetryAt, null, "failed", CancellationToken.None);
            if (code is "garmin_credentials_missing" or "garmin_credentials_rejected")
                throw new ConfigException($"Garmin refused the key in secrets/adapters/garmin_api_key ({code}); replace the file");
            // A request that timed out is Garmin's failure, never the engine stopping.
            if (e is OperationCanceledException) throw new GarminException(code);
            throw;
        }
    }

    async Task Update(Guid id, string status, string? error, DateTimeOffset? retry, DateOnly? next, string happened, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            UPDATE sync_jobs SET status=@status,last_error=@error,not_before=@retry,next_date=coalesce(@next,next_date),
              attempts=attempts+CASE WHEN @status='failed' THEN 1 ELSE 0 END,updated_at=now() WHERE id=@id;
            INSERT INTO sync_events(job_id,event) VALUES(@id,@event)
            """);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("retry", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)retry?.ToUniversalTime() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("next", NpgsqlTypes.NpgsqlDbType.Date) { Value = (object?)next ?? DBNull.Value });
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("event", happened);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The day paths this adapter reads; each is one source resource per day (kind metric).
    public static IReadOnlyList<(string Stream, string Path)> DailyPaths(DateOnly date)
    {
        var d = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return
        [
            ("daily", $"/usersummary-service/usersummary/daily?calendarDate={d}"),
            ("sleep", $"/sleep-service/sleep/dailySleep?date={d}"),
            ("hrv", $"/hrv-service/hrv/{d}"),
            ("heart", $"/wellness-service/wellness/dailyHeartRate?date={d}"),
            ("body-battery", $"/wellness-service/wellness/bodyBattery/reports/daily?startDate={d}&endDate={d}"),
        ];
    }

    async Task<List<SourceWrite>> Fetch(DateOnly date, CancellationToken ct)
    {
        var writes = new List<SourceWrite>();
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var (stream, path) in DailyPaths(date))
        {
            if (await client.Get(path, ct) is not { } payload) continue;
            using var json = JsonDocument.Parse(payload.Bytes);
            var samples = GarminNormalize.Metrics(stream, json.RootElement);
            if (samples.Count > 0)
                writes.Add(new($"{stream}:{day}", "metric", $"Garmin {stream}, {Long(date)}", GarminClient.Origin + path, date, samples, null));
        }
        var seen = new HashSet<long>();
        for (var offset = 0; ; offset += 50)
        {
            if (offset >= 10000) throw new GarminException("activity_page_limit");
            var path = $"/activitylist-service/activities/search/activities?start={offset}&limit=50&startDate={day}&endDate={day}&sortOrder=asc&excludeChildren=false";
            var page = await client.Get(path, ct) ?? throw new GarminException("activity_list_unavailable");
            using var json = JsonDocument.Parse(page.Bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new GarminException("activity_contract_mismatch");
            var activities = json.RootElement.EnumerateArray().ToArray();
            if (activities.Length == 0) break;
            var added = 0;
            foreach (var activity in activities)
            {
                if (!activity.TryGetProperty("activityId", out var a) || !a.TryGetInt64(out var activityId)) throw new GarminException("activity_contract_mismatch");
                if (!seen.Add(activityId)) continue;
                added++;
                var samples = GarminNormalize.Metrics("activity", activity);
                if (samples.Count == 0 || samples[0].At is null) continue;
                writes.Add(new($"activity:{activityId}", "activity", $"Garmin activity, {Long(date)}",
                    $"https://connect.garmin.com/modern/activity/{activityId}", date, samples, GarminNormalize.Details(activity)));
            }
            if (added == 0) throw new GarminException("activity_pagination_stalled");
            if (activities.Length < 50) break;
        }
        return writes;
    }

    // An activity Garmin no longer lists for a day it was synced on leaves the tiles (its archived originals stay).
    async Task Reconcile(DateOnly date, string[] present, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            UPDATE resources r SET status='missing',indexed_at=now() WHERE r.subject_id=@s AND r.provider='garmin' AND r.kind='activity'
              AND r.status='current' AND r.external_id LIKE 'activity:%' AND NOT (r.external_id=ANY(@present))
              AND EXISTS(SELECT 1 FROM measurements m WHERE m.resource_id=r.id AND m.local_date=@date)
            """);
        cmd.Parameters.AddWithValue("s", Owner); cmd.Parameters.AddWithValue("present", present); cmd.Parameters.AddWithValue("date", date);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // One source resource and its samples, as ingest_measurements arguments. Daily totals carry the start of their
    // calendar day (in the device's time zone) as observedAt and the day itself as localDate. A source whose content
    // is unchanged since the last write is skipped, so the archive keeps one original per change.
    public sealed record SourceWrite(string ExternalId, string Kind, string Title, string? Url, DateOnly Date, IReadOnlyList<GarminSample> Samples, JsonObject? Details);

    public async Task<bool> Write(MeasurementSink sink, SourceWrite write, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var dayStart = new DateTimeOffset(write.Date.ToDateTime(TimeOnly.MinValue), Zone.GetUtcOffset(write.Date.ToDateTime(TimeOnly.MinValue)));
        var measurements = new JsonArray(write.Samples.Where(s => s.At is null || s.At <= now.AddMinutes(5)).Take(Ingest.MaxMeasurements).Select(s => (JsonNode)new JsonObject
        {
            ["metric"] = s.Metric, ["value"] = s.Value, ["unit"] = s.Unit, ["observedAt"] = Iso(s.At ?? dayStart),
            ["localDate"] = write.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["key"] = s.Key, ["quality"] = "ok",
        }).ToArray());
        if (measurements.Count == 0) return false;
        var device = write.Samples.Select(s => s.Device).FirstOrDefault(d => d is { Length: > 0 and <= 200 });
        var revision = "sha256:" + Hashing.Hash(new JsonArray(write.Kind, write.Title, write.Url, measurements.DeepClone(), write.Details?.DeepClone(), device).ToJsonString());
        await using (var stored = source.CreateCommand("SELECT revision FROM resources WHERE subject_id=@s AND provider='garmin' AND external_id=@e AND status='current'"))
        {
            stored.Parameters.AddWithValue("s", Owner); stored.Parameters.AddWithValue("e", write.ExternalId);
            if (await stored.ExecuteScalarAsync(ct) as string == revision) return false;
        }
        var origin = new JsonObject
        {
            ["externalId"] = write.ExternalId, ["kind"] = write.Kind, ["title"] = write.Title, ["fetchedAt"] = Iso(now),
            ["revision"] = revision, ["timezone"] = ZoneName,
        };
        if (write.Url is not null) origin["url"] = write.Url;
        if (device is not null) origin["device"] = new JsonObject { ["id"] = device };
        var args = new JsonObject
        {
            ["provider"] = Provider, ["subject"] = Owner,
            ["idempotencyKey"] = $"garmin:{write.ExternalId}:{revision[7..23]}:{now:yyyyMMddHHmmssfff}",
            ["source"] = origin, ["measurements"] = measurements,
        };
        using var json = JsonDocument.Parse(args.ToJsonString());
        await sink.Write(json.RootElement, ct, write.Details);
        return true;
    }

    static string Long(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    public static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
