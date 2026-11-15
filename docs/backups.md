# Backups

An install's backup is a consistent `pg_dump` of the database plus the engine's archive (the immutable
originals it keeps), with a checksum manifest, saved into an encrypted [restic](https://restic.net)
repository and read back in full (`restic check --read-data`) every time. It runs from the compose
`backup` profile (`engine/backup/`), on demand; the engine never runs it by itself.

## With the rhythm command

From the repository root (`.\rhythm.ps1` in PowerShell; [cli/README.md](../cli/README.md) has the details):

```sh
./rhythm backup              # a backup now; the first one creates the repository
./rhythm snapshots           # the backups in the repository
./rhythm restore latest      # put one back in place of the live database (kept, renamed), then start again
```

`./rhythm upgrade` takes a backup first. The rest of this page is what these do underneath, the nightly
schedule, the restore drill (a restore **beside** the live install, which `./rhythm restore` is not) and
recovering on a new machine.

All `docker compose` commands below run from `deploy/compose/`, where `docker compose` finds
`compose.yaml` and the install's `.env` by itself (including `COMPOSE_FILE` and `COMPOSE_PROJECT_NAME`,
which a TrueNAS install sets). On Windows in Git Bash, set `MSYS_NO_PATHCONV=1` first, or Git Bash
rewrites the `/staging/...` paths.

## What is in a backup, and what is not

| In the backup | Not in the backup: keep your own copy |
|---|---|
| the database: every star, measurement, insight, dashboard and its history, feedback, tokens' hashes and the person each one speaks for, Family's members and admins, source shares, and the `memory.scopes` mode it ran with | `rhythm.yaml` and `.env` (settings, not secret: keep a copy with your other records) |
| the archive (`/var/lib/rhythm/archive`): the originals of imported records | `secrets/`: the backup password (offline, below); the rest can be made again |
| | your notes folder and import folder: they are your own files, back them up as such |
| | the downloaded local model (it downloads again) |

## Once: the password and the repository

1. **The repository password.** `rhythm init` creates it; by hand:
   ```sh
   openssl rand -base64 32 > secrets/backup_password
   ```
2. **Keep a copy offline**, now: in a password manager you can reach without this server, or printed
   and stored with your important papers. Without it the backups cannot be opened by anyone, you
   included. Never keep the only copy on the machine being backed up.
3. **Choose where the repository lives.** `RHYTHM_BACKUP_DIR` in `.env` (default `./data/backups`).
   Prefer another disk or dataset than the install's own.
4. **Create the repository** (the first backup does this too):
   ```sh
   docker compose --profile backup run --rm --entrypoint restic backup init
   ```

On Linux the backup container runs as uid 70: the repository folder and the staging folder
(`RHYTHM_BACKUP_STAGING_DIR`, default `./data/backup-staging`) must be writable by it, and
`secrets/backup_password` and `secrets/postgres_password` readable ([security.md](security.md#secret-files)).

## Each backup

```sh
../backups/backup.sh
```

It takes the dump and the archive into a new staging folder, checks them against the manifest, saves
them as one restic snapshot, reads the whole repository back, prints
`{"snapshot": "...", "verified_files": N}`, and then removes the staging folder. That folder holds an
**unencrypted** copy of the database while the backup runs; if a backup fails, the script stops and
keeps it for inspection: remove it yourself afterwards (`docker compose --profile backup
run --rm --no-deps --entrypoint rm backup -r /staging/<folder>`).

The same by hand, the second command only once the first has printed its snapshot line:

```sh
S=$(date -u +%Y%m%d-%H%M%S)
docker compose --profile backup run --rm backup backup /var/lib/rhythm/archive /staging/$S
# The snapshot is saved and read back: remove the unencrypted copy.
docker compose --profile backup run --rm --no-deps --entrypoint rm backup -r /staging/$S
```

**Schedule it** nightly with whatever the host offers: a cron line such as
`30 3 * * * /srv/rhythm/deploy/backups/backup.sh >> /var/log/rhythm-backup.log 2>&1`, a TrueNAS cron job
running the script as root ([deploy/truenas/](../deploy/truenas/README.md)), or the Windows Task Scheduler
running it through Git Bash. Check the log now and then: the last line of a good run is the snapshot
line.

**Keep a copy elsewhere.** The repository is encrypted, so copying its folder to another disk, another
computer or a cloud storage service (rsync, rclone, a TrueNAS replication or cloud sync task) exposes
nothing without the password. One copy off the premises protects against fire and theft.

**Old snapshots** stay until you remove them. Each snapshot comes from a new staging folder and
container, so tell restic to treat them as one series:

```sh
docker compose --profile backup run --rm --entrypoint restic backup \
  forget --group-by '' --keep-daily 7 --keep-weekly 5 --keep-monthly 12 --prune
```

Add `--dry-run` first to see what it would remove.

## The restore drill

A backup you have never restored is a hope, not a backup. Every few months, and after every upgrade
that changes the database, restore the newest snapshot into a **separate drill project** next to the
running install, look at it, and throw it away. The drill never touches the live install: it has its
own project name, Postgres, volumes and ports.

```sh
R=restore-$(date -u +%Y%m%d)
# The database's owner and name: rhythm_admin and rhythm, unless .env sets RHYTHM_DB_ADMIN_USER or RHYTHM_DB_NAME
# (configuration.md); then use those.
U=rhythm_admin DB=rhythm
# 1. The snapshot's files into a new folder, checked against their manifest ("verified_files").
docker compose --profile backup run --rm backup restore latest /staging/$R

# 2. A drill project: only compose.yaml (no install overrides, so its volumes are new Docker volumes, never
#    your data folders), no exposure profiles (or a second tunnel would serve the drill), its own ports.
D="env COMPOSE_FILE= COMPOSE_PROFILES= RHYTHM_APP_PORT=18080 RHYTHM_MCP_PORT=18181 docker compose -p rhythm-drill -f compose.yaml"
$D up -d --wait postgres

# 3. The database into the drill's new, empty Postgres, then the quarantine: it disables every token and
#    removes old grants, because an old backup can bring back access revoked since.
$D exec -T postgres pg_restore -U $U -d $DB --no-owner --no-acl --exit-on-error < data/backup-staging/$R/database.dump
$D exec -T postgres psql -U $U -d $DB -q -v ON_ERROR_STOP=1 < ../../engine/backup/restore-quarantine.sql

# 4. The archive into the drill's engine volume, owned by the engine's user (1654).
$D --profile cli run --rm --no-deps --user 0 -v engine-data:/var/lib/rhythm --entrypoint sh cli \
  -c "cp -R /work/data/backup-staging/$R/archive /var/lib/rhythm/ && chown -R 1654:1654 /var/lib/rhythm"

# 5. Compare with the live install: the drill's numbers equal the live ones at backup time, or are a little lower.
Q='SELECT (SELECT count(*) FROM resources), (SELECT count(*) FROM measurements), (SELECT count(*) FROM insights), (SELECT count(*) FROM dashboards)'
$D exec -T postgres psql -U $U -d $DB -At -c "$Q"
docker compose exec -T postgres psql -U $U -d $DB -At -c "$Q"

# 6. Start the drill's engine and app and look at it: http://127.0.0.1:18080
$D up -d --wait

# 7. Throw it away, including the unencrypted restore folder.
$D down -v
docker compose --profile backup run --rm --no-deps --entrypoint rm backup -r /staging/$R
```

Step 4 assumes the default staging folder (inside `deploy/compose/`, which the `cli` container sees
as `/work`). In step 6 the engine re-creates the app's token from `secrets/app_token`; harness tokens
stay disabled in the drill, as they would after a real restore. The local model downloads again.

Write down the date and the snapshot you restored. If any step fails, fix the backup before you need
it.

## Recovering for real

With the `rhythm` command, on this machine: `./rhythm restore <snapshot>`. On a new machine:
`./rhythm init`, replace `secrets/backup_password` with your offline copy, copy the repository to
`<data>/backups/restic` (on Linux owned by uid 70), `./rhythm up` once, then `./rhythm restore latest`
([cli/README.md](../cli/README.md#restore)). By hand instead:

On a new machine, or on this one after `docker compose -p <project> down` and removing the broken
volumes (`docker volume rm <project>_pgdata <project>_engine-data`, where `<project>` is the `COMPOSE_PROJECT_NAME` in
`.env`, `rhythm` unless you chose another; name it, never let Compose guess it: only after a drill of this snapshot
worked):

1. Get the repository at the same or a newer version, and your `rhythm.yaml` and `.env` back.
2. Make new `secrets/postgres_password`, `secrets/engine_db` and `secrets/app_token`
   ([deploy/compose/README.md](../deploy/compose/README.md)); put the **offline copy** of the backup
   password into `secrets/backup_password`, and point `RHYTHM_BACKUP_DIR` at the repository (or a copy
   of it).
3. Run steps 1 to 5 of the drill, with its `R`, `U` and `DB` lines, without `-p rhythm-drill` and the port settings
   (use `D="docker compose"`), so they fill the install's own new volumes.
4. `docker compose up -d --wait`. The migrate step applies any migrations newer than the
   backup; the engine re-creates the app's token principal.
5. Every harness, plugin and capture token is disabled: create a new one per AI client (`rhythm token create <name>`,
   with `--person <id>` for a client that spoke for a person) and update the clients, and one per plugin
   (`rhythm token create --role ingest --plugin <name>`) and capture connection (`rhythm capture connect <name>`), each
   after deleting its old file. `rhythm up` names only the harness token files that no longer work. Adapters whose secrets
   were lost need `rhythm connect <name>` again. A restore keeps memory scopes as they were: when the
   backup recorded `memory.scopes: on`, the engine refuses to start until `rhythm.yaml` says `on` too,
   and Family keeps the members, admins and source shares the backup holds
   ([dev/memory.md](dev/memory.md) sections 6.2 and 9).
6. Remove the restore folder (drill step 7, the `rm` line).

## When a restore stops, and what it undoes

`./rhythm restore` checks the restored folder before it changes anything, and refuses one that holds a file its manifest
does not list, a link, or an archive file that differs from the engine's own copy. If loading fails after that (the dump
does not load, the quarantine fails), the database it was loading into is dropped and the live one is untouched: the
stack starts again on it and the command says what stopped it. The restored folder, a plain copy of the database, is
removed either way. Retrying is safe; `cli/chaos.sh` runs each of these failures on a throwaway stack.

A restore puts the database back as it was at the backup, so every change since is undone, the ones you made for safety
too. A person suspended since is current again (their tokens stay disabled, as every token does, but a new one could be
made for them), a person taken out of Family since is a member again, a source share or a Family admin taken back since
is back, a capture connection disconnected since is connected again with its mirrors out of the trash, a person
offboarded since is as they were with their private memories out of the trash, and a memory deleted for good since is
back as the backup held it: in the trash, with 14 days or more from the restore, or not in the trash at all. Do those
steps again after the restore: `rhythm person suspend`, `rhythm person offboard`, `rhythm family remove`,
`rhythm share revoke`, `rhythm family unadmin`, `rhythm capture disconnect`, `rhythm delete-forever`. What lives outside
the database stays as it is now: secret files (a calendar's private link, an adapter's token) are not in a backup, so
one you reset since stays reset.
