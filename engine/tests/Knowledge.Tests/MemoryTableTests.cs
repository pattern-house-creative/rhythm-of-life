using Knowledge.Relationships;
using Npgsql;

namespace Knowledge.Tests;

// The native memory tables (migration 025, SB-P1-07; docs/dev/memory.md sections 2, 5, 8 and 10). Nothing writes a
// memory yet: these tests write rows the way phase 3's checked functions will, through the owner's connection (the
// tests run as the database's owner, as those functions will). Fictional people only: alex, sam and the household.
static class MemoryRows
{
    // Tests that depend on chronology choose distinct instants in the past. Independent database transactions
    // read the wall clock, which can repeat or step back; their insertion order does not guarantee timestamp order.
    public static Task SetCreationOrder(NpgsqlDataSource source, params Guid[] ids) => Run(source, """
        UPDATE memories m SET created_at=now()-(60+cardinality(@ids)-x.position)*interval '1 second'
        FROM unnest(@ids::uuid[]) WITH ORDINALITY x(id,position) WHERE m.id=x.id
        """, ("ids", ids));

    public static async Task Run(NpgsqlDataSource source, string sql, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, sql, args);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params (string, object?)[] args)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = Database.Command(c, null, sql, args);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    // The SQLSTATE a statement fails with.
    public static async Task<string> Fails(Func<Task> act) => (await Assert.ThrowsAsync<PostgresException>(act)).SqlState;
    public static Task<string> Fails(NpgsqlDataSource source, string sql, params (string, object?)[] args) =>
        Fails(() => Run(source, sql, args));

    // A memory's resources row: provider 'memory', the memory id as its id and external id (memory.md 10.3).
    public static Task Row(NpgsqlDataSource source, Guid id, string subject, string scope) => Run(source, """
        INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
        VALUES(@id,@s,'memory',@id::text,'memory',@scope,'DEMO memory','','current')
        """, ("id", id), ("s", subject), ("scope", scope));

    // A memory and its resources row, in the scope its subject gives it; `columns` and `values` add fields.
    public static async Task<Guid> Memory(NpgsqlDataSource source, string subject, string? author, string content,
        string columns = "", string values = "", params (string, object?)[] args)
    {
        var id = Guid.NewGuid();
        var scope = subject == "household" ? "shared" : "private";
        await Row(source, id, subject, scope);
        await Run(source, $"""
            INSERT INTO memories(id,subject_id,scope,author_id,content,source{columns})
            VALUES(@id,@s,@scope,@author,@content,'claude'{values})
            """, [("id", id), ("s", subject), ("scope", scope), ("author", author), ("content", content), .. args]);
        return id;
    }

    // The map's snapshot row for a memory's star (migration 025): provider 'memory', the map's catalog id, a snapshot, and
    // no memory behind it; a memory's own row never holds one.
    public static async Task<Guid> Star(NpgsqlDataSource source, Guid memory, string subject, string title)
    {
        var id = Guid.NewGuid();
        await Run(source, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status,relationship_snapshot)
            VALUES(@id,@s,'memory','relationship-engine:'||jsonb_build_array('memory',@m::text)::text,'memory',
              CASE WHEN @s='household' THEN 'shared' ELSE 'private' END,@t,'current',jsonb_build_object('Title',@t::text))
            """, ("id", id), ("s", subject), ("m", memory.ToString()), ("t", title));
        return id;
    }

    public static async Task<IReadOnlyList<string>> CheckedTables(NpgsqlDataSource source)
    {
        await using var cmd = source.CreateCommand("SELECT t::text FROM unnest(checked_tables()) t");
        await using var reader = await cmd.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return [.. tables.Order(StringComparer.Ordinal)];
    }

    public static async Task<Guid> Principal(NpgsqlDataSource source) =>
        await new Principals(source).System("memory-test", default);

    // A database of its own on the test server, dropped by the caller.
    public static async Task<NpgsqlDataSource> NewDatabase(string name)
    {
        var server = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!;
        await using (var admin = NpgsqlDataSource.Create(server))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        return NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
    }

    public static async Task DropDatabase(NpgsqlDataSource? source, string name)
    {
        if (source is not null) await source.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")!);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}

// A login set up as migrate sets up the engine's (Engine.GrantLogin), in one test database; dropped again by Dispose.
internal sealed class TestLogin : IAsyncDisposable
{
    readonly NpgsqlDataSource owner;
    public string Name { get; } = "sb_engine_" + Guid.NewGuid().ToString("N")[..12];
    public NpgsqlDataSource Source { get; private set; } = null!;
    TestLogin(NpgsqlDataSource owner) => this.owner = owner;

    public static async Task<TestLogin> Make(NpgsqlDataSource owner, string database)
    {
        var login = new TestLogin(owner);
        var password = Guid.NewGuid().ToString("N");
        await using (var c = await owner.OpenConnectionAsync())
        {
            await using (var format = Database.Command(c, null, "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L',@r,@p)", ("r", login.Name), ("p", password)))
                await Database.Execute(c, null, (string)(await format.ExecuteScalarAsync())!, default);
            await using var tx = await c.BeginTransactionAsync();
            await Engine.GrantLogin(c, tx, login.Name, default);
            await tx.CommitAsync();
        }
        login.Source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB"))
        { Database = database, Username = login.Name, Password = password, Pooling = false }.ConnectionString);
        return login;
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await MemoryRows.Run(owner, $"DROP OWNED BY {Name}; DROP ROLE {Name}");
    }
}

public sealed class MemoryMigrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static HashSet<string> Set(params string[] names) => [.. names];

    [PostgresFact] public async Task The_memory_tables_hold_the_contracts_fields_and_nothing_writes_them_yet()
    {
        var tables = await MemoryRows.CheckedTables(fixture.Source);
        Assert.Equal(["admin_events", "capsule_revisions", "insight_events", "made_links", "map_link_dismissals", "memories", "memory_checks", "memory_citations", "memory_commitments", "memory_contradictions", "memory_decision_states", "memory_decisions", "memory_duplicate_flags", "memory_events", "memory_insight_candidates", "memory_insight_events", "memory_link_dismissals", "memory_mirrors", "memory_origins",
            "memory_project_aliases", "memory_projects", "memory_requests", "memory_resurface_dismissals", "memory_retraction_flags", "memory_trash", "memory_upstream_tags", "memory_versions",
            "memory_work", "memory_work_claims", "memory_work_states", "offboardings", "recall_counts", "recall_log"], tables);
        // Inert until phase 3's tools: a migrated, started install has no memory, project, version, event, trash row,
        // idempotency key, origin (043: an import's) or recall log row (042, off until engine.recallLog says on). Migration 033's checked writers exist, and nothing calls them yet (Core/Memories.cs). The admin
        // audit (037) holds only what the start did: the people it came to know.
        foreach (var table in tables.Where(t => t != "admin_events"))
            Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source, $"SELECT count(*) FROM {table}"));
        Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source,
            "SELECT count(*) FROM admin_events WHERE NOT (event='person_known' AND channel='system' AND job='start')"));
        // The fields memory.md's ownership and scope rules need, so phase 3 adds functions and no columns for them.
        async Task<HashSet<string>> Columns(string table)
        {
            await using var cmd = fixture.Source.CreateCommand("SELECT column_name FROM information_schema.columns WHERE table_name=@t");
            cmd.Parameters.AddWithValue("t", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            var names = new HashSet<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            return names;
        }
        Assert.Superset(Set("id", "provider", "subject_id", "scope", "author_id", "created_by", "content", "tags", "kind", "status",
            "importance", "volatility", "source", "project_id", "capsule", "capsule_slot", "hold", "hold_score", "valid_from",
            "valid_until", "revision", "created_at", "updated_at"), await Columns("memories"));
        Assert.Superset(Set("memory_id", "revision", "subject_id", "scope", "content", "tags", "status", "valid_from", "valid_until",
            "state", "written_at", "retired_at"), await Columns("memory_versions"));
        Assert.Superset(Set("memory_id", "project_id", "subject_id", "revision", "event", "principal_id", "channel", "client",
            "detail", "at"), await Columns("memory_events"));
        Assert.Superset(Set("memory_id", "nonce", "reason", "trashed_by", "channel", "trashed_at", "purge_after", "links"),
            await Columns("memory_trash"));
        Assert.Superset(Set("id", "subject_id", "slug", "name", "description", "status", "author_id", "created_by", "revision"),
            await Columns("memory_projects"));
        Assert.Superset(Set("project_id", "subject_id", "alias"), await Columns("memory_project_aliases"));
        Assert.Superset(Set("subject_id", "revision", "changed_at"), await Columns("capsule_revisions"));
        Assert.Superset(Set("link_id", "source_id", "target_id", "type", "verification", "evidence", "lifecycle", "made_by", "created_at"),
            await Columns("made_links"));
        Assert.Superset(Set("principal_id", "idempotency_key", "operation", "payload_hash", "memory_id", "result", "received_at"),
            await Columns("memory_requests"));
        // The map's links keep the table and the key they had.
        Assert.DoesNotContain("link_id", await Columns("relationships"));
        // Nor any memory row in the catalog yet.
        Assert.Equal(0L, await MemoryRows.Scalar(fixture.Source, "SELECT count(*) FROM resources WHERE provider='memory'"));
    }

    [PostgresFact] public async Task The_migration_applies_to_an_install_that_already_has_records_and_links()
    {
        // An existing install: every migration before this one, a document, a map link and an ingest link.
        var name = "rhythm_test_" + Guid.NewGuid().ToString("N")[..12];
        var source = await MemoryRows.NewDatabase(name);
        try
        {
            var before = Database.Migrations().TakeWhile(m => !m.Version.EndsWith("_memories.sql", StringComparison.Ordinal)).ToArray();
            var mine = Database.Migrations().Single(m => m.Version.EndsWith("_memories.sql", StringComparison.Ordinal));
            foreach (var (_, sql) in before) await MemoryRows.Run(source, sql);
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            foreach (var id in new[] { a, b })
                await MemoryRows.Run(source, """
                    INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                    VALUES(@id,'household','documents',@id::text,'document','shared','DEMO document','DEMO text','current')
                    """, ("id", id));
            await MemoryRows.Run(source, """
                INSERT INTO relationships(source_id,target_id,type,verification,evidence) VALUES(@a,@b,'contains','verified','');
                INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine) VALUES(@b,@a,'similar','suggested','','cross-source-v1')
                """, ("a", a), ("b", b));
            await MemoryRows.Run(source, mine.Sql);
            Assert.Equal(2L, await MemoryRows.Scalar(source, "SELECT count(*) FROM relationships"));
            Assert.Equal(0L, await MemoryRows.Scalar(source, "SELECT count(*) FROM made_links"));
            // The documents keep working as before: the memory guard looks only at provider 'memory'.
            await MemoryRows.Run(source, "UPDATE resources SET title='DEMO document, renamed' WHERE id=@a", ("a", a));
        }
        finally { await MemoryRows.DropDatabase(source, name); }
    }

    // A migration that replaces a function (CREATE OR REPLACE) drops the search path an earlier one pinned on it, so it
    // must set it again: checked_tables() (025), which a migration adding a checked table rebuilds from the list in force
    // (043's way), is called by the engine's start (Program.GrantLogin) and casts unqualified names. Checked after every
    // migration, not only at the end, where a later migration's own setting hides a gap (052 had one until 053 ran):
    // checked_tables() after each, and every function after each migration from 049 on. 001 to 048 are on an install and
    // never edited again, so a gap there that a later one closed (015's dashboard functions, 019) stays history.
    [PostgresFact] public async Task Every_migration_that_replaces_a_function_sets_its_search_path_again()
    {
        var name = "rhythm_test_" + Guid.NewGuid().ToString("N")[..12];
        var source = await MemoryRows.NewDatabase(name);
        try
        {
            const string Pinned = """
                SELECT p.oid::regprocedure::text FROM pg_proc p
                WHERE p.pronamespace=current_schema()::regnamespace
                  AND EXISTS(SELECT 1 FROM unnest(coalesce(p.proconfig,'{}'::text[])) c WHERE c LIKE 'search_path=pg\_catalog, %')
                """;
            const string All = "SELECT p.oid::regprocedure::text FROM pg_proc p WHERE p.pronamespace=current_schema()::regnamespace";
            async Task<HashSet<string>> Names(string sql)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                await using var cmd = source.CreateCommand(sql);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) names.Add(reader.GetString(0));
                return names;
            }
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            var checkedTables = false;
            foreach (var (version, sql) in Database.Migrations())
            {
                await MemoryRows.Run(source, sql);
                var now = await Names(Pinned);
                var lost = pinned.Intersect(await Names(All)).Except(now).Order(StringComparer.Ordinal).ToArray();
                var number = int.Parse(version["Knowledge.Migrations.".Length..][..3], System.Globalization.CultureInfo.InvariantCulture);
                if (number <= 48) lost = [.. lost.Where(f => f == "checked_tables()")];
                Assert.True(lost.Length == 0, $"{version} left {string.Join(", ", lost)} without the search path set before it");
                checkedTables |= now.Contains("checked_tables()");
                pinned = now;
            }
            Assert.True(checkedTables);
            Assert.Contains("checked_tables()", pinned);
        }
        finally { await MemoryRows.DropDatabase(source, name); }
    }

    [PostgresFact] public async Task The_migration_stops_on_a_catalog_that_already_uses_the_provider_name()
    {
        var name = "rhythm_test_" + Guid.NewGuid().ToString("N")[..12];
        var source = await MemoryRows.NewDatabase(name);
        try
        {
            foreach (var (_, sql) in Database.Migrations().TakeWhile(m => !m.Version.EndsWith("_memories.sql", StringComparison.Ordinal)))
                await MemoryRows.Run(source, sql);
            await MemoryRows.Run(source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                VALUES(gen_random_uuid(),'household','memory','x1','note','shared','DEMO plugin row','DEMO','current')
                """);
            var mine = Database.Migrations().Single(m => m.Version.EndsWith("_memories.sql", StringComparison.Ordinal));
            Assert.Equal("P0001", await MemoryRows.Fails(source, mine.Sql));
        }
        finally { await MemoryRows.DropDatabase(source, name); }
    }

    // No adapter or plugin may register the provider name the native memories use.
    [Fact] public void The_provider_name_memory_is_reserved()
    {
        Assert.Contains("memory", ProviderPolicies.Reserved);
        Assert.Throws<ArgumentException>(() => new ProviderPolicies(new Dictionary<string, ProviderPolicy> { ["memory"] = new(SourceAuthority.LiveSource) }));
        Assert.DoesNotContain("memory", ProviderPolicies.Core.Keys);
    }
}

public sealed class MemoryTableTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    NpgsqlDataSource Source => fixture.Source;

    [PostgresFact] public async Task A_memory_keeps_the_subject_and_scope_of_its_resources_row()
    {
        var cafe = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: Alex likes the fictional cafe on the corner.");
        async Task<string> Where(Guid id) => (string)(await MemoryRows.Scalar(Source,
            "SELECT subject_id||' '||scope||' '||coalesce(author_id,'-') FROM memories WHERE id=@id", ("id", id)))!;
        // A share changes the resources row's subject in place (memory.md 10.3), and the memory follows with its author.
        await MemoryRows.Run(Source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", cafe));
        Assert.Equal("household shared alex", await Where(cafe));
        // An un-share returns it to its author's private scope, never anyone else's.
        Assert.Equal("23514", await MemoryRows.Fails(Source, "UPDATE resources SET subject_id='sam',scope='private' WHERE id=@id", ("id", cafe)));
        await MemoryRows.Run(Source, "UPDATE resources SET subject_id='alex',scope='private' WHERE id=@id", ("id", cafe));
        Assert.Equal("alex private alex", await Where(cafe));
        // A Family memory with no person author is the household's: there is no one to be private to.
        var bins = await MemoryRows.Memory(Source, "household", null, "DEMO: the bins go out on Thursday.");
        Assert.Equal("household shared -", await Where(bins));
        Assert.Equal("23514", await MemoryRows.Fails(Source, "UPDATE resources SET subject_id='sam',scope='private' WHERE id=@id", ("id", bins)));
        // The memory's own copy of its subject never moves apart from its row, and the row stays while the memory does.
        Assert.Equal("23503", await MemoryRows.Fails(Source, "UPDATE memories SET subject_id='household',scope='shared' WHERE id=@id", ("id", cafe)));
        Assert.Equal("23503", await MemoryRows.Fails(Source, "DELETE FROM resources WHERE id=@id", ("id", cafe)));
        // A private memory is its author's own; the household is never an author; a person's row is never shared.
        Assert.Equal("23514", await MemoryRows.Fails(() => MemoryRows.Memory(Source, "sam", "alex", "DEMO: written into another person's scope.")));
        Assert.Equal("23514", await MemoryRows.Fails(() => MemoryRows.Memory(Source, "sam", null, "DEMO: private with no author.")));
        Assert.Equal("23514", await MemoryRows.Fails(() => MemoryRows.Memory(Source, "household", "household", "DEMO: the household as author.")));
        var shared = Guid.NewGuid();
        await MemoryRows.Row(Source, shared, "alex", "shared");
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO memories(id,subject_id,scope,author_id,content,source) VALUES(@id,'alex','shared','alex','DEMO','claude')", ("id", shared)));
        // Only a memory row of its own: another provider's row, or one in another scope, is no memory's.
        var document = Guid.NewGuid();
        await MemoryRows.Run(Source, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
            VALUES(@id,'alex','documents',@id::text,'document','private','DEMO document','DEMO','current')
            """, ("id", document));
        Assert.Equal("23503", await MemoryRows.Fails(Source,
            "INSERT INTO memories(id,subject_id,scope,author_id,content,source) VALUES(@id,'alex','private','alex','DEMO','claude')", ("id", document)));
        var elsewhere = Guid.NewGuid();
        await MemoryRows.Row(Source, elsewhere, "sam", "private");
        Assert.Equal("23503", await MemoryRows.Fails(Source,
            "INSERT INTO memories(id,subject_id,scope,author_id,content,source) VALUES(@id,'alex','private','alex','DEMO','claude')", ("id", elsewhere)));
    }

    [PostgresFact] public async Task The_columns_refuse_what_the_contract_does_not_allow()
    {
        Task<Guid> With(string columns, string values, params (string, object?)[] args) =>
            MemoryRows.Memory(Source, "alex", "alex", "DEMO: a fictional fact.", columns, values, args);
        foreach (var (columns, values) in new[]
        {
            (",status", ",'trusted'"), (",kind", ",'fact'"), (",importance", ",6"), (",importance", ",0"), (",volatility", ",'forever'"),
            (",tags", ",array_fill('t'::text,ARRAY[65])"), (",tags", ",ARRAY['a',NULL]"),
            (",valid_from,valid_until", ",now(),now()-interval '1 day'"), (",valid_from,valid_until", ",now(),now()"),
            (",valid_until", ",now()-interval '1 day'"),
            (",capsule", ",'core'"), (",capsule,capsule_slot", ",'core','decisions'"), (",capsule,capsule_slot", ",'project','decisions'"),
            (",capsule_slot", ",'identity'"), (",hold_score", ",1.5"), (",hold", ",'Instruction phrase'"), (",provider", ",'documents'"),
        })
            Assert.Contains(await MemoryRows.Fails(() => With(columns, values)), new[] { "23514", "23503" });
        var unlabelled = Guid.NewGuid();
        await MemoryRows.Row(Source, unlabelled, "alex", "private");
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO memories(id,subject_id,scope,author_id,content,source) VALUES(@id,'alex','private','alex','DEMO','Not A Label')", ("id", unlabelled)));
        // Text is measured in bytes of UTF-8, up to 131 072 (D-042), and is never empty.
        Assert.Equal("23514", await MemoryRows.Fails(() => MemoryRows.Memory(Source, "alex", "alex", "")));
        await MemoryRows.Memory(Source, "alex", "alex", new string('é', 65536));
        Assert.Equal("23514", await MemoryRows.Fails(() => MemoryRows.Memory(Source, "alex", "alex", new string('é', 65537))));
        // What the contract does allow: an unknown start, a held row with its score, a core slot, upstream's words.
        await With(",valid_from,valid_until", ",'-infinity',now()");
        await With(",valid_from,valid_until", ",'-infinity',now()-interval '1 day'");
        await With(",valid_until", ",now()+interval '1 day'");
        // A version keeps the same rules for its start and end and its tags.
        var kept = await With("", "");
        foreach (var (columns, values) in new[]
        {
            (",valid_from,valid_until", ",now()-interval '1 day',now()-interval '2 days'"), (",valid_from,valid_until", ",now(),now()"),
            (",tags", ",ARRAY['a',NULL]"), (",tags", ",array_fill('t'::text,ARRAY[65])"),
        })
            Assert.Equal("23514", await MemoryRows.Fails(Source, $"""
                INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,written_at{columns})
                VALUES(@m,1,'alex','private','DEMO: an older fictional fact.','draft',now()-interval '1 day'{values})
                """, ("m", kept)));
        // A memory closed after the fact still changes, twice: written five days ago with no start, an edit two days ago
        // ends it as of four days ago (after its writing, so the memory takes it), and two more edits follow, each
        // retiring the state before it. Those states end before they were written, and the versions take them.
        var closed = await With(",created_at,updated_at", ",now()-interval '5 days',now()-interval '5 days'");
        async Task Change(int revision, string at, string content, string? until)
        {
            await MemoryRows.Run(Source, $"""
                INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,valid_from,valid_until,written_at)
                SELECT id,revision,subject_id,scope,content,status,valid_from,valid_until,updated_at FROM memories WHERE id=@m;
                UPDATE memories SET revision=revision+1,updated_at=now()-interval '{at}',content=@c,
                  valid_until={(until is null ? "valid_until" : "now()-interval '" + until + "'")} WHERE id=@m AND revision=@r
                """, ("m", closed), ("c", content), ("r", revision));
            Assert.Equal(revision + 1, await MemoryRows.Scalar(Source, "SELECT revision FROM memories WHERE id=@m", ("m", closed)));
        }
        await Change(1, "2 days", "DEMO: a fictional fact, true until day two.", "4 days");
        await Change(2, "1 day", "DEMO: a fictional fact, true until day two, reworded.", null);
        await Change(3, "0 days", "DEMO: a fictional fact, true until day two, reworded again.", null);
        Assert.Equal(3L, await MemoryRows.Scalar(Source,
            "SELECT count(*) FROM memory_versions WHERE memory_id=@m AND (revision=1 OR valid_until<written_at)", ("m", closed)));
        await With(",hold,hold_score", ",'instructions',1.2");
        await With(",capsule,capsule_slot,status,kind,importance,volatility", ",'core','identity','canonical','semantic',4,'durable'");
        await With(",tags", ",ARRAY['garden','project:allotment']");
    }

    [PostgresFact] public async Task A_memory_stays_in_projects_of_its_own_scope()
    {
        var who = await MemoryRows.Principal(Source);
        async Task<Guid> Project(string subject, string? author, string slug)
        {
            var id = Guid.NewGuid();
            await MemoryRows.Run(Source, """
                INSERT INTO memory_projects(id,subject_id,slug,name,author_id,created_by) VALUES(@id,@s,@slug,'DEMO project',@a,@p)
                """, ("id", id), ("s", subject), ("slug", slug), ("a", author), ("p", who));
            return id;
        }
        var garden = await Project("alex", "alex", "garden");
        // A slug is unique in its scope; Family may have its own; a private project is its owner's.
        Assert.Equal("23505", await MemoryRows.Fails(() => Project("alex", "alex", "garden")));
        var familyGarden = await Project("household", null, "garden");
        Assert.Equal("23514", await MemoryRows.Fails(() => Project("sam", "alex", "garden")));
        Assert.Equal("23514", await MemoryRows.Fails(() => Project("alex", "alex", "Not A Slug")));
        // An alias names one project in its scope.
        await MemoryRows.Run(Source, "INSERT INTO memory_project_aliases(project_id,subject_id,alias) VALUES(@p,'alex','allotment')", ("p", garden));
        var kitchen = await Project("alex", "alex", "kitchen");
        Assert.Equal("23505", await MemoryRows.Fails(Source,
            "INSERT INTO memory_project_aliases(project_id,subject_id,alias) VALUES(@p,'alex','allotment')", ("p", kitchen)));
        Assert.Equal("23503", await MemoryRows.Fails(Source,
            "INSERT INTO memory_project_aliases(project_id,subject_id,alias) VALUES(@p,'household','plot')", ("p", garden)));
        await MemoryRows.Run(Source, "INSERT INTO memory_project_aliases(project_id,subject_id,alias) VALUES(@p,'household','allotment')", ("p", familyGarden));
        // A memory sits in a project of its own scope only, so a Family reader never sees a private project's name.
        var beans = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: the beans went in on the fictional allotment.",
            ",project_id", ",@project", ("project", garden));
        Assert.Equal("23503", await MemoryRows.Fails(() => MemoryRows.Memory(Source, "household", null, "DEMO: Family note.",
            ",project_id", ",@project", ("project", garden))));
        // Sharing it moves it out of the private project first.
        Assert.Equal("23503", await MemoryRows.Fails(Source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", beans)));
        await MemoryRows.Run(Source, "UPDATE memories SET project_id=NULL WHERE id=@id", ("id", beans));
        await MemoryRows.Run(Source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", beans));
        // Deleting a project keeps its memories: its delete function clears them first; its aliases go with it.
        var peas = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: peas next year.", ",project_id", ",@project", ("project", garden));
        Assert.Equal("23503", await MemoryRows.Fails(Source, "DELETE FROM memory_projects WHERE id=@p", ("p", garden)));
        await MemoryRows.Run(Source, "UPDATE memories SET project_id=NULL WHERE project_id=@p; DELETE FROM memory_projects WHERE id=@p", ("p", garden));
        Assert.Equal(0L, await MemoryRows.Scalar(Source, "SELECT count(*) FROM memory_project_aliases WHERE project_id=@p", ("p", garden)));
        Assert.Equal("DEMO: peas next year.", await MemoryRows.Scalar(Source, "SELECT content FROM memories WHERE id=@id", ("id", peas)));
    }

    [PostgresFact] public async Task The_audit_trail_is_insert_only_and_outlives_a_memory_deleted_for_good()
    {
        var who = await MemoryRows.Principal(Source);
        var note = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: the second draft of a fictional plan.", ",revision,created_by", ",2,@p", ("p", who));
        await MemoryRows.Run(Source, """
            INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,written_at)
            VALUES(@m,1,'alex','private','DEMO: the first draft of a fictional plan.','draft',now()-interval '1 hour');
            INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client) VALUES
              (@m,'alex',1,'created',@p,'mcp','DEMO client'),(@m,'alex',2,'updated',@p,'mcp','DEMO client');
            INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@m,'forget',@p,'mcp',now()+interval '14 days');
            INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel) VALUES(@m,'alex',2,'deleted',@p,'mcp')
            """, ("m", note), ("p", who));
        // Insert-only, for the owner too.
        Assert.Equal("42501", await MemoryRows.Fails(Source, "UPDATE memory_events SET event='edited' WHERE memory_id=@m", ("m", note)));
        Assert.Equal("42501", await MemoryRows.Fails(Source, "DELETE FROM memory_events WHERE memory_id=@m", ("m", note)));
        Assert.Equal("42501", await MemoryRows.Fails(Source, "TRUNCATE memory_events"));
        // Only the operator on the host acts without a principal; an event's detail stays small (no memory text).
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO memory_events(memory_id,subject_id,event,channel) VALUES(@m,'alex','restored','mcp')", ("m", note)));
        await MemoryRows.Run(Source, "INSERT INTO memory_events(memory_id,subject_id,event,channel) VALUES(@m,'alex','exported','cli')", ("m", note));
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO memory_events(memory_id,subject_id,event,principal_id,channel,detail) VALUES(@m,'alex','noted',@p,'mcp',jsonb_build_object('x',repeat('y',4000)))",
            ("m", note), ("p", who)));
        // The trash keeps a memory 1 to 365 days (D-036); its nonce is unique.
        var tooLong = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: kept too long or too briefly.");
        foreach (var retention in new[] { "400 days", "366 days", "12 hours", "1 minute" })
            Assert.Equal("23514", await MemoryRows.Fails(Source, $"""
                INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after)
                VALUES(@m,'forget',@p,'mcp',now()+interval '{retention}')
                """, ("m", tooLong), ("p", who)));
        foreach (var retention in new[] { "1 day", "365 days" })
            await MemoryRows.Run(Source, $"""
                INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@m,'forget',@p,'mcp',now()+interval '{retention}');
                DELETE FROM memory_trash WHERE memory_id=@m
                """, ("m", tooLong), ("p", who));
        // Deleted for good: its versions and trash row go with it, the audit stays and holds none of its words.
        await MemoryRows.Run(Source, "DELETE FROM memories WHERE id=@m; DELETE FROM resources WHERE id=@m", ("m", note));
        Assert.Equal(0L, await MemoryRows.Scalar(Source, "SELECT count(*) FROM memory_versions WHERE memory_id=@m", ("m", note)));
        Assert.Equal(0L, await MemoryRows.Scalar(Source, "SELECT count(*) FROM memory_trash WHERE memory_id=@m", ("m", note)));
        Assert.Equal(4L, await MemoryRows.Scalar(Source, "SELECT count(*) FROM memory_events WHERE memory_id=@m", ("m", note)));
        Assert.Equal(0L, await MemoryRows.Scalar(Source,
            "SELECT count(*) FROM memory_events WHERE memory_id=@m AND (detail::text LIKE '%draft%' OR client LIKE '%plan%')", ("m", note)));
    }

    [PostgresFact] public async Task A_capsule_revision_moves_when_a_capsule_slot_changes()
    {
        async Task<long?> Revision(string subject) =>
            (long?)await MemoryRows.Scalar(Source, "SELECT revision FROM capsule_revisions WHERE subject_id=@s", ("s", subject));
        var who = await MemoryRows.Principal(Source);
        var plain = await MemoryRows.Memory(Source, "sam", "sam", "DEMO: an ordinary memory.");
        Assert.Null(await Revision("sam"));
        var identity = await MemoryRows.Memory(Source, "sam", "sam", "DEMO: Sam is a fictional beekeeper.",
            ",capsule,capsule_slot,status", ",'core','identity','canonical'");
        Assert.Equal(1L, await Revision("sam"));
        await MemoryRows.Run(Source, "UPDATE memories SET content='DEMO: Sam keeps fictional bees.',revision=2 WHERE id=@id", ("id", identity));
        Assert.Equal(2L, await Revision("sam"));
        // A row that is no capsule slot, or a change that changes nothing, leaves it.
        await MemoryRows.Run(Source, "UPDATE memories SET content='DEMO: still ordinary.' WHERE id=@id", ("id", plain));
        await MemoryRows.Run(Source, "UPDATE memories SET content=content WHERE id=@id", ("id", identity));
        Assert.Equal(2L, await Revision("sam"));
        // Into the trash and back.
        await MemoryRows.Run(Source, "INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@id,'forget',@p,'app',now()+interval '14 days')",
            ("id", identity), ("p", who));
        Assert.Equal(3L, await Revision("sam"));
        await MemoryRows.Run(Source, "DELETE FROM memory_trash WHERE memory_id=@id", ("id", identity));
        Assert.Equal(4L, await Revision("sam"));
        // A share changes both scopes' capsules; an un-share again.
        var before = await Revision("household") ?? 0;
        await MemoryRows.Run(Source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", identity));
        Assert.Equal((5L, before + 1), (await Revision("sam"), await Revision("household")));
        await MemoryRows.Run(Source, "UPDATE resources SET subject_id='sam',scope='private' WHERE id=@id", ("id", identity));
        Assert.Equal((6L, before + 2), (await Revision("sam"), await Revision("household")));
        // Taken out of its slot, and deleted for good.
        await MemoryRows.Run(Source, "UPDATE memories SET capsule=NULL,capsule_slot=NULL WHERE id=@id", ("id", identity));
        Assert.Equal(7L, await Revision("sam"));
        await MemoryRows.Run(Source, "UPDATE memories SET capsule='core',capsule_slot='principles' WHERE id=@id", ("id", identity));
        Assert.Equal(8L, await Revision("sam"));
        await MemoryRows.Run(Source, "DELETE FROM memories WHERE id=@id", ("id", identity));
        Assert.Equal(9L, await Revision("sam"));
    }

    [PostgresFact] public async Task Made_links_have_their_own_table_id_and_maker_and_the_map_leaves_them()
    {
        var who = await MemoryRows.Principal(Source);
        var walk = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: the fictional canal walk.");
        var map = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: a fictional map of the canal.");
        var other = Guid.NewGuid();
        await MemoryRows.Run(Source, """
            INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
            VALUES(@id,'alex','documents',@id::text,'document','private','DEMO canal guide','DEMO','current')
            """, ("id", other));
        Guid link = Guid.NewGuid(), toGuide = Guid.NewGuid();
        await MemoryRows.Run(Source, """
            INSERT INTO made_links(link_id,source_id,target_id,type,evidence,made_by) VALUES
              (@link,@a,@b,'relates_to','a person linked them',@p),(@guide,@a,@c,'similar','a person linked them too',@p);
            INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine) VALUES(@a,@c,'similar','suggested','x','cross-source-v1')
            """, ("a", walk), ("b", map), ("c", other), ("link", link), ("guide", toGuide), ("p", who));
        // A made link and the map's link of the same type between the same records both exist, and the map's upsert
        // (RelationshipEngine) still finds its own.
        await MemoryRows.Run(Source, """
            INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine) VALUES(@a,@c,'similar','suggested','y','cross-source-v1')
            ON CONFLICT(source_id,target_id,type) DO UPDATE SET evidence=excluded.evidence WHERE relationships.engine=excluded.engine
            """, ("a", walk), ("c", other));
        Assert.Equal("y", await MemoryRows.Scalar(Source, "SELECT evidence FROM relationships WHERE source_id=@a AND target_id=@c", ("a", walk), ("c", other)));
        // A made link has a maker (only one an import brought has none, marked imported: 043), a type of the map's shape,
        // two different ends, and one link per ends and type.
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO made_links(link_id,source_id,target_id,type) VALUES(gen_random_uuid(),@b,@a,'follows')", ("a", walk), ("b", map)));
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(gen_random_uuid(),@b,@a,'Not A Type',@p)", ("a", walk), ("b", map), ("p", who)));
        Assert.Equal("23514", await MemoryRows.Fails(Source,
            "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(gen_random_uuid(),@a,@a,'follows',@p)", ("a", walk), ("p", who)));
        Assert.Equal("23505", await MemoryRows.Fails(Source,
            "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(gen_random_uuid(),@a,@b,'relates_to',@p)", ("a", walk), ("b", map), ("p", who)));
        // An end that changes invalidates the map's own links (009) and leaves the made ones as they were.
        await MemoryRows.Run(Source, "UPDATE resources SET revision='2' WHERE id=@a", ("a", walk));
        Assert.Equal("stale", await MemoryRows.Scalar(Source, "SELECT lifecycle FROM relationships WHERE source_id=@a AND engine='cross-source-v1'", ("a", walk)));
        Assert.Equal("active a person linked them", await MemoryRows.Scalar(Source,
            "SELECT lifecycle||' '||evidence FROM made_links WHERE link_id=@l", ("l", link)));
        // An end is never deleted from under a made link, by a cascade or otherwise, even by the owner: the made link
        // goes first, through its function.
        Assert.Equal("23503", await MemoryRows.Fails(Source, "DELETE FROM resources WHERE id=@c", ("c", other)));
        Assert.Equal(2L, await MemoryRows.Scalar(Source, "SELECT count(*) FROM made_links WHERE source_id=@a", ("a", walk)));
    }

    [PostgresFact] public async Task A_restore_quarantine_and_a_revoke_leave_memory_text_intact()
    {
        var who = await MemoryRows.Principal(Source);
        var project = Guid.NewGuid();
        await MemoryRows.Run(Source, "INSERT INTO memory_projects(id,subject_id,slug,name,author_id) VALUES(@id,'alex','canal','DEMO canal walks','alex')", ("id", project));
        var walk = await MemoryRows.Memory(Source, "alex", "alex", "DEMO: the canal walk takes forty fictional minutes.",
            ",project_id,revision", ",@project,2", ("project", project));
        await MemoryRows.Run(Source, """
            INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,written_at)
            VALUES(@m,1,'alex','private','DEMO: the canal walk takes an hour.','draft',now()-interval '1 day')
            """, ("m", walk));
        var star = await MemoryRows.Star(Source, walk, "alex", "DEMO canal walk");
        var trashed = await MemoryRows.Memory(Source, "household", "sam", "DEMO: a forgotten Family memory.");
        await MemoryRows.Run(Source, """
            INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after) VALUES(@m,'forget',@p,'mcp',now()+interval '14 days');
            UPDATE resources SET status='deleted' WHERE id=@m
            """, ("m", trashed), ("p", who));
        // The quarantine a restore runs blanks every map snapshot row, this memory's star among them, and disables every
        // token; the memory's own row is the record, not a snapshot, and stays as it was.
        var quarantine = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "backup", "restore-quarantine.sql"));
        await MemoryRows.Run(Source, quarantine);
        Assert.Equal("revoked Unavailable", await MemoryRows.Scalar(Source, "SELECT status||' '||title||content FROM resources WHERE id=@m", ("m", star)));
        Assert.Equal("current DEMO memory", await MemoryRows.Scalar(Source, "SELECT status||' '||title||content FROM resources WHERE id=@m", ("m", walk)));
        // The memory's text, history, project and trash are untouched: they never lived in a snapshot row.
        Assert.Equal("DEMO: the canal walk takes forty fictional minutes.", await MemoryRows.Scalar(Source, "SELECT content FROM memories WHERE id=@m", ("m", walk)));
        Assert.Equal("DEMO: the canal walk takes an hour.", await MemoryRows.Scalar(Source, "SELECT content FROM memory_versions WHERE memory_id=@m", ("m", walk)));
        Assert.Equal("DEMO canal walks", await MemoryRows.Scalar(Source, "SELECT name FROM memory_projects WHERE id=@p", ("p", project)));
        Assert.Equal("DEMO: a forgotten Family memory.", await MemoryRows.Scalar(Source,
            "SELECT m.content FROM memories m JOIN memory_trash t ON t.memory_id=m.id WHERE m.id=@m", ("m", trashed)));
        // Revoking a record blanks it and what derives from it, never a memory's own text.
        await fixture.Db.Revoke("alex", "memory", walk.ToString(), default);
        Assert.Equal("DEMO: the canal walk takes forty fictional minutes.", await MemoryRows.Scalar(Source, "SELECT content FROM memories WHERE id=@m", ("m", walk)));
    }
}
