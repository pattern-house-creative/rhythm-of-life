-- Open loops and two-way commitments for native memories (SB-P5-09 in docs/dev/second-brain-parity/backlog.md; story
-- 12; ledger rows INT-11, INT-12, API-31 and API-32; docs/dev/memory.md sections 5 and 9): what a person said they would
-- do (you owe) and what someone else promised them (owed to you), each listed by one predicate that every count and list
-- reads, and closed with a resolve action that is a change like any other: a version kept, the revision moved, one audit
-- row, and undo puts it back. Adapted from Second Brain's open-loops queue, two-way commitments and their resolve actions
-- (src/memory/loops.ts, src/commitments/direction.ts, src/capture/t7-capture.ts and src/memory/actions.ts at the pinned
-- commit, MIT; NOTICE).
--
-- An open loop is upstream's: a memory tagged task (the caller's own word, as upstream; a #task in its text is one too)
-- and not task:done, and none of upstream's agent-log tags (claude-response, codex-response, build-log, resume-playbook:
-- an agent's build notes, not a commitment). Upstream marks those with tags and keeps its own state in tags beside them;
-- Rhythm keeps its state in its columns, so the open-loop predicate also asks what every queue asks of a memory (053's
-- stale queue): live (in the catalog, not in the trash), not held for review, not marked wrong (deprecated), and its
-- window open (a replaced or ended loop is not open, as upstream's validity rule). memory_open_loop is the one
-- definition: it answers the loop's direction, out (you owe) or in (owed to you), or null for a memory that is no open
-- loop. A caller's tags are its own, so an update that drops task or adds task:done opens or closes a loop as a change of
-- its tags, kept and undone as every change is.
--
-- A commitment's direction and counterparty are the engine's (upstream's owed-to-me and counterparty: tags, which 033
-- reserves): the memory_commitments row remember writes with owed_by (someone owes the person: in) or owed_to (the person
-- owes someone: out), set once when the memory is written, never changed and never versioned, as a memory's source and
-- author are not. A task-tagged memory with no row is a loop the person owes, as upstream's task tag alone is. The
-- counterparty is upstream's slug of the name given (lower case, spaces to hyphens, anything but a-z, 0-9, _ and -
-- dropped; none when nothing is left), so no free text reaches the row and the answers name it as words made from it;
-- a name that reads like instructions to an AI client is refused.
--
-- remember_memory_commitment writes the memory as create_memory does (033: a draft in the caller's scope by its landing
-- rule, screened for a hold, 036 and 045, one audit row, created) with the task tag added when the caller left it out,
-- and its commitment row in the same transaction. Its idempotency key covers the commitment too: the same key with
-- another direction or counterparty, or a remember without one, is a key used for another request.
--
-- resolve_memory_loop: whoever may change the memory (033's author lock) closes the loop. done: it was done, task:done
-- joins its tags (the task tag stays, as upstream: it is history); received: something owed to the person arrived, the
-- same write, refused for a loop the person owes; not_a_task: it never was one, its task tag comes off (task:done, when
-- it has one, stays). Each is a change of the memory's tags: the state it replaces is kept as a version (memory_retire),
-- the revision moves and one audit row, resolved, names the action and the direction; 041's undo puts the tags back and
-- the loop is open again. It is no content write, so it is not counted toward a burst (045 counts creates, appends and
-- updates) and moves no passage (033's text revision). A repeat answers unchanged and writes nothing, so a loop leaves
-- the queue once. A memory held for review, marked wrong or whose window has closed is refused, as still true refuses
-- them (053): a held memory waits for a person, and the others are in no queue. No idempotency key: a repeat answers
-- unchanged, and expected_revision makes a retry after an undo safe.
--
-- The queue a reader is served (Core/MemoryLoops.cs) is the memories it reads (the one read predicate on each memory's
-- own row) that are open loops, of those it may change (the author lock, as the stale queue's), and of those its own
-- (memory_loop_mine): a private memory of its person, a Family memory its person wrote, or one no person wrote. A Family
-- admin is not served another member's commitments, though the author lock lets an admin resolve one (upstream's
-- actionable view: what the caller owes and can settle, for admins too). With memory.scopes off the household shares
-- everything, every install's default, so every admitted login is served every loop, as it is every stale memory.
--
-- An export and import keep a commitment: rhythm export writes the memory's commitment (its direction and counterparty)
-- on its line, and import_memory_commitment, called by rhythm import for each memory it has just added, adds it there.
--
-- Nothing calls these but the memory tools (list_loops, resolve_loop and remember's owed_by and owed_to), made only with
-- memory.tools on (off by default), rhythm import, and tests: with none called no memory has a commitment, no audit row
-- is resolved, and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision; 23505 an idempotency key used for another
-- request; 54000 text over the limit; 22023 invalid input (also a memory with no task tag to mark done, received for a
-- loop the person owes, and a memory whose window has closed) and, with a hint the engine reads, held (a memory held for
-- review) and retracted (a deprecated memory). A refused call writes nothing. The number is provisional.

-- A commitment's direction and counterparty, one row a memory, gone with it when it is deleted for good. No subject of
-- its own: it is its memory's, wherever a share moves it.
CREATE TABLE memory_commitments(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 -- out: the person owes someone (owed_to); in: someone owes the person (owed_by).
 direction text NOT NULL CHECK(direction IN ('out','in')),
 -- Upstream's slug of the other person's name; none when the name left nothing to keep.
 counterparty text CHECK(counterparty ~ '^[a-z0-9][a-z0-9_-]{0,63}$'),
 created_at timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON memory_commitments FROM PUBLIC;

-- The list of checked tables in force, with the commitments added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_commitments']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- Upstream's agent-log tags: a task-tagged memory carrying one is an agent's build note, not a commitment.
CREATE FUNCTION memory_loop_logs() RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
 SELECT ARRAY['claude-response','codex-response','build-log','resume-playbook']
$$;

-- The one definition of an open loop: its direction, out (the person owes it) or in (owed to the person), or null when
-- p_memory is no open loop. Tagged task and not task:done nor an agent log; live (its own catalog row current, not in the
-- trash), not held, not deprecated, and its window open. Every count and list reads it.
CREATE FUNCTION memory_open_loop(p_memory uuid) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT coalesce(c.direction,'out')
 FROM memories m LEFT JOIN memory_commitments c ON c.memory_id=m.id
 WHERE m.id=p_memory AND 'task'=ANY(m.tags) AND NOT 'task:done'=ANY(m.tags) AND NOT m.tags && memory_loop_logs()
   AND m.status<>'deprecated' AND m.hold IS NULL AND (m.valid_until IS NULL OR m.valid_until>now())
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
     AND r.status='current')
$$;

-- Whether a loop of p_subject by p_author is one the principal is served as its own: one it may change (033's author lock)
-- that, with scopes on, it wrote, or that is its person's private memory, or that no person wrote. An admin's power to
-- change another member's Family memory does not make that member's commitments the admin's.
CREATE FUNCTION memory_loop_mine(p_principal uuid,p_subject text,p_author text,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT memory_may_change(p_principal,p_subject,p_author,p_scoped)
   AND (NOT p_scoped OR p_author IS NULL OR coalesce((SELECT c.person_id=p_author FROM principals c WHERE c.id=p_principal),false))
$$;

-- Upstream's counterparty slug of a name: trimmed, lower case, white space to hyphens, anything but a-z, 0-9, _ and -
-- dropped, then the hyphens and underscores at either end; null when nothing usable is left. A name is 1 to 64
-- characters with no control character.
CREATE FUNCTION memory_counterparty(p_name text) RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE slug text;
BEGIN
 IF p_name IS NULL OR length(btrim(p_name)) NOT BETWEEN 1 AND 64 OR p_name ~ '[[:cntrl:]]' THEN
   RAISE EXCEPTION 'The other person''s name is 1 to 64 characters' USING ERRCODE='22023';
 END IF;
 slug:=btrim(regexp_replace(regexp_replace(lower(btrim(p_name)),'\s+','-','g'),'[^a-z0-9_-]','','g'),'-_');
 RETURN CASE WHEN slug ~ '^[a-z0-9][a-z0-9_-]{0,63}$' THEN slug END;
END $$;

-- remember_memory_commitment: a new memory from p_memory (create_memory's fields, MemoryInput's), tagged task, that is a
-- commitment: p_direction out (the person owes p_counterparty) or in (p_counterparty owes the person), the counterparty's
-- name made into its slug. Written as create_memory writes it, with its commitment row in the same transaction. The key
-- is the caller's, over the memory and the commitment together.
CREATE FUNCTION remember_memory_commitment(p_principal uuid,p_memory jsonb,p_direction text,p_counterparty text,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE slug text; hash text; replay jsonb; tagged jsonb; result jsonb; commitment jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 IF p_direction IS NULL OR p_direction NOT IN ('out','in') THEN
   RAISE EXCEPTION 'A commitment is owed by someone (in) or to someone (out)' USING ERRCODE='22023';
 END IF;
 IF p_memory IS NULL OR jsonb_typeof(p_memory)<>'object' THEN
   RAISE EXCEPTION 'A memory takes content, tags, kind, importance, volatility, scope, project and source' USING ERRCODE='22023';
 END IF;
 slug:=memory_counterparty(p_counterparty);
 -- A name that reads like instructions to an AI client (036's screening, its hyphens and underscores read as spaces, as
 -- 045 screens a new project's slug) is refused: the name is listed to every AI client of the scope, and no hold covers it.
 IF slug IS NOT NULL AND memory_screen(translate(slug,'-_','  '))->>'reason' IS NOT NULL THEN
   RAISE EXCEPTION 'The other person''s name reads like instructions to an AI client: give their name alone' USING ERRCODE='22023';
 END IF;
 commitment:=jsonb_build_object('direction',p_direction,'counterparty',slug);
 -- The task tag, last, when the caller left it out (memory_strings and create_memory check the list as given).
 tagged:=jsonb_strip_nulls(p_memory);
 IF jsonb_typeof(coalesce(tagged->'tags','[]'::jsonb))<>'array' THEN
   RAISE EXCEPTION 'Tags are a list of words' USING ERRCODE='22023';
 END IF;
 IF NOT coalesce(tagged->'tags','[]'::jsonb) ? 'task' THEN
   tagged:=tagged||jsonb_build_object('tags',coalesce(tagged->'tags','[]'::jsonb)||'["task"]'::jsonb);
 END IF;
 hash:=memory_request_hash('create',NULL,tagged||jsonb_build_object('commitment',commitment),NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 result:=create_memory(p_principal,tagged,p_channel,p_client,NULL);
 INSERT INTO memory_commitments(memory_id,direction,counterparty) VALUES((result->>'id')::uuid,p_direction,slug);
 result:=result||jsonb_build_object('commitment',commitment);
 RETURN memory_answered(p_principal,p_key,'create',hash,(result->>'id')::uuid,result);
END $$;

-- resolve_loop: p_action done, received or not_a_task on p_memory, by whoever may change it, as a change of its tags (a
-- version kept, the revision moved, one audit row, resolved). A repeat writes nothing and answers unchanged.
CREATE FUNCTION resolve_memory_loop(p_principal uuid,p_memory uuid,p_action text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; c memory_commitments; dir text; v_tags text[]; said jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_action IS NULL OR p_action NOT IN ('done','received','not_a_task') THEN
   RAISE EXCEPTION 'action is done, received or not_a_task' USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is resolved only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is no open loop: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF m.valid_until<=now() THEN
   RAISE EXCEPTION 'This memory stopped being true at %: it is history, not an open loop (clear valid_until, or undo, if it is true again)',
     to_char(m.valid_until AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 SELECT * INTO c FROM memory_commitments WHERE memory_id=m.id;
 dir:=coalesce(c.direction,'out');
 said:=jsonb_build_object('action',p_action,'direction',dir,'counterparty',c.counterparty);
 IF p_action='not_a_task' THEN
   IF NOT 'task'=ANY(m.tags) THEN
     RETURN memory_reply(m,scoped)||said||jsonb_build_object('changed','[]'::jsonb,'unchanged',true);
   END IF;
   v_tags:=array_remove(m.tags,'task');
 ELSE
   IF NOT 'task'=ANY(m.tags) THEN
     RAISE EXCEPTION 'This memory is no open loop: it has no task tag, so there is nothing to mark %',
       CASE p_action WHEN 'done' THEN 'done' ELSE 'received' END USING ERRCODE='22023';
   END IF;
   IF p_action='received' AND dir<>'in' THEN
     RAISE EXCEPTION 'received is for something owed to the person (remember''s owed_by); this one they owe: use done' USING ERRCODE='22023';
   END IF;
   IF 'task:done'=ANY(m.tags) THEN
     RETURN memory_reply(m,scoped)||said||jsonb_build_object('changed','[]'::jsonb,'unchanged',true);
   END IF;
   IF cardinality(m.tags)>=64 THEN
     RAISE EXCEPTION 'This memory has 64 tags, the most it takes: take one off (update) before marking it %',p_action USING ERRCODE='22023';
   END IF;
   v_tags:=m.tags||'task:done'::text;
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET tags=v_tags,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'resolved',p_principal,channel,p_client,
   jsonb_build_object('action',p_action,'direction',dir,'fields',jsonb_build_array('tags')));
 RETURN memory_reply(m,scoped)||said||jsonb_build_object('changed',jsonb_build_array('tags'),'unchanged',false);
END $$;

-- import_memory_commitment: the commitment of one memory of an export (its line's commitment: a direction and maybe a
-- counterparty slug), for the memory rhythm import has just added from it (043's and 054's import_memory answered
-- added, in this transaction). Answers added (nameLeftOut when its counterparty read like instructions to an AI client:
-- screened as remember_memory_commitment screens a name, since the name is listed to every AI client of the scope and
-- no hold covers it, the commitment comes in with its direction and no name); present (the memory has one here already,
-- left as it is); not_imported (no memory this origin's import added by that id in this transaction is here: nothing
-- is added to a memory that was here before, or that an earlier import brought and a person may have changed since);
-- or refused, invalid.
CREATE FUNCTION import_memory_commitment(p_origin jsonb,p_memory jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_id uuid; v_direction text; v_counterparty text; v_left boolean:=false;
BEGIN
 BEGIN
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(p_memory->'commitment') IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_memory->'commitment'->'direction') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_memory->'commitment'->'counterparty','null'::jsonb)) NOT IN ('string','null') THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid;
   v_direction:=p_memory->'commitment'->>'direction';
   v_counterparty:=p_memory->'commitment'->>'counterparty';
   IF v_id IS NULL OR v_direction NOT IN ('out','in') OR v_counterparty !~ '^[a-z0-9][a-z0-9_-]{0,63}$' THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   -- Only a memory this import added: its imported row is of this transaction (memory_events.at is now(), its start).
   IF NOT memory_imported(p_origin,'memory',v_id) OR NOT EXISTS(SELECT 1 FROM memories m WHERE m.id=v_id)
     OR NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='imported' AND e.at=now()) THEN
     RETURN jsonb_build_object('outcome','not_imported');
   END IF;
   IF v_counterparty IS NOT NULL AND memory_screen(translate(v_counterparty,'-_','  '))->>'reason' IS NOT NULL THEN
     v_counterparty:=NULL;
     v_left:=true;
   END IF;
   INSERT INTO memory_commitments(memory_id,direction,counterparty) VALUES(v_id,v_direction,v_counterparty) ON CONFLICT(memory_id) DO NOTHING;
   IF NOT FOUND THEN RETURN jsonb_build_object('outcome','present'); END IF;
   RETURN jsonb_build_object('outcome','added','id',v_id)||CASE WHEN v_left THEN '{"nameLeftOut":true}'::jsonb ELSE '{}'::jsonb END;
 EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list of checked tables gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_loop_logs() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_open_loop(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_loop_mine(uuid,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_counterparty(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION remember_memory_commitment(uuid,jsonb,text,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_memory_loop(uuid,uuid,text,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_commitment(jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_loop_logs(),memory_open_loop(uuid),memory_loop_mine(uuid,text,text,boolean),
 memory_counterparty(text),remember_memory_commitment(uuid,jsonb,text,text,text,text,text),
 resolve_memory_loop(uuid,uuid,text,integer,text,text),import_memory_commitment(jsonb,jsonb) FROM PUBLIC;
