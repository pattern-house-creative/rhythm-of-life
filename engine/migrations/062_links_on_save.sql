-- Links on save (SB-P4-03 in docs/dev/second-brain-parity/backlog.md; D-015, D-067, D-191, D-194, D-200, D-203, D-211;
-- docs/dev/memory.md section 8; ledger rows WRT-11, GRA-04, JOB-03). Adapted from Second Brain's edge inference on write
-- (src/graph/edges.ts at the pinned commit, MIT; NOTICE): when a memory is saved, it is linked to at most 3 of the
-- memories of its scope whose meaning is closest above a threshold, relates_to, or follows when both are episodic, the
-- earlier was written within 30 minutes before it and it is the only one of them so; the link's weight is the score, and
-- the same runs again when the memory's words or kind change, so the links upkeep (056) marked out of date are drawn
-- again where they still hold.
--
-- infer_memory_links(principal, memory, text_revision, vector, model, threshold, channel, client): the engine's step
-- after a write that saved a memory's words (Core/MemoryLinkInference.cs: a new memory, an append, a change or an undo
-- of its words or kind, a release of its hold). The engine embeds the memory's words (the map's recipe with no title) and
-- names the revision of its text they were read at; nothing here reads a model.
-- - The memory: still at that text revision, current (not replaced, ended or retracted, 049), out of the trash, not held
--   for review, and the caller's to change (033's author lock: the caller just wrote it). Otherwise nothing is done
--   (skipped): a later change runs it again.
-- - The candidates: memories of the same scope (two scopes never link, D-194; a private memory and a Family one never),
--   current, out of the trash, not held (a held memory has no passages, 026), never the memory itself, never one a
--   person or an AI client dismissed with it, of any type (memory_link_dismissals, 052 and 057: a dismissed pair is never
--   linked again), with passages of the named model at their current text revision (the embeddings index's, read by the
--   memory's id; adapters.embeddings.sources lists memories), each scored by its best passage: the dot product of two
--   unit vectors, rounded to 4 places, as ThresholdTuning scores the labelled pairs. Those at or above the threshold,
--   the closest 3, ties by id.
-- - Each of the 3 that is linked to the memory already, by an active made link of any type either way (a person's, an AI
--   client's, a supersede's or an automatic one), is left as it is. Else an automatic link between them of a type a save
--   draws that a change of words left out of date (stale, 056), whichever end changed, is drawn again while their kinds
--   and times still allow it: relates_to either way (an import may store it larger id first), follows from the later
--   memory to the earlier, so an edit of either end of a follows keeps it a follows. It is made active, its weight the
--   new score, from the source end's revision now (whichever end that is), checked at the moment of writing, its maker
--   and its import flag as they were, so one an import brought is kept past an edit while it still holds (D-200). Else
--   its type: follows from the memory to it when both are episodic (051's episodic rule), it was written within 30
--   minutes before the memory and no other of the 3 is so; else relates_to, stored smaller id first (D-191). A link of
--   that type in another lifecycle (an import's retired, revoked or deleted one, or an out of date one that no longer
--   applies) is left as it is. Otherwise a new made link: verification suggested (an automatic link, upstream's
--   provenance inferred, LinkTypes.Provenance; never verified, D-015: a person confirms it by making the same link, 052),
--   made by the caller (the login whose write it follows), no evidence, the score as its confidence, the source end's
--   revision, checked at the moment of writing.
-- - Beyond the 3: every other memory of the scope that a stale automatic link of those types joins to it and that still
--   scores at or above the threshold has that link drawn again in the same way, and no new one made: the cap is on what
--   a save draws, not on what still holds, so an edit of a memory other saves linked to keeps every link that holds.
-- - Every end is locked as a link locks them (052's memory_link_lock, every end in id order, for share) before the
--   memory and each candidate are checked again and anything is written, a candidate's words included (its text
--   revision as scored), so a change, a hold, a trip to the trash or a dismissal at the same moment is either seen or
--   waits.
-- A link on save writes no audit row and moves no revision, as a link (052); 056's upkeep invalidates and prunes it as
-- any automatic link, and the trash keeps it (038). A person or an AI client removes it with unlink (052), which keeps
-- a dismissal so it is never drawn again; since an automatic link is the engine's suggestion, whoever may change either
-- end may (memory_link_may_remove, replaced here). A move between scopes (048) removes the moving memory's automatic
-- links whose other end is then in another scope (memory_event_links_moved, a trigger on the share's audit row), and a
-- restore from the trash (051) does not bring back an automatic link whose other end moved to another scope meanwhile
-- (made_link_one_scope, a trigger on made_links: the restore counts it with the links that did not come back), so none
-- keeps a memory in Family through 048's refusal of a link to another person's private record.
--
-- Nothing calls infer_memory_links but the engine's memory store after a write through the memory tools or the app
-- (MemoryStore, MemoryLinkInference), made only with memory.tools on (off by default), the live model loaded and
-- adapters.embeddings.sources listing memories; with memory.tools off no write calls it, and every answer is as
-- before. No table changes; it replaces 052's memory_link_may_remove (an automatic link only: every other link answers
-- as before) and adds two triggers that act only on automatic links joining two scopes: one on memory_events (a share
-- or un-share) and one on made_links (a restore). With memory.scopes off every memory is the household's, so neither
-- finds such a link. The number is provisional.
--
-- Errors, as 052's: 42501 not available to this login; 22023 invalid input.

-- At most this many memories a save links to (upstream's EDGE_INFER_MAX), and how soon after an episodic memory another
-- follows it (upstream's GRAPH_FOLLOWS_WINDOW_MS): fixed here, not settings.
CREATE FUNCTION memory_links_on_save() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 3 $$;
CREATE FUNCTION memory_follows_window() RETURNS interval LANGUAGE sql IMMUTABLE AS $$ SELECT interval '30 minutes' $$;

-- Whether the memory n may be linked on save: current in the catalog and in time, out of the trash and not held.
CREATE FUNCTION memory_linkable(n memories) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT n.id IS NOT NULL AND n.hold IS NULL AND memory_validity_state(n.id)='current'
   AND EXISTS(SELECT 1 FROM resources r WHERE r.id=n.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current')
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=n.id)
$$;

CREATE FUNCTION infer_memory_links(p_principal uuid,p_memory uuid,p_text_revision integer,p_vector text,p_model text,
 p_threshold double precision,p_channel text,p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; v public.vector; m memories; n memories; c record; picked uuid[]:='{}'; scores double precision[]:='{}';
 revs integer[]:='{}'; held uuid[]:='{}'; held_scores double precision[]:='{}'; held_revs integer[]:='{}'; top integer;
 follows uuid; near integer:=0; i integer; v_type text; v_from uuid; v_to uuid; l made_links; outcome text; links jsonb:='[]'::jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Linking memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_memory IS NULL OR p_text_revision IS NULL THEN
   RAISE EXCEPTION 'A link on save names a memory and the revision of its words' USING ERRCODE='22023';
 END IF;
 IF p_threshold IS NULL OR p_threshold<=0 OR p_threshold>1 THEN
   RAISE EXCEPTION 'A threshold is above 0 and at most 1' USING ERRCODE='22023';
 END IF;
 IF p_model IS NULL OR length(p_model) NOT BETWEEN 1 AND 200 THEN
   RAISE EXCEPTION 'A link on save names the model its vector is of' USING ERRCODE='22023';
 END IF;
 BEGIN
   v:=p_vector::public.vector;
 EXCEPTION WHEN data_exception OR invalid_text_representation OR feature_not_supported THEN
   RAISE EXCEPTION 'A vector is a list of numbers in brackets' USING ERRCODE='22023';
 END;
 SELECT * INTO m FROM memories x WHERE x.id=p_memory;
 IF m.id IS NULL OR m.text_revision<>p_text_revision OR NOT memory_linkable(m)
   OR NOT memory_may_change(p_principal,m.subject_id,m.author_id,scoped) THEN
   RETURN jsonb_build_object('memory',p_memory,'links','[]'::jsonb,'skipped',true);
 END IF;
 -- The closest memories of its scope above the threshold, each by its best passage of this model at its current words,
 -- with the revision of those words, so a change of them before the locks below is seen.
 FOR c IN
   SELECT k.resource_id AS id,x.text_revision AS rev,max(round((-(k.embedding OPERATOR(public.<#>) v))::numeric,4))::double precision AS score
   FROM resource_chunks k JOIN memories x ON x.id=k.resource_id
   WHERE k.model=p_model AND k.source_revision='memory:'||x.text_revision AND x.subject_id=m.subject_id AND x.id<>m.id
     AND memory_linkable(x)
     AND NOT EXISTS(SELECT 1 FROM memory_link_dismissals d WHERE d.low_id=least(m.id,x.id) AND d.high_id=greatest(m.id,x.id))
   GROUP BY k.resource_id,x.text_revision
   HAVING max(round((-(k.embedding OPERATOR(public.<#>) v))::numeric,4))>=p_threshold
   ORDER BY 3 DESC,1
   LIMIT memory_links_on_save()
 LOOP
   picked:=picked||c.id; scores:=scores||c.score; revs:=revs||c.rev;
 END LOOP;
 top:=cardinality(picked);
 -- Beyond those: each other memory of its scope that an automatic link of a type a save draws joins to it, left out of
 -- date by an edit (stale, 056), and that still scores at or above the threshold. That link is drawn again too, and no
 -- new one is made: the cap of 3 is on the links a save draws, not on those that still hold (D-200).
 FOR c IN
   SELECT k.resource_id AS id,x.text_revision AS rev,max(round((-(k.embedding OPERATOR(public.<#>) v))::numeric,4))::double precision AS score
   FROM resource_chunks k JOIN memories x ON x.id=k.resource_id
   WHERE k.model=p_model AND k.source_revision='memory:'||x.text_revision AND x.subject_id=m.subject_id AND x.id<>m.id
     AND NOT x.id=ANY(picked) AND memory_linkable(x)
     AND EXISTS(SELECT 1 FROM made_links y WHERE y.verification='suggested' AND y.lifecycle='stale' AND y.type IN ('relates_to','follows')
       AND (y.source_id=m.id AND y.target_id=x.id OR y.source_id=x.id AND y.target_id=m.id))
     AND NOT EXISTS(SELECT 1 FROM memory_link_dismissals d WHERE d.low_id=least(m.id,x.id) AND d.high_id=greatest(m.id,x.id))
   GROUP BY k.resource_id,x.text_revision
   HAVING max(round((-(k.embedding OPERATOR(public.<#>) v))::numeric,4))>=p_threshold
   ORDER BY 1
 LOOP
   held:=held||c.id; held_scores:=held_scores||c.score; held_revs:=held_revs||c.rev;
 END LOOP;
 picked:=picked||held; scores:=scores||held_scores; revs:=revs||held_revs;
 IF cardinality(picked)=0 THEN
   RETURN jsonb_build_object('memory',m.id,'links','[]'::jsonb,'skipped',false);
 END IF;
 -- Every end locked as a link locks its two, in id order, then each checked again.
 PERFORM 1 FROM resources r WHERE r.id=ANY(picked||m.id) AND r.provider='memory' AND r.relationship_snapshot IS NULL ORDER BY r.id FOR SHARE;
 PERFORM 1 FROM memories x WHERE x.id=ANY(picked||m.id) ORDER BY x.id FOR SHARE;
 SELECT * INTO m FROM memories x WHERE x.id=p_memory;
 IF m.text_revision<>p_text_revision OR NOT memory_linkable(m) THEN
   RETURN jsonb_build_object('memory',p_memory,'links','[]'::jsonb,'skipped',true);
 END IF;
 -- follows: the only one of the closest both episodic and written within the window before the memory.
 FOR i IN 1..top LOOP
   SELECT * INTO n FROM memories x WHERE x.id=picked[i];
   IF link_kinds_allow('follows',m.kind,n.kind) AND n.created_at<=m.created_at AND m.created_at-n.created_at<=memory_follows_window() THEN
     near:=near+1; follows:=n.id;
   END IF;
 END LOOP;
 IF near<>1 THEN follows:=NULL; END IF;
 FOR i IN 1..cardinality(picked) LOOP
   SELECT * INTO n FROM memories x WHERE x.id=picked[i];
   -- Still in the memory's scope and linkable, its words as they were scored, and not dismissed with it.
   CONTINUE WHEN n.subject_id<>m.subject_id OR NOT memory_linkable(n) OR n.text_revision<>revs[i]
     OR EXISTS(SELECT 1 FROM memory_link_dismissals d WHERE d.low_id=least(m.id,n.id) AND d.high_id=greatest(m.id,n.id));
   l:=NULL;
   IF EXISTS(SELECT 1 FROM made_links x WHERE x.lifecycle='active'
     AND (x.source_id=m.id AND x.target_id=n.id OR x.source_id=n.id AND x.target_id=m.id)) THEN
     outcome:='linked';
     SELECT * INTO l FROM made_links x WHERE x.lifecycle='active'
       AND (x.source_id=m.id AND x.target_id=n.id OR x.source_id=n.id AND x.target_id=m.id) ORDER BY x.created_at,x.link_id LIMIT 1;
   ELSE
     -- An automatic link between them of a type a save draws, left out of date by an edit of either end, is drawn again
     -- while their kinds and times still allow it, whichever way it runs: relates_to either way (an import may store it
     -- larger id first), follows from the later memory, so an edit of a follows' earlier end keeps it a follows.
     SELECT x.* INTO l FROM made_links x JOIN memories s ON s.id=x.source_id JOIN memories t ON t.id=x.target_id
     WHERE x.verification='suggested' AND x.lifecycle='stale' AND x.type IN ('relates_to','follows')
       AND (x.source_id=m.id AND x.target_id=n.id OR x.source_id=n.id AND x.target_id=m.id)
       AND link_kinds_allow(x.type,s.kind,t.kind)
       AND (x.type<>'follows' OR t.created_at<=s.created_at AND s.created_at-t.created_at<=memory_follows_window())
     ORDER BY x.created_at,x.link_id LIMIT 1 FOR UPDATE OF x;
     IF l.link_id IS NOT NULL THEN
       -- Its maker and import flag as they were; the source end's revision now, whichever end that is; checked at the
       -- moment of writing, so an edit's audit row written before it never reads as later (056).
       UPDATE made_links SET lifecycle='active',evidence='',confidence=scores[i],
         source_revision='memory:'||(CASE WHEN l.source_id=m.id THEN m.revision ELSE n.revision END),checked_at=clock_timestamp()
       WHERE link_id=l.link_id RETURNING * INTO l;
       outcome:='renewed';
     ELSIF i>top THEN
       -- Beyond the closest 3 a link is only drawn again, never made.
       CONTINUE;
     ELSE
       v_type:=CASE WHEN n.id=follows THEN 'follows' ELSE 'relates_to' END;
       IF v_type='follows' THEN v_from:=m.id; v_to:=n.id; ELSE v_from:=least(m.id,n.id); v_to:=greatest(m.id,n.id); END IF;
       -- relates_to has no direction: one an import stored either way is the same link. One of that type in another
       -- lifecycle (an import's retired, revoked or deleted one, or an out of date one that no longer applies) is left.
       SELECT * INTO l FROM made_links x WHERE x.type=v_type AND (x.source_id=v_from AND x.target_id=v_to
         OR v_type='relates_to' AND x.source_id=v_to AND x.target_id=v_from) ORDER BY x.created_at,x.link_id LIMIT 1;
       IF l.link_id IS NULL THEN
         INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,confidence,source_revision,checked_at)
         VALUES(gen_random_uuid(),v_from,v_to,v_type,'suggested','','active',p_principal,scores[i],
           'memory:'||(CASE WHEN v_from=m.id THEN m.revision ELSE n.revision END),clock_timestamp())
         ON CONFLICT(source_id,target_id,type) DO NOTHING
         RETURNING * INTO l;
         IF l.link_id IS NULL THEN
           -- Made by a step that ran at the same time: there already, as that step left it.
           SELECT * INTO l FROM made_links x WHERE x.source_id=v_from AND x.target_id=v_to AND x.type=v_type;
           outcome:='linked';
         ELSE
           outcome:='made';
         END IF;
       ELSE
         outcome:='kept';
       END IF;
     END IF;
   END IF;
   links:=links||jsonb_build_object('id',l.link_id,'memory',n.id,'type',l.type,'from',l.source_id,'to',l.target_id,
     'verification',l.verification,'lifecycle',l.lifecycle,'score',scores[i],'outcome',outcome);
 END LOOP;
 RETURN jsonb_build_object('memory',m.id,'links',links,'skipped',false);
END $$;

-- 052's memory_link_may_remove, with an automatic link (verification suggested: a link on save, or an import's inferred
-- one) removable by whoever may change either end, whatever its type, as an engine suggestion is dismissed by the author
-- of either end (memory.md section 5). So an automatic follows drawn from another member's memory onto a person's own is
-- theirs to remove, and kept as a dismissal (052). Otherwise as 052 wrote it; connections' may_unlink reads it too.
CREATE OR REPLACE FUNCTION memory_link_may_remove(p_principal uuid,l made_links,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT p_scoped THEN principal_has_role(p_principal,ARRAY['harness','app'])
   ELSE coalesce(l.made_by=p_principal,false)
     OR coalesce((SELECT a.person_id FROM principals a WHERE a.id=l.made_by)=(SELECT c.person_id FROM principals c WHERE c.id=p_principal),false)
     OR EXISTS(SELECT 1 FROM memories m WHERE (m.id=l.source_id
         OR m.id=l.target_id AND (l.verification='suggested' OR NOT coalesce((SELECT x.directed FROM link_types() x WHERE x.name=l.type),true)))
       AND memory_may_change(p_principal,m.subject_id,m.author_id,true)) END
$$;

-- A move between scopes (048's share and un-share): the moving memory's automatic links whose other end is not in its
-- scope now are removed, since a link on save never joins two scopes and no person stated them. So none keeps a memory
-- in Family through 048's refusal of a link to another person's private record (only a link a person or an AI client
-- made can), and none is left that the other end's author can neither see nor remove. No dismissal is kept: the pair may
-- be linked again once the two are in one scope again and a save draws it.
CREATE FUNCTION memory_event_links_moved() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 DELETE FROM made_links l USING memories x, resources r
 WHERE x.id=NEW.memory_id AND l.verification='suggested' AND (l.source_id=x.id OR l.target_id=x.id)
   AND r.id=CASE WHEN l.source_id=x.id THEN l.target_id ELSE l.source_id END AND r.relationship_snapshot IS NULL
   AND r.subject_id IS DISTINCT FROM x.subject_id;
 RETURN NULL;
END $$;
CREATE TRIGGER memory_events_links_moved AFTER INSERT ON memory_events FOR EACH ROW
 WHEN (NEW.memory_id IS NOT NULL AND NEW.event IN ('shared','unshared'))
 EXECUTE FUNCTION memory_event_links_moved();

-- A restore from the trash (051's restore_memory) writes back the links its trash row kept. An automatic one whose other
-- end moved to another scope while this end was in the trash (a share or un-share, whose trigger above cannot reach a
-- link kept in a trash row) would join two scopes, so it is not written: an automatic link (verification suggested)
-- whose two ends are memories of different subjects is skipped before it is written, and the restore counts it with the
-- links that did not come back (links_gone). Both ends are locked first as a link locks them (052's memory_link_lock),
-- so a share of the other end at the same moment is either seen here or waits and then removes the link. No other
-- writer makes such a link (a save links only its own scope, an import only memories of one subject), so this changes
-- only a restore's. No dismissal is kept: the pair may be linked again once the two are in one scope and a save draws it.
CREATE FUNCTION made_link_one_scope() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 PERFORM memory_link_lock(NEW.source_id,NEW.target_id);
 IF EXISTS(SELECT 1 FROM memories s JOIN memories t ON t.id=NEW.target_id WHERE s.id=NEW.source_id AND s.subject_id<>t.subject_id) THEN
   RETURN NULL;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER made_links_one_scope BEFORE INSERT ON made_links FOR EACH ROW
 WHEN (NEW.verification='suggested')
 EXECUTE FUNCTION made_link_one_scope();

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_links_on_save() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_follows_window() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_linkable(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION infer_memory_links(uuid,uuid,integer,text,text,double precision,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 -- CREATE OR REPLACE resets a function's settings, so the replaced removal rule gets its search path again.
 EXECUTE format('ALTER FUNCTION memory_link_may_remove(uuid,made_links,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_links_moved() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION made_link_one_scope() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_links_on_save(),memory_follows_window(),memory_linkable(memories),
 infer_memory_links(uuid,uuid,integer,text,text,double precision,text,text),memory_link_may_remove(uuid,made_links,boolean),
 memory_event_links_moved(),made_link_one_scope() FROM PUBLIC;
