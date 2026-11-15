#!/bin/sh
# Writes a private pgpass file from the mounted password file (never an argument or an environment value), then
# runs backup.py with the given action: backup <archive> <new-staging-folder> | restore <snapshot> <new-folder> |
# verify <folder> | load <restored-folder>. The entry matches any database on that server, since load makes one.
set -eu
umask 077
printf '%s:5432:*:%s:%s\n' "${PGHOST:-postgres}" "${PGUSER:-rhythm_admin}" "$(cat "${POSTGRES_PASSWORD_FILE:?}")" > /tmp/pgpass
export PGPASSFILE=/tmp/pgpass
exec python3 /app/backup.py "$@"
