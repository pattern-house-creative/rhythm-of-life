-- The opt-in recall log and implicit feedback (SB-P2-11 in docs/dev/second-brain-parity/backlog.md; D-022, D-034;
-- ledger rows RET-20, RET-21, DAT-13): with engine.recallLog on, each recall a reader asks through the recall tool or the
-- app's explorer search (Core/RecallLog.cs) is kept with its question, its arguments, the records it answered with in
-- order and its receipt, at most 200 a day for each scope, for 30 days; a later get, append or update of one of those
-- records within 30 minutes marks it followed; and a count per scope and record of how often it was recalled and followed.
-- Off, the default and every existing install's state (absent from every rhythm.yaml written before it), nothing calls
-- these functions, so nothing is written and nothing is read; on, no answer changes: no ranking reads the log or the
-- counts (they feed the evaluation's golden set first, as upstream's do), and a receipt is what it was.
--
-- Adapted from Second Brain's sampled recall log (src/recall/log.ts at the pinned commit, MIT): the question as asked,
-- the cap inside the write, a purge of a few expired rows on each write, the follow window of 30 minutes, the latest
-- recall only. Unlike upstream, the recall counts are kept apart from the memory rows (D-034: a read never changes a
-- memory or its revision), per scope like the log, and only while the log is on; and every row is kept in the scope of
-- its reader: the reader's own person, or the household for a login with no person, so one person's questions and what
-- they found are never counted in, or capped by, another's.
--
-- Both tables change only through the functions here (checked_tables()): the engine's login reads them. No tool or route
-- serves them yet (an admin view is SB-P5-14's). The number is provisional.

CREATE TABLE recall_log(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 -- The scope the row is kept in: the reader's own person, or the household for a login with no person.
 subject_id text NOT NULL REFERENCES subjects,
 channel text NOT NULL CHECK(channel IN ('mcp','app')),
 -- The question as the reader asked it (this is what makes the log opt-in), and its arguments: topK, hops and filters.
 query text NOT NULL CHECK(length(query) BETWEEN 1 AND 800),
 params jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(params)='object' AND length(params::text)<=2000),
 -- The records it answered with, in order (ids only, each one the reader read then), those of them that were its direct
 -- results (the rest are neighbours a hop reached), and those followed since.
 returned uuid[] NOT NULL CHECK(cardinality(returned)<=100 AND array_position(returned,NULL) IS NULL),
 direct uuid[] NOT NULL DEFAULT '{}' CHECK(direct<@returned),
 followed uuid[] NOT NULL DEFAULT '{}' CHECK(followed<@returned),
 receipt text NOT NULL CHECK(receipt ~ '^rc1-[0-9a-f]{16}$'),
 at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX recall_log_scope ON recall_log(subject_id,at DESC);
CREATE INDEX recall_log_at ON recall_log(at);

-- How often a record was recalled (a direct result, not a neighbour a hop reached) and followed, per scope, apart from the
-- record itself. Gone with the record; a count nobody has touched in 30 days goes with the log rows that made it.
CREATE TABLE recall_counts(
 subject_id text NOT NULL REFERENCES subjects,
 resource_id uuid NOT NULL REFERENCES resources ON DELETE CASCADE,
 recalled integer NOT NULL DEFAULT 0 CHECK(recalled>=0),
 followed integer NOT NULL DEFAULT 0 CHECK(followed>=0),
 last_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(subject_id,resource_id)
);
CREATE INDEX recall_counts_last ON recall_counts(last_at);
REVOKE ALL ON recall_log,recall_counts FROM PUBLIC;

-- The list of checked tables as the migrations before this one left it, with the recall log's tables added: the engine's
-- login reads them and writes them only through the functions below. Built from the list in force rather than written
-- out, so a migration of the same run that adds a table of its own keeps it whichever is numbered first.
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['recall_log','recall_counts']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The bounds (D-022, as upstream): 200 logged recalls a day for each scope, kept 30 days; a follow-up counts within 30
-- minutes of the recall that returned it.
CREATE FUNCTION recall_log_bounds() RETURNS TABLE(per_day integer,kept interval,follow interval) LANGUAGE sql IMMUTABLE AS $$
 SELECT 200,interval '30 days',interval '30 minutes'
$$;

-- The scope a reader's log rows are kept in, and the channel its recalls come through: an AI client (harness) over MCP,
-- the app's login through the app; null for any other login, which logs nothing.
CREATE FUNCTION recall_log_scope(p_principal uuid) RETURNS TABLE(subject_id text,channel text) LANGUAGE sql STABLE AS $$
 SELECT coalesce(p.person_id,'household'),CASE p.role WHEN 'harness' THEN 'mcp' ELSE 'app' END
 FROM principals p WHERE p.id=p_principal AND p.enabled AND p.role IN ('harness','app')
$$;

-- log_recall: keeps one recall in its reader's scope (recall_log_scope) when that scope has logged fewer than 200 in the
-- last day, with each direct result's count moved; and deletes up to 20 of that scope's rows past the 30 days. The
-- returned ids are the records the reader was served; only those still in the catalog are counted. Answers whether it was
-- kept. A login that logs nothing, or a scope over its day's cap, writes nothing and answers false. The engine calls it
-- only with engine.recallLog on, and never lets it change the answer (RecallLog.Write).
CREATE FUNCTION log_recall(p_principal uuid,p_query text,p_params jsonb,p_returned uuid[],p_direct uuid[],p_receipt text)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE who record; b record;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN RETURN false; END IF;
 SELECT * INTO who FROM recall_log_scope(p_principal);
 IF p_query IS NULL OR length(p_query) NOT BETWEEN 1 AND 800 OR p_receipt IS NULL OR p_receipt !~ '^rc1-[0-9a-f]{16}$'
   OR p_returned IS NULL OR cardinality(p_returned)>100 OR p_direct IS NULL OR NOT p_direct<@p_returned THEN
   RAISE EXCEPTION 'A logged recall is a question of 1 to 800 characters, at most 100 results and its receipt' USING ERRCODE='22023';
 END IF;
 SELECT * INTO b FROM recall_log_bounds();
 -- One scope's writers in turn, so the cap is exact when several recall at once.
 PERFORM pg_advisory_xact_lock(hashtextextended('recall-log:'||who.subject_id,0));
 DELETE FROM recall_log WHERE id IN (SELECT x.id FROM recall_log x WHERE x.subject_id=who.subject_id AND x.at<now()-b.kept
   ORDER BY x.at LIMIT 20);
 IF (SELECT count(*) FROM recall_log x WHERE x.subject_id=who.subject_id AND x.at>now()-interval '1 day')>=b.per_day THEN
   RETURN false;
 END IF;
 INSERT INTO recall_log(subject_id,channel,query,params,returned,direct,receipt)
 VALUES(who.subject_id,who.channel,p_query,coalesce(p_params,'{}'),p_returned,p_direct,p_receipt);
 INSERT INTO recall_counts(subject_id,resource_id,recalled)
 SELECT who.subject_id,d.id,1 FROM (SELECT DISTINCT unnest(p_direct) AS id) d WHERE EXISTS(SELECT 1 FROM resources r WHERE r.id=d.id)
 ON CONFLICT(subject_id,resource_id) DO UPDATE SET recalled=recall_counts.recalled+1,last_at=now();
 RETURN true;
END $$;

-- recall_followed: implicit feedback. Of p_ids, those the reader's scope's latest logged recall returned within the last
-- 30 minutes and has not marked yet are marked followed on it, and the counts of those that were its direct results
-- moved: a neighbour a hop reached is marked on the row but never counted, as it was never counted recalled, so a count
-- is never followed more often than recalled. Answers how many were marked. With nothing logged in that window (the log
-- off, or no recall lately) it finds no row and writes nothing.
CREATE FUNCTION recall_followed(p_principal uuid,p_ids uuid[]) RETURNS integer LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE who record; latest recall_log; fresh uuid[];
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN RETURN 0; END IF;
 SELECT * INTO who FROM recall_log_scope(p_principal);
 IF p_ids IS NULL OR cardinality(p_ids)=0 THEN RETURN 0; END IF;
 SELECT x.* INTO latest FROM recall_log x WHERE x.subject_id=who.subject_id
   AND x.at>now()-(SELECT b.follow FROM recall_log_bounds() b) ORDER BY x.at DESC,x.id LIMIT 1 FOR UPDATE;
 IF NOT FOUND THEN RETURN 0; END IF;
 fresh:=ARRAY(SELECT DISTINCT i FROM unnest(p_ids) i WHERE i=ANY(latest.returned) AND NOT i=ANY(latest.followed) AND i IS NOT NULL);
 IF cardinality(fresh)=0 THEN RETURN 0; END IF;
 UPDATE recall_log SET followed=followed||fresh WHERE id=latest.id;
 INSERT INTO recall_counts(subject_id,resource_id,followed)
 SELECT who.subject_id,f.id,1 FROM unnest(fresh) f(id) WHERE f.id=ANY(latest.direct) AND EXISTS(SELECT 1 FROM resources r WHERE r.id=f.id)
 ON CONFLICT(subject_id,resource_id) DO UPDATE SET followed=recall_counts.followed+1,last_at=now();
 RETURN cardinality(fresh);
END $$;

-- prune_recall_log: the engine's daily job (the scheduler, RecallLog.Prune), whatever engine.recallLog says now, so a log
-- turned off empties itself: at most p_limit (1 to 10000) rows older than 30 days, oldest first, and as many counts no
-- recall or follow-up has touched in 30 days. On an install that never turned the log on both tables are empty and it
-- deletes nothing. Answers how many of each went.
CREATE FUNCTION prune_recall_log(p_limit integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE kept interval:=(SELECT b.kept FROM recall_log_bounds() b); rows integer; counts integer;
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 10000 THEN
   RAISE EXCEPTION 'A prune takes 1 to 10000 rows' USING ERRCODE='22023';
 END IF;
 DELETE FROM recall_log WHERE id IN (SELECT x.id FROM recall_log x WHERE x.at<now()-kept ORDER BY x.at LIMIT p_limit);
 GET DIAGNOSTICS rows=ROW_COUNT;
 DELETE FROM recall_counts c WHERE (c.subject_id,c.resource_id) IN (
   SELECT x.subject_id,x.resource_id FROM recall_counts x WHERE x.last_at<now()-kept ORDER BY x.last_at LIMIT p_limit);
 GET DIAGNOSTICS counts=ROW_COUNT;
 RETURN jsonb_build_object('rows',rows,'counts',counts);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION recall_log_bounds() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION recall_log_scope(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION log_recall(uuid,text,jsonb,uuid[],uuid[],text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION recall_followed(uuid,uuid[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION prune_recall_log(integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION recall_log_bounds(),recall_log_scope(uuid),log_recall(uuid,text,jsonb,uuid[],uuid[],text),
 recall_followed(uuid,uuid[]),prune_recall_log(integer) FROM PUBLIC;
