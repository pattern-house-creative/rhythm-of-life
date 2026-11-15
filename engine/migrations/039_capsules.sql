-- Prompt capsules' slots (SB-P3-08 in docs/dev/second-brain-parity/backlog.md; ledger rows CTX-01 to CTX-05;
-- docs/dev/memory.md sections 5 and 7.1): the checked writer that puts a native memory into one capsule slot or takes it
-- out of one, and the rule that a project slot stays with its project. Adapted from Second Brain's prompt capsules
-- (src/prompt-capsule at the pinned commit, MIT; NOTICE): upstream slots a memory with its capsule: and capsule-slot:
-- tags, which Rhythm keeps in 025's typed columns (capsule, capsule_slot, with 025's checks on the slot names) and never
-- takes as tags (033's reserved list). The capsule itself is built in the engine (Core/Capsules.cs) through the one read
-- predicate on each memory's own resources row, like every memory read; nothing here reads for a caller.
--
-- Nothing calls this writer yet but tests: no MCP tool, app tool or route writes a slot (SB-P3-09 and SB-P3-12), so no
-- install's memory is in a capsule slot and every existing answer is as before. The write takes the recorded
-- memory.scopes mode FOR SHARE first (lock_memory_scopes, 033), locks the memory's resources row before the memory
-- (memory_for_change), keeps the state it replaces as a version, moves the revision and writes one audit row; 025's
-- triggers move the capsule revision of the scope. With scopes on, only those memory.md section 5 names change a slot,
-- as any other change. Setting the slot a memory has writes nothing and answers unchanged, so the write takes no
-- idempotency key (as release_memory_hold, 036): a retry answers unchanged.
--
-- A slot is a person's step: every client of the scope reads its capsule first, so the writer refuses an AI client's
-- channel (mcp) before it reads anything, a slot taken out included, and 041's undo refuses an undo through that
-- channel that would change a slot. Upstream lets an AI client set its capsule tags and scores the change as a hold
-- signal (SB-P5-01, not built); until that signal exists Rhythm takes the stricter rule (D-172). An AI client may still
-- edit a memory a person has slotted, and confirm a slotted draft (D-145): its words are screened by 036 like any
-- change's.
-- The channel is the caller's word: the MCP tools are to name mcp (SB-P3-09), and only a login bound to a person may
-- name the terminal (033's memory_channel), so a person's own AI client holding a person-bound token is told apart
-- from the terminal only by what its caller passes (memory.md section 5; SB-P3-19).
--
-- Errors, as 033's: 42501 not available to this login or through an AI client's channel, or not this caller's to
-- change; P0002 an unknown memory (also one the caller does not read, or one in the trash); 22023 invalid input, or a
-- project slot for a memory in no project; 40001 a stale expected revision. The number is provisional.

-- set_memory_capsule: p_capsule core with p_slot identity, preferences, constraints or principles; project with p_slot
-- current-state, decisions or open-questions, for a memory in a project (the project's capsule); or both null to take
-- the memory out of its slot. Only a canonical memory, not held, deprecated or past its validity, fills a slot when the
-- capsule is read (Core/Capsules.cs): a draft's slot waits until the draft is confirmed (set_memory_status), which
-- anyone allowed to change the memory may do, an AI client included (D-145). Only the app or the terminal sets a slot.
CREATE FUNCTION set_memory_capsule(p_principal uuid,p_memory uuid,p_capsule text,p_slot text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; was jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel='mcp' THEN
   RAISE EXCEPTION 'A capsule slot is set or cleared by a person, from the app or the terminal, never by an AI client'
     USING ERRCODE='42501';
 END IF;
 IF (p_capsule IS NULL)<>(p_slot IS NULL)
   OR p_capsule IS NOT NULL AND NOT (p_capsule='core' AND p_slot IN ('identity','preferences','constraints','principles')
     OR p_capsule='project' AND p_slot IN ('current-state','decisions','open-questions')) THEN
   RAISE EXCEPTION 'A capsule slot is core identity, preferences, constraints or principles, or project current-state, decisions or open-questions; or none to take the memory out of its slot'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.capsule IS NOT DISTINCT FROM p_capsule AND m.capsule_slot IS NOT DISTINCT FROM p_slot THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot,'unchanged',true);
 END IF;
 IF p_capsule='project' AND m.project_id IS NULL THEN
   RAISE EXCEPTION 'A project slot is in its project''s capsule: give the memory a project first' USING ERRCODE='22023';
 END IF;
 was:=jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot);
 PERFORM memory_retire(m);
 UPDATE memories SET capsule=p_capsule,capsule_slot=p_slot,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'capsule_changed',p_principal,channel,p_client,
   jsonb_build_object('fields',jsonb_build_array('capsule'),'from',was,'to',jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot,'changed',jsonb_build_array('capsule'));
END $$;

-- A project slot belongs to its project's capsule, so a change that moves a memory filling one to another project, or
-- out of its project, is refused before anything is written: the slot is taken out first (set_memory_capsule), never
-- carried into another project's capsule or guessed away. A change that clears the slot with the project (the project's
-- delete, 035) passes. Without this, update_memory moving such a memory to another project would fill that project's
-- slot unasked, and clearing its project would fail 025's check with no reason a caller can act on.
CREATE FUNCTION memory_capsule_project() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'This memory fills its project''s % capsule slot: take it out of the slot first, then change its project',OLD.capsule_slot
   USING ERRCODE='22023';
END $$;
CREATE TRIGGER memories_capsule_project BEFORE UPDATE OF project_id ON memories
 FOR EACH ROW WHEN (OLD.capsule='project' AND NEW.capsule='project' AND NEW.project_id IS DISTINCT FROM OLD.project_id)
 EXECUTE FUNCTION memory_capsule_project();

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION set_memory_capsule(uuid,uuid,text,text,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_capsule_project() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION set_memory_capsule(uuid,uuid,text,text,integer,text,text),memory_capsule_project() FROM PUBLIC;
