-- On-demand map rebuilds ("Refresh rebuilds the map"). The app's login asks for
-- one when someone presses refresh; the engine's scheduler claims the request and runs the same rebuild as
-- the scheduled tick. At most one request every three minutes, and none while one is waiting or running. The household
-- writer login receives EXECUTE on the entry point and no table privileges.
CREATE TABLE relationship_refresh_requests(
 id uuid PRIMARY KEY,
 requested_by uuid NOT NULL REFERENCES principals,
 requested_at timestamptz NOT NULL DEFAULT now(),
 started_at timestamptz,
 finished_at timestamptz,
 outcome text CHECK(outcome IN ('complete','busy','not_due','disabled','failed'))
);
CREATE INDEX relationship_refresh_requests_newest ON relationship_refresh_requests(requested_at DESC);
REVOKE ALL ON relationship_refresh_requests FROM PUBLIC;

-- Records a request when none is waiting or running and the last was at least three minutes ago. Either way it says
-- what is happening: whether this one was accepted, since when one has been waiting, whether it is running, when the
-- next may be asked for, and when a rebuild last finished.
CREATE FUNCTION request_relationship_refresh(p_principal uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE waiting relationship_refresh_requests; latest relationship_refresh_requests; accepted boolean:=false;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name='app') THEN
   RAISE EXCEPTION 'Requesting a rebuild is not authorized' USING ERRCODE='42501';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtext('relationship_refresh_requests'));
 SELECT * INTO waiting FROM relationship_refresh_requests WHERE finished_at IS NULL ORDER BY requested_at LIMIT 1;
 SELECT * INTO latest FROM relationship_refresh_requests ORDER BY requested_at DESC LIMIT 1;
 IF waiting.id IS NULL AND (latest.id IS NULL OR latest.requested_at<=now()-interval '3 minutes') THEN
   INSERT INTO relationship_refresh_requests(id,requested_by) VALUES(gen_random_uuid(),p_principal) RETURNING * INTO waiting;
   accepted:=true;
 END IF;
 RETURN jsonb_build_object('accepted',accepted,'pendingSince',waiting.requested_at,'running',waiting.started_at IS NOT NULL,
   'nextAllowedAt',CASE WHEN waiting.id IS NOT NULL THEN NULL ELSE latest.requested_at+interval '3 minutes' END,
   'lastFinishedAt',(SELECT max(finished_at) FROM relationship_refresh_requests WHERE outcome='complete'));
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION request_relationship_refresh(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION request_relationship_refresh(uuid) FROM PUBLIC;
