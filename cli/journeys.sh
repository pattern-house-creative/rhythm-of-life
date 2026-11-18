#!/bin/sh
# cli/journeys.sh: a person's journeys end to end, in the app as a person uses it, against a throwaway Docker stack with
# fictional data only (SB-P6-04; stories 14 and 18; phase 6's exit: a person's journeys work without command-line
# workarounds and no core Rhythm regression).
#
#   this checkout's images -> init with the demo's fictional data, the sample notes and documents, a fictional glucose
#   plugin -> every memory setting off: nothing of the memory view, no memory tool, and the stars, dashboards, health views
#   and original links as they are -> memory.tools and engine.recall on, the synthetic parity corpus in: Family's memories
#   through rhythm import, its documents as an AI client saves them -> saving a memory and finding it, changing it and taking
#   it back, what is held read and released, the review queues (copies, contradictions, what to check, insights and pairs,
#   each count its list's length), from the brief to tasks, due dates and decisions, the trash, several at once, delete for
#   good and an AI client's group undone, projects, a capsule slot and the memory settings -> the stars, dashboards, health
#   views and original links again -> memory scopes and memory.share on, the corpus's private memories in: one memory shared
#   exactly and taken back, private ones to their people -> the stars and the rest again -> down -v, volumes included
#
# Each journey is a file of app/tests/journeys/ (sandbox.tsx says how): the real React app in jsdom, whose requests go to
# this stack's own app container; AI clients through /mcp with their own tokens, as a client calls. What jsdom cannot show
# is not shown: layout, the service worker, a PDF's pages on a canvas. A step that proves part of an acceptance story names
# it (step "<what>" <story> ...), and scripts/acceptance.mjs (SB-P6-08) runs this script after cli/lifecycle.sh and reads
# which steps passed.
#
# What still needs a terminal is said, so a run lists it: each journey's "terminal: rhythm <command>" line. Sharing is the
# one a person's journey in the app still needs it for (with viewer sessions off the app's login is the household view,
# docs/dev/memory.md 7.1); setting up (init, up, a token, a connector, a setting in rhythm.yaml) is an operator's.
#
#   RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files> \
#   COMPOSE_PROJECT_NAME=rhythm-journeys RHYTHM_VERSION=journeys RHYTHM_APP_PORT=18480 RHYTHM_MCP_PORT=18581 \
#   sh cli/journeys.sh
#
# The last four are the defaults. RHYTHM_MODEL_DIR has none: the folder must hold model_quantized.onnx and vocab.txt
# (docs/models.md), which the engine and the terminal read mounted read-only (cli/e2e.compose.yaml), so no model is
# downloaded (tests never download one). It needs the app's dependencies (npm --prefix app ci): vitest is never fetched. Its
# first step builds the engine and app images from this checkout under $RHYTHM_VERSION (with the build cache, never
# --pull), so the stack runs this checkout's code; those builds need the image and package registries for what the cache
# does not hold. It pulls no image and moves no tag of another image. It runs only in a checkout with no install (no
# deploy/compose/rhythm.yaml, .env or secrets/), in a Compose project with no containers or volumes yet, with an image tag
# no other container runs and no other Compose project built, and on ports nothing answers on, and removes what it made,
# the stack's volumes included; the images it builds stay, tagged $RHYTHM_VERSION.
set -eu
cd "$(dirname "$0")/.."
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME-rhythm-journeys}" RHYTHM_VERSION="${RHYTHM_VERSION:-journeys}" MSYS_NO_PATHCONV=1
app=${RHYTHM_APP_PORT:-18480}
mcp=${RHYTHM_MCP_PORT:-18581}
unset RHYTHM_APP_PORT RHYTHM_MCP_PORT # init writes them into .env
deploy=deploy/compose
secrets=$deploy/secrets
refuse() { echo "cli/journeys.sh: $*" >&2; exit 2; }
# Every Compose call names this run's project (-p), never one Compose would infer: with none exported and no .env it
# falls back to the stack's own name, rhythm, an install's, and the cleanup removes everything in its project (D-240).
case "$(printf %s "$COMPOSE_PROJECT_NAME" | tr 'A-Z' 'a-z')" in
  "" | rhythm) refuse "the Compose project '$COMPOSE_PROJECT_NAME' is empty or an install's default (rhythm): give the run its own (COMPOSE_PROJECT_NAME)." ;;
esac
if [ -e $deploy/rhythm.yaml ] || [ -e $deploy/.env ] || [ -e $secrets ]; then
  echo "An install exists in $deploy; cli/journeys.sh runs only in a checkout without one." >&2
  exit 2
fi
model=${RHYTHM_MODEL_DIR:-}
if [ -z "$model" ] || [ ! -f "$model/model_quantized.onnx" ] || [ ! -f "$model/vocab.txt" ]; then
  refuse "NOT RUN: set RHYTHM_MODEL_DIR to a folder holding the pinned MiniLM files (model_quantized.onnx, vocab.txt);" \
    "without them the engine's first start would download the model (D-041)."
fi
command -v curl >/dev/null 2>&1 || refuse "needs curl."
command -v node >/dev/null 2>&1 || refuse "needs node."
[ -e app/node_modules/.bin/vitest ] || refuse "needs the app's tests installed (npm --prefix app ci): vitest is never fetched."
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
# An AI client's one call with its token, the JSON-RPC body on standard input (a document's text is long for an argument).
mcp_call() {
  curl -s -X POST "http://127.0.0.1:$mcp/mcp" -H "Authorization: Bearer $(cat "$1")" -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' -H 'MCP-Protocol-Version: 2025-03-26' --data-binary @-
}
# journey <file>: one file of app/tests/journeys/ against this stack, every test in it run and passed. It prints what the
# journey needed a terminal for, and everything vitest says when one fails.
journey() {
  [ -f "app/tests/journeys/$1.test.tsx" ] || fail "no journey $1"
  said=$(cd app && NO_COLOR=1 DEBUG_PRINT_LIMIT=0 RHYTHM_JOURNEY_APP="http://127.0.0.1:$app" RHYTHM_JOURNEY_MCP="http://127.0.0.1:$mcp" \
    npx --no-install vitest run "tests/journeys/$1.test.tsx" --silent=false 2>&1) || { echo "$said"; fail "the journey $1"; }
  echo "$said" | grep -E '^terminal: rhythm|^ +Tests +' || true
  echo "$said" | grep -qE '^ +Tests +[0-9]+ passed' && ! echo "$said" | grep -qE '^ +Tests +.*skipped' ||
    { echo "$said"; fail "the journey $1 did not run every test"; }
}
# The map's stars, as the explorer reads them.
stars() { curl -s -X POST "http://127.0.0.1:$app/api/graph" -H 'Content-Type: application/json' -d '{"operation":"discover"}'; }
# The app's Refresh, asked again until the map's wait between rebuilds lets one in, until the map holds each title (four
# minutes at most). Under MSYS_NO_PATHCONV Git Bash's curl cannot write to /dev/null, so the shell discards each answer.
on_the_map() {
  tries=0
  while :; do
    out=$(stars) || out=""; missing=""
    for title in "$@"; do echo "$out" | grep -qF "$title" || missing="$missing [$title]"; done
    [ -z "$missing" ] && return 0
    tries=$((tries + 1))
    [ "$tries" -lt 120 ] || fail "the map never held$missing"
    curl -s -X POST "http://127.0.0.1:$app/api/graph-refresh" >/dev/null || true
    sleep 2
  done
}
model_on() {
  tries=0
  until curl -s "http://127.0.0.1:$mcp/health" | grep -q '"embeddings":"on"'; do
    tries=$((tries + 1))
    [ "$tries" -lt 30 ] || fail "the engine did not load the model from RHYTHM_MODEL_DIR: $(curl -s "http://127.0.0.1:$mcp/health")"
    sleep 2
  done
}
# A fictional reading from the plugin, then Home's next read, with no restart: the engine lists a person's glucose once a source
# has data its reader reads, and the app's server asks again for a listing without it at the next glucose read, at most every ten
# seconds (contracts/README.md 1.4), so one read past those ten seconds has the reading as the current one.
glucose_listed() {
  now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  printf '{"jsonrpc":"2.0","id":"g","method":"tools/call","params":{"name":"ingest_measurements","arguments":{"provider":"journey-cgm","subject":"alex","idempotencyKey":"journey:%s","source":{"externalId":"journey-%s","kind":"glucose-readings","title":"DEMO readings","fetchedAt":"%s","revision":"r-0","timezone":"Europe/London","device":{"id":"demo-sensor","model":"DEMO CGM"}},"measurements":[{"metric":"glucose","value":137,"unit":"mg/dL","observedAt":"%s","key":"%s","quality":"ok"}]}}}' \
    "$now" "$now" "$now" "$now" "$now" | mcp_call $secrets/plugin_journey-cgm_token | grep -q '"accepted":1' || fail "the plugin's reading was not accepted"
  sleep 11
  home=$(curl -s "http://127.0.0.1:$app/api/observatory")
  printf %s "$home" | node -e 'let s = ""; process.stdin.on("data", (d) => (s += d)).on("end", () => {
    let read; try { read = JSON.parse(s); } catch {} process.exit(read?.current?.value === 137 ? 0 : 1); })' ||
    fail "the plugin's reading is not on Home at its next read: $(printf %s "$home" | head -c 400)"
  echo "the plugin's reading is on Home at its next read"
}
# A folder of this checkout into the install's data folder, written from inside a container as the services' files are.
into_data() { tar -C "$1" -cf - . | dc --profile cli run --rm --no-deps -T --entrypoint sh cli -c "mkdir -p /work/data/$2 && tar -C /work/data/$2 -xf -" >/dev/null 2>&1; }
# held <scope> <the import's output>: the import held as many memories as the corpus marks held in that scope (their
# expect.held, by their words alone: the file marks none), and said so.
held() {
  n=$(node cli/journeys-corpus.mjs held "$1") || fail "could not count the corpus's held memories for $1"
  case $n in
    0) ! echo "$2" | grep -q 'held for review here' || fail "the import for $1 held memories the corpus does not mark held: $2" ;;
    1) echo "$2" | grep -qF '1 memory is held for review here' || fail "the corpus's injection for $1 was not held: $2" ;;
    *) echo "$2" | grep -qF "$n memories are held for review here" || fail "the corpus's $n injections for $1 were not held: $2" ;;
  esac
}
# The insight candidate pass's own pairs, after a restart: its line in the engine's log (in the start's daily turn when the
# model has loaded by then, or else at the first minute it is on, never a day later), then at least two pairs, as the
# app counts them for the review queues' journey.
pairs_found() {
  tries=0
  until dc logs engine 2>/dev/null | grep -qE 'Insight candidates: [0-9]+ memories reached, [0-9]+ candidates added'; do
    tries=$((tries + 1))
    [ "$tries" -lt 150 ] || fail "the insight candidate pass did not run after the engine's start: $(dc logs --tail 20 engine 2>&1)"
    sleep 2
  done
  dc logs engine 2>/dev/null | grep -oE 'Insight candidates: [0-9]+ memories reached, [0-9]+ candidates added' | tail -n 1
  found=$(curl -s -X POST "http://127.0.0.1:$app/api/memories" -H 'Content-Type: application/json' -d '{"op":"queues"}' |
    grep -oE '"candidates": *[0-9]+' | grep -oE '[0-9]+$')
  [ "${found:-0}" -ge 2 ] || fail "the insight candidate pass found fewer than two pairs: ${found:-none}"
  echo "pairs: $found waiting, found by the nightly pass"
}
# One of the corpus's files (cli/journeys-corpus.mjs) into the install's data folder.
corpus() {
  node cli/journeys-corpus.mjs "$1" | dc --profile cli run --rm --no-deps -T --entrypoint sh cli -c "mkdir -p /work/data/journeys && cat > /work/data/journeys/$1.json" >/dev/null 2>&1 ||
    fail "could not put the corpus's $1 file in the stack"
}

step "this checkout's images, then init with the demo's data, the sample folders and a fictional glucose plugin" 18
dc build engine app >/dev/null 2>&1 || fail "could not build the engine and app images from this checkout"
out=$(./rhythm init --no-input --starter no --people "Alex, Sam" --timezone Europe/London --demo yes --app-port "$app" --mcp-port "$mcp" 2>&1) || fail "init: $out"
echo "$out" | tail -n 2
into_data demo/import import || fail "could not put the sample documents and readings in the stack"
into_data demo/notes notes || fail "could not put the sample notes in the stack"
./rhythm connect notes --no-input --set owner=alex >/dev/null 2>&1 || fail "connect notes"
# Written from inside a container, as the services' own file: a plugin that may write glucose for Alex, as carelink's entry is.
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e 's/^plugins: {}.*$/plugins:\n  journey-cgm:\n    enabled: true\n    providers: [journey-cgm]\n    subjects: [alex]\n    policy: live\n    domain: health/' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not name the plugin in rhythm.yaml"
grep -q '^  journey-cgm:$' $deploy/rhythm.yaml || fail "rhythm.yaml does not name the plugin"
out=$(./rhythm up 2>&1) || fail "up: $out"
echo "$out" | tail -n 3 | head -n 2
curl -s "http://127.0.0.1:$app/api/instance" | grep -q '"mode":"live"' || fail "the app is not in live mode"
# The built page and what installs it, as a browser asks for them: the journeys below draw the app in jsdom, from its source.
curl -sf "http://127.0.0.1:$app/" | grep -q '<div id="root">' || fail "the app does not serve its page"
for file in sw.js manifest.webmanifest; do
  curl -sf "http://127.0.0.1:$app/$file" >/dev/null || fail "the app does not serve $file"
done
model_on
./rhythm token create journey-alex --person alex >/dev/null 2>&1 || fail "token create for Alex"
./rhythm token create journey-sam --person sam >/dev/null 2>&1 || fail "token create for Sam"
./rhythm token create --role ingest --plugin journey-cgm >/dev/null 2>&1 || fail "token create for the plugin"
# The plugin's first reading, on Home at its next read with no restart (glucose_listed; contracts/README.md 1.4).
glucose_listed
on_the_map "DEMO · Bike commute log" "DEMO Boiler manual" "Alex · Activity"
curl -s "http://127.0.0.1:$app/api/dashboards" | grep -qF "DEMO · Week at a glance" || fail "the demo's dashboards are not there"
echo "the map holds the sample notes, documents and readings, and the demo's dashboards"

step "every memory setting off: nothing of the memory view, and no memory tool" 18
journey off

step "every memory setting off: stars, dashboards, health views and original links, at a phone's and a desktop's size" 14 18
journey core

step "memory.tools and engine.recall on, Family's memories and documents from the synthetic corpus in" 18
# The per-token write limit (engine.writeLimit) on with them, as in cli/e2e.sh and cli/lifecycle.sh, so a person's journeys
# and their AI clients' writes go through it from here on (WriteLimitTests counts them; docs/security.md, the write limit).
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e '/^memory:/a\  tools: on' -e '/^engine:/a\  recall: on' -e '/^engine:/a\  writeLimit: on' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not switch memory.tools and engine.recall on in rhythm.yaml"
grep -q '^  writeLimit: on$' $deploy/rhythm.yaml || fail "rhythm.yaml does not say engine.writeLimit on"
./rhythm up >/dev/null 2>&1 || fail "up with memory.tools and engine.recall on"
model_on
corpus family
out=$(./rhythm import data/journeys/family.json --family 2>/dev/null) || fail "the corpus's import: $out"
echo "$out" | grep -E '^  (memories|projects|links|commitments|decisions|due dates):|held for review'
echo "$out" | grep -q '^  memories: 39 added$' || fail "the corpus's Family memories did not come in: $out"
held family "$out"
# Its documents as an AI client saves its copies, one call each.
node cli/journeys-corpus.mjs documents | {
  saved=0
  while IFS= read -r line; do
    answer=$(printf '{"jsonrpc":"2.0","id":"d","method":"tools/call","params":{"name":"save_document","arguments":%s}}' "$line" | mcp_call $secrets/harness_journey-alex_token)
    case "$answer" in *saved*true*) ;; *) fail "save_document refused a corpus document: $answer" ;; esac
    saved=$((saved + 1))
  done
  echo "documents: $saved saved by an AI client"
}
# The daily turn runs at the engine's start, so a restart runs the staleness pass over what the import brought, and the
# insight candidate pass, whose pairs the review queues' journey decides: the pass's own, nothing written for it.
dc restart engine >/dev/null 2>&1 || fail "could not restart the engine"
model_on
pairs_found

step "saving a memory and finding it again, by its tag, in the search and on the map, at both sizes" 3 4 18
journey save-and-find

step "changing a memory and taking it back: edit, update, confirm, undo, history, a stale edit refused" 3 18
journey edit-and-undo

step "pasted instructions held, read whole and released by a person" 8 18
journey review-held

step "the review queues: copies, contradictions, what to check, insights and pairs, each count its list's length" 9 11 12 18
journey review-queues

step "from the brief: tasks, due dates and decisions, a person's word on each, with its Undo" 12 18
journey plans

step "the trash and undo: forgotten, restored, several at once, deleted for good, an AI client's group undone, at both sizes" 3 18
journey trash-and-undo

step "projects, a capsule slot and the memory settings" 10 18
journey projects-and-settings

step "with memory.tools on, stars, dashboards, health views and original links as before" 14 18
journey core

step "memory scopes and memory.share on, as an operator turns them on, the corpus's private memories in" 1 2
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e 's/^  scopes: off /  scopes: on  /' -e '/^memory:/a\  share: on' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not switch memory scopes and memory.share on in rhythm.yaml"
./rhythm up >/dev/null 2>&1 || fail "up with memory scopes and memory.share on"
model_on
for who in alex sam; do
  corpus "$who"
  out=$(./rhythm import "data/journeys/$who.json" --person "$who" 2>/dev/null) || fail "the corpus's import for $who: $out"
  echo "$out" | grep -q '^  memories: 26 added$' || fail "the corpus's private memories for $who did not come in: $out"
  held "$who" "$out"
done

step "sharing one memory exactly, at the terminal, and private ones kept to their people in the app and every AI client" 1 2
journey share

step "with memory scopes on and Alex's sources shared with Family at the terminal, stars, dashboards, health views and original links as before" 2 14
# Each is a person's own until they share it, and sharing a source has no way in the app (docs/dev/memory.md section 4).
for source in notes csv journey-cgm; do
  echo "terminal: rhythm share add alex $source"
  ./rhythm share add alex "$source" 2>/dev/null | grep -q "^Shared alex's $source records" || fail "share add alex $source"
done
glucose_listed
on_the_map "DEMO · Bike commute log" "Alex · Activity"
journey core

step "down -v: this stack and its volumes gone"
./rhythm down -v >/dev/null 2>&1
echo
echo "cli/journeys.sh: all steps passed."
