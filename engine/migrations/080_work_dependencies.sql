-- Dependencies between work items (memory.work, off by default; docs/dev/memory.md section 5; migration 079): one work
-- item that cannot start until another is done says so with a link of a new type, depends_on, from the item that waits to
-- the item it waits on. Whether an item is blocked is never stored: it is read from its links whenever it is listed.
--
-- 1. The vocabulary. link_types() (051) gains depends_on, directed, joining memories of any kind, and Rhythm's own, not
--    Second Brain's. The engine's LinkTypes.All and the app's linkTypes list the same, and tests hold the three together.
-- 2. The writer. link_memories (052, as 057 replaced it) takes one more argument, p_work, which the engine passes true
--    only while memory.work is on, as 078 gave set_memory_capsule p_clients; every call without it is answered as
--    before, and depends_on is refused. With it, a depends_on link is made as any link is (both ends read by the caller
--    and in one scope, the source end the caller's to change, neither held, an AI client's inferred and a person's
--    verified), and two more things are checked:
--    - both ends are work items now (079's memory_work): a dependency is between two pieces of work;
--    - it closes no loop: a link from A to B is refused when B already waits, through one or more active depends_on
--      links, on A, so no set of items waits on itself. The scope's dependencies are taken under one advisory lock
--      first, so two links made at the same moment cannot close a loop between them.
--    The function is dropped and made again because it takes one more argument; the engine is its only caller.
-- 3. What an item waits on (memory_work_waiting, the one definition every list reads): the targets of its active
--    depends_on links in its own scope that are work items listed now (079's memory_work_listed: live, not marked wrong,
--    their window open) and neither done nor cancelled. A dependency held for review still holds the work up, though its
--    words wait for a person; one in the trash, marked wrong, ended, made no longer a work item (an undo) or finished
--    holds nothing up. An item is blocked when it is open itself (neither done nor cancelled) and waits on any. A link an
--    import brought in counts as one made here; one that joins a memory that is no work item counts for nothing.
--
-- A link changes no memory (052): making or removing a dependency moves no revision and writes no audit row, so it is
-- in no memory's history and no undo reverses it; unlink removes it, by its maker or whoever may change the waiting item.
-- 038's trash keeps a trashed item's links and brings them back with it; 043's export and import carry them as every
-- made link; and the map draws them as every made link, with memory.tools on.
--
-- Nothing calls these but the memory tool link (and the app's link) with depends_on, which the engine passes only with
-- memory.work on beside memory.tools, and list_work, made only then: with none called no install holds a depends_on link
-- but an import's, every link_memories call answers as it did, and every other answer is as before.
--
-- Errors, as 052's: 42501 not available to this login, or not this caller's to link; P0002 an unknown memory, also one the
-- caller does not read or one in the trash; 22023 invalid input (also depends_on without memory.work, an end that is no
-- work item, and a link that would close a loop) and, with the hint held, a memory held for review. A refused call writes
-- nothing.

-- 051's link_types(), with depends_on last.
CREATE OR REPLACE FUNCTION link_types() RETURNS TABLE(name text,directed boolean,kinds text[],upstream boolean)
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
 VALUES
  ('relates_to',false,NULL::text[],true),
  ('supersedes',true,NULL,true),
  ('caused_by',true,NULL,true),
  ('decided',true,ARRAY['episodic'],true),
  ('about_person',true,NULL,true),
  ('part_of_project',true,NULL,true),
  ('follows',true,ARRAY['episodic'],true),
  ('drawn_from',true,NULL,true),
  ('references',true,NULL,false),
  ('in_project',true,NULL,false),
  ('in_folder',true,NULL,false),
  ('about',false,NULL,false),
  ('similar_to',false,NULL,false),
  ('copy_of',false,NULL,false),
  ('contains',true,NULL,false),
  ('depends_on',true,NULL,false)
$$;

-- The work items p_memory waits on now: the one definition every list reads.
CREATE FUNCTION memory_work_waiting(p_memory uuid) RETURNS TABLE(id uuid,work_type text,state text,priority smallint,created_at timestamptz)
LANGUAGE sql STABLE AS $$
 SELECT w.memory_id,w.work_type,w.state,w.priority,t.created_at
 FROM made_links l JOIN memories s ON s.id=l.source_id JOIN memories t ON t.id=l.target_id JOIN memory_work w ON w.memory_id=t.id
 WHERE l.source_id=p_memory AND l.type='depends_on' AND l.lifecycle='active' AND t.subject_id=s.subject_id
   AND w.state NOT IN ('done','cancelled') AND memory_work_listed(t.id)
$$;

-- Whether p_from already waits on p_to through one or more active depends_on links (a link from p_to to p_from would
-- then close a loop). UNION keeps each item once, so a loop an import brought in still ends the walk.
CREATE FUNCTION memory_work_reaches(p_from uuid,p_to uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 WITH RECURSIVE r(id) AS (
   SELECT p_from
   UNION
   SELECT l.target_id FROM made_links l JOIN r ON l.source_id=r.id WHERE l.type='depends_on' AND l.lifecycle='active')
 SELECT EXISTS(SELECT 1 FROM r WHERE r.id=p_to AND p_from<>p_to)
$$;

DROP FUNCTION link_memories(uuid,uuid,uuid,text,double precision,text,text);

-- 057's link_memories, with p_work: depends_on only with it, between two work items, closing no loop. Every other line as
-- 057 wrote it.
CREATE FUNCTION link_memories(p_principal uuid,p_source uuid,p_target uuid,p_type text,p_confidence double precision,
 p_channel text,p_client text DEFAULT NULL,p_work boolean DEFAULT false) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; s memories; t memories; directed boolean; v_from uuid; v_to uuid; l made_links;
 v_verification text; rank_new integer; rank_old integer; created boolean:=false;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Linking memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_source IS NULL OR p_target IS NULL OR p_source=p_target THEN
   RAISE EXCEPTION 'A link joins two different memories' USING ERRCODE='22023';
 END IF;
 IF p_type='supersedes' THEN
   RAISE EXCEPTION 'A newer memory replacing an earlier one is supersede''s: it ends the earlier one too' USING ERRCODE='22023';
 END IF;
 IF p_type='depends_on' AND NOT coalesce(p_work,false) THEN
   RAISE EXCEPTION 'depends_on joins two work items, and work items are off in this install (memory.work)' USING ERRCODE='22023';
 END IF;
 SELECT x.directed INTO directed FROM link_types() x WHERE x.name=p_type AND (x.upstream OR x.name='depends_on' AND coalesce(p_work,false));
 IF directed IS NULL THEN
   RAISE EXCEPTION 'A link''s type is relates_to, caused_by, decided, about_person, part_of_project, follows or drawn_from%',
     CASE WHEN coalesce(p_work,false) THEN ', or depends_on between two work items' ELSE '' END USING ERRCODE='22023';
 END IF;
 IF p_confidence IS NOT NULL AND NOT (p_confidence BETWEEN 0 AND 1) THEN
   RAISE EXCEPTION 'A confidence is a number from 0 to 1' USING ERRCODE='22023';
 END IF;
 PERFORM memory_link_lock(p_source,p_target);
 s:=memory_link_end(p_principal,p_source);
 t:=memory_link_end(p_principal,p_target);
 IF s.subject_id<>t.subject_id THEN
   RAISE EXCEPTION 'Two memories in different scopes never link: both ends of a link are in one scope' USING ERRCODE='22023';
 END IF;
 IF NOT memory_may_change(p_principal,s.subject_id,s.author_id,scoped) THEN
   RAISE EXCEPTION 'Only the memory''s owner, its author or a Family admin may link from it' USING ERRCODE='42501';
 END IF;
 IF s.hold IS NOT NULL OR t.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is linked only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF NOT link_kinds_allow(p_type,s.kind,t.kind) THEN
   RAISE EXCEPTION '% joins two episodic memories only (kind episodic on both)',p_type USING ERRCODE='22023';
 END IF;
 IF p_type='depends_on' THEN
   IF NOT EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=s.id) OR NOT EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=t.id) THEN
     RAISE EXCEPTION 'depends_on joins two work items: make each one a work item first (create_work with its id)' USING ERRCODE='22023';
   END IF;
   -- The scope's dependencies, one link at a time, so two made at the same moment never close a loop between them.
   PERFORM pg_advisory_xact_lock(hashtextextended('memory-depends:'||s.subject_id,0));
 END IF;
 IF NOT directed AND s.id>t.id THEN v_from:=t.id; v_to:=s.id; ELSE v_from:=s.id; v_to:=t.id; END IF;
 v_verification:=CASE WHEN channel='mcp' THEN 'inferred' ELSE 'verified' END;
 SELECT * INTO l FROM made_links x WHERE x.source_id=v_from AND x.target_id=v_to AND x.type=p_type FOR UPDATE;
 IF p_type='depends_on' AND (l.link_id IS NULL OR l.lifecycle<>'active') AND memory_work_reaches(t.id,s.id) THEN
   RAISE EXCEPTION 'That would close a loop: the item it would wait on already waits, directly or through others, on this one. Nothing was linked'
     USING ERRCODE='22023';
 END IF;
 IF l.link_id IS NULL THEN
   INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,confidence,source_revision,checked_at)
   VALUES(gen_random_uuid(),v_from,v_to,p_type,v_verification,'','active',p_principal,p_confidence,
     'memory:'||(CASE WHEN v_from=s.id THEN s.revision ELSE t.revision END),now())
   ON CONFLICT(source_id,target_id,type) DO NOTHING
   RETURNING * INTO l;
   IF l.link_id IS NOT NULL AND channel<>'mcp' THEN
     -- Made now by a person: a dismissal of this type between the two is taken back. An AI client's leaves it.
     DELETE FROM memory_link_dismissals d WHERE d.low_id=least(s.id,t.id) AND d.high_id=greatest(s.id,t.id) AND d.type=p_type;
   END IF;
   IF l.link_id IS NULL THEN
     -- Made by a call that ran at the same time: there already, as that call left it.
     SELECT * INTO l FROM made_links x WHERE x.source_id=v_from AND x.target_id=v_to AND x.type=p_type;
     RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('id',l.link_id,'type',l.type,'from',l.source_id,'to',l.target_id,
       'directed',directed,'verification',l.verification,'created',false,'unchanged',true);
   END IF;
   created:=true;
 ELSIF l.lifecycle<>'active' THEN
   -- A link in another lifecycle (an import's stale, retired, revoked or deleted one) that the caller states now: made
   -- anew, by the caller, and, by a person, a dismissal of its type between the two taken back.
   UPDATE made_links SET lifecycle='active',verification=v_verification,evidence='',made_by=p_principal,created_at=now(),imported=false,
     confidence=p_confidence,source_revision='memory:'||(CASE WHEN v_from=s.id THEN s.revision ELSE t.revision END),checked_at=now()
   WHERE link_id=l.link_id RETURNING * INTO l;
   IF channel<>'mcp' THEN
     DELETE FROM memory_link_dismissals d WHERE d.low_id=least(s.id,t.id) AND d.high_id=greatest(s.id,t.id) AND d.type=p_type;
   END IF;
   created:=true;
 ELSE
   -- There already: a person's link confirms an AI client's or a suggestion, an AI client's confirms a suggestion; any
   -- other answers unchanged.
   rank_new:=CASE v_verification WHEN 'verified' THEN 3 WHEN 'inferred' THEN 2 ELSE 1 END;
   rank_old:=CASE l.verification WHEN 'verified' THEN 3 WHEN 'inferred' THEN 2 ELSE 1 END;
   IF rank_new<=rank_old THEN
     RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('id',l.link_id,'type',l.type,'from',l.source_id,'to',l.target_id,
       'directed',directed,'verification',l.verification,'created',false,'unchanged',true);
   END IF;
   UPDATE made_links SET verification=v_verification,checked_at=now() WHERE link_id=l.link_id RETURNING * INTO l;
 END IF;
 RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('id',l.link_id,'type',l.type,'from',l.source_id,'to',l.target_id,
   'directed',directed,'verification',l.verification,'created',created,'unchanged',false);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so each replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION link_types() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_waiting(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_reaches(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION link_memories(uuid,uuid,uuid,text,double precision,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION link_types(),memory_work_waiting(uuid),memory_work_reaches(uuid,uuid),
 link_memories(uuid,uuid,uuid,text,double precision,text,text,boolean) FROM PUBLIC;
