-- Where an imported Second Brain memory lived there (SB-P8-08; D-242, D-247; engine/src/Cli/SecondBrainImport.cs,
-- rhythm import-second-brain --household; docs/dev/second-brain-parity/migration-runbook.md). With --household, while
-- memory.scopes is off, the importer brings every memory in as the household's, the importing person its author, and
-- keeps beside it which workspace it lived in: the login's personal workspace, a team workspace, or unknown. The export
-- does not say (ledger EXP-01), so the terminal takes it from the read-only Second Brain connector's rows (its personal
-- workspace's memories private, a team workspace's shared) or the owner's scope map, and passes it with the origin; 059's
-- checked functions write it into the memory's row of the origin map, so turning scopes on later can tell a person's own
-- memories from a team's. Nothing reads it yet. Every other row, and every import without --household, leaves it empty:
-- nothing changes for an install that never runs the household import. The number is provisional.

ALTER TABLE memory_origins ADD COLUMN workspace text CHECK(workspace IN ('personal','team','unknown')),
 ADD CONSTRAINT memory_origins_workspace_kind CHECK(workspace IS NULL OR kind='memory');

-- 059's memory_import_origin_add, with the workspace the origin names ({"origin","export","exportedAt","workspace"}),
-- none when it names none; otherwise as it was.
CREATE OR REPLACE FUNCTION memory_import_origin_add(p_origin jsonb,p_kind text,p_source text,p_id uuid,p_subject text) RETURNS void LANGUAGE sql AS $$
 INSERT INTO memory_origins(origin,kind,source_id,local_id,subject_id,export_id,exported_at,workspace)
 VALUES(p_origin->>'origin',p_kind,p_source,p_id,p_subject,(p_origin->>'export')::uuid,(p_origin->>'exportedAt')::timestamptz,p_origin->>'workspace')
$$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so it gets its search path again.
 EXECUTE format('ALTER FUNCTION memory_import_origin_add(jsonb,text,text,uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_import_origin_add(jsonb,text,text,uuid,text) FROM PUBLIC;
