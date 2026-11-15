using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Knowledge.Relationships;
using Npgsql;
using NpgsqlTypes;

namespace Knowledge;

// Live screens (docs/dev/screens.md): every open copy of the app is a screen that listens for commands, sent by an AI
// client (the harness tools list_screens and control_screen) or by another screen (app_screens). The actions and their
// arguments are checked here, and so are the settings a screen keeps; the database functions (migration 020) keep the
// screens and commands and every rule that needs them, except a new screen's distinct name and the number it keeps
// (ScreenStore.Register, so piece 2 needs no migration), and ScreenWaits only decides when to look again. Piece 3's
// move, resize and focus_map arrange the parts of what a screen shows (section 10): a dashboard's for the household,
// Home's for that screen.
public static partial class ScreenCommands
{
    public static readonly string[] Actions = ["open_star", "open_dashboard", "home", "layout", "move_tile", "corner_card", "search", "undo",
        "full_screen", "set_home", "move", "resize", "focus_map"];
    public static readonly string[] Layouts = ["corners", "hero", "band"];
    // Home's places, each named after the tile that sits there by default; the corner cards use the same names.
    public static readonly string[] Places = Mcp.AppEndpoint.Corners;
    // Where move sends a part, and how resize changes it (docs/dev/screens.md 10.6).
    public static readonly string[] MovePlaces = ["left", "right", "up", "down", "top_left", "top_right", "bottom_left", "bottom_right"];
    public static readonly string[] Sizes = ["bigger", "smaller", "wider", "narrower", "taller", "shorter"];
    // What each action takes, required then optional: corner_card's dashboard may be left out (or null) to clear the card,
    // and set_home's to restore the default Home; full_screen without one shows the dashboard open on that screen. move,
    // resize and focus_map take a dashboard as a guard (null: the screen's default Home), which the engine fills in from
    // what the screen last reported when it is left out (Resolve).
    static readonly Dictionary<string, (string[] Required, string[] Optional)> Takes = new(StringComparer.Ordinal)
    {
        ["open_star"] = (["star"], []), ["open_dashboard"] = (["dashboard"], []), ["home"] = ([], []), ["layout"] = (["layout"], []),
        ["move_tile"] = (["tile", "to"], []), ["corner_card"] = (["corner"], ["dashboard"]), ["search"] = (["query"], []), ["undo"] = ([], []),
        ["full_screen"] = ([], ["dashboard"]), ["set_home"] = ([], ["dashboard"]),
        ["move"] = (["part"], ["place", "with", "x", "y", "dashboard"]), ["resize"] = (["part"], ["size", "w", "h", "dashboard"]),
        ["focus_map"] = ([], ["focus", "part", "dashboard"]),
    };
    // The actions whose dashboard may be null: the screen gets null when it is left out.
    static readonly string[] Clears = ["corner_card", "set_home"];
    // The actions that arrange parts, whose dashboard may be null (the screen's default Home), and is filled in when left out.
    public static readonly string[] Arranges = ["move", "resize", "focus_map"];
    public const int MaxQuery = 100, MaxStar = 1000;
    // A listen waits at most 25 seconds (the app server gives the call 35); a send waits 8 for the screen's report.
    public const int MaxHold = 25;
    public static readonly TimeSpan Outcome = TimeSpan.FromSeconds(8);

    public static bool Id(string? id) => id is not null && IdPattern().IsMatch(id);
    // A screen's name, as its browser chose it: 1-60 characters once trimmed, and no control characters. Characters
    // are counted as the table and the schemas count them, so an emoji is one.
    public static bool Name(string? name) => name is not null && Characters(name.Trim()) is >= 1 and <= 60 && !name.Any(char.IsControl);
    static int Characters(string text) => text.EnumerateRunes().Count();

    // The first problem with an action's arguments, naming the argument and what it takes; null when they fit, with the
    // arguments as the command stores them (a query trimmed, a dashboard id in lower case, corner_card's and set_home's
    // dashboard null when left out, a focus as a layout stores it, and focus_map's null when left out).
    public static string? Problem(string action, IReadOnlyDictionary<string, JsonElement> given, out JsonObject args)
    {
        args = [];
        if (!Takes.TryGetValue(action, out var takes)) return $"action is one of {string.Join(", ", Actions)}";
        string[] all = [.. takes.Required, .. takes.Optional];
        foreach (var name in given.Keys)
            if (!all.Contains(name))
                return $"{(Plain().IsMatch(name) ? name : "That argument")} is not an argument of {action}; "
                    + (all.Length == 0 ? "it takes none" : $"it takes {string.Join(" and ", all)}");
        foreach (var name in takes.Required)
            if (!given.ContainsKey(name)) return $"{action} needs {name}: {Expected(name)}";
        foreach (var (name, value) in given)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int? number = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
            if (name == "focus")
            {
                if (value.ValueKind == JsonValueKind.Null) { args[name] = null; continue; }
                if (MapFocus.Read(value, name, out var focus) is { } unfocused) return unfocused;
                args[name] = JsonSerializer.SerializeToNode(focus!.Normalized(), DashboardLayout.Json);
                continue;
            }
            var (fits, stored) = name switch
            {
                "star" => (text is { Length: > 0 and <= MaxStar }, JsonValue.Create(text)),
                "dashboard" => Guid.TryParseExact(text, "D", out var id) ? (true, JsonValue.Create(id.ToString()))
                    : ((Clears.Contains(action) || Arranges.Contains(action)) && value.ValueKind == JsonValueKind.Null, null),
                "layout" => (Layouts.Contains(text), JsonValue.Create(text)),
                "tile" or "to" or "corner" => (Places.Contains(text), JsonValue.Create(text)),
                "part" or "with" => (DashboardPart.IdFits(text), JsonValue.Create(text)),
                "place" => (MovePlaces.Contains(text), JsonValue.Create(text)),
                "size" => (Sizes.Contains(text), JsonValue.Create(text)),
                "x" or "y" => (number is >= 0 and < DashboardLayout.Cells, JsonValue.Create(number)),
                "w" or "h" => (number is >= 1 and <= DashboardLayout.Cells, JsonValue.Create(number)),
                _ => Query(text) is { } query ? (true, JsonValue.Create(query)) : (false, null),
            };
            if (!fits) return $"{name} must be {Expected(name)}";
            args[name] = stored;
        }
        if (Clears.Contains(action) && !args.ContainsKey("dashboard")) args["dashboard"] = null;
        if (action == "focus_map" && !args.ContainsKey("focus")) args["focus"] = null;
        return Shape(action, args);
    }

    // move takes exactly one of place, with, or x and y, and resize one of size, or w and h; x and y, and w and h, each
    // come with their pair, and with names another part than part.
    static string? Shape(string action, JsonObject args)
    {
        bool Has(string name) => args.ContainsKey(name);
        foreach (var (one, other) in new[] { ("x", "y"), ("y", "x"), ("w", "h"), ("h", "w") })
            if (Has(one) && !Has(other)) return $"{one} is given with {other}";
        if (Has("with") && (string?)args["with"] == (string?)args["part"]) return "with names another part than part";
        var given = action switch
        {
            "move" => (Has("place") ? 1 : 0) + (Has("with") ? 1 : 0) + (Has("x") ? 1 : 0),
            "resize" => (Has("size") ? 1 : 0) + (Has("w") ? 1 : 0),
            _ => 1,
        };
        return (action, given) switch
        {
            ("move", 0) => "move needs one of place, with, or x and y",
            ("move", > 1) => "move takes one of place, with, or x and y, not more",
            ("resize", 0) => "resize needs size, or w and h",
            ("resize", > 1) => "resize takes size, or w and h, not both",
            _ => null,
        };
    }

    // A screen's settings as it keeps them (docs/dev/screens.md 5, 8 and 10.5), each member optional, since a screen older
    // than piece 2 keeps no home: layout one of Layouts, tiles each tile in exactly one place, home null (the default
    // Home) or {dashboard: a dashboard's id}, and grid its Home grid (DashboardLayout.HomeGrid; never null, since a screen
    // without one leaves it out). A Home dashboard deleted since is not refused: the screen falls back to the default
    // Home and says so in what it shows.
    public static bool Settings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || settings.EnumerateObject().GroupBy(x => x.Name).Any(g => g.Count() > 1)) return false;
        return settings.EnumerateObject().All(x => x.Name switch
        {
            "layout" => x.Value.ValueKind == JsonValueKind.String && Layouts.Contains(x.Value.GetString()),
            "tiles" => x.Value.ValueKind == JsonValueKind.Object && x.Value.EnumerateObject().Select(t => t.Name).Order().SequenceEqual(Places.Order())
                && x.Value.EnumerateObject().Select(t => t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString() : null).Order().SequenceEqual(Places.Order()),
            "home" => x.Value.ValueKind == JsonValueKind.Null || x.Value.ValueKind == JsonValueKind.Object && x.Value.EnumerateObject().Count() == 1
                && x.Value.TryGetProperty("dashboard", out var dashboard) && dashboard.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(dashboard.GetString(), "D", out _),
            "grid" => DashboardLayout.HomeGrid(x.Value),
            _ => false,
        });
    }

    // A new screen's name, distinct from the names `taken` by the other screens seen in the last 30 days, compared once
    // trimmed and in any case, as control_screen matches names: the name itself while no such screen has it, else with
    // the lowest free " 2", " 3"... added, the name cut to fit 60 characters. A name outside the bounds is left as it
    // came, for the database to refuse.
    public static string Distinct(string name, IEnumerable<string> taken)
    {
        if (!Name(name)) return name;
        var used = taken.Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name.Trim())) return name;
        for (var n = 2; ; n++)
        {
            var named = WithNumber(name, n);
            if (!used.Contains(named)) return named;
        }
    }

    // Whether `stored` is `sent` with the number Distinct gives ("Windows PC · Chrome 2" for "Windows PC · Chrome"),
    // compared as Distinct compares names. A listen never strips that number: the browser may have sent its bare
    // default before it took the numbered name from an answer.
    public static bool Numbered(string stored, string sent) =>
        Name(sent) && NumberAfter().Match(stored.Trim()) is { Success: true } number
        && int.TryParse(number.Groups[1].ValueSpan, out var n)
        && string.Equals(WithNumber(sent, n), stored.Trim(), StringComparison.OrdinalIgnoreCase);

    // The name trimmed, with " n" after it, cut to fit 60 characters and never ending in a space before the number.
    static string WithNumber(string name, int n)
    {
        var suffix = " " + n;
        return string.Concat(name.Trim().EnumerateRunes().Take(60 - suffix.Length)).TrimEnd() + suffix;
    }

    static string Expected(string name) => name switch
    {
        "star" => "a star's graphId from the map",
        "dashboard" => "a dashboard's id (a UUID)",
        "layout" => $"one of {string.Join(", ", Layouts)}",
        "tile" or "to" or "corner" => $"one of {string.Join(", ", Places)}",
        "part" or "with" => "a part's id from list_screens: a lowercase letter, then up to 31 lowercase letters, digits or underscores",
        "place" => $"one of {string.Join(", ", MovePlaces)}",
        "size" => $"one of {string.Join(", ", Sizes)}",
        "x" or "y" => $"a whole number 0-{DashboardLayout.Cells - 1}",
        "w" or "h" => $"a whole number 1-{DashboardLayout.Cells}",
        _ => $"1-{MaxQuery} characters once trimmed, with no control characters",
    };
    static string? Query(string? text) =>
        text?.Trim() is { } query && Characters(query) is >= 1 and <= MaxQuery && !query.Any(char.IsControl) ? query : null;

    // open_star with a dashboard's star (the app makes them: <catalog provider>:dashboard:<uuid>) opens that dashboard,
    // whose existence the database checks; any other star must be on the map now. Null when it is not. move, resize and
    // focus_map are checked as Arranged says, against `showing`, what the target screen last reported. With memory.scopes
    // on (`scope`, from ScreenStore.Scope; docs/dev/memory.md 7.5) every dashboard a command names is read as its sender
    // (DashboardStore.Get), so one it can't read is refused exactly as an unknown one is, and what goes to a screen whose
    // login doesn't read all its sender reads is checked against that login too (Carried).
    public static async Task<(string Action, JsonObject Args)?> Resolve(RelationshipHost host, DashboardStore? dashboards, Guid principal, string action,
        JsonObject args, JsonNode? showing, CancellationToken ct, ScreenScope? scope = null)
    {
        (string Action, JsonObject Args)? resolved;
        if (Arranges.Contains(action)) resolved = (action, await Arranged(host, dashboards, principal, action, args, showing, ct, scope is not null));
        else if (action != "open_star") resolved = (action, args);
        else
        {
            var star = (string)args["star"]!;
            resolved = DashboardOf(star) is { } dashboard ? ("open_dashboard", new JsonObject { ["dashboard"] = dashboard.ToString() })
                : (await host.Known(principal, [star], ct)).Contains(star) ? (action, args) : null;
        }
        if (scope is null || resolved is not { } command) return resolved;
        // undo names nothing, yet the screen saves it back through its own login onto what its stack holds (a dashboard's
        // earlier layout among them): only a sender that reads all the screen's login reads may send it.
        if (command.Action == "undo" && !scope.SenderReadsAll) throw new ScreenRefused(404, NotYours);
        // Arranged has read the dashboard of move, resize and focus_map as the sender already.
        if (!Arranges.Contains(command.Action) && Named(command.Args) is { } named && await Need(dashboards).Get(principal, named, null, false, ct) is null)
            throw new ScreenRefused(404, "Unknown dashboard.");
        await Carried(host, dashboards, command.Action, command.Args, scope, ct);
        return command;
    }

    // With memory.scopes on, the screen a command goes to as the engine knows it (ScreenStore.Scope): the login its last
    // report came from (null when none is known), whether that login reads all its sender reads (reads_all_of, migration
    // 027), whether the sender is a viewer session (an app login bound to a person), and whether the sender reads all
    // that login reads (false while none is known). Null with scopes off.
    public sealed record ScreenScope(Guid? Target, bool ReadsAll, bool FromViewer, bool SenderReadsAll);
    public const string Elsewhere = "That screen can't show that: it reads another scope than yours.";
    public const string NotYours = "That screen's last change is in another scope than yours, so it can't be undone from here.";

    // With scopes on, a command for a screen whose login doesn't read all its sender reads (another person's session, or
    // the household view's screen for a person's client) carries only what that login reads: the star it opens or focuses
    // on and the dashboard it names, each refused (404) otherwise, and anything at all for a screen whose login is not
    // known yet. A search sent from a viewer session goes only to its person's own screens: its query is what that
    // person typed, and would be reported to whoever reads the other screen (memory.md 7.5).
    static async Task Carried(RelationshipHost host, DashboardStore? dashboards, string action, JsonObject args, ScreenScope scope, CancellationToken ct)
    {
        if (scope.ReadsAll) return;
        if (action == "search" && scope.FromViewer)
            throw new ScreenRefused(400, "A search from a person's own session is sent only to their own screens.");
        string?[] stars = [action == "open_star" ? (string?)args["star"] : null, (string?)args["focus"]?["near"]?["star"]];
        foreach (var star in stars.OfType<string>())
            if (scope.Target is not { } target || !await Reads(() => host.Known(target, [star], ct), found => found.Contains(star)))
                throw new ScreenRefused(404, Elsewhere);
        if (Named(args) is { } dashboard
            && (scope.Target is not { } reader || !await Reads(() => Need(dashboards).Get(reader, dashboard, null, false, ct), found => found is not null)))
            throw new ScreenRefused(404, Elsewhere);
    }
    // Whether another login reads it: one that reads no map or no dashboards now (a session ended since) reads nothing.
    static async Task<bool> Reads<T>(Func<Task<T>> read, Func<T, bool> holds)
    {
        try { return holds(await read()); }
        catch (UnauthorizedAccessException) { return false; }
        catch (PostgresException e) when (e.SqlState == "42501") { return false; }
    }
    static Guid? Named(JsonObject args) => args["dashboard"] is JsonValue named && Guid.TryParse((string?)named, out var id) ? id : null;
    static DashboardStore Need(DashboardStore? dashboards) => dashboards ?? throw new InvalidOperationException("No dashboard store");

    // move, resize and focus_map (docs/dev/screens.md 10.7). Without a dashboard, the guard is filled in from what the
    // target last reported: the dashboard it shows (view dashboard or full_screen), null at its default Home (view home),
    // and otherwise left out, so the screen checks the command against what it shows. With a dashboard, part and with
    // must be parts of its newest layout (the page alone when it has none), and focus_map's part a map (left out, the one
    // map); with null, part and with must be Home's, and Home's map takes no focus. A focus star must be on the map now.
    // What doesn't hold is refused with the reason: 404 for a part or star that isn't there, 400 otherwise. With scopes on
    // (`scoped`) a screen showing a dashboard its sender can't read reports no dashboard to it (read_screens, migration
    // 027): left to the screen, the command would arrange that dashboard all the same, so it is refused as that one is.
    static async Task<JsonObject> Arranged(RelationshipHost host, DashboardStore? dashboards, Guid principal, string action, JsonObject args,
        JsonNode? showing, CancellationToken ct, bool scoped = false)
    {
        if (!args.ContainsKey("dashboard") && Guard(showing, out var shown)) args["dashboard"] = shown?.ToString();
        else if (scoped && !args.ContainsKey("dashboard") && showing?["view"] is JsonValue view && view.TryGetValue<string>(out var named)
            && named is "dashboard" or "full_screen")
            throw new ScreenRefused(404, "Unknown dashboard.");
        var (part, with) = ((string?)args["part"], (string?)args["with"]);
        if (args.ContainsKey("dashboard") && args["dashboard"] is null)
        {
            if (action == "focus_map") throw new ScreenRefused(400, "Home's map takes no focus; focus_map changes a map part of a dashboard");
            foreach (var (name, id) in new[] { ("part", part), ("with", with) })
                if (id is not null && !DashboardLayout.HomeParts.Contains(id))
                    throw new ScreenRefused(404, $"{name} {id} is not one of Home's parts: {string.Join(", ", DashboardLayout.HomeParts)}");
        }
        else if (Named(args) is { } id)
        {
            var found = await Need(dashboards).Get(principal, id, null, false, ct)
                ?? throw new ScreenRefused(404, "Unknown dashboard.");
            using var read = JsonDocument.Parse(found);
            var parts = DashboardLayout.Of(read.RootElement).Layout.Parts;
            foreach (var (name, given) in new[] { ("part", part), ("with", with) })
                if (given is not null && !parts.Any(p => p.Id == given))
                    throw new ScreenRefused(404, $"{name} {given} is not a part of that dashboard; list_screens shows the parts a screen shows");
            var maps = parts.Where(p => p.Kind == "map").Select(p => p.Id).ToArray();
            if (action == "focus_map" && part is null && maps.Length != 1)
                throw new ScreenRefused(400, maps.Length == 0 ? "That dashboard has no map part"
                    : $"That dashboard has {maps.Length} map parts: give part, one of {string.Join(", ", maps)}");
            if (action == "focus_map" && part is not null && !maps.Contains(part))
                throw new ScreenRefused(400, $"part {part} is not a map; focus_map changes a map part's focus");
        }
        if (args["focus"]?["near"]?["star"] is JsonValue near && (string?)near is { } star && !(await host.Known(principal, [star], ct)).Contains(star))
            throw new ScreenRefused(404, "focus.near.star is not a star on the map now; find stars with explore_graph");
        return args;
    }

    // The dashboard a screen shows, as it last reported it: its id while it shows a dashboard (in a view or full screen),
    // null at its default Home, and nothing while it shows anything else or has reported nothing.
    static bool Guard(JsonNode? showing, out Guid? dashboard)
    {
        dashboard = null;
        if (showing is not JsonObject shows || shows["view"] is not JsonValue view || !view.TryGetValue<string>(out var named)) return false;
        if (named == "home") return true;
        if (named is not ("dashboard" or "full_screen") || shows["dashboard"]?["id"] is not JsonValue id || !id.TryGetValue<string>(out var text)
            || !Guid.TryParseExact(text, "D", out var shown)) return false;
        dashboard = shown;
        return true;
    }
    public static Guid? DashboardOf(string star)
    {
        var prefix = CatalogStars.Provider + ":dashboard:";
        return star.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(star[prefix.Length..], "D", out var id) ? id : null;
    }

    // A screen as list answers it, for choosing control_screen's target. ActiveAt is when a person last used it, as the
    // screen reports it (showing.activeAt, which the app server stamps by its own clock; null when it reports none, as a
    // screen older than piece 2 does), and never later than the engine last heard from it, so a clock running ahead
    // does not seem used in the future. Listening is whether it still listens (ScreenWaits.Listening): a screen whose app
    // was just hidden or closed counts as open for a minute more, but no longer takes commands.
    public sealed record Seen(string Id, string Name, bool Open, DateTimeOffset SeenAt, DateTimeOffset? ActiveAt = null, bool Listening = false,
        JsonNode? Showing = null);
    public static IReadOnlyList<Seen> Listed(JsonArray screens) => screens.Select(s =>
    {
        var seen = DateTimeOffset.Parse((string)s!["seenAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        var active = s["showing"] is JsonObject showing && showing["activeAt"] is JsonValue value && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at < seen ? at : seen : (DateTimeOffset?)null;
        return new Seen((string)s["id"]!, (string)s["name"]!, (bool)s["open"]!, seen, active, Showing: s["showing"]?.DeepClone());
    }).ToArray();

    // A screen's Home as it reports it (showing.home): {dashboard: {id, title}} while a dashboard is its Home, with
    // missing: true when that dashboard was deleted and the screen shows the default Home instead; else null, the default
    // Home (the sky and tiles), which a screen that reports none (one older than piece 2) has too.
    public static JsonObject? Home(JsonNode? showing)
    {
        if (showing is not JsonObject shows || shows["home"] is not JsonObject home || home["dashboard"] is not JsonObject dashboard
            || dashboard["id"] is not JsonValue id || !id.TryGetValue<string>(out var dashboardId) || !Guid.TryParseExact(dashboardId, "D", out _)
            || dashboard["title"] is not JsonValue title || !title.TryGetValue<string>(out var named))
            return null;
        var listed = new JsonObject { ["dashboard"] = new JsonObject { ["id"] = dashboardId, ["title"] = named } };
        if (home["missing"] is JsonValue missing && missing.TryGetValue<bool>(out var gone) && gone) listed["missing"] = true;
        return listed;
    }

    // control_screen's target: without `asked`, among the open screens that still listen (any open one when none does),
    // the one a person used most recently, then the one seen most recently (Q-147); else the screen with that id (in any
    // case when only one id matches that way), or the one open screen with that name (in any case). Otherwise the
    // reason, which says what to do instead and never repeats what was asked; two open screens of that name are listed
    // with their ids.
    public static (Seen? Target, string? Problem) Target(IReadOnlyList<Seen> screens, string? asked)
    {
        var open = screens.Where(s => s.Open).OrderByDescending(s => s.Listening).ThenByDescending(s => s.ActiveAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(s => s.SeenAt).ToArray();
        const string Look = "list_screens shows the screens seen in the last 24 hours";
        if (string.IsNullOrWhiteSpace(asked))
            return open.Length > 0 ? (open[0], null) : (null, $"No screen is open now: the app has to be open on a screen to show anything; {Look}");
        if ((screens.FirstOrDefault(s => s.Id == asked)
            ?? (screens.Where(s => string.Equals(s.Id, asked.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray() is [var only] ? only : null))
            is { } byId)
            return byId.Open ? (byId, null) : (null, $"{byId.Name} is not open now (last seen {byId.SeenAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC); {Look}");
        bool Named(Seen s) => string.Equals(s.Name.Trim(), asked.Trim(), StringComparison.OrdinalIgnoreCase);
        var named = open.Where(Named).ToArray();
        if (named.Length == 1) return (named[0], null);
        if (named.Length > 1)
            return (null, $"{named.Length} open screens have that name: {string.Join("; ", named.Select(s => $"{s.Name} (id {s.Id})"))}. Give screen as one of their ids");
        return (null, screens.Any(Named) ? $"That screen is not open now; {Look}" : $"No screen has that id or name; {Look}");
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")] private static partial Regex Plain();
    [GeneratedRegex(" ([2-9]|[1-9][0-9]{1,8})$")] private static partial Regex NumberAfter();
}

// control_screen's action, listed as an enum of the names in sections 2, 8 and 10.6.
[JsonConverter(typeof(JsonStringEnumConverter<ScreenAction>))]
public enum ScreenAction
{
    [JsonStringEnumMemberName("open_star")] OpenStar,
    [JsonStringEnumMemberName("open_dashboard")] OpenDashboard,
    [JsonStringEnumMemberName("home")] Home,
    [JsonStringEnumMemberName("layout")] Layout,
    [JsonStringEnumMemberName("move_tile")] MoveTile,
    [JsonStringEnumMemberName("corner_card")] CornerCard,
    [JsonStringEnumMemberName("search")] Search,
    [JsonStringEnumMemberName("undo")] Undo,
    [JsonStringEnumMemberName("full_screen")] FullScreen,
    [JsonStringEnumMemberName("set_home")] SetHome,
    [JsonStringEnumMemberName("move")] Move,
    [JsonStringEnumMemberName("resize")] Resize,
    [JsonStringEnumMemberName("focus_map")] FocusMap,
}

// A command the rules refuse, with a reason fit to show: 400 for bad input, 404 for an unknown star, dashboard or
// screen, or one that is not open.
public sealed class ScreenRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public static ScreenRefused From(PostgresException e) => new(e.SqlState == "P0002" ? 404 : 400,
        e.SqlState is "P0002" or "22023" ? e.MessageText + "." : "The engine refused that entry.");
    // The function's own reasons (migration 020) and a value Postgres would not take.
    public static bool Is(PostgresException e) => e.SqlState is "22023" or "P0002" or "22P02" or "22P05";
}

// Wakes a waiting listen when a command is sent to its screen, and a waiting send when its command's screen reports back.
// In memory only: the engine is one process, and a restart drops the waiters (the screens simply listen again). A wait
// never holds a database connection, and whoever waited reads the database again afterwards. When the engine stops,
// every wait ends as if its time ran out, so a held listen answers at once and never holds up the stop. It also knows
// which screens still listen, for control_screen's target.
public sealed class ScreenWaits(CancellationToken stopping, TimeProvider? clock = null)
{
    public ScreenWaits() : this(CancellationToken.None) { }
    public ScreenWaits(IHostApplicationLifetime lifetime) : this(lifetime.ApplicationStopping) { }
    readonly CancellationToken stopping = stopping;
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    // A screen whose listen was answered listens again in a moment: at once, or once it has shown the command it took.
    public static readonly TimeSpan Between = TimeSpan.FromSeconds(10);
    // Each screen's listens under way, and when its last one ended answered (MinValue once one was abandoned).
    readonly Dictionary<string, (int Now, DateTimeOffset Answered)> listens = new(StringComparer.Ordinal);

    internal sealed class Signal { public readonly TaskCompletionSource Woken = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Waiting; }
    readonly Dictionary<string, Signal> signals = new(StringComparer.Ordinal);
    readonly object gate = new();

    // Begun before the database is read, so a wake that comes between that read and the wait is not lost. The tabs of
    // one browser share a screen, so one wake can end several waits.
    public Wait ForScreen(string screen) => Begin("screen:" + screen);
    public Wait ForCommand(Guid command) => Begin("command:" + command);
    public void WakeScreen(string screen) => Wake("screen:" + screen);
    public void WakeCommand(Guid command) => Wake("command:" + command);
    // The keys someone waits on now; each goes once its last wait ends.
    public int Waiting { get { lock (gate) return signals.Count; } }

    // Marks one listen of `screen` from its start to its end. One whose caller went away (ct: the app was hidden or
    // closed, or went offline) ends its screen's listening at once, unless another of its listens is still under way.
    public IDisposable Hearing(string screen, CancellationToken ct)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var stale in listens.Where(l => l.Value.Now == 0 && now - l.Value.Answered >= Between).Select(l => l.Key).ToArray())
                listens.Remove(stale);
            listens[screen] = listens.TryGetValue(screen, out var was) ? (was.Now + 1, was.Answered) : (1, DateTimeOffset.MinValue);
        }
        return new Heard(this, screen, ct);
    }
    // Whether `screen` listens: a listen of it is under way, or one was answered less than Between ago.
    public bool Listening(string screen)
    {
        lock (gate) return listens.TryGetValue(screen, out var l) && (l.Now > 0 || clock.GetUtcNow() - l.Answered < Between);
    }
    void Ended(string screen, bool answered)
    {
        lock (gate)
            if (listens.TryGetValue(screen, out var was))
                listens[screen] = (was.Now - 1, answered ? clock.GetUtcNow() : DateTimeOffset.MinValue);
    }
    sealed class Heard(ScreenWaits owner, string screen, CancellationToken ct) : IDisposable
    {
        bool ended;
        public void Dispose() { if (!ended) { ended = true; owner.Ended(screen, !ct.IsCancellationRequested); } }
    }

    Wait Begin(string key)
    {
        lock (gate)
        {
            if (!signals.TryGetValue(key, out var signal)) signals[key] = signal = new();
            signal.Waiting++;
            return new(this, key, signal);
        }
    }
    void Wake(string key)
    {
        Signal? signal;
        lock (gate) if (!signals.Remove(key, out signal)) return;
        signal.Woken.TrySetResult();
    }
    void End(string key, Signal signal)
    {
        lock (gate)
            if (--signal.Waiting == 0 && signals.TryGetValue(key, out var current) && current == signal) signals.Remove(key);
    }

    public sealed class Wait : IDisposable
    {
        readonly ScreenWaits owner; readonly string key; readonly Signal signal; bool ended;
        internal Wait(ScreenWaits owner, string key, Signal signal) { this.owner = owner; this.key = key; this.signal = signal; }
        // True when woken, false when the time ran out or the engine is stopping; a cancelled request (the caller went
        // away) ends it at once.
        public async Task<bool> Until(TimeSpan timeout, CancellationToken ct)
        {
            using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, owner.stopping);
            try { await signal.Woken.Task.WaitAsync(timeout, either.Token); return true; }
            catch (TimeoutException) { return false; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        public void Dispose() { if (!ended) { ended = true; owner.End(key, signal); } }
    }
}

// The screens and their commands, through the database functions of migration 020, which check the caller's role:
// the app's login listens, reports, renames and sends as one of its screens; a harness login lists and sends as the
// assistant. A screen's id is the app login's that first listened with it (migration 029, docs/dev/memory.md 7.5): no
// other app login listens as it, takes its commands, reports for it, sends as it or renames it, so the settings a listen
// and a rename answer reach that login alone (42501 otherwise, a 403 at app_screens); the household view's screens are
// every login of the shared app token's, a new token's included. Only a new screen's name is read from the table first,
// and never answered. With memory.scopes on (migration 027,
// docs/dev/memory.md 7.5) what a screen shows and reports is its login's view, told to another principal only as far as
// that principal reads it; `host` is the map that says which stars a reader reads, without which no star is told.
public sealed class ScreenStore(NpgsqlDataSource source, ScreenWaits waits, RelationshipHost? host = null)
{
    readonly DerivedScopes scopes = new(source, host);

    // The screen's oldest pending command; with none, a wait of up to `hold` for one and one more look.
    // {command (or null), screen: {id, name, settings}}, the name as the engine stored it. A listen counts as its screen
    // listening only once its first look has been answered, so one the database refuses (another login's screen, or a
    // caller that is not the app's), or that its caller gives up on before it is answered, never makes the screen seem to
    // listen nor ends the listening of the login the screen is. The wait is begun first, so no wake is lost.
    public async Task<JsonObject> Listen(Guid principal, string screen, string name, TimeSpan hold, JsonElement? showing, JsonElement? settings,
        CancellationToken ct)
    {
        using var wait = waits.ForScreen(screen);
        var first = await Register(principal, screen, name, showing, settings, ct);
        using var hearing = waits.Hearing(screen, ct);
        if (first["command"] is not null || hold <= TimeSpan.Zero) return first;
        await wait.Until(hold, ct);
        return await Call("SELECT listen_screen_again(@p,@s)::text", ct, ("p", principal), ("s", screen));
    }

    // A listen's first look (listen_screen). A screen the engine does not know yet is stored with the name it sends unless
    // another screen seen in the last 30 days has that name, and then with " 2", " 3"... added (ScreenCommands.Distinct),
    // under the lock listen_screen takes to add a screen, so two new screens never take one name. A screen the engine
    // knows keeps the name it sends, so a name a person chose is never changed, except that a listen never strips the
    // number the engine gave: one sent before its browser took the numbered name carries the bare default. A rename
    // (rename_screen) still sets any name.
    async Task<JsonObject> Register(Guid principal, string screen, string name, JsonElement? showing, JsonElement? settings, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string? stored;
        await using (var named = Database.Command(c, tx, "SELECT name FROM screens WHERE id=@s", ("s", screen)))
            stored = await named.ExecuteScalarAsync(ct) as string;
        if (stored is null)
        {
            await Database.Execute(c, tx, "SELECT pg_advisory_xact_lock(hashtext('screens'))", ct);
            var taken = new List<string>();
            await using (var names = Database.Command(c, tx, "SELECT name FROM screens WHERE id<>@s AND seen_at>now()-interval '30 days'", ("s", screen)))
            await using (var reader = await names.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) taken.Add(reader.GetString(0));
            name = ScreenCommands.Distinct(name, taken);
        }
        else if (ScreenCommands.Numbered(stored, name)) name = stored;
        var first = await Call(new NpgsqlCommand("SELECT listen_screen(@p,@s,@n,@w::jsonb,@t::jsonb)::text", c, tx), ct, ("p", principal), ("s", screen),
            ("n", name), ("w", showing?.GetRawText()), ("t", settings?.GetRawText()));
        await tx.CommitAsync(ct);
        return first;
    }

    // A screen's report on a delivered command, which wakes whoever waits for it. {ok: true}.
    public async Task<JsonObject> Done(Guid principal, string screen, Guid command, string result, JsonElement? detail, JsonElement? showing,
        CancellationToken ct)
    {
        var done = await Call("SELECT done_screen_command(@p,@s,@c,@r,@d::jsonb,@w::jsonb)::text", ct, ("p", principal), ("s", screen),
            ("c", command), ("r", result), ("d", detail?.GetRawText()), ("w", showing?.GetRawText()));
        waits.WakeCommand(command);
        return done;
    }

    // A command already checked (ScreenCommands.Problem and Resolve) for an open screen, from the screen `from` (the app)
    // or the assistant (null, a harness login). The screen is woken, and the answer is the command as it stands once the
    // screen has reported or `outcome` has passed.
    public async Task<JsonObject> Send(Guid principal, string to, string action, JsonObject args, string? from, TimeSpan outcome, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        using var wait = waits.ForCommand(id);
        var sent = await Call("SELECT send_screen_command(@p,@c,@to,@a,@x::jsonb,@f)::text", ct, ("p", principal), ("c", id), ("to", to),
            ("a", action), ("x", args.ToJsonString()), ("f", from));
        waits.WakeScreen(to);
        if (outcome <= TimeSpan.Zero) return sent;
        await wait.Until(outcome, ct);
        return await Command(principal, id, ct) ?? sent;
    }

    // One command as it stands (an overdue one expires first), or null when unknown.
    public async Task<JsonObject?> Command(Guid principal, Guid command, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT read_screen_command(@p,@c)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("c", command);
        return await cmd.ExecuteScalarAsync(ct) is string found ? JsonNode.Parse(found)!.AsObject() : null;
    }

    // The screens as control_screen chooses among them (ScreenCommands.Target): as listed, and whether each still listens.
    public async Task<IReadOnlyList<ScreenCommands.Seen>> Seen(Guid principal, CancellationToken ct) =>
        [.. ScreenCommands.Listed(await List(principal, ct)).Select(s => s with { Listening = waits.Listening(s.Id) })];

    // Screens seen in the last 24 hours, most recently seen (so every open one) first: [{id, name, open, seenAt, showing}],
    // with scopes on each showing as `principal` is told it (read_screens), given the stars it reads now.
    public async Task<JsonArray> List(Guid principal, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("SELECT read_screens(@p,@s)::text");
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.Add(DerivedScopes.Parameter("s", await scopes.Stars(principal, ct)));
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!)!.AsArray();
    }

    // With scopes on, the screen `screen` as a command from `principal` goes to it (ScreenCommands.ScreenScope): the login
    // its last report came from, whether that login reads all `principal` reads, whether `principal` is a viewer session,
    // and whether `principal` reads all that login reads. Null with scopes off. A screen the engine doesn't know has no
    // login, so everything a command for it names is refused as for a screen that reads another scope.
    public async Task<ScreenCommands.ScreenScope?> Scope(Guid principal, string screen, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            SELECT s.shown_by,coalesce(reads_all_of(s.shown_by,@p),false),
              coalesce((SELECT role='app' AND person_id IS NOT NULL FROM principals WHERE id=@p),false),
              coalesce(reads_all_of(@p,s.shown_by),false)
            FROM (SELECT 1) one LEFT JOIN screens s ON s.id=@s WHERE scopes_on()
            """);
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("s", screen);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
    }

    public Task<JsonObject> Rename(Guid principal, string screen, string name, CancellationToken ct) =>
        Call("SELECT rename_screen(@p,@s,@n)::text", ct, ("p", principal), ("s", screen), ("n", name));

    // The scheduler's daily pruning: commands sent more than 30 days ago, and screens unseen for 180 days (with theirs).
    public static async Task Prune(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand("""
            DELETE FROM screen_commands WHERE sent_at<now()-interval '30 days';
            DELETE FROM screens WHERE seen_at<now()-interval '180 days'
            ; DELETE FROM studio_sessions WHERE expires_at<now()-interval '30 days'
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // One function call, its connection back in the pool as soon as it answers.
    Task<JsonObject> Call(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) => Call(source.CreateCommand(sql), ct, parameters);
    static async Task<JsonObject> Call(NpgsqlCommand command, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = command;
        foreach (var (name, value) in parameters)
            if (value is null) cmd.Parameters.AddWithValue(name, NpgsqlDbType.Text, DBNull.Value);
            else cmd.Parameters.AddWithValue(name, value);
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!)!.AsObject();
    }
}
