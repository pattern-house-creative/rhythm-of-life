# Benchmarks: the embedding models, the reranker and the thresholds

Two measurements on the synthetic parity corpus: MiniLM against bge-small-en-v1.5 on the golden set (SB-P2-06, D-025),
and the similarity thresholds re-tuned per model from the labelled pairs (SB-P2-13, D-028). Synthetic data only, in
throwaway databases, with the pinned model files placed locally (D-041), on 5 November 2026 (`sb/p2-bench`, from
`sb/main` `9c0ceb8`), on a development machine shared with other work (32 logical processors, Windows). On 9 November
bge-base-en-v1.5 was measured the same way and the optional reranker (SB-P2-12, D-026) beside combined retrieval, from
`sb/p2-models2` on the same machine ([bge-base](#bge-base), [the reranker](#the-reranker-sb-p2-12)). Compare with
[combined-retrieval.md](combined-retrieval.md) (the paths measured here) and [baseline.md](baseline.md) section 6
(the gate).

## Where it stands

- **The live model stays MiniLM.** bge-small does not pass the retrieval gate on any path in either load mode, with or
  without the query instruction, and with the map's thresholds as today or re-tuned for it: its meaning search finds
  more paraphrases, translations and long texts, and loses exact ids, chains, visibility checks and replaced facts, and
  the gate fails any type that loses 0.01 or more. EmbeddingSettings still refuses another live model at start (D-025,
  D-068); nothing here changes that, and switching stays the owner's call.
- **bge-base** (measured on 9 November with the owner's OK, D-248, D-253) fails the gate on every path in both modes too:
  its meaning search alone is clearly better than MiniLM's with copies (recall at 5 +0.057, +0.099 with the instruction),
  but the combined list and the map lose a little, as installed nothing changes, and it costs eleven times the time and
  432 MiB of memory, over D-029's 300 MB ([below](#bge-base)).
- **The reranker** (SB-P2-12) is built behind `engine.rerank`, off: both cross-encoders gain with copies and fail the
  gate's regression rule in both modes, and bge-reranker-base its latency budget too ([below](#the-reranker-sb-p2-12),
  D-251).
- **The map model** (D-256, 9 November): the owner chose bge-small for the map's similar links alone,
  `graph.similarity.model: bge-small`, absent by default. With it on every path but the map's answers as its MiniLM
  lock; the map links more related pairs and keeps the losses on chains and visibility the owner accepted
  ([below](#the-map-model-d-256)).
- **A cap on each star's similar links** (D-258, 9 November): `graph.similarity.maxLinks`, absent by default. With the map
  model on, search answers as its locks at any cap; at 6 the map path gets worse on chains of links with copies (graph-chain
  MRR −0.017 against the uncapped map); at 4 no question type gets worse in either mode ([below](#a-cap-on-each-stars-similar-links-d-258)).
- **The map relevance ideas** scored on the same labelled pairs (SB-P4-12: shared names, the hub correction, the
  near-miss check, cited together) are in [relevance.md](relevance.md): none was kept.
- **Thresholds** are recorded per model in `EmbeddingThresholds` with the precision and recall each gives on the
  labelled pairs. Every install keeps today's: only `adapters.embeddings.thresholds: tuned`, off by default, reads the
  live model's tuned ones ([../../configuration.md](../../configuration.md#embedding-models)), and on the map the gate
  does not pass MiniLM's tuned ones either ([below](#the-tuned-thresholds-on-the-map)). Since run 19 links on save
  (SB-P4-03) read the link-on-save threshold: 0.78 with `today`, MiniLM's 0.58 with `tuned`, re-derived on 104 more
  labelled pairs ([below](#run-19-more-pairs-and-link-on-save-as-a-save-reads-it)). The duplicate flag threshold has a
  reader too, the save's check (SB-P5-02, `DuplicateCheck`, through the same switch: today's 0.85 off, MiniLM's 0.63
  tuned), whose own score of every labelled pair is the one tuned here
  (`DuplicateThresholdTests.The_engines_score_gives_the_recorded_precision_on_the_labelled_pairs`); the duplicate block
  threshold has none, since no save is refused as a copy (D-211). The insight threshold has a reader, the nightly
  insight candidate pass (SB-P5-05, [below](#insight-candidates-sb-p5-05): today's 0.80 off, MiniLM's 0.52 tuned). The
  standing threshold has a reader, what `recall` fires (SB-P5-12, `StandingCheck`, [below](#standing-instructions-sb-p5-12): today's 0.67 off, MiniLM's 0.59 tuned).

## How to run it

```sh
RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> sh engine/bench-embeddings.sh
```

The script checks both folders hold `model_quantized.onnx` and `vocab.txt` and otherwise says `NOT RUN` and starts
nothing; it never downloads a model. It runs `EmbeddingBenchmarkRun` through `engine/test-db.sh` (its own throwaway
Postgres container, removed afterwards; `RHYTHM_TEST_CONTAINER` names it, by default `embedding-bench-<pid>-<time>`).
The test checks each folder's files against their pinned checksums before it measures anything. The report goes to
`RHYTHM_PARITY_OUT`: `embedding-benchmark.json` and `.md`, and each variant's full gate report,
`embedding-benchmark-gate-<variant>-<mode>.md`. `RHYTHM_EMBEDDING_BENCH_VARIANTS` (a comma list) runs fewer variants.
One run of all five variants took 4 m 39 s here. The thresholds' numbers come from `ThresholdTuningTests`, which run
in the ordinary test run whenever the model folders are set (`dotnet test engine` or `sh engine/test-db.sh`).

The gate's own run judges another model the same way:

```sh
RHYTHM_PARITY_EVAL=1 RHYTHM_PARITY_MODEL=bge-small RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> \
  RHYTHM_PARITY_GATE_MODEL_CHANGE=1 sh engine/test-db.sh --filter ParityGateRun
```

(`RHYTHM_PARITY_QUERY_INSTRUCTION=on` adds the instruction.) It writes the same verdicts and then fails, as it should,
on answers that moved from the MiniLM locks: a model change is adopted only by re-locking with a reason. Run once for
bge-small on 5 November, it gave the benchmark's verdicts and deltas path by path in both modes. The parity evaluation
takes the same two variables. Without them both run MiniLM exactly as before.

bge-base runs when its variants are named, from its own folder (its fp32 file is `model.onnx`); the benchmark widens the
passage column of its throwaway databases for its 768-dimensional vectors (`ParityLoader`), and nothing else differs:

```sh
RHYTHM_EMBEDDING_BENCH_VARIANTS=minilm,bge-base,bge-base-instruction,bge-base-tuned RHYTHM_MODEL_DIR=<MiniLM's files> \
  RHYTHM_MODEL_DIR_BGE_BASE=<bge-base's files> sh engine/bench-embeddings.sh
```

The reranker's variants are paths of the gate run, each judged against combined retrieval in the same run with the
800 ms budget (SB-P2-12, [below](#the-reranker-sb-p2-12)):

```sh
RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_RERANKER_DIR_BGE=<bge-reranker-base's files> \
  RHYTHM_RERANKER_DIR_MSMARCO=<ms-marco-MiniLM's files> \
  RHYTHM_PARITY_RECALL=rerank-bge-20,rerank-bge-10,rerank-msmarco-20,rerank-msmarco-10 \
  RHYTHM_PARITY_GATE_COMPARE="combined:rerank-bge-20=combined,combined:rerank-bge-10=combined,\
combined:rerank-msmarco-20=combined,combined:rerank-msmarco-10=combined" \
  sh engine/test-db.sh --filter ParityGateRun
```

Variants have no lock, so that run fails its tripwire for them, as it should; its report has each path's numbers and
the comparisons' verdicts.

## What is measured

Each variant is loaded into its own database in each load mode exactly as the parity evaluation loads the corpus
(`ParityLoader`), and every golden query runs on every path (`ParityEvaluator`), so a variant differs from MiniLM only
in its model, its questions' instruction and the map's similarity thresholds.

| Variant | Model | Questions | Map thresholds |
|---|---|---|---|
| `minilm` | MiniLM | as they are | today's (`graph.similarity`'s defaults): every install's |
| `bge-small` | bge-small-en-v1.5 | as they are (D-068) | today's |
| `bge-small-instruction` | bge-small-en-v1.5 | after "Represent this sentence for searching relevant passages: " | today's |
| `minilm-tuned` | MiniLM | as they are | MiniLM's tuned ones, through `adapters.embeddings.thresholds: tuned` |
| `bge-small-tuned` | bge-small-en-v1.5 | as they are | bge-small's tuned ones, as `graph.similarity` numbers |
| `bge-base` | bge-base-en-v1.5 (768 dimensions; 9 November) | as they are | today's |
| `bge-base-instruction` | bge-base-en-v1.5 | after the same instruction | today's |
| `bge-base-tuned` | bge-base-en-v1.5 | as they are | bge-base's tuned ones, as `graph.similarity` numbers |

- **Quality**: recall at 5 and 10, MRR at 10 and nDCG at 10, over each path's judged queries (a relevant answer, no
  capability the path lacks: the gate's own set, 207 queries in 103 clusters). Grades as the golden set gives them.
- **Speed**: p50 and p95 latency of each path over every measured query, each the median of three calls after a warm-up.
  Per model, outside any database: the time to load it, to embed each of the corpus's 154 passages and each of the 272
  golden questions.
- **Memory**: the process's extra working set and private memory once the model has loaded and embedded every passage
  and question, read after a full collection (the inference runtime's native memory, mostly). Approximate: the test
  process holds other things, and the runtime was warmed by a model loaded and let go first, so neither model pays for
  its start.
- **Disk**: the model's files; per load, the passages, their vectors' bytes and `resource_chunks` with its indexes.
- **The gate's verdict** (SB-P2-15): each path against the checked-in MiniLM lock of its mode, a model change declared
  where the model or the questions differ, exactly as `RHYTHM_PARITY_GATE_MODEL_CHANGE=1` does. MiniLM's own run must
  answer as the locks hold, or the benchmark fails; it answered as they hold in both modes.

## Each model's cost

| Model | Files | Load | Per passage (mean / p95) | Per question (p50 / p95) | Extra working set | Extra private memory |
|---|---|---|---|---|---|---|
| MiniLM | 23.2 MB | 74 ms | 2.6 / 5.1 ms | 0.5 / 0.8 ms | 45 MiB | 42 MiB |
| bge-small | 34.2 MB | 134 ms | 3.7 / 7.2 ms | 1.1 / 1.6 ms | 58 MiB | 56 MiB |
| bge-small, with the instruction | 34.2 MB | 134 ms | 3.8 / 7.5 ms | 1.5 / 2.1 ms | 54 MiB | 53 MiB |

Both are far inside D-029's 300 MB of extra memory. bge-small has twice MiniLM's layers: about 1.4 times the time per
passage and twice per question, which on this corpus is a millisecond. Both make 384-dimensional vectors and cut a
text into the same passages, so they store the same: 152 passages and 234 500 bytes of vectors with copies, 20 and
30 832 bytes as installed. `resource_chunks` with its indexes measured 590 or 713 kB with copies (MiniLM itself took
both in two loads: pages, not what is stored) and 197 kB as installed. The numbers above are the second of two runs,
which gave the same rankings. Each time and memory figure is that one run's (a latency the median of three calls a
query); the first run's figures were not kept, so how far they moved between the two is not on record. Amended by run
18's fix review, which found the first wording ("a few per cent") unsupported: see
[how far to trust the numbers](#how-far-to-trust-the-numbers).

## Results with copies

Every memory, note and document also saved as a `save_document` copy: the paths have the text in hand.

| Variant | Path | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 / p95 ms | Gate |
|---|---|---|---|---|---|---|---|
| minilm | fulltext | 0.816 | 0.862 | 0.773 | 0.778 | 2.7 / 3.7 | the lock's answers |
| minilm | semantic | 0.767 | 0.826 | 0.720 | 0.723 | 3.6 / 4.2 | the lock's answers |
| minilm | graph | 0.276 | 0.280 | 0.245 | 0.249 | 14.0 / 16.3 | the lock's answers |
| minilm | combined | 0.841 | 0.909 | 0.796 | 0.807 | 12.5 / 15.2 | the lock's answers |
| bge-small | semantic | 0.789 | 0.842 | 0.748 | 0.746 | 3.3 / 3.9 | **FAIL** |
| bge-small | graph | 0.272 | 0.277 | 0.230 | 0.238 | 14.5 / 15.6 | **FAIL** |
| bge-small | combined | 0.831 | 0.891 | 0.790 | 0.799 | 10.7 / 13.1 | **FAIL** |
| bge-small-instruction | semantic | 0.789 | 0.843 | 0.747 | 0.748 | 3.7 / 4.3 | **FAIL** |
| bge-small-instruction | graph | 0.272 | 0.277 | 0.230 | 0.238 | 12.8 / 15.1 | **FAIL** |
| bge-small-instruction | combined | 0.831 | 0.891 | 0.790 | 0.801 | 10.9 / 13.6 | **FAIL** |

Full text answers the same with either model (it reads no vector), so it is no candidate and is left out above; MiniLM's
paths answer exactly as their locks. Every latency is far inside D-029's 500 ms, and a difference of a few
milliseconds between two rows is within one path's spread from run to run
([below](#how-far-to-trust-the-numbers)).

The gate's reasons, over the 207 judged queries (deltas against the lock, with their 95% paired cluster intervals):

- **semantic**: overall recall at 5 +0.022 [−0.023, +0.070], MRR +0.027 [−0.014, +0.070]: a gain, but not one the
  interval shows. By type, bge-small finds more paraphrases (recall +0.144 [+0.013, +0.275]), translations
  (multilingual +0.095) and long texts (MRR +0.122 [+0.018, +0.269]), and loses exact ids (recall −0.114), chains
  (graph-chain −0.092), visibility checks (−0.064), replaced facts (stale MRR −0.133), health (MRR −0.042) and as-of
  questions (MRR −0.036). Each loss of 0.01 or more fails the regression rule (D-056). Declaring paraphrase the
  target type would pass the improvement rule, not the regression one.
- **combined**: overall recall at 5 −0.010 [−0.025, +0.000], MRR −0.005 [−0.011, −0.000]; paraphrase recall −0.025 and
  graph-chain −0.050: the keyword arm already finds what the better meaning arm adds, and fusion loses the rest.
- **graph**: MRR −0.015 [−0.032, −0.000], nDCG −0.012; graph-chain MRR −0.185. The map's similar links are made with
  today's thresholds, set for MiniLM's scores, which sit much lower than bge-small's (the median of two corpus records
  scores 0.12 under MiniLM and 0.45 under bge-small); with bge-small's own thresholds see
  [below](#the-tuned-thresholds-on-the-map).

## Results as installed

Only what the adapters and the index job write: Second Brain memories are reference-only and get no passages, so the
meaning paths have 20 passages (notes and documents) to search.

| Variant | Path | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 / p95 ms | Gate |
|---|---|---|---|---|---|---|---|
| minilm | fulltext | 0.121 | 0.121 | 0.110 | 0.107 | 2.2 / 3.2 | the lock's answers |
| minilm | semantic | 0.106 | 0.106 | 0.123 | 0.099 | 3.3 / 4.6 | the lock's answers |
| minilm | graph | 0.263 | 0.267 | 0.241 | 0.242 | 12.4 / 17.0 | the lock's answers |
| minilm | combined | 0.155 | 0.164 | 0.164 | 0.144 | 13.5 / 17.6 | the lock's answers |
| bge-small | semantic | 0.106 | 0.106 | 0.128 | 0.102 | 3.0 / 3.6 | **FAIL** |
| bge-small | graph | 0.259 | 0.264 | 0.222 | 0.228 | 12.2 / 13.3 | **FAIL** |
| bge-small | combined | 0.155 | 0.164 | 0.162 | 0.142 | 11.3 / 13.6 | **FAIL** |
| bge-small-instruction | semantic | 0.106 | 0.106 | 0.127 | 0.101 | 3.5 / 4.2 | **FAIL** |
| bge-small-instruction | graph | 0.259 | 0.264 | 0.222 | 0.228 | 12.3 / 13.5 | **FAIL** |
| bge-small-instruction | combined | 0.155 | 0.164 | 0.161 | 0.142 | 11.9 / 14.5 | **FAIL** |

- **semantic**: no type loses, but nothing gains either (MRR +0.005 [−0.005, +0.015]): with 20 passages there is
  little to rank. FAIL for want of a gain.
- **combined**: graph-chain MRR −0.025, the only loss; no gain.
- **graph**: the same losses as with copies (MRR −0.018 [−0.035, −0.002]; graph-chain MRR −0.214).

## The query instruction (D-068)

Embedding each question after "Represent this sentence for searching relevant passages: " (BAAI's instruction for
retrieval with bge-*-en-v1.5; passages as they are) changes little and helps nothing the gate counts: with copies
semantic recall at 5 is 0.789 either way, MRR at 10 0.747 against 0.748; paraphrase recall gains less (+0.106 against
+0.144, its interval now reaching below 0) and rare-name MRR loses more (−0.051 against −0.011); as installed it is
within 0.001 of no instruction. It costs a question about half a millisecond more. D-068's default, questions as they
are, stays.

## bge-base

D-025 allows bge-base (768-D) only if the benchmark shows a benefit worth its schema, storage and CPU cost. Run 18 thought
it unlikely and did not fetch it. With the owner's OK to fetch its pinned files once (D-248), it was measured on 9 November
2026 (`sb/p2-models2`, from `sb/main` `8fc6b9e`) exactly as bge-small was, by rules committed first (D-249): variants
`bge-base`, `bge-base-instruction` and `bge-base-tuned` beside `minilm`, every path in both load modes, each judged by
the gate against the MiniLM locks with the model change declared (`RHYTHM_EMBEDDING_BENCH_VARIANTS=minilm,bge-base,
bge-base-instruction,bge-base-tuned`, 6 m 50 s). MiniLM's own run answered as its locks in both modes. Its 768-dimensional
passages were held only in the benchmark's throwaway databases, whose passage column the loader widens there; an install
refuses bge-base as the live model and as the shadow. **It fails the gate on every path, in both modes, with and without
the instruction and with its own thresholds; MiniLM stays live** (D-253).

| Model | Files | Load | Per passage (mean / p95) | Per question (p50 / p95) | Extra working set | Extra private memory |
|---|---|---|---|---|---|---|
| MiniLM (this run) | 23.2 MB | 64 ms | 2.7 / 5.8 ms | 0.7 / 0.9 ms | 47 MiB | 44 MiB |
| bge-base-en-v1.5 (fp32, `BAAI/bge-base-en-v1.5` at `a5beb1e3`, 109M parameters, MIT) | 436.0 MB | 658 ms | 29.0 / 73.5 ms | 9.1 / 12.3 ms | 432 MiB | 437 MiB |
| bge-base, with the instruction | 436.0 MB | 651 ms | 29.6 / 73.7 ms | 13.1 / 16.2 ms | 434 MiB | 439 MiB |

bge-base is the first model over D-029's 300 MB of extra memory for an embedding model (by about half again). It takes
about 11 times MiniLM's time a passage and 13 times a question, and stores twice the bytes a vector: with copies 467 552
bytes of vectors against 234 500, `resource_chunks` 1.08 MB against 0.59 MB; as installed 61 520 against 30 832. Its
repository publishes only the fp32 file, which is what was measured; a quantized copy would be smaller and faster and
would be another model, with its own measurement.

**With copies** (judged queries; latency the median of three calls):

| Variant | Path | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 / p95 ms | Gate |
|---|---|---|---|---|---|---|---|
| minilm | semantic | 0.767 | 0.826 | 0.720 | 0.723 | 2.7 / 3.5 | the lock's answers |
| minilm | graph | 0.276 | 0.280 | 0.245 | 0.249 | 11.8 / 14.2 | the lock's answers |
| minilm | combined | 0.841 | 0.909 | 0.796 | 0.807 | 10.3 / 13.2 | the lock's answers |
| bge-base | semantic | 0.824 | 0.885 | 0.784 | 0.786 | 12.8 / 15.4 | **FAIL** |
| bge-base | graph | 0.263 | 0.275 | 0.232 | 0.238 | 15.7 / 17.1 | **FAIL** |
| bge-base | combined | 0.831 | 0.899 | 0.791 | 0.802 | 20.3 / 24.9 | **FAIL** |
| bge-base-instruction | semantic | 0.866 | 0.894 | 0.797 | 0.804 | 15.6 / 19.3 | **FAIL** |
| bge-base-instruction | graph | 0.263 | 0.275 | 0.232 | 0.238 | 14.3 / 16.5 | **FAIL** |
| bge-base-instruction | combined | 0.831 | 0.899 | 0.792 | 0.803 | 23.7 / 28.7 | **FAIL** |
| bge-base-tuned | semantic | 0.824 | 0.885 | 0.784 | 0.786 | 12.7 / 15.6 | **FAIL** |
| bge-base-tuned | graph | 0.263 | 0.277 | 0.236 | 0.241 | 13.3 / 15.8 | **FAIL** |
| bge-base-tuned | combined | 0.831 | 0.899 | 0.791 | 0.802 | 20.5 / 24.4 | **FAIL** |

**As installed:**

| Variant | Path | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 / p95 ms | Gate |
|---|---|---|---|---|---|---|---|
| minilm | semantic | 0.106 | 0.106 | 0.123 | 0.099 | 2.5 / 2.9 | the lock's answers |
| minilm | graph | 0.263 | 0.267 | 0.241 | 0.242 | 10.4 / 11.6 | the lock's answers |
| minilm | combined | 0.155 | 0.164 | 0.164 | 0.144 | 11.2 / 13.4 | the lock's answers |
| bge-base | semantic | 0.101 | 0.106 | 0.124 | 0.099 | 12.1 / 14.5 | **FAIL** |
| bge-base | graph | 0.250 | 0.261 | 0.227 | 0.228 | 13.5 / 14.5 | **FAIL** |
| bge-base | combined | 0.155 | 0.164 | 0.160 | 0.142 | 20.5 / 24.9 | **FAIL** |
| bge-base-instruction | semantic | 0.101 | 0.106 | 0.124 | 0.099 | 15.1 / 18.7 | **FAIL** |
| bge-base-instruction | graph | 0.250 | 0.261 | 0.227 | 0.228 | 13.6 / 15.0 | **FAIL** |
| bge-base-instruction | combined | 0.155 | 0.164 | 0.161 | 0.142 | 24.0 / 29.0 | **FAIL** |
| bge-base-tuned | semantic | 0.101 | 0.106 | 0.124 | 0.099 | 12.2 / 14.7 | **FAIL** |
| bge-base-tuned | graph | 0.251 | 0.264 | 0.231 | 0.232 | 12.8 / 14.0 | **FAIL** |
| bge-base-tuned | combined | 0.155 | 0.164 | 0.160 | 0.142 | 21.5 / 25.5 | **FAIL** |

Full text answers the same under every model and is left out. Every latency is far inside D-029's 500 ms.

The gate's reasons (deltas against the lock over the 207 judged queries, 95% paired cluster intervals):

- **semantic, with copies**: bge-base's meaning search is clearly better overall, unlike bge-small's: recall at 5 +0.057
  [+0.011, +0.106] and MRR +0.063 [+0.021, +0.106], and with the instruction +0.099 [+0.054, +0.149] and +0.076. By type
  it wins paraphrases (recall +0.181 [+0.050, +0.319]; +0.215 with the instruction), translations (multilingual recall
  +0.143; +0.238), exact ids (MRR +0.150 [+0.039, +0.269]; +0.212), long texts (MRR +0.122), commitments (recall +0.167)
  and current facts (MRR +0.226 [+0.077, +0.360]). It loses visibility checks (MRR −0.138 [−0.254, −0.042], a loss whose
  whole interval is below 0; −0.090 with the instruction), chains (graph-chain recall −0.108; with the instruction it holds
  them, +0.017), as-of questions (MRR −0.115; −0.074) and replaced facts (stale MRR −0.048; −0.036). Each loss fails the
  regression rule.
- **combined, with copies**: the list the `recall` tool and the app's search serve loses a little, as with bge-small:
  recall at 5 −0.010 [−0.025, +0.000], MRR −0.004; paraphrase recall −0.025, graph-chain recall −0.050 and MRR −0.043
  [−0.101, −0.001]; nothing gains 0.02. The keyword arm already finds most of what the better meaning arm adds.
- **graph**, both modes: the map loses (overall recall at 5 −0.013, MRR −0.012 with copies; graph-chain recall −0.108
  and MRR −0.129), and with bge-base's own thresholds (similar 0.63, documents 0.83, mutual 0.68) still graph-chain
  recall −0.108 and MRR −0.089 with copies, −0.100 and −0.102 as installed.
- **as installed**: the meaning arm has 20 passages to search and bge-base finds nothing more (recall at 5 −0.005 on the
  meaning path, MRR +0.001); combined loses graph-chain MRR −0.033 (−0.025 with the instruction).
- **The query instruction** helps bge-base's meaning search, unlike bge-small's (recall at 5 0.866 against 0.824), and
  changes nothing the combined list or the map serves.

**bge-base against MiniLM, in short**: on meaning search alone, with every text in hand, bge-base is better (it wins on
paraphrases, translations, exact ids, long texts and current facts, and loses on visibility checks, chains, as-of
questions and replaced facts); on the combined list that people and AI clients are served, and on the map, it is a little
worse; as installed it changes nothing. It also costs about eleven times MiniLM's time per text, 432 MiB of memory (over
D-029's 300 MB) and twice the storage, and it would need a migration for 768-dimensional passages. Switching stays the
owner's call, and these numbers argue against it.

**What this means.** The engine finds things two ways at once, by the words in a question and by its meaning, and blends
the two lists. A bigger meaning model understands questions asked in other words, or in another language, noticeably
better on its own. But in the blended list that search actually returns, the word search was already catching most of
those, so the bigger model's advantage disappears, and a few follow-on questions across linked notes slip down. On an install as it stands, with few texts stored for meaning
search, it changes nothing at all. Since it is ten times slower, needs more memory than the budget allows and would need
the database changed, MiniLM stays.

## The reranker (SB-P2-12)

D-026 allows a small local reranker, behind a readiness probe and a circuit breaker, off by default, turned on only if the
gate shows a gain within D-029's latency budget (p95 at most 800 ms with a reranker). Built and measured on 9 November
2026 (`sb/p2-models2`, from `sb/main` `8fc6b9e`), with the owner's OK to fetch the two cross-encoders once (D-248), by
rules committed before either ran on the golden set (D-249, `8a0103e`). **Neither passes; `engine.rerank` stays off.**

**What is built** (`engine/src/Core/Rerankers.cs`): after combined retrieval's fusion and diversity step and before any
hop, a cross-encoder scores the first 20 results against the question, each as its title and the text its excerpt comes
from (cut at 1,200 characters, a pair at most 512 tokens), and those 20 take the order of its scores; the fused scores and
the reply's shape are unchanged. The engine loads the model named by `engine.rerank` (`bge-reranker-base` or
`ms-marco-minilm`; absent or `off`: none) at start, from its pinned and checksummed files, and uses it only after a probe
pair scores the right way round. While it is loading or failed its checks, after three failures in a row (for a minute),
or when one call fails or runs past 2 s, the results keep the combined order and the answer says reranking was skipped.
With the setting off nothing is loaded or downloaded and every search answers as before ([../../configuration.md](../../configuration.md)).

| Model | Files | Parameters | Licence |
|---|---|---|---|
| bge-reranker-base (`BAAI/bge-reranker-base` at `2cfc18c9`, XLM-RoBERTa, fp32 ONNX) | 1,112 MB and its 5 MB SentencePiece model | 278M | MIT |
| ms-marco-MiniLM-L6-v2 (`cross-encoder/ms-marco-MiniLM-L6-v2` at `233902d2`, fp32 ONNX) | 91 MB and MiniLM's `vocab.txt` | 22.7M | Apache-2.0 |

Loaded and run on batches of 10 pairs of up to 320 tokens, in a process of their own, the inference runtime's start
included: 1,309 MiB of extra working set for bge-reranker-base, most of it its 1.1 GB of weights, and 377 MiB for
ms-marco-MiniLM, most of it the runtime and its buffers for a batch; approximate, measured once. D-029
sets no memory budget for a reranker; on a small home server either would sit beside the engine's other models, which
the benchmark machine cannot measure.

Both score their model cards' examples as published (`RerankerModelTests`: ms-marco 8.607 and −4.320 for the card's two
Berlin passages; bge-reranker-base reads XLM-RoBERTa's pieces, "Hello world" as 35378 and 8999).

**How it was measured.** Four variants, each a path of the gate run beside combined retrieval in both load modes: each
model reordering the first 20 and the first 10, 4 inference threads, no time budget (so the run measures the model, not
the safety net; a failure would have been an error, and there was none). Each was judged against combined retrieval of
the same run by the gate's rules, with the reranker's 800 ms. The run took 43 m 49 s; MiniLM's own paths answered as their
locks in both modes.

### With copies

| Path | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 / p95 ms | Gate against combined |
|---|---|---|---|---|---|---|
| combined | 0.841 | 0.909 | 0.796 | 0.807 | 10.4 / 13.1 | (the baseline) |
| bge-reranker-base, 20 | 0.890 | 0.919 | 0.852 | 0.857 | 802 / 1,369 | **FAIL**: regression, latency |
| bge-reranker-base, 10 | 0.879 | 0.909 | 0.856 | 0.855 | 554 / 1,141 | **FAIL**: regression, latency |
| ms-marco-MiniLM, 20 | 0.895 | 0.916 | 0.860 | 0.859 | 114 / 217 | **FAIL**: regression |
| ms-marco-MiniLM, 10 | 0.888 | 0.909 | 0.861 | 0.853 | 83 / 186 | **FAIL**: regression |

(Over the 207 judged queries; latency over every query, each the median of three calls.) Every variant gains, with
intervals above 0: overall recall at 5 +0.038 to +0.054 and MRR +0.056 to +0.064 (ms-marco at 20: +0.054 [+0.022, +0.091]
and +0.064 [+0.029, +0.101]; bge at 20: +0.049 [+0.009, +0.093] and +0.056 [+0.021, +0.091]). The gains are where combined
retrieval is weakest: paraphrase MRR +0.28 to +0.30, as-of MRR +0.24 (bge) and +0.30 (ms-marco), current MRR +0.12 to
+0.18, graph-chain recall +0.08 to +0.13. And every variant loses a few answers that were right: an exact id moved from
first to second (`q-exact-jars`: exact-id MRR −0.023), one of a long note's two answers left the top five (`q-long-tap`:
long-text recall −0.019), one of a commitment's (`q-commit-talk`: commitment recall −0.033), and a visibility check's
answer moved down (ms-marco: `q-vis-health`, visibility MRR −0.036; bge: `q-vis-december-sam` from first to ninth, sixth at depth 10,
and `q-vis-party-sam`, visibility recall −0.095). Each loss of 0.01 or more in a type fails the regression rule (D-056). No
record was revealed that combined retrieval does not reveal. bge-reranker-base's p95 is also over 800 ms at either depth.

### As installed

| Path | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | p50 / p95 ms | Gate against combined |
|---|---|---|---|---|---|---|
| combined | 0.155 | 0.164 | 0.164 | 0.144 | 10.9 / 13.8 | (the baseline) |
| bge-reranker-base, 20 | 0.159 | 0.164 | 0.171 | 0.150 | 531 / 632 | **FAIL**: regression, no gain |
| bge-reranker-base, 10 | 0.159 | 0.164 | 0.171 | 0.150 | 536 / 684 | **FAIL**: regression, no gain |
| ms-marco-MiniLM, 20 | 0.157 | 0.164 | 0.174 | 0.153 | 81 / 98 | **FAIL**: regression, no gain |
| ms-marco-MiniLM, 10 | 0.157 | 0.164 | 0.174 | 0.153 | 81 / 102 | **FAIL**: regression, no gain |

As installed most records the questions name are reference-only memories with only a title to read, and the combined
list rarely holds the answer at all, so there is little to reorder: overall MRR +0.007 to +0.011, under what the gate
could see (improvement INCONCLUSIVE). Every variant loses a graph chain's answer (`q-graph-japanese`: graph-chain recall
−0.050), bge a multilingual one (`q-ml-ja-library`, MRR −0.024) and ms-marco the same visibility check as with copies
(MRR −0.038). Both models are inside 800 ms here, with shorter texts.

### Verdict

**Not kept: `engine.rerank` stays off, and no default changes** (D-251). By the rule fixed first, a variant needed no
failed rule in either mode and a gain in one; each gains with copies and each fails the regression rule in both modes,
and bge-reranker-base also fails the latency budget with copies. Every loss is one query moving a place or two, and none
is a clear regression (no interval lies wholly below 0): D-056 reads D-028's 0.01 literally, stricter than upstream's
max(0.03, 1/n) a type, and records that the owner may prefer upstream's floor. Under it, ms-marco-MiniLM's losses with
copies would all be inside the floor (the largest, commitment recall −0.033 on 15 queries, under 1/15), and its loss as
installed, graph-chain recall −0.050 on 20 queries, would equal 1/20. That is a change of rule for the owner, not a
tuning step, and it was not applied here.

**What this means.** A reranker reads each of the top results side by side with the question and sorts them again,
instead of trusting how the word search and the meaning search ranked them. On the test questions, where every record's
full text is there to read, it put a right answer near the top much more often, above all for questions asked in other
words and questions about what was true at some time. But it also pushed a handful of answers that were already first
down a place, among them an exact ticket number and a privacy check's answer, and the gate refuses any change that makes
any kind of question even slightly worse. On an install as it is today, where most of what the questions ask about has no
stored text for it to read, it changes almost nothing. The larger model is also too slow: more than 1.3 seconds for one
question in twenty. So search stays as it is. The small model is fast (at most about a fifth of a second for nineteen questions in twenty) and close: whether one answer
moving from first to second should block a gain this size is the owner's call (D-056), and native memories with their
own text (phase 3) are where it would next be worth measuring.

## Thresholds per model (SB-P2-13)

### How the thresholds were chosen

The rules were written before the first run (`ThresholdTuning`). A pair's score is the dot product of its two texts'
vectors made by the map's own recipe (`SimilaritySuggestions.Vector`: the mean of a text's first four passages, unit
length, rounded to 4 places). For the map's thresholds (similar, and the corpus the document and mutual ones are
transferred on) each stored record is embedded as the map embeds it, its title and its text (a memory's title is made
from its words, a note has its own, a document's is part of its text here), so a map threshold means the same here as
on the map. The thresholds read on a save (link on save, the duplicates, insight, standing) embed the words alone, as
a save compares a new memory's words, which have no title, with what is stored. Every candidate is on a 0.01 grid from
0.20 to 0.99, and a pair counts when its score is at or above the threshold.

Amended by run 18's review, before any reader used a value: the first run embedded the words alone for every kind
(87 of the 89 pairs score differently with the map's titles), and counted link on save over every pair, though
SB-P4-03 links only within one scope; the two unrelated pairs that set its values were each across two scopes.

Amended by run 19 (SB-P4-03), before links on save read a value: link on save is counted only over the pairs a save
compares, two memories of one scope or a memory and an incoming save, neither held for review (a made link joins two
memories, and a held one has no passages), and each is scored as `MemoryLinkInference` reads it, the saved words alone
against each passage of the stored memory, the best (`ThresholdTuning.LinkPairs`; for the corpus's short memories the
same as before, for a long one its closest passage). And the labels grew from 89 pairs to 193
([below](#run-19-more-pairs-and-link-on-save-as-a-save-reads-it)). The numbers below are the amended rules' on the
193 pairs.

| Threshold | Reads | Positives | Negatives | Rule |
|---|---|---|---|---|
| similar | the map's dashed links (`graph.similarity.threshold`) | duplicate, near-duplicate, contradiction, related, insight (two records a reader wants shown together) | unrelated | balanced |
| linkOnSave | an automatic link written on save (SB-P4-03) | the same, over the 155 pairs of two memories of one scope a save compares | the same (62 pairs) | precision first |
| duplicateBlock | a save refused as a copy (SB-P5-02) | duplicate | every other pair | precision first |
| duplicateFlag | a save flagged for review (SB-P5-02) | duplicate, near-duplicate | every other pair | balanced |
| insight | a nightly insight candidate (SB-P5-05) | insight | related and unrelated, both stored and 30 or more days apart (duplicates and contradictions have their own review) | balanced |
| standing | a standing instruction firing for a question (SB-P5-12) | a labelled question it should fire for, or a golden question for which it is a relevant answer | every near miss, every other instruction's question and every other golden question, unless labelled; a question about its subject with another purpose counts neither way ([below](#standing-instructions-sb-p5-12)) | precision first (balanced until SB-P5-12) |
| contradiction | a close memory offered to the calling AI and queued for a person (SB-P5-03) | contradiction, over the 129 pairs a write compares (two memories of one scope, or a memory and an incoming save, neither held; the words alone on both sides) | insight, related and unrelated; a duplicate and a near-duplicate are left out, since no score tells them apart (D-211) and they have their own review | balanced |
| similarDocuments, mutual | the map's documentThreshold and mutualThreshold | too few labelled pairs to tune (2 document pairs; mutual closeness depends on every record's neighbours) | | MiniLM keeps today's; another model takes the score with the same share of the corpus's 7 140 pairs at or above it |

**Balanced** is the largest mean of the recall on the positives and the recall on the negatives, so neither the 117
pairs shown together nor the 76 unrelated ones decide it alone; among equal values, the middle one, so the threshold
sits between the scores it separates. **Precision first** is the lowest value above every negative's score (a written
link, or a refused save, is kept, so no labelled negative may reach it); with a negative at 0.99 or more the starting
value stays. Today's values are the map's defaults and D-028's starting points (Second Brain's, set for its own model).

### The numbers

Precision and recall on the labelled pairs (193 pairs since run 19, 27 incoming saves among their members; link on
save over the 155 a save compares; standing over the 10 269 pairs of the standing cases), at today's threshold and at the chosen one.
`ThresholdTuningTests` recomputes every number from the model files. Run 18's figures on its 89 pairs are in this
file's history (`2eabc48`); what moved, and why, is [below](#run-19-more-pairs-and-link-on-save-as-a-save-reads-it).

| Threshold | Today | MiniLM: today's P / R | MiniLM chosen | MiniLM: P / R | bge-small: today's P / R | bge-small chosen | bge-small: P / R |
|---|---|---|---|---|---|---|---|
| similar | 0.60 | 0.980 / 0.419 | **0.56** | 0.948 / 0.470 | 0.696 / 0.940 | **0.68** | 0.922 / 0.803 |
| similarDocuments | 0.80 | – (2 negatives, none reached) | 0.80 | – | – | **0.88** | – |
| mutual | 0.45 | – (by share of pairs) | 0.45 | – | – | **0.73** | – |
| linkOnSave | 0.78 | 1.000 / 0.301 | **0.58** | 1.000 / 0.505 | 1.000 / 0.333 | **0.67** | 1.000 / 0.602 |
| duplicateBlock | 0.95 | 0.857 / 0.462 | **0.96** | 1.000 / 0.462 | 0.889 / 0.615 | **0.96** | 1.000 / 0.538 |
| duplicateFlag | 0.85 | 0.889 / 0.615 | **0.63** | 0.553 / 1.000 | 0.792 / 0.731 | **0.75** | 0.634 / 1.000 |
| insight | 0.80 | – / 0.000 | **0.52** | 0.133 / 0.200 | 0.000 / 0.000 | **0.33** | 0.115 / 1.000 |
| standing | 0.67 | 1.000 / 0.067 | **0.59** | 1.000 / 0.167 | 0.786 / 0.367 | **0.72** | 1.000 / 0.211 |
| contradiction | 0.85 | 1.000 / 0.095 | **0.58** | 0.842 / 0.762 | 1.000 / 0.190 | **0.66** | 0.514 / 0.857 |

bge-base-en-v1.5 (9 November, D-249), by the same rules on the same 193 pairs, recorded before the benchmark read them
(`ThresholdTuningTests.Bge_bases_thresholds_are_what_the_labelled_pairs_give`):

| Threshold | Today's P / R | Chosen | Chosen P / R |
|---|---|---|---|
| similar | 0.887 / 0.872 | **0.63** | 0.959 / 0.795 |
| similarDocuments | – | **0.83** | – |
| mutual | – (by share of pairs) | **0.68** | – |
| linkOnSave | 1.000 / 0.344 | **0.68** | 1.000 / 0.570 |
| duplicateBlock | 0.875 / 0.538 | **0.97** | 1.000 / 0.538 |
| duplicateFlag | 0.800 / 0.769 | **0.78** | 0.727 / 0.923 |
| insight | 0.000 / 0.000 | **0.68** | 0.286 / 0.200 |
| standing | 0.957 / 0.244 | **0.69** | 1.000 / 0.189 |
| contradiction | 1.000 / 0.190 | **0.66** | 0.526 / 0.952 |

Its scale sits between the other two (the median corpus pair 0.47, the 99th percentile 0.70). Its precision-first values
(link on save, standing) rest on pairs labelled where MiniLM and bge-small scored high, so unlike theirs no test shows
every pair above them labelled; no install reads any of them, since bge-base can be neither the live model nor the
shadow.

What they say:

- **The scales differ.** As the map scores them, two corpus records score 0.12 at the median under MiniLM and 0.53
  under bge-small (the 99th percentiles 0.49 and 0.75): one number cannot serve both models, which is why the thresholds
  are per model.
- **similar**: with the 60 unrelated pairs of one scope run 19 added, most of them memories of one person that score
  high (a train, a book, a payment in common), MiniLM's best balance is 0.56, just under today's 0.6 (it finds 47% of
  the pairs shown together instead of 42%, linking 3 of the 76 unrelated pairs instead of 1), and bge-small's 0.68 (80%
  instead of 94%, 8 unrelated pairs instead of 48). Of the corpus's 7 140 pairs 32 reach 0.56 under MiniLM, against 21
  at 0.6, so the map's cap of three neighbours a star seldom decides; the gate judges the result on the map
  ([below](#the-tuned-thresholds-on-the-map)).
- **linkOnSave**: over the 155 pairs of two memories of one scope, scored as a save reads them, precision first puts
  MiniLM at 0.58 and bge-small at 0.67, each just above the highest of the 62 unrelated pairs (MiniLM's: a bookshop
  reading group and a talk on the library's catalogue, 0.5794; bge-small's: an eye test and the library's catalogue
  migration, 0.6637), finding 51% and 60% of the 93 pairs shown together. Every pair a save compares in the corpus at
  0.43 or more under MiniLM (0.62 under bge-small) is labelled, so these precisions hold over every such pair of the
  corpus, not a sample of it; the corpus saved in order through links on save draws 46 links at 0.58, all 46 labelled
  pairs shown together (`MemoryLinkCorpusTests`). Today's 0.78 finds 30% and 33%.
- **duplicate**: no threshold tells a duplicate from a contradiction that changes one number or day. A refused save
  needs 0.96 under either model (today's 0.95 refuses a near-duplicate under both, and a contradiction is at 0.92
  under bge-small and 0.88 under MiniLM), and then finds about half the duplicates (6 and 7 of 13). Flagging for
  review: MiniLM at 0.63 flags every duplicate and near-duplicate with 21 other pairs, bge-small at 0.75 with 15 (run
  18's 0.72 and 0.78 flagged 91% and all, with 9 and 7 others; the run 19 contradictions of an incoming save with an
  older memory are among the new others). SB-P5-02 should flag, compare the facts, and refuse rarely.
- **insight**: a weak signal under either model, now weaker. Today's 0.80 finds none of the 10 insight pairs; the best
  balance finds 2 under MiniLM, with 13 wrong, and under bge-small no threshold does better than chance on run 19's
  stored related and unrelated pairs 30 or more days apart, so the rule picks 0.33, where every pair is a candidate.
  The pairs that make an insight (bills down since the boiler was replaced, cover for a car that was sold) are not
  alike in words or meaning, so SB-P5-05 needs more than a similarity: upstream's other factors (time gap, importance,
  topic, volatility).
- **contradiction** (run 20, SB-P5-03): today's 0.85 has no threshold of its own: the duplicate flag is all that named a
  contradiction before, so a new reader takes it and is no looser. It finds 2 of the 21 labelled contradictions under MiniLM and 4
  under bge-small, none wrongly. MiniLM at 0.58 finds 16 with 3 others (P 0.842, R 0.762; counts 16, 3, 5, 105); bge-small at 0.66
  finds 18 with 17 others (P 0.514, R 0.857; counts 18, 17, 3, 91). A candidate changes nothing and only asks, so a false one costs a
  look, not a fact; no score tells a contradiction from a copy (D-211), which is why the calling AI and a person decide. In-sample, as
  every figure here, and on 21 positives.
- **standing**: run 18 had two positives (one standing memory, two questions it answers), so balanced chose where both
  fire, which fired it for 31 other questions under MiniLM and 11 under bge-small, and D-211 asked for more labels before
  adopting a value. SB-P5-12 labelled them and takes the threshold by precision first:
  [below](#standing-instructions-sb-p5-12).

### How far to trust the numbers

Added by run 18's fix review.

- **The thresholds' precision and recall are in-sample.** Each threshold was chosen on the labelled pairs (193 since
  run 19, the 155 a save compares for link on save, the 10 269 pairs of the standing cases for standing) and is scored on the same pairs; there
  is no held-out set, so each figure flatters the value it chose. The 95% Wilson intervals on the chosen values' counts
  (`ThresholdTuningTests.Recorded`) are still wide (standing's are [below](#standing-instructions-sb-p5-12)). similar: MiniLM at 0.56 precision 0.948 [0.86, 0.98] (55 of 58) and
  recall 0.470 [0.38, 0.56] (55 of 117), 73 of the 76 unrelated pairs kept out, 0.961 [0.89, 0.99]; bge-small at 0.68
  0.922 [0.85, 0.96] and 0.803 [0.72, 0.87] (94 of 102, 94 of 117), 68 of 76 kept out, 0.895 [0.81, 0.95].
  linkOnSave: precision 1.000 [0.92, 1.00] (47 of 47) under MiniLM and [0.94, 1.00] (56 of 56) under bge-small,
  recall 0.505 [0.41, 0.60] and 0.602 [0.50, 0.70] (of 93), and its 62 unrelated pairs give [0.94, 1.00] for keeping
  one out (run 18's 5 gave [0.57, 1.00]); since every pair a save compares in the corpus at or above the chosen value
  is labelled, the precision is the corpus's own there, though the corpus is small and synthetic. duplicateBlock:
  precision 1.000 [0.61, 1.00] (6 of 6) and [0.65, 1.00] (7 of 7), recall 0.462 [0.23, 0.71] and 0.538 [0.29, 0.77].
  duplicateFlag: precision 0.553 [0.41, 0.69] and 0.634 [0.48, 0.76], recall 1.000 [0.87, 1.00] (26 of 26) under both.
  insight: precision 0.133 [0.04, 0.38] (2 of 15) and 0.115 [0.06, 0.20] (10 of 87), recall 0.200 [0.06, 0.51] and
  1.000 [0.72, 1.00]. No value here should be adopted on its point figure alone; the gate on the map, not these counts,
  decides the map's thresholds, and the labels above every link on save threshold decide that one.
- **Latency is one run's.** The same MiniLM paths, timed by the benchmark and by the gate's own runs on the benchmark machine,
  gave p95s (full text, semantic, graph, combined) with copies of 3.7 / 4.2 / 16.3 / 15.2 ms (the benchmark), 3.9 /
  5.1 / 22.2 / 17.7 ms (the gate at run 18's integration) and 5.4 / 6.0 / 24.1 / 22.0 ms (the gate at its fix review),
  and as installed of 3.2 / 4.6 / 17.0 / 17.6, 2.4 / 3.0 / 11.8 / 13.6 and 2.4 / 2.9 / 11.8 / 13.2 ms: ranges of 3.7 to
  5.4, 4.2 to 6.0, 16.3 to 24.1 and 15.2 to 22.0 ms with copies, 2.4 to 3.2, 2.9 to 4.6, 11.8 to 17.0 and 13.2 to
  17.6 ms as installed, up to about one and a half times apart for one path. The two do not time quite the same thing
  (the benchmark every measured query, the gate its 207 judged ones), and neither repeats its timing pass. Read a
  latency here as a range: only D-029's 500 ms, which every run meets by far, is decided by them, and no difference
  between two variants of a few milliseconds is one. The quality figures do not vary: the same code and model answer
  the same, and MiniLM's paths answered as the gate's locks in each of these runs.

### Run 19: more pairs, and link on save as a save reads it

D-211 asked SB-P4-03 to label more unrelated pairs of one scope before adopting a link on save threshold, since run
18's rested on 5. Run 19 took every pair a save compares in the corpus (two memories of one scope, or a memory and an
incoming save, neither held: 2 014 pairs) that scores 0.43 or more under MiniLM or 0.62 or more under bge-small as a
save reads it, the two models' run 18 values, and labelled each one not labelled yet by meaning: 104 pairs (60
unrelated, 31 of them hard; 36 related; 5 contradictions of an incoming save with an older memory; 2 near-duplicates;
1 duplicate). The scores chose which pairs were read, never their labels ([fixtures.md](fixtures.md#labelled-pairs)).
Adding negatives can only raise a precision-first value, so the new one sits above both floors (0.58 and 0.67), and
every pair at or above it in the corpus is labelled, none of them unrelated
(`ThresholdTuningTests.Every_pair_a_save_compares_above_MiniLMs_floor_is_labelled`, and bge-small's).

The added pairs come from one region of scores, the pairs a save finds alike, and most are two memories of one person.
They count for every kind, as the rules say, so every tuned value was recomputed (the tables above): MiniLM's similar
0.43 to 0.56, duplicate flag 0.72 to 0.63, insight 0.45 to 0.52; bge-small's similar 0.67 to 0.68, duplicate flag 0.78
to 0.75, insight 0.52 to 0.33 (no better than chance); duplicate block and standing as before. All stay behind
`adapters.embeddings.thresholds: tuned`, off; the map's were measured again (below), and the others still have no
reader. A builder who adds pairs later recomputes on the union.

### The tuned thresholds on the map

The map is the only reader today, and the gate judges its links through the graph path (`explore_graph`). With
`adapters.embeddings.thresholds: tuned`, MiniLM's similar links start at 0.43 (documents 0.80, mutual 0.45 as today, so
the mutual rule adds no link between records other than two documents; two documents that are each other's closest
are still linked from 0.45 up to their own 0.80, as today). Only the graph path moves. Measured again at run 18's
integration with the amended values (`RHYTHM_EMBEDDING_BENCH_VARIANTS=minilm,minilm-tuned,bge-small,bge-small-tuned`,
3 m 28 s; MiniLM's own run answered as its locks):

| Variant | Mode | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | Answers changed | Gate |
|---|---|---|---|---|---|---|---|
| minilm | copies | 0.276 | 0.280 | 0.245 | 0.249 | none (the lock's) | |
| minilm-tuned | copies | 0.271 | 0.280 | 0.236 | 0.243 | 40 | **FAIL** |
| minilm | installed | 0.263 | 0.267 | 0.241 | 0.242 | none (the lock's) | |
| minilm-tuned | installed | 0.261 | 0.267 | 0.229 | 0.235 | 37 | **FAIL** |
| bge-small | copies | 0.272 | 0.277 | 0.230 | 0.238 | 64 | **FAIL** |
| bge-small-tuned | copies | 0.266 | 0.277 | 0.240 | 0.246 | 54 | **FAIL** |
| bge-small | installed | 0.259 | 0.264 | 0.222 | 0.228 | 63 | **FAIL** |
| bge-small-tuned | installed | 0.252 | 0.265 | 0.233 | 0.237 | 52 | **FAIL** |

More links spread a chain's answers: with MiniLM's tuned thresholds graph-chain MRR −0.089 [−0.217, +0.040] with
copies and −0.123 [−0.228, −0.039] as installed, overall MRR −0.009 and −0.012, and no type gains (the first run, at
0.44, gave −0.081 and −0.110). So the labelled pairs' better balance does not carry over to the map's answers, and
MiniLM's tuned thresholds stay behind the switch, off: D-028 keeps a change only when the gate passes. bge-small's own
thresholds, now set on the map's scores, cut its map losses (graph-chain MRR −0.081 with copies and −0.102 as
installed, against −0.185 and −0.214 with today's) but do not pass: graph-chain recall −0.108 and visibility MRR
−0.051 in both modes. Under bge-small 118 of the corpus's 120 records have a third closest at 0.6 or more and 68 at
0.67 or more, so the cap of three neighbours still decides most links. The map's remaining losses with bge-small are
the model's.

Measured again at run 19 with the values recomputed on 193 pairs (MiniLM's similar 0.56, so the mutual rule links two
records that are each other's closest from 0.45 up, as today's 0.6 does; bge-small's 0.68; same command, 4 m 11 s;
MiniLM's own run answered as its locks):

| Variant | Mode | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | Answers changed | Gate |
|---|---|---|---|---|---|---|---|
| minilm-tuned | copies | 0.276 | 0.280 | 0.245 | 0.250 | 2 | **FAIL** (no loss, no gain) |
| minilm-tuned | installed | 0.263 | 0.267 | 0.241 | 0.242 | none (the lock's) | **FAIL** (not a candidate) |
| bge-small-tuned | copies | 0.267 | 0.279 | 0.241 | 0.247 | 50 | **FAIL** |
| bge-small-tuned | installed | 0.252 | 0.265 | 0.234 | 0.237 | 48 | **FAIL** |

At 0.56 MiniLM's tuned map is within a few links of today's: with copies two answers move (graph-chain MRR +0.004
[+0.000, +0.013]) and as installed none, so no type loses and none gains 0.02; the gate fails it for want of a gain
(D-056), not for a loss as at 0.43, and D-028 keeps it off. bge-small's still loses graph-chain recall (−0.092 with
copies, −0.108 as installed) and visibility MRR (−0.051).

## The map model (D-256)

On 9 November 2026 the owner chose bge-small for the map alone (D-256): `graph.similarity.model: bge-small` gives each
star a second vector from bge-small, made from the same title and text by the same recipe (`SimilaritySuggestions.Vector`:
the mean of the first four passages, unit length), stored beside the live one in the star's snapshot with its model's id
(`MapEmbedding`, `MapEmbeddingModel`) and read only by the map's similar and copy links, with bge-small's recorded
thresholds (similar 0.68, documents 0.88, mutual 0.73; the copy rule's 0.95 as before). MiniLM keeps every other job.
Absent, nothing changes. Measured on `sb/p4-mapmodel` from `main` `1244e59f`, synthetic data only, on the same machine.

**Every other path answers as its lock.** The gate with the map model on (`RHYTHM_PARITY_MAP_MODEL=bge-small`), against the
MiniLM locks with no model change declared and no re-lock: full text, meaning search and combined retrieval answered
exactly as their locks in both load modes (no answer changed); only the map path moved, 50 answers with copies and 48 as
installed, the counts run 19 recorded for `bge-small-tuned`. Without the key the gate passed on every path in both modes
(every answer the lock's) and the evaluation ran as before; the evaluation ran with the key on too.

**The map path** (the gate's `explore_graph` path, 207 judged queries):

| Mode | Map | Recall@5 | Recall@10 | MRR@10 | nDCG@10 | Gate |
|---|---|---|---|---|---|---|
| copies | MiniLM (the lock) | 0.276 | 0.280 | 0.245 | 0.249 | |
| copies | bge-small, map only | 0.267 | 0.279 | 0.241 | 0.247 | **FAIL**: graph-chain recall −0.092, graph-chain MRR −0.068, visibility MRR −0.051; no gain |
| installed | MiniLM (the lock) | 0.263 | 0.267 | 0.241 | 0.242 | |
| installed | bge-small, map only | 0.252 | 0.265 | 0.234 | 0.237 | **FAIL**: recall@5 −0.010, nDCG −0.011, graph-chain recall −0.108, graph-chain MRR −0.089, visibility MRR −0.051; no gain |

These are run 19's `bge-small-tuned` map numbers to the third place ([above](#the-tuned-thresholds-on-the-map)): the map
path reads nothing but the map's links, so drawing them from a second vector changes the map exactly as switching the
live model with its own thresholds did, and nothing else. These are the losses the owner accepted.

**The labelled pairs.** By score alone, on the 193 labelled pairs at bge-small's similar threshold: precision 0.922, recall
0.803 (94 of 117 pairs shown together, 8 of 76 unrelated), against MiniLM's 0.980 and 0.419 at today's 0.6
([above](#the-numbers); `ThresholdTuningTests` recomputed both in this run's checks). On the map's own links, the 124 of
those pairs the map may join, with the stars as the map lists them (the stated links as references) and the map's rules
(three neighbours, the mutual rule, the copy rule; `MapModelPairsTests`):

| Links | Shown together, linked | Unrelated, linked | Precision | Recall |
|---|---|---|---|---|
| similar links, MiniLM | 20 of 73 | 7 of 51 | 0.741 | 0.274 |
| similar links, bge-small map | 26 of 73 | 7 of 51 | 0.788 | 0.356 |
| the map (similar and stated links), MiniLM | 52 of 73 | 7 of 51 | 0.881 | 0.712 |
| the map, bge-small map | 58 of 73 | 7 of 51 | 0.892 | 0.795 |

On the map the gain is smaller than the scores alone suggest: 32 of the 73 pairs shown together are links the corpus
states, which the map never compares again (they are joined either way), and each star keeps at most three similar
links. Of the other 41, MiniLM links 20 and bge-small 26; both link 7 unrelated pairs.

**Links made.** Over the corpus's stars as the harness lists them, MiniLM draws 58 similar links, 46 of them by the mutual
rule; the bge-small map 155, 4 by the mutual rule: bge-small's scores spread out, so its main threshold does the work
the mutual rule did for MiniLM's bunched ones. On the gate's full maps (catalog stars and copies included, with scopes'
bridging): with copies 61 similar links (35 by the mutual rule) against 142 (4), as installed 50 (33) against 130 (4); no
copy link in any.

**Cost.** One whole map rebuild of the parity corpus, timed by the gate's loader in two runs each (the first rebuild
with the model makes every map vector, later ones reuse them while the text is unchanged): with copies 1.3 to 1.5 s with
MiniLM alone and 2.6 s with the map model, as installed 1.3 to 1.5 s and 3.2 to 3.3 s. bge-small's pinned file is the int8 quantized export, 34.2 MB on
disk (the fp32 weights, about 130 MB, are not what the registry pins); loaded and embedding the corpus it added 58 MiB of
working set in run 18's benchmark ([each model's cost](#each-models-cost)), and it embeds a passage in about 3.7 ms
against MiniLM's 2.6 ms. The snapshot of each star grows by one 384-number vector (about 3 kB of JSON).

**What this means.** The map draws a dashed line between two things in your records that are about the same subject. The
small model that does this today gives almost every pair a similar score, so it only dares to draw lines it is very sure
of, and misses more than half of the related pairs. bge-small spreads its scores out, so it can draw more of the right
lines without drawing more wrong ones: on the test set's pairs, six more related pairs are joined on the map and not one
more unrelated pair. Search, the AI's recall and everything that checks a new memory keep using the model they use today,
so they answer exactly as before. The price is on the map itself: with more lines to follow, a question that walks a chain of
links from one record to the next finds the right record a little less often, and questions about who may see a record
rank their answer a little lower. It costs one to two seconds more for each map rebuild of the test set, and about 60 MB
of memory.

bge-base was not measured as a map model: it was dropped on 9 November (D-256).

### A cap on each star's similar links (D-258)

A cap on the similar links each star keeps was approved on 9 November 2026 (D-258): `graph.similarity.maxLinks: N`
keeps a similar link only while it is among the N strongest similar links of both its stars, the mutual rule's ranked by
score with the rest, a tie by graph id; copy links are neither capped nor counted. `neighbors` caps what a star picks, not
what it receives, so a few general memories can hold a large share of a map's similar links. The condition for adopting it:
search stays exactly the same and the map path gets no worse. Measured on `sb/p4-linkcap` from `main` `5314cdd5`,
synthetic data only, on the same machine, both maps with `graph.similarity.model: bge-small`; the baseline is
that map without the cap (`MapLinkCapRun`, caps 4 to 10, both load modes; `MapModelPairsTests` for the labelled pairs).

**Search answers as its locks.** The gate with the map model on and the cap at 6, then at 4
(`RHYTHM_PARITY_MAP_MODEL=bge-small`, `RHYTHM_PARITY_MAP_MAXLINKS`), against the MiniLM locks with no model change
declared and no re-lock: full text, meaning search and combined retrieval (the recall tool's path) answered exactly as
their locks in both load modes at both caps, no answer changed; only the map path moved (47 answers with copies and 45
as installed at 6, 45 and 43 at 4). Without the key the gate passed on every path in both modes, every answer the
lock's, and the evaluation ran. Against the MiniLM lock the capped map keeps the visibility loss D-256 accepted (MRR
−0.051) and changes its chain losses: at 6 graph-chain recall −0.075 and MRR −0.085 with copies, −0.075 and −0.088 as
installed (uncapped −0.092 and −0.068, −0.108 and −0.089); at 4 −0.017 and −0.029, −0.017 and −0.038.

**The map path** (`explore_graph`, 207 judged queries), the cap against no cap. Each cell is recall at 5 / recall at 10 /
MRR at 10 / nDCG at 10. Only graph-chain and exact-id moved; every other type with judged queries (commitment, health,
long-text, multilingual, paraphrase, rare-name, stale, visibility) answered alike with and without either cap:

| Mode | Type | Judged | No cap | Cap 6 | Cap 4 |
|---|---|---|---|---|---|
| copies | overall | 207 | 0.267 / 0.279 / 0.241 / 0.247 | 0.269 / 0.280 / **0.239** / **0.245** | 0.275 / 0.280 / 0.244 / 0.249 |
| copies | graph-chain | 20 | 0.867 / 0.983 / 0.592 / 0.680 | 0.883 / 1.000 / **0.575** / **0.663** | 0.942 / 1.000 / 0.631 / 0.702 |
| copies | exact-id | 22 | 0.455 / 0.455 / 0.455 / 0.439 | 0.455 / 0.455 / 0.455 / **0.437** | 0.455 / 0.455 / 0.455 / 0.439 |
| installed | overall | 207 | 0.252 / 0.265 / 0.234 / 0.237 | 0.256 / 0.267 / 0.234 / **0.236** | 0.261 / 0.267 / 0.239 / 0.240 |
| installed | graph-chain | 20 | 0.850 / 0.983 / 0.575 / 0.671 | 0.883 / 1.000 / 0.577 / **0.665** | 0.942 / 1.000 / 0.627 / 0.700 |
| installed | exact-id | 22 | 0.455 / 0.455 / 0.455 / 0.439 | 0.455 / 0.455 / 0.455 / **0.437** | 0.455 / 0.455 / 0.455 / 0.439 |

In bold, every number that got worse. **At 6 the map path gets worse**: with copies graph-chain MRR −0.017 and nDCG −0.017
(q-graph-kitchen, q-graph-honey, q-graph-race and q-graph-plot rank their answer lower), which the gate's regression rule
fails (a type losing 0.01), and overall MRR −0.002 and nDCG −0.002; as installed graph-chain nDCG −0.006 and overall nDCG
−0.001, inside the rule; in both modes exact-id nDCG −0.002 (q-exact-kr5521). Graph-chain recall at 5 rises (+0.017 and
+0.033). **At 4 nothing gets worse**: no type loses in any of the four numbers in either mode, graph-chain gains in all
four (recall at 5 +0.075 and +0.092, MRR +0.039 and +0.052, nDCG +0.022 and +0.029) and every other type answers as without
the cap. The gate passes neither: no overall gain reaches its 0.02 margin with an interval above 0, so 4 is a change the
map path measures no worse at, not a measured gain.

Every cap measured, by the gate's regression rule (no type loses 0.01 in recall or MRR) and strictly (any of the four
numbers lower in any type):

| Cap | Copies: the gate's rule | Copies: anything lower | Installed: the gate's rule | Installed: anything lower |
|---|---|---|---|---|
| 4 | pass | nothing | pass | nothing |
| 5 | pass | exact-id nDCG −0.002, graph-chain nDCG −0.001, overall nDCG −0.0003 | pass | exact-id nDCG −0.002 |
| 6 | **fail**: graph-chain MRR −0.017 | graph-chain MRR and nDCG −0.017, exact-id nDCG −0.002, overall MRR and nDCG −0.002 | pass | graph-chain nDCG −0.006, exact-id nDCG −0.002, overall nDCG −0.001 |
| 7 | **fail**: graph-chain MRR −0.021 | graph-chain MRR and nDCG | **fail**: graph-chain MRR −0.019 | graph-chain MRR and nDCG |
| 8 | **fail**: graph-chain MRR −0.025 | graph-chain MRR and nDCG | **fail**: graph-chain MRR −0.032 | graph-chain MRR and nDCG |
| 9 | **fail**: graph-chain MRR −0.021 | graph-chain MRR and nDCG | **fail**: graph-chain MRR −0.029 | graph-chain MRR and nDCG |
| 10 | **fail**: graph-chain MRR −0.021 | graph-chain MRR and nDCG | **fail**: graph-chain MRR −0.021 | graph-chain MRR and nDCG |

**Links made**, on the gate's full maps (catalog stars and copies included; a link counted at both its stars; the median
over the stars with a similar link):

| Mode | Map | Similar links (by the mutual rule) | Stars with one | Most a star | Median | Stars left with none |
|---|---|---|---|---|---|---|
| copies | no cap | 142 (4) | 95 | 15 | 2 | |
| copies | cap 6 | 103 (4) | 90 | 6 | 2 | 5 |
| copies | cap 4 | 81 (4) | 87 | 4 | 2 | 8 |
| installed | no cap | 130 (4) | 81 | 15 | 3 | |
| installed | cap 6 | 90 (4) | 76 | 6 | 2 | 5 |
| installed | cap 4 | 68 (4) | 73 | 4 | 2 | 8 |

Caps 5, 7, 8, 9 and 10 drew 94, 111, 116, 122 and 126 with copies (6, 5, 5, 5 and 3 stars left with none) and 81, 98, 103,
109 and 114 as installed. No copy link in any map, as before. Over the stars as the pairs harness lists them: 155 similar
links without the cap (89 stars, most 17, median 3), 103 at 6 (80 stars, most 6, median 2, 9 left with none), 77 at 4 (76
stars, most 4, median 2, 13 left with none).

**The labelled pairs** (the 124 the map may join, as [above](#the-map-model-d-256)):

| Links | Shown together, linked | Unrelated, linked | Precision | Recall |
|---|---|---|---|---|
| similar links, bge-small map | 26 of 73 | 7 of 51 | 0.788 | 0.356 |
| similar links, cap 6 | 23 of 73 | 7 of 51 | 0.767 | 0.315 |
| similar links, cap 4 | 21 of 73 | 6 of 51 | 0.778 | 0.288 |
| the map (similar and stated links), bge-small | 58 of 73 | 7 of 51 | 0.892 | 0.795 |
| the map, cap 6 | 55 of 73 | 7 of 51 | 0.887 | 0.753 |
| the map, cap 4 | 53 of 73 | 6 of 51 | 0.898 | 0.726 |

Here the cap takes related pairs away (three at 6, five at 4) and only at 4 one unrelated pair; every value from 4 to 10
loses related pairs (the map links 55 to 57 of 73 at 5 to 10). The test set has no hub of the kind a real install grows: its
most-linked star has 15 or 17 similar links, where a few general notes in a real map can hold several times that, so the
noise the cap is for is mostly not in it. What the cap does to such a map was not measured here.

**What this means.** A few general notes (how the system works, say) are a little like everything, so every planning
document drew a line to them and they gathered dozens of lines each. The cap lets each record keep only its few strongest
lines, and a line stays only if both ends want it, so a general note keeps the documents it is closest to and the rest lose
their line to it. On the test set, at the six asked for, questions that follow a chain of links rank their answer a little
lower with copies (four of the twenty), which breaks the owner's "no worse" condition; at four nothing gets worse, and
those chain questions find their answer a little more often and a little higher. Search, the AI's recall and combined
retrieval answer exactly as before. The cost: some records lose every dashed line (8 of about 90 at four, 5 at six), and on
the test set's labelled pairs a few related pairs lose theirs, since this test set has no hub of the kind the cap is for.

## What this changes

Nothing either for an install that does not set `graph.similarity.maxLinks` (D-258; absent by default, and the example
settings and `rhythm init` write no such line): without it the map's links are as before, byte for byte
(`MapLinkCapTests`), and the gate without it answered as its locks on every path in both modes.

Nothing for any install that does not set `graph.similarity.model` (absent by default; the example settings and `rhythm
init` write no such line): without it no star's snapshot holds a second vector and the map is drawn exactly as before
([above](#the-map-model-d-256)): `MapAnswersRun` gave every map answer in both modes byte for byte the same on `main`
`1244e59f` and on `sb/p4-mapmodel` without the key (with copies sha256 `a7429f94…e749`, as installed `1a35a57f…cadd`,
which supersede the two below as today's). Since the models run, `engine.rerank` exists and is off (absent means off; the example settings
and `rhythm init` write no such line), and with it off nothing is loaded or downloaded and every search answers as before:
MiniLM's paths answered as their locks in both modes in the reranker's gate run and in the bge-base benchmark, and again
in the run that checked the locks without any variant. bge-base is in the registry for measurement only and refused as
the live model and as the shadow. The live model is MiniLM, and EmbeddingSettings refuses another (D-025); every threshold an
install reads is today's unless it sets `adapters.embeddings.thresholds: tuned` (off; the example settings and `rhythm
init` write no such line), and since run 19 links on save (SB-P4-03, only with `memory.tools` on) read the link on save
threshold through the same switch, 0.78 without it; with it off the map's answers are byte for byte as before, and the
evaluation and the gate answer as their locks (no re-lock). The map's answers were not compared at run 18's integration;
run 18's fix review ran `MapAnswersRun` (`RHYTHM_MAP_ANSWERS`, MiniLM on) on `9c0ceb8`, exported with `git archive`, and
on its own code (run 18 with the fix review's changes, none of them in the map): both modes byte for byte the same, with
copies sha256 `e71e4f0b…c039` and as installed `173c803c…145c` (the review's own run at `c3e0f9d` gave the same two).
The hashes the program's progress log records for runs 4 to 10 (`79d0ac2d…`, `d67ce4ad…`, then `7ce2f759…`, `a02c0f4f…`)
are those runs' answers and no longer today's; later runs compared two builds' files without recording a hash, so these
two supersede them; run 19 ran it again on `2eabc48` and on links on save's code (SB-P4-03, `sb/p4-autolinks`) and got
the same two files, byte for byte. At the unit level,
`EmbeddingThresholdTests.Off_the_map_reads_graph_similarity_as_before_and_tuned_reads_the_live_models_own` checks that
without `tuned` the map is configured from `graph.similarity` exactly as before.

For the owner, when it comes to it: switching to bge-small would need a gate pass first, and these numbers point the
other way: on this corpus its better meaning search does not survive fusion with the keyword arm, and it costs exact
ids, chains and replaced facts. More paraphrase and multilingual questions in the golden set, or upstream's per-type
tolerance instead of D-056's 0.01, are the questions to settle before measuring it again.

## Standing instructions (SB-P5-12)

D-211 found the standing threshold resting on two questions of one memory, and asked for more labels before any value was
adopted. The cases are `standing-cases.json` ([fixtures.md](fixtures.md#standing-cases)): 22 instructions, 88 questions
written to fire with the 2 golden ones that name the corpus's own (90 positives), 66 written near misses, 44 written
questions about an instruction's subject with another purpose and 27 such pairs read among the others (71 reported apart),
and every other pair of an instruction and a question as a negative: 10 179 negatives, of which 25 are read and labelled
near misses from the other questions and the golden set. The scores read are the reader's: the question as the model embeds
questions (no instruction, D-068), against the instruction's words by the map's recipe (`ThresholdTuning.Standing`,
`StandingCheck`).

The rule changed from balanced to precision first, the lowest score above every negative: a wrong instruction applied to an
answer costs more than a right one left out, and a written link and a refused save take the same rule for the same reason.
It was changed before any value was read through it, and no reader had a value before this item. `ThresholdTuningTests`
recomputes every count below from the model files.

| Model | Value | Fires | Wrong fires | Precision | Recall (of 90) | Intent fired (of 71) | Instructions that fire (of 22) |
|---|---|---|---|---|---|---|---|
| MiniLM, today | 0.67 | 6 | 0 | 1.000 | 0.067 | 2 | 5 |
| MiniLM, **chosen** | **0.59** | 15 | 0 | 1.000 | 0.167 | 10 | 7 |
| bge-small, at today's | 0.67 | 42 | 9 | 0.786 | 0.367 | 19 | 13 |
| bge-small, **chosen** | **0.72** | 19 | 0 | 1.000 | 0.211 | 5 | 8 |

What it costs below the chosen value, for whoever wants more recall and will take wrong fires:

| Value | MiniLM: P / R (wrong fires) | bge-small: P / R (wrong fires) |
|---|---|---|
| 0.50 | 0.684 / 0.289 (12) | 0.044 / 0.889 (1 722) |
| 0.55 | 0.760 / 0.211 (6) | 0.113 / 0.789 (557) |
| 0.57 | 0.947 / 0.200 (1) | 0.178 / 0.744 (310) |
| 0.60 | 1.000 / 0.167 (0) | 0.303 / 0.622 (129) |
| 0.65 | 1.000 / 0.078 (0) | 0.643 / 0.400 (20) |
| 0.70 | 1.000 / 0.044 (0) | 0.897 / 0.289 (3) |

What they say:

- **It fires when a question is close to the instruction's own words, and rarely otherwise.** Precision first finds about one
  question in six under MiniLM and one in five under bge-small. A question that uses the same words fires ("call the vet about
  a check-up for Pepper" 0.72; "draft a notice about the catalogue move" 0.77); one that says the situation in other words
  mostly does not ("get an electrician in to look at the garden office" 0.19 against "When booking a tradesperson"; "any ideas
  for keeping fit this month?" 0.19 against "When suggesting exercise for Sam"). Fifteen of the 22 instructions never fire
  under MiniLM on their questions. Say so to the person: an instruction that should come up often is worth writing in the
  words they would ask in.
- **MiniLM's highest negative is close to its highest positives.** "is sam still paying for the climbing gym?" reaches 0.58
  against "When suggesting exercise for Sam, leave out climbing until the wrist has healed", and "what is sam's job?" 0.56
  against the choir's one job a term. No threshold separates a shared word from a shared situation.
- **The corpus's own standing memory fires only for its relevant golden question.** At 0.59 "does alex like going by rail or by
  plane?" (0.64) fires it, and no other golden question; "standing instruction for planning trips", which also names it
  relevant, scores 0.32 and does not. At today's 0.67 it fires for none. Of the other 21, golden questions fire them 5 times at
  0.59 under MiniLM and 4 at 0.72 under bge-small, each its subject asked another way, labelled intent ("guest wifi joining
  card" for the guests' note on that card; "sabbatical savings" for the note on moving savings), never a labelled negative
  (`It_fires_only_for_relevant_golden_queries`, through the store, as each golden viewer, with memory scopes on).
- **Intent questions do fire**: 10 of 71 at 0.59 and 2 at 0.67 under MiniLM ("what jobs has Sam done for the choir this year?"
  0.66). Counted as negatives they would push the threshold to 0.77 and 0.81, where one question in 90 fires, so they are
  reported apart, as upstream's are. The answer tells a client to apply a note only if it fits, which is what absorbs them.
- **The situation clause alone does worse.** Scoring a question against the words up to the first comma ("When booking a flight
  for Alex") and precision first gives MiniLM 0.73 and recall 0.100, bge-small 0.79 and recall 0.244: the clause is short, so
  its score is noisier and more negatives reach it. Measured once, not kept.

How far to trust them: every figure is in-sample, on labels one person wrote, questions written before any score was seen and
the 52 pairs among the rest read after (`also`). Precision on 95% Wilson intervals: 15 of 15 [0.80, 1.00] and 19 of 19 [0.83,
1.00]; recall 15 of 90 [0.10, 0.26] and 19 of 90 [0.14, 0.31]. The questions of one instruction are not independent, and by
instruction 7 of 22 fire [0.16, 0.53] and 8 of 22 [0.20, 0.57]. Above the reading floors (0.50, 0.65) every pair is labelled,
so no unread pair reaches a chosen value. Upstream's own test on its model and corpus, with intent also reported apart, chose
0.67 at held-out precision 0.93 and recall 0.73; its questions and instructions are not these, so the numbers do not transfer.

## Insight candidates (SB-P5-05)

Added by run 19 (`sb/p5-insights`, from `sb/main` `2eabc48`). What says an older and a newer memory are worth a
reasoning client's look together, measured before the nightly insight candidate pass adopted any of it (D-211 asked for
signals beyond similarity). The slice measured them on 26 labelled pairs; run 19's integration counted them again on the
58 the pass can see once the links slice's 104 new pairs joined `pairs.json`
([below](#on-58-pairs-at-run-19s-integration)), and `InsightSignalTests` (in `ThresholdTuningTests.cs`) recomputes those;
the counts that need no model run always, the rest from the pinned model files, skipped without them (D-041). No verdict
moved: the pass's rule is the same.

### The labelled pairs it can see

The pass pairs only two native memories of one scope, both eligible (current, not replaced, not deprecated, not held),
the newer written 30 or more days after the older. Of `pairs.json`'s 30 insight, related and unrelated pairs 30 or more
days apart, six are such pairs (three insight); the other 24 join a note or a document, or two scopes. So run 19 labelled
20 more over the corpus's own memories, in a file of their own so that SB-P2-13's numbers stand
(`Parity/insight-pairs.json`, [fixtures.md](fixtures.md#insight-pairs)): five insight, seven related, eight unrelated, with
hard cases among each. Together: **8 insight pairs and 18 related or unrelated ones.** Insight is positive; related and
unrelated negative (duplicates and contradictions have their own review).

### Each signal

Counts are (true positives, false positives, false negatives, true negatives); balanced is the mean of the recall on
the 8 and the recall on the 18, the rule SB-P2-13 used.

| Signal | Used | P | R | Balanced | Counts |
|---|---|---|---|---|---|
| similar, MiniLM, today's 0.80 | yes | – | 0.000 | 0.500 | 0, 0, 8, 18 |
| similar, MiniLM, tuned 0.45 | yes, with `tuned` | 0.200 | 0.125 | 0.451 | 1, 4, 7, 14 |
| similar, bge-small, today's 0.80 | (not live) | – | 0.000 | 0.500 | 0, 0, 8, 18 |
| similar, bge-small, tuned 0.52 | (not live) | 0.500 | 1.000 | 0.778 | 8, 8, 0, 10 |
| weighty: both of importance 3 or more | yes | 0.700 | 0.875 | **0.854** | 7, 3, 1, 15 |
| a shared tag | no | 0.118 | 0.250 | 0.208 | 2, 15, 6, 3 |
| the same project | no | 0.250 | 0.250 | 0.458 | 2, 6, 6, 12 |
| a shared rare word (4+ letters, in at most 3 of the scope's eligible memories) | no | 0.357 | 0.625 | 0.563 | 5, 9, 3, 9 |
| both 80 characters or more (upstream's floor) | no | 0.273 | 0.750 | 0.431 | 6, 16, 2, 2 |
| **the pass's rule: similar or weighty**, MiniLM, today's 0.80 | yes | 0.700 | 0.875 | **0.854** | 7, 3, 1, 15 |
| the rule, MiniLM, tuned 0.45 | with `tuned` | 0.571 | 1.000 | 0.833 | 8, 6, 0, 12 |
| the rule, bge-small, tuned 0.52 | (not live) | 0.500 | 1.000 | 0.778 | 8, 8, 0, 10 |

What they say:

- **Likeness alone finds no insight under MiniLM.** At today's 0.80 nothing reaches it; at the tuned 0.45 one insight
  pair does (Sam's gym and wrist), with four related pairs. bge-small separates better (every insight pair between 0.52
  and 0.59, most unrelated pairs below), but it is not the live model and failed the retrieval gate.
- **Topic signals find related pairs, not insights.** A shared tag or the same project is far more common among the
  related and unrelated pairs than among insight pairs, which join two topics (a car and the bills, a diet and a trip).
  A shared rare word, standing for a shared name, fires about as often on a negative as on a positive. None decides a
  candidate; a shared tag or project only orders the list (upstream's 1.25 for a pair sharing none).
- **Importance separates best on these labels**, and is the caller's own word (`remember`'s `importance`), so the pass
  adds it as a second route: a pair of two memories of importance 3 or more is a candidate whatever their likeness.
  The rule `similar or weighty` was chosen as the one with the best balance at the threshold an install reads today
  among those in the table; it misses Sam's gym (importance 2) and lets in three related pairs of important memories.
- **Upstream's 80-character floor** would leave out two of the eight insight pairs (the gym's 67 characters and Alex's
  new job's 74), so the pass does not take it.

### The pass over the corpus

What the whole pass keeps over the corpus's eligible memories of Alex, Sam and Family (75), at each cap of pairs a memory
keeps (`PerSeed`, 3), and how many of the labelled pairs are among them:

| Model, threshold | 1 a memory | 2 | **3 (the pass)** | 5 | 10 |
|---|---|---|---|---|---|
| MiniLM, today's 0.80 | 31: 1 of 8, 1 of 18 | 60: 3, 2 | **86: 6, 3** | 127: 6, 3 | 171: 7, 3 |
| MiniLM, tuned 0.45 | 37: 1, 3 | 69: 4, 5 | **97: 7, 6** | 140: 7, 6 | 187: 8, 6 |
| bge-small, today's 0.80 | 31: 3, 0 | 60: 3, 1 | **86: 6, 1** | 127: 6, 3 | 171: 7, 3 |
| bge-small, tuned 0.52 | 53: 2, 2 | 96: 3, 5 | **131: 7, 6** | 184: 7, 8 | 240: 7, 8 |

Three a memory is the smallest cap that keeps six of the eight with today's threshold; ten keeps one more for twice the
list. About one candidate a memory is a long list for a reasoning client, which reads it best first and rejects what
says nothing; the score's order among the pairs nobody labelled is not measured.

### On 58 pairs, at run 19's integration

Run 19's links slice labelled every pair of two memories of one scope that a save scores at 0.43 or more under MiniLM
(or 0.62 under bge-small) in `pairs.json` in the same run; 37 of them are pairs the insight pass can see (all related or
unrelated), and five of those were in `insight-pairs.json` with the same labels, so the integration took those five out of
it (15 pairs left: 5 insight, 2 related, 8 unrelated). Together: **8 insight pairs and 50 related or unrelated ones.**
MiniLM's tuned insight threshold moved from 0.45 to 0.52 and bge-small's from 0.52 to 0.33 on the 193 pairs (D-216).

| Signal | Used | P | R | Balanced | Counts |
|---|---|---|---|---|---|
| similar, MiniLM, today's 0.80 | yes | – | 0.000 | 0.500 | 0, 0, 8, 50 |
| similar, MiniLM, tuned 0.52 | yes, with `tuned` | 0.167 | 0.125 | 0.513 | 1, 5, 7, 45 |
| similar, bge-small, today's 0.80 | (not live) | – | 0.000 | 0.500 | 0, 0, 8, 50 |
| similar, bge-small, tuned 0.33 | (not live) | 0.138 | 1.000 | 0.500 | 8, 50, 0, 0 |
| weighty: both of importance 3 or more | yes | 0.368 | 0.875 | **0.818** | 7, 12, 1, 38 |
| a shared tag | no | 0.059 | 0.250 | 0.305 | 2, 32, 6, 18 |
| the same project | no | 0.133 | 0.250 | 0.495 | 2, 13, 6, 37 |
| a shared rare word | no | 0.179 | 0.625 | 0.583 | 5, 23, 3, 27 |
| both 80 characters or more (upstream's floor) | no | 0.125 | 0.750 | 0.455 | 6, 42, 2, 8 |
| **the pass's rule: similar or weighty**, MiniLM, today's 0.80 | yes | 0.368 | 0.875 | **0.818** | 7, 12, 1, 38 |
| the rule, MiniLM, tuned 0.52 | with `tuned` | 0.320 | 1.000 | 0.830 | 8, 17, 0, 33 |
| the rule, bge-small, tuned 0.33 | (not live) | 0.138 | 1.000 | 0.500 | 8, 50, 0, 0 |

| Model, threshold | 1 a memory | 2 | **3 (the pass)** | 5 | 10 |
|---|---|---|---|---|---|
| MiniLM, today's 0.80 | 31: 1 of 8, 5 of 50 | 60: 3, 7 | **86: 6, 9** | 127: 6, 10 | 171: 7, 10 |
| MiniLM, tuned 0.52 | 34: 1, 8 | 64: 4, 11 | **91: 7, 13** | 132: 7, 14 | 176: 8, 14 |
| bge-small, today's 0.80 | 31: 3, 4 | 60: 3, 6 | **86: 6, 6** | 127: 6, 9 | 171: 7, 10 |
| bge-small, tuned 0.33 | 72: 2, 10 | 141: 3, 16 | **206: 6, 20** | 326: 6, 27 | 541: 7, 43 |

What changed: the new negatives are mostly pairs of important memories in one project (a job and its commute, a plot
and its rent), so weighty's precision falls from 0.700 to 0.368 [0.19, 0.59] while its recall stays 0.875 [0.53, 0.98];
the rule is still the best balanced at today's threshold, and with MiniLM's tuned 0.52 finds all 8 at precision 0.320
[0.17, 0.52]. Likeness alone still finds next to nothing under MiniLM, and bge-small's tuned value fires on every pair:
no threshold of it beats chance on the new negatives. The pass's cap of three a memory and its rule stand; the list a
reasoning client reads is longer in noise than the slice's numbers said, which SB-P5-06's daily template must expect.

### How far to trust the numbers

- **In-sample and few.** 8 positives and 18 negatives (50 since the integration); the rule was chosen on the same pairs it is scored on. 95%
  Wilson intervals: weighty's precision 0.700 [0.40, 0.89] and recall 0.875 [0.53, 0.98]; the rule with MiniLM tuned,
  precision 0.571 [0.33, 0.79] and recall 1.000 [0.68, 1.00].
- **Labelled in the run that chose the rule.** `insight-pairs.json`'s 20 pairs were written in run 19, after the signals
  were in view, by one labeller; its unrelated pairs lean to memories of low importance, which flatters the weighty
  route. A later item should label more insight pairs between memories of low importance, and unrelated pairs between
  important ones, before trusting importance this much.
- **Importance is optional.** A memory saved without one is never weighty, so on an install whose clients never give
  importance the pass finds next to nothing with today's threshold.
