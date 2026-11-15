#!/bin/sh
# cli/lifecycle.sh: a fresh install's native memory lifecycle end to end, with Rhythm alone (no Second Brain), in a
# throwaway Docker stack, with fictional data only (SB-P3-16, phase 3's exit: a person and an MCP client complete a
# full memory lifecycle in a fresh disposable install, undo included, without Second Brain).
#
#   this checkout's images -> init (no input, no demo data) -> up, the model on and nothing downloaded, Second Brain off
#   and nothing of it anywhere -> the memory tools off by default: no memory tool, no recall, no instructions at
#   initialize, no Memories view, no combined search in the app -> memory.tools and engine.recall on in rhythm.yaml ->
#   an AI client (a token bound to Alex) captures a memory and an unrelated one beside it, recalls the first by its
#   words and by its meaning alone, each time ranked above the other by the arm the explanation names, edits it
#   with the revision it read (a stale revision changes nothing), undoes the edit, forgets it into the trash (out of
#   get, lists and recall) and restores it with the trash's nonce, every change in its history as the AI client's ->
#   a person, in the app's Memories view, captures a memory, finds it in the app's explorer search and at the
#   terminal, edits it, undoes the edit, forgets it and restores it, every change in its history as the app's ->
#   rhythm export holds the three memories, the two lifecycles' as restored with their earlier states, and importing
#   that file again adds nothing -> memory scopes and memory.share on in rhythm.yaml (SB-P3-10, Q-157): the share tool
#   listed; Alex's AI client remembers a private memory that Sam's AI client neither gets nor recalls, shares it into
#   Family (Sam then reads it with its author, recalls it, and sees its history from the share on, and may not take it
#   back), the same share again changing nothing, and takes it back (Sam's next get and recall find nothing), every move
#   in its history as the AI client's -> down -v, the stack's volumes included
#
# A step that proves part of an acceptance story names it (step "<what>" <story> ...),
# and its line in the output says so: scripts/acceptance.mjs (SB-P6-08) runs this script after cli/e2e.sh as the
# stories' sandbox stack and reads which steps passed.
#
#   RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files> \
#   COMPOSE_PROJECT_NAME=rhythm-lifecycle RHYTHM_VERSION=lifecycle RHYTHM_APP_PORT=18280 RHYTHM_MCP_PORT=18381 \
#   sh cli/lifecycle.sh
#
# The last four are the defaults. RHYTHM_MODEL_DIR has none: the folder must hold model_quantized.onnx and vocab.txt
# (docs/models.md), which the engine and the terminal read mounted read-only (cli/e2e.compose.yaml), so no model is
# downloaded (tests never download one). Its first step builds the engine and app images from this checkout under
# $RHYTHM_VERSION (with the build cache, never --pull), so the stack runs this checkout's code; those builds need the
# image and package registries for what the cache does not hold. It pulls no image and moves no tag of another
# image. It runs only in a checkout with no install (no deploy/compose/rhythm.yaml, .env or secrets/), in a Compose
# project with no containers or volumes yet, with an image tag no other container runs and no other Compose project
# built (an install whose stack is down runs no container, but its next up would run what this run built), and on
# ports nothing answers on, and removes what it made, the stack's volumes included; the images it builds stay, tagged
# $RHYTHM_VERSION.
set -eu
cd "$(dirname "$0")/.."
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME-rhythm-lifecycle}" RHYTHM_VERSION="${RHYTHM_VERSION:-lifecycle}" MSYS_NO_PATHCONV=1
app=${RHYTHM_APP_PORT:-18280}
mcp=${RHYTHM_MCP_PORT:-18381}
unset RHYTHM_APP_PORT RHYTHM_MCP_PORT # init writes them into .env
deploy=deploy/compose
secrets=$deploy/secrets
refuse() { echo "cli/lifecycle.sh: $*" >&2; exit 2; }
# Every Compose call names this run's project (-p), never one Compose would infer: with none exported and no .env it
# falls back to the stack's own name, rhythm, an install's, and the cleanup removes everything in its project (D-240).
case "$(printf %s "$COMPOSE_PROJECT_NAME" | tr 'A-Z' 'a-z')" in
  "" | rhythm) refuse "the Compose project '$COMPOSE_PROJECT_NAME' is empty or an install's default (rhythm): give the run its own (COMPOSE_PROJECT_NAME)." ;;
esac
if [ -e $deploy/rhythm.yaml ] || [ -e $deploy/.env ] || [ -e $secrets ]; then
  echo "An install exists in $deploy; cli/lifecycle.sh runs only in a checkout without one." >&2
  exit 2
fi
model=${RHYTHM_MODEL_DIR:-}
if [ -z "$model" ] || [ ! -f "$model/model_quantized.onnx" ] || [ ! -f "$model/vocab.txt" ]; then
  refuse "NOT RUN: set RHYTHM_MODEL_DIR to a folder holding the pinned MiniLM files (model_quantized.onnx, vocab.txt);" \
    "without them the engine's first start would download the model (D-041)."
fi
command -v curl >/dev/null 2>&1 || refuse "needs curl."
# Its cleanup removes everything in its project, volumes included, and its first step builds its images under its tag:
# so a project of its own, a tag no other stack runs, and ports nothing else answers on.
label="label=com.docker.compose.project=$COMPOSE_PROJECT_NAME"
made=$(docker ps -aq --filter "$label") && volumes=$(docker volume ls -q --filter "$label") || refuse "Docker does not answer."
[ -z "$made$volumes" ] ||
  refuse "the Compose project $COMPOSE_PROJECT_NAME already has containers or volumes: give the run its own (COMPOSE_PROJECT_NAME)."
for image in rhythm-engine rhythm-app; do
  [ -z "$(docker ps -aq --filter "ancestor=$image:$RHYTHM_VERSION" 2>/dev/null)" ] ||
    refuse "another stack's containers run $image:$RHYTHM_VERSION, which the run builds: give it its own tag (RHYTHM_VERSION)."
  # An image under the tag that another Compose project built (Compose labels what it builds with its project), or that
  # no Compose project did, belongs to someone else, whose stack may only be down.
  if built=$(docker image inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$image:$RHYTHM_VERSION" 2>/dev/null); then
    [ "$built" = "$COMPOSE_PROJECT_NAME" ] ||
      refuse "$image:$RHYTHM_VERSION is already here, built by ${built:-no Compose project}, not by $COMPOSE_PROJECT_NAME: give the run its own tag (RHYTHM_VERSION)."
  fi
done
for port in "$app" "$mcp"; do
  answered=0
  curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/" || answered=$?
  [ "$answered" = 7 ] || refuse "something already answers on 127.0.0.1:$port: choose free ports (RHYTHM_APP_PORT, RHYTHM_MCP_PORT)."
done
# The engine and the terminal read the model from the host folder (cli/e2e.compose.yaml). Docker on Windows takes C:/
# paths, not Git Bash's /c/ ones, and the wrappers run Compose in deploy/compose, where these file names resolve.
RHYTHM_E2E_MODEL_DIR=$(cd "$model" && { pwd -W 2>/dev/null || pwd; })
export RHYTHM_E2E_MODEL_DIR COMPOSE_PATH_SEPARATOR=: COMPOSE_FILE=compose.yaml:../../cli/e2e.compose.yaml

dc() { (cd $deploy && docker compose -p "$COMPOSE_PROJECT_NAME" "$@"); }
cleanup() {
  # The files belong to the services' users, so they are removed from inside a container.
  dc --profile cli run --rm --no-deps --entrypoint rm cli -rf /work/rhythm.yaml /work/.env /work/secrets /work/data >/dev/null 2>&1 || true
  dc --profile cli --profile backup --profile restore down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT
# step "<what>" [<story> ...]: a step's line, with the acceptance stories it proves part of (scripts/acceptance.mjs).
step() {
  what=$1; shift
  case $# in 0) printf '\n== %s\n' "$what" ;; 1) printf '\n== %s  [story %s]\n' "$what" "$1" ;;
    *) printf '\n== %s  [stories %s]\n' "$what" "$(echo "$*" | sed 's/ /, /g')" ;; esac
}
fail() { echo "FAILED: $*" >&2; exit 1; }
mcp() { # <token file> <JSON-RPC body>
  curl -s -X POST "http://127.0.0.1:$mcp/mcp" -H "Authorization: Bearer $(cat "$1")" -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' -H 'MCP-Protocol-Version: 2025-03-26' -d "$2"
}
# call <token file> <tool> <arguments as JSON>: one tool's answer as JSON. A memory tool's answer is its structured
# content; a refusal, and the tools that answer in text alone (recall, search_resources, get_sync_status), are that
# text with its quotes unescaped (a text block writes each as \u0022 or \"). Each value read below is in it once.
call() {
  out=$(mcp "$1" "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"method\":\"tools/call\",\"params\":{\"name\":\"$2\",\"arguments\":$3}}") || true
  case "$out" in
    *'"structuredContent":'*) printf '%s\n' "${out#*\"structuredContent\":}" ;;
    *) printf '%s\n' "$out" | sed 's/\\u0022/"/g; s/\\"/"/g' ;;
  esac
}
# value <JSON> <key>: the first value of that key in it, a string's without its quotes (a number, true or null as is).
value() { printf '%s' "$1" | grep -oE "\"$2\":(\"[^\"]*\"|[^,}]*)" | head -n 1 | sed "s/^\"$2\"://; s/^\"//; s/\"\$//"; }
# The app's Memories view as its household view reads and writes it (no viewer session), through the app's facade.
memories() { curl -s -X POST "http://127.0.0.1:$app/api/memories" -H 'Content-Type: application/json' -d "$1"; }
# The app's explorer search (app_graph operation recall): the stars on the map that stand for what it finds.
explore() { curl -s -X POST "http://127.0.0.1:$app/api/graph" -H 'Content-Type: application/json' -d "{\"operation\":\"recall\",\"value\":\"$1\"}"; }
explores() { explore "$1" | grep -q "$2"; } # <words> <text>: the explorer's answer (its stars and results) holds the text
# rebuilt <check> [<argument> ...]: the app's Refresh, asked again until the map's wait between rebuilds lets one in,
# until the check passes (four minutes at most): a memory reaches the map at a rebuild. Under MSYS_NO_PATHCONV Git
# Bash's curl, a Windows program, gets /dev/null unchanged and cannot write to it, so the shell discards each answer.
rebuilt() {
  tries=0
  until "$@"; do
    tries=$((tries + 1))
    [ "$tries" -lt 120 ] || return 1
    curl -s -X POST "http://127.0.0.1:$app/api/graph-refresh" >/dev/null || return 1
    sleep 2
  done
}
uuid='[0-9a-f]\{8\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{12\}'
tab=$(printf '\t')

step "this checkout's images, then init with no input and no demo data" 18
dc build engine app >/dev/null 2>&1 || fail "could not build the engine and app images from this checkout"
out=$(./rhythm init --no-input --people "Alex, Sam" --timezone Europe/London --demo no --app-port "$app" --mcp-port "$mcp" 2>&1) ||
  fail "init: $out"
echo "$out" | tail -n 3
for f in postgres_password engine_db app_token backup_password; do [ -s $secrets/$f ] || fail "no $f"; done
grep -q '^demo:' $deploy/rhythm.yaml && grep -A1 '^demo:' $deploy/rhythm.yaml | grep -q 'enabled: false' || fail "init did not leave the demo data off"
grep -A1 '^  second-brain:' $deploy/rhythm.yaml | grep -q 'enabled: false' || fail "a fresh install has the Second Brain adapter on"
grep -A1 '^  calendar:' $deploy/rhythm.yaml | grep -q 'enabled: false' || fail "a fresh install has the calendar adapter on"
[ ! -e $secrets/adapters/calendar_link ] || fail "a fresh install holds a calendar link"
grep -q '^  scopes: off ' $deploy/rhythm.yaml || fail "a fresh install's memory scopes are not off"
grep -Eq '^  (tools|capsules|share): ' $deploy/rhythm.yaml && fail "a fresh install names a memory switch besides scopes"
grep -Eq '^  (recall|recallLog): ' $deploy/rhythm.yaml && fail "a fresh install names engine.recall or engine.recallLog"

step "up: the model on and nothing downloaded, Second Brain nowhere" 18
out=$(./rhythm up 2>/dev/null) || fail "up: $out"
echo "$out" | tail -n 3
curl -s "http://127.0.0.1:$app/api/instance" | grep -q '"mode":"live"' || fail "the app is not in live mode"
# The engine loads the model in the background after it starts: up to a minute for it to say on.
tries=0
until curl -s "http://127.0.0.1:$mcp/health" | grep -q '"embeddings":"on"'; do
  tries=$((tries + 1))
  [ "$tries" -lt 30 ] || fail "the engine did not load the model from RHYTHM_MODEL_DIR: $(curl -s "http://127.0.0.1:$mcp/health")"
  sleep 2
done
dc exec -T engine sh -c '[ ! -e /var/lib/rhythm/models ]' || fail "the engine downloaded a model into its data folder"
out=$(./rhythm doctor 2>/dev/null) || { echo "$out"; fail "doctor found problems"; }
echo "$out" | grep -q 'ok    embeddings model minilm (live) is on' || { echo "$out"; fail "doctor does not say the model is on"; }
./rhythm token create life-alex --person alex >/dev/null 2>&1 || fail "token create for Alex"
./rhythm token list 2>/dev/null | grep -q "^life-alex${tab}harness${tab}alex${tab}" || fail "token list does not name Alex"
ai=$secrets/harness_life-alex_token
# No record, sync job or resource of Second Brain: the install holds Rhythm's own data only.
out=$(call "$ai" search_resources '{"provider":"second-brain"}')
echo "$out" | grep -q '"text":"\[\]"' || fail "search_resources finds Second Brain resources in a fresh install: $out"
out=$(call "$ai" get_sync_status '{}')
echo "$out" | grep -q '"result"' && ! echo "$out" | grep -q '"isError":true' || fail "get_sync_status did not answer: $out"
echo "$out" | grep -q 'second-brain' && fail "get_sync_status names Second Brain in a fresh install: $out"

step "the memory tools off by default: no memory tool, no recall, no instructions, no Memories view" 18
listed=$(mcp "$ai" '{"jsonrpc":"2.0","id":"l","method":"tools/list"}')
echo "$listed" | grep -q '"save_document"' || fail "tools/list did not answer: $listed"
for tool in remember append update get list_recent set_status forget undo history list_projects list_teams recall get_prompt_capsule share; do
  echo "$listed" | grep -q "\"name\":\"$tool\"" && fail "tools/list names $tool with its switch off"
done
out=$(call "$ai" remember '{"content":"DEMO: a memory no tool takes while memory.tools is off."}')
echo "$out" | grep -q '"ok":true' && fail "remember stored a memory with memory.tools off: $out"
init='{"jsonrpc":"2.0","id":"i","method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"DEMO lifecycle","version":"1"}}}'
out=$(mcp "$ai" "$init")
echo "$out" | grep -q '"serverInfo"' || fail "initialize did not answer: $out"
echo "$out" | grep -q '"instructions"' && fail "initialize gives instructions with memory.tools off"
[ "$(curl -s "http://127.0.0.1:$app/api/memories")" = '{"available":false}' ] || fail "the app offers its Memories view with memory.tools off"
[ "$(memories '{"op":"list"}')" = '{"available":false}' ] || fail "the app's Memories view answers with memory.tools off"
explore "anything" | grep -q '"available":false' || fail "the app's explorer offers combined search with engine.recall off"

step "memory.tools and engine.recall on, as an operator turns them on" 18
# rhythm.yaml belongs to the services' users, so it is changed from inside a container, as the cleanup removes it.
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e '/^memory:/a\  tools: on' -e '/^engine:/a\  recall: on' -e '/^engine:/a\  writeLimit: on' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not switch memory.tools and engine.recall on in rhythm.yaml"
grep -A1 '^memory:' $deploy/rhythm.yaml | grep -q '^  tools: on$' && grep -A1 '^engine:' $deploy/rhythm.yaml | grep -q '^  recall: on$' ||
  fail "rhythm.yaml does not say memory.tools and engine.recall on"
./rhythm up >/dev/null 2>&1 || fail "up with memory.tools and engine.recall on"
tries=0
until curl -s "http://127.0.0.1:$mcp/health" | grep -q '"embeddings":"on"'; do
  tries=$((tries + 1))
  [ "$tries" -lt 30 ] || fail "the engine did not load the model again"
  sleep 2
done
listed=$(mcp "$ai" '{"jsonrpc":"2.0","id":"l","method":"tools/list"}')
for tool in remember append update get list_recent set_status forget undo history list_projects list_teams recall; do
  echo "$listed" | grep -q "\"name\":\"$tool\"" || fail "tools/list does not name $tool with its switch on"
done
echo "$listed" | grep -q '"name":"get_prompt_capsule"' && fail "tools/list names get_prompt_capsule, whose switch is still off"
echo "$listed" | grep -q '"name":"share"' && fail "tools/list names share, whose switch is still off"
mcp "$ai" "$init" | grep -q '"instructions":"You have a memory in Rhythm of Life' || fail "initialize gives no memory instructions with memory.tools on"
[ "$(curl -s "http://127.0.0.1:$app/api/memories")" = '{"available":true}' ] || fail "the app has no Memories view with memory.tools on"
./rhythm doctor >/dev/null 2>&1 || fail "doctor found problems with memory.tools and engine.recall on"

step "an AI client captures a memory: a draft, the household's while scopes are off, stored once for a retried key" 3 18
first="DEMO: the Larkspur Row allotment water butt is emptied every Sunday evening."
made=$(call "$ai" remember "{\"content\":\"$first\",\"tags\":[\"larkspur\"],\"project\":\"larkspur-row\",\"kind\":\"semantic\",\"idempotency_key\":\"lifecycle-ai-capture-01\"}")
[ "$(value "$made" ok)" = true ] || fail "remember: $made"
mid=$(value "$made" id)
echo "remembered $mid"
[ "$(value "$made" status) $(value "$made" revision) $(value "$made" scope) $(value "$made" scopes)" = "draft 1 shared off" ] ||
  fail "remember did not store a draft at revision 1 in the household's scope: $made"
[ "$(value "$made" slug) $(value "$made" created)" = "larkspur-row true" ] || fail "remember did not make the project: $made"
[ "$(value "$made" held)" = null ] || fail "the memory was held: $made"
again=$(call "$ai" remember "{\"content\":\"$first\",\"tags\":[\"larkspur\"],\"project\":\"larkspur-row\",\"kind\":\"semantic\",\"idempotency_key\":\"lifecycle-ai-capture-01\"}")
[ "$(value "$again" id) $(value "$again" replayed)" = "$mid true" ] || fail "a retry with the same key stored again: $again"
got=$(call "$ai" get "{\"id\":\"$mid\"}")
[ "$(value "$got" content)" = "$first" ] || fail "get does not give the memory's words: $got"
call "$ai" list_recent '{"project":"larkspur-row"}' | grep -q "\"id\":\"$mid\"" || fail "list_recent does not list it in its project"

step "recall: the AI client finds it first by its words, and by its meaning alone once it is indexed" 4 18
# An unrelated memory beside it, so that each recall below has another to rank it above: with one memory any question
# would find it, since the meaning arm has no similarity floor.
other="DEMO: the Hawthorn Close recycling goes out on Tuesday nights."
made=$(call "$ai" remember "{\"content\":\"$other\",\"project\":\"hawthorn-close\"}")
[ "$(value "$made" ok)" = true ] || fail "remember the unrelated memory: $made"
did=$(value "$made" id)
out=$(call "$ai" recall '{"query":"Larkspur Row water butt","explain":true}')
[ "$(value "$out" id)" = "$mid" ] || fail "recall does not find the memory first by its words: $out"
echo "$out" | grep -q '"keyword":{"rank":1' || fail "the keyword arm did not rank it first by its words: $out"
[ "$(value "$out" semanticSkipped)" = false ] || fail "recall skipped the meaning arm: $out"
# No word of this question is in either memory, so only the meaning arm ranks them, once the index job has embedded
# both (a bounded run a minute): asked again for up to three minutes, until it answers both, the memory first.
meaning='{"query":"when does someone drain the rain barrel at the garden plot","topK":2,"explain":true}'
tries=0
until out=$(call "$ai" recall "$meaning") && [ "$(value "$out" id)" = "$mid" ] && echo "$out" | grep -q "\"id\":\"$did\""; do
  tries=$((tries + 1))
  [ "$tries" -lt 36 ] || fail "recall never ranked the memory above the unrelated one by its meaning: $out"
  sleep 5
done
echo "$out" | grep -q '"keyword":{' && fail "the meaning question matched a memory's words: $out"
echo "$out" | grep -q '"meaning":{"rank":1' || fail "the meaning arm did not rank it first: $out"
echo "$out" | grep -q '"meaning":{"rank":2' || fail "the meaning arm did not rank the unrelated memory second: $out"

step "edit: an update with the revision read, a stale one refused and changing nothing" 3 18
edited="DEMO: the Larkspur Row allotment water butt is emptied every Saturday morning now."
out=$(call "$ai" update "{\"id\":\"$mid\",\"content\":\"$edited\",\"expected_revision\":1}")
[ "$(value "$out" ok) $(value "$out" revision)" = "true 2" ] || fail "update: $out"
out=$(call "$ai" update "{\"id\":\"$mid\",\"content\":\"DEMO: a stale edit of the Larkspur Row water butt.\",\"expected_revision\":1}")
[ "$(value "$out" code) $(value "$out" revision)" = "conflict 2" ] || fail "an update with a stale revision was not refused: $out"
got=$(call "$ai" get "{\"id\":\"$mid\"}")
[ "$(value "$got" content) $(value "$got" revision)" = "$edited 2" ] || fail "get does not give the edit: $got"
# Words only the edit holds, found by the keyword arm; the words it replaced no longer are.
out=$(call "$ai" recall '{"query":"Saturday morning","explain":true}')
[ "$(value "$out" id)" = "$mid" ] || fail "recall does not find the edit's words first: $out"
echo "$out" | grep -q '"keyword":{"rank":1[^}]*"saturday"' || fail "the keyword arm did not find the edit's words: $out"
out=$(call "$ai" recall '{"query":"Sunday evening","explain":true}')
echo "$out" | grep -q '"keyword":{' && fail "the keyword arm still finds the words the edit replaced: $out"
[ "$(value "$(call "$ai" get "{\"id\":\"$mid\",\"version\":1}")" content)" = "$first" ] || fail "the first revision is not kept"

step "undo: the edit undone, the first words back as a change of its own" 3 18
out=$(call "$ai" undo "{\"id\":\"$mid\"}")
[ "$(value "$out" action) $(value "$out" to) $(value "$out" revision)" = "reverted 1 3" ] || fail "undo: $out"
got=$(call "$ai" get "{\"id\":\"$mid\"}")
[ "$(value "$got" content) $(value "$got" revision)" = "$first 3" ] || fail "get does not give the first words after the undo: $got"

step "trash: forget moves it to the trash, out of get, the lists and recall" 3 18
out=$(call "$ai" forget "{\"id\":\"$mid\",\"expected_revision\":3}")
[ "$(value "$out" ok) $(value "$out" days)" = "true 14" ] || fail "forget: $out"
nonce=$(value "$out" nonce)
echo "$nonce" | grep -q "^$uuid\$" || fail "forget gave no trash nonce: $out"
[ "$(value "$(call "$ai" get "{\"id\":\"$mid\"}")" code)" = unknown ] || fail "get still gives the forgotten memory"
call "$ai" list_recent '{"n":50}' | grep -q "\"id\":\"$mid\"" && fail "list_recent still lists the forgotten memory"
call "$ai" recall '{"query":"Larkspur Row water butt"}' | grep -q "$mid" && fail "recall still finds the forgotten memory"
out=$(call "$ai" list_recent '{"in_trash":true,"n":50}')
row=$(echo "$out" | grep -o "{\"id\":\"$mid\"[^}]*}" | head -n 1)
[ -n "$row" ] || fail "the trash does not list it: $out"
# Deleting for good is a person's step: the AI client's trash never offers it.
[ "$(value "$row" nonce) $(value "$row" may_delete_forever)" = "$nonce false" ] || fail "the AI client's trash row: $row"

step "restore: the trash's nonce brings it back with its words, and the same nonce again changes nothing" 3 18
out=$(call "$ai" undo "{\"id\":\"$mid\",\"nonce\":\"$nonce\"}")
[ "$(value "$out" action) $(value "$out" revision)" = "restored 3" ] || fail "restore: $out"
got=$(call "$ai" get "{\"id\":\"$mid\"}")
[ "$(value "$got" content) $(value "$got" revision)" = "$first 3" ] || fail "get does not give the restored memory: $got"
[ "$(value "$(call "$ai" recall '{"query":"Larkspur Row water butt"}')" id)" = "$mid" ] || fail "recall does not find the restored memory"
out=$(call "$ai" undo "{\"id\":\"$mid\",\"nonce\":\"$nonce\"}")
[ "$(value "$out" ok)" != true ] || [ "$(value "$out" unchanged)" = true ] || fail "a retried restore changed something: $out"
[ "$(value "$(call "$ai" get "{\"id\":\"$mid\"}")" revision)" = 3 ] || fail "a retried restore moved the revision"
call "$ai" list_recent '{"in_trash":true,"n":50}' | grep -q "\"id\":\"$mid\"" && fail "the restored memory is still in the trash"

step "the AI client's history: every change, newest first, each through its own channel for Alex" 3 18
out=$(call "$ai" history "{\"id\":\"$mid\"}")
events=$(echo "$out" | grep -o '"event":"[a-z_]*"' | sed 's/"event":"//; s/"$//' | tr '\n' ' ')
echo "events: $events"
[ "$events" = "restored trashed reverted updated created " ] || fail "history does not hold each change once, newest first: $out"
[ "$(echo "$out" | grep -o '"channel":"[a-z]*"' | sort -u)" = '"channel":"mcp"' ] || fail "a change is not recorded as the AI client's: $out"
[ "$(echo "$out" | grep -o '"person":"[a-z]*"' | sort -u)" = '"person":"alex"' ] || fail "a change is not recorded for Alex: $out"

step "a person in the app's Memories view captures a memory, and finds it in the explorer and at the terminal" 3 18
second="DEMO: the Foxglove Lane compost bins are turned on the first Saturday of each month."
capture="{\"op\":\"capture\",\"content\":\"$second\",\"tags\":[\"foxglove\"],\"project\":\"foxglove-lane\",\"key\":\"lifecycle-app-capture-01\"}"
made=$(memories "$capture")
[ "$(value "$made" available) $(value "$made" ok) $(value "$made" op)" = "true true capture" ] || fail "the app's capture: $made"
pid=$(value "$made" id)
echo "captured $pid"
[ "$(value "$made" status) $(value "$made" revision) $(value "$made" scope)" = "draft 1 shared" ] || fail "the app's capture: $made"
again=$(memories "$capture")
[ "$(value "$again" id) $(value "$again" replayed)" = "$pid true" ] || fail "a retried capture stored again: $again"
[ "$(value "$(memories "{\"op\":\"get\",\"id\":\"$pid\"}")" content)" = "$second" ] || fail "the app does not read the memory back"
# The explorer's combined search finds the stars on the map: the memory reaches the map at a rebuild.
rebuilt explores "Foxglove Lane compost" "Foxglove Lane compost bins" || fail "the app's explorer never found the memory: $(explore "Foxglove Lane compost")"
out=$(./rhythm recall --person alex Foxglove Lane compost 2>/dev/null) || fail "rhythm recall: $out"
echo "$out" | head -n 1 | grep -q "  $pid\$" || fail "the terminal's recall does not find the app's memory first: $out"

step "the person edits it, undoes the edit, forgets it and restores it in the app" 3 18
changed="DEMO: the Foxglove Lane compost bins are turned on the third Saturday of each month."
out=$(memories "{\"op\":\"update\",\"id\":\"$pid\",\"content\":\"$changed\",\"expected\":1}")
[ "$(value "$out" ok) $(value "$out" revision)" = "true 2" ] || fail "the app's update: $out"
out=$(memories "{\"op\":\"update\",\"id\":\"$pid\",\"content\":\"DEMO: a stale edit of the Foxglove Lane bins.\",\"expected\":1}")
[ "$(value "$out" code) $(value "$out" revision)" = "conflict 2" ] || fail "the app's update with a stale revision was not refused: $out"
[ "$(value "$(memories "{\"op\":\"get\",\"id\":\"$pid\"}")" content)" = "$changed" ] || fail "the app does not give the edit"
out=$(memories "{\"op\":\"undo\",\"id\":\"$pid\",\"expected\":2}")
[ "$(value "$out" ok) $(value "$out" to) $(value "$out" revision)" = "true 1 3" ] || fail "the app's undo: $out"
[ "$(value "$(memories "{\"op\":\"get\",\"id\":\"$pid\"}")" content)" = "$second" ] || fail "the app does not give the first words after the undo"
out=$(memories "{\"op\":\"forget\",\"id\":\"$pid\",\"expected\":3}")
[ "$(value "$out" ok) $(value "$out" days)" = "true 14" ] || fail "the app's forget: $out"
pnonce=$(value "$out" nonce)
[ "$(value "$(memories "{\"op\":\"get\",\"id\":\"$pid\"}")" code)" = unknown ] || fail "the app still gives the forgotten memory"
row=$(memories '{"op":"trash","limit":50}' | grep -o "{\"id\":\"$pid\"[^}]*}" | head -n 1)
# A person's channel: the app's trash offers deleting for good, with the row's nonce.
[ "$(value "$row" nonce) $(value "$row" mayDeleteForever)" = "$pnonce true" ] || fail "the app's trash row: $row"
out=$(memories "{\"op\":\"restore\",\"id\":\"$pid\",\"nonce\":\"$pnonce\"}")
[ "$(value "$out" ok) $(value "$out" op) $(value "$out" revision)" = "true restore 3" ] || fail "the app's restore: $out"
[ "$(value "$(memories "{\"op\":\"get\",\"id\":\"$pid\"}")" content)" = "$second" ] || fail "the app does not give the restored memory"
out=$(memories "{\"op\":\"history\",\"id\":\"$pid\"}")
events=$(echo "$out" | grep -o '"event":"[a-z_]*"' | sed 's/"event":"//; s/"$//' | tr '\n' ' ')
echo "events: $events"
[ "$events" = "restored trashed reverted updated created " ] || fail "the app's history does not hold each change once, newest first: $out"
[ "$(echo "$out" | grep -o '"channel":"[a-z]*"' | sort -u)" = '"channel":"app"' ] || fail "a change is not recorded as the app's: $out"

step "export: the memories as restored, with their earlier states; importing the file again adds nothing" 15 18
out=$(./rhythm export --out data/lifecycle/household.json 2>/dev/null) || fail "rhythm export: $out"
echo "$out" | head -n 2
echo "$out" | head -n 1 | grep -q ': 3 memories (0 in the trash, 0 held for review), 3 projects and 0 links\.$' ||
  fail "the export does not say it holds the three memories, out of the trash, and their three projects: $out"
file=$deploy/data/lifecycle/household.json
[ -s "$file" ] || fail "the export wrote no file"
head -n 1 "$file" | grep -q '^{"format":"rhythm-memories","version":1,' || fail "the export is not in Rhythm's own format"
for id in "$mid" "$pid"; do
  line=$(grep "^{\"id\": \"$id\"" "$file") || fail "the export does not hold $id"
  echo "$line" | grep -q '"trash": null' || fail "the export holds $id as in the trash"
  echo "$line" | grep -q '"revision": 3' || fail "the export does not hold $id at revision 3"
done
grep "^{\"id\": \"$mid\"" "$file" | grep -q "\"content\": \"$first\".*\"content\": \"$edited\"" ||
  fail "the export does not hold the AI client's memory as restored with its edit as an earlier state"
grep "^{\"id\": \"$pid\"" "$file" | grep -q "\"content\": \"$second\".*\"content\": \"$changed\"" ||
  fail "the export does not hold the person's memory as restored with its edit as an earlier state"
grep -q '"slug": "larkspur-row"' "$file" && grep -q '"slug": "foxglove-lane"' "$file" && grep -q '"slug": "hawthorn-close"' "$file" ||
  fail "the export does not hold the three projects"
grep -q "^{\"id\": \"$did\"" "$file" || fail "the export does not hold the unrelated memory"
out=$(./rhythm import data/lifecycle/household.json --family --dry-run 2>/dev/null) || fail "the import's dry run: $out"
echo "$out" | tail -n 3
echo "$out" | grep -q '^  memories: 3 already here (left as it is)$' || fail "importing the export again would add something: $out"

step "share: with memory scopes and memory.share on, an AI client shares one memory exactly with Family and takes it back" 2 18
# After the export, so its counts are the household's three. Memory scopes on as an operator turns them on (the first
# start with them on puts everyone in people into Family, docs/dev/memory.md section 6.3), and the share's own switch.
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e 's/^  scopes: off /  scopes: on  /' -e '/^memory:/a\  share: on' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not switch memory scopes and memory.share on in rhythm.yaml"
grep -q '^  scopes: on ' $deploy/rhythm.yaml && grep -A3 '^memory:' $deploy/rhythm.yaml | grep -q '^  share: on$' ||
  fail "rhythm.yaml does not say memory scopes and memory.share on"
./rhythm up >/dev/null 2>&1 || fail "up with memory scopes and memory.share on"
tries=0
until curl -s "http://127.0.0.1:$mcp/health" | grep -q '"embeddings":"on"'; do
  tries=$((tries + 1))
  [ "$tries" -lt 30 ] || fail "the engine did not load the model with memory scopes on"
  sleep 2
done
./rhythm token create life-sam --person sam >/dev/null 2>&1 || fail "token create for Sam"
sam=$secrets/harness_life-sam_token
mcp "$ai" '{"jsonrpc":"2.0","id":"l","method":"tools/list"}' | grep -q '"name":"share"' || fail "tools/list does not name share with memory.share on"
kept="DEMO: the Primrose Walk greenhouse key is kept under the blue pot."
made=$(call "$ai" remember "{\"content\":\"$kept\"}")
[ "$(value "$made" ok) $(value "$made" scope) $(value "$made" owner) $(value "$made" scopes) $(value "$made" revision)" = "true private alex on 1" ] ||
  fail "remember with memory scopes on did not keep the memory in Alex's private scope: $made"
sid=$(value "$made" id)
echo "remembered $sid"
[ "$(value "$(call "$sam" get "{\"id\":\"$sid\"}")" code)" = unknown ] || fail "Sam's client reads Alex's private memory"
call "$sam" recall '{"query":"Primrose Walk greenhouse key"}' | grep -q "$sid" && fail "Sam's client recalls Alex's private memory"
out=$(call "$ai" share "{\"id\":\"$sid\",\"expected_revision\":1}")
[ "$(value "$out" ok) $(value "$out" scope) $(value "$out" owner) $(value "$out" revision) $(value "$out" unchanged)" = "true shared household 2 false" ] ||
  fail "share: $out"
got=$(call "$sam" get "{\"id\":\"$sid\"}")
[ "$(value "$got" content)|$(value "$got" author)|$(value "$got" owner)" = "$kept|alex|household" ] ||
  fail "Sam's client does not read the shared memory with its author: $got"
call "$sam" recall '{"query":"Primrose Walk greenhouse key"}' | grep -q "$sid" || fail "Sam's client does not recall the shared memory"
out=$(call "$sam" history "{\"id\":\"$sid\"}")
events=$(echo "$out" | grep -o '"event":"[a-z_]*"' | sed 's/"event":"//; s/"$//' | tr '\n' ' ')
[ "$events $(value "$out" cut) $(value "$out" earlier_by)" = "shared  true alex" ] || fail "Sam's history does not start at the share: $out"
[ "$(value "$(call "$sam" share "{\"id\":\"$sid\",\"workspace\":\"private\"}")" code)" = forbidden ] ||
  fail "Sam's client, neither its author nor a Family admin, took Alex's memory back"
out=$(call "$ai" share "{\"id\":\"$sid\",\"expected_revision\":1}")
[ "$(value "$out" ok) $(value "$out" unchanged) $(value "$out" revision)" = "true true 2" ] || fail "the same share again changed something: $out"
out=$(call "$ai" share "{\"id\":\"$sid\",\"workspace\":\"private\",\"expected_revision\":2}")
[ "$(value "$out" ok) $(value "$out" scope) $(value "$out" owner) $(value "$out" revision)" = "true private alex 3" ] || fail "taking it back: $out"
[ "$(value "$(call "$sam" get "{\"id\":\"$sid\"}")" code)" = unknown ] || fail "Sam's client still reads the memory taken back"
call "$sam" recall '{"query":"Primrose Walk greenhouse key"}' | grep -q "$sid" && fail "Sam's client still recalls the memory taken back"
[ "$(value "$(call "$ai" get "{\"id\":\"$sid\"}")" content)" = "$kept" ] || fail "Alex's client does not read the memory taken back"
out=$(call "$ai" history "{\"id\":\"$sid\"}")
events=$(echo "$out" | grep -o '"event":"[a-z_]*"' | sed 's/"event":"//; s/"$//' | tr '\n' ' ')
echo "events: $events"
[ "$events" = "unshared shared created " ] || fail "Alex's history does not hold each move once, newest first: $out"
[ "$(echo "$out" | grep -o '"channel":"[a-z]*"' | sort -u)" = '"channel":"mcp"' ] || fail "a move is not recorded as the AI client's: $out"

step "down -v: the stack and its volumes removed"
./rhythm down -v >/dev/null 2>&1
echo
echo "cli/lifecycle.sh: all steps passed."
