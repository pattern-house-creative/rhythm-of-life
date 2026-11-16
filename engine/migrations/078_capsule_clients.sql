-- A capsule slot set by an AI client, where the install lets it (memory.capsuleWrites, off by default;
-- docs/dev/memory.md sections 5 and 7.1). 039's set_memory_capsule refused an AI client's channel (mcp) outright: a
-- slot was a person's step, taken in the app or at the terminal. That stays what every install does until its
-- settings say otherwise. With memory.capsuleWrites on, the engine's set_capsule tool passes p_clients true, and the
-- writer then takes a slot through that channel as it takes one from a person: the author lock, an expected revision,
-- a version of the state it replaces and one audit row (capsule_changed, channel mcp, the client's name), so the
-- change is in the memory's history and among the brief's changes by AI clients, and a person can undo it.
--
-- p_clients is the caller's word, as the channel is (039; memory.md section 5): only the engine's login runs this
-- function, and the engine passes true only from the tool it makes with the setting on. Every other caller (the app,
-- the terminal, update's capsule tags) leaves it false, and is answered as before.
--
-- Through an AI client's channel a slot another memory fills is not taken twice: the writer refuses, naming the memory
-- that fills it, so the client takes that one out first and nothing is left for the capsule to report as a duplicate
-- slot. "Fills" is any other memory of the scope in that slot (for a project slot, of that project) that is not in the
-- trash, not marked wrong and not past its validity: a draft or held one counts, since it would fill the slot once
-- confirmed or released. A person's channel is not checked, as before: the capsule reports a slot two memories fill
-- and serves neither. Two clients setting one slot at the same moment can both pass; the capsule then reports it.
--
-- 041's undo is unchanged: an undo through an AI client's channel that would change a slot stays refused, so a client
-- reverses a slot it set by setting it again, and a person undoes it from the app or the terminal.
--
-- The function is dropped and made again because it takes one more argument; nothing else calls it by its old list.
-- Errors as 039's, and 22023 for a slot already filled.

DROP FUNCTION set_memory_capsule(uuid,uuid,text,text,integer,text,text);

CREATE FUNCTION set_memory_capsule(p_principal uuid,p_memory uuid,p_capsule text,p_slot text,p_expected integer,p_channel text,
 p_client text DEFAULT NULL,p_clients boolean DEFAULT false) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; was jsonb; other uuid;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel='mcp' AND NOT coalesce(p_clients,false) THEN
   RAISE EXCEPTION 'A capsule slot is set or cleared by a person, from the app or the terminal, never by an AI client'
     USING ERRCODE='42501';
 END IF;
 IF (p_capsule IS NULL)<>(p_slot IS NULL)
   OR p_capsule IS NOT NULL AND NOT (p_capsule='core' AND p_slot IN ('identity','preferences','constraints','principles')
     OR p_capsule='project' AND p_slot IN ('current-state','decisions','open-questions')) THEN
   RAISE EXCEPTION 'A capsule slot is core identity, preferences, constraints or principles, or project current-state, decisions or open-questions; or none to take the memory out of its slot'
     USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.capsule IS NOT DISTINCT FROM p_capsule AND m.capsule_slot IS NOT DISTINCT FROM p_slot THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot,'unchanged',true);
 END IF;
 IF p_capsule='project' AND m.project_id IS NULL THEN
   RAISE EXCEPTION 'A project slot is in its project''s capsule: give the memory a project first' USING ERRCODE='22023';
 END IF;
 IF channel='mcp' AND p_capsule IS NOT NULL THEN
   SELECT o.id INTO other FROM memories o
   WHERE o.subject_id=m.subject_id AND o.id<>m.id AND o.capsule=p_capsule AND o.capsule_slot=p_slot
     AND (p_capsule<>'project' OR o.project_id=m.project_id)
     AND o.status<>'deprecated' AND (o.valid_until IS NULL OR o.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=o.id)
   ORDER BY o.id LIMIT 1;
   IF other IS NOT NULL THEN
     RAISE EXCEPTION 'That capsule slot is filled by memory %: take that one out of its slot first, then set this one',other
       USING ERRCODE='22023';
   END IF;
 END IF;
 was:=jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot);
 PERFORM memory_retire(m);
 UPDATE memories SET capsule=p_capsule,capsule_slot=p_slot,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'capsule_changed',p_principal,channel,p_client,
   jsonb_build_object('fields',jsonb_build_array('capsule'),'from',was,'to',jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot)));
 RETURN memory_reply(m,scoped)||jsonb_build_object('capsule',m.capsule,'slot',m.capsule_slot,'changed',jsonb_build_array('capsule'));
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION set_memory_capsule(uuid,uuid,text,text,integer,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION set_memory_capsule(uuid,uuid,text,text,integer,text,text,boolean) FROM PUBLIC;
