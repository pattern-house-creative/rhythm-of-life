# engine/backup

Encrypted backups of one install: a consistent `pg_dump` of the database plus the engine's immutable archive
(`/var/lib/rhythm/archive`), with a checksum manifest, saved into a [restic](https://restic.net) repository and
checked with `restic check --read-data`.

Day to day, `./rhythm backup` runs it and `./rhythm restore <snapshot>` puts one back (`cli/README.md`). By hand:

```sh
# once: a repository password (keep a copy offline); the first backup creates a local repository itself
openssl rand -base64 32 > deploy/compose/secrets/backup_password

# each backup (the staging folder must be new)
docker compose -f deploy/compose/compose.yaml --profile backup run --rm backup   backup /var/lib/rhythm/archive /staging/$(date +%Y%m%d-%H%M%S)
```

Restoring is two steps, both in the `restore` profile (the same image, as root with only `CHOWN`, `DAC_OVERRIDE` and
`FOWNER`, and the engine's data volume writable):

1. `restore <snapshot> /staging/<new-folder>` writes the snapshot's files and checks them against the manifest. It
   touches nothing else.
2. `load /staging/<that-folder>`, with the engine and app stopped. First, before anything changes, the folder is checked
   against its manifest again, and refused if it holds a link or a file the manifest does not list (a restore writes
   exactly a snapshot's files), or if an archive file the engine already has differs from the backup's copy. Then
   `database.dump` goes into a **new** database, `restore-quarantine.sql` runs there, archive files missing from
   `/var/lib/rhythm/archive` are added (owned by the engine's user), and last the new database takes the live one's
   name. The live one is kept as `rhythm_before_<time>`, never dropped. A step that fails after the new database is
   made drops that new database (this load made it and nothing used it), so the live one is as it was and a retry
   starts clean; the message says so, or, if the switch itself stopped half way, which database to rename.
   `./rhythm restore` removes the folder afterwards, whatever became of the load: it is a plain copy of the database.

The next `migrate` brings the restored schema and the engine's login up to date. The quarantine disables every
token and removes old grants, because an old backup can bring back access that was revoked since; the engine takes
the app's token from its file again, and harness and plugin tokens are made anew. The admin audit (migration 037)
keeps every row it had and records each token the quarantine disabled as the system's job `restore`. The quarantine blanks the map's
snapshot rows, which the next rebuild fills again. The native memory tables (empty until memories can be written) come
back as they were, text, history, scopes and audit included: no memory text lives in a snapshot row, and the
quarantine leaves those tables alone, but for the trash: a memory in it gets the trash's 14 days again from the
restore (or what it had left, if longer), so the first daily purge never deletes what the restore brought back; one
trashed more than 351 days before the restore is dated 351 days before it, the latest date migration 025's 365-day
bound allows with 14 days to go, and its audit row keeps when it was trashed. So do the memory scope rows: Family's
members and admins, the person each token was bound to, source shares, people who left, the subject and scope of
every insight, dashboard, write, pin and corner card, and the recorded `memory.scopes` mode, so a backup that
records `on` refuses to start while `rhythm.yaml` says `off`, and a start with `on` adds nobody to Family. Every other
table comes back row for row (`MemoryRestoreTests`, which restores a real `pg_dump` into a throwaway database and
compares every table).

The scheduled script can apply the [opt-in snapshot retention policy](../../docs/backups.md#each-backup).
Failed staging folders and old `rhythm_before_*` databases remain for the operator to review.
