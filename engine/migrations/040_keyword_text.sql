-- Keyword search over passage text and native memory text (SB-P2-19, D-152, D-153;
-- docs/dev/second-brain-parity/combined-retrieval.md). Combined retrieval's keyword arm (Core/Recall.cs) finds a
-- record whose words only its passages hold (a note or a Drive file as installed: its star row has no content), and a
-- native memory, whose resources row carries none of its words (033), by those words, in the english configuration
-- ranked keyword search uses (021). Indexes only: no table, row or function changes, so every other search answers as
-- before. What a reader is served stays the arm's to decide, under the read predicate: a record's passages only while
-- the map would serve its star, and never a held memory's text. IF NOT EXISTS while the number is provisional: a
-- database that applied this file under another number takes it again as a new migration, and runs it cleanly.
CREATE INDEX IF NOT EXISTS resource_chunks_search_english ON resource_chunks USING gin(to_tsvector('english',text_content));
CREATE INDEX IF NOT EXISTS memories_search_english ON memories USING gin(to_tsvector('english',content));
