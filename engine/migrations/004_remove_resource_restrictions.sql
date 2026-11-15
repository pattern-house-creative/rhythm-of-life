-- User correction: use existing connection/subject/provider/scope grants, without
-- an additional per-document audience layer. Migration 003 already ran locally,
-- so preserve its checksum and remove the unused policy tables with a new migration.
-- Guard against silently discarding a policy configured in an unexpected installation.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM resource_restrictions) THEN
   RAISE EXCEPTION 'Unexpected resource restrictions exist; review before removing policy tables';
 END IF;
END $$;
DROP TABLE resource_readers;
DROP TABLE resource_restrictions;
