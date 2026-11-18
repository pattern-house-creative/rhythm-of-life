# TrueNAS SCALE

Running the compose stack as a TrueNAS SCALE custom app. TrueNAS is one install target, not a
dependency: the stack is the same `deploy/compose/` files, plus one small override here that keeps the
data on your own encrypted dataset. The engine schedules its own map refresh, so the only host job is
the nightly backup.

Needs TrueNAS SCALE 24.10 or later (apps run on Docker Compose). One household runs Rhythm on
TrueNAS SCALE with its app accessible over a tailnet and a Cloudflare tunnel for MCP. The example
paths, owners and YAML below are checked with Compose. Substitute your own pool, backup pool
and app name for `<pool>`, `<backup-pool>` and `rhythm`.

| File | What |
|---|---|
| `README.md` | this page |
| `truenas.compose.yaml` | binds the `pgdata` and `engine-data` volumes to folders on your dataset |
| `backup.compose.yaml` | a standalone nightly backup job for a separately managed installation |

### Nightly backups for a separately managed app

Use `backup.compose.yaml` when the app already has its own service names, volumes and database.
This Compose project contains just one disposable job attached to the installed database's
existing network. Its run command has no dependencies to start. From the repository root,
build the job image with `docker build -t rhythm-backup:<release-tag> engine/backup`
and set `RHYTHM_BACKUP_IMAGE_TAG` to that release tag.

Create a private environment file outside the repository using the variable names in
`backup.compose.yaml`. Set the existing database network, host, name and user. Supply paths to
the database and restic password files. Mount the engine archive and the source provider's
original files read-only. Set the repository path to the existing restic folder containing
`config`. The backup user (uid 70) needs read access to both password files and source trees
and write access to the repository and the encrypted staging folder.
Choose a consistent `RHYTHM_BACKUP_SERIES` and leave retention off until its policy is approved.

From the repository root, with the absolute path to that environment file:

```sh
docker compose --env-file /path/to/backup.env -p rhythm-nightly -f deploy/truenas/backup.compose.yaml config -q
docker compose --env-file /path/to/backup.env -p rhythm-nightly -f deploy/truenas/backup.compose.yaml run --rm --no-deps -T --name rhythm-nightly-job backup
```

Keep the fixed container name for each installation so overlapping runs refuse to start a
second job. Confirm the backup image, mounts, source coverage and snapshot ID before switching
the scheduler. Run outside the import window so the provider originals stay stable during
the copy. The job includes them under `originals/` in the same verified snapshot as the
database dump and archive. It fails on an empty originals directory or a symbolic link.
Other source folders covered by the current job must also be accounted for before switching.

Restore the candidate snapshot to a separate drill folder, verify its manifest and load the
dump into an isolated database. Compare the archive and provider originals to known files.
Recover `originals/` to the provider's own storage as a separate step. The engine's database
load handles only its database and archive. Keep the previous schedule definition and its last
good snapshots. Switch the nightly schedule after the drill passes and verify the first scheduled
run. Revert to the previous job if any source is missing or the drill fails.

## 1. Datasets and folders

One encrypted dataset holds everything; the backup repository goes elsewhere.

| Path | What | Owner (uid:gid) | Mode |
|---|---|---|---|
| `<pool>/rhythm` | an encrypted dataset (Generic preset, so numeric owners apply) | root | 0755 |
| `/mnt/<pool>/rhythm/repo` | this repository: a `git clone`, or an unpacked release | root | |
| `/mnt/<pool>/rhythm/repo/deploy/compose/secrets` | the install's secrets | root | 0700 |
| `/mnt/<pool>/rhythm/repo/deploy/compose/data/backup-staging` | the backup's temporary plain copy | 70:70 | 0700 |
| `/mnt/<pool>/rhythm/pgdata` | Postgres's data | 999:999 | 0700 |
| `/mnt/<pool>/rhythm/engine` | the engine's archive, state and downloaded model | 1654:1654 | 0750 |
| `/mnt/<pool>/rhythm/notes` (optional) | your Markdown notes; the engine only reads | any, readable by 1654 | |
| `/mnt/<backup-pool>/rhythm-backups` | the restic repository (another pool or disk if you can) | 70:70 | 0700 |

Child datasets for `pgdata` and the backups are optional (separate snapshots and quotas); plain
folders work. Docker's own volumes would live in TrueNAS's apps dataset instead, which is why
`truenas.compose.yaml` binds the two data volumes here.

The users are the containers' own (more in [docs/security.md](../../docs/security.md#secret-files)):

| uid | Container |
|---|---|
| 999 | `postgres` |
| 1654 | `engine`, `migrate`, `cli` |
| 1000 | `app` |
| 70 | `backup` |
| 65532 | `cloudflared` (Cloudflare recipe), `openai-tunnel` (OpenAI tunnel recipe) |
| 0 | `tailscale` (Tailscale recipe) |

From a root shell (`sudo -i`):

```sh
cd /mnt/<pool>/rhythm
git clone <repository> repo           # or unpack a release into repo/
install -d -m 0700 -o 999 -g 999 pgdata
install -d -m 0750 -o 1654 -g 1654 engine
install -d -m 0700 -o 70 -g 70 repo/deploy/compose/data/backup-staging /mnt/<backup-pool>/rhythm-backups
install -d -m 0700 repo/deploy/compose/secrets
install -d -m 0755 repo/deploy/compose/secrets/adapters
```

## 2. Settings and secrets

`./rhythm init --project-name ix-rhythm` in `/mnt/<pool>/rhythm/repo/`, as root, writes `rhythm.yaml`,
`.env` (with `COMPOSE_PROJECT_NAME` and `RHYTHM_VERSION`) and every secret with the owner of the container that
reads it ([cli/README.md](../../cli/README.md)); then add the other TrueNAS lines of step 2 below to its `.env`.
The `rhythm` commands run Compose in `deploy/compose/`, so they use that `.env`'s `COMPOSE_FILE` and
`COMPOSE_PROJECT_NAME` too. Or by hand, in `/mnt/<pool>/rhythm/repo/deploy/compose/`, still as root:

1. `cp rhythm.example.yaml rhythm.yaml` and edit it: instance name, people, time zone, adapters
   ([docs/configuration.md](../../docs/configuration.md)).
2. `.env`, the compose-level settings. Besides the usual ones, these make `docker compose` in this
   folder act on the same project TrueNAS runs, with the same files:
   ```sh
   RHYTHM_VERSION=<release or tag>
   RHYTHM_CONFIG_FILE=./rhythm.yaml
   COMPOSE_PROJECT_NAME=ix-rhythm
   COMPOSE_FILE=compose.yaml:../truenas/truenas.compose.yaml
   RHYTHM_PGDATA_DIR=/mnt/<pool>/rhythm/pgdata
   RHYTHM_ENGINE_DATA_DIR=/mnt/<pool>/rhythm/engine
   RHYTHM_BACKUP_DIR=/mnt/<backup-pool>/rhythm-backups
   # optional: RHYTHM_NOTES_DIR=/mnt/<pool>/rhythm/notes
   # reaching it: RHYTHM_BIND (below), COMPOSE_PROFILES=tailscale, cloudflare or openai-tunnel
   ```
3. The secrets:
   ```sh
   openssl rand -hex 24 > secrets/postgres_password
   printf 'Host=postgres;Database=rhythm;Username=rhythm_engine;Password=%s' "$(openssl rand -hex 24)" > secrets/engine_db
   openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n' > secrets/app_token
   openssl rand -base64 32 > secrets/backup_password
   find secrets -type f -exec chmod 0444 {} +
   ```
   The folder (0700, root) keeps everyone else on the NAS out; the files are 0444 so each container
   can read the ones it is given, since a file mounted into a container is opened there directly.
   Store a copy of `backup_password` offline now ([docs/backups.md](../../docs/backups.md)).
4. Check: `docker compose config -q` prints nothing when the files are valid.

## 3. Build the images

The images are built from the repository, on the NAS (it needs the internet for the base images and
packages, and takes a few minutes):

```sh
docker compose --profile backup build --pull
```

`--pull` fetches the current base images (.NET, Node, Postgres) even when the NAS has older copies:
their tags follow upstream's patch releases, and a build without it keeps the copy Docker pulled first.

## 4. Install the custom app

In the TrueNAS web interface, open Apps, Discover Apps, and choose Install via YAML. Name it `rhythm`
and paste (with your pool):

```yaml
include:
  - path:
      - /mnt/<pool>/rhythm/repo/deploy/compose/compose.yaml
      - /mnt/<pool>/rhythm/repo/deploy/truenas/truenas.compose.yaml
    env_file: /mnt/<pool>/rhythm/repo/deploy/compose/.env
```

TrueNAS runs it as the compose project `ix-rhythm` (hence `COMPOSE_PROJECT_NAME` above): Postgres
starts, `migrate` runs once and exits, then the engine and the app start. The Apps screen shows them;
from the shell, `docker compose ps` in `deploy/compose/` shows the same. An external plugin joins the
networks `ix-rhythm_internal` and `ix-rhythm_egress` (not `rhythm_*`). Keep app-facing proxies
on `ix-rhythm_edge`. If Cloudflare serves only MCP, use `RHYTHM_CLOUDFLARE_NETWORK=egress`
so it cannot directly reach the app. Only processes that need a database connection belong on `ix-rhythm_database`.
When upgrading an installation with a separately managed backup that connects directly to Postgres,
move its database connection to this new network and verify a backup and restore.

If the dataset is locked after a reboot (passphrase encryption), unlock it, then start the app.

## 5. Reaching it, and tokens

- **Home network:** set `RHYTHM_BIND=0.0.0.0` (or the NAS's own address) and open
  `http://<nas address>:8080`; to open it by a name instead, add the name to `app.allowedHosts` in
  `rhythm.yaml`. TrueNAS's own web interface keeps ports 80 and 443; choose free ports
  with `RHYTHM_APP_PORT` and `RHYTHM_MCP_PORT` if 8080 or 8181 are taken.
- **Away from home:** the [Tailscale](../exposure/tailscale/README.md) or
  [Cloudflare](../exposure/cloudflare/README.md) recipe; add its profile to `COMPOSE_PROFILES`. The
  Caddy example needs ports 80 and 443, so on TrueNAS give it other ports or move the web interface.
- **AI clients:** a token each, as root, from `repo/`:
  ```sh
  ./rhythm token create my-assistant
  ```
  Then follow [harness/](../../harness/README.md).

After editing `rhythm.yaml` or `.env`, restart what reads it: `docker compose up -d` recreates only
the services whose settings changed, and `docker compose up -d --force-recreate --no-deps engine` re-reads
`rhythm.yaml` (Compose does not notice a change to it by itself).
Saving the app in the TrueNAS interface recreates every container.

## 6. Backups

- **Nightly:** System, Advanced Settings, Cron Jobs, Add: command
  `/mnt/<pool>/rhythm/repo/deploy/backups/backup.sh`, run as `root`, daily at a quiet hour. Run
  `docker compose --profile backup run --rm --entrypoint restic backup init` once first.
  ([docs/backups.md](../../docs/backups.md))
- **Off the NAS:** a replication task or a Cloud Sync task for `/mnt/<backup-pool>/rhythm-backups`.
  The repository is encrypted, so the copy reveals nothing without the password.
- **Snapshots** of `<pool>/rhythm` are useful before an update, but a snapshot of a running database
  is not a substitute for the dumps.
- **The restore drill** works here as written; its drill project uses Docker volumes, never these
  folders.

## 7. Updates

1. Back up: `../backups/backup.sh`.
2. Get the new version into `repo/` (`git fetch --tags` and `git checkout <tag>`, or unpack the new
   release), set `RHYTHM_VERSION` to it in `.env`, and build: `docker compose --profile backup build --pull`.
3. Switch: `docker compose up -d`. `migrate` applies new migrations before the engine starts; only
   services whose image or settings changed are recreated, and a pinned image the new version moved
   (Postgres, for example) is pulled first. After a new Postgres image, update the pgvector extension
   ([Pinned images](../README.md#pinned-images)). To change just one service, use
   `docker compose up -d --no-deps <service>`.
4. Check: `docker compose ps` (all healthy), the app opens, `GET /api/health` says `live`. Refresh
   ChatGPT's connector so it sees the new tools.
5. Roll back by checking out the old version and repeating steps 2 and 3. Migrations only go forward:
   if the update applied one, run the old version against the backup from step 1 (restore it as in
   [docs/backups.md](../../docs/backups.md#recovering-for-real)), not against the newer database.

`./rhythm upgrade` does these steps from the shell too (backup, build, recreate, check, roll back on failure);
saving the app in the TrueNAS interface afterwards is not needed, since it is the same compose project. It also
names the pinned images due a check and any image an `.env` line such as `RHYTHM_POSTGRES_IMAGE` sets, which no
update moves ([Pinned images](../README.md#pinned-images)).

## Undo

Stop and delete the app in the Apps screen, then remove `/mnt/<pool>/rhythm` (or the dataset) and the
backup repository when you no longer need them. `docker volume rm ix-rhythm_pgdata ix-rhythm_engine-data`
removes the two volume definitions; the data is in the folders, not in them.
