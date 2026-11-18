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

- **Tokens created without an expiry stay valid until revoked.** Give each client its own token, use
  `--expires-at` for time-limited access, and check last use with `./rhythm token list`.
- **Set a role filter for a public MCP host.** Without an `engine.hostRoles` entry, any valid token
  can reach its own role's routes there. Allow only `harness` at the public AI hostname and have
  the proxy preserve that host. Keep app and plugin access on the internal engine name.
- **The write limit is off by default.** Set `engine.writeLimit: on` for any install with a public
  address ([security.md](security.md#the-write-limit)), and add a rate-limit rule at your edge
  (Cloudflare or your proxy).

## Memories

Only with `memory.tools: on`.

- **History keeps the last copy of each wording.** After 20 earlier versions, Rhythm can prune duplicate
  states in the same scope and hold status. Wording lost before this change can be recovered only from a backup.
- **The trash empties itself after 14 days.**
  The brief flags five or more memories forgotten by one AI connection within ten minutes during the last
  48 hours. It counts memories still in the trash that the reader can restore.

## Running it

- **App-facing proxies are trusted.** Cloudflare, Tailscale and Caddy relay the entire app on
  the `edge` network and can reach its unauthenticated facade. The OpenAI tunnel and Cloudflare's MCP-only setup have
  no shared network with the app. Postgres has its own `database` network, separate from every proxy.
- **Recovering after a failed migration needs a restore.** Upgrade stops the stack and prints the exact
  pre-upgrade snapshot command. Restoring disables prior tokens and undoes data written since the backup.
- **Schedule and copy backups off-site.** Configure the nightly job, preview retention, keep the
  password offline and run a [restore drill](backups.md#the-restore-drill). Doctor checks snapshot age
  and shared filesystems; separate filesystems can still share the same physical disk.
- **Images are built on your machine.** There are no published images yet, so `./rhythm upgrade --pull`
  has nothing to pull. Third-party image defaults are pinned by manifest digest ([../deploy/README.md](../deploy/README.md#pinned-images)).

## Connections

- **Deployment coverage is limited.** One installation runs Rhythm on TrueNAS SCALE, serves the
  app over a tailnet, and uses a Cloudflare tunnel for MCP with ChatGPT and Claude. The Cloudflare
  Access app-login example and the full install procedure on a fresh NAS still need end-to-end
  verification. Automated adapter checks use fakes and deployment checks render the Compose files.
- **The CareLink plugin is not published yet.** It is a separate, unofficial repository, and when it is out it can
  stop working whenever the vendor changes its service ([adapters/carelink.md](adapters/carelink.md)).

## Models and dependencies

- **The app's build and test toolchain has outstanding npm advisories.** On 17 November 2026
  npm flagged 14 dependency entries (12 high and two moderate). The production dependency
  audit reported zero. The affected graph includes Vite and Vitest as well as the offline
  build plugin. [Dependency review](dependency-license-review-20261109.md#17-november-recheck)

## Not in this release

- **A release of the Android launcher.** Its source is in [apps/android-launcher](../apps/android-launcher/README.md)
  as a development build: you build a debug APK yourself, it has been used on one phone, and it cannot
  sign in through a front door that sends you to another site.
- **Per-person private memories in the app** (`app.viewers`) are built but refused by this version;
  `memory.scopes` is off by default.
