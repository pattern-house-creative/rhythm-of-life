-- Insight runs and richer feedback. Feedback gains `handled` and an optional note
-- kept with it, and a harness login logs each run so the app can show when and how the insights last ran. As in
-- 011, the tables are reached only through these functions; text is untrusted data wherever it is shown.
ALTER TABLE insights DROP CONSTRAINT insights_status_check;
ALTER TABLE insights ADD CONSTRAINT insights_status_check
  CHECK(status IN ('new','saved','explored','snoozed','dismissed','superseded','handled'));
UPDATE insights SET feedback=feedback||'{"note":null}'::jsonb WHERE feedback IS NOT NULL AND feedback->'note' IS NULL;

CREATE TABLE insight_runs(
 id uuid PRIMARY KEY,
 generated_by text NOT NULL CHECK(generated_by IN ('chatgpt-daily','chatgpt-weekly','manual')),
 outcome text NOT NULL CHECK(outcome IN ('saved','quiet','blocked')),
 note text CHECK(length(note) BETWEEN 1 AND 500),
 insight_id uuid REFERENCES insights ON DELETE SET NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX insight_runs_newest ON insight_runs(created_at DESC,id);
REVOKE ALL ON insight_runs FROM PUBLIC;

-- The app's login records one feedback value with an optional note (1-500 characters once trimmed),
-- replacing the last; the value also sets the status. "later" snoozes until the given time, or for a day.
CREATE FUNCTION insight_feedback(p_principal uuid,p_id uuid,p_value text,p_until timestamptz,p_note text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE updated insights;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name='app') THEN
   RAISE EXCEPTION 'Insight feedback is not authorized' USING ERRCODE='42501';
 END IF;
 IF p_value='later' THEN p_until:=coalesce(p_until,now()+interval '1 day'); END IF;
 p_note:=btrim(p_note,E' \t\r\n');
 IF p_value IS NULL OR p_value NOT IN ('interesting','explore','not_useful','later','handled') OR (p_value<>'later' AND p_until IS NOT NULL)
   OR p_until<=now() OR p_until>now()+interval '90 days' OR length(p_note) NOT BETWEEN 1 AND 500 THEN
   RAISE EXCEPTION 'Invalid insight feedback' USING ERRCODE='22023';
 END IF;
 UPDATE insights SET feedback=jsonb_build_object('value',p_value,'at',now(),'until',p_until,'note',p_note),
   status=CASE p_value WHEN 'interesting' THEN 'saved' WHEN 'explore' THEN 'explored' WHEN 'not_useful' THEN 'dismissed'
     WHEN 'handled' THEN 'handled' ELSE 'snoozed' END,
   updated_at=now()
 WHERE id=p_id RETURNING * INTO updated;
 RETURN CASE WHEN updated.id IS NULL THEN NULL ELSE insight_json(updated) END;
END $$;

-- 011's entry point, without a note, so the release before this one keeps working after a rollback. Replacing
-- it keeps its owner and EXECUTE grant.
CREATE OR REPLACE FUNCTION insight_feedback(p_principal uuid,p_id uuid,p_value text,p_until timestamptz)
RETURNS jsonb LANGUAGE sql SECURITY DEFINER AS $$ SELECT insight_feedback(p_principal,p_id,p_value,p_until,NULL::text) $$;

-- A harness login logs how one run ended, with an optional note and the insight it saved, if any.
CREATE FUNCTION log_insight_run(p_principal uuid,p_generated_by text,p_outcome text,p_note text,p_insight uuid)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE logged uuid:=gen_random_uuid();
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name='harness') THEN
   RAISE EXCEPTION 'Logging insight runs is not authorized' USING ERRCODE='42501';
 END IF;
 IF p_insight IS NOT NULL AND NOT EXISTS(SELECT 1 FROM insights WHERE id=p_insight) THEN
   RAISE EXCEPTION 'Unknown insight' USING ERRCODE='22023';
 END IF;
 INSERT INTO insight_runs(id,generated_by,outcome,note,insight_id) VALUES(logged,p_generated_by,p_outcome,btrim(p_note,E' \t\r\n'),p_insight);
 RETURN logged;
END $$;

-- The newest runs first, for either reader.
CREATE FUNCTION read_insight_runs(p_principal uuid,p_limit int)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled
   AND name IN ('harness','app')) THEN
   RAISE EXCEPTION 'Reading insight runs is not authorized' USING ERRCODE='42501';
 END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 14 THEN RAISE EXCEPTION 'Invalid run limit' USING ERRCODE='22023'; END IF;
 RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',r.id,'generatedBy',r.generated_by,'outcome',r.outcome,'note',r.note,
     'insightId',r.insight_id,'at',r.created_at) ORDER BY r.created_at DESC,r.id),'[]'::jsonb)
   FROM (SELECT * FROM insight_runs ORDER BY created_at DESC,id LIMIT p_limit) r);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION insight_feedback(uuid,uuid,text,timestamptz,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_feedback(uuid,uuid,text,timestamptz) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION log_insight_run(uuid,text,text,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insight_runs(uuid,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION insight_feedback(uuid,uuid,text,timestamptz,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION log_insight_run(uuid,text,text,text,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_insight_runs(uuid,int) FROM PUBLIC;
