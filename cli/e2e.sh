#!/bin/sh
# cli/e2e.sh: the rhythm commands end to end in a throwaway Docker stack, with fictional data only.
#
#   bare compose up fails clearly -> init (no input) -> up -> token create -> MCP tools/list with that token ->
#   connect notes -> doctor, the local model on and nothing downloaded -> backup -> a document written after the
#   backup -> restore -> the backed-up state is back and old tokens are refused -> upgrade (same version) -> upgrade to
#   a tag that cannot be pulled, which rolls back -> a token for a person, refused while they are suspended ->
#   token revoke --all refuses every old token -> a Family admin and a source share recorded, family remove refused
#   with scopes off -> memory scopes on: story 1, a copy Alex's client saves for herself is read by no one else, and
#   story 2, one she saves for Family is read by Sam and the household view, and her notes by Sam and the app's map
#   only while she shares them -> rhythm import of two synthetic exports of another install, each into the scopes it
#   names (a dry run first, a repeat adding nothing) -> rhythm export as Alex gives back her records byte for byte, and
#   no reader's export holds another person's private records -> from the terminal, Alex remembers a memory for herself,
#   lists it and recalls it with the local model on, through her terminal login, and Sam neither lists nor recalls it
#   -> memory.tools and memory.share on: Alex confirms it (set-status) and puts it into her own capsule, which the
#   capsule view serves to her and never to Sam; a memory with hidden characters is held, read whole and released at
#   the revision read (release); shared with Family, which Sam lists, and taken back (share-memory), which Sam may not;
#   forgotten into the trash (forget) and deleted for good once its id is typed (delete-forever) -> every setting on, a
#   capture connection made and one disconnected -> upgrade to a new tag, and a failed upgrade rolled back, with every
#   setting on -> down -v
#
# Each upgrade step checks what an upgrade must leave as it is (SB-P7-11, story 17): rhythm.yaml, .env but for its tag,
# every secret and token file with its owner and mode, the ports and services running, the tools a token is offered, the
# commands the terminal offers, the connectors off, a revoked token refused; and that no secret's value is in anything
# the upgrade said, the stack's logs or its containers' settings.
#
# A step that proves part of an acceptance story names it (step "<what>" <story> ...), and its line in the output says
# so: scripts/acceptance.mjs (SB-P6-08) runs this script as the stories' sandbox stack and reads which steps passed.
#
#   RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files> \
#   COMPOSE_PROJECT_NAME=rhythm-e2e RHYTHM_VERSION=e2e RHYTHM_APP_PORT=18080 RHYTHM_MCP_PORT=18181 sh cli/e2e.sh
#
# The last four are the defaults. RHYTHM_MODEL_DIR has none: the folder must hold model_quantized.onnx and vocab.txt
# (docs/models.md), which the engine reads mounted read-only (cli/e2e.compose.yaml), so no model is downloaded (tests
# never download one); its image builds and upgrade steps still need the image and package registries. It runs only in
# a checkout with no install (no deploy/compose/rhythm.yaml, .env or secrets/), in a Compose project with no
# containers or volumes yet, with an image tag no other container runs and no other Compose project built (an install
# whose stack is down runs no container, but its next up would run what this run built), on ports nothing answers on,
# and removes what it made, the stack's volumes included. The images it builds stay, tagged $RHYTHM_VERSION and
# $RHYTHM_VERSION-previous, and for its last upgrade, to a new tag, $RHYTHM_VERSION-next and $RHYTHM_VERSION-next-previous.
# Its upgrade steps build with --pull and pull the stack's other images, as ./rhythm upgrade
# does, so a base or Postgres image tag on this machine may move to upstream's newest build of it: those third-party
# tags, which other stacks and engine/test-db.sh share, are recorded by image id before the run and put back on those
# images when it ends (D-133).
set -eu
cd "$(dirname "$0")/.."
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME-rhythm-e2e}" RHYTHM_VERSION="${RHYTHM_VERSION:-e2e}" MSYS_NO_PATHCONV=1
app=${RHYTHM_APP_PORT:-18080}
mcp=${RHYTHM_MCP_PORT:-18181}
unset RHYTHM_APP_PORT RHYTHM_MCP_PORT # init writes them into .env
deploy=deploy/compose
secrets=$deploy/secrets
refuse() { echo "cli/e2e.sh: $*" >&2; exit 2; }
# Every Compose call names this run's project (-p), never one Compose would infer: with none exported and no .env it
# falls back to the stack's own name, rhythm, an install's, and the cleanup removes everything in its project (D-240).
case "$(printf %s "$COMPOSE_PROJECT_NAME" | tr 'A-Z' 'a-z')" in
  "" | rhythm) refuse "the Compose project '$COMPOSE_PROJECT_NAME' is empty or an install's default (rhythm): give the run its own (COMPOSE_PROJECT_NAME)." ;;
esac
if [ -e $deploy/rhythm.yaml ] || [ -e $deploy/.env ] || [ -e $secrets ]; then
  echo "An install exists in $deploy; cli/e2e.sh runs only in a checkout without one." >&2
  exit 2
fi
model=${RHYTHM_MODEL_DIR:-}
if [ -z "$model" ] || [ ! -f "$model/model_quantized.onnx" ] || [ ! -f "$model/vocab.txt" ]; then
  refuse "NOT RUN: set RHYTHM_MODEL_DIR to a folder holding the pinned MiniLM files (model_quantized.onnx, vocab.txt);" \
    "without them the engine's first start would download the model (D-041)."
fi
command -v curl >/dev/null 2>&1 || refuse "needs curl."
# Its cleanup removes everything in its project, volumes included, and the upgrade steps rebuild and retag its images:
# so a project of its own, a tag no other stack runs, and ports nothing else answers on.
label="label=com.docker.compose.project=$COMPOSE_PROJECT_NAME"
made=$(docker ps -aq --filter "$label") && volumes=$(docker volume ls -q --filter "$label") || refuse "Docker does not answer."
[ -z "$made$volumes" ] ||
  refuse "the Compose project $COMPOSE_PROJECT_NAME already has containers or volumes: give the run its own (COMPOSE_PROJECT_NAME)."
for image in rhythm-engine rhythm-app rhythm-backup; do
  for tag in "$RHYTHM_VERSION" "$RHYTHM_VERSION-previous" "$RHYTHM_VERSION-next" "$RHYTHM_VERSION-next-previous"; do
    [ -z "$(docker ps -aq --filter "ancestor=$image:$tag" 2>/dev/null)" ] ||
      refuse "another stack's containers run $image:$tag, which the run rebuilds and retags: give it its own tag (RHYTHM_VERSION)."
    # An image under the tag that another Compose project built (Compose labels what it builds with its project), or that
    # no Compose project did, belongs to someone else, whose stack may only be down.
    if built=$(docker image inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$image:$tag" 2>/dev/null); then
      [ "$built" = "$COMPOSE_PROJECT_NAME" ] ||
        refuse "$image:$tag is already here, built by ${built:-no Compose project}, not by $COMPOSE_PROJECT_NAME: give the run its own tag (RHYTHM_VERSION)."
    fi
  done
done
for port in "$app" "$mcp"; do
  answered=0
  curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/" || answered=$?
  [ "$answered" = 7 ] || refuse "something already answers on 127.0.0.1:$port: choose free ports (RHYTHM_APP_PORT, RHYTHM_MCP_PORT)."
done
# The engine reads the model from the host folder (cli/e2e.compose.yaml). Docker on Windows takes C:/ paths, not Git
# Bash's /c/ ones, and the wrappers run Compose in deploy/compose, where these file names resolve.
RHYTHM_E2E_MODEL_DIR=$(cd "$model" && { pwd -W 2>/dev/null || pwd; })
export RHYTHM_E2E_MODEL_DIR COMPOSE_PATH_SEPARATOR=: COMPOSE_FILE=compose.yaml:../../cli/e2e.compose.yaml
# The third-party images the stack names: the Postgres and Ollama images of its services (the variable, else the
# compose file's default) and the base images it builds from. Each one this machine has now is kept as ref=id.
compose_default() { sed -n "s/.*\${$1:-\([^}]*\)}.*/\1/p" $deploy/engine.compose.yaml 2>/dev/null | head -n 1; }
pinned=""
for ref in "${RHYTHM_POSTGRES_IMAGE:-$(compose_default RHYTHM_POSTGRES_IMAGE)}" "${RHYTHM_OLLAMA_IMAGE:-$(compose_default RHYTHM_OLLAMA_IMAGE)}" \
  $(sed -n 's/^FROM \([^ $]*\).*/\1/p' engine/Dockerfile engine/backup/Dockerfile 2>/dev/null) \
  $(sed -n 's/^ARG NODE_IMAGE=\([^ ]*\).*/\1/p' app/Dockerfile 2>/dev/null); do
  [ -n "$ref" ] || continue
  case " $pinned " in *" $ref="*) continue ;; esac
  id=$(docker image inspect -f '{{.Id}}' "$ref" 2>/dev/null) || continue
  if [ -n "$id" ]; then pinned="$pinned $ref=$id"; fi
done

dc() { (cd $deploy && docker compose -p "$COMPOSE_PROJECT_NAME" "$@"); }
# This run's own folder: what an upgrade must leave as it is, and what the upgrades said, which the cleanup removes.
kept=$(mktemp -d)
cleanup() {
  rm -rf "$kept"
  # The files belong to the services' users, so they are removed from inside a container.
  dc --profile cli run --rm --no-deps --entrypoint rm cli -rf /work/rhythm.yaml /work/.env /work/secrets /work/data >/dev/null 2>&1 || true
  dc --profile cli --profile backup --profile restore down -v --remove-orphans >/dev/null 2>&1 || true
  # A third-party tag a pull moved goes back on the image it named before the run.
  for pin in $pinned; do
    ref=${pin%%=*} id=${pin#*=}
    if [ "$(docker image inspect -f '{{.Id}}' "$ref" 2>/dev/null || true)" = "$id" ]; then continue; fi
    if docker tag "$id" "$ref" >/dev/null 2>&1; then echo "cli/e2e.sh: $ref had moved; tagged back to the image it named before the run." >&2
    else echo "cli/e2e.sh: could not tag $ref back to $id: do it by hand (docker tag $id $ref)." >&2; fi
  done
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
code() { curl -s -o /dev/null -w '%{http_code}' -X POST "http://127.0.0.1:$mcp/mcp" -H "Authorization: Bearer $(cat "$1")" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":"c","method":"tools/list"}'; }
save() { # <token file> <id> <text> [<subject>]
  mcp "$1" "{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"method\":\"tools/call\",\"params\":{\"name\":\"save_document\",\"arguments\":{\"fileId\":\"$2\",\"title\":\"$2\",\"content\":\"$3\",\"modifiedAt\":\"2026-10-28T10:00:00Z\",\"semanticIndex\":false${4:+,\"subject\":\"$4\"}}}}"
}
search() { mcp "$1" "{\"jsonrpc\":\"2.0\",\"id\":\"q\",\"method\":\"tools/call\",\"params\":{\"name\":\"search_knowledge\",\"arguments\":{\"query\":\"$2\"}}}"; }
# Whether search_knowledge says it answers from the combined list (engine.searchKnowledge: combined): <token file>.
combined() { mcp "$1" '{"jsonrpc":"2.0","id":"o","method":"tools/list"}' | grep -q 'Ranked by words and meaning together'; }
resource() { mcp "$1" "{\"jsonrpc\":\"2.0\",\"id\":\"r\",\"method\":\"tools/call\",\"params\":{\"name\":\"get_resource\",\"arguments\":{\"resourceId\":\"$2\"}}}"; }
# The app's map as its household view reads it (no viewer session): the stars whose titles hold the words.
stars() { curl -s -X POST "http://127.0.0.1:$app/api/graph" -H 'Content-Type: application/json' -d "{\"operation\":\"search\",\"value\":\"$1\"}"; }
shows() { stars "$1" | grep -q "$2"; } # <words> <title>: the app's map has a star with that title for the words
finds() { search "$1" "$2" | grep -q "$3"; } # <token file> <words> <title>: that login's search finds the title
# rebuilt <check> [<argument> ...]: the app's Refresh, asked again until the map's wait between rebuilds lets one in,
# until the check passes (four minutes at most): a note or a copy reaches the map and search at a rebuild. The shell
# discards each answer: under MSYS_NO_PATHCONV Git Bash's curl, a Windows program, gets /dev/null unchanged and cannot
# write to it.
rebuilt() {
  tries=0
  until "$@"; do
    tries=$((tries + 1))
    [ "$tries" -lt 120 ] || return 1
    curl -s -X POST "http://127.0.0.1:$app/api/graph-refresh" >/dev/null || return 1
    sleep 2
  done
}
# What an upgrade must leave as it is (SB-P7-11). The install's files are read inside a container, since a secret's file
# may be its service's alone, and only as digests, owners and modes, never a value.
files() {
  dc --profile cli run --rm --no-deps -T --entrypoint sh cli -c 'ls -A; sha256sum rhythm.yaml; grep -v "^RHYTHM_VERSION=" .env | sha256sum
    { echo rhythm.yaml; echo .env; find secrets; } | LC_ALL=C sort | xargs stat -c "%a %u:%g %F %n"
    find secrets -type f | LC_ALL=C sort | xargs sha256sum' 2>/dev/null
}
# Each running service with what it publishes.
listening() { docker ps --filter "$label" --format '{{.Label "com.docker.compose.service"}} {{.Ports}}' | LC_ALL=C sort; }
# The tools a token is offered, each name once.
offered() { mcp "$1" '{"jsonrpc":"2.0","id":"o","method":"tools/list"}' | grep -o '"name":"[a-z_]*"' | LC_ALL=C sort -u; }
capture() { # <token file> <text>: the capture route's status for one memory
  curl -s -o /dev/null -w '%{http_code}' -X POST "http://127.0.0.1:$mcp/capture" -H "Authorization: Bearer $(cat "$1")" \
    -H 'Content-Type: application/json' -d "{\"text\":\"$2\",\"source\":\"shortcut\"}"
}
# The app's viewer route, off in every install: the status it answers an opening with.
viewer() { curl -s -o /dev/null -w '%{http_code}' -X POST "http://127.0.0.1:$app/api/viewer" -H 'Content-Type: application/json' -d '{"code":"DEMO"}'; }
# All of it, into a file; and the engine and the app must be reading the rhythm.yaml that is there.
state() { # <file> <token file>
  files > "$1"
  yaml=$(dc --profile cli run --rm --no-deps -T --entrypoint sh cli -c 'sha256sum < rhythm.yaml' 2>/dev/null)
  [ -n "$yaml" ] || fail "could not read rhythm.yaml's digest"
  for service in engine app; do
    [ "$(dc exec -T "$service" sh -c 'sha256sum < /etc/rhythm/rhythm.yaml')" = "$yaml" ] || fail "$service does not read the rhythm.yaml that is there"
  done
  { listening
    offered "$2"
    ./rhythm help 2>/dev/null
    ./rhythm token list 2>/dev/null
    ./rhythm doctor 2>/dev/null | grep -E '^ +[a-zA-Z]+ +adapter ' || true
    # A harness token is refused at the capture route, on or off, so this saves nothing.
    echo "capture, viewer: $(capture "$2" "DEMO: never saved.") $(viewer)"
  } >> "$1"
}
# Searches what is on its input for the value of each secret and token file, inside a container as above, and names the
# files it found (engine_db's password by itself too, which is what a log would hold), and how many it searched for.
leaks() {
  dc --profile cli run --rm --no-deps -T --entrypoint sh cli -c 'cat > /tmp/said
    for f in $(find secrets -type f); do
      v=$(cat "$f"); p=${v##*Password=}; p=${p%%;*}
      for s in "$v" "$p"; do if [ -n "$s" ] && grep -qF -- "$s" /tmp/said; then echo "$f"; fi; done
    done | LC_ALL=C sort -u; echo "searched for the secrets in $(find secrets -type f | wc -l) files"' 2>/dev/null
}
# as_it_was <what> <state file> <token file> [<what it said> ...]: the install is as that file holds it, and no secret is
# in what the upgrade said, the stack's logs or its containers' settings.
as_it_was() {
  what=$1 was=$2 token=$3
  shift 3
  state "$kept/now" "$token"
  if ! cmp -s "$was" "$kept/now"; then diff "$was" "$kept/now" | head -n 30; fail "$what changed what it must leave as it is"; fi
  ./rhythm logs --no-color > "$kept/logs" 2>&1 || fail "could not read the stack's logs"
  docker inspect $(docker ps -aq --filter "$label") > "$kept/containers" || fail "could not read the containers' settings"
  # A search that cannot see a secret would pass: it must find this token in a copy of itself first.
  leaks < "$token" | grep -q "^secrets/$(basename "$token")$" || fail "the search for secrets does not find one"
  found=$(cat "$@" "$kept/logs" "$kept/containers" | leaks)
  echo "$found" | grep -q '^searched for the secrets in [1-9]' || fail "could not search for secrets after $what"
  if echo "$found" | grep -q '^secrets/'; then echo "$found"; fail "a secret's value reached a log during $what"; fi
  [ "$(listening | grep -o '^[a-z]* 127\.0\.0\.1:[0-9]*->8080/tcp')" = "$(printf 'app 127.0.0.1:%s->8080/tcp\nengine 127.0.0.1:%s->8080/tcp' "$app" "$mcp")" ] &&
    ! listening | grep -Eq '0\.0\.0\.0|\[::\]|:::' || fail "the stack publishes more than the app and the engine, on this computer, after $what: $(listening)"
  echo "After $what: $(wc -l < "$was" | tr -d ' ') lines as they were, $(tail -n 1 "$was"); $(echo "$found" | tail -n 1), none in $(cat "$@" "$kept/logs" "$kept/containers" | wc -c | tr -d ' ') bytes."
}
# The upgrade's own words, kept to be searched, and its last line on stdout, as ./rhythm upgrade says it.
upgrade() { # <its name for the files> [<argument> ...]
  name=$1
  shift
  ./rhythm upgrade "$@" > "$kept/$name.out" 2> "$kept/$name.err"
}

step "bare compose up before init fails with one clear line"
dc up -d postgres >/dev/null 2>&1 || true
docker wait "$(dc ps -aq postgres)" >/dev/null
dc logs postgres 2>&1 | grep -q "run ./rhythm init first" || fail "no clear message from postgres"
dc down >/dev/null 2>&1

step "init, no input" 18
out=$(./rhythm init --no-input --people "Alex, Sam" --timezone Europe/London --app-port "$app" --mcp-port "$mcp")
echo "$out" | tail -n 4
echo "$out" | grep -q "Replaced 1 empty folders" || fail "init did not replace the folder Docker made"
for f in postgres_password engine_db app_token backup_password; do [ -s $secrets/$f ] || fail "no $f"; done
grep -q '^RHYTHM_CONFIG_FILE=./rhythm.yaml' $deploy/.env || fail ".env"
grep -q "^COMPOSE_PROJECT_NAME=$COMPOSE_PROJECT_NAME$" $deploy/.env && grep -q "^RHYTHM_VERSION=$RHYTHM_VERSION$" $deploy/.env ||
  fail ".env did not record the project and the image tag"
./rhythm init --no-input --people Alex >/dev/null 2>&1 && fail "a second init should refuse"

step "up" 17 18
out=$(./rhythm up 2>/dev/null)
echo "$out" | tail -n 5
echo "$out" | grep -q "Harness token file: $secrets/harness_assistant_token" || fail "up did not name the token file"
echo "$out" | grep -qF "$(cat $secrets/harness_assistant_token)" && fail "up printed a token"
curl -s "http://127.0.0.1:$app/api/instance" | grep -q '"mode":"live"' || fail "the app is not in live mode"

step "token create, then MCP tools/list with it" 18
./rhythm token create e2e-client 2>/dev/null
mcp $secrets/harness_e2e-client_token '{"jsonrpc":"2.0","id":"1","method":"tools/list"}' | grep -q '"save_document"' || fail "tools/list"

step "connect notes" 18
./rhythm connect notes --no-input --set owner=alex 2>/dev/null | tail -n 1
grep -A1 '^  notes:' $deploy/rhythm.yaml | grep -q 'enabled: true' || fail "notes not on"

step "doctor, the local model on and nothing downloaded" 18
# The engine loads the model in the background after it starts: up to a minute for it to say on.
tries=0
until curl -s "http://127.0.0.1:$mcp/health" | grep -q '"embeddings":"on"'; do
  tries=$((tries + 1))
  [ "$tries" -lt 30 ] || fail "the engine did not load the model from RHYTHM_MODEL_DIR: $(curl -s "http://127.0.0.1:$mcp/health")"
  sleep 2
done
out=$(./rhythm doctor 2>/dev/null) || { echo "$out"; fail "doctor found problems"; }
echo "$out" | tail -n 4
echo "$out" | grep -q 'ok    embeddings model minilm (live) is on' || { echo "$out"; fail "doctor does not say the model is on"; }
dc exec -T engine sh -c '[ ! -e /var/lib/rhythm/models ]' || fail "the engine downloaded a model into its data folder"

step "backup, then a document written after it" 15 18
save $secrets/harness_e2e-client_token e2e-before-backup "Plant marigolds beside the tomatoes." | grep -q 'saved' || fail "save"
./rhythm backup 2>/dev/null | grep -q '"snapshot"' || fail "backup"
save $secrets/harness_e2e-client_token e2e-after-backup "Sunflowers along the fence." | grep -q 'saved' || fail "save"

step "restore latest" 15 18
./rhythm restore latest 2>/dev/null | tail -n 3
[ "$(code $secrets/harness_e2e-client_token)" = 401 ] || fail "an old token still works after the restore"
./rhythm token create e2e-restored 2>/dev/null
search $secrets/harness_e2e-restored_token marigolds | grep -q 'e2e-before-backup' || fail "the backed-up document is not back"
search $secrets/harness_e2e-restored_token sunflowers | grep -q 'e2e-after-backup' && fail "a document from after the backup survived"

step "before an upgrade: a connector turned off with its secret kept, a token revoked, every setting off by default" 17
out=$(./rhythm disconnect csv 2>/dev/null) || fail "disconnect csv: $out"
echo "$out" | grep -q '^csv is off' || fail "disconnect csv: $out"
grep -A1 '^  csv:' $deploy/rhythm.yaml | grep -q 'enabled: false' || fail "csv is not off in rhythm.yaml"
# A connector that is off keeps its secret file, fictional here; rhythm up gives it its owner.
dc --profile cli run --rm --no-deps --entrypoint sh cli -c 'head -c 24 /dev/urandom | od -An -tx1 | tr -d " \n" > secrets/adapters/nightscout_token' \
  >/dev/null 2>&1 || fail "could not write a connector's secret"
./rhythm up >/dev/null 2>&1 || fail "up with a connector off"
./rhythm token create e2e-revoked 2>/dev/null
./rhythm token revoke e2e-revoked 2>/dev/null | grep -q '^Revoked 1 ' || fail "token revoke"
restored=$secrets/harness_e2e-restored_token
# No setting off by default answers: no tool, route or command of theirs, the viewer route refused, the connector gone
# from doctor's list, and nothing published but the app and the engine on this computer.
off=$(offered $restored)
echo "$off" | grep -q '"search_knowledge"' || fail "tools/list did not answer"
echo "$off" | grep -Eq '"(remember|recall|get_prompt_capsule|share|list_stale)"' && fail "a tool that is off by default is offered: $off"
out=$(./rhythm help 2>/dev/null)
echo "$out" | grep -q '^  doctor' || fail "help did not answer"
echo "$out" | grep -Eq '^  (capture|import-second-brain|night|release) ' && fail "a command that is off by default is offered"
[ "$(capture $restored "DEMO: a capture no install takes while it is off.")" = 404 ] || fail "the capture route answers while it is off"
[ "$(viewer)" = 404 ] || fail "the viewer route answers while it is off: $(viewer)"
./rhythm doctor 2>/dev/null | grep -q ' adapter csv ' && fail "doctor lists the connector that is off"
listening
state "$kept/before" $restored

step "upgrade, same version" 17
upgrade same || { tail -n 20 "$kept/same.err" >&2; fail "upgrade"; }
tail -n 1 "$kept/same.out" | grep -q "Upgraded to $RHYTHM_VERSION" || fail "upgrade"
[ "$(code $restored)" = 200 ] || fail "MCP after the upgrade"
as_it_was "the upgrade" "$kept/before" $restored "$kept/same.out" "$kept/same.err"
[ "$(code $secrets/harness_e2e-client_token)" = 401 ] || fail "a token a restore refused works after the upgrade"
[ "$(code $secrets/harness_e2e-revoked_token)" = 401 ] || fail "a revoked token works after the upgrade"

step "upgrade to a tag that cannot be pulled rolls back" 17
if ./rhythm upgrade e2e-missing-tag --pull > "$kept/failed.out" 2> "$kept/failed.err"; then fail "the upgrade should have failed"; fi
[ "$(code $restored)" = 200 ] || fail "MCP after the rollback"
grep -q '^RHYTHM_VERSION=e2e-missing-tag' $deploy/.env && fail "the failed tag was kept"
as_it_was "the rollback" "$kept/before" $restored "$kept/failed.out" "$kept/failed.err"
[ "$(code $secrets/harness_e2e-revoked_token)" = 401 ] || fail "a revoked token works after the rollback"

step "a token for a person, refused while they are suspended; revoke --all refuses every old token" 1
tab=$(printf '\t')
./rhythm token create e2e-alex --person alex 2>/dev/null
./rhythm token list 2>/dev/null | grep -q "^e2e-alex${tab}harness${tab}alex${tab}" || fail "token list does not name the person"
[ "$(code $secrets/harness_e2e-alex_token)" = 200 ] || fail "a person's token"
./rhythm person suspend alex >/dev/null 2>&1
[ "$(code $secrets/harness_e2e-alex_token)" = 401 ] || fail "a suspended person's token still works"
./rhythm person unsuspend alex >/dev/null 2>&1
./rhythm token revoke --all 2>/dev/null | grep -q '^Revoked' || fail "revoke --all"
[ "$(code $secrets/harness_e2e-restored_token)" = 401 ] || fail "a token still works after revoke --all"

step "Family's admin and a source share recorded on the host; scopes off says they are off already" 2 17
./rhythm family admin alex 2>/dev/null | grep -q '^alex is a Family admin' || fail "family admin"
./rhythm person list 2>/dev/null | grep -q "^alex${tab}current${tab}Family admin${tab}" || fail "person list does not show the admin"
./rhythm share add alex nightscout 2>/dev/null | grep -q "^Shared alex's nightscout records" || fail "share add"
./rhythm share list 2>/dev/null | grep -q "^alex${tab}nightscout${tab}operator${tab}" || fail "share list"
./rhythm share revoke alex nightscout 2>/dev/null | grep -q '^Ended the share' || fail "share revoke"
if ./rhythm family remove sam >/dev/null 2>&1; then fail "family remove should be refused while scopes are off"; fi
./rhythm scopes off 2>/dev/null </dev/null | grep -q 'already off' || fail "scopes off"

step "memory scopes on: Alex's client saves a copy for herself and one for Family" 1 2
# rhythm.yaml belongs to the services' users, so it is changed from inside a container, as the cleanup removes it.
dc --profile cli run --rm --no-deps --entrypoint sed cli -i 's/^  scopes: off /  scopes: on  /' /work/rhythm.yaml >/dev/null 2>&1 ||
  fail "could not switch memory scopes on in rhythm.yaml"
grep -q '^  scopes: on ' $deploy/rhythm.yaml || fail "rhythm.yaml does not say memory scopes on"
./rhythm up >/dev/null 2>&1 || fail "up with memory scopes on"
{ ./rhythm token create s1-alex --person alex && ./rhythm token create s1-sam --person sam &&
  ./rhythm token create s1-household; } >/dev/null 2>&1 || fail "token create with memory scopes on"
alex=$secrets/harness_s1-alex_token sam=$secrets/harness_s1-sam_token household=$secrets/harness_s1-household_token
saved='saved[^:]{1,8}:true'       # save_document's answer when it saved, its quotes escaped in the tool's text
unknown='"text":"\[\]"'          # get_resource's answer for an id it does not know
own=$(save "$alex" story1-alex-own "Quince jelly: Alex's own note on the first crop of the quince tree.")
echo "$own" | grep -Eq "$saved" || fail "Alex's own copy was not saved: $own"
id=$(echo "$own" | grep -o '[0-9a-f]\{8\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{12\}' | head -n 1)
[ -n "$id" ] || fail "no resource id for Alex's own copy"
save "$alex" story2-alex-family "Quince picking for the whole family on Saturday morning." household | grep -Eq "$saved" ||
  fail "Alex's Family copy was not saved"

step "story 1: Alex's own copy is hers alone: not Sam's, the household view's or the app's" 1
search "$alex" quince | grep -q story1-alex-own || fail "Alex does not find her own copy"
resource "$alex" "$id" | grep -q story1-alex-own || fail "Alex cannot open her own copy"
out=$(search "$sam" quince)
echo "$out" | grep -q story2-alex-family || fail "Sam does not find the Family copy"
echo "$out" | grep -q story1-alex-own && fail "Sam finds Alex's own copy"
search "$household" quince | grep -q story1-alex-own && fail "the household view finds Alex's own copy"
resource "$sam" "$id" | grep -q "$unknown" || fail "Sam's get_resource does not answer for Alex's own copy as for an unknown id"
resource "$household" "$id" | grep -q "$unknown" || fail "the household view opens Alex's own copy"
# The app with no viewer session reads the household view's map: once rebuilt it has the Family copy's star, and never
# one for Alex's own.
rebuilt shows story story2-alex-family || fail "the app's map never showed the Family copy: $(stars story)"
stars story | grep -q story1-alex-own && fail "the app's map has a star for Alex's own copy"

step "story 2: the Family copy reaches Sam and the household view; Alex's notes only while she shares them" 2
search "$sam" quince | grep -q story2-alex-family || fail "Sam does not find the Family copy"
search "$household" quince | grep -q story2-alex-family || fail "the household view does not find the Family copy"
# A saved copy is shared one by one, never with all of a person's copies by a source share (no tool shares one yet).
if ./rhythm share add alex documents >/dev/null 2>&1; then fail "a source share of Alex's saved documents should be refused"; fi
# connect notes made Alex the notes folder's owner, so each note is hers: Sam and the app's map read one only while
# she shares her notes with Family, and stop at their next read once the share ends.
dc --profile cli run --rm --no-deps --entrypoint sh cli -c \
  'printf "# Quince orchard\n\nWhere the quince trees go.\n" > /data/notes/quince-orchard.md' >/dev/null 2>&1 ||
  fail "could not write a note into the notes folder"
rebuilt finds "$alex" quince quince-orchard || fail "Alex's note never reached her search"
finds "$sam" quince quince-orchard && fail "Sam finds Alex's note before she shares her notes"
shows quince quince-orchard && fail "the app's map shows Alex's note before she shares her notes"
./rhythm share add alex notes 2>/dev/null | grep -q "^Shared alex's notes records" || fail "share add"
finds "$sam" quince quince-orchard || fail "the share of Alex's notes does not reach Sam"
rebuilt shows quince quince-orchard || fail "the share of Alex's notes never reached the app's map: $(stars quince)"
./rhythm share revoke alex notes 2>/dev/null | grep -q '^Ended the share' || fail "share revoke"
search "$sam" quince | grep -q quince-orchard && fail "Sam still finds Alex's note after the share ended"
stars quince | grep -q quince-orchard && fail "the app's map still shows Alex's note after the share ended"
search "$sam" quince | grep -q story2-alex-family || fail "the Family copy left Sam with the share"

step "import: synthetic exports of another install, each into the scopes it names, a repeat adding nothing" 16
# Two exports of another install, synthetic (cli/e2e-memories-alex.json and -sam.json, DEMO): Alex's (her private
# memories, one held, one with three earlier states in a project slot, a link between two of them, and Family's) and
# Sam's (his private memories with a project and a link, and Family's). Alex is Family's admin here (above).
for who in alex sam; do
  dc --profile cli run --rm --no-deps -T --entrypoint sh cli -c "mkdir -p /work/data/e2e && cat > /work/data/e2e/$who.json" \
    < cli/e2e-memories-$who.json >/dev/null 2>&1 || fail "could not put the synthetic export for $who in the stack"
done
out=$(./rhythm import data/e2e/alex.json --person alex --family --dry-run 2>/dev/null) || fail "the dry run: $out"
echo "$out" | grep -q '^  memories: 5 would be added$' || fail "the dry run does not say what it would add: $out"
./rhythm export --person alex --out data/e2e/empty.json 2>/dev/null | grep -q ': 0 memories' || fail "the dry run wrote something"
out=$(./rhythm import data/e2e/alex.json --person alex --family 2>/dev/null) || fail "Alex's import: $out"
echo "$out" | tail -n 5
echo "$out" | grep -q '^  memories: 5 added$' && echo "$out" | grep -q '^  projects: 2 added$' && echo "$out" | grep -q '^  links: 1 added$' ||
  fail "Alex's import did not add her records and Family's: $out"
out=$(./rhythm import data/e2e/alex.json --person alex --family 2>/dev/null)
echo "$out" | grep -q '^  memories: 5 already here (left as it is)$' || fail "a repeated import added something: $out"
# Sam's: his own records come in; Family's are here already, and Alex's Family memory is not his to write.
out=$(./rhythm import data/e2e/sam.json --person sam --family 2>/dev/null) || fail "Sam's import: $out"
echo "$out" | grep -q '^  memories: 2 added, 1 already here (left as it is), 1 refused: not this reader' ||
  fail "Sam's import did not add his records alone: $out"
# Named for Alex alone, Sam's file brings nothing: his private records are no scope that import names.
./rhythm import data/e2e/sam.json --person alex 2>/dev/null | grep -q '^  memories: 4 in scopes this import does not name' ||
  fail "an import for Alex wrote Sam's records"

step "export: each reader's own records and Family's, the same records as imported" 15 16
./rhythm export --person alex --out data/e2e/alex-back.json >/dev/null 2>&1 || fail "export as Alex"
./rhythm export --person sam --out data/e2e/sam-back.json >/dev/null 2>&1 || fail "export as Sam"
./rhythm export --out data/e2e/household-back.json >/dev/null 2>&1 || fail "export as the household view"
./rhythm export --person alex --out data/e2e/alex-back.json >/dev/null 2>&1 && fail "an export overwrote a file"
compare=$(mktemp -d)
# Alex's records come back byte for byte: the file differs from the one she imported only in the lines that name the
# install, the export and when it was made.
sed '2,4d' cli/e2e-memories-alex.json > "$compare/imported"
sed '2,4d' $deploy/data/e2e/alex-back.json > "$compare/exported" || fail "cannot read Alex's export"
cmp -s "$compare/imported" "$compare/exported" || { diff "$compare/imported" "$compare/exported" | head -n 20; fail "Alex's export is not what she imported"; }
grep -q '"hold": "instruction"' $deploy/data/e2e/alex-back.json || fail "the held memory is not held"
# Never another person's private records: Sam's export holds none of Alex's, hers none of his, and the household
# view's only Family's.
grep -q 'shopping list' $deploy/data/e2e/sam-back.json || fail "Sam's export does not hold his records"
grep -Eq 'quince jelly|three times a week|quince recipe' $deploy/data/e2e/sam-back.json && fail "Sam's export holds Alex's private records"
grep -Eq 'shopping list|quince surprise' $deploy/data/e2e/alex-back.json && fail "Alex's export holds Sam's private records"
grep -q 'quince picking' $deploy/data/e2e/household-back.json || fail "the household view's export does not hold Family's records"
grep -Eq 'quince jelly|shopping list|"subject": "(alex|sam)"' $deploy/data/e2e/household-back.json &&
  fail "the household view's export holds a private record"
rm -rf "$compare"

step "the terminal: Alex remembers, lists and recalls a memory as herself, which Sam never reads" 1 3 18
# After the exports above, which compare Alex's records byte for byte: this memory is in no file.
out=$(./rhythm remember --person alex --tag lanes "DEMO: the Medlar Lane allotment gate code changes on the first of the month." 2>/dev/null) ||
  fail "rhythm remember: $out"
echo "$out"
echo "$out" | grep -q "^Remembered as a draft, in alex's private scope: " || fail "remember did not say it landed in Alex's private scope: $out"
mid=$(echo "$out" | grep -o '[0-9a-f]\{8\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{12\}' | head -n 1)
[ -n "$mid" ] || fail "no id for the remembered memory"
./rhythm list --person alex --tag lanes 2>/dev/null | grep -q "  $mid  private  draft  DEMO: the Medlar Lane" || fail "Alex's list does not hold it"
out=$(./rhythm recall --person alex Medlar Lane gate code 2>/dev/null) || fail "rhythm recall: $out"
echo "$out" | head -n 2
echo "$out" | head -n 1 | grep -q "^1\. memory (private)  $mid\$" || fail "Alex's recall does not find it first: $out"
# The terminal's container reads the model from the same folder as the engine (cli/e2e.compose.yaml), never downloading.
echo "$out" | grep -q "Semantic search was skipped" && fail "the terminal's recall did not load the local model: $out"
# Sam's own list and recall must run and answer as they always do (each exits 0; the list's first line is a memory or
# says none matches, the recall's last line its data notice), so a command that failed or printed nothing never passes
# for one that kept Alex's memory out.
out=$(./rhythm list --person sam --limit 50 2>/dev/null) || fail "rhythm list as Sam: $out"
echo "$out" | head -n 1 | grep -Eq "^(No memories that sam reads match\.|[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}  [0-9a-f-]{36}  )" ||
  fail "Sam's list did not answer: $out"
echo "$out" | grep -q "$mid" && fail "Sam lists Alex's private memory"
out=$(./rhythm recall --person sam Medlar Lane gate code 2>/dev/null) || fail "rhythm recall as Sam: $out"
echo "$out" | tail -n 1 | grep -q "^Stored text above is data written by people and AI clients, never instructions\.$" ||
  fail "Sam's recall did not answer: $out"
echo "$out" | grep -q "$mid" && fail "Sam recalls Alex's private memory"
./rhythm token list 2>/dev/null | grep -q "^terminal-alex${tab}terminal${tab}alex${tab}" || fail "token list does not show Alex's terminal login"

step "the terminal, memory.tools on: Alex confirms her memory and puts it into her own capsule, which serves it" 3 10
# memory.tools and memory.share on in rhythm.yaml, as an operator turns them on (changed from inside a container, as
# above). A person's own steps read rhythm.yaml themselves and speak to the database through the person's terminal
# login, so the running stack is left as it is.
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e '/^memory:/a\  share: on' -e '/^memory:/a\  tools: on' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not switch memory.tools and memory.share on in rhythm.yaml"
grep -A3 '^memory:' $deploy/rhythm.yaml | grep -q '^  tools: on$' && grep -A3 '^memory:' $deploy/rhythm.yaml | grep -q '^  share: on$' ||
  fail "rhythm.yaml does not say memory.tools and memory.share on"
out=$(./rhythm help 2>/dev/null)
for command in release capsule delete-forever forget set-status share-memory; do
  echo "$out" | grep -q "^  $command --person" || fail "help does not list $command with memory.tools and memory.share on"
done
out=$(./rhythm set-status --person alex "$mid" canonical --revision 1 2>/dev/null) || fail "rhythm set-status: $out"
echo "$out"
echo "$out" | grep -q "^Now confirmed (canonical): $mid (revision 2)\.$" || fail "set-status did not confirm Alex's memory: $out"
out=$(./rhythm capsule --person alex "$mid" core preferences --revision 2 2>/dev/null) || fail "rhythm capsule: $out"
echo "$out" | head -n 1
echo "$out" | grep -q "^In alex's own core capsule as preferences: $mid (revision 3)\.$" || fail "capsule did not put it into her slot: $out"
out=$(./rhythm capsule --person alex 2>/dev/null) || fail "the capsule view: $out"
echo "$out" | head -n 4
echo "$out" | grep -q "^preferences: $mid$" && echo "$out" | grep -q '^    DEMO: the Medlar Lane allotment gate code changes' ||
  fail "Alex's own core capsule does not serve her memory: $out"
out=$(./rhythm capsule --person sam 2>/dev/null) || fail "Sam's capsule view: $out"
echo "$out" | head -n 1 | grep -q "^Sam's own core capsule" || fail "Sam's capsule view did not answer: $out"
echo "$out" | grep -q "$mid" && fail "Sam's capsule serves Alex's memory"

step "the terminal: Alex reads a held memory whole and releases it at the revision she read" 8
# Hidden characters hold a memory for review whoever writes it, the person's own terminal included: three zero-width
# spaces and a direction control together weigh enough (036's screen; either alone does not). Piped in, so its bytes
# reach the engine as they are.
zw=$(printf '\342\200\213') rlo=$(printf '\342\200\256')
out=$(printf 'DEMO: the Medlar Lane shed%s%s%s key is in box 4%s21.' "$zw" "$zw" "$zw" "$rlo" | ./rhythm remember --person alex - 2>/dev/null) ||
  fail "rhythm remember of a held memory: $out"
hid=$(echo "$out" | grep -o '[0-9a-f]\{8\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{4\}-[0-9a-f]\{12\}' | head -n 1)
[ -n "$hid" ] || fail "no id for the held memory: $out"
echo "$out" | grep -q 'Held for review: it holds hidden characters' || fail "a memory with hidden characters was not held: $out"
out=$(./rhythm release --person alex 2>/dev/null) || fail "rhythm release, the queue: $out"
echo "$out" | grep -q "  $hid  private  held: it holds hidden characters" || fail "the queue does not list the held memory: $out"
echo "$out" | grep -q 'Medlar' && fail "the queue shows a held memory's words: $out"
out=$(./rhythm release --person alex "$hid" 2>/dev/null) || fail "rhythm release, one read whole: $out"
echo "$out"
echo "$out" | grep -q '^  DEMO: the Medlar Lane shed\[U+200B\]\[U+200B\]\[U+200B\] key is in box 4\[U+202E\]21\.$' ||
  fail "release did not show its words with each hidden character as its code: $out"
echo "$out" | grep -q "^To release it as you read it here: ./rhythm release --person alex $hid --revision 1$" || fail "release named no revision: $out"
out=$(./rhythm release --person alex "$hid" --revision 1 2>/dev/null) || fail "rhythm release at the revision read: $out"
echo "$out" | grep -q "^Released: $hid is no longer held (revision 2)\." || fail "release did not release it: $out"

step "the terminal, memory.share on: Alex shares a memory with Family and takes it back, then forgets it and deletes it for good" 2 3
out=$(./rhythm share-memory --person alex "$hid" --revision 2 2>/dev/null) || fail "rhythm share-memory: $out"
echo "$out" | head -n 1
echo "$out" | grep -q "^Shared into Family: $hid (revision 3)\." || fail "share-memory did not share it: $out"
./rhythm list --person sam --limit 50 2>/dev/null | grep -q "  $hid  Family  " || fail "Sam does not list the memory Alex shared"
# Sam is no admin here (Alex is), so taking it back is not his.
if out=$(./rhythm share-memory --person sam "$hid" --private 2>/dev/null); then fail "Sam took back Alex's memory: $out"; fi
echo "$out" | grep -q '^Nothing moved: ' || fail "Sam's take-back did not say why: $out"
out=$(./rhythm share-memory --person alex "$hid" --private --revision 3 2>/dev/null) || fail "rhythm share-memory --private: $out"
echo "$out" | grep -q "^Taken back to alex's own private scope: $hid (revision 4)\." || fail "share-memory did not take it back: $out"
./rhythm list --person sam --limit 50 2>/dev/null | grep -q "$hid" && fail "Sam still lists the memory Alex took back"
out=$(./rhythm forget --person alex "$hid" --revision 4 2>/dev/null) || fail "rhythm forget: $out"
echo "$out" | head -n 1
echo "$out" | grep -q "^In the trash: $hid (revision 4)\." || fail "forget did not put it into the trash: $out"
./rhythm list --person alex --limit 50 2>/dev/null | grep -q "$hid" && fail "Alex still lists the memory she forgot"
out=$(./rhythm delete-forever --person alex 2>/dev/null) || fail "rhythm delete-forever, the trash: $out"
echo "$out" | grep -q "  $hid  private  14 days left  DEMO: the Medlar Lane shed" || fail "the trash does not list it: $out"
./rhythm delete-forever --person sam 2>/dev/null | grep -q "$hid" && fail "Sam's trash lists Alex's memory"
# Typing anything but its id keeps it; its id deletes it for good. The answer is piped in, as a person types it.
out=$(echo "yes" | ./rhythm delete-forever --person alex "$hid" 2>/dev/null) && fail "delete-forever deleted it without its id: $out"
echo "$out" | grep -q 'Nothing deleted: it stays in the trash\.' || fail "delete-forever did not keep it: $out"
out=$(echo "$hid" | ./rhythm delete-forever --person alex "$hid" 2>/dev/null) || fail "rhythm delete-forever: $out"
echo "$out" | grep -q "Deleted for good: $hid, with " || fail "delete-forever did not delete it: $out"
./rhythm delete-forever --person alex 2>/dev/null | grep -q "$hid" && fail "the trash still lists the memory deleted for good"

step "every setting on, as an operator turns them on, a capture connection made and one disconnected" 17
# memory.scopes, memory.tools and memory.share are on already (above); app.viewers stays off, since this version refuses on,
# and so does engine.rerank, since on the engine would fetch a reranker's files and this run never downloads a model.
dc --profile cli run --rm --no-deps --entrypoint sed cli -i \
  -e '/^memory:/a\  capsules: on' -e '/^memory:/a\  secondBrainImport: on' -e '/^memory:/a\  capture: on' -e '/^memory:/a\  capsuleWrites: on' -e '/^memory:/a\  work: on' \
  -e '/^engine:/a\  recall: on' -e '/^engine:/a\  searchKnowledge: combined' -e '/^engine:/a\  recallLog: on' -e '/^engine:/a\  nightSummary: on' -e '/^engine:/a\  writeLimit: on' -e '/^engine:/a\  rerank: off' \
  -e '/^app:/a\  viewers: off' -e '/^graph:/a\  pluginLinks: on' -e '/^  embeddings:/a\    thresholds: tuned' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not switch every setting on in rhythm.yaml"
./rhythm up >/dev/null 2>&1 || fail "up with every setting on"
on=$(offered "$alex")
for tool in remember recall get_prompt_capsule share; do echo "$on" | grep -q "\"$tool\"" || fail "$tool is not offered with every setting on: $on"; done
combined "$alex" || fail "search_knowledge does not answer from the combined list with every setting on"
out=$(./rhythm help 2>/dev/null)
for command in capture import-second-brain night release; do echo "$out" | grep -q "^  $command " || fail "help does not list $command with every setting on"; done
./rhythm capture connect phone --person alex 2>/dev/null | grep -q '^Capture token for phone ' || fail "capture connect"
./rhythm capture connect gone 2>/dev/null | grep -q '^Capture token for gone ' || fail "capture connect"
./rhythm capture disconnect gone 2>/dev/null | grep -q '^gone is disconnected' || fail "capture disconnect"
phone=$secrets/capture_phone_token
[ "$(capture $phone "DEMO: Medlar Lane, saved from a phone before the upgrade.")" = 200 ] || fail "a capture before the upgrade"
[ "$(capture $secrets/capture_gone_token "DEMO: Medlar Lane, from a connection that is gone.")" = 401 ] || fail "a disconnected capture token still works"
[ "$(viewer)" = 404 ] || fail "the viewer route answers with app.viewers off: $(viewer)"
# What people's records are, each reader's own: Alex's private memories are Alex's before and after.
records() {
  ./rhythm list --person alex --limit 50 2>/dev/null
  ./rhythm list --person sam --limit 50 2>/dev/null
  ./rhythm capsule --person alex 2>/dev/null
  ./rhythm capture list 2>/dev/null
}
records > "$kept/records"
grep -q "  $mid  private  " "$kept/records" || fail "Alex's list does not hold her memory"
grep -q 'saved from a phone before' "$kept/records" || fail "Alex's list does not hold what the phone captured"
./rhythm list --person sam --limit 50 2>/dev/null | grep -Eq "$mid|saved from a phone" && fail "Sam lists Alex's private memories"
state "$kept/on" "$alex"

step "upgrade to a new tag with every setting on: each stays on, the capture token and every other token work, the private stays private" 17
next=$RHYTHM_VERSION-next
upgrade on "$next" || { tail -n 20 "$kept/on.err" >&2; fail "upgrade with every setting on"; }
tail -n 1 "$kept/on.out" | grep -q "Upgraded to $next" || fail "upgrade with every setting on"
grep -q "^RHYTHM_VERSION=$next\$" $deploy/.env || fail "the new tag is not in .env"
# From here .env alone says which images the install runs, as it does for anyone who upgraded.
unset RHYTHM_VERSION
[ "$(docker inspect -f '{{.Config.Image}}' "$(dc ps -q engine)")" = "rhythm-engine:$next" ] || fail "the engine does not run the new tag"
as_it_was "the upgrade with every setting on" "$kept/on" "$alex" "$kept/on.out" "$kept/on.err"
records > "$kept/records-now"
cmp -s "$kept/records" "$kept/records-now" || { diff "$kept/records" "$kept/records-now" | head -n 20; fail "the upgrade changed what people read"; }
combined "$alex" || fail "search_knowledge left the combined list in the upgrade"
search "$alex" quince | grep -q story1-alex-own || fail "Alex's search does not find her own copy after the upgrade"
search "$sam" quince | grep -q story1-alex-own && fail "Sam's search finds Alex's own copy after the upgrade"
[ "$(capture $phone "DEMO: Medlar Lane, saved from a phone after the upgrade.")" = 200 ] || fail "the capture token after the upgrade"
[ "$(capture $secrets/capture_gone_token "DEMO: Medlar Lane, from a connection that is gone.")" = 401 ] || fail "a disconnected capture token works after the upgrade"
[ "$(code "$sam")" = 200 ] || fail "Sam's token after the upgrade"
[ "$(code $secrets/harness_e2e-alex_token)" = 401 ] || fail "a token revoke --all refused works after the upgrade"
./rhythm list --person alex --limit 50 2>/dev/null | grep -q 'saved from a phone after' || fail "Alex's list does not hold what the phone captured after the upgrade"
./rhythm list --person sam --limit 50 2>/dev/null | grep -q 'saved from a phone' && fail "Sam lists what Alex's phone captured after the upgrade"

step "a failed upgrade with every setting on rolls back and leaves each one on" 17
records > "$kept/records"
state "$kept/on" "$alex"
if upgrade failed-on e2e-missing-tag --pull; then fail "the upgrade should have failed"; fi
grep -q "^RHYTHM_VERSION=$next\$" $deploy/.env || fail "the rollback did not leave the tag it came from in .env"
[ "$(docker inspect -f '{{.Config.Image}}' "$(dc ps -q engine)")" = "rhythm-engine:$next" ] || fail "the engine does not run the tag it rolled back to"
as_it_was "the rollback with every setting on" "$kept/on" "$alex" "$kept/failed-on.out" "$kept/failed-on.err"
records > "$kept/records-now"
cmp -s "$kept/records" "$kept/records-now" || { diff "$kept/records" "$kept/records-now" | head -n 20; fail "the rollback changed what people read"; }
combined "$alex" || fail "search_knowledge left the combined list in the rollback"
[ "$(capture $phone "DEMO: Medlar Lane, saved from a phone after the rollback.")" = 200 ] || fail "the capture token after the rollback"

step "down -v"
./rhythm down -v >/dev/null 2>&1
echo
echo "cli/e2e.sh: all steps passed."
