-- Prototype only: reuse the catalog and relationship primary key. No new tables.
-- Private processing state is not resource.metadata (legacy retrieval exposes that field).
ALTER TABLE resources ADD COLUMN relationship_snapshot jsonb;
ALTER TABLE relationships ADD COLUMN engine text;
ALTER TABLE relationships ADD COLUMN lifecycle text NOT NULL DEFAULT 'active'
 CHECK(lifecycle IN ('active','stale','retired','revoked','deleted'));
ALTER TABLE relationships ADD COLUMN details jsonb NOT NULL DEFAULT '{}';
ALTER TABLE relationships ADD COLUMN checked_at timestamptz;

-- Any writer changing an endpoint invalidates derived claims, including old ingest paths.
-- Keep the row/key for stable reactivation; discard cached evidence immediately.
CREATE FUNCTION invalidate_discovered_relationships() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.status<>'current' AND NEW.relationship_snapshot IS NOT NULL THEN
   NEW.relationship_snapshot=NEW.relationship_snapshot ||
     jsonb_build_object('Title','Unavailable','Url',NULL,'References','[]'::jsonb,'Status',NEW.status);
 END IF;
 IF NEW.status IS DISTINCT FROM OLD.status OR NEW.revision IS DISTINCT FROM OLD.revision
 OR NEW.source_updated_at IS DISTINCT FROM OLD.source_updated_at
 OR NEW.content IS DISTINCT FROM OLD.content OR NEW.title IS DISTINCT FROM OLD.title
 OR NEW.source_url IS DISTINCT FROM OLD.source_url OR NEW.metadata IS DISTINCT FROM OLD.metadata
 OR (NEW.relationship_snapshot - 'RetrievedAt') IS DISTINCT FROM (OLD.relationship_snapshot - 'RetrievedAt')
 OR NEW.scope IS DISTINCT FROM OLD.scope OR NEW.authorized_until IS DISTINCT FROM OLD.authorized_until THEN
   UPDATE relationships SET lifecycle=CASE WHEN NEW.status IN ('deleted','revoked') THEN NEW.status ELSE 'stale' END,
     evidence='',details='{}'
   WHERE engine='cross-source-v1' AND (source_id=NEW.id OR target_id=NEW.id);
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER discovered_relationship_invalidation BEFORE UPDATE ON resources
 FOR EACH ROW EXECUTE FUNCTION invalidate_discovered_relationships();
