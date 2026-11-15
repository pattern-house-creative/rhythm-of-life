-- Standing instructions for native memories (SB-P5-12 in docs/dev/second-brain-parity/backlog.md; story 12; ledger rows
-- INT-15, RET-22, API-40, API-41 and CFG-15; D-028, D-211; docs/dev/memory.md sections 5 and 9): a memory the person asked
-- to have applied whenever its topic comes up ("When booking a flight, check the shared calendar first."), which recall
-- shows above its results, with where it came from, when a question is close enough to it. Adapted from Second Brain's
-- standing memory (src/standing/*, src/capture/entry.ts, src/capture/t7-capture.ts, src/memory/actions.ts and
-- src/routes/standing.ts at the pinned commit, MIT; NOTICE).
--
-- A standing instruction is a memory remember wrote with standing true (remember_memory_standing): created as create_memory
-- creates one (033: a draft in the caller's scope by its landing rule, screened for a hold, 036 and 045, one audit row),
-- marked in the same transaction, with an audit row of its own (standing_set). Upstream marks one with its reserved
-- standing:active tag, which 033 reserves; here it is a column of the memory, as a due date is (061), since export and
-- import refuse reserved tags and an update would drop one. It is part of a memory's state: memory_retire (and a move's and
-- a cascade's retire, replaced below) keeps it in the version it writes, and undo (replaced below) puts it back.
--
-- It fires by topic, so it is nothing with a date or a debt: no due date and no task tag (058: a task-tagged memory is a loop
-- the person owes). Each is a constraint on the row, whichever write would bring it: resolve_due's snooze, an update that
-- adds the tag, an import. A refused write leaves nothing, and stopped, it is an ordinary memory that may take
-- either. remember says so first, in its own words, and the tools refuse a decision or a commitment beside it.
--
-- The limits are upstream's. At most 500 characters (memory_standing_chars; a constraint on the row, so an update or an
-- append past it is refused too, nothing written). At most 50 in a scope (memory_standing_max): remember counts the
-- scope's instructions that can fire under a per-scope lock, so two saves at the same moment do not pass it, and refuses
-- the 51st with nothing saved (upstream saves the memory without the mark and says so; here the person asked for an
-- instruction, so the client is told and asks them which to stop). An instruction held for review does not count until a
-- person releases it, so a burst of held text cannot fill a scope. A share or an import can still bring a scope past 50,
-- and an undo of a stop can too, so what fires is decided at read time as upstream's cache build decides it: the oldest 50
-- of a scope, the rest listed as over_limit.
--
-- One definition every list, count and firing reads (memory_standing_rows): a standing memory, in the catalog, not in the
-- trash, not marked wrong and its window open, with why it cannot fire: held (held for review: never fires, and its words
-- stay out of every answer), project_held (in a project held for review, 046, which no unheld project shares by a tag: no
-- question can name it, and it takes no place either) or over_limit (past the oldest 50 of its scope, those two left out);
-- no reason means it may. A project-scoped instruction (one in a project, or tagged with an alias its scope's project
-- claims, as a project's members are, 035) fires only for a question that names that project, by slug or alias, never a
-- project held for review, which claims nothing, as for every read of a project's members (memory_standing_applies); one
-- with no project fires for any question. What it fires from is what the caller reads: the
-- engine (Core/MemoryStanding.cs) reads these rows under the one read predicate on each memory's own row. The score and
-- the threshold are the engine's, which runs the model; none of it is stored.
--
-- stop_memory_standing: whoever may change the memory (033's author lock). The instruction stops firing and stays a
-- memory, as a change: the state it replaces kept as a version (memory_retire), the revision moved, one audit row,
-- standing_stopped; undo puts it back. A repeat answers unchanged. A memory that never was one is refused, and so is one
-- held for review or marked wrong, as resolve_loop and resolve_due refuse them. No idempotency key: a repeat answers
-- unchanged, and expected_revision makes a retry after an undo safe.
--
-- A standing instruction is no digest's source, as upstream's eligibility leaves it out (054 said so): it is a rule the
-- person keeps, not a fact to roll up. memory_digest_eligible is replaced below with that one line.
--
-- An export and import keep it: rhythm export writes standing on the memory's line, and import_memory_standing, called by
-- rhythm import for each memory it has just added, marks it, at its imported revision, unless it is a decision or a
-- commitment, which a standing instruction never is (the tools refuse both at once, as upstream does), or the row's
-- constraints refuse it (words over the limit, a due date, the task tag).
--
-- Nothing calls these but the memory tools (remember's standing, list_standing, stop_standing, and recall's firing), made
-- only with memory.tools on (off by default), rhythm import, and tests: with none called no memory is standing, no audit
-- row is standing_set or standing_stopped, every version's state is as before (strip_nulls drops the empty field), every
-- digest list is as before and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision; 23505 an idempotency key used for another
-- request; 54000 a standing instruction over its 500 characters; 22023 invalid input (also a memory that is no standing
-- instruction, and one that is also a task) and, with a hint the engine reads, limit (a scope with 50 already), held (a
-- memory held for review) and retracted (a deprecated memory); 23514 by constraint name: a change past the 500 characters
-- (memories_standing_short), a due date (memories_standing_undated) or the task tag (memories_standing_no_task) on one. A
-- refused call writes nothing. The number is provisional.

ALTER TABLE memories ADD COLUMN standing boolean NOT NULL DEFAULT false,
 ADD CONSTRAINT memories_standing_short CHECK(NOT standing OR char_length(content)<=500),
 ADD CONSTRAINT memories_standing_undated CHECK(NOT standing OR due_at IS NULL),
 ADD CONSTRAINT memories_standing_no_task CHECK(NOT standing OR NOT 'task'=ANY(tags));
CREATE INDEX memories_standing ON memories(subject_id,created_at,id) WHERE standing;

-- Upstream's STANDING_MAX_CHARS and STANDING_MAX. MemoryStanding.MaxChars and MaxInScope say the same, and a test keeps
-- them equal; the constraint above says the first.
CREATE FUNCTION memory_standing_chars() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 500 $$;
CREATE FUNCTION memory_standing_max() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 50 $$;

-- The state a change retires (061's memory_retire), with the mark beside the rest of its columns; none when it is not one.
CREATE OR REPLACE FUNCTION memory_retire(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score,
     'due_at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'due_kind',m.due_kind,'due_source',m.due_source,
     'standing',CASE WHEN m.standing THEN true END)),
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- 061's memory_retire_moved, with the mark.
CREATE OR REPLACE FUNCTION memory_retire_moved(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score,
     'due_at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'due_kind',m.due_kind,'due_source',m.due_source,
     'standing',CASE WHEN m.standing THEN true END))
   ||'{"moved":true}'::jsonb,
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- 061's memory_retire_cascade, with the mark.
CREATE OR REPLACE FUNCTION memory_retire_cascade(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score,
     'due_at',to_char(m.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),'due_kind',m.due_kind,'due_source',m.due_source,
     'standing',CASE WHEN m.standing THEN true END))
   ||'{"cascade":true}'::jsonb,
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- The one definition every list, count and firing reads: each standing memory that is live (in the catalog, not in the
-- trash, not marked wrong, its window open), with why it cannot fire, or none when it may: held (held for review),
-- project_held (in a project held for review, and no unheld project claims it by a tag, so memory_standing_applies lets no
-- question through), or over_limit (past the oldest memory_standing_max() of its scope that are neither). Who reads which is
-- the caller's read predicate, on top.
CREATE FUNCTION memory_standing_rows() RETURNS TABLE(id uuid,reason text) LANGUAGE sql STABLE AS $$
 SELECT x.id,CASE WHEN x.hold IS NOT NULL THEN 'held' WHEN x.project_held THEN 'project_held' WHEN x.n>memory_standing_max() THEN 'over_limit' END
 FROM (SELECT m.id,m.hold,h.project_held,
     row_number() OVER (PARTITION BY m.subject_id,m.hold IS NULL AND NOT h.project_held ORDER BY m.created_at,m.id) AS n
   FROM memories m JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
   CROSS JOIN LATERAL (SELECT EXISTS(SELECT 1 FROM memory_projects p WHERE p.id=m.project_id AND p.held)
     AND NOT EXISTS(SELECT 1 FROM memory_project_aliases a JOIN memory_projects p ON p.id=a.project_id
       WHERE p.subject_id=m.subject_id AND NOT p.held AND a.alias=ANY(m.tags)) AS project_held) h
   WHERE m.standing AND m.status<>'deprecated' AND (m.valid_until IS NULL OR m.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)) x
$$;

-- Whether a standing memory may fire for a question that names the project p_project (a slug or alias, null for none): one
-- with no project always may. One in a project (or tagged with an alias its scope's project claims, as a project's members
-- are, 035) only when p_project is that project's, and never while that project is held for review: a held project claims
-- nothing, as for every read of a project's members, so a tag naming only one leaves a memory with no project.
CREATE FUNCTION memory_standing_applies(p_memory uuid,p_project text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE
   WHEN m.project_id IS NULL AND NOT EXISTS(SELECT 1 FROM memory_project_aliases a JOIN memory_projects p ON p.id=a.project_id
     WHERE NOT p.held AND p.subject_id=m.subject_id AND a.alias=ANY(m.tags)) THEN true
   WHEN p_project IS NULL THEN false
   ELSE EXISTS(SELECT 1 FROM memory_projects p WHERE NOT p.held AND p.subject_id=m.subject_id
     AND (p.slug=p_project OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=p_project))
     AND (p.id=m.project_id OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=ANY(m.tags))))
 END FROM memories m WHERE m.id=p_memory
$$;

-- remember_memory_standing: a new memory from p_memory (create_memory's fields, MemoryInput's) that is a standing
-- instruction, at most memory_standing_chars() characters and, unless screening held it, within memory_standing_max() in
-- its scope, and no task (its tag, a #task in its words among them). Written as create_memory writes it, marked in the same
-- transaction, with one audit row of its own. The key is the caller's, over the memory and the mark.
CREATE FUNCTION remember_memory_standing(p_principal uuid,p_memory jsonb,p_channel text,p_client text DEFAULT NULL,p_key text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE hash text; replay jsonb; result jsonb; m memories; v_count integer;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 IF p_memory IS NULL OR jsonb_typeof(p_memory)<>'object' THEN
   RAISE EXCEPTION 'A memory takes content, tags, kind, importance, volatility, scope, project and source' USING ERRCODE='22023';
 END IF;
 hash:=memory_request_hash('create',NULL,jsonb_strip_nulls(p_memory)||jsonb_build_object('standing',true),NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 IF char_length(p_memory->>'content')>memory_standing_chars() THEN
   RAISE EXCEPTION 'A standing instruction is at most % characters, this one is %: nothing was saved. Shorten it to what to do and when; a longer note can be a memory of its own',
     memory_standing_chars(),char_length(p_memory->>'content') USING ERRCODE='54000';
 END IF;
 result:=create_memory(p_principal,jsonb_strip_nulls(p_memory),p_channel,p_client,NULL);
 SELECT * INTO m FROM memories WHERE id=(result->>'id')::uuid;
 IF 'task'=ANY(m.tags) THEN
   RAISE EXCEPTION 'A standing instruction can''t also be a task, something owed until it is done: nothing was saved. Leave the tag task and any #task out, or save a task of its own'
     USING ERRCODE='22023';
 END IF;
 -- The scope's limit, counted under its own lock, so two saves at one moment do not pass it. A held one waits for a person
 -- and never fires, so it takes no place, and a burst of held text fills no scope; nor does one a held project keeps from firing.
 IF m.hold IS NULL THEN
   PERFORM pg_advisory_xact_lock(hashtextextended('memory-standing:'||m.subject_id,0));
   SELECT count(*) INTO v_count FROM memories x JOIN memory_standing_rows() s ON s.id=x.id
   WHERE x.subject_id=m.subject_id AND x.hold IS NULL AND s.reason IS DISTINCT FROM 'project_held';
   IF v_count>=memory_standing_max() THEN
     RAISE EXCEPTION 'This scope has % standing instructions already, the most it takes: nothing was saved. list_standing lists them; ask the person which to stop (stop_standing)',
       v_count USING ERRCODE='22023',HINT='limit';
   END IF;
 END IF;
 UPDATE memories SET standing=true WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'standing_set',p_principal,memory_channel(p_principal,p_channel,p_client),p_client,
   jsonb_build_object('fields',jsonb_build_array('standing')));
 result:=result||jsonb_build_object('standing',true);
 RETURN memory_answered(p_principal,p_key,'create',hash,m.id,result);
END $$;

-- stop_memory_standing: the instruction p_memory stops firing and stays a memory, by whoever may change it, as a change of
-- it (a version kept, the revision moved, one audit row, standing_stopped). A repeat writes nothing and answers unchanged.
CREATE FUNCTION stop_memory_standing(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is stopped only once a person releases it: until then it never fires' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) never fires: there is nothing to stop. Set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF NOT m.standing THEN
   IF EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.event='standing_stopped') THEN
     RETURN memory_reply(m,scoped)||jsonb_build_object('changed','[]'::jsonb,'unchanged',true);
   END IF;
   RAISE EXCEPTION 'This memory is no standing instruction: one remembered with standing true fires and can be stopped' USING ERRCODE='22023';
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET standing=false,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'standing_stopped',p_principal,channel,p_client,jsonb_build_object('fields',jsonb_build_array('standing')));
 RETURN memory_reply(m,scoped)||jsonb_build_object('changed',jsonb_build_array('standing'),'unchanged',false);
END $$;

-- 041's undo_memory, the same but for the mark: a revision's mark comes back with the rest of its state (its version's
-- standing; none in a state kept without one), and 'standing' is a field an undo compares and names. It replaces 061's
-- replacement, so it keeps 060's and 061's lines as they are.
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
   ('standing',n.standing IS DISTINCT FROM m.standing)) c(f,d) WHERE d);
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
 UPDATE memories SET content=n.content,tags=n.tags,status=n.status,kind=n.kind,importance=n.importance,volatility=n.volatility,
   project_id=n.project_id,capsule=n.capsule,capsule_slot=n.capsule_slot,valid_from=n.valid_from,valid_until=n.valid_until,
   hold=n.hold,hold_score=n.hold_score,due_at=n.due_at,due_kind=n.due_kind,due_source=n.due_source,standing=n.standing,
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

-- 054's memory_digest_eligible, the same but for one line: a standing instruction is left out, as upstream's eligibility
-- leaves it out and 054 said this item must.
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
   AND (m.valid_until IS NULL OR m.valid_until>now())
   AND (m.importance IS NULL OR m.importance<b.importance)
   AND (k.n=0 OR k.n<b.recalls AND m.created_at<now()-b.age)
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM memory_retraction_flags f WHERE f.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM made_links l JOIN memories d ON d.id=l.source_id AND d.subject_id=p_subject
     WHERE l.target_id=m.id AND l.type='drawn_from' AND l.lifecycle='active' AND memory_digest_live(d.id))
 ORDER BY m.created_at DESC,m.id DESC
$$;

-- import_memory_standing: the mark of one memory of an export (its line's standing true), for the memory rhythm import has
-- just added from it (043's and 054's import_memory answered added, in this transaction). Answers added; present (it is
-- marked here already, left as it is); not_imported (no memory this origin's import added by that id in this transaction
-- is here: nothing is added to a memory that was here before, or that an earlier import brought and a person may have
-- changed since); or refused, invalid (anything else: also a memory the row's constraints refuse, its words over the limit,
-- a due date or the task tag, which import_memory_due runs first for, and a memory that is a decision or a commitment, which
-- import_memory_decision and import_memory_commitment run first for, so a line naming both keeps those and the memory stays
-- unmarked).
-- Written with the memory, at its imported revision: no revision moves. The limit of a scope is read when it fires.
CREATE FUNCTION import_memory_standing(p_origin jsonb,p_memory jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_id uuid;
BEGIN
 BEGIN
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR p_memory->'standing' IS DISTINCT FROM 'true'::jsonb THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid;
   IF v_id IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   -- Only a memory this import added: its imported row is of this transaction (memory_events.at is now(), its start).
   IF NOT memory_imported(p_origin,'memory',v_id) OR NOT EXISTS(SELECT 1 FROM memories m WHERE m.id=v_id)
     OR NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='imported' AND e.at=now()) THEN
     RETURN jsonb_build_object('outcome','not_imported');
   END IF;
   IF EXISTS(SELECT 1 FROM memory_decisions d WHERE d.memory_id=v_id) OR EXISTS(SELECT 1 FROM memory_commitments k WHERE k.memory_id=v_id) THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   UPDATE memories SET standing=true WHERE id=v_id AND NOT standing;
   IF NOT FOUND THEN RETURN jsonb_build_object('outcome','present'); END IF;
   RETURN jsonb_build_object('outcome','added','id',v_id);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so each replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION memory_standing_chars() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_standing_max() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire_moved(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire_cascade(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_standing_rows() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_standing_applies(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION remember_memory_standing(uuid,jsonb,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION stop_memory_standing(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_memory(uuid,uuid,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_eligible(text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_standing(jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_standing_chars(),memory_standing_max(),memory_retire(memories),memory_retire_moved(memories),
 memory_retire_cascade(memories),memory_standing_rows(),memory_standing_applies(uuid,text),
 remember_memory_standing(uuid,jsonb,text,text,text),stop_memory_standing(uuid,uuid,integer,text,text),
 undo_memory(uuid,uuid,integer,integer,text,text),memory_digest_eligible(text,uuid,text),import_memory_standing(jsonb,jsonb) FROM PUBLIC;
