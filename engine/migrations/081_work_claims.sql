-- Assignment and claims for work items (memory.work, off by default; docs/dev/memory.md section 5; migrations 079 and
-- 080). Every AI client session of a household uses the same login, and the name a client gives is its own word, so
-- neither tells two sessions apart. A claim does: the engine makes a claim id when an item is claimed and gives it to the
-- caller, and only that id renews the claim, releases it or moves the item while it holds. The label the caller gives
-- ("<session title> - <agent>") is kept with it for people to read and is never checked.
--
-- 1. Assignment: whose the work is, a work property beside type, state and priority, so it is versioned with them
--    (memory_work_states), in history and undone by undo. Either a person of the install (assignee_person, a current
--    person when it is set) or an agent's label (assignee_label, 1 to 120 characters on one line, refused when it reads
--    like instructions to an AI client, since it is listed to every client that reads the item and no hold covers it, as
--    058 refuses such a name). A word that names a current person is that person; any other is a label. Assignment
--    holds nothing: the claim is what binds an item to one session, so a label need be neither unique nor stable.
-- 2. Claims: memory_work_claims, one row an item, gone with its work row (an undo that makes it an ordinary memory, a
--    delete for good). A claim lasts memory_work_claim_minutes() (two hours) from when it was made or last renewed;
--    one past its time is no claim, for every read and write, and a new claim takes its row. A claim is no change of
--    the memory: it moves no revision and keeps no version, so undo neither makes nor ends one, and a renewal writes
--    nothing but the row. Making and ending one each write an audit row (work_claimed, work_released) at the memory's
--    revision, so history says who worked on it and when.
--    - claim_memory_work: whoever may change the memory (033's author lock) claims an open work item (not done or
--      cancelled, not held, not marked wrong, its window open) that no live claim holds; the answer gives the claim id.
--      With a claim id, the claim it names is renewed, while it holds. An item in backlog or ready moves to active in the
--      same call, as a change of the memory (a version, the revision moved, work_changed with action claim); one already
--      active or in review keeps its state.
--    - release_memory_work: the claim ends. Through an AI client's channel only with its claim id; through a person's
--      (the app, the terminal), by whoever may change the memory, with or without it, so a stuck claim never blocks a
--      person. With no live claim it answers unchanged.
--    - update_memory_work (079's, replaced here): while a live claim holds, a change of state needs that claim's id,
--      through every channel (a person releases the claim first); a change of type, priority or assignee does not.
--      Done or cancelled with the claim's id ends the claim in the same change.
-- 3. The writers 079 made take assignee too (create_work's and set's p_work gain it; update's p_change gains assignee and
--    claim), undo_memory (079's, replaced here) puts a revision's assignee back with its other properties, and export
--    and import carry it: a person only when a current person of that id is here, a label only when it is one; claims
--    are no export's.
--
-- Nothing calls these but the memory tools create_work, update_work, claim_work, release_work and list_work, and the app's
-- release_claim, made only with memory.work on beside memory.tools, rhythm import, undo_memory and tests: with none called
-- no work item has an assignee or a claim, and every answer is as 080 left it.
--
-- Errors, as 079's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory; 40001 a
-- stale expected revision; 22023 invalid input (also a label that reads like instructions, a finished item claimed, and a
-- claim id that names no live claim) and, with a hint the engine reads, held, retracted, and claimed (a live claim of
-- another holds the item: its label and when it ends are named). A refused call writes nothing.

ALTER TABLE memory_work ADD COLUMN assignee_person text REFERENCES subjects(id),
 ADD COLUMN assignee_label text CHECK(char_length(assignee_label) BETWEEN 1 AND 120 AND assignee_label !~ '[[:cntrl:]]'),
 ADD CONSTRAINT memory_work_one_assignee CHECK(assignee_person IS NULL OR assignee_label IS NULL);
ALTER TABLE memory_work_states ADD COLUMN assignee_person text REFERENCES subjects(id),
 ADD COLUMN assignee_label text CHECK(char_length(assignee_label) BETWEEN 1 AND 120 AND assignee_label !~ '[[:cntrl:]]'),
 ADD CONSTRAINT memory_work_states_one_assignee CHECK(assignee_person IS NULL OR assignee_label IS NULL),
 ADD CONSTRAINT memory_work_states_assignee_on_work CHECK(work_type IS NOT NULL OR assignee_person IS NULL AND assignee_label IS NULL);

-- A live or ended claim of one work item. The claim id is the caller's proof; the label is for people to read.
CREATE TABLE memory_work_claims(
 memory_id uuid PRIMARY KEY REFERENCES memory_work ON DELETE CASCADE,
 claim_id uuid NOT NULL,
 label text NOT NULL CHECK(char_length(label) BETWEEN 1 AND 120 AND label !~ '[[:cntrl:]]'),
 claimed_by uuid NOT NULL REFERENCES principals,
 channel text NOT NULL,
 client text,
 claimed_at timestamptz NOT NULL DEFAULT now(),
 renewed_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL,
 CHECK(expires_at>claimed_at)
);
REVOKE ALL ON memory_work_claims FROM PUBLIC;

DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_work_claims']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- How long a claim holds from when it was made or last renewed. MemoryWork.ClaimMinutes says the same; a test keeps them equal.
CREATE FUNCTION memory_work_claim_minutes() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 120 $$;

-- The live claim on p_memory, or none (every column null).
CREATE FUNCTION memory_work_claim(p_memory uuid) RETURNS memory_work_claims LANGUAGE sql STABLE AS $$
 SELECT c.* FROM memory_work_claims c WHERE c.memory_id=p_memory AND c.expires_at>now()
$$;

-- A claim as answers and lists name it: its label, when it was made and when it ends; never its id. Null for none.
CREATE FUNCTION memory_work_claim_json(c memory_work_claims) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN c.memory_id IS NOT NULL THEN jsonb_build_object('label',c.label,
   'claimedAt',to_char(c.claimed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),
   'expiresAt',to_char(c.expires_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"')) END
$$;

-- An assignee as answers name it: {kind: person, name: its id} or {kind: agent, name: its label}; null for none.
CREATE FUNCTION memory_work_assignee_json(p_person text,p_label text) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
 SELECT CASE WHEN p_person IS NOT NULL THEN jsonb_build_object('kind','person','name',p_person)
   WHEN p_label IS NOT NULL THEN jsonb_build_object('kind','agent','name',p_label) END
$$;

-- A label a caller gives (an assignee's or a claim's): trimmed, 1 to 120 characters on one line, and nothing that reads
-- like instructions to an AI client (036's screening).
CREATE FUNCTION memory_work_label(p_text text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE t text:=btrim(coalesce(p_text,''));
BEGIN
 IF t='' OR char_length(t)>120 OR t ~ '[[:cntrl:]]' THEN
   RAISE EXCEPTION 'A label is 1 to 120 characters on one line, such as "<session title> - <agent>": nothing was changed' USING ERRCODE='22023';
 END IF;
 IF memory_screen(t)->>'reason' IS NOT NULL THEN
   RAISE EXCEPTION 'The label reads like instructions to an AI client: give a name alone, such as "<session title> - <agent>". Nothing was changed'
     USING ERRCODE='22023';
 END IF;
 RETURN t;
END $$;

-- An assignee as a call names it: a current person of the install by their id, else an agent's label; null for none.
-- Answers {person} or {label}.
CREATE FUNCTION memory_work_assignee(p_text text) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE t text:=btrim(coalesce(p_text,''));
BEGIN
 IF t='' THEN RETURN NULL; END IF;
 IF EXISTS(SELECT 1 FROM subjects s WHERE s.id=t AND s.kind='person' AND s.status='current') THEN
   RETURN jsonb_build_object('person',t);
 END IF;
 RETURN jsonb_build_object('label',memory_work_label(t));
END $$;

-- 079's memory_work_checked, taking the assignee as the call gives it too (resolved by the writer): a known type; a
-- known state, backlog when none is given; a priority 1 to 4 or none. Answers type, state and priority only.
CREATE OR REPLACE FUNCTION memory_work_checked(p_work jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_work IS NULL OR jsonb_typeof(p_work)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_work) k WHERE k NOT IN ('type','state','priority','assignee'))
   OR jsonb_typeof(p_work->'type') IS DISTINCT FROM 'string'
   OR jsonb_typeof(coalesce(p_work->'state','null'::jsonb)) NOT IN ('string','null')
   OR jsonb_typeof(coalesce(p_work->'priority','null'::jsonb)) NOT IN ('number','null')
   OR jsonb_typeof(coalesce(p_work->'assignee','null'::jsonb)) NOT IN ('string','null') THEN
   RAISE EXCEPTION 'A work item takes a type, a state, a priority and an assignee' USING ERRCODE='22023';
 END IF;
 IF p_work->>'type' NOT IN ('task','bug','feature','improvement') THEN
   RAISE EXCEPTION 'work_type is task, bug, feature or improvement' USING ERRCODE='22023';
 END IF;
 IF coalesce(p_work->>'state','backlog') NOT IN ('backlog','ready','active','review','done','cancelled') THEN
   RAISE EXCEPTION 'state is backlog, ready, active, review, done or cancelled' USING ERRCODE='22023';
 END IF;
 IF p_work->>'priority' IS NOT NULL AND p_work->>'priority' !~ '^[1-4]$' THEN
   RAISE EXCEPTION 'priority is 1 (first) to 4 (last), or none' USING ERRCODE='22023';
 END IF;
 RETURN jsonb_build_object('type',p_work->>'type','state',coalesce(p_work->>'state','backlog'),'priority',(p_work->>'priority')::smallint);
END $$;

-- 079's remember_memory_work, with an assignee: p_work's assignee, as the call gives it, joins the key's request only
-- when given, so a call without one hashes as before.
CREATE OR REPLACE FUNCTION remember_memory_work(p_principal uuid,p_memory jsonb,p_work jsonb,p_due jsonb,p_asked jsonb,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE work jsonb; asg jsonb; asked jsonb; hash text; replay jsonb; result jsonb; m memories;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 IF p_memory IS NULL OR jsonb_typeof(p_memory)<>'object' THEN
   RAISE EXCEPTION 'A memory takes content, tags, kind, importance, volatility, scope, project and source' USING ERRCODE='22023';
 END IF;
 IF p_due IS NULL AND p_asked IS NOT NULL THEN
   RAISE EXCEPTION 'What a caller asked of a due date goes with the due date' USING ERRCODE='22023';
 END IF;
 work:=memory_work_checked(p_work);
 IF work->>'state' IN ('done','cancelled') THEN
   RAISE EXCEPTION 'A new work item starts backlog, ready, active or review: nothing was saved. update_work makes it done or cancelled, with a summary'
     USING ERRCODE='22023';
 END IF;
 IF jsonb_typeof(p_memory->'project') IS DISTINCT FROM 'string' OR btrim(p_memory->>'project')='' THEN
   RAISE EXCEPTION 'A work item belongs to a project: name its project, a slug from list_projects or a new one. Nothing was saved'
     USING ERRCODE='22023';
 END IF;
 asked:=CASE WHEN p_asked IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('due',jsonb_strip_nulls(p_asked)) END;
 hash:=memory_request_hash('create',NULL,jsonb_strip_nulls(p_memory)
   ||jsonb_build_object('work',work||CASE WHEN p_work->>'assignee' IS NOT NULL THEN jsonb_build_object('assignee',p_work->>'assignee') ELSE '{}'::jsonb END)
   ||asked,NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 asg:=memory_work_assignee(p_work->>'assignee');
 result:=CASE WHEN p_due IS NULL THEN create_memory(p_principal,jsonb_strip_nulls(p_memory),p_channel,p_client,NULL)
   ELSE remember_memory_due(p_principal,p_memory,p_due,p_asked,NULL,NULL,p_channel,p_client,NULL) END;
 SELECT * INTO m FROM memories WHERE id=(result->>'id')::uuid;
 IF 'task'=ANY(m.tags) THEN
   RAISE EXCEPTION 'A work item can''t also be a personal task: nothing was saved. Leave the tag task and any #task out; its state says where the work stands'
     USING ERRCODE='22023';
 END IF;
 INSERT INTO memory_work(memory_id,work_type,state,priority,assignee_person,assignee_label)
 VALUES(m.id,work->>'type',work->>'state',(work->>'priority')::smallint,asg->>'person',asg->>'label');
 INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority,assignee_person,assignee_label)
 VALUES(m.id,m.revision,work->>'type',work->>'state',(work->>'priority')::smallint,asg->>'person',asg->>'label');
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_set',p_principal,memory_channel(p_principal,p_channel,p_client),p_client,
   jsonb_build_object('work',work||jsonb_strip_nulls(jsonb_build_object('assignee',memory_work_assignee_json(asg->>'person',asg->>'label'))),
     'fields',jsonb_build_array('work')));
 result:=result||jsonb_build_object('work',work,'assignee',memory_work_assignee_json(asg->>'person',asg->>'label'));
 RETURN memory_answered(p_principal,p_key,'create',hash,m.id,result);
END $$;

-- 079's set_memory_work, with an assignee. Unchanged only when the type, state, priority and assignee are all as given.
CREATE OR REPLACE FUNCTION set_memory_work(p_principal uuid,p_memory uuid,p_work jsonb,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; work jsonb; asg jsonb; w memory_work;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 work:=memory_work_checked(p_work);
 asg:=memory_work_assignee(p_work->>'assignee');
 IF work->>'state' IN ('done','cancelled') THEN
   RAISE EXCEPTION 'A new work item starts backlog, ready, active or review: nothing was changed. update_work makes it done or cancelled, with a summary'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 SELECT * INTO w FROM memory_work WHERE memory_id=m.id;
 IF FOUND THEN
   IF memory_work_json(w.work_type,w.state,w.priority)=work AND w.assignee_person IS NOT DISTINCT FROM asg->>'person'
     AND w.assignee_label IS NOT DISTINCT FROM asg->>'label' THEN
     RETURN memory_reply(m,scoped)||jsonb_build_object('work',work,'assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label),
       'changed','[]'::jsonb,'unchanged',true);
   END IF;
   RAISE EXCEPTION 'This memory is a work item already (%, %): update_work changes its state, type, priority or assignee',w.work_type,w.state
     USING ERRCODE='22023';
 END IF;
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review becomes a work item only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is no work to do: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF m.valid_until<=now() THEN
   RAISE EXCEPTION 'This memory stopped being true at %: it is history, not work to do (clear valid_until, or undo, if it is true again)',
     to_char(m.valid_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 IF m.project_id IS NULL THEN
   RAISE EXCEPTION 'A work item belongs to a project: give the memory a project first (update), then make it a work item'
     USING ERRCODE='22023';
 END IF;
 IF m.standing OR EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=m.id) THEN
   RAISE EXCEPTION 'A decision or a standing instruction is not a work item: save the work as a memory of its own' USING ERRCODE='22023';
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_work(memory_id,work_type,state,priority,assignee_person,assignee_label)
 VALUES(m.id,work->>'type',work->>'state',(work->>'priority')::smallint,asg->>'person',asg->>'label');
 INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority,assignee_person,assignee_label)
 VALUES(m.id,m.revision,work->>'type',work->>'state',(work->>'priority')::smallint,asg->>'person',asg->>'label');
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_changed',p_principal,channel,p_client,
   jsonb_build_object('action','set','to',work||jsonb_strip_nulls(jsonb_build_object('assignee',memory_work_assignee_json(asg->>'person',asg->>'label'))),
     'fields',jsonb_build_array('work')));
 RETURN memory_reply(m,scoped)||jsonb_build_object('work',work,'assignee',memory_work_assignee_json(asg->>'person',asg->>'label'),
   'changed',jsonb_build_array('work'),'unchanged',false);
END $$;

-- 079's update_memory_work, with an assignee (p_change's assignee: a word to set, null to take it off) and a claim id
-- (p_change's claim): while a live claim holds, a change of state needs its id, and done or cancelled with it ends it.
CREATE OR REPLACE FUNCTION update_memory_work(p_principal uuid,p_memory uuid,p_change jsonb,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; w memory_work; c memory_work_claims; v_type text; v_state text; v_priority smallint;
 v_person text; v_label text; asg jsonb; v_summary text; before jsonb; after jsonb; fields text[]; ended boolean:=false;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_change IS NULL OR jsonb_typeof(p_change)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_change) k WHERE k NOT IN ('type','state','priority','summary','assignee','claim'))
   OR NOT (p_change ? 'type' OR p_change ? 'state' OR p_change ? 'priority' OR p_change ? 'assignee')
   OR jsonb_typeof(coalesce(p_change->'type','""'::jsonb))<>'string' OR jsonb_typeof(coalesce(p_change->'state','""'::jsonb))<>'string'
   OR jsonb_typeof(coalesce(p_change->'priority','null'::jsonb)) NOT IN ('number','null')
   OR jsonb_typeof(coalesce(p_change->'assignee','null'::jsonb)) NOT IN ('string','null')
   OR jsonb_typeof(coalesce(p_change->'summary','null'::jsonb)) NOT IN ('string','null')
   OR jsonb_typeof(coalesce(p_change->'claim','null'::jsonb)) NOT IN ('string','null') THEN
   RAISE EXCEPTION 'A work change names a state, a type, a priority or an assignee, a summary when the state becomes done or cancelled, and the claim id when the item is claimed'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 SELECT * INTO w FROM memory_work WHERE memory_id=m.id FOR UPDATE;
 IF NOT FOUND THEN
   RAISE EXCEPTION 'This memory is no work item: create_work with its id makes it one' USING ERRCODE='22023';
 END IF;
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A work item held for review changes only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is on no work list: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF m.valid_until<=now() THEN
   RAISE EXCEPTION 'This memory stopped being true at %: it is history, not work to do (clear valid_until, or undo, if it is true again)',
     to_char(m.valid_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 v_type:=coalesce(p_change->>'type',w.work_type);
 v_state:=coalesce(p_change->>'state',w.state);
 IF v_type NOT IN ('task','bug','feature','improvement') THEN
   RAISE EXCEPTION 'work_type is task, bug, feature or improvement' USING ERRCODE='22023';
 END IF;
 IF v_state NOT IN ('backlog','ready','active','review','done','cancelled') THEN
   RAISE EXCEPTION 'state is backlog, ready, active, review, done or cancelled' USING ERRCODE='22023';
 END IF;
 IF p_change ? 'priority' THEN
   IF p_change->>'priority' IS NOT NULL AND p_change->>'priority' !~ '^[1-4]$' THEN
     RAISE EXCEPTION 'priority is 1 (first) to 4 (last), or none' USING ERRCODE='22023';
   END IF;
   v_priority:=(p_change->>'priority')::smallint;
 ELSE
   v_priority:=w.priority;
 END IF;
 IF p_change ? 'assignee' THEN
   asg:=memory_work_assignee(p_change->>'assignee');
   v_person:=asg->>'person'; v_label:=asg->>'label';
 ELSE
   v_person:=w.assignee_person; v_label:=w.assignee_label;
 END IF;
 before:=memory_work_json(w.work_type,w.state,w.priority);
 after:=memory_work_json(v_type,v_state,v_priority);
 IF after=before AND v_person IS NOT DISTINCT FROM w.assignee_person AND v_label IS NOT DISTINCT FROM w.assignee_label THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('work',after,'before',before,
     'assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label),'changed','[]'::jsonb,'unchanged',true);
 END IF;
 -- A live claim holds the item's state: only its id moves it, through every channel.
 c:=memory_work_claim(m.id);
 IF v_state<>w.state AND c.memory_id IS NOT NULL AND c.claim_id::text IS DISTINCT FROM btrim(p_change->>'claim') THEN
   RAISE EXCEPTION 'This work item is claimed by % until %: only that claim moves its state. Change its type, priority or assignee, or ask its holder; a person can release the claim from the app',
     c.label,to_char(c.expires_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023',HINT='claimed';
 END IF;
 IF v_state IN ('done','cancelled') AND v_state<>w.state THEN
   v_summary:=memory_work_summary(p_change->>'summary');
 ELSIF p_change->>'summary' IS NOT NULL THEN
   RAISE EXCEPTION 'A summary goes with a state that becomes done or cancelled: nothing was changed. Put other notes in the memory itself (append)'
     USING ERRCODE='22023';
 END IF;
 fields:=ARRAY(SELECT f FROM (VALUES ('work_state',v_state<>w.state),('work_type',v_type<>w.work_type),
   ('work_priority',v_priority IS DISTINCT FROM w.priority),
   ('work_assignee',v_person IS DISTINCT FROM w.assignee_person OR v_label IS DISTINCT FROM w.assignee_label)) x(f,d) WHERE d);
 PERFORM memory_retire(m);
 UPDATE memories SET revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 UPDATE memory_work SET work_type=v_type,state=v_state,priority=v_priority,assignee_person=v_person,assignee_label=v_label WHERE memory_id=m.id;
 INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority,assignee_person,assignee_label)
 VALUES(m.id,m.revision,v_type,v_state,v_priority,v_person,v_label);
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_changed',p_principal,channel,p_client,
   jsonb_strip_nulls(jsonb_build_object('action','update','summary',v_summary))
   ||jsonb_build_object('from',before||jsonb_strip_nulls(jsonb_build_object('assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label))),
     'to',after||jsonb_strip_nulls(jsonb_build_object('assignee',memory_work_assignee_json(v_person,v_label))),'fields',to_jsonb(fields)));
 -- Finished with its claim: the claim ends with the change.
 IF c.memory_id IS NOT NULL AND v_state IN ('done','cancelled') AND v_state<>w.state THEN
   DELETE FROM memory_work_claims WHERE memory_id=m.id;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'work_released',p_principal,channel,p_client,jsonb_build_object('label',c.label,'cause',v_state));
   ended:=true;
 END IF;
 RETURN memory_reply(m,scoped)||jsonb_build_object('work',after,'before',before,'summary',v_summary,
   'assignee',memory_work_assignee_json(v_person,v_label),'claimEnded',ended,'changed',to_jsonb(fields),'unchanged',false);
END $$;

-- claim_memory_work: p_memory, an open work item, is claimed by the caller, or with p_claim its live claim renewed. The
-- answer names the claim id, the caller's to keep; no list ever shows it.
CREATE FUNCTION claim_memory_work(p_principal uuid,p_memory uuid,p_claim uuid,p_label text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; w memory_work; c memory_work_claims; v_label text; v_id uuid; moved boolean:=false;
 before jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 v_label:=CASE WHEN p_label IS NOT NULL THEN memory_work_label(p_label) END;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 SELECT * INTO w FROM memory_work WHERE memory_id=m.id FOR UPDATE;
 IF NOT FOUND THEN
   RAISE EXCEPTION 'This memory is no work item: only a work item is claimed' USING ERRCODE='22023';
 END IF;
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A work item held for review is claimed only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is on no work list: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF m.valid_until<=now() THEN
   RAISE EXCEPTION 'This memory stopped being true at %: it is history, not work to do',
     to_char(m.valid_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 IF w.state IN ('done','cancelled') THEN
   RAISE EXCEPTION 'This work item is % already: finished work is not claimed. update_work moves it back first if there is more to do',w.state
     USING ERRCODE='22023';
 END IF;
 SELECT * INTO c FROM memory_work_claims x WHERE x.memory_id=m.id FOR UPDATE;
 IF c.memory_id IS NOT NULL AND c.expires_at>now() THEN
   IF p_claim IS NOT NULL AND c.claim_id=p_claim THEN
     -- Renewed: its time starts again; nothing but the claim's row changes.
     UPDATE memory_work_claims SET renewed_at=now(),expires_at=now()+make_interval(mins=>memory_work_claim_minutes()),label=coalesce(v_label,label)
     WHERE memory_id=m.id RETURNING * INTO c;
     RETURN memory_reply(m,scoped)||jsonb_build_object('work',memory_work_json(w.work_type,w.state,w.priority),
       'assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label),
       'claim',memory_work_claim_json(c)||jsonb_build_object('id',c.claim_id,'renewed',true),'changed','[]'::jsonb,'unchanged',false);
   END IF;
   RAISE EXCEPTION 'This work item is claimed by % until %: pick another, or ask its holder; a person can release the claim from the app',
     c.label,to_char(c.expires_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023',HINT='claimed';
 END IF;
 IF p_claim IS NOT NULL THEN
   RAISE EXCEPTION 'That claim has ended (or was never this item''s): claim the item again without claim_id, and keep the new id' USING ERRCODE='22023';
 END IF;
 v_id:=gen_random_uuid();
 -- No label given: the client's name when it makes one (screened as a label), else what the channel is.
 IF v_label IS NULL AND p_client IS NOT NULL THEN
   BEGIN v_label:=memory_work_label(p_client); EXCEPTION WHEN data_exception THEN v_label:=NULL; END;
 END IF;
 v_label:=coalesce(v_label,CASE WHEN channel IN ('app','cli') THEN 'a person' ELSE 'an AI client' END);
 -- Claimed while not started: it moves to active, as a change of the memory.
 IF w.state IN ('backlog','ready') THEN
   before:=memory_work_json(w.work_type,w.state,w.priority);
   PERFORM memory_retire(m);
   UPDATE memories SET revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
   UPDATE memory_work SET state='active' WHERE memory_id=m.id RETURNING * INTO w;
   INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority,assignee_person,assignee_label)
   VALUES(m.id,m.revision,w.work_type,w.state,w.priority,w.assignee_person,w.assignee_label);
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'work_changed',p_principal,channel,p_client,
     jsonb_build_object('action','claim','from',before,'to',memory_work_json(w.work_type,w.state,w.priority),'fields',jsonb_build_array('work_state')));
   moved:=true;
 END IF;
 INSERT INTO memory_work_claims(memory_id,claim_id,label,claimed_by,channel,client,claimed_at,renewed_at,expires_at)
 VALUES(m.id,v_id,v_label,p_principal,channel,p_client,now(),now(),now()+make_interval(mins=>memory_work_claim_minutes()))
 ON CONFLICT(memory_id) DO UPDATE SET claim_id=EXCLUDED.claim_id,label=EXCLUDED.label,claimed_by=EXCLUDED.claimed_by,channel=EXCLUDED.channel,
   client=EXCLUDED.client,claimed_at=EXCLUDED.claimed_at,renewed_at=EXCLUDED.renewed_at,expires_at=EXCLUDED.expires_at
 RETURNING * INTO c;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_claimed',p_principal,channel,p_client,jsonb_build_object('label',c.label,
   'expiresAt',to_char(c.expires_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"')));
 RETURN memory_reply(m,scoped)||jsonb_build_object('work',memory_work_json(w.work_type,w.state,w.priority),
   'assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label),
   'claim',memory_work_claim_json(c)||jsonb_build_object('id',c.claim_id,'renewed',false),
   'changed',CASE WHEN moved THEN jsonb_build_array('work_state') ELSE '[]'::jsonb END,'unchanged',false);
END $$;

-- release_memory_work: the live claim on p_memory ends. Through an AI client's channel only with its id (p_claim);
-- through a person's (app, cli) by whoever may change the memory, with or without it.
CREATE FUNCTION release_memory_work(p_principal uuid,p_memory uuid,p_claim uuid,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; w memory_work; c memory_work_claims;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,NULL,scoped);
 SELECT * INTO w FROM memory_work x WHERE x.memory_id=m.id;
 IF NOT FOUND THEN
   RAISE EXCEPTION 'This memory is no work item: only a work item is claimed' USING ERRCODE='22023';
 END IF;
 SELECT * INTO c FROM memory_work_claims x WHERE x.memory_id=m.id AND x.expires_at>now() FOR UPDATE;
 IF c.memory_id IS NULL THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('work',memory_work_json(w.work_type,w.state,w.priority),
     'assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label),'claim',NULL,'changed','[]'::jsonb,'unchanged',true);
 END IF;
 IF channel NOT IN ('app','cli') AND c.claim_id IS DISTINCT FROM p_claim THEN
   RAISE EXCEPTION 'This work item is claimed by % until %: only that claim''s id releases it through an AI client; a person can release it from the app',
     c.label,to_char(c.expires_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023',HINT='claimed';
 END IF;
 DELETE FROM memory_work_claims WHERE memory_id=m.id;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_released',p_principal,channel,p_client,
   jsonb_build_object('label',c.label,'cause',CASE WHEN c.claim_id=p_claim THEN 'released' ELSE 'person' END));
 RETURN memory_reply(m,scoped)||jsonb_build_object('work',memory_work_json(w.work_type,w.state,w.priority),
   'assignee',memory_work_assignee_json(w.assignee_person,w.assignee_label),'claim',memory_work_claim_json(c),'changed','[]'::jsonb,'unchanged',false);
END $$;

-- 079's undo_memory, the same but for the assignee: a revision's assignee comes back with its other work properties,
-- and a difference in it is a difference in work.
CREATE OR REPLACE FUNCTION undo_memory(p_principal uuid,p_memory uuid,p_to integer,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; n memories; v memory_versions; target integer; floor integer; kept integer;
 project uuid; changed text[]; detail jsonb; ds memory_decision_states; dt memory_decision_states; ws memory_work; wt memory_work_states;
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
 -- 065: the revision's mark as a standing instruction (none in a state kept without one).
 n.standing:=coalesce((v.state->>'standing')::boolean,false);
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
 -- 079: its work properties now, against those at the target (every column null where it has none); 081: its assignee too.
 SELECT * INTO ws FROM memory_work WHERE memory_id=m.id;
 wt:=memory_work_at(m.id,target);
 changed:=ARRAY(SELECT f FROM (VALUES
   ('content',n.content IS DISTINCT FROM m.content),('tags',n.tags IS DISTINCT FROM m.tags),('status',n.status IS DISTINCT FROM m.status),
   ('kind',n.kind IS DISTINCT FROM m.kind),('importance',n.importance IS DISTINCT FROM m.importance),
   ('volatility',n.volatility IS DISTINCT FROM m.volatility),('project',n.project_id IS DISTINCT FROM m.project_id),
   ('capsule',n.capsule IS DISTINCT FROM m.capsule OR n.capsule_slot IS DISTINCT FROM m.capsule_slot),
   ('validity',n.valid_from IS DISTINCT FROM m.valid_from OR n.valid_until IS DISTINCT FROM m.valid_until),
   ('outcome',dt.memory_id IS NOT NULL AND (dt.outcome,dt.rearms,dt.review_at) IS DISTINCT FROM (ds.outcome,ds.rearms,ds.review_at)),
   -- 061: the due date.
   ('due',n.due_at IS DISTINCT FROM m.due_at OR n.due_kind IS DISTINCT FROM m.due_kind OR n.due_source IS DISTINCT FROM m.due_source),
   -- 065: the mark.
   ('standing',n.standing IS DISTINCT FROM m.standing),
   -- 079 and 081: the work properties.
   ('work',wt.work_type IS DISTINCT FROM ws.work_type OR wt.state IS DISTINCT FROM ws.state OR wt.priority IS DISTINCT FROM ws.priority
     OR wt.assignee_person IS DISTINCT FROM ws.assignee_person OR wt.assignee_label IS DISTINCT FROM ws.assignee_label)) c(f,d)
   WHERE d);
 -- A capsule slot is a person's step (039): an undo through an AI client's channel that would put a memory into a slot,
 -- take it out or move it is refused, and writes nothing.
 IF channel='mcp' AND 'capsule'=ANY(changed) THEN
   RAISE EXCEPTION 'This undo would change a capsule slot, which a person does from the app or the terminal, never an AI client'
     USING ERRCODE='42501';
 END IF;
 -- A memory a capture made is confirmed only by a person (067's set_memory_status, whatever became of it since): an undo
 -- through an AI client's channel that would make one canonical is refused, and writes nothing. 067 adds the channel.
 IF channel='mcp' AND 'status'=ANY(changed) AND n.status='canonical'
   AND EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.event='created' AND e.channel='capture') THEN
   RAISE EXCEPTION 'This undo would confirm a memory captured from outside: only a person confirms it, in the app''s Memories view or with rhythm set-status at the terminal'
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
 -- 079: properties the target did not have come off first, so tags it had then (the tag task among them) may come back.
 IF 'work'=ANY(changed) AND wt.work_type IS NULL THEN
   DELETE FROM memory_work WHERE memory_id=m.id;
 END IF;
 UPDATE memories SET content=n.content,tags=n.tags,status=n.status,kind=n.kind,importance=n.importance,volatility=n.volatility,
   project_id=n.project_id,capsule=n.capsule,capsule_slot=n.capsule_slot,valid_from=n.valid_from,valid_until=n.valid_until,
   hold=n.hold,hold_score=n.hold_score,due_at=n.due_at,due_kind=n.due_kind,due_source=n.due_source,standing=n.standing,
   revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO n;
 IF 'outcome'=ANY(changed) THEN
   INSERT INTO memory_decision_states(memory_id,revision,outcome,rearms,review_at) VALUES(n.id,n.revision,dt.outcome,dt.rearms,dt.review_at);
 END IF;
 -- 079: the target's properties, put back once the row holds the target's tags, and written at this revision; 081: with
 -- its assignee, unless that person is no longer current, when the assignee comes back empty.
 IF 'work'=ANY(changed) THEN
   IF wt.assignee_person IS NOT NULL AND NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=wt.assignee_person AND s.status='current') THEN
     wt.assignee_person:=NULL;
   END IF;
   IF wt.work_type IS NOT NULL THEN
     INSERT INTO memory_work(memory_id,work_type,state,priority,assignee_person,assignee_label)
     VALUES(n.id,wt.work_type,wt.state,wt.priority,wt.assignee_person,wt.assignee_label)
     ON CONFLICT(memory_id) DO UPDATE SET work_type=EXCLUDED.work_type,state=EXCLUDED.state,priority=EXCLUDED.priority,
       assignee_person=EXCLUDED.assignee_person,assignee_label=EXCLUDED.assignee_label;
   END IF;
   INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority,assignee_person,assignee_label)
   VALUES(n.id,n.revision,wt.work_type,wt.state,wt.priority,wt.assignee_person,wt.assignee_label);
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

-- An assignee as an export names it ({kind, name}), as it may come in here: a person only when a current person of that
-- id is here, a label only when memory_work_label takes it; else none.
CREATE FUNCTION memory_work_imported_assignee(p jsonb) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF jsonb_typeof(p) IS DISTINCT FROM 'object' THEN RETURN NULL; END IF;
 IF p->>'kind'='person' AND EXISTS(SELECT 1 FROM subjects s WHERE s.id=p->>'name' AND s.kind='person' AND s.status='current') THEN
   RETURN jsonb_build_object('person',p->>'name');
 END IF;
 IF p->>'kind'='agent' THEN
   BEGIN RETURN jsonb_build_object('label',memory_work_label(p->>'name'));
   EXCEPTION WHEN data_exception THEN RETURN NULL; END;
 END IF;
 RETURN NULL;
END $$;

-- 079's import_memory_work, with an assignee on the line and on each state ({kind, name}): a person only when a current
-- person of that id is here, a label only when it is one (memory_work_label's rule), else none. Everything else as 079's.
CREATE OR REPLACE FUNCTION import_memory_work(p_origin jsonb,p_memory jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_id uuid; w jsonb; states jsonb; v_revision integer; v_floor integer; s memory_work_states;
BEGIN
 BEGIN
   w:=p_memory->'work';
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(w) IS DISTINCT FROM 'object'
     OR jsonb_typeof(w->'type') IS DISTINCT FROM 'string' OR jsonb_typeof(w->'state') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(w->'priority','null'::jsonb)) NOT IN ('number','null')
     OR jsonb_typeof(coalesce(w->'assignee','null'::jsonb)) NOT IN ('object','null')
     OR jsonb_typeof(coalesce(w->'states','[]'::jsonb))<>'array' OR jsonb_array_length(coalesce(w->'states','[]'::jsonb))>1000
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(w->'states','[]'::jsonb)) x WHERE jsonb_typeof(x)<>'object'
       OR jsonb_typeof(x->'revision') IS DISTINCT FROM 'number'
       OR jsonb_typeof(coalesce(x->'type','null'::jsonb)) NOT IN ('string','null')
       OR jsonb_typeof(coalesce(x->'state','null'::jsonb)) NOT IN ('string','null')
       OR jsonb_typeof(coalesce(x->'priority','null'::jsonb)) NOT IN ('number','null')
       OR jsonb_typeof(coalesce(x->'assignee','null'::jsonb)) NOT IN ('object','null')) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid;
   IF v_id IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   -- Only a memory this import added: its imported row is of this transaction (memory_events.at is now(), its start).
   IF NOT memory_imported(p_origin,'memory',v_id) OR NOT EXISTS(SELECT 1 FROM memories m WHERE m.id=v_id)
     OR NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='imported' AND e.at=now()) THEN
     RETURN jsonb_build_object('outcome','not_imported');
   END IF;
   IF EXISTS(SELECT 1 FROM memory_work k WHERE k.memory_id=v_id) THEN RETURN jsonb_build_object('outcome','present'); END IF;
   IF EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=v_id) OR EXISTS(SELECT 1 FROM memories m WHERE m.id=v_id AND m.standing) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   SELECT m.revision INTO v_revision FROM memories m WHERE m.id=v_id;
   SELECT coalesce(min(v.revision),v_revision) INTO v_floor FROM memory_versions v WHERE v.memory_id=v_id;
   states:=coalesce(nullif(w->'states','[]'::jsonb),jsonb_build_array(jsonb_build_object('revision',v_floor,'type',w->'type',
     'state',w->'state','priority',w->'priority','assignee',w->'assignee')));
   INSERT INTO memory_work(memory_id,work_type,state,priority) VALUES(v_id,w->>'type',w->>'state',(w->>'priority')::smallint);
   -- The newest state at or below the floor, written at the floor, and those above it up to the memory's revision, each
   -- with its assignee as it may come in here.
   INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority,assignee_person,assignee_label)
   SELECT v_id,greatest(y.rev,v_floor),y.work_type,y.state,y.priority,
     CASE WHEN y.work_type IS NOT NULL THEN y.asg->>'person' END,CASE WHEN y.work_type IS NOT NULL THEN y.asg->>'label' END
   FROM (SELECT (x->>'revision')::integer AS rev,x->>'type' AS work_type,x->>'state' AS state,(x->>'priority')::smallint AS priority,
       memory_work_imported_assignee(x->'assignee') AS asg FROM jsonb_array_elements(states) x) y
   WHERE y.rev<=v_revision AND (y.rev>=v_floor
     OR y.rev=(SELECT max((z->>'revision')::integer) FROM jsonb_array_elements(states) z WHERE (z->>'revision')::integer<=v_floor));
   -- The states end at what the line says the item is now, or nothing of it comes in.
   s:=memory_work_at(v_id,v_revision);
   IF s.work_type IS DISTINCT FROM w->>'type' OR s.state IS DISTINCT FROM w->>'state' OR s.priority IS DISTINCT FROM (w->>'priority')::smallint THEN
     RAISE EXCEPTION 'The states of a work item end at its properties' USING ERRCODE='22023';
   END IF;
   UPDATE memory_work SET assignee_person=s.assignee_person,assignee_label=s.assignee_label WHERE memory_id=v_id;
   RETURN jsonb_build_object('outcome','added','id',v_id);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so each replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_claim_minutes() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_claim(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_claim_json(memory_work_claims) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_assignee_json(text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_label(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_assignee(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_checked(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION remember_memory_work(uuid,jsonb,jsonb,jsonb,jsonb,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_memory_work(uuid,uuid,jsonb,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION update_memory_work(uuid,uuid,jsonb,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION claim_memory_work(uuid,uuid,uuid,text,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION release_memory_work(uuid,uuid,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_memory(uuid,uuid,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_imported_assignee(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_work(jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_work_claim_minutes(),memory_work_claim(uuid),memory_work_claim_json(memory_work_claims),
 memory_work_assignee_json(text,text),memory_work_label(text),memory_work_assignee(text),memory_work_checked(jsonb),
 remember_memory_work(uuid,jsonb,jsonb,jsonb,jsonb,text,text,text),set_memory_work(uuid,uuid,jsonb,integer,text,text),
 update_memory_work(uuid,uuid,jsonb,integer,text,text),claim_memory_work(uuid,uuid,uuid,text,integer,text,text),
 release_memory_work(uuid,uuid,uuid,text,text),undo_memory(uuid,uuid,integer,integer,text,text),memory_work_imported_assignee(jsonb),
 import_memory_work(jsonb,jsonb) FROM PUBLIC;
