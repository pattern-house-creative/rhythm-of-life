-- Agent ticket updates are checked with the same row locked by the normal writers.
-- Replay, authorization and revision checks precede the guard. A refused write
-- consumes no version, idempotency key or hold. Ordinary memory screening still
-- applies to every accepted write, including a compact replacement.
CREATE FUNCTION check_work_text_size(m memories,channel text,bytes integer) RETURNS void
LANGUAGE plpgsql STABLE AS $$
BEGIN
 IF channel='mcp' AND bytes>32768 AND EXISTS(SELECT 1 FROM memory_work WHERE memory_id=m.id) THEN
   IF m.hold IS NOT NULL THEN
     RAISE EXCEPTION 'This work item is held for review. A person can review it in Memories.'
       USING ERRCODE='22023',HINT='held';
   END IF;
   RAISE EXCEPTION 'Ticket unchanged. The proposed text is % bytes (limit 32768). Read the current ticket with get. Fold the pending update into a concise summary, preserving its scope, decisions, open work and required check lines with evidence. Save with update(id, content, expected_revision) using the revision read. On conflict, re-read and reconcile.',bytes
     USING ERRCODE='22023',HINT='work_summary_required';
 END IF;
END $$;
REVOKE ALL ON FUNCTION check_work_text_size(memories,text,integer) FROM PUBLIC;

CREATE OR REPLACE FUNCTION append_memory(p_principal uuid,p_memory uuid,p_text text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; addition text; whole text; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 hash:=memory_request_hash('append',p_memory,to_jsonb(p_text),p_expected);
 replay:=memory_replay(p_principal,p_key,'append',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 addition:=memory_check_text(btrim(p_text,E' \t\r\n'));
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 whole:=m.content||E'\n\n[Update '||to_char(install_today(),'YYYY-MM-DD')||']: '||addition;
 PERFORM check_work_text_size(m,channel,octet_length(whole));
 IF octet_length(whole)>131072 THEN
   RAISE EXCEPTION 'Not added: the memory would be over 131072 bytes; save the new text as a memory of its own' USING ERRCODE='54000';
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET content=whole,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client)
 VALUES(m.id,m.subject_id,m.revision,'appended',p_principal,channel,p_client);
 result:=memory_reply(m,scoped);
 RETURN memory_answered(p_principal,p_key,'append',hash,m.id,result);
END $$;

CREATE OR REPLACE FUNCTION update_memory(p_principal uuid,p_memory uuid,p_change jsonb,p_expected integer,p_channel text,
 p_client text DEFAULT NULL,p_key text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; hash text; replay jsonb; m memories; n memories; person text; project jsonb;
 changed text[]; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_change IS NULL OR jsonb_typeof(p_change)<>'object' OR p_change='{}'::jsonb
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_change) k WHERE k NOT IN ('content','tags','hashtags','kind','importance','volatility','project'))
   OR p_change ? 'content' AND jsonb_typeof(p_change->'content')<>'string'
   OR p_change ? 'tags' AND jsonb_typeof(p_change->'tags')<>'array'
   OR p_change ? 'hashtags' AND jsonb_typeof(p_change->'hashtags')<>'array'
   OR p_change ? 'kind' AND jsonb_typeof(p_change->'kind')<>'null' AND p_change->>'kind' NOT IN ('episodic','semantic')
   OR p_change ? 'importance' AND jsonb_typeof(p_change->'importance')<>'null'
     AND (jsonb_typeof(p_change->'importance')<>'number' OR p_change->>'importance' !~ '^[1-5]$')
   OR p_change ? 'volatility' AND jsonb_typeof(p_change->'volatility')<>'null' AND p_change->>'volatility' NOT IN ('durable','state','volatile')
   OR p_change ? 'project' AND jsonb_typeof(p_change->'project') NOT IN ('null','string') THEN
   RAISE EXCEPTION 'A change names content, tags, kind, importance, volatility or project' USING ERRCODE='22023';
 END IF;
 hash:=memory_request_hash('update',p_memory,p_change,p_expected);
 replay:=memory_replay(p_principal,p_key,'update',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 IF p_change ? 'content' THEN PERFORM memory_check_text(p_change->>'content'); END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 n:=m;
 IF p_change ? 'content' THEN
   n.content:=p_change->>'content';
   IF n.content IS DISTINCT FROM m.content THEN
     PERFORM check_work_text_size(m,channel,octet_length(n.content));
   END IF;
 END IF;
 IF p_change ? 'tags' THEN n.tags:=memory_strings(p_change->'tags'); END IF;
 IF p_change ? 'hashtags' THEN
   n.tags:=n.tags||ARRAY(SELECT h FROM unnest(memory_strings(p_change->'hashtags')) WITH ORDINALITY x(h,i)
     WHERE NOT h=ANY(n.tags) ORDER BY i);
 END IF;
 n.tags:=memory_check_tags(n.tags);
 IF p_change ? 'kind' THEN n.kind:=p_change->>'kind'; END IF;
 IF p_change ? 'importance' THEN n.importance:=(p_change->>'importance')::smallint; END IF;
 IF p_change ? 'volatility' THEN n.volatility:=p_change->>'volatility'; END IF;
 IF p_change ? 'project' THEN
   IF p_change->>'project' IS NULL THEN n.project_id:=NULL;
   ELSE
     SELECT person_id INTO person FROM principals WHERE id=p_principal;
     project:=memory_project(p_principal,CASE WHEN m.subject_id='household' THEN person ELSE m.subject_id END,m.subject_id,
       p_change->>'project',channel,p_client);
     n.project_id:=(project->>'id')::uuid;
   END IF;
 END IF;
 changed:=ARRAY(SELECT f FROM (VALUES
   ('content',n.content IS DISTINCT FROM m.content),('tags',n.tags IS DISTINCT FROM m.tags),
   ('kind',n.kind IS DISTINCT FROM m.kind),('importance',n.importance IS DISTINCT FROM m.importance),
   ('volatility',n.volatility IS DISTINCT FROM m.volatility),('project',n.project_id IS DISTINCT FROM m.project_id)) c(f,d) WHERE d);
 IF cardinality(changed)=0 THEN
   RETURN memory_answered(p_principal,p_key,'update',hash,m.id,memory_reply(m,scoped)||jsonb_build_object('unchanged',true));
 END IF;
 PERFORM memory_retire(m);
 UPDATE memories SET content=n.content,tags=n.tags,kind=n.kind,importance=n.importance,volatility=n.volatility,
   project_id=n.project_id,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'updated',p_principal,channel,p_client,jsonb_build_object('fields',to_jsonb(changed)));
 result:=memory_reply(m,scoped)||jsonb_build_object('changed',to_jsonb(changed))
   ||CASE WHEN coalesce((project->>'created')::boolean,false) THEN jsonb_build_object('project',jsonb_build_object('id',project->'id','slug',project->'slug','created',true)) ELSE '{}'::jsonb END;
 RETURN memory_answered(p_principal,p_key,'update',hash,m.id,result);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION append_memory(uuid,uuid,text,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION update_memory(uuid,uuid,jsonb,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION append_memory(uuid,uuid,text,integer,text,text,text),update_memory(uuid,uuid,jsonb,integer,text,text,text) FROM PUBLIC;
