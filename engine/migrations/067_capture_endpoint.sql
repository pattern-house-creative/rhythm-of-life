-- The generic capture endpoint (SB-P7-01 in docs/dev/second-brain-parity/backlog.md; docs/dev/memory.md sections 3 and 5;
-- contracts/README.md section 6; ledger rows API-01, API-48, LAN-14, LAN-15, SEC-17): webhooks, CI jobs and shortcuts save
-- a memory over HTTP with a token of their own, which writes and reads nothing. The behaviour the ledger records of Second
-- Brain's capture route and its managed mirrors, built here on Rhythm's own writers; no upstream code is taken.
--
-- A capture token is a principal of role capture, stored and revoked as every token is (a hash, rhythm token revoke, a
-- restore's quarantine, 037's admin audit). It writes into one scope: bound to a person, that person's private scope once
-- memory scopes are on; with no person, Family. With scopes off every memory is the household's, as for every writer
-- (D-048). A call may name that scope and no other. The role reaches capture_memory alone: every other writer's role
-- check and the read predicate (023's readable_subjects) leave it out, in both modes, so it reads nothing.
--
-- A captured memory is a draft and takes no kind or importance from outside. It is never canonical before a person reviews
-- it: set_memory_status, replaced here, and 065's undo_memory refuse an AI client's confirming one. An outside source can't
-- put anything on what the person owes: the tag task is dropped from a capture (058's memory_open_loop reads it), and a
-- person or an AI client who means one adds it after. Its audit row says channel capture and the
-- connection's name as the client. That channel is scored as an AI client's:
-- 036's screening and 045's held projects read every channel but a person's own, and a burst past a login's 40th write
-- in ten minutes is held too (memory_event_capture_burst, a trigger of its own that fires before 045's). Only a person
-- releases what is held (036), and the review queue (Core/MemoryReview.cs) lists it like any other hold. A source name is text
-- from outside that AI clients read, and a held memory's source is served as any memory's, so a hold would not keep it from
-- them: one that reads like instructions (036's memory_screen, hyphens, underscores and dots read as spaces, as 045 reads a new
-- project's slug) is refused, and nothing is saved.
--
-- A mirror is a captured memory that keeps the id an outside item has (memory_mirrors): the same connection capturing the
-- same id again changes that memory, keeping the earlier state as a version, and nothing else does. A connection is a
-- capture token's name, so a token replaced under one name (revoked, then made again) keeps its mirrors. While it is
-- connected a mirror is read-only: no writer changes its words, tags, status, scope or anything else
-- (memories_mirror_read_only, on the table, so a writer to come is held to it too) and it does not go to the trash on its
-- own (memory_trash_no_mirror). A person's release of a hold, an audit row, a link to it and a project's delete (which takes
-- its memories out of it) still work. Removing the connection (disconnect_capture, the operator's rhythm capture
-- disconnect) disables its token and moves its mirrors to the trash with reason disconnect, keeping their links, from
-- where a person restores one as an ordinary draft or the trash's retention (038) ends it. restore_memory, replaced here,
-- refuses an AI client's restoring one (needs_person), so what an operator took out is not back in recall with no person.
-- Nothing here deletes a memory.
-- Upstream's mirrors skip its duplicate and contradiction checks; a capture runs neither, and a mirror is no insight
-- material (memory_insight_eligible, replaced here), as upstream's eligibility says.
--
-- Nothing calls these but the engine's /capture route and rhythm capture, both only with memory.capture on beside
-- memory.tools (off by default): with it off the route answers as an unknown route does, no capture principal exists, no
-- mirror does, and every answer is as before. Nothing is exposed: the route is on the engine's one port, and no recipe in
-- deploy/exposure routes it. The tables and functions below change nothing for any other role: 033's channel check gains
-- one line for this role, 037's audit of tokens one role, and memory_events' channel one value; 033's set_memory_status
-- and 051's restore_memory each gain one refusal, which only a memory a capture made can meet, and 035's delete_memory_project
-- one setting, which only a mirror's trigger reads.
--
-- Errors, as 033's: 42501 not available to this login or outside its one scope, and the read-only refusals; 22023 invalid
-- input; 54000 text over the limit; 23505 an idempotency key used before for another request. A refused write writes
-- nothing. The number is provisional.

ALTER TABLE principals DROP CONSTRAINT principals_role_check;
ALTER TABLE principals ADD CONSTRAINT principals_role_check CHECK(role IN ('legacy','system','harness','app','ingest','capture'));
ALTER TABLE principals DROP CONSTRAINT principals_person_role;
ALTER TABLE principals ADD CONSTRAINT principals_person_role CHECK(person_id IS NULL OR role IN ('harness','app','capture'));

ALTER TABLE memory_events DROP CONSTRAINT memory_events_channel_check;
ALTER TABLE memory_events ADD CONSTRAINT memory_events_channel_check CHECK(channel IN ('mcp','app','cli','system','capture'));

-- A mirror: the memory, the connection that keeps it (a capture token's name) and the outside item's id, which names it for
-- that connection alone. It goes with its memory, and when its connection is removed.
CREATE TABLE memory_mirrors(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 connection text NOT NULL CHECK(length(connection) BETWEEN 1 AND 40),
 external_id text NOT NULL CHECK(external_id ~ '^[A-Za-z0-9._:/#-]{1,200}$'),
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(connection,external_id)
);
REVOKE ALL ON memory_mirrors FROM PUBLIC;

-- The list of checked tables in force, with this one added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_mirrors']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- 044's memory_channel, with a capture login's one channel; every other login as before.
CREATE OR REPLACE FUNCTION memory_channel(p_principal uuid,p_channel text,p_client text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE login_role text; person text; is_terminal boolean;
BEGIN
 SELECT p.role,p.person_id,p.terminal INTO login_role,person,is_terminal FROM principals p WHERE p.id=p_principal;
 IF p_channel IS NULL OR login_role IS NULL OR NOT coalesce(login_role='app' AND p_channel='app'
   OR login_role='harness' AND NOT is_terminal AND p_channel='mcp'
   OR login_role='harness' AND is_terminal AND person IS NOT NULL AND p_channel='cli'
   OR login_role='capture' AND p_channel='capture',false) THEN
   RAISE EXCEPTION 'Unknown channel for this login' USING ERRCODE='22023';
 END IF;
 IF p_client IS NOT NULL AND (length(p_client) NOT BETWEEN 1 AND 80 OR p_client ~ '[[:cntrl:]]') THEN
   RAISE EXCEPTION 'A client name is 1 to 80 characters' USING ERRCODE='22023';
 END IF;
 RETURN p_channel;
END $$;

-- 044's admin audit of tokens, with a capture token's rows; every other row as before.
CREATE OR REPLACE FUNCTION admin_audit_principals() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE r principals:=CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END; event text;
BEGIN
 IF NOT (r.role IN ('harness','ingest','legacy','capture') OR r.role='app' AND r.person_id IS NOT NULL) THEN RETURN NULL; END IF;
 event:=CASE WHEN TG_OP='INSERT' THEN 'token_made' WHEN TG_OP='DELETE' THEN 'token_deleted'
   WHEN NEW.enabled IS DISTINCT FROM OLD.enabled THEN CASE WHEN NEW.enabled THEN 'token_enabled' ELSE 'token_disabled' END END;
 IF event IS NOT NULL THEN
   PERFORM admin_event_row(event,r.person_id,r.id,jsonb_strip_nulls(jsonb_build_object('name',r.name,'role',r.role,'plugin',r.plugin,
     'terminal',CASE WHEN r.terminal THEN true END)));
 END IF;
 RETURN NULL;
END $$;

-- A burst through the capture channel (045's rule for MCP, on the same limit and window): a create or an update by a login
-- that has already made 40 such writes in the last ten minutes is held, reason burst, whatever its words; a memory its words
-- held keeps their reason, with the burst added to its signals and its score. Fires before 045's trigger, which reads the
-- hold and the note it leaves (a table's BEFORE triggers fire in name order).
CREATE INDEX memory_events_capture_writes ON memory_events(principal_id,at)
 WHERE channel='capture' AND memory_id IS NOT NULL AND event IN ('created','updated');
CREATE FUNCTION memory_event_capture_burst() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE earlier integer; notes jsonb; noted jsonb;
BEGIN
 SELECT count(*) INTO earlier FROM (SELECT 1 FROM memory_events e WHERE e.principal_id=NEW.principal_id AND e.channel='capture'
   AND e.memory_id IS NOT NULL AND e.event IN ('created','updated') AND e.at>now()-memory_burst_window() LIMIT memory_burst_limit()) x;
 IF earlier<memory_burst_limit() THEN RETURN NEW; END IF;
 notes:=coalesce(nullif(current_setting('rhythm.memory_held',true),'')::jsonb,'{}'::jsonb);
 noted:=notes->(NEW.memory_id::text);
 IF noted IS NOT NULL AND noted->>'revision' IS DISTINCT FROM NEW.revision::text THEN noted:=NULL; END IF;
 IF noted IS NOT NULL THEN
   noted:=noted||jsonb_build_object('score',round((noted->>'score')::numeric+1.0,2),'signals',coalesce(noted->'signals','[]'::jsonb)||'["burst"]'::jsonb);
   UPDATE memories SET hold_score=(noted->>'score')::real WHERE id=NEW.memory_id;
 ELSIF EXISTS(SELECT 1 FROM memories WHERE id=NEW.memory_id AND hold IS NULL) THEN
   noted:=jsonb_build_object('score',1.0,'reason','burst','signals','["burst"]'::jsonb,'revision',NEW.revision);
   UPDATE memories SET hold='burst',hold_score=1.0 WHERE id=NEW.memory_id;
 ELSE
   RETURN NEW;
 END IF;
 PERFORM set_config('rhythm.memory_held',(notes||jsonb_build_object(NEW.memory_id::text,noted))::text,true);
 RETURN NEW;
END $$;
CREATE TRIGGER memory_events_capture_burst BEFORE INSERT ON memory_events FOR EACH ROW
 WHEN (NEW.channel='capture' AND NEW.memory_id IS NOT NULL AND NEW.event IN ('created','updated'))
 EXECUTE FUNCTION memory_event_capture_burst();

-- A mirror is read-only: a change of anything but its hold (a person's release, or a re-screening), its revision and when
-- it changed is refused, unless its own connection is writing it (capture_memory says so for its one update) or a
-- project's delete is taking it out of that project (delete_memory_project says which, for its one loop; no other writer
-- may clear a mirror's project). A whole-row comparison, so a column added later is read-only too.
CREATE FUNCTION memory_mirror_read_only() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE was memories:=OLD;
BEGIN
 IF current_setting('rhythm.mirror_write',true) IS NOT DISTINCT FROM NEW.id::text
   OR NOT EXISTS(SELECT 1 FROM memory_mirrors x WHERE x.memory_id=NEW.id) THEN RETURN NEW; END IF;
 was.hold:=NEW.hold; was.hold_score:=NEW.hold_score; was.revision:=NEW.revision; was.updated_at:=NEW.updated_at;
 was.text_revision:=NEW.text_revision;
 IF NEW.project_id IS NULL AND OLD.project_id::text IS NOT DISTINCT FROM current_setting('rhythm.project_delete',true) THEN was.project_id:=NULL; END IF;
 IF to_jsonb(was) IS DISTINCT FROM to_jsonb(NEW) THEN
   RAISE EXCEPTION 'This memory mirrors an outside source and is read-only: only its connection writes it, and rhythm capture disconnect moves it to the trash'
     USING ERRCODE='42501';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memories_mirror_read_only BEFORE UPDATE ON memories FOR EACH ROW EXECUTE FUNCTION memory_mirror_read_only();

-- A mirror stays out of the trash while it is connected: disconnect_capture takes its mirror row first.
CREATE FUNCTION memory_trash_no_mirror() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM memory_mirrors x WHERE x.memory_id=NEW.memory_id) THEN
   RAISE EXCEPTION 'This memory mirrors an outside source and is read-only: rhythm capture disconnect moves it to the trash, with the rest of its connection''s'
     USING ERRCODE='42501';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memory_trash_no_mirror BEFORE INSERT ON memory_trash FOR EACH ROW EXECUTE FUNCTION memory_trash_no_mirror();

-- 064's memory_insight_eligible, with a mirror left out: an outside source's copy is not insight material (INT-03).
CREATE OR REPLACE FUNCTION memory_insight_eligible(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((SELECT m.status<>'deprecated' AND m.hold IS NULL AND m.source<>'system'
     AND (m.valid_until IS NULL OR m.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
     AND NOT EXISTS(SELECT 1 FROM memory_mirrors x WHERE x.memory_id=m.id)
     AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
       AND r.status='current')
   FROM memories m WHERE m.id=p_memory),false)
$$;

-- 033's set_memory_status, with one refusal: an AI client does not confirm a memory a capture made, whenever it asks and
-- whatever became of the memory since. Only a person's channel does (the app's, or the terminal's). An AI client still makes
-- one a draft or deprecated as any memory it may change, and asking for the status it has still answers unchanged, so one a
-- person confirmed is as before. Every other memory is as before.
CREATE OR REPLACE FUNCTION set_memory_status(p_principal uuid,p_memory uuid,p_status text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; was text; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_status IS NULL OR p_status NOT IN ('canonical','draft','deprecated') THEN
   RAISE EXCEPTION 'status is canonical, draft or deprecated' USING ERRCODE='22023';
 END IF;
 hash:=memory_request_hash('set_status',p_memory,to_jsonb(p_status),p_expected);
 replay:=memory_replay(p_principal,p_key,'set_status',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.status=p_status THEN
   RETURN memory_answered(p_principal,p_key,'set_status',hash,m.id,memory_reply(m,scoped)||jsonb_build_object('unchanged',true));
 END IF;
 IF p_status='canonical' AND channel='mcp'
   AND EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.event='created' AND e.channel='capture') THEN
   RAISE EXCEPTION 'This memory was captured from outside: only a person confirms it, in the app''s Memories view or with rhythm set-status at the terminal'
     USING ERRCODE='42501';
 END IF;
 was:=m.status;
 PERFORM memory_retire(m);
 UPDATE memories SET status=p_status,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'status_changed',p_principal,channel,p_client,jsonb_build_object('from',was,'to',p_status));
 result:=memory_reply(m,scoped);
 RETURN memory_answered(p_principal,p_key,'set_status',hash,m.id,result);
END $$;

-- 051's restore_memory, with one refusal: what the operator's disconnect moved to the trash a person brings back, never an AI
-- client (needs_person), or a feed removed because it went wrong could be in recall again with no person. Every other restore
-- is as before.
CREATE OR REPLACE FUNCTION restore_memory(p_principal uuid,p_memory uuid,p_nonce uuid,p_channel text,p_client text DEFAULT NULL,
 p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; t memory_trash; back integer; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 hash:=memory_request_hash('restore',p_memory,coalesce(to_jsonb(p_nonce),'null'::jsonb),NULL);
 replay:=memory_replay(p_principal,p_key,'restore',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 t:=memory_in_trash(p_principal,p_memory,p_nonce);
 SELECT * INTO m FROM memories WHERE id=t.memory_id;
 IF NOT memory_may_change(p_principal,m.subject_id,m.author_id,scoped) THEN
   RAISE EXCEPTION 'Only this memory''s author or a Family admin may restore it' USING ERRCODE='42501';
 END IF;
 IF channel='mcp' AND t.reason='disconnect' THEN
   RAISE EXCEPTION 'The operator removed the connection this memory came through, so a person decides whether it comes back, from the app or the terminal: tell the person and leave it in the trash'
     USING ERRCODE='22023',HINT='needs_person';
 END IF;
 INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported,
   confidence,source_revision,checked_at)
 SELECT l.link_id,l.source_id,l.target_id,l.type,l.verification,l.evidence,l.lifecycle,l.made_by,l.created_at,coalesce(l.imported,false),
   l.confidence,l.source_revision,l.checked_at
 FROM jsonb_populate_recordset(NULL::made_links,t.links) l
 WHERE EXISTS(SELECT 1 FROM resources s WHERE s.id=l.source_id) AND EXISTS(SELECT 1 FROM resources x WHERE x.id=l.target_id)
   AND (l.made_by IS NULL AND coalesce(l.imported,false) OR EXISTS(SELECT 1 FROM principals q WHERE q.id=l.made_by))
 ON CONFLICT DO NOTHING;
 GET DIAGNOSTICS back=ROW_COUNT;
 UPDATE resources SET status='current' WHERE id=m.id AND provider='memory' AND relationship_snapshot IS NULL;
 DELETE FROM memory_trash WHERE memory_id=m.id;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'restored',p_principal,channel,p_client,jsonb_build_object('reason',t.reason,
   'trashed_at',t.trashed_at,'links',back,'links_gone',jsonb_array_length(t.links)-back));
 result:=memory_reply(m,scoped)||jsonb_build_object('restored',jsonb_build_object('links',back,
   'links_gone',jsonb_array_length(t.links)-back,'trashed_at',t.trashed_at,'reason',t.reason));
 RETURN memory_answered(p_principal,p_key,'restore',hash,m.id,result);
END $$;

-- 035's delete_memory_project, naming the project it is taking its memories out of for the length of its one loop, so a
-- mirror's trigger lets this and no other writer clear a mirror's project. Otherwise as before.
CREATE OR REPLACE FUNCTION delete_memory_project(p_principal uuid,p_project uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; known memory_projects; p memory_projects; m memories; fields text[]; moved integer:=0;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing projects is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 -- Refused before any memory is locked: an unknown or unreadable project, or one not this caller's to change.
 SELECT * INTO known FROM memory_projects WHERE id=p_project;
 IF NOT FOUND OR NOT reads_subject(p_principal,known.subject_id) THEN
   RAISE EXCEPTION 'Unknown project' USING ERRCODE='P0002';
 END IF;
 IF NOT memory_may_change(p_principal,known.subject_id,known.author_id,scoped) THEN
   RAISE EXCEPTION 'Only this project''s creator or a Family admin may change it' USING ERRCODE='42501';
 END IF;
 -- Its memories' resources rows, then the memories (D-086), then the project's row, whose lock keeps any other memory
 -- from joining it now (a memory names a project only under a key share of its row). One that joined before that lock
 -- is locked after it, the same order.
 PERFORM 1 FROM resources r WHERE r.provider='memory' AND r.relationship_snapshot IS NULL
   AND r.id IN (SELECT x.id FROM memories x WHERE x.project_id=p_project) ORDER BY r.id FOR UPDATE;
 PERFORM 1 FROM memories x WHERE x.project_id=p_project ORDER BY x.id FOR UPDATE;
 p:=memory_project_for_change(p_principal,p_project,p_expected,scoped);
 PERFORM 1 FROM resources r WHERE r.provider='memory' AND r.relationship_snapshot IS NULL
   AND r.id IN (SELECT x.id FROM memories x WHERE x.project_id=p_project) ORDER BY r.id FOR UPDATE;
 PERFORM set_config('rhythm.project_delete',p.id::text,true);
 FOR m IN SELECT * FROM memories x WHERE x.project_id=p_project ORDER BY x.id FOR UPDATE LOOP
   fields:=CASE WHEN m.capsule='project' THEN ARRAY['project','capsule'] ELSE ARRAY['project'] END;
   PERFORM memory_retire(m);
   UPDATE memories SET project_id=NULL,
     capsule=CASE WHEN capsule='project' THEN NULL ELSE capsule END,
     capsule_slot=CASE WHEN capsule='project' THEN NULL ELSE capsule_slot END,
     revision=revision+1,updated_at=greatest(now(),updated_at)
   WHERE id=m.id RETURNING * INTO m;
   INSERT INTO memory_events(memory_id,project_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,p.id,m.subject_id,m.revision,'updated',p_principal,channel,p_client,
     jsonb_build_object('fields',to_jsonb(fields),'cause','project_deleted'));
   moved:=moved+1;
 END LOOP;
 PERFORM set_config('rhythm.project_delete','',true);
 DELETE FROM memory_projects WHERE id=p.id;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(p.id,p.subject_id,p.revision,'deleted',p_principal,channel,p_client,jsonb_build_object('slug',p.slug,'memories',moved));
 RETURN jsonb_build_object('id',p.id,'slug',p.slug,'subject',p.subject_id,'deleted',true,'memories',moved,
   'scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
END $$;

-- What a capture answers: its memory's id and revision, the scope it is in, the mode it was written under (the reply that
-- saves one with scopes off must say everyone reads it, D-048), its status and whether it is held. Never its words, a
-- project or anything else of the scope it was written into: the login reads nothing.
CREATE FUNCTION capture_reply(m memories,p_scoped boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',m.id,'revision',m.revision,'scope',m.scope,'scopes',CASE WHEN p_scoped THEN 'on' ELSE 'off' END,
   'status',m.status,'held',m.hold IS NOT NULL)
$$;

-- capture_memory: one capture from p_capture, the fields the engine prepared (MemoryInput): content, tags, and optionally
-- scope (only the login's own), project (a slug, made in the scope when new), source (a short name, else capture; never
-- one of the engine's own channels) and mirror (the outside item's id). The tag task is left off, however it came. A new
-- memory is a draft; a mirror its connection already keeps is changed, and a capture that changes nothing writes nothing and answers unchanged. An idempotency key
-- is the login's own, as for every writer, and every capture has one operation, so a retry finds its first answer whether
-- that made or changed a memory.
--
-- Locks, in the writers' order: the recorded mode's row FOR SHARE (033), the login's row FOR SHARE, which a disconnect
-- takes FOR NO KEY UPDATE, so no capture lands a mirror after a disconnect has gathered them; a mirror's advisory lock, so two
-- captures of one new item make one memory; then a mirror's resources row and memories row (D-086).
CREATE FUNCTION capture_memory(p_principal uuid,p_capture jsonb,p_client text DEFAULT NULL,p_key text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; person text; v_name text; hash text; replay jsonb; wanted text; own text; landed text; v_mirror text; v_source text; v_tags text[];
 made uuid:=gen_random_uuid(); known uuid; project jsonb; m memories; changed text[]; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['capture']) THEN
   RAISE EXCEPTION 'Capturing is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 SELECT p.person_id,p.name INTO person,v_name FROM principals p WHERE p.id=p_principal AND p.enabled AND p.role='capture'
   AND (p.person_id IS NULL OR EXISTS(SELECT 1 FROM subjects s WHERE s.id=p.person_id AND s.status='current')) FOR SHARE OF p;
 IF NOT FOUND THEN RAISE EXCEPTION 'Capturing is not available to this login' USING ERRCODE='42501'; END IF;
 channel:=memory_channel(p_principal,'capture',p_client);
 p_capture:=jsonb_strip_nulls(p_capture);
 IF p_capture IS NULL OR jsonb_typeof(p_capture)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_capture) k WHERE k NOT IN ('content','tags','scope','project','source','mirror')) THEN
   RAISE EXCEPTION 'A capture takes content, tags, scope, project, source and mirror' USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM jsonb_each(p_capture) e WHERE e.key IN ('content','scope','project','source','mirror') AND jsonb_typeof(e.value)<>'string') THEN
   RAISE EXCEPTION 'content, scope, project, source and mirror are each text' USING ERRCODE='22023';
 END IF;
 hash:=memory_request_hash('create',NULL,p_capture,NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 own:=CASE WHEN scoped AND person IS NOT NULL THEN 'private' ELSE 'shared' END;
 wanted:=memory_scope_named(p_capture->>'scope');
 IF wanted IS NOT NULL AND wanted<>own THEN
   RAISE EXCEPTION 'A capture token writes into one scope, the one it was made for, and this is not it' USING ERRCODE='42501';
 END IF;
 landed:=CASE WHEN own='private' THEN person ELSE 'household' END;
 PERFORM memory_check_text(p_capture->>'content');
 v_tags:=array_remove(memory_check_tags(memory_strings(coalesce(p_capture->'tags','[]'))),'task');
 v_source:=coalesce(p_capture->>'source','capture');
 IF v_source !~ '^[a-z][a-z0-9_.-]{0,63}$' OR v_source IN ('app','cli','system') THEN
   RAISE EXCEPTION 'source is a short lower-case name, and not one of the engine''s own channels' USING ERRCODE='22023';
 END IF;
 IF memory_screen(translate(v_source,'-_.','   '))->>'reason' IS NOT NULL THEN
   RAISE EXCEPTION 'source reads like instructions to an AI client, and AI clients read it: name where this comes from in plain words. Nothing was saved'
     USING ERRCODE='22023';
 END IF;
 v_mirror:=p_capture->>'mirror';
 IF v_mirror IS NOT NULL THEN
   IF v_mirror !~ '^[A-Za-z0-9._:/#-]{1,200}$' THEN
     RAISE EXCEPTION 'mirror is an outside item''s id: 1 to 200 letters, digits or ._:/#-' USING ERRCODE='22023';
   END IF;
   PERFORM pg_advisory_xact_lock(hashtextextended('memory-mirror:'||v_name||':'||v_mirror,0));
   SELECT x.memory_id INTO known FROM memory_mirrors x WHERE x.connection=v_name AND x.external_id=v_mirror;
 END IF;
 IF known IS NULL THEN
   IF p_capture->>'project' IS NOT NULL THEN
     project:=memory_project(p_principal,person,landed,p_capture->>'project',channel,p_client);
   END IF;
   INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
   VALUES(made,landed,'memory',made::text,'memory',own,'Memory','','current','memory');
   INSERT INTO memories(id,subject_id,scope,author_id,created_by,content,tags,status,source,project_id)
   VALUES(made,landed,own,person,p_principal,p_capture->>'content',v_tags,'draft',v_source,(project->>'id')::uuid);
   IF v_mirror IS NOT NULL THEN
     INSERT INTO memory_mirrors(memory_id,connection,external_id) VALUES(made,v_name,v_mirror);
   END IF;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client)
   VALUES(made,landed,1,'created',p_principal,channel,p_client);
   SELECT * INTO m FROM memories WHERE id=made;
   result:=capture_reply(m,scoped);
 ELSE
   PERFORM 1 FROM resources r WHERE r.id=known AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
   SELECT * INTO m FROM memories WHERE id=known FOR UPDATE;
   IF m.subject_id<>landed THEN
     RAISE EXCEPTION 'A capture token writes into one scope, the one it was made for, and this mirror is not in it' USING ERRCODE='42501';
   END IF;
   IF p_capture->>'project' IS NOT NULL THEN
     project:=memory_project(p_principal,person,landed,p_capture->>'project',channel,p_client);
   END IF;
   changed:=ARRAY(SELECT f FROM (VALUES('content',p_capture->>'content' IS DISTINCT FROM m.content),('tags',v_tags IS DISTINCT FROM m.tags),
     ('source',v_source IS DISTINCT FROM m.source),('project',(project->>'id')::uuid IS DISTINCT FROM m.project_id)) c(f,d) WHERE d);
   IF cardinality(changed)=0 THEN
     result:=capture_reply(m,scoped)||jsonb_build_object('unchanged',true);
   ELSE
     PERFORM memory_retire(m);
     PERFORM set_config('rhythm.mirror_write',m.id::text,true);
     UPDATE memories SET content=p_capture->>'content',tags=v_tags,source=v_source,project_id=(project->>'id')::uuid,
       revision=revision+1,updated_at=greatest(now(),updated_at)
     WHERE id=m.id RETURNING * INTO m;
     PERFORM set_config('rhythm.mirror_write','',true);
     INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
     VALUES(m.id,m.subject_id,m.revision,'updated',p_principal,channel,p_client,jsonb_build_object('fields',to_jsonb(changed)));
     SELECT * INTO m FROM memories WHERE id=m.id;
     result:=capture_reply(m,scoped);
   END IF;
 END IF;
 result:=result||CASE WHEN v_mirror IS NOT NULL THEN '{"mirror":true}'::jsonb ELSE '{}'::jsonb END;
 RETURN memory_answered(p_principal,p_key,'create',hash,m.id,result);
END $$;

-- disconnect_capture: the operator removes a connection on the host (rhythm capture disconnect; memory.md section 5): its
-- capture token is disabled, whichever of that name is enabled, and each mirror it keeps, its token revoked or not, moves to the trash, reason disconnect, with its links, as a forget does, for 038's retention, its mirror row
-- gone so a person's restore brings back an ordinary draft. Nothing is deleted. One audit row per mirror, trashed, with no
-- principal (channel cli, the operator); the token's own row is the admin audit's. The answer counts them; known false
-- when no capture token has that name. The caller names the operator for the admin audit (AdminAudit.As).
CREATE FUNCTION disconnect_capture(p_name text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE tokens integer; mirrors integer:=0; target uuid; m memories; kept jsonb; until timestamptz:=now()+make_interval(days=>memory_trash_days());
BEGIN
 PERFORM lock_memory_scopes();
 PERFORM 1 FROM principals p WHERE p.role='capture' AND p.name=p_name ORDER BY p.id FOR NO KEY UPDATE;
 IF NOT EXISTS(SELECT 1 FROM principals p WHERE p.role='capture' AND p.name=p_name) THEN RETURN jsonb_build_object('known',false); END IF;
 UPDATE principals SET enabled=false WHERE role='capture' AND name=p_name AND enabled;
 GET DIAGNOSTICS tokens=ROW_COUNT;
 FOR target IN SELECT x.memory_id FROM memory_mirrors x WHERE x.connection=p_name ORDER BY x.memory_id LOOP
   PERFORM 1 FROM resources r WHERE r.id=target AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
   SELECT * INTO m FROM memories WHERE id=target FOR UPDATE;
   SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.created_at,l.link_id),'[]'::jsonb) INTO kept
     FROM made_links l WHERE l.source_id=m.id OR l.target_id=m.id;
   DELETE FROM made_links WHERE source_id=m.id OR target_id=m.id;
   DELETE FROM memory_mirrors WHERE memory_id=m.id;
   UPDATE resources SET status='deleted' WHERE id=m.id AND provider='memory' AND relationship_snapshot IS NULL;
   INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after,links) VALUES(m.id,'disconnect',NULL,'cli',until,kept);
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'trashed',NULL,'cli',NULL,
     jsonb_build_object('reason','disconnect','connection',p_name,'purge_after',until,'links',jsonb_array_length(kept)));
   mirrors:=mirrors+1;
 END LOOP;
 RETURN jsonb_build_object('known',true,'tokens',tokens,'mirrors',mirrors,'purge_after',CASE WHEN mirrors>0 THEN until END);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_channel(uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_principals() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_capture_burst() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_mirror_read_only() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_trash_no_mirror() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_insight_eligible(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_memory_status(uuid,uuid,text,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION restore_memory(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION delete_memory_project(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION capture_reply(memories,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION capture_memory(uuid,jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION disconnect_capture(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_channel(uuid,text,text),admin_audit_principals(),memory_event_capture_burst(),
 memory_mirror_read_only(),memory_trash_no_mirror(),memory_insight_eligible(uuid),set_memory_status(uuid,uuid,text,integer,text,text,text),
 restore_memory(uuid,uuid,uuid,text,text,text),delete_memory_project(uuid,uuid,integer,text,text),
 capture_reply(memories,boolean),capture_memory(uuid,jsonb,text,text),
 disconnect_capture(text) FROM PUBLIC;
