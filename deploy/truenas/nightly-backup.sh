#!/bin/sh
# Runs inside the one-shot backup container. Retain staging on failure for the operator to inspect.
set -eu
umask 077
if [ ! -s /repository/config ]; then
  echo "The mounted folder is missing the existing restic repository config" >&2
  exit 1
fi
stage="/staging/nightly-$(date -u +%Y%m%dT%H%M%SZ)"
/app/entrypoint.sh backup /archive "$stage"
# backup completed its manifest verification and full encrypted-repository readback.
rm -r "$stage"
exec python3 /app/backup.py retention
