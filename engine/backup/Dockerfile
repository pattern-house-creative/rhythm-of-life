# Backups: a consistent pg_dump plus the engine's archive, checked, into an encrypted restic repository.
# Runs on demand from the compose "backup" profile; see engine/backup/README.md.
FROM postgres:17-alpine
RUN apk add --no-cache restic python3
WORKDIR /app
COPY backup.py restore-quarantine.sql entrypoint.sh ./
RUN chmod 0755 entrypoint.sh
USER 70:70
ENTRYPOINT ["/app/entrypoint.sh"]
