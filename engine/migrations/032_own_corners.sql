-- A person's own corner choice can be dropped again (SB-P1-20's open point, decisions.md D-139; docs/dev/screens.md 6,
-- Undo). With memory.scopes on, a viewer session's corner choice is its person's own (030), and nothing but the
-- dashboard cascade removed it, so a screen's Undo of corner_card on a session's screen left a person row behind where
-- the corner had followed the household's. Two changes, both with 030's signatures:
--
-- set_ambient_card takes the nil uuid as "no own choice": from a login bound to a person, with scopes on, it removes
-- that person's choice in the corner, which then shows the household's again (dropping one never made changes nothing).
-- read_ambient_cards tells such a reader, beside the corners, which of them are its own person's choice: `own`, the
-- corners its person has a row for, the stat card included, in corner order. The app's Undo saves a corner that was not
-- its own as "no own choice" and puts that back with the nil uuid.
--
-- From any other login, and with scopes off, the nil uuid is what it was: no dashboard has it, so it is refused as an
-- unknown one (P0002), and no answer carries `own`: with scopes off every corner is the household's, and the household
-- view's corners are all its own. Nothing reaches the new path while app.viewers is off, every install's setting and
-- the only one this version accepts: only a viewer session is an app login bound to a person, and the door admits none
-- then. Its diff went to the screens program on the board first. The number is provisional.

-- As in 030, and the nil uuid from a login bound to a person, with scopes on, drops that person's choice in the corner.
CREATE OR REPLACE FUNCTION set_ambient_card(p_principal uuid,p_corner text,p_dashboard uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE mine text:='household';
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Choosing corner cards');
 IF p_corner NOT IN ('main','recovery','training','changed') THEN RAISE EXCEPTION 'Unknown corner' USING ERRCODE='22023'; END IF;
 IF scopes_on() THEN mine:=coalesce((SELECT person_id FROM principals WHERE id=p_principal),'household'); END IF;
 IF p_dashboard='00000000-0000-0000-0000-000000000000'::uuid AND mine<>'household' THEN
   DELETE FROM ambient_cards WHERE corner=p_corner AND subject_id=mine AND reads_subject(p_principal,subject_id);
 ELSIF p_dashboard IS NULL AND mine='household' THEN
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

-- As in 030, and for a reader bound to a person, with scopes on, `own`: the corners that are its person's own choice.
CREATE OR REPLACE FUNCTION read_ambient_cards(p_principal uuid,p_stars text[] DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
DECLARE cards jsonb; person text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading corner cards');
 cards:=(SELECT coalesce(jsonb_object_agg(s.corner,s.dashboard_id),'{}'::jsonb) FROM ambient_cards_of(p_principal) s
   WHERE s.dashboard_id IS NOT NULL AND (NOT scopes_on() OR dashboard_readable(p_principal,s.dashboard_id,p_stars)));
 IF NOT scopes_on() THEN RETURN cards; END IF;
 SELECT person_id INTO person FROM principals WHERE id=p_principal;
 IF person IS NULL THEN RETURN cards; END IF;
 RETURN cards || jsonb_build_object('own',(SELECT coalesce(jsonb_agg(c.corner ORDER BY c.corner),'[]'::jsonb) FROM ambient_cards c
   WHERE c.subject_id=person AND reads_subject(p_principal,c.subject_id)));
END $$;

DO $$
DECLARE f text;
BEGIN
 FOREACH f IN ARRAY ARRAY['set_ambient_card(uuid,text,uuid,text[])','read_ambient_cards(uuid,text[])']
 LOOP
   EXECUTE format('ALTER FUNCTION %s SET search_path TO pg_catalog,%I,pg_temp',f,current_schema());
   EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
