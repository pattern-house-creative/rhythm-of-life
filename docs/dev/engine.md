# The engine, for integrators

What the app, the CLI and deploy work need to know about `engine/`. The contract is `contracts/README.md`; this page
says how the engine implements it and where it goes beyond it.

## Services and start order

`deploy/compose/engine.compose.yaml`:

| Service | Image | Does | Networks |
|---|---|---|---|
| `postgres` | `pgvector/pgvector:0.8.7-pg17-bookworm` (`RHYTHM_POSTGRES_IMAGE`) | the database | internal |
| `migrate` | `rhythm-engine` | one shot: migrations, then the engine's database login | internal |
| `engine` | `rhythm-engine` | MCP at `/mcp`, `GET /health`, the scheduler | internal, egress |
| `backup` (profile `backup`) | `rhythm-backup` | pg_dump + archive into restic, on demand | internal |
| `restore` (profile `restore`) | `rhythm-backup` | restores a snapshot and loads it into a new database (`rhythm restore`) | internal |
| `cli` (profile `cli`) | `rhythm-engine` | the `rhythm` commands: `init`, `connect`, `doctor`, `token`, `preflight` and `ready` around `up`, and `refresh`, `check-config`, `migrate` (`cli/README.md`); runs as root in the deploy folder | internal |
| `titles` (profile `titles`) | `ollama/ollama` (`RHYTHM_OLLAMA_IMAGE`) | the titles adapter's model server, no published port | internal |
| `titles-pull` (profile `titles-pull`) | the same | one shot: `run --rm titles-pull <model>` downloads a model into the server's volume | egress |

The image builds from the repository root: `docker build -f engine/Dockerfile .` (compose does this). `engine` waits
for `migrate` to exit 0; it also waits by itself (logging "Waiting for the migrate service") until every migration it
carries is recorded, so starting it alone is safe.

## Ports, health

- The engine listens on `8080` inside the network (`http://engine:8080`), published as
  `${RHYTHM_BIND:-127.0.0.1}:${RHYTHM_MCP_PORT:-8181}`.
- `GET /health`: `200 {"status":"ok","embeddings":"on|off|downloading|download-failed|unverified","adapters":[...],
  "version":"1.0.0","schema":26,"models":[...]}` once the schema is current and the start work is done; `503` before.
  The compose health check calls it. `version` is the engine's (as MCP's `serverInfo` gives it), `schema` the number
  of the newest migration it carries, and `models` each model's job and state, its index counts read at most once a
  minute beside the request so a slow count never holds the answer, and none while memory scopes are on, since they
  count every scope (`Core/ModelStatus.cs`, the shape of `contracts/mcp/app_status.output.schema.json`'s `models`;
  [../models.md](../models.md)). `rhythm doctor` prints them.
- `POST /mcp`, and for the app role only `GET /app/documents/<id>`: a document's archived original, streamed
  (`contracts/README.md` 1.5, `Core/Mcp/AppFiles.cs`). `Host` must be in `engine.allowedHosts`; a request with
  `Origin` is refused (403).

## Environment and secret files

| Variable | Used by | Meaning |
|---|---|---|
| `RHYTHM_CONFIG` | all | path of `rhythm.yaml` (compose: `/etc/rhythm/rhythm.yaml`) |
| `RHYTHM_DB_FILE` | engine, migrate, cli | file holding the engine's connection string (secret `engine_db`) |
| `RHYTHM_DB_HOST`, `RHYTHM_DB_NAME`, `RHYTHM_DB_ADMIN_USER` | migrate | defaults `postgres`, `rhythm`, `rhythm_admin` |
| `POSTGRES_PASSWORD_FILE` | migrate, postgres | the owner's password (secret `postgres_password`) |
| `RHYTHM_ADMIN_DB_FILE` | migrate (optional) | an owner connection string instead of the three above |
| `RHYTHM_APP_TOKEN_FILE` | engine | the app token file (secret `app_token`); re-read every minute |
| `RHYTHM_DATA` | engine | data folder (archive, refresh state, downloaded model), default `/var/lib/rhythm` |
| `RHYTHM_MODEL_DIR` | engine (optional) | a folder already holding the MiniLM files; skips the download |
| `RHYTHM_MODEL_DIR_BGE_SMALL` | engine (optional) | a folder already holding the bge-small files, for a shadow model or the map model (`graph.similarity.model`, `docs/configuration.md`); skips the download |
| `RHYTHM_RERANKER_DIR_BGE`, `RHYTHM_RERANKER_DIR_MSMARCO` | engine (optional) | a folder already holding bge-reranker-base's or ms-marco-MiniLM's files, for `engine.rerank` (`docs/configuration.md`); skips the download |
| `RHYTHM_MODEL_DIR_BGE_BASE` | tests only | a folder holding the bge-base files, for the embedding benchmark (`docs/dev/second-brain-parity/benchmarks.md`) |
| `RHYTHM_MIGRATION_COMPAT_FILE` | migrate (optional) | accepted old checksums for an existing install (`docs/dev/migration-compat.md`) |
| `RHYTHM_CATALOG_PROVIDER` | engine, app (optional) | provider id of catalog stars, default `rhythm`; the app needs the same value (`app/server/README.md`) |
| `RHYTHM_<ADAPTER>_STAR_ACCOUNT` | engine (optional) | the account part of that adapter's star ids (`drive`, `second-brain`), default the provider id; only for an install whose stars an older engine named (`docs/dev/migration-compat.md`) |
| `RHYTHM_<ADAPTER>_<PURPOSE>_FILE` | engine | an adapter's secret file (`docs/dev/adapters.md`) |
| `RHYTHM_TEST_UPSTREAM` | engine (tests only) | sends adapter calls to a stub instead of their services (`docs/dev/adapters.md`); never set it in an install |

`engine_db` is a connection string such as `Host=postgres;Database=rhythm;Username=rhythm_engine;Password=<random>`.
`migrate` creates that login (or sets its password) with data rights and EXECUTE on the functions, but no schema
rights and no temporary tables (it revokes PUBLIC's `TEMPORARY` on the database at every run); if its user is the
owner, it is used as is. `rhythm init` should generate it with a random password. Every `SECURITY DEFINER` function
pins its search path to the installation schema with `pg_temp` last; a migration that replaces one with `CREATE OR
REPLACE` must set it again (a database test fails otherwise). The newest migration is 076 (`076_studio_search_path.sql`,
which gives 075's three functions that pinned path); before it, 075 (`075_studio.sql`: Studio's draft
sessions, [studio.md](studio.md), [migration-compat.md](migration-compat.md#studio-075)). 001 to 075 are on `main` and
deployed, so none of them is edited again; 049 to 074 came from the Second Brain program's branches, numbered by its
coordination board. 049 (`049_truth.sql`, SB-P4-06 and SB-P4-08: when a memory was true, one memory
replacing another after a confirmed plan, the states current, replaced, ended and retracted, `memory_retraction_flags`,
and the retraction cascade as a trigger on `memory_events`; it replaces 048's `memory_versions_prune` in place, so a
cascade's states are pruned first as a move's are, and 041's `memory_own_changes`, so the cascade's flag rows never
count against a member's own undo, and sets their search paths again). 050 (`050_nightly_jobs.sql`:
`job_cursors`, a nightly job's place per job and scope and the night a once-a-night job last finished, and
`night_runs`, the night summary kept only with `engine.nightSummary` on, kinds and counts only; plain tables the
engine's login writes, read by `rhythm night`; `Core/NightlyJobs.cs`, SB-P5-14). 051 (`051_typed_links.sql`, SB-P4-01:
`link_types()` and `link_kinds_allow()`, the link vocabulary and the episodic rule, called by nothing yet, and
`made_links.confidence`, `source_revision` and `checked_at` with their checks; it replaces 043's `restore_memory` and
`import_memory_link` in place to carry those three columns, and sets their search path again). 052 (`052_made_links.sql`, SB-P4-02:
`link_memories` and `unlink_memories`, the checked writers of person- and AI-made links, one scope, the author lock on
the source end and on a removal (either end's for `relates_to`), the episodic rule through `link_types()` and
`link_kinds_allow()`, a supersede's link left to supersede, and `memory_link_dismissals`, a checked table of the map's
suggestions dismissed, and of the made links marked suggested that unlink removed, kept apart from the links; it
replaces one earlier function, `checked_tables()`, rebuilt from the list in force (043's way), and sets its search path
again; the `link` and `unlink` tools call them only with `memory.tools` on, the map's reads and recall's hops read
`made_links` beside `relationships` only with it on too (`RelationshipEngine.MadeLinks`, `RecallOptions.MadeLinks`), and
they leave a dismissed suggestion out whatever it says). 053
(`053_staleness.sql`, SB-P4-09: `memory_checks`, a memory's last still true and the staleness pass's mark, at the
revision it was made at; `memory_stale_after`, `memory_stale_reason` (the stale queue's one predicate) and
`memory_stale_clock`; `memory_staleness_pass`, a piece of the nightly pass, a definer taking no principal, as the
daily jobs' are; `confirm_memory`, still true, under the author lock; it replaces 049's `memory_own_changes` in place
so a confirmation's audit row never counts against a member's own undo, and `checked_tables()`, rebuilt from the list
in force (043's way) with `memory_checks` added, and sets both search paths again). 054 (`054_digests.sql`,
SB-P5-13: digests of a project or a topic tag in one scope, with no model in the engine and no source touched;
`memory_digest_bounds` (at least 10 memories, the 50 newest, one a day, importance 4 or two recalls protecting a
memory, upstream's), `memory_digest_eligible` (the one rule `digest_sources` lists and the write checks),
`memory_digest_basis`, `memory_digest_latest` and `memory_digest_live`; `save_memory_digest`, a definer under the
caller's landing rule, which saves the client's paragraph as a new memory with source `system` (the engine's own
label) and a `drawn_from` made link to each source, answering the digest already saved from the same basis
unchanged; it adds `digest` to `memory_requests`' operations; the memory tools call it only with `memory.tools` on,
`Core/MemoryDigests.cs`; since run 16's integration a digest's eligibility reads its own scope's recall counts alone,
its key is read from its `created` row by `memory_digest_keyed`, and saves in a scope take one lock; since the fix
review it replaces 043's `import_memory`, which writes the digest object `rhythm export` now carries on the `imported`
row, which `memory_digest_keyed` reads too, so an imported digest stays the digest of its key). 055 (`055_citations.sql`, SB-P4-10: `memory_citations`, a checked table of the Drive documents native
memories cite, by file id and the revision, name and folders a metadata check at Drive found; `memory_cited_row`,
`memory_citation_readable` (served only under the map's newest row for the document, none when it holds no row),
`memory_citation_parents`, and the definers `cite_memory_source` and `revalidate_memory_citations`, under the author
lock and never for a held memory; a revalidation writes no check of Drive for a document the map no longer holds as
current (since the fix review it records `unavailable` for one whose newest row is missing, and nothing for one revoked
since a restore), and a check only while the citation keeps the checked time it was compared against; it rebuilds `checked_tables()` from
the list in force and sets its search path again; only `cite`, `revalidate` and `get` call them, with `memory.tools`
on, `Core/MemoryCitations.cs`). 056 (`056_link_upkeep.sql`, SB-P4-04,
numbered after 054 and 055: `invalidate_memory_links`, a trigger after a change of a memory's
words or kind, which marks the automatic made links at it (verification `suggested`) stale, in `made_links` and in a
trash row, and the map's links drawn from its words at its star rows stale with their evidence gone, never a link
someone stated, every lock SKIP LOCKED; `made_link_broken`, `made_link_prunable`, `made_link_grace` and
`made_link_weak`; `made_link_words_changed` and `made_link_missed`, the pass's re-check that marks out of date an
automatic link a change could not reach while it moved through the trash; and `prune_made_links`, a piece of the
nightly link pruning pass, a definer taking no principal, as the daily jobs' are; a supersede's link of any
verification is never invalidated or pruned; it replaces no earlier function and adds no table). 057 (`057_kept_dismissals.sql`,
SB-P4-05: `map_link_dismissals`, a checked table of the map's suggestions a person dismissed between two stars that are
not both native memories', keyed by the two star rows and the type; `dismiss_suggestion` and `restore_suggestion`, a
person's steps only (an AI client's channel is refused), each end read as the map serves it (`map_star_readable`, a
source share and a copy's narrower reader included, a memory's star never while held or in the trash, and since the
fix review a dismissal between two memories never taken back while either is held), by whoever may change either end
(`map_star_may_judge`),
between two memories' stars into 052's `memory_link_dismissals`; it replaces 052's `link_memories` in place so an AI
client's link takes back no dismissal, and `checked_tables()`, rebuilt from the list in force, and sets their search
paths again; only `app_memories`' `dismiss`, `undismiss` and `dismissals` and, since run 17, `rhythm undismiss` call
them, with `memory.tools` on, and every
map read and recall's hops leave out what they keep, `MapDismissals`). 058 (`058_loops.sql`, SB-P5-09:
`memory_commitments`, a checked table of a commitment's direction (`out`, the person owes it; `in`, owed to the person)
and counterparty slug, one row a memory, written once with it; `memory_open_loop`, the one definition of an open loop
(tagged `task`, not `task:done` nor an agent's build note, live, not held, not deprecated, its window open) that every
count and list reads, answering its direction; `memory_loop_mine`, the loops a reader is served as its own (the author
lock, and with scopes on its person's or no person's); `memory_counterparty`, upstream's slug of a name; and the
definers `remember_memory_commitment` (a memory written as `create_memory` writes it, tagged `task`, with its
commitment, a name that reads like instructions refused), `resolve_memory_loop` (`done`, `received` or `not_a_task` as a
change of the tags under the author lock: a version kept, one audit row `resolved`, undone by 041's undo; a repeat
writes nothing) and `import_memory_commitment` (the terminal's, adding an exported commitment only to a memory the
import has just added); it rebuilds `checked_tables()` from the list in force and sets its search path again; only
`list_loops`, `resolve_loop`, `remember`'s `owed_to` and `owed_by` and `rhythm import` call them, with `memory.tools`
on, `Core/MemoryLoops.cs`). 061 (`061_due_dates.sql`, SB-P5-10 beside 060's
decisions: `due_at`, `due_kind` (`due`, `event`, `wake`) and `due_source` (`explicit`, `text`) on `memories`, all three
or none, with an index on the moment; `memory_due_json`, a date as answers name it (the moment in UTC, how it reads on
the install's clocks and the zone, `install_timezone()`); `memory_due`, the one definition of the due list (a due
date, not `task:done`, live, not held, not deprecated, its window open) that every count and list reads, and
`memory_due_window` (`overdue`, `upcoming` within 48 hours, `later`); `memory_due_checked`, a date a write takes (at most
five years ahead); and the definers `remember_memory_due` (`create_memory`'s write, or 058's
`remember_memory_commitment`'s, then the date at revision 1 with a `due_set` audit row, its key hashing the request as
asked, so a date read from the words hashes as the same request without one), `resolve_memory_due` (`snooze` to a
moment to come, a memory with none taking one, kind `wake`, or `clear_date`, as a change under the author lock: a
version kept, one audit row `due_changed`; a repeat writes nothing; a decision refused, since it takes no due date,
D-214)
and `import_memory_due` (the terminal's, putting an exported date only on a memory the import has just added, never on a
decision); it replaces 033's `memory_retire`, 048's
`memory_retire_moved` and 049's `memory_retire_cascade` to keep the date in each version's state, and 041's
`undo_memory` to put it back (060, built beside it in run 18, replaces `undo_memory` too: the integrator merges both
into the later); only `list_due`, `resolve_due`, `remember`'s `when` and `when_kind` (and the date it reads from the
words, `Core/DueDates.cs`) and `rhythm import` call them, with `memory.tools` on, `Core/MemoryDue.cs`). 063
(`063_duplicates.sql`, SB-P5-02 and SB-P5-04: `memory_duplicate_flags`, a checked table
of the copies a new memory was flagged as close to on save, with the score, the model (or `exact`) and the threshold,
no key on the copy; `memory_duplicate_live` (in the catalog, not in the trash, not held, not deprecated, its window
open), `memory_duplicate_copies` (the flags that stand: both live, one scope) and `memory_duplicate_listed`, the one
definition of the duplicate review that every count and list reads; the definers `flag_memory_duplicates` (only the
memory's writer, at the revision it saved, at most three copies each from the threshold to 1, those not live in its
scope left out, one audit row `duplicate_flagged`) and `clear_memory_duplicates` (keep both, under the author lock, one
audit row `flags_cleared`); a trigger on `shared` and `unshared` audit rows that takes a moving memory's flags off and
those naming it (`unflagged`); `memory_transcript_sources()` (upstream's `claude-code`, `codex-session`,
`cursor-session`, and `gemini-session` for the fourth agent `harness/hooks` knows) and `memory_system_row` (a digest whose words and tags nobody changed since it was written); it
replaces 049's `supersede_memory` with the protection rules beside its canonical one (importance 4 or 5 replaced only
by a confirmed memory, a transcript's memory never replacing another source's, a slotted memory never replaced through
an AI client's channel; each `protected`, read only for a supersede still to plan or apply), and rebuilds
`checked_tables()` from the list in force; only `remember`, the app's `capture`, `list_duplicates`, `update`'s `clear`
`duplicate_of`, the app's `duplicates` and `keep_copy`, `supersede` and `digest` call them, with `memory.tools` on,
`Core/MemoryDuplicates.cs`). 065
(`065_standing.sql`, SB-P5-12: `memories.standing`, a column kept in a version's state
and put back by an undo, and constraints on a row that has it: 500 characters, no due date, no `task` tag;
`memory_standing_rows`, the one definition every list, count and firing reads (live, with why it cannot fire: `held`,
`project_held` in a project held for review, or `over_limit` past the oldest 50 of its scope);
`memory_standing_applies`, one in a project only for a question naming that project, never a project held for review; the
caller-checked `remember_memory_standing` (the 50 counted under a lock of the scope, a held one taking no place) and
`stop_memory_standing` (033's author lock, a version kept, one audit row, a repeat unchanged); `import_memory_standing`;
it replaces `memory_retire`, `memory_retire_moved`, `memory_retire_cascade`, `undo_memory` (keeping 060's and 061's lines)
and `memory_digest_eligible` (leaving a standing instruction out); only `remember`'s `standing`, `list_standing`,
`stop_standing`, the `recall` tool and `rhythm import` call them, with `memory.tools` on but for the import,
`Core/MemoryStanding.cs`). 070 (`070_resurface.sql`, SB-P5-17: `memory_resurface_dismissals`, a
checked table of one row a memory and a reader; `memory_resurface_before`, `memory_resurface_eligible` (the first read of 059's
`memory_upstream_tags`, for what upstream's own jobs wrote), `memory_resurface_dismissed` and `dismiss_resurfaced`, a person's step that refuses an AI client's channel; only `app_memories`' ops `resurface` and `resurface_dismiss` call them,
with `memory.tools` on, `Core/MemoryResurface.cs`). 069 (`069_brief.sql`, SB-P5-08: no table; one due
definition, D-214: it replaces 061's `memory_due`, leaving a decision out, and 060's `memory_decision_listed`, whose `due` and new
`upcoming` read 061's `memory_due_window`; the brief itself, `Core/MemoryBrief.cs`, reads the lists as they stand). 068 (`068_contradictions.sql`, SB-P5-03:
`memory_contradictions`, a checked table of one row a pair, a memory and the one of its scope its words came close to, with the
score, the model and the threshold, who named it a contradiction and why, and a person's decision; `memory_contradiction_stands`
and `memory_contradiction_listed`, the one definition of the review that every count and list reads; the definers
`offer_memory_contradictions`, `propose_memory_contradiction` (an id not offered is `not_offered`, whatever it names) and
`settle_memory_contradiction` (a person's channel only: `keep_both`, or `supersede_memory` at once); `recheck_memory_review` (what new
words no longer reach comes off, whichever memory's words made it) and `review_imported_memory` (what an import added, as the operator);
triggers on audit rows that take a moving memory's rows off, refuse an AI client's move of either memory of a row a person still has
to see, and refuse an AI client's change that would end a row's earlier memory or supersede the pair (`needs_person`; a named row also
while the later is in the trash, held or ended); and it replaces 063's `flag_memory_duplicates` (whoever made the revision, one flag a
pair, none for a pair somebody kept both of, at most 3 standing), `clear_memory_duplicates` (only the flags the review shows; through a
person's channel it settles those pairs too) and `memory_duplicate_copies` (not a pair named a contradiction, at most the 3 closest),
053's `memory_own_changes` (a contradiction's audit rows don't stop a member undoing their own change), and 062's `infer_memory_links`
(no automatic link between a pair flagged at 0.95 or more or named a contradiction), and rebuilds `checked_tables()` from the list in force; only the memory tools, the app's Memories, `rhythm release` and
the two imports call them, with `memory.tools` on, `Core/MemoryContradictions.cs`, `Core/MemoryDuplicates.cs`). 071
(`071_resolve.sql`, SB-P5-15: `memory_resolve_waiting`, the one definition of whether an item is waiting in the queue an
action resolves, each queue's own predicate; `memory_snooze_in_force` and `memory_resolve_counted`; the definers `resolve_memory_item`,
`resolve_insight_review` and `resolve_insight_candidate_once`, which take each step's locks and call the step only for an item still
waiting; no table, audit event or earlier function changes; only the memory tool `resolve` calls them, with `memory.tools` on,
`Core/MemoryResolve.cs`). 073 (`073_keep_copy_revisions.sql`, D-233's second revision check, run 22:
`resolve_memory_copy`, a definer that checks a person's keep both against the copies it names, 1 to 3 `{id, revision}`, under the
scopes switch's lock, the flagged memory's and, shared, its copies' and their contradiction rows', then calls 071's
`resolve_memory_item`; no table, audit event or earlier function changes; only `MemoryStore.ResolveItem` calls it, when a keep
both names its copies (the app's `resolve` with `copies`, `rhythm resolve` with `--copy`), with `memory.tools` on,
`Core/MemoryResolve.cs`). 064
(`064_insight_candidates.sql`, SB-P5-05: `memory_insight_candidates`, a checked
table of pairs of an older and a newer memory of one scope, written 30 or more days apart, with their similarity and the
model that measured it, the whole days between them, the signals that said they are worth a look (`similar`,
`weighty`), upstream's score and a status (`pending`, `used`, `rejected`), one row a pair whichever way round;
`memory_insight_events`, a checked table of each decision on one, by whom, through which channel and client;
`memory_insight_eligible`, a memory that may be half of one (live, not held, not deprecated, its window open, not the
engine's own); `memory_insight_live` and `memory_insight_listed`, the one definition of the list (waiting, both
memories eligible and of its scope) that every count and list reads; the job's definer `memory_insight_accrue` (no
principal: it checks each pair again, one scope, both eligible, 30 days apart, weighty only when both are of
importance 3 or more, and writes a pair it does not hold already, whatever became of it) and the caller-checked
`resolve_insight_candidate` (`use` or `reject` by a login that reads the pair's scope, a repeat unchanged, the
other decision on a decided one refused, one event row; no memory changes); it rebuilds `checked_tables()` from the
list in force and replaces no earlier function; only the nightly insight candidate pass and `list_insight_candidates`
and `resolve_insight_candidate` call them, with `memory.tools` on, `Core/InsightCandidates.cs` and
`Core/MemoryInsights.cs`). 066 (`066_insight_stream.sql`, SB-P5-06: the one insight
stream. `insights` gains `review` (`proposed`, every insight there is; `confirmed`; `dismissed`), `reviewed_at` and
`reviewed_by`, which `insight_json` does not serve; `memory_insight_candidates` gains `note` and `insight_id`;
`insight_events`, a checked table, keeps each feedback and review; `insight_stream_view` is the one definition every
count and list of the stream reads, a snooze ending in it by the clock; the caller-checked `review_insight` (memory.md
section 5's rule, and never over MCP by the login that saved the insight), `insight_feedback_kept` (027's feedback, the
same answer, kept), the reads `read_insight_stream`, `read_insight_day` and `read_insight_history`, and a longer
`resolve_insight_candidate` beside 064's, which stays for the release before. It rebuilds `checked_tables()` and replaces
one earlier function, 027's `save_insight`, pinned again in its search_path block: `supersedes` on a confirmed insight now
takes `review_insight`'s rule and leaves a `replaced` event, and every other save answers as it did, so a later migration
replacing it keeps that branch; only `memory.tools` on ever confirms an insight, so an install that never had it on does
not reach the branch. Only
`list_insight_stream`, `review_insight`, `insight_history`, `resolve_insight_candidate` and, for `insight_feedback_kept`,
the app's `app_insight_feedback` call the rest, all only with `memory.tools` on, `Core/InsightStream.cs`; no job is added). 059
(`059_second_brain_import.sql`, SB-P8-01:
`memory_upstream_tags`, a checked table of the tags an imported Second Brain memory carried in a namespace the engine
sets itself (standing instructions, decisions, confidences, commitments, holds), kept beside it as data that only 070's
`memory_resurface_eligible` reads; `import_second_brain_project`, `import_second_brain_memory` and `import_second_brain_link`, the operator's
definers (no principal, channel `cli`) that give each record a new id and keep its Second Brain id in 043's
`memory_origins` under the origin `second-brain:default`, never write a record the origin map holds, check where it
lands with 043's `memory_import_refusal`, screen each memory by 036's trigger and give it one `imported` audit row; it
rebuilds `checked_tables()` from the list in force and replaces no earlier function; only `rhythm import-second-brain`
calls them, with `memory.secondBrainImport` on, `Cli/SecondBrainImport.cs`). 060 (`060_decisions.sql`, SB-P5-11:
`memory_decisions`, a checked table of a decision's confidence and its source, one row
a memory, written once with it, and `memory_decision_states`, a checked table of its outcome, re-asks and review date
from each revision at which they were set; `memory_decision_at`, the state at a revision (the newest row at or before
it); `memory_decision_listed`, the one definition of the ledger's lists (`open`, `due`, `resolved`, `all` and
`scored`) that every count and list reads; `memory_confidence`, `memory_review_at` and `memory_review_default`
(upstream's rounding and review date, at 09:00 in the install's time zone); the definers `remember_memory_decision` (a
memory written as `create_memory` writes it, with its decision and first state), `record_memory_outcome` (`right`,
`wrong`, `mixed` or `unknown` as a change of the state under the author lock: a version kept, one audit row
`outcome`, no words; a repeat of `right`, `wrong` or `mixed`, or an `unknown` once the asking has stopped, answers
unchanged and writes nothing, while another `unknown` asks again, so it is not idempotent) and `import_memory_decision` (the terminal's, adding an exported decision
only to a memory the import has just added); it replaces 041's `undo_memory` in place, the same body with a decision's
state compared and written back (`outcome` among the fields it changed), rebuilds `checked_tables()` from the list in
force and sets their search paths again; only `remember`'s `decision`, `list_decisions`, `record_outcome`,
`decision_calibration`, `undo` and `rhythm import` call them, with `memory.tools` on, `Core/MemoryDecisions.cs`). 062
(`062_links_on_save.sql`, SB-P4-03: `memory_links_on_save` and
`memory_follows_window` (upstream's 3 links and 30 minutes, fixed), `memory_linkable` (a memory current in the catalog
and in time, out of the trash and not held) and the definer `infer_memory_links`, which links a memory whose words were
just saved to at most 3 memories of its scope by their best passage of the live model at or above the threshold the
engine names (`relates_to`, or `follows` to the one episodic memory written within 30 minutes before it), never a
dismissed pair, as automatic made links by the saving login, drawing again an out-of-date automatic link of that type;
it adds no table; it replaces 052's `memory_link_may_remove` for automatic (`suggested`) links alone, so whoever may
change either end may remove one, and adds two triggers that act only on automatic links joining two scopes: one on
`shared` and `unshared` audit rows (`memory_events_links_moved`) that removes the moving memory's automatic links into
another scope, and one before a `made_links` insert (`made_links_one_scope`) that keeps a restore from the trash from
bringing back one whose other end moved meanwhile (since run 19's fix review); only the memory store calls
`infer_memory_links`, after a write through the memory tools or the app, with `memory.tools` on, the model loaded and `adapters.embeddings.sources` listing `memories`,
`Core/MemoryLinkInference.cs`). 048 (`048_memory_share.sql`, run 13:
`share_memory`, which moves
one native memory between its owner's private scope and Family and back under memory.md section 5's rules, refused
while `memory.scopes` is off, its project following by slug, its capsule slot left by a person's move and a slotted
memory refused through MCP, a former author's or a link to another person's private record keeping it in Family, and
the map's star rows for it dropped; the `share` tool calls it only with `memory.share` and `memory.tools` on, and asks
for a map rebuild at most every three minutes (`RefreshRequests.QueueBounded`, which since run 13's fix review counts a
running rebuild only by when it was asked for), SB-P3-10; since that review the state a move retires is marked moved
(`memory_retire_moved`) and 034's `memory_versions_prune` is replaced to take such a state first, never the newest kept
one, so moving a memory back and forth never prunes its earlier edits, the newest twenty kept exactly as before when no
move was made; run 13 also changed 043's
`import_memory_project` in place to keep a held project held, SB-P3-23). 047 (`047_search_vector.sql`, run 12: `resources.search_english`, a stored
generated english text vector of a row's title and content, with its own index `resources_search_vector`, which ranked
keyword search and combined retrieval's catalog tier read, `Core/LexicalSearch.cs`; 021's expression index
`resources_search_english` dropped; 025's `guard_memory_resources` replaced to leave the column out of its whole-row
comparison; an upgrade rewrites `resources` once, 8.7 to 9.5 s across runs at 1 000 rows of 40 000 characters). 046
(`046_held_projects.sql`, run 12: `memory_projects.held` and `held_for`, a project a held memory write made kept out of
every list until a person lets it in; 033's `memory_project` and 035's `memory_project_claim` replaced, and an audit
trigger that lists or keeps one). 045 (`045_quarantine.sql`, run 12: write bursts, 40 content writes of one login
through MCP in ten minutes holding the next; the channel factor, instruction phrases holding nothing on a person's own
channel; a write into a held project held, and one that makes a project whose slug reads like instructions; all
decided in a trigger on `memory_events`, so no writer is replaced; the review queue reads it, `Core/MemoryReview.cs`).
044 (`044_terminal_logins.sql`, run 12: `principals.terminal`, the
terminal's own login, a harness principal bound to one person that no token exists for and the door refuses, one
enabled per person, its mark never changing; 033's `memory_channel` replaced to take `cli` from a terminal login alone
and `mcp` from every other harness login alone, and to refuse a missing channel; 037's `admin_audit_principals`
replaced to mark a terminal login's rows; `Cli/TerminalLogin.cs`). 043
(`043_export.sql`: the install's own id, made once and kept through a backup and restore; `memory_origins`, the origin
map of what an import added, ids only; `made_links.imported`; and the operator's checked import functions
`import_memory_project`, `import_memory` and `import_memory_link`, which only `rhythm import` calls, as the operator on
the host, `Cli/MemoryArchive.cs`). 038 to 042 (run 10) are on `main` since the merge of runs 8 to 10: 042
(`042_recall_log.sql`: the opt-in recall log, `recall_log` and `recall_counts` with their
checked writers `log_recall`, `recall_followed` and `prune_recall_log`, `Core/RecallLog.cs`; written only with
`engine.recallLog` on, off by default); 041 (`041_memory_undo.sql`: `undo_memory` puts a native memory back as it was
before its newest change or at an earlier revision, as a change of its own, so an undo is undone the same way;
`Core/MemoryUndo.cs` groups an AI client's changes and undoes a group a few memories a call); 040
(`040_keyword_text.sql`: two text indexes, on passages and on native memories' text, which combined retrieval's keyword
arm reads, `Core/Recall.cs`; no table, row or function changes, so nothing else answers differently); 039
(`039_capsules.sql`: `set_memory_capsule`, the checked writer of a memory's capsule slot, and a trigger that keeps a
project slot with its project, `Core/Capsules.cs`); 038 (`038_memory_trash.sql`: the trash's checked writers
`forget_memory`, `restore_memory` and `delete_memory_forever`, and the daily purge, `purge_memory_trash` and
`prune_memory_requests`, `Core/MemoryTrash.cs`; the audit trail's `system` channel for the engine's own job); 037
(`037_admin_audit.sql`: `admin_events`, an insert-only trail of the setup changes that decide who reads what, written
by triggers on memberships, source shares, principals, people and the `memory.scopes` row, each writer naming itself
for its transaction with plain settings, `Core/AdminAudit.cs`; the engine's login only reads it, and the host commands
and the start now record their changes, answering as before); 036 (`036_holds.sql`: the database screens every native
memory it stores and holds one that reads like instructions, hides characters or is over 32 KiB, and
`release_memory_hold` lets a person lift it from the app or the terminal: the app's Memories view calls it with
`memory.tools` on, SB-P3-12, and so does `rhythm release` through a person's own terminal login, SB-P3-22); 035 (`035_memory_projects.sql`: the project registry's checked writers,
`create_memory_project`, `update_memory_project` and `delete_memory_project`, which keeps its memories); 034 (`034_memory_history.sql`: each memory's 20 newest earlier states kept by a trigger, and
`memory_history_floor`, how far back a reader sees its history). Nothing calls a memory writer yet (D-050), so no
install holds a native memory and nothing answers differently; 033
(`033_memstore.sql`: the native memory store's checked writers, `create_memory`, `append_memory`, `update_memory` and
`set_memory_status`, with expected revisions, idempotency keys (`memory_requests`, a checked table) and the author lock,
each taking the `memory.scopes` mode's row `FOR SHARE` first; `Core/Memories.cs` reads memories through the one read
predicate. Nothing calls them yet, so no install holds a native memory and nothing answers differently); 032
(`032_own_corners.sql`: with `memory.scopes` on, a viewer session drops its own corner choice again with the nil
uuid and is told which corners are its own, nothing reaching it while `app.viewers` is off); 031
(`031_document_scopes.sql`: with `memory.scopes` on, `save_document` lands a copy only for the caller's own person or,
for a Family reader, the household, and a subject left out is the caller's own person; nothing changes with scopes
off); 030 (`030_person_corners.sql`: corner cards per person, nothing written while `app.viewers` is off); 029
(`029_screen_binding.sql`: a screen's id bound to the app login that first listened with it, refusing nothing while
`app.viewers` is off); 028 (`028_viewer_sessions.sql`: the one-time codes that open viewer sessions, unused while
`app.viewers` is off); 027
(`027_derived_scopes.sql`) put a subject on insights, insight runs, dashboards, dashboard writes, corner cards and pins,
and the functions that read and write them take the stars their caller reads. The Second Brain program's 021
(lexical search), 023 (memory scopes), 024 (embedding models), 025 (native memory tables), 026 (passages) and 027 to 074
are on `main`, merged from its branches; a later one is numbered provisionally on that trunk and renumbered above `main`'s
highest before it merges. 022 (`022_composable_layouts.sql`): each dashboard version's `layout`, the `dashboard_layouts` table of
in-place arrangements, `dashboard_json` and `save_dashboard` replaced and `save_dashboard_layout` added
(`docs/dev/screens.md` 10.7, `docs/dev/migration-compat.md`). 020 (`020_screens.sql`) added the live screens' tables
`screens` and `screen_commands` and their role-gated functions.

## Tokens

- **App:** the engine reads `RHYTHM_APP_TOKEN_FILE` at start and every minute and keeps exactly one enabled principal
  `app` (role `app`) with that token's SHA-256, disabling older ones. A missing, empty or directory file changes
  nothing. Format as the contract says (43 base64url characters); the engine accepts 32-512 characters.
- **Harness:** `./rhythm token create <name>` (role `harness` by default), which runs
  `docker compose --profile cli run --rm cli token create <name>` in `deploy/compose`. The token is
  written to `secrets/harness_<name>_token` (or `--out <file>`; mode 0600, owned by the deploy folder's owner, never
  overwritten) and shown to no one. `./rhythm up` makes a first one, `assistant`, when there is none.
- **Ingest:** `token create --role ingest --plugin <name>` (the name is `plugin:<name>`; the file
  `secrets/plugin_<name>_token`); the plugin also needs `plugins.<name>` in `rhythm.yaml`, else every call is 403. Its
  providers take its `policy`, or their own from `policies` (a live feed beside an archive, as the CareLink plugin
  writes).
- **Capture:** `capture connect <name>` (with `memory.capture` on; the file `secrets/capture_<name>_token`; `--person <id>` for a
  person's scope). It reads nothing and writes only through `capture_memory` (migration 067).
- **A person's client:** `token create <name> --person <id>` binds a harness token to one person in `people` for
  good (`principals.person_id`, migration 023; docs/dev/memory.md section 3). With memory scopes off it only records
  who wrote what. The engine refuses a token bound to a suspended or former person at the door (401), whatever its
  `enabled` flag says.
- `token list` (each enabled token and whom it speaks for: a person, the household view or a plugin),
  `token revoke <name>`, and `token revoke --all` after a leak: every harness, plugin and capture token and every viewer
  credential disabled at once, for good. The app's own token is not among them: the engine takes it from its file.
- `person list`, `person suspend <id>` (disables every token bound to that person at once; lifting it with
  `person unsuspend <id>` leaves them disabled, and makes someone the engine's last start did not find in `people`
  former rather than current). Like `token`, these run without reading `rhythm.yaml`. The start that finds a person
  gone from `people` disables every token bound to them for good too, and logs the count.
- `person offboard <id>` (SB-P7-13, migration 072): someone leaving for good. After a preview and the id typed, their
  access ends at once (as `person suspend`, out of Family, shares ended) and their private memories move to the trash in
  batches, reason `offboard`; a stopped run goes on when run again. The daily turn (`Scheduler`, `Offboardings.Daily`)
  deletes their emptied private projects once the trash's purge has taken what waited, and records it finished.
- `family add|remove|admin|unadmin <id>` (Family's members and admins, `memberships`), `share add|revoke <person>
  <provider>` and `share list` (`source_shares`, recorded with no principal: the operator's), and `scopes off`, which
  records `memory.scopes` off after a preview and is refused while any native memory, earlier version or memory
  project is private (`engine/src/Cli/ScopeCommands.cs`; docs/dev/memory.md sections 4 and 6.2; cli/README.md).

## Database login

What `migrate` gives the engine's own login today (`Engine.GrantLogin` in `Core/Program.cs`, at every run, so a
restored database gets it again):

- **Every table** in the installation schema: `SELECT`, `INSERT`, `UPDATE` and `DELETE`, and the sequences. That
  includes the insights, insight runs, dashboards and their writes, pins, corner cards, refresh requests and screens.
  The comments of migrations 006, 010, 011, 012 (by reference to 011) and 013 to 016 say the login gets EXECUTE on
  their functions and no table rights. That described the private engine they came from, with a login per caller;
  this engine's `migrate` has granted every table since it was imported, with 017's roles. Applied migrations are
  checksummed and never edited, so their comments stay and this page is what holds. The role checks inside those functions decide which principal (harness, app, ingest) may do
  what; they do not bind the login, which could change those tables with plain SQL.
- **Read only:** `schema_versions`, and the tables `checked_tables()` lists (migration 025: the native memory tables
  `memories`, `memory_versions`, `memory_events`, `memory_trash`, `memory_projects`, `memory_project_aliases`,
  `capsule_revisions`, and `made_links`, the links people and AI clients make; migration 033: `memory_requests`, the
  idempotency keys of memory writes, which hold no memory's words; migration 037: `admin_events`, the admin audit,
  which triggers on the tables it records write, so the login's own host commands are recorded too; migration 042:
  `recall_log` and `recall_counts`, the opt-in recall log and its counts; migration 043: `memory_origins`, the import's
  origin map). The login changes those only by calling
  their checked functions: `SECURITY DEFINER`, the caller's role checked first, the scope and author rules of
  [memory.md](memory.md) inside. The memory writers are 033's (`create_memory`, `append_memory`, `update_memory`,
  `set_memory_status`), 035's project writers, 036's `release_memory_hold`, 038's `forget_memory`, `restore_memory`
  and `delete_memory_forever`, 039's `set_memory_capsule` and 041's `undo_memory`, and 043's import functions. Since
  run 11 the MCP memory tools call them as an AI client (SB-P3-09, only with `memory.tools` on, off by default), and
  `rhythm import` calls 043's as the operator; since run 12 the app's Memories view calls them as the app
  (`app_memories`, SB-P3-12, behind the same `memory.tools`), and `rhythm remember` calls 033's `create_memory` as a
  person's terminal login (044, SB-P3-19), so on an install with the tools off nothing but the operator's commands, the
  import and `rhythm remember`, writes a memory. Since run 13 a person's own steps beyond one memory at a time call
  them too, only with `memory.tools` on (SB-P3-22): the app's review queue and release of several (036's, through
  `MemoryStore.Review` and `ReleaseAll`) and its capsule slot (039's), and at the terminal, through the person's own
  terminal login, `rhythm release`, `rhythm capsule` and `rhythm delete-forever` (036's, 039's and 038's); with the
  tools off those commands answer as ones the engine does not know. Since run 14 the terminal also takes `rhythm
  forget` (038's), `rhythm set-status` (033's) and the capsule view (`rhythm capsule` with no memory, a read), and with
  `memory.share` on beside the tools a person's share calls 048's `share_memory` as the app (`app_memories` `share`,
  listed only while memory scopes are on) and as the person's terminal login (`rhythm share-memory`). Since run 17 a
  person's link steps call 052's `link_memories` and `unlink_memories`, 053's `confirm_memory`, 049's
  `clear_memory_flags` and 057's `restore_suggestion` as the app (`app_memories` `link`, `unlink`, `still_true`,
  `clear_flags`) and as the person's terminal login (`rhythm link`, `rhythm unlink`, `rhythm still-true`, `rhythm
  clear-flags`, `rhythm undismiss`, and since run 21 `rhythm queues`, `rhythm duplicates`, `rhythm contradictions`, `rhythm
  insight-candidates`, `rhythm insights` and `rhythm resolve`), only with `memory.tools` on. The engine itself calls only the daily jobs' writers
  (038's `purge_memory_trash` and `prune_memory_requests`, 042's `prune_recall_log`), which delete nothing an install
  holds today, and with `engine.recallLog` on 042's `log_recall` (`recall_followed` is called by nothing until the
  memory tools). The audit trails (`memory_events`, and `admin_events` for setup changes) are insert-only, for the
  owner too.
- **Guarded rows** in a table the login writes: a memory's `resources` row (provider `memory`), on which it may change
  only `indexed_at`, so a share (a change of that row's subject) goes through the functions too. The map keeps a
  memory's star in a snapshot row of its own (provider `memory`, a `relationship-engine:` external id), which the
  login writes like any star. A trigger refuses the rest, since a grant can't single out rows. Links a person or an AI
  client makes are in `made_links`, one of the checked tables, so the map's rebuild never touches one.
- **EXECUTE** on every function; no `CREATE` on the schema and no temporary tables.

A migration that adds a memory table lists it in `checked_tables()`; a database test fails for a table named like one
that isn't. These limits hold only when the engine has a login of its own: one that is the owner (allowed above) passes
every check. `EngineLoginTests` checks the grant table by table and the guards; `MemoryRestoreTests` checks them again
after a backup is restored.

## Roles and tools

- `harness`: 29 tools (the private engine's 25 less `resync_carelink_csv`, which left with the CareLink plugin, plus
  `list_screens` and `control_screen` for live screens, and Studio's `studio_dashboard`, `discover_dashboard_controls`
  and `invoke_dashboard_action`, `Mcp/StudioTools.cs`, [studio.md](studio.md)), served by the MCP SDK, stateless; no
  adapter here adds one yet. `semantic_search` is listed only while the local model is on, so a client sees 28 without
  it. `recall` (combined retrieval, SB-P2-09) is a 30th, listed only with `engine.recall: on` (`RecallSettings`, `Mcp/RecallReply.cs`);
  `get_prompt_capsule` only with `memory.capsules: on`; and the 41 memory tools (`MemoryTools.Names`; SB-P3-09:
  `remember`, `append`, `update`, `get`, `list_recent`, `set_status`, `forget`, `undo`, `history`, `list_projects`, `list_teams`; and Rhythm's
  `supersede`, SB-P4-06, migration 049; `link`, `unlink` and `connections`, SB-P4-02, migration 052,
  `Core/MemoryLinks.cs`; and `list_stale` and `still_true`, SB-P4-09, migration 053; and `digest_sources`,
  `save_digest` and `digest`, SB-P5-13, migration 054, `Core/MemoryDigests.cs`; and `cite` and `revalidate`, SB-P4-10,
  migration 055, `Core/MemoryCitations.cs`, which check a cited Drive document's metadata at Drive through the adapter's
  `ICitedSource` at each cite, read and revalidation and never touch the map; and `list_loops` and `resolve_loop`,
  with `remember`'s `owed_to` and `owed_by`, SB-P5-09, migration 058, `Core/MemoryLoops.cs`; and `list_decisions`,
  `record_outcome` and `decision_calibration`, with `remember`'s `decision`, `confidence`, `confidence_source` and
  `review_by`, SB-P5-11, migration 060, `Core/MemoryDecisions.cs`, whose calibration is the engine's own arithmetic over
  the scored list, each figure with its sample count and none below its minimum; and `list_due` and `resolve_due`, with
  `remember`'s `when` and `when_kind`, SB-P5-10, migration 061, `Core/MemoryDue.cs` and `Core/DueDates.cs`; and
  `list_duplicates`, with `remember` checking a new memory's words against its scope's and flagging a near copy for
  review (never refusing or merging it) and `update`'s `clear` `duplicate_of`, SB-P5-02, migration 063,
  `Core/MemoryDuplicates.cs`, whose score is the live model's (the words alone, the thresholds' recipe) against the
  duplicate threshold `EmbeddingThresholds.Override` reads, today's 0.85 unless `adapters.embeddings.thresholds: tuned`;
  and `list_contradictions` and `propose_contradiction`, with every write that saves words naming the close memories that may
  conflict and queueing the pair for a person, who alone settles it, SB-P5-03, migration 068, `Core/MemoryContradictions.cs`, the
  threshold `Contradiction` of the same switch, today's 0.85 unless tuned;
  and `list_insight_candidates` (with `dry_run`, which reads the live embeddings model and writes nothing) and
  `resolve_insight_candidate`, SB-P5-05, migration 064, `Core/MemoryInsights.cs` and `Core/InsightCandidates.cs`;
  and `list_standing` and `stop_standing`, with `remember`'s `standing` and what `recall` fires, SB-P5-12, migration 065,
  `Core/MemoryStanding.cs`, whose score is the live model's (the question against the words alone, the thresholds' recipe)
  against the standing threshold `EmbeddingThresholds.Override` reads, today's 0.67 unless
  `adapters.embeddings.thresholds: tuned`;
  and `list_insight_stream`, `review_insight` and `insight_history`, SB-P5-06, migration 066, `Core/InsightStream.cs`;
  and `brief`, each section the head of the list its own tool reads, SB-P5-08, migration 069, `Core/MemoryBrief.cs`;
  and `resolve`, one tool for every queue's action, each that queue's step, an item leaving its queue once, SB-P5-15, migration 071,
  `Core/MemoryResolve.cs`;
  `Mcp/MemoryTools.cs`)
  are made at
  all only with `memory.tools: on`, each reading and writing as an AI client (channel
  `mcp`, fixed in its handler) through the memory store, with an output schema and structured content; `share`
  (SB-P3-10) only with `memory.share: on` as well, both off by default. With `memory.tools: on`, `recall` is made from
  `HarnessTools.RecallAsOf` in place of `RecallSearch`, the same tool taking `as_of` besides (SB-P4-07,
  `Core/RecallAsOf.cs`), which answers exactly as `RecallSearch` without it. With it on, the
  handshake also gives a client `harness/memory.md`'s block as the server's instructions (`Mcp/MemoryInstructions.cs`,
  SB-P3-20); off, none, as before. `Engine.Mcp` (`Program.cs`) builds the server, which `McpClientCompatibilityTests`
  drives as clients on every protocol from 2024-11-05 to 2026-07-28 reach it (SB-P6-05).
  `save_household_document` is accepted as an alias of `save_document` and listed only with `engine.legacyToolNames`.
  Descriptions are filled from `rhythm.yaml` (people, instance name). The dashboard design guide is the resource and
  prompt `rhythm://guides/dashboards`. Each call's arguments are checked against the tool's listed schema first
  (`Mcp/ToolArguments.cs`): a missing, unknown or wrongly typed argument answers which one and what it takes, never
  the SDK's generic "An error occurred invoking", and never the value sent.
- `app`: `app_graph`, `app_insights`, `app_insight_feedback`, `app_dashboards`, `app_request_refresh`, `app_screens`,
  `app_status` (the engine's version and each model's state, as `/health` gives them, for the Sources view),
  `app_sync` (Sync now in the Sources view: the adapters on the scheduler's clock, and one batch now of one of them,
  `SyncRequests`, below), `app_latest_glucose` / `app_recent_glucose` while any glucose data exists, and `app_upload_document` while the
  documents adapter is on (an app call whose body may exceed 32 KiB: up to 7 MiB), `app_memories` only while
  `memory.tools` is on (the app's Memories view, `Mcp/AppMemories.cs`: the memory store's reads and writes as the app, channel
  `app` fixed in its handler, a body of up to 288 KiB for a memory's whole text; the review queue, a release of several
  and a capsule slot since SB-P3-22; the duplicate review, `duplicates` and `keep_copy`, and `capture` checked for
  copies since SB-P5-02; since SB-P6-01, `Mcp/AppMemoriesManage.cs`, a list's tags and count, an AI client's groups of
  changes and a group's undo, several memories at once, 035's project writers, a capsule's status and the memory settings to
  read, and, `Mcp/AppMemoriesQueues.cs`, how many wait in each review queue, the insight candidates and stream, a review and
  one resolve for a person's step in a queue, none of it reaching an AI client; also since SB-P6-01, `Mcp/AppMemoriesPlans.cs`,
  the lists the brief heads, tasks and commitments, what is due and decisions with their outcomes, and a person's word on each
  through the app's channel), `app_studio` (Studio for the screen a draft is on: its state, the draft's data and a
  saved version's controls to read, and the screen's validate, feedback, cancel, discard and save, `Mcp/StudioTools.cs`,
  [studio.md](studio.md)), and `app_viewer` only while
  `app.viewers` is on, which this version refuses (viewer sessions, `ViewerSessions`, `docs/security.md`).
  `app_graph`'s `content` answer names a document's archived original (`resource.original`), which
  `GET /app/documents/<id>` serves. Old `observatory_*` names and `get_relationship_graph` are accepted as aliases.
  Enabled adapters add their own app tools (`AppTool`): with `adapters.garmin` on, `app_garmin_summary` and
  `app_request_garmin_sync` (aliases `observatory_garmin_summary`, `observatory_request_garmin_sync`).
- `ingest`: `ingest_measurements`, `ingest_records` (body up to 512 KiB).
- `capture`: no tool and no `/mcp`: `POST /capture` alone, with `memory.capture` on beside `memory.tools` (body up to 288 KiB;
  `Mcp/CaptureEndpoint.cs`, `contracts/README.md` section 6). Off, the route answers as an unknown route does.

With `engine.writeLimit` on (off by default; off, no service, so nothing asks), a token past 300 writes in 10 minutes has its next
write refused (`Mcp/WriteLimit.cs`, SB-P7-12, `docs/security.md`): `CheckedTool` asks for a harness tool not marked read only,
`IngestEndpoint` for a `tools/call` and `CaptureEndpoint` before it reads a body. They ask past the door, since the door has read no
body and a read must never count. The count is in this one process on a monotonic clock, needs no database and no migration, and a
restart forgives it. The app's endpoint and the terminal never ask.

Corner cards (`app_dashboards` `{cards: true}`, `{corner, dashboard}`): `main`, `recovery`, `training`, `changed`.
Migration 018 renamed the tall slot from `glucose` to `main`, moving a stored choice with it.

Composable layouts (`docs/dev/screens.md` 10): a dashboard version keeps a grid of parts (`Dashboards.cs`:
`DashboardLayout`, `DashboardPart`, `MapFocus`), its page one of them; a version saved without one is its page alone,
and `dashboard_json` answers `layout: null` for it. `DashboardLayout.Problem()` checks every layout before it is saved,
from either role, with the rules the app's `parts.ts` keeps. Moves, resizes and focus changes arrange the newest
version in place as a layout revision (`save_dashboard_layout`), never a new version, so `updated_at`, Allow edits,
writes, corner cards and pins stay as they are. The app role may only arrange the parts a layout has (the database
compares them without their rectangles and a map's focus); a harness composes any layout. A save checks again only
new parts and changed references: a focus star readable by whoever saves, a card naming another saved dashboard.
`save_dashboard` takes a layout with a new dashboard or a next version (a next version without one keeps the
arrangement; a restore brings back the restored version's), and arranges the newest version with `dashboardId`,
`layout` and `note` alone. `DashboardMap.cs` answers a map part's focused sky (`app_dashboards` `{id, map}`): it reads
the reader's whole map (`RelationshipHost.Readable`), keeps the providers the map shows and the links that aren't
disputed, walks `near` itself, works out each star's source as the app's `starSource` does, and ranks and pages the
matches as `app_graph`'s overview.

Screens (`app_screens`, `list_screens`, `control_screen`; `docs/dev/screens.md`): each command is checked, stored in
`screen_commands` and handed to its screen's waiting `listen`. The wait is in memory (the engine is one process) and
never holds a database connection; the database stays the source of truth, so a restart only makes the screens listen
again. A `listen` is held up to 25 s and answered `command: null` when nothing came, and at once when the engine starts
to stop, so a held request never delays a restart; a command not picked up within 30 s expires. The engine stores commands but applies none: the screen does, a `corner_card` one through the same
`{corner, dashboard}` call the corner-card picker uses. What a screen keeps (`settings`: layout, tiles, Home and its Home
grid) is checked on the way in. `move`, `resize` and `focus_map` (piece 3) take a dashboard as a guard: left out, the
engine fills it in from what the target last reported (its dashboard, or null at Home), and checks the parts named
against that dashboard's newest layout or Home's five parts. A new screen whose name another screen seen in the last 30 days has is stored with a number after it:
the engine reads the names under the lock `listen_screen` takes to add a screen and calls it with that name, so
migration 020 stays as it was. A later `listen` never replaces that numbered name with its bare one; `rename` sets
any name.
With `memory.scopes` on (never on today), what a screen shows and reports is the view of the app login it listens
with, and each reader is told of it only what it reads; a command names only what its sender reads and carries only
what the target's login reads (027, `ScreenStore.Scope`; `docs/dev/memory.md` 7.5, `docs/dev/screens.md` 3.2).

Errors: HTTP 401 (no or unknown token), 403 (wrong role, Host, Origin, disabled plugin), 400 (arguments outside the
schema), 404, 413, 503, each with a JSON-RPC body whose `result.isError` is true and
`structuredContent.error = {code, message}`. Ingest validation failures are `200` with `isError: true` and up to 20
`issues`, as the contract says. A 405 (any method but POST, outside the harness) has no body.

Each refusal and failure is one warning line in the engine's log (`Mcp/RequestLog.cs`, Q-137), with fixed fields only:
`Request refused: <status> <reason>` at the door (`origin_present`, `host_not_allowed`, `not_found`, `not_ready`,
`no_token`, `unknown_token`, `method_not_allowed`, `wrong_role`, `plugin_off`, with the caller's role where known), and
`Call failed: <role> <tool>, <status> <code> (<exception type>)` from an endpoint or a harness tool. The tool is the
engine's own name (`unknown_tool` for one the role does not have); no body, argument, token, header or exception
message is logged. The same line is written at most once a minute, then says how many more there were.

### App tool shapes

Every `app_*` result's `structuredContent` is pinned by `contracts/mcp/<tool>.output.schema.json`, with a fictional
example in `contracts/fixtures/`. The engine's tests check its real answers against the schemas
(`engine/tests/Knowledge.Tests/Schemas.cs`, the same small JSON Schema subset as `scripts/json-schema.mjs`); the app's
tests read the fixtures (`app/tests/contract-fixtures.test.ts`).

Glucose (`AppHealth.cs`) works for any provider that writes metric `glucose`. Each provider's readings are an
**archive** or a **live feed** by its policy (`AuthorizedArchive` or `LiveSource`, from its adapter's or plugin's
registration; an unregistered provider is live), never by its name:

- `app_latest_glucose`: the newest live reading, in mg/dL. An archive is never the current reading.
- `app_recent_glucose` `{}`: 24 hours of live readings as rows, one per 5-minute slot, in mg/dL.
- `app_recent_glucose` `{days: 14}`: one person's archive up to the 5-minute slot of the first live reading in the
  window, then their live feeds, as `series: [[epochSeconds, mg/dL, -1 | 1?]]`; `sources` is at most an archive
  stretch (`timeBasis: device-local`) then a live one (`source-zone`), each with its `provider`; `latest` is the newest
  live reading or null; `provider` the newest live reading's provider, else the archive's; `demo` marks the seed.
- Both forms carry `summary`: the archive's latest 14 complete local days within the last 60 (`sourceKind: archive`,
  `timeBasis: device-local-unverified`), or null without an archive.

So a Nightscout site (live), a CSV import (archive) and an external CGM plugin that writes an archive provider and a
live one (for example `carelink-csv` and `carelink-live`) all fill the same tiles. The demo seed has no glucose. A
plugin's per-provider policies come from its `plugins.<name>` entry.

### Garmin shapes (for the app's facade)

Pinned like the others: `contracts/mcp/app_garmin_summary.output.schema.json` and
`app_request_garmin_sync.output.schema.json` (and `app_upload_document.output.schema.json` for uploads).

`app_garmin_summary` `{subject?}` (only the adapter's owner): `{subject, selection: "garmin", complete, rows, today,
startDate, endDate, start, end, coverageComplete, activityCount, lastSyncedAt, summary: null}`, the private reader's shape.
`today` is in `adapters.garmin.deviceTimezone`; `rows` (at most 2000) are the last 14 days of the tiles' twelve metrics,
each `{resource_id, subject_id, provider: "garmin", source_kind: "metric" | "activity", timezone, source_updated_at,
metric, sample_key, observed_at, local_date, value, unit, quality, activity_type, pool_unit, multisport}`. Heart rates
are stored in `bpm` and handed over as `beats/min`, the unit the facade reads. `coverageComplete` is true when a Garmin
job covered this week through today; `lastSyncedAt` is the newest finished job or accepted write.
`app_request_garmin_sync` answers `{accepted, pendingSince, nextAllowedAt, lastSyncedAt}` (`request_garmin_sync`).

## Scheduler (no host cron)

Every minute: the app token file, each MeasurementSource and ImportSource whose interval has passed (the CSV and
documents folders are read every minute; Garmin's interval is one minute: it works only when a file, a job or its own
poll is due, at most two minutes a tick), and one waiting "refresh now" request (claimed, rebuilt, finished with its
outcome). An import or upload that changed something queues such a request itself (open question Q-049), and so
does the embeddings model coming on after a rebuild ran without it (a first start downloads it), so similar links
appear within minutes rather than at the next timer. Every `graph.refreshSeconds`: the map rebuild. Daily: ingest
keys older than 90 days are pruned (the contract keeps them at least 30), and so are screen commands sent more than
30 days ago and screens unseen for 180 days; native memories in the trash past their retention are deleted for good, at
most 1000 a day, and memory idempotency keys older than 30 days pruned (SB-P3-06, `MemoryTrash.Purge`: with no memory in
the trash, every install's state today, it deletes nothing); and the opt-in recall log's rows and counts older than 30
days, whatever `engine.recallLog` says now, so a log turned off empties itself (SB-P2-11, `RecallLog.Prune`: on an
install that never turned it on, both are empty and it deletes nothing). A failing job is logged by kind only.

The daily jobs run through the nightly job runner (SB-P5-14, `Core/NightlyJobs.cs`, migration 050), at the same time
(the engine's start, then every 24 hours) and in the same order, each writing the log lines it always wrote
(`NightlyJobs.Daily`; later jobs are added at the end of that list, ids never reused). Each job has its own budget (15
minutes for each of the four today): when it runs out, the job's token is cancelled and its run ends `timed out`; a run
that still has not ended after a grace (its budget again, at most 30 seconds) is left to end on its own while the next
job starts, and its job is not started again until it has (`still running`). A failure is logged by kind
(`<job> failed (<kind>)`) and the next job runs; a job's own cancellation inside its budget (a client's timeout in it)
is a failure of that kind, never `timed out`. A job may keep a cursor per job and scope (`job_cursors`), saved after
each piece it finishes, so a run stopped by its own bound (`partial`: the trash purge with every batch full or its keys
at their bound, the recall log's prune at its bound of rows or counts), a failure or its budget goes on from there at
its next run. A piece written in a transaction of its own saves its cursor in that transaction
(`NightlyContext.Save(connection, transaction, ...)`), so a process killed between the two repeats nothing; a job that
saves apart from its piece keeps each piece safe to do twice. A job may run once a night, not started again the same night once it has finished
(a night is the install's date in its time zone when the daily turn began). None of the four daily jobs keeps a cursor
or runs once a night, so they write no such row. With `memory.tools: on` a fifth job follows them, the memory
staleness pass (SB-P4-09, `Core/MemoryStaleness.cs`, migration 053; off, it is not in the list, and the turn, its log
lines and the summary are as before): with the same 15-minute budget it walks each scope's native memories in id order
from that scope's cursor, 200 a piece and at most 1000 a scope a run (`partial` when a scope has more), each piece and
its place in one transaction, and marks each memory whose volatility's time has passed since its last change or
confirmation, taking off a mark that no longer holds. It writes nothing for a memory that is not due, keeps a place
only for a scope it has not walked to its end, and logs `Memory staleness: ...` only when it marked or cleared one, so
on an install with no native memory it writes and logs nothing. A sixth follows it with the same switch, the memory link
pruning pass (SB-P4-04, `Core/MemoryLinkPruning.cs`, migration 056): with the same budget it walks each scope's person-
and AI-made links in id order from that scope's cursor, 200 a piece and at most 1000 a scope a run, and deletes each
automatic one (`suggested`) past its 7-day grace period that is weak (a confidence under 0.3) or no longer active,
never one with an end in the trash or one a writer holds right then; a link someone stated it never deletes, and it
counts those that are broken (an end in the trash, marked wrong or held, or no longer of the kinds their type joins).
It logs `Memory links: ...` only when it pruned or counted one, so with no made link it writes and logs nothing. A
seventh follows with the same switch, the insight candidate pass (SB-P5-05, `Core/InsightCandidates.cs`, migration
064; the scheduler hands it the engine's `Embeddings`): with the same budget it walks each scope's eligible memories
oldest first from that scope's cursor, 25 a piece and at most 200 a scope a run (`partial` when a scope has more),
reading only those and, for each piece, the earlier memories they are compared with (keyset reads on `memories_recent`,
never a whole scope), and pairs each with the latest 2000 eligible memories of its scope written 30 or more days before it, by their words'
similarity under the live model (made as it goes, kept for the run only, read against the insight threshold the
install reads, D-211) and both being of importance 3 or more, keeping each memory's best three by upstream's score;
each piece's pairs and its place are written in one transaction through `memory_insight_accrue`, a pair held already
is never written again (but a waiting one whose two memories have both moved to this scope, which no list serves since,
is moved here), and a scope walked to its end starts again from its first memory at its next run. Without the
model loaded it reads and writes nothing, keeps every place and logs `Insight candidates: waiting for the embeddings
model`, and the scheduler runs it once more, alone, at the first minute the model is on (`NightlyRunner.Again`),
rather than at the next daily turn: the engine's start most often finds the model still loading, so an engine
restarted every day would otherwise never run it. The turn keeps its time, and with the summary on that run is one more
run of the night. With no native memory it does nothing at all. It logs `Insight candidates: ...` only when it added one or found one no longer eligible. With `engine.nightSummary` on, each job's night is kept in
`night_runs` (the latest outcome, a failure's kind and time against budget; counts summed over the night; how often it
ran), one line `Night <date>: <n> jobs, ...` is logged after each daily turn, and the operator reads it with `rhythm
night`; off, as by default, nothing is kept or logged. Rows of the 30th night back and older are pruned at each daily
turn whatever the setting says.

Between those jobs, the rest of each minute waits for Sync now (SB-P6-09, `Core/SyncRequests.cs`): someone in the
app's Sources view asks one MeasurementSource or ImportSource for a batch now (`app_sync`), and the scheduler runs it
within seconds, never beside another batch: one call of the adapter's own `Sync` or `Import` (or its `ISyncNow`
batch: Garmin first asks for its own two-day job, as the Garmin tiles' sync now does, then works one tick), under the
adapter's own bounds and backoff, cut off after five minutes. A request is refused while a batch of that adapter runs
(its own or one asked for), while one asked for waits, and inside the adapter's own limit since the last one asked
for (its `SyncLimit`, else its interval, never under a minute). The limit counts asked-for batches only, so a source
may be read twice within one interval, once on its own clock and once when asked. A batch that runs past the minute
ends that minute's wait: one asked for meanwhile runs in the next minute's wait, after the clock's own jobs, so
requests for one adapter after another never keep those from running. The state and the last outcome live in the
process only; with no request the minute's wait is as it always was. A failed or timed-out batch is logged by kind
(`<adapter> sync now failed (<kind>)`) and never stops the clock. An `ISyncNow` batch its source's own rule turns away
(Garmin's 15 minutes, which the tiles' own sync now also uses; Nightscout's back-off after a failed poll, during
which it asks the site nothing; Nightscout's limit is its `pollSeconds`) reaches nothing and ends `failed`, logged as
`<adapter> sync now not run: the source's own limit refused it`.

## Demo

With `demo.enabled` the demo adapter writes fictional everyday wellbeing (steps, sleep, resting heart rate and an
evening mood, provider `demo`) and notes for the synthetic subject `fixture` (labelled DEMO, `provenance.demo: true`),
and saves two DEMO dashboards and one DEMO insight. "Week at a glance" draws a tall card and takes the `main` corner,
"Mood log" the `recovery` corner, each only while that corner is free and only when its seed version is saved: a
corner someone clears stays clear, and a dashboard someone has changed keeps their version. With the demo off, the
engine's start removes the demo dashboards only the demo saved or wrote through (with their corners) and the demo
insight if nobody gave feedback on it (`DemoAdapter.Retire`). A demo source the seed no
longer has (an older build's fictional CGM, providers `demo` and `demo-live`) is retired, so it leaves the map and the
glucose views. With no glucose data, `app_latest_glucose` and `app_recent_glucose` are not listed.

## Default sources

The `notes`, `csv` and `documents` adapters (`engine/src/Adapters/`) need no account. The example settings turn them on;
what `rhythm init` writes turns on `csv` and `documents`, and `notes` only when given `--notes <folder>`
(`InitCommand`); a `rhythm.yaml` without their sections has them off, as every adapter but `embeddings` is
(`AdapterSettings.Enabled`; `EmbeddingSettings.From` keeps the local model on while its section is absent):
`docs/adapters/notes.md`, `csv.md` and `documents.md`. Compose mounts `RHYTHM_NOTES_DIR` at `/data/notes` and
`RHYTHM_IMPORT_DIR` at `/data/import`, read-only. PDF text comes from PdfPig (Apache-2.0).

## The install commands

`engine/src/Cli/` holds the in-container half of the `rhythm` command (`cli/README.md`): `init`, `connect`,
`disconnect`, `doctor`, and `preflight` and `ready`, which the host wrappers run before and after
`docker compose up`. They run before `rhythm.yaml` is loaded, so they work when it is missing or broken. The image
carries the adapter descriptors (`contracts/adapters/*.json`, in `/app/adapters`) and the examples `init` starts
from (`/app/templates`).

## Adapters

`demo` (`demo.enabled`); `nightscout` (`docs/adapters/nightscout.md`: a live CGM feed polled inside the engine,
with an in-process fake Nightscout in its tests and `scripts/fake-nightscout.mjs` for trying it by hand); `garmin`
(`docs/adapters/garmin.md`: the Personal API and exported files, on the engine's scheduler); `titles`
(`docs/adapters/titles.md`: star titles from a local Ollama server in the `titles` profile); `drive`
(`docs/adapters/drive.md`: files under chosen folders as stars, live Sheet and Doc contents and declared writes for
dashboards, with the install's own Google Cloud project); `second-brain` (`docs/adapters/second-brain.md`: a hosted
memory service read by reference); `calendar` (`docs/adapters/calendar.md`: the events of a person's own private ICS links
as stars, read at map rebuilds, by reference). `engine/tests/upstream/` is a stub of the vendor APIs for running a whole stack
against fakes (`RHYTHM_TEST_UPSTREAM`, tests only). The seams for new adapters are in `docs/dev/adapters.md`.
