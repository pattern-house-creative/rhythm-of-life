#!/bin/sh
# engine/titles-eval.sh: the titles-model evaluation (SB-P2-14, D-027; docs/dev/second-brain-parity/titles-eval.md).
# Starts the titles service's Ollama image in a compose project of its own on a loopback port, downloads each named
# model into that project's volume, and runs the opt-in TitlesEvaluation test against a fresh server per model, so the
# server's memory peak is that model's alone. Then it rebuilds the report from every run in the folder and removes the
# project (containers, network and the volume with the downloaded models), also when a step fails or the run is stopped.
#
#   sh engine/titles-eval.sh <model> [<model> ...]
#
# RHYTHM_TITLES_EVAL_OUT names the report folder (default ./titles-report), RHYTHM_TITLES_EVAL_PORT the loopback port
# (default 11534), RHYTHM_TITLES_EVAL_PROJECT the compose project (default sb-titles-eval; never the stack's own),
# RHYTHM_TITLES_EVAL_REPEATS the passes over the set per model (default 3), RHYTHM_TITLES_EVAL_IMAGE another image
# (default the one deploy/compose/engine.compose.yaml pins), and RHYTHM_TITLES_EVAL_KEEP=1 keeps the volume for a later
# run. The image stays; remove it with `docker image rm` when done. Needs Docker, curl and the .NET SDK.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
if [ $# -eq 0 ]; then echo "Name the models: sh engine/titles-eval.sh <model> [<model> ...]" >&2; exit 2; fi
image=${RHYTHM_TITLES_EVAL_IMAGE:-$(sed -n 's/.*\${RHYTHM_OLLAMA_IMAGE:-\([^}]*\)}.*/\1/p' "$here/../deploy/compose/engine.compose.yaml" | head -n 1)}
if [ -z "$image" ]; then echo "No Ollama image in deploy/compose/engine.compose.yaml; set RHYTHM_TITLES_EVAL_IMAGE." >&2; exit 1; fi
project=${RHYTHM_TITLES_EVAL_PROJECT:-sb-titles-eval}
port=${RHYTHM_TITLES_EVAL_PORT:-11534}
case "$project" in rhythm | rhythm[-_]*) echo "Use a compose project of its own, not $project." >&2; exit 2 ;; esac
case "$port" in 8080 | 8181) echo "Port $port is the stack's; pick another." >&2; exit 2 ;; esac
out=${RHYTHM_TITLES_EVAL_OUT:-$PWD/titles-report}
mkdir -p "$out"
export RHYTHM_TITLES_EVAL_IMAGE="$image" RHYTHM_TITLES_EVAL_PORT="$port"

compose() { docker compose -p "$project" -f "$here/tests/titles-eval.compose.yaml" "$@"; }
cleanup() {
  if [ "${RHYTHM_TITLES_EVAL_KEEP:-}" = 1 ]; then compose down >/dev/null 2>&1 || true
  else compose down -v >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# One file from the server's cgroup (v2). MSYS_NO_PATHCONV keeps Git Bash on Windows from rewriting the container path.
cgroup() { MSYS_NO_PATHCONV=1 docker exec "$1" cat "/sys/fs/cgroup/$2" 2>/dev/null || echo 0; }
evaluate() {
  RHYTHM_TITLES_EVAL=1 RHYTHM_TITLES_EVAL_URL="http://localhost:$port/" RHYTHM_TITLES_EVAL_MODELS="$1" RHYTHM_TITLES_EVAL_OUT="$out" \
    dotnet test "$here/tests/Knowledge.Tests" --no-build --filter "FullyQualifiedName~Knowledge.Tests.Parity.TitlesEvaluation"
}

dotnet build "$here/tests/Knowledge.Tests" -nologo -v quiet >/dev/null
for model in "$@"; do
  compose run --rm pull "$model"
  compose up -d --force-recreate server
  tries=0
  until curl -sf "http://127.0.0.1:$port/api/version" >/dev/null; do
    tries=$((tries + 1))
    if [ "$tries" -ge 60 ]; then echo "The titles server did not start." >&2; exit 1; fi
    sleep 1
  done
  id=$(compose ps -q server)
  idle=$(cgroup "$id" memory.current)
  evaluate "$model"
  peak=$(cgroup "$id" memory.peak)
  current=$(cgroup "$id" memory.current)
  limit=$(cgroup "$id" memory.max)
  case "$limit" in '' | *[!0-9]*) limit=null ;; esac
  anon=$(cgroup "$id" memory.stat | sed -n 's/^anon \([0-9]*\)$/\1/p')
  file=$(cgroup "$id" memory.stat | sed -n 's/^file \([0-9]*\)$/\1/p')
  cpus=$(docker exec "$id" nproc 2>/dev/null || echo null)
  stats=$(docker stats --no-stream --format '{{.MemUsage}}' "$id" | tr -d '"')
  slug=$(printf '%s' "$model" | tr 'A-Z' 'a-z' | sed 's/[^a-z0-9.-]/-/g')
  printf '{"model":"%s","idleBytes":%s,"peakBytes":%s,"currentBytes":%s,"limitBytes":%s,"stats":"%s","cpus":%s,"anonBytes":%s,"fileBytes":%s}\n' \
    "$model" "$idle" "$peak" "$current" "$limit" "$stats" "$cpus" "${anon:-null}" "${file:-null}" >"$out/titles-memory-$slug.json"
  # The server's own log, for reading load times and errors; it holds no titles or texts.
  compose logs --no-color server >"$out/titles-server-$slug.log" 2>&1 || true
  compose stop server
done
evaluate ""
echo "Report: $out/titles-eval.md"
