# Changelog

What changed in each version, newest first. Versions follow [Semantic Versioning](https://semver.org/);
while the project is in alpha, any version may change settings or need a step from the operator, and
each entry says so under **Upgrading**.

## Unreleased

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
