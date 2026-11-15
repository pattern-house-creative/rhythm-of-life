-- Digests of native memories (SB-P5-13 in docs/dev/second-brain-parity/backlog.md; story 13; ledger rows MCP-05,
-- API-07, JOB-02, INT-06, INT-07 and CFG-07; D-032, D-033, D-034; docs/dev/memory.md sections 5 and 9): one memory that
-- sums up a project or a topic tag of one scope, drawn from its long tail of small memories. Adapted from Second Brain's
-- digests (src/compression/eligibility.ts, src/compression/digest.ts and the digest tool in src/mcp/server.ts at the
-- pinned commit, MIT; NOTICE): the same eligibility rule and bounds, one scope at a time, a cooldown of a day.
--
-- Unlike upstream, no model runs in the engine (D-033) and no source is touched (D-032). The engine lists, by one rule,
-- the memories a digest of a key would draw from (memory_digest_eligible), newest first, and hands out the list's basis
-- (memory_digest_basis: the scope, the key and each memory's id and revision, hashed). The AI client writes the
-- paragraph and saves it with that basis (save_memory_digest), which writes only while the list still hashes to it.
-- The digest is a new memory of the same scope, labelled the engine's own (source 'system', which no AI client, the app
-- or the terminal can give a memory), with a drawn_from link (made_links, 052's kind of link) to each source. Upstream
-- appends "[Digest: <id>]" to each source's own text and tags it rolled-up; here a source keeps its row, text, tags,
-- revision, versions and history exactly as they were: a source is told apart only by the link, which no source's own
-- answer moves.
--
-- Eligible (upstream's rule, in Rhythm's columns): a memory of the scope, in the project (or tagged with one of its
-- aliases, as a list counts members) or carrying the tag; live (in the catalog, not in the trash, not held for review,
-- not marked wrong, its window open); not a digest itself and not drawn into a live one already (upstream's rolled-up);
-- in no capsule slot; importance under 4 or none; and recalled never, or fewer than twice and written over 60 days ago
-- (042's recall counts of the digest's own scope alone, so one person's recalls never change another scope's list, which
-- exist only while engine.recallLog is on: without them every memory counts as never recalled, as upstream's counter
-- starts). 042 keeps a recall in its reader's own scope: a login bound to a person counts it in that person's, whatever
-- memory.scopes says. So a Family digest (every digest while scopes are off) is protected only by the household's own
-- recalls, those of a login with no person (the app's own, a household view), and never by a person's AI client's:
-- ten recalls through a token bound to Alex leave a Family memory eligible (Q-159's default; D-198). A memory is drawn
-- into a live digest of its own scope only when that digest holds it: one taken into another scope by a share (048)
-- holds nothing of this one's. Stricter than upstream: a memory flagged for a check (049, built on one marked wrong
-- since) waits until it is checked. Upstream also leaves out contradiction winners and
-- standing instructions, which Rhythm does not have yet (phase 5's SB-P5-12 and the contradiction review must leave
-- theirs out when they come).
--
-- Due: at least 10 eligible, and no live digest of the key in the scope written in the last 24 hours (upstream's bounds;
-- fixed here, not settings: CFG-07 stays missing). A digest draws from the 50 newest. A held digest is live: its sources
-- wait until a person releases, marks wrong or forgets it, as upstream holds a held draft's tag back. A digest marked
-- wrong, ended or in the trash holds nothing, so its sources are eligible again.
--
-- Repeats never duplicate. Saving again with a basis a live digest of the scope was written from answers that digest,
-- unchanged, whoever asks; within the day a new basis is refused as not due; and once written, its sources are drawn and
-- leave the list. An idempotency key replays the first answer, as every write's does (033).
--
-- The digest is written as remember writes (033's create_memory): a draft in the caller's scope by its author lock and
-- landing rule, screened for a hold (036, 045: through an AI client's channel its write counts toward the burst), with
-- one audit row, created, whose detail names the digest's key, basis and source count; its links are the caller's,
-- inferred through an AI client's channel (D-015). Its text is the engine's line "[Digest of N memories tagged x]" (or
-- "in project slug") and the client's paragraph. Only a project the caller's lists show is taken (046: a held project
-- would be let into the lists by a write that is not held).
--
-- An export and import round trip keeps a digest the digest of its key: rhythm export writes the created row's digest
-- object (its key, basis and source count) on the memory's line, and 043's import_memory, replaced here, copies it into
-- the imported row's detail, its project mapped as the memory's own is, so memory_digest_keyed reads the key from that
-- row as it reads it from a created one. Only for the engine's own memory (source system); a digest whose project did
-- not come in names no key there, though it still holds its sources. Every other line of import_memory is 043's.
--
-- Nothing calls these but the memory tools (digest_sources, save_digest, digest), made only with memory.tools on (off by
-- default), and tests: with none called no memory has source 'system', and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not where this caller writes; 22023 invalid input and, with a
-- hint the engine reads, not_due (too few, or within the day) and unknown_project; 40001 a basis the list no longer
-- hashes to; 54000 text over the limit; 23505 an idempotency key used for another request. A refused call writes
-- nothing. The number is provisional.

-- A digest's write takes an idempotency key, as every other write does (033, 038, 049).
ALTER TABLE memory_requests DROP CONSTRAINT memory_requests_operation_check;
ALTER TABLE memory_requests ADD CONSTRAINT memory_requests_operation_check
 CHECK(operation IN ('create','append','update','set_status','forget','restore','validity','digest'));

-- The bounds (upstream's COMPRESSION_* defaults and its 24 hour cooldown and LIMIT 50): at least 10 memories and at most
-- the 50 newest a digest; one digest of a key in a scope a day; importance 4 or more protects a memory, as two recalls do,
-- or one before it is 60 days old.
CREATE FUNCTION memory_digest_bounds() RETURNS TABLE(minimum integer,maximum integer,cooldown interval,importance integer,recalls integer,age interval)
LANGUAGE sql IMMUTABLE AS $$
 SELECT 10,50,interval '24 hours',4,2,interval '60 days'
$$;

-- Whether p_memory is a digest that holds its sources: labelled the engine's own (source system), in the catalog, not in
-- the trash, not marked wrong and its window open. A held digest holds them too.
CREATE FUNCTION memory_digest_live(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memories d
   JOIN resources r ON r.id=d.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
   WHERE d.id=p_memory AND d.source='system' AND d.status<>'deprecated' AND (d.valid_until IS NULL OR d.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=d.id))
$$;

-- The memories a digest of one key in the scope p_subject would draw from, every one of them, newest first: a project
-- (p_project, of that scope) or a topic tag (p_tag), exactly one. The one rule digest_sources lists and the write checks.
CREATE FUNCTION memory_digest_eligible(p_subject text,p_project uuid,p_tag text)
RETURNS TABLE(id uuid,revision integer,created_at timestamptz) LANGUAGE sql STABLE AS $$
 SELECT m.id,m.revision,m.created_at FROM memories m
 JOIN resources r ON r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current'
 CROSS JOIN memory_digest_bounds() b
 CROSS JOIN LATERAL (SELECT coalesce(sum(c.recalled),0) AS n FROM recall_counts c WHERE c.resource_id=m.id AND c.subject_id=p_subject) k
 WHERE m.subject_id=p_subject AND (p_project IS NULL)<>(p_tag IS NULL)
   AND (p_project IS NULL OR m.project_id=p_project
     OR m.tags && ARRAY(SELECT a.alias FROM memory_project_aliases a JOIN memory_projects p ON p.id=a.project_id
       WHERE a.project_id=p_project AND p.subject_id=p_subject))
   AND (p_tag IS NULL OR m.tags @> ARRAY[p_tag])
   AND m.hold IS NULL AND m.source<>'system' AND m.capsule IS NULL AND m.status<>'deprecated'
   AND (m.valid_until IS NULL OR m.valid_until>now())
   AND (m.importance IS NULL OR m.importance<b.importance)
   AND (k.n=0 OR k.n<b.recalls AND m.created_at<now()-b.age)
   AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM memory_retraction_flags f WHERE f.memory_id=m.id)
   AND NOT EXISTS(SELECT 1 FROM made_links l JOIN memories d ON d.id=l.source_id AND d.subject_id=p_subject
     WHERE l.target_id=m.id AND l.type='drawn_from' AND l.lifecycle='active' AND memory_digest_live(d.id))
 ORDER BY m.created_at DESC,m.id DESC
$$;

-- A list as one token: the scope, the key, and p_sources, the [id, revision] of each memory in the list's order, hashed.
CREATE FUNCTION memory_digest_hash(p_subject text,p_project uuid,p_tag text,p_sources jsonb) RETURNS text LANGUAGE sql IMMUTABLE AS $$
 SELECT encode(sha256(convert_to(jsonb_build_object('subject',p_subject,'project',p_project,'tag',p_tag,
   'sources',coalesce(p_sources,'[]'::jsonb))::text,'UTF8')),'hex')
$$;

-- What a digest of a key in a scope would be drawn from now, as one token: the hash of the memories it would take (the
-- newest memory_digest_bounds().maximum eligible).
CREATE FUNCTION memory_digest_basis(p_subject text,p_project uuid,p_tag text) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT memory_digest_hash(p_subject,p_project,p_tag,(SELECT jsonb_agg(jsonb_build_array(e.id,e.revision) ORDER BY e.created_at DESC,e.id DESC)
   FROM (SELECT x.* FROM memory_digest_eligible(p_subject,p_project,p_tag) x ORDER BY x.created_at DESC,x.id DESC
     LIMIT (SELECT b.maximum FROM memory_digest_bounds() b)) e))
$$;

-- Whether p_memory is a digest of that key: a project (p_project) or a topic tag (p_tag), as its created row names it
-- (or, for one rhythm import brought, its imported row). The key is read from that row, which nothing changes, never
-- from the digest's tags or project as they stand now: an update may change those, and a re-tagged digest is still the
-- digest of the key it was drawn for.
CREATE FUNCTION memory_digest_keyed(p_memory uuid,p_project uuid,p_tag text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=p_memory AND e.event IN ('created','imported') AND e.detail ? 'digest'
   AND (p_project IS NULL OR e.detail->'digest'->>'project'=p_project::text)
   AND (p_tag IS NULL OR e.detail->'digest'->>'tag'=p_tag))
$$;

-- The newest live digest of one key in one scope, or none (every column null).
CREATE FUNCTION memory_digest_latest(p_subject text,p_project uuid,p_tag text) RETURNS memories LANGUAGE sql STABLE AS $$
 SELECT d.* FROM memories d
 WHERE d.subject_id=p_subject AND d.source='system' AND (p_project IS NULL)<>(p_tag IS NULL)
   AND memory_digest_keyed(d.id,p_project,p_tag) AND memory_digest_live(d.id)
 ORDER BY d.created_at DESC,d.id DESC LIMIT 1
$$;

-- What a digest's write answers: a write's answer (033's memory_reply), its key, the memories it was drawn from that
-- p_principal reads, in the list's order (every one, for the digest it writes now), and whether this call wrote it.
CREATE FUNCTION memory_digest_reply(m memories,p_scoped boolean,p_tag text,p_unchanged boolean,p_principal uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT memory_reply(m,p_scoped)||jsonb_build_object('tag',p_tag,'unchanged',p_unchanged,
   'drawnFrom',coalesce((SELECT jsonb_agg(l.target_id ORDER BY s.created_at DESC,s.id DESC) FROM made_links l JOIN memories s ON s.id=l.target_id
     WHERE l.source_id=m.id AND l.type='drawn_from' AND l.lifecycle='active' AND reads_subject(p_principal,s.subject_id)),'[]'::jsonb))
$$;

-- save_memory_digest: the digest an AI client (or a person) wrote from digest_sources' list, saved as a new memory of the
-- scope with a drawn_from link to each memory of that list. p_digest: content (the paragraph), project (a slug or alias)
-- or tag, scope (as memory_scope_named takes it; none: the caller's own, as remember lands) and basis (the list's).
CREATE FUNCTION save_memory_digest(p_principal uuid,p_digest jsonb,p_channel text,p_client text DEFAULT NULL,p_key text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; person text; channel text; hash text; replay jsonb; landed text; scope text; v_project memory_projects;
 v_tag text; b record; v_locked uuid[]; v_ids uuid[]; v_sources jsonb; v_count bigint; prior memories; made uuid:=gen_random_uuid();
 m memories; v_content text;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing a digest is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 p_digest:=jsonb_strip_nulls(p_digest);
 IF p_digest IS NULL OR jsonb_typeof(p_digest)<>'object'
   OR EXISTS(SELECT 1 FROM jsonb_object_keys(p_digest) k WHERE k NOT IN ('content','project','tag','scope','basis'))
   OR EXISTS(SELECT 1 FROM jsonb_each(p_digest) e WHERE jsonb_typeof(e.value)<>'string') THEN
   RAISE EXCEPTION 'A digest takes content, project or tag, scope and basis, each a string' USING ERRCODE='22023';
 END IF;
 IF (p_digest ? 'project')=(p_digest ? 'tag') THEN
   RAISE EXCEPTION 'A digest is of one project or one tag: name exactly one' USING ERRCODE='22023';
 END IF;
 IF coalesce(p_digest->>'basis','') !~ '^[0-9a-f]{64}$' THEN
   RAISE EXCEPTION 'basis is the one digest_sources gave, as it was given' USING ERRCODE='22023';
 END IF;
 IF NOT (p_digest ? 'content') THEN
   RAISE EXCEPTION 'A digest needs some text' USING ERRCODE='22023';
 END IF;
 PERFORM memory_check_text(p_digest->>'content');
 hash:=memory_request_hash('digest',NULL,p_digest,NULL);
 replay:=memory_replay(p_principal,p_key,'digest',hash);
 IF replay IS NOT NULL THEN RETURN replay; END IF;
 SELECT person_id INTO person FROM principals WHERE id=p_principal;
 landed:=memory_landing(p_principal,person,p_digest->>'scope',scoped);
 scope:=CASE WHEN landed='household' THEN 'shared' ELSE 'private' END;
 SELECT * INTO b FROM memory_digest_bounds();
 IF p_digest ? 'tag' THEN
   v_tag:=(memory_check_tags(ARRAY[p_digest->>'tag']))[1];
 ELSE
   SELECT p.* INTO v_project FROM memory_projects p WHERE p.subject_id=landed AND NOT p.held
     AND (p.slug=p_digest->>'project' OR EXISTS(SELECT 1 FROM memory_project_aliases a WHERE a.project_id=p.id AND a.alias=p_digest->>'project'))
     ORDER BY p.slug=p_digest->>'project' DESC LIMIT 1 FOR KEY SHARE OF p;
   IF v_project.id IS NULL THEN
     RAISE EXCEPTION 'No project by that name in this scope' USING ERRCODE='22023',HINT='unknown_project';
   END IF;
 END IF;
 -- One digest of a scope is written at a time, whatever its key: a second waits here, then finds the first and the
 -- memories it drew (a project and one of its tags, or an alias, share memories, and a lock FOR SHARE never waits for
 -- another).
 PERFORM pg_advisory_xact_lock(hashtextextended('memory-digest:'||landed,0));
 -- The memories it would draw from, locked for share as a change locks a memory (its resources row, then the memory, in
 -- id order, D-086), so none changes, moves or goes to the trash before the digest and its links are written.
 SELECT coalesce(array_agg(e.id),'{}') INTO v_locked FROM (SELECT x.id FROM memory_digest_eligible(landed,v_project.id,v_tag) x
   ORDER BY x.created_at DESC,x.id DESC LIMIT b.maximum) e;
 PERFORM 1 FROM resources r WHERE r.id=ANY(v_locked) AND r.provider='memory' AND r.relationship_snapshot IS NULL ORDER BY r.id FOR SHARE;
 PERFORM 1 FROM memories x WHERE x.id=ANY(v_locked) ORDER BY x.id FOR SHARE;
 -- Saved already: a live digest of this scope written from this basis answers, unchanged, whoever asks.
 SELECT d.* INTO prior FROM memories d
 WHERE d.subject_id=landed AND d.source='system' AND memory_digest_live(d.id)
   AND EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=d.id AND e.event IN ('created','imported')
     AND e.detail->'digest'->>'basis'=p_digest->>'basis')
 ORDER BY d.created_at DESC,d.id DESC LIMIT 1;
 IF prior.id IS NOT NULL THEN
   RETURN memory_answered(p_principal,p_key,'digest',hash,prior.id,memory_digest_reply(prior,scoped,v_tag,true,p_principal));
 END IF;
 prior:=memory_digest_latest(landed,v_project.id,v_tag);
 IF prior.id IS NOT NULL AND prior.created_at>now()-b.cooldown AND prior.hold IS NOT NULL THEN
   -- A held digest is named by no key (D-149): the wait is told, not the digest.
   RAISE EXCEPTION 'Not due: a digest of this is waiting for a person''s review; the next is due a day after it was written'
     USING ERRCODE='22023',HINT='not_due';
 ELSIF prior.id IS NOT NULL AND prior.created_at>now()-b.cooldown THEN
   RAISE EXCEPTION 'Not due: a digest of this was written at % UTC (memory %); the next is due a day after it',
     to_char(prior.created_at AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI'),prior.id USING ERRCODE='22023',HINT='not_due';
 END IF;
 -- The list as it stands now, its length and its basis, in one read: the links go to exactly the memories the basis
 -- names, each of them locked above.
 SELECT coalesce(array_agg(e.id ORDER BY e.created_at DESC,e.id DESC) FILTER (WHERE e.n<=b.maximum),'{}'),
   jsonb_agg(jsonb_build_array(e.id,e.revision) ORDER BY e.created_at DESC,e.id DESC) FILTER (WHERE e.n<=b.maximum),count(*)
 INTO v_ids,v_sources,v_count
 FROM (SELECT x.*,row_number() OVER (ORDER BY x.created_at DESC,x.id DESC) AS n FROM memory_digest_eligible(landed,v_project.id,v_tag) x) e;
 IF v_count<b.minimum THEN
   RAISE EXCEPTION 'Not due: % memories are eligible, and a digest draws from at least %',v_count,b.minimum
     USING ERRCODE='22023',HINT='not_due';
 END IF;
 IF memory_digest_hash(landed,v_project.id,v_tag,v_sources)<>p_digest->>'basis'
   OR NOT (v_ids<@v_locked AND v_locked<@v_ids) THEN
   RAISE EXCEPTION 'The memories a digest of this draws from changed since digest_sources listed them: list them again and write it from that list'
     USING ERRCODE='40001';
 END IF;
 v_count:=cardinality(v_ids);
 v_content:=format('[Digest of %s memories %s]',v_count,CASE WHEN v_tag IS NULL THEN 'in project '||v_project.slug ELSE 'tagged '||v_tag END)
   ||E'\n\n'||btrim(p_digest->>'content',E' \t\r\n');
 PERFORM memory_check_text(v_content);
 INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
 VALUES(made,landed,'memory',made::text,'memory',scope,'Memory','','current','memory');
 INSERT INTO memories(id,subject_id,scope,author_id,created_by,content,tags,kind,status,importance,volatility,source,project_id)
 VALUES(made,landed,scope,person,p_principal,v_content,CASE WHEN v_tag IS NULL THEN '{}'::text[] ELSE ARRAY[v_tag] END,NULL,'draft',
   NULL,NULL,'system',v_project.id)
 RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,1,'created',p_principal,channel,p_client,jsonb_build_object('digest',
   jsonb_strip_nulls(jsonb_build_object('project',v_project.id,'tag',v_tag,'basis',p_digest->>'basis','sources',v_count))));
 INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,source_revision,checked_at)
 SELECT gen_random_uuid(),m.id,x.id,'drawn_from',CASE WHEN channel='mcp' THEN 'inferred' ELSE 'verified' END,'','active',p_principal,
   'memory:'||m.revision,now()
 FROM unnest(v_ids) x(id);
 -- The screening and the burst may have held it as its audit row went in (036, 045).
 SELECT * INTO m FROM memories WHERE id=made;
 RETURN memory_answered(p_principal,p_key,'digest',hash,m.id,memory_digest_reply(m,scoped,v_tag,false,p_principal));
END $$;


-- 043's import_memory, but that the engine's own memory (source system) brings its digest object (above), copied into
-- its imported audit row. Every other line as 043 wrote it.
CREATE OR REPLACE FUNCTION import_memory(p_person text,p_private boolean,p_family boolean,p_origin jsonb,p_memory jsonb,p_projects jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; v_id uuid; v_subject text; v_scope text; v_author text; why text; v_project uuid; v_trash jsonb;
 m memories; v_floor integer; kept integer; given integer; v_digest jsonb;
BEGIN
 scoped:=lock_memory_scopes();
 BEGIN
   IF jsonb_typeof(p_memory) IS DISTINCT FROM 'object' OR jsonb_typeof(p_memory->'subject') IS DISTINCT FROM 'string'
     OR jsonb_typeof(p_memory->'content') IS DISTINCT FROM 'string'
     OR jsonb_typeof(coalesce(p_memory->'author','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_memory->'project','null'::jsonb)) NOT IN ('string','null')
     OR jsonb_typeof(coalesce(p_memory->'versions','[]'::jsonb))<>'array'
     OR jsonb_typeof(coalesce(p_memory->'trash','null'::jsonb)) NOT IN ('object','null')
     OR jsonb_typeof(coalesce(p_projects,'{}'::jsonb))<>'object' THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   v_id:=(p_memory->>'id')::uuid; v_subject:=p_memory->>'subject'; v_author:=p_memory->>'author';
   v_scope:=CASE WHEN v_subject='household' THEN 'shared' ELSE 'private' END;
   IF v_id IS NULL OR p_memory->>'scope' IS DISTINCT FROM v_scope THEN
     RETURN jsonb_build_object('outcome','refused','reason','invalid');
   END IF;
   why:=memory_import_refusal(p_person,p_private,p_family,v_subject,v_author,scoped);
   IF why='not_named' THEN RETURN jsonb_build_object('outcome','not_named'); END IF;
   IF why IS NOT NULL THEN RETURN jsonb_build_object('outcome','refused','reason',why); END IF;
   IF EXISTS(SELECT 1 FROM memories x WHERE x.id=v_id) THEN RETURN jsonb_build_object('outcome','present'); END IF;
   IF EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=v_id AND e.event='purged') THEN
     RETURN jsonb_build_object('outcome','deleted_here');
   END IF;
   IF memory_imported(p_origin,'memory',v_id) THEN RETURN jsonb_build_object('outcome','imported_before'); END IF;
   IF EXISTS(SELECT 1 FROM resources r WHERE r.id=v_id) THEN RETURN jsonb_build_object('outcome','refused','reason','id_taken'); END IF;
   IF p_memory->>'project' IS NOT NULL THEN
     v_project:=(coalesce(p_projects,'{}'::jsonb)->>(p_memory->>'project'))::uuid;
     IF v_project IS NULL OR NOT EXISTS(SELECT 1 FROM memory_projects p WHERE p.id=v_project AND p.subject_id=v_subject) THEN
       RETURN jsonb_build_object('outcome','refused','reason','project');
     END IF;
   END IF;
   v_trash:=CASE WHEN jsonb_typeof(p_memory->'trash')='object' THEN p_memory->'trash' END;
   IF v_trash IS NOT NULL AND (v_trash->>'purgeAfter')::timestamptz<=now() THEN RETURN jsonb_build_object('outcome','expired'); END IF;
   INSERT INTO resources(id,subject_id,provider,external_id,kind,scope,title,content,status,domain)
   VALUES(v_id,v_subject,'memory',v_id::text,'memory',v_scope,'Memory','',CASE WHEN v_trash IS NULL THEN 'current' ELSE 'deleted' END,'memory');
   INSERT INTO memories(id,subject_id,scope,author_id,created_by,content,tags,kind,status,importance,volatility,source,project_id,
     capsule,capsule_slot,hold,hold_score,valid_from,valid_until,revision,created_at,updated_at)
   VALUES(v_id,v_subject,v_scope,v_author,NULL,memory_check_text(p_memory->>'content'),
     memory_check_tags(memory_strings(coalesce(p_memory->'tags','[]'::jsonb))),p_memory->>'kind',p_memory->>'status',
     (p_memory->>'importance')::smallint,p_memory->>'volatility',p_memory->>'source',v_project,p_memory->>'capsule',
     p_memory->>'capsuleSlot',p_memory->>'hold',(p_memory->>'holdScore')::real,(p_memory->>'validFrom')::timestamptz,
     (p_memory->>'validUntil')::timestamptz,(p_memory->>'revision')::integer,(p_memory->>'createdAt')::timestamptz,
     (p_memory->>'updatedAt')::timestamptz)
   RETURNING * INTO m;
   IF EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(p_memory->'versions','[]'::jsonb)) v
       WHERE jsonb_typeof(v)<>'object' OR jsonb_typeof(v->'subject') IS DISTINCT FROM 'string' OR (v->>'revision')::integer>=m.revision) THEN
     RAISE EXCEPTION 'An earlier version comes before the memory''s revision' USING ERRCODE='22023';
   END IF;
   given:=jsonb_array_length(coalesce(p_memory->'versions','[]'::jsonb));
   SELECT coalesce(max((v->>'revision')::integer),0)+1 INTO v_floor FROM jsonb_array_elements(coalesce(p_memory->'versions','[]'::jsonb)) v
     WHERE CASE WHEN scoped THEN NOT memory_import_reads(p_person,v->>'subject',true) ELSE v->>'subject'<>'household' END;
   -- Each earlier version is screened as the memory is: one whose state has no hold, held there and released or never
   -- held, is held here when 036's screening holds its words, so no release crosses installs. Otherwise an undo through
   -- an AI client's channel would bring back a released state's words unheld (041 keeps a revision's own hold, and its
   -- update screens new words only for what they add), and history and a version read would serve them (MemoryHistory
   -- serves a state without a hold whole).
   INSERT INTO memory_versions(memory_id,revision,subject_id,scope,content,tags,status,valid_from,valid_until,state,written_at,retired_at)
   SELECT v_id,(v->>'revision')::integer,v->>'subject',v->>'scope',x.content,x.tags,v->>'status',(v->>'validFrom')::timestamptz,
     (v->>'validUntil')::timestamptz,
     x.state||CASE WHEN x.state->>'hold' IS NULL AND w.verdict->>'reason' IS NOT NULL
       THEN jsonb_build_object('hold',w.verdict->>'reason','hold_score',w.verdict->'score') ELSE '{}'::jsonb END,
     (v->>'writtenAt')::timestamptz,(v->>'retiredAt')::timestamptz
   FROM jsonb_array_elements(coalesce(p_memory->'versions','[]'::jsonb)) v
   CROSS JOIN LATERAL (SELECT memory_check_text(v->>'content') AS content,
     memory_check_tags(memory_strings(coalesce(v->'tags','[]'::jsonb))) AS tags,
     CASE WHEN coalesce(p_projects,'{}'::jsonb) ? coalesce(v->'state'->>'project_id','')
       THEN jsonb_set(v->'state','{project_id}',p_projects->(v->'state'->>'project_id')) ELSE v->'state' END AS state) x
   CROSS JOIN LATERAL (SELECT CASE WHEN x.state->>'hold' IS NULL THEN memory_screen(x.content,x.tags) END AS verdict) w
   WHERE (v->>'revision')::integer>=v_floor ORDER BY (v->>'revision')::integer;
   GET DIAGNOSTICS kept=ROW_COUNT;
   IF v_trash IS NOT NULL THEN
     INSERT INTO memory_trash(memory_id,reason,trashed_by,channel,trashed_at,purge_after,links)
     VALUES(v_id,v_trash->>'reason',NULL,'cli',(v_trash->>'trashedAt')::timestamptz,(v_trash->>'purgeAfter')::timestamptz,'[]');
   END IF;
   -- A digest's key (054): only for the engine's own memory, as the export's digest object names it, its project mapped
   -- as the memory's own is (none when that project did not come in), so the imported row names the key its created
   -- row named there.
   IF m.source='system' AND jsonb_typeof(p_memory->'digest')='object' THEN
     v_digest:=jsonb_strip_nulls(jsonb_build_object(
       'project',CASE WHEN jsonb_typeof(p_memory->'digest'->'project')='string' THEN (SELECT p.id FROM memory_projects p
         WHERE p.id=(coalesce(p_projects,'{}'::jsonb)->>(p_memory->'digest'->>'project'))::uuid AND p.subject_id=v_subject) END,
       'tag',CASE WHEN jsonb_typeof(p_memory->'digest'->'tag')='string' THEN (memory_check_tags(ARRAY[p_memory->'digest'->>'tag']))[1] END,
       'basis',CASE WHEN p_memory->'digest'->>'basis' ~ '^[0-9a-f]{64}$' THEN p_memory->'digest'->>'basis' END,
       'sources',CASE WHEN jsonb_typeof(p_memory->'digest'->'sources')='number' THEN p_memory->'digest'->'sources' END));
   END IF;
   INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
   VALUES(v_id,v_subject,m.revision,'imported',NULL,'cli','rhythm import',jsonb_build_object('origin',p_origin->>'origin',
     'export',p_origin->>'export','versions',kept,'versions_left_out',given-kept,'trashed',v_trash IS NOT NULL)
     ||CASE WHEN v_digest IS NOT NULL THEN jsonb_build_object('digest',v_digest) ELSE '{}'::jsonb END);
   PERFORM memory_import_origin(p_origin,'memory',v_id,v_subject);
   RETURN jsonb_build_object('outcome','added','id',v_id,'versions',kept,'versionsLeftOut',given-kept,'trashed',v_trash IS NOT NULL,
     'held',m.hold IS NOT NULL,'heldHere',m.hold IS NOT NULL AND p_memory->>'hold' IS NULL);
 EXCEPTION WHEN data_exception OR integrity_constraint_violation OR program_limit_exceeded THEN
   RETURN jsonb_build_object('outcome','refused','reason','invalid');
 END;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_digest_bounds() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_live(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_eligible(text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_hash(text,uuid,text,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_basis(text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_keyed(uuid,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_latest(text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_digest_reply(memories,boolean,text,boolean,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_memory_digest(uuid,jsonb,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 -- CREATE OR REPLACE resets a function's settings, so 043's import_memory gets its search path again.
 EXECUTE format('ALTER FUNCTION import_memory(text,boolean,boolean,jsonb,jsonb,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_digest_bounds(),memory_digest_live(uuid),memory_digest_eligible(text,uuid,text),
 memory_digest_hash(text,uuid,text,jsonb),memory_digest_basis(text,uuid,text),memory_digest_keyed(uuid,uuid,text),memory_digest_latest(text,uuid,text),
 memory_digest_reply(memories,boolean,text,boolean,uuid),
 save_memory_digest(uuid,jsonb,text,text,text),import_memory(text,boolean,boolean,jsonb,jsonb,jsonb) FROM PUBLIC;
