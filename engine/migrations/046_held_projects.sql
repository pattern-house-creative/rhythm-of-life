-- A held write's new project kept out of the registry's lists (SB-P3-21 in docs/dev/second-brain-parity/backlog.md;
-- D-160, D-177; docs/dev/memory.md section 7.1). A memory write that names a new project makes it (033's
-- memory_project, as 035 left it), and before this migration it was listed at once to every reader of its scope
-- (list_projects, list_recent's unknown-project answer, a capsule's, the map) though the write's memory was held: so a
-- planted instruction's project slug, which a held memory's own answers leave out (D-149), still reached every AI
-- client of the scope. Now a project a memory write makes is held with it: kept, but out of every list the engine
-- serves (Core/MemoryProjects.cs, Capsules.cs, the map's MemoryStars and RelationshipCatalog), until a memory in it is
-- not held. That is decided when the write's audit row goes in, after 045's channel hold has had its say:
--   - the write's memory is not held: its project lists, as before this migration;
--   - it is held: the project stays held, for that memory (held_for), until a person releases it (D-039) or a write
--     that is not held names the project or puts a memory in it; then it lists as any other. A write through a channel
--     scored as an AI client's that names a project a held write made is itself held (045, reason held_project), the
--     planted memory's own after a release elsewhere included, so only a person's step lists such a project. A release
--     lists only the project the released state is in, which the person reviewing it saw (a release vouches for nothing
--     else, D-161); one the memory left while it was held stays held, as a project of a memory deleted for good does.
-- The slug a write through such a channel gives a new project is screened too (045): one that reads like instructions,
-- hyphens read as spaces, holds the write, reason instruction, so its project stays held with it. So a planted
-- instruction's slug reaches no AI client whether the instruction is in the memory's words or in the slug itself.
-- A person who names a held project's slug for a new project, or as an alias, is refused in words that say a memory
-- held for review named it and how it lists, rather than only that another project answers to it, since no list shows
-- them that one.
-- A project made any other way (create_memory_project, a person's step; an import, the operator's) is listed as
-- before, an import's too when the memory it brings into it is held (the operator's step, D-178), unless the export
-- carried the project's hold (SB-P3-23, 043), and no project held when its memory is deleted for good is listed again
-- by that. No install holds a project a held write made: no tool wrote a memory before run 11's memory tools, behind
-- memory.tools (off by default, D-177), so every project already there stays listed and every existing answer is as
-- before. The number is provisional.

ALTER TABLE memory_projects ADD COLUMN held boolean NOT NULL DEFAULT false;
-- The memory whose held write made it (no key: the project stays held after that memory is deleted for good).
ALTER TABLE memory_projects ADD COLUMN held_for uuid;
ALTER TABLE memory_projects ADD CONSTRAINT memory_projects_held_for CHECK(held OR held_for IS NULL);
CREATE INDEX memory_projects_held ON memory_projects(held_for) WHERE held;

-- 035's memory_project, the project a memory write names, found or made in the memory's own scope, with the project it
-- makes held until the write's audit row says whether its memory is (memory_event_projects). Otherwise as before.
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
 INSERT INTO memory_projects(id,subject_id,slug,name,author_id,created_by,held)
 VALUES(made,p_subject,p_slug,p_slug,p_person,p_principal,true) ON CONFLICT(subject_id,slug) DO NOTHING;
 IF NOT FOUND THEN
   SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=p_subject AND p.slug=p_slug FOR KEY SHARE OF p;
   RETURN jsonb_build_object('id',known.id,'slug',known.slug,'created',false);
 END IF;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(made,p_subject,1,'created',p_principal,p_channel,p_client,jsonb_build_object('slug',p_slug));
 RETURN jsonb_build_object('id',made,'slug',p_slug,'created',true);
END $$;

-- A memory's change, as its audit row goes in: a memory not held lists the project it is in; a held one keeps a project
-- made for it held, naming it. Postgres fires a table's BEFORE triggers in name order,
-- so 045's memory_events_channel_hold has decided the hold first. A row of a memory deleted for good finds none.
CREATE FUNCTION memory_event_projects() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_hold text; v_project uuid;
BEGIN
 IF NEW.memory_id IS NULL THEN RETURN NEW; END IF;
 SELECT m.hold,m.project_id INTO v_hold,v_project FROM memories m WHERE m.id=NEW.memory_id;
 IF NOT FOUND THEN RETURN NEW; END IF;
 IF v_hold IS NULL THEN
   UPDATE memory_projects p SET held=false,held_for=NULL WHERE p.held AND p.id=v_project;
 ELSIF v_project IS NOT NULL THEN
   UPDATE memory_projects p SET held_for=NEW.memory_id WHERE p.id=v_project AND p.held AND p.held_for IS NULL;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memory_events_projects BEFORE INSERT ON memory_events FOR EACH ROW EXECUTE FUNCTION memory_event_projects();

-- 035's memory_project_claim, which refuses a name another project of p_subject already answers to, before anything is
-- written; a held project's name refused in words that say why no list shows it. Otherwise as before.
CREATE OR REPLACE FUNCTION memory_project_claim(p_subject text,p_slug text,p_aliases text[],p_project uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE taken text;
BEGIN
 SELECT n INTO taken FROM unnest(array_remove(ARRAY[p_slug],NULL)||coalesce(p_aliases,'{}')) n
   WHERE NOT memory_project_free(p_subject,n,p_project) ORDER BY n IS DISTINCT FROM p_slug,n LIMIT 1;
 IF taken IS NULL THEN RETURN; END IF;
 IF EXISTS(SELECT 1 FROM memory_projects p WHERE p.subject_id=p_subject AND p.held AND p.held_for IS NOT NULL AND p.id IS DISTINCT FROM p_project
     AND (p.slug=taken OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=taken))) THEN
   RAISE EXCEPTION 'A memory held for review already named a project %; it lists once a person releases a memory in it, or writes one into it from the terminal',taken
     USING ERRCODE='23505',CONSTRAINT='memory_project_names';
 END IF;
 RAISE EXCEPTION 'Another project in this scope already answers to %',taken USING ERRCODE='23505',CONSTRAINT='memory_project_names';
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_project(uuid,text,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_projects() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project_claim(text,text,text[],uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_project(uuid,text,text,text,text,text),memory_event_projects(),memory_project_claim(text,text,text[],uuid) FROM PUBLIC;
