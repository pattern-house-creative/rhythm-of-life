using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge;
using Knowledge.Mcp;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// Composable layouts without a database (docs/dev/screens.md 10.2-10.5 and 10.7): a layout's rules, a focus, a screen's
// Home grid, what a save checks again, and a map part's focused sky over a fictional map.
public sealed class DashboardLayoutTests
{
    static DashboardLayout Layout(string json) => JsonSerializer.Deserialize<DashboardLayout>(json, DashboardLayout.Json)!;
    static string? Problem(string json) => DashboardLayout.Read(JsonDocument.Parse(json).RootElement, out _);
    const string Card = "6d3c2a10-0000-4000-8000-00000000d002";
    // The contract's example: the map on the left focused near the training plan, the page at the top right and glucose
    // below it.
    const string Example = """
        {"parts":[{"id":"map","kind":"map","x":0,"y":0,"w":7,"h":12,"focus":{"near":{"star":"notes:notes:Training%20plan.md","depth":1}}},
          {"id":"page","kind":"page","x":7,"y":0,"w":5,"h":8},{"id":"glucose","kind":"glucose","x":7,"y":8,"w":5,"h":4}]}
        """;
    static string Parts(params string[] parts) => "{\"parts\":[" + string.Join(",", parts) + "]}";
    const string Page = """{"id":"page","kind":"page","x":0,"y":0,"w":6,"h":6}""";

    [Fact] public void A_layout_keeps_the_grids_rules()
    {
        Assert.Null(Problem(Example));
        Assert.Null(DashboardLayout.PageAlone.Problem());
        Assert.Equal("""{"parts":[{"id":"page","kind":"page","x":0,"y":0,"w":12,"h":12}]}""", DashboardLayout.PageAlone.ToJson());
        // Every kind at its smallest, with free cells between them.
        Assert.Null(Problem(Parts("""{"id":"page","kind":"page","x":0,"y":0,"w":3,"h":3}""", """{"id":"sky","kind":"map","x":3,"y":0,"w":4,"h":4}""",
            """{"id":"sky_two","kind":"map","x":7,"y":0,"w":4,"h":4}""", """{"id":"glucose","kind":"glucose","x":0,"y":4,"w":2,"h":2}""",
            """{"id":"recovery","kind":"recovery","x":2,"y":4,"w":2,"h":2}""", """{"id":"training","kind":"training","x":4,"y":4,"w":2,"h":2}""",
            """{"id":"changed","kind":"changed","x":6,"y":4,"w":2,"h":2}""", """{"id":"insights","kind":"insights","x":8,"y":4,"w":3,"h":2}""",
            $$"""{"id":"card_a","kind":"card","x":0,"y":6,"w":2,"h":2,"dashboard":"{{Card}}"}""",
            $$"""{"id":"card_b","kind":"card","x":2,"y":6,"w":2,"h":2,"dashboard":"{{Card}}"}""",
            $$"""{"id":"card_c","kind":"card","x":4,"y":6,"w":2,"h":2,"dashboard":"{{Card}}"}""",
            $$"""{"id":"card_d","kind":"card","x":6,"y":6,"w":2,"h":2,"dashboard":"{{Card}}"}""")));
        // 13 parts, none, no page, two pages, three maps, five cards, two glucose tiles.
        Assert.Equal("layout.parts holds 1-12 parts", Problem("""{"parts":[]}"""));
        Assert.Equal("layout.parts holds 1-12 parts", Problem(Parts([.. Enumerable.Range(0, 13).Select(i => $$"""{"id":"p{{i}}","kind":"glucose","x":0,"y":0,"w":2,"h":2}""")])));
        Assert.Equal("layout has exactly one page part", Problem(Parts("""{"id":"sky","kind":"map","x":0,"y":0,"w":4,"h":4}""")));
        Assert.Equal("layout has exactly one page part", Problem(Parts(Page, """{"id":"again","kind":"page","x":6,"y":6,"w":6,"h":6}""")));
        Assert.Equal("layout has at most 2 map parts", Problem(Parts(Page, """{"id":"a","kind":"map","x":6,"y":0,"w":4,"h":4}""",
            """{"id":"b","kind":"map","x":0,"y":6,"w":4,"h":4}""", """{"id":"c","kind":"map","x":6,"y":6,"w":4,"h":4}""")));
        Assert.Equal("layout has at most 4 card parts", Problem(Parts([Page, .. Enumerable.Range(0, 5).Select(i =>
            $$"""{"id":"c{{i}}","kind":"card","x":{{i * 2}},"y":8,"w":2,"h":2,"dashboard":"{{Card}}"}""")])));
        Assert.Equal("layout has at most one glucose part", Problem(Parts(Page, """{"id":"a","kind":"glucose","x":6,"y":0,"w":2,"h":2}""",
            """{"id":"b","kind":"glucose","x":8,"y":0,"w":2,"h":2}""")));
        // Overlapping parts, a part outside the grid or below its kind's smallest size, a repeated id.
        Assert.Equal("layout.parts page and sky overlap at cell 4, 4", Problem(Parts(Page, """{"id":"sky","kind":"map","x":4,"y":4,"w":4,"h":4}""")));
        Assert.Equal("layout.parts[1]: x + w is at most 12", Problem(Parts(Page, """{"id":"sky","kind":"map","x":9,"y":6,"w":4,"h":4}""")));
        Assert.Equal("layout.parts[1]: y is 0-11", Problem(Parts(Page, """{"id":"sky","kind":"map","x":6,"y":12,"w":4,"h":4}""")));
        Assert.Equal("layout.parts[1]: w is 1-12", Problem(Parts(Page, """{"id":"sky","kind":"map","x":6,"y":6,"w":0,"h":4}""")));
        Assert.Equal("layout.parts[1] is a map part, at least 4 × 4 cells", Problem(Parts(Page, """{"id":"sky","kind":"map","x":6,"y":6,"w":4,"h":3}""")));
        Assert.Equal("layout.parts[1] is a insights part, at least 3 × 2 cells", Problem(Parts(Page, """{"id":"tips","kind":"insights","x":6,"y":6,"w":2,"h":3}""")));
        Assert.Equal("layout.parts[0] is a page part, at least 3 × 3 cells", Problem(Parts("""{"id":"page","kind":"page","x":0,"y":0,"w":12,"h":2}""")));
        Assert.Equal("layout.parts: two parts have the id page", Problem(Parts(Page, """{"id":"page","kind":"glucose","x":6,"y":6,"w":2,"h":2}""")));
        // Ids, kinds and the members each kind takes, and nothing else.
        Assert.Equal("layout.parts[1].id is a lowercase letter, then up to 31 lowercase letters, digits or underscores",
            Problem(Parts(Page, """{"id":"Sky","kind":"map","x":6,"y":6,"w":4,"h":4}""")));
        Assert.StartsWith("layout.parts[1].kind is one of page, map", Problem(Parts(Page, """{"id":"clock","kind":"clock","x":6,"y":6,"w":2,"h":2}""")));
        Assert.Equal("layout.parts[1] takes only id, kind, x, y, w and h, with focus for a map or dashboard for a card",
            Problem(Parts(Page, """{"id":"sky","kind":"map","x":6,"y":6,"w":4,"h":4,"colour":"teal"}""")));
        Assert.Equal("layout takes only parts", Problem("""{"parts":[{"id":"page","kind":"page","x":0,"y":0,"w":12,"h":12}],"theme":"dark"}"""));
        Assert.Equal("layout.parts[1] needs x, y, w and h, in cells", Problem(Parts(Page, """{"id":"sky","kind":"map","x":6,"y":6,"w":4}""")));
        Assert.Equal("layout.parts[1].dashboard is the id of the dashboard a card shows", Problem(Parts(Page, """{"id":"c","kind":"card","x":6,"y":6,"w":2,"h":2}""")));
        Assert.Equal("layout.parts[1]: dashboard is for a card part",
            Problem(Parts(Page, $$"""{"id":"g","kind":"glucose","x":6,"y":6,"w":2,"h":2,"dashboard":"{{Card}}"}""")));
        Assert.Equal("layout.parts[0]: focus is for a map part", Problem(Parts("""{"id":"page","kind":"page","x":0,"y":0,"w":6,"h":6,"focus":{"query":"x"}}""")));
        // Members are named as the contract names them, and numbers are numbers.
        Assert.NotNull(Problem(Parts(Page, """{"id":"sky","kind":"map","x":"6","y":6,"w":4,"h":4}""")));
        Assert.NotNull(Problem(Parts(Page, """{"ID":"sky","kind":"map","x":6,"y":6,"w":4,"h":4}""")));
        Assert.Equal("layout is an object, {parts: [...]}", Problem("[]"));
    }

    [Fact] public void A_focus_narrows_by_its_members_and_is_stored_trimmed_without_repeats()
    {
        static string? Focus(string json) => MapFocus.Read(JsonDocument.Parse(json).RootElement, "focus", out _);
        Assert.Null(Focus("""{"query":"training"}"""));
        Assert.Null(Focus("""{"near":{"star":"notes:notes:Plan.md","depth":2},"types":["note","document"],"providers":["garmin"],"groups":["rhythm:alex:garmin"],"owners":["alex","household"],"links":["verified"]}"""));
        Assert.Equal("focus has at least one of query, near, types, providers, groups, owners or links; null shows everything", Focus("{}"));
        Assert.Equal("focus takes query, near {star, depth}, types, providers, groups, owners and links", Focus("""{"tags":["run"]}"""));
        Assert.Equal("focus takes query, near {star, depth}, types, providers, groups, owners and links", Focus("""{"near":{"star":"x","hops":1}}"""));
        Assert.Equal("focus.query is 1-100 characters once trimmed, with no control characters", Focus("""{"query":"   "}"""));
        Assert.Equal("focus.query is 1-100 characters once trimmed, with no control characters", Focus($$"""{"query":"{{new string('a', 101)}}"}"""));
        Assert.Null(Focus($$"""{"query":"{{string.Concat(Enumerable.Repeat("🌊", 100))}}"}"""));
        Assert.Equal("focus.near.star is a star's graphId from the map", Focus("""{"near":{"star":" "}}"""));
        // A star and a group id have no control characters, as the app's parts.ts checks them; both are counted in
        // characters once trimmed.
        Assert.Equal("focus.near.star is a star's graphId from the map", Focus("""{"near":{"star":"notes:notes:Plan\u0007.md"}}"""));
        Assert.Null(Focus("""{"near":{"star":" notes:notes:Plan.md\t"}}"""));
        Assert.Equal("focus.near.depth is 1 or 2", Focus("""{"near":{"star":"x","depth":3}}"""));
        Assert.StartsWith("focus.types lists 1-7 of theme", Focus("""{"types":["tag"]}"""));
        Assert.StartsWith("focus.types lists", Focus("""{"types":[]}"""));
        Assert.StartsWith("focus.providers lists 1-20 source ids", Focus("""{"providers":["Garmin"]}"""));
        Assert.StartsWith("focus.groups lists 1-20 group ids", Focus($$"""{"groups":["{{new string('g', 301)}}"]}"""));
        Assert.StartsWith("focus.groups lists 1-20 group ids", Focus("""{"groups":["notes:alex:Run\u0001ning"]}"""));
        Assert.Null(Focus($$"""{"groups":["{{string.Concat(Enumerable.Repeat("🌊", 300))}}"," notes:alex:Running "]}"""));
        Assert.StartsWith("focus.owners lists 1-10 owners", Focus("""{"owners":["Alex"]}"""));
        Assert.StartsWith("focus.links lists 1-4 of verified", Focus("""{"links":["disputed"]}"""));
        Assert.Equal("focus is at most 1,500 characters",
            Focus("{\"groups\":[" + string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"{i}{new string('g', 80)}\"")) + "]}"));
        // Stored as given, trimmed, lists without repeats and near's depth written out.
        Assert.Null(MapFocus.Read(JsonDocument.Parse("""
            {"query":"  training ","near":{"star":" notes:notes:Plan.md "},"providers":["garmin","garmin"," drive"]}
            """).RootElement, "focus", out var focus));
        Assert.Equal("""{"query":"training","near":{"star":"notes:notes:Plan.md","depth":1},"providers":["garmin","drive"]}""",
            JsonSerializer.Serialize(focus!.Normalized(), DashboardLayout.Json));
    }

    [Fact] public void A_layout_is_measured_as_the_database_stores_it()
    {
        // jsonb's text form puts a space after every colon and comma and leaves other characters as they are.
        Assert.Equal("""{"a": 1, "b": [1, 2]}""".Length, DashboardLayout.StoredLength(JsonNode.Parse("""{"a":1,"b":[1,2]}""")));
        Assert.Equal("""{"q": "\"é🌊\n"}""".EnumerateRunes().Count(), DashboardLayout.StoredLength(JsonNode.Parse("""{"q":"\"é🌊\n"}""")));
        Assert.Equal("""{"x": null, "y": [], "z": {}}""".Length, DashboardLayout.StoredLength(JsonNode.Parse("""{"x":null,"y":[],"z":{}}""")));
        Assert.Equal(8, DashboardLayout.StoredLength(JsonNode.Parse("\"\\u0001\"")));
        // Twelve parts, each with as much focus as it may hold, would pass 8,000 characters as stored.
        var heavy = new MapFocus(Groups: [.. Enumerable.Range(0, 20).Select(i => $"{i}{new string('g', 60)}")]);
        var parts = new List<DashboardPart> { new("page", "page", 0, 0, 3, 3), new("a", "map", 3, 0, 4, 4, heavy), new("b", "map", 7, 0, 4, 4, heavy),
            new("c", "map", 0, 4, 4, 4, heavy) };
        Assert.Equal("layout has at most 2 map parts", new DashboardLayout(parts).Problem());
        Assert.Null(new DashboardLayout(parts[..3]).Problem());
        Assert.True(DashboardLayout.StoredLength(JsonNode.Parse(new DashboardLayout(parts[..3]).ToJson())) is > 2000 and < DashboardLayout.MaxLength);
    }

    [Fact] public void A_screens_home_grid_has_homes_five_parts_at_homes_smallest_sizes()
    {
        static bool Grid(string json) => DashboardLayout.HomeGrid(JsonDocument.Parse(json).RootElement);
        // The Corners layout as a grid (10.5's table).
        const string Corners = """
            {"parts":[{"id":"map","x":2,"y":0,"w":8,"h":12},{"id":"main","x":0,"y":0,"w":2,"h":8},{"id":"recovery","x":10,"y":0,"w":2,"h":8},
              {"id":"training","x":0,"y":8,"w":2,"h":4},{"id":"changed","x":10,"y":8,"w":2,"h":4}]}
            """;
        Assert.True(Grid(Corners));
        Assert.True(Grid("""
            {"parts":[{"id":"map","x":3,"y":0,"w":9,"h":8},{"id":"main","x":0,"y":0,"w":3,"h":12},{"id":"recovery","x":3,"y":8,"w":3,"h":4},
              {"id":"training","x":6,"y":8,"w":3,"h":4},{"id":"changed","x":9,"y":8,"w":3,"h":4}]}
            """));
        Assert.False(Grid("null"));
        Assert.False(Grid(Corners.Replace("""{"id":"changed","x":10,"y":8,"w":2,"h":4}""", """{"id":"main","x":10,"y":8,"w":2,"h":4}""")));
        Assert.False(Grid(Corners.Replace(""",{"id":"changed","x":10,"y":8,"w":2,"h":4}""", "")));
        // A corner below 2 × 3 at Home, the map below 4 × 4, two parts on one cell, a member a Home part doesn't have.
        Assert.False(Grid(Corners.Replace("""{"id":"changed","x":10,"y":8,"w":2,"h":4}""", """{"id":"changed","x":10,"y":10,"w":2,"h":2}""")));
        Assert.False(Grid(Corners.Replace("""{"id":"map","x":2,"y":0,"w":8,"h":12}""", """{"id":"map","x":2,"y":0,"w":3,"h":12}""")));
        Assert.False(Grid(Corners.Replace("""{"id":"changed","x":10,"y":8,"w":2,"h":4}""", """{"id":"changed","x":9,"y":8,"w":2,"h":4}""")));
        Assert.False(Grid(Corners.Replace("""{"id":"changed","x":10,"y":8,"w":2,"h":4}""", """{"id":"changed","kind":"corner","x":10,"y":8,"w":2,"h":4}""")));
        Assert.False(Grid(Corners.Replace("""{"id":"changed","x":10,"y":8,"w":2,"h":4}""", """{"id":"changed","x":10,"y":8,"w":2,"h":4.5}""")));
        Assert.False(Grid(Corners.Replace("]}", "],\"preset\":\"corners\"}")));
        // Well inside the settings' 2,000 characters as stored.
        Assert.True(DashboardLayout.StoredLength(JsonNode.Parse(Corners)) < 400);
    }

    [Fact] public void Only_new_parts_and_changed_references_are_checked_again()
    {
        var current = Layout(Example);
        var moved = Layout(Example.Replace("\"x\":0,\"y\":0,\"w\":7", "\"x\":5,\"y\":0,\"w\":7").Replace("\"x\":7,\"y\":0,\"w\":5,\"h\":8", "\"x\":0,\"y\":0,\"w\":5,\"h\":8")
            .Replace("\"x\":7,\"y\":8", "\"x\":0,\"y\":8"));
        Assert.Null(moved.Problem());
        Assert.Empty(moved.Changed(current));
        // A focus compared as stored: the same focus with its depth left out is not a change.
        Assert.Empty(Layout(Example.Replace(",\"depth\":1", "")).Changed(current));
        Assert.Equal(["map"], Layout(Example.Replace("\"depth\":1", "\"depth\":2")).Changed(current).Select(x => x.Part.Id));
        var carded = Layout(Example.Replace("{\"id\":\"glucose\",\"kind\":\"glucose\"", $$"""{"id":"glucose","kind":"card","dashboard":"{{Card}}" """));
        Assert.Equal(["glucose"], carded.Changed(current).Select(x => x.Part.Id));
        Assert.Equal(["map", "page", "glucose"], current.Changed(null).Select(x => x.Part.Id));
    }

    [Fact] public async Task A_save_refuses_a_focus_star_it_cant_read_and_a_card_of_itself_or_of_nothing()
    {
        var self = Guid.NewGuid();
        Task<IReadOnlySet<string>> None(IReadOnlyList<string> stars) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        Task<IReadOnlySet<string>> All(IReadOnlyList<string> stars) => Task.FromResult<IReadOnlySet<string>>(stars.ToHashSet());
        Task<bool> Saved(Guid id) => Task.FromResult(id == Guid.Parse(Card));
        var example = Layout(Example);
        Assert.Equal("layout.parts[0].focus.near.star is not a star you can read; find stars with explore_graph",
            await example.ReferenceProblem(null, self, None, Saved));
        Assert.Null(await example.ReferenceProblem(null, self, All, Saved));
        // A part whose star has gone since still moves: its rectangle alone changed.
        var moved = example with { Parts = [example.Parts[0] with { X = 5 }, example.Parts[1] with { X = 0 }, example.Parts[2] with { X = 0 }] };
        Assert.Null(await moved.ReferenceProblem(example, self, None, Saved));
        var card = new DashboardLayout([new("page", "page", 0, 0, 6, 6), new("c", "card", 6, 0, 2, 2, Dashboard: self)]);
        Assert.Equal("layout.parts[1].dashboard is this dashboard; a card shows another one", await card.ReferenceProblem(null, self, All, Saved));
        Assert.Equal("layout.parts[1].dashboard names no saved dashboard", await card.ReferenceProblem(null, Guid.NewGuid(), All, Saved));
        Assert.Null(await (card with { Parts = [card.Parts[0], card.Parts[1] with { Dashboard = Guid.Parse(Card) }] }).ReferenceProblem(null, self, All, Saved));
    }

    [Fact] public void The_app_endpoint_takes_a_layout_save_and_a_map_read_each_with_exactly_its_members()
    {
        TestSettings.Use();
        static bool Allowed(string args) => AppEndpoint.Allowed(JsonDocument.Parse(
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"tools/call\",\"params\":{\"name\":\"app_dashboards\",\"arguments\":" + args + "}}").RootElement);
        const string Id = "\"id\":\"6d3c2a10-0000-4000-8000-00000000d001\"";
        Assert.True(Allowed("{" + Id + ",\"version\":3,\"layout\":" + Example + ",\"base\":0,\"note\":\"Moved the map right\"}"));
        Assert.False(Allowed("{" + Id + ",\"version\":3,\"layout\":" + Example + ",\"note\":\"Moved the map right\"}"));
        Assert.False(Allowed("{" + Id + ",\"layout\":" + Example + ",\"base\":0,\"note\":\"Moved the map right\"}"));
        Assert.False(Allowed("{" + Id + ",\"version\":3,\"layout\":" + Example + ",\"base\":-1,\"note\":\"Moved the map right\"}"));
        Assert.False(Allowed("{" + Id + ",\"version\":3,\"layout\":" + Example + ",\"base\":0,\"note\":\"  \"}"));
        Assert.False(Allowed("{" + Id + ",\"version\":3,\"layout\":[],\"base\":0,\"note\":\"Moved\"}"));
        Assert.False(Allowed("{" + Id + ",\"version\":3,\"layout\":" + Example + ",\"base\":0,\"note\":\"Moved\",\"data\":true}"));
        Assert.True(Allowed("{" + Id + ",\"map\":\"map\"}"));
        Assert.True(Allowed("{" + Id + ",\"version\":2,\"map\":\"sky_two\",\"cursor\":\"o:300\"}"));
        Assert.False(Allowed("{" + Id + ",\"map\":\"Map\"}"));
        Assert.False(Allowed("{\"map\":\"map\"}"));
        Assert.False(Allowed("{" + Id + ",\"map\":\"map\",\"note\":\"Moved\"}"));
        Assert.False(Allowed("{" + Id + ",\"version\":3,\"map\":\"map\",\"layout\":" + Example + ",\"base\":0,\"note\":\"Moved\"}"));
    }
}

// A map part's focused sky (10.4) over a fictional map: only what the map shows, no disputed links, its own near walk,
// the sources as the app works them out, and the overview's ranking and pages.
public sealed class DashboardMapTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
    static DiscoveryRecord Star(string provider, string account, string id, string kind, string title, string subject = "alex", int age = 0,
        string? overview = null, RecordGroup[]? groups = null) =>
        new(new ResourceKey(subject, provider, account, id), "household", kind, title, null, "r1", null, Now, [], CreatedAt: Now.AddDays(-age),
            Overview: overview, Groups: groups);
    static DiscoveredLink Link(DiscoveryRecord a, DiscoveryRecord b, string verification) =>
        new(a.Key, b.Key, "references", "references", verification, "test", [new(a.Key, "line 1", "DEMO", Now)], "r1", "r1", null);

    static readonly DiscoveryRecord Plan = Star("notes", "notes", "Training plan.md", "note", "DEMO · Training plan", age: 1,
        groups: [new("notes:alex:Running", "Running")]);
    static readonly DiscoveryRecord Run = Star(CatalogStars.Provider, "data:garmin", "alex:training", "record", "Alex · Training", age: 2);
    static readonly DiscoveryRecord Sleep = Star(CatalogStars.Provider, "data:garmin", "alex:recovery", "record", "Alex · Recovery", age: 3);
    static readonly DiscoveryRecord Calendar = Star(CatalogStars.Provider, "drive", "alex:calendar", "document", "DEMO · Race calendar", age: 4,
        overview: "Spring races and the training blocks before them");
    static readonly DiscoveryRecord Knee = Star("second-brain", "memories", "m1", "memory", "DEMO · Knee note", subject: "sam", age: 5);
    static readonly DiscoveryRecord Hidden = Star("hidden", "hidden", "h1", "note", "DEMO · Training secret", age: 6);
    static readonly DiscoveryRecord Far = Star("notes", "notes", "Far.md", "note", "DEMO · Far away", age: 7);
    static readonly SnapshotView View = new([Plan, Run, Sleep, Calendar, Knee, Hidden, Far],
        [Link(Plan, Run, "verified"), Link(Run, Sleep, "inferred"), Link(Plan, Calendar, "suggested"), Link(Calendar, Far, "verified"),
         Link(Plan, Hidden, "verified"), Link(Plan, Knee, "disputed")], new Dictionary<string, DateTimeOffset>());
    static FocusedMap Read(MapFocus focus, int limit = 300, int offset = 0) =>
        DashboardMap.Focused(View, p => p != "hidden", focus, limit, offset, TimeSpan.FromMinutes(35));
    static string[] Titles(FocusedMap map) => [.. map.Page.nodes.Select(n => n.title)];

    [Fact] public void A_stars_source_is_worked_out_as_the_app_does()
    {
        // A data star: the provider after data:; a record saved in Rhythm: its account; any other: its own provider.
        Assert.Equal("garmin", DashboardMap.Source(Run));
        Assert.Equal("drive", DashboardMap.Source(Calendar));
        Assert.Equal("notes", DashboardMap.Source(Plan));
        Assert.Equal("second-brain", DashboardMap.Source(Knee));
        // A catalog record that isn't a document keeps the catalog's own provider.
        Assert.Equal(CatalogStars.Provider, DashboardMap.Source(Star(CatalogStars.Provider, "drive", "alex:folder", "project", "DEMO · Folder")));
    }

    [Fact] public void A_focus_shows_the_stars_that_meet_every_member_of_what_the_map_shows()
    {
        // query: a title, an overview or a group's label, in any case; never a star whose provider the map doesn't show.
        Assert.Equal(["DEMO · Training plan", "Alex · Training", "DEMO · Race calendar"], Titles(Read(new(Query: "TRAINING"))));
        Assert.Equal(["DEMO · Training plan"], Titles(Read(new(Query: "running"))));
        // near: the star and its neighbours over the links the sky draws, in either direction, never over a disputed link.
        Assert.Equal(["DEMO · Training plan", "Alex · Training", "DEMO · Race calendar"], Titles(Read(new(Near: new(Plan.Key.GraphId)))));
        Assert.Equal(["DEMO · Training plan", "Alex · Training", "DEMO · Far away", "DEMO · Race calendar", "Alex · Recovery"],
            Titles(Read(new(Near: new(Plan.Key.GraphId, 2)))));
        Assert.Equal(["Alex · Recovery", "Alex · Training"], Titles(Read(new(Near: new(Sleep.Key.GraphId)))).Order());
        Assert.Empty(Read(new(Near: new(Hidden.Key.GraphId))).Page.nodes);
        Assert.Empty(Read(new(Near: new("notes:notes:Nowhere.md"))).Page.nodes);
        // types, providers (sources), groups and owners, and every member at once.
        Assert.Equal(["DEMO · Race calendar"], Titles(Read(new(Types: ["document"]))));
        Assert.Equal(["Alex · Training", "Alex · Recovery"], Titles(Read(new(Providers: ["garmin"]))));
        Assert.Equal(["DEMO · Race calendar"], Titles(Read(new(Providers: ["drive"]))));
        Assert.Equal(["DEMO · Training plan"], Titles(Read(new(Groups: ["notes:alex:Running"]))));
        Assert.Equal(["DEMO · Knee note"], Titles(Read(new(Owners: ["sam"]))));
        Assert.Equal(["Alex · Training"], Titles(Read(new(Near: new(Plan.Key.GraphId), Providers: ["garmin"]))));
        Assert.Empty(Read(new(Query: "training", Owners: ["sam"])).Page.nodes);
    }

    [Fact] public void Links_are_the_skys_and_a_focus_may_pick_among_them()
    {
        static string[] Links(FocusedMap map) => [.. map.Page.edges.Select(e => e.verification).Order()];
        Assert.Equal(["suggested", "verified"], Links(Read(new(Near: new(Plan.Key.GraphId)))));
        Assert.Equal(["verified"], Links(Read(new(Near: new(Plan.Key.GraphId), Links: ["verified"]))));
        // The plan and the knee note are both shown; the disputed link between them never is.
        var both = Read(new(Owners: ["alex", "sam"], Types: ["note", "memory"], Query: "DEMO"));
        Assert.Contains("DEMO · Knee note", Titles(both));
        Assert.DoesNotContain("disputed", Links(both));
        Assert.Equal(["inferred", "suggested", "verified", "verified"], Links(Read(new(Query: "e"))));
    }

    [Fact] public void Matches_are_ranked_as_the_overview_ranks_them_and_paged_by_its_cursor()
    {
        // Memories, notes and what they cite first, then documents, then the rest; within each, most linked, then newest.
        var all = Read(new(Near: new(Plan.Key.GraphId, 2)), limit: 2);
        Assert.Equal((5, "o:2"), (all.Matched, all.Page.nextCursor));
        Assert.Equal(["DEMO · Training plan", "Alex · Training"], Titles(all));
        var next = Read(new(Near: new(Plan.Key.GraphId, 2)), limit: 2, offset: 2);
        Assert.Equal(["DEMO · Far away", "DEMO · Race calendar"], Titles(next));
        var last = Read(new(Near: new(Plan.Key.GraphId, 2)), limit: 2, offset: 4);
        Assert.Equal(["Alex · Recovery"], Titles(last));
        Assert.Null(last.Page.nextCursor);
        Assert.Equal(4, DashboardMap.Offset("o:4"));
        Assert.Equal(0, DashboardMap.Offset(null));
        Assert.Throws<ArgumentException>(() => DashboardMap.Offset("page 2"));
        // The page is app_graph's, issues included.
        Schemas.Valid(Schemas.Folder("mcp"), "app_graph.output.schema.json", all.Page);
        Assert.Contains("Showing 2 of 5 records matching the focus", Assert.Single(all.Page.issues).reason);
    }
}
