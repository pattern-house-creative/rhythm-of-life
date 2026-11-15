-- Undo for native memories (docs/dev/memory.md section 5, undo a change; SB-P3-05 in
-- docs/dev/second-brain-parity/backlog.md; D-019; ledger rows MCP-17, VER-03, VER-04, API-15 and API-16): undo a
-- memory's newest change, or put it back as it was at an earlier revision. An undo is a change like any other: it
-- retires the state it replaces as a version, moves the revision and writes one audit row ('reverted', naming the
-- revision it went back to and the fields it changed), so undoing an undo puts the memory back as it was before it.
-- A group of an AI client's changes is undone a memory at a time through this same function, by the memory store
-- (Core/MemoryUndo.cs), which finds the group's members again at every call from the audit trail as the caller reads it.
--
-- Adapted from Second Brain's undo (src/memory/undo.ts and src/brief/changes.ts at the pinned commit, MIT; NOTICE):
-- the newest change or a version by number, history visible to the caller only (034's floor), an author lock with
-- upstream's "a member may undo their own newest change" (canRevert), and a group re-derived by the server. Unlike
-- upstream, an undo never releases a hold for an AI client (D-039): undoing a release holds the memory again, an undo
-- that leaves the words as they are keeps their hold unless a person (the app or the terminal) undoes it, and new words
-- an undo brings back are screened by 036's trigger like any change's; it never moves a memory between scopes (an undo
-- of a share is an un-share, SB-P3-10's); and a project deleted since is not made again.
--
-- Nothing calls this yet but tests: no MCP tool, app tool or route undoes a memory (SB-P3-09, SB-P3-12), so no install
-- holds an undo and every existing answer is as before. With memory.scopes off, every install's default, any admitted
-- harness or app login may undo any change, today's model (D-048); with them on only those memory.md section 5 names.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to undo, or an undo through an AI client's
-- channel that would change a capsule slot (a person's step, 039); P0002 an unknown memory, which is
-- also the answer for one the caller does not read or one in the trash; 40001 a stale expected revision (its message
-- names the current one); 22023 invalid input, and, with a hint the engine reads, nothing_to_undo (the memory has not
-- changed since it was written), no_version (above the current revision), not_visible (from before the change that
-- brought the memory to this caller, 034), pruned:<n> (no longer kept; n is the oldest revision still kept) and moved
-- (a revision from another scope). A refused undo writes nothing. The number is provisional.

-- Whether every change of a Family memory after revision p_after, up to its current one, was made by a login bound to the
-- caller's own person (memory.md section 5: whoever made a change may undo it). Only with scopes on (with them off every
-- admitted login may undo anything) and only for a reader of Family; a login with no person, and the operator, made no
-- person's change. Every revision in the range must have its audit row, so a change recorded by nobody counts against it.
CREATE FUNCTION memory_own_changes(p_principal uuid,p_memory uuid,p_subject text,p_after integer,p_current integer,p_scoped boolean)
RETURNS boolean LANGUAGE plpgsql STABLE AS $$
DECLARE person text; covered integer; others boolean;
BEGIN
 IF NOT p_scoped OR p_subject<>'household' OR p_after>=p_current OR NOT reads_subject(p_principal,p_subject) THEN RETURN false; END IF;
 SELECT c.person_id INTO person FROM principals c WHERE c.id=p_principal;
 IF person IS NULL THEN RETURN false; END IF;
 SELECT count(DISTINCT e.revision),bool_or(q.person_id IS DISTINCT FROM person) INTO covered,others
 FROM memory_events e LEFT JOIN principals q ON q.id=e.principal_id
 WHERE e.memory_id=p_memory AND e.revision>p_after AND e.revision<=p_current;
 RETURN covered=p_current-p_after AND NOT coalesce(others,true);
END $$;

-- undo_memory: the memory as it was at revision p_to, or before its newest change when p_to is null, written as a new
-- revision. What it brings back: the text, tags, status, kind, importance, volatility, validity, capsule slot and project
-- of that revision (a project deleted since stays gone, and a project capsule slot with it); never its scope, author or
-- source. Its hold: a revision that was held comes back held (an undone release holds the memory again, D-161); new
-- words are screened by 036's trigger against the words they replace, as any change's are; words that stay as they are
-- keep their hold, unless a person (the app or the terminal, never an AI client, D-039) undoes back to a revision where
-- they were released. A capsule slot comes back only when a person undoes: through an AI client's channel an undo that
-- would change one is refused (039). An undo to the current revision, or one that would change nothing, writes nothing
-- and answers unchanged. p_expected, the revision the caller read, makes a retry safe: a second undo with it is refused
-- as stale rather than undoing the first (an undo takes no idempotency key).
CREATE FUNCTION undo_memory(p_principal uuid,p_memory uuid,p_to integer,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; n memories; v memory_versions; target integer; floor integer; kept integer;
 project uuid; changed text[]; detail jsonb;
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
 changed:=ARRAY(SELECT f FROM (VALUES
   ('content',n.content IS DISTINCT FROM m.content),('tags',n.tags IS DISTINCT FROM m.tags),('status',n.status IS DISTINCT FROM m.status),
   ('kind',n.kind IS DISTINCT FROM m.kind),('importance',n.importance IS DISTINCT FROM m.importance),
   ('volatility',n.volatility IS DISTINCT FROM m.volatility),('project',n.project_id IS DISTINCT FROM m.project_id),
   ('capsule',n.capsule IS DISTINCT FROM m.capsule OR n.capsule_slot IS DISTINCT FROM m.capsule_slot),
   ('validity',n.valid_from IS DISTINCT FROM m.valid_from OR n.valid_until IS DISTINCT FROM m.valid_until)) c(f,d) WHERE d);
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

-- 036's memory_event_held, with an undo's audit row marked held too when the undo held the memory: 036's screening of the
-- words it brought back, or a held revision it brought back (undo_memory notes that one, 'restored').
CREATE OR REPLACE FUNCTION memory_event_held() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE held jsonb;
BEGIN
 IF NEW.memory_id IS NULL OR NEW.event NOT IN ('created','appended','updated','reverted') THEN RETURN NEW; END IF;
 held:=nullif(current_setting('rhythm.memory_held',true),'')::jsonb->(NEW.memory_id::text);
 IF held IS NOT NULL AND held->>'revision'=NEW.revision::text THEN
   NEW.detail:=NEW.detail||jsonb_build_object('held',held-'revision');
 END IF;
 RETURN NEW;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_own_changes(uuid,uuid,text,integer,integer,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_memory(uuid,uuid,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_held() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_own_changes(uuid,uuid,text,integer,integer,boolean),undo_memory(uuid,uuid,integer,integer,text,text),
 memory_event_held() FROM PUBLIC;
