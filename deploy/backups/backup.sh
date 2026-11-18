#!/bin/sh
# One backup of the install: pg_dump plus the engine's archive, checked, into the encrypted restic repository
# (the compose "backup" profile, engine/backup), then removal of the unencrypted staging copy. For cron or a
# TrueNAS cron job; see docs/backups.md.
#
#   deploy/backups/backup.sh
#
# Runs from anywhere. It works in deploy/compose/, so docker compose reads that folder's .env, including
# COMPOSE_FILE and COMPOSE_PROJECT_NAME when an install sets them (TrueNAS does). Exit status is the backup's: on
# failure the staging folder is kept for inspection, and it holds a plain copy of the database, so remove it then.
set -eu
cd "$(dirname "$0")/../compose"
stamp=$(date -u +%Y%m%d-%H%M%S)
docker compose --profile backup run --rm -T backup backup /var/lib/rhythm/archive "/staging/$stamp"
# The snapshot is saved and read back (restic check --read-data), so the plain copy can go. Removed inside the
# backup container, by the user that wrote it.
docker compose --profile backup run --rm -T --no-deps --entrypoint rm backup -r "/staging/$stamp"

# Only after verification and staging cleanup, apply the configured policy to this project.
docker compose --profile backup run --rm -T --no-deps --entrypoint python3 backup /app/backup.py retention
