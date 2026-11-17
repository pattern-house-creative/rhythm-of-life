# Operating an install

The operator's guide: a first walk from a fresh checkout through the core features on fictional sample data (the
install, the app, `rhythm doctor`, an AI client, memories and recall, a backup, a restore and an upgrade), then every
setting that is off until you turn it on, every connector that needs something outside Rhythm, what runs on its own and
when, the limits, how fresh each source is, and what you opt into for privacy. The other settings are described, not
walked, and no sample Second Brain export ships: `rhythm import-second-brain` needs an export of your own. Each part
points to the page that has the details: [configuration.md](configuration.md) for every setting,
[../cli/README.md](../cli/README.md) for every command, [security.md](security.md) for who sees what, and
[backups.md](backups.md) for backups and restores.

Rhythm of Life is in alpha and has had no outside security review ([known-limits.md](known-limits.md)). Everything below works with the fictional data in
`demo/` and needs no account anywhere.

## A first walk, on sample data only

You need Docker with Compose 2.20 or later, nothing else. Every command runs from the repository's root; on Windows
PowerShell type `.\rhythm.ps1` where this page says `./rhythm`.

1. **Install** with the fictional notes:

   ```sh
   ./rhythm init --people "Alex, Sam" --timezone Europe/London --notes ../../demo/notes --project-name rhythm-try
   ```

   `init` asks what the flags leave out (`--no-input` takes the defaults), writes `deploy/compose/rhythm.yaml`, `.env`
   and the secret files under `deploy/compose/secrets/`, and prints none of them. Keep a copy of
   `secrets/backup_password` offline: backups cannot be read without it. Paths in `.env` start at `deploy/compose/`, so
   the samples are `../../demo/...` there. Then set `RHYTHM_IMPORT_DIR=../../demo/import` in `deploy/compose/.env`, the
   fictional CSV files and documents.

   `--project-name rhythm-try` gives the trial a Docker Compose project of its own, so it never shares the default
   project, `rhythm`, or its volumes with an install already on this computer. To run it beside such an install, use a
   checkout of its own and give the trial its own ports and image tag on the command line too:
   `RHYTHM_VERSION=try ./rhythm init --project-name rhythm-try --app-port 8090 --mcp-port 8191 ...`. In PowerShell set
   the variable first and clear it after:
   `$env:RHYTHM_VERSION = "try"; .\rhythm.ps1 init --project-name rhythm-try --app-port 8090 --mcp-port 8191 ...`, then
   `Remove-Item Env:RHYTHM_VERSION`. `init` records them in that checkout's `.env`, and every `./rhythm` command there
   then acts on the trial alone ([../cli/README.md](../cli/README.md#two-installs-on-one-computer)).
2. **Start it**: `./rhythm up`. The first one builds the images, which takes some minutes; Compose may first say it
   cannot pull `rhythm-engine` or `rhythm-app`, which is expected, since they are built here. It ends by printing the
   app's address (`http://127.0.0.1:8080`), the MCP address for AI clients (`http://127.0.0.1:8181/mcp`) and the path
   of the first harness token's file (`deploy/compose/secrets/harness_assistant_token`), never a token. At its first
   start the engine downloads the local embeddings model's two files, about 23 MB, once, from the model's pinned public
   repository, and checks them against their checksums ([models.md](models.md),
   [configuration.md](configuration.md#embedding-models)).
3. **Look around the app.** The sky holds the demo's fictional stars, two dashboards and an insight, all labelled DEMO,
   and the sample notes, CSV readings and documents as stars of their own. Sources & settings lists every source with
   its state: the glucose source, Garmin, Google Drive, Second Brain and the calendar each say Not connected, with the
   `./rhythm connect` command that would connect it, and the Rhythm engine and its models are listed below them.
4. **Check it**: `./rhythm doctor` checks the settings, the secret files and their modes, the engine, MCP and the app,
   and exits 1 when something failed ([../cli/README.md](../cli/README.md#doctor)).
5. **Connect your AI client** with the MCP address and the token file's contents as a Bearer token
   ([../harness/README.md](../harness/README.md)). Its `tools/list` shows 29 tools, 28 while the local model is still
   loading.
6. **Memories, on sample data.** In `deploy/compose/rhythm.yaml`, add `tools: on` under `memory:` and, for the
   `recall` tool and the explorer's combined search, `recall: on` under `engine:`, each a line of its own in the section
   that is already there:

   ```yaml
   memory:
     scopes: off
     tools: on          # added
   engine:
     url: http://engine:8080
     recall: on         # added, beside the engine's other keys
   ```

   Then `./rhythm up` again: it restarts the engine and the app on the new settings. Your AI client then has the 41
   memory tools ([../harness/memory.md](../harness/memory.md)), the app has its Memories view, and
   the terminal has a person's own steps. From the terminal, as Alex:

   ```sh
   ./rhythm remember --person alex "DEMO: the allotment key is under the blue pot. #allotment"
   ./rhythm list --person alex
   ./rhythm recall --person alex allotment key
   ```

   `remember`, `list` and `recall` work with `memory.tools` off too. At the terminal `recall` matches words alone unless
   the engine image is given a model folder, and says so; it never downloads one. Everything a memory goes through is
   in [../cli/README.md](../cli/README.md#memories-from-the-terminal).
7. **A backup, and a restore**: `./rhythm backup`, `./rhythm snapshots`, then `./rhythm restore latest`. The restore
   keeps the database it replaces, renamed, and disables every token the backup held, since an old backup could bring
   back access revoked since. `./rhythm up` (which the restore runs at its end) names each harness token file that no
   longer works; delete it and make a new one with `./rhythm token create <name>`. Plugin and capture token files it
   does not check: make those again too ([../cli/README.md](../cli/README.md#restore), [backups.md](backups.md)).
8. **An upgrade**: `./rhythm upgrade` rebuilds this checkout under the same tag, after a backup, and rolls back to the
   images before it if anything fails. It leaves every setting, secret and connector as it was
   ([../cli/README.md](../cli/README.md#upgrade)).
9. **Remove the trial** when you are done. First check which project the next command acts on: the
   `COMPOSE_PROJECT_NAME` your shell sets, if any (`echo $COMPOSE_PROJECT_NAME`; in PowerShell
   `$env:COMPOSE_PROJECT_NAME`), else the one in `deploy/compose/.env` (`grep COMPOSE_PROJECT_NAME deploy/compose/.env`;
   in PowerShell `Select-String COMPOSE_PROJECT_NAME deploy/compose/.env`); it must be `rhythm-try`. `docker compose ls`
   lists every project running on this computer. Never go on when it names another install's project, such as
   `rhythm`: the next command would delete that install's data. Then `./rhythm down -v` stops the trial and deletes its
   volumes, its database and its archive, for good. Run it before you delete `.env`; a command by hand names the project
   too (`docker compose -p rhythm-try down -v`), never one Compose would guess.

   `down -v` keeps the rest: the images the trial built and step 8 tagged, the encrypted backups of step 7 (in
   `deploy/compose/data/backups` unless `init --data` named another folder), and the settings and secret files `init`
   wrote, the backup password among them. A later `init` in that checkout keeps those secrets and backs up into the
   trial's repository. To remove them too, after `down -v`: the images by the trial's tag, its `RHYTHM_VERSION` in
   `deploy/compose/.env` (`dev` unless you gave another), and only when no other install on this computer uses that
   tag: `docker image rm rhythm-engine:<tag> rhythm-app:<tag> rhythm-backup:<tag>`, and after step 8 the same three at
   `<tag>-previous`. Then delete the trial's own checkout, or in it
   `rm -r deploy/compose/data deploy/compose/secrets deploy/compose/.env deploy/compose/rhythm.yaml` (in PowerShell
   `Remove-Item -Recurse deploy/compose/data, deploy/compose/secrets, deploy/compose/.env, deploy/compose/rhythm.yaml`),
   never in a checkout that holds an install you keep.

## Settings that are off until you turn them on

Each is a `rhythm.yaml` setting that is off when absent, so an install whose settings predate one keeps it off after an
upgrade. The example and what `rhythm init` writes leave each out, and so off, but for three they show at their
defaults: `memory.scopes: off`, `engine.legacyToolNames: false` and `adapters.drive.edits: false`. To turn one on, add it
under its section, as step 6 of the walk does, and run `./rhythm up` (or `--force-recreate` with plain
`docker compose`). Any value of an `off` or `on` setting but `off`, `on` or empty stops the engine at start, naming it.

| Setting | Default | With it on | Needs |
|---|---|---|---|
| `memory.tools` | `off` | AI clients get the 41 memory tools and their instructions; the app gets its Memories view; the terminal gets a person's own steps and review queues; the nightly memory passes run (staleness, link pruning, insight candidates) | nothing more; links on save, contradictions and standing instructions need the embeddings model, and possible copies are found by meaning with it and by the same words without it |
| `memory.capsules` | `off` | AI clients get `get_prompt_capsule` and the `rhythm://capsules/...` resources: one scope's "start here" context from its canonical memories | memories in capsule slots, which a person puts there (`memory.tools: on`) |
| `memory.share` | `off` | `share`, and `rhythm share-memory`, moving a memory between its owner's private scope and Family | `memory.tools: on`; while `memory.scopes` is off it refuses every call |
| `memory.scopes` | `off` | per-person private memories and records, and a shared Family; switching on only narrows what anyone reads | read [security.md](security.md#per-person-memory-scopes) and run `./rhythm doctor` first; back off only with `rhythm scopes off`, refused while any memory is private |
| `memory.secondBrainImport` | `off` | `rhythm import-second-brain`: reads one file a Second Brain service exported and says what it would import, writing only with `--write` | the export file; it never reads the service itself |
| `memory.capture` | `off` | `POST /capture` on the engine's own port, for a capture token (`rhythm capture connect <name>`), saving drafts or held memories into the token's one scope | `memory.tools: on` |
| `memory.capsuleWrites` | `off` | `set_capsule` for AI clients: one memory into a capsule slot, or out of it, as a change with a version and an audit row; without it a slot is a person's step | `memory.tools: on` and `memory.capsules: on` |
| `memory.work` | `off` | `create_work`, `list_work`, `update_work`, `claim_work` and `release_work` for AI clients: a project's backlog kept as memories with a type, a state, a priority and an assignee, each change a version and an audit row, and a 2-hour claim so one session works on an item at a time; never on the open loops or in a digest. The app's Memories list filters by them, and each is a star a screen can hide | `memory.tools: on` |
| `engine.recall` | `off` | the `recall` tool (words and meaning in one ranked list, with reasons) and the explorer's combined search | the embeddings model for meaning; without it, keyword results that say so |
| `engine.searchKnowledge` | `fulltext` | `combined`: `search_knowledge` answers in combined retrieval's order | `engine.legacySearch: false` (the engine refuses it otherwise) |
| `engine.recallLog` | `off` | each question `recall` and the explorer's search answer is kept in its reader's scope, with what it found, for 30 days | `engine.recall: on` |
| `engine.rerank` | `off` | `bge-reranker-base` or `ms-marco-minilm`: a local cross-encoder reorders combined retrieval's first 20 results; measured on the test set, neither model was kept (each loses a few answers, and `bge-reranker-base` is too slow), so leave it off ([models.md](models.md)) | `engine.recall: on` or `engine.searchKnowledge: combined`, and the model's files, downloaded once or from `RHYTHM_RERANKER_DIR_BGE` or `RHYTHM_RERANKER_DIR_MSMARCO` |
| `engine.writeLimit` | `off` | each token past 300 writes in 10 minutes has its next write refused until its oldest leaves the window ([security.md](security.md#the-write-limit)) | nothing |
| `engine.nightSummary` | `off` | each daily job's night kept for 30 nights, as kinds and counts, and read with `rhythm night` | nothing |
| `engine.legacyToolNames` | `false` | `true` also lists the old tool names (`contracts/README.md` 2.3) | nothing |
| `app.viewers` | `off` | viewer sessions for one person in the app; this version refuses `on`, and the engine and the app stop at start with it | not available in this version |
| `graph.pluginLinks` | `off` | a plugin's own links between its records join the map as that plugin's suggestions | a plugin that sends links |
| `graph.similarity.model` | none | `bge-small`: the map's similar and copy links come from a second vector per star made by bge-small-en-v1.5, read against its own thresholds, which finds more of the related pairs; search, recall and memory checks keep MiniLM ([configuration.md](configuration.md#rhythmyaml), D-256) | the embeddings model and similar links on, and bge-small's files, downloaded once or from `RHYTHM_MODEL_DIR_BGE_SMALL`; until they load the map uses MiniLM's vectors |
| `graph.similarity.maxLinks` | none | a number from 2 to 50: a similar link stays only while it is among that many strongest of both its stars, so a record close to almost everything stops gathering links from everywhere; some stars may be left with none ([configuration.md](configuration.md#rhythmyaml), D-258) | similar links on (`graph.similarity.threshold` not `null`) |
| `adapters.embeddings.thresholds` | `today` | `tuned`: the map, links on save, possible copies, insight candidates, standing instructions and contradictions read the live model's own re-tuned thresholds; measured on the test set, it gains nothing the retrieval check counts | the embeddings model |
| `adapters.embeddings.shadow` | none | `bge-small`: a second model's index built beside MiniLM's, a few documents a minute, read by no search | that model's files, downloaded once or from `RHYTHM_MODEL_DIR_BGE_SMALL` |
| `adapters.embeddings.backfill` | `false` | `true` gives passages to saved documents that asked for them while the model was off or loading | the embeddings model |
| `adapters.drive.edits` | `false` | `true` lets a dashboard make its declared writes to Sheets and Docs, once a person turns on Allow edits for it | the Drive adapter, and its edit grant (`rhythm connect drive edit`) |

The other way round, `demo.enabled` is `true`: set it to `false` and the demo's stars, numbers, dashboards and insight
go, but for a demo dashboard someone changed and the insight once someone gave feedback on it
([configuration.md](configuration.md)).

## Connectors, each off and not connected until you connect it

Nothing below is connected on a new install: no account is asked for, no link ships with Rhythm, and nobody can enter
yours for you. The example settings list each adapter below with `enabled: false`, and `./rhythm connect` lists every
adapter this build describes, each `on` or `off`. Connecting one asks its settings and secrets (secrets at a hidden
prompt, into a file under `secrets/adapters/`) and only then turns it on.

| Connector | Needs, outside Rhythm | On a new install | Where it says so | To connect |
|---|---|---|---|---|
| Nightscout, `adapters.nightscout` | your own Nightscout site and, if it asks for one, a `readable` access token | off, not connected | Sources & settings (the glucose source: Not connected), `./rhythm connect` | `./rhythm connect nightscout` ([adapters/nightscout.md](adapters/nightscout.md)) |
| Garmin, `adapters.garmin` | a Garmin Connect account: its Personal API key, or its exported files | off, not connected | Sources & settings (Garmin: Not connected), `./rhythm connect` | `./rhythm connect garmin` ([adapters/garmin.md](adapters/garmin.md)) |
| Google Drive, `adapters.drive` | your own Google Cloud project, and your consent in a browser | off, not connected | Sources & settings (Google Drive: Not connected), `./rhythm connect` | `./rhythm connect drive` ([adapters/drive.md](adapters/drive.md)) |
| Second Brain, `adapters.second-brain` | a Second Brain service and a token for it | off, not connected | Sources & settings (Second Brain: Not connected), `./rhythm connect` | `./rhythm connect second-brain` ([adapters/second-brain.md](adapters/second-brain.md)) |
| Calendar, `adapters.calendar` | your own calendar's private ICS link (Google, Outlook or iCloud), which only you enter | off, not connected: it reads nothing until you turn it on and enter your own link | Sources & settings (Calendar: Not connected), `./rhythm connect` | `./rhythm connect calendar` ([adapters/calendar.md](adapters/calendar.md)) |
| Local titles, `adapters.titles` | no account: Ollama in the `titles` profile, and a model you pull once (about 2.5 GB for the suggested one) | off, not connected: no titles service runs | `./rhythm connect`, and the titles model under Models in Sources & settings | `./rhythm connect titles` ([adapters/titles.md](adapters/titles.md)) |
| Tailscale, `adapters.tailscale` | a Tailscale account | off, not connected: no exposure container runs | `./rhythm connect` | [../deploy/exposure/tailscale/](../deploy/exposure/tailscale/README.md) |
| Cloudflare, `adapters.cloudflare` | a Cloudflare account, a domain on it and a tunnel | off, not connected: no exposure container runs | `./rhythm connect` | [../deploy/exposure/cloudflare/](../deploy/exposure/cloudflare/README.md) |
| OpenAI tunnel, `adapters.openai-tunnel` | an OpenAI platform organization with a Secure MCP Tunnel, and a restricted runtime key | off, not connected: no exposure container runs | `./rhythm connect` | [../deploy/exposure/openai-tunnel/](../deploy/exposure/openai-tunnel/README.md) |
| CareLink, `plugins.carelink` | a CareLink account, and the separate, unofficial plugin repository | off, not connected: `rhythm.yaml` lists no plugin (`plugins: {}`) | [adapters/carelink.md](adapters/carelink.md) | its own repository, then `./rhythm token create --role ingest --plugin carelink` |
| The capture endpoint, `memory.capture` | nothing: your own webhook, CI job or shortcut | off, not connected: `POST /capture` is no route, and no capture token exists | [security.md](security.md#capturing-from-outside) | `memory.capture: on` beside `memory.tools: on`, then `./rhythm capture connect <name>` |

Two more things live outside Rhythm and connect only when you do it yourself: **your AI client** (an account with
ChatGPT, Claude or another MCP client), which the first `up` makes a token file for and which reads nothing until you
give that file's contents to a client ([../harness/README.md](../harness/README.md)); and **session hooks** for coding
agents, which nothing installs ([../harness/hooks/README.md](../harness/hooks/README.md)).

## What runs on its own, and when

| What | When | Turned on by |
|---|---|---|
| the map rebuild | every `graph.refreshSeconds` (900, 15 minutes); sooner after an import, an upload or the app's Refresh | always |
| the notes folder, Drive, Second Brain and the calendar | read at each map rebuild; the calendar repeats a good read at most once every `pollSeconds` (900) | each adapter |
| the CSV import folder and the documents folder | looked at every minute | each adapter |
| Nightscout | every `pollSeconds` (300) | the adapter |
| Garmin | with a key, every `pollSeconds` (21600, 6 hours); exported files whenever you drop them | the adapter |
| Sync now | within seconds of someone asking in Sources & settings, one batch at a time, within each source's own limit | a person |
| the embeddings index | a few documents a minute, resuming where it stopped | the embeddings model |
| the daily jobs | at the engine's start, then every 24 hours: ingest keys over 90 days, screen commands over 30 days and screens unseen for 180 days pruned; memories in the trash past their 14 days deleted for good, at most 1000 a day; memory idempotency keys over 30 days and the recall log's rows over 30 days pruned | always (each does nothing when there is nothing to do) |
| an offboarding under way | after the trash purge in the daily jobs: once the purge has taken a person's private memories, their emptied private projects are deleted and the offboarding recorded finished; it never starts one, and waits while the person is brought back ([../cli/README.md](../cli/README.md#offboarding-a-person)) | `./rhythm person offboard <id>` (nothing to do when none is under way) |
| the memory passes | after the daily jobs: the staleness pass, the link pruning pass and the insight candidate pass (once the model is on) | `memory.tools: on` |
| the night summary | after each daily turn | `engine.nightSummary: on` |
| backups | never on their own: `./rhythm backup`, or the host's own scheduler ([backups.md](backups.md)) | you |

The daily jobs and the memory passes each have a 15-minute budget; one that its budget or its own bound stops does the
rest at its next run ([dev/engine.md](dev/engine.md)).

## Limits

| What | Limit |
|---|---|
| writes per token, with `engine.writeLimit: on` | 300 in 10 minutes; reads, the app and the terminal are never counted ([security.md](security.md#the-write-limit)) |
| a burst | past a token's 40th memory write in 10 minutes, its writes are saved but held for a person |
| the map | `graph.maxMemories` 3000 and `graph.maxDocuments` 1000 (at most 4900 together), `graph.nodeLimit` 300 |
| a document | 5 MiB added in the app; 20 MiB dropped in the documents folder |
| a memory's text | over 32 KiB it is held for review on every channel; a capture's body is at most 288 KiB and its text 131,072 bytes |
| a memory's history | 20 earlier states kept, every audit row kept |
| the trash | 14 days, then deleted for good by the daily purge |
| standing instructions (`memory.tools: on`) | 500 characters each, 50 a scope |
| a work item's summary (`memory.work: on`) | 280 characters on one line, given when it becomes done or cancelled |
| the recall log (`engine.recallLog: on`) | 200 a day a scope, kept 30 days |
| screens | a command not picked up within 30 seconds expires; at most 20 wait for a screen |
| people | 20 in `people` |
| calendars | 3 private links |
| a plugin's call | a body of at most 512 KiB |

## How fresh each source is

- **The map** is rebuilt every `graph.refreshSeconds` and shows as stale once it is older than `graph.freshnessSeconds`
  (2100 seconds, 35 minutes).
- **Sources & settings** shows, for each source, when its newest reading was observed, its last successful sync and when
  the app retrieved it. A reading that is missing stays missing: it is never replaced with demo data. The Rhythm engine's
  row is connected by its own stars on the map (data, saved documents and dashboards, under the catalog provider id:
  `rhythm`, unless an install kept an older engine's with `RHYTHM_CATALOG_PROVIDER`), observed at the newest of them;
  with none of them on the map, by the engine's answer to the map, retrieved when it answered.
- **A glucose reading** reads as stale 20 minutes after it was observed.
- **Search** serves a note's or a Drive file's passages only while the map would show its star (seen by a rebuild within
  `graph.freshnessSeconds`), and a Drive file's only while Drive still holds the version they were read from.
- **Memories** (`memory.tools: on`): a `state` memory may be out of date 90 days after its last change or confirmation, a
  `volatile` one after 14, and `list_stale` and the Memories view's To check list name them; a memory citing a Drive
  document is checked at Drive at each read.

## Privacy: what you opt into

Each of these is off until you turn it on, and each page linked says what it keeps:

- **`engine.recallLog`** keeps the questions `recall` answered and what they found, in each reader's own scope, for 30
  days. Turned off, the daily job prunes them within 30 days.
- **`adapters.embeddings.sources` listing `drive`** keeps a copy of each Drive file's text in the search passages, read
  again for every new version; nothing else keeps a Drive file's words. Taken out, its passages go at the next start.
- **The calendar** stores each event's name and place, reads its description from the calendar when a star is opened,
  and lets your AI clients read all three; anyone who can send you an invitation can write them. The link stays in its
  secret file, outside the database and its backups ([security.md](security.md#a-calendars-private-link)).
- **`memory.capture`, and session hooks**, save what a webhook, a CI job, a shortcut or a coding agent's finished
  message sends, as drafts a person reviews ([security.md](security.md#capturing-from-outside)).
- **`memory.scopes`** narrows what each person reads; it never widens it. The operator, the host and every backup still
  hold everything.
- **Exposure** (`RHYTHM_BIND=0.0.0.0`, Tailscale, Cloudflare, the OpenAI tunnel) decides who can reach the app, which has
  no login of its own, and which AI clients can reach `/mcp` ([security.md](security.md#what-never-to-expose)).

What leaves the machine on its own: the engine reaches the internet only for the adapters you connect and, once, for the
embeddings model's files when no folder gives them. The app's server talks only to the engine, and the browser loads the
app from its own server. Building the images (`./rhythm up` the first time, `./rhythm upgrade`) fetches their base
images and packages.

## Secrets, scopes, upgrades, backups and restores

- **Secrets** are one file each under `deploy/compose/secrets/`, never a value in `rhythm.yaml` or `.env`; `rhythm up`
  gives each its reader's owner and mode, and `rhythm doctor` checks them ([configuration.md](configuration.md#secrets)).
- **Scopes**: `memory.scopes` stays `off` until you decide otherwise; how to switch, and what it does not protect, is in
  [security.md](security.md#per-person-memory-scopes).
- **Upgrades** keep every setting, secret and connector as they were; a setting newer than your `rhythm.yaml` answers off
  ([../cli/README.md](../cli/README.md#upgrade)).
- **Someone leaving**: `./rhythm person offboard <id>` on the host, after a preview and their id typed, ends the access
  bound to them at once (their tokens and sessions, Family and source shares) and moves their private memories to the
  trash, from which the purge deletes them after 14 days; the Family memories they wrote stay with them as their
  author. With `memory.scopes` off it ends that access and moves nothing. It does not end what is bound to no person,
  and the preview names each: a household token they used (`assistant`, the first one, among them) stays enabled until
  you `./rhythm token revoke <name>` it; the app has no login, so keep them off its network; and an adapter or plugin
  reading their own account keeps adding records until you disconnect it, an adapter whose `owner` is them needing
  another owner before you take them out of `people`. Brought back later, they stay out of Family until
  `./rhythm family add <id>` ([../cli/README.md](../cli/README.md#offboarding-a-person),
  [security.md](security.md#per-person-memory-scopes)).
- **Backups and restores**: encrypted with the backup password, which you keep offline; a restore disables every token
  and undoes what changed since the backup, safety steps included ([backups.md](backups.md)).
