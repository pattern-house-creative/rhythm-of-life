-- Truth over time for native memories (SB-P4-06 and SB-P4-08 in docs/dev/second-brain-parity/backlog.md; story 6;
-- ledger rows TIM-01, TIM-02, TIM-04 and WRT-09; docs/dev/memory.md section 5): when a memory was true (its validity
-- window), one memory replacing another with an explicit confirmation, the states a reader is told (current, replaced,
-- ended, retracted), and the retraction cascade. Adapted from Second Brain's validity windows and retraction hooks
-- (src/memory/validity.ts and src/recall/validity-view.ts at the pinned commit, MIT; NOTICE).
--
-- A memory is true from its stated start (valid_from; none means since it was written, '-infinity' an unknown start, 025)
-- until its end (valid_until; none means still true). Every change here is a change like any other: the state it replaces
-- is kept as a version (memory_retire, 033), the revision moves and one audit row is written, so history reads the words
-- and the window of every earlier state and undo (041) puts one back. Nothing rewrites a version or an audit row.
--
-- Supersede. Unlike upstream, where a model's contradiction verdict closes the older memory on a write, the engine runs no
-- model (D-033): a caller names both memories, the one true now and the one it replaces, and the engine answers a plan
-- first (when the older stops being true: the newer's start) with a confirmation, which a second call passes back to
-- apply exactly that plan. The older memory's window closes at the newer's start, a person- or AI-made link (made_links,
-- D-067) records that the newer supersedes it, and the older keeps its words and status: it is history, not wrong.
-- Windows are compared, not arrival order (upstream's planSupersede): a newer that starts at or before the older is
-- refused, an older that had ended by then and a newer told as already over within the older's time change nothing.
-- Canonical memories are protected: only a canonical memory replaces a canonical one (confirm the newer first), held
-- and deprecated memories neither replace nor are replaced, both must be in one scope, and both are the caller's to
-- change (033's author lock). A replaced memory leaves recall, the map and capsules as any memory past its end does
-- (Recall.Current, MemoryStars, Capsules).
--
-- States (memory_validity_state), as upstream's validity view: retracted (deprecated: marked wrong), current (no end, or
-- an end still to come), replaced (ended, with a live memory of its scope that supersedes it and starts where it ends:
-- not deprecated, held or in the trash) and ended (ended, with none).
--
-- The retraction cascade (SB-P4-08; upstream's retraction and un-retraction hooks). When a memory that replaced others is
-- retracted (set deprecated, undone to deprecated, or put in the trash), each memory it closed at its own start reopens:
-- its end becomes the retracted memory's own end (none when that was still true, or the end a third memory gave it,
-- which then supersedes the reopened one too), as a change of its own (reopened). Each memory built on it (a made link
-- from it, drawn_from or caused_by, one hop, its scope) is flagged for a check (memory_retraction_flags, flagged):
-- flagged, never blocked, changed or deleted. When it comes back (another status, an undo, a restore from the trash),
-- each memory it reopened closes again at its start, if nothing has changed that memory's words or window since
-- (reclosed), and its flags come off (unflagged); a memory flagged by another source stays flagged for that one. At
-- most 25 of each per change.
-- The cascade runs in the change's own transaction, from its audit row (memory_event_retraction), with that row's login,
-- channel and client, so it lands only with the change and is audited as its consequence; the change's caller is told
-- what it reopened, closed again and flagged (rhythm.memory_cascade). A reopen and a reclose only put back a window a
-- supersede set, as a consequence of a change the caller may make, so they take no author lock on the memory they move;
-- a moved start (set_memory_validity, or an undo that moves one) gives what it replaced an end it never had, so it is
-- refused unless the caller may change each of them too. A memory reopened by a source that is still retracted closes
-- again when an earlier replacement of its own comes back, so a chain retracted and brought back in any order leaves
-- one current fact. Two stay current only where someone chose it: a reopened memory whose words or window were changed
-- since stays as they left it, and an undo of the supersede itself makes the earlier memory current beside the newer.
-- A link the cascade made for "replaced by" (evidence 'retraction') goes when the memory closes again at another's
-- start. The states these steps retire are marked (cascade), so the prune drops them before any state a person or a
-- client wrote. 041's own-changes rule leaves the cascade's flag rows out (they change nothing a person could undo).
-- A cascade keeps to one scope: a move between scopes (048's share, shared or unshared) takes the moving memory's flags
-- off, and those that name it, each audited where its memory is.
--
-- Nothing calls these writers yet but the memory tools, made only with memory.tools on (off by default), and tests:
-- with no native memory, no install holds a window, a supersede or a flag, and every existing answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 40001 a stale expected revision, or a confirmation that is not the plan's
-- now; 23505 an idempotency key used for another request; 22023 invalid input and, with a hint the engine reads, held
-- (a memory held for review), retracted (a deprecated memory) and protected (a canonical memory a draft would replace).
-- A refused change writes nothing. The number is provisional.

-- A change of when a memory was true takes an idempotency key, as every other change does (033, 038).
ALTER TABLE memory_requests DROP CONSTRAINT memory_requests_operation_check;
ALTER TABLE memory_requests ADD CONSTRAINT memory_requests_operation_check
 CHECK(operation IN ('create','append','update','set_status','forget','restore','validity'));

-- The flags the cascade puts on a memory built on a retracted one: which memory, and which retracted source, since when.
-- No key on the source, so a flag outlives a source deleted for good (it was retracted first: a trash trip is one). A
-- flag is no state of the memory: it moves no revision and keeps no version; its audit rows (flagged, unflagged,
-- flags_cleared) say when it came and went.
CREATE TABLE memory_retraction_flags(
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 retracted_id uuid NOT NULL,
 flagged_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(memory_id,retracted_id),
 CHECK(memory_id<>retracted_id)
);
CREATE INDEX memory_retraction_flags_retracted ON memory_retraction_flags(retracted_id);
REVOKE ALL ON memory_retraction_flags FROM PUBLIC;

-- The list of checked tables in force, with the flags added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_retraction_flags']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The memory that replaced p_memory, or none: a memory of its scope with a supersedes link to it, live (not deprecated,
-- not held, not in the trash) and starting exactly where it ends; the newest such when there are several. The one
-- definition every state and answer reads (upstream's supersededBySql).
CREATE FUNCTION memory_replaced_by(p_memory uuid) RETURNS uuid LANGUAGE sql STABLE AS $$
 SELECT s.id FROM memories m
 JOIN made_links l ON l.target_id=m.id AND l.type='supersedes' AND l.lifecycle='active'
 JOIN memories s ON s.id=l.source_id AND s.subject_id=m.subject_id
 JOIN resources r ON r.id=s.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
 WHERE m.id=p_memory AND m.valid_until IS NOT NULL AND s.status<>'deprecated' AND s.hold IS NULL
   AND coalesce(s.valid_from,s.created_at)=m.valid_until
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=s.id)
 ORDER BY s.created_at DESC,s.id LIMIT 1
$$;

-- What a memory is now: retracted (deprecated), current (no end, or an end still to come), replaced (ended, and
-- memory_replaced_by names what replaced it) or ended (ended, replaced by nothing live). Null for no such memory.
CREATE FUNCTION memory_validity_state(p_memory uuid) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN m.status='deprecated' THEN 'retracted'
   WHEN m.valid_until IS NULL OR m.valid_until>now() THEN 'current'
   WHEN memory_replaced_by(m.id) IS NOT NULL THEN 'replaced' ELSE 'ended' END
 FROM memories m WHERE m.id=p_memory
$$;

-- The state a cascade's step replaces (a reopen, a reclose, a moved start's end), kept as memory_retire keeps any
-- change's, marked cascade in its state as 048 marks a move, so the prune below sees the mark at the state's own insert.
CREATE FUNCTION memory_retire_cascade(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score))||'{"cascade":true}'::jsonb,
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- 048's prune, with a cascade's states beside a move's: after a state is retired, a memory over memory_versions_kept()
-- earlier states loses as many as it is over, a state a move or a cascade retired before any other (never the newest
-- kept state), and otherwise the oldest. So a window moved back and forth by another memory never evicts a state that
-- someone wrote. With no marked state it is 034's rule: the newest twenty stay.
CREATE OR REPLACE FUNCTION memory_versions_prune() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE kept integer; newest integer;
BEGIN
 SELECT count(*)-memory_versions_kept(),max(x.revision) INTO kept,newest FROM memory_versions x WHERE x.memory_id=NEW.memory_id;
 IF kept>0 THEN
   DELETE FROM memory_versions v WHERE v.memory_id=NEW.memory_id AND v.revision IN (
     SELECT x.revision FROM memory_versions x WHERE x.memory_id=NEW.memory_id
     ORDER BY ((x.state ? 'moved' OR x.state ? 'cascade') AND x.revision<newest) DESC,x.revision LIMIT kept);
 END IF;
 RETURN NULL;
END $$;

-- 041's memory_own_changes, with the cascade's flag rows left out (flagged, unflagged, flags_cleared): a flag moves no
-- revision and changes nothing of the memory an undo puts back, so another person's retraction of a source, or their
-- check of a flag, never stops a member from undoing their own newest change. Every revision in the range still needs
-- a row of its own, and any other row by someone else still counts against it.
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
   AND e.event NOT IN ('flagged','unflagged','flags_cleared');
 RETURN covered=p_current-p_after AND NOT coalesce(others,true);
END $$;

-- A memory's person- and AI-made links, those in made_links and those its trash row keeps (038 moves them there).
CREATE FUNCTION memory_links_kept(p_memory uuid) RETURNS SETOF made_links LANGUAGE sql STABLE AS $$
 SELECT l.* FROM made_links l WHERE l.source_id=p_memory OR l.target_id=p_memory
 UNION ALL
 SELECT k.* FROM memory_trash t CROSS JOIN LATERAL jsonb_populate_recordset(NULL::made_links,t.links) k WHERE t.memory_id=p_memory
$$;

-- What a change's cascade did, noted for its transaction under the memory it changed (as 036 notes a hold), so the
-- change's answer names the memories reopened, closed again, flagged and unflagged.
CREATE FUNCTION memory_cascade_note(p_memory uuid,p_what text,p_id uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE notes jsonb:=coalesce(nullif(current_setting('rhythm.memory_cascade',true),'')::jsonb,'{}'::jsonb); mine jsonb;
BEGIN
 mine:=coalesce(notes->(p_memory::text),'{}'::jsonb);
 mine:=mine||jsonb_build_object(p_what,coalesce(mine->p_what,'[]'::jsonb)||to_jsonb(p_id::text));
 PERFORM set_config('rhythm.memory_cascade',(notes||jsonb_build_object(p_memory::text,mine))::text,true);
END $$;

-- A memory locked for a cascade's change, as every change locks one (its resources row, then the memory, D-086), when
-- it is still current in the catalog and not in the trash; else none.
CREATE FUNCTION memory_cascade_lock(p_memory uuid) RETURNS memories LANGUAGE plpgsql AS $$
DECLARE m memories;
BEGIN
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current' FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 SELECT * INTO m FROM memories x WHERE x.id=p_memory AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=x.id) FOR UPDATE;
 RETURN m;
END $$;

-- The retraction hook: x, which may have replaced others, is retracted now (deprecated, or in the trash).
CREATE FUNCTION memory_retract(x memories,p_principal uuid,p_channel text,p_client text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE start timestamptz:=coalesce(x.valid_from,x.created_at); target uuid; y memories; z uuid; maker uuid; d uuid; added integer;
BEGIN
 -- Each memory it closed at its own start reopens to its own end.
 FOR target IN SELECT DISTINCT l.target_id FROM memory_links_kept(x.id) l
   WHERE l.source_id=x.id AND l.type='supersedes' AND l.lifecycle='active' ORDER BY l.target_id LIMIT 25
 LOOP
   y:=memory_cascade_lock(target);
   CONTINUE WHEN y.id IS NULL OR y.subject_id<>x.subject_id OR y.valid_until IS DISTINCT FROM start;
   PERFORM memory_retire_cascade(y);
   UPDATE memories SET valid_until=x.valid_until,revision=revision+1,updated_at=greatest(now(),updated_at)
   WHERE id=y.id RETURNING * INTO y;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'reopened',p_principal,p_channel,p_client,
     jsonb_build_object('fields',jsonb_build_array('validity'),'cause','retraction','retracted',x.id,'until',y.valid_until));
   PERFORM memory_cascade_note(x.id,'reopened',y.id);
   -- x had itself been replaced by z: z replaces y now, so "replaced by" stays true. The link is the cascade's (evidence
   -- 'retraction'), and goes again when y closes at another memory's start (memory_unretract).
   IF x.valid_until IS NOT NULL THEN
     z:=NULL;
     SELECT s.id,l.made_by INTO z,maker FROM memory_links_kept(x.id) l
     JOIN memories s ON s.id=l.source_id AND s.subject_id=x.subject_id
     JOIN resources r ON r.id=s.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
     WHERE l.target_id=x.id AND l.type='supersedes' AND l.lifecycle='active' AND s.id<>y.id AND s.status<>'deprecated'
       AND s.hold IS NULL AND coalesce(s.valid_from,s.created_at)=x.valid_until
       AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=s.id)
     ORDER BY s.created_at DESC,s.id LIMIT 1;
     IF z IS NOT NULL AND coalesce(p_principal,maker) IS NOT NULL THEN
       INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by)
       VALUES(gen_random_uuid(),z,y.id,'supersedes','verified','retraction',coalesce(p_principal,maker))
       ON CONFLICT(source_id,target_id,type) DO UPDATE SET lifecycle='active' WHERE made_links.lifecycle<>'active';
     END IF;
   END IF;
 END LOOP;
 -- Each memory built on it is flagged for a check, once per source.
 FOR d IN SELECT DISTINCT l.source_id FROM memory_links_kept(x.id) l
   JOIN memories m ON m.id=l.source_id AND m.subject_id=x.subject_id
   JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
   WHERE l.target_id=x.id AND l.type IN ('drawn_from','caused_by') AND l.lifecycle='active' AND m.status<>'deprecated'
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   ORDER BY l.source_id LIMIT 25
 LOOP
   INSERT INTO memory_retraction_flags(memory_id,retracted_id) VALUES(d,x.id) ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS added=ROW_COUNT;
   IF added>0 THEN
     SELECT * INTO y FROM memories m WHERE m.id=d;
     INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
     VALUES(y.id,y.subject_id,y.revision,'flagged',p_principal,p_channel,p_client,jsonb_build_object('cause','retraction','retracted',x.id));
     PERFORM memory_cascade_note(x.id,'flagged',d);
   END IF;
 END LOOP;
END $$;

-- The un-retraction hook: x is live again (not deprecated, back from the trash).
CREATE FUNCTION memory_unretract(x memories,p_principal uuid,p_channel text,p_client text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE start timestamptz:=coalesce(x.valid_from,x.created_at); target uuid; y memories; d uuid;
BEGIN
 -- Each memory x replaced closes again at x's start, if the newest change to its words or its window (an append, or a
 -- change whose fields name content or validity: a status, a tag, a capsule slot, a release or a flag touches neither)
 -- is a reopening: x's own, or one by a source still retracted (deprecated, in the trash, or gone), which reopened it
 -- past x while x was retracted too. A memory whose words or window someone changed after its reopening stays as they
 -- left it. The links the cascade made to it for "replaced by" from any other memory go, since x closes it now.
 FOR target IN SELECT DISTINCT l.target_id FROM made_links l
   WHERE l.source_id=x.id AND l.type='supersedes' AND l.lifecycle='active' ORDER BY l.target_id LIMIT 25
 LOOP
   y:=memory_cascade_lock(target);
   CONTINUE WHEN y.id IS NULL OR y.subject_id<>x.subject_id OR y.valid_until IS DISTINCT FROM x.valid_until
     OR coalesce(y.valid_from,y.created_at)>=start
     OR NOT EXISTS(SELECT 1 FROM (SELECT e.event,e.detail FROM memory_events e WHERE e.memory_id=y.id
         AND (e.event='appended' OR e.detail->'fields' ?| ARRAY['content','validity'])
         ORDER BY e.revision DESC,e.id DESC LIMIT 1) n
       WHERE n.event='reopened' AND (n.detail->>'retracted'=x.id::text OR NOT EXISTS(SELECT 1 FROM memories r
         WHERE r.id::text=n.detail->>'retracted' AND r.status<>'deprecated' AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=r.id))));
   PERFORM memory_retire_cascade(y);
   UPDATE memories SET valid_until=start,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=y.id RETURNING * INTO y;
   DELETE FROM made_links l WHERE l.target_id=y.id AND l.type='supersedes' AND l.evidence='retraction' AND l.source_id<>x.id;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'reclosed',p_principal,p_channel,p_client,
     jsonb_build_object('fields',jsonb_build_array('validity'),'cause','unretraction','by',x.id,'until',start));
   PERFORM memory_cascade_note(x.id,'reclosed',y.id);
 END LOOP;
 -- Its flags come off, in its own scope only (a cascade keeps to one); a memory flagged by another source too stays
 -- flagged for that one.
 FOR d IN DELETE FROM memory_retraction_flags f USING memories m
   WHERE f.retracted_id=x.id AND m.id=f.memory_id AND m.subject_id=x.subject_id RETURNING f.memory_id
 LOOP
   SELECT * INTO y FROM memories m WHERE m.id=d;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'unflagged',p_principal,p_channel,p_client,jsonb_build_object('cause','unretraction','by',x.id));
   PERFORM memory_cascade_note(x.id,'unflagged',d);
 END LOOP;
END $$;

-- A move between scopes (048): x has left the scope its flags were set in, so its own flags come off (audited on x, where
-- it is now) and so do those naming it (audited on each memory, in x's old scope, which the mover read x in). A cascade
-- keeps to one scope, so no flag names a memory of another.
CREATE FUNCTION memory_flags_moved(x memories,p_principal uuid,p_channel text,p_client text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r uuid; d uuid; y memories;
BEGIN
 FOR r IN DELETE FROM memory_retraction_flags f WHERE f.memory_id=x.id RETURNING f.retracted_id
 LOOP
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(x.id,x.subject_id,x.revision,'unflagged',p_principal,p_channel,p_client,jsonb_build_object('cause','moved','by',r));
 END LOOP;
 FOR d IN DELETE FROM memory_retraction_flags f WHERE f.retracted_id=x.id RETURNING f.memory_id
 LOOP
   SELECT * INTO y FROM memories m WHERE m.id=d;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'unflagged',p_principal,p_channel,p_client,jsonb_build_object('cause','moved','by',x.id));
 END LOOP;
END $$;

-- m's start moved from p_old to p_new: each memory it replaced (ending at p_old) ends at p_new, so "replaced by" stays
-- true. Each is the caller's to change too (033's author lock: this gives it an end it never had), else the whole
-- change is refused; and never to or before its own start. At most 10. Returns the memories moved.
CREATE FUNCTION memory_move_replaced(m memories,p_old timestamptz,p_new timestamptz,p_principal uuid,p_channel text,
 p_client text,p_scoped boolean) RETURNS uuid[] LANGUAGE plpgsql AS $$
DECLARE target uuid; y memories; moved uuid[]:='{}';
BEGIN
 IF p_new IS NOT DISTINCT FROM p_old THEN RETURN moved; END IF;
 FOR target IN SELECT DISTINCT l.target_id FROM made_links l
   WHERE l.source_id=m.id AND l.type='supersedes' AND l.lifecycle='active' ORDER BY l.target_id LIMIT 10
 LOOP
   y:=memory_cascade_lock(target);
   CONTINUE WHEN y.id IS NULL OR y.subject_id<>m.subject_id OR y.valid_until IS DISTINCT FROM p_old;
   IF p_new<=coalesce(y.valid_from,y.created_at) THEN
     RAISE EXCEPTION 'Memory % began at %, and this memory replaced it, so this one cannot start then or before: nothing changed',
       y.id,to_char(coalesce(y.valid_from,y.created_at) AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
   END IF;
   IF NOT memory_may_change(p_principal,y.subject_id,y.author_id,p_scoped) THEN
     RAISE EXCEPTION 'This memory replaced memory %, which only its author or a Family admin may change, so its start stays: nothing changed',
       y.id USING ERRCODE='42501';
   END IF;
   PERFORM memory_retire_cascade(y);
   UPDATE memories SET valid_until=p_new,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=y.id RETURNING * INTO y;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'validity_changed',p_principal,p_channel,p_client,
     jsonb_build_object('fields',jsonb_build_array('validity'),'cause','propagate','by',m.id,'valid_until',p_new));
   moved:=moved||y.id;
 END LOOP;
 RETURN moved;
END $$;

-- The cascade, from a change's audit row: whether the memory was live before the change (not deprecated, not in the
-- trash) and is now. A status change names its from; an undo's state before is the version it retired; a trip into the
-- trash leaves a live memory retracted, and a restore brings one back. An undo that moves a memory's start moves what
-- it replaced as set_memory_validity does, and a move between scopes takes flags off. The cascade's own rows start
-- nothing.
CREATE FUNCTION memory_event_retraction() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE x memories; v memory_versions; was boolean; live boolean;
BEGIN
 SELECT * INTO x FROM memories WHERE id=NEW.memory_id;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF NEW.event IN ('shared','unshared') THEN
   PERFORM memory_flags_moved(x,NEW.principal_id,NEW.channel,NEW.client);
   RETURN NULL;
 END IF;
 live:=x.status<>'deprecated' AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=x.id);
 IF NEW.event='status_changed' THEN was:=coalesce(NEW.detail->>'from',x.status)<>'deprecated';
 ELSIF NEW.event='reverted' THEN
   SELECT * INTO v FROM memory_versions WHERE memory_id=x.id AND revision=NEW.revision-1;
   was:=coalesce(v.status,x.status)<>'deprecated';
   IF v.memory_id IS NOT NULL THEN
     PERFORM memory_move_replaced(x,coalesce(v.valid_from,x.created_at),coalesce(x.valid_from,x.created_at),NEW.principal_id,
       NEW.channel,NEW.client,lock_memory_scopes());
   END IF;
 ELSIF NEW.event='trashed' THEN was:=x.status<>'deprecated';
 ELSIF NEW.event='restored' THEN was:=false;
 ELSE RETURN NULL;
 END IF;
 IF was AND NOT live THEN PERFORM memory_retract(x,NEW.principal_id,NEW.channel,NEW.client);
 ELSIF live AND NOT was THEN PERFORM memory_unretract(x,NEW.principal_id,NEW.channel,NEW.client);
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER memory_events_retraction AFTER INSERT ON memory_events FOR EACH ROW
 WHEN (NEW.memory_id IS NOT NULL AND NEW.event IN ('status_changed','reverted','trashed','restored','shared','unshared'))
 EXECUTE FUNCTION memory_event_retraction();

-- set_memory_validity: when a memory was true, set or cleared, alone (upstream's update with valid_from or valid_until
-- and no content: a new fact that replaces an old one is a memory of its own, then a supersede). p_change names
-- valid_from, valid_until or both, each a time with its offset or null (null: since it was written; still true). Both
-- are of what has already happened: a time after now is refused (a plan or a deadline is in the memory's words), and
-- the end comes after the start. When its start moves, each memory it replaced (ending at its old start) ends at its
-- new start too, so "replaced by" stays true; a start at or before such a memory's own start is refused, naming it. At
-- most 10 are moved. A change that changes nothing writes nothing and answers unchanged.
CREATE FUNCTION set_memory_validity(p_principal uuid,p_memory uuid,p_change jsonb,p_expected integer,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; given_from timestamptz;
 given_until timestamptz; n_from timestamptz; n_until timestamptz; old_start timestamptz; new_start timestamptz;
 moved uuid[]; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_change IS NULL OR jsonb_typeof(p_change)<>'object' OR p_change='{}'::jsonb
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_change) k WHERE k NOT IN ('valid_from','valid_until'))
   OR EXISTS(SELECT 1 FROM jsonb_each(p_change) e WHERE jsonb_typeof(e.value) NOT IN ('string','null')) THEN
   RAISE EXCEPTION 'A change of when a memory was true names valid_from, valid_until or both, each a time or null' USING ERRCODE='22023';
 END IF;
 hash:=memory_request_hash('validity',p_memory,p_change,p_expected);
 replay:=memory_replay(p_principal,p_key,'validity',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 BEGIN
   given_from:=(p_change->>'valid_from')::timestamptz;
   given_until:=(p_change->>'valid_until')::timestamptz;
 EXCEPTION WHEN data_exception THEN
   RAISE EXCEPTION 'valid_from and valid_until are times with an offset, such as 2026-06-15T00:00:00Z' USING ERRCODE='22023';
 END;
 IF NOT isfinite(coalesce(given_from,now())) OR NOT isfinite(coalesce(given_until,now())) THEN
   RAISE EXCEPTION 'valid_from and valid_until are times, never infinity' USING ERRCODE='22023';
 END IF;
 IF given_from>now() OR given_until>now() THEN
   RAISE EXCEPTION 'When a memory was true is about what has already happened: a time after now is refused (a plan or a deadline belongs in its words)'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 n_from:=CASE WHEN p_change ? 'valid_from' THEN given_from ELSE m.valid_from END;
 n_until:=CASE WHEN p_change ? 'valid_until' THEN given_until ELSE m.valid_until END;
 old_start:=coalesce(m.valid_from,m.created_at);
 new_start:=coalesce(n_from,m.created_at);
 IF n_until IS NOT NULL AND n_until<=new_start THEN
   RAISE EXCEPTION 'That end is not after this memory''s start (%): pass valid_from too if it began earlier',
     to_char(new_start AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"') USING ERRCODE='22023';
 END IF;
 IF n_from IS NOT DISTINCT FROM m.valid_from AND n_until IS NOT DISTINCT FROM m.valid_until THEN
   RETURN memory_answered(p_principal,p_key,'validity',hash,m.id,memory_reply(m,scoped)||jsonb_build_object('unchanged',true));
 END IF;
 -- The memories it replaced end where it starts now, each the caller's to change too.
 moved:=memory_move_replaced(m,old_start,new_start,p_principal,channel,p_client,scoped);
 PERFORM memory_retire(m);
 UPDATE memories SET valid_from=n_from,valid_until=n_until,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'validity_changed',p_principal,channel,p_client,
   jsonb_build_object('fields',jsonb_build_array('validity'),'cause','explicit','valid_from',m.valid_from,'valid_until',m.valid_until,
     'propagated',to_jsonb(moved)));
 result:=memory_reply(m,scoped)||jsonb_build_object('changed',jsonb_build_array('validity'),'propagated',to_jsonb(moved));
 RETURN memory_answered(p_principal,p_key,'validity',hash,m.id,result);
END $$;

-- supersede_memory: p_memory, true now, replaces p_replaces, an earlier fact of the same scope. Without a confirmation
-- it answers the plan and writes nothing: when p_replaces stops being true (p_memory's start) and the confirmation that
-- applies exactly that plan, which changes with either memory's revision. With the plan's confirmation it closes
-- p_replaces's window at p_memory's start, as a change of its own (superseded), and records the supersedes link; with
-- any other it is refused (40001), and nothing changes. A plan that would change nothing answers unchanged with why:
-- replaced (p_memory replaces it already, so a retry after a lost answer is safe), ended (it had stopped being true by
-- the time p_memory began) or enclosed (p_memory is told as over already, within p_replaces's time). No idempotency key:
-- the confirmation is the retry's safety.
CREATE FUNCTION supersede_memory(p_principal uuid,p_memory uuid,p_replaces uuid,p_confirmation text,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; x memories; y memories; xs timestamptz; ys timestamptz; reason text; code text; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_memory IS NULL OR p_replaces IS NULL OR p_memory=p_replaces THEN
   RAISE EXCEPTION 'A memory replaces another memory, never itself' USING ERRCODE='22023';
 END IF;
 IF p_confirmation IS NOT NULL AND p_confirmation !~ '^[0-9a-f]{16}$' THEN
   RAISE EXCEPTION 'A confirmation is the 16 letters and digits a plan gave, exactly' USING ERRCODE='22023';
 END IF;
 -- Both memories locked as every change locks one: the resources rows first, then the memories rows, each in id order.
 PERFORM 1 FROM resources r WHERE r.id IN (p_memory,p_replaces) AND r.provider='memory' AND r.relationship_snapshot IS NULL
   ORDER BY r.id FOR UPDATE;
 PERFORM 1 FROM memories m WHERE m.id IN (p_memory,p_replaces) ORDER BY m.id FOR UPDATE;
 SELECT * INTO x FROM memories WHERE id=p_memory;
 SELECT * INTO y FROM memories WHERE id=p_replaces;
 IF x.id IS NULL OR y.id IS NULL
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=x.id AND r.status='current')
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=y.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id IN (x.id,y.id))
   OR NOT reads_subject(p_principal,x.subject_id) OR NOT reads_subject(p_principal,y.subject_id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 IF NOT memory_may_change(p_principal,y.subject_id,y.author_id,scoped) OR NOT memory_may_change(p_principal,x.subject_id,x.author_id,scoped) THEN
   RAISE EXCEPTION 'Only these memories'' author or a Family admin may say one replaces the other' USING ERRCODE='42501';
 END IF;
 IF x.subject_id<>y.subject_id THEN
   RAISE EXCEPTION 'Two memories in different scopes never replace each other' USING ERRCODE='22023';
 END IF;
 IF x.hold IS NOT NULL OR y.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review neither replaces another nor is replaced until a person releases it'
     USING ERRCODE='22023',HINT='held';
 END IF;
 IF x.status='deprecated' OR y.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) neither replaces another nor is replaced: it is out of current answers already'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 IF y.status='canonical' AND x.status<>'canonical' THEN
   RAISE EXCEPTION 'Memory % is confirmed (canonical): only a confirmed memory replaces it. Confirm memory % first (set_status canonical, once the person agrees), then ask again',
     y.id,x.id USING ERRCODE='22023',HINT='protected';
 END IF;
 xs:=coalesce(x.valid_from,x.created_at);
 ys:=coalesce(y.valid_from,y.created_at);
 IF xs<=ys THEN
   RAISE EXCEPTION 'Memory % began being true at or before memory % did, so it cannot replace it: give it the start it had (update valid_from), or name them the other way round',
     x.id,y.id USING ERRCODE='22023';
 END IF;
 IF y.valid_until=xs AND EXISTS(SELECT 1 FROM made_links l WHERE l.source_id=x.id AND l.target_id=y.id AND l.type='supersedes'
     AND l.lifecycle='active') THEN reason:='replaced';
 ELSIF y.valid_until<=xs THEN reason:='ended';
 ELSIF x.valid_until IS NOT NULL AND (y.valid_until IS NULL OR y.valid_until>=x.valid_until) THEN reason:='enclosed';
 END IF;
 result:=jsonb_build_object('id',x.id,'replaces',y.id,'revision',y.revision,'subject',y.subject_id,'scope',y.scope,
   'status',y.status,'scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
 IF reason IS NOT NULL THEN
   RETURN result||jsonb_build_object('action','unchanged','reason',reason,'at',y.valid_until,'until',x.valid_until);
 END IF;
 code:=left(encode(sha256(convert_to(concat_ws('|','supersede',x.id,x.revision,y.id,y.revision,
   (extract(epoch FROM xs)*1000000)::bigint),'UTF8')),'hex'),16);
 IF p_confirmation IS NULL THEN
   RETURN result||jsonb_build_object('action','plan','at',xs,'confirmation',code);
 END IF;
 IF p_confirmation<>code THEN
   RAISE EXCEPTION 'These memories changed since that plan, or the confirmation is not its own: ask for the plan again (no confirmation)'
     USING ERRCODE='40001';
 END IF;
 PERFORM memory_retire(y);
 UPDATE memories SET valid_until=xs,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=y.id RETURNING * INTO y;
 INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,made_by)
 VALUES(gen_random_uuid(),x.id,y.id,'supersedes','verified','',p_principal)
 ON CONFLICT(source_id,target_id,type) DO UPDATE SET lifecycle='active',verification='verified';
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(y.id,y.subject_id,y.revision,'superseded',p_principal,channel,p_client,
   jsonb_build_object('fields',jsonb_build_array('validity'),'by',x.id,'until',xs));
 RETURN result||jsonb_build_object('action','superseded','at',xs,'revision',y.revision);
END $$;

-- clear_memory_flags: a memory's retraction flags taken off once it has been checked and still stands (upstream's
-- keep), by whoever may change it. One audit row, flags_cleared, naming the sources; none when it had none.
CREATE FUNCTION clear_memory_flags(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; cleared uuid[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 WITH gone AS (DELETE FROM memory_retraction_flags f WHERE f.memory_id=m.id RETURNING f.retracted_id)
 SELECT coalesce(array_agg(g.retracted_id ORDER BY g.retracted_id),'{}') INTO cleared FROM gone g;
 IF cardinality(cleared)=0 THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true);
 END IF;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'flags_cleared',p_principal,channel,p_client,jsonb_build_object('retracted',to_jsonb(cleared)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('changed',jsonb_build_array('retracted_sources'));
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_replaced_by(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_validity_state(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_links_kept(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire_cascade(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_versions_prune() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_own_changes(uuid,uuid,text,integer,integer,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_flags_moved(memories,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_move_replaced(memories,timestamptz,timestamptz,uuid,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_cascade_note(uuid,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_cascade_lock(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retract(memories,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_unretract(memories,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_retraction() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_memory_validity(uuid,uuid,jsonb,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION supersede_memory(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION clear_memory_flags(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_replaced_by(uuid),memory_validity_state(uuid),memory_links_kept(uuid),
 memory_retire_cascade(memories),memory_versions_prune(),memory_own_changes(uuid,uuid,text,integer,integer,boolean),
 memory_flags_moved(memories,uuid,text,text),
 memory_move_replaced(memories,timestamptz,timestamptz,uuid,text,text,boolean),
 memory_cascade_note(uuid,text,uuid),memory_cascade_lock(uuid),memory_retract(memories,uuid,text,text),
 memory_unretract(memories,uuid,text,text),memory_event_retraction(),set_memory_validity(uuid,uuid,jsonb,integer,text,text,text),
 supersede_memory(uuid,uuid,uuid,text,text,text),clear_memory_flags(uuid,uuid,integer,text,text) FROM PUBLIC;
