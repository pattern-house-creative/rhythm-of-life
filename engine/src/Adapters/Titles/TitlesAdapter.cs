using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knowledge.Adapters.Titles;

// A local title model as a TitleModel adapter (docs/adapters/titles.md): short titles for stars whose title is still
// their excerpt, from an Ollama server in the compose profile `titles`. The server is named by its compose service
// name (adapters.titles.url, default http://titles:11434/): plain http, a host with no dots, so it can never be a
// public name, an IP literal or another machine. adapters.titles.model names the model; there is no default. A
// star's text (its whole text where its source gives one, else its excerpt: CatalogRelationshipSource.Retitle, D-255)
// goes only to that server, never through a proxy or a redirect, and only the cleaned title is kept. The prompt is the
// one the titles evaluation measured (docs/dev/second-brain-parity/titles-eval.md, D-254): a change to the instruction,
// the "Note:\n...\n\nTitle:" frame, MaxInput or the options leaves those numbers behind. A
// failed call throws (the core then stops asking for this pass, keeps excerpts and logs the failure's kind,
// TitleFailures), an answer past 64 KiB as InvalidDataException; unusable output returns null.
// Ported from the private version's local titles, which were configured in the map's own settings file.
public sealed partial class TitlesAdapter(HttpMessageHandler? handler = null) : ITitleModel
{
    public const string AdapterName = "titles";
    const int MaxInput = 6000, MaxWords = 8, MaxLength = 80, MaxAnswer = 65536;
    const string Instruction = "You title personal notes. Reply with only a title for the note: at most 8 words, "
        + "in the note's own language, with no quotes, labels or final period.";
    public string Name => AdapterName;
    public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } = new Dictionary<string, ProviderPolicy>();
    HttpClient? http;
    string model = "";

    public void Configure(AdapterContext context)
    {
        var settings = context.Settings;
        if ((settings.String("provider") ?? "ollama") != "ollama") throw new ConfigException("adapters.titles.provider is ollama");
        model = settings.String("model") ?? "";
        if (!ModelName().IsMatch(model)) throw new ConfigException("adapters.titles.model names the model to use (for example one you pulled into the titles service); there is no default");
        var url = settings.String("url") is { Length: > 0 } given ? given : "http://titles:11434/";
        if (!Valid(url)) throw new ConfigException("adapters.titles.url is http://<compose service name>:<port>/, a service on the internal network");
        // An injected handler is a trusted test transport.
        http = handler is null ? new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) : new(handler, false);
        http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    public static bool Valid(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == "http" && u.UserInfo == ""
        && u.AbsolutePath == "/" && u.Query == "" && u.Fragment == "" && ServiceName().IsMatch(u.Host);

    // Which model titled which text: a new model or a changed text means a new title. It hashes what the model is sent,
    // the text as cut at MaxInput (Input), so a title made from a star's excerpt is made again once the core sends the
    // star's whole text (D-255), and a text that changes only past the cut keeps its title.
    public string Revision(string text) => "title-sha256:" + Hashing.Hash(model + "\n" + Input(text));
    static string Input(string text) => text.Length <= MaxInput ? text : text[..MaxInput];

    public async Task<string?> Title(string text, CancellationToken ct)
    {
        if (http is null) return null;
        var body = JsonSerializer.Serialize(new
        {
            model, stream = false, keep_alive = "5m", system = Instruction,
            prompt = "Note:\n" + Input(text) + "\n\nTitle:",
            options = new { temperature = 0, num_predict = 32 }
        });
        using var response = await http.PostAsync("api/generate", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxAnswer) throw new InvalidDataException("The title model's answer is too large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxAnswer + 1]; var length = 0; int read;
        while (length <= MaxAnswer && (read = await stream.ReadAsync(buffer.AsMemory(length), ct)) > 0) length += read;
        if (length > MaxAnswer) throw new InvalidDataException("The title model's answer is too large");
        using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("response", out var answer)
            && answer.ValueKind == JsonValueKind.String ? Clean(answer.GetString()) : null;
    }

    // The first line, without labels, quotes, markup or a final period; at most 8 words and 80 characters.
    public static string? Clean(string? raw)
    {
        var line = raw?.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        line = Label().Replace(line, "");
        line = new string(line.Where(c => !char.IsControl(c)).ToArray()).Replace("**", "");
        line = line.Trim().Trim('"', '\'', '`', '*', '#', '“', '”', '‘', '’', ' ').TrimEnd('.', '。', ' ');
        if (line.Contains("://", StringComparison.Ordinal) || line.IndexOfAny(['<', '>', '[', ']', '{', '}']) >= 0) return null;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(MaxWords).ToList();
        while (words.Count > 1 && string.Join(' ', words).Length > MaxLength) words.RemoveAt(words.Count - 1);
        line = string.Join(' ', words);
        return line.Length is >= 2 and <= MaxLength ? line : null;
    }

    [GeneratedRegex(@"^(title|titel|título|titre)\s*:\s*", RegexOptions.IgnoreCase)] private static partial Regex Label();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$")] private static partial Regex ServiceName();
    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")] private static partial Regex ModelName();
}
