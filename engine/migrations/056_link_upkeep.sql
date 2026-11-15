-- Upkeep of the links drawn between native memories (SB-P4-04 in docs/dev/second-brain-parity/backlog.md; D-015, D-017,
-- D-067; docs/dev/memory.md sections 8 and 9; ledger rows JOB-03, GRA-05 and GRA-07). Adapted from Second Brain's graph
-- pass (src/graph/pass.ts at the pinned commit, MIT; NOTICE): an automatic link under a weight of 0.3 is pruned once it
-- has gone 7 days unchanged, a link someone stated is never deleted by the pruning pass or any other upkeep job (it
-- goes only with its end, when delete forever or the trash's daily purge removes that memory, 038), and one whose end is
-- gone is reported instead (its GET /stats/graph?deep=1 counts them).
--
-- 1. A changed source invalidates what was drawn from it (a trigger for the new link store: 009's invalidation sees
--    only the map's own links and only a change of a catalog row, never the words of a native memory, which live in
--    memories). When a memory's words change (update, append, undo):
--    - each automatic made link at either end (verification suggested: one no person or AI client stated, which only an
--      import writes today, SB-P4-03's links on save later) is out of date: lifecycle stale, its evidence (an excerpt
--      an import may have brought) gone, its checked time now, so its grace period counts from here. No read serves a
--      stale link (the map, recall's hops and connections read active ones only), and the nightly pass prunes it once
--      its grace period has passed. Nothing draws an automatic link again until SB-P4-03 (carried forward, D-203), so an
--      imported one is gone for good 7 days after either end's words change (D-200). One waiting in the trash row of
--      its other end (038 keeps a forgotten memory's links there) is marked the same way, so a restore brings it back
--      out of date rather than as it was drawn;
--    - the map's own links at the memory's star rows that were drawn from its words (every type but in_project, which
--      is drawn from its project and tags) go stale with their evidence and details gone, as 009 does for a catalog
--      row, so the map, recall's hops and connections stop serving them at once; the next rebuild draws them again from
--      the new words. Each current star row is written again unchanged first, so a rebuild that had saved the star from
--      the old words before this change finds the row changed under it when it comes to write its links, and writes
--      none from them. One gap, as 048's: a rebuild that read the old words before the change but saves the star only
--      after it can still write links drawn from them, served until the rebuild after it. The star itself stays: its
--      text is read from the memory at each read, so an edit shows at once, and its title is drawn again at the next
--      rebuild.
--    When a memory's kind changes, each automatic made link at either end whose type the episodic rule (051) no longer
--    allows goes stale the same way.
--    A link someone stated (verified, a person's; inferred, an AI client's; a supersede's of any verification, 049's
--    record of a replacement) is never touched: it is the person's or the client's statement, not something drawn from
--    the words. Every lock here is taken SKIP LOCKED, so a change never waits on a rebuild, a trash or another change; a
--    link or row another transaction holds right then (a forget moving it into the trash, a restore bringing it back, a
--    rebuild writing it) is left as that transaction leaves it. The map's next rebuild draws its own links again; an
--    automatic made link so missed (it was moving through the trash at that moment, and comes back active with an
--    excerpt of the old words) is found by the nightly pass's re-check (3 below), which marks it out of date when it
--    reaches the link: until then, at most a day or a few on a scope with many links, it is served as it was.
-- 2. A link someone stated is broken while an end is in the trash (a restore brought the link back while the other end
--    waits there), marked wrong, held for review or no longer of the kinds its type joins (made_link_broken, the one
--    definition). It is kept as it is, and the nightly pass counts it, so the night summary and the engine's log report
--    it; a person sees the end's state where they look (connections names a neighbour marked wrong or held, and the
--    trash list counts the links a forgotten memory keeps). A link whose end is deleted for good goes with it (038).
-- 3. The nightly pass (Core/MemoryLinkPruning.cs, a job made only with memory.tools on, after the staleness pass) walks
--    each scope's made links in id order from that scope's cursor (050's job_cursors), a bounded number a run, and
--    deletes each automatic one past its grace period (7 days since it was last checked, or made) that is weak (a
--    confidence under 0.3) or no longer active (stale, retired, revoked or deleted), upstream's prune. Before that it
--    re-checks each active automatic link the change above may have missed: one with an end whose words changed after
--    it was last checked (made_link_words_changed: an append, or an update or undo naming content, in that end's audit
--    trail), or whose type the episodic rule no longer allows between its ends' kinds, is marked out of date as the
--    trigger marks one, so its grace period starts then. A link with no
--    confidence is not known to be weak and stays while it is active. A link with an end in the trash waits for it: the
--    trash keeps links, so a restore brings every one back. Both ends' own rows are locked FOR SHARE SKIP LOCKED before
--    a link goes, the order every change takes, so a memory a writer holds right now keeps its links until the next
--    run. A pruned link writes no audit row and leaves no dismissal (it was no one's judgement); a link of its type
--    made afterwards is a new one. Upstream's weekly sweep of links with a missing end has nothing to find here:
--    made_links' ends are keys (D-067), so no link outlives its end.
--
-- With memory.tools off (off by default) no tool or app view changes a memory's words or kind and the pass is no job,
-- so nothing here runs; with no made link and no map star for a memory, a change of its words writes nothing. The map's
-- answers on an install with every setting off are as before. The number is provisional.

-- Upstream's grace period and weight (EDGE_PRUNE_MIN_AGE_MS, EDGE_PRUNE_WEIGHT): fixed here, not settings.
CREATE FUNCTION made_link_grace() RETURNS interval LANGUAGE sql IMMUTABLE AS $$ SELECT interval '7 days' $$;
CREATE FUNCTION made_link_weak() RETURNS double precision LANGUAGE sql IMMUTABLE AS $$ SELECT 0.3::double precision $$;

-- Whether the made link e (a made_links row as JSON: one in made_links, or one a trash row keeps) is an automatic link
-- a change of p_memory leaves out of date: active, suggested, with p_memory at an end, and, unless its words changed
-- (p_words), of a type the episodic rule no longer allows between its two ends' kinds now.
CREATE FUNCTION memory_link_outdated(e jsonb,p_memory uuid,p_words boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT e->>'verification'='suggested' AND e->>'lifecycle'='active' AND e->>'type'<>'supersedes'
   AND p_memory::text IN (e->>'source_id',e->>'target_id')
   AND (p_words OR NOT link_kinds_allow(e->>'type',(SELECT k.kind FROM memories k WHERE k.id=(e->>'source_id')::uuid),
     (SELECT k.kind FROM memories k WHERE k.id=(e->>'target_id')::uuid)))
$$;

-- Why the made link l, one someone stated, is broken now, or null: trash (an end in the trash), unavailable (an end's
-- own row no longer current otherwise), retracted (an end marked wrong), held (an end held for review) or kinds (the
-- episodic rule no longer allows its type between its ends). Null for an automatic link, a supersede's (049's cascade
-- keeps it as the record of a replacement) and one not active.
CREATE FUNCTION made_link_broken(l made_links) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE
   WHEN l.verification='suggested' OR l.type='supersedes' OR l.lifecycle<>'active' THEN NULL
   WHEN EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id IN (l.source_id,l.target_id)) THEN 'trash'
   WHEN (SELECT count(*) FROM resources r WHERE r.id IN (l.source_id,l.target_id) AND r.provider='memory'
     AND r.relationship_snapshot IS NULL AND r.status='current')<2 THEN 'unavailable'
   WHEN EXISTS(SELECT 1 FROM memories m WHERE m.id IN (l.source_id,l.target_id) AND m.status='deprecated') THEN 'retracted'
   WHEN EXISTS(SELECT 1 FROM memories m WHERE m.id IN (l.source_id,l.target_id) AND m.hold IS NOT NULL) THEN 'held'
   WHEN NOT link_kinds_allow(l.type,(SELECT m.kind FROM memories m WHERE m.id=l.source_id),
     (SELECT m.kind FROM memories m WHERE m.id=l.target_id)) THEN 'kinds'
 END
$$;

-- Whether an end of the made link l had its words changed after l was last checked (or made): an append, or an update
-- or an undo whose changed fields name content (033's and 041's audit rows). The change's own row is written in the
-- transaction that invalidated l, at the same moment as its checked time, so a link the trigger did reach is not found.
CREATE FUNCTION made_link_words_changed(l made_links) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id IN (l.source_id,l.target_id) AND e.at>coalesce(l.checked_at,l.created_at)
   AND (e.event='appended' OR e.event IN ('updated','reverted') AND e.detail->'fields' ? 'content'))
$$;

-- Whether the nightly pass marks the made link l out of date now, as the trigger would have: active and automatic (not
-- a supersede's), with an end whose words changed since it was last checked, or of a type the episodic rule no longer
-- allows between its ends' kinds.
CREATE FUNCTION made_link_missed(l made_links) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT l.verification='suggested' AND l.lifecycle='active' AND l.type<>'supersedes'
   AND (made_link_words_changed(l) OR NOT link_kinds_allow(l.type,(SELECT m.kind FROM memories m WHERE m.id=l.source_id),
     (SELECT m.kind FROM memories m WHERE m.id=l.target_id)))
$$;

-- Whether the nightly pass deletes the made link l now: automatic (not a supersede's), past its grace period since it
-- was last checked (or made), and weak or no longer active; never while an end is in the trash.
CREATE FUNCTION made_link_prunable(l made_links) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT l.verification='suggested' AND l.type<>'supersedes' AND coalesce(l.checked_at,l.created_at)<=now()-made_link_grace()
   AND (l.lifecycle<>'active' OR coalesce(l.confidence<made_link_weak(),false))
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id IN (l.source_id,l.target_id))
$$;

-- The invalidation (1 above), after a change of a memory's words or kind, in the change's own transaction.
CREATE FUNCTION invalidate_memory_links() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE words boolean:=NEW.content IS DISTINCT FROM OLD.content;
BEGIN
 UPDATE made_links l SET lifecycle='stale',evidence='',checked_at=now()
 WHERE l.link_id IN (SELECT x.link_id FROM made_links x WHERE (x.source_id=NEW.id OR x.target_id=NEW.id)
   AND memory_link_outdated(to_jsonb(x),NEW.id,words) FOR UPDATE OF x SKIP LOCKED);
 UPDATE memory_trash t SET links=(SELECT jsonb_agg(CASE WHEN memory_link_outdated(y.e,NEW.id,words)
     THEN y.e||jsonb_build_object('lifecycle','stale','evidence','','checked_at',now()) ELSE y.e END ORDER BY y.o)
   FROM jsonb_array_elements(t.links) WITH ORDINALITY y(e,o))
 WHERE t.memory_id IN (SELECT x.memory_id FROM memory_trash x WHERE x.memory_id<>NEW.id
   AND EXISTS(SELECT 1 FROM jsonb_array_elements(x.links) e WHERE memory_link_outdated(e,NEW.id,words)) FOR UPDATE OF x SKIP LOCKED);
 IF words THEN
   UPDATE resources r SET indexed_at=r.indexed_at
   WHERE r.id IN (SELECT x.id FROM resources x WHERE x.provider='memory' AND x.relationship_snapshot IS NOT NULL
     AND x.external_id=memory_star_external_id(NEW.id) AND x.status='current' FOR UPDATE OF x SKIP LOCKED);
   UPDATE relationships l SET lifecycle='stale',evidence='',details='{}'::jsonb
   WHERE (l.source_id,l.target_id,l.type) IN (SELECT x.source_id,x.target_id,x.type FROM relationships x
     JOIN resources star ON star.id IN (x.source_id,x.target_id) AND star.provider='memory' AND star.relationship_snapshot IS NOT NULL
       AND star.external_id=memory_star_external_id(NEW.id)
     WHERE x.engine='cross-source-v1' AND x.lifecycle='active' AND x.type<>'in_project' FOR UPDATE OF x SKIP LOCKED);
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER memory_links_invalidation AFTER UPDATE OF content,kind ON memories
 FOR EACH ROW WHEN (OLD.content IS DISTINCT FROM NEW.content OR OLD.kind IS DISTINCT FROM NEW.kind)
 EXECUTE FUNCTION invalidate_memory_links();

-- prune_made_links: one piece of the nightly pass (3 above) over p_subject's made links (a link's scope is its ends',
-- one scope, read from its source end), the next p_limit (1 to 1000) after p_after in id order (none: from the first).
-- Each automatic one made_link_missed finds out of date is marked so (its row locked SKIP LOCKED: one a writer holds
-- right now is left for the next run); each one made_link_prunable says may go is deleted once both ends' own rows are
-- locked (a link whose end a writer holds right now is left for the next run); each one someone stated is counted when
-- made_link_broken says it is broken, and kept. Answers how many it looked at, marked out of date, pruned, counted
-- broken and left, the last id it looked at, and whether it took a full piece (more may follow).
CREATE FUNCTION prune_made_links(p_subject text,p_after uuid,p_limit integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE x made_links; inspected integer:=0; pruned integer:=0; broken integer:=0; held integer:=0; outdated integer:=0; last uuid;
 ends integer; gone integer;
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN
   RAISE EXCEPTION 'A link pruning pass takes 1 to 1000 links at a time' USING ERRCODE='22023';
 END IF;
 IF p_subject IS NULL OR NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=p_subject) THEN
   RAISE EXCEPTION 'A link pruning pass names a scope that exists' USING ERRCODE='22023';
 END IF;
 FOR x IN SELECT l.* FROM made_links l JOIN memories s ON s.id=l.source_id
   WHERE s.subject_id=p_subject AND (p_after IS NULL OR l.link_id>p_after) ORDER BY l.link_id LIMIT p_limit
 LOOP
   inspected:=inspected+1;
   last:=x.link_id;
   IF made_link_broken(x) IS NOT NULL THEN broken:=broken+1; END IF;
   IF made_link_missed(x) THEN
     UPDATE made_links l SET lifecycle='stale',evidence='',checked_at=now()
     WHERE l.link_id IN (SELECT y.link_id FROM made_links y WHERE y.link_id=x.link_id AND made_link_missed(y) FOR UPDATE OF y SKIP LOCKED);
     GET DIAGNOSTICS gone=ROW_COUNT;
     IF gone=0 THEN held:=held+1; END IF;
     outdated:=outdated+gone;
     CONTINUE;
   END IF;
   CONTINUE WHEN NOT made_link_prunable(x);
   SELECT count(*) INTO ends FROM (SELECT 1 FROM resources r WHERE r.id IN (x.source_id,x.target_id) AND r.provider='memory'
     AND r.relationship_snapshot IS NULL ORDER BY r.id FOR SHARE SKIP LOCKED) k;
   IF ends<2 THEN held:=held+1; CONTINUE; END IF;
   DELETE FROM made_links l WHERE l.link_id=x.link_id AND made_link_prunable(l);
   GET DIAGNOSTICS gone=ROW_COUNT;
   pruned:=pruned+gone;
 END LOOP;
 RETURN jsonb_build_object('inspected',inspected,'outdated',outdated,'pruned',pruned,'broken',broken,'left',held,'last',last,
   'more',inspected=p_limit);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION made_link_grace() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION made_link_weak() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_link_outdated(jsonb,uuid,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION made_link_broken(made_links) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION made_link_words_changed(made_links) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION made_link_missed(made_links) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION made_link_prunable(made_links) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION invalidate_memory_links() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION prune_made_links(text,uuid,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION made_link_grace(),made_link_weak(),memory_link_outdated(jsonb,uuid,boolean),made_link_broken(made_links),
 made_link_words_changed(made_links),made_link_missed(made_links),made_link_prunable(made_links),invalidate_memory_links(),
 prune_made_links(text,uuid,integer) FROM PUBLIC;
