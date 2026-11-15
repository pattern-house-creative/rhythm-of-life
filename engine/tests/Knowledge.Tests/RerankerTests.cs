using System.Text.Json;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// A stand-in reranker: a text scores by how many of the question's words it holds, unless Fails (it throws), Slow (it
// waits for its token) or Wrong (it answers the wrong number of scores). Calls counts how often it was asked.
internal sealed class WordReranker : IReranker
{
    public bool Fails, Slow, Wrong;
    // With Prefer, a text scores 1 when it holds that word and 0 otherwise.
    public string? Prefer;
    public int Calls;
    public string Id => "word-reranker";
    public IReadOnlyList<double> Score(string query, IReadOnlyList<string> texts, CancellationToken ct)
    {
        Calls++;
        if (Fails) throw new InvalidOperationException("DEMO: the stand-in reranker failed");
        if (Slow) Task.Delay(Timeout.Infinite, ct).Wait(ct);
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var scores = texts.Select(t => Prefer is { } word ? (t.Contains(word, StringComparison.Ordinal) ? 1.0 : 0.0)
            : t.ToLowerInvariant().Split([' ', '\n', '.'], StringSplitOptions.RemoveEmptyEntries).Count(words.Contains)).ToList();
        return Wrong ? scores.Skip(1).ToList() : scores;
    }
}

// The reranker step (SB-P2-12, D-026, D-249): off by default; on, it reorders the first results by its scores and keeps the
// rest, and whenever it is not ready, rests after failures, fails or overruns, the list stays as fused, with a notice.
public sealed class RerankerTests
{
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static RecallCandidate Candidate(string title, string text, double score) =>
        new(Guid.NewGuid(), new(JsonSerializer.SerializeToElement(new { title }), "DEMO: " + text, text.Length, "notes", title), score);

    static List<RecallCandidate> Fused() =>
    [
        Candidate("Shopping", "milk and eggs", 0.05),
        Candidate("Boiler", "the boiler service is on Thursday", 0.04),
        Candidate("Garden", "plant the beans", 0.03),
        Candidate("Boiler quote", "boiler service quote", 0.02),
    ];
    static string[] Titles(IEnumerable<RecallCandidate> list) => [.. list.Select(c => c.Row.Row.GetProperty("title").GetString()!)];

    [Fact] public void It_reorders_the_first_results_by_their_scores_and_keeps_the_rest_as_fused()
    {
        var model = new WordReranker();
        var step = new RecallRerank(() => model, () => "on", new RerankBreaker(), depth: 3);
        var (list, notice) = step.Apply("when is the boiler service", Fused(), default);
        // The first three by the model's scores (the boiler 4 of the question's words, the garden 1, shopping none); the
        // fourth, past the depth, stays fourth although it would score higher than two above it.
        Assert.Equal(["Boiler", "Garden", "Shopping", "Boiler quote"], Titles(list));
        Assert.Null(notice);
        // The fused scores are kept.
        Assert.Equal([0.04, 0.03, 0.05, 0.02], list.Select(c => c.Score));
        // Ties keep the fused order: with no word in common every result scores 0.
        Assert.Equal(Titles(Fused()), Titles(step.Apply("xylophone", Fused(), default).List));
        // Each result as its title and, on the next line, the text its excerpt comes from, cut at 1,200 characters.
        var text = RecallRerank.Text(Candidate("Long", new string('x', 3000), 0.1));
        Assert.StartsWith("Long\nDEMO: xxx", text);
        Assert.True(text.Length <= RecallRerank.TextChars);
        Assert.Equal(20, RecallRerank.DefaultDepth);
    }

    [Fact] public void A_failure_a_wrong_answer_or_an_overrun_leaves_the_fused_order()
    {
        foreach (var model in new[] { new WordReranker { Fails = true }, new WordReranker { Wrong = true }, new WordReranker { Slow = true } })
        {
            var breaker = new RerankBreaker();
            var step = new RecallRerank(() => model, () => "on", breaker, budget: TimeSpan.FromMilliseconds(50));
            var fused = Fused();
            var (list, notice) = step.Apply("when is the boiler service", fused, default);
            Assert.Equal(Titles(fused), Titles(list));
            Assert.Equal("Reranking was skipped this time (the reranker failed): the results are in the combined order.", notice);
            Assert.False(breaker.Open);
        }
    }

    [Fact] public void Three_failures_in_a_row_rest_it_for_a_minute_and_a_success_closes_it()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-09T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var breaker = new RerankBreaker(clock: clock);
        var model = new WordReranker { Fails = true };
        var step = new RecallRerank(() => model, () => "on", breaker);
        for (var i = 0; i < 3; i++) step.Apply("boiler", Fused(), default);
        Assert.True(breaker.Open);
        // While it rests, the model is not asked.
        var (list, notice) = step.Apply("boiler", Fused(), default);
        Assert.Equal(3, model.Calls);
        Assert.Equal(Titles(Fused()), Titles(list));
        Assert.Contains("rests for a minute", notice);
        // After a minute one search tries again; a failure rests it again at once, a success closes it.
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False(breaker.Open);
        step.Apply("boiler", Fused(), default);
        Assert.Equal(4, model.Calls);
        Assert.True(breaker.Open);
        clock.Now += TimeSpan.FromMinutes(1);
        model.Fails = false;
        Assert.Null(step.Apply("boiler", Fused(), default).Notice);
        Assert.False(breaker.Open);
        // A success between failures starts the count again.
        model.Fails = true;
        step.Apply("boiler", Fused(), default);
        step.Apply("boiler", Fused(), default);
        model.Fails = false;
        step.Apply("boiler", Fused(), default);
        model.Fails = true;
        step.Apply("boiler", Fused(), default);
        step.Apply("boiler", Fused(), default);
        Assert.False(breaker.Open);
    }

    [Fact] public void Not_ready_it_is_not_asked_and_the_answer_says_why()
    {
        var step = new RecallRerank(() => null, () => "downloading", new RerankBreaker());
        var (list, notice) = step.Apply("boiler", Fused(), default);
        Assert.Equal(Titles(Fused()), Titles(list));
        Assert.Equal("Reranking was skipped this time (the reranker is downloading): the results are in the combined order.", notice);
        // One result or none: nothing to reorder, nothing to say.
        Assert.Null(step.Apply("boiler", [Candidate("Only", "one", 1)], default).Notice);
    }

    [Fact] public void Off_unless_engine_rerank_names_a_model_and_nothing_else_is_accepted()
    {
        static RhythmConfig With(string value) => RhythmConfig.Parse(TestSettings.Yaml.Replace("engine: { legacySearch: false }",
            "engine: { legacySearch: false, rerank: " + value + " }", StringComparison.Ordinal));
        Assert.Contains("engine: { legacySearch: false }", TestSettings.Yaml);
        Assert.Null(RecallSettings.From(RhythmConfig.Parse(TestSettings.Yaml)).Rerank);
        Assert.Null(RecallSettings.From(With("off")).Rerank);
        Assert.Same(RerankerModels.MsMarcoMiniLm, RecallSettings.From(With("ms-marco-minilm")).Rerank);
        Assert.Same(RerankerModels.BgeRerankerBase, RecallSettings.From(With("bge-reranker-base")).Rerank);
        var refused = Assert.Throws<ConfigException>(() => With("bge-reranker-large"));
        Assert.Equal("engine.rerank is off, bge-reranker-base or ms-marco-minilm", refused.Message);
        // Off, the engine holds no reranker and no search gets a step.
        Assert.Null(Reranking.Off.Step());
        Assert.Equal("off", Reranking.Off.State);
    }

    [Fact] public async Task A_folder_that_does_not_verify_leaves_it_unverified_and_downloads_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhythm-rerank-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in RerankerModels.MsMarcoMiniLm.Files) await File.WriteAllTextAsync(Path.Combine(folder, file.Name), "DEMO: not a model");
            using var reranking = new Reranking(RerankerModels.MsMarcoMiniLm);
            Assert.Equal("starting", reranking.State);
            await reranking.Start(Path.Combine(folder, "data"), default, name => name == RerankerModels.MsMarcoMiniLm.Variable ? folder : null);
            Assert.Equal("unverified", reranking.State);
            Assert.Null(reranking.Ready);
            Assert.False(Directory.Exists(Path.Combine(folder, "data")));
            Assert.Contains("the reranker is unverified", reranking.Step()!.Apply("boiler", Fused(), default).Notice);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact] public void The_parity_runs_measure_each_reranker_at_the_depths_fixed_first()
    {
        // D-249: each cross-encoder at depth 20 and 10, as a path of its own beside combined retrieval.
        var variants = ParityRecall.Variants("rerank-bge-20,rerank-bge-10,rerank-msmarco-20,rerank-msmarco-10");
        Assert.Equal([(RerankerModels.BgeRerankerBase, 20), (RerankerModels.BgeRerankerBase, 10), (RerankerModels.MsMarcoMiniLm, 20), (RerankerModels.MsMarcoMiniLm, 10)],
            variants.Select(v => (v.Reranker!, v.Depth)));
        Assert.All(variants, v => Assert.Equal(RecallOptions.Default, v.Options));
        Assert.True(ParityRecall.Reranks("combined:rerank-msmarco-10"));
        Assert.False(ParityRecall.Reranks("combined"));
        Assert.False(ParityRecall.Reranks("combined:upstream"));
        Assert.Null(ParityRerankers.Unavailable(null));
        Assert.Null(ParityRecall.Variants("upstream").Single().Reranker);
    }

    [Fact] public void Every_reranker_is_pinned_and_checksummed()
    {
        Assert.Equal(["bge-reranker-base", "ms-marco-minilm"], RerankerModels.All.Select(m => m.Name));
        foreach (var m in RerankerModels.All)
        {
            Assert.Same(m, RerankerModels.Named(m.Name));
            Assert.Matches("^[0-9a-f]{40}$", m.Revision);
            Assert.All(m.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
            Assert.Contains("-" + m.Weights.Sha256[..8] + "-", m.Id);
            Assert.Equal($"https://huggingface.co/{m.Repository}/resolve/{m.Revision}/onnx/model.onnx", m.Url(m.Weights));
            Assert.Equal(512, m.MaxTokens);
        }
        Assert.Equal(RerankerModels.All.Count, RerankerModels.All.Select(m => m.Variable).Distinct().Count());
        // ms-marco-MiniLM's vocabulary is byte for byte MiniLM's.
        Assert.Equal(EmbeddingModels.MiniLm.Vocabulary.Sha256, RerankerModels.MsMarcoMiniLm.Vocabulary.Sha256);
    }
}

// With the pinned files (RHYTHM_RERANKER_DIR_BGE, RHYTHM_RERANKER_DIR_MSMARCO), skipped without them (D-041): each
// cross-encoder loads behind its probe, scores its model card's examples as published, cuts a pair to its limit and
// gives the same score in a batch as alone.
public sealed class RerankerModelTests
{
    sealed class RerankerFactAttribute : FactAttribute
    {
        public RerankerFactAttribute(string name)
        {
            if (ParityRerankers.Unavailable(RerankerModels.Named(name)!) is { } why) Skip = "NOT RUN: " + why;
        }
    }

    [RerankerFact("ms-marco-minilm")] public void Ms_marco_scores_its_cards_example_as_published()
    {
        using var model = new LocalReranker(RerankerModels.Given(RerankerModels.MsMarcoMiniLm)!, RerankerModels.MsMarcoMiniLm);
        var scores = model.Score("How many people live in Berlin?",
            ["Berlin had a population of 3,520,031 registered inhabitants in an area of 891.82 square kilometers.", "Berlin is well known for its museums."], default);
        // The model card's own figures: 8.607138 and -4.320078.
        Assert.Equal(8.607, scores[0], 0.01);
        Assert.Equal(-4.320, scores[1], 0.01);
        Same(model);
    }

    [RerankerFact("bge-reranker-base")] public void Bge_reranker_reads_xlm_roberta_pieces_and_ranks_its_cards_example()
    {
        using var model = new LocalReranker(RerankerModels.Given(RerankerModels.BgeRerankerBase)!, RerankerModels.BgeRerankerBase);
        // XLM-RoBERTa's ids for "Hello world" are 35378 and 8999 (SentencePiece's own, plus one).
        Assert.Equal([35378L, 8999L], model.Ids("Hello world"));
        Assert.Equal([0L, 35378L, 2L, 2L, 8999L, 2L], model.Pair("Hello", "world").Ids);
        var scores = model.Score("what is panda?", ["hi",
            "The giant panda (Ailuropoda melanoleuca), sometimes called a panda bear or simply panda, is a bear species endemic to China."], default);
        Assert.True(scores[1] > 0 && scores[0] < 0, $"{scores[0]} and {scores[1]}");
        Same(model);
    }

    [RerankerFact("ms-marco-minilm")] public async Task It_loads_only_behind_its_probe()
    {
        using var reranking = new Reranking(RerankerModels.MsMarcoMiniLm);
        await reranking.Start(Path.GetTempPath(), default);
        Assert.Equal("on", reranking.State);
        Assert.NotNull(reranking.Ready);
        Assert.Null(reranking.Step()!.Apply("when is the boiler service", [.. Enumerable.Range(0, 3).Select(i =>
            new RecallCandidate(Guid.NewGuid(), new(JsonSerializer.SerializeToElement(new { title = "DEMO " + i }), "DEMO text", 9, "notes", "x" + i), 1))], default).Notice);
    }

    // A long text is cut to the model's limit, the question kept; and a batch, padded, scores as each pair alone.
    static void Same(LocalReranker model)
    {
        var long_ = string.Join(" ", Enumerable.Repeat("DEMO: the boiler service is booked with the heating engineer on Thursday.", 120));
        var (ids, types) = model.Pair("when is the boiler service?", long_);
        Assert.Equal(model.Spec.MaxTokens, ids.Length);
        Assert.Equal(ids.Length, types.Length);
        Assert.Equal(model.Pair("when is the boiler service?", "x").Ids[..8], ids[..8]);
        string[] texts = ["DEMO: the boiler service is on Thursday.", "DEMO: milk", long_, "DEMO: plant the beans in May, after the frost has passed.",
            .. Enumerable.Range(0, 9).Select(i => $"DEMO: note {i} about the garden and the shed")];
        var together = model.Score("when is the boiler service?", texts, default);
        for (var i = 0; i < texts.Length; i++)
            Assert.Equal(model.Score("when is the boiler service?", [texts[i]], default)[0], together[i], 0.001);
    }
}

// Combined retrieval with the step, against a real database: the reranker's order, and with a failing one exactly the
// answer the search gives without one, with the notice.
public sealed class RerankerDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [PostgresFact] public async Task Recall_takes_the_rerankers_order_and_without_it_answers_as_fused()
    {
        var db = RecallRows.Db(fixture.Source);
        var model = new MeaningModel();
        var reader = await Passages.Harness(fixture.Source, "recall-rerank");
        await RecallRows.Add(db, fixture.Source, "Brindle boiler", "The Brindle boiler service is booked for March.", model);
        await RecallRows.Add(db, fixture.Source, "Brindle bake sale", "Brindle bake sale on Saturday.", model);
        await RecallRows.Add(db, fixture.Source, "Brindle radiators", "The Brindle radiators stay as they are.", model);
        var question = new RecallQuery("Brindle", 5);
        var fused = await new Recall(db, model).Search(reader, question, default);
        Assert.Equal(3, fused.Hits.Count);
        // The stand-in prefers what the fused list puts last.
        var last = RecallRows.Title(fused.Hits[^1]);
        var reranker = new WordReranker { Prefer = last };
        var reranked = await new Recall(db, model, rerank: new RecallRerank(() => reranker, () => "on", new RerankBreaker())).Search(reader, question, default);
        Assert.Equal([last, .. fused.Hits.Take(2).Select(RecallRows.Title)], reranked.Hits.Select(RecallRows.Title));
        Assert.Equal(fused.Hits.Select(h => h.Id).Order(), reranked.Hits.Select(h => h.Id).Order());
        Assert.Empty(reranked.Notices);
        reranker.Fails = true;
        var failed = await new Recall(db, model, rerank: new RecallRerank(() => reranker, () => "on", new RerankBreaker())).Search(reader, question, default);
        Assert.Equal(fused.Hits.Select(h => h.Id), failed.Hits.Select(h => h.Id));
        Assert.Equal(fused.Hits.Select(h => h.Score), failed.Hits.Select(h => h.Score));
        Assert.Equal(["Reranking was skipped this time (the reranker failed): the results are in the combined order."], failed.Notices);
    }
}
