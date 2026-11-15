-- One insight stream (SB-P5-06 in docs/dev/second-brain-parity/backlog.md; story 11; ledger rows INT-04, INT-05, JOB-11,
-- RHY-16; decisions D-018 and D-031; docs/dev/memory.md sections 5, 7.1 and 8): the daily insight, as it was, with what
-- it was missing to be the one stream: what an AI proposed kept apart from what a person confirmed, a snooze that ends,
-- the feedback an insight has had kept instead of replaced, and a candidate's decision tied to the insight written from
-- it, or to why it was declined. No job is added: the insight is still written by the outside AI client's daily prompt
-- (harness/chatgpt/daily-insight.md), and a snooze ends by the clock, at every read, never by a write (D-034).
--
-- The review (insights.review): proposed, every insight as it is saved and every insight there is today; confirmed, a
-- person said it holds; dismissed, a person said it does not. Nothing but review_insight changes it: save_insight never
-- reads it, whatever a draft names, feedback never moves it (interesting and explore say more of this, not that it is
-- true), and an insight that replaces a confirmed one is proposed itself. A confirmed insight is replaced only by who
-- could review it, so supersedes is no way round the rule, and the history keeps that it was (this file's save_insight,
-- the one earlier function it changes). Who may (memory.md section 5, stricter than
-- upstream, D-031): with scopes on a private insight's owner; a Family insight's author person or an admin; no person
-- wrote it (the daily insight with an unbound token), an admin. With scopes off any admitted login, today's model. And
-- never, over MCP, the login that saved it: an AI client does not review what it proposed. A person does, through
-- another client's login (the database would take the terminal's and the app's too; neither has a way to call it yet).
--
-- The stream (insight_stream_view, the one definition every count and list reads): each insight the reader reads is in
-- exactly one view. closed (replaced by a later one, which a later feedback does not undo: the link says so, not the
-- status 027's feedback overwrites; or dismissed by a review); snoozed (a snooze in force); confirmed (its review says
-- so, and only then); closed again for what a person answered without confirming it (not useful, handled); older (it became
-- one to show more than insight_pending_days() ago and nobody has reviewed it); else proposed. So a reopened insight someone
-- marked not useful or handled stays closed until new feedback, and review_insight answers its view to say so. Older is why a
-- count of what waits stays bounded: the daily client adds about one a day and an install with one client can't review its
-- own (below), so with every insight there is proposed the first turn of memory.tools on would count the whole history and the
-- brief's line would only grow. An older insight is still listed and may still be reviewed; it is only no longer pending. A
-- daily client still keeps about a week's waiting until a person reviews them (SB-P6-01). A snooze ends when its time comes: the insight is back where it was, first in its list by
-- when it came back (insight_stream_at, as the app's own reading does), though its stored status stays snoozed until the
-- next feedback, which the app and list_insights read as they always have.
--
-- The history (insight_events): each feedback (value, until, note), recorded per principal, and each review, kept with
-- who, through which channel and client, and when, in the insight's subject and readable only by who reads the insight.
-- insights.feedback stays the latest, as every reader receives it. A feedback written before the history began, or while
-- memory.tools was off, is carried in ahead of the next event, with no principal: who gave it was never recorded, and
-- read_insight_history serves one not carried yet the same way. So events are in the order they happened by id alone,
-- whatever the clock did, and one not carried yet is the newest there is.
--
-- The day (read_insight_day): what the reader's own day came to, in the window the engine passes (the install's zone):
-- an insight it reads saved in it, or the one the newest run it reads says it saved; else that run (quiet or blocked);
-- else none; with how many candidates its scopes declined. So a day with no insight says so.
--
-- A candidate's decision (resolve_insight_candidate's longer form, beside 064's, which stays as it is for the release
-- before): reject may say why (note); use may name the insight written from the pair (insight_id), one the caller reads
-- that cites both memories (kind memory, locator the memory's id), no wider than the pair: an insight from a private
-- pair is of that scope (D-060). Both are kept on the candidate; a repeat of the decision changes nothing, and names the
-- insight only to a reader of that insight's subject, as list_insight_candidates does.
--
-- With memory.tools off, the default, nothing calls these: the tools that do (list_insight_stream, review_insight,
-- insight_history, and the longer form behind resolve_insight_candidate) are not made, and the app's feedback calls the
-- function it always called, so both new columns' defaults are all an insight has and insight_events stays empty. So on
-- an install that has never had it on, save_insight's new branch, which only a confirmed insight enters, is never reached;
-- once a person has confirmed one, it holds whatever the setting is later. insight_json is not changed, so every insight
-- answer is as it was.
--
-- Errors, as 033's and 064's: 42501 not available to this login, or not this login's to review; P0002 an unknown insight,
-- also one the caller does not read; 22023 invalid input. The number is provisional.

ALTER TABLE insights ADD COLUMN review text NOT NULL DEFAULT 'proposed' CHECK(review IN ('proposed','confirmed','dismissed')),
  ADD COLUMN reviewed_at timestamptz,
  ADD COLUMN reviewed_by uuid REFERENCES principals,
  ADD CONSTRAINT insights_review_who CHECK((review='proposed')=(reviewed_at IS NULL) AND (reviewed_at IS NULL)=(reviewed_by IS NULL));

-- What became of a decided candidate: why it was declined, in the client's words, and the insight written from it.
ALTER TABLE memory_insight_candidates ADD COLUMN note text CHECK(note IS NULL OR length(note) BETWEEN 1 AND 500),
  ADD COLUMN insight_id uuid REFERENCES insights ON DELETE SET NULL,
  ADD CONSTRAINT memory_insight_candidates_written CHECK(insight_id IS NULL OR status='used');
CREATE INDEX memory_insight_candidates_insight ON memory_insight_candidates(insight_id) WHERE insight_id IS NOT NULL;

-- Each feedback and each review of an insight, newest last by id.
CREATE TABLE insight_events(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 insight_id uuid NOT NULL REFERENCES insights ON DELETE CASCADE,
 subject_id text NOT NULL REFERENCES subjects,
 event text NOT NULL CHECK(event IN ('feedback','confirmed','dismissed','reopened','replaced')),
 -- A feedback's value and how long it snoozed; a review or a replacement has neither.
 value text CHECK(value IN ('interesting','explore','not_useful','later','handled')),
 until_at timestamptz,
 -- Words given with it: a person's with a feedback, whoever reviewed's with a review.
 note text CHECK(note IS NULL OR length(note) BETWEEN 1 AND 500),
 -- Null only for a feedback carried in from before the history: who gave it was never recorded.
 principal_id uuid REFERENCES principals,
 channel text CHECK(channel IN ('app','mcp','cli')),
 client text CHECK(client IS NULL OR length(client) BETWEEN 1 AND 80),
 at timestamptz NOT NULL DEFAULT now(),
 CHECK((event='feedback')=(value IS NOT NULL)),
 CHECK(until_at IS NULL OR value='later'),
 CHECK((principal_id IS NULL)=(channel IS NULL)),
 CHECK(principal_id IS NOT NULL OR event='feedback')
);
CREATE INDEX insight_events_insight ON insight_events(insight_id,id);
-- What insight_replaced reads, once per insight of a page.
CREATE INDEX insights_supersedes ON insights(supersedes) WHERE supersedes IS NOT NULL;
REVOKE ALL ON insight_events FROM PUBLIC;

-- The list of checked tables in force, with the history added (043's way): written only through these functions.
DO $$
DECLARE tables text:=(SELECT array_agg(x.t::text ORDER BY x.o)::text
  FROM unnest(checked_tables()||ARRAY['insight_events']::regclass[]) WITH ORDINALITY x(t,o));
BEGIN
 EXECUTE format('CREATE OR REPLACE FUNCTION checked_tables() RETURNS regclass[] LANGUAGE sql STABLE AS %L',
   format('SELECT %L::regclass[]',tables));
END $$;

-- Whether a snooze holds an insight back now. One that names no end has none to wait for.
CREATE FUNCTION insight_snooze_in_force(p_status text,p_feedback jsonb) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT p_status='snoozed' AND coalesce((p_feedback->>'until')::timestamptz>now(),false)
$$;

-- Whether a later insight replaced this one: the link, since 027's feedback overwrites the status that said so.
CREATE FUNCTION insight_replaced(p_id uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM insights n WHERE n.supersedes=p_id)
$$;

-- How long an insight nobody has reviewed counts as waiting on a person: a week of a daily client's.
CREATE FUNCTION insight_pending_days() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 7 $$;

-- Which view of the stream an insight is in: the one definition every count and list reads. Confirmed only by its review.
-- The status still counts: one whose replacement was deleted has no link left. p_shown_at: when it became one to show
-- (insight_stream_at); with none it is taken as new.
CREATE FUNCTION insight_stream_view(p_status text,p_review text,p_feedback jsonb,p_replaced boolean,p_shown_at timestamptz DEFAULT NULL) RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_replaced OR p_status='superseded' OR p_review='dismissed' THEN 'closed'
   WHEN insight_snooze_in_force(p_status,p_feedback) THEN 'snoozed'
   WHEN p_review='confirmed' THEN 'confirmed'
   WHEN p_status IN ('dismissed','handled') THEN 'closed'
   WHEN p_shown_at<=now()-make_interval(days=>insight_pending_days()) THEN 'older'
   ELSE 'proposed' END
$$;

-- When an insight became one to show: when it was made, or for one whose snooze has ended, when it ended.
CREATE FUNCTION insight_stream_at(p_status text,p_feedback jsonb,p_created timestamptz) RETURNS timestamptz LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN p_status='snoozed' AND NOT insight_snooze_in_force(p_status,p_feedback)
   THEN coalesce((p_feedback->>'until')::timestamptz,p_created) ELSE p_created END
$$;

-- Whether a login may review an insight of this subject that p_saved_by saved (memory.md section 5, D-031). With scopes
-- off, any admitted login. With them on, only a reader of its subject: a private insight's owner; a Family insight, the
-- person who saved it or an admin; a Family insight no person saved, an admin. An admin never reviews a private one.
CREATE FUNCTION insight_may_review(p_principal uuid,p_subject text,p_saved_by uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN NOT scopes_on() THEN principal_has_role(p_principal,ARRAY['harness','app'])
   WHEN NOT reads_subject(p_principal,p_subject) THEN false
   WHEN p_subject<>'household' THEN coalesce((SELECT person_id FROM principals WHERE id=p_principal)=p_subject,false)
   ELSE coalesce((SELECT a.person_id=c.person_id OR EXISTS(SELECT 1 FROM memberships m WHERE m.person_id=c.person_id AND m.role='admin')
     FROM principals a,principals c WHERE a.id=p_saved_by AND c.id=p_principal),false) END
$$;

-- A feedback on the insight that the history does not hold yet goes in first, naming no one. Runs inside the functions
-- below, under their lock on the insight: no definer, so the engine's login cannot call it to write.
CREATE FUNCTION insight_carry_feedback(p_insight insights) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 IF p_insight.feedback IS NOT NULL AND NOT EXISTS(SELECT 1 FROM insight_events e WHERE e.insight_id=p_insight.id AND e.event='feedback'
     AND e.at=(p_insight.feedback->>'at')::timestamptz) THEN
   INSERT INTO insight_events(insight_id,subject_id,event,value,until_at,note,at)
   VALUES(p_insight.id,p_insight.subject_id,'feedback',p_insight.feedback->>'value',(p_insight.feedback->>'until')::timestamptz,p_insight.feedback->>'note',
     (p_insight.feedback->>'at')::timestamptz);
 END IF;
END $$;

-- review_insight: p_action confirm, dismiss or reopen (back to proposed), with optional words. Answers the insight's id,
-- its review now, the view that puts it in, when it was reviewed, and whether this call changed nothing (the same review again).
CREATE FUNCTION review_insight(p_principal uuid,p_id uuid,p_action text,p_note text,p_channel text,p_client text,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; i insights; wanted text; said text:=btrim(p_note,E' \t\r\n'); moment timestamptz:=now();
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Reviewing insights is not available to this login' USING ERRCODE='42501';
 END IF;
 -- The scopes switch held still while the read rule is applied, as every caller-checked writer since 033 does.
 PERFORM lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 wanted:=CASE p_action WHEN 'confirm' THEN 'confirmed' WHEN 'dismiss' THEN 'dismissed' WHEN 'reopen' THEN 'proposed' END;
 IF wanted IS NULL THEN RAISE EXCEPTION 'An insight is confirmed, dismissed or reopened' USING ERRCODE='22023'; END IF;
 IF length(said) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'note is 1 to 500 characters once trimmed' USING ERRCODE='22023'; END IF;
 SELECT * INTO i FROM insights WHERE id=p_id FOR UPDATE;
 IF NOT FOUND OR NOT insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars) THEN
   RAISE EXCEPTION 'Unknown insight' USING ERRCODE='P0002';
 END IF;
 IF i.status='superseded' OR insight_replaced(i.id) THEN
   RAISE EXCEPTION 'A later insight replaced this one: review that one' USING ERRCODE='22023';
 END IF;
 IF NOT insight_may_review(p_principal,i.subject_id,i.saved_by) THEN
   RAISE EXCEPTION 'Only its owner, the person who saved it or an admin reviews this insight' USING ERRCODE='42501';
 END IF;
 IF channel='mcp' AND i.saved_by=p_principal THEN
   RAISE EXCEPTION 'This login saved it, and a client does not review what it proposed: a person reviews it through another client' USING ERRCODE='42501';
 END IF;
 IF i.review=wanted THEN
   RETURN jsonb_build_object('id',i.id,'review',i.review,'view',insight_stream_view(i.status,i.review,i.feedback,false,insight_stream_at(i.status,i.feedback,i.created_at)),'reviewed_at',i.reviewed_at,'unchanged',true);
 END IF;
 PERFORM insight_carry_feedback(i);
 UPDATE insights SET review=wanted,reviewed_at=CASE WHEN wanted='proposed' THEN NULL ELSE moment END,
   reviewed_by=CASE WHEN wanted='proposed' THEN NULL ELSE p_principal END,updated_at=moment
 WHERE id=i.id;
 INSERT INTO insight_events(insight_id,subject_id,event,note,principal_id,channel,client,at)
 VALUES(i.id,i.subject_id,CASE wanted WHEN 'proposed' THEN 'reopened' ELSE wanted END,said,p_principal,channel,p_client,moment);
 RETURN jsonb_build_object('id',i.id,'review',wanted,'view',insight_stream_view(i.status,wanted,i.feedback,false,insight_stream_at(i.status,i.feedback,i.created_at)),
   'reviewed_at',CASE WHEN wanted='proposed' THEN NULL ELSE moment END,'unchanged',false);
END $$;

-- As 027's insight_feedback, and the same answer, keeping the feedback in the history (only with memory.tools on: the
-- engine calls this one then). A feedback there from before is carried in first, so nothing replaces it and is lost.
CREATE FUNCTION insight_feedback_kept(p_principal uuid,p_id uuid,p_value text,p_until timestamptz,p_note text,p_stars text[],p_channel text,p_client text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE channel text; answered jsonb; earlier insights;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['app'],'Insight feedback');
 channel:=memory_channel(p_principal,p_channel,p_client);
 SELECT * INTO earlier FROM insights WHERE id=p_id FOR UPDATE;
 answered:=insight_feedback(p_principal,p_id,p_value,p_until,p_note,p_stars);
 IF answered IS NULL THEN RETURN NULL; END IF;
 PERFORM insight_carry_feedback(earlier);
 INSERT INTO insight_events(insight_id,subject_id,event,value,until_at,note,principal_id,channel,client,at)
 VALUES(p_id,earlier.subject_id,'feedback',answered->'feedback'->>'value',(answered->'feedback'->>'until')::timestamptz,answered->'feedback'->>'note',
   p_principal,channel,p_client,(answered->'feedback'->>'at')::timestamptz);
 RETURN answered;
END $$;

-- As 027's save_insight, the one earlier function this file changes: supersedes on a confirmed insight closes a person's
-- word, so it takes what review_insight takes: who may review it, and never over MCP the login that saved it. Its history
-- keeps who did. Refused, nothing is saved: the refusal says to save it without supersedes. Every other save answers as
-- 027's, and only memory.tools on ever confirms an insight. The scopes switch held still first, in review_insight's order.
CREATE OR REPLACE FUNCTION save_insight(p_principal uuid,p_insight jsonb,p_stars text[] DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE saved uuid:=gen_random_uuid(); replaced uuid:=(p_insight->>'supersedes')::uuid; earlier insights; landed text; cited jsonb;
  checked text[]; scoped boolean:=scopes_on(); channel text;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness'],'Saving insights');
 -- A saver cites only stars it reads now (the engine drops the others first).
 IF NOT stars_readable((SELECT array_agg(n->>'graphId') FROM jsonb_array_elements(
     CASE WHEN jsonb_typeof(p_insight->'stars')='array' THEN p_insight->'stars' ELSE '[]'::jsonb END) n),p_stars) THEN
   RAISE EXCEPTION 'An insight cites stars its saver reads' USING ERRCODE='22023';
 END IF;
 -- Kept only with scopes on and as the engine passes it; without it every locator is checked at each read.
 IF scoped AND jsonb_typeof(p_insight->'checked')='array' THEN
   checked:=coalesce((SELECT array_agg(c) FROM jsonb_array_elements_text(p_insight->'checked') c),'{}');
 END IF;
 IF replaced IS NOT NULL THEN
   PERFORM lock_memory_scopes();
   SELECT * INTO earlier FROM insights WHERE id=replaced FOR UPDATE;
   IF NOT FOUND OR NOT insight_readable(p_principal,earlier.subject_id,earlier.stars,earlier.sources,earlier.sources_checked,p_stars) THEN
     RAISE EXCEPTION 'Unknown superseded insight' USING ERRCODE='22023';
   END IF;
   IF earlier.review='confirmed' THEN
     channel:=CASE WHEN (SELECT p.terminal FROM principals p WHERE p.id=p_principal) THEN 'cli' ELSE 'mcp' END;
     IF NOT insight_may_review(p_principal,earlier.subject_id,earlier.saved_by) OR (channel='mcp' AND earlier.saved_by=p_principal) THEN
       RAISE EXCEPTION 'A person confirmed this insight, and this login may not replace it: save it without supersedes' USING ERRCODE='42501';
     END IF;
     PERFORM insight_carry_feedback(earlier);
     INSERT INTO insight_events(insight_id,subject_id,event,principal_id,channel) VALUES(replaced,earlier.subject_id,'replaced',p_principal,channel);
   END IF;
   UPDATE insights SET status='superseded',updated_at=now() WHERE id=replaced;
 END IF;
 cited:=CASE WHEN jsonb_typeof(p_insight->'cited')='array' THEN p_insight->'cited' ELSE '[]'::jsonb END;
 IF coalesce(jsonb_array_length(CASE WHEN jsonb_typeof(p_insight->'stars')='array' THEN p_insight->'stars' END),0)=0 THEN
   cited:=cited||jsonb_build_array(jsonb_build_object('subject',coalesce((SELECT person_id FROM principals WHERE id=p_principal),'household')));
 END IF;
 landed:=derived_subject(p_principal,cited,earlier.subject_id);
 -- Superseding keeps the earlier insight's subject unless its own author's person takes it private (memory.md section
 -- 5), as a dashboard's next version does: no one else marks a Family insight superseded by a private one. With scopes
 -- off every new insight is the household's, superseding a private one (saved while scopes were on) included.
 IF scoped AND replaced IS NOT NULL AND landed<>earlier.subject_id AND NOT coalesce((SELECT a.person_id=landed AND c.person_id=landed
     FROM principals a,principals c WHERE a.id=earlier.saved_by AND c.id=p_principal),false) THEN
   RAISE EXCEPTION 'This insight would replace a Family insight with a private one, which only the person who saved it does; save it without supersedes' USING ERRCODE='22023';
 END IF;
 INSERT INTO insights(id,kind,title,summary,why_it_matters,next_step,points,evidence_level,limitations,stars,sources,
   generated_by,generated_at,sources_as_of,supersedes,saved_by,subject_id,scope,sources_checked)
 VALUES(saved,p_insight->>'kind',p_insight->>'title',p_insight->>'summary',p_insight->>'whyItMatters',p_insight->>'nextStep',
   p_insight->'points',p_insight->>'evidenceLevel',p_insight->>'limitations',p_insight->'stars',p_insight->'sources',
   p_insight->>'generatedBy',(p_insight->>'generatedAt')::timestamptz,(p_insight->>'sourcesAsOf')::timestamptz,replaced,p_principal,
   landed,CASE WHEN landed='household' THEN 'shared' ELSE 'private' END,checked);
 RETURN saved;
END $$;

-- One view of the stream for a reader (proposed, older, confirmed, snoozed, closed or all), newest shown first, optionally of one
-- scope, from a page's place; and each view's length for this reader and that scope, read in the same statement, so a
-- count is the length of its list. A read writes nothing: a snooze ends here by the clock.
CREATE FUNCTION read_insight_stream(p_principal uuid,p_view text,p_scope text,p_limit int,p_after_at timestamptz,p_after_id uuid,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insights');
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 51 THEN RAISE EXCEPTION 'Invalid insight limit' USING ERRCODE='22023'; END IF;
 IF p_view IS NULL OR p_view NOT IN ('proposed','older','confirmed','snoozed','closed','all') THEN RAISE EXCEPTION 'Invalid stream view' USING ERRCODE='22023'; END IF;
 RETURN (WITH q AS (
     SELECT i AS rec,i.id,insight_stream_view(i.status,i.review,i.feedback,insight_replaced(i.id),insight_stream_at(i.status,i.feedback,i.created_at)) AS stream_view,insight_stream_at(i.status,i.feedback,i.created_at) AS shown_at
     FROM insights i WHERE (p_scope IS NULL OR i.scope=p_scope)
       AND insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars))
   SELECT jsonb_build_object(
     'proposed',(SELECT count(*) FROM q WHERE stream_view='proposed'),'older',(SELECT count(*) FROM q WHERE stream_view='older'),'confirmed',(SELECT count(*) FROM q WHERE stream_view='confirmed'),
     'snoozed',(SELECT count(*) FROM q WHERE stream_view='snoozed'),'closed',(SELECT count(*) FROM q WHERE stream_view='closed'),
     'items',coalesce((SELECT jsonb_agg(jsonb_build_object('id',p.id,'view',p.stream_view,'review',(p.rec).review,'reviewed_at',(p.rec).reviewed_at,
         'snooze_until',CASE WHEN (p.rec).status='snoozed' THEN (p.rec).feedback->'until' END,
         'snooze_ended',(p.rec).status='snoozed' AND NOT insight_snooze_in_force((p.rec).status,(p.rec).feedback),
         'shown_at',p.shown_at,'scope',(p.rec).scope,'owner',(p.rec).subject_id,
         'candidates',coalesce((SELECT jsonb_agg(c.id ORDER BY c.id) FROM memory_insight_candidates c WHERE c.insight_id=p.id),'[]'::jsonb),
         'insight',insight_json(p.rec)) ORDER BY p.shown_at DESC,p.id DESC)
       FROM (SELECT * FROM q WHERE (p_view='all' OR stream_view=p_view)
           AND (p_after_at IS NULL OR (shown_at,id)<(p_after_at,p_after_id))
         ORDER BY shown_at DESC,id DESC LIMIT p_limit) p),'[]'::jsonb)));
END $$;

-- What a day came to for this reader, in the window the engine passes: the newest insight it reads saved in it, or the
-- one the newest run it reads says it saved (a run that saves just before midnight and logs just after), the newest run
-- it reads, and how many candidates of the scopes it reads were declined in it.
CREATE FUNCTION read_insight_day(p_principal uuid,p_from timestamptz,p_to timestamptz,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
DECLARE saved uuid; run insight_runs;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insights');
 SELECT i.id INTO saved FROM insights i WHERE i.created_at>=p_from AND i.created_at<p_to
   AND insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars) ORDER BY i.created_at DESC,i.id LIMIT 1;
 -- The runs the reader reads, as read_insight_runs (027) reads them.
 SELECT x.* INTO run FROM insight_runs x WHERE x.created_at>=p_from AND x.created_at<p_to AND reads_subject(p_principal,x.subject_id)
   AND (x.insight_id IS NULL OR NOT scopes_on() OR EXISTS(SELECT 1 FROM insights i WHERE i.id=x.insight_id
     AND insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars)))
   ORDER BY x.created_at DESC,x.id LIMIT 1;
 saved:=coalesce(saved,CASE WHEN run.outcome='saved' THEN run.insight_id END);
 RETURN jsonb_build_object('saved',saved,'outcome',run.outcome,'note',run.note,
   'declined',(SELECT count(*) FROM memory_insight_events e WHERE e.event='rejected' AND e.at>=p_from AND e.at<p_to
     AND reads_subject(p_principal,e.subject_id)));
END $$;

-- An insight's feedback and reviews, newest first, for a reader of the insight: each with who gave it (a person's id,
-- or none) and through what. A feedback not carried in yet (it was given before the history, or with memory.tools off)
-- is served too, first, with no one named. Total: how many there are, whatever p_limit serves.
CREATE FUNCTION read_insight_history(p_principal uuid,p_id uuid,p_limit int,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER AS $$
DECLARE i insights;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Reading insights');
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Invalid history limit' USING ERRCODE='22023'; END IF;
 SELECT * INTO i FROM insights WHERE id=p_id;
 IF NOT FOUND OR NOT insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars) THEN
   RAISE EXCEPTION 'Unknown insight' USING ERRCODE='P0002';
 END IF;
 RETURN (WITH h AS (
     SELECT e.id,e.event,e.value,e.until_at,e.note,e.channel,e.client,e.at,(SELECT p.person_id FROM principals p WHERE p.id=e.principal_id) AS person
     FROM insight_events e WHERE e.insight_id=i.id
     UNION ALL
     SELECT NULL,'feedback',i.feedback->>'value',(i.feedback->>'until')::timestamptz,i.feedback->>'note',NULL,NULL,(i.feedback->>'at')::timestamptz,NULL
     WHERE i.feedback IS NOT NULL AND NOT EXISTS(SELECT 1 FROM insight_events e WHERE e.insight_id=i.id AND e.event='feedback'
       AND e.at=(i.feedback->>'at')::timestamptz))
   SELECT jsonb_build_object('review',i.review,'total',(SELECT count(*) FROM h),
     'items',coalesce((SELECT jsonb_agg(jsonb_build_object('event',x.event,'value',x.value,'until',x.until_at,'note',x.note,'person',x.person,
         'channel',x.channel,'client',x.client,'at',x.at) ORDER BY x.id DESC NULLS FIRST)
       FROM (SELECT * FROM h ORDER BY id DESC NULLS FIRST LIMIT p_limit) x),'[]'::jsonb)));
END $$;

-- resolve_insight_candidate as the memory tool calls it since 066: 064's decision (its own checks and lock, unchanged),
-- with why it was declined or the insight written from it. A repeat of the decision answers what was kept and writes
-- nothing, the insight only to a reader of its subject (064 lets anyone who reads the pair's scope repeat it, and an
-- insight may be narrower than a Family pair). 064's shorter form stays as it is for the release before.
CREATE FUNCTION resolve_insight_candidate(p_principal uuid,p_candidate uuid,p_action text,p_channel text,p_client text,p_note text,p_insight uuid,p_stars text[])
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE answered jsonb; said text:=btrim(p_note,E' \t\r\n'); c memory_insight_candidates; i insights;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Insight candidates are not available to this login' USING ERRCODE='42501';
 END IF;
 PERFORM memory_channel(p_principal,p_channel,p_client);
 IF length(said) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'note is 1 to 500 characters once trimmed' USING ERRCODE='22023'; END IF;
 IF p_insight IS NOT NULL AND p_action IS DISTINCT FROM 'use' THEN
   RAISE EXCEPTION 'insight_id names the insight written from a pair: it goes with action use' USING ERRCODE='22023';
 END IF;
 answered:=resolve_insight_candidate(p_principal,p_candidate,p_action,p_channel,p_client);
 SELECT * INTO c FROM memory_insight_candidates WHERE id=p_candidate;
 IF (answered->>'unchanged')::boolean THEN
   RETURN answered||jsonb_build_object('note',c.note,'insight_id',(SELECT x.id FROM insights x WHERE x.id=c.insight_id AND reads_subject(p_principal,x.subject_id)));
 END IF;
 IF p_insight IS NOT NULL THEN
   SELECT * INTO i FROM insights x WHERE x.id=p_insight;
   IF NOT FOUND OR NOT insight_readable(p_principal,i.subject_id,i.stars,i.sources,i.sources_checked,p_stars) THEN
     RAISE EXCEPTION 'insight_id names no insight this login reads' USING ERRCODE='22023';
   END IF;
   IF NOT ARRAY[c.older_id::text,c.newer_id::text]<@insight_locators(i.sources) THEN
     RAISE EXCEPTION 'An insight written from a pair cites both its memories: sources of kind memory, each locator a memory''s id' USING ERRCODE='22023';
   END IF;
   IF scopes_on() AND c.subject_id<>'household' AND i.subject_id<>c.subject_id THEN
     RAISE EXCEPTION 'An insight written from a private pair stays in that scope: this one is wider' USING ERRCODE='22023';
   END IF;
 END IF;
 UPDATE memory_insight_candidates SET note=said,insight_id=p_insight WHERE id=p_candidate;
 RETURN answered||jsonb_build_object('note',said,'insight_id',p_insight);
END $$;

DO $$ BEGIN
 -- CREATE OR REPLACE resets a function's settings, so the list gets its search path again.
 EXECUTE format('ALTER FUNCTION checked_tables() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_snooze_in_force(text,jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_replaced(uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_pending_days() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_stream_view(text,text,jsonb,boolean,timestamptz) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_stream_at(text,jsonb,timestamptz) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_may_review(uuid,text,uuid) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_carry_feedback(insights) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION review_insight(uuid,uuid,text,text,text,text,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION insight_feedback_kept(uuid,uuid,text,timestamptz,text,text[],text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION save_insight(uuid,jsonb,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insight_stream(uuid,text,text,int,timestamptz,uuid,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insight_day(uuid,timestamptz,timestamptz,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION read_insight_history(uuid,uuid,int,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION resolve_insight_candidate(uuid,uuid,text,text,text,text,uuid,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION checked_tables(),insight_snooze_in_force(text,jsonb),insight_replaced(uuid),insight_pending_days(),insight_stream_view(text,text,jsonb,boolean,timestamptz),
 insight_stream_at(text,jsonb,timestamptz),insight_may_review(uuid,text,uuid),insight_carry_feedback(insights),review_insight(uuid,uuid,text,text,text,text,text[]),
 insight_feedback_kept(uuid,uuid,text,timestamptz,text,text[],text,text),save_insight(uuid,jsonb,text[]),read_insight_stream(uuid,text,text,int,timestamptz,uuid,text[]),
 read_insight_day(uuid,timestamptz,timestamptz,text[]),read_insight_history(uuid,uuid,int,text[]),
 resolve_insight_candidate(uuid,uuid,text,text,text,text,uuid,text[]) FROM PUBLIC;
