# deploy/compose

Docker Compose is the only required deployment. It needs Compose 2.20 or later.

| File | What |
|---|---|
| `compose.yaml` | the entry point: includes the two component files; shared networks, the app token, the settings file |
| `engine.compose.yaml` | postgres, engine, adapter workers (one profile each), the `cli` |
| `app.compose.yaml` | the app |
| `rhythm.example.yaml` | example settings; copy to `rhythm.yaml` |
| `.env.example` | compose-level settings, names only; copy to `.env` |

Not committed, created per install by `rhythm init`: `rhythm.yaml`, `.env`, `secrets/` (one file per secret),
`data/`.

From the repository root, with nothing but Docker:

```sh
./rhythm init     # or .\rhythm.ps1 init in PowerShell; --no-input and flags for scripts
./rhythm up
```

`init` asks a few questions and writes the settings, `.env` and every secret, each owned by the user of the
service that reads it. `up` checks them, runs `docker compose up -d --wait` (Postgres, migrations, the engine and
the app, and any adapter profiles in `.env`), and prints the app URL, the MCP URL and the path of the harness token
file. Every command, and the compose calls behind them, is in [cli/README.md](../../cli/README.md).

`docker compose -f deploy/compose/compose.yaml up -d app` runs the app alone in demo mode. A bare
`docker compose ... up` of the whole stack before `init` stops at Postgres with "secrets/postgres_password is
missing: run ./rhythm init first"; Docker will have made empty folders where the secret files belong (open
question Q-017), and `init` replaces them.

Networks: `internal` (no internet, no published ports; Postgres lives only here) and `egress`
(outbound internet and published ports, bound to `RHYTHM_BIND`, default `127.0.0.1`). The compose
project is named `rhythm` unless `.env` names another (`COMPOSE_PROJECT_NAME`, which `rhythm init --project-name`
records), so an external plugin joins `rhythm_internal` and `rhythm_egress`, or `<project>_internal` and
`<project>_egress`.

Adapters that need a container use a profile named after the adapter. See `docs/configuration.md`
and `contracts/README.md` section 3.
