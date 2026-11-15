using System.Text;
using System.Text.Json;

namespace Knowledge.Tests.Parity;

// SB-P4-12 idea 5, the near-miss check (D-217), with the rules fixed before it was measured (D-249): the
// pairs of RelevanceIdeas.NearMisses (today's map leaves them out, they score from 0.40 up to the similar threshold, at
// most 5 a star) get a second look from a reviewer, and the map would keep only those it keeps. Two kinds of reviewer:
//   a cross-encoder (RerankerModels): each pair scored in both orders, each record as its title and text as the map's
//     hook reads them, each cut at TextChars, a pair at most the model's 512 tokens with the longer text cut first
//     (LocalReranker.Pair); kept when the mean of the two scores is at least 0, the model's own probability 0.5;
//   a language model through the titles service's Ollama (the titles model, qwen3:4b-instruct-2507-q4_K_M): asked once a
//     pair, its first id first, with Instruction and Prompt below, Ollama's JSON format, temperature 0, at most 16
//     tokens; kept when the answer is {"related": true}; a failed or unreadable answer turns the pair down.
// No reviewer is asked for a reason and none is kept (the ideas page's question 1 stays the owner's).
public static class NearMissReview
{
    public const int TextChars = 1200;

    public static string Text(ParityText t) => Recall.Cut(t.Title.Length == 0 ? t.Text : t.Title + "\n" + t.Text, TextChars);

    // The cross-encoder's verdict on one pair and the two scores it rests on.
    public static (bool Kept, double AB, double BA) CrossEncoder(IReranker reranker, ParityText a, ParityText b)
    {
        var (ta, tb) = (Text(a), Text(b));
        // Each order once, one record as the question and the other as the text (LocalReranker.Pair cuts the longer first).
        var ab = reranker.Score(ta, [tb], default)[0];
        var ba = reranker.Score(tb, [ta], default)[0];
        return ((ab + ba) / 2 >= 0, ab, ba);
    }

    public const string Instruction = "You compare two records from one household's notes and memories. Decide whether a person "
        + "reading one of them should be shown the other as connected. Answer true only when both are about the same specific "
        + "thing (the same plan, event, purchase, place, object, illness, job or piece of work), or one changes, causes or "
        + "explains the other. Answer false when they only share a topic, a common word, a name or a person. Reply with JSON only.";

    public static string Prompt(ParityText a, ParityText b) => $"Record A:\n{Text(a)}\n\nRecord B:\n{Text(b)}\n\nAre A and B connected?";

    // Ollama's structured output: an object with one boolean, nothing else.
    static readonly object Format = new
    {
        type = "object",
        properties = new { related = new { type = "boolean" } },
        required = new[] { "related" },
    };

    // One pair asked of the model at `url` (the evaluation's loopback port): true, false, or null when the call failed or
    // the answer was not the JSON asked for (which turns the pair down).
    public static async Task<bool?> Ask(HttpClient http, string model, ParityText a, ParityText b, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model, stream = false, keep_alive = "5m", system = Instruction, prompt = Prompt(a, b), format = Format,
            options = new { temperature = 0, num_predict = 16 },
        });
        try
        {
            using var response = await http.PostAsync("api/generate", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("response", out var answer) || answer.ValueKind != JsonValueKind.String) return null;
            using var verdict = JsonDocument.Parse(answer.GetString()!);
            return verdict.RootElement.ValueKind == JsonValueKind.Object && verdict.RootElement.TryGetProperty("related", out var related)
                && related.ValueKind is JsonValueKind.True or JsonValueKind.False ? related.GetBoolean() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }
}
