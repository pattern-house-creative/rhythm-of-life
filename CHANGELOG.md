# Changelog

What changed in each version, newest first. Versions follow [Semantic Versioning](https://semver.org/);
while the project is in alpha, any version may change settings or need a step from the operator, and
each entry says so under **Upgrading**.

## 0.1.0-alpha.4 (23 November 2026)

- The whole app and the Android launcher (0.1.17) have one refreshed look. One set of design tokens (palette, type
  scale, spacing, corners, focus ring) draws everything the app itself draws: Home and its sky, the health tiles and
  panels, the Explorer, Sources & settings, Memories, Studio, the readers and the launcher. Text a person reads is at
  least 12 px, the app's own controls show keyboard focus, touch targets are 44 px on the web and 48 dp on Android
  wherever the layout has room, and closing a Home panel returns focus to what opened it. A dashboard's card on Home has a frame with its name, an opening arrow and a
  freshness line. What its author wrote inside is untouched, and the `--rhythm-*` names a page reads are unchanged.
  A wide sky names more stars at rest and scales names up on a wall display; a phone's sky lets names that would
  collide go quiet, and the selected star's name always shows. Wording says more exactly what a reading, a time, a
  permission or an action means: a dashboard's footer says when its data was recorded and when it was read, Allow
  edits says what each write may do, and the launcher's Settings says what saving a new household address clears
  before it is saved. The starter Projects, Agents and Groceries dashboards take the same look on new installs;
  pages already installed are not rewritten. See [the design system](docs/dev/design-system.md) and
  [the launcher](apps/android-launcher/README.md#what-you-get).

- Android launcher 0.1.16 draws app icons to fit the sky. On Home, in the dock and in the drawer, each app is a
  glyph on a dark glass tile in the launcher's own colours instead of a stock icon on a white circle. Settings,
  **App icons**, switches between **Sky** (the default), **Glass** (the app's own colours on the tile) and
  **Original**. See [the launcher](apps/android-launcher/README.md#what-you-get).

- When a check an install adds to the database refuses a memory or work write, such as a trigger that guards which
  agent session may claim a ticket, the agent is told `refused` with that check's own words instead of
  `unavailable`, which read as an outage and led agents to carry on without a claim. A database that does not answer
  is still `unavailable`. See [the contracts](contracts/README.md) and [harness/memory.md](harness/memory.md).

- Rhythm checks the final size before an agent appends to a work item. When an update would push the ticket over 32 KiB, the ticket stays usable and the agent receives instructions to consolidate it. The previous text remains in version history when the agent saves the summary.

- The Android launcher's Home is now a set of fixed-height pages instead of a scrolling grid. Apps and
  previews that do not fit go on the next page; swipe left and right between pages, or hold a lifted
  tile at the screen's edge to move it to another page. An existing Home is split into pages on first
  start without losing or reordering any pin. Launcher 0.1.15 keeps previews on the three most recently
  visited preview pages in memory so swiping back resumes the loaded dashboards.
  See [the launcher](apps/android-launcher/README.md#what-you-get).

- The starter Agents dashboard has a **T3 team** view: the orchestrator, project leads and their workers as a
  connected dispatcher reports them, each worker linked to its original ticket. It tells a ticket's claim, the
  session's last observed state and an overdue report apart. A fresh workspace shows a waiting message until a
  dispatcher connects. See [starter setup](harness/starter-workspace.md#t3-team-reporting).

- Collections and the Projects dashboard are now separate. A collection groups memories, tickets and capsule
  entries; **Memories → Collections**, **Edit → Show in Projects** lists it on the Projects dashboard, even with no
  tickets, and archiving hides it until it is restored. In Memories, **Start a project** on an idea gives it a
  collection of its own, with the original note as its overview and its links and history kept. Agent tools still
  take the collection's slug as `project`. See
  [starter setup](harness/starter-workspace.md#collections-and-the-projects-dashboard).

- **Upgrading:** the engine applies migrations 087 to 090 on startup. 087 turns **Show in Projects** on for
  collections that have open work, a tagged project overview or a starter project guide. 088 and 089 give the
  starter's Projects and Agents dashboards a new version, with the designated collections and the T3 team view,
  when their page is one of the known published shapes; the earlier version is kept, and a page customized
  separately is left as it is. 090 adds the 32 KiB check on agent updates to work items.

- The Second Brain adapter's guide says how to retire the service after importing its memories: the order of steps,
  what Rhythm keeps and what to remove. See [Second Brain](docs/adapters/second-brain.md#retiring-the-service-after-an-import).

- The web app's full npm audit, build and test tools included, now reports zero findings. The offline
  build plugin's file matcher is pinned to a patched release. See the
  [dependency review](docs/dependency-license-review-20261109.md#18-november-cleared).

- Android launcher 0.1.14 lets you respond to the daily insight from Home, include a note,
  and choose when a reminder should return.

## 0.1.0-alpha.3 (18 November 2026)

- Fresh installs include Projects with an Ideas inbox, Agents, shared agent instructions and editable starter capsules.
  Choose `--starter no` for a minimal install. Existing installations can add the workspace deliberately. Groceries is
  available separately as a native shared shopping list. See [starter setup](harness/starter-workspace.md).

## 0.1.0-alpha.2 (18 November 2026)

- The Android launcher drawer shows apps against the sky. Choose the icon grid or an alphabetical
  list with a draggable letter index. Both support search and remember the selected layout.
  The reader opens original Google files in the Android browser.
- **Upgrading:** the engine applies migrations 085 and 086 on startup. Memory history keeps distinct
  prior wording, and tokens can record their last use and an optional expiry. Existing tokens keep
  their previous access and have no expiry unless one is set.
- The web app's production npm audit reported zero findings. Its build and test tools have
  14 reported dependency findings, documented in [Known limits](docs/known-limits.md).

- Nightly backups can prune snapshots under an opt-in retention policy. The doctor command reports
  snapshot age and warns when the encrypted repository and engine data share a filesystem.
- Unusual-day comparisons now use completed source-local days. Current readings still appear in coverage.
- Set `engine.hostRoles` to accept only harness tokens on a public MCP hostname while preserving
  the app and plugin roles on the internal hostname. Existing settings retain their behavior.

- Postgres now uses its own private Compose network. App-facing proxies use a separate edge
  network. **Upgrading:** move any custom service that accesses Postgres directly (such as a
  separately managed backup) onto the project's `database` network before its next run.

- Third-party container defaults now use multi-platform manifest digests. Base image patches arrive
  when the inventory pin is updated.

- The optional BGE-small model now downloads from a pinned MIT-declared copy. Its quantized weights,
  vocabulary, and vector identity match the previous source. Existing vectors need no rebuild.

- AI clients now keep related feedback and acceptance criteria on the existing project item and file a new
  item when an outcome needs independent follow-up. The shared instructions also explain which decisions
  agents can settle and which need the person's approval.
- **Milestones for the project backlog** (with `memory.work` on). A step of a project's roadmap that takes
  several pieces can be a work item of its own that depends on each piece: it stays blocked until every
  piece is done or cancelled, and now says how far they have come. `list_work` and a dashboard's memories
  feed give each work item `dependencies`: how many work items it depends on in all, whatever their state,
  and how many of those are done and cancelled, so a board can show a milestone's progress. The served
  instructions tell AI clients to make milestones this way and to move one to review when its last piece
  is done. **Upgrading:** migration 084 applies at the next start; it adds one read-only function and
  changes no table.
- **Fixed: the explorer said "A source returned incomplete or unavailable results"** each time it loaded a
  star's connections, though nothing had failed. The engine's note on the snapshot it read came through
  as an issue on every search and neighbours read. The app's server now leaves that note out of a read
  the engine answered, and says the read is complete when no page is left, so the explorer says the
  neighbours were retrieved, and that a source couldn't be read only when the read failed.
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
