using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Knowledge.Cli;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Knowledge.Tests;

// The per-token write limit (SB-P7-12, Mcp/WriteLimit.cs, docs/security.md, ledger SEC-18) with engine.writeLimit on: at and
// past the limit, per token, the window ending, reads untouched, the app and the terminal never counted, and normal use (the
// corpus, the end-to-end scripts) never near it. Off, absent or empty, nothing is counted and every tool answers as before.
// Fictional people only; every text is DEMO.
static class WriteLimits
{
    public static RhythmConfig With(string value, string memory = "tools: on, capture: on") =>
        RhythmConfig.Parse(TestSettings.Yaml.Replace("engine: { legacySearch: false }", "engine: { legacySearch: false, writeLimit: " + value + " }")
            + "\nmemory: { " + memory + " }\n");
    public static RhythmConfig On => With("on");

    public sealed class Clock
    {
        public TimeSpan Now = TimeSpan.FromHours(1);
        public TimeSpan Read() => Now;
    }

    // As many writes as asked for, none of them refused.
    public static void Take(WriteLimit limit, Guid token, int writes = WriteLimit.Writes)
    {
        for (var i = 0; i < writes; i++) Assert.Null(limit.Take(token));
    }

    // The links of a memory a token makes with link. The corpus's supersedes links are its replacements, which supersede makes
    // (link takes no such type).
    public static ParityLink[] Linked(ParityMemory memory) => [.. memory.Links.Where(l => l.Type != "supersedes")];

    // Every counted call the whole corpus takes if one token writes all of it in one sitting: each memory, its links, its
    // confirmation and its replacement, which is two (supersede's plan and then its confirmation, neither marked read only),
    // and each document, dashboard, insight and run.
    public static int WholeCorpus(ParityCorpus corpus) =>
        corpus.Memories.Length + corpus.Memories.Sum(m => Linked(m).Length) + corpus.Memories.Count(m => m.Status == "canonical")
        + 2 * corpus.Memories.Count(m => m.ReplacedBy is not null) + corpus.Documents.Length + corpus.Dashboards.Length + corpus.Insights.Length + corpus.InsightRuns.Length;

    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    public const HttpStatusCode TooMany = (HttpStatusCode)429;

    // Every tool an install can list, with every setting that makes one on, and every harness tool a known adapter adds while
    // it is on (none today), so a tool an adapter brings is classified here too. A tool's words read the settings in force,
    // so they are loaded first, and a test that lists them runs alone too.
    public static IReadOnlyList<ModelContextProtocol.Server.McpServerTool> Tools()
    {
        TestSettings.Use();
        return HarnessTools.Create(RhythmConfig.Parse(TestSettings.Yaml.Replace("engine: { legacySearch: false }", "engine: { legacySearch: false, recall: on, writeLimit: on, legacyToolNames: true }")
            + "\nmemory: { tools: on, capsules: on, share: on, capture: on }\n"), AdapterTools());
    }
    public static ModelContextProtocol.Server.McpServerTool[] AdapterTools() =>
        [.. Knowledge.Adapters.AdapterCatalog.Known.SelectMany(make => make().HarnessTools())];

    // cli/journeys.sh's writes by token (its lines, comments left out), from the step that turns the limit on; what runs
    // before it is never counted, the journeys with every memory setting off among it. The corpus's Family documents its AI
    // client saves, one call each; each call of a shell function of its own, as many as the function's calls; and each journey
    // it runs, each time it runs it (JourneyWrites). A token is named by its secret's file: harness_<name> for an AI client,
    // plugin_<name> for a plugin.
    public static Dictionary<string, int> JourneysWrites(string[] script, IReadOnlySet<string> writes)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string token, int n) => counts[token] = counts.GetValueOrDefault(token) + n;
        var on = Array.FindIndex(script, l => l.Contains(@"/^engine:/a\  writeLimit: on", StringComparison.Ordinal));
        Assert.True(on >= 0, "cli/journeys.sh turns the write limit on");
        // A function's calls through mcp_call, each counted as a write whatever it calls, so the count is never short.
        var functions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? body = null;
        foreach (var line in script)
            if (Regex.Match(line, @"^(\w+)\(\) \{$") is { Success: true } named) functions[named.Groups[1].Value] = body = [];
            else if (line == "}") body = null;
            else if (body is not null && Regex.Match(line, @"mcp_call \$secrets/([\w-]+)_token") is { Success: true } call) body.Add(call.Groups[1].Value);
        var documents = ParityCorpus.Load().Documents.Count(d => d.Scope == "family");
        for (var i = on; i < script.Length; i++)
            if (Regex.Match(script[i], @"^journey ([\w-]+)$") is { Success: true } journey)
            {
                var file = $"app/tests/journeys/{journey.Groups[1].Value}.test.tsx";
                foreach (var (token, n) in JourneyWrites(File.ReadAllText(Path.Combine(Root, file)), writes, file)) Add(token, n);
            }
            else if (script[i].Contains(@"""name"":""save_document""", StringComparison.Ordinal))
            {
                // Inside the loop over the lines cli/journeys-corpus.mjs gives: Family's documents in the corpus.
                Assert.Contains(script[on..i], l => l.StartsWith("node cli/journeys-corpus.mjs documents | {", StringComparison.Ordinal));
                Add(Regex.Match(script[i], @"mcp_call \$secrets/([\w-]+)_token").Groups[1].Value, documents);
            }
            else
            {
                foreach (Match word in Regex.Matches(script[i], @"\w+"))
                    if (functions.TryGetValue(word.Value, out var calls)) foreach (var token in calls) Add(token, 1);
                // Any other call to the engine's /mcp would be one this count missed.
                Assert.DoesNotMatch(@"mcp_call|/mcp", script[i]);
            }
        return counts;
    }

    // One journey's writes by token (a file of app/tests/journeys/, formatted as the app's code is, so the lines a line is
    // inside are the nearer ones less indented): each aiClient("<name>", "<tool>") call of a tool not marked read only, and
    // each plugin("<name>", ...) call and rpc(tokenOf("<file>"), "tools/call") call, every one a write. A call counts as many
    // times as the literal lists of the describe.each and for loops it is inside say. A call written another way, or one inside
    // any other loop, a map or a retry (eventually), fails the test, since its count cannot be read: the count is never short.
    public static Dictionary<string, int> JourneyWrites(string text, IReadOnlySet<string> writes, string file)
    {
        var lines = text.Split('\n');
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        static int Indent(string line) => line.Length - line.TrimStart().Length;
        static bool Code(string line) => line.Trim() is { Length: > 0 } code && !code.StartsWith("//", StringComparison.Ordinal) && !code.StartsWith('*');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Code(lines[i])) continue;
            foreach (Match call in Regex.Matches(lines[i], @"\b(aiClient|plugin|rpc)\("))
            {
                var form = Regex.Match(lines[i][call.Index..], call.Groups[1].Value == "rpc" ? @"^rpc\(tokenOf\(""([\w-]+)""\), ""([\w/]+)""" : @"^\w+\(""([\w-]+)"", ""(\w+)""");
                Assert.True(form.Success, $"{file}:{i + 1}: a call this test cannot read: {lines[i].Trim()}");
                var (name, what) = (form.Groups[1].Value, form.Groups[2].Value);
                var (token, write) = call.Groups[1].Value switch
                {
                    "aiClient" => ("harness_" + name, writes.Contains(what)),
                    "plugin" => ("plugin_" + name, true),
                    _ => (name, what == "tools/call"),
                };
                if (!write) continue;
                var times = Times(lines[i][..call.Index], file, i);
                for (int j = i - 1, indent = Indent(lines[i]); j >= 0 && indent > 0; j--)
                    if (Code(lines[j]) && Indent(lines[j]) < indent) { indent = Indent(lines[j]); times *= Times(lines[j], file, j); }
                counts[token] = counts.GetValueOrDefault(token) + times;
            }
        }
        return counts;
    }

    // How many times what a line opens runs: the length of a describe.each's or a for's literal list, else once; a failed
    // test for any other loop or a retry.
    static int Times(string line, string file, int at)
    {
        if (Regex.Match(line, @"\b(?:describe|it|test)\.each\(\[([^\]]*)\]|\bfor \((?:const|let) \w+ of \[([^\]]*)\]\)") is { Success: true } list)
            return (list.Groups[1].Success ? list.Groups[1] : list.Groups[2]).Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        Assert.False(Regex.IsMatch(line, @"\b(?:for|while)\s*\(|\bdo\s*\{|\.(?:each|map|flatMap|forEach|filter|some|every|reduce)\(|\beventually\("),
            $"{file}:{at + 1}: a write inside a loop or a retry whose count this test cannot read: {line.Trim()}");
        return 1;
    }
}

public sealed class WriteLimitTests
{
    [Fact] public void A_token_writes_up_to_its_limit_and_the_next_is_told_how_long_to_wait()
    {
        var clock = new WriteLimits.Clock();
        var limit = new WriteLimit(clock.Read);
        var token = Guid.NewGuid();
        for (var i = 0; i < WriteLimit.Writes; i++)
        {
            Assert.Null(limit.Take(token));
            clock.Now += TimeSpan.FromSeconds(1);
        }
        // Past it: the first of them is that many seconds old, so it leaves the window in the rest of the ten minutes.
        var wait = limit.Take(token);
        Assert.Equal(WriteLimit.Window - TimeSpan.FromSeconds(WriteLimit.Writes), wait);
        var words = WriteLimit.Message(wait!.Value);
        Assert.Contains($"{WriteLimit.Writes} writes in the last {WriteLimit.Window.TotalMinutes:0} minutes", words);
        Assert.Contains("changed nothing", words);
        Assert.Contains($"{(WriteLimit.Window - TimeSpan.FromSeconds(WriteLimit.Writes)).TotalSeconds:0} seconds", words);
        Assert.Contains("reads still answer", words);
        Assert.Contains("again in 1 second;", WriteLimit.Message(TimeSpan.FromMilliseconds(1)));
    }

    [Fact] public void Each_token_has_its_own_count()
    {
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        var (busy, other) = (Guid.NewGuid(), Guid.NewGuid());
        WriteLimits.Take(limit, busy);
        Assert.NotNull(limit.Take(busy));
        WriteLimits.Take(limit, other);
        Assert.NotNull(limit.Take(other));
    }

    // The window slides: what was written more than ten minutes ago no longer counts, and what is still inside it does. A
    // refused call changes nothing, so asking again and again ends at the same moment as waiting.
    [Fact] public void When_the_window_ends_what_left_it_is_free_and_no_more()
    {
        var clock = new WriteLimits.Clock();
        var limit = new WriteLimit(clock.Read);
        var token = Guid.NewGuid();
        var half = WriteLimit.Writes / 2;
        WriteLimits.Take(limit, token, half);
        clock.Now += TimeSpan.FromMinutes(5);
        WriteLimits.Take(limit, token, half);
        Assert.Equal(TimeSpan.FromMinutes(5), limit.Take(token));

        clock.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        for (var i = 0; i < 1000; i++) Assert.Equal(TimeSpan.FromSeconds(1), limit.Take(token));

        clock.Now += TimeSpan.FromSeconds(1);
        WriteLimits.Take(limit, token, half);
        Assert.Equal(TimeSpan.FromMinutes(5), limit.Take(token));

        clock.Now += TimeSpan.FromMinutes(20);
        WriteLimits.Take(limit, token);
    }

    [Fact] public async Task Many_calls_at_once_get_exactly_the_limit()
    {
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        var token = Guid.NewGuid();
        var refused = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, WriteLimit.Writes * 3), (_, _) =>
        {
            if (limit.Take(token) is not null) Interlocked.Increment(ref refused);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(WriteLimit.Writes * 2, refused);
    }

    // Absent, empty or off counts nothing and leaves no service to ask; on makes one; anything else stops the engine at
    // start naming the key. The settings schema takes the key, so a rhythm.yaml that turns it on still validates.
    [Fact] public void The_limit_is_off_unless_it_says_on()
    {
        Assert.False(WriteLimitSettings.On(TestSettings.Use()));
        Assert.False(WriteLimitSettings.On(WriteLimits.With("off")));
        Assert.False(WriteLimitSettings.On(WriteLimits.With("''")));
        Assert.True(WriteLimitSettings.On(WriteLimits.On));
        foreach (var value in new[] { "yes", "true", "300" })
            Assert.Contains("engine.writeLimit is off or on", Assert.Throws<ConfigException>(() => WriteLimits.With(value)).Message);
        TestSettings.Use();
        bool Serves(RhythmConfig config)
        {
            var services = new ServiceCollection();
            Engine.Mcp(services, config, []);
            using var built = services.BuildServiceProvider();
            return WriteLimit.Of(built) is not null;
        }
        Assert.False(Serves(TestSettings.Use()));
        Assert.False(Serves(WriteLimits.With("off")));
        Assert.True(Serves(WriteLimits.On));
        TestSettings.Use();
        var key = JsonDocument.Parse(File.ReadAllText(Path.Combine(WriteLimits.Root, "contracts", "rhythm.schema.json"))).RootElement
            .GetProperty("properties").GetProperty("engine").GetProperty("properties").GetProperty("writeLimit");
        Assert.Equal(["off", "on", null], key.GetProperty("enum").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()));
        Assert.Equal("off", key.GetProperty("default").GetString());
    }

    // D-225: a CI job past 40 writes in ten minutes gets its next writes held. The limit stands behind that rule on its window,
    // so a burst is still seen and held for a person before anything is refused, and a token that runs on is stopped.
    [Fact] public void It_stands_behind_the_burst_rule_on_the_same_window()
    {
        Assert.Equal(MemoryHolds.BurstWindow, WriteLimit.Window);
        Assert.True(WriteLimit.Writes > MemoryHolds.BurstLimit * 2, "the limit must leave room to see a burst held first");
    }

    // A tool counts as a write unless it says it is read only, so a tool nobody marked counts. Both lists are pinned whole, an
    // adapter's tools among them, so a new tool, or one that changes its mind, is looked at here: one marked read only that
    // writes would go past the limit uncounted. Each list says plainly what a token past its limit can do.
    [Fact] public void Every_tool_says_whether_it_writes_and_the_lists_are_the_contracts()
    {
        var tools = WriteLimits.Tools();
        foreach (var t in tools) Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint is not null, $"{t.ProtocolTool.Name} does not say whether it writes");
        string[] Names(bool readOnly) => [.. tools.Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint == readOnly).Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] {
            "append", "cite", "control_screen", "delete_dashboard", "forget", "invoke_dashboard_action", "link", "log_insight_run", "propose_contradiction", "record_outcome", "remember",
            "resolve", "resolve_due", "resolve_insight_candidate", "resolve_loop", "revalidate", "review_insight", "save_dashboard", "save_digest", "save_document",
            "save_household_document", "save_insight", "set_status", "share", "still_true", "stop_standing", "studio_dashboard", "supersede", "undo", "unlink", "update",
        }, Names(false));
        Assert.Equal(new[] {
            "brief", "compare_health_trends", "connections", "dashboard_data", "dashboard_history", "decision_calibration", "digest", "digest_sources",
            "discover_dashboard_controls", "discover_health_sources", "evaluate_health_hypothesis", "explore_graph", "get", "get_prompt_capsule", "get_related_resources",
            "get_relationship_graph", "get_resource", "get_sync_status", "graph_changes", "history", "insight_history", "investigate_health",
            "list_contradictions", "list_dashboards", "list_decisions", "list_due", "list_duplicates", "list_insight_candidates", "list_insight_stream",
            "list_insights", "list_loops", "list_projects", "list_recent", "list_screens", "list_stale", "list_standing", "list_teams",
            "query_health_metrics", "read_health_source", "recall", "search_knowledge", "search_resources", "semantic_search",
        }, Names(true));
        // No known adapter adds a harness tool today; one that does joins the lists above.
        Assert.Empty(WriteLimits.AdapterTools());
    }

    // Past its limit a tool nobody marked is refused, as one marked as a write is; only one that says it is read only goes on.
    [Fact] public void A_tool_that_says_nothing_counts()
    {
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        var token = Guid.NewGuid();
        WriteLimits.Take(limit, token);
        var context = new DefaultHttpContext();
        context.Items["principal"] = token;
        var services = new ServiceCollection().AddSingleton(limit).AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context }).BuildServiceProvider();
        foreach (var unmarked in new[] { new Tool { Name = "x" }, new Tool { Name = "x", Annotations = new() }, new Tool { Name = "x", Annotations = new() { ReadOnlyHint = false } } })
            Assert.True(WriteLimit.Refused(unmarked, services)?.IsError);
        Assert.Null(WriteLimit.Refused(new Tool { Name = "x", Annotations = new() { ReadOnlyHint = true } }, services));
    }

    // What the limit was measured against. The synthetic corpus: the busiest person's busiest day, and every write it takes
    // if one token writes all of it in one sitting (WholeCorpus, driven for real by The_whole_synthetic_corpus_through_one_token_is_never_refused).
    // The docs' numbers come from here (docs/security.md).
    [Fact] public void The_whole_synthetic_corpus_through_one_token_stays_under_it()
    {
        var corpus = ParityCorpus.Load();
        var busiestDay = corpus.Memories.Where(m => m.Author is not null).GroupBy(m => (m.Author, m.CreatedAt.Date)).Max(g => g.Count());
        var whole = WriteLimits.WholeCorpus(corpus);
        Assert.Equal(corpus.Memories.Count(m => m.ReplacedBy is not null), corpus.Memories.Sum(m => m.Links.Count(l => l.Type == "supersedes")));
        Assert.True(busiestDay * 50 < WriteLimit.Writes, $"the corpus's busiest day is {busiestDay} writes");
        Assert.True(whole < WriteLimit.Writes, $"the whole corpus through one token is {whole} writes");
    }

    // The end-to-end runs unaffected: cli/e2e.sh, cli/lifecycle.sh and cli/journeys.sh run with the setting on (each says so),
    // and in each run every write it makes, whichever token makes it, adds up to far less than one token's limit. The journeys
    // are the run closest to a person's own use: the app and their AI clients (WriteLimits.JourneysWrites). Each run's busiest
    // token is the count docs/security.md gives it, and the ratio it gives is the limit over the busiest of them.
    [Fact] public void The_end_to_end_scripts_stay_far_under_it()
    {
        string Read(string file) => File.ReadAllText(Path.Combine(WriteLimits.Root, "cli", file));
        string[] Lines(string text) => [.. text.Split('\n').Where(l => !l.TrimStart().StartsWith('#'))];
        static Dictionary<string, int> ByToken(IEnumerable<string> tokens) =>
            tokens.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var writes = WriteLimits.Tools().Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint != true).Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var e2e = Lines(Read("e2e.sh"));
        var lifecycle = Lines(Read("lifecycle.sh"));
        var journeys = Lines(Read("journeys.sh"));
        var runs = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
        {
            ["cli/e2e.sh"] = ByToken(e2e.SelectMany(l => Regex.Matches(l, @"(?:^|[\s(])(?:save|capture) +""?(\$[\w/$-]+)").Select(m => m.Groups[1].Value))),
            ["cli/lifecycle.sh"] = ByToken(lifecycle.SelectMany(l => Regex.Matches(l, @"call ""(\$\w+)"" (\w+)").Where(m => writes.Contains(m.Groups[2].Value)).Select(m => m.Groups[1].Value))),
            ["cli/journeys.sh"] = WriteLimits.JourneysWrites(journeys, writes),
        };
        string Said(string run) => $"{run}: {string.Join(", ", runs[run])}";
        Assert.True(runs["cli/e2e.sh"].Values.Sum() + runs["cli/lifecycle.sh"].Values.Sum() >= 20, $"these scripts read as this test expects: {Said("cli/e2e.sh")}; {Said("cli/lifecycle.sh")}");
        Assert.True(runs["cli/journeys.sh"].Values.Sum() >= 20, $"the journeys read as this test expects: {Said("cli/journeys.sh")}");
        Assert.Equal("harness_journey-alex", runs["cli/journeys.sh"].MaxBy(t => t.Value).Key);
        var security = File.ReadAllLines(Path.Combine(WriteLimits.Root, "docs", "security.md"));
        foreach (var (run, byToken) in runs)
        {
            Assert.True(byToken.Values.Sum() * 5 < WriteLimit.Writes, $"the end-to-end scripts' writes: {Said(run)}");
            Assert.EndsWith($"| {byToken.Values.Max()} writes |", Assert.Single(security, l => l.StartsWith($"| `{run}`", StringComparison.Ordinal)));
        }
        var busiest = runs.Values.Max(r => r.Values.Max());
        Assert.Contains(security, l => l.Contains($"about {WriteLimit.Writes / busiest} times the busiest token of the end-to-end runs", StringComparison.Ordinal));
        // And the scripts do run with it on, so a run says so for real.
        Assert.Contains(e2e, l => l.Contains(@"/^engine:/a\  writeLimit: on", StringComparison.Ordinal));
        Assert.Contains(lifecycle, l => l.Contains(@"/^engine:/a\  writeLimit: on", StringComparison.Ordinal));
        Assert.Contains(journeys, l => l.Contains(@"/^engine:/a\  writeLimit: on", StringComparison.Ordinal));
    }

    // The numbers an operator reads are the code's.
    [Fact] public void The_docs_say_the_codes_numbers()
    {
        var words = $"{WriteLimit.Writes} writes in {WriteLimit.Window.TotalMinutes:0} minutes";
        foreach (var doc in new[] { "docs/security.md", "docs/configuration.md", "contracts/README.md" })
            Assert.Contains(words, File.ReadAllText(Path.Combine(WriteLimits.Root, doc)));
    }

    // The setting stands on its own: with memory.tools off, as on every deployed install, it limits the tools those installs
    // have, and until it is set they answer as today (every other test of them runs with no limit in the services).
    [Fact] public async Task With_memory_tools_off_it_limits_the_tools_a_deployed_install_has()
    {
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        var config = WriteLimits.With("on", "tools: off");
        Assert.False(MemorySettings.ToolsOn(config));
        await using var engine = await McpEngine.Start(config, limit: limit);
        WriteLimits.Take(limit, McpEngine.AssistantCaller.Id);
        await using var ai = await McpEngine.Connect(engine.Handler());
        var args = new Dictionary<string, object?> { ["outcome"] = "quiet" };
        var refused = await ai.CallToolAsync("log_insight_run", args);
        Assert.True(refused.IsError);
        Assert.StartsWith("This token has made", refused.Content.OfType<TextContentBlock>().Single().Text);
        // A read goes on to whatever it meets (here no database), never to the limit.
        var read = await ai.CallToolAsync("get_sync_status", new Dictionary<string, object?>());
        Assert.DoesNotContain("This token has made", string.Join(" ", read.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        // A memory tool is an unknown tool as before: it does not exist, so no limit answers for it.
        var unknown = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(() => ai.CallToolAsync("remember", new Dictionary<string, object?> { ["content"] = "DEMO" }).AsTask());
        Assert.DoesNotContain("This token has made", unknown.Message);
    }

    // A refusal is one line in the engine's log, fixed fields only: the role, the tool, the status and code.
    [Fact] public async Task A_refusal_at_a_door_is_a_line_in_the_log_and_says_when_to_try_again()
    {
        var log = new LogLines();
        var clock = new WriteLimits.Clock();
        var limit = new WriteLimit(clock.Read);
        var caller = new Caller(Guid.NewGuid(), "ci", "capture", null);
        WriteLimits.Take(limit, caller.Id);
        clock.Now += TimeSpan.FromSeconds(0.5);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(new RequestLog(log)).AddSingleton(limit).BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        Assert.True(await WriteLimit.Refused(context, caller, "capture"));
        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("600", context.Response.Headers.RetryAfter.ToString());
        Assert.Contains("capture capture, 429 write_limit", Assert.Single(log.Lines));

        // With no limit in the services a door goes on, and nothing is answered.
        var off = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        Assert.False(await WriteLimit.Refused(off, caller, "capture"));
        Assert.Equal(200, off.Response.StatusCode);
    }

    // A harness tool's refusal is the same line, through the real MCP server, whichever shape the tool answers in; a read is none.
    [Fact] public async Task A_refusal_of_a_harness_tool_is_a_line_in_the_log()
    {
        var log = new LogLines();
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        await using var engine = await McpEngine.Start(WriteLimits.On, limit: limit, more: s => s.AddSingleton(new RequestLog(log)));
        WriteLimits.Take(limit, McpEngine.AssistantCaller.Id);
        await using var ai = await McpEngine.Connect(engine.Handler());
        Assert.True((await ai.CallToolAsync("log_insight_run", new Dictionary<string, object?> { ["outcome"] = "quiet" })).IsError);
        Assert.True((await ai.CallToolAsync("remember", new Dictionary<string, object?> { ["content"] = "DEMO: a note past the limit." })).IsError);
        await ai.CallToolAsync("get_sync_status", new Dictionary<string, object?>());
        Assert.Contains(log.Lines, l => l.Contains("harness log_insight_run, 429 write_limit", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("harness remember, 429 write_limit", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("get_sync_status", StringComparison.Ordinal) && l.Contains(WriteLimit.Code, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("DEMO", StringComparison.Ordinal));
    }

    // A capture asks before it reads its body: under the limit a body that cannot be saved takes a slot and answers as ever,
    // and past the limit the same body is not read at all, and answers 429 rather than 400.
    [Fact] public async Task A_capture_is_counted_whatever_it_holds_and_refused_before_its_body_is_read()
    {
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        var caller = new Caller(Guid.NewGuid(), "ci", "capture", null);
        async Task<(int Status, long Read)> Send()
        {
            var body = new MemoryStream(Encoding.UTF8.GetBytes("this is not one JSON object"));
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(limit).BuildServiceProvider() };
            context.Request.Method = "POST";
            context.Request.Body = body;
            context.Response.Body = new MemoryStream();
            await CaptureEndpoint.Handle(context, caller, null!);
            return (context.Response.StatusCode, body.Position);
        }
        WriteLimits.Take(limit, caller.Id, WriteLimit.Writes - 1);
        var last = await Send();
        Assert.Equal(400, last.Status);
        Assert.True(last.Read > 0);
        Assert.Equal((429, 0L), await Send());
    }
}

// A plugin's calls, on the door and endpoint Program builds, without a database: a refused call is answered before one.
public sealed class WriteLimitIngestDoorTests
{
    static string Call(string tool, object arguments) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "w", method = "tools/call", @params = new { name = tool, arguments } });
    const string Plugin = "Bearer " + McpEngine.Plugin;

    [Fact] public async Task A_plugin_past_its_limit_is_told_so_and_its_handshake_still_answers()
    {
        var clock = new WriteLimits.Clock();
        var limit = new WriteLimit(clock.Read);
        await using var engine = await McpEngine.Start(WriteLimits.On, limit: limit);
        WriteLimits.Take(limit, engine.Tokens[McpEngine.Plugin].Id);

        foreach (var tool in new[] { IngestEndpoint.Measurements, IngestEndpoint.Records })
        {
            var (status, body, response) = await engine.Send("POST", "/mcp", Call(tool, new { }), Plugin);
            Assert.Equal(WriteLimits.TooMany, status);
            Assert.Equal("w", (string)body!["id"]!);
            Assert.True((bool)body["result"]!["isError"]!);
            Assert.Equal(WriteLimit.Code, (string)body["result"]!["structuredContent"]!["error"]!["code"]!);
            Assert.Contains("changed nothing", (string)body["result"]!["structuredContent"]!["error"]!["message"]!);
            Assert.Equal("600", response.Headers.RetryAfter?.ToString());
        }
        // What is no write answers as ever, and a call that is not one is refused as ever, counting nothing.
        Assert.Equal(HttpStatusCode.OK, (await engine.Send("POST", "/mcp", McpEngine.Initialize("2025-03-26"), Plugin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await engine.Send("POST", "/mcp", """{"jsonrpc":"2.0","id":"p","method":"ping"}""", Plugin)).Status);
        var listed = await engine.Send("POST", "/mcp", """{"jsonrpc":"2.0","id":"l","method":"tools/list"}""", Plugin);
        Assert.Equal(HttpStatusCode.OK, listed.Status);
        Assert.Equal(2, listed.Body!["result"]!["tools"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await engine.Send("POST", "/mcp", Call("remember", new { }), Plugin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await engine.Send("POST", "/mcp", "{}", Plugin)).Status);

        // The window ending: its next call goes on past the limit, to whatever the call meets (here the store's own check).
        clock.Now += WriteLimit.Window;
        Assert.NotEqual(WriteLimits.TooMany, (await engine.Send("POST", "/mcp", Call(IngestEndpoint.Measurements, new { }), Plugin)).Status);

        // Unlike a harness call, an ingest call whose arguments do not fit takes a slot: it counts whatever it holds.
        WriteLimits.Take(limit, engine.Tokens[McpEngine.Plugin].Id, WriteLimit.Writes - 2);
        var unfit = await engine.Send("POST", "/mcp", Call(IngestEndpoint.Measurements, new { }), Plugin);
        Assert.Equal(HttpStatusCode.OK, unfit.Status);
        Assert.Equal("invalid_arguments", (string)unfit.Body!["result"]!["structuredContent"]!["error"]!["code"]!);
        Assert.Equal(WriteLimits.TooMany, (await engine.Send("POST", "/mcp", Call(IngestEndpoint.Measurements, new { }), Plugin)).Status);
    }
}

public sealed class WriteLimitDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The door and capture route as Program builds them, with a limit in the services or none.
    sealed class Door : IAsyncDisposable
    {
        WebApplication app = null!;
        HttpClient client = null!;

        public static async Task<Door> Open(DatabaseFixture fixture, RhythmConfig config, WriteLimit? limit)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            if (limit is not null) builder.Services.AddSingleton(limit);
            var app = builder.Build();
            app.Use(new Perimeter(["engine"], () => true, fixture.Db.Authenticate, new(App: (c, _) => c.Response.WriteAsync("app"), Ingest: (c, _) => c.Response.WriteAsync("ingest"),
                Document: (c, _, _) => c.Response.WriteAsync("document"), Capture: Engine.CaptureRoute(config, fixture.Db))).Handle);
            await app.StartAsync();
            return new Door { app = app, client = app.GetTestClient() };
        }

        public async Task<(HttpStatusCode Status, string? RetryAfter, JsonElement Body)> Capture(string token, string text)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://engine/capture") { Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, response.Headers.RetryAfter?.ToString(), JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }

    static string Code(JsonElement body) => body.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString()!;
    Task<object?> Scalar(string sql, params (string, object?)[] args) => MemoryRows.Scalar(fixture.Source, sql, args);
    Task<object?> Memories() => Scalar("SELECT count(*) FROM memories");
    Task<object?> Events() => Scalar("SELECT count(*) FROM memory_events");

    async Task<(Guid Id, string Token)> Connect(string name, string? person = null)
    {
        var file = Path.Combine(Path.GetTempPath(), $"limit-test-{Guid.NewGuid():N}");
        try
        {
            var id = await new Principals(fixture.Source).CreateCapture(name, person, file, default);
            return (id, await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }

    async Task<(Guid Id, string Token)> Client(string name, string? person = null)
    {
        var file = Path.Combine(Path.GetTempPath(), $"limit-test-{Guid.NewGuid():N}");
        try
        {
            var id = await new Principals(fixture.Source).Create(name, "harness", null, person, file, default);
            return (id, await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }

    async Task Off()
    {
        await Scoped.Mode(fixture.Source, "off");
        await MemoryLogins.Family(fixture.Source);
    }

    static async Task<(bool Error, string Text)> Tool(McpClient client, string tool, params (string Name, object? Value)[] arguments)
    {
        var answer = await client.CallToolAsync(tool, arguments.ToDictionary(a => a.Name, a => a.Value));
        return (answer.IsError == true, string.Join(" ", answer.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    // D-225's CI job, end to end through the door: its first 40 writes in ten minutes save as written, the next are saved but
    // held for a person, and the one past the limit is refused and saves nothing; when the window ends it writes again.
    [PostgresFact] public async Task A_capture_past_the_burst_is_held_and_past_the_limit_is_refused_until_the_window_ends()
    {
        await Off();
        var clock = new WriteLimits.Clock();
        await using var door = await Door.Open(fixture, WriteLimits.On, new WriteLimit(clock.Read));
        var (_, busy) = await Connect("limit-busy");
        var (_, other) = await Connect("limit-other");

        var held = new List<bool>();
        for (var i = 0; i < WriteLimit.Writes; i++)
        {
            var (status, _, reply) = await door.Capture(busy, $"DEMO: limit note {i}.");
            Assert.Equal(HttpStatusCode.OK, status);
            held.Add(reply.GetProperty("held").GetBoolean());
        }
        Assert.Equal(MemoryHolds.BurstLimit, held.TakeWhile(h => !h).Count());
        Assert.All(held.Skip(MemoryHolds.BurstLimit), Assert.True);

        // At the limit the next is refused, plainly, and saves nothing: no memory, no audit row.
        var (memories, events) = (await Memories(), await Events());
        var refused = await door.Capture(busy, "DEMO: limit note past it.");
        Assert.Equal(WriteLimits.TooMany, refused.Status);
        Assert.Equal(WriteLimit.Code, Code(refused.Body));
        Assert.Contains("changed nothing", refused.Body.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("600", refused.RetryAfter);
        Assert.Equal((memories, events), (await Memories(), await Events()));

        // Another token is another count.
        Assert.Equal(HttpStatusCode.OK, (await door.Capture(other, "DEMO: another connection's note.")).Status);

        // The window ending: it writes again, and the database's burst rule, on its own clock, still holds it.
        clock.Now += WriteLimit.Window;
        var again = await door.Capture(busy, "DEMO: limit note after the window.");
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.True(again.Body.GetProperty("held").GetBoolean());
    }

    // An AI client, through the real MCP server: every write refused past its limit, in the shape its tool's refusals have;
    // every read answering; tools/list as it was; another client's count its own; and the window ending.
    [PostgresFact] public async Task An_AI_client_past_its_limit_writes_nothing_and_reads_as_ever()
    {
        await Off();
        var config = WriteLimits.On;
        var clock = new WriteLimits.Clock();
        var limit = new WriteLimit(clock.Read);
        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config), limit,
            s => s.AddSingleton(new InsightStore(fixture.Source)));
        var (alex, token) = await Client("limit-alex", "alex");
        var (_, otherToken) = await Client("limit-sam", "sam");
        await using var ai = await McpEngine.Connect(engine.Handler(), "Bearer " + token, "2025-06-18");
        await using var other = await McpEngine.Connect(engine.Handler(), "Bearer " + otherToken, "2025-06-18");

        var made = await Tool(ai, "remember", ("content", "DEMO: the first note of the limit's test."));
        Assert.False(made.Error, made.Text);
        var id = Guid.Parse((string)JsonNode.Parse(made.Text)!["id"]!);
        var logged = await Tool(ai, "log_insight_run", ("outcome", "quiet"));
        Assert.False(logged.Error, logged.Text);

        // Up to the limit: its last write is saved like any other.
        WriteLimits.Take(limit, alex, WriteLimit.Writes - 3);
        var last = await Tool(ai, "remember", ("content", "DEMO: the last note the limit lets through."));
        Assert.False(last.Error, last.Text);

        async Task<(object?, object?, object?)> Counts() => (await Memories(), await Events(), await Scalar("SELECT count(*) FROM insight_runs"));
        var before = await Counts();

        // Past it, a memory tool's refusal is its usual {ok, code, error}.
        foreach (var (tool, arguments) in new (string, (string, object?)[])[]
        {
            ("remember", [("content", "DEMO: a note past the limit.")]),
            ("append", [("id", id), ("addition", "DEMO: more past the limit.")]),
            ("update", [("id", id), ("content", "DEMO: changed past the limit.")]),
            ("set_status", [("id", id), ("status", "canonical")]),
            ("forget", [("id", id)]),
            ("supersede", [("id", id), ("replaces", Guid.NewGuid())]),
        })
        {
            var (error, text) = await Tool(ai, tool, arguments);
            Assert.True(error, tool);
            var answer = JsonNode.Parse(text)!.AsObject();
            Assert.Equal(new[] { "code", "error", "ok" }, answer.Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.False((bool)answer["ok"]!);
            Assert.Equal(WriteLimit.Code, (string)answer["code"]!);
            Assert.Contains("changed nothing", (string)answer["error"]!);
            Schemas.Valid(Schemas.Folder("mcp"), tool + ".output.schema.json", answer);
        }
        // Any other tool's refusal is one line of text, and it does not run.
        foreach (var (tool, arguments) in new (string, (string, object?)[])[]
        {
            ("log_insight_run", [("outcome", "quiet")]),
            ("save_document", [("fileId", "limit-doc"), ("title", "DEMO"), ("content", "DEMO"), ("modifiedAt", "2026-10-28T10:00:00Z")]),
            ("delete_dashboard", [("dashboardId", Guid.NewGuid()), ("title", "DEMO")]),
        })
        {
            var (error, text) = await Tool(ai, tool, arguments);
            Assert.True(error, tool);
            Assert.StartsWith("This token has made", text);
        }
        Assert.Equal(before, await Counts());

        // Arguments that do not fit are answered as ever, and count for nothing.
        Assert.Contains("is not an argument of remember", (await Tool(ai, "remember", ("content", "DEMO"), ("nope", 1))).Text);

        // Reads answer. So does the list of tools, the same as with the limit off.
        foreach (var (tool, arguments) in new (string, (string, object?)[])[]
        {
            ("get", [("id", id)]), ("list_recent", []), ("history", [("id", id)]), ("list_projects", []), ("list_teams", []), ("get_sync_status", []), ("list_insights", []),
        })
        {
            var (error, text) = await Tool(ai, tool, arguments);
            Assert.False(error, $"{tool}: {text}");
            Assert.DoesNotContain(WriteLimit.Code, text);
        }
        await using (var plain = await McpEngine.Start(WriteLimits.With("off"), fixture.Db, Engine.Door(fixture.Db, config)))
        await using (var client = await McpEngine.Connect(plain.Handler(), "Bearer " + token, "2025-06-18"))
            Assert.Equal((await client.ListToolsAsync()).Select(t => t.Name), (await ai.ListToolsAsync()).Select(t => t.Name));

        // Another client's count is its own.
        var theirs = await Tool(other, "remember", ("content", "DEMO: another client's note."));
        Assert.False(theirs.Error, theirs.Text);

        // The window ending.
        clock.Now += WriteLimit.Window;
        var again = await Tool(ai, "remember", ("content", "DEMO: a note after the window."));
        Assert.False(again.Error, again.Text);
    }

    // A plugin's writes: at the limit its last is saved, past it nothing is, and another plugin's token is another count.
    [PostgresFact] public async Task A_plugin_past_its_limit_writes_nothing()
    {
        var clock = new WriteLimits.Clock();
        var limit = new WriteLimit(clock.Read);
        await using var engine = await McpEngine.Start(WriteLimits.On, fixture.Db, limit: limit);
        engine.Tokens["DEMO-pump-token"] = new(Guid.NewGuid(), "plugin:pump", "ingest", "pump");
        static string Reading(string provider, string key, double value) => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = "w", method = "tools/call", @params = new
            {
                name = IngestEndpoint.Measurements, arguments = new
                {
                    provider, subject = "alex", idempotencyKey = $"{provider}:alex:limit-{key}",
                    source = new { externalId = "limit-" + key, kind = "body-readings", title = "DEMO readings", fetchedAt = "2026-10-28T12:00:05Z", timezone = "Europe/London" },
                    measurements = new[] { new { metric = "weight", value, unit = "kg", observedAt = "2026-10-28T11:45:00Z", key = "limit-" + key } },
                },
            },
        });
        Task<object?> Readings() => Scalar("SELECT count(*) FROM measurements");
        var (status, body, _) = await engine.Send("POST", "/mcp", Reading("cgm", "first", 71.4), "Bearer " + McpEngine.Plugin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(true, (bool?)body!["result"]!["isError"]);
        WriteLimits.Take(limit, engine.Tokens[McpEngine.Plugin].Id, WriteLimit.Writes - 2);
        Assert.Equal(HttpStatusCode.OK, (await engine.Send("POST", "/mcp", Reading("cgm", "last", 71.5), "Bearer " + McpEngine.Plugin)).Status);
        var readings = await Readings();
        Assert.Equal(WriteLimits.TooMany, (await engine.Send("POST", "/mcp", Reading("cgm", "past", 71.6), "Bearer " + McpEngine.Plugin)).Status);
        Assert.Equal(readings, await Readings());
        var pump = await engine.Send("POST", "/mcp", Reading("pump-live", "theirs", 71.7), "Bearer DEMO-pump-token");
        Assert.Equal(HttpStatusCode.OK, pump.Status);
        Assert.NotEqual(readings, await Readings());
    }

    // The app's role and the terminal are never counted: the app's endpoint (here its shared login, the one with no person, which
    // docs/dev/memory.md scores as an AI client's) and the terminal, a login with a full count included, and their writes take
    // nothing from the count.
    [PostgresFact] public async Task The_app_and_the_terminal_are_never_counted()
    {
        await Off();
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "limit-person");
        var at = await MemoryLogins.Terminals(s);
        var config = WriteLimits.On;
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        var services = new ServiceCollection().AddSingleton(config).AddSingleton(fixture.Db).AddSingleton(limit).BuildServiceProvider();
        async Task App(int i)
        {
            var (status, content) = await AppMemoryCalls.Call(services, who.App,
                AppMemoryCalls.Body(AppEndpoint.Memories, new { op = "capture", content = $"DEMO: a person saves note {i}." }), new AppHealth(fixture.Db, config));
            Assert.Equal(200, status);
            Assert.True((bool)content!["ok"]!, content.ToJsonString());
        }
        async Task Terminal(int i)
        {
            using var o = new StringWriter();
            Assert.Equal(0, await MemoryCommands.Remember(["--person", "alex", $"DEMO: a person saves terminal note {i}."], new StringReader(""), o, s));
            Assert.Contains("Remembered as a draft", o.ToString());
        }
        for (var i = 0; i < 3; i++) { await App(i); await Terminal(i); }
        // None of those was counted: each login still has its whole limit, and a full count stops neither.
        WriteLimits.Take(limit, who.App);
        WriteLimits.Take(limit, at.Alex);
        await App(3);
        await Terminal(3);
    }

    // Off, absent or empty: no limit in the services, and past the count every write answers as before.
    [PostgresFact] public async Task With_the_setting_off_a_token_writes_past_the_count_as_before()
    {
        await Off();
        await using var door = await Door.Open(fixture, WriteLimits.With("off"), null);
        var (_, token) = await Connect("limit-off");
        for (var i = 0; i <= WriteLimit.Writes; i++)
            Assert.Equal(HttpStatusCode.OK, (await door.Capture(token, $"DEMO: limit-off note {i}.")).Status);
    }

    // Normal use, for real: every call the whole synthetic corpus takes through one AI client's token in one sitting, on the real
    // MCP server, and each memory through one capture token. None is refused, and every memory is saved, past 40 held for a person.
    // What any other call answers for itself is not what this measures, since one that fails for its own reasons still counts: a
    // held memory takes no link or replacement, a link to a document is refused and a dashboard with no feed is not saved. The room
    // left at the end says each call was counted and none twice, so the sum in the docs is what the limit counted.
    [PostgresFact] public async Task The_whole_synthetic_corpus_through_one_token_is_never_refused()
    {
        await Off();
        var config = WriteLimits.On;
        var corpus = ParityCorpus.Load();
        var limit = new WriteLimit(new WriteLimits.Clock().Read);
        void Used(Guid token, int writes)
        {
            WriteLimits.Take(limit, token, WriteLimit.Writes - writes);
            Assert.NotNull(limit.Take(token));
        }

        await using var door = await Door.Open(fixture, config, limit);
        var (connection, capture) = await Connect("limit-corpus");
        foreach (var memory in corpus.Memories)
        {
            var (status, _, body) = await door.Capture(capture, memory.Text);
            Assert.True(status == HttpStatusCode.OK, $"{memory.Id}: {status} {body}");
        }
        Used(connection, corpus.Memories.Length);

        await using var engine = await McpEngine.Start(config, fixture.Db, Engine.Door(fixture.Db, config), limit,
            s => s.AddSingleton(new InsightStore(fixture.Source)).AddSingleton(new DocumentStore(fixture.Source, Embeddings.Off)));
        var (client, token) = await Client("limit-corpus-ai", "alex");
        await using var ai = await McpEngine.Connect(engine.Handler(), "Bearer " + token, "2025-06-18");
        async Task<(bool Error, string Text)> Call(string tool, params (string Name, object? Value)[] arguments)
        {
            var answer = await Tool(ai, tool, arguments);
            Assert.DoesNotContain(WriteLimit.Code, answer.Text);
            Assert.DoesNotContain("This token has made", answer.Text);
            return answer;
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var memory in corpus.Memories)
        {
            var (error, text) = await Call("remember", ("content", memory.Text));
            Assert.False(error, $"{memory.Id}: {text}");
            ids[memory.Id] = Guid.Parse((string)JsonNode.Parse(text)!["id"]!);
        }
        foreach (var memory in corpus.Memories)
            foreach (var link in WriteLimits.Linked(memory))
                await Call("link", ("source_id", ids[memory.Id]), ("target_id", ids.GetValueOrDefault(link.Target, Guid.NewGuid())), ("type", link.Type));
        foreach (var memory in corpus.Memories.Where(m => m.Status == "canonical"))
            await Call("set_status", ("id", ids[memory.Id]), ("status", "canonical"));
        var replaced = 0;
        foreach (var memory in corpus.Memories.Where(m => m.ReplacedBy is not null))
        {
            var pair = new (string Name, object? Value)[] { ("id", ids[memory.ReplacedBy!]), ("replaces", ids[memory.Id]) };
            var plan = await Call("supersede", pair);
            var confirmation = plan.Error ? "DEMO" : (string)JsonNode.Parse(plan.Text)!["confirmation"]!;
            var applied = await Call("supersede", [.. pair, ("confirmation", confirmation)]);
            if (plan.Error) continue;
            Assert.False(applied.Error, applied.Text);
            replaced++;
        }
        Assert.True(replaced > 0, "no replacement in the corpus was planned and applied, so this measures neither");
        foreach (var document in corpus.Documents)
        {
            var (error, text) = await Call("save_document", ("fileId", document.Source.FileId), ("title", document.Title), ("content", document.Text), ("modifiedAt", document.Source.ModifiedAt));
            Assert.False(error, $"{document.Id}: {text}");
        }
        foreach (var dashboard in corpus.Dashboards)
            await Call("save_dashboard", ("note", "DEMO: the corpus's dashboard."), ("title", dashboard.Title), ("html", "<main>DEMO</main>"));
        var insights = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var insight in corpus.Insights)
        {
            var (error, text) = await Call("save_insight", ("kind", insight.Kind), ("title", insight.Title), ("summary", insight.Summary), ("whyItMatters", ""), ("nextStep", ""),
                ("evidenceLevel", "moderate"), ("limitations", ""), ("generatedAt", insight.GeneratedAt), ("sourcesAsOf", insight.GeneratedAt));
            Assert.False(error, $"{insight.Id}: {text}");
            insights[insight.Id] = Guid.Parse((string)JsonNode.Parse(text)!["id"]!);
        }
        foreach (var run in corpus.InsightRuns)
        {
            var (error, text) = await Call("log_insight_run", ("outcome", run.Outcome), ("note", run.Note), ("insightId", run.Insight is { } saved ? insights[saved] : null));
            Assert.False(error, $"{run.Id}: {text}");
        }
        Used(client, WriteLimits.WholeCorpus(corpus));
    }
}
