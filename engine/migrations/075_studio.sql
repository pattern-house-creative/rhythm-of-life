-- Durable Studio drafts. No saved version is changed until studio_publish locks and compares both bases.
CREATE TABLE studio_sessions(
 id uuid PRIMARY KEY, owner uuid NOT NULL REFERENCES principals, screen text NOT NULL REFERENCES screens ON DELETE CASCADE,
 dashboard uuid REFERENCES dashboards ON DELETE CASCADE, base_version int NOT NULL DEFAULT 0, base_layout int NOT NULL DEFAULT 0,
 revision int NOT NULL DEFAULT 0, valid_revision int NOT NULL DEFAULT 0,
 status text NOT NULL DEFAULT 'active' CHECK(status IN ('active','canceled','discarded','saved')),
 candidate jsonb, preview jsonb, controls jsonb NOT NULL DEFAULT '[]', preview_controls jsonb NOT NULL DEFAULT '[]',
 events jsonb NOT NULL DEFAULT '[]', feedback jsonb NOT NULL DEFAULT '[]', feedback_seq int NOT NULL DEFAULT 0,
 result jsonb, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL DEFAULT now()+interval '7 days',
 CHECK(revision BETWEEN 0 AND 100), CHECK(jsonb_array_length(feedback)<=100));
CREATE UNIQUE INDEX studio_one_active_screen ON studio_sessions(screen) WHERE status='active';
CREATE TABLE studio_requests(session uuid NOT NULL REFERENCES studio_sessions ON DELETE CASCADE, request uuid NOT NULL,
 fingerprint text NOT NULL, reply jsonb NOT NULL, PRIMARY KEY(session,request));
CREATE TABLE dashboard_controls(dashboard uuid NOT NULL REFERENCES dashboards ON DELETE CASCADE, version int NOT NULL,
 manifest jsonb NOT NULL, PRIMARY KEY(dashboard,version),
 FOREIGN KEY(dashboard,version) REFERENCES dashboard_versions(dashboard_id,version) ON DELETE CASCADE);
REVOKE ALL ON studio_sessions,studio_requests,dashboard_controls FROM PUBLIC;

CREATE FUNCTION studio_access(p_principal uuid,p_id uuid,p_screen text,p_stars text[]) RETURNS studio_sessions
LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE s studio_sessions; role text; v jsonb;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Studio');
 SELECT r.role INTO role FROM principals r WHERE r.id=p_principal;
 SELECT * INTO s FROM studio_sessions WHERE id=p_id;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown Studio session' USING ERRCODE='P0002'; END IF;
 IF role='harness' AND s.owner<>p_principal OR role='app' AND (s.screen IS DISTINCT FROM p_screen
   OR NOT EXISTS(SELECT 1 FROM screens x WHERE x.id=s.screen AND screen_login_is(p_principal,x.bound_to))) THEN
   RAISE EXCEPTION 'Studio belongs to another caller' USING ERRCODE='42501';
 END IF;
 IF s.dashboard IS NOT NULL AND NOT dashboard_readable(p_principal,s.dashboard,p_stars) THEN
   RAISE EXCEPTION 'Unknown Studio session' USING ERRCODE='P0002';
 END IF;
 -- Code and history are readable only while every referenced candidate/preview source is still readable.
 IF scopes_on() THEN
   FOREACH v IN ARRAY ARRAY[s.candidate,s.preview] LOOP
     IF v IS NULL THEN CONTINUE; END IF;
     IF NOT stars_readable(ARRAY(SELECT x->>'graphId' FROM jsonb_array_elements(v->'stars') x),p_stars)
       OR EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(v->'feeds','[]')||coalesce(v->'writes','[]')) f WHERE f->>'kind'='log' AND NOT reads_subject(p_principal,f->>'subject'))
       OR EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(v->'layout'->'parts','[]')) part
         WHERE part->>'kind'='card' AND NOT dashboard_readable(p_principal,(part->>'dashboard')::uuid,p_stars)) THEN
       RAISE EXCEPTION 'Studio sources are no longer readable' USING ERRCODE='42501';
     END IF;
   END LOOP;
 END IF;
 RETURN s;
END $$;

CREATE FUNCTION studio_change(p_principal uuid,p_id uuid,p_screen text,p_op text,p_request uuid,p_expected int,
 p_payload jsonb,p_stars text[] DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE s studio_sessions; role text; prior studio_requests; reply jsonb; fp text; d uuid; v int; l int;
BEGIN
 PERFORM dashboard_reader(p_principal,ARRAY['harness','app'],'Studio');
 SELECT r.role INTO role FROM principals r WHERE r.id=p_principal;
 IF p_op='begin' THEN
   IF role<>'harness' OR p_screen IS NULL THEN RAISE EXCEPTION 'Only an agent starts Studio' USING ERRCODE='42501'; END IF;
   PERFORM 1 FROM screens WHERE id=p_screen AND seen_at>now()-interval '60 seconds' FOR UPDATE;
   IF NOT FOUND THEN RAISE EXCEPTION 'Target screen is not open' USING ERRCODE='P0002'; END IF;
   IF NOT EXISTS(SELECT 1 FROM studio_sessions WHERE id=p_id) THEN
     d:=(p_payload->>'dashboard')::uuid;
     IF d IS NOT NULL THEN
       IF NOT dashboard_readable(p_principal,d,p_stars) THEN RAISE EXCEPTION 'Unknown dashboard' USING ERRCODE='P0002'; END IF;
       SELECT max(version) INTO v FROM dashboard_versions WHERE dashboard_id=d;
       SELECT coalesce(revision,0) INTO l FROM dashboard_layouts WHERE dashboard_id=d AND version=v;
     END IF;
     UPDATE studio_sessions SET status='canceled' WHERE screen=p_screen AND status='active' AND expires_at<=now();
     IF EXISTS(SELECT 1 FROM studio_sessions WHERE screen=p_screen AND status='active') THEN
       RAISE EXCEPTION 'This screen already has an active Studio session' USING ERRCODE='22023';
     END IF;
     INSERT INTO studio_sessions(id,owner,screen,dashboard,base_version,base_layout)
       VALUES(p_id,p_principal,p_screen,d,coalesce(v,0),coalesce(l,0));
   END IF;
 END IF;
 SELECT * INTO s FROM studio_sessions WHERE id=p_id FOR UPDATE;
 s:=studio_access(p_principal,p_id,p_screen,p_stars);
 IF p_op='begin' AND (s.screen IS DISTINCT FROM p_screen OR s.dashboard IS DISTINCT FROM (p_payload->>'dashboard')::uuid) THEN
   RAISE EXCEPTION 'The session target is immutable' USING ERRCODE='22023'; END IF;
 IF s.status='active' AND s.expires_at<=now() THEN
   UPDATE studio_sessions SET status='canceled' WHERE id=p_id; s.status:='canceled';
 END IF;
 IF p_op='read' THEN RETURN to_jsonb(s)-'owner'; END IF;
 IF p_request IS NULL THEN RAISE EXCEPTION 'requestId is required' USING ERRCODE='22023'; END IF;
 fp:=md5(jsonb_build_array(p_op,p_expected,coalesce(p_payload->'fingerprint',p_payload))::text);
 SELECT * INTO prior FROM studio_requests WHERE session=p_id AND request=p_request;
 IF FOUND THEN
   IF prior.fingerprint<>fp THEN RAISE EXCEPTION 'requestId was used for another operation' USING ERRCODE='22023'; END IF;
   RETURN (to_jsonb(s)-'owner')||jsonb_build_object('replayed',true,'appliedRevision',prior.reply->'revision');
 END IF;
 IF s.status<>'active' OR s.expires_at<=now() THEN RAISE EXCEPTION 'Studio session has ended' USING ERRCODE='22023'; END IF;
 IF p_op NOT IN ('begin','cancel','discard','feedback') AND p_expected IS DISTINCT FROM s.revision THEN
   RAISE EXCEPTION 'Draft revision changed; read Studio again' USING ERRCODE='22023';
 END IF;
 IF role='harness' AND p_op IN ('validate','feedback') THEN RAISE EXCEPTION 'Only the target screen reports this' USING ERRCODE='42501'; END IF;
 IF role='app' AND p_op IN ('begin','update','progress') THEN RAISE EXCEPTION 'Only the owning agent supplies drafts' USING ERRCODE='42501'; END IF;
 CASE p_op
 WHEN 'begin' THEN NULL;
 WHEN 'update' THEN
   IF s.revision>=100 OR p_payload->'draft' IS NULL OR length(p_payload::text)>350000 THEN
     RAISE EXCEPTION 'Draft exceeds the session limits' USING ERRCODE='22023'; END IF;
   s.revision:=s.revision+1; s.candidate:=p_payload->'draft'; s.controls:=coalesce(p_payload->'controls','[]');
 WHEN 'progress' THEN NULL;
 WHEN 'validate' THEN
   IF s.candidate IS NULL THEN RAISE EXCEPTION 'No candidate to validate' USING ERRCODE='22023'; END IF;
   IF p_payload->>'ok'='true' THEN s.valid_revision:=s.revision; s.preview:=s.candidate; s.preview_controls:=s.controls;
   ELSIF s.valid_revision=s.revision THEN s.valid_revision:=0; END IF;
 WHEN 'feedback' THEN
   IF jsonb_array_length(s.feedback)>=100 THEN RAISE EXCEPTION 'Feedback queue is full' USING ERRCODE='22023'; END IF;
   s.feedback_seq:=s.feedback_seq+1;
   s.feedback:=s.feedback||jsonb_build_array(jsonb_build_object('seq',s.feedback_seq,'text',p_payload->>'text','at',clock_timestamp()));
 WHEN 'cancel' THEN s.status:='canceled';
 WHEN 'discard' THEN s.status:='discarded';
 ELSE RAISE EXCEPTION 'Unknown Studio operation' USING ERRCODE='22023';
 END CASE;
 s.events:=s.events||jsonb_build_array(jsonb_build_object('seq',jsonb_array_length(s.events)+1,'op',p_op,'revision',s.revision,
   'at',clock_timestamp(),'message',p_payload->>'message','ok',p_payload->'ok'));
 IF jsonb_array_length(s.events)>400 THEN RAISE EXCEPTION 'Studio event limit reached' USING ERRCODE='22023'; END IF;
 UPDATE studio_sessions SET revision=s.revision,valid_revision=s.valid_revision,status=s.status,candidate=s.candidate,preview=s.preview,
 controls=s.controls,preview_controls=s.preview_controls,events=s.events,feedback=s.feedback,feedback_seq=s.feedback_seq,updated_at=clock_timestamp() WHERE id=p_id;
 reply:=to_jsonb(s)-'owner';
 INSERT INTO studio_requests VALUES(p_id,p_request,fp,jsonb_build_object('revision',s.revision,'status',s.status));
 RETURN reply;
END $$;

CREATE FUNCTION studio_publish(p_principal uuid,p_id uuid,p_screen text,p_request uuid,p_expected int,p_version jsonb,p_stars text[],p_view_stars text[] DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE s studio_sessions; saved jsonb; v int; l int;
BEGIN
 SELECT * INTO s FROM studio_sessions WHERE id=p_id FOR UPDATE;
 s:=studio_access(p_principal,p_id,p_screen,CASE WHEN (SELECT role FROM principals WHERE id=p_principal)='app' THEN p_view_stars ELSE p_stars END);
 IF s.status='saved' THEN RETURN s.result; END IF;
 IF s.status<>'active' OR s.expires_at<=now() OR s.revision<>p_expected OR s.valid_revision<>s.revision OR s.preview IS NULL THEN
   RAISE EXCEPTION 'Studio has no current validated draft to save' USING ERRCODE='22023'; END IF;
 IF (p_version-'cited') IS DISTINCT FROM (s.candidate-'cited') THEN
   RAISE EXCEPTION 'Only the validated candidate can be saved' USING ERRCODE='22023'; END IF;
 -- App save is an explicit user action, using the original harness identity and the same author/scope policy.
 PERFORM dashboard_reader(s.owner,ARRAY['harness'],'Save Studio');
 IF s.dashboard IS NOT NULL THEN
   PERFORM 1 FROM dashboards WHERE id=s.dashboard FOR UPDATE;
   SELECT max(version) INTO v FROM dashboard_versions WHERE dashboard_id=s.dashboard;
   SELECT coalesce(revision,0) INTO l FROM dashboard_layouts WHERE dashboard_id=s.dashboard AND version=v;
   IF v<>s.base_version OR coalesce(l,0)<>s.base_layout THEN
     RAISE EXCEPTION 'Saved dashboard changed; start a new draft from its current version' USING ERRCODE='22023'; END IF;
 END IF;
 saved:=save_dashboard(s.owner,s.dashboard,NULL,p_version,p_stars);
 INSERT INTO dashboard_controls VALUES((saved->>'id')::uuid,(saved->>'version')::int,s.controls);
 UPDATE studio_sessions SET status='saved',result=saved,updated_at=clock_timestamp() WHERE id=p_id;
 RETURN saved;
END $$;
REVOKE ALL ON FUNCTION studio_access(uuid,uuid,text,text[]),studio_change(uuid,uuid,text,text,uuid,int,jsonb,text[]),studio_publish(uuid,uuid,text,uuid,int,jsonb,text[],text[]) FROM PUBLIC;
