using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Knowledge;

// What indexes passages: a model's id (the tag every vector it makes is stored with), how it cuts a text into passages,
// and how it embeds one. LocalEmbeddings is the engine's; tests stand in with their own.
public interface IEmbedder
{
    string Id { get; }
    float[] Embed(string text);
    IEnumerable<string> Chunks(string content);
}

// One pinned, CPU-only model from the registry (EmbeddingModels) in the engine process. No inference network calls.
public sealed class LocalEmbeddings : IEmbedder, IDisposable
{
    // MiniLM's, kept for the code and tests that name them.
    public const string Model = EmbeddingModels.MiniLmId;
    public const string ModelSha256 = "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1";
    public const string VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";
    const string Output = "last_hidden_state";
    readonly InferenceSession session;
    readonly BertTokenizer tokenizer;
    readonly bool tokenTypes;
    readonly object gate = new();
    public EmbeddingModel Spec { get; }
    public string Id => Spec.Id;

    public LocalEmbeddings(string directory) : this(directory, EmbeddingModels.MiniLm) { }

    // Loads the model's files from `directory` after checking each one's pinned SHA-256; throws InvalidDataException when
    // a checksum differs or the model is not the shape the registry says.
    public LocalEmbeddings(string directory, EmbeddingModel spec)
    {
        Spec = spec;
        foreach (var file in spec.Files)
            if (EmbeddingModels.Checksum(Path.Combine(directory, file.Name)) != file.Sha256)
                throw new InvalidDataException("Embedding model checksum mismatch");
        tokenizer = BertTokenizer.Create(Path.Combine(directory, spec.Vocabulary.Name),
            new BertOptions { LowerCaseBeforeTokenization = true, ApplyBasicTokenization = true, RemoveNonSpacingMarks = true });
        // No telemetry from the inference runtime: nothing about the install leaves it.
        OrtEnv.Instance().DisableTelemetryEvents();
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 2 };
        session = new InferenceSession(Path.Combine(directory, spec.Weights.Name), options);
        var size = session.OutputMetadata.TryGetValue(Output, out var output) ? output.Dimensions : null;
        if (size is not { Length: 3 } || size[2] > 0 && size[2] != spec.Dimensions
            || !session.InputMetadata.ContainsKey("input_ids") || !session.InputMetadata.ContainsKey("attention_mask"))
        {
            session.Dispose();
            throw new InvalidDataException("Embedding model is not the shape its registry entry says");
        }
        tokenTypes = session.InputMetadata.ContainsKey("token_type_ids");
    }

    public float[] Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12000) throw new ArgumentException("Embedding text must be 1–12000 characters");
        // Never silently truncate a chunk: past the model's own limit (256 wordpieces for MiniLM, which it was trained
        // on; 512 for bge), the text is refused.
        var ids = tokenizer.EncodeToIds(text, addSpecialTokens: true).Select(x => (long)x).ToArray();
        if (ids.Length > Spec.MaxTokens) throw new ArgumentException("Embedding text exceeds model token limit");
        var shape = new[] { 1, ids.Length };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), shape)),
        };
        if (tokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[ids.Length], shape)));
        lock (gate)
        {
            using var results = session.Run(inputs);
            var values = results.First(x => x.Name == Output).AsTensor<float>();
            var width = Spec.Dimensions;
            if (values.Dimensions.Length != 3 || values.Dimensions[2] != width) throw new InvalidDataException("Invalid embedding");
            var vector = new float[width];
            if (Spec.Pooling == Pooling.Cls)
                for (var j = 0; j < width; j++) vector[j] = values[0, 0, j];
            else
                for (var token = 0; token < ids.Length; token++) for (var j = 0; j < width; j++) vector[j] += values[0, token, j] / ids.Length;
            var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
            if (!double.IsFinite(norm) || norm == 0) throw new InvalidDataException("Invalid embedding");
            for (var j = 0; j < vector.Length; j++) vector[j] = (float)(vector[j] / norm);
            return vector;
        }
    }

    public IEnumerable<string> Chunks(string content)
    {
        // Keep source characters intact; tokenizer offsets can refer to normalized text.
        var offset = 0;
        while (offset < content.Length)
        {
            var consumed = Math.Min(1000, content.Length - offset);
            while (tokenizer.EncodeToIds(content.Substring(offset, consumed), addSpecialTokens: true).Count > 200) consumed /= 2;
            if (consumed > 1 && char.IsHighSurrogate(content[offset + consumed - 1])) consumed--;
            if (offset + consumed < content.Length)
            {
                var boundary = content.LastIndexOfAny([' ', '\n', '\t'], offset + consumed - 1, Math.Min(consumed, 150));
                if (boundary >= offset) consumed = boundary - offset + 1;
            }
            var part = content.Substring(offset, consumed);
            if (!string.IsNullOrWhiteSpace(part)) yield return part;
            offset += consumed;
        }
    }

    public void Dispose() => session.Dispose();
}
