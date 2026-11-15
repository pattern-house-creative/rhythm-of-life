-- The Second Brain export importer (SB-P8-01 in docs/dev/second-brain-parity/backlog.md; story 16; D-035, D-037, D-048,
-- D-049, D-120 to D-124; docs/dev/memory.md sections 9 and 10.3; docs/dev/second-brain-parity/migration-runbook.md; ledger
-- row EXP-02). The terminal (engine/src/Cli/SecondBrainImport.cs, rhythm import-second-brain, only with
-- memory.secondBrainImport on) reads one export file of a Second Brain service (upstream's GET /export, version 3 and
-- older: entries, edges and projects), works out where each record lands and what it carries, and writes only through
-- the checked functions below, as the operator: no principal, channel cli, as 043's rhythm import (only the terminal calls
-- them, EngineLoginTests). A dry run (the command's default) calls them in a transaction it rolls back.
--
-- Unlike 043's import of Rhythm's own format, which keeps each record's id, a Second Brain record gets a new native id
-- (memory.md 10.3) and its own id goes into the origin map (043's memory_origins) under the origin second-brain:default:
-- a memory's id as the file gives it, or, when it is outside the map's alphabet (an id a client chose before upstream's
-- 4.0, D-124), its SHA-256; a project's slug and creation time (one slug can come from two workspaces in one file, D-123);
-- a link's type and ends, hashed (an edge has no id in the export). A record the origin map holds is never written again,
-- whether it is still here or was deleted since, so a repeated import adds nothing, and nothing is matched by title.
--
-- Where a record lands is the terminal's to say, and these functions check it as 043's do: a memory and a project land in
-- the importing person's private scope, or in Family when the owner's reviewed scope map says so (D-049; the export does
-- not say which workspace a memory was in), always with the importing person as author (the export names none); a
-- private one is refused while memory.scopes is off (D-048), and a Family one only lands for a member (043's
-- memory_import_refusal, the rule the record is changed by once it is here). A link joins two memories here of one scope,
-- its source end the importing person's to write, its type one of upstream's eight with 051's episodic rule for decided
-- and follows; relates_to is stored smaller id first (D-191); its verification comes from upstream's provenance (explicit
-- verified, system inferred, inferred suggested; LinkTypes.Verification) and its confidence from upstream's weight. A
-- link has no maker here and is marked imported (043); its checked time is the import's, so the link pruning pass (056)
-- counts its grace from the import as upstream counts seven days unchanged. A project of the same slug in that scope
-- here before this export takes the memories that name it and is left as it is (043's join); never one this export
-- added (two projects of one file are never joined by a name, D-123), and never a held one (046), which is refused.
-- A new project is screened as an AI client's is (D-037; 045 screens a new project's slug, hyphens read as spaces): one
-- whose slug, name, description or an alias reads like instructions comes in held (046), kept out of every list, and a
-- memory that comes into a held project is held with it, reason held_project, as 045 holds an AI client's write that
-- names one, so only a person's release lists it.
--
-- A memory keeps its text, its ordinary tags, its kind, status, volatility, importance (upstream's 0, never classified,
-- as none), capsule slot (never for a memory held here: a slot the screening's hold leaves is kept beside it with the
-- upstream tags, memory.md section 9), validity window and dates; its hold, which the terminal maps onto one of Rhythm's
-- reasons (D-120: every quarantine: tag holds, reasons Rhythm has no hold of its own for as instruction); and its capture
-- source (upstream's system, its own jobs', as second-brain: Rhythm's system source is its own digests', 054). The tags in a
-- namespace Rhythm's engine sets itself, which no write here takes (033's memory_reserved_tags), are kept beside it as
-- data (D-121): memory_upstream_tags below, which nothing reads yet. Of those, upstream's commitment tags also make the
-- memory's commitment (058's memory_commitments, as rhythm import brings one): owed-to-me owed to the person (in), else
-- a counterparty: tag one the person owes (out), with that tag's name as 058's slug, none when it reads like instructions
-- to an AI client; so an imported open loop is listed the way upstream counted it. Every memory is screened by 036's trigger as any
-- write is, so a held one stays held and one upstream served is held here when its words read like instructions to an AI
-- client (D-037). Its history starts at the import: one audit row, imported, as the operator's, saying that earlier
-- changes were not recorded (VER-11), since the export carries no versions and no audit trail.
--
-- Nothing changes for an install that never runs the import: no tool, route or job calls these functions, nothing reads
-- memory_upstream_tags, and memory.secondBrainImport is off by default. The number is provisional.

-- The tags of an imported memory in a namespace the engine sets itself (D-121: standing instructions, decisions,
-- confidences, commitments and counterparties, staleness and retraction marks, upstream's pipeline markers, and each
-- quarantine: reason as the file gave it), kept as data beside the memory, under the origin they came from. Deleted with
-- the memory. Like the memory tables, it changes only through its functions (checked_tables()).
CREATE TABLE memory_upstream_tags(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 origin text NOT NULL CHECK(origin ~ '^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9._:-]{1,200}$'),
 tags text[] NOT NULL CHECK(cardinality(tags) BETWEEN 1 AND 64 AND array_position(tags,NULL) IS NULL)
);
REVOKE ALL ON memory_upstream_tags FROM PUBLIC;

-- The list of checked tables in force, with the upstream tags added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_upstream_tags']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- An origin and the export it came in, as the terminal names them: {"origin","export","exportedAt"}.
CREATE FUNCTION memory_import_origin_valid(p_origin jsonb) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
 SELECT jsonb_typeof(p_origin)='object' AND jsonb_typeof(p_origin->'origin')='string' AND jsonb_typeof(p_origin->'export')='string'
   AND jsonb_typeof(p_origin->'exportedAt')='string' AND (p_origin->>'origin') ~ '^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9._:-]{1,200}$'
$$;

-- What the origin map holds of this origin's record of p_kind by its id there, or nothing (every column null).
CREATE FUNCTION memory_import_origin_row(p_origin jsonb,p_kind text,p_source text) RETURNS memory_origins LANGUAGE sql STABLE AS $$
 SELECT o.* FROM memory_origins o WHERE o.origin=p_origin->>'origin' AND o.kind=p_kind AND o.source_id=p_source
$$;

-- The origin map's row for what an import of another system added: its id there (p_source) and its new id here.
CREATE FUNCTION memory_import_origin_add(p_origin jsonb,p_kind text,p_source text,p_id uuid,p_subject text) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_origins(origin,kind,source_id,local_id,subject_id,export_id,exported_at)
 VALUES(p_origin->>'origin',p_kind,p_source,p_id,p_subject,(p_origin->>'export')::uuid,(p_origin->>'exportedAt')::timestamptz)
$$;

-- import_second_brain_project: one project of a Second Brain export ({"key","slug","name","description","status",
-- "aliases","createdAt","updatedAt"}: key its id in the origin map, slug the one it takes here), for p_person into their
-- private scope, or Family with p_family. Answers its outcome: added (with its new id, its scope, how many aliases
-- another project of that scope answers to already, left out, and whether it came in held); imported_before (the origin
-- map has it: with its id and subject here while it is still here); joined (a project of that scope that was here before
-- this export answers to its slug, so the memories that name it go into that one, which is left as it is); or refused,
-- with the reason (memory_import_refusal's; held_project for a held project of that name, which nothing joins; slug_taken
-- for a name a project this export added answers to; invalid). As in 043, the author lock applies only to a project this
-- import would add.
CREATE FUNCTION import_second_brain_project(p_person text,p_family boolean,p_origin jsonb,p_project jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_subject text; v_slug text; why text; o memory_origins; known memory_projects; v_id uuid;
 aliases text[]; kept text[]; v_created timestamptz; v_name text; v_description text; v_held boolean;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF p_person IS NULL OR p_family IS NULL OR NOT memory_import_origin_valid(p_origin) OR jsonb_typeof(p_project) IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_project->'key') IS DISTINCT FROM 'string' OR jsonb_typeof(p_project->'slug') IS DISTINCT FROM 'string' THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_subject:=CASE WHEN p_family THEN 'household' ELSE p_person END;
   v_slug:=p_project->>'slug';
   why:=memory_import_refusal(p_person,NOT p_family,p_family,v_subject,p_person,scoped);
   -- Forbidden by the author lock alone (the reader reads this scope): only adding the project is refused, below.
   IF why IS NOT NULL AND NOT (why='forbidden' AND memory_import_reads(p_person,v_subject,scoped)) THEN
     RETURN jsonb_build_object('outcome','refused','reason',why);
   END IF;
   o:=memory_import_origin_row(p_origin,'project',p_project->>'key');
   IF o.local_id IS NOT NULL THEN
     RETURN jsonb_build_object('outcome','imported_before')||coalesce((SELECT jsonb_build_object('id',p.id,'subject',p.subject_id)
       FROM memory_projects p WHERE p.id=o.local_id),'{}'::jsonb);
   END IF;
   IF v_slug !~ '^[a-z0-9][a-z0-9-]{0,63}$' THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   PERFORM memory_project_lock(v_subject);
   -- A project that was here before this export (one this export added is never joined by a name, D-123).
   SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=v_subject
     AND (p.slug=v_slug OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=v_slug))
     AND NOT EXISTS(SELECT 1 FROM memory_origins x WHERE x.origin=p_origin->>'origin' AND x.kind='project' AND x.local_id=p.id
       AND x.export_id=(p_origin->>'export')::uuid)
     ORDER BY p.slug=v_slug DESC LIMIT 1;
   -- A held project (046) is joined by nothing: an import's memory in it would list it to every AI client (D-037).
   IF FOUND AND known.held THEN RETURN jsonb_build_object('outcome','refused','reason','held_project'); END IF;
   IF FOUND THEN RETURN jsonb_build_object('outcome','joined','id',known.id,'subject',known.subject_id); END IF;
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   IF NOT memory_project_free(v_subject,v_slug,NULL) THEN RETURN jsonb_build_object('outcome','refused','reason','slug_taken'); END IF;
   aliases:=memory_project_aliases_checked(memory_strings(coalesce(p_project->'aliases','[]'::jsonb)),v_slug);
   kept:=ARRAY(SELECT a FROM unnest(aliases) a WHERE memory_project_free(v_subject,a,NULL) ORDER BY a);
   v_name:=memory_project_name(p_project->>'name');
   v_description:=memory_project_description(p_project->>'description');
   -- Screened as an AI client's new project is (D-037, 045): each of its names and its description on its own.
   v_held:=EXISTS(SELECT 1 FROM unnest(ARRAY[replace(v_slug,'-',' '),v_name,v_description]||ARRAY(SELECT replace(a,'-',' ') FROM unnest(kept) a)) x
     WHERE memory_screen(x)->>'reason' IS NOT NULL);
   v_id:=gen_random_uuid();
   v_created:=coalesce((p_project->>'createdAt')::timestamptz,(p_origin->>'exportedAt')::timestamptz);
   INSERT INTO memory_projects(id,subject_id,slug,name,description,status,author_id,created_by,revision,created_at,updated_at,held)
   VALUES(v_id,v_subject,v_slug,v_name,v_description,
     coalesce(p_project->>'status','active'),p_person,NULL,1,v_created,greatest(v_created,coalesce((p_project->>'updatedAt')::timestamptz,v_created)),v_held);
   INSERT INTO memory_project_aliases(project_id,subject_id,alias) SELECT v_id,v_subject,a FROM unnest(kept) a;
   INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(v_id,v_subject,1,'imported',NULL,'cli','rhythm import-second-brain',
     jsonb_build_object('origin',p_origin->>'origin','export',p_origin->>'export','slug',v_slug,
       'aliases_left_out',cardinality(aliases)-cardinality(kept),'held',v_held));
   PERFORM memory_import_origin_add(p_origin,'project',p_project->>'key',v_id,v_subject);
   RETURN jsonb_build_object('outcome','added','id',v_id,'subject',v_subject,'aliasesLeftOut',cardinality(aliases)-cardinality(kept),'held',v_held);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

-- import_second_brain_memory: one memory of a Second Brain export ({"key","content","tags","upstream","kind","status",
-- "importance","volatility","source","project","capsule","capsuleSlot","hold","createdAt","updatedAt","validFrom",
-- "validUntil"}: key its id in the origin map, tags the ordinary ones, upstream the ones in a namespace the engine sets
-- itself, project a project of its scope here or none), for p_person into their private scope, or Family with p_family.
-- Answers its outcome: added (with its new id, its scope, its hold here, whether Rhythm held it though the file did not:
-- 036's screening, or a held project, and whether a capsule slot it named was left because it is held); imported_before
-- (the origin map has it: with its id and subject here while it is still here, in or out of the trash); or refused, with
-- the reason (memory_import_refusal's, project for a project not of its scope here, invalid: text, tags, dates or a
-- window Rhythm does not take).
CREATE FUNCTION import_second_brain_memory(p_person text,p_family boolean,p_origin jsonb,p_memory jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_subject text; v_scope text; why text; o memory_origins; v_project uuid; v_id uuid; m memories;
 upstream text[]; v_project_held boolean:=false; v_hold text; dropped boolean:=false; v_name text; v_counterparty text;
 v_commitment jsonb;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF p_person IS NULL OR p_family IS NULL OR NOT memory_import_origin_valid(p_origin) OR jsonb_typeof(p_memory) IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_memory->'key') IS DISTINCT FROM 'string' OR jsonb_typeof(p_memory->'content') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_memory->'source') IS DISTINCT FROM 'string' OR jsonb_typeof(p_memory->'createdAt') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_memory->'project','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_memory->'importance','null'::jsonb)) NOT IN ('number','null') THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_subject:=CASE WHEN p_family THEN 'household' ELSE p_person END;
   v_scope:=CASE WHEN p_family THEN 'shared' ELSE 'private' END;
   why:=memory_import_refusal(p_person,NOT p_family,p_family,v_subject,p_person,scoped);
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   o:=memory_import_origin_row(p_origin,'memory',p_memory->>'key');
   IF o.local_id IS NOT NULL THEN
     RETURN jsonb_build_object('outcome','imported_before')||coalesce((SELECT jsonb_build_object('id',x.id,'subject',x.subject_id)
       FROM memories x WHERE x.id=o.local_id),'{}'::jsonb);
   END IF;
   IF p_memory->>'project' IS NOT NULL THEN
     v_project:=(p_memory->>'project')::uuid;
     SELECT p.held INTO v_project_held FROM memory_projects p WHERE p.id=v_project AND p.subject_id=v_subject;
     IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused','reason','project'); END IF;
   END IF;
   -- Into a held project (046) only held, reason held_project, as 045 holds an AI client's write that names one: a
   -- memory not held there would list the project (046's memory_event_projects) before any person saw it (D-037).
   v_hold:=coalesce(p_memory->>'hold',CASE WHEN v_project_held THEN 'held_project' END);
   upstream:=memory_strings(coalesce(p_memory->'upstream','[]'::jsonb));
   IF cardinality(upstream)>64 OR EXISTS(SELECT 1 FROM unnest(upstream) t WHERE t<>btrim(t) OR t<>lower(t) OR length(t) NOT BETWEEN 1 AND 128
       OR t ~ '[[:cntrl:]]' OR NOT memory_reserved_tag(t))
     OR cardinality(upstream)<>(SELECT count(DISTINCT t) FROM unnest(upstream) t) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=gen_random_uuid();
   INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
   VALUES(v_id,v_subject,'memory',v_id::text,'memory',v_scope,'Memory','','current','memory');
   INSERT INTO memories(id,subject_id,scope,author_id,created_by,content,tags,kind,status,importance,volatility,source,project_id,
     capsule,capsule_slot,hold,hold_score,valid_from,valid_until,revision,created_at,updated_at)
   VALUES(v_id,v_subject,v_scope,p_person,NULL,memory_check_text(p_memory->>'content'),
     memory_check_tags(memory_strings(coalesce(p_memory->'tags','[]'::jsonb))),p_memory->>'kind',coalesce(p_memory->>'status','draft'),
     (p_memory->>'importance')::smallint,p_memory->>'volatility',p_memory->>'source',v_project,p_memory->>'capsule',
     p_memory->>'capsuleSlot',v_hold,CASE WHEN p_memory->>'hold' IS NULL AND v_project_held THEN 1.0 END,
     (p_memory->>'validFrom')::timestamptz,(p_memory->>'validUntil')::timestamptz,1,
     (p_memory->>'createdAt')::timestamptz,coalesce((p_memory->>'updatedAt')::timestamptz,(p_memory->>'createdAt')::timestamptz))
   RETURNING * INTO m;
   -- A capsule slot only for a memory not held (memory.md section 9; a slot is a person's step, 039): one 036's screening
   -- held as its row went in leaves the slot, its capsule tags kept beside it with the other upstream tags.
   IF m.hold IS NOT NULL AND m.capsule IS NOT NULL THEN
     IF cardinality(upstream)<=62 THEN
       upstream:=upstream||ARRAY(SELECT t FROM unnest(ARRAY['capsule:'||m.capsule,'capsule-slot:'||m.capsule_slot]) t
         WHERE t IS NOT NULL AND NOT t=ANY(upstream));
     END IF;
     UPDATE memories SET capsule=NULL,capsule_slot=NULL WHERE id=v_id RETURNING * INTO m;
     dropped:=true;
   END IF;
   IF cardinality(upstream)>0 THEN
     INSERT INTO memory_upstream_tags(memory_id,origin,tags) VALUES(v_id,p_origin->>'origin',upstream);
   END IF;
   -- Its commitment from upstream's tags (058), the name screened as remember_memory_commitment screens one.
   IF 'owed-to-me'=ANY(upstream) OR EXISTS(SELECT 1 FROM unnest(upstream) t WHERE starts_with(t,'counterparty:')) THEN
     v_name:=(SELECT substr(t,14) FROM unnest(upstream) t WHERE starts_with(t,'counterparty:') ORDER BY t LIMIT 1);
     v_counterparty:=CASE WHEN length(btrim(v_name)) BETWEEN 1 AND 64 AND v_name !~ '[[:cntrl:]]' THEN memory_counterparty(v_name) END;
     IF memory_screen(translate(v_counterparty,'-_','  '))->>'reason' IS NOT NULL THEN v_counterparty:=NULL; END IF;
     v_commitment:=jsonb_build_object('direction',CASE WHEN 'owed-to-me'=ANY(upstream) THEN 'in' ELSE 'out' END,'counterparty',v_counterparty);
     INSERT INTO memory_commitments(memory_id,direction,counterparty) VALUES(v_id,v_commitment->>'direction',v_counterparty);
   END IF;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(v_id,v_subject,1,'imported',NULL,'cli','rhythm import-second-brain',jsonb_build_object('origin',p_origin->>'origin',
     'export',p_origin->>'export','earlier','not_recorded','upstream_tags',cardinality(upstream)));
   PERFORM memory_import_origin_add(p_origin,'memory',p_memory->>'key',v_id,v_subject);
   -- 036's screening may have held it as its row went in.
   SELECT * INTO m FROM memories WHERE id=v_id;
   RETURN jsonb_build_object('outcome','added','id',v_id,'subject',v_subject,'hold',m.hold,
     'heldHere',m.hold IS NOT NULL AND p_memory->>'hold' IS NULL,'slotDropped',dropped,'commitment',v_commitment);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

-- import_second_brain_link: one edge of a Second Brain export as a made link ({"key","from","to","type","verification",
-- "confidence","createdAt"}: key its id in the origin map, from and to the memories here its ends came in as), for
-- p_person. Answers its outcome: added (with its id and scope); present (a link of that type between the two is here
-- already); imported_before (the origin map has it); or refused, with the reason (ends: not two memories here out of the
-- trash; scopes: two memories of different scopes, which never link; kinds: decided or follows between memories that are
-- not both episodic; memory_import_refusal's for its source end; invalid).
CREATE FUNCTION import_second_brain_link(p_person text,p_origin jsonb,p_link jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; s memories; t memories; x memories; directed boolean; why text; o memory_origins; v_id uuid;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF p_person IS NULL OR NOT memory_import_origin_valid(p_origin) OR jsonb_typeof(p_link) IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_link->'key') IS DISTINCT FROM 'string' OR jsonb_typeof(p_link->'type') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_link->'from') IS DISTINCT FROM 'string' OR jsonb_typeof(p_link->'to') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_link->'createdAt') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_link->'confidence','null'::jsonb)) NOT IN ('number','null') THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   o:=memory_import_origin_row(p_origin,'link',p_link->>'key');
   IF o.local_id IS NOT NULL THEN RETURN jsonb_build_object('outcome','imported_before'); END IF;
   SELECT y.directed INTO directed FROM link_types() y WHERE y.name=p_link->>'type' AND y.upstream;
   IF directed IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   PERFORM memory_link_lock((p_link->>'from')::uuid,(p_link->>'to')::uuid);
   SELECT * INTO s FROM memories y WHERE y.id=(p_link->>'from')::uuid;
   SELECT * INTO t FROM memories y WHERE y.id=(p_link->>'to')::uuid;
   IF s.id IS NULL OR t.id IS NULL OR s.id=t.id OR EXISTS(SELECT 1 FROM memory_trash y WHERE y.memory_id IN (s.id,t.id)) THEN
     RETURN jsonb_build_object('outcome','refused','reason','ends');
   END IF;
   IF s.subject_id<>t.subject_id THEN RETURN jsonb_build_object('outcome','refused','reason','scopes'); END IF;
   why:=memory_import_refusal(p_person,s.subject_id<>'household',s.subject_id='household',s.subject_id,s.author_id,scoped);
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   IF NOT link_kinds_allow(p_link->>'type',s.kind,t.kind) THEN RETURN jsonb_build_object('outcome','refused','reason','kinds'); END IF;
   -- A type with no direction is stored with the smaller id first (052, D-191).
   IF NOT directed AND s.id>t.id THEN x:=s; s:=t; t:=x; END IF;
   IF EXISTS(SELECT 1 FROM made_links l WHERE l.source_id=s.id AND l.target_id=t.id AND l.type=p_link->>'type') THEN
     RETURN jsonb_build_object('outcome','present');
   END IF;
   v_id:=gen_random_uuid();
   -- Checked now: the pruning pass's grace (056) counts from the import, never from upstream's creation time.
   INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported,confidence,checked_at)
   VALUES(v_id,s.id,t.id,p_link->>'type',p_link->>'verification','','active',NULL,(p_link->>'createdAt')::timestamptz,true,
     (p_link->>'confidence')::double precision,now());
   PERFORM memory_import_origin_add(p_origin,'link',p_link->>'key',v_id,s.subject_id);
   RETURN jsonb_build_object('outcome','added','id',v_id,'subject',s.subject_id);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list of checked tables gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_origin_valid(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_origin_row(jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_import_origin_add(jsonb,text,text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_second_brain_project(text,boolean,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_second_brain_memory(text,boolean,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_second_brain_link(text,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_import_origin_valid(jsonb),memory_import_origin_row(jsonb,text,text),
 memory_import_origin_add(jsonb,text,text,uuid,text),import_second_brain_project(text,boolean,jsonb,jsonb),
 import_second_brain_memory(text,boolean,jsonb,jsonb),import_second_brain_link(text,jsonb,jsonb) FROM PUBLIC;
