-- Links a person or an AI client makes between two native memories (SB-P4-02 in docs/dev/second-brain-parity/backlog.md;
-- D-067, D-191, D-031, D-015, D-152; docs/dev/memory.md sections 5 and 8; ledger rows MCP-18 to MCP-20, GRA-03, RHY-13).
-- Adapted from Second Brain's link and unlink (src/mcp/server.ts and src/graph/edges.ts at the pinned commit, MIT; NOTICE):
-- an explicit typed link inside one scope, the episodic rule for decided and follows, relates_to stored with its ends in
-- one order, and an automatic link that unlinking dismisses rather than deletes.
--
-- The links live in made_links (025, D-067), beside the map's relationships: the map's rebuild, ingest and 009's
-- invalidation never see one, so a made link survives every rebuild. With memory.tools on, the map's reads take them
-- beside its own links (RelationshipEngine.Readable, MadeLinks), and so do combined retrieval's hops (Recall.Expand,
-- RecallOptions.MadeLinks), each end through the reader's own predicate on the memory's own row, so a held memory is
-- never reached (D-152); off, neither reads one.
--
-- link_memories: one link from a memory to another of the same scope, of one of Second Brain's types but supersedes (a
-- replacement is supersede's, 049, which closes the earlier memory's window too). Both memories are read by the caller
-- (else unknown, as for a memory it does not read); the two are in one scope (two scopes never link, stricter than
-- upstream, which checks only the workspace: memory.md section 5); the caller may change the source end (033's author
-- lock: a private memory's owner; a Family memory's author or a Family admin; one no person wrote, any reader of Family;
-- with scopes off any admitted login, D-031); neither is held for review (a held memory's words wait for a person); and
-- decided and follows join two episodic memories only (051's link_kinds_allow, D-191). An AI client's link is inferred, a
-- person's (the app's login, the terminal's) verified (D-015); it carries the source end's revision it was made from and
-- when it was made as checked, and a confidence when the caller gives one. relates_to has no direction, so its ends are
-- stored in one order (the smaller id first, D-191). A link that is there already answers unchanged. A link made or
-- made anew takes back a dismissal of its type between the two memories (below), since the caller now says they belong
-- together that way. No evidence text: a client's words about a link are not screened as a memory's are (036), so a
-- link carries none.
--
-- unlink_memories: the made links between two memories, either way, of one type or of every type but supersedes (049's
-- link from a newer memory to the one it replaced, which says the earlier one is replaced and which the retraction
-- cascade follows: supersede makes it, and an undo or a retraction takes it away, so unlink leaves it, and refuses it
-- when named). Each is removed only by who may (memory.md section 5): its maker (the login, or another login of the same
-- person), or whoever may change its source end, or for a type with no direction (relates_to) either end, since either
-- end's author could have made it (the owner; in Family its author or an admin; one no person wrote, any reader of
-- Family; with scopes off, any admitted login); an import's link has no maker, so the rest of the rule (043). One the
-- caller may not remove refuses the whole call, and nothing changes. A made link a person or a client stated is
-- deleted; an automatic one (verification suggested: none is written yet but by an import, SB-P4-03's links on save to
-- come) is deleted too, and its two memories and type kept as a dismissal (memory_link_dismissals, below), so it is
-- never suggested again and no link check counts it. The map's own suggestions between their stars (a similarity the
-- rebuild found, of that type or of any) are dismissed in the same call, as upstream's unlink removes links of every
-- provenance: each a row of memory_link_dismissals for the two memories and its type, by the caller, which every read
-- of the map, every hop and connections then leave that suggestion out by, and which survives every rebuild (the
-- rebuild never reads it). Who may dismiss one (memory.md section 5): whoever may change either memory. A suggestion
-- may join two scopes (the map's bridges join a person's private memory and a Family one), and a dismissal is no link,
-- so it is kept apart from made_links: no link check (048's share, a restore's or an export's count of links) ever
-- counts one. A dismissal writes no audit row (dismissed_by keeps who made it), and only a link of its type made
-- between the two takes one back: no tool, route or command lists one yet (SB-P4-05).
--
-- 052 replaces one earlier function, checked_tables(), rebuilt from the list in force with the dismissals added (043's
-- way), and sets its search path again.
--
-- A link changes no memory: no revision moves and no audit row is written (a memory's history lists its own changes;
-- made_links keeps who made each link and when). 038's trash keeps a memory's made links, 043's export and import carry
-- them, and 049's cascade follows the active drawn_from and caused_by ones. A dismissal stays where it is while either
-- memory is in the trash, goes when either is deleted for good, and is not exported.
--
-- Nothing calls these but the memory tools (link, unlink; connections reads made_links itself), made only with
-- memory.tools on (off by default), and tests: with none called no install holds a made link but 049's supersedes and
-- an import's, and with memory.tools off the map and the hops read no made link, so every answer is as before.
--
-- Errors, as 033's: 42501 not available to this login, or not this caller's to link or remove; P0002 an unknown memory,
-- also one the caller does not read or one in the trash; 22023 invalid input and, with the hint held, a memory held for
-- review. A refused call writes nothing. The number is provisional.

-- The map's suggestions a person or an AI client dismissed (unlink_memories), one row a pair of memories and a type, the
-- smaller id first, since a suggestion has no direction to keep. Like the memory tables, it changes only through its
-- functions (checked_tables()).
CREATE TABLE memory_link_dismissals(
 low_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 high_id uuid NOT NULL REFERENCES memories ON DELETE CASCADE,
 type text NOT NULL CHECK(type ~ '^[a-z][a-z0-9_-]{0,63}$'),
 dismissed_by uuid NOT NULL REFERENCES principals,
 dismissed_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(low_id,high_id,type),
 CHECK(low_id<high_id)
);
CREATE INDEX memory_link_dismissals_high ON memory_link_dismissals(high_id);
REVOKE ALL ON memory_link_dismissals FROM PUBLIC;

-- The list of checked tables in force, with the dismissals added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['memory_link_dismissals']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The two ends of a link locked as a change locks a memory (its resources row, then the memory, D-086), each in id order,
-- for share: a share or a change of either waits for the link, so its scope and its hold are the ones checked.
CREATE FUNCTION memory_link_lock(p_a uuid,p_b uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 PERFORM 1 FROM resources r WHERE r.id IN (p_a,p_b) AND r.provider='memory' AND r.relationship_snapshot IS NULL ORDER BY r.id FOR SHARE;
 PERFORM 1 FROM memories m WHERE m.id IN (p_a,p_b) ORDER BY m.id FOR SHARE;
END $$;

-- One end of a link: a memory current in the catalog, not in the trash, that the caller reads; else unknown, as for a
-- memory that never was.
CREATE FUNCTION memory_link_end(p_principal uuid,p_memory uuid) RETURNS memories LANGUAGE plpgsql STABLE AS $$
DECLARE m memories;
BEGIN
 SELECT * INTO m FROM memories x WHERE x.id=p_memory;
 IF m.id IS NULL
   OR NOT EXISTS(SELECT 1 FROM resources r WHERE r.id=m.id AND r.provider='memory' AND r.relationship_snapshot IS NULL AND r.status='current')
   OR EXISTS(SELECT 1 FROM memory_trash t WHERE t.memory_id=m.id)
   OR NOT reads_subject(p_principal,m.subject_id) THEN
   RAISE EXCEPTION 'Unknown memory' USING ERRCODE='P0002';
 END IF;
 RETURN m;
END $$;

-- Whether p_principal may remove the made link l (memory.md section 5): its maker, or another login of the same person;
-- or whoever may change its source end (033's memory_may_change), or for a type with no direction (051's link_types(),
-- relates_to) either end, since relates_to is stored smaller id first whichever end was named and either end's author
-- could have made it. With scopes off, any admitted harness or app login.
CREATE FUNCTION memory_link_may_remove(p_principal uuid,l made_links,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT p_scoped THEN principal_has_role(p_principal,ARRAY['harness','app'])
   ELSE coalesce(l.made_by=p_principal,false)
     OR coalesce((SELECT a.person_id FROM principals a WHERE a.id=l.made_by)=(SELECT c.person_id FROM principals c WHERE c.id=p_principal),false)
     OR EXISTS(SELECT 1 FROM memories m WHERE (m.id=l.source_id
         OR m.id=l.target_id AND NOT coalesce((SELECT x.directed FROM link_types() x WHERE x.name=l.type),true))
       AND memory_may_change(p_principal,m.subject_id,m.author_id,true)) END
$$;

-- The external id of the map's star row for the native memory p_memory (Relationships.MemoryStars.StarExternalId).
CREATE FUNCTION memory_star_external_id(p_memory uuid) RETURNS text LANGUAGE sql IMMUTABLE AS $$
 SELECT 'relationship-engine:["memory","'||p_memory::text||'"]'
$$;

-- What a link call answers about its two memories: the source end's scope and the install's mode.
CREATE FUNCTION memory_link_reply(s memories,t memories,p_scoped boolean) RETURNS jsonb LANGUAGE sql STABLE AS $$
 SELECT jsonb_build_object('source',s.id,'target',t.id,'subject',s.subject_id,'scope',s.scope,
   'scopes',CASE WHEN p_scoped THEN 'on' ELSE 'off' END)
$$;

CREATE FUNCTION link_memories(p_principal uuid,p_source uuid,p_target uuid,p_type text,p_confidence double precision,
 p_channel text,p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; s memories; t memories; directed boolean; v_from uuid; v_to uuid; l made_links;
 v_verification text; rank_new integer; rank_old integer; created boolean:=false;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Linking memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_source IS NULL OR p_target IS NULL OR p_source=p_target THEN
   RAISE EXCEPTION 'A link joins two different memories' USING ERRCODE='22023';
 END IF;
 IF p_type='supersedes' THEN
   RAISE EXCEPTION 'A newer memory replacing an earlier one is supersede''s: it ends the earlier one too' USING ERRCODE='22023';
 END IF;
 SELECT x.directed INTO directed FROM link_types() x WHERE x.name=p_type AND x.upstream;
 IF directed IS NULL THEN
   RAISE EXCEPTION 'A link''s type is relates_to, caused_by, decided, about_person, part_of_project, follows or drawn_from' USING ERRCODE='22023';
 END IF;
 IF p_confidence IS NOT NULL AND NOT (p_confidence BETWEEN 0 AND 1) THEN
   RAISE EXCEPTION 'A confidence is a number from 0 to 1' USING ERRCODE='22023';
 END IF;
 PERFORM memory_link_lock(p_source,p_target);
 s:=memory_link_end(p_principal,p_source);
 t:=memory_link_end(p_principal,p_target);
 IF s.subject_id<>t.subject_id THEN
   RAISE EXCEPTION 'Two memories in different scopes never link: both ends of a link are in one scope' USING ERRCODE='22023';
 END IF;
 IF NOT memory_may_change(p_principal,s.subject_id,s.author_id,scoped) THEN
   RAISE EXCEPTION 'Only the memory''s owner, its author or a Family admin may link from it' USING ERRCODE='42501';
 END IF;
 IF s.hold IS NOT NULL OR t.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is linked only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF NOT link_kinds_allow(p_type,s.kind,t.kind) THEN
   RAISE EXCEPTION '% joins two episodic memories only (kind episodic on both)',p_type USING ERRCODE='22023';
 END IF;
 IF NOT directed AND s.id>t.id THEN v_from:=t.id; v_to:=s.id; ELSE v_from:=s.id; v_to:=t.id; END IF;
 v_verification:=CASE WHEN channel='mcp' THEN 'inferred' ELSE 'verified' END;
 SELECT * INTO l FROM made_links x WHERE x.source_id=v_from AND x.target_id=v_to AND x.type=p_type FOR UPDATE;
 IF l.link_id IS NULL THEN
   INSERT INTO made_links(link_id,source_id,target_id,type,verification,evidence,lifecycle,made_by,confidence,source_revision,checked_at)
   VALUES(gen_random_uuid(),v_from,v_to,p_type,v_verification,'','active',p_principal,p_confidence,
     'memory:'||(CASE WHEN v_from=s.id THEN s.revision ELSE t.revision END),now())
   ON CONFLICT(source_id,target_id,type) DO NOTHING
   RETURNING * INTO l;
   IF l.link_id IS NOT NULL THEN
     -- Made now: a dismissal of this type between the two is taken back.
     DELETE FROM memory_link_dismissals d WHERE d.low_id=least(s.id,t.id) AND d.high_id=greatest(s.id,t.id) AND d.type=p_type;
   END IF;
   IF l.link_id IS NULL THEN
     -- Made by a call that ran at the same time: there already, as that call left it.
     SELECT * INTO l FROM made_links x WHERE x.source_id=v_from AND x.target_id=v_to AND x.type=p_type;
     RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('id',l.link_id,'type',l.type,'from',l.source_id,'to',l.target_id,
       'directed',directed,'verification',l.verification,'created',false,'unchanged',true);
   END IF;
   created:=true;
 ELSIF l.lifecycle<>'active' THEN
   -- A link in another lifecycle (an import's stale, retired, revoked or deleted one) that the caller states now: made
   -- anew, by the caller, and a dismissal of its type between the two taken back.
   UPDATE made_links SET lifecycle='active',verification=v_verification,evidence='',made_by=p_principal,created_at=now(),imported=false,
     confidence=p_confidence,source_revision='memory:'||(CASE WHEN v_from=s.id THEN s.revision ELSE t.revision END),checked_at=now()
   WHERE link_id=l.link_id RETURNING * INTO l;
   DELETE FROM memory_link_dismissals d WHERE d.low_id=least(s.id,t.id) AND d.high_id=greatest(s.id,t.id) AND d.type=p_type;
   created:=true;
 ELSE
   -- There already: a person's link confirms an AI client's or a suggestion, an AI client's confirms a suggestion; any
   -- other answers unchanged.
   rank_new:=CASE v_verification WHEN 'verified' THEN 3 WHEN 'inferred' THEN 2 ELSE 1 END;
   rank_old:=CASE l.verification WHEN 'verified' THEN 3 WHEN 'inferred' THEN 2 ELSE 1 END;
   IF rank_new<=rank_old THEN
     RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('id',l.link_id,'type',l.type,'from',l.source_id,'to',l.target_id,
       'directed',directed,'verification',l.verification,'created',false,'unchanged',true);
   END IF;
   UPDATE made_links SET verification=v_verification,checked_at=now() WHERE link_id=l.link_id RETURNING * INTO l;
 END IF;
 RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('id',l.link_id,'type',l.type,'from',l.source_id,'to',l.target_id,
   'directed',directed,'verification',l.verification,'created',created,'unchanged',false);
END $$;

CREATE FUNCTION unlink_memories(p_principal uuid,p_source uuid,p_target uuid,p_type text,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; s memories; t memories; l made_links; removed jsonb:='[]'::jsonb; kept jsonb:='[]'::jsonb;
 dismissed jsonb:='[]'::jsonb; already integer:=0; suggestion record; kept_types text[]:='{}';
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Unlinking memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF p_source IS NULL OR p_target IS NULL OR p_source=p_target THEN
   RAISE EXCEPTION 'A link joins two different memories' USING ERRCODE='22023';
 END IF;
 IF p_type IS NOT NULL AND p_type !~ '^[a-z][a-z0-9_-]{0,63}$' THEN
   RAISE EXCEPTION 'A link''s type is a lower-case word such as relates_to' USING ERRCODE='22023';
 END IF;
 IF p_type='supersedes' THEN
   RAISE EXCEPTION 'A replacement is not unlinked: undo the supersede, or mark the newer memory wrong' USING ERRCODE='22023';
 END IF;
 PERFORM memory_link_lock(p_source,p_target);
 s:=memory_link_end(p_principal,p_source);
 t:=memory_link_end(p_principal,p_target);
 -- The made links between them, either way, but a supersede's: each the caller's to remove, or the whole call is refused.
 FOR l IN SELECT * FROM made_links x WHERE x.lifecycle='active' AND (p_type IS NULL OR x.type=p_type) AND x.type<>'supersedes'
   AND (x.source_id=s.id AND x.target_id=t.id OR x.source_id=t.id AND x.target_id=s.id) ORDER BY x.link_id FOR UPDATE
 LOOP
   IF NOT memory_link_may_remove(p_principal,l,scoped) THEN
     RAISE EXCEPTION 'Only the link''s maker, the owner, the source memory''s author or a Family admin may remove it' USING ERRCODE='42501';
   END IF;
   -- Either way the link goes; an automatic one is kept as a dismissal of its type (below), never as a link.
   DELETE FROM made_links WHERE link_id=l.link_id;
   IF l.verification='suggested' THEN
     kept:=kept||jsonb_build_object('id',l.link_id,'type',l.type);
     kept_types:=kept_types||l.type;
   ELSE
     removed:=removed||jsonb_build_object('id',l.link_id,'type',l.type);
   END IF;
 END LOOP;
 -- The map's own suggestions between their stars (any direction), one per type, dismissed in the same call.
 FOR suggestion IN SELECT l2.type FROM relationships l2
   JOIN resources a ON a.id=l2.source_id AND a.provider='memory' AND a.relationship_snapshot IS NOT NULL
   JOIN resources b ON b.id=l2.target_id AND b.provider='memory' AND b.relationship_snapshot IS NOT NULL
   WHERE l2.engine='cross-source-v1' AND l2.lifecycle='active' AND l2.verification<>'verified' AND (p_type IS NULL OR l2.type=p_type)
     AND (a.external_id=memory_star_external_id(s.id) AND b.external_id=memory_star_external_id(t.id)
       OR a.external_id=memory_star_external_id(t.id) AND b.external_id=memory_star_external_id(s.id))
   GROUP BY l2.type ORDER BY l2.type
 LOOP
   IF NOT (memory_may_change(p_principal,s.subject_id,s.author_id,scoped) OR memory_may_change(p_principal,t.subject_id,t.author_id,scoped)) THEN
     RAISE EXCEPTION 'Only these memories'' owner, their author or a Family admin may dismiss a suggestion between them' USING ERRCODE='42501';
   END IF;
   INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by)
   VALUES(least(s.id,t.id),greatest(s.id,t.id),suggestion.type,p_principal)
   ON CONFLICT DO NOTHING;
   IF FOUND THEN dismissed:=dismissed||to_jsonb(suggestion.type); ELSE already:=already+1; END IF;
 END LOOP;
 -- The automatic made links removed above, each kept as a dismissal of its type (after the map's, which it may share).
 INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by)
 SELECT DISTINCT least(s.id,t.id),greatest(s.id,t.id),x.type,p_principal FROM unnest(kept_types) x(type)
 ON CONFLICT DO NOTHING;
 RETURN memory_link_reply(s,t,scoped)||jsonb_build_object('removed',removed,'kept',kept,'dismissed',dismissed,
   'unchanged',jsonb_array_length(removed)+jsonb_array_length(kept)+jsonb_array_length(dismissed)=0,'already',already);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list of checked tables gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_link_lock(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_link_end(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_link_may_remove(uuid,made_links,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_star_external_id(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_link_reply(memories,memories,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION link_memories(uuid,uuid,uuid,text,double precision,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION unlink_memories(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_link_lock(uuid,uuid),memory_link_end(uuid,uuid),memory_link_may_remove(uuid,made_links,boolean),
 memory_star_external_id(uuid),memory_link_reply(memories,memories,boolean),
 link_memories(uuid,uuid,uuid,text,double precision,text,text),unlink_memories(uuid,uuid,uuid,text,text,text) FROM PUBLIC;
