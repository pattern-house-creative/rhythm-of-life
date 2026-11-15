-- The operator installs the pinned extension package before applying this migration.
CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;
CREATE TABLE resource_chunks(
 resource_id uuid NOT NULL REFERENCES resources ON DELETE CASCADE,
 ordinal integer NOT NULL CHECK(ordinal>=0),
 source_revision text NOT NULL,
 content_hash text NOT NULL,
 model text NOT NULL,
 text_content text NOT NULL,
 embedding public.vector(384) NOT NULL,
 indexed_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(resource_id,ordinal)
);
-- Exact search suits household scale; no approximate index or extra service.
CREATE INDEX resource_chunks_revision ON resource_chunks(resource_id,source_revision);

-- Derived text/vectors must not survive a changed or revoked source revision.
CREATE FUNCTION invalidate_resource_chunks() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.status<>'current' OR NEW.revision IS DISTINCT FROM OLD.revision
    OR NEW.content IS DISTINCT FROM OLD.content OR NEW.title IS DISTINCT FROM OLD.title
    OR NEW.metadata->>'semanticIndex' IS DISTINCT FROM OLD.metadata->>'semanticIndex' THEN
   DELETE FROM resource_chunks WHERE resource_id=NEW.id;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER resource_chunks_invalidation AFTER UPDATE ON resources
 FOR EACH ROW EXECUTE FUNCTION invalidate_resource_chunks();
