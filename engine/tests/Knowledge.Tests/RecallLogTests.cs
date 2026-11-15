using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// SB-P2-11 (D-022, D-034; migration 042): the opt-in recall log and implicit feedback. Off, the default, nothing is kept
// and nothing is read; on, each recall the recall tool or the explorer answers is kept in its reader's scope with the
// receipt it answered with, at most 200 a day a scope, for 30 days; a follow-up within 30 minutes is marked; and the counts
// per scope are kept apart from the records. No answer changes either way. Fictional people only.
public sealed class RecallLogSettingsTests
{
    [Fact] public void The_log_is_off_unless_the_settings_say_on_and_only_the_receipted_searches_are_logged()
    {
        TestSettings.Use();
        // Absent, as in every rhythm.yaml written before it: off.
        Assert.False(RecallSettings.From(RhythmConfig.Parse(TestSettings.Yaml)).Log);
        Assert.False(RecallSettings.From(RecallSettingsYaml.With("legacySearch: false, recall: on, recallLog: off")).Log);
        var on = RecallSettingsYaml.With("legacySearch: false, recall: on, recallLog: on");
        Assert.Equal(new RecallSettings(true, false, true), RecallSettings.From(on));
        Assert.Equal("engine.recallLog is off or on", Assert.Throws<ConfigException>(() => RecallSettingsYaml.With("recallLog: yes")).Message);
        // Off, no log is made for any search; on, for the recall tool's and the explorer's only, never search_knowledge's
        // combined mode (Logged off) or the evaluation's own options.
        using var source = Npgsql.NpgsqlDataSource.Create("Host=example.invalid");
        foreach (var options in new[] { RecallOptions.Tool, Relationships.RecallStars.Budget, RecallOptions.Default })
            Assert.Null(RecallServing.Log(RecallSettingsYaml.On, options, source));
        Assert.NotNull(RecallServing.Log(on, RecallOptions.Tool, source));
        Assert.NotNull(RecallServing.Log(on, Relationships.RecallStars.Budget, source));
        Assert.Null(RecallServing.Log(on, RecallOptions.Default, source));
        Assert.Null(RecallServing.Log(on, RecallOptions.Default with { MaxTopK = 50, Memories = false }, source));
        // The settings schema takes the key, off or on.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "contracts", "rhythm.schema.json");
        var key = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("properties").GetProperty("engine")
            .GetProperty("properties").GetProperty("recallLog");
        Assert.Equal(["off", "on", null], key.GetProperty("enum").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()));
        Assert.Equal("off", key.GetProperty("default").GetString());
    }
}

public sealed class RecallLogTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    async Task<long> Count(string sql, params (string, object?)[] args) => Convert.ToInt64(await MemoryLogins.Scalar(fixture.Source, sql, args));

    static RecallHit Hit(Guid id, int? hop = null) => new(id, JsonSerializer.SerializeToElement(new { id, revision = "1" }), "DEMO", false, 4, 0.01,
        new(1, 1, [], 0, null, null, null, hop, hop is null ? null : "references", hop is null ? null : id, hop is null ? null : "out"));

    [PostgresFact] public async Task Off_a_recall_keeps_nothing_and_on_it_is_kept_in_its_readers_scope_with_the_same_answer()
    {
        var s = fixture.Source;
        var db = RecallRows.Db(s);
        var model = new MeaningModel();
        var alex = await Scoped.Principal(s, "log-alex", "harness", "alex");
        var house = await Scoped.Principal(s, "log-house", "harness");
        var app = (Guid)(await MemoryLogins.Scalar(s, "SELECT id FROM principals WHERE role='app' AND enabled AND person_id IS NULL")
            ?? await Scoped.Principal(s, "log-app", "app"));
        await RecallRows.Add(db, s, "Varrow boiler", "The Varrow boiler is serviced in March.", model);
        await RecallRows.Add(db, s, "Varrow radiators", "The Varrow radiators stay in the hall.", model);
        await RecallRows.Add(db, s, "Shopping", "Milk and eggs for the Varrow fair.", model);
        var q = new RecallQuery("Varrow boiler", 5, Domain: "household");
        // The class's other tests write log rows of their own; this one counts from none.
        await MemoryRows.Run(s, "DELETE FROM recall_log; DELETE FROM recall_counts");
        var plain = await new Recall(db, model, options: RecallOptions.Tool).Search(alex, q, default);
        Assert.NotEmpty(plain.Hits);
        // Off (no log made): nothing kept, nothing counted.
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_log"));
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_counts"));

        var log = new RecallLog(s);
        var kept = await new Recall(db, model, options: RecallOptions.Tool, log: log).Search(alex, q, default);
        // The same answer, the same receipt.
        Assert.Equal(plain.Hits.Select(h => (h.Id, h.Score)), kept.Hits.Select(h => (h.Id, h.Score)));
        var receipt = RecallReply.Receipt(q, kept.Hits);
        Assert.Equal(RecallReply.Receipt(q, plain.Hits), receipt);
        // One row in Alex's own scope: the question, its arguments, what it found in order, the receipt.
        Assert.Equal($"alex|mcp|Varrow boiler|{receipt}|{string.Join(",", kept.Hits.Select(h => h.Id))}|", await MemoryLogins.Scalar(s,
            "SELECT subject_id||'|'||channel||'|'||query||'|'||receipt||'|'||array_to_string(returned,',')||'|'||array_to_string(followed,',') FROM recall_log"));
        Assert.Equal("""{"hops": 0, "topK": 5, "domain": "household"}""", await MemoryLogins.Scalar(s, "SELECT params::text FROM recall_log"));
        Assert.Equal(kept.Hits.Count, await Count("SELECT count(*) FROM recall_counts WHERE subject_id='alex' AND recalled=1 AND followed=0"));
        // The household view's AI client and the app keep theirs in the household's scope; the counts are each scope's own.
        await new Recall(db, model, options: RecallOptions.Tool, log: log).Search(house, q, default);
        await new Recall(db, model, options: Relationships.RecallStars.Budget, log: log).Search(app, q, default);
        Assert.Equal("alex:mcp household:app household:mcp", await MemoryLogins.Scalar(s,
            "SELECT string_agg(subject_id||':'||channel,' ' ORDER BY subject_id,channel) FROM recall_log"));
        Assert.Equal(2L, await Count("SELECT max(recalled) FROM recall_counts WHERE subject_id='household'"));
        Assert.Equal(1L, await Count("SELECT max(recalled) FROM recall_counts WHERE subject_id='alex'"));
        // A search its options do not log (search_knowledge's combined mode) keeps nothing, with a log or without.
        await new Recall(db, model, options: RecallOptions.Default with { MaxTopK = 50, Memories = false }, log: log).Search(alex, q, default);
        Assert.Equal(3L, await Count("SELECT count(*) FROM recall_log"));
        // A neighbour a hop reached is returned, not counted as recalled.
        Assert.True(await log.Write(alex, new("DEMO hop", 5, 1), [Hit(kept.Hits[0].Id), Hit(kept.Hits[1].Id, 1)], "rc1-0000000000000001", default));
        Assert.Equal(2L, await Count("SELECT recalled FROM recall_counts WHERE subject_id='alex' AND resource_id=@id", ("id", kept.Hits[0].Id)));
        Assert.Equal(1L, await Count("SELECT recalled FROM recall_counts WHERE subject_id='alex' AND resource_id=@id", ("id", kept.Hits[1].Id)));
        // A login that recalls nothing logs nothing, and a malformed row is refused, never the answer's failure.
        var plugin = await Scoped.Principal(s, "log-plugin", "ingest", plugin: "cgm");
        Assert.False(await log.Write(plugin, q, kept.Hits, receipt, default));
        Assert.False(await log.Write(alex, q, kept.Hits, "not-a-receipt", default));
        Assert.Equal(4L, await Count("SELECT count(*) FROM recall_log"));
    }

    // Through the tools as a call reaches them, the log decided as the engine decides it (RecallServing.For hands the
    // factory its log): the recall tool's answer is kept with the receipt it answered with, and is the same answer as with
    // the log off; search_knowledge in its combined mode answers as it did and is never kept.
    [PostgresFact] public async Task Through_the_tools_only_the_recall_tool_is_kept_with_the_receipt_it_answered()
    {
        TestSettings.Use();
        var s = fixture.Source;
        var db = RecallRows.Db(s);
        var model = new MeaningModel();
        var sam = await Scoped.Principal(s, "log-tool-sam", "harness", "sam");
        await RecallRows.Add(db, s, "Tarrow kettle", "The Tarrow kettle is descaled in May.", model);
        await RecallRows.Add(db, s, "Tarrow toaster", "The Tarrow toaster sits by the window.", model);
        (HarnessTools, IServiceProvider) Tools(RhythmConfig settings) =>
            (new HarnessTools(new Retrieval(db), new HarnessLogin(sam)), new ServiceCollection().AddSingleton(db).AddSingleton(settings)
                .AddSingleton<RecallFactory>((o, log) => new Recall(db, model, options: o, log: log)).BuildServiceProvider());
        await MemoryRows.Run(s, "DELETE FROM recall_log; DELETE FROM recall_counts");

        var (offTools, offServices) = Tools(RecallSettingsYaml.Combined);
        var off = await offTools.RecallSearch(offServices, "Tarrow kettle", 5, explain: true);
        var offRows = await offTools.SearchKnowledge(offServices, "Tarrow kettle", 20);
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_log"));

        var (onTools, onServices) = Tools(RecallSettingsYaml.With("legacySearch: false, recall: on, searchKnowledge: combined, recallLog: on"));
        var on = await onTools.RecallSearch(onServices, "Tarrow kettle", 5, explain: true);
        Assert.Equal(off, on);
        Assert.Equal(offRows, await onTools.SearchKnowledge(onServices, "Tarrow kettle", 20));
        var answer = JsonNode.Parse(on)!;
        var ids = answer["results"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToArray();
        Assert.NotEmpty(ids);
        Assert.Equal($"sam|mcp|Tarrow kettle|{answer["receipt"]!.GetValue<string>()}|{string.Join(",", ids)}", await MemoryLogins.Scalar(s,
            "SELECT string_agg(subject_id||'|'||channel||'|'||query||'|'||receipt||'|'||array_to_string(returned,','),';') FROM recall_log"));
    }

    [PostgresFact] public async Task On_the_log_keeps_at_most_200_a_day_for_each_scope_and_drops_its_expired_rows_as_it_writes()
    {
        var s = fixture.Source;
        var sam = await Scoped.Principal(s, "cap-sam", "harness", "sam");
        var other = await Scoped.Principal(s, "cap-sam-2", "harness", "sam");
        var alex = await Scoped.Principal(s, "cap-alex", "harness", "alex");
        var row = await Scoped.Row(s, "household", "documents", "shared", "cap-doc");
        await MemoryRows.Run(s, """
            DELETE FROM recall_log WHERE subject_id IN ('sam','alex');
            INSERT INTO recall_log(subject_id,channel,query,returned,receipt,at)
            SELECT 'sam','mcp','DEMO question '||g,ARRAY[@r]::uuid[],'rc1-00000000000000aa',now()-interval '1 hour' FROM generate_series(1,199) g;
            INSERT INTO recall_log(subject_id,channel,query,returned,receipt,at)
            SELECT 'sam','mcp','DEMO old question '||g,ARRAY[@r]::uuid[],'rc1-00000000000000bb',now()-interval '31 days' FROM generate_series(1,25) g
            """, ("r", row));
        var log = new RecallLog(s);
        var q = new RecallQuery("DEMO cap", 5);
        Assert.True(await log.Write(sam, q, [Hit(row)], "rc1-0000000000000002", default));
        // The 200th of the day is kept; the next is not, from any of Sam's logins; Alex's scope has its own 200.
        Assert.False(await log.Write(sam, q, [Hit(row)], "rc1-0000000000000003", default));
        Assert.False(await log.Write(other, q, [Hit(row)], "rc1-0000000000000004", default));
        Assert.True(await log.Write(alex, q, [Hit(row)], "rc1-0000000000000005", default));
        Assert.Equal(200L, await Count("SELECT count(*) FROM recall_log WHERE subject_id='sam' AND at>now()-interval '1 day'"));
        // Each write dropped up to 20 of its scope's rows past the 30 days, oldest first.
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_log WHERE subject_id='sam' AND at<now()-interval '30 days'"));
        Assert.Equal(1L, await Count("SELECT recalled FROM recall_counts WHERE subject_id='sam' AND resource_id=@r", ("r", row)));
    }

    [PostgresFact] public async Task A_get_or_change_of_a_recalled_memory_within_30_minutes_is_marked_followed()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "follow");
        await Scoped.Mode(s, "off");
        var plain = new MemoryStore(s);
        var log = new RecallLog(s);
        var store = new MemoryStore(s, log);
        var walk = (await plain.Create(who.Alex, new("DEMO: the Ferrow walk."), MemoryLogins.Mcp, default)).Id;
        var cafe = (await plain.Create(who.Alex, new("DEMO: the Ferrow cafe."), MemoryLogins.Mcp, default)).Id;
        var bins = (await plain.Create(who.Alex, new("DEMO: the Ferrow bins."), MemoryLogins.Mcp, default)).Id;
        await MemoryRows.Run(s, "DELETE FROM recall_log WHERE subject_id IN ('alex','sam')");
        async Task<string> Followed() => (string)(await MemoryLogins.Scalar(s,
            "SELECT array_to_string(followed,',') FROM recall_log WHERE subject_id='alex' ORDER BY at DESC LIMIT 1"))!;
        // Nothing logged: a follow-up marks nothing.
        await store.Get(who.Alex, walk, default);
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_counts WHERE resource_id=@m AND followed>0", ("m", walk)));
        Assert.True(await log.Write(who.Alex, new("DEMO Ferrow", 5), [Hit(walk), Hit(cafe)], "rc1-0000000000000006", default));
        // Without the log the store marks nothing; with it, a get and then an append are marked, each once.
        await plain.Get(who.Alex, walk, default);
        Assert.Equal("", await Followed());
        await store.Get(who.Alex, walk, default);
        await store.Get(who.Alex, walk, default);
        Assert.Equal(walk.ToString(), await Followed());
        await store.Append(who.Alex, cafe, "DEMO: it opens at nine.", null, MemoryLogins.Key("follow-key-0001"), default);
        Assert.Equal($"{walk},{cafe}", await Followed());
        // A retry answered again, and a record the recall did not return, are no follow-ups.
        await store.Append(who.Alex, cafe, "DEMO: it opens at nine.", null, MemoryLogins.Key("follow-key-0001"), default);
        await store.Update(who.Alex, bins, new(Tags: ["ferrow"]), null, MemoryLogins.Mcp, default);
        Assert.Equal($"{walk},{cafe}", await Followed());
        Assert.Equal(1L, await Count("SELECT followed FROM recall_counts WHERE subject_id='alex' AND resource_id=@m", ("m", cafe)));
        // Another scope's reader follows its own recalls only: Sam reads the walk (scopes off), and Alex's row is as it was.
        await store.Get(who.Sam, walk, default);
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_counts WHERE subject_id='sam' AND resource_id=@m", ("m", walk)));
        // A neighbour a hop reached is marked followed on the recall's row, but never counted: it was never counted
        // recalled, so no count is followed more often than recalled.
        var gate = (await plain.Create(who.Alex, new("DEMO: the Ferrow gate."), MemoryLogins.Mcp, default)).Id;
        Assert.True(await log.Write(who.Alex, new("DEMO Ferrow walk", 5), [Hit(walk), Hit(gate, hop: 1)], "rc1-0000000000000008", default));
        await store.Get(who.Alex, gate, default);
        Assert.Equal(gate.ToString(), await Followed());
        Assert.Equal(0L, await Count("SELECT count(*) FROM recall_counts WHERE resource_id=@m", ("m", gate)));
        Assert.Equal(walk.ToString(), await MemoryLogins.Scalar(s,
            "SELECT array_to_string(direct,',') FROM recall_log WHERE subject_id='alex' ORDER BY at DESC LIMIT 1"));
        // Past 30 minutes a follow-up counts no more.
        Assert.True(await log.Write(who.Alex, new("DEMO Ferrow bins", 5), [Hit(bins)], "rc1-0000000000000007", default));
        await MemoryRows.Run(s, "UPDATE recall_log SET at=at-interval '31 minutes' WHERE subject_id='alex'");
        await store.Update(who.Alex, bins, new(Tags: ["ferrow", "bins"]), null, MemoryLogins.Mcp, default);
        Assert.Equal("", await Followed());
    }

    // The engine's login reads the log and writes it only through 042's functions.
    [PostgresFact] public async Task The_engine_login_writes_the_log_only_through_the_functions()
    {
        var s = fixture.Source;
        var alex = await Scoped.Principal(s, "log-login-alex", "harness", "alex");
        var row = await Scoped.Row(s, "household", "documents", "shared", "log-login-doc");
        await using var login = await TestLogin.Make(s, fixture.Name);
        Assert.True(await new RecallLog(login.Source).Write(alex, new("DEMO login", 5), [Hit(row)], "rc1-0000000000000008", default));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source,
            "INSERT INTO recall_log(subject_id,channel,query,returned,receipt) VALUES('alex','mcp','DEMO','{}','rc1-0000000000000009')"));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE recall_counts SET recalled=99"));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "DELETE FROM recall_log"));
        Assert.Equal((0, 0), await RecallLog.Prune(login.Source, default));
        Assert.Contains("recall_log", await MemoryRows.CheckedTables(s));
        Assert.Contains("recall_counts", await MemoryRows.CheckedTables(s));
    }
}
