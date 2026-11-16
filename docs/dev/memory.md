# Memory scopes, identity and ownership: the contract

Who may read and change memory data once Rhythm holds private memories, and how an install opts in without anyone
seeing more than before; and who owns each record: Rhythm itself, or the source an adapter reads. Phase 1 of the Second
Brain parity program builds to this file (the program's backlog, SB-P1-02
to SB-P1-16). Nothing here is built yet unless it says **today**. Where this file and the code disagree, fix one of them
in the same change.

With `memory.scopes` off, the default and every existing install's setting, Rhythm behaves exactly as it does today:
the household shares everything ([../security.md](../security.md)). This file says what changes when it is on.

"Memory data" means every row a read can return, not only native memories: catalog records and their stars, links,
passages and vectors, health measurements, and what is derived from them (insights, dashboards, pins). One predicate
covers them all (section 7). Examples use the fictional household of the parity corpus
(`engine/tests/Knowledge.Tests/Parity/corpus.json`, all DEMO): `alex`, `sam` and Family; ids such as `mem-sam-party` are
the corpus's.

## 1. Words

| Word | Meaning |
|---|---|
| subject | whose data a row is: `subjects(id, kind)`, kind `person`, `household` or `synthetic` (001). Each person in `people` is one, added at start (`Core/Principals.cs` `Install`); `household` always exists; `fixture` is the demo's synthetic subject |
| person | a subject of kind `person`. A person taken out of `people` keeps their subject and rows: a *former person* |
| private scope | one person's own records: the rows whose subject is that person |
| Family, the shared scope | the household's records: the rows whose subject is `household`. `private` and `shared` are the stored words; the app says "Family" (D-044) |
| principal | a caller: a row in `principals` with one role (`system`, `harness`, `app`, `ingest`, `legacy`; 017; `capture`, 067) and, under this contract, at most one person |
| person-bound principal | a principal whose person is set (SB-P1-02, SB-P1-12) |
| household view | what a principal with no person reads while scopes are on: Family and DEMO rows, nothing private |
| member | a person on Family's membership list; only members read Family (section 4) |
| admin | a member with the admin role, who may change Family records others wrote (section 5). An admin never reads anyone's private scope |
| author | the person who wrote a native record (a memory, project, link, insight or dashboard). A record written by a principal with no person has no person author: it is the household's |
| owner | of a private record: the person whose private scope holds it |
| operator | whoever runs the `rhythm` command on the host. Not a principal: the CLI works on the database and files directly |
| native, adapter and derived records | section 10: Rhythm is the system of record for a native record; an adapter record mirrors a source Rhythm does not own; a derived record is computed from others (a star, a link, a passage, a vector, an insight, a cached title) |

## 2. Subjects and scopes

**Today.** Every catalog row (`resources`) has a `subject_id` and a `scope`, `shared` or `private` (001:5-15). Writers
follow one rule: the household's rows are `shared`, a person's are `private` (`Core/Ingest/Ingest.cs:293`,
`save_document()` 017:383, dashboard log entries 017:203, the notes, documents and Drive adapters' `Scope`). Two depart
from it: the Second Brain adapter's personal workspace is `private` under its `owner`, which defaults to `household`
(`Adapters/SecondBrain/SecondBrainAdapter.cs:38,54`), and the demo's notes are `private` under `fixture`
(`Adapters/Demo/DemoAdapter.cs:235,241`). Map stars copy their record's subject and scope
(`Core/RelationshipCatalog.cs:286-326`). With scopes off, no read by an AI client or the app looks at either column
(`readable_subjects` in migration 023, section 7).

**With scopes on**, a row's subject decides who reads it; the `scope` column describes it and is kept consistent by the
writers. This covers every row, health data included: Alex's heart rate is Alex's (the parity baseline counts it as a
must-hide record for Sam, [second-brain-parity/baseline.md](second-brain-parity/baseline.md)).

| Row's subject | Stored scope | Readable by, besides the engine's own jobs |
|---|---|---|
| a person, such as `alex` | `private` | principals bound to `alex` |
| a person | `shared` | nothing writes this; it is read as private to that person, the narrower reading |
| `household` | `shared` | Family: principals bound to a member, and principals with no person |
| `household` | `private` (the Second Brain personal workspace with `owner: household`) | Family, as today: the household is not a person, so nothing narrower exists; `rhythm doctor` names the setting (section 6.4) |
| `fixture` | either | every harness and app principal: fictional, labelled DEMO |
| a former person | either | no principal; the operator's offboarding moves their private memories to the trash (SB-P7-13, section 4) |

A principal reads nothing unless it is enabled, has role `system`, `harness` or `app`, and the row matches a line
above. A `legacy` principal reads nothing while scopes are on (today it reads through `grants`, and the perimeter
already refuses it at `/mcp`, `Mcp/Perimeter.cs:49`). An `ingest` principal reads nothing, as today. Upstream's
workspace names `personal` and `company` are accepted at the MCP boundary as aliases of private and Family (D-044), and
a filter that names a scope can only narrow what the caller already reads, never widen it (as upstream's layer
filters).

## 3. Principals bound to a person

| Role | Today | Under this contract |
|---|---|---|
| `harness` (an AI client) | reads everything | an optional person, set when the token is made (`rhythm token create <name> --person alex`, SB-P1-12) and never changed: to move a client to another person, revoke its token and make a new one. Every existing token has no person. **Since 044** a harness principal may instead be the terminal's own login for one person (marked `terminal`, made by `rhythm remember`, `recall` or `list`, or with `memory.tools` on by `rhythm release`, `capsule`, `delete-forever`, `forget` or `set-status` (and with `memory.share` on `share-memory`), with no token anyone holds and refused at the door): it reads and writes as a harness login bound to that person does, and it alone writes through the terminal's channel (section 5) |
| `app` (the app's server) | one principal; reads everything | the shared app token's principal is never bound: with scopes on it has the household view. The shared app token (`RHYTHM_APP_TOKEN`, held by every front end, a hosted one included) alone always reads the household view and can never name a person: the engine trusts no person sent beside it. An opt-in viewer session (SB-P1-11, D-046) reaches the engine with its own person-bound, revocable credential, made by the engine or the operator for that session, held by the app's server and never sent to the browser; the engine reads the person from that credential only. That credential is a principal of role `app` bound to its person (migration 023, D-070), so the perimeter serves it the app's tools; the shared token's principal is the one enabled `app` principal with no person, and the token file's sync (`Principals.SyncApp`) never makes a bound one the shared one; a new token in the file ends every bound one, whose credential the app's server derived from the old token. A header or cookie is never identity (`app/server/http.ts:19-21`). **Built, off** (SB-P1-11, migration 028; `app.viewers` off on every install and refused by this version, `ViewerSettings`, until D-046): the operator makes a one-time code for one current person (`rhythm viewer invite`, `viewer_codes` keeps its SHA-256, once, at most an hour); the app's server registers a new session's credential with the shared token and that code (`app_viewer` `enrol`, only the shared token's principal may), and the engine makes the credential's principal, bound to the code's person (`ViewerSessions`). The app's server keeps nothing: it derives the credential from the session cookie's random handle and the app token (`app/server/viewerScope.ts`), the HttpOnly cookie holds only the handle, and a cookie it did not make, or one past 30 days, is the household view. A suspended or removed person's session gets 401 at the door, as does every session past its 30 days (at the engine too, `ViewerSessions.Lifetime`) and every session while `app.viewers` is off (`Database.Authenticate`, told the setting by `Engine.Door`, refusing every session when not told it), and the app then clears the cookie (`ViewerSessionDatabaseTests`, `ViewerSessionScopeTests`, `app/tests/viewer-session.test.ts`) |
| `system` (the engine's jobs, such as `graph`) | reads everything | unchanged. It answers no reader directly; what it writes follows section 9 |
| `ingest` (a plugin) | writes its own providers and subjects (`Core/Ingest/Ingest.cs:43-49`) | unchanged |
| `legacy` | reads through `grants`; refused at `/mcp` | reads nothing while scopes are on |
| `capture` (a webhook, CI job or shortcut, 067) | reads nothing, in either mode; writes a memory only through `capture_memory` | an optional person, set when the token is made (`rhythm capture connect <name> --person alex`) and never changed: its one scope is that person's private scope once scopes are on, or with no person Family; with scopes off the household's. A call may name that scope and no other |

- A token bound to a former or suspended person is refused like a revoked one (401), whatever its `enabled` flag says.
  Suspending a person disables all their tokens and sessions (SB-P1-12): a session's credential is checked by the
  engine like a token, so suspending or removing the person makes it fail with 401 at the engine, whatever the app's
  server still holds. **Today** (SB-P1-12): `Database.Authenticate` refuses such a token (`Principals.PersonCurrent`),
  `rhythm person suspend <id>` disables every token bound to the person at once and for good (`rhythm person
  unsuspend <id>` lifts the suspension and leaves them disabled; someone the engine's last start did not find in
  `people` is former then, not current), the start that finds a person gone from `people` disables every token bound
  to them for good in the same way (`Principals.Install`), and `rhythm token revoke --all` disables every harness and
  plugin token and every viewer credential after a leak; the shared app token comes back from its file, so replacing
  it is a file change and `./rhythm up` (D-075).
- With scopes **off**, a binding changes nothing a principal reads: it records who wrote what, and the token is refused
  while its person is suspended or no longer in `people` (above). Binding tokens before switching on is the safe order.
- `rhythm token list` shows each token's person, or "household view" (**today**, with a person who is not current
  marked refused).
- A revoked token gets 401 at once (today). What it wrote keeps its author. What its client was already told stays in
  that client: Rhythm can stop answering, not take an answer back.

## 4. Memberships and admins

- Family's members are people (SB-P1-02). A principal bound to someone who is not a member reads that person's private
  scope and DEMO rows only.
- The first start with scopes on adds every person then in `people` to Family, so nobody loses the household's records.
  A person added to `people` later joins Family only when the operator adds them; joining is never automatic.
- The admin role is given and taken by the operator on the host, never over MCP or in the app. `rhythm doctor` warns
  when Family has no admin; then a Family memory can be changed only by its author.
- Leaving Family (or being taken out of `people`) ends Family reads at once. The person's private scope and the Family
  records they wrote stay, attributed (SB-P7-13). Taken out of `people`, a current person is former at the next start,
  and their membership, source shares and tokens end then (a token for good: an old device never comes back); listed
  again, they are current but rejoin Family, share again and have a working token only when that is recorded or made
  again. A suspension is the operator's, not `rhythm.yaml`'s: a suspended person taken out of `people` stays
  suspended, and listed again is still suspended, until the operator lifts it; their membership and source shares end
  when they are taken out all the same, and lifting the suspension while they are not listed makes them former, never
  current (the start records whom it found in `people`, `install_settings` key `people.listed`). The start logs how
  many people it marked former and how many memberships, shares and tokens ended, as counts only (`Principals.Install`).
- **Offboarding. Today** (SB-P7-13, migration 072; `Core/Offboarding.cs`, `Cli/OffboardCommand.cs`): when someone leaves
  for good, the operator runs `rhythm person offboard <id>` on the host, never an AI client (no tool or route reaches
  it). It first shows what it does and leaves, as counts and never a title, and changes nothing unless the operator
  types the id. Then, in transactions of its own: at once nothing bound to the person reads or writes (suspended unless
  already former, every token and session credential bound to them disabled for good, their terminal login and a
  capture connection made for them included, out of Family with any admin role, their source shares and any unused
  one-time viewer code ended); their private memories (subject the person) move to the trash in batches of 100, reason
  `offboard`, as a forget puts one there, the operator's (no principal, channel `cli`): nothing is deleted then, each
  keeps its row, text, versions and project, its links wait in its trash row, and a capture mirror's row goes first, as
  a disconnect takes it; and each private project of theirs that no memory is in any more, in the trash or out, is
  deleted (one `deleted` audit row, no words). The trash's retention (14 days) is the grace: brought back in that time
  (`person unsuspend`, or listed in `people` again), they restore what they want with a new token, they stay out of
  Family and their ended shares stay ended until `rhythm family add` and `rhythm share add` (unsuspend says so, and the
  switch's first start leaves out anyone with an offboarding, brought back or not), and the offboarding records the
  return (`offboardings.returned_at`, a trigger on `subjects`): from then on nothing of it moves, while they
  are current and once they are suspended or former again, until the operator runs `person offboard` again, which then
  shows what it does and asks for the id again. Otherwise 038's daily purge deletes each for good, and the engine's
  daily turn then deletes their emptied projects and records the offboarding finished (job `offboarding`, channel
  `system`; it starts none and moves nothing to the trash). A run stopped part way has ended their access already and
  has moved whole batches only; run again, it goes on from where it stopped and asks nothing, unless they were brought
  back meanwhile. The Family memories they wrote stay
  in Family with them as their author (an admin changes them; a take-back is refused, `no_author`, section 5). Their
  own records from adapters and plugins stay as their source keeps them; the records of theirs the core wrote
  (`save_document` copies, uploads and dashboard log entries, native, section 10.1) stay as they are, and so do their
  private insights and dashboards: with scopes on nobody reads any of them while they are suspended or former. The
  recall log's rows of their questions, if `engine.recallLog` was on, stay in their own scope until its daily prune
  takes each after 30 days; the preview counts each kind it leaves. The admin audit records each change as
  the operator's, and `offboarding_started` and `offboarding_finished` (`offboardings`, a checked table: ids and counts
  only). With scopes off, every install's default, nothing is private to anyone (D-048), so it ends the access bound to
  them and moves nothing to the trash, and says so; the switch's first start then leaves them out of Family (6.3), so their
  membership stays ended (`OffboardingTests`). **What it does not end**, which the preview names last
  (`OffboardPlan.Unbound`): the enabled harness, plugin and capture tokens bound to no person (the first token,
  `assistant`, among them; on the default install, scopes off with one shared token, that token keeps full MCP reads
  and writes), which the operator revokes by name if the person held one; the app, which has no login; and the adapters
  and plugins reading the person's own account, which keep adding their records until disconnected (an adapter whose
  `owner` is the person must get another owner before they leave `people`, or the start refuses the settings).
- **Source shares.** With scopes on, a person's own data from an adapter or plugin (`nightscout`, `garmin`, a CSV
  import, CareLink) is private to them, so a household whose app shows one person's glucose to everyone would lose it.
  A source share makes one provider's rows of one person readable by Family. Only that person (through a viewer session
  or a client bound to them, while it is enabled) or the operator on the host records or revokes one; never an admin
  for someone else, never automatically. A share is never rewritten: revoking deletes it and sharing again adds a new
  one. `rhythm doctor` lists them.
- Memberships, admin roles and source shares are audited like memory changes (SB-P3-04). **Today** (migration 037,
  `Core/AdminAudit.cs`): the admin audit, `admin_events`, has one row per setup change that decides who reads what: a
  person joining or leaving Family (with the role), an admin role given or taken, a source share recorded or revoked, a
  token or a viewer session's credential made, disabled or deleted, a person known, suspended, unsuspended, former or
  current again, and `memory.scopes` switched. Triggers on those tables write it, so every writer is recorded, each as
  it names itself for its transaction: the host commands as the operator (channel `cli`), the start as the system's
  job `start`, the app token's file as `app-token`, a viewer session's end of life as `viewer-lifetime`, a restore's
  quarantine as `restore`, the app's server as its login (`app`); a change no writer named is the system's, with no
  job. A command that changes nothing records nothing. Rows hold ids and names (person, principal, provider, role),
  never a token, a code or a record's words, and no key on what they name, so they outlive it. Like `memory_events` the
  trail is insert-only, for the owner too, and the engine's login only reads it; nothing serves it yet (an admin view
  is SB-P6-01's). The shared app token's principal and the engine's own are given to no one and are not recorded.
- **Today** (SB-P1-18) the operator records them with host commands, straight against the database
  (`engine/src/Cli/ScopeCommands.cs`, `Principals`; [../../cli/README.md](../../cli/README.md)):
  `rhythm family add <id>` and `family remove <id>` (a person joins or leaves Family at once), `family admin <id>` (an
  admin is a member with the role, so it adds them to Family if needed) and `family unadmin <id>` (they stay a member);
  `rhythm share add <person> <provider>`, `share revoke <person> <provider>` and `share list`. Only someone the engine's
  last start found in `people` (current, or suspended and still listed) joins, becomes an admin or shares; a suspended
  member reads nothing until the suspension is lifted. A share is recorded with no principal (the operator) and covers one adapter's or plugin's
  provider only: never `memory`, the core's own `documents`, `household-documents` and `log` (save_document copies,
  uploads and log entries) or the catalog's stars, which are shared one by one if at all. Taking someone out of Family
  is refused while scopes are off until the switch's first start has been: everyone reads Family then, and that start
  adds everyone in `people` but a person the operator offboarded (6.3). Once it has been (`install_settings` key `memory.scopes.joined`, kept through
  `rhythm scopes off`), a later switch-on adds nobody, so `family remove` with scopes off stands and says so. Each command says when Family is left without an admin. Each change is recorded in the admin audit as the
  operator's (SB-P3-04, above).

## 5. Who may change what

A private record is read by its owner only; a Family record by every member and by the household view. Changes follow
upstream's author lock (ledger SEC-04), with its gaps closed (below).

| Action on a memory | Private | Family, written by a person | Family, with no person author (written by a principal with no person) |
|---|---|---|---|
| edit, append, set status | owner | author or admin | any principal that reads Family, as today |
| trash (`forget`), restore from the trash | owner | author or admin | any principal that reads Family |
| delete forever: from the app or the CLI with the trash row's nonce, never over MCP (D-036) | owner, or the operator | author or admin, or the operator | admin, or the operator |
| share into Family | owner | already shared | already shared |
| un-share | n/a | author or admin; it moves back to the **author's** private scope, never anyone else's | not possible: there is no person to be private to |
| undo a change | the owner | whoever made it, the author or an admin | any principal that reads Family |
| read its history | the owner | the author sees all of it while in Family (one who has left Family is cut from its Family stretch, as anyone outside Family is); anyone else from the share that brought it into Family on (as upstream, SB-P3-03) | all of it |
| link it to another record (person- or AI-made, SB-P4-02) | owner; both ends in the same scope | author of the link's source end, or an admin; both ends in Family | any principal that reads Family; both ends in Family |
| remove a person- or AI-made link (one an import brought has no maker, 043: the rest of the rule; never a supersede's, which only `undo` or a retraction takes away) | its maker, or the owner | its maker, the source end's author (either end's for `relates_to`, which has no direction), or an admin | any principal that reads Family |
| dismiss an engine suggestion between two records, or take a dismissal back (SB-P4-05, 057: a person's channel only, the app's login or the terminal's; an AI client's `unlink` dismisses and never takes one back) | owner | author of either end, or an admin; for a record that is no memory, any principal that reads Family | any principal that reads Family |
| dismiss a memory from Home's old memory (SB-P5-17, 070: a person's channel only, the app's login or the terminal's; it changes no memory, and holds for the one who dismissed it; a dismissal by the household view, or with scopes off by anyone, holds for every reader) | the owner | anyone who reads it | anyone who reads it |
| cite a Drive document, or revalidate its citations (SB-P4-10, 055, MCP only) | owner (the author lock, as an edit) | author or admin | any principal that reads Family |

| Action on another record | Rule |
|---|---|
| a project: create | in your private scope, or in Family by any member |
| a project: rename, alias, archive, delete (its memories kept) | private: its owner; Family: its creator or an admin; a Family project with no person creator: any principal that reads Family |
| an insight: confirm or dismiss it (SB-P5-06, migration 066; `review_insight`) | private: its owner; Family: its author person or an admin; no person author (the daily insight with an unbound token): an admin; and never, over MCP, the login that saved it. **Today** with scopes off any admitted login but that one. Its review alone changes: no memory, and a confirmed insight is not recalled (D-033's line: the engine records what a person decided, and a memory of it is one `remember` away). Replacing a confirmed insight (`save_insight`'s `supersedes`) closes it, so it takes the same rule, or nothing is saved, and the history keeps who did (`replaced`) |
| an insight: feedback (interesting, explore, later, not useful, handled) | anyone who reads the insight, as today (`app_insight_feedback`), recorded per principal with `memory.tools` on (`insight_events`, migration 066) |
| a contradiction: say two memories conflict (SB-P5-03, migration 068) | anyone who reads the pair's scope, only a candidate offered for that memory; neither memory changes, so the author lock does not apply. An automatic link between them comes off only when the caller may change either memory, as removing one takes; otherwise it stays for the person's settling, and no new one is drawn |
| a contradiction: settle it (SB-P5-03, migration 068) | a person's channel only (the app's login, or the terminal's), never an AI client's. Keep both: whoever may change either memory. One replaces the other: as a supersede, whoever may change both. While it stands an AI client can't supersede the pair, end its earlier memory or move either to another scope |
| an insight candidate: use or reject (SB-P5-05, migration 064) | anyone who reads the pair's scope (both its memories are of one scope), as an insight's feedback; one decision a candidate, kept with who made it; neither memory changes, so the author lock does not apply |
| a dashboard: edit, delete, allow edits | private: its owner; Family by a person: its author or an admin; every existing dashboard has no person author and keeps today's rules. Agreed with the screens program. **Today** (SB-P1-06, migration 027) with scopes on: the same rule for a next version or a restore, writing from it (a log entry, a Sheet or Doc change) and undoing a write, and adding, removing or re-pointing its layout's parts; arranging it (the same parts moved, resized or refocused) is any reader's, as a corner card is, except that a saver bound to a person, arranging or composing (its author and an admin included), focuses a Family dashboard's maps only on stars it names already, a Family or DEMO star included (section 8). A next version or a restore that would take a Family dashboard private is its author's person's alone (an un-share): an admin or another member saves a new dashboard instead. Every one is refused to a principal that can't read the dashboard as for an unknown dashboard (`may_change`, `dashboard_write_scope`) |

- Admin powers are Family's only: an admin never reads, edits or un-shares a private record.
- **Today** (migration 033, SB-P3-02): the first row is built in the memory store's checked writers, `append_memory`,
  `update_memory` and `set_memory_status` (`memory_may_change`; `Core/Memories.cs`), reached by tests only, since no
  tool writes a memory yet (D-050). A memory the caller does not read, one in the trash and an unknown id answer alike
  (`unknown`); one it reads but may not change is refused (`42501`), and nothing changes (`MemoryAuthorLockTests`). The
  audit trail records the channel: the app's login through the app, an AI client over MCP, and the terminal only for a
  login bound to a person (D-149). It has one row per change (and one for a project a write makes), none for a write
  that changes nothing, a replay or a refusal, each with the principal, the channel and the client's name as the
  client gave it, cleaned (controls, formatting characters and angle brackets removed, white space folded) and cut to
  80 characters, never a reason to refuse the write (`AdminAudit.Client`, SB-P3-04; `MemoryAuditTests`). Through the
  memory tools that name is the one the MCP request carries: `/mcp` is served stateless, so a client is named only when
  each request names it (protocol 2026-07-28 on), and a client on an older protocol, which names itself once at
  initialize, is recorded with no client (`MemoryToolStatelessTests`). It is a label, never the channel. Reading its
  history is built too (next point); the other rows wait for their items.
- **Today** (migration 034, SB-P3-03): reading its history is built in the store (`MemoryStore.History` and
  `GetVersion`, `Core/MemoryHistory.cs`), reached by tests only. A reader of the memory sees its history from the change
  that brought it into a scope the reader reads (`memory_history_floor`): each state and audit row keeps the subject the
  memory had at its revision, and the cut falls just above the newest one in a subject the reader does not read, so
  its author sees all of it while the author reads every scope it has been in, every reader of a Family memory no
  person wrote sees all of it, and with scopes off everyone does. An author who has left Family is cut from its
  Family stretch, as anyone outside Family is, and that stretch is named as nobody's (`EarlierBy` names the author
  only for history in the author's own scope). A read by revision from before the cut answers `not_visible`, as
  upstream. A share or un-share (SB-P3-10) is a change: it retires the state it replaces, moves the revision and
  records its audit row under the subject it lands in, so its arrival is the first revision new readers see. A state
  that was held is served without its words unless a later state the reader sees holds the same text released, and
  then with its tags and project only when that state holds the same ones (a release vouched for nothing else). The
  20 newest earlier states are kept (D-036); every audit row is kept, so an older change is listed without its words
  (`MemoryHistoryTests`).
- **Today** (migration 035, SB-P3-07): the project row is built in the store's checked writers
  (`create_memory_project`, `update_memory_project` for its name, description, aliases and archive or restore, and
  `delete_memory_project`, which keeps its memories: each leaves it at a new revision with the state it had kept as a
  version and one audit row; `Core/MemoryProjects.cs`), reached by tests only. An unknown project and one the caller
  does not read answer alike (`P0002`); one it reads but may not change is refused (`42501`), by
  `memory_may_change`'s rule above; a stale expected revision is refused (`40001`); a slug or alias another project
  of the scope answers to is refused (`23505`, `memory_project_names`). A memory write that names a project being
  deleted at that moment waits for the delete and makes the project anew (`MemoryProjectTests`).
- **Today** (migration 038, SB-P3-06): the trash row is built in the store's checked writers (`forget_memory`,
  `restore_memory` and `delete_memory_forever`; `Core/MemoryTrash.cs`), reached by tests only. A forget comes from any
  channel and deletes nothing: the memory keeps its row, text, versions, revision and project, its `resources` row is
  marked deleted so no read serves it from that transaction on (get, list, history, recall, the map, its passages), and
  its person- and AI-made links (`made_links`) wait in the trash row, 14 days (`memory_trash_days()`, D-036). A restore
  brings back each link whose other end is still in the catalog and counts those that are not (since run 19's fix review
  also an automatic link whose other end moved to another scope meanwhile: 062's trigger on `made_links` skips it, so
  none comes back joining two scopes, `A_restore_brings_back_no_automatic_link_whose_other_end_moved_to_another_scope`),
  at the revision and state
  the memory had, held still if it was held; neither moves the revision, and each has its audit row (`trashed`,
  `restored`). `MemoryStore.Trash` lists only what its reader may restore, newest first, with the days left and the trash
  row's nonce. Delete forever is refused through an AI client's channel (`42501`) and without the nonce or a
  confirmation equal to the memory's id (`22023`); a nonce of an earlier trip, or of another memory, answers as for an
  unknown memory. It removes the memory with its versions, trash row and kept links, its `resources` row with its
  passages, its links and the map's star rows for it (their titles are excerpts), and keeps its audit trail with one
  `purged` row and none of its words. The operator on the host may delete forever too (no principal, channel `cli`).
  The engine's daily job (`MemoryTrash.Purge`, the scheduler) deletes what has stayed past its retention, at most ten
  batches of 100 a run, as the job `memory-trash` (channel `system`, no principal), and prunes idempotency keys older than
  30 days (`MemoryTrashTests`, `MemoryTrashPurgeTests`: it deletes nothing an install had before native memories).
- **Today** (migration 041, SB-P3-05): the undo row is built in the store (`MemoryStore.Undo`, `Changes` and
  `UndoGroup`, `Core/MemoryUndo.cs`; `undo_memory`), reached by tests only. An undo puts a memory back as it was before
  its newest change, or as it was at a revision the caller names, as a change of its own: a new revision, the state it
  replaces kept as a version, one audit row (`reverted`, with the revision it went back to and the fields it changed),
  so an undo is undone the same way. It brings back that revision's text, tags, status, kind, importance, volatility,
  validity, capsule slot and project (a project deleted since stays gone), never its scope, author or source: a revision
  in another scope is refused (`moved`; undoing a share is un-sharing, SB-P3-10's). Only the history the caller sees
  (034's floor) is there to go back to (`not_visible` before it, `pruned` past the 20 kept). Who may undo: the first
  row's rule, plus anyone whose own changes are all a Family memory's newest (whoever made a change may undo it, as
  upstream's "a member may undo their own newest change"); with scopes off every admitted login. That is checked before
  anything else is answered, as 033's writers check it: one who reads the memory but may not undo it is refused
  (`42501`) whatever revision it names, the current one or a stale one included. Holds: a held
  revision comes back held (an undone release holds the memory again), new words an undo brings back are screened by
  036's trigger like any change's, and the same words keep their hold unless a person undoes (the app or the terminal):
  an AI client never undoes its way to a release (D-039). A group is one login's changes of one kind (edits, status
  changes, releases or undos) through one channel and client, each within ten minutes of the one before, as the
  caller reads the trail; its key names its first and last change by memory and revision, and `UndoGroup` finds its
  members again from that key at every call (at most 1 000 rows, else no group), undoes five memories a call, each
  only while the group's changes are its newest (`changed_since` otherwise, touching nothing), passes over those
  already undone, reports and passes a member it may not undo (`needs_person` when only a person may, 068) or whose undo
  writes nothing without counting it among the five, so every call reaches further and the group is done once each
  member is tried, and answers with the key of its own undos, which undoes them and stops at an undo of anything else, so a redo never puts back what its person undid apart from the group (`MemoryUndoTests`, `AppMemoryListTests`). An undo brings a project slot back with its project, leaving the
  slot it fills now in the same change (039's rule). An undo takes no idempotency key: its expected revision makes a
  retry safe. The brief's AI changes read `Changes` (SB-P5-08), and the app's Memories view reads them too and takes a group back
  (`app_memories`' `changes` and `undo_group`, SB-P6-01). Not built: undo of a forget, which is the trash's restore (SB-P3-06).
  The undo tool is SB-P3-09's and the app's undo SB-P3-12's (below).
- **Today** (migration 045, SB-P5-01): bursts, channel factors and the review queue. A memory write through an AI
  client's channel (`mcp`: a create, an append or an update) by a login that has made 40 such writes in the last ten
  minutes is held, reason `burst`, whatever its words (SEC-13; a person's two AI clients are two logins, counted
  apart); one its words hold keeps their reason with the burst added to its signals, and one held before keeps its
  hold. An undo, a status change, a release, the trash, an import and a change the engine makes for another step (its
  audit row names a cause, as a project's delete moving its memories out does) are not counted. On a person's own
  channel, the app through a viewer session's login (an app login bound to a current person, so a person is signed in;
  `app.viewers` stays off and refused, D-046) or the terminal through the person's own terminal login (044, which no AI
  client holds), instruction phrases never hold and hidden characters and length still do, a released memory's change
  held again only for a hidden character it adds (D-037, SEC-11); every other channel is scored as an AI client's: the
  app's shared login, MCP and an import. A write through those into a project a held memory made is held too, reason
  `held_project` (046, section 7.1), and so is one that makes a project whose slug reads like instructions, reason
  `instruction` with the slug's signals (since run 12's fix review). Capsule changes over MCP stay refused outright (D-172). All are decided as the
  write's audit row goes in (`memory_event_channel_hold`), which names the login and the checked channel, so the row
  and the reply say what held it. The review queue (`MemoryStore.Review`, `ReviewCount`, `Core/MemoryReview.cs`) lists the held memories
  a reader reads, newest held first, each with its reason, score, the signals and the login, channel and client of the
  write that held it, and whether the caller may release it through the channel it names (the author lock, never
  `mcp`), never its words, tags or project, which `Get` serves a person; a count is the length of its list.
  `ReleaseAll` releases several at the revisions a person reviewed, each answered on its own (one changed since is a
  conflict and stays held), never through `mcp` (`MemoryQuarantineTests`, `MemoryReviewTests`). The queue reads the
  holding audit row from the reader's history floor on, as history does. **Today** (SB-P3-22, only with `memory.tools`
  on) a person's channels serve it: the app's Memories view lists the queue (`app_memories` `review`, with `count` the
  queue's length by the same filters) and releases several at the revisions its person read there (`release_all`, each
  answered on its own: one changed since is a conflict and stays held), a memory being chosen only once its words were
  read; the terminal lists it, shows one held memory whole with every character no person sees as its code, and
  releases it only at the revision read (`rhythm release`, through the person's own terminal login). No MCP tool lists
  it or releases (`No_AI_clients_token_reaches_a_persons_step`).
- **Today** (migration 067, SB-P7-01, only with `memory.capture` on beside `memory.tools`): the capture channel. A capture token's
  write goes through `capture_memory` alone: a draft in its one scope (section 3), never canonical before a person confirms it
  (`set_memory_status` and `undo_memory` refuse an AI client asking, on anything a capture made, by its own audit trail, so on its
  own install: an export carries no such thing),
  with no kind, importance or validity from outside, without the tag `task`, which a capture drops, so an outside source adds nothing
  to what the person owes, and with no `source` that reads like instructions, which is refused, since AI clients read a source and a
  held memory's too; its audit row saying channel `capture` and the connection's name. The channel
  is scored as an AI client's (`memory_own_channel` is false for it, and 045's held projects and 036's screening read it so), and a
  burst past a connection's 40th write in ten minutes holds the next (`memory_event_capture_burst`, 045's rule). Nothing else takes
  that login, and only a person releases what it wrote. A **mirror** (`memory_mirrors`) is a capture that keeps an outside item's
  id: the same connection (a token's name) capturing that id again changes that memory, and while the connection stands nothing
  else changes it: `memories_mirror_read_only`, on the table, refuses every change but a hold's, a revision's and a project's delete
  taking it out (`delete_memory_project` names that project for its one loop, so no other writer clears a mirror's project),
  and `memory_trash_no_mirror` keeps it from a forget. `disconnect_capture` (the operator's, `rhythm capture disconnect`)
  disables the token and moves its mirrors to the trash, reason `disconnect`, with their links, their mirror rows gone, so a person's
  restore brings back an ordinary draft; an AI client's is refused (`needs_person`, `restore_memory`), or what an operator took out
  could be back in recall with no person (`CaptureEndpointTests`). With `memory.capture` turned off, `rhythm capture list` and
  `disconnect` still answer once a capture token has ever been made (`CaptureCommandOffTests`). Not built: a capture linked on save or flagged as a copy at capture, one
  mirror taken out of its connection, moving a connection's mirrors to another scope (API-50).
- **Today** (SB-P7-12, `Mcp/WriteLimit.cs`, only with `engine.writeLimit` on): the limit behind the burst. A token past 300 writes in 10
  minutes, every write tool counted, has its next write refused, code `write_limit`, and nothing saved, until its oldest write leaves
  the window. It stands behind 045's rule on the same window, so what comes past 40 is held for a person first. The window slides, so it
  slows a token that keeps writing to 300 in any ten minutes and does not stop it, and nothing caps the review queue. The app role and the
  terminal are never counted, the app's shared login included though it is no person's own channel (above), and a read never is
  (`WriteLimitTests`, `docs/security.md`).
- **The channel is the caller's word** (since run 10's final review). The steps that only a person takes (a release,
  036; an undo that clears a hold, 041; a capsule slot, 039; delete forever, 038; the trash list's `MayDeleteForever`)
  tell a person from an AI client only by the channel the caller passes: `app` for the app's login, `mcp` or `cli` for a
  harness login, `cli` only for one bound to a person (`memory_channel`, 033; since 044, `cli` only for the terminal's
  own login, below). A person's own AI client holds the same kind of token as `rhythm remember` (D-149), so nothing in
  the database stops a caller naming `cli` for it. Two rules close that: every MCP memory tool passes `mcp`, fixed in
  the handler and never taken from a client's argument or header, with a test per tool that a call cannot reach `cli`
  (SB-P3-09); and the terminal gets a login the database can tell apart (a principal role or flag, or the operator's
  path with no principal), so `memory_channel` refuses `cli` for an AI client's token (SB-P3-19). Until both are built,
  no tool or route calls these writers through `cli`, and "never over MCP" holds for the `mcp` channel
  only. **Today** the first is built (SB-P3-09, `Mcp/MemoryTools.cs`, only with `memory.tools` on): every MCP memory
  tool passes `mcp` from a constant in its handler; no tool takes a channel or a client argument (a call naming one is
  refused before anything is read), no header or client name is read for it, and a person-bound client whose call says
  `cli` every way it can is recorded as `mcp` on every change, has `update`'s capsule tags and an `undo` of a slot
  refused, never releases a hold by undo, and is offered no delete forever in the trash
  (`Every_memory_tool_reads_and_writes_as_an_AI_client_whatever_the_call_says`, `A_call_cannot_name_its_channel`).
  **Today** the second is built too (SB-P3-19, migration 044): the terminal speaks through a login of its own for each
  person, a principal of role `harness` bound to that person and marked `terminal`, which `rhythm remember`, `rhythm
  recall` and `rhythm list` make on the host on first use, as do `rhythm release`, `rhythm capsule`, `rhythm
  delete-forever`, `rhythm forget`, `rhythm set-status`, `rhythm link`, `rhythm unlink`, `rhythm still-true`, `rhythm
  clear-flags`, `rhythm undismiss`, `rhythm queues`, `rhythm duplicates`, `rhythm contradictions`, `rhythm insight-candidates`, `rhythm insights` and
  `rhythm resolve` with `memory.tools` on, and `rhythm share-memory` with
  `memory.share` on too (`engine/src/Cli/TerminalLogin.cs`), one enabled at a time per
  person. Its token hash is of random bytes nobody keeps, so no token for it exists to give an AI client, and the door
  refuses it anyway (`Database.Authenticate`); its mark, person and hash never change. 044's `memory_channel` takes
  `cli` from a terminal login alone and `mcp` from every other harness login alone, so an AI client's token, a
  person-bound one included, is refused the terminal's channel through every checked writer, and the terminal's login
  never writes as an AI client; the trash list offers delete forever on the terminal to that login alone
  (`The_database_tells_the_terminals_login_from_an_AI_clients`). So the channel is the login's, not the caller's word.
  **Today** (SB-P3-22) the terminal takes a person's own steps through that login, only with `memory.tools` on (with
  it off each answers as a command the engine does not know): `rhythm release` (the review queue, one held memory
  whole, its release at the revision read), `rhythm capsule` (a capsule slot, 039) and `rhythm delete-forever` (from the
  trash, with its nonce, once the person types the memory's id, 038), each recorded as `cli`, client `rhythm`, under
  that login. **Since run 14** the same login also takes `rhythm forget` (the trash, 038), `rhythm set-status` (033's
  writer) and the capsule view, `rhythm capsule` with no memory named, which shows one capsule as an AI client that
  reads it is served it (`PromptCapsules.Build`) with the memories that wait in its slots (`PromptCapsules.Waiting`: a
  draft, one marked wrong, one past its validity, and one held, by id and slot only) and writes nothing; and with
  `memory.share` on beside `memory.tools`, `rhythm share-memory` (048's `share_memory`, below), the terminal's share,
  a person's channel for their own scope (`PersonShareDatabaseTests`). **Since run 17** it also takes a person's link
  steps (`engine/src/Cli/MemoryLinkCommands.cs`): `rhythm link` (a memory's links and the map's suggestions between it
  and another memory, or a person's link from it to another of its scope, 052's `link_memories`, `verified`), `rhythm
  unlink` (052's `unlink_memories`), `rhythm still-true` (the stale queue the person may act on, or one confirmed, 053's
  `confirm_memory`), `rhythm clear-flags` (049's `clear_memory_flags`) and `rhythm undismiss` (the dismissals on the
  person's map, or one taken back, 057's `restore_suggestion`, over the map as the engine last saved it, never
  rebuilt), each through the same checked function as the MCP tool or app op of its name, so the author lock, one
  scope and the holds decide as there (`PersonLinkDatabaseTests`, `PersonDismissalTerminalTests`).
  With the first, every MCP tool still passes `mcp` itself; no app route or tool takes `cli`. Suspending the person,
  `rhythm token revoke --all` and a restore's quarantine disable a terminal login as they disable every token; the next
  command for a current person makes a new one. A missing channel, or a login that is not there, is refused too.
  **Today** the app's Memories view passes `app` (SB-P3-12, `Mcp/AppMemories.cs`, only with `memory.tools` on), from a
  constant in its handler, and it is a person's channel by construction: `memory_channel` takes `app` only from a login of the app's role, which only the app's server
  holds (the app token, read from its secret file, or a viewer session's credential, while `app.viewers` is on), and an
  AI client's harness token reaches neither the app's endpoint nor that channel. No op takes a channel or a client argument
  (refused as outside the schema, 400), no header is read for it, and every change it makes is recorded as `app` with no
  client (`Every_op_reads_and_writes_as_the_app_whatever_the_call_says`). So the app may release a hold (036), undo its
  way to a release (041) and delete for good from the trash with the nonce and a confirmation equal to the memory's id,
  which the app asks its person for (038), and its trash offers delete forever (`MayDeleteForever`); since SB-P3-22 it
  also puts a memory into a capsule slot or takes it out (`capsule`, 039) and serves the review queue with a release of
  several (`review`, `release_all`), as the household view or a viewer session bound to its person
  (`With_scopes_on_the_app_slots_only_what_its_login_may_change`); since run 17 it also reads a memory's links
  (`connections`) and the stale queue (`stale`), and makes and removes a person's link (`link`, `unlink`), says one is
  still true (`still_true`) and takes a memory's flags off (`clear_flags`), through the same checked functions as the
  MCP tools (`PersonLinkDatabaseTests`). With scopes on the app token is the household view: it reads Family and DEMO, writes a
  memory only into Family and only when the call names it, and changes a Family memory a person wrote only as the
  author lock allows, which for a login with no person and no admin role is never
  (`With_scopes_on_the_app_reads_and_writes_as_the_household_view`).
- **Today** (migration 048, SB-P3-10, Q-157): the share and un-share rows are built in the store's checked writer
  `share_memory` (`MemoryStore.Share`, `Core/MemoryShare.cs`), served by the MCP tool `share` only with `memory.tools`
  and `memory.share` both on, each off by default (7.1); real use waits for the owner's per-person scopes (D-013). With
  scopes off it is refused (`scopes_off`) before anything is read, and nothing changes. With them on, a private memory
  is shared into Family by its owner alone, and only as a member of Family; a Family memory a person wrote is taken back
  by its author or a Family admin, always into its **author's** private scope (an admin who asks no longer reads it, and
  its answer names no project there), and one no person wrote cannot be taken back (`no_author`), nor one whose author
  is no longer a current person (former or suspended: section 2's table, where no principal would read it, `no_author`
  too), nor one linked to another person's private record (two people's private records never link, below; refused,
  `invalid`, and the link stays). A held memory is shared only once a person has released it (`held`); taking a held
  one back narrows, and keeps its hold. A move to where the memory is already answers unchanged and writes nothing,
  whatever expected revision it carries (a retry after a lost answer); it takes the author lock first, as every change
  does, then the expected revision, and no idempotency key. A caller the move left no longer reading the memory (an
  admin who took someone else's memory back) is answered unknown on a repeat, as for any memory it does not read, so no
  answer of its tells it where the memory is. Like any change it retires the state it replaces, moves the revision and writes one audit row,
  `shared` or `unshared`, under the subject it lands in (with where it came from, the fields it changed and how many star
  rows it dropped), so 034's floor starts a new reader's history at it, and an undo of it is refused (`moved`). The
  state a move retires is marked moved, and 034's prune (replaced in 048) takes such a state first, never the newest kept
  one, when a memory is over its twenty: a move changes no words, so moving a memory back and forth evicts at most one
  earlier edit (the first move's, as any change's would) and never every one, which a loop of twenty moves otherwise
  would; each move still writes an audit row, never removed. What
  follows the memory: its subject and scope (its passages and vectors with them), its author, its person- and AI-made
  links (`made_links` keep their ids; each is served only to a reader of both ends) and its project by slug, the landing
  scope's project of that slug or alias taking it or one being made there with the slug as its name (no private
  description travels). Through a channel scored as an AI client's a clean memory never joins a project a held write
  made, nor makes one whose slug reads like instructions (045, 046): it then comes with no project (`projectLeft`), so a
  share is never the step that lists such a project; a person's own channel (the terminal) is, for their own scope only:
  someone else taking a memory back (an admin, from any channel) joins only a project of its slug already listed in the
  author's scope, and makes none there. Its capsule slot does not follow: a slot is a person's step (D-172), so a
  person's move takes it out, and a person slots it again; through an AI client's channel a memory in a slot is not
  moved at all (`forbidden`, as 041 refuses an undo that would change a slot). What is dropped: the map's star rows
  for it, with the map's links to them and their evidence (section 8); the tool then asks for a rebuild, at most one
  every three minutes, so a client looping over it never keeps the map rebuilding, a rebuild running then counting only
  by when it was asked for (section 8). It locks the recorded mode's row
  `FOR SHARE` first, so `rhythm scopes off` waits for an un-share into a private scope, then the memory's `resources` row
  before its `memories` row (`MemoryShareTests`, `MemoryShareToolTests`, `MemoryShareToolDatabaseTests`; the history and
  undo tests move memories through it too). **Since run 14** a person's channels take it too, behind the same two
  switches: `app_memories` `share` (`scope` shared or private, `expected`; Mcp/AppMemories.cs) as the app, listed only
  while memory scopes are on as well and only to an app login that speaks for a person, and `rhythm share-memory`
  through the person's own terminal login (`cli`), each asking for the same bounded rebuild. The household view (the
  app token, no person) reads no private memory and changes a person's Family memory only as the author lock allows,
  so it shares nothing and takes nothing back (`unknown`, `forbidden`), and is never listed the op; the app's login
  that does is a viewer session bound to its person, which `app.viewers` (off and refused, D-046) gates. So in this
  version the Memories view, whose facade reads the app token's listing, offers no share (run 14's integration). An AI client's token is refused both channels by 044's
  `memory_channel` (`No_AI_clients_token_reaches_a_persons_share_or_terminal_step`).
- **Today** (migration 049, SB-P4-06 and SB-P4-08; story 6): when a memory was true, one memory replacing another, and
  the retraction cascade, in the store's checked writers (`set_memory_validity`, `supersede_memory`,
  `clear_memory_flags`; `Core/MemoryTruth.cs`), reached through the memory tools only with `memory.tools` on (7.1). Each
  takes the first row's author lock, and each change retires the state it replaces as a version, moves the revision and
  writes one audit row, so history reads every earlier window in its own words and undo (041) puts one back: nothing
  rewrites a version. A window is set alone (`validity_changed`): a start and an end that have passed (a time after now
  is refused: a plan is in a memory's words), the end after the start; cleared, since it was written and still true. A
  supersede is the caller's word, not a model's (D-033): it names the memory true now and the earlier one of the same
  scope it replaces, both the caller's to change; it answers a plan first, writing nothing (the earlier one stops being
  true where the newer began), with a confirmation that changes with either memory's revision, and only that
  confirmation applies it, in a second call (the tool tells the client to ask the person first; the engine does not
  check who agreed, since the plan's answer carries the confirmation): the earlier memory's end becomes the newer's
  start (`superseded`), a `supersedes` made link (D-067) records what replaced it, and it keeps its words and status.
  Windows are compared, not arrival order: a newer memory that began at or before the earlier one is refused, and one
  that had ended by then, or a newer one told as over already within its time, answers unchanged. A confirmed
  (canonical) memory is protected: only a confirmed one replaces it (`protected`), and an AI client may confirm the
  newer one too (`set_status`, told to only once the person agrees), unless a capture made it, which only a person
  confirms. Held and deprecated memories neither replace nor
  are replaced. A memory's state is read with it (`memory_validity_state`): retracted (deprecated), current (no end, or
  one still to come), replaced (ended, and a live memory of its scope, not held, deprecated or in the trash, supersedes
  it and starts where it ends: `memory_replaced_by`) or ended; a replaced or ended memory leaves recall, the map and
  capsules as any memory past its end does, and get, lists and history keep it. Since SB-P4-11, with `memory.tools` on
  a replaced memory stays a star on the map, listed after every current one and served with `truth` naming what
  replaced it, which the app draws faded, never hidden (`MemoryStars.Served`, `RelationshipCatalog.Replaced`;
  `With_memory_tools_on_a_replaced_fact_stays_a_star_naming_what_replaced_it`); an ended one is still no one's star,
  and with `memory.tools` off the map is as before. When its start moves, what it replaced
  ends at its new start too (at most 10, never at or before their own start), by `set_memory_validity` or an undo that
  moves the start alike; that gives the replaced memory an end it never had, so the move is refused (`forbidden`,
  nothing changed) unless the caller may change each of them too (the author lock). The cascade runs from the audit row
  of a change that retracts a memory (deprecated by a status change or an undo, or put in the trash) or brings it back,
  with that row's login, channel and client, in the change's own transaction: retracted, each memory it had closed at
  its start reopens to its own end (`reopened`; a third memory that had replaced it then replaces the reopened one too),
  and each memory built on it (a `drawn_from` or `caused_by` made link, one hop, its scope) is flagged for a check
  (`memory_retraction_flags`, `flagged`: no revision moves, nothing is changed or blocked); back, each it reopened
  closes again if the newest change to its words or its window (an append, or a change naming content or validity: a
  status, tag, capsule slot, release or flag counts as neither) is that reopening, or one by a source still retracted
  (`reclosed`; so a chain retracted in any order leaves one current fact; one whose words or window someone changed
  since stays as they left it, and an undo of the supersede itself leaves both current, by design), a link the cascade
  made for "replaced by" goes then, and its flags come off (`unflagged`); at most 25 of each. A reopen and a reclose
  only put back a window a supersede set, as the consequence of a change the caller may make, so they take no author
  lock on the memory they move. The states a cascade's step retires are marked (`cascade`) and pruned before any state
  someone wrote (049 replaces 048's prune). Whoever may change a flagged memory takes its flags off once it is checked
  (`flags_cleared`): an AI client with `update`'s `clear`, and since run 17 a person in the app (`app_memories`
  `clear_flags`, the flagged note's "It still holds" button) and at the terminal (`rhythm clear-flags`), through the
  same `clear_memory_flags`. The change's answer names what its cascade did (`MemoryCascade`): an MCP tool's `message`, and at
  the terminal `rhythm set-status` and `rhythm forget` (since run 14's second review), and since SB-P4-11 the app's
  `app_memories` writes (`cascade`), which its Memories view says in words; its `get` and lists carry each memory's
  `state`, `replacedBy` and `retractedSources`, which the view shows
  (`The_app_names_a_memorys_truth_and_what_its_cascade_did`). 041's own-changes rule
  leaves the flag rows out, so another person's retraction never stops a member from undoing their own newest change
  (`With_scopes_on_a_flag_from_someone_elses_retraction_never_stops_an_own_undo`). With scopes on a cascade stays in the
  retracted memory's scope, so a reopened or flagged memory is read by the same people: only flags in its scope come off
  when it comes back, a move between scopes (048's share) takes off the moving memory's flags and those naming it, each
  audited where its memory is, and a read serves a source only of the memory's own scope
  (`With_scopes_on_a_cascade_keeps_to_one_scope_and_a_move_takes_its_flags_off`,
  `With_scopes_on_a_moved_start_takes_the_author_lock_of_what_it_replaced`, `MemoryTruthToolTests`). Not built: a window
  on `remember` (set with `update` after it); an export carrying flags (an import has none, and none are needed: the
  cascade has already run); a supersede op in the app's Memories view or at the terminal; moving a memory between scopes (a
  share) leaves what it replaced ended rather than reopened, since a replacement is of its own scope.
- **Today** (migration 053, SB-P4-09; story 12): staleness, in the store (`Core/MemoryStaleness.cs`), reached through
  the memory tools `list_stale` and `still_true` only with `memory.tools` on (7.1), and a nightly job made only then
  (section 9). A memory's volatility is the caller's word (D-033; no model or heuristic sets it): a `state` memory may
  be out of date once 90 days pass with no change and no confirmation, a `volatile` one after 14, a `durable` one and
  one with no volatility never by age. Every memory saved without one has none, so none of them goes stale; one saved
  with a volatility goes stale by the time since its last change, as upstream's does. The clock is the newer of its last
  change (`updated_at`, which every change moves; a flag moves nothing) and its last confirmation. The pass marks a
  memory whose time has passed at the revision it has (`memory_checks`), and a mark counts only at that revision, so any
  change takes it out of the queue at once. It locks the memory's row before writing a mark, in the order every writer
  takes them, so a still true at the same moment is waited for and then wins, never a deadlock
  (`A_pass_beside_a_still_true_waits_for_it_and_leaves_the_memory_confirmed`). The stale queue is one predicate, `memory_stale_reason`: a live, current
  memory (not held, not deprecated, not ended, not in the trash) that the pass marked at its revision (`not_confirmed`),
  or that a retracted source flagged (049, `retracted_source`, which wins). A reader's queue is the memories it reads
  (the read predicate) and may change (the author lock, the first row of the table above), and the count and each page
  are read in one statement from that one clause, so the count is the length of the list
  (`The_stale_count_is_the_length_of_the_stale_list_for_every_reader_and_scope`). Still true (`confirm_memory`) is a
  change's step under the same author lock: its checked time moves to now, its mark and its retraction flags come off,
  and one audit row (`confirmed`) names why it was in the queue and the sources whose flags came off; no revision moves
  and no version is kept or pruned, so every version stays and an expected revision others read stays good. 041's
  own-changes rule leaves `confirmed` rows out as it leaves the flag rows out (053 replaces 049's `memory_own_changes`
  in place), so another member's confirmation never stops a member from undoing their own change, and an undo undoes the
  newest change, never a confirmation. A memory held for review, deprecated or ended is refused (`held`, `retracted`,
  `invalid`); one it reads but may not change is refused (`forbidden`), and one in the trash, one the caller does not
  read and an unknown id answer `unknown`; a repeat at the same revision within a day, out of the queue, answers
  unchanged and writes nothing. Since run 17 a person reads the same queue and says still true in the app
  (`app_memories` `stale` and `still_true`, the Memories view's To check list) and at the terminal (`rhythm
  still-true`), through the same `memory_stale_reason` and `confirm_memory`, recorded as `app` or `cli`. Not built:
  upstream's heuristic volatility and its date-passed staleness (a memory naming a date that
  has gone); the stale penalty in recall (TIM-07, upstream's ships off); the 14 and
  90 days as settings (CFG-10).
- **Today** (SB-P4-07; story 6; no migration): what was true at a past moment, through `recall`'s `as_of` with
  `memory.tools` on (7.1; `Core/RecallAsOf.cs`), with every correction made since applied and in the words of that
  time. A memory answers when it is not marked wrong now and its window as it stands now (049: a supersede, an end, a
  moved start, a reopening) covers the moment, from its stated start, else from when it was written; its words are
  those of its state then, of its current state and the earlier ones it keeps (034's 20): the one written by then and
  not yet replaced, or for a memory told as true from before it was written, its first (`recordedAfter`). That state
  must be one the reader is served (not held, D-160; at or above `memory_history_floor`), so a memory held then, or
  from before it came into a scope the reader reads, is left out; so is one whose state then is no longer kept. A
  memory believed then (written by then, its window covering it, its state then served and not deprecated) and marked
  wrong since is listed under the results, never among them, at most three, newest retraction first, with the result
  it had replaced through a `supersedes` made link (which the cascade reopened), found by the arms or through that
  link. Any other record keeps no earlier words, so it answers only when it has not changed since. Every read keeps
  the read predicate and the hold now on the memory's own row: one held now, in the trash or in a scope the reader does
  not read now is in neither list (`RecallAsOfTests`). With hops, a link a person or an AI client made is followed as
  of a moment only when it was made by then (its `created_at`); the map's own links are followed as they stand now,
  and what a hop reaches is judged as of then as any result is (`As_of_a_moment_a_hop_follows_no_made_link_made_after_it`).
  A memory is found by its words now, so a question in words of then that it no longer holds may not find it (as
  upstream). Not built: `as_of` in the app's explorer search or at the terminal.
- **Today** (migration 054, SB-P5-13; story 13): digests, in the store (`Core/MemoryDigests.cs`), reached through the
  memory tools `digest_sources`, `save_digest` and `digest` only with `memory.tools` on (7.1). A digest is of one
  project or one topic tag in one scope, and is written by the AI client: the engine runs no model (D-033). The
  memories it may draw from are one rule, 054's `memory_digest_eligible`, which the list and the write both read: live
  memories of that scope (in the catalog, not in the trash, not held, not deprecated, their window open) in the project
  (or tagged with one of its aliases, as a list counts members) or with the tag; not a digest, not drawn into a live
  digest already (upstream's rolled-up, here a `drawn_from` link and no mark on the source), in no capsule slot, of
  importance under 4 or none, recalled never, or fewer than twice and written over 60 days ago (042's counts kept in
  the digest's own scope alone, since run 16's integration, so one person's recalls never change another reader's list;
  kept only while `engine.recallLog` is on, so without it every memory counts as never recalled; and since 042 files a
  recall under the person its login is bound to whatever `memory.scopes` says, a Family digest, every digest while
  scopes are off, is protected only by recalls of a login with no person, never by a person's AI client's: Q-159's
  default, `A_persons_client_recalls_never_protect_a_Family_memory_from_a_digest`), and not flagged by a
  retracted source (049; stricter than upstream). A memory is drawn into a live digest only by one of its own scope: a
  digest taken into another scope by a share holds none of this scope's. The scope is the one the call names, else the
  caller's own as `remember` lands (with scopes off the household's; a login with no person names Family), only where
  the caller reads: a person's private memories are listed for that person alone, Family's for its readers, and two
  scopes are never pooled. `digest_sources` gives every eligible memory's count, the 50 newest as previews, and a
  basis (the scope, the key and each listed memory's id and revision, hashed), read in one snapshot; the same store
  gives every reader of the scope the same list and basis (`The_same_store_gives_the_same_eligibility_list`), and the
  read writes nothing. A digest is due with at least 10 eligible and no live digest of the key in the scope written in
  the last day. A digest's key is the one its `created` audit row names (054's `memory_digest_keyed`), never its tags or
  project as an update left them; since run 16's fix review `rhythm export` carries that row's key, basis and count on
  the digest's line and `rhythm import` (054's replacement of 043's `import_memory`) writes them on its `imported` row,
  its project mapped as the memory's own is, so an imported digest is still the digest of its key: `digest` finds it,
  its day's wait counts and a save from its basis answers it (`A_digest_comes_in_still_the_digest_of_its_key`); one
  whose project did not come in still holds its sources but names no key. `save_digest` (`save_memory_digest`)
  writes, under the caller's landing rule and one
  lock for the whole scope (since the integration: a project and one of its tags share memories, so two keys' saves
  never draw one memory into two digests), a new memory: a draft, its source `system` (the engine's own label, which no AI client, the app or
  the terminal can give a memory), its author the caller's person, its text the engine's line `[Digest of N memories
  tagged x]` (or `in project slug`) and the client's paragraph, its tag the key's (or its project the key's), screened
  as any write is (036, 045); one audit row, `created`, names the key, the basis and the count; and one `drawn_from`
  made link from it to each listed memory, the caller's, inferred through an AI client's channel. Every source keeps its
  row, text, tags, revision, versions, history and flags exactly (`A_digest_changes_no_source_memory`), and every link
  joins two memories of one scope (`A_held_or_out_of_scope_memory_is_never_eligible_and_a_digests_links_stay_in_its_scope`).
  It writes only while the list still hashes to the basis it was given (else `conflict`), and refuses `not_due`
  within the day or with too few. A save from a basis a live digest of the scope was saved from answers that digest,
  unchanged, whoever saves, and two at once write one, so a repeated or scheduled run never duplicates one
  (`Story_13_digests_never_overwrite_and_repeated_runs_never_duplicate`). Only a live digest holds its sources: one
  marked wrong, ended or in the trash frees them, and a held one keeps them until a person acts on it
  (`Only_a_live_digest_holds_its_sources`); a held digest's day still counts, but `digest_sources` names no `latest` id
  or time for it and the day's refusal names none (D-149). A project's digest names only a project the caller's lists show (046: a
  held project never). `digest` reads the newest live digest of the key the caller reads that is not held (a key that
  selected a held one would tell what it holds, D-149), in one scope or any, with how many memories it was drawn from;
  it writes nothing, no recall follow-up included. A source marked wrong flags the digest for a check (049's cascade
  through `drawn_from`). Otherwise a digest is a memory like any other: a person or a client may edit, confirm, forget or
  undo it under the author lock, and its owner may share it, which moves it and leaves its links (048), so a private
  digest shared into Family links to private memories that only their owner reads. Not built: digests in the app's Memories view, at the terminal or over HTTP
  (API-07); a nightly job (the engine has no model to write one, D-033: a client's own schedule calls the tools); the
  bounds as settings (CFG-07); leaving out contradiction winners, which 068 did not add (a standing instruction is left
  out since 065, SB-P5-12).
- **Today** (migration 058, SB-P5-09; story 12): open loops and two-way commitments, in the store
  (`Core/MemoryLoops.cs`), reached through the memory tools `list_loops` and `resolve_loop`, `remember`'s `owed_to`
  and `owed_by`, and the app's `app_memories` `loops` and `resolve_loop` (7.2) only with `memory.tools` on (7.1). An open loop is one predicate, 058's `memory_open_loop`, which every
  count and list reads: a memory tagged `task` (the caller's own word, as upstream's; a `#task` in its text is one too)
  and not `task:done`, nor an agent's build note (upstream's `build-log`, `claude-response`, `codex-response` and
  `resume-playbook` tags), that is live (in the catalog, not in the trash), not held, not deprecated and whose window
  is open, as the stale queue asks of a memory. It answers the loop's direction: `out` (the person owes it) or `in`
  (owed to the person). A commitment's direction and counterparty are the engine's, not tags (033 reserves upstream's
  `owed-to-me` and `counterparty:` tags): `remember_memory_commitment` writes the memory as `create_memory` does (the
  caller's landing rule, screened for a hold, one audit row), tagged `task` when the caller left it out, and its
  `memory_commitments` row in the same transaction, set once and never versioned, as a memory's source and author are
  not; a memory tagged `task` with no row is a loop the person owes. The counterparty is upstream's slug of the name
  given (lower case, spaces to hyphens, anything but `a-z`, `0-9`, `_` and `-` dropped; none when nothing is left), so
  no free text reaches the row, and a name that reads like instructions to an AI client (036's screening, as 045
  screens a new project's slug) is refused. Its idempotency key covers the commitment too. A reader's queue is the
  memories it reads (the read predicate) that are open loops, of those it may change (the author lock, the first row of
  the table above, as the stale queue's), and of those its own (`memory_loop_mine`): its person's private ones, the
  Family ones its person wrote, and those no person wrote. A Family admin is not served another member's commitments,
  though the author lock lets an admin resolve one (upstream's actionable view). With scopes off the household shares
  everything, so every admitted login is served every loop. The count, the page and both directions' lengths are read
  in one statement from that one clause, so each count is the length of its list for every reader, scope and direction
  (`Each_readers_count_is_the_length_of_its_list_for_every_scope_and_direction`), and a held memory is in none
  (`Story12_due_commitments_have_accurate_counts_and_resolving_each_removes_it_once`). Resolving
  (`resolve_memory_loop`) is a change's step under the same author lock: `done` adds `task:done` to the tags (`task`
  stays, as upstream: it is history); `received`, for a loop owed to the person only, the same; `not_a_task` takes
  `task` off. The state it replaces is kept as a version, the revision moves and one audit row, `resolved`, names the
  action and the direction; 041's undo puts the tags back and the loop is open again, and an AI client's resolves are a
  change group of their own (`resolve`). It writes no words, so no passage moves and no burst counts it (045 counts
  creates, appends and updates). A repeat, or `not_a_task` on a memory with no `task` tag, answers unchanged and writes
  nothing, so a loop leaves its list once (`Resolving_a_loop_writes_history_once_and_undo_opens_it_again`). A memory
  held for review, deprecated or ended is refused (`held`, `retracted`, `invalid`), as still true refuses them; one it
  reads but may not change is refused (`forbidden`), and one in the trash, one the caller does not read and an unknown
  id answer `unknown`. `rhythm export` writes a memory's commitment on its line and `rhythm import` adds it only to the
  memory it has just added, in the same transaction (section 9); since run 17's review a counterparty in the file that
  reads like instructions to an AI client comes in as no name, the direction kept, as `remember` refuses such a name. A
  counterparty's slug drops hyphens and underscores at either end ("— Sam" is `sam`). The app's Memories view lists loops in
  Tasks, and its history says "Its task was resolved". Not built: loops at the terminal; a commitment saved from the app's capture; upstream's refusal of a commitment that is also a standing
  instruction, which comes with those (SB-P5-12; a commitment that is also a decision is refused since SB-P5-11); a
  nightly job (none is needed: the queue is read when it is asked for).
- **Today** (migration 060, SB-P5-11; story 12): the decision ledger, in the store (`Core/MemoryDecisions.cs`), reached
  through `remember`'s `decision`, `confidence`, `confidence_source` and `review_by`, the memory tools
  `list_decisions`, `record_outcome` and `decision_calibration`, and the app's `app_memories` `decisions`, `calibration`
  and `record_outcome` (7.2) only with `memory.tools` on (7.1). A decision is a memory
  `remember_memory_decision` writes as `create_memory` does (the caller's landing rule, screened for a hold, one audit
  row) with its `memory_decisions` row in the same transaction: how sure the person was (upstream's confidence, above 0
  and at most 1, kept to the nearest 0.05 within 0.05 to 0.95, and its source, `stated` or `inferred`), set once and
  never versioned, as a commitment's direction is, so no later change moves what calibration compares; upstream's
  reserved `ledger:decision`, `confidence:` and `confidence-source:` tags (033) are not used. Its idempotency key covers
  the decision as asked (the review date's words, not the date they make). A decision is no commitment: the tools refuse
  both at once, as upstream. What changes is its state (`memory_decision_states`): its outcome (none yet, `right`,
  `wrong`, `mixed` or `unknown`), how often `unknown` asked again (at most twice) and its review date (upstream's
  `review_by`, a date at 09:00 in the install's time zone or a date and time, else 90 calendar days on at 09:00; none
  once nothing is left to ask), one row for each revision at which it was set, so the state at any revision is the
  newest row at or before it and every other change carries it on. Recording an outcome (`record_memory_outcome`) is a
  change's step under the author lock (`memory_for_change`): the state it replaces is kept as a version, the revision
  moves, one audit row, `outcome`, names the result and the outcome before, and the new state is written at the new
  revision; it writes no words (upstream appends a note in the same version; here detail is an `append` of its own, so
  every word a memory takes is screened by 036 and counted by 045's burst rule), so no passage moves. `right`, `wrong`
  and `mixed` end the reviews; `unknown` asks again 90 days on, twice, and a third stops the asking; a repeat of
  `right`, `wrong` or `mixed`, or a fourth `unknown`, answers unchanged and writes nothing, while a second or third
  `unknown` asks again, a change of its own, so recording an outcome is not idempotent. 060 replaces 041's `undo_memory`
  with the same body and one addition: it compares the decision's state at the revision it goes back to with the state
  now, counts a difference as a field it changed (`outcome`) and writes the state it brought back at its own revision,
  so an undo takes an outcome back and an undo of that undo brings it back
  (`Recording_an_outcome_writes_history_once_and_undo_takes_it_back`); an AI client's outcomes are a change group of
  their own (`outcome`). A memory held for review is refused (`held`) and one marked wrong (`retracted`), as
  `resolve_loop` refuses them; one whose window has ended takes an outcome, since calibration scores it; one that is no
  decision is refused (`invalid`). Every list of the ledger is one predicate, 060's `memory_decision_listed`: a decision
  that is live, not held and not deprecated, and of those `open` (no outcome), `due` (its review date has come),
  `resolved` (an outcome, `unknown` included) and `all`, each with its window open, and `scored` (an outcome `right`,
  `wrong` or `mixed` and a confidence, in any window, as upstream's calibration reads replaced decisions too). Stricter
  than upstream, a held decision is in no list and never scored, even one scored before an edit held it
  (`A_held_or_retracted_decision_is_in_no_list_and_never_scored`). A reader's ledger is the decisions it reads, may
  change and owns, the open loops' rule (058's `memory_loop_mine`): a Family admin may record an outcome of another
  member's decision but is never served it, and calibration is the person's own, as upstream's actionable rule; with
  scopes off every admitted login is served every decision. The page and all four lists' lengths are read in one
  statement, so each count is the length of its list for every reader, scope and list, and calibration's `n` is the
  scored list's length (`Each_readers_count_is_the_length_of_its_list_for_every_scope_and_list`), or with `source`
  `stated` or `inferred` the length of that source's part of it
  (`Calibration_rests_on_the_scored_list_with_exact_figures`). Calibration is the engine's own arithmetic over that list
  (`MemoryDecisions.Calibrate`, adapted from upstream's): a confidence below 50% folded onto the other outcome, mixed
  counted half, five bands of folded confidence, a Brier score on the calls as made, and over-, under- or in line by an
  80% Wilson interval; no figure below 10 scored decisions, no band's figures below 5 in it (upstream returns them
  marked unshown; here they are null), at most one topic (a tag that is no kind-of-note tag) with at least 5, and a line
  in words naming how many decisions it rests on (`DecisionCalibrationTests`, exact values on synthetic decisions, empty
  and single-sample cases). `rhythm export` writes a decision's confidence and states on its line and `rhythm import`
  adds them only to the memory it has just added (section 9). Not built: decisions at the terminal; a decision's
  review date from `when` (a decision takes no due date: `when` with it is refused, its words are not read for one,
  `resolve_due` refuses to snooze or clear one (`invalid`) and an import line naming both keeps the decision alone,
  since a memory has one date that can come due, D-214, settled by 069; `review_by` only here); a note with an outcome in the same version; the
  minimums and the 90 days as settings (CFG-15); the Second Brain importer making a decision from the ledger tags it
  keeps beside a memory (D-121).
- **Today** (migration 061, SB-P5-10; story 12): due dates, in the store (`Core/MemoryDue.cs`, `Core/DueDates.cs`),
  reached through the memory tools `list_due` and `resolve_due`, `remember`'s `when` and `when_kind`, and the app's
  `app_memories` `due` and `resolve_due` (7.2) only with `memory.tools` on (7.1). A due date is three columns on the memory (the moment, its kind `due`, `event` or `wake`,
  and its source `explicit` or `text`), part of its state: every retire keeps it in the version it writes (061 replaces
  033's, 048's and 049's) and 041's undo, replaced in 060 and again in 061 (its body there
  keeps 060's decision state too), puts it back. `remember` takes an explicit `when`, an ISO
  date or date and time read on the install's clocks (`install_timezone()`, D-043) or with an offset; a day is due from
  its first moment there (where the clocks skip midnight, the first moment the day has), and a time the clocks skip or
  pass twice is refused rather than moved; its kind is `when_kind`, else `wake`, `due` for a commitment, as upstream.
  Without `when` the memory's words are read for the one date they name (`DueDates.FromText`: an ISO date, a day and a
  month name with or without a year, a numeric date with a four-digit year that reads one way only), taken as `due`;
  two days (also days joined by `and`, `or` or a comma: "23 and 24 November"), a weekday without its date, a relative
  day (`tomorrow`), a date that reads two ways (a numeric date that reads as two days or has a two-digit year, a day
  number on each side of a month name: "5 Dec 10 guests", a number after a date with no year: "1 December '27"), a range
  (a dash, `to`, `through` or `until`: "from 16 to 18 November"), a weekday that disagrees with its date, a day that
  does not exist, has passed or is over five years ahead give none, never guessed, and the answer says why, without the
  words for a memory held for review (D-160). A time of day after a date ("3 December 10:30") is neither its day nor its
  year; `may`, `march` and `mar` in lower case are a month only with an ordinal day or a year ("march 20th"), so "Sam
  will march 20 miles" names no date. The date is written with the memory in one transaction (`remember_memory_due`, at
  revision 1, with a `due_set` audit row), and its idempotency key hashes the request as the caller sent it, so a date
  read from the words hashes as the same request without one
  (`Remember_takes_an_explicit_when_or_one_date_from_the_words_and_says_why_it_took_none`). The due list is
  one predicate, `memory_due`: a due date, not `task:done` (a finished task keeps its date and leaves the list), live,
  not held, not deprecated, its window open; a reader's list is the memories it reads, may change and owns (058's
  `memory_loop_mine`, as its open loops: a Family admin is not served another member's), with three windows (`overdue`,
  `upcoming` within 48 hours, `later`), and its count, page and the windows' lengths are read in one statement, so each
  count is the length of its list (`Each_readers_count_is_the_length_of_its_list_for_every_scope_and_window`); with
  scopes off every admitted login is served every one. `resolve_due` (`resolve_memory_due`) is a change's step under
  the author lock: `snooze` moves the date to a moment to come, at most five years ahead (a memory with none takes one,
  kind `wake`), `clear_date` takes it off; the state it replaces is kept as a version, the revision moves, one audit row
  (`due_changed`) names the action and the dates before and after, 041's undo puts the date back, and an AI client's
  snoozes and clears are a change group of their own (`due`); a repeat writes nothing; a held, deprecated or ended
  memory is refused (`held`, `retracted`, `invalid`), as resolve_loop refuses them, and so is a decision (`invalid`: it
  takes no due date, D-214) (`Snoozing_and_clearing_write_history_once_and_undo_puts_the_date_back`). `get` and
  `list_recent` give a memory's due date, never a held memory's. `rhythm export` writes a memory's due date on its line
  and `rhythm import` puts it only on the memory it has just added (`import_memory_due`), never on a decision. Not
  built: due dates at the terminal; `append`'s and `update`'s `when` (upstream's `append`
  takes one; here a date moves with `resolve_due`); reading relative days ("tomorrow"), weekdays or a date with a time
  of day from the words, or words in another language; upstream's nightly model pass for dates (D-033) and a date taken
  from an imported Second Brain memory's `when_at` (SB-P8-01's importer keeps none); push reminders (SB-P5-16).
- **Today** (migration 063, SB-P5-02; story 9's duplicate case): duplicates on save (`Core/MemoryDuplicates.cs`),
  through the memory tools' `remember` and the app's `capture` only with `memory.tools` on (7.1, 7.2). A new memory whose
  words are close to a memory already in its own scope is saved as any other and flagged for a person's review naming
  that memory, at most three, closest first; nothing is refused, merged or dropped (D-211: no threshold tells a
  duplicate from a contradiction that changes one number or day), and neither memory changes (no revision, version,
  status, window, slot or link moves). The score is the one SB-P2-13 tuned the thresholds on (the live model's vector of
  the words alone, the map's recipe), against `DuplicateFlag` of what `EmbeddingThresholds.Override` reads: with
  `adapters.embeddings.thresholds` off, as on every install, today's 0.85 (Second Brain's starting point, D-028); with
  `tuned`, the live model's own (MiniLM 0.63, run 19). With no model loaded only the same words count (case and spacing aside),
  scored 1. A save is compared with the live memories of its own scope (not held, not marked wrong, not ended, not in
  the trash), the 500 newest and any older one with the same words; each memory's vector is made once per text
  revision and model and kept in the engine's process only, never stored or served (section 8). A held save is not
  checked, and a held memory is never named. The flag is written by `flag_memory_duplicates`, which only the memory's
  writer reaches, at the revision it saved, keeping only copies still live in its scope, with one audit row
  (`duplicate_flagged`, naming them with the model and the threshold); a replayed save names the copies its flags name
  then. A flag stands while both memories are live and in one scope (`memory_duplicate_copies`), so the review ends by
  itself when a person forgets one, marks one wrong, replaces the older (`supersede`) or a hold comes, and returns with
  them; a person keeps both by taking the flag off (`clear_memory_duplicates`, `flags_cleared`: the MCP `update`'s
  `clear` `duplicate_of`, the app's `keep_copy`), under the author lock. A move between scopes (048) takes the moving
  memory's flags off and those naming it (`unflagged`), so a copy in another scope is never named to someone who cannot
  read it. The review is one predicate, `memory_duplicate_listed`, which the list and its count read in one statement:
  the memories a reader reads and may change (`list_duplicates`, the app's `duplicates`), so the count is the length of
  the list (`A_near_copy_and_a_contradiction_are_saved_and_flagged_and_nothing_else_changes`,
  `With_scopes_on_a_copy_in_another_scope_is_never_named`, `A_held_memory_is_never_checked_never_named_and_leaves_the_review`,
  `Story_9_on_the_corpus_flags_every_copy_the_threshold_finds_and_changes_nothing`). `get`, `list_recent` and the app's
  `get` and `list` name a memory's standing copies (`duplicate_of`, `duplicateOf`), never a held one's. Since migration 068
  the check runs on every write that saves a memory's words, through the memory tools and the app (a new memory, an
  `append`, an `update` of its words, an `undo`, a release from review, `MemoryStore.Check`), on `rhythm release`, and on what
  `rhythm import` and `rhythm import-second-brain` have just added (`review_imported_memory`, as the operator; at most 10
  minutes, what it did not reach says so); whoever made the revision may flag it, no longer only its first writer. A later
  check of the same memory rescores what it flags, keeps at most 3 standing, flags one pair one way only, flags no pair
  somebody kept both of (063's `flags_cleared` rows name them), and takes off a flag whose score fell below the threshold,
  either way round (`unflagged`, cause `changed`) (`An_append_an_update_and_an_undo_are_checked_as_a_save_is`,
  `A_held_memory_is_never_named_and_a_release_is_checked`, `An_import_is_checked_with_memory_tools_on_and_not_otherwise`). A
  flag at 0.95 or more, a copy for certain, wins over a link on save: the check runs before the links, such a pair takes no
  automatic link, one already between them comes off with no dismissal kept, and a person's keep both draws the link the words
  allow; below that the flag is only a possible copy, and the flag and the link both stand
  (`A_copy_for_certain_and_a_named_contradiction_win_over_a_link_on_save`). Not built: the check at the terminal's `rhythm remember`, which
  reads no settings, so it saves as before. Since run 21 (SB-P6-01) the Memories view's Copies is the list and its keep both, at the revision shown, through
  `resolve`, a question offered once every memory it is close to is read whole and none held (run 21's fix review), since it may settle a pair, as the next paragraph says; `rhythm duplicates` and `rhythm resolve` are the terminal's; the facade still drops the new fields of a capture's answer.
  Since run 22 (073's `resolve_memory_copy`) the keep both names each memory it is close to at the revision read, the view's `copies`
  and the terminal's `--copy <memory>:<revision>` as `rhythm duplicates <memory>` prints it: while the memory waits there, one the review
  shows that changed since, or that is not named, is refused `conflict` and nothing changes, the list read again. `rhythm resolve`
  refuses a keep both that names none; over the app's channel (an older app) and MCP one that names none checks the flagged memory's
  revision alone, as before (`A_keep_both_holds_only_at_the_revisions_read_of_what_it_is_close_to`; D-233).
- **Today** (migration 068, SB-P5-03; story 9's contradiction case): contradiction candidates, the part of a verdict the engine
  can do (D-033, D-211). A write's check also names the memories of its own scope close enough that they may conflict, at or
  above `Contradiction` of what `EmbeddingThresholds.Override` reads (today's 0.85, the duplicate flag's, since that flag is all
  that named a contradiction before; `tuned`: MiniLM 0.58; never above the flag), at most 5, closest first, each a row of
  `memory_contradictions`, one row a pair. The same words are a copy and no candidate; with no model there are none. A write's
  answer names them with no words (`contradiction_candidates`), and `get`, `list_recent` and the app's `get` and `list` name those
  standing against a memory (`contradictions`): both live and in one scope, never settled, never a held memory's. Neither memory
  changes. The calling AI reads both and says they conflict (`propose_contradiction`, a reason of at most 200 characters, never one
  that reads like instructions); an id it was not offered for that memory is refused (`not_offered`), the same whatever the id names,
  and it settles nothing. The person's review is one predicate, `memory_contradiction_listed`, which the list and its count read in
  one statement: the rows a reader reads and may change on either end (`list_contradictions`, the app's `contradictions`). A pair
  is in one review at a time: a flagged copy nobody has named stays in the duplicate review, and once somebody names it, it leaves
  that for this one. Only a person settles one (`settle_memory_contradiction`, the app's `settle_contradiction`, the app's login or
  a terminal login): `keep_both`, or the one named replaces the other (049's supersede, planned and applied at once, as 063 protects
  it), each at the revisions read; whoever may change either end for keep both, supersede's rule for replace (one that would change
  nothing, because an end still to come rules it out, is refused `protected` and settles nothing); a person's keep both of
  a flagged copy (`clear_memory_duplicates`) settles the pairs that review showed, never a pair named a contradiction, which it does
  not show, and takes off no flag whose copy is in the trash or held. A pair settled is offered no more. Through an AI client's channel, while a
  row stands, `settle` and `supersede` of the pair and anything that would end the earlier of the two by when it was written
  (`set_status` deprecated, `forget`, `update` `valid_until`, `undo` to one of those) are refused (`needs_person`;
  `memory_event_contradiction_guard`, on the change's audit row, so nothing of it is kept), and it may still withdraw the later one,
  which ends the review until that memory is back. A change that finds the earlier one ended already ends nothing and is not refused.
  Once somebody has named a pair, the same holds while the later one is in the trash, held or ended, so withdrawing the later and
  ending the earlier does not decide the pair with no person, and an edit, append or undo that changes the earlier's words is refused
  too, since that could settle it by making the two say the same; it holds until a person settles it or a memory is deleted for good. An
  offer nobody has named holds only while both are live, takes an edit, and comes off with a change of words (Q-161). A person's
  settling holds against an AI client as well (`needs_person`, the same guard): of a pair kept both, neither memory ended while the
  other is live, or replaced; of a replacement, the replaced memory not brought back, nor the one that stands replaced by it. It decides
  which memories stand, not their words for ever, so an edit of a settled memory is an ordinary edit. An AI client's keep both takes the duplicate flag off and leaves the pair queued. A change of the words of
  either memory takes off an offer they no longer reach, unless somebody has named it, which stays until a person settles it or a memory
  goes. A move between scopes takes the unsettled rows of the moving memory off, so a candidate in another scope is never named, and leaves
  a settled row, so a pair a person looked at is not offered again (a proposal answers `not_offered` across scopes, `settled` within one); an
  AI client's move of either memory of a row a person still has to see is refused (`needs_person`), since it would take the pair off their review. A named pair takes
  no automatic link, as a copy for certain does, though a proposal by one who may change neither memory leaves a link already there (`MemoryContradictionTests`, `MemoryContradictionToolTests`, `MemoryContradictionCorpusTests`). Not
  built, since run 21 (SB-P6-01, D-222's person's way): the Memories view's Conflicts, both sides at the revisions read, and `rhythm contradictions` and
  `rhythm resolve` at the terminal, each settling through `MemoryStore.ResolveItem` on a person's channel (`AppMemoryQueueDatabaseTests`). A row's first memory is
  only the one checked, so both name each side by when it began (`startedAt`, 049's `coalesce(valid_from,created_at)`, an unknown start before every other) and offer a
  replacement only by the later, the one direction 049 takes. The earlier stands by a person marking the later wrong or forgetting it, which ends the review until that memory comes back, as
  above; named the other way by hand, the replacement is refused (`protected`) in words a person can follow, not 049's, which speak of a field no person's channel
  sets. Q-161 is the owner's and is left as it stands.
- **Today** (migration 063, SB-P5-04; stories 9 and 13): the protection rules, upstream's for its automatic merge,
  replace and supersede, which here can only be a caller's supersede (049) or a duplicate's flag. Each is the database's,
  so it holds through every channel (`MemoryProtectionTests`): a confirmed memory, and since 063 one of importance 4 or
  5, is replaced only by a confirmed memory (`protected`); a memory saved from a session transcript (source
  `claude-code`, `codex-session` or `cursor-session`, upstream's list, or `gemini-session`, the fourth agent `harness/hooks` knows,
  kept equal to `MemoryInput.TranscriptSources` and to each agent's own source in `harness/hooks/agents.mjs`)
  never replaces a memory of another source, confirmed or not (`protected`); through an AI client's channel a memory in
  a capsule slot is never replaced, since that would change the capsule, a person's step (D-172; `protected`: the person
  takes it out of its slot first). Each is read only for a supersede still to plan or apply, so a retry of one applied
  before answers unchanged. The rest hold by construction, each with a test that tries to break it: no step merges
  memories, and a duplicate's flag changes neither memory, whatever the copy is (confirmed, important, a transcript's, in
  a capsule slot, a digest); a digest draws from no memory of importance 4 or more, none in a slot and none of the
  engine's own, and changes none (054); no job writes a memory through the checked writers, whose role check refuses the
  system's own login and whose `memory_channel` takes no `system` channel (every definer that takes a channel reads it
  there, or hands it to `create_memory`, which does); only the digest writer labels a memory `system` (no AI client, the
  app or the terminal can: `MemoryTools.SourceProblem`, and the app's capture and the terminal take no source); and a
  person's edit marks a system row theirs: `memory_system_row` holds while nobody has changed a digest's words or tags
  since it was written, and `digest` says `edited` once someone has (upstream's `user-edited` tag). Not built: a
  protection on the retraction cascade's "replaced by" link (049), which follows the chain a person or client confirmed
  and is protected transitively except where a memory's status was lowered by hand since; the app's or the terminal's
  supersede (none exists).
- **Today** (migration 064, SB-P5-05; story 11, its candidate half): insight candidates, found by a nightly pass
  (`Core/InsightCandidates.cs`, section 9) and served through the memory tools `list_insight_candidates` and
  `resolve_insight_candidate` only with `memory.tools` on (7.1; `Core/MemoryInsights.cs`). A candidate pairs an older
  and a newer memory of one subject, written 30 or more days apart, both eligible (064's `memory_insight_eligible`:
  live, not held, not deprecated, its window open, not the engine's own); a pair across two scopes is never written,
  and one whose memory later moves to another scope (a share or un-share) is served to nobody, Family's readers
  included, until both its memories are in one scope again: the next pass then finds a waiting one there, keeping its
  id (a decided one never comes back). A dry run counts as kept only what the pass would not write again, but it is a
  sample, not a promise: it reaches at most 10 memories a scope from the pass's place and compares each with the latest 300
  earlier memories, where the pass compares up to 2000, so for a memory with more than 300 written 30 or more days
  before it the pass may keep better pairs than the dry run names (since run 19's fix review; no test goes past 300).
  The pass completes when a scope has no eligible memory after its place (all held, trashed, marked wrong, ended or the
  engine's own, or those after its place gone since): its place is cleared and the scopes after it are walked
  (`A_scope_with_no_eligible_memory_is_passed_over_and_the_scopes_after_it_are_reached`,
  `A_place_whose_later_memories_left_is_cleared_and_the_walk_starts_again`; before the fix review it failed there every
  night). The list is one predicate, `memory_insight_listed` (waiting, both memories still eligible and of its
  subject), over the candidates whose two memories the reader reads (the read predicate on both memories' own rows), so
  with scopes on a person's client is served that person's and Family's and the household view Family's, and the count,
  read with the page, is the length of its list (`Only_eligible_memories_of_one_scope_30_days_apart_pair_and_a_dry_run_writes_nothing`,
  `Story_11_old_and_new_memories_make_a_candidate_a_reasoning_client_can_reject`); with scopes off every admitted
  login is served every one. Using or rejecting one is the candidate's, not a change of either memory: no revision,
  version or audit row of a memory; `memory_insight_events` keeps the decision with the principal, channel and client,
  in the pair's subject. A dry run (`list_insight_candidates` with `dry_run`) walks only the scopes the reader reads
  and writes nothing. Since run 21 (SB-P6-01) a person reads and decides them, each decision asked first since nothing undoes it, in the Memories view's Pairs and at the terminal (`rhythm insight-candidates`,
  `rhythm resolve`), through the same predicate and step. Not built: a candidate pairing a memory with a note or a document; a decision's undo.
- **Today** (migration 066, SB-P5-06; story 11, the stream's half; D-018, D-031): the daily insight stays the one stream
  and the only writer of insights, with no job added: its template (`harness/chatgpt/daily-insight.md`) reads the
  candidates when their tools are listed, records what it declines with why (`resolve_insight_candidate`'s `note`; a
  declined pair never comes back) and ties an insight to its pair (`insight_id`: one the caller reads, citing both
  memories, no wider than the pair, else nothing is decided). `list_insight_stream` serves the insights a reader reads
  in one view each (066's `insight_stream_view`): `proposed`, `older` once nobody has reviewed it for a week after it became one to
  show (still listed and reviewable, no longer counted as waiting, so the count of a daily client's insights stays bounded and a first
  turn of `memory.tools` on counts no history), `confirmed` only by a review, `snoozed` while a snooze
  holds, `closed`; a snooze ends by the clock at the read, writing nothing, while the stored status the app and
  `list_insights` read stays as it was; each view's count is read with its page, and `day` says what today came to for
  this reader (a saved insight, or the one its newest run says it saved; else that run, quiet or blocked, with its note;
  else that none it reads was saved and no run says why), so a day with no insight says so (`Story_11_the_daily_insight_reads_candidates_declines_what_says_nothing_and_proposes_one_in_one_stream`,
  `A_proposed_insight_is_never_shown_as_confirmed_whatever_its_status_or_feedback`,
  `A_snooze_ends_when_its_time_comes_and_a_read_writes_nothing`, `A_day_with_no_insight_says_so`). `review_insight` is the
  only way an insight's review changes; feedback never moves it, an insight replacing a confirmed one is proposed
  itself, and the login that saved one is refused over MCP, so a scheduled run cannot call its own words confirmed
  (`Only_its_owner_its_saver_or_an_admin_reviews_an_insight`). A reopen takes the review back to proposed and answers the
  view that puts it in: one marked not useful or handled stays closed until new feedback. A later insight closes the one it
  replaces by the link, whatever feedback it has since (the app's Earlier insights still take it, and its status moves as
  it always did), and only who could review a confirmed one may replace it, 066's `save_insight` being the one earlier
  function it changes, whose new branch an install that never had `memory.tools` on does not reach: nothing there is confirmed
  (`A_confirmed_insight_is_replaced_only_by_who_may_review_it`, `A_replaced_insight_stays_closed_whatever_feedback_it_has_since`). `insight_events` keeps each review and, with `memory.tools`
  on, each feedback per principal, in the insight's subject, served by `insight_history` to who reads the insight; a
  feedback from before, or written with it off, is carried in with no one named
  (`Feedback_history_is_kept_and_only_with_memory_tools_on`). An insight drawn from a private memory stays in that scope,
  by what its sources name (section 8; `An_insight_written_from_a_pair_cites_it_and_stays_in_its_scope`). With
  `memory.tools` off none of it is reached: every insight answer, the app's feedback and the daily jobs are as they
  were. Since run 21 (SB-P6-01, D-224's person's way) a person reviews them in the Memories view's Insights and at the terminal (`rhythm insights`, `rhythm
  resolve`), so an install whose one client saves every insight has its way to review one. The MCP `review_insight` and `resolve` stay as they are: the
  login that saved it never, another client's login under this section's rule, which the owner may yet close. Not built: a confirmed insight
  becoming a recallable memory; a separate Family-scope team job (none is planned: Family's candidates reach the one
  daily prompt when its token reads Family).
- **Today** (migration 065, SB-P5-12; story 12; D-211): standing instructions, in the store (`Core/MemoryStanding.cs`),
  reached through `remember`'s `standing`, the memory tools `list_standing` and `stop_standing`, and what the `recall`
  tool fires, only with `memory.tools` on (7.1). One is a memory `remember_memory_standing` writes as `create_memory` does
  (the caller's landing rule, screened for a hold, one audit row), marked in the same transaction with an audit row of its
  own (`standing_set`). The mark is a column (`memories.standing`), kept in a version's state and put back by an undo:
  upstream's reserved `standing:active` tag (033) is not used, since an export and an import refuse reserved tags and an
  update would drop one. Its words are at most 500 characters: a constraint on the row, so a save, an update, an append
  and an undo are each refused past it (`too_large`), nothing written. A scope holds at most 50: `remember` counts those
  that can fire under a lock of the scope and refuses the next with nothing saved (`limit`; upstream saves the memory
  unmarked), and a held one takes no place, so a burst of held text cannot fill a scope. A share, an undo of a stop or an
  import can still pass 50, so what fires is read at each question: the oldest 50 of a scope, the rest `over_limit`.
  It is no decision and no commitment (refused, `invalid`, as upstream refuses them together), no task (its tag or a `#task`
  in its words: 058 makes that a loop the person owes) and takes no due date, neither `when` nor one its words name, since
  it fires by topic. The last two are constraints on the row, so `resolve_due`'s snooze, an `update` that adds the tag and
  an import are refused too (`invalid`, nothing written); stopped, it is an ordinary memory that may take either. A question
  fires one when it is live (in the catalog, not in the trash, not held, not stopped, not marked wrong, its window open: 065's
  `memory_standing_rows`, which every list,
  count and firing reads), the caller reads it (the one read predicate on its own row), it has no project or the question
  names its project (`memory_standing_applies`: a project's by id, or a project of its scope claiming one of its tags as an
  alias, as a project's members are; never a project held for review, which claims nothing: a tag naming only one leaves it
  with no project, and one in such a project is listed `project_held`, takes no place and fires for no question), and its
  score reaches the threshold; of those the two closest, then the oldest (`At_most_two_fire_closest_first`, `One_in_a_project_fires_only_inside_that_project`,
  `It_fires_only_from_scopes_the_caller_reads`, `A_held_trashed_stopped_wrong_or_ended_one_never_fires`). The score is the
  one the threshold was tuned on: the question's vector, as the model embeds questions (no instruction, D-068), against
  the instruction's made by the map's recipe from its words alone. The threshold is what `EmbeddingThresholds.Override`
  reads: with `adapters.embeddings.thresholds` off, as on every install, today's 0.67, no looser than any value the
  labelled cases give MiniLM; with `tuned`, MiniLM's 0.59, by precision first
  ([benchmarks](second-brain-parity/benchmarks.md#standing-instructions-sb-p5-12)). With no model nothing fires; as of a
  moment nothing fires (what was true then, not what applies now, as upstream), and with `engine.recall` off nothing calls
  `recall`, so nothing fires and `list_standing` and `remember` say so. What fired names its own id and revision, and the
  receipt stays over the results, the one the recall log keeps. An AI client's stays a draft and fires as
  one, its `status` saying so (as upstream's fires unconfirmed): the person asked for it, and the answer tells it as their own note
  to apply only if it fits, stored data and never an instruction; a held one never fires and its words reach no answer.
  Reading writes nothing. A stop is whoever may change the memory's (033's author lock, as `resolve_loop`): a version
  kept, the revision moved, one audit row (`standing_stopped`), `undo` starts it again, a repeat answers unchanged, and it
  stays a memory; a memory that never was one is refused (`invalid`), a held one (`held`) and one marked wrong
  (`retracted`), as `resolve_due` refuses them. It is no digest's source (054 said this item must leave them out). `rhythm
  export` writes `standing` on its line and `rhythm import` marks only the memory it has just added (section 9). Not built:
  standing instructions at the terminal (since run 22 the app's Memories view lists them and a person stops one, `app_memories`
  `standing` and `stop_standing`, 7.2); the Second Brain importer making one from the `standing:active` tag
  it keeps as data (D-121); upstream's 24-hour cache of ids and vectors (each question reads the rows, and a vector is made
  once per text revision and model in this process); the 0.67 and 50 as settings (CFG-15). `resolve` has its `stop_standing`
  since migration 071, below.
- **Today** (migration 069, SB-P5-08; story 12; D-214): the brief (`Core/MemoryBrief.cs`), what needs the person now, reached
  through the memory tool `brief` and the app's `app_memories` op `brief`, only with `memory.tools` on. It
  defines nothing and writes nothing: each section is the head of a list another tool walks, read by that list's own statement,
  so its count is that list's length and what the list leaves out it leaves out. Due: `list_due`'s overdue and upcoming
  windows, at most 5. Decisions: `list_decisions`' `due` and `upcoming`, at most 3. You owe and owed to you: `list_loops`
  out and in, at most 5 each. Stale: `list_stale`, at most 2. Insights: `list_insight_stream`'s proposed view, at most 1.
  Contradictions and duplicates: `list_contradictions` and `list_duplicates`, at most 3 each. Held: a number with no words, the review
  queue's own count (`ReviewCount`), since only a person releases a hold, so a line that says nothing needs attention is true.
  Standing: with a project named,
  the instructions that fire inside it and nowhere else and may fire now (`list_standing`'s `project`, 065's
  `memory_standing_applies` both ways), at most 3; none with no project, and a project narrows no other section, as `recall`'s
  narrows nothing but what fires (upstream's narrows every section: a list would then need it too). Calibration: its line once
  ready, and `n`, the scored list's length. Changes: the groups of an AI client's changes in the last 48 hours with the key
  `undo` takes, the newest 5 with the count of all among the newest 200 changes (`truncated` says more were made, so each count
  is at least what it says, and `says` adds "or more"), none of a memory held now or in the trash (`Changes`'
  `notHeld`), of the `workspace` when one is named. A change left out ends its group, so a group's key undoes what the group
  counts and none of what the answer left out (`A_group_in_a_narrowed_brief_undoes_what_it_counts_and_nothing_it_left_out`).
  So no list section counts or shows a held or trashed memory (`A_held_or_trashed_memory_is_counted_in_no_section`), each count is its list's
  length for every reader and scope (`Each_sections_count_is_the_length_of_its_list_for_every_reader_and_scope`), a reader is
  served only what its lists serve it, and resolving an item removes it once
  (`Story_12_a_due_commitment_a_stale_fact_and_a_pending_insight_are_in_one_brief_and_each_leaves_it_once`). One due definition
  (D-214, settled here): a memory has one date that can come due, a decision's its review date and any other memory's its due
  date. A decision still takes no due date, and since 069 `memory_due` leaves a decision out whatever a version kept by hand holds,
  so it is counted once. One rule reads either date, 061's `memory_due_window`: overdue (its moment has come), upcoming (within
  48 hours), later. A decision's list `due` is that rule's overdue, as it always read, and its new list `upcoming` that rule's
  upcoming (`A_due_date_and_a_review_date_fall_in_the_same_window_at_the_same_moment`). Home draws no brief (the owner,
  D-257, 9 November 2026: a summary card above the sky goes against the ambient vision); run 20's Home card is gone, and the
  app no longer asks the op. Not built: a project
  narrowing every section; `lean`, upstream's shorter brief for its session hooks; paging past
  the newest 5 groups of AI changes. The app's own lists for the brief's sections are SB-P6-01's: the Memories view's sections, each
  the list a count is the length of (due, decisions, you owe, owed to you, may be out of date, held, AI changes), with
  contradictions, copies and insights to join with the review queues' screens; `AppMemoryPlansDatabaseTests` walks each list against the card.
- **Today** (migration 070, SB-P5-17): a resurfaced old memory on Home (`Core/MemoryResurface.cs`), reached only through the app's
  `app_memories` ops `resurface` and `resurface_dismiss` (its Home card), only with `memory.tools` on, and offered to no AI client.
  The pick is a read and writes nothing: of what the reader reads (the one predicate on each memory's own row) that is eligible and
  not dismissed, the one whose md5 of the install's day and its id comes first, so a reload shows the same one all day on any
  device (`A_reload_shows_the_same_one_all_day_and_the_day_moves_it`). Taking another memory away moves it only if it was the
  pick, and a new one takes it only by coming first. Eligible (070's `memory_resurface_eligible`): older than 60 days at the day's
  first moment in the install's zone, so a memory turns old at a day's start; current in the catalog, out of the trash, not held,
  not marked wrong, its window open and not built on something marked wrong; none of the engine's own (source `system`), no mirror,
  no capture until a person confirms it (the card names no status or source to say what it is), and, after
  a Second Brain import, none of what upstream's own jobs wrote (the tags `auto-pattern`, `auto-insight` and `synthesized`, which
  upstream's filter leaves out and the import keeps in `memory_upstream_tags`); not an event (kind `episodic`), a finished task
  (`task:done`), a capsule slot or a standing instruction; rated 3 or above, or not rated, which takes in a memory upstream never
  classified, whose 0 an import brings as none, where upstream's filter leaves it out
  (`Only_an_old_memory_worth_reading_again_is_picked`). A dismissal is a person's, through the app's channel or the terminal's and
  never an AI client's (`Only_a_person_dismisses_and_only_what_they_read`), of a memory they read, in the trash or held too: the
  card may have drawn it before it went, and a restore or a release does not bring it back
  (`A_pick_trashed_or_held_since_the_card_drew_it_stays_dismissed`). A memory they no longer read, or one deleted for good, is
  `unknown` and nothing is kept, so one taken out of their reach and shared back later can come again. A dismissal is one row of
  `memory_resurface_dismissals` (a checked table) for the memory and the reader, the person the login
  speaks for or the household for the shared app token. It is no change of the memory: no revision, version or audit row
  (`dismissed_by` keeps who), so `undo` does not take it back, and a change, the trash and back, or a share do not end it
  (`A_dismissed_pick_never_returns`). With scopes on a memory is retired for its reader by that person's own dismissal or the
  household's, so Alex dismissing a Family memory leaves it for Sam; with scopes off everyone reads as one household, so by anyone.
  It goes when the memory is deleted for good and is not exported, as the map's dismissals. The Home card
  (`app/src/ResurfaceCard.tsx`) is a slim line at the head of the sky's column in Home's own flow, its words opening in
  place; it asks only while the engine's listing names `app_memories`, and with it off, in the demo, while a read fails or with none eligible it draws nothing, so
  Home is as it was. On an upright phone under 700 pixels tall it gives way, so the sky keeps its rows. Not built: upstream's
  preference for what the brain has been about lately and its list of what was shown lately, so a memory can come again within
  weeks, as chance has it; opening the memory or adding to it from the card; taking a dismissal back.
- **Today** (migration 071, SB-P5-15; story 12): one resolve for every review queue (`Core/MemoryResolve.cs`), the memory tool
  `resolve`, only with `memory.tools` on. Each action is its queue's own step, under that step's author lock, with its audit row
  and its words: `still_true` (stale, `confirm_memory`); `done`, `received`, `not_a_task` (loops, `resolve_memory_loop`); `outcome`
  with a result (decisions, `record_memory_outcome`); `snooze` with `until` and `clear_date` (due, `resolve_memory_due`);
  `stop_standing` (`stop_memory_standing`); `keep_copy` (duplicates, `clear_memory_duplicates`); `use_candidate`, `reject_candidate`
  (candidates, 066's `resolve_insight_candidate`); `confirm_insight`, `dismiss_insight` (the stream, `review_insight`). What it adds
  is once. A step alone answers unchanged only to the same word: a done loop can still be called not a task, a decision right can
  still be called wrong, another unknown asks again, a still true a day later moves the clock again, a snooze to another date moves
  it. Those are a person correcting something, so the steps and their tools stay. Through `resolve` an item leaves its queue once:
  a second resolve, in that word or another, however close together, finds it no longer waiting (071's `memory_resolve_waiting`,
  each queue's own predicate, read under the step's lock), writes nothing and says so, and each count drops by exactly one
  (`Two_resolves_at_once_take_an_item_off_once`, and each queue's test). Two things differ. A memory with a fourth flag takes a
  second `keep_copy`, since 068 shows three at a time and the next stands once those come off. A snooze into the 48 hours is
  below. An item not in the queue answers unchanged the same way, and that includes a standing instruction whose time has ended,
  which is on no list and fires for nobody (065's `memory_standing_rows`); `stop_standing` still takes off its mark. An undo, a
  reopen or the nightly pass's next mark puts an item back, and then it waits again. What it refuses it refuses as the steps do
  and before saying anything else: an unknown id alike whatever it names, one the caller may not change (`forbidden`), a stale
  expected revision (`conflict`), a held memory (`held`), one marked wrong (`retracted`). A second resolve by someone who may not act on the item is refused too, never told
  unchanged. Two actions can leave an item in the brief's section, and the answer says so (`still_counted`): a `keep_copy` with a
  further flag standing, and a snooze to within 48 hours. Such a snooze of an overdue date moves it to upcoming, so the section's
  overdue drops by one and its total does not. A snooze of a date past the 48 hours into them brings in what the brief never
  counted, so its total and upcoming each rise by one: the one resolve that raises a count, answered `newly_counted`, not
  `still_counted`, and said in its message. A second snooze does nothing while a snooze is in force (071's
  `memory_snooze_in_force`), and a clear still ends it, the count dropping once.
  `resolve_due` still moves a snoozed date. A memory in two queues leaves each as its own step says: done closes a loop's date too,
  and not a task leaves its date on the due list.

  **A person's steps.** `release` (a held memory) and `settle_keep_both`, `settle_replace` (a contradiction) are answered
  `needs_person` over MCP before anything is read, alike whatever the id names, so the call says nothing about it. Through
  `MemoryStore.ResolveItem` an AI client's login naming the app's or the terminal's channel is refused by `memory_channel` (044), as any
  other word is; only the app's login and a person's terminal login reach them, each once, through the app's `resolve` op and `rhythm resolve` (SB-P6-01,
  `No_AI_clients_token_reaches_a_settling_a_release_or_a_review_of_its_own`). No other action reaches them: every action on a held memory is refused `held`, and an AI client's `keep_copy` takes
  the flags off and leaves a pair also queued as a possible contradiction unsettled, for the person (068). A login never reviews an
  insight it saved. `MemoryResolvePersonTests`. Undoable: a change of the memory by `undo`, which is every memory action but
  `still_true` and `keep_copy`, which move no revision and write only an audit row; a review by `review_insight`'s `reopen`; a candidate's decision
  stays as it was made. A person's review queues take nine of its actions (`MemoryResolve.ReviewActions`); how many wait in each is `MemoryStore.QueueCounts`,
  each count read through its own list's read (`Each_queues_count_is_the_length_of_its_list`). Not built: its loops, decisions and dates
  from the app (each has its own op there, D-234). Since run 22 the app takes upstream's bulk resolve for the one-word steps
  (`MemoryResolve.BulkActions`: `still_true`, `reject_candidate`, `confirm_insight`, `dismiss_insight`) through `app_memories` `resolve_all`
  (`MemoryStore.ResolveAll`): each item through this one door, answered on its own, a person's step refused through an AI client's channel
  before anything is read (`A_person_resolves_several_at_once_each_answered_on_its_own`, `No_AI_clients_token_reaches_the_new_steps`);
  the MCP tool still takes one item a call, and standing instructions are stopped in the app with `stop_standing`, 065's writer.
- With scopes **off**, every admitted harness and app principal may do what the last column allows, which is today's
  model, and a native memory is a household memory (D-048). Its author is still recorded when the principal that wrote
  it has a person, so the middle column applies to it once scopes are on.
- **Upstream's gaps are not copied** (ledger SEC-05, D-031). Upstream lets any member `link` and `unlink` shared
  memories whatever the link's provenance, edit or delete shared projects, and confirm or dismiss insights; its admins
  read rows with no workspace; and a read made without an identity is not scoped at all. In Rhythm every row has a
  subject, each action above has an author test (SB-P1-10), and a call without a principal reads nothing.
- A link a person or an AI makes joins two records in one scope. Links the engine finds (an exact reference, a project,
  a similarity) may join one person's private record and a Family record, as the bridging guard allows today
  (`RelationshipDiscovery.Bridges` in `Core/RelationshipDiscovery.cs`), and are served only to a reader of both ends.
  Two people's private records never link.

**Where writes land**, with scopes on:

| Write | Lands in |
|---|---|
| a native memory (`remember`, the app's capture) | the caller's private scope unless the call names Family (D-021). A principal with no person has no private scope: it writes one only into Family, and only when the call names it, and such a memory has no person as its author (D-073). With scopes off, a private one is refused and a Family one is a household memory (D-048). **Today** (migration 033, SB-P3-01) `create_memory` decides it in the database under the mode's row locked `FOR SHARE` (`memory_landing`, which `MemorySettings.Lands` mirrors, `Landing_in_the_database_matches_MemorySettings_Lands`), and also refuses a writer that would land where it does not read: a person outside Family naming Family, a suspended or former person's login. No tool calls it yet |
| `save_document` | the caller's private scope when `subject` is left out and the caller has a person (with scopes off, the household's, as before); `household` only when named; never another person. A copy whose original the engine can identify (the same file id or address as a record it holds) lands no wider than that original. Where a copy lands never decides who reads it: section 8 serves it to its original's current readers, so a copy saved before switch-on (every one today is Family) narrows with its original. **Today** (migration 031, added by the run 7 fix review, after phase 1's exit change): with scopes on a copy lands only for the caller's own person or, for a caller that reads Family, the household; any other subject is refused (`42501`) before anything there is read, so the refusal never says whether that person holds a copy with that file id or how recent it is, and a subject left out is the caller's own person, or the household's for a login with no person (`Save_document_lands_only_for_the_callers_own_person_or_Family`). Not built: landing a copy of a known original no wider than it; the read rule of section 8 already serves every copy to its original's current readers alone. With scopes off any harness login saves for anyone, as before |
| an insight, dashboard, pin or corner card | the narrowest scope of what it cites (SB-P1-06). **Today** (migration 027, `derived_subject`) with scopes on: a star counts as the subject whose readers alone it is served to (a copy's star its narrower `Reader`), and a person's star that a source share lets Family read, the household's or a DEMO one narrows nothing; two people's private records are refused, and so is a star its writer can't read; a dashboard's next version keeps or narrows its subject, never widens it, and narrows a Family dashboard only when its saver is the person who made it; an insight is no wider than one it supersedes, narrows a Family one it supersedes only when its saver is that insight's author's person, and one citing no star lands in its writer's own scope (the household's for no person), as does a run of no insight (a run of an insight takes the insight's); an insight's sources count too (`InsightStore.Save`): a source not on the web counts as the star its locator names, or the record it names (a `save_document` copy as the saver's own scope), when the saver reads it, and as the saver's own scope otherwise. What the sources named is kept (`insights.sources_checked`: the stars and records the saver read) and checked again at every read, as the stars are (section 8), so an insight narrows when a record or star its sources name does; a pin takes its star's subject; a corner card is its chooser's own (migration 030, SB-P1-20): a viewer session's is its person's, showing a Family dashboard or one of theirs, or the stat card, and the household view's is the household's, showing a Family dashboard only; each reader is shown its own person's choice where that person made one, else the household's, and nobody else is told it. Since 032 a session drops its person's choice in a corner again (the nil uuid as the dashboard), so the corner follows the household's, and a reader bound to a person is told which corners are its person's own (`own`), which a screen's Undo of `corner_card` needs. With scopes off every one is the household's, as every row 027 found is, and a person's corner is not read |
| a dashboard's log entry | the subject the write names, which must be the caller's person or the household, and no wider than its dashboard. A log write or feed that names no subject means the caller's person, or the household for the household view; with scopes off it means the first person in `people`, as before (`DashboardFeed.Normalized`, `DashboardWrite.Normalized`, `DerivedScopes.LogSubject`). **Today** with scopes on (027): a saved version's log feeds and writes name the saver's person or the household, a private dashboard's log writes its owner's, and an entry is written only by a writer whose own person or household it is (a Family dashboard saved before the switch whose log names Alex is read by Alex's readers alone, section 8, and takes entries from Alex alone), and never a household entry on a private dashboard, from an older version whose edits are still allowed included |
| an upload in the app | the documents adapter's `owner`, as today, from the household view; a viewer session's private scope unless it chooses Family (`app_upload_document` `family: true`). **Built** (SB-P1-20, `ViewerSessions.Lands`): only while `app.viewers` is on is `family` listed and taken and the answer's `document.scope` given, which says who reads it: `private`, one person's readers, or `shared`, everyone in the household (Family's, and every upload with scopes off). A Family choice lands in the household's; with scopes on it is refused (`403`, as `derived_subject` refuses a record citing what its writer does not read) to a login that does not read Family. A session's own upload with scopes on is keyed apart (`upload:own:`) from the uploads the `owner` setting puts in the same person's scope from the household view, which does not read them, so its answer never says whether that person holds the same file and never renames theirs. With scopes off a session's upload lands at the `owner` as every upload does (its person only records who saved it, 6.1). The same file added for oneself and to Family is two documents |
| an adapter's or a plugin's rows | wherever its settings put them (section 10.2) |
| a memory, project or link `rhythm import` brings | the subject it had where it was exported, only when the import names that scope and its reader may write the record there (section 9, Imports); never another person's private scope, never a private one with scopes off |

## 6. The `memory.scopes` switch

### 6.1 Values

`memory.scopes` in `rhythm.yaml` (SB-P1-09) is `off` or `on`; absent means `off`. The example `rhythm.yaml` and
`rhythm init` say `off`, also since the scopes mode passed phase 1's exit: switching a real household on is the owner's
decision (D-013, Q-152). `memory`
takes three other keys, `memory.capsules` (SB-P3-08), `memory.tools` (SB-P3-09) and `memory.share` (SB-P3-10): each
`off` or `on`, absent or empty meaning `off`. `memory.share: on`, with `memory.tools: on` too, makes and lists the
`share` tool (7.1), which refuses every call while `memory.scopes` is off; it also gives the app's memory tool its
`share` op (7.2; listed only while memory scopes are on, and only to an app login that speaks for a person) and the
terminal `rhythm share-memory` (since run 14); off, absent or without `memory.tools`, none of them exists, and every
answer is as before. `memory.capsules: on` lists `get_prompt_capsule` and the `rhythm://capsules/...` resources to AI clients
(what they serve: 7.1); `memory.tools: on` makes and lists the memory tools (`remember`, `append`, `update`, `get`,
`list_recent`, `set_status`, `forget`, `undo`, `history`, `list_projects`, `list_teams`, `supersede`, `list_stale`,
`still_true`, `digest_sources`, `save_digest`, `digest`, `cite`, `revalidate`, `list_loops`, `resolve_loop`,
`list_decisions`, `record_outcome`, `decision_calibration`, `list_due`, `resolve_due`, `list_insight_candidates`,
`resolve_insight_candidate`, `list_insight_stream`, `review_insight`, `insight_history`, `list_standing`, `stop_standing`, `brief`,
`resolve`;
7.1, and `remember`'s `owed_to` and `owed_by`, its `decision`, `confidence`, `confidence_source` and `review_by`, its
`when` and `when_kind` and its `standing`, `recall`'s `project` and the instructions it fires, and
the nightly staleness, link pruning and insight candidate passes, section 9), which with it off do not
exist at all, so a call naming one is answered as an unknown tool, as before; and it lists `app_memories` to the app,
for its Memories view (7.2, SB-P3-12), which with it off the app's role answers as a tool it does not have, and the
terminal's person's steps (`rhythm release`, `rhythm capsule`, `rhythm delete-forever`, since run 21 `rhythm queues`, `rhythm
duplicates`, `rhythm contradictions`, `rhythm insight-candidates`, `rhythm insights` and `rhythm resolve`, and since run 14 `rhythm
forget`, `rhythm set-status` and `rhythm capsule` with no memory, the capsule view, and since run 17 `rhythm link`,
`rhythm unlink`, `rhythm still-true`, `rhythm clear-flags` and `rhythm undismiss`), which with it off answer as
commands the terminal does not know, and help lists none of them. Any other
value of any of them, or any other key under `memory`, fails at start naming it. **Today** `MemorySettings` reads every key when the settings load
(`RhythmConfig.Validate`), so `rhythm check-config` and `rhythm doctor` say the same as the start, and the settings
schema (`contracts/rhythm.schema.json`) takes the same keys, `secondBrainImport` and `capture` among them.

The other defaults of D-021 are rules rather than settings, kept in `MemorySettings` for the write paths of phase 3 (the
store's checked writers take them since migration 033: a new memory is a draft whatever the call says, and confirming it
is a `set_memory_status` of its own): a memory lands private by default when scopes are on (`MemorySettings.Lands`: a
call that names no scope lands in the writer's private scope, and a writer with no person writes only into Family, only
when the call names it; with scopes off every memory is a household one and a private one is refused, D-048); what an AI
client saves is a draft (`MemorySettings.NewStatus`, the column default of migration 025) or held for review, never
canonical on its own (since migration 036, SB-P3-18, the database screens every memory it stores and holds one that
reads like instructions to an AI client, holds hidden characters or is over 32 KiB; only a person releases it, from the
app or the terminal, never an AI client, `MemoryHolds`; the app's Memories view does, with `memory.tools` on (SB-P3-12),
and so does the terminal's `rhythm release` with the same switch (SB-P3-22), so with `memory.tools` off a held memory stays held; a release lasts for the text and tags it covered, and only for them: a later change holds the
memory again for what it adds, an instance of a signal its text and tags did not have as many of (counted over the whole
text, its unscanned middle included, whatever families the released text had: `memory_signal_matches`) or text newly
over 32 KiB, so an append or a change of tags that adds no signal never holds again what a person released, and an
instruction a change adds is always held, D-161); and no capture lane is on: the one built, the browser's (SB-P7-03), is
not an adapter but the app's own page, there only with `memory.tools` on (with it off, as in the demo or with the engine
out of reach, an address carrying a page opens the app as before, kept up to date as any tab: `Start.tsx`), and it saves only on a person's Save, through
`app_memories` as the app, a draft in Family tagged `reading` whose words are scored as an AI client's through the
app's shared login (Q-158); each other lane phase 7 adds is an adapter, off until its person turns it on (D-016).

| | `off` (the default) | `on` |
|---|---|---|
| reads | today's: every enabled system, harness and app principal reads every subject (`Database.Allowed`) | section 2, through one predicate on every path (section 7) |
| native memories | household memories, visible to all; each reply that saves one says so; private capture and imports of personal memories refused (D-048) | private by default; Family when the call says so |
| person bindings | recorded, used for authorship only | decide what each principal reads |
| changes | roles only, as today | section 5 |

### 6.2 No accidental widening

Turning scopes on only narrows: nobody reads anything with them on that they could not read with them off. These are
the ways an install could still widen by accident, and what stops each.

| Case | Rule |
|---|---|
| `rhythm.yaml` lost, or replaced by an older copy without the key (it is not in a backup, [../backups.md](../backups.md)) | the engine records the mode it runs with in the database (`install_settings`, key `memory.scopes`). Starting `off` against a database that records `on` fails at start and names the setting, until `rhythm.yaml` says `on` again or the operator turns scopes off on purpose (below) |
| a backup restored | the restored database carries its recorded mode, so the same check applies; the quarantine still disables every token (`engine/backup/restore-quarantine.sql`) |
| turning scopes off on purpose | only on the host, by a command that says what becomes visible to everyone (counts per person, no titles) and asks to confirm; refused while any native memory is private, or any insight, dashboard or copy cites a private one. Adapter rows then go back to the household model they had before. **Today** `rhythm scopes off` (SB-P1-18): refused unless `rhythm.yaml` loads and says off (else the next start would switch them on again), and while any memory is private (trashed ones included), any memory now in Family has a private earlier version or a private earlier change on its audit trail (so the 20-state retention of migration 034 never lets the switch through for a memory once private: its audit rows are never pruned), or any memory project is private; it refuses unless `.env` starts the engine from `./rhythm.yaml`, the file it checks. Derived and native records of a person's scope (insights, their runs and, since 066, their feedback and reviews with their notes, dashboards and the writes made from them, pins, `save_document` copies) are not refused: they go back to the household's model with the adapter rows they cite, and are counted. They can hold text about a private memory that no longer exists (an insight keeps its stars' and sources' titles and the AI's text, a copy its full text, a write its before and after values), which everyone then reads: the preview's counts are what the operator weighs. It then prints each person's current records by provider (marking those a source share lets Family read already), their dashboards, the writes made from dashboards, insights, insight runs, insight feedback and reviews and pins of their own scope, and Family's record count with the current people not in Family, counts only, and records off only when the operator types `off`, in the transaction that checks again with the mode's row locked. That check holds only because every checked memory writer (phase 3, SB-P3-01) locks the mode's row `FOR SHARE` in its own transaction before it writes a private memory, version or project or un-shares into a private scope; a plain read of the mode would not wait. **Today** every writer of migration 033 does, before any write, and reads the mode from that locked row (`lock_memory_scopes`): a writer waits for the switch, and the switch for a writer, whose private memory it then finds (`MemoryScopesOffLockTests`). The un-share (SB-P3-10, migration 048) takes the same lock first; and a memory once shared keeps the switch refused, its private stretch being on its audit trail. Memberships, admin roles and source shares are kept, and a later switch-on adds nobody to Family (6.3). A database that records no mode is put back the same way |
| a token with no person | the household view: Family and DEMO only, never more |
| a new person in `people` | not in Family until the operator adds them (section 4) |
| a person's own data the household used to see (glucose on a shared screen) | private to that person until a source share says otherwise (section 4); the preview (6.3) lists it |
| an adapter whose `owner` is `household` | its rows stay Family, as before: not widening, but not private either. `rhythm doctor` names each adapter that writes private-looking rows under the household (the Second Brain personal workspace with `owner: household`) |
| a record that leaves Family (an un-share in Rhythm, or a move in Second Brain) | every copy of it stops reaching readers who lost it at their next read (section 8) |
| an AI client's copy of a record (`save_document`), saved before or after switching on | served only to readers of its original's current subject when the engine can tell what it copies, checked at read time (section 8). The switch therefore narrows every copy saved before it (each is Family today: copies of Second Brain memories and Drive files that turn private under `adapters.*.owner`), and a copy narrows again when its original later does. Where it was written (section 5) only keeps a new copy from landing wider |
| counts, neighbours, errors, ranking statistics (document frequencies and rarity weights, candidate windows) and result budgets | computed over the reader's rows, or per scope, never across scopes the reader can't read; an unreadable record answers as an unknown one (section 8). Today ranked keyword search already weighs rarity among the candidates the reader's predicate admits (`LexicalSearch`, the predicate inside each candidate tier); a fused or global rarity statistic (SB-P2-08) must keep to that, or Sam could learn the rare words of Alex's private memories from the order of his own results. The model status's index counts (`app_status`, `/health`) count every scope together, so with scopes on they are left out for every caller and only the operator reads them (`rhythm doctor`, from the database; D-103) |

### 6.3 Switching on

1. The operator binds each AI client's token to its person (revoke, then `rhythm token create <name> --person <id>`),
   or leaves it with the household view.
2. A preview (SB-P1-09: `rhythm doctor`, or a dry run of the switch) lists, per principal, what it will stop seeing:
   counts per person and provider, never titles, including the household's `save_document` copies of originals that
   become private (they narrow with them, section 8). The operator records source shares where the household should
   keep seeing one person's data, and names at least one admin. **Today** `rhythm doctor` prints the preview while
   scopes are off: it applies the next start's switch below inside a transaction it rolls back (adding everyone in
   `people` to Family only before the first switch-on), and counts each enabled harness and app token's readable
   catalog rows through `Database.Visible` before and after. Derived records (insights, dashboards, pins, corners) are
   not counted: which stars a token reads is the map's to say, and each narrows at read time with the stars, records
   and logs it draws on (section 8), those saved before the switch included. The one narrowing it can see without the
   map is a Family dashboard's log: it lists, by id and title (Family's own), each Family dashboard whose newest version
   reads or writes a log that names no subject or names a person, which from the switch on only that person's readers
   read (no one, for a log that names none), so the operator can have it saved again naming the household. Viewer
   sessions refused at the door (past 30 days, or any while `app.viewers` is off) are left out of the counts and
   pointed out. `rhythm share add
   <person> <provider>` records a source share and `rhythm family admin <id>` names an admin (SB-P1-18, section 4).
3. The operator sets `memory.scopes: on` and runs `rhythm up`. The first start records `on`, adds every person in
   `people` to Family and logs that it did. **Today** `Principals.Install` does this, suspended people included (they
   read nothing while suspended) and a person the operator offboarded left out (SB-P7-13: their offboarding ended their
   membership, brought back or not, and only `rhythm family add` gives it back), and the scheduler logs the count; it is reached only once the switch is accepted
   (6.4), as it is since phase 1's exit (D-131). It records that it did (`install_settings` key
   `memory.scopes.joined`), so switching on again after `rhythm scopes off` adds nobody: Family stays as the operator
   left it. The host commands that add someone to Family, give the admin role or record a share lock the mode's row
   `FOR SHARE` first, and a start locks it `FOR UPDATE` first, so the two never interleave (a start that drops a
   person ends what such a command recorded for them).

Nothing in these steps shares, binds or adds anyone automatically. For a real household, when and whether to do this is
the owner's decision (D-013, Q-152); the program proves it on `alex` and `sam` only.

### 6.4 What fails at start, and what is only reported

- **Fails:** a value other than `off` or `on`; `on` without the scope tables (SB-P1-02); `off` against a database that
  records `on`. And `on` itself, until every read path in section 7 filters and the authorisation tests pass (SB-P1-10,
  phase 1's exit): the engine never accepts the switch while one path would still leak. **Today** (SB-P1-09) each
  holds: the settings load refuses a wrong value (`MemorySettings.From`), and `Principals.Install` refuses a database
  without the scope tables, one that records no mode and one that records `on` while `rhythm.yaml` says `off`
  (`MemorySettingsDatabaseTests`). `on` itself was refused too until phase 1's exit change made
  `MemorySettings.ScopesAccepted` true (1 November 2026, D-131): every read path in section 7 filters, the
  authorisation tests pass with scopes on (SB-P1-10, its write-side cases carried to phase 3 with the write functions
  they test, D-130), and `cli/e2e.sh` has run end to end. Since then the settings load and the switch's first start
  accept `on`; off stays the default, and nothing answers differently with it. One write path was missed then:
  `save_document` still took any subject with scopes on, so one person's AI client could write a copy into another
  person's private scope. Its landing rule (section 5) was added by the run 7 fix review (migration 031, D-131's
  amendment); no install had turned scopes on meanwhile.
- **Reported by `rhythm doctor`:** a token with no person while scopes are on; Family without an admin; an adapter that
  writes private rows under the household; each source share; a token bound to a former person. **Today** each,
  whenever the database can be reached (the engine may be down: a mode mismatch is what stops it starting; the adapter
  from the settings alone), plus the recorded mode against `rhythm.yaml`'s, which fails, and the preview of 6.3 while
  scopes are off; a token bound to a suspended person is reported like a former one (the engine itself disables both
  kinds, so doctor finds one only after a hand edit or an older engine).

## 7. What each read path filters

One predicate is meant to cover every read: `Database.Visible` (status current, not expired, and the copy rule of
section 8) with `Database.Allowed`, in `Core/Database.cs`. Both match a row against `readable_subjects(principal)`
(migration 023, SB-P1-03): with scopes off it is today's rule, the household shares everything (the principal is
enabled and is `system`, `harness` or `app`, or `legacy` with a matching grant); with scopes on it is section 2. The
database functions for insights, their runs, dashboards with their writes, pins and corner cards call the same function
on their record's subject (`reads_subject`, migration 027, SB-P1-06), and with scopes on check every star the record
cites against the stars the map serves the reader now, which the engine hands them (`DerivedScopes`), a dashboard's
logs against their subjects, and an insight's sources against those stars and the records the reader reads through
`Database.Visible`, which the engine hands them too (`InsightStore`). Five reads
skipped it (7.4); **today** each takes it (SB-P1-04). Below, **the predicate** means section 2 for the caller; Item is
the backlog item that makes the change.

### 7.1 Harness tools (AI clients)

| Tool | Code | Today | With scopes on | Item |
|---|---|---|---|---|
| `search_knowledge` | `Retrieval.Search` (`Core/Retrieval.cs`) | `Visible`, then the live-source check (`Retrieval.Validate`) | the predicate | SB-P1-03 |
| `search_resources` | `Retrieval.SearchResources` | `Visible`; its `subject`, `provider`, `domain` and `kind` filters narrow | the predicate; naming another person's subject returns nothing | SB-P1-03 |
| `get_resource` | `Retrieval.Resource` | `Visible` | the predicate; an unreadable id answers as an unknown one | SB-P1-03 |
| `get_related_resources` | `Retrieval.Related` | `Visible` on the origin and on each neighbour | the predicate on both | SB-P1-03 |
| `semantic_search` | `SemanticSearch.Search` (`Core/SemanticSearch.cs`) | `Visible` on each passage's resource, in the same query as the distance | the predicate | SB-P1-03 |
| `recall` (with `engine.recall` on), and `search_knowledge` with `engine.searchKnowledge: combined` | `Recall.Search` (`Core/Recall.cs`), `Mcp/RecallReply.cs` | `Visible` in both arms' queries, the rows read again and the hops' both ends; a native memory by its passages and by its text in `memories` (SB-P2-19, migration 040), never by its own row's title, never one held, deprecated or past its `valid_until`, and never through `search_knowledge` (a catalog tool in both modes); a note's or a Drive file's star by its passages' words only while `semantic_search` would serve them (as its record's newest observation with scopes on, seen within `graph.freshnessSeconds`, a live file's while its source holds their revision); the receipt hashes only the ids and revisions the caller was served, a native memory's revision its own (`memory:` and its revision, which every change moves) | the predicate; keyword weights, ranks and the budget over the caller's own rows (`RecallScopeTests`) | SB-P2-08, SB-P2-09 |
| `recall` with `as_of` (with `memory.tools` and `engine.recall` on) | `Recall.Search`, `Core/RecallAsOf.cs` | as without it, with the moment in place of now: a native memory not marked wrong whose window as it stands now covers it, in the words of its state then, and a record unchanged since; a belief of then (marked wrong since) only under the results; every read keeps `Visible` and the hold now on the memory's own row, and its state then must be not held and at or above the caller's `memory_history_floor`, so a memory held then, held now, in the trash or from before it came into a scope the caller reads is in neither list | the predicate now and the scope the memory had then (`RecallAsOfTests`) | SB-P4-07 |
| `query_health_metrics`, `compare_health_trends` | `Retrieval.Metrics`, `Retrieval.Trends` (and their `Samples`) | `Visible`, then the asked subject | the predicate: another person's subject returns no rows | SB-P1-03 |
| `discover_health_sources`, `read_health_source`, `investigate_health`, `evaluate_health_hypothesis` | `Core/HealthInvestigation.cs` (`Visible` at `:25,34,44,63,107`) | `Visible` | the predicate | SB-P1-03 |
| `get_sync_status` | `Retrieval.Status` | `Allowed` on jobs and ingest freshness, `Visible` on their coverage | the predicate | SB-P1-03 |
| `get_relationship_graph`, `explore_graph`, `graph_changes` | `RelationshipHost.Read`, `Explore` and `Changes`; `RelationshipEngine.Load` with `Served` and `Keys` (stars), `ReadEdges` (links), `Readable` | with scopes off, snapshot rows and both ends of each link with `Visible` for the caller, over one snapshot built for everyone; with scopes on, `Served` (section 8), reachable since the switch is accepted (D-131) | the predicate on each star and both ends of each link; counts, degree and paging after the filter; the re-attribution rule (section 8) | SB-P1-05 |
| `dashboard_data` | `Core/DashboardFeeds.cs:33-60` | stars from the caller's view of the map, then each feed (7.3) | 7.3 | SB-P1-04, SB-P1-05 |
| `list_insights` | `read_insights()`, `read_insight_runs()` (027) | role only: every insight and run | the predicate on the insight's subject, and no wider than what it cites; a run's on its own subject and its insight's. **Built** (027, `DerivedScopeTests`) | SB-P1-06 |
| `list_dashboards`, `dashboard_history` | `read_dashboards()`, `read_dashboard_writes()` (027) | role only | the predicate on the dashboard's subject, and every star its newest version names now (its stars and its layout's focus stars), counts after the filter; a write's also on its own subject. **Built** (027) | SB-P1-06 |
| `save_insight`, `save_dashboard`: the stars they cite | `RelationshipHost.cs:99-104` `Known`; `DashboardFeeds.cs:26-31` `Stars` | only stars in the caller's view; others dropped (insight) or refused (dashboard); a `log` feed names a subject, checked only against the install's subjects (`Core/Dashboards.cs:26`) | the same for stars, so a caller cites only what it reads; a `log` feed's subject as in section 5 | SB-P1-05, SB-P1-06 |

**Today** the memory store's reads (`MemoryStore.Get` and `List`, migration 033's store core, SB-P3-01) take the predicate on
a memory's own `resources` row through `Database.Visible`, so a filter that names a scope only narrows; no tool calls them
yet. A list serves a held memory without its words: no text, tags or project, and its tags and project never select
it in a filter either (D-149); `Get` serves it whole with its hold, for a person to review before releasing it (migration
036 decides what is held, SB-P3-18). `Get` takes no channel, so the MCP `get` tool (SB-P3-09) must serve a held
memory as a list does, without its words, and only the app and the terminal serve them, for review (D-160). **Today**
it does (below). Combined
retrieval (`Recall`, SB-P2-08; served by the `recall` tool and the explorer's search only with `engine.recall` on,
SB-P2-09 and SB-P2-10) reaches a native memory by meaning, through its passages, and by its words, through its text in
`memories` (SB-P2-19, migration 040), under the predicate and never a held one, and never by its own wordless row
(D-152); nor one deprecated or no longer true (its `valid_until` passed), which leaves combined retrieval as it leaves
the map (`Recall.Current`, judged now; SB-P2-16's as-of will judge it at
another time). Its hops start from a memory's map star too (SB-P3-11): the map's
links between native stars (in a project, a reference, a similarity) are followed, and a star row a hop reaches answers
as the memory's own row (`MemoryStars.Answering`), whose predicate and hold decide it; and since SB-P4-02 (migration 052),
with `memory.tools` on (`RecallOptions.MadeLinks`, set by `RecallServing`), the links a person or an AI client made
(`made_links`), which join memories' own rows, so a made link reaches its memory under the same predicate and hold;
off, no made link is followed, so one an operator's import brought changes no answer. A suggestion between two memories
someone dismissed is no link to follow. The
memory tools to come (`get`, `list_recent`, `history`, `connections`, `brief`,
capsules) read through the same predicate (phases 2 to 5). The catalog's tools (`search_knowledge`, `search_resources`,
`get_related_resources`) never serve a native memory's rows, its own or its star, and leave them out before their limit
(`Retrieval`, 10.1); `search_knowledge` with `engine.searchKnowledge: combined` too (since run 9's final review), whose
combined list leaves every memory-provider row out of every arm (`RecallOptions.Memories` off).

**Built** (migration 042, SB-P2-11, D-022): the opt-in recall log, off unless `engine.recallLog` says on. On, each
question the `recall` tool or the explorer's search answers is kept in its reader's own scope (the person its login is
bound to, else the household), with the ids it was served in order and its receipt, at most 200 a day a scope, for 30
days (`RecallLog`, `log_recall`); a get, append or update of one of those memories within 30 minutes marks it followed
on that scope's latest recall (`recall_followed`), counted only for its direct results, never a neighbour a hop reached;
and each scope keeps its own counts of what it recalled and followed (`recall_counts`), apart from the records, so
a read never changes a memory or its revision (D-034). Nothing ranks by them and no tool or route serves them; since
run 16 a digest's eligibility (054, behind `memory.tools`) reads them, the digest's own scope's counts alone, so one
person's questions are never counted in, capped by or served to another (`RecallLogTests`,
`The_same_store_gives_the_same_eligibility_list`); a person's AI client's recalls therefore never protect a Family
memory from a Family digest, with scopes off too (Q-159).
Only the store marks follow-ups, and only tests build it with the log until the memory tools (SB-P3-09).

**Today** a prompt capsule (`PromptCapsules.Build`, `Core/Capsules.cs`, SB-P3-08; served by `get_prompt_capsule` and the
`rhythm://capsules/...` resources only with `memory.capsules` on) is one scope's: the one the call names, else the
caller's own (its person's private scope with scopes on, else Family). The caller must read that scope (the predicate
on its subject, provider `memory`), or it is refused, never answered empty; with scopes off a private capsule is
refused as a private memory is (D-048), and a login with no person has no private one. Its memories are that scope's
canonical ones in the capsule's slots, each read through the predicate on its own `resources` row, never one held,
deprecated, past its `valid_until` or in the trash. An unknown project names only that scope's projects. Nothing is
cached, so no capsule is shared between people. A memory's slot is set only through `set_memory_capsule` (migration 039),
a change like any other under section 5's author lock; a person calls it from the app's Memories view (`app_memories`
`capsule`) or the terminal (`rhythm capsule`), both only with `memory.tools` on (SB-P3-22). A slot is a person's step (since run 10's
final review, D-172): the writer refuses an AI client's channel (`mcp`, `42501`) before it reads anything, a slot taken
out included, and an undo through that channel that would change a slot is refused too (041), so an AI client alone
never puts its own words into a capsule. **Unless `memory.capsuleWrites` is on** (migration 078; off by default, and
only with `memory.tools` and `memory.capsules` on): the engine then makes `set_capsule`, which calls the same writer
with `p_clients` true, the one thing that opens the `mcp` channel. The engine passes it from that tool alone, so
`update`'s capsule tags, and every install without the setting, are answered as before. Through that channel a slot
another memory of the scope fills (not in the trash, marked wrong or past its validity; a draft or held one counts) is
refused, naming it; the change is a version and a `capsule_changed` audit row like a person's, listed among the changes
by AI clients (`MemoryStore.Undoable`), which a person undoes from the app or the terminal; and 041's refusal of an
undo through `mcp` that would change a slot stands. The engine records which client changed a slot and cannot tell
whether a person asked ([../security.md](../security.md#capsule-slots-set-by-an-ai-client)). What is not enforced: confirming a slotted draft is anyone's who may change the
memory, an AI client included (D-145), and an AI client may edit the words of a memory a person has slotted, screened by
036 like any change's; upstream's capsule hold signal is SB-P5-01's.

**Today** a memory's history and a state by revision (`MemoryStore.History`, `GetVersion`, SB-P3-03) take the same
predicate on the memory's own row first, then the history rule of section 5 on each state and audit row; a count and a
tag vocabulary (`MemoryStore.Count`, `Tags`, SB-P3-17) take a list's own predicates (`Filtered`), so each count is the
length of the list it summarises, a tag comes only from a memory the reader reads, and a held memory's tags are none of
it. The `history` tool and `get`'s `version` serve the first two (below); the app's Memories view serves a count and the
tag vocabulary (`app_memories`' `tags`, SB-P6-01); the brief takes each count from the statement that reads its own list
instead.

**Today** the AI change listing and a group undo (`MemoryStore.Changes`, `UndoGroup`, SB-P3-05) read the audit trail
the same way: each change row only on a memory whose own row the reader reads (the predicate), none in the trash, each
from the revision the reader's history starts at (034's floor), and no words of any memory, so an author's changes from
a memory's private stretch are never in a Family reader's listing, and a key anchored on one names no group for him
(`MemoryUndoTests`). The `undo` tool calls `UndoGroup` with a key (below), and the brief serves `Changes`, leaving out what is
held now (SB-P5-08).

**Today** the memory tools (SB-P3-09, `Mcp/MemoryTools.cs`, made and listed only with `memory.tools` on; with it off
none exists and every answer is as before) are AI clients' reads and writes of the store above, each as the caller,
through the channel `mcp` fixed in its handler (section 5):

| Tool | Store | What the caller is served |
|---|---|---|
| `remember` | `Create`, `Commit`, `Remember` | where its memory landed (section 5, Where writes land), never its words; a held one's answer names no project; with `owed_to` or `owed_by`, the commitment it recorded, its direction and the counterparty as words made from its slug (058); the due date it saved, never a held one's, or why it took none of the dates its words name, without a held memory's words (061); `duplicates`, the ids and scores of at most 3 live memories of its own scope it was flagged as a possible copy of, never for a held memory and never naming one (063) |
| `append`, `update`, `set_status` | `Append`, `Update`, `SetStatus` | the author lock's answer; `update`'s capsule tags go to `SetCapsule` through `mcp`, refused before it reads anything, and nothing of the call is written; `update`'s `valid_from` and `valid_until`, and `clear`'s, go alone to `SetValidity`, and `clear` `retracted_sources` alone to `ClearFlags` (049); a change that retracts a memory or brings it back names what its cascade did |
| `get` | `Get`, `GetVersion`, `Citations` | the memory whole, or one state; a held memory or state without its text, tags or project, only its hold (D-160); with its `state`, `replaced_by` (a memory of its own scope) and `retracted_sources` (049); `citations` (055) only of documents the reader reads under the map's newest row for each (`memory_citation_readable`, none when the map holds no row), none for a held memory or a version, each checked at Drive for that answer unless the map no longer holds the document as current (then unchecked, saying why), and nothing a check finds stored; `duplicate_of`, the ids and scores of the live memories of its own scope its flags name while both stand, never on a held memory or naming one (063) |
| `supersede` | `Supersede` | the plan with its confirmation, the plan applied, or why nothing needed doing; the replaced memory's revision, scope and status, never a memory's words (049) |
| `link`, `unlink` | `Link`, `Unlink` (`Core/MemoryLinks.cs`) | 052's checked writers decide under the recorded mode: both memories read by the caller (else `unknown`) and of one scope, the source end the caller's to change (section 5), neither held; `unlink` leaves a supersede's link (every type but `supersedes`; naming it is `invalid`) and keeps a dismissal in `memory_link_dismissals`, never as a link; the link as stored and the source end's scope, never a memory's words |
| `connections` | `Connections` | the memory and each neighbour through `Database.Visible` on their own rows, in one repeatable-read transaction: the made links, then the map's suggestions between their stars at both stars' current revisions, none dismissed; a held neighbour without its words, with its hold; whether the caller may unlink each |
| `list_recent` | `List`, `Trash` | a list's predicates and previews, each with its `duplicate_of` as `get` serves it (063); the trash only what the caller may restore, `may_delete_forever` false |
| `forget`, `undo` | `Forget`, `Undo`, `Restore`, `UndoGroup` | an undo of a memory in the trash restores it, held still if it was held (its answer says so); the trash nonce restores one exact trip, and a retry with it answers `unknown`; right after a restore an undo of the newest change changes nothing (a restore moves no revision, so it would otherwise undo the change before the forget); through `mcp` an undo never releases a hold or changes a slot (041) |
| `history` | `History` | from 034's floor; no preview of a held state |
| `list_projects` | `Projects` | the projects of scopes the caller reads, never one a held write made while its memory is held (046) |
| `list_teams` | `Database.Allowed` on the household's memory scope | Family when the caller reads it, with its member count (every current person while scopes are off); else none |
| `list_stale` | `Stale` | the stale queue of memories it reads and may change, with the queue's `total` from the same read, each a 500-character preview with why it is there; a held memory is never in it (053) |
| `still_true` | `StillTrue` | a write's place with its new checked time, why it was in the queue and the flags that came off, never a memory's words; no revision moves (053) |
| `list_loops` | `Loops` | the open loops it reads, may change and owns (section 5), with the asked list's `total` and both directions' lengths from the same read, each a 500-character preview with its direction and counterparty; a held memory is never in it (058) |
| `resolve_loop` | `Resolve` | a write's place with the action, the loop's direction and counterparty and the fields it changed (`tags`), never a memory's words; a held memory is refused (058) |
| `list_decisions` | `Decisions` | the decisions it reads, may change and owns (section 5, the open loops' rule), in one list (`open`, `due`, `upcoming`, `resolved`, `scored` or `all`), with the asked list's `total` and the five lists' lengths from the same read, each a 500-character preview with its confidence, outcome and review date; a held memory is never in it (060) |
| `record_outcome` | `RecordOutcome` | a write's place with the result, the outcome before, the re-asks and the next review date and the fields it changed (`outcome`), never a memory's words; a held memory is refused (060) |
| `decision_calibration` | `Calibration` | counts and figures over the scored list it is served (`list_decisions` `scored`: the same clause, so `n` is that list's length), none below its minimum; tag names only as a topic, from a memory it reads; writes nothing (060) |
| `list_due` | `Due` | the memories with a due date it reads, may change and owns (section 5), soonest first, with the asked window's `total` and the three windows' lengths from the same read, each a 500-character preview with its due date, window and open loop's direction; a held memory is never on it (061) |
| `resolve_due` | `ResolveDue` | a write's place with the action and the due date after and before it, never a memory's words; a held memory is refused (061) |
| `list_contradictions` | `Contradictions` | the pairs of memories of one scope it reads and may change (either one) that 068's `memory_contradiction_listed` lists, newest first, with both previews and the review's `total` from the same read; `may_settle` false, since only a person settles one |
| `propose_contradiction` | `Propose` | a write's place with the other memory, how close and the reason kept, never a memory's words; only a candidate offered for that memory (`not_offered` otherwise, whatever the id names), read through `reads_subject` (068) |
| `list_duplicates` | `Duplicates` | the memories it reads and may change that were flagged on save as copies of another of their own scope while both stand, newest first, with the review's `total` from the same read, each a 500-character preview with the copies it names; a held memory is never in it, nor named (063) |
| `list_insight_candidates` | `Insights`, `InsightPreview` | the listed candidates whose two memories it reads (the predicate on both memories' own rows), best first, with the list's `total` from the same read, each with both memories' 500-character previews; with `state` `used` or `rejected`, those decided while their pair is live, each with its note and `insight_id` only when `reads_subject` says it reads that insight's scope, else null (066); never a pair with a held, trashed, deprecated or ended memory or one across scopes; a dry run only over the scopes it reads (`reads_subject`), writing nothing (064) |
| `resolve_insight_candidate` | `InsightStore.Decide` (066, over 064's `ResolveInsight`) | the candidate's id, the decision and when it was made, its note and the insight named, never a memory's words; only a candidate it is served (its scope read through `reads_subject`, its pair live), else `unknown`; the insight named only one it reads, citing both memories, no wider than the pair, and on a repeat the one kept only when `reads_subject` says it reads that insight's scope, else null; no memory changes (064, 066) |
| `list_insight_stream` | `InsightStore.Stream`, `Today` | the insights it reads (`insight_readable`, as `list_insights`), each view's count read with the page; the day's run only of those it reads (as `read_insight_runs`), its declined count only of the scopes it reads (066) |
| `review_insight` | `InsightStore.Review` | only an insight it reads, else `unknown`; section 5's rule and never its own login's over MCP, else `forbidden`; the review alone changes (066) |
| `insight_history` | `InsightStore.History` | only an insight it reads, else `unknown`; events in the insight's subject, each naming a person's id, never a login's name (066) |
| `brief` | `Brief` | each section the head of the list its own tool reads, under that list's predicate and rule, with that list's whole length; no held or trashed memory; an AI change only on a memory it reads, none held now or in the trash; `project` names standing instructions and narrows nothing else (069) |
| `resolve` | `ResolveItem` | a write's place with the queue, the action and what the queue's step answers, never a memory's words; an item of a scope it does not read answers `unknown`, one it may not change `forbidden`; an item no longer waiting writes nothing and answers `unchanged`; `release` and a settling answer `needs_person`, reading nothing (071) |
| `list_standing` | `Standing` | the standing instructions it reads (the predicate on each memory's own row), oldest first, with `project` only those that fire inside it and nowhere else and never a held one, each with whether it fires and why not (`held`, `project_held`, `over_limit`), with `total` and how many may fire, are held and are over the limit from the same read; a held one without its words, tags or project (065) |
| `stop_standing` | `StopStanding` | a write's place with the fields it changed (`standing`), never a memory's words; a held memory is refused (065) |
| `digest_sources` | `DigestSources` | one scope only, the one named or the caller's own where it reads (033's landing rule): the memories a digest of a project or tag there would draw from, 500-character previews, never a held one, with the list's basis; writes nothing (054) |
| `save_digest` | `SaveDigest` | a write's place for the new digest, in that scope, with the memories it was drawn from by id, never a memory's words; no source changes (054) |
| `digest` | `Digest` | the newest live digest of the key in a scope the caller reads, whole as `get` serves it, never a held one, with `edited` once a person's or a client's change of its words or tags made it theirs (063's `memory_system_row`); writes nothing (054) |
| `cite` | `OnMap`, `Cite` | a write's place and the citation as `get` serves it; asked of the map before Drive (a document the caller does not read on the map is `unknown_document`, never looked up), under the author lock, never for a held memory, with scopes on only a document of the memory's own subject or Family's; no revision moves (055) |
| `revalidate` | `Citations`, `Revalidate` | only the citations `get` serves the caller, under the author lock, never for a held memory; a document the map no longer holds as current is not checked or moved, and a check is written only while the citation still has the checked time it was compared against; every move listed, no revision moves (055) |
| `share` (only with `memory.share` on as well) | `Share` | where the memory is now and what moved with it (a write's answer: its project by id and slug, none for a held memory or for a caller that does not read where it landed); refused with scopes off, and for either memory of a contradiction queued for a person (`needs_person`, since 068); after a move the map is asked for a rebuild (section 5) |

With scopes on a client reaches no scope its login does not read (`With_scopes_on_an_AI_client_reaches_only_scopes_its_login_reads`).
**Today** (migration 046, SB-P3-21) a project a memory write makes is held with its memory: kept in the registry, but
out of every list the engine serves (`list_projects`, a list filter's and a capsule's answer to a name it does not
know, the map; `MemoryStore.ProjectListed`) while that memory is held. A write through a channel scored as an AI
client's that names such a project, or moves a memory into one, is held too (045, reason `held_project`), so the login
that planted the held write cannot list the slug with one clean write more, and no answer of that memory names it.
That includes the planted memory itself once a person has released it in another project (since run 12's fix review):
the project it left while held stays held, and a write that takes the memory back there is held again. And the slug
such a write gives a new project is screened as text, hyphens read as spaces (045, since the fix review): one that
reads like instructions holds the write, reason `instruction` with the slug's signals, so its project stays held with
it. So a planted instruction's project slug reaches no AI client, with scopes off or on, whether the instruction is in
the memory's words or in the slug. Only a person's step lists the project: a release of a memory in it (only the
project the released state is in, which the person reviewing it saw; one the memory left while held stays held, D-161),
or a write through a person's own channel naming it, where a slug, like the words, holds nothing for instruction
phrases. A project a person makes, or an import brings, lists as before (neither screens its slug), and one whose held
memory is deleted for good stays unlisted; a person who names a held project's slug for a new project, or as an alias,
is told a memory held for review named it and that it lists once a person releases a memory in it or writes one into
it from the terminal (`MemoryHeldProjectTests`). An export carries such a project's hold (SB-P3-23, section 9), so an
import keeps it unlisted there too, until a person releases a memory in it on that install.

### 7.2 App tools (the app's server)

| Tool or route | Code | Today | With scopes on | Item |
|---|---|---|---|---|
| `app_graph` `overview`, `search`, `neighbors`, `get` | `RelationshipHost.cs:49-65` | as `get_relationship_graph` | as `get_relationship_graph`, for the household view or a viewer session | SB-P1-05 |
| `app_graph` `content` | `RelationshipHost.Read`, `WithRecordText` | text only for a star in the caller's page: a memory's from its adapter, a record's and its pages' through `CatalogReader.Text` with the predicate for the caller (SB-P1-04) | the same | SB-P1-04 |
| `app_graph` `data` | `RelationshipHost.Read`, `WithMeasurements`, `CatalogReader.Series` | a data star in the caller's page, then its measurements with the predicate for the caller (SB-P1-04) | the same | SB-P1-04 |
| `app_graph` `recall` (only with `engine.recall` on) | `Core/RecallStars.cs` | `Recall.Search` for the caller; each result shown as a star only from the caller's own view of the map (`RelationshipHost.Readable`), a series' data star after `Database.Visible` on its rows; `offMap` a count of the caller's own results that no star it reads stands for; the receipt over those results | the same: another person's private document, series or memory gives neither a star, a title nor a passage, nor a count (`RecallStarsScopeTests`) | SB-P2-10 |
| `app_insights`, `app_insight_feedback` | `read_insights()`, `insight_feedback()` (027) | role only | the predicate; feedback only on an insight the caller reads. **Built** (027) | SB-P1-06 |
| `app_dashboards`: list, get, data, history, pins, corners | `Core/Dashboards.cs` `DashboardStore` and the 027 functions | role only; data through the caller's view of the map | the predicate on dashboards, pins and corners; feeds as 7.3. **Built** (027): a card part of a dashboard the reader can't read answers `dashboard` null and `unavailable` true | SB-P1-06 |
| `app_latest_glucose`, `app_recent_glucose` | `Mcp/AppHealth.cs` | the predicate for the caller (SB-P1-04, 7.4): the readings, and their last sync from ingest freshness as `get_sync_status` reads it; the default subject is the first person whose readings the caller reads (`Subject`), and the tools are listed only to a caller that reads some glucose (`HasGlucose`), each looked up at most once a minute per caller | the same | SB-P1-04 |
| `GET /app/documents/<id>` | `Mcp/AppFiles.cs` (`Serve`) | the predicate for the caller (SB-P1-04): a current `documents` original of a configured subject, by id; an unreadable id answers 404, as an unknown one does | the same | SB-P1-04 |
| `app_garmin_summary` | `Adapters/Garmin/GarminTiles.cs` (`Read`) | the predicate for the caller (SB-P1-04): the Garmin owner's measurements, its argument fixed to that owner, and their sync jobs and ingest freshness as `get_sync_status` reads them | the same | SB-P1-04 |
| `app_request_garmin_sync` | `Adapters/Garmin/GarminTiles.cs` (`Request`) | a write that answers with the Garmin owner's sync state (`lastSyncedAt`, `pendingSince`): the predicate for the caller on the owner's Garmin rows first; a caller who can't read them queues nothing and is refused (403), as a caller without the app role is | the same | SB-P1-04 |
| `app_request_refresh`, `app_upload_document` | writes | return no rows | unchanged; an upload lands as section 5 says | n/a |
| `app_sync` | `Core/SyncRequests.cs` (`Reads`) | no rows: each adapter on the scheduler's clock with its state and times (running, waiting, when it may be asked again, the last asked-for batch's outcome), and a request for one batch now; the predicate for the caller on the adapter's owner (`IAdapter.Owner`, section 10.2) for every provider it writes, which with scopes off every app principal passes | the same: an adapter whose owner's rows the caller does not read (a person's Garmin or Nightscout for another person, or for the household view without a source share) is left out of the list, and asking for it answers 404 as for an unknown adapter and queues nothing. A CSV folder's rows may name other subjects; its owner (`adapters.csv.owner`, default the household) decides. An adapter that states no owner is shown to no one with scopes on: the rule fails closed, and every adapter on the clock states its own (`SyncRequestsTests`). **Built** (`SyncNowDatabaseTests`) | SB-P6-09 |
| `app_viewer` | `AppEndpoint`, `ViewerSessions` (SB-P1-11) | none: not listed or accepted while `app.viewers` is off | no rows: `enrol` answers the code's person (only to the shared token), `whoami` the caller's own person, `end` signs the caller out | SB-P1-11 |
| `app_status` (and `GET /health`) | `Core/ModelStatus.cs` | no rows: each model's state, and install-wide counts (passages per model, rows the index job has left); never a row's text, title or id | no counts at all: each embeddings model's `index` is null for every caller, since the counts are every scope's together (6.2, D-103); the states as before. **Built** (`ModelStatusDatabaseTests`); the operator reads the passages with `rhythm doctor` | SB-P2-01 |
| `app_memories` (only with `memory.tools` on) | `Mcp/AppMemories.cs` | the memory store's reads as the MCP memory tools take them (7.1), as the app (channel `app`): `list` and `trash` serve a held memory without its words, `get` serves it whole with its hold for a person to review (D-160), `history` and a version read leave a held state's words out (034); `review` serves the review queue the reader reads, never a held memory's words, tags or project (SB-P3-22); its writes are the checked writers under section 5's author lock, `release_all` and `capsule` a person's steps, and with `memory.share` on `share` (since run 14, listed only while scopes are on, and only to a login that speaks for a person); the map's kept dismissals (`dismissals`, `dismiss`, `undismiss`, SB-P4-05, section 8), each suggestion or dismissal named on and checked against the reader's own view of the map; a person's link steps (`connections`, `stale`, `link`, `unlink`, `still_true`, `clear_flags`, run 17), the MCP tools' checked functions through the app's channel; the duplicate review (`duplicates`, `keep_copy`, and `capture` checked for copies, run 19, 063), as `list_duplicates` and `update`'s `clear` `duplicate_of`; Home's old memory (`resurface`, `resurface_dismiss`, run 20, 070), which no MCP tool has: `resurface` through `Database.Visible` on each memory's own row, 070's `dismiss_resurfaced` deciding the dismissal; the brief's lists (run 21, SB-P6-01, `Mcp/AppMemoriesPlans.cs`): `loops`, `due`, `decisions` and `calibration` through the store's own lists under 058's and 069's predicates, as `list_loops`, `list_due`, `list_decisions` and `decision_calibration`, and `resolve_loop`, `resolve_due` and `record_outcome` through their checked functions on the app's channel, a person's; the full memory UI's list (run 21, SB-P6-01, `Mcp/AppMemoriesManage.cs`, `Core/MemoryBulk.cs`): `tags` through the list's own predicates, `changes` the audit trail as the brief's AI changes read it with a preview of each memory through `Database.Visible` on its own row and none of a held one, `undo_group` the undo tool's `group` through the app's channel, `forget_all`, `restore_all`, `delete_all` and with `memory.share` on `share_all` each memory through its one checked writer, refused through an AI client's channel in the store, 035's project writers, which no MCP tool calls, `capsule_status` the capsule's own read and `Waiting` with no words, and `settings` | the same, for the household view (Family and DEMO; a capture names Family) or a viewer session (`With_scopes_on_the_app_reads_and_writes_as_the_household_view`, `With_scopes_on_the_apps_review_queue_is_its_readers`, `With_scopes_on_a_suggestion_is_dismissed_by_whoever_may_change_either_end`, `With_scopes_on_a_persons_link_steps_keep_the_author_lock_and_one_scope`, `With_scopes_on_each_of_several_keeps_the_author_lock_the_one_scope_and_its_hold`, `A_tag_or_a_count_of_a_private_memory_reaches_no_other_reader`, `With_scopes_on_AI_changes_are_their_readers_and_keep_the_author_lock`, `With_scopes_on_a_project_keeps_the_author_lock_and_the_one_scope`, `With_scopes_on_a_capsules_status_is_its_readers`, `With_scopes_on_the_cards_counts_are_still_its_lists_and_only_the_readers`); a person's review queues (SB-P6-01, `Mcp/AppMemoriesQueues.cs`): `queues`, each count through the read of its own list, `insight_candidates` through 064's predicate and `insight_stream` and `insight_history` through 066's definition and the insight's own read check, with `mayReview` from 066's `insight_may_review`, `review_insight` under section 5's rule, and `resolve` through `MemoryStore.ResolveItem`, each step's own locks and refusals (`With_scopes_on_each_queue_is_its_readers`, `No_AI_clients_token_reaches_a_settling_a_release_or_a_review_of_its_own`); the memory UI's last pieces (run 22, SB-P6-01, `Mcp/AppMemoriesStanding.cs`): `list`'s and `tags`' `query`, the words a memory holds, through the predicates every list shares, never a held memory's words, `standing` as `list_standing` reads it under 065's `memory_standing_rows` and the read predicate, `stop_standing` through 065's checked writer, and `resolve_all` each item through `MemoryStore.ResolveItem`, refused through an AI client's channel in the store (`With_scopes_on_words_find_only_what_the_reader_reads`, `With_scopes_on_standing_and_several_are_their_readers`: another person's private instructions in no count of the list and no stop, and a step for several over their private items each `unknown`; `No_AI_clients_token_reaches_the_new_steps`) | SB-P3-12, SB-P3-22, SB-P4-05, API-21, SB-P5-02, SB-P6-01 |

### 7.3 Dashboard feeds (`dashboard_data`, `app_dashboards` data)

| Feed | Code | Today | With scopes on |
|---|---|---|---|
| `data` | `DashboardFeeds.Data` | the star must be in the caller's view; its measurements are then read with the predicate for the caller, the metrics a star lists kept per reader (SB-P1-04) | the same |
| `contents` | `DashboardFeeds.Text`, `SourceContents` | the star must be in the caller's view; text through `app_graph` `content`, or the adapter's `Contents`; a native memory's from the memory tables at that read (`MemoryStars.Text`, SB-P6-03), its star in the view only while the caller reads the memory's own row and it is not held, deprecated or past its validity (with `memory.tools` on, a replaced one is in the view too, marked by its `truth`, SB-P4-11) | as `app_graph` `content` (SB-P1-04, SB-P1-05); a memory of another person's private scope is "not on the map", with no title (`MemoryDashboardTests`) |
| `links` | `DashboardFeeds.Links` | `explore_graph` around a star in the caller's view, a native memory's or project's included (SB-P6-03) | as `explore_graph` (SB-P1-05) |
| `log` | `DashboardFeeds.LogFeed` | a log names no star, so it is checked against its subject instead (SB-P1-04): a caller who can't read the log's subject gets `unavailable` ("This log belongs to a scope you can't read.") whether or not the log has entries; the entries are read with the predicate for the caller | the same |

### 7.4 The reads that skipped the predicate

Phase 0 found four (ledger SEC-06); a fifth, `GarminTiles`, turned up while writing this contract. **Today** each reads
through the predicate as its caller (SB-P1-04); the table says where each was.

| Read | Where | Reached by | Reads |
|---|---|---|---|
| `CatalogReader` | `Core/RelationshipCatalog.cs:108` (`Own`), used at `:126,159,203,231,246` | the map rebuild (system), and `app_graph` `content` and `data` and the dashboard feeds after their star check | document text and pages, and measurements, of any configured subject |
| `AppHealth` | `Mcp/AppHealth.cs:19`, used at `:46,59,79,130,206` | the app | glucose readings of any subject |
| `DashboardFeeds` | `Core/DashboardFeeds.cs:18`, used at `:113,180,200` | the app and AI clients | a data star's measurements; log entries of any subject |
| `AppFiles` | `Mcp/AppFiles.cs:41-43` | the app | any current `documents` original of a configured subject, by id |
| `GarminTiles` | `Adapters/Garmin/GarminTiles.cs:51` (an inline filter) | the app (`app_garmin_summary`) | the Garmin owner's measurements |

SB-P1-04 moved each onto the predicate. `MemoryScopeTests` names every harness tool, app tool (an adapter's included)
and `/app` route with the item that checks it or still owes its predicate, so a new one fails until it is listed. It
reads each of the five with scopes on, so that every reader is served rows of its own scopes only
(`The_catalog_reads_each_record_and_series_as_its_reader`, `The_apps_glucose_reads_only_the_callers_scopes`,
`The_document_route_serves_an_original_only_to_its_readers`, `The_Garmin_summary_reads_its_owners_rows_as_the_caller`,
`Each_dashboard_feed_reads_as_its_reader_and_a_log_is_checked_against_its_subject`). And it fails when any read in the
engine's source writes a visibility rule of its own: a row's expiry checked anywhere but in `Database.Visible` and in
the map's `Served`, which pairs it with `Database.Allowed` (`No_read_of_the_catalog_writes_a_visibility_rule_of_its_own`).
Its limit: it catches a hand-written expiry check only. A read that writes no rule at all, or checks only a row's
status, passes it, and so does SQL in `engine/migrations/` (`request_garmin_sync` read its sync jobs that way until
`GarminTiles.Request` checked the caller first); the tool and route listings, and a database test per read, are what
catch those. The rebuild goes on reading everything, through the
predicate with its system principal (the map host's configuration's) rather than a string of its own. An adapter's
bookkeeping reads during its own sync or import (`CsvAdapter.cs:284`, `DocumentsAdapter.cs:73,147`,
`GarminAdapter.cs:280,304`) look only at that adapter's rows and return nothing to a caller; they stay as they are.

### 7.5 Screens

`control_screen` ([screens.md](screens.md), the screens program) opens a star or runs a search on a screen and reports
titles back to whoever sent it. A screen reads as the app principal, so with scopes on and no viewer session it shows
the household view, and a sender learns nothing beyond Family. Once a screen can show a viewer's private stars
(SB-P1-11), `open_star` and `search` answer a sender who can't read a star as for an unknown star, and no private title
travels in a command, a reply or a screen's `showing` report (SB-P1-15, agreed with the screens program).
Titles are not the only private text: a viewer session's `showing` (its query, star or dashboard, id and title) and its
commands' arguments are reported to another principal (`list_screens` returns them today, `Core/Mcp/ToolText.cs`) only
for records that principal reads, and the query text a viewer session typed ("Sam surprise party guests") is never
reported to a principal of another person or to the household view; that person's own logins (their AI client, their
other sessions) are told it (the program's decision log D-112).

**Built** (SB-P1-15; migration 027, `Core/Screens.cs`, agreed with the screens program;
`ScreenScopeTests`), reachable with scopes on only. A screen's `showing` and a command's outcome are the view of the app
login that reported them, which the engine keeps (`screens.shown_by`, `screen_commands.reported_by`): the household
view's (the shared app token's) or a viewer session's. Every reader is told, by `list_screens` and `app_screens list`
(`read_screens`), only the open star, dashboard and Home dashboard it reads now, so a record that has left its scopes
since the screen reported it is not told either, and a dashboard's `parts` with their dashboard. Of the rest of a
showing only the keys that name no record (`view`, `layout`, `tiles`, `narrow`, `activeAt`) reach a reader that does
not read all that login reads; any other key a screen reports, today's app reports none, is told only to one that does
(`screen_showing_for`, D-126). The query, and by
`control_screen` and `app_screens send` (`read_screen_command`) a command's outcome with its detail, reach only a reader
that reads all that login reads (`reads_all_of`: a login bound to the same person; for the household view's screen,
every reader of Family); anyone else is told the outcome without its detail: no titles, counts or summaries from that
screen. A
command names only what its sender reads: `open_star` and a focus star as before (an unknown star's answer), and every
dashboard through `DashboardStore.Get`, so one it can't read is refused exactly as an unknown one (404, "Unknown
dashboard."), an arranging command left to a screen that shows such a dashboard included. A command for a screen whose
login doesn't read all its sender reads carries only what that login reads (its star, focus star and dashboard, 404
otherwise), so neither a person's private star id nor their private dashboard's reaches the household view's screens or
another person's; and a `search` sent from a viewer session goes only to its person's own screens. An `undo` names
nothing, but the screen saves its earlier arrangement back through its own login, so it goes only from a sender that
reads all that login reads (404 otherwise, naming nothing). A search an AI client sends is its own words, which it
chose to show on that screen; it is then that screen's query, told to readers of that screen's login (D-111). With
scopes off nothing here is read and every answer is as before (`ScreenTests`, `DashboardLayoutTests` unchanged).

**Built** (SB-P1-19; migration 029, `Core/Screens.cs`, agreed with the screens program;
`ScreenBindingTests`), with scopes off and on alike: a screen's id is the app login's that first listened with it
(`screens.bound_to`). `list_screens` shows every screen's id, but only that login listens as the screen (and so takes
its commands), reports for it, sends as it and renames it, so the `settings` a listen and `rename_screen` answer, its
Home dashboard's id among them, reach that login alone; any other app login is refused (42501, a 403 at `app_screens`)
before anything is written, and a listen counts as its screen listening only once its first look is answered, so a
refused or abandoned one never makes the screen seem to listen nor ends its own login's listening. The household view is
one login here: a screen the shared app token's login made is every such login's, so a new app token keeps the
household's screens. A screen from before 029 is the household view's alone, as every screen made before 029 was, until
the household view's next listen binds it, which is never refused for it. The 100-screen cap is per login: a new screen
replaces the one its own login has seen least lately, so no login pushes another's screen out and then takes its id. A viewer session's screen stays that
session's once it has ended, so its settings never reach another login. While `app.viewers` is off every app login is
the household view's, so nothing is refused (`ScreenTests`, `DashboardLayoutTests` and `ScreenScopeTests` unchanged).

A browser that listened as one login and then as another (a viewer session opened where the household view listened,
or one ended) is refused for the screen it listened as; with `app.viewers` on the app's server answers that listen
`{taken: true}` and the app takes a new screen id, listening as a new screen of its new login (`app/src/screens.ts`,
`useScreenListen.ts`, agreed with the screens program; `docs/dev/screens.md` 3.2 and 6).

## 8. Copies, vectors and neighbours

Everything Rhythm keeps that was taken from a record is a copy, and **a copy is never wider than its source, and narrows
when its source narrows, at the next read**: reads check the source record's current subject, not a scope stored with
the copy.

| Copy | Where | Holds | Served with scopes on |
|---|---|---|---|
| a map star | `resources.relationship_snapshot` (009:3), a row per source record and subject | title (a Second Brain memory's is its first line, up to 80 characters), overview, groups, links, a vector, and for a native memory's star the memory's kind (for the episodic rule, 051) | to its record's readers; its vector and its kind to no one |
| link evidence | `relationships.evidence`, `details` (009) | a locator, a bounded excerpt (a note's raw wikilink, for example), the method, both revisions | with the link, to a reader of both ends |
| `save_document` copies | `resources` rows of provider `documents` (`save_document()`, 017), with their passages | the full text an AI client saved, and the original's id as the copy's external id (a legacy copy's `sourceFileId`) | a copy whose original the engine holds (that external id or `sourceFileId` equals a record's file id or address) only to readers of the original's **current** subject, by a join at read time, never by the copy's own stored subject alone: a reader needs both, so a copy is never wider than either (`Database.Visible`; the original is any record not itself a copy whose snapshot `Key.ExternalId` or external id is that id, whatever its status); a copy of nothing the engine holds, by its own subject. So a copy of Alex's private Drive file saved as Family before switch-on is not served to Sam, nor Alex's private copy of a Family file |
| passages | `resource_chunks` (005, 024), made by the index job (SB-P2-04, SB-P2-05) for the kinds `adapters.embeddings.sources` lists (`Core/PassageSources.cs`) | each chunk's text and vector, tagged with its model, at the parent's revision (a native memory's: the revision its text was last written at in `memories`, 033) | to readers of the parent row (for a copy, as the copy; for a note's or Drive file's star row, as that star, its newest observation only (`RelationshipEngine.Newest`, before the reader's filter, as the map serves it), and only while a pass saw it within `graph.freshnessSeconds`, a Drive file's also only while Drive holds their revision, checked at each read, eight at once and within 5 s a search, one not answered in time left out and counted in the answer, D-085), and only while the settings list their kind (`SemanticSearch`); a held memory's never, and a change of its text or hold drops them (026). Phase 3's checked memory writers lock a memory's `resources` row before they update `memories`, the order a share takes, or an edit and a share of one memory deadlock with 026's trigger. **Today** 033's do (`memory_for_change`); an edit waiting for a share of the same memory lands after it, the share holding the `resources` row before it moves it, which deadlocks under the reversed order (`Concurrent_changes_never_lose_text`). A native memory's passages are made at its text revision, not its revision (D-149) |
| health projections | `source_payloads` (007) | bounded JSON of an original | to readers of the parent row (`read_health_source` joins it today) |
| in-process caches | `DashboardFeeds.measuredBy`; `AppHealth.cache` and `chosen`; `DuplicateCheck`'s vectors (SB-P5-02) | metric names per data star; whether any glucose exists; the default glucose subject; a native memory's vector of its words by id, text revision and model, made when a save of its scope is checked | consulted only after the caller's own check, or kept per reader (`AppHealth`'s per caller, tested with one instance for every reader; `measuredBy`'s defensively, since a data star is served exactly to readers of its rows); never text. A duplicate vector is read only for the memories the check's own query chose in the saving memory's scope, and never served: the answer names only memories of that scope |
| insights | `insights` (011, a subject and scope from 027) | titles of the stars and sources cited, and the AI's text about them | by the insight's subject, and no wider than every star it cites and every star or record its sources name now (SB-P1-06, built in 027: `insight_readable`, the records through `Database.Visible` as the reader). A source counts by what it named at save; an insight saved with scopes off, every one 027 found, by its every locator not on the web, so one naming a private record, a star off the reader's map or nothing the engine can tell is unknown to that reader. A source with no locator names nothing to check |
| insight run notes | `insight_runs.note` (012, a subject and scope from 027) | free text an AI wrote about a run | by the run's subject, and no wider than its insight now (built in 027) |
| an insight's feedback and reviews | `insight_events.note` (066), beside the feedback `insights.feedback` keeps the latest of | free text a person or a client wrote about an insight, which may be about private or Family records | by the insight's subject, read only by who reads the insight (`read_insight_history`, `insight_readable`); with scopes off, by every login, which `rhythm scopes off` counts per person first (built in 066) |
| an insight candidate's decision | `memory_insight_candidates.note` (066) | free text a client wrote about a pair of memories | by the pair's scope, while its pair is live (`reads_subject`, `memory_insight_live`); `insight_id` beside it only to a reader of that insight's scope (built in 066) |
| dashboards and their history | `dashboards` (a subject and scope from 027), `dashboard_versions` (013, 015, 022), `dashboard_layouts` (022), `dashboard_writes` (015, a subject from 027) | an AI's page, star ids and titles, feeds, a layout; before and after values of a Sheet, Doc or log | by the dashboard's subject, no wider than every star its newest version names now (its feeds' and writes' stars, its layout's focus stars) and the subject of every log it reads or writes, and a version only with the stars and logs it names; history also no wider than the write's own subject (built in 027). A card part of a dashboard the reader can't read names no id (`dashboard_cards_shown`) |
| pins and corner cards | `sky_pins` (016), `ambient_cards` (015), each with a subject from 027 | star ids, dashboard ids | a pin by its subject (its star's) and its star now; a corner by its subject (its chooser's: the household's, or with scopes on a viewer session's person's, 030) and its dashboard now (built in 027 and 030); a pin of a private star never shows to another reader |
| screens | `screens.showing`, `screen_commands` (the screens program's 020) | what a screen shows; commands with star ids and queries | 7.5: a viewer session's showing and command arguments only for records the asker reads, its typed query text to no other principal (SB-P1-15, built in 027 and `Core/Screens.cs`) |
| the app | the browser | nothing: no `/api` answer is cached (`app/vite.config.ts`, `runtimeCaching: []`), text lives in React state until the reader closes, `localStorage` keeps the layout; but the browser's own history keeps the address a capture tab opened with (SB-P7-03): the page's title, its address and up to 8 000 characters of selection after the `#`, which a browser that syncs history sends to its sync service too ([../security.md](../security.md#saving-pages-from-the-browser)) | unchanged |
| an AI client | outside Rhythm | what it was answered | beyond Rhythm's reach: scopes decide what Rhythm answers, not what a client keeps |
| logs | the engine's request log and rebuild notes | fixed fields, kinds and counts; never an id, title or text (`Mcp/RequestLog.cs`, `RelationshipHost.Notes`) | unchanged |
| backups | restic, outside the database | everything | the operator's (section 9) |

**Vectors.** No tool returns a vector. Star vectors are read only by the rebuild (every other read drops them,
`RelationshipEngine.Load`). A passage's vector answers a query only as a score beside a passage the caller reads, and
the scope filter sits in the same query as the distance (`SemanticSearch.Search`), with no unfiltered fallback (when its
vector store rejects the workspace filter, upstream queries without it and filters afterwards, `src/vectorize/scope.ts`;
Rhythm fails closed). A filtered vector search is exact, as today's is, or an iterative scan that keeps reading until it
has its `k` readable rows; never an approximate index that takes its `ef_search` candidates first and filters after,
which would give a reader with many hidden neighbours fewer answers and let hidden rows decide which readable ones are
seen. Comparing records of different scopes happens only in the system rebuild, and the links it makes follow the
neighbour rule below. A share or un-share changes a native memory's subject in place, so its passages and vectors go
with it; nothing is re-stamped.

**Budgets.** The map rebuild takes one `graph.maxMemories` budget (native memories first, Q-141) and one
`graph.maxDocuments` budget across every adapter, and one pass holds at most `RelationshipDiscovery.MaximumRecords`
(5000) records (`RelationshipHost`, the pass budgets and `Bounded`), over every subject together: today Alex's
private volume can push Family stars off Sam's map. With scopes on, SB-P1-05 makes these budgets per scope (each
subject's records counted against its own share), so what one reader sees never depends on how much another scope
holds. The same holds for result limits and candidate windows on every read (6.2).

Built (SB-P1-05): with scopes on (the recorded mode, read at each rebuild), every budget of a pass is shared out among
the catalog's subjects (the people in `people`, the household and, with demo data on, `fixture`) in equal, fixed shares,
the remainder one each to the first (`RelationshipCatalog.Scoped` and `Share`): `graph.maxMemories` and
`graph.maxDocuments` (an adapter's records use up only their own subject's share, `SourcePass.MemoriesOf` and
`DocumentsOf`: a Second Brain personal workspace whose `owner` is a person uses up that person's share, not the team
workspaces'; with the default owner, `household`, both count against the household's), the pass's 5000 records,
the catalog's 500 saved documents and 2000 measured series (each of a subject's measuring providers read against the
subject's share on its own, so one provider's series never push another's data stars off; a provider past it is left
out and noted, and its text records are still no documents), and the similarity links: each pair of scopes a link may
join (one subject's own records, or one subject's and the household's) finds its neighbours and mutual closest among its
own two scopes' records only, with an equal share of the 1000 `copy_of` and 1000 `similar_to` links
(`SimilaritySuggestions`). A URL reference resolves within each subject (`RelationshipDiscovery.UrlTargets`), so whether
one is ambiguous never depends on another scope. A record counts in the scope whose readers alone it can be served to:
a `save_document` copy, read only by readers of its own subject and of its original's (the copy rule), takes the
narrower of the two, so a household copy of Alex's private Drive file (every copy saved before switch-on is a household
one) uses up Alex's shares of the saved documents and the pass and is compared within Alex's pairs, never the
household's; a copy of two people's records is no one's and is left out; an original that a source share lets Family
read counts as the household's (`CatalogReader`'s `CopyBudget`, carried on its star as `DiscoveryRecord.Reader`, D-089).
The pass's 12 000 links are shared out the same way (SB-P1-17, `RelationshipDiscovery.Bounded`): a link counts in the
scope whose readers alone can be served it, the narrower of its two ends' (a person's before the household's, the
household's before the demo's; a copy's end in its reader's scope, as above), and each subject keeps at most its own
share, verified links first, then inferred, then suggested; the rest are left out and the rebuild's notes count them. A
scope past its share therefore loses only some of its own links, and the rebuild completes for every reader. A link
between two people's scopes, which no reader can be served, is left out too.
A share depends only on the settings: a subject outside them (a former person, whom no reader reads) has none, and a household of four subjects keeps a quarter of each budget for Family,
however little the others hold. With scopes off every budget is the install's, as before, and a pass past 12 000 links
fails for every reader (`RelationshipEngine.Refresh`). Still the install's with scopes on: each reader's own 5000 stars
and 12 000 links, which are counted over what that reader is served.

A person's data star is the one star of a person's that Family may read (through a source share), so with scopes on
nothing Family is served of it depends on the person's own records (`CatalogStars.Records`): its topic links to the
household's records are taken newest first up to their own cap, apart from its links to the person's own records; its
revision names its household links only; and it is compared for similarity with its own subject's records only, never
with the household's, whose closest records would be ranked against the person's own too.

A record's revision never names a record of another scope (SB-P1-17). A source whose revisions fold in a record's links
names only links within the record's own subject: the Second Brain adapter takes only links between two memories of one
workspace (`SecondBrainAdapter.LinkLayer`), and with scopes on the parity stand-in names only links to its record's own
subject (`SourcePass.Scoped`). So a Family star's revision, which every Family reader is served with the star, never
moves when a private record it links to is added, changed or removed; the link itself is still found, and served to a
reader of both ends.

**Graph neighbours.** An unreadable star is absent, not redacted: its id, title, kind, revision and links are missing
from every answer. Degree, counts, `truncated` and paging cursors are computed after the reader's filter, so no answer
shows how many neighbours are hidden. An unreadable star and an unknown one get the same answer ("not found", never
"forbidden"). The bridging guard keeps two people's private records unlinked.

**The re-attribution window.** Today a record re-attributed between passes (a Second Brain memory moved to another
workspace) can briefly have two fresh snapshot rows under one graph id, and each read keeps the more recent observation
(`RelationshipEngine.Readable`). With scopes on that leaks: after a memory moves from Family to Alex's private scope,
Sam can't read the new row, so Sam's reads keep serving the old Family row until its freshness lapses (up to
`graph.freshnessSeconds`, 2100 s by default). SB-P1-05 must retire the older row when a pass sees the same graph id
under another subject, or pick the newest observation before the reader's filter, so a narrowing takes effect for every
reader at once. The copy rule already does the second: `copy_readable` (migration 023) judges each provider's rows for
a source id by their newest observation only, so a file moved into Family widens its copies again at the next read.
For a native record the record's own row decides instead (a memory's: provider `memory`, its id as its external id,
no snapshot), whatever the map's star row for it says: a share or un-share changes that row in place and re-stamps
nothing, so a star row the last pass refreshed must not outvote it (D-063).
For an adapter record, "at once" means at the pass that sees the change: until then Rhythm shows what
the source last said.

Built (SB-P1-05), with scopes on, judged at each read (`RelationshipEngine.Served`): of the rows that stand for one star
(provider, account and source id), only the newest observation counts, before the reader's filter and whatever its
status, so the older row is served to no one, also once the newer is deleted at its source. A star is served to its
record's readers: a catalog data star to readers of its person's measurements from that provider (a source share
included), a catalog document star only while the reader reads the catalog row behind it (a `save_document` copy's star
follows the copy rule), and a native memory's star only while the reader reads the memory's own row. With scopes off each
read keeps the newest row it is served, as before. The pass that lists a record under a new subject also reports its
old key missing; that notice is stamped with the pass's start, before any source listed (`CatalogRelationshipSource`),
so the record's new row is the newest observation from that pass on: its new owner is served the star at once, its old
readers are not, and its copies narrow with it.

Built (SB-P3-11), in both modes: every pass lists each native memory as a star from the memory tables (`MemoryStars`,
in the catalog's own read): its excerpt title, its project stars (a project with a member on the map), an `in_project`
link to each and the Drive addresses in its text, never its words, which opening the star reads from `memories` at
that moment through the reader's own predicate. A held, deprecated, no longer true or trashed memory is never listed,
but with `memory.tools` on (SB-P4-11) a memory a live memory of its scope replaced is: after every current one, in the
room the native memories and the adapters leave of `graph.maxMemories` (never counted against them, and never making a
listing incomplete), and on every bounded page (the overview, a dashboard map part, `explore_graph`'s limit) after
every current memory and what they cite, so it never takes a current fact's place.
Each read serves a native star only while its record says so now (`MemoryStars.Served`, with scopes off too): a
memory's while the reader reads its own row and it is not held, deprecated or past its `valid_until` (with
`memory.tools` on, or replaced by a live memory of its scope, then served with `truth {state: replaced, replacedBy}`
judged at that read, and its text opens), a project's while
the reader reads the project, so a hold, a deprecation, an end of validity, the trash, a share or an un-share reaches
every reader at once. A pass whose read of the memory tables fails lists none and reports none missing, so the last
pass's stars stay while they are fresh; a pass whose catalog is past its star budget makes no catalog stars, as before,
and still lists the native memories, which are read from their own tables. Only a subject whose listing was complete
has its stars that are no longer listed reported missing: with scopes on one scope past its share of
`graph.maxMemories` never stops another's (budgets are per scope, 6.2). A `save_document` copy of a native
memory is never a document star of its own (one memory, one star, ledger RHY-33). Combined retrieval's hops start from
a memory's star row too, and a star row they reach answers as the memory's own row (D-152).

**Link types.** Built (SB-P4-01, migration 051; D-030): one vocabulary for both link stores (`Core/LinkTypes.cs`, the
database's `link_types()`, the app's `linkTypes`): Second Brain's eight types (`relates_to`, which has no direction,
`supersedes`, `caused_by`, `decided`, `about_person`, `part_of_project`, `follows`, `drawn_from`) beside Rhythm's own
(`references`, `in_project`, `in_folder`, `about`, `similar_to`, `copy_of`, `contains`), which keep their names and
meaning. A type not listed is its source's own word. `decided` and `follows` join two episodic memories only (the
episodic rule, `LinkTypes.Allows` and `link_kinds_allow()`): an end of no kind, a memory not yet classified or any
record that is no memory, never qualifies. A native memory's star carries its memory's kind for it
(`DiscoveryRecord.MemoryKind`, left out of every other star's snapshot). The map keeps the vocabulary for the links
Rhythm states itself, its own records' references (a native star's, a catalog star's) and its suggestions: a listed
type's direction, and the rule, at every pass, so a reclassified memory's derived link goes at the next pass. A
source's own link (a Second Brain link, a note's) is the source's assertion, kept as stated; Second Brain keeps the same
rule by kinds Rhythm is not sent. Upstream's provenance is a verification (explicit verified, system inferred, the
rest suggested; `LinkTypes.Verification`, and back). Beside its type, verification and evidence a link carries a
confidence from 0 to 1, the revision of its source end it was made from and when it was last checked: the map's in
`relationships` (the strength and revisions in `details`, `checked_at`, 009), a person- or AI-made one in `made_links`
(`confidence`, `source_revision`, `checked_at`, 051), which the trash keeps and a restore, an export and an import carry.
Since SB-P4-02 the `link` tool's writer (052) sets the confidence when the caller gives one, the source end's revision
(`memory:` and its revision) and when it was made. No map answer changes: every link Rhythm states today
(a native star's `in_project` and `references`, a catalog star's `about`, the `similar_to` and `copy_of` suggestions)
keeps its type and direction, none of those types needs kinds, and a native star's kind is never served (`GraphNode`
is built field by field).

**Person- and AI-made links.** Built (SB-P4-02, migration 052; D-067, D-152): `link` writes one into `made_links`, never
into the map's own table, so the rebuild, ingest and 009's invalidation never see it. With `memory.tools` on, every read
of the map takes the active made links beside its own (`RelationshipEngine.ReadMade`, in `Readable`'s transaction,
`RelationshipEngine.MadeLinks`); off, the default, it reads none, so a made link an operator's import brought (043)
changes no map answer: one between two native
memories whose stars the reader is served (`MemoryStars.Served`: the memory's own row through the predicate, not held,
deprecated or past its end), so each is served only to a reader of both ends, from the read after it is made and
through every rebuild, newest first within what the map's own links leave of the 12 000 budget. On the map it has an id
of its own (`made-link:` and the link's id), so it never shares one with a map link of the same ends and type, its
verification as made (`inferred` for an AI client's, `verified` for a person's, D-015), its confidence as the link's
strength, and one piece of evidence that names it and holds no words. A suggestion between two memories' stars (a
similarity the rebuild found) that someone unlinked is dismissed: a row of `memory_link_dismissals` (052) for the two
memories and its type, kept apart from the links since a suggestion may join two scopes (a bridge between a person's
private memory and a Family one) and a dismissal is no link, so 048's un-share check, a restore's and an export's
counts never count one; every read of the map, `connections` and recall's hops leave that suggestion out by it,
whatever later rebuilds write again (`RelationshipEngine.Dismissed`). A made link marked suggested (an import's, or a
link on save's) that someone unlinks is removed and kept the same way, as a dismissal of its type, never as a revoked
link, which a link check would count. A person's link of a dismissal's type made between the two takes it back; since
057 an AI client's is made beside it and takes nothing back. No dismissal writes an audit row (`dismissed_by` keeps
who). It goes when either memory is deleted for good and is not exported. With no made link and no dismissal every
answer is as before. Since run 17 (API-21) a person makes, confirms and removes links too, through a person's
channel: in the app's Memories view (`app_memories` `connections`, `link`, `unlink`: a memory's Links, where an AI
client's link is confirmed by making it again and a suggestion is dismissed) and at the terminal (`rhythm link`,
`rhythm unlink`), through the same `link_memories` and `unlink_memories`, so a person's link is `verified` and made by
the app's login or the person's terminal login, and the author lock, one scope, the episodic rule, the holds and
"never a supersede" decide as for an AI client. A link writes no audit row there either. Since run 17's review both
say who made a link by its verification first: a link of the person's own AI client (a token bound to them) is "made
by alex's AI client, not yet confirmed" at the terminal and "made by your AI client" in the app, never the person's
own, and an automatic one (`suggested`) is "suggested automatically" and is confirmed the same way. Since run 17's
fix review a link an import brought (a made link with no maker, marked `imported`: a Second Brain export's or another
install's) is "imported, not yet confirmed" or "imported, confirmed" in both (`app_memories` `connections` serves
`imported`), never a person's or an AI client's, since its file does not say which; a person's confirmation raises its
verification and leaves it imported, with no maker (057). An
unlink takes every made link of its type between the two memories, either way (052), so the app offers it only when
each of them is the person's to remove and its prompt says so; past 50 links the app says the list is cut.

**Kept dismissals.** Built (SB-P4-05, migration 057; connection idea 1), behind `memory.tools`: Discover's Dismiss is a
person's step through the app's channel (`app_memories` `dismiss`, 7.2; 057's `dismiss_suggestion` refuses an AI
client's channel). The app names a suggestion by its id on its reader's map, and the engine finds it in that reader's
own view (`MapDismissals`, `RelationshipHost.ShownView`), so no one dismisses a link they are not served. A suggestion
of the map's between two native memories' stars is kept as a row of `memory_link_dismissals`, by the two memories, as
`unlink` keeps one; between any other two stars (two documents, a memory and a document, a data star's topic link, a
source's own automatic link, a plugin's) as a row of `map_link_dismissals` (057, a checked table), by the two star rows
and the type, which a share or a delete forever that drops a memory's star rows takes with them. Every read of the map
leaves such a suggestion out (`RelationshipEngine.StarDismissed`), never a confirmed link, whatever later rebuilds
write, and so do recall's hops (since run 16's integration with the same guard: a confirmed link is followed); with no
row, every answer is as before. 057 checks each end itself too, as the map serves it (`map_star_readable`: the subject
whole or through a source share of the provider the star stands for; a save_document copy's star only for the narrower
reader it names; a memory's star only while the memory is current, out of the trash and not held), so its own guard
never rests on the engine's view check alone. A link a person or an AI client made is no
suggestion of the map's: Dismiss on one is a person's `unlink` of its one type (052), which removes it, keeping a
dismissal only for one marked suggested. Who may dismiss: whoever may change either end (section 5); for a star that is
no memory, its subject's owner, any Family reader for the household's, any reader for DEMO. A person sees the
dismissals between two stars on their map (`app_memories` `dismissals`: each pair with its titles as their map names
them, the type, when, by a person or by an AI client's `unlink`, and whether they may take it back) and takes one back
(`undismiss`, 057's `restore_suggestion`, by the same rule, a person's channel only); the suggestion is served again
from the next read, and the answer's `back` says whether the map finds one now: a dismissal kept when a person removed
a suggested made link brings nothing back, since that link is gone. While the map is not ready the three ops answer
`unavailable`. Since run 17 a person lists the same dismissals and takes one back at the terminal too (`rhythm
undismiss`, through their terminal login, over the map as the engine last saved it, never rebuilding it; before the
engine's first start, and while the map is not ready, it says so). With `memory.tools` off nothing writes a dismissal through the app, and Dismiss hides a suggestion
on that screen only.

**A plugin's own links** (SB-P4-13, `graph.pluginLinks`, off by default). Ingest keeps a plugin's links between its
records (`ingest_records`' `links`: one subject and one provider, as suggestions with evidence `ingest:<plugin>`) in
`relationships` with no engine, which only `get_related_resources` served. With the setting on, each pass reads those
between two records it lists as document stars (`CatalogReader.PluginLinks`, at most 20 a record and, since run 16's
integration, at most 2000 a pass, the oldest first, so they never push a pass past its link budget; since the fix
review never one of a type the map gives its own meaning, `CatalogStars.MapOwnLinkTypes`: `similar_to`, `copy_of`,
`supersedes`, `contains`, `in_project`, `in_folder` and `drawn_from`, so none is drawn or worded as Rhythm's own) and
states them as
the two stars' link, the plugin's assertion kept as stated (D-191), so it is a map link like any other: served only to
a reader of both stars, counted in its scope's share of the links, retired by the next pass once the setting is off or
the plugin drops it. A document's pages stay off the map (its star holds their text); the explorer's combined search
shows a page it finds as its document's star (`RecallStars`), through the reader's own predicate on the document's row.
Two scopes never link; a share keeps a memory's made links (their ids, 10.3), each still served only to a reader of
both ends.

**Invalidation and pruning.** Built (SB-P4-04, migration 056; D-015, D-017, D-067), with nothing to do on an install
whose memory tools are off (no tool or view changes a memory's words, and the pass is no job). A change of a native
memory's words (an update, an append or an undo) invalidates in its own transaction what was drawn from them: each
automatic made link at either end (verification `suggested`: one no person or AI client stated, an import's or a link
on save's) goes `stale`, its evidence gone and its checked time now, including one waiting in the trash row of its
other end, so a restore brings it back out of date; and the map's own links at the memory's star rows drawn from the
words (every type but `in_project`, drawn from its project and tags) go stale with their evidence and details gone, as
009 does for a catalog row, so the map, recall's hops and `connections` stop serving them at once and the next rebuild
draws them again from the new words. The star stays (its text is read from the memory at each read; its title is drawn
again at the next rebuild), and its row is written again unchanged, so a rebuild that had saved the star from the old
words before the change writes no link from them (`A_rebuild_that_read_the_old_words_writes_no_link_from_them`); one
that read the old words but saves the star only after the change can still write links drawn from them, served until
the rebuild after it (048's gap). A change of a memory's kind invalidates each automatic made link at it whose type the
episodic rule no longer allows. A link someone stated (a person's, an AI client's, a supersede's of any verification)
is never touched. Every lock this takes is SKIP LOCKED, so a change never waits on a rebuild, a trash or another
change; a row another transaction holds right then (a forget moving a link into the trash, a restore bringing one
back, a rebuild writing its links) is left as that transaction leaves it. The map's next rebuild draws its own links
again; an automatic made link so missed comes back active with an excerpt of the old words, and since run 16's
integration the pruning pass re-checks each active automatic link before it prunes: one with an end whose words changed
after it was last checked (`made_link_words_changed`, from that end's audit trail) or whose type the episodic rule no
longer allows is marked out of date as the change would have marked it (`made_link_missed`,
`The_pass_marks_out_of_date_an_automatic_link_a_change_of_words_missed`). Until the pass reaches it (a night, or a few
on a scope of many links) it is served as it was. The nightly link pruning pass (`MemoryLinkPruning`, after
the staleness pass, only with `memory.tools` on; section 9) deletes an automatic link once 7 days have passed since it
was last checked (or made) and it is weak (a confidence under 0.3) or no longer active, upstream's prune; one with no
confidence stays while it is active, nothing is pruned inside the grace period, and nothing with an end in the trash
(the trash keeps links, so a trip through it and back brings every link back as it was). A pruned link leaves no
dismissal and no audit row. A link someone stated is never deleted by the pruning pass or any other upkeep job: it
goes only with its end, when delete forever or the trash's daily purge removes that memory (038), and the pass's
count of broken links (one count, whatever the reason each is broken) is the only warning before the purge takes one.
While an end is in the trash, marked
wrong or held, or the episodic rule no longer allows its type (`made_link_broken`, the one definition), it is broken:
kept as it is and counted by the pass, so the night summary and the engine's log report it (counts only), while a
person sees the end's state where they look (`connections` names a neighbour marked wrong or held, the trash list
counts the links a forgotten memory keeps). A link whose end is deleted for good goes with it (038): `made_links`' ends
are keys, so upstream's sweep of links with a missing end has nothing to find (`MemoryLinkUpkeepTests`).

**Links on save.** Built (SB-P4-03, migration 062; D-015, D-194, D-200, D-211), adapted from upstream's edge inference
on write, with `memory.tools` on, the embeddings model loaded and `adapters.embeddings.sources` listing `memories`
(`MemoryLinkInference.For`; any one of them off, no store links anything, and the terminal, which loads no model, never
does). After a write through the memory tools or the app's Memories view that saved a memory's words (a new memory, an
append, a change or an undo of its content or kind, a release of its hold), once the write has committed, the engine
gives the memory its passages (the index job's own step, so a save right after finds it), embeds its words alone
(`MemoryLinkInference.Query`) and calls 062's `infer_memory_links`: the memory, still at the text revision it read,
current, out of the trash, not held and the caller's to change, is compared with every memory of its own scope that is
current, out of the trash and not held and has passages of the live model at its current words, never one a person or an
AI client dismissed with it (`memory_link_dismissals`, any type), each by its best passage, and linked to the closest 3
at or above the threshold. A pair linked already, by any active made link, is left as it is; otherwise the type is
`follows` from the new memory when both are episodic, the other was written within 30 minutes before it and it is the
only one of the 3 so (upstream's rule), else `relates_to`. A new link is automatic (verification `suggested`, upstream's
provenance `inferred`, never verified: a person confirms it by making the same link), made by the saving login, with no
evidence, the score as its confidence, the source end's revision and checked now; so `connections` serves it as
suggested with provenance `inferred`, the app and the terminal say "suggested automatically", the map draws it
unconfirmed for a reader of both ends, and recall's hops follow it, all as for any automatic link. A person's or an AI
client's `unlink` removes it and keeps a dismissal, so it is never drawn again; since it is the engine's suggestion,
whoever may change either end may remove it (062 replaces 052's `memory_link_may_remove` for automatic links alone,
section 5), a `follows` onto another member's memory included. An automatic link of a type a save draws that a change of
words left out of date (056) is drawn again instead, whichever end changed, while the two ends' kinds and times still
allow it (`relates_to` either way, `follows` from the later memory, so an edit of a `follows`' earlier end keeps it a
`follows`), keeping its id, its maker and its import flag, so one an import brought survives an edit while it still
holds (D-200); this holds beyond the closest 3, for every memory of the scope still at or above the threshold that such
a link joins, since a memory collects automatic links from other memories' saves; one no longer close enough stays out
of date for the pruning pass. A candidate is checked again under the lock at the words it was scored at, and a link is
checked at the moment it is written, so an edit committing at the same moment is seen or flags it (056). A share or
un-share removes the moving memory's automatic links whose other end is then in another scope (062's trigger on the
share's audit row), and a restore from the trash does not bring back one whose other end moved to another scope while
it was in the trash, out of that trigger's reach (062's trigger on `made_links`, since run 19's fix review: the restore
counts it with the links that did not come back), so one never keeps a memory in Family through 048's refusal of a link
to another person's private record, which a link a person or an AI client made still does. A step that finds the memory moved on writes nothing (a later change runs it again); a failure is
logged by its kind and never fails the write, which has committed. No audit row is written and no revision moves. The
threshold is `EmbeddingThresholds.For`'s link on save: 0.78, Second Brain's, unless `adapters.embeddings.thresholds` is
`tuned` (MiniLM's 0.58, the lowest value above every unrelated pair of memories of one scope in the labelled pairs,
which since run 19 hold every such pair of the corpus scoring 0.43 or more: precision 1.000 there and on the corpus's
own saves; [benchmarks](second-brain-parity/benchmarks.md#thresholds-per-model-sb-p2-13)). Not built: the write's answer
naming the links it drew (`connections` lists them), a link on save at the terminal or by `rhythm import`, a pass
linking memories saved before, and leaving a flagged copy unlinked from what it copies (since run 19 the save's
duplicate check, SB-P5-02, flags it, and a link on save still draws `relates_to` between the two, where upstream draws
none) (`MemoryLinkInferenceTests`,
`MemoryLinkCorpusTests`).

**Un-share and revoke.** When a record leaves Family (or a pass sees it leave), every copy above stops reaching readers
who lost it at their next read: its star, links and their excerpts, passages, the insight and dashboard titles that name
it, pins. What was derived from it narrows with it. Nothing waits for a rebuild. **Today** for a native memory (SB-P3-10,
migration 048) a share or un-share also drops the map's star rows for it, whose titles are excerpts of its words, with
the map's links to them and their evidence, as a delete forever does: so no excerpt of it stays cached under the scope
it left, and until the next pass lists it again under its new subject (the `share` tool asks for one, at most one every
three minutes, else the scheduled pass does) no one's map holds its star, its author's included, nor an insight,
dashboard or pin that cites it (an unreadable star and an absent one answer alike). One gap: a pass that read the memory
before the move and saves after it writes its star row again under the subject it left, excerpt title and all, since
the move cannot see that pass's uncommitted row and nothing locks the two against each other. That row is served to no
one (a native star is judged by the memory's own row, `MemoryStars.Served`), and the pass after it reports it missing,
which takes its title away. The move asks for that next pass unless one was asked for in the three minutes before it,
a rebuild running then counting only by when it was asked for (`RefreshRequests.QueueBounded`), so a racing pass the
scheduler started, or one asked for more than three minutes earlier, has one queued behind it; when one was asked for
within those three minutes the row stays until a later pass, the scheduled one at the latest, up to
`graph.refreshSeconds` later (15 minutes by default). Its passages are its own row's and go with it (`MemoryShareTests`,
`A_share_drops_the_maps_copies_and_a_reader_who_lost_it_is_served_nothing_of_it`).

**Derived records citing a star nobody reads now** (built in 027). An unreadable star and an unknown one answer alike, so
with scopes on an insight, dashboard, pin or corner card citing a star that is off the map for its reader, deleted at
its source or past its freshness included, is not served to that reader, its owner included, until the star is back.
The same holds for a record or star an insight's sources name, and for a log a dashboard reads or writes: a Family
dashboard saved before the switch whose log is Alex's (a log's default then was the first person's) is served to
Alex's readers alone from the switch on, and the household view and its screens no longer show it.
Telling the two apart would tell a reader that a star it can't read still exists. A dashboard so hidden can't be
changed through any tool meanwhile; it comes back with its star.

**Arranging never narrows.** A dashboard's layout counts its map parts' focus stars among the stars it names, so a
refocus onto a narrower star would hide the dashboard from its other readers. With scopes on, `DashboardStore.SaveLayout`
passes the subject of each focus star (`p_cited`, as `derived_subject` takes them) and `save_dashboard_layout` (027)
refuses a layout whose focus stars would land the dashboard narrower than it is, whoever saves it, composing a new map
part as well as arranging (the dashboard's author and an admin included): a Family dashboard's map focuses on Family's
stars, DEMO ones and those a source share lets Family read, never on a person's private star. Called without the
subjects (an engine that passes none), the function keeps the earlier rule: a saver bound to a person focuses a Family
dashboard's maps only on stars the dashboard names already. A saver with no person (the household view's screens) reads
only what Family reads and refocuses freely either way. Agreed with the screens program.

**Pins** are counted, for the limit of 24, over the pins the pinner is served, each star once, and unpinning removes
only pins of a star the pinner reads now; a reader is served at most the 24 oldest.

## 9. Exports, backups and jobs

- **Backups** (`rhythm backup`, [../backups.md](../backups.md)) hold the whole database, every scope, encrypted with
  the backup password: scopes decide what principals read, not what the operator holds. A restore keeps subjects,
  scopes, memberships, bindings, source shares and the recorded mode, and disables every token as today (SB-P1-13).
  **Today** `MemoryRestoreTests` restores a real `pg_dump` into a throwaway database and finds every table as it was
  but for what the quarantine changes on purpose: every token disabled (viewer credentials and person-bound tokens
  included; only the shared app token comes back, from its file), grants removed, map snapshot rows blanked with their
  passages and the evidence of links found to them until the next rebuild, unfinished sync jobs held for review, and a
  memory in the trash given the trash's 14 days again from the restore, so the first daily purge never deletes what the
  restore brought back (since run 10's integration). 025 keeps a trash row's retention within 365 days of its date, so
  one trashed more than 351 days before the restore is dated 351 days before it (since run 10's final review); when it
  was trashed stays on its `trashed` audit row. Every derived record keeps its subject and scope. A restored
  database that records `on` refuses a start with `off`, a start with `on` adds nobody to Family, and new tokens for
  the same people read what the old ones read.
- **Exports.** **Today** (SB-P3-14, D-035; migration 043, `engine/src/Cli/MemoryArchive.cs`; [../../cli/README.md](../../cli/README.md),
  Export and import) `rhythm export` and `rhythm import` are the operator's, on the host, for native memories in
  Rhythm's own format (`rhythm-memories`, version 1: a JSON file of one record a line). An export is what one reader
  reads, not the whole install (that is a backup): `--person <id>` reads as a client bound to that person, without it
  the household view, each through the one predicate: every memory by `Database.Visible` on its own `resources` row,
  its earlier versions from that reader's history floor on (034), and of the trash what its trash list shows that
  reader (who may restore it, section 5), with the projects it reads and the person- and AI-made links between two of
  those memories. It does so as a principal made for it inside its own transaction, which is rolled back, so no token
  for it ever exists and no row of it stays. So with scopes on a person's export holds their private records and, as
  a member, Family's, never another person's private memory nor the private states a Family memory had before it came
  into Family; the household view's holds Family's only; with scopes off every memory is the household's, and so is
  every export (`MemoryArchiveTests`, SB-P1-10's export case, D-130). The file carries each memory's id, subject and
  scope, author, source, text, tags, kind, status, importance, volatility, validity, project, capsule slot, hold,
  revision and dates, its versions and its trash dates, and for a digest (054; since run 16's fix review) its `digest`
  object, the key, basis and source count its audit row names, which no other memory's line carries, and for a
  commitment (058, SB-P5-09) its `commitment` object, its direction and counterparty slug, which no other memory's line
  carries either (an import adds it only to the memory it has just added, `import_memory_commitment`), and for a
  decision (060, SB-P5-11) its `decision` object, its confidence and source and its states (outcome, re-asks and review
  date) from the reader's history floor on, the state at the floor written at the floor (an import adds them only to
  the memory it has just added, at the revisions it keeps, `import_memory_decision`), and for a memory with a due date
  (061, SB-P5-10) its `due` object, the moment in UTC, its kind and source (an import puts it only on the memory it has
  just added, `import_memory_due`), and for a standing instruction (065, SB-P5-12) `standing` true (an import marks only
  the memory it has just added, `import_memory_standing`, never a decision, a commitment, a task or a dated memory, and a
  line over 500 characters stays unmarked, refused); each
  project's id,
  scope, slug, name, description, status,
  author, aliases, revision and dates, and for a project a held write made that no person has let in yet (046) its
  hold, `held` and `heldFor` (the memory it was held for; SB-P3-23), which a project not held carries neither of, so its
  line is as before; each link's id, ends, type, verification, evidence and lifecycle, and its confidence, source
  revision and checked time only when it has them (051), so a link with none is the line it was. It never
  carries the audit trail, the logins that wrote anything (principals are an install's own), idempotency keys,
  passages, vectors, stars or the links the engine finds (all rebuilt), nor any other catalog record.
- **Imports.** `rhythm import <file>` names its reader and the scopes it writes: `--person <id>` that person's private
  scope, `--family` Family (as that person, or as the household view). It writes only through 043's checked functions,
  as the operator (no principal, channel `cli`, as delete forever's operator path; only the terminal calls them,
  `EngineLoginTests`), and adds only what that reader may write there, by the rule the record is changed by once it is
  here (`memory_import_may_write`, which a test keeps equal to `memory_may_change` for a login bound to that reader):
  never another person's private record, no private record while scopes are off (D-048), a Family record a person
  wrote only for that person or a Family admin, one no person wrote for any reader of Family; a record of a scope the
  import does not name is left out and counted. It keeps each record's id (10.3), subject, author, dates and revision;
  earlier versions come in from where the reader's history starts, so a state in a scope the reader does not read (with
  scopes off, any person's) never lands. It never overwrites, moves or deletes a row: a memory, project or link whose id
  is here stays as it is, so does a memory this install deleted for good (its `purged` audit row) and anything the
  origin map (`memory_origins`: each imported record's origin install, its id there and here, its subject and the
  export it came in, ids only) says was imported before; a repeated import adds nothing. A project of the same slug in
  that scope here takes the memories that name it and is left as it is. Every memory is screened by 036 as any write
  is: a held one stays held, and one a person released elsewhere is held again here if its words read like instructions
  (a release vouches for its own install). Its earlier versions are screened the same way: a state released elsewhere
  is held here when its words would be, so no undo here, by an AI client or anyone, brings them back unheld, and no
  version read or history serves them. A project the export carries as held comes in held, for the memory it was held
  for, so it is out of every list here as it was there, and only a person's release of a memory in it (or a person's
  write into it from the terminal) lists it, on either install; a project not held, or one from an export made before
  exports carried the hold, lists as before, its held memory's included (D-178; `MemoryArchiveHeldProjectTests`). One in the trash keeps its trash dates, and one past them is left out. A
  link it brings has no maker here (`made_links.made_by` empty and `imported` set; every other link keeps its maker), and
  one a trashed memory's trash row kept goes back into that memory's trash row and comes back with it. Each memory and
  project it adds has one `imported` audit row, as the operator's; the origin's audit trail is not carried. `--dry-run`
  writes nothing.
  Neither command answers over MCP or in the app: any export added there reads through the predicate like any other
  read. The app's document download is a read and takes the predicate (7.2).
- **Second Brain imports.** **Today** (SB-P8-01, run 17; migration 059, `engine/src/Cli/SecondBrainImport.cs`;
  [../../cli/README.md](../../cli/README.md), Importing a Second Brain export) `rhythm import-second-brain <file> --person
  <id> [--scope-map <file>] [--write]` is the operator's, on the host, only with `memory.secondBrainImport` on (off by
  default; off, it answers as a command the engine does not know). It reads one file a Second Brain service exported
  (upstream's `GET /export`, version 3 and older), never the service, and writes only through 059's checked functions, as
  the operator (no principal, channel `cli`, as 043's import); a dry run, the default, does all of it in one transaction
  and rolls it back, so its report is the one `--write` gives. The export holds one member's readable set and says
  neither which workspace a memory was in nor who wrote it, so each memory lands in the importing person's private scope
  unless the owner's reviewed scope map names it for Family (D-049), with that person as its author; where it lands is
  checked by 043's `memory_import_refusal`, so no private memory lands while scopes are off (D-048) and Family only for a
  member. Since SB-P8-08 (D-247, migration 074) `--household`, only with scopes off (refused with them on), lands every
  memory and project in Family instead, the person its author, and keeps where each memory lived in Second Brain
  (`personal`, `team` or `unknown`: from the Second Brain connector's rows, read only, else the scope map) in its origin
  map row (`memory_origins.workspace`), which nothing reads yet. Each record gets a new native id (10.3; D-124 for an
  id upstream would not take) and its Second Brain id goes
  into the origin map under `second-brain:default` (a project by its slug and creation time, D-123; a link, which has no
  id in the export, by its type and ends, hashed): a record the origin map holds is never written again, so a repeated
  import, or another member's file holding the same Family memories, adds nothing, and nothing is matched by title. A
  memory keeps its text, its ordinary tags, its kind, status, volatility, importance, validity and dates, a capsule slot
  of Rhythm's (never for a held one: since run 17's review one 036 holds as its row goes in leaves the slot, its capsule
  tags kept beside it), and its project when exactly one project of its scope answers to its tag (a slug held
  twice is two projects and joins neither, D-123; since the review two projects of one file are never joined by a name,
  an alias included, since 059 joins only a project that was here before this export). A project is screened as an AI
  client's new project is (D-037, 045): one whose slug, name, description or an alias reads like instructions comes in
  held (046), and a memory going into a held project is held with it (`held_project`); a held project here is joined by
  nothing (refused `held_project`). Aliases Rhythm does not take are left out and counted; the tags in a namespace the engine sets itself are kept beside it in
  `memory_upstream_tags` (D-121), which only Home's old memory reads (070, section 7.2: what upstream's own jobs wrote is not
  picked) and `rhythm export` does not carry; since the integration of run
  17 upstream's `owed-to-me` and `counterparty:` tags also make the memory's commitment (058: owed to the person, else
  owed by them to the one the tag names, a name that reads like instructions kept as none), so an imported open loop
  lists the way upstream counted it. A `quarantine:` tag holds it, any reason (D-120; reasons
  Rhythm has no hold of its own for as `instruction`), and 036 screens every memory as any write, so one upstream served
  is held when its words read like instructions (D-037). A link comes in only between two memories it brought into one
  scope (D-122: an end the file does not hold is skipped and counted, its foreign id kept nowhere), as an imported made
  link with no maker, verified, inferred or suggested by upstream's provenance and its weight as its confidence, checked
  at the import so the link pruning pass's seven days count from there. A record another import put in a private scope
  this file does not mean (another member's, or one made without the map) is counted where this file meant it
  (`imported_before_elsewhere`), never under the other scope's name, and listed for review; since run 17's fix review
  so is a link with such a memory at either end (`skipped:end_elsewhere`), which is not imported (059 would refuse it
  as a scope the import does not name). Each
  memory has one `imported` audit row saying earlier changes were not recorded, and each project one `imported` row: the export carries no
  versions, audit or trash, and the report names each field it lacks. The report counts, per scope, what was added,
  imported before, held (by reason) and skipped (by reason), never a record's words (`SecondBrainImportTests`, story 16);
  since run 17's fix review its notes and review lines count only what the run adds, so a repeated import, or another
  member's file of the same Family memories, notes nothing of them again, and it counts the Family memories it adds
  with the importing person as author (Q-160; the file does not say which another member wrote).
  The comparison harness (SB-P8-03, `SecondBrainComparisonTests`) holds both sides of the synthetic brain in one install
  and reports, by the origin map, what each serves, owns, groups and links, who may view and edit what, capsules,
  retention and recall ([second-brain-parity/comparison.md](second-brain-parity/comparison.md)).
- **Jobs.** The engine's own jobs run as system principals and read everything. They answer no reader, and what they
  write follows its inputs. The daily insight is not one of them: it is an AI client, and reads as its token does.

| Job | Writes | Scope of what it writes |
|---|---|---|
| the map rebuild (`RelationshipHost.Refresh`, principal `graph`) | snapshot rows, catalog stars, links | each star its record's subject; links only between records the bridging guard allows |
| the embeddings index and backfill (SB-P2-04, SB-P2-05) | passages, from a row's own text, a memory's, or the text an adapter reads on request (`ISearchableSource`), never a reference-only provider's | the parent row's |
| titles (`Adapters/Titles`, a local model) | a star's title, from its text | the star's own row |
| imports (the documents folder, CSV) and adapter syncs | catalog rows, measurements | the adapter's settings (10.2) |
| insight candidates (`InsightCandidates`, a nightly job only with `memory.tools` on, after the link pruning pass, SB-P5-05; migration 064) | candidate pairs in `memory_insight_candidates`: an older and a newer memory of one subject, 30 or more days apart, both eligible, through `memory_insight_accrue`, which checks each pair again | each scope walked from its own cursor (`job_cursors`, scope its subject), a bounded piece at a time, each piece and its place in one transaction, a scope with no eligible memory after its place at its end (its place cleared); a pair's two memories are of one subject, which the pair takes, so the same people read both; served only while both stay there (section 5); the vectors it compares are made for the run and kept nowhere; kinds and counts only in the summary and the log |
| the trash purge (`MemoryTrash.Purge`, daily, SB-P3-06; migration 038) | removals: native memories past their trash retention, idempotency keys older than 30 days | it removes what its retention passed, whatever the scope, and answers no reader; each memory's `purged` audit row is in that memory's own subject, with none of its words, as the job `memory-trash` (channel `system`, no principal) |
| the recall log's prune (`RecallLog.Prune`, daily, SB-P2-11; migration 042) | removals: log rows and counts older than 30 days | removal only; each row and count was kept in its reader's own scope when it was written |
| an offboarding's close (`Offboardings.Daily`, daily right after the nightly job runner and so after its trash purge, outside the runner: no budget, cursor or night summary row of its own, one small transaction for each offboarding under way and with none one query, `NightlyJobs.cs`; SB-P7-13, migration 072) | removals: the private memory projects of a person the operator offboarded that no memory is in any more; the offboarding recorded finished | only the offboarded person's own private scope, only while they are not current and were not brought back since the operator last ran it (`returned_at`); it starts no offboarding and moves nothing to the trash; each project's `deleted` audit row is in that subject, with no words, as the job `offboarding` (channel `system`, no principal) |
| the staleness pass (`MemoryStaleness`, a nightly job only with `memory.tools` on, SB-P4-09; migration 053) | marks in `memory_checks`: a memory whose volatility's time has passed, at its revision; a mark taken off once it no longer holds | each scope walked from its own cursor (`job_cursors`, scope its subject), a bounded piece at a time, each piece and its place in one transaction; a mark is no change of the memory (no revision, no audit row) and is served only to a reader of that memory who may change it (the stale queue, section 5); nothing is written for a memory that is not due |
| the link pruning pass (`MemoryLinkPruning`, a nightly job only with `memory.tools` on, after the staleness pass, SB-P4-04; migration 056) | removals: automatic made links (`suggested`) past their 7-day grace period that are weak (a confidence under 0.3) or no longer active; a count of the broken links someone stated, which it keeps | each scope walked from its own cursor (`job_cursors`, scope its subject), a bounded piece at a time, each piece and its place in one transaction; a link's scope is its ends' (one scope); never a link with an end in the trash, never a link someone stated; kinds and counts only in the summary and the log |
| digests (SB-P5-13; migration 054) | none: no job writes one (the engine runs no model, D-033); a client's own schedule lists and saves through the memory tools | a digest is one scope's, drawn only from memories of that scope its writer reads, and lands there; a repeated run writes no second one (section 5) |
| the decision ledger (SB-P5-11; migration 060) | none: no job reads or writes a decision; a due decision is listed when a reader asks, and calibration is reckoned at each read | nothing to scope: each reader's ledger and calibration are its own (section 5) |
| open loops (SB-P5-09; migration 058) | none: no job reads or writes a loop; the queue is read when a reader asks for it, and a loop closes only by a resolve | nothing to scope: each reader's queue is its own (section 5) |
| due dates (SB-P5-10; migration 061) | none: no job reads memories for dates or moves one (no model, D-033); a date is written with its memory or by a snooze or clear, and the list is read when a reader asks for it | nothing to scope: each reader's list is its own (section 5) |
| standing instructions (SB-P5-12; migration 065) | none: no job reads or writes one; what fires is read at each question, and a vector is kept only in the process | nothing to scope: what a question fires is what its asker reads (section 5) |
| the daily insight: an outside AI client (`harness/chatgpt/daily-insight.md`; since SB-P5-06 it reads the insight candidates and records what it declines when their tools are listed, and every insight it saves is proposed) | insights and runs | what its token reads: with scopes on, one bound to `alex` sees Alex's private records and Family, one with no person Family only; each insight lands in the narrowest scope it cites |

Job summaries and logs keep kinds and counts only, as today.

## 10. Ownership: native records and read-only adapters

### 10.1 Who owns what

| Kind | Examples | System of record | What Rhythm keeps | Does Rhythm write the original? |
|---|---|---|---|---|
| native | native memories with their versions, audit, trash, projects and capsules (phase 3); person- and AI-made links (phase 4); insights and runs; dashboards with their versions, writes, corners and pins; dashboard log entries; `save_document` copies | Rhythm | the record, written only through role- and scope-checked database functions (the `save_document()` pattern, 017:356-402) | it is the original |
| archived original (`AuthorizedArchive`, `Core/ProviderPolicies.cs`) | the documents folder and app uploads, CSV imports, the demo, a plugin's archive provider | the file a person (or the demo's seed) gave Rhythm | a content-addressed copy of the file, its text and measurements | never: a changed file is a new revision, a removed one is marked deleted |
| live source (`LiveSource`) | Drive, Nightscout, a plugin's live feed | the provider | Drive: a snapshot row with name, revision, parents, links and a vector, and no text; with `drive` in `adapters.embeddings.sources` (never by default), passages of a readable file's text at that revision (Q-097, closed); Nightscout: measurements. Revisions are checked at read time (`Retrieval.Validate`, `SemanticSearch` for passages, both through `SourceValidation`) | only a dashboard's declared Sheets and Docs writes (`IDashboardWriter`, `Adapters/Drive/DriveWrites.cs`), after a person turns on Allow edits, with before and after kept and Undo |
| reference (`ReferenceOnly`) | Second Brain; a calendar (`calendar`: a title and overview, its description read on open) | the Second Brain service; the calendar's own service | an excerpt title, links and a vector; text read on open and never kept (`SecondBrainAdapter.cs:291-313`); a reference provider may not store content (`ProviderPolicies.Validate`) | never |
| read-only folder | notes | the Markdown files | a snapshot row; the file is read again on open; with `notes` in `adapters.embeddings.sources` (the example settings list it), passages of its text at that revision | never |
| engine-derived | map and catalog stars, similarity links, passages, payload projections, titles | none: rebuilt from the kinds above | section 8 | n/a |

- No adapter becomes a way to write to its source: nothing in this program writes to Drive, the notes folder or Second
  Brain beyond the dashboard writes that exist today.
- A native memory's own `resources` row carries none of its words (migration 033): the title `Memory`, no content, the
  domain `memory`. A catalog read that finds one learns only that a memory exists, and only a reader of its subject does.
  The catalog's tools (`search_knowledge`, `search_resources`, `get_related_resources`) never serve a native memory's
  rows, its own or its map star's (provider `memory`), and leave them out before their limit, so they never take a
  result's place or weigh in the ranking (`Retrieval`, `The_catalog_search_tools_leave_native_star_rows_out_before_their_limit`;
  `search_knowledge`'s combined mode likewise, `A_recalled_memory_carries_its_own_revision_and_search_knowledge_never_serves_it`);
  memories are served by the memory store, combined retrieval and the map (SB-P3-09, SB-P3-11). A memory's
  passages are made from its text at its text revision (`text_revision`, which only new text moves), so a change of its
  status, tags or project never puts them out of date (D-149).
- A native record is never kept in a snapshot row. A restore's quarantine blanks every snapshot row
  (`engine/backup/restore-quarantine.sql`) and `Database.Revoke` blanks content down `resource_dependencies`, so
  native memory text lives in its own tables, and its `resources` row and passages are derived from them (SB-P1-07,
  ledger RHY-18). A memory's own `resources` row never holds a map snapshot either: the map keeps a memory's star in a
  snapshot row of its own (provider `memory`, the map's `relationship-engine:` external id), which a restore blanks
  and the next rebuild fills, while the quarantine leaves the memory's own row as it was (migration 025).

### 10.2 Which scope each source's rows land in

| Source | Provider | Subject | With scopes on, read by |
|---|---|---|---|
| notes | `notes` | `adapters.notes.owner` (default `household`) | Family, or that one person |
| the documents folder, app uploads | `documents` (pages and sections as their document) | `adapters.documents.owner` (default `household`) | Family, or that one person |
| `save_document` | `documents` | the call's `subject`, within section 5's rule | Family, or the caller's person |
| Drive | `drive` | `adapters.drive.owner` | Family, or that one person. Who may open the file in Google is Google's to say; Rhythm's scope decides only who sees it in Rhythm |
| Second Brain, the personal workspace | `second-brain` | `adapters.second-brain.owner` (default `household`) | that person; or Family when the owner is `household`, as today ([../adapters/second-brain.md](../adapters/second-brain.md)) |
| Second Brain, each workspace in `teams` | `second-brain` | `household` | Family: every member, whether or not they are on that team in Second Brain |
| a calendar | `calendar` | `adapters.calendar.owner` (default the first person) | that person, plus Family under a source share; or Family when the owner is `household` |
| CSV imports | `csv` | the row's `subject` column, else `adapters.csv.owner` | that person, plus Family under a source share; Family for the household's rows |
| Garmin, Nightscout | `garmin`, `nightscout` | the adapter's `owner` | that person, plus Family under a source share |
| plugins (CareLink and others) | the plugin's providers | the subjects `plugins.<name>` allows | that person, plus Family under a source share |
| dashboard log entries | `log` | the subject the write names | that person, or Family |
| the demo | `demo` and its catalog stars | `fixture` | everyone, labelled DEMO |
| catalog stars | `rhythm` (`RHYTHM_CATALOG_PROVIDER`) | their record's | their record's readers |
| native memories | `memory`, a core provider of their own (SB-P1-07, migration 025) | the owner, or `household` once shared | the owner, or Family |

### 10.3 Stable ids

| Thing | Id | Stays the same when |
|---|---|---|
| a catalog row | `resources.id`, unique by `(subject_id, provider, external_id)` (001) | the row is updated in place. An adapter record that changes subject (a Second Brain memory moved to another workspace) gets a new row |
| a star | its graph id, `provider:account:externalId` with each part escaped (`ResourceKey.GraphId`, `Core/RelationshipDiscovery.cs`), with no subject in it | its record changes scope: a moved Second Brain memory keeps its star. Catalog document and data stars carry the subject in their external id (`RelationshipCatalog.cs:294,315`), so theirs change with it |
| a native memory | a uuid Rhythm makes at capture and never reuses; it is its `resources` row's external id, so the graph id comes from the memory id alone | edits, status changes, share and un-share (the row's subject changes in place, keeping its row id and graph id), trash and restore, export and import |
| a Second Brain memory | the service's uuid, under the star account `RHYTHM_SECOND_BRAIN_STAR_ACCOUNT` (default the provider) | the service keeps it; no native record takes it over (10.4) |
| a Drive file | Drive's file id, with Drive's revision | Drive keeps it; a new revision keeps the id |
| a calendar's occurrence | a hash of the event's UID and, in a series, of when this occurrence was due (`IcsFeed.Id`) | its name, place or time change, an occurrence is moved, or its link is reset; two calendars holding one event share it |
| a note | its path in the notes folder | the file stays put: a moved or renamed note is a new star |
| a link the engine finds | a hash of both ends' subjects and graph ids and the type (`DiscoveredLink.Id`) | both ends' subjects and ids; after a share the rebuild finds it again under a new id |
| a link a person or an AI makes (SB-P4-02) | its own id (`made_links.link_id`, migration 025), not derived from the ends' subjects; such links have a table of their own, beside the map's `relationships` | a share or un-share of either end |
| a dashboard, an insight | a uuid | every version |
| a memory, project or link from `rhythm import` (Rhythm's own format) | its id in the export, kept; its origin in `memory_origins` (043) | a repeated import, which adds nothing (D-035) |
| an imported memory (SB-P8-01) | **today** (run 17, migration 059): a new native uuid, with its Second Brain id in the origin map (`memory_origins`, origin `second-brain:default`; an id outside the map's alphabet as its SHA-256, D-124) | a repeated import, which adds nothing (D-035), and another member's file holding the same Family memories |

### 10.4 Lineage: memories, Drive originals, copies, dashboards and Second Brain references

A memory, a Drive original and a stored copy are three identities, joined by links that say which came from which; a
stored copy never stands in for the current original (D-014).

| From | To | Today | With this program |
|---|---|---|---|
| a page or section | its document | `contains` (no engine tag, `DocumentsAdapter.cs:183-187`); revoking the document revokes its pages (`resource_dependencies`, 003:23-32) | unchanged |
| a `save_document` copy | the Drive file it copies | the copy's external id (a legacy copy's `sourceFileId`) is the file id, and the Drive star stands for the copy on the map (`RelationshipCatalog.cs:313-314`) | unchanged; with scopes on the copy lands no wider than the file (section 5) and is served only to the file's current readers (section 8) |
| a Second Brain memory | a Drive file its text names | `references`, verified, found by the rebuild | unchanged; served to readers of both |
| a native memory | a record it cites (a Drive file, a document, another memory) | **today** for a Drive file (SB-P4-10, migration 055, behind `memory.tools`): a citation (`memory_citations`) made with `cite` once the memory is saved: the file's id under the map's star account, the subject and scope the map held it under, and the revision, modified time, name and folders a check of its metadata at Drive found then, with when; none of its words. `get` checks each citation at Drive again (metadata only, four at a time within five seconds), stores nothing, and says whether the document is as cited, changed since (edited, moved, renamed, revised), in the trash, gone, unreadable, unavailable (no longer found by the Drive sync), or could not be checked; `revalidate` records what a check found, moving the citation to the current revision and keeping the one it moved from, and lists every move: never silently. Whoever may change the memory cites and revalidates (the first row of section 5), never for a held memory; with scopes on a memory cites only a document of its own subject or Family's, and a citation is served only to a reader of the memory who reads the document under its newest row (none while the map holds no row of it: since run 16's integration it fails closed). A document the map no longer holds as current is not checked at Drive. Since run 16's fix review one whose newest row is missing (the Drive sync's complete listing of the folders Rhythm is given no longer finds it: moved out, put in the trash, deleted or no longer shared, all alike, since the folders are listed without the trash) is `unavailable`, said plainly with since when, and `revalidate` records that state, its revision, name and folders kept (055 records it only while the map's row is missing); before, it was served unchecked with the wrong reason, so story 7's plain states held only until the next sync. One the map holds back since a restore (revoked until the next rebuild) is unchecked, saying why, and `revalidate` records nothing of it. A document past the document budget keeps its last row, current, and is checked as before. A 403 or 429 for one of Drive's usage limits is unchecked, never unreadable. Documents, other memories and other live sources: not yet | a citation link written with the memory: the cited record's id and revision and when it was checked. Reading the memory says whether the original changed since or is unavailable, and a cached excerpt never answers for it (SB-P4-10, story 7). Links its author makes follow the same-scope rule (section 5) |
| a dashboard | the stars and feeds it reads | star ids in each version; feeds read live for each reader | a subject and scope (SB-P1-06); its page counts as citing every star it names |
| an insight | the stars and sources it cites | star ids and titles and sources, stored in the insight (011) | a subject and scope, narrowing with what it cites |
| an imported memory | its Second Brain original | **today** (SB-P8-01, run 17): an origin map entry, the Second Brain id, the export it came in (by the file's checksum) and when it was taken; nothing links the native star to the adapter's star of the same memory yet | an origin map entry: the Second Brain id, the export it came from and when (SB-P8-01) |

Three different things are called a snapshot, and none is a source of truth:

- a **map snapshot row** is the engine's cache of what a source listed at a pass (section 8): a restore blanks it, the
  next rebuild fills it again;
- a **dated copy** (`save_document`, `retrievalMode: dated-snapshot`) is a native record of what a source said when an
  AI client saved it, with its own id, subject, scope and date, and the original's id as its external id; its own
  subject never widens who reads it (section 8: no wider than the original's current subject). Search marks
  it stale a day after it was saved (the `Columns` of `Retrieval`); it never replaces the original's current text;
- a **backup snapshot** is the operator's (section 9).

**Second Brain references and native memories, side by side.** The Second Brain adapter keeps reading, read-only and
unchanged, beside native memories (SB-P3-15, D-047): different providers and ids, never merged by title. A native
memory and the adapter's star for the same text are two records until an import joins them. **Today**
(`SecondBrainSideBySideTests`): with native memories of the same words as the service's on the map, and native writes
and an undo made meanwhile, each reader's view of the adapter's stars and their links is as it was without them, with
scopes off and on (each with an update and an undo); a comparison check (a test's, never the map's) pairs each adapter
memory star with its native twin by title, two stars with their own providers, ids and owners, each text read from its
own source (the native one from the memory tables with no call to the service); every request the adapter makes is a
GET, and no native write calls the service. The importer gives each imported memory a new native id, puts it in the
importing person's private scope unless the owner's reviewed scope map says Family (D-049), and keeps the Second Brain
id in its origin map. From then on the native star stands for the
adapter's, as a Drive star stands for its saved copy, so one memory is one star (ledger RHY-33). A Second Brain memory
is edited in Second Brain; Rhythm writes nothing there (SB-P8-07 needs its own approval).

## 11. Examples (DEMO)

**Story 1, a private fact stays private.** Scopes are on. Sam's client, bound to `sam`, saves `mem-sam-party` (a
surprise party for Alex) without naming a scope, so it is private to Sam.

- Alex's client gets nothing about it from `search_knowledge`, `semantic_search`, `explore_graph`,
  `get_relationship_graph`, `dashboard_data` or `list_insights`: no star, link, excerpt, passage or neighbour count.
  `get_resource` with its id answers as for an unknown id.
- The app with no viewer session (the household view) has no star for it: it is not Family.
- An insight Sam's client saves citing it is private to Sam; so is a dashboard that shows it.
- Sam's client sees it everywhere. The rebuild may find a similarity link from it to a Family record; only Sam is
  served that link.

**Story 2, one memory shared exactly.** Alex shares `mem-alex-cafe` into Family.

- Sam now reads it, with its author (Alex) and any links it has within Family; Alex's other private memories, and any
  link from it to them, stay hidden from Sam.
- Sam can't edit, trash or un-share it; an admin can. When Alex un-shares it, it goes back to Alex's private scope and
  Sam's next read finds nothing: no star, excerpt title or passage (section 8).
- A client with no person sees it while it is shared, and not after.
- **Built** (SB-P3-10, migration 048, behind `memory.share`, off by default): `MemoryShareTests`
  (`One_memory_is_shared_exactly_and_its_author_takes_it_back`, with the admin, project, held and map cases beside it)
  and, through the `share` tool as AI clients, `MemoryShareToolDatabaseTests`.

## 12. What each phase 1 item takes from this file

| Item | Sections |
|---|---|
| SB-P1-02 migration: principals bound to people, memberships and roles, plus source shares and the recorded mode | 2, 3, 4, 6.2 |
| SB-P1-03 one read predicate | 2, 7.1, 7.2, 8 (`save_document` copies served by their original's current subject) |
| SB-P1-04 the reads that skip it | 7.2 to 7.4 (five reads and the `log` feed) |
| SB-P1-05 the map filtered per reader | 6.2 (statistics and budgets), 7.1, 7.2, 8 (the re-attribution window, budgets per scope) |
| SB-P1-06 a subject and scope on derived records | 5 (where writes land), 8 (insight run notes too) |
| SB-P1-07 native memory tables | 10.1, 10.3 |
| SB-P1-08 the ownership contract, with [../adapters/second-brain.md](../adapters/second-brain.md) | 10 |
| SB-P1-09 settings and safe defaults | 5 (where a `save_document` copy lands), 6 |
| SB-P1-10 authorisation tests | 5, 7, 8, 11 |
| SB-P1-11 an opt-in viewer identity in the app | 3 (a person-bound, revocable credential per session; the app token alone never names a person), 5, 7.5 |
| SB-P1-12 person-bound tokens | 3, 4 |
| SB-P1-13 backup and restore | 6.2, 9 |
| SB-P1-15 screens and private stars | 7.5 |
| SB-P1-16 `docs/security.md` | all; what scopes protect and what they don't (section 8's AI clients, section 9's backups) |
| SB-P1-19 a screen's id bound to its first login | 7.5 |

## Related

- [../security.md](../security.md): today's model, which scopes change only when switched on.
- [second-brain-parity/](second-brain-parity/README.md): the program, its ledger (SEC, DAT and RHY rows), decisions and
  backlog.
- [../adapters/second-brain.md](../adapters/second-brain.md): the Second Brain adapter, and where its memories land.
- [screens.md](screens.md): the screens contract, whose `control_screen` section 7.5 asks to respect private stars.
- the open-questions log: Q-006 (no built-in login), Q-008 (Second Brain as an adapter), Q-152 and
  Q-153 (scopes and a viewer identity on a real install).
