-- Daily insights (product overview, "insights"). A harness login saves them, the app's login records feedback,
-- and both read them, only through these functions; the engine's writer login
-- receives EXECUTE on the three entry points and no table privileges. Text is stored as given and is
-- untrusted data wherever it is shown.
CREATE TABLE insights(
 id uuid PRIMARY KEY,
 kind text NOT NULL CHECK(kind IN ('focus','discovery','suggestion')),
 title text NOT NULL CHECK(length(title) BETWEEN 1 AND 120),
 summary text NOT NULL CHECK(length(summary) BETWEEN 1 AND 600),
 why_it_matters text NOT NULL CHECK(length(why_it_matters)<=600),
 next_step text NOT NULL CHECK(length(next_step)<=400),
 points jsonb NOT NULL CHECK(jsonb_typeof(points)='array' AND jsonb_array_length(points)<=12),
 evidence_level text NOT NULL CHECK(evidence_level IN ('strong','moderate','weak')),
 limitations text NOT NULL CHECK(length(limitations)<=400),
 stars jsonb NOT NULL CHECK(jsonb_typeof(stars)='array' AND jsonb_array_length(stars)<=20),
 sources jsonb NOT NULL CHECK(jsonb_typeof(sources)='array' AND jsonb_array_length(sources)<=20),
 generated_by text NOT NULL CHECK(generated_by IN ('chatgpt-daily','chatgpt-weekly','manual')),
 generated_at timestamptz NOT NULL,
 sources_as_of timestamptz NOT NULL,
 supersedes uuid REFERENCES insights ON DELETE SET NULL,
 status text NOT NULL DEFAULT 'new' CHECK(status IN ('new','saved','explored','snoozed','dismissed','superseded')),
 feedback jsonb,
 saved_by uuid NOT NULL REFERENCES principals,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX insights_newest ON insights(created_at DESC,id);
REVOKE ALL ON insights FROM PUBLIC;

-- The record exactly as every reader receives it.
CREATE FUNCTION insight_json(i insights) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',i.id,'kind',i.kind,'title',i.title,'summary',i.summary,'whyItMatters',i.why_it_matters,
   'nextStep',i.next_step,'points',i.points,'evidenceLevel',i.evidence_level,'limitations',i.limitations,'stars',i.stars,
   'sources',i.sources,'generatedBy',i.generated_by,'generatedAt',i.generated_at,'sourcesAsOf',i.sources_as_of,
   'supersedes',i.supersedes,'status',i.status,'feedback',i.feedback,'createdAt',i.created_at,'updatedAt',i.updated_at)
$$;

-- A harness login saves an insight the engine has already validated; a superseded one is marked as such.
CREATE FUNCTION save_insight(p_principal uuid,p_insight jsonb)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid:=gen_random_uuid(); replaced uuid:=(p_insight->>'supersedes')::uuid;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name='harness') THEN
   RAISE EXCEPTION 'Saving insights is not authorized' USING ERRCODE='42501';
 END IF;
 IF replaced IS NOT NULL THEN
   UPDATE insights SET status='superseded',updated_at=now() WHERE id=replaced;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown superseded insight' USING ERRCODE='22023'; END IF;
 END IF;
 INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,
   generated_by,generated_at,sources_as_of,supersedes,saved_by)
 VALUES(saved,p_insight->>'kind',p_insight->>'title',p_insight->>'summary',p_insight->>'whyItMatters',p_insight->>'nextStep',
   p_insight->'points',p_insight->>'evidenceLevel',p_insight->>'limitations',p_insight->'stars',p_insight->'sources',
   p_insight->>'generatedBy',(p_insight->>'generatedAt')::timestamptz,(p_insight->>'sourcesAsOf')::timestamptz,replaced,p_principal);
 RETURN saved;
END $$;

-- The app's login records one feedback value, which also sets the status; "later" snoozes
-- until the given time, or for a day.
CREATE FUNCTION insight_feedback(p_principal uuid,p_id uuid,p_value text,p_until timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE updated insights;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name='app') THEN
   RAISE EXCEPTION 'Insight feedback is not authorized' USING ERRCODE='42501';
 END IF;
 IF p_value='later' THEN p_until:=coalesce(p_until,now()+interval '1 day'); END IF;
 IF p_value IS NULL OR p_value NOT IN ('interesting','explore','not_useful','later') OR (p_value<>'later' AND p_until IS NOT NULL)
   OR p_until<=now() OR p_until>now()+interval '90 days' THEN
   RAISE EXCEPTION 'Invalid insight feedback' USING ERRCODE='22023';
 END IF;
 UPDATE insights SET feedback=jsonb_build_object('value',p_value,'at',now(),'until',p_until),
   status=CASE p_value WHEN 'interesting' THEN 'saved' WHEN 'explore' THEN 'explored' WHEN 'not_useful' THEN 'dismissed' ELSE 'snoozed' END,
   updated_at=now()
 WHERE id=p_id RETURNING * INTO updated;
 RETURN CASE WHEN updated.id IS NULL THEN NULL ELSE insight_json(updated) END;
END $$;

-- Newest first, optionally one status or those saved since a time.
CREATE FUNCTION read_insights(p_principal uuid,p_status text,p_since timestamptz,p_limit int)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled
   AND name IN ('harness','app')) THEN
   RAISE EXCEPTION 'Reading insights is not authorized' USING ERRCODE='42501';
 END IF;
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 THEN RAISE EXCEPTION 'Invalid insight limit' USING ERRCODE='22023'; END IF;
 RETURN (SELECT coalesce(jsonb_agg(insight_json(i) ORDER BY i.created_at DESC,i.id),'[]'::jsonb) FROM insights i
   WHERE i.id IN (SELECT id FROM insights WHERE (p_status IS NULL OR status=p_status) AND (p_since IS NULL OR created_at>=p_since)
     ORDER BY created_at DESC,id LIMIT p_limit));
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION insight_json(insights) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_insight(uuid,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_feedback(uuid,uuid,text,timestamptz) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insights(uuid,text,timestamptz,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION insight_json(insights) FROM PUBLIC;
REVOKE ALL ON FUNCTION save_insight(uuid,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION insight_feedback(uuid,uuid,text,timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_insights(uuid,text,timestamptz,int) FROM PUBLIC;
