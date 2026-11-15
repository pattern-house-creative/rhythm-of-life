using System.Net;
using System.Text;
using System.Text.Json;
using Knowledge.Adapters;
using Knowledge.Adapters.Titles;
using Knowledge.Relationships;

namespace Knowledge.Tests;

// A fictional Ollama generate API, in process. Every request ends here.
internal sealed class FakeOllama : HttpMessageHandler
{
    public readonly List<string> Prompts = [], Models = [];
    public bool Down;
    public Func<string, string> Reply = _ => "A short title";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("http://titles:11434/api/generate", request.RequestUri!.ToString());
        if (Down) return new(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        var prompt = body.RootElement.GetProperty("prompt").GetString()!;
        Prompts.Add(prompt); Models.Add(body.RootElement.GetProperty("model").GetString()!);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { response = Reply(prompt), done = true }), Encoding.UTF8, "application/json") };
    }
}

public sealed class TitlesTests
{
    static TitlesAdapter Configured(HttpMessageHandler handler, Dictionary<string, object?> values)
    {
        values["enabled"] = "true";
        var titles = new TitlesAdapter(handler);
        titles.Configure(new(new AdapterSettings("titles", values), TestSettings.Use(),
            new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none")), new Archive(Path.GetTempPath())));
        return titles;
    }

    [Theory]
    [InlineData("Title: \"Spring garden plan.\"", "Spring garden plan")]
    [InlineData("**Weekly budget review**\nA second line", "Weekly budget review")]
    [InlineData("one two three four five six seven eight nine ten", "one two three four five six seven eight")]
    [InlineData("See https://example.invalid/x", null)]
    [InlineData("<think>hmm</think>", null)]
    [InlineData("   \n  ", null)]
    public void Model_output_becomes_a_clean_short_title(string raw, string? expected) => Assert.Equal(expected, TitlesAdapter.Clean(raw));

    [Theory]
    [InlineData("http://titles:11434/", true)]
    [InlineData("http://titles:11434", true)]
    [InlineData("http://titles.example.invalid:11434/", false)]
    [InlineData("http://10.0.0.5:11434/", false)]
    [InlineData("https://titles:11434/", false)]
    [InlineData("http://user@titles:11434/", false)]
    [InlineData("http://titles:11434/v1", false)]
    public void The_model_server_must_be_a_service_on_the_internal_network(string endpoint, bool valid) =>
        Assert.Equal(valid, TitlesAdapter.Valid(endpoint));

    [Fact] public void There_is_no_default_model_and_only_ollama()
    {
        Assert.Contains("model", Assert.Throws<ConfigException>(() => Configured(new FakeOllama(), [])).Message);
        Assert.Contains("provider", Assert.Throws<ConfigException>(() => Configured(new FakeOllama(), new() { ["provider"] = "other", ["model"] = "m" })).Message);
        Assert.Contains("url", Assert.Throws<ConfigException>(() => Configured(new FakeOllama(), new() { ["model"] = "m", ["url"] = "http://titles.example.invalid/" })).Message);
    }

    // A memory source whose records have no titles of their own: each title is still its excerpt.
    sealed class Untitled(params string[] texts) : IMemorySource
    {
        public string Name => "untitled";
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
            new Dictionary<string, ProviderPolicy> { ["untitled"] = new(SourceAuthority.AuthorizedArchive, "household") };
        public Task<SourceListing> List(SourcePass pass, CancellationToken ct) => Task.FromResult(new SourceListing(texts.Select((t, i) =>
            new DiscoveryRecord(new("sam", "untitled", "untitled", "m" + i), "private", "memory", t, null, "r" + t.Length, null, DateTimeOffset.UtcNow, [],
                Overview: t)).ToArray(), true));
        public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact] public async Task Excerpt_titled_stars_get_a_model_title_once_per_text_and_keep_the_excerpt_when_it_is_down()
    {
        var config = TestSettings.Use();
        var ollama = new FakeOllama { Reply = p => p.Contains("compost") ? "Title: Compost for the raised beds." : "<b>unusable</b>" };
        var titles = Configured(ollama, new() { ["model"] = "fictional-model:1b" });
        var registry = new AdapterRegistry([new Untitled("Raised beds need compost before spring", "Call the plumber about the boiler"), titles]);
        var principal = Guid.NewGuid();
        var host = RelationshipHostConfiguration.From(config, principal, Path.GetTempPath());
        CatalogRelationshipSource Source() => new(host, registry, (_, _) => throw new InvalidOperationException("no catalog in this test"));
        var first = (await Source().Fetch(principal, default)).Records;
        Assert.Equal("Compost for the raised beds", first[0].Title);
        Assert.StartsWith("title-sha256:", first[0].TitleRevision);
        Assert.Equal("Call the plumber about the boiler", first[1].Title);
        Assert.Equal(["fictional-model:1b", "fictional-model:1b"], ollama.Models);
        // The next pass reuses the stored title for unchanged text and asks again only for the one still untitled.
        var again = Source();
        again.Prime(first);
        ollama.Prompts.Clear();
        Assert.Equal("Compost for the raised beds", (await again.Fetch(principal, default)).Records[0].Title);
        Assert.DoesNotContain(ollama.Prompts, p => p.Contains("compost"));
        // Down: the excerpts stand and the pass still completes.
        ollama.Down = true;
        var down = (await Source().Fetch(principal, default)).Records;
        Assert.Equal(["Raised beds need compost before spring", "Call the plumber about the boiler"], down.Select(r => r.Title));
    }
}
