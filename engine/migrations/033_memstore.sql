-- The native memory store's checked writers (docs/dev/memory.md sections 2, 5, 6.2 and 10; SB-P3-01 and SB-P3-02 in
-- docs/dev/second-brain-parity/backlog.md): create a memory, append to it, update it and set its status, each with an
-- optional expected revision and an optional idempotency key, within the size limits and validation of D-042 and the
-- author lock of memory.md section 5. Reads (get, list and filter) go through the one read predicate in the engine
-- (Core/Memories.cs, Database.Visible on the memory's resources row); nothing here reads for a caller.
--
-- Nothing calls these functions yet: no MCP tool, app tool or route writes a memory until a later phase adds one (the
-- tools of SB-P3-09 and SB-P3-12, after the holds of SB-P3-18, D-050), so native memories stay inert for every install
-- and every existing answer is as before. With memory.scopes off, every install's default, a memory is a household
-- memory that everyone admitted reads, a private one is refused (D-048), and every admitted harness and app login may
-- change any memory, today's model; with scopes on a memory lands in its writer's private scope unless the call names
-- Family (D-021, D-073) and only those section 5 names change it.
--
-- Every writer here takes the recorded mode's row (install_settings memory.scopes) FOR SHARE in its own transaction
-- before it writes anything, and reads the mode from that locked row: rhythm scopes off takes the row FOR UPDATE before
-- it checks that nothing private exists (Principals.SwitchOff), so the two serialise, and no private memory, version or
-- project lands between that check and the switch (memory.md 6.2). Every writer that changes a memory locks its
-- resources row before its memories row, the order a share takes through 025's cascade (D-086, 026's trigger).
--
-- Errors: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, which is also
-- the answer for one the caller does not read or one in the trash; 22023 invalid input; 54000 text over the limit;
-- 40001 a stale expected revision (its message names the current one); 23505 an idempotency key used before for another
-- request. A refused write writes nothing. The number is provisional.

-- A source share covers one adapter's or plugin's provider, never native memories (memory.md section 4), so the read
-- predicate in the engine (Database.Allowed, which matches a share's provider) and these writers (reads_subject, which
-- takes no share) always agree on who reads a memory. rhythm share add refuses the name already; this holds it for a
-- hand-made row too.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM source_shares WHERE provider='memory') THEN
   RAISE EXCEPTION 'A source share names the provider memory, which native memories reserve; revoke it (rhythm share revoke) first';
 END IF;
END $$;
ALTER TABLE source_shares ADD CONSTRAINT source_shares_not_memories CHECK(provider<>'memory');

-- Idempotency keys (VER-02): a retried write with the same key and the same request applies once and answers what it
-- answered the first time, marked replayed; the same key with another request is refused. Keys are the caller's own:
-- one principal's key never answers another's. A row holds the request's hash and the answer (ids, revision, subject,
-- scope and status, never a memory's words), and no key on the memory, so it can outlive a memory deleted for good, as
-- the audit trail does. Kept until the trash's purge job (SB-P3-06) prunes old keys.
CREATE TABLE memory_requests(
 principal_id uuid NOT NULL REFERENCES principals,
 idempotency_key text NOT NULL CHECK(idempotency_key ~ '^[A-Za-z0-9._:-]{8,200}$'),
 operation text NOT NULL CHECK(operation IN ('create','append','update','set_status')),
 payload_hash text NOT NULL CHECK(payload_hash ~ '^[0-9a-f]{64}$'),
 memory_id uuid NOT NULL,
 result jsonb NOT NULL CHECK(jsonb_typeof(result)='object' AND length(result::text)<=4000),
 received_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(principal_id,idempotency_key)
);
CREATE INDEX memory_requests_received ON memory_requests(received_at);
REVOKE ALL ON memory_requests FROM PUBLIC;

-- 025's list with memory_requests added: the engine's login reads it and writes it only through the functions here.
CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS $$
 SELECT ARRAY['memories','memory_versions','memory_events','memory_trash','memory_projects','memory_project_aliases',
   'capsule_revisions','made_links','memory_requests']::regclass[]
$$;

-- The revision a memory's text was last written at. Its passages are made at it (PassageSources.Revision), so a change
-- of status, tags, kind, importance, volatility or project, each of which moves the revision a writer compares, keeps
-- the passages of its unchanged text and leaves nothing to embed again; new text moves it, and 026's trigger drops the
-- old text's passages. A trigger keeps it for every row written and every change of the text, whoever makes it. No
-- install holds a native memory yet, so no row already there needs it set.
ALTER TABLE memories ADD COLUMN text_revision integer NOT NULL DEFAULT 1 CHECK(text_revision>=1);
CREATE FUNCTION memory_text_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' OR NEW.content IS DISTINCT FROM OLD.content THEN NEW.text_revision:=NEW.revision; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memory_text_revision BEFORE INSERT OR UPDATE ON memories FOR EACH ROW EXECUTE FUNCTION memory_text_revision();

-- The recorded memory.scopes mode, read from its row locked FOR SHARE: true unless it is exactly 'off', as scopes_on()
-- reads it (a missing row reads as on, and the engine's start refuses a database without one).
CREATE FUNCTION lock_memory_scopes() RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE mode text;
BEGIN
 SELECT value INTO mode FROM install_settings WHERE key='memory.scopes' FOR SHARE;
 RETURN mode IS DISTINCT FROM 'off';
END $$;

-- Tags the engine sets itself, in namespaces of its own or as markers (upstream's reserved namespaces and pipeline
-- markers, D-030; Rhythm keeps kind, status, volatility, capsule slots, holds and projects in columns): a caller's tag
-- here is dropped before it reaches the database (MemoryInput.ReservedPrefixes and ReservedNames, which a test keeps
-- equal to these), and refused if it arrives anyway. One list: a name ending in a colon is a namespace (a prefix), any
-- other a marker, matched case aside.
CREATE FUNCTION memory_reserved_tags() RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
 SELECT ARRAY['auto-pattern','auto-insight','synthesized','rolled-up','duplicate-candidate','contradiction-resolved',
   'user-edited','conflict-held','owed-to-me','retracted-source','kind:','status:','volatility:','stale:','capsule:',
   'capsule-slot:','quarantine:','edited-canonical:','standing:','ledger:','confidence:','confidence-source:','outcome:',
   'review-rearms:','counterparty:','project:']
$$;
CREATE FUNCTION memory_reserved_tag(p_tag text) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
 SELECT EXISTS(SELECT 1 FROM unnest(memory_reserved_tags()) x
   WHERE CASE WHEN right(x,1)=':' THEN starts_with(lower(p_tag),x) ELSE lower(p_tag)=x END)
$$;

-- A memory's tags as the engine hands them (MemoryInput.Tags): at most 64, each trimmed and lower-case, 1 to 128
-- characters, no control character, none reserved, none twice (case aside).
CREATE FUNCTION memory_check_tags(p_tags text[]) RETURNS text[] LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_tags IS NULL OR array_position(p_tags,NULL) IS NOT NULL OR cardinality(p_tags)>64
   OR EXISTS(SELECT 1 FROM unnest(p_tags) t WHERE t<>btrim(t) OR t<>lower(t) OR length(t) NOT BETWEEN 1 AND 128 OR t ~ '[[:cntrl:]]')
   OR cardinality(p_tags)<>(SELECT count(DISTINCT lower(t)) FROM unnest(p_tags) t) THEN
   RAISE EXCEPTION 'Tags are at most 64 distinct lower-case words of 1 to 128 characters' USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM unnest(p_tags) t WHERE memory_reserved_tag(t)) THEN
   RAISE EXCEPTION 'A tag in a namespace the engine sets itself is not taken' USING ERRCODE='22023';
 END IF;
 RETURN p_tags;
END $$;

-- A JSON array of strings as text[] (a write's tags); anything else is refused, never cast.
CREATE FUNCTION memory_strings(p_list jsonb) RETURNS text[] LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF jsonb_typeof(p_list)<>'array' OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_list) e WHERE jsonb_typeof(e)<>'string') THEN
   RAISE EXCEPTION 'Tags are a list of words' USING ERRCODE='22023';
 END IF;
 RETURN ARRAY(SELECT jsonb_array_elements_text(p_list));
END $$;

-- A memory's text: not blank, at most 131 072 bytes of UTF-8 (D-042, as upstream).
CREATE FUNCTION memory_check_text(p_content text) RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
 IF p_content IS NULL OR length(btrim(p_content,E' \t\r\n'))=0 THEN
   RAISE EXCEPTION 'A memory needs some text' USING ERRCODE='22023';
 END IF;
 IF octet_length(p_content)>131072 THEN
   RAISE EXCEPTION 'Too long for one memory: the limit is 131072 bytes of text' USING ERRCODE='54000';
 END IF;
 RETURN p_content;
END $$;

-- The scope a call names, in Rhythm's words or upstream's (D-044): private, or shared (Family); null when it names none.
CREATE FUNCTION memory_scope_named(p_named text) RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE named text:=lower(btrim(coalesce(p_named,'')));
BEGIN
 IF named='' THEN RETURN NULL; END IF;
 IF named IN ('private','personal') THEN RETURN 'private'; END IF;
 IF named IN ('shared','family','company') THEN RETURN 'shared'; END IF;
 RAISE EXCEPTION 'scope is private or shared (personal and company are accepted too)' USING ERRCODE='22023';
END $$;

-- Where a new memory lands (memory.md section 5, Where writes land; MemorySettings.Lands says the same, and a test
-- keeps the two equal): the subject. With scopes off every memory is the household's and a private one is refused
-- (D-048). With them on, the writer's own person's scope unless the call names Family (D-021); a writer with no person
-- has no private scope and writes only into Family, only when the call names it (D-073); and a writer writes only where
-- it reads (a person outside Family names Family in vain; a suspended or former person's login reads nothing).
CREATE FUNCTION memory_landing(p_principal uuid,p_person text,p_named text,p_scoped boolean) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE wanted text:=memory_scope_named(p_named); landed text;
BEGIN
 IF NOT p_scoped THEN
   IF wanted='private' THEN
     RAISE EXCEPTION 'private memories wait until per-person scopes are on; with them off every memory is visible to everyone in the household' USING ERRCODE='42501';
   END IF;
   RETURN 'household';
 END IF;
 IF wanted='shared' THEN landed:='household';
 ELSIF p_person IS NULL THEN
   RAISE EXCEPTION 'this client has no person, so it has no private scope: name the shared scope (Family) to save a household memory' USING ERRCODE='42501';
 ELSE landed:=p_person;
 END IF;
 IF NOT reads_subject(p_principal,landed) THEN
   RAISE EXCEPTION 'A memory is saved only where its writer reads: its own person''s scope or, as a Family member, Family' USING ERRCODE='42501';
 END IF;
 RETURN landed;
END $$;

-- Whether a principal may change a memory of subject p_subject whose author is p_author (memory.md section 5: edit,
-- append, set status). With scopes off, any admitted harness or app login (today's model, D-048). With them on, only a
-- reader of its subject: a private memory's owner; a Family memory a person wrote, that person or a Family admin; a
-- Family memory no person wrote, any principal that reads Family. An admin never changes a private memory.
CREATE FUNCTION memory_may_change(p_principal uuid,p_subject text,p_author text,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT p_scoped THEN principal_has_role(p_principal,ARRAY['harness','app'])
   WHEN NOT reads_subject(p_principal,p_subject) THEN false
   WHEN p_subject<>'household' THEN coalesce((SELECT person_id FROM principals WHERE id=p_principal)=p_subject,false)
   WHEN p_author IS NULL THEN true
   ELSE coalesce((SELECT c.person_id=p_author OR EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=c.person_id AND m.role='admin')
     FROM principals c WHERE c.id=p_principal),false) END
$$;

-- The channel a write came through, as the audit trail stores it: the app's login writes through the app; an AI client
-- (harness) through MCP, or the terminal (rhythm remember, SB-P3-19), which takes a person-bound token, so a login with
-- no person never records it. And the client's name, at most 80 characters with no control character, or none.
CREATE FUNCTION memory_channel(p_principal uuid,p_channel text,p_client text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE role text; person text;
BEGIN
 SELECT p.role,p.person_id INTO role,person FROM principals p WHERE p.id=p_principal;
 IF NOT (role='app' AND p_channel='app' OR role='harness' AND (p_channel='mcp' OR p_channel='cli' AND person IS NOT NULL)) THEN
   RAISE EXCEPTION 'Unknown channel for this login' USING ERRCODE='22023';
 END IF;
 IF p_client IS NOT NULL AND (length(p_client) NOT BETWEEN 1 AND 80 OR p_client ~ '[[:cntrl:]]') THEN
   RAISE EXCEPTION 'A client name is 1 to 80 characters' USING ERRCODE='22023';
 END IF;
 RETURN p_channel;
END $$;

-- An idempotent request's identity: its operation, memory, request and expected revision, as canonical JSON (jsonb's
-- text is the same for equal values), hashed.
CREATE FUNCTION memory_request_hash(p_operation text,p_memory uuid,p_request jsonb,p_expected integer) RETURNS text LANGUAGE sql IMMUTABLE AS $$
 SELECT encode(sha256(convert_to(jsonb_build_object('operation',p_operation,'memory',p_memory,'request',p_request,
   'expected',p_expected)::text,'UTF8')),'hex')
$$;

-- A request already answered under this key: its answer, marked replayed; null for a new key or none. The same key with
-- another request is refused. Concurrent requests with one key wait for each other here, so one applies.
CREATE FUNCTION memory_replay(p_principal uuid,p_key text,p_operation text,p_hash text) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE seen memory_requests;
BEGIN
 IF p_key IS NULL THEN RETURN NULL; END IF;
 IF p_key !~ '^[A-Za-z0-9._:-]{8,200}$' THEN
   RAISE EXCEPTION 'An idempotency key is 8 to 200 letters, digits or ._:-' USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('memory-request:'||p_principal::text||':'||p_key,0));
 SELECT * INTO seen FROM memory_requests WHERE principal_id=p_principal AND idempotency_key=p_key;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF seen.operation<>p_operation OR seen.payload_hash<>p_hash THEN
   RAISE EXCEPTION 'This idempotency key was used with a different request' USING ERRCODE='23505';
 END IF;
 RETURN seen.result||jsonb_build_object('replayed',true);
END $$;
CREATE FUNCTION memory_answered(p_principal uuid,p_key text,p_operation text,p_hash text,p_memory uuid,p_result jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
BEGIN
 IF p_key IS NOT NULL THEN
   INSERT INTO memory_requests(principal_id,idempotency_key,operation,payload_hash,memory_id,result)
   VALUES(p_principal,p_key,p_operation,p_hash,p_memory,p_result);
 END IF;
 RETURN p_result;
END $$;

-- A memory's project by slug or alias in its own scope, made there when it names none yet (as upstream makes a project
-- a write names): its id, slug and whether this call made it. Made by the writer, its person the author (none for a
-- writer with no person); a private project is its owner's (025's check). Audited like a memory change.
CREATE FUNCTION memory_project(p_principal uuid,p_person text,p_subject text,p_slug text,p_channel text,p_client text) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE known memory_projects; made uuid:=gen_random_uuid();
BEGIN
 IF p_slug IS NULL OR p_slug !~ '^[a-z0-9][a-z0-9-]{0,63}$' THEN
   RAISE EXCEPTION 'A project is a slug of lower-case letters, digits and hyphens, at most 64' USING ERRCODE='22023';
 END IF;
 SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=p_subject
   AND (p.slug=p_slug OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=p_slug))
   ORDER BY p.slug=p_slug DESC LIMIT 1;
 IF FOUND THEN RETURN jsonb_build_object('id',known.id,'slug',known.slug,'created',false); END IF;
 INSERT INTO memory_projects(id,subject_id,slug,name,author_id,created_by)
 VALUES(made,p_subject,p_slug,p_slug,p_person,p_principal) ON CONFLICT(subject_id,slug) DO NOTHING;
 IF NOT FOUND THEN
   SELECT p.* INTO known FROM memory_projects p WHERE p.subject_id=p_subject AND p.slug=p_slug;
   RETURN jsonb_build_object('id',known.id,'slug',known.slug,'created',false);
 END IF;
 INSERT INTO memory_events(project_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(made,p_subject,1,'created',p_principal,p_channel,p_client,jsonb_build_object('slug',p_slug));
 RETURN jsonb_build_object('id',made,'slug',p_slug,'created',true);
END $$;

-- The state a change retires, in full, into memory_versions (025): the words of that time with its scope, tags,
-- status, validity and the rest of its columns.
CREATE FUNCTION memory_retire(m memories) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
 VALUES(m.id,m.revision,m.subject_id,m.scope,m.content,m.tags,m.status,m.valid_from,m.valid_until,
   jsonb_strip_nulls(jsonb_build_object('kind',m.kind,'importance',m.importance,'volatility',m.volatility,'project_id',m.project_id,
     'capsule',m.capsule,'capsule_slot',m.capsule_slot,'hold',m.hold,'hold_score',m.hold_score)),
   m.updated_at,greatest(now(),m.updated_at))
$$;

-- What a write answers: the memory's id, revision, owner (its subject), scope and status, its project, and the mode it
-- was written under ('off': visible to everyone in the household, which the reply that saves one must say, D-048).
CREATE FUNCTION memory_reply(m memories,p_scoped boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('id',m.id,'revision',m.revision,'subject',m.subject_id,'scope',m.scope,'status',m.status,
   'scopes',CASE WHEN p_scoped THEN 'on' ELSE 'off' END,
   'project',(SELECT jsonb_build_object('id',p.id,'slug',p.slug) FROM memory_projects p WHERE p.id=m.project_id))
$$;

-- The memory a change names, locked for it: its resources row first, then the memory (D-086), then the checks. One the
-- caller does not read, one in the trash and an unknown id answer alike (P0002); one it reads but may not change is
-- refused (42501); a stale expected revision is refused (40001) with the current one.
CREATE FUNCTION memory_for_change(p_principal uuid,p_memory uuid,p_expected integer,p_scoped boolean) RETURNS memories LANGUAGE plpgsql AS $$
DECLARE m memories;
BEGIN
 PERFORM 1 FROM resources r WHERE r.id=p_memory AND r.provider='memory' AND r.relationship_snapshot IS NULL FOR UPDATE;
 SELECT * INTO m FROM memories WHERE id=p_memory FOR UPDATE;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id) OR NOT reads_subject(p_principal,m.subject_id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 IF NOT memory_may_change(p_principal,m.subject_id,m.author_id,p_scoped) THEN
   RAISE EXCEPTION 'Only this memory''s author or a Family admin may change it' USING ERRCODE='42501';
 END IF;
 IF p_expected IS NOT NULL AND p_expected<>m.revision THEN
   RAISE EXCEPTION 'The memory changed since revision %: it is at revision % now; read it again and retry',p_expected,m.revision
     USING ERRCODE='40001';
 END IF;
 RETURN m;
END $$;

-- create_memory: a new memory from p_memory, the fields the engine prepared (MemoryInput): content, tags, and optionally
-- kind (episodic, semantic), importance (1 to 5), volatility (durable, state, volatile), scope (named as
-- memory_scope_named takes it), project (a slug, made in the memory's scope when new) and source (the capturing client,
-- else the channel). Status is always draft (MemorySettings.NewStatus, D-021): a later set_memory_status confirms it.
CREATE FUNCTION create_memory(p_principal uuid,p_memory jsonb,p_channel text,p_client text DEFAULT NULL,p_key text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; person text; channel text; hash text; replay jsonb; landed text; scope text; made uuid:=gen_random_uuid();
 project jsonb; m memories; result jsonb;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 p_memory:=jsonb_strip_nulls(p_memory);
 IF p_memory IS NULL OR jsonb_typeof(p_memory)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_memory) k WHERE k NOT IN ('content','tags','kind','importance','volatility','scope','project','source')) THEN
   RAISE EXCEPTION 'A memory takes content, tags, kind, importance, volatility, scope, project and source' USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM jsonb_each(p_memory) e WHERE e.key IN ('scope','kind','volatility','project','source')
   AND jsonb_typeof(e.value)<>'string') THEN
   RAISE EXCEPTION 'scope, kind, volatility, project and source are each a word' USING ERRCODE='22023';
 END IF;
 hash:=memory_request_hash('create',NULL,p_memory,NULL);
 replay:=memory_replay(p_principal,p_key,'create',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 SELECT person_id INTO person FROM principals WHERE id=p_principal;
 landed:=memory_landing(p_principal,person,p_memory->>'scope',scoped);
 scope:=CASE WHEN landed='household' THEN 'shared' ELSE 'private' END;
 IF jsonb_typeof(p_memory->'content') IS DISTINCT FROM 'string' THEN
   RAISE EXCEPTION 'A memory needs some text' USING ERRCODE='22023';
 END IF;
 PERFORM memory_check_text(p_memory->>'content');
 IF coalesce(p_memory->>'kind','episodic') NOT IN ('episodic','semantic')
   OR jsonb_typeof(coalesce(p_memory->'importance','1'))<>'number' OR coalesce(p_memory->>'importance','1') !~ '^[1-5]$'
   OR coalesce(p_memory->>'volatility','durable') NOT IN ('durable','state','volatile')
   OR coalesce(p_memory->>'source',channel) !~ '^[a-z][a-z0-9_.-]{0,63}$' THEN
   RAISE EXCEPTION 'kind is episodic or semantic, importance 1 to 5, volatility durable, state or volatile, and source a short lower-case name' USING ERRCODE='22023';
 END IF;
 PERFORM memory_check_tags(memory_strings(coalesce(p_memory->'tags','[]')));
 IF p_memory->>'project' IS NOT NULL THEN
   project:=memory_project(p_principal,person,landed,p_memory->>'project',channel,p_client);
 END IF;
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
 VALUES(made,landed,'memory',made::text,'memory',scope,'Memory','','current','memory');
 INSERT INTO memories(id,subject_id,scope,author_id,created_by,content,tags,kind,status,importance,volatility,source,project_id)
 VALUES(made,landed,scope,person,p_principal,p_memory->>'content',
   memory_strings(coalesce(p_memory->'tags','[]')),p_memory->>'kind','draft',
   (p_memory->>'importance')::smallint,p_memory->>'volatility',coalesce(p_memory->>'source',channel),(project->>'id')::uuid)
 RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client)
 VALUES(m.id,m.subject_id,1,'created',p_principal,channel,p_client);
 result:=memory_reply(m,scoped)||CASE WHEN project IS NULL THEN '{}'::jsonb
   ELSE jsonb_build_object('project',jsonb_build_object('id',project->'id','slug',project->'slug','created',project->'created')) END;
 RETURN memory_answered(p_principal,p_key,'create',hash,m.id,result);
END $$;

-- append_memory: p_text added to the end as a dated update, "[Update YYYY-MM-DD]: ..." in the install's time zone
-- (D-043), the earlier text kept as a version. The whole text stays within the limit, or nothing is added.
CREATE FUNCTION append_memory(p_principal uuid,p_memory uuid,p_text text,p_expected integer,p_channel text,
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

-- update_memory: what p_change names, and nothing else: content (replaced; the volatility verdict goes with the old
-- text unless the change sets one, as upstream), tags (a replacement), hashtags (tags the engine found in the new text,
-- added), kind, importance, volatility and project (each set by a value or cleared by null). A change that changes
-- nothing writes nothing and answers unchanged. The state it replaces is kept as a version.
CREATE FUNCTION update_memory(p_principal uuid,p_memory uuid,p_change jsonb,p_expected integer,p_channel text,
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
   IF n.content IS DISTINCT FROM m.content AND NOT p_change ? 'volatility' THEN n.volatility:=NULL; END IF;
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

-- set_memory_status: canonical, draft or deprecated (D-030). A deprecated memory leaves recall and capsules and stays
-- in history (SB-P3-03 onward). Setting the status it has writes nothing and answers unchanged.
CREATE FUNCTION set_memory_status(p_principal uuid,p_memory uuid,p_status text,p_expected integer,p_channel text,
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
 was:=m.status;
 PERFORM memory_retire(m);
 UPDATE memories SET status=p_status,revision=revision+1,updated_at=greatest(now(),updated_at) WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'status_changed',p_principal,channel,p_client,jsonb_build_object('from',was,'to',p_status));
 result:=memory_reply(m,scoped);
 RETURN memory_answered(p_principal,p_key,'set_status',hash,m.id,result);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_text_revision() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION lock_memory_scopes() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_reserved_tag(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_reserved_tags() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_check_tags(text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_strings(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_check_text(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_scope_named(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_landing(uuid,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_may_change(uuid,text,text,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_channel(uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_request_hash(text,uuid,jsonb,integer) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_replay(uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_answered(uuid,text,text,text,uuid,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_project(uuid,text,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_retire(memories) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_reply(memories,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_for_change(uuid,uuid,integer,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION create_memory(uuid,jsonb,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION append_memory(uuid,uuid,text,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION update_memory(uuid,uuid,jsonb,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION set_memory_status(uuid,uuid,text,integer,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_text_revision(),lock_memory_scopes(),memory_reserved_tag(text),memory_reserved_tags(),
 memory_check_tags(text[]),memory_strings(jsonb),memory_check_text(text),memory_scope_named(text),
 memory_landing(uuid,text,text,boolean),memory_may_change(uuid,text,text,boolean),memory_channel(uuid,text,text),
 memory_request_hash(text,uuid,jsonb,integer),memory_replay(uuid,text,text,text),memory_answered(uuid,text,text,text,uuid,jsonb),
 memory_project(uuid,text,text,text,text,text),memory_retire(memories),memory_reply(memories,boolean),
 memory_for_change(uuid,uuid,integer,boolean),create_memory(uuid,jsonb,text,text,text),
 append_memory(uuid,uuid,text,integer,text,text,text),update_memory(uuid,uuid,jsonb,integer,text,text,text),
 set_memory_status(uuid,uuid,text,integer,text,text,text) FROM PUBLIC;
