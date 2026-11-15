-- Duplicate copies flagged on save, and the protection rules (SB-P5-02 and SB-P5-04 in
-- docs/dev/second-brain-parity/backlog.md; story 9's duplicate case and story 13; ledger rows WRT-05, WRT-08, INT-08 and
-- CFG-02; D-028, D-033, D-211; docs/dev/memory.md section 5). Adapted from Second Brain's duplicate check and its
-- protection rules (src/capture/duplicate.ts, src/capture/entry.ts, src/constants.ts and src/tags/system.ts at the pinned
-- commit, MIT; NOTICE).
--
-- Duplicates. Upstream blocks a save at 0.95 and flags it from 0.85, then lets a model merge, replace or keep both. Here
-- the engine runs no model that decides (D-033) and refuses nothing as a copy (D-211: no threshold tells a duplicate
-- from a contradiction that changes one number or day): a new memory is saved as any other, and when its words are close
-- to a memory already in its own scope, it is flagged for a person's review naming that memory (at most 3, closest
-- first). The engine scores the words (Core/MemoryDuplicates.cs: the live model's vector of the words alone, the recipe
-- the thresholds were tuned on, against today's or the tuned duplicate threshold, EmbeddingThresholds; or, with no model,
-- the same words, case and spacing aside) and hands the copies here (flag_memory_duplicates), which keeps only those
-- that are still live in the memory's own scope and writes one audit row, duplicate_flagged, naming them with the model
-- and the threshold. Only the memory's writer flags it, right after saving it (its revision unchanged), so no caller
-- flags another's memory or an older one. Nothing changes in either memory: no revision moves, no version is kept, and
-- the copy keeps its words, status, window, slot and links.
--
-- A flag stands while both memories are live (in the catalog, not in the trash, not held for review, not marked wrong,
-- their window open) and in one scope (memory_duplicate_copies). So the review ends by itself when a person forgets one
-- of them, marks one wrong, replaces the older (049's supersede) or a hold comes; and a person keeps both by taking the
-- flag off (clear_memory_duplicates, flags_cleared). A move between scopes (048's share) takes the moving memory's flags
-- off and those naming it, each audited where its memory is (unflagged), so a copy in another scope is never named to
-- someone who cannot read it. A held memory is never flagged and never named. The review list is one predicate,
-- memory_duplicate_listed, which every list and count reads, so a count is the length of its list.
--
-- Protection (upstream's rules for its automatic merge, replace and supersede, which here can only be a caller's
-- supersede, 049): a confirmed (canonical) memory, as before, and since this migration one of importance 4 or 5, is
-- replaced only by a confirmed memory; a memory saved from a session transcript (source claude-code, codex-session or
-- cursor-session, upstream's TRANSCRIPT_SOURCES) never replaces one of another source; and through an AI client's
-- channel a memory in a capsule slot is never replaced, since that would change the capsule, a person's step (D-172).
-- Each is refused (protected) before a plan is answered, so nothing is written. A retry of a supersede already applied
-- still answers unchanged. The other rules hold by construction and have their tests: no step merges memories or
-- changes a copy (above); a digest draws from no memory of importance 4 or 5, none in a capsule slot and none of the
-- engine's own, and changes none (054); no job writes a memory through the checked writers, whose memory_channel takes
-- no system channel and whose role check takes no system login; and a digest whose words or tags anyone changed since
-- it was written is no longer the engine's own (memory_system_row: a person's edit marks it).
--
-- Nothing calls these but the memory tools and the app's Memories view, both made only with memory.tools on (off by
-- default), and tests: with none called no memory is flagged, and every answer is as before, a supersede's included
-- unless it names a memory of importance 4 or 5, one from a transcript source or one in a capsule slot, which no install
-- holds without the memory tools.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read, one in the trash or one it did not write (a flag); 40001 a stale expected revision; 22023
-- invalid input and, with a hint the engine reads, protected. A refused call writes nothing. The number is provisional.

-- A memory's flags as a copy: which memory it is close to, how close (the dot product of the two vectors, or 1 for the
-- same words), by which model (or exact) and against which threshold, since when. No key on the copy, so a flag
-- outlives a copy deleted for good, which then no longer stands. A flag is no state of the memory: it moves no revision
-- and keeps no version; its audit rows (duplicate_flagged, unflagged, flags_cleared) say when it came and went.
CREATE TABLE memory_duplicate_flags(
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 copy_id uuid NOT NULL,
 score real NOT NULL CHECK(score BETWEEN 0 AND 1),
 model text NOT NULL CHECK(model ~ '^[a-z0-9][a-z0-9._:-]{0,127}$'),
 threshold real NOT NULL CHECK(threshold BETWEEN 0 AND 1),
 flagged_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(memory_id,copy_id),
 CHECK(memory_id<>copy_id)
);
CREATE INDEX memory_duplicate_flags_copy ON memory_duplicate_flags(copy_id);
REVOKE ALL ON memory_duplicate_flags FROM PUBLIC;

-- The list of checked tables in force, with the flags added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_duplicate_flags']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- Whether a memory takes part in the duplicate review: in the catalog, not in the trash, not held for review, not
-- marked wrong (deprecated), and its window open (049: a replaced or ended memory is history, never a copy, as
-- upstream's superseded rows are never a duplicate).
CREATE FUNCTION memory_duplicate_live(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memories m
   JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
   WHERE m.id=p_memory AND m.hold IS NULL AND m.status<>'deprecated' AND (m.valid_until IS NULL OR m.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id))
$$;

-- The copies a memory's flags name that stand now: live, in the memory's own scope, closest first.
CREATE FUNCTION memory_duplicate_copies(p_memory uuid) RETURNS TABLE(copy_id uuid,score real) LANGUAGE sql STABLE AS $$
 SELECT f.copy_id,f.score FROM memory_duplicate_flags f
 JOIN memories m ON m.id=f.memory_id
 JOIN memories c ON c.id=f.copy_id AND c.subject_id=m.subject_id
 WHERE f.memory_id=p_memory AND memory_duplicate_live(c.id)
 ORDER BY f.score DESC,f.copy_id
$$;

-- The duplicate review, one definition every list and count reads: a live memory with a flag that stands.
CREATE FUNCTION memory_duplicate_listed(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT memory_duplicate_live(p_memory) AND EXISTS(SELECT 1 FROM memory_duplicate_copies(p_memory))
$$;

-- flag_memory_duplicates: the copies the engine found close to a memory its caller has just saved (p_copies, at most 3,
-- each {id, score}, the score at or above p_threshold), flagged on it. Only its writer flags it, at the revision it
-- saved (a memory changed since is flagged no more), and only while it is live; each copy must be live in its scope,
-- else it is left out. One audit row, duplicate_flagged, when any is kept. Answers the copies kept.
CREATE FUNCTION flag_memory_duplicates(p_principal uuid,p_memory uuid,p_revision integer,p_copies jsonb,p_model text,
 p_threshold real,p_channel text,p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; m memories; c memories; one jsonb; v_score real; v_copy uuid; kept jsonb:='[]'::jsonb; added integer;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_copies IS NULL OR jsonb_typeof(p_copies)<>'array' OR jsonb_array_length(p_copies)>3
   OR p_model IS NULL OR p_model !~ '^[a-z0-9][a-z0-9._:-]{0,127}$'
   OR p_threshold IS NULL OR NOT (p_threshold BETWEEN 0 AND 1) OR p_revision IS NULL THEN
   RAISE EXCEPTION 'A duplicate flag names at most 3 copies, the model that scored them and the threshold they reached'
     USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(p_copies) AS x(item) WHERE jsonb_typeof(x.item)<>'object'
     OR jsonb_typeof(x.item->'score') IS DISTINCT FROM 'number' OR jsonb_typeof(x.item->'id') IS DISTINCT FROM 'string'
     OR (x.item->>'id') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     OR CASE WHEN jsonb_typeof(x.item->'score')='number' THEN (x.item->>'score')::real NOT BETWEEN p_threshold AND 1 ELSE true END) THEN
   RAISE EXCEPTION 'Each copy is an id and a score from the threshold to 1' USING ERRCODE='22023';
 END IF;
 -- The memory locked as every change locks one (its resources row, then the memory, D-086).
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
 SELECT * INTO m FROM memories WHERE id=p_memory FOR UPDATE;
 IF NOT FOUND OR m.created_by IS DISTINCT FROM p_principal OR NOT reads_subject(p_principal,m.subject_id)
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 IF m.revision<>p_revision OR NOT memory_duplicate_live(m.id) THEN
   RETURN jsonb_build_object('copies','[]'::jsonb);
 END IF;
 FOR one IN SELECT x.item FROM jsonb_array_elements(p_copies) AS x(item) ORDER BY (x.item->>'score')::real DESC,x.item->>'id' LOOP
   v_copy:=(one->>'id')::uuid;
   v_score:=(one->>'score')::real;
   -- The copy locked as it is read, so a share of it in flight is waited for and seen: a copy that moved scope is
   -- skipped, never flagged behind the share's trigger.
   SELECT * INTO c FROM memories x WHERE x.id=v_copy FOR SHARE;
   CONTINUE WHEN c.id IS NULL OR c.id=m.id OR c.subject_id<>m.subject_id OR NOT memory_duplicate_live(c.id);
   INSERT INTO memory_duplicate_flags(memory_id,copy_id,score,model,threshold) VALUES(m.id,c.id,v_score,p_model,p_threshold)
   ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS added=ROW_COUNT;
   IF added>0 THEN kept:=kept||jsonb_build_array(jsonb_build_object('id',c.id,'score',v_score)); END IF;
 END LOOP;
 IF jsonb_array_length(kept)>0 THEN
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'duplicate_flagged',p_principal,channel,p_client,
     jsonb_build_object('copies',kept,'model',p_model,'threshold',p_threshold));
 END IF;
 RETURN jsonb_build_object('copies',kept);
END $$;

-- clear_memory_duplicates: a person keeps both, so the memory's duplicate flags come off (upstream has no such step: its
-- duplicate-candidate tag stays), by whoever may change it (033's author lock), through any channel, as 049's flags come
-- off. One audit row, flags_cleared, naming the copies; none when it had none.
CREATE FUNCTION clear_memory_duplicates(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; cleared uuid[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 WITH gone AS (DELETE FROM memory_duplicate_flags f WHERE f.memory_id=m.id RETURNING f.copy_id)
 SELECT coalesce(array_agg(g.copy_id ORDER BY g.copy_id),'{}') INTO cleared FROM gone g;
 IF cardinality(cleared)=0 THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true);
 END IF;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'flags_cleared',p_principal,channel,p_client,
   jsonb_build_object('cause','duplicate','copies',to_jsonb(cleared)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('changed',jsonb_build_array('duplicate_of'));
END $$;

-- A move between scopes (048): the memory has left the scope its flags were set in, so its own duplicate flags come off
-- (audited on it, where it is now) and so do those naming it (audited on each flagged memory, in its old scope, which
-- the mover read it in), with the move's login, channel and client.
CREATE FUNCTION memory_event_duplicates_moved() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE x memories; y memories; gone uuid[]; d uuid;
BEGIN
 SELECT * INTO x FROM memories WHERE id=NEW.memory_id;
 IF NOT FOUND THEN RETURN NULL; END IF;
 WITH off AS (DELETE FROM memory_duplicate_flags f WHERE f.memory_id=x.id RETURNING f.copy_id)
 SELECT coalesce(array_agg(o.copy_id ORDER BY o.copy_id),'{}') INTO gone FROM off o;
 IF cardinality(gone)>0 THEN
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(x.id,x.subject_id,x.revision,'unflagged',NEW.principal_id,NEW.channel,NEW.client,
     jsonb_build_object('cause','moved','copies',to_jsonb(gone)));
 END IF;
 FOR d IN DELETE FROM memory_duplicate_flags f WHERE f.copy_id=x.id RETURNING f.memory_id
 LOOP
   SELECT * INTO y FROM memories m WHERE m.id=d;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'unflagged',NEW.principal_id,NEW.channel,NEW.client,
     jsonb_build_object('cause','moved','copies',jsonb_build_array(x.id)));
 END LOOP;
 RETURN NULL;
END $$;
CREATE TRIGGER memory_events_duplicates_moved AFTER INSERT ON memory_events FOR EACH ROW
 WHEN (NEW.memory_id IS NOT NULL AND NEW.event IN ('shared','unshared'))
 EXECUTE FUNCTION memory_event_duplicates_moved();

-- The sources a session transcript is saved from (upstream's TRANSCRIPT_SOURCES, and gemini-session for the fourth agent
-- harness/hooks knows, SB-P7-02): a memory of one never replaces a memory of another source. MemoryInput.TranscriptSources
-- lists the same, harness/hooks/agents.mjs names one for each agent, and tests keep the three equal.
CREATE FUNCTION memory_transcript_sources() RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
 SELECT ARRAY['claude-code','codex-session','cursor-session','gemini-session']
$$;

-- Whether a memory is the engine's own, untouched: labelled source system (a digest, 054) and nobody has changed its
-- words or tags since it was written (an append, or a change whose fields name content or tags, through any channel).
-- A person's or a client's edit marks it theirs, as upstream's user-edited tag does.
CREATE FUNCTION memory_system_row(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT m.source='system' AND NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id
   AND (e.event='appended' OR jsonb_typeof(e.detail->'fields')='array' AND e.detail->'fields' ?| ARRAY['content','tags']))
 FROM memories m WHERE m.id=p_memory
$$;

-- 049's supersede_memory, with the protection rules beside its canonical one: a memory of importance 4 or 5 is replaced
-- only by a confirmed memory too, a memory from a transcript source never replaces one of another source, and an AI
-- client's channel never replaces a memory in a capsule slot. The new rules are read only for a supersede still to
-- plan or apply (reason none), so a retry of one already applied answers unchanged as before. Every other line is 049's.
CREATE OR REPLACE FUNCTION supersede_memory(p_principal uuid,p_memory uuid,p_replaces uuid,p_confirmation text,p_channel text,
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
 -- 063: the protection rules (SB-P5-04), for a supersede still to plan or apply.
 IF reason IS NULL THEN
   IF coalesce(y.importance,0)>=4 AND x.status<>'canonical' THEN
     RAISE EXCEPTION 'Memory % is marked important (importance %): only a confirmed memory replaces it. Confirm memory % first (set_status canonical, once the person agrees), then ask again',
       y.id,y.importance,x.id USING ERRCODE='22023',HINT='protected';
   END IF;
   IF x.source=ANY(memory_transcript_sources()) AND x.source<>y.source THEN
     RAISE EXCEPTION 'Memory % was saved from a session transcript (%), which never replaces a memory from another source: if memory % is out of date, ask the person and update it, or save the new fact in their words',
       x.id,x.source,y.id USING ERRCODE='22023',HINT='protected';
   END IF;
   IF y.capsule IS NOT NULL AND channel='mcp' THEN
     RAISE EXCEPTION 'Memory % is in a capsule slot, and replacing it would change the capsule, which only a person does: the person takes it out of its slot first (the app''s Memories view, or rhythm capsule at the terminal), then ask again',
       y.id USING ERRCODE='22023',HINT='protected';
   END IF;
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

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list and the replaced supersede get their search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_duplicate_live(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_duplicate_copies(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_duplicate_listed(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION flag_memory_duplicates(uuid,uuid,integer,jsonb,text,real,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION clear_memory_duplicates(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_duplicates_moved() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_transcript_sources() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_system_row(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION supersede_memory(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_duplicate_live(uuid),memory_duplicate_copies(uuid),memory_duplicate_listed(uuid),
 flag_memory_duplicates(uuid,uuid,integer,jsonb,text,real,text,text),clear_memory_duplicates(uuid,uuid,integer,text,text),
 memory_event_duplicates_moved(),memory_transcript_sources(),memory_system_row(uuid),
 supersede_memory(uuid,uuid,uuid,text,text,text) FROM PUBLIC;
