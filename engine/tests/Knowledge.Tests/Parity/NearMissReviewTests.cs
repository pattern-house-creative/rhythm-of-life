using System.Globalization;
using Xunit.Abstractions;

namespace Knowledge.Tests.Parity;

// A test that needs MiniLM's pinned files and every listed reranker's (each from the folder its variable names), skipped,
// saying so, without them: tests never download a model (D-041).
public sealed class RerankersFactAttribute : FactAttribute
{
    public RerankersFactAttribute(params string[] names)
    {
        if (new ModelsFactAttribute("minilm").Skip is { } minilm) { Skip = minilm; return; }
        foreach (var name in names)
            if (ParityRerankers.Unavailable(RerankerModels.Named(name) ?? throw new ArgumentException("Unknown reranker " + name)) is { } why)
            {
                Skip = "NOT RUN: " + why;
                return;
            }
    }
}

// SB-P4-12 idea 5 (D-217), measured by the rules D-249 fixed first (NearMissReview): each cross-encoder reviews the band
// of near misses, and the pairs it keeps join today's map; relevance.md's keep rule decides. Recomputed from the pinned
// model files, skipped without them.
public sealed class NearMissReviewTests(ITestOutputHelper output)
{
    // (true positives, false positives, false negatives, true negatives) on the 124 labelled pairs the map may join:
    // the reviewer's kept pairs alone, and today's map with them; and how many of the band's 114 pairs each keeps.
    static readonly Dictionary<string, (Confusion Alone, Confusion WithToday, int Kept)> Recorded = new()
    {
        ["bge-reranker-base"] = (new(1, 0, 72, 51), new(53, 7, 20, 44), 3),
        ["ms-marco-minilm"] = (new(1, 3, 72, 48), new(53, 10, 20, 41), 7),
    };

    static string N(double? x) => x is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "–";
    void Line(string name, Confusion c) => output.WriteLine(
        $"{name,-40} P {N(c.Precision)} R {N(c.Recall)} ({c.TruePositives}, {c.FalsePositives}, {c.FalseNegatives}, {c.TrueNegatives})");

    // The band and today's map, as RelevanceIdeasTests counts them (the stars as the map lists them).
    internal static async Task<(IReadOnlyList<ParityPair> Pairs, HashSet<(string, string)> Today, HashSet<(string, string)> Band, ParityCorpus Corpus)> Band()
    {
        using var embeddings = new Embeddings(EmbeddingModels.MiniLm);
        Assert.True(embeddings.TryLoad(EmbeddingModels.Given(EmbeddingModels.MiniLm)!), "the pinned MiniLM files did not verify");
        var corpus = ParityCorpus.Load();
        var pairs = RelevanceIdeas.MapPairs(corpus, ParityPairs.Load());
        var stars = RelevanceIdeas.Stars(corpus, embeddings.Model!, EmbeddingModels.MiniLmId, references: true);
        var today = RelevanceIdeas.Union(await RelevanceIdeas.Linked(RelevanceIdeas.Today(), stars), RelevanceIdeas.Stated(corpus));
        var band = RelevanceIdeas.NearMisses(stars, today, RelevanceIdeas.SimilarThreshold);
        return (pairs, today, band, corpus);
    }

    [RerankersFact("bge-reranker-base", "ms-marco-minilm")] public async Task Each_cross_encoder_reviews_the_near_misses()
    {
        var (pairs, today, band, corpus) = await Band();
        Assert.Equal(114, band.Count);
        var texts = corpus.Texts.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var labels = pairs.ToDictionary(p => RelevanceIdeas.Pair(p.A, p.B), p => p.Label);
        var todayCount = RelevanceIdeas.Count(pairs, today);
        Line("today's map", todayCount);
        foreach (var model in RerankerModels.All)
        {
            var reranker = ParityRerankers.Model(model)!;
            var kept = new HashSet<(string, string)>();
            foreach (var (a, b) in band.Order())
            {
                var (keep, ab, ba) = NearMissReview.CrossEncoder(reranker, texts[a], texts[b]);
                if (keep) kept.Add((a, b));
                if (labels.TryGetValue((a, b), out var label))
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {model.Name} {label,-14} {(keep ? "kept" : "    ")} {ab,7:0.000} {ba,7:0.000} {a} {b}"));
            }
            var alone = RelevanceIdeas.Count(pairs, kept);
            var with = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(today, kept));
            Line(model.Name + ", kept pairs alone", alone);
            Line(model.Name + ", today's map with them", with);
            output.WriteLine($"{model.Name}: keeps {kept.Count} of the band's {band.Count}; keep rule {(RelevanceIdeas.Kept(todayCount, with) ? "passes" : "fails")}");
            Assert.Equal(Recorded[model.Name], (alone, with, kept.Count));
        }
    }
}

// Opt-in, never part of an ordinary run: the language-model reviewer (the titles model) on the same band, through an
// Ollama server the titles evaluation's compose project runs on a loopback port (engine/titles-eval.sh's, with
// RHYTHM_TITLES_EVAL_KEEP=1 so the model stays in its volume):
//   RHYTHM_NEAR_MISS_REVIEW=1 RHYTHM_NEAR_MISS_URL=http://localhost:11534/ RHYTHM_NEAR_MISS_MODEL=qwen3:4b-instruct-2507-q4_K_M
//   RHYTHM_MODEL_DIR=<MiniLM's files> dotnet test engine --filter NearMissLanguageModelRun
// It writes near-miss-review.json to RHYTHM_PARITY_OUT (pair ids, labels, verdicts and times; no text, no reason).
public sealed class NearMissLanguageModelFactAttribute : FactAttribute
{
    public NearMissLanguageModelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RHYTHM_NEAR_MISS_REVIEW") != "1")
            Skip = "NOT RUN: opt-in near-miss review by a language model (D-249); RHYTHM_NEAR_MISS_REVIEW=1 RHYTHM_NEAR_MISS_URL=<an Ollama server> RHYTHM_NEAR_MISS_MODEL=<model> (docs/dev/second-brain-parity/relevance.md).";
        else if (new ModelsFactAttribute("minilm").Skip is { } minilm) Skip = minilm;
    }
}

public sealed class NearMissLanguageModelRun(ITestOutputHelper output)
{
    [NearMissLanguageModelFact] public async Task The_titles_model_reviews_the_near_misses()
    {
        var url = Environment.GetEnvironmentVariable("RHYTHM_NEAR_MISS_URL") is { Length: > 0 } u ? u : "http://localhost:11534/";
        var model = Environment.GetEnvironmentVariable("RHYTHM_NEAR_MISS_MODEL") is { Length: > 0 } m ? m : "qwen3:4b-instruct-2507-q4_K_M";
        var (pairs, today, band, corpus) = await NearMissReviewTests.Band();
        Assert.Equal(114, band.Count);
        var texts = corpus.Texts.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var labels = pairs.ToDictionary(p => RelevanceIdeas.Pair(p.A, p.B), p => p.Label);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        // A first call loads the model; it is not timed with the band.
        var warm = System.Diagnostics.Stopwatch.GetTimestamp();
        await NearMissReview.Ask(http, model, texts[band.Order().First().Item1], texts[band.Order().First().Item2], default);
        var warmMs = System.Diagnostics.Stopwatch.GetElapsedTime(warm).TotalMilliseconds;
        var kept = new HashSet<(string, string)>();
        var rows = new System.Text.Json.Nodes.JsonArray();
        var times = new List<double>();
        var failed = 0;
        foreach (var (a, b) in band.Order())
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var verdict = await NearMissReview.Ask(http, model, texts[a], texts[b], default);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            times.Add(ms);
            if (verdict is null) failed++;
            if (verdict == true) kept.Add((a, b));
            var label = labels.GetValueOrDefault((a, b));
            rows.Add(new System.Text.Json.Nodes.JsonObject { ["a"] = a, ["b"] = b, ["label"] = label, ["verdict"] = verdict, ["ms"] = Math.Round(ms) });
            if (label is not null) output.WriteLine($"  {label,-14} {(verdict == true ? "kept" : verdict == false ? "    " : "FAIL")} {a} {b}");
        }
        var todayCount = RelevanceIdeas.Count(pairs, today);
        var alone = RelevanceIdeas.Count(pairs, kept);
        var with = RelevanceIdeas.Count(pairs, RelevanceIdeas.Union(today, kept));
        var sorted = times.Order().ToArray();
        double P(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{model}: keeps {kept.Count} of {band.Count}, {failed} failed; alone {alone.TruePositives}, {alone.FalsePositives}, {alone.FalseNegatives}, {alone.TrueNegatives}; "
            + $"with today's map {with.TruePositives}, {with.FalsePositives}, {with.FalseNegatives}, {with.TrueNegatives} (today {todayCount.TruePositives}, {todayCount.FalsePositives}); "
            + $"keep rule {(RelevanceIdeas.Kept(todayCount, with) ? "passes" : "fails")}; per pair p50 {P(0.5):0} ms, p95 {P(0.95):0} ms, total {times.Sum() / 1000:0} s; first call {warmMs:0} ms");
        output.WriteLine(summary);
        var folder = Environment.GetEnvironmentVariable("RHYTHM_PARITY_OUT") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "parity-report");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "near-miss-review.json"), new System.Text.Json.Nodes.JsonObject
        {
            ["label"] = "DEMO: synthetic corpus only", ["model"] = model, ["summary"] = summary, ["pairs"] = rows,
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failed <= band.Count / 20, $"{failed} of {band.Count} answers failed");
    }
}
