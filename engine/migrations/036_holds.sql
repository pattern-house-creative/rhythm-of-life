-- Holds for native memories (SB-P3-18 in docs/dev/second-brain-parity/backlog.md; D-050, D-039, D-042; ledger rows
-- SEC-10 and SEC-12; docs/dev/memory.md section 5): text that reads like instructions to an AI client, text with hidden
-- characters and text over 32 KiB is held for review from the first write. A held memory keeps its words, but no passage
-- is made from it (PassageSources.Memory, SemanticSearch.Index), combined retrieval never serves it (Recall.NotHeld), a
-- list serves it without its words (MemoryStore.List), the map never lists it (MemoryStars, SB-P3-11), and nothing
-- puts it in a capsule (SB-P3-08 keeps it out when it does). Only a person releases it, from the app or the terminal, never an
-- AI client (release_memory_hold below); the release is audited and keeps the held state as a version, so undoing it
-- (SB-P3-05) holds the memory again.
--
-- The score is the database's own, so every writer is screened alike, the ones to come included (an import, an undo, a
-- later migration's writer): a trigger screens a memory's text and tags whenever a row is written or its text or tags
-- change, under the row lock the writer holds, on the whole text the write leaves (an append is screened with the text
-- it joins, so an instruction split across two writes is still found). Nothing writes a memory yet (no tool calls the
-- store before SB-P3-09), so native memories stay inert for every install and every existing answer is as before.
--
-- Scoring, adapted from Second Brain's quarantine (src/quarantine/score.ts, hold.ts and normalize.ts at the pinned
-- commit; NOTICE): each signal counts once, by its weight, and a memory is held when they add up to 1.0 or more.
-- Hidden characters are looked for in the text as written; instruction phrases in the text normalised (NFKC, invisible
-- and direction characters removed, curly apostrophes made straight, lower case, white space collapsed), so neither a
-- zero-width space inside a word nor full-width letters hide a phrase. Rhythm's patterns are its own, English only. Every
-- channel is scored as an AI client is (MCP): channel factors, the damping inside quotes and code, write bursts and
-- capsule changes are phase 5's (SB-P5-01, D-037). Text over 32 KiB is held as too long, its first 24 KiB and last 8 KiB
-- scanned for the signals the reply and the audit name. A score is a starting point to review, not a verdict on intent:
-- a person's own note that says "ignore the previous instructions" is held until a person releases it.
-- A release lasts for the text and tags it covered, and only for them: a later change of a released memory is screened
-- for each instance of a signal it adds, anywhere in the whole text (nothing cut) and whatever families the released
-- text had, so an instruction appended to a long memory's unscanned middle, or one of a family the released text
-- already held, is held again, while an ordinary append or a change of tags is not (memory_screening).
-- The number is provisional.

-- The instruction families: a signal, its weight, and the patterns that find it in normalised text (ARE syntax, \y a
-- word boundary). A row with a second pattern (also) counts only when that matches too: a request to send something
-- somewhere counts only beside an outside address.
CREATE FUNCTION memory_hold_patterns() RETURNS TABLE(ord integer,signal text,weight numeric,pattern text,also text) LANGUAGE sql IMMUTABLE AS $$
 VALUES
  -- Telling the reader to drop what it was told, or that it is now someone with more power.
  (1,'ignore_previous',1.0,$p$\y(?:ignore|disregard|forget|override|bypass|skip)\s+(?:all\s+|any\s+)?(?:of\s+)?(?:the\s+|your\s+|my\s+|these\s+|those\s+)?(?:previous|prior|above|earlier|preceding|former|original|existing|system|safety)\s+(?:instructions?|prompts?|rules|guidelines|directions|directives|commands|messages|context|constraints)\y$p$,NULL::text),
  (1,'ignore_previous',1.0,$p$\ysystem\s+override\y$p$,NULL),
  (1,'ignore_previous',1.0,$p$\yyou\s+are\s+now\s+(?:in\s+)?(?:an?\s+|the\s+)?(?:[a-z']+\s+){0,2}(?:admin|administrator|developer|root|god|jailbroken|unrestricted|unfiltered|dan|superuser)(?:\s+mode)?\y$p$,NULL),
  (1,'ignore_previous',1.0,$p$\y(?:new|updated|real|actual)\s+(?:system\s+)?instructions\s*:$p$,NULL),
  (1,'ignore_previous',1.0,$p$\y(?:enter|enable|activate|switch\s+to)\s+(?:developer|admin|god|debug|jailbreak|dan)\s+mode\y$p$,NULL),
  -- Speaking to an AI that reads the text.
  (2,'ai_address',0.6,$p$\y(?:note|message|instructions?|reminder|attention|p\.?\s?s\.?|ps)\s*(?:to|for)\s+(?:the\s+|any\s+|all\s+|every\s+|my\s+|our\s+|your\s+)?(?:ai|a\.i\.|assistants?|ai\s+assistants?|llms?|language\s+models?|chat\s?bots?|bots?|agents?|claude|chatgpt|gpt|copilot|gemini)\y$p$,NULL),
  (2,'ai_address',0.6,$p$\y(?:any|every|all|each)\s+(?:ai\s+)?(?:assistants?|ais|llms?|models?|agents?|bots?)\s+(?:reading|processing|seeing|parsing|that\s+reads|who\s+reads)\y$p$,NULL),
  (2,'ai_address',0.6,$p$(?:^|[.!?:;]\s*)(?:system|assistant|ai)\s*:$p$,NULL),
  (2,'ai_address',0.6,$p$(?:^|[.!?:;,]\s*)(?:assistant|ai|claude|chatgpt|gpt|bot)\s*,\s*(?:please\s+)?(?:you\s+|from\s+now\s+on\s+)?[a-z]$p$,NULL),
  (2,'ai_address',0.6,$p$\yas\s+(?:your|the|an?)\s+(?:trusted\s+|helpful\s+|personal\s+)?(?:ai\s+)?assistant\s+(?:you|i)\s+(?:must|should|will|shall|have\s+to|need\s+to)\y$p$,NULL),
  (2,'ai_address',0.6,$p$\yas\s+an\s+ai(?:\s+language\s+model|\s+assistant)?\s*,$p$,NULL),
  (2,'ai_address',0.6,$p$\y(?:dear|hey|hi|hello)\s+(?:ai|assistant|claude|chatgpt|gpt|bot)\y$p$,NULL),
  -- Keeping the instruction, or what it does, from the person.
  (3,'hide_from_user',0.8,$p$\y(?:do\s+not|don't|dont|never|must\s+not|mustn't|should\s+not|shouldn't)\s+(?:mention|reveal|disclose|show|repeat|acknowledge|admit|read\s+out|tell\s+(?:anyone|anybody|the\s+user|them)\s+about)\s+(?:this|these|that)\s+(?:line|note|message|instructions?|text|memory|part|request|rule)\y$p$,NULL),
  (3,'hide_from_user',0.8,$p$\y(?:hide|conceal|keep)\s+(?:this|these|that|it)\s+(?:(?:line|note|message|instructions?|text|memory)\s+)?(?:secret\s+|hidden\s+)?from\s+(?:the\s+user|the\s+person|everyone|anyone)\y$p$,NULL),
  (3,'hide_from_user',0.8,$p$\ywithout\s+(?:asking|telling|informing|notifying|alerting)(?:\s+(?:anyone|anybody|the\s+user|them|first))?\y$p$,NULL),
  (3,'hide_from_user',0.8,$p$\y(?:the\s+user|they)\s+(?:must|should)\s+(?:not|never)\s+(?:know|find\s+out|see)\y$p$,NULL),
  -- Scripting what to answer.
  (4,'when_asked',0.6,$p$\ywhen(?:ever)?\s+(?:[a-z']+\s+){1,3}(?:asks?|asked|asking)\y[^.!?]{0,120}?\y(?:tell|say|answer|reply|respond|claim|reveal|share|give)\y$p$,NULL),
  (4,'when_asked',0.6,$p$\y(?:reply|respond|answer|say)\s+only\s+(?:with|by|that)\y$p$,NULL),
  (4,'when_asked',0.6,$p$\y(?:to|with)\s+(?:whoever|anyone\s+who|anybody\s+who)\s+asks\y$p$,NULL),
  -- Steering what the reader recommends.
  (5,'always_recommend',0.4,$p$\y(?:always|never|only)\s+(?:recommend|suggest|promote|endorse|advertise)\y$p$,NULL),
  -- Acting on many records at once, or sending something to an outside address.
  (6,'act_outside',0.5,$p$\y(?:delete|remove|erase|wipe|purge|destroy|move|export|share|send|forward|leak|upload)\s+(?:all|every|each|any)\s+(?:of\s+)?(?:the\s+|your\s+|my\s+)?(?:[a-z']+\s+){0,2}(?:memor(?:y|ies)|notes?|records?|files?|messages?|data|passwords?)\y$p$,NULL),
  (6,'act_outside',0.5,$p$\y(?:send|pay|transfer|wire|forward|upload|submit|book|post|email)\y[^.!?]{0,80}?\y(?:to|through|via|at|there)\y$p$,
    $p$https?://|www\.|@[a-z0-9-]+\.[a-z]{2,}|\y[a-z0-9-]+(?:\.[a-z0-9-]+)+\.[a-z]{2,}\y|\y[a-z0-9-]+\.(?:com|net|org|io|co|uk|info|biz|app|dev|xyz|me|invalid|example)\y$p$)
$$;

-- Text as the instruction phrases are matched in it: NFKC (in a UTF-8 database, which it checks), invisible and
-- direction characters removed, curly apostrophes made straight, lower case, white space collapsed.
CREATE FUNCTION memory_screen_plain(p_text text) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE plain text:=CASE WHEN current_setting('server_encoding')='UTF8' THEN normalize(p_text,NFKC) ELSE p_text END;
BEGIN
 plain:=regexp_replace(plain,'[­᠎​-‏⁠-⁤﻿‪-‮⁦-⁩\U000E0000-\U000E007F]','','g');
 RETURN lower(regexp_replace(regexp_replace(plain,'[‘’ʼ]','''','g'),'\s+',' ','g'));
END $$;

-- The verdict on a memory's text and tags: {"score": n, "reason": "instruction" | "hidden" | "too_long" | null,
-- "signals": [...]}, held when reason is not null. Reason too_long for text over 32 768 bytes of UTF-8 (D-042), whatever
-- the score; else hidden when a hidden-character signal is among those that held it (no person types them), else
-- instruction. A long text's first 24 KiB and last 8 KiB are scanned, or all of it with p_whole (the verdict of a
-- change that adds a signal to a released memory, memory_screening). Stable rather than immutable only because NFKC
-- needs a UTF-8 database, which it checks.
CREATE FUNCTION memory_screen(p_text text,p_tags text[] DEFAULT '{}',p_whole boolean DEFAULT false) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE
 long boolean:=octet_length(coalesce(p_text,''))>32768;
 scanned text; plain text; signals text[]:='{}'; hidden numeric:=0; told numeric:=0; f record;
BEGIN
 scanned:=CASE WHEN long AND NOT coalesce(p_whole,false) THEN left(p_text,24576)||E'\n'||right(p_text,8192) ELSE coalesce(p_text,'') END
   ||E'\n'||coalesce(array_to_string(p_tags,E'\n'),'');
 -- Hidden characters, as written: tag characters (an invisible copy of ASCII), three or more zero-width characters,
 -- any control of text direction, an HTML comment, a long run of base64.
 IF scanned ~ '[\U000E0000-\U000E007F]' THEN signals:=signals||'tag_characters'::text; hidden:=hidden+1.0; END IF;
 IF regexp_count(scanned,'[\u200B\u200C\u200D\u2060\uFEFF\u180E]')>=3 THEN signals:=signals||'zero_width'::text; hidden:=hidden+0.6; END IF;
 IF scanned ~ '[\u202A-\u202E\u2066-\u2069]' THEN signals:=signals||'bidi_controls'::text; hidden:=hidden+0.6; END IF;
 IF scanned ~ '<!--.*?-->' THEN signals:=signals||'html_comment'::text; hidden:=hidden+0.4; END IF;
 IF scanned ~ '[A-Za-z0-9+/]{200,}' THEN signals:=signals||'encoded_run'::text; hidden:=hidden+0.3; END IF;
 plain:=memory_screen_plain(scanned);
 FOR f IN SELECT p.signal,max(p.weight) AS weight,bool_or(plain ~ p.pattern AND (p.also IS NULL OR plain ~ p.also)) AS hit
   FROM memory_hold_patterns() p GROUP BY p.signal ORDER BY min(p.ord) LOOP
   IF f.hit THEN signals:=signals||f.signal; told:=told+f.weight; END IF;
 END LOOP;
 RETURN jsonb_build_object('score',round(hidden+told,2),
   'reason',CASE WHEN long THEN 'too_long' WHEN hidden+told>=1.0 THEN CASE WHEN hidden>0 THEN 'hidden' ELSE 'instruction' END END,
   'signals',to_jsonb(signals));
END $$;

-- Each instance of a signal in a text and its tags, all of it (nothing cut, unlike memory_screen's head and tail): the
-- signal and the text that matched, as memory_screen finds them. Hidden characters as written, each run of them (each
-- zero-width character, and only where there are three or more); instruction phrases in the normalised text, each
-- match (a request to send something only beside an outside address). memory_screening compares a released memory's
-- instances before and after a change, so it holds the change for an instance it adds, never for one the release
-- covered. A test keeps the signals found here equal to memory_screen's over the whole text.
CREATE FUNCTION memory_signal_matches(p_text text,p_tags text[] DEFAULT '{}') RETURNS TABLE(signal text,matched text) LANGUAGE plpgsql STABLE AS $$
DECLARE scanned text:=coalesce(p_text,'')||E'\n'||coalesce(array_to_string(p_tags,E'\n'),''); plain text;
BEGIN
 RETURN QUERY SELECT 'tag_characters'::text,m[1] FROM regexp_matches(scanned,'[\U000E0000-\U000E007F]+','g') m;
 IF regexp_count(scanned,'[​‌‍⁠﻿᠎]')>=3 THEN
   RETURN QUERY SELECT 'zero_width'::text,m[1] FROM regexp_matches(scanned,'[​‌‍⁠﻿᠎]','g') m;
 END IF;
 RETURN QUERY SELECT 'bidi_controls'::text,m[1] FROM regexp_matches(scanned,'[‪-‮⁦-⁩]+','g') m;
 RETURN QUERY SELECT 'html_comment'::text,m[1] FROM regexp_matches(scanned,'<!--.*?-->','g') m;
 RETURN QUERY SELECT 'encoded_run'::text,m[1] FROM regexp_matches(scanned,'[A-Za-z0-9+/]{200,}','g') m;
 plain:=memory_screen_plain(scanned);
 RETURN QUERY SELECT p.signal,m[1] FROM memory_hold_patterns() p CROSS JOIN LATERAL regexp_matches(plain,p.pattern,'g') m
   WHERE p.also IS NULL OR plain ~ p.also;
END $$;

-- Every row written, and every change of a memory's text or tags, is screened before it is stored. A memory held
-- already stays held, whatever its new text says: only release_memory_hold lifts a hold, and it changes neither text nor
-- tags, so it is never screened again by its own change. A release lasts for the text and tags it covered: a later
-- change of a released memory (one whose old text and tags would be held) is held only for what it adds, an instance of
-- a signal the old text and tags did not have as many of (memory_signal_matches, over the whole text: an instruction
-- appended into a long memory's unscanned middle counts, and so does one of a family the released text already had) or
-- text newly too long; the hold's verdict is then over the whole text. So an append or a change of tags that adds no
-- signal never holds again what a person released, and an instruction a change adds is always held. A memory this
-- screening holds is noted for the transaction (rhythm.memory_held: its id, the verdict and the revision it was given
-- at), for that revision's audit row and the write's reply.
CREATE FUNCTION memory_screening() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE verdict jsonb; before jsonb;
BEGIN
 IF TG_OP='UPDATE' AND NEW.content IS NOT DISTINCT FROM OLD.content AND NEW.tags IS NOT DISTINCT FROM OLD.tags THEN RETURN NEW; END IF;
 IF NEW.hold IS NOT NULL THEN RETURN NEW; END IF;
 verdict:=memory_screen(NEW.content,NEW.tags);
 IF verdict->>'reason' IS NULL THEN RETURN NEW; END IF;
 IF TG_OP='UPDATE' THEN
   before:=memory_screen(OLD.content,OLD.tags);
   IF before->>'reason' IS NOT NULL THEN
     IF (verdict->>'reason'<>'too_long' OR before->>'reason'='too_long') AND NOT EXISTS(
         SELECT 1 FROM (SELECT a.signal,a.matched,count(*) AS n FROM memory_signal_matches(NEW.content,NEW.tags) a GROUP BY 1,2) a
         LEFT JOIN (SELECT b.signal,b.matched,count(*) AS n FROM memory_signal_matches(OLD.content,OLD.tags) b GROUP BY 1,2) b
           ON b.signal=a.signal AND b.matched=a.matched
         WHERE a.n>coalesce(b.n,0)) THEN RETURN NEW; END IF;
     verdict:=memory_screen(NEW.content,NEW.tags,true);
   END IF;
 END IF;
 NEW.hold:=verdict->>'reason';
 NEW.hold_score:=(verdict->>'score')::real;
 PERFORM set_config('rhythm.memory_held',(coalesce(nullif(current_setting('rhythm.memory_held',true),'')::jsonb,'{}'::jsonb)
   ||jsonb_build_object(NEW.id::text,verdict||jsonb_build_object('revision',NEW.revision)))::text,true);
 RETURN NEW;
END $$;
CREATE TRIGGER memory_screening BEFORE INSERT OR UPDATE OF content,tags ON memories FOR EACH ROW EXECUTE FUNCTION memory_screening();

-- The audit row of the write that held a memory says so, in its detail ("held": the verdict), so the trail keeps one
-- row per write and still records each hold with who wrote it, through which channel and client (SB-P3-04). Only the
-- row of the revision the hold was given at: a later write in the same transaction is not marked held by it.
CREATE FUNCTION memory_event_held() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE held jsonb;
BEGIN
 IF NEW.memory_id IS NULL OR NEW.event NOT IN ('created','appended','updated') THEN RETURN NEW; END IF;
 held:=nullif(current_setting('rhythm.memory_held',true),'')::jsonb->(NEW.memory_id::text);
 IF held IS NOT NULL AND held->>'revision'=NEW.revision::text THEN
   NEW.detail:=NEW.detail||jsonb_build_object('held',held-'revision');
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER memory_events_held BEFORE INSERT ON memory_events FOR EACH ROW EXECUTE FUNCTION memory_event_held();

-- release_memory_hold: a person lifts a memory's hold (D-039), from the app (the app's login) or the terminal (a login
-- bound to a person, SB-P3-19), never through an AI client's channel (MCP). With scopes off any admitted login of those
-- channels may, as for every change (D-048); with them on only those memory.md section 5 names (the author lock). The
-- held state is kept as a version and the release audited, so an undo (SB-P3-05) can hold it again; the memory then
-- gets its passages (026's trigger cleared any) and reaches search. Releasing one that is not held writes nothing and
-- answers unchanged. A stale expected revision is refused (40001), as for every change.
CREATE FUNCTION release_memory_hold(p_principal uuid,p_memory uuid,p_expected integer,p_channel text,p_client text DEFAULT NULL)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER AS $$
DECLARE scoped boolean; channel text; m memories; was text; score real;
BEGIN
 IF NOT principal_has_role(p_principal,ARRAY['harness','app']) THEN
   RAISE EXCEPTION 'Writing memories is not available to this login' USING ERRCODE='42501';
 END IF;
 scoped:=lock_memory_scopes();
 channel:=memory_channel(p_principal,p_channel,p_client);
 IF channel NOT IN ('app','cli') THEN
   RAISE EXCEPTION 'A held memory is released only by a person, from the app or the terminal, never by an AI client' USING ERRCODE='42501';
 END IF;
 m:=memory_for_change(p_principal,p_memory,p_expected,scoped);
 IF m.hold IS NULL THEN RETURN memory_reply(m,scoped)||jsonb_build_object('unchanged',true); END IF;
 was:=m.hold; score:=m.hold_score;
 PERFORM memory_retire(m);
 UPDATE memories SET hold=NULL,hold_score=NULL,revision=revision+1,updated_at=greatest(now(),updated_at)
 WHERE id=m.id RETURNING * INTO m;
 INSERT INTO memory_events(memory_id,subject_id,revision,event,principal_id,channel,client,detail)
 VALUES(m.id,m.subject_id,m.revision,'released',p_principal,channel,p_client,jsonb_build_object('hold',was,'score',score));
 RETURN memory_reply(m,scoped);
END $$;

DO $$ BEGIN
 EXECUTE format('ALTER FUNCTION memory_hold_patterns() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_screen_plain(text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_screen(text,text[],boolean) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_signal_matches(text,text[]) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_screening() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION memory_event_held() SET search_path TO pg_catalog,%I,pg_temp',current_schema());
 EXECUTE format('ALTER FUNCTION release_memory_hold(uuid,uuid,integer,text,text) SET search_path TO pg_catalog,%I,pg_temp',current_schema());
END $$;
REVOKE ALL ON FUNCTION memory_hold_patterns(),memory_screen_plain(text),memory_screen(text,text[],boolean),
 memory_signal_matches(text,text[]),memory_screening(),memory_event_held(),
 release_memory_hold(uuid,uuid,integer,text,text) FROM PUBLIC;
