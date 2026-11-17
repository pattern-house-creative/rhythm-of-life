# Live screens: the contract (pieces 1, 2 and 3)

Every open copy of the app is a named **screen**. An AI client (through two harness tools) or another screen (through
"Show on…") sends a **command**; the engine checks it, stores it and wakes the screen's waiting request; the screen
applies it within about a second, shows a small note naming the sender with Undo, and reports back. Commands only change
what a screen shows. They never edit data, except `corner_card`, which sets the household's corner card exactly as the
corner-card picker does today, and, from piece 3, `move`, `resize` and `focus_map` on a dashboard, which change how that
dashboard is arranged for the whole household (section 10), never its page, feeds or writes.

This file is the agreement between the engine, the app and the docs for piece 1, "send to a screen", piece 2 (full
screen and Home, section 8) and piece 3 (composable layouts, section 10): Home and dashboards become grids of parts, the
assistant's page one of them and the app's own map and tiles the others. Where this file and the code disagree, fix one
of them in the same change.

## 1. Words

| Word | Meaning |
|---|---|
| screen | one browser running the app. Its id is random, made by the browser once and kept in `localStorage` |
| open screen | a screen the engine has heard from (`listen`) in the last 60 seconds |
| command | one typed action for one screen, stored in `screen_commands` |
| sender | who sent a command: `{kind:"assistant"}` for a harness caller, `{kind:"screen", screen, name}` for another screen. Names are labels the browser chose, never identity |
| showing | what a screen reports it shows now (section 5) |

There is no new login: anyone who can open the app can control its screens, and an AI client uses the harness login it
already has. All screens reach the engine as the one app principal, so the screen id is always an argument. (Once
viewer sessions are on, a screen in one reaches it as that session's own app login, and a screen's id belongs to the
login that first listened with it, 3.2.)

## 2. Commands (piece 1)

| `action` | Arguments | What the screen does | Lasts |
|---|---|---|---|
| `open_star` | `star` (graph id) | Opens the explorer in Focus on that star, clearing any search. A dashboard star (`<catalog provider>:dashboard:<uuid>`) is turned into `open_dashboard` by the engine | until the next command or Home |
| `open_dashboard` | `dashboard` (uuid) | Opens that dashboard in the reader, as a card's "open" does | until the next command or Home |
| `home` | none | This screen's Home: the sky and tiles, or its Home dashboard full screen (section 8) | |
| `layout` | `layout`: `corners`, `hero` or `band` (shown as Corners, Tall column, Rhythm band) | Switches the Home layout and saves it for this screen | kept on this screen |
| `move_tile` | `tile` and `to`, each `main`, `recovery`, `training` or `changed` | Swaps two Home tiles: `tile` moves to the place `to` names, and whatever was there takes `tile`'s old place. Places are named after the tile that sits there by default (default layout: `main` top left, `recovery` top right, `training` bottom left, `changed` bottom right) | kept on this screen |
| `corner_card` | `corner` (`main`, `recovery`, `training`, `changed`) and `dashboard` (uuid, or absent/null to clear) | Sets or clears that corner's dashboard card for the household, through the same call the picker uses | household-wide, as today; on a viewer session's screen with `memory.scopes` on, that person's own corner (migration 030, SB-P1-20), which Undo drops again where the corner showed the household's (032, section 6) |
| `search` | `query`, 1-100 characters | Opens the explorer in Focus with the search box filled and the matching stars lit. Reports how many stars matched and the first 10 titles | until the next command or Home |
| `undo` | none | Reverses the last command applied on this screen (section 6) | |

A star, dashboard or search opened by a command does not close after the usual 2 idle minutes: it stays until the next
command or `home`, or until someone uses that screen, which turns the normal idle return back on. `layout` and
`move_tile` are saved even on a screen too narrow to show layouts; the report says so (`detail.note`).

## 3. Engine

### 3.1 Tables (migration `020_screens.sql`)

```sql
-- One row per browser that has run the app.
CREATE TABLE screens(
  id text PRIMARY KEY CHECK (id ~ '^[A-Za-z0-9_-]{16,64}$'),
  name text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 60),
  created_at timestamptz NOT NULL DEFAULT now(),
  seen_at timestamptz NOT NULL DEFAULT now(),
  showing jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(showing)='object' AND length(showing::text)<=4000),
  settings jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(settings)='object' AND length(settings::text)<=2000));
-- One row per command, kept 30 days.
CREATE TABLE screen_commands(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  screen_id text NOT NULL REFERENCES screens(id) ON DELETE CASCADE,
  action text NOT NULL CHECK (action ~ '^[a-z_]{1,32}$'),
  args jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(args)='object' AND length(args::text)<=2000),
  sender jsonb NOT NULL CHECK (jsonb_typeof(sender)='object' AND length(sender::text)<=500),
  sent_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  delivered_at timestamptz,
  done_at timestamptz,
  result text NOT NULL DEFAULT 'pending' CHECK (result IN ('pending','delivered','shown','failed','expired')),
  detail jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(detail)='object' AND length(detail::text)<=4000));
```

Plus indexes for "oldest pending command of a screen" and for pruning by `sent_at`. The action list is checked in code,
not by the table, so pieces 2 and 3 add actions without a migration. Follow the conventions of migrations 013-019:
a header comment, `REVOKE ALL ... FROM PUBLIC`, SECURITY DEFINER functions gated by `dashboard_reader(p_principal,
ARRAY[...], '...')` with the search path pinned, errors raised with `22023` (bad input), `P0002` (unknown) or `42501`
(role). The database is the source of truth; the wait (3.3) only decides when to look again. Document 020 in
`docs/dev/migration-compat.md` and `docs/dev/engine.md`.

### 3.2 Rules the engine enforces

- Actions and their arguments exactly as in sections 2 and 8: unknown actions, missing arguments and arguments the action does
  not use are refused (400 / `invalid_arguments`, or an `isError` naming the argument).
- `star` must be a star on the map now (`RelationshipHost.Known`), or a dashboard star whose dashboard exists (then it
  becomes `open_dashboard`). `dashboard` must exist. `query` is trimmed, 1-100 characters, no control characters.
  Names and searches are counted in characters (Unicode scalar values) by the engine and the app alike, as the table
  counts them: an emoji is one.
- The target must be an open screen. A command not delivered within **30 seconds** expires (`result='expired'`) and is
  never delivered later.
- At most 20 pending commands per screen; at most 100 screens per app login (a new screen replaces the one its own
  login has seen least lately, never another login's; with one login, every install's today, that is every screen).
- Daily (the scheduler's daily block): delete commands sent more than 30 days ago and screens unseen for 180 days.
- A screen's name as section 8 gives it: a new screen's numbered when another screen has it, and a listen never
  strips that number from the stored name; `rename` sets any valid name.
- Times are recorded on every command (`sent_at`, `delivered_at`, `done_at`), each by the clock when that step happens
  (`clock_timestamp()`, never a transaction's start, so a delay is never negative), and answers carry
  `ms: {deliver, show}` in milliseconds from `sent_at`, so the real delay is visible.
- Refusals say why in the error's message. A target that is not open and an unknown star, dashboard, screen or command
  are 404 `not_found`; a full queue, input out of bounds and a `done` for a command that is not waiting for one
  (reported already, or expired) are 400 `invalid_arguments`; another login's screen is 403 `forbidden` (below).
- A screen's id is the app login's that first listened with it (`screens.bound_to`, second-brain's migration 029,
  `docs/dev/memory.md` 7.5, SB-P1-19), whatever `memory.scopes` says. Only that login listens as the screen (and so
  takes its commands), reports for it (`done`), sends as it (`send`'s `screen`) and renames it, so the `settings` a
  `listen` and a `rename` answer, a Home dashboard's id among them, reach that login alone. Any other app login is
  refused before anything is written or claimed (`42501` in the database, 403 `forbidden` at `app_screens`, naming
  nothing about the screen), and a listen counts as its screen listening only once its first look is answered, so a
  refused one never makes the screen seem to listen. The household view is one login here: a screen the shared app
  token's login made is every such login's, a new token's included. A screen from before 029 is the household view's
  alone until its next listen from the household view binds it, which is never refused for it. Another login's new
  screens never push a bound screen out (the cap above is per login), so its id is never free to take. A viewer
  session's screen stays that session's once it has ended. While `app.viewers` is off every app login is the household view's, so nothing is refused and every answer is
  as above. A browser whose login changed (a viewer session started or ended in it) is refused for the screen it
  listened as, and then takes a new screen id and listens as a new screen of its new login (sections 4 and 6).
- With `memory.scopes` on (second-brain's migration 027, `docs/dev/memory.md` 7.5; never on today), what a screen shows
  and reports is the view of the app login it listens with: the household view's (the shared app token's) or a viewer
  session's (an app login bound to one person, SB-P1-11). The engine keeps which login last reported what each screen
  shows and each command's outcome (`screens.shown_by`, `screen_commands.reported_by`), and:
  - reads every dashboard a command names as its sender (`DashboardStore.Get`), so one it can't read is refused exactly
    as an unknown one (404, "Unknown dashboard."), as a star it can't read already is;
  - lets a command for a screen whose login doesn't read all its sender reads (another person's session, or the
    household view's screen for a person's client) carry only what that login reads: its star, focus star and
    dashboard are refused otherwise (404, "That screen can't show that: it reads another scope than yours."), and a
    `search` sent from a viewer session goes only to that person's own screens (400);
  - sends `undo`, which names nothing but has the screen save its earlier arrangement back through its own login, only
    from a sender that reads all the screen's login reads (404, "That screen's last change is in another scope than
    yours, so it can't be undone from here."), so no one undoes a change to a dashboard they can't read;
  - tells `list_screens` and `app_screens list` of each screen's `showing` only the star, dashboard and Home dashboard
    the reader reads now (a record that has left its scopes since the screen reported it included) and a dashboard's
    `parts` with their dashboard, and the `query` only to a reader that reads all the screen's login reads (that
    person's own logins, or for the household view's screen every reader of Family);
  - answers a command's outcome to any other reader with `detail: {summary}` saying only that it is not passed on.

  With scopes off none of this is read and every answer is as above.

### 3.3 The wait

One in-memory singleton (the engine is one process) holds a wake signal per screen and per command. `send` wakes the
target screen's waiting `listen`; `done` wakes whoever waits for that command. A wait never holds a database connection
and always looks at the database again after waking or timing out. When the engine stops, every wait ends as if its
time ran out (a `listen` answers `command: null`), so a stop never waits on a held request. A restart drops waiters;
the screens simply listen again.

### 3.4 App tool `app_screens` (role `app`)

One tool, with `op` choosing the shape. Every answer is `200` JSON on success. Errors use the existing app error shape.

| `op` | Arguments | Answer |
|---|---|---|
| `listen` | `screen`, `name`, `hold` (0-25 s, default 25), optional `showing`, optional `settings` (checked as in section 8) | Upserts the screen (name, numbered for a new screen and never stripped of that number as in section 8, `seen_at`, and `showing`/`settings` when given), expires overdue commands, and claims this screen's oldest pending command (`result='delivered'`, `delivered_at` set). With none, waits up to `hold` seconds for one, then looks once more, updating only `seen_at` (a rename made meanwhile stays). `{command: Command \| null, screen: {id, name, settings}}`, the name as stored. An empty wait is a normal `200` with `command: null` |
| `done` | `screen`, `command` (uuid), `result` (`shown` or `failed`), optional `detail`, optional `showing` | Records the outcome of a delivered command of this screen, and `showing`. `{ok: true}` |
| `send` | `screen` (the sender's own id), `to`, `action`, and the action's arguments as one object `args` (or beside the others, never both; not for `move_tile`, whose own `to` would be taken for the target) | Checks as in 3.2, stores the command with sender `{kind:"screen", screen, name}`, wakes `to`, then waits up to 8 s for its outcome. `{command: Command}` |
| `list` | `screen` | Screens seen in the last 24 hours, most recent first: `{screens: [{id, name, open, seenAt, showing, self}]}` |
| `rename` | `screen`, `name` | `{screen: {id, name, settings}}` |

`Command` is `{id, action, args, sender, sentAt, deliveredAt, doneAt, result, detail, ms}`, null fields included.
`listen`, `done`, `send` and `rename` act as `screen`, which must be the calling login's (3.2); `list` only marks it
`self`.
Add `contracts/mcp/app_screens.output.schema.json`, a fixture in `contracts/fixtures/`, and the tool to the engine's
`Known`/`Tools`/`Allowed`, the schema test list and `scripts/contracts.test.mjs`.

### 3.5 Harness tools

- `list_screens` (read-only): `{screens: [{id, name, open, seenAt, showing, home}]}` for screens seen in the last 24
  hours, open ones first, `home` as in section 8. Lets an AI client say what each screen shows.
- `control_screen`: `action` (enum of sections 2 and 8) plus `screen` (optional: an id or a name, in any case; an exact
  id first, then an id in another case when only one matches), `star`, `dashboard`, `layout`, `tile`, `to`, `corner`,
  `query` as the action needs. Without `screen`, the target is the open screen a person used last (section 8, Q-147);
  with none open the answer says so; a name matching two open screens is refused, listing them. Sender
  `{kind:"assistant"}`. Waits up to 8 s for the outcome and answers `{screen: {id, name}, action, result, detail, ms}`,
  where `result` is `shown`, `failed`, or `waiting` (sent, not confirmed yet).

Both need `ToolText` templates; descriptions name no product or host (the existing tool-list test checks this). Update
the harness tool contract array in the tests and the tool counts in the docs.

## 4. App server and hosting

- `POST /api/screens` (`server/screens.ts`, registered in `routes.ts`, with `api/screens.ts` for Vercel exporting POST
  and OPTIONS). Body `{op, ...}` with an exact key allowlist per op, mirroring 3.4. `requestGuard` applies. In demo mode
  (no engine) it answers `200 {available: false}` and the browser keeps screens itself (section 7). So does an install
  whose engine does not list `app_screens` (one older than the app), and then there are no screens.
- The facade passes on `showing`, `settings` and `detail` with only their own fields (sections 5 and 6): titles and
  plain words cut to 200 characters, the search to 100, at most 10 titles, and `showing.activeAgo` as `activeAt` by
  its own clock (section 8). A `send` reaches the engine with the action's arguments as `args`, whichever way the
  browser sent them.
- Engine call limits: `app_screens` gets its own `limits()` entry, 35 s per try and in total, and is never retried.
- Timeout chain, each longer than the one before: engine hold 25 s < app server 35 s < browser `read()` 40 s < Vercel
  `maxDuration` 60 s (`vercel.json`: `"functions": {"api/screens.ts": {"maxDuration": 60, "supportsCancellation": true}}`).
- While viewer sessions are on (`app.viewers`), a `listen` the engine refuses as another app login's screen (403,
  3.2) answers `200 {taken: true}`, never a 403 (which would read as lost access), and the browser takes a new id
  (section 6); `done`, `send` and `rename` fail as before. While they are off no login is refused, and a 403 is the
  failure (502) it always was.
- An abandoned request must end its engine call: the browser's disconnect reaches `request.signal` in `server/node.ts`
  (`toRequest`) and `server/devApi.ts`, and on Vercel only because `supportsCancellation` is set.

## 5. What a screen reports (`showing`)

`{view, star?, dashboard?, query?, layout, tiles, narrow, home?, activeAt?}` (`home` and `activeAt` in section 8; the
browser sends `activeAgo` in place of `activeAt`), at
most 4000 characters as the database stores it (its text form spaces the JSON out a little, so keep well below), titles
cut to 200 characters:
- `view`: `home`, `star`, `dashboard`, `search`, `full_screen` (section 8) or `other`.
- `star` / `dashboard`: `{id, title}` of what is open.
- `query`: the active search.
- `layout`: `corners`, `hero` or `band`; `tiles`: the place of each tile (`{main: "recovery", recovery: "main", ...}`);
  `narrow`: true when the screen is too narrow to show layouts.

`settings` (sent on the first `listen` and after a change) is `{layout, tiles, home}` (section 8). The browser keeps
its own copy in `localStorage` so the app still opens offline. A browser that kept its screen id but lost its layout
and tiles takes the engine's copy, unless someone changed them there first; the next listen, which may leave before the
app has drawn them, never sends back the ones they replaced. Clearing a browser clears its id too, so it starts as a new
screen with the default settings.

## 6. The app

- **Identity.** `localStorage["rhythm-of-life.screen"] = {id, name, chosen}` (`chosen` from piece 2, section 8). The id
  is 16 random bytes in base64url. The default name comes from the device ("iPhone", "iPad", "Android phone", "Android
  tablet", "Mac", "Windows PC", "Linux PC", "Chromebook", else "Browser"), and from piece 2 the browser too. The
  Details panel, next to the layout picker, gets a "This screen's name" field that renames it. With an engine, the
  browser's other tabs follow a rename (the `storage` event, and a look at the identity whenever the page is shown),
  so their listens carry the new name.
  A listen answered `{taken: true}` (3.2: this browser's login changed) has the app take a new id, keeping a name a
  person chose (a default name starts again from the device's, which the engine numbers anew), and listen at once as
  that new screen, its first listen carrying this browser's settings; the Undo stack starts empty, since what it held
  was done as the other login's screen. The browser's other tabs, refused in turn, take the id the first one stored. An
  id taken this way and refused again before any listen with it was answered is not replaced (the listen backs off as
  after any failure), so a refusal for another reason never takes one id after another.
- **Listening.** A hook keeps one `listen` waiting while the app has a session, the page is visible and the browser is
  online, and stops on hidden, `pagehide`, offline and `observatory-access-lost`, as `useMapRebuild` and
  `useDashboards` do. It backs off after errors (1, 2, 5, 10, then 30 s). It sends `showing` with each `listen`, and
  for a touch a short one beside it (section 8). Commands are applied and reported one at a time, whichever listen
  claimed them.
- **Applying.** A runner applies each command through App's own functions (`selectNode`, `openDashboard`, `close`,
  `setLayout`, the corner-card setter, and new ones for tiles and search), reached through refs so it always calls the
  current version. The explorer gets a request prop (with a nonce) so a command can open a star, a dashboard or a search
  while it is already open; opening a star clears any search so the star panel shows. A star or dashboard missing from
  the screen's map (the bounded overview read every 15 minutes, plus what it has reached since; the engine checks its
  whole map) is fetched before the screen reports `failed`: a star comes with its neighbours, a dashboard by reading the
  dashboards again. What a command opened stays on the map until Home, and a refresh fetches it again; Undo fetches what
  it goes back to the same way. `corner_card` reports `failed`, changing nothing, while the dashboard is not among the
  screen's saved dashboards. Then it reports `done` with `result`, `detail` (`{summary}` in
  plain words, and for `search` `{matches, titles}`) and `showing`.
- **The note.** A small non-modal note, outside the `inert` main content so it stays clickable, reads
  "From your assistant: Opened Training plan" (or "From the screen “Kitchen iPad”: …", so a screen can never read as
  the assistant) with an Undo button, and fades after 10 s.
- **Undo.** Before each command the runner saves what it is about to change (view, star, dashboard, search, layout,
  tiles, the corner's previous card) on a stack of at most 20, in memory. On a viewer session's screen with
  `memory.scopes` on, a corner that showed the household's (none of the session's own, the cards' `own`) is saved as
  no choice of its own, and Undo drops the session's choice with the nil uuid, so the corner follows the household's
  again (second-brain's migration 032, SB-P1-20). `undo` (or the note's button) restores the
  last one. With nothing to undo it reports `failed`, `detail.summary` "Nothing to undo".
- **Tiles.** A per-screen tile map in `localStorage` (`rhythm-of-life.tiles`) gives each tile its place; each tile's
  `grid-area` follows the map (place `changed` is grid area `context`). The default map changes nothing on screen.
- **Work items on the sky.** With the engine's `memory.work` on, a memory that is a work item is a star drawn as a
  small box (a dot in it while the work is open, a tick once done, a bar when cancelled; finished work is quieter), named
  as a work item to a screen reader. Sources & settings has "Work items on the sky", on by default and kept per screen in
  `localStorage` (`rhythm-of-life.work-items`), followed by the browser's other tabs (`watchSettings`); it is offered
  only while the map has a work item, or while they are hidden on that screen. Like the automatic links it is display
  only and never reaches the engine: off, no sky of that screen (Home's, a dashboard's map part, the explorer's) draws a
  work item's star or a link to one (`graphView.ts` `withoutWork`), so a hidden star takes no room either; the chosen
  star and pinned ones are still drawn, the explorer's lists and a star's panel still read them, and the Memories view
  lists every one. Nothing is deleted or changed.
- **Lines on the sky.** Sources & settings has "Automatic links on the sky" (first "Similar links on the sky"), on by
  default and kept per screen in `localStorage` (`rhythm-of-life.similar-links`, the first name's key, so an "off" chosen
  then holds); the browser's other tabs follow it (`watchSettings`). Unlike the layout and tiles it is display only and
  never reaches the engine. Off, a sky draws no dashed line: similar, topic, plugin and Second Brain links and every other
  link nobody confirmed (`graphView.ts` `dashedLink`, the one rule for dashing and hiding). A calm sky (Home's, a
  dashboard's map part and the explorer's Focus) also draws no folder or project line at rest, and draws every line
  thinner and fainter; the chosen star's own lines, and on a mouse or pen a hovered star's, are lit and the rest dim,
  easing over 0.3 s (at once with reduced motion). The chosen star always draws all its own links, so a chosen folder or
  project draws its members' lines. Trace and Discover keep every folder and project line and their earlier look, and
  Discover draws every suggestion whatever the switch says. A star's panel lists every link, and the layout reads every
  link, so turning the switch, choosing a star or hovering over one moves no star.
- **Show on…** In a star's panel (`item-actions`), a "Show on…" button lists the other open screens inline, in the panel
  (no popup). Choosing one sends `open_star` (`open_dashboard` for a dashboard star) and says inline "Shown on Kitchen
  iPad", "Sent to Kitchen iPad, not confirmed yet" or why it failed: the facade maps the engine's known refusals (a
  closed screen, a full queue, a star off the map, an unknown dashboard or sender) to the app's words, and anything else
  to the general reason. With no other screen open it says so.

## 7. Demo mode

Without an engine the browser plays the engine's part: screens in the same browser find each other over a
`BroadcastChannel`, with the same commands, checks, expiry, note and Undo, and nothing stored. Each tab is its own demo
screen: its id is the browser's id plus a per-tab suffix kept in `sessionStorage`, so two tabs can command each other.
Where `BroadcastChannel` is missing (tests), screens are simply off. The demo never calls `/api/screens`.

With an engine, the tabs of one browser share its screen (one id in `localStorage`). One visible tab listens at a time,
holding a Web Lock named after the screen, and the others stand by, so a command and its Undo reach the same tab. Without
Web Locks each visible tab listens and the first to claim a command gets it. Undo is per tab.

## 8. Piece 2: full screen and Home

Piece 2 adds two commands, a per-screen Home choice, and two fixes found in piece 1's end-to-end test. Everything else
above stays as it is, except where this section says otherwise.

| `action` | Arguments | What the screen does | Lasts |
|---|---|---|---|
| `full_screen` | `dashboard` (uuid), optional | Shows a dashboard filling the whole display: no sky, tiles or panels, only the dashboard's page (its sealed frame unchanged) and a small exit control that appears when the pointer moves or the screen is touched; Escape also exits. Without `dashboard`, the dashboard open on that screen; with none open it reports `failed`, "No dashboard is open" | until `home`, the next command, or someone exits |
| `set_home` | `dashboard` (uuid), or absent/null | Makes that dashboard this screen's Home: whenever the screen goes Home (the `home` command, the idle return, the close button) it shows that dashboard full screen. Absent or null restores the default Home (the sky and tiles) | kept on this screen until restored |

- **Settings** become `{layout, tiles, home}`, where `home` is `null` (the default Home) or `{dashboard}`; kept like layout
  and tiles (in `localStorage` as `rhythm-of-life.home`, and on the engine). `app_screens listen` checks them: each
  member optional (a new screen keeps nothing yet, one older than piece 2 keeps no home), `layout` one of the three,
  `tiles` each tile in exactly one place, `home` null or `{dashboard: uuid}`, and nothing else. The dashboard is not
  checked to exist: a Home deleted since must fall back, not break the screen's listen. The facade still takes `{layout,
  tiles}` from an older cached bundle.
- **What a screen shows.** `showing` adds `view: "full_screen"` with `dashboard` the one shown, and `home: {dashboard:
  {id, title}, missing?: true} | null`, the title from the saved dashboards (or the dashboard's star on the sky until
  they are read). A Home dashboard that has been deleted (the engine's whole list was read without it) falls back to the
  default Home and reports `missing: true`, title `""`; one not read yet is shown and says itself if it can't be. Only a
  list the engine answered, shorter than the 100 dashboards it lists at most (the most recently changed), tells:
  `/api/dashboards` answers a failed or unreadable read as 502, never as an empty list, and a failed read, or one of
  100, leaves the Home shown. `list_screens` adds each screen's `home` from this, `missing` included, or null when it
  reports none.
- **Engine.** Both actions join the checked list, `control_screen`'s enum and `app_screens send`. A named dashboard must
  exist. `full_screen` without `dashboard` is stored as `{}`; null is refused, as for `open_dashboard`. `set_home`
  without one, or with null, is stored as `{dashboard: null}`, as `corner_card` clears.
- **App.** The dashboard reader and the dashboard panel get "Full screen" and "Set as Home here" (inline, no popup); the
  panel's earlier "Full screen", which opened the explorer's reader, is now "Open in the explorer". The Details panel,
  next to the layout picker, always says what this screen's Home is and offers "Restore default Home". Undo covers both
  commands. The full-screen view follows the app's phone and desktop rules, and keeps its data fresh as the dashboard
  does today. A screen takes `full_screen` and `set_home` only for its saved dashboards (read again once, as for
  `corner_card`), and reports `failed` otherwise.
- **Full screen** sits over whatever is open. Exiting (its button, Escape, Back; for the Home dashboard's, not Back)
  returns to what is under it: the Home dashboard when it replaced that, else the view or the sky beneath. Undo brings
  back both. `open_star`, `open_dashboard`, `search` and `home` replace
  it; `layout`, `move_tile` and a `corner_card` that changed the card end it so the change shows; `set_home` leaves a
  full screen of another dashboard alone. The page's frame stays sealed, so the app hears nothing over it: while the exit control is hidden, a clear
  layer over the page takes the first movement, touch or scroll and shows the control (that first tap does not reach
  the page). A tap into the page counts too, and while the page keeps focus the control stays, the layer away, for a
  minute after the last sign. The browser's Fullscreen API is not used: a command has no user gesture to start it. A
  full screen that can't open (its code didn't load, or it broke while drawing) gives way to what is under it by
  itself, with no alert to dismiss, and is not tried again by itself until the app reloads: going Home skips a Home
  dashboard that couldn't open, as it does a deleted one, and the Details panel says so. A command or a tap may still
  ask for it again.
- **Home.** The app opens at its Home dashboard. Going Home (the `home` command, the idle return, closing a view, Escape
  from a view, the brand link, Back from a view) shows it full screen. Exiting it shows the sky, which drifts back to it
  after the usual 2 idle minutes (Q-155); Escape on the plain sky does nothing. Chosen while the screen is at Home, a
  new Home shows at once. With an engine, a layout, tiles or Home chosen in one tab of a browser shows in its other tabs
  too, and whichever tab listens sends it on; only a new Home changes what such a tab shows (at Home), a layout or tiles
  change alone leaves it as it is.
- **History.** The app keeps a history entry for an open view and one for a full screen over the sky or a view, so Back
  steps back through them as above instead of leaving the app, and what closes otherwise takes its own entry off again.
  The Home dashboard's full screen is Home itself and keeps none: there, as on the sky, Back leaves the app.
- **Distinct default names.** A default name names the browser too ("Windows PC · Chrome", "iPhone · Safari"; the
  device alone when the browser can't be told). When a screen first registers (an id the engine has no row for) with a
  name another screen seen in the last 30 days already has, compared trimmed and in any case, the engine adds the lowest
  free " 2", " 3"..., cutting the name to fit 60 characters. A known screen's listen stores the name it sends, except
  that it never replaces a stored numbered name ("Windows PC · Chrome 2") with its bare one ("Windows PC · Chrome",
  compared and cut as for numbering): a listen may leave before its browser took the number. `rename` still sets any
  name, the bare one included. The listen answer carries the stored name and a browser still on its default name (or
  on one the engine numbered) adopts it at once: the next listen and a touch's carry it, though the app has not drawn
  it yet, and a render still showing the old name does not put it back. (Sending the old name back made the stored name
  flip between the two on every listen until a reload.) The identity's `chosen` says whether a person chose the name
  (in Details), and a name a person chose is never changed by an answer: its listens keep sending it, and a tab that
  missed another tab's rename (the `storage` event; a page kept in the back-forward cache hears none) takes it from
  the browser's identity when it is shown again, or from the next answer at the latest. Since a listen never strips a
  number, while the engine keeps another name than the chosen one (a rename that never reached it, a listen that left
  before the rename and arrived after it, or a number the engine gave a screen it no longer knew), the listening tab
  sends the chosen name again as a `rename`, at most once a minute, until an answer carries it. An identity from piece
  1 whose name is its device's old default takes the new default (an update, so it is not numbered, and two such
  screens can share a name); any other piece 1 name counts as chosen. The demo never numbers.
- **Default target (Q-147).** `showing` adds `activeAt`: when a person last touched that screen (pointer, key or touch;
  not commands), an ISO 8601 time; it is not kept across reloads, so a screen nobody touched since it opened reports
  none. One clock sets it: the browser sends `activeAgo`, the whole milliseconds since that touch when it sends what it
  shows (any `activeAt` of its own is left out), and the facade passes it on as `activeAt` by its own clock (the demo
  by the browser's, as it plays the facade's part). A touch while a listen waits goes to the engine in a short listen
  beside it (`hold` 0), at most every 10 seconds, so it reaches the engine within 10 seconds; the waiting listen is
  never cut short, since the engine may already have given it a command on its way to the screen, and a command the
  short one claims is applied in turn like any other. Without `screen`, `control_screen` targets, among the open
  screens that still listen (a listen of it under way in the engine, or one answered in the last 10 seconds; any open
  screen when none does), the one with the latest `activeAt` (one without an offset is read as UTC, one later than the
  screen's `seenAt` counts as `seenAt`, anything else as none), then the one seen most recently. A screen whose app
  was hidden or closed ends its listen, so it stops counting at once, though it stays open for a minute. The tool text
  still says to name the screen when several are open.

## 9. Not in pieces 1, 2 and 3

A voice agent, TVs, a private mode for shared rooms (later). Next to piece 3 but not in it: adding or removing a
dashboard's parts by command or by hand (an AI client does that with `save_dashboard`, 10.7); a page that drives native
parts, the layout or a screen (the frame's bridge stays exactly as it is); tags in a map focus (the map has none yet,
10.4); a Home grid on narrow screens and phones (they keep today's fixed Home, 10.5); arranging by hand on a folded
screen (commands still apply there, 10.6.3); layout history beyond Undo and restoring a version; a dashboard arranged
differently for each person or screen.

## 10. Piece 3: composable layouts

Piece 3 makes Home and every dashboard a **grid of parts**. A dashboard's page, the one an AI client writes, becomes one
part, still in its sealed frame. The app's own working pieces (the map, the health tiles, the daily insights) become
**native parts** that the app draws around the page, never inside it. Parts move and resize in place, by command or by
hand, without writing a new page or a new version. Everything in sections 1-8 stays as it is, except where this section
says otherwise.

### 10.1 Words

| Word | Meaning |
|---|---|
| part | one rectangle of a grid showing one thing: the page, the map, a tile, another dashboard's card |
| native part | a part the app draws itself from its own data: every kind except `page` and `card` |
| layout | a dashboard's grid, `{parts: [Part, ...]}` (10.2, 10.3), kept with a version. Not Home's `layout` setting, which names a preset (section 2) |
| Home grid | one screen's arrangement of Home's five parts, `{parts: [{id, x, y, w, h}, ...]}` (10.5) |
| cell | one twelfth of the grid's width by one twelfth of its height |
| focus | what a map part narrows its sky to (10.4) |
| layout revision | a change to the newest version's layout made in place, without a new version (10.7); revision 0 is the layout the version was saved with |
| band | the parts beside a part that trade sides with it as one when it moves (10.6.1) |
| fold | how a dashboard's grid is drawn when its box is too small to draw it as saved (10.2). Home never folds |

### 10.2 The grid

- The grid is **12 columns by 12 rows of cells** filling the box it is drawn in: the reader's page area, a full screen,
  or Home's area under the app bar. A part is placed and sized in whole cells: `x` and `y` (0-11) are its top left
  cell, `w` and `h` (1-12) its width and height, with `x + w <= 12` and `y + h <= 12`. The app's gap sits between every
  two tracks (CSS grid `gap`: Home's gaps at Home, the reader's spacing in a dashboard's hosts), so a part `w` columns
  wide spans `w` columns and the `w - 1` gaps between them. There are no borders or card frames between parts.
- Every layout keeps these rules, checked by the engine and the app alike (a Home grid keeps them with Home's own parts
  and smallest sizes, 10.5):
  - 1-12 parts, each with a unique `id`: a lowercase letter, then up to 31 lowercase letters, digits or underscores
    (`^[a-z][a-z0-9_]{0,31}$`).
  - No two parts overlap. Cells that no part covers are allowed: they show the surface behind, and nothing is drawn
    there.
  - Each part is at least its kind's smallest size (10.3).
  - A dashboard's layout has exactly one `page` part (a version always has its page, as today), at most 2 `map` parts,
    at most 4 `card` parts, and at most one part of each other kind.
  - A layout is at most 8,000 characters as the database stores it.
- **Reading order** is by `y`, then `x`, then `id`: the top row first, left to right. Folds, the arranging announcements
  and `showing.parts` follow it; keyboards and screen readers follow it as 10.9 says.
- **Folding** applies to dashboards only. The app measures the box, not the window. A layout whose only part is a page
  covering the grid never folds: it draws as today.
  - **As saved** while every part gets at least its kind's smallest size in pixels (10.3).
  - **Two columns** when that fails and the box is at least 560 px wide. Parts flow in reading order. A part 7 or more
    columns wide spans both columns; the others take one. Each part takes its kind's folded height (10.3); a row is as
    tall as its taller part, and the shorter one fills its cell with its content centred.
  - **One column** when that fails and the box is narrower than 560 px (phones). Parts stack full width in reading
    order, each at its folded height. A narrow box where every part still gets its smallest size draws as saved.
  - In a fold, the grid host is the scroll container; its own host (the reader, the dashboard panel, full screen)
    stays unscrolled as today. A map part never takes a vertical swipe from it (10.9), and a page's frame never covers
    more than 70% of the box, so there is always room to scroll past it.
  - A fold is drawn, never stored. The layout stays as saved, and each screen folds it to fit its own box. Nothing is
    arranged by hand on a fold (10.9).
- Parts are placed only with CSS (`grid-column`, `grid-row`). Each part's element is keyed by its id and mounted once,
  in reading order as the grid first draws, and moving, resizing or folding never moves it in the DOM, so nothing is
  mounted again: a page's frame never loads twice (its one-load rule stays), and a map never settles again because a
  neighbour moved.

### 10.3 Parts

A part is `{id, kind, x, y, w, h}`, plus `focus` for a `map` (optional) or `dashboard` for a `card` (required), and
nothing else.

| `kind` | What it shows | Arguments | Smallest, in cells | Smallest, in px (about) | Folded height |
|---|---|---|---|---|---|
| `page` | the dashboard's own page in its sealed frame, in mode `full`, as the reader shows it today | none | 3 × 3 | 280 × 180 | 70% of the box's height, so at least 30% stays free to scroll past it |
| `map` | the real sky, with the stars, links, drift, labels and pins Home's sky shows, narrowed only by its own focus | `focus` (10.4), optional | 4 × 4 | 280 × 220 | half the box's height, at least 220 px; drawn settled, and a vertical swipe on it scrolls the column (10.9) |
| `glucose` | Home's glucose tile: the latest reading, the three-hour line, time in range | none | 2 × 2 | 200 × 110 | as in Home's phone layout |
| `recovery` | Home's recovery tile | none | 2 × 2 | 180 × 110 | as in Home's phone layout |
| `training` | Home's training tile | none | 2 × 2 | 180 × 110 | as in Home's phone layout |
| `changed` | Home's changed-lately corner, with the current insight when there is one | none | 2 × 2 | 200 × 110 | its content's |
| `insights` | the daily insights: the current insight, the earlier ones and the last run | none | 3 × 2 | 260 × 140 | its content's |
| `card` | another saved dashboard's card in its own sealed frame (mode `card`), as a corner shows it: shape `tall` when the cell is more than one and a half times as tall as it is wide, else `compact` | `dashboard`: a dashboard's id, never this dashboard's | 2 × 2 | 200 × 140 | a compact card's |

The app owns and tunes the pixel sizes and pins them in its tests; they decide only when a dashboard folds. The engine
checks the cell sizes. Home has its own smallest sizes and never folds (10.5).

- Native parts read the app's own data the way Home does (the glucose, Garmin summary, insights and map reads). The
  only new engine read is a focused map (10.4). Each native part uses the same component as Home's tile, laid out by
  its cell's size (container queries) rather than by the window or the Home layout.
- Activating a native part does what the same tile does at Home: a tile opens its panel, which grows from that part; a
  card opens its dashboard; a star on a map opens that star the way a command opens one (section 6): a star beyond the
  screen's map is fetched onto it first, with its neighbours, so every star a part shows opens.
- **When the data isn't there**, a part keeps its place and says so quietly, in the app's own words. Nothing reflows:

| Part | Shows |
|---|---|
| `glucose`, when no glucose source has data | the hint Home's main corner shows today |
| `recovery` or `training`, when no training source is on | its label and "Appears once a training source is connected" |
| `changed` or `insights`, with nothing yet | its label and "Nothing yet", with the last run when there was one |
| `map`, when its focus matches nothing | an empty sky and "Nothing on the map matches this focus", naming the focus |
| `map`, while the map can't be read | an empty sky and the line Home's sky shows ("Knowledge graph unavailable") |
| `card` of a dashboard deleted since, or one this viewer can't read (10.11) | "That dashboard is unavailable", with no title: the dashboard list doesn't tell the two apart, so neither names anything |
| `page` | its states as today, its fallback included: when the newest version's page fails to draw, the whole dashboard falls back to the version before it, that version's layout included, and says so; nothing is arranged until the newest version draws |
| a part of a kind this app doesn't know (a later piece's) | nothing: its cell stays empty, and the app doesn't arrange that dashboard (10.9) |
| any part, while access is lost | nothing, as Home shows nothing |
| any part, in demo mode | the demo's data, labelled DEMO as everywhere |

### 10.4 Map focus

`focus` narrows a map part's sky. Absent or null, the part shows everything the main map shows. Otherwise it is an
object with at least one member. A star is shown when it meets every member given, and for a list, any one of its
values:

| Member | Takes | A star is shown when |
|---|---|---|
| `query` | 1-100 characters once trimmed, no control characters | its title, its overview or one of its groups' labels contains the text, in any case |
| `near` | `{star, depth}`: a graphId, and `depth` 1 or 2 (default 1, stored written out) | it is that star, or within `depth` links of it in either direction, following only the links the sky draws (every verification but `disputed`) |
| `types` | 1-7 of `theme`, `project`, `note`, `record`, `document`, `memory`, `decision` | its `type` is one of them |
| `providers` | 1-20 sources (`^[a-z][a-z0-9-]{0,40}$`: `garmin`, `drive`, `notes`...) | its source (below) is one of them |
| `groups` | 1-20 group ids, 1-300 characters each | one of its `groups` has that id exactly: a project, a notes folder, or a source facet (`<catalog provider>:<subject>:<provider>`) |
| `owners` | 1-10 subjects (`^[a-z][a-z0-9-]{0,31}$`) | its owner is one of them |
| `links` | 1-4 of `verified`, `suggested`, `inferred`, `unverified` | (this one picks links, not stars) a link between two shown stars is drawn when its verification is one of them; without it, every link the sky draws is. A disputed link is never drawn on a map part, as on the sky |

- A star's **source** is the provider its records came from, worked out the same way by the engine (`DashboardMap`)
  and the app (`mapFocus.ts`, as `starSource` does today). Both pin each row of this table in their tests:

| Star | Its source |
|---|---|
| a data star: the catalog's provider, account `data:<provider>` | the provider after `data:`, such as `garmin` |
| a record saved in Rhythm: the catalog's provider, type `document`, not a data star | its account, such as `drive` |
| any other star | its own provider (`provenance.sourceId`), such as `drive`, `notes` or `second-brain` |

- A focus is at most 1,500 characters as the database stores it (as a layout, 10.2: a space after each colon and
  comma). Values are stored as given, trimmed, and lists without repeats; a list's bounds count its values as given,
  repeats included, and a `query` has no control character even before it is trimmed. `near.star` and each group id
  have none once trimmed, and every length counts characters. The engine (`MapFocus`) and the app (`parts.ts`) check
  it alike.
- "Only training stars on this map" becomes whichever member says it best: `{query: "training"}`, `{near: {star:
  <the training plan's graphId>}}`, or `{providers: ["garmin"]}` for every star whose readings come from that source.
  The map has **no tags** today; when stars carry tags, `focus` gains a `tags` member that works the same way, as an
  additive change.
- **Only what the map shows.** The focused read starts from the reader's own view of the map
  (`RelationshipHost.Readable(principal)`). Before it matches, walks, ranks, counts or picks links, it keeps only the
  stars whose provider the map shows (`AdapterRegistry.Shows`, as `Known`, the explorer and `app_graph` do) and the
  links between two kept stars that aren't disputed. It walks `near` itself over those links (the explorer's `Explore`
  stops at 60 stars) and projects with `GraphProjection.Create` and the same `shows`. `owners` and `providers` only
  narrow what is drawn; they never widen what a reader may see, and once the map is filtered per reader, a map part
  shows only what that reader may see.
- **Where a focus is applied.** A map part with no focus draws the app's own sky data (what Home's sky draws: the map,
  the pins and the dashboard stars) and needs no new read. A focused part asks the engine (10.7, the `map` read). The
  engine filters the whole readable map, not the app's bounded overview, so nothing that matches is dropped. It
  answers in `app_graph`'s page shape (`nodes`, `edges`, `nextCursor`, `completeness`, `issues`), ranked as the
  overview ranks stars (memories and what they cite first, then documents, then themes, then the rest; within each,
  most linked, then newest, then by graphId), `graph.nodeLimit` stars per page with an `o:N` cursor, plus `matched`,
  the count of every match. The part draws the first page as Home's sky draws its overview, adds the dashboard stars
  linked to a shown star, and says when a source couldn't be read (`issues`), as Home's sky does. Its caption names the
  focus in the words `showing.parts` uses (10.8) and the count: "near Training plan · 42 stars", or "near Training
  plan · 300 of 1,240 stars" when there are more pages.
- **Explore.** The caption's "Explore" adds the stars of the part's page to the screen's map (kept until Home, as a
  command's star is, section 6) and opens the explorer with them lit. A focus that is only a `query` opens the
  explorer's search for that text instead.
- **Dashboard only.** A focus lives in one dashboard's layout. It never changes the main sky, Home or another
  dashboard. Home's map takes no focus, and `focus_map` at Home is refused.
- A focused part is read again when its focus changes, when the main map is read again (every 15 minutes, and after
  Sync now or a rebuild) and when the app becomes visible.

### 10.5 Home as a layout

Home has five parts, with fixed ids: `map` (the main sky; it takes no focus) and the four corners `main`, `recovery`,
`training` and `changed`, each showing what it shows today: its tile, or the household's card for that corner. Every
Home grid has each of them exactly once. At Home the smallest sizes are `map` 4 × 4 and each corner 2 × 3 cells.

- **A screen without a grid looks and behaves exactly as today**, apart from the arranging grips (10.10). Its `layout`
  and `tiles` drive Home's CSS as sections 2, 5 and 8 say. A grid exists only after someone moves or resizes a Home
  part on that screen.
- **The first move or resize** turns what the screen shows into a grid, then applies the change:
  - On a screen drawing Home's layouts, the grid comes from what is drawn: each part's area in Home's CSS grid (not
    its content), measured, with every edge snapped to the nearest grid line (a gap counts half to each side). So Home
    looks the same after the first move, within a cell, at the size it was drawn.
  - When a part has no drawn area (an empty corner gives its room away today), when a snapped part ends up below its
    smallest size or the grid breaks a rule, and on a screen not drawing layouts, the grid comes from this table
    instead, each corner taking the rectangle of the place that `tiles` gives it in the current layout (places named
    as in section 2), as `x, y, w × h`:

| Layout | `map` | place `main` | place `recovery` | place `training` | place `changed` |
|---|---|---|---|---|---|
| `corners` | 2, 0, 8 × 12 | 0, 0, 2 × 8 | 10, 0, 2 × 8 | 0, 8, 2 × 4 | 10, 8, 2 × 4 |
| `hero` | 3, 0, 9 × 8 | 0, 0, 3 × 12 | 3, 8, 3 × 4 | 6, 8, 3 × 4 | 9, 8, 3 × 4 |
| `band` | 0, 0, 12 × 8 | 0, 8, 4 × 4 | 4, 8, 3 × 4 | 7, 8, 3 × 4 | 10, 8, 2 × 4 |

  - A change that only swaps two corners is applied as `move_tile`, and makes no grid.
  - **Back to the designed Home.** A change whose grid is the designed Home, the map in its own rectangle and each
    corner in one of the places' (as the grid the first change made has them; for a grid kept from before the app
    opened, as the table has them), keeps no grid: the corners take those places in `tiles` and the grid goes. So
    moves made back leave the screen exactly as it was, and a swap of two corners is one of these.
  - A grid is kept only when the change succeeds. A drop where the part started, Escape, a refused change or a
    `failed` report leaves the screen without a grid and saves nothing.
- **Never folds.** A Home grid shows only where layouts show today, at 1100 × 501 px or more, and there it is always
  drawn as saved: each tile lays itself out for its cell (container queries), down to its smallest cells. The pixel
  sizes of 10.3 play no part at Home. Narrower screens and phones keep today's fixed Home; a change made there is saved
  and reported with `detail.note`, as `layout` and `move_tile` are.
- **Kept** per screen, like `layout` and `tiles`: `settings.grid = {parts: [{id, x, y, w, h}, ...]}`, in `localStorage`
  as `rhythm-of-life.grid`, followed across a browser's tabs and sent with `listen` as in section 8. `app_screens
  listen` checks it by 10.2's rules, with the five ids exactly once and Home's smallest sizes. Absent means no grid;
  `grid` is never null (null is refused, and the app never sends it). It stays well inside the settings' 2,000
  characters. A listen stores the settings whole (section 5), so the engine's copy follows the browser's: when a listen
  answers settings without the grid the browser keeps (an older tab of the same browser listened since), the app sends
  its settings again with its next listen.
- **Looks follow the place.** Without a grid, a corner takes the look of the place it sits in, not its own: on the
  Tall column layout the main place's tall look (glucose large with its ring and GMI at the foot, another reading
  filling the column) and the row's for its other places, as for every place of the Rhythm band (glucose with the
  reading beside its line); on the Corners layout each place's alignment. With a grid each part lays itself out by
  its cell, and glucose in a tall, narrow cell reads as in the tall column.
- **Empty corners.** With a grid, a corner with neither its source nor a card keeps its cells and shows its quiet state
  (10.3). Without a grid, it gives its room away as today.
- **`layout`** puts that preset back exactly as today and **drops the grid** (Undo brings the grid back). Choosing the
  layout already shown drops it too, so choosing a layout is always the way back to the designed Home. Choosing one in
  Sources & settings does the same: while there is a grid it shows the command's note ("Switched to the … layout")
  with Undo, and without one it is chosen exactly as today.
- **`move_tile`** works as before: it swaps `tiles`, and with a grid it also swaps the two corners' rectangles, sizes
  included (a grid that comes back to the designed Home goes, as above). It stays the way to swap two corners (10.6).
- On the Corners layout the corners beside the map are usually at their smallest, so the map can't grow there; the
  answer says so and names the layout with room (10.6.2).

### 10.6 Commands (piece 3)

| `action` | Arguments | What the screen does | Lasts |
|---|---|---|---|
| `move` | `part`, and exactly one of: `place` (`left`, `right`, `up`, `down`, `top_left`, `top_right`, `bottom_left`, `bottom_right`), `with` (another part's id), or `x` and `y` (0-11); optional `dashboard` | Moves that part (10.6.1) | saved with the layout (below) |
| `resize` | `part`, and either `size` (`bigger`, `smaller`, `wider`, `narrower`, `taller`, `shorter`) or `w` and `h` (1-12); optional `dashboard` | Resizes that part (10.6.2) | saved with the layout |
| `focus_map` | `focus` (10.4; absent or null shows everything again), optional `part` (a map part; it may be left out when the dashboard has exactly one) and optional `dashboard` | Changes that map part's focus | saved with the dashboard's layout |

`dashboard` is a dashboard's id, or null for the screen's default Home. Left out, the engine fills it in from what the
screen last reported (10.7).

- **Target.** A command changes what the screen shows: the dashboard open there (in the reader, the dashboard panel or
  full screen, a Home dashboard included), or else its Home when the screen is at the default Home. Anywhere else (a
  star, a search, the explorer) the screen reports `failed`, "Nothing here has parts; open a dashboard or go Home".
  `dashboard` is a guard: the screen must be showing that dashboard (null: its default Home), or it reports `failed`
  and changes nothing. A dashboard shown at an earlier version (through the version picker, or after its newest
  version failed to draw) reports `failed`, "This is an earlier version; open the newest to arrange it". Nothing is
  arranged until the newest version's page has drawn (10.3): a command that comes while it is still drawing waits up
  to 3 seconds for it, then reports `failed`, "This dashboard hasn't finished drawing yet; nothing was changed". A
  change to a dashboard shown full screen keeps it full screen. A Home change never meets a full screen, because a full
  screen is then what the screen shows.
- **How long.** On a dashboard, the change is saved for the household with the dashboard's newest version as a layout
  revision (10.7). The screen that made it shows it at once, in every view of that version it has drawn (the reader or
  the dashboard panel stays drawn under a full screen), and every other screen showing that dashboard within about 2
  minutes (at its next feed read). At Home, the change is kept on that screen, like `layout`.
- **No new version.** A move, a resize or a focus change never writes a page or a version. The version list and its
  notes, Allow edits, and the dashboard's place in the list (`updatedAt`) stay as they are. A later version keeps the
  arrangement unless it brings its own (10.7).
- **At Home**, `move_tile` stays the way to swap two corners, and makes no grid. A `move` that comes to the same swap
  is applied as one (10.5). `move` and `resize` are for sizes and for putting the map somewhere else.
- **Undo.** Before each change the runner saves what it is about to change on its stack (section 6): for a dashboard,
  its id, version and layout with its revision; at Home, the grid or its absence. Undo saves the earlier layout back as
  a new revision, provided nobody changed it since (otherwise `failed`, "The layout changed since; nothing undone"),
  or puts the Home grid back. An Undo whose answer was lost, or that couldn't be sent or taken right now, stays on the
  stack to be tried again; one the engine refuses for good goes, and says why ("The dashboard has a newer version
  since; nothing undone", or "Nothing undone: " and the engine's reason), so it never blocks what is beneath it. A
  person's own in-place changes (10.9) go on the same stack, with the same note.
- **Refusals.** A part that isn't among what the screen shows, or a part that isn't a map for `focus_map`, reports
  `failed` naming it. A change that would leave things as they are (already there, already at its smallest) reports
  `failed` saying why.
- A screen whose app is older than piece 3 reports `failed`, "This screen can't do that.", as it does for any action it
  doesn't know.

#### 10.6.1 How a part moves

Two parts **line up** when they share exactly the same rows (the same `y` and `h`) or exactly the same columns (the
same `x` and `w`), with no other part between them there.

A part's **band** on one side (`left`, `right`, `up` or `down`) starts at the nearest part on that side that shares rows
(for left and right) or columns (for up and down) with it. The band is the smallest range of columns (rows) holding
that part such that every part overlapping the range within the moving part's rows (columns) lies wholly inside the
range and wholly within those rows (columns). When a part there reaches outside the moving part's rows (columns), there
is no band. Two parts that line up are a band of one.

- **`left`, `right`, `up`, `down`**:
  - With a band on that side, the part and its band **trade sides**. Everything in the band keeps its size and its
    place inside the band, free cells included; the moving part keeps its size; free columns (rows) between them stay
    between them. With the map on the left (0, 0, 7 × 12) and the page and glucose stacked on its right (7, 0, 5 × 8
    and 7, 8, 5 × 4), "move the map right" puts the page and glucose at `x` 0 and the map at 5, 0, 7 × 12.
  - Without a band, the part **trades rectangles**, sizes included, with the nearest part on that side that shares the
    most rows (columns), then the topmost or leftmost, provided each fits its kind's smallest size in the other's
    rectangle. The summary says so ("Traded places and sizes with the map"). Otherwise `failed`, naming the part that
    doesn't fit.
  - With no part on that side sharing rows (columns), the part slides to that edge of the grid over free cells; if it
    is already at that edge, `failed`.
- **`with`**: when the two line up, they **trade order** along that line: each keeps its own size, and the gap between
  them stays. Otherwise they trade rectangles as above, and the summary says so. At Home two corners always trade
  places, rectangles and all, as `move_tile` does.
- **`top_left`, `top_right`, `bottom_left`, `bottom_right`**: the part covering that corner cell of the grid is the
  other part, as for `with`. With none, the part goes into that corner when the cells are free, and otherwise reports
  `failed`, "No room there". A part already covering that corner reports `failed`.
- **`x` and `y`**: the part moves there at its own size (pulled back inside the grid) when the cells are free or its
  own. Otherwise the part covering cell (`x`, `y`) is the other part, as for `with`; with none, `failed`.
- **A drag** drops onto free cells as `x` and `y`, onto a part in the moving part's band on that side as a move to that
  side, and onto any other part as `with`. At Home a corner dropped on another part goes `with` it when each fits the
  other's rectangle (two corners always do), even a part in its band, so dropping it back where it was puts both back;
  otherwise as above.

#### 10.6.2 How a part resizes

- A step is 2 cells. `wider` moves one side edge outward, `taller` one top or bottom edge, and `bigger` does both.
  `narrower`, `shorter` and `smaller` move the same edges back. The edge that moves is the one away from the grid
  edge the part touches: the right edge, or the left edge when the part touches the grid's right edge but not its left;
  the bottom edge, or the top edge when it touches the grid's bottom but not its top.
- **Growing** takes cells from the parts in the way. Each gives them up from the side facing the growing part and keeps
  at least its smallest size. When one of them can't give up a whole step, the part grows only as far as all of them
  can. When it can't grow at all, `failed`, naming what is in the way: "No room: the page is at its smallest there",
  or "The map already fills the grid". At Home the answer also names the layout with room: "The map fills the space
  between the tiles; the Rhythm band layout gives it the full width". `bigger` succeeds when either direction grows.
- **Shrinking** never goes below the kind's smallest size (`failed` at it). The freed cells go to the parts that line
  up with the part on that side, which grow into them; with none, they stay empty.
- `w` and `h`: the part takes that size from its top left corner (moved up or left to stay inside the grid), and the
  other parts give way as for growing; refused when they can't.

#### 10.6.3 On a folded screen

A dashboard folded into one or two columns still takes every command. The change is made on the grid as saved, by the
rules above, and the screen shows the new reading order, reported with `detail.note`: "Saved; this screen shows the
parts in a column". Nothing is arranged by hand on a fold (10.9). A Home grid is not shown on a narrow screen (10.5).

### 10.7 Engine

#### Storage (migration `022_composable_layouts.sql`)

The number is provisional: it is renumbered above `main`'s highest migration before merging.

```sql
-- The grid a version was saved with. NULL is the page alone, as for every version before piece 3.
ALTER TABLE dashboard_versions ADD COLUMN layout jsonb
  CHECK (layout IS NULL OR (jsonb_typeof(layout)='object' AND length(layout::text)<=8000));
-- The newest version's arrangement as moves, resizes and focus changes leave it: at most one row per version.
CREATE TABLE dashboard_layouts(
  dashboard_id uuid NOT NULL,
  version int NOT NULL,
  revision int NOT NULL CHECK (revision BETWEEN 1 AND 1000000),
  layout jsonb NOT NULL CHECK (jsonb_typeof(layout)='object' AND length(layout::text)<=8000),
  note text NOT NULL CHECK (length(note) BETWEEN 1 AND 500),
  saved_by uuid NOT NULL REFERENCES principals,
  saved_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (dashboard_id, version),
  FOREIGN KEY (dashboard_id, version) REFERENCES dashboard_versions ON DELETE CASCADE);
REVOKE ALL ON dashboard_layouts FROM PUBLIC;
```

- A version's **layout** is its `dashboard_layouts` row's when it has one, and otherwise its own column; its
  `layoutRevision` is that row's `revision`, or 0 for the column. Versions stay immutable. Deleting a dashboard removes
  its layouts with its versions, and there is nothing to prune.
- **The page alone.** A NULL column (every version saved before piece 3, and any saved without a layout) means exactly
  one part, `{id: "page", kind: "page", x: 0, y: 0, w: 12, h: 12}`. The engine (`Resolve`, `save_dashboard_layout`, the
  map read), the app's `parts.ts` and `showing.parts` all read it so, and each has a test for it. `dashboard_json`
  still answers `layout: null` for it, so a reader can tell a dashboard never arranged.
- A layout change touches nothing else: not `dashboards.updated_at` or `edits_allowed`, not `dashboard_writes`,
  `ambient_cards` or `sky_pins`. `screens` and `screen_commands` need no change: `settings.grid` and `showing.parts`
  are members of their objects, and the new actions are checked in code.
- Functions follow migrations 013-020: role-gated through `dashboard_reader`, errors raised as `22023`, `P0002` or
  `42501`, `REVOKE ALL ... FROM PUBLIC`. Every `CREATE OR REPLACE` is followed, in the same migration, by `ALTER
  FUNCTION ... SET search_path TO pg_catalog,<schema>,pg_temp` (see 019).
  - `dashboard_json` adds `layout` (as above, or null), `layoutRevision`, `layoutNote` and `layoutSavedAt` (both null
    for revision 0), in the list as in one version.
  - `save_dashboard` (harness): a new dashboard stores `p_version->'layout'`, or NULL without one. A next version stores
    the layout it is given, or without one the newest version's layout, so revising the page keeps the arrangement. A
    restore copies the restored version's layout as it was last arranged into the new version's column. Feeds, stars
    and writes are saved as today.
  - New `save_dashboard_layout(p_principal, p_dashboard, p_version, p_base, p_layout, p_note)`, for the roles `app` and
    `harness`. It locks the dashboard (`FOR UPDATE`) and refuses a version that isn't the newest (`22023`). The `app`
    role always gives `p_base` (`22023` without it), so no screen skips the stale check; a harness may leave it out
    to arrange whatever is there. When `p_base` is given and isn't the current `layoutRevision`, it saves nothing and
    answers the current layout with `saved: false` (never null: a version without a layout answers the page alone, so
    a screen can apply its change to it). Otherwise it writes the row with the next revision and answers `{id,
    version, layout, layoutRevision, saved: true}`. With `memory.scopes` on (second-brain's 027) it also takes, last
    and with defaults, the stars its caller reads and the subject of each map part's focus star (`p_stars`,
    `p_cited`; `DashboardStore.SaveLayout` passes both), and refuses (`22023`) a focus that would narrow the
    dashboard: a Family dashboard's map refocused onto a person's private star. With scopes off it answers as above.
  - Two helpers, both with the search path pinned and revoked from `PUBLIC`: `dashboard_layout(uuid, int)`, a
    version's layout as it stands (the page alone for NULL), its revision and the revision's note and time, and
    `layout_identity(jsonb)`, a layout without its rectangles and its map parts' focus, parts ordered by id, which the
    next rule compares.
- **The app arranges; the harness composes.** For the `app` role, `save_dashboard_layout` saves only an arrangement of
  the current layout: the same part ids, each with the same `kind` and, for a card, the same `dashboard`. Only `x`,
  `y`, `w`, `h` and a map part's `focus` may differ. The database enforces it (`layout_identity`, `22023`), and the
  endpoint answers `{ok: false, reason}`. So no screen, and no older or broken app, can add or drop a part or a focus
  member it doesn't understand. The harness may save any layout that keeps the rules.
- **Checks.** The engine checks every layout before saving it, from either role, with `DashboardLayout.Problem()`
  (10.2-10.4). It reads a layout with case-sensitive member names and strict numbers, and refuses a member a layout, a
  part or a focus doesn't take. The harness tools read `save_dashboard`'s `layout` and `control_screen`'s `focus` so
  too, from the arguments as written, since the SDK's own binding takes member names in any case and numbers written
  as strings. An app save meets the stale check (`base`) first, so a stale screen always gets the
  current layout back. Parts are matched to the current layout by id, and only their reference members count as
  changed: a map's `focus` (compared as stored) and a card's `dashboard`. A new part, or one whose reference member
  changed, has its references checked: a focus star (`near.star`) must be readable by whoever saves
  (`DashboardFeeds.Stars` for the harness, `RelationshipHost.Known` for the app), and a card's dashboard must exist and
  isn't this one. A part whose rectangle alone changed is never checked again, for either role or for Undo, so a part
  whose star or dashboard has gone since still moves, resizes, trades places and comes back, and shows as 10.3 says.
- The demo seed (`DemoAdapter`) inserts versions without a layout. It treats a layout row saved by another principal as
  someone changing the dashboard, so it stops updating that dashboard and Retire keeps it.

#### `app_dashboards` (role `app`): two new shapes

| Arguments | Answer |
|---|---|
| `id`, `version`, `layout`, `base` (the `layoutRevision` the change started from), `note` (1-500 characters: the change in plain words, as the screen's note says it) | `{ok: true, layout, layoutRevision}`. `{ok: false, reason}` when the version isn't the newest, the change is more than an arrangement, or the engine refuses the layout. `{ok: false, reason, layout, layoutRevision}` when the layout changed since `base`: the screen applies its change once more to that layout and saves again, and reports `failed` if it changed again |
| `id`, optional `version`, `map` (a map part's id), optional `cursor` | `{map: {part, focus, matched, nodes, edges, nextCursor, completeness, issues}}`: the focused read of 10.4, with `nodes`, `edges` and `issues` as `app_graph` gives them. 400 when that part isn't a map or has no focus, 404 when there is no such part |

- Every summary and version adds `layout`, `layoutRevision`, `layoutNote` and `layoutSavedAt`. The feed read (`id` with
  `data`) adds them to its `dashboard: {id, version}`, so a screen sees another screen's change at its 2-minute read.
- The listed input schema adds `layout` (an object), `base` (an integer, at least 0), `note`, `map` and `cursor`, each
  shape exclusive, as `DashboardArgs` keeps them. `contracts/mcp/app_dashboards.output.schema.json`, its fixtures,
  `contracts/README.md` 2.2 and `scripts/contracts.test.mjs` change in the same commit.
- **The facade** chooses an engine call's limits by tool and shape: the map read gets `app_graph`'s, and a layout save
  keeps `app_dashboards`' own. The map read (`{id, version?, map, cursor?}`) only reads, so it may be tried again; it
  refuses (400) a cursor the engine wouldn't take (`o:` and at most five digits) rather than ask. A layout save is
  never repeated: a second try after a lost answer would meet its own revision as stale and apply a relative change
  twice. The facade's answer to a layout save it couldn't make or hear back from (no answer, the engine unreachable or
  without piece 3) adds `transient: true`, as the browser's own lost answer does: trying again later may work, so the
  screen's Undo keeps it (10.6).

#### `app_screens`

`listen` takes `settings.grid` (10.5). `send` takes the new actions and arguments, as `args` or beside the others
(`with` never collides with the target `to`). `listen` and `done` take `showing.parts` (10.8). The listed input schema
names `grid` among `settings`' properties, which is how the facade knows the engine has piece 3 (10.10). The schema,
the fixtures, `contracts/README.md` 2.1 and the contracts test change in the same commit.

#### Rules the engine enforces, beside 3.2

- `move`, `resize` and `focus_map` join the checked actions, `control_screen`'s enum (`ScreenAction`) and `app_screens
  send`. Every new argument has its own case in `ScreenCommands.Problem` and `Expected`, so none falls to the search
  rule: `part` and `with` are part ids and differ; `place` and `size` come from their lists; `x` and `y` are integers
  0-11 and `w` and `h` 1-12, each given with its pair; `move` takes exactly one of `place`, `with` or `x` and `y`, and
  `resize` one of `size` or `w` and `h`; `focus` is as 10.4 says, and absent or null is stored as `focus: null`, as
  `corner_card` clears; `dashboard` is a uuid or null.
- **Filling the guard.** When `move`, `resize` or `focus_map` comes without `dashboard`, the engine fills it in from the
  target's last reported `showing`: its `dashboard.id` when `view` is `dashboard` or `full_screen`, null when `view` is
  `home`. With any other view, or none reported, it stays out, and the screen checks the command against what it
  shows. So a screen that has moved on since the caller looked refuses the command instead of changing another
  dashboard. With `memory.scopes` on, a screen showing a dashboard the caller can't read reports none to it (3.2), and
  such a command is refused as for an unknown dashboard (404) instead of being left to the screen.
- With `dashboard` a uuid, `Resolve` reads that dashboard's newest layout (the page alone when it has none). It refuses
  (404) a `part` or `with` the layout doesn't have and, for `focus_map`, refuses (400) a part that isn't a map, or a
  missing `part` when there is no map or more than one. A focus star must be on the map now (`RelationshipHost.Known`,
  404), as for `open_star`. With `dashboard` null, `Resolve` checks `part` and `with` against Home's five ids and
  refuses `focus_map` (400, "Home's map takes no focus").

#### Harness tools

- `save_dashboard` adds an optional `layout` (`{parts: [...]}`, 10.2-10.4) for a new dashboard or a next version, and
  a third form: `dashboardId`, `layout`, `note` and an optional `layoutRevision` arrange the newest version without
  making a new one, refused when the layout changed since that revision (read it again), and answer `{id, version,
  layoutRevision}`. Without `layoutRevision` it arranges whatever is there, from the revision it reads (tried again,
  at most three times, when another change lands in between). A next version without `layout` keeps the arrangement.
  To go back to the page alone, send a layout whose only part is a `page` covering the grid. `restoreVersion` brings
  back that version's arrangement too. The feed, write and 12-star rules don't change, and a layout's stars don't
  become sky links.
- `list_dashboards` returns `layout`, `layoutRevision`, `layoutNote` and `layoutSavedAt`. Its `star` filter also keeps
  the dashboards with a map part focused `near` that star.
- `list_screens` returns `showing.parts` (10.8). `control_screen` adds the three actions and the arguments `part`,
  `place`, `with`, `x`, `y`, `size`, `w`, `h` and `focus` (an object), and `dashboard` may be null. A null counts as
  given only for `move`'s, `resize`'s and `focus_map`'s `dashboard` (the screen's default Home, told from one left
  out); any other null is left out as before piece 3, so a client may send null for every argument it doesn't use.
- Piece 3 adds no harness tool. The `ToolText` templates (no names, products or hosts) say:
  - for `save_dashboard`, what parts are for (add the map, the health tiles and the insights as parts instead of
    drawing them; the page draws what only it can), the grid, the kinds and their smallest sizes, the focus members and
    the forms above;
  - for `control_screen`, the three actions, that they change a dashboard for the household and Home for that screen
    only, to read `list_screens` for the part ids and pass `dashboard` (null at Home) so a screen that moved on
    refuses, and that at Home `move_tile` swaps two corners without changing the designed Home;
  - for `list_screens`, the `parts`.
- The dashboard guide (`rhythm://guides/dashboards`) gains a short part on layouts: a map wants room (at least 4 × 4),
  the page goes where its content reads well, and phones stack parts top left first.

A new dashboard with the map on the left, its page at the top right and the glucose tile below the page (graphIds come
from `explore_graph`):

```json
{
  "title": "Training",
  "html": "<main>…</main>",
  "feeds": [{"name": "training", "kind": "data", "star": "<graphId of a data star>"}],
  "note": "First version: the map, the plan page and glucose",
  "layout": {"parts": [
    {"id": "map", "kind": "map", "x": 0, "y": 0, "w": 7, "h": 12,
     "focus": {"near": {"star": "<graphId of the training plan>", "depth": 1}}},
    {"id": "page", "kind": "page", "x": 7, "y": 0, "w": 5, "h": 8},
    {"id": "glucose", "kind": "glucose", "x": 7, "y": 8, "w": 5, "h": 4}]}
}
```

On a screen showing it, "put the map on the right" is `control_screen` `{action: "move", part: "map", place: "right",
dashboard: <its id>}`: the page and glucose are the map's band on the right, so they trade sides with it, and the map
lands at 5, 0, 7 × 12 at its own size. "Make the map bigger" is `{action: "resize", part: "map", size: "bigger"}`, and
"only my garmin stars on this map" is `{action: "focus_map", focus: {providers: ["garmin"]}}`.

### 10.8 What a screen reports

`showing` adds `parts`: what the screen shows as a grid, `[{id, kind, x, y, w, h, focus?}]` in reading order, with
rectangles as saved.

- For an open dashboard, its layout. A dashboard without one reports the page alone, `[{id: "page", kind: "page", x:
  0, y: 0, w: 12, h: 12}]` (10.7).
- At Home, its grid, or without one the grid the first move would make (10.5): measured on a screen drawing layouts,
  else from the table. Home's parts report `kind` `map` or `corner`.
- For anything else, `[]`. An app with piece 3 always sends `parts`.
- `focus` is the focus in plain words, cut to 100 characters ("near Training plan; providers garmin"). The whole focus
  is in `list_dashboards`.
- `narrow` is also true when a dashboard's grid is folded.

Twelve parts stay well inside the 4,000 characters `showing` may take.

### 10.9 The app

- **One grid.** A shared module, `app/src/parts.ts` (beside `screens.ts` and `dashboards.ts`, used by the app, the
  facade and the demo), holds the kinds and their sizes, the rules, Home's presets and the measured first grid,
  reading order, folding, and the moves, resizes and focus changes of 10.6, as pure functions with tests. The engine's
  `DashboardLayout` checks the same rules. One grid component draws Home with a grid and every dashboard with a layout,
  in each of the dashboard's hosts: the explorer's reader, the dashboard panel and full screen (a Home dashboard
  included). The dashboard's own line (title, version picker, refresh, Allow edits, freshness and write Undo) stays
  where it is today, below the page, and the grid takes the page's place above it; each part's own states stay in its
  cell. A corner card stays the page's card alone.
- **The page part's frame stays exactly as strict.** It keeps the same two sealed frames, the same policies and the
  same bridge: data in; `ready`, `idle`, `error`, `openStar` and declared writes out. No message reaches layouts, parts
  or screens, so a page can never move a part, change a focus or control a screen. Everything that arranges parts is
  the app's own and drawn outside the frame. Arranging listens only to pointer events (with pointer capture) and key
  events that start on the app's own grips and corners; the grid registers no `dragstart`, `dragover` or `drop`
  handlers, so a drag that a page starts in its frame can never move a part. A card part is a corner card's frame
  (mode `card`, writes refused).
- **Maps.** A map part is the app's sky component in its cell. Only Home's sky seeds the explorer's star positions. A
  part's sky pauses when hidden, off screen, under reduced motion or while a panel covers it, as Home's does, and a
  resize settles it again once, when the resize ends. While a dashboard with a map part is in the explorer's reader,
  the explorer's own sky behind it pauses, as Home's sky pauses under the explorer, so no more than the dashboard's own
  two maps move at once. In a fold a map part is drawn settled and still: a vertical swipe on it scrolls the column
  (`touch-action: pan-y`), a tap opens a star, and panning and zooming happen in the explorer, through "Explore".
- **In place, with no mode.** Arranging is always there where it applies: a dashboard's newest version drawn as saved
  (not folded), with more than one part or a part with room to grow, whose kinds and focus members this app knows; and
  Home at 1100 × 501 px or more. There is no Edit button, mode or popup.
  - **Pointer.** Each part has a quiet grip at its top right and a resize corner at its bottom right, drawn by the app
    over the cell, never inside a frame, and shown on hover or focus. Dragging the grip shows where the part would go
    and drops it as 10.6.1 says; dragging the corner resizes it by whole cells. While a part is held, a clear layer
    covers every frame so the drag isn't lost into one.
  - **Touch.** On touch screens (a coarse pointer) a dashboard's grips and corners show quietly all the time instead,
    since a touch inside a frame or on a map is never heard as a touch on the part. Home's show for a few seconds after
    any touch on Home outside a frame (a tile or the sky), so an unchanged Home looks as it does today. At Home a grip
    or corner takes the pointer only while it shows (hovered, focused, held, or in those seconds after a touch), so a
    hidden one never takes a click or a tap from the tile or sky beneath it. A press and hold on a grip, for the app's
    hold time (`holdMs` in `gestures.ts`), picks the part up; on the corner, it resizes it. Nothing is arranged by hand
    on a folded grid: it shows no grips, and the commands still work there (10.6.3).
  - **Keyboard and screen readers.** Each part is a group (`role="group"`, not a landmark) labelled "Map, near
    Training plan", "Glucose", or the dashboard's title for its page. Its grip is a button, "Arrange map", the first
    thing in its part. Enter or Space picks the part up; the arrow keys move it one step (`left`, `right`, `up`,
    `down`); Shift with the arrows resizes it (`wider`, `narrower`, `taller`, `shorter`); Enter or Space drops it;
    Escape puts it back. While a part is held the root element carries `data-arranging`, and the explorer's, the app's
    and the Details panel's own Escape handlers stand aside, so Escape only puts the part back. Each step is announced
    politely, in plain words: "Map moved right, beside the page"; "Map wider, 7 of 12 columns". Tab and screen readers
    follow the parts' DOM order, which is the reading order as the grid first drew. After a move they follow the new
    reading order where the browser supports CSS `reading-flow: grid-rows`, and otherwise the drawn order until the
    host opens the grid again; the announcement always says where the part went.
  - Each change is saved when it is dropped, never while dragging. It shows the same small note as a command ("Moved
    the map", with Undo; a person's own change names no sender) and goes on the same Undo stack, which the runner
    shares with in-place changes. A change that isn't made (a drop with no room there, a refusal, an answer that
    didn't come back) shows why in that note, with no Undo, besides saying it to screen readers; the part goes back
    where it was. Without the runner a dashboard says it below the page, where a write's outcome shows.
  - A focused map's caption names its focus and offers "Show everything" in place, which clears it as `focus_map`
    would, with the same note and Undo. It shows on a fold too: it changes the focus and moves nothing. Other focus
    changes come from commands or `save_dashboard`.
- **Arranging only what it knows.** The app arranges only a layout it read whole. When it meets a part kind or a focus
  member it doesn't know (a later piece's), it draws what it knows, leaves that cell empty, shows no grips, and reports
  `failed`, "This screen can't arrange this dashboard yet", for commands on that dashboard. A layout it arranges goes
  back with every part and member exactly as read, apart from the change.
- **Full screen** with a layout draws the grid over the whole display. The exit control works as section 8 says; its
  clear layer covers only the frames (page and card parts), since native parts hear the pointer themselves. Grips work
  the same, and Escape while a part is held puts it back without leaving full screen.
- **Saving.** A dashboard change goes through `app_dashboards` `{id, version, layout, base, note}` (in the facade,
  `POST /api/dashboards` `{action: "layout", ...}` with an exact key allowlist), never retried (10.7). A Home change
  goes into the screen's settings. The feed read every 2 minutes brings other screens' changes; a newer layout than the
  one drawn is drawn without loading any frame again.
- **Looks.** Parts use the app's fonts, colours and gaps. Tiles keep their designed looks at every size through
  container queries, and a folded column fills the phone's width with the app's phone spacing.

### 10.10 Compatibility and demo mode

- **Nothing changes until someone changes it.** A dashboard without a layout (every one saved before piece 3, and any
  saved without one) draws exactly as today: its page fills the reader, the full screen and its card. A layout whose
  only part is a page covering the grid draws the same way. A screen without `grid` draws Home exactly as today.
  Existing commands and settings mean what they meant. The one addition on an unchanged Home is the way to arrange it:
  the grips on hover or focus (on touch screens, for a few seconds after a touch), and five "Arrange …" buttons in its
  tab order, one per part. Nothing moves until someone uses them.
- **Engine first.** The engine refuses settings members and argument shapes it doesn't know, so an app with piece 3
  must not send them to an older engine. The facade learns what the engine offers from its tool list, keeping each
  tool's input properties beside the names it caches today: piece 3 lists `grid` among `app_screens`' settings, and
  `layout` and `map` among `app_dashboards`' arguments. A listing the facade couldn't read counts as no piece 3 (the
  opposite of `offered`'s default): it drops `settings.grid` and answers layout saves and map reads as unavailable. A
  listen refused with `invalid_arguments` while it carried `grid` drops the cached listing and is sent once more
  without `grid`; the refused listen claimed nothing.

| Engine | App | What happens |
|---|---|---|
| with piece 3 | with piece 3 | everything in this section |
| older | with piece 3 | the facade drops `settings.grid` before listening (the browser keeps its Home grid alone) and answers layout saves and focused reads as unavailable; dashboards draw their page alone, with no arranging; the new commands can't be sent |
| with piece 3 | older | every dashboard draws its page alone (the app's cleaners drop `layout`); the app never sends a grid and ignores one it is given; the new commands report `failed`, "This screen can't do that." |

- **An older tab** of the same browser (a cached bundle) listens with settings that carry no `grid`, which replaces the
  engine's copy. The browser keeps its grid, and its next piece-3 listen sends it again (10.5).
- **Demo mode** (section 7). The demo's dashboards include one fictional dashboard with a layout (the map, its page and
  glucose; labelled DEMO), arranged in the browser only. The demo's screens take the three actions with the same checks
  and Undo. A focused map filters the demo's map in the browser, which holds all of it, with the same rules as the
  engine (10.4: what the map shows, no disputed links, sources as in the table), so nothing that matches is dropped.
  Nothing is sent to an engine.

### 10.11 Tables and fields, for second-brain's subject and scope

Second-brain plans to give dashboards, their pins, corner cards, refresh requests and insights a subject and a scope
(SB-P1-06), with author rules: a private dashboard is changed by its owner, a Family one by its author or an admin, and
every existing dashboard keeps today's rules. Piece 3 adds no subject or scope column anywhere, and needs none:
everything it adds hangs off a dashboard or a screen, and SB-P1-06's migration comes after piece 3's. This table is
what SB-P1-06 builds on.

| Where | What piece 3 adds or changes | Keyed by | Subject and scope |
|---|---|---|---|
| `dashboard_versions` | new nullable column `layout jsonb` | (`dashboard_id`, `version`) | its dashboard's |
| `dashboard_layouts` | new table: `dashboard_id`, `version`, `revision`, `layout`, `note`, `saved_by`, `saved_at` | (`dashboard_id`, `version`), removed with the version | its dashboard's; `saved_by` is read like `dashboard_versions.saved_by` |
| `dashboards` | unchanged; a layout change touches neither `updated_at` nor `edits_allowed` | `id` | where SB-P1-06 adds them |
| `ambient_cards`, `sky_pins`, `dashboard_writes`, `relationship_refresh_requests`, insights | unchanged | | as SB-P1-06 plans |
| `screens.settings` | new member `grid` (no column) | the screen | none: a screen's settings stay household-wide |
| `screens.showing` | new member `parts` | the screen | none; with scopes on, the view of the login that reported it (`shown_by`, 027), told to another principal only as far as it reads it (3.2) |
| `screen_commands` | new actions `move`, `resize`, `focus_map`; new `args` members `part`, `place`, `with`, `x`, `y`, `size`, `w`, `h`, `focus`; `dashboard` may be null | the command | none; with scopes on, its arguments are checked against the target's login and its outcome is the reporting login's view (`reported_by`, 027; 3.2) |
| functions | `dashboard_json` and `save_dashboard` replaced, `save_dashboard_layout` new, with the helpers `dashboard_layout` and `layout_identity` (10.7); `read_dashboards` and `delete_dashboard` unchanged (layouts go with their versions) | | SB-P1-06 replaces `dashboard_json` and `save_dashboard` after piece 3, keeping their `layout` handling |
| `save_dashboard_layout` | a new way to change a dashboard: the `app` role (every screen) arranges it, the harness arranges or composes it | (`dashboard_id`, `version`) | SB-P1-06 gates it with the author rule it gives `save_dashboard`, and decides what the `app` role may arrange, as it decides for the app's other dashboard changes; with scopes on a focus never narrows its dashboard (027, the engine passing each focus star's subject) |
| card parts | a layout can name another dashboard (`card.dashboard`) | | a viewer who can't read the named dashboard sees it as unavailable, with no title (10.3); SB-P1-06 decides whether `dashboard_json` shows that id to them |
| reads | the focused map read (10.4) and `list_dashboards`' `near` filter | | both read through `RelationshipHost` and the dashboard list, so the shared read predicate applies by itself; a focus star that a reader may not see shows as gone, and a card's dashboard as unavailable (10.3) |
| native parts | read Home's data | | as Home reads it (the app principal, the first person), until SB-P1-06 says otherwise |

Native memories in feeds (second-brain's SB-P6-03, agreed with that program): a `contents` or `links` feed on a
native memory's star reads it as any other star, through its reader's own view of the map, so with `memory.scopes` on
it serves only memories that reader reads. A memory's star is on the map while its reader reads the memory's own row
and it is not held, deprecated or past its validity, judged at each read (`docs/dev/memory.md` 7.3): otherwise the feed
is `unavailable` ("This star isn't on the map now.") with no title. `contents` gives the memory's current text, read
from the memory tables at that read; `links` the stars around it, its project's star among them; a project's star has
no text of its own. A dashboard citing a person's private memory lands in that person's scope (027's
`derived_subject`). No dashboard code or function changed for it, so with no native memories every dashboard answers
as before (`MemoryDashboardTests`).

`docs/security.md` gains one point with piece 3: the `app` role can now change how a household dashboard is arranged
(the same parts, moved, resized or refocused; never added or removed), as it already sets corner cards, and only the
app's own controls can do that, never a page.

### 10.12 Memories feeds

A dashboard can read native memories by filter rather than by star: a feed of kind `memories` (`DashboardFeed`,
`MemoryMatch`, `DashboardFeeds`), only with `memory.tools` on, and its work filters and fields only with `memory.work`
on as well. It is what a project board is built on.

- **Filters.** `where` holds the filters of the app's Memories view, defined once in `MemoryMatch`, which both the
  view's list (`MemoryStore.Filtered`, through `MemoryMatch.Of` for its one-value arguments) and the feed
  (`MemoryStore.Matching`) turn into the same SQL, so the two give the same memories for the same filters
  (`MemoryFeedTests`): `words` (text its words hold, case aside, 1-200 characters), `status` (canonical, draft or
  deprecated), `kind` (episodic or semantic), `project` (a slug or an alias), `tag`, `work` (true: work items only;
  false: no work items), `workType`, `state` and `priority` (1 to 4, or 0 for the unranked). Each filter but `work` is
  one value, a list of 1-20 values (any of them) or `{"not": values}` (none of them); every filter given must match,
  and there is no nesting, so two unrelated sets are two feeds. Any of `workType`, `state` and `priority` keeps work
  items only, negated or not, and `work: false` beside any of them is refused. A held memory's words, tags and project
  never select it, by a filter or by its negation. A member it doesn't know is refused.
- **As stored** (`DashboardFeed.Normalized`): each filter a list or `{"not": [...]}`, and a feed that names no status
  gets `{"not": ["deprecated"]}`, so it leaves out the memories marked wrong unless its status filter names them.
  `limit` is 1-1000, default 200.
- **What it serves.** The memories the dashboard's reader reads (the one read predicate on each memory's own row,
  `Database.Visible`, as every memory read), so each reader of the same dashboard gets only their own; never one held
  for review (not even without its words, as the list shows one) and never one in the trash, which has its own view.
  Every project it names must be one the reader reads, or the feed is `unavailable`, naming them; `save_dashboard` and
  Studio refuse a project the saver doesn't read, with the projects it does.
- **Payload** (`memories-payload` in `app_dashboards.output.schema.json`, fixture `app_dashboards.memories.example.json`):
  `{kind: "memories", status, asOf, where, limit, count, memories, truncated}`, no star: `count` is how many match in
  all, `memories` the newest `limit` of them, newest first as the list orders them, and `truncated` that more match.
  Each memory has `id`, `graphId` (its star, for `rhythm.openStar`), `scope`, `owner`, `project` (`{id, slug, name,
  graphId}`, the project's star) or null, `tags`, `kind`, `status`, `state` (current, replaced, ended or retracted, as
  the list says it: the list shows a replaced or ended memory, so the feed does too), `createdAt`, `updatedAt`, `due`
  (`{at, local, kind}`) or null, `preview` (its first 300 characters, as the Memories list cuts them) and `truncated`,
  and `work`: null for an ordinary memory and with `memory.work` off, else `{type, state, priority, assignee, claim,
  blocked, blockedBy}`, where
  `claim` is `{label, claimedAt, expiresAt}`, who holds it and until when, never the claim's id, and `blockedBy` the
  unfinished items it waits on (080's `memory_work_waiting`), first by priority, at most 20, each `{id, graphId, type,
  state, priority}` with no words. `blocked` is true when the item is open and waits on any. `dashboard_data`'s
  preview keeps the newest 20, with `previewOf`; a feed past the dashboard's 3 MB is unavailable ("ask for fewer
  memories (limit) or narrow the filters").
- **Off.** With `memory.tools` off, `save_dashboard`, `dashboard_data` and Studio refuse a memories feed, and a saved
  one is `unavailable` ("Memories are off in this install (memory.tools)."); with `memory.work` off, a work filter is
  refused the same way, a saved one is unavailable, and every `work` is null. The tool descriptions name the feed only
  with `memory.tools` on, its work filters and fields only with `memory.work` on, so with them off every description
  is as before; the tools' input schema gains `where` on a feed in every install.
- **The star rule.** A dashboard names 1-12 stars across its feeds and writes, unless one of its feeds is a memories
  feed, when it may name none (`DashboardDraft.Unanchored`, in `save_dashboard` and Studio; migration 083 relaxes 013's
  check on `dashboard_versions.stars` to the same rule, which a restore keeps since it copies feeds and stars
  together). A memories feed reads by filter, so a board over every project has no star to name, and an anchor star
  would be decoration that, with `memory.scopes` on, would narrow who sees the dashboard. A version that names no star
  cites none: with scopes on it lands in Family unless its logs or its earlier version narrow it, and each reader's
  feed still serves only what that reader reads. The app lists such a dashboard, shows it on a screen and as a card,
  and joins it to the sky only when a screen is asked to open it (`withDashboards`), since the sky places a dashboard
  beside a star it reads.

## 11. Studio

[Studio](studio.md) adds persisted full-screen drafts and host-owned typed selections. `open_studio`, `dashboard_controls` and `dashboard_action` are internal commands sent by the new tools through the same screen queue and acknowledged by the active browser. They are not arbitrary new `control_screen` actions. Studio follows the tab already holding `rhythm-of-life.screen:<id>`, so background tabs do not compete to validate drafts. The app facade multiplexes `app_studio` through `POST /api/screens?studio=1`, preserving the existing origin, authentication and viewer boundary and the hosted function count. Existing screen actions and saved dashboards keep their behaviour.
