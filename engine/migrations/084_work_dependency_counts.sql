-- How far the work an item waits on has come (memory.work, off by default; docs/dev/memory.md section 5; migration 080).
-- A milestone, one work item that waits through depends_on links on each piece of a step of a project's roadmap, can now
-- say how many of its pieces are done. 080's memory_work_waiting lists only the unfinished items one waits on, so a
-- finished piece dropped out of every list and nothing could count it.
--
-- 1. What an item depends on (memory_work_depends, the one definition): the targets of its active depends_on links in its
--    own scope that are work items listed now (079's memory_work_listed: live, not marked wrong, their window open),
--    whatever their state. It is 080's rule for what an item waits on, with the finished items kept.
-- 2. What it waits on (memory_work_waiting) is now read from it: those neither done nor cancelled, as before. It is
--    replaced with the same argument and answer, so every list and the blocked filter read as they did.
--
-- Nothing is stored and no table changes: list_work and a memories feed count an item's dependencies by state at each
-- read (dependencies: total, done, cancelled). An engine from before this migration runs on it unchanged, reading
-- memory_work_waiting as it did.

CREATE FUNCTION memory_work_depends(p_memory uuid) RETURNS TABLE(id uuid,work_type text,state text,priority smallint,created_at timestamptz)
LANGUAGE sql STABLE AS $$
 SELECT w.memory_id,w.work_type,w.state,w.priority,t.created_at
 FROM made_links l JOIN memories s ON s.id=l.source_id JOIN memories t ON t.id=l.target_id JOIN memory_work w ON w.memory_id=t.id
 WHERE l.source_id=p_memory AND l.type='depends_on' AND l.lifecycle='active' AND t.subject_id=s.subject_id
   AND memory_work_listed(t.id)
$$;

CREATE OR REPLACE FUNCTION memory_work_waiting(p_memory uuid) RETURNS TABLE(id uuid,work_type text,state text,priority smallint,created_at timestamptz)
LANGUAGE sql STABLE AS $$
 SELECT d.id,d.work_type,d.state,d.priority,d.created_at FROM memory_work_depends(p_memory) d WHERE d.state NOT IN ('done','cancelled')
$$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION memory_work_depends(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_waiting(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_work_depends(uuid),memory_work_waiting(uuid) FROM PUBLIC;
