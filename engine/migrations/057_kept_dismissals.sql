-- Kept dismissals: the owner's judgement on the map's suggestions (SB-P4-05 in docs/dev/second-brain-parity/backlog.md;
-- connection idea 1 in docs/dev/connection-ideas.md; D-194; docs/dev/memory.md sections 5 and 8; ledger row API-22).
-- Discover's Dismiss is saved and honoured on every read and every rebuild, and a person sees the dismissals and takes
-- one back. Both are a person's steps: the app's login or the terminal's, never an AI client's channel (mcp), which keeps
-- unlink (052) as its way to dismiss.
--
-- map_link_dismissals: the map's own suggestions (relationships of its engine, cross-source-v1, active and not verified:
-- a similarity, a copy, a data star's topic link, a source's own automatic link, a plugin's suggestion) that a person
-- dismissed between two stars that are not both native memories', one row a pair of star rows and a type, the smaller id
-- first, since a suggestion has no direction to keep. A suggestion between two native memories' stars is kept where
-- 052's unlink keeps it (memory_link_dismissals), by the two memories, so it outlives a share, which drops a memory's
-- star rows (048). Here the ends are the star rows themselves (rows of resources the rebuild keeps from pass to pass,
-- 009), so a star row a share or a delete forever drops (038, 048) takes its dismissals with it. Like the memory tables,
-- it changes only through its functions (checked_tables()).
--
-- dismiss_suggestion(principal, a, b, type, channel, client): the map's active suggestion of that type between the two
-- star rows a and b, either way, dismissed by a person. Both rows are stars the caller reads as the map serves them
-- (map_star_readable: the subject whole or through a source share of the provider it stands for, and a save_document
-- copy's star only for the narrower reader it names), a native memory's star only while its memory is current, out of the
-- trash, read by the caller and not held (else unknown, as for a suggestion that is not there; the engine also finds the
-- suggestion in the caller's own view of the map first), and the caller may change the record either end stands for
-- (memory.md section 5, as 052's unlink dismisses one): a native memory's star, that memory's owner, author or a Family
-- admin (033's author lock);
-- the demo's, any reader; any other star, its subject's owner or, for the household's, any reader of Family; with scopes
-- off, any admitted login. Between two native memories' stars it is a row of memory_link_dismissals (052), else of
-- map_link_dismissals. Dismissing again answers unchanged. A confirmed (verified) link is no suggestion and is not taken.
--
-- restore_suggestion(principal, kind, low, high, type, channel, client): a dismissal taken back by a person, kind
-- memories (memory_link_dismissals: two memories the caller reads, current, neither in the trash nor held, else unknown)
-- or stars (map_link_dismissals: two star rows the caller reads, as dismiss_suggestion checks them), by whoever may
-- dismiss it; the suggestion is served again from the next read, as the
-- rebuild never stopped finding it. Taking back one that is not there answers unchanged. A dismissal an AI client's
-- unlink made is taken back the same way: only a person's channel takes one back.
--
-- link_memories (052) is replaced with one change: a link made through an AI client's channel (mcp) no longer takes back
-- a dismissal of its type between the two memories. A person's link (the app's, the terminal's) still does, as 052 made
-- it; an AI client's is made beside the dismissal, which keeps holding the map's suggestions (a made link was never one
-- the dismissal hides). So no AI client's step reverses a dismissal.
--
-- Nothing calls these but the app's Memories tool (app_memories, Mcp/AppMemories.cs, made only with memory.tools on, off
-- by default), and tests: with none called, no install holds a row of map_link_dismissals, every read of the map leaves
-- out exactly what it left out before (MapDismissals.Filter), and link_memories answers as before but for that one case.
--
-- Errors, as 033's: 42501 not available to this login, an AI client's channel, or not this caller's to dismiss or take
-- back; P0002 a star or memory the caller does not read, one in the trash, or no such suggestion; 22023 invalid input. A
-- refused call writes nothing. The number is provisional.

CREATE TABLE map_link_dismissals(
 low_id uuid NOT NULL REFERENCES resources ON DELETE CASCADE,
 high_id uuid NOT NULL REFERENCES resources ON DELETE CASCADE,
 type text NOT NULL CHECK(type ~ '^[a-z][a-z0-9_-]{0,63}$'),
 dismissed_by uuid NOT NULL REFERENCES principals,
 dismissed_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(low_id,high_id,type),
 CHECK(low_id<high_id)
);
CREATE INDEX map_link_dismissals_high ON map_link_dismissals(high_id);
CREATE INDEX map_link_dismissals_at ON map_link_dismissals(dismissed_at);
CREATE INDEX memory_link_dismissals_at ON memory_link_dismissals(dismissed_at);
REVOKE ALL ON map_link_dismissals FROM PUBLIC;

-- The list of checked tables in force, with the map's dismissals added (043's way: built from the list, not written out).
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['map_link_dismissals']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- The native memory a star row stands for (its external id, 052's memory_star_external_id), else NULL.
CREATE FUNCTION memory_star_memory(p_star uuid) RETURNS uuid LANGUAGE sql STABLE AS $$
 SELECT (regexp_match(r.external_id,'^relationship-engine:\["memory","([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"\]$'))[1]::uuid
 FROM resources r WHERE r.id=p_star AND r.provider='memory' AND r.relationship_snapshot IS NOT NULL
$$;

-- Whether p_principal reads the star row p_star as the map serves it (RelationshipEngine.Served): its subject read whole,
-- or through a source share of the provider it stands for (the row's own provider, the source provider a catalog
-- record's star names as its account, or the one a data star names as data:<provider>); and a star whose snapshot names
-- a narrower reader (a save_document copy's, 031's copy rule: DiscoveryRecord.Reader) only for a reader of that subject.
CREATE FUNCTION map_star_readable(p_principal uuid,p_star uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM resources r CROSS JOIN LATERAL (SELECT r.relationship_snapshot->'Key'->>'Account' AS account) k
   WHERE r.id=p_star AND r.relationship_snapshot IS NOT NULL
     AND (r.relationship_snapshot->>'Reader' IS NULL OR reads_subject(p_principal,r.relationship_snapshot->>'Reader'))
     AND EXISTS(SELECT 1 FROM readable_subjects(p_principal) x WHERE (x.subject_id IS NULL OR x.subject_id=r.subject_id)
       AND (x.provider IS NULL OR x.provider IN (r.provider,k.account,CASE WHEN k.account LIKE 'data:%' THEN substr(k.account,6) END))
       AND (x.scope IS NULL OR x.scope=r.scope)))
$$;

-- Whether p_principal may judge the suggestions of the star row p_star (memory.md section 5): for a native memory's star,
-- whoever may change the memory (033's memory_may_change, its author lock); for the demo's (a synthetic subject), any
-- reader of it; for any other star, whoever may change a record of its subject that no person wrote: its owner for a
-- person's, any reader of Family for the household's. With scopes off, any admitted harness or app login.
CREATE FUNCTION map_star_may_judge(p_principal uuid,p_star uuid,p_scoped boolean) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT p_scoped THEN principal_has_role(p_principal,ARRAY['harness','app'])
   WHEN memory_star_memory(p_star) IS NOT NULL THEN coalesce((SELECT memory_may_change(p_principal,m.subject_id,m.author_id,true)
     FROM memories m WHERE m.id=memory_star_memory(p_star)),false)
   ELSE coalesce((SELECT CASE WHEN k.kind='synthetic' THEN reads_subject(p_principal,r.subject_id)
       ELSE memory_may_change(p_principal,r.subject_id,NULL,true) END
     FROM resources r JOIN subjects k ON k.id=r.subject_id WHERE r.id=p_star),false) END
$$;

-- The two star rows of a dismissal, locked as a change locks them: a native memory's own rows first (052's
-- memory_link_lock, the order a share takes before it drops the memory's star rows), then the star rows in id order, so
-- neither goes while the dismissal is written. Each must be a star row the caller reads as the map serves it
-- (map_star_readable), and a native memory's star one whose memory is current, out of the trash and read by the caller
-- (052's memory_link_end) and not held (D-160), else unknown. The engine has found the suggestion in the caller's own
-- view of the map before it calls this, so these are the database's own guard, never the only one.
CREATE FUNCTION map_dismissal_lock(p_principal uuid,p_a uuid,p_b uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE x uuid; m memories;
BEGIN
 PERFORM memory_link_lock(coalesce(memory_star_memory(p_a),p_a),coalesce(memory_star_memory(p_b),p_b));
 PERFORM 1 FROM resources r WHERE r.id IN (p_a,p_b) ORDER BY r.id FOR KEY SHARE;
 IF NOT (map_star_readable(p_principal,p_a) AND map_star_readable(p_principal,p_b)) THEN
   RAISE EXCEPTION 'No such suggestion on your map' USING ERRCODE='P0002';
 END IF;
 FOREACH x IN ARRAY ARRAY[memory_star_memory(p_a),memory_star_memory(p_b)] LOOP
   CONTINUE WHEN x IS NULL;
   m:=memory_link_end(p_principal,x);
   IF m.hold IS NOT NULL THEN
     RAISE EXCEPTION 'No such suggestion on your map' USING ERRCODE='P0002';
   END IF;
 END LOOP;
END $$;

CREATE FUNCTION dismiss_suggestion(p_principal uuid,p_a uuid,p_b uuid,p_type text,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; ma uuid; mb uuid; kind text; v_low uuid; v_high uuid; written boolean;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Dismissing a suggestion is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel='mcp' THEN
   RAISE EXCEPTION 'Dismissing a suggestion on the map is a person''s step, in the app or at the terminal; an AI client unlinks two memories instead'
     USING ERRCODE='42501';
 END IF;
 IF p_a IS NULL OR p_b IS NULL OR p_a=p_b THEN
   RAISE EXCEPTION 'A suggestion joins two different stars' USING ERRCODE='22023';
 END IF;
 IF p_type IS NULL OR p_type !~ '^[a-z][a-z0-9_-]{0,63}$' THEN
   RAISE EXCEPTION 'A link''s type is a lower-case word such as similar_to' USING ERRCODE='22023';
 END IF;
 PERFORM map_dismissal_lock(p_principal,p_a,p_b);
 IF NOT EXISTS(SELECT 1 FROM relationships l WHERE l.engine='cross-source-v1' AND l.lifecycle='active' AND l.verification<>'verified'
   AND l.type=p_type AND (l.source_id=p_a AND l.target_id=p_b OR l.source_id=p_b AND l.target_id=p_a)) THEN
   RAISE EXCEPTION 'No such suggestion on your map' USING ERRCODE='P0002';
 END IF;
 IF NOT (map_star_may_judge(p_principal,p_a,scoped) OR map_star_may_judge(p_principal,p_b,scoped)) THEN
   RAISE EXCEPTION 'Only the owner, the author of either end or a Family admin may dismiss this suggestion' USING ERRCODE='42501';
 END IF;
 ma:=memory_star_memory(p_a); mb:=memory_star_memory(p_b);
 IF ma IS NOT NULL AND mb IS NOT NULL AND ma<>mb THEN
   kind:='memories'; v_low:=least(ma,mb); v_high:=greatest(ma,mb);
   INSERT INTO memory_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(v_low,v_high,p_type,p_principal) ON CONFLICT DO NOTHING;
 ELSE
   kind:='stars'; v_low:=least(p_a,p_b); v_high:=greatest(p_a,p_b);
   INSERT INTO map_link_dismissals(low_id,high_id,type,dismissed_by) VALUES(v_low,v_high,p_type,p_principal) ON CONFLICT DO NOTHING;
 END IF;
 written:=FOUND;
 RETURN jsonb_build_object('kind',kind,'low',v_low,'high',v_high,'type',p_type,'dismissed',written,'unchanged',NOT written,
   'scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
END $$;

CREATE FUNCTION restore_suggestion(p_principal uuid,p_kind text,p_low uuid,p_high uuid,p_type text,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; s memories; t memories; taken boolean;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Taking back a dismissal is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel='mcp' THEN
   RAISE EXCEPTION 'Taking back a dismissal is a person''s step, in the app or at the terminal' USING ERRCODE='42501';
 END IF;
 IF p_kind IS NULL OR p_kind NOT IN ('memories','stars') OR p_low IS NULL OR p_high IS NULL OR p_low>=p_high THEN
   RAISE EXCEPTION 'A dismissal names memories or stars and two ids, the smaller first' USING ERRCODE='22023';
 END IF;
 IF p_type IS NULL OR p_type !~ '^[a-z][a-z0-9_-]{0,63}$' THEN
   RAISE EXCEPTION 'A link''s type is a lower-case word such as similar_to' USING ERRCODE='22023';
 END IF;
 IF p_kind='memories' THEN
   PERFORM memory_link_lock(p_low,p_high);
   s:=memory_link_end(p_principal,p_low);
   t:=memory_link_end(p_principal,p_high);
   -- A held memory's star is on no map (D-160), so no suggestion of it is anyone's to take back, as map_dismissal_lock
   -- refuses one for the stars.
   IF s.hold IS NOT NULL OR t.hold IS NOT NULL THEN
     RAISE EXCEPTION 'No such dismissal on your map' USING ERRCODE='P0002';
   END IF;
   IF NOT (memory_may_change(p_principal,s.subject_id,s.author_id,scoped) OR memory_may_change(p_principal,t.subject_id,t.author_id,scoped)) THEN
     RAISE EXCEPTION 'Only these memories'' owner, their author or a Family admin may take back this dismissal' USING ERRCODE='42501';
   END IF;
   DELETE FROM memory_link_dismissals d WHERE d.low_id=p_low AND d.high_id=p_high AND d.type=p_type;
 ELSE
   PERFORM map_dismissal_lock(p_principal,p_low,p_high);
   IF NOT (map_star_may_judge(p_principal,p_low,scoped) OR map_star_may_judge(p_principal,p_high,scoped)) THEN
     RAISE EXCEPTION 'Only the owner, the author of either end or a Family admin may take back this dismissal' USING ERRCODE='42501';
   END IF;
   DELETE FROM map_link_dismissals d WHERE d.low_id=p_low AND d.high_id=p_high AND d.type=p_type;
 END IF;
 taken:=FOUND;
 RETURN jsonb_build_object('kind',p_kind,'low',p_low,'high',p_high,'type',p_type,'restored',taken,'unchanged',NOT taken,
   'scopes',CASE WHEN scoped THEN 'on' ELSE 'off' END);
END $$;

-- 052's link_memories, but that a link made through an AI client's channel (mcp) takes back no dismissal: only a
-- person's channel reverses one. Every other line as 052 wrote it.
CREATE OR REPLACE FUNCTION link_memories(p_principal uuid,p_source uuid,p_target uuid,p_type text,p_confidence double precision,
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
   IF l.link_id IS NOT NULL AND channel<>'mcp' THEN
     -- Made now by a person: a dismissal of this type between the two is taken back. An AI client's leaves it.
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
   -- anew, by the caller, and, by a person, a dismissal of its type between the two taken back.
   UPDATE made_links SET lifecycle='active',verification=v_verification,evidence='',made_by=p_principal,created_at=now(),imported=false,
     confidence=p_confidence,source_revision='memory:'||(CASE WHEN v_from=s.id THEN s.revision ELSE t.revision END),checked_at=now()
   WHERE link_id=l.link_id RETURNING * INTO l;
   IF channel<>'mcp' THEN
     DELETE FROM memory_link_dismissals d WHERE d.low_id=least(s.id,t.id) AND d.high_id=greatest(s.id,t.id) AND d.type=p_type;
   END IF;
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

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list of checked tables and link_memories get theirs again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_star_memory(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION map_star_readable(uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION map_star_may_judge(uuid,uuid,boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION map_dismissal_lock(uuid,uuid,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION dismiss_suggestion(uuid,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION restore_suggestion(uuid,text,uuid,uuid,text,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION link_memories(uuid,uuid,uuid,text,double precision,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),memory_star_memory(uuid),map_star_readable(uuid,uuid),map_star_may_judge(uuid,uuid,boolean),map_dismissal_lock(uuid,uuid,uuid),
 dismiss_suggestion(uuid,uuid,uuid,text,text,text),restore_suggestion(uuid,text,uuid,uuid,text,text,text),
 link_memories(uuid,uuid,uuid,text,double precision,text,text) FROM PUBLIC;
