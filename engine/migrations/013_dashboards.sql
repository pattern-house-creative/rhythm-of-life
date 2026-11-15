-- Living dashboards. A harness login saves a dashboard and each later version; it and
-- the app's login read them, only through these functions. The engine's writer login receives EXECUTE
-- on the two entry points and no table privileges. A page is stored as text and never run by the engine: the app runs
-- it in a sandboxed frame. Text is untrusted data wherever it is shown.
CREATE TABLE dashboards(
 id uuid PRIMARY KEY,
 created_by uuid NOT NULL REFERENCES principals,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE dashboard_versions(
 dashboard_id uuid NOT NULL REFERENCES dashboards ON DELETE CASCADE,
 version int NOT NULL CHECK(version BETWEEN 1 AND 10000),
 title text NOT NULL CHECK(length(title) BETWEEN 1 AND 120),
 description text NOT NULL CHECK(length(description)<=600),
 html text NOT NULL CHECK(length(html) BETWEEN 1 AND 300000),
 feeds jsonb NOT NULL CHECK(jsonb_typeof(feeds)='array' AND jsonb_array_length(feeds) BETWEEN 1 AND 12),
 stars jsonb NOT NULL CHECK(jsonb_typeof(stars)='array' AND jsonb_array_length(stars) BETWEEN 1 AND 12),
 note text NOT NULL CHECK(length(note) BETWEEN 1 AND 500),
 restored_from int CHECK(restored_from BETWEEN 1 AND 10000),
 saved_by uuid NOT NULL REFERENCES principals,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(dashboard_id,version)
);
CREATE INDEX dashboards_newest ON dashboards(updated_at DESC,id);
REVOKE ALL ON dashboards FROM PUBLIC;
REVOKE ALL ON dashboard_versions FROM PUBLIC;

-- One version as every reader receives it, with every version's note for the history; the page and its feeds
-- only when asked for.
CREATE FUNCTION dashboard_json(d dashboards,v dashboard_versions,p_code boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',d.id,'version',v.version,'latest',(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id),
   'title',v.title,'description',v.description,'stars',v.stars,'note',v.note,'restoredFrom',v.restored_from,
   'savedAt',v.created_at,'createdAt',d.created_at,'updatedAt',d.updated_at,
   'versions',(SELECT jsonb_agg(jsonb_build_object('version',h.version,'note',h.note,'restoredFrom',h.restored_from,'savedAt',h.created_at)
     ORDER BY h.version DESC) FROM (SELECT * FROM dashboard_versions WHERE dashboard_id=d.id ORDER BY version DESC LIMIT 100) h))
   || CASE WHEN p_code THEN jsonb_build_object('feeds',v.feeds,'html',v.html) ELSE '{}'::jsonb END
$$;

-- A harness login saves a new dashboard (no id) or the next version of one, either from content the engine has already
-- validated or as a copy of an earlier version (restore).
CREATE FUNCTION save_dashboard(p_principal uuid,p_dashboard uuid,p_restore int,p_version jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE target uuid:=coalesce(p_dashboard,gen_random_uuid()); numbered int:=1; earlier dashboard_versions;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name='harness') THEN
   RAISE EXCEPTION 'Saving dashboards is not authorized' USING ERRCODE='42501';
 END IF;
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
   INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,note,restored_from,saved_by)
   VALUES(target,numbered,earlier.title,earlier.description,earlier.html,earlier.feeds,earlier.stars,p_version->>'note',p_restore,p_principal);
 ELSE
   INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,note,saved_by)
   VALUES(target,numbered,p_version->>'title',coalesce(p_version->>'description',''),p_version->>'html',p_version->'feeds',
     p_version->'stars',p_version->>'note',p_principal);
 END IF;
 RETURN jsonb_build_object('id',target,'version',numbered);
END $$;

-- Every dashboard's latest version, most recently changed first (no page or feeds), or one dashboard's version
-- (the latest unless given) with its page and feeds when p_code. Null when that dashboard or version is unknown.
CREATE FUNCTION read_dashboards(p_principal uuid,p_dashboard uuid,p_version int,p_code boolean)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled
   AND name IN ('harness','app')) THEN
   RAISE EXCEPTION 'Reading dashboards is not authorized' USING ERRCODE='42501';
 END IF;
 IF p_dashboard IS NULL THEN
   IF p_version IS NOT NULL OR p_code THEN RAISE EXCEPTION 'A version needs a dashboard' USING ERRCODE='22023'; END IF;
   RETURN (SELECT coalesce(jsonb_agg(dashboard_json(d,v,false) ORDER BY d.updated_at DESC,d.id),'[]'::jsonb)
     FROM (SELECT * FROM dashboards ORDER BY updated_at DESC,id LIMIT 100) d
     JOIN dashboard_versions v ON v.dashboard_id=d.id AND v.version=(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id));
 END IF;
 RETURN (SELECT dashboard_json(d,v,coalesce(p_code,false)) FROM dashboards d JOIN dashboard_versions v ON v.dashboard_id=d.id
   WHERE d.id=p_dashboard AND v.version=coalesce(p_version,(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id)));
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION dashboard_json(dashboards,dashboard_versions,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_dashboard(uuid,uuid,int,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_dashboards(uuid,uuid,int,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION dashboard_json(dashboards,dashboard_versions,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION save_dashboard(uuid,uuid,int,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_dashboards(uuid,uuid,int,boolean) FROM PUBLIC;
