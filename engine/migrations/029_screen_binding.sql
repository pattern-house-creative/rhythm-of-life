-- A screen's id bound to the app login that first listened with it (docs/dev/screens.md 3.2, docs/dev/memory.md 7.5;
-- SB-P1-19, agreed with the screens program on the coordination board, message 271). A screen's id is made by its
-- browser and listed to every reader (list_screens), so without this any app login that knew one could listen as that
-- screen, take its commands, report for it, send as it and rename it, and the answers to a listen and a rename carry the
-- screen's settings, its Home dashboard's id included. bound_to is the login a screen's id belongs to: set by the listen
-- that makes the screen, and on a row this migration finds (null) by its next listen from the household view, which is
-- never refused for it. Only that login then listens as the screen (and so takes its commands), reports for it, sends
-- as it and renames it; any other is refused (42501, naming nothing about the screen), so what a listen and a rename
-- answer, the settings among it, reaches that login alone. The 100-screen cap counts each login's screens apart: a new
-- screen replaces the one its own login has seen least lately, never another login's, so no login pushes another's
-- screen out and then takes its id.
--
-- The household view is one login here: a screen the household view made (bound to an app login with no person, the
-- shared app token's) is every such login's, so the login a new app token brings (Principals.SyncApp makes one and
-- disables the old) keeps the household's screens. A row no listen has bound yet is the household view's alone until
-- one of its listens binds it: every screen made before this migration was, since no viewer session could reach the
-- engine before it (app.viewers is refused on). A viewer session's screen stays that session's once it has ended, so
-- its settings never reach another login.
--
-- Unlike shown_by and reported_by (027) this holds whatever memory.scopes says: it is which login a screen is, not what
-- anyone reads. While app.viewers is off, every install's setting and the only one this version accepts, every app
-- login is the household view's, so nothing is refused and every answer is as before. The functions keep their
-- signatures and are replaced in place, each with its search path pinned again (019).
ALTER TABLE screens ADD COLUMN bound_to uuid REFERENCES principals(id) ON DELETE CASCADE;

-- Whether p_principal is the login of a screen bound to p_login (null while no listen has bound it): that login itself,
-- or, for the household view's screen and for one not bound yet, any app login with no person.
CREATE FUNCTION screen_login_is(p_principal uuid,p_login uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT p_login IS NOT DISTINCT FROM p_principal
   OR EXISTS(SELECT 1 FROM principals r WHERE r.id=p_principal AND r.role='app' AND r.person_id IS NULL)
     AND (p_login IS NULL OR EXISTS(SELECT 1 FROM principals o WHERE o.id=p_login AND o.role='app' AND o.person_id IS NULL))
$$;

-- Refuses a login that is not the screen's.
CREATE FUNCTION screen_login(p_principal uuid,p_login uuid) RETURNS void LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF NOT screen_login_is(p_principal,p_login) THEN
   RAISE EXCEPTION 'That screen listens with another login' USING ERRCODE='42501';
 END IF;
END $$;

-- As in 027, and a screen is its first login's: a new one is bound to the login that makes it, one not bound yet to the
-- household view's login that listens now, and any other login's listen is refused before anything is written or
-- claimed. A new screen past a login's hundredth replaces the one that login (for the household view, any of its logins,
-- and a row not bound yet) has seen least lately; another login's screens are never counted or removed.
CREATE OR REPLACE FUNCTION listen_screen(p_principal uuid,p_screen text,p_name text,p_showing jsonb,p_settings jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE s screens;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Listening for commands');
 IF p_name IS NULL THEN RAISE EXCEPTION 'A screen listens with its name' USING ERRCODE='22023'; END IF;
 PERFORM screen_input(p_screen,p_name,p_showing,p_settings);
 SELECT * INTO s FROM screens WHERE id=p_screen FOR UPDATE;
 IF NOT FOUND THEN
   PERFORM pg_advisory_xact_lock(hashtext('screens'));
   DELETE FROM screens WHERE id IN (SELECT id FROM screens WHERE id<>p_screen AND screen_login_is(p_principal,bound_to)
     ORDER BY seen_at,id
     LIMIT greatest((SELECT count(*) FROM screens WHERE id<>p_screen AND screen_login_is(p_principal,bound_to))-99,0));
   INSERT INTO screens(id,name,showing,settings,shown_by,bound_to)
   VALUES(p_screen,btrim(p_name),coalesce(p_showing,'{}'),coalesce(p_settings,'{}'),p_principal,p_principal)
   ON CONFLICT(id) DO NOTHING;
   IF FOUND THEN RETURN claim_screen_command(p_screen); END IF;
   -- Another listen made it meanwhile: as for a screen that was there.
   SELECT * INTO s FROM screens WHERE id=p_screen FOR UPDATE;
 END IF;
 PERFORM screen_login(p_principal,s.bound_to);
 UPDATE screens SET name=btrim(p_name),seen_at=now(),showing=coalesce(p_showing,showing),settings=coalesce(p_settings,settings),
   shown_by=CASE WHEN p_showing IS NULL THEN shown_by ELSE p_principal END,bound_to=coalesce(bound_to,p_principal)
 WHERE id=p_screen;
 RETURN claim_screen_command(p_screen);
END $$;

-- As in 020, from the screen's login alone (a row not bound yet is bound by this look from the household view, as by a
-- first one).
CREATE OR REPLACE FUNCTION listen_screen_again(p_principal uuid,p_screen text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE s screens;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Listening for commands');
 PERFORM screen_input(p_screen,NULL,NULL,NULL);
 SELECT * INTO s FROM screens WHERE id=p_screen FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 PERFORM screen_login(p_principal,s.bound_to);
 UPDATE screens SET seen_at=now(),bound_to=coalesce(bound_to,p_principal) WHERE id=p_screen;
 RETURN claim_screen_command(p_screen);
END $$;

-- As in 020, a screen that is not the caller's sending screen sends nothing as it.
CREATE OR REPLACE FUNCTION send_screen_command(p_principal uuid,p_command uuid,p_to text,p_action text,p_args jsonb,p_from text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE sender jsonb; c screen_commands; sent timestamptz; f screens;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app','harness'],'Sending commands to screens');
 PERFORM screen_input(p_to,NULL,NULL,NULL);
 IF p_command IS NULL THEN RAISE EXCEPTION 'A command needs its id' USING ERRCODE='22023'; END IF;
 IF p_action IS NULL OR p_action !~ '^[a-z_]{1,32}$' THEN RAISE EXCEPTION 'Unknown action' USING ERRCODE='22023'; END IF;
 IF p_args IS NULL OR jsonb_typeof(p_args)<>'object' OR length(p_args::text)>2000 THEN
   RAISE EXCEPTION 'A command''s arguments are an object of at most 2000 characters' USING ERRCODE='22023';
 END IF;
 IF principal_has_role(p_principal,ARRAY['app']) THEN
   IF p_from IS NULL THEN RAISE EXCEPTION 'A screen sends as itself' USING ERRCODE='22023'; END IF;
   SELECT * INTO f FROM screens WHERE id=p_from;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown sending screen' USING ERRCODE='P0002'; END IF;
   PERFORM screen_login(p_principal,f.bound_to);
   sender:=jsonb_build_object('kind','screen','screen',f.id,'name',f.name);
 ELSE
   IF p_from IS NOT NULL THEN RAISE EXCEPTION 'Only the app sends as a screen' USING ERRCODE='22023'; END IF;
   sender:='{"kind":"assistant"}';
 END IF;
 PERFORM 1 FROM screens WHERE id=p_to AND seen_at>now()-interval '60 seconds' FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'No open screen has that id' USING ERRCODE='P0002'; END IF;
 IF jsonb_typeof(p_args->'dashboard')='string' AND NOT EXISTS(SELECT 1 FROM dashboards WHERE id=(p_args->>'dashboard')::uuid) THEN
   RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002';
 END IF;
 PERFORM expire_screen_commands(p_to);
 IF (SELECT count(*) FROM screen_commands WHERE screen_id=p_to AND result='pending')>=20 THEN
   RAISE EXCEPTION 'That screen already has 20 commands waiting' USING ERRCODE='22023';
 END IF;
 sent:=clock_timestamp();
 INSERT INTO screen_commands(id,screen_id,action,args,sender,sent_at,expires_at)
 VALUES(p_command,p_to,p_action,p_args,sender,sent,sent+interval '30 seconds') RETURNING * INTO c;
 RETURN screen_command_json(c);
END $$;

-- As in 027, from the screen's login alone.
CREATE OR REPLACE FUNCTION done_screen_command(p_principal uuid,p_screen text,p_command uuid,p_result text,p_detail jsonb,p_showing jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE s screens;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Reporting on commands');
 PERFORM screen_input(p_screen,NULL,p_showing,NULL);
 IF p_result IS NULL OR p_result NOT IN ('shown','failed') THEN RAISE EXCEPTION 'A result is shown or failed' USING ERRCODE='22023'; END IF;
 IF p_detail IS NOT NULL AND (jsonb_typeof(p_detail)<>'object' OR length(p_detail::text)>4000) THEN
   RAISE EXCEPTION 'A detail is an object of at most 4000 characters' USING ERRCODE='22023';
 END IF;
 -- The screen's row first, as a send does, so the two never wait on each other.
 SELECT * INTO s FROM screens WHERE id=p_screen FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 PERFORM screen_login(p_principal,s.bound_to);
 UPDATE screens SET showing=coalesce(p_showing,showing),shown_by=CASE WHEN p_showing IS NULL THEN shown_by ELSE p_principal END
 WHERE id=p_screen;
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

-- As in 020, from the screen's login alone, which alone is answered its settings.
CREATE OR REPLACE FUNCTION rename_screen(p_principal uuid,p_screen text,p_name text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE s screens;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Naming screens');
 IF p_name IS NULL THEN RAISE EXCEPTION 'A screen name is 1-60 characters' USING ERRCODE='22023'; END IF;
 PERFORM screen_input(p_screen,p_name,NULL,NULL);
 SELECT * INTO s FROM screens WHERE id=p_screen FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 PERFORM screen_login(p_principal,s.bound_to);
 UPDATE screens SET name=btrim(p_name) WHERE id=p_screen RETURNING * INTO s;
 RETURN screen_json(s);
END $$;

DO $$
DECLARE f text;
BEGIN
 FOREACH f IN ARRAY ARRAY['screen_login_is(uuid,uuid)','screen_login(uuid,uuid)','listen_screen(uuid,text,text,jsonb,jsonb)',
   'listen_screen_again(uuid,text)','send_screen_command(uuid,uuid,text,text,jsonb,text)',
   'done_screen_command(uuid,text,uuid,text,jsonb,jsonb)','rename_screen(uuid,text,text)']
 LOOP
   EXECUTE format('ALTER FUNCTION %s SET search_path TO pg_catalog,%I,pg_temp',f,current_schema());
   EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
