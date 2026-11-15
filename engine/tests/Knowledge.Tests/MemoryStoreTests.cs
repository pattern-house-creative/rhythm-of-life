using Knowledge.Cli;
using Npgsql;

namespace Knowledge.Tests;

// The native memory store's core and its checked writers (migration 033, Core/Memories.cs; SB-P3-01 and SB-P3-02,
// docs/dev/memory.md sections 2, 5, 6.2 and 10). Fictional people only: alex, sam and the household; every text is DEMO.
// Nothing outside these tests calls the store yet, so no install holds a native memory.

public sealed class MemoryInputTests
{
    [Fact] public void Hashtags_are_words_after_a_hash_and_never_references_addresses_or_entities()
    {
        Assert.Equal(["canal", "paren", "café", "a_1", "user"], MemoryInput.Hashtags(
            "DEMO #Canal walk on #2026-11-01, see example.invalid/page#part and &#39; ##double (#paren) #café #a_1 #user #canal #synthesized"));
        Assert.Empty(MemoryInput.Hashtags("DEMO issue #298 and # heading"));
    }

    [Fact] public void Tags_are_lower_case_once_each_and_the_engines_own_are_left_off_and_named()
    {
        var (kept, dropped) = MemoryInput.Tags(["Canal", " canal ", "STATUS:canonical", "Quarantine:x", "project:canal", "User-Edited", "", "walks"],
            "DEMO text #walks #fresh");
        Assert.Equal(["canal", "walks", "fresh"], kept);
        Assert.Equal(["status:canonical", "quarantine:x", "project:canal", "user-edited"], dropped);
        foreach (var tag in MemoryInput.ReservedNames.Concat(MemoryInput.ReservedPrefixes.Select(p => p + "x")))
        {
            Assert.True(MemoryInput.Reserved(tag), tag);
            Assert.True(MemoryInput.Reserved(tag.ToUpperInvariant()), tag);
        }
        Assert.False(MemoryInput.Reserved("decision"));
        Assert.False(MemoryInput.Reserved("standing"));
    }

    [Fact] public void Text_and_tags_keep_to_the_limits()
    {
        static string Code(Action act) => Assert.Throws<MemoryRefused>(act).Code;
        Assert.Equal("DEMO: kept", MemoryInput.Text("  DEMO: kept \n"));
        Assert.Equal("invalid", Code(() => MemoryInput.Text(" \t ")));
        Assert.Equal("invalid", Code(() => MemoryInput.Text(null)));
        Assert.Equal("invalid", Code(() => MemoryInput.Text("DEMO\0")));
        Assert.Equal(131_072, MemoryInput.Text(new string('a', 131_072)).Length);
        Assert.Equal("too_large", Code(() => MemoryInput.Text(new string('a', 131_073))));
        // Bytes, not characters: 43 691 euro signs are 131 073 bytes.
        Assert.Equal("too_large", Code(() => MemoryInput.Text(new string('€', 43_691))));
        Assert.Equal("invalid", Code(() => MemoryInput.Tags(Enumerable.Range(0, 65).Select(i => $"t{i}"), null)));
        Assert.Equal("invalid", Code(() => MemoryInput.Tags([new string('t', 129)], null)));
        Assert.Equal("invalid", Code(() => MemoryInput.Tags(["bell\u0007"], null)));
        Assert.Equal("invalid", Code(() => MemoryInput.Tags(Enumerable.Range(0, 64).Select(i => $"t{i}"), "DEMO #one-more")));
        Assert.Equal(64, MemoryInput.Tags(Enumerable.Range(0, 64).Select(i => $"t{i}"), "DEMO #t1").Kept.Count);
        Assert.Equal("canal-walks", MemoryInput.Project(" Canal-Walks "));
        Assert.Null(MemoryInput.Project(" "));
    }

    [Fact] public void A_named_scope_is_private_or_shared_in_either_programs_words()
    {
        Assert.Equal("private", MemorySettings.Named("Personal"));
        Assert.Equal("shared", MemorySettings.Named(" family "));
        Assert.Equal("shared", MemorySettings.Named("company"));
        Assert.Null(MemorySettings.Named(""));
        Assert.Throws<ArgumentException>(() => MemorySettings.Named("team"));
    }
}

// The logins a test writes with: Alex's and Sam's AI clients, a client with no person (the household view) and the
// app's shared login, each made as rhythm token create makes one.
internal sealed record MemoryLogins(Guid Alex, Guid Sam, Guid House, Guid App)
{
    public static readonly MemoryCall Mcp = new("mcp", "DEMO client");
    public static MemoryCall Key(string key) => new("mcp", "DEMO client", key);

    // The app's shared login is the one enabled app login with no person (017, 023), so a database keeps one for every test.
    public static async Task<MemoryLogins> Make(NpgsqlDataSource s, string tag) => new(
        await Scoped.Principal(s, $"{tag}-alex", "harness", "alex"), await Scoped.Principal(s, $"{tag}-sam", "harness", "sam"),
        await Scoped.Principal(s, $"{tag}-house", "harness"),
        await Scalar(s, "SELECT id FROM principals WHERE role='app' AND enabled AND person_id IS NULL") as Guid? ?? await Scoped.Principal(s, "memory-app", "app"));

    // The database's clock, which stamps every memory: a test's own memories are those written since.
    public static async Task<DateTimeOffset> Now(NpgsqlDataSource s) => new((DateTime)(await Scalar(s, "SELECT clock_timestamp()"))!);

    // Family as the switch's first start leaves it (both people members), and no admin.
    public static async Task Family(NpgsqlDataSource s)
    {
        await Scoped.Execute(s, "DELETE FROM memberships");
        await Scoped.Members(s, "alex", "sam");
    }

    public static Task<object?> Scalar(NpgsqlDataSource s, string sql, params (string, object?)[] args) => MemoryRows.Scalar(s, sql, args);
    public static async Task<long> Count(NpgsqlDataSource s, string table) => (long)(await Scalar(s, $"SELECT count(*) FROM {table}"))!;

    public static async Task<string> Refused(Func<Task> act) => (await Assert.ThrowsAsync<MemoryRefused>(act)).Code;

    // The terminal's own logins for alex and sam (migration 044), as rhythm remember makes them: the only logins that
    // write through the terminal's channel (cli), which every AI client's token is refused. Guid.Empty for someone who
    // is not a current person here.
    public static async Task<(Guid Alex, Guid Sam)> Terminals(NpgsqlDataSource s) => (await Terminal(s, "alex"), await Terminal(s, "sam"));
    public static async Task<Guid> Terminal(NpgsqlDataSource s, string person) =>
        await TerminalLogin.Refusal(s, person, default) is null ? await TerminalLogin.For(s, person, default) : Guid.Empty;
    // The terminal's own login for the person an AI client's login speaks for.
    public static async Task<Guid> AtTerminal(NpgsqlDataSource s, Guid login) =>
        await Terminal(s, (string)(await Scalar(s, "SELECT person_id FROM principals WHERE id=@id", ("id", login)))!);
}

// SB-P3-01: where a memory lands, what each reader is served, filters, projects, the engine's login, and the lock that
// makes rhythm scopes off wait for a writer.
public sealed class MemoryStoreTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    [PostgresFact] public async Task With_scopes_off_a_memory_is_the_households_and_a_private_one_is_refused()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "off");
        await Scoped.Mode(s, "off");
        var write = await Store.Create(who.Alex, new("DEMO: the canal walk takes forty fictional minutes. #Walks #2026",
            ["Canal", "status:canonical", "canal"], Kind: "semantic", Importance: 3, Source: "claude"), MemoryLogins.Mcp, default);
        Assert.Equal(("household", "shared", "draft", 1, "off"), (write.Subject, write.Scope, write.Status, write.Revision, write.Scopes));
        Assert.Equal(["status:canonical"], write.DroppedTags);
        Assert.False(write.Replayed);
        // The text keeps its hashtags; the tags hold them too. The author is recorded though scopes are off.
        Assert.Equal("DEMO: the canal walk takes forty fictional minutes. #Walks #2026|canal,walks|alex|semantic|3|claude",
            await MemoryLogins.Scalar(s, "SELECT content||'|'||array_to_string(tags,',')||'|'||author_id||'|'||kind||'|'||importance||'|'||source FROM memories WHERE id=@id", ("id", write.Id)));
        Assert.Equal(who.Alex, await MemoryLogins.Scalar(s, "SELECT created_by FROM memories WHERE id=@id", ("id", write.Id)));
        // Its resources row: provider memory, its id as its external id, and none of its words.
        Assert.Equal($"memory|{write.Id}|memory|household|shared|Memory||current|memory|", await MemoryLogins.Scalar(s, """
            SELECT provider||'|'||external_id||'|'||kind||'|'||subject_id||'|'||scope||'|'||title||'|'||content||'|'||status||'|'||domain||'|'
              ||coalesce(relationship_snapshot::text,'') FROM resources WHERE id=@id
            """, ("id", write.Id)));
        Assert.Equal("created|mcp|DEMO client|1", await MemoryLogins.Scalar(s,
            "SELECT event||'|'||channel||'|'||client||'|'||revision FROM memory_events WHERE memory_id=@id", ("id", write.Id)));
        // The household shares everything: Sam's client and the household view read it.
        Assert.Equal("DEMO: the canal walk takes forty fictional minutes. #Walks #2026", (await Store.Get(who.Sam, write.Id, default))!.Content);
        Assert.Equal("alex", (await Store.Get(who.House, write.Id, default))!.Author);
        // A private memory waits for scopes (D-048), and nothing is written.
        var memories = await MemoryLogins.Count(s, "memories");
        var refused = await Assert.ThrowsAsync<MemoryRefused>(() => Store.Create(who.Alex, new("DEMO: a private thought.", Scope: "private"), MemoryLogins.Mcp, default));
        Assert.Equal("forbidden", refused.Code);
        Assert.Equal(MemorySettings.Lands(false, "alex", "private").Refusal, refused.Message);
        Assert.Equal(memories, await MemoryLogins.Count(s, "memories"));
        // The source defaults to the channel; a source that isn't a short lower-case name is refused.
        Assert.Equal("mcp", (await Store.Get(who.Alex, (await Store.Create(who.Alex, new("DEMO: no source."), MemoryLogins.Mcp, default)).Id, default))!.Source);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: odd source.", Source: "Claude Desktop"), MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: odd kind.", Kind: "procedural"), MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: odd importance.", Importance: 6), MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: odd scope.", Scope: "team"), MemoryLogins.Mcp, default)));
        // The channel is the login's: an AI client writes over MCP or the terminal, the app's login through the app.
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: wrong channel."), new("app"), default)));
        // The terminal is its own login, bound to a person (044): it records the terminal, and writes through nothing else;
        // no AI client's token reaches the terminal, a person-bound one (Alex's client) included, nor the household view.
        var terminal = await MemoryLogins.Terminal(s, "alex");
        Assert.Equal("cli", (await Store.Get(who.Alex, (await Store.Create(terminal, new("DEMO: from the terminal."), new("cli"), default)).Id, default))!.Source);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: not the terminal."), new("cli"), default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.House, new("DEMO: not the terminal.", Scope: "shared"), new("cli"), default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(terminal, new("DEMO: not an AI client."), MemoryLogins.Mcp, default)));
        Assert.Equal("app", (await Store.Get(who.App, (await Store.Create(who.App, new("DEMO: from the app."), new("app"), default)).Id, default))!.Source);
        Assert.Equal(memories + 3, await MemoryLogins.Count(s, "memories"));
    }

    [PostgresFact] public async Task With_scopes_on_a_memory_is_private_to_its_writer_unless_the_call_names_Family()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "on");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var own = await Store.Create(who.Alex, new("DEMO: Alex's fictional allotment plot is number nine."), MemoryLogins.Mcp, default);
            Assert.Equal(("alex", "private", "on"), (own.Subject, own.Scope, own.Scopes));
            Assert.NotNull(await Store.Get(who.Alex, own.Id, default));
            Assert.Null(await Store.Get(who.Sam, own.Id, default));
            Assert.Null(await Store.Get(who.House, own.Id, default));
            Assert.Null(await Store.Get(who.App, own.Id, default));
            var shared = await Store.Create(who.Alex, new("DEMO: the fictional cafe opens at eight.", Scope: "company"), MemoryLogins.Mcp, default);
            Assert.Equal(("household", "shared"), (shared.Subject, shared.Scope));
            Assert.Equal("alex", (await Store.Get(who.Sam, shared.Id, default))!.Author);
            // The household view has no private scope (D-073): it names Family, and its memory has no person author.
            var refused = await Assert.ThrowsAsync<MemoryRefused>(() => Store.Create(who.House, new("DEMO: the bins go out on Thursday."), MemoryLogins.Mcp, default));
            Assert.Equal(("forbidden", MemorySettings.Lands(true, null, null).Refusal), (refused.Code, refused.Message));
            var bins = await Store.Create(who.House, new("DEMO: the bins go out on Thursday.", Scope: "Family"), MemoryLogins.Mcp, default);
            Assert.Null((await Store.Get(who.Alex, bins.Id, default))!.Author);
            // Out of Family, Sam writes for himself only; suspended, for no one.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Create(who.Sam, new("DEMO: for Family.", Scope: "shared"), MemoryLogins.Mcp, default)));
            Assert.Equal("sam", (await Store.Create(who.Sam, new("DEMO: Sam's own fictional note."), MemoryLogins.Mcp, default)).Subject);
            await Scoped.Execute(s, "UPDATE subjects SET status='suspended' WHERE id='sam'");
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Create(who.Sam, new("DEMO: while suspended."), MemoryLogins.Mcp, default)));
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE content IN ('DEMO: for Family.','DEMO: while suspended.')"));
        }
        finally
        {
            await Scoped.Execute(s, "UPDATE subjects SET status='current' WHERE id='sam'");
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    // memory_landing (033) and MemorySettings.Lands decide alike for every mode, writer and named scope.
    [PostgresFact] public async Task Landing_in_the_database_matches_MemorySettings_Lands()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "lands");
        await MemoryLogins.Family(s);
        try
        {
            foreach (var on in new[] { false, true })
            {
                await Scoped.Mode(s, on ? "on" : "off");
                foreach (var (writer, person) in new[] { (who.Alex, "alex"), (who.House, (string?)null) })
                    foreach (var named in new[] { null, "", "private", "Personal", "shared", "Family", "company", "team" })
                    {
                        MemorySettings.Landing? expected = null;
                        string? invalid = null;
                        try { expected = MemorySettings.Lands(on, person, named); } catch (ArgumentException e) { invalid = e.Message; }
                        try
                        {
                            var write = await Store.Create(writer, new($"DEMO: landing {on} {person} {named}.", Scope: named), MemoryLogins.Mcp, default);
                            Assert.True(expected?.Refusal is null && invalid is null, $"{on} {person} {named} landed");
                            Assert.Equal((expected!.Subject, expected.Scope), (write.Subject, write.Scope));
                        }
                        catch (MemoryRefused refused)
                        {
                            Assert.Equal(invalid ?? expected?.Refusal, refused.Message);
                            Assert.Equal(invalid is null ? "forbidden" : "invalid", refused.Code);
                        }
                    }
            }
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    [PostgresFact] public async Task The_engine_and_the_database_leave_off_the_same_reserved_tags()
    {
        var s = fixture.Source;
        var listed = (string[])(await MemoryLogins.Scalar(s, "SELECT memory_reserved_tags()"))!;
        Assert.Equal(MemoryInput.ReservedNames.Concat(MemoryInput.ReservedPrefixes).Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        foreach (var tag in MemoryInput.ReservedNames.Concat(MemoryInput.ReservedPrefixes.Select(p => p + "x")))
            Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT memory_check_tags(ARRAY[@t])", ("t", tag.ToUpperInvariant())));
        // The function that enforces the list reads it (memory_reserved_tag from memory_reserved_tags): a tag just past a
        // namespace or a marker is taken.
        foreach (var tag in new[] { "kinds", "status", "user-edited-later", "projects", "decision" })
            Assert.False((bool)(await MemoryLogins.Scalar(s, "SELECT memory_reserved_tag(@t)", ("t", tag)))!, tag);
        foreach (var tags in new[] { new[] { "a", "a" }, [" a"], [""], [new string('t', 129)], ["bell\u0007"], [.. Enumerable.Range(0, 65).Select(i => $"t{i}")],
            ["Canal"], ["canal", "CANAL"] })
            Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT memory_check_tags(@t)", ("t", tags)));
        // What the database takes from the engine is checked again, never cast: tags that aren't words, text that isn't
        // text, a field it doesn't know.
        await Scoped.Mode(s, "off");
        var writer = await Scoped.Principal(s, "tags-house", "harness");
        foreach (var memory in new[] { """{"content":"DEMO: tags.","tags":[1]}""", """{"content":5}""", """{"content":"DEMO: colour.","colour":"red"}""",
            """{"content":"DEMO: importance.","importance":"3"}""", """{"content":"DEMO: a project number.","project":5}""",
            """{"content":"DEMO: a kind list.","kind":["episodic"]}""", """{"content":"DEMO: a scope flag.","scope":true}""",
            """{"content":"DEMO: a source object.","source":{"name":"claude"}}""", """{"content":"DEMO: a volatility.","volatility":1}""",
            """{"content":"DEMO: mixed case.","tags":["Canal","canal"]}""" })
            Assert.Equal("22023", await MemoryRows.Fails(s, "SELECT create_memory(@p,@m::jsonb,'mcp')", ("p", writer), ("m", memory)));
        Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE created_by=@p", ("p", writer)));
    }

    [PostgresFact] public async Task Get_and_list_serve_each_reader_its_own_scopes_and_filters_only_narrow()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "list");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var since = await MemoryLogins.Now(s);
            async Task<Guid> Make(Guid by, MemoryDraft draft) => (await Store.Create(by, draft, MemoryLogins.Mcp, default)).Id;
            var walk = await Make(who.Alex, new("DEMO: the towpath walk #walks", ["outdoors"], Kind: "episodic", Project: "lockside", Source: "claude"));
            var plot = await Make(who.Alex, new("DEMO: allotment plot nine #garden", Kind: "semantic", Source: "chatgpt"));
            var cafe = await Make(who.Alex, new("DEMO: the fictional cafe #walks", ["outdoors"], Scope: "shared"));
            var bins = await Make(who.House, new("DEMO: bins on Thursday", Scope: "shared"));
            var mine = new[] { walk, plot, cafe, bins };
            async Task<Guid[]> Ids(Guid reader, MemoryFilter filter) =>
                [.. (await Store.List(reader, filter with { Since = filter.Since ?? since }, default)).Items.Select(i => i.Id)];

            Assert.Equal([bins, cafe, plot, walk], await Ids(who.Alex, new(Limit: 50)));
            Assert.Equal([bins, cafe], await Ids(who.Sam, new(Limit: 50)));
            Assert.Equal([bins, cafe], await Ids(who.House, new(Limit: 50)));
            Assert.Equal([plot, walk], await Ids(who.Alex, new(Scope: "personal", Limit: 50)));
            // Naming a scope never widens: Sam asking for private memories gets only his own (none).
            Assert.Empty(await Ids(who.Sam, new(Scope: "private", Limit: 50)));
            Assert.Equal([cafe, walk], await Ids(who.Alex, new(Tags: ["Walks", "outdoors"], Limit: 50)));
            Assert.Equal([cafe], await Ids(who.Sam, new(Tags: ["walks"], Limit: 50)));
            Assert.Equal([plot], await Ids(who.Alex, new(Kind: "semantic", Limit: 50)));
            Assert.Equal([plot], await Ids(who.Alex, new(Source: "chatgpt", Limit: 50)));
            Assert.Equal([cafe, plot, walk], await Ids(who.Alex, new(Author: "alex", Limit: 50)));
            Assert.Equal([bins, cafe, plot, walk], await Ids(who.Alex, new(Status: "draft", Limit: 50)));
            Assert.Empty(await Ids(who.Alex, new(Status: "canonical", Limit: 50)));
            Assert.Empty(await Ids(who.Alex, new(Before: since)));
            // Paging: one at a time, newest first, each once.
            var paged = new List<Guid>();
            MemoryCursor? after = null;
            do
            {
                var page = await Store.List(who.Alex, new(Since: since, Limit: 1, After: after), default);
                paged.AddRange(page.Items.Select(i => i.Id));
                after = page.Next;
            } while (after is not null);
            Assert.Equal([bins, cafe, plot, walk], paged);
            // A preview cuts the text; a held memory is listed without its words (D-039): no text, tags or project, which
            // can carry the same words. Get gives them with the hold.
            var preview = (await Store.List(who.Alex, new(Since: since, Limit: 50, PreviewChars: 10), default)).Items.Single(i => i.Id == plot);
            Assert.Equal(("DEMO: allo", true), (preview.Content, preview.Truncated));
            await Scoped.Execute(s, "UPDATE memories SET hold='instructions',hold_score=0.9 WHERE id=@id", ("id", walk));
            var held = (await Store.List(who.Alex, new(Since: since, Limit: 50), default)).Items.Single(i => i.Id == walk);
            Assert.Equal((null, false, "instructions"), (held.Content, held.Truncated, held.Hold));
            Assert.Empty(held.Tags);
            Assert.Null(held.Project);
            // Nor do its tags or project select it, which would say what it holds: it is listed, unfiltered, alone.
            Assert.Equal([cafe], await Ids(who.Alex, new(Tags: ["walks"], Limit: 50)));
            Assert.Equal([cafe], await Ids(who.Alex, new(Tags: ["outdoors"], Limit: 50)));
            Assert.Empty(await Ids(who.Alex, new(Project: "lockside", Limit: 50)));
            Assert.Contains(walk, await Ids(who.Alex, new(Limit: 50)));
            Assert.Contains(walk, await Ids(who.Alex, new(Kind: "episodic", Limit: 50)));
            var whole = (await Store.Get(who.Alex, walk, default))!;
            Assert.Equal(("DEMO: the towpath walk #walks", "instructions", "lockside"), (whole.Content, whole.Hold, whole.Project?.Slug));
            Assert.Equal(["outdoors", "walks"], whole.Tags);
            // A memory in the trash is served to no one.
            await Scoped.Execute(s, """
                INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@id,'forget',@p,'mcp',now()+interval '14 days');
                UPDATE resources SET status='deleted' WHERE id=@id
                """, ("id", plot), ("p", who.Alex));
            Assert.Null(await Store.Get(who.Alex, plot, default));
            Assert.Equal([bins, cafe, walk], await Ids(who.Alex, new(Limit: 50)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.List(who.Alex, new(Limit: 51), default)));
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.List(who.Alex, new(Scope: "team"), default)));
            Assert.All(mine, id => Assert.NotEqual(Guid.Empty, id));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    [PostgresFact] public async Task A_project_a_write_names_is_found_or_made_in_the_memorys_own_scope()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "project");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var first = await Store.Create(who.Alex, new("DEMO: the first canal walk.", Project: " Canal-Walks "), MemoryLogins.Mcp, default);
            Assert.Equal(("canal-walks", true), (first.Project!.Slug, first.Project.Created));
            Assert.Equal("alex|private|alex|canal-walks|active", await MemoryLogins.Scalar(s,
                "SELECT subject_id||'|'||CASE WHEN subject_id='household' THEN 'shared' ELSE 'private' END||'|'||author_id||'|'||name||'|'||status FROM memory_projects WHERE id=@id",
                ("id", first.Project.Id)));
            Assert.Equal("created|{\"slug\": \"canal-walks\"}", await MemoryLogins.Scalar(s,
                "SELECT event||'|'||detail::text FROM memory_events WHERE project_id=@id", ("id", first.Project.Id)));
            var second = await Store.Create(who.Alex, new("DEMO: the second canal walk.", Project: "canal-walks"), MemoryLogins.Mcp, default);
            Assert.Equal((first.Project.Id, false), (second.Project!.Id, second.Project.Created));
            await Scoped.Execute(s, "INSERT INTO memory_project_aliases(project_id,subject_id,alias) VALUES(@p,'alex','towpath')", ("p", first.Project.Id));
            var third = await Store.Create(who.Alex, new("DEMO: the towpath again.", Project: "towpath"), MemoryLogins.Mcp, default);
            Assert.Equal(first.Project.Id, third.Project!.Id);
            // Family's project of the same name is another one, the household's.
            var family = await Store.Create(who.Alex, new("DEMO: a family canal walk.", Scope: "shared", Project: "canal-walks"), MemoryLogins.Mcp, default);
            Assert.NotEqual(first.Project.Id, family.Project!.Id);
            Assert.True(family.Project.Created);
            Assert.Equal("household|alex", await MemoryLogins.Scalar(s, "SELECT subject_id||'|'||author_id FROM memory_projects WHERE id=@id", ("id", family.Project.Id)));
            Assert.Equal("canal-walks", (await Store.Get(who.Sam, family.Id, default))!.Project!.Slug);
            Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: bad slug.", Project: "canal walks!"), MemoryLogins.Mcp, default)));
            // Listing by project: each reader's own projects of that name; one it does not read is unknown.
            var mine = (await Store.List(who.Alex, new(Project: "canal-walks", Limit: 50), default)).Items.Select(i => i.Id);
            Assert.Equal([family.Id, third.Id, second.Id, first.Id], mine);
            Assert.Equal([family.Id], (await Store.List(who.Sam, new(Project: "canal-walks", Limit: 50), default)).Items.Select(i => i.Id));
            Assert.Equal("unknown_project", await MemoryLogins.Refused(() => Store.List(who.Sam, new(Project: "towpath"), default)));
            Assert.Equal("unknown_project", await MemoryLogins.Refused(() => Store.List(who.House, new(Project: "no-such-project"), default)));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    // The engine's login reads the memory tables and writes them only through 033's functions, as migrate grants it.
    [PostgresFact] public async Task The_engine_login_writes_memories_only_through_the_functions()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "login");
        await Scoped.Mode(s, "off");
        await using var login = await TestLogin.Make(s, fixture.Name);
        var store = new MemoryStore(login.Source);
        var write = await store.Create(who.Alex, new("DEMO: written through the engine's login."), MemoryLogins.Key("login-key-0001"), default);
        Assert.Equal("DEMO: written through the engine's login.", (await store.Get(who.Alex, write.Id, default))!.Content);
        Assert.Equal(2, (await store.Append(who.Alex, write.Id, "DEMO: and again.", 1, MemoryLogins.Mcp, default)).Revision);
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "INSERT INTO memory_requests(principal_id,idempotency_key,operation,payload_hash,memory_id,result) VALUES(@p,'login-key-0002','create',repeat('0',64),@m,'{}')",
            ("p", who.Alex), ("m", write.Id)));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE memories SET content='DEMO: changed directly' WHERE id=@m", ("m", write.Id)));
        // A helper called directly runs with the login's own rights, so it writes nothing either.
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "SELECT memory_answered(@p,'login-key-0003','create',repeat('0',64),@m,'{}')",
            ("p", who.Alex), ("m", write.Id)));
        Assert.Contains("memory_requests", await MemoryRows.CheckedTables(s));
    }
}

// SB-P3-02: append, update and set_status, each with an expected revision and an idempotency key, within the limits,
// keeping the state each change replaces; and concurrent writes that never lose text.
public sealed class MemoryChangeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    async Task<string> Today() => (string)(await MemoryLogins.Scalar(fixture.Source, "SELECT install_today()::text"))!;

    async Task<string> Versions(Guid id) => (string)(await MemoryLogins.Scalar(fixture.Source, """
        SELECT coalesce(string_agg(revision||':'||status||':'||content,' / ' ORDER BY revision),'') FROM memory_versions WHERE memory_id=@id
        """, ("id", id)))!;

    [PostgresFact] public async Task Append_adds_a_dated_update_and_keeps_the_earlier_text_as_a_version()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "append");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: the canal walk."), MemoryLogins.Mcp, default)).Id;
        var appended = await Store.Append(who.Alex, id, "  DEMO: it rained.  ", 1, MemoryLogins.Mcp, default);
        Assert.Equal((2, "household", "draft"), (appended.Revision, appended.Subject, appended.Status));
        var today = await Today();
        Assert.Equal($"DEMO: the canal walk.\n\n[Update {today}]: DEMO: it rained.", (await Store.Get(who.Sam, id, default))!.Content);
        Assert.Equal("1:draft:DEMO: the canal walk.", await Versions(id));
        Assert.Equal("created:1 appended:2", await MemoryLogins.Scalar(s,
            "SELECT string_agg(event||':'||revision,' ' ORDER BY id) FROM memory_events WHERE memory_id=@id", ("id", id)));
        // A stale expected revision is refused with the current one, and nothing changes.
        var stale = await Assert.ThrowsAsync<MemoryRefused>(() => Store.Append(who.Sam, id, "DEMO: late.", 1, MemoryLogins.Mcp, default));
        Assert.Equal(("conflict", 2), (stale.Code, stale.Revision));
        Assert.Equal(2, (await Store.Get(who.Alex, id, default))!.Revision);
        // Without one, an append adds to the text as it stands.
        Assert.Equal(3, (await Store.Append(who.Sam, id, "DEMO: then sun.", null, MemoryLogins.Mcp, default)).Revision);
        Assert.EndsWith($"[Update {today}]: DEMO: then sun.", (await Store.Get(who.Alex, id, default))!.Content);
        // The whole text stays within the limit, or nothing is added.
        var big = (await Store.Create(who.Alex, new("DEMO " + new string('a', 131_000)), MemoryLogins.Mcp, default)).Id;
        Assert.Equal("too_large", await MemoryLogins.Refused(() => Store.Append(who.Alex, big, new string('b', 100), null, MemoryLogins.Mcp, default)));
        Assert.Equal(1, (await Store.Get(who.Alex, big, default))!.Revision);
        Assert.Equal("", await Versions(big));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Append(who.Alex, id, " ", null, MemoryLogins.Mcp, default)));
        Assert.Equal("unknown", await MemoryLogins.Refused(() => Store.Append(who.Alex, Guid.NewGuid(), "DEMO: to nothing.", null, MemoryLogins.Mcp, default)));
    }

    [PostgresFact] public async Task Update_changes_only_what_it_names_and_keeps_the_replaced_state()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "update");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: old text.", ["a"], Kind: "episodic", Importance: 2, Volatility: "state"), MemoryLogins.Mcp, default)).Id;
        // New text: its hashtags join the tags, and the volatility verdict goes with the old text (as upstream).
        var text = await Store.Update(who.Alex, id, new(Content: "DEMO: new text #fresh"), 1, MemoryLogins.Mcp, default);
        Assert.Equal(2, text.Revision);
        Assert.Equal(["content", "tags", "volatility"], text.Changed);
        var now = (await Store.Get(who.Alex, id, default))!;
        Assert.Equal(["a", "fresh"], now.Tags);
        Assert.Equal(("DEMO: new text #fresh", (string?)null, "episodic", (int?)2), (now.Content, now.Volatility, now.Kind, now.Importance));
        Assert.Equal("1:draft:DEMO: old text.", await Versions(id));
        Assert.Equal("""{"kind": "episodic", "importance": 2, "volatility": "state"}""", await MemoryLogins.Scalar(s,
            "SELECT state::text FROM memory_versions WHERE memory_id=@id AND revision=1", ("id", id)));
        Assert.Equal("""{"fields": ["content", "tags", "volatility"]}""", await MemoryLogins.Scalar(s,
            "SELECT detail::text FROM memory_events WHERE memory_id=@id AND revision=2", ("id", id)));
        // A tag replacement, the engine's own namespaces left off and named.
        var tags = await Store.Update(who.Sam, id, new(Tags: ["b", "Kind:semantic"]), 2, MemoryLogins.Mcp, default);
        Assert.Equal(3, tags.Revision);
        Assert.Equal(["tags"], tags.Changed);
        Assert.Equal(["kind:semantic"], tags.DroppedTags);
        Assert.Equal(["b"], (await Store.Get(who.Alex, id, default))!.Tags);
        var fields = await Store.Update(who.Alex, id, new(Kind: "semantic", Importance: 5), null, MemoryLogins.Mcp, default);
        Assert.Equal(["kind", "importance"], fields.Changed);
        Assert.Equal(["importance"], (await Store.Update(who.Alex, id, new(Clear: new HashSet<string> { "importance" }), null, MemoryLogins.Mcp, default)).Changed);
        Assert.Null((await Store.Get(who.Alex, id, default))!.Importance);
        // What it has already: nothing written.
        var same = await Store.Update(who.Alex, id, new(Tags: ["b"], Kind: "semantic"), null, MemoryLogins.Mcp, default);
        Assert.Equal((true, 5), (same.Unchanged, same.Revision));
        Assert.Equal(4L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_versions WHERE memory_id=@id", ("id", id)));
        // A project, made in the memory's scope, and cleared again.
        var project = await Store.Update(who.Alex, id, new(Project: "kitchen"), null, MemoryLogins.Mcp, default);
        Assert.Equal(("kitchen", true, "household"), (project.Project!.Slug, project.Project.Created, project.Subject));
        Assert.Null((await Store.Update(who.Alex, id, new(Clear: new HashSet<string> { "project" }), null, MemoryLogins.Mcp, default)).Project);
        Assert.Equal(("conflict", 7), await Refusal(() => Store.Update(who.Alex, id, new(Content: "DEMO: late."), 2, MemoryLogins.Mcp, default)));
        Assert.Equal("too_large", await MemoryLogins.Refused(() => Store.Update(who.Alex, id, new(Content: new string('a', 131_073)), null, MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Update(who.Alex, id, new(), null, MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Update(who.Alex, id, new(Kind: "procedural"), null, MemoryLogins.Mcp, default)));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Update(who.Alex, id, new(Clear: new HashSet<string> { "content" }), null, MemoryLogins.Mcp, default)));
        Assert.Equal(7, (await Store.Get(who.Alex, id, default))!.Revision);
    }

    static async Task<(string, int?)> Refusal(Func<Task> act)
    {
        var refused = await Assert.ThrowsAsync<MemoryRefused>(act);
        return (refused.Code, refused.Revision);
    }

    [PostgresFact] public async Task Set_status_moves_between_canonical_draft_and_deprecated()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "status");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: the boiler is serviced in March."), MemoryLogins.Mcp, default)).Id;
        var confirmed = await Store.SetStatus(who.Alex, id, "canonical", 1, MemoryLogins.Mcp, default);
        Assert.Equal(("canonical", 2), (confirmed.Status, confirmed.Revision));
        Assert.True((await Store.SetStatus(who.Alex, id, "canonical", null, MemoryLogins.Mcp, default)).Unchanged);
        Assert.Equal("deprecated", (await Store.SetStatus(who.Alex, id, "deprecated", 2, MemoryLogins.Mcp, default)).Status);
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.SetStatus(who.Alex, id, "final", null, MemoryLogins.Mcp, default)));
        Assert.Equal("1:draft:DEMO: the boiler is serviced in March. / 2:canonical:DEMO: the boiler is serviced in March.", await Versions(id));
        Assert.Equal("""{"to": "canonical", "from": "draft"}|{"to": "deprecated", "from": "canonical"}""", await MemoryLogins.Scalar(s,
            "SELECT string_agg(detail::text,'|' ORDER BY id) FROM memory_events WHERE memory_id=@id AND event='status_changed'", ("id", id)));
    }

    // A change that leaves the text alone keeps the revision its passages are made at (PassageSources.Revision), so a
    // status, tags, kind, importance, volatility or project change never puts its passages out of date or embeds the same
    // text again; new text, appended or replaced, moves it to the memory's revision.
    [PostgresFact] public async Task Only_new_text_moves_the_revision_its_passages_are_made_at()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "textrev");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: the shed key is under the pot."), MemoryLogins.Mcp, default)).Id;
        async Task<string> PassagesAt() => (string)(await MemoryLogins.Scalar(s, $"SELECT {PassageSources.Revision} FROM resources r WHERE r.id=@id", ("id", id)))!;
        Assert.Equal("memory:1", await PassagesAt());
        Assert.Equal(2, (await Store.SetStatus(who.Alex, id, "canonical", null, MemoryLogins.Mcp, default)).Revision);
        Assert.Equal(3, (await Store.Update(who.Alex, id, new(Tags: ["shed"], Kind: "semantic", Importance: 4, Volatility: "state", Project: "garden-shed"),
            null, MemoryLogins.Mcp, default)).Revision);
        Assert.Equal("memory:1", await PassagesAt());
        var appended = await Store.Append(who.Alex, id, "DEMO: and a spare in the drawer.", null, MemoryLogins.Mcp, default);
        Assert.Equal(("memory:4", 4), (await PassagesAt(), appended.Revision));
        await Store.Update(who.Alex, id, new(Tags: ["shed", "keys"]), null, MemoryLogins.Mcp, default);
        await Store.SetStatus(who.Alex, id, "deprecated", null, MemoryLogins.Mcp, default);
        Assert.Equal("memory:4", await PassagesAt());
        var replaced = await Store.Update(who.Alex, id, new(Content: "DEMO: the shed key hangs on the hook."), null, MemoryLogins.Mcp, default);
        Assert.Equal(("memory:7", 7), (await PassagesAt(), replaced.Revision));
    }

    [PostgresFact] public async Task An_idempotency_key_applies_a_retried_write_once()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "keys");
        await Scoped.Mode(s, "off");
        var first = await Store.Create(who.Alex, new("DEMO: saved once."), MemoryLogins.Key("demo-key-0001"), default);
        var again = await Store.Create(who.Alex, new("DEMO: saved once."), MemoryLogins.Key("demo-key-0001"), default);
        Assert.Equal((first.Id, false, true), (again.Id, first.Replayed, again.Replayed));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE content='DEMO: saved once.'"));
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memory_events WHERE memory_id=@id", ("id", first.Id)));
        Assert.Equal("key_reused", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: something else."), MemoryLogins.Key("demo-key-0001"), default)));
        Assert.Equal("key_reused", await MemoryLogins.Refused(() => Store.SetStatus(who.Alex, first.Id, "canonical", null, MemoryLogins.Key("demo-key-0001"), default)));
        // Keys are each login's own.
        Assert.NotEqual(first.Id, (await Store.Create(who.Sam, new("DEMO: saved once."), MemoryLogins.Key("demo-key-0001"), default)).Id);
        // A retried append adds its text once.
        var appended = await Store.Append(who.Alex, first.Id, "DEMO: once more.", 1, MemoryLogins.Key("demo-key-0002"), default);
        var retried = await Store.Append(who.Alex, first.Id, "DEMO: once more.", 1, MemoryLogins.Key("demo-key-0002"), default);
        Assert.Equal((2, 2, true), (appended.Revision, retried.Revision, retried.Replayed));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches((await Store.Get(who.Alex, first.Id, default))!.Content!, "once more"));
        var set = await Store.SetStatus(who.Alex, first.Id, "canonical", 2, MemoryLogins.Key("demo-key-0003"), default);
        var reset = await Store.SetStatus(who.Alex, first.Id, "canonical", 2, MemoryLogins.Key("demo-key-0003"), default);
        Assert.Equal(("canonical", 3, false, 3, true), (set.Status, set.Revision, set.Replayed, reset.Revision, reset.Replayed));
        Assert.True((await Store.Update(who.Alex, first.Id, new(Tags: ["kept"]), 3, MemoryLogins.Key("demo-key-0004"), default)) is { Replayed: false, Revision: 4 });
        Assert.True((await Store.Update(who.Alex, first.Id, new(Tags: ["kept"]), 3, MemoryLogins.Key("demo-key-0004"), default)) is { Replayed: true, Revision: 4 });
        // Two at once with one key: the second waits while the first's transaction is open (memory_replay's lock), then
        // is answered again, so there is one memory. Without the lock it would write a second memory and then fail on the
        // key's row once the first commits.
        await using (var c = await s.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            Guid raced;
            await using (var opener = Database.Command(c, tx, """
                SELECT (create_memory(@p,'{"content":"DEMO: raced once.","tags":[]}','mcp','DEMO client','demo-key-0005')->>'id')::uuid
                """, ("p", who.Alex)))
                raced = (Guid)(await opener.ExecuteScalarAsync())!;
            var second = Store.Create(who.Alex, new("DEMO: raced once."), MemoryLogins.Key("demo-key-0005"), default);
            Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(500)));
            await tx.CommitAsync();
            Assert.True(await second is { Replayed: true } answer && answer.Id == raced);
        }
        Assert.Equal(1L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE content='DEMO: raced once.'"));
        Assert.Equal("invalid", await MemoryLogins.Refused(() => Store.Create(who.Alex, new("DEMO: short key."), MemoryLogins.Key("short"), default)));
    }

    // Two writers at once (WRT-14): with the same expected revision one wins and the other is told; without one both
    // land in turn; and an edit waiting for a share of the same memory completes after it (the lock order of D-086).
    [PostgresFact] public async Task Concurrent_changes_never_lose_text()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "race");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            async Task<Task<T>> Behind<T>(string first, Guid memory, Func<Task<T>> second)
            {
                await using var c = await s.OpenConnectionAsync();
                await using var tx = await c.BeginTransactionAsync();
                await using (var cmd = Database.Command(c, tx, first, ("p", who.Alex), ("m", memory))) await cmd.ExecuteScalarAsync();
                var waiting = second();
                Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(500)));
                await tx.CommitAsync();
                return waiting;
            }
            var id = (await Store.Create(who.Alex, new("DEMO: the original."), MemoryLogins.Mcp, default)).Id;
            var loser = await Behind("SELECT update_memory(@p,@m,'{\"content\":\"DEMO: the winner.\"}',1,'mcp')", id,
                () => Store.Update(who.Alex, id, new(Content: "DEMO: the loser."), 1, MemoryLogins.Mcp, default));
            Assert.Equal(("conflict", 2), await Refusal(() => loser));
            Assert.Equal("DEMO: the winner.", (await Store.Get(who.Alex, id, default))!.Content);
            Assert.Equal("1:draft:DEMO: the original.", await Versions(id));

            var both = await Behind("SELECT append_memory(@p,@m,'DEMO: first.',NULL,'mcp')", id,
                () => Store.Append(who.Alex, id, "DEMO: second.", null, MemoryLogins.Mcp, default));
            Assert.Equal(4, (await both).Revision);
            var today = await Today();
            Assert.Equal($"DEMO: the winner.\n\n[Update {today}]: DEMO: first.\n\n[Update {today}]: DEMO: second.", (await Store.Get(who.Alex, id, default))!.Content);

            // A share locks the resources row, then moves it, and with it the memory (025's cascade). Here it moves it only
            // once the edit is waiting: an edit that locked the memory before its resources row would hold what the cascade
            // needs while waiting for what the share holds, and one of the two would fail with a deadlock (D-086). The edit
            // waits for the share, then lands.
            Task<MemoryWrite> sharedEdit;
            await using (var c = await s.OpenConnectionAsync())
            {
                await using var tx = await c.BeginTransactionAsync();
                await using (var lockRow = Database.Command(c, tx, "SELECT 1 FROM resources WHERE id=@m FOR UPDATE", ("m", id))) await lockRow.ExecuteScalarAsync();
                sharedEdit = Store.Update(who.Alex, id, new(Content: "DEMO: edited while shared."), null, MemoryLogins.Mcp, default);
                Assert.NotSame(sharedEdit, await Task.WhenAny(sharedEdit, Task.Delay(500)));
                await using (var share = Database.Command(c, tx, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@m", ("m", id)))
                    Assert.Equal(1, await share.ExecuteNonQueryAsync());
                await tx.CommitAsync();
            }
            var edit = await sharedEdit;
            Assert.Equal(("household", "shared", 5), (edit.Subject, edit.Scope, edit.Revision));
            Assert.Equal("DEMO: edited while shared.", (await Store.Get(who.Sam, id, default))!.Content);
        }
        finally { await Scoped.Mode(s, "off"); }
    }
}

// SB-P1-10's author-lock cases, now SB-P3-02's (D-130; memory.md section 5): with scopes on a private memory changes
// only by its owner, a Family one by its author or an admin, and a Family one with no person author by any reader of
// Family; with scopes off any admitted harness or app login changes any memory, as today.
public sealed class MemoryAuthorLockTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    // Each change a login may try: append, update and set status, each named in a refusal.
    async Task<string[]> Changes(Guid by, Guid memory, MemoryCall? call = null)
    {
        var how = call ?? MemoryLogins.Mcp;
        var answers = new List<string>();
        foreach (var change in new Func<Task<MemoryWrite>>[] {
            () => Store.Append(by, memory, "DEMO: an addition.", null, how, default),
            () => Store.Update(by, memory, new(Tags: [$"t{Guid.NewGuid():N}"]), null, how, default),
            () => Store.SetStatus(by, memory, "canonical", null, how, default) })
        {
            try { await change(); answers.Add("changed"); }
            catch (MemoryRefused refused) { answers.Add(refused.Code); }
        }
        return [.. answers];
    }

    static readonly string[] Changed = ["changed", "changed", "changed"];
    static readonly string[] Forbidden = ["forbidden", "forbidden", "forbidden"];
    static readonly string[] Unknown = ["unknown", "unknown", "unknown"];

    [PostgresFact] public async Task With_scopes_on_a_private_memory_changes_only_by_its_owner()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "private");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            var id = (await Store.Create(who.Alex, new("DEMO: Alex's private fictional plan."), MemoryLogins.Mcp, default)).Id;
            // Sam, an admin, and the household view: as for an unknown memory, and nothing changes.
            Assert.Equal(Unknown, await Changes(who.Sam, id));
            Assert.Equal(Unknown, await Changes(who.House, id));
            Assert.Equal(Unknown, await Changes(who.App, id, new("app")));
            Assert.Equal(1, (await Store.Get(who.Alex, id, default))!.Revision);
            Assert.Equal(Changed, await Changes(who.Alex, id));
        }
        finally { await Scoped.Mode(s, "off"); }
    }

    [PostgresFact] public async Task With_scopes_on_a_Family_memory_a_person_wrote_changes_by_its_author_or_an_admin()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "family");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var id = (await Store.Create(who.Alex, new("DEMO: Alex shared the fictional cafe.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            Assert.Equal(Forbidden, await Changes(who.Sam, id));
            Assert.Equal(Forbidden, await Changes(who.House, id));
            Assert.Equal(Forbidden, await Changes(who.App, id, new("app")));
            Assert.Equal(1, (await Store.Get(who.Sam, id, default))!.Revision);
            Assert.Equal(Changed, await Changes(who.Alex, id));
            await Scoped.Execute(s, "UPDATE memberships SET role='admin' WHERE person_id='sam'");
            Assert.Equal(Changed, await Changes(who.Sam, id));
            // Out of Family, its author reads it no more: as for an unknown memory.
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='alex'");
            Assert.Equal(Unknown, await Changes(who.Alex, id));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task With_scopes_on_a_Family_memory_with_no_person_author_changes_by_any_reader_of_Family()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "household");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        try
        {
            var id = (await Store.Create(who.House, new("DEMO: the bins go out on Thursday.", Scope: "Family"), MemoryLogins.Mcp, default)).Id;
            Assert.Equal(Changed, await Changes(who.Alex, id));
            Assert.Equal(Changed, await Changes(who.Sam, id));
            Assert.Equal(Changed, await Changes(who.House, id));
            Assert.Equal(Changed, await Changes(who.App, id, new("app")));
            await Scoped.Execute(s, "DELETE FROM memberships WHERE person_id='sam'");
            Assert.Equal(Unknown, await Changes(who.Sam, id));
        }
        finally
        {
            await MemoryLogins.Family(s);
            await Scoped.Mode(s, "off");
        }
    }

    [PostgresFact] public async Task With_scopes_off_every_admitted_login_changes_any_memory()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "off");
        await Scoped.Mode(s, "off");
        var id = (await Store.Create(who.Alex, new("DEMO: written by Alex's client."), MemoryLogins.Mcp, default)).Id;
        Assert.Equal(Changed, await Changes(who.Sam, id));
        Assert.Equal(Changed, await Changes(who.House, id));
        Assert.Equal(Changed, await Changes(who.App, id, new("app")));
        // Not an AI client or the app: no memory writes at all.
        var plugin = await Scoped.Principal(s, "off-plugin", "ingest", plugin: "cgm");
        var legacy = await Scoped.Principal(s, "off-legacy", "legacy");
        var revoked = await Scoped.Principal(s, "off-revoked", "harness", "sam");
        await Scoped.Execute(s, "UPDATE principals SET enabled=false WHERE id=@p", ("p", revoked));
        foreach (var login in new[] { plugin, legacy, revoked })
        {
            Assert.Equal(Forbidden, await Changes(login, id));
            Assert.Equal("forbidden", await MemoryLogins.Refused(() => Store.Create(login, new("DEMO: refused."), MemoryLogins.Mcp, default)));
        }
    }
}

// Every checked writer takes the recorded mode's row FOR SHARE before it writes, in a database of its own so that nothing
// private exists before the race: rhythm scopes off then refuses only for the memory the race wrote.
public sealed class MemoryScopesOffLockTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    MemoryStore Store => new(fixture.Source);

    // Every checked writer takes the recorded mode's row FOR SHARE before it writes (memory.md 6.2), so rhythm scopes
    // off, which takes it FOR UPDATE, never interleaves with one: a writer waits for the switch, and the switch for a
    // writer, whose private memory it then finds and refuses.
    [PostgresFact] public async Task Every_writer_waits_for_rhythm_scopes_off_and_the_switch_for_a_writer()
    {
        var s = fixture.Source;
        var who = await MemoryLogins.Make(s, "lock");
        await Scoped.Mode(s, "on");
        await MemoryLogins.Family(s);
        var timing = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB"))
        { Database = fixture.Name, Options = "-c lock_timeout=300" }.ConnectionString);
        try
        {
            // Nothing private yet, so the switch would go through; a private memory not yet committed holds it back, and
            // once that commits the switch finds it and refuses, leaving scopes on.
            Assert.Empty((await new Principals(s).OffPreview(default)).Private);
            await using (var writer = await s.OpenConnectionAsync())
            await using (var tx = await writer.BeginTransactionAsync())
            {
                await using (var cmd = Database.Command(writer, tx, "SELECT create_memory(@p,'{\"content\":\"DEMO: committed after the switch asked.\"}','mcp')", ("p", who.Sam)))
                    await cmd.ExecuteScalarAsync();
                var off = new Principals(s).SwitchOff(default);
                Assert.NotSame(off, await Task.WhenAny(off, Task.Delay(500)));
                await tx.CommitAsync();
                Assert.Equal([("sam", "private memories", 1L)], await off);
            }
            Assert.Equal("on", await MemoryLogins.Scalar(s, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            // While the switch holds the row, every writer waits, a Family one too.
            var memory = (await Store.Create(who.Alex, new("DEMO: locked fictional note."), MemoryLogins.Mcp, default)).Id;
            var family = (await Store.Create(who.Alex, new("DEMO: a family note.", Scope: "shared"), MemoryLogins.Mcp, default)).Id;
            var waiting = new MemoryStore(timing);
            await using (var hold = await s.OpenConnectionAsync())
            await using (var tx = await hold.BeginTransactionAsync())
            {
                await Database.Execute(hold, tx, "SELECT 1 FROM install_settings WHERE key='memory.scopes' FOR UPDATE", default);
                foreach (var write in new Func<Task>[] {
                    () => waiting.Create(who.Alex, new("DEMO: waits."), MemoryLogins.Mcp, default),
                    () => waiting.Create(who.Alex, new("DEMO: waits for Family.", Scope: "shared"), MemoryLogins.Mcp, default),
                    () => waiting.Append(who.Alex, memory, "DEMO: waits.", null, MemoryLogins.Mcp, default),
                    () => waiting.Update(who.Alex, family, new(Content: "DEMO: waits."), null, MemoryLogins.Mcp, default),
                    () => waiting.SetStatus(who.Alex, memory, "canonical", null, MemoryLogins.Mcp, default),
                    // A share and an un-share into a private scope (SB-P3-10, 048).
                    () => waiting.Share(who.Alex, memory, "shared", null, MemoryLogins.Mcp, default),
                    () => waiting.Share(who.Alex, family, "private", null, MemoryLogins.Mcp, default) })
                    Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(write)).SqlState);
                await tx.RollbackAsync();
            }
            Assert.Equal(0L, await MemoryLogins.Scalar(s, "SELECT count(*) FROM memories WHERE content LIKE 'DEMO: waits%'"));
        }
        finally
        {
            await timing.DisposeAsync();
            await Scoped.Mode(s, "off");
        }
    }
}
