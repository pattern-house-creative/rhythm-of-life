# Rhythm of Life

A calm, ambient home screen for a household's life: health, notes, documents and the links between
them, drawn as a sky of stars. It runs on your own server with Docker, keeps your data there, and
lets the AI you already use (ChatGPT, Claude or any MCP client) read it, build living dashboards
from it and leave daily insights in it, over MCP.

![The home screen on a wide display, with the fictional demo data: two dashboards, the sky of stars and a daily insight](docs/images/home-desktop.png)

> **Alpha.** This is an early release for people comfortable running Docker on their own machine.
> It works and is used daily by one household, but it has had no outside security review, and
> upgrades between alpha versions may need care. Read [Known limits](docs/known-limits.md) first.

> **Not a medical device.** Rhythm of Life shows readings from your own devices and services for
> your information only. It does not diagnose, treat or give medical advice, and it must not be
> used to make treatment decisions. Always use your device's own display and your care team for
> those.

## What you get

- **A sky of your own things.** Notes, documents, CSV files of numbers and health readings each become
  a star. Links between them are drawn as lines: the links you wrote yourself, and "similar in meaning"
  links found by a small model that runs on your own machine.
- **A home screen for a wall, a laptop or a phone.** An installable web app with corner cards,
  dashboards and a daily insight. Every open copy is a named screen you, or your AI, can send things to.
- **Your AI, over MCP.** One endpoint, a token per client, 29 tools (30 with `engine.recall: on`; 41 more with `memory.tools: on`)
  to explore the graph, read health data, build dashboards, save insights and, if you turn it on, keep
  memories. There is no model inside the stack by default: your AI stays outside and connects in.
- **Your data stays with you.** Everything is in Postgres and a folder on your own disk. Encrypted
  backups, restore and upgrade with rollback are one command each.
- **Everything else is optional.** Garmin, Nightscout, Google Drive, a calendar, a Second Brain service
  and a local titles model are adapters, each off until you connect it.

A fresh install starts with fictional demo data, all labelled DEMO, so there is something to look at
before you connect anything.

<img src="docs/images/home-phone.png" alt="The same home screen on a phone" width="300">

## How it fits together

| Part | What it is |
|---|---|
| **app** | an installable web app (PWA), with a small server that is the only thing talking to the engine |
| **engine** | a .NET service on Postgres with pgvector: the catalog of everything you connect, the relationship graph, dashboards, insights and one MCP endpoint |
| **adapters** | every outside service is an optional, replaceable adapter |
| **your AI** | outside the stack, connecting to the engine's `/mcp` with its own token |

**There is no login inside Rhythm of Life, and the household shares everything.** Everyone you list in
an install sees all of its data. Who can reach it at all is decided by where it listens: this computer
only (the default), your home network, Tailscale, Cloudflare Access, or your own reverse proxy.
[docs/security.md](docs/security.md) explains the model and what never to expose.

## Quick start

You need Docker with Compose 2.20 or later, and nothing else. The first start builds the images on
your machine, which takes some minutes, and downloads one small model (about 23 MB).

```sh
git clone <this repository> rhythm-of-life && cd rhythm-of-life
./rhythm init     # a few questions; writes deploy/compose/rhythm.yaml, .env and secrets/
./rhythm up       # builds and starts Postgres, the engine and the app, then prints where to go
```

On Windows PowerShell use `.\rhythm.ps1 init` and `.\rhythm.ps1 up`.

`up` prints three things:

- the app's address, `http://127.0.0.1:8080` by default;
- the MCP address for your AI client, `http://127.0.0.1:8181/mcp`;
- the path of a token file to give that client. The token itself is never printed.

Then:

| To do this | Run or read |
|---|---|
| check the install | `./rhythm doctor` |
| see what you can connect | `./rhythm connect` |
| connect ChatGPT, Claude or another MCP client | [harness/](harness/README.md) |
| reach it from other devices | [deploy/exposure/](deploy/exposure/README.md) |
| back it up | `./rhythm backup`, and keep `secrets/backup_password` offline ([docs/backups.md](docs/backups.md)) |
| upgrade | `./rhythm upgrade` ([cli/README.md](cli/README.md#upgrade)) |
| stop it | `./rhythm down` |

[docs/operations.md](docs/operations.md) is a full first walk on sample data: the install, the app, an
AI client, memories, a backup, a restore and an upgrade.

## Your own stars

Your own things need no account anywhere.

- **Notes:** `./rhythm init --notes <folder>` points the `notes` adapter at a folder of Markdown notes.
  Wikilinks, folders and tags become links and projects ([notes](docs/adapters/notes.md)).
- **Numbers:** CSV files in the import folder become data stars ([csv](docs/adapters/csv.md)).
- **Documents:** PDFs, text and Markdown, added in the app or dropped in the import folder's
  `documents/` folder, open in the reader as themselves ([documents](docs/adapters/documents.md)).

To try all three with the fictional samples in `demo/`:

```sh
./rhythm init --notes ../../demo/notes     # paths in .env start at deploy/compose/
# then set RHYTHM_IMPORT_DIR=../../demo/import in deploy/compose/.env
./rhythm up
```

## Optional connections

Each is off until you connect it with `./rhythm connect <adapter>`; [docs/](docs/README.md) has a page
for each.

| Adapter | Brings |
|---|---|
| Nightscout | a live glucose feed |
| Garmin | sleep, heart rate, steps and activities, from a Personal API key or exported files |
| Google Drive | your documents, and live Sheets and Docs for dashboards (your own Google Cloud project) |
| Calendar | private calendar links |
| Second Brain | memories from a hosted Second Brain service, read by reference or imported |
| Titles | short titles for stars from a local model (Ollama) |
| [CareLink plugin](docs/adapters/carelink.md) | a separate, optional, unofficial plugin; not published yet |

Ways to reach an install from elsewhere: [Tailscale](deploy/exposure/tailscale/README.md),
[Cloudflare Tunnel and Access](deploy/exposure/cloudflare/README.md),
[OpenAI's Secure MCP Tunnel](deploy/exposure/openai-tunnel/README.md) for ChatGPT, a
[reverse proxy](deploy/exposure/reverse-proxy/README.md), and a [TrueNAS guide](deploy/truenas/README.md).

**How far these are tested.** The core (notes, CSV, documents, MCP, backup, restore, upgrade) is tested
end to end on real stacks with fictional data. The Tailscale, Cloudflare, Google, Garmin and TrueNAS
steps are checked against fakes and `docker compose config` only, and the OpenAI tunnel against a local
stack cut off from the internet; none against real accounts or hardware. Treat an exposure recipe as a
starting point and check what it exposes yourself.

## Documentation

| Read | For |
|---|---|
| [docs/operations.md](docs/operations.md) | running an install: the first walk, every setting that is off by default, limits, what runs on its own |
| [docs/configuration.md](docs/configuration.md) | every setting in `rhythm.yaml` and `.env`, and every secret file |
| [docs/security.md](docs/security.md) | who can see what, tokens, and what never to expose |
| [docs/backups.md](docs/backups.md) | backups, the restore drill, recovering on a new machine |
| [docs/known-limits.md](docs/known-limits.md) | what this alpha does not do yet, and risks to know about |
| [cli/README.md](cli/README.md) | every `rhythm` command |
| [harness/](harness/README.md) | connecting AI clients, the daily insight template, session hooks |
| [contracts/README.md](contracts/README.md) | the app, MCP, adapter and plugin contracts |
| [docs/dev/](docs/README.md#contributors) | how the engine, memories, screens and Studio work inside |

## Repository layout

```
app/         the web app and its server
engine/      the knowledge engine, adapters, migrations, tests
contracts/   app to engine contract, MCP tools, adapter and plugin contracts, JSON Schemas
deploy/      compose (the only required deployment), TrueNAS, Vercel and exposure recipes
harness/     connecting ChatGPT, Claude or any MCP client
demo/        fictional seed data
cli/         the rhythm command's guide and end-to-end tests
docs/        configuration, adapters, security, backups and contributor docs
scripts/     repository checks and their tests
```

## Contributing and security

- Changes are welcome: start with [CONTRIBUTING.md](CONTRIBUTING.md) and the
  [Code of Conduct](CODE_OF_CONDUCT.md).
- Found a security problem? Please report it privately: [SECURITY.md](SECURITY.md).
- What changed in each version: [CHANGELOG.md](CHANGELOG.md).

## Licence

[Apache License 2.0](LICENSE). Copyright 2026 Rhythm of Life contributors. See [NOTICE](NOTICE) for
what was adapted from other projects, and the [third-party licence inventory](docs/licenses/README.md).

Garmin, Nightscout, Google, Tailscale, Cloudflare, OpenAI, ChatGPT, Claude, TrueNAS and CareLink are
names of their owners' products. Rhythm of Life is not affiliated with or endorsed by any of them.
