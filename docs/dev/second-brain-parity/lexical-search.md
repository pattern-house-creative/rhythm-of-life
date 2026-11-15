# Lexical search: ranking and its text configuration

How `search_knowledge` and `search_resources` rank their full-text matches since SB-P2-07, which text search
configuration they use and the measurements that chose it. Synthetic data only, in disposable databases, on
31 October 2026: first on `sb/p2-retrieval` against the phase 0 golden set (34 queries), then on `sb/main` after the
review, against the grown set (231 queries, [fixtures.md](fixtures.md)); again on 2 November 2026 (run 11) on 272
queries, and on rows as long as a saved document ([below](#measured-again-in-run-11)). Compare with
[baseline.md](baseline.md), whose phase 0 numbers are the "legacy" rows here.

## What changed

Before: `plainto_tsquery('simple', query)` required every word of the query, function words included, with no
stemming, and returned matches newest indexed first, unranked ([baseline.md](baseline.md), finding 3).

Now (`engine/src/Core/LexicalSearch.cs`):

- **Words.** The query is split into words; a possessive "'s" and the punctuation around a word are dropped, so are
  single letters and function words: the stop words of the `english` configuration, which it never indexes ("which",
  "the", "is", "now"), and a few modal words it does index but that only add noise ("could", "whose"). A word made only
  of them as Postgres splits it ("don't") goes too; "may" (the month) and "us" stay. At most 12 words.
- **Any word matches.** Each word is matched on its own, in the `english` configuration (stemmed: "collected" finds
  "Bin collection", "renew" finds "renews"). A plain number also matches its signed form, because Postgres reads
  "RHY-4127" as `rhy` and `-4127`, so "4127" alone finds the ticket.
- **Ranked.** Rows are ordered by (1) identifier hits: each identifier-shaped word ("RHY-4127", "v2.14.3", "Q9": a digit
  beside a letter, not an ordinal or a clock hour, or digits in three dotted parts; never a plain number, a date, a time
  or an amount) found whole in the title (2) or the text (1); (2) the words the row holds, each weighted by its rarity
  among the candidates, `log(1 + n / (df + 1))`, so a row with every word comes first and one rare name outweighs a
  common word; (3) `ts_rank_cd` (cover density, normalised by length); (4) newest indexed first; (5) the source's own id
  (provider, external id), so rows a rebuild wrote together come back in one order every time.
- **Bounded.** The candidates scored are the newest 200 rows holding every word plus, for each word, the newest rows
  holding it (400 shared among the words, at least 50 each). Every tier is found through the new index; a rare word's
  rows stay candidates however many rows a common word fills. An empty query, or one with no word left to search for
  (only punctuation, or only function words), runs the query from before, which keeps function words: "the who" still
  finds "The Who tickets".
- **Index.** Migration `021_lexical_search.sql` adds `resources_search_english`, a GIN index on
  `to_tsvector('english', title || ' ' || content)`. The `simple` index from 001 stays for the setting below. Since
  migration 047 (SB-P2-21, [below](#the-stored-text-vector-sb-p2-21)) each row keeps that parse in a stored column,
  `search_english`, with its own index, `resources_search_vector`, and 021's index is dropped.
- **Compatibility.** `engine.legacySearch: true` in `rhythm.yaml` keeps the old query exactly: every word, `simple`,
  newest first ([../../configuration.md](../../configuration.md)). An absent key means `true`, so an existing install
  keeps its search on upgrade; `rhythm init` and the example settings write `false`, so a new install ranks
  (the program's decision log D-053, amended after review: the first version ranked for every install).

The visibility predicate, the live-source check, the limits (50 results; 200 candidates for `search_resources`) and the
returned columns are unchanged. Matching any word, dropping function words and weighting words by rarity are adapted from
Second Brain's keyword arm (`src/recall/search.ts`, `src/recall/fts.ts`, `src/text/tokenize.ts` at `228cea49`); no code
is copied, and `NOTICE` credits it (D-005). The function words are Postgres's own list, not upstream's.

## How it was measured

The phase 0 evaluation with one addition: `RHYTHM_PARITY_LEXICAL` lists full-text variants to run as paths of their own
(`fulltext:<variant>`) beside the engine's own `fulltext` path, in the same databases and on the same queries.

```sh
RHYTHM_PARITY_EVAL=1 RHYTHM_PARITY_LEXICAL=legacy,simple,english,simple_unaccent,english_unaccent \
  RHYTHM_PARITY_OUT="$PWD/parity-out" sh engine/test-db.sh --filter ParityEvaluation
# and again with RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files>
```

- `legacy`: the query from before ranking.
- `simple`, `english`: ranked, in Postgres's built-in configurations.
- `simple_unaccent`, `english_unaccent`: ranked, with accents folded by the `unaccent` dictionary first. The evaluation
  creates these two configurations in its own throwaway database; no migration does.

Each ran in both load modes (installed and with copies), with the local model off and on: four runs, each with every
variant, and a fifth with the documented command alone (no variants) to confirm the engine's own path. Full-text
numbers do not depend on the model; they were the same in every run, apart from latency.

## Results on the phase 0 set

Measured on `sb/p2-retrieval` before the review, on the 34 phase 0 queries. The current numbers, after the review and
on the grown set, are in the next section.

### Overall (recall@5 / MRR / nDCG@5; pass, partial, miss; leaked or exposed)

| Variant | With copies | As installed |
|---|---|---|
| legacy (the phase 0 baseline) | 0.333 / 0.333 / 0.333; 10, 0, 15; 5 | 0.033 / 0.033 / 0.033; 1, 0, 24; 2 |
| simple, ranked | 0.800 / 0.717 / 0.712; 19, 2, 4; 7 | 0.033 / 0.033 / 0.033; 1, 0, 24; 2 |
| **english, ranked (chosen)** | **0.944 / 0.836 / 0.860; 24, 1, 0; 7** | **0.067 / 0.067 / 0.067; 2, 0, 23; 2** |
| simple_unaccent, ranked | 0.800 / 0.717 / 0.712; 19, 2, 4; 7 | 0.033 / 0.033 / 0.033; 1, 0, 24; 2 |
| english_unaccent, ranked | 0.944 / 0.836 / 0.860; 24, 1, 0; 7 | 0.067 / 0.067 / 0.067; 2, 0, 23; 2 |

Nine queries are expected gaps on every variant (as-of, current, three visibility, two injection: capabilities no
lexical path has), as in the baseline.

### By query type, english against legacy (recall@5, MRR)

| Type | With copies | As installed |
|---|---|---|
| exact-id (3) | 1.00, 1.00 → 1.00, 1.00 (first at rank 1 for all three) | 0 → 0 |
| rare-name (3) | 1.00, 1.00 → 1.00, 1.00 | 0 → 0 |
| paraphrase (6) | 0, 0 → 1.00, 0.72 | 0 → 0 |
| graph-chain (3) | 0, 0 → 0.78, 0.53 | 0 → 0.33 (the planting note, by its title) |
| as-of (2) | gap | gap |
| current (2) | gap; now returns both facts | gap |
| visibility (5) | 0.50, 0.50 → 1.00, 1.00 | 0 → 0 |
| injection (2) | gap | gap |
| multilingual (3) | 0.33 → 1.00 | 0 → 0 |
| long-text (2) | 0.50 → 1.00 | 0 → 0 |
| stale (1) | 0 → 1.00 | 0 → 0 |
| commitment (1) | 0 → 1.00 | 0 → 0 |
| health (1) | 1.00 → 1.00 | 1.00 → 1.00 |

No type loses recall or MRR in either mode. `semantic_search` and `explore_graph` numbers are the phase 0 ones,
unchanged.

### Leaks and exposure

- With copies, "leaked or exposed" goes from 5 to 7. The two new ones are the current-fact queries ("where does alex
  work?", "which allotment plot does sam have?"): ranked search now finds the current fact and also returns the replaced
  one, as `semantic_search` already does. Nothing on any path knows a fact was replaced (the validity capability,
  phase 3 and 4); both queries stay expected gaps.
- The five must-hide checks fail as before: 5 of 5 with copies, 2 of 5 as installed. One changes kind as installed:
  for "sabbatical coast path", Alex's private coast path note was exposed (opened through `get_resource`) and is now
  also returned, because one of its title words matches. Every principal reads every row today (household shares
  everything, SEC-03); per-person scopes are phase 1.
- As installed, a question can now return measurement series whose title holds one of its words ("alex"). They are not
  forbidden, only noise; the rarity weighting puts them after better matches when there are any.

### Latency

On the 29-record corpus, ranked full text answered in about 2 to 11 ms at p50 and 4 to 23 ms at p95, against 1 to 5 ms
and 3 to 6 ms for legacy, over the five runs; the same path's numbers moved by up to two times between runs as the
machine's load changed (the unchanged graph path did too). Well inside D-029's 500 ms.

A larger synthetic catalog, generated in a throwaway test that is not part of the repository, gave the cost at scale
(median of five calls, limit 20, one run of each; plans vary with the planner's choices, and two earlier runs with a
first draft of the candidate tiers, a single any-word tier of 500, took 36 ms to 1.1 s on the first catalog):

| Catalog | legacy | ranked |
|---|---|---|
| 5 000 rows of about 300 random words (about 2 KB) | 1.5 to 3.8 ms | 2 ms for a missing id; 36 to 54 ms for questions and common words |
| 2 000 rows of about 2 000 words (about 16 KB) | 1.6 s for every query (the planner read the whole table) | 1.6 s for a missing id; 180 to 360 ms for questions and common words |

Ranked search read each candidate's text again to score it (there was no stored text vector then), so its cost grew
with the candidates' length, at most 800 candidates. An install's catalog today holds little text: adapter rows are
titles only, so this was a cost for saved documents and uploads. Migration 047 stores the parse (SB-P2-21, below).

## The review, and the numbers on the grown set

A review of the slice (31 October 2026) reproduced the numbers above exactly and found these, all fixed on `sb/main`
before the merge's checks:

- **A query made only of function words found nothing.** `english` never indexes its stop words, so "the who" matched
  no row where the old query found "The Who tickets"; queries made only of "now", "after" or "very" did the same. The
  function words are now exactly the configuration's stop words plus the few modal words above, a database test checks
  every one against the configuration, and a query left with no other word runs the query from before.
- **The identifier boost covered dates, times and amounts** ("2026-10-31", "10:30", "12.50"), so a row holding only a
  date could outrank rows holding every other word. Identifiers are now as described above, with tests for each shape.
- **Ties changed order between rebuilds.** Rows a rebuild writes together share their indexed time, and a row's id is
  random, so the gate's lock moved from run to run; ties now fall to the source's own id.
- **Smaller:** the Drive adapter's re-read on a model change (SB-P2-02) gained its own test; migration 021's comment
  named the wrong page (021 has been applied on no install, so it was corrected); `NOTICE` gained the attribution; and
  "exact ids first" holds with copies only (the limits below).

Then the evaluation ran on the grown golden set (231 queries in 101 clusters, 105 text records), on `sb/main`, in both
load modes, with `RHYTHM_PARITY_LEXICAL=legacy,simple,english,english_unaccent` and the model off, and again with the
model on and no variants. Full-text numbers were the same in both runs; `semantic_search` and `explore_graph` matched
[fixtures.md](fixtures.md)'s, unchanged.

### Overall, 231 queries (recall@5 / MRR / nDCG@5; pass, partial, miss; leaked or exposed)

| Variant | With copies | As installed |
|---|---|---|
| legacy (the grown-set baseline) | 0.243 / 0.248 / 0.240; 46, 4, 119; 35 | 0.027 / 0.027 / 0.026; 4, 1, 164; 11 |
| simple, ranked | 0.792 / 0.704 / 0.699; 127, 15, 27; 47 | 0.124 / 0.113 / 0.108; 21, 3, 145; 11 |
| **english, ranked (the engine's)** | **0.810 / 0.716 / 0.715; 130, 15, 24; 47** | **0.136 / 0.125 / 0.119; 23, 4, 142; 11** |
| english_unaccent, ranked | 0.815 / 0.721 / 0.720; 131, 15, 23; 47 | 0.136 / 0.125 / 0.119; 23, 4, 142; 11 |

62 queries are expected gaps on every variant (as-of, current, injection and 20 visibility checks).

### By query type, english against legacy (recall@5 / MRR)

| Type | With copies | As installed |
|---|---|---|
| exact-id (17) | 0.882 / 0.941 → 1.000 / 1.000 | 0 → 0 |
| rare-name (16) | 1.000 / 1.000 → 1.000 / 0.969 | 0 → 0 |
| paraphrase (32) | 0 → 0.581 / 0.463 | 0 → 0.156 / 0.167 |
| graph-chain (18) | 0 → 0.500 / 0.468 | 0 → 0.194 / 0.194 |
| visibility (7 judged) | 0.519 / 0.500 → 1.000 / 0.944 | 0.167 / 0.222 → 0.167 / 0.222 |
| multilingual (18) | 0.361 / 0.389 → 0.583 / 0.569 | 0 → 0 |
| long-text (23) | 0.130 / 0.130 → 1.000 / 0.946 | 0 → 0.174 / 0.130 |
| stale (12) | 0 → 1.000 / 0.958 | 0 → 0 |
| commitment (12) | 0 → 1.000 / 1.000 | 0 → 0 |
| health (14) | 0.286 / 0.250 → 0.857 / 0.746 | 0.286 / 0.250 → 0.857 / 0.679 |

`simple` beats `english` on paraphrase (0.607) and health (0.929) with copies; `english` is ahead overall, on stale,
commitment, graph-chain and multilingual, and as installed. `english_unaccent` adds one multilingual query with copies
(0.639) and nothing as installed: +0.005 overall, under the gate's 0.02 margin.

### The gate's verdict

Against the grown-set lock of the code before ranking ([baseline.md](baseline.md) section 6), full text is
**INCONCLUSIVE** in both load modes: 169 judged queries in 90 clusters and 7 judged visibility queries are under
D-028's floor. For information only, judged recall@5 gains +0.526 with copies (interval +0.455 to +0.600) and +0.121 as
installed (+0.068 to +0.183). As installed no type loses. With copies five queries lose:

- `q-rare-milers` ("Marram Milers") drops from rank 1 to 2, so rare-name MRR falls 0.031. Sam's private guest list
  holds both words, ranks first on cover density, and is a record Alex may not see; the old order put Alex's record
  first only because it was newer. Viewer scopes (phase 1) remove it. Once the set is large enough, D-056's 0.01 type
  tolerance would fail this; upstream's max(0.03, 1/n) would not.
- Four lose nDCG only, the first answer still at rank 1: a grade-1 record (a replaced fact or a second source) now comes
  before the grade-2 one (`q-rare-pebblekin`, `q-rare-ravenmoor`, `q-rare-thistlecombe`, `q-vis-quote-family`).

With copies, leaked or exposed goes from 35 to 47: the 12 current-fact queries now find the current fact and return the
replaced one beside it, as `semantic_search` does (an expected gap until validity, phase 4). The must-hide checks reveal
what they did: 35 of 35 with copies, 11 of 35 as installed. The new lock records this run as the third entry of each
lock's history.

### Latency, 231 queries

Ranked full text took 18.6 ms at p50 and 28.9 ms at p95 with copies (legacy 4.0 and 4.5 ms), 4.5 and 6.4 ms as installed
(legacy 1.4 and 1.6 ms), on a development machine; the gate's run with the model on gave 30.2 and 6.3 ms at p95. Inside
D-029's 500 ms.


## The choice

`english`: the best on every measure in both modes. With copies, ranking alone (`simple`) still misses four queries
that stemming finds: the rubbish collection, the quote and plot chains, and the English question about the French
recipe; as installed, only stemming finds the planting note ("planned" against "Planting plan").

Not `unaccent`: no query of the phase 0 set separated `english_unaccent` from `english`, and on the grown set it adds
one multilingual query with copies and nothing as installed (above). "Kelptide cafe" now finds
"Kelptide Café" through "kelptide" alone, so the only accent query passes either way. Folding would still help a query
whose only distinctive word carries the accent ("cafe on the harbour" against "Café"), but the program adopts changes on
measured evidence (D-028) and folding needs the `unaccent` extension in every install's database. Measure it again once
the golden set has more such queries; the variant stays in the evaluation for that (the program's decision log
D-054).

### On the set grown past the floor

The review of run 2 grew the golden set to 272 queries, 207 judged ([fixtures.md](fixtures.md)), past D-028's floor.
The evaluation there (model on, `RHYTHM_PARITY_LEXICAL=legacy`) gives the same picture: with copies ranked full text
reaches 0.814 recall@5 against legacy's 0.263, and loses one query, `q-rare-milers` again (rare-name MRR 0.976 against
1.000, 21 queries); as installed it reaches 0.115 against 0.023 and no type loses. D-056's type rule (a loss of 0.01)
now applies, so with copies ranked search does not meet SB-P2-07's "no category regresses": a type regression, computed
from the evaluation's per-type numbers (no lock of the legacy query exists on this data, so no gate run compares the
two). Viewer scopes (phase 1) remove the record that outranks the answer. Ranked full text took 22.6 ms at p50 and
38.6 ms at p95 with copies, 2.5 and 6.2 ms as installed.

## Measured again in run 11

On `sb/main` at `f86c9c1` (2 November 2026, the code runs 8 to 10 deployed), on the 272-query set (207 judged in 103
clusters), the model on, in both load modes: the gate with the legacy query as a path of its own
(`RHYTHM_PARITY_LEXICAL=legacy`, `RHYTHM_PARITY_GATE_COMPARE=fulltext=fulltext:legacy,combined=fulltext:legacy`; the
variant has no lock, so that run fails its tripwire by design, while the four engine paths answered exactly as their
locks), the evaluation with `RHYTHM_PARITY_LEXICAL=legacy,simple,english,english_unaccent`, the evaluation with
`RHYTHM_PARITY_SCOPES=on` and `legacy`, and a gate run with `simple` (below). Ranked search's code has not changed since
run 2, and its numbers are the ones recorded in run 2's review and every run since. So SB-P2-07 stays partial, and every
install whose settings predate ranking keeps the old query (D-053).

### Ranked against the legacy query (recall@5 / MRR)

| Type (queries) | With copies: legacy → ranked | As installed: legacy → ranked |
|---|---|---|
| exact-id (22) | 0.909 / 0.955 → 1.000 / 1.000 | 0 → 0 |
| rare-name (21) | 1.000 / 1.000 → 1.000 / **0.976** | 0 → 0 |
| paraphrase (40) | 0 → 0.571 / 0.488 | 0 → 0.125 / 0.133 |
| graph-chain (20) | 0 → 0.475 / 0.425 | 0 → 0.175 / 0.175 |
| visibility (35) | 0.548 / 0.536 → 1.000 / 0.964 | 0.107 / 0.143, unchanged |
| multilingual (21) | 0.357 / 0.381 → 0.595 / 0.583 | 0 → 0 |
| long-text (26) | 0.115 / 0.115 → 1.000 / 0.952 | 0 → 0.154 / 0.135 |
| stale (14) | 0 → 1.000 / 0.964 | 0 → 0 |
| commitment (15) | 0 → 1.000 / 1.000 | 0 → 0 |
| health (16) | 0.250 / 0.219 → 0.875 / 0.778 | 0.250 / 0.219 → 0.750 / 0.594 |
| overall (272) | 0.263 / 0.267 → 0.814 / 0.735 | 0.023 / 0.023 → 0.115 / 0.108 |

As-of, current and injection are expected gaps on both. The gate, ranked against legacy:

- **With copies: FAIL**, on the regression rule alone: rare-name MRR −0.024, interval [−0.079, +0.000], against D-056's
  tolerance of 0.01. Judged recall@5 gains +0.515 [+0.454, +0.580] and MRR +0.471 [+0.403, +0.541]; no other type loses.
  Privacy is inconclusive, as for every path with scopes off: 35 of 38 must-hide checks reveal through the household
  model, as the legacy query's do.
- **As installed: INCONCLUSIVE**, for privacy alone (11 of 38, the household model). No query loses anything; judged
  recall@5 gains +0.099 [+0.054, +0.152].
- Combined retrieval against legacy gives the same two verdicts, for the same query (D-151).

p95 latency in that run: ranked 30.4 ms with copies and 5.9 ms as installed, legacy 4.4 and 1.4 ms.

### What fails, exactly

One query, `q-rare-milers` ("Marram Milers", asked as Alex). Three records hold both words: Sam's private guest list for
Alex's surprise party (`mem-sam-party-guests`, not relevant: Alex may not see it), Alex's running memory
(`mem-alex-running-new`, grade 2) and Alex's race plan (`note-alex-race-plan`, grade 1). Ranked search finds no
identifier and the same rarity in all three, so cover density decides, normalised by length, and Sam's record is the
shortest: it comes first and Alex's memory second (MRR 0.5). The legacy query put the race plan first because it was
indexed last (MRR 1). The four other losers lose nDCG only, the first answer still first (`q-rare-pebblekin`,
`q-rare-ravenmoor`, `q-rare-thistlecombe`, `q-vis-quote-family`: a grade-1 record now before the grade-2 one).

Nothing in the words separates the three records; who may read Sam's does. Neither length nor date would be a fix: Sam's
record is also the newest by its own date (20 October against 6 January), and tuning the length normalisation for one
query would fit the golden set rather than rank better.

### What would fix it

- **Per-person scopes.** With `memory.scopes` on, Sam's record is not served to Alex, and ranked search loses nothing to
  legacy in either mode: rare-name MRR 1.000 for both; overall recall@5 / MRR 0.832 / 0.752 against 0.263 / 0.269 with
  copies, 0.115 / 0.114 against 0.023 / 0.025 as installed; nothing revealed (the evaluation's scopes-on run; it is not
  a gate verdict, since the locks are scopes off). Scopes stay off by default (D-131); a real install turns them on only
  with the owner (D-013).
- **Upstream's per-type tolerance.** D-056 reads D-028 as a loss of 0.01 in any type and says the owner may prefer
  upstream's max(0.03, 1/n): 0.048 for 21 rare-name queries, which this loss is inside.
- **The owner accepting the loss** (D-151): one query of 21 at rank 2 instead of 1, against +0.515 judged recall@5.
- **Not `simple`.** Ranked in the `simple` configuration, the length normalisation moves with the stop words it keeps
  and happens to put Alex's memory first, so against legacy no type loses in either mode (the gate: INCONCLUSIVE in
  both, for the household model's reveals only; judged recall@5 +0.490 with copies, +0.087 as installed). Against
  `english`, what new installs run, the gate FAILs it in both modes: with copies overall recall@5 −0.026, graph-chain
  −0.100 and stale −0.143, with losses in visibility, multilingual, commitment, health, as-of and current; as installed
  overall −0.012, paraphrase −0.038 and graph-chain −0.050, with long-text and health MRR. A worse ranking that passes
  against legacy by one query's luck, so it is not taken.

### The configuration on 272 queries

`english` stays (D-054). Overall recall@5 / MRR with copies: `simple` 0.788 / 0.726, `english` 0.814 / 0.735,
`english_unaccent` 0.818 / 0.739 (one multilingual query more: 0.643 against 0.595); as installed `simple` 0.104 / 0.095
and the other two 0.115 / 0.108. Accent folding still gains less than the gate's 0.02 and needs an extension in every
install's database.

### Exact ids first

- With copies: 22 of 22 first (exact-id MRR 1.000; legacy 0.955, missing one, `q-exact-ctx-waveform`).
- As installed: 0 of 22 for `search_knowledge`, as legacy: its rows for Second Brain memories and Drive files hold no
  text, and a note's star holds only its title. Combined retrieval's keyword arm, which reads passages and memory text
  since SB-P2-19, finds 1 of 22 with the example's sources (MRR 0.045) and 7 of the 8 whose text Rhythm holds with
  `drive` listed ([combined-retrieval.md](combined-retrieval.md)); the other 14 are Second Brain memories, which wait
  for the importer. `search_knowledge` itself reads no passages, so this criterion stays with combined retrieval
  (SB-P2-08, SB-P2-19), not ranking.

### Rows as long as a saved document (D-054)

`LexicalScaleRun` (opt-in: `RHYTHM_LEXICAL_SCALE=1 sh engine/test-db.sh --filter LexicalScaleRun`) fills a throwaway
database with DEMO rows of 40 000 characters, what `save_document` accepts, made of 20 736 invented words of four
syllables drawn with a strong skew (a few words in every row, the rest in ever fewer, the rarest in about one row in
twenty), each row cut at a word's end, with an id and a name planted in a few; and times `search_knowledge`'s own path
in both modes (median of five calls after one warm-up, limit 20). The word a third of the rows hold is checked to be a
vocabulary word in a quarter to 45% of them. Run at the integration of run 11 (2 November 2026, `sb/main` after the
slice merges), on this data:

| Query | 100 rows: legacy | ranked | 1 000 rows: legacy | ranked |
|---|---|---|---|---|
| an id a few rows hold | 161 ms | 214 ms | 1.4 ms | 2 157 ms |
| an id no row holds | 161 ms | 209 ms | 1.0 ms | 2 155 ms |
| a name a few rows hold | 154 ms | 221 ms | 1.3 ms | 2 137 ms |
| a word a third of the rows hold | 160 ms | 279 ms | 2.9 ms | 2 855 ms |
| the two commonest words | 150 ms | 854 ms | 1 512 ms | 6 817 ms |
| a question, five words to search for | 150 ms | 1 486 ms | 1 491 ms | 13 012 ms |

Ranked search is over D-029's 500 ms for the two many-word queries at 100 such rows, and for every query at 1 000 (2.1
to 13.0 s). The legacy query is within it at 100 rows and, at 1 000, for an id, a name or a word a third of the rows
hold (the planner takes the text index), and over it for the commonest words (1.5 s), which most rows hold. The plans
say why ranked search is slow: it reads the whole table rather than the text index, parsing every row's 40 000
characters (about 2 ms a row) once per candidate tier (up to one tier per word plus one for all of them), and then
parses its candidates again to score them; the planner cannot know that `to_tsvector` on a long text is costly. In the
same run, the same ranked query reading a stored text vector with its own index (a generated column added in that
throwaway database only, the test checking that only the parse was replaced and that it answers the same rows in the
same order) took 1.0 to 26 ms at 100 rows and 0.9 to 150 ms at 1 000. The first version of this run (the slice's, at
`5f5a2c8`) drew 1 728 words of three syllables, so nearly every word was in most rows, and cut each row inside a word,
whose fragment was then picked as "a word a third of the rows hold"; its numbers (ranked 1.1 to 1.7 s for the many-word
queries at 100 rows and 2.5 to 15.8 s at 1 000, legacy 1.8 s at 1 000 for every query) showed the same verdict for
ranked search, and are replaced by the table above.

So D-054's follow-up, the stored column, is what ranked search on long saved documents needs, and it is backlog item
SB-P2-21: a migration that adds it to `resources` (a table rewrite on every install, and room for the vectors), with
ranked search and combined retrieval's catalog tier reading it. That migration must also replace 025's
`guard_memory_resources` trigger, which lets the engine login update a native memory's own `resources` row only when
nothing but `indexed_at` changes (`to_jsonb(NEW)-'indexed_at'` against `to_jsonb(OLD)-'indexed_at'`): in a BEFORE
trigger a stored generated column is still null in `NEW` (the review checked it on a throwaway Postgres 17), so that comparison would
fail and refuse the update `EngineLoginTests` relies on; the column has to be left out of both sides. It should also
decide whether 021's expression index `resources_search_english` is dropped once the column has its own, so a rewrite
does not keep two copies. The legacy query (another configuration) would need a column of its own to gain, and its cost
on common words is not new. The catalog rows of today's adapters are titles only, so the cost falls on saved documents
and uploads.

This is not a later merge's risk: ranked search is on `main` since run 2's merge, and every new install runs it, since
`rhythm init` and the example settings write `legacySearch: false` (D-053); an install whose settings predate ranking
keeps the old query. So a new install that saves long documents has these latencies today. Q-156 takes the default
(new installs keep ranked search; SB-P2-21 is built before any further merge into `main`), and D-054 is amended to say
so.

### The stored text vector (SB-P2-21)

Migration 047 (`047_search_vector.sql`, run 12, provisional) adds `resources.search_english`, a stored generated column
holding exactly the parse ranked search made at every search, `to_tsvector('english', title || ' ' || content)`, with
a GIN index of its own (`resources_search_vector`). The catalog tiers match it and scoring reads it
(`LexicalSearch.Stored`), for `search_knowledge` and `search_resources` and for combined retrieval's catalog tier,
where a catalog row's own text (its part -1, not a memory) is that parse; a native memory's text and a record's
passages are still parsed at the search (their own indexes from 040). Another configuration (the evaluation's
variants) and the legacy query (`simple`, `engine.legacySearch`) parse as before; the legacy query's own column, which
would help it only for the commonest words, is decided apart. 021's expression index is dropped: nothing reads that
expression now, and keeping it would parse every row twice on each write and keep two copies. An engine from before
047 still answers on this schema, as slowly as before 021.

025's `guard_memory_resources` compares a native memory's whole `resources` row before and after an update by the
engine's login, allowing only `indexed_at` to move; in a BEFORE trigger a stored generated column is still null in
`NEW`, so 047 replaces it to leave `search_english` out of both sides (it derives from `title` and `content`, which it
still compares). Without that, `EngineLoginTests` fails (checked: 42501 on the login's `indexed_at`-only update).

Answers are unchanged: `LexicalSearchDatabaseTests` checks the column equals the parse on every row and after a
change, and that ranked search answers the same rows in the same order reading it or parsing; `LexicalScaleRun` checks
the same at scale; the parity evaluation in both load modes with the model on gives run 11's numbers (copies combined
recall@5 / MRR 0.844 / 0.754, installed 0.148 / 0.158; with scopes on as before; no leak), and the gate finds every
path's answers unchanged against both locks, p95 within the budget, with no re-lock.

`LexicalScaleRun` on the same synthetic data as run 11 (1 000 rows of 40 000 characters, run 12, a development
machine; median and slowest of five calls after a warm-up, limit 20; "parsed" is the same ranked query parsing each
row at the search, as before 047; "combined" is combined retrieval's keyword arm with no model):

| Query | legacy | ranked | ranked, parsed | combined |
|---|---|---|---|---|
| an id a few rows hold | 1.3 / 1.9 ms | 42 / 50 ms | 2 215 / 2 325 ms | 7.7 / 8.1 ms |
| an id no row holds | 1.2 / 2.1 ms | 45 / 49 ms | 2 273 / 2 435 ms | 2.1 / 2.4 ms |
| a name a few rows hold | 1.2 / 1.2 ms | 42 / 43 ms | 2 173 / 2 217 ms | 4.3 / 4.7 ms |
| a word a third of the rows hold | 2.7 / 3.1 ms | 67 / 71 ms | 2 979 / 3 507 ms | 40 / 44 ms |
| the two commonest words | 1 629 / 1 878 ms | 157 / 162 ms | 7 147 / 7 607 ms | 156 / 167 ms |
| a question, five words to search for | 1 582 / 1 609 ms | 278 / 279 ms | 13 532 / 13 561 ms | 82 / 86 ms |

So every ranked query is within D-029's 500 ms at 1 000 such rows (42 to 279 ms; combined 2 to 167 ms), where parsing
at the search took 2.2 to 13.5 s. The plans show ranked search still reads the table for a selective query, now only
each row's stored vector (about 40 ms at 1 000 rows), where the legacy query takes its index; the run fails if any
ranked or combined call is over 500 ms. That rule is what the runs show, not one run's number: other runs of the same
test in run 12 (the slice's again, and the final review's) put the slowest ranked call at 282 and 285 ms.

The migration itself rewrites `resources` on every install, every row parsed once: on these 1 000 rows of 40 000
characters (68 MB with the new index, which is 7.6 MB) the whole of 047 took 9.5 s, timed by running its file on the
same rows with the column taken away and 021's index put back (9.1 and 8.7 s in the other runs: 8.7 to 9.5 s in all). An
install's catalog today is mostly adapter rows that are titles only, so its rewrite is much shorter; one that has saved
many long documents takes about that long per 1 000 of them, while the migration holds the table (the engine is not
serving then: migrate runs before it starts).

## Limits

- On the 231-query set every verdict here was INCONCLUSIVE by the gate's rule (D-028, D-056): 169 judged queries and 7
  judged visibility queries. The set now has 207 judged queries (above), and with copies the rare-name type fails the
  0.01 tolerance, as it did again in run 11, so SB-P2-07 is partial (the program's backlog).
- Before migration 047 scoring read each candidate's text again, and on rows as long as `save_document` accepts ranked
  search was over D-029's 500 ms (run 11, above). With 047's stored text vector (SB-P2-21, run 12, above) every ranked
  query is within it at 1 000 such rows (at most 279 to 285 ms across run 12's runs); legacy is still over it for the
  commonest words at 1 000 (1.6 s), which the legacy query's own column would need (decided apart).
- As installed, full text still reaches only notes (by title) and measurement series: Second Brain memories and Drive
  records are dropped by the live-source check, and adapters keep no text in the catalog. So SB-P2-07's "exact-id golden
  queries first" holds with copies (22 of 22 first in run 11) and not as installed (0 of 22, as legacy): that check
  moves to SB-P2-05, SB-P2-08 and SB-P2-19, not ranking. Only `LexicalSearchDatabaseTests`, not the golden set, shows
  the identifier boost changing an order.
- `english` stems every text as English. Other languages still match word for word (both sides are stemmed alike), but
  English stop words are dropped from them too.
- Not measured: a substring fallback (RET-05) and match levels (RET-06); neither was built.
