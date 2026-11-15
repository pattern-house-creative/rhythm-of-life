using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Tests;

// SB-P2-09 against a real database: the recall tool answers combined retrieval's list with explain and a receipt in the
// contract's shape, and search_knowledge answers exactly as before unless engine.searchKnowledge is combined, when its
// rows, in its own shape, come in the combined order.
public sealed class RecallToolDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The harness tools as a call reaches them, for `reader`, under `settings`, with the stand-in model.
    (HarnessTools Tools, IServiceProvider Services) Tools(Database db, Guid reader, RhythmConfig settings, IEmbedder? model)
    {
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(settings)
            .AddSingleton<RecallFactory>((o, log) => new Recall(db, model, options: o, log: log)).BuildServiceProvider();
        return (new HarnessTools(new Retrieval(db), new HarnessLogin(reader)), services);
    }

    [PostgresFact] public async Task Recall_answers_with_reasons_and_a_receipt_in_the_contracts_shape()
    {
        TestSettings.Use();
        var db = RecallRows.Db(fixture.Source);
        var model = new MeaningModel();
        var reader = await Passages.Harness(fixture.Source, "recall-tool");
        var quote = await RecallRows.Add(db, fixture.Source, "Installer quote HP-2291", "The installer quoted HP-2291 for the Brindle pump.", model);
        var file = await RecallRows.Add(db, fixture.Source, "Quote file", "Filed with the other HP-2291 papers.");
        await RecallRows.Add(db, fixture.Source, "Boiler swap", "We are swapping the gas boiler next spring.", model);
        // No passages and none of the words: only a link can bring it.
        var manual = await RecallRows.Add(db, fixture.Source, "Brindle manual", "Clear the leaves from the outdoor unit.");
        await RecallRows.Link(fixture.Source, quote, manual, "references");
        var (tools, services) = Tools(db, reader, RecallSettingsYaml.On, model);
        async Task<JsonObject> Ask(string query, int topK = 5, int hops = 0, bool explain = false) =>
            JsonNode.Parse(await tools.RecallSearch(services, query, topK, hops, explain: explain))!.AsObject();

        var plain = await Ask("HP-2291");
        Schemas.Valid(Schemas.Folder("mcp"), "recall.output.schema.json", plain);
        var results = plain["results"]!.AsArray();
        Assert.Equal([quote, file], results.Take(2).Select(r => Guid.Parse(r!["id"]!.GetValue<string>())));
        Assert.All(results, r => Assert.Null(r!["why"]));
        Assert.Equal("DEMO: The installer quoted HP-2291 for the Brindle pump.", results[0]!["excerpt"]!.GetValue<string>());
        Assert.False(plain["semanticSkipped"]!.GetValue<bool>());
        Assert.Empty(plain["notices"]!.AsArray());

        // Explain: the same answer, each result saying why. The quote found by both arms and both its id hits; the file,
        // which has no passages, by its words alone, given the meaning arm's share at its keyword rank.
        var explained = await Ask("HP-2291", explain: true);
        Schemas.Valid(Schemas.Folder("mcp"), "recall.output.schema.json", explained);
        Assert.Equal(plain["receipt"]!.GetValue<string>(), explained["receipt"]!.GetValue<string>());
        var why = explained["results"]!.AsArray().Select(r => r!["why"]!).ToArray();
        Assert.Equal((1, 1, 2), (why[0]["rank"]!.GetValue<int>(), why[0]["keyword"]!["rank"]!.GetValue<int>(), why[0]["keyword"]!["exact"]!.GetValue<int>()));
        Assert.NotNull(why[0]["meaning"]);
        Assert.False(why[0]["imputed"]!.GetValue<bool>());
        Assert.Equal((2, 1), (why[1]["keyword"]!["rank"]!.GetValue<int>(), why[1]["keyword"]!["exact"]!.GetValue<int>()));
        Assert.Null(why[1]["meaning"]);
        Assert.True(why[1]["imputed"]!.GetValue<bool>());
        // A question in other words: found by meaning, no word of it held.
        var meaning = (await Ask("what is replacing the heating?", explain: true))["results"]!.AsArray()[0]!;
        Assert.EndsWith("swap", meaning["title"]!.GetValue<string>());
        Assert.Null(meaning["why"]!["keyword"]);
        Assert.Equal(1, meaning["why"]!["meaning"]!["rank"]!.GetValue<int>());
        // A typed link from a result, with hops: the manual at its graph place, saying which link brought it.
        var hop = (await Ask("HP-2291", 10, 1, true))["results"]!.AsArray().Single(r => Guid.Parse(r!["id"]!.GetValue<string>()) == manual)!;
        Assert.Equal(("references", quote.ToString(), "outgoing"), (hop["why"]!["link"]!["type"]!.GetValue<string>(),
            hop["why"]!["link"]!["from"]!.GetValue<string>(), hop["why"]!["link"]!["direction"]!.GetValue<string>()));

        // The receipt: the same question over the same records gives the same one; a record changing gives another.
        Assert.Equal(plain["receipt"]!.GetValue<string>(), (await Ask("HP-2291"))["receipt"]!.GetValue<string>());
        await MemoryRows.Run(fixture.Source, "UPDATE resources SET revision='2' WHERE id=@id", ("id", file));
        Assert.NotEqual(plain["receipt"]!.GetValue<string>(), (await Ask("HP-2291"))["receipt"]!.GetValue<string>());

        // Without the model: keyword results, and the answer says so.
        var (keywordOnly, keywordServices) = Tools(db, reader, RecallSettingsYaml.On, null);
        var off = JsonNode.Parse(await keywordOnly.RecallSearch(keywordServices, "HP-2291"))!;
        Schemas.Valid(Schemas.Folder("mcp"), "recall.output.schema.json", off);
        Assert.True(off["semanticSkipped"]!.GetValue<bool>());
        Assert.StartsWith("Semantic search was skipped", off["notices"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(quote, Guid.Parse(off["results"]!.AsArray()[0]!["id"]!.GetValue<string>()));
    }

    [PostgresFact] public async Task Search_knowledge_answers_as_before_until_the_switch_and_then_from_the_combined_list()
    {
        TestSettings.Use();
        var db = RecallRows.Db(fixture.Source);
        var model = new MeaningModel();
        var reader = await Passages.Harness(fixture.Source, "recall-switch");
        await RecallRows.Add(db, fixture.Source, "Larrow quote WM-7710", "The Larrow installer quoted WM-7710 for the pump.", model);
        await RecallRows.Add(db, fixture.Source, "Larrow radiators", "The Larrow radiators in the hall stay as they are.", model);
        await RecallRows.Add(db, fixture.Source, "Larrow kettle", "The Larrow kettle descaler is under the sink.", model);
        await RecallRows.Add(db, fixture.Source, "Larrow swap", "Next spring the Larrow boiler swap goes ahead.", model);
        var before = new Retrieval(db);
        string[] questions = ["WM-7710", "Larrow radiators", "what is replacing the heating?", "Larrow", ""];

        // Off (absent), and with recall on but the switch not: exactly what search_knowledge returned before.
        foreach (var settings in new[] { RhythmConfig.Parse(TestSettings.Yaml), RecallSettingsYaml.On })
        {
            var (tools, services) = Tools(db, reader, settings, model);
            foreach (var q in questions)
                Assert.Equal(await before.Search(reader, q, 20, default), await tools.SearchKnowledge(services, q, 20));
        }

        // With engine.searchKnowledge combined: the combined list's records, in its order, in search_knowledge's rows.
        var (combined, combinedServices) = Tools(db, reader, RecallSettingsYaml.Combined, model);
        var recall = new Recall(db, model, options: RecallOptions.Default with { MaxTopK = 50 });
        var columns = JsonNode.Parse(await before.Search(reader, "Larrow", 20, default))!.AsArray()[0]!.AsObject().Select(p => p.Key).ToArray();
        foreach (var q in questions.Where(q => q.Length > 0))
        {
            var rows = JsonNode.Parse(await combined.SearchKnowledge(combinedServices, q, 20))!.AsArray();
            Assert.Equal((await recall.Search(reader, new(q, 20), default)).Hits.Select(h => h.Id), rows.Select(r => Guid.Parse(r!["id"]!.GetValue<string>())));
            Assert.All(rows, r => Assert.Equal(columns, r!.AsObject().Select(p => p.Key)));
            Assert.All(rows, r => Assert.True(r!["excerpt"]!.GetValue<string>().Length <= 400));
        }
        // By meaning, which the keyword search alone never finds.
        Assert.DoesNotContain("swap", await before.Search(reader, "what is replacing the heating?", 20, default));
        Assert.EndsWith("swap", JsonNode.Parse(await combined.SearchKnowledge(combinedServices, "what is replacing the heating?", 20))!
            .AsArray()[0]!["title"]!.GetValue<string>());
        // Its bounds and their words as before, 50 results at most; an empty query as before.
        Assert.Equal(await before.Search(reader, "", 20, default), await combined.SearchKnowledge(combinedServices, "", 20));
        Assert.Equal("Limit must be 1–50", (await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => combined.SearchKnowledge(combinedServices, "Larrow", 51))).Message);
        Assert.Equal("Query too long", (await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => combined.SearchKnowledge(combinedServices, new string('a', 201), 5))).Message);
        Assert.True(JsonNode.Parse(await combined.SearchKnowledge(combinedServices, "Larrow", 50))!.AsArray().Count <= 50);
    }

    // A native memory the recall tool finds carries the memory's own revision, so a change of it gives another receipt
    // even when the order stays as it was; search_knowledge, combined or not, is a catalog tool and never serves it,
    // its memory left out before the limit.
    [PostgresFact] public async Task A_recalled_memory_carries_its_own_revision_and_search_knowledge_never_serves_it()
    {
        TestSettings.Use();
        var db = RecallRows.Db(fixture.Source);
        var model = new MeaningModel();
        var reader = await Passages.Harness(fixture.Source, "recall-memory-receipt");
        var who = await MemoryLogins.Make(fixture.Source, "recall-memory-receipt");
        await Scoped.Mode(fixture.Source, "off");
        var store = new MemoryStore(fixture.Source);
        var memory = await store.Create(who.Alex, new("DEMO: the Pellow kitten sees the vet on Friday."), MemoryLogins.Mcp, default);
        var note = await RecallRows.Add(db, fixture.Source, "Pellow vet bill", "The Pellow vet bill is paid.");
        var covered = PassageSources.Of(false, false, true, []);
        await Passages.Sweep(new EmbeddingBackfill(db), model, covered);
        (HarnessTools Tools, IServiceProvider Services) With(RhythmConfig settings)
        {
            var services = new ServiceCollection().AddSingleton(db).AddSingleton(settings)
                .AddSingleton<RecallFactory>((o, log) => new Recall(db, model, covered, options: o, log: log)).BuildServiceProvider();
            return (new HarnessTools(new Retrieval(db), new HarnessLogin(reader)), services);
        }
        var (tools, services) = With(RecallSettingsYaml.Combined);
        async Task<JsonObject> Ask() => JsonNode.Parse(await tools.RecallSearch(services, "Pellow kitten", 5))!.AsObject();
        static Guid[] Ids(JsonArray rows) => [.. rows.Select(r => Guid.Parse(r!["id"]!.GetValue<string>()))];
        JsonNode Of(JsonObject answer) => answer["results"]!.AsArray().Single(r => Guid.Parse(r!["id"]!.GetValue<string>()) == memory.Id)!;

        var first = await Ask();
        Schemas.Valid(Schemas.Folder("mcp"), "recall.output.schema.json", first);
        Assert.Contains(memory.Id, Ids(first["results"]!.AsArray()));
        Assert.Equal($"memory:{memory.Revision}", Of(first)["revision"]!.GetValue<string>());
        // An append: new text, its passages made again, the same order, another revision and so another receipt.
        var appended = await store.Append(who.Alex, memory.Id, "DEMO: bring the Pellow blanket.", null, MemoryLogins.Mcp, default);
        await Passages.Sweep(new EmbeddingBackfill(db), model, covered);
        var second = await Ask();
        Assert.Equal(Ids(first["results"]!.AsArray()), Ids(second["results"]!.AsArray()));
        Assert.Equal($"memory:{appended.Revision}", Of(second)["revision"]!.GetValue<string>());
        Assert.NotEqual(first["receipt"]!.GetValue<string>(), second["receipt"]!.GetValue<string>());
        // A change of its tags alone keeps its passages and still moves its revision.
        var tagged = await store.Update(who.Alex, memory.Id, new(Tags: ["pets"]), null, MemoryLogins.Mcp, default);
        var third = await Ask();
        Assert.Equal($"memory:{tagged.Revision}", Of(third)["revision"]!.GetValue<string>());
        Assert.NotEqual(second["receipt"]!.GetValue<string>(), third["receipt"]!.GetValue<string>());

        // search_knowledge in either mode: the note, never the memory, even asking for one result.
        foreach (var settings in new[] { RecallSettingsYaml.Combined, RecallSettingsYaml.On })
        {
            var (catalog, catalogServices) = With(settings);
            foreach (var limit in new[] { 1, 20 })
            {
                var rows = JsonNode.Parse(await catalog.SearchKnowledge(catalogServices, "Pellow vet", limit))!.AsArray();
                Assert.Equal(note, Ids(rows)[0]);
                Assert.DoesNotContain(memory.Id, Ids(rows));
                Assert.DoesNotContain("blanket", rows.ToJsonString());
            }
        }
    }

    // However long the texts, every combined row keeps search_knowledge's excerpt: the start of its text, up to 400
    // characters, never cut short by the recall tool's budget. Keyword-only (no model), as on an install with embeddings off.
    [PostgresFact] public async Task Search_knowledge_from_the_combined_list_gives_every_row_its_excerpt()
    {
        TestSettings.Use();
        var db = RecallRows.Db(fixture.Source);
        var reader = await Passages.Harness(fixture.Source, "recall-excerpts");
        var texts = new Dictionary<Guid, string>();
        for (var i = 0; i < 16; i++)
        {
            var text = $"Tressel ledger page {i}. " + string.Concat(Enumerable.Repeat($"Tressel line {i} of the ledger. ", 160));
            texts[await RecallRows.Add(db, fixture.Source, $"Tressel ledger {i}", text)] = "DEMO: " + text;
        }
        var (combined, services) = Tools(db, reader, RecallSettingsYaml.Combined, null);
        var rows = JsonNode.Parse(await combined.SearchKnowledge(services, "Tressel ledger", 20))!.AsArray();
        Assert.Equal(16, rows.Count);
        // The start of its text, up to 400 characters, cut at a space near the end as every excerpt is (Recall.Cut).
        foreach (var r in rows)
        {
            var excerpt = r!["excerpt"]!.GetValue<string>();
            Assert.StartsWith(excerpt, texts[Guid.Parse(r["id"]!.GetValue<string>())]);
            Assert.InRange(excerpt.Length, 321, 400);
        }
    }
}
