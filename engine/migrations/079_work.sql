-- Work items for native memories (memory.work, off by default; docs/dev/memory.md sections 5, 7.1 and 9): a project's
-- backlog, kept as memories. A work item is no new kind of record: it is a memory, with its one id, its words, its
-- project, its links, its history and its place on the map, that also has work properties here: what kind of work it
-- is (task, bug, feature or improvement), where it stands (backlog, ready, active, review, done or cancelled) and, when
-- someone ranked it, a priority (1 first to 4 last). What it must do, and how to tell it is done, stay in the memory's
-- own words, so the review hold (036) screens them as it screens every memory; no column here holds words.
--
-- A work item is not a personal commitment. It never carries the tag task, which makes a memory an open loop the person
-- owes (058): giving work properties to a memory tagged task is refused, and so is adding that tag to a work item
-- (memories_work_no_task, memory_work_no_task: two triggers, so no write path brings the two together). So 058's
-- memory_open_loop never answers for a work item, and list_loops, the brief and resolve are as they were. Nor is it a
-- decision or a standing instruction: an existing one is refused.
--
-- A work item belongs to a project when it is made one: a new one names its project, and an existing memory must be in
-- one. A project deleted later (035) still takes its memories out of it, a work item among them: that one keeps its
-- properties and is listed with no project until it is given one.
--
-- Two tables. memory_work holds what an item is now, one row a memory, which the list filters read. memory_work_states
-- holds what it was from each revision on, as 060 keeps a decision's states: a row for each revision at which the
-- properties were set or changed, never changed once written, so the properties at any revision are the newest row at or
-- before it (memory_work_at) and every other change of the memory (an edit, a status, a share) carries them on. A row
-- with no type says the memory was no work item from that revision on, which only an undo writes. Both go with their
-- memory when it is deleted for good; in the trash a memory keeps its rows, is in no list, and comes back a work item.
--
-- The writers, each a change like any other under 033's author lock:
-- - remember_memory_work writes a new memory as create_memory does (a draft in the caller's scope, screened for a hold,
--   one audit row), or as 061's remember_memory_due does when the call gives a due date, with its properties in the same
--   transaction and an audit row of its own (work_set). Its idempotency key covers the properties. A due date is the
--   memory's own (061's columns): there is none here to keep beside it.
-- - set_memory_work gives properties to a memory that is here already: the state it replaces kept as a version
--   (memory_retire), the revision moved, one audit row (work_changed, action set). A repeat with the same properties
--   answers unchanged; no idempotency key.
-- - update_memory_work changes state, type or priority: a version, the revision, one audit row (work_changed, action
--   update, naming the properties before and after). A state that becomes done or cancelled takes a summary, 1 to 280
--   characters, kept in that audit row and nowhere else; it is listed to AI clients with the item, so one that reads
--   like instructions to an AI client is refused (036's screening), as 058 refuses such a name. A repeat answers
--   unchanged; no idempotency key. It writes no words of the memory, so it is not counted toward a burst (045).
-- - undo_memory (065's, replaced below) compares the properties at the revision it goes back to with those now, counts a
--   difference as a field it changed (work), and writes what it brought back at its own revision: an undo of a change
--   takes it back, an undo of the change that made a memory a work item makes it an ordinary memory again, and an undo
--   of that undo brings the properties back.
--
-- The list (memory_work_listed, the one definition every count and list reads): a work item that is live (in the
-- catalog, not in the trash), not marked wrong and its window open. One held for review is listed without its words, as
-- list_recent lists it. Who reads which is the caller's read predicate, on top: a backlog is its scope's, not one
-- person's, so every reader of the memory is served it, and only who may change the memory changes it.
--
-- Two predicates change by one line each, and only for a memory with a row here:
-- - memory_digest_eligible (065's): a work item is no digest's source. It is a thing to do, not a fact to roll up.
-- - memory_due (069's): a work item that is done or cancelled keeps its due date and leaves the due list, as a finished
--   task does (061).
-- Recall, links on save, the duplicate and contradiction checks, the stale queue and the map read a work item as any
-- memory.
--
-- An export and import keep them: rhythm export writes a work item's properties and its states from the reader's
-- history floor on, and import_memory_work, called by rhythm import for each memory it has just added, adds them there.
-- A summary is the audit trail's, which no export carries.
--
-- Nothing calls these but the memory tools create_work, list_work and update_work, made only with memory.work on beside
-- memory.tools, rhythm import, undo_memory and tests: with none called no memory has a row here, no audit row is
-- work_set or work_changed, every undo compares the same fields with the same answers, both predicates answer as before
-- and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision; 23505 an idempotency key used for another
-- request; 54000 text over the limit; 22023 invalid input (also a memory with no project, one tagged task, a decision, a
-- standing instruction, a memory that is no work item, one whose window has closed, and a missing or refused summary)
-- and, with a hint the engine reads, held (a memory held for review) and retracted (a deprecated memory). A refused call
-- writes nothing.

-- What a work item is now. No subject of its own: it is its memory's, wherever a share moves it.
CREATE TABLE memory_work(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 work_type text NOT NULL CHECK(work_type IN ('task','bug','feature','improvement')),
 state text NOT NULL CHECK(state IN ('backlog','ready','active','review','done','cancelled')),
 -- 1 first to 4 last; none when nobody ranked it.
 priority smallint CHECK(priority BETWEEN 1 AND 4),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX memory_work_state ON memory_work(state,priority);
REVOKE ALL ON memory_work FROM PUBLIC;

-- A work item's properties from a revision on. A row for each revision at which they were set or changed (the memory's
-- writing, a change, an undo that changed them); never changed once written. No type: no work item from there on.
CREATE TABLE memory_work_states(
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 revision integer NOT NULL CHECK(revision>=1),
 work_type text CHECK(work_type IN ('task','bug','feature','improvement')),
 state text CHECK(state IN ('backlog','ready','active','review','done','cancelled')),
 priority smallint CHECK(priority BETWEEN 1 AND 4),
 recorded_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(memory_id,revision),
 CHECK((work_type IS NULL)=(state IS NULL)),
 CHECK(work_type IS NOT NULL OR priority IS NULL)
);
REVOKE ALL ON memory_work_states FROM PUBLIC;

-- The list of checked tables in force, with these two added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_work','memory_work_states']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The longest summary a finished work item takes. MemoryWork.SummaryMax says the same, and a test keeps them equal.
CREATE FUNCTION memory_work_summary_chars() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 280 $$;

-- Work properties as answers and audit rows name them; null for none.
CREATE FUNCTION memory_work_json(p_type text,p_state text,p_priority smallint) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
 SELECT CASE WHEN p_type IS NOT NULL THEN jsonb_build_object('type',p_type,'state',p_state,'priority',p_priority) END
$$;

-- A work item's properties at revision p_revision: the newest row at or before it; every column null when it has none.
CREATE FUNCTION memory_work_at(p_memory uuid,p_revision integer) RETURNS memory_work_states LANGUAGE sql STABLE AS $$
 SELECT s.* FROM memory_work_states s WHERE s.memory_id=p_memory AND s.revision<=p_revision ORDER BY s.revision DESC LIMIT 1
$$;

-- The one definition of the work list: whether p_memory, as it is now, is a work item a list shows. Live (its own catalog
-- row current, not in the trash), not marked wrong and its window open. Every count and list reads it.
CREATE FUNCTION memory_work_listed(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memories m JOIN memory_work w ON w.memory_id=m.id
   WHERE m.id=p_memory AND m.status<>'deprecated' AND (m.valid_until IS NULL OR m.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
     AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
       AND r.status='current'))
$$;

-- Work properties a write takes (p_work: type, state and priority), checked and whole: a known type; a known state,
-- backlog when none is given; a priority 1 to 4 or none.
CREATE FUNCTION memory_work_checked(p_work jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_work IS NULL OR jsonb_typeof(p_work)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_work) k WHERE k NOT IN ('type','state','priority'))
   OR jsonb_typeof(p_work->'type') IS DISTINCT FROM 'string'
   OR jsonb_typeof(coalesce(p_work->'state','null'::jsonb)) NOT IN ('string','null')
   OR jsonb_typeof(coalesce(p_work->'priority','null'::jsonb)) NOT IN ('number','null') THEN
   RAISE EXCEPTION 'A work item takes a type, a state and a priority' USING ERRCODE='22023';
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

-- The summary a finished work item takes: trimmed, 1 to memory_work_summary_chars() characters, no control character,
-- and nothing that reads like instructions to an AI client (036's screening), since it is listed to every AI client that
-- reads the item and no hold covers an audit row.
CREATE FUNCTION memory_work_summary(p_text text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE t text:=btrim(coalesce(p_text,''));
BEGIN
 IF t='' THEN
   RAISE EXCEPTION 'A work item that becomes done or cancelled takes a summary: one or two sentences on what was done, or why it was dropped. Nothing was changed'
     USING ERRCODE='22023';
 END IF;
 IF char_length(t)>memory_work_summary_chars() OR t ~ '[[:cntrl:]]' THEN
   RAISE EXCEPTION 'A summary is at most % characters on one line: nothing was changed. Put the detail in the memory itself (append)',
     memory_work_summary_chars() USING ERRCODE='22023';
 END IF;
 IF memory_screen(t)->>'reason' IS NOT NULL THEN
   RAISE EXCEPTION 'The summary reads like instructions to an AI client: say only what was done or why it was dropped. Nothing was changed'
     USING ERRCODE='22023';
 END IF;
 RETURN t;
END $$;

-- A work item never carries the tag task (058: that tag makes an open loop the person owes). Two triggers, one on each
-- table, so whichever write would bring the two together is refused: properties for a memory tagged task, and the tag
-- for a memory with properties.
CREATE FUNCTION memory_work_no_task() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM memories m WHERE m.id=NEW.memory_id AND 'task'=ANY(m.tags)) THEN
   RAISE EXCEPTION 'This memory is tagged task, which makes it a personal task on the open loops: a work item is not one. Take the tag off first (resolve_loop not_a_task), or save the work as a memory of its own'
     USING ERRCODE='22023';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memory_work_no_task BEFORE INSERT ON memory_work FOR EACH ROW EXECUTE FUNCTION memory_work_no_task();

CREATE FUNCTION memories_work_no_task() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=NEW.id) THEN
   RAISE EXCEPTION 'A work item can''t also be a personal task: nothing was changed. Leave the tag task and any #task out; its state says where the work stands (update_work)'
     USING ERRCODE='22023';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memories_work_no_task BEFORE UPDATE OF tags ON memories
 FOR EACH ROW WHEN ('task'=ANY(NEW.tags) AND NOT 'task'=ANY(OLD.tags)) EXECUTE FUNCTION memories_work_no_task();

-- remember_memory_work: a new memory from p_memory (create_memory's fields, MemoryInput's) that is a work item of the
-- project it names, with p_work's {type, state, priority} (state backlog when none; never done or cancelled, which a
-- change makes with its summary) and, when p_due is given, a due date (061's remember_memory_due, with p_asked, what the
-- caller asked of it). Written as create_memory writes it, with its properties and their first state in the same
-- transaction and one audit row of its own. The key is the caller's, over the memory, the properties and the due date
-- as asked.
CREATE FUNCTION remember_memory_work(p_principal uuid,p_memory jsonb,p_work jsonb,p_due jsonb,p_asked jsonb,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE work jsonb; asked jsonb; hash text; replay jsonb; result jsonb; m memories;
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
 hash:=memory_request_hash('create',NULL,jsonb_strip_nulls(p_memory)||jsonb_build_object('work',work)||asked,NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 result:=CASE WHEN p_due IS NULL THEN create_memory(p_principal,jsonb_strip_nulls(p_memory),p_channel,p_client,NULL)
   ELSE remember_memory_due(p_principal,p_memory,p_due,p_asked,NULL,NULL,p_channel,p_client,NULL) END;
 SELECT * INTO m FROM memories WHERE id=(result->>'id')::uuid;
 IF 'task'=ANY(m.tags) THEN
   RAISE EXCEPTION 'A work item can''t also be a personal task: nothing was saved. Leave the tag task and any #task out; its state says where the work stands'
     USING ERRCODE='22023';
 END IF;
 INSERT INTO memory_work(memory_id,work_type,state,priority) VALUES(m.id,work->>'type',work->>'state',(work->>'priority')::smallint);
 INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority)
 VALUES(m.id,m.revision,work->>'type',work->>'state',(work->>'priority')::smallint);
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_set',p_principal,memory_channel(p_principal,p_channel,p_client),p_client,
   jsonb_build_object('work',work,'fields',jsonb_build_array('work')));
 result:=result||jsonb_build_object('work',work);
 RETURN memory_answered(p_principal,p_key,'create',hash,m.id,result);
END $$;

-- set_memory_work: p_memory, a memory that is here already, becomes a work item with p_work's {type, state, priority}, by
-- whoever may change it, as a change of it (a version kept, the revision moved, one audit row, work_changed). It must be
-- in a project, and no personal task, decision or standing instruction. A repeat with the same properties writes nothing
-- and answers unchanged; other properties for a memory that is one already are update_memory_work's.
CREATE FUNCTION set_memory_work(p_principal uuid,p_memory uuid,p_work jsonb,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; work jsonb; w memory_work;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 work:=memory_work_checked(p_work);
 IF work->>'state' IN ('done','cancelled') THEN
   RAISE EXCEPTION 'A new work item starts backlog, ready, active or review: nothing was changed. update_work makes it done or cancelled, with a summary'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 SELECT * INTO w FROM memory_work WHERE memory_id=m.id;
 IF FOUND THEN
   IF memory_work_json(w.work_type,w.state,w.priority)=work THEN
     RETURN memory_reply(m,scoped)||jsonb_build_object('work',work,'changed','[]'::jsonb,'unchanged',true);
   END IF;
   RAISE EXCEPTION 'This memory is a work item already (%, %): update_work changes its state, type or priority',w.work_type,w.state
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
 -- A memory tagged task is refused by memory_work_no_task, below, in its own words.
 PERFORM memory_retire(m);
 UPDATE memories SET revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_work(memory_id,work_type,state,priority) VALUES(m.id,work->>'type',work->>'state',(work->>'priority')::smallint);
 INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority)
 VALUES(m.id,m.revision,work->>'type',work->>'state',(work->>'priority')::smallint);
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_changed',p_principal,channel,p_client,
   jsonb_build_object('action','set','to',work,'fields',jsonb_build_array('work')));
 RETURN memory_reply(m,scoped)||jsonb_build_object('work',work,'changed',jsonb_build_array('work'),'unchanged',false);
END $$;

-- update_memory_work: the work item p_memory changes its state, type or priority (p_change: each only when named; a
-- priority named null comes off), by whoever may change it, as a change of it (a version kept, the revision moved, one
-- audit row, work_changed, naming the properties before and after). A state that becomes done or cancelled takes
-- p_change's summary, kept in that audit row; a summary with any other change is refused. A repeat writes nothing and
-- answers unchanged.
CREATE FUNCTION update_memory_work(p_principal uuid,p_memory uuid,p_change jsonb,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; w memory_work; v_type text; v_state text; v_priority smallint; v_summary text;
 before jsonb; after jsonb; fields text[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_change IS NULL OR jsonb_typeof(p_change)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_change) k WHERE k NOT IN ('type','state','priority','summary'))
   OR NOT (p_change ? 'type' OR p_change ? 'state' OR p_change ? 'priority')
   OR jsonb_typeof(coalesce(p_change->'type','""'::jsonb))<>'string' OR jsonb_typeof(coalesce(p_change->'state','""'::jsonb))<>'string'
   OR jsonb_typeof(coalesce(p_change->'priority','null'::jsonb)) NOT IN ('number','null')
   OR jsonb_typeof(coalesce(p_change->'summary','null'::jsonb)) NOT IN ('string','null') THEN
   RAISE EXCEPTION 'A work change names a state, a type or a priority, and a summary when the state becomes done or cancelled'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 SELECT * INTO w FROM memory_work WHERE memory_id=m.id;
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
 before:=memory_work_json(w.work_type,w.state,w.priority);
 after:=memory_work_json(v_type,v_state,v_priority);
 IF after=before THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('work',after,'before',before,'changed','[]'::jsonb,'unchanged',true);
 END IF;
 IF v_state IN ('done','cancelled') AND v_state<>w.state THEN
   v_summary:=memory_work_summary(p_change->>'summary');
 ELSIF p_change->>'summary' IS NOT NULL THEN
   RAISE EXCEPTION 'A summary goes with a state that becomes done or cancelled: nothing was changed. Put other notes in the memory itself (append)'
     USING ERRCODE='22023';
 END IF;
 fields:=ARRAY(SELECT f FROM (VALUES ('work_state',v_state<>w.state),('work_type',v_type<>w.work_type),
   ('work_priority',v_priority IS DISTINCT FROM w.priority)) c(f,d) WHERE d);
 PERFORM memory_retire(m);
 UPDATE memories SET revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 UPDATE memory_work SET work_type=v_type,state=v_state,priority=v_priority WHERE memory_id=m.id;
 INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority) VALUES(m.id,m.revision,v_type,v_state,v_priority);
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'work_changed',p_principal,channel,p_client,
   jsonb_strip_nulls(jsonb_build_object('action','update','summary',v_summary))
   ||jsonb_build_object('from',before,'to',after,'fields',to_jsonb(fields)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('work',after,'before',before,'summary',v_summary,'changed',to_jsonb(fields),'unchanged',false);
END $$;

-- 065's undo_memory, the same but for work properties: the properties at the revision it goes back to (none when the
-- memory was no work item then), when they differ from those now, are a field it changes (work), and what it brought
-- back is written at the undo's own revision. Properties that come off are taken off before the memory's row is written
-- and those that come back are put back after it, so the two triggers above see a tag and a row that go together. It
-- keeps 060's, 061's and 065's lines as they are.
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
 -- 079: its work properties now, against those at the target (every column null where it has none).
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
   -- 079: the work properties.
   ('work',wt.work_type IS DISTINCT FROM ws.work_type OR wt.state IS DISTINCT FROM ws.state OR wt.priority IS DISTINCT FROM ws.priority)) c(f,d)
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
 -- 079: the target's properties, put back once the row holds the target's tags, and written at this revision.
 IF 'work'=ANY(changed) THEN
   IF wt.work_type IS NOT NULL THEN
     INSERT INTO memory_work(memory_id,work_type,state,priority) VALUES(n.id,wt.work_type,wt.state,wt.priority)
     ON CONFLICT(memory_id) DO UPDATE SET work_type=EXCLUDED.work_type,state=EXCLUDED.state,priority=EXCLUDED.priority;
   END IF;
   INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority) VALUES(n.id,n.revision,wt.work_type,wt.state,wt.priority);
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

-- 065's memory_digest_eligible, the same but for one line: a work item is left out. It is a thing to do, with a state of
-- its own, not a fact to roll up.
CREATE OR REPLACE FUNCTION memory_digest_eligible(p_subject text,p_project uuid,p_tag text)
RETURNS TABLE(id uuid,revision integer,created_at timestamptz) LANGUAGE sql STABLE AS $$
 SELECT m.id,m.revision,m.created_at FROM memories m
 JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
 CROSS JOIN memory_digest_bounds() b
 CROSS JOIN LATERAL (SELECT coalesce(sum(c.recalled),0) AS n FROM recall_counts c WHERE c.resource_id=m.id AND c.subject_id=p_subject) k
 WHERE m.subject_id=p_subject AND (p_project IS NULL)<>(p_tag IS NULL)
   AND (p_project IS NULL OR m.project_id=p_project
     OR m.tags && ARRAY(SELECT a.alias FROM memory_project_aliases a JOIN memory_projects p ON p.id=a.project_id
       WHERE a.project_id=p_project AND p.subject_id=p_subject))
   AND (p_tag IS NULL OR m.tags @> ARRAY[p_tag])
   AND m.hold IS NULL AND m.source<>'system' AND m.capsule IS NULL AND m.status<>'deprecated'
   AND NOT m.standing
   AND NOT EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=m.id)
   AND (m.valid_until IS NULL OR m.valid_until>now())
   AND (m.importance IS NULL OR m.importance<b.importance)
   AND (k.n=0 OR k.n<b.recalls AND m.created_at<now()-b.age)
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM memory_retraction_flags f WHERE f.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM made_links l JOIN memories d ON d.id=l.source_id AND d.subject_id=p_subject
     WHERE l.target_id=m.id AND l.type='drawn_from' AND l.lifecycle='active' AND memory_digest_live(d.id))
 ORDER BY m.created_at DESC,m.id DESC
$$;

-- 069's memory_due, with one line added: a work item that is done or cancelled keeps its date and leaves the due list,
-- as a finished task does.
CREATE OR REPLACE FUNCTION memory_due(p_memory uuid) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT m.due_at FROM memories m
 WHERE m.id=p_memory AND m.due_at IS NOT NULL AND NOT 'task:done'=ANY(m.tags)
   AND m.status<>'deprecated' AND m.hold IS NULL AND (m.valid_until IS NULL OR m.valid_until>now())
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM memory_work w WHERE w.memory_id=m.id AND w.state IN ('done','cancelled'))
   AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
     AND r.status='current')
$$;

-- import_memory_work: the work properties of one memory of an export (its line's work: a type, a state and maybe a
-- priority, and its states, each a revision with the properties from there on, none for a stretch it was no work item),
-- for the memory rhythm import has just added from it (043's and 054's import_memory answered added, in this
-- transaction), at the revisions the import kept, as 060's import_memory_decision keeps a decision's: from the oldest
-- version it kept (or the memory's revision, with none kept) on, the properties at that floor written at the floor. A
-- line with no states is written as one state at the floor. Answers added; present (the memory has properties here
-- already, left as they are); not_imported (no memory this origin's import added by that id in this transaction is
-- here); or refused, invalid (anything the tables do not take, a memory tagged task, a decision or a standing
-- instruction, or states that do not end at the properties the line gives).
CREATE FUNCTION import_memory_work(p_origin jsonb,p_memory jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_id uuid; w jsonb; states jsonb; v_revision integer; v_floor integer; s memory_work_states;
BEGIN
 BEGIN
   w:=p_memory->'work';
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(w) IS DISTINCT FROM 'object'
     OR jsonb_typeof(w->'type') IS DISTINCT FROM 'string' OR jsonb_typeof(w->'state') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(w->'priority','null'::jsonb)) NOT IN ('number','null')
     OR jsonb_typeof(coalesce(w->'states','[]'::jsonb))<>'array' OR jsonb_array_length(coalesce(w->'states','[]'::jsonb))>1000
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(w->'states','[]'::jsonb)) x WHERE jsonb_typeof(x)<>'object'
       OR jsonb_typeof(x->'revision') IS DISTINCT FROM 'number'
       OR jsonb_typeof(coalesce(x->'type','null'::jsonb)) NOT IN ('string','null')
       OR jsonb_typeof(coalesce(x->'state','null'::jsonb)) NOT IN ('string','null')
       OR jsonb_typeof(coalesce(x->'priority','null'::jsonb)) NOT IN ('number','null')) THEN
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
   -- The import's own floor: the oldest version import_memory kept here, or the memory's revision when it kept none.
   SELECT coalesce(min(v.revision),v_revision) INTO v_floor FROM memory_versions v WHERE v.memory_id=v_id;
   states:=coalesce(nullif(w->'states','[]'::jsonb),jsonb_build_array(jsonb_build_object('revision',v_floor,'type',w->'type',
     'state',w->'state','priority',w->'priority')));
   INSERT INTO memory_work(memory_id,work_type,state,priority) VALUES(v_id,w->>'type',w->>'state',(w->>'priority')::smallint);
   -- The newest state at or below the floor, written at the floor, and those above it up to the memory's revision.
   INSERT INTO memory_work_states(memory_id,revision,work_type,state,priority)
   SELECT v_id,greatest((x->>'revision')::integer,v_floor),x->>'type',x->>'state',(x->>'priority')::smallint
   FROM jsonb_array_elements(states) x
   WHERE (x->>'revision')::integer<=v_revision AND ((x->>'revision')::integer>=v_floor
     OR (x->>'revision')::integer=(SELECT max((y->>'revision')::integer) FROM jsonb_array_elements(states) y
       WHERE (y->>'revision')::integer<=v_floor));
   -- The states end at what the line says the item is now, or nothing of it comes in.
   s:=memory_work_at(v_id,v_revision);
   IF s.work_type IS DISTINCT FROM w->>'type' OR s.state IS DISTINCT FROM w->>'state' OR s.priority IS DISTINCT FROM (w->>'priority')::smallint THEN
     RAISE EXCEPTION 'The states of a work item end at its properties' USING ERRCODE='22023';
   END IF;
   RETURN jsonb_build_object('outcome','added','id',v_id);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so each replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_summary_chars() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_json(text,text,smallint) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_at(uuid,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_listed(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_checked(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_summary(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_work_no_task() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memories_work_no_task() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION remember_memory_work(uuid,jsonb,jsonb,jsonb,jsonb,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_memory_work(uuid,uuid,jsonb,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION update_memory_work(uuid,uuid,jsonb,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_memory(uuid,uuid,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_eligible(text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_due(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_work(jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_work_summary_chars(),memory_work_json(text,text,smallint),memory_work_at(uuid,integer),
 memory_work_listed(uuid),memory_work_checked(jsonb),memory_work_summary(text),memory_work_no_task(),memories_work_no_task(),
 remember_memory_work(uuid,jsonb,jsonb,jsonb,jsonb,text,text,text),set_memory_work(uuid,uuid,jsonb,integer,text,text),
 update_memory_work(uuid,uuid,jsonb,integer,text,text),undo_memory(uuid,uuid,integer,integer,text,text),
 memory_digest_eligible(text,uuid,text),memory_due(uuid),import_memory_work(jsonb,jsonb) FROM PUBLIC;
