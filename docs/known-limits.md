# Known limits

What this alpha does not do yet, and the risks worth knowing before you put your household's data in
it. Each item says what to do about it today. Nothing here is hidden behind a setting: it is how the
software stands.

## Before you rely on it

- **No outside security review.** The code has been reviewed by its authors only.
  [security.md](security.md) says what it protects and what it does not.
- **No login, and the household shares everything.** This is the design, not a gap: whoever can reach
  the app sees everything in the install. Decide who can reach it before you expose it
  ([security.md](security.md#the-model-no-login-the-network-is-the-gate)).
- **Alpha upgrades may need care.** Database migrations only go forward. Take a backup before every
  upgrade (`./rhythm upgrade` does), and read the [changelog](../CHANGELOG.md) first.

## A public address for your AI

If you put `/mcp` on the internet (the Cloudflare recipe, or any other), know that:

- **A token is the only gate, and tokens never expire.** The engine does not record when a token was
  last used, so a leaked token is hard to notice. Give each client its own token, revoke the ones you
  stop using (`./rhythm token revoke <name>`), and rotate them now and then.
- **The public address answers any valid token, not only AI tokens.** The app's own token is the
  powerful one: it is not counted by the write limit and `token revoke` cannot turn it off. Keep it on
  the server; never give it to a client.
- **The write limit is off by default.** Set `engine.writeLimit: on` for any install with a public
  address ([security.md](security.md#the-write-limit)), and add a rate-limit rule at your edge
  (Cloudflare or your proxy).

## Memories

Only with `memory.tools: on`.

- **A memory keeps its 20 most recent earlier versions.** A client that edits one memory more than 20
  times pushes its original wording out of the history. Only a backup brings it back.
- **The trash empties itself after 14 days.**

## Running it

- **Other containers on the stack's networks are trusted.** The app's server and Postgres answer to any
  container on the stack's `internal` network, and every exposure container joins it. Do not attach
  containers you do not trust to those networks.
- **A failed upgrade rolls the images back, not the database.** If an upgrade fails after its
  migrations ran, the older engine starts on the newer schema. If anything looks wrong, restore the
  backup the upgrade took first (`./rhythm restore`).
- **Backups are yours to schedule, copy and prune.** `./rhythm backup` makes an encrypted backup on the
  same machine. Nothing copies it elsewhere, removes old snapshots or warns you when the last one is
  old. Copy the repository off the machine, keep `secrets/backup_password` offline, and do the
  [restore drill](backups.md#the-restore-drill) once.
- **`./rhythm init --force` writes a fresh `.env`.** Check its folders afterwards, the backup folder
  above all, or the next backup starts a new repository somewhere else.
- **Images are built on your machine.** There are no published images yet, so `./rhythm upgrade --pull`
  has nothing to pull. Third-party images are pinned by version tag, three of them to a floating tag,
  and none by digest ([../deploy/README.md](../deploy/README.md#pinned-images)).

## Connections

- **Adapters and exposure recipes are not tested against real accounts.** The Google, Garmin,
  Tailscale, Cloudflare and TrueNAS steps are checked against fakes and `docker compose config` only.
- **The CareLink plugin is not published yet.** It is a separate, unofficial repository, and when it is out it can
  stop working whenever the vendor changes its service ([adapters/carelink.md](adapters/carelink.md)).

## Models and dependencies

- **The optional `bge-small` model's licence is not confirmed.** The default model (MiniLM) is
  Apache-2.0. The pinned export of `bge-small` carries no licence statement of its own, though the
  original model declares MIT ([licenses/README.md](licenses/README.md)). The engine downloads it only
  if you choose it.
- **Six advisories in build-time tools** (two low, four moderate), in the chain that builds the web
  app's offline support. None is in what runs
  ([dependency-license-review-20261109.md](dependency-license-review-20261109.md)).

## Not in this release

- **A release of the Android launcher.** Its source is in [apps/android-launcher](../apps/android-launcher/README.md)
  as a development build: you build a debug APK yourself, it has been used on one phone, and it cannot
  sign in through a front door that sends you to another site.
- **Per-person private memories in the app** (`app.viewers`) are built but refused by this version;
  `memory.scopes` is off by default.
