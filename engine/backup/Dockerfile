# Backups: a consistent pg_dump plus the engine's archive, checked, into an encrypted restic repository.
# Runs on demand from the compose "backup" profile; see engine/backup/README.md.
FROM postgres:17-alpine@sha256:a77aa70e641402a21411b92b721c58d10f23b50ae5fdd2333efd22d166e1e5f7
RUN apk add --no-cache restic python3
WORKDIR /app
COPY backup.py restore-quarantine.sql entrypoint.sh ./
RUN chmod 0755 entrypoint.sh
USER 70:70
ENTRYPOINT ["/app/entrypoint.sh"]
