using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge;

// The external-plugin ingest contract (contracts/README.md section 4): ingest_measurements and ingest_records.
// Validation is all or nothing; a call either writes everything it carries or nothing, and says why with at most
// 20 issues. The same key with the same payload replays the first result; with a different payload it is refused.
// MeasurementSource adapters write through the same code (trusted: the adapter's own providers, any subject).
public sealed partial class Ingest(Database db, Archive archive, RhythmConfig config)
{
    public const int MaxIssues = 20, MaxMeasurements = 2000, MaxRecords = 100, MaxText = 40_000, MaxMetadata = 4096;

    public sealed class Refused(string code, string message, IReadOnlyList<JsonObject>? issues = null) : Exception(message)
    {
        public string Code { get; } = code;
        public IReadOnlyList<JsonObject> Issues { get; } = issues ?? [];
        public JsonObject Error() => new()
        {
            ["code"] = Code, ["message"] = Message,
            ["issues"] = new JsonArray(Issues.Select(i => (JsonNode)i.DeepClone()).ToArray())
        };
    }

    static readonly Dictionary<string, string[]> KnownUnits = new(StringComparer.Ordinal)
    {
        ["glucose"] = ["mg/dL", "mmol/L"], ["heart_rate"] = ["bpm"], ["resting_heart_rate"] = ["bpm"], ["hrv"] = ["ms"],
        ["steps"] = ["count"], ["sleep_minutes"] = ["min"], ["intensity_minutes"] = ["min"], ["insulin"] = ["U"],
        ["carbs"] = ["g"], ["weight"] = ["kg"], ["body_battery"] = ["score"], ["stress"] = ["score"],
    };
    // The quality column keeps the health tools' conventions: censored values carry comparison=< or >.
    static readonly Dictionary<string, string> Qualities = new(StringComparer.Ordinal)
    {
        ["ok"] = "source-reported", ["estimated"] = "source-reported;estimated", ["calibration"] = "source-reported;calibration",
        ["censored_high"] = "source-reported;comparison=>", ["censored_low"] = "source-reported;comparison=<",
        ["invalid"] = "source-reported;invalid",
    };

    // Who may write what: a plugin's configured providers and subjects, or a trusted adapter's own providers.
    (IReadOnlyCollection<string> Providers, IReadOnlyCollection<string> Subjects) Scope(string writer, IReadOnlyCollection<string>? trusted)
    {
        if (trusted is not null) return (trusted, [.. config.Subjects, RhythmConfig.Synthetic]);
        if (!config.Plugins.TryGetValue(writer, out var plugin) || !plugin.Enabled) throw new Refused("forbidden", "This plugin is not enabled in rhythm.yaml.");
        return (plugin.Providers, plugin.Subjects);
    }

    // details (trusted adapters only) is kept as the source resource's metadata.details, for example an activity's type.
    public async Task<JsonObject> Measurements(string writer, JsonElement args, CancellationToken ct, IReadOnlyCollection<string>? trusted = null,
        JsonObject? details = null)
    {
        var issues = new Issues();
        var (providers, subjects) = Scope(writer, trusted);
        Only(args, "", issues, "provider", "subject", "idempotencyKey", "source", "measurements");
        var provider = Id(args, "provider", issues); var subject = Id(args, "subject", issues);
        var key = Key(args, issues);
        var source = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        if (source.ValueKind != JsonValueKind.Object) issues.Add("/source", "invalid_arguments");
        else Only(source, "/source", issues, "externalId", "kind", "title", "fetchedAt", "sourceUpdatedAt", "revision", "url", "timezone", "device");
        var externalId = Text(source, "externalId", "/source", 1, 200, true, issues);
        var kind = Pattern(source, "kind", "/source", KindPattern(), true, issues);
        var title = Text(source, "title", "/source", 1, 200, true, issues);
        var fetched = Instant(source, "fetchedAt", "/source", true, issues);
        var updated = Instant(source, "sourceUpdatedAt", "/source", false, issues);
        var revision = Text(source, "revision", "/source", 1, 200, false, issues);
        var url = Url(source, "/source", issues);
        var zone = Zone(source, "/source", issues);
        string? deviceId = null, deviceModel = null;
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("device", out var device))
        {
            if (device.ValueKind != JsonValueKind.Object) issues.Add("/source/device", "invalid_arguments");
            else
            {
                Only(device, "/source/device", issues, "id", "model");
                deviceId = Text(device, "id", "/source/device", 1, 200, false, issues);
                deviceModel = Text(device, "model", "/source/device", 1, 200, false, issues);
            }
        }
        var samples = new List<(string Metric, double Value, string Unit, DateTimeOffset At, DateOnly? Date, string Key, string Quality, string? Device, string Basis)>();
        if (!args.TryGetProperty("measurements", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaxMeasurements)
            issues.Add("/measurements", list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > MaxMeasurements ? "too_large" : "invalid_arguments");
        else
        {
            var i = 0;
            foreach (var m in list.EnumerateArray())
            {
                var at = $"/measurements/{i++}";
                if (m.ValueKind != JsonValueKind.Object) { issues.Add(at, "invalid_arguments"); continue; }
                // A trusted adapter may say how it knew the time (a bare local time, a date); a plugin always sends an offset.
                if (trusted is null) Only(m, at, issues, "metric", "value", "unit", "observedAt", "localDate", "key", "quality", "deviceId");
                else Only(m, at, issues, "metric", "value", "unit", "observedAt", "localDate", "key", "quality", "deviceId", "timeBasis");
                var metric = Pattern(m, "metric", at, MetricPattern(), true, issues, "metric_invalid");
                double value = double.NaN;
                if (!m.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out value) || !double.IsFinite(value))
                    issues.Add(at + "/value", "invalid_arguments");
                var unit = Pattern(m, "unit", at, UnitPattern(), true, issues, "unit_invalid");
                if (metric is not null && unit is not null && KnownUnits.TryGetValue(metric, out var allowed) && !allowed.Contains(unit))
                    issues.Add(at + "/unit", "unit_invalid");
                var observed = Instant(m, "observedAt", at, true, issues);
                var local = Date(m, "localDate", at, issues);
                var sampleKey = Text(m, "key", at, 1, 200, true, issues);
                var quality = "ok";
                if (m.TryGetProperty("quality", out var q) && (q.ValueKind != JsonValueKind.String || !Qualities.ContainsKey(quality = q.GetString()!)))
                    issues.Add(at + "/quality", "invalid_arguments");
                var sampleDevice = Text(m, "deviceId", at, 1, 200, false, issues);
                var basis = m.TryGetProperty("timeBasis", out var b) ? b.ValueKind == JsonValueKind.String ? b.GetString() : "" : "explicit-offset";
                if (basis is not ("explicit-offset" or "install-local" or "local-date")) issues.Add(at + "/timeBasis", "invalid_arguments");
                if (metric is not null && unit is not null && observed is not null && sampleKey is not null && double.IsFinite(value))
                    samples.Add((metric, value, unit, observed.Value, local, sampleKey, Qualities.GetValueOrDefault(quality, "source-reported"), sampleDevice ?? deviceId, basis!));
            }
            if (samples.GroupBy(x => (x.Metric, x.Key)).Any(g => g.Count() > 1)) issues.Add("/measurements", "invalid_arguments");
        }
        Allowed(provider, subject, providers, subjects, issues);
        issues.ThrowIfAny();

        var hash = PayloadHash(args);
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (await Replay(c, tx, writer, key!, hash, ct) is { } replayed) { await tx.CommitAsync(ct); return replayed; }
        var policy = db.Policies.For(provider!);
        var original = policy.Authority == SourceAuthority.AuthorizedArchive ? await archive.Save(Canonical(args), ct) : null;
        var metadata = new JsonObject { ["ingest"] = writer, ["fetchedAt"] = Iso(fetched!.Value), ["receivedAt"] = Iso(DateTimeOffset.UtcNow) };
        if (deviceId is not null) metadata["device"] = deviceId;
        if (deviceModel is not null) metadata["deviceModel"] = deviceModel;
        if (details is not null && trusted is not null && Encoding.UTF8.GetByteCount(details.ToJsonString()) <= MaxMetadata) metadata["details"] = details.DeepClone();
        var timezone = zone ?? config.Timezone;
        var resource = await UpsertSource(c, tx, new(subject!, provider!, externalId!, kind!, ScopeFor(subject!), title!, url, "",
            revision ?? hash, updated, Timezone: timezone, RawPath: original?.Path, Checksum: original?.Checksum ?? hash,
            Metadata: metadata.ToJsonString()), original, ct);
        // The archived original stays browsable with read_health_source.
        if (original is not null) await SourceProjection.Save(resource, original.Checksum, Canonical(args), "json", c, tx, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        int inserted = 0, changed = 0, unchanged = 0;
        foreach (var x in samples)
        {
            var date = x.Date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.At, tz).DateTime);
            var quality = x.Quality + ";time_basis=" + x.Basis;
            await using var cmd = Database.Command(c, tx, """
                WITH old AS (SELECT value,unit,observed_at,local_date,quality,device_id FROM measurements
                  WHERE resource_id=@id AND metric=@metric AND sample_key=@key)
                INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,device_id,quality)
                VALUES(@id,@metric,@key,@at,@date,@value,@unit,@device,@quality)
                ON CONFLICT(resource_id,metric,sample_key) DO UPDATE SET observed_at=excluded.observed_at,local_date=excluded.local_date,
                  value=excluded.value,unit=excluded.unit,device_id=excluded.device_id,quality=excluded.quality
                RETURNING CASE WHEN NOT EXISTS(SELECT 1 FROM old) THEN 'inserted'
                  WHEN EXISTS(SELECT 1 FROM old WHERE value=@value AND unit=@unit AND observed_at=@at AND local_date=@date AND quality=@quality
                    AND device_id IS NOT DISTINCT FROM @device) THEN 'unchanged' ELSE 'updated' END
                """, ("id", resource), ("metric", x.Metric), ("key", x.Key), ("at", x.At.ToUniversalTime()), ("date", date),
                ("value", x.Value), ("unit", x.Unit), ("device", x.Device), ("quality", quality));
            cmd.Parameters["device"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            switch ((string)(await cmd.ExecuteScalarAsync(ct))!)
            {
                case "inserted": inserted++; break;
                case "updated": changed++; break;
                default: unchanged++; break;
            }
        }
        var last = samples.Max(x => x.At);
        await Fresh(c, tx, writer, provider!, subject!, last, ct);
        var result = new JsonObject
        {
            ["resourceId"] = resource.ToString(), ["accepted"] = samples.Count, ["inserted"] = inserted, ["updated"] = changed,
            ["unchanged"] = unchanged, ["lastObservedAt"] = Iso(last), ["replayed"] = false
        };
        await Remember(c, tx, writer, key!, "ingest_measurements", hash, provider!, subject!, result, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<JsonObject> Records(string writer, JsonElement args, CancellationToken ct, IReadOnlyCollection<string>? trusted = null)
    {
        var issues = new Issues();
        var (providers, subjects) = Scope(writer, trusted);
        Only(args, "", issues, "provider", "subject", "idempotencyKey", "records");
        var provider = Id(args, "provider", issues); var subject = Id(args, "subject", issues);
        var key = Key(args, issues);
        var items = new List<RecordItem>();
        if (!args.TryGetProperty("records", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaxRecords)
            issues.Add("/records", list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > MaxRecords ? "too_large" : "invalid_arguments");
        else
        {
            var i = 0;
            foreach (var r in list.EnumerateArray())
            {
                var at = $"/records/{i++}";
                if (r.ValueKind != JsonValueKind.Object) { issues.Add(at, "invalid_arguments"); continue; }
                Only(r, at, issues, "externalId", "kind", "title", "text", "observedAt", "localDate", "timezone", "revision", "url", "fetchedAt", "sourceUpdatedAt", "metadata", "links");
                var item = new RecordItem
                {
                    ExternalId = Text(r, "externalId", at, 1, 200, true, issues), Kind = Pattern(r, "kind", at, KindPattern(), true, issues),
                    Title = Text(r, "title", at, 1, 200, true, issues), Body = Text(r, "text", at, 0, MaxText, true, issues),
                    Observed = Instant(r, "observedAt", at, false, issues), LocalDate = Date(r, "localDate", at, issues),
                    Timezone = Zone(r, at, issues), Revision = Text(r, "revision", at, 1, 200, false, issues), Url = Url(r, at, issues),
                    Fetched = Instant(r, "fetchedAt", at, true, issues), Updated = Instant(r, "sourceUpdatedAt", at, false, issues),
                };
                if (r.TryGetProperty("metadata", out var meta))
                {
                    if (meta.ValueKind != JsonValueKind.Object || meta.EnumerateObject().Count() > 50 || Encoding.UTF8.GetByteCount(meta.GetRawText()) > MaxMetadata)
                        issues.Add(at + "/metadata", "invalid_arguments");
                    else item.Metadata = JsonNode.Parse(meta.GetRawText()) as JsonObject;
                }
                if (r.TryGetProperty("links", out var links))
                {
                    if (links.ValueKind != JsonValueKind.Array || links.GetArrayLength() > 20) issues.Add(at + "/links", "invalid_arguments");
                    else
                    {
                        var j = 0;
                        foreach (var l in links.EnumerateArray())
                        {
                            var lat = $"{at}/links/{j++}";
                            if (l.ValueKind != JsonValueKind.Object) { issues.Add(lat, "invalid_arguments"); continue; }
                            Only(l, lat, issues, "externalId", "relationship");
                            var target = Text(l, "externalId", lat, 1, 200, true, issues);
                            var type = Pattern(l, "relationship", lat, LinkPattern(), true, issues);
                            if (target is not null && type is not null) item.Links.Add((target, type));
                        }
                    }
                }
                items.Add(item);
            }
            if (items.Where(x => x.ExternalId is not null).GroupBy(x => x.ExternalId).Any(g => g.Count() > 1)) issues.Add("/records", "invalid_arguments");
        }
        Allowed(provider, subject, providers, subjects, issues);
        issues.ThrowIfAny();

        var hash = PayloadHash(args);
        await using var c = await db.Source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (await Replay(c, tx, writer, key!, hash, ct) is { } replayed) { await tx.CommitAsync(ct); return replayed; }
        var policy = db.Policies.For(provider!);
        var original = policy.Authority == SourceAuthority.AuthorizedArchive ? await archive.Save(Canonical(args), ct) : null;
        var results = new JsonArray(); int created = 0, updatedCount = 0, unchanged = 0;
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        DateTimeOffset? newest = null;
        foreach (var item in items)
        {
            var zone = item.Timezone ?? config.Timezone;
            var date = item.LocalDate ?? (item.Observed is { } o ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime) : null);
            var normalized = JsonSerializer.Serialize(new { item.ExternalId, item.Kind, item.Title, item.Body, item.Url, item.Revision,
                observedAt = item.Observed, date, metadata = item.Metadata?.ToJsonString(), links = item.Links });
            var checksum = Hashing.Hash(normalized);
            var metadata = (item.Metadata?.DeepClone() as JsonObject) ?? new JsonObject();
            metadata["ingest"] = writer; metadata["fetchedAt"] = Iso(item.Fetched!.Value); metadata["receivedAt"] = Iso(DateTimeOffset.UtcNow);
            if (item.Observed is { } observed) metadata["observedAt"] = Iso(observed);
            string? before;
            await using (var prior = Database.Command(c, tx, "SELECT checksum FROM resources WHERE subject_id=@s AND provider=@p AND external_id=@e FOR UPDATE",
                ("s", subject), ("p", provider), ("e", item.ExternalId)))
                before = await prior.ExecuteScalarAsync(ct) as string;
            var id = await UpsertSource(c, tx, new(subject!, provider!, item.ExternalId!, item.Kind!, ScopeFor(subject!), item.Title!, item.Url,
                item.Body!, item.Revision ?? checksum, item.Updated ?? item.Observed, Date: date, Timezone: zone,
                RawPath: original?.Path, Checksum: checksum, Metadata: metadata.ToJsonString()), original, ct);
            ids[item.ExternalId!] = id;
            var status = before is null ? "created" : before == checksum ? "unchanged" : "updated";
            if (status == "created") created++; else if (status == "updated") updatedCount++; else unchanged++;
            results.Add(new JsonObject { ["externalId"] = item.ExternalId, ["resourceId"] = id.ToString(), ["status"] = status });
            var when = item.Observed ?? item.Updated ?? item.Fetched;
            if (newest is null || when > newest) newest = when;
        }
        foreach (var item in items)
            foreach (var (target, type) in item.Links)
            {
                Guid? to = ids.TryGetValue(target, out var known) ? known : null;
                if (to is null)
                    await using (var find = Database.Command(c, tx, "SELECT id FROM resources WHERE subject_id=@s AND provider=@p AND external_id=@e",
                        ("s", subject), ("p", provider), ("e", target)))
                        to = await find.ExecuteScalarAsync(ct) as Guid?;
                if (to is null || to == ids[item.ExternalId!]) continue;
                await using var link = Database.Command(c, tx, """
                    INSERT INTO relationships(source_id,target_id,type,verification,evidence) VALUES(@s,@t,@type,'suggested',@evidence)
                    ON CONFLICT(source_id,target_id,type) DO NOTHING
                    """, ("s", ids[item.ExternalId!]), ("t", to), ("type", type), ("evidence", $"ingest:{writer}"));
                await link.ExecuteNonQueryAsync(ct);
            }
        await Fresh(c, tx, writer, provider!, subject!, newest, ct);
        var result = new JsonObject { ["records"] = results, ["created"] = created, ["updated"] = updatedCount, ["unchanged"] = unchanged, ["replayed"] = false };
        await Remember(c, tx, writer, key!, "ingest_records", hash, provider!, subject!, result, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    sealed class RecordItem
    {
        public string? ExternalId, Kind, Title, Body, Timezone, Revision, Url;
        public DateTimeOffset? Observed, Fetched, Updated;
        public DateOnly? LocalDate;
        public JsonObject? Metadata;
        public List<(string ExternalId, string Relationship)> Links = [];
    }

    string ScopeFor(string subject) => subject == RhythmConfig.Household ? "shared" : "private";

    async Task<Guid> UpsertSource(NpgsqlConnection c, NpgsqlTransaction tx, ResourceInput input, Archived? original, CancellationToken ct)
    {
        await using (var cmd = Database.Command(c, tx, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,content,revision,source_updated_at,local_date,timezone,raw_path,checksum,metadata,status,domain)
            VALUES(@id,@subject,@provider,@external,@kind,@scope,@title,@url,@content,@revision,@updated,@date,@zone,@raw,@checksum,@metadata::jsonb,'current',@domain)
            ON CONFLICT(subject_id,provider,external_id) DO UPDATE SET kind=excluded.kind,title=excluded.title,source_url=excluded.source_url,
              content=excluded.content,revision=excluded.revision,source_updated_at=coalesce(excluded.source_updated_at,resources.source_updated_at),
              local_date=excluded.local_date,timezone=excluded.timezone,raw_path=coalesce(excluded.raw_path,resources.raw_path),
              checksum=excluded.checksum,metadata=excluded.metadata,status='current',indexed_at=now()
            RETURNING id
            """, ("id", Guid.NewGuid()), ("subject", input.Subject), ("provider", input.Provider), ("external", input.ExternalId), ("kind", input.Kind),
            ("scope", input.Scope), ("title", input.Title), ("url", input.Url), ("content", input.Content), ("revision", input.Revision),
            ("updated", input.SourceUpdated?.ToUniversalTime()), ("date", input.Date), ("zone", input.Timezone), ("raw", input.RawPath),
            ("checksum", input.Checksum), ("metadata", input.Metadata), ("domain", db.Policies.Validate(input))))
        {
            cmd.Parameters["updated"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
            cmd.Parameters["date"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date;
            cmd.Parameters["url"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            cmd.Parameters["raw"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
            var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            if (original is not null)
            {
                await using var version = Database.Command(c, tx, "INSERT INTO resource_versions(resource_id,checksum,raw_path) VALUES(@id,@hash,@path) ON CONFLICT DO NOTHING",
                    ("id", id), ("hash", original.Checksum), ("path", original.Path));
                await version.ExecuteNonQueryAsync(ct);
            }
            return id;
        }
    }

    static async Task<JsonObject?> Replay(NpgsqlConnection c, NpgsqlTransaction tx, string writer, string key, string hash, CancellationToken ct)
    {
        await Database.Execute(c, tx, $"SELECT pg_advisory_xact_lock(hashtextextended('ingest:{writer}',0))", ct);
        await using var cmd = Database.Command(c, tx, "SELECT payload_hash,result::text FROM ingest_calls WHERE plugin=@p AND idempotency_key=@k", ("p", writer), ("k", key));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetString(0) != hash) throw new Refused("idempotency_conflict", "This idempotencyKey was used with a different payload.");
        var result = (JsonObject)JsonNode.Parse(reader.GetString(1))!;
        result["replayed"] = true;
        return result;
    }
    static async Task Remember(NpgsqlConnection c, NpgsqlTransaction tx, string writer, string key, string tool, string hash, string provider,
        string subject, JsonObject result, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, tx, """
            INSERT INTO ingest_calls(plugin,idempotency_key,tool,payload_hash,provider,subject_id,result) VALUES(@p,@k,@t,@h,@provider,@s,@r::jsonb)
            """, ("p", writer), ("k", key), ("t", tool), ("h", hash), ("provider", provider), ("s", subject), ("r", result.ToJsonString()));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    static async Task Fresh(NpgsqlConnection c, NpgsqlTransaction tx, string writer, string provider, string subject, DateTimeOffset? observed, CancellationToken ct)
    {
        await using var cmd = Database.Command(c, tx, """
            INSERT INTO ingest_freshness(plugin,provider,subject_id,last_accepted_at,last_observed_at) VALUES(@w,@p,@s,now(),@o)
            ON CONFLICT(plugin,provider,subject_id) DO UPDATE SET last_accepted_at=now(),
              last_observed_at=greatest(ingest_freshness.last_observed_at,excluded.last_observed_at)
            """, ("w", writer), ("p", provider), ("s", subject), ("o", observed?.ToUniversalTime()));
        cmd.Parameters["o"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The payload's identity: its canonical JSON (objects with sorted members), hashed.
    public static string PayloadHash(JsonElement args) => Hashing.Hash(Canonical(args));
    public static byte[] Canonical(JsonElement value) => Encoding.UTF8.GetBytes(Sorted(JsonNode.Parse(value.GetRawText()))?.ToJsonString() ?? "null");
    static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sorted(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone()
    };
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- validation helpers: each adds an issue with a JSON pointer and returns null when the value is wrong ----
    sealed class Issues
    {
        readonly List<JsonObject> list = [];
        public void Add(string path, string code) { if (list.Count < MaxIssues && !list.Any(i => (string?)i["path"] == path)) list.Add(new() { ["path"] = path, ["code"] = code }); }
        public void ThrowIfAny()
        {
            if (list.Count == 0) return;
            var codes = list.Select(i => (string)i["code"]!).ToHashSet();
            var top = codes.Contains("provider_not_allowed") ? "provider_not_allowed" : codes.Contains("subject_not_allowed") ? "subject_not_allowed"
                : codes.Contains("too_large") ? "too_large" : codes.Count == 1 ? codes.Single() : "invalid_arguments";
            throw new Refused(top, "Nothing was written: the call has invalid items.", list);
        }
    }
    static void Only(JsonElement e, string at, Issues issues, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) { issues.Add(at == "" ? "/" : at, "invalid_arguments"); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (!names.Contains(p.Name) || !seen.Add(p.Name)) issues.Add($"{at}/{p.Name}", "invalid_arguments");
    }
    static string? Text(JsonElement e, string name, string at, int min, int max, bool required, Issues issues)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) { if (required) issues.Add($"{at}/{name}", "invalid_arguments"); return null; }
        if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length < min || v.GetString()!.Length > max) { issues.Add($"{at}/{name}", "invalid_arguments"); return null; }
        return v.GetString();
    }
    static string? Pattern(JsonElement e, string name, string at, Regex pattern, bool required, Issues issues, string code = "invalid_arguments")
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) { if (required) issues.Add($"{at}/{name}", "invalid_arguments"); return null; }
        if (v.ValueKind != JsonValueKind.String || !pattern.IsMatch(v.GetString()!)) { issues.Add($"{at}/{name}", code); return null; }
        return v.GetString();
    }
    static string? Id(JsonElement e, string name, Issues issues) => Pattern(e, name, "", IdPattern(), true, issues);
    static string? Key(JsonElement e, Issues issues) => Pattern(e, "idempotencyKey", "", KeyPattern(), true, issues);
    static DateTimeOffset? Instant(JsonElement e, string name, string at, bool required, Issues issues)
    {
        var text = Pattern(e, name, at, InstantPattern(), required, issues, "time_invalid");
        if (text is null) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            || value > DateTimeOffset.UtcNow.AddMinutes(10) || value.Year < 1970) { issues.Add($"{at}/{name}", "time_invalid"); return null; }
        return value;
    }
    static DateOnly? Date(JsonElement e, string name, string at, Issues issues)
    {
        var text = Pattern(e, name, at, DatePattern(), false, issues, "time_invalid");
        if (text is null) return null;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { issues.Add($"{at}/{name}", "time_invalid"); return null; }
        return d;
    }
    static string? Zone(JsonElement e, string at, Issues issues)
    {
        var zone = Pattern(e, "timezone", at, ZonePattern(), false, issues);
        if (zone is null) return null;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); return zone; }
        catch (Exception x) when (x is TimeZoneNotFoundException or InvalidTimeZoneException) { issues.Add($"{at}/timezone", "invalid_arguments"); return null; }
    }
    static string? Url(JsonElement e, string at, Issues issues)
    {
        var url = Text(e, "url", at, 1, 2000, false, issues);
        if (url is null) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo != "") { issues.Add($"{at}/url", "invalid_arguments"); return null; }
        return url;
    }
    static void Allowed(string? provider, string? subject, IReadOnlyCollection<string> providers, IReadOnlyCollection<string> subjects, Issues issues)
    {
        if (provider is not null && !providers.Contains(provider)) issues.Add("/provider", "provider_not_allowed");
        if (subject is not null && !subjects.Contains(subject)) issues.Add("/subject", "subject_not_allowed");
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[A-Za-z0-9._:-]{8,200}$")] private static partial Regex KeyPattern();
    [GeneratedRegex("^[a-z][a-z0-9-]{1,40}$")] private static partial Regex KindPattern();
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,63}$")] private static partial Regex MetricPattern();
    [GeneratedRegex("^[A-Za-z0-9%/._ -]{1,16}$")] private static partial Regex UnitPattern();
    [GeneratedRegex("^[a-z][a-z0-9_-]{1,40}$")] private static partial Regex LinkPattern();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,9})?(Z|[+-][0-9]{2}:[0-9]{2})$")] private static partial Regex InstantPattern();
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")] private static partial Regex DatePattern();
    [GeneratedRegex("^(UTC|[A-Za-z_]+(/[A-Za-z0-9_+-]+){1,2})$")] private static partial Regex ZonePattern();
}
