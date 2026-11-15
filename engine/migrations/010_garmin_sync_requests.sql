-- Sync now for the app's Garmin reader: one bounded write for the engine's writer login,
-- which receives EXECUTE on this function only. At most one request per 15 minutes, none while a
-- Garmin job for the subject is unfinished, and each request is a two-day job the worker picks up.
CREATE FUNCTION request_garmin_sync(p_principal uuid,p_subject text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE today date:=(now() AT TIME ZONE 'UTC')::date; pending timestamptz; requested timestamptz; accepted boolean:=false;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals p JOIN grants g ON g.principal_id=p.id
   WHERE p.id=p_principal AND p.enabled AND p.name='app'
     AND g.subject_id=p_subject AND g.provider='garmin-dashboard-summary' AND g.scope='private')
 OR (SELECT count(*) FROM grants WHERE principal_id=p_principal)<>1 THEN
   RAISE EXCEPTION 'Garmin sync request is not authorized' USING ERRCODE='42501';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('garmin-sync-request:'||p_subject,0));
 SELECT min(coalesce((metadata->>'requestedAt')::timestamptz,updated_at)) INTO pending FROM sync_jobs
   WHERE subject_id=p_subject AND provider='garmin' AND status<>'complete';
 SELECT max((metadata->>'requestedAt')::timestamptz) INTO requested FROM sync_jobs
   WHERE subject_id=p_subject AND provider='garmin' AND metadata->>'reason'='on-demand';
 IF pending IS NULL AND (requested IS NULL OR requested<=now()-interval '15 minutes') THEN
   INSERT INTO sync_jobs(id,subject_id,provider,from_date,until_date,next_date,metadata)
   VALUES(gen_random_uuid(),p_subject,'garmin',today-1,today,today-1,
     jsonb_build_object('reason','on-demand','requestedBy',p_principal,'requestedAt',now()));
   accepted:=true;pending:=now();requested:=now();
 END IF;
 RETURN jsonb_build_object('accepted',accepted,'pendingSince',pending,
   'nextAllowedAt',greatest(coalesce(requested+interval '15 minutes',now()),now()),
   'lastSyncedAt',(SELECT max(updated_at) FROM sync_jobs WHERE subject_id=p_subject AND provider='garmin' AND status='complete' AND last_error IS NULL));
END $$;
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION request_garmin_sync(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION request_garmin_sync(uuid,text) FROM PUBLIC;
