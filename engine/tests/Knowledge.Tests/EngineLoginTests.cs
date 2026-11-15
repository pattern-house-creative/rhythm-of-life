using Npgsql;

namespace Knowledge.Tests;

// The engine's own database login (SB-P1-14, docs/dev/engine.md "Database login"): the rights `migrate` gives it
// (Engine.GrantLogin), checked by connecting as a login made for the test. Today that is every table read and written,
// whatever the comments of migrations 006 and 010 to 016 say; the memory tables are the exception, changed only through
// their checked functions.
public sealed class EngineLoginTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    NpgsqlDataSource Source => fixture.Source;

    [PostgresFact] public async Task The_engine_login_writes_every_table_but_the_memory_tables()
    {
        await using var login = await TestLogin.Make(Source, fixture.Name);
        var checkedTables = await MemoryRows.CheckedTables(Source);
        await using (var cmd = Source.CreateCommand("""
            SELECT c.relname,has_table_privilege(@r,c.oid,'SELECT'),has_table_privilege(@r,c.oid,'INSERT'),
              has_table_privilege(@r,c.oid,'UPDATE'),has_table_privilege(@r,c.oid,'DELETE'),has_table_privilege(@r,c.oid,'TRUNCATE')
            FROM pg_class c WHERE c.relnamespace=current_schema()::regnamespace AND c.relkind IN ('r','p') ORDER BY 1
            """))
        {
            cmd.Parameters.AddWithValue("r", login.Name);
            await using var reader = await cmd.ExecuteReaderAsync();
            var tables = 0;
            while (await reader.ReadAsync())
            {
                tables++;
                var table = reader.GetString(0);
                Assert.True(reader.GetBoolean(1), $"{table}: the login reads every table");
                // Today's grant: insights, dashboards, screens and the rest are written by the login too (their migrations'
                // comments say EXECUTE only); schema_versions and the memory tables only through their owner.
                var writes = table != "schema_versions" && !checkedTables.Contains(table);
                Assert.True((reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4)) == (writes, writes, writes), $"{table}: writes {writes}");
                Assert.False(reader.GetBoolean(5), $"{table}: no truncate");
            }
            Assert.True(tables > 20);
        }
        // Each memory table, tried directly: read, never written.
        foreach (var table in checkedTables)
        {
            Assert.Equal(0L, await MemoryRows.Scalar(login.Source, $"SELECT count(*) FROM {table} WHERE false"));
            var column = (string)(await MemoryRows.Scalar(Source, """
                SELECT attname::text FROM pg_attribute WHERE attrelid=@t::regclass AND attnum>0 AND NOT attisdropped
                  AND attidentity='' AND attgenerated='' ORDER BY attnum LIMIT 1
                """, ("t", table)))!;
            foreach (var write in new[] { $"INSERT INTO {table} DEFAULT VALUES", $"UPDATE {table} SET {column}={column} WHERE false",
                $"DELETE FROM {table} WHERE false", $"TRUNCATE {table}" })
                Assert.Equal("42501", await MemoryRows.Fails(login.Source, write));
        }
        // No schema of its own, and no temporary tables (019).
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "CREATE TABLE sb_login_test(id int)"));
        Assert.Equal("42501", await MemoryRows.Fails(login.Source, "CREATE TEMPORARY TABLE sb_login_test(id int)"));
    }

    [PostgresFact] public async Task Memory_rows_change_only_through_their_checked_functions()
    {
        // A checked function standing in for phase 3's: SECURITY DEFINER with its own search path, the caller's role
        // first, and the memory, its resources row and its audit written together.
        await MemoryRows.Run(Source, """
            CREATE FUNCTION sb_test_remember(p_principal uuid,p_id uuid,p_content text) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
            BEGIN
             PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Remembering');
             INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
               VALUES(p_id,'household','memory',p_id::text,'memory','shared','DEMO memory',p_content,'current');
             INSERT INTO memories(id,subject_id,scope,created_by,content,source) VALUES(p_id,'household','shared',p_principal,p_content,'claude');
             INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel) VALUES(p_id,'household',1,'created',p_principal,'mcp');
            END $$
            """);
        await using var login = await TestLogin.Make(Source, fixture.Name);
        var principals = new Principals(Source);
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var harness = await principals.Create("memory-test-client", "harness", null, Path.Combine(folder, "harness"), default);
            var app = (await principals.SyncApp(Principals.NewToken(), default))!.Value;
            var bins = Guid.NewGuid();
            // Through the function, as the engine would call it: written, with its row and its audit.
            await MemoryRows.Run(login.Source, "SELECT sb_test_remember(@p,@id,'DEMO: the bins go out on Thursday.')", ("p", harness), ("id", bins));
            Assert.Equal("DEMO: the bins go out on Thursday.", await MemoryRows.Scalar(login.Source, "SELECT content FROM memories WHERE id=@id", ("id", bins)));
            Assert.Equal(1L, await MemoryRows.Scalar(login.Source, "SELECT count(*) FROM memory_events WHERE memory_id=@id", ("id", bins)));
            // The function's role check: the app may not use the harness's entry point.
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "SELECT sb_test_remember(@p,@id,'DEMO')", ("p", app), ("id", Guid.NewGuid())));
            // Directly, no part of it: the memory, a memory's resources row (a share is a change of its subject), or a made link.
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE memories SET content='DEMO: changed' WHERE id=@id", ("id", bins)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE resources SET subject_id='alex',scope='private' WHERE id=@id", ("id", bins)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE resources SET status='deleted' WHERE id=@id", ("id", bins)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "DELETE FROM resources WHERE id=@id", ("id", bins)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                VALUES(@id,'household','memory',@id::text,'memory','shared','DEMO','','current')
                """, ("id", Guid.NewGuid())));
            var document = Guid.NewGuid();
            await MemoryRows.Run(login.Source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status)
                VALUES(@id,'household','documents',@id::text,'document','shared','DEMO document','DEMO','current')
                """, ("id", document));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source,
                "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(gen_random_uuid(),@a,@b,'relates_to',@p)",
                ("a", bins), ("b", document), ("p", harness)));
            // A made link the owner's function wrote: the login neither changes nor removes it, nor deletes an end from
            // under it.
            var made = Guid.NewGuid();
            await MemoryRows.Run(Source, "INSERT INTO made_links(link_id,source_id,target_id,type,made_by) VALUES(@l,@a,@b,'relates_to',@p)",
                ("l", made), ("a", bins), ("b", document), ("p", harness));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE made_links SET evidence='DEMO' WHERE link_id=@l", ("l", made)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "DELETE FROM made_links WHERE link_id=@l", ("l", made)));
            Assert.Equal("23503", await MemoryRows.Fails(login.Source, "DELETE FROM resources WHERE id=@id", ("id", document)));
            // A memory's own row holds no map snapshot: the login may only say when it last indexed it.
            Assert.Equal("42501", await MemoryRows.Fails(login.Source,
                "UPDATE resources SET relationship_snapshot='{\"Title\":\"DEMO\"}',indexed_at=now() WHERE id=@id", ("id", bins)));
            await MemoryRows.Run(login.Source, "UPDATE resources SET indexed_at=now() WHERE id=@id", ("id", bins));
            // The map keeps a memory's star in a snapshot row of its own, which the login writes, refreshes, revokes and
            // removes as it does every star; never one with a memory behind it, nor one without a snapshot.
            var star = Guid.NewGuid();
            await MemoryRows.Run(login.Source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status,relationship_snapshot)
                VALUES(@id,'household','memory','relationship-engine:'||jsonb_build_array('memory',@m::text)::text,'memory','shared','DEMO','current','{"Title":"DEMO"}')
                """, ("id", star), ("m", bins.ToString()));
            await MemoryRows.Run(login.Source, "UPDATE resources SET title='DEMO, again',status='revoked',relationship_snapshot='{\"Title\":\"DEMO, again\"}' WHERE id=@id", ("id", star));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE resources SET relationship_snapshot=NULL WHERE id=@id", ("id", star)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE resources SET external_id=id::text WHERE id=@id", ("id", star)));
            Assert.Equal("42501", await MemoryRows.Fails(login.Source, """
                INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,status)
                VALUES(gen_random_uuid(),'household','memory','relationship-engine:["memory","x"]','memory','shared','DEMO','current')
                """));
            await MemoryRows.Run(login.Source, "DELETE FROM resources WHERE id=@id", ("id", star));
            // What the login does write itself stays as it was: its own links, documents.
            await MemoryRows.Run(login.Source, """
                INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine) VALUES(@a,@b,'similar','suggested','','cross-source-v1')
                """, ("a", bins), ("b", document));
            await MemoryRows.Run(login.Source, "UPDATE resources SET title='DEMO document, renamed' WHERE id=@id", ("id", document));
        }
        finally
        {
            Directory.Delete(folder, true);
            await MemoryRows.Run(Source, "DROP FUNCTION sb_test_remember(uuid,uuid,text)");
        }
    }

    // Every SECURITY DEFINER function that writes a memory table checks its caller's role first, as save_document and
    // the dashboard functions do. The scan is checked on functions made here: a check that raises counts, a role test
    // whose answer is thrown away does not, and a qualified, quoted or dynamic write is found as a plain one is. The
    // engine's own daily jobs (Jobs) have no caller to check: each takes no principal and deletes only what has passed
    // its retention, whoever calls it (MemoryTrashPurgeTests), or, the staleness pass (053), marks only what its
    // volatility's time has passed and takes off a mark that no longer holds, writing no memory (MemoryStalenessTests). The
    // link pruning pass (056) deletes only automatic links past their grace period, never one someone stated or one with
    // an end in the trash, writing no memory (MemoryLinkPruningTests). The insight candidate pass (064) writes only pairs of
    // one scope whose memories are both eligible and 30 or more days apart, checked again as it writes, never a memory
    // (InsightCandidateTests). The operator's imports (Operator: 043's, 058's, 060's
    // and 061's of Rhythm's own format, 059's of a Second Brain export) have no caller to check either: the operator on
    // the host is no principal, as in delete forever's operator path (038), and only the terminal calls them (rhythm import and rhythm
    // import-second-brain, engine/src/Cli/), which the scan below holds; each one decides what its named reader may write
    // (MemoryArchiveTests, SecondBrainImportTests). So too rhythm capture disconnect (067), which deletes nothing and moves
    // only the named connection's mirrors to the trash (CaptureEndpointTests), and rhythm person offboard's first two steps
    // (072), which end the named person's access and move only their private memories to the trash, deleting nothing
    // (OffboardingTests). Its close (offboard_close) is the daily turn's as well as the command's: it deletes only the
    // private projects of a person whose offboarding is under way, who is not current and was not brought back since the
    // operator last ran it, that no memory is in any more, in the trash or out, whoever calls it. A trigger (Triggers) has
    // no caller either: 072's offboarding_returned, on subjects, only records on the offboarding of a person made current
    // that they were brought back, which makes every step wait, and deletes and moves nothing (OffboardingTests).
    static readonly string[] Jobs = ["prune_memory_requests", "prune_recall_log", "purge_memory_trash", "memory_staleness_pass", "prune_made_links",
        "memory_insight_accrue", "offboard_close"];
    static readonly string[] Triggers = ["offboarding_returned"];
    static readonly string[] Operator = ["import_memory", "import_memory_commitment", "import_memory_decision", "import_memory_due", "import_memory_standing", "import_memory_link",
        "import_memory_project", "import_second_brain_link", "import_second_brain_memory", "import_second_brain_project", "disconnect_capture",
        "offboard_person", "offboard_trash"];
    static readonly string Unchecked = $"""
        SELECT p.proname::text FROM pg_proc p
        WHERE p.pronamespace=current_schema()::regnamespace AND p.prosecdef
          AND (p.prosrc ~* ('(insert\s+into|update|delete\s+from|truncate(\s+table)?|merge\s+into)\s+(only\s+)?("?public"?\.)?"?(' ||
              (SELECT string_agg(t::text,'|') FROM unnest(checked_tables()) t) || ')"?\M')
            OR p.prosrc ~* '\mexecute\M' AND p.prosrc ~* ('\m(' || (SELECT string_agg(t::text,'|') FROM unnest(checked_tables()) t) || ')\M'))
          AND p.prosrc !~* '(dashboard_reader\s*\(\s*p_principal|if\s+not\s+principal_has_role\s*\(\s*p_principal)'
          AND NOT (p.proname IN ({string.Join(",", Jobs.Concat(Operator).Concat(Triggers).Select(j => $"'{j}'"))}) AND NOT 'p_principal'=ANY(coalesce(p.proargnames,ARRAY[]::text[])))
        ORDER BY 1
        """;

    [PostgresFact] public async Task Every_definer_function_that_writes_a_memory_table_checks_its_caller()
    {
        async Task<List<string>> Found(NpgsqlConnection c, NpgsqlTransaction? tx)
        {
            await using var cmd = Database.Command(c, tx, Unchecked);
            await using var reader = await cmd.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            return names;
        }
        await using var c = await Source.OpenConnectionAsync();
        Assert.Empty(await Found(c, null));
        // The jobs are the definers they are named as, and none takes a principal.
        Assert.Equal(Jobs.Length, (long)(await MemoryRows.Scalar(Source, """
            SELECT count(*) FROM pg_proc p WHERE p.pronamespace=current_schema()::regnamespace AND p.prosecdef AND p.proname=ANY(@jobs)
              AND NOT 'p_principal'=ANY(coalesce(p.proargnames,'{}'::text[]))
            """, ("jobs", Jobs)))!);
        Assert.Equal(Operator.Length, (long)(await MemoryRows.Scalar(Source, """
            SELECT count(*) FROM pg_proc p WHERE p.pronamespace=current_schema()::regnamespace AND p.prosecdef AND p.proname=ANY(@names)
              AND NOT 'p_principal'=ANY(coalesce(p.proargnames,'{}'::text[]))
            """, ("names", Operator)))!);
        // The triggers are trigger functions, called by no one.
        Assert.Equal(Triggers.Length, (long)(await MemoryRows.Scalar(Source, """
            SELECT count(*) FROM pg_proc p WHERE p.pronamespace=current_schema()::regnamespace AND p.prosecdef AND p.proname=ANY(@names)
              AND p.prorettype='trigger'::regtype AND p.pronargs=0
            """, ("names", Triggers)))!);
        // Only the terminal calls the operator's functions: no file of the engine outside engine/src/Cli/ names one.
        var src = Path.GetFullPath(Path.Combine(Here(), "..", "..", "src"));
        var callers = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => Operator.Any(name => File.ReadAllText(f).Contains(name, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')).ToArray();
        Assert.NotEmpty(callers);
        Assert.All(callers, f => Assert.StartsWith("Cli/", f));
        await using var tx = await c.BeginTransactionAsync();
        await Database.Execute(c, tx, """
            CREATE FUNCTION sb_test_checked(p_principal uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Testing'); DELETE FROM memory_trash WHERE false; END $$;
            CREATE FUNCTION sb_test_checked_role(p_principal uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN IF NOT principal_has_role(p_principal,ARRAY['harness']) THEN RAISE EXCEPTION 'no'; END IF; DELETE FROM memories WHERE false; END $$;
            CREATE FUNCTION sb_test_unchecked(p_principal uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN UPDATE memories SET status='draft' WHERE false; END $$;
            CREATE FUNCTION sb_test_unchecked_qualified(p_principal uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN UPDATE public."memories" SET status='draft' WHERE false; END $$;
            CREATE FUNCTION sb_test_unchecked_performed(p_principal uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN PERFORM principal_has_role(p_principal,ARRAY['harness']); DELETE FROM made_links WHERE false; END $$;
            CREATE FUNCTION sb_test_unchecked_dynamic(p_principal uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $$
            BEGIN EXECUTE format('DELETE FROM %I WHERE false','memory_trash'); END $$
            """, default);
        Assert.Equal(["sb_test_unchecked", "sb_test_unchecked_dynamic", "sb_test_unchecked_performed", "sb_test_unchecked_qualified"], await Found(c, tx));
        await tx.RollbackAsync();
    }

    [PostgresFact] public async Task Every_memory_table_is_listed_as_checked_and_closed_to_everyone_else()
    {
        // A new table named like a memory table that a later migration forgets to list fails here.
        Assert.Equal(0L, await MemoryRows.Scalar(Source, """
            SELECT count(*) FROM (SELECT c.relname::text FROM pg_class c WHERE c.relnamespace=current_schema()::regnamespace
              AND c.relkind IN ('r','p') AND (c.relname='memories' OR c.relname LIKE 'memory\_%' OR c.relname LIKE 'capsule\_%'
                OR c.relname LIKE 'made\_%')
              EXCEPT SELECT t::text FROM unnest(checked_tables()) t) missing
            """));
        // None of them grants PUBLIC anything; the check finds one that does (a grant tried and rolled back).
        const string Public = """
            SELECT coalesce(bool_or(a.grantee=0),false) FROM unnest(checked_tables()) t JOIN pg_class c ON c.oid=t,
              aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a
            """;
        Assert.Equal(false, await MemoryRows.Scalar(Source, Public));
        await using var c = await Source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Database.Execute(c, tx, "GRANT SELECT ON made_links TO PUBLIC", default);
        await using (var cmd = Database.Command(c, tx, Public)) Assert.Equal(true, await cmd.ExecuteScalarAsync());
        await tx.RollbackAsync();
    }

    static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
