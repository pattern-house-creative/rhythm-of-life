-- The admin audit (SB-P3-04 in docs/dev/second-brain-parity/backlog.md; ledger rows VER-10 and DAT-08; docs/dev/memory.md
-- section 4): an insert-only trail of the setup changes that decide who reads what, beside the memory audit trail of
-- migration 025 (memory_events, which records each memory and project change with its principal, channel and client).
-- One row per change: a person joining or leaving Family, an admin role given or taken, a source share recorded or
-- revoked, a token or a viewer session's credential made, disabled or deleted, a person's status changing, and
-- memory.scopes switched. It names who made the change (a principal, or none for the operator on the host and the
-- engine's own jobs), through which channel, and what changed in ids and names, never a token, a code or a record's
-- words. Like memory_events it is never changed or removed, not even by the owner, and holds no key on the rows it
-- names, so it outlives them.
--
-- Triggers on the tables themselves write it, so every writer is audited alike, the host commands, the engine's start
-- and a restore's quarantine included, and the ones to come. A writer names itself for its transaction with three
-- settings, set_config(..., true) (Core/AdminAudit.cs; set_config works before this migration too, so a script for any
-- version may set them): rhythm.audit_channel (cli the operator on the host, app the app's server, mcp an AI client,
-- system the engine itself), rhythm.audit_actor (the login, for app and mcp) and rhythm.audit_job (for system). The host
-- commands name the operator, the start its job start, the app's server a viewer session's own login. A change no
-- writer named, such as a statement run by hand, is recorded as the system's with no job. This changes no answer: every
-- command and start says what it said before, and only the engine's login reads the trail (no tool or route serves it
-- yet; an admin view is SB-P6-01's). The number is provisional.

CREATE TABLE admin_events(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 event text NOT NULL CHECK(event ~ '^[a-z][a-z_]{1,39}$'),
 -- The person the change is about, and the principal (a token or a viewer session's credential) it is about: no keys,
 -- so a row outlives what it names.
 person_id text CHECK(length(person_id) BETWEEN 1 AND 200),
 principal_id uuid,
 -- Who made it: a principal through the app or an AI client, or none (the operator on the host, channel cli, or the
 -- engine itself, channel system, with its job when it named one).
 actor_id uuid,
 channel text NOT NULL CHECK(channel IN ('cli','app','mcp','system')),
 job text CHECK(job ~ '^[a-z][a-z0-9_.-]{0,39}$'),
 detail jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(detail)='object' AND length(detail::text)<=2000),
 at timestamptz NOT NULL DEFAULT now(),
 CHECK(actor_id IS NOT NULL OR channel IN ('cli','system')),
 CHECK(job IS NULL OR channel='system')
);
CREATE INDEX admin_events_at ON admin_events(at,id);
CREATE INDEX admin_events_person ON admin_events(person_id,id) WHERE person_id IS NOT NULL;
REVOKE ALL ON admin_events FROM PUBLIC;
CREATE FUNCTION admin_events_insert_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'The admin audit trail is insert-only' USING ERRCODE='42501';
END $$;
CREATE TRIGGER admin_events_no_change BEFORE UPDATE OR DELETE ON admin_events
 FOR EACH ROW EXECUTE FUNCTION admin_events_insert_only();
CREATE TRIGGER admin_events_no_truncate BEFORE TRUNCATE ON admin_events
 FOR EACH STATEMENT EXECUTE FUNCTION admin_events_insert_only();

-- 033's list with admin_events added: the engine's login reads it and writes it only through the triggers below.
CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS $$
 SELECT ARRAY['memories','memory_versions','memory_events','memory_trash','memory_projects','memory_project_aliases',
   'capsule_revisions','made_links','memory_requests','admin_events']::regclass[]
$$;

-- One row, as the writer named itself (else the system's, with no job). Called by the triggers below, which run as the
-- table's owner; the engine's login calling it directly holds no INSERT on admin_events, so it is refused. What it
-- records is cut to fit the row, so no value of the change it describes fails that change: the person's id at 200
-- characters and each name in the detail at 200 (a legacy token's name has no limit), and a detail still too long
-- once cut is recorded only as cut.
CREATE FUNCTION admin_event_row(p_event text,p_person text,p_principal uuid,p_detail jsonb) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
 channel text:=nullif(current_setting('rhythm.audit_channel',true),'');
 actor text:=nullif(current_setting('rhythm.audit_actor',true),'');
 job text:=nullif(current_setting('rhythm.audit_job',true),'');
 person text:=nullif(left(p_person,200),'');
 detail jsonb:=(SELECT coalesce(jsonb_object_agg(e.key,CASE WHEN jsonb_typeof(e.value)='string' THEN to_jsonb(left(e.value#>>'{}',200))
   ELSE e.value END),'{}'::jsonb) FROM jsonb_each(coalesce(p_detail,'{}'::jsonb)) e);
BEGIN
 IF length(detail::text)>2000 THEN detail:=jsonb_build_object('cut',true); END IF;
 -- A setting that names no known writer counts as unnamed, so a stray value never fails the change it describes.
 IF channel IS NULL OR channel NOT IN ('cli','app','mcp','system')
   OR actor IS NOT NULL AND actor !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
   OR channel IN ('app','mcp') AND actor IS NULL
   OR job IS NOT NULL AND (channel<>'system' OR job !~ '^[a-z][a-z0-9_.-]{0,39}$') THEN
   channel:='system'; actor:=NULL; job:=NULL;
 END IF;
 INSERT INTO admin_events(event,person_id,principal_id,actor_id,channel,job,detail)
 VALUES(p_event,person,p_principal,actor::uuid,channel,job,detail);
END $$;

-- Family: a person joins (with the role they join with) or leaves (with the role they had), an admin role is given or
-- taken. A truncation, which no writer makes, is recorded as one row too.
CREATE FUNCTION admin_audit_memberships() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 IF TG_OP='TRUNCATE' THEN PERFORM admin_event_row('family_cleared',NULL,NULL,'{}'); RETURN NULL; END IF;
 IF TG_OP IN ('DELETE','UPDATE') AND (TG_OP='DELETE' OR NEW.person_id IS DISTINCT FROM OLD.person_id) THEN
   PERFORM admin_event_row('family_left',OLD.person_id,NULL,jsonb_build_object('role',OLD.role));
 END IF;
 IF TG_OP='INSERT' OR TG_OP='UPDATE' AND NEW.person_id IS DISTINCT FROM OLD.person_id THEN
   PERFORM admin_event_row('family_joined',NEW.person_id,NULL,jsonb_build_object('role',NEW.role));
 ELSIF TG_OP='UPDATE' AND NEW.role IS DISTINCT FROM OLD.role THEN
   PERFORM admin_event_row(CASE WHEN NEW.role='admin' THEN 'admin_given' ELSE 'admin_taken' END,NEW.person_id,NULL,'{}');
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER memberships_audit AFTER INSERT OR UPDATE OR DELETE ON memberships FOR EACH ROW EXECUTE FUNCTION admin_audit_memberships();
CREATE TRIGGER memberships_audit_truncate AFTER TRUNCATE ON memberships FOR EACH STATEMENT EXECUTE FUNCTION admin_audit_memberships();

-- Source shares: recorded (by whom: the operator, or the person's own login) or revoked; a share is never rewritten
-- (023's trigger), so an update records nothing.
CREATE FUNCTION admin_audit_source_shares() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 IF TG_OP='TRUNCATE' THEN PERFORM admin_event_row('shares_cleared',NULL,NULL,'{}');
 ELSIF TG_OP='INSERT' THEN
   PERFORM admin_event_row('source_shared',NEW.person_id,NEW.shared_by,jsonb_build_object('provider',NEW.provider));
 ELSIF TG_OP='DELETE' THEN
   PERFORM admin_event_row('source_unshared',OLD.person_id,OLD.shared_by,jsonb_build_object('provider',OLD.provider));
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER source_shares_audit AFTER INSERT OR DELETE ON source_shares FOR EACH ROW EXECUTE FUNCTION admin_audit_source_shares();
CREATE TRIGGER source_shares_audit_truncate AFTER TRUNCATE ON source_shares FOR EACH STATEMENT EXECUTE FUNCTION admin_audit_source_shares();

-- Tokens anyone was given: harness and plugin tokens, legacy ones and viewer sessions' credentials (app principals bound
-- to a person). Made, disabled, enabled again or deleted, with its name, role, plugin and person; never its hash. The
-- shared app token's principal (its file is the authority, and the engine takes it back from there every minute) and
-- the engine's own principals (no usable token) are not given to anyone, so they are not recorded.
CREATE FUNCTION admin_audit_principals() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE r principals:=CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END; event text;
BEGIN
 IF NOT (r.role IN ('harness','ingest','legacy') OR r.role='app' AND r.person_id IS NOT NULL) THEN RETURN NULL; END IF;
 event:=CASE WHEN TG_OP='INSERT' THEN 'token_made' WHEN TG_OP='DELETE' THEN 'token_deleted'
   WHEN NEW.enabled IS DISTINCT FROM OLD.enabled THEN CASE WHEN NEW.enabled THEN 'token_enabled' ELSE 'token_disabled' END END;
 IF event IS NOT NULL THEN
   PERFORM admin_event_row(event,r.person_id,r.id,jsonb_strip_nulls(jsonb_build_object('name',r.name,'role',r.role,'plugin',r.plugin)));
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER principals_audit AFTER INSERT OR UPDATE OF enabled OR DELETE ON principals FOR EACH ROW EXECUTE FUNCTION admin_audit_principals();

-- People: one becomes known (the start, from rhythm.yaml's people), is suspended, has a suspension lifted, becomes
-- former (taken out of people) or current again (listed again).
CREATE FUNCTION admin_audit_subjects() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 IF NEW.kind<>'person' THEN RETURN NULL; END IF;
 IF TG_OP='INSERT' THEN
   PERFORM admin_event_row('person_known',NEW.id,NULL,jsonb_build_object('status',NEW.status));
 ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
   PERFORM admin_event_row(CASE NEW.status WHEN 'suspended' THEN 'person_suspended' WHEN 'former' THEN 'person_former'
     ELSE CASE OLD.status WHEN 'suspended' THEN 'person_unsuspended' ELSE 'person_current' END END,
     NEW.id,NULL,jsonb_build_object('from',OLD.status,'to',NEW.status));
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER subjects_audit AFTER INSERT OR UPDATE OF status ON subjects FOR EACH ROW EXECUTE FUNCTION admin_audit_subjects();

-- The memory.scopes switch: recorded on (the switch's start) or off (rhythm scopes off), or removed by hand.
CREATE FUNCTION admin_audit_scopes() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE was text:=CASE WHEN TG_OP IN ('UPDATE','DELETE') THEN OLD.value END;
 became text:=CASE WHEN TG_OP IN ('INSERT','UPDATE') THEN NEW.value END;
 setting text:=CASE WHEN TG_OP='DELETE' THEN OLD.key ELSE NEW.key END;
BEGIN
 IF setting<>'memory.scopes' OR TG_OP='UPDATE' AND became IS NOT DISTINCT FROM was THEN RETURN NULL; END IF;
 PERFORM admin_event_row(CASE became WHEN 'on' THEN 'scopes_on' WHEN 'off' THEN 'scopes_off' ELSE 'scopes_changed' END,NULL,NULL,
   jsonb_build_object('from',was,'to',became));
 RETURN NULL;
END $$;
CREATE TRIGGER install_settings_audit AFTER INSERT OR UPDATE OR DELETE ON install_settings FOR EACH ROW EXECUTE FUNCTION admin_audit_scopes();

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_events_insert_only() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_event_row(text,text,uuid,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_memberships() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_source_shares() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_principals() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_subjects() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_scopes() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),admin_events_insert_only(),admin_event_row(text,text,uuid,jsonb),
 admin_audit_memberships(),admin_audit_source_shares(),admin_audit_principals(),admin_audit_subjects(),admin_audit_scopes() FROM PUBLIC;
