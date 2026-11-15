using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Adapters;
using Knowledge.Mcp;
using Knowledge.Relationships;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using Npgsql;

namespace Knowledge.Tests;

// Live screens without a database: the actions' arguments, the settings a screen keeps, distinct names, control_screen's
// choice of screen, and the wait.
public sealed class ScreenRuleTests
{
    static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());

    [Fact] public void Each_action_takes_exactly_its_arguments_and_names_the_wrong_one()
    {
        Assert.Null(ScreenCommands.Problem("search", Args("""{"query":"  canal walks "}"""), out var search));
        Assert.Equal("canal walks", (string?)search["query"]);
        Assert.Null(ScreenCommands.Problem("corner_card", Args("""{"corner":"recovery"}"""), out var cleared));
        Assert.Equal("""{"corner":"recovery","dashboard":null}""", cleared.ToJsonString());
        Assert.Null(ScreenCommands.Problem("open_dashboard", Args("""{"dashboard":"6D3C2A10-0000-4000-8000-00000000D001"}"""), out var opened));
        Assert.Equal("6d3c2a10-0000-4000-8000-00000000d001", (string?)opened["dashboard"]);
        Assert.Null(ScreenCommands.Problem("move_tile", Args("""{"tile":"main","to":"changed"}"""), out _));
        Assert.Null(ScreenCommands.Problem("home", Args("{}"), out _));
        Assert.Equal("star is not an argument of home; it takes none", ScreenCommands.Problem("home", Args("""{"star":"rhythm:x"}"""), out _));
        Assert.Equal("open_star needs star: a star's graphId from the map", ScreenCommands.Problem("open_star", Args("{}"), out _));
        Assert.Equal("layout must be one of corners, hero, band", ScreenCommands.Problem("layout", Args("""{"layout":"grid"}"""), out _));
        Assert.Equal("to must be one of main, recovery, training, changed", ScreenCommands.Problem("move_tile", Args("""{"tile":"main","to":"glucose"}"""), out _));
        Assert.Equal("query must be 1-100 characters once trimmed, with no control characters",
            ScreenCommands.Problem("search", Args("""{"query":"walks\u0007"}"""), out _));
        Assert.NotNull(ScreenCommands.Problem("search", Args("""{"query":"   "}"""), out _));
        Assert.NotNull(ScreenCommands.Problem("search", Args($$"""{"query":"{{new string('a', 101)}}"}"""), out _));
        Assert.NotNull(ScreenCommands.Problem("open_dashboard", Args("""{"dashboard":null}"""), out _));
        // Characters are counted as the table and the schemas count them: an emoji is one.
        Assert.Null(ScreenCommands.Problem("search", Args($$"""{"query":"{{string.Concat(Enumerable.Repeat("🌊", 100))}}"}"""), out _));
        Assert.NotNull(ScreenCommands.Problem("search", Args($$"""{"query":"{{string.Concat(Enumerable.Repeat("🌊", 101))}}"}"""), out _));
        Assert.True(ScreenCommands.Name(string.Concat(Enumerable.Repeat("🌊", 60))));
        Assert.False(ScreenCommands.Name(string.Concat(Enumerable.Repeat("🌊", 61))));
        Assert.False(ScreenCommands.Name("   "));
        Assert.Equal("That argument is not an argument of undo; it takes none", ScreenCommands.Problem("undo", Args("""{"a private note":1}"""), out _));
        Assert.StartsWith("action is one of open_star", ScreenCommands.Problem("dance", Args("{}"), out _));
        // A dashboard's star, as the app makes it, names its dashboard; nothing else does.
        var dashboard = Guid.NewGuid();
        Assert.Equal(dashboard, ScreenCommands.DashboardOf($"{CatalogStars.Provider}:dashboard:{dashboard}"));
        Assert.Null(ScreenCommands.DashboardOf($"{CatalogStars.Provider}:dashboard:walks"));
        Assert.Null(ScreenCommands.DashboardOf($"other:dashboard:{dashboard}"));
    }

    [Fact] public void Full_screen_and_set_home_take_a_dashboard_and_set_home_takes_none_to_restore_the_default()
    {
        // full_screen without a dashboard shows the one open on that screen; with one, that dashboard. Null is not one.
        Assert.Null(ScreenCommands.Problem("full_screen", Args("{}"), out var open));
        Assert.Equal("{}", open.ToJsonString());
        Assert.Null(ScreenCommands.Problem("full_screen", Args("""{"dashboard":"6D3C2A10-0000-4000-8000-00000000D001"}"""), out var shown));
        Assert.Equal("""{"dashboard":"6d3c2a10-0000-4000-8000-00000000d001"}""", shown.ToJsonString());
        Assert.Equal("dashboard must be a dashboard's id (a UUID)", ScreenCommands.Problem("full_screen", Args("""{"dashboard":null}"""), out _));
        Assert.Equal("star is not an argument of full_screen; it takes dashboard", ScreenCommands.Problem("full_screen", Args("""{"star":"rhythm:x"}"""), out _));
        // set_home without a dashboard, or with null, restores the default Home: the screen gets null, as corner_card's does.
        Assert.Null(ScreenCommands.Problem("set_home", Args("{}"), out var restored));
        Assert.Equal("""{"dashboard":null}""", restored.ToJsonString());
        Assert.Null(ScreenCommands.Problem("set_home", Args("""{"dashboard":null}"""), out var cleared));
        Assert.Equal("""{"dashboard":null}""", cleared.ToJsonString());
        Assert.Null(ScreenCommands.Problem("set_home", Args("""{"dashboard":"6d3c2a10-0000-4000-8000-00000000d001"}"""), out _));
        Assert.Equal("dashboard must be a dashboard's id (a UUID)", ScreenCommands.Problem("set_home", Args("""{"dashboard":"walks"}"""), out _));
        Assert.Equal("layout is not an argument of set_home; it takes dashboard", ScreenCommands.Problem("set_home", Args("""{"layout":"band"}"""), out _));
        Assert.Equal(["full_screen", "set_home"], ScreenCommands.Actions[8..10]);
    }

    [Fact] public void Move_resize_and_focus_map_take_their_arguments_one_way_each()
    {
        const string Dashboard = "6d3c2a10-0000-4000-8000-00000000d001";
        Assert.Null(ScreenCommands.Problem("move", Args("""{"part":"map","place":"right","dashboard":"6D3C2A10-0000-4000-8000-00000000D001"}"""), out var moved));
        Assert.Equal($$"""{"part":"map","place":"right","dashboard":"{{Dashboard}}"}""", moved.ToJsonString());
        // Left out, the guard stays out for the engine to fill in; null is the screen's default Home.
        Assert.Null(ScreenCommands.Problem("move", Args("""{"part":"map","with":"page"}"""), out var traded));
        Assert.False(traded.ContainsKey("dashboard"));
        Assert.Null(ScreenCommands.Problem("move", Args("""{"part":"main","x":0,"y":8,"dashboard":null}"""), out var home));
        Assert.Equal("""{"part":"main","x":0,"y":8,"dashboard":null}""", home.ToJsonString());
        Assert.Equal("move needs one of place, with, or x and y", ScreenCommands.Problem("move", Args("""{"part":"map"}"""), out _));
        Assert.Equal("move takes one of place, with, or x and y, not more", ScreenCommands.Problem("move", Args("""{"part":"map","place":"left","with":"page"}"""), out _));
        Assert.Equal("move takes one of place, with, or x and y, not more", ScreenCommands.Problem("move", Args("""{"part":"map","place":"left","x":0,"y":0}"""), out _));
        Assert.Equal("y is given with x", ScreenCommands.Problem("move", Args("""{"part":"map","y":3}"""), out _));
        Assert.Equal("with names another part than part", ScreenCommands.Problem("move", Args("""{"part":"map","with":"map"}"""), out _));
        Assert.Equal("place must be one of left, right, up, down, top_left, top_right, bottom_left, bottom_right",
            ScreenCommands.Problem("move", Args("""{"part":"map","place":"middle"}"""), out _));
        Assert.Equal("x must be a whole number 0-11", ScreenCommands.Problem("move", Args("""{"part":"map","x":12,"y":0}"""), out _));
        Assert.Equal("x must be a whole number 0-11", ScreenCommands.Problem("move", Args("""{"part":"map","x":1.5,"y":0}"""), out _));
        Assert.StartsWith("part must be a part's id from list_screens", ScreenCommands.Problem("move", Args("""{"part":"The map","place":"left"}"""), out _));
        Assert.Equal("move needs part: a part's id from list_screens: a lowercase letter, then up to 31 lowercase letters, digits or underscores",
            ScreenCommands.Problem("move", Args("""{"place":"left"}"""), out _));
        Assert.Equal("dashboard must be a dashboard's id (a UUID)", ScreenCommands.Problem("move", Args("""{"part":"map","place":"left","dashboard":"home"}"""), out _));
        // resize: a step by size, or a size in cells.
        Assert.Null(ScreenCommands.Problem("resize", Args("""{"part":"map","size":"bigger"}"""), out _));
        Assert.Null(ScreenCommands.Problem("resize", Args("""{"part":"map","w":8,"h":12,"dashboard":null}"""), out _));
        Assert.Equal("resize needs size, or w and h", ScreenCommands.Problem("resize", Args("""{"part":"map"}"""), out _));
        Assert.Equal("resize takes size, or w and h, not both", ScreenCommands.Problem("resize", Args("""{"part":"map","size":"bigger","w":8,"h":8}"""), out _));
        Assert.Equal("w must be a whole number 1-12", ScreenCommands.Problem("resize", Args("""{"part":"map","w":0,"h":4}"""), out _));
        Assert.Equal("w is given with h", ScreenCommands.Problem("resize", Args("""{"part":"map","w":4}"""), out _));
        Assert.Equal("size must be one of bigger, smaller, wider, narrower, taller, shorter", ScreenCommands.Problem("resize", Args("""{"part":"map","size":"huge"}"""), out _));
        Assert.StartsWith("place is not an argument of resize", ScreenCommands.Problem("resize", Args("""{"part":"map","place":"left"}"""), out _));
        // focus_map: without a focus, or with null, everything again, stored as focus null; a focus as a layout stores it.
        Assert.Null(ScreenCommands.Problem("focus_map", Args("{}"), out var cleared));
        Assert.Equal("""{"focus":null}""", cleared.ToJsonString());
        Assert.Null(ScreenCommands.Problem("focus_map", Args("""{"focus":null,"part":"map"}"""), out var named));
        Assert.Equal("""{"focus":null,"part":"map"}""", named.ToJsonString());
        Assert.Null(ScreenCommands.Problem("focus_map", Args("""{"focus":{"providers":["garmin"," garmin"]},"dashboard":null}"""), out var garmin));
        Assert.Equal("""{"focus":{"providers":["garmin"]},"dashboard":null}""", garmin.ToJsonString());
        Assert.Null(ScreenCommands.Problem("focus_map", Args("""{"focus":{"near":{"star":" notes:notes:Plan.md "}}}"""), out var near));
        Assert.Equal("""{"focus":{"near":{"star":"notes:notes:Plan.md","depth":1}}}""", near.ToJsonString());
        Assert.StartsWith("focus has at least one of", ScreenCommands.Problem("focus_map", Args("""{"focus":{}}"""), out _));
        Assert.StartsWith("focus must be a map focus", ScreenCommands.Problem("focus_map", Args("""{"focus":"garmin"}"""), out _));
        Assert.StartsWith("focus must be a map focus", ScreenCommands.Problem("focus_map", Args("""{"focus":{"types":"note"}}"""), out _));
        Assert.Equal("focus is not an argument of move; it takes part and place and with and x and y and dashboard",
            ScreenCommands.Problem("move", Args("""{"part":"map","place":"left","focus":null}"""), out _));
        // A dashboard of null is a Home only for the actions that arrange parts.
        Assert.NotNull(ScreenCommands.Problem("open_dashboard", Args("""{"dashboard":null}"""), out _));
        Assert.Equal(["move", "resize", "focus_map"], ScreenCommands.Actions[^3..]);
    }

    [Fact] public void A_screens_settings_keep_a_layout_its_tiles_and_a_home_and_nothing_else()
    {
        static bool Valid(string json) => ScreenCommands.Settings(JsonDocument.Parse(json).RootElement);
        const string Tiles = """{"main":"recovery","recovery":"main","training":"training","changed":"changed"}""";
        // Each member is optional: a new screen keeps nothing yet, and one older than piece 2 keeps no home.
        Assert.True(Valid("{}"));
        Assert.True(Valid($$"""{"layout":"hero","tiles":{{Tiles}} }"""));
        Assert.True(Valid($$"""{"layout":"band","tiles":{{Tiles}},"home":null}"""));
        Assert.True(Valid("""{"home":{"dashboard":"6D3C2A10-0000-4000-8000-00000000D001"}}"""));
        // A Home is a dashboard's id, never what the screen reports about it; a layout, tiles or member it does not keep
        // is refused.
        Assert.False(Valid("""{"home":{"dashboard":{"id":"6d3c2a10-0000-4000-8000-00000000d001","title":"DEMO · Training plan"}}}"""));
        Assert.False(Valid("""{"home":{"dashboard":"6d3c2a10-0000-4000-8000-00000000d001","title":"DEMO · Training plan"}}"""));
        Assert.False(Valid("""{"home":{}}"""));
        Assert.False(Valid("""{"home":"6d3c2a10-0000-4000-8000-00000000d001"}"""));
        Assert.False(Valid("""{"home":{"dashboard":"walks"}}"""));
        Assert.False(Valid("""{"layout":"grid"}"""));
        Assert.False(Valid("""{"tiles":{"main":"main","recovery":"main","training":"training","changed":"changed"}}"""));
        Assert.False(Valid("""{"tiles":{"main":"main","recovery":"recovery","training":"training"}}"""));
        Assert.False(Valid("""{"tiles":{"main":"main","recovery":"recovery","training":"training","changed":"changed","glucose":"main"}}"""));
        Assert.False(Valid("""{"theme":"dark"}"""));
        Assert.False(Valid("""{"layout":"hero","layout":"band"}"""));
        // Piece 3: a Home grid (DashboardLayout.HomeGrid), never null; a screen without one leaves it out.
        const string Grid = """
            {"parts":[{"id":"map","x":2,"y":0,"w":8,"h":12},{"id":"main","x":0,"y":0,"w":2,"h":8},{"id":"recovery","x":10,"y":0,"w":2,"h":8},
              {"id":"training","x":0,"y":8,"w":2,"h":4},{"id":"changed","x":10,"y":8,"w":2,"h":4}]}
            """;
        Assert.True(Valid($$"""{"layout":"corners","tiles":{{Tiles}},"home":null,"grid":{{Grid}} }"""));
        Assert.False(Valid("""{"layout":"corners","grid":null}"""));
        Assert.False(Valid("""{"grid":{"parts":[]}}"""));
        Assert.False(Valid("[]"));
        // The app endpoint checks them so on every listen.
        TestSettings.Use();
        static JsonElement Listen(string settings) => JsonDocument.Parse("""
            {"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"app_screens","arguments":{"op":"listen","screen":"kitchen-0123456789ab","name":"Kitchen iPad","settings":
            """ + settings + "}}}").RootElement;
        Assert.True(AppEndpoint.Allowed(Listen($$"""{"layout":"hero","tiles":{{Tiles}},"home":{"dashboard":"6d3c2a10-0000-4000-8000-00000000d001"} }""")));
        Assert.True(AppEndpoint.Allowed(Listen("{}")));
        Assert.True(AppEndpoint.Allowed(Listen($$"""{"layout":"corners","grid":{{Grid}} }""")));
        Assert.False(AppEndpoint.Allowed(Listen("""{"grid":null}""")));
        Assert.False(AppEndpoint.Allowed(Listen("""{"home":{"dashboard":"walks"}}""")));
    }

    [Fact] public void A_new_screen_whose_name_is_taken_gets_the_lowest_free_number()
    {
        Assert.Equal("Windows PC · Chrome", ScreenCommands.Distinct("Windows PC · Chrome", ["iPhone · Safari", "Windows PC · Firefox"]));
        // Names compare once trimmed and in any case, as control_screen matches them; the screen keeps its own spelling.
        Assert.Equal("Windows PC · Chrome 2", ScreenCommands.Distinct(" Windows PC · Chrome ", ["windows pc · chrome"]));
        Assert.Equal("Windows PC · Chrome 3", ScreenCommands.Distinct("Windows PC · Chrome", ["Windows PC · Chrome", "Windows PC · Chrome 2 "]));
        Assert.Equal("Windows PC · Chrome 2", ScreenCommands.Distinct("Windows PC · Chrome", ["Windows PC · Chrome", "Windows PC · Chrome 3"]));
        // Cut to fit 60 characters, counted as the table counts them (an emoji is one), never ending in a space.
        var waves = string.Concat(Enumerable.Repeat("🌊", 60));
        Assert.Equal(string.Concat(Enumerable.Repeat("🌊", 58)) + " 2", ScreenCommands.Distinct(waves, [waves]));
        var spaced = new string('a', 57) + " bc";
        Assert.Equal(new string('a', 57) + " 2", ScreenCommands.Distinct(spaced, [spaced]));
        // That name and every number to 99 taken: the next one.
        var full = new string('b', 60);
        string[] taken = [full, .. Enumerable.Range(2, 98).Select(n => new string('b', 59 - n.ToString().Length) + " " + n)];
        Assert.Equal(new string('b', 56) + " 100", ScreenCommands.Distinct(full, taken));
        // A name the database refuses stays as it came.
        Assert.Equal("   ", ScreenCommands.Distinct("   ", ["Kitchen iPad"]));
    }

    [Fact] public void A_numbered_name_is_its_bare_name_with_the_number_the_engine_gives()
    {
        Assert.True(ScreenCommands.Numbered("Windows PC · Chrome 2", "Windows PC · Chrome"));
        Assert.True(ScreenCommands.Numbered("Windows PC · Chrome 12", "Windows PC · Chrome"));
        // Compared as Distinct compares: once trimmed, in any case.
        Assert.True(ScreenCommands.Numbered("WINDOWS PC · CHROME 3 ", " windows pc · chrome"));
        // Cut to fit 60 characters as Distinct cuts it.
        var waves = string.Concat(Enumerable.Repeat("🌊", 60));
        Assert.True(ScreenCommands.Numbered(ScreenCommands.Distinct(waves, [waves]), waves));
        var spaced = new string('a', 57) + " bc";
        Assert.True(ScreenCommands.Numbered(ScreenCommands.Distinct(spaced, [spaced]), spaced));
        // Another name, a number the engine never gives, or none: not that name numbered.
        Assert.False(ScreenCommands.Numbered("Windows PC · Chrome 2", "Windows PC · Edge"));
        Assert.False(ScreenCommands.Numbered("Windows PC · Chrome", "Windows PC · Chrome"));
        Assert.False(ScreenCommands.Numbered("Windows PC · Chrome 1", "Windows PC · Chrome"));
        Assert.False(ScreenCommands.Numbered("Windows PC · Chrome 02", "Windows PC · Chrome"));
        Assert.False(ScreenCommands.Numbered("Windows PC · Chrome2", "Windows PC · Chrome"));
        Assert.False(ScreenCommands.Numbered("Kitchen 2", "Kitchen 2"));
        Assert.False(ScreenCommands.Numbered("Kitchen 2", "   "));
    }

    [Fact] public void A_screen_lists_its_home_and_when_a_person_last_used_it()
    {
        var seen = DateTimeOffset.Parse("2026-10-28T12:01:02Z");
        static JsonArray Screen(JsonNode? showing) => [new JsonObject { ["id"] = "kitchen-0123456789ab", ["name"] = "Kitchen iPad", ["open"] = true,
            ["seenAt"] = "2026-10-28T12:01:02+00:00", ["showing"] = showing }];
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T11:58:40.25Z"),
            ScreenCommands.Listed(Screen(new JsonObject { ["activeAt"] = "2026-10-28T11:58:40.25+00:00" }))[0].ActiveAt);
        // A browser whose clock runs ahead counts as used when the engine last heard from it; none, or not an instant, is none.
        Assert.Equal(seen, ScreenCommands.Listed(Screen(new JsonObject { ["activeAt"] = "2026-10-28T12:30:00Z" }))[0].ActiveAt);
        Assert.Null(ScreenCommands.Listed(Screen(new JsonObject { ["view"] = "home" }))[0].ActiveAt);
        Assert.Null(ScreenCommands.Listed(Screen(new JsonObject { ["activeAt"] = 1793001600 }))[0].ActiveAt);
        Assert.Null(ScreenCommands.Listed(Screen(new JsonObject { ["activeAt"] = "yesterday" }))[0].ActiveAt);
        Assert.Null(ScreenCommands.Listed(Screen(new JsonObject { ["activeAt"] = new JsonObject { ["at"] = "2026-10-28T11:58:40Z" } }))[0].ActiveAt);

        // Its Home is {dashboard: {id, title}} as the screen reports it, and nothing more but its missing mark; anything else
        // is the default Home.
        var home = ScreenCommands.Home(JsonNode.Parse("""
            {"view":"full_screen","home":{"dashboard":{"id":"6d3c2a10-0000-4000-8000-00000000d001","title":"DEMO · Training plan","pages":3},"since":"today"}}
            """));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"dashboard":{"id":"6d3c2a10-0000-4000-8000-00000000d001","title":"DEMO · Training plan"}}"""), home));
        // A Home dashboard deleted since stays its Home, marked missing while the screen shows the default Home instead.
        var gone = ScreenCommands.Home(JsonNode.Parse("""
            {"view":"home","home":{"dashboard":{"id":"6d3c2a10-0000-4000-8000-00000000d001","title":""},"missing":true}}
            """));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"dashboard":{"id":"6d3c2a10-0000-4000-8000-00000000d001","title":""},"missing":true}"""), gone));
        Assert.True(JsonNode.DeepEquals(home, ScreenCommands.Home(JsonNode.Parse("""
            {"home":{"dashboard":{"id":"6d3c2a10-0000-4000-8000-00000000d001","title":"DEMO · Training plan"},"missing":"yes"}}
            """))));
        Assert.Null(ScreenCommands.Home(JsonNode.Parse("""{"view":"home","home":null}""")));
        Assert.Null(ScreenCommands.Home(JsonNode.Parse("""{"view":"home"}""")));
        Assert.Null(ScreenCommands.Home(JsonNode.Parse("""{"home":{"dashboard":{"id":"walks","title":"Walks"}}}""")));
        Assert.Null(ScreenCommands.Home(JsonNode.Parse("""{"home":{"dashboard":"6d3c2a10-0000-4000-8000-00000000d001"}}""")));
        Assert.Null(ScreenCommands.Home(JsonNode.Parse("""{"home":"6d3c2a10-0000-4000-8000-00000000d001"}""")));
        Assert.Null(ScreenCommands.Home(JsonNode.Parse("""{"home":["6d3c2a10-0000-4000-8000-00000000d001"]}""")));
        Assert.Null(ScreenCommands.Home(null));
    }

    [Fact] public void Without_a_screen_control_screen_picks_the_open_one_a_person_used_last_then_the_one_seen_last()
    {
        var now = DateTimeOffset.UtcNow;
        ScreenCommands.Seen kitchen = new("kitchen-0123456789ab", "Kitchen iPad", true, now.AddSeconds(-20), now.AddMinutes(-1)),
            hallway = new("hallway-0123456789ab", "Hallway PC", true, now.AddSeconds(-2), now.AddMinutes(-30)),
            garage = new("garage-0123456789abc", "Garage tablet", true, now.AddSeconds(-1)),
            phone = new("phone-0123456789abcd", "Sam's phone", false, now.AddHours(-3), now.AddHours(-3));
        // Used a minute ago beats used half an hour ago, whichever listened last.
        Assert.Equal(kitchen, ScreenCommands.Target([garage, hallway, kitchen, phone], null).Target);
        // A screen that reports no activeAt (one older than piece 2) comes after those that do; among those, the one seen last.
        Assert.Equal(hallway, ScreenCommands.Target([garage, hallway, phone], null).Target);
        Assert.Equal(garage, ScreenCommands.Target([garage, hallway with { ActiveAt = null }, phone], null).Target);
        // A closed screen is never the target, however recently it was used.
        Assert.StartsWith("No screen is open now", ScreenCommands.Target([phone with { ActiveAt = now }], null).Problem);
        // A screen that no longer listens (its app was just hidden, so it counts as open for a minute more) loses to one
        // that does, however recently it was used; with none listening, the order above holds.
        ScreenCommands.Seen listening = garage with { Listening = true }, idle = hallway with { Listening = true };
        Assert.Equal(listening, ScreenCommands.Target([kitchen with { ActiveAt = now }, listening, phone], null).Target);
        Assert.Equal(idle, ScreenCommands.Target([kitchen, idle, listening, phone], null).Target);
        // A name is still a name, listening or not.
        Assert.Equal(kitchen, ScreenCommands.Target([kitchen, listening], "Kitchen iPad").Target);
    }

    [Fact] public void Control_screen_picks_the_open_screen_seen_last_or_the_one_with_that_id_or_name()
    {
        var now = DateTimeOffset.UtcNow;
        ScreenCommands.Seen kitchen = new("kitchen-0123456789ab", "Kitchen iPad", true, now.AddSeconds(-5)),
            hallway = new("hallway-0123456789ab", "Hallway PC", true, now.AddSeconds(-2)),
            phone = new("phone-0123456789abcd", "Sam's phone", false, now.AddHours(-3));
        IReadOnlyList<ScreenCommands.Seen> seen = [hallway, kitchen, phone];
        Assert.Equal(hallway, ScreenCommands.Target(seen, null).Target);
        Assert.Equal(kitchen, ScreenCommands.Target(seen, "kitchen-0123456789ab").Target);
        // An id in another case still names its screen, unless two ids match that way.
        Assert.Equal(kitchen, ScreenCommands.Target(seen, "KITCHEN-0123456789AB").Target);
        ScreenCommands.Seen twin = new("KITCHEN-0123456789AB", "Garage", true, now.AddSeconds(-9));
        Assert.Equal(twin, ScreenCommands.Target([kitchen, twin], "KITCHEN-0123456789AB").Target);
        Assert.StartsWith("No screen has that id or name", ScreenCommands.Target([kitchen, twin], "Kitchen-0123456789AB").Problem);
        Assert.Equal(kitchen, ScreenCommands.Target(seen, " KITCHEN ipad ").Target);
        Assert.Contains("not open now", ScreenCommands.Target(seen, "phone-0123456789abcd").Problem);
        Assert.Contains("not open now", ScreenCommands.Target(seen, "sam's PHONE").Problem);
        Assert.StartsWith("No screen has that id or name", ScreenCommands.Target(seen, "Garage").Problem);
        Assert.StartsWith("No screen is open now", ScreenCommands.Target([phone], null).Problem);
        // Two open screens of one name: refused, listing both with their ids.
        ScreenCommands.Seen other = new("kitchen-fedcba987654", "kitchen ipad", true, now.AddSeconds(-1));
        var (target, problem) = ScreenCommands.Target([other, hallway, kitchen, phone], "Kitchen iPad");
        Assert.Null(target);
        Assert.Contains("2 open screens have that name", problem);
        Assert.Contains("(id kitchen-0123456789ab)", problem);
        Assert.Contains("(id kitchen-fedcba987654)", problem);
    }

    [Fact] public async Task A_wake_before_the_wait_is_kept_and_every_wait_ends_cleanly()
    {
        var waits = new ScreenWaits();
        // Woken between the database's answer and the wait: the wait returns at once.
        using (var early = waits.ForScreen("kitchen-0123456789ab"))
        {
            waits.WakeScreen("kitchen-0123456789ab");
            Assert.True(await early.Until(TimeSpan.FromSeconds(5), default));
        }
        // Two tabs of one browser wait on one screen; one send wakes both. A wake for another screen wakes neither.
        using (var first = waits.ForScreen("kitchen-0123456789ab"))
        using (var second = waits.ForScreen("kitchen-0123456789ab"))
        {
            var both = Task.WhenAll(first.Until(TimeSpan.FromSeconds(5), default), second.Until(TimeSpan.FromSeconds(5), default));
            waits.WakeScreen("hallway-0123456789ab");
            await Task.Delay(50);
            Assert.False(both.IsCompleted);
            waits.WakeScreen("kitchen-0123456789ab");
            Assert.Equal(new[] { true, true }, await both);
        }
        var command = Guid.NewGuid();
        using (var quiet = waits.ForCommand(command)) Assert.False(await quiet.Until(TimeSpan.FromMilliseconds(50), default));
        // A request that goes away ends its wait at once.
        using (var abandoned = waits.ForScreen("kitchen-0123456789ab"))
        using (var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.Until(TimeSpan.FromSeconds(25), gone.Token));
        Assert.Equal(0, waits.Waiting);
    }

    [Fact] public void A_screen_listens_while_a_listen_is_under_way_and_a_moment_after_one_answered_never_after_one_it_left()
    {
        var clock = new Clock();
        var waits = new ScreenWaits(CancellationToken.None, clock);
        const string Kitchen = "kitchen-0123456789ab", Hallway = "hallway-0123456789ab";
        Assert.False(waits.Listening(Kitchen));
        using (waits.Hearing(Kitchen, default)) Assert.True(waits.Listening(Kitchen));
        // Answered, it listens again in a moment (at once, or once it has shown what it took), so it still counts.
        clock.Now += ScreenWaits.Between - TimeSpan.FromSeconds(1);
        Assert.True(waits.Listening(Kitchen));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.False(waits.Listening(Kitchen));
        // A listen whose caller went away (the app was hidden or closed) ends its screen's listening at once, even right
        // after one that was answered, unless another of its listens is still under way.
        using (var hidden = new CancellationTokenSource())
        {
            using (waits.Hearing(Hallway, default))
            {
                using (waits.Hearing(Hallway, hidden.Token)) hidden.Cancel();
                Assert.True(waits.Listening(Hallway));
            }
            Assert.True(waits.Listening(Hallway));
            using (waits.Hearing(Hallway, hidden.Token)) { }
        }
        Assert.False(waits.Listening(Hallway));
    }
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T09:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact] public async Task The_engine_stopping_ends_every_wait_as_if_its_time_ran_out()
    {
        using var stopping = new CancellationTokenSource();
        var waits = new ScreenWaits(stopping.Token);
        var clock = Stopwatch.StartNew();
        using (var held = waits.ForScreen("kitchen-0123456789ab"))
        {
            var waiting = held.Until(TimeSpan.FromSeconds(25), default);
            await Task.Delay(50);
            Assert.False(waiting.IsCompleted);
            stopping.Cancel();
            Assert.False(await waiting);
        }
        // One begun while the engine stops ends at once too.
        using (var late = waits.ForCommand(Guid.NewGuid())) Assert.False(await late.Until(TimeSpan.FromSeconds(25), default));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the waits took {clock.Elapsed}");
        Assert.Equal(0, waits.Waiting);
        // The engine's own services make it with the host's lifetime.
        var services = new ServiceCollection().AddSingleton<IHostApplicationLifetime, Lifetime>().AddSingleton<ScreenWaits>().BuildServiceProvider();
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        using (var held = services.GetRequiredService<ScreenWaits>().ForScreen("kitchen-0123456789ab"))
        {
            var waiting = held.Until(TimeSpan.FromSeconds(25), default);
            lifetime.StopApplication();
            Assert.False(await waiting);
        }
    }
    sealed class Lifetime : IHostApplicationLifetime
    {
        readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
    }

    [Fact] public void The_screen_tools_list_their_choices()
    {
        var tools = HarnessTools.Create(TestSettings.Use(), []);
        var control = tools.Single(t => t.ProtocolTool.Name == "control_screen").ProtocolTool;
        var action = control.InputSchema.GetProperty("properties").GetProperty("action");
        Assert.Equal(ScreenCommands.Actions, action.GetProperty("enum").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("action", control.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("action must be one of " + string.Join(", ", ScreenCommands.Actions) + ".",
            ToolArguments.Problem(control, new Dictionary<string, JsonElement> { ["action"] = JsonSerializer.SerializeToElement("dance") }));
        Assert.True(tools.Single(t => t.ProtocolTool.Name == "list_screens").ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.Contains("Test install", tools.Single(t => t.ProtocolTool.Name == "list_screens").ProtocolTool.Description);
        // With several open, the default target (the screen used last, Q-147) can still be the wrong one, so both say to
        // name the screen; control_screen lists piece 2's actions.
        Assert.Contains("when more than one screen is open, read list_screens first and give screen", control.Description);
        Assert.Contains("when more than one is open, name it there", tools.Single(t => t.ProtocolTool.Name == "list_screens").ProtocolTool.Description);
        Assert.Contains("full_screen (dashboard", control.Description);
        Assert.Contains("set_home (dashboard", control.Description);
        Assert.Contains("the open screen a person used most recently", control.Description);
        // Piece 3: the three arranging actions and their arguments, and the parts each screen shows.
        Assert.Contains("move (part", control.Description);
        Assert.Contains("resize (part", control.Description);
        Assert.Contains("focus_map (focus", control.Description);
        Assert.Contains("give dashboard (its id, or null at Home)", control.Description);
        Assert.Contains("parts (what it shows as a grid of parts", tools.Single(t => t.ProtocolTool.Name == "list_screens").ProtocolTool.Description);
        var properties = control.InputSchema.GetProperty("properties");
        foreach (var name in new[] { "part", "place", "with", "x", "y", "size", "w", "h", "focus" }) Assert.True(properties.TryGetProperty(name, out _), name);
        Assert.Contains("null", properties.GetProperty("dashboard").GetProperty("type").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("x must be a whole number or null.", ToolArguments.Problem(control, new Dictionary<string, JsonElement>
            { ["action"] = JsonSerializer.SerializeToElement("move"), ["x"] = JsonSerializer.SerializeToElement("left") }));
    }
}

// Live screens against a real database: the role gates, a held listen woken by a send, the outcome reaching the sender,
// expiry, the limits, the star rule, full screen and Home, distinct names, control_screen's choice of screen and the
// daily pruning.
[Collection(SharedStateCollection.Name)]
public sealed class ScreenDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    readonly ScreenWaits waits = new();
    ScreenStore Store() => new(fixture.Source, waits);
    static string NewScreen(string label) => label + "-" + Guid.NewGuid().ToString("N")[..16];
    // A name no other screen in these tests has, for a test that reads the name back: a new screen whose name another
    // screen seen in the last 30 days has gets a number after it.
    static string Named(string label) => label + " " + Guid.NewGuid().ToString("N")[..6];
    async Task<Guid> App() => (await new Principals(fixture.Source).SyncApp(Principals.NewToken(), default))!.Value;
    async Task<Guid> Harness()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-test-screens-" + Guid.NewGuid().ToString("N")[..8]);
        try { return await new Principals(fixture.Source).Create("screens-" + Guid.NewGuid().ToString("N")[..8], "harness", null, Path.Combine(folder, "harness"), default); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // app_screens through the app's endpoint, as the app server calls it: the status and the result.
    async Task<(int Status, JsonNode Content)> Call(Guid app, object arguments, RelationshipHost? host = null, CancellationToken aborted = default)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(Store()).BuildServiceProvider(), RequestAborted = aborted };
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "s", method = "tools/call",
            @params = new { name = "app_screens", arguments } }));
        context.Response.Body = new MemoryStream();
        await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), host!, new AppHealth(fixture.Db, TestSettings.Use()), AdapterRegistry.Empty);
        return (context.Response.StatusCode, JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!);
    }
    static void Valid(JsonNode content) => Schemas.Valid(Schemas.Folder("mcp"), "app_screens.output.schema.json", content);

    // A screen as the app runs one: it listens, applies each command (here, nothing) and reports it shown, until stopped.
    async Task Run(Guid app, string screen, string name, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
            try
            {
                var (_, heard) = await Call(app, new { op = "listen", screen, name, hold = 25 }, aborted: stop);
                if (heard["command"] is { } command)
                    await Call(app, new { op = "done", screen, command = (string)command["id"]!, result = "shown", detail = new { summary = "Done on " + name } });
            }
            catch (OperationCanceledException) { return; }
    }

    [PostgresFact] public async Task Roles_gate_the_screen_functions()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        var kitchen = NewScreen("kitchen");
        // Only the app's login listens, reports and renames; a harness login lists and sends, as the assistant.
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => store.Listen(harness, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default))).SqlState);
        await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => store.Rename(harness, kitchen, "Hallway", default))).SqlState);
        Assert.Contains(await store.List(harness, default), s => (string?)s!["id"] == kitchen && (bool)s["open"]!);
        var sent = await store.Send(harness, kitchen, "home", [], null, TimeSpan.Zero, default);
        Assert.Equal("""{"kind":"assistant"}""", sent["sender"]!.ToJsonString());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() =>
            store.Done(harness, kitchen, Guid.Parse((string)sent["id"]!), "shown", null, null, default))).SqlState);
        // The app sends only as one of its screens; a harness login never does.
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => store.Send(app, kitchen, "home", [], null, TimeSpan.Zero, default))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => store.Send(harness, kitchen, "home", [], kitchen, TimeSpan.Zero, default))).SqlState);
        // A principal with no role reaches none of them.
        var nobody = Guid.NewGuid();
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => store.List(nobody, default))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => store.Command(nobody, Guid.Parse((string)sent["id"]!), default))).SqlState);
        // The perimeter sends only the app's login to this endpoint; were another to reach it, the database refuses it.
        var (status, refused) = await Call(harness, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0 });
        Assert.Equal((403, "forbidden"), (status, (string?)refused["error"]!["code"]));
    }

    [PostgresFact] public async Task A_held_listen_is_woken_by_a_send_and_the_outcome_reaches_the_sender()
    {
        var app = await App();
        var (kitchen, hallway) = (NewScreen("kitchen"), NewScreen("hallway"));
        var (kitchenName, hallwayName) = (Named("Kitchen iPad"), Named("Hallway PC"));
        var settings = new { layout = "hero", tiles = new { main = "main", recovery = "recovery", training = "training", changed = "changed" } };
        var (status, first) = await Call(app, new { op = "listen", screen = kitchen, name = $" {kitchenName} ", hold = 0, settings, showing = new { view = "home", layout = "hero" } });
        Assert.Equal(200, status);
        Assert.Null(first["command"]);
        Assert.Equal((kitchenName, "hero"), ((string?)first["screen"]!["name"], (string?)first["screen"]!["settings"]!["layout"]));
        Valid(first);
        await Call(app, new { op = "listen", screen = hallway, name = hallwayName, hold = 0 });

        // The kitchen waits; nothing holds a database connection meanwhile.
        var held = Call(app, new { op = "listen", screen = kitchen, name = kitchenName });
        await Task.Delay(500);
        Assert.False(held.IsCompleted);
        await using (var busy = fixture.Source.CreateCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname=current_database() AND backend_type='client backend' AND pid<>pg_backend_pid() AND state<>'idle'
            """))
            Assert.Equal(0L, await busy.ExecuteScalarAsync());
        var clock = Stopwatch.StartNew();
        var sending = Call(app, new { op = "send", screen = hallway, to = kitchen, action = "search", query = "  canal walks " });
        var (_, heard) = await held;
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the listen took {clock.Elapsed}");
        Valid(heard);
        var command = heard["command"]!;
        Assert.Equal(("search", "canal walks", "delivered"), ((string?)command["action"], (string?)command["args"]!["query"], (string?)command["result"]));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "screen", ["screen"] = hallway, ["name"] = hallwayName }, command["sender"]));
        Assert.NotNull(command["ms"]!["deliver"]);
        Assert.Null(command["ms"]!["show"]);
        Assert.False(sending.IsCompleted);

        // The screen reports; the sender, still waiting, hears it at once.
        var (doneStatus, done) = await Call(app, new { op = "done", screen = kitchen, command = (string)command["id"]!, result = "shown",
            detail = new { summary = "Searched for canal walks", matches = 2, titles = new[] { "DEMO · Canal walk", "DEMO · Walks" } },
            showing = new { view = "search", query = "canal walks", layout = "hero" } });
        Assert.Equal((200, """{"ok":true}"""), (doneStatus, done.ToJsonString()));
        var (sentStatus, sent) = await sending;
        Assert.Equal(200, sentStatus);
        Assert.True(clock.Elapsed < ScreenCommands.Outcome, $"the send took {clock.Elapsed}");
        Valid(sent);
        Assert.Equal(("shown", 2), ((string?)sent["command"]!["result"], (int?)sent["command"]!["detail"]!["matches"]));
        Assert.True((long)sent["command"]!["ms"]!["show"]! >= (long)sent["command"]!["ms"]!["deliver"]!);
        Assert.Equal(0, waits.Waiting);

        // What it shows now is listed, with which screen asked; a rename is kept by the listen that was waiting.
        var (_, listed) = await Call(app, new { op = "list", screen = hallway });
        Valid(listed);
        var row = listed["screens"]!.AsArray().Single(s => (string?)s!["id"] == kitchen)!;
        Assert.Equal(("search", false, true), ((string?)row["showing"]!["view"], (bool)row["self"]!, (bool)row["open"]!));
        Assert.True((bool)listed["screens"]!.AsArray().Single(s => (string?)s!["id"] == hallway)!["self"]!);
        var waiting = Call(app, new { op = "listen", screen = kitchen, name = kitchenName, hold = 1 });
        await Task.Delay(200);
        var (_, renamed) = await Call(app, new { op = "rename", screen = kitchen, name = "Kitchen" });
        Valid(renamed);
        Assert.Equal("Kitchen", (string?)(await waiting).Content["screen"]!["name"]);

        // A report on a command that is not waiting for one, or not this screen's, is refused.
        var (again, twice) = await Call(app, new { op = "done", screen = kitchen, command = (string)command["id"]!, result = "failed" });
        Assert.Equal((400, "invalid_arguments", "That command is not waiting for an outcome."),
            (again, (string?)twice["error"]!["code"], (string?)twice["error"]!["message"]));
        Assert.Equal(404, (await Call(app, new { op = "done", screen = hallway, command = (string)command["id"]!, result = "shown" })).Status);
        Assert.Equal(404, (await Call(app, new { op = "rename", screen = NewScreen("unknown"), name = "Garage" })).Status);
    }

    [PostgresFact] public async Task Tabs_listening_as_one_screen_take_each_command_once_and_twenty_wait_at_most()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        var kitchen = NewScreen("kitchen");
        await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default);
        // Two tabs of one browser hold a listen each; one command wakes both, and only one of them takes it.
        var tabs = new[] { store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.FromSeconds(10), null, null, default),
            store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.FromSeconds(10), null, null, default) };
        await Task.Delay(300);
        var sent = await store.Send(harness, kitchen, "home", [], null, TimeSpan.Zero, default);
        var heard = await Task.WhenAll(tabs);
        Assert.Equal(new[] { (string)sent["id"]! }, heard.Select(h => (string?)h["command"]?["id"]).OfType<string>().ToArray());
        Assert.Equal(0, waits.Waiting);
        // Twenty-one sent at once to one screen: twenty wait for it, and one is refused.
        var hallway = NewScreen("hallway");
        await store.Listen(app, hallway, "Hallway PC", TimeSpan.Zero, null, null, default);
        var sends = await Task.WhenAll(Enumerable.Range(0, 21).Select(async _ =>
        {
            try { await store.Send(harness, hallway, "home", [], null, TimeSpan.Zero, default); return true; }
            catch (PostgresException e) when (e.SqlState == "22023") { return false; }
        }));
        Assert.Equal((20, 1), (sends.Count(s => s), sends.Count(s => !s)));
    }

    [PostgresFact] public async Task A_listen_ends_with_its_request_and_a_send_to_a_closed_screen_is_refused()
    {
        var app = await App();
        var (kitchen, hallway) = (NewScreen("kitchen"), NewScreen("hallway"));
        await Call(app, new { op = "listen", screen = hallway, name = "Hallway PC", hold = 0 });
        using var gone = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var held = Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 25 }, aborted: gone.Token);
        await Task.Delay(300);
        gone.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the listen took {clock.Elapsed}");
        Assert.Equal(0, waits.Waiting);

        // A screen not heard from in the last minute is not open: nothing is sent to it.
        await using (var cmd = fixture.Source.CreateCommand($"UPDATE screens SET seen_at=now()-interval '61 seconds' WHERE id='{kitchen}'"))
            await cmd.ExecuteNonQueryAsync();
        var (status, refused) = await Call(app, new { op = "send", screen = hallway, to = kitchen, action = "home" });
        Assert.Equal((404, "not_found", "No open screen has that id."), (status, (string?)refused["error"]!["code"], (string?)refused["error"]!["message"]));
        Assert.Equal(400, (await Call(app, new { op = "send", screen = hallway, to = kitchen, action = "home", query = "walks" })).Status);
        Assert.Equal(400, (await Call(app, new { op = "send", screen = hallway, to = kitchen, action = "dance" })).Status);
        Assert.Equal(400, (await Call(app, new { op = "listen", screen = "short", name = "Kitchen iPad" })).Status);
        Assert.Equal(400, (await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 26 })).Status);
        // What a screen shows is measured again in the database's own form, which spaces it out: 3,741 characters as
        // sent, past 4,000 as stored.
        var wide = Enumerable.Range(0, 360).ToDictionary(i => $"k{i}", i => i);
        Assert.Equal(3741, JsonSerializer.Serialize(wide).Length);
        Assert.Equal(400, (await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0, showing = wide })).Status);
    }

    [PostgresFact] public async Task A_command_not_taken_within_30_seconds_expires_and_is_never_delivered()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        var kitchen = NewScreen("kitchen");
        await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default);
        var late = await store.Send(harness, kitchen, "layout", new JsonObject { ["layout"] = "band" }, null, TimeSpan.Zero, default);
        var soon = await store.Send(harness, kitchen, "home", [], null, TimeSpan.Zero, default);
        Assert.Equal("pending", (string?)late["result"]);
        var expires = DateTimeOffset.Parse((string)late["sentAt"]!).AddSeconds(30);
        await using (var cmd = fixture.Source.CreateCommand("SELECT expires_at FROM screen_commands WHERE id=@c"))
        {
            cmd.Parameters.AddWithValue("c", Guid.Parse((string)late["id"]!));
            Assert.Equal(expires, new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync())!, TimeSpan.Zero));
        }
        // The first command's 30 seconds pass before the screen looks.
        await using (var cmd = fixture.Source.CreateCommand("UPDATE screen_commands SET sent_at=sent_at-interval '31 seconds',expires_at=expires_at-interval '31 seconds' WHERE id=@c"))
        {
            cmd.Parameters.AddWithValue("c", Guid.Parse((string)late["id"]!));
            await cmd.ExecuteNonQueryAsync();
        }
        var heard = await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default);
        Assert.Equal((string?)soon["id"], (string?)heard["command"]!["id"]);
        Assert.Null((await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default))["command"]);
        var expired = await store.Command(harness, Guid.Parse((string)late["id"]!), default);
        Assert.Equal(("expired", null), ((string?)expired!["result"], (string?)expired["deliveredAt"]));
        Assert.Null(await store.Command(harness, Guid.NewGuid(), default));
    }

    [PostgresFact] public async Task Twenty_commands_wait_per_screen_and_a_new_screen_past_the_hundredth_replaces_the_one_unseen_longest()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        await using (var clear = fixture.Source.CreateCommand("DELETE FROM screens")) await clear.ExecuteNonQueryAsync();
        var kitchen = NewScreen("kitchen"); var hallway = NewScreen("hallway");
        await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default);
        await store.Listen(app, hallway, "Hallway PC", TimeSpan.Zero, null, null, default);
        for (var i = 0; i < 20; i++) await store.Send(harness, kitchen, "home", [], null, TimeSpan.Zero, default);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => store.Send(harness, kitchen, "home", [], null, TimeSpan.Zero, default))).SqlState);
        var (status, full) = await Call(app, new { op = "send", screen = hallway, to = kitchen, action = "undo" });
        Assert.Equal((400, "That screen already has 20 commands waiting."), (status, (string?)full["error"]!["message"]));
        // Taking one makes room for one more.
        await store.Listen(app, kitchen, "Kitchen iPad", TimeSpan.Zero, null, null, default);
        await store.Send(harness, kitchen, "home", [], null, TimeSpan.Zero, default);

        // 100 screens; the kitchen, unseen longest, makes way for the 101st, its commands with it.
        for (var i = 0; i < 98; i++) await store.Listen(app, NewScreen("screen"), "Fictional screen", TimeSpan.Zero, null, null, default);
        await using (var cmd = fixture.Source.CreateCommand($"UPDATE screens SET seen_at=now()-interval '1 hour' WHERE id='{kitchen}'"))
            await cmd.ExecuteNonQueryAsync();
        async Task<long> Count(string table)
        {
            await using var cmd = fixture.Source.CreateCommand($"SELECT count(*) FROM {table}");
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Equal(100, await Count("screens"));
        var newest = NewScreen("garage");
        await store.Listen(app, newest, "Garage tablet", TimeSpan.Zero, null, null, default);
        Assert.Equal(100, await Count("screens"));
        var ids = (await store.List(app, default)).Select(s => (string)s!["id"]!).ToArray();
        Assert.DoesNotContain(kitchen, ids);
        Assert.Contains(newest, ids);
        Assert.Contains(hallway, ids);
        Assert.Equal(0, await Count("screen_commands"));
        // A screen seen again is updated, never counted twice.
        await store.Listen(app, newest, "Garage tablet", TimeSpan.Zero, null, null, default);
        Assert.Equal(100, await Count("screens"));
        await using (var clear = fixture.Source.CreateCommand("DELETE FROM screens")) await clear.ExecuteNonQueryAsync();
    }

    [PostgresFact] public async Task A_dashboards_star_opens_the_dashboard_and_unknown_stars_and_dashboards_are_refused()
    {
        var config = TestSettings.Use();
        var app = await App(); var harness = await Harness();
        // One star on the map: a body data star from a fictional reading.
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        using (var call = JsonDocument.Parse($$"""
            {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:screens-1","source":{"externalId":"screens-1","kind":"body-readings",
             "title":"Fictional body readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
             "measurements":[{"metric":"weight","value":71.4,"unit":"kg","observedAt":"{{at}}","key":"screens-1"}]}
            """))
            await new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config).Measurements("cgm", call.RootElement, default);
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, AdapterRegistry.Empty, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), AdapterRegistry.Empty);
        Assert.Equal("complete", await host.Refresh(false, default));
        var body = new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:body").GraphId;
        var draft = new DashboardDraft("Fictional body", "", "<p>DEMO</p>", [new("body", "data", body)], "First version.");
        var (dashboard, _) = await new DashboardStore(fixture.Source).Save(harness, null, null, draft, [new(body, "Alex · Body")], "First version.", default);

        var (kitchen, hallway) = (NewScreen("kitchen"), NewScreen("hallway"));
        await Call(app, new { op = "listen", screen = hallway, name = "Hallway PC", hold = 0 });
        await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0 });
        using var stop = new CancellationTokenSource();
        var running = Run(app, kitchen, "Kitchen iPad", stop.Token);
        try
        {
            async Task<(int Status, JsonNode Content)> Send(object command) =>
                await Call(app, JsonSerializer.SerializeToNode(command)!.AsObject().Concat(new JsonObject { ["op"] = "send", ["screen"] = hallway, ["to"] = kitchen })
                    .ToDictionary(p => p.Key, p => p.Value?.DeepClone()), host);
            var (status, opened) = await Send(new { action = "open_star", star = $"{CatalogStars.Provider}:dashboard:{dashboard}" });
            Assert.Equal(200, status);
            Valid(opened);
            Assert.Equal(("open_dashboard", dashboard.ToString(), "shown"), ((string?)opened["command"]!["action"],
                (string?)opened["command"]!["args"]!["dashboard"], (string?)opened["command"]!["result"]));
            var (_, star) = await Send(new { action = "open_star", star = body });
            Assert.Equal(("open_star", body), ((string?)star["command"]!["action"], (string?)star["command"]!["args"]!["star"]));
            Assert.Equal("open_dashboard", (string?)(await Send(new { action = "open_dashboard", dashboard })).Content["command"]!["action"]);
            Assert.Equal(200, (await Send(new { action = "corner_card", corner = "recovery", dashboard = (Guid?)null })).Status);
            // move_tile's own `to` goes in args.
            var (moved, tile) = await Send(new { action = "move_tile", args = new { tile = "main", to = "changed" } });
            Assert.Equal(200, moved);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"tile":"main","to":"changed"}"""), tile["command"]!["args"]));
            Assert.Equal(400, (await Send(new { action = "move_tile", tile = "main" })).Status);
            // full_screen shows a dashboard, or without one the dashboard open there; set_home makes one this screen's Home,
            // and without one, or with null, restores the default Home.
            var (full, filled) = await Send(new { action = "full_screen", dashboard });
            Assert.Equal((200, "full_screen", "shown"), (full, (string?)filled["command"]!["action"], (string?)filled["command"]!["result"]));
            Assert.Equal(dashboard.ToString(), (string?)filled["command"]!["args"]!["dashboard"]);
            Assert.Equal("{}", (await Send(new { action = "full_screen" })).Content["command"]!["args"]!.ToJsonString());
            Assert.Equal(dashboard.ToString(), (string?)(await Send(new { action = "set_home", args = new { dashboard } })).Content["command"]!["args"]!["dashboard"]);
            Assert.Equal("""{"dashboard":null}""", (await Send(new { action = "set_home", dashboard = (Guid?)null })).Content["command"]!["args"]!.ToJsonString());
            Assert.Equal(400, (await Send(new { action = "full_screen", dashboard = (Guid?)null })).Status);
            // Stars not on the map and dashboards nobody saved are refused, and nothing is sent.
            var (unknown, refused) = await Send(new { action = "open_star", star = "notes:notes:Nowhere.md" });
            Assert.Equal((404, "That star is not on the map now."), (unknown, (string?)refused["error"]!["message"]));
            Assert.Equal((404, "Unknown dashboard."), ((await Send(new { action = "open_star", star = $"{CatalogStars.Provider}:dashboard:{Guid.NewGuid()}" })).Status,
                (string?)(await Send(new { action = "open_dashboard", dashboard = Guid.NewGuid() })).Content["error"]!["message"]));
            Assert.Equal(404, (await Send(new { action = "corner_card", corner = "main", dashboard = Guid.NewGuid() })).Status);
            Assert.Equal(404, (await Send(new { action = "full_screen", dashboard = Guid.NewGuid() })).Status);
            Assert.Equal(404, (await Send(new { action = "set_home", dashboard = Guid.NewGuid() })).Status);
            // control_screen refuses them the same way, saying why.
            var services = new ServiceCollection().AddSingleton(Store()).AddSingleton(host).BuildServiceProvider();
            var tools = new HarnessTools(new Retrieval(fixture.Db), new HarnessLogin(harness));
            async Task<string> Refused(Func<Task<string>> control) => (await Assert.ThrowsAsync<McpException>(control)).Message;
            Assert.Equal("star is not a star on the map now; find stars with explore_graph",
                await Refused(() => tools.ControlScreen(services, ScreenAction.OpenStar, screen: kitchen, star: "notes:notes:Nowhere.md")));
            Assert.Equal("Unknown dashboard.", await Refused(() => tools.ControlScreen(services, ScreenAction.OpenDashboard, screen: kitchen, dashboard: Guid.NewGuid())));
            Assert.Equal("Unknown dashboard.", await Refused(() => tools.ControlScreen(services, ScreenAction.OpenStar, screen: kitchen,
                star: $"{CatalogStars.Provider}:dashboard:{Guid.NewGuid()}")));
            Assert.Equal("Unknown dashboard.", await Refused(() => tools.ControlScreen(services, ScreenAction.CornerCard, screen: kitchen, corner: "main",
                dashboard: Guid.NewGuid())));
            Assert.Equal("Unknown dashboard.", await Refused(() => tools.ControlScreen(services, ScreenAction.FullScreen, screen: kitchen, dashboard: Guid.NewGuid())));
            Assert.Equal("Unknown dashboard.", await Refused(() => tools.ControlScreen(services, ScreenAction.SetHome, screen: kitchen, dashboard: Guid.NewGuid())));
            // control_screen's set_home without a dashboard restores the default Home: the screen gets null.
            using (var restored = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.SetHome, screen: kitchen)))
                Assert.Equal(("set_home", "shown"), (restored.RootElement.GetProperty("action").GetString(), restored.RootElement.GetProperty("result").GetString()));
            await using (var last = fixture.Source.CreateCommand("SELECT args::text FROM screen_commands WHERE screen_id=@s ORDER BY sent_at DESC LIMIT 1"))
            {
                last.Parameters.AddWithValue("s", kitchen);
                Assert.Equal("""{"dashboard": null}""", await last.ExecuteScalarAsync());
            }
            await using var cmd = fixture.Source.CreateCommand("SELECT count(*) FROM screen_commands WHERE screen_id=@s");
            cmd.Parameters.AddWithValue("s", kitchen);
            Assert.Equal(10L, await cmd.ExecuteScalarAsync());
        }
        finally { stop.Cancel(); await running; Directory.Delete(state, true); }
    }

    [PostgresFact] public async Task Control_screen_sends_to_the_screen_it_names_or_the_open_one_seen_last()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        // No action here opens a star, so the map is never read.
        var host = new RelationshipHost(fixture.Db, () => null, _ => new SyntheticRelationshipSource());
        var services = new ServiceCollection().AddSingleton(store).AddSingleton(host).BuildServiceProvider();
        var tools = new HarnessTools(new Retrieval(fixture.Db), new HarnessLogin(harness));
        var (kitchen, hallway, other) = (NewScreen("kitchen"), NewScreen("hallway"), NewScreen("kitchen"));
        await using (var clear = fixture.Source.CreateCommand("UPDATE screens SET seen_at=now()-interval '2 minutes'")) await clear.ExecuteNonQueryAsync();
        Assert.StartsWith("No screen is open now", (await Assert.ThrowsAsync<McpException>(() => tools.ControlScreen(services, ScreenAction.Home))).Message);
        // A name no other screen in these tests has, so the kitchen keeps it as it came.
        var named = Named("Kitchen iPad");
        await store.Listen(app, kitchen, named, TimeSpan.Zero, null, null, default);
        await store.Listen(app, hallway, "Hallway PC", TimeSpan.Zero, null, null, default);
        using var stop = new CancellationTokenSource();
        var runners = new[] { Run(app, kitchen, named, stop.Token), Run(app, hallway, "Hallway PC", stop.Token) };
        try
        {
            await Task.Delay(300);
            // By name in any case; the answer names the screen and how it went.
            using (var shown = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.Layout, screen: named.ToUpperInvariant(), layout: "band")))
            {
                Assert.Equal((kitchen, named, "layout", "shown"), (shown.RootElement.GetProperty("screen").GetProperty("id").GetString(),
                    shown.RootElement.GetProperty("screen").GetProperty("name").GetString(), shown.RootElement.GetProperty("action").GetString(),
                    shown.RootElement.GetProperty("result").GetString()));
                Assert.Equal("Done on " + named, shown.RootElement.GetProperty("detail").GetProperty("summary").GetString());
                Assert.True(shown.RootElement.GetProperty("ms").GetProperty("show").GetInt64() >= 0);
            }
            // Without a screen, the open one heard from last: the kitchen, listening again since it reported.
            await Task.Delay(300);
            using (var latest = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.Home)))
                Assert.Equal((kitchen, "shown"), (latest.RootElement.GetProperty("screen").GetProperty("id").GetString(), latest.RootElement.GetProperty("result").GetString()));
            using (var byId = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.Search, screen: hallway, query: "walks")))
                Assert.Equal((hallway, "shown"), (byId.RootElement.GetProperty("screen").GetProperty("id").GetString(), byId.RootElement.GetProperty("result").GetString()));
            // Two open screens of that name: refused, listing both; an argument the action does not take: refused, named.
            // A new screen named as an open one gets a number after its name; a person may still give it the same name.
            Assert.Equal(named.ToUpperInvariant() + " 2", (string?)(await store.Listen(app, other, named.ToUpperInvariant(), TimeSpan.Zero, null, null, default))["screen"]!["name"]);
            await store.Rename(app, other, named.ToUpperInvariant(), default);
            var ambiguous = await Assert.ThrowsAsync<McpException>(() => tools.ControlScreen(services, ScreenAction.Home, screen: named));
            Assert.Contains($"(id {kitchen})", ambiguous.Message);
            Assert.Contains($"(id {other})", ambiguous.Message);
            Assert.Equal("query is not an argument of home; it takes none",
                (await Assert.ThrowsAsync<McpException>(() => tools.ControlScreen(services, ScreenAction.Home, screen: hallway, query: "walks"))).Message);
            // With the map unavailable, a star can't be checked, so nothing is sent.
            Assert.Equal("Relationship graph unavailable; nothing was sent",
                (await Assert.ThrowsAsync<McpException>(() => tools.ControlScreen(services, ScreenAction.OpenStar, screen: hallway, star: "notes:notes:Walks.md"))).Message);
            // A screen nobody runs: sent, not confirmed within 8 seconds.
            var clock = Stopwatch.StartNew();
            using (var waiting = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.Undo, screen: other)))
            {
                Assert.Equal(("waiting", JsonValueKind.Null), (waiting.RootElement.GetProperty("result").GetString(),
                    waiting.RootElement.GetProperty("ms").GetProperty("deliver").ValueKind));
                Assert.True(clock.Elapsed >= ScreenCommands.Outcome - TimeSpan.FromMilliseconds(100));
            }
            using (var listed = JsonDocument.Parse(await tools.ListScreens(services)))
            {
                var screens = listed.RootElement.GetProperty("screens").EnumerateArray().ToArray();
                Assert.Equal(3, screens.Count(s => s.GetProperty("open").GetBoolean()));
                Assert.All(screens, s => Assert.False(s.TryGetProperty("self", out _)));
            }
        }
        finally { stop.Cancel(); await Task.WhenAll(runners); }
    }

    [PostgresFact] public async Task A_new_screen_whose_name_is_taken_gets_a_number_and_a_name_a_person_chose_is_kept()
    {
        var app = await App();
        var name = Named("Windows PC · Chrome");
        async Task<string> Listen(string screen, string named, int hold = 0) =>
            (string)(await Call(app, new { op = "listen", screen, name = named, hold })).Content["screen"]!["name"]!;
        var (first, second, third, fourth) = (NewScreen("first"), NewScreen("second"), NewScreen("third"), NewScreen("fourth"));
        Assert.Equal(name, await Listen(first, name));
        // The listen answer carries the name as stored, after a wait too, in the screen's own spelling.
        Assert.Equal(name.ToUpperInvariant() + " 2", await Listen(second, name.ToUpperInvariant(), hold: 1));
        Assert.Equal(name + " 3", await Listen(third, name));
        // A screen the engine knows keeps the name it sends: the stored one its browser took, or one a person chose.
        Assert.Equal(name + " 3", await Listen(third, name + " 3"));
        Assert.Equal("Study", await Listen(second, "Study"));
        Assert.Equal(name, (string?)(await Call(app, new { op = "rename", screen = second, name })).Content["screen"]!["name"]);
        Assert.Equal(name, (string?)(await Call(app, new { op = "rename", screen = third, name })).Content["screen"]!["name"]);
        // The lowest free number: three screens have the name itself now, and none has " 2".
        Assert.Equal(name + " 2", await Listen(fourth, name));
        // A screen unseen for 30 days no longer holds its name.
        await using (var age = fixture.Source.CreateCommand("UPDATE screens SET seen_at=now()-interval '31 days' WHERE id=ANY(@ids)"))
        {
            age.Parameters.AddWithValue("ids", new[] { first, second, third, fourth });
            await age.ExecuteNonQueryAsync();
        }
        Assert.Equal(name, await Listen(NewScreen("fifth"), name));
    }

    [PostgresFact] public async Task A_listen_never_strips_the_number_the_engine_gave_and_a_rename_still_can()
    {
        var app = await App();
        var name = Named("Windows PC · Chrome");
        async Task<string> Listen(string screen, string named, int hold = 0) =>
            (string)(await Call(app, new { op = "listen", screen, name = named, hold })).Content["screen"]!["name"]!;
        var (first, second) = (NewScreen("first"), NewScreen("second"));
        Assert.Equal(name, await Listen(first, name));
        Assert.Equal(name + " 2", await Listen(second, name));
        // The next listen left before the browser took the number from the answer, and a touch's short one may too:
        // both carry the bare default, in any spelling, and the screen keeps its number, after a wait as well.
        Assert.Equal(name + " 2", await Listen(second, name));
        Assert.Equal(name + " 2", await Listen(second, " " + name.ToUpperInvariant() + " ", hold: 1));
        // Any other name a listen carries is kept, as one a person chose; so is the numbered name itself.
        Assert.Equal(name + " 2", await Listen(second, name + " 2"));
        Assert.Equal(name + " 7", await Listen(second, name + " 7"));
        Assert.Equal(name + " 7", await Listen(second, name));
        Assert.Equal("Study", await Listen(second, "Study"));
        Assert.Equal(name, await Listen(second, name));
        // A rename sets any name, the bare default included, and listens keep it.
        Assert.Equal(name + " 2", (string?)(await Call(app, new { op = "rename", screen = second, name = name + " 2" })).Content["screen"]!["name"]);
        Assert.Equal(name, (string?)(await Call(app, new { op = "rename", screen = second, name })).Content["screen"]!["name"]);
        Assert.Equal(name, await Listen(second, name));
    }

    [PostgresFact] public async Task A_screen_keeps_its_home_list_screens_shows_it_and_the_screen_used_last_is_the_default_target()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        var host = new RelationshipHost(fixture.Db, () => null, _ => new SyntheticRelationshipSource());
        var services = new ServiceCollection().AddSingleton(store).AddSingleton(host).BuildServiceProvider();
        var tools = new HarnessTools(new Retrieval(fixture.Db), new HarnessLogin(harness));
        await using (var close = fixture.Source.CreateCommand("UPDATE screens SET seen_at=now()-interval '2 minutes' WHERE seen_at>now()-interval '2 minutes'"))
            await close.ExecuteNonQueryAsync();
        var (kitchen, hallway, garage) = (NewScreen("kitchen"), NewScreen("hallway"), NewScreen("garage"));
        // A Home need not be a dashboard that exists now: one deleted since stays, and the screen shows the default Home.
        var dashboard = Guid.NewGuid().ToString();
        var tiles = new { main = "main", recovery = "recovery", training = "training", changed = "changed" };

        // A screen keeps its Home with its layout and tiles, and the listen answer gives them back, for a browser that lost
        // its own copy. A Home in the shape a screen reports it, or a layout it does not have, is refused, and nothing changes.
        var (_, kept) = await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0, settings = new { layout = "hero", tiles, home = new { dashboard } } });
        Valid(kept);
        Assert.Equal(dashboard, (string?)kept["screen"]!["settings"]!["home"]!["dashboard"]);
        var reported = new { dashboard = new { id = dashboard, title = "DEMO · Training plan" } };
        Assert.Equal(400, (await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0, settings = new { home = reported } })).Status);
        Assert.Equal(400, (await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0, settings = new { layout = "grid", tiles } })).Status);
        var (_, again) = await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0 });
        Assert.Equal(("hero", dashboard), ((string?)again["screen"]!["settings"]!["layout"], (string?)again["screen"]!["settings"]!["home"]!["dashboard"]));

        // The kitchen shows its Home dashboard full screen and was used a minute ago; the hallway was used half an hour ago;
        // the garage reports no activeAt, as a screen older than piece 2.
        var now = DateTimeOffset.UtcNow;
        await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0, showing = new { view = "full_screen",
            dashboard = reported.dashboard, layout = "hero", tiles, narrow = false, home = reported, activeAt = now.AddMinutes(-1) } });
        await Call(app, new { op = "listen", screen = hallway, name = "Hallway PC", hold = 0, showing = new { view = "home", layout = "corners", tiles,
            narrow = false, home = (object?)null, activeAt = now.AddMinutes(-30) } });
        await Call(app, new { op = "listen", screen = garage, name = "Garage tablet", hold = 0, showing = new { view = "home", layout = "corners", tiles, narrow = false } });
        using var stop = new CancellationTokenSource();
        using var hidden = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var kitchenRuns = Run(app, kitchen, "Kitchen iPad", hidden.Token);
        var runners = new[] { kitchenRuns, Run(app, hallway, "Hallway PC", stop.Token), Run(app, garage, "Garage tablet", stop.Token) };
        try
        {
            await Task.Delay(300);
            // Seen longest ago of the three, still open: without a screen, the one a person used last still gets it.
            await using (var earlier = fixture.Source.CreateCommand("UPDATE screens SET seen_at=now()-interval '30 seconds' WHERE id=@s"))
            {
                earlier.Parameters.AddWithValue("s", kitchen);
                await earlier.ExecuteNonQueryAsync();
            }
            using (var target = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.Home)))
                Assert.Equal((kitchen, "shown"), (target.RootElement.GetProperty("screen").GetProperty("id").GetString(), target.RootElement.GetProperty("result").GetString()));
            // list_screens shows each screen's Home: the kitchen's dashboard, and the default Home (null) for the other two.
            using var listed = JsonDocument.Parse(await tools.ListScreens(services));
            var homes = listed.RootElement.GetProperty("screens").EnumerateArray().Where(s => s.GetProperty("open").GetBoolean())
                .ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("home"));
            Assert.Equal([garage, hallway, kitchen], homes.Keys.Order());
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(reported), JsonNode.Parse(homes[kitchen].GetRawText())));
            Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (homes[hallway].ValueKind, homes[garage].ValueKind));

            // The kitchen's app is hidden, so its listen ends with its request: it counts as open for a minute more, but
            // the next screen used last that still listens gets the command, which is shown there, not left to expire.
            await Task.Delay(300);
            hidden.Cancel();
            await kitchenRuns;
            Assert.Contains(await store.List(harness, default), s => (string?)s!["id"] == kitchen && (bool)s["open"]!);
            using (var target = JsonDocument.Parse(await tools.ControlScreen(services, ScreenAction.Home)))
                Assert.Equal((hallway, "shown"), (target.RootElement.GetProperty("screen").GetProperty("id").GetString(), target.RootElement.GetProperty("result").GetString()));
        }
        finally { stop.Cancel(); await Task.WhenAll(runners); }
    }

    [PostgresFact] public async Task Move_resize_and_focus_map_are_checked_against_what_the_screen_shows()
    {
        var config = TestSettings.Use();
        var app = await App(); var harness = await Harness();
        // One star on the map, a dashboard whose map is focused near it, and one saved without a layout (its page alone).
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        using (var call = JsonDocument.Parse($$"""
            {"provider":"cgm","subject":"alex","idempotencyKey":"cgm:alex:screens-arrange","source":{"externalId":"screens-arrange","kind":"body-readings",
             "title":"Fictional body readings","fetchedAt":"{{at}}","timezone":"Europe/London"},
             "measurements":[{"metric":"weight","value":71.4,"unit":"kg","observedAt":"{{at}}","key":"screens-arrange"}]}
            """))
            await new Ingest(fixture.Db, new Archive(Path.Combine(Path.GetTempPath(), "rhythm-test-archive")), config).Measurements("cgm", call.RootElement, default);
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var state = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var host = new RelationshipHost(fixture.Db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, AdapterRegistry.Empty, (wanted, ct) => CatalogReader.Inventory(fixture.Db, c.Principal, wanted, ct)), AdapterRegistry.Empty);
        Assert.Equal("complete", await host.Refresh(false, default));
        var body = new ResourceKey("alex", CatalogStars.Provider, CatalogStars.DataAccount("cgm"), "alex:body").GraphId;
        var dashboards = new DashboardStore(fixture.Source);
        var draft = new DashboardDraft("Fictional body", "", "<p>DEMO</p>", [new("body", "data", body)], "First version.");
        var layout = new DashboardLayout([new("map", "map", 0, 0, 7, 12, new MapFocus(Near: new(body))), new("page", "page", 7, 0, 5, 8),
            new("glucose", "glucose", 7, 8, 5, 4)]);
        var (arranged, _) = await dashboards.Save(harness, null, null, draft, [new(body, "Alex · Body")], "First version.", default, layout);
        var (alone, _) = await dashboards.Save(harness, null, null, draft, [new(body, "Alex · Body")], "First version.", default);

        var (kitchen, hallway) = (NewScreen("kitchen"), NewScreen("hallway"));
        await Call(app, new { op = "listen", screen = hallway, name = "Hallway PC", hold = 0 });
        async Task Shows(object showing) => await Call(app, new { op = "listen", screen = kitchen, name = "Kitchen iPad", hold = 0, showing });
        await Shows(new { view = "dashboard", dashboard = new { id = arranged, title = "Fictional body" } });
        using var stop = new CancellationTokenSource();
        var running = Run(app, kitchen, "Kitchen iPad", stop.Token);
        var services = new ServiceCollection().AddSingleton(Store()).AddSingleton(host).AddSingleton(dashboards).BuildServiceProvider();
        try
        {
            async Task<(int Status, JsonNode Content)> Send(object command)
            {
                var context = new DefaultHttpContext { RequestServices = services };
                context.Request.Method = "POST";
                var arguments = JsonSerializer.SerializeToNode(command)!.AsObject();
                foreach (var (name, value) in new JsonObject { ["op"] = "send", ["screen"] = hallway, ["to"] = kitchen }) arguments[name] = value?.DeepClone();
                context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = "s", method = "tools/call",
                    @params = new { name = "app_screens", arguments } }));
                context.Response.Body = new MemoryStream();
                await AppEndpoint.Handle(context, new Caller(app, "app", "app", null), host, new AppHealth(fixture.Db, config), AdapterRegistry.Empty);
                return (context.Response.StatusCode, JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!["result"]!["structuredContent"]!);
            }
            string? Error(JsonNode content) => (string?)content["error"]?["message"];
            // Without a dashboard, the one the screen reports showing is the guard; the command goes as checked.
            var (status, sent) = await Send(new { action = "move", part = "map", place = "right" });
            Assert.Equal((200, "shown"), (status, (string?)sent["command"]!["result"]));
            Valid(sent);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"part":"map","place":"right","dashboard":"{{arranged}}"}"""), sent["command"]!["args"]));
            Assert.Equal(200, (await Send(new { action = "resize", part = "map", size = "bigger" })).Status);
            Assert.Equal(200, (await Send(new { action = "focus_map", focus = new { providers = new[] { "cgm" } } })).Status);
            Assert.Equal(200, (await Send(new { action = "focus_map", focus = (object?)null, part = "map" })).Status);
            // A part the dashboard doesn't have, a focus on a part that isn't a map, a focus star that isn't on the map.
            var (missing, refused) = await Send(new { action = "move", part = "sky", place = "right" });
            Assert.Equal((404, "part sky is not a part of that dashboard; list_screens shows the parts a screen shows"), (missing, Error(refused)));
            Assert.Equal(404, (await Send(new { action = "move", part = "map", with = "insights" })).Status);
            var (unmapped, notMap) = await Send(new { action = "focus_map", part = "page", focus = new { query = "walks" } });
            Assert.Equal((400, "part page is not a map; focus_map changes a map part's focus"), (unmapped, Error(notMap)));
            var (gone, star) = await Send(new { action = "focus_map", focus = new { near = new { star = "notes:notes:Nowhere.md" } } });
            Assert.Equal((404, "focus.near.star is not a star on the map now; find stars with explore_graph"), (gone, Error(star)));
            // A dashboard saved without a layout is its page alone; one nobody saved is unknown.
            Assert.Equal(404, (await Send(new { action = "move", part = "map", place = "left", dashboard = alone })).Status);
            Assert.Equal(400, (await Send(new { action = "focus_map", focus = new { query = "walks" }, dashboard = alone })).Status);
            Assert.Equal(200, (await Send(new { action = "resize", part = "page", w = 6, h = 6, dashboard = alone })).Status);
            Assert.Equal((404, "Unknown dashboard."), ((await Send(new { action = "move", part = "page", place = "left", dashboard = Guid.NewGuid() })).Status,
                Error((await Send(new { action = "move", part = "page", place = "left", dashboard = Guid.NewGuid() })).Content)));
            // At Home: Home's five parts, and its map takes no focus. A screen at Home fills the guard with null.
            await Shows(new { view = "home" });
            var (homeStatus, atHome) = await Send(new { action = "move", part = "map", place = "right" });
            Assert.Equal(200, homeStatus);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"part":"map","place":"right","dashboard":null}"""), atHome["command"]!["args"]));
            Assert.Equal((404, "part page is not one of Home's parts: map, main, recovery, training, changed"),
                ((await Send(new { action = "move", part = "page", place = "right" })).Status, Error((await Send(new { action = "move", part = "page", place = "right" })).Content)));
            Assert.Equal(400, (await Send(new { action = "focus_map", focus = new { query = "walks" } })).Status);
            // Anywhere else, the guard stays out and the screen checks the command against what it shows.
            await Shows(new { view = "star", star = new { id = body, title = "Alex · Body" } });
            var (_, elsewhere) = await Send(new { action = "move", part = "map", place = "right" });
            Assert.False(elsewhere["command"]!["args"]!.AsObject().ContainsKey("dashboard"));

            // control_screen does the same; a dashboard given as null is the screen's default Home, told from one left out.
            HarnessTools Written(string arguments) => new(new Retrieval(fixture.Db), new HarnessLogin(harness))
            { Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments) };
            var tools = Written($$"""{"action":"focus_map","screen":"{{kitchen}}","focus":{"query":"walks"},"dashboard":null}""");
            Assert.Equal("Home's map takes no focus; focus_map changes a map part of a dashboard",
                (await Assert.ThrowsAsync<McpException>(() => tools.ControlScreen(services, ScreenAction.FocusMap, screen: kitchen, focus: new(Query: "walks")))).Message);
            // Any other null is left out, as before piece 3, so a client may send null for every argument it doesn't use.
            async Task<string?> Result(string arguments, Func<HarnessTools, Task<string>> call)
            {
                using var sent = JsonDocument.Parse(await call(Written(arguments)));
                return sent.RootElement.GetProperty("result").GetString();
            }
            Assert.Equal("shown", await Result("""{"action":"home","star":null,"dashboard":null,"focus":null}""",
                t => t.ControlScreen(services, ScreenAction.Home, screen: kitchen)));
            Assert.Equal("shown", await Result($$"""{"action":"open_star","star":"{{body}}","dashboard":null,"query":null}""",
                t => t.ControlScreen(services, ScreenAction.OpenStar, screen: kitchen, star: body)));
            Assert.Equal("shown", await Result("""{"action":"full_screen","dashboard":null}""", t => t.ControlScreen(services, ScreenAction.FullScreen, screen: kitchen)));
            Assert.Equal("shown", await Result($$"""{"action":"move","part":"map","place":"right","with":null,"x":null,"focus":null,"dashboard":"{{arranged}}"}""",
                t => t.ControlScreen(services, ScreenAction.Move, screen: kitchen, part: "map", place: "right", dashboard: arranged)));
            // A focus is read as it was written, by the engine's rules: a member named in another case is refused.
            Assert.Equal("focus takes query, near {star, depth}, types, providers, groups, owners and links", (await Assert.ThrowsAsync<McpException>(() =>
                Written("""{"action":"focus_map","focus":{"Query":"walks"}}""").ControlScreen(services, ScreenAction.FocusMap, screen: kitchen,
                    focus: new(Query: "walks")))).Message);
            using (var shown = JsonDocument.Parse(await new HarnessTools(new Retrieval(fixture.Db), new HarnessLogin(harness))
                .ControlScreen(services, ScreenAction.Move, screen: kitchen, part: "map", place: "left", dashboard: arranged)))
                Assert.Equal(("move", "shown"), (shown.RootElement.GetProperty("action").GetString(), shown.RootElement.GetProperty("result").GetString()));
            Assert.Equal("part sky is not a part of that dashboard; list_screens shows the parts a screen shows",
                (await Assert.ThrowsAsync<McpException>(() => new HarnessTools(new Retrieval(fixture.Db), new HarnessLogin(harness))
                    .ControlScreen(services, ScreenAction.Resize, screen: kitchen, part: "sky", size: "bigger", dashboard: arranged))).Message);
            Assert.Equal("with names another part than part", (await Assert.ThrowsAsync<McpException>(() => new HarnessTools(new Retrieval(fixture.Db),
                new HarnessLogin(harness)).ControlScreen(services, ScreenAction.Move, screen: kitchen, part: "map", @with: "map"))).Message);
            await using var last = fixture.Source.CreateCommand("SELECT args::text FROM screen_commands WHERE screen_id=@s AND action='move' ORDER BY sent_at DESC LIMIT 1");
            last.Parameters.AddWithValue("s", kitchen);
            Assert.Equal($$"""{"part": "map", "place": "left", "dashboard": "{{arranged}}"}""", await last.ExecuteScalarAsync());
        }
        finally { stop.Cancel(); await running; Directory.Delete(state, true); }
    }

    [PostgresFact] public async Task Commands_go_after_30_days_and_screens_unseen_for_180()
    {
        var app = await App(); var harness = await Harness();
        var store = Store();
        var (kept, old) = (NewScreen("kept"), NewScreen("old"));
        foreach (var screen in new[] { kept, old }) await store.Listen(app, screen, "Fictional screen", TimeSpan.Zero, null, null, default);
        var (recent, stale) = (await store.Send(harness, kept, "home", [], null, TimeSpan.Zero, default), await store.Send(harness, kept, "undo", [], null, TimeSpan.Zero, default));
        await using (var age = fixture.Source.CreateCommand($"""
            UPDATE screen_commands SET sent_at=now()-interval '29 days',expires_at=now()-interval '29 days' WHERE id='{(string)recent["id"]!}';
            UPDATE screen_commands SET sent_at=now()-interval '31 days',expires_at=now()-interval '31 days' WHERE id='{(string)stale["id"]!}';
            UPDATE screens SET seen_at=now()-interval '179 days' WHERE id='{kept}';
            UPDATE screens SET seen_at=now()-interval '181 days' WHERE id='{old}';
            """))
            await age.ExecuteNonQueryAsync();
        await ScreenStore.Prune(fixture.Source, default);
        Assert.NotNull(await store.Command(harness, Guid.Parse((string)recent["id"]!), default));
        Assert.Null(await store.Command(harness, Guid.Parse((string)stale["id"]!), default));
        await using var cmd = fixture.Source.CreateCommand("SELECT array_agg(id ORDER BY id) FROM screens WHERE id IN (@a,@b)");
        cmd.Parameters.AddWithValue("a", kept); cmd.Parameters.AddWithValue("b", old);
        Assert.Equal([kept], (string[])(await cmd.ExecuteScalarAsync())!);
    }
}
