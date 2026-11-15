-- A shadow index (Core/EmbeddingBackfill.cs): one model's passages kept beside another's, so a second embedding model
-- can be built and measured while search reads the first, and neither's passages are dropped when the other takes over.
-- A passage is keyed by its model as well; every read names one model (Core/SemanticSearch.cs), so no query compares
-- two models' vectors.
ALTER TABLE resource_chunks DROP CONSTRAINT resource_chunks_pkey;
ALTER TABLE resource_chunks ADD PRIMARY KEY(resource_id,model,ordinal);

-- Where the index job's sweep has got to, one row per model, so a run resumes after a stop (decisions D-017): the last
-- row it reached in id order, the sweep's counts, and a pause after a sweep in which nothing succeeded. Counts only:
-- never a title, a text or a vector.
CREATE TABLE embedding_backfill(
 model text PRIMARY KEY CHECK(length(model) BETWEEN 1 AND 100),
 after_id uuid,
 sweep integer NOT NULL DEFAULT 1 CHECK(sweep>=1),
 indexed integer NOT NULL DEFAULT 0 CHECK(indexed>=0),
 failed integer NOT NULL DEFAULT 0 CHECK(failed>=0),
 paused_until timestamptz,
 updated_at timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON embedding_backfill FROM PUBLIC;
