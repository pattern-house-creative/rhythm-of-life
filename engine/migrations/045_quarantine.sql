-- Quarantine, the rest (SB-P5-01 in docs/dev/second-brain-parity/backlog.md; D-037, D-039, D-050, D-161; ledger rows
-- SEC-10, SEC-11 and SEC-13; docs/dev/memory.md section 5): write bursts and channel factors on top of 036's holds.
-- 036's screening scores every write's words as an AI client's, in a trigger on memories that cannot see who is
-- writing. This migration decides the rest when the write's audit row goes in, which names the login and the channel
-- the writer checked (033's memory_channel): a trigger on memory_events that Postgres fires before 036's
-- memory_events_held (a table's BEFORE triggers fire in name order), so the audit row and the write's reply say what it
-- decided (the transaction's rhythm.memory_held note, as 036 and 041 keep it).
--
-- Bursts (SEC-13; adapted from Second Brain's src/quarantine/burst.ts and score.ts at the pinned commit, MIT; NOTICE):
-- a memory write through an AI client's channel (mcp: a create, an append or an update) by a login that has already
-- made 40 such writes in the last ten minutes is held, reason burst, whatever its words, so a runaway or hijacked
-- client's writes wait for a person. The write being counted is never one of the 40 (its row is not there yet). One
-- login is one actor: a person's two AI clients are counted apart, as their logins are. A burst hold is a hold like any
-- other: out of search, the map and capsules, released only by a person (D-039), and a release lasts as 036 says. A
-- memory held before the write keeps its hold; one this write's words held keeps their reason, with the burst added to
-- its signals and its score. An undo, a status change, a release, the trash and an import are not counted, as upstream
-- counts only its content writes; nor is a change the engine makes to a memory for another step, which its audit row
-- names as its cause (a project's delete takes its memories out of it, 035).
--
-- Channel factors (SEC-11, D-037): upstream ignores instruction phrases on a person's own channel and still counts
-- hidden characters. Rhythm's person's own channels are the app through a login bound to a current person, a viewer
-- session (028), so a person is signed in, and the terminal through a current person's own terminal login (044), which
-- no AI client holds. There a hold this write's screening put on is decided again on hidden characters and length
-- alone, before the write ends: lifted when they would not hold it (and so never recorded), else kept with their reason
-- and score; a released memory's change is held again only for a hidden character it adds or text newly too long, as
-- 036 judges one. Every other channel stays scored as an AI client: the app's shared login (no person: anyone with the
-- app token, D-037), MCP, and an import (D-037, stricter than upstream). Upstream's mirror factor (1.25) has no channel
-- here. app.viewers is off on every install and refused (D-046), so no viewer session writes yet.
--
-- A held project (046): a project a held write made stays out of every list until a person lets it in. A write through
-- a channel scored as an AI client's that names such a project, or moves a memory into one, is held too, reason
-- held_project, whatever its words, so the login that planted the held write cannot list its slug with one clean write
-- more, and no answer of that memory names the slug; a person's release of either lists the project (046). That holds
-- for the planted memory itself once a person has released it in another project: the project it left while held was
-- never seen by that person (D-161), so a write that takes it back there is held again. And a project such a write
-- makes is screened by its slug, hyphens read as spaces: a slug that reads like instructions to an AI client (036's
-- memory_screen) holds the write, reason instruction with the slug's signals, so a clean memory cannot plant an
-- instruction as the name of a new project, which would otherwise list at once to every AI client of its scope.
--
-- Capsule changes over MCP stay refused outright (039, 041; D-172), stricter than upstream's half-weight signal, so no
-- capsule signal is scored here. Nothing writes a memory through MCP unless memory.tools is on (off by default, D-177),
-- so an install with no native memories answers as before. The number is provisional.

-- The burst rule: a login's memory writes through MCP in the window, once there are this many, hold the next one.
CREATE FUNCTION memory_burst_limit() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 40 $$;
CREATE FUNCTION memory_burst_window() RETURNS interval LANGUAGE sql IMMUTABLE AS $$ SELECT interval '10 minutes' $$;

-- The writes a burst counts, by login and time: a count reads at most the limit's rows, however large the burst. A row
-- whose detail names a cause is the engine's change for another step, not the login's own write.
CREATE INDEX memory_events_mcp_writes ON memory_events(principal_id,at)
 WHERE channel='mcp' AND memory_id IS NOT NULL AND event IN ('created','appended','updated') AND NOT (detail ? 'cause');

-- 036's hidden-character signals with their weights, as memory_screen counts them (MemoryHolds.Signals' first five; a
-- test keeps them equal).
CREATE FUNCTION memory_hidden_signals() RETURNS TABLE(signal text,weight numeric) LANGUAGE sql IMMUTABLE AS $$
 VALUES ('tag_characters',1.0),('zero_width',0.6),('bidi_controls',0.6),('html_comment',0.4),('encoded_run',0.3)
$$;

-- A verdict of 036's memory_screen with its instruction phrases left out: its hidden signals, in its order, and their
-- score; held as too long when it was (length is no phrase), else as hidden from 1.0.
CREATE FUNCTION memory_verdict_hidden(p_verdict jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
 SELECT jsonb_build_object('score',round(coalesce(sum(h.weight),0),2),
   'reason',CASE WHEN p_verdict->>'reason'='too_long' THEN 'too_long' WHEN coalesce(sum(h.weight),0)>=1.0 THEN 'hidden' END,
   'signals',coalesce(jsonb_agg(s.signal ORDER BY s.ord) FILTER (WHERE h.signal IS NOT NULL),'[]'::jsonb))
 FROM jsonb_array_elements_text(coalesce(p_verdict->'signals','[]'::jsonb)) WITH ORDINALITY s(signal,ord)
 LEFT JOIN memory_hidden_signals() h ON h.signal=s.signal
$$;

-- Whether a write came through a person's own channel (D-037): the app, through an enabled app login bound to a person
-- who is current, which only a viewer session is (023, 028); or the terminal, through an enabled terminal login of a
-- current person (044), which holds no token anyone has and which the door refuses. The app's shared login has no
-- person, and every other harness login is an AI client's.
CREATE FUNCTION memory_own_channel(p_principal uuid,p_channel text) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT coalesce(p_channel IN ('app','cli'),false) AND EXISTS(SELECT 1 FROM principals p
   JOIN subjects s ON s.id=p.person_id AND s.kind='person' AND s.status='current'
   WHERE p.id=p_principal AND p.enabled
     AND (p_channel='app' AND p.role='app' OR p_channel='cli' AND p.role='harness' AND p.terminal))
$$;

-- The hold a person's own channel leaves on memory m, which this write's screening held: the verdict on hidden
-- characters and length alone over the text and tags the write left, or null when they would not hold it. For a memory
-- that was not held before the write (p_before, the state it retired) though its words would have been (a person
-- released it), only a hidden character the write adds, counted over the whole text as 036 counts instances, or text
-- newly too long holds it again, and the verdict is then over the whole text.
CREATE FUNCTION memory_own_verdict(m memories,p_before memory_versions) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE verdict jsonb:=memory_verdict_hidden(memory_screen(m.content,m.tags)); before jsonb;
BEGIN
 IF verdict->>'reason' IS NULL THEN RETURN NULL; END IF;
 IF p_before.memory_id IS NOT NULL AND p_before.state->>'hold' IS NULL THEN
   before:=memory_verdict_hidden(memory_screen(p_before.content,p_before.tags));
   IF before->>'reason' IS NOT NULL THEN
     IF (verdict->>'reason'<>'too_long' OR before->>'reason'='too_long') AND NOT EXISTS(
         SELECT 1 FROM (SELECT a.signal,a.matched,count(*) AS n FROM memory_signal_matches(m.content,m.tags) a
             WHERE a.signal IN (SELECT h.signal FROM memory_hidden_signals() h) GROUP BY 1,2) a
         LEFT JOIN (SELECT b.signal,b.matched,count(*) AS n FROM memory_signal_matches(p_before.content,p_before.tags) b
             WHERE b.signal IN (SELECT h.signal FROM memory_hidden_signals() h) GROUP BY 1,2) b ON b.signal=a.signal AND b.matched=a.matched
         WHERE a.n>coalesce(b.n,0)) THEN RETURN NULL; END IF;
     verdict:=memory_verdict_hidden(memory_screen(m.content,m.tags,true));
   END IF;
 END IF;
 RETURN verdict;
END $$;

-- The channel's say on a memory write, as its audit row goes in (NEW names the login, the checked channel, the event
-- and the revision the write left). First a person's own channel: a hold this write's screening put on (the note at
-- this revision; never a held revision an undo brought back, which 041 notes as restored) is decided again without
-- instruction phrases. Then a held project: a write through any other channel that leaves a memory not held in a project
-- a held write made, and no person let in yet (046), holds it, reason held_project, the memory whose held write made it
-- included (a release elsewhere vouched for none of its other projects); in a project this write made (held, for no
-- memory yet) it holds it when the slug would hold as text, with the slug's verdict. Then a burst: an AI
-- client's create, append or update after its login's 40th such write in ten minutes holds the memory, unless it was
-- held before this write. A change the engine makes for another step (its detail names a cause) is neither. The note is
-- kept for 036's memory_events_held and the write's reply.
CREATE FUNCTION memory_event_channel_hold() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE notes jsonb; noted jsonb; m memories; v memory_versions; own jsonb; earlier integer; slug text; planted uuid;
BEGIN
 IF NEW.memory_id IS NULL OR NEW.event NOT IN ('created','appended','updated','reverted') THEN RETURN NEW; END IF;
 notes:=coalesce(nullif(current_setting('rhythm.memory_held',true),'')::jsonb,'{}'::jsonb);
 noted:=notes->(NEW.memory_id::text);
 IF noted IS NOT NULL AND (noted->>'revision' IS DISTINCT FROM NEW.revision::text OR coalesce((noted->>'restored')::boolean,false)) THEN
   noted:=NULL;
 END IF;
 IF noted IS NOT NULL AND memory_own_channel(NEW.principal_id,NEW.channel) THEN
   SELECT * INTO m FROM memories WHERE id=NEW.memory_id;
   IF NEW.event<>'created' THEN SELECT * INTO v FROM memory_versions WHERE memory_id=m.id AND revision=NEW.revision-1; END IF;
   own:=memory_own_verdict(m,v);
   IF own IS NULL THEN
     UPDATE memories SET hold=NULL,hold_score=NULL WHERE id=m.id;
     notes:=notes-(m.id::text);
   ELSE
     UPDATE memories SET hold=own->>'reason',hold_score=(own->>'score')::real
     WHERE id=m.id AND (hold IS DISTINCT FROM own->>'reason' OR hold_score IS DISTINCT FROM (own->>'score')::real);
     notes:=notes||jsonb_build_object(m.id::text,own||jsonb_build_object('revision',NEW.revision));
   END IF;
   PERFORM set_config('rhythm.memory_held',notes::text,true);
   RETURN NEW;
 END IF;
 IF NEW.event NOT IN ('created','appended','updated') OR coalesce(NEW.detail ? 'cause',false) THEN RETURN NEW; END IF;
 IF NOT memory_own_channel(NEW.principal_id,NEW.channel) THEN
   SELECT p.slug,p.held_for INTO slug,planted FROM memories x JOIN memory_projects p ON p.id=x.project_id
   WHERE x.id=NEW.memory_id AND x.hold IS NULL AND p.held;
   IF FOUND THEN
     own:=CASE WHEN planted IS NOT NULL THEN '{"score":1.0,"reason":"held_project","signals":["held_project"]}'::jsonb
       ELSE memory_screen(replace(slug,'-',' ')) END;
     IF own->>'reason' IS NOT NULL THEN
       noted:=own||jsonb_build_object('revision',NEW.revision);
       UPDATE memories SET hold=noted->>'reason',hold_score=(noted->>'score')::real WHERE id=NEW.memory_id;
       notes:=notes||jsonb_build_object(NEW.memory_id::text,noted);
       PERFORM set_config('rhythm.memory_held',notes::text,true);
     END IF;
   END IF;
 END IF;
 IF NEW.channel<>'mcp' OR NEW.principal_id IS NULL THEN RETURN NEW; END IF;
 SELECT count(*) INTO earlier FROM (SELECT 1 FROM memory_events e WHERE e.principal_id=NEW.principal_id AND e.channel='mcp'
   AND e.memory_id IS NOT NULL AND e.event IN ('created','appended','updated') AND NOT (e.detail ? 'cause')
   AND e.at>now()-memory_burst_window() LIMIT memory_burst_limit()) x;
 IF earlier<memory_burst_limit() THEN RETURN NEW; END IF;
 IF noted IS NOT NULL THEN
   noted:=noted||jsonb_build_object('score',round((noted->>'score')::numeric+1.0,2),'signals',coalesce(noted->'signals','[]'::jsonb)||'["burst"]'::jsonb);
   UPDATE memories SET hold_score=(noted->>'score')::real WHERE id=NEW.memory_id;
 ELSIF EXISTS(SELECT 1 FROM memories WHERE id=NEW.memory_id AND hold IS NULL) THEN
   noted:=jsonb_build_object('score',1.0,'reason','burst','signals','["burst"]'::jsonb,'revision',NEW.revision);
   UPDATE memories SET hold='burst',hold_score=1.0 WHERE id=NEW.memory_id;
 ELSE
   RETURN NEW;
 END IF;
 PERFORM set_config('rhythm.memory_held',(notes||jsonb_build_object(NEW.memory_id::text,noted))::text,true);
 RETURN NEW;
END $$;
CREATE TRIGGER memory_events_channel_hold BEFORE INSERT ON memory_events FOR EACH ROW EXECUTE FUNCTION memory_event_channel_hold();

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_burst_limit() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_burst_window() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_hidden_signals() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_verdict_hidden(jsonb) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_own_channel(uuid,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_own_verdict(memories,memory_versions) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_channel_hold() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_burst_limit(),memory_burst_window(),memory_hidden_signals(),memory_verdict_hidden(jsonb),
 memory_own_channel(uuid,text),memory_own_verdict(memories,memory_versions),memory_event_channel_hold() FROM PUBLIC;
