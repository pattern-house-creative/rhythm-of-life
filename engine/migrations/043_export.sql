-- rhythm export and rhythm import of native memories in Rhythm's own format (SB-P3-14 in
-- docs/dev/second-brain-parity/backlog.md; D-035; docs/dev/memory.md sections 5, 9 and 10.3; cli/README.md, Export and
-- import). An export is a read: the terminal (engine/src/Cli/MemoryArchive.cs) reads what one reader reads, through the
-- one read predicate, and needs nothing here but the install's id. An import writes only through the checked functions
-- below. They are the operator's, on the host, with no principal (channel cli, as delete forever's operator path in
-- 038): the engine's login, which only reads the memory tables, calls them, and only the terminal does
-- (EngineLoginTests).
--
-- An import names the reader it writes for (a person, or none: the household view) and the scopes it writes into (that
-- person's private scope, Family, or both), and adds only what such a reader may write there: never another person's
-- private records, never a private one while memory.scopes is off (D-048), and a Family record a person wrote only for
-- that person or a Family admin (memory.md section 5's author lock: the rule the record is changed by once it is here).
-- It never overwrites, moves or deletes a row that is here: a memory, project or link whose id is here already is left
-- as it is, and so is a memory this install deleted for good (its audit trail's purged row) and anything imported
-- before (the origin map). It keeps what the export carried: ids, scopes, authors, sources, statuses, tags, kinds,
-- importance, volatility, validity, capsule slots, holds, revisions and dates, earlier versions, projects with their
-- aliases, a trashed memory's trash dates, and person- and AI-made links between memories. Every memory is screened by
-- 036's trigger as any write is: one held there stays held, and one a person released there is held again here if its
-- words read like instructions (a release vouches for the install it was made in). Each earlier version is screened the
-- same way, so a state released there is held here too, and no undo, history or version read here serves its words as
-- released. Each memory and project it adds has one audit row, imported, as the operator's; the origin's audit rows are
-- not carried, since its logins are not this install's.
--
-- Nothing changes for an install that never runs rhythm import: no tool, route or job calls these functions, and no
-- answer reads the origin map. The number is provisional.

-- The install's own id, which every export names as its origin, so an import tells an export of this install from
-- another's. Made once here; a backup and its restore keep it (the same install's data).
INSERT INTO install_settings(key,value) VALUES('install.id',gen_random_uuid()::text) ON CONFLICT(key) DO NOTHING;

-- The origin map (D-035): each memory, project and link an import added, under the install it came from (rhythm: and
-- that install's id; SB-P8-01's importer adds origins of its own), with its id there, its id here (the same in Rhythm's
-- own format, which keeps ids, memory.md 10.3), its subject here and the export it came in. Ids only, never a word, and
-- no key on the record, so it outlives one deleted for good: an import of the same record again adds nothing.
CREATE TABLE memory_origins(
 origin text NOT NULL CHECK(origin ~ '^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9._:-]{1,200}$'),
 kind text NOT NULL CHECK(kind IN ('memory','project','link')),
 source_id text NOT NULL CHECK(source_id ~ '^[A-Za-z0-9._:-]{1,200}$'),
 local_id uuid NOT NULL,
 subject_id text NOT NULL REFERENCES subjects,
 export_id uuid NOT NULL,
 exported_at timestamptz NOT NULL,
 imported_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(origin,kind,source_id)
);
CREATE INDEX memory_origins_local ON memory_origins(local_id);
REVOKE ALL ON memory_origins FROM PUBLIC;

-- The list of checked tables in force, with the origin map added (042's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_origins']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- A made link an import brings has no maker here, and says it was imported: the origin's login is not this install's,
-- and an import is the operator's, who is no principal (as a memory's created_by and an audit row's principal are for
-- the operator, 025). Every other made link keeps its maker. Who removes an imported one is SB-P4-02's rule without its
-- maker: the owner, the source end's author or an admin.
ALTER TABLE made_links ADD COLUMN imported boolean NOT NULL DEFAULT false;
ALTER TABLE made_links ALTER COLUMN made_by DROP NOT NULL;
ALTER TABLE made_links ADD CONSTRAINT made_links_maker CHECK(made_by IS NOT NULL OR imported);

-- 038's restore_memory, bringing back a kept link an import brought too (no maker, marked imported; a link kept before
-- 043 has no such mark and is not imported); otherwise as it was.
CREATE OR REPLACE FUNCTION restore_memory(p_principal uuid,p_memory uuid,p_nonce uuid,p_channel text,p_client text DEFAULT NULL,
 p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; t memory_trash; back integer; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 hash:=memory_request_hash('restore',p_memory,coalesce(to_jsonb(p_nonce),'null'::jsonb),NULL);
 replay:=memory_replay(p_principal,p_key,'restore',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 t:=memory_in_trash(p_principal,p_memory,p_nonce);
 SELECT * INTO m FROM memories WHERE id=t.memory_id;
 IF NOT memory_may_change(p_principal,m.subject_id,m.author_id,scoped) THEN
   RAISE EXCEPTION 'Only this memory''s author or a Family admin may restore it' USING ERRCODE='42501';
 END IF;
 INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported)
 SELECT l.link_id,l.source_id,l.target_id,l.type,l.verification,l.evidence,l.lifecycle,l.made_by,l.created_at,coalesce(l.imported,false)
 FROM jsonb_populate_recordset(NULL::made_links,t.links) l
 WHERE EXISTS(SELECT 1 FROM resources s WHERE s.id=l.source_id) AND EXISTS(SELECT 1 FROM resources x WHERE x.id=l.target_id)
   AND (l.made_by IS NULL AND coalesce(l.imported,false) OR EXISTS(SELECT 1 FROM principals q WHERE q.id=l.made_by))
 ON CONFLICT DO NOTHING;
 GET DIAGNOSTICS back=ROW_COUNT;
 UPDATE resources SET status='current' WHERE id=m.id AND provider='memory' AND relationship_snapshot IS NULL;
 DELETE FROM memory_trash WHERE memory_id=m.id;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'restored',p_principal,channel,p_client,jsonb_build_object('reason',t.reason,
   'trashed_at',t.trashed_at,'links',back,'links_gone',jsonb_array_length(t.links)-back));
 result:=memory_reply(m,scoped)||jsonb_build_object('restored',jsonb_build_object('links',back,
   'links_gone',jsonb_array_length(t.links)-back,'trashed_at',t.trashed_at,'reason',t.reason));
 RETURN memory_answered(p_principal,p_key,'restore',hash,m.id,result);
END $$;

-- 041's memory_event_held, with an import's audit row marked held too when 036's screening held what it brought.
CREATE OR REPLACE FUNCTION memory_event_held() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE held jsonb;
BEGIN
 IF NEW.memory_id IS NULL OR NEW.event NOT IN ('created','appended','updated','reverted','imported') THEN RETURN NEW; END IF;
 held:=nullif(current_setting('rhythm.memory_held',true),'')::jsonb->(NEW.memory_id::text);
 IF held IS NOT NULL AND held->>'revision'=NEW.revision::text THEN
   NEW.detail:=NEW.detail||jsonb_build_object('held',held-'revision');
 END IF;
 RETURN NEW;
END $$;

-- Whether the current person p_person (null: a login with no person, the household view) reads p_subject's memories, as
-- reads_subject answers for a harness login bound to that person (readable_subjects, 023): with scopes off every
-- subject; with them on its person's own, Family's when it has no person or its person is a member, and DEMO's. Never
-- for a person who is not current, whatever the mode (their login is refused at the door, memory.md section 3). A source
-- share never names memories (033), so none counts. A test keeps the two equal.
CREATE FUNCTION memory_import_reads(p_person text,p_subject text,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_person IS NOT NULL AND NOT EXISTS(SELECT 1 FROM subjects b WHERE b.id=p_person AND b.kind='person' AND b.status='current') THEN false
   WHEN NOT p_scoped THEN true
   ELSE EXISTS(SELECT 1 FROM subjects s WHERE s.id=p_subject AND (s.kind='synthetic' OR s.kind='person' AND s.id=p_person
     OR s.kind='household' AND (p_person IS NULL OR EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=p_person)))) END
$$;

-- Whether that reader may write a record of p_subject whose author is p_author, as memory_may_change answers for its login
-- (033; memory.md section 5): the rule the record is changed by once it is here. With scopes off, every subject's (any
-- admitted login, D-048); with them on, a private record only its owner's, a Family record a person wrote only that
-- person's or a Family admin's, and one no person wrote any reader of Family's. Never for a person who is not current.
-- A test keeps it equal to memory_may_change for such a login.
CREATE FUNCTION memory_import_may_write(p_person text,p_subject text,p_author text,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_person IS NOT NULL AND NOT EXISTS(SELECT 1 FROM subjects b WHERE b.id=p_person AND b.kind='person' AND b.status='current') THEN false
   WHEN NOT p_scoped THEN true
   WHEN NOT memory_import_reads(p_person,p_subject,p_scoped) THEN false
   WHEN p_subject<>'household' THEN coalesce(p_person=p_subject,false)
   WHEN p_author IS NULL THEN true
   ELSE coalesce(p_person=p_author OR EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=p_person AND m.role='admin'),false) END
$$;

-- Why an import leaves out a record of p_subject by p_author, or null when it may add it: not_named (a scope the import
-- does not name, another person's private scope among them), private_off (a private record while memory.scopes is off,
-- D-048), unknown_author (an author who is no person here), invalid (a private record whose author is not its owner,
-- which 025 refuses), forbidden (one its reader may not write: the author lock, or Family for someone not in it).
CREATE FUNCTION memory_import_refusal(p_person text,p_private boolean,p_family boolean,p_subject text,p_author text,p_scoped boolean)
RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE
   WHEN p_subject IS NULL THEN 'invalid'
   WHEN NOT (p_subject='household' AND p_family OR p_person IS NOT NULL AND p_subject=p_person AND p_private) THEN 'not_named'
   WHEN NOT p_scoped AND p_subject<>'household' THEN 'private_off'
   WHEN p_author IS NOT NULL AND NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=p_author AND s.kind='person') THEN 'unknown_author'
   WHEN p_subject<>'household' AND p_author IS DISTINCT FROM p_subject THEN 'invalid'
   WHEN NOT memory_import_may_write(p_person,p_subject,p_author,p_scoped) THEN 'forbidden' END
$$;

-- The origin map's row for what an import added.
CREATE FUNCTION memory_import_origin(p_origin jsonb,p_kind text,p_id uuid,p_subject text) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_origins(origin,kind,source_id,local_id,subject_id,export_id,exported_at)
 VALUES(p_origin->>'origin',p_kind,p_id::text,p_id,p_subject,(p_origin->>'export')::uuid,(p_origin->>'exportedAt')::timestamptz)
$$;

-- Whether the origin map holds a record of this origin by that id.
CREATE FUNCTION memory_imported(p_origin jsonb,p_kind text,p_id uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_origins o WHERE o.origin=p_origin->>'origin' AND o.kind=p_kind AND o.source_id=p_id::text)
$$;

-- import_memory_project: one project of an export (its id, subject, slug, name, description, status, author, aliases,
-- revision and dates, and for a project a held write made its hold: held, and the memory it was held for), for p_person
-- (null: the household view) into the scopes p_private and p_family name. A held project comes in held, so it stays out
-- of every list here as it was there until a person releases a memory in it (SB-P3-23; held and held_for are 046's
-- columns, which this function reads only when it runs, after every migration); one not held, or from an export made
-- before an export carried the hold, comes in listed, as before. Answers its
-- outcome: added (with its id here, the same as there, and how many aliases another project here answers to already,
-- left out); present (a project with that id is here: left as it is); joined (a project of that scope here answers to
-- its slug, so the memories that named it go into that one, which is left as it is); deleted_here (this install
-- deleted that project); imported_before (the origin map has it, and it is gone since); not_named; or refused, with
-- the reason (memory_import_refusal's, id_taken for an id another scope's project has, invalid). The author lock
-- applies only to a project this import would add: one here already, by its id or its slug, writes nothing, so a
-- member's own memories in a project another member made join it (any reader of that scope); the rest of the refusal
-- applies to every outcome.
CREATE FUNCTION import_memory_project(p_person text,p_private boolean,p_family boolean,p_origin jsonb,p_project jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_id uuid; v_subject text; v_author text; v_slug text; why text; known memory_projects;
 aliases text[]; kept text[];
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF jsonb_typeof(p_project) IS DISTINCT FROM 'object' OR jsonb_typeof(p_project->'subject') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_project->'slug') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_project->'author','null'::jsonb)) NOT IN ('string','null') THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_project->>'id')::uuid; v_subject:=p_project->>'subject'; v_author:=p_project->>'author'; v_slug:=p_project->>'slug';
   IF v_id IS NULL OR p_project->>'scope' IS DISTINCT FROM (CASE WHEN v_subject='household' THEN 'shared' ELSE 'private' END) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,v_subject,v_author,scoped);
   IF why='not_named' THEN RETURN jsonb_build_object('outcome','not_named'); END IF;
   -- Forbidden by the author lock alone (the reader reads this scope): only adding the project is refused, below.
   IF why IS NOT NULL AND NOT (why='forbidden' AND memory_import_reads(p_person,v_subject,scoped)) THEN
     RETURN jsonb_build_object('outcome','refused','reason',why);
   END IF;
   SELECT * INTO known FROM memory_projects p WHERE p.id=v_id;
   IF FOUND THEN
     RETURN CASE WHEN known.subject_id=v_subject THEN jsonb_build_object('outcome','present','id',known.id)
       ELSE jsonb_build_object('outcome','refused','reason','id_taken') END;
   END IF;
   IF EXISTS(SELECT 1 FROM memory_events e WHERE e.project_id=v_id AND e.memory_id IS NULL AND e.event='deleted') THEN
     RETURN jsonb_build_object('outcome','deleted_here');
   END IF;
   IF memory_imported(p_origin,'project',v_id) THEN RETURN jsonb_build_object('outcome','imported_before'); END IF;
   PERFORM memory_project_lock(v_subject);
   SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=v_subject
     AND (p.slug=v_slug OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=v_slug))
     ORDER BY p.slug=v_slug DESC LIMIT 1;
   IF FOUND THEN RETURN jsonb_build_object('outcome','joined','id',known.id); END IF;
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   aliases:=memory_project_aliases_checked(memory_strings(coalesce(p_project->'aliases','[]'::jsonb)),v_slug);
   kept:=ARRAY(SELECT a FROM unnest(aliases) a WHERE memory_project_free(v_subject,a,v_id) ORDER BY a);
   INSERT INTO memory_projects(id,subject_id,slug,name,description,status,author_id,created_by,revision,created_at,updated_at,held,held_for)
   VALUES(v_id,v_subject,v_slug,memory_project_name(p_project->>'name'),memory_project_description(p_project->>'description'),
     p_project->>'status',v_author,NULL,(p_project->>'revision')::integer,(p_project->>'createdAt')::timestamptz,
     (p_project->>'updatedAt')::timestamptz,coalesce((p_project->>'held')::boolean,false),
     CASE WHEN coalesce((p_project->>'held')::boolean,false) THEN (p_project->>'heldFor')::uuid END);
   INSERT INTO memory_project_aliases(project_id,subject_id,alias) SELECT v_id,v_subject,a FROM unnest(kept) a;
   INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(v_id,v_subject,(p_project->>'revision')::integer,'imported',NULL,'cli','rhythm import',
     jsonb_build_object('origin',p_origin->>'origin','export',p_origin->>'export','slug',v_slug,
       'aliases_left_out',cardinality(aliases)-cardinality(kept)));
   PERFORM memory_import_origin(p_origin,'project',v_id,v_subject);
   RETURN jsonb_build_object('outcome','added','id',v_id,'aliasesLeftOut',cardinality(aliases)-cardinality(kept));
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

-- import_memory: one memory of an export, with its earlier versions and its trash row, for p_person into the scopes
-- p_private and p_family name; p_projects maps the export's project ids to the projects here its projects came in as.
-- Answers its outcome: added (with how many earlier versions it kept and left out, whether it is in the trash, held,
-- and held here by 036's screening though it was not held there); present (a memory with that id is here, in any state:
-- left as it is); deleted_here (this install deleted it for good); imported_before (the origin map has it); expired (it
-- was in the trash past its purge date, so this install's purge would delete it at once); not_named; or refused, with
-- the reason (memory_import_refusal's, id_taken for an id another record here has, project for a project that did not
-- come in, invalid). Earlier versions are kept from the newest one in a scope the reader does not read on, as its
-- history would show them to that reader (034's floor): a version in another person's private scope never lands here,
-- nor, with scopes off, one in any person's. A version whose state has no hold is held when 036's screening holds its
-- text and tags (a release there counts only there, D-178).
CREATE FUNCTION import_memory(p_person text,p_private boolean,p_family boolean,p_origin jsonb,p_memory jsonb,p_projects jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_id uuid; v_subject text; v_scope text; v_author text; why text; v_project uuid; v_trash jsonb;
 m memories; v_floor integer; kept integer; given integer;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(p_memory->'subject') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_memory->'content') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_memory->'author','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_memory->'project','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_memory->'versions','[]'::jsonb))<>'array'
     OR jsonb_typeof(coalesce(p_memory->'trash','null'::jsonb)) NOT IN ('object','null')
     OR jsonb_typeof(coalesce(p_projects,'{}'::jsonb))<>'object' THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid; v_subject:=p_memory->>'subject'; v_author:=p_memory->>'author';
   v_scope:=CASE WHEN v_subject='household' THEN 'shared' ELSE 'private' END;
   IF v_id IS NULL OR p_memory->>'scope' IS DISTINCT FROM v_scope THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,v_subject,v_author,scoped);
   IF why='not_named' THEN RETURN jsonb_build_object('outcome','not_named'); END IF;
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   IF EXISTS(SELECT 1 FROM memories x WHERE x.id=v_id) THEN RETURN jsonb_build_object('outcome','present'); END IF;
   IF EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='purged') THEN
     RETURN jsonb_build_object('outcome','deleted_here');
   END IF;
   IF memory_imported(p_origin,'memory',v_id) THEN RETURN jsonb_build_object('outcome','imported_before'); END IF;
   IF EXISTS(SELECT 1 FROM resources r WHERE r.id=v_id) THEN RETURN jsonb_build_object('outcome','refused','reason','id_taken'); END IF;
   IF p_memory->>'project' IS NOT NULL THEN
     v_project:=(coalesce(p_projects,'{}'::jsonb)->>(p_memory->>'project'))::uuid;
     IF v_project IS NULL OR NOT EXISTS(SELECT 1 FROM memory_projects p WHERE p.id=v_project AND p.subject_id=v_subject) THEN
       RETURN jsonb_build_object('outcome','refused','reason','project');
     END IF;
   END IF;
   v_trash:=CASE WHEN jsonb_typeof(p_memory->'trash')='object' THEN p_memory->'trash' END;
   IF v_trash IS NOT NULL AND (v_trash->>'purgeAfter')::timestamptz<=now() THEN RETURN jsonb_build_object('outcome','expired'); END IF;
   INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
   VALUES(v_id,v_subject,'memory',v_id::text,'memory',v_scope,'Memory','',CASE WHEN v_trash IS NULL THEN 'current' ELSE 'deleted' END,'memory');
   INSERT INTO memories(id,subject_id,scope,author_id,created_by,content,tags,kind,status,importance,volatility,source,project_id,
     capsule,capsule_slot,hold,hold_score,valid_from,valid_until,revision,created_at,updated_at)
   VALUES(v_id,v_subject,v_scope,v_author,NULL,memory_check_text(p_memory->>'content'),
     memory_check_tags(memory_strings(coalesce(p_memory->'tags','[]'::jsonb))),p_memory->>'kind',p_memory->>'status',
     (p_memory->>'importance')::smallint,p_memory->>'volatility',p_memory->>'source',v_project,p_memory->>'capsule',
     p_memory->>'capsuleSlot',p_memory->>'hold',(p_memory->>'holdScore')::real,(p_memory->>'validFrom')::timestamptz,
     (p_memory->>'validUntil')::timestamptz,(p_memory->>'revision')::integer,(p_memory->>'createdAt')::timestamptz,
     (p_memory->>'updatedAt')::timestamptz)
   RETURNING * INTO m;
   IF EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(p_memory->'versions','[]'::jsonb)) v
       WHERE jsonb_typeof(v)<>'object' OR jsonb_typeof(v->'subject') IS DISTINCT FROM 'string' OR (v->>'revision')::integer>=m.revision) THEN
     RAISE EXCEPTION 'An earlier version comes before the memory''s revision' USING ERRCODE='22023';
   END IF;
   given:=jsonb_array_length(coalesce(p_memory->'versions','[]'::jsonb));
   SELECT coalesce(max((v->>'revision')::integer),0)+1 INTO v_floor FROM jsonb_array_elements(coalesce(p_memory->'versions','[]'::jsonb)) v
     WHERE CASE WHEN scoped THEN NOT memory_import_reads(p_person,v->>'subject',true) ELSE v->>'subject'<>'household' END;
   -- Each earlier version is screened as the memory is: one whose state has no hold, held there and released or never
   -- held, is held here when 036's screening holds its words, so no release crosses installs. Otherwise an undo through
   -- an AI client's channel would bring back a released state's words unheld (041 keeps a revision's own hold, and its
   -- update screens new words only for what they add), and history and a version read would serve them (MemoryHistory
   -- serves a state without a hold whole).
   INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
   SELECT v_id,(v->>'revision')::integer,v->>'subject',v->>'scope',x.content,x.tags,v->>'status',(v->>'validFrom')::timestamptz,
     (v->>'validUntil')::timestamptz,
     x.state||CASE WHEN x.state->>'hold' IS NULL AND w.verdict->>'reason' IS NOT NULL
       THEN jsonb_build_object('hold',w.verdict->>'reason','hold_score',w.verdict->'score') ELSE '{}'::jsonb END,
     (v->>'writtenAt')::timestamptz,(v->>'retiredAt')::timestamptz
   FROM jsonb_array_elements(coalesce(p_memory->'versions','[]'::jsonb)) v
   CROSS JOIN LATERAL (SELECT memory_check_text(v->>'content') AS content,
     memory_check_tags(memory_strings(coalesce(v->'tags','[]'::jsonb))) AS tags,
     CASE WHEN coalesce(p_projects,'{}'::jsonb) ? coalesce(v->'state'->>'project_id','')
       THEN jsonb_set(v->'state','{project_id}',p_projects->(v->'state'->>'project_id')) ELSE v->'state' END AS state) x
   CROSS JOIN LATERAL (SELECT CASE WHEN x.state->>'hold' IS NULL THEN memory_screen(x.content,x.tags) END AS verdict) w
   WHERE (v->>'revision')::integer>=v_floor ORDER BY (v->>'revision')::integer;
   GET DIAGNOSTICS kept=ROW_COUNT;
   IF v_trash IS NOT NULL THEN
     INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,trashed_at,purge_after,links)
     VALUES(v_id,v_trash->>'reason',NULL,'cli',(v_trash->>'trashedAt')::timestamptz,(v_trash->>'purgeAfter')::timestamptz,'[]');
   END IF;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(v_id,v_subject,m.revision,'imported',NULL,'cli','rhythm import',jsonb_build_object('origin',p_origin->>'origin',
     'export',p_origin->>'export','versions',kept,'versions_left_out',given-kept,'trashed',v_trash IS NOT NULL));
   PERFORM memory_import_origin(p_origin,'memory',v_id,v_subject);
   RETURN jsonb_build_object('outcome','added','id',v_id,'versions',kept,'versionsLeftOut',given-kept,'trashed',v_trash IS NOT NULL,
     'held',m.hold IS NOT NULL,'heldHere',m.hold IS NOT NULL AND p_memory->>'hold' IS NULL);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

-- import_memory_link: one person- or AI-made link of an export (its id, the scope both its ends are in, its ends, type,
-- verification, evidence, lifecycle and date; and, for one a trashed memory's trash row kept, that memory), for p_person
-- into the scopes p_private and p_family name. A link joins two memories of one scope (memory.md section 5), so both
-- ends must be memories here in the scope it names, and its reader may write the source end. A kept one goes back into
-- the trash row of the memory this export brought in trashed, and comes back with it (restore_memory); any other needs
-- both ends out of the trash. Its maker here is none, and it is marked imported (see made_links above). Answers its outcome: added;
-- present (a link with that id, or of that type between the same two memories, is here); imported_before; not_named;
-- or refused, with the reason (memory_import_refusal's, ends for ends that are not such memories here, invalid).
CREATE FUNCTION import_memory_link(p_person text,p_private boolean,p_family boolean,p_origin jsonb,p_link jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_id uuid; v_subject text; s memories; t memories; why text; v_kept uuid; v_link jsonb;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF jsonb_typeof(p_link) IS DISTINCT FROM 'object' OR jsonb_typeof(p_link->'subject') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_link->'trash','null'::jsonb)) NOT IN ('string','null') THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_link->>'id')::uuid; v_subject:=p_link->>'subject'; v_kept:=(p_link->>'trash')::uuid;
   IF v_id IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,v_subject,NULL,scoped);
   IF why='not_named' THEN RETURN jsonb_build_object('outcome','not_named'); END IF;
   SELECT * INTO s FROM memories x WHERE x.id=(p_link->>'source')::uuid AND x.subject_id=v_subject;
   SELECT * INTO t FROM memories x WHERE x.id=(p_link->>'target')::uuid AND x.subject_id=v_subject;
   IF s.id IS NULL OR t.id IS NULL OR s.id=t.id OR v_kept IS NOT NULL AND v_kept NOT IN (s.id,t.id) THEN
     RETURN jsonb_build_object('outcome','refused','reason','ends');
   END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,s.subject_id,s.author_id,scoped);
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   IF EXISTS(SELECT 1 FROM made_links l WHERE l.link_id=v_id OR l.source_id=s.id AND l.target_id=t.id AND l.type=p_link->>'type')
     OR EXISTS(SELECT 1 FROM memory_trash x, jsonb_array_elements(x.links) e WHERE e->>'link_id'=v_id::text) THEN
     RETURN jsonb_build_object('outcome','present');
   END IF;
   IF memory_imported(p_origin,'link',v_id) THEN RETURN jsonb_build_object('outcome','imported_before'); END IF;
   v_link:=jsonb_build_object('link_id',v_id,'source_id',s.id,'target_id',t.id,'type',p_link->>'type',
     'verification',p_link->>'verification','evidence',coalesce(p_link->>'evidence',''),'lifecycle',p_link->>'lifecycle',
     'made_by',NULL,'imported',true,'created_at',(p_link->>'createdAt')::timestamptz);
   IF v_kept IS NOT NULL THEN
     -- Only into the trash row of a memory this same export brought in: an import never changes a row it found here.
     IF NOT EXISTS(SELECT 1 FROM memory_trash x WHERE x.memory_id=v_kept)
       OR NOT EXISTS(SELECT 1 FROM memory_origins o WHERE o.origin=p_origin->>'origin' AND o.kind='memory'
         AND o.source_id=v_kept::text AND o.export_id=(p_origin->>'export')::uuid) THEN
       RETURN jsonb_build_object('outcome','refused','reason','ends');
     END IF;
     -- The same checks made_links' columns would make.
     PERFORM 1 FROM jsonb_populate_record(NULL::made_links,v_link) l WHERE l.type ~ '^[a-z][a-z0-9_-]{0,63}$'
       AND l.verification IN ('verified','suggested','inferred') AND length(l.evidence)<=1000
       AND l.lifecycle IN ('active','stale','retired','revoked','deleted') AND l.created_at IS NOT NULL;
     IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
     UPDATE memory_trash SET links=links||jsonb_build_array(v_link) WHERE memory_id=v_kept;
   ELSE
     IF EXISTS(SELECT 1 FROM memory_trash x WHERE x.memory_id IN (s.id,t.id)) THEN
       RETURN jsonb_build_object('outcome','refused','reason','ends');
     END IF;
     INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported)
     SELECT l.link_id,l.source_id,l.target_id,l.type,l.verification,l.evidence,l.lifecycle,NULL,l.created_at,true
     FROM jsonb_populate_record(NULL::made_links,v_link) l;
   END IF;
   PERFORM memory_import_origin(p_origin,'link',v_id,v_subject);
   RETURN jsonb_build_object('outcome','added','id',v_id,'trashed',v_kept IS NOT NULL);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION restore_memory(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_held() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_reads(text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_may_write(text,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_refusal(text,boolean,boolean,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_origin(jsonb,text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_imported(jsonb,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_project(text,boolean,boolean,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory(text,boolean,boolean,jsonb,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_link(text,boolean,boolean,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION restore_memory(uuid,uuid,uuid,text,text,text),memory_event_held(),memory_import_reads(text,text,boolean),
 memory_import_may_write(text,text,text,boolean),memory_import_refusal(text,boolean,boolean,text,text,boolean),
 memory_import_origin(jsonb,text,uuid,text),memory_imported(jsonb,text,uuid),
 import_memory_project(text,boolean,boolean,jsonb,jsonb),import_memory(text,boolean,boolean,jsonb,jsonb,jsonb),
 import_memory_link(text,boolean,boolean,jsonb,jsonb) FROM PUBLIC;
