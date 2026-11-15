# Migration compatibility

The engine's migrations are `engine/migrations/NNN_name.sql`, applied in order by the one-shot `migrate` service.
Each applied migration is recorded in `schema_versions` with its **version** (the embedded resource name,
`Knowledge.Migrations.NNN_name.sql`) and the SHA-256 of its text. On every run the migrator recomputes the checksum of
each recorded migration and refuses to continue when one differs ("Applied migration changed"). That keeps an install
from silently running a schema that no longer matches its history.

The migrations came from the private, single-household version of the engine. An install created by that version
already holds 001-016 in `schema_versions`, so this repository keeps every migration it could byte for byte and
records here what it changed in the rest. It never records the old checksums themselves: those are hashes of private
files and belong to that install's private overlay (open question Q-013).

## What is kept

- **Resource names.** The project embeds the SQL with `LogicalName="Knowledge.Migrations.%(Filename)%(Extension)"`,
  so the recorded versions stay `Knowledge.Migrations.001_foundation.sql` and so on, whatever the project is called.
- **Line endings.** Checksums include them. `.gitattributes` stores every text file with LF, as the originals were.
- **Byte-identical migrations:** 002, 003, 004, 005, 006, 007, 009. Their checksums match an existing install's.

## What changed (same schema, neutral text)

| Migration | Change | Schema effect |
|---|---|---|
| 001 `foundation` | the subjects it inserts are only `household` and the synthetic `fixture`; people now come from `rhythm.yaml` at start | none for an existing install (its people rows stay) |
| 008 `carelink_csv_jobs` | keeps the `sync_jobs.metadata` column; the plugin-specific resync queue function moved out with the CareLink plugin (Q-007, Q-015) | a fresh install lacks that one function; an existing install keeps it |
| 010 `garmin_sync_requests` | the gating principal name and the fixed time zone replaced by neutral values; comments neutral | none: 017 replaces the function |
| 011 `insights`, 012 `insight_pilot`, 013 `dashboards`, 014 `relationship_refresh_requests`, 015 `dashboard_writes`, 016 `dashboard_delete_and_pins` | principal names in the gate checks replaced by neutral names (`harness`, `app`), a fixed time zone by UTC, and comments that named people or hosting rewritten | none: 017 replaces every gated function |

Because 017 replaces every function whose body changed in 010-016, a fresh install and an existing install end in the
same schema after 017, whichever wording of 001-016 they recorded.

## 017 `roles_and_install` (new)

- `principals.role` (`legacy`, `system`, `harness`, `app`, `ingest`), `principals.plugin`, `principals.created_at`;
  at most one enabled `app` principal; enabled role principals have distinct names.
- Every entry point that 011-016 gated on a principal's **name** is replaced by the same function gated on its
  **role** (`dashboard_reader` now takes role names). Function bodies are otherwise unchanged, except
  `write_dashboard_log` and `request_garmin_sync`, which read the install's time zone from `install_settings`.
- `install_settings` (the engine writes `timezone` and `instance.name` at start), `install_timezone()`, `install_today()`.
- Insight labels: `generated_by` accepts any short label (`^[a-z][a-z0-9-]{0,39}$`), default `harness-daily`.
- `save_document` (006's save, generalized to any subject, any https URL or none, provider `documents`).
- `claim_relationship_refresh()` and `finish_relationship_refresh()` for the engine's scheduler.
- `ingest_calls` and `ingest_freshness` for the ingest contract.

Existing principals get role `legacy`: they still read through their grants exactly as before, and reach none of the
role-gated entry points until they are given a role.

## 019 `definer_search_path` (new)

018 replaced `set_ambient_card` with `CREATE OR REPLACE`, which drops a function's settings, so it ran as its owner
with the caller's search path. 019 gives every `SECURITY DEFINER` function in the installation schema that has no
search path of its own (that one, and any an older install kept, such as a plugin function 008 no longer creates)
`pg_catalog, <schema>, pg_temp`, and revokes `TEMPORARY` on the database from PUBLIC. The migrate service revokes it
again at every run, so a restored database (a new one) loses it too.

## 020 `screens` (new)

New tables only, for live screens (`docs/dev/screens.md` 3.1): `screens` (one row per browser that has run the app)
and `screen_commands` (one row per command, kept 30 days), their indexes, and `SECURITY DEFINER` functions gated by role
with their search path pinned. It changes no existing table or function, and nothing carries over: the private
version had no screens, so an existing install needs no accepted checksum and no data step for it. Screens appear as
each browser first runs the app.

## 022 `composable_layouts` (new)

For composable layouts (`docs/dev/screens.md` 10.7). `dashboard_versions` gains a nullable `layout` column: every
version saved before 022 keeps NULL, its page alone, so every existing dashboard draws exactly as before. A new table,
`dashboard_layouts`, keeps the newest version's arrangement (at most one row per version, removed with it).
`dashboard_json` and `save_dashboard` are replaced with the same signatures (the first adds `layout`,
`layoutRevision`, `layoutNote` and `layoutSavedAt`; the second stores a layout, keeps the newest one for a next version
and copies the restored one), each with its search path pinned again, and `save_dashboard_layout` is new, for the
`app` and `harness` roles. No data step: nothing is arranged until someone arranges it. The number is provisional; it is
renumbered above `main`'s highest migration before it merges.

## 027 `derived_scopes` (new)

A subject and a scope on derived records (`docs/dev/memory.md` sections 5 and 8; SB-P1-06). `insights`,
`insight_runs` and `dashboards` gain `subject_id` and `scope`, and `ambient_cards` and `sky_pins` a `subject_id`, all
defaulting to the household's, so every existing row is `household`/`shared`; `sky_pins` is keyed by
(`subject_id`, `graph_id`). `insights` also gains `sources_checked`, null on every existing row: what an insight's
sources named, kept only for one saved with scopes on. `dashboard_writes` gains `subject_id`, filled once: a log write's from its target's subject
(its entry's), every other write `household`. The read and write functions of insights, runs, dashboards, their writes,
pins and corner cards are replaced by ones that take the stars their caller reads as a last argument with a default,
and the stars a pin cites likewise, so a call from the release before still resolves and, with `memory.scopes` off
(every install), is answered as before; each has its search path pinned. With scopes off every harness and app
principal reads every subject, as today, every new row is the household's and nothing new is refused (superseding an
insight saved private while scopes were on included). With scopes on, rows made before it are checked at each read by
what they draw on: a dashboard's stars and logs, an insight's stars and every locator of its sources not on the web.
`save_dashboard_layout` also takes, last and with a default, the subject of each map part's focus star, and with scopes on
refuses only a focus that would narrow the dashboard. For the live screens (SB-P1-15), `screens` gains `shown_by` and
`screen_commands` gains `reported_by`, the app login whose view a screen's showing and a command's outcome are, null on
every existing row; `listen_screen`, `done_screen_command` and `read_screen_command` are replaced with the same
signatures and `read_screens` with one that takes the reader's stars last, with a default, each with its search path
pinned. With scopes off neither column is read and every screens answer is 020's; with scopes on a row with no login is
told whole to no one until its screen reports again. The number is provisional.

## 028 `viewer_sessions` (new)

Viewer sessions in the app (`docs/security.md`, Viewer sessions; SB-P1-11). One new table, `viewer_codes`: the SHA-256
of each one-time code the operator makes for one person (`rhythm viewer invite`), when it expires (at most an hour after
it was made), and once used the principal it made, an `app` principal bound to that person (023 allows one, D-070). It
changes no existing table or function, and nothing writes it while `app.viewers` is off, every install's setting and
the only one this version accepts. No data step. The number is provisional.

## 029 `screen_binding` (new)

A screen's id bound to the app login that first listened with it (`docs/dev/screens.md` 3.2, `docs/dev/memory.md` 7.5;
SB-P1-19). `screens` gains `bound_to`, that login, null on every existing row and set by each such screen's next
listen from the household view, which is never refused for it (until then the row is the household view's alone). `listen_screen`, `listen_screen_again`,
`send_screen_command`, `done_screen_command` and `rename_screen` are replaced with the same signatures, each with its
search path pinned again, and refuse (`42501`) another app login's listen, report, send as the screen and rename; the
household view's screens are every login of the shared app token's, so a new app token keeps them, and a row not bound
yet is the household view's until a listen binds it. The 100-screen cap counts each login's screens apart. While
`app.viewers` is off every app login is the household view's, so nothing is refused and every answer is as before, with
`memory.scopes` off or on. No data step. The number
is provisional.

## 030 `person_corners` (new)

Corner cards per person (`docs/dev/memory.md` section 5; SB-P1-20). `ambient_cards` is keyed by (`subject_id`,
`corner`) instead of `corner`, and `dashboard_id` may be null on a person's row (their stat card), never on the
household's; every existing row is the household's (027), so none changes. `set_ambient_card`, `read_ambient_cards`,
`dashboard_json` and `delete_dashboard` are replaced with the same signatures and their search path pinned, and one
helper is added (`ambient_cards_of`). With `memory.scopes` off every corner is the household's and only those rows are
read, so every answer is 027's; with it on, a login bound to a person (a viewer session, only with `app.viewers` on)
chooses its person's corners. No data step. The number is provisional.

## 031 `document_scopes` (new)

Where a `save_document` copy lands with `memory.scopes` on (`docs/dev/memory.md` section 5; SB-P1-09).
`save_document` is replaced with the same signature and its search path pinned again. With scopes on it lands a copy
only for the caller's own person or, for a caller that reads Family, the household, and refuses any other subject
(`42501`) before it reads anything there, so a refusal says nothing about what another person holds; a subject left
out (null, which the tool now sends when the call names none) is the caller's own person, or the household for a
login with no person. With scopes off every answer is 017's: any harness login saves for any person or the household,
and a subject left out is the household's, the tool's default before. An engine of the release before always names
the subject. No data step. The number is provisional.

## 032 `own_corners` (new)

A person's own corner choice dropped again (`docs/dev/screens.md` 6, Undo; SB-P1-20, D-139). `set_ambient_card` and
`read_ambient_cards` are replaced with 030's signatures and their search path pinned again. With `memory.scopes` on, a
login bound to a person (a viewer session, only with `app.viewers` on) drops that person's choice in a corner with the
nil uuid as the dashboard, so the corner shows the household's again, and such a reader's corners carry `own`, the
corners that are its person's own choice. From any other login, and with scopes off, the nil uuid is an unknown
dashboard and no answer carries `own`, so every answer is 030's. No data step. The number is provisional.

## 033 `memstore` (new)

The native memory store's checked writers (`docs/dev/memory.md` sections 5, 6.2 and 10; SB-P3-01, SB-P3-02):
`create_memory`, `append_memory`, `update_memory` and `set_memory_status`, `SECURITY DEFINER` with their search paths
pinned, and their helpers; a new checked table, `memory_requests` (idempotency keys), with `checked_tables()` replaced to
list it; a check that no source share names the provider `memory` (the migration stops, naming it, on an install
that has one; `rhythm share add` never made one); and `memories.text_revision`, the revision a memory's text was last
written at, kept by a trigger, at which its passages are made (`PassageSources.Revision`, D-149). No data step and no
change to any existing function's answer: nothing calls the new functions yet, so every install keeps no native memory,
and an engine of the release before, which keys a memory's passages on its revision, finds none to key. The number is
provisional.

## Moving an existing install onto this repository (milestone 4)

Done from that install's private overlay, never from this repository:

1. **Accepted checksums.** Write a file of lines `<version> <old sha256>` for each rewritten migration (001, 008,
   010-016), computed from the private copies, and point the migrate service at it with
   `RHYTHM_MIGRATION_COMPAT_FILE`. The migrator then accepts either checksum for those versions. Lines starting with
   `#` are comments.
2. **Roles.** After 017 applies, give each existing principal its role once (for example the AI client's login
   `harness`, the app's reader `app`), or retire them and create new tokens with `token create`. The app's principal
   is re-created from `RHYTHM_APP_TOKEN_FILE` at every start.
3. **Graph ids.** Catalog stars' graph ids start with the catalog provider id, `rhythm` by default. An install whose
   pins, dashboards and insights name the old id keeps it with `RHYTHM_CATALOG_PROVIDER=<old id>` (open question Q-022).
   Set it for the app too, to the same value: the app container (`app.compose.yaml` passes the variable through) or a
   hosted front end (`deploy/vercel/README.md`). The app serves it at `/api/instance` and finds the data stars, saved
   documents and dashboards by it. Without it an added document opens without its text or original, a data star
   without its readings, and a pin or insight naming a dashboard's old star id finds no star.
4. **Tool names.** Set `engine.legacyToolNames: true` so the old tool names stay listed for clients set up against them.
5. **Star accounts.** The private version's Drive and Second Brain star ids carried its account identifiers
   (`drive:<account>:<file id>`); this repository's carry the provider (`drive:drive:<file id>`, open question Q-091).
   `RHYTHM_DRIVE_STAR_ACCOUNT=<old account>` and `RHYTHM_SECOND_BRAIN_STAR_ACCOUNT=<old account>` on the engine keep the
   old ids, so pins, dashboards, insights and the map's titles and links carry over without a rewrite. Set them in the
   overlay's compose file rather than `.env`: they are not secrets, but `rhythm doctor` may take a long one for one.
6. **Data star groups.** The private version named its data star groups in its own settings; `graph.catalog.groups` and
   `graph.catalog.providers` in `rhythm.yaml` give the same groups and labels, so the data stars keep their ids
   (`<catalog provider>:data:<provider>:<subject>:<group>`) and their measurements.
7. **Providers nobody owns now.** Records of a provider that no adapter or plugin here writes (one the private version
   imported) are served only with a `providers.<id>` entry giving its policy, usually `archive`.
8. **The install's own data.** Anything that differs in the data itself (a provider renamed per kind, a unit spelled
   differently, readings stored without an instant) is fixed by the overlay's own SQL after 017, never by a migration
   here.

## Studio (075)

`075_studio.sql` adds durable draft sessions, idempotency receipts and per-version typed selection manifests. Existing dashboard and screen tables and functions retain their contracts. It adds three security-definer functions with pinned search paths and revoked public execution: `studio_access`, `studio_change`, `studio_publish`. Publication locks the session and saved dashboard, compares the original version and layout revision, then calls the existing `save_dashboard` author/scope policy atomically. No existing migration is edited. A deployment must apply 075 before running the new engine; an older app may continue reading saved dashboards. The newer app reports Studio unavailable against an older engine. 075 is on `main` (`4afda144`) and deployed, so it is not edited again.

## Studio's search path (076)

`076_studio_search_path.sql` changes no table and no behaviour: it gives 075's three security-definer functions the
search path every other definer function here has (`pg_catalog`, the schema the migration runs in, `pg_temp` last),
where 075 had named `public`. An install that already ran 075 gets it at its next start, as any new migration.
