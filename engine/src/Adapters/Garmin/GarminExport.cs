using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Garmin;

// Garmin Connect's own exports, for a person without developer access (docs/adapters/garmin.md). Files are read from
// importPath (mounted read-only), at most MaxFiles of MaxBytes, and read again only when their size or time changes:
//   *.csv   the activity list (Garmin Connect: Activities, Export CSV), one activity per row, English headers
//   *.json  daily files of the account data export: a list of daily records with calendarDate and the field names of
//           the API's daily summary or sleep; anything else is skipped
// FIT files are not read: Garmin's FIT SDK is under Garmin's own licence, not an open-source one (open question Q-081).
// Rows become the API's metrics under external ids starting `export:`, so the tiles show them; an unchanged row is
// not written again.
public sealed partial class GarminExport(GarminAdapter garmin, string folder, string distanceUnit)
{
    const int MaxFiles = 200, MaxBytes = 20 * 1024 * 1024;
    readonly Dictionary<string, (long Length, DateTime Modified)> read = new(StringComparer.Ordinal);

    public async Task Import(MeasurementSink sink, Func<bool> late, CancellationToken ct)
    {
        if (!Directory.Exists(folder)) return;
        var files = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Take(MaxFiles);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (info.Length > MaxBytes || read.TryGetValue(file, out var seen) && seen == stamp) continue;
            if (late()) return;
            List<GarminAdapter.SourceWrite> writes;
            try
            {
                var text = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
                writes = file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? Activities(text) : Daily(text);
            }
            // A file that is not one of the exports above is left alone until it changes.
            catch (Exception e) when (e is JsonException or FormatException or IOException or UnauthorizedAccessException) { read[file] = stamp; continue; }
            var finished = true;
            foreach (var write in writes)
            {
                if (late()) { finished = false; break; }
                try { await garmin.Write(sink, write, ct); }
                catch (Ingest.Refused) { }
            }
            if (finished) read[file] = stamp;
        }
    }

    // ---- the activity list --------------------------------------------------------------------------------------
    // The columns read, by their English header; every other column is ignored. Distances are in the person's
    // display unit (distanceUnit), except swims, whose pool unit the export does not say, so their distance is left out.
    public List<GarminAdapter.SourceWrite> Activities(string text)
    {
        var rows = Csv(text);
        if (rows.Count < 2) return [];
        var header = rows[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.IndexOf(name);
        int type = Col("Activity Type"), date = Col("Date"), time = Col("Time");
        if (type < 0 || date < 0 || time < 0) throw new FormatException("not an activity list");
        int distance = Col("Distance"), calories = Col("Calories"), avgHr = Col("Avg HR"), maxHr = Col("Max HR"), aerobic = Col("Aerobic TE"), reps = Col("Total Reps");
        var writes = new List<GarminAdapter.SourceWrite>();
        foreach (var row in rows.Skip(1))
        {
            string? Cell(int i) => i >= 0 && i < row.Count && row[i].Trim() is { Length: > 0 } v && v != "--" ? v.Trim() : null;
            if (Key(Cell(type)) is not { } typeKey || Cell(date) is not { } started
                || !DateTime.TryParseExact(started, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                || HealthSemantics.VerifiedInstant(local, garmin.ZoneName) is not { } at) continue;
            var samples = new List<GarminSample>();
            void Add(string metric, double? value, string unit) { if (value is { } v && v >= 0 && double.IsFinite(v)) samples.Add(new(metric, v, unit, "daily", at, null)); }
            Add("activity_duration", Duration(Cell(time)), "s");
            if (!typeKey.Contains("swim", StringComparison.Ordinal))
                Add("activity_distance", Number(Cell(distance)) * (distanceUnit == "mi" ? 1609.344 : 1000), "m");
            Add("activity_calories", Number(Cell(calories)), "kcal");
            Add("activity_heart_rate", Number(Cell(avgHr)), "bpm");
            Add("activity_heart_rate_max", Number(Cell(maxHr)), "bpm");
            Add("training_effect_aerobic", Number(Cell(aerobic)), "score");
            Add("strength_repetitions", Number(Cell(reps)), "count");
            if (samples.Count == 0) continue;
            var day = DateOnly.FromDateTime(local);
            writes.Add(new($"export:activity:{local:yyyyMMdd'T'HHmmss}:{typeKey}", "activity", $"Garmin activity (export), {Long(day)}", null, day, samples,
                new System.Text.Json.Nodes.JsonObject { ["activityType"] = typeKey }));
        }
        return writes;
    }

    // The export names types for display ("Pool Swim"); the API's type keys are used where they differ.
    static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["pool_swim"] = "lap_swimming", ["open_water_swim"] = "open_water_swimming", ["road_cycling"] = "road_biking",
        ["mountain_biking"] = "mountain_biking", ["multisport"] = "multi_sport", ["indoor_rowing"] = "indoor_rowing",
    };
    static string? Key(string? display)
    {
        if (display is null) return null;
        var key = NotWord().Replace(display.ToLowerInvariant(), "_").Trim('_');
        return GarminNormalize.TypeKey(Types.GetValueOrDefault(key, key));
    }
    static double? Number(string? cell) =>
        cell is not null && double.TryParse(cell.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    // "1:02:03", "02:03" or "1:02:03.4" as seconds.
    static double? Duration(string? cell)
    {
        if (cell is null) return null;
        var parts = cell.Split(':');
        if (parts.Length is < 2 or > 3 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return null;
        return parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).Aggregate(0d, (total, p) => total * 60 + p);
    }

    // ---- daily files of the account export ----------------------------------------------------------------------
    public List<GarminAdapter.SourceWrite> Daily(string text)
    {
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("not a list of daily records");
        var writes = new List<GarminAdapter.SourceWrite>();
        foreach (var record in json.RootElement.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("calendarDate", out var d) || d.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            foreach (var stream in new[] { "daily", "sleep" })
            {
                var samples = GarminNormalize.Metrics(stream, record);
                if (samples.Count > 0)
                    writes.Add(new($"export:{stream}:{date:yyyy-MM-dd}", "metric", $"Garmin {stream} (export), {Long(date)}", null, date, samples, null));
            }
        }
        return writes;
    }

    // RFC 4180 rows: quoted fields may hold commas, quotes ("") and line breaks.
    public static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        return rows;
    }

    static string Long(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NotWord();
}
