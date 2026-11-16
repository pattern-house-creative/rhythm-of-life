# Contracts

This folder is the agreement between the parts of Rhythm of Life. The app and the engine are built
against it in parallel, so it is written to be exact. When code and this file disagree, fix one of
them in the same change and say which in the commit message.

| File | What it pins down |
|---|---|
| `README.md` (this file) | app to engine calls, the MCP tool list and roles, adapters, the external-plugin ingest contract |
| `rhythm.schema.json` | the install's `rhythm.yaml` (see `docs/configuration.md`) |
| `mcp/ingest_measurements.input.schema.json` | arguments of `ingest_measurements` |
| `mcp/ingest_records.input.schema.json` | arguments of `ingest_records` |
| `mcp/app_*.output.schema.json` | `structuredContent` of each app tool (section 2.2) |
| `mcp/capture_request.input.schema.json`, `mcp/capture_webhook.input.schema.json`, `mcp/capture.output.schema.json` | `POST /capture`'s bodies and its answer (section 6) |
| `adapters/*.json` | what `rhythm connect` asks for each adapter (`adapters/README.md`) |
| `fixtures/*.json` | synthetic example payloads; both sides' tests read them |
| `../app/src/model.ts` | the app's own types: what the app's server hands the browser (section 5) |
| `../docs/dev/screens.md` | live screens: the commands, the engine's tables and wait, `app_screens`, `/api/screens` |

Words: **MUST**, **SHOULD** and **MAY** mean what RFC 2119 says. "The engine" is the .NET service in
`engine/`. "The app" is the PWA plus its small Node server in `app/`. "Harness" is any MCP client a
person uses with their AI (ChatGPT, Claude, others). Examples use the fictional people `alex` and
`sam`, the time zone `Europe/London`, and `example.invalid` hosts.

---

## 1. App to engine

The browser never talks to the engine and never sees a token. It calls the app's own server
(`/api/*`), and that server, the **facade**, calls the engine.

### 1.1 Transport

- The facade sends `POST {engineUrl}/mcp`, one JSON-RPC 2.0 request per HTTP request.
- `engineUrl` is `RHYTHM_ENGINE_URL` when that variable is set and not empty, else `engine.url` from
  `rhythm.yaml`. The compose default is `http://engine:8080` on the internal network. The facade
  removes a trailing `/` and appends `/mcp`.
- Request headers:

  | Header | Value |
  |---|---|
  | `Content-Type` | `application/json` |
  | `Accept` | `application/json, text/event-stream` |
  | `MCP-Protocol-Version` | `2025-03-26` |
  | `Authorization` | `Bearer <app token>` |

  The facade MUST NOT send `Origin` or cookies, and MUST NOT follow redirects (`redirect: "error"`).
- Body: `{"jsonrpc":"2.0","id":"<string>","method":"tools/call","params":{"name":"<tool>","arguments":{...}}}`.
  `arguments` is always an object, `{}` when there are none. No other top-level or `params` members.
- The app role is **stateless**: no `initialize` is needed before `tools/call`, and no
  `Mcp-Session-Id` is sent or expected. The engine MUST accept `initialize`, `ping` and `tools/list`
  from the app role too (the facade uses `tools/list`, see 1.4).
- The engine SHOULD answer with `Content-Type: application/json` and a single JSON-RPC response. The
  facade MUST also accept `text/event-stream` and take the first `data:` message whose `id` matches.
- A successful app tool result is
  `{"jsonrpc":"2.0","id":<same>,"result":{"structuredContent":{...},"content":[{"type":"text","text":"<summary>"}],"isError":false}}`.
  `structuredContent` MUST be an object; the facade reads only it. The text block is a one-line
  summary for humans and logs.

### 1.2 The app token

- One app token for the whole app, replacing the six per-feature tokens and leases of the private
  version.
- The facade reads it from the file named by `RHYTHM_APP_TOKEN_FILE` (compose default
  `/run/secrets/app_token`) at start and on each request after the file changes. It trims surrounding
  whitespace. It never logs the token, never puts it in a URL, a `VITE_*` variable or a response.
- Format: 32 random bytes, base64url without padding (43 characters). `rhythm init` generates it.
- The engine stores only a SHA-256 hash. At start it reads the same file (`RHYTHM_APP_TOKEN_FILE`)
  and ensures exactly one enabled principal named `app` with role `app` and that hash, disabling any
  older `app` principal. Rotating: replace the file, restart engine and app.
- Optional `app.credentialExpiry` (an RFC 3339 instant) in `rhythm.yaml`: after it the facade stops
  calling the engine and reports `unavailable` with reason `credential_expired`. Default: none.
- The app token alone always reads the household view and never names a person: the engine reads
  no person sent beside it, in an argument, a header or a cookie. A viewer session's requests
  (`app.viewers: on`, refused by this version; `../docs/security.md`, Viewer sessions) carry that
  session's own credential instead, never both: an `app` principal bound to one person, which the
  engine made when the facade registered the credential with an operator's one-time code
  (`app_viewer` `enrol`, 2.2). The facade derives the credential from the session's cookie handle
  and the app token (`app/server/viewerScope.ts`), so it keeps nothing; the browser holds only the
  handle. The engine refuses it with 401 once it is signed out or revoked or its person is suspended
  or gone, and the facade then clears the cookie.

### 1.3 Demo mode and honest states

- **Demo mode** when the engine is not configured: `engineUrl` is empty, or the token file is
  missing, empty, unreadable or a directory (Docker creates an empty directory when a secret file did
  not exist at `docker compose up`). In demo mode the app serves its built-in fictional data, every
  item has `demo: true` and the UI shows DEMO. The facade makes no engine calls.
- When the engine **is** configured, the app never falls back to demo data. A failed call shows that
  feature as `unavailable` (with a reason), stale data as `stale`, and an adapter that is off as
  `Not connected` in the source list; its tiles stay off the home screen (2.2, `corner`).
- The engine's own fictional seed (`demo.enabled` in `rhythm.yaml`) is not such a fallback. It
  arrives labelled (a graph star's `provenance.demo`, a glucose row's `demo`, a dashboard titled
  DEMO) and the app shows it as DEMO. It is everyday wellbeing, with no glucose. It belongs to the engine's synthetic subject, which is not in `people`: the facade
  accepts a subject outside `people` only for an item labelled demo.
- `GET /api/instance` (served by the facade from `rhythm.yaml` and its environment, never from the engine) returns
  `{"name":string,"people":[{"id":string,"name":string}],"timezone":string,"mode":"demo"|"live","catalogProvider":string}`.
  The app uses it instead of any hardcoded person, time zone or instance name. `catalogProvider` is the
  provider id of the engine's own stars (data stars, saved documents, dashboards): the facade's
  `RHYTHM_CATALOG_PROVIDER`, the same value as the engine's, `rhythm` when unset and always in demo mode.
  An answer without it means `rhythm`.

### 1.4 Discovery, limits, errors

- The facade calls `tools/list` (role app) at start and at most every 5 minutes, and treats an app
  tool that is absent as "adapter off": the matching source says `Not connected`, its tiles stay off
  the home screen, and the facade makes no call for it. The glucose tools are the one exception to the
  5 minutes: the engine lists them once a glucose source has data (2.2), which a source's first reading
  changes with no setting changed and no restart, so a listing without the one a glucose read asks for
  is asked again at that read, at most every 10 seconds, and the engine keeps a reader's "none" for
  10 seconds and a "some" for a minute. A source's first reading reaches the home screen at its first
  read 10 seconds after it.
- Request body at most 32 KiB, except `app_upload_document`: 7 MiB (a file of up to 5 MiB, base64), and
  `app_memories`: 288 KiB (a memory's whole text, 2.2).
  Response at most 2,000,000 bytes and 8 s, except `app_dashboards`: 3,600,000 bytes and 25 s,
  `app_upload_document`: 100,000 bytes and 60 s, and `app_screens`: 35 s. The facade aborts past either limit.
- A screen waits for its next command with one held request (`app_screens` `listen`, `docs/dev/screens.md`), so its
  times form a chain, each longer than the one before: the engine holds it at most 25 s (`hold`), the facade allows
  35 s for the call, the browser 40 s for `/api/screens`, and a hosted front end's function 60 s (`maxDuration` in
  `app/vercel.json`, `deploy/vercel/`). A wait that ends empty is a normal answer (`command: null`), not a timeout.
  `app_screens` has 35 s per try and in total, and is never retried, a `listen` included: a `listen` claims a
  command, so a second try could leave the first one claimed but never shown, and a second `send` would send its
  command twice. The screen simply listens again.
- Engine-side checks, as today: the `Host` header MUST be in `engine.allowedHosts` (default
  `localhost`, `127.0.0.1`, `[::1]`, `engine`); a request carrying `Origin` is refused (no CORS, no
  browser calls); only `POST /mcp`, `GET /health` and the app's `GET /app/documents/<id>` (1.5) exist.
- Failures. The engine answers an app tool call that cannot succeed either with an HTTP status or with
  `result.isError: true` and `structuredContent.error = {"code":string,"message":string}`:

  | HTTP | `error.code` | Meaning | App shows |
  |---|---|---|---|
  | 401 | `unauthorized` | token missing or unknown | unavailable: `engine_auth` |
  | 403 | `forbidden` | tool not in the caller's role, Host not allowed, Origin present | unavailable |
  | 400 | `invalid_arguments` | arguments outside the tool's schema; for `app_screens` also a rule refusing them (20 commands already waiting, a `done` for a command not waiting for one) | unavailable (a facade bug); a screen command not sent |
  | 404 | `not_found` | the dashboard, insight, star, screen or screen command does not exist, or a `send`'s target is not open | that item missing |
  | 413 | `too_large` | request body over the limit | unavailable |
  | 503 | `unavailable` | a source or the database is not answering | unavailable, retry later |

  The facade MUST treat a JSON-RPC `error` member, a non-object `structuredContent`, an `id` mismatch
  or a non-JSON response as `unavailable`.

When `engine.oauth.enabled` is true, a harness may also use an OAuth access token at the same `/mcp`.
The existing bearer lookup takes precedence; OAuth resolves to an existing enabled harness principal
with the same scope and role checks. A 401 includes protected-resource discovery. Only the optional
OAuth protocol routes permit browser consent; the no-Origin rule on `/mcp` remains in force.
See [OAuth setup and lifecycle](../harness/chatgpt/oauth.md).

### 1.5 A document's original file

A document someone added as a file (`app_upload_document`, or the documents folder) keeps its original in the
engine's archive. `app_graph` `content` names it in `resource.original` = `{id, format: pdf|text|markdown, bytes,
name}`, and the facade reads it with one plain HTTP request, streamed, because a file (up to 20 MiB) is too large for
a JSON-RPC answer:

- `GET {engineUrl}/app/documents/<id>`, `<id>` the resource id (a lower-case uuid), with the same `Authorization`
  (and access) headers as 1.1, no body, no `Origin`, no cookies, no redirects. App role only: any other role is 403.
- `200` with the file's bytes, `Content-Type` `application/pdf`, `text/plain; charset=utf-8` or
  `text/markdown; charset=utf-8`, `Content-Length`, `Content-Disposition: inline` with the safe `name`,
  `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: no-store`.
- `404` (`not_found`) when the id is not a current document of a subject in this install with an archived file;
  `503` when the archive cannot be read or the file no longer matches its checksum. Errors carry the JSON body of 1.4.
- The engine finds the file only by the path and checksum its catalog recorded, never by anything in the request, and
  checks it against that checksum before sending it.
- The facade serves it to the browser at `GET /api/document-file?id=<id>` (`&download=1` for an attachment) with its own
  `Content-Type`, `Content-Disposition`, `nosniff` and CSP, at most 20 MiB and 120 s (a hosted front end less, see
  `deploy/vercel/`).

---

## 2. MCP tools and roles

The engine has **one** MCP server at `/mcp`. Every token belongs to a principal, and every principal
has exactly one role. The role decides which tools `tools/list` shows and which `tools/call` accepts.

| Role | Who | Tools |
|---|---|---|
| `harness` | an AI client: ChatGPT, Claude, any MCP client (`rhythm token create <name>`) | every harness tool in 2.1, plus enabled adapters' harness tools |
| `app` | the app's facade (one token, section 1.2) | the `app_*` tools in 2.2 only: reads, insight feedback, refresh requests, declared dashboard writes and screen commands, and with `memory.tools` on a person's native memory writes (`app_memories`) |
| `ingest` | an external plugin container (`rhythm token create --role ingest --plugin <name>`) | `ingest_measurements`, `ingest_records` only (section 4) |
| `capture` | a webhook, CI job or shortcut (`rhythm capture connect <name>`, with `memory.capture` on) | no tool, and no `/mcp`: `POST /capture` only (section 6) |

The household shares everything: `harness` and `app` principals read every subject in the install
(each person and `household`). The only per-token boundary is the ingest role's write scope.

The harness role is the MCP SDK's server over Streamable HTTP, stateless (`McpClientCompatibilityTests`): a client on
any protocol from `2024-11-05` to `2026-07-28` gets the same tools and answers. No `Mcp-Session-Id` is handed out and a
request carrying one is refused (400); `GET` and `DELETE` are 405 (the older HTTP+SSE transport does not connect); the
client's `Accept` must cover both `application/json` and `text/event-stream`, by naming both or by a wildcard such as
`*/*` (406 for only one of them, or none); a JSON-RPC batch is refused (400).
Every request's token is checked on its own, so a revoked token is refused on the very next call, and a connected
client carries on across a 503 or an engine restart without a new handshake.

Screens are not principals and add no login. Every open copy of the app reaches the engine as the one `app`
principal, so `app_screens` names the screen in its arguments; a screen's name is a label its browser chose, never
identity. An AI client reaches screens with its own harness token, through `list_screens` and `control_screen` (2.1).

**The write limit** (`engine.writeLimit`, off by default; off, nothing here happens and every answer is as before). With it on,
a token past 300 writes in 10 minutes has its next write refused, and nothing is saved, until its oldest write leaves those ten
minutes ([docs/security.md](../docs/security.md#the-write-limit)). A write is a harness tool not marked read only (R in 2.1), an
`ingest_*` call and a `POST /capture`; each token has its own count.

| Who | The answer past the limit |
|---|---|
| `harness` | the tool does not run and answers `isError: true`: a memory tool `{ok: false, code: "write_limit", error}` (`remember.write_limit.example.json`), any other tool one line of text in the same words |
| `ingest`, `capture` | HTTP `429` with `Retry-After` in seconds and the door's body (1.4 and 6.3), code `write_limit`. A retry with the same idempotency key is safe: nothing was written |

The error's words say how many seconds to wait. A read, `tools/list`, the handshake and `ping` answer as ever. A harness call whose
arguments do not fit its schema is answered as before and not counted; an `ingest_*` or capture call counts whether or not its
arguments fit. Past the limit a capture call's `429` comes before its body is read, and a plugin's once its body reads as an ingest
call, before its arguments are checked: one naming another tool, or whose arguments are not an object, is refused as before and not
counted. A `supersede`'s plan counts, and past the limit is refused, though it writes nothing. The `app` role, its shared login
included, and the terminal never meet the limit and are never counted.

### 2.1 Harness tools (73 tools)

R = read only, W = writes, D = destructive. "Gate" says when the tool registers. The private engine's pinned commit
had 25: `resync_carelink_csv` (the last row) left with the CareLink plugin, live screens added `list_screens` and
`control_screen`, Studio added `studio_dashboard`, `discover_dashboard_controls` and `invoke_dashboard_action` (see
[Studio authoring](#studio-authoring-and-typed-dashboard-selections)), combined retrieval added `recall`, listed only
with `engine.recall` on, prompt capsules added
`get_prompt_capsule`, listed only with `memory.capsules` on, and native memories added eleven memory tools with Second
Brain's names, Rhythm's `supersede` and the stale queue's `list_stale` and `still_true` (upstream serves its stale queue
over HTTP and `still_true` as an action), made and listed only with `memory.tools` on, then `link`, `unlink` and
`connections` with them, then digests' `digest_sources`, `save_digest` and upstream's `digest` with them, and
Rhythm's `cite` and `revalidate` (a memory citing a Drive document, SB-P4-10) with them, and the open loops'
`list_loops` and `resolve_loop` (upstream serves its loops over HTTP and their resolve as actions, SB-P5-09) with them,
and the decision ledger's `list_decisions`, `record_outcome` and `decision_calibration` (upstream serves its ledger and
calibration over HTTP and an outcome as a resolve action, SB-P5-11) with them, and the due dates' `list_due` and
`resolve_due` (upstream serves its due feed over HTTP and snooze and clear as actions, SB-P5-10) with them, and the
duplicate review's `list_duplicates` (Rhythm's own: upstream lists its duplicate candidates nowhere, SB-P5-02) with them, and the contradiction review's `list_contradictions` and `propose_contradiction` (upstream closes the older memory on its model's verdict; here the calling AI proposes and a person settles it, SB-P5-03), and the insight candidates' `list_insight_candidates` and `resolve_insight_candidate` (upstream keeps its candidates for its own weekly reasoning pass, SB-P5-05) with them, the one insight stream's `list_insight_stream`, `review_insight` and `insight_history` (upstream serves its queue over HTTP and confirm and dismiss as actions, SB-P5-06) with them, and the standing instructions' `list_standing` and `stop_standing` (upstream serves its list over HTTP and a stop as a resolve action, SB-P5-12) with them, and `brief` (upstream's, its text as sections in structured content, SB-P5-08) with them, and `share`, made and listed only with `memory.share` on as well. Enabled adapters add their own harness tools (2.4); none in this repository does
yet.

| Group | Tool | R/W | Gate | Purpose |
|---|---|---|---|---|
| graph | `explore_graph` | R | core | stars around a `star` (graphId) or matching `query`, `depth` 1–2, `limit` 1–60 |
| graph | `graph_changes` | R | core | stars changed and links first seen `since` (last 30 days), `limit` 1–100 |
| graph | `get_relationship_graph` | R | core | the persisted graph as a GraphPage, with removals for `knownNodeIds` / `knownEdgeIds` |
| retrieval | `search_knowledge` | R | core | lexical search of indexed resources (`query`, `limit`); with `engine.searchKnowledge: combined`, the same rows in combined retrieval's order |
| retrieval | `recall` | R | `engine.recall` on | words and meaning in one ranked list, with excerpts, `explain` and a receipt (`query`, `topK` 1–20, `hops` 0–3, filters) |
| retrieval | `search_resources` | R | core | search with exact `domain`, `provider`, `kind`, `subject` filters, max 50 |
| retrieval | `semantic_search` | R | embeddings on | passages by meaning in indexed documents (`query`, `domain`, `limit`) |
| retrieval | `get_resource` | R | core | up to 8,000 characters of one resource (`resourceId`) |
| retrieval | `get_related_resources` | R | core | links in both directions with evidence and status |
| memory | `get_prompt_capsule` | R | `memory.capsules` on | one scope's core or project prompt capsule and its strong ETag (`kind`, `project_id`, `workspace`, `if_none_match`) |
| memory | `remember` | W | `memory.tools` on | save one memory, always a draft, in the caller's own scope unless `workspace` names one |
| memory | `append` | W | `memory.tools` on | add a dated update to a memory; the text before kept as a version |
| memory | `update` | W | `memory.tools` on | replace a memory's text, tags or fields; capsule tags refused |
| memory | `get` | R | `memory.tools` on | one memory whole, or one of its states by `version`; a held one without its words |
| memory | `list_recent` | R | `memory.tools` on | memories newest first with filters and a cursor, or with `in_trash` the trash the caller may restore (and, to an AI client, what a disconnect moved there, reason `disconnect`, which only a person restores) |
| memory | `set_status` | W | `memory.tools` on | canonical, draft or deprecated |
| memory | `forget` | W, D | `memory.tools` on | to the trash with its links for 14 days; never deleted for good through this tool |
| memory | `undo` | W | `memory.tools` on | the newest change, back to `to_version`, a restore from the trash, or a `group` of AI changes |
| memory | `history` | R | `memory.tools` on | a memory's changes newest first, from the change that brought it into the caller's scope |
| memory | `list_projects` | R | `memory.tools` on | the memory projects the caller reads |
| memory | `list_teams` | R | `memory.tools` on | the shared scopes the caller reads and writes: Family, or none |
| memory | `supersede` | W | `memory.tools` on | one memory, true now, replacing an earlier one of its scope: a plan first, applied only with the plan's `confirmation` |
| memory | `link` | W | `memory.tools` on | a typed link from one memory to another of its scope; an AI client's is inferred until a person confirms it |
| memory | `unlink` | W, D | `memory.tools` on | the links between two memories removed, one type or every type but a supersede's; the map's suggestion between them dismissed until a person takes it back in the app |
| memory | `connections` | R | `memory.tools` on | a memory's neighbours one step away: the links made, then the map's suggestions, each with its provenance |
| memory | `list_stale` | R | `memory.tools` on | the stale queue: memories the caller may change that may be out of date, with the queue's `total`, longest unchecked first |
| memory | `still_true` | W | `memory.tools` on | a memory still holds as it is: its checked time moves and it leaves the queue; no revision moves, no version is kept |
| memory | `digest_sources` | R | `memory.tools` on | the memories a digest of a project or a tag in one scope would draw from, by one rule, with the list's `basis` and whether one is `due` |
| memory | `save_digest` | W | `memory.tools` on | the client's digest saved with that `basis` as a new memory labelled the engine's own, with a `drawn_from` link to each source; no source changes |
| memory | `digest` | R | `memory.tools` on | the newest digest of a project or a tag the caller reads, or none; never writes one |
| memory | `cite` | W | `memory.tools` on | a memory cites a Drive document on the map, at the revision a check of it at Drive finds now; none of its words kept |
| memory | `revalidate` | W | `memory.tools` on | a memory's citations checked at Drive now and recorded: each moved to the document's current revision, or marked trashed, gone, unreadable or unavailable, every move listed |
| memory | `list_loops` | R | `memory.tools` on | the person's own open loops, what they owe (`out`) and what is owed to them (`in`), with the asked list's `total` and both directions' lengths, newest first |
| memory | `list_decisions` | R | `memory.tools` on | the person's own decisions in one list (`open`, `due`, `resolved`, `scored` or `all`), each with its confidence, outcome and review date, with the asked list's `total` and the four lists' lengths, newest first |
| memory | `record_outcome` | W | `memory.tools` on | how a decision turned out, `right`, `wrong`, `mixed` or `unknown` (asked again twice), as a change of its state kept as a version that `undo` takes back |
| memory | `decision_calibration` | R | `memory.tools` on | how sure the person was against how their decisions turned out, each figure with the number of decisions it rests on and none below its minimum |
| memory | `resolve_loop` | W | `memory.tools` on | an open loop closed, `done`, `received` or `not_a_task`, as a change of its tags kept as a version that `undo` puts back |
| memory | `list_due` | R | `memory.tools` on | the person's own memories with a due date, soonest first, in three windows (`overdue`, `upcoming` within 48 hours, `later`), with the asked list's `total` and each window's length |
| memory | `resolve_due` | W | `memory.tools` on | a due date snoozed to a moment to come, or cleared, as a change kept as a version that `undo` puts back |
| memory | `list_contradictions` | R | `memory.tools` on | the contradiction review: pairs of memories of one scope the caller may change that may conflict, each with both previews, how close and who named it one, with the review's length |
| memory | `propose_contradiction` | W | `memory.tools` on | says two memories conflict: only a candidate offered for that memory; settles nothing, queued for a person |
| memory | `list_duplicates` | R | `memory.tools` on | the duplicate review: memories the caller may change that were flagged on save as possible copies of another of their scope, with the review's `total`, newest first |
| memory | `list_insight_candidates` | R | `memory.tools` on | pairs of an older and a newer memory of one scope, 30 or more days apart, that the nightly pass found worth a look, best first, with the list's `total`; `dry_run` shows a sample of what the next pass would add, writing nothing |
| memory | `resolve_insight_candidate` | W | `memory.tools` on | a candidate used (an insight came of it) or rejected, kept with who decided, why, and the insight written from it; no memory changes |
| memory | `list_insight_stream` | R | `memory.tools` on | the one insight stream, one view at a time: proposed, confirmed, snoozed, closed or all, each view's length read with the page, and what today came to, so a day with no insight says so |
| memory | `review_insight` | W | `memory.tools` on | a person's confirm or dismiss of one insight, or a reopen, under memory.md section 5's rule, never by the login that saved it; kept with who reviewed it |
| memory | `insight_history` | R | `memory.tools` on | one insight's feedback and reviews, newest first |
| memory | `brief` | R | `memory.tools` on | what needs the person now: each section the head of the list its own tool walks, with that list's whole length; no held or trashed memory |
| memory | `resolve` | W | `memory.tools` on | one tool for every queue's action, each that queue's own step: an item leaves its queue once, a second resolve writes nothing and says so, and a person's steps are refused (`needs_person`) |
| memory | `list_standing` | R | `memory.tools` on | the standing instructions the caller reads, oldest first, each with where it came from and whether it fires, with the list's `total` and how many may fire, are held and are over the limit |
| memory | `stop_standing` | W | `memory.tools` on | a standing instruction stopped: it fires no more and stays a memory, as a change kept as a version that `undo` starts again |
| memory | `share` | W | `memory.tools` and `memory.share` on | move one memory between its owner's private scope and Family, or take it back; refused while `memory.scopes` is off |
| health | `discover_health_sources` | R | core | inventory of health sources and metrics for `subject`, `from`, `until` |
| health | `read_health_source` | R | core | a bounded projection of one original record (`resourceId`, `pointer`, paging) |
| health | `investigate_health` | R | core | deterministic discovery across one subject's data |
| health | `evaluate_health_hypothesis` | R | core | challenge one relationship (`exposure`, `outcome`, daily or event mode) |
| health | `query_health_metrics` | R | core | one metric for one subject, max 365 days, 500 samples |
| health | `compare_health_trends` | R | core | daily statistics for two metrics |
| health | `get_sync_status` | R | core | connector jobs and freshness, including each plugin's last accepted ingest |
| dashboards | `dashboard_data` | R | core | exactly what a dashboard page receives, for given `feeds` or a saved `dashboardId` |
| dashboards | `save_dashboard` | W | core | a new dashboard or its next version (page, feeds, declared writes, layout), or the newest version arranged in place |
| dashboards | `list_dashboards` | R | core | saved dashboards with their layouts; one with its page for revising |
| dashboards | `delete_dashboard` | W, D | core | delete one dashboard for good; needs the exact current `title` |
| dashboards | `dashboard_history` | R | core | what dashboards wrote, with before and after |
| insights | `save_insight` | W | core | save one daily insight with points, stars and sources |
| insights | `list_insights` | R | core | saved insights with feedback, plus the newest runs |
| insights | `log_insight_run` | W | core | record how one insight run ended (saved, quiet, blocked) |
| documents | `save_document` | W | core | add or refresh a searchable document copy (renamed, see 2.3) |
| screens | `list_screens` | R | core | screens seen in the last 24 hours, open ones first, each with what it shows now and its Home |
| screens | `control_screen` | W | core | one command for one screen (`action`, optional `screen`: an id or a name), waiting up to 8 s for its outcome; `move`, `resize` and `focus_map` arrange its parts |
| studio | `studio_dashboard` | W | core | a draft of a dashboard shown live on one screen, kept apart from its saved versions for seven days (`op` `begin`, `read`, `update`, `progress`, `cancel`, `discard` or `save`); `save` publishes the validated draft as a new dashboard or the dashboard's next version |
| studio | `discover_dashboard_controls` | R | core | the typed controls (tabs, filters, a week, a date range) a saved dashboard version declares, as one screen shows them (`screen`, `dashboardId`, `version`) |
| studio | `invoke_dashboard_action` | W | core | one typed selection on one screen (`screen`, `dashboardId`, `version`, `selectionRevision`, `control`, `value`); no version is saved |
| adapter | `resync_carelink_csv` | W | not in this repo | moved out: the CareLink plugin's own `resync` command (open question Q-015) |

Combined retrieval (SB-P2-09, `docs/dev/second-brain-parity/combined-retrieval.md`):

- `recall` takes `query` (1–800 characters), `topK` (1–20, default 5), `hops` (0–3, default 0: records linked to the
  first five take the 5th and 10th places), the filters `domain`, `provider`, `kind` and `subject` (exact; empty means
  any), `after` and `before` (a window on the source's own time: its last change, else when it was indexed) and
  `explain` (default false). It answers `{query, results, notices, semanticSkipped, receipt}` as JSON text
  (`mcp/recall.output.schema.json`; fixtures `recall.example.json` and `recall.explain.example.json`). Each result is
  the catalog row as `search_knowledge` returns its columns, with `excerpt` (the passage that matched, or the start of
  its text; at most 12 000 characters of excerpts in all), `truncated`, `length` and `score`. With `explain` it adds
  `why`: its `rank`, the keyword arm's `{rank, weight, terms, exact}`, the meaning arm's `{rank, similarity, passage}`,
  `imputed`, and for a linked record `link` `{hop, type, from, direction}`; an arm that did not find it is null.
  `notices` says when semantic search was skipped (the model off or failing: keyword results only) and counts live files
  left out. The `receipt`, `rc1-` and 16 hex characters, hashes the question, its arguments and each result's id and
  revision in order, so the same question over the same records gives the same receipt and a change of any of them
  another. A native memory's `revision` is the memory's own (`memory:` and its revision, which every change of it
  moves), not its catalog row's. A memory held for review, deprecated or past its `valid_until` is never a result.
  Nothing is kept, and no reply holds a vector.
- With `memory.tools` on, `recall` also takes `as_of` (SB-P4-07, `docs/dev/memory.md` section 5): a date and time that
  has passed, never with `after` or `before`. Off, `as_of` is no argument of `recall` (refused as any unknown argument
  is) and its schema is as before; on, a call without it answers byte for byte as without the memory tools. Asked as of
  a moment, the answer is what was true then, with every correction made since: a native memory not marked wrong now
  whose window as it stands now covers that moment, in its words of then (its state then, of its current state and the
  ones it keeps: the one written by then and not yet replaced; for a memory told as true from before it was written,
  its first: its `excerpt` is the start of those words, never a matched passage, while `why.meaning.passage` still
  names the passage of its current words that ranked it), each such result with `asOf` `{revision, status, writtenAt,
  changedAt, recordedAfter}`; any other record
  only when it has not changed since (its source's time, else when it was indexed, at or before then), without `asOf`.
  A memory whose state then was held, from before it came into a scope the caller reads (`memory_history_floor`), or no
  longer kept is left out. The answer adds `asOf` (the moment, in UTC) and `retracted`: at most three memories believed
  then and marked wrong (deprecated) since, newest retraction first, each its row with its words then (`excerpt`,
  `truncated`, `length`), `retractedAt`, `insteadOf` (the result it had replaced through a supersede, or null) and its
  `asOf` state; never among the results, and found by the arms or through that supersede. A memory held now, in the
  trash or in a scope the caller does not read now is in neither list. With `hops`, a hop as of a moment follows a link
  a person or an AI client made only when it was made by then; the map's own links are followed as they stand now, and
  what a hop reaches is judged as of then as every result is. The receipt adds the moment and each belief's id and
  revision after the results (fixture `recall.as_of.example.json`). With `engine.recall` off the call is refused as
  before.
- Every read takes the read predicate for the caller (`docs/dev/memory.md` section 7.1), and every ranking statistic is
  the caller's own. Off (`engine.recall` absent, as in every `rhythm.yaml` written before it) the tool is not listed,
  and a call is answered `isError: true`.
- `engine.searchKnowledge: combined` answers `search_knowledge` from the same list in its own shape: the same columns
  with a 400-character `excerpt`, the same bounds (`limit` 1–50, `query` at most 200 characters), and an empty `query`
  as before. It stays a catalog tool: no native memory's row, its own or its map star's, is in the list it ranks. The engine refuses it at start unless `engine.legacySearch` is false. Absent, every search tool answers as
  before.

Prompt capsules (SB-P3-08, `docs/dev/memory.md` section 7.1):

- `get_prompt_capsule` takes `kind` (`core` or `project`), `project_id` (a project's slug or alias, for `project`
  only), `workspace` (`private` or `shared`, or upstream's `personal` and `company`; default the caller's own: its
  person's private scope with scopes on, else Family) and `if_none_match` (an ETag). It answers upstream's shape as JSON
  text, `{ok, schema: "prompt-capsule-mcp.v1", etag, capsule}`, the capsule `{ok, schema: "prompt-capsule.v1", kind,
  project_id?, scope, workspace, prompt_hash, text, sections, omitted_slots, missing_slots, complete, populated,
  invalid_entries, duplicate_slots, char_count, max_chars}`; with `if_none_match` naming the current ETag, `{ok,
  schema, etag, unchanged: true}` instead. `text` is byte-stable for the same memories and holds no ids or times;
  `etag` is a strong ETag of the whole capsule answer. A refusal is `isError: true` with `{ok: false, schema, code,
  status, error}`: `invalid_request` (400), `forbidden` (403, a scope the caller does not read), `unknown_project`
  (404, with `known_projects`) or `too_many_candidates` (409, over 200 canonical memories in its slots).
- The same answer is the MCP resource `rhythm://capsules/core` (the caller's own scope) and the templates
  `rhythm://capsules/{scope}/core` and `rhythm://capsules/{scope}/projects/{project}`, `application/json`.
- Off (`memory.capsules` absent, as in every `rhythm.yaml` written before it) neither the tool nor the resources are
  listed, and a call is answered `isError: true`. Its answer's schema is `mcp/get_prompt_capsule.output.schema.json`,
  with the fixtures `get_prompt_capsule.example.json`, `.unchanged` and `.refused` (SB-P3-09).

Memory tools (SB-P3-09, `docs/dev/memory.md` sections 5 to 7; upstream's names and arguments where upstream has them):

- Made and listed only with `memory.tools` on. Off (absent, as in every `rhythm.yaml` written before it), none of them
  exists: `tools/list` and every answer are as before, and a call naming one is answered as a call naming no tool.
- `share` (SB-P3-10, Q-157) is made and listed only with `memory.share` on as well, off by default: off, absent or
  empty (every `rhythm.yaml` written before it), or with `memory.tools` off, it does not exist, and a call naming it is
  answered as a call naming no tool. It takes `id`, `workspace` (`shared`, the default, or `private`; upstream's
  `company` and `personal` too) and `expected_revision`; upstream's `team` is not taken (one shared scope), and there is
  no idempotency key (a repeat answers `unchanged`). `shared` moves one of the caller's own private memories into
  Family (its owner, a member of Family); `private` takes a Family memory back to its author's private scope, never the
  caller's own (its author or a Family admin), and only while that author is a current person (else `no_author`, as for
  a memory no person wrote), and never when that would link two people's private records (`invalid`). It answers a
  write's shape (`mcp/share.output.schema.json`, fixtures
  `share.example.json` and `share.refused.example.json`): `changed` names `scope`, and `project` and `capsule` when
  they changed. A repeat answers `unchanged` whatever `expected_revision` it carries; a stale one refuses a move
  (`conflict`). A caller the move left no longer reading the memory (a Family admin who took someone else's memory
  back) is answered `unknown` on a repeat, as for any memory it does not read, so no answer tells it where the memory
  is. Its project follows by slug (the landing scope's project of that name, made there when there is none),
  except into a project held for review or under a name that reads like instructions, when it comes with none, and an
  admin taking a memory back joins only a project already listed in its author's scope; a memory in a capsule slot is
  refused (`forbidden`: a slot is a person's step, which a person's own move leaves); and the map's copies of it are
  dropped and rebuilt where it is now, at most one rebuild asked for every three minutes. The answer to a caller
  that does not read where the memory landed (an admin who took it back) names no project. While `memory.scopes` is
  off every call is refused (`scopes_off`) before anything is read or changed (`docs/dev/memory.md` section 5).
- `supersede` (SB-P4-06, migration 049) is Rhythm's own: upstream supersedes on a model's contradiction verdict while
  storing a memory, and Rhythm runs no model (D-033), so the caller names both memories. It takes `id` (the memory true
  now), `replaces` (an earlier memory of the same scope) and `confirmation`, and no idempotency key. Without
  `confirmation` it answers `action: plan`, writing nothing: `at`, when the earlier memory would stop being true (the
  newer's `valid_from`, else when it was written), and a `confirmation` of 16 hex digits that changes with either
  memory's revision. With that confirmation it applies exactly that plan (`action: superseded`); the tool's
  description tells the client to ask the person first, but the engine does not check who agreed: the plan's answer
  carries the confirmation, so the client that asked can pass it straight back (D-192). Applied, the earlier
  memory's `valid_until` becomes `at` as a change of its own (history `superseded`), a `supersedes` link records what
  replaced it, and it keeps its words and status; any other confirmation is refused (`conflict`) and nothing changes. A plan that would change nothing answers `action: unchanged` with `reason`:
  `replaced` (it replaces it already, so a retry is safe), `ended` (the earlier one had ended by then) or `enclosed`
  (the newer is told as over already, within the earlier one's time). Refused: the same memory twice, a newer memory
  that began at or before the earlier one, or two scopes (`invalid`); either memory not the caller's to change
  (`forbidden`); one held for review (`held`) or marked wrong (`retracted`); and a confirmed (`canonical`) memory that a
  memory not confirmed would replace (`protected`: confirming the newer one first is `set_status canonical`, which an
  AI client may call too, on anything but a capture, section 6.4). Answer: `mcp/supersede.output.schema.json` (fixtures
  `supersede.plan.example.json`, `supersede.example.json`, `supersede.refused.example.json`), no memory's words.
- `link`, `unlink` and `connections` (SB-P4-02, migration 052) are upstream's, stricter (D-031). `link` (`source_id`,
  `target_id`, `type`, `confidence`) joins two memories the caller reads, of one scope (two scopes never link, a
  private memory and a Family one included: `invalid`), from a memory the caller may change (the author lock on the
  source end: `forbidden`); neither held for review (`held`). `type` is one of upstream's but `supersedes`, which is
  `supersede`'s: `relates_to` (the default, no direction, stored with the smaller id first, so the answer's `from` and
  `to` may be `target` and `source`), `caused_by`, `decided`, `about_person`, `part_of_project`, `follows` or
  `drawn_from`; `decided` and `follows` join two episodic memories only (`invalid`). An AI client's link is `inferred`
  (provenance `system`), never `verified`, until a person's own channel links the same pair and type, which confirms
  it; linking again answers `unchanged`. It takes no idempotency key. The link lives in `made_links` (D-067), never in
  the map's own table: with `memory.tools` on every read of the map serves it, to a reader of both memories only,
  through every rebuild, and recall's hops follow it under the read predicate, never to a held memory (D-152); off,
  neither the map nor the hops read a made link, so one an operator's import brought changes no answer. `unlink`
  (`source_id`,
  `target_id`, `type`) removes the made links between the two, either way round, of one type or every type but
  `supersedes` (a supersede's link, undone with `undo` or by marking the newer memory deprecated; naming it is
  `invalid`, and `connections` gives it `may_unlink` false), each only by its maker or whoever may change its source
  memory, either memory for `relates_to`, which has no direction (`forbidden` for any one refuses the whole call); a made
  link marked suggested is removed and kept as a dismissal of its type (`kept`), and the map's own suggestions between
  the two memories' stars (`dismissed`, by type) are dismissed, never served or followed again whatever later rebuilds
  find unless a person takes the dismissal back, by whoever may change either memory. A dismissal is kept apart from the links
  (`memory_link_dismissals`), so it never counts as a link, even between two scopes; a person's link of its type made
  between the two takes it back (an AI client's is made beside it, since 057), and a person sees and takes one back in
  the app (2.2's `app_memories` `dismissals` and `undismiss`, SB-P4-05) and at the terminal (`rhythm undismiss`, run
  17); no MCP tool does. `connections` (`id`, `type`, `limit`
  1-50, default 20) lists the made links
  newest first, then the map's suggestions strongest first, each with its `direction`, `verification` and
  `provenance`, `mine`, `may_unlink`, `at`, `confidence` and the other memory's scope, status, state and a preview of
  at most 120 characters (none for one held for review); only memories the caller reads. A link moves no revision and
  writes no history row. Answers: `mcp/link.output.schema.json`, `mcp/unlink.output.schema.json` and
  `mcp/connections.output.schema.json` (fixtures `link.example.json`, `link.refused.example.json`,
  `unlink.example.json`, `connections.example.json`); only `connections` carries a memory's words, with the notice.
- When a memory was true (SB-P4-06): `update` takes `valid_from` and `valid_until`, times that have passed, and `clear`
  takes `valid_from` (since it was written) and `valid_until` (true still), alone, with no other field, answered as a
  write with `changed: ["validity"]`; a time after now, or an end at or before the start, is refused (`invalid`). When
  its start moves, the memories it replaced end at its new start too, never at or before their own. `clear` also takes
  `retracted_sources`, alone: the flags a retracted source put on a memory come off once the person has checked it.
  `get` and `list_recent` serve each memory's `state` (`current`, `replaced` with `replaced_by`, `ended` or
  `retracted`) and `retracted_sources` (SB-P4-08: retracted memories it was built on). Retracting a memory that
  replaced others (`set_status deprecated`, an `undo` to a deprecated state, `forget`) makes each of those current
  again (history `reopened`) and flags each memory built on it (a `drawn_from` or `caused_by` link, history
  `flagged`); bringing it back (another status, an `undo`, a restore) undoes both (`reclosed`, `unflagged`; a reopened
  memory whose words or window changed since stays as it was left), and the tool's `message` names them. The app's
  `app_memories` answers (2.2) name them in `cascade` (SB-P4-11), and a person takes a memory's flags off there
  (`clear_flags`) and at the terminal (`rhythm clear-flags`) since run 17.
- The stale queue (SB-P4-09, migration 053; upstream's stale queue, staleness pass and `still_true`). A memory's
  `volatility` is the caller's word (D-033): a `state` memory may be out of date once 90 days pass with no change and no
  confirmation, a `volatile` one after 14; a `durable` one, and one with no volatility (every memory saved without
  one), never by age. A nightly pass with `memory.tools` on marks each memory whose time has passed, a bounded piece at a
  time from each scope's own cursor, and takes off a mark that no longer holds; any change to the memory takes it out of
  the queue. The queue is one predicate (053's `memory_stale_reason`): such a mark at the memory's current revision
  (`not_confirmed`), or a flag from a retracted source (`retracted_source`, which wins), on a memory that is live,
  current, not held, not deprecated, not ended and not in the trash. `list_stale` takes `n` (1-50, default 10),
  `workspace` and `cursor`, and answers `{ok, total, stale, next, notice}` (`mcp/list_stale.output.schema.json`;
  fixtures `list_stale.example.json`, `list_stale.flagged.example.json`): the memories the caller reads and may change
  (the author lock), longest unchanged and unconfirmed first, each with `reason`, `since` (its last change or
  confirmation), `found_at`, `confirmed_at`, a 500-character preview and `retracted_sources`; `total` is read in the
  same statement as the page, so it is the length of the list the pages walk. A held memory is never in it.
  `still_true` takes `id` and `expected_revision`, and no idempotency key: whoever may change the memory says it
  still stands. Its checked time moves to now, its mark and its retraction flags come off, and its history records
  `confirmed`, naming why it was in the queue; no revision moves and no version is kept or pruned, so every version
  stays and an `expected_revision` others read stays good. A repeat at the same revision within a day, out of the
  queue, answers `unchanged`. It answers a write's place with `changed` (`["checked"]`, or empty when unchanged),
  `checked_at`, `was` (`not_confirmed`, `retracted_source` or null) and `retracted` (the sources whose flags came off)
  (`mcp/still_true.output.schema.json`; fixtures `still_true.example.json`, `.unchanged`, `.refused`). Refused: a
  memory held for review (`held`), marked wrong (`retracted`) or whose time has ended (`invalid`). A person reads the
  same queue and says still true in the app (2.2's `app_memories` `stale` and `still_true`) and at the terminal
  (`rhythm still-true`) since run 17.
- Digests (SB-P5-13, migration 054; upstream's digest, with its nightly model's paragraph written by the AI client
  instead, D-033, and no source touched, D-032). A digest is of one project (`project`, a slug or alias) or one topic
  tag (`tag`), exactly one, in one scope (`workspace`; left out, the caller's own, as `remember` lands). `digest_sources`
  lists, by one rule (054's `memory_digest_eligible`), the memories it would draw from: live memories of that scope in
  the project (or tagged with one of its aliases) or with the tag, never held, marked wrong, ended, in the trash, in a
  capsule slot, of importance 4 or more, flagged by a source marked wrong, recalled twice (or once in its first 60
  days; 042's counts of the digest's own scope alone, kept only while `engine.recallLog` is on), a digest, or drawn
  into a live digest of its own scope already. It
  answers `{ok, scope, owner, scopes, project, tag, eligible, minimum, maximum, due, waiting, latest, latest_at,
  next_at, basis, sources, message, notice}` (`mcp/digest_sources.output.schema.json`; fixtures
  `digest_sources.example.json`, `.waiting`): `eligible` counts every one, `sources` holds the 50 newest as
  500-character previews, newest first, and `basis` hashes the scope, the key and each source's id and revision. A
  digest is `due` with at least 10 eligible and no live digest of the key in the scope written in the last day
  (`waiting`: `cooldown`, with `next_at`, or `too_few`); `latest` and `latest_at` are null while that digest is held
  for review, though its day still counts. A digest's key is the one it was saved for, never its tags or project as an
  update left them. The same store gives every reader of the scope the same answer,
  and reading it writes nothing. `save_digest` takes the client's paragraph (`content`), that `basis`, the same key and
  `workspace`, and `idempotency_key`; it saves a new memory of the scope, a draft labelled the engine's own (`source`
  `system`, which no AI client, the app or the terminal gives a memory), whose text is the engine's line `[Digest of N
  memories tagged x]` (or `in project slug`) and the paragraph, with a `drawn_from` link from it to each source (an AI
  client's, so inferred) and one audit row, `created`; no source's row, text, tags, revision, versions or history
  changes. Its text is screened as any write's, so it may be held, and a held digest keeps its links. It answers
  `{ok, id, revision, scope, owner, status, scopes, project, tag, drawn_from, unchanged, replayed, held, message}`
  (`mcp/save_digest.output.schema.json`; fixtures `save_digest.example.json`, `.unchanged`, `.refused`). Saving again
  from a basis a live digest of the scope was saved from answers that digest, `unchanged`, whoever asks, so a repeated
  or scheduled run never writes a second one, and saves in a scope are written one at a time whatever their key, so
  two keys that share memories never draw one into two digests; refused: `not_due` (within the day, or too few), `conflict` (the list no
  longer hashes to the basis: list again), `unknown_project`, `invalid`. A digest marked wrong, ended or in the trash
  holds nothing, so its sources are eligible again. `digest` takes the key and `workspace` (left out, any scope the
  caller reads) and answers `{ok, digest, sources, message, notice}` (`mcp/digest.output.schema.json`; fixtures
  `digest.example.json`, `.none`): the newest live digest the caller reads that is not held, as `get` serves a memory,
  or null, and how many memories it was drawn from. It never writes a digest, and reading it writes nothing. When a
  source is marked wrong, the digest is flagged for a check (`retracted_sources`, 049's cascade through `drawn_from`).
- Citations (SB-P4-10, migration 055; story 7; D-014; [../docs/dev/memory.md](../docs/dev/memory.md) 10.4). A memory,
  a Drive original and a stored copy are three identities: a citation joins a memory to the original by the source's
  own id and the revision the memory was written against, and keeps none of the document's words, so no cached copy
  answers for it. `cite` takes `id` and `document` (a Drive file id, its Google address, or its star id from
  `explore_graph`, `drive:<account>:<id>`), and no idempotency key: whoever may change the memory (the author lock), not
  held, cites a document the map holds now as a document, which the caller reads (asked first, so a document the caller
  does not read is never looked up at Drive); with `memory.scopes` on, only one of the memory's own subject or Family's;
  at most 20 a memory. The engine checks the document at Drive itself, one metadata read (no content downloaded, no map
  pass, sync or stored row touched), and the citation records what it found: revision, modified time, name and folders,
  and when; a document Drive cannot give now is not cited (`source_unavailable`). Citing a document cited already
  answers `unchanged` and moves nothing. It answers `{ok, id, revision, scope, owner, status, scopes, citation, created,
  unchanged, message, notice}` (`mcp/cite.output.schema.json`; fixtures `cite.example.json`, `.refused`). `get` of a
  memory that cites documents its reader reads adds `citations` (`mcp/get.output.schema.json`; fixture
  `get.cited.example.json`), each checked at Drive for that answer, four at a time within five seconds: `state` is
  `current` (the cited revision, name and folders), `changed` with `changes` (`edited`, `moved`, `renamed`, `revised`),
  `trashed`, `gone` (deleted for good, or no longer shared with Rhythm's Google account: Drive answers both alike),
  `unreadable` (Drive refuses Rhythm the file, or Google no longer accepts Rhythm's sign-in), `unavailable` (the map's
  newest row for it is missing: the Drive sync's complete listing of the folders Rhythm is given no longer finds it,
  so it was moved out, put in the trash, deleted or no longer shared; it is not checked at Drive, `seen_at` is null and
  the message says since when), or `unchecked` (Drive did not answer, its adapter is off, or the map holds it back
  since a restore until the next rebuild: `seen_at` null, and the message says when it was last checked and what that
  found); `now` is what Drive holds, when readable; `checked_at` and `checked_state` are the last stored check; and
  `message` says it in plain words. A read stores nothing. A memory that cites none, a version read and a held memory
  answer without `citations`, exactly as before. `revalidate` takes `id` and `document` (one of its citations; left
  out, every one): whoever may change the memory, not held, has each checked at Drive now and recorded: a document at
  another revision, name or folders moves the citation there, keeping the revision it moved from (`earlier_revision`,
  `revalidated_at`); one in the trash, gone, unreadable or `unavailable` is recorded as such, its cited revision, name and
  folders kept (an unavailable one only while the map's newest row for it is missing, which 055 checks itself); one
  Drive did not answer for stays as it was, and so does one the map holds back since a restore, which is not checked at
  Drive at all (`get` serves it `unchecked`, saying why); a check is written only while the citation still has the checked time it was
  compared against, so an earlier check never overwrites a later one. A 403 or 429 for one of Drive's usage limits is
  `unchecked`, never `unreadable`. It answers `{ok, id, revision, scope, owner, status, scopes, citations, revalidated,
  unchecked, message, notice}` (`mcp/revalidate.output.schema.json`; fixture `revalidate.example.json`), `revalidated`
  listing every move from and to which revision and state: never silent. Neither tool changes the memory: no revision
  moves, no version is kept, no audit row is written. A citation is served only to a reader of its memory who reads the
  document too, under its newest row's subject on the map (to no one while the map holds no row of it), so a document taken into
  one person's private scope later leaves a Family memory's citation to that person; a held memory's citations to no
  one. Not exported by `rhythm export` yet; the app's Memories view does not show them yet.
- Open loops and two-way commitments (SB-P5-09, migration 058; story 12; upstream's open-loops queue, two-way
  commitments and the resolve actions done, received and not_a_task). An open loop is a memory tagged `task` (the
  caller's own word; a `#task` in the text is one too) and not `task:done`, nor an agent's build note (tagged
  `build-log`, `claude-response`, `codex-response` or `resume-playbook`), that is live, not held, not deprecated, not
  ended and not in the trash: one predicate (058's `memory_open_loop`) every count and list reads. `remember` takes
  `owed_to` (the person promised someone something: that one's name) or `owed_by` (someone promised the person
  something: their name), never both, at most 64 characters: the memory is saved tagged `task` with its commitment,
  `out` or `in`, and the other person's name as upstream's slug of it (a name that reads like instructions to an AI
  client is refused, `invalid`); its answer adds `commitment` (`direction`, `counterparty`: words made from the slug,
  or null when nothing was left to keep) (fixture `remember.commitment.example.json`), and every other write answers
  without it. The direction is the engine's, written once with the memory and never changed; a memory tagged `task`
  with none is a loop the person owes. `list_loops` takes `direction` (`out`, `in` or `all`, the default), `n` (1-50,
  default 10), `workspace` and `cursor`, and answers `{ok, direction, total, you_owe, owed_to_you, loops, next,
  notice}` (`mcp/list_loops.output.schema.json`; fixtures `list_loops.example.json`, `.in`): the open loops the caller
  reads, may change and owns (its person's private ones, and the Family ones its person wrote or no person wrote: a
  Family admin is not listed another member's), newest first, each with its `direction`, `counterparty`, tags,
  project and a 500-character preview; `total` (the asked list's length) and `you_owe` and `owed_to_you` (the two
  directions' lengths, under the same filters) are read in the same statement as the page, so each is the length of
  the list it counts. With `memory.scopes` off every admitted client is listed every loop, as the household shares
  everything. A held memory is never in it. `resolve_loop` takes `id`, `action` and `expected_revision`, and no
  idempotency key: whoever may change the memory (the author lock) closes the loop as a change of its tags. `done`:
  `task:done` joins them and `task` stays (history); `received`: the same, for a loop owed to the person only;
  `not_a_task`: `task` comes off. The tags before are kept as a version, the revision moves, history records
  `resolved` (naming the action and the direction) and `undo` puts the tags back, opening the loop again; it is no
  content write, so it counts toward no burst. A repeat answers `unchanged` and writes nothing, so a loop leaves the
  list once. It answers `{ok, id, revision, scope, owner, status, scopes, project, action, direction, counterparty,
  changed, unchanged, message}` (`mcp/resolve_loop.output.schema.json`; fixtures `resolve_loop.example.json`,
  `.received`, `.unchanged`, `.refused`). Refused: a memory held for review (`held`), marked wrong (`retracted`) or
  whose time has ended (`invalid`), and `done` or `received` for a memory with no `task` tag and `received` for a loop
  the person owes (`invalid`). `rhythm export` writes a memory's commitment on its line and `rhythm import` adds it
  with the memory. The app's Memories view lists both directions (`app_memories` op `loops`, 2.2); the brief heads them.
- The decision ledger (SB-P5-11, migration 060; story 12; upstream's decision capture, outcome action and calibration).
  `remember` takes `decision` (true when the person commits to a meaningful choice), `confidence` (above 0 and at most
  1, kept to the nearest 0.05 within 0.05 to 0.95), `confidence_source` (`stated` or `inferred`, the default with a
  confidence) and `review_by` (a date, at 09:00 in the install's time zone, or a date and time, with an offset or in that
  zone; at most five years on; default 90 days on at 09:00). The confidence, its source and the review date go with
  `decision: true` only, and a decision is no commitment (never with `owed_to` or `owed_by`): each refused `invalid`,
  saving nothing. The memory is written as any `remember` writes one, with its confidence set once, never changed; its
  answer adds `decision` (`confidence`, `confidence_source`, `review_at`, and `clamped`, `highest` or `lowest`, when the
  confidence given was outside 0.05 to 0.95) (fixture `remember.decision.example.json`), and every other write answers
  without it. One predicate (060's `memory_decision_listed`) defines every list of the ledger: a decision that is live,
  not held and not deprecated; `open` (no outcome yet), `due` (its review date has come), `upcoming` (it comes within 48
  hours; migration 069, the window `list_due` reads a due date by, D-214), `resolved` (an outcome,
  `unknown` included) and `all` with its window open, and `scored` (an outcome `right`, `wrong` or `mixed` and a
  confidence, in any window: what calibration rests on). `list_decisions` takes `state` (one of those, default `open`),
  `n` (1-50, default 10), `workspace` and `cursor`, and answers `{ok, state, total, open, due, upcoming, resolved, scored,
  decisions, next, notice}` (`mcp/list_decisions.output.schema.json`; fixtures `list_decisions.example.json`,
  `.scored`): the decisions the caller reads, may change and owns (the open loops' rule), newest first, each with its
  `confidence` and `confidence_source`, `outcome`, `rearms`, `review_at` and `due`, `current`, `edited` (its words
  changed after it was recorded), tags, project and a 500-character preview; the five counts are read in the same
  statement as the page, so each is the length of its list. `record_outcome` takes `id`, `result` and
  `expected_revision`, and no idempotency key and no note (detail is an `append` of its own): `right`, `wrong` and
  `mixed` end the reviews; `unknown` asks again 90 days on, twice, and a third stops the asking (`reviews_done`). It is
  a change of the decision's state under the author lock, not its words: the state before is kept as a version, the
  revision moves, history records `outcome` and `undo` takes it back (060 replaces 041's `undo_memory` so an undo
  compares the decision's state too, its `changed` naming `outcome`); a repeat of `right`, `wrong` or `mixed`, or an
  `unknown` once the asking has stopped, answers `unchanged`, while another `unknown` asks again, so it is not
  idempotent. It answers `{ok, id, revision, scope, owner, status, scopes, project, result, previous, rearms, review_at,
  reviews_done, changed, unchanged, message}` (`mcp/record_outcome.output.schema.json`; fixtures
  `record_outcome.example.json`, `.unknown`, `.unchanged`, `.refused`). Refused: a memory that is no decision
  (`invalid`), one held for review (`held`) or marked wrong (`retracted`). `decision_calibration` takes `source` (`all`,
  `stated` or `inferred`) and `workspace`, reads only, and answers `{ok, ready, source, n, n_stated, n_inferred,
  minimums, brier, gap, direction, bands, headline, topic, kind, line, topic_line, stated, hit, notice}`
  (`mcp/decision_calibration.output.schema.json`; fixtures `decision_calibration.example.json`, `.not_ready`): `n` is
  the scored list's length with `source` `all`, and the length of that source's part of it with `stated` or `inferred`;
  below 10 decisions `ready` is false and every figure is null; each of the five bands of folded confidence gives its
  counts, and its mean confidence, hit rate and 80% Wilson interval only with 5 decisions in it; a topic (a tag) only
  with 5 and outside its interval; `line` says it in words with the number it rests on. `rhythm export` writes a
  decision's confidence and states on its line and `rhythm import` adds them with the memory. The app's Memories view
  lists them (`app_memories` ops `decisions` and `calibration`, 2.2); the brief heads `due` and `upcoming` and gives
  calibration's line.
- Due dates (SB-P5-10, migration 061), with `memory.tools` on: when a memory should come back to the person, as
  upstream's time anchors. `remember` takes `when` (an ISO date, `2026-12-01`, or date and time, `2026-12-01T09:30`,
  read on the install's clocks, D-043, or with `Z` or an offset; at most 5 years ahead; a time the clocks skip or pass
  twice there is refused, `invalid`, and a day is due from its first moment there) and `when_kind` (`due`, `event` or
  `wake`, the default, `due` for a commitment; only with `when`). Without `when` the engine reads the memory's words for
  the one date they name (an ISO date, a day and a month name with or without a year, or a numeric date with a
  four-digit year that reads one way only) and takes it as `due`, `source` `text`; two days (also days joined by `and`,
  `or` or a comma), a weekday without its date, a relative day (`tomorrow`, `next week`), a date that reads two ways (a
  numeric date that reads as two days or has a two-digit year, a day number on each side of a month name: "5 Dec 10
  guests", a number after a date with no year: "1 December '27"), a range (a dash, `to`, `through` or `until`: "from 16
  to 18 November"), a weekday that disagrees with its date, a day that does not exist, has passed or is over 5 years
  ahead give none, never guessed. A time of day after a date is neither its day nor its year, and is not read; `may`,
  `march` and `mar` in lower case are a month only with an ordinal day or a year ("march 20th"). Its answer adds `due`
  (`at`, `local`, `zone`, `kind`, `source`; fixture `remember.due.example.json`) when it saved one for a memory not
  held, or `due_not_taken` (`reason` and `message`; fixture `remember.due_not_taken.example.json`) when the words named
  a date and none was taken; the message never repeats a held memory's words. Every other write answers without them,
  and so does a `remember` whose words name no date. A decision (`decision: true`) takes none: `when` or `when_kind`
  with it is refused (`invalid`) and its words are not read for a date, since it comes up for review at its `review_by`
  (060). `get` and `list_recent` give a memory's `due` when it has one and is not held (fixture `get.due.example.json`).
  `list_due` takes `window` (`overdue`, `upcoming`, `later` or `all`, the default), `n` (1-50, default 10), `workspace`
  and `cursor`, and answers `{ok, window, total, overdue, upcoming, later, zone, items, next, notice}`
  (`mcp/list_due.output.schema.json`; fixtures `list_due.example.json`, `.overdue`): one predicate (061's `memory_due`:
  a due date, not `task:done`, live, not held, not deprecated, not ended) over the memories the caller reads, may change
  and owns (as `list_loops`), soonest first, each with its `due`, `window`, `loop` (the open loop's direction when it is
  one, else null), tags, project and a 500-character preview; `total` and the three windows' lengths are read in the
  same statement as the page, so each is the length of the list it counts. `resolve_due` takes `id`, `action` (`snooze`
  or `clear_date`), `until` (for `snooze`, as `when`, still to come) and `expected_revision`, and no idempotency key:
  whoever may change the memory moves its due date to `until` (a memory with none takes one, kind `wake`) or takes it
  off, as a change: the state before is kept as a version, the revision moves, history records `due_changed` (a date
  written with its memory is `due_set`) and `undo` puts it back. A repeat answers `unchanged` and writes nothing. It
  answers `{ok, id, revision, scope, owner, status, scopes, project, action, due, before, changed, unchanged, message}`
  (`mcp/resolve_due.output.schema.json`; fixtures `resolve_due.example.json`, `.cleared`, `.unchanged`, `.refused`).
  Refused: a memory held for review (`held`), marked wrong (`retracted`) or whose time has ended, a decision, which
  takes no due date (D-214: its review date is its own), and an `until` that has come or is over 5 years ahead
  (`invalid`). `rhythm export` writes a memory's due date on its line and `rhythm import` puts it on the memory it adds,
  unless that memory is a decision, and since migration 069 a decision is never on the due list, whatever a version kept by
  hand holds. The app's Memories view lists them (`app_memories` op `due`, 2.2); the brief heads the overdue and
  upcoming windows.
- Duplicates (SB-P5-02, migration 063), with `memory.tools` on: a `remember` whose words are close to a memory already
  in its own scope is saved as any other and flagged for the person's review; nothing is refused, merged or changed
  (D-211). The score is the live model's dot product of the two texts' vectors (the words alone, the recipe the
  thresholds were tuned on) at or above the install's duplicate threshold (today's 0.85, or with
  `adapters.embeddings.thresholds: tuned` the live model's own; docs/configuration.md), or 1 for the same words, case
  and spacing aside, which is all that counts with the model off. Its answer adds `duplicates` (at most 3 `{id, score}`,
  closest first; fixture `remember.duplicate.example.json`) and says what the person may do; a held memory is not
  checked and its answer names none, and a held memory is never named. A keyed retry names the copies the memory's
  flags name then. `get` and `list_recent` add `duplicate_of` (the same shape) to a memory with a flag that stands: both
  memories live (in the catalog, not in the trash, not held, not marked wrong, their window open) and in one scope
  (fixture `get.duplicate.example.json`); every other memory answers as before. `list_duplicates` takes `n` (1-50,
  default 10), `workspace` and `cursor` and answers `{ok, total, duplicates, next, notice}`
  (`mcp/list_duplicates.output.schema.json`; fixture `list_duplicates.example.json`): one predicate (063's
  `memory_duplicate_listed`) over the memories the caller reads and may change, newest first, each with `duplicate_of`
  and a 500-character preview; `total` is read in the same statement, so it is the length of the list. `update`'s
  `clear` takes `duplicate_of`, alone, to keep both (fixture `update.duplicate_kept.example.json`): the flags `duplicate_of`
  names come off (since 068 no others: not a pair named a contradiction, nor one whose copy is in the trash or held),
  history records `flags_cleared`, nothing else changes, and a repeat answers `unchanged`. The review also ends when
  either memory is forgotten, marked wrong or replaced (`supersede`), and a move between scopes takes the flags off.
  History names the flag `duplicate_flagged`. Since migration 068 `append`, `update` and `undo` are checked as `remember`
  is, a later check takes off a flag whose score fell below the threshold, and a pair somebody kept both of is not flagged
  again. `duplicate_of` names at most 3, the closest, whatever stands. A flag from 0.95, a copy for certain, wins over a link on save: no automatic link is drawn between such a pair; below
  that the flag and the link both stand.
- Contradiction candidates (SB-P5-03, migration 068), with `memory.tools` on. A write whose words are close to memories of
  its own scope at or above the install's contradiction threshold (today's 0.85, the duplicate flag's, or with
  `adapters.embeddings.thresholds: tuned` the live model's own; docs/configuration.md; never above the flag's) adds
  `contradiction_candidates` (at most 5 at the moment of a write, 10 standing, each `{id, score, proposed}`, closest first,
  no words; fixture `remember.contradiction.example.json`) and says what the person may do; the same words are a copy and
  no candidate; with the model off there are none. Nothing is refused or merged and neither memory changes (D-033, D-211);
  a held memory is not checked and is never named; a candidate is only ever of the memory's own scope. `get` and
  `list_recent` add `contradictions` (the same shape; fixture `get.contradiction.example.json`) while both are live and no
  person has settled them. `list_contradictions` takes `n` (1-50, default 10), `workspace` and `cursor` and answers
  `{ok, total, contradictions, next, notice}` (`mcp/list_contradictions.output.schema.json`; fixture
  `list_contradictions.example.json`): one predicate (068's `memory_contradiction_listed`) over the pairs the caller reads and
  may change (either memory), newest first; a pair also flagged as a possible copy is listed by `list_duplicates` until
  somebody names it a contradiction, and then by this alone; `total` is read in the same statement. `propose_contradiction`
  takes `id`, `conflicting_id` and `reason` (at most 200 characters; one that reads like instructions is refused) and answers
  `mcp/propose_contradiction.output.schema.json` (fixtures `propose_contradiction.example.json`,
  `propose_contradiction.not_offered.example.json`): an id not offered for that memory is refused `not_offered`, the same
  whatever it names; a pair a person settled is refused `settled`; a repeat answers `unchanged`. An automatic link between the
  two comes off when the caller may change either memory, and otherwise stays. Only a person settles one,
  through the app's `settle_contradiction` (section 2.2): `keep_both`, or the one named replaces the other. While a pair is
  queued, an AI client's `supersede` of it, and any change of its that would end the earlier memory (`set_status`
  deprecated, `forget`, `update` `valid_until`, `undo`), is refused `needs_person` (fixture
  `supersede.needs_person.example.json`), and it may still withdraw the newer memory it saved. A change that finds the earlier
  memory ended already ends nothing and is not refused. Once a pair is named, the earlier memory stays protected while the newer
  is in the trash, held or ended, until a person settles it or a memory is deleted for good, and its words are kept too: an
  `update` of its `content`, an `append` or an `undo` that changes them is refused `needs_person`. An offer nobody has named holds
  only while both memories are live, and takes an edit (Q-161). `share` of either memory of a queued pair is refused `needs_person`
  too, and a person's move takes the pair off. A person's settling holds against an AI client as well, also `needs_person`: of a pair
  kept both, neither memory may be ended while the other is live or replaced (by the other, or by a third), and of a replacement, the
  replaced memory may not be brought back (`undo`, `update` `valid_until`) nor the one that stands be replaced by it. A retry of
  the replacement the person settled on answers `unchanged`. A settling decides which memories stand, not their words for
  ever, so an edit of a settled memory is an ordinary edit. A settled pair stays settled through a move between scopes, so it is
  not offered again; across scopes a proposal answers `not_offered`. It does not leave its install: `rhythm export` carries no row
  of a pair, so after an import a settled pair can be queued again (D-222). An AI client's `update` `clear` `duplicate_of` takes the flag off and the pair stays queued, unsettled. A change of
  either memory's words takes off an offer nobody has named that they no longer reach. History names `contradiction_proposed` and
  `contradiction_settled`.
- The brief (SB-P5-08, migration 069; story 12), with `memory.tools` on: `brief` takes `project` (a slug or alias of at most 128 characters, naming its
  standing instructions and narrowing nothing else) and `workspace`, and answers `mcp/brief.output.schema.json` (fixtures
  `brief.example.json`, `brief.quiet.example.json`): `{ok, project, zone, says, due, decisions, you_owe, owed_to_you, stale,
  insights, contradictions, duplicates, held, standing, calibration, changes, notice}`. Each section is `{total, items}`, the head of the
  list its own tool walks and that list's whole length, its items in that tool's shape: `due` is `list_due`'s overdue and upcoming
  windows (`overdue`, `upcoming`, `total` the two together, at most 5), `decisions` `list_decisions`' `due` and `upcoming` (the same
  three counts, `overdue` that list's `due`, at most 3), `you_owe` and `owed_to_you` `list_loops`' `out` and `in` (at most 5 each),
  `stale` `list_stale` (at most 2), `insights` `list_insight_stream`'s `proposed` (at most 1), `contradictions` and `duplicates`
  their tools' (at most 3 each), `standing` `list_standing`'s with that `project`, those that may fire (at most 3, none with no
  project). `held` is a number with no words: how many memories the caller reads wait in the review queue, by that queue's own
  predicates, since only a person releases one. `calibration` is `{ready, n, min_decisions, line}`, `line` only once ready and `n` the scored list's length. `changes` is
  `{total, changes, truncated, since, groups}`: an AI client's changes in the last 48 hours in groups, none of a memory held now or in
  the trash, of the `workspace` when one is named, the newest 5 groups, each `{group, family, person, client, changes, memories, from, until, capped}` with the key `undo`
  takes and no words. A change left out ends its group, so a key undoes what its group counts. `truncated`: more were made than one read takes (200), so each count is at
  least what it says. `says` is the counts in a line ("or more groups" when truncated), or "Nothing needs attention.". A project the caller reads none by adds a notice,
  and `standing` is empty. It writes nothing. One due definition (D-214): a decision's date is its review date and any other memory's
  its due date, a memory has one, and one window rule reads either. `list_standing` takes `project` too, last (a slug or alias): only
  those that fire inside it and nowhere else, never a held one. Off, neither the tool nor the argument exists.
- Standing instructions (SB-P5-12, migration 065), with `memory.tools` on: `remember` takes `standing` (true: a note to
  apply whenever its topic comes up, words at most 500 characters and 50 in a scope, or refused with nothing saved,
  `too_large` and `limit`; never with `decision`, `owed_by`, `owed_to`, `when` or a review date, or as a task, its tag or a
  `#task` in its words, `invalid`, and its words are not read for a due date), and its answer adds `standing: true` (fixtures
  `remember.standing.example.json` and `remember.standing_limit.example.json`); its message says that `recall` shows it only
  while one can fire, and never for a held one. An `update` or `append` past the 500 characters is refused too
  (`too_large`), and so are an `update` that adds the tag `task` and a `resolve_due` snooze (`invalid`), until it is stopped.
  `get` adds `standing: true` to one, never to a held one (fixture `get.standing.example.json`); every other memory answers
  as before. `recall` adds `standing` after `query`, only when one fired and never as of a moment (fixture
  `recall.standing.example.json`): `{notice, threshold, model, fired}`, at most 2 in `fired`, closest first, each `{id,
  revision, scope, owner, author, source, status, unconfirmed, project, createdAt, content, score}`, `unconfirmed` true while no person
  has confirmed it (status draft: an AI client saved it, and the engine can't tell that a person asked for it, as `notice` says); the receipt stays over the results,
  so it is the one the recall log keeps; with none fired the whole answer is as before, byte for byte. A question
  fires one whose score under the live model reaches the threshold (`adapters.embeddings.thresholds`,
  `docs/configuration.md`), of those the caller reads that are live (in the catalog, not in the trash, not held, not
  stopped, not marked wrong, their window open) and within the oldest 50 of their scope; with no model none fires. `recall`
  takes `project` too, last (a slug or alias, at most 128 characters): one in a project (or tagged with an alias of its
  scope's project) fires only when `project` names it, and `project` narrows nothing else; a name the caller reads no
  project by adds a notice, and a check that fails adds another and fails nothing. Off, `project` is no argument of `recall`.
  `list_standing` takes `n` (1-50, default 10), `workspace` and `cursor` and answers `{ok, total, firing, held, over_limit,
  max_in_scope, max_fires, threshold, model, standing, next, notice}` (`mcp/list_standing.output.schema.json`; fixture
  `list_standing.example.json`): one predicate (065's `memory_standing_rows`) over what the caller reads, oldest first, each
  `{id, revision, scope, owner, author, source, status, project, created_at, content, held, fires, reason}`, a held one
  without its words, tags or project; `reason` is `held`, `project_held` (in a project held for review, which no question
  can name; counted in `held`, its words kept), `over_limit` or null; `model` is null while none can fire, with `engine.recall`
  off as with the embeddings model off, and `notice` then says which.
  `stop_standing` takes `id` and `expected_revision` (`mcp/stop_standing.output.schema.json`; fixtures
  `stop_standing.example.json`, `stop_standing.unchanged.example.json`, `stop_standing.refused.example.json`): whoever may
  change the memory stops it, it stays a memory, history records `standing_stopped` (`standing_set` when it was saved), and
  `undo` starts it again; a repeat answers `unchanged`, a memory that never was one is `invalid`, a held one `held` and a
  deprecated one `retracted`.
- One resolve for every queue (SB-P5-15, migration 071; story 12), with `memory.tools` on: `resolve` takes `id`, `action`, `result` (with
  `outcome`), `until` (with `snooze`), `note` (with a candidate or an insight), `insight_id` (with `use_candidate`) and `expected_revision`
  (with a memory), and refuses (`invalid`) an argument that does not go with the action (`mcp/resolve.output.schema.json`; fixtures
  `resolve.example.json`, `resolve.snoozed.example.json`, `resolve.insight.example.json`, `resolve.candidate.example.json`,
  `resolve.unchanged.example.json`, `resolve.refused.example.json`). `action`, by `queue`: `stale` `still_true`; `loops` `done`, `received`,
  `not_a_task`; `decisions` `outcome`; `due` `snooze`, `clear_date`; `standing` `stop_standing`; `duplicates` `keep_copy`;
  `insight_candidates` `use_candidate`, `reject_candidate`; `insights` `confirm_insight`, `dismiss_insight`. Each is that queue's own step, with
  its author lock, audit row and words. It answers `{ok, queue, action, id, unchanged, still_counted, newly_counted, revision, scope, owner, status,
  scopes, project, changed, due, before, result, previous, review_at, review, view, decision, note, insight_id, message, notice}`: a write's place of
  a memory, `review` and `view` of an insight, `decision`, `note`, `insight_id` and `notice` of a candidate, the rest null, and never a
  memory's words. An item leaves its queue once: `unchanged` true means nothing was written, because it was no longer waiting, in that word
  or another, and `message` says so and which tool changes it from there (`record_outcome`, `resolve_due`, `still_true`, `review_insight`'s
  `reopen`, `undo`). `still_counted` true: the brief's section for that queue counted it and still does, as after a snooze to within
  48 hours or a copy with a further flag standing, which takes `keep_copy` again. `newly_counted` true: it did not and now does, so that
  count rose by one, only after a snooze from past the 48 hours into them, which answers `still_counted` false. `release`, `settle_keep_both` and `settle_replace` are a person's: `needs_person`, alike whatever the id
  names, with nothing read. `held` a memory held for review, `retracted` one marked wrong, `unknown` no item the caller reads by that id,
  `forbidden` not this login's (the author lock, or an insight this login saved), `conflict` a stale expected revision.
- Protection (SB-P5-04, migration 063): `supersede` refuses (`protected`, fixtures `supersede.refused.example.json`
  and `supersede.protected.example.json`) a draft replacing a confirmed memory or one of importance 4 or 5; a memory
  saved from a session transcript (`source` `claude-code`, `codex-session` or `cursor-session`) replacing one of another
  source; and replacing a memory in a capsule slot, which a person takes out of its slot first. A retry of a supersede
  already applied answers `unchanged` as before. `digest` adds `edited: true` when the digest's words or tags were
  changed since it was written (a person's or a client's edit marks it theirs); an untouched one answers as before.
- Insight candidates (SB-P5-05, migration 064). A nightly pass, made only with `memory.tools` on and run only once the
  local embeddings model has loaded, pairs an older and a newer memory of one scope, written 30 or more days apart, both
  live, not held, not marked wrong, not ended and not the engine's own (a digest), when their words are alike (at or
  above the insight threshold the install reads, `adapters.embeddings.thresholds`: 0.80 today) or both carry
  importance 3 or more; each memory keeps its best three by upstream's score, and a pair is written once whatever
  becomes of it (a waiting one whose two memories both moved to another scope is found again there, the same id). `list_insight_candidates` takes `n` (1-50, default 10), `workspace`, `cursor` and `dry_run`, and
  answers `{ok, dry_run, total, items, next, notice}` (`mcp/list_insight_candidates.output.schema.json`; fixtures
  `list_insight_candidates.example.json`, `.dry_run`): one predicate (064's `memory_insight_listed`: waiting, both
  memories still eligible and of its scope) over the pairs whose two memories the caller reads, best first (`score`),
  each with its `id`, `scope`, `owner`, `signals` (`similar`, `weighty`), `similarity` and the `model` that
  measured it, `gap_days`, `found_at`, and both memories, `older` and `newer` (`id`, `revision`, `created_at`,
  `importance`, a 500-character `content` preview, `truncated`); `total` is read in the same statement as the page.
  With `dry_run` true it answers a sample of the pairs the next pass would add, from the start of its walk in the scopes
  the caller reads (at most 10 memories a scope, each compared with at most 300 earlier memories where the pass compares
  up to 2000, so for a memory with more the pass may keep other pairs), with `id` and `found_at` null and no `next`,
  and writes nothing (refused `unavailable` while the model is not loaded, `invalid` with a `cursor`).
  `resolve_insight_candidate` takes `id` and `action` (`use`: an insight was written from the pair; `reject`: it says
  nothing worth saying), and no idempotency key: any login that reads the pair's scope decides it; it leaves every list
  and the pass never offers the pair again; no memory changes (no revision, version or history row), and the decision is
  kept with who made it, when, the channel and the client. A repeat answers `unchanged`; the other decision on a decided
  candidate is refused (`invalid`); a candidate whose memory since was held, trashed, marked wrong, ended or moved to
  another scope is `unknown`. It answers `{ok, id, action, status, unchanged, decided_at, message}`
  (`mcp/resolve_insight_candidate.output.schema.json`; fixtures `resolve_insight_candidate.example.json`,
  `.rejected`, `.unchanged`, `.refused`). Since SB-P5-06 (migration 066) it also takes `note` (1-500 characters, why it
  was declined or used) and `insight_id` (with `use`: the insight written from the pair, one the caller reads, citing
  both memories as sources of kind `memory` with each memory's id as locator, and no wider than the pair, else
  `invalid`), and answers both beside the rest, with `notice`, the note being stored data; a repeat answers what it was
  first told, `insight_id` only when the caller reads that insight's scope (else null). And
  `list_insight_candidates` takes `state` (`waiting`, the default; `used`; `rejected`, what was declined; not with
  `dry_run`), and each item says `status`, `decided_at`, `note` and `insight_id` (null when the caller does not read that
  insight's scope), and its `notice` names notes as stored data; fixture `list_insight_candidates.rejected.example.json`. The daily insight template
  (`harness/chatgpt/daily-insight.md`) reads them when they are listed, and records what it declines.
- The one insight stream (SB-P5-06, migration 066; D-018, D-031): `list_insight_stream` takes `view` (`proposed`, the
  default, `older`, `confirmed`, `snoozed`, `closed` or `all`), `n` (1-50, default 10), `workspace` and `cursor`, and answers
  `{ok, view, total, proposed, older, confirmed, snoozed, closed, candidates, day, items, next, notice}`
  (`mcp/list_insight_stream.output.schema.json`; fixtures `list_insight_stream.example.json`, `.quiet`): one definition
  (066's `insight_stream_view`) puts each insight the caller reads in one view, `confirmed` only by its review, so every
  count is the length of its list, read in the statement that makes the page. `older` is an insight nobody reviewed a week after it became one to
  show: still listed and still reviewable, no longer counted as waiting, so what `proposed` and the brief count stays bounded. Each item has `id`, `view`, `review`
  (`proposed`, `confirmed`, `dismissed`), `reviewed_at`, `snooze_until`, `snooze_ended`, `shown_at` (when it became one to
  show, newest first), `scope`, `owner`, `candidates` and `insight`, the record as `list_insights` gives it, which keeps
  its status (`snoozed` until the next feedback). A snooze ends by the clock at every read and writes nothing. `day` says
  what today came to in the install's time zone: `outcome` `saved` (an insight saved today, or the one the newest run
  names), `quiet`, `blocked` or `none`, a quiet or blocked run's `note`, `insight_id`, `declined` (candidates declined today in the scopes the caller reads) and `says`, the
  engine's own words. `review_insight` takes `id`, `action` (`confirm`, `dismiss`, `reopen`) and `note`, and answers
  `{ok, id, action, review, view, unchanged, reviewed_at, message}` (`mcp/review_insight.output.schema.json`; fixtures
  `review_insight.example.json`, `.unchanged`, `.reopened`, `.closed`, `.refused`). `reopen` takes the review back to
  `proposed`, and `view` says where that puts it: an insight marked not useful or handled stays `closed` until new
  feedback, one with a snooze in force `snoozed`. Who may: a private insight's owner; a Family insight's
  saver's person or an admin; one no person saved, an admin; with scopes off any login; never, over MCP, the login that
  saved it (`forbidden`); an insight a later one replaced is `invalid`, whatever feedback it had since, one the caller does
  not read `unknown`. It changes the review alone, and no memory: confirming does not make an insight recallable. The same
  rule holds a confirmed insight against `save_insight`'s `supersedes`: a login that may not review it, or that saved it,
  is refused and nothing is saved (a refusal naming `supersedes`); one that may has its `replaced` event in the history. `insight_history` takes `id`
  and `limit` (1-100, default 50), and answers `{ok, id, review, total, items, notice}`
  (`mcp/insight_history.output.schema.json`; fixture `insight_history.example.json`): each feedback and review, newest
  first, with `event` (`feedback`, `confirmed`, `dismissed`, `reopened`, `replaced`), `value`, `until`, `note`, `person`,
  `channel`, `client` and `at`. The app's feedback answers as
  before, and with `memory.tools` on is kept here as well; a feedback from before that names no one. The app does not
  show reviews yet: `app_insights` is unchanged.
- With `memory.tools` on, the handshake (`initialize`, and the newest protocol's `server/discover`) also carries
  `instructions`: the block in `harness/memory.md`, byte for byte (SB-P3-20, `Mcp/MemoryInstructions.cs`). Off, the
  handshake answers exactly as before, with no `instructions`.
- Each takes its arguments by upstream's names: `remember` (`content`, `tags`, `project`, `source`, `volatility`,
  `workspace`, `kind`, `importance`, `idempotency_key`, `owed_by`, `owed_to`, `decision`, `confidence`, `confidence_source`, `review_by`, `when`,
  `when_kind`), `append` (`id`, `addition`, `expected_revision`,
  `idempotency_key`), `update` (`id`, `content`, `tags`, `volatility`, `kind`, `importance`, `project`, `clear`,
  `expected_revision`, `idempotency_key`), `get` (`id`, `version`), `list_recent` (`n` 1-50, `tag`, `after`, `before`,
  `workspace`, `actor`, `project`, `kind`, `status`, `cursor`, `in_trash`), `set_status` (`id`, `status`,
  `expected_revision`, `idempotency_key`), `forget` (`id`, `expected_revision`, `idempotency_key`), `undo` (`id`,
  `to_version`, `group`, `expected_revision`, `nonce`), `history` (`id`, `before`, `limit` 1-100), `list_projects`
  (`workspace`, `include_archived`), `list_teams` (none), `supersede` (`id`, `replaces`, `confirmation`), `link`
  (`source_id`, `target_id`, `type`, `confidence`), `unlink` (`source_id`, `target_id`, `type`), `connections`
  (`id`, `type`, `limit`), `list_stale` (`n` 1-50, `workspace`, `cursor`), `still_true` (`id`, `expected_revision`),
  `digest_sources` (`project`, `tag`, `workspace`), `save_digest` (`content`, `basis`, `project`, `tag`, `workspace`,
  `idempotency_key`), `digest` (`project`, `tag`, `workspace`), `cite` (`id`, `document`), `revalidate` (`id`,
  `document`), `list_loops` (`direction`, `n` 1-50, `workspace`, `cursor`), `resolve_loop` (`id`, `action`,
  `expected_revision`), `list_decisions` (`state`, `n` 1-50, `workspace`, `cursor`), `record_outcome` (`id`, `result`,
  `expected_revision`), `decision_calibration` (`source`, `workspace`), `list_due` (`window`, `n` 1-50, `workspace`,
  `cursor`), `resolve_due` (`id`, `action`, `until`, `expected_revision`), `list_contradictions` (`n` 1-50, `workspace`, `cursor`),
  `propose_contradiction` (`id`, `conflicting_id`, `reason`), `list_duplicates` (`n` 1-50, `workspace`,
  `cursor`), `list_insight_candidates` (`n` 1-50,
  `workspace`, `cursor`, `dry_run`, `state`), `resolve_insight_candidate` (`id`, `action`, `note`, `insight_id`),
  `list_insight_stream` (`view`, `n` 1-50, `workspace`, `cursor`), `review_insight` (`id`, `action`, `note`),
  `insight_history` (`id`, `limit` 1-100), `list_standing` (`n` 1-50, `workspace`, `cursor`, `project`), `stop_standing` (`id`,
  `expected_revision`), `brief` (`project`, `workspace`) and `resolve` (`id`, `action`, `result`, `until`, `note`, `insight_id`,
  `expected_revision`); `update`
  also takes `valid_from` and `valid_until`, after its others. `workspace` is `private` or `shared`, or upstream's
  `personal` and `company` (`family` too). Upstream's `team`, `append`'s `when` and `when_kind`, and
  `remember`'s `valid_from` and `valid_until`, are not taken (one shared scope; phases 4 and 5: a new memory's window is
  set with `update`), and a call naming one is told which arguments the tool takes.
- The channel is not an argument. Every tool reads and writes as an AI client (`mcp`), fixed in its handler, never from
  an argument, a header or the client's name. The name is recorded on the audit trail as the MCP request gives it
  (`clientInfo`): `/mcp` is stateless, so a client on protocol 2026-07-28 or later, which names itself with each
  request, is recorded by name, and one on an older protocol, which names itself only at `initialize`, with none. So an AI
  client never releases a hold (an undo of a release holds the memory again, and undoing that keeps the hold), never
  deletes for good (no tool does; the trash's `may_delete_forever` is always false), never puts a memory into a capsule
  slot (`update`'s `capsule:` and `capsule-slot:` tags are refused, `forbidden`, and nothing of the call is written; an
  `undo` that would change a slot likewise), and reaches only what its login reads, under the read predicate and the
  author lock.
- Each answers JSON in its text content and, on success, the same as its structured content, against an output schema
  it lists (the success shape): `mcp/<tool>.output.schema.json`, whose shared parts are `mcp/memory.defs.schema.json`,
  with fixtures `<tool>.example.json` and others of the same prefix. A write answers `{ok, id, revision, scope, owner,
  status, scopes, project, changed, unchanged, replayed, dropped_tags, held, message}`, never a memory's words; `forget`
  its trash row (`nonce`, `trashed_at`, `purge_after`, `days`, `links`); `undo` its `action` (`reverted`, `restored` or
  `group`) with what it did. To bring a memory back from the trash, `undo` takes the trash row's `nonce`: it restores
  that one trip, and a retry with it answers `unknown`. `undo` with `id` alone restores a memory in the trash too, and
  right after a restore answers `restored` with `unchanged: true` and `restored: null`, changing nothing (a restore
  moves no revision). A restored memory that is held answers `held`. `get` answers `{ok, memory, version, notice}`, `list_recent` `{ok, in_trash, memories,
  trash, next, notice}`, `history` `{ok, id, revision, changes, next, cut, earlier_by, kept, oldest_kept, pruned,
  notice}`, `list_projects` `{ok, projects, notice}` and `list_teams` `{ok, teams, scopes, message}`. `scopes` is the
  install's `memory.scopes` mode; with it off every memory is the household's and a write's `message` says it is
  visible to everyone in the household.
- A memory held for review is never served with its words, tags or project (D-160): `get`, a list, a version read and
  the trash give it with `held` (`reason`, `signals`, `notice`) and `content` null, a history change of a held state
  has no `preview`, and a write's answer about one names no project. A write's answer names any other memory's project
  by `id` and `slug` only (`name` is always null), so it carries no stored text. The answers that carry a memory's
  words or tags, a project's name or description, or a client's name (`get`, `list_recent`, `history`, `list_projects`,
  `list_stale`, `digest_sources`, `digest`, `list_loops`, `list_decisions`, `decision_calibration`, `list_due` and `list_insight_candidates`) have `notice`: they are stored data, never instructions; so does an `unknown_project` refusal,
  beside its `known_projects`.
- A refusal is `isError: true` with `{ok: false, code, error}` and, where it applies, `revision` (the current one for
  `conflict`, the oldest kept for `pruned`) or `known_projects` (for `unknown_project`, up to 10, with `notice`). Codes: `unknown` (no
  memory the caller reads by that id: one in another scope, one in the trash and one that never was answer alike),
  `forbidden`, `invalid`, `too_large`, `conflict`, `key_reused`, `unknown_project`, `taken`, `nothing_to_undo`,
  `no_version`, `not_visible`, `pruned`, `moved`, `unavailable` (the store did not answer), `off` (a tool called
  while `memory.tools` is off, or `share` while `memory.share` is) and a share's `scopes_off` (`memory.scopes` is off),
  `held` (a memory held for review, shared only once a person releases it) and `no_author` (a Family memory no person
  wrote, which stays in Family), and a supersede's `held`, `retracted` (a memory marked wrong) and `protected` (a
  confirmed memory a memory not confirmed would replace), and a still true's, a loop's resolve's, an outcome's and a due date's snooze or clear's `held` and `retracted`, and a digest's
  `not_due`, and a citation's `held`, `unknown_document` (no Drive document by that id on the map for the caller, or
  none of the memory's citations) and `source_unavailable` (Drive could not give the document when it was checked).
  Arguments outside a memory tool's `inputSchema` (below) are refused with this same body, code `invalid`, the `error`
  naming the argument and what the tool takes. Hints: `get`, `list_recent`, `history`, `list_projects`, `list_teams`,
  `list_stale`, `digest_sources`, `digest`, `list_loops`, `list_decisions`, `decision_calibration`, `list_due` and `list_insight_candidates` are read only; `forget` is the one destructive tool; `set_status`,
  `forget`, `share`, `supersede`, `still_true`, `save_digest`, `cite`, `revalidate`, `resolve_loop`, `resolve_due`, `resolve_insight_candidate` and `resolve` are idempotent (`record_outcome` is not: another `unknown` asks again); `get`, `cite`
  and `revalidate` alone reach outside Rhythm (open world), each checking a cited document at Drive (`get` only for a
  memory that cites one).

Generalizing while importing (the engine agent's job, recorded in the import manifest):

- Tool descriptions MUST NOT name people, the product owner's household or a hosting provider. Where a
  description needs people, it is a template filled from `people` in `rhythm.yaml`.
- `subject` arguments take a person id from config or `household`. No subject is hardcoded.
- The provider-specific `carelinkSource` (`csv` | `live`) argument of `investigate_health`,
  `evaluate_health_hypothesis`, `query_health_metrics` and `compare_health_trends` becomes two optional,
  vendor-neutral ones: `provider` keeps one source for every metric, and `glucoseSource` (a provider id, such as
  `nightscout`, `csv` or `carelink-live`) keeps glucose readings (`glucose`, `sensor_glucose`, `blood_glucose`)
  to that one provider and leaves every other metric as it is. `carelinkSource` stays accepted as a deprecated alias
  of `glucoseSource`: `csv` and `live` select the CareLink plugin's providers `carelink-csv` and `carelink-live`;
  `glucoseSource` wins when both are given.
- The dashboard design guidance now inside `save_dashboard`'s description moves to an MCP resource
  `rhythm://guides/dashboards` (and a prompt of the same name), so every harness gets it.
- `log_insight_run`'s `generatedBy` default becomes `harness-daily`; any short label is accepted.

Composable layouts (`docs/dev/screens.md` 10, piece 3):

- `save_dashboard` takes an optional `layout`, `{parts: [{id, kind, x, y, w, h, focus?, dashboard?}]}`, with a new
  dashboard or a next version: 1-12 parts on 12 columns by 12 rows of cells, exactly one `page`, the app's own `map`
  (at most 2, each with an optional `focus`), `glucose`, `recovery`, `training`, `changed` and `insights` parts (one
  each), and up to 4 `card` parts naming another dashboard; no two overlap, and each is at least its kind's smallest
  size (`page` 3 × 3, `map` 4 × 4, `insights` 3 × 2, the others 2 × 2). A focus has at least one of `query`, `near`
  (`{star, depth}`), `types`, `providers`, `groups`, `owners` and `links`. A focus star must be one the caller can read
  and a card's dashboard must exist and not be this one, checked only for new parts and changed references. Without
  `layout`, a new dashboard is its page alone and a next version keeps the newest version's arrangement;
  `restoreVersion` brings that version back as it was last arranged. A third form, `dashboardId`, `layout`, `note` and
  an optional `layoutRevision`, arranges the newest version in place without a new version (refused when the layout
  changed since that revision) and answers `{id, version, layoutRevision}`. A layout's stars don't become sky links and
  don't count toward the 12 stars.
- `list_dashboards` adds each version's `layout` (null for the page alone), `layoutRevision` (0 for the layout it was
  saved with), `layoutNote` and `layoutSavedAt` (null until it is arranged). Its `star` filter also keeps the
  dashboards with a map part focused `near` that star.
- `list_screens`' `showing` adds `parts`, what the screen shows as a grid (`docs/dev/screens.md` 10.8).

A harness call whose arguments are outside the tool's listed `inputSchema` (a missing required argument, an unknown
one, a wrong type or format) is answered `isError: true` with one line naming the argument and what it takes, for
example `since is required: a date and time such as 2026-10-28T12:00:00Z.` The value sent is never repeated. A memory
tool puts that line in its refusal body instead, `{ok: false, code: "invalid", error: <the line>}`.

The screen tools (`docs/dev/screens.md` 3.5 and 8):

- `list_screens` takes nothing and answers `{screens: [{id, name, open, seenAt, showing, home}]}`, `showing` as the
  screen last reported it (`docs/dev/screens.md` sections 5 and 8, `activeAt` included) and `home` its Home as it
  reports it: `{dashboard: {id, title}}` while a dashboard is its Home, with `missing: true` when that dashboard was
  deleted and the screen shows the default Home instead, else `null`, the default Home (the sky and tiles), which a
  screen older than piece 2 has too. A screen is open when the engine heard from it in the last 60 s.
- `control_screen` takes `action` (`open_star`, `open_dashboard`, `home`, `layout`, `move_tile`, `corner_card`,
  `search`, `undo`, `full_screen`, `set_home`, `move`, `resize`, `focus_map`) and exactly the arguments that action
  uses: `star`, `dashboard`, `layout`, `tile`, `to`, `corner`, `query`, `part`, `place`, `with`, `x`, `y`, `size`, `w`,
  `h`, `focus`. `full_screen` takes an optional `dashboard` (without one, the dashboard open on that screen); `set_home`
  takes `dashboard`, or none (or null) to restore the default Home. A dashboard named must exist. `move` takes `part`
  and exactly one of `place` (`left`, `right`, `up`, `down`, `top_left`, `top_right`, `bottom_left`,
  `bottom_right`), `with` (another part) or `x` and `y` (0-11); `resize` takes `part` and one of `size` (`bigger`,
  `smaller`, `wider`, `narrower`, `taller`, `shorter`) or `w` and `h` (1-12); `focus_map` takes `focus` (absent or
  null shows everything) and `part` (needed when there is more than one map). Each of the three takes `dashboard` as a
  guard: an id, or null for the screen's default Home; left out, the engine fills it in from what the screen last
  reported, and the parts named are checked against that dashboard's newest layout or Home's five parts (`map`,
  `main`, `recovery`, `training`, `changed`). Home's map takes no focus. Without `screen` the target is, among the open screens that still listen (any open one when none does),
  the one a person used most recently (`showing.activeAt`, stamped by the app server's clock), then the one seen most
  recently (open question Q-147); with none open the answer says so, and a name two open screens
  share is refused, listing them. The sender is `{kind: "assistant"}`,
  which the screen's note shows as "your assistant" (Q-149). It answers `{screen: {id, name}, action, result, detail,
  ms}`, `result` being `shown`, `failed` or `waiting` (sent, not confirmed within 8 s). A command the screen has not
  picked up within 30 s expires and is never shown later (Q-150).
- Commands change only what a screen shows, except `corner_card`, which sets or clears a corner's dashboard card for
  the household, as `app_dashboards` `{corner, dashboard}` does, and `move`, `resize` and `focus_map` on a dashboard,
  which arrange it for the household (a layout revision, never a new version). `set_home` changes that one screen's
  Home, a setting it keeps like its layout, and so do the three at Home (its `grid`).

### 2.2 App tools (`observatory_*` renamed `app_*`)

| Tool | Old name (alias) | R/W | Gate | Arguments | `structuredContent` |
|---|---|---|---|---|---|
| `app_graph` | `get_relationship_graph` (app role) | R | core | `operation`: `overview` (default), `search`, `neighbors`, `project`, `get`, `content`, `data`; `value` (1–1000 chars); `cursor` (≤ 20 chars); `knownNodeIds`, `knownEdgeIds` (≤ 500 ids of ≤ 200 chars) | a GraphPage: `nodes`, `edges`, completeness, optional removals; `content` adds one star's text (and a document's archived original, 1.5), `data` one data star's recent daily measurements; `project` (`value` a project star's id) that star first, then the stars in it (an `in_project` link to it) and the stars they link to, nothing for a star that is not a project |
| `app_insights` | `observatory_insights` | R | core | `limit` 1–50 (default 20) | `{insights, runs}`: insights newest first with feedback; the newest 14 runs |
| `app_insight_feedback` | `observatory_insight_feedback` | W | core | `id` (uuid), `value`: `interesting` \| `explore` \| `not_useful` \| `later` \| `handled`; `until` (only with `later`, ≤ 90 days); `note` (1–500 chars) | the updated insight |
| `app_dashboards` | `observatory_dashboards` | R + declared writes + arranging | core | exactly one shape: `{}` list; `{id, version?}` one version with page and feeds; `{id, version?, data: true}` its feeds now; `{id, version, layout, base, note}` an arrangement of the newest version; `{id, version?, map, cursor?}` a map part's focused sky; `{id, version, write: {name, change}}` a declared write; `{undo}`; `{id, version, allowEdits}`; `{history: true, id?}`; `{cards: true}`; `{corner, dashboard}`; `{pins: true}`; `{pin, pinned}` | `{dashboards}`, `{dashboard}`, feed data, `{ok, layout, layoutRevision}` or `{ok: false, reason}` (with `layout` and `layoutRevision` when it changed since `base`), `{map}`, `{ok, write}` or `{ok: false, reason}`, `{writes}`, `{cards}`, `{pins}` |
| `app_request_refresh` | `observatory_request_refresh` | W (request only) | core | none | `{accepted, pendingSince, running, nextAllowedAt, lastFinishedAt}`; at most one request every 3 minutes |
| `app_latest_glucose` | `observatory_latest_glucose` | R | a glucose source is enabled | none, or `subject` | the latest glucose reading with provenance and freshness |
| `app_recent_glucose` | `observatory_recent_glucose` | R | a glucose source is enabled | none (24 hours of readings), or `{days: 14}` (compact 14-day series); optional `subject` | readings, coverage and provenance |
| `app_garmin_summary` | `observatory_garmin_summary` | R | `adapters.garmin` | none, or `subject` (the adapter's owner) | recovery and training summary with provenance |
| `app_request_garmin_sync` | `observatory_request_garmin_sync` | W (request only) | `adapters.garmin` | none, or `subject` (the adapter's owner) | `{accepted, pendingSince, nextAllowedAt, lastSyncedAt}` |
| `app_upload_document` | none | W | `adapters.documents` | exactly `name` (1–200 chars, ending `.pdf`, `.txt`, `.md` or `.markdown`) and `content` (the file as base64, at most 5 MiB decoded); while `app.viewers` is on, also an optional `family` (boolean) | `{ok: true, document: {graphId, resourceId, title, format: pdf\|text\|markdown, pages, characters, status: created\|updated\|unchanged}}` (while `app.viewers` is on, `document` also has `scope: private\|shared`), or `{ok: false, code, reason}` with a reason fit to show |
| `app_status` | none | R | core | none | `{version, schema, models}`: the engine's version, the number of its newest migration, and each model with its job (`embeddings` or `titles`), role (`live`, `shadow` or `map`), name, id, state, index (`{passages, indexing, pending, pausedUntil}` for an embeddings model) and failure (`{kind, at}` for a titles model that is `failing`) ([../docs/models.md](../docs/models.md)) |
| `app_sync` | none | R (the list) + W (request only) | core | none (the list), or exactly `adapter` (an adapter's name) | `{adapters}`: each enabled adapter on the engine's clock the caller may ask, with its state; with `adapter`, `{accepted, refused, adapter}`: whether one batch now was accepted, why not (`running`, `pending`, `too-soon`) and that adapter's state |
| `app_memories` | none | R + W (native memories) | `memory.tools` | exactly one `op` and its members: reads `list` `{scope?, tag?, kind?, status?, project?, query?, cursor?, limit?}`, `trash` `{cursor?, limit?}`, `get` `{id, version?}`, `history` `{id, before?, limit?}`, `projects` `{scope?, archived?}`, `review` `{scope?, reason?, cursor?, limit?}`; writes `capture` `{content, scope?, tags?, project?, kind?, importance?, volatility?, key?}`, `append` `{id, text, expected?, key?}`, `update` `{id, content?, tags?, kind?, importance?, volatility?, project?, clear?, expected?, key?}`, `status` `{id, status, expected?, key?}`, `release` `{id, expected?}`, `release_all` `{memories: [{id, expected}]}`, `capsule` `{id, capsule, slot?, expected?}`, `forget` `{id, expected?, key?}`, `restore` `{id, nonce?}`, `undo` `{id, to?, expected?}`, `delete` `{id, nonce, confirm}`; the map's kept dismissals: read `dismissals` `{limit?}`, writes `dismiss` `{link}` and `undismiss` `{dismissal}`; a person's link steps (run 17): reads `connections` `{id, type?, limit?}` and `stale` `{scope?, cursor?, limit?}`, writes `link` `{id, target, type?}`, `unlink` `{id, target, type?}`, `still_true` `{id, expected?}` and `clear_flags` `{id, expected?}`; the duplicate review (run 19): read `duplicates` `{scope?, cursor?, limit?}`, write `keep_copy` `{id, expected?}`; the contradiction review (run 20): read `contradictions` `{scope?, cursor?, limit?}`, `brief` `{scope?, project?}`, `resurface` `{}`, writes `settle_contradiction` `{id, target, decision, expected?, targetExpected?}`, `resurface_dismiss` `{id}`; the full memory UI's list (run 21): reads `tags` `{scope?, tag?, kind?, status?, project?}`, `changes` `{since?, scope?, notHeld?}`, `capsule_status` `{capsule, project?, scope?}`, `settings` `{}`, writes `undo_group` `{group}`, `forget_all` `{memories: [{id, expected}]}`, `restore_all` `{memories: [{id, nonce}]}`, `delete_all` `{memories: [{id, nonce, confirm}]}`, `project_create` `{name, slug?, description?, aliases?, scope?}`, `project_update` `{id, name?, description?, aliases?, archived?, expected?}`, `project_delete` `{id, expected?}`; the brief's lists (run 21): reads `loops` `{direction?, scope?, cursor?, limit?}`, `due` `{window?, scope?, cursor?, limit?}`, `decisions` `{state?, scope?, cursor?, limit?}`, `calibration` `{source?, scope?}`, writes `resolve_loop` `{id, action, expected?}`, `resolve_due` `{id, action, until?, expected?}`, `record_outcome` `{id, result, expected?}`; the memory UI's last pieces (run 22): `tags` takes `query?` too, read `standing` `{scope?, project?, cursor?, limit?}`, writes `stop_standing` `{id, expected?}` and `resolve_all` `{action, items: [{id, expected?}]}`; with `memory.share` on as well, `share` `{id, scope?, expected?}` and `share_all` `{memories: [{id, expected}], scope?}` | `{ok: true, memories, next}`, `{ok: true, trash, next}`, `{ok: true, memory}` or `{ok: true, version}`, a history, `{ok: true, projects}`, `{ok: true, review, next, count}`, a write's `{ok: true, op, id, revision, ...}` (`clear_flags` among them), `{ok: true, op: "release_all", results}`, `{ok: true, dismissals, truncated}`, `{ok: true, op: "dismiss", link, dismissal, removed, unchanged, scopes}`, `{ok: true, op: "undismiss", dismissal, unchanged, back, scopes}`, `{ok: true, id, connections, truncated}`, `{ok: true, stale, next, count}`, `{ok: true, duplicates, next, count}`, `{ok: true, op: "link", link, id, target, ...}`, `{ok: true, op: "unlink", removed, kept, dismissed, ...}`, `{ok: true, op: "still_true", id, revision, checkedAt, was, retracted, ...}`, `{ok: true, tags, truncated, count}`, `{ok: true, groups, truncated, since}`, a capsule's status, the settings, `{ok: true, op: "undo_group", group, total, remaining, done, capped, results, redo}`, `{ok: true, op, results}` for several at once, `{ok: true, op, project, scopes, changed, unchanged}`, `{ok: true, op: "project_delete", id, slug, owner, memories, scopes}`, `{ok: true, standing, next, count, firing, held, overLimit, maxInScope, maxFires, served}`, `{ok: true, op: "resolve_all", action, results}`, or `{ok: false, code, reason}` |
| `app_viewer` | none | W (viewer sessions) | `app.viewers` (refused by this version) | exactly one `op` and its members: `enrol` `{code, credential}` (`code` 1–40 chars as the person typed it; `credential` base64url, 43–512 chars), only with the app token; `whoami` `{}`; `end` `{}`, only with a session's credential | `enrol` and `whoami`: `{person}`, a person id, or null (`enrol`: the code was not accepted; `whoami`: the app token's household view); `end`: `{ended}` |
| `app_screens` | none | W (what screens show) | core | exactly one `op` and its members: `listen` `{screen, name, hold?, showing?, settings?}` (`hold` 0–25 s, default 25; `settings` `{layout?, tiles?, home?, grid?}`); `done` `{screen, command, result: shown\|failed, detail?, showing?}`; `send` `{screen, to, action, args}`, `args` the action's arguments (they may instead sit beside the others, except `move_tile`'s, whose `to` would be taken for the target); `list` `{screen}`; `rename` `{screen, name}` | `listen`: `{command, screen: {id, name, settings}}`, `command` null after an empty wait; `done`: `{ok: true}`; `send`: `{command}`; `list`: `{screens: [{id, name, open, seenAt, showing, self}]}`; `rename`: `{screen}` |
| `app_studio` | none | R + W (a Studio draft on its screen) | core | exactly one `op`, with `screen` and that op's members: reads `active` `{screen}`, `read` `{screen, sessionId}`, `data` `{screen, sessionId, revision}` (the draft's feeds, read as the caller), `controls` `{screen, dashboardId, version}`; the target screen's steps `validate` `{screen, sessionId, requestId, revision, payload: {ok, message?}}`, `feedback` `{screen, sessionId, requestId, revision, payload: {text}}` (1–1000 characters), and `cancel`, `discard` and `save` `{screen, sessionId, requestId, revision, payload?}` (`payload` empty). Only the login the screen is bound to reaches a session; a `save` publishes as the AI client that began it ([Studio authoring](#studio-authoring-and-typed-dashboard-selections)) | `{value}`: the session, the draft's data, a version's controls or the saved dashboard; `{value: null}` from `active` with no draft on the screen; `{error}` for arguments it refuses, or `{error, code}`, `code` `forbidden` or `conflict` |

- `app_graph` also takes `operation` `recall` while `engine.recall` is on (SB-P2-10), and lists it in its input schema
  only then; off, as in every `rhythm.yaml` written before it, it is refused as outside the schema and every other
  operation answers as before. `value` is a question of at most 800 characters, with no `cursor` or known ids. It
  answers a GraphPage of the stars that stand on this reader's map for what combined retrieval (the `recall` tool's
  list, 2.1) finds, best first, with `recall` `{query, results, notices, semanticSkipped, offMap, receipt}`: each result
  a star's `id` (one of `nodes`, in the same order), the fused `score`, an `excerpt` of at most 300 characters,
  `truncated`, and `why` `{rank, words: {rank, terms, exact} | null, meaning: {rank, similarity} | null, imputed}`.
  A record no star the reader can open stands for (a native memory written since the last pass) is only counted in
  `offMap`, never named; two records one star stands for are one result. A document's page, which is no star of its
  own, stands for its document's star (SB-P4-13). The receipt hashes every record found, as the
  `recall` tool's does, and equals the `recall` tool's for the same question with `topK` 20 (the explorer asks for 20)
  (`mcp/app_graph.output.schema.json` `$defs.recall`; fixture `app_graph.recall.example.json`).
- Replaced facts on the map (SB-P4-11, `../docs/dev/memory.md` section 5): while the engine's `memory.tools` is on, a
  native memory that a newer memory of its scope replaced (migration 049's state `replaced`) stays a star, judged at
  each read as every native star is, and carries `truth` `{state: "replaced", replacedBy}`, `replacedBy` the replacing
  memory's star id; the app draws it faded, never hidden, and never as the current fact. Every other star, and every star
  with `memory.tools` off, has no `truth`, and is served exactly as before: an ended memory (replaced by nothing live), a
  retracted one and a held one are still no one's star. The same holds in every map read that names stars (`app_graph`,
  a dashboard map part's sky, the harness map tools; `explore_graph` and `graph_changes` name `truth` on their stars).
  Its text opens as any memory's. Fixture `app_graph.replaced.example.json`.
- A plugin's own links on the map (SB-P4-13): while the engine's `graph.pluginLinks` is on, a link a plugin gave
  between two of its records (`ingest_records`' `links`, section 4) joins their document stars as an edge like any
  other, its `type` the plugin's word, `verification` as the plugin stated it (`suggested`), `directed`, and one
  `evidence` item whose `locator` is `ingest:` and the plugin's name; served to a reader of both stars. Off, as in every
  `rhythm.yaml` written before it, no such edge is served, and every answer is as before.
- The adapter tools' shapes are pinned too: `mcp/app_garmin_summary.output.schema.json`,
  `mcp/app_request_garmin_sync.output.schema.json` and `mcp/app_upload_document.output.schema.json`.
- `app_upload_document` archives the file, reads its text and saves it under provider `documents`
  (the same as `save_document`); the star is the catalog's (`graphId`) and shows after the map rebuild
  the engine queues. The same file again answers `unchanged`. A refused file (`unsupported_type`,
  `too_large`, `empty`, `not_text`, `not_pdf`, `pdf_unreadable`, `too_many_pages`, `invalid_name`) is
  `ok: false`, not an error.
- Where an upload lands (`../docs/dev/memory.md` section 5): where `adapters.documents.owner` puts it, as
  always, unless `family` is true, which puts it in Family (the household's). With `memory.scopes` on, a
  viewer session's upload without `family` is its person's own instead, and a `family` upload from a
  login that does not read Family (a session whose person is not a member) is refused (403). `family` is
  listed and taken, and `document.scope` answered, only while the engine's `app.viewers` is on: who reads
  the document, `private` (one person's readers: a session's own person, or the person the `owner`
  setting names for the household view) or `shared` (everyone in the household: Family's, and every
  upload while `memory.scopes` is off). With it off the tool takes exactly `name` and `content` and
  answers as before. The facade passes
  `family` (`POST /api/documents` `{name, content, family}`) only from inside a viewer session; any
  other body with it is a 400.
- With `app.viewers` on and `memory.scopes` on, the corners a viewer session sets (`{corner, dashboard}`)
  are its person's own: a Family dashboard or one of theirs, or `null` for the stat card, kept as their
  choice. `{cards: true}` and a dashboard's `corners` show each reader its own person's choice where it
  made one, else the household's (migration 030). Any other login sets the household's, as always.
  Since migration 032 a session's `{corner, dashboard}` with the nil uuid
  (`00000000-0000-0000-0000-000000000000`) drops its person's own choice there, so the corner shows the
  household's again, and its `cards` carry `own`, the corners that are its person's own choice (the stat card
  included); a screen's Undo of `corner_card` uses both (`docs/dev/screens.md` 6). From any other login, and
  with `memory.scopes` off, the nil uuid is an unknown dashboard and `cards` carry no `own`, as before.
- `corner` is one of `main`, `recovery`, `training`, `changed`. `main` is the ambient screen's tall left slot
  (it was `glucose`; migration 018 renames a stored choice): it shows its dashboard as a tall card, else the glucose
  tile once a glucose source has data, else a short hint. `recovery` and `training` show the Garmin tiles while
  Garmin is connected, `changed` what changed lately; without a dashboard or their source they stay empty. Declared
  dashboard writes keep the private engine's rules: only writes the dashboard declared, a revision check, history and
  Undo.
- A dashboard's page learns where it draws from the app's frame, never from the engine: `rhythm.onData` receives
  `mode` (`full` or `card`) and, in card mode, `shape` (`tall` in the main slot, `compact` in the other corners), and
  the frame sets `<html data-mode="card" data-shape="tall|compact">`. A page that ignores `shape` still works: its
  compact card sits centred in the tall box.
- A "glucose source" is any enabled MeasurementSource or ingest plugin that writes metric `glucose`
  (CSV import counts once it holds glucose rows). `subject` defaults to the first person in
  `people` that has glucose data, then to the first person.
- Each glucose provider is an **archive** or a **live feed** by its provider policy (section 3.2),
  never by its name. `app_latest_glucose` and the 24-hour form read live feeds only. The 14-day form
  is the person's archive up to the first live reading in the window, then their live feeds, never
  interleaved: at most an `archive` stretch (`timeBasis: device-local`) followed by a `live` one
  (`source-zone`), each naming its provider. Its 14-day totals are the archive's. Everything is in
  mg/dL. The shapes are `mcp/app_latest_glucose.output.schema.json` and
  `mcp/app_recent_glucose.output.schema.json`.
- Composable layouts (`docs/dev/screens.md` 10.7). Every summary and version, and the feed read's `dashboard`, carry
  `layout` (null for a version saved without one: its page alone), `layoutRevision` (0 for the layout it was saved
  with), `layoutNote` and `layoutSavedAt` (null until it is arranged). `{id, version, layout, base, note}` saves a
  screen's arrangement of the newest version as its next layout revision (`base` is the `layoutRevision` the change
  started from; `note`, 1-500 characters, says the change in plain words) and answers `{ok: true, layout,
  layoutRevision}`. Only an arrangement is saved: the same part ids, each with its `kind` and a card's `dashboard`;
  only `x`, `y`, `w`, `h` and a map's `focus` may change, and a changed focus star must be on the map now. Anything
  else, a version that isn't the newest or a layout that breaks the rules answers `{ok: false, reason}`, the reason fit
  to show; a layout changed since `base` answers `{ok: false, reason, layout, layoutRevision}` with the layout as it
  stands, for the screen to apply its change to once more. The facade never retries a layout save. `{id, version?, map,
  cursor?}` answers `{map: {part, focus, matched, nodes, edges, nextCursor, completeness, issues}}`: the stars of the
  whole readable map that meet that map part's focus, only from providers the map shows and never over a disputed link,
  ranked and paged (`o:N`) as `app_graph`'s overview, with `nodes`, `edges` and `issues` as `app_graph` gives them and
  `matched` the count of every match; 404 when the version has no such part, 400 when it isn't a map with a focus.
  It only reads, so it may be tried again. The listed input schema names `layout` and `map` among the arguments, which
  is how the facade learns the engine has piece 3. Shapes: `mcp/app_dashboards.output.schema.json`.
- `app_screens` is the screens' side of live screens, pinned by `docs/dev/screens.md` (3.4) and
  `mcp/app_screens.output.schema.json`. `screen` is always the calling screen's own id (16-64 characters of
  `A-Za-z0-9_-`), because every screen shares the app principal. `listen` records the screen as open, expires its
  overdue commands and claims its oldest pending one, else waits up to `hold` seconds for one. The `settings` a
  screen keeps are checked: `layout` one of `corners`, `hero`, `band`; `tiles` each tile in exactly one place; `home`
  `null` (the default Home) or `{dashboard}` (an id; one deleted since is kept, and the screen shows the default Home);
  `grid` (piece 3, never null) `{parts: [{id, x, y, w, h}]}` with Home's five parts (`map`, `main`, `recovery`,
  `training`, `changed`) each once, inside the 12 × 12 grid, never overlapping, the map at least 4 × 4 and each corner
  2 × 3. The listed input schema names `grid` among `settings`' properties. `send` takes `move`, `resize` and
  `focus_map` with their arguments beside the others or as `args` (`with` never collides with the target `to`), and
  `listen` and `done` carry `showing.parts`.
  A screen's first `listen` with a name another screen seen in the last 30 days has (in any case) is stored with the
  lowest free number after it (`Windows PC · Chrome 2`), and the answer carries the name as stored; a screen the engine
  knows keeps the name it sends, so a name a person chose is never changed, except that a listen never replaces a
  stored numbered name with its bare one (`Windows PC · Chrome 2` stays when a listen sends `Windows PC · Chrome`);
  `rename` sets any name. `send` checks the command as `control_screen` does, with sender
  `{kind: "screen", screen, name}`, and waits up to 8 s for its outcome. A `Command` is `{id, action, args, sender, sentAt, deliveredAt, doneAt, result, detail, ms}`, null fields
  included, `result` one of `pending`, `delivered`, `shown`, `failed`, `expired`, and `ms` `{deliver, show}` in
  milliseconds from `sentAt`. At most 20 pending commands per screen and 100 screens (a new one replaces the one
  unseen longest); the target of a `send` must be open. The facade serves it to the browser as `POST /api/screens`
  (`{op, ...}` with the same members); in demo mode that answers `{available: false}` and the browser's tabs command
  each other without the engine. An engine whose `tools/list` lacks `app_screens` (one older than the app) gets the
  same answer from the facade, and screens stay off: a core tool the facade checks against the listing (1.4), as it
  does `app_status`.
- `app_status` gives the app what `GET /health` gives `rhythm doctor` (`docs/dev/engine.md`): states and counts only,
  never a title, a text or a record's id. An embeddings model's `index` is read at most once a minute beside the
  request, so an answer never waits for it, and is null until it is first read, and always while memory scopes are on:
  its counts are every scope's together (`docs/dev/memory.md` 6.2). A titles model is `failing`, with `failure`
  `{kind, at}`, from the newest rebuild that stopped on a failure until one asks it for titles without one, the engine
  restarted in between included;
  `kind` is `timeout`, `unreachable`, `http-<status>`, `too-large` or `unreadable`. The facade serves it to the browser
  as `GET /api/status`: `{mode: "live", available: true, version, schema, models}`; `{mode: "demo", available: false}`
  in demo mode; `{mode: "live", available: false}` when the engine's `tools/list` lacks `app_status` (one older than
  the app) or the call fails. The shape is `mcp/app_status.output.schema.json`.
- `app_sync` is Sync now from the Sources view (SB-P6-09). It knows the adapters that sync or import on the engine's
  own clock (every enabled MeasurementSource and ImportSource; a graph source such as notes, Drive or Second Brain is
  read by the map's rebuild, which `app_request_refresh` asks for). Each adapter in `{adapters}` (by name) is
  `{adapter, kind, interval, limit, running, pendingSince, nextAllowedAt, lastAskedAt, lastFinishedAt, outcome}`:
  `kind` `measurements` or `imports`; `interval` how often it runs on its own and `limit` how often it may be asked, in
  seconds (its own limit when it states one, Garmin's 15 minutes, else its interval and never under a minute);
  `running` whether a batch of it runs now, its own or one asked for; `pendingSince` since when one asked for waits;
  `nextAllowedAt` when the next may be asked for (null while one waits or runs, and before any was asked; it may have
  passed); `lastAskedAt`, `lastFinishedAt` and `outcome` (`complete`, `failed` or `timed-out`) of the last one asked
  for. States and times only: never a row, a count or an error's words. `{adapter}` asks for one batch now: one call of
  the adapter's own sync or import (Garmin first asks for its own two-day job, as `app_request_garmin_sync` does, and
  ends `failed` without a tick when Garmin's own 15 minutes refuse it), run by
  the engine's scheduler between its own jobs within seconds and cut off after five minutes. It is refused while a
  batch of that adapter runs (`running`), while one asked for waits (`pending`) and inside its `limit` since the last
  one asked for (`too-soon`). An adapter that is not on the clock, or (with `memory.scopes` on) whose owner's rows the
  caller does not read or that states no owner, answers 404 like an unknown one, and the list leaves it out. The state lives in the engine
  only: a restart forgets it, and the scheduler runs every adapter at its start anyway. The facade serves the list as
  `GET /api/sync` (`{available: true, adapters}`) and a request as `POST /api/sync {adapter}` (`{available: true,
  accepted, refused, adapter}`), never sent twice; both answer `{available: false}` in demo mode, when the engine's
  `tools/list` lacks `app_sync` (one older than the app), and when the call fails or is refused. The shape is
  `mcp/app_sync.output.schema.json`.
- `app_memories` is the app's Memories view (SB-P3-12; `../docs/dev/memory.md` sections 5 to 7.2): native memories
  read and written through the memory store's checked functions, as the MCP memory tools are (2.1). It is listed and
  taken only while the engine's `memory.tools` is on; off, absent or empty, it is answered as a tool this role does not
  have (403 `forbidden`, 2.4) and every other answer is as before. Every op is the app's, channel `app`, fixed by the
  engine and never named by the call (an argument `channel` or `client` is outside the schema, 400): a person's
  channel, so it may `release` a hold (D-039), `undo` its way to a release, and `delete` a memory from the trash for
  good with the trash row's `nonce` and `confirm` equal to the memory's id, which the app asks its person for (D-036);
  an AI client can do none of these. Since SB-P3-22 it also takes a person's steps beyond one memory at a time, which
  no AI client's token reaches (the door sends a harness token to the MCP tools, none of which takes them): `review`
  lists the held memories its caller reads, newest held first, each with its `reason`, `score`, `signals`, when
  (`heldAt`), for whom (`heldBy`) and how (`channel`, `client`) the write that held it came, its `revision` and whether
  this caller may release it (`mayRelease`, the author lock), never its words, tags or project, with `count` the whole
  queue's length by the same filters (a `scope` and a `reason`: `instruction`, `hidden`, `too_long`, `burst` or
  `held_project`); `release_all` releases 1 to 50 memories, each at the revision its person read (`expected`), and
  answers each on its own in `results` (`released`, `unchanged` when it was not held, `unknown`, `forbidden`, or
  `conflict` when it changed since, which stays held, with the `revision` it is at); `capsule` puts a memory into a
  capsule slot (`capsule: core` with `slot` `identity`, `preferences`, `constraints` or `principles`; `capsule: project`
  with `current-state`, `decisions` or `open-questions`, its project's capsule) or takes it out (`capsule: none`, no
  `slot`), as a change with its version and audit row and no `key` (setting the slot it has answers `unchanged`); its
  answer is a write's with the slot it is in now (`capsule` `{kind, slot}` or null). A capsule serves a slotted memory
  only while it is canonical and not held. A memory's `capsule` is its slot in `get` and in a list, null in a list for a
  held one. Since run 14, with `memory.share` on beside `memory.tools` (each off by default), it also takes `share`, a
  person's move of a memory between its owner's private scope and Family through the store's share (2.1's `share`,
  migration 048), as the app: `scope: shared`, the default, shares one of its owner's private memories into Family (its
  owner's step, a member of Family); `scope: private` takes a Family memory back to its author's private scope, its
  author's or a Family admin's step; `expected` is the revision read, and no `key` (a repeat answers `unchanged`). Its
  answer is a write's with `projectLeft` (its project could not come with it); with memory scopes off it is refused
  (`scopes_off`) before anything is read, a held memory waits for its release (`held`), and one no current person wrote
  stays in Family (`no_author`). `tools/list` names the op only while memory scopes are on too, and only to an app
  login that speaks for a person (a viewer session's), so a view is never offered a step every call of which is
  refused; with `memory.share` off the op is outside the schema (400) and the listing and every answer are as before.
  The household view reads no private memory and changes a Family memory a person wrote only as the author lock
  allows, so it shares nothing and takes nothing back (`unknown`, `forbidden`), and its listing (the app token's, the
  one the facade reads) never names the op: the app's login that shares is a viewer session's, bound to its person,
  while `app.viewers` is on. So in this version, with `app.viewers` refused (D-046), the Memories view offers no share.
  Its reads are the household view's with the app token (with `memory.scopes` on:
  Family's memories only; a `capture` names `scope: shared`, since the household view has no private scope; a Family
  memory a person wrote changes only as its author or an admin allows, so the household view gets `forbidden`), or a
  viewer session's while `app.viewers` is on. `get` serves a held memory whole, its words with its `held`
  `{reason, signals}`, for a person to review before releasing it; `list` and `trash` serve it with no words, tags or
  project, and `history` and `get` with `version` leave a held state's words out (034). A list's `content` is a preview
  of at most 300 characters (`truncated` when longer); `cursor` continues from `next`. `expected` is the revision the
  caller read: a write when the memory has moved on answers `conflict` with the current `revision`; `release_all`
  names one for each memory. `key` (8 to 200 of
  `A-Za-z0-9._:-`) makes a retry of `capture`, `append`, `update`, `status` or `forget` answer as the first did
  (`replayed`). A refusal is `{ok: false, code, reason}` with status 200, the reason the engine's own words, fit to
  show: `unknown` (no such memory the caller reads; one in the trash, another scope's and one never made answer alike),
  `forbidden` (the author lock, or a capture with no scope from the household view), `invalid`, `too_large`,
  `conflict`, `key_reused`, `unknown_project` (`known`: up to 10 slugs the caller reads), `taken`, and an undo's or a
  version read's `nothing_to_undo`, `no_version`, `not_visible`, `pruned` (`revision`: the oldest still kept) and
  `moved`, and a share's `scopes_off`, `held` and `no_author`. A call may carry a memory's whole text, so its body may be up to 288 KiB, while every other call keeps 32
  KiB. The facade serves it to the browser as `GET /api/memories` (`{available}`: whether the engine lists the tool;
  `{available: true, share: true}` while its listing names the `share` op)
  and `POST /api/memories` (`{op, ...}` with the same members): `{available: false}` in demo mode and when the engine's
  `tools/list` lacks `app_memories`, else `{available: true, ...}` with the answer, or `{available: true, ok: false,
  code: "unavailable", reason}` when the call failed; a write is never sent twice. The shape is
  `mcp/app_memories.output.schema.json`.
- `app_memories` and a memory's truth (SB-P4-11, the app half of D-192; `../docs/dev/memory.md` section 5): `get` and a
  list's memories carry `state` (`current`, `replaced`, `ended` or `retracted`, migration 049's one definition),
  `replacedBy` (the memory that replaced it, only while `replaced`, else null) and `retractedSources` (the retracted
  memories of its own scope it was built on, which flagged it for a check, oldest first; empty when none), as the MCP
  `get` and `list_recent` serve `state`, `replaced_by` and `retracted_sources`. A held memory in a list carries them too:
  they hold no words. A write, a `forget` and a `restore` whose retraction cascade did something carry `cascade`
  `{reopened, reclosed, flagged, unflagged}` (memory ids of its own scope, at most 25 each), what the MCP tools' message
  and the terminal say in words; one whose cascade did nothing carries no `cascade`, as before. A person takes a
  memory's flags off with `clear_flags` (below), as an AI client does with `update`'s `clear`. Fixtures
  `app_memories.replaced.example.json` and `app_memories.cascade.example.json`.
- `app_memories` and the map's kept dismissals (SB-P4-05, migration 057; `../docs/dev/memory.md` sections 5 and 8):
  Discover's Dismiss, saved and honoured on every read of the map and every rebuild, and a person's view of the
  dismissals with a way to take one back, each a person's step through the app's channel (057 refuses an AI client's).
  `dismiss` names a suggestion by its id on the caller's map (`link`: an `app_graph` edge's id, `relationship:` and 64
  hex digits, or `made-link:` and a uuid), which the engine finds in that caller's own view, so nobody dismisses a link
  they are not served: a suggestion of the map's (active, not verified: a similarity, a copy, a data star's topic link,
  a source's own automatic link, a plugin's) is kept as a dismissal, between two native memories' stars by the two
  memories (where 2.1's `unlink` keeps one), between any other two stars by the star rows; a link a person or an AI
  client made is unlinked instead, of its one type as 2.1's `unlink` does: removed, and kept as a dismissal only when it
  was marked suggested (`removed: true`, `dismissal` null for one stated). Who may: whoever may change either end (the
  owner; in Family a memory's author or an admin, any Family reader for a record no person wrote; any reader for DEMO;
  with scopes off any admitted login). A confirmed link is no suggestion (`invalid`); one no longer on the caller's map
  is `unknown`. `dismissals` (`limit` 1 to 50, default 50) lists the dismissals between two stars on the caller's map,
  newest first, `truncated` when there were more: each with its `id` (`memories:` or `stars:`, the two ids smaller
  first, and the link `type`), `kind`, `type`, the two `stars` `{id, title}` as the caller's map names them,
  `dismissedAt`, `by` (`person`: the app's login or the terminal's; `ai_client`: an AI client's `unlink`), `mine` (this
  login or its person made it) and `mayRestore` (the same rule as dismissing). `undismiss` takes one back by its `id`;
  `back` says whether the map finds a suggestion of its type between the two stars now, which is then on the map again
  from the next read, since the rebuild never stopped finding it; it is false for a dismissal kept when a person
  removed a suggested made link, which brings nothing back (the link was removed: taking the dismissal back only lets
  one of its type be suggested again). `unchanged` when there was none. An AI client's `link` (2.1) of a dismissal's type between two memories no longer takes it back; a
  person's still does. With `memory.tools` off none of this is listed or taken and no dismissal is written here, so
  every map answer is as before. Fixtures `app_memories.dismissals.example.json`, `app_memories.dismiss.example.json`
  and `app_memories.undismiss.example.json`.
- `app_memories` and a person's own link steps (run 17; API-21; SB-P4-02, SB-P4-08 and SB-P4-09's person halves;
  `../docs/dev/memory.md` section 5): the steps 2.1's `link`, `unlink`, `connections`, `list_stale`, `still_true` and
  `update`'s `clear` `retracted_sources` take for an AI client, through the app's channel, a person's, and the same
  checked functions (052, 053, 049), so the same rules hold: the author lock, one scope, no held memory, the episodic
  rule, never a `supersedes` link. `connections` (`id`, `type`, `limit` 1-50, default 20) answers `{id, connections,
  truncated}`: the made links first, newest first, then the map's suggestions between this memory's star and another
  memory's, strongest first, each with `linkId` (null for a suggestion) and `made`, `type`, `direction` from this
  memory (`outgoing`, `incoming`, `none` for a type with no direction), `verification` (`verified` a person's link,
  `inferred` an AI client's, `suggested` an automatic one), `mine` (this login or its person made it), `imported`
  (since run 17's fix review: an import brought it, so it has no maker here and its verification is as its file gave
  it, or `verified` once a person here confirmed it; it stays imported), `mayUnlink`
  (unlink would remove it, or dismiss a suggestion, for this caller), `at`, `confidence` and the other `memory` (its
  scope, owner, status, state, kind and a preview of at most 120 characters, none for one held for review, with its
  hold); an unknown memory, or one the caller does not read, is `unknown`. `link` (`id`, `target`, `type`, by default
  `relates_to`; no confidence and no idempotency key) makes a person's link, `verified`, and answers `{op: "link",
  link, id, target, from, to, type, directed, verification, created, unchanged, scope, owner, scopes}`: one there
  already as it is is `unchanged`; one an AI client made of that type is confirmed (neither `created` nor `unchanged`,
  the same `link`); and a person's link made or made anew takes back a dismissal of its type between the two (057).
  `unlink` (`id`, `target`, `type`: one type, else every type but `supersedes`) answers `{op: "unlink", id, target,
  removed, kept, dismissed, already, unchanged, scope, owner, scopes}` as 2.1's `unlink` does. `stale` (`scope`,
  `cursor`, `limit` 1-50, default 20) answers `{stale, next, count}`: the memories the caller may change in 053's one
  queue, longest unchecked first, each with `reason`, `since`, `foundAt`, `confirmedAt`, a 300-character `preview`,
  `retractedSources` and the `revision` a still true names; `count` is the length of the list the pages walk.
  `still_true` (`id`, `expected`) answers `{op: "still_true", id, revision, scope, owner, status, scopes, unchanged,
  checkedAt, was, retracted}`, refused for a memory held for review (`held`), marked wrong (`retracted`) or whose time
  has ended (`invalid`). `clear_flags` (`id`, `expected`) takes a memory's flags for a check off once its person has
  checked it, a write's answer (`changed: ["retracted_sources"]`, or `unchanged` when there were none). A link writes no
  history row; still true writes `confirmed` and a flag clear `flags_cleared`, each through the app's channel. None
  takes a channel or a client, and with `memory.tools` off none is listed or taken (the app's server answers each as the
  400 it was before them). Fixtures `app_memories.connections.example.json`, `app_memories.stale.example.json`,
  `app_memories.link.example.json`, `app_memories.unlink.example.json`, `app_memories.still_true.example.json` and
  `app_memories.clear_flags.example.json`.
- The duplicate review (run 19, SB-P5-02, migration 063), with `memory.tools` on: `capture` checks a new memory's words
  against its scope's as the MCP `remember` does, and a near copy is saved and flagged, its answer adding `duplicates`
  (at most 3 `{id, score}`, closest first; never for a held memory; fixture `app_memories.capture.duplicate.example.json`);
  `get` and `list` add `duplicateOf` to a memory with a flag that stands (never a held one's). `duplicates` (`scope`,
  `cursor`, `limit`) reads the review the caller may act on, newest first, `{duplicates, next, count}`, each item its
  `id`, `scope`, `owner`, `author`, `status`, `createdAt`, a 300-character `preview`, `truncated`, `duplicateOf` and the
  `revision` a keep names; `count` is the length of the list (fixture `app_memories.duplicates.example.json`).
  `keep_copy` (`id`, `expected`) takes the memory's duplicate flags off once its person keeps both, a write's answer
  (`changed: ["duplicate_of"]`, or `unchanged`; fixture `app_memories.keep_copy.example.json`), recorded `flags_cleared`.
  Neither takes a channel, a client or a key. The app's facade (`app/src/memories.ts`) passes `duplicates` since run 21, and keeps
  both through `resolve` (below); it still drops the new fields of a capture's answer.
- The brief (run 20, SB-P5-08, migration 069), with `memory.tools` on: op `brief` (`scope`, `project`) reads the same
  brief as the MCP tool, item for item (fixtures `app_memories.brief.example.json`, `app_memories.brief.quiet.example.json`):
  `{project, unknownProject, zone, says, due, decisions, youOwe, owedToYou, stale, insights, contradictions, duplicates, held, standing,
  calibration, changes}`, `held` a number with no words (op `review`'s queue, by its own predicates), each other section `{count, items}` (`due` and `decisions` also `overdue` and `upcoming`), `count` the whole length of
  the list its MCP tool walks; an item its `id`, `revision`, `scope`, `owner`, a 120-character `preview` and `truncated`, with `window`, `at`
  and a due date's `local`, `counterparty`, a stale one's `reason` or a contradiction's `conflict`; an insight its `id`, `scope`, `owner` and
  `title`; `calibration` `{ready, n, line}`; `changes` `{count, changes, truncated, groups}`. The facade passes it on only while the
  engine's listing names the tool; otherwise it is the 400 it was. The app no longer reads it: Home draws no brief (D-257).
- The resurfaced memory for the Home card (run 20, SB-P5-17, migration 070), with `memory.tools` on: op `resurface` takes nothing and answers
  `{day, pick}` (fixtures `app_memories.resurface.example.json`, `app_memories.resurface.none.example.json`): `day` the install's day, `YYYY-MM-DD`;
  `pick` the same memory all that day for this reader, or `null` when none is eligible: `{id, revision, scope, owner, preview, truncated, createdAt}`,
  `preview` at most 280 characters. It is a read of what the caller reads, older than 60 days at the day's first moment in the install's zone, and
  never held, trashed, marked wrong, the engine's own, what a Second Brain job wrote (after an import), a mirror, a capture no person has confirmed, or dismissed. Op `resurface_dismiss` `{id}`
  answers `{op, id, unchanged}` (fixture `app_memories.resurface_dismiss.example.json`): this reader has retired the memory from the card for good,
  `unchanged` when it already had; a dismissal by the household view (the shared app token), or with scopes off by anyone, holds for every reader. A
  memory the caller reads may be dismissed in the trash or held too, and a restore or a release does not bring it back; `unknown` for one the caller
  does not read or one deleted for good, and nothing is kept. It changes no memory: no revision, version or audit row. No AI client is offered
  either. The facade passes them on only while the engine's listing names the tool; otherwise each is the 400 it was. The app's Home card reads and
  dismisses (`app/src/ResurfaceCard.tsx`) and with the setting off draws nothing.
- The contradiction review (run 20, SB-P5-03, migration 068), with `memory.tools` on: every write that saves words (`capture`,
  `append`, `update`, `undo`, `release`, `release_all`) is checked as the MCP tools' are, and its answer adds `duplicates` and
  `contradictionCandidates` (`{id, score, proposed}`, closest first; never for a held memory); `get` and `list` add
  `contradictions` while they stand. `contradictions` (`scope`, `cursor`, `limit`) reads the review the caller may act on, newest
  first, `{contradictions, next, count}`, each pair's `scope`, `owner`, `score`, `offeredAt`, `memory` and `conflict` (`id`,
  `revision`, `author`, `status`, a 300-character `preview`, `truncated`, and `startedAt`, when it began being true, null for an unknown start, which counts as before every other: `memory` is only the
  one whose words were checked, so this says which is the later), `proposed` and `maySettle`; `count` is the length of the
  list (fixture `app_memories.contradictions.example.json`). `settle_contradiction` (`id`, `target`, `decision`, `expected`,
  `targetExpected`) is a person's decision: `keep_both`, or `replace`, where `id` replaces `target` as a supersede does, with its
  refusals; a write's answer with `target` and `decision` (`keep_both` or `replaced`), or `unchanged` when it was settled already
  (fixture `app_memories.settle_contradiction.example.json`), recorded `contradiction_settled`. A refusal adds `not_offered` for a
  pair not queued, and `protected` too when `replace` would change nothing because an end still to come rules it out, or when `id` did
  not begin after `target`, since only the later replaces the earlier: nothing is settled, and the person can keep both or, for the
  earlier to stand, mark the later wrong or forget it. `keep_copy` through this channel settles the pairs its review showed, never a pair named a
  contradiction. Neither takes a channel, a client or a key. The app's facade passes `contradictions` since run 21, and settles
  through `resolve` (below).
- A person's review queues (run 21, SB-P6-01; `engine/src/Core/Mcp/AppMemoriesQueues.cs`, `engine/src/Core/MemoryQueues.cs`), with
  `memory.tools` on, no new table or function, and no MCP tool for any of it: the app's channel, a person's.
  - `queues` (`scope`): `{held, duplicates, contradictions, stale, candidates, insights}`, how many wait in each, each read through the
    read that serves its list, so each is the length of its list (fixture `app_memories.queues.example.json`).
  - `insight_candidates` (`scope`, `state`: `waiting`, the default, `used` or `rejected`, `cursor`, `limit`): `{candidates, next, count}`, the
    pairs the nightly pass found, best first, each `{id, scope, owner, score, signals, similarity, gapDays, foundAt, older, newer, status,
    decidedAt, note, insight}`, `older` and `newer` each `{id, revision, createdAt, importance, preview, truncated}` with at most 300
    characters of its words; `insight` only when this reader reads that insight's scope (fixtures
    `app_memories.insight_candidates.example.json`, `app_memories.insight_candidates.rejected.example.json`).
  - `insight_stream` (`view`: `proposed`, the default, `older`, `confirmed`, `snoozed`, `closed` or `all`, `scope`, `cursor`, `limit`):
    `{view, count, proposed, older, confirmed, snoozed, closed, candidates, day, insights, next}`, as the MCP `list_insight_stream` reads
    it, in the app's words. Each insight is `{id, view, review, reviewedAt, snoozeUntil, snoozeEnded, shownAt, scope, owner, candidates,
    mayReview, insight}`, `insight` the insight as every reader receives it and `mayReview` whether this login may review it (066's
    `insight_may_review`); `day` is `{date, zone, outcome, note, insight, declined, says}` (fixtures `app_memories.insight_stream.example.json`,
    `app_memories.insight_stream.quiet.example.json`).
  - `insight_history` (`id`, `limit` to 100): `{id, review, count, events}`, newest first, each `{event, value, until, note, person, channel,
    client, at}` (fixture `app_memories.insight_history.example.json`).
  - `review_insight` (`id`, `action`: `confirm`, `dismiss` or `reopen`, `note`): `{op, id, review, view, reviewedAt, unchanged}`, 066's
    `review_insight` as the app; `reopen`, no resolve, is how a review is taken back (fixture `app_memories.review_insight.example.json`).
  - `resolve` (`id`, `action`, `target`, `expected`, `targetExpected`, `note`, `insight`, `copies`): what a person does in a review queue, through
    `MemoryStore.ResolveItem` as the app, so an item leaves its queue once whatever the word: `release`, `keep_copy` (since 073 with `copies`,
    1 to 3 `{id, expected}`, each memory it is close to at the revision read: one the review shows that changed since or is not named is
    refused `conflict` and nothing changes; without `copies` as before), `settle_keep_both` and
    `settle_replace` (`id` stands and began later, `target` the other memory; `targetExpected` the revision read of it), `still_true`, `use_candidate` and
    `reject_candidate` (`note`, `insight`), `confirm_insight` and `dismiss_insight` (`note`). Loops, decisions, dates and standing instructions
    are not taken here. `{op, queue, action, id, unchanged, waiting, stillCounted, revision, scope, owner, status, scopes, changed, held, target,
    decision, review, view, reviewedAt, candidate, decidedAt, note, insight, checkedAt, was, retracted}`, one step's fields filled and the others
    null, with `cascade`, `duplicates` and `contradictionCandidates` when they say something, and no memory's words. `waiting` false: the item
    was not in its queue and nothing was written; `stillCounted`: its queue still counts it, a copy with a further flag standing (fixtures
    `app_memories.resolve.*.example.json`).
  - A refusal's `code` adds what the store can say of these steps: `needs_person`, `not_offered`, `settled`, `protected`, `limit` and `not_due`
    (fixtures `app_memories.refused.not_offered.example.json`, `app_memories.refused.protected.example.json`).
  - The facade passes each of them, with `duplicates` and `contradictions`, only while the engine serves the view; otherwise each is the 400 it was
    before them. An AI client's token reaches none of them: the door sends it to the MCP tools. With `memory.tools` off none is listed or taken.
- The full memory UI's list (run 21, SB-P6-01; `engine/src/Core/Mcp/AppMemoriesManage.cs`, `engine/src/Core/MemoryBulk.cs`),
  with `memory.tools` on and no new table or function: everything a person does here goes through this tool, the app's channel,
  and keeps the rules the MCP tools have (the author lock, the one scope, the holds); no MCP tool does any of it.
  - `tags` reads the tags on the memories a `list` with the same filters holds, `{tags: [{tag, count}], truncated, count}`, most
    carried first, at most 200, and `count` the whole list's length, through the list's own predicates; a held memory's tags are
    none of them and select nothing, though it is counted where a list holds it (fixture `app_memories.tags.example.json`).
  - `changes` reads an AI client's changes this caller reads on the audit trail, since `since` (48 hours when not given) and of
    one `scope` when it names one: `{groups, truncated, since, count}` (`count` how many groups the read found; with `notHeld` true it leaves out the changes of a memory held now, as the brief does, so `count` is the brief's section's), the newest 50 groups, each `{group, family, person, client,
    changes, memories, from, until, capped, items}`, `group` the key `undo_group` takes, `items` its newest 20 memories, one
    row each with `changes` (how many of the group's are its) and at most 120 characters of its words now, none for a held
    memory or one this reader no longer reads. It reads the changes of the channel `mcp` only: what a person does in the app or
    at the terminal is no AI change, and a memory's first writing is no change to undo (fixture
    `app_memories.changes.example.json`).
  - `undo_group` takes a group's key as `changes` or an earlier `undo_group` gave it: up to 5 of its memories back as they were
    before it, `{op, group, total, remaining, done, capped, results: [{id, result}], redo}`, the rest for the next call with the
    same key. `result` is `undone`, `unchanged`, `changed_since` (it changed after the group, and stays as it is) or the undo's
    refusal (`forbidden` for one this caller may not change); a memory left as it is is named again by each call that passes
    it, and one undone already by none. `redo` is the key of this group's undos by this caller, which undoes them and nothing else it
    undid; null when this call undid nothing. A key that names no group now is `unknown` (fixture `app_memories.undo_group.example.json`).
  - `forget_all`, `restore_all` and `delete_all`, and with `memory.share` on `share_all`, take 1 to 50 `memories`, each named once
    and each exactly as its one step takes it: `{id, expected}`, `{id, nonce}`, `{id, nonce, confirm}` (`confirm` the memory's own
    id, refused before anything is read otherwise). `{ok: true, op, results}` answers each on its own, one refused leaving the
    others done: `forgotten` with `nonce`, `days` and a `cascade` when it did something; `restored` with `held` and a `cascade`;
    `deleted`; `moved` or `unchanged` with `projectLeft`; or why not (`unknown`, `forbidden`, `conflict`, and a share's `held`,
    `no_author`, `scopes_off`, or `invalid`, as a take-back that would join two people's private records). Fixtures `app_memories.forget_all.example.json`, `app_memories.restore_all.example.json`,
    `app_memories.delete_all.example.json` and `app_memories.share_all.example.json`.
  - `project_create`, `project_update` and `project_delete` are the project registry's checked writers (migration 035): a slug
    made from the name when none is given and never changed after, a name, a description (empty clears it), up to 16 aliases,
    `archived` true or false; each with `expected`, the revision read, where it takes one. A create or a change answers `{op,
    project, scopes, changed, unchanged}`, a delete `{op, id, slug, owner, memories, scopes}`, its memories kept and out of
    it. A refusal adds nothing new: `taken`, `invalid`, `conflict`, `forbidden` (the author lock: only its creator or a Family
    admin, or with scopes off any admitted login), `unknown`. `projects` adds `description`, `author`, `revision`, `createdAt` and
    `updatedAt`, optional for an engine from before them (fixtures `app_memories.projects.full.example.json`,
    `app_memories.project_create.example.json`, `app_memories.project_update.example.json` and
    `app_memories.project_delete.example.json`).
  - `capsule_status` names `capsule` (`core`, or `project` with its `project`, a slug or alias) and `scope`, else the caller's own:
    `{kind, project, scope, complete, populated, hash, chars, maxChars, served, slots}`, `slots` in the capsule's fixed order, each
    `{slot, state, filledBy, duplicates, invalid, waiting}`, `state` one of `filled`, `empty`, `duplicate` (two or more fill it
    and none is served), `omitted` (the slots before it took the budget); `waiting` the memories in it the capsule does not serve
    (`{id, status, held, expired}`). No words of any memory. `served` says whether AI clients are offered capsules
    (`memory.capsules`), which does not change what a person reads here. A refusal adds `too_many_candidates` (fixture
    `app_memories.capsule_status.example.json`).
  - `settings` reads the install's memory settings: `{scopes, person, share, capsules, capture, secondBrainImport, recall,
    recallLog, nightSummary, trashDays, versionsKept, maxBytes}`, `scopes` as the database records them and `person` the person
    this login speaks for, or null for the household view. They change in `rhythm.yaml`, never here (fixture
    `app_memories.settings.example.json`).
  - The app's facade passes each only while the engine's listing names `app_memories`, and `share_all` only while it names `share`;
    otherwise each is the 400 it was before them. A newer app reaching an older engine reads a refusal as `unavailable`, and the
    view goes on without what it could not read.
- The brief's lists (run 21, SB-P6-01; `engine/src/Core/Mcp/AppMemoriesPlans.cs`), with `memory.tools` on and no
  new table or function: a count in the brief is the length of its list in the Memories view, because both read one predicate, and a person's word
  on a row goes through this tool, the app's channel, through the checked function the MCP tool of that name uses.
  - `loops` reads the open loops this caller owns and may change (058's `memory_open_loop` and `memory_loop_mine`), newest first,
    of one `direction` (`out`, you owe; `in`, owed to you) or both: `{loops, next, count, youOwe, owedToYou}`, `count` the asked
    list's whole length and the other two both directions', all from the statement that read the page. The brief's `youOwe` and
    `owedToYou` are its `count` for `out` and `in`. Each row has at most 300 characters of its words (fixtures
    `app_memories.loops.example.json` and `app_memories.loops.owed.example.json`).
  - `due` reads the memories with a due date this caller owns and may change (069's `memory_due`), soonest first, of one `window`
    (`overdue`, `upcoming` within 48 hours, `later`) or the whole list: `{due, next, count, overdue, upcoming, later, zone}`. The
    brief's `due` counts the first two windows, which the whole list holds ahead of the later ones (fixture
    `app_memories.due.example.json`).
  - `decisions` reads this caller's decisions (069's `memory_decision_listed`), newest first, of one `state` (`open`, the default,
    `due`, `upcoming`, `resolved`, `scored` or `all`): `{decisions, next, count, open, dueNow, upcoming, resolved, scored}`. The brief's
    `decisions` counts `due` and `upcoming`, and calibration's `n` is `scored`. A row carries its `outcome`, how sure the person
    was (`confidence`, `confidenceSource`), its `reviewAt` and `window`, and `edited`, when its words differ from those it was recorded
    with (fixtures `app_memories.decisions.example.json` and `app_memories.decisions.resolved.example.json`).
  - `calibration` reads how sure the person was against how things turned out, of one `source` (`stated`, `inferred`) or all, over the
    scored list: `{source, ready, n, nStated, nInferred, minDecisions, minBand, minTopic, brier, gap, direction, bands, headline, topic,
    kind, line, topicLine, stated, hit}`. Below `minDecisions` every figure is null and only the counts are given, and a band shows its
    figures only with at least `minBand` in it (fixtures `app_memories.calibration.example.json` and
    `app_memories.calibration.not_ready.example.json`).
  - `resolve_loop` (`action` `done`, `received` or `not_a_task`), `resolve_due` (`action` `snooze` with `until`, or `clear_date`) and
    `record_outcome` (`result` `right`, `wrong`, `mixed` or `unknown`) are a person's word on one, a write's answer beside what its step
    said: `action`, `direction` and `counterparty`; `action`, `due` and `before`; `result`, `previous`, `rearms`, `reviewAt` and
    `reviewsDone`. Each keeps its step's rules: the author lock, `held` and `retracted` refused, a decision takes no due date, a stale
    `expected` is `conflict`, each is a change `undo` takes back, and its audit row names the channel `app` and no client. They are the steps
    and not `resolve`, so a person can correct an outcome or move a date already put off, which a `resolve` answers unchanged. An AI
    client's token reaches none of them here: it is sent to its own tools of those names, on its own channel, and the database refuses the
    app's channel from any login but the app's (fixtures `app_memories.resolve_loop.example.json`, `app_memories.resolve_due.example.json`,
    `app_memories.resolve_due.cleared.example.json` and `app_memories.record_outcome.example.json`).
  - The app's facade passes each only while the engine's listing names `app_memories`; otherwise each is the 400 it was before them.
- The memory UI's last pieces (run 22, SB-P6-01; `engine/src/Core/Mcp/AppMemoriesStanding.cs`, `engine/src/Core/MemoryBulk.cs`), with
  `memory.tools` on and no new table or function: the app's channel, a person's, keeping the rules the MCP tools have.
  - `list` and `tags` take `query`, words a memory's text holds, at most 200 characters as sent and never only spaces (the store
    trims them before it looks): a plain match anywhere in its text, case aside (the database's `lower()`), with no wildcards,
    beside every other filter and through the same predicates, so `tags`' `count` stays the length of the list. A held memory's
    words never select it, as its tags never do. Nothing else changes: every answer is the shape it was (no new fixture), and a
    caller naming no `query` lists exactly as before.
  - `standing` (`scope`, `project`, `cursor`, `limit` to 50): `{standing, next, count, firing, held, overLimit, maxInScope, maxFires,
    served}`, the standing instructions this caller reads, oldest first, as the MCP `list_standing` reads them (065's
    `memory_standing_rows` under the one read predicate), `count` the whole list's length and the other three how many of it may fire,
    are held and are over the limit, all from the statement that read the page. Each is `{id, revision, scope, owner, author, source,
    status, project, createdAt, content, held, fires, reason}`: its whole words (at most 500 characters), none and no project for a held
    one; `fires` false says why in `reason` (`held`, `project_held`, `over_limit`). `served` says whether any fires at all now
    (`engine.recall` on and the embeddings model loaded), as `list_standing`'s `model` does (fixture `app_memories.standing.example.json`).
  - `stop_standing` (`id`, `expected`): 065's `stop_memory_standing` as the app, the MCP tool's checked writer, its author lock and
    refusals (`invalid` for a memory that never was one, `held`, `retracted`, `conflict`): a write's answer with `changed: ["standing"]`,
    or `unchanged` when it is stopped already; `undo` starts it again (fixture `app_memories.stop_standing.example.json`).
  - `resolve_all` (`action`: `still_true`, `reject_candidate`, `confirm_insight` or `dismiss_insight`; `items`: 1 to 50, each named once,
    `{id, expected}` for `still_true`, the revision read of each memory, and `{id}` for an insight or a pair): the same step for each item,
    each through `resolve`'s one door, so each leaves its queue once with its own lock and audit row, and one refused leaves the others
    done. `{op, action, results}`, each `{id, result, revision, review, view, candidate}`: `resolved`, `unchanged` (it was not waiting, so
    nothing was written) or why not (`unknown`, `forbidden`, `conflict`, `held`, `retracted`, `invalid`); `revision` a memory's,
    `review` and `view` an insight's now, `candidate` a pair's decision now; no one's words. Every other step stays one at a time: a
    release has `release_all`, a copy's keep both is asked once its close memories are read whole, a settling names the other memory
    and a candidate's use the insight written from it. A person's step: an AI client's channel is refused before anything is read, and
    no MCP tool takes several (fixture `app_memories.resolve_all.example.json`).
  - The app's facade passes each only while the engine's listing names `app_memories`; otherwise each is the 400 it was before them.
    An AI client's token reaches none of them: the door sends it to the MCP tools, where its own `list_standing` and `stop_standing`
    answer as before.
- `app_viewer` is listed and accepted only while the engine's `app.viewers` is on; otherwise it is
  answered as a tool this role does not have (403 `forbidden`, 2.4). `enrol` registers `credential`
  for one new session with an operator's one-time code (`rhythm viewer invite <person>`: 12
  characters, once, within at most an hour) and answers the code's person; a code that is unknown,
  used, expired, or whose person is suspended or gone answers `{person: null}` alike. Only the app
  token's principal enrols, a session's credential never does (403), and a credential already
  registered is never taken over. `end` disables the calling session's credential for good; the app
  token can't end itself (403). The facade serves it to the browser as `GET /api/viewer`
  (`{available, person: {id, name} | null}`) and `POST /api/viewer` (`{op: "enrol", code}` or
  `{op: "end"}`), and while its own `app.viewers` is off answers `{available: false}` (and 404 to a
  POST) without calling the engine. The shape is `mcp/app_viewer.output.schema.json`.
- The engine MAY generalize the Garmin pair later (for example an activity summary over any
  provider); the names above are the contract until then.
- Argument validation stays as strict as today: unknown members, duplicate members or values outside
  these ranges are `invalid_arguments`.

### 2.3 Aliases

- Old names are accepted on `tools/call` for the role that had them: `observatory_*` and
  `get_relationship_graph` for `app`; `save_household_document` (now `save_document`) for `harness`.
- Aliases are hidden from `tools/list` unless `engine.legacyToolNames: true` in `rhythm.yaml` (for an
  install whose clients were set up against the old names). Default: `false`.
- An alias behaves exactly like the new name, arguments included.

### 2.4 Adapter tools

An adapter's MCP tools, harness and app alike, register only when `adapters.<name>.enabled` is true (section 3). When it is
off, its tools are absent from `tools/list` and a call to them is `-32602` (unknown tool) or HTTP 403;
neither leaks whether data exists.

---

## 3. Adapters

Every vendor integration is an optional adapter. The default install needs none of the vendor ones.

### 3.1 Kinds

| Kind | Does | Writes to |
|---|---|---|
| `MeasurementSource` | brings in numeric samples (time, metric, value, unit) | `measurements`, under a source resource |
| `DocumentSource` | lists and reads documents, with revisions | `resources` (kind `document`), optional archive copy |
| `MemorySource` | lists and reads memories or notes, with links and projects | `resources` (kind `memory` or `note`), `relationships` |
| `Writer` | performs a dashboard's declared writes at a source (a sheet row, a doc section, a log entry) | the source, then `dashboard_writes` history |
| `TitleModel` | short titles for stars that have none | star titles only; falls back to excerpts when off |
| `Exposure` | how people and harnesses reach the stack (Tailscale, Cloudflare, a reverse proxy, a ChatGPT tunnel) | nothing in the database |

One adapter may have several kinds (Drive is a DocumentSource and a Writer).

### 3.2 What every adapter has

1. **A name**, `^[a-z][a-z0-9-]{1,30}$`, and one config section `adapters.<name>` in `rhythm.yaml`
   with at least `enabled: boolean` (default `false`). The schema for each known adapter is in
   `rhythm.schema.json`. Config never holds a secret: no key may end in `token`, `password`, `secret`
   or `apiKey`. A key ending in `File` holds a path.
2. **Secret files**, named `<adapter>_<purpose>` (for example `garmin_api_key`), stored under
   `deploy/compose/secrets/`, mounted at `/run/secrets/<adapter>_<purpose>` and passed as
   `RHYTHM_<ADAPTER>_<PURPOSE>_FILE` (upper case, `-` becomes `_`). The engine reads them when it
   needs them, so a replaced file takes effect without a rebuild.
3. **A compose profile named after the adapter**, only if it needs its own container (a worker, a
   tunnel, a model server). Its services carry `profiles: ["<name>"]`, join `internal`, and join
   `egress` only if they need the internet. Turning an adapter on is `enabled: true` in
   `rhythm.yaml` plus `--profile <name>` (or `COMPOSE_PROFILES`) for its container.
4. **A provider policy** for each provider id it writes, fixed in code or config, never taken from a
   caller or from source metadata:

   | Policy | Meaning |
   |---|---|
   | `live` | the source is the truth; the engine revalidates at the source before serving content |
   | `archive` | the copy in Rhythm is an independently kept original; immutable versions are archived |
   | `reference` | titles and links only; content is resolved through the source when opened |

   Plus a default `domain` (`health`, `household`, `unclassified`). An adapter cannot change another
   adapter's policy. An unknown provider is `live` and never becomes a trusted archive. An external
   plugin's policies come from its `plugins.<name>` entry (4.1), one per provider when it writes more
   than one kind of source.
5. **MCP tools only while enabled** (2.4), and `get_sync_status` entries for its jobs.
6. **Provenance on everything it writes**: provider, subject, external id, revision or checksum,
   source time, fetch time. The app shows these; nothing is invented.

### 3.3 Planned adapters

| Adapter | Kind | Providers (policy) | Worker profile | Default |
|---|---|---|---|---|
| `notes` | MemorySource | `notes` (archive) | none: the engine reads a mounted Markdown folder | on |
| `csv` | MeasurementSource | `csv` (archive) | none: the engine reads the mounted import folder | on |
| `documents` | DocumentSource | the core's `documents` (archive), shared with `save_document` | none: app uploads and the import folder's `documents/` | on |
| `embeddings` | (local semantic index) | none | none: MiniLM via ONNX in the engine | on if the model download verifies |
| `drive` | DocumentSource, Writer | `drive` (live) | none | off |
| `garmin` | MeasurementSource | `garmin` (archive) | none: the engine calls Garmin and reads exported files | off |
| `second-brain` | MemorySource | `second-brain` (reference) | none | off |
| `nightscout` | MeasurementSource | `nightscout` (live) | none: the engine polls the site | off |
| `calendar` | MemorySource | `calendar` (reference) | none: the engine reads your own private ICS links at map rebuilds | off, and unconnected until you enter your link |
| `titles` | TitleModel | none | `titles` (an Ollama server) | off |
| `tailscale`, `cloudflare`, `openai-tunnel` | Exposure | none | one profile each | off |

External plugins (section 4) are not adapters in this repo; they are listed under `plugins.<name>`.

---

## 4. External plugins: the ingest contract

An out-of-repo plugin (the CareLink reader is the first) runs in its own container, talks to its
vendor, and writes into Rhythm only through two MCP tools. It needs no engine code and no database
access.

### 4.1 Setup

1. `rhythm.yaml` lists the plugin:
   ```yaml
   plugins:
     carelink:
       enabled: true
       providers: [carelink-live, carelink-csv]   # provider ids this plugin may write
       subjects: [alex]                           # people (or household) it may write for
       policy: live                               # live | archive | reference, for its providers
       policies: { carelink-csv: archive }        # optional: per provider, overriding policy
       domain: health
   ```
2. `rhythm token create --role ingest --plugin carelink` creates principal `plugin:carelink` with
   role `ingest`, stores the token's SHA-256 hash, and writes the token to
   `deploy/compose/secrets/plugin_carelink_token`, owned by uid 1000 with mode 0600: a plugin container
   reads its token as uid 1000. The token is shown to no one.
3. The plugin's own compose file (in the plugin's repo) mounts that file at
   `/run/secrets/ingest_token`, sets `RHYTHM_ENGINE_URL=http://engine:8080` and
   `RHYTHM_INGEST_TOKEN_FILE=/run/secrets/ingest_token`, and joins the stack's networks as external
   networks named `rhythm_internal` (to reach the engine) and `rhythm_egress` (to reach its vendor).
4. A plugin whose entry is missing or `enabled: false` gets HTTP 403 for every call.

### 4.2 Calls

- Same transport as 1.1, with the ingest token as the bearer. The role is stateless. Body at most
  512 KiB.
- `tools/list` for the ingest role shows exactly `ingest_measurements` and `ingest_records`.
- Validation is **all or nothing**: if any item in a call is invalid, nothing is written and the
  result is `isError: true` with `structuredContent.error = {"code": "...", "message": "...",
  "issues": [{"path": "/measurements/12/unit", "code": "unit_invalid"}]}` (at most 20 issues).
- Error codes: `invalid_arguments`, `provider_not_allowed`, `subject_not_allowed`, `unit_invalid`,
  `metric_invalid`, `time_invalid`, `idempotency_conflict`, `too_large`, `unavailable`; with `engine.writeLimit` on,
  `write_limit` (HTTP `429` with `Retry-After`, nothing written: section 2).
- The engine records the time of every accepted call per (plugin, provider, subject).
  `get_sync_status` and the app's source status derive freshness from it and from the newest
  `observedAt`, so a plugin needs no separate heartbeat.

### 4.3 Common fields

| Field | Rule |
|---|---|
| `provider` | one of the plugin's configured `providers` |
| `subject` | one of the plugin's configured `subjects` (a person id, or `household`) |
| `idempotencyKey` | 8–200 chars `[A-Za-z0-9._:-]`. The same key with the same payload returns the first call's result with `replayed: true` and writes nothing. The same key with a different payload is `idempotency_conflict`. Keys are kept at least 30 days. |
| times | RFC 3339 with `Z` or an explicit offset (`2026-10-28T11:55:00Z`). A device that keeps local time is converted by the plugin using the source `timezone`; never send a bare local time. |
| `timezone` | IANA name of the device or source clock; the engine computes each `localDate` with it (else with the subject's install time zone) |
| provenance | the engine adds the plugin name, the receive time and a SHA-256 checksum of each normalized item, and archives the call's payload as the immutable original when the policy is `archive` |

### 4.4 `ingest_measurements`

Arguments (schema: `mcp/ingest_measurements.input.schema.json`, example:
`fixtures/ingest_measurements.example.json`):

```json
{
  "provider": "carelink",
  "subject": "alex",
  "idempotencyKey": "carelink:alex:2026-10-28T12:00Z",
  "source": {
    "externalId": "cgm-2026-10-28",
    "kind": "glucose-readings",
    "title": "CGM readings, 28 October 2026",
    "fetchedAt": "2026-10-28T12:00:05Z",
    "sourceUpdatedAt": "2026-10-28T11:56:00Z",
    "revision": "r-1042",
    "timezone": "Europe/London",
    "device": { "id": "demo-sensor-01", "model": "Demo CGM" }
  },
  "measurements": [
    { "metric": "glucose", "value": 112, "unit": "mg/dL",
      "observedAt": "2026-10-28T11:55:00Z", "key": "2026-10-28T11:55:00Z", "quality": "ok" }
  ]
}
```

- `source` is the resource the samples hang under: unique per (provider, subject, `externalId`).
  Re-sending the same `externalId` updates its title, revision and times.
- `measurements`: 1–2000 items. `metric` matches `^[a-z][a-z0-9_.-]{0,63}$`. `key` (1–200 chars) is
  the sample's idempotency key within (source, metric); the time is a good key. Re-sending a key with
  the same value is `unchanged`; with a different value it is `updated` (last write wins, the archive
  keeps both originals).
- `unit` is required. Known metrics accept only their units: `glucose` `mg/dL` or `mmol/L`;
  `heart_rate` and `resting_heart_rate` `bpm`; `hrv` `ms`; `steps` `count`; `sleep_minutes`,
  `intensity_minutes` `min`; `insulin` `U`; `carbs` `g`; `weight` `kg`; `body_battery`, `stress`
  `score`. Any other metric accepts a unit matching `^[A-Za-z0-9%/._ -]{1,16}$`.
- `quality`: `ok` (default), `estimated`, `calibration`, `censored_high`, `censored_low`,
  `invalid`. Censored values are kept and excluded from means, as the health tools do today.
- `observedAt` is required; `localDate` (`YYYY-MM-DD`) is optional and, when given, wins over the
  computed one (for daily totals that belong to a calendar day).
- Result `structuredContent`:
  `{"resourceId": "<uuid>", "accepted": 1, "inserted": 1, "updated": 0, "unchanged": 0,
  "lastObservedAt": "2026-10-28T11:55:00Z", "replayed": false}`.

### 4.5 `ingest_records`

Text records: device settings, events, notes, reports, exported pages. Arguments (schema:
`mcp/ingest_records.input.schema.json`, example: `fixtures/ingest_records.example.json`):

```json
{
  "provider": "carelink",
  "subject": "alex",
  "idempotencyKey": "carelink:alex:settings:2026-10-28",
  "records": [
    { "externalId": "settings-2026-10-28", "kind": "device-settings",
      "title": "Pump settings on 28 October 2026",
      "text": "Basal pattern: Standard. Active insulin time: 3 h.",
      "observedAt": "2026-10-28T08:00:00Z", "timezone": "Europe/London",
      "revision": "r-77", "fetchedAt": "2026-10-28T12:00:05Z",
      "metadata": { "format": "text/plain" },
      "links": [{ "externalId": "cgm-2026-10-28", "relationship": "describes" }] }
  ]
}
```

- `records`: 1–100 items, unique `externalId` per (provider, subject). `kind` matches
  `^[a-z][a-z0-9-]{1,40}$` (suggested: `note`, `event`, `report`, `device-settings`, `document`).
- `title` 1–200 chars; `text` 0–40,000 chars, stored and searchable; `url` optional (`https:` only);
  `metadata` an object of at most 4 KiB with no secrets; `links` (≤ 20) point at other `externalId`s
  of the same provider and subject, and become `suggested` relationships.
- Result `structuredContent`:
  `{"records": [{"externalId": "settings-2026-10-28", "resourceId": "<uuid>", "status": "created"}],
  "created": 1, "updated": 0, "unchanged": 0, "replayed": false}`.

### 4.6 What a plugin MUST NOT do

- Send credentials, tokens or account identifiers of its vendor in any field.
- Write for a subject or provider outside its configuration (the engine refuses it anyway).
- Depend on engine internals, database tables or file paths. This contract is its only interface.

---

## 5. App contract types

- The app's types live in **`app/src/model.ts`**. They describe what the facade returns to the
  browser (`ObservatoryData`, `HealthMetric`, `MeasurementSeries`, `GlucoseHistory`,
  `KnowledgeNode`, `KnowledgeEdge`, `SourceStatus`, `Provenance` and the rest). `src/validation.ts`
  checks them at runtime, failing closed.
- Generalizing on import: `SourceId` stops being a fixed list of one household's vendors. It becomes
  the engine's provider id (a string such as `notes`, `csv`, `garmin`, a plugin's provider, or
  `demo`), and display names come from the engine or the adapter list.
- The engine's `app_*` `structuredContent` shapes are what the facade maps into those types. Each is
  pinned by `contracts/mcp/<tool>.output.schema.json` with a synthetic fixture in
  `contracts/fixtures/` (`<tool>[.<form>].example.json`). The engine's tests (xUnit) check its real
  answers against the schemas, the app's tests (Vitest) read the fixtures, and `npm test` at the root
  checks each fixture against its schema. The schemas keep to the JSON Schema subset that
  `scripts/json-schema.mjs` and the engine's `Schemas.cs` implement (no other keywords).
- Every value the app shows carries provenance: source, record id, observed time, and `demo` when it
  is fictional.

---

## 6. The capture endpoint

`POST /capture` lets a webhook, a CI job or a shortcut save one memory (SB-P7-01). It is on only with `memory.capture: on`
beside `memory.tools: on`, each off by default; off, the path answers exactly as an unknown route does (`404`, before any
token is looked at), and `rhythm capture` as a command the engine does not know. It is on the engine's one port, and no
recipe in `deploy/exposure/` routes it, so it opens nothing beyond what that port already reaches.

### 6.1 Setup

`rhythm capture connect <name> [--person <id>]` makes a capture token (role `capture`) in `secrets/capture_<name>_token`:
the database keeps its SHA-256, the file is shown to no one. Its one scope: with `--person`, that person's private scope
once memory scopes are on; with no person, Family. With scopes off every memory is the household's, as for every writer
(its person is its author). `rhythm capture list` shows the connections, and `rhythm token revoke <name>` refuses a token at
once. A connection is the token's name: a token made again under the name of one revoked keeps that connection's mirrors.
Delete the old file first (a token file is never overwritten), or name another with `--out`.

### 6.2 The call

`POST /capture`, `Authorization: Bearer <token>`, no `Origin`, a `Host` in `engine.allowedHosts`: the same door as `/mcp`
(1.4). Any other role gets `403`; a capture token gets `403` anywhere else. The body is one JSON object of at most 288 KiB,
not JSON-RPC:

- **By its fields** (`capture_request.input.schema.json`): `text` and only `tags`, `scope`, `project`, `source`, `mirror` and
  `idempotencyKey`. Fixtures: `capture_request.ci.example.json`, `capture_request.shortcut.example.json`,
  `capture_request.mirror.example.json`, `capture_request.session.example.json` (what a coding agent's session hook sends,
  [harness/hooks/](../harness/hooks/README.md)). `source` says where it came from (`capture` when absent, never `app`, `cli` or
  `system`); `scope`, if named, must be the token's own; `project` is a slug in that scope, made when new.
- **Any other object** is a webhook's own payload, kept whole as the memory's text, source `webhook`
  (`capture_webhook.example.json`): indented JSON with its strings as sent, so the screening reads what a reader will. A
  field of the first form beside anything else is that payload's own and decides nothing, so no such body names a status,
  a scope or a source.

`idempotencyKey` (8 to 200 of `A-Z a-z 0-9 . _ : -`), or the `Idempotency-Key` header, which is the only way for a webhook's
own payload: the same key and request apply once, the same key with another request is refused. A key is its connection's own.

### 6.3 The answer

`200` with `capture.output.schema.json` (`capture.example.json`, `capture.held.example.json`): `id`, `revision`, `status`, always
`draft`, `scope`, `scopes` (`off`: everyone in the household reads it), `held`, `mirror`, `unchanged`, `replayed`, `droppedTags`
and `message`. It holds no words, no project and no other memory: the token reads nothing.

A refusal has the door's body (1.4), its code at `result.structuredContent.error.code`:

| Status | Code | When |
|---|---|---|
| `400` | `invalid_arguments` | not one JSON object, no text to save, blank text, a NUL in any field or half a surrogate pair anywhere, a field of the wrong type, a reserved `source`, a bad key, tag, slug or mirror id |
| `401` | `unauthorized` | token missing, unknown, revoked, or bound to a person suspended or gone |
| `403` | `forbidden` | another role; a `scope` that is not the token's one; a mirror that lies outside that scope |
| `405` | | not `POST` |
| `409` | `key_reused` | a key used for another request |
| `413` | `too_large` | a body over 288 KiB, or text over 131 072 bytes |
| `429` | `write_limit` | with `engine.writeLimit` on, this token is past its writes in ten minutes (section 2). Nothing is saved, the body is not read, and `Retry-After` says how many seconds to wait |
| `503` | `unavailable` | the engine is starting, or the database is not answering |

### 6.4 What a capture is

A checked write (`capture_memory`, migration 067): a draft, never canonical before a person confirms it, with no kind,
importance or validity from outside. `set_status canonical` on one from an AI client is `forbidden`, whatever became of it
since, and so is an `undo` that would make one canonical; the app and the terminal confirm it. The guard reads the memory's own
audit trail, so it holds on its install: an export carries no such thing, and an imported copy is an ordinary draft to whoever
reads it there. The tag `task`, as a field or as a `#task` in its words, is left off and named in `droppedTags`, so an outside source
adds nothing to what the person owes; somebody who means one adds the tag after. A `source` that reads like instructions to an AI
client is refused `invalid_arguments` and nothing is saved, since AI clients read a source, and a held memory's too. Its audit row says channel `capture` and the connection's name as the client,
which
`history`, the review queue and the app's Memories view show. The channel is scored as an AI client's: the screening (instruction
phrases, hidden characters, text past 32 KiB), a held project and a burst past a connection's 40th write in ten minutes hold it
for a person, who alone releases it. A capture is not linked on save or flagged as a copy at capture: those steps need a
login that reads the scope. A person's release runs links on save as for any memory.

### 6.5 Mirrors

A capture naming `mirror` (an outside item's id, 1 to 200 of `A-Z a-z 0-9 . _ : / # -`) says this memory mirrors that item, and
says its whole state: the same connection capturing the same id changes that memory (its text, tags, source and project, each
kept as a version; nothing new answers `unchanged`), re-screened as it comes, and another connection's id is its own.

While its connection stands a mirror is read-only, on the table, for every writer: no update, append, status, undo, share,
supersede, capsule slot or forget, for a person, an AI client or the app (`403`, `forbidden`). So it stays a draft. A person's
release of a hold, a link to it and a project's delete, which takes its memories out of that project, still work. A mirror is
no insight material.

`rhythm capture disconnect <name>` removes the connection: its token is disabled and each of its mirrors moves to the trash
with its links, reason `disconnect`, one `trashed` audit row each (no principal, channel `cli`). It deletes nothing. A person
restores one within the trash's 14 days as an ordinary draft, kept by no connection, and the trash's daily purge ends what stays
past that, as for anything in the trash. An AI client sees it in the trash, reason `disconnect`, and its `undo` or restore is
refused `needs_person`. With `memory.capture` turned off, `rhythm capture list` and `disconnect` still answer once a capture token
has ever been made, since a mirror is read-only until its connection is removed; `connect` does not. Revoking a token alone leaves its mirrors read-only until the connection is disconnected
or a token is made again under its name. An export carries a mirror as a memory with no connection.

## Studio authoring and typed dashboard selections

`studio_dashboard` owns a durable draft session (`begin`, `read`, `update`, `progress`, `save`, `cancel`, `discard`). A begin takes an agent-generated UUID sessionId/requestId, a screen id and optional saved dashboardId. Update takes a complete renderable DashboardDraft, optional existing native layout, optional typed control manifest and expected revision. Each mutation has a UUID requestId; the same requestId is only an identical retry. Read returns the persisted snake_case session fields, ordered event/feedback sequences, current candidate, last validated preview and status. There are at most 100 revisions, 400 events and 100 feedback entries per seven-day session. Publication is atomic and conflict checked. See [Studio's contract](../docs/dev/studio.md).

`discover_dashboard_controls` takes screen, dashboardId and version. `invoke_dashboard_action` also takes selectionRevision, control id and typed value. The screen returns the existing command receipt with shown/failed/pending/delivered state, detail and measured delivery/show timing. The detail includes the actual selection revision and values; a missing manifest is reported as no controls. A tab/filter value must be a declared option, week is an integer -520..520, date_range is `{from,to}` in YYYY-MM-DD with from <= to. Known graph focus uses existing `control_screen focus_map`. There is no generic script/DOM execution tool.

`app_studio` is app-role only: active/read/data/controls and target-screen validate/feedback/cancel/discard/save. Browser calls cannot supply draft code. The app validates exact snapshots through its existing sealed sandbox; preview source writes are refused. Sources are still read using the authenticated principal's normal permissions. Watch code contains actual received code and timestamps; completed batches are not presented as token streaming.
