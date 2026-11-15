-- Required BEFORE exposing a restored database. Old backups can resurrect revoked access.
-- Every token is disabled (create new ones with `token create`; the engine re-creates the app's from its file),
-- legacy grants are removed, a viewer session's one-time code not used yet is removed (so a restore never brings back
-- one the operator revoked since), and the map's snapshot rows are marked revoked until the next refresh rebuilds them. A
-- native memory's own row is the record itself, never a snapshot (migration 025), and is left as it is.
-- authorized_until stays as it was: a refresh writes status and title back but never that column, so setting it here
-- would hide every star from before the backup for good. The admin audit (migration 037) records the tokens disabled
-- here as a restore's: plain settings, which a backup from before 037 takes and ignores. A memory in the trash gets the
-- trash's retention again from the restore (14 days, 038's memory_trash_days, or what it had left if that is longer),
-- so the engine's first daily purge after a restore never deletes forever what the restore brought back before anyone
-- could see it or restore it (D-019's separate, visible steps). 025 keeps a trash row's retention within 365 days of
-- its trashed_at, so a row trashed more than 351 days before the restore is dated 351 days before it, the latest date
-- that leaves it 14 days; when it was trashed stays on its audit row (the memory_events trashed row), which nothing
-- changes.
BEGIN;
DO $$ BEGIN PERFORM set_config('rhythm.audit_channel','system',true),set_config('rhythm.audit_job','restore',true); END $$;
UPDATE principals SET enabled=false WHERE role<>'system';
DELETE FROM grants;
-- viewer_codes is 028's: a backup from before it has none.
DO $$ BEGIN IF to_regclass('viewer_codes') IS NOT NULL THEN DELETE FROM viewer_codes WHERE used_at IS NULL; END IF; END $$;
-- memory_trash is 025's: a backup from before it has none.
DO $$ BEGIN IF to_regclass('memory_trash') IS NOT NULL THEN
  UPDATE memory_trash SET trashed_at=greatest(trashed_at,now()-interval '351 days'),
    purge_after=least(greatest(trashed_at,now()-interval '351 days')+interval '365 days',greatest(purge_after,now()+interval '14 days'));
END IF; END $$;
-- pg_restore --no-acl restores functions with PostgreSQL's default PUBLIC execute.
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
UPDATE resources SET status='revoked',content='',title='Unavailable',metadata='{}'
WHERE relationship_snapshot IS NOT NULL AND NOT (provider='memory' AND external_id=id::text);
UPDATE sync_jobs SET status='failed',last_error='restore_requires_review' WHERE status<>'complete';
COMMIT;
