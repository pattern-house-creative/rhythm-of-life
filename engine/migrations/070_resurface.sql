-- A resurfaced old memory on Home (SB-P5-17 in docs/dev/second-brain-parity/backlog.md; ledger rows API-26, INT-10 and UI-04;
-- docs/dev/memory.md sections 5 and 7.2), adapted from Second Brain's resurface pick (src/brief/compute.ts and
-- src/runtime/resurface-state.ts at the pinned commit, MIT; NOTICE): one memory a day worth reading again, which a person may
-- dismiss, and a dismissed one never comes back.
--
-- memory_resurface_eligible(memory, before): whether a memory may be the pick. A memory written before that moment, in the
-- catalog as current, out of the trash, not held, not marked wrong, its window open, not built on something marked wrong, and
-- none of the engine's own (source system, a digest). Nor a mirror, an outside source's copy that is no memory of the person's
-- (067 leaves one out of insights for the same reason), nor a capture no person has confirmed: what it saved is not trusted, the
-- card names no status or source to say so, and once a person confirms one it may be. After a Second Brain import, none of what upstream's own jobs wrote
-- either: its filter leaves out the tags auto-pattern, auto-insight and synthesized, which the import keeps in 059's
-- memory_upstream_tags, so this is that table's first read (059's comment says nothing reads it; an install that never ran
-- the import holds no row). Not an event (kind episodic: a true-then fact, upstream's reason), a finished task (task:done),
-- a capsule slot or a standing instruction (both already in front of an AI client, so nothing to be reminded of), or one
-- rated below 3. An unrated memory may be: importance NULL here is "not rated yet", where upstream's column defaults to 0 and
-- a nightly pass rates it. So may a memory upstream never classified, whose 0 an import brings as NULL.
--
-- memory_resurface_before(day): the moment a memory must be older than on that day: 60 days (upstream's) before that day's
-- first moment in the install's zone (D-043), so a memory reaches the age at a day's start and never in its middle, which
-- would move the pick.
--
-- The pick is a read and writes nothing (Core/MemoryResurface.cs): of what the caller reads (the one read predicate on each
-- memory's own row), eligible and not dismissed, the one whose md5 of the day and its id comes first. So a reload shows the
-- same one all day, on any device: taking another memory away moves the pick only if it was the pick, and a new one takes it
-- only by coming first. Upstream keeps the day's pick in a store and a list of what it showed; here nothing is kept, so a
-- memory can come again within weeks, as chance has it.
--
-- memory_resurface_dismissals: who retired which memory for good, one row a memory and a reader. The reader is the person the
-- login speaks for, or the household for a login with no person (the shared app token). It is the person's judgement on
-- reading a memory again and no change of the memory: no revision, version or audit row (dismissed_by keeps who), and undo
-- does not take it back. It goes when the memory is deleted for good and is not exported, as the map's dismissals (052, 057).
-- Like the memory tables, it changes only through its function (checked_tables()).
--
-- memory_resurface_dismissed(principal, memory): whether the memory is retired for this reader: with scopes on, by that person
-- or by the household, so a dismissal at the shared app's Home holds wherever the memory is read; with scopes off everyone
-- reads as one household, so by anyone. Alex dismissing a Family memory leaves it for Sam.
--
-- dismiss_resurfaced(principal, memory, channel, client): a person's step, the app's login or the terminal's, never an AI
-- client's channel (mcp). Any memory the caller reads, in the trash or held too: the card may have drawn it before it went,
-- the row changes no memory and says nothing of its words, and a restore or a release must not bring back what a person
-- dismissed. Else unknown, as for one that never was: one the caller does not read or one deleted for good, and nothing is
-- kept. Dismissing again answers unchanged. No idempotency key: a repeat answers unchanged.
--
-- Nothing calls these but the app's Memories tool (app_memories ops resurface and resurface_dismiss, Mcp/AppMemories.cs, made
-- only with memory.tools on, off by default), and tests: with none called, no install holds a row and every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login or an AI client's channel; P0002 a memory the caller does not read, or
-- one not there; 22023 invalid input. A refused call writes nothing. The number is provisional.

CREATE TABLE memory_resurface_dismissals(
 memory_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 reader_id text NOT NULL REFERENCES subjects,
 dismissed_by uuid NOT NULL REFERENCES principals,
 dismissed_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(memory_id,reader_id)
);
CREATE INDEX memory_resurface_dismissals_at ON memory_resurface_dismissals(dismissed_at);
REVOKE ALL ON memory_resurface_dismissals FROM PUBLIC;

-- The list of checked tables in force, with this one added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_resurface_dismissals']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

CREATE FUNCTION memory_resurface_before(p_day date) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT (p_day-60)::timestamp AT TIME ZONE install_timezone()
$$;

CREATE FUNCTION memory_resurface_eligible(p_memory uuid,p_before timestamptz) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce((SELECT m.created_at<p_before AND m.status<>'deprecated' AND m.hold IS NULL AND m.source<>'system'
     AND m.capsule IS NULL AND NOT m.standing AND m.kind IS DISTINCT FROM 'episodic' AND NOT 'task:done'=ANY(m.tags)
     AND (m.importance IS NULL OR m.importance>=3)
     AND (m.valid_until IS NULL OR m.valid_until>now())
     AND NOT EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
     AND NOT EXISTS(SELECT 1 FROM memory_retraction_flags f WHERE f.memory_id=m.id)
     AND NOT EXISTS(SELECT 1 FROM memory_mirrors x WHERE x.memory_id=m.id)
     AND NOT (m.status='draft' AND EXISTS(SELECT 1 FROM memory_events e WHERE e.memory_id=m.id AND e.event='created' AND e.channel='capture'))
     AND NOT EXISTS(SELECT 1 FROM memory_upstream_tags u WHERE u.memory_id=m.id
       AND u.tags && ARRAY['auto-pattern','auto-insight','synthesized'])
     AND EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL
       AND r.status='current')
   FROM memories m WHERE m.id=p_memory),false)
$$;

CREATE FUNCTION memory_resurface_dismissed(p_principal uuid,p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM memory_resurface_dismissals d WHERE d.memory_id=p_memory
   AND (NOT scopes_on() OR d.reader_id='household'
     OR d.reader_id=(SELECT p.person_id FROM principals p WHERE p.id=p_principal)))
$$;

CREATE FUNCTION dismiss_resurfaced(p_principal uuid,p_memory uuid,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; v_subject text; reader text; written boolean;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Dismissing a resurfaced memory is not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel='mcp' THEN
   RAISE EXCEPTION 'Dismissing a resurfaced memory is a person''s step, in the app' USING ERRCODE='42501';
 END IF;
 IF p_memory IS NULL THEN
   RAISE EXCEPTION 'A dismissal names a memory' USING ERRCODE='22023';
 END IF;
 -- Held while the row is written: a delete forever waits, so the key below never fails on a memory just gone.
 SELECT x.subject_id INTO v_subject FROM memories x WHERE x.id=p_memory FOR KEY SHARE;
 IF v_subject IS NULL OR NOT reads_subject(p_principal,v_subject) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 SELECT coalesce(p.person_id,'household') INTO reader FROM principals p WHERE p.id=p_principal;
 INSERT INTO memory_resurface_dismissals(memory_id,reader_id,dismissed_by) VALUES(p_memory,reader,p_principal) ON CONFLICT DO NOTHING;
 written:=FOUND;
 RETURN jsonb_build_object('id',p_memory,'unchanged',NOT written);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the replaced one gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_resurface_before(date) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_resurface_eligible(uuid,timestamptz) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_resurface_dismissed(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION dismiss_resurfaced(uuid,uuid,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_resurface_before(date),memory_resurface_eligible(uuid,timestamptz),
 memory_resurface_dismissed(uuid,uuid),dismiss_resurfaced(uuid,uuid,text,text) FROM PUBLIC;
