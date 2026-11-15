-- Where a save_document copy lands with memory.scopes on (docs/dev/memory.md section 5, Where writes land; SB-P1-09).
-- 017's save_document took any person or the household from any harness login: with scopes on, one person's AI client
-- could write into another person's private scope, rewrite a copy there, and learn from 'Older source revision'
-- whether that person held a copy with a given file id and how recent it was. Replaced with the same signature and its
-- search path pinned again. With scopes on, a copy lands in the caller's own person's scope or, for a caller that reads
-- Family, the household's; any other subject is refused (42501) before anything is read, so a refusal says nothing about
-- what the other person holds. A subject left out (null) is the caller's own person when it has one, else the
-- household's.
--
-- With scopes off, every install's default, nothing changes: every harness login saves for any person or the
-- household, as before, and a subject left out is the household's, the tool's default before. An engine of the release
-- before always names the subject, so it is answered as it was. No data step. The number is provisional.
CREATE OR REPLACE FUNCTION save_document(p_principal uuid,p_subject text,p_external text,p_title text,p_url text,
 p_content text,p_modified timestamptz,p_domain text,p_semantic boolean,p_chunks jsonb)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid; previous timestamptz; hash text; chunk jsonb; ordinal integer:=0; scoped boolean:=scopes_on();
 person text; landed text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving documents');
 SELECT person_id INTO person FROM principals WHERE id=p_principal;
 landed:=coalesce(p_subject,CASE WHEN scoped AND person IS NOT NULL THEN person ELSE 'household' END);
 IF NOT EXISTS(SELECT 1 FROM subjects WHERE id=landed AND kind IN ('person','household')) THEN
   RAISE EXCEPTION 'Unknown subject' USING ERRCODE='22023';
 END IF;
 -- Before anything reads the subject's rows: another person's scope, Family for a caller that does not read it, and a
 -- caller whose own person reads nothing (suspended or former) are refused alike.
 IF scoped AND (landed<>'household' AND landed IS DISTINCT FROM person OR NOT reads_subject(p_principal,landed)) THEN
   RAISE EXCEPTION 'A document is saved only for your own person or, as a Family member, the household' USING ERRCODE='42501';
 END IF;
 IF p_external IS NULL OR p_external !~ '^[A-Za-z0-9_.:-]{1,200}$'
   OR p_title IS NULL OR length(trim(p_title)) NOT BETWEEN 1 AND 300
   OR p_content IS NULL OR length(trim(p_content)) NOT BETWEEN 1 AND 40000 OR length(p_content)>40000
   OR p_domain IS NULL OR p_domain !~ '^[a-z][a-z0-9-]{0,63}$'
   OR (p_url IS NOT NULL AND (length(p_url)>2000 OR p_url !~ '^https://[^\s@/]+(/\S*)?$'))
   OR p_modified IS NULL OR p_modified>now()+interval '5 minutes'
   OR p_semantic IS NULL OR p_chunks IS NULL OR jsonb_typeof(p_chunks)<>'array' THEN
   RAISE EXCEPTION 'Invalid document' USING ERRCODE='22023';
 END IF;
 IF jsonb_array_length(p_chunks)>200 OR (NOT p_semantic AND jsonb_array_length(p_chunks)<>0) THEN
   RAISE EXCEPTION 'Invalid document chunks' USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(61984422);
 SELECT source_updated_at INTO previous FROM resources WHERE subject_id=landed AND provider='documents' AND external_id=p_external;
 IF previous>p_modified THEN RAISE EXCEPTION 'Older source revision' USING ERRCODE='22023'; END IF;
 hash:=encode(sha256(convert_to(p_content,'UTF8')),'hex');
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,content,revision,
   source_updated_at,checksum,metadata,status,domain)
 VALUES(gen_random_uuid(),landed,'documents',p_external,'document',CASE WHEN landed='household' THEN 'shared' ELSE 'private' END,
   p_title,p_url,p_content,hash,p_modified,hash,jsonb_build_object('retrievalMode','dated-snapshot','semanticIndex',p_semantic,
     'archiveConsent','explicit-save','savedBy',p_principal),'current',p_domain)
 ON CONFLICT(subject_id,provider,external_id) DO UPDATE SET title=excluded.title,source_url=excluded.source_url,
   content=excluded.content,revision=excluded.revision,source_updated_at=excluded.source_updated_at,
   checksum=excluded.checksum,metadata=excluded.metadata,status='current',domain=excluded.domain,indexed_at=now()
 RETURNING id INTO saved;
 DELETE FROM resource_chunks WHERE resource_id=saved;
 FOR chunk IN SELECT value FROM jsonb_array_elements(p_chunks) LOOP
   IF coalesce(length(chunk->>'text'),0) NOT BETWEEN 1 AND 12000
     OR coalesce(length(chunk->>'model'),0) NOT BETWEEN 1 AND 100 OR chunk->>'vector' IS NULL THEN
     RAISE EXCEPTION 'Invalid document chunk' USING ERRCODE='22023';
   END IF;
   INSERT INTO resource_chunks(resource_id,ordinal,source_revision,content_hash,model,text_content,embedding)
     VALUES(saved,ordinal,hash,encode(sha256(convert_to(chunk->>'text','UTF8')),'hex'),
       chunk->>'model',chunk->>'text',(chunk->>'vector')::public.vector(384));
   ordinal:=ordinal+1;
 END LOOP;
 RETURN saved;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION save_document(uuid,text,text,text,text,text,timestamptz,text,boolean,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION save_document(uuid,text,text,text,text,text,timestamptz,text,boolean,jsonb) FROM PUBLIC;
