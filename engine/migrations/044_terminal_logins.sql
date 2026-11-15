-- The terminal's own login (SB-P3-19 in docs/dev/second-brain-parity/backlog.md; docs/dev/memory.md sections 3 and 5;
-- cli/README.md, Memories from the terminal). rhythm remember, rhythm recall and rhythm list speak for one person through
-- the same checked writers and reads as that person's AI client, so 033's memory_channel told the terminal (cli) from an
-- AI client (mcp) only by the channel the caller named: a person's own client holds the same kind of person-bound token
-- (D-149), and nothing here stopped a call through it naming cli, which a person's own steps take (a release, 036; an
-- undo that clears a hold, 041; a capsule slot, 039; delete forever, 038).
--
-- Here the login says it. A terminal login is a principal of role harness bound to a person and marked terminal: the
-- operator's command makes it on the host on first use (engine/src/Cli/MemoryCommands.cs, TerminalLogin), one enabled
-- at a time per person, with a token hash of random bytes nobody keeps, so no token for it exists to give an AI client;
-- the door refuses it all the same (Database.Authenticate). Being bound to a person, its token hash and person never
-- change (023's trigger), and the mark never changes either (below). memory_channel then takes cli only from a terminal
-- login and mcp only from any other harness login: an AI client's token, person-bound or not, never reaches the
-- terminal's channel, and the terminal's login never writes as an AI client. A terminal login reads and writes as a
-- harness login bound to its person does in every other way (the read predicate, the author lock, where a write lands),
-- and suspending its person, rhythm token revoke --all and a restore's quarantine disable it as they do every token.
--
-- Nothing changes for an install where no one runs those commands: no tool, route or job calls the checked writers
-- through cli (memory.md section 5), every principal there is unmarked, and every answer is the one before. The number
-- is provisional.
ALTER TABLE principals ADD COLUMN terminal boolean NOT NULL DEFAULT false;
ALTER TABLE principals ADD CONSTRAINT principals_terminal CHECK(NOT terminal OR role='harness' AND person_id IS NOT NULL);
CREATE UNIQUE INDEX principals_one_terminal ON principals(person_id) WHERE terminal AND enabled;

-- A principal is a terminal login from when it is made, or never: an AI client's token cannot be marked later.
CREATE FUNCTION principal_terminal_unchanged() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.terminal IS DISTINCT FROM OLD.terminal THEN
   RAISE EXCEPTION 'A principal is a terminal login from when it is made, or never' USING ERRCODE='22023';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER principals_terminal_unchanged BEFORE UPDATE OF terminal ON principals
 FOR EACH ROW EXECUTE FUNCTION principal_terminal_unchanged();

-- 033's memory_channel, with the terminal told apart by its login: the app's login writes through the app; an AI client
-- (harness, not a terminal login) through MCP; the terminal's login, bound to a person, through the terminal alone.
-- And the client's name, at most 80 characters with no control character, or none. A channel or a login that is not
-- there is refused too, rather than let through by SQL's unknown, so every check after this one sees a channel.
CREATE OR REPLACE FUNCTION memory_channel(p_principal uuid,p_channel text,p_client text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE login_role text; person text; is_terminal boolean;
BEGIN
 SELECT p.role,p.person_id,p.terminal INTO login_role,person,is_terminal FROM principals p WHERE p.id=p_principal;
 IF p_channel IS NULL OR login_role IS NULL OR NOT coalesce(login_role='app' AND p_channel='app'
   OR login_role='harness' AND NOT is_terminal AND p_channel='mcp'
   OR login_role='harness' AND is_terminal AND person IS NOT NULL AND p_channel='cli',false) THEN
   RAISE EXCEPTION 'Unknown channel for this login' USING ERRCODE='22023';
 END IF;
 IF p_client IS NOT NULL AND (length(p_client) NOT BETWEEN 1 AND 80 OR p_client ~ '[[:cntrl:]]') THEN
   RAISE EXCEPTION 'A client name is 1 to 80 characters' USING ERRCODE='22023';
 END IF;
 RETURN p_channel;
END $$;

-- 037's admin audit of tokens, with a terminal login's row saying it is one; every other row as before.
CREATE OR REPLACE FUNCTION admin_audit_principals() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE r principals:=CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END; event text;
BEGIN
 IF NOT (r.role IN ('harness','ingest','legacy') OR r.role='app' AND r.person_id IS NOT NULL) THEN RETURN NULL; END IF;
 event:=CASE WHEN TG_OP='INSERT' THEN 'token_made' WHEN TG_OP='DELETE' THEN 'token_deleted'
   WHEN NEW.enabled IS DISTINCT FROM OLD.enabled THEN CASE WHEN NEW.enabled THEN 'token_enabled' ELSE 'token_disabled' END END;
 IF event IS NOT NULL THEN
   PERFORM admin_event_row(event,r.person_id,r.id,jsonb_strip_nulls(jsonb_build_object('name',r.name,'role',r.role,'plugin',r.plugin,
     'terminal',CASE WHEN r.terminal THEN true END)));
 END IF;
 RETURN NULL;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION principal_terminal_unchanged() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_channel(uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_principals() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION principal_terminal_unchanged(),memory_channel(uuid,text,text),admin_audit_principals() FROM PUBLIC;
