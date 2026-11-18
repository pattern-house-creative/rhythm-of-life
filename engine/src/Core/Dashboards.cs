using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;

namespace Knowledge;

// Living dashboards. A dashboard is one page a harness writes, plus the feeds it reads: data (a
// data star's measurement rows), contents (what a star holds), links (the stars around one), log (a log's entries) or
// memories (the native memories that match a filter, docs/dev/screens.md 10.12). `Name` is the key the page receives the
// feed under. Text and pages are untrusted data wherever they are shown; the app runs a page in a sandboxed frame and the
// engine never runs it.
public sealed partial record DashboardFeed(string Name, string Kind, string Star, int? Days = null, string? By = null,
    string[]? Metrics = null, int? Limit = null, string? Log = null, string? Subject = null, MemoryWhere? Where = null, bool? FullText = null)
{
    public const int MaxDays = 3650, MaxLinks = 60, DefaultMemories = 200;
    // The first problem with feed `i`, in words a harness can act on; null when it is valid.
    public string? Problem(int i)
    {
        if (Name is null || !FeedName().IsMatch(Name)) return $"feeds[{i}].name is a letter then up to 39 letters, digits or underscores";
        if (Kind is not ("data" or "contents" or "links" or "log" or "memories")) return $"feeds[{i}].kind must be data, contents, links, log or memories";
        // A memories feed reads memories by filter (MemoryMatch), not a star: where (none is every memory its reader
        // reads) and limit, how many of the newest it gives.
        if (Kind == "memories")
        {
            if (Star is not null || Days is not null || By is not null || Metrics is not null || Log is not null || Subject is not null)
                return $"feeds[{i}]: a memories feed takes only where, limit and fullText";
            if (Limit is < 1 or > MemoryStore.MatchMax) return $"feeds[{i}].limit is 1–{MemoryStore.MatchMax} for a memories feed";
            return MemoryMatch.Read(Where, $"feeds[{i}].where", out _);
        }
        if (Where is not null) return $"feeds[{i}]: where is for memories feeds";
        if (FullText is not null) return $"feeds[{i}]: fullText is for memories feeds";
        // A log feed reads a log by name, the one a log write adds to, even before it has entries or a star.
        if (Kind == "log")
        {
            if (Log is null || !LogName().IsMatch(Log)) return $"feeds[{i}].log names the log: a lowercase letter, then up to 39 lowercase letters, digits or hyphens";
            if (Subject is not null && !DashboardWrite.Subjects.Contains(Subject)) return $"feeds[{i}].subject is {DashboardWrite.SubjectList}";
            if (Star is not null || By is not null || Metrics is not null || Limit is not null) return $"feeds[{i}]: a log feed takes only log, subject and days";
            return Days is < 1 or > MaxDays ? $"feeds[{i}].days is 1–{MaxDays}" : null;
        }
        if (Log is not null || Subject is not null) return $"feeds[{i}]: log and subject are for log feeds";
        if (string.IsNullOrWhiteSpace(Star) || Star.Length > 1000) return $"feeds[{i}].star is a graphId from explore_graph";
        if (Kind != "data" && (Days is not null || By is not null || Metrics is not null)) return $"feeds[{i}]: days, by and metrics are for data feeds";
        if (Kind != "links" && Limit is not null) return $"feeds[{i}]: limit is for links and memories feeds";
        if (Days is < 1 or > MaxDays) return $"feeds[{i}].days is 1–{MaxDays}";
        if (By is not null and not ("reading" or "day")) return $"feeds[{i}].by must be reading or day";
        if (Metrics is not null && (Metrics.Length is < 1 or > 40 || Metrics.Any(m => m is null || !Metric().IsMatch(m))))
            return $"feeds[{i}].metrics lists 1–40 metric names, such as sleep_duration";
        if (Limit is < 1 or > MaxLinks) return $"feeds[{i}].limit is 1–{MaxLinks}";
        return null;
    }
    // As stored: the defaults written out, so a saved feed reads the same after the defaults change. A log feed that names
    // no subject reads `subject`'s log when given (with memory.scopes on, the caller's own: DerivedScopes.LogSubject),
    // else the first person's. A memories feed's where is written as MemoryMatch.ToJson writes it, and one that names no
    // status leaves out the memories marked wrong ({"not": ["deprecated"]}), so it asks for them only by naming them.
    public DashboardFeed Normalized(string? subject = null) => Kind switch
    {
        "data" => this with { Days = Days ?? 30, By = By ?? "day" },
        "links" => this with { Limit = Limit ?? 40 },
        "log" => this with { Days = Days ?? 30, Subject = Subject ?? subject ?? DashboardWrite.DefaultSubject },
        "memories" => this with { Limit = Limit ?? DefaultMemories, Where = Matched().ToWhere() },
        _ => this
    };
    // A memories feed's match as it reads, the default status written in; an invalid where (refused before it is saved)
    // as no filter.
    public MemoryMatch Matched()
    {
        MemoryMatch.Read(Where, "where", out var match);
        return match.Status is null ? match with { Status = new([MemoryStore.Deprecated], true) } : match;
    }
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")] private static partial Regex LogName();
    [GeneratedRegex("^[a-z][A-Za-z0-9_]{0,39}$")] private static partial Regex FeedName();
    [GeneratedRegex("^[a-z0-9_]{1,100}$")] private static partial Regex Metric();
}
// What a version may write, declared at save time like its feeds; the engine refuses anything not declared. log adds
// entries to a log (numbers with a unit, check-offs, notes) for a subject; sheet sets cells within `Range` and/or
// adds rows at the end of tab `AppendTo` of a Google Sheet star; doc replaces the text under heading `Section`, a cell
// of table `Table` (0 is the first), or adds to the end (`Append`) of a Google Doc star.
public sealed partial record DashboardWrite(string Name, string Kind, string? Log = null, string? Subject = null, string? Star = null,
    string? Range = null, string? AppendTo = null, string? Section = null, int? Table = null, bool? Append = null)
{
    // People from rhythm.yaml, then household; the first person is the default.
    public static IReadOnlyList<string> Subjects => RhythmConfig.Current.Subjects;
    public static string DefaultSubject => RhythmConfig.Current.FirstPerson;
    public static string SubjectList => string.Join(", ", Subjects.Take(Subjects.Count - 1)) + " or " + Subjects[^1];
    public string? Problem(int i)
    {
        if (Name is null || !WriteName().IsMatch(Name)) return $"writes[{i}].name is a letter then up to 39 letters, digits or underscores";
        if (Kind is not ("log" or "sheet" or "doc")) return $"writes[{i}].kind must be log, sheet or doc";
        if (Kind == "log")
        {
            if (Log is null || !LogName().IsMatch(Log)) return $"writes[{i}].log names the log: a lowercase letter, then up to 39 lowercase letters, digits or hyphens";
            if (Subject is not null && !Subjects.Contains(Subject)) return $"writes[{i}].subject is {SubjectList}";
            if (Star is not null || Range is not null || AppendTo is not null || Section is not null || Table is not null || Append is not null)
                return $"writes[{i}]: a log write takes only log and subject";
            return null;
        }
        if (string.IsNullOrWhiteSpace(Star) || Star.Length > 1000) return $"writes[{i}].star is the graphId of a Google {(Kind == "sheet" ? "Sheet" : "Doc")} from explore_graph";
        if (Log is not null || Subject is not null) return $"writes[{i}]: log and subject are for log writes";
        if (Kind == "sheet")
        {
            if (Section is not null || Table is not null || Append is not null) return $"writes[{i}]: section, table and append are for doc writes";
            if (Range is null && AppendTo is null) return $"writes[{i}] needs range (cells it may set, such as 'Budget'!B2:D40) and/or appendTo (a tab it may add rows to)";
            if (Range is not null && A1Range.Parse(Range) is null) return $"writes[{i}].range is a tab and cells, such as 'Budget'!B2:D40";
            if (AppendTo is not null && (AppendTo.Trim().Length == 0 || AppendTo.Length > 100)) return $"writes[{i}].appendTo is a tab's title";
            return null;
        }
        if (Range is not null || AppendTo is not null) return $"writes[{i}]: range and appendTo are for sheet writes";
        if ((Section is not null ? 1 : 0) + (Table is not null ? 1 : 0) + (Append == true ? 1 : 0) != 1)
            return $"writes[{i}] needs exactly one of section (a heading's text), table (0 for the first table) or append true";
        if (Section is not null && (Section.Trim().Length == 0 || Section.Length > 200)) return $"writes[{i}].section is a heading's text, at most 200 characters";
        if (Table is < 0 or > 100) return $"writes[{i}].table is 0–100";
        return null;
    }
    // A log write that names no subject writes `subject`'s log when given (as DashboardFeed.Normalized), else the first
    // person's.
    public DashboardWrite Normalized(string? subject = null) => Kind == "log" ? this with { Subject = Subject ?? subject ?? DefaultSubject } : this;
    [GeneratedRegex("^[a-z][A-Za-z0-9_]{0,39}$")] private static partial Regex WriteName();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")] private static partial Regex LogName();
}
// A Sheets A1 range with a tab: 'My tab'!B2:D40, Budget!A1 or Budget!C:C. Rows and columns are 1-based; an open end is
// int.MaxValue.
public sealed partial record A1Range(string Tab, int FirstColumn, int FirstRow, int LastColumn, int LastRow)
{
    public static A1Range? Parse(string? text)
    {
        if (text is null || text.Length > 300 || A1().Match(text) is not { Success: true } m) return null;
        var tab = m.Groups["quoted"].Success ? m.Groups["quoted"].Value.Replace("''", "'") : m.Groups["bare"].Value;
        if (tab.Length is 0 or > 100) return null;
        var (c1, r1) = (Column(m.Groups["c1"].Value), Row(m.Groups["r1"].Value));
        var (c2, r2) = m.Groups["c2"].Success || m.Groups["r2"].Success ? (Column(m.Groups["c2"].Value), Row(m.Groups["r2"].Value)) : (c1, r1);
        if (c1 is null || c2 is null || r1 is null || r2 is null) return null;
        if (m.Groups["c1"].Value.Length == 0 && m.Groups["r1"].Value.Length == 0) return null;
        // An open end (B:B, 2:2) is the whole column or row.
        var first = (Column: c1 == 0 ? 1 : c1.Value, Row: r1 == 0 ? 1 : r1.Value);
        var last = (Column: c2 == 0 ? int.MaxValue : c2.Value, Row: r2 == 0 ? int.MaxValue : r2.Value);
        if (last.Column < first.Column || last.Row < first.Row) return null;
        return new(tab, first.Column, first.Row, last.Column, last.Row);
    }
    public bool Contains(A1Range other) => other.Tab == Tab && other.FirstColumn >= FirstColumn && other.LastColumn <= LastColumn
        && other.FirstRow >= FirstRow && other.LastRow <= LastRow;
    public int Rows => LastRow - FirstRow + 1;
    public int Columns => LastColumn - FirstColumn + 1;
    public override string ToString() => $"'{Tab.Replace("'", "''")}'!{Letters(FirstColumn)}{FirstRow}:{Letters(LastColumn)}{LastRow}";
    public string Cells => FirstColumn == LastColumn && FirstRow == LastRow ? $"{Letters(FirstColumn)}{FirstRow}"
        : $"{Letters(FirstColumn)}{FirstRow}:{Letters(LastColumn)}{LastRow}";
    static int? Column(string letters)
    {
        if (letters.Length == 0) return 0;
        var n = 0;
        foreach (var ch in letters.ToUpperInvariant()) n = n * 26 + (ch - 'A' + 1);
        return n is >= 1 and <= 18278 ? n : null;
    }
    static int? Row(string digits) => digits.Length == 0 ? 0 : int.TryParse(digits, out var n) && n is >= 1 and <= 10_000_000 ? n : null;
    public static string Letters(int column)
    {
        var s = "";
        for (var n = column; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }
    [GeneratedRegex(@"^(?:'(?<quoted>(?:[^']|'')+)'|(?<bare>[A-Za-z0-9_ ]+))!(?<c1>[A-Za-z]{0,3})(?<r1>[0-9]{0,8})(?::(?<c2>[A-Za-z]{0,3})(?<r2>[0-9]{0,8}))?$")]
    private static partial Regex A1();
}
public sealed record DashboardStar(string GraphId, string Title);

// A dashboard's grid (docs/dev/screens.md 10.2-10.4): 1-12 parts on 12 columns by 12 rows of cells, its page one of
// them and the app's own map, tiles, insights and other dashboards' cards the others. A version keeps the layout it was
// saved with, and moves, resizes and focus changes arrange the newest version in place (a layout revision). The app
// (parts.ts) checks the same rules. A layout a version was saved without is the page alone. Members a part, a focus or
// the layout doesn't take are kept in `Other` only so Problem can refuse them.
public sealed partial record DashboardLayout(IReadOnlyList<DashboardPart> Parts)
{
    public const int Cells = 12, MaxParts = 12, MaxLength = 8000;
    public static readonly string[] Kinds = ["page", "map", "glucose", "recovery", "training", "changed", "insights", "card"];
    // Each kind's smallest size in cells, width by height.
    public static readonly IReadOnlyDictionary<string, (int W, int H)> Smallest = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
    {
        ["page"] = (3, 3), ["map"] = (4, 4), ["glucose"] = (2, 2), ["recovery"] = (2, 2), ["training"] = (2, 2), ["changed"] = (2, 2),
        ["insights"] = (3, 2), ["card"] = (2, 2),
    };
    // Home's five parts and their smallest sizes there (10.5): the sky and the four corners.
    public static readonly string[] HomeParts = ["map", .. Mcp.AppEndpoint.Corners];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.Strict, PropertyNameCaseInsensitive = false,
    };
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; init; }

    // The page alone: what a version saved without a layout shows.
    public static DashboardLayout PageAlone { get; } = new([new("page", "page", 0, 0, Cells, Cells)]);

    // The first problem with this layout, in words a harness can act on; null when it keeps every rule.
    public string? Problem()
    {
        if (Other is { Count: > 0 }) return "layout takes only parts";
        if (Parts is null || Parts.Count is < 1 or > MaxParts) return $"layout.parts holds 1-{MaxParts} parts";
        for (var i = 0; i < Parts.Count; i++)
            if (Parts[i] is not { } part) return $"layout.parts[{i}] is missing";
            else if (part.Problem($"layout.parts[{i}]") is { } wrong) return wrong;
        if (Parts.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"layout.parts: two parts have the id {twice.Key}";
        var kinds = Parts.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());
        if (kinds.GetValueOrDefault("page") != 1) return "layout has exactly one page part";
        if (kinds.GetValueOrDefault("map") > 2) return "layout has at most 2 map parts";
        if (kinds.GetValueOrDefault("card") > 4) return "layout has at most 4 card parts";
        if (kinds.FirstOrDefault(k => k.Key is not ("page" or "map" or "card") && k.Value > 1) is { Key: { } kind }) return $"layout has at most one {kind} part";
        if (Overlap(Parts.Select(p => (p.Id, p.X!.Value, p.Y!.Value, p.W!.Value, p.H!.Value)).ToArray()) is { } overlap) return "layout." + overlap;
        return StoredLength(JsonSerializer.SerializeToNode(Normalized(), Json)) > MaxLength ? $"layout is at most {MaxLength:N0} characters as stored" : null;
    }

    // As stored: each focus as Normalized says.
    public DashboardLayout Normalized() => new(Parts.Select(p => p with { Focus = p.Focus?.Normalized() }).ToArray());
    public string ToJson() => JsonSerializer.Serialize(Normalized(), Json);

    // A layout as the app sends it, or null and why not.
    public static string? Read(JsonElement json, out DashboardLayout? layout)
    {
        layout = null;
        if (json.ValueKind != JsonValueKind.Object) return "layout is an object, {parts: [...]}";
        try { layout = json.Deserialize<DashboardLayout>(Json); }
        catch (JsonException e) { return $"layout{e.Path?.TrimStart('$')} doesn't fit a layout"; }
        return layout is null ? "layout is an object, {parts: [...]}" : layout.Problem();
    }

    // A version's layout as dashboard_json gives it (null is the page alone), with its revision.
    public static (DashboardLayout Layout, int Revision) Of(JsonElement dashboard) =>
        (dashboard.TryGetProperty("layout", out var stored) && stored.ValueKind == JsonValueKind.Object
            ? stored.Deserialize<DashboardLayout>(Json) ?? PageAlone : PageAlone,
         dashboard.TryGetProperty("layoutRevision", out var revision) ? revision.GetInt32() : 0);

    // The parts whose references a save checks (10.7): parts that are new against `current`, matched by id, and parts
    // whose reference member changed, a map's focus as stored or a card's dashboard. A part whose rectangle alone changed
    // is never checked again, so one whose star or dashboard has gone since still moves.
    public IEnumerable<(int Index, DashboardPart Part)> Changed(DashboardLayout? current)
    {
        var before = (current?.Parts ?? []).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        static string? Stored(MapFocus? focus) => focus is null ? null : JsonSerializer.Serialize(focus.Normalized(), Json);
        return Parts.Select((p, i) => (i, p)).Where(x => !before.TryGetValue(x.p.Id, out var was)
            || Stored(was.Focus) != Stored(x.p.Focus) || was.Dashboard != x.p.Dashboard);
    }
    // The first changed reference that doesn't hold: a focus star `readable` doesn't know, or a card naming this dashboard
    // (`self`) or one that doesn't exist.
    public async Task<string?> ReferenceProblem(DashboardLayout? current, Guid? self, Func<IReadOnlyList<string>, Task<IReadOnlySet<string>>> readable,
        Func<Guid, Task<bool>> exists)
    {
        var changed = Changed(current).ToArray();
        var stars = changed.Select(x => x.Part.Focus?.Near?.Star?.Trim()).OfType<string>().Distinct().ToArray();
        var known = stars.Length == 0 ? new HashSet<string>() : await readable(stars);
        foreach (var (i, part) in changed)
        {
            if (part.Focus?.Near?.Star?.Trim() is { } star && !known.Contains(star))
                return $"layout.parts[{i}].focus.near.star is not a star you can read; find stars with explore_graph";
            if (part.Dashboard is { } card && card == self) return $"layout.parts[{i}].dashboard is this dashboard; a card shows another one";
            if (part.Dashboard is { } other && !await exists(other)) return $"layout.parts[{i}].dashboard names no saved dashboard";
        }
        return null;
    }

    // A screen's Home grid (settings.grid, 10.5): {parts: [{id, x, y, w, h}]} with each of Home's five parts exactly once,
    // inside the grid, never overlapping and at least Home's smallest size (the map 4 × 4, each corner 2 × 3), and
    // nothing else. Null is refused: a screen without a grid leaves it out.
    public static bool HomeGrid(JsonElement grid)
    {
        if (grid.ValueKind != JsonValueKind.Object || grid.EnumerateObject().Select(x => x.Name).ToArray() is not ["parts"]
            || grid.GetProperty("parts") is not { ValueKind: JsonValueKind.Array } parts || parts.GetArrayLength() != HomeParts.Length) return false;
        var placed = new List<(string, int, int, int, int)>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || part.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray()
                is not ["h", "id", "w", "x", "y"] || part.GetProperty("id") is not { ValueKind: JsonValueKind.String } id) return false;
            int? Cell(string name) => part.GetProperty(name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : null;
            if (Cell("x") is not { } x || Cell("y") is not { } y || Cell("w") is not { } w || Cell("h") is not { } h) return false;
            var named = id.GetString()!;
            var (smallW, smallH) = named == "map" ? (4, 4) : (2, 3);
            if (!HomeParts.Contains(named) || Rectangle(x, y, w, h) is not null || w < smallW || h < smallH) return false;
            placed.Add((named, x, y, w, h));
        }
        return placed.Select(p => p.Item1).Distinct().Count() == HomeParts.Length && Overlap(placed) is null;
    }

    // What is wrong with one rectangle in cells, or null.
    internal static string? Rectangle(int x, int y, int w, int h) =>
        x is < 0 or >= Cells ? $"x is 0-{Cells - 1}" : y is < 0 or >= Cells ? $"y is 0-{Cells - 1}"
        : w is < 1 or > Cells ? $"w is 1-{Cells}" : h is < 1 or > Cells ? $"h is 1-{Cells}"
        : x + w > Cells ? $"x + w is at most {Cells}" : y + h > Cells ? $"y + h is at most {Cells}" : null;
    // The first two parts that cover a cell together, or null.
    static string? Overlap(IReadOnlyList<(string Id, int X, int Y, int W, int H)> parts)
    {
        var taken = new string?[Cells, Cells];
        foreach (var p in parts)
            for (var col = p.X; col < p.X + p.W; col++)
                for (var row = p.Y; row < p.Y + p.H; row++)
                {
                    if (taken[col, row] is { } other) return $"parts {other} and {p.Id} overlap at cell {col}, {row}";
                    taken[col, row] = p.Id;
                }
        return null;
    }

    // A value's length as the database measures it (length(value::text) of jsonb): its JSON with a space after every
    // colon and comma, strings unescaped but for quotes, backslashes and control characters, counted in characters.
    internal static int StoredLength(JsonNode? node) => node switch
    {
        null => 4,
        JsonObject o => 2 + o.Sum(p => Text(p.Key) + 2 + StoredLength(p.Value)) + Math.Max(0, o.Count - 1) * 2,
        JsonArray a => 2 + a.Sum(StoredLength) + Math.Max(0, a.Count - 1) * 2,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => Text(v.GetValue<string>()),
        _ => node.ToJsonString().Length,
    };
    static int Text(string text) => 2 + text.EnumerateRunes().Sum(r => r.Value switch
    {
        '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2,
        < 0x20 => 6,
        _ => 1,
    });
}

// One part of a layout (10.3): its id, its kind, and its rectangle in cells, x and y its top left cell; a map's
// optional focus, a card's required dashboard, and nothing else.
public sealed partial record DashboardPart(string Id, string Kind, int? X, int? Y, int? W, int? H, MapFocus? Focus = null, Guid? Dashboard = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; init; }
    public static bool IdFits(string? id) => id is not null && PartId().IsMatch(id);
    public string? Problem(string at)
    {
        if (!IdFits(Id)) return $"{at}.id is a lowercase letter, then up to 31 lowercase letters, digits or underscores";
        if (Kind is null || !DashboardLayout.Kinds.Contains(Kind)) return $"{at}.kind is one of {string.Join(", ", DashboardLayout.Kinds)}";
        if (Other is { Count: > 0 }) return $"{at} takes only id, kind, x, y, w and h, with focus for a map or dashboard for a card";
        if (X is not { } x || Y is not { } y || W is not { } w || H is not { } h) return $"{at} needs x, y, w and h, in cells";
        if (DashboardLayout.Rectangle(x, y, w, h) is { } wrong) return $"{at}: {wrong}";
        var (smallW, smallH) = DashboardLayout.Smallest[Kind];
        if (w < smallW || h < smallH) return $"{at} is a {Kind} part, at least {smallW} × {smallH} cells";
        if (Focus is not null && Kind != "map") return $"{at}: focus is for a map part";
        if (Focus?.Problem($"{at}.focus") is { } unfocused) return unfocused;
        if (Kind == "card" ? Dashboard is null : Dashboard is not null) return Kind == "card" ? $"{at}.dashboard is the id of the dashboard a card shows" : $"{at}: dashboard is for a card part";
        return null;
    }
    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")] private static partial Regex PartId();
}

// A map part's focus (10.4): what its sky narrows to. A star is shown when it meets every member given, and for a list
// any one of its values; `links` picks which links are drawn between shown stars instead. Stored as given, trimmed, with
// lists without repeats and near's depth written out.
public sealed partial record MapFocus(string? Query = null, MapNear? Near = null, string[]? Types = null, string[]? Providers = null,
    string[]? Groups = null, string[]? Owners = null, string[]? Links = null)
{
    public const int MaxQuery = 100, MaxLength = 1500;
    public static readonly string[] StarTypes = ["theme", "project", "note", "record", "document", "memory", "decision"];
    public static readonly string[] Verifications = ["verified", "suggested", "inferred", "unverified"];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; init; }

    public string? Problem(string at)
    {
        if (Other is { Count: > 0 } || Near?.Other is { Count: > 0 })
            return $"{at} takes query, near {{star, depth}}, types, providers, groups, owners and links";
        if (Query is null && Near is null && Types is null && Providers is null && Groups is null && Owners is null && Links is null)
            return $"{at} has at least one of query, near, types, providers, groups, owners or links; null shows everything";
        if (Query is not null && (Query.Trim().EnumerateRunes().Count() is < 1 or > MaxQuery || Query.Any(char.IsControl)))
            return $"{at}.query is 1-{MaxQuery} characters once trimmed, with no control characters";
        // Text the app checks alike (parts.ts): 1 to `most` characters, none of them a control character.
        static bool Text(string value, int most) => value.EnumerateRunes().Count() is var n && n >= 1 && n <= most && !value.Any(char.IsControl);
        if (Near is not null && (Near.Star is null || !Text(Near.Star.Trim(), 1000))) return $"{at}.near.star is a star's graphId from the map";
        if (Near?.Depth is not (null or 1 or 2)) return $"{at}.near.depth is 1 or 2";
        static bool List(string[]? values, int most, Func<string, bool> fits) =>
            values is null || values.Length >= 1 && values.Length <= most && values.All(v => v is not null && fits(v.Trim()));
        if (!List(Types, 7, StarTypes.Contains)) return $"{at}.types lists 1-7 of {string.Join(", ", StarTypes)}";
        if (!List(Providers, 20, Source().IsMatch)) return $"{at}.providers lists 1-20 source ids: a lowercase letter, then up to 40 lowercase letters, digits or hyphens";
        if (!List(Groups, 20, g => Text(g, 300))) return $"{at}.groups lists 1-20 group ids of 1-300 characters, with no control characters";
        if (!List(Owners, 10, Owner().IsMatch)) return $"{at}.owners lists 1-10 owners: a lowercase letter, then up to 31 lowercase letters, digits or hyphens";
        if (!List(Links, 4, Verifications.Contains)) return $"{at}.links lists 1-4 of {string.Join(", ", Verifications)}";
        return DashboardLayout.StoredLength(JsonSerializer.SerializeToNode(Normalized(), DashboardLayout.Json)) > MaxLength
            ? $"{at} is at most {MaxLength:N0} characters" : null;
    }
    // A focus as a command or a layout gives it, or why it isn't one; `at` names it in the reason.
    public static string? Read(JsonElement value, string at, out MapFocus? focus)
    {
        focus = null;
        var expected = $"{at} must be a map focus: an object with any of query, near {{star, depth}}, types, providers, groups, owners and links, or null to show everything";
        if (value.ValueKind != JsonValueKind.Object) return expected;
        try { focus = value.Deserialize<MapFocus>(DashboardLayout.Json); }
        catch (JsonException) { return expected; }
        return focus is null ? expected : focus.Problem(at);
    }
    public MapFocus Normalized()
    {
        static string[]? Once(string[]? values) => values?.Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return new(Query?.Trim(), Near is null ? null : new(Near.Star.Trim(), Near.Depth ?? 1), Once(Types), Once(Providers), Once(Groups),
            Once(Owners), Once(Links));
    }
    [GeneratedRegex("^[a-z][a-z0-9-]{0,40}$")] private static partial Regex Source();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")] private static partial Regex Owner();
}
// The star a focus centres on, and how many links away (1 or 2, default 1) its neighbours may be.
public sealed record MapNear(string Star, int? Depth = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; init; }
}

// One version's content as a harness supplies it; the store adds the id, version number and times.
public sealed record DashboardDraft(string Title, string Description, string Html, IReadOnlyList<DashboardFeed> Feeds, string Note,
    IReadOnlyList<DashboardWrite>? Writes = null)
{
    public const int MaxHtml = 300_000, MaxFeeds = 12, MaxWrites = 12;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static bool Text(string? value, int max, bool required = true) => value is not null && value.Length <= max && (!required || value.Trim().Length > 0);
    public static string? NoteProblem(string? note) => Text(note, 500) ? null : "note says what changed, 1–500 characters";
    public static string? FeedsProblem(IReadOnlyList<DashboardFeed>? feeds)
    {
        if (feeds is null || feeds.Count is < 1 or > MaxFeeds) return $"feeds holds 1–{MaxFeeds} entries";
        for (var i = 0; i < feeds.Count; i++)
            if (feeds[i] is null) return $"feeds[{i}] is missing";
            else if (feeds[i].Problem(i) is { } problem) return problem;
        return feeds.Select(f => f.Name).Distinct().Count() == feeds.Count ? null : "feeds need distinct names";
    }
    // A dashboard names at least one star across its feeds and writes (`named`), which its star on the map is linked to and,
    // with memory.scopes on, whose scopes it lands in; unless a feed reads memories (kind memories), which reads by filter
    // for each reader as that reader reads them, and needs no star to say what the dashboard is about. Null when it holds.
    public static string? Unanchored(IReadOnlyList<DashboardFeed> feeds, IReadOnlyCollection<string> named) =>
        named.Count > 0 || feeds.Any(f => f.Kind == "memories") ? null
            : "At least one feed or write names a star; add a feed for a star the dashboard is about";
    public static string? WritesProblem(IReadOnlyList<DashboardWrite>? writes)
    {
        if (writes is null) return null;
        if (writes.Count > MaxWrites) return $"writes holds at most {MaxWrites} entries";
        for (var i = 0; i < writes.Count; i++)
            if (writes[i] is null) return $"writes[{i}] is missing";
            else if (writes[i].Problem(i) is { } problem) return problem;
        return writes.Select(w => w.Name).Distinct().Count() == writes.Count ? null : "writes need distinct names";
    }
    public string? Problem()
    {
        if (!Text(Title, 120)) return "title is required, at most 120 characters";
        if (!Text(Description, 600, false)) return "description is at most 600 characters";
        if (!Text(Html, MaxHtml)) return $"html is the page, at most {MaxHtml:N0} characters";
        return FeedsProblem(Feeds) ?? WritesProblem(Writes) ?? NoteProblem(Note);
    }
}

// Saves and reads dashboards through the bounded database functions, which check the caller's role and, from migration
// 027, the dashboard's subject, its author rule and with memory.scopes on the stars it cites (DerivedScopes;
// docs/dev/memory.md sections 5 and 8). `host` is the map that says which stars a caller reads; without it nothing
// citing a star is read with scopes on.
public sealed class DashboardStore(NpgsqlDataSource writer, Relationships.RelationshipHost? host = null)
{
    readonly DerivedScopes scopes = new(writer, host);

    // A new dashboard (no `dashboard`) or its next version: the draft's content, or a copy of version `restore` as it
    // was last arranged. A draft without a layout is the page alone when new, and keeps the newest version's layout
    // when it is a next version. With scopes on it lands in the narrowest scope of the stars it names and the logs it
    // reads and writes, a log that names no subject being the caller's own.
    public async Task<(Guid Id, int Version)> Save(Guid principal, Guid? dashboard, int? restore, DashboardDraft? draft,
        IReadOnlyList<DashboardStar> stars, string note, CancellationToken ct, DashboardLayout? layout = null)
    {
        var subject = await scopes.LogSubject(principal, ct);
        var version = draft is null ? new JsonObject { ["note"] = note } : new JsonObject {
            ["title"] = draft.Title, ["description"] = draft.Description, ["html"] = draft.Html, ["note"] = note,
            ["feeds"] = JsonSerializer.SerializeToNode(draft.Feeds.Select(f => f.Normalized(subject)).ToArray(), DashboardDraft.Json),
            ["writes"] = JsonSerializer.SerializeToNode((draft.Writes ?? []).Select(w => w.Normalized(subject)).ToArray(), DashboardDraft.Json),
            ["stars"] = JsonSerializer.SerializeToNode(stars, DashboardDraft.Json) };
        if (layout is not null) version["layout"] = JsonNode.Parse(layout.ToJson());
        // The stars it names: a restore's are the restored version's, as it was last arranged.
        IEnumerable<string> named = [.. stars.Select(s => s.GraphId), .. Focused(layout)];
        if (subject is not null && restore is not null && dashboard is { } restoring
            && await Get(principal, restoring, restore, false, ct) is { } restored)
        {
            using var read = JsonDocument.Parse(restored);
            named = [.. read.RootElement.GetProperty("stars").EnumerateArray().Select(s => s.GetProperty("graphId").GetString()!),
                .. Focused(DashboardLayout.Of(read.RootElement).Layout)];
        }
        var (readable, cited) = await scopes.Citing(principal, named, ct);
        if (cited is not null) version["cited"] = cited;
        await using var cmd = writer.CreateCommand("SELECT save_dashboard(@p,@d,@r,@v::jsonb,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("d", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)dashboard ?? DBNull.Value);
        cmd.Parameters.AddWithValue("r", NpgsqlTypes.NpgsqlDbType.Integer, (object?)restore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("v", version.ToJsonString());
        cmd.Parameters.Add(DerivedScopes.Parameter("s", readable));
        using var saved = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        return (saved.RootElement.GetProperty("id").GetGuid(), saved.RootElement.GetProperty("version").GetInt32());
    }
    // The focus stars of a layout's map parts, which its dashboard names as it names its feeds' stars.
    static IEnumerable<string> Focused(DashboardLayout? layout) =>
        (layout?.Parts ?? []).Select(p => p.Focus?.Near?.Star?.Trim()).OfType<string>();
    // Every dashboard's latest version, most recently changed first, without pages or feeds: a JSON array.
    public Task<string> List(Guid principal, CancellationToken ct) => Read(principal, null, null, false, ct)!;
    // One dashboard's version (the latest unless given), with its page and feeds when `code`; null when unknown.
    public Task<string?> Get(Guid principal, Guid dashboard, int? version, bool code, CancellationToken ct) => Read(principal, dashboard, version, code, ct);
    async Task<string?> Read(Guid principal, Guid? dashboard, int? version, bool code, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT read_dashboards(@p,@d,@v,@c,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("d", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)dashboard ?? DBNull.Value);
        cmd.Parameters.AddWithValue("v", NpgsqlTypes.NpgsqlDbType.Integer, (object?)version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", code);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await scopes.Stars(principal, ct)));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    // The feeds a saved version reads, or null when that dashboard or version is unknown; with its layout as it stands
    // ({layout, layoutRevision, layoutNote, layoutSavedAt}), so a screen reading its feeds sees another screen's change.
    public async Task<(IReadOnlyList<DashboardFeed> Feeds, int Version, JsonObject Layout)?> Feeds(Guid principal, Guid dashboard, int? version, CancellationToken ct)
    {
        if (await Get(principal, dashboard, version, true, ct) is not { } found) return null;
        using var record = JsonDocument.Parse(found);
        var layout = new JsonObject();
        foreach (var name in LayoutFields) layout[name] = record.RootElement.TryGetProperty(name, out var value) ? JsonNode.Parse(value.GetRawText()) : null;
        return (record.RootElement.GetProperty("feeds").Deserialize<DashboardFeed[]>(DashboardDraft.Json)!,
            record.RootElement.GetProperty("version").GetInt32(), layout);
    }
    public static readonly string[] LayoutFields = ["layout", "layoutRevision", "layoutNote", "layoutSavedAt"];

    // Arranges the newest version in place (save_dashboard_layout): {id, version, layout, layoutRevision, saved}. Saved
    // is false, with the layout as it stands, when `baseRevision` is given and the layout has changed since; a version
    // that isn't the newest is refused (22023), and so is anything but an arrangement from the app's login. With
    // memory.scopes on it passes the subject of each map part's focus star, so that only a focus that would narrow the
    // dashboard is refused (a Family dashboard's map refocused onto a private star; docs/dev/memory.md section 8).
    public async Task<JsonObject> SaveLayout(Guid principal, Guid dashboard, int version, int? baseRevision, DashboardLayout layout, string note,
        CancellationToken ct)
    {
        var (readable, cited) = await scopes.Citing(principal, Focused(layout), ct);
        await using var cmd = writer.CreateCommand("SELECT save_dashboard_layout(@p,@d,@v,@b,@l::jsonb,@n,@s,@c::jsonb)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("d", dashboard);
        cmd.Parameters.AddWithValue("v", version);
        cmd.Parameters.AddWithValue("b", NpgsqlTypes.NpgsqlDbType.Integer, (object?)baseRevision ?? DBNull.Value);
        cmd.Parameters.AddWithValue("l", layout.ToJson());
        cmd.Parameters.AddWithValue("n", note);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", readable));
        cmd.Parameters.AddWithValue("c", NpgsqlTypes.NpgsqlDbType.Text, (object?)cited?.ToJsonString() ?? DBNull.Value);
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!)!.AsObject();
    }
    // Whether a dashboard exists, for a card part.
    public async Task<bool> Exists(Guid principal, Guid dashboard, CancellationToken ct) => await Get(principal, dashboard, null, false, ct) is not null;

    // Someone turns edits on or off for a dashboard in the app; on covers exactly the writes that version declares.
    public Task<string> AllowEdits(Guid principal, Guid dashboard, int version, bool allowed, CancellationToken ct) =>
        Call("SELECT allow_dashboard_edits(@p,@d,@v,@a,@s)::text", principal, ct, ("d", dashboard), ("v", version), ("a", allowed));
    // The declared write `name`, when edits are allowed for that version's writes; refused (42501) otherwise.
    public async Task<DashboardWrite> Begin(Guid principal, Guid dashboard, int version, string name, CancellationToken ct) =>
        JsonSerializer.Deserialize<DashboardWrite>(await Call("SELECT begin_dashboard_write(@p,@d,@v,@n,@s)::text", principal, ct,
            ("d", dashboard), ("v", version), ("n", name)), DashboardDraft.Json)!;
    // A Sheet or Doc change the API has made, with what it replaced. Its summary names the file, whose title can be
    // longer than the history keeps: it is shortened, never refused, since the change is already made.
    public const int MaxSummary = 300;
    public Task<string> Record(Guid principal, Guid dashboard, int version, string name, JsonObject target, string summary,
        JsonNode? before, JsonNode after, CancellationToken ct) =>
        Call("SELECT record_dashboard_write(@p,@d,@v,@n,@t::jsonb,@summary,@b::jsonb,@a::jsonb,@s)::text", principal, ct, ("d", dashboard),
            ("v", version), ("n", name), ("t", target.ToJsonString()), ("summary", Shortened(summary)), ("b", before?.ToJsonString() ?? "null"), ("a", after.ToJsonString()));
    static string Shortened(string summary)
    {
        if (summary.Length <= MaxSummary) return summary;
        var end = MaxSummary - 1;
        return summary[..(char.IsHighSurrogate(summary[end - 1]) ? end - 1 : end)] + "…";
    }
    // A log entry, its measurement and its history row, together.
    public Task<string> Log(Guid principal, Guid dashboard, int version, string name, JsonObject entry, CancellationToken ct) =>
        Call("SELECT write_dashboard_log(@p,@d,@v,@n,@e::jsonb,@s)::text", principal, ct, ("d", dashboard), ("v", version),
            ("n", name), ("e", entry.ToJsonString()));
    public Task<string> MarkUndone(Guid principal, Guid write, CancellationToken ct) =>
        Call("SELECT undo_dashboard_write(@p,@w,@s)::text", principal, ct, ("w", write));
    // One write (null when unknown), or the newest writes of one dashboard or of all.
    public async Task<string?> Writes(Guid principal, Guid? dashboard, Guid? write, int? limit, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT read_dashboard_writes(@p,@d,@w,@l,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("d", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)dashboard ?? DBNull.Value);
        cmd.Parameters.AddWithValue("w", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)write ?? DBNull.Value);
        cmd.Parameters.AddWithValue("l", NpgsqlTypes.NpgsqlDbType.Integer, (object?)limit ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await scopes.Stars(principal, ct)));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    // Deletes a dashboard, named by its current title as well as its id; its versions, writes and corner cards go too.
    public Task<string> Delete(Guid principal, Guid dashboard, string title, CancellationToken ct) =>
        Call("SELECT delete_dashboard(@p,@d,@t,@s)::text", principal, ct, ("d", dashboard), ("t", title));
    // The stars pinned to the sky, oldest first; pinning one answers them all. With scopes on a pin takes its star's
    // subject, so a person's pin of their private star is theirs.
    public Task<string> Pins(Guid principal, CancellationToken ct) => Call("SELECT read_sky_pins(@p,@s)::text", principal, ct);
    public async Task<string> SetPin(Guid principal, string star, bool pinned, CancellationToken ct)
    {
        var (readable, cited) = await scopes.Citing(principal, pinned ? [star] : [], ct);
        await using var cmd = writer.CreateCommand("SELECT set_sky_pin(@p,@star,@x,@s,@c::jsonb)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("star", star);
        cmd.Parameters.AddWithValue("x", pinned);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", readable));
        cmd.Parameters.AddWithValue("c", NpgsqlTypes.NpgsqlDbType.Text, (object?)cited?.ToJsonString() ?? DBNull.Value);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    // The corners showing a dashboard's card: {corner: dashboardId}. Setting null puts the stat card back.
    public Task<string> Cards(Guid principal, CancellationToken ct) => Call("SELECT read_ambient_cards(@p,@s)::text", principal, ct);
    public async Task<string> SetCard(Guid principal, string corner, Guid? dashboard, CancellationToken ct)
    {
        await using var cmd = writer.CreateCommand("SELECT set_ambient_card(@p,@c,@d,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("c", corner);
        cmd.Parameters.AddWithValue("d", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)dashboard ?? DBNull.Value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await scopes.Stars(principal, ct)));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    // A function taking the caller (@p) and, last, the stars it reads (@s).
    async Task<string> Call(string sql, Guid principal, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = writer.CreateCommand(sql);
        cmd.Parameters.AddWithValue("p", principal);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await scopes.Stars(principal, ct)));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
