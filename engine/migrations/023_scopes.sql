-- Memory scopes (docs/dev/memory.md sections 2 to 4 and 6.2; SB-P1-02): the person a principal speaks for, Family's
-- members and admins, the sources a person shares with Family, the mode the engine runs with, and the one read
-- predicate every read path takes (section 7, Database.Visible and Database.Allowed). Nothing here changes what anyone
-- reads while memory.scopes is off, which this migration records for every install: readable_subjects then answers
-- today's rule, the household shares everything. It names no person: people become subjects at start
-- (Principals.Install), and bindings, memberships and shares are made by the operator or by the person.

-- A person is current while rhythm.yaml lists them, former once taken out (their subject and rows stay, and with
-- scopes on nobody but the engine reads them), or suspended by the operator (SB-P1-12), listed or not, until the
-- operator lifts it (Principals.Install never does). The household and the synthetic subjects are always current.
-- (id, kind) is unique so that a column naming a person can require one.
ALTER TABLE subjects ADD COLUMN status text NOT NULL DEFAULT 'current' CHECK(status IN ('current','suspended','former'));
ALTER TABLE subjects ADD CONSTRAINT subjects_only_people_leave CHECK(kind='person' OR status='current');
ALTER TABLE subjects ADD CONSTRAINT subjects_id_kind UNIQUE(id,kind);

-- The person a principal speaks for (section 3): a harness login (an AI client) is bound when its token is made
-- (rhythm token create --person, SB-P1-12), and a viewer session's credential (SB-P1-11, D-070) is an app login bound
-- to its person, so the perimeter serves it the app's tools. The binding never changes: a client moves to another
-- person by a new token. The shared app token's login is the one app login with no person, and never names one:
-- exactly one of those is enabled (017's index, narrowed here), and Principals.SyncApp touches no bound one. Every
-- existing principal has none, so reads the household view once scopes are on. A bound principal's token never moves
-- either (no code changes a token's hash), so a token cannot be carried over to a principal bound to someone else.
ALTER TABLE principals ADD COLUMN person_id text;
ALTER TABLE principals ADD COLUMN person_kind text NOT NULL DEFAULT 'person' CHECK(person_kind='person');
ALTER TABLE principals ADD CONSTRAINT principals_person FOREIGN KEY(person_id,person_kind) REFERENCES subjects(id,kind);
ALTER TABLE principals ADD CONSTRAINT principals_person_role CHECK(person_id IS NULL OR role IN ('harness','app'));
DROP INDEX principals_one_app;
CREATE UNIQUE INDEX principals_one_app ON principals(role) WHERE role='app' AND enabled AND person_id IS NULL;
CREATE FUNCTION principal_person_unchanged() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.person_id IS DISTINCT FROM OLD.person_id
   OR OLD.person_id IS NOT NULL AND NEW.token_hash IS DISTINCT FROM OLD.token_hash THEN
   RAISE EXCEPTION 'A principal''s person never changes: revoke its token and make a new one' USING ERRCODE='22023';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER principals_person_unchanged BEFORE UPDATE OF person_id,token_hash ON principals
 FOR EACH ROW EXECUTE FUNCTION principal_person_unchanged();

-- Family's members (section 4): with scopes on, only a member's principals and principals with no person read the
-- household's rows. The operator gives and takes the admin role on the host, never over MCP or in the app; an admin
-- may change Family records others wrote and never reads anyone's private scope. The first start with scopes on adds
-- everyone then in people (SB-P1-09); nobody joins automatically after that, and a person taken out of people leaves.
CREATE TABLE memberships(
 person_id text PRIMARY KEY,
 person_kind text NOT NULL DEFAULT 'person' CHECK(person_kind='person'),
 role text NOT NULL DEFAULT 'member' CHECK(role IN ('member','admin')),
 added_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY(person_id,person_kind) REFERENCES subjects(id,kind)
);
REVOKE ALL ON memberships FROM PUBLIC;

-- Source shares (section 4, D-059): one provider's rows of one person, read by Family while scopes are on, so a
-- household that shows one person's glucose on a shared screen keeps it. Recorded or revoked only by that person (a
-- principal bound to them and enabled: shared_by) or by the operator on the host (shared_by null); never by an admin
-- for someone else, never automatically. A share is never rewritten: revoking is a delete, sharing again a new row.
CREATE TABLE source_shares(
 person_id text NOT NULL,
 person_kind text NOT NULL DEFAULT 'person' CHECK(person_kind='person'),
 provider text NOT NULL CHECK(provider ~ '^[a-z][a-z0-9-]{0,63}$'),
 shared_by uuid REFERENCES principals(id),
 shared_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(person_id,provider),
 FOREIGN KEY(person_id,person_kind) REFERENCES subjects(id,kind)
);
REVOKE ALL ON source_shares FROM PUBLIC;
CREATE FUNCTION source_share_by_its_person() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='UPDATE' AND (NEW.person_id,NEW.provider,NEW.shared_by) IS DISTINCT FROM (OLD.person_id,OLD.provider,OLD.shared_by) THEN
   RAISE EXCEPTION 'A source share is not rewritten: revoke it and share again' USING ERRCODE='42501';
 END IF;
 IF NEW.shared_by IS NOT NULL
   AND NOT EXISTS(SELECT 1 FROM principals WHERE id=NEW.shared_by AND person_id=NEW.person_id AND enabled) THEN
   RAISE EXCEPTION 'Only the person, or the operator on the host, shares their source' USING ERRCODE='42501';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER source_shares_by_its_person BEFORE INSERT OR UPDATE ON source_shares
 FOR EACH ROW EXECUTE FUNCTION source_share_by_its_person();

-- The mode the engine runs with (section 6.2): kept in the database, so a lost or older rhythm.yaml, or a restore,
-- cannot turn scopes off by accident. Every install this migration finds is off. This build accepts nothing else
-- (Principals.Install refuses a database that records on) until phase 1's exit (section 6.4, SB-P1-09). Only an
-- exact 'off' is off: a missing row reads as on, and the engine's start refuses it, so deleting the row never widens.
ALTER TABLE install_settings ADD CONSTRAINT install_settings_memory_scopes CHECK(key<>'memory.scopes' OR value IN ('off','on'));
INSERT INTO install_settings(key,value) VALUES('memory.scopes','off') ON CONFLICT(key) DO NOTHING;

-- The one read predicate (section 7): what a principal reads, as rows of (subject, provider, scope), where null means
-- any. Database.Allowed matches a row against them, Database.Visible adds a row's status and the rule for copies.
-- Scopes off (the recorded value is exactly 'off'), today's rule: an enabled system, harness or app principal reads
-- every subject; a legacy one what its grants name; an ingest one nothing.
-- Scopes on (section 2): system reads everything; a harness or app principal reads the synthetic (DEMO) subjects, its
-- own person's rows, and Family (the household's rows and current people's shared sources) when it has no person or
-- its person is a member; a principal whose person is not current reads nothing; legacy and ingest read nothing. A
-- row's subject decides, whatever its scope column says, and a former or suspended person's rows are no one's.
-- PL/pgSQL keeps its plans for the session, and each place a read takes the predicate evaluates it once (a function
-- scan keeps its rows); plain SQL was inlined into every such place and planned there each time. STABLE, not a
-- definer, with its own search path; a null principal reads nothing.
CREATE FUNCTION readable_subjects(p_principal uuid) RETURNS TABLE(subject_id text, provider text, scope text)
LANGUAGE plpgsql STABLE STRICT ROWS 4 AS $$
#variable_conflict use_column
BEGIN
 IF coalesce((SELECT value FROM install_settings WHERE key='memory.scopes')='off',false) THEN
   RETURN QUERY
   SELECT NULL::text,NULL::text,NULL::text FROM principals p
    WHERE p.id=p_principal AND p.enabled AND p.role IN ('system','harness','app')
   UNION ALL
   SELECT g.subject_id,g.provider,g.scope FROM principals p JOIN grants g ON g.principal_id=p.id
    WHERE p.id=p_principal AND p.enabled AND p.role='legacy';
   RETURN;
 END IF;
 RETURN QUERY
 SELECT NULL::text,NULL::text,NULL::text FROM principals p WHERE p.id=p_principal AND p.enabled AND p.role='system'
 UNION ALL
 SELECT s.id,NULL,NULL FROM principals p JOIN subjects s ON s.kind='synthetic'
    OR s.kind='person' AND s.id=p.person_id
    OR s.kind='household' AND (p.person_id IS NULL OR EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=p.person_id))
  WHERE p.id=p_principal AND p.enabled AND p.role IN ('harness','app')
    AND (p.person_id IS NULL OR EXISTS(SELECT 1 FROM subjects b WHERE b.id=p.person_id AND b.status='current'))
 UNION ALL
 SELECT x.person_id,x.provider,NULL FROM principals p JOIN source_shares x ON true
    JOIN subjects s ON s.id=x.person_id AND s.status='current'
  WHERE p.id=p_principal AND p.enabled AND p.role IN ('harness','app')
    AND (p.person_id IS NULL OR EXISTS(SELECT 1 FROM subjects b WHERE b.id=p.person_id AND b.status='current')
      AND EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=p.person_id));
END $$;

-- With scopes on, a save_document copy is read only by readers of the original it copies as well (section 8): any
-- record that is not itself a copy and whose source id (a snapshot row's Key.ExternalId, or a catalog row's external
-- id) is the copy's external id or a legacy copy's sourceFileId, whatever its status. Of one provider's rows for that
-- source id only the newest observation counts (its snapshot's RetrievedAt, else when it was indexed; on a tie, every
-- newest row), so the original's current subject decides: a file moved into Family widens its copies again, and one
-- deleted at its source still narrows them. A native record's own row (a memory's: provider 'memory', its id as its
-- external id, no snapshot) is the record itself, so when it is among them it alone decides for its provider: a share
-- or un-share changes it in place and re-stamps nothing (section 8), and the map's star row for it, refreshed every
-- pass, never outvotes it. Database.Visible calls it only for a copy, and only while scopes are on; the two indexes
-- find a copy's originals without reading the catalog.
CREATE FUNCTION copy_readable(p_principal uuid,p_copy uuid,p_external text,p_source_file text) RETURNS boolean
LANGUAGE plpgsql STABLE AS $$
BEGIN
 RETURN NOT EXISTS(SELECT 1 FROM (
   SELECT o.subject_id,o.provider,o.scope,s.seen,s.own,max(s.seen) OVER (PARTITION BY o.provider) AS newest,
     bool_or(s.own) OVER (PARTITION BY o.provider) AS owned FROM resources o
    CROSS JOIN LATERAL (SELECT coalesce((o.relationship_snapshot->>'RetrievedAt')::timestamptz,o.indexed_at) AS seen,
      o.provider='memory' AND o.relationship_snapshot IS NULL AND o.external_id=o.id::text AS own) s
    WHERE o.id<>p_copy AND coalesce(o.metadata->>'retrievalMode','') NOT IN ('dated-snapshot','dated-reference-snapshot')
      AND (o.external_id IN (p_external,p_source_file)
        OR o.relationship_snapshot IS NOT NULL AND o.relationship_snapshot->'Key'->>'ExternalId' IN (p_external,p_source_file))) n
   WHERE CASE WHEN n.owned THEN n.own ELSE n.seen=n.newest END
     AND NOT EXISTS(SELECT 1 FROM readable_subjects(p_principal) x WHERE (x.subject_id IS NULL OR x.subject_id=n.subject_id)
       AND (x.provider IS NULL OR x.provider=n.provider) AND (x.scope IS NULL OR x.scope=n.scope)));
END $$;
CREATE INDEX resources_source_id ON resources((relationship_snapshot->'Key'->>'ExternalId')) WHERE relationship_snapshot IS NOT NULL;
CREATE INDEX resources_external_id ON resources(external_id);
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION readable_subjects(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION copy_readable(uuid,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION principal_person_unchanged() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION source_share_by_its_person() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION readable_subjects(uuid),copy_readable(uuid,uuid,text,text),principal_person_unchanged(),
 source_share_by_its_person() FROM PUBLIC;
