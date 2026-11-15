-- A subject and a scope on derived records (docs/dev/memory.md sections 5 and 8; SB-P1-06, agreed with the screens
-- program on the coordination board): insights and their runs, dashboards, the writes a dashboard made, corner cards and
-- pinned stars. A dashboard's versions and layouts have no column of their own: they are their dashboard's. A derived
-- record is read only by readers of its subject (the one read predicate, readable_subjects) and, with memory.scopes on,
-- of every star it cites (a dashboard also of every log it reads or writes, an insight of what its sources name),
-- judged at each read: which stars a caller reads now is the map's to say, so the engine hands each function the stars
-- its caller reads (p_stars; for insights with the records it reads among those their sources name). A record
-- therefore narrows when a star it cites narrows, and one its reader can't read, or one citing a star nobody can read
-- now, answers as an unknown one.
--
-- While memory.scopes is off, every install's mode, nothing changes: every row this migration finds, and every one
-- written while scopes are off, is the household's (subject household, scope shared); p_stars is ignored; the functions
-- refuse nothing they did not refuse before and answer as they did. With scopes on, a new record lands in the narrowest
-- subject of what it cites (memory.md section 5), and the author rules of section 5 apply: a private record is changed by
-- its owner, a Family one by the person who wrote it or an admin, and one no person wrote (every row made before) by any
-- principal that reads Family. A record's author is principals.person_id of the principal that made it, which 023 makes
-- immutable.
--
-- Functions whose arguments grow take the new ones last, with defaults, so an engine of the release before calls them as
-- it did; every function made here has its search path pinned (019). relationship_refresh_requests keeps no subject: a
-- request names no record, and the rebuild is the engine's own job.

-- sources_checked: what an insight's sources named when it was saved with scopes on, each locator of a source not on
-- the web that named a star or a record its saver read (a record by its id), checked again at every read; null for one
-- saved with scopes off, every row this migration finds, whose every such locator is checked at each read instead.
ALTER TABLE insights ADD COLUMN subject_id text NOT NULL DEFAULT 'household' REFERENCES subjects,
  ADD COLUMN scope text NOT NULL DEFAULT 'shared' CHECK(scope IN ('shared','private')),
  ADD COLUMN sources_checked text[],
  ADD CONSTRAINT insights_scope_subject CHECK((subject_id='household')=(scope='shared'));
ALTER TABLE insight_runs ADD COLUMN subject_id text NOT NULL DEFAULT 'household' REFERENCES subjects,
  ADD COLUMN scope text NOT NULL DEFAULT 'shared' CHECK(scope IN ('shared','private')),
  ADD CONSTRAINT insight_runs_scope_subject CHECK((subject_id='household')=(scope='shared'));
ALTER TABLE dashboards ADD COLUMN subject_id text NOT NULL DEFAULT 'household' REFERENCES subjects,
  ADD COLUMN scope text NOT NULL DEFAULT 'shared' CHECK(scope IN ('shared','private')),
  ADD CONSTRAINT dashboards_scope_subject CHECK((subject_id='household')=(scope='shared'));
-- A write's own subject: a Sheet or Doc write its dashboard's, a log write its entry's. Every write made before is the
-- household's but a log write, whose subject is its entry's (the first person's when the write named none).
ALTER TABLE dashboard_writes ADD COLUMN subject_id text REFERENCES subjects;
UPDATE dashboard_writes w SET subject_id=CASE WHEN w.kind='log' AND EXISTS(SELECT 1 FROM subjects s WHERE s.id=w.target->>'subject')
  THEN w.target->>'subject' ELSE 'household' END;
ALTER TABLE dashboard_writes ALTER COLUMN subject_id SET NOT NULL;
-- A corner is the household's until corners are per person (SB-P1-11, then keyed by subject and corner).
ALTER TABLE ambient_cards ADD COLUMN subject_id text NOT NULL DEFAULT 'household' REFERENCES subjects;
-- A pin takes its star's subject: a person's pin of their private star is theirs, and the household's pin of the same
-- star id is another row.
ALTER TABLE sky_pins ADD COLUMN subject_id text NOT NULL DEFAULT 'household' REFERENCES subjects;
ALTER TABLE sky_pins DROP CONSTRAINT sky_pins_pkey, ADD PRIMARY KEY(subject_id,graph_id);

-- True unless the recorded memory.scopes mode is exactly 'off', as readable_subjects and Database.ScopesOff read it.
CREATE FUNCTION scopes_on() RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT NOT coalesce((SELECT value FROM install_settings WHERE key='memory.scopes')='off',false)
$$;

-- Whether a principal reads the derived records of a subject: the one read predicate, for a subject as a whole (a source
-- share covers one provider's rows, never a derived record). With scopes off, every enabled harness and app principal
-- reads every subject, as today.
CREATE FUNCTION reads_subject(p_principal uuid,p_subject text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM readable_subjects(p_principal) x WHERE (x.subject_id IS NULL OR x.subject_id=p_subject) AND x.provider IS NULL)
$$;

-- With scopes on, whether every star of p_cited is among p_stars, the stars the engine says the caller reads now (none
-- when not given). With scopes off, always.
CREATE FUNCTION stars_readable(p_cited text[],p_stars text[]) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT scopes_on() THEN true ELSE coalesce(p_cited,'{}')<@coalesce(p_stars,'{}') END
$$;

-- The subject a derived record lands in (memory.md section 5): with scopes off the household's, as every row is. With
-- scopes on, the narrowest of what it cites and of p_within (an earlier version's subject, an insight it supersedes):
-- p_cited holds {subject, provider} for each star it cites, the subject whose readers the map serves the star to and the
-- source its rows come from, and {subject} for each log it reads or writes. A person's star that a source share lets
-- Family read narrows nothing, nor does the household's or a synthetic (DEMO) one. Two people's are refused (22023), and
-- so is a subject its writer does not read (42501): a writer cites only what it reads.
CREATE FUNCTION derived_subject(p_principal uuid,p_cited jsonb,p_within text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE landed text:=coalesce(p_within,'household'); one jsonb; kind text; cited text;
BEGIN
 IF NOT scopes_on() THEN RETURN 'household'; END IF;
 FOR one IN SELECT value FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_cited)='array' THEN p_cited ELSE '[]'::jsonb END) LOOP
   SELECT s.kind INTO kind FROM subjects s WHERE s.id=one->>'subject';
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown subject' USING ERRCODE='22023'; END IF;
   cited:=CASE WHEN kind<>'person' THEN 'household'
     WHEN one->>'provider' IS NOT NULL AND EXISTS(SELECT 1 FROM source_shares x WHERE x.person_id=one->>'subject' AND x.provider=one->>'provider')
       THEN 'household'
     ELSE one->>'subject' END;
   IF cited='household' OR cited=landed THEN CONTINUE; END IF;
   IF landed<>'household' THEN RAISE EXCEPTION 'A record cites two people''s private records' USING ERRCODE='22023'; END IF;
   landed:=cited;
 END LOOP;
 IF NOT reads_subject(p_principal,landed) THEN RAISE EXCEPTION 'A record cites what its writer does not read' USING ERRCODE='42501'; END IF;
 RETURN landed;
END $$;

-- Whether a principal may change a derived record of subject p_subject that principal p_author made (memory.md section
-- 5). With scopes off, any principal of the role the function takes, as today. With scopes on, only a reader of its
-- subject: a private record's owner; a Family record a person made, that person or an admin; a Family record no person
-- made, any principal that reads Family.
CREATE FUNCTION may_change(p_principal uuid,p_subject text,p_author uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT scopes_on() THEN true
   WHEN NOT reads_subject(p_principal,p_subject) THEN false
   WHEN p_subject<>'household' THEN coalesce((SELECT person_id FROM principals WHERE id=p_principal)=p_subject,false)
   ELSE coalesce((SELECT a.person_id IS NULL OR a.person_id=c.person_id
       OR EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=c.person_id AND m.role='admin')
     FROM principals a,principals c WHERE a.id=p_author AND c.id=p_principal),false) END
$$;

-- The locators of an insight's sources that are not on the web, a record id in its canonical form (lower case).
CREATE FUNCTION insight_locators(p_sources jsonb) RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
 SELECT coalesce(array_agg(CASE WHEN x.l ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN lower(x.l) ELSE x.l END),'{}')
 FROM (SELECT s->>'locator' AS l FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_sources)='array' THEN p_sources ELSE '[]'::jsonb END) s
   WHERE s->>'kind' IS DISTINCT FROM 'web' AND jsonb_typeof(s->'locator')='string') x
$$;

-- An insight is read by readers of its subject and, with scopes on, of everything it draws on now (memory.md section
-- 8): every star it cites, and what its sources name. p_stars holds the stars the caller reads now and the ids of the
-- records it reads among those the insights' sources name (InsightStore), so a source narrows the insight when what it
-- names does, as a star does. A source counts by what its locator named at save (p_checked, the stars and records its
-- saver read; a locator that named neither landed the insight in its saver's own scope and is not checked again); for
-- an insight saved with scopes off (p_checked null, every row 027 found), by every locator of a source not on the web,
-- so one naming anything the caller doesn't read now, a private record, a star off its map or nothing the engine can
-- tell, is unknown to that caller, its owner included, as one citing a star nobody reads now is.
CREATE FUNCTION insight_readable(p_principal uuid,p_subject text,p_cited jsonb,p_sources jsonb,p_checked text[],p_stars text[])
RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT reads_subject(p_principal,p_subject) THEN false WHEN NOT scopes_on() THEN true
   ELSE (coalesce((SELECT array_agg(s->>'graphId') FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_cited)='array' THEN p_cited ELSE '[]'::jsonb END) s),'{}')
     || coalesce(p_checked,insight_locators(p_sources)))<@coalesce(p_stars,'{}') END
$$;

-- The stars one dashboard version names (memory.md section 8): its stars (those its feeds read and its writes write) and
-- each map part's focus star, in its layout as it stands.
CREATE FUNCTION dashboard_version_stars(p_dashboard uuid,p_version int) RETURNS text[] LANGUAGE sql STABLE AS $$
 SELECT coalesce(array_agg(DISTINCT s.star) FILTER (WHERE s.star IS NOT NULL),'{}') FROM (
   SELECT n->>'graphId' AS star FROM dashboard_versions v CROSS JOIN LATERAL jsonb_array_elements(v.stars) n
    WHERE v.dashboard_id=p_dashboard AND v.version=p_version
   UNION ALL
   SELECT btrim(p->'focus'->'near'->>'star') FROM dashboard_layout(p_dashboard,p_version) a
    CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(a.layout->'parts')='array' THEN a.layout->'parts' ELSE '[]'::jsonb END) p
    WHERE p->>'kind'='map') s
$$;

-- Whether a principal reads one version of a dashboard: the dashboard's subject and, with scopes on, every star that
-- version names now and the subject of every log its feeds read and its writes write (a page draws on its logs as on
-- its stars: a Family dashboard saved before the switch whose log is Alex's is Alex's readers' alone). A dashboard is
-- read through its newest version (dashboard_readable), so one whose newest version cites a star or a log its reader
-- can't read answers as unknown, whichever version is asked for.
CREATE FUNCTION dashboard_version_readable(p_principal uuid,p_dashboard uuid,p_version int,p_stars text[]) RETURNS boolean
LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT coalesce((SELECT reads_subject(p_principal,d.subject_id) FROM dashboards d WHERE d.id=p_dashboard),false) THEN false
   WHEN NOT scopes_on() THEN true
   ELSE dashboard_version_stars(p_dashboard,p_version)<@coalesce(p_stars,'{}')
     AND NOT EXISTS(SELECT 1 FROM dashboard_versions v CROSS JOIN LATERAL jsonb_array_elements(
         coalesce(CASE WHEN jsonb_typeof(v.feeds)='array' THEN v.feeds END,'[]'::jsonb)
         || coalesce(CASE WHEN jsonb_typeof(v.writes)='array' THEN v.writes END,'[]'::jsonb)) f
       WHERE v.dashboard_id=p_dashboard AND v.version=p_version AND f->>'kind'='log'
         AND NOT reads_subject(p_principal,coalesce(f->>'subject',''))) END
$$;
CREATE FUNCTION dashboard_readable(p_principal uuid,p_dashboard uuid,p_stars text[]) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT dashboard_version_readable(p_principal,p_dashboard,(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=p_dashboard),p_stars)
$$;

-- What a version cites, for derived_subject: the {subject, provider} of each star the engine passes (p_cited) and the
-- subject of each log its feeds read and its writes write.
CREATE FUNCTION dashboard_cites(p_cited jsonb,p_feeds jsonb,p_writes jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
 SELECT coalesce(CASE WHEN jsonb_typeof(p_cited)='array' THEN p_cited END,'[]'::jsonb)
   || coalesce((SELECT jsonb_agg(jsonb_build_object('subject',f->>'subject')) FROM jsonb_array_elements(
     coalesce(CASE WHEN jsonb_typeof(p_feeds)='array' THEN p_feeds END,'[]'::jsonb)
     || coalesce(CASE WHEN jsonb_typeof(p_writes)='array' THEN p_writes END,'[]'::jsonb)) f WHERE f->>'kind'='log'),'[]'::jsonb)
$$;

-- A layout as one reader is shown it: with scopes on, a card part whose dashboard that reader can't read, or one deleted
-- since (the two look alike, docs/dev/screens.md 10.3), is the part with dashboard null and unavailable true, so it names
-- no id and no title. With scopes off, the layout as stored.
CREATE FUNCTION dashboard_cards_shown(p_layout jsonb,p_principal uuid,p_stars text[]) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_layout IS NULL OR jsonb_typeof(p_layout->'parts') IS DISTINCT FROM 'array' OR NOT scopes_on() THEN p_layout
   ELSE jsonb_set(p_layout,'{parts}',(SELECT coalesce(jsonb_agg(CASE WHEN p->>'kind' IS DISTINCT FROM 'card' THEN p
       WHEN jsonb_typeof(p->'dashboard')='string' AND p->>'dashboard' ~ '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
         THEN CASE WHEN dashboard_readable(p_principal,(p->>'dashboard')::uuid,p_stars) THEN p
           ELSE (p-'dashboard')||jsonb_build_object('dashboard',NULL,'unavailable',true) END
       ELSE (p-'dashboard')||jsonb_build_object('dashboard',NULL,'unavailable',true) END ORDER BY x.o),'[]'::jsonb)
     FROM jsonb_array_elements(p_layout->'parts') WITH ORDINALITY x(p,o))) END
$$;

-- Before a dashboard write: the dashboard and that version as its writer reads them (unknown otherwise, as an unknown
-- version is), and the author rule. The dashboard's subject.
CREATE FUNCTION dashboard_write_scope(p_principal uuid,p_dashboard uuid,p_version int,p_stars text[]) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE saved dashboards;
BEGIN
 SELECT * INTO saved FROM dashboards WHERE id=p_dashboard;
 IF NOT FOUND OR NOT dashboard_readable(p_principal,p_dashboard,p_stars)
   OR NOT dashboard_version_readable(p_principal,p_dashboard,p_version,p_stars) THEN
   RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002';
 END IF;
 IF NOT may_change(p_principal,saved.subject_id,saved.created_by) THEN
   RAISE EXCEPTION 'Only its owner, the person who made it or an admin writes from this dashboard' USING ERRCODE='42501';
 END IF;
 RETURN saved.subject_id;
END $$;

-- Each function below takes, last, the stars its caller reads now (dashboard_json the reader too). The old ones go, so a
-- call with the old arguments resolves to the new one, its new arguments null.
DROP FUNCTION dashboard_json(dashboards,dashboard_versions,boolean);
DROP FUNCTION read_dashboards(uuid,uuid,int,boolean);
DROP FUNCTION save_dashboard(uuid,uuid,int,jsonb);
DROP FUNCTION save_dashboard_layout(uuid,uuid,int,int,jsonb,text);
DROP FUNCTION allow_dashboard_edits(uuid,uuid,int,boolean);
DROP FUNCTION begin_dashboard_write(uuid,uuid,int,text);
DROP FUNCTION record_dashboard_write(uuid,uuid,int,text,jsonb,text,jsonb,jsonb);
DROP FUNCTION write_dashboard_log(uuid,uuid,int,text,jsonb);
DROP FUNCTION undo_dashboard_write(uuid,uuid);
DROP FUNCTION read_dashboard_writes(uuid,uuid,uuid,int);
DROP FUNCTION set_ambient_card(uuid,text,uuid);
DROP FUNCTION read_ambient_cards(uuid);
DROP FUNCTION delete_dashboard(uuid,uuid,text);
DROP FUNCTION set_sky_pin(uuid,text,boolean);
DROP FUNCTION read_sky_pins(uuid);
DROP FUNCTION save_insight(uuid,jsonb);
DROP FUNCTION insight_feedback(uuid,uuid,text,timestamptz,text);
DROP FUNCTION read_insights(uuid,text,timestamptz,int);
DROP FUNCTION log_insight_run(uuid,text,text,text,uuid);
DROP FUNCTION read_insight_runs(uuid,int);

-- As in 022, for one reader: the history lists the versions it reads, the corners those of subjects it reads, and with
-- scopes on a card part of a dashboard it can't read is unavailable (dashboard_cards_shown).
CREATE FUNCTION dashboard_json(d dashboards,v dashboard_versions,p_code boolean,p_principal uuid,p_stars text[]) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',d.id,'version',v.version,'latest',(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id),
   'title',v.title,'description',v.description,'stars',v.stars,'note',v.note,'restoredFrom',v.restored_from,
   'savedAt',v.created_at,'createdAt',d.created_at,'updatedAt',d.updated_at,'writes',v.writes,
   'editsAllowed',jsonb_array_length(v.writes)>0 AND d.edits_allowed IS NOT NULL AND d.edits_allowed=v.writes,
   'corners',(SELECT coalesce(jsonb_agg(c.corner ORDER BY c.corner),'[]'::jsonb) FROM ambient_cards c
     WHERE c.dashboard_id=d.id AND reads_subject(p_principal,c.subject_id)),
   'versions',(SELECT jsonb_agg(jsonb_build_object('version',h.version,'note',h.note,'restoredFrom',h.restored_from,'savedAt',h.created_at)
     ORDER BY h.version DESC) FROM (SELECT * FROM dashboard_versions x WHERE x.dashboard_id=d.id
       AND (NOT scopes_on() OR dashboard_version_readable(p_principal,d.id,x.version,p_stars)) ORDER BY x.version DESC LIMIT 100) h),
   'layout',dashboard_cards_shown(coalesce(l.layout,v.layout),p_principal,p_stars),'layoutRevision',coalesce(l.revision,0),
   'layoutNote',l.note,'layoutSavedAt',l.saved_at)
   || CASE WHEN p_code THEN jsonb_build_object('feeds',v.feeds,'html',v.html) ELSE '{}'::jsonb END
 FROM (SELECT 1) one LEFT JOIN dashboard_layouts l ON l.dashboard_id=d.id AND l.version=v.version
$$;

-- As in 017, the dashboards and versions the reader reads: the list counted after that filter, and one it can't read
-- null, as an unknown one.
CREATE FUNCTION read_dashboards(p_principal uuid,p_dashboard uuid,p_version int,p_code boolean,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading dashboards');
 IF p_dashboard IS NULL THEN
   IF p_version IS NOT NULL OR p_code THEN RAISE EXCEPTION 'A version needs a dashboard' USING ERRCODE='22023'; END IF;
   RETURN (SELECT coalesce(jsonb_agg(dashboard_json(d,v,false,p_principal,p_stars) ORDER BY d.updated_at DESC,d.id),'[]'::jsonb)
     FROM (SELECT * FROM dashboards x WHERE dashboard_readable(p_principal,x.id,p_stars) ORDER BY x.updated_at DESC,x.id LIMIT 100) d
     JOIN dashboard_versions v ON v.dashboard_id=d.id AND v.version=(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id));
 END IF;
 RETURN (SELECT dashboard_json(d,v,coalesce(p_code,false),p_principal,p_stars) FROM dashboards d JOIN dashboard_versions v ON v.dashboard_id=d.id
   WHERE d.id=p_dashboard AND v.version=coalesce(p_version,(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id))
     AND dashboard_readable(p_principal,d.id,p_stars) AND dashboard_version_readable(p_principal,d.id,v.version,p_stars));
END $$;

-- As in 022, with a subject: a new dashboard lands in the narrowest subject of the stars (p_version->'cited', which the
-- engine passes) and logs its version names; a next version or a restore may keep it or narrow it, never widen it, and is
-- saved only by a reader of the dashboard under the author rule. With scopes on, a log a version reads or writes is the
-- saver's own person's or the household's, and a private dashboard's log writes are its owner's. With scopes off it
-- saves exactly as 022 did, the dashboard the household's.
CREATE FUNCTION save_dashboard(p_principal uuid,p_dashboard uuid,p_restore int,p_version jsonb,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE target uuid:=coalesce(p_dashboard,gen_random_uuid()); numbered int:=1; earlier dashboard_versions; arranged jsonb;
  saved dashboards; landed text; declared jsonb; scoped boolean:=scopes_on(); person text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving dashboards');
 IF p_version ? 'layout' AND jsonb_typeof(p_version->'layout')<>'object' THEN
   RAISE EXCEPTION 'A layout is an object' USING ERRCODE='22023';
 END IF;
 IF scoped AND p_restore IS NULL THEN
   -- A saver cites only stars it reads now: those its feeds and writes name, and its layout's focus stars.
   IF NOT stars_readable((SELECT array_agg(s.star) FROM (
       SELECT n->>'graphId' AS star FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_version->'stars')='array' THEN p_version->'stars' ELSE '[]'::jsonb END) n
       UNION ALL
       SELECT btrim(p->'focus'->'near'->>'star') FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_version->'layout'->'parts')='array'
         THEN p_version->'layout'->'parts' ELSE '[]'::jsonb END) p WHERE p->>'kind'='map' AND p->'focus'->'near'->>'star' IS NOT NULL) s),p_stars) THEN
     RAISE EXCEPTION 'A dashboard names stars its saver reads' USING ERRCODE='22023';
   END IF;
   person:=(SELECT person_id FROM principals WHERE id=p_principal);
   IF EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(CASE WHEN jsonb_typeof(p_version->'feeds')='array' THEN p_version->'feeds' END,'[]'::jsonb)
       || coalesce(CASE WHEN jsonb_typeof(p_version->'writes')='array' THEN p_version->'writes' END,'[]'::jsonb)) f
     WHERE f->>'kind'='log' AND f->>'subject' IS DISTINCT FROM 'household' AND f->>'subject' IS DISTINCT FROM person) THEN
     RAISE EXCEPTION 'A log a dashboard reads or writes is your own or the household''s' USING ERRCODE='22023';
   END IF;
 END IF;
 IF p_dashboard IS NULL THEN
   IF p_restore IS NOT NULL THEN RAISE EXCEPTION 'Restoring needs a dashboard' USING ERRCODE='22023'; END IF;
   landed:=derived_subject(p_principal,dashboard_cites(p_version->'cited',p_version->'feeds',p_version->'writes'),NULL);
   INSERT INTO dashboards(id,created_by,subject_id,scope) VALUES(target,p_principal,landed,CASE WHEN landed='household' THEN 'shared' ELSE 'private' END);
   arranged:=p_version->'layout';
   declared:=p_version->'writes';
 ELSE
   SELECT * INTO saved FROM dashboards WHERE id=target FOR UPDATE;
   IF NOT FOUND OR NOT dashboard_readable(p_principal,target,p_stars) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
   IF NOT may_change(p_principal,saved.subject_id,saved.created_by) THEN
     RAISE EXCEPTION 'Only its owner, the person who made it or an admin changes this dashboard' USING ERRCODE='42501';
   END IF;
   SELECT max(version)+1 INTO numbered FROM dashboard_versions WHERE dashboard_id=target;
   IF p_restore IS NOT NULL THEN
     SELECT * INTO earlier FROM dashboard_versions WHERE dashboard_id=target AND version=p_restore;
     IF NOT FOUND OR NOT dashboard_version_readable(p_principal,target,p_restore,p_stars) THEN
       RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002';
     END IF;
     landed:=derived_subject(p_principal,dashboard_cites(p_version->'cited',earlier.feeds,earlier.writes),saved.subject_id);
     declared:=earlier.writes;
   ELSE
     landed:=derived_subject(p_principal,dashboard_cites(p_version->'cited',p_version->'feeds',p_version->'writes'),saved.subject_id);
     declared:=p_version->'writes';
   END IF;
   -- A next version or a restore keeps its dashboard's subject unless it is an un-share by the dashboard's own author
   -- (memory.md section 5): only the person who made a Family dashboard takes it into their own private scope. Anyone
   -- else, an admin included, saves a new dashboard instead, so no one hides a Family dashboard from its author or
   -- Family. (derived_subject only narrows, so landed differs only for a Family dashboard going private.)
   IF scoped AND landed<>saved.subject_id AND NOT coalesce((SELECT a.person_id=landed AND c.person_id=landed
       FROM principals a,principals c WHERE a.id=saved.created_by AND c.id=p_principal),false) THEN
     RAISE EXCEPTION 'This version would make a Family dashboard private, which only the person who made it does; save it as a new dashboard, or name the household''s log' USING ERRCODE='22023';
   END IF;
   UPDATE dashboards SET updated_at=now(),subject_id=landed,scope=CASE WHEN landed='household' THEN 'shared' ELSE 'private' END WHERE id=target;
   arranged:=coalesce(p_version->'layout',(SELECT coalesce(l.layout,v.layout) FROM dashboard_versions v
     LEFT JOIN dashboard_layouts l ON l.dashboard_id=v.dashboard_id AND l.version=v.version
     WHERE v.dashboard_id=target AND v.version=numbered-1));
 END IF;
 IF scoped AND landed<>'household' AND EXISTS(SELECT 1 FROM jsonb_array_elements(
     coalesce(CASE WHEN jsonb_typeof(declared)='array' THEN declared END,'[]'::jsonb)) w
   WHERE w->>'kind'='log' AND w->>'subject' IS DISTINCT FROM landed) THEN
   RAISE EXCEPTION 'A private dashboard''s log writes are its owner''s' USING ERRCODE='22023';
 END IF;
 IF p_restore IS NOT NULL THEN
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

-- As in 022 (an app role gives p_base, board message 173), for readers of the dashboard: any of them may arrange it (the
-- same parts, moved, resized or refocused: presentation), while composing (a harness adding, removing or re-pointing
-- parts) follows save_dashboard's author rule. With scopes on every focus star of the new layout is one its saver reads
-- now, so an arrangement never hides the dashboard from the one who made it, and its maps' focus stars never narrow it
-- (below): p_cited holds {subject, provider} for each, as derived_subject takes them, which the engine passes
-- (DashboardStore.SaveLayout).
CREATE FUNCTION save_dashboard_layout(p_principal uuid,p_dashboard uuid,p_version int,p_base int,p_layout jsonb,p_note text,
  p_stars text[] DEFAULT NULL,p_cited jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE newest int; arranged jsonb; revised int; saved dashboards;
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
 SELECT * INTO saved FROM dashboards WHERE id=p_dashboard FOR UPDATE;
 IF NOT FOUND OR NOT dashboard_readable(p_principal,p_dashboard,p_stars) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
 SELECT max(version) INTO newest FROM dashboard_versions WHERE dashboard_id=p_dashboard;
 IF p_version IS DISTINCT FROM newest THEN
   IF NOT EXISTS(SELECT 1 FROM dashboard_versions WHERE dashboard_id=p_dashboard AND version=p_version) THEN
     RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002';
   END IF;
   RAISE EXCEPTION 'Only the newest version is arranged' USING ERRCODE='22023';
 END IF;
 SELECT a.layout,a.revision INTO arranged,revised FROM dashboard_layout(p_dashboard,newest) a;
 IF p_base IS NOT NULL AND p_base<>revised THEN
   RETURN jsonb_build_object('id',p_dashboard,'version',newest,'layout',dashboard_cards_shown(arranged,p_principal,p_stars),
     'layoutRevision',revised,'saved',false);
 END IF;
 IF principal_has_role(p_principal,ARRAY['app']) AND layout_identity(p_layout) IS DISTINCT FROM layout_identity(arranged) THEN
   RAISE EXCEPTION 'A screen only moves, resizes or refocuses the parts a dashboard has' USING ERRCODE='22023';
 END IF;
 IF layout_identity(p_layout) IS DISTINCT FROM layout_identity(arranged) AND NOT may_change(p_principal,saved.subject_id,saved.created_by) THEN
   RAISE EXCEPTION 'Only its owner, the person who made it or an admin changes this dashboard''s parts' USING ERRCODE='42501';
 END IF;
 IF NOT stars_readable((SELECT array_agg(btrim(p->'focus'->'near'->>'star')) FROM jsonb_array_elements(
     CASE WHEN jsonb_typeof(p_layout->'parts')='array' THEN p_layout->'parts' ELSE '[]'::jsonb END) p
     WHERE p->>'kind'='map' AND p->'focus'->'near'->>'star' IS NOT NULL),p_stars) THEN
   RAISE EXCEPTION 'A map part focuses on a star you can read' USING ERRCODE='22023';
 END IF;
 -- A layout never narrows a dashboard (memory.md section 8): a Family dashboard's map focused on a person's private star
 -- would hide it from its author and Family, so whoever saves it (its author and an admin included, arranging or
 -- composing) focuses its maps only on stars that land it where it is, as derived_subject finds from p_cited: Family's,
 -- DEMO ones, a person's that a source share lets Family read. Without p_cited (an engine that passes none) the
 -- function can't tell a private star from a Family one, so a saver bound to a person focuses a Family dashboard's maps
 -- only on stars the dashboard names already. A saver with no person reads only what Family reads (and the stars it
 -- names are among them, checked above), so it refocuses freely either way: the household view's screens do.
 IF scopes_on() AND p_cited IS NOT NULL AND derived_subject(p_principal,p_cited,saved.subject_id)<>saved.subject_id THEN
   RAISE EXCEPTION 'A Family dashboard''s map is focused only on stars Family reads' USING ERRCODE='22023';
 END IF;
 IF scopes_on() AND p_cited IS NULL AND saved.subject_id='household' AND (SELECT person_id FROM principals WHERE id=p_principal) IS NOT NULL
   AND NOT coalesce((SELECT array_agg(btrim(p->'focus'->'near'->>'star')) FROM jsonb_array_elements(
     CASE WHEN jsonb_typeof(p_layout->'parts')='array' THEN p_layout->'parts' ELSE '[]'::jsonb END) p
     WHERE p->>'kind'='map' AND p->'focus'->'near'->>'star' IS NOT NULL),'{}')<@dashboard_version_stars(p_dashboard,newest) THEN
   RAISE EXCEPTION 'A Family dashboard''s map is focused only on stars the dashboard names already' USING ERRCODE='22023';
 END IF;
 INSERT INTO dashboard_layouts(dashboard_id,version,revision,layout,note,saved_by)
 VALUES(p_dashboard,newest,revised+1,p_layout,btrim(p_note),p_principal)
 ON CONFLICT(dashboard_id,version) DO UPDATE SET revision=excluded.revision,layout=excluded.layout,note=excluded.note,
   saved_by=excluded.saved_by,saved_at=now();
 RETURN jsonb_build_object('id',p_dashboard,'version',newest,'layout',p_layout,'layoutRevision',revised+1,'saved',true);
END $$;

-- As in 017, for a reader of that version under the author rule.
CREATE FUNCTION allow_dashboard_edits(p_principal uuid,p_dashboard uuid,p_version int,p_allowed boolean,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb; saved dashboards;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Allowing edits');
 SELECT * INTO saved FROM dashboards WHERE id=p_dashboard;
 SELECT writes INTO declared FROM dashboard_versions WHERE dashboard_id=p_dashboard AND version=p_version;
 IF NOT FOUND OR NOT dashboard_readable(p_principal,p_dashboard,p_stars) OR NOT dashboard_version_readable(p_principal,p_dashboard,p_version,p_stars) THEN
   RAISE EXCEPTION 'Unknown dashboard version' USING ERRCODE='P0002';
 END IF;
 IF NOT may_change(p_principal,saved.subject_id,saved.created_by) THEN
   RAISE EXCEPTION 'Only its owner, the person who made it or an admin allows edits on this dashboard' USING ERRCODE='42501';
 END IF;
 IF p_allowed AND jsonb_array_length(declared)=0 THEN RAISE EXCEPTION 'This version writes nothing' USING ERRCODE='22023'; END IF;
 UPDATE dashboards SET edits_allowed=CASE WHEN p_allowed THEN declared END WHERE id=p_dashboard;
 RETURN jsonb_build_object('id',p_dashboard,'version',p_version,'editsAllowed',p_allowed);
END $$;

-- As in 017, for a writer that reads the dashboard and may write from it (dashboard_write_scope).
CREATE FUNCTION begin_dashboard_write(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 PERFORM dashboard_write_scope(p_principal,p_dashboard,p_version,p_stars);
 RETURN dashboard_write_allowed(p_dashboard,p_version,p_name);
END $$;

-- As in 017, and the write takes its dashboard's subject.
CREATE FUNCTION record_dashboard_write(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_target jsonb,
  p_summary text,p_before jsonb,p_after jsonb,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb; written uuid:=gen_random_uuid(); subject text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 subject:=dashboard_write_scope(p_principal,p_dashboard,p_version,p_stars);
 declared:=dashboard_write_allowed(p_dashboard,p_version,p_name);
 IF declared->>'kind' NOT IN ('sheet','doc') THEN RAISE EXCEPTION 'Not a source write' USING ERRCODE='22023'; END IF;
 INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,before,after,written_by,subject_id)
 VALUES(written,p_dashboard,p_version,p_name,declared->>'kind',p_target,p_summary,p_before,p_after,p_principal,subject);
 RETURN dashboard_write_json(written);
END $$;

-- As in 017, and the write takes its entry's subject. With scopes on, a log entry is the writer's own person's or the
-- household's, and one it reads.
CREATE FUNCTION write_dashboard_log(p_principal uuid,p_dashboard uuid,p_version int,p_name text,p_entry jsonb,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE declared jsonb; written uuid:=gen_random_uuid();
  record uuid:=gen_random_uuid(); who text; subject_kind text; logged text; day date; entry_title text; dashboard_subject text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Writing from a dashboard');
 dashboard_subject:=dashboard_write_scope(p_principal,p_dashboard,p_version,p_stars);
 declared:=dashboard_write_allowed(p_dashboard,p_version,p_name);
 IF declared->>'kind'<>'log' THEN RAISE EXCEPTION 'Not a log write' USING ERRCODE='22023'; END IF;
 who:=declared->>'subject'; logged:=declared->>'log';
 SELECT kind INTO subject_kind FROM subjects WHERE id=who AND kind IN ('person','household');
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown subject' USING ERRCODE='22023'; END IF;
 IF scopes_on() AND NOT (reads_subject(p_principal,who)
   AND (who='household' OR who IS NOT DISTINCT FROM (SELECT person_id FROM principals WHERE id=p_principal))) THEN
   RAISE EXCEPTION 'This log is someone else''s' USING ERRCODE='42501';
 END IF;
 -- No wider than its dashboard: a private dashboard's log writes are its owner's, also from a version saved before
 -- the dashboard went private (memory.md section 5).
 IF scopes_on() AND dashboard_subject<>'household' AND who IS DISTINCT FROM dashboard_subject THEN
   RAISE EXCEPTION 'A private dashboard''s log writes are its owner''s' USING ERRCODE='22023';
 END IF;
 day:=(p_entry->>'date')::date;
 IF day NOT BETWEEN install_today()-400 AND install_today()+1 THEN
   RAISE EXCEPTION 'A log date is within the last 400 days' USING ERRCODE='22023';
 END IF;
 entry_title:=coalesce(p_entry->>'metric',p_entry->>'check','note');
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,checksum,source_updated_at,status,local_date,timezone,metadata)
 VALUES(record,who,'log',logged||':'||record,'log-entry',CASE WHEN subject_kind='household' THEN 'shared' ELSE 'private' END,
   initcap(replace(logged,'-',' '))||' log · '||entry_title,'',md5(p_entry::text),now(),'current',day,install_timezone(),
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
 INSERT INTO dashboard_writes(id,dashboard_id,version,name,kind,target,summary,before,after,entry,written_by,subject_id)
 VALUES(written,p_dashboard,p_version,p_name,'log',jsonb_build_object('log',logged,'subject',who),
   left(initcap(replace(logged,'-',' '))||' log: '||CASE
     WHEN p_entry ? 'value' THEN (p_entry->>'metric')||' '||(p_entry->>'value')||' '||(p_entry->>'unit')
     WHEN p_entry ? 'check' THEN (p_entry->>'check')||' done'
     ELSE 'note' END||' for '||to_char(day,'Mon FMDD'),300),
   NULL,p_entry,record,p_principal,who);
 RETURN dashboard_write_json(written);
END $$;

-- As in 017, for a reader of the write (its dashboard and its own subject) under its dashboard's author rule.
CREATE FUNCTION undo_dashboard_write(p_principal uuid,p_write uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE w dashboard_writes; saved dashboards;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Undoing a dashboard write');
 SELECT * INTO w FROM dashboard_writes WHERE id=p_write FOR UPDATE;
 IF NOT FOUND OR NOT dashboard_readable(p_principal,w.dashboard_id,p_stars) OR NOT reads_subject(p_principal,w.subject_id) THEN
   RAISE EXCEPTION 'Unknown write' USING ERRCODE='P0002';
 END IF;
 SELECT * INTO saved FROM dashboards WHERE id=w.dashboard_id;
 IF NOT may_change(p_principal,saved.subject_id,saved.created_by) THEN
   RAISE EXCEPTION 'Only its owner, the person who made it or an admin undoes a write from this dashboard' USING ERRCODE='42501';
 END IF;
 IF w.undone_at IS NOT NULL THEN RAISE EXCEPTION 'Already undone' USING ERRCODE='22023'; END IF;
 IF w.kind='log' THEN UPDATE resources SET status='deleted',indexed_at=now() WHERE id=w.entry AND provider='log'; END IF;
 UPDATE dashboard_writes SET undone_at=now(),undone_by=p_principal WHERE id=p_write;
 RETURN dashboard_write_json(p_write);
END $$;

-- As in 017, the writes the reader reads: those of dashboards it reads, each of a subject it reads, counted after that
-- filter; one it can't read null, as an unknown one.
CREATE FUNCTION read_dashboard_writes(p_principal uuid,p_dashboard uuid,p_write uuid,p_limit int,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading dashboard writes');
 RETURN (WITH seen AS MATERIALIZED (SELECT x.id FROM dashboards x WHERE dashboard_readable(p_principal,x.id,p_stars)),
   subjects_read AS MATERIALIZED (SELECT s.id FROM subjects s WHERE reads_subject(p_principal,s.id))
   SELECT CASE WHEN p_write IS NOT NULL THEN (SELECT dashboard_write_json(w.id) FROM dashboard_writes w
       WHERE w.id=p_write AND w.dashboard_id IN (SELECT id FROM seen) AND w.subject_id IN (SELECT id FROM subjects_read))
     ELSE (SELECT coalesce(jsonb_agg(dashboard_write_json(w.id) ORDER BY w.written_at DESC,w.id),'[]'::jsonb) FROM (
       SELECT id,written_at FROM dashboard_writes WHERE (p_dashboard IS NULL OR dashboard_id=p_dashboard)
         AND dashboard_id IN (SELECT id FROM seen) AND subject_id IN (SELECT id FROM subjects_read)
       ORDER BY written_at DESC,id LIMIT least(greatest(coalesce(p_limit,50),1),200)) w) END);
END $$;

-- As in 018, for a dashboard the setter reads; with scopes on a corner (the household's) shows a Family dashboard only,
-- never one narrower than the card. Clearing a corner is a reader's of its subject.
CREATE FUNCTION set_ambient_card(p_principal uuid,p_corner text,p_dashboard uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Choosing corner cards');
 IF p_corner NOT IN ('main','recovery','training','changed') THEN RAISE EXCEPTION 'Unknown corner' USING ERRCODE='22023'; END IF;
 IF p_dashboard IS NULL THEN DELETE FROM ambient_cards WHERE corner=p_corner AND reads_subject(p_principal,subject_id);
 ELSE
   IF NOT dashboard_readable(p_principal,p_dashboard,p_stars) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
   IF scopes_on() AND (SELECT subject_id FROM dashboards WHERE id=p_dashboard)<>'household' THEN
     RAISE EXCEPTION 'A corner shows a Family dashboard' USING ERRCODE='22023';
   END IF;
   INSERT INTO ambient_cards(corner,dashboard_id,set_by) VALUES(p_corner,p_dashboard,p_principal)
   ON CONFLICT(corner) DO UPDATE SET dashboard_id=excluded.dashboard_id,set_by=excluded.set_by,set_at=now()
     WHERE reads_subject(p_principal,ambient_cards.subject_id);
 END IF;
 RETURN read_ambient_cards(p_principal,p_stars);
END $$;
-- The corners the reader reads (its subject's), each showing a dashboard it reads.
CREATE FUNCTION read_ambient_cards(p_principal uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading corner cards');
 RETURN (SELECT coalesce(jsonb_object_agg(corner,dashboard_id),'{}'::jsonb) FROM ambient_cards
   WHERE reads_subject(p_principal,subject_id) AND (NOT scopes_on() OR dashboard_readable(p_principal,dashboard_id,p_stars)));
END $$;

-- As in 017, for a reader of the dashboard under the author rule; the writes counted are those it reads.
CREATE FUNCTION delete_dashboard(p_principal uuid,p_dashboard uuid,p_title text,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE named text; versions int; writes int; cards int; saved dashboards;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Deleting dashboards');
 SELECT * INTO saved FROM dashboards WHERE id=p_dashboard FOR UPDATE;
 IF NOT FOUND OR NOT dashboard_readable(p_principal,p_dashboard,p_stars) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
 IF NOT may_change(p_principal,saved.subject_id,saved.created_by) THEN
   RAISE EXCEPTION 'Only its owner, the person who made it or an admin deletes this dashboard' USING ERRCODE='42501';
 END IF;
 SELECT title INTO named FROM dashboard_versions WHERE dashboard_id=p_dashboard ORDER BY version DESC LIMIT 1;
 IF named IS DISTINCT FROM p_title THEN RAISE EXCEPTION 'The title does not match the dashboard' USING ERRCODE='22023'; END IF;
 SELECT count(*) INTO versions FROM dashboard_versions WHERE dashboard_id=p_dashboard;
 SELECT count(*) INTO writes FROM dashboard_writes WHERE dashboard_id=p_dashboard AND reads_subject(p_principal,subject_id);
 SELECT count(*) INTO cards FROM ambient_cards WHERE dashboard_id=p_dashboard;
 DELETE FROM dashboards WHERE id=p_dashboard;
 RETURN jsonb_build_object('id',p_dashboard,'title',named,'versions',versions,'writes',writes,'corners',cards);
END $$;

-- As in 017. With scopes on a pin names a star its pinner reads now and takes that star's subject (p_cited, the star's
-- {subject, provider} as the engine passes it), at most 24 among the pins the pinner reads; unpinning removes the pins
-- of that star the pinner reads. With scopes off every pin is the household's, as before.
CREATE FUNCTION set_sky_pin(p_principal uuid,p_star text,p_pinned boolean,p_stars text[] DEFAULT NULL,p_cited jsonb DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE subject text:='household';
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Pinning stars');
 IF p_star IS NULL OR length(p_star) NOT BETWEEN 1 AND 1000 THEN RAISE EXCEPTION 'A pin names a star' USING ERRCODE='22023'; END IF;
 IF NOT p_pinned THEN DELETE FROM sky_pins WHERE graph_id=p_star AND reads_subject(p_principal,subject_id)
   AND (NOT scopes_on() OR p_star=ANY(coalesce(p_stars,'{}')));
 ELSE
   IF scopes_on() THEN
     IF NOT p_star=ANY(coalesce(p_stars,'{}')) THEN RAISE EXCEPTION 'A pin names a star on the map' USING ERRCODE='22023'; END IF;
     subject:=derived_subject(p_principal,p_cited,NULL);
   END IF;
   IF NOT EXISTS(SELECT 1 FROM sky_pins WHERE graph_id=p_star AND subject_id=subject) THEN
     -- Counted over the pins read_sky_pins serves this caller, each star once, never over pins it can't read.
     IF (SELECT count(DISTINCT graph_id) FROM sky_pins WHERE reads_subject(p_principal,subject_id)
         AND (NOT scopes_on() OR graph_id=ANY(coalesce(p_stars,'{}'))))>=24 THEN
       RAISE EXCEPTION 'At most 24 stars are pinned' USING ERRCODE='22023';
     END IF;
     INSERT INTO sky_pins(subject_id,graph_id,pinned_by) VALUES(subject,p_star,p_principal);
   END IF;
 END IF;
 RETURN read_sky_pins(p_principal,p_stars);
END $$;
-- The pinned stars the reader reads (a pin's subject, and with scopes on its star now), oldest first, each once.
CREATE FUNCTION read_sky_pins(p_principal uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading pins');
 RETURN (SELECT coalesce(jsonb_agg(p.graph_id ORDER BY p.pinned_at,p.graph_id),'[]'::jsonb) FROM (
   SELECT graph_id,min(pinned_at) AS pinned_at FROM sky_pins WHERE reads_subject(p_principal,subject_id)
     AND (NOT scopes_on() OR graph_id=ANY(coalesce(p_stars,'{}'))) GROUP BY graph_id ORDER BY 2,1 LIMIT 24) p);
END $$;

-- As in 017. With scopes on an insight lands in the narrowest subject of what it draws on (p_insight->'cited', which
-- the engine passes: its stars' and its sources'), no wider than one it supersedes (which its saver must read); one
-- citing no star lands in its saver's own scope, the household's for a saver with no person; and what its sources named
-- (p_insight->'checked', which the engine passes) is kept for the read-time check. With scopes off it is the
-- household's, nothing is kept for its sources, and superseding refuses nothing it did not refuse before.
CREATE FUNCTION save_insight(p_principal uuid,p_insight jsonb,p_stars text[] DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid:=gen_random_uuid(); replaced uuid:=(p_insight->>'supersedes')::uuid; earlier insights; landed text; cited jsonb;
  checked text[]; scoped boolean:=scopes_on();
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving insights');
 -- A saver cites only stars it reads now (the engine drops the others first).
 IF NOT stars_readable((SELECT array_agg(n->>'graphId') FROM jsonb_array_elements(
     CASE WHEN jsonb_typeof(p_insight->'stars')='array' THEN p_insight->'stars' ELSE '[]'::jsonb END) n),p_stars) THEN
   RAISE EXCEPTION 'An insight cites stars its saver reads' USING ERRCODE='22023';
 END IF;
 -- Kept only with scopes on and as the engine passes it; without it every locator is checked at each read.
 IF scoped AND jsonb_typeof(p_insight->'checked')='array' THEN
   checked:=coalesce((SELECT array_agg(c) FROM jsonb_array_elements_text(p_insight->'checked') c),'{}');
 END IF;
 IF replaced IS NOT NULL THEN
   SELECT * INTO earlier FROM insights WHERE id=replaced FOR UPDATE;
   IF NOT FOUND OR NOT insight_readable(p_principal,earlier.subject_id,earlier.stars,earlier.sources,earlier.sources_checked,p_stars) THEN
     RAISE EXCEPTION 'Unknown superseded insight' USING ERRCODE='22023';
   END IF;
   UPDATE insights SET status='superseded',updated_at=now() WHERE id=replaced;
 END IF;
 cited:=CASE WHEN jsonb_typeof(p_insight->'cited')='array' THEN p_insight->'cited' ELSE '[]'::jsonb END;
 IF coalesce(jsonb_array_length(CASE WHEN jsonb_typeof(p_insight->'stars')='array' THEN p_insight->'stars' END),0)=0 THEN
   cited:=cited||jsonb_build_array(jsonb_build_object('subject',coalesce((SELECT person_id FROM principals WHERE id=p_principal),'household')));
 END IF;
 landed:=derived_subject(p_principal,cited,earlier.subject_id);
 -- Superseding keeps the earlier insight's subject unless its own author's person takes it private (memory.md section
 -- 5), as a dashboard's next version does: no one else marks a Family insight superseded by a private one. With scopes
 -- off every new insight is the household's, superseding a private one (saved while scopes were on) included.
 IF scoped AND replaced IS NOT NULL AND landed<>earlier.subject_id AND NOT coalesce((SELECT a.person_id=landed AND c.person_id=landed
     FROM principals a,principals c WHERE a.id=earlier.saved_by AND c.id=p_principal),false) THEN
   RAISE EXCEPTION 'This insight would replace a Family insight with a private one, which only the person who saved it does; save it without supersedes' USING ERRCODE='22023';
 END IF;
 INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,
   generated_by,generated_at,sources_as_of,supersedes,saved_by,subject_id,scope,sources_checked)
 VALUES(saved,p_insight->>'kind',p_insight->>'title',p_insight->>'summary',p_insight->>'whyItMatters',p_insight->>'nextStep',
   p_insight->'points',p_insight->>'evidenceLevel',p_insight->>'limitations',p_insight->'stars',p_insight->'sources',
   p_insight->>'generatedBy',(p_insight->>'generatedAt')::timestamptz,(p_insight->>'sourcesAsOf')::timestamptz,replaced,p_principal,
   landed,CASE WHEN landed='household' THEN 'shared' ELSE 'private' END,checked);
 RETURN saved;
END $$;

-- As in 017, on an insight the caller reads; any other answers as an unknown one (null).
CREATE FUNCTION insight_feedback(p_principal uuid,p_id uuid,p_value text,p_until timestamptz,p_note text,p_stars text[] DEFAULT NULL)
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
 WHERE id=p_id AND insight_readable(p_principal,subject_id,stars,sources,sources_checked,p_stars) RETURNING * INTO updated;
 RETURN CASE WHEN updated.id IS NULL THEN NULL ELSE insight_json(updated) END;
END $$;

-- As in 017, the insights the reader reads, counted after that filter.
CREATE FUNCTION read_insights(p_principal uuid,p_status text,p_since timestamptz,p_limit int,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insights');
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50 THEN RAISE EXCEPTION 'Invalid insight limit' USING ERRCODE='22023'; END IF;
 RETURN (SELECT coalesce(jsonb_agg(insight_json(i) ORDER BY i.created_at DESC,i.id),'[]'::jsonb) FROM insights i
   WHERE i.id IN (SELECT x.id FROM insights x WHERE (p_status IS NULL OR x.status=p_status) AND (p_since IS NULL OR x.created_at>=p_since)
     AND insight_readable(p_principal,x.subject_id,x.stars,x.sources,x.sources_checked,p_stars) ORDER BY x.created_at DESC,x.id LIMIT p_limit));
END $$;

-- As in 017. A run takes its insight's subject (an insight its writer reads), or with none, with scopes on, its writer's
-- own scope (the household's for a writer with no person); with scopes off the household's.
CREATE FUNCTION log_insight_run(p_principal uuid,p_generated_by text,p_outcome text,p_note text,p_insight uuid,p_stars text[] DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE logged uuid:=gen_random_uuid(); landed text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Logging insight runs');
 IF p_insight IS NOT NULL THEN
   SELECT i.subject_id INTO landed FROM insights i WHERE i.id=p_insight AND insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars);
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown insight' USING ERRCODE='22023'; END IF;
 ELSE
   landed:=derived_subject(p_principal,jsonb_build_array(jsonb_build_object('subject',
     coalesce((SELECT person_id FROM principals WHERE id=p_principal),'household'))),NULL);
 END IF;
 INSERT INTO insight_runs(id,generated_by,outcome,note,insight_id,subject_id,scope)
 VALUES(logged,p_generated_by,p_outcome,btrim(p_note,E' \t\r\n'),p_insight,landed,CASE WHEN landed='household' THEN 'shared' ELSE 'private' END);
 RETURN logged;
END $$;

-- As in 017, the runs the reader reads: of its subjects, and of an insight it reads.
CREATE FUNCTION read_insight_runs(p_principal uuid,p_limit int,p_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insight runs');
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 14 THEN RAISE EXCEPTION 'Invalid run limit' USING ERRCODE='22023'; END IF;
 RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',r.id,'generatedBy',r.generated_by,'outcome',r.outcome,'note',r.note,
     'insightId',r.insight_id,'at',r.created_at) ORDER BY r.created_at DESC,r.id),'[]'::jsonb)
   FROM (SELECT * FROM insight_runs x WHERE reads_subject(p_principal,x.subject_id)
     AND (x.insight_id IS NULL OR NOT scopes_on() OR EXISTS(SELECT 1 FROM insights i WHERE i.id=x.insight_id
       AND insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars)))
     ORDER BY x.created_at DESC,x.id LIMIT p_limit) r);
END $$;

-- Live screens (docs/dev/screens.md, the screens program's; memory.md 7.5, SB-P1-15, agreed with the screens program on
-- the coordination board, messages 234 and 237). What a screen shows, and what it reports about a command, is the view
-- of the app login it listens with: the household view's (the shared app token's) or a viewer session's (an app login
-- bound to its person, SB-P1-11). With memory.scopes on another principal is told it only as far as it reads it
-- (read_screens, read_screen_command), and the engine checks what a command carries to another principal's screen
-- (ScreenCommands.Resolve, with reads_all_of). shown_by is the login that last reported what a screen shows (a listen
-- or a report that gives it, or the listen that made the screen), reported_by the one that reported a command's
-- outcome. Both are null on every row this migration finds, whose query and outcomes with scopes on reach no one until
-- its screen reports again (each listen does). With scopes off neither is read, and every answer is 020's.
ALTER TABLE screens ADD COLUMN shown_by uuid REFERENCES principals(id) ON DELETE SET NULL;
ALTER TABLE screen_commands ADD COLUMN reported_by uuid REFERENCES principals(id) ON DELETE SET NULL;

-- Whether p_reader reads all that p_other reads, as far as who they are tells (a screen's login, a command's sender):
-- with scopes off, always. With scopes on, all that a harness or app login with no person reads (the household view's:
-- Family, the shares Family reads and DEMO rows), every reader of Family does; all that one bound to a person reads, a
-- login bound to the same person that reads their scope now; of any other login, or of none known, no one.
CREATE FUNCTION reads_all_of(p_reader uuid,p_other uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT NOT scopes_on() OR coalesce((SELECT CASE WHEN o.person_id IS NULL THEN reads_subject(p_reader,'household')
     ELSE r.person_id=o.person_id AND reads_subject(p_reader,o.person_id) END
   FROM principals o,principals r WHERE o.id=p_other AND o.role IN ('harness','app') AND r.id=p_reader),false)
$$;

-- Whether a reader reads the dashboard a screen names (its id as the screen reported it), as list_dashboards serves it.
CREATE FUNCTION screen_dashboard_readable(p_id text,p_reader uuid,p_stars text[]) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_id ~* '^[0-9a-f]{8}-([0-9a-f]{4}-){3}[0-9a-f]{12}$' THEN dashboard_readable(p_reader,p_id::uuid,p_stars) ELSE false END
$$;

-- What a screen shows, as told to one reader with scopes on: only what that reader reads now, as if nothing else were
-- open, so a record that has left the reader's scopes since the screen reported it is not told either (memory.md 6.2).
-- The star open (one of p_stars, the stars the reader reads, which the engine passes), the dashboard open and the Home
-- dashboard (each as list_dashboards serves it to the reader), each only when the reader reads it, a Home deleted since
-- (missing) to a reader that reads all the screen's login reads; the parts only at Home (Home's own, its map and
-- corners) or with the dashboard they are of, since a map part's focus names stars; and the query, which is what
-- someone typed, only to a reader that reads all the screen's login reads (p_whole, reads_all_of; memory.md 7.5). The
-- view, layout, tiles, narrow and activeAt name no record and stay. Any other key a screen reported (the engine takes any
-- object as a showing, and the app may add keys) reaches only a reader that reads all the screen's login reads: to the
-- rest a showing is these keys alone, so a key added later stays with its login until it is checked here.
CREATE FUNCTION screen_showing_for(p_showing jsonb,p_reader uuid,p_stars text[],p_whole boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_whole THEN p_showing-'query'-'star'-'dashboard'-'home'-'parts'
     ELSE (SELECT coalesce(jsonb_object_agg(e.key,e.value),'{}'::jsonb) FROM jsonb_each(p_showing) e
       WHERE e.key IN ('view','layout','tiles','narrow','activeAt')) END
   || CASE WHEN p_whole AND p_showing ? 'query' THEN jsonb_build_object('query',p_showing->'query') ELSE '{}'::jsonb END
   || CASE WHEN jsonb_typeof(p_showing->'star')='object' AND p_showing->'star'->>'id'=ANY(coalesce(p_stars,'{}'))
     THEN jsonb_build_object('star',p_showing->'star') ELSE '{}'::jsonb END
   || CASE WHEN x.dashboard THEN jsonb_build_object('dashboard',p_showing->'dashboard') ELSE '{}'::jsonb END
   || CASE WHEN jsonb_typeof(p_showing->'home')='null' OR p_whole AND p_showing->'home'->'missing'='true'::jsonb
       OR screen_dashboard_readable(p_showing->'home'->'dashboard'->>'id',p_reader,p_stars)
     THEN jsonb_build_object('home',p_showing->'home') ELSE '{}'::jsonb END
   || CASE WHEN p_showing ? 'parts' AND (p_showing->>'view'='home' AND NOT p_showing ? 'dashboard' OR x.dashboard OR p_showing->'parts'='[]'::jsonb)
     THEN jsonb_build_object('parts',p_showing->'parts') ELSE '{}'::jsonb END
 FROM (SELECT screen_dashboard_readable(p_showing->'dashboard'->>'id',p_reader,p_stars) AS dashboard) x
$$;

-- As in 020, and the login that listens is the one whose view a showing it gives is (shown_by).
CREATE OR REPLACE FUNCTION listen_screen(p_principal uuid,p_screen text,p_name text,p_showing jsonb,p_settings jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Listening for commands');
 IF p_name IS NULL THEN RAISE EXCEPTION 'A screen listens with its name' USING ERRCODE='22023'; END IF;
 PERFORM screen_input(p_screen,p_name,p_showing,p_settings);
 UPDATE screens SET name=btrim(p_name),seen_at=now(),showing=coalesce(p_showing,showing),settings=coalesce(p_settings,settings),
   shown_by=CASE WHEN p_showing IS NULL THEN shown_by ELSE p_principal END
 WHERE id=p_screen;
 IF NOT FOUND THEN
   PERFORM pg_advisory_xact_lock(hashtext('screens'));
   DELETE FROM screens WHERE id IN (SELECT id FROM screens WHERE id<>p_screen ORDER BY seen_at,id
     LIMIT greatest((SELECT count(*) FROM screens WHERE id<>p_screen)-99,0));
   INSERT INTO screens(id,name,showing,settings,shown_by) VALUES(p_screen,btrim(p_name),coalesce(p_showing,'{}'),coalesce(p_settings,'{}'),p_principal)
   ON CONFLICT(id) DO UPDATE SET name=excluded.name,seen_at=now(),showing=coalesce(p_showing,screens.showing),
     settings=coalesce(p_settings,screens.settings),shown_by=CASE WHEN p_showing IS NULL THEN screens.shown_by ELSE excluded.shown_by END;
 END IF;
 RETURN claim_screen_command(p_screen);
END $$;

-- As in 020, and the reporting login is the one whose view the outcome's detail is (reported_by), and a showing it
-- gives (shown_by).
CREATE OR REPLACE FUNCTION done_screen_command(p_principal uuid,p_screen text,p_command uuid,p_result text,p_detail jsonb,p_showing jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Reporting on commands');
 PERFORM screen_input(p_screen,NULL,p_showing,NULL);
 IF p_result IS NULL OR p_result NOT IN ('shown','failed') THEN RAISE EXCEPTION 'A result is shown or failed' USING ERRCODE='22023'; END IF;
 IF p_detail IS NOT NULL AND (jsonb_typeof(p_detail)<>'object' OR length(p_detail::text)>4000) THEN
   RAISE EXCEPTION 'A detail is an object of at most 4000 characters' USING ERRCODE='22023';
 END IF;
 -- The screen's row first, as a send does, so the two never wait on each other.
 UPDATE screens SET showing=coalesce(p_showing,showing),shown_by=CASE WHEN p_showing IS NULL THEN shown_by ELSE p_principal END
 WHERE id=p_screen;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 UPDATE screen_commands SET result=p_result,done_at=clock_timestamp(),detail=coalesce(p_detail,'{}'),reported_by=p_principal
 WHERE id=p_command AND screen_id=p_screen AND result='delivered';
 IF NOT FOUND THEN
   IF EXISTS(SELECT 1 FROM screen_commands WHERE id=p_command AND screen_id=p_screen) THEN
     RAISE EXCEPTION 'That command is not waiting for an outcome' USING ERRCODE='22023';
   END IF;
   RAISE EXCEPTION 'Unknown command' USING ERRCODE='P0002';
 END IF;
 RETURN jsonb_build_object('ok',true);
END $$;

-- As in 020. With scopes on, an outcome reported by a login whose view the reader doesn't read all of (reads_all_of)
-- says only that: what the screen found or opened names what that screen reads, not what the reader does.
CREATE OR REPLACE FUNCTION read_screen_command(p_principal uuid,p_command uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE c screen_commands;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app','harness'],'Reading commands');
 UPDATE screen_commands SET result='expired' WHERE id=p_command AND result='pending' AND expires_at<=now();
 SELECT * INTO c FROM screen_commands WHERE id=p_command;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF c.result IN ('shown','failed') AND NOT reads_all_of(p_principal,c.reported_by) THEN
   c.detail:=jsonb_build_object('summary','What that screen reported is not passed on: it reads what you can''t');
 END IF;
 RETURN screen_command_json(c);
END $$;

-- As in 020, each screen's showing as its reader is told it: with scopes on, only what that reader reads now, the query
-- only when it reads all the screen's login reads (screen_showing_for, with p_stars, the stars it reads now, which the
-- engine passes). The old one goes, so a call with its arguments resolves to this one, p_stars null.
DROP FUNCTION read_screens(uuid);
CREATE FUNCTION read_screens(p_principal uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app','harness'],'Reading screens');
 RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'name',name,'open',seen_at>now()-interval '60 seconds',
   'seenAt',seen_at,'showing',CASE WHEN NOT scopes_on() THEN showing
     ELSE screen_showing_for(showing,p_principal,p_stars,reads_all_of(p_principal,shown_by)) END)
   ORDER BY seen_at DESC,id),'[]'::jsonb)
   FROM screens WHERE seen_at>now()-interval '24 hours');
END $$;

DO $$
DECLARE f text;
BEGIN
 FOREACH f IN ARRAY ARRAY['scopes_on()','reads_subject(uuid,text)','stars_readable(text[],text[])','derived_subject(uuid,jsonb,text)',
   'may_change(uuid,text,uuid)','insight_locators(jsonb)','insight_readable(uuid,text,jsonb,jsonb,text[],text[])','dashboard_version_stars(uuid,int)',
   'dashboard_version_readable(uuid,uuid,int,text[])','dashboard_readable(uuid,uuid,text[])','dashboard_cites(jsonb,jsonb,jsonb)',
   'dashboard_cards_shown(jsonb,uuid,text[])','dashboard_write_scope(uuid,uuid,int,text[])',
   'dashboard_json(dashboards,dashboard_versions,boolean,uuid,text[])','read_dashboards(uuid,uuid,int,boolean,text[])',
   'save_dashboard(uuid,uuid,int,jsonb,text[])','save_dashboard_layout(uuid,uuid,int,int,jsonb,text,text[],jsonb)',
   'allow_dashboard_edits(uuid,uuid,int,boolean,text[])','begin_dashboard_write(uuid,uuid,int,text,text[])',
   'record_dashboard_write(uuid,uuid,int,text,jsonb,text,jsonb,jsonb,text[])','write_dashboard_log(uuid,uuid,int,text,jsonb,text[])',
   'undo_dashboard_write(uuid,uuid,text[])','read_dashboard_writes(uuid,uuid,uuid,int,text[])','set_ambient_card(uuid,text,uuid,text[])',
   'read_ambient_cards(uuid,text[])','delete_dashboard(uuid,uuid,text,text[])','set_sky_pin(uuid,text,boolean,text[],jsonb)',
   'read_sky_pins(uuid,text[])','save_insight(uuid,jsonb,text[])','insight_feedback(uuid,uuid,text,timestamptz,text,text[])',
   'read_insights(uuid,text,timestamptz,int,text[])','log_insight_run(uuid,text,text,text,uuid,text[])','read_insight_runs(uuid,int,text[])',
   'reads_all_of(uuid,uuid)','screen_dashboard_readable(text,uuid,text[])','screen_showing_for(jsonb,uuid,text[],boolean)',
   'listen_screen(uuid,text,text,jsonb,jsonb)','done_screen_command(uuid,text,uuid,text,jsonb,jsonb)','read_screen_command(uuid,uuid)',
   'read_screens(uuid,text[])']
 LOOP
   EXECUTE format('ALTER FUNCTION %s SET search_path TO pg_catalog,%I,pg_temp',f,current_schema());
   EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
