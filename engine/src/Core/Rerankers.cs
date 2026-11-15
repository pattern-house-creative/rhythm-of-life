using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Knowledge;

// How a cross-encoder cuts its text: BERT's uncased wordpieces from a vocab.txt (ms-marco-MiniLM), or XLM-RoBERTa's
// SentencePiece pieces from its model file (bge-reranker-base), whose ids sit one above the piece's own (fairseq's offset).
public enum RerankerTokens { Wordpiece, SentencePiece }

// A local cross-encoder the engine knows (SB-P2-12, D-026): pinned to a commit of its public repository, each file with
// the SHA-256 it must have before it is loaded. `Name` is what engine.rerank names; `Variable` the environment variable
// for a folder already holding the files. Its ONNX graph takes a question and a text as one input and gives one score,
// higher for a text more relevant to the question.
public sealed record RerankerModel(string Name, string Id, string Repository, string Revision, RerankerTokens Tokens, int MaxTokens,
    string Variable, IReadOnlyList<ModelFile> Files)
{
    public ModelFile Weights => Files[0];
    public ModelFile Vocabulary => Files[1];
    public string Url(ModelFile file) => $"https://huggingface.co/{Repository}/resolve/{Revision}/{file.Remote}";
}

public static class RerankerModels
{
    // BAAI's bge-reranker-base (XLM-RoBERTa base, 278M parameters, MIT), the fp32 ONNX file BAAI publishes, 1.1 GB.
    public static readonly RerankerModel BgeRerankerBase = new("bge-reranker-base", "bge-reranker-base-fp32-15b9a8c3-v1",
        "BAAI/bge-reranker-base", "2cfc18c9415c912f9d8155881c133215df768a70", RerankerTokens.SentencePiece, 512, "RHYTHM_RERANKER_DIR_BGE",
        [new("onnx/model.onnx", "model.onnx", "15b9a8c3da82eddf263df571281166e00e9308fe19d077084b642ebfcaf06d2b"),
         new("sentencepiece.bpe.model", "sentencepiece.bpe.model", "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865")]);

    // ms-marco-MiniLM-L6-v2 (sentence-transformers' cross-encoder, 22.7M parameters, Apache-2.0), its fp32 ONNX file, 91 MB.
    // Its vocabulary is byte for byte MiniLM's.
    public static readonly RerankerModel MsMarcoMiniLm = new("ms-marco-minilm", "ms-marco-minilm-l6-v2-fp32-5d3e70fd-v1",
        "cross-encoder/ms-marco-MiniLM-L6-v2", "233902d25c440f23af6f7d6e94d2946bac0bee0a", RerankerTokens.Wordpiece, 512, "RHYTHM_RERANKER_DIR_MSMARCO",
        [new("onnx/model.onnx", "model.onnx", "5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a"),
         new("vocab.txt", "vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3")]);

    public static IReadOnlyList<RerankerModel> All { get; } = [BgeRerankerBase, MsMarcoMiniLm];
    public static RerankerModel? Named(string? name) => All.FirstOrDefault(m => m.Name == name);

    public static string? Given(RerankerModel model, Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(model.Variable) is { } path && !string.IsNullOrWhiteSpace(path) ? path : null;

    // Whether `directory` holds every file of the model with its pinned checksum. Never throws for a missing file.
    public static bool Verified(RerankerModel model, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try { return model.Files.All(f => File.Exists(Path.Combine(directory, f.Name)) && EmbeddingModels.Checksum(Path.Combine(directory, f.Name)) == f.Sha256); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

// Scores texts against a question: one score a text, in the order given, higher for a more relevant one. The engine's is
// LocalReranker; tests stand in with their own. Throws when it cannot score (the caller keeps its own order then).
public interface IReranker
{
    string Id { get; }
    IReadOnlyList<double> Score(string query, IReadOnlyList<string> texts, CancellationToken ct);
}

// One pinned cross-encoder from the registry, CPU only, in the engine process. No network calls. A pair is the question
// and one text, at most the model's MaxTokens with the text cut first; pairs run in batches of similar length, each
// padded to its own longest, so padding is small and changes no score. A cancelled token stops a running batch
// (RunOptions.Terminate), which then throws.
public sealed class LocalReranker : IReranker, IDisposable
{
    public const int Threads = 4, Batch = 10;
    readonly InferenceSession session;
    readonly BertTokenizer? wordpieces;
    readonly SentencePieceTokenizer? pieces;
    readonly bool tokenTypes;
    readonly object gate = new();
    public RerankerModel Spec { get; }
    public string Id => Spec.Id;

    // Throws InvalidDataException when a checksum differs or the graph is not a cross-encoder's.
    public LocalReranker(string directory, RerankerModel spec, int threads = Threads)
    {
        Spec = spec;
        foreach (var file in spec.Files)
            if (EmbeddingModels.Checksum(Path.Combine(directory, file.Name)) != file.Sha256)
                throw new InvalidDataException("Reranker model checksum mismatch");
        if (spec.Tokens == RerankerTokens.Wordpiece)
            wordpieces = BertTokenizer.Create(Path.Combine(directory, spec.Vocabulary.Name),
                new BertOptions { LowerCaseBeforeTokenization = true, ApplyBasicTokenization = true, RemoveNonSpacingMarks = true });
        else
        {
            using var model = File.OpenRead(Path.Combine(directory, spec.Vocabulary.Name));
            pieces = SentencePieceTokenizer.Create(model, false, false);
        }
        OrtEnv.Instance().DisableTelemetryEvents();
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = threads };
        session = new InferenceSession(Path.Combine(directory, spec.Weights.Name), options);
        var output = session.OutputMetadata.TryGetValue("logits", out var logits) ? logits.Dimensions : null;
        if (output is not { Length: 2 } || output[1] != 1 || !session.InputMetadata.ContainsKey("input_ids") || !session.InputMetadata.ContainsKey("attention_mask"))
        {
            session.Dispose();
            throw new InvalidDataException("Reranker model is not the shape its registry entry says");
        }
        tokenTypes = session.InputMetadata.ContainsKey("token_type_ids");
    }

    // The ids of a text without the special tokens.
    internal long[] Ids(string text) => wordpieces is not null
        ? [.. wordpieces.EncodeToIds(text, addSpecialTokens: false).Select(i => (long)i)]
        // SentencePiece's unknown (0) is XLM-RoBERTa's <unk> (3); every other piece sits one above (fairseq's offset).
        : [.. pieces!.EncodeToIds(text, false, false).Select(i => i == 0 ? 3L : i + 1L)];

    // One pair's ids and token types: [CLS] q [SEP] t [SEP] for wordpieces (types 0 then 1), <s> q </s></s> t </s> for
    // XLM-RoBERTa (no types), the text cut so the pair fits MaxTokens, and the question too if it alone would not leave
    // room for some of the text.
    internal (long[] Ids, long[] Types) Pair(string query, string text)
    {
        var (q, t) = (Ids(query), Ids(text));
        var special = wordpieces is not null ? 3 : 4;
        var room = Spec.MaxTokens - special;
        if (q.Length > room / 2 && q.Length + t.Length > room) q = q[..Math.Max(room / 2, room - t.Length)];
        if (q.Length + t.Length > room) t = t[..(room - q.Length)];
        if (wordpieces is not null)
        {
            long cls = wordpieces.ClassificationTokenId, sep = wordpieces.SeparatorTokenId;
            long[] ids = [cls, .. q, sep, .. t, sep];
            return (ids, [.. ids.Select((_, i) => i <= q.Length + 1 ? 0L : 1L)]);
        }
        long[] xlmr = [0, .. q, 2, 2, .. t, 2];
        return (xlmr, new long[xlmr.Length]);
    }

    public IReadOnlyList<double> Score(string query, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var pairs = texts.Select(t => Pair(query, t)).ToArray();
        var scores = new double[pairs.Length];
        long pad = wordpieces is not null ? 0 : 1;
        // Shortest first, so each batch's pairs are of similar length.
        var order = Enumerable.Range(0, pairs.Length).OrderBy(i => pairs[i].Ids.Length).ThenBy(i => i).ToArray();
        for (var start = 0; start < order.Length; start += Batch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = order.Skip(start).Take(Batch).ToArray();
            var width = batch.Max(i => pairs[i].Ids.Length);
            var ids = new long[batch.Length * width];
            var mask = new long[ids.Length];
            var types = new long[ids.Length];
            Array.Fill(ids, pad);
            for (var b = 0; b < batch.Length; b++)
            {
                var (pairIds, pairTypes) = pairs[batch[b]];
                Array.Copy(pairIds, 0, ids, b * width, pairIds.Length);
                Array.Copy(pairTypes, 0, types, b * width, pairTypes.Length);
                Array.Fill(mask, 1L, b * width, pairIds.Length);
            }
            var shape = new[] { batch.Length, width };
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
            };
            if (tokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, shape)));
            lock (gate)
            {
                using var run = new RunOptions();
                using var stop = ct.Register(() => run.Terminate = true);
                using var results = session.Run(inputs, ["logits"], run);
                var logits = results.First().AsTensor<float>();
                for (var b = 0; b < batch.Length; b++)
                {
                    var value = logits[b, 0];
                    if (!float.IsFinite(value)) throw new InvalidDataException("Invalid reranker score");
                    scores[batch[b]] = value;
                }
            }
        }
        return scores;
    }

    public void Dispose() => session.Dispose();
}

// A circuit breaker for the reranker: after Failures failures in a row it opens for Cooldown, and while open no search
// asks the model; after the cooldown one search may try again (half open), and a success closes it, a failure opens it
// for another cooldown. A failure is an error, an unreadable score or a call past the time budget.
public sealed class RerankBreaker(int failures = 3, TimeSpan? cooldown = null, TimeProvider? clock = null)
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(1);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly TimeSpan wait = cooldown ?? DefaultCooldown;
    readonly object gate = new();
    int inARow;
    DateTimeOffset? openUntil;
    public static RerankBreaker Never { get; } = new(int.MaxValue);

    public bool Open { get { lock (gate) return openUntil is { } until && time.GetUtcNow() < until; } }
    public void Succeeded() { lock (gate) { inARow = 0; openUntil = null; } }
    public void Failed()
    {
        lock (gate)
        {
            inARow++;
            // Past the cooldown a failure (the half-open trial) opens it again at once.
            if (inARow >= failures || openUntil is not null) openUntil = time.GetUtcNow() + wait;
        }
    }
}

// The reranker step of one search (Recall): `model` gives the reranker while it is ready (null while it is loading, off
// or failed), `state` its state for the notice. Applied after fusion and the diversity step, before any hop: the first
// Depth results are scored against the question, each as its title and, on the next line, the text its excerpt comes
// from (the nearest passage, else the keyword arm's passage, else the start of its text), cut at TextChars; they take
// the order of their scores, best first, ties in the fused order, and the rest stay as fused. The fused score is kept.
// The model is asked only while the breaker is closed, within Budget; anything else returns the list as fused, with a
// notice. The rules were fixed before the gate measured them (D-249).
public sealed class RecallRerank(Func<IReranker?> model, Func<string> state, RerankBreaker breaker, int depth = RecallRerank.DefaultDepth,
    TimeSpan? budget = null)
{
    public const int DefaultDepth = 20, TextChars = 1200;
    // A call past this is stopped and counts as a failure: a safety net well past D-029's 800 ms for the whole search.
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);
    public int Depth { get; } = depth;
    readonly TimeSpan limit = budget ?? DefaultBudget;

    internal static string Text(RecallCandidate c)
    {
        var title = c.Row.Row.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() ?? "" : "";
        var text = c.Meaning?.Passage is { Length: > 0 } passage ? passage : c.Keyword?.Passage is { Length: > 0 } words ? words : c.Row.Head;
        var whole = title.Length == 0 ? text : text.Length == 0 ? title : title + "\n" + text;
        return Recall.Cut(whole, TextChars);
    }

    internal (List<RecallCandidate> List, string? Notice) Apply(string query, List<RecallCandidate> ordered, CancellationToken ct)
    {
        if (ordered.Count <= 1) return (ordered, null);
        const string skipped = "Reranking was skipped this time ({0}): the results are in the combined order.";
        if (breaker.Open) return (ordered, string.Format(System.Globalization.CultureInfo.InvariantCulture, skipped, "the reranker failed several times in a row and rests for a minute"));
        if (model() is not { } reranker) return (ordered, string.Format(System.Globalization.CultureInfo.InvariantCulture, skipped, $"the reranker is {state()}"));
        var head = ordered.Take(Depth).ToList();
        IReadOnlyList<double> scores;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limit != Timeout.InfiniteTimeSpan) timeout.CancelAfter(limit);
        try
        {
            scores = reranker.Score(query, [.. head.Select(Text)], timeout.Token);
            if (scores.Count != head.Count || scores.Any(s => !double.IsFinite(s))) throw new InvalidDataException("Invalid reranker scores");
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is OnnxRuntimeException or InvalidDataException or OperationCanceledException
            or ArgumentException or InvalidOperationException)
        {
            breaker.Failed();
            return (ordered, string.Format(System.Globalization.CultureInfo.InvariantCulture, skipped, "the reranker failed"));
        }
        breaker.Succeeded();
        var reranked = head.Select((c, i) => (c, i)).OrderByDescending(x => scores[x.i]).ThenBy(x => x.i).Select(x => x.c);
        return ([.. reranked, .. ordered.Skip(Depth)], null);
    }
}

// The engine's reranker (engine.rerank, off by default): the named model loaded from the folder its environment variable
// names, else <data>/models/<name>, downloaded there once from its repository at the pinned revision when missing, every
// file checked against its SHA-256. Before it is used, a readiness probe: it must score a pair about the probe's
// question above one that is not (Ready). States: off, starting, downloading, download-failed, unverified (a checksum or
// the graph differs), probe-failed, on. Nothing loads and nothing is downloaded while the setting is off.
public sealed class Reranking(RerankerModel? spec) : IDisposable
{
    static readonly (string Query, string Yes, string No) Probe = ("when is the boiler service?",
        "The boiler service is booked for Thursday morning.", "The cat's flea treatment is due next week.");
    readonly object gate = new();
    LocalReranker? model;
    public RerankerModel? Spec { get; } = spec;
    public string State { get; private set; } = spec is null ? "off" : "starting";
    public RerankBreaker Breaker { get; } = new();
    public IReranker? Ready { get { lock (gate) return State == "on" ? model : null; } }
    public static Reranking Off { get; } = new(null);

    // The step one search reads it through, or null while the setting is off.
    public RecallRerank? Step() => Spec is null ? null : new(() => Ready, () => State, Breaker);

    public bool TryLoad(string directory)
    {
        if (Spec is null) return false;
        LocalReranker loaded;
        try { loaded = new LocalReranker(directory, Spec); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OnnxRuntimeException or ArgumentException)
        { State = "unverified"; return false; }
        try
        {
            var scores = loaded.Score(Probe.Query, [Probe.Yes, Probe.No], default);
            if (!(scores[0] > scores[1])) throw new InvalidDataException("The reranker failed its probe");
        }
        catch (Exception e) when (e is OnnxRuntimeException or InvalidDataException)
        {
            loaded.Dispose();
            State = "probe-failed";
            return false;
        }
        lock (gate) { model?.Dispose(); model = loaded; State = "on"; }
        return true;
    }

    public async Task Start(string dataDirectory, CancellationToken ct, Func<string, string?>? environment = null)
    {
        if (Spec is null) return;
        var given = RerankerModels.Given(Spec, environment);
        var directory = given ?? Path.Combine(dataDirectory, "models", Spec.Name);
        // As the embeddings model opens (Embeddings.Open): a folder the environment names is never downloaded into.
        if (TryLoad(directory) || given is not null) return;
        State = "downloading";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (await Fetch(Spec, directory, http, ct)) TryLoad(directory);
        else State = "download-failed";
    }

    // Streams each missing file into `directory`, keeping it only when its SHA-256 matches. Never throws.
    public static async Task<bool> Fetch(RerankerModel spec, string directory, HttpClient http, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in spec.Files)
            {
                var target = Path.Combine(directory, file.Name);
                if (File.Exists(target) && EmbeddingModels.Checksum(target) == file.Sha256) continue;
                var temporary = target + ".download";
                using (var response = await http.GetAsync(spec.Url(file), HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(temporary);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    {
                        output.Close();
                        File.Delete(temporary);
                        return false;
                    }
                }
                File.Move(temporary, target, true);
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException) { return false; }
    }

    public void Dispose() { lock (gate) { model?.Dispose(); model = null; } }
}
