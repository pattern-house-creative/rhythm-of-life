-- Native memories (docs/dev/memory.md sections 2, 5, 8 and 10; SB-P1-07 in docs/dev/second-brain-parity/backlog.md).
-- Rhythm is the system of record for these rows. They are inert until phase 3: nothing writes a memory yet and no read
-- path reads these tables. A memory's text lives here, never in a map snapshot row, which a restore's quarantine blanks
-- (engine/backup/restore-quarantine.sql); its resources row and passages are derived from it.
--
-- Writes go only through role- and scope-checked SECURITY DEFINER functions, the save_document() pattern (017):
-- migrate leaves the engine's own login SELECT on the tables checked_tables() lists and takes its INSERT, UPDATE and
-- DELETE away (Engine.GrantLogin in Core/Program.cs). No function here writes a memory; phase 3 adds them. The
-- triggers below only keep the capsule revisions and refuse the engine's login what it must not change directly.
--
-- Scopes as memory.md section 2 stores them: a row's subject decides who reads it, and the scope describes it, 'shared'
-- for the household (Family) and 'private' for anyone else. An author is a person (never the household); a NULL author
-- means a principal with no person wrote it, so the row is the household's. A NULL principal means the operator, on the
-- host (channel 'cli'), who is not a principal.

-- The provider name 'memory' is the native memories' alone (ProviderPolicies.Reserved refuses it to any adapter or
-- plugin); an install whose catalog already holds rows under that name stops here rather than fail on every later write.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM resources WHERE provider='memory') THEN
   RAISE EXCEPTION 'The provider name memory is reserved for native memories, and this catalog already has rows under it';
 END IF;
END $$;

-- A memory's resources row: provider 'memory', its id the memory's id and its external id the same uuid as text, so the
-- graph id comes from the memory id alone (memory.md 10.3). The read predicate looks at that row's subject and scope;
-- the composite key below keeps the memory's own copy of them the same: a share or un-share changes the row's subject
-- in place and the memory follows, and neither can be changed apart from the other.
CREATE UNIQUE INDEX resources_memory_key ON resources(id,provider,subject_id,scope);
-- So a memory's resources row is written by the checked functions too. The engine's login writes resources rows
-- itself, so here a trigger stands in for the grant: anyone but the table's owner (the checked functions run as it, and
-- so do cascades) may change only when such a row was last indexed. A memory's own row never holds a map snapshot
-- (memory.md 10.1), which a restore's quarantine would blank: the map keeps a memory's star in a snapshot row of its
-- own (provider 'memory', the map's 'relationship-engine:' external id, a snapshot, and no memory behind it), which the
-- login writes, refreshes and removes as it does every other star. Phase 3's map source binds no memory row to a star.
CREATE FUNCTION guard_memory_resources() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
 old_star boolean := TG_OP<>'INSERT' AND OLD.provider='memory' AND starts_with(OLD.external_id,'relationship-engine:')
   AND OLD.relationship_snapshot IS NOT NULL AND NOT EXISTS(SELECT 1 FROM memories WHERE id=OLD.id);
 new_star boolean := TG_OP<>'DELETE' AND NEW.provider='memory' AND starts_with(NEW.external_id,'relationship-engine:')
   AND NEW.relationship_snapshot IS NOT NULL AND NOT EXISTS(SELECT 1 FROM memories WHERE id=NEW.id);
 -- The login may write a star row, and on a memory's own row change when it was last indexed; nothing else.
 allowed boolean := CASE TG_OP WHEN 'INSERT' THEN new_star WHEN 'DELETE' THEN old_star
   ELSE old_star AND new_star OR OLD.provider='memory' AND NOT old_star AND to_jsonb(NEW)-'indexed_at' IS NOT DISTINCT FROM to_jsonb(OLD)-'indexed_at' END;
BEGIN
 IF current_user<>(SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid=TG_RELID) AND NOT allowed THEN
   RAISE EXCEPTION 'A memory''s resources row changes only through its functions' USING ERRCODE='42501';
 END IF;
 RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER memory_resources_insert BEFORE INSERT ON resources
 FOR EACH ROW WHEN (NEW.provider='memory') EXECUTE FUNCTION guard_memory_resources();
CREATE TRIGGER memory_resources_update BEFORE UPDATE ON resources
 FOR EACH ROW WHEN (OLD.provider='memory' OR NEW.provider='memory') EXECUTE FUNCTION guard_memory_resources();
CREATE TRIGGER memory_resources_delete BEFORE DELETE ON resources
 FOR EACH ROW WHEN (OLD.provider='memory') EXECUTE FUNCTION guard_memory_resources();

-- Projects, one registry per scope (upstream's projects, adapted): a slug unique in its scope, a name, a description,
-- archived or active. Deleting one keeps its memories: the delete function clears their project first.
CREATE TABLE memory_projects(
 id uuid PRIMARY KEY,
 subject_id text NOT NULL REFERENCES subjects,
 slug text NOT NULL CHECK(slug ~ '^[a-z0-9][a-z0-9-]{0,63}$'),
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 120 AND name !~ '[[:cntrl:]]'),
 description text NOT NULL DEFAULT '' CHECK(length(description)<=1000),
 status text NOT NULL DEFAULT 'active' CHECK(status IN ('active','archived')),
 author_id text REFERENCES subjects CHECK(author_id<>'household'),
 created_by uuid REFERENCES principals,
 revision integer NOT NULL DEFAULT 1 CHECK(revision>=1),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(subject_id,slug),
 UNIQUE(id,subject_id),
 -- A private project is its owner's own (memory.md 5: created in your private scope).
 CHECK(subject_id='household' OR coalesce(author_id=subject_id,false))
);
-- Other names a project answers to in its scope (older tags it adopts without rewriting them).
CREATE TABLE memory_project_aliases(
 project_id uuid NOT NULL,
 subject_id text NOT NULL,
 alias text NOT NULL CHECK(length(alias) BETWEEN 1 AND 128 AND alias=btrim(alias) AND alias !~ '[[:cntrl:]]'),
 PRIMARY KEY(project_id,alias),
 UNIQUE(subject_id,alias),
 FOREIGN KEY(project_id,subject_id) REFERENCES memory_projects(id,subject_id) ON DELETE CASCADE
);

CREATE TABLE memories(
 id uuid PRIMARY KEY,
 provider text NOT NULL DEFAULT 'memory' CHECK(provider='memory'),
 subject_id text NOT NULL REFERENCES subjects,
 scope text NOT NULL CHECK(scope IN ('shared','private')),
 author_id text REFERENCES subjects CHECK(author_id<>'household'),
 created_by uuid REFERENCES principals,
 -- Up to 131 072 bytes of UTF-8, as upstream (D-042); longer text never arrives here.
 content text NOT NULL CHECK(octet_length(content) BETWEEN 1 AND 131072),
 tags text[] NOT NULL DEFAULT '{}' CHECK(cardinality(tags)<=64 AND array_position(tags,NULL) IS NULL),
 -- Upstream's words at the MCP boundary (D-030). NULL: not classified or rated yet.
 kind text CHECK(kind IN ('episodic','semantic')),
 status text NOT NULL DEFAULT 'draft' CHECK(status IN ('canonical','draft','deprecated')),
 importance smallint CHECK(importance BETWEEN 1 AND 5),
 volatility text CHECK(volatility IN ('durable','state','volatile')),
 -- The client or channel that captured it ('claude', 'app', 'cli', 'import').
 source text NOT NULL CHECK(source ~ '^[a-z][a-z0-9_.-]{0,63}$'),
 project_id uuid,
 -- A capsule slot (upstream's capsule and capsule-slot tags as columns): core slots, or a project's.
 capsule text CHECK(capsule IN ('core','project')),
 capsule_slot text,
 -- Held for review (SB-P3-18): why, and the score that held it; only a person releases it (D-039).
 hold text CHECK(hold ~ '^[a-z][a-z0-9_]{0,31}$'),
 hold_score real CHECK(hold_score>=0),
 -- When it was true: NULL from means since it was written, '-infinity' an unknown start; NULL until means still true.
 valid_from timestamptz,
 valid_until timestamptz,
 -- Optimistic concurrency: a change names the revision it read; each change adds one.
 revision integer NOT NULL DEFAULT 1 CHECK(revision>=1),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK(scope=CASE WHEN subject_id='household' THEN 'shared' ELSE 'private' END),
 CHECK(valid_until>coalesce(valid_from,created_at)),
 FOREIGN KEY(id,provider,subject_id,scope) REFERENCES resources(id,provider,subject_id,scope) ON UPDATE CASCADE,
 -- A memory sits only in a project of its own scope; a share moves it out of a private project first.
 FOREIGN KEY(project_id,subject_id) REFERENCES memory_projects(id,subject_id),
 -- A private memory's author is its owner; un-sharing returns a memory to its author's private scope only, and a
 -- memory with no person author cannot be private (memory.md 5).
 CHECK(scope='shared' OR coalesce(author_id=subject_id,false)),
 CHECK((capsule IS NULL)=(capsule_slot IS NULL)),
 CHECK(capsule IS DISTINCT FROM 'core' OR capsule_slot IN ('identity','preferences','constraints','principles')),
 CHECK(capsule IS DISTINCT FROM 'project'
   OR project_id IS NOT NULL AND capsule_slot IN ('current-state','decisions','open-questions')),
 CHECK(hold IS NOT NULL OR hold_score IS NULL)
);
CREATE INDEX memories_recent ON memories(subject_id,created_at DESC,id);
CREATE INDEX memories_tags ON memories USING gin(tags);
CREATE INDEX memories_project ON memories(project_id) WHERE project_id IS NOT NULL;
CREATE INDEX memories_capsules ON memories(subject_id,capsule,capsule_slot) WHERE capsule IS NOT NULL;
CREATE INDEX memories_held ON memories(subject_id) WHERE hold IS NOT NULL;

-- Each state a change retired, in full (DAT-14: full text first), with the scope it was in then, so history and as-of
-- answers read the words of that time. Kept with the memory, and gone with it when it is deleted for good. A state's
-- end comes after its start; a state with no start had its end checked against the memory's writing while it was
-- current (memories' own check), never against when that state was written, so a memory closed after the fact can
-- still change again.
CREATE TABLE memory_versions(
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 revision integer NOT NULL CHECK(revision>=1),
 subject_id text NOT NULL REFERENCES subjects,
 scope text NOT NULL CHECK(scope IN ('shared','private')),
 content text NOT NULL CHECK(octet_length(content) BETWEEN 1 AND 131072),
 tags text[] NOT NULL DEFAULT '{}' CHECK(cardinality(tags)<=64 AND array_position(tags,NULL) IS NULL),
 status text NOT NULL CHECK(status IN ('canonical','draft','deprecated')),
 valid_from timestamptz,
 valid_until timestamptz,
 -- The rest of that state (kind, importance, volatility, project, capsule slot, hold), as the memory's columns name it.
 state jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(state)='object'),
 written_at timestamptz NOT NULL,
 retired_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(memory_id,revision),
 CHECK(scope=CASE WHEN subject_id='household' THEN 'shared' ELSE 'private' END),
 CHECK(valid_until>valid_from),
 CHECK(retired_at>=written_at)
);

-- The audit trail: one row per change to a memory or a project, never changed or removed, not even by the owner. It
-- names who (a principal, or the operator on the host), through which channel and client, and what kind of change; it
-- never holds a memory's text (that is in the versions), so it can outlive a memory deleted for good and keep no words
-- of it. No key on the memory or project for the same reason.
CREATE TABLE memory_events(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 memory_id uuid,
 project_id uuid,
 subject_id text NOT NULL REFERENCES subjects,
 revision integer CHECK(revision>=1),
 event text NOT NULL CHECK(event ~ '^[a-z][a-z_]{1,39}$'),
 principal_id uuid REFERENCES principals,
 channel text NOT NULL CHECK(channel IN ('mcp','app','cli','system')),
 client text CHECK(length(client) BETWEEN 1 AND 80 AND client !~ '[[:cntrl:]]'),
 detail jsonb NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(detail)='object' AND length(detail::text)<=4000),
 at timestamptz NOT NULL DEFAULT now(),
 CHECK(memory_id IS NOT NULL OR project_id IS NOT NULL),
 CHECK(principal_id IS NOT NULL OR channel='cli')
);
CREATE INDEX memory_events_memory ON memory_events(memory_id,id) WHERE memory_id IS NOT NULL;
CREATE INDEX memory_events_project ON memory_events(project_id,id) WHERE project_id IS NOT NULL;
CREATE INDEX memory_events_scope ON memory_events(subject_id,at);
CREATE FUNCTION memory_events_insert_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'The memory audit trail is insert-only' USING ERRCODE='42501';
END $$;
CREATE TRIGGER memory_events_no_change BEFORE UPDATE OR DELETE ON memory_events
 FOR EACH ROW EXECUTE FUNCTION memory_events_insert_only();
CREATE TRIGGER memory_events_no_truncate BEFORE TRUNCATE ON memory_events
 FOR EACH STATEMENT EXECUTE FUNCTION memory_events_insert_only();

-- The trash: a forgotten memory keeps its row, text and versions here until it is restored or its retention ends
-- (14 days by default, 1-365, D-036); its resources row is marked deleted meanwhile, so no read serves it. Deleting it
-- for good names the nonce (from the app or the CLI, never over MCP). The links at either end when it was trashed are
-- kept to restore with it.
CREATE TABLE memory_trash(
 memory_id uuid PRIMARY KEY REFERENCES memories ON DELETE CASCADE,
 nonce uuid NOT NULL UNIQUE DEFAULT gen_random_uuid(),
 reason text NOT NULL CHECK(reason IN ('forget','disconnect','mirror')),
 trashed_by uuid REFERENCES principals,
 channel text NOT NULL CHECK(channel IN ('mcp','app','cli','system')),
 trashed_at timestamptz NOT NULL DEFAULT now(),
 purge_after timestamptz NOT NULL,
 links jsonb NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(links)='array'),
 CHECK(trashed_by IS NOT NULL OR channel='cli'),
 CHECK(purge_after-trashed_at BETWEEN interval '1 day' AND interval '365 days')
);
CREATE INDEX memory_trash_purge ON memory_trash(purge_after);

-- A capsule's revision per scope, which a client compares to reuse what it already holds. Any change to a row that is,
-- or was, a capsule slot moves the revision of each scope it touched, a trip through the trash included.
CREATE TABLE capsule_revisions(
 subject_id text PRIMARY KEY REFERENCES subjects,
 revision bigint NOT NULL DEFAULT 1 CHECK(revision>=1),
 changed_at timestamptz NOT NULL DEFAULT now()
);
CREATE FUNCTION capsule_changed(p_subject text) RETURNS void LANGUAGE sql AS $$
 INSERT INTO capsule_revisions(subject_id) VALUES(p_subject)
 ON CONFLICT(subject_id) DO UPDATE SET revision=capsule_revisions.revision+1,changed_at=now()
$$;
CREATE FUNCTION memory_capsule_changed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_NAME='memory_trash' THEN
   PERFORM capsule_changed(m.subject_id) FROM memories m
     WHERE m.id=CASE TG_OP WHEN 'DELETE' THEN OLD.memory_id ELSE NEW.memory_id END AND m.capsule IS NOT NULL;
   RETURN NULL;
 END IF;
 IF TG_OP IN ('UPDATE','DELETE') AND OLD.capsule IS NOT NULL THEN PERFORM capsule_changed(OLD.subject_id); END IF;
 IF TG_OP IN ('INSERT','UPDATE') AND NEW.capsule IS NOT NULL
   AND (TG_OP='INSERT' OR OLD.capsule IS NULL OR NEW.subject_id<>OLD.subject_id) THEN
   PERFORM capsule_changed(NEW.subject_id);
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER memories_capsule_insert AFTER INSERT ON memories
 FOR EACH ROW WHEN (NEW.capsule IS NOT NULL) EXECUTE FUNCTION memory_capsule_changed();
CREATE TRIGGER memories_capsule_update AFTER UPDATE ON memories
 FOR EACH ROW WHEN ((OLD.capsule IS NOT NULL OR NEW.capsule IS NOT NULL) AND OLD.* IS DISTINCT FROM NEW.*)
 EXECUTE FUNCTION memory_capsule_changed();
CREATE TRIGGER memories_capsule_delete AFTER DELETE ON memories
 FOR EACH ROW WHEN (OLD.capsule IS NOT NULL) EXECUTE FUNCTION memory_capsule_changed();
CREATE TRIGGER memory_trash_capsule AFTER INSERT OR DELETE ON memory_trash
 FOR EACH ROW EXECUTE FUNCTION memory_capsule_changed();

-- Links a person or an AI client makes (SB-P4-02), in a table of their own beside the map's relationships: the map's
-- rebuild, ingest and 009's invalidation never see one, and a made link and a map link of the same type between the
-- same two records both exist (relationships keys a link by its ends and type alone). A made link has its own id,
-- kept through a share or un-share of either end (memory.md 10.3), and the principal that made it. Like the memory
-- tables, it changes only through its functions (checked_tables()). Its ends never cascade: a record a made link names
-- is not deleted for good until a checked function has removed the link, so a person's links are never removed
-- automatically (SB-P4-04 reports one whose end is gone). Phase 4's reads take made links beside relationships.
CREATE TABLE made_links(
 link_id uuid PRIMARY KEY,
 source_id uuid NOT NULL REFERENCES resources,
 target_id uuid NOT NULL REFERENCES resources,
 type text NOT NULL CHECK(type ~ '^[a-z][a-z0-9_-]{0,63}$'),
 verification text NOT NULL DEFAULT 'verified' CHECK(verification IN ('verified','suggested','inferred')),
 evidence text NOT NULL DEFAULT '' CHECK(length(evidence)<=1000),
 lifecycle text NOT NULL DEFAULT 'active' CHECK(lifecycle IN ('active','stale','retired','revoked','deleted')),
 made_by uuid NOT NULL REFERENCES principals,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(source_id,target_id,type),
 CHECK(source_id<>target_id)
);
CREATE INDEX made_links_target ON made_links(target_id);

-- The tables written only through checked functions. A later migration that adds one replaces this list.
CREATE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS $$
 SELECT ARRAY['memories','memory_versions','memory_events','memory_trash','memory_projects','memory_project_aliases',
   'capsule_revisions','made_links']::regclass[]
$$;

REVOKE ALL ON memory_projects,memory_project_aliases,memories,memory_versions,memory_events,memory_trash,
 capsule_revisions,made_links FROM PUBLIC;
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION guard_memory_resources() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_events_insert_only() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION capsule_changed(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_capsule_changed() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION capsule_changed(text),guard_memory_resources(),memory_events_insert_only(),memory_capsule_changed(),
 checked_tables() FROM PUBLIC;
