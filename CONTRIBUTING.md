# Contributing

Thank you for wanting to help. This page is what you need to make a change and get it accepted.
Please read the [Code of Conduct](CODE_OF_CONDUCT.md) first; security problems go to
[SECURITY.md](SECURITY.md), never to a public issue.

## Before you start

- **Small fixes** (a typo, a wrong sentence in the docs, an obvious bug): open a pull request.
- **Anything larger** (a feature, a new adapter, a change to a contract or the database): open an issue
  first and say what you want to do and why. It saves you building something that cannot be merged.
- **The project keeps things simple on purpose.** No new login system, queue, graph database or agent
  framework without a real need written down first.

## What you need

| For | You need |
|---|---|
| the repository's own checks | Node 22 or later |
| the app (`app/`) | Node 24 |
| the engine (`engine/`) | the .NET SDK named in `engine/global.json` |
| the engine's database tests, and running a stack | Docker with Compose 2.20 or later |

## Where things live

| Path | What |
|---|---|
| `app/` | the web app (React, Vite, TypeScript) and `app/server/`, its server |
| `engine/` | the .NET engine: catalog, graph, dashboards, insights, retrieval, MCP server, adapters, migrations, tests |
| `contracts/` | the agreement between app, engine, adapters and plugins: JSON Schemas and fictional fixtures. Change it with every side in mind |
| `deploy/` | the compose files and the install and exposure recipes |
| `harness/` | connecting AI clients over MCP, prompt templates, session hooks |
| `cli/`, `rhythm`, `rhythm.ps1`, `engine/src/Cli/` | the `rhythm` command: its guide, its end-to-end tests, the two wrappers and the commands inside the engine image |
| `docs/` | user docs; `docs/dev/` for how things work inside |
| `scripts/` | repository checks and their tests |

## Run the checks

All from the repository root. A pull request should pass every one that its change can affect; CI
runs them again.

| What | Command |
|---|---|
| Repository script tests | `npm test` |
| Scrub check, every tracked file | `node scripts/scrub-check.mjs --all --public-only` |
| App: tests, build and smoke | `npm --prefix app ci`, then `npm --prefix app run verify` |
| App: lint | `npm --prefix app run lint` |
| Engine: build | `dotnet build engine/Engine.slnx` (0 warnings expected) |
| Engine: tests without a database | `dotnet test engine/Engine.slnx` (the database tests are skipped, each saying so) |
| Engine: every test, with a throwaway database | `sh engine/test-db.sh` (or `.\engine\test-db.ps1`); needs Docker, removes its container after |
| Compose files | `docker compose -f deploy/compose/compose.yaml config` |
| The `rhythm` command on a real stack | `sh cli/e2e.sh` (see [cli/README.md](cli/README.md#testing)); give it its own project name, never an install's |

## Rules for what goes in the repository

- **Fictional examples only.** The people are `alex` and `sam`, the time zone is `Europe/London` or
  `UTC`, hosts end in `example.invalid`. Demo data is fictional and always labelled DEMO.
- **No secrets and no real data**, anywhere, fixtures included: no tokens, passwords, real names, real
  host names, real addresses, real health readings, or paths from your own machine.
- **Tests build secret-shaped samples at run time** (string concatenation), so the files themselves
  stay clean.
- **Never edit an applied migration.** Each migration's text is checksummed; add a new numbered file
  instead ([docs/dev/migration-compat.md](docs/dev/migration-compat.md)).
- **Docs and code move together.** A change to a setting, a tool, a command or a limit updates the
  page that describes it in the same pull request. Several tests hold the docs to the code.

### The scrub check

`scripts/scrub-check.mjs` keeps secrets and personal details out. It has public rules (secret
patterns, blocked file names, a 1 MiB size guard) and a private pattern file that only the
maintainers hold. You do not need that file:

```sh
git config core.hooksPath .githooks        # optional: run the check before each commit
git config rhythm.scrubPublicOnly true     # run the public rules alone
```

A finding names the file, line and rule. Fix the content; for a reviewed false positive add a line
to `scripts/scrub-allow.txt` with a reason.

## Commits and pull requests

- One logical change per commit. Subject line in the imperative, at most 72 characters ("Add the
  ingest schemas"). Body: what changed and why, wrapped at 72.
- Name no person, host, account or local path in a commit message.
- Say in the pull request what you tested and what you could not test.
- Commit as yourself. (Maintainers' own commits use a shared project identity; see
  [docs/dev/conventions.md](docs/dev/conventions.md).)

## Licence of your contribution

Rhythm of Life is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution you
agree that it is licensed under the same terms, as section 5 of the licence says. Do not copy code
from other projects into a pull request unless its licence allows it and you say where it came from;
adapted work is credited in [NOTICE](NOTICE).

## Ids in comments

Comments and tests cite ids such as `D-123`, `Q-045` and `SB-P5-04`. They refer to the project's
working notes from before its first release, which are not in this repository. You never need them:
each comment says what matters in its own words
([docs/dev/second-brain-parity/README.md](docs/dev/second-brain-parity/README.md#ids-you-will-meet)).
New work does not need ids of that kind; refer to the issue or pull request instead.
