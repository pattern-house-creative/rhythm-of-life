-- A person's keep both on a possible copy, checked against what they read of every memory it is close to (SB-P6-01's copies
-- queue in docs/dev/second-brain-parity/backlog.md; D-233; docs/dev/memory.md section 5). A keep both through a person's channel
-- settles a pair of them that also waits as a possible contradiction (068's clear_memory_duplicates), after which an AI client
-- neither ends nor replaces either while the other stands, and nothing takes it back. 071's resolve_memory_item checks the
-- revision read of the flagged memory alone, so a memory it is close to that changed after the person read it, or that the list
-- shows now and the person never read, was settled all the same (run 21's fix review, the part D-233 left "not taken").
--
-- resolve_memory_copy takes, beside the flagged memory's revision, each memory it is close to at the revision read (p_copies,
-- 1 to 3 of {id, revision}: the duplicate review shows at most 3 at a time, 068's memory_duplicate_copies). While the memory waits
-- in the copies queue, every memory the review shows it close to now must be named there at the revision it has now, else
-- nothing changes (40001, as a stale revision): one changed since it was read, or one shown now that was not read. A memory
-- named that the review no longer shows is passed over, since its flag is not taken off. Then it is 071's resolve_memory_item
-- with keep_copy, unchanged: its refusals, its once and its answer.
--
-- Locks, in the order the steps take them: the scopes switch, the flagged memory (033's memory_for_change, which refuses an
-- unknown memory, one the caller may not change and a stale revision first, as resolve does); then every memory its flags name,
-- in id order, and its contradiction rows, shared, so what the review shows cannot change before the flags come off: a check of
-- one of them (which would rescore a flag or add one), its trash, hold or move, and a proposal naming a pair all wait.
--
-- No table, no audit event and no earlier function changes; resolve_memory_item and clear_memory_duplicates answer as before for
-- every caller that does not name the copies (an AI client's resolve, an older app, the terminal without --copy). With
-- memory.tools off, the default, nothing calls it. The number is provisional until it is merged into main.
--
-- Errors, as resolve_memory_item's, and 22023 for copies not 1 to 3 of {id, revision} with distinct ids.

CREATE FUNCTION resolve_memory_copy(p_principal uuid,p_memory uuid,p_expected integer,p_copies jsonb,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; m memories; c record; v_read integer;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 IF (CASE WHEN p_copies IS NULL OR jsonb_typeof(p_copies)<>'array' THEN true ELSE jsonb_array_length(p_copies) NOT BETWEEN 1 AND 3
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_copies) AS x(item) WHERE CASE WHEN jsonb_typeof(x.item)<>'object' THEN true
     ELSE (SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(x.item) k)<>ARRAY['id','revision']
       OR jsonb_typeof(x.item->'id')<>'string'
       OR (x.item->>'id') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
       OR jsonb_typeof(x.item->'revision')<>'number'
       OR CASE WHEN (x.item->>'revision') ~ '^[1-9][0-9]{0,9}$' THEN (x.item->>'revision')::bigint>2147483647 ELSE true END END)
   OR (SELECT count(DISTINCT x.item->>'id') FROM jsonb_array_elements(p_copies) AS x(item))<>jsonb_array_length(p_copies) END) THEN
   RAISE EXCEPTION 'A keep both names each memory it is close to at the revision read: 1 to 3 of {id, revision}, each memory once'
     USING ERRCODE='22023';
 END IF;
 scoped:=lock_memory_scopes();
 PERFORM memory_channel(p_principal,p_channel,p_client);
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NULL AND m.status<>'deprecated' AND memory_resolve_waiting(m.id,'keep_copy') THEN
   PERFORM 1 FROM memories x WHERE x.id IN (SELECT f.copy_id FROM memory_duplicate_flags f WHERE f.memory_id=m.id) ORDER BY x.id FOR SHARE;
   PERFORM 1 FROM memory_contradictions k WHERE k.memory_id=m.id OR k.conflict_id=m.id ORDER BY k.id FOR SHARE;
   FOR c IN SELECT x.id,x.revision FROM memory_duplicate_copies(m.id) d JOIN memories x ON x.id=d.copy_id ORDER BY x.id LOOP
     v_read:=(SELECT (e.item->>'revision')::integer FROM jsonb_array_elements(p_copies) AS e(item) WHERE (e.item->>'id')::uuid=c.id);
     IF v_read IS NULL THEN
       RAISE EXCEPTION 'Memory % is close to this one and was not read whole for this keep both, so nothing changed: read it, then keep both again',c.id
         USING ERRCODE='40001';
     END IF;
     IF v_read<>c.revision THEN
       RAISE EXCEPTION 'Memory % that this one is close to changed since revision % was read, so nothing changed: read it again, then keep both again',c.id,v_read
         USING ERRCODE='40001';
     END IF;
   END LOOP;
 END IF;
 RETURN resolve_memory_item(p_principal,p_memory,'keep_copy',NULL,NULL,p_expected,p_channel,p_client);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION resolve_memory_copy(uuid,uuid,integer,jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION resolve_memory_copy(uuid,uuid,integer,jsonb,text,text) FROM PUBLIC;
