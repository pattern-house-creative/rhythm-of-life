-- Typed links (SB-P4-01 in docs/dev/second-brain-parity/backlog.md; D-030, D-015, D-067; docs/dev/memory.md sections 8
-- and 9). Adapted from Second Brain's edge vocabulary (src/graph/types.ts and src/graph/edges.ts at the pinned commit,
-- MIT; NOTICE): its eight edge types, which of them has a direction, and the rule that `decided` and `follows` join two
-- episodic memories only.
--
-- 1. The vocabulary, for the checked functions that make a link (SB-P4-02 on): link_types() lists Second Brain's eight
--    types beside Rhythm's own, as the engine's LinkTypes.All does (LinkTypeTests holds them the same), and
--    link_kinds_allow() is the episodic rule: a type with kinds joins only memories of those kinds at both ends, and an end
--    of no kind (a memory not yet classified, or a record that is no memory) never qualifies. A link's type stays open
--    text, as in relationships (001) and made_links (025): a type not listed is a source's own word, allowed anywhere.
--    Nothing calls either function yet; no link a person or an AI client makes exists before SB-P4-02.
-- 2. What a person- or AI-made link carries beside its type, verification and evidence: a confidence from 0 to 1, the
--    revision of its source end it was made from, and when it was last checked against its sources (the map's links keep
--    the same three already: a strength and a source revision in relationships.details, and relationships.checked_at,
--    009). Each is empty until a writer states it; nothing writes one yet.
-- 3. A forgotten memory's links wait in its trash row with all their columns (038's forget keeps each row whole), so
--    restore_memory brings the three back with the link; and rhythm import carries them from an export that has them
--    (engine/src/Cli/MemoryArchive.cs writes each only when it is set, so an export with none reads as before).
--
-- Nothing an install answers changes: no link has any of the three columns set, the two functions are called by
-- nothing, and the restore and the import write what they wrote before for every link that has none. The number is
-- provisional.

CREATE FUNCTION link_types() RETURNS TABLE(name text,directed boolean,kinds text[],upstream boolean)
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
 VALUES
  ('relates_to',false,NULL::text[],true),
  ('supersedes',true,NULL,true),
  ('caused_by',true,NULL,true),
  ('decided',true,ARRAY['episodic'],true),
  ('about_person',true,NULL,true),
  ('part_of_project',true,NULL,true),
  ('follows',true,ARRAY['episodic'],true),
  ('drawn_from',true,NULL,true),
  ('references',true,NULL,false),
  ('in_project',true,NULL,false),
  ('in_folder',true,NULL,false),
  ('about',false,NULL,false),
  ('similar_to',false,NULL,false),
  ('copy_of',false,NULL,false),
  ('contains',true,NULL,false)
$$;

CREATE FUNCTION link_kinds_allow(p_type text,p_source_kind text,p_target_kind text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
 SELECT NOT EXISTS(SELECT 1 FROM link_types() t WHERE t.name=p_type AND t.kinds IS NOT NULL
   AND NOT (coalesce(p_source_kind=ANY(t.kinds),false) AND coalesce(p_target_kind=ANY(t.kinds),false)))
$$;

ALTER TABLE made_links
 ADD COLUMN confidence double precision CHECK(confidence BETWEEN 0 AND 1),
 ADD COLUMN source_revision text CHECK(length(source_revision) BETWEEN 1 AND 500),
 ADD COLUMN checked_at timestamptz;

-- 043's restore_memory, bringing back a kept link's confidence, source revision and checked time too (a link kept before
-- 051 has none); otherwise as it was.
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

-- 043's import_memory_link, carrying a link's confidence (a number from 0 to 1), source revision (a short text) and checked
-- time (a date) when its export has them, and refusing one whose any is of another shape as invalid; otherwise as it was.
CREATE OR REPLACE FUNCTION import_memory_link(p_person text,p_private boolean,p_family boolean,p_origin jsonb,p_link jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_id uuid; v_subject text; s memories; t memories; why text; v_kept uuid; v_link jsonb;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF jsonb_typeof(p_link) IS DISTINCT FROM 'object' OR jsonb_typeof(p_link->'subject') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_link->'trash','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_link->'confidence','null'::jsonb)) NOT IN ('number','null')
     OR jsonb_typeof(coalesce(p_link->'sourceRevision','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_link->'checkedAt','null'::jsonb)) NOT IN ('string','null') THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_link->>'id')::uuid; v_subject:=p_link->>'subject'; v_kept:=(p_link->>'trash')::uuid;
   IF v_id IS NULL THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,v_subject,NULL,scoped);
   IF why='not_named' THEN RETURN jsonb_build_object('outcome','not_named'); END IF;
   SELECT * INTO s FROM memories x WHERE x.id=(p_link->>'source')::uuid AND x.subject_id=v_subject;
   SELECT * INTO t FROM memories x WHERE x.id=(p_link->>'target')::uuid AND x.subject_id=v_subject;
   IF s.id IS NULL OR t.id IS NULL OR s.id=t.id OR v_kept IS NOT NULL AND v_kept NOT IN (s.id,t.id) THEN
     RETURN jsonb_build_object('outcome','refused','reason','ends');
   END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,s.subject_id,s.author_id,scoped);
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   IF EXISTS(SELECT 1 FROM made_links l WHERE l.link_id=v_id OR l.source_id=s.id AND l.target_id=t.id AND l.type=p_link->>'type')
     OR EXISTS(SELECT 1 FROM memory_trash x, jsonb_array_elements(x.links) e WHERE e->>'link_id'=v_id::text) THEN
     RETURN jsonb_build_object('outcome','present');
   END IF;
   IF memory_imported(p_origin,'link',v_id) THEN RETURN jsonb_build_object('outcome','imported_before'); END IF;
   v_link:=jsonb_build_object('link_id',v_id,'source_id',s.id,'target_id',t.id,'type',p_link->>'type',
     'verification',p_link->>'verification','evidence',coalesce(p_link->>'evidence',''),'lifecycle',p_link->>'lifecycle',
     'made_by',NULL,'imported',true,'created_at',(p_link->>'createdAt')::timestamptz,
     'confidence',(p_link->>'confidence')::double precision,'source_revision',p_link->>'sourceRevision',
     'checked_at',(p_link->>'checkedAt')::timestamptz);
   IF v_kept IS NOT NULL THEN
     -- Only into the trash row of a memory this same export brought in: an import never changes a row it found here.
     IF NOT EXISTS(SELECT 1 FROM memory_trash x WHERE x.memory_id=v_kept)
       OR NOT EXISTS(SELECT 1 FROM memory_origins o WHERE o.origin=p_origin->>'origin' AND o.kind='memory'
         AND o.source_id=v_kept::text AND o.export_id=(p_origin->>'export')::uuid) THEN
       RETURN jsonb_build_object('outcome','refused','reason','ends');
     END IF;
     -- The same checks made_links' columns would make.
     PERFORM 1 FROM jsonb_populate_record(NULL::made_links,v_link) l WHERE l.type ~ '^[a-z][a-z0-9_-]{0,63}$'
       AND l.verification IN ('verified','suggested','inferred') AND length(l.evidence)<=1000
       AND l.lifecycle IN ('active','stale','retired','revoked','deleted') AND l.created_at IS NOT NULL
       AND (l.confidence IS NULL OR l.confidence BETWEEN 0 AND 1)
       AND (l.source_revision IS NULL OR length(l.source_revision) BETWEEN 1 AND 500);
     IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused','reason','invalid'); END IF;
     UPDATE memory_trash SET links=links||jsonb_build_array(v_link) WHERE memory_id=v_kept;
   ELSE
     IF EXISTS(SELECT 1 FROM memory_trash x WHERE x.memory_id IN (s.id,t.id)) THEN
       RETURN jsonb_build_object('outcome','refused','reason','ends');
     END IF;
     INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,created_at,imported,
       confidence,source_revision,checked_at)
     SELECT l.link_id,l.source_id,l.target_id,l.type,l.verification,l.evidence,l.lifecycle,NULL,l.created_at,true,
       l.confidence,l.source_revision,l.checked_at
     FROM jsonb_populate_record(NULL::made_links,v_link) l;
   END IF;
   PERFORM memory_import_origin(p_origin,'link',v_id,v_subject);
   RETURN jsonb_build_object('outcome','added','id',v_id,'trashed',v_kept IS NOT NULL);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION link_types() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION link_kinds_allow(text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION restore_memory(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION import_memory_link(text,boolean,boolean,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION link_types(),link_kinds_allow(text,text,text),restore_memory(uuid,uuid,uuid,text,text,text),
 import_memory_link(text,boolean,boolean,jsonb,jsonb) FROM PUBLIC;
