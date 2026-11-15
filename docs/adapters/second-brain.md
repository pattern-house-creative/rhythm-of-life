# Second Brain

The `second-brain` adapter shows the memories kept in a Second Brain memory service as stars on the map. It is
**optional** and off by default: the default memory source is the `notes` adapter, a folder of Markdown files. Turn this
one on only if you already run such a service.

## What it does

- **Stars.** Every current memory in the token's personal workspace, and in each team workspace listed in
  `adapters.second-brain.teams`, becomes a memory star titled with a short excerpt of its first line. Memories that are
  drafts, held for review (`quarantine:` tags), retracted or no longer current are left out. The personal workspace's
  memories belong to `owner`; a team workspace is shared, so its memories belong to the household.
- **Projects.** A memory tagged `project:<id>`, or with one of a project's aliases, hangs from that project's star, named
  from the service's project registry.
- **Links.** The service's own links between memories become links: one the person made is verified, one the system made
  is inferred, an automatic similarity link is only suggested. A Drive address in a memory's text links it to that file's
  star when the `drive` adapter shows it.
- **By reference.** The memory text is not stored in Rhythm: only the excerpt title, the links and, with similarity on, a
  similarity vector. Opening a memory's star reads its current text from the service, for that view only.
- **Read only.** The adapter never writes to the service.

## Who owns these memories, and who sees them

- **Second Brain owns them.** Their text, history, links and sharing live in the service. Edit, share or delete a
  memory there; Rhythm shows the change at its next map refresh and never changes anything in the service. Memories
  Rhythm keeps itself, once it has them (planned), are separate records with their own ids, never merged with these by
  title ([../dev/memory.md](../dev/memory.md) section 10).
- **Where they land.** The personal workspace's memories are filed as `owner`'s private records; each team workspace's
  in `teams` as the household's, shown as Family. A memory keeps its star when it moves between workspaces in Second
  Brain.
- **Who sees them today:** everyone the install admits, whatever `owner` says. The household shares everything
  ([../security.md](../security.md)).
- **Planned, not in this version: per-person scopes.** With `memory.scopes` on (off by default,
  [../dev/memory.md](../dev/memory.md) section 6), the personal workspace's memories will reach only AI clients and app
  sessions tied to `owner`, and the team workspaces' everyone in Family, whether or not they are on that team in Second
  Brain: listing a team here shares it with the whole household. With `owner: household` the personal workspace's
  memories stay visible to everyone, as today; set `owner` to the token's person first if they should be private.
- **The token is one member's.** Rhythm lists only what that member can read in Second Brain: their personal workspace
  and the teams you name.

## Before you start

You need the service's address (its `https` origin) and a bearer token for your member account, from the service's own
settings. The token only needs to read.

## Connecting

```sh
./rhythm connect second-brain     # from the repository root; .\rhythm.ps1 connect second-brain in PowerShell
```

It asks for the address, any team workspace IDs and whose memories these are, and for the token at a hidden prompt, which
it saves as `deploy/compose/secrets/adapters/second-brain_token` (or write that file yourself, mode 0600). The next map
refresh lists the memories.

## Settings

```yaml
adapters:
  second-brain:
    enabled: true
    url: https://brain.example.invalid/   # the service's https origin; there is no default
    teams: []                             # team workspace IDs to show besides your personal one, up to 10
    owner: household                      # a person id, or household
```

| Secret file (under `deploy/compose/secrets/adapters/`) | What |
|---|---|
| `second-brain_token` | the bearer token; or give its path in `RHYTHM_SECOND_BRAIN_TOKEN_FILE` |

A replaced token file takes effect on the next refresh.

## What the service must answer

The adapter reads these routes, relative to `url`, each with `Authorization: Bearer <token>`. It follows no redirects and
sends no cookies.

| Route | Answer |
|---|---|
| `GET list?workspace=personal&n=100[&before=<ms>]` (team: `workspace=company&team=<id>`) | a JSON array, newest first, of `{id (uuid), content, tags (array, or a JSON string of one), created_at (ms), workspace, workspace_id, validity_state, retracted_source, source}`; `before` is inclusive |
| `GET projects?workspace=...` | `{projects: [{id, name, aliases: []}]}` |
| `GET graph?limit=1500&workspace=...` | `{nodes: [...], edges: [{source, target, type, weight, provenance: explicit, system or automatic}]}` |
| `GET entry?id=<uuid>` | `{ok, entry: {id, content, tags, workspace, validity_state, retracted_source, updated_at}}` |

A failed page leaves the map as it was; it never removes stars.

## Limits

- At most `graph.maxMemories` memories (3,000 by default, less what the demo and notes adapters take, Q-141),
  newest first; a memory of more than 40,000 characters or with more than 100 links is left out.
- Up to 100 links per memory are kept.
