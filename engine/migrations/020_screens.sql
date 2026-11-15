-- Live screens (docs/dev/screens.md): every open copy of the app is a screen that listens for commands, which an AI
-- client (a harness login) or another screen (the app's login) sends. Commands only change what a screen shows. The
-- engine checks each action and its arguments in code, so later actions need no migration; these functions check the
-- role, the bounds and the rules that need the tables: an open target (heard from in the last 60 seconds), at most 20
-- waiting commands per screen and 100 screens, and a command not taken within 30 seconds expires and is never
-- delivered later. The database is the source of truth; the engine's in-memory wait only decides when to look again.
-- The scheduler deletes commands after 30 days and screens unseen for 180 days. Only these functions write here.
CREATE TABLE screens(
 id text PRIMARY KEY CHECK(id ~ '^[A-Za-z0-9_-]{16,64}$'),
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 60),
 created_at timestamptz NOT NULL DEFAULT now(),
 seen_at timestamptz NOT NULL DEFAULT now(),
 showing jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(showing)='object' AND length(showing::text)<=4000),
 settings jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(settings)='object' AND length(settings::text)<=2000)
);
CREATE TABLE screen_commands(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 screen_id text NOT NULL REFERENCES screens(id) ON DELETE CASCADE,
 action text NOT NULL CHECK(action ~ '^[a-z_]{1,32}$'),
 args jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(args)='object' AND length(args::text)<=2000),
 sender jsonb NOT NULL CHECK(jsonb_typeof(sender)='object' AND length(sender::text)<=500),
 sent_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL,
 delivered_at timestamptz,
 done_at timestamptz,
 result text NOT NULL DEFAULT 'pending' CHECK(result IN ('pending','delivered','shown','failed','expired')),
 detail jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(detail)='object' AND length(detail::text)<=4000)
);
-- A screen's oldest pending command, the daily pruning by age, and the screens by when they were last heard from.
CREATE INDEX screen_commands_pending ON screen_commands(screen_id,sent_at) WHERE result='pending';
CREATE INDEX screen_commands_sent ON screen_commands(sent_at);
CREATE INDEX screens_seen ON screens(seen_at);
REVOKE ALL ON screens FROM PUBLIC;
REVOKE ALL ON screen_commands FROM PUBLIC;

-- A command as every answer carries it, with the delay to delivery and to the screen's report in milliseconds from
-- sent_at (null until it happens).
CREATE FUNCTION screen_command_json(c screen_commands) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',c.id,'action',c.action,'args',c.args,'sender',c.sender,'sentAt',c.sent_at,
   'deliveredAt',c.delivered_at,'doneAt',c.done_at,'result',c.result,'detail',c.detail,
   'ms',jsonb_build_object('deliver',round(extract(epoch FROM c.delivered_at-c.sent_at)*1000)::bigint,
     'show',round(extract(epoch FROM c.done_at-c.sent_at)*1000)::bigint))
$$;
CREATE FUNCTION screen_json(s screens) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',s.id,'name',s.name,'settings',s.settings)
$$;

-- A screen id, name, or what it shows or keeps, outside the tables' bounds is bad input, refused before any write.
-- A null name, showing or settings was not given.
CREATE FUNCTION screen_input(p_screen text,p_name text,p_showing jsonb,p_settings jsonb) RETURNS void LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_screen IS NULL OR p_screen !~ '^[A-Za-z0-9_-]{16,64}$' THEN
   RAISE EXCEPTION 'A screen id is 16-64 letters, digits, _ or -' USING ERRCODE='22023';
 END IF;
 IF p_name IS NOT NULL AND length(btrim(p_name)) NOT BETWEEN 1 AND 60 THEN
   RAISE EXCEPTION 'A screen name is 1-60 characters' USING ERRCODE='22023';
 END IF;
 IF p_showing IS NOT NULL AND (jsonb_typeof(p_showing)<>'object' OR length(p_showing::text)>4000) THEN
   RAISE EXCEPTION 'What a screen shows is an object of at most 4000 characters' USING ERRCODE='22023';
 END IF;
 IF p_settings IS NOT NULL AND (jsonb_typeof(p_settings)<>'object' OR length(p_settings::text)>2000) THEN
   RAISE EXCEPTION 'A screen''s settings are an object of at most 2000 characters' USING ERRCODE='22023';
 END IF;
END $$;

-- A screen's commands not taken within 30 seconds expire, for good.
CREATE FUNCTION expire_screen_commands(p_screen text) RETURNS void LANGUAGE sql AS $$
 UPDATE screen_commands SET result='expired' WHERE screen_id=p_screen AND result='pending' AND expires_at<=now()
$$;

-- The screen's oldest pending command, delivered to this listener: the tabs of one browser share a screen, and the
-- first to look takes it. Answers {command (or null), screen}.
CREATE FUNCTION claim_screen_command(p_screen text) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE c screen_commands;
BEGIN
 PERFORM expire_screen_commands(p_screen);
 UPDATE screen_commands SET result='delivered',delivered_at=clock_timestamp()
 WHERE id=(SELECT id FROM screen_commands WHERE screen_id=p_screen AND result='pending' AND expires_at>now()
   ORDER BY sent_at,id LIMIT 1 FOR UPDATE SKIP LOCKED)
 RETURNING * INTO c;
 RETURN jsonb_build_object('command',CASE WHEN c.id IS NULL THEN NULL ELSE screen_command_json(c) END,
   'screen',(SELECT screen_json(s) FROM screens s WHERE s.id=p_screen));
END $$;

-- A screen listens: it is created or updated (its name, seen now, and what it shows and its settings when given),
-- then given its oldest pending command. A new screen past the 100th replaces the one unseen longest.
CREATE FUNCTION listen_screen(p_principal uuid,p_screen text,p_name text,p_showing jsonb,p_settings jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Listening for commands');
 IF p_name IS NULL THEN RAISE EXCEPTION 'A screen listens with its name' USING ERRCODE='22023'; END IF;
 PERFORM screen_input(p_screen,p_name,p_showing,p_settings);
 UPDATE screens SET name=btrim(p_name),seen_at=now(),showing=coalesce(p_showing,showing),settings=coalesce(p_settings,settings)
 WHERE id=p_screen;
 IF NOT FOUND THEN
   PERFORM pg_advisory_xact_lock(hashtext('screens'));
   DELETE FROM screens WHERE id IN (SELECT id FROM screens WHERE id<>p_screen ORDER BY seen_at,id
     LIMIT greatest((SELECT count(*) FROM screens WHERE id<>p_screen)-99,0));
   INSERT INTO screens(id,name,showing,settings) VALUES(p_screen,btrim(p_name),coalesce(p_showing,'{}'),coalesce(p_settings,'{}'))
   ON CONFLICT(id) DO UPDATE SET name=excluded.name,seen_at=now(),showing=coalesce(p_showing,screens.showing),
     settings=coalesce(p_settings,screens.settings);
 END IF;
 RETURN claim_screen_command(p_screen);
END $$;

-- A listener looks once more after its wait: seen now, and given its oldest pending command, if any. Its name and
-- what it shows stay as they are, so a rename made during the wait is kept.
CREATE FUNCTION listen_screen_again(p_principal uuid,p_screen text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Listening for commands');
 PERFORM screen_input(p_screen,NULL,NULL,NULL);
 UPDATE screens SET seen_at=now() WHERE id=p_screen;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 RETURN claim_screen_command(p_screen);
END $$;

-- A command for one open screen, from another screen (the app's login names it, and the sender carries its name) or
-- from an AI client (a harness login, the assistant), with arguments the engine has already checked. A dashboard it
-- names must exist. The engine gives the command its id, so it can wait for the outcome before the screen can report.
-- A command's times (sent here, then delivered and done) are the clock's when each step happens, not when its
-- transaction began, so a step that waited on the screen's row never reads as earlier than the one before it.
CREATE FUNCTION send_screen_command(p_principal uuid,p_command uuid,p_to text,p_action text,p_args jsonb,p_from text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE sender jsonb; c screen_commands; sent timestamptz;
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
   SELECT jsonb_build_object('kind','screen','screen',id,'name',name) INTO sender FROM screens WHERE id=p_from;
   IF NOT FOUND THEN RAISE EXCEPTION 'Unknown sending screen' USING ERRCODE='P0002'; END IF;
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

-- A screen reports how a command it was given went (shown or failed), with a detail and what it shows now.
CREATE FUNCTION done_screen_command(p_principal uuid,p_screen text,p_command uuid,p_result text,p_detail jsonb,p_showing jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Reporting on commands');
 PERFORM screen_input(p_screen,NULL,p_showing,NULL);
 IF p_result IS NULL OR p_result NOT IN ('shown','failed') THEN RAISE EXCEPTION 'A result is shown or failed' USING ERRCODE='22023'; END IF;
 IF p_detail IS NOT NULL AND (jsonb_typeof(p_detail)<>'object' OR length(p_detail::text)>4000) THEN
   RAISE EXCEPTION 'A detail is an object of at most 4000 characters' USING ERRCODE='22023';
 END IF;
 -- The screen's row first, as a send does, so the two never wait on each other.
 UPDATE screens SET showing=coalesce(p_showing,showing) WHERE id=p_screen;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 UPDATE screen_commands SET result=p_result,done_at=clock_timestamp(),detail=coalesce(p_detail,'{}')
 WHERE id=p_command AND screen_id=p_screen AND result='delivered';
 IF NOT FOUND THEN
   IF EXISTS(SELECT 1 FROM screen_commands WHERE id=p_command AND screen_id=p_screen) THEN
     RAISE EXCEPTION 'That command is not waiting for an outcome' USING ERRCODE='22023';
   END IF;
   RAISE EXCEPTION 'Unknown command' USING ERRCODE='P0002';
 END IF;
 RETURN jsonb_build_object('ok',true);
END $$;

-- One command as it stands now (an overdue one expires first), or null when unknown.
CREATE FUNCTION read_screen_command(p_principal uuid,p_command uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE c screen_commands;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app','harness'],'Reading commands');
 UPDATE screen_commands SET result='expired' WHERE id=p_command AND result='pending' AND expires_at<=now();
 SELECT * INTO c FROM screen_commands WHERE id=p_command;
 IF NOT FOUND THEN RETURN NULL; END IF;
 RETURN screen_command_json(c);
END $$;

-- Screens seen in the last 24 hours, most recently seen (so every open one) first.
CREATE FUNCTION read_screens(p_principal uuid) RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app','harness'],'Reading screens');
 RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'name',name,'open',seen_at>now()-interval '60 seconds',
   'seenAt',seen_at,'showing',showing) ORDER BY seen_at DESC,id),'[]'::jsonb)
   FROM screens WHERE seen_at>now()-interval '24 hours');
END $$;

CREATE FUNCTION rename_screen(p_principal uuid,p_screen text,p_name text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE s screens;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Naming screens');
 IF p_name IS NULL THEN RAISE EXCEPTION 'A screen name is 1-60 characters' USING ERRCODE='22023'; END IF;
 PERFORM screen_input(p_screen,p_name,NULL,NULL);
 UPDATE screens SET name=btrim(p_name) WHERE id=p_screen RETURNING * INTO s;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown screen' USING ERRCODE='P0002'; END IF;
 RETURN screen_json(s);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION screen_command_json(screen_commands) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION screen_json(screens) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION screen_input(text,text,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION expire_screen_commands(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION claim_screen_command(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION listen_screen(uuid,text,text,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION listen_screen_again(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION send_screen_command(uuid,uuid,text,text,jsonb,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION done_screen_command(uuid,text,uuid,text,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_screen_command(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_screens(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION rename_screen(uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION screen_command_json(screen_commands) FROM PUBLIC;
REVOKE ALL ON FUNCTION screen_json(screens) FROM PUBLIC;
REVOKE ALL ON FUNCTION screen_input(text,text,jsonb,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION expire_screen_commands(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION claim_screen_command(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION listen_screen(uuid,text,text,jsonb,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION listen_screen_again(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION send_screen_command(uuid,uuid,text,text,jsonb,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION done_screen_command(uuid,text,uuid,text,jsonb,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_screen_command(uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION read_screens(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION rename_screen(uuid,text,text) FROM PUBLIC;
