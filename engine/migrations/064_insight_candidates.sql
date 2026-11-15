-- Insight candidates for native memories (SB-P5-05 in docs/dev/second-brain-parity/backlog.md; story 11, its candidate
-- half; ledger rows INT-01 to INT-03, JOB-10, DAT-03 and API-67; docs/dev/memory.md sections 5 and 9): an older and a
-- newer memory of one scope, written 30 or more days apart, that a nightly pass found worth a reasoning client's look,
-- kept until the client uses or rejects the pair. Adapted from Second Brain's nightly candidate accrual and its score
-- (src/insight/candidates.ts, src/insight/score.ts and src/insight/eligibility.ts at the pinned commit, MIT; NOTICE).
--
-- The pass (Core/InsightCandidates.cs, a nightly job made only with memory.tools on) walks each scope's eligible memories
-- oldest first from that scope's cursor (050's job_cursors), a bounded number a run, and compares each with the eligible
-- memories of its own scope written 30 or more days before it, by its similarity (the live embeddings model, the words
-- alone, read against the insight threshold the install reads, D-211) and its weight (both memories of importance 3 or
-- more, the caller's own word): a pair either says something of is a candidate, the best few of each memory by
-- upstream's score. It writes through memory_insight_accrue only, which checks each pair again: both memories exist, are
-- eligible and are of the scope named, the newer written 30 or more days after the older, and a pair said weighty is
-- weighty. A pair already kept, whatever became of it, is never written again, so a repeated run adds nothing and a
-- rejected pair never comes back; only a waiting one whose two memories moved together to another scope (a share or an
-- un-share of both), which no list serves since, is found again in the scope it is now of. It writes no memory, no
-- version and no audit row: a candidate is no change of either memory, as a stale mark is not (053).
--
-- Eligible (memory_insight_eligible): live (its own catalog row current, not in the trash), not held for review, not
-- deprecated, its window open (a replaced memory is not insight material, as upstream), and not the engine's own (source
-- system: a digest, 054), as upstream leaves out what the brain wrote about itself. Upstream's floor of 80 characters is
-- not taken: it would leave out two of the eight labelled insight pairs the pass can see (benchmarks.md).
--
-- A candidate is listed (memory_insight_listed, the one definition the tool's count and list read) while it waits
-- (pending) and both its memories are still eligible and still of its scope: one held, trashed, marked wrong, ended or
-- moved to another scope since (048's share) takes it out of every list at once, and it is never served across scopes.
-- A reader is served the candidates whose two memories it reads, which is reading their scope.
--
-- resolve_insight_candidate: any harness or app login that reads the pair's scope uses or rejects a listed candidate
-- (use: a reasoning client wrote an insight from it; reject: it says nothing worth saying). The decision is the
-- candidate's, not either memory's: no memory changes, and memory_insight_events keeps who decided what, when, through
-- which channel and client. A repeat answers unchanged and writes nothing; the other decision on a decided candidate is
-- refused. Nothing undoes a decision.
--
-- Nothing calls these but the pass and the memory tools (list_insight_candidates and resolve_insight_candidate), made
-- only with memory.tools on (off by default), and tests: with none called both tables stay empty and every answer is as
-- before. Neither table is exported (they are derived, as the map's links are) or read by anything else.
--
-- Errors, as 033's: 42501 not available to this login; P0002 an unknown candidate, also one the caller does not read or
-- one no longer listed; 22023 invalid input (also the other decision on a decided candidate). The number is provisional.

CREATE TABLE memory_insight_candidates(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 -- The scope both memories were in when the pass found them; the pair is listed only while both still are.
 subject_id text NOT NULL REFERENCES subjects,
 older_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 newer_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 -- The two memories' similarity as the model that measured it scored it (rounded to 4 places), and that model's id.
 similarity double precision NOT NULL CHECK(similarity BETWEEN -1 AND 1),
 model text NOT NULL CHECK(model ~ '^[a-z0-9][a-z0-9._-]{0,127}$'),
 -- Whole days between the two memories' writing.
 gap_days integer NOT NULL CHECK(gap_days>=30),
 -- What said it is worth a look: similar (at or above the insight threshold the install read), weighty (both of
 -- importance 3 or more); at least one.
 signals text[] NOT NULL CHECK(cardinality(signals) BETWEEN 1 AND 2 AND signals<@ARRAY['similar','weighty']
   AND array_position(signals,NULL) IS NULL),
 -- Upstream's score: the higher, the sooner a reasoning client is shown it.
 score double precision NOT NULL CHECK(score>=0 AND score<1000),
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','used','rejected')),
 found_at timestamptz NOT NULL DEFAULT now(),
 decided_at timestamptz,
 decided_by uuid REFERENCES principals,
 CHECK(older_id<>newer_id),
 CHECK((status='pending')=(decided_at IS NULL) AND (decided_at IS NULL)=(decided_by IS NULL))
);
-- One row a pair, whichever way round it was found.
CREATE UNIQUE INDEX memory_insight_candidates_pair ON memory_insight_candidates(least(older_id,newer_id),greatest(older_id,newer_id));
CREATE INDEX memory_insight_candidates_queue ON memory_insight_candidates(subject_id,status,score DESC,id);
CREATE INDEX memory_insight_candidates_newer ON memory_insight_candidates(newer_id);
-- And the older end, so a memory deleted for good or purged finds its candidates by either end (the cascade's own read).
CREATE INDEX memory_insight_candidates_older ON memory_insight_candidates(older_id);

-- Each decision on a candidate: used or rejected, by whom, through which channel and client, when.
CREATE TABLE memory_insight_events(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 candidate_id uuid NOT NULL REFERENCES memory_insight_candidates ON DELETE CASCADE,
 subject_id text NOT NULL REFERENCES subjects,
 event text NOT NULL CHECK(event IN ('used','rejected')),
 principal_id uuid NOT NULL REFERENCES principals,
 channel text NOT NULL CHECK(channel IN ('app','mcp','cli')),
 client text CHECK(client IS NULL OR length(client) BETWEEN 1 AND 80),
 at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX memory_insight_events_candidate ON memory_insight_events(candidate_id,at);
REVOKE ALL ON memory_insight_candidates,memory_insight_events FROM PUBLIC;

-- The list of checked tables in force, with these two added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_insight_candidates','memory_insight_events']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- Whether a memory may be half of an insight candidate now.
CREATE FUNCTION memory_insight_eligible(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((SELECT m.status<>'deprecated' AND m.hold IS NULL AND m.source<>'system'
     AND (m.valid_until IS NULL OR m.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
     AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
       AND r.status='current')
   FROM memories m WHERE m.id=p_memory),false)
$$;

-- Whether a candidate's two memories are both still eligible and both still of its scope, whatever became of it.
CREATE FUNCTION memory_insight_live(p_candidate uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((SELECT o.subject_id=c.subject_id AND n.subject_id=c.subject_id
     AND memory_insight_eligible(o.id) AND memory_insight_eligible(n.id)
   FROM memory_insight_candidates c JOIN memories o ON o.id=c.older_id JOIN memories n ON n.id=c.newer_id
   WHERE c.id=p_candidate),false)
$$;

-- Whether a candidate is listed: it waits, and its pair is live. The one definition every count and list reads.
CREATE FUNCTION memory_insight_listed(p_candidate uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((SELECT c.status='pending' FROM memory_insight_candidates c WHERE c.id=p_candidate),false)
   AND memory_insight_live(p_candidate)
$$;

-- memory_insight_accrue: one piece of the nightly pass over p_subject: the pairs it found, each an object of older and
-- newer (memory ids), similarity, score and signals, as p_model measured them. Each is checked again here and written
-- unless kept already (any status), or moved to p_subject when it waits for a scope its memories have both left (added).
-- Answers how many were added, were kept already and were refused (a pair that is not
-- the scope's, not eligible, not 30 days apart or not what it says), counts only.
CREATE FUNCTION memory_insight_accrue(p_subject text,p_model text,p_pairs jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE x jsonb; o memories; n memories; signals text[]; added integer:=0; known integer:=0; refused integer:=0; done integer;
BEGIN
 IF p_subject IS NULL OR NOT EXISTS(SELECT 1 FROM subjects s WHERE s.id=p_subject) THEN
   RAISE EXCEPTION 'An insight pass names a scope that exists' USING ERRCODE='22023';
 END IF;
 IF p_model IS NULL OR p_model !~ '^[a-z0-9][a-z0-9._-]{0,127}$' THEN
   RAISE EXCEPTION 'An insight pass names the model that measured it' USING ERRCODE='22023';
 END IF;
 IF jsonb_typeof(p_pairs) IS DISTINCT FROM 'array' OR jsonb_array_length(p_pairs)>1000 THEN
   RAISE EXCEPTION 'An insight pass writes at most 1000 pairs at a time' USING ERRCODE='22023';
 END IF;
 FOR x IN SELECT e FROM jsonb_array_elements(p_pairs) e LOOP
   IF jsonb_typeof(x)<>'object' OR jsonb_typeof(x->'older')<>'string' OR jsonb_typeof(x->'newer')<>'string'
     OR jsonb_typeof(x->'similarity')<>'number' OR jsonb_typeof(x->'score')<>'number' OR jsonb_typeof(x->'signals')<>'array'
     OR (x->>'older') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     OR (x->>'newer') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(x->'signals') s WHERE jsonb_typeof(s)<>'string') THEN
     RAISE EXCEPTION 'A pair is older, newer, similarity, score and signals' USING ERRCODE='22023';
   END IF;
   signals:=ARRAY(SELECT DISTINCT s FROM jsonb_array_elements_text(x->'signals') s ORDER BY s);
   SELECT * INTO o FROM memories WHERE id=(x->>'older')::uuid;
   SELECT * INTO n FROM memories WHERE id=(x->>'newer')::uuid;
   IF o.id IS NULL OR n.id IS NULL OR o.id=n.id OR o.subject_id<>p_subject OR n.subject_id<>p_subject
     OR n.created_at-o.created_at<interval '30 days'
     OR NOT memory_insight_eligible(o.id) OR NOT memory_insight_eligible(n.id)
     OR cardinality(signals)=0 OR NOT signals<@ARRAY['similar','weighty']
     OR 'weighty'=ANY(signals) AND NOT (coalesce(o.importance,0)>=3 AND coalesce(n.importance,0)>=3)
     OR (x->>'similarity')::double precision NOT BETWEEN -1 AND 1
     OR (x->>'score')::double precision<0 OR (x->>'score')::double precision>=1000 THEN
     refused:=refused+1;
     CONTINUE;
   END IF;
   INSERT INTO memory_insight_candidates(subject_id,older_id,newer_id,similarity,model,gap_days,signals,score)
   VALUES(p_subject,o.id,n.id,round((x->>'similarity')::numeric,4),p_model,
     floor(extract(epoch FROM n.created_at-o.created_at)/86400)::integer,signals,(x->>'score')::double precision)
   -- A pair held already is never written again, whatever became of it, except a waiting one no longer listed because
   -- its two memories moved together to another scope (a share or an un-share of both): it is found here again, in the
   -- scope it is now of, so it is served there rather than to nobody.
   ON CONFLICT ((least(older_id,newer_id)),(greatest(older_id,newer_id))) DO UPDATE
     SET subject_id=EXCLUDED.subject_id,similarity=EXCLUDED.similarity,model=EXCLUDED.model,gap_days=EXCLUDED.gap_days,
       signals=EXCLUDED.signals,score=EXCLUDED.score,found_at=now()
     WHERE memory_insight_candidates.status='pending' AND memory_insight_candidates.subject_id<>EXCLUDED.subject_id
       AND NOT memory_insight_live(memory_insight_candidates.id);
   GET DIAGNOSTICS done=ROW_COUNT;
   added:=added+done;
   known:=known+1-done;
 END LOOP;
 RETURN jsonb_build_object('added',added,'known',known,'refused',refused);
END $$;

-- resolve_insight_candidate: p_action use or reject, by a login that reads the candidate's scope, on a listed candidate
-- (or a decided one whose pair is still live, which a repeat of its own decision answers unchanged). Answers the
-- candidate's id, status and when it was decided, and whether this call changed nothing.
CREATE FUNCTION resolve_insight_candidate(p_principal uuid,p_candidate uuid,p_action text,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; c memory_insight_candidates; wanted text;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Insight candidates are not available to this login' USING ERRCODE='42501';
 END IF;
 -- The scopes switch held still while the read rule is applied, as every caller-checked writer since 033 does.
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 wanted:=CASE p_action WHEN 'use' THEN 'used' WHEN 'reject' THEN 'rejected' END;
 IF wanted IS NULL THEN
   RAISE EXCEPTION 'An insight candidate is used (use) or rejected (reject)' USING ERRCODE='22023';
 END IF;
 SELECT * INTO c FROM memory_insight_candidates WHERE id=p_candidate FOR UPDATE;
 IF NOT FOUND OR NOT reads_subject(p_principal,c.subject_id) OR NOT memory_insight_live(c.id) THEN
   RAISE EXCEPTION 'Unknown insight candidate' USING ERRCODE='P0002';
 END IF;
 IF c.status=wanted THEN
   RETURN jsonb_build_object('id',c.id,'status',c.status,'decided_at',c.decided_at,'unchanged',true);
 END IF;
 IF c.status<>'pending' THEN
   RAISE EXCEPTION 'This candidate was % already: a decision on a candidate stays as it was made', c.status USING ERRCODE='22023';
 END IF;
 UPDATE memory_insight_candidates SET status=wanted,decided_at=now(),decided_by=p_principal WHERE id=c.id;
 INSERT INTO memory_insight_events(candidate_id,subject_id,event,principal_id,channel,client)
 VALUES(c.id,c.subject_id,wanted,p_principal,channel,p_client);
 RETURN jsonb_build_object('id',c.id,'status',wanted,'decided_at',now(),'unchanged',false);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_insight_eligible(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_insight_live(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_insight_listed(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_insight_accrue(text,text,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_insight_candidate(uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_insight_eligible(uuid),memory_insight_live(uuid),memory_insight_listed(uuid),
 memory_insight_accrue(text,text,jsonb),resolve_insight_candidate(uuid,uuid,text,text,text) FROM PUBLIC;
