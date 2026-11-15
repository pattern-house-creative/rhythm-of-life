-- Due dates for native memories (SB-P5-10 in docs/dev/second-brain-parity/backlog.md; story 12; ledger rows WRT-12,
-- TIM-08, API-33, API-34, API-35 and INT-16; docs/dev/memory.md sections 5 and 9): when a memory should come back to
-- the person, from an explicit when or from the one unambiguous date in its words, in the install's time zone (D-043);
-- the due list, by one predicate that every count and list reads; and snooze and clear, each a change like any other:
-- a version kept, the revision moved, one audit row, and undo puts it back. Adapted from Second Brain's time anchors
-- (src/when/input.ts, src/when/heuristic.ts, src/when/timezone.ts, src/memory/actions.ts and the due feed in
-- src/routes/admin.ts at the pinned commit, MIT; NOTICE).
--
-- A due date is three columns on the memory, as upstream's when_at, when_kind and when_source: the moment (due_at), what
-- kind of moment it is (due: a deadline; event: something happening then; wake: a plain reminder) and where it came from
-- (explicit: the caller gave it; text: the engine read it from the memory's words). The engine reads words only for one
-- date that cannot be read two ways (Core/DueDates.cs: two dates, also joined by and, or or a comma, a weekday or a
-- relative day such as tomorrow, a date that reads two ways (a numeric date that reads as two days or has a two-digit
-- year, a day number on each side of a month name, a number after a date with no year), a range, by a dash, to, through
-- or until, a day that does not exist or has passed are never guessed, and the answer says why), and runs no model for
-- dates (D-033; upstream's nightly model pass, JOB-05, is not adapted). A date with no zone is anchored in the install's
-- zone (install_timezone(), D-043): a day at its first moment there, a time of day as it reads on that zone's clocks. A
-- due date is part of a memory's state: memory_retire (and a move's and a cascade's retire) keeps it in the version it
-- writes, history reads the change that set it, and undo (041) puts it back.
--
-- The due list (memory_due) is upstream's due feed: a memory with a due date, not task:done (a finished task keeps its
-- date and leaves the list), live (its own catalog row current, not in the trash), not held, not deprecated and its
-- window open, as every queue asks of a memory (053's stale queue, 058's open loops). Its windows are upstream's:
-- overdue (the moment has come), upcoming (within 48 hours) and later. A reader's list is what its open loops are (058's
-- memory_loop_mine): the memories it reads, may change and owns; a Family admin is not served another member's.
--
-- remember_memory_due writes a memory with its due date in one transaction: create_memory's write (033), or 058's
-- remember_memory_commitment's for a commitment, then the date on it, at revision 1, with an audit row of its own
-- (due_set, naming the date and where it came from). Its idempotency key hashes the request as the caller sent it:
-- the memory (and the commitment) with the when and when_kind it asked, none for a date the engine read from the words,
-- which hashes as create_memory and remember_memory_commitment hash the same request, so a retry answers the first answer
-- whatever the words give on the day of the retry.
--
-- resolve_memory_due: whoever may change the memory (033's author lock). snooze: its due date moves to a moment in the
-- future, at most five years ahead (a memory with none takes one, kind wake, as upstream's snooze sets one); clear_date:
-- its due date comes off. Each is a change of the memory: the state it replaces is kept as a version (memory_retire),
-- the revision moves and one audit row, due_changed, names the action and the dates before and after; 041's undo
-- (replaced here) puts the date back. A repeat answers unchanged and writes nothing. A memory held for review, marked
-- wrong or whose window has closed is refused, as still true and resolve_loop refuse them, and so is a decision (060),
-- which takes no due date (D-214): its review date is its own. No idempotency key: a repeat answers unchanged, and
-- expected_revision makes a retry after an undo safe.
--
-- An export and import keep a due date: rhythm export writes the memory's due on its line, and import_memory_due, called
-- by rhythm import for each memory it has just added, puts it there, unless that memory is a decision.
--
-- Nothing calls these but the memory tools (list_due, resolve_due and remember's when and when_kind, and remember reading
-- a date from the words), made only with memory.tools on (off by default), rhythm import, and tests: with none called no
-- memory has a due date, no audit row is due_set or due_changed, every version's state is as before (strip_nulls drops
-- the empty fields) and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision; 23505 an idempotency key used for another
-- request; 22023 invalid input (also a due date more than five years ahead, a snooze to a moment that has come, a memory
-- whose window has closed and, with the hint decision, a decision) and, with a hint the engine reads, held (a memory
-- held for review) and retracted (a deprecated memory). A refused call writes nothing. The number is provisional.

-- The due date: all three columns or none, at a finite moment.
ALTER TABLE memories ADD COLUMN due_at timestamptz CHECK(isfinite(due_at)),
 ADD COLUMN due_kind text CHECK(due_kind IN ('due','event','wake')),
 ADD COLUMN due_source text CHECK(due_source IN ('explicit','text')),
 ADD CONSTRAINT memories_due_whole CHECK((due_at IS NULL)=(due_kind IS NULL) AND (due_at IS NULL)=(due_source IS NULL));
CREATE INDEX memories_due ON memories(due_at,id) WHERE due_at IS NOT NULL;

-- A due date as answers and audit rows name it: the moment in UTC, how it reads on the install's clocks, the zone, its
-- kind and where it came from; null for none.
CREATE FUNCTION memory_due_json(p_at timestamptz,p_kind text,p_source text) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_at IS NOT NULL THEN jsonb_build_object('at',to_char(p_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),
   'local',to_char(p_at AT TIME ZONE install_timezone(),'YYYY-MM-DD HH24:MI'),'zone',install_timezone(),'kind',p_kind,'source',p_source) END
$$;

-- The state a change retires (033's memory_retire), with the due date beside the rest of its columns.
CREATE OR REPLACE FUNCTION memory_retire(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score,
     'due_at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'due_kind',m.due_kind,'due_source',m.due_source)),
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- 048's memory_retire_moved, with the due date.
CREATE OR REPLACE FUNCTION memory_retire_moved(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score,
     'due_at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'due_kind',m.due_kind,'due_source',m.due_source))
   ||'{"moved":true}'::jsonb,
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- 049's memory_retire_cascade, with the due date.
CREATE OR REPLACE FUNCTION memory_retire_cascade(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score,
     'due_at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'due_kind',m.due_kind,'due_source',m.due_source))
   ||'{"cascade":true}'::jsonb,
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- The one definition of the due list: the due moment of p_memory when it is on it, else null. A due date, not task:done;
-- live (its own catalog row current, not in the trash), not held, not deprecated, and its window open. Every count and
-- list reads it.
CREATE FUNCTION memory_due(p_memory uuid) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT m.due_at FROM memories m
 WHERE m.id=p_memory AND m.due_at IS NOT NULL AND NOT 'task:done'=ANY(m.tags)
   AND m.status<>'deprecated' AND m.hold IS NULL AND (m.valid_until IS NULL OR m.valid_until>now())
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
     AND r.status='current')
$$;

-- A due moment's window, as upstream's due feed draws it: overdue (its moment has come), upcoming (within 48 hours) or
-- later; null for none.
CREATE FUNCTION memory_due_window(p_at timestamptz) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_at IS NULL THEN NULL WHEN p_at<=now() THEN 'overdue' WHEN p_at<=now()+interval '48 hours' THEN 'upcoming'
   ELSE 'later' END
$$;

-- A due date a write takes (p_due: at, kind and source), checked: a known kind and source, a finite moment that says its
-- offset (Z or one such as +01:00, so it never reads in the session's zone), at most five years ahead. Answers the moment
-- to the whole second, as every answer, page and version state names it, so an imported moment with a fraction of a
-- second is kept as they name it.
CREATE FUNCTION memory_due_checked(p_due jsonb) RETURNS timestamptz LANGUAGE plpgsql STABLE AS $$
DECLARE v_at timestamptz;
BEGIN
 IF p_due IS NULL OR jsonb_typeof(p_due)<>'object' OR jsonb_typeof(p_due->'at') IS DISTINCT FROM 'string'
   OR coalesce(p_due->>'kind','') NOT IN ('due','event','wake') OR coalesce(p_due->>'source','') NOT IN ('explicit','text')
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_due) k WHERE k NOT IN ('at','kind','source')) THEN
   RAISE EXCEPTION 'A due date takes at, kind (due, event or wake) and source (explicit or text)' USING ERRCODE='22023';
 END IF;
 IF p_due->>'at' !~ '^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?([Zz]|[+-]\d{2}(:?\d{2})?)$' THEN
   RAISE EXCEPTION 'A due date''s moment is a date and time with its offset (Z or one such as +01:00)' USING ERRCODE='22023';
 END IF;
 v_at:=date_trunc('second',(p_due->>'at')::timestamptz);
 IF NOT isfinite(v_at) OR v_at>now()+interval '5 years' THEN
   RAISE EXCEPTION 'A due date is at most five years ahead' USING ERRCODE='22023';
 END IF;
 RETURN v_at;
END $$;

-- remember_memory_due: a new memory from p_memory (create_memory's fields, MemoryInput's) with its due date p_due, and,
-- when p_direction is given, a commitment (058: out, the person owes p_counterparty; in, p_counterparty owes them). The
-- key hashes the request as the caller sent it (p_asked: the when and when_kind it gave, none for a date read from the
-- words), as create_memory and remember_memory_commitment hash it with nothing asked.
CREATE FUNCTION remember_memory_due(p_principal uuid,p_memory jsonb,p_due jsonb,p_asked jsonb,p_direction text,p_counterparty text,
 p_channel text,p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_at timestamptz; asked jsonb; tagged jsonb; hash text; replay jsonb; result jsonb; m memories;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 IF p_memory IS NULL OR jsonb_typeof(p_memory)<>'object' THEN
   RAISE EXCEPTION 'A memory takes content, tags, kind, importance, volatility, scope, project and source' USING ERRCODE='22023';
 END IF;
 IF p_asked IS NOT NULL AND (jsonb_typeof(p_asked)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_asked) k WHERE k NOT IN ('when','when_kind'))) THEN
   RAISE EXCEPTION 'What a caller asked of a due date is its when and when_kind' USING ERRCODE='22023';
 END IF;
 v_at:=memory_due_checked(p_due);
 asked:=CASE WHEN p_asked IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('due',jsonb_strip_nulls(p_asked)) END;
 IF p_direction IS NULL THEN
   hash:=memory_request_hash('create',NULL,jsonb_strip_nulls(p_memory)||asked,NULL);
 ELSE
   IF p_direction NOT IN ('out','in') THEN
     RAISE EXCEPTION 'A commitment is owed by someone (in) or to someone (out)' USING ERRCODE='22023';
   END IF;
   -- 058's request, as remember_memory_commitment hashes it: the task tag added when left out, and the commitment.
   tagged:=jsonb_strip_nulls(p_memory);
   IF jsonb_typeof(coalesce(tagged->'tags','[]'::jsonb))<>'array' THEN
     RAISE EXCEPTION 'Tags are a list of words' USING ERRCODE='22023';
   END IF;
   IF NOT coalesce(tagged->'tags','[]'::jsonb) ? 'task' THEN
     tagged:=tagged||jsonb_build_object('tags',coalesce(tagged->'tags','[]'::jsonb)||'["task"]'::jsonb);
   END IF;
   hash:=memory_request_hash('create',NULL,tagged||jsonb_build_object('commitment',
     jsonb_build_object('direction',p_direction,'counterparty',memory_counterparty(p_counterparty)))||asked,NULL);
 END IF;
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 result:=CASE WHEN p_direction IS NULL THEN create_memory(p_principal,p_memory,p_channel,p_client,NULL)
   ELSE remember_memory_commitment(p_principal,p_memory,p_direction,p_counterparty,p_channel,p_client,NULL) END;
 UPDATE memories SET due_at=v_at,due_kind=p_due->>'kind',due_source=p_due->>'source' WHERE id=(result->>'id')::uuid RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'due_set',p_principal,memory_channel(p_principal,p_channel,p_client),p_client,
   jsonb_build_object('due',jsonb_build_object('at',to_char(v_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),
     'kind',m.due_kind,'source',m.due_source),'fields',jsonb_build_array('due')));
 result:=result||jsonb_build_object('due',memory_due_json(m.due_at,m.due_kind,m.due_source));
 RETURN memory_answered(p_principal,p_key,'create',hash,m.id,result);
END $$;

-- resolve_memory_due: p_action snooze (to p_until) or clear_date on p_memory, by whoever may change it, as a change of its
-- due date (a version kept, the revision moved, one audit row, due_changed). A repeat writes nothing and answers unchanged.
CREATE FUNCTION resolve_memory_due(p_principal uuid,p_memory uuid,p_action text,p_until timestamptz,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; v_at timestamptz; v_kind text; v_source text; before jsonb; after jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_action IS NULL OR p_action NOT IN ('snooze','clear_date') THEN
   RAISE EXCEPTION 'action is snooze or clear_date' USING ERRCODE='22023';
 END IF;
 IF p_action='snooze' AND (p_until IS NULL OR NOT isfinite(p_until)) THEN
   RAISE EXCEPTION 'snooze takes until: the date it should come back' USING ERRCODE='22023';
 END IF;
 IF p_action='clear_date' AND p_until IS NOT NULL THEN
   RAISE EXCEPTION 'clear_date takes no until: it takes the date off' USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is snoozed or cleared only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is on no due list: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF m.valid_until<=now() THEN
   RAISE EXCEPTION 'This memory stopped being true at %: it is history, not something due (clear valid_until, or undo, if it is true again)',
     to_char(m.valid_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 -- A decision takes no due date (D-214): it comes up for review at its own review date (060), until the brief settles
 -- one
 -- due definition (SB-P5-08).
 IF EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=m.id) THEN
   RAISE EXCEPTION 'A decision takes no due date: it comes up for review at its review date (record_outcome, or remember''s review_by)'
     USING ERRCODE='22023',HINT='decision';
 END IF;
 before:=memory_due_json(m.due_at,m.due_kind,m.due_source);
 IF p_action='snooze' THEN
   IF p_until<=now() THEN
     RAISE EXCEPTION 'until must be in the future: % has come already',to_char(p_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"')
       USING ERRCODE='22023';
   END IF;
   IF p_until>now()+interval '5 years' THEN
     RAISE EXCEPTION 'until is at most five years ahead' USING ERRCODE='22023';
   END IF;
   v_at:=date_trunc('second',p_until); v_kind:=coalesce(m.due_kind,'wake'); v_source:='explicit';
 END IF;
 IF v_at IS NOT DISTINCT FROM m.due_at AND v_kind IS NOT DISTINCT FROM m.due_kind AND v_source IS NOT DISTINCT FROM m.due_source THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('action',p_action,'due',before,'before',before,'changed','[]'::jsonb,'unchanged',true);
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET due_at=v_at,due_kind=v_kind,due_source=v_source,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 after:=memory_due_json(m.due_at,m.due_kind,m.due_source);
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'due_changed',p_principal,channel,p_client,
   jsonb_build_object('action',p_action,'from',before-'local'-'zone','to',after-'local'-'zone','fields',jsonb_build_array('due')));
 RETURN memory_reply(m,scoped)||jsonb_build_object('action',p_action,'due',after,'before',before,'changed',jsonb_build_array('due'),
   'unchanged',false);
END $$;

-- 041's undo_memory, the same but for the due date: a revision's due date comes back with the rest of its state (its
-- version's due_at, due_kind and due_source; none for a state kept before 061, when no memory had one), and 'due' is a
-- field an undo compares and names. It replaces 060's replacement, so it keeps 060's lines as they are: a decision's
-- state at the revision it goes back to, against the state now, 'outcome' a field it compares, and that state written
-- at its own revision.
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
 -- 061: the revision's due date (none in a state kept without one).
 n.due_at:=(v.state->>'due_at')::timestamptz; n.due_kind:=v.state->>'due_kind'; n.due_source:=v.state->>'due_source';
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
   ('outcome',dt.memory_id IS NOT NULL AND (dt.outcome,dt.rearms,dt.review_at) IS DISTINCT FROM (ds.outcome,ds.rearms,ds.review_at)),
   -- 061: the due date.
   ('due',n.due_at IS DISTINCT FROM m.due_at OR n.due_kind IS DISTINCT FROM m.due_kind OR n.due_source IS DISTINCT FROM m.due_source)) c(f,d) WHERE d);
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
   hold=n.hold,hold_score=n.hold_score,due_at=n.due_at,due_kind=n.due_kind,due_source=n.due_source,
   revision=revision+1,updated_at=greatest(now(),updated_at)
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

-- import_memory_due: the due date of one memory of an export (its line's due: at, kind and source), for the memory rhythm
-- import has just added from it (043's and 054's import_memory answered added, in this transaction). Answers added;
-- present (the memory has one here already, left as it is); not_imported (no memory this origin's import added by that
-- id in this transaction is here: nothing is added to a memory that was here before, or that an earlier import brought
-- and a person may have changed since); or refused, invalid (also a date more than five years ahead, and a memory that
-- is a decision: import_memory_decision runs first, so a line naming both keeps the decision, D-214). The date is
-- written with the memory, at its imported revision, as remember_memory_due writes one: no revision moves.
CREATE FUNCTION import_memory_due(p_origin jsonb,p_memory jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_id uuid; v_at timestamptz;
BEGIN
 BEGIN
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(p_memory->'due') IS DISTINCT FROM 'object' THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid;
   v_at:=memory_due_checked(jsonb_build_object('at',p_memory->'due'->'at','kind',p_memory->'due'->'kind','source',p_memory->'due'->'source'));
   IF v_id IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   -- Only a memory this import added: its imported row is of this transaction (memory_events.at is now(), its start).
   IF NOT memory_imported(p_origin,'memory',v_id) OR NOT EXISTS(SELECT 1 FROM memories m WHERE m.id=v_id)
     OR NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='imported' AND e.at=now()) THEN
     RETURN jsonb_build_object('outcome','not_imported');
   END IF;
   -- A decision takes no due date (D-214): a line naming both keeps the decision (import_memory_decision runs first).
   IF EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=v_id) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   UPDATE memories SET due_at=v_at,due_kind=p_memory->'due'->>'kind',due_source=p_memory->'due'->>'source'
   WHERE id=v_id AND due_at IS NULL;
   IF NOT FOUND THEN RETURN jsonb_build_object('outcome','present'); END IF;
   RETURN jsonb_build_object('outcome','added','id',v_id);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR invalid_parameter_value THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so each replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION memory_due_json(timestamptz,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire_moved(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire_cascade(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_due(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_due_window(timestamptz) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_due_checked(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION remember_memory_due(uuid,jsonb,jsonb,jsonb,text,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_memory_due(uuid,uuid,text,timestamptz,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_memory(uuid,uuid,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_due(jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_due_json(timestamptz,text,text),memory_retire(memories),memory_retire_moved(memories),
 memory_retire_cascade(memories),memory_due(uuid),memory_due_window(timestamptz),memory_due_checked(jsonb),
 remember_memory_due(uuid,jsonb,jsonb,jsonb,text,text,text,text,text),resolve_memory_due(uuid,uuid,text,timestamptz,integer,text,text),
 undo_memory(uuid,uuid,integer,integer,text,text),import_memory_due(jsonb,jsonb) FROM PUBLIC;
