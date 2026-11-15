-- Corner cards per person (SB-P1-20; docs/dev/memory.md section 5, Where writes land). With memory.scopes on, a corner
-- a viewer session chooses is its person's own, kept by subject and corner, and shows a Family dashboard or one of that
-- person's; the household view's corners stay the household's and show a Family dashboard only, as in 027. Each reader
-- is shown, corner by corner, its own person's choice where that person made one, else the household's, so a person's
-- Home keeps the household's cards until they choose their own. A person's choice may be the stat card (no dashboard),
-- which keeps that corner empty on their Home whatever the household's shows.
--
-- With scopes off every corner is the household's, as before: a choice is the household's and only the household's are
-- read, so every answer is 027's. Nothing writes a person's corner while app.viewers is off, every install's setting and
-- the only one this version accepts: only an app login bound to a person, a viewer session, chooses for a person, and
-- the door admits none then. Its diff went to the screens program on the board first. The number is provisional.
ALTER TABLE ambient_cards DROP CONSTRAINT ambient_cards_pkey, ADD PRIMARY KEY(subject_id,corner);
ALTER TABLE ambient_cards ALTER COLUMN dashboard_id DROP NOT NULL,
  ADD CONSTRAINT ambient_cards_household_card CHECK(dashboard_id IS NOT NULL OR subject_id<>'household');

-- The corners as one principal has them, before any dashboard is checked: with scopes on, for each corner its person's
-- own choice, else the household's; with scopes off, the household's alone. Only subjects the principal reads.
CREATE FUNCTION ambient_cards_of(p_principal uuid) RETURNS TABLE(corner text,dashboard_id uuid) LANGUAGE sql STABLE AS $$
 SELECT DISTINCT ON (c.corner) c.corner,c.dashboard_id FROM ambient_cards c
 WHERE reads_subject(p_principal,c.subject_id) AND (c.subject_id='household'
   OR scopes_on() AND c.subject_id=(SELECT person_id FROM principals WHERE id=p_principal))
 ORDER BY c.corner,c.subject_id='household'
$$;

-- As in 027, choosing for the setter's own corners: with scopes on, a login bound to a person (a viewer session) chooses
-- that person's, showing a dashboard of Family or of that person, and the stat card (null) is kept as their choice; any
-- other login chooses the household's, showing a Family dashboard only, and null puts the household's stat card back.
-- With scopes off every choice is the household's, as before.
CREATE OR REPLACE FUNCTION set_ambient_card(p_principal uuid,p_corner text,p_dashboard uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE mine text:='household';
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Choosing corner cards');
 IF p_corner NOT IN ('main','recovery','training','changed') THEN RAISE EXCEPTION 'Unknown corner' USING ERRCODE='22023'; END IF;
 IF scopes_on() THEN mine:=coalesce((SELECT person_id FROM principals WHERE id=p_principal),'household'); END IF;
 IF p_dashboard IS NULL AND mine='household' THEN
   DELETE FROM ambient_cards WHERE corner=p_corner AND subject_id='household' AND reads_subject(p_principal,subject_id);
 ELSE
   IF p_dashboard IS NOT NULL AND NOT dashboard_readable(p_principal,p_dashboard,p_stars) THEN
     RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002';
   END IF;
   IF p_dashboard IS NOT NULL AND scopes_on() AND (SELECT subject_id FROM dashboards WHERE id=p_dashboard) NOT IN ('household',mine) THEN
     RAISE EXCEPTION 'A corner shows a Family dashboard, or one of its own person''s' USING ERRCODE='22023';
   END IF;
   INSERT INTO ambient_cards(subject_id,corner,dashboard_id,set_by) VALUES(mine,p_corner,p_dashboard,p_principal)
   ON CONFLICT(subject_id,corner) DO UPDATE SET dashboard_id=excluded.dashboard_id,set_by=excluded.set_by,set_at=now()
     WHERE reads_subject(p_principal,ambient_cards.subject_id);
 END IF;
 RETURN read_ambient_cards(p_principal,p_stars);
END $$;

-- As in 027: the corners the reader has (ambient_cards_of), each showing a dashboard it reads now.
CREATE OR REPLACE FUNCTION read_ambient_cards(p_principal uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading corner cards');
 RETURN (SELECT coalesce(jsonb_object_agg(s.corner,s.dashboard_id),'{}'::jsonb) FROM ambient_cards_of(p_principal) s
   WHERE s.dashboard_id IS NOT NULL AND (NOT scopes_on() OR dashboard_readable(p_principal,s.dashboard_id,p_stars)));
END $$;

-- As in 027, but a dashboard's corners are those it shows in for this reader (ambient_cards_of), so another person's
-- choice is never told and a household corner the reader's own choice covers is not listed.
CREATE OR REPLACE FUNCTION dashboard_json(d dashboards,v dashboard_versions,p_code boolean,p_principal uuid,p_stars text[]) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',d.id,'version',v.version,'latest',(SELECT max(version) FROM dashboard_versions WHERE dashboard_id=d.id),
   'title',v.title,'description',v.description,'stars',v.stars,'note',v.note,'restoredFrom',v.restored_from,
   'savedAt',v.created_at,'createdAt',d.created_at,'updatedAt',d.updated_at,'writes',v.writes,
   'editsAllowed',jsonb_array_length(v.writes)>0 AND d.edits_allowed IS NOT NULL AND d.edits_allowed=v.writes,
   'corners',(SELECT coalesce(jsonb_agg(c.corner ORDER BY c.corner),'[]'::jsonb) FROM ambient_cards_of(p_principal) c
     WHERE c.dashboard_id=d.id),
   'versions',(SELECT jsonb_agg(jsonb_build_object('version',h.version,'note',h.note,'restoredFrom',h.restored_from,'savedAt',h.created_at)
     ORDER BY h.version DESC) FROM (SELECT * FROM dashboard_versions x WHERE x.dashboard_id=d.id
       AND (NOT scopes_on() OR dashboard_version_readable(p_principal,d.id,x.version,p_stars)) ORDER BY x.version DESC LIMIT 100) h),
   'layout',dashboard_cards_shown(coalesce(l.layout,v.layout),p_principal,p_stars),'layoutRevision',coalesce(l.revision,0),
   'layoutNote',l.note,'layoutSavedAt',l.saved_at)
   || CASE WHEN p_code THEN jsonb_build_object('feeds',v.feeds,'html',v.html) ELSE '{}'::jsonb END
 FROM (SELECT 1) one LEFT JOIN dashboard_layouts l ON l.dashboard_id=d.id AND l.version=v.version
$$;

-- As in 027, but the corners counted are those of subjects the deleter reads: another person's choice is never told.
CREATE OR REPLACE FUNCTION delete_dashboard(p_principal uuid,p_dashboard uuid,p_title text,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
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
 SELECT count(*) INTO cards FROM ambient_cards WHERE dashboard_id=p_dashboard AND reads_subject(p_principal,subject_id);
 DELETE FROM dashboards WHERE id=p_dashboard;
 RETURN jsonb_build_object('id',p_dashboard,'title',named,'versions',versions,'writes',writes,'corners',cards);
END $$;

DO $$
DECLARE f text;
BEGIN
 FOREACH f IN ARRAY ARRAY['ambient_cards_of(uuid)','set_ambient_card(uuid,text,uuid,text[])','read_ambient_cards(uuid,text[])',
   'dashboard_json(dashboards,dashboard_versions,boolean,uuid,text[])','delete_dashboard(uuid,uuid,text,text[])']
 LOOP
   EXECUTE format('ALTER FUNCTION %s SET search_path TO pg_catalog,%I,pg_temp',f,current_schema());
   EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
