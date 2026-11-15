-- The project registry's checked writers (docs/dev/memory.md section 5; SB-P3-07 in docs/dev/second-brain-parity/
-- backlog.md): create a project, change its name, description, aliases or status (archive and back), and delete it,
-- keeping its memories. Reads (list, and a project found by its slug or an alias) go through the read predicate in the
-- engine (Core/MemoryProjects.cs); nothing here reads for a caller.
--
-- Adapted from Second Brain's registry (src/projects/registry.ts at the pinned commit, MIT): one registry per scope, a
-- slug that never changes once made, a name, a description, up to 16 aliases that claim plain tags (a memory of the
-- project's scope tagged with one is a member, as upstream, without its tags rewritten), archive that hides a project from
-- the list unless asked, and delete that removes the grouping only. Unlike upstream, a Family project a person made
-- changes only by that person or a Family admin (D-031, memory.md section 5), a slug and an alias never name two projects
-- of one scope, and a memory that sat in a deleted project keeps a version of the state it had.
--
-- Nothing calls these yet but tests: no MCP tool, app tool or route writes a project (SB-P3-09, SB-P3-12), so every
-- existing answer is as before. With memory.scopes off, every install's default, a project is a household project and
-- every admitted harness and app login may change any project, today's model (D-048); a private one is refused.
--
-- Every writer here takes the recorded mode's row FOR SHARE first (lock_memory_scopes, 033), so rhythm scopes off, which
-- refuses while any memory project is private, never interleaves with one (memory.md 6.2). A writer that adds a slug or
-- an alias takes its scope's registry lock (memory_project_lock) before it looks, and so does 033's memory_project,
-- replaced below, which makes the project a memory write names. The delete locks each member memory's resources row
-- before its memories row (D-086), then the project's row, so it waits for a memory change in flight and never deadlocks
-- with one.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown project, which
-- is also the answer for one the caller does not read; 22023 invalid input; 40001 a stale expected revision (its message
-- names the current one); 23505 with the constraint memory_project_names for a slug or alias another project of that
-- scope already answers to. A refused write writes nothing. The number is provisional.

-- A scope's registry lock, held to the end of the transaction: whoever adds a slug or an alias to that scope's registry
-- takes it first, so two writers never give one name to two projects.
CREATE FUNCTION memory_project_lock(p_subject text) RETURNS void LANGUAGE sql AS $$
 SELECT pg_advisory_xact_lock(hashtextextended('memory-projects:'||p_subject,0))
$$;

-- Whether p_name is free in p_subject's registry for p_project (null for a new one): no other project there has it as
-- its slug or as an alias.
CREATE FUNCTION memory_project_free(p_subject text,p_name text,p_project uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT NOT EXISTS(SELECT 1 FROM memory_projects p WHERE p.subject_id=p_subject AND p.slug=p_name AND p.id IS DISTINCT FROM p_project)
   AND NOT EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.subject_id=p_subject AND a.alias=p_name AND a.project_id IS DISTINCT FROM p_project)
$$;

-- A project's name (1 to 120 characters once trimmed, no control character) and description (at most 1000), as 025's
-- checks hold them, refused here with the engine's words.
CREATE FUNCTION memory_project_name(p_name text) RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_name IS NULL OR length(btrim(p_name)) NOT BETWEEN 1 AND 120 OR p_name ~ '[[:cntrl:]]' THEN
   RAISE EXCEPTION 'A project''s name is 1 to 120 characters, with no control characters' USING ERRCODE='22023';
 END IF;
 RETURN btrim(p_name);
END $$;
CREATE FUNCTION memory_project_description(p_description text) RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF length(btrim(coalesce(p_description,'')))>1000 THEN
   RAISE EXCEPTION 'A project''s description is at most 1000 characters' USING ERRCODE='22023';
 END IF;
 RETURN btrim(coalesce(p_description,''));
END $$;

-- A project's aliases as the engine hands them (MemoryProjectInput.Aliases): at most 16 distinct tags, each trimmed and
-- lower-case, 1 to 128 characters, no control character, none in a namespace the engine sets itself (memory_reserved_tag,
-- 033: an alias claims a plain tag), and none the project's own slug.
CREATE FUNCTION memory_project_aliases_checked(p_aliases text[],p_slug text) RETURNS text[] LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_aliases IS NULL OR array_position(p_aliases,NULL) IS NOT NULL OR cardinality(p_aliases)>16
   OR EXISTS(SELECT 1 FROM unnest(p_aliases) a WHERE a<>btrim(a) OR a<>lower(a) OR length(a) NOT BETWEEN 1 AND 128 OR a ~ '[[:cntrl:]]')
   OR cardinality(p_aliases)<>(SELECT count(DISTINCT a) FROM unnest(p_aliases) a) THEN
   RAISE EXCEPTION 'A project takes at most 16 distinct aliases, each a lower-case tag of 1 to 128 characters' USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM unnest(p_aliases) a WHERE memory_reserved_tag(a)) THEN
   RAISE EXCEPTION 'An alias claims a plain tag, never one in a namespace the engine sets itself' USING ERRCODE='22023';
 END IF;
 IF p_slug=ANY(p_aliases) THEN
   RAISE EXCEPTION 'An alias is another name than the project''s own slug' USING ERRCODE='22023';
 END IF;
 RETURN ARRAY(SELECT a FROM unnest(p_aliases) a ORDER BY a);
END $$;

-- Where a new project lands (memory.md section 5: in your private scope, or in Family by any member), as a memory lands
-- (033's memory_landing, in a project's words): with scopes off the household's, a private one refused (D-048); with them
-- on the writer's own person's scope unless the call names Family; a writer with no person only in Family, only when the
-- call names it; and only where the writer reads.
CREATE FUNCTION memory_project_landing(p_principal uuid,p_person text,p_named text,p_scoped boolean) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE wanted text:=memory_scope_named(p_named); landed text;
BEGIN
 IF NOT p_scoped THEN
   IF wanted='private' THEN
     RAISE EXCEPTION 'private projects wait until per-person scopes are on; with them off every project is visible to everyone in the household' USING ERRCODE='42501';
   END IF;
   RETURN 'household';
 END IF;
 IF wanted='shared' THEN landed:='household';
 ELSIF p_person IS NULL THEN
   RAISE EXCEPTION 'this client has no person, so it has no private scope: name the shared scope (Family) to make a household project' USING ERRCODE='42501';
 ELSE landed:=p_person;
 END IF;
 IF NOT reads_subject(p_principal,landed) THEN
   RAISE EXCEPTION 'A project is made only where its writer reads: its own person''s scope or, as a Family member, Family' USING ERRCODE='42501';
 END IF;
 RETURN landed;
END $$;

-- What a project write answers: the project as the registry holds it, and the mode it was written under.
CREATE FUNCTION memory_project_reply(p memory_projects,p_scoped boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',p.id,'subject',p.subject_id,'scope',CASE WHEN p.subject_id='household' THEN 'shared' ELSE 'private' END,
   'slug',p.slug,'name',p.name,'description',p.description,'status',p.status,'author',p.author_id,'revision',p.revision,
   'aliases',to_jsonb(ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id ORDER BY a.alias)),
   'created_at',p.created_at,'updated_at',p.updated_at,'scopes',CASE WHEN p_scoped THEN 'on' ELSE 'off' END)
$$;

-- The project a change names, locked for it, then the checks: one the caller does not read and an unknown id answer
-- alike (P0002); one it reads but may not change is refused (42501): memory_may_change's rule (033), which is section
-- 5's for projects too (private: its owner; Family: its creator or an admin; a Family project no person made: any reader
-- of Family; with scopes off any admitted login); a stale expected revision is refused (40001) with the current one.
CREATE FUNCTION memory_project_for_change(p_principal uuid,p_project uuid,p_expected integer,p_scoped boolean) RETURNS memory_projects LANGUAGE plpgsql AS $$
DECLARE p memory_projects;
BEGIN
 SELECT * INTO p FROM memory_projects WHERE id=p_project FOR UPDATE;
 IF NOT FOUND OR NOT reads_subject(p_principal,p.subject_id) THEN
   RAISE EXCEPTION 'Unknown project' USING ERRCODE='P0002';
 END IF;
 IF NOT memory_may_change(p_principal,p.subject_id,p.author_id,p_scoped) THEN
   RAISE EXCEPTION 'Only this project''s creator or a Family admin may change it' USING ERRCODE='42501';
 END IF;
 IF p_expected IS NOT NULL AND p_expected<>p.revision THEN
   RAISE EXCEPTION 'The project changed since revision %: it is at revision % now; read it again and retry',p_expected,p.revision
     USING ERRCODE='40001';
 END IF;
 RETURN p;
END $$;

-- Refuses a name another project of p_subject already answers to, before anything is written.
CREATE FUNCTION memory_project_claim(p_subject text,p_slug text,p_aliases text[],p_project uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE taken text;
BEGIN
 IF p_slug IS NOT NULL AND NOT memory_project_free(p_subject,p_slug,p_project) THEN
   RAISE EXCEPTION 'Another project in this scope already answers to %',p_slug USING ERRCODE='23505',CONSTRAINT='memory_project_names';
 END IF;
 SELECT a INTO taken FROM unnest(coalesce(p_aliases,'{}')) a WHERE NOT memory_project_free(p_subject,a,p_project) ORDER BY a LIMIT 1;
 IF taken IS NOT NULL THEN
   RAISE EXCEPTION 'Another project in this scope already answers to %',taken USING ERRCODE='23505',CONSTRAINT='memory_project_names';
 END IF;
END $$;

-- create_memory_project: a new project from p_project, the fields the engine prepared (MemoryProjectInput): slug (lower
-- case letters, digits and hyphens, at most 64; the engine derives one from the name when the call gives none), name,
-- and optionally description, aliases and scope (named as memory_scope_named takes it). Active, revision 1, the writer's
-- person its author (none for a writer with no person), one audit row.
CREATE FUNCTION create_memory_project(p_principal uuid,p_project jsonb,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; person text; landed text; slug text; name text; description text; aliases text[];
 made uuid:=gen_random_uuid(); p memory_projects;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing projects is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 p_project:=jsonb_strip_nulls(p_project);
 IF p_project IS NULL OR jsonb_typeof(p_project)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_project) k WHERE k NOT IN ('slug','name','description','aliases','scope'))
   OR EXISTS(SELECT 1 FROM jsonb_each(p_project) e WHERE e.key IN ('slug','name','description','scope') AND jsonb_typeof(e.value)<>'string') THEN
   RAISE EXCEPTION 'A project takes slug, name, description, aliases and scope' USING ERRCODE='22023';
 END IF;
 SELECT person_id INTO person FROM principals WHERE id=p_principal;
 landed:=memory_project_landing(p_principal,person,p_project->>'scope',scoped);
 slug:=p_project->>'slug';
 IF slug IS NULL OR slug !~ '^[a-z0-9][a-z0-9-]{0,63}$' THEN
   RAISE EXCEPTION 'A project is a slug of lower-case letters, digits and hyphens, at most 64' USING ERRCODE='22023';
 END IF;
 name:=memory_project_name(p_project->>'name');
 description:=memory_project_description(p_project->>'description');
 aliases:=memory_project_aliases_checked(memory_strings(coalesce(p_project->'aliases','[]')),slug);
 PERFORM memory_project_lock(landed);
 PERFORM memory_project_claim(landed,slug,aliases,NULL);
 INSERT INTO memory_projects(id,subject_id,slug,name,description,author_id,created_by)
 VALUES(made,landed,slug,name,description,person,p_principal) RETURNING * INTO p;
 INSERT INTO memory_project_aliases(project_id,subject_id,alias) SELECT made,landed,a FROM unnest(aliases) a;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(made,landed,1,'created',p_principal,channel,p_client,jsonb_build_object('slug',slug));
 RETURN memory_project_reply(p,scoped);
END $$;

-- update_memory_project: what p_change names, and nothing else: name, description, aliases (a replacement) and status
-- (active or archived). The slug never changes: another name is an alias. A change that changes nothing writes nothing
-- and answers unchanged.
CREATE FUNCTION update_memory_project(p_principal uuid,p_project uuid,p_change jsonb,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; subject text; p memory_projects; n memory_projects; aliases text[]; had text[];
 changed text[]; detail jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing projects is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_change IS NULL OR jsonb_typeof(p_change)<>'object' OR p_change='{}'::jsonb
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_change) k WHERE k NOT IN ('name','description','aliases','status'))
   OR EXISTS(SELECT 1 FROM jsonb_each(p_change) e WHERE e.key IN ('name','description','status') AND jsonb_typeof(e.value)<>'string')
   OR p_change ? 'aliases' AND jsonb_typeof(p_change->'aliases')<>'array'
   OR p_change ? 'status' AND p_change->>'status' NOT IN ('active','archived') THEN
   RAISE EXCEPTION 'A change names name, description, aliases or status (active or archived)' USING ERRCODE='22023';
 END IF;
 -- A project never changes scope, so its registry's lock can be taken before its row is (the order create takes).
 SELECT x.subject_id INTO subject FROM memory_projects x WHERE x.id=p_project;
 IF FOUND THEN PERFORM memory_project_lock(subject); END IF;
 p:=memory_project_for_change(p_principal,p_project,p_expected,scoped);
 n:=p;
 IF p_change ? 'name' THEN n.name:=memory_project_name(p_change->>'name'); END IF;
 IF p_change ? 'description' THEN n.description:=memory_project_description(p_change->>'description'); END IF;
 IF p_change ? 'status' THEN n.status:=p_change->>'status'; END IF;
 had:=ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id ORDER BY a.alias);
 aliases:=had;
 IF p_change ? 'aliases' THEN
   aliases:=memory_project_aliases_checked(memory_strings(p_change->'aliases'),p.slug);
   PERFORM memory_project_claim(p.subject_id,NULL,aliases,p.id);
 END IF;
 changed:=ARRAY(SELECT f FROM (VALUES ('name',n.name IS DISTINCT FROM p.name),('description',n.description IS DISTINCT FROM p.description),
   ('aliases',aliases IS DISTINCT FROM had),('status',n.status IS DISTINCT FROM p.status)) c(f,d) WHERE d);
 IF cardinality(changed)=0 THEN
   RETURN memory_project_reply(p,scoped)||jsonb_build_object('unchanged',true);
 END IF;
 IF 'aliases'=ANY(changed) THEN
   DELETE FROM memory_project_aliases a WHERE a.project_id=p.id AND NOT a.alias=ANY(aliases);
   INSERT INTO memory_project_aliases(project_id,subject_id,alias) SELECT p.id,p.subject_id,x FROM unnest(aliases) x WHERE NOT x=ANY(had);
 END IF;
 UPDATE memory_projects SET name=n.name,description=n.description,status=n.status,revision=revision+1,
   updated_at=greatest(now(),updated_at) WHERE id=p.id RETURNING * INTO p;
 detail:=jsonb_build_object('fields',to_jsonb(changed));
 IF 'status'=ANY(changed) THEN detail:=detail||jsonb_build_object('status',p.status); END IF;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(p.id,p.subject_id,p.revision,CASE WHEN changed=ARRAY['status'] THEN CASE p.status WHEN 'archived' THEN 'archived' ELSE 'restored' END
   ELSE 'updated' END,p_principal,channel,p_client,detail);
 RETURN memory_project_reply(p,scoped)||jsonb_build_object('changed',to_jsonb(changed));
END $$;

-- delete_memory_project: the grouping only (memory.md section 5: delete, its memories kept). Each memory in it leaves it,
-- keeping the state it had as a version, at a new revision, with one audit row; one in the project's capsule slots
-- leaves those too, since a project slot needs its project (025). A memory tagged with one of its aliases keeps its tags.
-- The project's aliases go with it, and its audit rows stay. Answers the project's id and slug and how many memories
-- left it.
CREATE FUNCTION delete_memory_project(p_principal uuid,p_project uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; known memory_projects; p memory_projects; m memories; fields text[]; moved integer:=0;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing projects is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 -- Refused before any memory is locked: an unknown or unreadable project, or one not this caller's to change.
 SELECT * INTO known FROM memory_projects WHERE id=p_project;
 IF NOT FOUND OR NOT reads_subject(p_principal,known.subject_id) THEN
   RAISE EXCEPTION 'Unknown project' USING ERRCODE='P0002';
 END IF;
 IF NOT memory_may_change(p_principal,known.subject_id,known.author_id,scoped) THEN
   RAISE EXCEPTION 'Only this project''s creator or a Family admin may change it' USING ERRCODE='42501';
 END IF;
 -- Its memories' resources rows, then the memories (D-086), then the project's row, whose lock keeps any other memory
 -- from joining it now (a memory names a project only under a key share of its row). One that joined before that lock
 -- is locked after it, the same order.
 PERFORM 1 FROM resources r WHERE r.provider='memory' AND r.relationship_snapshot IS NULL
   AND r.id IN (SELECT x.id FROM memories x WHERE x.project_id=p_project) ORDER BY r.id FOR UPDATE;
 PERFORM 1 FROM memories x WHERE x.project_id=p_project ORDER BY x.id FOR UPDATE;
 p:=memory_project_for_change(p_principal,p_project,p_expected,scoped);
 PERFORM 1 FROM resources r WHERE r.provider='memory' AND r.relationship_snapshot IS NULL
   AND r.id IN (SELECT x.id FROM memories x WHERE x.project_id=p_project) ORDER BY r.id FOR UPDATE;
 FOR m IN SELECT * FROM memories x WHERE x.project_id=p_project ORDER BY x.id FOR UPDATE LOOP
   fields:=CASE WHEN m.capsule='project' THEN ARRAY['project','capsule'] ELSE ARRAY['project'] END;
   PERFORM memory_retire(m);
   UPDATE memories SET project_id=NULL,
     capsule=CASE WHEN capsule='project' THEN NULL ELSE capsule END,
     capsule_slot=CASE WHEN capsule='project' THEN NULL ELSE capsule_slot END,
     revision=revision+1,updated_at=greatest(now(),updated_at)
   WHERE id=m.id RETURNING * INTO m;
   INSERT INTO memory_events(memory_id,project_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,p.id,m.subject_id,m.revision,'updated',p_principal,channel,p_client,
     jsonb_build_object('fields',to_jsonb(fields),'cause','project_deleted'));
   moved:=moved+1;
 END LOOP;
 DELETE FROM memory_projects WHERE id=p.id;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(p.id,p.subject_id,p.revision,'deleted',p_principal,channel,p_client,jsonb_build_object('slug',p.slug,'memories',moved));
 RETURN jsonb_build_object('id',p.id,'slug',p.slug,'subject',p.subject_id,'deleted',true,'memories',moved,
   'scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
END $$;

-- 033's memory_project, the project a memory write names, found or made in the memory's own scope, now under its scope's
-- registry lock (memory_project_lock) so a project made here never takes a name a registry writer is giving an alias,
-- and with the project it finds key-share locked, so a delete of that project at the same moment either waits for this
-- write (and then takes its memory out too) or comes first, and the project is made anew here: never a foreign-key
-- error. Otherwise as before: by slug or alias, the slug first; made with the slug as its name when new.
CREATE OR REPLACE FUNCTION memory_project(p_principal uuid,p_person text,p_subject text,p_slug text,p_channel text,p_client text) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE known memory_projects; made uuid:=gen_random_uuid();
BEGIN
 IF p_slug IS NULL OR p_slug !~ '^[a-z0-9][a-z0-9-]{0,63}$' THEN
   RAISE EXCEPTION 'A project is a slug of lower-case letters, digits and hyphens, at most 64' USING ERRCODE='22023';
 END IF;
 PERFORM memory_project_lock(p_subject);
 SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=p_subject
   AND (p.slug=p_slug OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=p_slug))
   ORDER BY p.slug=p_slug DESC LIMIT 1 FOR KEY SHARE OF p;
 IF FOUND THEN RETURN jsonb_build_object('id',known.id,'slug',known.slug,'created',false); END IF;
 INSERT INTO memory_projects(id,subject_id,slug,name,author_id,created_by)
 VALUES(made,p_subject,p_slug,p_slug,p_person,p_principal) ON CONFLICT(subject_id,slug) DO NOTHING;
 IF NOT FOUND THEN
   SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=p_subject AND p.slug=p_slug FOR KEY SHARE OF p;
   RETURN jsonb_build_object('id',known.id,'slug',known.slug,'created',false);
 END IF;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(made,p_subject,1,'created',p_principal,p_channel,p_client,jsonb_build_object('slug',p_slug));
 RETURN jsonb_build_object('id',made,'slug',p_slug,'created',true);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_project_lock(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_free(text,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_name(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_description(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_aliases_checked(text[],text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_landing(uuid,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_reply(memory_projects,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_for_change(uuid,uuid,integer,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_claim(text,text,text[],uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION create_memory_project(uuid,jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION update_memory_project(uuid,uuid,jsonb,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION delete_memory_project(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project(uuid,text,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_project_lock(text),memory_project_free(text,text,uuid),memory_project_name(text),
 memory_project_description(text),memory_project_aliases_checked(text[],text),memory_project_landing(uuid,text,text,boolean),
 memory_project_reply(memory_projects,boolean),memory_project_for_change(uuid,uuid,integer,boolean),
 memory_project_claim(text,text,text[],uuid),create_memory_project(uuid,jsonb,text,text),
 update_memory_project(uuid,uuid,jsonb,integer,text,text),delete_memory_project(uuid,uuid,integer,text,text),
 memory_project(uuid,text,text,text,text,text) FROM PUBLIC;
