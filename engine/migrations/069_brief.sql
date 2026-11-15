-- The brief (SB-P5-08 in docs/dev/second-brain-parity/backlog.md; story 12; docs/dev/memory.md sections 5 and 7.1), which
-- settles one due definition (D-214). The brief itself is a read of the queues as they stand and adds no table: its due
-- dates are 061's memory_due, its decisions 060's memory_decision_listed, its loops 058's memory_open_loop, its stale
-- memories 053's memory_stale_reason, its review 063's and 068's lists, its insights 066's stream view and its standing
-- instructions 065's rows, each read by the statement that makes its list, so each count is the length of its list.
--
-- One due definition. A memory has at most one date that can come due: a decision's is its review date (review_by), and
-- any other memory's is its due date (when, or the one date its words name). So a decision still takes no due date (D-214's
-- refusals stand) and is never on the due list, whatever a version kept by hand holds: memory_due says so here. One rule
-- reads that date for both, 061's memory_due_window: overdue (its moment has come), upcoming (within the next 48 hours) or
-- later. A decision's list due is that rule's overdue, as it always read, and its new list upcoming is that rule's
-- upcoming, so the brief asks the same two windows of each and a count is the length of its list.
--
-- Provisional, as 060 and 061 are: neither has reached an install, and with memory.tools off nothing reads them.

-- 061's memory_due, with one line added: a decision's date is its review date, so it is never on the due list.
CREATE OR REPLACE FUNCTION memory_due(p_memory uuid) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT m.due_at FROM memories m
 WHERE m.id=p_memory AND m.due_at IS NOT NULL AND NOT 'task:done'=ANY(m.tags)
   AND m.status<>'deprecated' AND m.hold IS NULL AND (m.valid_until IS NULL OR m.valid_until>now())
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=m.id)
   AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
     AND r.status='current')
$$;

-- 060's memory_decision_listed: due reads the one window rule, and upcoming is new. Otherwise as 060 wrote it.
CREATE OR REPLACE FUNCTION memory_decision_listed(p_memory uuid,p_list text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((
   SELECT CASE p_list
     WHEN 'scored' THEN s.outcome IN ('right','wrong','mixed') AND d.confidence IS NOT NULL
     WHEN 'open' THEN c.current AND s.outcome IS NULL
     WHEN 'due' THEN c.current AND memory_due_window(s.review_at)='overdue'
     WHEN 'upcoming' THEN c.current AND memory_due_window(s.review_at)='upcoming'
     WHEN 'resolved' THEN c.current AND s.outcome IS NOT NULL
     WHEN 'all' THEN c.current
   END
   FROM memories m JOIN memory_decisions d ON d.memory_id=m.id
   CROSS JOIN LATERAL memory_decision_at(m.id,m.revision) s
   CROSS JOIN LATERAL (SELECT m.valid_until IS NULL OR m.valid_until>now()) c(current)
   WHERE m.id=p_memory AND m.status<>'deprecated' AND m.hold IS NULL
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
     AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
       AND r.status='current')),false)
$$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so both get their search path again.
 EXECUTE format('ALTER FUNCTION memory_due(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_decision_listed(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_due(uuid),memory_decision_listed(uuid,text) FROM PUBLIC;
