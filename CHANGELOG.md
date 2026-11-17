# Changelog

What changed in each version, newest first. Versions follow [Semantic Versioning](https://semver.org/);
while the project is in alpha, any version may change settings or need a step from the operator, and
each entry says so under **Upgrading**.

## Unreleased

- **Fixed: a dashboard's focused map always said "A source couldn't be read"** when nothing had failed. The
  engine notes on every map read it answers what it read (a scheduled-refresh snapshot, and how much of it
  is shown), under its own catalog provider, and the map part took that note for a source it couldn't
  read. It now says so only for an issue a source gave.
- **Fixed: a dashboard that reads memories by filter and names no star never showed on the sky**, so a
  board like a Projects board could only be opened by its link. It now sits beside the project stars its
  memories feeds filter on, as a dashboard sits beside the stars it reads; one that names no project (a
  board over every project) is a star of its own, on the sky's quieter outer ring where every star with no
  links goes, named and drawn as a dashboard and kept on a full sky. It opens as any dashboard star does,
  and the explorer's search finds it by its title. The app's dashboard list gives such a dashboard the
  project stars it sits beside (`projects`), found for each reader as its feed finds them. Dashboards that
  name stars are placed as before, and nothing else is added to Home.
- **Dashboards can read memories by filter** (with `memory.tools` on). A new feed kind, `memories`, gives a
  dashboard the memories its reader reads that match the Memories view's own filters: words, status, kind,
  project and tag, and with `memory.work` on work, type, state and priority. Within one filter any of
  several values matches, every filter given must match, and each can be turned round ("any state except
  done and cancelled"). Each memory comes with its project, tags, dates and a preview of its words, and a
  work item with its state, type, priority, assignee, who holds its claim and until when, and what blocks
  it, so an AI client can build a project board on it. A memory held for review, one in the trash and,
  unless the filter asks for them, those marked wrong are never in it; a feed gives at most 1,000, newest
  first, and says when there are more. The Memories view and the feed share one definition of the filters,
  so they show the same memories. A dashboard with a memories feed needs no star of its own. **Upgrading:**
  migration 083 applies at the next start.
- **A project backlog for AI clients, kept as memories** (off by default). `memory.work: on`, beside
  `memory.tools`, gives them `create_work`, `list_work` and `update_work`. A work item is a memory in a
  project with a type (task, bug, feature or improvement), a state (backlog, ready, active, review, done or
  cancelled) and maybe a priority, so recall finds it, links tie it to what it is about and its history
  keeps every change; what it must do stays in its own words. Each change is a version with an audit row
  naming the client, listed with the client's other changes in the brief, and `undo` puts it back; done
  and cancelled take a short summary. A work item is never a personal task: it stays off the open loops
  and out of digests, and your own commitments work as before. `rhythm export` and `import` carry work
  items. In the app the Memories view's list filters by work, its state, type and priority, and a work
  item says what it is on its row. On the sky a work item is a star drawn as a small box; "Work items
  on the sky" in Sources & settings leaves them off that screen's skies, and deletes nothing. There is
  no board, and nothing new on Home. One work item that waits on another says so with `link`'s
  `depends_on`, and `list_work` says which items are blocked and on what, read each time it lists them;
  a link that would close a loop is refused. A work item can be assigned to a person of the household
  or to an agent's label, and an AI session claims an item with `claim_work` before it starts on it:
  for 2 hours, renewed while it works, only that session moves the item's state, and another session is
  told who holds it and until when; `release_work` lets it go, and a person can release any claim from
  the item in the Memories view. **Upgrading:** migrations 079, 080 and 081 apply at the next start.
- **Fixed: Sources & settings said the Rhythm engine was not connected** on an install that kept an
  older engine's star ids (`RHYTHM_CATALOG_PROVIDER`), while the engine answered every read. Its row
  looked for the engine's own stars under `rhythm` alone; it now reads them under the catalog provider
  id the install uses, and with none of them on the map it says the engine is connected once it has
  answered the map.
- **Fixed: `update` cleared a memory's volatility when it changed only the words.** Saving new text
  through `update`, or editing the words in the Memories view, emptied the memory's volatility (durable,
  state or volatile), though only what a change names should change; a memory left with none drops out
  of the stale queue. New text now keeps it, as it keeps the kind, importance, project and tags. A memory
  that already lost its volatility keeps none until it is set again. **Upgrading:** migration 082
  applies at the next start.
- **AI clients can keep capsule slots, where you let them** (off by default). `memory.capsuleWrites: on`,
  beside `memory.tools` and `memory.capsules`, gives them `set_capsule`: one memory into a capsule slot,
  or out of it, as a change with a version and an audit row naming the client. A client's capsule
  changes are listed with its other changes in the brief and the app's Memories view, where a person can
  undo them; a slot another memory fills is refused. Without the setting a slot stays a person's step,
  as before. The engine cannot tell whether a person asked for a change, so read
  [security.md](docs/security.md#capsule-slots-set-by-an-ai-client) before turning it on.
  **Upgrading:** migration 078 applies at the next start.
- **Fixed: a map rebuild that slowed with the square of the map.** The step that retires old links
  searched the links once for every current record, so with a few thousand records it could pass the
  rebuild's four-second statement limit and fail. It now checks only the links it would retire.
- **Optional OAuth on the MCP endpoint** (off by default). With `engine.oauth.enabled: true` the engine
  takes an OAuth sign-in (authorization code with PKCE, one predefined public client) beside bearer
  tokens on the same `/mcp`, for clients that connect that way; it needs a canonical HTTPS
  `engine.publicMcpUrl` and an exact `engine.oauth.redirectUris` list
  ([setup](harness/chatgpt/oauth.md)). Bearer clients work as before. **Upgrading:** migration 077
  applies at the next start.
- **The memory instructions say how to keep a task.** The block the engine gives an AI client when it
  connects (and `harness/memory.md`) now says that something the person means to do is saved with the
  `task` tag, `owed_to` or `owed_by`, with no special phrase; that a memory has one date, which a snooze
  moves, so a deadline is never pushed back for a later reminder without saying so; that `append` and
  `update` read no date from their words; and that "what needs doing" walks `list_loops` and `list_due`
  to the end, not `brief` alone. No tool changed. A client reads the block when it connects, so
  reconnect it after upgrading.
- **The Android launcher's source** is in `apps/android-launcher`: a phone's Home screen with dashboard
  previews beside your apps, the sky and the day's insight. It is a development build that you compile
  yourself; there is no released APK. Opened from the launcher, a dashboard fills the screen with no exit
  bar, since Android's Back already leaves it.

## 0.1.0-alpha.1 (15 November 2026)

The first public version.

### What is in it

- **The app.** An installable web app for a wall screen, a laptop or a phone: the sky of stars, corner
  cards, dashboards, a daily insight, an explorer with search, a reader for documents, Sources &
  settings, and named live screens that an AI client or another screen can send things to.
- **The engine.** A .NET service on Postgres with pgvector: the catalog, the relationship graph with
  "similar in meaning" links from a local embeddings model, dashboards, insights and one MCP endpoint
  with a token per client.
- **Your own stars with no account:** a Markdown notes folder, CSV files and PDF, text and Markdown
  documents.
- **MCP for your AI:** 29 tools by default, with `recall`, the memory tools, capsules and sharing
  behind settings that are off until you turn them on. Studio lets a client draft a dashboard live on
  one screen before it is saved.
- **Memories** (off by default): native memories with history, undo, trash, holds for suspicious
  writes, projects, due dates, loops, decisions, duplicates and contradictions, in the app, over MCP
  and from the terminal; import and export; an importer for a Second Brain export.
- **Optional adapters:** Nightscout, Garmin, Google Drive, a calendar, Second Brain and a local titles
  model. The CareLink plugin is a separate repository, not published yet.
- **The `rhythm` command:** `init`, `up`, `down`, `connect`, `doctor`, tokens and people, encrypted
  `backup` and `restore`, and `upgrade` with rollback, for sh and PowerShell.
- **Exposure recipes:** Tailscale, Cloudflare Tunnel and Access, OpenAI's Secure MCP Tunnel, a reverse
  proxy, and a TrueNAS guide.

### Safety changes made for this release

- `./rhythm down -v` is refused in a checkout that names no Compose project, where it would have
  deleted the default project's data, whichever install that was. A first `init` is refused while the
  default project already has volumes, unless you name the project.
- The engine refuses a `rhythm.yaml` key it does not know, naming the line and the nearest known key.
  A misspelt key used to be ignored without a word.
- `init --exposure cloudflare` asks for the separate host name that carries `/mcp` (`--mcp-host`) and
  writes an MCP address that works behind Cloudflare Access.

### Upgrading

Nothing to upgrade from. If you ran a pre-release checkout: run `./rhythm doctor` before `./rhythm up`,
since a key the engine used to ignore in `rhythm.yaml` now stops it at start with a message naming
the key.

### Known limits

See [docs/known-limits.md](docs/known-limits.md).
