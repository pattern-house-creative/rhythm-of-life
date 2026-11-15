# Parity fixtures

The synthetic corpus, the golden queries and the labelled pairs in `engine/tests/Knowledge.Tests/Parity/`, and the
rules every label follows. They grew from the phase 0 set ([baseline.md](baseline.md) section 2) to upstream's minimum
sizes (SB-P2-17) and gained a labelled-pair file (SB-P2-18). Everything in them is DEMO data: two invented people,
invented places, firms, numbers and `example.invalid` hosts. `ParityCorpusTests` checks every rule below in every
ordinary test run, so a label cannot drift away from the records it names.

## What the files hold

| File | Holds |
|---|---|
| `corpus.json` | the household: people `alex` and `sam`, and a former person `jo`; scopes `personal:alex`, `personal:sam`, the shared `Family`, `personal:jo` (read by no one), `household:private` (the Second Brain personal workspace with owner household, read as Family) and `team:wickmere-bees` (a team workspace, read as Family); three viewers (the two people, and `family`: a reader with no person, as the app principal behind a shared screen, who sees the Family scope only, SB-P1-05, D-046); 11 projects; 98 memories, 11 notes, 11 Drive-linked documents, 9 measurement series and 6 dashboards; and, not loaded, 3 insights, 3 insight runs, 2 pins, 2 corner cards and one scripted share (D-061) |
| `golden-queries.json` | 272 labelled queries over 13 types, asked as Alex, Sam or the Family view; 207 are judged on today's paths (a relevant answer, no capability needed), in 103 clusters |
| `pairs.json` | 89 labelled pairs over the corpus and 27 incoming saves: duplicate, near-duplicate, contradiction, related, unrelated and insight, with the labelling rules written into the file |
| `standing-cases.json` | 22 standing instructions (the corpus's own and 21 outside the corpus, so the gate's answers are untouched), with 88 questions that should fire, 66 near misses and 44 asked about their subject with another purpose ([below](#standing-cases), SB-P5-12) |
| `sb-export-brain.json` | a fictional Second Brain 4.0.1 deployment as upstream's own rows, and the three exports taken from it ([below](#second-brain-export-fixtures), SB-P8-02) |
| `sb-export-alex.v3.json`, `sb-export-sam.v3.json`, `sb-export-alex.v2.json` | what upstream's `GET /export` returns to Alex and to Sam today, and an older backup of Alex's |

The corpus covers what the plan asks the program to handle:

- **Time.** 10 supersession chains (a job each, an allotment plot, the car, the electricity tariff, home insurance, a
  choir's rehearsal night, a running club, a commute, the cat's food), each with validity windows and a `supersedes`
  link; two Drive documents with an earlier revision (the heat pump quote, the kitchen quote).
- **Review.** 10 stale facts with a review date in the past, 10 commitments with who owes what to whom by when, a
  decision with a confidence, dated events, a draft.
- **Privacy.** Secrets each person keeps from the other (Sam's surprise party for Alex's 40th, with its deposit and
  guest list; Alex's present for Sam), private health series and notes, a memory Alex shared into Family, and dashboards
  whose cards their readers may all see.
- **Held content.** 6 controlled prompt injections, each marked as one that must be held: an admin-mode export, a
  forum clipping that asks for Alex's health data, a forwarded newsletter claiming the household's authority to delete,
  a recipe with zero-width characters hiding an instruction, a newsletter steering deposits to an outside site, and a
  shopping list that tries to move every Family memory into one person's scope.
- **Hard text.** 10 records of more than 2000 characters (Drive documents, notes and memories); French, German,
  Spanish, Italian and Japanese records; accented names; short codes and identifiers; one stored duplicate and one
  stored near-duplicate.

Dashboards are in the corpus for later phases (SB-P1-06, SB-P6-03) but no golden query has one as its answer: no
retrieval path reads dashboards today ([baseline.md](baseline.md) section 3).

## Golden queries

Each query names a viewer, the relevant records (grade 2 for the answer, 1 for also relevant), the records it forbids,
and the capabilities a correct answer needs (`viewer-scope`, `as-of`, `validity`, `quarantine`); a path without one
records an expected gap, not a regression.

| Type | Queries | Clusters | Judged | What it checks |
|---|---|---|---|---|
| exact-id | 22 | 17 | 22 | a ticket, version, commit, quote, booking, tracking, order, meter, licence or permit number |
| rare-name | 21 | 19 | 21 | an invented name that appears in only a few records |
| paraphrase | 40 | 40 | 40 | the same meaning in other words, answered by memories, notes and documents |
| graph-chain | 20 | 19 | 20 | an answer one or two typed links from a seed |
| as-of | 15 | 12 | 0 | what was true on an earlier date |
| current | 12 | 10 | 0 | what is true now, with the replaced fact forbidden |
| visibility | 35 | 27 | 12 | must-hide checks for Alex (8), Sam (8) and the Family view (7), and must-show checks for each (12: judged, since they need no capability) |
| injection | 15 | 10 | 0 | a held instruction must never be served, even beside a real answer |
| multilingual | 21 | 11 | 21 | other languages, cross-language queries and accent folding |
| long-text | 26 | 11 | 26 | a fact deep inside a long record |
| stale | 14 | 12 | 14 | a fact overdue for review |
| commitment | 15 | 13 | 15 | a promise someone made |
| health | 16 | 13 | 16 | a person's measurement series or health notes |

By the record kind of their answer, 177 queries have a memory, 24 a note, 35 a Drive document and 14 a measurement
series as a grade-2 answer.

### Labelling rules

The test that enforces each rule is `Golden_labels_follow_the_rule_of_their_type` unless the rule says otherwise.

- **Relevant** records are visible to the query's viewer (`Golden_labels_agree_with_who_may_see_what`). A stored
  duplicate of an answer is grade 1: a correct engine may show only one of the two.
- **Forbidden** records appear only in visibility, injection and current-fact queries. In a visibility query they are
  records the viewer may not read, and the query needs `viewer-scope`; in an injection query each is held, or hidden
  from the viewer with `viewer-scope` needed too, and at least one is held; in a current-fact query each is a replaced
  fact whose chain ends in an answer.
- **Clusters** follow upstream's rule that queries sharing a source record resample together in the gate's bootstrap.
  A query's cluster is its first grade-2 record, else its first relevant one, else its first forbidden one; a query
  whose answer is a replaced fact names its chain's current record in `cluster`, so one chain is one cluster
  (`ParityQuery.ClusterKey`).
- **Exact-id:** every word of the query that contains a digit appears as written in each answer.
- **Rare-name:** the records the viewer may see that contain every word of the name, ignoring case and accents, are
  exactly the relevant records.
- **Graph-chain:** the seed is a record the map lists, and every relevant record is within the query's depth of it
  along typed links between listed records, in either direction.
- **As-of:** each answer was written by the as-of date and valid on it; each `wrongAtTime` record was not. A document
  counts from its first revision.
- **Multilingual:** the query, the answer's text or the answer's language is not plain English.
- **Long-text:** every answer is longer than 2000 characters.
- **Stale, commitment, health:** every answer has a review date in the past, a commitment, or is a measurement series
  or a memory tagged `health`.
- **Size** (`The_golden_set_is_large_enough_for_a_gate_verdict`): at least 200 judged queries (a relevant answer and no
  capability needed, as the gate counts them, D-056) in 30 or more clusters, at least 10 judged in every type that has
  any and 10 judged visibility (must-show) queries; and for every type at least 10 queries in 10 or more clusters, so a
  targeted gain on any one type can be powered once its capability exists (D-028); each of memory, note, document and
  measurement series is a grade-2 answer of at least 10 queries; each viewer has at least 5 must-hide checks and a
  must-show one.
- **Scope cases** (`The_corpus_holds_the_cases_the_scope_contract_defines`, D-061): a former person's records are read
  by no one and a must-hide check forbids one for every viewer; the household's Second Brain workspace and a team
  workspace read as Family; a memory or project with no person author is in a shared scope; an insight, insight run,
  pin or corner card is in the narrowest scope of what it cites or shows; the scripted share's history ends at the
  memory's current text.

## Labelled pairs

A pair is two records, `a` the earlier and `b` the later, with one label, a one-line `why`, and `hard` on the pairs a
similarity threshold is likely to get wrong. A member is a corpus text record or an **incoming** save: a text that
has not been saved, compared with what is stored, as a duplicate check on save sees it. Incoming saves are never loaded
into the evaluation database.

| Label | Pairs | Meaning |
|---|---|---|
| duplicate | 13 | the same facts in the same scope, however worded: merging loses nothing; one pair is stored, the rest are incoming |
| near-duplicate | 13 | the same subject and scope, mostly the same facts, one adding or dropping a minor detail without contradicting the other; one pair is stored |
| contradiction | 22 | facts that cannot be true at once; `resolved` is true for the 10 supersession chains and false for 12 that nobody has settled (11 incoming, and one stored note that the bin day change made wrong) |
| related | 59 | different facts about the same thing, project, event or people, linked in the corpus or not; two memories in one project count as related |
| unrelated | 76 | no shared subject; 40 are hard: a shared word or name (Pepper the cat and black pepper, a book's plot and plot 22, Marram Point and the Marram Milers) or a shared topic (two recipes, two quotes, two injuries) |
| insight | 10 | an older and a newer stored record, 30 or more days apart, whose combination says something neither does: bills down since the boiler failed, a gym membership renewing while Sam cannot climb, a sabbatical the work rules do not yet allow, breakdown cover for a car that was sold |

The rules, also in the file (checked by `Every_labelled_pair_resolves_and_each_label_is_well_represented` and
`Pair_labels_follow_the_written_rules`):

1. One label per pair; when two could apply, the first of duplicate, near-duplicate, contradiction, insight, related,
   unrelated wins.
2. `a` is the earlier record: a memory by its creation, a note by its last change, a document by its current revision,
   an incoming save by when it would be saved. An incoming save is always `b`.
3. Duplicates and near-duplicates live in one scope; a copy in another scope is a share.
4. A pair across Alex's and Sam's personal scopes is labelled by meaning, related or unrelated only, and no engine may
   link, merge or compare it where either person would see the result. The fixture has both kinds.
5. An insight pairs two stored records that one person may read together, written at least 30 days apart.
6. Every supersession in the corpus is a resolved contradiction, and an unresolved one has no supersedes link.
7. Unrelated pairs have no link in the corpus.
8. Each label has at least 10 pairs, and there are at least 5 hard negatives and 5 hard positives.

These pairs are what SB-P2-13 re-tunes the similarity, duplicate and insight thresholds on, per model, and what the
map relevance ideas are judged by (SB-P4-12). Thresholds are not tuned here.

Run 19 (SB-P4-03) added 104 pairs (89 to 193) chosen by where links on save read them, not by their labels: every pair
of two memories of one scope, or a memory and an incoming save, neither held for review, that a save scores at 0.43 or
more under MiniLM or 0.62 or more under bge-small (the saved words against the stored memory's best passage,
`ThresholdTuning.SavePairs`) and that was not labelled yet, each then labelled by meaning by the rules above. So every
such pair of the corpus at or above those scores is labelled, which
`ThresholdTuningTests.Every_pair_a_save_compares_above_MiniLMs_floor_is_labelled` (and bge-small's) holds: a memory
added to the corpus that a save would score above them needs its pairs labelled too. Most of the added pairs are
unrelated (60, 31 of them hard: two memories of one person that share a train, a book, a payment or a word) or
related (36, of which two memories in one project, such as Alex's work on the Larkspur editor). Because they come from
one region of scores they change every kind's counts, not only link on save's
([benchmarks.md](benchmarks.md#thresholds-per-model-sb-p2-13)).

### Insight pairs

`Parity/insight-pairs.json` (run 19, SB-P5-05) labels 15 more pairs in the same format, each two native memories of one
scope, both current, the newer written 30 or more days after the older: the only pairs the nightly insight candidate
pass compares, of which `pairs.json` holds 43 since run 19 (6 before it). It is a file of its own so that SB-P2-13's
thresholds and counts stand. The insights slice labelled 20; five of its related pairs (the heat pump's handover notes
and its remote, the tariff and the bills, the long runs and the knee, Sam's new job and the library talk, the plot and
its rent) were labelled the same in `pairs.json` by the links slice in the same run, so the integration kept them there
alone.

| Label | Pairs | Meaning |
|---|---|---|
| insight | 5 | the heat pump decision and the bills it brought down; the new car charging at home in the same bills; Pepper's renal diet and the days Sam feeds her; a sabbatical about 18 months into a new job; a three-month walk resting on a strained knee |
| related | 2 | the same thing, project or people, that together say nothing new: a kitchen chosen and its fitter booked, the choir and its bake sale |
| unrelated | 8 | no shared subject; most hard: the hallway in two notes, two jobs to book again in a year, two household bills, two places Alex likes |

Its rules, also in the file (checked by `Every_insight_pair_is_two_eligible_memories_of_one_scope_30_days_apart_and_new`):
one label per pair, by `pairs.json`'s order; two eligible memories of one scope, `a` written 30 or more days before `b`;
no pair already in `pairs.json`; unrelated pairs have no link in the corpus; each label has hard cases (related's three,
since the integration, in `pairs.json`). With `pairs.json`'s 43, they are what the insight candidate signals are measured
on: 8 insight pairs and 50 others
([benchmarks.md](benchmarks.md#insight-candidates-sb-p5-05)). They were labelled in the run that chose the pass's rule,
after the signals were in view, which the benchmark says.

### Standing cases

`standing-cases.json` (SB-P5-12) labels questions for standing instructions, since D-211 found the standing threshold
resting on two questions of one memory. Each of its 22 instructions (a person's and Family's, of the household's own
subjects: a flight, a patch, a long run, the vet, the allotment, the bins, a tradesperson, the heat pump) has at least
four questions asked at the moment it is about (`fire`), three that share its words or a topic and are about something
else (`near`), and two about its subject with another purpose, a fact or a past event (`intent`). Every other pair, of an
instruction and another's question or a golden query, is a negative unless `also` labels it, by meaning, with why: every
such pair scoring 0.50 or more under MiniLM or 0.65 or more under bge-small was read and is listed there, 52 pairs (the
scores chose which pairs were read, never their labels, and `Bge_smalls_standing_threshold_fires_only_for_what_is_labelled`
and MiniLM's hold it true); below that a pair not listed was not read. An `intent` question counts neither way: it should
not fire, and a fire would do less harm than a near miss's, since the answer says to apply it only if it fits; upstream
reports these apart for the same reason, and so do the benchmarks. The file's rules are checked by
`The_standing_cases_keep_their_rules`. The questions were written before any score was seen; the 52 were labelled after,
which the benchmark says.

## Adding to the fixtures

- A new record needs a unique id, a scope its author may write, and dates no later than the corpus's `now`. Name only
  invented things and `example.invalid` hosts.
- A new query must follow its type's rule above; run `dotnet test engine --filter ParityCorpusTests` to see which rule
  it breaks. A record that mentions a rare name changes that name's rare-name query, and a new private record on a
  visibility query's topic belongs in its forbidden list.
- A new supersession needs a resolved contradiction pair.
- The evaluation numbers move whenever the corpus or the labels do: record the new run beside the change.

## The evaluation on the larger set

Run on 31 October 2026 on `sb/p2-fixtures`, with the commands in [baseline.md](baseline.md) section 5 and a test
container named outside the `rhythm-` prefix: both load modes, the local model off and on (the pinned MiniLM files,
`minilm-l6-v2-quantized-afdb6f1a-mean-l2-v1`). k = 5. No path errored. The whole run through `engine/test-db.sh`,
container start included, took 30 to 37 s without the model and 65 to 99 s with it; loading and rebuilding the map took
0.5 to 0.7 s (installed) and 2 to 6 s (copies) without the model, 1.6 to 2.7 s and 3.1 to 3.8 s with it.

Two runs with the model returned the same top 5 for every query and path, and every recall, MRR and nDCG figure
matched a third. That needed one change to the loader: it wrote all measurement series in one transaction, so they
shared one `indexed_at`, and `search_knowledge`, which lists the newest indexed first, returned Alex's and Sam's resting
heart rate series in a different order on each run. It now writes each series in its own transaction. Latencies are
the median of three calls after a warm-up on a development machine shared with other work; one of the three runs with
the model was up to twice as slow on every path, and the tables show the two that agreed.

These numbers replace the phase 0 ones as the baseline for later work on the same corpus. They are not comparable with
[baseline.md](baseline.md)'s 34-query numbers: the corpus is 3.6 times larger, so `semantic_search`'s 20 results are
a fifth of the text records rather than two thirds, and the new queries include many that today's paths cannot answer.

### As installed (what an install finds today)

| Path | Recall@5 | Recall@5 without gaps | MRR | nDCG@5 | Pass | Partial | Miss | Expected gap | Leaked or exposed | p50 | p95 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| fulltext | 0.027 | 0.027 | 0.027 | 0.026 | 4 | 1 | 164 | 62 | 11 | 1.5 ms | 1.8 ms |
| semantic, model on | 0 | 0 | 0 | 0 | 0 | 0 | 169 | 62 | 11 | 2.5 ms | 3.1 ms |
| graph, model on | 0.230 | 0.269 | 0.202 | 0.205 | 42 | 6 | 121 | 62 | 35 | 16.0 ms | 17.8 ms |
| graph, model off | 0.227 | 0.266 | 0.209 | 0.208 | 41 | 7 | 121 | 62 | 35 | 6.8 ms | 7.9 ms |

Full text is unchanged without the model; `semantic_search` is then unavailable. Relevant records found in the top 5
over the queries a path has every capability for, model on:

| Kind | fulltext | semantic | graph |
|---|---|---|---|
| memory | 0 of 149 | 0 of 149 | 50 of 149 |
| note | 1 of 28 | 0 of 28 | 12 of 28 |
| Drive document | 0 of 43 | 0 of 43 | 9 of 43 |
| measurement series | 4 of 15 | 0 of 15 | 1 of 15 |

### With `save_document` copies (what today's tools do with the text in hand)

| Path | Recall@5 | Recall@5 without gaps | MRR | nDCG@5 | Pass | Partial | Miss | Expected gap | Leaked or exposed | p50 | p95 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| fulltext | 0.243 | 0.285 | 0.248 | 0.240 | 46 | 4 | 119 | 62 | 35 | 4.0 ms | 4.7 ms |
| semantic, model on | 0.783 | 0.759 | 0.695 | 0.689 | 117 | 24 | 28 | 62 | 47 | 3.2 ms | 4.2 ms |
| graph, model on | 0.243 | 0.285 | 0.211 | 0.214 | 47 | 2 | 120 | 62 | 35 | 38.2 ms | 43.6 ms |
| graph, model off | 0.241 | 0.282 | 0.214 | 0.216 | 46 | 3 | 120 | 62 | 35 | 7.3 ms | 10.8 ms |

By query type (recall@5, model on; "gap" means the path lacks a capability every query of the type needs):

| Type | fulltext | semantic | graph | fulltext, installed | graph, installed |
|---|---|---|---|---|---|
| exact-id (17) | 0.88 | 0.94 | 0.53 | 0 | 0.53 |
| rare-name (16) | 1.00 | 0.91 | 0.88 | 0 | 0.70 |
| paraphrase (32) | 0 | 0.66 | 0 | 0 | 0 |
| graph-chain (18) | 0 | 0.57 | 0.95 | 0 | 0.95 |
| as-of (15) | gap | gap | gap | gap | gap |
| current (12) | gap | gap | gap | gap | gap |
| visibility (27) | 0.52; 20 gaps | 0.89; 20 gaps | 0.33; 20 gaps | 0.17; 20 gaps | 0.33; 20 gaps |
| injection (15) | gap | gap | gap | gap | gap |
| multilingual (18) | 0.36 | 0.67 | 0.28 | 0 | 0.28 |
| long-text (23) | 0.13 | 1.00 | 0 | 0 | 0 |
| stale (12) | 0 | 1.00 | 0 | 0 | 0 |
| commitment (12) | 0 | 0.88 | 0 | 0 | 0 |
| health (14) | 0.29 | 0.14 | 0.07 | 0.29 | 0.07 |

### What the larger set shows

1. **The phase 0 findings hold on a corpus 3.6 times larger.** As installed, full text finds only measurement series and
   one note, by their titles, and `semantic_search` finds nothing; the graph's title match is the only path to memories, notes and
   Drive files. None of the 21 must-hide checks that need a viewer's scope passes on any path with copies; as installed,
   11 still fail on the catalog paths (Sam's and Alex's health series, and private notes, open through `get_resource`)
   and all 21 on the map.
2. **Semantic search is strong on long text, stale facts and commitments, weak across languages.** With copies it
   finds every long-text answer in the top 5, but misses all six queries asked in one language about a record in
   another (English-only MiniLM): the French, German, Spanish and Japanese records are found only by their own
   words. Health queries reach 0.14, and only through the two health notes: measurement series have no chunks.
3. **A held injection outranks real answers.** For Alex's "resting heart rate", the top semantic result is Sam's
   forum clipping that asks for Alex's health data: an injection and another person's private record at once.
4. **Full text still needs every word** (before SB-P2-07, which ranks any-word matches since; its numbers on this set
   are in [lexical-search.md](lexical-search.md)). All 32 paraphrases miss; "installer quote HP-2291" misses the quote
   document because its text never says "installer"; "ticket RHY-4310 waveform" misses because no record says "ticket"
   beside the id.
5. **The graph path now differs between load modes.** In copies mode a replaced fact's `save_document` copy becomes a
   catalog document star, so rare names found only in a replaced fact ("Pellmoor", the old insurer) are found there and
   not as installed (rare-name 0.88 against 0.70). With the model on, similarity links make the map denser: the graph
   path's p95 rises from 8 to 18 ms as installed and from 11 to 44 ms with copies, still far inside the 500 ms D-029
   allows combined recall.

### Limits

- At 231 queries the set passed D-028's size by count only: the gate counts judged queries (D-056), 62 were expected
  gaps, which left 169, and 7 judged visibility queries, so its verdicts were INCONCLUSIVE. The next section grows it
  past the floor. It is one synthetic household: written to exercise the rules above, not sampled from real use.
- The gaps are the same four capabilities as at phase 0; no path has any of them, so 62 queries of each path are
  expected gaps whatever their results.
- Every query still runs as one harness principal: the viewer field is recorded, not enforced, until reads take a
  viewer (phase 1).
- Neither the dashboards nor the incoming saves are loaded; the earlier revisions of the two quotes are dropped by the
  loader, so the as-of quote queries can only be gaps.

## The phase 1 cases and the judged floor

A review of run 2 found two gaps in the 231-query set: the gate's floor counts judged queries, of which it had 169 (7 of
them visibility), and the corpus had none of the cases the scope contract ([../memory.md](../memory.md)) defines beyond
two people and Family. Both were closed on `sb/main` on 31 October 2026 (D-061, D-056 amended):

- **15 memories**: a former person's two (`jo`, who left `people`); five Family memories and a Family project with no
  person author; two in the household's Second Brain personal workspace; three in a Second Brain team workspace, one
  of them longer than 2000 characters; a Portuguese recipe; a health note of Sam's; and a Family memory with a person
  author. `mem-alex-cafe` gained a version before the share story 2 performs.
- **Not loaded**: three insights (one citing Alex's private memory, one with no person author citing Sam's), three
  insight runs (one whose note names Alex's private memory), a pin of Alex's private star, a corner card showing Alex's
  private dashboard, one of each in Family, and the scripted share.
- **41 queries**: 38 judged (5 must-show visibility checks, 8 paraphrases, 5 exact ids, 5 rare names, 2 graph chains, 3
  long-text, 3 multilingual, 2 stale, 3 commitments, 2 health), and a must-hide check for each viewer on the former
  person's records. 272 queries in all, 207 judged in 103 clusters, every type that has judged queries at least 10.

The evaluation on this set, model on, both load modes (recall@5 / MRR; pass, partial, miss; expected gaps; leaked or
exposed):

| Path | With copies | As installed |
|---|---|---|
| fulltext (ranked) | 0.814 / 0.735; 163, 12, 32; 65; 47 | 0.115 / 0.108; 23, 4, 180; 65; 11 |
| fulltext, legacy query | 0.263 / 0.267; 60, 4, 143; 65; 35 | 0.023 / 0.023; 4, 1, 202; 65; 11 |
| semantic | 0.786 / 0.704; 147, 25, 35; 65; 47 | 0 / 0; 0, 0, 207; 65; 11 |
| graph | 0.242 / 0.215; 56, 2, 149; 65; 38 | 0.231 / 0.212; 51, 6, 150; 65; 38 |

The former person's records reveal through the map in both modes (the graph's leaked or exposed count rises by their
three checks) and nowhere else: `save_document` takes a subject in `people` only, so they have no copy. The new
records also pushed some old answers down where they share a person's name with the query: with copies, 19 queries
ranked an answer lower on full text and 4 lost a top-5 answer (`q-para-gift`, `q-para-hike`, `q-para-party`,
`q-graph-car`). No label changed; the locks were taken again on this data in the same commit. With the floor met the
gate now gives verdicts: against the new locks every path is unchanged, so FAIL for want of a gain, which the opt-in run
does not count (D-056); against legacy keyword search, ranked search with copies loses `q-rare-milers` (rare-name MRR
0.976 against 1.000), a type regression by D-056 ([lexical-search.md](lexical-search.md)).

## Second Brain export fixtures

The inputs for the Second Brain importer (SB-P8-01) and its comparison harness (SB-P8-03), built by SB-P8-02 before
either exists. They are separate from the corpus above and load into nothing yet. `sb-export-brain.json` is a fictional
Second Brain deployment at the pinned version (4.0.1, `228cea49`), written as upstream's own rows: its users,
workspaces, memberships, projects, memories, edges, versions, events and trash, with each column upstream has
(`db/schema.sql` and the runtime columns of `src/db/init.ts`) and nothing else, times as ISO 8601 in UTC where upstream
stores milliseconds. `key`, `note` and `expect` are the fixture's own. Each export file is exactly what upstream's
`GET /export` (`src/routes/entries.ts`) returns from that deployment for one member at one moment: their readable set
(their personal workspace and every team they are on), memories oldest first, the edges whose own workspace is
readable, the readable projects. `UpstreamExport.Take` derives it and the test compares the file with it.

| File | Taken by | Release, version | Memories | Edges | Projects |
|---|---|---|---|---|---|
| `sb-export-alex.v3.json` | Alex (the admin), 31 October 2026 | 4.0.1, 3 | 29 | 13 | 6 |
| `sb-export-sam.v3.json` | Sam, 31 October 2026 | 4.0.1, 3 | 25 | 11 | 8 |
| `sb-export-alex.v2.json` | Alex, 14 March 2026 | 3.3, 2 | 12 | 3 | none |

The deployment: three members (Alex, Sam, and Jo, who left on 31 December 2025), five workspaces (three personal, the
Family team every member is on, and the Wickmere beekeepers' team with Sam alone), 11 projects, 40 memories and 2 in the
trash, 15 edges and 1 in the trash, 5 versions and 10 events.

### What the files hold for the import

- **Scopes.** Family's 15 memories are in both people's files under the same ids; each personal workspace is in its
  owner's file alone; the beekeepers' 2 are in Sam's only. Family holds a memory shared from Alex's personal workspace,
  one Jo wrote before leaving, and one the weekly insight pass wrote with no author. Jo's personal memory is in no file.
  No file says which workspace or author any memory has.
- **Links.** All eight edge types and all three provenances (explicit, inferred, system), `relates_to` stored with the
  smaller id first. Sam's file has an edge to Alex's private memory, which Sam's file does not hold: sharing a memory
  into Family moves its edges with it, and the export picks edges by their own workspace. Drive files are named in the
  text in both address forms (`docs.google.com/document/d/…`, `drive.google.com/file/d/…`, with invented `fx-sbx-`
  ids), and a memory mirrored from Notion links its page on an invented host. The format has no attachments: a file is
  reached only through an address in the text.
- **Projects.** Aliases, an archived project, membership by `project:<slug>` tags, and the slug `choir` from two
  workspaces in Sam's file (Sam's choir and the archived Family bake sale) with nothing to tell them apart.
- **Holds.** Each of upstream's five hold reasons once: an instruction pasted from a forum, zero-width characters in a
  recipe, a burst of writes by one AI tool, an AI tool's change to the core capsule, and a 34.9 KB diary over the 32 KiB
  scan. A newsletter paste saved from the browser, which upstream serves (a person's own channel ignores instruction
  phrases) and Rhythm's import should hold (D-037). Jo's `quarantine:review` from before 4.0: an ordinary tag upstream
  serves, while Rhythm's Second Brain adapter leaves any `quarantine:` tag off the map, so SB-P8-01 decides. Each such
  row's `expect` says what upstream does and what Rhythm's import should do.
- **Time and status.** A replaced tariff (a `supersedes` edge, the old window closed where the new one starts); a
  retracted fair date and a stall booking built on it (`retracted-source`); every status, kind and volatility; due and
  event times, which only the deployment holds; a staleness flag; five versions; decision, standing, commitment and
  capsule tags.
- **The older file.** Version 2: no projects, no `updated_at`, no validity window. It holds the contract's text from
  before its later edit, the car's tags from before the staleness flag, and two memories forgotten since, which today's
  file does not hold.
- **Hard text.** An id a client chose before 4.0, over upstream's 40-byte bound; Japanese; zero-width characters.

### What upstream's export leaves out

`not_exported` in the brain names each column and table with what is lost, and the test checks that the list is exactly
the columns the export drops. Memories lose `workspace_id`, `actor_id`, `when_at`, `when_kind`, `when_source`,
`when_label`, `staleness_checked_at` and `vector_ids`; edges lose their `id`, `metadata`, `updated_at` and
`workspace_id`; projects lose `workspace_id`. No table but memories, edges and projects is exported: not the users,
workspaces, memberships, versions, events or trash, nor the capsule revisions, insight candidates, recall log or admin
events. The runbook says what the import does about each.

Upstream's own import would change two things in these files (`What_upstreams_own_import_would_change_is_known`): the
over-long id gets a minted one, and the tags 4.0 writes itself (`ledger:decision`, `confidence:`, `confidence-source:`,
`standing:active`, `owed-to-me`, `counterparty:`, and each `quarantine:` reason, which it reads first to hold the row
again) are dropped from the file's tags. A restore through upstream therefore loses decision, standing instruction and
commitment markers, which Rhythm's import is meant to keep.

### Limits

- The 3.3 shape (version 2) is inferred from what upstream's importer still treats as optional and from its changelog
  (projects reached the export in 3.4, validity windows in 4.0), not from a file a 3.3 deployment wrote.
- The older file carries today's recall, contradiction and importance counts: upstream keeps no history of them.
- Events are the ones an import must account for (shares, edits, status changes, holds, deletions); a real deployment
  also records a `created` event per memory. Payloads keep only what an import reads. Users carry no token hash.
- The rules upstream's import applies are restated in C# from the pinned source (`UpstreamImportRules`); nothing here
  ran upstream's code.

### Checks and changes

`SecondBrainExportTests` runs in every ordinary test run: the deployment is one upstream could hold (members of the
workspaces they write in, held rows never classified or embedded, edge kinds, version and event order, no 4.0 tag or
validity window before the upgrade without a version); each file is what upstream would export; each file has its
release's keys and nothing more; upstream's import rules accept every row; what upstream's own import would change; the
`not_exported` list; the cases above; and that nothing is real. To change the fixtures, edit `sb-export-brain.json`,
then run `RHYTHM_SB_EXPORT_WRITE=<folder> dotnet test engine --filter SecondBrainExportTests`, which writes each export
file derived from the brain into that folder, and copy them over the checked-in ones.
