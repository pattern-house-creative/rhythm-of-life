# Documents (`documents`)

Your own PDFs, text and Markdown files as document stars that open in the app's reader as themselves (a
PDF's pages, Markdown rendered) and that your AI finds with `search_knowledge` and `get_resource`. Two ways
in, no account needed. On in a new install: the example settings, and so `rhythm init`, turn it on; a `rhythm.yaml`
without its section has it off, and the app then offers no Add a document.

1. **Add one in the app.** In the explorer, the file button beside search (Add a document) takes one
   PDF, `.txt` or `.md` file of up to 5 MiB. Its star joins the map within a couple of minutes and
   opens in the reader.
2. **Drop files in the import folder**, in its `documents/` folder (`RHYTHM_IMPORT_DIR/documents` on
   the host, `/data/import/documents` in the engine, read-only). The engine looks every minute;
   files up to 20 MiB.

Harnesses keep `save_document` for documents they have read elsewhere; those are the same kind of
document star.

## Settings

```yaml
adapters:
  documents:
    enabled: true
    path: /data/import/documents   # the drop folder, inside the engine
    owner: household               # whose documents they are: a person id, or household
```

Turning it off removes the app's upload button (the engine stops offering `app_upload_document`) and
stops reading the folder; documents already saved stay.

`owner` decides where the folder's files and the app's uploads land. Viewer sessions (`app.viewers`, off on every
install and refused by this version) change that for one case: with memory scopes on, a document a session adds is
its person's own unless they choose Family, which puts it in the household's whatever `owner` says
([../security.md](../security.md#viewer-sessions)).

## What happens to a file

- The file is kept as it came in the engine's archive (`/var/lib/rhythm/archive`, backed up with the
  rest), and its text is read:
  - **PDF:** the text of each page, with [PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0).
    Rhythm does no OCR: a scanned PDF with no text layer becomes a star that says so. Encrypted or
    damaged PDFs are not added.
  - **.txt and .md:** the text as it is (UTF-8). A Markdown file's title is its front-matter `title`,
    else its first `# heading`; every other title is the file name.
- Text longer than 40,000 characters is kept as pages (one per PDF page) under one record, so search
  finds the page and the reader shows the whole document.
- The same file added again changes nothing. A file in the folder that changes is read again; one
  that is removed leaves the map (its archived copy stays). Uploads stay: there is no delete in the app
  yet (open question Q-045).
- A file of the folder that cannot be added (an encrypted PDF, over 20 MiB, empty) is listed with the
  reason in `get_sync_status`.

Document text is data: the engine never follows instructions in it and never logs it.

## Where documents are kept

- **The file itself** is in the engine's archive, `/var/lib/rhythm/archive` in the `engine-data` volume, exactly as it
  came, named by its checksum. `./rhythm backup` backs it up with the database ([backups](../backups.md)).
- **Its text** is in Postgres, for search (`search_knowledge`, `search_resources`, `get_resource`), your AI and the
  map's similar links.
- **A dropped file** also stays where you put it, in the import folder's `documents/`. The engine only reads that
  folder; removing a file there takes its star off the map, while its archived copy stays.

A document a harness saved with `save_document` is text only: it has no file in the archive.

## How the reader shows a document

Opening a document star in the app reads its original from the engine's archive, through the app's own server
(`/api/document-file`; the browser never reaches the engine), and shows it as itself:

- **PDF:** its real pages, drawn in the app by [pdf.js](https://mozilla.github.io/pdf.js/) (Apache-2.0), with a text
  layer, so you can select the text and a screen reader can read it. Pages fit the reader's width and are drawn as you
  scroll, on a phone or a wall screen. A scanned PDF with no text shows its pages too. Nothing in a PDF runs: no
  scripts, forms or actions.
- **Markdown:** rendered with the same safe renderer as notes, without its front matter (raw HTML shows as text).
- **Text:** as it is.

**Download** in the reader's top bar saves the original file. The saved text shows instead, with a line saying why,
when there is no file (a `save_document` copy, the app's built-in demo), when the file can't be read now or a PDF
can't be drawn, when a text or Markdown file is over 2 MiB, or when a hosted front end can't pass the file on (below).
Drive documents keep opening in Google's own viewer.

## Limits

- Only `.pdf`, `.txt`, `.md` and `.markdown`. Office files: save them as PDF first.
- Up to 5 MiB per upload (a hosted front end may allow less), 20 MiB per dropped file, 2,000 pages.
- The app shows originals of up to 20 MiB. A hosted front end (`deploy/vercel/`) passes on at most 4 MiB: a larger
  file shows its saved text there, and opens in full from the install's own address.
- The map shows at most 500 saved documents in all (with `save_document`'s), the most recently changed when there
  are more; the engine's log says when that happens. Open question Q-046.
- Uploaded and dropped documents, and their pages, are in `semantic_search` when `adapters.embeddings.sources` lists
  `documents` ([../configuration.md](../configuration.md#embedding-models)), as the example settings and `rhythm init`
  do; the engine gives them passages a few a minute after they are added (Q-044, closed). Settings written before that
  line existed list nothing, so there they are found only by `search_knowledge`, `search_resources` and
  `get_resource`, and in the map's similar links.
