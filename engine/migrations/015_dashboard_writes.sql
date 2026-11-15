-- Two-way dashboards and dashboard cards.
-- A version declares what it may write, as it declares its feeds: a log in Rhythm, cells in a Google Sheet or a Google
-- Doc. Writes stay off until someone allows them for a dashboard, and that allowance covers exactly the writes they saw:
-- a later version that declares different writes asks again. Every write is kept with what it replaced, so it can be
-- undone and a harness can read the history. Each corner of the ambient screen may show a dashboard's card instead of its
-- stat card. The engine's writer login receives EXECUTE on the entry points and no table privileges.
ALTER TABLE dashboard_versions ADD COLUMN writes jsonb NOT NULL DEFAULT '[]'::jsonb
  CHECK(jsonb_typeof(writes)='array' AND jsonb_array_length(writes)<=12);
ALTER TABLE dashboards ADD COLUMN edits_allowed jsonb CHECK(edits_allowed IS NULL OR jsonb_typeof(edits_allowed)='array');

CREATE TABLE dashboard_writes(
 id uuid PRIMARY KEY,
 dashboard_id uuid NOT NULL REFERENCES dashboards ON DELETE CASCADE,
 version int NOT NULL CHECK(version BETWEEN 1 AND 10000),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 40),
 kind text NOT NULL CHECK(kind IN ('log','sheet','doc')),
 target jsonb NOT NULL CHECK(jsonb_typeof(target)='object'),
 summary text NOT NULL CHECK(length(summary) BETWEEN 1 AND 300),
 before jsonb,
 after jsonb NOT NULL,
 entry uuid REFERENCES resources ON DELETE SET NULL,
 written_by uuid NOT NULL REFERENCES principals,
 written_at timestamptz NOT NULL DEFAULT now(),
 undone_at timestamptz,
 undone_by uuid REFERENCES principals
);
CREATE INDEX dashboard_writes_newest ON dashboard_writes(dashboard_id,written_at DESC);
CREATE TABLE ambient_cards(
 corner text PRIMARY KEY CHECK(corner IN ('glucose','recovery','training','changed')),
 dashboard_id uuid NOT NULL REFERENCES dashboards ON DELETE CASCADE,
 set_by uuid NOT NULL REFERENCES principals,
 set_at timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON dashboard_writes FROM PUBLIC;
REVOKE ALL ON ambient_cards FROM PUBLIC;

CREATE FUNCTION dashboard_reader(p_principal uuid,p_names text[],p_what text) RETURNS void LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals WHERE id=p_principal AND enabled AND name=ANY(p_names)) THEN
   RAISE EXCEPTION '% is not authorized',p_what USING ERRCODE='42501';
 END IF;
END $$;

-- One version as every reader receives it: what it may write and whether edits are allowed for exactly those writes,
-- the corners showing its card, and every version's note; the page and its feeds only when asked for.
CREATE OR REPLACE FUNCTION dashboard_json(d dashboards,v dashboard_versions,p_code boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',d.id,'version',v.version,'latest',(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id),
   'title',v.title,'description',v.description,'stars',v.stars,'note',v.note,'restoredFrom',v.restored_from,
   'savedAt',v.created_at,'createdAt',d.created_at,'updatedAt',d.updated_at,'writes',v.writes,
   'editsAllowed',jsonb_array_length(v.writes)>0 AND d.edits_allowed IS NOT NULL AND d.edits_allowed=v.writes,
   'corners',(SELECT coalesce(jsonb_agg(corner ORDER BY corner),'[]'::jsonb) FROM ambient_cards WHERE dashboard_id=d.id),
   'versions',(SELECT jsonb_agg(jsonb_build_object('version',h.version,'note',h.note,'restoredFrom',h.restored_from,'savedAt',h.created_at)
     ORDER BY h.version DESC) FROM (SELECT * FROM dashboard_versions WHERE dashboard_id=d.id ORDER BY version DESC LIMIT 100) h))
   || CASE WHEN p_code THEN jsonb_build_object('feeds',v.feeds,'html',v.html) ELSE '{}'::jsonb END
$$;

-- As in 013, now carrying the version's declared writes (none unless given), and a restore copies them.
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

-- Someone turns edits on (or off) for a dashboard from the app: on covers exactly the writes that version declares.
CREATE FUNCTION allow_dashboard_edits(p_principal uuid,p_dashboard uuid,p_version int,p_allowed boolean)
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

-- The declared write `p_name` of that version, when edits are allowed for its writes; refused otherwise.
CREATE FUNCTION dashboard_write_allowed(p_dashboard uuid,p_version int,p_name text) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE declared jsonb; allowed jsonb; one jsonb;
BEGIN
 SELECT v.writes,d.edits_allowed INTO declared,allowed FROM dashboards d JOIN dashboard_versions v ON v.dashboard_id=d.id
   WHERE d.id=p_dashboard AND v.version=p_version;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002'; END IF;
 SELECT w INTO one FROM jsonb_array_elements(declared) w WHERE w->>'name'=p_name;
 IF one IS NULL THEN RAISE EXCEPTION 'This dashboard declares no write named %',p_name USING ERRCODE='P0002'; END IF;
 IF allowed IS NULL OR allowed<>declared THEN RAISE EXCEPTION 'Edits are off for this dashboard' USING ERRCODE='42501'; END IF;
 RETURN one;
END $$;

-- Before the API changes a Sheet or Doc: the declared write, when allowed.
CREATE FUNCTION begin_dashboard_write(p_principal uuid,p_dashboard uuid,p_version int,p_name text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 RETURN dashboard_write_allowed(p_dashboard,p_version,p_name);
END $$;

-- After the API changed a Sheet or Doc: the write and what it replaced, checked against the declaration again.
CREATE FUNCTION record_dashboard_write(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_target jsonb,
  p_summary text,p_before jsonb,p_after jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb:=dashboard_write_allowed(p_dashboard,p_version,p_name); written uuid:=gen_random_uuid();
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 IF declared->>'kind' NOT IN ('sheet','doc') THEN RAISE EXCEPTION 'Not a Google write' USING ERRCODE='22023'; END IF;
 INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,before,after,written_by)
 VALUES(written,p_dashboard,p_version,p_name,declared->>'kind',p_target,p_summary,p_before,p_after,p_principal);
 RETURN dashboard_write_json(written);
END $$;

-- A log entry: a number with its unit, a check-off or a note, kept as a record of provider "log" so its numbers are
-- measurements (a data star once the map rebuilds). The entry, its measurement and the history row land together.
CREATE FUNCTION write_dashboard_log(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_entry jsonb)
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
 IF day NOT BETWEEN (now() AT TIME ZONE 'UTC')::date-400 AND (now() AT TIME ZONE 'UTC')::date+1 THEN
   RAISE EXCEPTION 'A log date is within the last 400 days' USING ERRCODE='22023';
 END IF;
 entry_title:=coalesce(p_entry->>'metric',p_entry->>'check','note');
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,checksum,source_updated_at,status,local_date,timezone,metadata)
 VALUES(record,who,'log',logged||':'||record,'log-entry',CASE WHEN subject_kind='household' THEN 'shared' ELSE 'private' END,
   initcap(replace(logged,'-',' '))||' log · '||entry_title,'',md5(p_entry::text),now(),'current',day,'UTC',
   -- The note stays in metadata: a record with text of its own would show as a document star of its own.
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

-- Marks a write undone, once. A log entry leaves the log; the API has already restored a Sheet or Doc.
CREATE FUNCTION undo_dashboard_write(p_principal uuid,p_write uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
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

CREATE FUNCTION dashboard_write_json(p_write uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',w.id,'dashboard',w.dashboard_id,'version',w.version,'name',w.name,'kind',w.kind,'target',w.target,
   'summary',w.summary,'before',w.before,'after',w.after,'writtenAt',w.written_at,'undoneAt',w.undone_at)
 FROM dashboard_writes w WHERE w.id=p_write
$$;

-- The history, newest first: one write, one dashboard's writes or every dashboard's.
CREATE FUNCTION read_dashboard_writes(p_principal uuid,p_dashboard uuid,p_write uuid,p_limit int)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading dashboard writes');
 IF p_write IS NOT NULL THEN RETURN dashboard_write_json(p_write); END IF;
 RETURN (SELECT coalesce(jsonb_agg(dashboard_write_json(w.id) ORDER BY w.written_at DESC,w.id),'[]'::jsonb) FROM (
   SELECT id,written_at FROM dashboard_writes WHERE p_dashboard IS NULL OR dashboard_id=p_dashboard
   ORDER BY written_at DESC,id LIMIT least(greatest(coalesce(p_limit,50),1),200)) w);
END $$;

-- The ambient screen's corners that show a dashboard's card instead of their stat card.
CREATE FUNCTION set_ambient_card(p_principal uuid,p_corner text,p_dashboard uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
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
CREATE FUNCTION read_ambient_cards(p_principal uuid) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading corner cards');
 RETURN (SELECT coalesce(jsonb_object_agg(corner,dashboard_id),'{}'::jsonb) FROM ambient_cards);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION dashboard_reader(uuid,text[],text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION dashboard_write_allowed(uuid,int,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION dashboard_write_json(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION allow_dashboard_edits(uuid,uuid,int,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION begin_dashboard_write(uuid,uuid,int,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION record_dashboard_write(uuid,uuid,int,text,jsonb,text,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION write_dashboard_log(uuid,uuid,int,text,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION undo_dashboard_write(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_dashboard_writes(uuid,uuid,uuid,int) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_ambient_card(uuid,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_ambient_cards(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION dashboard_reader(uuid,text[],text) FROM PUBLIC;
REVOKE ALL ON FUNCTION dashboard_write_allowed(uuid,int,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION dashboard_write_json(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION allow_dashboard_edits(uuid,uuid,int,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION begin_dashboard_write(uuid,uuid,int,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION record_dashboard_write(uuid,uuid,int,text,jsonb,text,jsonb,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION write_dashboard_log(uuid,uuid,int,text,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION undo_dashboard_write(uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_dashboard_writes(uuid,uuid,uuid,int) FROM PUBLIC;
REVOKE ALL ON FUNCTION set_ambient_card(uuid,text,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_ambient_cards(uuid) FROM PUBLIC;
