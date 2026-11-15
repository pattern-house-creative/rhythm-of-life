# Google Drive

The `drive` adapter shows the files in Drive folders you choose as stars on the map, and lets dashboards read them and,
if you allow it, write to Google Sheets and Google Docs. It is off by default and needs your own Google Cloud project:
Rhythm of Life has no Google app of its own, so nothing passes through anyone else's account.

## What it does

- **Stars.** Every Doc, Sheet, Slides deck, PDF, Office file and text or Markdown file under the folders in
  `adapters.drive.roots` becomes a document star. Each folder on the way down becomes a small folder star its files hang
  from. A link from one document to another Drive file (in its text or its hyperlinks) becomes a link between their stars.
  Images, video and other media are left out. With no roots, nothing is shown.
- **Nothing is copied.** Titles, addresses, versions and links are kept; a document's body is read in memory when its
  version changes, only to find its links (and, with similarity on, a similarity vector). Opening a Drive star in the app
  shows Google's own viewer, where Google checks your own sign-in. The one exception is opt-in: with `drive` in
  `adapters.embeddings.sources` (never listed by default), the engine reads each document whose text reads as words
  again for every new version and keeps that text as `semantic_search` passages, answered while the map shows the
  file's star ([configuration](../configuration.md#embedding-models), Q-097). While memory scopes are off (every install
  today), every AI client of the install then finds that text by meaning, whoever the adapter's `owner` is.
- **Dashboards.** A dashboard's `contents` feed reads a file when the dashboard opens: a Sheet as its tabs' rows of cells,
  a Doc as Markdown, other files as text, always with the file's current `revision`.
- **Memories that cite a document.** With `memory.tools` on, an AI client may say that one of Rhythm's own memories
  cites a Drive document on the map (`cite`). Rhythm then reads the file's metadata from Drive (its version, modified
  time, name and folders; never its contents) and records that revision. Each time the memory is read (`get`) it reads
  the metadata again and says plainly whether the document was edited, moved, renamed, put in the trash, deleted or
  no longer shared, or can't be read, or that Drive did not answer; `revalidate` records the current revision and says
  what moved. These reads go through the read grant, one file at a time, and change nothing in the map, its refresh or
  its stored rows ([../dev/memory.md](../dev/memory.md) 10.4).
- **Writes.** With `edits: true`, a dashboard may make the writes it declared: set cells in a range or add rows to a
  Sheet's tab, rewrite the text under a Doc heading, a Doc table cell, or add to the end of a Doc. Each write first checks
  that the file is still at the revision the dashboard read, records what it replaced, and can be undone from the app
  while nobody has changed that part since. Someone still has to turn on **Allow edits** for each dashboard in the app.

The map refreshes every `graph.refreshSeconds` (15 minutes by default) and reads at most `graph.maxDocuments` files and
folders, newest first.

## Before you start: your Google Cloud project

Do this once, in a browser, with the Google account that holds the files (or the one that administers your Google
Workspace).

1. **Create a project** in the Google Cloud console (console.cloud.google.com): any name, no billing needed.
2. **Turn on the APIs** under *APIs & Services › Library*: **Google Drive API** and **Google Sheets API** (Sheet feeds
   read cells through it), and **Google Docs API** if you will let dashboards edit Docs.
3. **Set up the consent screen** (*Google Auth Platform*, or *OAuth consent screen* in older consoles):
   - App name and support email: anything you like; only you see them.
   - Audience: **Internal** if your account is part of a Google Workspace organisation and only its members will sign in;
     otherwise **External**.
   - For External, while the app is in **Testing**, add every Google account that will sign in under **Test users**.
     Accounts that aren't listed are refused on Google's page.
   - Data access (scopes): optional. Rhythm asks for its scopes on the consent page itself.
4. **Create the OAuth client**: *Credentials › Create credentials › OAuth client ID*, application type **Desktop app**,
   then **Download JSON**. That file is the `drive_oauth_client` secret. Keep it like a password.

### Scopes Rhythm asks for

| Grant | Scopes | When | Secret file |
|---|---|---|---|
| read | `drive.readonly` | always | `secrets/adapters/drive_read_token` |
| edit | `spreadsheets`, `documents`, `drive.metadata.readonly` | only when `adapters.drive.edits` is `true` | `secrets/adapters/drive_edit_token` |

The read grant can never change anything. The edit grant is separate, so turning edits off and deleting
`drive_edit_token` removes write access without touching the read grant. Both must be the same Google account.

### The seven-day expiry

Google expires the refresh tokens of an **External** app in **Testing** after **7 days**. When that happens the Drive stars
stop updating (they stay on the map until they age out), dashboards say Google no longer accepts the sign-in, and
`rhythm connect drive` fixes it again for another week. To stop it expiring:

- an **Internal** (Workspace) app has no such limit; or
- **publish** the app (*Audience › Publish app*, status **In production**) without sending it for verification. Google
  then shows a "Google hasn't verified this app" page on every consent; as the app's owner choose *Advanced* and continue.
  Unverified apps that ask for Drive access are limited to 100 users, plenty for a household.

A refresh token also stops working when you remove the app's access in your Google account's security settings, or when
it goes unused for six months.

## Connecting

```sh
./rhythm connect drive     # from the repository root; .\rhythm.ps1 connect drive in PowerShell
```

`rhythm connect drive` asks for the folder IDs (the part after `/folders/` in a folder's address), whether dashboards may
edit, and whose files these are (a person, or `household`), and for the OAuth client JSON (pasted at a hidden prompt, or
save the downloaded file as `deploy/compose/secrets/adapters/drive_oauth_client` first). Then, for each grant:

1. It prints Google's consent address. Open it in a browser on **any** computer (it doesn't have to be the server) and
   sign in with the account that holds the files.
2. Allow access. Tick every box if Google shows them one by one.
3. Google sends the browser to `http://127.0.0.1:53682/?state=...&code=...`. Nothing listens there, so the page doesn't
   load; that is expected. Copy the **whole address** from the address bar and paste it into the terminal.

The code in that address works once, for a few minutes. Rhythm exchanges it (with PKCE and a check that it belongs to this
attempt) for a refresh token and saves it as `secrets/adapters/drive_read_token` (and `drive_edit_token`), readable only
by its owner. The engine picks it up on its next Drive call; no restart is needed.

- `rhythm connect drive read` or `rhythm connect drive edit` renews one grant only.
- `rhythm connect drive edit` needs `edits: true` first.

## Settings

```yaml
adapters:
  drive:
    enabled: true
    roots: [example-folder-id]                    # folder IDs; up to 20; a file ID also works
    edits: false                                  # dashboards' declared writes to Sheets and Docs
    owner: household                              # a person id, or household
```

| Secret file (under `deploy/compose/secrets/adapters/`) | What |
|---|---|
| `drive_oauth_client` | the Desktop app client JSON from your Google Cloud project |
| `drive_read_token` | the read grant, written by `rhythm connect drive` |
| `drive_edit_token` | the edit grant, only with `edits: true` |

Each can also be given by path in `RHYTHM_DRIVE_OAUTH_CLIENT_FILE`, `RHYTHM_DRIVE_READ_TOKEN_FILE` and
`RHYTHM_DRIVE_EDIT_TOKEN_FILE`. A replaced file takes effect on the next call.

## Limits

- Files shared with you but not inside one of your roots are not shown. Shared drives work when a root is one of their
  folders.
- At most 1,000 folders are walked per refresh, and 400 changed documents are read for links per refresh (the rest on the
  next one). Files over 25 MiB are not read for links.
- A Sheet feed holds up to 20 tabs and 50,000 cells; a text feed up to 200,000 characters.
- Only native Google Sheets and Docs can be written. A Word or Excel file in Drive must be saved as a Google file first
  (*File › Save as Google Docs/Sheets*).
- Drive's own permissions still apply: Rhythm sees what the signed-in account can see, and the app's viewer shows a file
  only to someone whose own Google account can open it.
