#!/bin/sh
# engine/test-db.sh: the engine's whole test run, database tests included. Starts a throwaway pgvector container on
# a random local port, runs `dotnet test` with RHYTHM_TEST_DB pointing at it, and removes the container again, also
# when a test fails or the run is stopped. Plain `dotnet test engine` skips the database tests.
#
#   sh engine/test-db.sh [dotnet test arguments, for example --filter DatabaseTests]
#
# RHYTHM_TEST_POSTGRES_IMAGE picks another image (default the one compose runs, read from its RHYTHM_POSTGRES_IMAGE
# default), RHYTHM_TEST_DB_PORT a fixed local port and RHYTHM_TEST_CONTAINER the container's name. The tests get the
# name as RHYTHM_TEST_DB_CONTAINER, for the backup and restore test's pg_dump and pg_restore. Needs Docker; nothing else
# on the machine is touched. engine/test-db.ps1 does the same in PowerShell.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
image=${RHYTHM_TEST_POSTGRES_IMAGE:-$(sed -n 's/.*\${RHYTHM_POSTGRES_IMAGE:-\([^}]*\)}.*/\1/p' "$here/../deploy/compose/engine.compose.yaml" | head -n 1)}
if [ -z "$image" ]; then echo "No Postgres image in deploy/compose/engine.compose.yaml; set RHYTHM_TEST_POSTGRES_IMAGE." >&2; exit 1; fi
name=${RHYTHM_TEST_CONTAINER:-rhythm-engine-test-$$-$(date +%s)}
password=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
# -v: the image keeps its data in an anonymous volume, which --rm would remove with a container that stops by itself but
# rm -f alone leaves behind, one per run.
cleanup() { docker rm -f -v "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
trap 'exit 130' INT TERM
# Each test class migrates a database of its own, all at once, every migration in one transaction: the default lock
# table (64 locks per connection) runs out ("out of shared memory", 53200), so the test server keeps more.
docker run -d --rm --name "$name" -e POSTGRES_PASSWORD="$password" -p "127.0.0.1:${RHYTHM_TEST_DB_PORT:-}:5432" "$image"   -c max_locks_per_transaction=256 >/dev/null
port=$(docker port "$name" 5432/tcp | head -n 1 | sed 's/.*://')
# The image's first start runs its setup on a local socket; the TCP listener means it is done.
tries=0
until docker exec "$name" pg_isready -q -U postgres -h 127.0.0.1; do
  tries=$((tries + 1))
  if [ "$tries" -ge 60 ]; then echo "The test database did not start." >&2; exit 1; fi
  sleep 1
done
RHYTHM_TEST_DB="Host=127.0.0.1;Port=$port;Username=postgres;Password=$password" RHYTHM_TEST_DB_CONTAINER="$name"   dotnet test "$here" "$@"
