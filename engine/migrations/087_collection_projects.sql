-- One registry, with optional membership in the project portfolio. Existing foreign keys
-- remain valid. Only this upgrade infers membership from legacy dashboard records.
ALTER TABLE memory_projects ADD COLUMN show_in_projects boolean NOT NULL DEFAULT false;

WITH designated AS (
UPDATE memory_projects p SET show_in_projects=true,revision=revision+1,updated_at=greatest(now(),p.updated_at)
WHERE NOT p.held AND p.slug NOT IN ('project-ideas','projects-for-others') AND EXISTS (
 SELECT 1 FROM memories m LEFT JOIN memory_work w ON w.memory_id=m.id
 WHERE m.project_id=p.id AND m.hold IS NULL AND m.status<>'deprecated'
   AND (m.valid_until IS NULL OR m.valid_until>now())
   AND EXISTS (SELECT 1 FROM resources r WHERE r.id=m.id AND r.relationship_snapshot IS NULL AND r.status='current')
   AND NOT EXISTS (SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND ((w.state IN ('backlog','ready','active','review') AND memory_work_listed(m.id))
     OR (w.memory_id IS NULL AND m.tags && ARRAY['portfolio-project','project-overview'])
     OR (m.source='rhythm-starter' AND 'project-guide'=ANY(m.tags)))
)
RETURNING p.id,p.subject_id,p.revision
)
INSERT INTO memory_events(project_id,subject_id,revision,event,channel,client,detail)
 SELECT id,subject_id,revision,'updated','cli','collection migration',
   '{"fields":["show_in_projects"],"show_in_projects":true}'::jsonb FROM designated;

CREATE OR REPLACE FUNCTION memory_project_reply(p memory_projects,p_scoped boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',p.id,'subject',p.subject_id,'scope',CASE WHEN p.subject_id='household' THEN 'shared' ELSE 'private' END,
   'slug',p.slug,'name',p.name,'description',p.description,'status',p.status,'author',p.author_id,'revision',p.revision,
   'show_in_projects',p.show_in_projects,'aliases',to_jsonb(ARRAY(SELECT a.alias FROM memory_project_aliases a WHERE a.project_id=p.id ORDER BY a.alias)),
   'created_at',p.created_at,'updated_at',p.updated_at,'scopes',CASE WHEN p_scoped THEN 'on' ELSE 'off' END)
$$;

CREATE OR REPLACE FUNCTION create_memory_project(p_principal uuid,p_project jsonb,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; person text; landed text; slug text; name text; description text; aliases text[];
 made uuid:=gen_random_uuid(); p memory_projects; idea memories; overview_tags text[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing projects is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 p_project:=jsonb_strip_nulls(p_project);
 IF p_project IS NULL OR jsonb_typeof(p_project)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_project) k WHERE k NOT IN ('slug','name','description','aliases','scope','show_in_projects','source_idea','expected_idea'))
   OR ((p_project ? 'source_idea') <> (p_project ? 'expected_idea'))
   OR (p_project ? 'show_in_projects' AND jsonb_typeof(p_project->'show_in_projects')<>'boolean')
   OR EXISTS(SELECT 1 FROM jsonb_each(p_project) e WHERE e.key IN ('slug','name','description','scope') AND jsonb_typeof(e.value)<>'string') THEN
   RAISE EXCEPTION 'A collection takes a slug, name, description, aliases, scope and optional Show in Projects. Starting from an idea also requires its ID and revision.' USING ERRCODE='22023';
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
 -- Lock the source in the same order as update_memory, before the registry lock.
 IF p_project ? 'source_idea' THEN
   idea:=memory_for_change(p_principal,(p_project->>'source_idea')::uuid,(p_project->>'expected_idea')::integer,scoped);
   IF idea.subject_id<>landed OR idea.hold IS NOT NULL OR idea.status='deprecated' OR idea.capsule IS NOT NULL
     OR idea.valid_until<=now()
     OR EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=idea.id)
     OR idea.tags && ARRAY['project-overview','portfolio-project','capture-rule','capture-workflow','standing','idea-inbox','inbox-guide']
     OR NOT (idea.tags && ARRAY['project-idea','idea-stage','third-party-project','portfolio-idea']
       OR EXISTS (SELECT 1 FROM memory_projects inbox WHERE inbox.id=idea.project_id AND inbox.slug IN ('project-ideas','projects-for-others'))) THEN
     RAISE EXCEPTION 'Choose a current idea in the same scope as the new project.' USING ERRCODE='22023';
   END IF;
   IF NOT coalesce((p_project->>'show_in_projects')::boolean,false) THEN
     RAISE EXCEPTION 'Starting a project from an idea requires Show in Projects.' USING ERRCODE='22023';
   END IF;
 END IF;
 PERFORM memory_project_lock(landed);
 PERFORM memory_project_claim(landed,slug,aliases,NULL);
 INSERT INTO memory_projects(id,subject_id,slug,name,description,author_id,created_by,show_in_projects)
 VALUES(made,landed,slug,name,description,person,p_principal,coalesce((p_project->>'show_in_projects')::boolean,false)) RETURNING * INTO p;
 INSERT INTO memory_project_aliases(project_id,subject_id,alias) SELECT made,landed,a FROM unnest(aliases) a;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(made,landed,1,'created',p_principal,channel,p_client,jsonb_build_object('slug',slug));
 IF idea.id IS NOT NULL THEN
   overview_tags:=ARRAY(SELECT t FROM unnest(idea.tags) WITH ORDINALITY tags(t,i)
     WHERE t NOT IN ('project-idea','idea-stage','third-party-project','portfolio-idea') ORDER BY i);
   PERFORM update_memory(p_principal,idea.id,jsonb_build_object('project',slug,'tags',to_jsonb(overview_tags||ARRAY['project-overview'])),
     idea.revision,p_channel,p_client,NULL);
 END IF;
 RETURN memory_project_reply(p,scoped);
END $$;

CREATE OR REPLACE FUNCTION update_memory_project(p_principal uuid,p_project uuid,p_change jsonb,p_expected integer,p_channel text,
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
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_change) k WHERE k NOT IN ('name','description','aliases','status','show_in_projects'))
   OR (p_change ? 'show_in_projects' AND jsonb_typeof(p_change->'show_in_projects')<>'boolean')
   OR EXISTS(SELECT 1 FROM jsonb_each(p_change) e WHERE e.key IN ('name','description','status') AND jsonb_typeof(e.value)<>'string')
   OR p_change ? 'aliases' AND jsonb_typeof(p_change->'aliases')<>'array'
   OR p_change ? 'status' AND p_change->>'status' NOT IN ('active','archived') THEN
   RAISE EXCEPTION 'A collection change can set name, description, aliases, status (active or archived) or Show in Projects.' USING ERRCODE='22023';
 END IF;
 -- A project never changes scope, so its registry's lock can be taken before its row is (the order create takes).
 SELECT x.subject_id INTO subject FROM memory_projects x WHERE x.id=p_project;
 IF FOUND THEN PERFORM memory_project_lock(subject); END IF;
 p:=memory_project_for_change(p_principal,p_project,p_expected,scoped);
 n:=p;
 IF p_change ? 'show_in_projects' THEN n.show_in_projects:=(p_change->>'show_in_projects')::boolean; END IF;
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
   ('show_in_projects',n.show_in_projects IS DISTINCT FROM p.show_in_projects),('aliases',aliases IS DISTINCT FROM had),('status',n.status IS DISTINCT FROM p.status)) c(f,d) WHERE d);
 IF cardinality(changed)=0 THEN
   RETURN memory_project_reply(p,scoped)||jsonb_build_object('unchanged',true);
 END IF;
 IF 'aliases'=ANY(changed) THEN
   DELETE FROM memory_project_aliases a WHERE a.project_id=p.id AND NOT a.alias=ANY(aliases);
   INSERT INTO memory_project_aliases(project_id,subject_id,alias) SELECT p.id,p.subject_id,x FROM unnest(aliases) x WHERE NOT x=ANY(had);
 END IF;
 UPDATE memory_projects SET name=n.name,description=n.description,status=n.status,show_in_projects=n.show_in_projects,revision=revision+1,
   updated_at=greatest(now(),updated_at) WHERE id=p.id RETURNING * INTO p;
 detail:=jsonb_build_object('fields',to_jsonb(changed));
 IF 'status'=ANY(changed) THEN detail:=detail||jsonb_build_object('status',p.status); END IF;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(p.id,p.subject_id,p.revision,CASE WHEN changed=ARRAY['status'] THEN CASE p.status WHEN 'archived' THEN 'archived' ELSE 'restored' END
   ELSE 'updated' END,p_principal,channel,p_client,detail);
 RETURN memory_project_reply(p,scoped)||jsonb_build_object('changed',to_jsonb(changed));
END $$;

CREATE OR REPLACE FUNCTION import_memory_project(p_person text,p_private boolean,p_family boolean,p_origin jsonb,p_project jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_id uuid; v_subject text; v_author text; v_slug text; why text; known memory_projects;
 aliases text[]; kept text[];
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF jsonb_typeof(p_project) IS DISTINCT FROM 'object' OR jsonb_typeof(p_project->'subject') IS DISTINCT FROM 'string'
     OR (p_project ? 'showInProjects' AND jsonb_typeof(p_project->'showInProjects')<>'boolean')
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
   INSERT INTO memory_projects(id,subject_id,slug,name,description,status,author_id,created_by,revision,created_at,updated_at,held,held_for,show_in_projects)
   VALUES(v_id,v_subject,v_slug,memory_project_name(p_project->>'name'),memory_project_description(p_project->>'description'),
     p_project->>'status',v_author,NULL,(p_project->>'revision')::integer,(p_project->>'createdAt')::timestamptz,
     (p_project->>'updatedAt')::timestamptz,coalesce((p_project->>'held')::boolean,false),
     CASE WHEN coalesce((p_project->>'held')::boolean,false) THEN (p_project->>'heldFor')::uuid END,coalesce((p_project->>'showInProjects')::boolean,false));
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

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_project_reply(memory_projects,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION create_memory_project(uuid,jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION update_memory_project(uuid,uuid,jsonb,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_project(text,boolean,boolean,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
