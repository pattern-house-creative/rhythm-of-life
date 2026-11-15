using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Adapters;

namespace Knowledge.Relationships;

// A refusal the page can show as it is: what went wrong and what to do.
public sealed class DashboardWriteRefused(string reason) : Exception(reason);

// Two-way dashboards. A page asks, through the app, to apply one of its version's declared writes; the database
// checks the declaration and that someone allowed edits for it. A log entry is written by the database itself. A
// write at a source (a Sheet's cells, a Doc's section) is made by the adapter that names that kind (a Writer, see
// docs/dev/adapters.md), after it checks the file hasn't changed since the dashboard read it, and is recorded with
// what it replaced, so Undo can put it back while nobody has changed it since.
public sealed partial class DashboardWriter(RelationshipHost host, DashboardStore store, AdapterRegistry adapters)
{
    public const int MaxNote = 1000;

    public async Task<string> Write(Guid principal, Guid dashboard, int version, string name, JsonElement change, CancellationToken ct)
    {
        var declared = (await store.Begin(principal, dashboard, version, name, ct)).Normalized();
        if (declared.Kind == "log") return await store.Log(principal, dashboard, version, name, LogEntry(change), ct);
        var writer = Writer(declared.Kind);
        var (view, _) = await host.Readable(principal, ct);
        var star = view.Records.Where(r => adapters.Shows(r.Key.Provider)).FirstOrDefault(r => r.Key.GraphId == declared.Star)
            ?? throw new DashboardWriteRefused("That file isn't on the map now.");
        var outcome = await writer.Write(new(declared, star, change), ct);
        return await store.Record(principal, dashboard, version, name, outcome.Target, outcome.Summary, outcome.Before, outcome.After, ct);
    }

    // Puts back what a write replaced, while what it wrote is still there; a log entry leaves the log.
    public async Task<string> Undo(Guid principal, Guid write, CancellationToken ct)
    {
        if (await store.Writes(principal, null, write, null, ct) is not { } found || JsonNode.Parse(found) is not JsonObject w)
            throw new DashboardWriteRefused("That change isn't in the history.");
        if (w["undoneAt"] is JsonValue) throw new DashboardWriteRefused("That change was already undone.");
        var kind = (string)w["kind"]!;
        if (kind != "log") await Writer(kind).Undo(new(kind, (JsonObject)w["target"]!, w["before"], w["after"]!), ct);
        return await store.MarkUndone(principal, write, ct);
    }

    IDashboardWriter Writer(string kind) => adapters.Writers.FirstOrDefault(a => a.WriteKinds.Contains(kind))
        ?? throw new DashboardWriteRefused($"No adapter that makes {kind} writes is on in this install.");

    // {date?, metric, value, unit, note?} | {date?, check, note?} | {date?, note}
    static JsonObject LogEntry(JsonElement change)
    {
        Only(change, "date", "metric", "value", "unit", "check", "note");
        var today = RhythmConfig.Current.Today();
        var date = Text(change, "date", 10) is { } given
            ? DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
                : throw new DashboardWriteRefused("date is a day, such as 2026-10-29.")
            : today;
        if (date < today.AddDays(-400) || date > today.AddDays(1)) throw new DashboardWriteRefused("A log date is within the last 400 days.");
        var entry = new JsonObject { ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        var note = Text(change, "note", MaxNote);
        if (change.TryGetProperty("value", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new DashboardWriteRefused("value is a number.");
            var metric = Text(change, "metric", 100);
            if (metric is null || !Metric().IsMatch(metric)) throw new DashboardWriteRefused("metric names the number, such as body_weight.");
            var unit = Text(change, "unit", 20);
            if (string.IsNullOrWhiteSpace(unit)) throw new DashboardWriteRefused("unit says what the number counts, such as kg or min.");
            if (change.TryGetProperty("check", out _)) throw new DashboardWriteRefused("An entry is a number or a check-off, not both.");
            entry["metric"] = metric; entry["value"] = number; entry["unit"] = unit.Trim();
        }
        else if (Text(change, "check", 100) is { } check)
        {
            if (!Metric().IsMatch(check)) throw new DashboardWriteRefused("check names what was done, such as stretching.");
            if (change.TryGetProperty("metric", out _) || change.TryGetProperty("unit", out _)) throw new DashboardWriteRefused("A check-off takes no metric or unit.");
            entry["check"] = check;
        }
        else if (string.IsNullOrWhiteSpace(note)) throw new DashboardWriteRefused("An entry is a number (metric, value, unit), a check-off (check) or a note.");
        else if (change.TryGetProperty("metric", out _) || change.TryGetProperty("unit", out _)) throw new DashboardWriteRefused("A number needs a value.");
        if (!string.IsNullOrWhiteSpace(note)) entry["note"] = note.Trim();
        return entry;
    }

    // Shared with Writer adapters: the change carries only these members.
    public static void Only(JsonElement change, params string[] names)
    {
        if (change.ValueKind != JsonValueKind.Object) throw new DashboardWriteRefused("A change is an object.");
        foreach (var p in change.EnumerateObject())
            if (!names.Contains(p.Name)) throw new DashboardWriteRefused($"This write takes {string.Join(", ", names)}; not {p.Name}.");
    }
    // Shared with Writer adapters: one text member, at most `max` characters, no control characters but line breaks and tabs.
    public static string? Text(JsonElement change, string name, int max)
    {
        if (change.ValueKind != JsonValueKind.Object || !change.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > max || v.GetString()!.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r')))
            throw new DashboardWriteRefused($"{name} is text of at most {max:N0} characters.");
        return v.GetString();
    }
    [GeneratedRegex("^[a-z0-9_]{1,100}$")] private static partial Regex Metric();
}
