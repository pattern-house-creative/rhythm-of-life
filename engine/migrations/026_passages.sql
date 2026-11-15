-- Passages for every searchable text (SB-P2-05; docs/dev/second-brain-parity/backlog.md). The embeddings index job
-- (Core/EmbeddingBackfill.cs) now also gives passages to the documents adapter's uploads and dropped files, to the records
-- of graph sources that read their text on request (notes, readable Drive files) and to native memories, as
-- adapters.embeddings.sources lists them (Core/PassageSources.cs). Passages stay what 005 and 024 made them: one row per
-- passage, model and ordinal, at the revision of the row they belong to, dropped when that row changes (005's trigger).

-- A row whose text gave no passage at a revision: a Drive file that does not read as words, a note with no body. The
-- index job passes such a row over until its revision changes, so it does not read the source again every round.
-- Ids and revisions only: never a title, a text or a vector.
CREATE TABLE no_passages(
 resource_id uuid PRIMARY KEY REFERENCES resources ON DELETE CASCADE,
 source_revision text,
 checked_at timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON no_passages FROM PUBLIC;

-- A native memory's passages are made from its text in `memories`, not from its resources row, so 005's trigger on that
-- row does not see the text change. Here a change of a memory's text, or a hold put on it or lifted, drops its passages
-- at once, whatever its resources row says, and the index job makes them again (a held memory gets none). The job holds
-- the memory's resources row while it reads the text and writes the passages (SemanticSearch.Index); taking the same
-- lock first means passages made from the old text are there to delete. Nothing writes a memory yet (025): phase 3's
-- checked functions run as the owner, and so does this trigger. The order of the two locks is the writers' to keep:
-- this trigger takes the memories row, then the resources row, while a share or un-share takes the resources row, then
-- the memories row (025's ON UPDATE CASCADE). So every checked function that changes a memory locks its resources row
-- first (SELECT ... FOR UPDATE) and only then updates `memories`, or a concurrent edit and share of one memory deadlock.
CREATE FUNCTION invalidate_memory_passages() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.content IS DISTINCT FROM OLD.content OR NEW.hold IS DISTINCT FROM OLD.hold THEN
   PERFORM 1 FROM resources WHERE id=NEW.id FOR UPDATE;
   DELETE FROM resource_chunks WHERE resource_id=NEW.id;
   DELETE FROM no_passages WHERE resource_id=NEW.id;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memory_passages_invalidation AFTER UPDATE OF content,hold ON memories
 FOR EACH ROW EXECUTE FUNCTION invalidate_memory_passages();
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION invalidate_memory_passages() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION invalidate_memory_passages() FROM PUBLIC;
