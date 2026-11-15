# Security

How an install is protected, what it shows by default, and what never to expose. Rhythm of Life is
in alpha and has had no outside security review ([known-limits.md](known-limits.md)).

## The model: no login, the network is the gate

- **There is no login inside Rhythm of Life.** No accounts, passwords, sessions or cookies. Whoever
  reaches the app sees everything in the install, and can give insight feedback, ask for a map
  refresh, change what the household's other open screens show ([below](#screens)) and, on dashboards
  where edits are allowed, make their declared writes.
- **The household shares everything.** Everyone listed in `people` sees all of the install's data;
  there are no per-person or per-document barriers. Don't add people you would not show everything to.
  Two settings will change this, both off on every install:
  [per-person memory scopes](#per-person-memory-scopes) (`memory.scopes`, which this version accepts
  but no install has on until its operator sets it) and [viewer sessions](#viewer-sessions) in the app
  (`app.viewers`, refused by this version). While both are off, this section is the whole model.
- **Who gets in is decided by where it listens and what stands in front of it**: this computer only
  (the default), your home network, Tailscale, Cloudflare Access, or your own reverse proxy with a
  login ([deploy/exposure/](../deploy/exposure/README.md)).
- **AI clients get a token each.** The engine's MCP endpoint is the one door for them, and it checks
  a bearer token on every call. ChatGPT reaches it through Cloudflare or OpenAI's Secure MCP Tunnel
  ([below](#the-openai-tunnel)).

## What is exposed by default

With the shipped `compose.yaml` and an empty `.env`:

| What | Where | Reachable from |
|---|---|---|
| the app | `127.0.0.1:8080` | this computer only |
| the engine's MCP (`POST /mcp`) and `GET /health` | `127.0.0.1:8181` | this computer only, and `/mcp` needs a token |
| Postgres | the `internal` network only, no port | the engine, `migrate` and `backup` containers |
| `cli`, `backup` | not running; started on demand (`--profile cli`, `--profile backup`) | |
| exposure containers (`tailscale`, `cloudflared`, `openai-tunnel`) | not running unless their profile is on | |

With `memory.capture` on beside `memory.tools` (off by default), `POST /capture` answers on the engine's same port,
[below](#capturing-from-outside). Off, it is a path like any other the engine does not know.

The app, engine and `migrate` containers run as non-root users with read-only file systems and all
capabilities dropped; every container has `no-new-privileges`. The `internal` network has no internet
access; only services that publish a port or talk to a vendor also join `egress`.

`RHYTHM_BIND=0.0.0.0` publishes the app and the engine to every network the computer is on. That is
fine on a home network you trust; on a computer with a public address it puts the app on the internet
with no login.

## Tokens and roles

Every caller of `/mcp` is a principal with exactly one role (`contracts/README.md` section 2). The
database keeps only each token's SHA-256 hash; the token itself lives in one file and is shown to no
one.

| Token | Made by | Role | Can | Cannot |
|---|---|---|---|---|
| the app token, `secrets/app_token` | `rhythm init` | `app` | the `app_*` tools: reads for the screen, insight feedback, refresh requests, dashboards' declared writes, screen commands; for a screen bound to it that a [Studio](dev/studio.md) draft is on, reading that draft and validating, feeding back on, cancelling, discarding or saving it (`app_studio`), a save publishing the dashboard as the AI client that began the draft (its harness principal, whose access to the draft's sources is checked again then), not as the app; with `memory.tools` on, the Memories view (`app_memories`), a person's channel: it saves and changes memories as the household view, reviews held ones and releases one or several, puts a memory into a capsule slot and deletes a memory from the trash for good, links two memories (a person's link, verified, which confirms an AI client's or an automatic one) or unlinks them, dismisses or takes back a map suggestion, says a memory is still true and takes its flags for a check off, marks a task done or received, snoozes or clears a date and records how a decision turned out, forgets, restores, deletes for good or (with `memory.share` on) shares several at once, undoes an AI client's group of changes, makes, changes, archives and deletes a project, keeps both of a possible copy, settles a possible contradiction, decides a pair of memories worth a look and confirms or dismisses an insight (with memory scopes on it reviews none, since the household view is no one's: a person reviews from the terminal), under the author lock (with memory scopes on, Family's memories only, and a person's Family memory not at all) | any harness or ingest tool |
| one per AI client, `secrets/harness_<name>_token` | `rhythm token create <name>`, with `--person <id>` to speak for one person | `harness` | read everything in the install (with [memory scopes](#per-person-memory-scopes) on: its person's records and Family's, or Family's alone without a person); save insights, dashboards (pages, feeds, declared writes) and document copies; delete a dashboard when asked; change what an open screen shows; draft a dashboard live on a screen in Studio and change a saved dashboard's typed selections there | the app's and plugins' tools; turning on a dashboard's edits (only a person in the app can) |
| one per external plugin, `secrets/plugin_<name>_token` | `rhythm token create --role ingest --plugin <name>` | `ingest` | `ingest_measurements`, `ingest_records`, only for the providers and people its `plugins.<name>` entry lists | anything else, or anything while its entry is off |
| one terminal login per person, no token file | `rhythm remember`, `rhythm recall` or `rhythm list --person <id>` on the host, or with `memory.tools` on `rhythm release`, `rhythm capsule`, `rhythm delete-forever`, `rhythm forget`, `rhythm set-status`, `rhythm link`, `rhythm unlink`, `rhythm still-true`, `rhythm clear-flags` or `rhythm undismiss --person <id>` (and with `memory.share` on `rhythm share-memory`), on first use for a current person (`rhythm token list` shows its role as `terminal`) | `harness`, bound to that person and marked terminal | through the terminal's commands only: save a memory as that person and list and recall what that person reads, as their own AI client would; with `memory.tools` on, that person's own steps no AI client takes (`rhythm release`, `rhythm capsule`, `rhythm delete-forever`: release a hold at the revision read, a capsule slot, delete for good from the trash once they type the memory's id; `rhythm queues`, `rhythm duplicates`, `rhythm contradictions`, `rhythm insight-candidates`, `rhythm insights` and `rhythm resolve`: a person's review queues, a settling and an insight's review among them), forget a memory into the trash, set its status and read a capsule as AI clients are served it, link two memories or unlink them, say one is still true, take its flags for a check off and take back a dismissed suggestion on their map, and with `memory.share` on share a memory with Family or take one back (`rhythm share-memory`), under the author lock; the only login that writes through the terminal's channel (`cli`), a person's own channel, which no AI client's token reaches | `/mcp`: the door refuses it, and no token for it exists to give anyone (its hash is of random bytes nobody keeps); anything once its person is suspended, after `rhythm token revoke --all`, or after a restore, which disable it as they disable every token (the next command for a current person makes a new one) |
| one per viewer session (only with `app.viewers` on) | the engine, when the app's server registers it with a one-time code from `rhythm viewer invite <person>` | `app`, bound to that person | what the app token can, as that person (with memory scopes on, their records and Family's; a document it adds is that person's own unless it chooses Family) | registering another session; anything once it is signed out, revoked or past its 30 days, its person suspended, or while `app.viewers` is off (401) ([below](#viewer-sessions)) |
| one per capture connection (only with `memory.capture` on), `secrets/capture_<name>_token` | `rhythm capture connect <name>`, with `--person <id>` to write into that person's scope | `capture` | `POST /capture`: save a memory into its one scope, as a draft or held for a person | read anything; write any other scope; reach `/mcp` or the app's routes; confirm, release or change a memory; [below](#capturing-from-outside) |

- **One token per client**, so you can revoke one (`rhythm token revoke <name>`) without touching the
  others. A revoked token gets `401` at once.
- **A token's person is set when it is made and never changes**; `rhythm token list` shows it, or "household
  view". `rhythm person suspend <id>` disables every token and viewer session of that person at once, for good.
  With memory scopes off a person only records who wrote what.
- **The app token is for the app's server only.** Never give it to an AI client or a browser; the
  browser never sees any token.
- **Rotate** the app token by replacing its file and restarting the engine and the app. Rotate a
  harness token by revoking it and creating a new one.
- **A restored backup disables every token** (`engine/backup/restore-quarantine.sql`), because an old
  backup could bring back access you have revoked since. Create new harness, plugin and capture tokens after a
  restore: `./rhythm up` names each harness token file that no longer works, and checks no plugin or capture token
  file ([backups.md](backups.md)). The engine takes the app's from its file again.
- **A restore undoes what you did since the backup, for safety too**: a person suspended since is current again (a new
  token could be made for them), a person taken out of Family since is a member again, a source share or a Family admin
  taken back since is back, a capture connection disconnected since is connected again, its token disabled with the
  rest, a person offboarded since is as they were, their private memories out of the trash, and a memory deleted for
  good since is back, in the trash or out of it as the backup held it. Do those steps
  again after a restore ([backups.md](backups.md#when-a-restore-stops-and-what-it-undoes)). `./rhythm restore` removes
  the restored folder, a plain copy of the database, once it has loaded or failed.
- Some MCP clients accept only OAuth for remote servers. Rhythm has bearer tokens only for now (open
  question Q-016).

## The OpenAI tunnel

The `openai-tunnel` recipe ([deploy/exposure/openai-tunnel/](../deploy/exposure/openai-tunnel/README.md))
works the other way round from the rest: nothing listens for the internet, and the AI client holds no token.

- **Outbound only.** OpenAI's tunnel client opens HTTPS connections to `api.openai.com` and polls for
  ChatGPT's calls. It publishes no port, its health and admin pages answer only inside its container, and
  only its MCP channel is on.
- **The token stays on the server.** `secrets/harness_openai-tunnel_token` is an ordinary harness token,
  owned by the client's user and sent by the client with every call. `rhythm token revoke openai-tunnel` cuts
  ChatGPT off at once; after a restore, make it again (the recipe's Rotating section).
- **OpenAI decides who uses the tunnel.** Everyone in the ChatGPT workspace associated with it who can add a
  custom MCP server, and every associated Platform organization whose keys have Tunnels Use, reaches the
  harness tools with that one token. Associate it with the household's own workspace only (open question
  Q-124).
- **The runtime key** is a restricted key with Tunnels Read and Use only. Whoever holds it can run a client in
  your tunnel's place and receive ChatGPT's calls, so keep it like a password and rotate it if it may have
  leaked.

## What the engine and the app check

- **Engine:** only `POST /mcp`, `GET /health`, `GET /app/documents/<id>` (a document's original file, for the app's
  token alone) and, with `memory.capture` on beside `memory.tools`, `POST /capture` (for a capture token alone) exist;
  the `Host` header must be in `engine.allowedHosts` (default `localhost`, `127.0.0.1`, `[::1]`, `engine`), which stops
  DNS rebinding and names you did not plan for; any request with an `Origin` header is refused, so no web page can call
  the engine from a browser; request bodies are capped. Tool results are data: tool
  descriptions tell models that source text is untrusted, never instructions.
- **The engine's database login** reads and writes the install's tables (the role checks inside its database
  functions limit callers, not the login), cannot change the schema or make temporary tables, and changes the memory
  tables only through their checked functions ([dev/engine.md](dev/engine.md#database-login)).
- **App:** its own server answers only to `localhost`, `app`, IP addresses and the install's own
  host names (`app.allowedHosts` in [configuration.md](configuration.md)), which stops DNS rebinding; writes are
  accepted only from the app's own origin (the same-origin check, which needs a
  proxy to keep `Host` and set `X-Forwarded-Proto`); responses are `no-store` with `nosniff`,
  `X-Frame-Options: DENY` and a strict referrer policy. It never treats a header or cookie as a
  person's identity.
- **Dashboards** run in a double-sandboxed frame: a page an AI wrote cannot read the app, and its
  content policies refuse every request they cover (fetch, images, fonts, scripts, frames, forms,
  navigation). They don't cover WebRTC, whose STUN lookups can reach any address. The page's own
  peer connection is removed before it runs, which stops a page that simply opens one, but not one
  written to get round it: in a Chromium browser (checked), a page that draws a frame of its own
  gets a fresh peer connection there and can send what it was shown. Firefox and Safari are not
  checked yet. Its writes are only the ones it declared, only after a person turns on Allow edits,
  with history and Undo.

## Saving pages from the browser

With `memory.tools` on, the app's Memories view offers a bookmark that saves the page you are on as a memory. Second
Brain's bookmarklet does this with its service token written inside the bookmark, where every page it runs on can read
it; this one holds no token. With `memory.tools` off (or the engine out of reach) an address that carries a page opens
the app as any address does: nothing to save, and nothing of the page sent anywhere.

- **The bookmark holds the app's address and nothing else.** It is made for the address you opened the app at. Clicked
  on a page, it opens the app in a new tab, with no referrer and no way back to that page, and hands it the page's
  title, its address and the text you selected, after the `#` of the app's address: browsers send that part to no
  server in the request, the app's included. The page it runs on learns the app's address, nothing more. A page's own
  scripts can change what a bookmark hands over, as with any bookmarklet, so read what arrives.
- **Nothing is saved until you press Save** on the app's own page, which shows what came as text you can change. It
  saves through the app's server like everything else the app does, so the browser still holds no token. A page can't
  save into Rhythm by itself (the app takes writes only from its own origin), and since anyone can make an address that
  opens this page with words of their choosing, the page never saves on its own.
- **The words are checked like any memory's.** It lands as a draft in Family, tagged `reading`. Through the app's own
  login (no viewer session) its words are scored as an AI client's: text that reads like instructions to an AI is held
  for review, and hidden characters always are.
- **What the browser keeps.** The app's page takes what came out of the address bar as soon as it reads it and keeps
  nothing in the browser, so closing or reloading the tab drops it (click the bookmark again). The browser's own
  history may still list the address the tab opened with, selection included, and a browser that syncs its history
  sends that address to its sync service too. A page's address is saved as the page shows it: edit out anything in it
  that works like a key, such as a sign-in or reset link.
- **Sign in to the app first.** If a front door in front of the app (an access proxy, a hosted sign-in) sends a
  signed-out visitor to its sign-in page, browsers carry everything after the `#` along to that page, which can read
  it, and it is lost on the way back: you see the app, not the page for saving. Open the app and sign in, then click
  the bookmark again.

## Capturing from outside

With `memory.capture` on beside `memory.tools` (both off by default), a webhook, a CI job or a shortcut can save a memory
with `POST /capture` and a capture token of its own ([contracts/README.md](../contracts/README.md) section 6). Off, the
route answers as an unknown route does and nothing else differs.

- **It writes and reads nothing else.** A capture token writes into one scope: with no person, Family; bound to a person,
  that person's private scope once memory scopes are on. With scopes off every memory is the household's, as for every
  writer. Naming another scope is refused. It reads nothing, in either mode: its answer holds no words and names no other
  memory, and it reaches neither `/mcp` nor the app's routes.
- **What it saves is not trusted.** A capture is a draft, never canonical before a person confirms it (an AI client asking
  to, by `set_status` or `undo`, is refused, on any capture, whenever it asks; the rule reads this install's own audit trail, so an
  exported copy imported elsewhere is an ordinary draft there), and it takes no kind or importance from outside, no tag `task`, so
  it adds nothing to what you owe, and no source name that reads like instructions, which is refused. It is not the day's old
  memory on Home until a person confirms it, and a mirror never is. Its channel is
  `capture`, on its audit row with the connection's name, and is scored as
  an AI client's: instruction phrases, hidden characters, text past 32 KiB, a held project and a burst past the
  connection's 40th write in ten minutes all hold it for a person, who alone releases it.
- **It is revocable** as every token is: `rhythm token revoke <name>` and `rhythm token revoke --all` refuse it at once,
  suspending its person disables it, and a restore disables it. A token's hash is all the database keeps.
- **A mirror is read-only.** A capture may name the outside item it mirrors, and the same connection capturing that item
  again changes that memory. Nothing else changes it, not a person, an AI client or the app; a person may still release a
  hold on it and link to it. `rhythm capture disconnect <name>` disables the token and moves every mirror of that connection
  to the trash, with its links, where a person restores it as an ordinary draft within the trash's 14 days. An AI client
  can see it there and can't bring it back. Nothing
  is deleted at that step; as with anything in the trash, the daily purge ends what stays there past its retention.
- **It opens nothing new.** There is no new port, tunnel or public route: `/capture` is on the engine's one port
  (`127.0.0.1:8181` by default), behind the same `Origin`, `Host` and token checks as `/mcp`, and every recipe in
  [deploy/exposure/](../deploy/exposure/README.md) routes only `/mcp`. So a caller reaches it only from where the engine's
  port already reaches: this computer, or your network with `RHYTHM_BIND=0.0.0.0`. Routing it to the internet is
  a choice no recipe makes, and it would put a write path behind nothing but a token; don't add it on your own.
- **Limits.** A body is at most 288 KiB and a memory's text at most 131 072 bytes. Refusals are logged by status and code,
  never with the payload. With `engine.writeLimit` on, a connection past its limit is refused outright ([below](#the-write-limit)).

### Session hooks for coding agents

A hook ([harness/hooks/](../harness/hooks/README.md)) saves one finished message of a coding agent's session through
`POST /capture` with a capture token. It is a file you install on purpose, in each agent's own settings; nothing installs it,
and it does nothing until a settings file of yours says `"capture": "on"` and lists the folders it may capture from.

- **Never a transcript.** It reads only the field in which the agent documents a finished message. It does not open the
  transcript, so tool output, file contents and what you typed stay out. One message a session, at most 2000 characters by
  default, fenced code left out, and only if a content gate on this computer finds something lasting in it.
- **Secrets go first.** It redacts before anything is written to disk and again before anything is sent: its own token,
  values the agent's environment names as secrets, key shapes, private keys, connection strings, passwords and
  high-entropy strings. **This is a pattern match, not a promise**: a secret with no telling name or shape can get through. Look
  at what your first few sessions leave in the review queue, and delete any that hold what they should not.
- **What arrives is not trusted.** An agent may have read a hostile file before it wrote its message. It is a capture like any
  other: a draft, held for a person if it reads like instructions, and readable by the household while memory scopes are off.
- **The token stays out of reach of settings files.** It is read from a file you name, never from an agent's settings or a
  command line. It goes only to `/capture` on the engine you named, with no redirect followed and, past this computer, only
  over https unless you say otherwise. Revoking it stops the hook at once.
- **What waits on your computer** (a message until its session ends, already redacted) is in a private file beside the
  settings, deleted when sent, refused or two weeks old, or at the next event after your settings file turns capture off.
  Each of those needs the hook to run: if you remove it from your agents, delete its `hooks-pending/` folder yourself.

## The write limit

`engine.writeLimit` in `rhythm.yaml` is `off` on every install until its operator sets `on`. Off, no token is counted, every tool
answers as before, and the rest of this section does not apply. It is behind a setting so that nothing already deployed answers
differently because of it.

**What it does.** With it on, each token may make **300 writes in 10 minutes**. Its next write is refused and changes nothing, until
its oldest write leaves those ten minutes: an AI client's tool answers an error saying so and how many seconds to wait, a plugin or
a capture connection gets `429` with `Retry-After` ([contracts/README.md](../contracts/README.md) section 2). A write it refused
is not counted, so a client that keeps asking waits no longer than one that stops.

- **What counts:** every harness tool not marked read only (a tool that says nothing counts; `WriteLimitTests` pins the list), a
  plugin's `ingest_measurements` and `ingest_records`, and `POST /capture`. One call is one write, whatever it holds, and one
  that fails for its own reasons still counts. So does a `supersede`'s plan, though it writes nothing: a replacement takes two (the plan
  and its confirmation), and past the limit a plan is refused too. A harness call whose arguments do not fit the tool's schema is not
  counted. An ingest or capture call counts whether or not its arguments fit. Past the limit a capture call is refused before
  its body is read; a plugin's is refused once its body reads as an ingest call, before its arguments are checked (one that
  names another tool or whose arguments are not an object is refused as before, and not counted).
- **What never counts:** reads, `tools/list` and the handshake, so a token past its limit reads as ever; and the `app` role, its shared
  login included, and the terminal, whose logins are never counted, not even when they are busy (below).
- **Each token alone.** Two AI clients are two counts, as they are for a burst. A token made again is a new count: only the operator
  can do that.

**How it goes with the hold on a burst.** It stands behind that rule, on the same ten minutes, so a burst is still seen. A token's
first 40 memory writes save as written. The next are saved but held for a person, who alone releases them, as for any burst. Past
300 nothing is saved until the oldest write leaves the ten minutes. So a CI job that writes 60 memories in ten minutes has 20 held and
none refused. One that runs away adds at most 260 held in its first ten minutes, then at most 300 in any ten minutes, 30 a minute, for
as long as it runs: the ten minutes slide, so the limit slows it, the review queue has no ceiling (about 43,200 a day), and only revoking
its token stops it. The two count different things: the burst counts what changes a memory's words (an
undo, a status change or the trash are not counted) and reads the database's clock, while the limit counts every write tool and
keeps its own monotonic clock, so a host's clock stepping does not move it.

**How the number was chosen.** From this repository's synthetic data and tests, the most any one token writes in ten minutes:

| Measured | Busiest token |
|---|---|
| the synthetic corpus: a person's busiest day, in any channel (through an AI client: 2) | 3 writes |
| a coding agent's session hook | 1 a session |
| the daily insight prompt | 1 insight and 1 logged run, and a decision for each candidate it read |
| `cli/e2e.sh`, the whole run | 3 writes |
| `cli/lifecycle.sh`, the whole run, an AI client's full memory lifecycle | 14 writes |
| `cli/journeys.sh`, from the step that turns the limit on: a person's journeys in the app, with two AI clients and a plugin | 33 writes |
| the engine's tests, 402 AI-client and capture logins on the shared test database: median 5, 90th percentile 21, 99th percentile 49 memory writes | 202 writes: a test that writes more than a page of changes on purpose. Next, 91 and 78, tests that drive the burst rule; then at most 52 |
| the whole corpus written by one token in one sitting, driven through the tools and counted by the limit: 98 memories, 29 links, 97 confirmations, 10 replacements (a plan and its confirmation each), 11 documents, 6 dashboards, 3 insights and 3 runs | 267 writes |

300 clears every row: about 9 times the busiest token of the end-to-end runs, and above the whole corpus at once, by 33 writes. It is 7.5 times the burst
rule's 40, so a burst is held long before anything is refused. It leaves a token one write every 2 seconds, held all the while. These are synthetic
numbers: no real install was measured, and nothing here reads one.

**What it does not do.**

- **It is a brake, not a wall.** A token at the limit can still write 30 times a minute for as long as it runs, and a leaked token
  should be revoked (`rhythm token revoke <name>`), which stops it at once.
- **It does not limit the app.** The `app` role never asks. In this version (`app.viewers` stays off) its one login is the shared one,
  which no person signs in to and a hosted front end holds too, so a leaked app token writes uncounted. `rhythm token revoke` does not
  reach it: rotate it, by replacing its file (and on a hosted front end its copy there) and restarting the engine and the app ([above](#tokens-and-roles)).
- **A restart forgives.** The count is kept in the engine's memory, with no table and no write for a read or a refusal, so a restart
  starts every token at nothing.
- **A plugin's backfill can meet it.** A plugin that sends more than 300 calls in ten minutes gets `429` for the rest. Each call is all
  or nothing and safe to repeat with its key, so it can go on later, if the plugin does; check yours before turning this on. The CareLink
  plugin's code sends about a call per pump day of an export, so 90 days stay under and a year's would not all go at once. No plugin has
  been run against the limit.
- **It sets no number per token.** There is one limit and one setting; a job that truly needs more should slow down, or the operator
  leaves the setting off.

The engine's log has a line for a refusal, at most one a minute for each kind: the role, the tool, `429` and `write_limit`, never a payload.

## Screens

Every open copy of the app is a named **screen**. An AI client (`control_screen`) or another screen ("Show on…")
can send it a command ([dev/screens.md](dev/screens.md)); the screen shows a note naming the sender, with Undo.

- **A command changes only what a screen shows**: a star, a dashboard (full screen too), a search, Home, the layout,
  the places of the tiles, which dashboard that screen shows as its Home, or how its Home's parts are arranged. It
  never edits data, with two exceptions: `corner_card` sets or clears a corner's dashboard card for the whole
  household, through the same call as the corner-card picker, which anyone at the app can use already (on a
  [viewer session](#viewer-sessions)'s screen, with memory scopes on, for that session's person alone); and `move`,
  `resize` and `focus_map` on a dashboard arrange it for the household (below), as anyone at the app can by hand. A dashboard shown full screen, or as a screen's Home,
  runs in the same sealed frame as anywhere else.
- **No new login.** Anyone who can open the app can command its screens, and an AI client uses the harness token it
  already has. So whoever reaches the app, and every AI client with a token, can put anything in the install on any
  open screen, one in a shared room included. There is no private mode for a screen yet.
- **Names are labels, not identity.** A screen's name and the sender in its note ("From your assistant", "From the
  screen “Kitchen iPad”") are text a browser chose. A screen is always shown as a screen, so no name it chooses reads
  as the assistant. Every screen reaches the engine as the one app principal (a screen in a
  [viewer session](#viewer-sessions), once those are on, with that session's own); nothing is allowed or refused by a
  name. A screen's id is how commands find it, and belongs to the app login that first listened with it: no other
  login listens, reports, sends or renames as that screen. A new screen whose name another screen already has gets a
  number after it, to tell them apart, never to prove which is which.
- **Arranging a dashboard.** From piece 3 the app's login can change how a household dashboard is arranged
  (`app_dashboards` `{id, version, layout, base, note}`): the same parts, moved, resized or refocused, never added,
  removed or changed in kind, as it already sets corner cards. The database refuses anything more from that login.
  Only the app's own controls (its grips, keys and the screens' commands) do that, never a page: the page's frame
  hears nothing about layouts or screens, so a page can't move a part, change a focus or control a screen. A map
  part's focus only narrows what the reader's map shows; it never widens it.
- **Commands expire.** A command the screen has not picked up within 30 seconds expires and is never shown later, so
  a screen that wakes up hours afterwards does not replay what was sent meanwhile. At most 20 wait per screen.
  Commands are kept 30 days, and screens unseen for 180 days are removed.
- **Checked before stored.** Unknown actions and arguments are refused; a star must be on the map now, a dashboard
  must exist, and a search is 1-100 characters with no control characters; a part must be one of what the screen shows
  (the dashboard's newest layout, or Home's five parts), and a focus star must be on the map now. What a screen keeps
  (its layout, tiles, Home and Home grid) is checked whenever a screen sends it.
- **Dashboards cannot reach screens.** A dashboard's page runs in the sealed frame above: its content policies refuse
  its requests, `/api/screens` included, and the frame's bridge offers no screen command. A screen that shows a
  dashboard is commanded like any other.

## Per-person memory scopes

`memory.scopes` in `rhythm.yaml` is `off` on every install until its operator sets `on`. This version of the engine
accepts `on`: every way of reading data filters by scope and the authorisation tests pass with it on, for native
memories and for every record the engine keeps. Off, everything in this file above holds. This section says what the
mode protects, what it does not, and how an install switches. The full contract is [dev/memory.md](dev/memory.md).

**What it protects.** Every row the engine keeps belongs to a subject: one person, the household, or the fictional demo.
With scopes on:

- **A person's private records** are read only by tokens and viewer sessions bound to that person: their own memories,
  and whatever their adapters and plugins write for them (glucose, Garmin, a CSV import, a Drive or notes folder whose
  `owner` is that person). Another person, an admin, the household view and the app with no viewer session read none
  of it, unless the operator records a source share for that source (`rhythm share add <person> <provider>`, step 3
  below): Family, the household view included, then reads that one source's rows until the share ends.
- **Family** (the household's records) is read by Family's members and by everything with no person: AI client tokens
  made without `--person`, and the app with no viewer session. DEMO rows stay everyone's.
- **Copies follow their original.** A document an AI client saved, a map star, a search passage, an insight's or a
  dashboard's titles and a pin are served only to readers of what they came from, as it is now: when a record leaves
  Family, every copy of it stops reaching those who lost it at their next read, with no rebuild to wait for.
- **Nothing leaks by what is missing.** A record you can't read answers as an unknown one, never "forbidden"; counts,
  neighbours, ranking and result limits are each reader's own; the models' index counts, which add up every scope, are
  left out for everyone and only the operator reads them (`rhythm doctor`).
- **Switching on only narrows.** Nobody reads anything with scopes on that they could not read with them off. The engine
  records the mode in its database, so a lost or older `rhythm.yaml` (or a restored backup) cannot quietly turn them off:
  starting `off` against a database that records `on` stops the engine.
- **Changes need the right person.** A private record is changed only by its owner; a Family record by the person who
  wrote it or a Family admin (one that no person wrote, by anyone who reads Family, as today). An admin never reads
  anyone's private records.
- **An AI client saves documents only for its own person or Family.** A client bound to a person saves a copy of a
  document (`save_document`) for that person, or for Family when it names Family and its person is a member; a client
  with no person, for Family only. A copy for anyone else is refused before anything of theirs is looked at, so the
  refusal says nothing about what they hold. Left to choose, a client bound to a person saves for that person. (This
  rule came with migration 031: the development builds before it accepted scopes on but let a client save into any
  person's records.)
- **Suspending a person** (`rhythm person suspend <id>`) disables every token and viewer session bound to them at once;
  with scopes on nobody reads their records meanwhile.
- **Offboarding a person** (`rhythm person offboard <id>`, on the host only, after a preview and their id typed) ends
  the access bound to them as a suspension does, takes them out of Family and ends their source shares, all at once,
  then moves their private memories to the trash, from where the daily purge deletes them for good after 14 days. The
  Family memories they wrote stay, with them as their author; their records from adapters and plugins stay as their
  source keeps them, read by nobody with scopes on. With scopes off it ends that access and moves nothing, since nothing
  is private to anyone then. It cannot end what is bound to no person, and its preview names each: a household,
  plugin or capture token they used stays enabled until `rhythm token revoke <name>` (on the default install, scopes
  off and one shared `assistant` token, that token keeps full MCP reads and writes); the app has no login, so
  whoever reaches its network reads what the household reads; and an adapter or plugin reading their own account keeps
  adding records until it is disconnected ([../cli/README.md](../cli/README.md#offboarding-a-person)).

**What it does not protect.**

- **The operator and the host.** Whoever runs `rhythm` on the host, or holds the database, the secrets folder, `.env` or
  a backup, reads everything. Backups hold every scope, encrypted with the backup password ([backups.md](backups.md)).
  The engine's own jobs (the map rebuild, the search index) read everything to build what each reader is served.
- **What a client was already told.** An AI client keeps what Rhythm answered it. Revoking its token or un-sharing a
  record stops the next answer, never takes one back.
- **The app without a viewer session.** Whoever reaches the app sees Family, as the household view; screens show it
  too. It is still the network that decides who reaches the app.
- **Records the settings keep under the household.** An adapter whose `owner` is `household` keeps its rows in Family,
  as before: the Second Brain personal workspace with the default owner is one, and `rhythm doctor` names it.
- **Who may open a file at its source.** Scopes decide who sees a Drive file in Rhythm, not who may open it in Google.
- **What the audit trails can't do.** Setup changes (Family's members and admins, source shares, tokens, a person's
  status, the switch) and memory changes are recorded in insert-only trails in the database (migrations 025 and 037),
  for the database's owner too; but whoever holds the database can still drop or rewrite them. A memory's trail is
  shown as its history (the `history` tool and the Memories view, with `memory.tools` on); the setup trail is shown
  nowhere yet. Nothing here has had an outside review.

**How an install switches on** (for a real household that is the owner's decision):

1. Bind each AI client to its person: `rhythm token revoke <name>`, then `rhythm token create <name> --person <id>`. A
   client left without a person reads Family only.
2. Run `rhythm doctor`: with scopes off it previews what each token would stop reading, as counts per person and
   source, never a title, and lists by id and title each Family dashboard whose log would take it off the household
   view (step 3).
3. Record the sources Family should keep seeing (`rhythm share add <person> <provider>`, for a glucose feed a shared
   screen shows) and name at least one admin (`rhythm family admin <id>`). A shared dashboard whose log names no subject
   logs as the first person in `people`, so from the switch on it is that person's readers' alone, off the household
   view and its screens (one from before that rule, whose log names nobody, is no one's): for each dashboard
   `rhythm doctor` lists, have the AI client save a new version whose log feed and write name `subject: household`,
   then run `rhythm doctor` again until it lists none.
4. Set `memory.scopes: on` and run `./rhythm up`. The first start adds everyone in `people` to Family; anyone added to
   `people` later joins only when the operator adds them (`rhythm family add <id>`).

**Switching off on purpose** is `rhythm scopes off` on the host: it previews what would become visible to everyone
(counts only), and is refused while any memory is private, so it never turns a private record into a household one.

## Viewer sessions

`app.viewers` in `rhythm.yaml` is `off` on every install, and this version refuses `on` (the engine and the app both stop
at start): an identity in the app is the owner's decision, beside open question Q-006's "no built-in login". Off, the
app has no sessions or cookies of its own and everyone at it sees the household view, as above. This section says how
the feature is built, for when it is approved.

**How it works.** The operator makes a one-time code for one person on the host: `rhythm viewer invite alex` prints it
once (12 letters and digits, valid once, within 15 minutes or at most an hour; the database keeps only its SHA-256).
Someone at the app types it in. The app's server then registers a new credential with the engine, using its own app
token and that code; the engine makes it an `app` login bound to the code's person, and from then on reads the person
from that credential alone. The browser gets an HttpOnly, SameSite=Strict cookie (`__Host-` over HTTPS) holding a
random handle: never the credential, the app token or a person's name. The app's server derives the session's
credential from the handle and its app token on each request, so it keeps nothing, and a cookie it did not make (or one
past its 30 days) is the household view. A header or a cookie is never taken as identity, and the app token alone
always reads the household view, whatever is sent beside it.

**What it protects.** With memory scopes on, a session reads what its person's own AI client reads: their private
records and Family's, never another person's. Each session has its own credential, so ending one ends no other. A
screen a session made is that session's alone, with memory scopes off too: no other login (another session, or the app
without one) listens as it, takes its commands, reports or sends as it, renames it or is answered its settings by a
listen or a rename, even once the session has ended, and no other login's new screens push it out to free its id. What
its Home shows is still listed with the screen (`list_screens`, `showing.home`): to every reader while memory scopes are
off, and with them on only to readers of that dashboard.

**Where its uploads land.** With memory scopes on, a viewer session's upload lands in its person's private scope, or in
Family when the session chooses it (`app_upload_document` `family`); the household view's lands where the documents
adapter's `owner` puts it, as before. With memory scopes off every upload lands at the `owner`, a session's included
(Family by default, which everyone reads then), unless it chooses Family: the person a session speaks for only records
who added it. With memory scopes on a session whose person is not in Family can't choose it (refused, `403`), and a
session's own upload is kept apart from the ones the household view adds to the same person's scope through the
`owner` setting, so the household view is never told whether that person has the same file. Only while `app.viewers`
is on does the engine list and take the choice, and say in its answer who reads the document (`private` or `shared`,
always `shared` with memory scopes off); with it off uploads are as they always were.

**Its corner cards.** With memory scopes on, the corner cards a session chooses on Home are its person's own: a Family
dashboard or one of theirs, or the stat card. Their logins see them; everyone else keeps the household's, which the
session also sees in any corner its person has not chosen. With scopes off every corner is the household's, as before.

**What it does not protect.**

- **The browser that holds a session.** Whoever uses it sees what that person sees until it signs out or lapses: don't
  open a session on a shared screen in a common room.
- **The code.** Whoever types it first gets the session. Give it to that person alone, and make a new one if it may
  have been seen.
- **Reaching the app.** Sessions are not a login against strangers: the network, Tailscale or an access proxy still
  decides who reaches the app at all.
- **Plain HTTP on a network.** Without HTTPS the cookie travels in the clear, and anyone on the way can copy it: open
  sessions over HTTPS (Tailscale, Cloudflare Access or your own proxy), or on this computer only.
- **A hosted front end** (`deploy/vercel/`) has no viewer sessions: everyone there gets the household view.
- **Documents added with memory scopes off.** They land at the documents adapter's `owner`, a session's too (above):
  with Family as the owner, everyone reads them, and they stay Family's when scopes are switched on later. The app's
  page for typing a code in, and its control for the Family choice, are not built yet.
- **A browser that changes login.** The app keeps one screen id per browser, and a screen is the login's that first
  listened with it, so a browser that opens a session where the app without one listened, or ends one, is refused as
  that screen and has no live screen until the app takes a new id for its new login, which it does not do yet. That is
  a condition for turning viewer sessions on.

**Ending a session.** The person signs out in the app; the operator runs `rhythm viewer revoke <name>` or
`rhythm viewer revoke --person <id>` (which also removes their unused codes), or `rhythm token revoke --all` after a
leak; `rhythm person suspend <id>` ends every session of theirs at once. The engine then refuses the credential with
`401`, whatever the app's server still holds, and the app clears the cookie. Replacing the app token's file ends every
session, at the engine too: each session's credential was derived from the old token. Setting `app.viewers: off` again
returns every browser to the household view at once (the engine refuses every session's credential while it is off;
one still within its 30 days works again if they are switched back on, so revoke them to end them for good),
and a session lasts 30 days at most, at the engine as in the browser, also when a sign-out could not reach the engine.
`rhythm viewer list` and `rhythm token list` show the enabled ones: `viewer list` first disables any past its 30 days,
and `token list` marks one past them `lapsed: refused`. While `app.viewers` is off every one listed is refused at the
door all the same, and `rhythm doctor` says how many are still enabled.

## Secret files

All secrets are files under `deploy/compose/secrets/` (adapters' under `secrets/adapters/`), passed to
containers as Docker secrets or a read-only folder and named by `*_FILE` variables. Never a plain
environment value, a `VITE_*` variable, a command argument, a log line, or a value in `rhythm.yaml` or
`.env`. The full list is in [configuration.md](configuration.md#secrets).

On Docker Desktop (Windows, macOS) keep the folder inside your user profile; file modes are not
enforced inside containers there. On a **Linux host** (including TrueNAS) the containers read the
files as their own users:

| User (uid) | Container | Reads |
|---|---|---|
| 0, then 999 | `postgres` | `postgres_password`, read as root at start, before the server drops to 999 |
| 1654 | `engine`, `migrate`, `cli` | `engine_db`, `app_token`, `postgres_password` (migrate), `secrets/adapters/*` |
| 1000 | `app` | `app_token` |
| 70 | `backup` | `postgres_password`, `backup_password` |
| 65532 | `cloudflared`, `openai-tunnel` | `adapters/cloudflare_tunnel_token` (cloudflared); `adapters/openai-tunnel_key`, `harness_openai-tunnel_token` (openai-tunnel) |
| 0 | `tailscale`, `caddy` | `proxy_users` (caddy) |

`rhythm init`, `rhythm connect` and `rhythm up` give each file the owner of the container that reads it
and no access for anyone else ([configuration.md](configuration.md#owners-and-modes), open question
Q-050); `secrets/` itself is yours, mode `0700`. A file mounted into a container is opened there
directly, without passing through the host folder. If you write the files without the `rhythm`
command, protect the **folder** instead: `secrets/` owned by root (or your admin user) with mode
`0700`, the files inside mode `0444`, and `secrets/adapters/` mode `0755`, so each container can read
what it is given (open question Q-061). `rhythm doctor` checks owners, modes and that no secret-shaped
value appears in config.

## A calendar's private link

The `calendar` adapter (off by default, [adapters/calendar.md](adapters/calendar.md)) reads a calendar over the private ICS link
you enter yourself. Whoever holds that link can read that calendar with no password, so it is kept as a password is:

- **A secret file**, `secrets/adapters/calendar_link`, outside the database and its backups. The engine refuses to start with
  one under `adapters.calendar` in `rhythm.yaml`, and `rhythm doctor` reports the file's link if it finds it anywhere in `rhythm.yaml`.
- **Never shown back or logged.** It is entered at a hidden prompt, and is in no star, answer or export. What the log says of a
  failed read is in the engine's own words, never the link or the server's address, and a feed repeating its own link has it
  taken out.
- **Sent only as the address of one GET**, with no other credential. Over https, or plain http only on your own network; a
  redirect is followed only to https on a public name, and connects only to a public address, whatever that name resolves to at
  that moment, so a feed's server cannot point the engine into your own network.

Reset the address in your calendar if it may have leaked. An event's name and place are stored and reach your AI clients
through the map, its description reaches them through `dashboard_data`, read from the calendar when asked, and anyone who can
send you an invitation can write all three. They are data: tool descriptions tell models that source text is untrusted
(`dashboard_data`'s says so while this adapter is on), and hidden characters, the Unicode tag characters among them, are taken
out, but they do not pass through the holds a memory's words do.

## What never to expose

- **Postgres.** Never publish its port or attach it to a network with other machines.
- **The app on a public address without a login in front.** It has none of its own. Use Cloudflare
  Access, a proxy login or Tailscale; on a hosted front end, the host's own login, and no share or
  bypass links.
- **`RHYTHM_BIND=0.0.0.0` on a computer with a public address** (a rented server, a host in a DMZ).
- **Any engine path but `/mcp`** through a proxy or tunnel, and **a proxy login on `/mcp`** (it breaks
  the token instead of adding to it). The one exception is `/app/documents/`, which the Vercel recipe lets through beside
  `/mcp` when a hosted front end should show documents' original files ([deploy/vercel/](../deploy/vercel/README.md),
  open question Q-129): it answers the app token alone, so route it only for such a front end, and never `/health` or
  any other path.
- **Tailscale Funnel** for the app (it makes a tailnet name public).
- **An OpenAI admin key**, or one with all permissions, in the tunnel client, and **the OpenAI tunnel**
  associated with a workspace or organization beyond the household.
- **The secrets folder, `.env` and `rhythm.yaml`** in a synced folder, a shared drive, a repository or
  a support message. **Token files** in chats, tickets or screenshots.
- **The backup password and the backup staging folder.** The restic repository is encrypted; the
  staging folder holds a plain copy of the database while a backup or a restore runs, and a backup that failed
  leaves its copy there for you to look at and then delete (open question Q-064, [backups.md](backups.md)).

## Not a medical device

Rhythm of Life shows readings from your own devices and services for information only. Health tools
tell AI clients the same: no diagnosis, no treatment decisions, no changes to medication or insulin
settings.

## Studio drafts and selections

Studio drafts are owned by their authenticated harness principal and bound to one screen login. Current source visibility is checked on reads and again on publication, including both publisher and owner. Preview writes are always refused, independently of the saved dashboard's Allow edits setting. Cancellation is a terminal server fence; there is no implicit ownership takeover. Replayed requests cannot publish twice, and a changed saved version or layout causes a conflict.

Typed dashboard selections are UI state, carried over the existing screen-command and sandbox data channels. The allowed kinds are enumerated tabs/filters, bounded integer week offsets and ISO date ranges. MCP supplies no JavaScript, DOM selector, URL or function to execute. The sandbox remains sealed; its page receives host selection data and acknowledges a successful draw. Existing declared source writes retain their original Allow edits, history and Undo policy.
