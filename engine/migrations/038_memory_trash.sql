-- The trash for native memories (docs/dev/memory.md section 5: trash, restore, delete forever; SB-P3-06 in
-- docs/dev/second-brain-parity/backlog.md; D-019, D-036, D-065): forget a memory into the trash with its links, restore
-- it from there with its links, delete it forever from the app or the terminal with the trash row's nonce, never through
-- an AI client's channel, and a bounded purge of what has stayed in the trash past its retention. 025 made the trash
-- table; this migration adds its checked writers.
--
-- Adapted from Second Brain's trash (src/memory/trash.ts, trash-list.ts and cleanup.ts at the pinned commit, MIT): a
-- forgotten memory keeps its row, text and versions; restore is the trash's own undo, so it writes no version; every
-- step that consumes a trash row names its nonce, so a stale view never restores or deletes a row that has since been
-- purged, restored or trashed again; delete forever leaves the audit trail, which holds none of the memory's words; the
-- purge is bounded per run. Unlike upstream, the memory stays in place (its resources row marked deleted, so no read
-- serves it, 025), its person- and AI-made links (made_links, D-067) are what the trash row keeps to restore, and the map's
-- own links come back with the next pass, which lists the memory again.
--
-- Nothing calls these yet but tests and the engine's daily purge: no MCP tool, app tool, route or command trashes a
-- memory (SB-P3-09, SB-P3-12), so no install holds a trash row, the purge finds nothing, and every existing answer is as
-- before. With memory.scopes off, every install's default, any admitted harness or app login may trash and restore any
-- memory, today's model (D-048); with them on only those memory.md section 5 names.
--
-- Every writer takes the recorded mode's row FOR SHARE first (lock_memory_scopes, 033) and locks a memory's resources
-- row before its memories row (D-086), then its trash row.
--
-- Errors, as 033's: 42501 not available to this login or channel, or not this caller's to change; P0002 an unknown
-- memory, which is also the answer for one the caller does not read, and, for a restore or a delete forever, for one not
-- in the trash or a nonce that is not its trash row's; 22023 invalid input; 40001 a stale expected revision; 23505 an
-- idempotency key used before for another request. A refused write writes nothing. The number is provisional.

-- How long the trash keeps a memory before the purge deletes it for good: 14 days, as upstream (D-036; 025 holds every
-- trash row to 1 to 365). A setting later (CFG-14); a row keeps the retention it was trashed with.
CREATE FUNCTION memory_trash_days() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 14 $$;

-- A forget and a restore take an idempotency key as every other change does (033's memory_requests).
ALTER TABLE memory_requests DROP CONSTRAINT memory_requests_operation_check;
ALTER TABLE memory_requests ADD CONSTRAINT memory_requests_operation_check
 CHECK(operation IN ('create','append','update','set_status','forget','restore'));

-- The audit trail's system channel (VER-09, SB-P3-04's "not built"): the engine's own job writes with no principal and
-- names itself, as the admin audit does (037): a purge past retention is the job memory-trash's. Every other row keeps
-- 025's rule: a principal, or the operator on the host (channel cli).
DO $$
DECLARE c text;
BEGIN
 SELECT conname INTO c FROM pg_constraint WHERE conrelid='memory_events'::regclass AND contype='c'
   AND pg_get_constraintdef(oid) LIKE '%principal_id IS NOT NULL%';
 IF c IS NULL THEN RAISE EXCEPTION 'memory_events has no check naming who acts'; END IF;
 EXECUTE format('ALTER TABLE memory_events DROP CONSTRAINT %I',c);
END $$;
ALTER TABLE memory_events ADD COLUMN job text CHECK(job ~ '^[a-z][a-z0-9_.-]{0,39}$');
ALTER TABLE memory_events ADD CONSTRAINT memory_events_actor CHECK(principal_id IS NOT NULL OR channel IN ('cli','system'));
ALTER TABLE memory_events ADD CONSTRAINT memory_events_job CHECK((channel='system')=(job IS NOT NULL) AND (job IS NULL OR principal_id IS NULL));

-- Who may delete a memory forever (memory.md section 5): with scopes off, any admitted harness or app login, as for
-- every change (D-048); with them on, a private memory's owner, a Family memory's author or a Family admin, and a Family
-- memory no person wrote only an admin, stricter than its trash and restore (any reader of Family). The operator on the
-- host (no principal) may too; delete_memory_forever decides that before it asks here.
CREATE FUNCTION memory_may_delete(p_principal uuid,p_subject text,p_author text,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT p_scoped THEN principal_has_role(p_principal,ARRAY['harness','app'])
   WHEN p_subject='household' AND p_author IS NULL THEN reads_subject(p_principal,p_subject)
     AND EXISTS(SELECT 1 FROM principals c JOIN memberships m ON m.person_id=c.person_id AND m.role='admin' WHERE c.id=p_principal)
   ELSE memory_may_change(p_principal,p_subject,p_author,p_scoped) END
$$;

-- The external id of a memory's star row on the map (Relationships.MemoryStars: the catalog id of its key, account
-- memory and the memory's id), which a delete forever removes with the memory: its title is an excerpt of the words.
CREATE FUNCTION memory_star_external(p_memory uuid) RETURNS text LANGUAGE sql IMMUTABLE AS $$
 SELECT 'relationship-engine:["memory","'||p_memory::text||'"]'
$$;

-- What a trash write answers: the memory as memory_reply says it, and its trash row: the nonce that names it, when it was
-- trashed and when the purge will delete it, why, and how many links it keeps.
CREATE FUNCTION memory_trash_reply(m memories,t memory_trash,p_scoped boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT memory_reply(m,p_scoped)||jsonb_build_object('trash',jsonb_build_object('nonce',t.nonce,'reason',t.reason,
   'trashed_at',t.trashed_at,'purge_after',t.purge_after,'links',jsonb_array_length(t.links)))
$$;

-- The memory a restore or a delete forever names, in the trash, locked for it: its resources row, its memories row, then
-- its trash row. Unknown (P0002), as for one not in the trash at all, when there is none, when p_nonce is given and is not
-- its trash row's, or when a principal is given that does not read its subject. The caller checks who may act.
CREATE FUNCTION memory_in_trash(p_principal uuid,p_memory uuid,p_nonce uuid) RETURNS memory_trash LANGUAGE plpgsql AS $$
DECLARE m memories; t memory_trash;
BEGIN
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
 SELECT * INTO m FROM memories WHERE id=p_memory FOR UPDATE;
 IF FOUND THEN SELECT * INTO t FROM memory_trash WHERE memory_id=m.id FOR UPDATE; END IF;
 IF m.id IS NULL OR t.memory_id IS NULL OR p_nonce IS NOT NULL AND t.nonce<>p_nonce
   OR p_principal IS NOT NULL AND NOT reads_subject(p_principal,m.subject_id) THEN
   RAISE EXCEPTION 'No memory in the trash by that id' USING ERRCODE='P0002';
 END IF;
 RETURN t;
END $$;

-- Deletes a trashed memory for good, locked by its caller: the links at either end (made_links, D-067: a record a made
-- link names is deleted only once a checked function has removed them), the map's star rows for it (their titles are
-- excerpts of its words; their links go with them), the memory with its versions and trash row (025's cascades: the
-- links the trash row kept go too), then its resources row with its passages and anything else keyed to it. One audit
-- row, purged, with no words: why (permanent, or retention for the purge), when it was trashed and how many links and
-- versions went. The audit trail and the idempotency keys stay (they hold none of its words).
CREATE FUNCTION memory_purge(m memories,t memory_trash,p_principal uuid,p_channel text,p_client text,p_job text,p_why text)
RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE links integer; versions integer;
BEGIN
 DELETE FROM made_links WHERE source_id=m.id OR target_id=m.id;
 GET DIAGNOSTICS links=ROW_COUNT;
 links:=links+jsonb_array_length(t.links);
 SELECT count(*) INTO versions FROM memory_versions WHERE memory_id=m.id;
 DELETE FROM resources WHERE provider='memory' AND relationship_snapshot IS NOT NULL AND id<>m.id
   AND external_id=memory_star_external(m.id);
 DELETE FROM memories WHERE id=m.id;
 DELETE FROM resources WHERE id=m.id AND provider='memory';
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,job,detail)
 VALUES(m.id,m.subject_id,m.revision,'purged',p_principal,p_channel,p_client,p_job,jsonb_build_object('reason',p_why,
   'trash_reason',t.reason,'trashed_at',t.trashed_at,'purge_after',t.purge_after,'links',links,'versions',versions));
 RETURN jsonb_build_object('id',m.id,'subject',m.subject_id,'deleted',true,'links',links,'versions',versions);
END $$;

-- forget_memory: a memory into the trash (memory.md section 5: its owner; a Family one's author or an admin; one no person
-- wrote any reader of Family; with scopes off any admitted login), from any channel, an AI client's included: nothing is
-- deleted. It keeps its row, text, versions, revision and project in place; its resources row is marked deleted, so no
-- read, search, list, map or capsule serves it from this transaction on, and its passages go (005's trigger; the index
-- makes them again after a restore). Its person- and AI-made links leave made_links into the trash row, to come back
-- with it. Retention memory_trash_days(). A stale expected revision is refused, as for every change; an idempotency key
-- answers a retry again. One audit row, trashed, at its current revision (the trash changes no state, so it moves no
-- revision, as upstream).
CREATE FUNCTION forget_memory(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL,
 p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; t memory_trash; kept jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 hash:=memory_request_hash('forget',p_memory,'null'::jsonb,p_expected);
 replay:=memory_replay(p_principal,p_key,'forget',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.created_at,l.link_id),'[]'::jsonb) INTO kept
   FROM made_links l WHERE l.source_id=m.id OR l.target_id=m.id;
 DELETE FROM made_links WHERE source_id=m.id OR target_id=m.id;
 UPDATE resources SET status='deleted' WHERE id=m.id AND provider='memory' AND relationship_snapshot IS NULL;
 INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,purge_after,links)
 VALUES(m.id,'forget',p_principal,channel,now()+make_interval(days=>memory_trash_days()),kept) RETURNING * INTO t;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'trashed',p_principal,channel,p_client,
   jsonb_build_object('reason','forget','purge_after',t.purge_after,'links',jsonb_array_length(kept)));
 RETURN memory_answered(p_principal,p_key,'forget',hash,m.id,memory_trash_reply(m,t,scoped));
END $$;

-- restore_memory: a memory back from the trash (who may: as for its trash), with the links its trash row kept whose other
-- end is still in the catalog (a link to a memory still in the trash comes back too, and is served once both ends are);
-- one whose other end was deleted for good meanwhile is not, and the reply counts it. With p_nonce, only the trash row
-- with that nonce, so a stale view never restores a row trashed again since; without one, whatever row is there. The
-- memory comes back at the revision and state it had, held still if it was held (a restore is not a release); its
-- resources row is current again, so every read serves it at once and the index makes its passages again. No version is
-- written: the restore is the trash's own undo. One audit row, restored, at its revision.
CREATE FUNCTION restore_memory(p_principal uuid,p_memory uuid,p_nonce uuid,p_channel text,p_client text DEFAULT NULL,
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
 INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at)
 SELECT l.link_id,l.source_id,l.target_id,l.type,l.verification,l.evidence,l.lifecycle,l.made_by,l.created_at
 FROM jsonb_populate_recordset(NULL::made_links,t.links) l
 WHERE EXISTS(SELECT 1 FROM resources s WHERE s.id=l.source_id) AND EXISTS(SELECT 1 FROM resources x WHERE x.id=l.target_id)
   AND EXISTS(SELECT 1 FROM principals q WHERE q.id=l.made_by)
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

-- delete_memory_forever: a memory in the trash deleted for good (memory.md section 5, D-036): only from the app (the app's
-- login) or the terminal (a login bound to a person, or the operator on the host, p_principal null), never through an AI
-- client's channel (MCP); only with the trash row's nonce, so it works only on a memory forgotten first and on the trash
-- row the caller saw; and only with p_confirm equal to the memory's id, the confirmation the app or the terminal asked
-- for. Who: with scopes off any admitted harness or app login; with them on a private memory's owner, a Family one's
-- author or a Family admin, and a Family memory no person wrote only an admin (memory_may_delete); and the operator. What
-- goes and what stays: memory_purge. No idempotency key: a second delete answers as for an unknown memory.
CREATE FUNCTION delete_memory_forever(p_principal uuid,p_memory uuid,p_nonce uuid,p_confirm text,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; t memory_trash;
BEGIN
 IF p_principal IS NULL THEN
   IF p_channel IS DISTINCT FROM 'cli' THEN
     RAISE EXCEPTION 'Without a login, only the operator on the host deletes a memory forever (channel cli)' USING ERRCODE='42501';
   END IF;
   IF p_client IS NOT NULL AND (length(p_client) NOT BETWEEN 1 AND 80 OR p_client ~ '[[:cntrl:]]') THEN
     RAISE EXCEPTION 'A client name is 1 to 80 characters' USING ERRCODE='22023';
   END IF;
   channel:='cli';
 ELSE
   IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
     RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
   END IF;
   channel:=memory_channel(p_principal,p_channel,p_client);
   IF channel NOT IN ('app','cli') THEN
     RAISE EXCEPTION 'A memory is deleted forever only from the app or the terminal, never by an AI client: an AI client forgets it into the trash' USING ERRCODE='42501';
   END IF;
 END IF;
 scoped:=lock_memory_scopes();
 IF p_nonce IS NULL THEN
   RAISE EXCEPTION 'Deleting forever needs the trash row''s nonce: forget the memory first, then delete it from the trash' USING ERRCODE='22023';
 END IF;
 IF p_confirm IS DISTINCT FROM p_memory::text THEN
   RAISE EXCEPTION 'To delete a memory forever, confirm with its id' USING ERRCODE='22023';
 END IF;
 t:=memory_in_trash(p_principal,p_memory,p_nonce);
 SELECT * INTO m FROM memories WHERE id=t.memory_id;
 IF p_principal IS NOT NULL AND NOT memory_may_delete(p_principal,m.subject_id,m.author_id,scoped) THEN
   RAISE EXCEPTION 'Only this memory''s author or a Family admin may delete it forever' USING ERRCODE='42501';
 END IF;
 RETURN memory_purge(m,t,p_principal,channel,p_client,NULL,'permanent')||jsonb_build_object('scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
END $$;

-- purge_memory_trash: the engine's daily job (the scheduler, MemoryTrash.Purge): at most p_limit (1 to 1000) memories
-- whose trash retention has passed, oldest first, each deleted for good as memory_purge says and audited as the job
-- memory-trash's (channel system, no principal). Only rows past their retention: a memory restored, or trashed again
-- with a later purge date, after the candidates were read is left, since each is checked again under its locks, and one
-- a writer holds right now is left for the next run. A memory whose deletion fails is counted and left in the trash, and
-- the rest go on. Answers how many went, failed and were skipped.
CREATE FUNCTION purge_memory_trash(p_limit integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE c record; m memories; t memory_trash; purged integer:=0; failed integer:=0; skipped integer:=0;
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN
   RAISE EXCEPTION 'A purge takes 1 to 1000 memories' USING ERRCODE='22023';
 END IF;
 FOR c IN SELECT x.memory_id FROM memory_trash x WHERE x.purge_after<=now() ORDER BY x.purge_after,x.memory_id LIMIT p_limit LOOP
   BEGIN
     PERFORM 1 FROM resources r WHERE r.id=c.memory_id AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE SKIP LOCKED;
     IF NOT FOUND THEN skipped:=skipped+1; CONTINUE; END IF;
     SELECT * INTO m FROM memories WHERE id=c.memory_id FOR UPDATE SKIP LOCKED;
     IF NOT FOUND THEN skipped:=skipped+1; CONTINUE; END IF;
     SELECT * INTO t FROM memory_trash WHERE memory_id=c.memory_id AND purge_after<=now() FOR UPDATE SKIP LOCKED;
     IF NOT FOUND THEN skipped:=skipped+1; CONTINUE; END IF;
     PERFORM memory_purge(m,t,NULL,'system',NULL,'memory-trash','retention');
     purged:=purged+1;
   EXCEPTION WHEN OTHERS THEN failed:=failed+1;
   END;
 END LOOP;
 RETURN jsonb_build_object('purged',purged,'failed',failed,'skipped',skipped);
END $$;

-- prune_memory_requests: idempotency keys older than 30 days, at most p_limit (1 to 10000) of them, oldest first (D-148:
-- kept until the trash's purge job prunes them). A key only answers a retry, and no client retries a month on; the
-- keys hold ids, revisions and scopes, never a memory's words.
CREATE FUNCTION prune_memory_requests(p_limit integer) RETURNS integer LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE gone integer;
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 10000 THEN
   RAISE EXCEPTION 'A prune takes 1 to 10000 keys' USING ERRCODE='22023';
 END IF;
 DELETE FROM memory_requests q WHERE (q.principal_id,q.idempotency_key) IN (
   SELECT x.principal_id,x.idempotency_key FROM memory_requests x WHERE x.received_at<now()-interval '30 days'
   ORDER BY x.received_at LIMIT p_limit);
 GET DIAGNOSTICS gone=ROW_COUNT;
 RETURN gone;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_trash_days() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_may_delete(uuid,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_star_external(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_trash_reply(memories,memory_trash,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_in_trash(uuid,uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_purge(memories,memory_trash,uuid,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION forget_memory(uuid,uuid,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION restore_memory(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION delete_memory_forever(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION purge_memory_trash(integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION prune_memory_requests(integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_trash_days(),memory_may_delete(uuid,text,text,boolean),memory_star_external(uuid),
 memory_trash_reply(memories,memory_trash,boolean),memory_in_trash(uuid,uuid,uuid),
 memory_purge(memories,memory_trash,uuid,text,text,text,text),forget_memory(uuid,uuid,integer,text,text,text),
 restore_memory(uuid,uuid,uuid,text,text,text),delete_memory_forever(uuid,uuid,uuid,text,text,text),
 purge_memory_trash(integer),prune_memory_requests(integer) FROM PUBLIC;
