using System.Text.Json;
using Knowledge.Adapters;

namespace Knowledge.Tests;

// How search_knowledge and search_resources read a query, and the SQL each mode runs.
public sealed class LexicalSearchTests
{
    [Fact] public void A_query_keeps_its_content_words_whole_identifiers_included()
    {
        Assert.Equal(["day", "rubbish", "collected"], LexicalSearch.Terms("Which day is the rubbish collected now?"));
        Assert.Equal(["bin", "day", "may", "change"], LexicalSearch.Terms("Bin day in May: would it change?"));
        Assert.Equal(["grandmother", "apple", "tart"], LexicalSearch.Terms("grandmother's apple tart"));
        Assert.Equal(["rhy-4127", "fixed", "v2.14.3"], LexicalSearch.Terms("(RHY-4127), fixed in v2.14.3."));
        Assert.Equal(["kelptide", "café"], LexicalSearch.Terms("Kelptide Café"));
        // Only function words, or nothing searchable at all: no words, so the query from before ranking runs.
        Assert.Empty(LexicalSearch.Terms("the who"));
        Assert.Empty(LexicalSearch.Terms("Don't, who's?"));
        Assert.Equal(["t-shirt"], LexicalSearch.Terms("the t-shirt"));
        Assert.Empty(LexicalSearch.Terms(" ?! - x "));
        Assert.Equal(LexicalSearch.MaxTerms, LexicalSearch.Terms(string.Join(' ', Enumerable.Range(0, 30).Select(i => "word" + i))).Count);
        Assert.Equal(["7"], LexicalSearch.Terms("7"));
    }

    [Theory]
    [InlineData("rhy-4127", true)] [InlineData("v2.14.3", true)] [InlineData("hp-2291", true)] [InlineData("q9", true)]
    [InlineData("2.4.1", true)] [InlineData("trk-90417-b", true)] [InlineData("a41f9e2", true)]
    [InlineData("2026", false)] [InlineData("kitchen", false)] [InlineData("grand-mère", false)]
    [InlineData("2026-10-31", false)] [InlineData("31/10", false)] [InlineData("10:30", false)] [InlineData("12.50", false)]
    [InlineData("40th", false)] [InlineData("3pm", false)]
    public void Identifiers_are_digits_beside_letters_or_in_dotted_parts(string term, bool identifier) =>
        Assert.Equal(identifier, LexicalSearch.Identifier(term));

    [Fact] public void Legacy_and_wordless_queries_run_the_query_from_before_ranking()
    {
        foreach (var (search, query) in new[] { (LexicalSearch.Legacy, "which day are the bins collected"), (LexicalSearch.Standard, ""),
            (LexicalSearch.Standard, "?!"), (LexicalSearch.Standard, "the who") })
        {
            var (sql, args) = search.Sql(query, "r.id", "true", "@limit");
            Assert.Contains("to_tsvector('simple',r.title || ' ' || r.content) @@ plainto_tsquery('simple',@q)", sql);
            Assert.Contains("ORDER BY r.indexed_at DESC,r.id LIMIT @limit", sql);
            Assert.Equal([("q", (object?)query)], args);
        }
        var (ranked, bound) = LexicalSearch.Standard.Sql("RHY-4127 4127 crash", "r.id", "true", "@limit");
        // Ranked search reads the row's stored parse of its title and content (047), never parses them again.
        Assert.Contains($"r.{LexicalSearch.Stored} @@", ranked);
        Assert.Contains($"(SELECT r.{LexicalSearch.Stored} AS doc OFFSET 0) d", ranked);
        Assert.DoesNotContain("r.title || ' ' || r.content", ranked);
        // So does combined retrieval's keyword arm, for a catalog row's own text; a memory's text and a passage are parsed.
        var (texts, _) = LexicalSearch.Standard.Sql("crash", "r.id", "true", "@limit", true, new("true", true, "false", "", "''"));
        Assert.Contains($"CASE WHEN t.part=-1 AND NOT kx.memory THEN r.{LexicalSearch.Stored} ELSE to_tsvector('english',t.head || ' ' || t.body) END", texts);
        Assert.DoesNotContain("r.title || ' ' || r.content", texts);
        // Another configuration (the evaluation's variants) parses as before.
        Assert.Contains("to_tsvector('simple',r.title || ' ' || r.content) @@", new LexicalSearch("simple", true).Sql("crash", "r.id", "true", "1").Sql);
        // Rows that tie on everything else come back in one order however often the map is rebuilt.
        Assert.EndsWith("r.indexed_at DESC,r.provider,r.external_id,r.id LIMIT @limit", ranked.TrimEnd());
        Assert.Equal(["q", "t0", "x0", "t1", "n1", "t2"], bound.Select(a => a.Item1));
        Assert.Equal("-4127", bound.Single(a => a.Item1 == "n1").Item2);
        Assert.Throws<InvalidOperationException>(() => new LexicalSearch("english',r.content)--", true).Sql("crash", "r.id", "true", "1"));
    }

    // New behaviour stays off for an existing install (D-053): settings written before ranking have no legacySearch line
    // and keep the old query; the example, which `rhythm init` copies for a new install, says false and ranks.
    [Fact] public void An_install_without_the_setting_keeps_the_old_search_and_a_new_one_ranks()
    {
        const string Head = "version: 1\ntimezone: UTC\npeople: [{ id: alex, name: Alex }]\n";
        Assert.Same(LexicalSearch.Legacy, LexicalSearch.For(RhythmConfig.Parse(Head)));
        Assert.Same(LexicalSearch.Legacy, LexicalSearch.For(RhythmConfig.Parse(Head + "engine: { url: http://engine:8080 }\n")));
        Assert.Same(LexicalSearch.Legacy, LexicalSearch.For(RhythmConfig.Parse(Head + "engine: { legacySearch: true }\n")));
        Assert.Same(LexicalSearch.Standard, LexicalSearch.For(RhythmConfig.Parse(Head + "engine: { legacySearch: false }\n")));
        Assert.Same(LexicalSearch.Standard, LexicalSearch.For(RhythmConfig.Load(Path.Combine(AppContext.BaseDirectory, "rhythm.example.yaml"))));
        // The tests run as a new install would.
        Assert.Same(LexicalSearch.Standard, LexicalSearch.For(TestSettings.Use()));
    }
}

// Against a real database: ranked search finds what every-word search missed, puts exact ids and rare words first, uses
// its own index, and the legacy setting answers exactly as before.
public sealed class LexicalSearchDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    const string Provider = "lexical-notes";
    sealed class Notes : IAdapter
    {
        public string Name => Provider;
        public IReadOnlyDictionary<string, ProviderPolicy> Providers { get; } =
            new Dictionary<string, ProviderPolicy> { [Provider] = new(SourceAuthority.AuthorizedArchive, "household") };
    }

    // DEMO records, one transaction each, so each is indexed after the one before it.
    async Task<(Database Db, Guid Reader)> Seed(params (string Title, string Text, string Kind)[] records)
    {
        var config = TestSettings.Use();
        var db = new Database(fixture.Source, new AdapterRegistry([new Notes()]).Policies(config));
        foreach (var (title, text, kind) in records)
        {
            await using var c = await fixture.Source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await db.Upsert(new(RhythmConfig.Household, Provider, title, kind, "shared", title, null, "DEMO: " + text), [], c, tx, default);
            await tx.CommitAsync();
        }
        return (db, await new Principals(fixture.Source).System("lexical-search-test", default));
    }
    static string[] Titles(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(r => r.GetProperty("title").GetString()!)];

    [PostgresFact] public async Task Ranked_search_finds_more_and_puts_exact_ids_and_rare_words_first()
    {
        var (db, reader) = await Seed(
            ("RHY-4127 export crash", "Pinned the encoder to v2.14.3 to fix the export crash.", "note"),
            ("Garden office wiring", "The garden office runs on circuit 7 of the consumer unit.", "document"),
            ("Bins", "Bin collection moves from Monday to Tuesday.", "note"),
            ("Pepper", "Pepper the cat sleeps on the landing.", "note"),
            ("Vet booking", "Booked the cat in at Quillon Veterinary for the booster.", "note"),
            ("Cat flap", "The cat flap in the back door sticks in the cold.", "note"),
            ("Office move", "The office party moved to the garden.", "document"),
            ("Ticket list", "Open tickets this week: RHY-4127, RHY-4130 and a printer fault.", "note"),
            ("The Who tickets", "Two seats for The Who at the arena.", "note"),
            ("Boathouse", "The Ottervale boathouse is booked for the fifth.", "note"));
        var ranked = new Retrieval(db, null, LexicalSearch.Standard);
        var legacy = new Retrieval(db, null, LexicalSearch.Legacy);
        async Task<string[]> Search(Retrieval r, string q) => Titles(await r.Search(reader, q, 10, default));

        // An exact id: the record with it in its title, then the one with it in its text; before, newest first.
        Assert.Equal(["RHY-4127 export crash", "Ticket list"], await Search(ranked, "RHY-4127"));
        Assert.Equal(["Ticket list", "RHY-4127 export crash"], await Search(legacy, "RHY-4127"));
        // The number alone finds it, though Postgres reads "RHY-4127" as "rhy" and "-4127".
        Assert.Equal(["RHY-4127 export crash", "Ticket list"], (await Search(ranked, "4127")).Order());
        Assert.Empty(await Search(legacy, "4127"));
        // A question: function words dropped, words stemmed, one matching word enough.
        Assert.Equal("Bins", (await Search(ranked, "which day are the bins collected?"))[0]);
        Assert.Empty(await Search(legacy, "which day are the bins collected?"));
        // A rare word outweighs a common one; every word outweighs some.
        Assert.Equal("Vet booking", (await Search(ranked, "cat Quillon"))[0]);
        Assert.Equal(["Garden office wiring", "Office move"], (await Search(ranked, "garden office circuit")).Take(2));
        // Only function words: the query from before, which keeps them, so the band is still found.
        Assert.Equal(["The Who tickets"], await Search(ranked, "the who"));
        Assert.Equal(await Search(legacy, "The Who"), await Search(ranked, "The Who"));
        // Nothing to search for: everything, newest first, as before.
        Assert.Equal(await Search(legacy, ""), await Search(ranked, ""));
        Assert.Equal("Boathouse", (await Search(ranked, ""))[0]);

        // search_resources ranks the same way within its filters.
        Assert.Equal(["Garden office wiring", "Office move"],
            Titles(await ranked.SearchResources(reader, "garden office circuit", "", "", "document", "", 10, default)));
        Assert.Equal(["Garden office wiring"], Titles(await legacy.SearchResources(reader, "garden office circuit", "", "", "document", "", 10, default)));

        // However many newer rows hold a common word, a rare word's row stays a candidate and comes first.
        await using (var flood = fixture.Source.CreateCommand($"""
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
            SELECT gen_random_uuid(),'household','{Provider}','flood-' || i,'note','shared','Cat note ' || i,'DEMO: the cat again.','current','unclassified'
            FROM generate_series(1,{LexicalSearch.AllWords + LexicalSearch.Words}) i
            """)) await flood.ExecuteNonQueryAsync();
        Assert.Equal("Boathouse", (await Search(ranked, "cat Ottervale"))[0]);
    }

    // Every function word the configuration never indexes is dropped, so a query made only of them runs the query from
    // before ranking; the modal words dropped as noise, and a word that is kept ("may", the month), are indexed.
    [PostgresFact] public async Task Dropped_function_words_include_every_stop_word_of_the_configuration()
    {
        TestSettings.Use();
        await using var c = await fixture.Source.OpenConnectionAsync();
        async Task<string[]> Indexed(IEnumerable<string> words)
        {
            await using var cmd = Database.Command(c, null,
                $"SELECT coalesce(array_agg(w ORDER BY w),'{{}}') FROM unnest(@words) w WHERE to_tsvector('{LexicalSearch.Chosen}',w) <> ''::tsvector",
                ("words", words.ToArray()));
            return (string[])(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Empty(await Indexed(LexicalSearch.Unindexed));
        Assert.Equal(LexicalSearch.Modal.Order(StringComparer.Ordinal), await Indexed(LexicalSearch.Modal));
        Assert.Equal(["may", "us"], await Indexed(["may", "us"]));
        Assert.Equal(["may"], LexicalSearch.Terms("may"));
    }

    // The words' match is the indexed expression exactly, or every search reads the whole table. Without the visibility
    // conditions, whose own index a small table prefers, the planner has only the text index to use.
    [PostgresFact] public async Task Each_mode_can_use_its_own_index()
    {
        await Seed(("Index probe", "A fictional record.", "note"));
        foreach (var (search, index) in new[] { (LexicalSearch.Standard, "resources_search_vector"), (LexicalSearch.Legacy, "resources_search") })
        {
            var (sql, args) = search.Sql("fictional record probe", "r.id", "true", "10");
            await using var c = await fixture.Source.OpenConnectionAsync();
            await using (var off = Database.Command(c, null, "SET enable_seqscan=off")) await off.ExecuteNonQueryAsync();
            await using var explain = Database.Command(c, null, "EXPLAIN " + sql, args);
            var plan = new List<string>();
            await using (var rows = await explain.ExecuteReaderAsync()) while (await rows.ReadAsync()) plan.Add(rows.GetString(0));
            Assert.True(plan.Any(line => line.Contains(index + " ", StringComparison.Ordinal) || line.EndsWith(index, StringComparison.Ordinal)),
                string.Join("\n", plan));
        }
    }

    // The stored text vector (047, SB-P2-21) is the parse it replaced, on every row and after every change, and with it
    // ranked search answers the same rows in the same order as when it parsed each row at the search. 021's expression
    // index is gone; the column has its own.
    [PostgresFact] public async Task The_stored_vector_is_the_parse_it_replaced_and_answers_alike()
    {
        var (db, reader) = await Seed(
            ("Stored vector one", "The fictional Brockle report is due on Friday; collected figures follow.", "note"),
            ("Stored vector two", string.Concat(Enumerable.Repeat("DEMO collection of fictional Brockle figures, ", 900)), "document"),
            ("Stored vector three", "Nothing about the report here, only the fictional heron.", "note"));
        static string Same(string column) =>
            $"SELECT count(*) FROM resources WHERE {column} IS DISTINCT FROM to_tsvector('english'::regconfig,title || ' ' || content)";
        Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source, Same(LexicalSearch.Stored)));
        await MemoryRows.Run(fixture.Source, "UPDATE resources SET content=content || ' DEMO: a later fictional line.' WHERE provider=@p", ("p", Provider));
        Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source, Same(LexicalSearch.Stored)));
        foreach (var query in new[] { "Brockle report", "collected figures", "when is the fictional Brockle report due?", "heron" })
        {
            var (sql, args) = LexicalSearch.Standard.Sql(query, $"{Retrieval.Columns},left(r.content,400) AS excerpt", Database.Visible(), "20");
            var parsed = sql.Replace($"r.{LexicalSearch.Stored}", $"to_tsvector('{LexicalSearch.Chosen}',r.title || ' ' || r.content)", StringComparison.Ordinal);
            Assert.NotEqual(sql, parsed);
            Assert.Equal(Titles(await db.Read(reader, parsed, default, args)), Titles(await db.Read(reader, sql, default, args)));
        }
        Assert.Null(await MemoryRows.Scalar(fixture.Source, "SELECT to_regclass('resources_search_english')::text"));
        Assert.Equal("resources_search_vector", await MemoryRows.Scalar(fixture.Source, "SELECT to_regclass('resources_search_vector')::text"));
    }
}
