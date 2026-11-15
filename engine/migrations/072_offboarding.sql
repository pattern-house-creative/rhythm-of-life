-- Offboarding a person (SB-P7-13 in docs/dev/second-brain-parity/backlog.md; docs/dev/memory.md sections 2, 4 and 5;
-- ledger rows API-59, SEC-09 and JOB-07): the operator's step on the host when someone leaves the household for good,
-- rhythm person offboard <id>. The behaviour the ledger records of Second Brain's member removal (the personal workspace
-- removed, resumable, the shared entries kept with their author), built on Rhythm's own trash; no upstream code is taken.
--
-- Three steps, each in transactions of its own, so a stop anywhere leaves a state the next run goes on from:
-- 1. offboard_person: from now on the person reads and writes nothing, in either mode. Suspended if they were current
--    (a former person, no longer in people, stays former), every token and session credential bound to them disabled
--    for good as a suspension does (their terminal login and a capture connection made for them included), and their
--    Family membership (an admin role with it), their source shares and any one-time viewer code of theirs not used yet
--    ended. A row in offboardings records that it started; running it again after a stop, or after they were brought
--    back, goes on with the same row.
-- 2. offboard_trash: their private memories (the rows whose subject is the person), a bounded number a call, move to the
--    trash with reason offboard, as a forget does: nothing is deleted. Each keeps its row, text, versions and project;
--    its person- and AI-made links wait in the trash row; its resources row is marked deleted, so no read serves it; a
--    mirror's row goes first (as 067's disconnect takes it), so a restore brings back an ordinary draft; one trashed
--    audit row each, with no principal (channel cli, the operator). The trash's retention (038, 14 days) is the grace:
--    brought back in that time (rhythm person unsuspend, or listed in people again), the person restores what they want
--    with a new token, as from any trip into the trash; otherwise the engine's daily purge deletes each for good, as it
--    deletes every memory past its retention.
-- 3. offboard_close: each private memory project of theirs that no memory is in any more, in the trash or out, is deleted
--    (its aliases with it; one deleted audit row, no words), and once nothing of their private memory data is left the
--    offboarding is recorded finished. The command closes what it can at once; the engine's daily turn closes the rest
--    once the purge has taken what waited in the trash (job offboarding, channel system). The daily turn starts nothing
--    and moves nothing to the trash.
-- No step changes anything once the person has been brought back (made current again: rhythm person unsuspend, or
-- listed in people again), whatever their status since: their offboarding records it (returned_at) and waits, while
-- they are current and once they are suspended or former again, until the operator runs it again, which shows what it
-- does and asks for the id again before it changes anything.
--
-- What it leaves: the Family memories they wrote, in Family with them as their author (an admin changes them; nobody takes
-- one back into a private scope no one reads, 048's no_author); their records from adapters and plugins, whose source is
-- the system of record, kept as it keeps them (with scopes on nobody reads them while the person is suspended or former);
-- their private insights and dashboards (027), which nobody reads with scopes on either; and both audit trails, which hold
-- no words. With memory.scopes off, every install's default, every
-- memory is the household's (D-048) and nothing is private to the person, so an offboarding ends their tokens, sessions,
-- membership and shares and moves nothing to the trash.
--
-- Nothing calls these but the operator's rhythm person offboard and, for an offboarding it started, the daily turn's close:
-- with no offboarding, every install's state, offboardings is empty and every answer is as before. The changes to
-- earlier tables are memory_trash's reason, which gains offboard, and a trigger on subjects that records a return on an
-- offboarding (with none, it changes nothing); the admin audit (037) gains two events, an offboarding started and
-- finished. Errors: P0002 not a person this install knows, or no offboarding of them under way; 22023 invalid
-- input. The number is provisional.

-- A trip into the trash an offboarding made says so.
DO $$
DECLARE c text;
BEGIN
 SELECT conname INTO c FROM pg_constraint WHERE conrelid='memory_trash'::regclass AND contype='c'
   AND pg_get_constraintdef(oid) LIKE '%''disconnect''%';
 IF c IS NULL THEN RAISE EXCEPTION 'memory_trash has no check naming its reasons'; END IF;
 EXECUTE format('ALTER TABLE memory_trash DROP CONSTRAINT %I',c);
END $$;
ALTER TABLE memory_trash ADD CONSTRAINT memory_trash_reason_check CHECK(reason IN ('forget','disconnect','mirror','offboard'));

-- One row a person the operator offboarded: when it started and when it was last run again, what it moved to the trash
-- and how many emptied projects it deleted, all runs together, when it finished (null while under way), and when the
-- person was brought back since the operator last ran it (null otherwise: running it again clears it). Ids and counts
-- only. Written only by the functions below (a checked table); the subject it names is never deleted.
CREATE TABLE offboardings(
 person_id text PRIMARY KEY,
 person_kind text NOT NULL DEFAULT 'person' CHECK(person_kind='person'),
 started_at timestamptz NOT NULL DEFAULT now(),
 resumed_at timestamptz,
 trashed integer NOT NULL DEFAULT 0 CHECK(trashed>=0),
 projects integer NOT NULL DEFAULT 0 CHECK(projects>=0),
 finished_at timestamptz,
 returned_at timestamptz,
 FOREIGN KEY(person_id,person_kind) REFERENCES subjects(id,kind),
 CHECK(resumed_at IS NULL OR resumed_at>=started_at),
 CHECK(finished_at IS NULL OR finished_at>=started_at),
 CHECK(returned_at IS NULL OR returned_at>=started_at)
);
REVOKE ALL ON offboardings FROM PUBLIC;

-- A person made current again (Principals.Unsuspend, or the start that finds someone former listed in people again) is
-- recorded on their offboarding, under way or finished, as brought back: from then on no step and no daily turn changes
-- anything of theirs, whatever their status becomes, until the operator runs the offboarding again (offboard_person
-- clears it). With no offboarding, every install's state, it changes nothing.
CREATE FUNCTION offboarding_returned() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 UPDATE offboardings SET returned_at=greatest(now(),started_at,coalesce(resumed_at,started_at)) WHERE person_id=NEW.id;
 RETURN NULL;
END $$;
CREATE TRIGGER subjects_offboarding_returned AFTER UPDATE OF status ON subjects FOR EACH ROW
 WHEN (NEW.kind='person' AND NEW.status='current' AND OLD.status IS DISTINCT FROM NEW.status)
 EXECUTE FUNCTION offboarding_returned();

-- The list of checked tables in force, with this one added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['offboardings']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The admin audit (037): an offboarding started (again: one finished before, or brought back since) and finished, as
-- the writer named itself (the operator, or the daily turn's job offboarding), with its counts.
CREATE FUNCTION admin_audit_offboardings() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   PERFORM admin_event_row('offboarding_started',NEW.person_id,NULL,'{}');
 ELSIF OLD.finished_at IS NOT NULL AND NEW.finished_at IS NULL THEN
   PERFORM admin_event_row('offboarding_started',NEW.person_id,NULL,jsonb_build_object('again',true));
 ELSIF OLD.finished_at IS NULL AND NEW.finished_at IS NOT NULL THEN
   PERFORM admin_event_row('offboarding_finished',NEW.person_id,NULL,jsonb_build_object('trashed',NEW.trashed,'projects',NEW.projects));
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER offboardings_audit AFTER INSERT OR UPDATE OF finished_at ON offboardings FOR EACH ROW EXECUTE FUNCTION admin_audit_offboardings();

-- The person an offboarding step names, with their offboarding under way, both locked for the step (the subject FOR
-- SHARE, so nobody brings them back meanwhile; the offboarding FOR UPDATE, so two runs take turns): whether it waits,
-- because they are current or were brought back since the operator last ran it. Unknown (P0002) for an id that is no
-- person this install knows, and for one with no offboarding under way.
CREATE FUNCTION offboarding_waits(p_person text) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE status text; returned timestamptz;
BEGIN
 SELECT s.status INTO status FROM subjects s WHERE s.id=p_person AND s.kind='person' FOR SHARE;
 SELECT o.returned_at INTO returned FROM offboardings o WHERE o.person_id=p_person AND o.finished_at IS NULL FOR UPDATE;
 IF status IS NULL OR NOT FOUND THEN
   RAISE EXCEPTION 'No offboarding of that person is under way' USING ERRCODE='P0002';
 END IF;
 RETURN status='current' OR returned IS NOT NULL;
END $$;

-- offboard_person: the first step, and the first of every run again. The caller names the operator for the admin audit
-- (AdminAudit.As), whose triggers record each change here: the suspension, each token disabled, leaving Family, each
-- share ended, and the offboarding started. Run again, it goes on with the same row and clears a return recorded on it:
-- the operator has decided again. Answers what the person was and is now, what ended, whether this started the
-- offboarding (false: it goes on with one started before), and the recorded mode.
CREATE FUNCTION offboard_person(p_person text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; was text; tokens integer; codes integer; shares integer; v_role text; o offboardings;
BEGIN
 scoped:=lock_memory_scopes();
 SELECT s.status INTO was FROM subjects s WHERE s.id=p_person AND s.kind='person' FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Not a person this install knows' USING ERRCODE='P0002'; END IF;
 IF was='current' THEN UPDATE subjects SET status='suspended' WHERE id=p_person AND kind='person'; END IF;
 UPDATE principals SET enabled=false WHERE person_id=p_person AND enabled;
 GET DIAGNOSTICS tokens=ROW_COUNT;
 DELETE FROM viewer_codes WHERE person_id=p_person AND used_at IS NULL;
 GET DIAGNOSTICS codes=ROW_COUNT;
 DELETE FROM memberships WHERE person_id=p_person RETURNING role INTO v_role;
 DELETE FROM source_shares WHERE person_id=p_person;
 GET DIAGNOSTICS shares=ROW_COUNT;
 INSERT INTO offboardings(person_id) VALUES(p_person)
 ON CONFLICT(person_id) DO UPDATE SET finished_at=NULL,resumed_at=greatest(now(),offboardings.started_at),returned_at=NULL
 RETURNING * INTO o;
 RETURN jsonb_build_object('person',p_person,'was',was,'status',CASE WHEN was='current' THEN 'suspended' ELSE was END,
   'tokens',tokens,'codes',codes,'family',v_role,'shares',shares,'started',o.resumed_at IS NULL,'started_at',o.started_at,
   'scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
END $$;

-- offboard_trash: at most p_limit (1 to 1000) of the person's private memories not in the trash yet, by id, each moved
-- there as the header says, while their offboarding is under way and does not wait (offboarding_waits: paused
-- otherwise, moving nothing). Locks in the writers' order: the recorded mode's row FOR SHARE (033), the person, the offboarding, then each
-- memory's resources row before its memories row (D-086). Answers how many it moved, how many are still left out of the
-- trash, and until when the trash keeps those it moved.
CREATE FUNCTION offboard_trash(p_person text,p_limit integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE target uuid; m memories; kept jsonb; moved integer:=0; outside bigint;
 until timestamptz:=now()+make_interval(days=>memory_trash_days());
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN
   RAISE EXCEPTION 'An offboarding moves 1 to 1000 memories a call' USING ERRCODE='22023';
 END IF;
 PERFORM lock_memory_scopes();
 IF offboarding_waits(p_person) THEN RETURN jsonb_build_object('paused',true,'trashed',0); END IF;
 FOR target IN SELECT x.id FROM memories x WHERE x.subject_id=p_person
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=x.id) ORDER BY x.id LIMIT p_limit LOOP
   PERFORM 1 FROM resources r WHERE r.id=target AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
   SELECT * INTO m FROM memories WHERE id=target FOR UPDATE;
   CONTINUE WHEN NOT FOUND OR m.subject_id IS DISTINCT FROM p_person OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id);
   SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.created_at,l.link_id),'[]'::jsonb) INTO kept
     FROM made_links l WHERE l.source_id=m.id OR l.target_id=m.id;
   DELETE FROM made_links WHERE source_id=m.id OR target_id=m.id;
   DELETE FROM memory_mirrors WHERE memory_id=m.id;
   UPDATE resources SET status='deleted' WHERE id=m.id AND provider='memory' AND relationship_snapshot IS NULL;
   INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after,links) VALUES(m.id,'offboard',NULL,'cli',until,kept);
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(m.id,m.subject_id,m.revision,'trashed',NULL,'cli',NULL,
     jsonb_build_object('reason','offboard','purge_after',until,'links',jsonb_array_length(kept)));
   moved:=moved+1;
 END LOOP;
 UPDATE offboardings SET trashed=trashed+moved WHERE person_id=p_person AND moved>0;
 SELECT count(*) INTO outside FROM memories x WHERE x.subject_id=p_person
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=x.id);
 RETURN jsonb_build_object('paused',false,'trashed',moved,'left',outside,'purge_after',CASE WHEN moved>0 THEN until END);
END $$;

-- offboard_close: each private memory project of the person's that no memory is in any more (in the trash or out) is
-- deleted with one deleted audit row (no principal: the operator's, channel cli, or with p_job the daily turn's, channel
-- system), and the offboarding is recorded finished once none of their memories is left, in the trash or out, and no
-- project of theirs: what is left waits for the trash's purge. Paused, changing nothing, while the offboarding waits
-- (offboarding_waits: they are current, or were brought back since the operator last ran it), whoever calls it. Answers
-- the projects it deleted, the memories still out of the trash and in it, the projects left, the latest date the trash
-- keeps one of theirs, and whether it is finished.
CREATE FUNCTION offboard_close(p_person text,p_job text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE v_channel text:=CASE WHEN p_job IS NULL THEN 'cli' ELSE 'system' END; p memory_projects; gone integer:=0;
 outside bigint; waiting bigint; projects bigint; until timestamptz; done boolean;
BEGIN
 IF p_job IS NOT NULL AND p_job !~ '^[a-z][a-z0-9_.-]{0,39}$' THEN
   RAISE EXCEPTION 'A job is a short lower-case name' USING ERRCODE='22023';
 END IF;
 PERFORM lock_memory_scopes();
 IF offboarding_waits(p_person) THEN RETURN jsonb_build_object('paused',true,'projects',0,'finished',false); END IF;
 FOR p IN SELECT * FROM memory_projects x WHERE x.subject_id=p_person
   AND NOT EXISTS(SELECT 1 FROM memories m WHERE m.project_id=x.id) ORDER BY x.id FOR UPDATE LOOP
   CONTINUE WHEN EXISTS(SELECT 1 FROM memories m WHERE m.project_id=p.id);
   DELETE FROM memory_projects WHERE id=p.id;
   INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,job,detail)
   VALUES(p.id,p.subject_id,p.revision,'deleted',NULL,v_channel,NULL,p_job,jsonb_build_object('reason','offboard','memories',0));
   gone:=gone+1;
 END LOOP;
 SELECT count(*) FILTER (WHERE t.memory_id IS NULL),count(t.memory_id),max(t.purge_after) INTO outside,waiting,until
   FROM memories x LEFT JOIN memory_trash t ON t.memory_id=x.id WHERE x.subject_id=p_person;
 SELECT count(*) INTO projects FROM memory_projects x WHERE x.subject_id=p_person;
 done:=outside=0 AND waiting=0 AND projects=0;
 UPDATE offboardings SET projects=offboardings.projects+gone,
   finished_at=CASE WHEN done THEN greatest(now(),started_at,coalesce(resumed_at,started_at)) END
 WHERE person_id=p_person;
 RETURN jsonb_build_object('paused',false,'projects',gone,'left',outside,'waiting',waiting,'projects_left',projects,
   'purge_after',until,'finished',done);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION admin_audit_offboardings() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION offboarding_returned() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION offboarding_waits(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION offboard_person(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION offboard_trash(text,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION offboard_close(text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),admin_audit_offboardings(),offboarding_returned(),offboarding_waits(text),
 offboard_person(text),offboard_trash(text,integer),offboard_close(text,text) FROM PUBLIC;
