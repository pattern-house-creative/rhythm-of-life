-- Contradiction candidates returned to the calling AI and queued for a person (SB-P5-03 in
-- docs/dev/second-brain-parity/backlog.md; story 9's contradiction case; ledger rows WRT-06, WRT-07 and SEC-16; D-033, D-211,
-- D-218; docs/dev/memory.md section 5), with SB-P5-02's leftovers: the duplicate check on an append, an update, an import
-- and a release as well as a save, and which wins between a flag and a link on save. Adapted from Second Brain's
-- contradiction check (src/capture/duplicate.ts and src/capture/entry.ts at the pinned commit, MIT; NOTICE).
--
-- Upstream asks a model whether a new memory contradicts a close one, checks the id the model names against the rows it
-- was shown, and then closes the older memory's window itself unless a protection rule stands in the way. Here the engine
-- runs no model that decides (D-033) and no score tells a duplicate from a contradiction that changes one number or day
-- (D-211), so the engine names the candidates and neither memory changes:
-- - An offer. When a memory's words are close to memories of its own scope (at or above the install's contradiction
--   threshold, EmbeddingThresholds; Core/MemoryDuplicates.cs scores them), its writer's answer names them, at most 5,
--   closest first, and each is a row here. A row stands while both memories are live (063's memory_duplicate_live: in the
--   catalog, not in the trash, not held, not marked wrong, their window open) and in one scope, so a candidate is only
--   ever one the writer reads; a held memory is never offered and never named. One row a pair, whichever way it was found.
-- - A proposal. The calling AI reads the two (get) and says they conflict (propose_contradiction, with a short reason). An
--   id it was not offered for that memory is refused (not_offered), the same whether that id is unknown, in the trash, held
--   or of another scope, so the refusal tells nothing. A proposal settles nothing: it marks the row for the person.
-- - A person's review. One predicate, memory_contradiction_listed, which every list and count reads, so a count is the
--   length of its list. A pair is in one review at a time: while it is flagged as a possible copy (063) and nobody has named
--   it a contradiction it is in the duplicate review, and once somebody has, in this one, and it leaves the duplicate
--   review (memory_duplicate_copies, replaced here). An offered pair that is no flagged copy is in this one from the start.
-- - Settling. Only a person's channel (the app's login or the terminal's) settles a contradiction
--   (settle_memory_contradiction): keep both, or the one that stands replaces the other (049's supersede, as 063 protects
--   it). An AI client never does (needs_person). Through its channel, while a row stands, the earlier of the two memories
--   (by when it was written) can't be ended (marked wrong, trashed, given an end, replaced, or undone to one of those) and the
--   pair can't be superseded, whichever way the row runs, so the established fact is not rewritten behind the person's back:
--   memory_event_contradiction_guard refuses it from the change's audit row, which rolls the whole change back. A change that
--   finds the earlier ended already ends nothing, and the guard lets it through. Once somebody has named a pair the earlier's
--   words are kept too: an AI client's edit, append or undo that changes them is refused, since it could settle the pair by
--   making the two say the same. An offer nobody has named takes an edit, its own step in the audit trail. The later memory may
--   still be withdrawn: forgetting or marking it wrong ends the review, and it comes back with that memory, as 063's flags do.
--   Once somebody has named a pair, withdrawing the later does not free the earlier: a named row keeps it from an AI client for
--   as long as the later exists in its scope. An offer nobody has named holds only while it stands (Q-161). Nor does an AI
--   client move either memory of a row to another scope, which would take the row off (needs_person); a person may.
--   Nor does an AI client undo a settling. Kept both: neither memory can be ended while the other is live, nor one replaced by
--   the other. Replaced: the replaced memory can't be brought back (undo, or an end taken off), nor the one that stands be
--   replaced by it. Each needs_person, from the same guard, whichever scope either memory is in by then; once a person has
--   ended or reopened one, the guard reads what they left. A settling decides which memories stand, not their words for ever,
--   so an edit of a settled memory is an ordinary edit. A person's keep both on a flagged
--   copy (063's clear_memory_duplicates, through a person's channel) settles the pairs that review showed, and no pair it
--   hides. An AI client's keep both takes the duplicate flag off and the pair stays queued here, unsettled.
--
-- The check on append, update, import and release. flag_memory_duplicates is replaced so that whoever made the revision may
-- flag it, not only the memory's first writer: a person's release of an AI client's memory, or an edit by an admin, is
-- checked as a save is. A later check of the same memory rescores what it flags already, whichever way the flag runs, keeps at
-- most 3 flags standing, never flags a pair the other way round or one somebody kept both of (063's flags_cleared rows name
-- them), and through recheck_memory_review takes off a flag, or an offer nobody has proposed, of either memory's making, whose
-- score fell below its threshold. A proposed row stays through any change of words until a person settles it or a memory goes.
-- review_imported_memory does the same for what rhythm import or rhythm import-second-brain has just added (the audit
-- row's imported, as the operator's; flags and offers are this install's own, never in an export).
--
-- Which wins between a flag and a link on save (D-215 left this open). Upstream draws no inferred edge to a flagged duplicate,
-- since that would spend a slot restating the flag, nor to a conflicting row. Here a named contradiction, and a flag close
-- enough to be a copy for certain (0.95 or more, upstream's block and today's DuplicateBlock: the same words among them), win:
-- infer_memory_links (062, replaced with that one exclusion) draws no automatic link between such a pair, and whichever write
-- makes the pair so removes an automatic link between them, keeping no dismissal, except that a proposal by one who may change
-- neither memory leaves the link for the person's settling. Below that a flag is only a possible copy (no
-- score tells it from a contradiction or a close relation, D-211; a flag from 0.85 has precision 0.889 on the labelled pairs,
-- and from MiniLM's 0.63 only 0.553), those pairs are ones a reader wants shown together by the labels' own rule, and taking
-- their links would cost 18 of the 28 links a save draws on the corpus at today's thresholds, so the flag and the link both
-- stand. A link a person or an AI client made stays, and so does one an import brought. Once a person keeps both, the store
-- draws the link the words allow.
--
-- Nothing calls these but the memory tools, the app's Memories view, rhythm release and the two imports, each with
-- memory.tools on (off by default). With it off no row is written and every answer is as before: the guard and the
-- replaced functions see no row. The number is provisional.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one the
-- caller does not read or one in the trash; 40001 a stale expected revision; 22023 invalid input and, with a hint the engine
-- reads, not_offered, settled and needs_person. A refused call writes nothing.

-- One row a pair. memory_id: the memory whose words were checked; conflict_id: the memory they were close to, which has no
-- key, so a row outlives one deleted for good and then no longer stands. The score, model and threshold as for a duplicate
-- flag; the proposal (who and how, no reason ever in the audit trail); the settling (a person's). A row is no state of a
-- memory: it moves no revision and keeps no version.
CREATE TABLE memory_contradictions(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 conflict_id uuid NOT NULL,
 score real NOT NULL CHECK(score BETWEEN 0 AND 1),
 model text NOT NULL CHECK(model ~ '^[a-z0-9][a-z0-9._:-]{0,127}$'),
 threshold real NOT NULL CHECK(threshold BETWEEN 0 AND 1),
 offered_at timestamptz NOT NULL DEFAULT now(),
 proposed_at timestamptz,
 proposed_by uuid,
 proposed_client text,
 reason text CHECK(reason IS NULL OR (length(reason) BETWEEN 1 AND 200 AND reason !~ '[[:cntrl:]]')),
 settled_at timestamptz,
 settled_by uuid,
 decision text CHECK(decision IN ('keep_both','replaced')),
 CHECK(memory_id<>conflict_id),
 CHECK((settled_at IS NULL)=(decision IS NULL)),
 CHECK(proposed_at IS NOT NULL OR reason IS NULL)
);
CREATE UNIQUE INDEX memory_contradictions_pair ON memory_contradictions(least(memory_id,conflict_id),greatest(memory_id,conflict_id));
CREATE INDEX memory_contradictions_memory ON memory_contradictions(memory_id);
CREATE INDEX memory_contradictions_conflict ON memory_contradictions(conflict_id);
REVOKE ALL ON memory_contradictions FROM PUBLIC;

-- The list of checked tables in force, with these added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_contradictions']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- At most this many candidates a write offers (upstream's five nearest notes).
CREATE FUNCTION memory_contradiction_offers() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 5 $$;

-- Whether a duplicate flag is written between two memories, either way round.
CREATE FUNCTION memory_copy_flagged(a uuid,b uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_duplicate_flags f WHERE f.memory_id=a AND f.copy_id=b OR f.memory_id=b AND f.copy_id=a)
$$;

-- Whether somebody has kept both of a pair (063's flags_cleared rows name the copies they took off): never flagged again.
CREATE FUNCTION memory_copy_kept(a uuid,b uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id IN (a,b) AND e.event='flags_cleared' AND e.detail->>'cause'='duplicate'
   AND e.detail->'copies' ? (CASE WHEN e.memory_id=a THEN b ELSE a END)::text)
$$;

-- Whether a pair is named a contradiction and no person has settled it.
CREATE FUNCTION memory_contradiction_named(a uuid,b uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_contradictions k WHERE least(k.memory_id,k.conflict_id)=least(a,b)
   AND greatest(k.memory_id,k.conflict_id)=greatest(a,b) AND k.proposed_at IS NOT NULL AND k.settled_at IS NULL)
$$;

-- From this score a flag says a copy for certain, and wins over a link on save (above): upstream's block and today's
-- DuplicateBlock (EmbeddingThresholds); a test keeps the two equal.
CREATE FUNCTION memory_copy_certain() RETURNS real LANGUAGE sql IMMUTABLE AS $$ SELECT 0.95::real $$;

-- A pair that takes no automatic link: flagged as a copy for certain, or named a contradiction.
CREATE FUNCTION memory_review_pair(a uuid,b uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_duplicate_flags f WHERE (f.memory_id=a AND f.copy_id=b OR f.memory_id=b AND f.copy_id=a) AND f.score>=memory_copy_certain())
   OR memory_contradiction_named(a,b)
$$;

-- Whether a row stands: no person settled it, both memories live and in one scope.
CREATE FUNCTION memory_contradiction_stands(k memory_contradictions) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT k.settled_at IS NULL AND memory_duplicate_live(k.memory_id) AND memory_duplicate_live(k.conflict_id)
   AND EXISTS(SELECT 1 FROM memories x JOIN memories y ON y.subject_id=x.subject_id WHERE x.id=k.memory_id AND y.id=k.conflict_id)
$$;

-- The person's review, one definition every list and count reads: a row that stands and is named a contradiction, or
-- that is no flagged copy, which the duplicate review holds until somebody names it.
CREATE FUNCTION memory_contradiction_listed(k memory_contradictions) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT memory_contradiction_stands(k) AND (k.proposed_at IS NOT NULL OR NOT memory_copy_flagged(k.memory_id,k.conflict_id))
$$;

-- What a memory's readers are told stands against it, either way round, closest first, at most 10: the other memory, how
-- close, and whether somebody has named it a contradiction. The other is of its own scope, so its readers read it.
CREATE FUNCTION memory_open_contradictions(p_memory uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',o.other,'score',round(o.score::numeric,4),'proposed',o.proposed) ORDER BY o.score DESC,o.other),'[]'::jsonb)
 FROM (SELECT CASE WHEN k.memory_id=p_memory THEN k.conflict_id ELSE k.memory_id END AS other,k.score,k.proposed_at IS NOT NULL AS proposed
   FROM memory_contradictions k WHERE (k.memory_id=p_memory OR k.conflict_id=p_memory) AND memory_contradiction_stands(k)
   ORDER BY k.score DESC,1 LIMIT 10) o
$$;

-- 063's memory_duplicate_copies, leaving out a pair somebody has named a contradiction: it is in the contradiction review
-- now, and at most the 3 closest: a copy back from the trash can make a fourth flag stand, and it waits until one of these
-- comes off, so every list, read and write's answer holds to 3. Otherwise as 063 wrote it.
CREATE OR REPLACE FUNCTION memory_duplicate_copies(p_memory uuid) RETURNS TABLE(copy_id uuid,score real) LANGUAGE sql STABLE AS $$
 SELECT f.copy_id,f.score FROM memory_duplicate_flags f
 JOIN memories m ON m.id=f.memory_id
 JOIN memories c ON c.id=f.copy_id AND c.subject_id=m.subject_id
 WHERE f.memory_id=p_memory AND memory_duplicate_live(c.id) AND NOT memory_contradiction_named(f.memory_id,f.copy_id)
 ORDER BY f.score DESC,f.copy_id
 LIMIT 3
$$;

-- An automatic link between two memories comes off when the pair is flagged as a copy for certain or named: no dismissal is
-- kept, so a person's keep both lets it be drawn again. Neither a link a person or an AI client made (verified, inferred) nor
-- one an import brought is touched.
CREATE FUNCTION memory_review_unlink(a uuid,b uuid) RETURNS void LANGUAGE sql AS $$
 DELETE FROM made_links l WHERE l.verification='suggested' AND NOT l.imported AND l.type IN ('relates_to','follows')
   AND (l.source_id=a AND l.target_id=b OR l.source_id=b AND l.target_id=a)
$$;

-- What a check hands the writers: at most p_max {id, score}, each score from the threshold to 1, with the model and the
-- threshold they reached.
CREATE FUNCTION memory_review_input(p_items jsonb,p_max integer,p_model text,p_threshold real) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 IF p_items IS NULL OR jsonb_typeof(p_items)<>'array' OR jsonb_array_length(p_items)>p_max
   OR p_model IS NULL OR p_model !~ '^[a-z0-9][a-z0-9._:-]{0,127}$' OR p_threshold IS NULL OR NOT (p_threshold BETWEEN 0 AND 1) THEN
   RAISE EXCEPTION 'A check names at most % memories, the model that scored them and the threshold they reached',p_max USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(p_items) AS x(item) WHERE jsonb_typeof(x.item)<>'object'
     OR jsonb_typeof(x.item->'score') IS DISTINCT FROM 'number' OR jsonb_typeof(x.item->'id') IS DISTINCT FROM 'string'
     OR (x.item->>'id') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     OR CASE WHEN jsonb_typeof(x.item->'score')='number' THEN (x.item->>'score')::real NOT BETWEEN p_threshold AND 1 ELSE true END) THEN
   RAISE EXCEPTION 'Each memory is an id and a score from the threshold to 1' USING ERRCODE='22023';
 END IF;
END $$;

-- The memory a check names, locked as every change locks one (its resources row, then the memory, D-086): one the caller
-- reads and whose revision p_revision it made, or that it first wrote. Otherwise as for an unknown memory (P0002).
CREATE FUNCTION memory_review_target(p_principal uuid,p_memory uuid,p_revision integer) RETURNS memories LANGUAGE plpgsql AS $$
DECLARE m memories;
BEGIN
 IF p_revision IS NULL THEN RAISE EXCEPTION 'A check names the revision it read' USING ERRCODE='22023'; END IF;
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
 SELECT * INTO m FROM memories WHERE id=p_memory FOR UPDATE;
 IF NOT FOUND OR NOT reads_subject(p_principal,m.subject_id)
   OR NOT (m.created_by IS NOT DISTINCT FROM p_principal
     OR EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.revision=p_revision AND e.principal_id=p_principal))
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 RETURN m;
END $$;

-- The flags a check found: each copy live in the memory's own scope, rescored when flagged already, whichever way the flag
-- runs; else one flag a pair, not one somebody kept both of, at most 3 standing. One for certain takes off an automatic link
-- between them. One audit row, duplicate_flagged, when any is new. Answers the new ones.
CREATE FUNCTION memory_flag_copies(m memories,p_copies jsonb,p_model text,p_threshold real,p_principal uuid,p_channel text,p_client text)
RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE c memories; one jsonb; v_copy uuid; v_score real; kept jsonb:='[]'::jsonb; added integer; standing integer;
BEGIN
 SELECT count(*) INTO standing FROM memory_duplicate_copies(m.id);
 FOR one IN SELECT x.item FROM jsonb_array_elements(p_copies) AS x(item) ORDER BY (x.item->>'score')::real DESC,x.item->>'id' LOOP
   v_copy:=(one->>'id')::uuid;
   v_score:=(one->>'score')::real;
   -- The copy locked as it is read, so a share of it in flight is waited for and seen.
   SELECT * INTO c FROM memories x WHERE x.id=v_copy FOR SHARE;
   CONTINUE WHEN c.id IS NULL OR c.id=m.id OR c.subject_id<>m.subject_id OR NOT memory_duplicate_live(c.id);
   UPDATE memory_duplicate_flags f SET score=v_score,model=p_model,threshold=p_threshold
   WHERE f.memory_id=m.id AND f.copy_id=c.id OR f.memory_id=c.id AND f.copy_id=m.id;
   GET DIAGNOSTICS added=ROW_COUNT;
   IF added>0 THEN
     IF v_score>=memory_copy_certain() THEN PERFORM memory_review_unlink(m.id,c.id); END IF;
     CONTINUE;
   END IF;
   CONTINUE WHEN standing>=3 OR memory_copy_flagged(m.id,c.id) OR memory_copy_kept(m.id,c.id);
   INSERT INTO memory_duplicate_flags(memory_id,copy_id,score,model,threshold) VALUES(m.id,c.id,v_score,p_model,p_threshold)
   ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS added=ROW_COUNT;
   IF added>0 THEN
     standing:=standing+1;
     kept:=kept||jsonb_build_array(jsonb_build_object('id',c.id,'score',v_score));
     IF v_score>=memory_copy_certain() THEN PERFORM memory_review_unlink(m.id,c.id); END IF;
   END IF;
 END LOOP;
 IF jsonb_array_length(kept)>0 THEN
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'duplicate_flagged',p_principal,p_channel,p_client,
     jsonb_build_object('copies',kept,'model',p_model,'threshold',p_threshold));
 END IF;
 RETURN kept;
END $$;

-- The offers a check found: each candidate live in the memory's own scope, one row a pair (a row there already, settled or
-- not, is left; this memory's own open one is rescored). An offer changes nothing and writes no audit row: the row says when
-- it was made, by which model and against which threshold; a proposal and a settling are audited. Answers the new ones.
CREATE FUNCTION memory_offer_candidates(m memories,p_candidates jsonb,p_model text,p_threshold real)
RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE c memories; one jsonb; v_id uuid; v_score real; kept jsonb:='[]'::jsonb; added integer;
BEGIN
 FOR one IN SELECT x.item FROM jsonb_array_elements(p_candidates) AS x(item) ORDER BY (x.item->>'score')::real DESC,x.item->>'id' LOOP
   v_id:=(one->>'id')::uuid;
   v_score:=(one->>'score')::real;
   SELECT * INTO c FROM memories x WHERE x.id=v_id FOR SHARE;
   CONTINUE WHEN c.id IS NULL OR c.id=m.id OR c.subject_id<>m.subject_id OR NOT memory_duplicate_live(c.id);
   INSERT INTO memory_contradictions(memory_id,conflict_id,score,model,threshold) VALUES(m.id,c.id,v_score,p_model,p_threshold)
   ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS added=ROW_COUNT;
   IF added>0 THEN
     kept:=kept||jsonb_build_array(jsonb_build_object('id',c.id,'score',v_score));
   ELSE
     UPDATE memory_contradictions k SET score=v_score,model=p_model,threshold=p_threshold
     WHERE k.memory_id=m.id AND k.conflict_id=c.id AND k.settled_at IS NULL;
   END IF;
 END LOOP;
 RETURN kept;
END $$;

-- 063's flag_memory_duplicates, for whoever made the revision it names rather than the memory's first writer alone, through
-- memory_flag_copies. A memory changed since, or no longer live, is flagged no more.
CREATE OR REPLACE FUNCTION flag_memory_duplicates(p_principal uuid,p_memory uuid,p_revision integer,p_copies jsonb,p_model text,
 p_threshold real,p_channel text,p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; m memories;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 PERFORM memory_review_input(p_copies,3,p_model,p_threshold);
 m:=memory_review_target(p_principal,p_memory,p_revision);
 IF m.revision<>p_revision OR NOT memory_duplicate_live(m.id) THEN
   RETURN jsonb_build_object('copies','[]'::jsonb);
 END IF;
 RETURN jsonb_build_object('copies',memory_flag_copies(m,p_copies,p_model,p_threshold,p_principal,channel,p_client));
END $$;

-- offer_memory_contradictions: the candidates the engine found close to a memory its caller has just written (p_candidates,
-- at most 5, each {id, score}), offered and queued for a person. The caller's as flag_memory_duplicates' is. Answers the new
-- ones.
CREATE FUNCTION offer_memory_contradictions(p_principal uuid,p_memory uuid,p_revision integer,p_candidates jsonb,p_model text,
 p_threshold real,p_channel text,p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; m memories;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 PERFORM memory_review_input(p_candidates,memory_contradiction_offers(),p_model,p_threshold);
 m:=memory_review_target(p_principal,p_memory,p_revision);
 IF m.revision<>p_revision OR NOT memory_duplicate_live(m.id) THEN
   RETURN jsonb_build_object('candidates','[]'::jsonb);
 END IF;
 RETURN jsonb_build_object('candidates',memory_offer_candidates(m,p_candidates,p_model,p_threshold));
END $$;

-- recheck_memory_review: what a memory's new words no longer reach (the engine rescored them; a memory the words are the
-- same as, or that it could not score, it names neither). p_copies: the flags between it and each of these come off, either
-- way round, with one audit row where each flagged memory is, unflagged with cause changed. p_candidates: the offers between
-- it and each of these come off, either way round, unless somebody has proposed them, with no audit row as they came with
-- none. The caller's as flag_memory_duplicates' is. Answers how many came off.
CREATE FUNCTION recheck_memory_review(p_principal uuid,p_memory uuid,p_revision integer,p_copies uuid[],p_candidates uuid[],p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; m memories; y memories; d record; gone uuid[]; flags integer:=0; offers integer:=0;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_review_target(p_principal,p_memory,p_revision);
 IF m.revision<>p_revision OR NOT memory_duplicate_live(m.id) THEN
   RETURN jsonb_build_object('flags',0,'offers',0);
 END IF;
 WITH off AS (DELETE FROM memory_duplicate_flags f WHERE f.memory_id=m.id AND f.copy_id=ANY(coalesce(p_copies,'{}')) RETURNING f.copy_id)
 SELECT coalesce(array_agg(o.copy_id ORDER BY o.copy_id),'{}') INTO gone FROM off o;
 IF cardinality(gone)>0 THEN
   flags:=cardinality(gone);
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'unflagged',p_principal,channel,p_client,jsonb_build_object('cause','changed','copies',to_jsonb(gone)));
 END IF;
 FOR d IN DELETE FROM memory_duplicate_flags f WHERE f.copy_id=m.id AND f.memory_id=ANY(coalesce(p_copies,'{}')) RETURNING f.memory_id
 LOOP
   SELECT * INTO y FROM memories x WHERE x.id=d.memory_id;
   flags:=flags+1;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(y.id,y.subject_id,y.revision,'unflagged',p_principal,channel,p_client,jsonb_build_object('cause','changed','copies',jsonb_build_array(m.id)));
 END LOOP;
 WITH off AS (DELETE FROM memory_contradictions k WHERE (k.memory_id=m.id AND k.conflict_id=ANY(coalesce(p_candidates,'{}'))
       OR k.conflict_id=m.id AND k.memory_id=ANY(coalesce(p_candidates,'{}')))
     AND k.proposed_at IS NULL AND k.settled_at IS NULL RETURNING k.id)
 SELECT count(*) INTO offers FROM off;
 RETURN jsonb_build_object('flags',flags,'offers',offers);
END $$;

-- review_imported_memory: what the operator's import has just added, checked as a save is: flagged and offered with no
-- login, through the terminal's channel, as import_memory's own audit row is. Only a memory whose audit trail says it came
-- in by an import at that revision; anything else, or one no longer live, answers nothing.
CREATE FUNCTION review_imported_memory(p_memory uuid,p_revision integer,p_copies jsonb,p_candidates jsonb,p_model text,p_threshold real,
 p_offer real,p_client text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE m memories;
BEGIN
 PERFORM lock_memory_scopes();
 PERFORM memory_review_input(p_copies,3,p_model,p_threshold);
 PERFORM memory_review_input(p_candidates,memory_contradiction_offers(),p_model,p_offer);
 IF p_client IS NULL OR length(p_client) NOT BETWEEN 1 AND 80 OR p_client ~ '[[:cntrl:]]' THEN
   RAISE EXCEPTION 'A client name is 1 to 80 characters' USING ERRCODE='22023';
 END IF;
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
 SELECT * INTO m FROM memories WHERE id=p_memory FOR UPDATE;
 IF NOT FOUND OR m.revision IS DISTINCT FROM p_revision OR NOT memory_duplicate_live(m.id)
   OR NOT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.event='imported' AND e.principal_id IS NULL AND e.revision=p_revision) THEN
   RETURN jsonb_build_object('copies','[]'::jsonb,'candidates','[]'::jsonb);
 END IF;
 RETURN jsonb_build_object('copies',memory_flag_copies(m,p_copies,p_model,p_threshold,NULL,'cli',p_client),
   'candidates',memory_offer_candidates(m,p_candidates,p_model,p_offer));
END $$;

-- propose_memory_contradiction: the calling AI says a memory it was offered a candidate for conflicts with that candidate,
-- with a short reason of at most 200 characters. Whoever reads the pair's scope may (it changes neither memory, as an
-- insight candidate's decision). A pair with no row, or one that does not stand (a memory gone, held, marked wrong, ended or
-- moved), is not_offered, whatever the id names, so the refusal tells nothing of another scope; a pair a person settled is
-- settled while the two share a scope. Proposing again answers unchanged and keeps the first reason. One audit row, contradiction_proposed, with the
-- other memory and no reason. An automatic link between them comes off when the caller may change either memory, as removing
-- one takes; otherwise it stays for the person's settling, and no new one is drawn while the pair is named. Answers the row.
CREATE FUNCTION propose_memory_contradiction(p_principal uuid,p_memory uuid,p_conflict uuid,p_reason text,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; o memories; k memory_contradictions; other uuid;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_memory IS NULL OR p_conflict IS NULL OR p_memory=p_conflict THEN
   RAISE EXCEPTION 'A contradiction is between a memory and another memory' USING ERRCODE='22023';
 END IF;
 IF p_reason IS NOT NULL AND (length(p_reason) NOT BETWEEN 1 AND 200 OR p_reason ~ '[[:cntrl:]]') THEN
   RAISE EXCEPTION 'A reason is 1 to 200 characters' USING ERRCODE='22023';
 END IF;
 IF p_reason IS NOT NULL AND memory_screen(p_reason,'{}'::text[])->>'reason' IS NOT NULL THEN
   RAISE EXCEPTION 'A reason says why two facts conflict, in plain words: this one reads like instructions, so it was not kept and nothing changed'
     USING ERRCODE='22023';
 END IF;
 PERFORM 1 FROM resources r WHERE r.id IN (p_memory,p_conflict) AND r.provider='memory' AND r.relationship_snapshot IS NULL ORDER BY r.id FOR SHARE;
 PERFORM 1 FROM memories x WHERE x.id IN (p_memory,p_conflict) ORDER BY x.id FOR SHARE;
 SELECT * INTO m FROM memories WHERE id=p_memory;
 IF NOT FOUND OR NOT reads_subject(p_principal,m.subject_id)
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 SELECT * INTO k FROM memory_contradictions x WHERE least(x.memory_id,x.conflict_id)=least(p_memory,p_conflict)
   AND greatest(x.memory_id,x.conflict_id)=greatest(p_memory,p_conflict) FOR UPDATE;
 IF k.id IS NOT NULL AND k.settled_at IS NOT NULL
   AND EXISTS(SELECT 1 FROM memories x JOIN memories y ON y.subject_id=x.subject_id WHERE x.id=k.memory_id AND y.id=k.conflict_id) THEN
   RAISE EXCEPTION 'A person has settled these two already: both stand or one replaced the other, so nothing changed'
     USING ERRCODE='22023',HINT='settled';
 END IF;
 IF k.id IS NULL OR NOT memory_contradiction_stands(k) THEN
   RAISE EXCEPTION 'That memory was not offered as a possible contradiction of this one, so nothing changed: name one a write answered (contradiction_candidates), or get or list_contradictions names'
     USING ERRCODE='22023',HINT='not_offered';
 END IF;
 other:=CASE WHEN k.memory_id=m.id THEN k.conflict_id ELSE k.memory_id END;
 IF k.proposed_at IS NOT NULL THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('conflict',other,'score',round(k.score::numeric,4),'reason',k.reason,'unchanged',true);
 END IF;
 UPDATE memory_contradictions x SET proposed_at=now(),proposed_by=p_principal,proposed_client=p_client,reason=p_reason WHERE x.id=k.id RETURNING * INTO k;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'contradiction_proposed',p_principal,channel,p_client,jsonb_build_object('with',other));
 SELECT * INTO o FROM memories WHERE id=other;
 IF memory_may_change(p_principal,m.subject_id,m.author_id,scoped) OR memory_may_change(p_principal,o.subject_id,o.author_id,scoped) THEN
   PERFORM memory_review_unlink(m.id,other);
 END IF;
 RETURN memory_reply(m,scoped)||jsonb_build_object('conflict',other,'score',round(k.score::numeric,4),'reason',k.reason,'unchanged',false);
END $$;

-- settle_memory_contradiction: a person decides. keep_both: both stand. replace: p_memory stands and replaces p_conflict, as
-- 063's supersede_memory plans and applies it at once, its rules refused as they are there. Only a person's channel, never
-- an AI client's (needs_person). The pair in either order; whoever may change either end for keep_both, and supersede's rule
-- (both ends) for replace. Each expected revision is the one the person read (40001 when it moved on). A row settled already
-- answers unchanged. A replace that supersede answers it would change nothing, because an end still to come rules it out
-- (ended or enclosed), is refused (protected) and settles nothing, so the person can keep both; one already replaced is
-- recorded as it is. Flags between them come off, and one audit row, contradiction_settled, on the row's memory.
CREATE FUNCTION settle_memory_contradiction(p_principal uuid,p_memory uuid,p_conflict uuid,p_decision text,p_expected integer,
 p_conflict_expected integer,p_channel text,p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; x memories; y memories; n memories; k memory_contradictions; plan jsonb; done jsonb; v_decision text; d record;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel NOT IN ('app','cli') THEN
   RAISE EXCEPTION 'A person settles a contradiction, from the app or the terminal, never an AI client: tell the person and leave it queued'
     USING ERRCODE='22023',HINT='needs_person';
 END IF;
 IF p_decision IS NULL OR p_decision NOT IN ('keep_both','replace') THEN
   RAISE EXCEPTION 'A decision is keep_both or replace' USING ERRCODE='22023';
 END IF;
 IF p_memory IS NULL OR p_conflict IS NULL OR p_memory=p_conflict THEN
   RAISE EXCEPTION 'A contradiction is between a memory and another memory' USING ERRCODE='22023';
 END IF;
 PERFORM 1 FROM resources r WHERE r.id IN (p_memory,p_conflict) AND r.provider='memory' AND r.relationship_snapshot IS NULL ORDER BY r.id FOR UPDATE;
 PERFORM 1 FROM memories m WHERE m.id IN (p_memory,p_conflict) ORDER BY m.id FOR UPDATE;
 SELECT * INTO x FROM memories WHERE id=p_memory;
 SELECT * INTO y FROM memories WHERE id=p_conflict;
 IF x.id IS NULL OR y.id IS NULL OR NOT reads_subject(p_principal,x.subject_id) OR NOT reads_subject(p_principal,y.subject_id)
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=x.id AND r.status='current')
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=y.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id IN (x.id,y.id)) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 SELECT * INTO k FROM memory_contradictions c WHERE least(c.memory_id,c.conflict_id)=least(x.id,y.id)
   AND greatest(c.memory_id,c.conflict_id)=greatest(x.id,y.id) FOR UPDATE;
 IF k.id IS NULL THEN
   RAISE EXCEPTION 'These two are not queued as a possible contradiction, so nothing changed' USING ERRCODE='22023',HINT='not_offered';
 END IF;
 IF NOT memory_may_change(p_principal,x.subject_id,x.author_id,scoped) AND NOT memory_may_change(p_principal,y.subject_id,y.author_id,scoped) THEN
   RAISE EXCEPTION 'Only the author of one of these memories or a Family admin may settle this' USING ERRCODE='42501';
 END IF;
 IF p_expected IS NOT NULL AND p_expected<>x.revision THEN
   RAISE EXCEPTION 'The memory changed since revision %: it is at revision % now; read it again and retry',p_expected,x.revision USING ERRCODE='40001';
 END IF;
 IF p_conflict_expected IS NOT NULL AND p_conflict_expected<>y.revision THEN
   RAISE EXCEPTION 'The other memory changed since revision %: it is at revision % now; read it again and retry',p_conflict_expected,y.revision USING ERRCODE='40001';
 END IF;
 IF k.settled_at IS NOT NULL THEN
   RETURN memory_reply(x,scoped)||jsonb_build_object('conflict',y.id,'decision',k.decision,'unchanged',true);
 END IF;
 IF NOT memory_contradiction_stands(k) THEN
   RAISE EXCEPTION 'One of these memories is held, marked wrong or ended, so there is nothing to settle now, and nothing changed' USING ERRCODE='22023',HINT='not_offered';
 END IF;
 v_decision:='keep_both';
 IF p_decision='replace' THEN
   plan:=supersede_memory(p_principal,x.id,y.id,NULL,channel,p_client);
   IF plan->>'action'='plan' THEN
     done:=supersede_memory(p_principal,x.id,y.id,plan->>'confirmation',channel,p_client);
   ELSIF plan->>'reason' IS DISTINCT FROM 'replaced' THEN
     RAISE EXCEPTION 'Memory % can''t replace memory %, since %: nothing was settled and nothing changed. Keep both, or correct when they were true and decide again',
       x.id,y.id,CASE plan->>'reason' WHEN 'enclosed' THEN 'it ends before the other does' ELSE 'the other ends before it starts' END
       USING ERRCODE='22023',HINT='protected';
   END IF;
   v_decision:='replaced';
 END IF;
 UPDATE memory_contradictions c SET settled_at=now(),settled_by=p_principal,decision=v_decision WHERE c.id=k.id;
 FOR d IN DELETE FROM memory_duplicate_flags f WHERE f.memory_id=x.id AND f.copy_id=y.id OR f.memory_id=y.id AND f.copy_id=x.id
   RETURNING f.memory_id,f.copy_id
 LOOP
   SELECT * INTO n FROM memories m WHERE m.id=d.memory_id;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(n.id,n.subject_id,n.revision,'flags_cleared',p_principal,channel,p_client,
     jsonb_build_object('cause','duplicate','copies',jsonb_build_array(d.copy_id)));
 END LOOP;
 SELECT * INTO n FROM memories m WHERE m.id=k.memory_id;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(n.id,n.subject_id,n.revision,'contradiction_settled',p_principal,channel,p_client,
   jsonb_build_object('with',CASE WHEN k.memory_id=x.id THEN y.id ELSE x.id END,'decision',v_decision));
 SELECT * INTO x FROM memories WHERE id=p_memory;
 RETURN memory_reply(x,scoped)||jsonb_build_object('conflict',y.id,'decision',v_decision,'unchanged',false);
END $$;

-- 063's clear_memory_duplicates, taking off only the flags the review showed (memory_duplicate_copies): a pair named a
-- contradiction stays for that review, and a flag whose copy is in the trash or held stays until the copy is back. A person's
-- keep both (the app's login or the terminal's) settles the same pairs here: they have looked at both. An AI client's takes
-- the flags off as before and the pairs stay queued, unsettled. Otherwise as 063 wrote it.
CREATE OR REPLACE FUNCTION clear_memory_duplicates(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; cleared uuid[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 WITH gone AS (DELETE FROM memory_duplicate_flags f WHERE f.memory_id=m.id
     AND f.copy_id IN (SELECT d.copy_id FROM memory_duplicate_copies(m.id) d) RETURNING f.copy_id)
 SELECT coalesce(array_agg(g.copy_id ORDER BY g.copy_id),'{}') INTO cleared FROM gone g;
 IF cardinality(cleared)=0 THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true);
 END IF;
 IF channel IN ('app','cli') THEN
   UPDATE memory_contradictions k SET settled_at=now(),settled_by=p_principal,decision='keep_both'
   WHERE k.settled_at IS NULL AND (k.memory_id=m.id AND k.conflict_id=ANY(cleared) OR k.conflict_id=m.id AND k.memory_id=ANY(cleared));
 END IF;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'flags_cleared',p_principal,channel,p_client,
   jsonb_build_object('cause','duplicate','copies',to_jsonb(cleared)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('changed',jsonb_build_array('duplicate_of'));
END $$;

-- A move between scopes (048): the memory has left the scope its rows were made in, so every unsettled row of the pair goes,
-- whichever end moved and whatever state it was in, so a candidate in another scope is never named. A row a person settled
-- stays, so a settled pair is not offered again once both are in one scope (memory_contradiction_stands reads no settled row,
-- and across scopes propose_memory_contradiction answers not_offered). A memory deleted for good (038) takes every row with
-- it, a reason that may repeat its words included. One that somebody had named a contradiction is audited
-- where its row's memory is, by count, with the change's login, channel, client and job (the retention job's purge names one).
-- An AI client moves neither memory of a row a person still has to see, which would take it off their review and let the
-- earlier be ended: refused (needs_person), from the move's audit row, so nothing of the move is kept. A person may move it.
CREATE FUNCTION memory_event_contradictions_moved() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE d record; y memories;
BEGIN
 IF NEW.channel='mcp' AND NEW.event IN ('shared','unshared') AND EXISTS(SELECT 1 FROM memory_contradictions k
     JOIN memories o ON o.id=CASE WHEN k.memory_id=NEW.memory_id THEN k.conflict_id ELSE k.memory_id END
     WHERE NEW.memory_id IN (k.memory_id,k.conflict_id) AND k.settled_at IS NULL
       AND (k.proposed_at IS NOT NULL OR memory_duplicate_live(o.id))) THEN
   RAISE EXCEPTION 'This memory is queued for a person''s review as a possible contradiction, and moving it would take it off that review, so a person moves it, from the app or the terminal: tell the person and leave it where it is'
     USING ERRCODE='22023',HINT='needs_person';
 END IF;
 FOR d IN DELETE FROM memory_contradictions k WHERE (k.memory_id=NEW.memory_id OR k.conflict_id=NEW.memory_id)
     AND (NEW.event='purged' OR k.settled_at IS NULL) RETURNING k.memory_id,k.proposed_at
 LOOP
   SELECT * INTO y FROM memories m WHERE m.id=d.memory_id;
   CONTINUE WHEN y.id IS NULL OR d.proposed_at IS NULL;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,job,detail)
   VALUES(y.id,y.subject_id,y.revision,'unflagged',NEW.principal_id,NEW.channel,NEW.client,NEW.job,
     jsonb_build_object('cause',CASE WHEN NEW.event='purged' THEN 'gone' ELSE 'moved' END,'candidates',1));
 END LOOP;
 RETURN NULL;
END $$;
CREATE TRIGGER memory_events_contradictions_moved AFTER INSERT ON memory_events FOR EACH ROW
 WHEN (NEW.memory_id IS NOT NULL AND NEW.event IN ('shared','unshared','purged'))
 EXECUTE FUNCTION memory_event_contradictions_moved();

-- Whether p_by replaced p_memory, by the replaced memory's own audit row: a settled row says that one did, not which way.
CREATE FUNCTION memory_replaced_by(p_memory uuid,p_by uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=p_memory AND e.event='superseded' AND e.detail->>'by'=p_by::text)
$$;

-- Whether an AI client's change has just ended the earlier memory of an unsettled row (or replaced either, by the other), or
-- changed the earlier's words once somebody has named the pair. The
-- other end is live, so the row stands, or somebody named the pair and the other is still in this scope, in the trash, held or
-- ended, and comes back for the person: a person settles it. Only a change that ends it counts: the state before is what
-- the change retired (a trip into the trash moves no revision, so its state is the state now), and with no version to read it
-- is taken as live. Or whether it goes against a person's settling: of a pair kept both, it ended one while the other is live, or
-- replaced one by the other; of a pair settled by a replacement, it brought the replaced back, or replaced the one that stands
-- by it. Raised from the change's audit row, so nothing of the change is kept.
CREATE FUNCTION memory_event_contradiction_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE this memories; was_live boolean; ended boolean; words boolean;
BEGIN
 SELECT * INTO this FROM memories m WHERE m.id=NEW.memory_id;
 IF NEW.event='trashed' THEN
   was_live:=this.status<>'deprecated' AND (this.valid_until IS NULL OR this.valid_until>now());
 ELSE
   SELECT v.status<>'deprecated' AND (v.valid_until IS NULL OR v.valid_until>now()) INTO was_live
   FROM memory_versions v WHERE v.memory_id=NEW.memory_id AND v.revision=NEW.revision-1;
 END IF;
 was_live:=coalesce(was_live,true);
 ended:=this.status='deprecated' OR this.valid_until IS NOT NULL AND this.valid_until<=now()
   OR EXISTS(SELECT 1 FROM memory_trash x WHERE x.memory_id=this.id);
 words:=NEW.event='appended' OR NEW.event IN ('updated','reverted') AND coalesce(NEW.detail->'fields' ? 'content',false);
 IF EXISTS(SELECT 1 FROM memory_contradictions k
     JOIN memories o ON o.id=CASE WHEN k.memory_id=NEW.memory_id THEN k.conflict_id ELSE k.memory_id END AND o.subject_id=this.subject_id
     WHERE k.settled_at IS NULL AND NEW.memory_id IN (k.memory_id,k.conflict_id)
       AND (k.proposed_at IS NOT NULL OR memory_duplicate_live(o.id))
       AND (NEW.event='superseded' AND o.id::text=NEW.detail->>'by'
         OR (this.created_at,this.id)<(o.created_at,o.id)
           AND (NEW.event='superseded' OR was_live AND ended OR words AND k.proposed_at IS NOT NULL))) THEN
   RAISE EXCEPTION 'This memory is queued for a person''s review as a possible contradiction, so a person decides what happens to it, from the app or the terminal: tell the person and leave it as it is'
     USING ERRCODE='22023',HINT='needs_person';
 END IF;
 IF EXISTS(SELECT 1 FROM memory_contradictions k
     JOIN memories o ON o.id=CASE WHEN k.memory_id=NEW.memory_id THEN k.conflict_id ELSE k.memory_id END
     WHERE k.settled_at IS NOT NULL AND NEW.memory_id IN (k.memory_id,k.conflict_id)
       AND CASE k.decision
         WHEN 'keep_both' THEN NEW.event='superseded' AND o.id::text=NEW.detail->>'by'
           OR was_live AND (NEW.event='superseded' OR ended) AND memory_duplicate_live(o.id)
         ELSE NEW.event='superseded' AND o.id::text=NEW.detail->>'by' AND memory_replaced_by(o.id,this.id)
           OR NOT was_live AND NOT ended AND memory_replaced_by(this.id,o.id) END) THEN
   RAISE EXCEPTION 'A person has settled this memory against another one: they kept both, or one replaced the other, so a person changes that, from the app or the terminal: tell the person and leave it as it is'
     USING ERRCODE='22023',HINT='needs_person';
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER memory_events_contradiction_guard AFTER INSERT ON memory_events FOR EACH ROW
 WHEN (NEW.memory_id IS NOT NULL AND NEW.channel='mcp' AND NEW.event IN ('status_changed','trashed','superseded','validity_changed','reverted','updated','appended'))
 EXECUTE FUNCTION memory_event_contradiction_guard();

-- Whether a row stands between two memories the caller reads, or a person settled them against what an AI client's supersede
-- (a replaces b) asks: kept both, or b is the one that stands. The supersede is refused before a plan is answered, so no plan
-- is given that the guard would not let apply. A retry of the replacement the person settled on answers as any retry does.
CREATE FUNCTION memory_contradiction_between(p_principal uuid,a uuid,b uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_contradictions k JOIN memories m ON m.id=k.memory_id
   WHERE least(k.memory_id,k.conflict_id)=least(a,b) AND greatest(k.memory_id,k.conflict_id)=greatest(a,b)
     AND reads_subject(p_principal,m.subject_id)
     AND (memory_contradiction_stands(k) OR k.decision='keep_both' OR k.decision='replaced' AND NOT memory_replaced_by(b,a)))
$$;

-- 062's infer_memory_links with one exclusion (above): a pair flagged as a copy for certain or named a contradiction is never
-- chosen and takes no link, checked again once every end is locked. Otherwise as 062 wrote it.
CREATE OR REPLACE FUNCTION infer_memory_links(p_principal uuid,p_memory uuid,p_text_revision integer,p_vector text,p_model text,
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
     AND NOT memory_review_pair(m.id,x.id)
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
     AND NOT memory_review_pair(m.id,x.id)
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
     OR EXISTS(SELECT 1 FROM memory_link_dismissals d WHERE d.low_id=least(m.id,n.id) AND d.high_id=greatest(m.id,n.id))
     OR memory_review_pair(m.id,n.id);
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

-- 053's memory_own_changes, with the two rows of a contradiction left out too (contradiction_proposed, contradiction_settled):
-- neither moves a revision, so somebody else's never stops a member undoing their own change, as a flag's and a confirmation's
-- do not. Otherwise as 053 wrote it.
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
   AND e.event NOT IN ('flagged','unflagged','flags_cleared','confirmed','contradiction_proposed','contradiction_settled');
 RETURN covered=p_current-p_after AND NOT coalesce(others,true);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list and the replaced functions get their search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_contradiction_offers() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_copy_flagged(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_copy_kept(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_contradiction_named(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_copy_certain() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_review_pair(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_contradiction_stands(memory_contradictions) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_contradiction_listed(memory_contradictions) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_open_contradictions(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_duplicate_copies(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_review_unlink(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_review_input(jsonb,integer,text,real) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_review_target(uuid,uuid,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_flag_copies(memories,jsonb,text,real,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_offer_candidates(memories,jsonb,text,real) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION flag_memory_duplicates(uuid,uuid,integer,jsonb,text,real,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION offer_memory_contradictions(uuid,uuid,integer,jsonb,text,real,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION recheck_memory_review(uuid,uuid,integer,uuid[],uuid[],text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION review_imported_memory(uuid,integer,jsonb,jsonb,text,real,real,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION propose_memory_contradiction(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION settle_memory_contradiction(uuid,uuid,uuid,text,integer,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION clear_memory_duplicates(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_contradictions_moved() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_replaced_by(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_contradiction_guard() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_contradiction_between(uuid,uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION infer_memory_links(uuid,uuid,integer,text,text,double precision,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_own_changes(uuid,uuid,text,integer,integer,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_contradiction_offers(),memory_copy_flagged(uuid,uuid),memory_copy_kept(uuid,uuid),
 memory_contradiction_named(uuid,uuid),memory_copy_certain(),memory_review_pair(uuid,uuid),memory_contradiction_stands(memory_contradictions),
 memory_contradiction_listed(memory_contradictions),memory_open_contradictions(uuid),memory_duplicate_copies(uuid),
 memory_review_unlink(uuid,uuid),memory_review_input(jsonb,integer,text,real),memory_review_target(uuid,uuid,integer),
 memory_flag_copies(memories,jsonb,text,real,uuid,text,text),memory_offer_candidates(memories,jsonb,text,real),
 flag_memory_duplicates(uuid,uuid,integer,jsonb,text,real,text,text),offer_memory_contradictions(uuid,uuid,integer,jsonb,text,real,text,text),
 recheck_memory_review(uuid,uuid,integer,uuid[],uuid[],text,text),review_imported_memory(uuid,integer,jsonb,jsonb,text,real,real,text),
 propose_memory_contradiction(uuid,uuid,uuid,text,text,text),settle_memory_contradiction(uuid,uuid,uuid,text,integer,integer,text,text),
 clear_memory_duplicates(uuid,uuid,integer,text,text),memory_event_contradictions_moved(),memory_replaced_by(uuid,uuid),memory_event_contradiction_guard(),
 memory_contradiction_between(uuid,uuid,uuid),infer_memory_links(uuid,uuid,integer,text,text,double precision,text,text),
 memory_own_changes(uuid,uuid,text,integer,integer,boolean) FROM PUBLIC;
