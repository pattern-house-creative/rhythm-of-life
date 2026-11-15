-- One resolve for every review queue (SB-P5-15 in docs/dev/second-brain-parity/backlog.md; story 12; ledger row MCP-04;
-- docs/dev/memory.md sections 5 and 7.1), adapted from Second Brain's resolve tool (src/memory/actions.ts at the pinned commit,
-- MIT; NOTICE). Each queue already has its step, its author lock and its audit row, and each step on its own answers unchanged
-- only for the same word said twice: a loop marked done can still be called not_a_task, a decision right can still be called
-- wrong, another unknown asks again, a still true a day later moves the clock again, a snooze to another date moves it. Those are
-- right for a person correcting something, so the steps stay as they are. These functions add what resolving needs, in the
-- transaction that takes the step's lock: the item is taken off its queue once. So a second resolve of the same item, in the same
-- word or another, whoever calls it and however close together, finds it no longer waiting, writes nothing and says so, and each
-- queue's count drops by exactly one.
--
-- Waiting (memory_resolve_waiting, the one definition) is each queue's own predicate, never a copy of it: stale, 053's
-- memory_stale_reason; a loop, 058's memory_open_loop; a decision, 069's memory_decision_listed for open, due or upcoming; a
-- date, 069's memory_due; a copy, 063's memory_duplicate_listed; a standing instruction, 065's memory_standing_rows, so one whose
-- time has ended, on no list, waits no more. A snooze asks one thing more: no snooze of it in force (memory_snooze_in_force), since
-- a snoozed date is still on the due list. A clear of a snoozed date does take it off: a snooze puts a date off, a clear ends it,
-- and the count still drops once. Counted (memory_resolve_counted) says whether the item is in what the brief counts afterwards,
-- which only a snooze to within 48 hours leaves it in, by the one window rule, and a copy with a fourth flag standing (068 shows
-- three at a time). A date past the 48 hours was in no count of the brief, so a snooze into them is the one resolve that raises a
-- count: the answer's window_before is the window the date was in, and the engine says which of the two it was.
--
-- Who may: exactly the step's. Each function takes the step's locks in the step's order (the scopes switch, then the row), so
-- what it refuses it refuses first and alike: an unknown or unread id, one the caller may not change, a stale expected revision,
-- a held memory and one marked wrong. Past that it calls the step, which does its own checks again. No person-only step is here:
-- a release (036) and a settling (068) stay their own functions, each of which tells a person from an AI client by the channel
-- (memory_channel, 044), and the engine's ResolveItem answers an AI client's channel needs_person before it reads anything.
--
-- Insights and insight candidates are not memories, so each has a function of its own. Each, for an item no longer waiting,
-- asks its step for what the item already is, so every refusal is the step's own and the step answers unchanged: review_insight
-- for the review it has (reopen when it has none, so a person's not useful or handled stays as it was), and 066's
-- resolve_insight_candidate for the decision it has, where the step alone would refuse the other word.
--
-- No table, no audit event and no earlier function changes. With memory.tools off, the default, nothing calls these. The number
-- is provisional, as 060 on are.
--
-- Errors, as the steps': 42501 not available to this login, or not this login's to change; P0002 an unknown item, also one the
-- caller does not read; 40001 a stale expected revision; 22023 invalid input, HINT held or retracted.

-- Whether a snooze of p_memory is in force: its newest change of date was a snooze, to the date it has now, still to come. An
-- undo of the snooze brought another date back, so that one is not; when the moment comes it is due again.
CREATE FUNCTION memory_snooze_in_force(p_memory uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce(m.due_at>now() AND (SELECT e.detail->>'action'='snooze' AND (e.detail->'to'->>'at')::timestamptz=m.due_at
   FROM memory_events e WHERE e.memory_id=m.id AND e.event='due_changed' ORDER BY e.id DESC LIMIT 1),false)
 FROM memories m WHERE m.id=p_memory
$$;

-- Whether p_memory is waiting in the queue p_action resolves: the one definition, each queue's own predicate.
CREATE FUNCTION memory_resolve_waiting(p_memory uuid,p_action text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE
   WHEN p_action='still_true' THEN memory_stale_reason(p_memory) IS NOT NULL
   WHEN p_action IN ('done','received','not_a_task') THEN memory_open_loop(p_memory) IS NOT NULL
   WHEN p_action='outcome' THEN memory_decision_listed(p_memory,'open') OR memory_decision_listed(p_memory,'due')
     OR memory_decision_listed(p_memory,'upcoming')
   WHEN p_action='clear_date' THEN memory_due(p_memory) IS NOT NULL
   WHEN p_action='snooze' THEN memory_due(p_memory) IS NOT NULL AND NOT memory_snooze_in_force(p_memory)
   WHEN p_action='stop_standing' THEN EXISTS(SELECT 1 FROM memory_standing_rows() s WHERE s.id=p_memory)
   WHEN p_action='keep_copy' THEN memory_duplicate_listed(p_memory)
   ELSE false END
$$;

-- Whether the brief still counts p_memory in the section p_action resolves.
CREATE FUNCTION memory_resolve_counted(p_memory uuid,p_action text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_action='snooze' THEN coalesce(memory_due_window(memory_due(p_memory)) IN ('overdue','upcoming'),false)
   ELSE memory_resolve_waiting(p_memory,p_action) END
$$;

-- resolve_memory_item: p_action on memory p_memory, once. still_true, done, received, not_a_task, outcome (p_result), snooze
-- (p_until), clear_date, stop_standing or keep_copy. Waiting: the step's answer, with action, waiting and counted beside it, and
-- for a snooze window_before, the window its date was in. Not waiting: the memory's place, unchanged, and nothing written.
CREATE FUNCTION resolve_memory_item(p_principal uuid,p_memory uuid,p_action text,p_result text,p_until timestamptz,p_expected integer,p_channel text,
 p_client text DEFAULT NULL) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; m memories; answer jsonb; waiting boolean; window_before text;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 PERFORM memory_channel(p_principal,p_channel,p_client);
 IF p_action IS NULL OR p_action NOT IN ('still_true','done','received','not_a_task','outcome','snooze','clear_date','stop_standing','keep_copy') THEN
   RAISE EXCEPTION 'action is still_true, done, received, not_a_task, outcome, snooze, clear_date, stop_standing or keep_copy' USING ERRCODE='22023';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NOT NULL THEN
   RAISE EXCEPTION 'A memory held for review is resolved only once a person releases it' USING ERRCODE='22023',HINT='held';
 END IF;
 IF m.status='deprecated' THEN
   RAISE EXCEPTION 'A memory marked wrong (deprecated) is in no review: set another status first if it is right after all'
     USING ERRCODE='22023',HINT='retracted';
 END IF;
 waiting:=memory_resolve_waiting(m.id,p_action);
 IF NOT waiting THEN
   answer:=memory_reply(m,scoped)||jsonb_build_object('changed','[]'::jsonb,'unchanged',true);
 ELSE
   IF p_action='snooze' THEN window_before:=memory_due_window(memory_due(m.id)); END IF;
   answer:=CASE
     WHEN p_action='still_true' THEN confirm_memory(p_principal,m.id,p_expected,p_channel,p_client)
     WHEN p_action IN ('done','received','not_a_task') THEN resolve_memory_loop(p_principal,m.id,p_action,p_expected,p_channel,p_client)
     WHEN p_action='outcome' THEN record_memory_outcome(p_principal,m.id,p_result,p_expected,p_channel,p_client)
     WHEN p_action IN ('snooze','clear_date') THEN resolve_memory_due(p_principal,m.id,p_action,p_until,p_expected,p_channel,p_client)
     WHEN p_action='stop_standing' THEN stop_memory_standing(p_principal,m.id,p_expected,p_channel,p_client)
     ELSE clear_memory_duplicates(p_principal,m.id,p_expected,p_channel,p_client) END;
 END IF;
 RETURN answer||jsonb_build_object('action',p_action,'waiting',waiting,'counted',memory_resolve_counted(m.id,p_action),'window_before',window_before);
END $$;

-- resolve_insight_review: p_action confirm or dismiss on insight p_id, once. Waiting: no review yet, and not closed by a later
-- insight or by a person's not useful or handled. A snoozed insight waits: its snooze ends by the clock. So does an older one:
-- it is no longer counted as pending, and a person may still review it. Not waiting: 066's
-- review_insight is asked for what it is, so it refuses what it refuses and writes nothing. Answers review_insight's answer
-- with waiting beside it.
CREATE FUNCTION resolve_insight_review(p_principal uuid,p_id uuid,p_action text,p_note text,p_channel text,p_client text,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE i insights; waiting boolean; asked text;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Reviewing insights is not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 PERFORM memory_channel(p_principal,p_channel,p_client);
 IF p_action IS NULL OR p_action NOT IN ('confirm','dismiss') THEN
   RAISE EXCEPTION 'action is confirm or dismiss' USING ERRCODE='22023';
 END IF;
 SELECT * INTO i FROM insights WHERE id=p_id FOR UPDATE;
 waiting:=FOUND AND i.review='proposed' AND insight_stream_view(i.status,i.review,i.feedback,insight_replaced(i.id),insight_stream_at(i.status,i.feedback,i.created_at)) IN ('proposed','older','snoozed');
 asked:=CASE WHEN waiting THEN p_action WHEN i.review='confirmed' THEN 'confirm' WHEN i.review='dismissed' THEN 'dismiss' ELSE 'reopen' END;
 RETURN review_insight(p_principal,p_id,asked,CASE WHEN waiting THEN p_note END,p_channel,p_client,p_stars)||jsonb_build_object('waiting',waiting);
END $$;

-- resolve_insight_candidate_once: p_action use or reject on candidate p_candidate, once. Decided already: 066's
-- resolve_insight_candidate is asked for the decision it has, so its refusals are its own and it writes nothing. Answers its
-- answer with waiting beside it.
CREATE FUNCTION resolve_insight_candidate_once(p_principal uuid,p_candidate uuid,p_action text,p_note text,p_insight uuid,p_channel text,p_client text,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE c memory_insight_candidates; waiting boolean;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Insight candidates are not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM lock_memory_scopes();
 PERFORM memory_channel(p_principal,p_channel,p_client);
 IF p_action IS NULL OR p_action NOT IN ('use','reject') THEN
   RAISE EXCEPTION 'action is use or reject' USING ERRCODE='22023';
 END IF;
 SELECT * INTO c FROM memory_insight_candidates WHERE id=p_candidate FOR UPDATE;
 waiting:=NOT FOUND OR c.status='pending';
 IF waiting THEN
   RETURN resolve_insight_candidate(p_principal,p_candidate,p_action,p_channel,p_client,p_note,p_insight,p_stars)||jsonb_build_object('waiting',true);
 END IF;
 RETURN resolve_insight_candidate(p_principal,p_candidate,CASE c.status WHEN 'used' THEN 'use' ELSE 'reject' END,p_channel,p_client,NULL,NULL,p_stars)
   ||jsonb_build_object('waiting',false);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_snooze_in_force(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_resolve_waiting(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_resolve_counted(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_memory_item(uuid,uuid,text,text,timestamptz,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_insight_review(uuid,uuid,text,text,text,text,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_insight_candidate_once(uuid,uuid,text,text,uuid,text,text,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_snooze_in_force(uuid),memory_resolve_waiting(uuid,text),memory_resolve_counted(uuid,text),
 resolve_memory_item(uuid,uuid,text,text,timestamptz,integer,text,text),resolve_insight_review(uuid,uuid,text,text,text,text,text[]),
 resolve_insight_candidate_once(uuid,uuid,text,text,uuid,text,text,text[]) FROM PUBLIC;
