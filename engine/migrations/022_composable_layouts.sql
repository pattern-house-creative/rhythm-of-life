-- Composable layouts (docs/dev/screens.md 10.7): a dashboard is a grid of parts, its page one of them. A version keeps
-- the grid it was saved with; moves, resizes and focus changes arrange the newest version in place, as a layout
-- revision, without a new version, so its page, feeds, writes, edits_allowed and updated_at stay as they are. The
-- engine checks every layout's rules before saving it; these functions check the role, the newest version, the
-- revision a change started from and, for the app role, that a change only arranges the parts it was given. A NULL
-- layout is the page alone, as for every version saved before this migration. Only these functions write here.
ALTER TABLE dashboard_versions ADD COLUMN layout jsonb
  CHECK(layout IS NULL OR (jsonb_typeof(layout)='object' AND length(layout::text)<=8000));
-- The newest version's arrangement as moves, resizes and focus changes leave it: at most one row per version, removed
-- with its version.
CREATE TABLE dashboard_layouts(
 dashboard_id uuid NOT NULL,
 version int NOT NULL,
 revision int NOT NULL CHECK(revision BETWEEN 1 AND 1000000),
 layout jsonb NOT NULL CHECK(jsonb_typeof(layout)='object' AND length(layout::text)<=8000),
 note text NOT NULL CHECK(length(note) BETWEEN 1 AND 500),
 saved_by uuid NOT NULL REFERENCES principals,
 saved_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(dashboard_id,version),
 FOREIGN KEY(dashboard_id,version) REFERENCES dashboard_versions ON DELETE CASCADE
);
REVOKE ALL ON dashboard_layouts FROM PUBLIC;

-- A version's layout as it stands: its arrangement when it has one, else the layout it was saved with, else the page
-- alone; and the arrangement's revision (0 for the layout it was saved with).
CREATE FUNCTION dashboard_layout(p_dashboard uuid,p_version int) RETURNS TABLE(layout jsonb,revision int,note text,saved_at timestamptz)
LANGUAGE sql STABLE AS $$
 SELECT coalesce(l.layout,v.layout,'{"parts":[{"id":"page","kind":"page","x":0,"y":0,"w":12,"h":12}]}'::jsonb),
   coalesce(l.revision,0),l.note,l.saved_at
 FROM dashboard_versions v LEFT JOIN dashboard_layouts l ON l.dashboard_id=v.dashboard_id AND l.version=v.version
 WHERE v.dashboard_id=p_dashboard AND v.version=p_version
$$;

-- What a layout's parts are apart from where they sit and a map's focus: each part without x, y, w, h (and focus for a
-- map), in id order, beside every other member of the layout. Two layouts with the same identity arrange the same parts.
CREATE FUNCTION layout_identity(p_layout jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
 SELECT (p_layout-'parts')||jsonb_build_object('parts',CASE WHEN jsonb_typeof(p_layout->'parts')='array' THEN
   (SELECT coalesce(jsonb_agg(p-'x'-'y'-'w'-'h'-CASE WHEN p->>'kind'='map' THEN 'focus' ELSE '' END ORDER BY p->>'id',p::text),'[]'::jsonb)
    FROM jsonb_array_elements(p_layout->'parts') p) ELSE p_layout->'parts' END)
$$;

-- As in 015, with each version's layout, its revision, and the note and time of its last arrangement (null for the
-- layout it was saved with). A version saved without a layout answers layout null, so a reader can tell a dashboard
-- never arranged; it is the page alone.
CREATE OR REPLACE FUNCTION dashboard_json(d dashboards,v dashboard_versions,p_code boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',d.id,'version',v.version,'latest',(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id),
   'title',v.title,'description',v.description,'stars',v.stars,'note',v.note,'restoredFrom',v.restored_from,
   'savedAt',v.created_at,'createdAt',d.created_at,'updatedAt',d.updated_at,'writes',v.writes,
   'editsAllowed',jsonb_array_length(v.writes)>0 AND d.edits_allowed IS NOT NULL AND d.edits_allowed=v.writes,
   'corners',(SELECT coalesce(jsonb_agg(corner ORDER BY corner),'[]'::jsonb) FROM ambient_cards WHERE dashboard_id=d.id),
   'versions',(SELECT jsonb_agg(jsonb_build_object('version',h.version,'note',h.note,'restoredFrom',h.restored_from,'savedAt',h.created_at)
     ORDER BY h.version DESC) FROM (SELECT * FROM dashboard_versions WHERE dashboard_id=d.id ORDER BY version DESC LIMIT 100) h),
   'layout',coalesce(l.layout,v.layout),'layoutRevision',coalesce(l.revision,0),'layoutNote',l.note,'layoutSavedAt',l.saved_at)
   || CASE WHEN p_code THEN jsonb_build_object('feeds',v.feeds,'html',v.html) ELSE '{}'::jsonb END
 FROM (SELECT 1) one LEFT JOIN dashboard_layouts l ON l.dashboard_id=d.id AND l.version=v.version
$$;

-- As in 017, now with a layout: a new dashboard keeps the one it is given (none is the page alone); a next version
-- keeps the one it is given or else the newest version's, so revising the page keeps the arrangement; a restore brings
-- the restored version back as it was last arranged.
CREATE OR REPLACE FUNCTION save_dashboard(p_principal uuid,p_dashboard uuid,p_restore int,p_version jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE target uuid:=coalesce(p_dashboard,gen_random_uuid()); numbered int:=1; earlier dashboard_versions; arranged jsonb;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving dashboards');
 IF p_version ? 'layout' AND jsonb_typeof(p_version->'layout')<>'object' THEN
   RAISE EXCEPTION 'A layout is an object' USING ERRCODE='22023';
 END IF;
 IF p_dashboard IS NULL THEN
   IF p_restore IS NOT NULL THEN RAISE EXCEPTION 'Restoring needs a dashboard' USING ERRCODE='22023'; END IF;
   INSERT INTO dashboards(id,created_by) VALUES(target,p_principal);
   arranged:=p_version->'layout';
 ELSE
   PERFORM 1 FROM dashboards WHERE id=target FOR UPDATE;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
   SELECT max(version)+1 INTO numbered FROM dashboard_versions WHERE dashboard_id=target;
   UPDATE dashboards SET updated_at=now() WHERE id=target;
   arranged:=coalesce(p_version->'layout',(SELECT coalesce(l.layout,v.layout) FROM dashboard_versions v
     LEFT JOIN dashboard_layouts l ON l.dashboard_id=v.dashboard_id AND l.version=v.version
     WHERE v.dashboard_id=target AND v.version=numbered-1));
 END IF;
 IF p_restore IS NOT NULL THEN
   SELECT * INTO earlier FROM dashboard_versions WHERE dashboard_id=target AND version=p_restore;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002'; END IF;
   IF p_version ? 'layout' THEN RAISE EXCEPTION 'A restore brings back that version''s own layout' USING ERRCODE='22023'; END IF;
   arranged:=coalesce((SELECT layout FROM dashboard_layouts WHERE dashboard_id=target AND version=p_restore),earlier.layout);
   INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,writes,note,restored_from,saved_by,layout)
   VALUES(target,numbered,earlier.title,earlier.description,earlier.html,earlier.feeds,earlier.stars,earlier.writes,p_version->>'note',p_restore,
     p_principal,arranged);
 ELSE
   INSERT INTO dashboard_versions(dashboard_id,version,title,description,html,feeds,stars,writes,note,saved_by,layout)
   VALUES(target,numbered,p_version->>'title',coalesce(p_version->>'description',''),p_version->>'html',p_version->'feeds',
     p_version->'stars',coalesce(p_version->'writes','[]'::jsonb),p_version->>'note',p_principal,arranged);
 END IF;
 RETURN jsonb_build_object('id',target,'version',numbered);
END $$;

-- Arranges a dashboard's newest version in place: the app (any screen) moves, resizes and refocuses its parts, and a
-- harness arranges or composes it. A change made from a revision that is no longer current saves nothing and answers
-- the current layout with saved false, so the caller can apply its change again; otherwise the layout is saved as the
-- next revision. For the app role the layout must keep the parts it has, each with its kind and members: only where
-- each sits and a map's focus may change, and only from the revision it read. Nothing else about the dashboard
-- changes.
CREATE FUNCTION save_dashboard_layout(p_principal uuid,p_dashboard uuid,p_version int,p_base int,p_layout jsonb,p_note text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE newest int; arranged jsonb; revised int;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app','harness'],'Arranging dashboards');
 IF p_base IS NULL AND principal_has_role(p_principal,ARRAY['app']) THEN
   RAISE EXCEPTION 'A screen arranges from the layout revision it read' USING ERRCODE='22023';
 END IF;
 IF p_layout IS NULL OR jsonb_typeof(p_layout)<>'object' OR length(p_layout::text)>8000 THEN
   RAISE EXCEPTION 'A layout is an object of at most 8000 characters' USING ERRCODE='22023';
 END IF;
 IF p_note IS NULL OR length(btrim(p_note)) NOT BETWEEN 1 AND 500 THEN
   RAISE EXCEPTION 'A layout''s note is 1-500 characters' USING ERRCODE='22023';
 END IF;
 IF p_base<0 THEN RAISE EXCEPTION 'A layout revision is 0 or more' USING ERRCODE='22023'; END IF;
 PERFORM 1 FROM dashboards WHERE id=p_dashboard FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
 SELECT max(version) INTO newest FROM dashboard_versions WHERE dashboard_id=p_dashboard;
 IF p_version IS DISTINCT FROM newest THEN
   IF NOT EXISTS(SELECT 1 FROM dashboard_versions WHERE dashboard_id=p_dashboard AND version=p_version) THEN
     RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002';
   END IF;
   RAISE EXCEPTION 'Only the newest version is arranged' USING ERRCODE='22023';
 END IF;
 SELECT a.layout,a.revision INTO arranged,revised FROM dashboard_layout(p_dashboard,newest) a;
 IF p_base IS NOT NULL AND p_base<>revised THEN
   RETURN jsonb_build_object('id',p_dashboard,'version',newest,'layout',arranged,'layoutRevision',revised,'saved',false);
 END IF;
 IF principal_has_role(p_principal,ARRAY['app']) AND layout_identity(p_layout) IS DISTINCT FROM layout_identity(arranged) THEN
   RAISE EXCEPTION 'A screen only moves, resizes or refocuses the parts a dashboard has' USING ERRCODE='22023';
 END IF;
 INSERT INTO dashboard_layouts(dashboard_id,version,revision,layout,note,saved_by)
 VALUES(p_dashboard,newest,revised+1,p_layout,btrim(p_note),p_principal)
 ON CONFLICT(dashboard_id,version) DO UPDATE SET revision=excluded.revision,layout=excluded.layout,note=excluded.note,
   saved_by=excluded.saved_by,saved_at=now();
 RETURN jsonb_build_object('id',p_dashboard,'version',newest,'layout',p_layout,'layoutRevision',revised+1,'saved',true);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION dashboard_layout(uuid,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION layout_identity(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION dashboard_json(dashboards,dashboard_versions,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_dashboard(uuid,uuid,int,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_dashboard_layout(uuid,uuid,int,int,jsonb,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION dashboard_layout(uuid,int) FROM PUBLIC;
REVOKE ALL ON FUNCTION layout_identity(jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION dashboard_json(dashboards,dashboard_versions,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION save_dashboard(uuid,uuid,int,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION save_dashboard_layout(uuid,uuid,int,int,jsonb,text) FROM PUBLIC;
