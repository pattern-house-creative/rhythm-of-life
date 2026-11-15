# Changelog

What changed in each version, newest first. Versions follow [Semantic Versioning](https://semver.org/);
while the project is in alpha, any version may change settings or need a step from the operator, and
each entry says so under **Upgrading**.

## 0.1.0-alpha.1 (not yet released)

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
