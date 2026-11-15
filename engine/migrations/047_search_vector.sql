-- A stored text vector for ranked keyword search (SB-P2-21 in docs/dev/second-brain-parity/backlog.md; D-054 as amended,
-- Q-156; docs/dev/second-brain-parity/lexical-search.md). Ranked search (Core/LexicalSearch.cs: search_knowledge,
-- search_resources, combined retrieval's catalog tier) matched and scored a row by parsing its title and content in the
-- english configuration at every search, once per candidate tier and again to score it, and the planner could not know
-- how costly that is on a long saved document (save_document takes 40 000 characters): on 1 000 such rows every ranked
-- query took 2 to 13 s against D-029's 500 ms. Here each row keeps that parse, made once when the row is written, in a
-- generated column with its own index, which the tiers match and scoring reads. The column holds exactly what the
-- search parsed before (the same configuration, title, a space, content), so every answer, its order included, is the
-- one before; only the time it takes changes.
--
-- Adding a stored generated column rewrites the resources table, every row parsed once, while the migration runs
-- (lexical-search.md gives the time at the scale run's size). 021's expression index on the same parse is dropped: no
-- query reads that expression now, and keeping it would make every write parse each row twice and keep two copies. An
-- engine from before this migration still answers on this schema, as slowly as before 021. The legacy query (the
-- simple configuration, engine.legacySearch) keeps 001's index and parses as before.
--
-- 025's guard_memory_resources lets the engine's login change a native memory's own resources row only when nothing but
-- indexed_at changes, by comparing the whole row before and after. In a BEFORE trigger a stored generated column is
-- still null in NEW, so that comparison would refuse the update; the column is left out of both sides (it is derived
-- from title and content, which the comparison still holds). IF NOT EXISTS while the number is provisional.
ALTER TABLE resources ADD COLUMN IF NOT EXISTS search_english tsvector
 GENERATED ALWAYS AS (to_tsvector('english'::regconfig,title || ' ' || content)) STORED;
CREATE INDEX IF NOT EXISTS resources_search_vector ON resources USING gin(search_english);
DROP INDEX IF EXISTS resources_search_english;

-- 025's guard, with the stored text vector out of the whole-row comparison; otherwise as it was.
CREATE OR REPLACE FUNCTION guard_memory_resources() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
 old_star boolean := TG_OP<>'INSERT' AND OLD.provider='memory' AND starts_with(OLD.external_id,'relationship-engine:')
   AND OLD.relationship_snapshot IS NOT NULL AND NOT EXISTS(SELECT 1 FROM memories WHERE id=OLD.id);
 new_star boolean := TG_OP<>'DELETE' AND NEW.provider='memory' AND starts_with(NEW.external_id,'relationship-engine:')
   AND NEW.relationship_snapshot IS NOT NULL AND NOT EXISTS(SELECT 1 FROM memories WHERE id=NEW.id);
 -- The login may write a star row, and on a memory's own row change when it was last indexed; nothing else.
 allowed boolean := CASE TG_OP WHEN 'INSERT' THEN new_star WHEN 'DELETE' THEN old_star
   ELSE old_star AND new_star OR OLD.provider='memory' AND NOT old_star
     AND to_jsonb(NEW)-'indexed_at'-'search_english' IS NOT DISTINCT FROM to_jsonb(OLD)-'indexed_at'-'search_english' END;
BEGIN
 IF current_user<>(SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid=TG_RELID) AND NOT allowed THEN
   RAISE EXCEPTION 'A memory''s resources row changes only through its functions' USING ERRCODE='42501';
 END IF;
 RETURN CASE TG_OP WHEN 'DELETE' THEN OLD ELSE NEW END;
END $$;
DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION guard_memory_resources() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION guard_memory_resources() FROM PUBLIC;
