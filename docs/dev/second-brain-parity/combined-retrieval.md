# Combined retrieval: one ranked list from words and meaning

The combined retrieval core of SB-P2-08 (`engine/src/Core/Recall.cs`): what it does, the numbers it runs with and the
measurements that chose them, and what it leaves open. Synthetic data only, in disposable databases, with the pinned
MiniLM model on, on 1 and 2 November 2026 (`sb/p2-retrieval2`, from `sb/main` `9c058a2`). Compare with
[lexical-search.md](lexical-search.md) (the keyword arm) and [baseline.md](baseline.md) section 6 (the gate).

## Where it stands

No tool calls it yet. `search_knowledge`, `search_resources` and `semantic_search` send the same queries and answer
exactly as before, with any settings: the gate's locks show their answers unchanged. The `recall` tool (SB-P2-09) and
the app's search (SB-P2-10) are the items that put it in front of a reader, each with its own switch; this page is the
evidence they decide with. Until then the parity evaluation is its only caller.

## What it does

`Recall.Search(reader, query)` reads two arms for one reader, with the read predicate (`Database.Visible`) in every
query:

- **Keyword**: ranked full text over the catalog, exactly as `search_knowledge` ranks it when ranking is on
  (`LexicalSearch.Standard`: identifier hits, rarity-weighted words, cover density, newest, the source's id), the first
  50 rows, a live source's row only while its source still holds that revision (`Retrieval.Validate`). Since SB-P2-19
  the same ranking also reads two texts the catalog's rows do not hold (`LexicalTexts`, migration 040's indexes): a
  native memory's own text in `memories`, and the passages of a record whose passages `semantic_search` would serve
  (a note's star, or a Drive file's when `drive` is listed), [below](#keyword-search-over-passages-and-memory-text).
- **Meaning**: the 50 passages nearest the question, as `semantic_search` serves them (`SemanticSearch.Nearest`: the
  kinds `adapters.embeddings.sources` lists, a live source's record only while its source holds the passages'
  revision), each resource at the rank of its nearest passage.

Then:

1. **Fusion** by reciprocal rank: every resource an arm found gets `weight / (60 + rank)` from it. The meaning arm's
   weight is 1; the keyword arm's is the summed rarity of the query's words the row holds, `log(1 + N / (df + 1))` per
   word with N at least 400 (`lexical_weight`), so an exact id or a rare name outweighs a common word. A resource both
   arms found gathers from both.
2. **Imputation**: a keyword result the meaning arm could never have found, a row with no passages of the model (a data
   series, a reference-only record), gets the meaning arm's share at its keyword rank, `1 / (60 + keyword rank)`.
   Without it such a row loses to any row both arms found, however good its words.
3. **Filters**: domain, provider, kind, subject, and a window on the source's own time (its last change, else when
   Rhythm indexed it), inside both arms' queries and the hops', so a filter never empties a page an unfiltered read
   filled. A native memory held for review is never a candidate (D-039, D-050). A native memory is found by meaning,
   through its passages, and by its words, through its text in `memories` (SB-P2-19); never by its own `resources`
   row's title, which carries none of its words (migration 033) and would match only the word "memory" (D-152). The hops would reach it through a link to its own
   row, but no link an install writes reaches one today (step 5).
4. **Diversity**: each resource is one result, at its best, so one long text never fills the page. Since SB-P2-20 each
   record is one result too: a `save_document` copy and the record it copies (the one whose source id the copy names,
   as the copy rule finds it) come back once, at the better place either took, as that row with its own reasons and
   excerpt, and any other copy of a record already listed is left out; records that are not copies never fold, and a
   copy of a record not among the results stays (`Recall.OneRecord`; a hop neighbour that is a listed record takes no
   place). The gate: every answer unchanged in both load modes, since the corpus holds no copy beside a star the arms
   serve (`RecallTests`, `RecallCopyTests` show it). Maximal marginal relevance over the meaning arm's passage vectors
   is built (`Lambda` below 1) but off, as measured below.
5. **Hops** (asked for, 0 to 3; the default is 0, D-040): typed links from the first five results, both directions,
   active, both ends readable by the reader and meeting the filters, the neighbour checked as `get_related_resources`
   checks it (a native memory's own row taken as Rhythm's own record, with its text as the excerpt), each hop scoring
   0.6 times its parent. The best neighbour not in the first ten takes the 5th place and the next the 10th, as
   upstream places them. Only a link to a memory's own row reaches a memory, and today only tests write those: the map
   links its snapshot rows alone, a memory's star row (SB-P3-11) is one the hops drop as an unknown live source, and
   person- and AI-made links (`made_links`, SB-P4-02) are not read. Those two items make links reach memories (D-152).
   Since SB-P4-02 (run 15, migration 052) the hops read `made_links` beside `relationships`: a link the `link` tool
   writes joins two memories' own rows, so it reaches its memory under the predicate and the hold, and a suggestion
   between two memories someone unlinked is no link to follow. With no made link the hops read what they read before.
6. **Prefix**: the list is built to 20 whatever `topK` asks for, and cut last, so asking for more only extends it.
7. **Budget**: excerpts of at most 12 000 characters in all. The first two results scoring at least 0.75 of the first's
   get up to 4000 characters each, the rest 400; an excerpt is the nearest passage when the meaning arm found the
   resource, else the start of its text, cut at a space, and says when it was cut and how long the whole text is.
8. **Fallback**: without the local model, or when the meaning arm fails, the keyword results, and a notice that semantic
   search was skipped. There is no unfiltered fallback. Live files whose check did not answer in time are counted in a
   notice, as `semantic_search` counts them.

Each result carries the catalog row as `search_knowledge` returns its columns, its excerpt, the fused score and why it
is there: its keyword rank, weight, the query's words it holds and identifier hits; its meaning rank, similarity and
nearest passage; and for a graph neighbour the hop, the link's type, the result it was reached from and the direction.
No reply holds a vector.

**Scope.** Every statistic a ranking uses is the reader's own (memory.md 6.2): the keyword weights are computed among
the candidates the reader's predicate admits, each arm's ranks among its readable rows, and diversity among the
reader's own results. `RecallScopeTests` shows it: with scopes on, Sam is served none of Alex's private records through
either arm or through a link, and his keyword weights, ranks and scores do not move when Alex adds 30 private rows
holding the same word (Alex's own weights do). `MemoryScopeTests` asks it, three hops deep, with every golden query that
names a private record, as each person and the household view, across links that cross scopes.

Adapted from Second Brain's recall (MIT; `src/recall/rrf.ts`, `search.ts`, `math.ts`, `snippet.ts`,
`neighborhood.ts`, `src/graph/traverse.ts` at `228cea49`): reciprocal rank fusion with k 60 and keyword weights from
rarity, maximal marginal relevance in blocks of five, graph places 5 and 10 with decay 0.6, the output budget and the
notice when vectors fail. No code is copied; `NOTICE` credits it (D-005). Imputation is Rhythm's own: upstream ranks
memories, each with its vectors, and has no rows the meaning arm cannot see.

## The numbers it runs with, and why

Every number starts as upstream's (D-028, D-040) and changes only where the gate judged the change. The combined path's
starting point is upstream's numbers as they stand (`RHYTHM_PARITY_RECALL=upstream`: diversity 0.7, no imputation); each
departure was judged against the run it departs from, in both load modes, by the gate's own rules
(`RHYTHM_PARITY_GATE_COMPARE`). A departure was taken when no type lost in either mode and one mode showed the gain the
gate asks for, overall or in a declared target type. The privacy rule is INCONCLUSIVE in every scopes-off run, for every
path and baseline alike: the household model reveals what a viewer scope would hide (D-055); the scopes-on run below is
where the reveals are measured.

| Change | Against | Copies | As installed | Taken |
|---|---|---|---|---|
| diversity off (λ 0.7 to 1) | upstream's numbers | no type lost; overall recall at 5 +0.041 (paraphrase +0.136, as-of +0.134) | no type lost, no gain | yes |
| imputation on | diversity off, no imputation | no type lost; health MRR +0.092, interval above 0 (target type) | the same answers | yes |
| keyword weight flat (1) | the defaults | commitment recall −0.133, MRR −0.153; visibility MRR −0.036 | no type lost, no gain | no |
| keyword weight as a share of the query's rarity | the defaults | multilingual recall −0.048, commitment MRR −0.033, health MRR −0.010 | graph-chain MRR −0.025 | no |
| one hop (from the first five) | the defaults | no type changed, no gain | no type changed, no gain | no: hops stay the caller's choice, default 0 |
| fusion constant 20; 15 passages for the meaning arm | upstream's numbers | measured once, not judged: within 0.013 of recall at 5 | the same | no |

Diversity loses here because a question's several relevant answers are often about one subject, and marginal relevance
pushes the later ones down (paraphrase recall at 5 0.640 without it, 0.504 with it). Upstream's results are passages of
memories, where several passages of one memory compete; here each resource is one result already. It stays built for a
later measurement (native memories' passages, phase 3).

Measuring eleven variants on one golden set risks choosing for that set: the defaults are the two departures above, both
judged on rules fixed before any run, and the variants' numbers are kept here so a later set can judge them again.

## Against each arm alone

The gate run judges combined retrieval against each arm by the same rules, every time it runs (the report's
"Comparisons within this run"). On the locked run (recall at 5 / MRR, over the queries each comparison judges):

| Load mode | Against | Verdict | Overall | Types that lose |
|---|---|---|---|---|
| copies | fulltext | INCONCLUSIVE (privacy only) | 0.814 / 0.735 to 0.844 / 0.754; gain shown | none by the gate's rule: health and current MRR −0.004 and −0.001, inside its 0.01 |
| installed | fulltext | INCONCLUSIVE (privacy only) | 0.131 / 0.123 to 0.164 / 0.175; gain shown | none |
| copies | semantic | FAIL | 0.796 / 0.713 to 0.842 / 0.756 | paraphrase, as-of, current, graph-chain recall, stale MRR |
| installed | semantic | FAIL | 0.112 / 0.132 to 0.160 / 0.171 | paraphrase, long-text |

So against ranked `search_knowledge` no type regresses by the gate's rule in either mode: with copies health and
current MRR fall 0.004 and 0.001 below full text (0.778 to 0.774, 0.424 to 0.422), inside D-056's 0.01, and every
other type gains or holds; as installed no type falls. That full text is ranked (`engine.legacySearch: false`, a new
install's). An install whose `rhythm.yaml` predates ranking keeps the legacy query (D-053), and against it, judged in
one gate run of the fix review (`RHYTHM_PARITY_LEXICAL=legacy`, `RHYTHM_PARITY_GATE_COMPARE=combined=fulltext:legacy`,
a variant with no lock), combined FAILS with copies: rare-name MRR 1.000 to 0.976 (−0.024, [−0.079, +0.000];
`q-rare-milers` second behind another person's private record), though recall at 5 goes from 0.304 to 0.842; as
installed it is INCONCLUSIVE for the household model's reveals only, recall at 5 0.026 to 0.159, no type lost. So
this is the evidence D-053 and D-056 ask for before SB-P2-09's compatibility switch sends `search_knowledge` to the
combined list on a ranked install, and not on a legacy one (D-151). It does not replace `semantic_search`: a
question in other words ("what did the cat's blood results show?") still finds its answer better by meaning alone,
because the keyword arm's partial matches on common words outrank the nearest passage. That is RET-08's "or the arms
stay separate" for meaning questions.

| Path (copies) | Recall at 5 / MRR | p95 ms | Path (installed) | Recall at 5 / MRR | p95 ms |
|---|---|---|---|---|---|
| fulltext | 0.814 / 0.735 | 27.5 | fulltext | 0.115 / 0.108 | 3.0 |
| semantic | 0.786 / 0.704 | 4.0 | semantic | 0.098 / 0.115 | 3.7 |
| graph | 0.242 / 0.215 | 14.0 | graph | 0.231 / 0.212 | 12.3 |
| combined | 0.844 / 0.754 | 33.4 | combined | 0.148 / 0.158 | 8.5 |

(Over every query, gaps included; latency from the gate run, well inside D-029's 500 ms.)

By type, with copies: exact-id 1.000 / 1.000 and rare-name 1.000 / 0.976 as full text; paraphrase 0.640 / 0.541 (full
text 0.571 / 0.488, semantic 0.715 / 0.753); as-of 0.867 / 0.475; multilingual 0.714 / 0.680; long-text, stale and
commitment as full text; health 0.875 / 0.774. As installed every type is at least full text's; injection (0.500 / 0.500
against 0.250 / 0.250), graph-chain (0.300 / 0.335 against 0.175 / 0.175) and long-text (0.212 / 0.217 against 0.154 /
0.135) gain most.

**Exact ids as installed** (carried from SB-P2-07; as it stood before SB-P2-19, which the next section updates): not met
in full. As installed with the example's sources, 1 of the
22 exact-id queries finds its answer (the heat pump checklist note, first). Fourteen of the 22 are answered only by
Second Brain memories, which Rhythm reads by reference and holds no text for (D-047: Rhythm's own memories, with their
text, are phase 3); the rest by Drive files, whose text Rhythm reads only when `drive` is listed (opt-in, D-085), and
the note. With `drive` listed (`RHYTHM_PARITY_SOURCES=documents,notes,memories,drive`), 7 of the 8 queries whose
answer's text Rhythm holds find it first, through the meaning arm; the eighth ("KR-5521") is 13th, because the keyword
arm does not read passages.

## Keyword search over passages and memory text

SB-P2-19 (D-152, D-153), measured on 2 November 2026 (`sb/p2-keyword`, from `sb/main` `f4590a9`), the model on.
Before it the keyword arm read only the catalog's rows, so a record whose words only its passages hold (a note's or a
Drive file's star row, whose content is empty as installed) and a native memory (whose row holds none of its words) were
found by meaning alone, and an exact id in them was missed when the meaning arm missed it.

**What it reads.** One ranking over three kinds of text (`LexicalSearch.Sql` with `LexicalTexts`), each found through
an index (migration 040: `resource_chunks_search_english`, `memories_search_english`, beside 021's on the catalog):
- the catalog's rows by their title and content, as before, never a memory-provider row (a memory's own row is titled
  Memory, its star answers only the map);
- a native memory by its own text in `memories`, never a held one (the memory tier and the scoring both ask for no
  hold), nor one deprecated or past `valid_until` (`Recall.Current`), and none at all with `Memories` off
  (`search_knowledge`'s combined mode, D-165);
- a record whose passages `semantic_search` would serve (a star of a source `adapters.embeddings.sources` lists, seen
  within `graph.freshnessSeconds`, its record's newest observation with scopes on) by its passages, of any model (the
  chunks are the same text, D-069), so the model off still finds them.

Each candidate is scored on one text of its own, its best: a memory on its text; a record on the passage, with its
title, that holds the whole question or an identifier the question names, else on its title alone; every other row on
its title and content, as before. Word rarity counts the records holding a word in any of their texts, so the weights
are the ones before whenever no memory and no passage is among the candidates
(`With_no_memory_and_no_passages_the_keyword_arm_ranks_as_search_knowledge`).

**What it serves.** A native memory's own row as Rhythm's own record (the predicate and the hold decide it). A record
scored on a passage of a live source (Drive) only while the source still holds that revision, asked by the record's
own source id at most eight at a time within 5 s, as `semantic_search` asks, one not answered in time left out and
counted in the notice (`SemanticSearch.Confirm`), once, and only when no arm got its source's answer: a file one arm
could not check and the other confirmed is served and not also counted, and one the source then refused is left out as
not current (`A_live_file_is_counted_as_left_out_only_when_no_arm_got_its_sources_answer`); within one search a revision confirmed for one arm is not asked again
(`ConfirmedChecks`), and the 5 s are the whole search's, both arms together (D-085): the meaning arm waits only for
what the keyword arm's checks left, and serves without asking again a record they confirmed at the revision its
passages are of, so a slow source holds a search up once, not once an arm
(`A_slow_source_holds_a_combined_search_up_within_one_budget`). Every other row as `search_knowledge` serves it (`Retrieval.Validate`), so a Drive star found by
its title alone is still refused for its namespaced id, as before. The excerpt of a record found by a passage is that
passage; of one found by its title, the start of its passages' text.

**Why only the whole question or an identifier.** A passage holding only some of the words is most of what the arm
reads as installed, and its partial matches on common words outweigh the meaning arm's answers. Measured as a variant
(`RHYTHM_PARITY_RECALL=passages-any`) against the arm before SB-P2-19 (`catalog-words`, the catalog's rows alone) in
the same gate run: as installed FAIL, paraphrase MRR −0.021, graph-chain recall −0.050, health MRR −0.042; with
`drive` listed FAIL, graph-chain recall −0.050 and MRR −0.019, multilingual recall −0.048, health MRR −0.021. So a
passage counts only for the whole question or an identifier it names, which only the words can find. A first version
also served by its source id a Drive star found by its title alone; with `drive` listed that put the bins letter
first for questions sharing a word with its title ("why did Pepper's food change?") and cost graph-chain MRR 0.050 in
the evaluation, so such a star is served as `search_knowledge` serves it, as before.

**The gate.**

| Load mode | Sources | combined against its lock | combined against `catalog-words`, same run |
|---|---|---|---|
| copies | the example's | every answer unchanged | no type changed (FAIL for want of a gain only) |
| installed | the example's | 4 answers moved, no metric changed, no reveal added; re-locked | no type changed (FAIL for want of a gain only) |
| installed | with `drive` (no lock) | | INCONCLUSIVE: no type loses; exact-id MRR +0.042, rare-name MRR +0.040, visibility MRR +0.038, long-text MRR +0.019, overall MRR +0.013, under the power to show a gain |

As installed the moved answers are `q-current-library`, `q-vis-sabbatical`, `q-vis-plot-alex` and `q-commit-bake`:
notes whose passages hold the whole question rise among records none of which is a relevant answer, and the forbidden
notes the household model already revealed stay revealed, no more. The installed lock was taken again for it, its reason
in the lock's history; the copies lock is unchanged.

**Exact ids as installed, now.** With the example's sources nothing moves: the exact-id answers are Second Brain
memories (reference-only, no text in Rhythm), Drive files (not listed) and the heat pump note, found first before. With
`drive` listed (`RHYTHM_PARITY_SOURCES=documents,notes,memories,drive`), all 8 exact-id queries whose answer's text
Rhythm holds find it first: "KR-5521" moved from 13th to 1st. Four answers moved there, all up and none down: that
one, the rare name "Thornfield Joinery" (6th to 1st), `q-vis-quote-family` and `q-long-office` (2nd to 1st). Over every
query combined recall at 5 / MRR went from 0.262 / 0.275 to 0.266 / 0.287 (`f4590a9` against this change, the
evaluation's own numbers, gaps included).
The 14 exact ids in Second Brain memories wait for those memories to be Rhythm's own (phase 3, the importer); a native
memory is found by an id in its text (`KeywordTextTests`).

**Scopes on** (`RHYTHM_PARITY_SCOPES=on`, both modes): the run passes, so each of the 23 visibility checks reveals nothing
on the combined path, and over all 272 queries it returns or opens nothing its viewer may not see; recall at 5 / MRR
0.871 / 0.769 with copies, 0.148 / 0.160 as installed, as before. `KeywordTextScopeTests` shows a person's private
memory and note found by their words by that person alone, and Sam's weights, ranks and scores unmoved by 20 of Alex's
private memories holding the same word.

## Time phrases (SB-P2-16)

Built and measured, not kept: `RecallOptions.TimePhrases` is off and no tool or setting turns it on. With it, a time
phrase in a question and no window asked for (`after`, `before`) becomes the window on the source's own time, read in
the install's time zone (`timezone`), and leaves the question (`Core/TimePhrases.cs`, after Second Brain's
`src/text/temporal.ts`): "last 3 days", "last 2 weeks", "last week", "this week" (from Monday), "last month", "this
month", "yesterday", "today", "around May 5th" (three days either side), and a month and day ("on Aug 8", "March 3rd,
2025") only when it stands alone, never after "as of" or by, until, before, after, since or from, and not when two such
dates stand alone, so "the Aug 8 Cafe" is no window. Upstream also reads regional zone abbreviations after a date;
Rhythm reads only UTC, GMT and the word time ("Aug 8 New York time"). `TimePhraseTests` checks each phrase at fixed
clocks in Europe/London and UTC (London's yesterday at 00:30 summer time is the day before UTC's turns), and
`TimePhraseDatabaseTests` that off the phrase is a word like any other, on it narrows, and a window the caller gives
wins.

The gate (`RHYTHM_PARITY_RECALL=time`, the corpus's own clock and zone, `RHYTHM_PARITY_GATE_COMPARE=combined:time=combined`,
health declared as the target type): FAIL in both load modes for want of a gain, every metric unchanged. Only two golden
questions hold a phrase it reads, both "this week" (`q-health-sleep-sam`, `q-vis-sleep-sam`), and the sleep series
was already their first answer; the golden set has no question whose answer only a window finds. So nothing is kept on;
the evidence it needs is golden time-phrase questions (a fixture change, with its own lock). Query profiles (RET-02:
causal, chronology, current or direct intent, and deterministic variants) are not built, for the same want of
questions to judge them by.

## Checks

| Check | Where | What it shows |
|---|---|---|
| fusion, weights, ties, imputation, diversity, budget, bounds, prefix (200 random cases) | `RecallTests` | each step on its own |
| a note's passages by an id they hold, with the model off or on; a live file's only while its source holds that revision, asked by its source id, once a search, and never by its title alone; a native memory by its words, never by its title and never held, deprecated, ended or through `search_knowledge`'s mode; the arm as `search_knowledge` ranks with neither; with scopes on, each reader's own and weights unmoved | `KeywordTextTests`, `KeywordTextScopeTests` | SB-P2-19 |
| words and meaning fused, an exact id first; the model off or failing; filters in both arms and on hop neighbours; a native memory by meaning and by a link written by hand, a held one never (each fails without its filter); typed links only with hops (story 4); prefix on a database | `RecallDatabaseTests` | the core against Postgres, with a stand-in model |
| scopes on: own scopes through either arm and any link; keyword weights the reader's own; a person's private native memory, by meaning and through a link from a Family note, served with its text to its owner alone, never to the other person or the household view at 0 to 3 hops | `RecallScopeTests`, `MemoryScopeTests` (whose corpus holds no native memory) | memory.md 6.2 and section 7 |
| top 5 and top 10 the start of top 20, each row once, on all 272 golden queries | `ParityEvaluation` (opt-in) | RET-13, RET-15 on the golden set |
| the gate against the locks, and combined against each arm | `ParityGateRun` (opt-in) | the tables above |
| the scopes-on evaluation, model on | `RHYTHM_PARITY_SCOPES=on` (opt-in) | in both modes the 23 visibility checks reveal nothing on the combined path, and over all 272 queries it returns or opens nothing its viewer may not see; what it still reveals with copies (27 queries) is through quarantine and validity, which no path has, as full text does; recall at 5 / MRR 0.871 / 0.769 with copies, 0.148 / 0.160 as installed |

## How to run it

```sh
# The evaluation with combined retrieval and, beside it, named variants (upstream, diversity, noimpute, flat,
# coverage, coverage-exact, k20, pool15, hops1, hops2, catalog-words, passages-any, time):
RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files> RHYTHM_PARITY_RECALL=upstream,hops1 \
  RHYTHM_PARITY_OUT="$PWD/parity-out" sh engine/test-db.sh --filter ParityEvaluation
# The gate, judging pairs of this run's paths as well (candidate=baseline):
RHYTHM_PARITY_EVAL=1 RHYTHM_MODEL_DIR=<...> RHYTHM_PARITY_RECALL=upstream \
  RHYTHM_PARITY_GATE_COMPARE="combined=combined:upstream" sh engine/test-db.sh --filter ParityGateRun
```

A variant is a path of its own with no lock, so a gate run that adds one fails its tripwire for that path; read the
report, and run without variants to check the locks.

## Left open

- **No tool yet.** SB-P2-09 (`recall`, with `explain`, a receipt and the compatibility switch) and SB-P2-10 (the app's
  search) serve it.
- **The legacy query.** No lock holds the legacy keyword query, so no gate run checks combined against it except by a
  variant path, as above. A legacy install's switch waits for such a lock and a passing run (D-151).
- **Native memories by link.** No link an install writes reaches a memory's own row yet (step 5): SB-P3-11 and
  SB-P4-02 (D-152). Since run 15 a made link does (SB-P4-02, behind `memory.tools`); the corpus holds none, so the
  gate's numbers are unchanged.
- **The lock's reason** for the combined path (both gate-baseline files) says it loses only paraphrase, as-of and
  current against semantic search and that no type loses against full text; the gate's numbers say graph-chain recall
  and stale MRR too, with copies, and long-text as installed, and health and current MRR a little below full text
  (D-151). The next re-lock writes the reason to match; nothing was re-locked for this, since no answer changed.
- **Passages for part of a question.** A passage counts for the keyword arm only when it holds the whole question or an
  identifier it names; counting any word lost paraphrase, graph-chain and health as installed (above). A larger or real
  set may judge it again (`passages-any`).
- **A Drive star by its title.** Found by its title alone, a Drive file's star is still refused by `search_knowledge`'s
  check (its namespaced id), as before SB-P2-19; serving it by its source id is a separate change for the gate.
- **Copies of a record not found.** Two copies of one record whose original is not among the results (a copy saved
  twice, or by two readers) both stay: nothing in the results says they are one record. A later item may fold them.
- **Ranking multipliers** (recency, importance, recall counts; RET-09) and query profiles (RET-02) are not built; time
  phrases are built and off (SB-P2-16, above), waiting for golden time-phrase questions.
- **Diversity** is off by measurement; native memories' passages (phase 3) are its next measurement.
- **The numbers are the synthetic set's.** A larger or real set may move them; every change goes through the gate.
