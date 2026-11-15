-- A native memory's history (docs/dev/memory.md section 5, read its history; SB-P3-03 in
-- docs/dev/second-brain-parity/backlog.md): how many of its earlier states are kept, and the rule for how far back a
-- reader sees. The states themselves are written by 033's checked writers (memory_retire, one memory_versions row per
-- change) and its audit rows by the same writers (one memory_events row per change, never removed); the engine reads
-- both through the read predicate (Core/MemoryHistory.cs). Nothing calls a writer yet (D-050), so no install holds a
-- version and every existing answer is as before. The number is provisional.

-- How many of a memory's earlier states are kept: 20, as upstream (D-036). Its audit rows are all kept, so history
-- still lists every change, an older one without its words.
CREATE FUNCTION memory_versions_kept() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 20 $$;

-- After a state is retired, the memory's earlier states beyond the newest memory_versions_kept() go, whoever retired it
-- and in whatever order the states arrive (a restore's or an import's included).
CREATE FUNCTION memory_versions_prune() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 DELETE FROM memory_versions v WHERE v.memory_id=NEW.memory_id AND v.revision<(
   SELECT min(k.revision) FROM (SELECT x.revision FROM memory_versions x WHERE x.memory_id=NEW.memory_id
     ORDER BY x.revision DESC LIMIT memory_versions_kept()) k);
 RETURN NULL;
END $$;
CREATE TRIGGER memory_versions_kept AFTER INSERT ON memory_versions FOR EACH ROW EXECUTE FUNCTION memory_versions_prune();

-- The oldest revision of a memory's history a principal sees (memory.md section 5): history from the move that brought
-- the memory into a scope the reader reads, as upstream's shared-history cut. Every state and every audit row carries
-- the subject the memory had at its revision (a state its own; an audit row the memory's after the change it records),
-- so the cut falls just above the newest revision with any record in a subject the reader does not read. Its author
-- reads every scope the memory has been in (its own private scope and Family) and so sees all of it, unless the author
-- has left Family since: then, as for any reader outside Family, the cut falls above its Family stretch. A memory no
-- person wrote is never private, so every reader of it sees all of it; with scopes off every admitted login reads
-- every subject. A share or un-share (SB-P3-10) is a change like any other: it retires the state it replaces, moves
-- the revision and records its audit row under the subject it lands in, so the arrival is the first revision its new
-- readers see. Through reads_subject, which takes no source share, as 033's writers do (a share never names memory).
-- Undoing back to a version (SB-P3-05) takes the same rule. A revision above the current one means the reader sees
-- none of it; the caller checks first that it reads the memory at all.
CREATE FUNCTION memory_history_floor(p_principal uuid,p_memory uuid) RETURNS integer LANGUAGE sql STABLE AS $$
 WITH x AS (
   SELECT m.revision,m.subject_id FROM memories m WHERE m.id=p_memory
   UNION ALL SELECT v.revision,v.subject_id FROM memory_versions v WHERE v.memory_id=p_memory
   UNION ALL SELECT e.revision,e.subject_id FROM memory_events e WHERE e.memory_id=p_memory AND e.revision IS NOT NULL),
 hidden AS (SELECT s.subject_id FROM (SELECT DISTINCT x.subject_id FROM x) s WHERE NOT reads_subject(p_principal,s.subject_id))
 SELECT coalesce((SELECT max(x.revision) FROM x WHERE x.subject_id IN (SELECT h.subject_id FROM hidden h)),0)+1
$$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_versions_kept() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_versions_prune() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_history_floor(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_versions_kept(),memory_versions_prune(),memory_history_floor(uuid,uuid) FROM PUBLIC;
