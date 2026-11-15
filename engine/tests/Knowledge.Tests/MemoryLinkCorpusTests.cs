using System.Text.Json.Nodes;
using Knowledge.Mcp;
using Knowledge.Tests.Parity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Knowledge.Tests;

// Links on save with the pinned MiniLM (SB-P4-03; skipped without its files, D-041). The corpus's memories of Alex's,
// Sam's and Family's scopes are saved one at a time in the order they were written, each linked on save at MiniLM's
// re-derived threshold, and then each incoming save of the labelled pairs, each put in the trash once its links are read
// (an incoming save is never stored beside another). Every pair a save compares at or above where the labelling started
// is labelled (ThresholdTuningTests), so every link drawn is judged: none may join an unrelated pair, two scopes or a
// held memory. And the same through the memory tools as an AI client reaches them, with the settings that turn it on.
[Collection(SharedStateCollection.Name)]
public sealed class MemoryLinkCorpusTests(DatabaseFixture fixture, ITestOutputHelper output) : IClassFixture<DatabaseFixture>
{
    static readonly string[] Scopes = ["personal:alex", "personal:sam", "family"];

    [ScopeModelFact] public async Task Every_link_the_corpus_draws_on_save_joins_a_labelled_pair_shown_together()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var who = await MemoryLogins.Make(s, "corpus-links");
        using var embeddings = new Embeddings();
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        var threshold = EmbeddingThresholds.Tuned[EmbeddingModels.MiniLmId].LinkOnSave;
        var store = new MemoryStore(s) { Inference = new MemoryLinkInference(db, embeddings.Model!, threshold) };
        var corpus = ParityCorpus.Load();
        var pairs = ParityPairs.Load();
        var members = pairs.Members(corpus);
        var kinds = corpus.Memories.ToDictionary(m => m.Id, m => m.Kind, StringComparer.Ordinal);
        var authors = corpus.Memories.ToDictionary(m => m.Id, m => m.Author, StringComparer.Ordinal);
        var held = corpus.Memories.Where(m => m.Expect is { Held: true }).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        // Each person writes at their terminal (a person's channel, which no burst of writes holds, as 40 an AI client's
        // in ten minutes would); a planted sample through the person's AI client, whose channel holds it as the corpus
        // expects; a Family memory no person wrote, through the household's AI client.
        var (alex, sam) = await MemoryLogins.Terminals(s);
        var cli = new MemoryCall("cli");
        (Guid, MemoryCall) Login(ParityMember m) => (m.Scope switch
        {
            "personal:alex" => "alex",
            "personal:sam" => "sam",
            _ => m.Kind == "incoming" ? pairs.Incoming.Single(i => i.Id == m.Id).Author : authors[m.Id],
        }, held.Contains(m.Id)) switch
        {
            ("alex", false) => (alex, cli),
            ("sam", false) => (sam, cli),
            ("alex", true) => (who.Alex, MemoryLogins.Mcp),
            ("sam", true) => (who.Sam, MemoryLogins.Mcp),
            _ => (who.House, MemoryLogins.Mcp),
        };
        var ids = new Dictionary<Guid, string>();
        var drawn = new List<(string A, string B, string Type, double Score)>();
        foreach (var m in members.Values.Where(m => m.Kind is "memory" or "incoming" && Scopes.Contains(m.Scope))
            .OrderBy(m => m.Written).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            var (login, call) = Login(m);
            var write = await store.Create(login, new(m.Text, Scope: m.Scope == "family" ? "shared" : "private",
                Kind: kinds.GetValueOrDefault(m.Id) is { Length: > 0 } kind ? kind : null), call, default);
            ids[write.Id] = m.Id;
            // The planted samples are held; the screening may hold another (its words read like an instruction), which
            // is then no candidate either.
            if (held.Contains(m.Id)) Assert.NotNull(write.Hold);
            else if (write.Hold is not null) { output.WriteLine($"also held: {m.Id} ({write.Hold.Reason})"); held.Add(m.Id); }
            foreach (var l in await Inferring.Links(s, write.Id))
                if (l.Lifecycle == "active" && ids.TryGetValue(l.Other, out var other)) drawn.Add((other, m.Id, l.Type, l.Confidence!.Value));
            if (m.Kind == "incoming") await store.Forget(login, write.Id, null, call, default);
        }
        var labels = pairs.Pairs.ToDictionary(p => (p.A, p.B), p => p.Label);
        output.WriteLine($"threshold {threshold:0.00}: {drawn.Count} links drawn on save");
        foreach (var (a, b, type, score) in drawn)
            output.WriteLine($"  {a} {type} {b} {score:0.0000} {labels.GetValueOrDefault((a, b), "(not labelled)")}");
        Assert.NotEmpty(drawn);
        Assert.All(drawn, d =>
        {
            Assert.Equal(members[d.A].Scope, members[d.B].Scope);
            Assert.False(held.Contains(d.A) || held.Contains(d.B), $"{d.A} or {d.B} is held");
            Assert.InRange(d.Score, threshold, 1.0001);
            Assert.True(labels.TryGetValue((d.A, d.B), out var label), $"{d.A} and {d.B} were linked and are not labelled");
            Assert.NotEqual("unrelated", label);
            Assert.True(d.Type == "relates_to" || d.Type == "follows" && kinds[d.A] == "episodic" && kinds.GetValueOrDefault(d.B) == "episodic", $"{d.A} {d.Type} {d.B}");
        });
        // Recall, for the record: of the labelled pairs shown together that a save scores at or above the threshold, how
        // many were drawn (at most 3 a save, so fewer than all).
        var reachable = ThresholdTuning.LinkPairs(embeddings.Model!, corpus, pairs)
            .Where(p => p.Score >= threshold && p.Label != "unrelated")
            .Select(p => pairs.Pairs.Single(x => x.Id == p.Id)).Where(p => Scopes.Contains(members[p.A].Scope)).ToArray();
        var found = reachable.Count(p => drawn.Any(d => d.A == p.A && d.B == p.B));
        output.WriteLine($"{found} of the {reachable.Length} labelled pairs shown together at or above {threshold:0.00} drawn; precision 1.000 over {drawn.Count}");
        Assert.True(found > reachable.Length / 2, $"only {found} of {reachable.Length}");
    }

    [ScopeModelFact] public async Task Through_the_memory_tools_a_save_is_linked_and_answers_as_before()
    {
        var s = fixture.Source;
        await Scoped.Mode(s, "on");
        var who = await MemoryLogins.Make(s, "tool-links");
        using var embeddings = new Embeddings();
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!));
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { tools: on }\nadapters:\n  embeddings:\n    enabled: true\n    sources: [memories]\n");
        TestSettings.Use();
        var services = new ServiceCollection().AddSingleton<IHttpContextAccessor>(new HarnessLogin(who.Sam)).AddSingleton(fixture.Db)
            .AddSingleton(config).AddSingleton(embeddings).AddSingleton(new Retrieval(fixture.Db)).BuildServiceProvider();
        await using var ai = await MemoryToolClient.Start(services, config);
        var first = await ai.Ok("remember", new { content = "DEMO: the Wendle orchard has six pear trees along the north wall." });
        // Close enough to link and no copy for certain: a copy for certain takes no link (068).
        var second = await ai.Ok("remember", new { content = "DEMO: the Wendle orchard's pear trees need pruning along the north wall." });
        var apart = await ai.Ok("remember", new { content = "DEMO: the dentist moved the check-up to a Thursday." });
        // remember answers in its contract's shape as before (MemoryToolClient checks it), saying nothing of links.
        Assert.Null(second["links"]);
        var listed = (await ai.Ok("connections", new { id = (string)second["id"]! }))["connections"]!.AsArray();
        var link = Assert.Single(listed);
        Assert.Equal(((string)first["id"]!, true, "relates_to", "suggested", "inferred"), ((string)link!["memory"]!["id"]!, (bool)link["made"]!,
            (string)link["type"]!, (string)link["verification"]!, (string)link["provenance"]!));
        Assert.Empty((await ai.Ok("connections", new { id = (string)apart["id"]! }))["connections"]!.AsArray());
        TestSettings.Use();
    }

    // The app's own path (the store MemoryTools.Store makes for app_memories, with the model wired in as the engine wires
    // it), with scopes off as on every install: a capture is linked within its subject and answers as before, and a person
    // releasing a held memory, one at a time or several, links it, which the terminal's store never does.
    [ScopeModelFact] public async Task Through_the_app_with_scopes_off_a_capture_and_a_release_are_linked()
    {
        var (s, db) = (fixture.Source, fixture.Db);
        await Scoped.Mode(s, "off");
        var who = await MemoryLogins.Make(s, "app-links");
        var app = (await new Principals(s).SyncApp(Principals.NewToken(), default))!.Value;
        using var embeddings = new Embeddings();
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!));
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { tools: on }\nadapters:\n  embeddings:\n    enabled: true\n    sources: [memories]\n");
        TestSettings.Use();
        var services = new ServiceCollection().AddSingleton(config).AddSingleton(db).AddSingleton(embeddings).BuildServiceProvider();
        Assert.NotNull(MemoryTools.Store(services).Inference);
        async Task<JsonNode> Ok(object arguments)
        {
            var (status, content) = await AppMemoryCalls.Call(services, app, AppMemoryCalls.Body(AppEndpoint.Memories, arguments), new AppHealth(db, config));
            Assert.Equal(200, status);
            Assert.True((bool)content!["ok"]!, content.ToJsonString());
            return content;
        }
        // One memory with its passages, saved before, and two held for review as the review queue holds them (no passages).
        var plain = new MemoryStore(s);
        var first = (await plain.Create(who.Alex, new("DEMO: the Quillow orchard has six pear trees along the north wall."), MemoryLogins.Mcp, default)).Id;
        await new SemanticSearch(db, embeddings.Model!, Inferring.Memories).Index(first, default, Inferring.Memories);
        async Task<(Guid Id, int Revision)> Held(string text)
        {
            var w = await plain.Create(who.Alex, new(text), MemoryLogins.Mcp, default);
            await MemoryRows.Run(s, "UPDATE memories SET hold='instruction',hold_score=0.9 WHERE id=@m", ("m", w.Id));
            await MemoryRows.Run(s, "DELETE FROM resource_chunks WHERE resource_id=@m", ("m", w.Id));
            return (w.Id, w.Revision);
        }
        // Each close enough to link and no copy for certain of it: a copy for certain takes no link (068).
        var one = await Held("DEMO: the Quillow orchard's pear trees need pruning along the north wall.");
        var two = await Held("DEMO: the Quillow orchard's pear trees want trimming along the north wall.");
        Assert.Empty(await Inferring.Links(s, one.Id));

        // A person releases one: it is linked to the memory it is close to, automatically, as the person's app login.
        await Ok(new { op = "release", id = one.Id, expected = one.Revision });
        var released = Assert.Single(await Inferring.Links(s, one.Id), l => l.Other == first);
        Assert.Equal(("relates_to", "suggested", "active"), (released.Type, released.Verification, released.Lifecycle));
        Assert.Equal(app, (Guid)(await MemoryRows.Scalar(s, "SELECT made_by FROM made_links WHERE link_id=@l", ("l", released.Id)))!);
        // Several at once: each one released is linked too.
        await Ok(new { op = "release_all", memories = new[] { new { id = two.Id, expected = two.Revision } } });
        Assert.Contains(await Inferring.Links(s, two.Id), l => l.Other == first && l.Type == "relates_to");

        // A capture with scopes off: linked to memories of its own subject only, and its answer as before, naming no link.
        var captured = await Ok(new { op = "capture", content = "DEMO: pears from the Quillow orchard are ripening beside its north wall." });
        Assert.Null(captured["links"]);
        var id = Guid.Parse((string)captured["id"]!);
        var links = await Inferring.Links(s, id);
        Assert.NotEmpty(links);
        Assert.Equal(0L, await MemoryRows.Scalar(s, """
            SELECT count(*) FROM made_links l JOIN memories a ON a.id=l.source_id JOIN memories b ON b.id=l.target_id
            WHERE @m IN (l.source_id,l.target_id) AND a.subject_id<>b.subject_id
            """, ("m", id)));
        TestSettings.Use();
    }
}
