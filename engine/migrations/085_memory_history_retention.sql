-- Keep the last copy of each wording in the scope and hold state where it can be read.
-- The trigger runs when a writer retires a state, before it overwrites the current wording.
-- Compare retired states to each other so the soon-to-be-replaced live row never counts as a copy.
CREATE OR REPLACE FUNCTION memory_versions_prune() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE excess integer;
BEGIN
 SELECT count(*)-memory_versions_kept() INTO excess FROM memory_versions WHERE memory_id=NEW.memory_id;
 IF excess>0 THEN
   WITH ranked AS (
     SELECT revision,state,row_number() OVER (
       PARTITION BY subject_id,scope,(state->>'hold'),content ORDER BY revision DESC) AS copy
     FROM memory_versions WHERE memory_id=NEW.memory_id),
   discard AS (
     SELECT revision FROM ranked WHERE copy>1
     ORDER BY (state ? 'moved' OR state ? 'cascade') DESC,revision LIMIT excess)
   DELETE FROM memory_versions v USING discard d WHERE v.memory_id=NEW.memory_id AND v.revision=d.revision;
 END IF;
 RETURN NULL;
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_versions_prune() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_versions_prune() FROM PUBLIC;
