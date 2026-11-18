#!/bin/sh
# cli/chaos.sh: restores that go wrong, in a throwaway Docker stack, with fictional data only (SB-P7-10, story 15).
#
#   init (no input) -> up -> memory.tools and memory.capture on -> tokens (one for Sam), a capture connection and its
#   mirror, a document and a file in the engine's archive -> backup -> after it: a document, a token revoked, Sam
#   suspended, the capture connection disconnected -> a snapshot that is not there: nothing stops, nothing changes ->
#   a restored folder whose dump was changed, one holding a file its manifest does not list, one whose dump is cut short
#   in its data with a manifest that agrees (pg_restore makes the schema and loads rows, then fails), each loaded as
#   ./rhythm restore loads it, and an engine archive whose file differs from the backup's, through ./rhythm restore
#   itself: each refused, the live database and
#   its tokens as they were, no half-loaded database and no restored folder left, the stack running again (for the
#   last, started again by ./rhythm restore, which fails saying the load did not) -> the restore, for real: every old
#   token refused (the revoked one, Sam's, the capture connection's among them), the backed-up document back and the
#   later one gone, the archive file put back with the engine's owner, and what the backup held undone since back as it
#   was (Sam current, the connection and its mirror), which the operator does again -> the restore again: the tokens
#   made after the first refused, the data the same, both earlier databases kept -> down -v and the images removed
#
#   RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files> \
#   COMPOSE_PROJECT_NAME=rhythm-chaos RHYTHM_VERSION=chaos RHYTHM_APP_PORT=18380 RHYTHM_MCP_PORT=18481 sh cli/chaos.sh
#
# The last four are the defaults. Like cli/e2e.sh it runs only in a checkout with no install, in a Compose project with
# no containers or volumes yet (never the project named rhythm, an install's default), with an image tag nothing else
# runs or built, on ports nothing answers on, and the engine reads MiniLM from RHYTHM_MODEL_DIR (cli/e2e.compose.yaml),
# so nothing is downloaded but the images' own packages. Every cleanup names the project itself (-p, and Docker's
# com.docker.compose.project label), never one Compose would infer once .env is gone, and removes the stack's
# containers, volumes and networks, and the three images it built (RHYTHM_CHAOS_KEEP_IMAGES=1 keeps the images, to run
# it again without building them).
set -eu
cd "$(dirname "$0")/.."
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME-rhythm-chaos}" RHYTHM_VERSION="${RHYTHM_VERSION:-chaos}" MSYS_NO_PATHCONV=1
project=$COMPOSE_PROJECT_NAME
app=${RHYTHM_APP_PORT:-18380}
mcp=${RHYTHM_MCP_PORT:-18481}
unset RHYTHM_APP_PORT RHYTHM_MCP_PORT # init writes them into .env
deploy=deploy/compose
secrets=$deploy/secrets
refuse() { echo "cli/chaos.sh: $*" >&2; exit 2; }
# Every Compose call names this run's project (-p), never one Compose would infer: with none exported and no .env it
# falls back to the stack's own name, rhythm, an install's, and the cleanup removes everything in its project (D-240).
case "$(printf %s "$COMPOSE_PROJECT_NAME" | tr 'A-Z' 'a-z')" in
  "" | rhythm) refuse "the Compose project '$COMPOSE_PROJECT_NAME' is empty or an install's default (rhythm): give the run its own (COMPOSE_PROJECT_NAME)." ;;
esac
case "$RHYTHM_VERSION" in dev | latest | m[0-9]* | "") refuse "the image tag $RHYTHM_VERSION may be an install's: give the run its own (RHYTHM_VERSION)." ;; esac
if [ -e $deploy/rhythm.yaml ] || [ -e $deploy/.env ] || [ -e $secrets ]; then refuse "an install exists in $deploy; this runs only in a checkout without one."; fi
model=${RHYTHM_MODEL_DIR:-}
if [ -z "$model" ] || [ ! -f "$model/model_quantized.onnx" ] || [ ! -f "$model/vocab.txt" ]; then
  refuse "NOT RUN: set RHYTHM_MODEL_DIR to a folder holding the pinned MiniLM files; without them the engine's first start would download the model (D-041)."
fi
command -v curl >/dev/null 2>&1 || refuse "needs curl."
label="label=com.docker.compose.project=$project"
made=$(docker ps -aq --filter "$label") && volumes=$(docker volume ls -q --filter "$label") || refuse "Docker does not answer."
[ -z "$made$volumes" ] || refuse "the Compose project $project already has containers or volumes: give the run its own (COMPOSE_PROJECT_NAME)."
for image in rhythm-engine rhythm-app rhythm-backup; do
  [ -z "$(docker ps -aq --filter "ancestor=$image:$RHYTHM_VERSION" 2>/dev/null)" ] || refuse "another stack runs $image:$RHYTHM_VERSION: give the run its own tag (RHYTHM_VERSION)."
  if built=$(docker image inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$image:$RHYTHM_VERSION" 2>/dev/null); then
    [ "$built" = "$project" ] || refuse "$image:$RHYTHM_VERSION is already here, built by ${built:-no Compose project}: give the run its own tag (RHYTHM_VERSION)."
  fi
done
for port in "$app" "$mcp"; do
  answered=0
  curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/" || answered=$?
  [ "$answered" = 7 ] || refuse "something already answers on 127.0.0.1:$port: choose free ports (RHYTHM_APP_PORT, RHYTHM_MCP_PORT)."
done
RHYTHM_E2E_MODEL_DIR=$(cd "$model" && { pwd -W 2>/dev/null || pwd; })
export RHYTHM_E2E_MODEL_DIR COMPOSE_PATH_SEPARATOR=: COMPOSE_FILE=compose.yaml:../../cli/e2e.compose.yaml

# Every call names the project itself.
dc() { (cd $deploy && docker compose -p "$project" "$@"); }
cleanup() {
  # The files belong to the services' users, so they are removed from inside a container.
  dc --profile cli run --rm --no-deps --entrypoint rm cli -rf /work/rhythm.yaml /work/.env /work/secrets /work/data >/dev/null 2>&1 || true
  dc --profile cli --profile backup --profile restore down -v --remove-orphans >/dev/null 2>&1 || true
  # Anything of the project's that Compose did not take down, by its label alone.
  left=$(docker ps -aq --filter "$label"); [ -z "$left" ] || docker rm -f -v $left >/dev/null 2>&1 || true
  left=$(docker volume ls -q --filter "$label"); [ -z "$left" ] || docker volume rm $left >/dev/null 2>&1 || true
  left=$(docker network ls -q --filter "$label"); [ -z "$left" ] || docker network rm $left >/dev/null 2>&1 || true
  [ "${RHYTHM_CHAOS_KEEP_IMAGES:-}" = 1 ] && return 0
  for image in rhythm-engine rhythm-app rhythm-backup; do
    built=$(docker image inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$image:$RHYTHM_VERSION" 2>/dev/null) || continue
    [ "$built" = "$project" ] && docker image rm "$image:$RHYTHM_VERSION" >/dev/null 2>&1 || true
  done
}
trap cleanup EXIT
# step "<what>" [<story> ...]: a step's line, with the acceptance stories it proves part of, as cli/e2e.sh writes it.
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
save() { # <token file> <id> <text>
  mcp "$1" "{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"method\":\"tools/call\",\"params\":{\"name\":\"save_document\",\"arguments\":{\"fileId\":\"$2\",\"title\":\"$2\",\"content\":\"$3\",\"modifiedAt\":\"2026-10-28T10:00:00Z\",\"semanticIndex\":false}}}"
}
search() { mcp "$1" "{\"jsonrpc\":\"2.0\",\"id\":\"q\",\"method\":\"tools/call\",\"params\":{\"name\":\"search_knowledge\",\"arguments\":{\"query\":\"$2\"}}}"; }
capture() { # <token file> <text> <mirror id>: the capture route's status
  curl -s -o /dev/null -w '%{http_code}' -X POST "http://127.0.0.1:$mcp/capture" -H "Authorization: Bearer $(cat "$1")" \
    -H 'Content-Type: application/json' -d "{\"text\":\"$2\",\"source\":\"tracker\",\"mirror\":\"$3\"}"
}
# As root in the restore service, which mounts the staging folder and the engine's data volume.
inside() { dc --profile restore run --rm --no-deps -T --entrypoint sh restore -c "$1"; }
databases() { dc exec -T postgres psql -U rhythm_admin -d postgres -tAc "SELECT datname FROM pg_database WHERE datname LIKE 'rhythm\\_%' ORDER BY 1"; }
# What a live install answers now: the client's token works, the document written after the backup is there, the
# capture connection disconnected after it stays refused, and no half-loaded database or restored folder is left.
as_it_was() { # <what>
  [ "$(code $secrets/harness_chaos-client_token)" = 200 ] || fail "after $1 the client's token no longer works"
  search $secrets/harness_chaos-client_token sunflowers | grep -q 'chaos-after-backup' || fail "after $1 the document written since the backup is gone"
  [ "$(capture $secrets/capture_feed_token "DEMO: an item after the disconnect." chaos:item/2)" = 401 ] || fail "after $1 a disconnected connection captures"
  databases | grep -q '^rhythm_restore_' && fail "after $1 a half-loaded database is left: $(databases | tr '\n' ' ')"
  [ -z "$(inside 'ls -A /staging | grep -E "^(chaos|restore)-" || true')" ] || fail "after $1 a restored folder is left in the staging folder"
  echo "after $1: the live install answers as it did, no half-loaded database, no restored folder"
}
# load_changed <name> <what is done to the folder, as sh> <what the refusal says>: the snapshot restored into a folder,
# changed, then loaded as ./rhythm restore loads it (the engine and the app stopped); refused, and the stack started again.
load_changed() {
  folder=/staging/chaos-$1
  dc --profile restore run --rm restore restore latest "$folder" >/dev/null 2>&1 || fail "could not restore the snapshot into $folder"
  inside "cd $folder && $2" || fail "could not change $folder"
  dc stop engine app >/dev/null 2>&1
  if dc --profile restore run --rm restore load "$folder" > "$kept/$1.out" 2> "$kept/$1.err"; then fail "a $1 folder was loaded"; fi
  grep -q "$3" "$kept/$1.err" || { cat "$kept/$1.err"; fail "the $1 folder's refusal does not say: $3"; }
  grep -v '^ *$' "$kept/$1.err" | tail -n 1 | sed 's/^/  load said: /'
  inside "rm -rf $folder" || fail "could not remove $folder"
  ./rhythm up >/dev/null 2>&1 || fail "up after the $1 load"
}
kept=$(mktemp -d)
trap 'rm -rf "$kept"; cleanup' EXIT

step "init, no input, up, and the memory tools and the capture route on"
./rhythm init --no-input --starter no --people "Alex, Sam" --timezone Europe/London --app-port "$app" --mcp-port "$mcp" >/dev/null 2>&1 || fail "init"
grep -q "^COMPOSE_PROJECT_NAME=$project$" $deploy/.env || fail ".env does not name the run's project"
dc --profile cli run --rm --no-deps --entrypoint sed cli -i -e '/^memory:/a\  capture: on' -e '/^memory:/a\  tools: on' /work/rhythm.yaml \
  >/dev/null 2>&1 || fail "could not turn memory.tools and memory.capture on"
./rhythm up >/dev/null 2>&1 || fail "up"
tries=0
until curl -s "http://127.0.0.1:$mcp/health" | grep -q '"embeddings":"on"'; do
  tries=$((tries + 1)); [ "$tries" -lt 30 ] || fail "the engine did not load the model from RHYTHM_MODEL_DIR"; sleep 2
done

step "before the backup: tokens, a capture connection and its mirror, a document and an archive file"
for name in chaos-client chaos-gone; do ./rhythm token create $name >/dev/null 2>&1 || fail "token create $name"; done
./rhythm token create chaos-sam --person sam >/dev/null 2>&1 || fail "token create chaos-sam"
./rhythm capture connect feed >/dev/null 2>&1 || fail "capture connect feed"
[ "$(capture $secrets/capture_feed_token "DEMO: the fictional tracker's item one." chaos:item/1)" = 200 ] || fail "the mirror's capture"
out=$(save $secrets/harness_chaos-client_token chaos-before-backup "Plant marigolds beside the tomatoes.")
echo "$out" | grep -q 'saved' || fail "save: $(echo "$out" | head -c 400)"
inside 'mkdir -p /var/lib/rhythm/archive/chaos && echo "DEMO: a fictional original." > /var/lib/rhythm/archive/chaos/original.txt &&
  chown -R 1654:1654 /var/lib/rhythm/archive/chaos' || fail "could not write the archive file"
./rhythm list --person alex --limit 50 2>/dev/null | grep -q "DEMO: the fictional tracker's item one" || fail "the mirror is not listed"

step "backup"
./rhythm backup 2>/dev/null | grep -q '"snapshot"' || fail "backup"

step "after the backup: a document, a token revoked, Sam suspended, the capture connection disconnected"
out=$(save $secrets/harness_chaos-client_token chaos-after-backup "Sunflowers along the fence.")
echo "$out" | grep -q 'saved' || fail "save: $(echo "$out" | head -c 400)"
./rhythm token revoke chaos-gone >/dev/null 2>&1 || fail "token revoke"
./rhythm person suspend sam >/dev/null 2>&1 || fail "person suspend"
./rhythm capture disconnect feed 2>/dev/null | grep -q '^feed is disconnected' || fail "capture disconnect"
for t in harness_chaos-gone_token harness_chaos-sam_token; do [ "$(code $secrets/$t)" = 401 ] || fail "$t still works"; done
./rhythm list --person alex --limit 50 2>/dev/null | grep -q "DEMO: the fictional tracker's item one" && fail "a disconnected mirror is still listed"
as_it_was "the changes since the backup"

step "a snapshot that is not there: refused, nothing stopped or changed"
./rhythm restore 0000000000000000 > "$kept/none.out" 2>&1 && fail "a snapshot that is not there was restored"
dc ps --status running --services 2>/dev/null | grep -qx engine || fail "the engine stopped for a snapshot that is not there"
as_it_was "a snapshot that is not there"

step "a restored folder whose dump was changed: refused before anything changes"
load_changed changed 'printf x >> database.dump' "Backup integrity check failed"
as_it_was "a changed dump"

step "a restored folder holding a file its manifest does not list: refused before anything changes"
load_changed extra 'echo "DEMO: put here afterwards." > archive/extra.txt' "a file its manifest does not list (archive/extra.txt)"
inside '[ ! -e /var/lib/rhythm/archive/extra.txt ]' || fail "the unlisted file reached the engine's archive"
as_it_was "an unlisted file"

# Its last 4 KiB go, which are table data: the table of contents at the head of the file, every object's definition, stays
# whole (pg_restore --list reads it), so pg_restore makes the schema and loads rows before it fails, and the database the
# load made holds them when it is dropped. A cut that reached the table of contents would fail before anything was made.
step "a dump cut short in its data, a manifest that agrees: pg_restore makes the schema and loads rows, then fails; the half-loaded database is dropped"
load_changed short 'size=$(wc -c < database.dump) && head -c $((size - 4096)) database.dump > short && mv short database.dump &&
  { pg_restore --list database.dump >/dev/null || { echo "the cut reached the dump'"'"'s table of contents" >&2; exit 1; }; } &&
  python3 -c "import hashlib,json; m=json.load(open(\"manifest.json\")); m[\"files\"][\"database.dump\"]=hashlib.sha256(open(\"database.dump\",\"rb\").read()).hexdigest(); json.dump(m,open(\"manifest.json\",\"w\"),sort_keys=True)"' \
  "the half-loaded database rhythm_restore_[0-9]* was dropped"
as_it_was "a dump cut short"

step "an engine archive file that differs from the backup's: ./rhythm restore refuses it before anything changes"
inside 'echo "DEMO: changed since." > /var/lib/rhythm/archive/chaos/original.txt' || fail "could not change the archive file"
if ./rhythm restore latest > "$kept/archive.out" 2>&1; then fail "./rhythm restore loaded over an archive file that differs"; fi
for said in "An archive file differs from the backup's copy; nothing was loaded" "The restore did not load"; do
  grep -q "$said" "$kept/archive.out" || { cat "$kept/archive.out"; fail "./rhythm restore does not say: $said"; }
done
grep "An archive file differs" "$kept/archive.out" | tail -n 1 | sed 's/^/  load said: /'
dc ps --status running --services 2>/dev/null | grep -qx engine || fail "./rhythm restore did not start the engine again after the failed load"
as_it_was "an archive file that differs"
# The file as the backup has it gone from the engine's archive, so the restore below must add it back.
inside 'rm /var/lib/rhythm/archive/chaos/original.txt' || fail "could not remove the archive file"

step "the restore, for real" 15
./rhythm restore latest > "$kept/restore.out" 2>&1 || { cat "$kept/restore.out"; fail "the restore"; }
for t in harness_chaos-client_token harness_chaos-gone_token harness_chaos-sam_token capture_feed_token; do
  [ "$(code $secrets/$t)" = 401 ] || fail "$t works after the restore"
done
[ "$(capture $secrets/capture_feed_token "DEMO: after the restore." chaos:item/1)" = 401 ] || fail "the capture token works after the restore"
./rhythm token create chaos-restored >/dev/null 2>&1 || fail "token create after the restore"
restored=$secrets/harness_chaos-restored_token
search $restored marigolds | grep -q 'chaos-before-backup' || fail "the backed-up document is not back"
search $restored sunflowers | grep -q 'chaos-after-backup' && fail "a document from after the backup survived the restore"
[ "$(inside 'cat /var/lib/rhythm/archive/chaos/original.txt; stat -c %u:%g /var/lib/rhythm/archive/chaos/original.txt')" = "$(printf 'DEMO: a fictional original.\n1654:1654')" ] ||
  fail "the archive file was not put back as the backup has it, owned by the engine"
[ -z "$(inside 'ls -A /staging | grep "^restore-" || true')" ] || fail "the restored folder is left in the staging folder"
[ "$(databases | grep -c '^rhythm_before_')" = 1 ] || fail "the database the restore replaced is not kept: $(databases | tr '\n' ' ')"
databases | grep -q '^rhythm_restore_' && fail "a half-loaded database is left after the restore"
echo "every old token refused; the backed-up document back and the later one gone; the archive file put back; the restored folder removed"

step "what the backup held, undone since, is back as it was, and the operator does it again" 15
./rhythm token create chaos-sam-again --person sam >/dev/null 2>&1 || fail "Sam is not current again after the restore"
[ "$(code $secrets/harness_chaos-sam-again_token)" = 200 ] || fail "a new token for Sam does not work after the restore"
./rhythm person suspend sam >/dev/null 2>&1 || fail "person suspend again"
[ "$(code $secrets/harness_chaos-sam-again_token)" = 401 ] || fail "Sam's new token works once Sam is suspended again"
./rhythm list --person alex --limit 50 2>/dev/null | grep -q "DEMO: the fictional tracker's item one" || fail "the mirror is not back as the backup held it"
./rhythm capture disconnect feed 2>/dev/null | grep -q '^feed is disconnected' || fail "capture disconnect again"
./rhythm list --person alex --limit 50 2>/dev/null | grep -q "DEMO: the fictional tracker's item one" && fail "the mirror is listed after the second disconnect"
echo "Sam current again until suspended again; the connection's mirror back until it is disconnected again"

step "the restore again: the tokens made after the first refused, the data the same, both earlier databases kept" 15
./rhythm restore latest > "$kept/again.out" 2>&1 || { cat "$kept/again.out"; fail "the second restore"; }
[ "$(code $restored)" = 401 ] || fail "a token made after the first restore works after the second"
./rhythm token create chaos-again >/dev/null 2>&1 || fail "token create after the second restore"
search $secrets/harness_chaos-again_token marigolds | grep -q 'chaos-before-backup' || fail "the backed-up document is not back after the second restore"
[ "$(databases | grep -c '^rhythm_before_')" = 2 ] || fail "both databases the restores replaced are not kept: $(databases | tr '\n' ' ')"
echo "the second restore as the first: $(databases | tr '\n' ' ')"

step "down -v: the chaos stack and its volumes gone"
./rhythm down -v >/dev/null 2>&1
echo "cli/chaos.sh: all steps passed."
