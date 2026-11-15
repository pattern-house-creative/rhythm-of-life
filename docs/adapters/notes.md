# Notes folder (`notes`)

Your own Markdown notes as stars on the map. Point Rhythm at a folder (an Obsidian vault works as it
is) and every note becomes a star, links between notes become solid lines, and folders and tags
become projects. Nothing to sign up for: no account, no key. On in the example settings; `rhythm init` turns it on
only when given `--notes <folder>`, and writes it off otherwise; a `rhythm.yaml` without its section has it off.

## Turning it on

1. Say where the folder is on the host, in `deploy/compose/.env`:
   ```sh
   RHYTHM_NOTES_DIR=/home/alex/notes        # any folder; relative paths start at deploy/compose/
   ```
   Compose mounts it read-only at `/data/notes` in the engine. To try it first with the fictional
   sample, use `RHYTHM_NOTES_DIR=../../demo/notes`.
2. In `rhythm.yaml` (the example already has this):
   ```yaml
   adapters:
     notes:
       enabled: true
       path: /data/notes        # inside the engine container
       owner: household         # a person id from people, or household
   ```
3. Restart the engine (`docker compose -f deploy/compose/compose.yaml up -d`). Notes show at the next
   map rebuild (every `graph.refreshSeconds`, 15 minutes by default), or straight away with the app's
   Refresh.

On Linux the engine runs as uid 1654: the folder and its files must be readable by it (world-readable
is enough). Rhythm never writes to the folder.

## What becomes what

| In the folder | On the map |
|---|---|
| a `.md` (or `.markdown`) file | a note star. Its title is the front-matter `title`, else the file name |
| `[[Other note]]`, `[[Other note\|shown text]]`, `[[Other note#Heading]]`, `![[Other note]]` | a solid, verified line to that note |
| `[text](relative/path.md)`, `[text](<path with spaces.md>)`, `%20` in paths | a solid line too |
| a folder | a project star; its notes and sub-folders hang from it |
| `#tag` in the text, or `tags:` in the front matter | a project star `#tag` (case does not matter; `#2026` is a number, not a tag) |
| front-matter `aliases:` | other names `[[...]]` links may use |

A wikilink finds its note the way Obsidian does: by path when it has one, else by file name (the note
in the same folder first, then the one with the shortest path), else by an alias only one note has.
Links that point nowhere, links to other kinds of files, web links and anything inside code (fenced
blocks or `inline code`) are left out. Similar notes get dashed "similar" lines, on by default
(`graph.similarity`; they use the local embeddings model, so they are off while it is).

Opening a star reads the file again, so you always see its current text (front matter left out). The
reader shows it as Markdown: headings, lists and tasks, emphasis, code, quotes and links, with wikilinks and
relative links that open the linked note when it is on the map. HTML in a note shows as text, and only http,
https and mailto links open.
Rhythm keeps no copy of your notes: the map holds titles, links and a short excerpt. The one exception, when
`adapters.embeddings.sources` lists `notes` (as the example settings and `rhythm init` do): `semantic_search`
passages of each note's text, read from the file and made again when it changes ([configuration](../configuration.md#embedding-models)).

## Limits

- Hidden files and folders (a name starting with `.`, such as `.obsidian`, `.git` or `.trash`) and
  links (symlinks) are skipped, so nothing outside the folder is ever read through it.
- A note larger than 1 MiB is left out. At most `graph.maxMemories` notes (3,000 by default, shared
  with the other memory sources, Q-141), 12 folder levels and 20,000 entries are read; at most 80
  links and 19 tags count per note.
- Notes are text: their content is never followed as instructions by the engine, and never logged.
- The folder is the truth. A removed note leaves the map at the next rebuild. If the folder is not
  there at all (not mounted), the map keeps what it had.
- All notes belong to one owner. Several people's vaults are several folders, which this adapter does
  not do yet (open question Q-041).
