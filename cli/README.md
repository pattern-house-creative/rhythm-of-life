# cli

The `rhythm` command. Installing, upgrading and running Rhythm of Life need only Docker (Compose 2.20 or later):
everything else runs inside the engine image.

| Piece | Where |
|---|---|
| `rhythm` (POSIX sh: Linux, macOS, Git Bash) and `rhythm.ps1` (PowerShell, Windows) | the repository root: small host wrappers |
| the in-container commands (`init`, `connect`, `doctor`, ...) | `engine/src/Cli/`, built into the engine image |
| the `cli` and `restore` compose services | `deploy/compose/engine.compose.yaml` |
| the end-to-end tests | `cli/e2e.sh`, `cli/lifecycle.sh` (a fresh install's memory lifecycle), `cli/journeys.sh` (a person's journeys in the real app, `app/tests/journeys/`) and `cli/chaos.sh` (restores that go wrong) |

```sh
./rhythm init      # asks a few questions, writes deploy/compose/rhythm.yaml, .env and secrets/
./rhythm up        # starts the stack and prints the app address, the MCP address and the token file
```

On Windows PowerShell use `.\rhythm.ps1 init` and `.\rhythm.ps1 up`; every command below works the same way.

## Commands

| Command | Runs | Does |
|---|---|---|
| `init` | in the engine image | the questions of a new install (below), then `rhythm.yaml`, `.env` and every secret |
| `up` | host | `preflight`, `docker compose up -d --wait`, then `ready`: prints the app URL, the MCP URL and the **path** of the harness token file, never a token. The first `up` makes a harness token named `assistant` |
| `down [-v]` | host | stops the stack. `-v` also deletes its volumes: the database and the archive, for good. It names the project it removes first, and is refused in a checkout that names none (no `.env`, or no `COMPOSE_PROJECT_NAME` in it or in the environment), where Compose would take the default project, `rhythm`, whichever install that is |
| `logs [-f] [<service>]` | host | the services' logs |
| `connect [<adapter>]` | in the engine image | with no name, lists the adapters this build describes; with one, asks its settings and secrets and turns it on (below) |
| `disconnect <adapter>` | in the engine image | turns it off; its secret files stay |
| `doctor` | in the engine image | checks settings, secret files and modes, secret-shaped values, enabled adapters' secrets and, when the stack is up, the engine, MCP, the app and the memory scope reports ([below](#doctor)). Exit 1 when something failed |
| `token create <name>` | in the engine image | a harness token in `secrets/harness_<name>_token` (`--person <id>`: it speaks for that person, [below](#tokens-and-people); `--out <file>` elsewhere; `--role ingest --plugin <name>` for a plugin, in `secrets/plugin_<name>_token`) |
| `token list`, `token revoke <name>`, `token revoke --all` | in the engine image | the enabled tokens and whom each speaks for; disable one, or every token at once after a leak |
| `capture connect <name> [--person <id>] [--out <file>]`, `capture list`, `capture disconnect <name>` | in the engine image | with `memory.capture` on beside `memory.tools`: a token for `POST /capture`, the connections, and removing one, its mirrors moved to the trash ([below](#capture-connections)) |
| `person list`, `person suspend <id>`, `person unsuspend <id>` | in the engine image | the people the install knows; suspend one, which disables their tokens at once, or lift it |
| `person offboard <id>` | in the engine image | someone leaves: the access bound to them ends at once and their private memories go to the trash, after a preview and the id typed ([below](#offboarding-a-person)) |
| `family add <id>`, `family remove <id>`, `family admin <id>`, `family unadmin <id>` | in the engine image | who reads Family's records once memory scopes are on, and who may change Family records others wrote ([below](#family-and-source-shares)) |
| `share list`, `share add <person> <provider>`, `share revoke <person> <provider>` | in the engine image | one person's records from one source that Family reads once memory scopes are on ([below](#family-and-source-shares)) |
| `scopes off` | in the engine image | switch memory scopes off on purpose, after a preview of what everyone would read ([below](#switching-memory-scopes-off)) |
| `viewer invite <person> [--minutes <1-60>]`, `viewer list`, `viewer revoke <name>`, `viewer revoke --person <id>` | in the engine image | viewer sessions in the app, only while `app.viewers` is on, which this version refuses ([below](#viewer-sessions)) |
| `export [--person <id>] [--out <file>]` | in the engine image | native memories, as that person (or, without `--person`, the household view) reads them, into a file in Rhythm's own format ([below](#export-and-import)) |
| `import <file> [--person <id>] [--family] [--dry-run]` | in the engine image | such a file back in: only into the scopes it names, only what that reader may write, never over anything that is here ([below](#export-and-import)) |
| `import-second-brain <file> --person <id> [--scope-map <file>] [--write]` | in the engine image | with `memory.secondBrainImport` on: a Second Brain service's own export, for that person: what it would import, hold and skip, per scope and why, and with `--write` the import itself, never over anything that is here ([below](#importing-a-second-brain-export)) |
| `remember --person <id> [--family] [...] <text>` | in the engine image | a memory, as that person: theirs unless `--family`, through the same checks as their AI client's `remember` ([below](#memories-from-the-terminal)) |
| `recall --person <id> [--top <n>] [--json] <question>` | in the engine image | what that person reads about a question, words and meaning together, as the `recall` tool ranks it ([below](#memories-from-the-terminal)) |
| `list --person <id> [--family\|--private] [...]` | in the engine image | that person's memories and Family's, newest first, as `list_recent` lists them ([below](#memories-from-the-terminal)) |
| `release --person <id> [<memory> [--revision <n>]]` | in the engine image | with `memory.tools` on: what that person reads that is held for review, one held memory whole, or its release at the revision read; a person's step no AI client takes ([below](#a-persons-own-steps)) |
| `capsule --person <id> <memory> core <slot> \| project <slot> \| none` | in the engine image | with `memory.tools` on: that person puts a memory into a capsule slot, or takes it out ([below](#a-persons-own-steps)) |
| `delete-forever --person <id> [<memory>]` | in the engine image | with `memory.tools` on: that person's trash, or one memory in it deleted for good once they type its id ([below](#a-persons-own-steps)) |
| `capsule --person <id> [--family] [--project <slug>]` | in the engine image | with `memory.tools` on: a capsule as that person's AI clients are served it, with what it leaves out and what waits in its slots ([below](#a-persons-own-steps)) |
| `forget --person <id> <memory>`, `set-status --person <id> <memory> canonical\|draft\|deprecated` | in the engine image | with `memory.tools` on: that person puts a memory into the trash, or confirms it, makes it a draft or marks it wrong ([below](#a-persons-own-steps)) |
| `link --person <id> <memory> [<other> [--type <type>]]`, `unlink --person <id> <memory> <other> [--type <type>]` | in the engine image | with `memory.tools` on: a memory's links and the map's suggestions, a person's link from it to another memory of its scope, or the links between two removed and the suggestion between them dismissed ([below](#a-persons-own-steps)) |
| `still-true --person <id> [<memory> [--revision <n>]]`, `clear-flags --person <id> <memory>` | in the engine image | with `memory.tools` on: what that person may change that may be out of date, one said still true without changing it, or its flags for a check taken off once checked ([below](#a-persons-own-steps)) |
| `queues --person <id>`, `duplicates --person <id> [<memory>]`, `contradictions --person <id> [<memory> <other>]`, `insight-candidates --person <id>`, `insights --person <id> [<insight> [reopen]]`, `resolve --person <id> <item> <action>` | in the engine image | with `memory.tools` on: what waits on that person, each review queue, and a person's step in one, once ([below](#a-persons-review-queues)) |
| `undismiss --person <id> [<dismissal>]` | in the engine image | with `memory.tools` on: the suggestions dismissed on that person's map, or one taken back ([below](#a-persons-own-steps)) |
| `share-memory --person <id> <memory> [--private]` | in the engine image | with `memory.share` on beside `memory.tools`, and memory scopes on: that person shares a memory with Family, or takes a Family one back to its author's private scope ([below](#a-persons-own-steps)) |
| `night [--night <YYYY-MM-DD>]` | in the engine image | with `engine.nightSummary` on: what the engine's daily jobs did, a line a job, the latest night kept or the one named ([below](#what-the-daily-jobs-did)) |
| `backup` | host | an encrypted backup now (database dump and archive into restic, checked). The first one creates the repository in the data folder |
| `snapshots` | host | the backups in the repository |
| `restore <snapshot>` | host | puts a backup back: see [Restore](#restore) |
| `upgrade [<tag>] [--pull]` | host | see [Upgrade](#upgrade) |
| `check-config`, `refresh`, `migrate` | in the engine image | the engine's own operator commands (`docs/dev/engine.md`) |
| `help` | both | the list |

"In the engine image" means the wrapper runs `docker compose --profile cli run --rm cli <command>` in
`deploy/compose` (so that folder's `.env`, including any `COMPOSE_FILE`, applies), which you can also type
yourself. The `cli` service works in `deploy/compose` (mounted at `/work`), reads secrets straight from
`secrets/`, and runs as root so that every file it writes gets the owner of the service that reads it
(`docs/configuration.md`, open question Q-050).

`init` records the Compose project name (`COMPOSE_PROJECT_NAME`) and the image tag (`RHYTHM_VERSION`) in
`deploy/compose/.env`, so every later command in this checkout acts on this install's own containers, networks,
volumes and images, with nothing to export. Any `.env` name can still be overridden from the environment for one
command.

## init

Every question has a flag, so scripts and tests use `--no-input`:

| Question | Flag | Default |
|---|---|---|
| name of this install | `--name` | `Rhythm of Life` |
| who uses it (comma-separated names; `id=Name` to choose an id) | `--people` | none, required |
| time zone (IANA) | `--timezone` | `UTC` |
| folder for notes, imports and backups | `--data` | `./data` (inside `deploy/compose`) |
| how it is reached: `local` (this computer only), `lan`, `tailscale`, `cloudflare`, `proxy` | `--exposure` | `local` |
| the host name others use (all but `local`) | `--host` | none |
| the host name for `/mcp` alone (`cloudflare` only) | `--mcp-host` | the app's host name with `-mcp` on its first label (`rhythm-mcp.example.invalid`) |
| fictional demo data, labelled DEMO | `--demo yes\|no` | yes |
| a folder of Markdown notes (turns the `notes` adapter on) | `--notes` | none |
| the Docker Compose project name (a second install on this computer needs its own) | `--project-name` | `rhythm` |
| published ports (not asked) | `--app-port`, `--mcp-port` | `8080`, `8181` |

```sh
./rhythm init --no-input --people "Alex, Sam" --timezone Europe/London
```

It writes:

- `rhythm.yaml`, from `rhythm.example.yaml` with your answers, comments kept;
- `.env`: `COMPOSE_PROJECT_NAME` (your answer), `RHYTHM_VERSION` (the tag `init` itself runs, `dev` unless you
  exported another), `RHYTHM_CONFIG_FILE=./rhythm.yaml`, the bind address (`127.0.0.1`, or `0.0.0.0` for `lan`), the
  ports and the data folders;
- `secrets/postgres_password`, `engine_db`, `app_token` and `backup_password`: 32 random bytes each (base64url),
  printed nowhere. Keep a copy of `backup_password` offline: backups cannot be read without it.

It runs once: a second `init` stops. `init --force` writes new settings and `.env` and keeps every secret that
exists (the database already uses those passwords). If a `docker compose up` ran before `init`, Docker left an
empty folder where each missing secret file should be; `init` replaces them.

With `lan` anyone on your network can open the app: there is no login. `tailscale`, `cloudflare` and `proxy`
keep the ports on `127.0.0.1` and assume your tunnel or proxy serves `https://<host>` (the app) and
`https://<host>/mcp` (the engine); `deploy/exposure/README.md` has the recipes. `cloudflare` puts a login in front
of the app's host name, which no AI client can pass, so there `/mcp` has a host name of its own (`--mcp-host`):
`init` writes it as `engine.publicMcpUrl`, `adapters.cloudflare.mcpHostname` and the name in `engine.allowedHosts`. Change `app.publicUrl` and
`engine.publicMcpUrl` in `rhythm.yaml` if yours differ.

### Two installs on one computer

Each install is its own checkout with its own `deploy/compose` folder. Give the second one another project name and
other ports when you set it up, and every command in that checkout then acts on it alone:

```sh
git clone <this repository> rhythm-test && cd rhythm-test
./rhythm init --project-name rhythm-test --app-port 8090 --mcp-port 8191
./rhythm up        # containers rhythm-test-*, networks rhythm-test_internal and rhythm-test_egress, its own volumes
```

Pass `--project-name` on the command line (rather than at the prompt) so that `init`'s own container already runs in
the new project. An external plugin for that install joins `rhythm-test_internal` and `rhythm-test_egress`. Both
installs use the images tagged `RHYTHM_VERSION`; when the checkouts differ, give each its own tag once, for
`init` (`RHYTHM_VERSION=test ./rhythm init ...`): it is recorded, and `up` builds the images under it.

A first `init` in a checkout that names no project is refused while a Compose project named `rhythm` already has
volumes on the computer: it would start the new install on the other one's data. Name the project, as above; to set a
checkout up on the existing data on purpose, say so with `--project-name rhythm`. `scripts/rhythm-down.test.mjs`
checks this, and `down -v`, for both wrappers.

## connect

`rhythm connect <adapter>` follows the adapter's descriptor (`contracts/adapters/<name>.json`):

1. shows what to set up outside Rhythm first (the descriptor's `docs`);
2. asks each setting, defaulting to the current value (`--set key=value` answers one without a prompt);
3. asks each `paste` secret with hidden input (an optional one may be left empty) and makes each `generate` one; an
   existing secret is kept unless you say otherwise;
4. runs the adapter's own connect step when the descriptor says `connect: true` (for example a browser consent);
5. only then writes `adapters.<name>` with `enabled: true` to `rhythm.yaml`, the secrets to
   `secrets/adapters/<name>_<purpose>` (mode 0600, owned by the engine's user), and the adapter's profile, if it
   has one, to `COMPOSE_PROFILES` in `.env`.

When the stack is running, the wrapper then recreates the engine (it reads its settings at start) and the app (it
asks the engine which tools it offers at start, so its tiles follow at once), and starts any new adapter container.
Secrets are never accepted as arguments; piped input works for scripts. An adapter whose container calls `/mcp` with
a token of its own (the OpenAI tunnel) needs that token first, `token create <name>`, which is then owned by that
container's user; `connect` says so and changes nothing until it exists.

## doctor

Read only. It checks:

- `rhythm.yaml` is valid, and `.env` points the containers at it;
- each secret file exists, is a file (not a folder Docker made), has the right shape, has no access for others and,
  where the host keeps owners, belongs to the user of the service that reads it (`./rhythm up` fixes owners and
  modes);
- no secret-shaped value in `rhythm.yaml` or `.env`: a secret file's value, a key that names a secret, or a long
  random-looking string;
- every enabled adapter's required secrets, and its profile in `COMPOSE_PROFILES`;
- that local addresses in `engine.publicMcpUrl` and `app.publicUrl` use the ports `.env` publishes;
- when the stack is up: the engine's `/health`, with the engine's version and schema and a line per model (its state
  and, for the embeddings model, its index: passages and rows left), MCP answering 401 without a token, and the app's
  `/api/health` ([../docs/models.md](../docs/models.md));
- then, from the database, the memory scope reports ([../docs/dev/memory.md](../docs/dev/memory.md) section 6.4): the
  mode the database records against `memory.scopes` in `rhythm.yaml` (a mismatch fails, since the engine will not
  start on it), each token bound to a person who is suspended or no longer in `people`, each source share, and with
  scopes off a preview of switching them on: what each token would stop reading, as counts per person and provider,
  never a title, and by id and title each Family dashboard whose log would take it off the household view at the
  switch. The preview runs the switch in a transaction it rolls back, so nothing changes. A viewer session the door
  refuses (past its 30 days, or any while `app.viewers` is off) is named or counted, and left out of the preview.

Notes (`note`) never fail the check. One also names an adapter that keeps a person's own records under the household
(the Second Brain personal workspace with `owner: household`); others name a model that is not in the state its
settings ask for (still loading, not downloaded, a titles model failing, a paused index job), since every model is
optional.

## Tokens and people

```sh
./rhythm token create alex-phone --person alex   # Alex's AI client; secrets/harness_alex-phone_token
./rhythm token list                              # alex-phone  harness  alex  2026-11-01
./rhythm person suspend alex                     # every token bound to Alex stops working at once
./rhythm token revoke --all                      # after a leak: every harness, plugin and capture token, at once
```

- `--person <id>` binds the token to one person in `people`, for good: to move a client to another person, revoke
  its token and make a new one. A token made without it has the **household view**. With memory scopes off, every
  install's setting today, a binding changes nothing anyone reads; it records who wrote what, the token is refused
  while its person is suspended or no longer in `people` (below), and binding tokens before scopes are switched on is
  the safe order ([../docs/dev/memory.md](../docs/dev/memory.md) section 3).
- A token bound to a suspended person, or to one taken out of `people`, is refused (401) whatever else is true of it;
  `token list` marks it. Suspending disables their tokens for good: after `person unsuspend`, make new ones. So does
  taking someone out of `people`, at the next `./rhythm up`: listed again, they need new tokens too. Lifting the
  suspension of someone the engine's last start did not find in `people` leaves them former, not current.
- `token revoke --all` disables every harness, plugin and capture token and every viewer credential; nothing turns
  them on again. It leaves the app's own token, which the engine takes from `secrets/app_token`: if that leaked too, replace
  the file with a new random token (32 to 512 characters) and `./rhythm up`. Delete the old token files before making
  new ones with the same names, since `token create` and `capture connect` never overwrite a file.
- `token` and `person` do not read `rhythm.yaml`, so they work while it is broken. They need the database: run them
  with the stack up.

## Offboarding a person

```sh
./rhythm person offboard sam   # what it does and leaves, as counts; type sam to go ahead
./rhythm person list           # sam  suspended  not in Family  0 token(s)  offboarding since 2026-11-08
```

When someone leaves the household for good. It says first what it does, what it leaves and what it does not end, as
counts and token names, and changes nothing unless you type their id ([../docs/dev/memory.md](../docs/dev/memory.md)
section 4):

- **At once** nothing bound to them reads or writes: suspended (someone already taken out of `people` stays former),
  every token and session bound to them disabled for good (their terminal login and a capture connection made for them
  included), out of Family with any admin role, their source shares and any unused one-time viewer code ended.
- **What it does not end**, since nothing ties it to them, and the preview lists last: every token bound to no person
  (the household's harness tokens, `assistant`, the first one `up` makes, among them; plugins' and capture tokens), which
  stays enabled, so if they used one, `./rhythm token revoke <name>` and make whoever else used it a new one; the app,
  which has no login of its own, so whoever reaches it reads what the household reads: keep them off the network it
  listens on; and an adapter or plugin reading their own account, which keeps adding records until you disconnect it
  (`./rhythm disconnect <adapter>`; for a plugin its own settings, or `./rhythm token revoke plugin:<name>`, which stops
  it for everyone). Give an adapter whose `owner` is them another owner before you take them out of `people`, or
  `./rhythm up` stops on it.
- **Their private memories go to the trash**, as `forget` puts one there: nothing is deleted then. The trash keeps them 14
  days, then the daily purge deletes them for good, and the daily jobs then delete their emptied private projects and
  record the offboarding finished. Brought back in that time (`person unsuspend`, or listed in `people` again and
  `./rhythm up` for someone already former), they restore what they want with a new token, and the offboarding waits:
  while they are current, and still once they are suspended or taken out of `people` again, until you run
  `person offboard` again, which shows what it does and asks for their id again. Brought back, they stay out of Family
  and the source shares it ended stay ended until `family add <id>` and `share add <id> <provider>`: neither `person
  unsuspend` nor the first start with memory scopes on puts them back, and `person unsuspend` says so.
- **It stops and goes on.** The memories move in batches, each its own transaction; a run stopped part way has ended
  their access already, and running it again goes on from where it stopped, asking nothing (unless they were brought
  back meanwhile).
- **What it leaves:** the Family memories they wrote, in Family with them as their author; their own records from
  adapters and plugins, kept as their source keeps them (with memory scopes on nobody reads them while they are
  suspended or former); what they saved in Rhythm itself (`save_document` copies, uploads, dashboard log entries) and
  their private insights and dashboards, which nobody reads with scopes on either; the recall log's rows of their
  questions, if `engine.recallLog` was on, until its daily prune takes each after 30 days; both audit trails, which
  hold no words. The preview counts each. Take them out of `people` in `rhythm.yaml` too and `./rhythm up`.
- **With memory scopes off**, every install's setting today, the household shares everything and nothing is private
  to anyone, so it ends the access bound to them and moves nothing to the trash: the memories they wrote stay, read by
  everyone, with them as their author. The first start with scopes on leaves them out of Family, brought back or not
  (`family add` brings them in).

It is the operator's step on the host, never an AI client's: no tool or route reaches it.

## Family and source shares

With memory scopes on (off on every install until its operator sets `memory.scopes: on`:
[../docs/dev/memory.md](../docs/dev/memory.md) section 6), a token or session bound to a person reads that person's own
records, DEMO, and Family's records only while the person is in Family; one with no person reads Family and DEMO. Only
the operator records who is in Family, who is an admin and which sources are shared, with these commands; nothing over
MCP or in the app does, and nothing joins or shares on its own.

```sh
./rhythm family admin alex           # Alex may change Family records others wrote (and is in Family)
./rhythm family add sam              # a person listed in people later joins Family only like this
./rhythm family remove sam           # Sam reads Family no more, at once; his own records stay his
./rhythm share add alex nightscout   # Family reads Alex's Nightscout records, as a shared screen showed them
./rhythm share list                  # alex  nightscout  operator  2026-11-01
./rhythm share revoke alex nightscout
```

- Only someone the engine's last start found in `people` joins Family, becomes an admin or has a source shared (a
  suspended person included, who reads nothing until `person unsuspend`). Taking someone out of `people` takes them
  out of Family and ends their shares at the next start.
- An admin is a member with the admin role: `family admin` adds them to Family when they are not in it, `family
  unadmin` leaves them a member. An admin never reads anyone's private records. Each command says when Family is left
  without an admin, and `doctor` warns about it while scopes are on.
- `family remove` is refused while scopes are off and have never been on: everyone reads Family then, and the first
  start with scopes on adds everyone in `people` to Family, so it would be undone. Take someone out once scopes are on.
  Only that first start adds anyone: switching on again after `scopes off` leaves Family as it was, so after it
  `family remove` works with scopes off too, and counts once they are on again.
- A source share is one adapter's or plugin's provider of one person (`nightscout`, `garmin`, `csv`, a plugin's), never
  `memory`, `documents`, `household-documents`, `log` or the map's catalog: those hold records shared one at a time,
  if at all. Revoking deletes the share; sharing again records a new one. The person can also share their own sources
  through their own client once that exists; an admin never shares someone else's.
- Like `token` and `person`, these do not read `rhythm.yaml`. Before switching scopes on, record the shares the
  household should keep seeing and name at least one admin; `doctor` previews what each token would stop reading.
- The engine's admin audit records, as the operator's, each change these commands make and each token, viewer
  session, suspension and switch that `token`, `person`, `viewer revoke` and `scopes off` change: one row per change
  (whom it is about, what changed, when: ids and names, never a token or a code). A command that changes nothing
  records nothing, and a viewer invite's one-time code is not recorded, only the session it opens. The engine's start records what it changes as its own
  (someone taken out of `people`, the switch's first start). The trail is never changed or removed; nothing shows it
  yet ([docs/dev/memory.md](../docs/dev/memory.md) section 4).

## Viewer sessions

Off unless `rhythm.yaml` says `app.viewers: on`, which this version refuses (`../docs/security.md`, Viewer sessions).
With them on, someone at the app can see what one person sees by typing a code the operator made for that person:

```sh
./rhythm viewer invite alex                # prints a code such as 7K3M-Q9TD-2HXA: once, within 15 minutes
./rhythm viewer invite alex --minutes 60   # at most an hour
./rhythm viewer list                       # viewer-alex-1a2b3c4d  alex  2026-11-01, and codes still waiting
./rhythm viewer revoke viewer-alex-1a2b3c4d
./rhythm viewer revoke --person alex       # every session of Alex's and their unused codes
```

- A code opens one session in one browser, once, and is shown only here; the database keeps its SHA-256. Give it to
  that person alone: whoever types it in reads what they read.
- `invite` reads `rhythm.yaml` and refuses while `app.viewers` is off; `list` and `revoke` do not, so a broken settings
  file never blocks ending a session. `token list` shows sessions too (one past its 30 days marked `lapsed: refused`),
  `token revoke <name>` and `token revoke --all`
  end them, and `person suspend` ends every session of that person's at once, as it disables their tokens.
- A session lasts 30 days at most, in its browser and at the engine, until the person signs out, or until it is
  revoked; `list` stops listing it then. Replacing the app token's file ends every session, and so does setting
  `app.viewers: off` (the engine then admits no session's credential; revoke them too to end them for good).

## Switching memory scopes off

```sh
# first set memory.scopes: off in rhythm.yaml, then:
./rhythm scopes off                  # what everyone would read, as counts; type off to confirm
./rhythm up
```

Switching off widens what everyone reads, so it is done only on purpose, here. `scopes off` is refused unless
`rhythm.yaml` loads and says `memory.scopes: off` (else the next start would switch scopes on again), unless `.env`
sets `RHYTHM_CONFIG_FILE=./rhythm.yaml` (the file the engine starts from is then the one checked), and while Rhythm
holds anything private of its own: a private memory (in the trash too), a memory now in Family with a private earlier
version, or a private memory project. Otherwise it prints, per person, their current records by provider (marking
those Family reads already through a source share) and their own dashboards, insights, insight runs, insight feedback and reviews and pins, and how
many of Family's records the people not in Family would read, never a title, and records off only once you type
`off`. Family's members and admins and the source shares are kept, for if scopes are switched on again; switching on
again adds nobody to Family. A database that records no mode at all, which the engine refuses to start
on, is put back to off the same way.

## Export and import

Native memories in Rhythm's own format, a JSON file of one record a line ([../docs/dev/memory.md](../docs/dev/memory.md)
section 9). A backup is the whole install, for the operator; an export is what one reader reads, to move it to another
install or to keep.

```sh
./rhythm export --person alex                     # deploy/compose/data/exports/rhythm-memories-alex-<time>.json
./rhythm export --out data/exports/family.json    # the household view: Family's records only, with memory scopes on
./rhythm import data/exports/alex.json --person alex --family --dry-run   # what it would add; writes nothing
./rhythm import data/exports/alex.json --person alex --family             # Alex's private records and Family's
```

What the file carries, of what its reader reads: each memory with its id, scope, author, source, text, tags, kind,
status, importance, volatility, validity, project, capsule slot, hold, revision and dates, its earlier versions as its
reader's history shows them, its trash dates when it is in the trash, and for a digest the project or tag it sums up,
with the list it was drawn from (as a hash) and how many, so it is still that project's or tag's digest after an
import, and for a commitment (an open loop owed to or by the person) its direction and the other person's name as a
slug, and for a decision how sure the person was and its outcomes and review dates from its reader's history on; each project with its id, slug, name,
description, status, author and aliases, and for a project a memory held for review made, its hold, so an import keeps
it out of every list there too until a person releases a memory in it; and each person- or AI-made link between two of
its memories. What it never
carries: another reader's records (with memory scopes on, a person's export holds their own private records and, as a
member, Family's, never another person's private ones, nor the private states a Family memory had before it came into
Family; the household view's holds Family's only), the audit trail, the logins that wrote anything, idempotency keys,
passages and vectors, the map's stars and the links the engine finds (rebuilt here), and the other records of the
catalog (documents, notes, health data: a backup holds those). With memory scopes off every memory is the household's,
so every reader's export holds every memory.

- `export` reads as a client bound to that person would, through the same read rule as every other read (a principal
  made for it inside its own transaction, which is rolled back, so no token for it ever exists). The file is written
  once, never over a file that is there, private to the deploy folder's owner, and only once it is whole. It holds the
  memories' words: keep it as private as a backup, and delete it once it is imported. `--out` names a file inside the
  deploy folder (a path relative to it, such as `data/exports/alex.json`): the command runs in a container that sees
  only that folder and is removed when it ends, so a path outside it is refused rather than written and lost.
- `import` names whom it writes for and where: `--person <id>` that person's private scope, `--family` Family (as
  that person, or as the household view without `--person`). It adds only what that reader may write there: never
  another person's private records; no private record while memory scopes are off; and a Family record a person wrote
  only for that person or a Family admin, as Family's author rule says. Each record keeps its id, scope, author, dates
  and revision; earlier versions come in from where its reader's history starts. Records of scopes it does not name are
  left out, and the report says how many.
- It never overwrites, moves or deletes anything here: a memory, project or link whose id is here already stays as it
  is, and so does a memory this install deleted for good or one imported before (the origin map, which keeps where each
  imported record came from). Importing the same file again adds nothing. A project of the same name in that scope here
  takes the memories that name it, and stays as it is. A commitment comes in only with the memory this import adds,
  never onto one already here; one whose other person's name reads like instructions to an AI client comes in with its
  direction and no name. The report counts commitments when the file holds one. A decision likewise comes in only
  with the memory this import adds, its outcomes at the revisions the import keeps; the report counts decisions when
  the file holds one.
- Every memory is screened as any write is: one held for review stays held, and one a person released elsewhere is held
  again here when its words read like instructions to an AI client, and so is each of its earlier versions, so an undo
  here never brings those words back unheld (a release counts only on the install where it was made). A person releases
  a held memory in the app's Memories view or with `rhythm release`, with `memory.tools` on; until then it stays
  held. A memory past its time in the trash is left out; one in the trash keeps its dates. Each memory and
  project it adds has one audit row, as the operator's import; the origin's audit trail and its logins are not this
  install's.
- `--dry-run` does all of it and writes nothing. Each command prints counts, never a record's title or text, and like
  `family` neither reads `rhythm.yaml`. `cli/e2e.sh` imports two synthetic exports and exports them back.

With `memory.tools` on, what an import added is then checked as a save is (the same words, or with the local model's files in the
folder `RHYTHM_MODEL_DIR` names, words close in meaning): a possible copy is flagged and a possible contradiction queued, each
for a person's review and naming a memory of its own scope, and the report says how many. It has at most 10 minutes for all of it and
says how many it did not reach. Nothing imported is changed or refused by this, and with `memory.tools` off nothing is checked or
printed. A release (`rhythm release`, below) is checked the same way.

## Importing a Second Brain export

With `memory.secondBrainImport: on` in `rhythm.yaml` (off by default; off, the command answers as one the engine does
not know), the operator can read one file a Second Brain service exported (its `GET /export`: version 3, and the older
files its own import still reads) and bring its memories, projects and links into Rhythm's native memories for one
person. It reads the file only, never the service, and writes only when asked:

```sh
./rhythm import-second-brain data/exports/second-brain-alex.json --person alex --scope-map data/exports/scope-map.json
                                                   # a dry run: what it would add, hold and skip, per scope; writes nothing
./rhythm import-second-brain data/exports/second-brain-alex.json --person alex --scope-map data/exports/scope-map.json --write
./rhythm import-second-brain data/exports/second-brain-alex.json --person alex --household
                                                   # scopes off: everything as the household's, each keeping its workspace
```

The export says neither which workspace a memory was in nor who wrote it, so:

- Every memory lands in that person's private scope, unless the owner's reviewed scope map names it for Family
  (`{"format": "rhythm-second-brain-scope-map", "version": 1, "family": {"memories": [<ids>], "projects": [<slugs>]}}`,
  the memories by their Second Brain ids). With memory scopes off nothing private comes in: only what the map names for
  Family. The importing person is every memory's author, so with memory scopes on a Family memory someone else wrote in
  Second Brain is theirs to change here only as a Family admin; the report counts the Family memories it adds (the
  file does not say which of them someone else wrote).
- `--household`, only with memory scopes off: every memory and project comes in as the household's, none refused, the
  person named its author, and each memory keeps where it lived in Second Brain (the login's personal workspace, a
  team workspace, or unknown), from the Second Brain connector's rows, else the scope map's Family list; the report
  counts them by it. With scopes on it is refused. Import while the connector is still on.
- Each memory, project and link gets a new id here; its Second Brain id goes into the origin map, so importing the same
  file again, or another member's file holding the same Family memories, adds nothing, and nothing is matched by title.
  A memory imported before is left as it is, whatever an older or newer file says of it: import the newest export. One
  another import put in a private scope this file does not mean (another member's, or one made without the map) is
  counted where this file meant it, never under the other scope's name, and listed for review; so is a link with such
  a memory at either end, which does not come in.
- A project lands where its memories do (or where the map names it). A slug the file holds twice (two workspaces'
  projects) is two projects, and a memory naming that slug goes into neither until the map names one by its slug and
  creation time; the report lists it. Two projects of one file are never joined by a name, an alias included. A
  project here held for review is joined by nothing, and a new one whose name reads like instructions to an AI client
  comes in held, with every memory that goes into it, until a person releases one. Aliases Rhythm does not take (the
  project's own slug, a reserved word, past 16) are left out and counted.
- A link comes in only when both its ends did and landed in one scope; one naming a memory the file does not hold
  (another person's private one) is skipped and counted. Second Brain's own links come in verified, its system's
  inferred and its automatic ones as suggestions, checked at the import, so the link pruning pass gives a weak one its
  seven days from there. A link has no maker here: the app and `rhythm link` call it imported, never a person's or an
  AI client's.
- Kind, status, volatility, capsule slot and project come from the tags that carry them. A memory held for review in
  Second Brain (`quarantine:` tags) stays held, and every memory is screened as any write is, so one Second Brain served
  is held here when its words read like instructions to an AI client; such a memory takes no capsule slot, its capsule
  tags kept beside it. A commitment's `owed-to-me` and `counterparty:` tags make it owed to the person, or owed by them
  to the one the tag names, as `list_loops` counts it. The tags of a standing instruction, a decision, a commitment and the like are kept beside the
  memory as data.
- Due dates, versions, the audit trail and the trash are not in the export, and the report says so each time; each
  memory's history starts at the import.

Each run prints, per scope, how many memories, projects and links it adds (or would), finds imported before, holds and
skips, with the reasons; what the file lacks and what becomes of each field; and, for the owner to review, the ids of
standing instructions and capsule slots it brings and any project slug it could not place. Its notes and review lines
are about what it adds: a record imported before, or refused, is only counted, so importing a file again notes nothing
of its records again. Every memory is screened as an AI client's write, more strictly than Second Brain's own import,
so a dry run may hold more than Second Brain did: read its held counts before `--write`. It never prints a record's
words. The file holds the memories' words: keep it as private as a backup, and delete it once it is imported. As with
`import`, a path is one inside the deploy folder. The migration runbook
(the migration runbook) says when a
real import may run: not before the owner approves it.

## Memories from the terminal

```sh
./rhythm remember --person alex "DEMO: the allotment key is under the blue pot. #allotment"
./rhythm remember --person alex --family --kind semantic "DEMO: the bins go out on Thursday."
echo "DEMO: a longer note, piped in." | ./rhythm remember --person alex -
./rhythm recall --person alex allotment key      # ranked, best first, with the start of each text
./rhythm list --person alex --private --limit 20 # newest first; --tag, --project, --kind, --status, --next
```

Each command speaks for one person (`--person`, a current person of `people`) and goes through the same engine functions
as that person's AI client: `remember` the memory tools' `remember`, `list` their `list_recent`, `recall` the `recall`
tool's combined retrieval, with the same read rule, author rule and holds
([../docs/dev/memory.md](../docs/dev/memory.md) section 5).

- A memory lands where that person's client would put it: with memory scopes on in their private scope unless
  `--family`; with them off it is the household's, which everyone in the household reads, and the answer says so. It is
  a draft, with the person as its author and `cli` as its source; `--tag` (again for more), `--project <slug>` (a new one
  is made in the memory's scope), `--kind episodic|semantic`, `--importance 1-5`, `--volatility durable|state|volatile`,
  and `--key <idempotency key>` (the same key and text store once, so a retry is safe). The terminal is a person's own
  channel, as the app is for a signed-in person: words that read like instructions to an AI client hold nothing here,
  since they are the person's own, but hidden characters (zero-width or direction characters, tag characters, an HTML
  comment, a long encoded run) and text over 32 KiB are held for review as on any channel, and the answer says so. A
  person releases a held memory in the app's Memories view or with `rhythm release` ([below](#a-persons-own-steps)),
  both there only with `memory.tools` on.
- The terminal has its own login for each person, which the first of these commands makes on the host, or the first of
  a person's own steps ([below](#a-persons-own-steps)) with `memory.tools` on (`rhythm token list` shows it as
  `terminal-<person>` with the role `terminal`; the admin audit records it made by the operator). It
  holds no token anyone could give an AI client, and the engine refuses it at `/mcp`. The database lets only this login
  write through the terminal's channel (`cli`), the one a person's own steps take, and refuses that channel to every AI
  client's token, one bound to that person included, so the audit trail tells the terminal from an AI client by the
  login, not by what a caller says. Suspending the person (`person suspend`), `token revoke --all` and a restore disable
  it with their tokens; the next command for a current person makes a new one.
- `recall` answers keyword results alone unless the engine image finds the local model in the folder its variable names
  (`RHYTHM_MODEL_DIR`); it never downloads one, and says when semantic search was skipped. It does not ask a live source
  (Drive) whether a record is current, so those records are left out here, and it says so. `--json` prints the `recall`
  tool's own answer, with why each result is there. Nothing is logged (`engine.recallLog` keeps AI clients' recalls).
- What they print of stored text is cut to one line and cleaned of control and formatting characters, so a memory's
  words never drive the terminal. Like `family`, `remember` and `list` do not read `rhythm.yaml`; `recall` does (the
  adapters, the passages it lists, the model), and needs it valid. All three need the stack's database: run them with
  the stack up. `cli/e2e.sh` remembers, lists and recalls a memory as Alex with memory scopes on, and checks that Sam
  does neither.

## A person's own steps

```sh
./rhythm release --person alex                     # what Alex reads that is held for review, never with its words
./rhythm release --person alex <memory>            # one held memory whole, every hidden character shown as its code
./rhythm release --person alex <memory> --revision 3   # released as read at revision 3; changed since, it stays held
./rhythm capsule --person alex <memory> core preferences   # into the core capsule of the memory's scope
./rhythm capsule --person alex <memory> project decisions  # into its project's capsule
./rhythm capsule --person alex <memory> none       # out of its slot
./rhythm delete-forever --person alex              # Alex's trash
./rhythm delete-forever --person alex <memory>     # shows it, then deletes it for good once Alex types its id
./rhythm capsule --person alex                     # Alex's own core capsule as her AI clients are served it
./rhythm capsule --person alex --family --project wendle-plot   # Family's capsule of a project
./rhythm forget --person alex <memory>             # into the trash, 14 days
./rhythm set-status --person alex <memory> canonical   # confirmed; draft and deprecated (marked wrong) too
./rhythm link --person alex <memory>               # its links, and what the map suggests between it and another memory
./rhythm link --person alex <memory> <other> --type caused_by   # a person's link, confirmed; relates_to by default
./rhythm unlink --person alex <memory> <other>      # the links between them removed, the map's suggestion dismissed
./rhythm still-true --person alex                  # what Alex may change that may be out of date
./rhythm still-true --person alex <memory> --revision 4   # still true as read at revision 4; nothing in it changes
./rhythm clear-flags --person alex <memory>        # its flags for a check off, once Alex has checked it
./rhythm undismiss --person alex                   # the suggestions dismissed on Alex's map
./rhythm undismiss --person alex <dismissal>       # one taken back, so the map may suggest it again
./rhythm share-memory --person alex <memory>       # with memory.share on: shared with Family
./rhythm share-memory --person alex <memory> --private   # taken back to its author's private scope
```

Only a person takes these steps, never an AI client: each goes through the person's own terminal login, the one login
the database lets write through the terminal's channel (`cli`), with the same author rule as the app's Memories view
([../docs/dev/memory.md](../docs/dev/memory.md) section 5). They are there only while `memory.tools` is on in
`rhythm.yaml`, as the Memories view is; with it off, absent or empty each answers as a command the engine does not know,
so they read `rhythm.yaml` (and need it valid), and `help` lists them only then.

- `release` without a memory lists the review queue that person reads, newest held first: why each is held, from which
  login and channel, its revision, and whether it is theirs to release, never its words, tags or project. With a memory
  it shows it whole for review, with every character no person sees (zero-width and direction characters, tag
  characters, control characters) written as its code, so its words never drive the terminal, and the command that
  releases it at the revision shown. Only `--revision` releases, and only if the memory has not changed since that
  revision; one that has stays held, and the answer says to read it again. `--family`, `--private`, `--reason` and
  `--limit`/`--next` narrow and page the queue.
- `capsule` puts a memory into a slot of its own scope's capsule (theirs, or Family's): `core` with `identity`,
  `preferences`, `constraints` or `principles`, or `project` with `current-state`, `decisions` or `open-questions` for a
  memory in a project (that project's capsule); `none` takes it out. Every AI client of that scope reads the capsule
  first, and it serves a slotted memory only while it is confirmed (canonical) and not held; the answer says when it
  waits for either, and a slot two memories fill is left out until one leaves it. `--revision` changes it only as of
  that revision.
- `delete-forever` without a memory lists the trash that person may restore, with the days left; with a memory in it,
  it shows it and asks for the memory's id to be typed, and deletes it for good only then: its text, earlier versions
  and links go and cannot come back, and only a line of its audit trail stays. A memory not in the trash is never
  deleted here: it is forgotten first (`rhythm forget`, the app's Memories view, or an AI client's `forget`), and the
  trash deletes it for good by itself once its 14 days are over.
- `capsule` without a memory shows one capsule as an AI client that reads it is served it (the same read as
  `get_prompt_capsule`): that person's own core capsule, Family's with `--family`, and a project's with `--project`;
  each slot with the memory that fills it and its words as served, every character no person sees written as its code;
  a slot two memories fill, one too long, and the memories in its slots that wait (a draft, one marked wrong, one past
  its validity, and one held for review, whose words are not shown here), each with the step that lets it in. It
  writes nothing. With `memory.capsules` off AI clients are served no capsule, and it says so.
- `forget` puts a memory into the trash as an AI client's `forget` or the app's would: nothing is deleted, its links
  wait with it, and it is deleted for good after 14 days unless someone brings it back (the app's trash) or a person
  deletes it sooner with `delete-forever`. `set-status` confirms a memory (`canonical`), makes it a draft again, or
  marks it wrong (`deprecated`). Both take `--revision`, and change nothing when the memory changed since. Marking a
  memory wrong or forgetting it names what that did to others: the memories it had replaced, current again, and those
  built on it, flagged for a check; marking it right again names those it replaces again and those no longer flagged.
- `link` with one memory lists its links, newest first, then what the map suggests between it and another memory,
  strongest first: each with its type and direction, who made it (that person, a person, or an AI client, whose link
  is its guess until a person confirms it; a link an import brought, which has no maker here, is "imported", confirmed
  or not yet), and whether it is that person's to unlink or dismiss; a memory held for
  review is listed without its words. With a second memory it makes a person's link from the first to the second
  (`--type`: `relates_to` by default, which has no direction, or `caused_by`, `decided`, `about_person`,
  `part_of_project`, `follows` or `drawn_from`), confirming an AI client's of that type if there is one. The rules an
  AI client's `link` keeps hold here too: two memories of one scope, the first one theirs to change, neither held, and
  a replacement only through a supersede. `unlink` removes the links between two memories, of one `--type` or of every
  type but a replacement's, where that person may, and dismisses the map's suggestion between them, kept through every
  rebuild.
- `still-true` without a memory lists what that person may change that may be out of date: a memory saved as a fact
  that changes often (`volatile`) 14 days after it last changed or was checked, a state 90 days after, and one built on
  a memory marked wrong since, longest unchecked first, with the revision each is at (`--family`, `--private`,
  `--limit`/`--next` narrow and page it). With a memory and `--revision` it says that memory still holds as read at
  that revision: nothing in it changes, it leaves the list until it is due again, and its flags for a check come off.
  A memory marked wrong, held for review or whose time has ended is refused. `clear-flags` takes only the flags off,
  once that person has checked it still stands.
- `undismiss` without a dismissal lists the suggestions dismissed between two stars on that person's map, newest first,
  whoever dismissed them (an AI client's `unlink` included), each with whether it is theirs to take back; with one, it
  takes it back, and the map may suggest it again from its next read. It reads the map as the engine last saved it and
  never rebuilds it.
- `share-memory` is there only with `memory.share` on beside `memory.tools` (`rhythm share` stays the operator's
  source share): it shares one of that person's private memories into Family, where every member reads it with its
  author from then on, or with `--private` takes a Family memory back to its author's private scope, which its author
  or a Family admin may do. With memory scopes off it is refused and nothing moves, since every memory is the
  household's already. A held memory is shared only once it is released; its project follows by slug, and it leaves
  its capsule slot; `--revision` moves it only as of that revision, and a repeat moves nothing.
- `cli/e2e.sh` runs these steps as Alex with memory scopes on: it confirms her memory and puts it into her own
  capsule, which serves it to her and never to Sam; holds one with hidden characters (three zero-width spaces and a
  direction control, which together weigh enough to hold it), shows it whole and releases it at the revision read;
  shares it with Family, which Sam lists, and takes it back, which Sam may not; then forgets it and deletes it for
  good once its id is typed.

## A person's review queues

With `memory.tools` on a person works what waits on them from the terminal as from the app's Memories view: through their own terminal login, which no AI
client holds. Off, absent or empty, each answers as a command the engine does not know, and help lists none of them.

```sh
./rhythm queues --person alex                      # how many wait in each queue, each the length of its list
./rhythm duplicates --person alex                  # flagged as possible copies, newest first
./rhythm duplicates --person alex <memory>         # one read whole, with what it is close to
./rhythm resolve --person alex <memory> keep_copy --revision 1 --copy <other>:2   # each memory it is close to, at the revision read
./rhythm contradictions --person alex              # pairs that may conflict, a person's to settle
./rhythm contradictions --person alex <memory> <other>   # both read whole, earlier first, with each step that works at those revisions
./rhythm resolve --person alex <memory> settle_keep_both --other <other> --revision 1 --other-revision 2
./rhythm resolve --person alex <later> settle_replace --other <earlier> --revision 1 --other-revision 2   # the later stands, the earlier's words kept
./rhythm insight-candidates --person alex          # pairs worth a look; --state used or rejected for what was decided
./rhythm resolve --person alex <pair> reject_candidate --note "says nothing new"
./rhythm resolve --person alex <pair> use_candidate [--insight <insight>]
./rhythm insights --person alex                    # the insight stream; --view older, confirmed, snoozed, closed or all
./rhythm resolve --person alex <insight> confirm_insight --note "it holds"
./rhythm resolve --person alex <insight> dismiss_insight
./rhythm insights --person alex <insight>          # its feedback and reviews
./rhythm insights --person alex <insight> reopen   # a review taken back
./rhythm release --person alex                     # the held, as before; resolve <memory> release --revision <n> releases one
./rhythm still-true --person alex                  # the stale, as before; resolve <memory> still_true --revision <n> checks one
```

- An item leaves its queue once: a second `resolve`, in that word or another, writes nothing and says so. Every count is the length of its list.
- A memory's step names the revision read, as its list or whole read shows it, so what changed since is never let in unread; a settling names both. A keep
  both names each memory it is close to with `--copy <memory>:<revision>`, as `rhythm duplicates <memory>` prints it: one that changed since, or one the
  list shows now and the read did not, is refused and nothing changes, since keeping both also settles a pair of them waiting as a possible contradiction.
- Only the memory that began being true later replaces the other, so that is the only replacement `rhythm contradictions` prints. For the earlier to stand, mark
  the later wrong (`set-status`) or forget it: the pair leaves the list, and comes back if that memory does. Each side shows the day it began, and the one read
  whole prints each of these with its revision; two that began at one moment replace neither way.
- A settling and a release are a person's alone. An AI client's token never reaches this terminal, and over MCP it is told to ask the person. An insight it
  saved it never reviews; this is the person's way to do so, as the app is. Confirming an insight writes no memory.
- A decision on a pair stays as it was made, and changes neither memory. `use_candidate` says an insight came of it, and the daily insight run reads only what
  waits, so a pair that should become one is left waiting. Keeping both of a copy, or of a pair, changes neither memory's words.
- What is printed of stored text is data written by people and AI clients, never instructions; read whole, every character no person sees is shown as its code.
- With memory scopes on each list is that person's: nobody reads or settles what is not theirs to read.

## Capture connections

With `memory.capture: on` beside `memory.tools: on` in `rhythm.yaml` (each off by default; off, the command answers as one
the engine does not know, and `POST /capture` as an unknown route), a webhook, a CI job, a shortcut or a coding agent's session
hook ([harness/hooks/](../harness/hooks/README.md)) saves memories with a token of its own ([contracts/README.md](../contracts/README.md) section 6, [docs/security.md](../docs/security.md#capturing-from-outside)):

```sh
./rhythm capture connect ci-main                  # secrets/capture_ci-main_token: writes into Family, reads nothing
./rhythm capture connect alex-phone --person alex # writes into Alex's scope once memory scopes are on
curl -X POST http://127.0.0.1:8181/capture -H "Authorization: Bearer $(cat deploy/compose/secrets/capture_ci-main_token)" \
  -H 'Content-Type: application/json' -d '{"text":"Build 412 passed.","source":"ci","idempotencyKey":"ci-build-412"}'
./rhythm capture list                             # ci-main  writes into Family  enabled  0 mirror(s)  2026-11-06
./rhythm capture disconnect ci-main               # token disabled; its mirrors moved to the trash
```

- The route is on the engine's port and no exposure recipe routes it, so a caller reaches it only from where that port
  already reaches. The `Host` it sends must be in `engine.allowedHosts`.
- What it saves is a draft, or held for a person on the same rules as an AI client's write, and waits in the review queue
  (the app's Memories view, `rhythm release`). A person confirms it; nothing from outside can.
- A capture naming `mirror` keeps one memory for an outside item, read-only to everyone else while the connection stands.
  `capture disconnect` moves a connection's mirrors to the trash, from where a person restores them as ordinary drafts within
  14 days (an AI client can't). It deletes nothing itself, and works on a connection whose token was revoked, and with
  `memory.capture` turned off, as `capture list` does: once a capture token has ever been made both still answer, since a mirror
  is read-only until its connection is removed. `capture connect` does not.
- `token revoke <name>` refuses a token at once and leaves its mirrors as they are; `capture connect` with that name again
  makes a new token for the same connection, keeping its mirrors. Delete the old file first (a token file is never
  overwritten), or name another with `--out`. `token list` shows capture tokens too, and `doctor` checks their files.
- With memory scopes off a token bound to a person writes into the household, where everyone reads it; `capture connect` and
  `capture list` say so, in the mode the install is in.

## What the daily jobs did

With `engine.nightSummary: on` in `rhythm.yaml` (off by default), the engine keeps a summary of its daily jobs (the
pruning of old ingest keys, screen commands and screens, the memory trash and the recall log; later jobs join them),
one row a night and job for 30 nights, and `./rhythm night` prints it:

```sh
./rhythm night                      # the latest night kept
./rhythm night --night 2026-11-03   # one night: the install's date when the daily jobs began
```

```text
Night of 2026-11-03 (the install's date when the daily jobs began): 4 jobs, 3 complete, 1 failed.
  ingest key pruning: complete in 0.1 s of its 15 min; 3 keys pruned
  screen pruning: complete in 0.0 s of its 15 min
  memory trash purge: failed (NpgsqlException) after 2.5 s of its 15 min
  recall log pruning: complete in 0.0 s of its 15 min; 1 rows pruned
```

Each line says how the job's latest run that night ended: complete; partial (it stopped at its own bound and goes on
from where it stopped at its next run); failed, with the kind of failure only; timed out (its budget ran out); or still
running (an earlier run had not ended, so it was not started again). Then its time against its budget, how often it
ran when more than once, and what it did, as counts (never a record's words). The engine's log holds the same failures
(`./rhythm logs engine`). With the setting off, absent or empty, `night` answers as a command the engine does not know,
and what was kept is pruned within 30 nights.

## Backup

`./rhythm backup` runs the `backup` profile: a consistent `pg_dump` plus the engine's archive, with a checksum
manifest, into the encrypted restic repository in `<data>/backups`, then `restic check --read-data`. The staging
folder in `<data>/backup-staging` holds a plain copy while it runs and is removed once the snapshot is checked, as
`deploy/backups/backup.sh` does; a failed backup keeps it for inspection (open question Q-064). Snapshots are never
pruned for you (open question Q-054).

## Restore

```sh
./rhythm snapshots           # pick one, or use latest
./rhythm restore latest
```

1. The snapshot is restored into a new staging folder and checked against its manifest.
2. The engine and the app stop.
3. Before anything changes, the folder is checked again: a file its manifest does not list, a link, or an archive file
   the engine already has that differs from the backup's copy stops the restore there.
4. `database.dump` goes into a **new** database, where `restore-quarantine.sql` disables every token and grant
   (an old backup can bring back access that was revoked since); archive files missing from the engine's archive are
   added.
5. The new database takes the live one's name. The live one is kept as `rhythm_before_<time>`, never dropped.
6. The restored folder, a plain copy of the whole database, is removed (the snapshot still holds it, encrypted).
7. `./rhythm up`: migrate brings the schema and the engine's login up to date, and the engine takes the app token
   from its file again.

If a step from 3 to 5 fails, the live database is as it was: the new database the restore was loading into, if it
made one, is dropped, the restored folder is removed, the stack starts again on the live database, and the command
fails, saying what stopped it. Retrying is safe. `cli/chaos.sh` tests these failures on a throwaway stack.

A restore puts back the database as it was at the backup, so what you changed since is undone too: a person suspended
since is current again, a person taken out of Family since is a member again, a source share or a Family admin taken back
since is back, a capture connection disconnected since is connected again, its mirrors out of the trash (its token stays
disabled, as every token does), a person offboarded since is as they were, their private memories out of the trash, and
a memory deleted for good since is back as the backup held it, in the trash (with 14 days or more from the restore) or
out of it. Do those steps again after the restore (`rhythm person suspend`, `rhythm person offboard`,
`rhythm family remove`, `rhythm share revoke`, `rhythm family unadmin`, `rhythm capture disconnect`,
`rhythm delete-forever`).

Every harness, plugin and capture token from before is disabled. `./rhythm up` names each harness token file that no
longer works; it checks no plugin or capture token file, so make those again as well. Delete each old file, then make
a new one with `./rhythm token create <name>` (`--role ingest --plugin <name>` for a plugin) or `./rhythm capture
connect <name>`: neither overwrites a token file. `cli/e2e.sh` tests this with fictional documents.

On a new machine: `./rhythm init`; replace `secrets/backup_password` with your offline copy (remove the generated
file, write yours; `./rhythm up` gives it its owner); copy the restic repository to `<data>/backups/restic`, which on
Linux belongs to the backup container's user (`sudo chown -R 70:70 <data>/backups/restic`); `./rhythm up` once; then
`./rhythm restore latest`.

## Upgrade

```sh
git pull
./rhythm upgrade             # same tag, rebuilt from this checkout
./rhythm upgrade 0.2.0       # or a new tag (--pull takes published images instead of building, once they exist)
```

1. `./rhythm backup`.
2. The current images are tagged `<tag>-previous`.
3. Build (or pull) with the new tag, then `up` with every container recreated: migrate runs, the engine and app
   switch, and `ready` checks the engine and MCP. The build pulls its base images again (`--pull`), so the .NET,
   Node and Postgres patch releases come in; an image whose pinned version the new tag moved is pulled at `up`.
4. On success the tag goes into `.env` (`RHYTHM_VERSION`). On any failure the previous images come back under their
   tag and the stack starts on them again.
5. It names, changing nothing, each pinned image last checked over 90 days ago and each image an `.env` line such as
   `RHYTHM_POSTGRES_IMAGE` sets, which no upgrade moves ([deploy/README.md](../deploy/README.md#pinned-images)).

It leaves the install's settings, secrets and connectors as they are, whether it succeeds or rolls back. `rhythm.yaml`
stays as you wrote it: a setting that is on stays on, one that is off stays off, and a setting the file predates is not added and answers
off. `.env` changes only in its tag, so the app and the engine publish where they did (`RHYTHM_BIND`, the ports) and
`COMPOSE_PROFILES` starts what it did. A connector you turned off stays off and keeps its secret file. Every secret and
token file stays, every token that worked works and every token revoked stays refused. No secret's value reaches what it
prints, the stack's logs or its containers' settings. `scripts/rhythm-upgrade.test.mjs` checks this for both wrappers,
and `cli/e2e.sh` on a real stack, with those settings off and again with them on. The test also holds steps 1, 2 and 4 to
their order, `./rhythm upgrade` under the same tag included: a rebuild that does not start is what they are for, and
`cli/e2e.sh`'s rollbacks stop at the pull, before anything is rebuilt.

Migrations only go forward. The previous engine runs on a schema that a newer migrate changed only as long as the
change is compatible; when it is not, restore the backup from step 1 (open question Q-055).

## Testing

- `dotnet test engine/Engine.slnx`: `CliTests` (temporary folders, fake descriptors), `ArchiveCommandTests`,
  `MemoryCommandTests` and, with `RHYTHM_TEST_DB`, `CliDatabaseTests`, `MemoryArchiveTests` (export and import between
  throwaway installs), `MemoryCommandDatabaseTests` (remember, list and recall per person) and `TerminalLoginTests`
  (the terminal's login, which the database tells from an AI client's).
- `sh cli/e2e.sh` in a checkout without an install: a whole run in a throwaway stack (project, tag and ports from
  the environment, see the script's header), removed again at the end. It needs `RHYTHM_MODEL_DIR`, a folder
  holding the pinned MiniLM files ([docs/models.md](../docs/models.md)), which its engine reads mounted read-only
  (`cli/e2e.compose.yaml`), so no model is downloaded; its image builds and its upgrade steps (story 17: a build
  with `--pull`, a pulled tag) still need the image and package registries. It stops before starting anything in a
  project that already has containers or volumes, with an image tag another stack's containers run or another Compose
  project built (a stopped install runs no container, but its next `up` would run what the run built), or on a port
  something answers on (`scripts/e2e-guards.test.mjs`). Before any docker call at all it refuses a project that is empty
  or `rhythm`, an install's default and the project Compose falls back to once `.env` has gone, and every Compose call
  it makes names its project itself (`-p`), never one Compose would infer (D-240); so do `cli/lifecycle.sh`,
  `cli/journeys.sh` and `cli/chaos.sh`, and the wrappers name an exported `COMPOSE_PROJECT_NAME` to Compose too.
  Its last steps switch memory scopes on in the throwaway stack and check acceptance stories 1 and 2 there: a copy
  Alex's client saves for herself is found by no one else (Sam, the household view, the app's map), one she saves for
  Family is, and her notes reach Sam and the app's map only while she shares them with Family. Then it imports two
  synthetic exports of another install (`cli/e2e-memories-alex.json` and `-sam.json`), each into the scopes it names,
  checks that a repeat adds nothing, and exports again: Alex's comes back byte for byte but its install, export and
  time lines, and no reader's export holds another person's private records. Last, Alex remembers a memory from the
  terminal, lists it and recalls it with the local model on, and Sam neither lists nor recalls it. Each upgrade step
  (story 17, SB-P7-11) compares what an upgrade must leave as it is: `rhythm.yaml`, `.env` but for its tag, every secret
  and token file (digests, owners and modes, read inside a container, never a value), the services running and what they
  publish, the tools a token is offered, the commands the terminal offers, the connectors `doctor` lists; and it searches
  what the upgrade said, the stack's logs and its containers' settings for each secret's value. It does so with every
  setting off by default, a connector turned off and a token revoked, and at its end with every setting on but
  `app.viewers`, a capture connection made and one disconnected, each time for an upgrade and for one that rolls back;
  the last upgrade moves to a new tag, and from there `.env` alone says which images run.
  A step that proves part of a story names it, and its line in the output says so (`== <step>  [story 1]`).
- `sh cli/lifecycle.sh` in a checkout without an install (SB-P3-16): a fresh install's native memory lifecycle with
  Rhythm alone, in a throwaway stack of its own (project, tag and ports from the environment, default
  `rhythm-lifecycle`, `lifecycle`, 18280 and 18381), removed again with its volumes at the end. It builds the engine and
  app images from the checkout first (with the build cache, never `--pull`), pulls nothing and moves no image tag, and
  stops before starting anything for the same reasons as `cli/e2e.sh`, the model files included
  (`scripts/e2e-guards.test.mjs`). After `init --no-input --demo no` and `up` it checks that Second Brain is off and
  nothing of it is there, and that the memory tools, `recall`, the memory instructions at `initialize`, the app's
  Memories view and its combined search are all off by default; then it turns `memory.tools` and `engine.recall` on in
  `rhythm.yaml`. An AI client (a token bound to Alex) captures a memory, recalls it by its words and by its meaning
  alone, edits it with the revision it read (a stale one changes nothing), undoes the edit, forgets it into the trash and
  restores it with the trash's nonce, each change in its history as the AI client's; a person in the app's Memories view
  does the same with a memory of their own and finds it in the explorer's search and with `rhythm recall`, each change
  the app's; and `rhythm export` holds both memories as restored, with their earlier states, which importing again
  would not add to. Last it turns memory scopes and `memory.share` on: Alex's AI client remembers a private memory
  that Sam's client neither gets nor recalls, shares it into Family (Sam's client then reads it with its author, recalls
  it and sees its history from the share on, and may not take it back), shares it again with nothing changed, and takes
  it back, after which Sam's client finds nothing of it.
- `sh cli/journeys.sh` in a checkout without an install (SB-P6-04): a person's journeys in the app itself, in a throwaway
  stack of its own (project, tag and ports from the environment, default `rhythm-journeys`, `journeys`, 18480 and 18581),
  removed again with its volumes at the end; it needs `npm --prefix app ci` and stops before starting anything for the same
  reasons as `cli/lifecycle.sh`. Each journey is a file of `app/tests/journeys/`: the real React app in jsdom, its every
  request going to the stack's own app container, with AI clients calling `/mcp` with their own tokens, on the demo's data,
  the sample folders and the synthetic parity corpus (`cli/journeys-corpus.mjs` writes the parity corpus's memories as
  the file `rhythm import` reads). With every memory setting off, nothing of the memory view is there and the stars,
  dashboards, health views and original links work; with `memory.tools` on a person saves and finds a memory, changes it
  and takes it back, releases what is held (each import holding as many as the corpus marks held), works every review queue
  (each count its list's length; the pairs are the nightly pass's own, found after a restart), opens tasks, due dates
  and decisions in the Memories view at the counts the brief gives, and uses the trash and undo, all in the app; with
  memory scopes on one memory is shared exactly and taken back, which is the journey that still needs a terminal: with
  viewer sessions off the app's login is the household view, which shares nothing. Each journey prints a
  `terminal: rhythm <command>` line for what it needed one for. Layout, the service worker and a PDF's pages on a canvas
  are what jsdom cannot show.
- `sh cli/chaos.sh` in a checkout without an install (SB-P7-10, story 15): restores that go wrong, in a throwaway stack of
  its own (project, tag and ports from the environment, default `rhythm-chaos`, `chaos`, 18380 and 18481; never the
  project `rhythm`), with `RHYTHM_MODEL_DIR` as for `cli/e2e.sh`. After a backup it revokes a token, suspends a person and
  disconnects a capture connection, then tries a snapshot that is not there, a restored folder whose dump was changed,
  one holding a file its manifest does not list, a dump cut short in its data with a manifest that agrees (pg_restore
  makes the schema and loads rows before it fails), and an engine archive file that differs: each refused with the live
  install answering as before, no half-loaded database and no restored folder left. Then it restores for real, twice: every old token refused, the backed-up records back and the later ones gone,
  what was undone since back as the backup held it, and both replaced databases kept. Every cleanup names its project
  itself and removes the stack's containers, volumes, networks and the three images it built.
- `node scripts/acceptance.mjs` (SB-P6-08): every acceptance story's tests and `cli/e2e.sh`, then `cli/lifecycle.sh`, then
  `cli/journeys.sh`, then `cli/chaos.sh`, as their sandbox stacks, in a Compose project of their own (`COMPOSE_PROJECT_NAME`, default
  `rhythm-acceptance`, named to each script, never `rhythm`) on two free ports it picks, one stack after the other, then a pass or fail table, one row per story. `--list` names each
  story's tests, steps and open backlog items.
