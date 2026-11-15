using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Cli;
using Knowledge.Mcp;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Knowledge.Tests;

// SB-P7-09 (D-240): the operator docs held to the settings schema, contracts/rhythm.schema.json. The example rhythm.yaml
// and what `rhythm init` writes are valid against it; docs/configuration.md names every key it has, shows each default it
// gives and says what turns each adapter on (its absent section, the example, init); docs/operations.md lists every setting that is off until an operator turns it on, and every connector the
// schema knows that the example does not turn on, as off and not connected; it names the switches the example shows; the
// numbers its schedules, limits and freshness give beside a setting are that setting's default, and the limits no setting
// holds are the engine's own constants and checks; and the docs that count the memory tools count what the engine makes.
// The walk itself (its steps, the capture endpoint, session hooks, an AI client) is held by nothing here: run 22's docs
// slice walked it by hand. No database, no network: files of this checkout only.
public sealed partial class OperatorDocsTests : IDisposable
{
    static readonly string Root = WriteLimits.Root;
    static string Text(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path])).Replace("\r\n", "\n", StringComparison.Ordinal);
    static readonly string ContractsFolder = Path.Combine(Root, "contracts");
    static readonly JsonObject Schema = JsonNode.Parse(Text("contracts", "rhythm.schema.json"))!.AsObject();
    readonly string scratch = Path.Combine(Path.GetTempPath(), "rhythm-docs-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }

    // ---- The settings files against the schema

    [Fact] public void The_example_settings_are_valid_against_the_schema()
    {
        Assert.Empty(Schemas.Check(ContractsFolder, "rhythm.schema.json", Yaml(Text("deploy", "compose", "rhythm.example.yaml"))));
        // The check refuses what the engine would not take: a key no section has, and a link under the calendar's.
        var example = Yaml(Text("deploy", "compose", "rhythm.example.yaml"))!.AsObject();
        example["graph"]!["similarity"]!["nosuch"] = 1;
        Assert.NotEmpty(Schemas.Check(ContractsFolder, "rhythm.schema.json", example));
        var calendar = Yaml(Text("deploy", "compose", "rhythm.example.yaml"))!.AsObject();
        calendar["adapters"]!["calendar"]!["url"] = "https://calendar.example.invalid/private.ics";
        Assert.NotEmpty(Schemas.Check(ContractsFolder, "rhythm.schema.json", calendar));
    }

    [Theory]
    [InlineData("--timezone", "Europe/London")]
    [InlineData("--exposure", "lan", "--host", "rhythm.example.invalid", "--demo", "no")]
    [InlineData("--exposure", "cloudflare", "--host", "rhythm.example.invalid")]
    [InlineData("--exposure", "tailscale", "--host", "rhythm.example.invalid", "--project-name", "rhythm-test")]
    public void What_init_writes_is_valid_against_the_schema(params string[] args) =>
        Assert.Empty(Schemas.Check(ContractsFolder, "rhythm.schema.json", Yaml(File.ReadAllText(Init(args)))));

    // `rhythm init --no-input` with these flags, in a deploy folder of its own; the settings file it wrote.
    string Init(params string[] args)
    {
        var deploy = new DeployFolder(Path.Combine(scratch, Guid.NewGuid().ToString("N")[..8], "deploy"));
        Directory.CreateDirectory(deploy.Root);
        var o = new StringWriter();
        Assert.Equal(0, InitCommand.Run(deploy, ["--no-input", "--people", "Alex, Sam", .. args],
            new Prompter(new StringReader(""), o, noInput: true, terminal: false), name => name == "RHYTHM_VERSION" ? "dev" : null));
        return deploy.Settings;
    }

    // ---- docs/configuration.md against the schema

    [Fact] public void The_configuration_page_names_every_key_and_each_default_the_schema_gives()
    {
        var page = Rows(Section(Text("docs", "configuration.md"), "## rhythm.yaml"));
        var missing = new List<string>();
        foreach (var leaf in Leaves())
        {
            var shown = Shown(page, leaf);
            if (shown is null) { missing.Add($"{leaf.Path}: not named"); continue; }
            // An adapter's enabled is its row in the adapters table, which says off (below).
            if (leaf.Path.StartsWith("adapters.", StringComparison.Ordinal) && leaf.Path.EndsWith(".enabled", StringComparison.Ordinal)) continue;
            if (leaf.Default is { } value && !Gives(shown, value)) missing.Add($"{leaf.Path}: default {value.ToJsonString()} not shown in \"{shown}\"");
        }
        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    [Fact] public void The_configuration_page_names_no_key_the_schema_lacks()
    {
        var known = Leaves().Select(l => l.Path).Concat(Containers()).ToHashSet(StringComparer.Ordinal);
        var unknown = Rows(Section(Text("docs", "configuration.md"), "## rhythm.yaml"))
            .SelectMany(r => r.Names).Where(n => !known.Contains(n)).Distinct().ToList();
        Assert.True(unknown.Count == 0, string.Join('\n', unknown));
    }

    [Fact] public void The_configuration_page_says_what_turns_each_adapter_on()
    {
        // Each adapter's state while its section is absent, read as the engine reads it: off for every adapter but
        // embeddings (AdapterSettings.Enabled), on for embeddings (EmbeddingSettings.From keeps the local model on). The
        // adapters table's first word is that state; it says "on in the example" of exactly the adapters the example turns
        // on, "in what `rhythm init` writes" of exactly those `rhythm init --no-input` turns on, and names `--notes` for
        // each one that init turns on only when given a notes folder.
        Assert.False(AbsentOn("notes"));
        Assert.True(AbsentOn("embeddings"));
        var example = RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"));
        var init = RhythmConfig.Load(Init());
        var withNotes = RhythmConfig.Load(Init("--notes", "../../demo/notes"));
        var adapters = Rows(Section(Section(Text("docs", "configuration.md"), "## rhythm.yaml"), "### Adapters"));
        var names = Schema["properties"]!["adapters"]!["properties"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Equal(["notes", "csv", "documents", "embeddings"], names.Where(n => On(example, n)));
        Assert.Equal(["csv", "documents", "embeddings"], names.Where(n => On(init, n)));
        foreach (var name in names)
        {
            var row = Assert.Single(adapters, r => r.Names.SequenceEqual([$"adapters.{name}"]));
            var cell = row.Cells[1];
            Assert.True(Regex.Match(cell, "^[a-z]+").Value == (AbsentOn(name) ? "on" : "off"), $"{name}: \"{cell}\"");
            Assert.True(On(example, name) == cell.Contains("on in the example", StringComparison.Ordinal), $"{name}: \"{cell}\"");
            Assert.True(On(init, name) == cell.Contains("in what `rhythm init` writes", StringComparison.Ordinal), $"{name}: \"{cell}\"");
            Assert.True((On(withNotes, name) && !On(init, name)) == cell.Contains("`--notes <folder>`", StringComparison.Ordinal), $"{name}: \"{cell}\"");
        }
    }

    // ---- docs/operations.md

    [Fact] public void The_operator_guide_lists_every_setting_that_is_off_until_turned_on()
    {
        var table = Rows(Section(Text("docs", "operations.md"), "## Settings that are off until you turn them on"));
        var listed = table.ToDictionary(r => Assert.Single(r.Names), r => r.Cells[1]);
        var leaves = Leaves().ToDictionary(l => l.Path);
        // Each listed one is a setting, shown with its default.
        foreach (var (path, cell) in listed)
        {
            Assert.True(leaves.TryGetValue(path, out var leaf), $"{path} is no setting");
            if (leaf!.Default is { } value) Assert.True(Gives(cell, value), $"{path}: default {value.ToJsonString()} not in \"{cell}\"");
        }
        // Every off/on setting, every one that is false by default (an adapter's enabled is the connectors' table), and the
        // choices whose other value is an opt-in.
        string[] choices = ["engine.searchKnowledge", "adapters.embeddings.thresholds", "adapters.embeddings.shadow"];
        var switches = leaves.Values.Where(l => OffOn(l.Schema) || l.Default is JsonValue v && v.GetValueKind() == JsonValueKind.False && !l.Path.EndsWith(".enabled", StringComparison.Ordinal))
            .Select(l => l.Path).Concat(choices).ToList();
        Assert.Contains("memory.scopes", switches);
        Assert.Contains("engine.writeLimit", switches);
        Assert.Contains("adapters.drive.edits", switches);
        Assert.DoesNotContain(switches, s => !listed.ContainsKey(s));
    }

    [Fact] public void The_operator_guide_labels_every_connector_the_example_leaves_off_as_not_connected()
    {
        var table = Rows(Section(Text("docs", "operations.md"), "## Connectors, each off and not connected until you connect it"));
        var example = RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"));
        // Every adapter the schema knows but those the example turns on (notes, csv, documents and embeddings, which need
        // nothing outside Rhythm): one the schema gains needs a row here even when the example leaves it out.
        var off = Schema["properties"]!["adapters"]!["properties"]!.AsObject().Select(p => p.Key).Where(a => !On(example, a)).ToList();
        Assert.Equal(["notes", "csv", "documents", "embeddings"],
            Schema["properties"]!["adapters"]!["properties"]!.AsObject().Select(p => p.Key).Where(a => On(example, a)));
        Assert.Equal(["titles", "drive", "garmin", "second-brain", "nightscout", "calendar", "tailscale", "cloudflare", "openai-tunnel"], off);
        Assert.Empty(example.Plugins);
        // Each is a row whose state on a new install is off and not connected; the CareLink plugin, which lives in its own
        // repository, too.
        foreach (var name in off.Append("plugins.carelink"))
        {
            var row = table.SingleOrDefault(r => r.Names.Contains(name.Contains('.') ? name : $"adapters.{name}"));
            Assert.True(row is not null, $"{name}: no row");
            Assert.Contains("off", row!.Cells[2], StringComparison.Ordinal);
            Assert.Contains("not connected", row.Cells[2], StringComparison.Ordinal);
        }
    }

    [Fact] public void The_operator_guide_names_the_switches_the_example_shows_at_their_defaults()
    {
        // The settings section says which off-until-turned-on settings the example and what `rhythm init` writes show at all
        // (`path: value`, at its default); every other one they leave out.
        var section = Section(Text("docs", "operations.md"), "## Settings that are off until you turn them on");
        var intro = section[..section.IndexOf("\n|", StringComparison.Ordinal)];
        var named = Code().Matches(intro).Select(m => Regex.Match(m.Groups[1].Value, @"^([a-z][A-Za-z0-9.-]*): (\S+)$"))
            .Where(m => m.Success).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.Equal(["memory.scopes", "engine.legacyToolNames", "adapters.drive.edits"], named.Keys);
        var listed = Rows(section).Select(r => Assert.Single(r.Names)).ToList();
        foreach (var settings in new[] { Yaml(Text("deploy", "compose", "rhythm.example.yaml")), Yaml(File.ReadAllText(Init())) })
        {
            var shown = listed.Select(path => (path, value: At(settings, path))).Where(x => x.value is not null)
                .Select(x => $"{x.path}: {x.value!.ToJsonString().Trim('"')}").Order(StringComparer.Ordinal);
            Assert.Equal(named.Select(n => $"{n.Key}: {n.Value}").Order(StringComparer.Ordinal), shown);
        }
    }

    // The value at a dotted path in settings read as JSON, or null when absent.
    static JsonNode? At(JsonNode? node, string path)
    {
        foreach (var key in path.Split('.')) node = node is JsonObject o && o.TryGetPropertyValue(key, out var child) ? child : null;
        return node;
    }

    [Fact] public void The_operator_guide_gives_each_settings_number_its_default()
    {
        // In the schedules, the limits and the freshness sections, a number written right after a setting (`pollSeconds`
        // (300), `graph.maxMemories` 3000) is that setting's default; a bare key is the one of the adapter named last
        // before it on its line ("Nightscout | every `pollSeconds`"); and a duration beside it in words ("(900, 15
        // minutes)") is the same time. Every dotted name there is a setting.
        var page = Text("docs", "operations.md");
        var leaves = Leaves().ToDictionary(l => l.Path);
        var adapters = Schema["properties"]!["adapters"]!["properties"]!.AsObject().Select(p => p.Key).ToList();
        var seen = new List<string>();
        var wrong = new List<string>();
        foreach (var heading in new[] { "## What runs on its own, and when", "## Limits", "## How fresh each source is" })
            // A list item's wrapped lines read as one line.
            foreach (var line in Section(page, heading).Replace("\n  ", " ", StringComparison.Ordinal).Split('\n'))
                foreach (Match m in Code().Matches(line))
                {
                    var name = Regex.Replace(m.Groups[1].Value, ": (on|off|true|false)$", "");
                    var path = leaves.ContainsKey(name) ? name
                        : adapters.Select(a => (a, at: line[..m.Index].LastIndexOf(a.Replace('-', ' '), StringComparison.OrdinalIgnoreCase)))
                            .Where(x => x.at >= 0 && leaves.ContainsKey($"adapters.{x.a}.{name}")).OrderByDescending(x => x.at)
                            .Select(x => $"adapters.{x.a}.{name}").FirstOrDefault();
                    if (path is null)
                    {
                        if (Regex.IsMatch(name, @"^[a-z][A-Za-z]*\.[A-Za-z.]+$")) wrong.Add($"{heading}: `{name}` is no setting");
                        continue;
                    }
                    var after = line[(m.Index + m.Length)..];
                    var number = Regex.Match(after, @"^\s*\(?([0-9][0-9,]*)\b");
                    if (!number.Success) continue;
                    seen.Add(path);
                    var value = double.Parse(number.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                    if (leaves[path].Default is not JsonValue d || d.GetValue<double>() != value)
                        wrong.Add($"{path}: {value} in \"{line.Trim()}\", default {leaves[path].Default?.ToJsonString()}");
                    var words = Regex.Match(after, @"^\s*\(([0-9,]+)(?: seconds)?, ([0-9]+) (minutes|hours)\)");
                    if (words.Success && int.Parse(words.Groups[2].Value, CultureInfo.InvariantCulture) * (words.Groups[3].Value == "hours" ? 3600 : 60) != value)
                        wrong.Add($"{path}: {words.Value.Trim()} is not the same time");
                }
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
        foreach (var expected in new[] { "graph.refreshSeconds", "graph.freshnessSeconds", "graph.maxMemories", "graph.maxDocuments", "graph.nodeLimit",
            "adapters.calendar.pollSeconds", "adapters.nightscout.pollSeconds", "adapters.garmin.pollSeconds" })
            Assert.Contains(expected, seen);
    }

    [Fact] public void The_operator_guide_gives_the_limits_the_engine_keeps()
    {
        // The numbers no setting holds, each against the constant, the check or the schema bound that keeps it.
        var page = Text("docs", "operations.md");
        var limits = Cells(Section(page, "## Limits"));
        void Says(string row, string words) => Assert.True(limits.TryGetValue(row, out var cell) && cell.Contains(words, StringComparison.Ordinal),
            $"the limits' \"{row}\" row does not say \"{words}\"");
        Says("writes per token, with `engine.writeLimit: on`", $"{WriteLimit.Writes} in {WriteLimit.Window.TotalMinutes:0} minutes");
        Says("a burst", $"past a token's {MemoryHolds.BurstLimit}th memory write in {MemoryHolds.BurstWindow.TotalMinutes:0} minutes");
        Says("a memory's text", $"over {MemoryHolds.TooLongBytes / 1024} KiB it is held");
        Says("a memory's text", $"a capture's body is at most {CaptureEndpoint.MaxBody / 1024} KiB");
        Says("a memory's text", $"its text {MemoryInput.MaxBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes");
        Says("the trash", $"{TrashDays()} days");
        Says("standing instructions (`memory.tools: on`)", $"{MemoryStanding.MaxChars} characters each, {MemoryStanding.MaxInScope} a scope");
        Says("people", $"{(int)Schema["properties"]!["people"]!["maxItems"]!} in `people`");
        Says("calendars", $"{Knowledge.Adapters.Calendar.CalendarAdapter.LinkPurposes.Length} private links");
        Says("a plugin's call", $"a body of at most {IngestEndpoint.MaxBody / 1024} KiB");
        // The map's two bounds together, as RelationshipHostConfiguration checks them.
        Says("the map", "(at most 4900 together)");
        var map = Relationships.RelationshipHostConfiguration.From(TestSettings.Use(), Guid.NewGuid(), scratch);
        (map with { MaxMemories = 4000, MaxDocuments = 900 }).Validate();
        (map with { MaxMemories = 900, MaxDocuments = 4000 }).Validate();
        Assert.Throws<ArgumentException>(() => (map with { MaxMemories = 4000, MaxDocuments = 901 }).Validate());
        // And the daily purge in the schedules: the trash's days, at most MemoryTrash's bound a day.
        var daily = Cells(Section(page, "## What runs on its own, and when"))["the daily jobs"];
        Assert.Contains($"memories in the trash past their {TrashDays()} days deleted for good, at most {MemoryTrash.Batch * MemoryTrash.Batches} a day", daily);
    }

    // The trash's retention, as the latest migration that defines memory_trash_days() gives it.
    static int TrashDays()
    {
        var days = Directory.GetFiles(Path.Combine(Root, "engine", "migrations"), "*.sql").Order(StringComparer.Ordinal)
            .Select(f => Regex.Match(File.ReadAllText(f), @"FUNCTION memory_trash_days\(\) RETURNS integer LANGUAGE sql IMMUTABLE AS \$\$ SELECT (\d+) \$\$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(days);
        return days[^1];
    }

    // A section's two-column table rows, first cell to second.
    static Dictionary<string, string> Cells(string section) => section.Split('\n')
        .Where(l => l.StartsWith('|') && !l.StartsWith("|---", StringComparison.Ordinal))
        .Select(l => l.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToArray())
        .Where(c => c.Length >= 2).GroupBy(c => c[0]).ToDictionary(g => g.Key, g => g.First()[1]);

    [Fact] public void The_docs_count_the_tools_the_engine_makes()
    {
        // The harness tools an install lists with every setting at its default and the local model on: the contract's,
        // without recall (engine.recall), get_prompt_capsule (memory.capsules) and the old alias (engine.legacyToolNames).
        var names = HarnessTools.Create(TestSettings.Use(), []).Select(t => t.ProtocolTool.Name)
            .Except(["recall", "get_prompt_capsule", "save_household_document"]).ToList();
        var harness = names.Count;
        var memory = MemoryTools.Names.Count;
        Assert.Equal(29, harness);
        var readme = Text("README.md");
        Assert.Contains($"{harness} tools (30 with `engine.recall: on`", readme, StringComparison.Ordinal);
        Assert.Contains($"{memory} more with `memory.tools: on`", readme, StringComparison.Ordinal);
        // And the settings schema's memory.tools, which editors show beside the setting.
        foreach (var page in new[] { Text("docs", "dev", "engine.md"), Text("harness", "generic-mcp", "README.md"), Text("docs", "operations.md"),
            (string)Schema["properties"]!["memory"]!["properties"]!["tools"]!["description"]! })
        {
            var counts = MemoryCount().Matches(page).Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(counts);
            Assert.All(counts, c => Assert.Equal(memory.ToString(CultureInfo.InvariantCulture), c));
        }
    }

    // A count of the memory tools, in digits or in words ("the eleven memory tools" was one such count).
    [GeneratedRegex(@"the (\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|[a-z]+teen|[a-z]+ty(?:-[a-z]+)?) memory tools")]
    private static partial Regex MemoryCount();

    // ---- Reading settings as the engine does

    // Whether settings turn an adapter on: EmbeddingSettings.From for the local model, whose absent section is on, and
    // AdapterSettings.Enabled for every other adapter.
    static bool On(RhythmConfig config, string adapter) =>
        adapter == "embeddings" ? EmbeddingSettings.From(config).Enabled : config.AdapterEnabled(adapter);

    // Whether each adapter of the schema is on in a rhythm.yaml without its section: the example with that section taken out.
    static readonly Lazy<IReadOnlyDictionary<string, bool>> Absent = new(() =>
        Schema["properties"]!["adapters"]!["properties"]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var config = RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"));
            config.Adapters.Remove(p.Key);
            return On(config, p.Key);
        }));
    static bool AbsentOn(string adapter) => Absent.Value[adapter];

    // ---- Reading the schema

    // A setting and its default; an adapter's enabled defaults to its state while its section is absent (AbsentOn).
    sealed record Leaf(string Path, JsonNode Schema, JsonNode? Default);

    // Every setting the schema names, as a dotted path; an adapter's own keys under adapters.<name>, and a plugin's and a
    // provider's under plugins.* and providers.*. An array or a map is one setting.
    static IEnumerable<Leaf> Leaves()
    {
        IEnumerable<Leaf> Walk(string path, JsonNode node)
        {
            if (node["properties"] is JsonObject properties && path != "adapters")
            {
                foreach (var (key, child) in properties)
                    foreach (var leaf in Walk(path.Length == 0 ? key : $"{path}.{key}", child!)) yield return leaf;
            }
            if (path == "adapters")
            {
                foreach (var (name, adapter) in node["properties"]!.AsObject())
                {
                    yield return new($"adapters.{name}.enabled", Schema["$defs"]!["adapter"]!["properties"]!["enabled"]!, JsonValue.Create(AbsentOn(name)));
                    foreach (var (key, child) in adapter!["properties"] as JsonObject ?? []) yield return Setting($"adapters.{name}.{key}", child!);
                }
                yield break;
            }
            if (path is "plugins" or "providers")
            {
                foreach (var (key, child) in node["additionalProperties"]!["properties"]!.AsObject()) yield return Setting($"{path}.*.{key}", child!);
                yield break;
            }
            if (node["properties"] is null) yield return Setting(path, node);
        }
        return Walk("", Schema);
    }
    static Leaf Setting(string path, JsonNode node)
    {
        var resolved = node["$ref"] is { } reference ? Schema["$defs"]![((string)reference!).Split('/')[^1]]! : node;
        return new(path, resolved, node["default"]?.DeepClone() ?? resolved["default"]?.DeepClone());
    }
    // The sections that hold settings, which the page may name as a whole.
    static IEnumerable<string> Containers() =>
        ["adapters.*", "plugins.*", "providers.*", .. Schema["properties"]!["adapters"]!["properties"]!.AsObject().Select(p => $"adapters.{p.Key}")];
    static bool OffOn(JsonNode schema) =>
        schema["enum"] is JsonArray values && values.Any(v => v?.ToString() == "off") && values.Any(v => v?.ToString() == "on");

    // ---- Reading the pages

    sealed record Row(IReadOnlyList<string> Names, IReadOnlyList<string> Cells, string Text);

    // The part of a page from a heading to the next heading of its level or above.
    static string Section(string page, string heading)
    {
        var level = heading.TakeWhile(c => c == '#').Count();
        var start = page.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no section {heading}");
        var rest = page[(start + heading.Length + 2)..];
        var next = Regex.Match(rest, @"\n#{1," + level + @"} ");
        return next.Success ? rest[..next.Index] : rest;
    }

    // Each table row whose first cell names settings: `a.b`, `a.b`, `.c` (c beside b), `people[]`, `<name>` forms as *,
    // and in the adapters table a bare adapter name.
    static List<Row> Rows(string section)
    {
        var rows = new List<Row>();
        var adapters = false;
        foreach (var line in section.Split('\n'))
        {
            // The adapters table, from its header to the first line that is not a row.
            if (!line.StartsWith('|')) { adapters = false; continue; }
            if (line.StartsWith("| Adapter | Default | Keys |", StringComparison.Ordinal)) { adapters = true; continue; }
            if (line.StartsWith("|---", StringComparison.Ordinal)) continue;
            var cells = line.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToList();
            var names = new List<string>();
            string? parent = null;
            foreach (Match m in Code().Matches(cells[0]))
            {
                var name = m.Groups[1].Value;
                if (name.StartsWith('.') && parent is not null) name = parent + name;
                else if (adapters && Regex.IsMatch(name, "^[a-z][a-z0-9-]*$")) name = $"adapters.{name}";
                name = Regex.Replace(name, "<[a-z]+>", "*").Replace("[]", "", StringComparison.Ordinal);
                if (!Regex.IsMatch(name, @"^[a-z][A-Za-z0-9-]*(\.([A-Za-z0-9-]+|\*))*$")) continue;
                if (!name.Contains('.') && !Schema["properties"]!.AsObject().ContainsKey(name)) continue;
                names.Add(name);
                parent = name.Contains('.') ? name[..name.LastIndexOf('.')] : null;
            }
            if (names.Count > 0) rows.Add(new(names, cells, line));
        }
        return rows;
    }

    // Where the page shows a setting: its row's default cell (the setting's own part of it when the row names several), or
    // in the row of a section that holds it, the words after the setting's key up to the next key of that section.
    static string? Shown(List<Row> rows, Leaf leaf)
    {
        foreach (var row in rows)
        {
            var at = row.Names.ToList().IndexOf(leaf.Path);
            if (at < 0 || row.Cells.Count < 2) continue;
            var parts = row.Cells[1].Split(", ");
            return row.Names.Count > 1 && parts.Length == row.Names.Count ? parts[at] : row.Cells[1];
        }
        var parent = leaf.Path[..leaf.Path.LastIndexOf('.')];
        var key = leaf.Path[(leaf.Path.LastIndexOf('.') + 1)..];
        foreach (var row in rows.Where(r => r.Names.Contains(parent)))
        {
            if (key == "enabled") return row.Cells[1];
            var text = string.Join(" | ", row.Cells.Skip(1));
            var mention = Regex.Match(text, $"`{Regex.Escape(key)}`");
            if (!mention.Success) continue;
            var siblings = Leaves().Where(l => l.Path.StartsWith(parent + ".", StringComparison.Ordinal)).Select(l => l.Path[(parent.Length + 1)..]).ToHashSet();
            var after = text[(mention.Index + mention.Length)..];
            var next = Code().Matches(after).FirstOrDefault(m => siblings.Contains(m.Groups[1].Value));
            return next is null ? after : after[..next.Index];
        }
        return null;
    }

    // Whether words show a default: a string or number as written, true or false, null or an empty list as null or none,
    // and every item of a list.
    static bool Gives(string words, JsonNode value) => value switch
    {
        JsonArray { Count: 0 } => words.Contains("none", StringComparison.Ordinal) || words.Contains("[]", StringComparison.Ordinal),
        JsonArray items => items.All(i => Gives(words, i!)),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => words.Contains(v.GetValue<string>(), StringComparison.Ordinal),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => Regex.IsMatch(words, $@"\b{(v.GetValue<bool>() ? "true" : "false")}\b"),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
            Regex.IsMatch(words, $@"(?<![0-9.]){Regex.Escape(v.GetValue<double>().ToString(CultureInfo.InvariantCulture))}(?![0-9])"),
        _ => false,
    };

    [GeneratedRegex("`([^`]+)`")] private static partial Regex Code();

    // ---- YAML as the engine reads it, as JSON

    // A YAML file as JSON, with YAML 1.2's core rules for plain scalars: null, true and false, integers and decimals; any
    // other plain scalar (off, on, a name) and every quoted one is a string, as the engine's settings take them.
    static JsonNode? Yaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return Json(stream.Documents[0].RootNode);
    }
    static JsonNode? Json(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, Json(c.Value)))),
        YamlSequenceNode list => new JsonArray([.. list.Children.Select(Json)]),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new InvalidOperationException("an alias or an unknown YAML node"),
    };
    static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any)) return JsonValue.Create(text);
        if (text is "" or "~" or "null" or "Null" or "NULL") return null;
        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (Regex.IsMatch(text, "^[-+]?[0-9]+$")) return JsonValue.Create(long.Parse(text, CultureInfo.InvariantCulture));
        if (Regex.IsMatch(text, @"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$"))
            return JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture));
        return JsonValue.Create(text);
    }
}
