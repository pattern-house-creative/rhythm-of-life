using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Csv;

// Numbers from CSV files dropped in the import folder (docs/adapters/csv.md): a header row with time, metric, value
// and unit, and optional subject and source columns. Every file is written through the MeasurementSink, with the
// ingest rules, under one source record per (file, subject, source); the catalog then shows the numbers as data stars.
// The folder is the truth for its files: an unchanged file changes nothing, a changed one updates (rows it no longer
// has are removed), and a removed file's numbers leave the map. A bad row is skipped and reported for its file in
// get_sync_status; it never blocks the other rows or files. A file that cannot be read at all changes nothing.
public sealed partial class CsvAdapter : IMeasurementSource
{
    public const string AdapterName = "csv", Provider = "csv";
    const long MaxFile = 20 * 1024 * 1024;
    const int MaxRows = 200_000, MaxReported = 20, Chunk = Ingest.MaxMeasurements;
    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
        new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "health") };
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    SourceFolder folder = new("/data/import", "adapters.csv.path");
    string owner = RhythmConfig.Household;
    // The subject of rows that name none (adapters.csv.owner): Sync now shows the folder to its readers (IAdapter.Owner).
    public string Owner => owner;
    AdapterContext? context;
    // What each file looked like when it was last read, so an unchanged file is not read again until a restart.
    readonly Dictionary<string, (long Length, DateTimeOffset Modified)> seen = new(StringComparer.Ordinal);

    public void Configure(AdapterContext context)
    {
        this.context = context;
        folder = new(context.Settings.String("path") ?? "/data/import", "adapters.csv.path");
        owner = context.Settings.Owner ?? RhythmConfig.Household;
    }

    // ---- the file format ---------------------------------------------------------------------------------------------
    internal sealed record Row(int Line, DateTimeOffset At, DateOnly? Date, string Key, string Basis, string Metric, double Value,
        string Unit, string Subject, string? Source);
    internal sealed record Issue(int Line, string Code);
    internal sealed record Parsed(IReadOnlyList<Row> Rows, IReadOnlyList<Issue> Issues, string? Problem);
    static readonly string[] Required = ["time", "metric", "value", "unit"];

    internal static Parsed Parse(string text, RhythmConfig config, string owner, DateTimeOffset now)
    {
        var records = Records(text.TrimStart('﻿')).Where(r => r.Fields.Any(f => f.Trim().Length > 0)).ToList();
        if (records.Count == 0) return new([], [], "The file is empty: it needs a header row with time, metric, value and unit.");
        var header = records[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToArray();
        var missing = Required.Where(c => !header.Contains(c)).ToArray();
        if (missing.Length > 0) return new([], [], $"The header row has no {string.Join(", ", missing)} column; it needs time, metric, value and unit.");
        if (header.Where(h => h.Length > 0).GroupBy(h => h).Any(g => g.Count() > 1)) return new([], [], "The header row names a column twice.");
        int Column(string name) => Array.IndexOf(header, name);
        var (time, metric, value, unit, subject, source) = (Column("time"), Column("metric"), Column("value"), Column("unit"), Column("subject"), Column("source"));
        var zone = config.Zone;
        var rows = new Dictionary<(string, string?, string, string), Row>();
        var issues = new List<Issue>();
        foreach (var (line, fields) in records.Skip(1).Take(MaxRows))
        {
            string Field(int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : "";
            if (fields.Skip(header.Length).Any(f => f.Trim().Length > 0)) { issues.Add(new(line, "columns")); continue; }
            var at = Time(Field(time), zone, now, out var ambiguous);
            if (at is not { } t) { issues.Add(new(line, ambiguous ? "time_ambiguous" : "time_invalid")); continue; }
            var name = Field(metric).ToLowerInvariant();
            if (!MetricPattern().IsMatch(name)) { issues.Add(new(line, "metric_invalid")); continue; }
            if (!double.TryParse(Field(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            { issues.Add(new(line, "value_invalid")); continue; }
            var measure = Field(unit);
            if (!UnitPattern().IsMatch(measure)) { issues.Add(new(line, "unit_invalid")); continue; }
            var who = Field(subject) is { Length: > 0 } s ? s : owner;
            if (!config.IsSubject(who)) { issues.Add(new(line, "subject_not_allowed")); continue; }
            var from = Field(source) is { Length: > 0 } named ? named : null;
            if (from is { Length: > 100 }) { issues.Add(new(line, "source_invalid")); continue; }
            var row = new Row(line, t.At, t.Date, t.Key, t.Basis, name, number, measure, who, from);
            // The same reading twice: the later row wins, and the earlier one is reported.
            if (rows.TryGetValue((who, from, name, t.Key), out var earlier)) issues.Add(new(earlier.Line, "duplicate_row"));
            rows[(who, from, name, t.Key)] = row;
        }
        if (records.Count - 1 > MaxRows) issues.Add(new(records[MaxRows + 1].Line, "too_many_rows"));
        return new(rows.Values.OrderBy(r => r.Line).ToArray(), issues.OrderBy(i => i.Line).ToArray(), null);
    }

    // ISO 8601: an instant with Z or an offset, a bare local time (read in the install's time zone) or a date (a daily
    // value). Each says how it was read, so the sample keeps it.
    internal static (DateTimeOffset At, DateOnly? Date, string Key, string Basis)? Time(string text, TimeZoneInfo zone, DateTimeOffset now) =>
        Time(text, zone, now, out _);
    // Ambiguous: a bare local time in the hour the clocks repeated, which was two instants an hour apart.
    internal static (DateTimeOffset At, DateOnly? Date, string Key, string Basis)? Time(string text, TimeZoneInfo zone, DateTimeOffset now,
        out bool ambiguous)
    {
        ambiguous = false;
        var m = TimePattern().Match(text);
        if (!m.Success) return null;
        int Part(string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        try
        {
            if (!m.Groups["hour"].Success)
            {
                if (m.Groups["zone"].Success) return null;
                var date = new DateOnly(Part("year"), Part("month"), Part("day"));
                var midnight = date.ToDateTime(TimeOnly.MinValue);
                if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
                var start = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
                return start > now || date.Year < 1970 ? null : (start, date, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "local-date");
            }
            var local = new DateTime(Part("year"), Part("month"), Part("day"), Part("hour"), Part("minute"),
                m.Groups["second"].Success ? Part("second") : 0, DateTimeKind.Unspecified);
            if (m.Groups["fraction"].Success) local = local.AddTicks(long.Parse(m.Groups["fraction"].Value.PadRight(7, '0')[..7], CultureInfo.InvariantCulture));
            DateTimeOffset at; string basis;
            if (m.Groups["zone"].Success)
            {
                var z = m.Groups["zone"].Value;
                var offset = z is "Z" or "z" ? TimeSpan.Zero
                    : new TimeSpan(int.Parse(z[1..3], CultureInfo.InvariantCulture), int.Parse(z[^2..], CultureInfo.InvariantCulture), 0) * (z[0] == '-' ? -1 : 1);
                if (offset.Duration() > TimeSpan.FromHours(14)) return null;
                (at, basis) = (new DateTimeOffset(local, offset), "explicit-offset");
            }
            else
            {
                // A local time the clocks skipped never happened. One they repeated happened twice and the row does not
                // say which: read either way it would merge the two hours or misplace one, so it is refused (as the
                // Garmin export refuses it) and reported as such. With its offset it reads.
                if (zone.IsInvalidTime(local)) return null;
                if (zone.IsAmbiguousTime(local)) { ambiguous = true; return null; }
                (at, basis) = (new DateTimeOffset(local, zone.GetUtcOffset(local)), "install-local");
            }
            if (at > now.AddMinutes(10) || at.Year < 1970) return null;
            return (at, null, Iso(at), basis);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // RFC 4180 records: comma-separated, quoted fields may hold commas, quotes ("") and line breaks. Each record says
    // the line it starts on.
    static IEnumerable<(int Line, List<string> Fields)> Records(string text)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var (line, start, quoted, any) = (1, 1, false, false);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else { if (c == '\n') line++; field.Append(c); }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0: quoted = true; any = true; break;
                case ',': fields.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    fields.Add(field.ToString()); field.Clear();
                    yield return (start, fields);
                    fields = []; line++; start = line; any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0) { fields.Add(field.ToString()); yield return (start, fields); }
    }

    // ---- the folder, synced ------------------------------------------------------------------------------------------
    public async Task Sync(MeasurementSink sink, CancellationToken ct)
    {
        if (context is null) return;
        // A folder that is not there (not mounted) changes nothing.
        if (!folder.Exists) return;
        var files = folder.Files(p => p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), skip: ["documents", "garmin"]);
        var changed = false;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.TryGetValue(file.Path, out var last) && last == (file.Length, file.Modified)) continue;
            // A file that cannot be read now (still being copied, no permission) is tried again next time.
            try { changed |= await Import(sink, file, ct); }
            catch (IOException) { continue; }
            seen[file.Path] = (file.Length, file.Modified);
        }
        var present = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in seen.Keys.Where(k => !present.Contains(k)).ToArray()) seen.Remove(gone);
        changed |= await Forget(present, ct);
        if (changed) await new RefreshRequests(context.Db.Source).Queue(ct);
    }

    // One file: its rows written group by group, rows it no longer has removed, and its status recorded. True when the
    // numbers on the map changed.
    async Task<bool> Import(MeasurementSink sink, SourceFolder.Entry file, CancellationToken ct)
    {
        var db = context!.Db;
        if (file.Length > MaxFile) { await Status(file, null, [], 0, 0, null, $"The file is larger than {MaxFile / 1024 / 1024} MiB.", ct); return false; }
        var bytes = await File.ReadAllBytesAsync(file.Full, ct);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { await Status(file, bytes, [], 0, 0, null, "The file is not UTF-8 text.", ct); return false; }
        var parsed = Parse(text, context.Config, owner, DateTimeOffset.UtcNow);
        if (parsed.Problem is { } problem) { await Status(file, bytes, parsed.Issues, 0, 0, null, problem, ct); return false; }
        var issues = parsed.Issues.ToList();
        var counts = new Counts();
        var written = new Dictionary<Guid, List<Row>>();
        var keys = new List<string>();
        foreach (var group in parsed.Rows.GroupBy(r => (r.Subject, r.Source)))
        {
            var rows = group.ToList();
            for (var start = 0; start < rows.Count; start += Chunk)
            {
                var chunk = rows.GetRange(start, Math.Min(Chunk, rows.Count - start));
                // The sink checks everything (units of known metrics among it); rows it refuses are reported and the
                // rest sent again, a few times at most.
                var sent = false;
                for (var attempt = 0; attempt < 6 && chunk.Count > 0 && !sent; attempt++)
                {
                    try
                    {
                        var call = Arguments(file, group.Key.Subject, group.Key.Source, chunk);
                        using var args = JsonDocument.Parse(call.ToJsonString());
                        var result = await sink.Write(args.RootElement, ct);
                        keys.Add((string)call["idempotencyKey"]!);
                        counts.Add(result);
                        var id = Guid.Parse((string)result["resourceId"]!);
                        (written.TryGetValue(id, out var list) ? list : written[id] = []).AddRange(chunk);
                        sent = true;
                    }
                    catch (Ingest.Refused refused)
                    {
                        var bad = refused.Issues.Select(i => (Index: Index((string?)i["path"]), Code: (string?)i["code"] ?? "invalid_arguments"))
                            .Where(i => i.Index is { } n && n < chunk.Count).ToArray();
                        if (bad.Length == 0) break;
                        foreach (var (index, code) in bad) issues.Add(new(chunk[index!.Value].Line, code));
                        var drop = bad.Select(b => b.Index!.Value).ToHashSet();
                        chunk = chunk.Where((_, i) => !drop.Contains(i)).ToList();
                    }
                }
                if (!sent) issues.AddRange(chunk.Select(r => new Issue(r.Line, "not_written")));
            }
        }
        var kept = written.Values.SelectMany(r => r).ToArray();
        if (kept.Length == 0 && parsed.Rows.Count + issues.Count > 0)
        {
            await Status(file, bytes, issues, 0, 0, null, "No row could be read, so nothing was changed.", ct);
            return false;
        }
        // The file is the truth for its readings: a row it no longer has, or a source it no longer names, goes.
        var (removed, restored) = (0, 0);
        await using (var c = await db.Source.OpenConnectionAsync(ct))
        {
            foreach (var (id, rows) in written)
            {
                await using var cmd = Database.Command(c, null, """
                    DELETE FROM measurements m WHERE m.resource_id=@id
                      AND NOT EXISTS(SELECT 1 FROM unnest(@metrics,@keys) AS k(metric,key) WHERE k.metric=m.metric AND k.key=m.sample_key)
                    """, ("id", id), ("metrics", rows.Select(r => r.Metric).ToArray()), ("keys", rows.Select(r => r.Key).ToArray()));
                removed += await cmd.ExecuteNonQueryAsync(ct);
            }
            await using var stale = Database.Command(c, null, """
                UPDATE resources SET status='deleted',indexed_at=now() WHERE provider=@p AND status='current' AND id<>ALL(@kept)
                  AND (external_id=@file OR left(external_id,length(@file)+1)=@file || '#')
                """, ("p", Provider), ("kept", written.Keys.ToArray()), ("file", file.Path));
            removed += await stale.ExecuteNonQueryAsync(ct);
            // A replayed call writes nothing, so it must never stand in for what the database no longer holds. A file put
            // back as it was (its records left when it went) is current again; and once a file is read as it is now, the
            // keys of its other versions are forgotten, so a version put back later is written again, not replayed.
            await using var back = Database.Command(c, null, "UPDATE resources SET status='current',indexed_at=now() WHERE id=ANY(@kept) AND status<>'current'",
                ("kept", written.Keys.ToArray()));
            restored = await back.ExecuteNonQueryAsync(ct);
            await using var forget = Database.Command(c, null, """
                DELETE FROM ingest_calls i USING resources r WHERE i.plugin=@plugin AND i.idempotency_key<>ALL(@calls)
                  AND i.result->>'resourceId'=r.id::text AND r.provider=@p AND (r.external_id=@file OR left(r.external_id,length(@file)+1)=@file || '#')
                """, ("plugin", AdapterName), ("calls", keys.ToArray()), ("p", Provider), ("file", file.Path));
            await forget.ExecuteNonQueryAsync(ct);
        }
        var dates = kept.Select(LocalDate).DefaultIfEmpty(context.Config.Today()).ToArray();
        await Status(file, bytes, issues, kept.Length, removed, counts, null, ct, (dates.Min(), dates.Max()));
        return counts.Inserted + counts.Updated + removed + restored > 0;
    }

    // Records of files that are no longer in the folder leave the map (the archived originals stay).
    async Task<bool> Forget(IReadOnlySet<string> present, CancellationToken ct)
    {
        var db = context!.Db;
        var gone = new List<Guid>();
        await using (var cmd = db.Source.CreateCommand("SELECT id,external_id FROM resources WHERE provider=@p AND status='current'"))
        {
            cmd.Parameters.AddWithValue("p", Provider);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(1);
                if (!present.Contains(id) && !present.Any(p => id.StartsWith(p + "#", StringComparison.Ordinal))) gone.Add(reader.GetGuid(0));
            }
        }
        if (gone.Count > 0)
        {
            await using var cmd = db.Source.CreateCommand("UPDATE resources SET status='deleted',indexed_at=now() WHERE id=ANY(@ids)");
            cmd.Parameters.AddWithValue("ids", gone.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = db.Source.CreateCommand("""
            UPDATE sync_jobs SET status='removed',last_error=NULL,updated_at=now() WHERE provider=@p AND status<>'removed' AND NOT (metadata->>'file'=ANY(@files))
            """))
        {
            cmd.Parameters.AddWithValue("p", Provider);
            cmd.Parameters.AddWithValue("files", present.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return gone.Count > 0;
    }

    // ingest_measurements arguments for one group of rows. The payload names the file's content and modified time,
    // never the time it was read, so reading an unchanged file again replays the first result and writes nothing.
    JsonObject Arguments(SourceFolder.Entry file, string subject, string? source, List<Row> rows)
    {
        var name = Path.GetFileName(file.Path);
        var args = new JsonObject
        {
            ["provider"] = Provider, ["subject"] = subject,
            ["source"] = new JsonObject
            {
                ["externalId"] = source is null ? file.Path : $"{file.Path}#{Slug(source)}", ["kind"] = "csv-readings",
                ["title"] = Bounded(source is null ? name : $"{source} ({name})"), ["fetchedAt"] = Iso(file.Modified),
                ["sourceUpdatedAt"] = Iso(file.Modified), ["timezone"] = context!.Config.Timezone,
            },
            ["measurements"] = new JsonArray(rows.Select(r =>
            {
                var sample = new JsonObject { ["metric"] = r.Metric, ["value"] = r.Value, ["unit"] = r.Unit, ["observedAt"] = Iso(r.At), ["key"] = r.Key, ["timeBasis"] = r.Basis };
                if (r.Date is { } date) sample["localDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return (JsonNode)sample;
            }).ToArray()),
        };
        if (source is not null) ((JsonObject)args["source"]!)["device"] = new JsonObject { ["id"] = Slug(source), ["model"] = Bounded(source) };
        using (var payload = JsonDocument.Parse(args.ToJsonString()))
            args["idempotencyKey"] = "csv:" + Ingest.PayloadHash(payload.RootElement)[..48];
        return args;
    }

    // The file's line in get_sync_status: one sync job per file, with what was read and every skipped row (line and
    // reason, never the values), at most 20 of them listed.
    async Task Status(SourceFolder.Entry file, byte[]? bytes, IReadOnlyList<Issue> issues, int accepted, int removed, Counts? counts,
        string? problem, CancellationToken ct, (DateOnly From, DateOnly Until)? range = null)
    {
        var today = context!.Config.Today();
        var (from, until) = range ?? (today, today);
        if (until < from) until = from;
        var skipped = issues.Select(i => i.Line).Distinct().Count();
        var listed = issues.OrderBy(i => i.Line).Take(MaxReported).ToArray();
        var rows = skipped == 0 ? null
            : $"{skipped} of {accepted + skipped} rows skipped: " + string.Join("; ", listed.Select(i => $"line {i.Line} {i.Code}")) + (issues.Count > MaxReported ? "; …" : "");
        var error = problem is null && rows is null ? null : $"{file.Path}: " + (problem is null ? rows : rows is null ? problem : $"{problem} {rows}");
        var status = problem is not null ? "failed" : skipped > 0 ? "partial" : "complete";
        var metadata = new JsonObject
        {
            ["file"] = file.Path, ["checksum"] = bytes is null ? null : Hashing.Hash(bytes), ["modifiedAt"] = Iso(file.Modified),
            ["rows"] = accepted + skipped, ["imported"] = accepted, ["skipped"] = skipped, ["inserted"] = counts?.Inserted ?? 0,
            ["updated"] = counts?.Updated ?? 0, ["unchanged"] = counts?.Unchanged ?? 0, ["removed"] = removed,
            ["issues"] = new JsonArray(listed.Select(i => (JsonNode)new JsonObject { ["line"] = i.Line, ["code"] = i.Code }).ToArray()),
        };
        await using var cmd = context.Db.Source.CreateCommand("""
            INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date,status,attempts,last_error,updated_at,metadata)
            VALUES(@id,@s,@p,@from,@until,@until,@status,0,@error,now(),@metadata::jsonb)
            ON CONFLICT(id) DO UPDATE SET subject_id=excluded.subject_id,from_date=excluded.from_date,until_date=excluded.until_date,
              next_date=excluded.next_date,status=excluded.status,last_error=excluded.last_error,updated_at=now(),metadata=excluded.metadata
            """);
        cmd.Parameters.AddWithValue("id", Guid.ParseExact(Hashing.Hash("csv-file:" + file.Path)[..32], "N"));
        cmd.Parameters.AddWithValue("s", owner); cmd.Parameters.AddWithValue("p", Provider);
        cmd.Parameters.AddWithValue("from", from); cmd.Parameters.AddWithValue("until", until);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("metadata", metadata.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    sealed class Counts
    {
        public int Inserted, Updated, Unchanged;
        public void Add(JsonObject result)
        {
            // A replayed call wrote nothing this time, whatever it wrote first.
            if ((bool?)result["replayed"] == true) { Unchanged += (int)result["accepted"]!; return; }
            Inserted += (int)result["inserted"]!; Updated += (int)result["updated"]!; Unchanged += (int)result["unchanged"]!;
        }
    }

    DateOnly LocalDate(Row r) => r.Date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.At, context!.Config.Zone).DateTime);
    static int? Index(string? path) => path is not null && IssuePath().Match(path) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    static string Bounded(string text) => text.Length <= 200 ? text : text[..199] + "…";
    static string Slug(string source)
    {
        var slug = Regex.Replace(source.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length is > 0 and <= 60 ? slug : Hashing.Hash(source)[..16];
    }
    static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})(?:[T ](?<hour>\d{2}):(?<minute>\d{2})(?::(?<second>\d{2})(?:[.,](?<fraction>\d{1,9}))?)?)?\s*(?<zone>Z|z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex TimePattern();
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,63}$")] private static partial Regex MetricPattern();
    [GeneratedRegex("^[A-Za-z0-9%/._ -]{1,16}$")] private static partial Regex UnitPattern();
    [GeneratedRegex(@"^/measurements/(\d+)(/|$)")] private static partial Regex IssuePath();
}
