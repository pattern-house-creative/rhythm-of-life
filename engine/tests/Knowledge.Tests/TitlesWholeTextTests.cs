using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowledge.Adapters;
using Knowledge.Adapters.Notes;
using Knowledge.Adapters.SecondBrain;
using Knowledge.Adapters.Titles;
using Knowledge.Relationships;
using Knowledge.Tests.Parity;

namespace Knowledge.Tests;

// D-255 (SB-P2-14): the titles model is given a star's whole text where its source has one (every native memory star's,
// drafts and replaced facts included; a note's; a Second Brain memory's), every other star its excerpt as before. The
// request is the one the evaluation's whole-text input measured (D-254), the revision hashes what is sent, and the whole
// text is held beside the records for the pass only: never stored, logged or served. Fictional people and DEMO texts
// only; every request to a titles server ends in process.
internal sealed class TitlesServer : HttpMessageHandler
{
    public readonly List<byte[]> Raw = [];
    public Func<string, string> Reply = _ => "DEMO model title";
    public Func<HttpResponseMessage>? Failure;
    public IReadOnlyList<string> Prompts => [.. Raw.Select(Prompt)];
    static string Prompt(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("prompt").GetString()!;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("http://titles:11434/api/generate", request.RequestUri!.ToString());
        var body = await request.Content!.ReadAsByteArrayAsync(ct);
        lock (Raw) Raw.Add(body);
        if (Failure is { } fail) return fail();
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { response = Reply(Prompt(body)), done = true }), Encoding.UTF8, "application/json")
        };
    }
    public static TitlesAdapter Adapter(HttpMessageHandler handler, string model = "fictional-model:1b")
    {
        var titles = new TitlesAdapter(handler);
        titles.Configure(new(new AdapterSettings("titles", new Dictionary<string, object?> { ["enabled"] = "true", ["model"] = model }),
            TestSettings.Use(), new Database(Npgsql.NpgsqlDataSource.Create("Host=example.invalid;Database=none")), new Archive(Path.GetTempPath())));
        return titles;
    }
    public static string Frame(string text) => "Note:\n" + text + "\n\nTitle:";
}

public sealed class TitlesWholeTextTests
{
    static readonly Guid Principal = Guid.NewGuid();
    static readonly DateTimeOffset Then = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    // A native memory as the pass reads it (CatalogReader.Inventory), newest first by its number.
    static CatalogMemory Memory(int i, string text, string status = "canonical", bool replaced = false, string subject = "household") =>
        new(new Guid($"00000000-0000-4000-8000-{i:D12}"), subject, subject == "household" ? "shared" : "private", text, [], status, null, 1, i,
            Then.AddMinutes(-i), Then.AddMinutes(-i), Replaced: replaced);
    static CatalogInventory With(params CatalogMemory[] memories) => new([], [], Memories: memories, Projects: []);
    static CatalogRelationshipSource Source(CatalogInventory inventory, params IAdapter[] adapters) => Source(inventory, false, adapters);
    static CatalogRelationshipSource Source(CatalogInventory inventory, bool scoped, params IAdapter[] adapters)
    {
        var settings = RelationshipHostConfiguration.From(TestSettings.Use(), Principal, Path.GetTempPath());
        if (scoped) settings = settings with { Catalog = settings.Catalog with { Scoped = true } };
        return new(settings, new AdapterRegistry(adapters), (_, _) => Task.FromResult(inventory));
    }
    static DiscoveryRecord Star(DiscoveryBatch batch, CatalogMemory m) => batch.Records.Single(r => r.Key == MemoryStars.Key(m.Subject, m.Id));

    // A memory source of one subject whose records' titles are their excerpts and that has no whole text to give, except
    // `foreign`: a text it tries to give for another source's record, which the pass must ignore.
    sealed class Excerpts(string subject, string[] excerpts, DiscoveryRecord? foreign = null) : IMemorySource
    {
        public string Name => "excerpts";
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
            new Dictionary<string, ProviderPolicy> { ["excerpts"] = new(SourceAuthority.AuthorizedArchive, "household") };
        public Task<SourceListing> List(SourcePass pass, CancellationToken ct)
        {
            if (foreign is not null) pass.TitleText(foreign, "DEMO foreign text qz7799k");
            return Task.FromResult(new SourceListing([.. excerpts.Select((e, i) => new DiscoveryRecord(new(subject, "excerpts", "excerpts", "e" + i),
                "private", "memory", e, null, "r" + i, null, DateTimeOffset.UtcNow, [], Overview: e))], true));
        }
        public Task<string?> ReadText(DiscoveryRecord record, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    // The evaluation's whole-text input (TitlesEvaluation.Run: the real adapter given TitlesEvaluation.Input(item, "text"))
    // is what a native memory's star sends now, byte for byte, for every item of the set, so D-254's numbers are this
    // wiring's. A memory saved with an item's text keeps that text as it is (MemoryInput.Text), and the request is the
    // one titles-eval.md records: the instruction, the frame, the 6,000-character cut and the options.
    [Fact] public async Task A_memory_star_sends_exactly_the_request_the_evaluations_whole_text_input_sent()
    {
        var set = TitlesSet.Load();
        Assert.All(set.Items, item => Assert.Equal(item.Text, MemoryInput.Text(item.Text)));
        var core = new TitlesServer();
        var memories = set.Items.Select((item, n) => Memory(n + 1, item.Text)).ToArray();
        var batch = await Source(With(memories), TitlesServer.Adapter(core)).Fetch(Principal, default);
        var evaluation = new TitlesServer();
        var adapter = TitlesServer.Adapter(evaluation);
        foreach (var item in set.Items) await adapter.Title(TitlesEvaluation.Input(item, "text"), default);
        Assert.Equal(set.Items.Length, core.Raw.Count);
        Assert.Equal(evaluation.Raw.Select(Convert.ToHexString), core.Raw.Select(Convert.ToHexString));
        // The star's excerpt is the evaluation's excerpt (the baseline it was scored against), and each star is titled.
        Assert.All(set.Items.Zip(memories), p => Assert.Equal(TitlesRubric.Excerpt(p.First), Star(batch, p.Second).Overview));
        Assert.All(memories, m => Assert.Equal("DEMO model title", Star(batch, m).Title));
        Assert.Equal(JsonSerializer.Serialize(new
        {
            model = "fictional-model:1b", stream = false, keep_alive = "5m",
            system = "You title personal notes. Reply with only a title for the note: at most 8 words, in the note's own language, with no quotes, labels or final period.",
            prompt = TitlesServer.Frame(set.Items[0].Text), options = new { temperature = 0, num_predict = 32 },
        }), Encoding.UTF8.GetString(core.Raw[0]));

        // A long memory: the pass reads its first 8,000 characters (CatalogReader's MemoryText), the adapter sends 6,000 of
        // them, exactly what the evaluation's adapter sends of the whole text.
        var text = "DEMO: a fictional long memory about the allotment.\n" + string.Concat(Enumerable.Range(0, 900).Select(i => $"row {i:D4}; "));
        Assert.True(text.Length > 8000);
        var cut = new TitlesServer();
        await Source(With(Memory(1, text[..8000])), TitlesServer.Adapter(cut)).Fetch(Principal, default);
        evaluation.Raw.Clear();
        await adapter.Title(text, default);
        Assert.Equal(TitlesServer.Frame(text[..6000]), Assert.Single(cut.Prompts));
        Assert.Equal(Convert.ToHexString(evaluation.Raw[0]), Convert.ToHexString(cut.Raw[0]));
        // A star with no whole text sends its excerpt, as the evaluation's excerpt input did.
        var excerpt = new TitlesServer();
        var preamble = set.Items.First(i => i.Lane == "preamble");
        await Source(With(), new Excerpts("sam", [TitlesRubric.Excerpt(preamble)]), TitlesServer.Adapter(excerpt)).Fetch(Principal, default);
        evaluation.Raw.Clear();
        await adapter.Title(TitlesEvaluation.Input(preamble, "excerpt"), default);
        Assert.Equal(Convert.ToHexString(evaluation.Raw[0]), Convert.ToHexString(Assert.Single(excerpt.Raw)));
    }

    // Which text each star sends: every memory star its whole text, a canonical memory's, a draft's and a replaced fact's
    // alike, as the map shows each; a star of a source with no whole text its excerpt, as before; and a source can never
    // give a text for another source's record (here the draft's star).
    [Fact] public async Task Every_memory_star_sends_its_whole_text_a_draft_and_a_replaced_fact_too_and_others_their_excerpt()
    {
        var canonical = Memory(1, "Note to self\nDEMO: the spare key is in the blue pot by the shed.");
        var draft = Memory(2, "Quick reminder\nDEMO: qz7701k the boiler code is fictional.", status: "draft");
        var replaced = Memory(3, "Old plan\nDEMO: qz7702k the bus left at eight.", replaced: true);
        var server = new TitlesServer();
        var batch = await Source(With(canonical, draft, replaced),
            new Excerpts("sam", ["DEMO excerpt of a source with no text"], foreign: new DiscoveryRecord(MemoryStars.Key("household", draft.Id), "shared",
                "memory", "Quick reminder", null, "r", null, DateTimeOffset.UtcNow, [], Overview: "Quick reminder")),
            TitlesServer.Adapter(server)).Fetch(Principal, default);
        // The natives in their order (current ones newest first, then the replaced fact after the adapters), the adapter's
        // excerpt between them.
        Assert.Equal([TitlesServer.Frame(canonical.Text), TitlesServer.Frame(draft.Text), TitlesServer.Frame("DEMO excerpt of a source with no text"),
            TitlesServer.Frame(replaced.Text)], server.Prompts);
        var revisions = TitlesServer.Adapter(new TitlesServer());
        Assert.All(new[] { canonical, draft, replaced }, m =>
            Assert.Equal(("DEMO model title", revisions.Revision(m.Text)), (Star(batch, m).Title, Star(batch, m).TitleRevision)));
        Assert.DoesNotContain(server.Prompts, p => p.Contains("qz7799k", StringComparison.Ordinal));
        // The whole texts are dropped when the pass ends.
        var held = typeof(CatalogRelationshipSource).GetField("titleTexts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var source = Source(With(canonical), TitlesServer.Adapter(new TitlesServer()));
        await source.Fetch(Principal, default);
        Assert.Empty((Dictionary<ResourceKey, string>)held.GetValue(source)!);
        // Without a titles model nothing is kept or sent, and every star keeps its excerpt.
        var off = Source(With(canonical));
        Assert.Equal("Note to self", Star(await off.Fetch(Principal, default), canonical).Title);
        Assert.Empty((Dictionary<ResourceKey, string>)held.GetValue(off)!);
    }

    // A note's whole text is what opening its star reads (front matter left out); only a note whose title is its excerpt
    // is ever titled, so a note with a title of its own sends nothing, its text included.
    [Fact] public async Task A_note_titled_by_its_excerpt_sends_the_text_its_star_opens()
    {
        var root = Directory.CreateTempSubdirectory("rhythm-wholetext-notes-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "milk.md"), "---\ntags: [demo]\n---\n# milk\n");
            File.WriteAllText(Path.Combine(root, "Garden plan.md"), "Beans along the fence.\nDEMO qz7703k: the shed code is fictional.\n");
            var notes = new NotesAdapter();
            notes.Configure(new(new AdapterSettings("notes", new Dictionary<string, object?> { ["enabled"] = "true", ["path"] = root }), TestSettings.Use(), null!, null!));
            var server = new TitlesServer();
            var batch = await Source(With(), notes, TitlesServer.Adapter(server)).Fetch(Principal, default);
            var milk = batch.Records.Single(r => r.Kind == "note" && r.Key.ExternalId == "milk.md");
            Assert.Equal(TitlesServer.Frame("# milk"), Assert.Single(server.Prompts));
            Assert.Equal("# milk", await notes.ReadText(milk, default));
            Assert.Equal("DEMO model title", milk.Title);
            Assert.Equal("Garden plan", batch.Records.Single(r => r.Key.ExternalId == "Garden plan.md").Title);
        }
        finally { Directory.Delete(root, true); }
    }

    // The revision hashes what is sent: the model and the text as the adapter cuts it. So a title made from an excerpt is
    // made again from the whole text, a stored whole-text title is kept while model and text are unchanged, a changed text
    // is titled again, and a text that changes only past the cut keeps its title.
    [Fact] public async Task The_revision_follows_the_text_sent_so_excerpt_titles_are_made_again_and_kept_after()
    {
        var titles = TitlesServer.Adapter(new TitlesServer());
        var other = TitlesServer.Adapter(new TitlesServer(), "fictional-model:4b");
        var lead = "DEMO: a fictional long note.\n" + new string('x', 6000);
        Assert.NotEqual(titles.Revision("DEMO a"), titles.Revision("DEMO b"));
        Assert.NotEqual(titles.Revision("DEMO a"), other.Revision("DEMO a"));
        Assert.Equal(titles.Revision(lead + "one ending"), titles.Revision(lead + "another ending"));
        Assert.Equal(titles.Revision(lead[..6000]), titles.Revision(lead));

        var memory = Memory(1, "Note to self\nDEMO: the spare key is in the blue pot by the shed.");
        var key = MemoryStars.Key(memory.Subject, memory.Id);
        // What a pass made before D-255: a title from the excerpt, its revision the excerpt's.
        var before = Star(await Source(With(memory)).Fetch(Principal, default), memory) with { Title = "DEMO excerpt-made title", TitleRevision = titles.Revision("Note to self") };
        var server = new TitlesServer();
        var again = Source(With(memory), TitlesServer.Adapter(server));
        again.Prime([before]);
        var remade = Star(await again.Fetch(Principal, default), memory);
        Assert.Equal([TitlesServer.Frame(memory.Text)], server.Prompts);
        Assert.Equal(("DEMO model title", titles.Revision(memory.Text)), (remade.Title, remade.TitleRevision));
        // Unchanged: kept, nothing asked.
        server.Raw.Clear();
        var kept = Source(With(memory), TitlesServer.Adapter(server));
        kept.Prime([remade with { Title = "DEMO kept title" }]);
        Assert.Equal("DEMO kept title", Star(await kept.Fetch(Principal, default), memory).Title);
        Assert.Empty(server.Raw);
        // The text changed below its first line (the excerpt is the same): titled again from the new text.
        var changed = memory with { Text = memory.Text + "\nDEMO: and the gate sticks.", TextRevision = 2 };
        var next = Source(With(changed), TitlesServer.Adapter(server));
        next.Prime([remade with { Title = "DEMO kept title" }]);
        Assert.Equal("DEMO model title", Star(await next.Fetch(Principal, default), changed).Title);
        Assert.Equal([TitlesServer.Frame(changed.Text)], server.Prompts);
        // A memory whose whole text is its excerpt sends what it sent before, so its excerpt-made title is kept.
        var plain = Memory(2, "DEMO: water the plants on Sunday.");
        Assert.Equal(plain.Text, SecondBrainAdapter.Title(plain.Text));
        server.Raw.Clear();
        var same = Source(With(plain), TitlesServer.Adapter(server));
        same.Prime([Star(await Source(With(plain)).Fetch(Principal, default), plain) with { Title = "DEMO watering", TitleRevision = titles.Revision(plain.Text) }]);
        Assert.Equal("DEMO watering", Star(await same.Fetch(Principal, default), plain).Title);
        Assert.Empty(server.Raw);
        Assert.Equal(key, remade.Key);
    }

    // Excerpt-made titles are made again within each pass's budget, 60 titles, as any changed text is: the rest keep their
    // excerpts until a later pass, which continues.
    [Fact] public async Task Excerpt_made_titles_are_made_again_within_the_pass_budget()
    {
        var titles = TitlesServer.Adapter(new TitlesServer());
        var memories = Enumerable.Range(1, 70).Select(i => Memory(i, $"DEMO fictional memory {i}\nIts second line, number {i}.")).ToArray();
        var first = (await Source(With(memories)).Fetch(Principal, default)).Records
            .Select(r => r with { Title = "DEMO excerpt-made " + r.Key.ExternalId[^4..], TitleRevision = titles.Revision(r.Title) }).ToArray();
        var server = new TitlesServer();
        var pass = Source(With(memories), TitlesServer.Adapter(server));
        pass.Prime(first);
        var once = (await pass.Fetch(Principal, default)).Records;
        Assert.Equal(new TitlePass(60, 60, 0, null), pass.Titles);
        Assert.Equal(60, once.Count(r => r.Title == "DEMO model title"));
        Assert.Equal(10, once.Count(r => r.Title == r.Overview));
        Assert.DoesNotContain(once, r => r.Title.StartsWith("DEMO excerpt-made", StringComparison.Ordinal));
        server.Raw.Clear();
        var later = Source(With(memories), TitlesServer.Adapter(server));
        later.Prime(once);
        Assert.All((await later.Fetch(Principal, default)).Records, r => Assert.Equal("DEMO model title", r.Title));
        Assert.Equal(new TitlePass(10, 10, 0, null), later.Titles);
    }

    // A failure stops the pass asking, as before: every star keeps its excerpt, and the note gives the failure's kind and
    // counts, never a text.
    [Fact] public async Task A_failure_keeps_every_excerpt_and_stops_the_pass_as_before()
    {
        var server = new TitlesServer { Failure = () => new(HttpStatusCode.ServiceUnavailable) };
        var memories = Enumerable.Range(1, 3).Select(i => Memory(i, $"DEMO fictional memory {i}\nqz7710k private line {i}.")).ToArray();
        var pass = Source(With(memories), TitlesServer.Adapter(server));
        var batch = await pass.Fetch(Principal, default);
        Assert.Single(server.Raw);
        Assert.All(memories, m => Assert.Equal(Star(batch, m).Overview, Star(batch, m).Title));
        Assert.Equal(new TitlePass(1, 0, 0, "http-503"), pass.Titles);
        var note = Assert.Single(pass.Notes, n => n.StartsWith("titles", StringComparison.Ordinal));
        Assert.Equal("titles stopped after 1 asked, 0 titled: http-503; the rest keep their excerpts until a later rebuild", note);
        Assert.All(pass.Notes, n => Assert.DoesNotContain("qz7710k", n));
    }

    // Nothing of the whole text is on a record: each star the pass lists, serialized as the save path stores it
    // (RelationshipEngine.Save), holds no more of its memory's text than with no titles model, only another title.
    [Fact] public async Task A_titled_star_holds_no_more_of_its_text_than_before()
    {
        var memory = Memory(1, "Note to self\nDEMO qz7720k: the spare key is in the blue pot by the shed.");
        var server = new TitlesServer();
        var titled = Star(await Source(With(memory), TitlesServer.Adapter(server)).Fetch(Principal, default), memory);
        var plain = Star(await Source(With(memory)).Fetch(Principal, default), memory);
        Assert.Contains(server.Prompts, p => p.Contains("qz7720k", StringComparison.Ordinal));
        var stored = JsonSerializer.Serialize(titled);
        Assert.DoesNotContain("qz7720k", stored);
        static JsonObject Without(DiscoveryRecord r, params string[] names)
        {
            var json = JsonSerializer.SerializeToNode(r)!.AsObject();
            foreach (var name in names) json.Remove(name);
            return json;
        }
        Assert.True(JsonNode.DeepEquals(Without(plain, "Title", "TitleRevision", "RetrievedAt"), Without(titled, "Title", "TitleRevision", "RetrievedAt")));
        Assert.Equal(("DEMO model title", "Note to self"), (titled.Title, plain.Title));
    }

    // With scopes on a star no reader is served (its subject has no share of the map, as a former person's) never has its
    // text sent, and a star of a subject the map serves does.
    [Fact] public async Task With_scopes_on_a_star_no_reader_is_served_sends_nothing()
    {
        var server = new TitlesServer();
        await Source(With(), true, new Excerpts("jo", ["DEMO qz7730k a former person's excerpt"]), TitlesServer.Adapter(server)).Fetch(Principal, default);
        Assert.Empty(server.Raw);
        await Source(With(), true, new Excerpts("sam", ["DEMO Sam's excerpt"]), TitlesServer.Adapter(server)).Fetch(Principal, default);
        Assert.Equal([TitlesServer.Frame("DEMO Sam's excerpt")], server.Prompts);
    }
}

// A Second Brain memory's whole text is the listed content, for the memories its adapter makes stars of only: a draft or
// a superseded one is no star, so nothing of it is sent. In the shared collection: the adapter's token is a secret file.
[Collection(SharedStateCollection.Name)]
public sealed class TitlesWholeTextSecondBrainTests : IDisposable
{
    readonly TestSecrets secrets = new TestSecrets().With("second-brain_token", FakeBrain.Token);
    public void Dispose() => secrets.Dispose();

    [Fact] public async Task A_second_brain_memory_sends_its_listed_text()
    {
        var brain = new FakeBrain();
        brain.Rows.Add(new(FakeBrain.Id(6), "Note to self\nDEMO: the spare key is in the blue pot.", 1_790_000_500_000));
        var adapter = new SecondBrainAdapter(brain.Handler);
        adapter.Configure(new(new AdapterSettings("second-brain", new Dictionary<string, object?>
        {
            ["enabled"] = "true", ["url"] = "https://brain.example.invalid/", ["owner"] = "alex", ["teams"] = new List<object>()
        }), TestSettings.Use(), null!, null!));
        var server = new TitlesServer();
        var settings = RelationshipHostConfiguration.From(TestSettings.Use(), Guid.NewGuid(), Path.GetTempPath());
        var pass = new CatalogRelationshipSource(settings, new AdapterRegistry([adapter, TitlesServer.Adapter(server)]),
            (_, _) => Task.FromResult(new CatalogInventory([], [], Memories: [], Projects: [])));
        var batch = await pass.Fetch(settings.Principal, default);
        var prompts = server.Prompts;
        Assert.Contains(TitlesServer.Frame("Note to self\nDEMO: the spare key is in the blue pot."), prompts);
        Assert.Contains(TitlesServer.Frame(brain.Rows.Single(r => r.Id == FakeBrain.Id(1)).Content), prompts);
        Assert.DoesNotContain(prompts, p => p.Contains("half-formed", StringComparison.Ordinal) || p.Contains("Superseded", StringComparison.Ordinal));
        Assert.Equal("DEMO model title", batch.Records.Single(r => r.Key.ExternalId == FakeBrain.Id(6)).Title);
    }
}

// From the memory tables, through a real rebuild: the titles model is sent the text of every memory that is a star (a
// canonical one, a draft and, with memory.tools on, a replaced fact), never a held, deprecated, ended or trashed one's,
// which are no stars; the database holds nothing of any text it did not hold before, the stored star differing only in
// its title; the rebuild's notes and state files name no text; an unchanged text is not asked again and a changed one is.
[Collection(SharedStateCollection.Name)]
public sealed class TitlesWholeTextDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    // The database's rows that hold `marker`, by table, compared without case: everything it keeps of a text.
    async Task<Dictionary<string, long>> Holding(string marker)
    {
        var tables = new List<string>();
        await using (var list = fixture.Source.CreateCommand(
            "SELECT quote_ident(table_name) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY 1"))
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var cmd = fixture.Source.CreateCommand($"SELECT count(*) FROM {table} t WHERE strpos(lower(t::text), @m) > 0");
            cmd.Parameters.AddWithValue("m", marker.ToLowerInvariant());
            var n = (long)(await cmd.ExecuteScalarAsync())!;
            if (n > 0) counts[table] = n;
        }
        return counts;
    }

    async Task<JsonObject> Stored(ResourceKey key)
    {
        await using var cmd = fixture.Source.CreateCommand(
            "SELECT relationship_snapshot::text FROM resources WHERE provider=@p AND external_id=@e AND relationship_snapshot IS NOT NULL");
        cmd.Parameters.AddWithValue("p", key.Provider);
        cmd.Parameters.AddWithValue("e", key.CatalogId);
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync())!)!.AsObject();
    }

    sealed record Map(RelationshipHost Host, string State, TitlesAdapter? Titles);
    async Task<Map> Rebuilt(string state, TitlesServer? server)
    {
        var config = RhythmConfig.Parse(TestSettings.Yaml + "\nmemory: { tools: on }\n"
            + (server is null ? "" : "adapters:\n  titles: { enabled: true, model: fictional-model:1b }\n"));
        TestSettings.Use();
        TitlesAdapter? titles = null;
        if (server is not null)
        {
            titles = new TitlesAdapter(server);
            titles.Configure(new(config.Adapter("titles"), config, fixture.Db, new Archive(Path.GetTempPath())));
        }
        IAdapter[] adapters = titles is null ? [] : [titles];
        var registry = new AdapterRegistry(adapters);
        var db = new Database(fixture.Source, registry.Policies(config));
        var graph = await new Principals(fixture.Source).System(Principals.GraphName, default);
        var host = new RelationshipHost(db, () => RelationshipHostConfiguration.From(config, graph, state),
            c => new CatalogRelationshipSource(c, registry, (wanted, ct) => CatalogReader.Inventory(db, c.Principal, wanted, ct)), registry);
        Assert.Equal("complete", await host.Refresh(false, default));
        return new(host, state, titles);
    }

    [PostgresFact] public async Task Every_memory_stars_text_is_sent_no_other_memorys_and_nothing_of_it_is_kept()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "wholetext");
        await Scoped.Mode(s, "off");
        async Task<Guid> Make(string text, string? sql = null)
        {
            var id = (await Store.Create(who.Alex, new(text), MemoryLogins.Mcp, default)).Id;
            if (sql is not null) await Scoped.Execute(s, sql, ("id", id));
            return id;
        }
        const string Canonical = "UPDATE memories SET status='canonical' WHERE id=@id";
        var shed = await Make("Note to self\nDEMO qz7741k: the allotment shed needs a new lock.", Canonical);
        var draft = await Make("Quick reminder\nDEMO qz7742k: the boiler service is fictional.");
        var held = await Make("Things to remember\nDEMO qz7743k: a held fictional note.", "UPDATE memories SET status='canonical',hold='instructions' WHERE id=@id");
        var wrong = await Make("Okay so\nDEMO qz7744k: a fictional note marked wrong.", Canonical);
        await Store.SetStatus(who.Alex, wrong, "deprecated", null, MemoryLogins.Mcp, default);
        var ended = await Make("Random thought\nDEMO qz7745k: the ferry ran until October.",
            "UPDATE memories SET status='canonical',valid_from=now()-interval '1 day',valid_until=now()-interval '1 minute' WHERE id=@id");
        var trashed = await Make("Idea\nDEMO qz7746k: a fictional note in the trash.", Canonical);
        await Scoped.Execute(s, "UPDATE resources SET status='deleted' WHERE id=@id", ("id", trashed));
        var old = await Make("From the call\nDEMO qz7747k: the Pellow bus leaves at eight.");
        var fresh = await Make("From the call again\nDEMO qz7748k: the Pellow bus leaves at half past eight now.");
        var plan = await Store.Supersede(who.Alex, fresh, old, null, MemoryLogins.Mcp, default);
        Assert.Equal("superseded", (await Store.Supersede(who.Alex, fresh, old, plan.Confirmation, MemoryLogins.Mcp, default)).Action);
        await Scoped.Execute(s, "UPDATE memories SET status='canonical' WHERE id=ANY(@ids)", ("ids", new[] { old, fresh }));
        string[] markers = ["qz7741k", "qz7742k", "qz7743k", "qz7744k", "qz7745k", "qz7746k", "qz7747k", "qz7748k"];
        var before = new Dictionary<string, Dictionary<string, long>>();
        foreach (var marker in markers) before[marker] = await Holding(marker);
        Assert.All(markers, m => Assert.NotEmpty(before[m]));

        var state = Path.Combine(Path.GetTempPath(), "rhythm-wholetext-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var key = MemoryStars.Key("household", shed);
            var plain = await Rebuilt(state, null);
            var untitled = await Stored(key);
            var server = new TitlesServer
            {
                Reply = p => p.Contains("qz7741k", StringComparison.Ordinal) ? "DEMO allotment shed lock"
                    : p.Contains("qz7742k", StringComparison.Ordinal) ? "DEMO boiler service"
                    : p.Contains("qz7747k", StringComparison.Ordinal) ? "DEMO Pellow bus at eight" : "DEMO Pellow bus time"
            };
            var map = await Rebuilt(state, server);
            // Sent: this test's four memory stars, whole: the canonical one, the draft, the replaced fact and what replaced
            // it (the class's other test may have left one of its own); nothing of the four that are no stars.
            Assert.Equal(
                [TitlesServer.Frame("From the call\nDEMO qz7747k: the Pellow bus leaves at eight."),
                 TitlesServer.Frame("From the call again\nDEMO qz7748k: the Pellow bus leaves at half past eight now."),
                 TitlesServer.Frame("Note to self\nDEMO qz7741k: the allotment shed needs a new lock."),
                 TitlesServer.Frame("Quick reminder\nDEMO qz7742k: the boiler service is fictional.")],
                server.Prompts.Where(p => p.Contains("qz774", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            string[] noStars = ["qz7743k", "qz7744k", "qz7745k", "qz7746k"];
            Assert.DoesNotContain(server.Prompts, p => noStars.Any(m => p.Contains(m, StringComparison.Ordinal)));
            var nodes = (await map.Host.Read(who.Sam, null, null, default, new GraphQuery())).nodes.ToDictionary(n => n.id);
            Assert.Equal("DEMO allotment shed lock", nodes[key.GraphId].title);
            Assert.Equal("DEMO boiler service", nodes[MemoryStars.Key("household", draft).GraphId].title);
            Assert.Equal(("DEMO Pellow bus at eight", "replaced"),
                (nodes[MemoryStars.Key("household", old).GraphId].title, nodes[MemoryStars.Key("household", old).GraphId].truth?.state));
            Assert.Equal("DEMO Pellow bus time", nodes[MemoryStars.Key("household", fresh).GraphId].title);
            Assert.All(new[] { held, wrong, ended, trashed }, id => Assert.DoesNotContain(MemoryStars.Key("household", id).GraphId, nodes.Keys));
            // Stored: the star as without a titles model but for its title; no table holds any text more than before.
            var titled = await Stored(key);
            Assert.Equal(("DEMO allotment shed lock", map.Titles!.Revision("Note to self\nDEMO qz7741k: the allotment shed needs a new lock.")),
                ((string?)titled["Title"], (string?)titled["TitleRevision"]));
            foreach (var json in new[] { untitled, titled }) { json.Remove("Title"); json.Remove("TitleRevision"); json.Remove("RetrievedAt"); }
            Assert.True(JsonNode.DeepEquals(untitled, titled));
            foreach (var marker in markers) Assert.Equal(before[marker], await Holding(marker));
            // Logged: the rebuild's notes and its state files name no text.
            Assert.All(markers, m => Assert.All(map.Host.Notes, n => Assert.DoesNotContain(m, n)));
            foreach (var file in Directory.GetFiles(state))
                Assert.All(markers, m => Assert.DoesNotContain(m, File.ReadAllText(file)));
            // The next rebuild keeps every title without asking; a changed text is asked again.
            server.Raw.Clear();
            map = await Rebuilt(state, server);
            Assert.Empty(server.Raw);
            await Scoped.Execute(s, "UPDATE memories SET content=content||E'\\nDEMO: and the gate sticks.',text_revision=text_revision+1 WHERE id=@id", ("id", shed));
            map = await Rebuilt(state, server);
            Assert.Equal([TitlesServer.Frame("Note to self\nDEMO qz7741k: the allotment shed needs a new lock.\nDEMO: and the gate sticks.")], server.Prompts);
            Assert.Equal(map.Titles!.Revision("Note to self\nDEMO qz7741k: the allotment shed needs a new lock.\nDEMO: and the gate sticks."),
                (string?)(await Stored(key))["TitleRevision"]);
            _ = plain;
        }
        finally { try { Directory.Delete(state, true); } catch (IOException) { } }
    }

    // With scopes on a private memory's title, made from its text, is on its own star only, which only its readers are
    // served: Sam's map holds nothing of Alex's.
    [PostgresFact] public async Task With_scopes_on_a_private_memorys_title_is_served_to_its_readers_alone()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "wholetext-on");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var state = Path.Combine(Path.GetTempPath(), "rhythm-wholetext-on-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var mine = (await Store.Create(who.Alex, new("Note to self\nDEMO qz7751k: Alex's fictional shed plan."), MemoryLogins.Mcp, default)).Id;
            await Scoped.Execute(s, "UPDATE memories SET status='canonical' WHERE id=@id", ("id", mine));
            var server = new TitlesServer { Reply = _ => "DEMO Alex shed plan" };
            var map = await Rebuilt(state, server);
            Assert.Contains(TitlesServer.Frame("Note to self\nDEMO qz7751k: Alex's fictional shed plan."), server.Prompts);
            var star = MemoryStars.Key("alex", mine).GraphId;
            Assert.Equal("DEMO Alex shed plan", (await map.Host.Read(who.Alex, null, null, default, new GraphQuery("get", star))).nodes.Single().title);
            Assert.Empty((await map.Host.Read(who.Sam, null, null, default, new GraphQuery("get", star))).nodes);
            Assert.DoesNotContain((await map.Host.Read(who.Sam, null, null, default, new GraphQuery())).nodes, n => n.title == "DEMO Alex shed plan");
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
            try { Directory.Delete(state, true); } catch (IOException) { }
        }
    }
}
