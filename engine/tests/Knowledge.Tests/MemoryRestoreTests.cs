using System.Diagnostics;
using Knowledge.Adapters;
using Knowledge.Adapters.Demo;
using Knowledge.Cli;
using Npgsql;

namespace Knowledge.Tests;

// Runs where engine/test-db.sh (or test-db.ps1) started the test database: it names the container, whose pg_dump and
// pg_restore make the backup and the restore, as engine/backup does.
public sealed class RestoreFactAttribute : FactAttribute
{
    public RestoreFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB_CONTAINER")))
            Skip = "NOT RUN: needs the test database's container for pg_dump and pg_restore; run sh engine/test-db.sh.";
    }
}

// Story 15 (SB-P1-13; docs/dev/memory.md sections 6.2 and 9): a restored backup gives back every table as it was but
// for what the quarantine changes on purpose. So it keeps every native memory with its id, text, history, author, scope
// and passages, its projects, aliases, trash, audit, capsule revisions, made links, idempotency keys, import origins and a capture connection's mirrors; Family's memberships and admins,
// the people principals are bound to, source shares and a former person; the subject and scope of every derived record
// (insights and their runs, dashboards, their versions and writes, pins and corner cards); and the install's recorded
// settings, the memory.scopes mode included. Every token stays disabled, viewer credentials and person-bound tokens
// with the rest, and only the shared app token comes back from its file; the engine's login again changes the memory
// tables only through their functions. With scopes on, the restore never turns them off and adds nobody to Family,
// and each reader's new token reads what that reader read before. The backup and restore are engine/backup's:
// pg_dump --format=custom --no-owner --no-acl, pg_restore --no-owner --no-acl into a new database (a throwaway one on
// the test server), restore-quarantine.sql, then migrate and an engine start. All of it is DEMO data.
public sealed class MemoryRestoreTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    static async Task Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, $"docker exec {args[2]} failed: {await error}");
    }

    static string Quarantine => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "backup", "restore-quarantine.sql"));

    // The backup of database `name` and its restore into the new database `restored` on the same server, quarantined, as
    // engine/backup's backup and load make them. The caller migrates it and drops it.
    static async Task<NpgsqlDataSource> BackUpAndRestore(string name, string restored)
    {
        var container = Environment.GetEnvironmentVariable("RHYTHM_TEST_DB_CONTAINER")!;
        var dump = $"/tmp/{name}.dump";
        await Docker("exec", container, "pg_dump", "-U", "postgres", "--format=custom", "--no-owner", "--no-acl", "--file", dump, name);
        await Docker("exec", container, "createdb", "-U", "postgres", restored);
        await Docker("exec", container, "pg_restore", "-U", "postgres", "--no-owner", "--no-acl", "--exit-on-error", "--dbname", restored, dump);
        await Docker("exec", container, "rm", "-f", dump);
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RHYTHM_TEST_DB")) { Database = restored }.ConnectionString);
        await MemoryRows.Run(source, Quarantine);
        return source;
    }

    // A map snapshot row, which the quarantine blanks: every row with a snapshot but a native memory's own row (025).
    static string Blanked(string r) => $"({r}.relationship_snapshot IS NOT NULL AND NOT ({r}.provider='memory' AND {r}.external_id={r}.id::text))";

    // What the quarantine changes on purpose (restore-quarantine.sql), and so what of those tables' rows a restore keeps;
    // a change to the quarantine changes this list in the same commit:
    // - every token is disabled, and who each one is stays (its name, role, plugin, hash and the person it is bound to);
    //   the engine's own principals keep theirs enabled;
    // - old grants are removed (checked on their own);
    // - a snapshot row is blanked until the next rebuild (status, title, text and metadata, with the stored text
    //   vector 047 keeps of its title and text, and of its snapshot the four display fields 009's trigger writes:
    //   Title, Url, References and Status) and keeps its id, subject, scope, provider, catalog id, source id and the
    //   rest of its snapshot, the Scope and RetrievedAt that decide who reads it included;
    // - its passages go with its status (005's trigger) until the index job makes them again; a memory's stay;
    // - a link the engine found to it keeps its ends and type, its evidence gone until the rebuild checks it (009's trigger);
    // - an unfinished sync job waits for review;
    // - a viewer session's one-time code not used yet is removed (028); a used one stays with the principal it made;
    // - the admin audit (037) gains a row for each token disabled, as the system's job restore, and keeps every row it had;
    // - a memory in the trash gets the trash's retention again from the restore (14 days, or what it had left if longer),
    //   so the first daily purge never deletes what the restore brought back (038); one trashed more than 351 days before
    //   the restore is dated 351 days before it, so 025's bound of 365 days leaves it the 14 (its audit row keeps when).
    // Every other table comes back exactly as it was, row for row and column for column.
    static readonly Dictionary<string, string> Quarantined = new(StringComparer.Ordinal)
    {
        ["principals"] = "SELECT CASE WHEN t.role<>'system' THEN to_jsonb(t)-'enabled' ELSE to_jsonb(t) END FROM principals t",
        ["grants"] = "SELECT NULL::jsonb WHERE false",
        ["resources"] = $"""
            SELECT CASE WHEN {Blanked("t")} THEN to_jsonb(t)-ARRAY['status','title','content','search_english','metadata','relationship_snapshot']
              || jsonb_build_object('relationship_snapshot',t.relationship_snapshot-ARRAY['Title','Url','References','Status'])
              ELSE to_jsonb(t) END FROM resources t
            """,
        ["resource_chunks"] = $"SELECT to_jsonb(t) FROM resource_chunks t WHERE NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=t.resource_id AND {Blanked("r")})",
        ["relationships"] = $"""
            SELECT CASE WHEN t.engine='cross-source-v1' AND EXISTS(SELECT 1 FROM resources r WHERE r.id IN (t.source_id,t.target_id) AND {Blanked("r")})
              THEN to_jsonb(t)-ARRAY['lifecycle','evidence','details'] ELSE to_jsonb(t) END FROM relationships t
            """,
        ["sync_jobs"] = "SELECT CASE WHEN t.status<>'complete' THEN to_jsonb(t)-ARRAY['status','last_error'] ELSE to_jsonb(t) END FROM sync_jobs t",
        ["viewer_codes"] = "SELECT to_jsonb(t) FROM viewer_codes t WHERE t.used_at IS NOT NULL",
        ["admin_events"] = "SELECT to_jsonb(t) FROM admin_events t WHERE t.job IS DISTINCT FROM 'restore'",
        ["memory_trash"] = "SELECT to_jsonb(t)-ARRAY['purge_after','trashed_at'] FROM memory_trash t",
    };

    // The tables the fixture leaves empty on purpose: the old grants, compared on their own; and job state that names no
    // one's record (the index job's progress and its rows with no passages, the map's refresh requests). Every other
    // table must hold rows before the backup, so the comparison proves something for each, and a table a later migration
    // adds fails the test until the fixture seeds it.
    static readonly string[] LeftEmpty = ["embedding_backfill", "grants", "no_passages", "relationship_refresh_requests"];

    // Every table of the schema as one JSON text, rows in a fixed order; for the quarantined tables, what a restore keeps.
    static async Task<SortedDictionary<string, string>> Tables(NpgsqlDataSource source)
    {
        var names = new List<string>();
        await using (var cmd = source.CreateCommand("SELECT c.relname::text FROM pg_class c WHERE c.relnamespace=current_schema()::regnamespace AND c.relkind IN ('r','p') ORDER BY 1"))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        var tables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
            tables[name] = (string)(await MemoryRows.Scalar(source,
                $"SELECT coalesce(jsonb_agg(x ORDER BY x::text),'[]')::text FROM ({Quarantined.GetValueOrDefault(name, $"SELECT to_jsonb(t) FROM {name} t")}) s(x)"))!;
        return tables;
    }

    static void Same(SortedDictionary<string, string> before, SortedDictionary<string, string> after)
    {
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (name, rows) in before) Assert.True(rows == after[name], $"{name} changed in the restore");
    }

    // Settings with memory.scopes on, which this version accepts since phase 1's exit (MemorySettings.ScopesAccepted).
    static RhythmConfig ScopesOn() => new()
    {
        Timezone = "Europe/London",
        People = [new RhythmConfig.Person { Id = "alex", Name = "Alex" }, new RhythmConfig.Person { Id = "sam", Name = "Sam" }],
        Memory = new Dictionary<object, object?> { ["scopes"] = "on" },
    };

    // A fictional passage for a row (the index job's shape, a vector of the live model's size).
    static Task Passage(NpgsqlDataSource source, Guid row, string revision) => MemoryRows.Run(source, """
        INSERT INTO resource_chunks(resource_id,ordinal,source_revision,content_hash,model,text_content,embedding)
        VALUES(@id,0,@rev,'demo-hash','all-minilm-l6-v2','DEMO passage',array_fill(0.05::real,ARRAY[384])::vector)
        """, ("id", row), ("rev", revision));

    // A fictional dashboard of `subject` with one version citing `star`, made by `by`.
    static async Task<Guid> Dashboard(NpgsqlDataSource source, string subject, Guid by, string star)
    {
        var id = Guid.NewGuid();
        await MemoryRows.Run(source, """
            INSERT INTO dashboards(id,created_by,subject_id,scope) VALUES(@id,@by,@s,CASE WHEN @s='household' THEN 'shared' ELSE 'private' END);
            INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,note,saved_by)
            VALUES(@id,1,'DEMO dashboard','DEMO · restore fixture','<p>DEMO</p>',
              jsonb_build_array(jsonb_build_object('name','card0','kind','contents','star',@star::text)),
              jsonb_build_array(jsonb_build_object('graphId',@star::text,'title','DEMO')),'First version.',@by)
            """, ("id", id), ("by", by), ("s", subject), ("star", star));
        return id;
    }

    [RestoreFact] public async Task A_restore_keeps_memories_history_and_scopes_and_every_token_stays_disabled()
    {
        var source = fixture.Source;
        var config = TestSettings.Use();
        var principals = new Principals(source);
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var restoredName = fixture.Name + "_restored";
        NpgsqlDataSource? restored = null;
        try
        {
            // An install with tokens of every kind and native rows in every scope (fictional).
            var harness = await principals.Create("restore-client", "harness", null, Path.Combine(folder, "harness"), default);
            var harnessToken = File.ReadAllText(Path.Combine(folder, "harness"));
            // Clients bound to alex and to sam (as rhythm token create --person makes them), Alex's viewer session (an app
            // principal bound to her, D-070), a legacy principal from an install that predates roles with its grant, both
            // people in Family with Sam its admin, a source Alex shares, and jo, who has left.
            await principals.Create("restore-alex", "harness", null, "alex", Path.Combine(folder, "alex"), default);
            var alexToken = File.ReadAllText(Path.Combine(folder, "alex"));
            var alexClient = (await new Database(source).Authenticate(alexToken, default))!.Id;
            await Scoped.Principal(source, "restore-sam", "harness", "sam");
            var viewer = await Scoped.Principal(source, "restore-viewer-alex", "app", "alex");
            var legacy = await Scoped.Principal(source, "restore-legacy", "legacy");
            await Scoped.Members(source, "alex", "sam");
            await MemoryRows.Run(source, """
                UPDATE memberships SET role='admin' WHERE person_id='sam';
                INSERT INTO source_shares(person_id,provider,shared_by) VALUES('alex','cgm',@by);
                INSERT INTO subjects(id,kind,status) VALUES('jo','person','former');
                INSERT INTO grants VALUES(@legacy,'alex','cgm','private')
                """, ("by", alexClient), ("legacy", legacy));
            await principals.Create("plugin:cgm", "ingest", "cgm", Path.Combine(folder, "ingest"), default);
            var ingestToken = File.ReadAllText(Path.Combine(folder, "ingest"));
            // A capture connection and the one outside item it mirrors (067).
            var feed = await principals.CreateCapture("restore-feed", null, Path.Combine(folder, "capture"), default);
            var captureToken = File.ReadAllText(Path.Combine(folder, "capture"));
            var mirrored = (await new MemoryStore(source).Capture(feed, new("DEMO: a fictional tracker item.", Mirror: "restore:item/1"), "restore-feed", null, default)).Id;
            var appToken = Principals.NewToken();
            await principals.SyncApp(appToken, default);
            await principals.System(Principals.GraphName, default);
            var project = Guid.NewGuid();
            await MemoryRows.Run(source, """
                INSERT INTO memory_projects(id,subject_id,slug,name,author_id,created_by) VALUES(@id,'alex','canal','DEMO canal walks','alex',@p);
                INSERT INTO memory_project_aliases(project_id,subject_id,alias) VALUES(@id,'alex','towpath')
                """, ("id", project), ("p", harness));
            var walk = await MemoryRows.Memory(source, "alex", "alex", "DEMO: the canal walk takes forty fictional minutes.",
                ",project_id,capsule,capsule_slot,status,valid_from,revision,created_by", ",@project,'project','current-state','canonical','-infinity',2,@p",
                ("project", project), ("p", harness));
            var party = await MemoryRows.Memory(source, "sam", "sam", "DEMO: Sam plans a fictional surprise party for Alex.");
            var cafe = await MemoryRows.Memory(source, "household", "alex", "DEMO: Alex shared the fictional cafe with the family.",
                ",valid_until", ",'2030-01-01T00:00:00Z'::timestamptz");
            var bins = await MemoryRows.Memory(source, "household", null, "DEMO: the bins go out on Thursday.");
            var gone = await MemoryRows.Memory(source, "alex", "alex", "DEMO: a forgotten fictional note.");
            var longAgo = await MemoryRows.Memory(source, "alex", "alex", "DEMO: a fictional note forgotten long ago.");
            await MemoryRows.Run(source, """
                INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,status,state,written_at)
                VALUES(@walk,1,'alex','private','DEMO: the canal walk takes an hour.','draft','{"kind":"episodic"}',now()-interval '1 day');
                INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client) VALUES
                  (@walk,'alex',1,'created',@p,'mcp','DEMO client'),(@walk,'alex',2,'updated',@p,'mcp','DEMO client'),
                  (@cafe,'household',1,'shared',@p,'mcp','DEMO client');
                INSERT INTO memory_events(project_id,subject_id,event,channel) VALUES(@project,'alex','created','cli');
                INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,trashed_at,purge_after,links)
                  VALUES(@gone,'forget',@p,'mcp',now()-interval '20 days',now()-interval '6 days','[{"type":"relates_to"}]');
                UPDATE resources SET status='deleted' WHERE id=@gone;
                -- One trashed 400 days before the backup: past 025's 365 days from then before the restore starts.
                INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,trashed_at,purge_after)
                  VALUES(@long,'forget',@p,'mcp',now()-interval '400 days',now()-interval '386 days');
                INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail,at)
                  VALUES(@long,'alex',1,'trashed',@p,'mcp','DEMO client','{"reason":"forget"}',now()-interval '400 days');
                UPDATE resources SET status='deleted' WHERE id=@long;
                INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by)
                  VALUES(gen_random_uuid(),@walk,@cafe,'relates_to','verified','DEMO',@p);
                -- The walk flagged for a check: built on the note forgotten since (049's retraction cascade).
                INSERT INTO memory_retraction_flags(memory_id,retracted_id) VALUES(@walk,@gone);
                -- The walk flagged on save as a possible copy of the note forgotten since (063's duplicate check).
                INSERT INTO memory_duplicate_flags(memory_id,copy_id,score,model,threshold) VALUES(@walk,@gone,1,'exact',1);
                INSERT INTO memory_contradictions(memory_id,conflict_id,score,model,threshold) VALUES(@walk,@gone,1,'exact',1);
                -- A suggestion between the walk and the cafe that Alex dismissed (052's unlink).
                INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by)
                  VALUES(least(@walk,@cafe),greatest(@walk,@cafe),'similar_to',@p);
                -- The walk retired from Alex's Home for good (070's resurfaced memory): a restore must not bring it back.
                INSERT INTO memory_resurface_dismissals(memory_id,reader_id,dismissed_by) VALUES(@walk,'alex',@p);
                -- The walk citing Alex's fictional Drive file at revision 7 (055's citations).
                INSERT INTO memory_citations(citation_id,memory_id,provider,account,source_id,subject_id,scope,revision,modified_at,title,parents,cited_by,cited_channel)
                  VALUES(gen_random_uuid(),@walk,'drive','drive','fx-restore-drive','alex','private','7',now()-interval '2 days','DEMO restored file',
                    ARRAY['fx-restore-folder'],@p,'mcp');
                -- The walk confirmed as still true once, and found out of date since (053's staleness pass).
                INSERT INTO memory_checks(memory_id,confirmed_at,confirmed_revision,stale_at,stale_revision,stale_since)
                  VALUES(@walk,now()-interval '40 days',1,now()-interval '1 day',2,now()-interval '40 days');
                -- The cafe saved as owed to Alex by Sam (058's commitments).
                INSERT INTO memory_commitments(memory_id,direction,counterparty) VALUES(@cafe,'in','sam');
                -- The cafe and the bins paired by the insight candidate pass, and Alex's rejection of the pair (064) and
                -- why (066).
                INSERT INTO memory_insight_candidates(id,subject_id,older_id,newer_id,similarity,model,gap_days,signals,score,status,decided_at,decided_by,note)
                  VALUES('00000000-0000-4000-8000-0000000000c3','household',@cafe,@bins,0.21,'recall-meaning-model',45,'{weighty}',2.4,
                    'rejected',now(),@p,'DEMO nothing new together');
                INSERT INTO memory_insight_events(candidate_id,subject_id,event,principal_id,channel,client)
                  VALUES('00000000-0000-4000-8000-0000000000c3','household','rejected',@p,'mcp','DEMO client');
                -- The walk a decision Alex was 70% sure of, found right since (060's ledger).
                INSERT INTO memory_decisions(memory_id,confidence,confidence_source) VALUES(@walk,0.70,'stated');
                INSERT INTO memory_decision_states(memory_id,revision,outcome,rearms,review_at) VALUES
                  (@walk,1,NULL,0,now()+interval '90 days'),(@walk,2,'right',0,NULL);
                INSERT INTO memory_requests(principal_id,idempotency_key,operation,payload_hash,memory_id,result)
                  VALUES(@p,'demo-restore-key-0001','append',repeat('0',64),@walk,jsonb_build_object('id',@walk::text,'revision',2));
                -- A recall Alex asked with the opt-in log on (042), the walk it found and she then read, and her count of it.
                INSERT INTO recall_log(subject_id,channel,query,params,returned,followed,receipt)
                  VALUES('alex','mcp','DEMO canal walk','{"topK":5}',ARRAY[@walk]::uuid[],ARRAY[@walk]::uuid[],'rc1-00000000000000c1');
                INSERT INTO recall_counts(subject_id,resource_id,recalled,followed) VALUES('alex',@walk,1,1);
                -- The walk as an import brought it (043): its origin map row.
                INSERT INTO memory_origins(origin,kind,source_id,local_id,subject_id,export_id,exported_at)
                  VALUES('rhythm:00000000-0000-4000-8000-0000000000c1','memory',@walk::text,@walk,'alex','00000000-0000-4000-8000-0000000000c2',now());
                -- The tags a Second Brain export gave the cafe in a namespace the engine sets itself, kept beside it (059).
                INSERT INTO memory_upstream_tags(memory_id,origin,tags) VALUES(@cafe,'second-brain:default',ARRAY['standing:active','confidence:0.70']);
                -- Jo's offboarding, under way (072): a restore keeps it, so the next run goes on with it.
                INSERT INTO offboardings(person_id,trashed) VALUES('jo',1)
                """, ("walk", walk), ("cafe", cafe), ("bins", bins), ("gone", gone), ("long", longAgo), ("project", project), ("p", harness));
            // The map's star for the walk, in its own snapshot row, and the walk's passage from its own text.
            var star = await MemoryRows.Star(source, walk, "alex", "DEMO canal walk");
            await Passage(source, walk, "memory:2");
            // Alex's private Drive file as the map saw it (a snapshot row with a passage from its text), a household
            // save_document copy of it, the link the engine found between them, Alex's glucose rows, and a sync job still
            // running.
            var drive = await Scoped.Row(source, "alex", "drive", "private", "relationship-engine:fx-restore-drive", native: "fx-restore-drive");
            await Passage(source, drive, "r1");
            var copy = await Scoped.Row(source, "household", "documents", "shared", "fx-restore-drive", mode: "dated-snapshot");
            await Scoped.Row(source, "alex", "cgm", "private", "restore-cgm-1");
            await MemoryRows.Run(source, """
                INSERT INTO relationships(source_id,target_id,type,verification,evidence,engine) VALUES(@copy,@drive,'references','verified','DEMO','cross-source-v1');
                INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date,status) VALUES(gen_random_uuid(),'alex','cgm','2026-10-01','2026-10-31','2026-10-15','running');
                -- A suggestion between the walk's star and the Drive file's that Alex dismissed in Discover (057).
                INSERT INTO map_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(least(@star,@drive),greatest(@star,@drive),'similar_to',@viewer)
                """, ("copy", copy), ("drive", drive), ("star", star), ("viewer", viewer));
            // Derived records in Alex's private scope and in Family's (027): an insight with what its sources named, its
            // run, a dashboard of each with a log write in Alex's name, a pin of each and Family's corner card.
            var alexDashboard = await Dashboard(source, "alex", alexClient, "rhythm:alex:steps");
            var familyDashboard = await Dashboard(source, "household", harness, "rhythm:household:notes");
            await MemoryRows.Run(source, """
                INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,
                  generated_by,generated_at,sources_as_of,saved_by,subject_id,scope,sources_checked)
                VALUES(gen_random_uuid(),'discovery','DEMO canal walks','DEMO · a fictional insight.','','','[]','weak','','[]',
                  '[{"kind":"record","locator":"fx-restore-drive"}]','manual',now(),now(),@alex,'alex','private',ARRAY['fx-restore-drive']),
                  (gen_random_uuid(),'focus','DEMO bins','DEMO · a fictional insight.','','','[]','weak','','[]','[]','manual',now(),now(),@p,'household','shared',NULL);
                INSERT INTO insight_runs(id,generated_by,outcome,note,subject_id,scope) VALUES(gen_random_uuid(),'manual','quiet','DEMO run','alex','private');
                -- Alex's word that the canal walks insight holds, and the feedback it had before it (066).
                UPDATE insights SET review='confirmed',reviewed_at=now(),reviewed_by=@alex WHERE title='DEMO canal walks';
                INSERT INTO insight_events(insight_id,subject_id,event,note,principal_id,channel,client)
                  SELECT id,subject_id,'confirmed','DEMO it holds',@alex,'mcp','DEMO client' FROM insights WHERE title='DEMO canal walks';
                INSERT INTO insight_events(insight_id,subject_id,event,value,at)
                  SELECT id,subject_id,'feedback','interesting',now()-interval '1 day' FROM insights WHERE title='DEMO canal walks';
                INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,after,written_by,subject_id)
                  VALUES(gen_random_uuid(),@family,1,'log','log','{"subject":"alex"}','DEMO entry','{"text":"DEMO"}',@alex,'alex');
                INSERT INTO sky_pins(subject_id,graph_id,pinned_by) VALUES('alex','rhythm:alex:steps',@alex),('household','rhythm:household:notes',@p);
                INSERT INTO ambient_cards(corner,dashboard_id,set_by,subject_id) VALUES('recovery',@family,@p,'household')
                """, ("alex", alexClient), ("p", harness), ("family", familyDashboard));
            // The rest of what a restore must keep, so no table is compared empty: Alex's glucose with its payload, raw
            // file, archive lineage, plugin call and freshness; the sync job's events; a layout of the Family dashboard
            // whose map is focused on a star; Alex's phone, listened to by her viewer session, showing her private star,
            // and a command it reported; and two of her one-time codes, one used (by that session) and one not yet.
            var cgm = (Guid)(await MemoryRows.Scalar(source, "SELECT id FROM resources WHERE external_id='restore-cgm-1'"))!;
            var archive = await Scoped.Row(source, "alex", "cgm", "private", "restore-cgm-archive");
            await MemoryRows.Run(source, """
                INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
                  VALUES(@cgm,'glucose','demo-1','2026-10-15T08:00:00Z','2026-10-15',5.4,'mmol/L','ok');
                INSERT INTO source_payloads(resource_id,checksum,format,adapter_version,payload,fields)
                  VALUES(@cgm,'demo-checksum','csv',1,'{"rows":1}','["glucose"]');
                INSERT INTO resource_versions(resource_id,checksum,raw_path) VALUES(@cgm,'demo-checksum','archive/demo/restore-cgm-1.csv');
                INSERT INTO resource_dependencies(child_id,parent_id) VALUES(@cgm,@archive);
                INSERT INTO ingest_calls(plugin,idempotency_key,tool,payload_hash,provider,subject_id,result)
                  VALUES('cgm','demo-restore-0001','ingest_measurements','demo-hash','cgm','alex','{"accepted":1}');
                INSERT INTO ingest_freshness(plugin,provider,subject_id,last_accepted_at,last_observed_at)
                  VALUES('cgm','cgm','alex',now(),'2026-10-15T08:00:00Z');
                INSERT INTO sync_events(job_id,event) SELECT id,'started' FROM sync_jobs;
                INSERT INTO dashboard_layouts(dashboard_id,version,revision,layout,note,saved_by) VALUES(@family,1,1,
                  '{"parts":[{"id":"map","kind":"map","x":0,"y":0,"w":8,"h":12,"focus":{"near":{"star":"rhythm:household:notes"}}}]}','DEMO layout',@p);
                INSERT INTO screens(id,name,showing,settings,shown_by,bound_to) VALUES('restore-alex-phone-0001','DEMO phone',
                  '{"view":"star","star":{"id":"rhythm:alex:steps","title":"DEMO steps"}}',jsonb_build_object('home',jsonb_build_object('dashboard',@mine::text)),@viewer,@viewer);
                INSERT INTO screen_commands(screen_id,action,args,sender,expires_at,result,detail,reported_by)
                  VALUES('restore-alex-phone-0001','open_star','{"star":"rhythm:alex:steps"}','{"kind":"assistant"}',now()+interval '1 minute',
                    'shown','{"summary":"DEMO opened"}',@viewer);
                INSERT INTO studio_sessions(id,owner,screen,dashboard,base_version,revision,valid_revision,status,candidate,preview,events,expires_at)
                  VALUES('5d0e1f6a-0000-4000-8000-00000000d0e1',@p,'restore-alex-phone-0001',@family,1,1,1,'discarded',
                    '{"title":"DEMO draft","html":"<p>DEMO draft</p>"}','{"title":"DEMO draft","html":"<p>DEMO draft</p>"}',
                    '[{"kind":"progress","message":"DEMO"}]','2026-10-27T08:00:00Z');
                INSERT INTO studio_requests(session,request,fingerprint,reply)
                  VALUES('5d0e1f6a-0000-4000-8000-00000000d0e1','5d0e1f6a-0000-4000-8000-00000000d0e2','demo-fingerprint','{"revision":1}');
                INSERT INTO dashboard_controls(dashboard,version,manifest)
                  VALUES(@family,1,'[{"id":"week","kind":"week","label":"DEMO week"}]');
                INSERT INTO viewer_codes(code_hash,person_id,created_at,expires_at,used_at,principal_id) VALUES
                  ('demo-used-code-hash','alex',now(),now()+interval '30 minutes',now(),@viewer),
                  ('demo-unused-code-hash','alex',now(),now()+interval '30 minutes',NULL,NULL);
                INSERT INTO job_cursors(job,scope,cursor,finished_night) VALUES('demo-walk','','{"after":3}','2026-10-20'),('demo-walk','alex','{"seen":2}',NULL);
                INSERT INTO night_runs(night,job,position,name,outcome,failure,counts,runs,budget_ms,elapsed_ms,started_at,finished_at) VALUES
                  ('2026-10-20','demo-walk',0,'demo walk','failed','IOException','{"items done":3}',2,1000,12,'2026-10-20T02:00:00Z','2026-10-20T02:00:01Z')
                """, ("cgm", cgm), ("archive", archive), ("family", familyDashboard), ("mine", alexDashboard), ("p", harness), ("viewer", viewer));
            // The two snapshot rows as a pass keeps them: what decides who reads a record (its Scope, when it was seen) and
            // what the map shows of it, besides the source id they already name.
            await MemoryRows.Run(source, """
                UPDATE resources SET relationship_snapshot=relationship_snapshot||jsonb_build_object('Scope',scope,'Kind',kind,
                  'RetrievedAt','2026-10-20T08:00:00+00:00','Title',title,'Url','https://example.invalid/demo','References','[]'::jsonb,'Status','current')
                WHERE id=ANY(@rows)
                """, ("rows", new[] { star, drive }));
            // OAuth protocol state is backed up too; its subject still depends on the quarantined principal.
            await MemoryRows.Run(source, """
                INSERT INTO oauth_applications("Id","ClientId","ClientType") VALUES('demo-client','demo-client','public');
                INSERT INTO oauth_scopes("Id","Name") VALUES('demo-scope','rhythm');
                INSERT INTO oauth_authorizations("Id","ApplicationId","Subject","Status","Type")
                  VALUES('demo-grant','demo-client',@p::text,'valid','permanent');
                INSERT INTO oauth_tokens("Id","ApplicationId","AuthorizationId","Subject","Status","Type","Payload")
                  VALUES('demo-token','demo-client','demo-grant',@p::text,'valid','access_token','DEMO encrypted state');
                """, ("p", harness));
            var before = await Tables(source);
            var goneTrashed = await MemoryRows.Scalar(source, "SELECT trashed_at::text FROM memory_trash WHERE memory_id=@id", ("id", gone));
            Assert.Equal(LeftEmpty, before.Where(t => t.Value == "[]").Select(t => t.Key).ToArray());
            Assert.Contains("\"memory.scopes\"", before["install_settings"]);
            Assert.Contains("\"jo\"", before["subjects"]);
            Assert.Contains("\"admin\"", before["memberships"]);
            Assert.Contains("\"capsule_slot\": \"current-state\"", before["memories"]);
            Assert.Contains(bins.ToString(), before["memories"]);
            Assert.Contains(alexDashboard.ToString(), before["dashboards"]);
            Assert.Contains("\"sources_checked\": [\"fx-restore-drive\"]", before["insights"]);
            Assert.Equal(1L, await MemoryRows.Scalar(source, "SELECT count(*) FROM grants"));
            // The tokens anyone was given that are enabled now, which the quarantine disables and the admin audit records.
            var given = (long)(await MemoryRows.Scalar(source,
                "SELECT count(*) FROM principals WHERE enabled AND (role IN ('harness','ingest','legacy','capture') OR role='app' AND person_id IS NOT NULL)"))!;
            Assert.True(given >= 5);

            // The backup and the restore, into a new database.
            restored = await BackUpAndRestore(fixture.Name, restoredName);
            var db = new Database(restored);
            Assert.Empty(await db.Migrate());
            await using (var login = await TestLogin.Make(restored, restoredName))
            {
                await new Principals(restored).Install(config, default);

                // Every table as it was, but what the quarantine changes on purpose: every memory, its row, history, scope,
                // author and passage, the projects, trash, audit, capsule revisions and made links, the recorded mode,
                // Family's members and admin, the bindings, the shares and the former person, and every derived record with
                // its subject and scope.
                Same(before, await Tables(restored));
                // What the quarantine changed: the old grant is gone; a snapshot row is blanked until the next rebuild, the
                // map's star for a memory as every other, with its passage and the evidence of the link found to it; the
                // memory's own row, text and passage are not in it; the unfinished sync job waits for review.
                Assert.Equal(0L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM grants"));
                foreach (var blanked in new[] { star, drive })
                    Assert.Equal("revoked Unavailable ", await MemoryRows.Scalar(restored, "SELECT status||' '||title||' '||content FROM resources WHERE id=@id", ("id", blanked)));
                // A blanked row's stored text vector (047) follows its blanked words: none of the old ones is searchable.
                Assert.Equal(0L, await MemoryRows.Scalar(restored,
                    "SELECT count(*) FROM resources WHERE search_english IS DISTINCT FROM to_tsvector('english'::regconfig,title || ' ' || content)"));
                Assert.Equal("current DEMO memory", await MemoryRows.Scalar(restored, "SELECT status||' '||title FROM resources WHERE id=@id", ("id", walk)));
                Assert.Equal(1L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM resource_chunks WHERE resource_id=@id", ("id", walk)));
                Assert.Equal(0L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM resource_chunks WHERE resource_id=@id", ("id", drive)));
                Assert.Equal("revoked", await MemoryRows.Scalar(restored, "SELECT lifecycle FROM relationships WHERE source_id=@c AND target_id=@d", ("c", copy), ("d", drive)));
                Assert.Equal("failed restore_requires_review", await MemoryRows.Scalar(restored, "SELECT status||' '||last_error FROM sync_jobs"));
                // The memory forgotten 20 days before the backup, its retention passed then, is in the trash for the trash's
                // 14 days from the restore, so the daily purge deletes nothing of what the restore brought back.
                // Compare with the recorded restore transaction, not a later wall-clock sample that can move
                // backwards while a VM's clock synchronizes during the suite.
                Assert.Equal(true, await MemoryRows.Scalar(restored, """
                    SELECT purge_after=(SELECT min(at) FROM admin_events WHERE job='restore')+interval '14 days'
                    FROM memory_trash WHERE memory_id=@id
                    """, ("id", gone)));
                // Trashed 20 days before the backup, it keeps its date.
                Assert.Equal(goneTrashed, await MemoryRows.Scalar(restored, "SELECT trashed_at::text FROM memory_trash WHERE memory_id=@id", ("id", gone)));
                // The one trashed 400 days before has its 14 days too: 025 keeps a retention within 365 days of the trash
                // row's date, so the row is dated 351 days before the restore; when it was trashed stays on its audit row.
                Assert.Equal(true, await MemoryRows.Scalar(restored, """
                    WITH restored AS (SELECT min(at) AS at FROM admin_events WHERE job='restore')
                    SELECT purge_after=restored.at+interval '14 days' AND trashed_at=restored.at-interval '351 days'
                    FROM memory_trash CROSS JOIN restored WHERE memory_id=@id
                    """, ("id", longAgo)));
                Assert.Equal(true, await MemoryRows.Scalar(restored,
                    "SELECT at<now()-interval '399 days' FROM memory_events WHERE memory_id=@id AND event='trashed'", ("id", longAgo)));
                Assert.Equal(0, (await MemoryTrash.Purge(restored, default)).Purged);
                Assert.Equal(2L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM memories WHERE id=ANY(@id)", ("id", new[] { gone, longAgo })));
                // A one-time code not used yet is gone, so a restore never brings back one revoked since; the used one stays.
                Assert.Equal("demo-used-code-hash", await MemoryRows.Scalar(restored, "SELECT string_agg(code_hash,',') FROM viewer_codes"));
                // Every token stays disabled: harness, person-bound, plugin, capture, viewer, legacy and app alike; the engine's
                // own jobs keep theirs.
                foreach (var token in new[] { harnessToken, alexToken, ingestToken, captureToken, appToken }) Assert.Null(await db.Authenticate(token, default));
                Assert.Equal(0L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM principals WHERE enabled AND role<>'system'"));
                // The admin audit names each of them as disabled by the restore, the system's job (037); the shared app
                // token's principal, which no one is given, is not recorded.
                Assert.Equal(given, await MemoryRows.Scalar(restored,
                    "SELECT count(*) FROM admin_events WHERE event='token_disabled' AND channel='system' AND job='restore' AND actor_id IS NULL"));
                Assert.Equal(given, await MemoryRows.Scalar(restored, "SELECT count(*) FROM admin_events WHERE job='restore'"));
                Assert.Equal(true, await MemoryRows.Scalar(restored, "SELECT enabled FROM principals WHERE role='system' AND name=@n", ("n", Principals.GraphName)));
                // The app's token comes back from its file at the engine's start, as before; nothing else does: not the
                // viewer credential, an app principal too, nor the tokens bound to people.
                await new Principals(restored).SyncApp(appToken, default);
                Assert.Equal("app", (await db.Authenticate(appToken, default))!.Role);
                Assert.Null(await db.Authenticate(harnessToken, default));
                Assert.Null(await db.Authenticate(alexToken, default));
                Assert.Equal(false, await MemoryRows.Scalar(restored, "SELECT enabled FROM principals WHERE id=@id", ("id", viewer)));
                Assert.Equal(1L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM principals WHERE enabled AND role<>'system'"));
                // After migrate the engine's login writes no memory table directly, and cannot move Sam's private memory.
                Assert.Equal("42501", await MemoryRows.Fails(login.Source, "DELETE FROM memories WHERE id=@id", ("id", party)));
                Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE resources SET subject_id='household',scope='shared' WHERE id=@id", ("id", party)));
                Assert.Equal("42501", await MemoryRows.Fails(login.Source, "UPDATE memory_events SET event='x' WHERE false"));
                // A capture token's mirror comes back as it was, still read-only (the table's triggers load with the restore)
                // and still kept by its connection, whose token stays refused; its operator can still remove the connection,
                // through the engine's login as the command does on an install, and the mirror moves to the trash.
                Assert.Equal("restore-feed restore:item/1", await MemoryRows.Scalar(restored, "SELECT connection||' '||external_id FROM memory_mirrors WHERE memory_id=@id", ("id", mirrored)));
                Assert.Equal("42501", await MemoryRows.Fails(restored, "UPDATE memories SET content='DEMO: by hand' WHERE id=@id", ("id", mirrored)));
                Assert.Equal("42501", await MemoryRows.Fails(restored, "INSERT INTO memory_trash(memory_id,reason,channel,purge_after) VALUES(@id,'forget','cli',now()+interval '14 days')", ("id", mirrored)));
                Assert.Equal("forbidden", (await Assert.ThrowsAsync<MemoryRefused>(() => new MemoryStore(restored).Capture(feed, new("DEMO: past the door.", Mirror: "restore:item/1"), "restore-feed", null, default))).Code);
                var said = new StringWriter();
                Assert.Equal(0, await CaptureCommand.Run(new DeployFolder(folder), ["disconnect", "restore-feed"], said, login.Source, MemoryToolSettings.With("tools: on, capture: on")));
                Assert.Contains("0 token(s) disabled, 1 mirror(s) moved to the trash", said.ToString());
                Assert.Equal("disconnect", await MemoryRows.Scalar(restored, "SELECT reason FROM memory_trash WHERE memory_id=@id", ("id", mirrored)));
                Assert.Equal(0L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM memory_mirrors"));
                Assert.Equal(1L, await MemoryRows.Scalar(restored, "SELECT count(*) FROM memories WHERE id=@id", ("id", mirrored)));
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            await MemoryRows.DropDatabase(restored, restoredName);
        }
    }

    // With scopes on (SB-P1-10 as well): the restored database records on, so starting it with rhythm.yaml off is refused
    // (memory.md 6.2: a restore never turns scopes off), and starting it with on adds nobody to Family (the switch's first
    // start was before the backup, 6.3), so Family is as the operator left it. Every old token stays disabled; the new
    // ones the operator makes for the same people and the household view read exactly what the old ones read, but the
    // rows the quarantine blanked until the next rebuild: Sam, taken out of Family, still reads his own scope and DEMO
    // rows only; the household view still reads Alex's glucose through her source share and not her private rows,
    // including the household's copy of her private Drive file, whose original the quarantine blanked; and each derived
    // record is served to the same readers.
    [RestoreFact] public async Task A_restore_keeps_scopes_on_and_Family_as_the_operator_left_it_and_each_reader_reads_as_before()
    {
        var name = "rhythm_restore_" + Guid.NewGuid().ToString("N")[..12];
        var restoredName = name + "_restored";
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        NpgsqlDataSource? source = null, restored = null;
        try
        {
            source = await MemoryRows.NewDatabase(name);
            var config = TestSettings.Use();
            await new Database(source, new AdapterRegistry([new DemoAdapter()]).Policies(config)).Migrate();
            var principals = new Principals(source);
            await principals.Install(config, default);
            // Scopes switched on at an earlier start (only tests reach it in this version), which put Alex and Sam in
            // Family; since then the operator took Sam out of Family, made Alex its admin and recorded her glucose share.
            var started = await principals.Install(ScopesOn(), default, acceptScopesOn: true);
            Assert.True(started.SwitchedOn);
            Assert.Equal(2, started.Joined);
            await principals.Leave("sam", default);
            await principals.Admin("alex", true, default);
            await principals.Share("alex", "cgm", default);
            var rows = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["alex note"] = await Scoped.Row(source, "alex", "notes", "private", "restore-alex-note"),
                ["alex glucose"] = await Scoped.Row(source, "alex", "cgm", "private", "restore-alex-cgm"),
                ["alex memory"] = await MemoryRows.Memory(source, "alex", "alex", "DEMO: Alex's fictional allotment plot is number 12."),
                ["alex drive file"] = await Scoped.Row(source, "alex", "drive", "private", "relationship-engine:fx-restore-on", native: "fx-restore-on"),
                ["household copy of alex drive file"] = await Scoped.Row(source, "household", "documents", "shared", "fx-restore-on", mode: "dated-snapshot"),
                ["sam note"] = await Scoped.Row(source, "sam", "notes", "private", "restore-sam-note"),
                ["family note"] = await Scoped.Row(source, "household", "notes", "shared", "restore-family-note"),
                ["family memory"] = await MemoryRows.Memory(source, "household", "alex", "DEMO: the fictional recycling goes out on Friday."),
                ["demo"] = await Scoped.Row(source, "fixture", "demo", "private", "restore-demo"),
            };
            // Each reader's token before the backup: Alex's and Sam's clients and one with no person (the household view).
            var readers = new[] { "alex", "sam", "household" };
            var old = new Dictionary<string, (Guid Id, string Token)>(StringComparer.Ordinal);
            foreach (var reader in readers)
            {
                var file = Path.Combine(folder, "old-" + reader);
                old[reader] = (await principals.Create("old-" + reader, "harness", null, reader == "household" ? null : reader, file, default), File.ReadAllText(file));
            }
            // An insight and a dashboard in Alex's private scope and one of each in Family's, as 027 stores them.
            var derived = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["alex dashboard"] = await Dashboard(source, "alex", old["alex"].Id, "rhythm:alex:notes"),
                ["family dashboard"] = await Dashboard(source, "household", old["household"].Id, "rhythm:household:notes"),
            };
            foreach (var (label, subject, by) in new[] { ("alex insight", "alex", old["alex"].Id), ("family insight", "household", old["household"].Id) })
            {
                derived[label] = Guid.NewGuid();
                await MemoryRows.Run(source, """
                    INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,
                      generated_by,generated_at,sources_as_of,saved_by,subject_id,scope)
                    VALUES(@id,'discovery','DEMO insight','DEMO · a fictional insight.','','','[]','weak','','[]','[]','manual',now(),now(),@by,@s,
                      CASE WHEN @s='household' THEN 'shared' ELSE 'private' END)
                    """, ("id", derived[label]), ("by", by), ("s", subject));
            }
            var oldDb = new Database(source);
            // The stars each reader is served (as DerivedScopes.Stars gives them from the map): Alex both notes stars, the
            // household view only Family's, Sam, out of Family, neither.
            var served = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["alex"] = ["rhythm:alex:notes", "rhythm:household:notes"], ["household"] = ["rhythm:household:notes"], ["sam"] = [],
            };
            async Task<SortedSet<string>> Reads(Database db, Guid reader, string who)
            {
                var readable = await Scoped.Readable(db, reader, rows.Values);
                var seen = new SortedSet<string>(rows.Where(r => readable.Contains(r.Value)).Select(r => r.Key), StringComparer.Ordinal);
                // The derived records it reads: the insights and dashboards the engine serves it, given the stars it is
                // served, so a dashboard citing a star it is not served is not one of them.
                var insights = await db.Read(reader, "SELECT read_insights(@principal,NULL,NULL,50,@stars) AS x", default, ("stars", served[who]));
                var dashboards = await db.Read(reader, "SELECT read_dashboards(@principal,NULL,NULL,false,@stars) AS x", default, ("stars", served[who]));
                foreach (var (label, id) in derived)
                    if ((label.EndsWith("insight") ? insights : dashboards).Contains(id.ToString())) seen.Add(label);
                return seen;
            }
            var before = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var reader in readers) before[reader] = await Reads(oldDb, old[reader].Id, reader);
            Assert.Equal(["demo", "sam note"], before["sam"]);
            Assert.Equal(["alex glucose", "demo", "family dashboard", "family insight", "family memory", "family note"], before["household"]);
            Assert.Contains("household copy of alex drive file", before["alex"]);
            Assert.Contains("alex insight", before["alex"]);

            restored = await BackUpAndRestore(name, restoredName);
            var db = new Database(restored);
            Assert.Empty(await db.Migrate());
            // Starting with rhythm.yaml off is refused: the restored database records on, so a restore never turns scopes
            // off, neither with this version of the engine nor with the one that accepts them.
            foreach (var accept in new[] { false, true })
                Assert.Contains("the database records on", (await Assert.ThrowsAsync<ConfigException>(
                    () => new Principals(restored).Install(config, default, acceptScopesOn: accept))).Message);
            // The start with scopes on adds nobody to Family: Sam stays out of it and Alex stays its admin.
            var memberships = (string)(await MemoryRows.Scalar(restored, "SELECT string_agg(person_id||' '||role,',' ORDER BY person_id) FROM memberships"))!;
            Assert.Equal("alex admin", memberships);
            Assert.Equal(new Principals.Started(0, 0, 0, false, 0, 0), await new Principals(restored).Install(ScopesOn(), default, acceptScopesOn: true));
            Assert.Equal(memberships, await MemoryRows.Scalar(restored, "SELECT string_agg(person_id||' '||role,',' ORDER BY person_id) FROM memberships"));
            Assert.Equal("alex cgm", await MemoryRows.Scalar(restored, "SELECT string_agg(person_id||' '||provider,',') FROM source_shares"));
            Assert.Equal("on", await MemoryRows.Scalar(restored, "SELECT value FROM install_settings WHERE key='memory.scopes'"));
            // Every old token stays disabled; the operator makes new ones for the same people and the household view.
            foreach (var reader in readers) Assert.Null(await db.Authenticate(old[reader].Token, default));
            foreach (var reader in readers)
            {
                var file = Path.Combine(folder, "new-" + reader);
                var id = await new Principals(restored).Create("new-" + reader, "harness", null, reader == "household" ? null : reader, file, default);
                Assert.Equal(id, (await db.Authenticate(File.ReadAllText(file), default))!.Id);
                // Each reads what that reader read before, but the Drive file's snapshot row, blanked until the next rebuild.
                before[reader].Remove("alex drive file");
                Assert.Equal(before[reader], await Reads(db, id, reader));
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            await MemoryRows.DropDatabase(restored, restoredName);
            await MemoryRows.DropDatabase(source, name);
        }
    }
}
