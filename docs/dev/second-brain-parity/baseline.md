# Phase 0 baseline

What the repository's own checks and the parity retrieval harness said on 31 October 2026, before any parity code.
Every number here comes from a run on synthetic data in a disposable database; nothing touched an install, a server
or real data. Rerun the commands below to compare a later branch against this page.

- Repository: `main` at `0cb2d44` (the harness branch started there; its own commits add only tests, fixtures and this
  page). Merged into `sb/main` with the ledger, where review added the installed load mode (`d6ea0bf`). Upstream
  reference: Second Brain `228cea49` (4.0.1).
- Toolchain: Node 24.22.1 with npm 11.19.1, .NET SDK 10.0.300 (`engine/global.json`), Docker 29.9.2, test database
  image `pgvector/pgvector:0.8.7-pg17-bookworm` (the compose default).

## 1. Repository checks

Every check in [../conventions.md](../conventions.md), from the repository root, timed end to end. Each was run on
`main` as it was, and again on `sb/main` at `1f3f169`, with the ledger, the harness and the review's changes merged.

| Check | Command | On `main` | Time | On `sb/main` | Time |
|---|---|---|---|---|---|
| Scrub, tracked files | `node scripts/scrub-check.mjs --all` | clean, 475 items | 1.0 s | clean, 489 items | 1.1 s |
| Import manifest | `node scripts/import-manifest.mjs --check` | ok, 239 entries | 0.1 s | ok, 239 entries | 0.2 s |
| Repo script tests | `npm test` | 53 passed | 10.3 s | 53 passed | 10.7 s |
| Compose files | `docker compose -f deploy/compose/compose.yaml config --quiet` | valid | 0.7 s | valid | |
| App install | `npm --prefix app ci` | ok | 30.7 s | not rerun (app unchanged) | |
| App | `npm --prefix app run verify` | 69 test files, 449 tests passed; build; smoke passed in demo and live mode | 89.7 s | not rerun (app unchanged) | |
| Engine build | `dotnet build engine/Engine.slnx` | 0 warnings, 0 errors | 10.4 s | 0 warnings, 0 errors | 6.2 s |
| Engine, no database | `dotnet test engine` | 192 passed, 28 skipped (the database tests, each "NOT RUN") | 4.8 s | 198 passed, 29 skipped | 4.5 s |
| Engine with database | `sh engine/test-db.sh` | 220 passed, 0 skipped | 27.0 s | 226 passed, 1 skipped (the opt-in evaluation) | 30.3 s |

Nothing failed. Notes:

- `npm --prefix app ci` reports 10 npm audit findings (2 low, 8 moderate) and a deprecation warning for `glob@11`.
  Not looked into here; they belong to the app's dependency updates, not to this program.
- `engine/test-db.sh` names its throwaway container `rhythm-engine-test-...` by default, close to an install's own
  `rhythm-*` containers. These runs set `RHYTHM_TEST_CONTAINER` to a name outside that prefix; every container was
  gone afterwards.
- Not run: `node scripts/scrub-check.mjs --history` (only needed before a push, and nothing is pushed) and the whole
  stack on fakes (`engine/tests/upstream/`), whose compose file builds `rhythm-engine:dev` and publishes the default
  app and MCP ports unless they are overridden.
- The harness adds six ordinary tests (fixture integrity, label consistency, map listing in both load modes, metrics)
  and one opt-in test, which is why the counts grow by six passed and one skipped. The app was not rerun on `sb/main`:
  phase 0 changes nothing under `app/`.

## 2. The synthetic corpus and golden queries

As they were at phase 0. They have since grown to 272 queries (207 judged) and gained labelled pairs and the scope
contract's cases; [fixtures.md](fixtures.md) describes them and records the evaluation on the larger sets.

In `engine/tests/Knowledge.Tests/Parity/`, all DEMO data: invented people, places, firms, numbers and
`example.invalid` hosts.

`corpus.json` is a fictional household in Second Brain's vocabulary, independent of how any engine stores it:

- people `alex` and `sam`; scopes `personal:alex`, `personal:sam` and a shared `Family` both read and write;
- 5 projects with aliases; 21 memories with kind (episodic, semantic), status (canonical, draft), importance,
  volatility, tags, author, source channel, project and typed links (`relates_to`, `supersedes`, `caused_by`,
  `follows`, `drawn_from`, `part_of_project`, with explicit, inferred or system provenance);
- two replaced facts with validity windows (a job, an allotment plot), a stale fact with a review date, a draft, a
  standing instruction, a decision with confidence, a due date, a commitment, a memory Alex shared into Family, and a
  controlled prompt injection marked as one that must be held;
- 4 Markdown notes, 4 Drive-linked documents (one with an earlier revision, one manual of about 2 600 characters
  that splits into several passages), 3 fictional measurement series (Alex's resting heart rate and sleep, Sam's
  steps) and 2 dashboards.

`golden-queries.json` has 34 labelled queries over 13 types: exact identifiers (3), rare names (3), paraphrases (6),
graph chains from a seed record (3), as-of (2), current fact after a replacement (2), visibility (5), injection (2),
multilingual and accents (3), long text (2), stale (1), commitment (1) and health (1). Each names the viewer, the
relevant records (grade 2 for the answer, 1 for also relevant), forbidden records, and the capabilities it depends on:
`viewer-scope`, `as-of`, `validity`, `quarantine`.

`ParityCorpusTests` runs in every ordinary test run and checks the fixtures like code: unique ids, every link,
project, card and label resolves, relevant records are visible to their viewer and forbidden ones in visibility
queries are not, replaced facts name a successor that supersedes them, only the two fictional people and `.invalid`
hosts appear, and the metrics match worked examples.

## 3. How Rhythm holds the corpus today

`ParityLoader` writes the corpus into a disposable database through the engine's own write paths, in one of two load
modes (`ParityMode`). They answer different questions, and the evaluation runs both, each in its own database:

- **copies**: every memory, note and Drive document is also saved as a harness `save_document` copy, with its full
  text, chunked and embedded. This is the only path today that feeds both `search_knowledge` and `semantic_search`, so
  it measures what today's tools do with the text in hand. An install holds no such copy unless an AI client saved one.
- **installed**: only what the adapters write themselves: snapshot rows with empty content and no chunks, each under
  the policy of the adapter it stands for (the Second Brain adapter's reference-only policy for memories and projects,
  the notes adapter's archive policy for notes, the Drive adapter's live-source policy for Drive files; the stand-ins
  take those policies from the adapters' own code). It measures what an install's tools find today. Since run 4
  (SB-P2-05) the index job also gives passages, in both modes, to the sources the settings list (by default the
  example settings' documents, notes and memories; `RHYTHM_PARITY_SOURCES` changes it): as installed the notes then
  have passages, the reference-only memories still none (the program's progress log). The tables below are the phase 0
  baseline.

| Corpus records | copies | installed | What is lost in both |
|---|---|---|---|
| memories, notes, Drive documents | a `save_document` copy each (provider `documents`, subject `alex`, `sam`, or `household` for Family; scope private or shared), chunked and embedded when the model is on | no catalog text at all: the map's snapshot rows below are the only rows, with empty content and no chunks; a star's text is read from its source when it opens | kind, status, importance, volatility, tags, author, source channel, project, links, validity, held state, review and due dates, decisions, commitments, share origin, earlier document revisions |
| the same records on the map | a fixture graph source standing in for the second-brain, notes and Drive adapters under one reference-only provider: memories with Second Brain's eligibility (no drafts, retracted or replaced facts) and titles, notes with the notes adapter's overview, typed links as the source's own references (part_of_project becomes `in_project`), one star per project | the same records and links, split into three providers with the three adapters' policies; Drive documents come from a second stand-in that answers the live-source check as the Drive adapter does | in copies mode the adapter leaves the replaced facts and the draft out, but their `save_document` copies still become catalog document stars; in installed mode they are simply absent |
| measurements | an archived CSV source per series with its samples (provider `csv`), which the catalog turns into data stars | the same | nothing for these queries |
| dashboards | not loaded: they live outside the resource catalog and no retrieval path reads them | the same | all |

As installed, full-text search can return only two kinds of record from this corpus: a note (by its title, since the
row's content is empty) and a measurement series. A Second Brain memory's row is reference-only, so the live-source
check drops it (`Core/Retrieval.cs`, `Validate`), and a Drive star's row carries the engine's namespaced catalog id,
which the Drive adapter's live check refuses as not a Drive file id (`DriveAdapter.Current`). `semantic_search` has no
chunks to search.

The map rebuild completes in one pass. Loading and rebuilding took about 0.7 s without the model and 1.2 s with it.

## 4. Retrieval baseline

`ParityEvaluation` runs each golden query through the paths a harness has today, k = 5:

| Path | Tool | How the harness calls it |
|---|---|---|
| fulltext | `search_knowledge` | `Retrieval.Search`, limit 50 |
| semantic | `semantic_search` | `SemanticSearch.Search`, limit 20, chunks of one record counted once |
| graph | `explore_graph` | `RelationshipHost.Explore`, limit 40: from the seed star for a graph-chain query (the seed left out of the ranking), else the query text, depth 1 |

Per query and path it records recall@5, reciprocal rank, nDCG@5, the rank of the first relevant record, forbidden
records returned (**leak**), forbidden records the reader could open anyway through `get_resource` or the map
(**exposed**, checked for queries that need viewer scope or quarantine), and the median latency of three calls after a
warm-up. When a path lacks a capability the query needs, the status is **expected gap** whatever the results say, and
the raw outcome is kept beside it: a lucky pass without the capability is not evidence of it. No path has any of the
four capabilities today. The graph source leaves replaced facts out, as the Second Brain adapter does, but in copies
mode their `save_document` copies still reach the map as catalog document stars, and nothing on the map says a fact
was replaced, so the map has no validity either.

### Results

Each load mode ran twice: with the local model off (no `RHYTHM_MODEL_DIR`), and on
(`minilm-l6-v2-quantized-afdb6f1a-mean-l2-v1`, from model files already on the machine whose checksums matched the
pinned ones; the harness never downloads anything). Full-text and graph numbers below are from the runs with the model
on; without it they are the same except the graph path loses its similarity links (recall@5 0.283, one fewer pass,
the exact quote id only partly found, and in copies mode the replaced plot no longer shows beside the current one).
Repeated runs gave the same numbers apart from latency.

**The numbers to carry forward are the installed ones.** The copies numbers say what today's tools can do when an AI
client has saved a record's text through `save_document`; for notes, Second Brain memories and Drive files that an
install holds through its adapters, paraphrase recall is 0 today on every path but the graph's title match.

#### As installed (what an install finds today)

| Path | Recall@5 | Recall@5 without gaps | MRR | nDCG@5 | Pass | Partial | Miss | Expected gap | Leaked or exposed | p50 | p95 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| fulltext | 0.033 | 0.040 | 0.033 | 0.033 | 1 | 0 | 24 | 9 | 2 of 5 must-hide checks | 1.3 ms | 1.5 ms |
| semantic, model on | 0 | 0 | 0 | 0 | 0 | 0 | 25 | 9 | 2 of 5 (both through `get_resource`) | 2.8 ms | 3.2 ms |
| semantic, model off | unavailable | | | | | | | | | | |
| graph | 0.300 | 0.360 | 0.250 | 0.261 | 9 | 0 | 16 | 9 | 5 of 5 | 6.9 ms | 7.5 ms |

Relevant records found in the top 5, by kind, over the queries each path has every capability for:

| Kind | fulltext | semantic | graph |
|---|---|---|---|
| memory | 0 of 22 | 0 of 22 | 8 of 22 |
| note | 0 of 1 | 0 of 1 | 1 of 1 |
| Drive document | 0 of 6 | 0 of 6 | 2 of 6 |
| measurement series | 1 of 1 | 0 of 1 | 1 of 1 |

Full text finds only the heart-rate series; semantic search finds nothing, since no row has chunks. Two of the must-hide
checks still fail on the catalog paths: Alex's heart-rate series is returned to Sam by full text (and opens for Sam
through `get_resource`), and Alex's private coast path note opens for Sam through `get_resource` (an archive row,
title only). Sam's private party and the injection are not
returned by search here, but both open on the map, as every forbidden record does. The graph path's numbers are the
same as in copies mode: its title and overview match never used the copies.

#### With `save_document` copies (what today's tools do with the text in hand)

| Path | Recall@5 | Recall@5 without gaps | MRR | nDCG@5 | Pass | Partial | Miss | Expected gap | Leaked or exposed | p50 | p95 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| fulltext | 0.333 | 0.400 | 0.333 | 0.333 | 10 | 0 | 15 | 9 | 5 of 5 must-hide checks | 1.8 ms | 2.4 ms |
| semantic, model on | 0.900 | 0.880 | 0.821 | 0.836 | 21 | 2 | 2 | 9 | 5 of 5, and both current-fact queries rank the replaced fact first | 3.1 ms | 4.0 ms |
| semantic, model off | unavailable | | | | | | | | | | |
| graph | 0.300 | 0.360 | 0.250 | 0.261 | 9 | 0 | 16 | 9 | 5 of 5 | 7.2 ms | 8.2 ms |

By query type (recall@5, model on; "gap" means the path lacks a capability every query of the type needs):

| Type | fulltext | semantic | graph |
|---|---|---|---|
| exact-id (3) | 1.00 | 1.00 (first at rank 2 for the ticket and version) | 1.00 |
| rare-name (3) | 1.00 | 1.00 | 0.33 |
| paraphrase (6) | 0.00 | 0.94 | 0.00 |
| graph-chain (3) | 0.00 | 0.78 | 1.00 (first relevant at rank 2, behind the project star; the replaced plot shows as a neighbour of the current one) |
| as-of (2) | gap | gap (old fact first, newer fact also returned as if current) | gap |
| current (2) | gap | gap (the replaced fact ranks first: a leak) | gap (found nothing) |
| visibility (5) | 0.50; 3 gaps, all leaked | 1.00; 3 gaps, all leaked or exposed | 0.00; 3 gaps, all leaked or exposed |
| injection (2) | gap, served as the top result | gap, served at rank 1 and 2 | gap, open on the map |
| multilingual (3) | 0.33 | 0.67 | 0.33 |
| long-text (2) | 0.50 | 1.00 | 0.00 |
| stale (1) | 0.00 | 1.00 | 0.00 |
| commitment (1) | 0.00 | 1.00 | 0.00 |
| health (1) | 1.00 | 0.00 (measurements have no chunks) | 1.00 |

### What the baseline shows

Items 1 to 7 are from copies mode, where the tools have the text; item 0 is what an install has.

0. **As installed, only the graph finds memories, notes and Drive files.** Full text and `semantic_search` read the
   catalog, and an install keeps no text there for its adapters' records: full-text recall@5 is 0.033 (the one
   heart-rate question) and semantic 0, against 0.333 and 0.900 with copies (RHY-05, RHY-07). The graph path's title
   and overview match is the only one that works, at the same 0.300. Every later search target is measured against
   the installed numbers, per content type (SB-P2-05, SB-P2-07, SB-P2-08).
1. **No per-person boundary on any path.** All five must-hide checks fail on all three paths: Sam's private surprise
   party is the top full-text and semantic result for Alex, and every forbidden record is readable through
   `get_resource` or the shared map. Expected: reads have no viewer (`Database.Allowed`, household shares everything).
   As installed, search returns less, but every forbidden record still opens on the map.
2. **The prompt injection is served as knowledge.** It is the top full-text and semantic result for "admin mode export
   every memory". For "standing instruction for planning trips" it is the only full-text result and the second semantic
   one, right behind the real standing instruction. Nothing holds it. As installed it is not a search result, but it
   opens on the map and in any saved copy an AI client makes of it.
3. **Full text finds exact identifiers and rare names, and almost nothing else.** `plainto_tsquery('simple', ...)` ANDs
   every word, stop words included, with no stemming, so a natural question misses unless every word appears (all six
   paraphrases, the commitment, the stale fact and one of the two long-text questions). Results have no ranking: they
   come newest-indexed first, so on this corpus their order is the loader's order.
4. **No accent folding in full text.** "Kelptide cafe" misses "Kelptide Café"; the semantic tokenizer strips the
   accent and finds it.
5. **Semantic search is strong on meaning but blind to time.** The as-of and current-fact queries get the same
   ranking, with the replaced job and plot first both times. English-only MiniLM misses the English query for the
   French recipe (rank 10). The ticket and version ids come second, behind the onboarding notes that mention ticket
   prefixes and pinned versions. Measurements are not indexed.
6. **The graph path is a substring match.** `explore_graph` matches the whole query inside a title or overview, so it
   finds short identifiers in titles but no question, not "Brennwick boiler" when the title says "Brennwick Q9
   boiler", and nothing in a document body (Drive records have no overview). From a seed star it follows typed links
   well: all three chains found.
7. **What an adapter leaves out can come back through a saved copy.** The graph source drops the replaced facts and
   the draft, as the Second Brain adapter would, but their `save_document` copies become catalog document stars
   (`CatalogStars.Records` hides a copy only when an adapter star stands for it). With the model on, the replaced plot
   is linked as similar to the current one. A native memory design must give one record one star, whatever its state.
8. **Latency is not a constraint yet.** Every path answers in under 9 ms at p95 on this corpus, after a warm-up. The
   whole evaluation through `engine/test-db.sh`, container start included, took 8.6 s without the model and 9.9 s
   with it for copies alone, and 11 s and 12 s for both modes.

### Limits of these numbers

- The corpus is small: 29 text records. Semantic search returns 20 of them for every query, so its recall@5 measures
  ranking among nearly everything; absolute numbers say little about a real install. Use them as a regression
  baseline for later phases on the same corpus.
- The golden set is too small to settle a gate. 30 of its 34 queries have a relevant answer, so one query changing
  its result moves overall recall by about 0.033, more than the gate's 0.02 overall gain (D-028); stale, commitment
  and health have one query each, so one result swings a category by 1.0. Upstream's gate also needs at least 200
  queries in 30 clusters and a paired bootstrap interval above 0 (EVL-04). Until the set grows (SB-P2-17), a gate run
  on it is recorded as INCONCLUSIVE, whatever its numbers say.
- No labelled pairs yet: the corpus has typed links but no duplicate, near-duplicate, contradiction, related and
  unrelated pairs, so it cannot tune a similarity threshold or judge map relevance (SB-P2-18).
- Parts of the corpus no query exercises yet: the two dashboards (never loaded, since no retrieval path reads them;
  for SB-P1-06 and SB-P6-03), the Drive quote's earlier revision (dropped by the loader; for story 7, SB-P4-10), and
  the draft (left off the map, found by no query; for the capsule's no-drafts rule, SB-P3-08). They are in the
  fixtures so later phases test on the same corpus.
- Viewers cannot be expressed today: every query runs as one harness principal, which is why visibility is reported
  as leaked or exposed rather than per viewer.
- The as-of date is not passed anywhere: no path takes one.
- The graph path reads records listed by a fixture source that mirrors the Second Brain, notes and Drive adapters'
  listing rules, not by those adapters against their services. Their own behaviour is covered by their tests. In
  installed mode the stand-ins also take the adapters' provider policies, and the Drive stand-in answers the
  live-source check by the Drive adapter's own file-id rule (`ParityCorpusTests` checks both).
- Each latency is the median of three calls on a local machine against a local database; it is not a benchmark.
- Not measured here: search explanations, receipts, stale flags, capsules and briefs (no current path produces them).

## 5. How to run it again

The evaluation never runs in an ordinary test run; it needs `RHYTHM_PARITY_EVAL=1` and the database tests' throwaway
Postgres. From the repository root:

Both load modes run by default, each in its own database; `RHYTHM_PARITY_MODE=copies` or `installed` runs one.

```sh
# Full text and graph only (semantic reported unavailable)
RHYTHM_PARITY_EVAL=1 RHYTHM_PARITY_OUT="$PWD/parity-out" sh engine/test-db.sh --filter ParityEvaluation

# With semantic search: RHYTHM_MODEL_DIR holds the pinned MiniLM files (model_quantized.onnx, vocab.txt)
RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<folder with the two files> RHYTHM_PARITY_OUT="$PWD/parity-out" \
  sh engine/test-db.sh --filter ParityEvaluation
```

Set `RHYTHM_TEST_CONTAINER` to a container name of your choice if you also run an install on the same machine. The
report is `parity-eval.json` (every query, every path: status, raw outcome, metrics, top five, leaks, exposure, gaps,
latency; recall by the relevant record's kind; the loader's record-by-record table) and `parity-eval.md`, the same
numbers for reading, for copies mode, and `parity-eval-installed.json` and `.md` for installed mode. Without
`RHYTHM_PARITY_OUT` they go to `parity-report/` beside the test binaries. The test fails only when a path errors or a
query is not evaluated; low scores are recorded, not failed.

`RHYTHM_PARITY_SCOPES=on` runs the same queries with `memory.scopes` on in the throwaway database, each asked as its
viewer, and fails on any reveal ([section 6](#the-must-hide-checks-with-scopes-on), D-127). Its reports are
`parity-eval-scopes` and `parity-eval-installed-scopes`. Off is the default, and what the gate's locks hold.

## 6. The retrieval gate

`ParityGate` (SB-P2-15) decides whether a change to a retrieval path may replace what the path does now, by rules fixed
before any run (the program's decision log D-028 and D-029), in the shape of Second Brain's locked evaluation: a
pre-registered gate, paired cluster bootstrap intervals and a locked baseline. Each path is judged in each load mode
against the baseline locked for that mode, checked in beside the corpus as `gate-baseline-copies.json` and
`gate-baseline-installed.json`: per query its status, scores, first ten records, must-hide records revealed and missing
capabilities, ids and numbers only.

Terms:

- A **judged** query has a relevant answer and needs no capability that either run's path lacks. A query that needs one
  is an **expected gap**: left out of the gains, but kept in the regression rule where the baseline already answered it.
- A **must-hide check** is a query whose forbidden records a viewer scope or a quarantine should keep from its reader
  (the visibility and injection queries). A record is **revealed** when the path returns it or the reader could open it
  anyway (section 4's leaked or exposed). A replaced fact under a current-fact query is a ranking error, not this.
- Queries that move together share a **cluster**, and the bootstrap resamples whole clusters. The cluster is the golden
  set's own key, as upstream's: a query's first grade-2 record, else its first relevant or forbidden one, unless its
  `cluster` field names one (a replaced-fact query names its chain's current record), [fixtures.md](fixtures.md)
  (`ParityQuery.ClusterKey`). The gate first joined queries that named any record in common; on the grown set that
  gave 66 clusters, the golden key 101. It now uses the key the fixture's own size test counts.

| Rule | Fails when | Inconclusive when |
|---|---|---|
| privacy | the candidate reveals a must-hide record on a check its path claims the capability for, or one the baseline did not reveal | it reveals only what the baseline does, through a capability the path lacks: a leaking path never passes |
| errors | either run has a query error | |
| comparable | | either path did not run, or the load mode, k, corpus or golden data (SHA-256 fingerprint), query ids, labels or embedding model differ (a model change on purpose is declared) |
| power | | fewer than 200 judged queries, 30 clusters, or 10 judged queries in any type: the verdict waits whatever the numbers say, and the deltas are shown as information only |
| regression | any metric overall (recall@5, MRR, nDCG@5), or recall@5 or MRR within a type, loses 0.01 or more, or falls with its whole interval below 0 | |
| improvement | no gain of 0.02 overall, or 0.05 in a declared target type, with the interval's lower bound above 0 | there is no gain, but the comparison could not have seen one of that size (its minimum detectable effect, 2.8 standard errors, is larger), or a target type has under 10 judged queries or 10 clusters |
| latency | the candidate's p95 is over D-029's 500 ms (a reranker's 800 ms is a threshold the run that adds one sets) | |

The verdict is FAIL if any rule fails, else INCONCLUSIVE if any is inconclusive, else PASS. Privacy and errors are
judged first and decide however small or incomparable the sample. Intervals are 95% percentile intervals from 10 000
draws with a fixed seed, so a verdict is the same on every machine. Queries that lost are listed beside the verdict,
outside it.

### The verdict on the phase 0 baseline

The first locks held the phase 0 baseline: `sb/main` at `2c6fce9`, the model on, the same numbers as section 4. Run
against them, every path in both modes was **INCONCLUSIVE**: 25 judged queries in 17 clusters, every type under 10, so
no quality rule was judged; and the must-hide checks still revealed what they did (as installed: 2 of 5 for full text
and semantic search, 5 of 5 for the graph; with copies: 5 of 5 on every path). Two runs gave the same answers as the
locks; only latency moved.

When the corpus and golden set grew (SB-P2-17, [fixtures.md](fixtures.md)), the same code was locked again on the new
data, before any retrieval change, so the second entry of each lock's history is the phase 0 retrieval on 231 queries.
A rerun gave the same answers. Against that lock every path in both modes is still **INCONCLUSIVE**, now on two
counts only: 169 judged queries (below 200: 62 of the 231 are expected gaps) in 90 clusters, and 7 judged visibility
queries (below 10). The must-hide checks reveal what they did on the new data: 11 of 35 as installed for full text and
semantic search, 35 of 35 for the graph and on every path with copies.

The first retrieval change judged by the gate was ranked keyword search (SB-P2-07, with its review applied). Against
the grown-set lock its full text path is **INCONCLUSIVE** in both modes on the same two counts; semantic search and the
graph did not move. For information only, judged recall@5 gained +0.526 with copies and +0.121 as installed, intervals
above 0; as installed no type lost, and with copies rare-name MRR lost 0.031 (one query;
[lexical-search.md](lexical-search.md) has the detail). No must-hide record was revealed that the lock did not. The run
was locked as the third entry of each lock's history, and a rerun matched it.

The review of run 2 grew the corpus and golden set again (D-061, [fixtures.md](fixtures.md)) with the retrieval code
unchanged, and locked it as the fourth entry: INCONCLUSIVE on every path, since the data changed and nothing is compared
with the third. Against that lock the set has its power for the first time (207 judged queries in 103 clusters, every
type with judged queries at least 10), so a rerun gives real verdicts: every path is unchanged, so its regression rule
passes and its improvement rule fails for want of a gain (FAIL, which the opt-in run does not count for a path that
answers exactly as its lock, D-056). The must-hide checks reveal 11 of 38 as installed for full text and semantic
search, 38 of 38 for the graph, and 35 of 38 with copies for full text and semantic search (the former person's
records have no copy to reveal).

### The must-hide checks with scopes on

Every reveal above is through a capability the paths lacked: each query was asked as one household harness token with
scopes off, so every visibility check was an expected gap and nothing measured whether scopes keep a record from its
reader. Since the final review of run 6 the evaluation can ask it (D-127): `RHYTHM_PARITY_SCOPES=on` loads the corpus
in each mode, accepts `memory.scopes` on in its throwaway database as the scope tests do, rebuilds the map with it on,
and asks each query as its viewer, a client bound to alex, one bound to sam, or the household view's with no person for
`family`. Every path then has the viewer-scope capability, so the visibility checks are judged.

Run on `sb/main` after the final review, the model on, both modes: each of the 23 visibility checks that need only a
viewer's scope (8 for Alex, 8 for Sam, 7 for the household view) reveals nothing on any path, neither returned nor open
to its reader: 138 runs, 0 leaked, 0 exposed. Over all 272 queries no path returned or would open a record its viewer
may not see by the corpus's rule. What is still revealed is through capabilities no path has: held records
(quarantine) on the graph in both modes and on every path with copies, and with copies replaced facts (validity) on
full text and semantic search; as installed full text and semantic search reveal nothing.
Recall moves with the reader, since each reads less than the household token did:

| Load mode | full text | semantic | graph |
|---|---|---|---|
| copies, scopes on | 0.832 / 0.752 | 0.795 / 0.713 | 0.241 / 0.216 |
| installed, scopes on | 0.115 / 0.114 | 0.098 / 0.117 | 0.231 / 0.210 |

(recall@5 / MRR over every query, gaps included.) This run is not a lock and changes none: the gate compares scopes-off
runs, which is what every install has.

### How to run it

```sh
RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<folder with the two files> RHYTHM_PARITY_OUT="$PWD/parity-out" \
  sh engine/test-db.sh --filter ParityGateRun
```

Without the model files the run is skipped, saying so (D-041): the locks were taken with the model on.
`RHYTHM_PARITY_MODE` picks a load mode as for the evaluation. `RHYTHM_PARITY_GATE_TARGET=paraphrase` (comma
separated) names the types a candidate claims to help; `RHYTHM_PARITY_GATE_MODEL_CHANGE=1` compares runs of two
embedding models (SB-P2-06). The report is `parity-gate.json` and `.md` for copies mode and `parity-gate-installed.json`
and `.md` for installed mode.

The run fails when a path errors, when any verdict is FAIL, and when a path's answers moved from its lock: a ranking
change is a recorded decision. To record one, run the gate on the change, then rerun with
`RHYTHM_PARITY_LOCK_REASON="<why, with the verdict>"`: the run writes `gate-baseline-<mode>.json` to the report folder,
with the reason added to the file's history, and that file replaces the checked-in one in the same commit as the
change. It refuses to lock a run with an error, a path that did not run, or a must-hide record revealed on a check
its path claims or that the old lock did not reveal. A change to the corpus or the golden queries needs a new lock too
(the fingerprint changes): take it on the code the old lock was taken on, since reveals on new data are not compared
with the old lock's (nor, in the privacy rule, with a baseline on other data), and the change of data is the reason.

The ordinary test run checks the gate's rules on synthetic runs (a planted regression, the floors, privacy, gaps,
target types, the bootstrap against worked examples and its coverage) and that the checked-in locks are present,
whole, and hold nothing but corpus ids, scores and the harness's own words (`ParityGateTests`).
