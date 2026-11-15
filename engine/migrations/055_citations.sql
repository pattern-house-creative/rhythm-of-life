-- Native memories that cite Drive documents (SB-P4-10 in docs/dev/second-brain-parity/backlog.md; story 7; D-014, D-085;
-- docs/dev/memory.md 10.4; ledger RHY-31). A memory, a Drive original and a stored copy are three identities: a citation
-- joins a memory to the original by the source's own id and the revision the memory was written against, with when that
-- was last checked, and keeps none of the document's words, so a cached excerpt never answers for it.
--
-- memory_citations: one row a memory and a document. What the memory cites: the provider (drive only, for now), the
-- map's star account and the source's file id, the subject and scope the map held the document under when it was cited,
-- and the document as a check found it then: its revision, when it was last modified, its name and its folders. Then the
-- last stored check (checked_at, checked_state: current, trashed, gone, unreadable or unavailable) and the last
-- revalidation: when, by whom, through which channel, and the revision it moved from (earlier_revision). The engine checks
-- the document at the source itself before it calls these functions (Core/MemoryCitations.cs, through the adapter's
-- ICitedSource): a citation records what a check found, never a revision a caller named. Reading a memory checks each of
-- its citations again, live, and says whether the document changed since, is in the trash, gone or unreadable, is
-- unavailable (the source's sync no longer finds it where Rhythm reads, so it is not checked at the source), or could not
-- be checked now; nothing a read finds is stored. Only a revalidation moves a citation to the document's current revision,
-- and it says what moved: never silently.
--
-- cite_memory_source: whoever may change the memory (033's author lock, memory_for_change), which is not held for review
-- (a held memory's words wait for a person, D-160), cites a document the map holds now, current, as a document (not a
-- folder), that the caller reads by the one read predicate on its newest row (memory_cited_row: the newest observation
-- counts, whatever its subject or status, as 023's copy rule reads an original). No wider than the document: with scopes
-- on, a memory cites only a document of its own subject or the household's (Family's), so no reader of the memory is
-- given a document they do not read. At most 20 documents a memory. Citing a document cited already answers unchanged and
-- moves nothing: bringing a citation up to date is a revalidation's.
--
-- revalidate_memory_citations: whoever may change the memory, which is not held, records what a check found now for each
-- of its citations the caller is served: a document readable at another revision, name or folders moves the citation
-- there, keeping the revision it moved from; one in the trash, gone or unreadable is recorded as such, its cited revision
-- kept; one found as cited moves only its checked time. A document the check could not reach is left out by the engine
-- and nothing of it is written. A document the map no longer holds as current is not checked at Drive (Rhythm reads only
-- what the map holds), and no name or folder of it now is stored: one whose newest row is missing or deleted (the Drive
-- sync's complete listing of the folders Rhythm is given no longer finds it: moved out, trashed, deleted or no longer
-- shared) is recorded as unavailable, only when the engine says so and the map agrees, its cited revision, name and
-- folders kept; for one revoked since a restore (until the next rebuild) nothing is written. Each check carries the
-- citation's checked time as the engine read it before checking, and is written only while the citation still has it, so
-- a check made earlier never overwrites one recorded meanwhile.
--
-- memory_citation_readable: a citation is served to a reader of its memory who reads the document too, under its newest
-- row's subject and scope (the map's current word on whose it is); when the map holds no row of it now, to no one (it
-- fails closed, rather than falling back to the subject it was cited under). So a document taken into one person's
-- private scope after a Family memory cited it leaves that citation to its readers only, and an un-share or a share of
-- the memory (048) never widens a citation. With scopes off every admitted login reads every subject, as today.
--
-- A citation changes no memory: no revision moves, no version is kept and no audit row is written (the citation keeps who
-- cited and revalidated it, when and through which channel). It stays with its memory in the trash and goes with it when
-- it is deleted for good. Nothing here is exported by rhythm export (043) yet, and no map pass, sync job or stored row of
-- the map reads or writes it: the Drive sync's schedule and its rows are as before.
--
-- Nothing calls these but the memory tools cite, revalidate and get (Mcp/MemoryTools.cs), made only with memory.tools on
-- (off by default), and tests: with none called no install holds a citation, and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to change; P0002 an unknown memory, also one
-- the caller does not read or one in the trash; 22023 invalid input and, with a hint, held (a memory held for review),
-- unknown_document (no such document on the map for the caller) or source_unavailable (no check found it readable). A
-- refused call writes nothing. The number is provisional.

CREATE TABLE memory_citations(
 citation_id uuid PRIMARY KEY,
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 provider text NOT NULL CHECK(provider='drive'),
 account text NOT NULL CHECK(length(account) BETWEEN 1 AND 500),
 source_id text NOT NULL CHECK(source_id ~ '^[A-Za-z0-9_-]{1,200}$'),
 subject_id text NOT NULL REFERENCES subjects,
 scope text NOT NULL CHECK(scope IN ('private','shared')),
 revision text NOT NULL CHECK(length(revision) BETWEEN 1 AND 200),
 modified_at timestamptz,
 title text NOT NULL CHECK(length(title) BETWEEN 1 AND 1000),
 parents text[] NOT NULL DEFAULT '{}' CHECK(cardinality(parents)<=20),
 cited_by uuid NOT NULL REFERENCES principals,
 cited_channel text NOT NULL,
 cited_at timestamptz NOT NULL DEFAULT now(),
 checked_at timestamptz NOT NULL DEFAULT now(),
 checked_state text NOT NULL DEFAULT 'current' CHECK(checked_state IN ('current','trashed','gone','unreadable','unavailable')),
 earlier_revision text CHECK(length(earlier_revision) BETWEEN 1 AND 200),
 revalidated_at timestamptz,
 revalidated_by uuid REFERENCES principals,
 revalidated_channel text,
 UNIQUE(memory_id,provider,account,source_id)
);
CREATE INDEX memory_citations_source ON memory_citations(provider,source_id);
REVOKE ALL ON memory_citations FROM PUBLIC;

-- The list of checked tables in force, with the citations added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_citations']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The newest row the map holds for a document of p_provider whose source id is p_source, whatever its subject or status:
-- its snapshot's RetrievedAt, else when it was indexed, then its id (023's copy_readable reads an original the same way).
-- None when the map holds no row of it.
CREATE FUNCTION memory_cited_row(p_provider text,p_source text) RETURNS resources LANGUAGE sql STABLE AS $$
 SELECT r.* FROM resources r WHERE r.relationship_snapshot IS NOT NULL AND r.relationship_snapshot->'Key'->>'ExternalId'=p_source
   AND r.provider=p_provider
 ORDER BY coalesce((r.relationship_snapshot->>'RetrievedAt')::timestamptz,r.indexed_at) DESC,r.id LIMIT 1
$$;

-- Whether p_principal reads the document a citation names: under its newest row's subject and scope, through the one
-- read predicate (readable_subjects); false when the map holds no row of it. p_subject and p_scope, the ones it was cited
-- under, are not read: a row the map no longer holds says nothing about who reads the document now.
CREATE FUNCTION memory_citation_readable(p_principal uuid,p_provider text,p_source text,p_subject text,p_scope text) RETURNS boolean
LANGUAGE plpgsql STABLE AS $$
DECLARE d resources;
BEGIN
 d:=memory_cited_row(p_provider,p_source);
 IF d.id IS NULL THEN RETURN false; END IF;
 RETURN EXISTS(SELECT 1 FROM readable_subjects(p_principal) x WHERE (x.subject_id IS NULL OR x.subject_id=d.subject_id)
   AND (x.provider IS NULL OR x.provider=p_provider) AND (x.scope IS NULL OR x.scope=d.scope));
END $$;

-- The folders of what a check found, as the engine passes it ({state, revision, modified_at, title, parents}): Drive ids
-- only, each once, in order, at most 20. The revision and title are cut to their columns' lengths where they are written.
CREATE FUNCTION memory_citation_parents(p_seen jsonb) RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
 SELECT coalesce((SELECT array_agg(y.id ORDER BY y.id) FROM (SELECT DISTINCT e.value AS id
   FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(p_seen->'parents')='array' THEN p_seen->'parents' ELSE '[]'::jsonb END) e
   WHERE e.value ~ '^[A-Za-z0-9_-]{1,200}$' ORDER BY 1 LIMIT 20) y),'{}'::text[])
$$;

CREATE FUNCTION cite_memory_source(p_principal uuid,p_memory uuid,p_provider text,p_source text,p_seen jsonb,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; d resources; c memory_citations; v_account text;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Citing documents is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_provider IS DISTINCT FROM 'drive' THEN
   RAISE EXCEPTION 'A memory cites a Drive document only, for now' USING ERRCODE='22023';
 END IF;
 IF p_source IS NULL OR p_source !~ '^[A-Za-z0-9_-]{1,200}$' THEN
   RAISE EXCEPTION 'A Drive document is named by its file id: letters, digits, - and _' USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,NULL,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review cites a document only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 -- The document as the map holds it now: its newest row, current, a document, which the caller reads.
 d:=memory_cited_row(p_provider,p_source);
 IF d.id IS NULL OR d.status<>'current' OR d.kind<>'document'
   OR NOT EXISTS(SELECT 1 FROM readable_subjects(p_principal) x WHERE (x.subject_id IS NULL OR x.subject_id=d.subject_id)
     AND (x.provider IS NULL OR x.provider=d.provider) AND (x.scope IS NULL OR x.scope=d.scope)) THEN
   RAISE EXCEPTION 'No Drive document with this id is on the map for you: cite one that explore_graph or search lists'
     USING ERRCODE='22023',HINT='unknown_document';
 END IF;
 IF scoped AND d.subject_id<>m.subject_id AND d.subject_id<>'household' THEN
   RAISE EXCEPTION 'A memory cites only a document of its own scope or of Family: this document is in a private scope the memory''s other readers do not read'
     USING ERRCODE='22023';
 END IF;
 IF p_seen->>'state' IS DISTINCT FROM 'ok' OR coalesce(p_seen->>'revision','')='' THEN
   RAISE EXCEPTION 'A citation records what a check of the document found, and no check found it readable' USING ERRCODE='22023',
     HINT='source_unavailable';
 END IF;
 v_account:=coalesce(d.relationship_snapshot->'Key'->>'Account',p_provider);
 SELECT * INTO c FROM memory_citations x WHERE x.memory_id=m.id AND x.provider=p_provider AND x.account=v_account AND x.source_id=p_source;
 IF c.citation_id IS NOT NULL THEN
   RETURN memory_reply(m,scoped)||jsonb_build_object('citation',c.citation_id,'created',false);
 END IF;
 IF (SELECT count(*) FROM memory_citations x WHERE x.memory_id=m.id)>=20 THEN
   RAISE EXCEPTION 'A memory cites at most 20 documents' USING ERRCODE='22023';
 END IF;
 INSERT INTO memory_citations(citation_id,memory_id,provider,account,source_id,subject_id,scope,revision,modified_at,title,parents,
   cited_by,cited_channel)
 VALUES(gen_random_uuid(),m.id,p_provider,v_account,p_source,d.subject_id,d.scope,left(p_seen->>'revision',200),
   (p_seen->>'modified_at')::timestamptz,coalesce(nullif(left(p_seen->>'title',1000),''),left(d.title,1000)),
   memory_citation_parents(p_seen),p_principal,channel)
 RETURNING * INTO c;
 RETURN memory_reply(m,scoped)||jsonb_build_object('citation',c.citation_id,'created',true);
END $$;

CREATE FUNCTION revalidate_memory_citations(p_principal uuid,p_memory uuid,p_seen jsonb,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; c memory_citations; s jsonb; results jsonb:='[]'::jsonb; v_state text;
 v_revision text; v_title text; v_parents text[]; v_modified timestamptz; moved boolean; d resources;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Revalidating citations is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF jsonb_typeof(coalesce(p_seen,'[]'::jsonb))<>'array' OR jsonb_array_length(coalesce(p_seen,'[]'::jsonb))>20 THEN
   RAISE EXCEPTION 'At most 20 checks, one a citation' USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,NULL,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review has its citations revalidated only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 FOR s IN SELECT x FROM jsonb_array_elements(coalesce(p_seen,'[]'::jsonb)) x LOOP
   SELECT * INTO c FROM memory_citations x WHERE x.memory_id=m.id
     AND x.citation_id=CASE WHEN s->>'citation' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN (s->>'citation')::uuid END
     FOR UPDATE;
   IF c.citation_id IS NULL OR NOT memory_citation_readable(p_principal,c.provider,c.source_id,c.subject_id,c.scope) THEN CONTINUE; END IF;
   -- Checked against the citation as it was read: one recorded since by another check is left as that one wrote it.
   IF s ? 'checked_at' AND c.checked_at IS DISTINCT FROM (s->>'checked_at')::timestamptz THEN CONTINUE; END IF;
   -- A document the map no longer holds as current is outside what Rhythm reads: no check of it at Drive is recorded.
   -- One the Drive sync no longer finds (its newest row missing or deleted) is recorded as unavailable when the engine
   -- says so, and nothing more; one revoked since a restore, nothing at all.
   d:=memory_cited_row(c.provider,c.source_id);
   v_state:=s->>'state';
   IF d.id IS NULL OR d.status<>'current' AND NOT (d.status IN ('missing','deleted') AND v_state='unavailable')
     OR d.status='current' AND v_state='unavailable' THEN
     CONTINUE;
   END IF;
   IF v_state='ok' AND coalesce(s->>'revision','')<>'' THEN
     v_revision:=left(s->>'revision',200);
     v_title:=coalesce(nullif(left(s->>'title',1000),''),c.title);
     v_parents:=memory_citation_parents(s);
     v_modified:=(s->>'modified_at')::timestamptz;
     moved:=c.revision<>v_revision OR c.title<>v_title OR c.parents<>v_parents OR c.modified_at IS DISTINCT FROM v_modified
       OR c.checked_state<>'current';
     IF moved THEN
       UPDATE memory_citations SET earlier_revision=CASE WHEN revision<>v_revision THEN revision ELSE earlier_revision END,
         revision=v_revision,title=v_title,parents=v_parents,modified_at=v_modified,checked_at=now(),checked_state='current',
         revalidated_at=now(),revalidated_by=p_principal,revalidated_channel=channel
       WHERE citation_id=c.citation_id;
     ELSE
       UPDATE memory_citations SET checked_at=now() WHERE citation_id=c.citation_id;
     END IF;
   ELSIF v_state IN ('trashed','gone','unreadable','unavailable') THEN
     moved:=c.checked_state<>v_state;
     UPDATE memory_citations SET checked_at=now(),checked_state=v_state,
       revalidated_at=CASE WHEN moved THEN now() ELSE revalidated_at END,
       revalidated_by=CASE WHEN moved THEN p_principal ELSE revalidated_by END,
       revalidated_channel=CASE WHEN moved THEN channel ELSE revalidated_channel END
     WHERE citation_id=c.citation_id;
   ELSE
     -- Not checked now: nothing is known about the document, so nothing is written.
     CONTINUE;
   END IF;
   results:=results||jsonb_build_object('citation',c.citation_id,'revalidated',moved,'revision',c.revision,
     'checked_state',c.checked_state,'checked_at',c.checked_at);
 END LOOP;
 RETURN memory_reply(m,scoped)||jsonb_build_object('results',results);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list of checked tables gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_cited_row(text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_citation_readable(uuid,text,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_citation_parents(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION cite_memory_source(uuid,uuid,text,text,jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION revalidate_memory_citations(uuid,uuid,jsonb,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_cited_row(text,text),memory_citation_readable(uuid,text,text,text,text),
 memory_citation_parents(jsonb),cite_memory_source(uuid,uuid,text,text,jsonb,text,text),
 revalidate_memory_citations(uuid,uuid,jsonb,text,text) FROM PUBLIC;
