# deploy/vercel

Optional: host the app's front end and its facade on Vercel instead of the `app` container. The
default install runs the app next to the engine (`deploy/compose/`), where none of this is needed.
Use it when you want the app on a public address with Vercel's own login in front, while the engine
stays at home.

The same web-standard handlers run in both places: `app/api/*.ts` are thin Vercel Functions around
`app/server/routes.ts`, which the Node server (`app/server/node.ts`) also runs.

## What you need first

- The engine reachable from the internet over HTTPS, through an exposure recipe
  (`deploy/exposure/`), for example a Cloudflare Tunnel with Cloudflare Access service-token
  authentication in front of the engine's `/mcp`.
- The engine's `engine.allowedHosts` including that public host name. If AI clients use a
  harness-only hostname, give the hosted facade its own protected route and list its hostname with
  `roles: [app]` under `engine.hostRoles`. The proxy forwards the original Host to the engine.
- The app token from `deploy/compose/secrets/app_token` (created by `rhythm init`).

## Project settings

| Setting | Value |
|---|---|
| Root Directory | `app` |
| Framework | Vite (from `app/vercel.json`) |
| Build Command | `npm run verify` (tests, build, smoke check; from `app/vercel.json`) |
| Output Directory | `dist` |
| Deployment Protection | Vercel Authentication (or another login), scope **All Deployments** |

There is no login inside Rhythm of Life: on a public address, Vercel's own authentication is the
only gate. Choose the scope **All Deployments** (Settings, Deployment Protection; every plan has it).
Vercel's recommended **Standard Protection** protects every deployment *except* the production
domains: `<project>.vercel.app` and any custom domain stay open to anyone, and a production
deployment with the engine variables below hands every reading and document to them. If only
Preview deployments may be protected, give Production no engine variables at all (it then runs in
demo mode). Do not create share or bypass links for protected deployments.

## Environment variables

Set them in the Vercel project (Production and, if you use it, Preview). The secret values are
Vercel's encrypted environment values, never `VITE_*` variables: the browser never sees them.

| Name | Secret | Value |
|---|---|---|
| `RHYTHM_ENGINE_URL` | no | the engine's public base URL, for example `https://rhythm-engine.example.invalid` (the app appends `/mcp`) |
| `RHYTHM_APP_TOKEN` | yes | the app token. Read only when `RHYTHM_APP_TOKEN_FILE` is unset; the container uses the file instead |
| `RHYTHM_CONFIG_YAML` | no | the text of your `rhythm.yaml` (at least `instance`, `timezone`, `people`), because a function cannot mount the file |
| `RHYTHM_CF_ACCESS_CLIENT_ID` | no | Cloudflare Access service token id, when Access protects the engine |
| `RHYTHM_CF_ACCESS_CLIENT_SECRET` | yes | its secret; both parts or neither |
| `RHYTHM_CATALOG_PROVIDER` | no | only when the engine sets it (an install that kept an older engine's ids, `docs/dev/migration-compat.md`): the same value, so the app finds the engine's data stars, saved documents and dashboards. Unset means `rhythm` |

Without `RHYTHM_ENGINE_URL` (or `engine.url`) and the token, the deployment runs in demo mode with
fictional data labelled DEMO.

## Limits

Each file in `app/api/` deploys as one Vercel Function, and the Hobby plan takes at most twelve in a deployment (a
deploy with more fails at "Deploying outputs"). The recipe has twelve: `/api/status`, `/api/sync` and `/api/memories`
share one, `app/api/sources.ts`, through the `rewrites` in `app/vercel.json` (`?route=status`, `?route=sync`,
`?route=memories`), and answer as their own routes do. Keep those rewrites if you change the file. A new route joins an existing function the same way rather
than adding a thirteenth; `app/tests/vercel-functions.test.ts` counts them and checks that every route of the app's
server reaches a function.

Vercel Functions take and answer at most 4.5 MB. So a document added here may be smaller than the 5 MiB the app
allows, and the reader shows a document's original file (`/api/document-file`) only up to 4 MiB: a larger one shows
its saved text, saying so, and opens in full from the install's own address
([docs/adapters/documents.md](../../docs/adapters/documents.md)).

The app reads those files from the engine's `GET /app/documents/<id>`, beside `/mcp` (`contracts/README.md` 1.5). An
exposure that passes only `/mcp`, such as the Cloudflare recipe's MCP host name, leaves every document showing its
saved text. To show the files, let that path through too, for example a second public hostname rule on the same host
name with the path `^/app/documents/` to `HTTP` `engine:8080` (and the same Access application). It answers only the app
token: every other token gets 403.

Each open screen waits for its next command with one request held open: `/api/screens` holds it up to 35 seconds (the
engine's own wait is at most 25 inside it, and the browser gives up after 40). So `app/vercel.json` sets that
function's `maxDuration` to 60 seconds, `"functions": {"api/screens.ts": {"maxDuration": 60, "supportsCancellation":
true}}`, the last of a chain in which each limit is longer than the one before, so the engine's wait always ends
first and the screen gets a normal answer. Keep it if you change the file: a function cut off sooner fails the held
request, and screens pick up commands late. Each open screen keeps such a request waiting most of the time, which
counts toward the project's function usage.

Its `"supportsCancellation": true` means a request the browser gives up on (a phone locked, a tab hidden) ends its
function and its engine call at once: Vercel passes a disconnect on to a function only when it opts in. Without it,
an abandoned wait would keep running for up to 35 seconds and could claim a command meant for that screen, which is
then never shown. Nothing in that route has to finish after the browser leaves.

## Checks after deploying

- Signed out (a private window, or curl), every address of the project asks for a login: the
  production domain, `<project>.vercel.app`, every custom domain and the Preview alias you use.
  `curl -s -o /dev/null -w '%{http_code}\n' https://<address>/api/instance` prints 401 (or a
  redirect, 3xx), never 200. A 200 means that address is public: fix the protection scope first.
- `GET /api/health` answers `{"status":"ok","mode":"live"}`.
- `GET /api/instance` shows your instance name, people and time zone.
- A tile whose adapter is off says Not connected; an engine that refuses the token shows the
  features as unavailable, never as demo data.
- Rotating the token: replace it in the engine's secret file, restart the engine, update
  `RHYTHM_APP_TOKEN` in Vercel and redeploy.
