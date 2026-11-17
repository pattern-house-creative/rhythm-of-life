# app/server

The facade: the app's own `/api/*` routes. The browser reaches nothing else, and only these talk to
the engine. No login: who reaches the app is decided by where it listens and what stands in front
of it (`docs/configuration.md`). Viewer sessions (`app.viewers`, off, and refused by this version)
let a person's requests reach the engine with their own credential (`viewer.ts`).

| File                                                               | What                                                                                                                                                                                |
| ------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `routes.ts`                                                        | the route table and the small handlers; shared by every runner                                                                                                                      |
| `node.ts`                                                          | the Node server: `dist/` plus the routes, security headers, no writes to disk; it answers only to `localhost`, `app`, IP addresses and the install's own host names (421 otherwise) |
| `devApi.ts`                                                        | the same routes inside Vite's dev server                                                                                                                                            |
| `config.ts`                                                        | the part of `rhythm.yaml` the app uses: `instance.name`, `timezone`, `people`, `engine.url`, `app.credentialExpiry`, and the install's host names                                   |
| `engine.ts`                                                        | the one engine client (`contracts/README.md` section 1)                                                                                                                             |
| `engineFetch.ts`                                                   | one request to the engine: bounded in time, one retry for a read, a failure logged by its kind only                                                                                 |
| `health.ts`, `additionalHealth.ts`, `glucose.ts`, `healthState.ts` | health tiles: latest glucose, 14 days of glucose, Garmin summary, typed unavailable states                                                                                          |
| `graph.ts`, `relationshipGraph.ts`                                 | the knowledge map (demo sky, or `app_graph`)                                                                                                                                        |
| `insights.ts`, `dashboards.ts`, `graphRefresh.ts`                  | insights and feedback, dashboards and their declared writes, pins, map rebuild                                                                                                      |
| `documents.ts`                                                     | documents a person adds in the explorer (one file, up to 5 MiB)                                                                                                                     |
| `screens.ts`                                                       | live screens: one `app_screens` call per request, a screen's long wait for a command included                                                                                       |
| `studio.ts` | Studio for a screen: one `app_studio` call per request, reached through `/api/screens?studio=1` (`docs/dev/studio.md`) |
| `status.ts`                                                        | the engine's version and each model's state for Sources & settings (`app_status`, `docs/models.md`)                                                                                 |
| `sync.ts` | Sync now in Sources & settings: the adapters on the engine's clock and one batch now of one of them (`app_sync`, SB-P6-09) |
| `memories.ts` | the Memories view: one `app_memories` op per request, only while the engine lists the tool (`memory.tools` on; SB-P3-12) |
| `documentFile.ts`                                                  | a document's original file for the reader, streamed from the engine (up to 20 MiB)                                                                                                  |
| `http.ts`                                                          | JSON responses (`no-store`), same-origin request guard, bounded body reading                                                                                                        |
| `viewer.ts`, `viewerScope.ts`, `viewerGate.ts` | viewer sessions, only with `app.viewers` on, which this version refuses (`viewerGate.ts`): `/api/viewer`, and a session's requests answered with its own engine credential, derived from its cookie's handle and the app token (`docs/security.md`, Viewer sessions) |

## Routes

| Route                        | Method    | Engine tool                                                                                                                                                                                                                                                                                                                                                                                                                                                            | Demo mode                                                                     |
| ---------------------------- | --------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `/api/instance`              | GET       | none (from `rhythm.yaml` and `RHYTHM_CATALOG_PROVIDER`)                                                                                                                                                                                                                                                                                                                                                                                                                | the settings, `mode: "demo"`, `catalogProvider: "rhythm"`                     |
| `/api/health`                | GET       | none                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | `{"status":"ok","mode":"demo"}`                                               |
| `/api/observatory`           | GET       | `app_latest_glucose`; `?source=history` `app_recent_glucose {days: 14}`; `?source=garmin` `app_garmin_summary`                                                                                                                                                                                                                                                                                                                                                         | the fictional dataset                                                         |
| `/api/graph`                 | POST      | `app_graph` (overview with known ids; `search` and `neighbors` over every star, paged by the engine's cursor; `get`, `content`, `data` for one star; `recall`, one question of at most 800 characters, the stars combined retrieval finds with why, only while the engine lists it (`engine.recall` on), else `{available: false}` without a call)                                                                                                                                                                                                                                                                                                                   | the fictional sky                                                             |
| `/api/insights`              | GET, POST | `app_insights {limit: 50}`, `app_insight_feedback`                                                                                                                                                                                                                                                                                                                                                                                                                     | empty; the browser keeps the demo's insights                                  |
| `/api/dashboards`            | GET, POST | `app_dashboards`                                                                                                                                                                                                                                                                                                                                                                                                                                                       | empty; the browser keeps the demo's dashboard                                 |
| `/api/graph-refresh`         | POST      | `app_request_refresh`                                                                                                                                                                                                                                                                                                                                                                                                                                                  | not accepted                                                                  |
| `/api/garmin-sync`           | POST      | `app_request_garmin_sync`                                                                                                                                                                                                                                                                                                                                                                                                                                              | not accepted                                                                  |
| `/api/documents`             | GET, POST | GET: whether `app_upload_document` is offered; POST `{name, content}` (base64), inside a viewer session also `family`: `app_upload_document`                                                                                                                                                                                                                                                                                                                                                                | not available; not added                                                      |
| `/api/document-file?id=<id>` | GET       | `GET /app/documents/<id>` (not MCP, `contracts/README.md` 1.5), asked for uncompressed (`Accept-Encoding: identity`, so a compressing proxy keeps its length): the file, streamed, with the app's own type, name, `nosniff` and CSP; `&download=1` saves it; 404, 413 `{maxBytes}` over the limit (4 MiB on a hosted front end), 502                                                                                                                                   | 404                                                                           |
| `/api/screens`               | POST      | `app_screens`: one `{op, ...}` with exactly that op's keys (`docs/dev/screens.md` 3.4): `listen` (waits up to 25 seconds for a command), `done`, `send` (and the action's own arguments, as `args` or beside its keys, passed on as `args`; waits up to 8 seconds for the outcome; answers `{command}` or `{ok: false, reason}`), `list`, `rename`; 400 for any other shape, 502 when unavailable; `{available: false}` from an engine that doesn't list `app_screens` | `{available: false}`; the browser keeps its screens over a `BroadcastChannel` |
| `/api/screens?studio=1` | POST | `app_studio` (`docs/dev/studio.md`), in the same function as `/api/screens`: one `{op, screen, ...}` with exactly that op's keys (`contracts/README.md` 2.2): `active`, `read`, `data`, `controls`, and `validate`, `feedback`, `cancel`, `discard` and `save`, each of the last five with `sessionId`, `revision`, `requestId` and `payload`; answers the engine's `{value}`, never sent twice; 400 for any other shape, 403 when the engine refuses this screen's login, 409 for its other refusals (`{error}`), 503 when the engine doesn't list `app_studio` or the call fails | `{value: null, available: false}` |
| `/api/status`                | GET       | `app_status`: `{mode: "live", available: true, version, schema, models}`; `{mode: "live", available: false}` when the engine doesn't list `app_status` or the call fails                                                                                                                                                                                                                                                                                               | `{mode: "demo", available: false}`                                            |
| `/api/sync` | GET, POST | `app_sync`: GET `{available: true, adapters}` (`{}`, a read); POST `{adapter}` (exactly an adapter's name) `{available: true, accepted, refused, adapter}`, never sent twice; `{available: false}` when the engine doesn't list `app_sync`, refuses the adapter (404) or the call fails | `{available: false}` |
| `/api/memories` | GET, POST | `app_memories` (only while the engine lists it: `memory.tools` on): GET `{available}`; POST `{op, ...}` (one op with exactly its members, never a channel or a client) `{available: true, ok: true, ...}` or `{available: true, ok: false, code, reason}`; reads (`list`, `trash`, `get`, `history`, `projects`) may be tried twice, writes never | `{available: false}` |
| `/api/viewer` | GET, POST | `app_viewer`, only with `app.viewers` on: GET `{available, person}` (`whoami` with the session's credential; `person` `{id, name}` or null for the household view); POST `{op: "enrol", code}` (`enrol` with the app token and a credential made here; sets the session cookie, HttpOnly, SameSite=Strict, `__Host-` over HTTPS; 400 for a code that did not work) or `{op: "end"}` (`end`; clears the cookie). Off: `{available: false}`, 404 to a POST, and the engine is not called | `{available: false}` |

Every route takes a request body of at most 128 KiB, except `/api/documents` (one file as base64, about 7 MiB) and
`/api/memories` (a memory's whole text, at most 128 KiB as UTF-8, with the rest of its op: at most 288 KiB). A memories
body over it is answered 400 and never reaches the engine; the Memories view says so, and that nothing was sent.

## A slow or unreachable engine

Each engine call has a time for one try and a total: 8 and 16 seconds, 25 and 25 for `app_dashboards` and `app_studio`,
35 and 35 for `app_screens`, 60 for an added document, 120 for a document's file. The total includes the `tools/list`
check an adapter tool needs. A read (`app_graph`, `app_insights`, the health reads, `app_dashboards` without a change, a
document's file) whose connection failed, timed out or met a gateway's 502, 503 or 504 is sent once more within its
total; a refusal, a cancelled request and every change, feedback or request (every `app_screens` call among them) are
never repeated. The browser waits 22 seconds for a route, 30 for `/api/dashboards`, 40 for `/api/screens`; a hosted
front end runs `api/screens.ts` for up to 60 (`vercel.json`), so a screen's wait ends at the engine (25) first, then
here (35), then in the browser. A browser that leaves ends its request's engine call: the Node server and the dev
server pass the disconnect on as the request's `signal`. Vercel does so only for a function that opts in, so
`vercel.json` sets `"supportsCancellation": true` for `api/screens.ts`; without it an abandoned listen would hold its
engine call and could claim a command that no screen then shows.

Engine requests use their own connection settings (undici 7, the version Node 24's fetch takes), never the
process-wide ones: when the engine's host name has several addresses, the next one is tried after a second, and
connecting gives up after five seconds, well inside a call's time.

The app runs on Node 24: `engines` in `package.json` is `24.x`, which a hosted front end such as Vercel uses over its
project setting, and the image is `node:24-alpine`. The first `GET /api/health` in each process logs
`rhythm-app-runtime` with the Node version, so the runtime a host chose shows in its log.

A failure that used up its tries is logged as `rhythm-engine-transport` with the call's name (`app_graph`,
`tools/list`, `document-file`), its kind (`timeout`, `refused`, `reset`, `dns`, `unreachable`, `tls`, `redirect` for
a front door sending the call to a sign-in page, else `network`), the tries, the time taken and Node's error codes from
a fixed list: never the engine's URL, a token, a request or an answer. The browser sees the feature as unavailable, as
for any other failure.

Any other failed call is logged as `rhythm-engine-call` with the call's name, its reason (`engine_auth`, `forbidden`,
`invalid_arguments`, `not_found`, `too_large`, `unavailable`, `invalid_response`, `credential_expired`), the engine's
HTTP status and the kind of answer: `json`, `event-stream`, `html` (a front door's page, such as Cloudflare Access
refusing the service token), `none` (no body, as the engine's own refusal of a host), `other`, or `result` with status
200 for a tool's own error or a result of the wrong shape. An answer the facade's checks refused (app and engine
versions that do not match) is logged the same way, with the reason `answer_rejected` and the error's type. Each kind
is logged at most once a minute, with the number of lines left out since (`repeats`). Demo mode, an adapter that is
off and a request the browser gave up on are not failures and are not logged.

## Settings and secrets

| Variable                                                                           | Default                            | Meaning                                                                                                                                                                                                                                                                                                                                                                |
| ---------------------------------------------------------------------------------- | ---------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `RHYTHM_CONFIG`                                                                    | `/etc/rhythm/rhythm.yaml`          | the settings file; missing means the demo settings (people `alex` and `sam`, UTC)                                                                                                                                                                                                                                                                                      |
| `RHYTHM_CONFIG_YAML`                                                               | none                               | the settings file's text, for hosts that cannot mount a file                                                                                                                                                                                                                                                                                                           |
| `RHYTHM_ENGINE_URL`                                                                | none                               | wins over `engine.url` when set and not empty                                                                                                                                                                                                                                                                                                                          |
| `RHYTHM_APP_TOKEN_FILE`                                                            | `/run/secrets/app_token`           | the app token; missing, empty, unreadable or a directory means demo mode                                                                                                                                                                                                                                                                                               |
| `RHYTHM_APP_TOKEN`                                                                 | none                               | the token as a value, only for hosted platforms, and only when `RHYTHM_APP_TOKEN_FILE` is unset                                                                                                                                                                                                                                                                        |
| `RHYTHM_CF_ACCESS_CLIENT_ID`, `RHYTHM_CF_ACCESS_CLIENT_SECRET` (or `_SECRET_FILE`) | none                               | Cloudflare Access service-token headers for an engine behind Access                                                                                                                                                                                                                                                                                                    |
| `RHYTHM_CATALOG_PROVIDER`                                                          | `rhythm` (unset or empty)          | the provider id of the engine's own stars (data stars, saved documents, dashboards), served to the browser at `/api/instance` in live mode. Give it the engine's value: only an install that kept an older engine's ids sets it (`docs/dev/migration-compat.md` step 3). A value that is no provider id stops the server at start and makes `/api/instance` answer 503 |
| `HOST`, `PORT`                                                                     | `127.0.0.1`, `8080`                | where the Node server listens (the container sets `0.0.0.0`)                                                                                                                                                                                                                                                                                                           |
| `RHYTHM_STATIC_DIR`                                                                | `../../dist` from the built server | the built PWA                                                                                                                                                                                                                                                                                                                                                          |

A settings file that is invalid at start stops the server with the keys that are wrong (never their
values). One that turns invalid while running keeps the last good settings.

## What the facade expects from the engine

The `app_*` arguments are in `contracts/README.md` section 2.2. The `structuredContent` shapes are
pinned by `contracts/mcp/<tool>.output.schema.json`; `tests/contract-fixtures.test.ts` reads each
fictional example in `contracts/fixtures/`. In short:

- **Any subject the settings name.** `subject` (and each row's `subject_id`) must be a person id from
  `people` or `household`; nothing is hardcoded.
- **Time zones from the data.** Garmin rows carry their own IANA `timezone`; hours on the glucose
  chart follow the install's `timezone`.
- **`app_latest_glucose`**: `{selection: "latest", complete: true, subject, rows: [≤1 row]}`; a row
  has `subject_id, provider, metric ("glucose", or the older "sensor_glucose"), source_kind (a live
feed's kind; archive, export or CSV kinds are refused), unit "mg/dL", value, resource_id,
sample_key, quality, observed_at (with offset), total_samples`, and optionally `demo` (true for the
  engine's fictional seed, whose synthetic subject is accepted only then) and `stale`.
- **`app_recent_glucose {days: 14}`**: the compact form `{subject, selection: "history", complete,
demo, provider, series: [[epochSeconds, value, bound?]], sources: [{kind, provider, start, end,
readings, timeBasis}], latest, intervalMinutes: 5, today, start, end, lastSyncedAt, summary}`. A
  stretch's `kind` is `archive` or `live` by the provider's policy (the older `<provider>-csv` and
  `<provider>-live` are read the same); `timeBasis` is `device-local` for an archive and
  `source-zone` for a live feed. `summary` (14-day archive totals) has `sourceKind: "archive"`,
  `timeBasis: "device-local-unverified"`. The engine's demo seed is accepted only with `demo: true`.
- **The glucose source** has one place in the source list (`id: "glucose"`), named after the
  provider that fed the reading (`src/graphView.ts` `sourceName`: `nightscout` is Nightscout,
  `carelink-live` CareLink live), whatever that provider is.
- **`app_garmin_summary`**: `{subject, selection: "garmin", complete, rows, today, startDate,
endDate, coverageComplete, lastSyncedAt, activityCount}` with Garmin's metric names and units.
- **The engine's own records** use the provider id `rhythm` (data stars
  `rhythm:data%3A<provider>:<subject>%3A<group>`, saved documents, dashboards), or the one the
  engine's `RHYTHM_CATALOG_PROVIDER` names, which the app is given too.
- **`app_graph`'s note on its snapshot.** Every map read the engine answers is `partial`, with an issue
  under that provider saying what it read (a scheduled-refresh snapshot, and how much is shown). It is
  no failure: on a `search` or `neighbors` read the engine answered, `/api/graph` leaves it out and says
  the read is `complete` when no page is left, so the explorer says a source couldn't be read only when
  the read failed. The overview keeps it.
- Insights may name a source of kind `rhythm` (or the older `nas`), and `generatedBy` is any short
  label (`harness-daily` by default).
