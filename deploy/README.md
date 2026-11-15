# deploy

- `compose/`: the stack (the only required deployment).
- `exposure/`: reaching it from other devices and from hosted AI: Tailscale, Cloudflare Tunnel and
  Access, a reverse proxy (Caddy example).
- `backups/`: `backup.sh`, one encrypted backup for cron or a TrueNAS cron job
  ([docs/backups.md](../docs/backups.md)).
- `truenas/`: running it as a TrueNAS SCALE custom app.
- `vercel/`: an optional hosted front end.
- `image-pins.txt`: every third-party image and how it moves ([Pinned images](#pinned-images)).

## Pinned images

[`image-pins.txt`](image-pins.txt) lists every third-party image the stack runs or builds from, and how each one
moves. `npm test` checks the list against the compose files, the Dockerfiles and the docs.

- **Floating tags**, the bases of the images built here (`dotnet/aspnet:10.0-noble`, `node:24-alpine`,
  `postgres:17-alpine` for `pg_dump`): upstream pushes each .NET, Node and Postgres patch release under the same
  tag, and a build takes it only with `--pull`. `./rhythm upgrade` and the TrueNAS steps build with `--pull`; a
  plain `docker compose build` keeps the copy Docker pulled first.
- **Exact versions** (Postgres with pgvector, cloudflared, the OpenAI tunnel client, Tailscale, Caddy, Ollama, the
  .NET SDK): nothing moves them on its own, not `rhythm upgrade` and not `docker compose pull`. `./rhythm upgrade`
  names each one the install builds from or runs that was last checked over 90 days ago, and each image the install
  runs that the list does not have: an `RHYTHM_*_IMAGE` line in `.env`, which only you move (remove the line to
  follow the default again). Cloudflare supports only `cloudflared` releases within a year of its newest one, so
  check that pin at least every few months.

To bump one: take upstream's newest release in the same line (the same major version, for Postgres the same Debian
release too), set it where the list says and wherever the docs name it (`npm test` names the stale mentions), and
give its line in `image-pins.txt` today's date. A check that finds nothing newer changes only the date. An
install takes the new default at its next `./rhythm upgrade` (or the TrueNAS update steps): `up` pulls an image
it does not have yet.

Postgres: a minor release (17.x) runs on the existing data folder. Back up first, and keep the Debian release of
the tag (`-bookworm`): a newer C library sorts some text differently, and every text index would need a `REINDEX`.
After the switch, bring the pgvector extension up to the new library, from `deploy/compose/`:

```sh
docker compose exec postgres psql -U rhythm_admin -d rhythm -c 'ALTER EXTENSION vector UPDATE;'
```

A new major version (18) does not run on the old data folder: it needs a dump and a restore.
