-- The decision ledger for native memories (SB-P5-11 in docs/dev/second-brain-parity/backlog.md; story 12; docs/dev/memory.md
-- sections 5 and 9): a decision the person made, with how sure they were and when to look at it again; how it turned out,
-- recorded as a change like any other (a version kept, the revision moved, one audit row, and undo puts it back); and
-- calibration, which compares how sure the person was with how things turned out, saying how many decisions each figure
-- rests on. Adapted from Second Brain's decision ledger (src/decisions/capture.ts, outcome.ts, queries.ts and
-- calibration.ts, and the outcome action in src/memory/actions.ts at the pinned commit, MIT; NOTICE). The arithmetic of
-- calibration is the engine's (Core/MemoryDecisions.cs); this file keeps the ledger and the one predicate it reads.
--
-- A decision is a memory remember wrote with decision true (remember_memory_decision): created as create_memory creates
-- one (033: a draft in the caller's scope by its landing rule, screened for a hold, 036 and 045, one audit row), with its
-- memory_decisions row in the same transaction. Upstream marks one with its reserved ledger:decision, confidence: and
-- confidence-source: tags, which 033 reserves; Rhythm keeps them in this row, set once and never versioned, as a
-- commitment's direction is (058): how sure the person was when they decided is what calibration compares, so no later
-- change moves it. Confidence, when the person gave or implied one, is upstream's: above 0 and at most 1, clamped to 0.05
-- to 0.95 and rounded to the nearest 0.05; its source stated (the person said it) or inferred (the client read it from
-- their words, the default). A decision is no commitment: the memory tools refuse both at once, as upstream does.
--
-- What changes is the decision's state: its outcome (none yet, right, wrong, mixed, or unknown: too early to tell), how
-- often unknown asked to look again (at most twice), and its review date (when to bring it up again; none once nothing is
-- left to ask). It is kept in memory_decision_states, one row for each revision at which it was set: the state at any
-- revision is the newest row at or before it (memory_decision_at), so every other change of the memory (an edit, a status,
-- a share) carries it on unchanged. record_memory_outcome writes a row at the revision it makes, and undo_memory (041's,
-- replaced below with that one addition) compares the state at the revision it goes back to with the state now, counts a
-- difference as a field it changed (outcome) and writes the state it brought back at its own revision. So an undo of an
-- outcome takes it back, and an undo of that undo brings it back again, with no change to memory_retire or the versions.
--
-- The review date is upstream's: review_by when the call gives it (a date, at 09:00 in the install's time zone; a date and
-- time, with an offset or in that zone; at most five years ahead), else 90 calendar days from today at 09:00 there. An
-- unknown outcome asks again 90 days on, twice; the third unknown stops asking. Upstream adds the 90 days to the moment;
-- here every review date is a calendar day at 09:00, as the first is.
--
-- The ledger's lists (memory_decision_listed, the one definition every count and list reads): a decision that is live (in
-- the catalog, not in the trash), not held for review and not marked wrong (deprecated), and of those open (no outcome
-- yet), due (its review date has come), resolved (an outcome recorded, unknown included) and all; each of those also asks
-- that its window is open, as upstream's log leaves a replaced decision out. scored is the list calibration rests on: an
-- outcome right, wrong or mixed and a confidence, in any window, since a decision replaced since was still a call the
-- person made (upstream's calibration reads replaced ones too); unknown outcomes and decisions with no confidence are
-- listed but never scored. Stricter than upstream, a held decision is in no list and never scored: it waits for a person.
--
-- Whose decisions a reader is served is the open loops' rule (058's memory_loop_mine): those it reads, may change (033's
-- author lock) and owns, so calibration is the person's own, never another member's, as upstream's actionable rule; with
-- memory.scopes off the household shares everything, every install's default, so every admitted login is served every
-- decision, as every loop. Recording an outcome is a change under the author lock (memory_for_change). It writes no words:
-- upstream appends a note in the same version; here a note is an append of its own, screened and counted toward a burst as
-- every content write is, so no free text reaches a memory past 036 and 045. A repeat of right, wrong or mixed (or a
-- fourth unknown, once asking stopped) answers unchanged and writes nothing; another unknown asks again, a change of its
-- own, so recording an outcome is not idempotent. A memory held for review or marked wrong is refused, as resolve_loop
-- refuses them; one whose window has ended takes an outcome, since calibration scores it.
--
-- An export and import keep a decision: rhythm export writes its confidence and its states from the reader's history floor
-- on (the state at the floor written at the floor), and import_memory_decision, called by rhythm import for each memory it
-- has just added, adds them there, at the revisions the import keeps.
--
-- Nothing calls these but the memory tools (remember's decision, list_decisions, record_outcome and decision_calibration),
-- made only with memory.tools on (off by default), rhythm import, undo_memory and tests: with none called no memory is a
-- decision, no audit row is an outcome, every undo compares the same fields with the same answers, and every answer is as
-- before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision; 23505 an idempotency key used for another
-- request; 54000 text over the limit; 22023 invalid input (also a memory that is no decision) and, with a hint the engine
-- reads, held (a memory held for review) and retracted (a deprecated memory). A refused call writes nothing. The number is
-- provisional.

-- A decision: how sure the person was, set once with the memory and never versioned. Gone with its memory when it is
-- deleted for good. No subject of its own: it is its memory's, wherever a share moves it.
CREATE TABLE memory_decisions(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 -- 0.05 to 0.95 in steps of 0.05; none when the person gave or implied none.
 confidence numeric(3,2) CHECK(confidence BETWEEN 0.05 AND 0.95 AND confidence*20=trunc(confidence*20)),
 -- stated: the person said it; inferred: read from their words.
 confidence_source text CHECK(confidence_source IN ('stated','inferred')),
 created_at timestamptz NOT NULL DEFAULT now(),
 CHECK((confidence IS NULL)=(confidence_source IS NULL))
);
REVOKE ALL ON memory_decisions FROM PUBLIC;

-- A decision's state from a revision on: its outcome, how often unknown asked again, and its review date. A row for each
-- revision at which it was set (the memory's writing, an outcome, an undo that changed it); never changed once written.
CREATE TABLE memory_decision_states(
 memory_id uuid NOT NULL REFERENCES memory_decisions ON DELETE CASCADE,
 revision integer NOT NULL CHECK(revision>=1),
 outcome text CHECK(outcome IN ('right','wrong','mixed','unknown')),
 rearms smallint NOT NULL DEFAULT 0 CHECK(rearms BETWEEN 0 AND 2),
 review_at timestamptz,
 recorded_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(memory_id,revision),
 -- Open: asked at its review date, never asked again yet.
 CHECK(outcome IS NOT NULL OR review_at IS NOT NULL AND rearms=0),
 -- Unknown: asked again once or twice, with a review date until the asking stops after the third (rearms 2, no date);
 -- right, wrong and mixed ask nothing more. A review date is a finite moment. So an import (060's import_memory_decision)
 -- stores no state the engine never writes.
 CHECK(outcome IS DISTINCT FROM 'unknown' OR rearms>=1),
 CHECK(outcome IS DISTINCT FROM 'unknown' OR review_at IS NOT NULL OR rearms=2),
 CHECK(outcome IS NULL OR outcome='unknown' OR review_at IS NULL AND rearms=0),
 CHECK(review_at IS NULL OR isfinite(review_at))
);
REVOKE ALL ON memory_decision_states FROM PUBLIC;

-- The list of checked tables in force, with the decisions added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_decisions','memory_decision_states']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- Upstream's DECISION_REVIEW_DEFAULT_DAYS: when to look at a decision again, and again after an unknown.
CREATE FUNCTION memory_decision_review_days() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 90 $$;

-- p_days calendar days from today in the install's time zone, at 09:00 there: calendar arithmetic, so a clock change
-- between now and then moves no day.
CREATE FUNCTION memory_review_default(p_days integer) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT ((install_today()+p_days)+time '09:00') AT TIME ZONE install_timezone()
$$;

-- A review date as a call gives it (upstream's review_by): none, the default; a date (YYYY-MM-DD), at 09:00 that day in the
-- install's time zone; a date and time (T or a space between), in that zone unless it ends with Z or an offset. At most
-- five years ahead (upstream's bound); a date already past is due at once.
CREATE FUNCTION memory_review_at(p_text text) RETURNS timestamptz LANGUAGE plpgsql STABLE AS $$
DECLARE t text:=btrim(coalesce(p_text,'')); at timestamptz;
BEGIN
 IF p_text IS NULL THEN RETURN memory_review_default(memory_decision_review_days()); END IF;
 BEGIN
   IF t ~ '^\d{4}-\d{2}-\d{2}$' THEN
     at:=(t::date+time '09:00') AT TIME ZONE install_timezone();
   ELSIF t ~ '^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,6})?)?$' THEN
     at:=replace(t,'T',' ')::timestamp AT TIME ZONE install_timezone();
   ELSIF t ~* '^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,6})?)?(Z|[+-]\d{2}(:?\d{2})?)$' THEN
     at:=t::timestamptz;
   END IF;
 EXCEPTION WHEN data_exception THEN at:=NULL;
 END;
 IF at IS NULL OR NOT isfinite(at) THEN
   RAISE EXCEPTION 'review_by is a date (YYYY-MM-DD: 09:00 that day in the install''s time zone) or a date and time (YYYY-MM-DDTHH:MM, with an offset or in that zone): nothing was saved'
     USING ERRCODE='22023';
 END IF;
 IF at>now()+interval '5 years' THEN
   RAISE EXCEPTION 'review_by is at most five years from now: nothing was saved' USING ERRCODE='22023';
 END IF;
 RETURN at;
END $$;

-- Upstream's confidence: above 0 and at most 1 (0.7 for 70%), clamped to 0.05 to 0.95 and rounded to the nearest 0.05
-- (a half up); none for none.
CREATE FUNCTION memory_confidence(p_raw double precision) RETURNS numeric LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_raw IS NULL THEN RETURN NULL; END IF;
 IF p_raw='NaN'::double precision OR NOT p_raw>0 OR p_raw>1 THEN
   RAISE EXCEPTION 'confidence is above 0 and at most 1, such as 0.7 for 70%%: nothing was saved' USING ERRCODE='22023';
 END IF;
 RETURN round(least(0.95,greatest(0.05,p_raw))::numeric*20)/20;
END $$;

-- A decision's state at revision p_revision: the newest row at or before it; every column null when it has none.
CREATE FUNCTION memory_decision_at(p_memory uuid,p_revision integer) RETURNS memory_decision_states LANGUAGE sql STABLE AS $$
 SELECT s.* FROM memory_decision_states s WHERE s.memory_id=p_memory AND s.revision<=p_revision ORDER BY s.revision DESC LIMIT 1
$$;

-- The one definition of the ledger's lists: whether p_memory, as it is now, is in the list p_list (open, due, resolved,
-- all or scored); false for a memory that is no decision, or not live (not in the catalog, in the trash), held for review
-- or marked wrong. open, due, resolved and all ask that its window is open; scored (what calibration rests on) asks an
-- outcome right, wrong or mixed and a confidence, in any window. Every count and list reads it.
CREATE FUNCTION memory_decision_listed(p_memory uuid,p_list text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((
   SELECT CASE p_list
     WHEN 'scored' THEN s.outcome IN ('right','wrong','mixed') AND d.confidence IS NOT NULL
     WHEN 'open' THEN c.current AND s.outcome IS NULL
     WHEN 'due' THEN c.current AND s.review_at<=now()
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

-- remember_memory_decision: a new memory from p_memory (create_memory's fields, MemoryInput's) that is a decision, with
-- p_decision's {confidence, confidenceSource, reviewBy} (each optional: a confidence as the call gives it, stated or
-- inferred, and the review date's words). Written as create_memory writes it, with its decision and its first state in
-- the same transaction. The key is the caller's, over the memory and the decision as asked (the review date's words, not
-- the date they make, so a retry tomorrow is the same request).
CREATE FUNCTION remember_memory_decision(p_principal uuid,p_memory jsonb,p_decision jsonb,p_channel text,p_client text DEFAULT NULL,
 p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE raw double precision; v_confidence numeric; v_source text; v_review timestamptz; asked jsonb; hash text; replay jsonb;
 result jsonb; v_id uuid;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 IF p_memory IS NULL OR jsonb_typeof(p_memory)<>'object' THEN
   RAISE EXCEPTION 'A memory takes content, tags, kind, importance, volatility, scope, project and source' USING ERRCODE='22023';
 END IF;
 IF jsonb_typeof(coalesce(p_decision,'{}'::jsonb))<>'object'
   OR jsonb_typeof(coalesce(p_decision->'confidence','null'::jsonb)) NOT IN ('number','null')
   OR jsonb_typeof(coalesce(p_decision->'confidenceSource','null'::jsonb)) NOT IN ('string','null')
   OR jsonb_typeof(coalesce(p_decision->'reviewBy','null'::jsonb)) NOT IN ('string','null') THEN
   RAISE EXCEPTION 'A decision takes a confidence, its source and a review date' USING ERRCODE='22023';
 END IF;
 raw:=(p_decision->>'confidence')::double precision;
 v_source:=p_decision->>'confidenceSource';
 IF v_source IS NOT NULL AND v_source NOT IN ('stated','inferred') THEN
   RAISE EXCEPTION 'confidence_source is stated or inferred: nothing was saved' USING ERRCODE='22023';
 END IF;
 IF v_source IS NOT NULL AND raw IS NULL THEN
   RAISE EXCEPTION 'confidence_source goes with a confidence: nothing was saved' USING ERRCODE='22023';
 END IF;
 v_confidence:=memory_confidence(raw);
 IF v_confidence IS NOT NULL THEN v_source:=coalesce(v_source,'inferred'); END IF;
 asked:=jsonb_build_object('confidence',v_confidence,'confidenceSource',v_source,'reviewBy',p_decision->>'reviewBy');
 hash:=memory_request_hash('create',NULL,jsonb_strip_nulls(p_memory)||jsonb_build_object('decision',asked),NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 v_review:=memory_review_at(p_decision->>'reviewBy');
 result:=create_memory(p_principal,jsonb_strip_nulls(p_memory),p_channel,p_client,NULL);
 v_id:=(result->>'id')::uuid;
 INSERT INTO memory_decisions(memory_id,confidence,confidence_source) VALUES(v_id,v_confidence,v_source);
 INSERT INTO memory_decision_states(memory_id,revision,review_at) VALUES(v_id,(result->>'revision')::integer,v_review);
 result:=result||jsonb_build_object('decision',jsonb_build_object('confidence',v_confidence,'confidenceSource',v_source,
   'reviewAt',v_review,'clamped',CASE WHEN raw>0.95 THEN 'highest' WHEN raw<0.05 THEN 'lowest' END));
 RETURN memory_answered(p_principal,p_key,'create',hash,v_id,result);
END $$;

-- record_memory_outcome: how decision p_memory turned out, p_result right, wrong, mixed or unknown, by whoever may change
-- it, as a change of its state (a version kept, the revision moved, one audit row, outcome). right, wrong and mixed end
-- its reviews; unknown asks again 90 days on, twice, and the third stops asking. A repeat of right, wrong or mixed, or an
-- unknown once the asking has stopped, writes nothing and answers unchanged; another unknown asks again, a change of its
-- own, so it is not idempotent.
CREATE FUNCTION record_memory_outcome(p_principal uuid,p_memory uuid,p_result text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; s memory_decision_states; v_rearms smallint; v_review timestamptz; said jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_result IS NULL OR p_result NOT IN ('right','wrong','mixed','unknown') THEN
   RAISE EXCEPTION 'result is right, wrong, mixed or unknown' USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF NOT EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=m.id) THEN
   RAISE EXCEPTION 'This memory is no decision: one remembered with decision true has an outcome to record' USING ERRCODE='22023';
 END IF;
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review takes an outcome only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is out of the ledger: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 s:=memory_decision_at(m.id,m.revision);
 said:=jsonb_build_object('result',p_result,'previous',s.outcome);
 IF p_result<>'unknown' THEN
   IF s.outcome=p_result THEN
     RETURN memory_reply(m,scoped)||said||jsonb_build_object('rearms',s.rearms,'reviewAt',s.review_at,'changed','[]'::jsonb,'unchanged',true);
   END IF;
   v_rearms:=0; v_review:=NULL;
 ELSE
   IF s.outcome='unknown' AND s.review_at IS NULL THEN
     RETURN memory_reply(m,scoped)||said||jsonb_build_object('rearms',s.rearms,'reviewAt',NULL,'changed','[]'::jsonb,'unchanged',true);
   END IF;
   v_rearms:=CASE WHEN s.outcome='unknown' THEN s.rearms ELSE 0 END;
   IF v_rearms<2 THEN
     v_rearms:=v_rearms+1; v_review:=memory_review_default(memory_decision_review_days());
   ELSE
     v_review:=NULL;
   END IF;
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_decision_states(memory_id,revision,outcome,rearms,review_at) VALUES(m.id,m.revision,p_result,v_rearms,v_review);
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'outcome',p_principal,channel,p_client,
   jsonb_build_object('result',p_result,'previous',s.outcome,'rearms',v_rearms,'fields',jsonb_build_array('outcome')));
 RETURN memory_reply(m,scoped)||said||jsonb_build_object('rearms',v_rearms,'reviewAt',v_review,'changed',jsonb_build_array('outcome'),
   'unchanged',false);
END $$;

-- 041's undo_memory, as it was, with a decision's state: the state at the revision it goes back to, when it differs from
-- the state now, is a field it changes (outcome), and it is written at the undo's own revision, so the outcome comes back
-- as it was then. A memory that is no decision, or whose state is the same, is undone exactly as before.
CREATE OR REPLACE FUNCTION undo_memory(p_principal uuid,p_memory uuid,p_to integer,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; n memories; v memory_versions; target integer; floor integer; kept integer;
 project uuid; changed text[]; detail jsonb; ds memory_decision_states; dt memory_decision_states;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_to IS NOT NULL AND p_to<1 THEN
   RAISE EXCEPTION 'A revision is 1 or more' USING ERRCODE='22023';
 END IF;
 -- The memory, locked as every change locks it: its resources row first, then the memory (D-086).
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
 SELECT * INTO m FROM memories WHERE id=p_memory FOR UPDATE;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) OR NOT reads_subject(p_principal,m.subject_id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 target:=coalesce(p_to,m.revision-1);
 -- Who may undo, before anything else is answered (033's memory_for_change order): one who reads the memory but may not
 -- change it, and did not make every change since the target, is refused whatever it asks, an undo to the current
 -- revision or a stale one included.
 IF NOT memory_may_change(p_principal,m.subject_id,m.author_id,scoped)
   AND NOT memory_own_changes(p_principal,m.id,m.subject_id,target,m.revision,scoped) THEN
   RAISE EXCEPTION 'Only this memory''s author, a Family admin or whoever made these changes may undo them' USING ERRCODE='42501';
 END IF;
 IF p_expected IS NOT NULL AND p_expected<>m.revision THEN
   RAISE EXCEPTION 'The memory changed since revision %: it is at revision % now; read it again and retry',p_expected,m.revision
     USING ERRCODE='40001';
 END IF;
 IF target>m.revision THEN
   RAISE EXCEPTION 'This memory has no revision %: it is at revision %',target,m.revision USING ERRCODE='22023',HINT='no_version';
 END IF;
 IF target<1 THEN
   RAISE EXCEPTION 'Nothing to undo: this memory has not changed since it was written' USING ERRCODE='22023',HINT='nothing_to_undo';
 END IF;
 IF target=m.revision THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true,'to',target);
 END IF;
 -- Only what this caller sees of its history (034): a revision from before the change that brought the memory to it is
 -- not there for it, whatever is kept; one no longer kept names the oldest that is.
 floor:=memory_history_floor(p_principal,m.id);
 IF target<floor THEN
   RAISE EXCEPTION 'Revision % is from before this memory reached you; history starts at revision %',target,floor
     USING ERRCODE='22023',HINT='not_visible';
 END IF;
 SELECT * INTO v FROM memory_versions WHERE memory_id=m.id AND revision=target;
 IF NOT FOUND THEN
   SELECT min(x.revision) INTO kept FROM memory_versions x WHERE x.memory_id=m.id AND x.revision>=floor;
   kept:=coalesce(kept,m.revision);
   RAISE EXCEPTION 'Revision % is no longer kept; the oldest kept is revision %',target,kept USING ERRCODE='22023',HINT='pruned:'||kept;
 END IF;
 IF v.subject_id<>m.subject_id THEN
   RAISE EXCEPTION 'Revision % is from another scope: an undo never moves a memory between scopes',target
     USING ERRCODE='22023',HINT='moved';
 END IF;
 n:=m;
 n.content:=v.content; n.tags:=v.tags; n.status:=v.status; n.valid_from:=v.valid_from; n.valid_until:=v.valid_until;
 n.kind:=v.state->>'kind'; n.importance:=(v.state->>'importance')::smallint; n.volatility:=v.state->>'volatility';
 n.capsule:=v.state->>'capsule'; n.capsule_slot:=v.state->>'capsule_slot';
 -- A project deleted since is not made again; one still there is key-share locked, so a delete at the same moment waits
 -- for this undo and then takes the memory out with it (035's order).
 project:=(v.state->>'project_id')::uuid;
 IF project IS NOT NULL THEN
   PERFORM 1 FROM memory_projects p WHERE p.id=project AND p.subject_id=m.subject_id FOR KEY SHARE;
   IF NOT FOUND THEN project:=NULL; END IF;
 END IF;
 n.project_id:=project;
 IF n.project_id IS NULL AND n.capsule='project' THEN n.capsule:=NULL; n.capsule_slot:=NULL; END IF;
 -- The hold. A held revision comes back held. New words: no hold here, so 036's trigger screens them against the words
 -- they replace. The same words keep the hold they have, unless a person undoes back to where they were released.
 IF v.state->>'hold' IS NOT NULL THEN
   n.hold:=v.state->>'hold'; n.hold_score:=(v.state->>'hold_score')::real;
 ELSIF n.content IS DISTINCT FROM m.content OR n.tags IS DISTINCT FROM m.tags OR channel IN ('app','cli') THEN
   n.hold:=NULL; n.hold_score:=NULL;
 END IF;
 -- A decision's state (060): the state at the target, when the memory had one then, against the state now.
 ds:=memory_decision_at(m.id,m.revision);
 dt:=memory_decision_at(m.id,target);
 changed:=ARRAY(SELECT f FROM (VALUES
   ('content',n.content IS DISTINCT FROM m.content),('tags',n.tags IS DISTINCT FROM m.tags),('status',n.status IS DISTINCT FROM m.status),
   ('kind',n.kind IS DISTINCT FROM m.kind),('importance',n.importance IS DISTINCT FROM m.importance),
   ('volatility',n.volatility IS DISTINCT FROM m.volatility),('project',n.project_id IS DISTINCT FROM m.project_id),
   ('capsule',n.capsule IS DISTINCT FROM m.capsule OR n.capsule_slot IS DISTINCT FROM m.capsule_slot),
   ('validity',n.valid_from IS DISTINCT FROM m.valid_from OR n.valid_until IS DISTINCT FROM m.valid_until),
   ('outcome',dt.memory_id IS NOT NULL AND (dt.outcome,dt.rearms,dt.review_at) IS DISTINCT FROM (ds.outcome,ds.rearms,ds.review_at))) c(f,d)
   WHERE d);
 -- A capsule slot is a person's step (039): an undo through an AI client's channel that would put a memory into a slot,
 -- take it out or move it is refused, and writes nothing.
 IF channel='mcp' AND 'capsule'=ANY(changed) THEN
   RAISE EXCEPTION 'This undo would change a capsule slot, which a person does from the app or the terminal, never an AI client'
     USING ERRCODE='42501';
 END IF;
 IF cardinality(changed)=0 AND n.hold IS NOT DISTINCT FROM m.hold THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true,'to',target);
 END IF;
 PERFORM memory_retire(m);
 -- 039 keeps a project slot with its project, refusing a change that moves a memory filling one to another project. An
 -- undo brings back a revision's slot and project together, so a memory filling one project's slot that goes back to
 -- another project's slot leaves the first slot in this same change before it moves.
 IF m.capsule='project' AND n.capsule='project' AND n.project_id IS DISTINCT FROM m.project_id THEN
   UPDATE memories SET capsule=NULL,capsule_slot=NULL WHERE id=m.id;
 END IF;
 UPDATE memories SET content=n.content,tags=n.tags,status=n.status,kind=n.kind,importance=n.importance,volatility=n.volatility,
   project_id=n.project_id,capsule=n.capsule,capsule_slot=n.capsule_slot,valid_from=n.valid_from,valid_until=n.valid_until,
   hold=n.hold,hold_score=n.hold_score,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO n;
 IF 'outcome'=ANY(changed) THEN
   INSERT INTO memory_decision_states(memory_id,revision,outcome,rearms,review_at) VALUES(n.id,n.revision,dt.outcome,dt.rearms,dt.review_at);
 END IF;
 -- A hold this undo put back, noted for the transaction as 036's screening notes one it puts on, so the audit row and
 -- the reply say the memory is held now.
 IF n.hold IS NOT NULL AND m.hold IS NULL AND v.state->>'hold' IS NOT NULL THEN
   PERFORM set_config('rhythm.memory_held',(coalesce(nullif(current_setting('rhythm.memory_held',true),'')::jsonb,'{}'::jsonb)
     ||jsonb_build_object(n.id::text,jsonb_build_object('score',round(n.hold_score::numeric,2),'reason',n.hold,'signals','[]'::jsonb,
       'restored',true,'revision',n.revision)))::text,true);
 END IF;
 IF n.hold IS DISTINCT FROM m.hold THEN changed:=changed||'hold'::text; END IF;
 detail:=jsonb_build_object('to',target,'fields',to_jsonb(changed))
   ||CASE WHEN m.hold IS NOT NULL AND n.hold IS NULL THEN jsonb_build_object('unheld',m.hold) ELSE '{}'::jsonb END;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(n.id,n.subject_id,n.revision,'reverted',p_principal,channel,p_client,detail);
 RETURN memory_reply(n,scoped)||jsonb_build_object('changed',to_jsonb(changed),'to',target);
END $$;

-- import_memory_decision: the decision of one memory of an export (its line's decision: a confidence and its source, or
-- neither, and its states, each a revision with its outcome, re-asks and review date), for the memory rhythm import has
-- just added from it (043's and 054's import_memory answered added, in this transaction), at the revisions the import kept:
-- from the oldest version it kept (or the memory's revision, with none kept) on, the state at that floor written at the
-- floor, as the export writes its own floor's, so no state stands at a revision whose version the import left out (one
-- from a scope its reader does not read, or not Family's with scopes off). Answers added; present (the memory has one
-- here already, left as it is); not_imported (no memory this origin's import added by that id in this transaction is
-- here: nothing is added to a memory that was here before, or that an earlier import brought and a person may have
-- changed since); or refused, invalid (anything the ledger's tables do not take, or no state at or before that floor).
CREATE FUNCTION import_memory_decision(p_origin jsonb,p_memory jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_id uuid; d jsonb; v_revision integer; v_floor integer;
BEGIN
 BEGIN
   d:=p_memory->'decision';
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(d) IS DISTINCT FROM 'object'
     OR jsonb_typeof(coalesce(d->'confidence','null'::jsonb)) NOT IN ('number','null')
     OR jsonb_typeof(coalesce(d->'confidenceSource','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(d->'states') IS DISTINCT FROM 'array' OR jsonb_array_length(d->'states') NOT BETWEEN 1 AND 1000
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(d->'states') x WHERE jsonb_typeof(x)<>'object'
       OR jsonb_typeof(x->'revision') IS DISTINCT FROM 'number' OR jsonb_typeof(coalesce(x->'rearms','0'::jsonb))<>'number'
       OR jsonb_typeof(coalesce(x->'outcome','null'::jsonb)) NOT IN ('string','null')
       OR jsonb_typeof(coalesce(x->'reviewAt','null'::jsonb)) NOT IN ('string','null')) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid;
   IF v_id IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   -- Only a memory this import added: its imported row is of this transaction (memory_events.at is now(), its start).
   IF NOT memory_imported(p_origin,'memory',v_id) OR NOT EXISTS(SELECT 1 FROM memories m WHERE m.id=v_id)
     OR NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='imported' AND e.at=now()) THEN
     RETURN jsonb_build_object('outcome','not_imported');
   END IF;
   SELECT m.revision INTO v_revision FROM memories m WHERE m.id=v_id;
   -- The import's own floor: the oldest version import_memory kept here, or the memory's revision when it kept none.
   SELECT coalesce(min(v.revision),v_revision) INTO v_floor FROM memory_versions v WHERE v.memory_id=v_id;
   IF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(d->'states') x WHERE (x->>'revision')::integer<=v_floor) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   INSERT INTO memory_decisions(memory_id,confidence,confidence_source)
   VALUES(v_id,(d->>'confidence')::numeric,d->>'confidenceSource') ON CONFLICT(memory_id) DO NOTHING;
   IF NOT FOUND THEN RETURN jsonb_build_object('outcome','present'); END IF;
   -- The newest state at or below the floor, written at the floor, and those above it up to the memory's revision.
   INSERT INTO memory_decision_states(memory_id,revision,outcome,rearms,review_at)
   SELECT v_id,greatest((x->>'revision')::integer,v_floor),x->>'outcome',coalesce((x->>'rearms')::smallint,0),(x->>'reviewAt')::timestamptz
   FROM jsonb_array_elements(d->'states') x
   WHERE (x->>'revision')::integer<=v_revision AND ((x->>'revision')::integer>=v_floor
     OR (x->>'revision')::integer=(SELECT max((y->>'revision')::integer) FROM jsonb_array_elements(d->'states') y
       WHERE (y->>'revision')::integer<=v_floor));
   RETURN jsonb_build_object('outcome','added','id',v_id);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list of checked tables and the undo get their search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_decision_review_days() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_review_default(integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_review_at(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_confidence(double precision) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_decision_at(uuid,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_decision_listed(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION remember_memory_decision(uuid,jsonb,jsonb,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION record_memory_outcome(uuid,uuid,text,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_memory(uuid,uuid,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_decision(jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_decision_review_days(),memory_review_default(integer),memory_review_at(text),
 memory_confidence(double precision),memory_decision_at(uuid,integer),memory_decision_listed(uuid,text),
 remember_memory_decision(uuid,jsonb,jsonb,text,text,text),record_memory_outcome(uuid,uuid,text,integer,text,text),
 undo_memory(uuid,uuid,integer,integer,text,text),import_memory_decision(jsonb,jsonb) FROM PUBLIC;
