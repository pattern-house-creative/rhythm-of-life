# Map relevance: the connection ideas measured

SB-P4-12 measured four of the ideas in [../connection-ideas.md](../connection-ideas.md) for finding more real links
between stars: shared names (idea 2), a correction for stars that match everything (idea 3), an AI check of near misses
(idea 5) and "cited together" in an insight (idea 6). Each was scored alone on the labelled pairs (SB-P2-18), and kept
only if it gained there and the retrieval gate then counted no loss on the map. Synthetic data only, in throwaway
databases, with the pinned MiniLM files placed locally (D-041), on 5 November 2026 (`sb/p4-relevance`, from `sb/main`
`2eabc48`). Compare [benchmarks.md](benchmarks.md) (the thresholds per model, the same labelled pairs) and
[baseline.md](baseline.md) section 6 (the gate).

## Where it stands

- **No idea is kept.** Shared names gains on the labelled pairs but loses graph chains on the map in both load modes;
  the hub correction links two more unrelated pairs and finds nothing today's map misses; "cited together" finds only a
  pair today's map already links. The near-miss check, measured since 9 November with three reviewers
  ([below](#idea-5-an-ai-check-of-near-misses)), fails the keep rule with each: the two cross-encoders keep almost
  nothing (one more pair shown together each), and the titles model, qwen3 4B, keeps 7 of the band's 9 pairs shown
  together but one unrelated pair too. No gate run on the map was due.
- **Nothing changes for any install.** No setting, migration, contract or app change: the engine's code is `sb/main`'s,
  so the map, Discover (which lists the map's inferred and suggested links in the order the map serves them: inferred
  before suggested, then by how many links the two stars have in all, then by the link's id) and recall's hops answer
  as before; `MapAnswersRun` gives the hashes recorded [below](#the-maps-answers).
- The two ideas that change how the map links were run on the map from a measurement build, commit `41b6a8c`, behind
  `graph.relevance.names` and `graph.relevance.hubs`; the next commit took them out again. Their rules stay in the tests
  (`Parity/RelevanceRules.cs`), so `RelevanceIdeasTests` recomputes every number on the labelled pairs: since run 19's
  integration those [on the 124 pairs](#at-run-19s-integration-124-pairs-the-stars-as-the-map-lists-them), with the
  stars as the map lists them; the tables below are the slice's first numbers, on 56 pairs.
- The measurement build was measured with scopes off only, and its scoped name path is not scope-safe: it decides which
  words are names across every scope in the pass, so with `memory.scopes` on a private record's capitalised word could
  change whether two Family stars are joined (run 19's review reproduced it: Alex's private "Fed Pepper at noon." turned
  "Pepper" into a name word and took away the join of two household stars that share "Care Plan"). A retry decides name
  words inside each scope-pair group (or from the records the pair's readers read), with a test where a private
  record's word would change a Family join.

## How each idea was measured

The rules were written after a first probe and before the numbers below were taken (`Parity/RelevanceIdeas.cs`;
[how far to trust them](#how-far-to-trust-the-numbers)).

- **Stars.** Each of the corpus's 120 stored records (memories, notes, documents) is one star, with its title and text
  as the map's hook reads them, its vector by the map's recipe and its subject as the parity loader gives it. Every
  record is a star, a replaced fact included (the copies mode lists their copies), so this scores each signal; the gate
  then scores the map.
- **Pairs.** The labelled pairs the map may join: both records stored (27 pairs with an incoming save are never on the
  map) and one subject's, or one's and the household's (6 pairs across Alex's and Sam's scopes are never linked,
  pairs.json's own rule). That leaves 56: 45 a reader wants shown together (duplicate 1, near-duplicate 1, contradiction
  11, related 22, insight 10) and 11 unrelated.
- **Today's map on those pairs**: the similarity rule with `graph.similarity`'s defaults (0.6, 3 neighbours, documents
  0.8, mutual 0.45) over the 120 stars, and the links the corpus states (a record's own links, which the loaders turn
  into references). Each idea alone is its own links; with the idea, today's links and the idea's, except the hub
  correction, whose similar links stand in place of today's (the stated links kept).
- **The keep rule.** With the idea, today's map finds at least 2 more of the pairs shown together and no more unrelated
  ones (one pair is one label's say, not evidence). An idea that passes is run on the map in both load modes through the
  embedding benchmark's gate (`EmbeddingBenchmark`, a variant with the idea on and today's thresholds) and kept only if
  the gate counts no loss: no type loses 0.01 or more on any path against the locks (D-056).

## The numbers

Precision and recall on the 56 pairs, with the counts (true positives, false positives, false negatives, true
negatives), as the slice measured them on stars that carry no references. The map never compares two records one
references (`SimilaritySuggestions.Comparable` leaves them to the stated link), so the "alone" rows for today's similar
links, the hub correction and the near-miss band are the similarity signal without that exclusion, not what the map
links; run 19's review counted them with the stated links as references: similar links alone 8, 1, 37, 10 over 50
corpus pairs, the hub correction alone 8, 3, 37, 8 over 73, and 8 of the band's 125 pairs are links the corpus states.
Every combined row and every verdict was the same on the 56. Since run 19's integration `RelevanceIdeasTests` records
the numbers [on the 124 pairs](#at-run-19s-integration-124-pairs-the-stars-as-the-map-lists-them) instead.

| Links | P | R | Counts | Corpus pairs linked |
|---|---|---|---|---|
| Today's similar links | 0.958 | 0.511 | 23, 1, 22, 10 | 65 |
| The corpus's stated links | 1.000 | 0.600 | 27, 0, 18, 11 | |
| **Today's map** (both) | 0.972 | 0.778 | 35, 1, 10, 10 | |
| Shared names alone | 1.000 | 0.267 | 12, 0, 33, 11 | 45 |
| Today's map with shared names | 0.974 | 0.844 | 38, 1, 7, 10 | |
| Hub correction alone (its similar links) | 0.903 | 0.622 | 28, 3, 17, 8 | 89 |
| Today's map with the hub correction | 0.921 | 0.778 | 35, 3, 10, 8 | |
| Near-miss band alone | 0.818 | 0.200 | 9, 2, 36, 9 | 125 to review |
| Today's map with the band, every pair kept | 0.927 | 0.844 | 38, 3, 7, 8 | |
| Today's map with the band, a perfect reviewer | 0.974 | 0.844 | 38, 1, 7, 10 | |
| Cited together alone | 1.000 | 0.022 | 1, 0, 44, 11 | 3 |
| Today's map with cited together | 0.972 | 0.778 | 35, 1, 10, 10 | |

The one unrelated pair today's map links is `p-unrel-gardens` (the allotment's planting and the home garden's plan,
which says the allotment is separate), by similarity at 0.49 through the mutual rule.

| Idea | On the pairs | On the map (the gate) | Kept |
|---|---|---|---|
| 2, shared names | +3 shown together, no more unrelated: passes | FAIL in both modes: graph-chain recall −0.025 and −0.050, MRR −0.054 and −0.052 | no |
| 3, hub correction | no more shown together, +2 unrelated: fails | (run for the record) FAIL with copies: graph-chain MRR −0.023; as installed no loss and no gain | no |
| 5, near-miss check | not measured by the slice: no reviewer was run (measured since, on the 124 pairs: [below](#idea-5-an-ai-check-of-near-misses)) | not run | no |
| 6, cited together | no gain: fails | not run | no |

### Idea 2: shared names

The rules (`SharedNames`): candidates are runs of capitalised words (possessives taken off; a word with a digit is a
code, a word in capitals only is no name, weekdays and months and their plurals are calendar words); a word is a name
word only where its place does not explain its capital, so a run that opens a sentence, line, heading or list item and is
one word long is dropped ("Home insurance is…"), and so is one word that names a person of the household (rhythm.yaml's
people, by id or name); a name joins stars when 2 to 5 of the compared stars carry it. No model. Two of the idea's
first-draft sources were left out: project names reach the map already as project stars with `in_project` links, and
tags and folders name no person, organisation, place or event.

- **On the pairs** it finds 12 pairs, all a reader wants shown together, and none of the 11 unrelated, the hard ones
  that share a word included: Gullhaven (a running club and a bookshop), Marram (the Marram Milers and Marram Point),
  Pepper the cat and black pepper, a book's plot and an allotment plot. Nine of the 12 are linked today already; it
  adds 3: `p-rel-checklist-install` and `p-rel-rent-handbook` (Brightwater Heating, Nettlebank) and the insight pair
  `p-ins-breakdown-car` (Corvel Estra). Precision 12 of 12 is [0.76, 1.00] by Wilson's 95% interval; the gain rests on
  3 pairs.
- **Over the corpus** it links 45 pairs. At least 2 are words, not names: Wi-Fi joins the car guide and the conference
  programme, and Christmas a Family lunch and Sam's library migration.
- **On the map** (commit `41b6a8c`, variant `minilm-names`, both modes), only the graph path moves (33 answers with
  copies, 31 as installed), and no type gains. With copies: graph-chain recall −0.025 [−0.136, +0.079] and MRR −0.054
  [−0.156, +0.042], overall MRR −0.005 [−0.016, +0.004]; as installed: graph-chain recall −0.050 [−0.136, +0.000] and
  MRR −0.052 [−0.149, +0.040], overall recall −0.005 [−0.015, +0.000]. The queries that lose are chains
  (`q-graph-quote`, `q-graph-food`, `q-graph-christmas`, `q-graph-plot`, `q-graph-letter`): `explore_graph` ranks a
  seed's neighbours by how linked they are, then by date, so more neighbours at one hop push a chain's answer down, as
  the tuned thresholds' extra links did ([benchmarks.md](benchmarks.md#the-tuned-thresholds-on-the-map)). Each loss of
  0.01 or more fails the regression rule (D-056), so the idea is not kept.

### Idea 3: the hub correction

CSLS (Conneau and others, 2018), as connection-ideas.md gives it: a pair scores its closeness less half the sum of each
star's hub score, the mean of its 10 closest scores (CSLS's own default, fixed before measuring). The corrected score
chooses which pairs pass, ranks each star's closest and decides the mutual rule; each threshold admits as many pairs as
it does today, by the corrected score, so a threshold is never looser than today's (`HubCorrection`).

- **On the pairs** its similar links find 28 shown together against today's 23 (both counted without the map's
  exclusion of stated pairs), but every one of the 5 more is a pair the corpus states already (two contradictions,
  three related), and it links 2 more unrelated pairs: `p-unrel-recipes`
  (two recipes) and `p-unrel-covers` (breakdown cover and home insurance). With the stated links: 35 against 35, 3
  unrelated against 1. It fails the keep rule.
- **Beside the rule**, at today's volume and without the map's neighbour cap and mutual rule (the corpus's 21 best
  pairs, the number at or above 0.6), corrected scores find 17 pairs shown together against 13, and no unrelated pair
  either way: the correction does rank pairs better. With the map's neighbour cap and mutual rule both following the
  corrected score, it links 89 of the corpus's pairs against 65 (73 against 50 with the stated pairs left out, as the
  map leaves them), the 2 unrelated ones among them (plain scores 0.36 and 0.41, which no threshold admits today).
- **On the map**, run for the record although the pairs had already failed it (on the 124 pairs it passes them, and
  this run decides; variant `minilm-hubs`): with copies
  graph-chain MRR −0.023 [−0.079, +0.007] (FAIL), 48 answers moved; as installed no type loses (graph-chain recall
  +0.042 [+0.000, +0.108]), 37 moved, and nothing gains enough for the improvement rule.

### Idea 5: an AI check of near misses

The slice ran no reviewer: there was no titles model (D-058) and no model was downloaded (D-041). It measured the band a
reviewer would check: pairs today's similar links leave out, scoring from 0.40 up to the similar threshold, at most 5 a
star. Over the corpus that is 125 pairs (114 with the stated pairs left out, as the map leaves them); at
connection-ideas.md's 8 to 20 seconds a pair on a CPU, 17 to 42 minutes of review for a first pass. To be kept, a
reviewer would have to turn every unrelated pair of the band down and keep at least 2 of the pairs shown together that
today's map misses, and then pass the gate.

**Measured on 9 November 2026** (`sb/p2-models2`, from `sb/main` `8fc6b9e`), with the owner's OK to fetch the reviewers
once (D-248) and by rules committed before any of them ran (D-249, `8a0103e`): on the 124 labelled pairs the map may join,
the stars as the map lists them, the band of 114 pairs holds 9 pairs shown together (2 insight, 7 related) and 27
unrelated, the rest unlabelled. Each reviewer looks at every pair of the band; the pairs it keeps join today's map (52
shown together, 7 unrelated of the 124).

- **A cross-encoder**, either of the two the reranker uses: each pair scored in both orders (one record as the question,
  the other as the text, each its title and text cut at 1,200 characters, a pair at most 512 tokens), kept when the mean
  of the two scores is at least 0 (the model's own probability 0.5).
- **The titles model**, qwen3:4b-instruct-2507-q4_K_M, through the titles service's pinned Ollama image on the benchmark machine's
  loopback: asked once a pair whether a person reading one record should be shown the other as connected (the same
  specific thing, or one changes, causes or explains the other; not a shared topic, word, name or person), answering only
  `{"related": true}` or `false` in Ollama's JSON format, temperature 0. No reviewer is asked for a reason and none is
  stored or shown: the ideas page's question 1 stays the owner's.

| Reviewer | Band pairs kept (of 114) | Labelled kept: shown together / unrelated | Today's map with them | Change | Keep rule | Speed |
|---|---|---|---|---|---|---|
| none (today's map) | | | 52, 7, 21, 44 | | | |
| every pair kept (the most it can cost) | 114 | 9 / 27 | 61, 34, 12, 17 | +9 / +27 | fails | |
| a perfect reviewer (the most it can gain) | | 9 / 0 | 61, 7, 12, 44 | +9 / 0 | passes | |
| bge-reranker-base | 3 | 1 / 0 | 53, 7, 20, 44 | +1 / 0 | **fails** (one more, under 2) | |
| ms-marco-MiniLM-L6-v2 | 7 | 1 / 3 | 53, 10, 20, 41 | +1 / +3 | **fails** | |
| qwen3 4B | 16 | 7 / 1 | 59, 8, 14, 43 | +7 / +1 | **fails** (one unrelated pair) | p50 573 ms, p95 1.41 s a pair; 79 s for the band; first call 8.5 s |

(Counts are true positives, false positives, false negatives, true negatives on the 124 pairs. The cross-encoders' time
was not measured here; as rerankers they score a pair in milliseconds to tens of milliseconds,
[benchmarks.md](benchmarks.md#the-reranker-sb-p2-12).)

What it says:

- **The cross-encoders are the wrong tool for this.** They were trained to judge a passage against a question, and two
  records that belong together are rarely question and answer: almost every pair scores far below 0 in both orders
  (bge-reranker-base gives 8 of the 9 pairs shown together a mean under −1). bge-reranker-base keeps one of them (the
  heat pump's handover notes and the parcel bringing its controller's new remote) and no unrelated pair; ms-marco keeps one (Alex's new commute and new
  job) and three unrelated pairs, each with Alex's new commute, a long memory that reads as an answer to many questions.
- **The titles model is a far better reviewer, and still fails the rule by one pair.** qwen3 4B keeps 7 of the 9 pairs
  shown together (it misses the engineering handbook and Alex's sabbatical, an insight pair, and the heat pump decision
  and the parcel with the remote) and turns down 26 of the 27 unrelated ones; the one it keeps is Alex's new commute and Alex's old job.
  The rule written first allows no more unrelated pairs, so it fails, whatever the gain, and it was not run on the map.
  It also keeps 8 pairs nobody labelled, whose worth is not measured.
- **Its cost**: about 0.6 s a pair warm (1.4 s at p95) on a 32-core desktop, well under connection-ideas.md's 8 to
  20 s estimate, so the band's 114 pairs take under 2 minutes here; on a small home server's CPU it would be slower, by an amount not
  measured here. It needs the titles model loaded (3.7 GB at peak,
  [titles-eval.md](titles-eval.md#re-test-under-the-successor-rule-d-250)). D-058 named the titles model as the reviewer
  this idea plans; this is that model, run on the benchmark machine, not on a real install.

`NearMissReviewTests` recomputes the cross-encoders' rows from the pinned files (`RHYTHM_RERANKER_DIR_BGE`,
`RHYTHM_RERANKER_DIR_MSMARCO`, with MiniLM's); the titles model's run is opt-in (`NearMissLanguageModelRun`,
[below](#how-to-rerun-it)), and writes each pair's id, label, verdict and time, no text and no reason.

### Idea 6: cited together in an insight

The corpus has 3 insights, which cite 3 pairs of stored records (Alex's knee and the race plan; the boathouse deposit
and the party; the smoke alarms and the chimney). One is labelled, `p-rel-party-deposit`, and today's map links it
already (similar at 0.45 through the mutual rule, and stated). No gain, so it fails the keep rule; the parity loader
does not load insights, and the map's own login cannot read them, so it was not run on the map either.

## How far to trust the numbers

- A first probe of the four ideas came before the keep rule and before shared names' rules were settled: it took codes,
  capitals-only words and lone sentence-opening words as names, and gave 39 shown together and 1 unrelated with today's
  map. The rules were then written down, shared names' from what the probe showed to be no name (GBP, RHY, Tuesdays),
  and everything was measured once. No verdict turns on the keep rule's 2: with 1 every verdict is the same (names +3,
  hubs +0 with 2 more unrelated, cited together +0).
- The labels are few: the names gain is 3 pairs, the hub correction's loss 2, and 11 unrelated pairs decide every false
  positive (51 on the 124 pairs since run 19).
- The slice's stars carried no references, so its "alone" rows for the similarity signals counted pairs the map leaves
  to their stated links; the harness's rule now leaves them out as the map does (`HubCorrection`, checked against
  `SimilaritySuggestions` with references by `Without_the_correction_the_rule_is_the_maps_own`), and the numbers on the
  124 pairs are counted that way.
- The gate's numbers are one run each, from commit `41b6a8c`. A quality figure does not vary between runs (the same code
  and model answer the same); its intervals are the gate's paired cluster bootstrap.
- The stars here are one per record, not the copies mode's two; the gate judges the map as each load mode builds it.

## At run 19's integration: 124 pairs, the stars as the map lists them

Run 19's links slice (SB-P4-03) labelled 104 more pairs in the same run (193 in all), chosen by where a save scores
them, not by these ideas. At the integration the ideas were counted again on every labelled pair the map may join, 124
(73 shown together: duplicate 1, near-duplicate 1, contradiction 11, related 50, insight 10; and 51 unrelated), with the
stars as the map lists them: the corpus's stated links as references, which the map never compares. The keep rule and
the gate runs are the slice's; no gate was run again (the engine's code is unchanged and the gate does not read the
labelled pairs). `RelevanceIdeasTests` records these.

| Links | P | R | Counts | Corpus pairs linked |
|---|---|---|---|---|
| Today's similar links | 0.741 | 0.274 | 20, 7, 53, 44 | 50 |
| The corpus's stated links | 1.000 | 0.438 | 32, 0, 41, 51 | |
| **Today's map** (both) | 0.881 | 0.712 | 52, 7, 21, 44 | |
| Shared names alone | 1.000 | 0.247 | 18, 0, 55, 51 | 45 |
| Today's map with shared names | 0.889 | 0.767 | 56, 7, 17, 44 | |
| Hub correction alone (its similar links) | 0.759 | 0.301 | 22, 7, 51, 44 | 73 |
| Today's map with the hub correction | 0.885 | 0.740 | 54, 7, 19, 44 | |
| Near-miss band alone (stated pairs left out) | 0.250 | 0.123 | 9, 27, 64, 24 | 114 to review |
| Today's map with the band, every pair kept | 0.642 | 0.836 | 61, 34, 12, 17 | |
| Today's map with the band, a perfect reviewer | 0.897 | 0.836 | 61, 7, 12, 44 | |
| Cited together alone | 1.000 | 0.014 | 1, 0, 72, 51 | 3 |
| Today's map with cited together | 0.881 | 0.712 | 52, 7, 21, 44 | |

Counted the slice's way, on stars without references, today's map is 52, 3, 21, 48 and with the hub correction 53, 6,
20, 45 on the same pairs: the 4 more unrelated pairs above are pairs the map's similar links reach once the stated pairs
leave their neighbour lists.

| Idea | On the 124 pairs | On the map (the slice's gate runs) | Kept |
|---|---|---|---|
| 2, shared names | +4 shown together (18 names, none unrelated), no more unrelated: passes | FAIL in both modes | no |
| 3, hub correction | +2 shown together, no more unrelated: passes (on the 56 without references it failed) | FAIL with copies: graph-chain MRR −0.023 | no |
| 5, near-miss check | the slice ran no reviewer; measured since (9 November): bge-reranker-base +1 and no unrelated, ms-marco-MiniLM +1 and +3 unrelated, qwen3 4B +7 and +1 unrelated: each fails | not run (the pairs failed) | no |
| 6, cited together | no gain: fails | not run | no |

No verdict changed: none is kept. The hub correction now passes the pairs and is failed by the gate run the slice made
for the record, so its verdict rests on the map, as shared names' does.

## How to rerun it

The numbers on the pairs, in any test run with the MiniLM files set (names and cited together run without them):

```sh
RHYTHM_MODEL_DIR=<MiniLM's files> dotnet test engine --filter RelevanceIdeasTests
```

The near-miss check's reviewers: the two cross-encoders in any test run with their files set, and the titles model
through an Ollama server on the titles evaluation's loopback port (the evaluation's compose project started with
`RHYTHM_TITLES_EVAL_KEEP=1`, so the model stays in its volume):

```sh
RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_RERANKER_DIR_BGE=<bge-reranker-base's files> \
  RHYTHM_RERANKER_DIR_MSMARCO=<ms-marco-MiniLM's files> dotnet test engine --filter NearMissReviewTests
RHYTHM_NEAR_MISS_REVIEW=1 RHYTHM_NEAR_MISS_URL=http://localhost:11534/ RHYTHM_NEAR_MISS_MODEL=qwen3:4b-instruct-2507-q4_K_M \
  RHYTHM_MODEL_DIR=<MiniLM's files> dotnet test engine --filter NearMissLanguageModelRun
```

The gate's runs, from the measurement build:

```sh
git checkout 41b6a8c
RHYTHM_EMBEDDING_BENCH_VARIANTS=minilm,minilm-names,minilm-hubs RHYTHM_MODEL_DIR=<MiniLM's files> \
  RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> sh engine/bench-embeddings.sh
```

The `minilm` and `minilm-names` variants took 1 m 40 s, `minilm-hubs` 51 s; MiniLM's own run answered as its locks in
both modes.

## The map's answers

`MapAnswersRun` (`RHYTHM_MAP_ANSWERS`, MiniLM on) before any change and at this branch's head, both modes: with copies
sha256 `e71e4f0b…c039` and as installed `173c803c…145c`, the hashes run 18's fix review recorded
([benchmarks.md](benchmarks.md#what-this-changes)).

## What would change the answer

- Shared names as name stars rather than pairwise links (connection-ideas.md's question 2): one star a name, linked to
  what mentions it, adds one neighbour to each star rather than several, which is what costs the chains.
- The hub correction without the mutual rule, or with a neighbour cap of its own: at today's volume it ranks better.
  Either is a new idea, to be written down before it is measured.
- A ranking of a seed's neighbours in `explore_graph` by the link's kind and strength rather than by how linked each
  neighbour is: every idea that adds links pays for it there.
- More labelled pairs, unrelated ones above all: 11 decided every false positive in the slice's count, 51 since run
  19. The titles model's review of the near misses fails on one of them (Alex's new commute and old job), so its verdict
  turns on a single label; a retry with more unrelated pairs in the band, or a rule that weighs gain against cost, is a
  new rule, written before it is measured. Before any review ships, the ideas page's question 1 (may an AI's judgement be
  stored and shown as evidence) needs the owner's answer, even without a reason.
