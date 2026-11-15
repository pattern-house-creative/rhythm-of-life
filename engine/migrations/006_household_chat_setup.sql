-- One bounded write operation for the existing household chat connection.
-- Its database login receives EXECUTE only, never general table write privileges.
CREATE FUNCTION save_household_document(p_principal uuid,p_file text,p_title text,p_url text,
 p_content text,p_modified timestamptz,p_checked timestamptz,p_domain text,p_semantic boolean,p_chunks jsonb)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid; previous timestamptz; hash text; chunk jsonb; ordinal integer:=0;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM principals p JOIN grants g ON g.principal_id=p.id
   WHERE p.id=p_principal AND p.enabled AND g.subject_id='household'
     AND g.provider='household-documents' AND g.scope='shared') THEN
   RAISE EXCEPTION 'Household connection unavailable' USING ERRCODE='42501';
 END IF;
 IF p_file IS NULL OR p_file !~ '^[A-Za-z0-9_-]{1,200}$'
   OR p_title IS NULL OR length(trim(p_title)) NOT BETWEEN 1 AND 300
   OR p_content IS NULL OR length(trim(p_content)) NOT BETWEEN 1 AND 40000
   OR length(p_content)>40000 OR p_domain IS NULL OR p_domain !~ '^[a-z][a-z0-9-]{0,63}$'
   OR p_url IS NULL OR length(p_url)>1000
   OR p_url !~ ('^https://(drive\.google\.com/file/d/|docs\.google\.com/(document|spreadsheets|presentation)/d/)'||p_file||'(/[^?#]*)?([?][^#]*)?(#.*)?$')
   OR p_checked IS NULL OR p_checked<now()-interval '15 minutes' OR p_checked>now()+interval '1 minute'
   OR p_modified IS NULL OR p_modified>p_checked+interval '1 minute'
   OR p_semantic IS NULL OR p_chunks IS NULL OR jsonb_typeof(p_chunks)<>'array' THEN
   RAISE EXCEPTION 'Invalid household document snapshot' USING ERRCODE='22023';
 END IF;
 IF jsonb_array_length(p_chunks)>200 OR (p_semantic AND jsonb_array_length(p_chunks)=0)
   OR (NOT p_semantic AND jsonb_array_length(p_chunks)<>0) THEN
   RAISE EXCEPTION 'Invalid document chunks' USING ERRCODE='22023';
 END IF;
 -- Serialize with the existing operator importer; revisions cannot race backward.
 PERFORM pg_advisory_xact_lock(61984422);
 SELECT source_updated_at INTO previous FROM resources
   WHERE subject_id='household' AND provider='household-documents' AND external_id=p_file;
 IF previous>p_modified THEN RAISE EXCEPTION 'Older source revision' USING ERRCODE='22023'; END IF;
 hash:=encode(sha256(convert_to(p_content,'UTF8')),'hex');
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,source_url,content,revision,
   source_updated_at,checksum,metadata,status,domain)
 VALUES(gen_random_uuid(),'household','household-documents',p_file,'document','shared',p_title,p_url,p_content,hash,
   p_modified,hash,jsonb_build_object('canonicalProvider','drive','retrievalMode','dated-reference-snapshot',
     'checkedAt',p_checked,'semanticIndex',p_semantic,'archiveConsent','explicit-chat-document-save',
     'sourceFileId',p_file,'savedBy',p_principal,'snapshotTransport','chat-connector-text'),'current',p_domain)
 ON CONFLICT(subject_id,provider,external_id) DO UPDATE SET title=excluded.title,source_url=excluded.source_url,
   content=excluded.content,revision=excluded.revision,source_updated_at=excluded.source_updated_at,
   checksum=excluded.checksum,metadata=excluded.metadata,status='current',domain=excluded.domain,indexed_at=now()
 RETURNING id INTO saved;
 DELETE FROM resource_chunks WHERE resource_id=saved;
 FOR chunk IN SELECT value FROM jsonb_array_elements(p_chunks) LOOP
   IF coalesce(length(chunk->>'text'),0) NOT BETWEEN 1 AND 12000
     OR coalesce(length(chunk->>'model'),0) NOT BETWEEN 1 AND 100
     OR chunk->>'vector' IS NULL THEN
     RAISE EXCEPTION 'Invalid document chunk' USING ERRCODE='22023';
   END IF;
   INSERT INTO resource_chunks(resource_id,ordinal,source_revision,content_hash,model,text_content,embedding)
     VALUES(saved,ordinal,hash,encode(sha256(convert_to(chunk->>'text','UTF8')),'hex'),
       chunk->>'model',chunk->>'text',(chunk->>'vector')::public.vector(384));
   ordinal:=ordinal+1;
 END LOOP;
 RETURN saved;
END $$;
-- Capture the installation schema, with no caller-controlled or temporary-table lookup.
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION save_household_document(uuid,text,text,text,text,timestamptz,timestamptz,text,boolean,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION save_household_document(uuid,text,text,text,text,timestamptz,timestamptz,text,boolean,jsonb) FROM PUBLIC;
