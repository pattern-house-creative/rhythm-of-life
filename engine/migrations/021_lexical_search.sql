-- Ranked full text (search_knowledge, search_resources; Core/LexicalSearch.cs) matches words in the english text search
-- configuration, stemmed, so "collected" finds "collection". Chosen on the parity golden set in both load modes
-- (docs/dev/second-brain-parity/lexical-search.md). The simple index from 001 stays for engine.legacySearch.
CREATE INDEX resources_search_english ON resources USING gin(to_tsvector('english',title || ' ' || content));
