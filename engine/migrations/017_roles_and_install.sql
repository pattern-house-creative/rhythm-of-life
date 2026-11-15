-- Roles instead of named logins, the install's own settings, generic insight labels, generic documents and the
-- bookkeeping the ingest contract needs (contracts/README.md sections 1.2, 2 and 4). Every function an earlier
-- migration gated on a principal's name is replaced here by the same function gated on the principal's role, so
-- this migration leaves the same schema whichever wording 001-016 had. Existing principals keep role 'legacy',
-- which reads through its grants as before and reaches none of the role-gated entry points until a role is given.

ALTER TABLE principals ADD COLUMN role text NOT NULL DEFAULT 'legacy'
  CHECK(role IN ('legacy','system','harness','app','ingest'));
ALTER TABLE principals ADD COLUMN plugin text CHECK(plugin IS NULL OR plugin ~ '^[a-z][a-z0-9-]{1,30}$');
ALTER TABLE principals ADD COLUMN created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE principals ADD CONSTRAINT principals_ingest_plugin CHECK((role='ingest')=(plugin IS NOT NULL));
-- At most one enabled app principal, and enabled role principals have distinct names.
CREATE UNIQUE INDEX principals_one_app ON principals(role) WHERE role='app' AND enabled;
CREATE UNIQUE INDEX principals_named ON principals(name) WHERE enabled AND role<>'legacy';

-- Settings the engine copies from rhythm.yaml at start, for the functions that need them (the time zone).
CREATE TABLE install_settings(
 key text PRIMARY KEY CHECK(key ~ '^[a-z][a-z0-9_.-]{0,63}$'),
 value text NOT NULL CHECK(length(value)<=1000),
 updated_at timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON install_settings FROM PUBLIC;
CREATE FUNCTION install_timezone() RETURNS text LANGUAGE sql STABLE AS $$
 SELECT coalesce((SELECT value FROM install_settings WHERE key='timezone'),'UTC')
$$;
CREATE FUNCTION install_today() RETURNS date LANGUAGE sql STABLE AS $$ SELECT (now() AT TIME ZONE install_timezone())::date $$;

CREATE FUNCTION principal_has_role(p_principal uuid,p_roles text[]) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND role=ANY(p_roles))
$$;
-- 015's guard now names roles: harness (AI clients) and app (the app's facade).
CREATE OR REPLACE FUNCTION dashboard_reader(p_principal uuid,p_names text[],p_what text) RETURNS void LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF NOT principal_has_role(p_principal,p_names) THEN
   RAISE EXCEPTION '% is not authorized',p_what USING ERRCODE='42501';
 END IF;
END $$;

-- Insight labels: any short label a harness chooses (harness-daily by default), no longer one vendor's names.
ALTER TABLE insights DROP CONSTRAINT insights_generated_by_check;
ALTER TABLE insights ADD CONSTRAINT insights_generated_by_check CHECK(generated_by ~ '^[a-z][a-z0-9-]{0,39}$');
ALTER TABLE insight_runs DROP CONSTRAINT insight_runs_generated_by_check;
ALTER TABLE insight_runs ADD CONSTRAINT insight_runs_generated_by_check CHECK(generated_by ~ '^[a-z][a-z0-9-]{0,39}$');

CREATE OR REPLACE FUNCTION save_insight(p_principal uuid,p_insight jsonb)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid:=gen_random_uuid(); replaced uuid:=(p_insight->>'supersedes')::uuid;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving insights');
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

CREATE OR REPLACE FUNCTION insight_feedback(p_principal uuid,p_id uuid,p_value text,p_until timestamptz,p_note text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE updated insights;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Insight feedback');
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

CREATE OR REPLACE FUNCTION read_insights(p_principal uuid,p_status text,p_since timestamptz,p_limit int)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insights');
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 THEN RAISE EXCEPTION 'Invalid insight limit' USING ERRCODE='22023'; END IF;
 RETURN (SELECT coalesce(jsonb_agg(insight_json(i) ORDER BY i.created_at DESC,i.id),'[]'::jsonb) FROM insights i
   WHERE i.id IN (SELECT id FROM insights WHERE (p_status IS NULL OR status=p_status) AND (p_since IS NULL OR created_at>=p_since)
     ORDER BY created_at DESC,id LIMIT p_limit));
END $$;

CREATE OR REPLACE FUNCTION log_insight_run(p_principal uuid,p_generated_by text,p_outcome text,p_note text,p_insight uuid)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE logged uuid:=gen_random_uuid();
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Logging insight runs');
 IF p_insight IS NOT NULL AND NOT EXISTS(SELECT 1 FROM insights WHERE id=p_insight) THEN
   RAISE EXCEPTION 'Unknown insight' USING ERRCODE='22023';
 END IF;
 INSERT INTO insight_runs(id,generated_by,outcome,note,insight_id) VALUES(logged,p_generated_by,p_outcome,btrim(p_note,E' \t\r\n'),p_insight);
 RETURN logged;
END $$;

CREATE OR REPLACE FUNCTION read_insight_runs(p_principal uuid,p_limit int)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insight runs');
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 14 THEN RAISE EXCEPTION 'Invalid run limit' USING ERRCODE='22023'; END IF;
 RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',r.id,'generatedBy',r.generated_by,'outcome',r.outcome,'note',r.note,
     'insightId',r.insight_id,'at',r.created_at) ORDER BY r.created_at DESC,r.id),'[]'::jsonb)
   FROM (SELECT * FROM insight_runs ORDER BY created_at DESC,id LIMIT p_limit) r);
END $$;

CREATE OR REPLACE FUNCTION read_dashboards(p_principal uuid,p_dashboard uuid,p_version int,p_code boolean)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading dashboards');
 IF p_dashboard IS NULL THEN
   IF p_version IS NOT NULL OR p_code THEN RAISE EXCEPTION 'A version needs a dashboard' USING ERRCODE='22023'; END IF;
   RETURN (SELECT coalesce(jsonb_agg(dashboard_json(d,v,false) ORDER BY d.updated_at DESC,d.id),'[]'::jsonb)
     FROM (SELECT * FROM dashboards ORDER BY updated_at DESC,id LIMIT 100) d
     JOIN dashboard_versions v ON v.dashboard_id=d.id AND v.version=(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id));
 END IF;
 RETURN (SELECT dashboard_json(d,v,coalesce(p_code,false)) FROM dashboards d JOIN dashboard_versions v ON v.dashboard_id=d.id
   WHERE d.id=p_dashboard AND v.version=coalesce(p_version,(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id)));
END $$;

CREATE OR REPLACE FUNCTION save_dashboard(p_principal uuid,p_dashboard uuid,p_restore int,p_version jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE target uuid:=coalesce(p_dashboard,gen_random_uuid()); numbered int:=1; earlier dashboard_versions;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving dashboards');
 IF p_dashboard IS NULL THEN
   IF p_restore IS NOT NULL THEN RAISE EXCEPTION 'Restoring needs a dashboard' USING ERRCODE='22023'; END IF;
   INSERT INTO dashboards(id,created_by) VALUES(target,p_principal);
 ELSE
   PERFORM 1 FROM dashboards WHERE id=target FOR UPDATE;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
   SELECT max(version)+1 INTO numbered FROM dashboard_versions WHERE dashboard_id=target;
   UPDATE dashboards SET updated_at=now() WHERE id=target;
 END IF;
 IF p_restore IS NOT NULL THEN
   SELECT * INTO earlier FROM dashboard_versions WHERE dashboard_id=target AND version=p_restore;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002'; END IF;
   INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,writes,note,restored_from,saved_by)
   VALUES(target,numbered,earlier.title,earlier.description,earlier.html,earlier.feeds,earlier.stars,earlier.writes,p_version->>'note',p_restore,p_principal);
 ELSE
   INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,writes,note,saved_by)
   VALUES(target,numbered,p_version->>'title',coalesce(p_version->>'description',''),p_version->>'html',p_version->'feeds',
     p_version->'stars',coalesce(p_version->'writes','[]'::jsonb),p_version->>'note',p_principal);
 END IF;
 RETURN jsonb_build_object('id',target,'version',numbered);
END $$;

CREATE OR REPLACE FUNCTION allow_dashboard_edits(p_principal uuid,p_dashboard uuid,p_version int,p_allowed boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Allowing edits');
 SELECT writes INTO declared FROM dashboard_versions WHERE dashboard_id=p_dashboard AND version=p_version;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002'; END IF;
 IF p_allowed AND jsonb_array_length(declared)=0 THEN RAISE EXCEPTION 'This version writes nothing' USING ERRCODE='22023'; END IF;
 UPDATE dashboards SET edits_allowed=CASE WHEN p_allowed THEN declared END WHERE id=p_dashboard;
 RETURN jsonb_build_object('id',p_dashboard,'version',p_version,'editsAllowed',p_allowed);
END $$;

CREATE OR REPLACE FUNCTION begin_dashboard_write(p_principal uuid,p_dashboard uuid,p_version int,p_name text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 RETURN dashboard_write_allowed(p_dashboard,p_version,p_name);
END $$;

-- A write an adapter made at its source (a Sheet's cells, a Doc's section), with what it replaced.
CREATE OR REPLACE FUNCTION record_dashboard_write(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_target jsonb,
  p_summary text,p_before jsonb,p_after jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb:=dashboard_write_allowed(p_dashboard,p_version,p_name); written uuid:=gen_random_uuid();
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 IF declared->>'kind' NOT IN ('sheet','doc') THEN RAISE EXCEPTION 'Not a source write' USING ERRCODE='22023'; END IF;
 INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,before,after,written_by)
 VALUES(written,p_dashboard,p_version,p_name,declared->>'kind',p_target,p_summary,p_before,p_after,p_principal);
 RETURN dashboard_write_json(written);
END $$;

-- As in 015, with the install's time zone instead of a fixed one.
CREATE OR REPLACE FUNCTION write_dashboard_log(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_entry jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb:=dashboard_write_allowed(p_dashboard,p_version,p_name); written uuid:=gen_random_uuid();
  record uuid:=gen_random_uuid(); who text; subject_kind text; logged text; day date; entry_title text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 IF declared->>'kind'<>'log' THEN RAISE EXCEPTION 'Not a log write' USING ERRCODE='22023'; END IF;
 who:=declared->>'subject'; logged:=declared->>'log';
 SELECT kind INTO subject_kind FROM subjects WHERE id=who AND kind IN ('person','household');
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown subject' USING ERRCODE='22023'; END IF;
 day:=(p_entry->>'date')::date;
 IF day NOT BETWEEN install_today()-400 AND install_today()+1 THEN
   RAISE EXCEPTION 'A log date is within the last 400 days' USING ERRCODE='22023';
 END IF;
 entry_title:=coalesce(p_entry->>'metric',p_entry->>'check','note');
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,checksum,source_updated_at,status,local_date,timezone,metadata)
 VALUES(record,who,'log',logged||':'||record,'log-entry',CASE WHEN subject_kind='household' THEN 'shared' ELSE 'private' END,
   initcap(replace(logged,'-',' '))||' log · '||entry_title,'',md5(p_entry::text),now(),'current',day,install_timezone(),
   jsonb_build_object('log',logged,'dashboard',p_dashboard,'version',p_version,'write',written,
     'type',CASE WHEN p_entry ? 'value' THEN 'number' WHEN p_entry ? 'check' THEN 'check' ELSE 'note' END)
     || CASE WHEN p_entry ? 'note' THEN jsonb_build_object('note',p_entry->>'note') ELSE '{}'::jsonb END);
 IF p_entry ? 'value' THEN
   INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
   VALUES(record,p_entry->>'metric','value',now(),day,(p_entry->>'value')::double precision,p_entry->>'unit','reported');
 ELSIF p_entry ? 'check' THEN
   INSERT INTO measurements(resource_id,metric,sample_key,observed_at,local_date,value,unit,quality)
   VALUES(record,p_entry->>'check','value',now(),day,1,'done','reported');
 END IF;
 INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,before,after,entry,written_by)
 VALUES(written,p_dashboard,p_version,p_name,'log',jsonb_build_object('log',logged,'subject',who),
   left(initcap(replace(logged,'-',' '))||' log: '||CASE
     WHEN p_entry ? 'value' THEN (p_entry->>'metric')||' '||(p_entry->>'value')||' '||(p_entry->>'unit')
     WHEN p_entry ? 'check' THEN (p_entry->>'check')||' done'
     ELSE 'note' END||' for '||to_char(day,'Mon FMDD'),300),
   NULL,p_entry,record,p_principal);
 RETURN dashboard_write_json(written);
END $$;

CREATE OR REPLACE FUNCTION undo_dashboard_write(p_principal uuid,p_write uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE w dashboard_writes;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Undoing a dashboard write');
 SELECT * INTO w FROM dashboard_writes WHERE id=p_write FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown write' USING ERRCODE='P0002'; END IF;
 IF w.undone_at IS NOT NULL THEN RAISE EXCEPTION 'Already undone' USING ERRCODE='22023'; END IF;
 IF w.kind='log' THEN UPDATE resources SET status='deleted',indexed_at=now() WHERE id=w.entry AND provider='log'; END IF;
 UPDATE dashboard_writes SET undone_at=now(),undone_by=p_principal WHERE id=p_write;
 RETURN dashboard_write_json(p_write);
END $$;

CREATE OR REPLACE FUNCTION read_dashboard_writes(p_principal uuid,p_dashboard uuid,p_write uuid,p_limit int)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading dashboard writes');
 IF p_write IS NOT NULL THEN RETURN dashboard_write_json(p_write); END IF;
 RETURN (SELECT coalesce(jsonb_agg(dashboard_write_json(w.id) ORDER BY w.written_at DESC,w.id),'[]'::jsonb) FROM (
   SELECT id,written_at FROM dashboard_writes WHERE p_dashboard IS NULL OR dashboard_id=p_dashboard
   ORDER BY written_at DESC,id LIMIT least(greatest(coalesce(p_limit,50),1),200)) w);
END $$;

CREATE OR REPLACE FUNCTION set_ambient_card(p_principal uuid,p_corner text,p_dashboard uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Choosing corner cards');
 IF p_corner NOT IN ('glucose','recovery','training','changed') THEN RAISE EXCEPTION 'Unknown corner' USING ERRCODE='22023'; END IF;
 IF p_dashboard IS NULL THEN DELETE FROM ambient_cards WHERE corner=p_corner;
 ELSE
   IF NOT EXISTS(SELECT 1 FROM dashboards WHERE id=p_dashboard) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
   INSERT INTO ambient_cards(corner,dashboard_id,set_by) VALUES(p_corner,p_dashboard,p_principal)
   ON CONFLICT(corner) DO UPDATE SET dashboard_id=excluded.dashboard_id,set_by=excluded.set_by,set_at=now();
 END IF;
 RETURN read_ambient_cards(p_principal);
END $$;
CREATE OR REPLACE FUNCTION read_ambient_cards(p_principal uuid) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading corner cards');
 RETURN (SELECT coalesce(jsonb_object_agg(corner,dashboard_id),'{}'::jsonb) FROM ambient_cards);
END $$;

CREATE OR REPLACE FUNCTION delete_dashboard(p_principal uuid,p_dashboard uuid,p_title text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE named text; versions int; writes int; cards int;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Deleting dashboards');
 PERFORM 1 FROM dashboards WHERE id=p_dashboard FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
 SELECT title INTO named FROM dashboard_versions WHERE dashboard_id=p_dashboard ORDER BY version DESC LIMIT 1;
 IF named IS DISTINCT FROM p_title THEN RAISE EXCEPTION 'The title does not match the dashboard' USING ERRCODE='22023'; END IF;
 SELECT count(*) INTO versions FROM dashboard_versions WHERE dashboard_id=p_dashboard;
 SELECT count(*) INTO writes FROM dashboard_writes WHERE dashboard_id=p_dashboard;
 SELECT count(*) INTO cards FROM ambient_cards WHERE dashboard_id=p_dashboard;
 DELETE FROM dashboards WHERE id=p_dashboard;
 RETURN jsonb_build_object('id',p_dashboard,'title',named,'versions',versions,'writes',writes,'corners',cards);
END $$;

CREATE OR REPLACE FUNCTION set_sky_pin(p_principal uuid,p_star text,p_pinned boolean) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Pinning stars');
 IF p_star IS NULL OR length(p_star) NOT BETWEEN 1 AND 1000 THEN RAISE EXCEPTION 'A pin names a star' USING ERRCODE='22023'; END IF;
 IF NOT p_pinned THEN DELETE FROM sky_pins WHERE graph_id=p_star;
 ELSIF NOT EXISTS(SELECT 1 FROM sky_pins WHERE graph_id=p_star) THEN
   IF (SELECT count(*) FROM sky_pins)>=24 THEN RAISE EXCEPTION 'At most 24 stars are pinned' USING ERRCODE='22023'; END IF;
   INSERT INTO sky_pins(graph_id,pinned_by) VALUES(p_star,p_principal);
 END IF;
 RETURN read_sky_pins(p_principal);
END $$;
CREATE OR REPLACE FUNCTION read_sky_pins(p_principal uuid) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading pins');
 RETURN (SELECT coalesce(jsonb_agg(graph_id ORDER BY pinned_at,graph_id),'[]'::jsonb) FROM sky_pins);
END $$;

CREATE OR REPLACE FUNCTION request_relationship_refresh(p_principal uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE waiting relationship_refresh_requests; latest relationship_refresh_requests; accepted boolean:=false;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Requesting a rebuild');
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

-- The scheduler claims the oldest waiting request (at most one runs) and records how it ended.
CREATE FUNCTION claim_relationship_refresh() RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE claimed uuid;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtext('relationship_refresh_requests'));
 IF EXISTS(SELECT 1 FROM relationship_refresh_requests WHERE started_at IS NOT NULL AND finished_at IS NULL
   AND started_at>now()-interval '15 minutes') THEN RETURN NULL; END IF;
 UPDATE relationship_refresh_requests SET started_at=now() WHERE id=(SELECT id FROM relationship_refresh_requests
   WHERE finished_at IS NULL ORDER BY requested_at LIMIT 1) RETURNING id INTO claimed;
 RETURN claimed;
END $$;
CREATE FUNCTION finish_relationship_refresh(p_request uuid,p_outcome text) RETURNS void LANGUAGE sql SECURITY DEFINER AS $$
 UPDATE relationship_refresh_requests SET finished_at=now(),outcome=p_outcome WHERE id=p_request AND finished_at IS NULL
$$;

-- As in 010, for the app's role and the install's time zone (used once a Garmin adapter is on).
CREATE OR REPLACE FUNCTION request_garmin_sync(p_principal uuid,p_subject text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE today date:=install_today(); pending timestamptz; requested timestamptz; accepted boolean:=false;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Garmin sync request');
 IF NOT EXISTS(SELECT 1 FROM subjects WHERE id=p_subject AND kind='person') THEN
   RAISE EXCEPTION 'Unknown subject' USING ERRCODE='22023';
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

-- save_document: 006's household save, generalized. Any person or the household; any https URL or none; the copy is
-- provider 'documents', archived, optionally embedded. A harness login receives it; text is untrusted data.
CREATE FUNCTION save_document(p_principal uuid,p_subject text,p_external text,p_title text,p_url text,
 p_content text,p_modified timestamptz,p_domain text,p_semantic boolean,p_chunks jsonb)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid; previous timestamptz; hash text; chunk jsonb; ordinal integer:=0;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving documents');
 IF NOT EXISTS(SELECT 1 FROM subjects WHERE id=p_subject AND kind IN ('person','household')) THEN
   RAISE EXCEPTION 'Unknown subject' USING ERRCODE='22023';
 END IF;
 IF p_external IS NULL OR p_external !~ '^[A-Za-z0-9_.:-]{1,200}$'
   OR p_title IS NULL OR length(trim(p_title)) NOT BETWEEN 1 AND 300
   OR p_content IS NULL OR length(trim(p_content)) NOT BETWEEN 1 AND 40000 OR length(p_content)>40000
   OR p_domain IS NULL OR p_domain !~ '^[a-z][a-z0-9-]{0,63}$'
   OR (p_url IS NOT NULL AND (length(p_url)>2000 OR p_url !~ '^https://[^\s@/]+(/\S*)?$'))
   OR p_modified IS NULL OR p_modified>now()+interval '5 minutes'
   OR p_semantic IS NULL OR p_chunks IS NULL OR jsonb_typeof(p_chunks)<>'array' THEN
   RAISE EXCEPTION 'Invalid document' USING ERRCODE='22023';
 END IF;
 IF jsonb_array_length(p_chunks)>200 OR (NOT p_semantic AND jsonb_array_length(p_chunks)<>0) THEN
   RAISE EXCEPTION 'Invalid document chunks' USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(61984422);
 SELECT source_updated_at INTO previous FROM resources WHERE subject_id=p_subject AND provider='documents' AND external_id=p_external;
 IF previous>p_modified THEN RAISE EXCEPTION 'Older source revision' USING ERRCODE='22023'; END IF;
 hash:=encode(sha256(convert_to(p_content,'UTF8')),'hex');
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,content,revision,
   source_updated_at,checksum,metadata,status,domain)
 VALUES(gen_random_uuid(),p_subject,'documents',p_external,'document',CASE WHEN p_subject='household' THEN 'shared' ELSE 'private' END,
   p_title,p_url,p_content,hash,p_modified,hash,jsonb_build_object('retrievalMode','dated-snapshot','semanticIndex',p_semantic,
     'archiveConsent','explicit-save','savedBy',p_principal),'current',p_domain)
 ON CONFLICT(subject_id,provider,external_id) DO UPDATE SET title=excluded.title,source_url=excluded.source_url,
   content=excluded.content,revision=excluded.revision,source_updated_at=excluded.source_updated_at,
   checksum=excluded.checksum,metadata=excluded.metadata,status='current',domain=excluded.domain,indexed_at=now()
 RETURNING id INTO saved;
 DELETE FROM resource_chunks WHERE resource_id=saved;
 FOR chunk IN SELECT value FROM jsonb_array_elements(p_chunks) LOOP
   IF coalesce(length(chunk->>'text'),0) NOT BETWEEN 1 AND 12000
     OR coalesce(length(chunk->>'model'),0) NOT BETWEEN 1 AND 100 OR chunk->>'vector' IS NULL THEN
     RAISE EXCEPTION 'Invalid document chunk' USING ERRCODE='22023';
   END IF;
   INSERT INTO resource_chunks(resource_id,ordinal,source_revision,content_hash,model,text_content,embedding)
     VALUES(saved,ordinal,hash,encode(sha256(convert_to(chunk->>'text','UTF8')),'hex'),
       chunk->>'model',chunk->>'text',(chunk->>'vector')::public.vector(384));
   ordinal:=ordinal+1;
 END LOOP;
 RETURN saved;
END $$;

-- The ingest contract: each accepted call by (plugin, idempotency key), kept at least 30 days, and the time of
-- the latest accepted call per (plugin, provider, subject) for freshness.
CREATE TABLE ingest_calls(
 plugin text NOT NULL CHECK(plugin ~ '^[a-z][a-z0-9-]{1,30}$'),
 idempotency_key text NOT NULL CHECK(idempotency_key ~ '^[A-Za-z0-9._:-]{8,200}$'),
 tool text NOT NULL CHECK(tool IN ('ingest_measurements','ingest_records')),
 payload_hash text NOT NULL,
 provider text NOT NULL,
 subject_id text NOT NULL REFERENCES subjects,
 result jsonb NOT NULL,
 received_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(plugin,idempotency_key)
);
CREATE INDEX ingest_calls_received ON ingest_calls(received_at);
CREATE TABLE ingest_freshness(
 plugin text NOT NULL,
 provider text NOT NULL,
 subject_id text NOT NULL REFERENCES subjects,
 last_accepted_at timestamptz NOT NULL,
 last_observed_at timestamptz,
 PRIMARY KEY(plugin,provider,subject_id)
);
REVOKE ALL ON ingest_calls FROM PUBLIC;
REVOKE ALL ON ingest_freshness FROM PUBLIC;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION install_timezone() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION install_today() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION principal_has_role(uuid,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION claim_relationship_refresh() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION finish_relationship_refresh(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_document(uuid,text,text,text,text,text,timestamptz,text,boolean,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 -- CREATE OR REPLACE resets a function's settings, so every replaced entry point gets its search path again.
 EXECUTE format('ALTER FUNCTION dashboard_reader(uuid,text[],text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_insight(uuid,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_feedback(uuid,uuid,text,timestamptz,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insights(uuid,text,timestamptz,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION log_insight_run(uuid,text,text,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insight_runs(uuid,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_dashboards(uuid,uuid,int,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_dashboard(uuid,uuid,int,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION allow_dashboard_edits(uuid,uuid,int,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION begin_dashboard_write(uuid,uuid,int,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION record_dashboard_write(uuid,uuid,int,text,jsonb,text,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION write_dashboard_log(uuid,uuid,int,text,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_dashboard_write(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_dashboard_writes(uuid,uuid,uuid,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_ambient_card(uuid,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_ambient_cards(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION delete_dashboard(uuid,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_sky_pin(uuid,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_sky_pins(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION request_relationship_refresh(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION request_garmin_sync(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION principal_has_role(uuid,text[]) FROM PUBLIC;
REVOKE ALL ON FUNCTION claim_relationship_refresh() FROM PUBLIC;
REVOKE ALL ON FUNCTION finish_relationship_refresh(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION save_document(uuid,text,text,text,text,text,timestamptz,text,boolean,jsonb) FROM PUBLIC;
