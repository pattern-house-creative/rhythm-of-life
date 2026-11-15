-- Share a native memory between its owner's private scope and Family, and take it back (SB-P3-10 in
-- docs/dev/second-brain-parity/backlog.md; docs/dev/memory.md sections 5, 8 and 11, story 2; Q-157). Adapted from Second
-- Brain's share (src/capture/share.ts at the pinned commit, MIT; NOTICE): a share moves the memory, it is no copy; the
-- move and its audit row commit together, pinned to the scope the caller's own read found; a move to where the memory
-- is already answers unchanged. Unlike upstream, an un-share takes a memory back to its author's private scope only,
-- never to the admin's who asks (memory.md section 5), a memory no person wrote cannot be un-shared, a held memory is
-- not shared until a person releases it, and the move is a change like any other: the state it replaces is kept as a
-- version, the revision moves, and its audit row is under the subject it lands in (034's history floor), so its arrival
-- is the first revision its new readers see.
--
-- Who: sharing a private memory into Family is its owner's (033's author lock: only its owner changes a private
-- memory), a member of Family; taking a Family memory back is its author's or a Family admin's, and only while its
-- author is a current person (a former or suspended person's private scope is read by no principal, so the memory would
-- leave every reader, with no undo, restore or way back: it stays in Family for the operator's offboarding, SB-P7-13).
-- With memory.scopes off,
-- every install's default, every memory is the household's already, so a share is refused before anything is read and
-- nothing changes. The tool that calls this (Mcp/MemoryTools.cs, `share`) is made only with memory.tools and
-- memory.share both on, each off by default; real use waits for the owner's per-person scopes (decisions.md D-013).
--
-- What follows the memory: its subject and scope (its resources row's, which 025's cascade carries into memories), so
-- its passages and vectors go with it, its person- and AI-made links (made_links keep their ids, memory.md 10.3) and its
-- author. Its project by slug: the landing scope's project of that slug or alias takes it, or one is made there with the
-- slug as its name (no private description travels), except that through a channel scored as an AI client's a clean
-- memory never joins a project a held write made (046: only a person's step lists one), nor makes one whose slug reads
-- like instructions (045's screening of a new slug): it then lands in no project and the answer says so. Someone other
-- than its author who takes a memory back (an admin) is never a person's own channel for the author's scope: the memory
-- joins only a project of that slug already listed there, else it lands in no project, and nothing is made in another
-- person's private scope in the admin's name. Its capsule slot does not follow: a slot is a person's step (039, D-172),
-- so a person's move takes it out of its slot and a person puts it into one again, and through an AI client's channel a
-- memory in a slot is not moved at all (as 041 refuses an undo that would change a slot): a person takes it out first.
-- A made link may join a Family record and a private one, served only to a reader of both ends, but never two people's
-- private records (memory.md section 5): taking back a memory linked to another person's private record is refused,
-- and nothing removes the link (025: a person's links are never removed automatically).
--
-- What is dropped (memory.md section 8, Un-share and revoke): the map's star rows for the memory, whose titles are
-- excerpts of its words, with the map's links to them and their evidence (001's cascade), as a delete forever drops them
-- (038's memory_purge). Reads judge a native star by the memory's own row already (D-063), so nobody who lost the memory
-- is served its star, links or passages from their next read on; dropping the rows leaves no excerpt cached under the
-- scope it left. The next pass of the map lists it again under its new subject and finds its links again (the tool asks
-- for one, at most one every three minutes, else the scheduled pass does). A pass that read the memory before the move
-- and saves after it can write its star row again under the subject it left; that row is served to no one (reads judge
-- a native star by the memory's own row), and the pass after it reports it missing, which takes its excerpt title away.
--
-- What is kept: the state a move replaces is a version like any change's, marked moved (memory_retire_moved), and 034's
-- prune, replaced here, takes such a state first when a memory is over its twenty kept states, the newest kept state
-- excepted: a move changes no words, so a moved state's words are the next state's, and an AI client moving a memory
-- back and forth evicts at most one of its earlier edits (the first move's, as any change's would), never every one.
-- With no moved state the prune keeps exactly what 034's kept (the newest twenty), so nothing answers differently while
-- nothing calls this. Each move still writes one audit row, never removed, as every change does.
--
-- Locks, as every checked writer's: the recorded mode's row FOR SHARE (lock_memory_scopes, 033: rhythm scopes off waits
-- for an un-share into a private scope), then the memory's resources row before its memories row (memory_for_change,
-- D-086), then the landing scope's project registry (memory_project_lock, 035).
--
-- Errors, as 033's: 42501 not available to this login, not this caller's to move (the author lock), a share by someone
-- outside Family, or a memory in a capsule slot moved through an AI client's channel; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision, checked after the answer for a memory
-- already where it is asked to go, so a retry with the revision first read answers unchanged; 22023 invalid input (a
-- take-back that would link two people's private records) and, with a hint the engine reads, scopes_off (memory.scopes
-- is off), held (a held memory, not shared until a person releases it) and no_author (a Family memory no person wrote,
-- or whose author is no longer a current person: there is no private scope anyone reads to take it back to). A refused
-- move writes nothing. The number is provisional.

-- The state a move replaces, kept as 033's memory_retire keeps any change's, marked moved in its state, so that the
-- prune below sees the mark at the state's own insert.
CREATE FUNCTION memory_retire_moved(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score))||'{"moved":true}'::jsonb,
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- 034's prune, with moved states first: after a state is retired, a memory over memory_versions_kept() earlier states
-- loses as many as it is over, a state a move retired before any other (but never the newest kept state), and otherwise
-- the oldest, whoever retired it and in whatever order the states arrive. With no moved state that is 034's rule: the
-- newest twenty stay.
CREATE OR REPLACE FUNCTION memory_versions_prune() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE kept integer; newest integer;
BEGIN
 SELECT count(*)-memory_versions_kept(),max(x.revision) INTO kept,newest FROM memory_versions x WHERE x.memory_id=NEW.memory_id;
 IF kept>0 THEN
   DELETE FROM memory_versions v WHERE v.memory_id=NEW.memory_id AND v.revision IN (
     SELECT x.revision FROM memory_versions x WHERE x.memory_id=NEW.memory_id
     ORDER BY (x.state ? 'moved' AND x.revision<newest) DESC,x.revision LIMIT kept);
 END IF;
 RETURN NULL;
END $$;

-- share_memory: p_to 'shared' (Family; upstream's company, the default when null) or 'private' (upstream's personal: back
-- to the author's private scope). Answers memory_reply's fields, the fields the move changed (scope, and project and
-- capsule when they changed), whether a project could not come with it (projectLeft), and unchanged when it is there
-- already. The project is named only to a caller that reads where the memory landed: an admin who takes a memory back
-- is told neither its project there nor whether one was made.
CREATE FUNCTION share_memory(p_principal uuid,p_memory uuid,p_to text,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; target text; landing text; own boolean; stranger boolean:=false; m memories; n memories;
 was memory_projects;
 known memory_projects; project jsonb; v_project uuid; v_left boolean:=false; changed text[]; dropped integer; detail jsonb; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 target:=coalesce(memory_scope_named(p_to),'shared');
 IF NOT scoped THEN
   RAISE EXCEPTION 'Sharing waits until per-person scopes are on: with them off every memory is the household''s already, visible to everyone in it'
     USING ERRCODE='22023',HINT='scopes_off';
 END IF;
 -- The author lock first, as for every change; the expected revision only after the answer for a memory already where
 -- it is asked to go, so a retry of a share that landed answers unchanged, not a conflict.
 m:=memory_for_change(p_principal,p_memory,NULL,scoped);
 IF m.scope=target THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true);
 END IF;
 IF p_expected IS NOT NULL AND p_expected<>m.revision THEN
   RAISE EXCEPTION 'The memory changed since revision %: it is at revision % now; read it again and retry',p_expected,m.revision
     USING ERRCODE='40001';
 END IF;
 -- A capsule slot is a person's step (039, D-172): through an AI client's channel a memory in a slot is not moved, since
 -- the move would take it out of its slot.
 IF channel='mcp' AND m.capsule IS NOT NULL THEN
   RAISE EXCEPTION 'This memory fills a capsule slot, which a person changes from the app or the terminal, never an AI client: a person takes it out of its slot first'
     USING ERRCODE='42501';
 END IF;
 IF target='shared' THEN
   IF m.hold IS NOT NULL THEN
     RAISE EXCEPTION 'This memory is held for review: it is shared into Family only once a person has released it'
       USING ERRCODE='22023',HINT='held';
   END IF;
   IF NOT reads_subject(p_principal,'household') THEN
     RAISE EXCEPTION 'Only a member of Family shares a memory into it' USING ERRCODE='42501';
   END IF;
   landing:='household';
 ELSE
   IF m.author_id IS NULL THEN
     RAISE EXCEPTION 'No person wrote this memory, so there is no private scope to take it back to: it stays in Family'
       USING ERRCODE='22023',HINT='no_author';
   END IF;
   IF NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=m.author_id AND s.kind='person' AND s.status='current') THEN
     RAISE EXCEPTION 'Its author is no longer a current person here, so no one would read it in a private scope: it stays in Family'
       USING ERRCODE='22023',HINT='no_author';
   END IF;
   -- Two people's private records never link (memory.md section 5): the link stays, and so does the memory.
   IF EXISTS(SELECT 1 FROM made_links l JOIN resources r ON r.id=CASE WHEN l.source_id=m.id THEN l.target_id ELSE l.source_id END
       JOIN subjects s ON s.id=r.subject_id AND s.kind='person'
       WHERE (l.source_id=m.id OR l.target_id=m.id) AND r.subject_id<>m.author_id) THEN
     RAISE EXCEPTION 'This memory is linked to a record in another person''s private scope, and two people''s private records never link: it stays in Family'
       USING ERRCODE='22023';
   END IF;
   landing:=m.author_id;
   stranger:=(SELECT q.person_id FROM principals q WHERE q.id=p_principal) IS DISTINCT FROM landing;
 END IF;
 -- A person's own channel for the scope it lands in, which someone else taking a memory back into its author's is not.
 own:=memory_own_channel(p_principal,channel) AND NOT stranger;
 -- Its project by slug in the landing scope, under that registry's lock, decided before anything is written.
 IF m.project_id IS NOT NULL THEN
   SELECT p.* INTO was FROM memory_projects p WHERE p.id=m.project_id;
   PERFORM memory_project_lock(landing);
   SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=landing
     AND (p.slug=was.slug OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=was.slug))
     ORDER BY p.slug=was.slug DESC LIMIT 1;
   v_left:=CASE WHEN stranger THEN known.id IS NULL OR known.held
     WHEN known.id IS NOT NULL THEN known.held AND m.hold IS NULL AND NOT own
     ELSE m.hold IS NULL AND NOT own AND memory_screen(replace(was.slug,'-',' '))->>'reason' IS NOT NULL END;
 END IF;
 PERFORM memory_retire_moved(m);
 -- Out of its project and its capsule slot first (025's key keeps a memory in a project of its own scope), then moved:
 -- its resources row's subject and scope, which the memory follows (025's cascade).
 UPDATE memories SET project_id=NULL,capsule=NULL,capsule_slot=NULL WHERE id=m.id
   AND (project_id IS NOT NULL OR capsule IS NOT NULL);
 UPDATE resources SET subject_id=landing,scope=target WHERE id=m.id AND provider='memory' AND relationship_snapshot IS NULL;
 IF m.project_id IS NOT NULL AND NOT v_left THEN
   project:=memory_project(p_principal,CASE WHEN landing='household' THEN m.author_id ELSE landing END,landing,was.slug,channel,p_client);
   v_project:=(project->>'id')::uuid;
 END IF;
 UPDATE memories SET project_id=v_project,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO n;
 DELETE FROM resources WHERE provider='memory' AND relationship_snapshot IS NOT NULL AND id<>m.id
   AND external_id=memory_star_external(m.id);
 GET DIAGNOSTICS dropped=ROW_COUNT;
 changed:=ARRAY['scope']||CASE WHEN m.project_id IS NOT NULL THEN ARRAY['project'] ELSE '{}'::text[] END
   ||CASE WHEN m.capsule IS NOT NULL THEN ARRAY['capsule'] ELSE '{}'::text[] END;
 detail:=jsonb_build_object('from',m.subject_id,'to',n.subject_id,'fields',to_jsonb(changed),'project_left',v_left,'stars',dropped);
 IF target='shared' THEN
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(n.id,n.subject_id,n.revision,'shared',p_principal,channel,p_client,detail);
 ELSE
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(n.id,n.subject_id,n.revision,'unshared',p_principal,channel,p_client,detail);
 END IF;
 result:=memory_reply(n,scoped)||jsonb_build_object('changed',to_jsonb(changed),'projectLeft',v_left);
 IF NOT reads_subject(p_principal,n.subject_id) THEN
   result:=result||jsonb_build_object('project',NULL,'projectLeft',false);
 ELSIF coalesce((project->>'created')::boolean,false) THEN
   result:=result||jsonb_build_object('project',jsonb_build_object('id',project->'id','slug',project->'slug','created',true));
 END IF;
 RETURN result;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_retire_moved(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 -- CREATE OR REPLACE resets a function's settings, so the replaced prune gets its search path again.
 EXECUTE format('ALTER FUNCTION memory_versions_prune() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION share_memory(uuid,uuid,text,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_retire_moved(memories),memory_versions_prune(),share_memory(uuid,uuid,text,integer,text,text) FROM PUBLIC;
