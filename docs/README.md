# docs

Start with the repository's [README](../README.md): what Rhythm of Life is, what you can ask your AI to do with it, and
the two commands that install it.

## If you are new

1. [operations.md](operations.md#a-first-walk-on-sample-data-only): a first walk on fictional sample data, from the
   install to a backup and an upgrade. It needs Docker and no account anywhere.
2. [../harness/README.md](../harness/README.md): connect ChatGPT, Claude or another MCP client, then set up the daily
   insight.
3. [known-limits.md](known-limits.md) and [security.md](security.md): read both before you put your own data in, or
   reach the install from another device. This is an alpha with no outside security review.

## Running an install

- [../cli/README.md](../cli/README.md): every `rhythm` command (`init`, `up`, `connect`, `doctor`, `token`,
  `backup`, `restore`, `upgrade`) and what it runs underneath.
- [operations.md](operations.md): the operator's guide: a first walk on sample data, every setting that is off until you
  turn it on, the connectors that need an outside account, schedules, limits, freshness and privacy opt-ins.
- [configuration.md](configuration.md): `rhythm.yaml`, `.env`, secret files and their owners.
- [security.md](security.md): the no-login model, what is exposed by default, tokens and roles, what never to expose.
- [models.md](models.md): every model and its job (search, titles, your AI client), and how to see each one's state.
- [backups.md](backups.md): encrypted backups, an offline copy of the password, the restore drill, recovering.
- [known-limits.md](known-limits.md): what this alpha does not do yet, and the risks to know about before you rely on it.
- [../deploy/compose/README.md](../deploy/compose/README.md): the compose files, networks and profiles.
- [../deploy/exposure/README.md](../deploy/exposure/README.md): reaching it from other devices
  ([Tailscale](../deploy/exposure/tailscale/README.md), [Cloudflare](../deploy/exposure/cloudflare/README.md),
  [a reverse proxy](../deploy/exposure/reverse-proxy/README.md), [the OpenAI tunnel for
  ChatGPT](../deploy/exposure/openai-tunnel/README.md)).
- [../deploy/truenas/README.md](../deploy/truenas/README.md): TrueNAS SCALE as a custom app.
- [../deploy/vercel/README.md](../deploy/vercel/README.md): the app on a hosted front end (optional).

## Adapters

One page per adapter: what it does, what to set up first, its settings and limits.

- No account needed, on in the example settings (`rhythm init` turns on csv and documents, and notes when given
  `--notes <folder>`): [notes](adapters/notes.md), [csv](adapters/csv.md), [documents](adapters/documents.md).
- Optional: [nightscout](adapters/nightscout.md), [garmin](adapters/garmin.md), [drive](adapters/drive.md),
  [second-brain](adapters/second-brain.md), [titles](adapters/titles.md), [calendar](adapters/calendar.md).
- Exposure: [tailscale](adapters/tailscale.md), [cloudflare](adapters/cloudflare.md),
  [openai-tunnel](adapters/openai-tunnel.md).
- The [CareLink plugin](adapters/carelink.md): a separate, optional, unofficial plugin; not published yet.

## Your AI

- [../harness/README.md](../harness/README.md): connecting an MCP client with its own token
  ([ChatGPT](../harness/chatgpt/README.md), [Claude and other MCP clients](../harness/generic-mcp/README.md)) and
  the [daily insight template](../harness/chatgpt/daily-insight.md).
- [../harness/hooks/README.md](../harness/hooks/README.md): session hooks for coding agents, off until you install them.

## Contributors

- [../CONTRIBUTING.md](../CONTRIBUTING.md): making a change: what you need, the checks to run, the content rules.
- [dev/conventions.md](dev/conventions.md): for maintainers: branches, the shared commit identity, the private scrub patterns, releases.
- [dev/importing.md](dev/importing.md): how code from the private version comes in.
- [dev/engine.md](dev/engine.md): the engine's services, roles, tools, scheduler and adapters.
- [dev/adapters.md](dev/adapters.md): writing an adapter.
- [dev/migration-compat.md](dev/migration-compat.md): migration checksums and an existing install's overlay.
- [dev/connection-ideas.md](dev/connection-ideas.md): ways the map could find more links, kept for later.
- [dev/screens.md](dev/screens.md): live screens, the contract between the engine, the app and the docs.
- [dev/studio.md](dev/studio.md): Studio, an AI client drafting a dashboard live on one screen before it is saved.
- [dev/second-brain-parity/](dev/second-brain-parity/README.md): the design notes of the memory and search work: measurements, rules and verdicts.
- [dev/memory.md](dev/memory.md): memory scopes, identity and ownership: who may read and change what once memories can
  be private, the `memory.scopes` switch, what each read path filters, and who owns each record.
- [../contracts/README.md](../contracts/README.md): the app, MCP, adapter and plugin contracts, with JSON Schemas and
  fictional fixtures.
- [../app/server/README.md](../app/server/README.md): the app's server facade.
