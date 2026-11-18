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

[`image-pins.txt`](image-pins.txt) lists every third-party base and service image. Each default
includes the upstream tag and the SHA-256 digest of its multi-platform manifest index. A rebuild
uses that exact index even if upstream has since moved the tag. Operators can override service
images in `.env`. The upgrade command reports overrides absent from the inventory and reminds
you when a pin you use was last checked more than 90 days ago.

To review a pin, check upstream's releases in the same major line. For Postgres keep the same
Debian release too. Run `docker buildx imagetools inspect IMAGE:TAG` and read the top-level digest.
Check that the index still includes each supported Linux architecture. Update the tag and digest
where the inventory specifies and in the inventory itself. Update any other references reported
by `npm test`, then set the row's check date. A check that confirms the existing pin updates only
the date. Installations take the reviewed defaults on their next upgrade. Cloudflare supports
cloudflared releases within one year of its newest release, so review that pin every few months.

Postgres: a minor release (17.x) runs on the existing data folder. Back up first, and keep the Debian release of
the tag (`-bookworm`): a newer C library sorts some text differently, and every text index would need a `REINDEX`.
After the switch, bring the pgvector extension up to the new library, from `deploy/compose/`:

```sh
docker compose exec postgres psql -U rhythm_admin -d rhythm -c 'ALTER EXTENSION vector UPDATE;'
```

A new major version (18) does not run on the old data folder: it needs a dump and a restore.
