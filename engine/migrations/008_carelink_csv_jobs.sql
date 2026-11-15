ALTER TABLE sync_jobs ADD COLUMN metadata jsonb NOT NULL DEFAULT '{}';
-- A plugin-specific queue function was here in the private version; it moved out with that plugin.
