-- Staleness for native memories (SB-P4-09 in docs/dev/second-brain-parity/backlog.md; story 12; ledger rows TIM-05,
-- TIM-06, JOB-04, API-29 and API-30; docs/dev/memory.md sections 5 and 9): how soon a fact may go out of date (its
-- volatility), a bounded nightly pass that finds the facts whose time has come, the stale queue as one predicate that
-- every count and list reads, and "still true", which a person or an AI client uses to confirm a memory without
-- changing it. Adapted from Second Brain's staleness pass, stale queue and still_true (src/staleness/pass.ts,
-- src/memory/stale.ts and src/memory/actions.ts at the pinned commit, MIT; NOTICE).
--
-- Volatility is the caller's word (025's column; D-033: no model or heuristic sets it). A volatile fact may be out of
-- date 14 days after it was last changed or confirmed, a state fact 90 days after (upstream's defaults); a durable
-- fact, and one with no volatility, never by age. A memory saved without one has none (none is set for it here), so no
-- memory becomes stale for want of a volatility; one its caller gave one ages from its last change. The clock is the
-- newer of the memory's last change (updated_at, which every change moves) and its last confirmation; a retraction flag
-- (049) moves neither.
--
-- The pass (Core/MemoryStaleness.cs, a nightly job made only with memory.tools on) walks each scope's memories in id
-- order from that scope's cursor (050's job_cursors), a bounded number a run, and marks each one whose time has passed
-- (memory_checks: when it found it, at which revision, since when); a mark stands only while the memory is at that
-- revision, so any change since takes it out of the queue, and the pass takes off a mark that no longer holds. It writes
-- nothing for a memory that is not due, so an install with no native memory, or none due, keeps no row here. It writes
-- no audit row and moves no revision: a mark is no change of the memory.
--
-- The stale queue (memory_stale_reason, the one definition): a live, current memory (not deprecated, not held, not in
-- the trash, its window open) that the pass marked at its current revision (not_confirmed), or that a retracted source
-- flagged (049; retracted_source, which wins, as upstream). Every count and list reads this function under the caller's
-- read predicate, so a count is the length of its list.
--
-- Still true (confirm_memory): whoever may change the memory (033's author lock) says it still stands. Its checked time
-- moves to now, its mark and its retraction flags come off, and one audit row (confirmed) says so, naming why it was in
-- the queue; no revision moves and no version is kept or pruned, so every version stays and an expected revision others
-- read stays good. 041's own-changes rule leaves confirmed rows out, as 049 leaves its flag rows out. A memory held for
-- review, marked wrong or whose window has closed is refused; one confirmed at this revision in the last day and not in
-- the queue answers unchanged, so a client confirming in a loop writes one row a day.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision; 22023 invalid input and, with a hint
-- the engine reads, held (a memory held for review) and retracted (a deprecated memory). The number is provisional.

-- The pass's marks and the last confirmation, one row a memory, gone with it when it is deleted for good.
CREATE TABLE memory_checks(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 -- When a person or an AI client last said it is still true, at which revision.
 confirmed_at timestamptz,
 confirmed_revision integer CHECK(confirmed_revision>=1),
 -- When the pass found it out of date, at the revision it had then, and the clock it read: since when it had gone
 -- unchanged and unconfirmed.
 stale_at timestamptz,
 stale_revision integer CHECK(stale_revision>=1),
 stale_since timestamptz,
 CHECK((confirmed_at IS NULL)=(confirmed_revision IS NULL)),
 CHECK((stale_at IS NULL)=(stale_revision IS NULL) AND (stale_at IS NULL)=(stale_since IS NULL))
);
REVOKE ALL ON memory_checks FROM PUBLIC;

-- The list of checked tables in force, with the checks added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_checks']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- How long a fact of this volatility goes unchanged and unconfirmed before it may be out of date; none for durable and
-- for no volatility.
CREATE FUNCTION memory_stale_after(p_volatility text) RETURNS interval LANGUAGE sql IMMUTABLE AS $$
 SELECT CASE p_volatility WHEN 'volatile' THEN interval '14 days' WHEN 'state' THEN interval '90 days' END
$$;

-- Why a memory is in the stale queue now, or null: the one definition every count and list reads.
CREATE FUNCTION memory_stale_reason(p_memory uuid) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE
   WHEN m.status='deprecated' OR m.hold IS NOT NULL OR m.valid_until<=now()
     OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
     OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
       AND r.status='current') THEN NULL
   WHEN EXISTS(SELECT 1 FROM memory_retraction_flags f LEFT JOIN memories s ON s.id=f.retracted_id
     WHERE f.memory_id=m.id AND (s.id IS NULL OR s.subject_id=m.subject_id)) THEN 'retracted_source'
   WHEN c.stale_revision=m.revision THEN 'not_confirmed' END
 FROM memories m LEFT JOIN memory_checks c ON c.memory_id=m.id WHERE m.id=p_memory
$$;

-- Since when a memory has gone unchanged and unconfirmed: the newer of its last change and its last confirmation.
CREATE FUNCTION memory_stale_clock(p_memory uuid) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT greatest(m.updated_at,c.confirmed_at) FROM memories m LEFT JOIN memory_checks c ON c.memory_id=m.id WHERE m.id=p_memory
$$;

-- memory_staleness_pass: one piece of the nightly pass over p_subject's memories, the next p_limit (1 to 1000) after
-- p_after in id order (none: from the first). Each one due (live, current, its volatility's time passed since its clock)
-- and not marked at its revision is marked; each marked one that is no longer due has its mark taken off. A mark is
-- written once the memory's row is locked (FOR KEY SHARE, before its memory_checks row, the order every writer takes
-- them in, so a still true running at the same time is waited for rather than deadlocked with), and only if no
-- confirmation came meanwhile (compare and set on the confirmation the piece read), so a person's "still true" is never
-- overwritten by a pass that read before it. Answers how many it looked at, marked and cleared, the last id it looked
-- at, and whether it took a full piece (more may follow).
CREATE FUNCTION memory_staleness_pass(p_subject text,p_after uuid,p_limit integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE x record; inspected integer:=0; marked integer:=0; cleared integer:=0; last uuid; due boolean; changed integer;
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN
   RAISE EXCEPTION 'A staleness pass takes 1 to 1000 memories at a time' USING ERRCODE='22023';
 END IF;
 IF p_subject IS NULL OR NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=p_subject) THEN
   RAISE EXCEPTION 'A staleness pass names a scope that exists' USING ERRCODE='22023';
 END IF;
 FOR x IN SELECT m.id,m.revision,m.status,m.hold,m.volatility,m.valid_until,greatest(m.updated_at,c.confirmed_at) AS clock,
     c.confirmed_at,c.stale_revision,
     EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) AS trashed,
     EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current') AS listed
   FROM memories m LEFT JOIN memory_checks c ON c.memory_id=m.id
   WHERE m.subject_id=p_subject AND (p_after IS NULL OR m.id>p_after)
   ORDER BY m.id LIMIT p_limit
 LOOP
   inspected:=inspected+1;
   last:=x.id;
   due:=x.listed AND NOT x.trashed AND x.status<>'deprecated' AND x.hold IS NULL
     AND (x.valid_until IS NULL OR x.valid_until>now())
     AND coalesce(x.clock+memory_stale_after(x.volatility)<=now(),false);
   IF due AND x.stale_revision IS DISTINCT FROM x.revision THEN
     PERFORM 1 FROM memories WHERE id=x.id FOR KEY SHARE;
     INSERT INTO memory_checks(memory_id,stale_at,stale_revision,stale_since) VALUES(x.id,now(),x.revision,x.clock)
     ON CONFLICT(memory_id) DO UPDATE SET stale_at=EXCLUDED.stale_at,stale_revision=EXCLUDED.stale_revision,stale_since=EXCLUDED.stale_since
     WHERE memory_checks.confirmed_at IS NOT DISTINCT FROM x.confirmed_at;
     GET DIAGNOSTICS changed=ROW_COUNT;
     marked:=marked+changed;
   ELSIF NOT due AND x.stale_revision IS NOT NULL THEN
     UPDATE memory_checks SET stale_at=NULL,stale_revision=NULL,stale_since=NULL
     WHERE memory_id=x.id AND stale_revision=x.stale_revision;
     GET DIAGNOSTICS changed=ROW_COUNT;
     cleared:=cleared+changed;
   END IF;
 END LOOP;
 RETURN jsonb_build_object('inspected',inspected,'marked',marked,'cleared',cleared,'last',last,'more',inspected=p_limit);
END $$;

-- confirm_memory: still true. Whoever may change p_memory says it still stands, without changing it: its checked time
-- moves to now, its stale mark and its retraction flags come off, and one audit row (confirmed) names why it was in the
-- queue (was: not_confirmed, retracted_source or none) and the flags' sources. No revision moves and no version is kept.
CREATE FUNCTION confirm_memory(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; c memory_checks; was text; cleared uuid[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is confirmed only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is not confirmed as true: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF m.valid_until<=now() THEN
   RAISE EXCEPTION 'This memory stopped being true at %: it is history, not a fact to confirm (clear valid_until, or undo, if it is true again)',
     to_char(m.valid_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 was:=memory_stale_reason(m.id);
 SELECT * INTO c FROM memory_checks WHERE memory_id=m.id FOR UPDATE;
 IF was IS NULL AND c.confirmed_revision=m.revision AND c.confirmed_at>now()-interval '1 day' THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true,'checked_at',c.confirmed_at,'was',NULL,'retracted','[]'::jsonb);
 END IF;
 INSERT INTO memory_checks(memory_id,confirmed_at,confirmed_revision) VALUES(m.id,now(),m.revision)
 ON CONFLICT(memory_id) DO UPDATE SET confirmed_at=EXCLUDED.confirmed_at,confirmed_revision=EXCLUDED.confirmed_revision,
   stale_at=NULL,stale_revision=NULL,stale_since=NULL;
 WITH gone AS (DELETE FROM memory_retraction_flags f WHERE f.memory_id=m.id RETURNING f.retracted_id)
 SELECT coalesce(array_agg(g.retracted_id ORDER BY g.retracted_id),'{}') INTO cleared FROM gone g;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'confirmed',p_principal,channel,p_client,
   jsonb_build_object('was',was,'previous',c.confirmed_at,'retracted',to_jsonb(cleared)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('changed',jsonb_build_array('checked'),'checked_at',now(),'was',was,
   'retracted',to_jsonb(cleared));
END $$;

-- 049's memory_own_changes, with confirmed rows left out beside the cascade's flag rows: a confirmation moves no revision
-- and changes nothing of the memory an undo puts back, so another member's "still true" never stops a member from
-- undoing their own newest change. Every revision in the range still needs a row of its own, and any other row by
-- someone else still counts against it.
CREATE OR REPLACE FUNCTION memory_own_changes(p_principal uuid,p_memory uuid,p_subject text,p_after integer,p_current integer,p_scoped boolean)
RETURNS boolean LANGUAGE plpgsql STABLE AS $$
DECLARE person text; covered integer; others boolean;
BEGIN
 IF NOT p_scoped OR p_subject<>'household' OR p_after>=p_current OR NOT reads_subject(p_principal,p_subject) THEN RETURN false; END IF;
 SELECT c.person_id INTO person FROM principals c WHERE c.id=p_principal;
 IF person IS NULL THEN RETURN false; END IF;
 SELECT count(DISTINCT e.revision),bool_or(q.person_id IS DISTINCT FROM person) INTO covered,others
 FROM memory_events e LEFT JOIN principals q ON q.id=e.principal_id
 WHERE e.memory_id=p_memory AND e.revision>p_after AND e.revision<=p_current
   AND e.event NOT IN ('flagged','unflagged','flags_cleared','confirmed');
 RETURN covered=p_current-p_after AND NOT coalesce(others,true);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list and the replaced rule get their search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_stale_after(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_stale_reason(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_stale_clock(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_staleness_pass(text,uuid,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION confirm_memory(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_own_changes(uuid,uuid,text,integer,integer,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_stale_after(text),memory_stale_reason(uuid),memory_stale_clock(uuid),
 memory_staleness_pass(text,uuid,integer),confirm_memory(uuid,uuid,integer,text,text),
 memory_own_changes(uuid,uuid,text,integer,integer,boolean) FROM PUBLIC;
