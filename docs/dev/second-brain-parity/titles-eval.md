# Titles model evaluation

SB-P2-14: the optional local titles model ([../../adapters/titles.md](../../adapters/titles.md)) measured on its own,
against the rule the program's decision log D-027 fixed before the first run. Synthetic data only, on a development
machine, in a disposable compose project; nothing touched an install, a server or real data, and the titles service and
its defaults are unchanged. The method below was committed before any model ran (`1c66a0e`); the results follow it.
**Verdict: keep the current setting, no titles model** ([Results](#results)). **Re-test, 9 November 2026**: under
D-058's successor rule (D-250, proposed), qwen3 4B given the whole text passes on the benchmark machine, on the guard's line and
over the 3 GB memory budget; the adapter stays off and nothing is adopted ([below](#re-test-under-the-successor-rule-d-250)).
**Since then** (D-255, the owner's choice, `sb/p2-wholetext`, not merged): the core gives the model a star's whole text,
the input measured here as `text`, byte for byte; "What the service does today" below describes the wiring before it.

## What the service does today

- **The adapter** (`engine/src/Adapters/Titles/TitlesAdapter.cs`) sends one `POST /api/generate` to an Ollama server on
  the stack's internal network: the system prompt "You title personal notes. Reply with only a title for the note: at
  most 8 words, in the note's own language, with no quotes, labels or final period.", the prompt
  `Note:\n<text>\n\nTitle:` (text cut at 6,000 characters), temperature 0, at most 32 tokens, a 30 s timeout. It keeps
  the first line of the answer without labels, quotes or markup, at most 8 words and 80 characters, and drops an
  answer with a link or markup (a reasoning block that opens with `<think>` among them). There is no default model;
  the docs suggest `qwen3:4b-instruct-2507-q4_K_M`.
- **Which stars it titles** (`engine/src/Core/RelationshipHost.cs`, `Retitle`): only current stars whose title equals
  their overview. Today that is the Second Brain adapter's memory stars, whose title and overview are both the same
  excerpt (`SecondBrainAdapter.Title`: the first line's first sentence, links and markup removed, at most 80
  characters). Notes keep their file name or front-matter title; demo, Drive and catalog stars have titles of their own.
  So the model is given the excerpt, never the memory's text: the "at most 6,000 characters" in the adapter docs is the
  adapter's own cap, not what the core sends.
- **Its budget**: at most 60 titles and 60 s per map rebuild (every 900 s by default). A title is kept while the model
  and text are unchanged. A server error or timeout ends titling for the rest of that rebuild; an unusable answer leaves
  the excerpt and stores no revision, so the same star is asked again at the next rebuild.

## The rule

D-027, as applied here. A **candidate** is a model given one input. Each title scores one point for each of:

| Criterion | Point when | How it is checked |
|---|---|---|
| accurate | it says nothing the text does not | every word that makes a claim is in the text, or restates it (below) |
| specific | it names the text's subject, not a generic label | it contains one of the item's subject terms |
| at most 80 characters | `title.Length <= 80` | as the adapter counts |
| private | no private detail beyond what the first line shows | none of the item's private details appears |

A candidate is adopted only if all of these hold, otherwise the current setting (no titles model: the adapter's excerpt)
stays:

| Check | Budget |
|---|---|
| mean points | at least 3.0 of 4 |
| margin | at least 0.5 above the excerpt's mean on the same set |
| latency | p95 at most 5 s per title |
| memory | at most 3 GB (3,000,000,000 bytes) of extra working memory |
| reliability | at most 5% failed or empty titles |

## The set

`engine/tests/Knowledge.Tests/Parity/titles-set.json`, labelled DEMO: 40 fictional notes and memories written for this
evaluation, by `alex` and `sam` in their personal scopes or the shared Family scope. Each has a reference title, the
subject terms a specific title must name, the private details its text holds beyond the first line (26 items have
some: a key-safe code, a salary, a third party's name, where a key is hidden), and a few words that fairly restate it
(`accept`, for example "appointment" for a booked visit). Five are in French, German or Spanish, since the prompt asks
for the note's own language.

Each item has one of three first-line shapes, a mix fixed before any run to give each shape enough items to report on,
not to match a real corpus:

| Lane | Items | First line | The excerpt scores |
|---|---|---|---|
| subject | 18 | names what the text is about (a heading, or a statement as an AI client saves one) | 4.0 |
| preamble | 12 | a lead-in that names nothing ("Note to self", "Monday 3 November", "TODO", "Okay so") | 3.0 |
| fragment | 10 | a list item, a figure or a link ("milk", "10k in 52:30", a listing URL) | 3.2 |

The excerpt is accurate, short and private by construction, so it loses points only on "specific": it scores **3.5**
over the set. The margin then asks a candidate for a mean of 4.0, every title passing every criterion. That is what
D-027 says on this mix, and the phase 0 corpus's memories (every first line names its subject) would leave even less
room; it is recorded, not changed, and the lanes are reported apart so the rule's effect can be seen.

Ordinary tests (`TitlesSetTests`) keep the set honest: at least 30 items, fictional people and scopes, every lane with
at least 8 items, every reference title scoring 4 of 4, every private detail in the text but not on its first line or
in its excerpt, and a scorer that catches an invented word, a generic label, a leak and an overlong title.

## Scoring

Fixed in `TitlesRubric.cs` before the first run:

- **Words**: lower case, accents folded, "13th" read as 13, "Sam's" as sam, month and day abbreviations spelled out,
  one-letter words dropped. Two words are the same if equal after a light stemming (plural, -ed, -ing, a final e), or
  if one stem of at least five letters starts the other ("renew", "renewal"); numbers only when equal.
- **Accurate**: function words in the four languages and frame words that make no claim ("note", "reminder", "list",
  "idea") are skipped; every other word must be in the text or in the item's `accept` list. A word check cannot see
  paraphrase or a swapped person, so after the run a reviewer reads every title and records judgements in
  `titles-review.json`: a flagged word that only restates the text (accepted for that item, for every candidate alike),
  or one exact title the check got wrong, each with its reason. Both scores are reported; the verdict uses the reviewed
  one and says whether the automatic one agrees.
- **Specific**: one of the item's subject terms, every word of it, is in the title.
- **Private**: a private detail counts when each of its words is in the title, matched exactly (a plural aside), so a
  code or a name is caught however it is placed.
- **A failed or unusable title** is scored as what a person then sees: the excerpt, which stays on the star. Failures
  also count against the 5% budget, and a strict mean (a failure scores 0) is reported beside.
- Quality is scored on the first of three passes (temperature 0); latency and failures cover every pass.
- For information only, not part of the rule: a paired bootstrap interval of the margin (10,000 resamples of the 40
  items, seed 27), word overlap with the reference title (F1), and how often the three passes agree.

## Inputs

- **excerpt**: what the core gives the model today. Its verdict is the verdict for the service as it is.
- **text**: the whole text, as the adapter would send it if the core passed a memory's text (for example once native
  memory stars exist, SB-P3-11). Its verdict says whether that wiring is worth proposing; it needs a code change, so it
  is not a setting this item can adopt.

## Candidates

| Model | Why | Download | Licence (the model file's own metadata) |
|---|---|---|---|
| `qwen3:4b-instruct-2507-q4_K_M` | the model the adapter docs suggest | 2.32 GiB, 4.0B parameters, Q4_K_M | Apache-2.0 |
| `qwen2.5:1.5b-instruct` | smaller, from the same family | 935 MiB, 1.5B, Q4_K_M | Apache-2.0 |
| `phi4-mini` | another family at the same size | 2.31 GiB, 3.8B, Q4_K_M | MIT |

The method was fixed with "about 2.5 GB" and "about 1.0 GB"; the sizes and licences above are what the server
reported for the files it loaded.

## Speed, memory and reliability

- **Latency**: wall time of each `TitlesAdapter.Title` call from the host, through Docker's loopback port, so it
  includes the HTTP round trip. Each model starts on a fresh server; its first call (a warm-up text outside the set)
  loads the model and is reported apart as the cold start, which a rebuild pays whenever the 5-minute keep-alive has
  lapsed. p95 is the nearest-rank value over 40 items, 3 passes, per input (120 calls).
- **Memory**: the server container's cgroup `memory.peak` over the model's whole run (pull excluded; load and both
  inputs included). Without a titles model there is no such container, so all of it is extra. It counts the model file
  the server maps into memory, so it is the conservative figure; `docker stats` and the server's own `/api/ps` size are
  recorded beside. The container has the stack's 4 GB limit.
- **Reliability**: a call fails when the server errors, the 30 s timeout passes, or the cleaned answer is empty or
  dropped.

## How to rerun

```sh
RHYTHM_TITLES_EVAL_OUT="$PWD/titles-out" sh engine/titles-eval.sh \
  qwen3:4b-instruct-2507-q4_K_M qwen2.5:1.5b-instruct phi4-mini
```

The script (`engine/titles-eval.sh`, beside `engine/test-db.sh`) reads the Ollama image that
`deploy/compose/engine.compose.yaml` pins, runs it in the compose project `sb-titles-eval` (never the stack's own) with
the stack's server settings (`engine/tests/titles-eval.compose.yaml`) on `127.0.0.1:11534`, pulls each model into that
project's volume, runs the opt-in test `TitlesEvaluation` against a fresh server per model, writes
`titles-run-<model>.json`, `titles-memory-<model>.json`, the server's log `titles-server-<model>.log` (load lines and
request timings, no texts), `titles-eval.json` and `titles-eval.md`, and removes the project, its network and its
volume on exit. The image stays until `docker image rm`. The evaluation lives with the
engine's tests rather than under `scripts/` because it drives the engine's own adapter and the adapter's excerpt code,
so what it measures is exactly what the service runs.

## Results

Run on 31 October 2026 from `sb/p2-titles` (the evaluation code at `722aa14`, the review at `29e3787`), with the
pinned image `ollama/ollama:0.40.13` (Ollama 0.40.13, CPU only, the Zen 4 backend), in Docker Desktop 29.9.2 whose VM
had 32 CPUs and 16 GB; the server container had the stack's 4 GB limit. Each model: 40 items, 2 inputs, 3 passes (240
titles) plus the cold-start calls. The final run took 9 minutes of compute and all runs together about 23 (below), so
the whole set was run, not D-027's minimum. Every number is from the final run unless it says otherwise.

### Verdict

**Keep the current setting: no titles model.** No candidate meets D-027 on either input, so nothing changes: the
adapter, its prompt and its defaults stay as they are, and the adapter stays off by default.

| Candidate | Input | Mean of 4 | vs excerpt (95% interval) | p95 per title | Peak memory | Failed | Fails D-027 on |
|---|---|---|---|---|---|---|---|
| no model: the excerpt | - | 3.50 | - | - | - | - | current setting |
| `qwen3:4b-instruct-2507-q4_K_M` | excerpt | 3.28 | −0.23 (−0.35 to −0.10) | 0.72 s | 3.72 GB | 0/120 | margin, memory |
| `qwen3:4b-instruct-2507-q4_K_M` | text | 3.85 | +0.35 (+0.13 to +0.58) | 0.85 s | 3.72 GB | 0/120 | margin, memory |
| `qwen2.5:1.5b-instruct` | excerpt | 3.15 | −0.35 (−0.53 to −0.20) | 0.46 s | 1.53 GB | 0/120 | margin |
| `qwen2.5:1.5b-instruct` | text | 3.58 | +0.08 (−0.18 to +0.33) | 0.94 s | 1.53 GB | 0/120 | margin |
| `phi4-mini` | excerpt | 3.15 | −0.35 (−0.50 to −0.20) | 1.15 s | 3.73 GB | 0/120 | margin, memory |
| `phi4-mini` | text | 3.83 | +0.33 (+0.13 to +0.50) | 1.18 s | 3.73 GB | 0/120 | margin, memory |

Every candidate passes the mean (3.0) and the latency and failure budgets; none passes the margin (0.5); both
4-billion-parameter models also fail the memory budget. The automatic check alone gives the same verdicts with lower
means (2.77 to 3.50); the review only narrowed the gap. The interval is information, not part of the rule.

| Criterion, share of titles passing | Excerpt | qwen3 4B excerpt / text | qwen2.5 1.5B excerpt / text | phi4-mini excerpt / text |
|---|---|---|---|---|
| accurate | 1.00 | 0.80 / 0.93 | 0.70 / 0.90 | 0.65 / 0.93 |
| specific | 0.50 | 0.48 / 0.93 | 0.45 / 0.78 | 0.50 / 0.98 |
| at most 80 characters | 1.00 | 1.00 / 1.00 | 1.00 / 1.00 | 1.00 / 1.00 |
| private | 1.00 | 1.00 / 1.00 | 1.00 / 0.90 | 1.00 / 0.93 |

| Mean points by lane | subject (18) | preamble (12) | fragment (10) |
|---|---|---|---|
| the excerpt | 4.00 | 3.00 | 3.20 |
| qwen3 4B, excerpt / text | 3.89 / 3.83 | 2.67 / 4.00 | 2.90 / 3.70 |
| qwen2.5 1.5B, excerpt / text | 3.83 / 3.56 | 2.33 / 3.83 | 2.90 / 3.30 |
| phi4-mini, excerpt / text | 3.94 / 4.00 | 2.50 / 3.83 | 2.50 / 3.50 |

### What the numbers say

1. **As the service is wired, a model makes titles worse** (−0.23 to −0.35). Given only a lead-in, every model
   invents a subject: "Meeting with John Smith" for "Monday 3 November", "New Marketing Strategy" for "Idea", "Stay
   organized and focused" for "Note to self", "Milk Facts and Uses" for a shopping list's first item, "Personal
   Reflection on 48211" for a meter reading. On the 20 items whose excerpt already names the subject the models are
   close to it (3.85 to 3.90 against 4.00) and shorter, but they drift there too ("Tarte aux pommes traditionnelle").
2. **Given the whole text, the two 4B models write better titles than the excerpt**, by +0.35 (qwen3) and +0.33
   (phi4-mini), with intervals above zero. The gain is all on the 20 items whose excerpt names nothing: qwen3 3.95 and
   phi4-mini 3.75 against the excerpt's 3.00. Where the excerpt was already good they lose a little (3.75 and 3.90
   against 4.00), so the overall gain stays below 0.5. On this set only a perfect titler (4.0) could clear the margin.
3. **Privacy, with the whole text**: phi4-mini put a private detail in 3 titles (a policy number, a third party's
   name, "Alex's Birthday Shopping List" on the list that hides the surprise's candles) and qwen2.5 in 4 (the key-safe
   code, the party's venue, two third parties' names). qwen3 4B leaked none of the 26 items' details.
4. **Language**: asked for the note's own language, qwen3 kept it for all 5 non-English texts given the whole text,
   phi4-mini for 3 and qwen2.5 for 1; given only the excerpt they mostly answered in English (1, 0 and 0 of 5). The
   rubric does not score language; a translated title that says nothing new counts as accurate.
5. **Speed is not the problem**: warm p95 is 0.46 to 1.18 s per title, so at each model's mean a rebuild's 60 s budget
   fits all 60 titles it may ask for.
6. **Memory is, for the 4B models**: cgroup peaks of 3.72 GB (qwen3) and 3.73 GB (phi4-mini), with 3.19 and 3.15 GB of
   anonymous memory while loaded; only qwen2.5 fits (1.53 GB peak, 1.19 GB anonymous). On a CPU the server reads the
   weights into memory instead of mapping the file (its log: "disabling mmap ... reason=cpu"), keeps most of them in a
   repacked copy for the CPU (qwen3: 693 MiB plus 1,683 MiB of weight buffers), and sizes the KV cache for its default
   4,096-token context (576 MiB for qwen3), though a title prompt is a few hundred tokens. Inside the 4 GB limit that
   leaves qwen3 about 0.6 GB.
7. **No failures in 720 set calls**, and the first pass was repeatable: every first-pass title of the final run equals
   the first run's, on another fresh server. Later passes on the same server changed some titles (the same title every
   pass for 0.78 to 1.00 of items), apparently through the server's prompt cache; the service asks once per text and
   model, so that rarely shows.
8. **Cold starts decide whether titling happens at all.** A rebuild every 900 s (the default) outlives the 5-minute
   keep-alive, so every rebuild's first title meets a cold server. The adapter's 30 s timeout was shorter than the first
   load on a fresh server for phi4-mini in both runs (the call gave up at 30.0 s; a whole load then took 53.2 s) and
   for qwen3 in the first run (30.1 s; 8.5 s in the final run); qwen2.5 took 4.5 and 16.5 s. Once a model had been
   loaded, a reload took 2.7 s (qwen2.5), 3.5 s (qwen3) and 9.0 s (phi4-mini). When the caller gives up, the server
   abandons the load ("client connection closed before llama-server finished loading, aborting load"), and the core
   stops titling for the rest of that rebuild, so a server whose first load is slower than 30 s may title nothing for
   several rebuilds. This is outside D-027's per-title budget and is recorded, not scored.

| Cold starts | First call, fresh server | Then a whole load took | After unloading | Peak memory | Anonymous / file at end | `docker stats` | Server's own size |
|---|---|---|---|---|---|---|---|
| qwen3 4B | 8.5 s (run 1: 30.1 s, timed out) | - | 3.5 s | 3.72 GB | 3.19 / 0.01 GB | 2.99 GiB | 3.19 GB |
| qwen2.5 1.5B | 16.5 s (run 1: 4.5 s) | - | 2.7 s | 1.53 GB | 1.19 / 0.21 GB | 1.32 GiB | 1.17 GB |
| phi4-mini | 30.0 s, timed out (run 1 too) | 53.2 s | 9.0 s | 3.73 GB | 3.15 / 0.15 GB | 3.09 GiB | 3.11 GB |

### Runs, review and deviations

- **Run 1** (the method as committed) produced the same titles as the final run and the same verdicts. Its cold calls
  for qwen3 and phi4-mini ran into the 30 s timeout, so before the final run the evaluation was extended
  (`722aa14`) to time a whole load after a failed first call and a reload after unloading, and the script to record
  the anonymous and file split and the server's log. Scoring, set and rule did not change. Run 1's peaks were 4.07 GB
  (qwen3), 1.32 GB (qwen2.5) and 3.82 GB (phi4-mini), and its p95s 0.48 to 1.48 s; peaks vary by run.
- One attempt in between stopped at its first model because its report folder, in a shared scratch area, was removed
  while it ran; it produced no results.
- **Compute**: about 23 minutes over all runs, plus the downloads (the 3.4 GB image, compressed, and about 6 GB of
  models). The project, its volume and network, and the image were removed afterwards; nothing is left running. The
  host's free disk fell by about 8 GB while it ran, mostly Docker's own disk file, which does not shrink by itself
  (the space is free again inside Docker's VM).
- **Review** (`titles-review.json`, read after the final run): 169 judgements. 98 flagged words were accepted as
  restatements (inflections, spellings such as "favorite", synonyms such as "rescheduled", translations of the French,
  German and Spanish texts, neutral summaries such as "plan" or "installation") and 66 rejected as added claims
  (invented meetings, people, numbers and judgements); 5 titles the subject terms missed were judged specific (four
  name the kitchen refit by its cabinets and worktops, one the boiler service by its company and date). The excerpt's
  score did not change. The word check's own blind spots, which the review covered: stems with a doubled letter
  ("planning" against "plans") and German compounds.

### Proposed, not decided here

- **D-027's margin on this rubric.** The excerpt is accurate, short and private by construction, so it scores 3 plus
  the share of first lines that name their subject; a candidate can beat it by 0.5 only when at least half the titled
  stars open with a lead-in and the candidate is then nearly perfect. On memories as AI clients save them (in the phase
  0 corpus every first line names its subject) no model could pass. If titles are to be judged where they can help, a
  successor rule could decide on the stars whose excerpt names nothing, with a no-regression guard on the rest; on
  this set qwen3 with the whole text gains 0.95 on the first group and loses 0.25 on the second.
- **If titles are taken up again** (each a code change for its own item, with its own measurement): give the model the
  memory's text rather than its excerpt when the excerpt names nothing, since the excerpt alone only invites invention
  (the privacy results argue for a qwen3-class model and a recheck); make a cold start survivable (a longer first-call
  timeout, a load at engine start, or a keep-alive longer than the rebuild interval); and set a smaller context
  (`num_ctx`), which would cut qwen3's 576 MiB KV cache to about a quarter at 1,024 tokens (an estimate from the
  server's own sizing, not measured) and bring its working memory near the 3 GB budget.
- **Docs**: `docs/adapters/titles.md` says the model sees "the star's excerpt (at most 6,000 characters)"; for the
  memory stars it titles, that excerpt is the first line's first sentence, at most 80 characters.
- **Silent failures** (RHY-11): the likeliest titles failure is the cold-start timeout, and nothing logs it.

### Every title

As the evaluation's own report prints them, one table per model: points of 4 after the review, with the criteria
missed (a accurate, s specific, l length, p private). The excerpt column is the current setting.

#### `qwen3:4b-instruct-2507-q4_K_M`

| Item | Lane | Excerpt today | From the excerpt | From the text |
|---|---|---|---|---|
| t01 | subject | The boiler service is booked with Tarnhill Heating for Thursday 13 November. (4) | Boiler service booked (4) | Boiler Service and Cupboard Instructions (4) |
| t02 | subject | Sam's climbing gym membership renews every March at 38 GBP a month. (4) | March Renewal: 38 GBP (3 s) | Cancel by 20 Feb to avoid clash (3 s) |
| t03 | subject | Alex prefers trains over flights for trips under six hours. (4) | Alex prefers trains for short trips (4) | Train over flight, window seat (4) |
| t04 | subject | Bin collection moves from Monday to Tuesday from 3 November 2026. (4) | Bin Collection Shifts 3 Nov 2026 (4) | Bin day shifts Nov 3–4 2026 (3 a) |
| t05 | subject | Booked Pepper the cat in at Quillon Vets for her annual booster on 12 November. (4) | Pepper's Annual Vet Visit (4) | Pepper's Vet Visit Reminder (4) |
| t06 | subject | Alex's passport expires on 2 April 2027, so renew it before the sabbatical. (4) | Renew passport before sabbatical (4) | Renew passport before sabbatical (4) |
| t07 | subject | Kitchen refit: chose sage green cabinets and oak worktops. (4) | Sage Green Cabinets, Oak Worktops (4) | Kitchen refit: sage green and oak (4) |
| t08 | subject | Sam promised to return the borrowed ladder to the neighbours at number 9 by… (4) | Sam's ladder return promise (4) | Ladder and Trimmer Return Plan (4) |
| t09 | subject | Alex started a new job as audio lead at Larkspur Labs on 1 September 2025. (4) | Alex's New Role at Larkspur Labs (4) | Alex's New Audio Lead Role (4) |
| t10 | subject | The car's MOT is due on 28 November at Fellgate Garage. (4) | MOT due 28 November (4) | MOT due 28 Nov – brake warning (3 a) |
| t11 | subject | Sam's allotment moved to plot 22 at Nettlebank allotments on 1 March 2026. (4) | Sam's plot moved to 22 (4) | Sam's plot update 22 (4) |
| t12 | subject | Fixed the export crash in RHY-4127 by pinning the encoder to v2.14.3. (4) | Fixed export crash by encoder pinning (4) | Fixed export crash with encoder pinning (4) |
| t13 | subject | Water meter reading on 1 October: 04217 cubic metres. (4) | Water meter reading October 1 (4) | Water Meter Reading January (4) |
| t14 | subject | Sam is planning a surprise 40th birthday party for Alex on 5 December 2026. (4) | Sam's Surprise Birthday Plan (4) | Surprise Birthday Party Plan (4) |
| t15 | subject | Alex's favourite café is Kelptide Café on the harbour. (4) | Alex's favourite café is Kelptide Café (4) | Kelptide Café Friday Tip (4) |
| t16 | subject | Heat pump checklist (4) | Heat Pump Installation Checklist (4) | Heat Pump Installation Tasks (4) |
| t17 | subject | Planting plan for plot 22 (4) | Planting Schedule for Plot 22 (4) | Plot 22 Planting Schedule (4) |
| t18 | subject | Recette de la tarte aux pommes de grand-mère : beurre demi-sel et pommes… (4) | Tarte aux pommes traditionnelle (3 a) | Tarte aux pommes au secret (4) |
| t19 | preamble | Note to self (3 s) | Stay organized and focused (2 as) | Spare key and alarm code (4) |
| t20 | preamble | Quick reminder for tomorrow (3 s) | Tomorrow's Quick Reminder (3 s) | Recycling Reminder Tonight (4) |
| t21 | preamble | Things to remember (3 s) | Key reminders for daily life (2 as) | Flea treatment reminder (4) |
| t22 | preamble | From the call this morning (3 s) | Morning call summary (3 s) | Oakvale Media Renewal Update (4) |
| t23 | preamble | Okay so (3 s) | Getting started with notes (2 as) | Choir Trip Deposit Reminder (4) |
| t24 | preamble | Idea (3 s) | Spark of Inspiration (3 s) | Box Room Workshop Setup (4) |
| t25 | preamble | Monday 3 November (3 s) | Morning Reflections – 3 November (2 as) | Dentist appointment update (4) |
| t26 | preamble | TODO (3 s) | Action Items to Complete (3 s) | Renew Home Insurance & Add Bikes (4) |
| t27 | preamble | Re what we talked about (3 s) | Follow up on our conversation (3 s) | Sam covers Ashworth dog walks (4) |
| t28 | preamble | Random thought (3 s) | Spontaneous idea strikes (3 s) | Sabbatical Start and Coast Path Plan (4) |
| t29 | preamble | Kurze Notiz (3 s) | Brief Reminder (3 s) | Heizungsablesung am 14.11 (4) |
| t30 | preamble | Para no olvidar (3 s) | Don't forget (3 s) | Cena remo viernes 28 (4) |
| t31 | fragment | milk (3 s) | Milk Facts and Uses (2 as) | Saturday Shop List (4) |
| t32 | fragment | 10k in 52:30 (4) | 10k in 52 minutes 30 seconds (4) | 5K in 52:30 with new shoes (2 as) |
| t33 | fragment | Tuesday (3 s) | Tuesday Reflections (2 as) | Glasses Done, Test in Two Years (4) |
| t34 | fragment | 3 x 2h sessions, 45 GBP each (3 s) | Three sessions at 45 GBP each (3 s) | Piano Lessons Schedule (4) |
| t35 | fragment | The flat on Weir Street: two bedrooms, 1,150 GBP a month, viewing on Saturday… (4) | Weir Street Flat Viewing (4) | Flat on Weir Street – View Saturday (4) |
| t36 | fragment | Pages 40–112 (3 s) | Pages 40 to 112 (3 s) | Book Club: The Salt Orchard (4) |
| t37 | fragment | Meter: 48211 (3 s) | Personal Reflection on 48211 (2 as) | Electricity Tariff Update (4) |
| t38 | fragment | ok (3 s) | Note ok (3 s) | Best Man Speech – 9 May 2027 (4) |
| t39 | fragment | à faire (3 s) | To Do (3 s) | Tâches à faire (3 s) |
| t40 | fragment | Sa., 10 Uhr (3 s) | Saturday at 10 a.m (3 s) | Flohmarkt Stand 31 reserviert (4) |

#### `qwen2.5:1.5b-instruct`

| Item | Lane | Excerpt today | From the excerpt | From the text |
|---|---|---|---|---|
| t01 | subject | The boiler service is booked with Tarnhill Heating for Thursday 13 November. (4) | Service booked for Tarnhill Heating on 13 November (4) | Clear cupboard, side gate, key safe code 3816 (2 sp) |
| t02 | subject | Sam's climbing gym membership renews every March at 38 GBP a month. (4) | Membership renewal: 38 GBP per month (4) | Renews March, 38 GBP, cancel by 20 Feb (3 s) |
| t03 | subject | Alex prefers trains over flights for trips under six hours. (4) | Alex prefers trains for short trips (4) | Alex prefers trains for short trips (4) |
| t04 | subject | Bin collection moves from Monday to Tuesday from 3 November 2026. (4) | Bin collection shifts to Tuesday, 3 Nov 2026 (4) | Bin collection shifts to Tuesday, recycling remains fortnightly, (4) |
| t05 | subject | Booked Pepper the cat in at Quillon Vets for her annual booster on 12 November. (4) | Booked Pepper for annual booster at Quillon Vets (4) | Booked Pepper for vet visit on 12 Nov (4) |
| t06 | subject | Alex's passport expires on 2 April 2027, so renew it before the sabbatical. (4) | Renew passport by 2 April 2027 (4) | Renew passport before sabbatical (4) |
| t07 | subject | Kitchen refit: chose sage green cabinets and oak worktops. (4) | Chose sage green cabinets, oak worktops (4) | Kitchen project: Sage green cabinets, oak worktops, budget (4) |
| t08 | subject | Sam promised to return the borrowed ladder to the neighbours at number 9 by… (4) | 9 o'clock (2 as) | Sam returns ladder to neighbors by Saturday, hedge (4) |
| t09 | subject | Alex started a new job as audio lead at Larkspur Labs on 1 September 2025. (4) | Alex joins Larkspur Labs as audio lead (4) | Alex starts new job at Larkspur Labs (4) |
| t10 | subject | The car's MOT is due on 28 November at Fellgate Garage. (4) | MOT due at Fellgate Garage on 28 November (4) | MOT due at Fellgate Garage on 28 November (4) |
| t11 | subject | Sam's allotment moved to plot 22 at Nettlebank allotments on 1 March 2026. (4) | Sam's allotment relocated to plot 22, Nettlebank (4) | Sam's allotment moved to Nettlebank, plot 22, with (4) |
| t12 | subject | Fixed the export crash in RHY-4127 by pinning the encoder to v2.14.3. (4) | Fixed encoder issue in RHY-4127 (4) | Fixed encoder pinning for v2.14.3 release (3 a) |
| t13 | subject | Water meter reading on 1 October: 04217 cubic metres. (4) | Meter shows 4217 cubic meters on 1 October (3 s) | Read meter, prepare January bill (2 as) |
| t14 | subject | Sam is planning a surprise 40th birthday party for Alex on 5 December 2026. (4) | Sam plans Alex's 40th birthday party on 5 (4) | Party for Alex at Ottervale Boathouse, Dec 5, (3 p) |
| t15 | subject | Alex's favourite café is Kelptide Café on the harbour. (4) | Alex's favorite café is Kelptide Café by the (4) | Alex's favorite café on the harbour, Kelptide Café, (4) |
| t16 | subject | Heat pump checklist (4) | Checklist for Heat Pump Installation (4) | Prepare for installation checklist (3 s) |
| t17 | subject | Planting plan for plot 22 (4) | Planting schedule for plot 22 (4) | Planting schedule for plot 22 (4) |
| t18 | subject | Recette de la tarte aux pommes de grand-mère : beurre demi-sel et pommes… (4) | Grand-mère's Apple Tart Recipe (4) | Grand-mère's Apple Tart Recipe: Half-Salted Butter and Reinette (4) |
| t19 | preamble | Note to self (3 s) | Important personal reminder (2 as) | Reminder: Spare Key & Alarm Code (4) |
| t20 | preamble | Quick reminder for tomorrow (3 s) | Reminder: Tomorrow's Event (2 as) | Recycling and glass collection changes (4) |
| t21 | preamble | Things to remember (3 s) | Important things to keep in mind (2 as) | Pepper's next flea treatment is due on the (4) |
| t22 | preamble | From the call this morning (3 s) | Important meeting reminder (2 as) | Contract renewal date shifts to January. Rate remains (4) |
| t23 | preamble | Okay so (3 s) | Plan next steps (2 as) | Collecting choir deposits for Brackenford trip (4) |
| t24 | preamble | Idea (3 s) | An idea sparked (3 s) | Transform box room into workshop for £300 (4) |
| t25 | preamble | Monday 3 November (3 s) | Day 1 of the week (2 as) | Referral letter needed for Thursday appointment (3 s) |
| t26 | preamble | TODO (3 s) | TODO (3 s) | Renew home insurance, inquire about bike coverage (4) |
| t27 | preamble | Re what we talked about (3 s) | Meeting recap (2 as) | Sam will take over dog walks for Ashworths (4) |
| t28 | preamble | Random thought (3 s) | Random thought (3 s) | Plan train for coast path start (4) |
| t29 | preamble | Kurze Notiz (3 s) | Short Note (3 s) | Keller aufräumen am 14. November (3 s) |
| t30 | preamble | Para no olvidar (3 s) | Remember to keep notes (2 as) | Reminder: Remo Club Dinner, Friday 28th, 20:30, Mirador. (4) |
| t31 | fragment | milk (3 s) | Dairy product (2 as) | Prepare Alex's breakfast, milk, eggs, oat milk, bin (2 as) |
| t32 | fragment | 10k in 52:30 (4) | 10k in 52:30 (4) | First run under 53, new shoes help. Next: (4) |
| t33 | fragment | Tuesday (3 s) | Day of the week (3 s) | Glasses picked up, next eye test in two (4) |
| t34 | fragment | 3 x 2h sessions, 45 GBP each (3 s) | 3 sessions, 45 GBP each (3 s) | Sam's piano lessons commence, lessons cost £45 each, (4) |
| t35 | fragment | The flat on Weir Street: two bedrooms, 1,150 GBP a month, viewing on Saturday… (4) | Flat for viewing on Weir Street (4) | Flat listing: Weir Street, 2 bedrooms, £1,150, Saturday (4) |
| t36 | fragment | Pages 40–112 (3 s) | Book chapters 40-112 (2 as) | Book club meeting at Hettie's on 4 December (3 p) |
| t37 | fragment | Meter: 48211 (3 s) | 48211 meter (3 s) | Electricity bill for Brightwater Energy switch (3 a) |
| t38 | fragment | ok (3 s) | Understood (3 s) | Alex agrees to Tomasz's wedding speech (3 p) |
| t39 | fragment | à faire (3 s) | To do (3 s) | Call plumber for sink leak. Buy silicone sealant (3 s) |
| t40 | fragment | Sa., 10 Uhr (3 s) | Meeting at 10 AM (2 as) | Reserve booth at 31st stall at 10 AM (3 s) |

#### `phi4-mini`

| Item | Lane | Excerpt today | From the excerpt | From the text |
|---|---|---|---|---|
| t01 | subject | The boiler service is booked with Tarnhill Heating for Thursday 13 November. (4) | Boiler Service Booking: Thursday 13 November (4) | Boiler Service Booking and House Key Code (4) |
| t02 | subject | Sam's climbing gym membership renews every March at 38 GBP a month. (4) | Sam's Gym Membership Renewal March 38 GBP (4) | Sam's Gym Membership Renewal and Cancellation Info (4) |
| t03 | subject | Alex prefers trains over flights for trips under six hours. (4) | Alex's Train Preference for Short Trips (4) | Alex's Train Travel Preferences and Seat Choice (4) |
| t04 | subject | Bin collection moves from Monday to Tuesday from 3 November 2026. (4) | Bin Collection Rescheduled to Tuesday (4) | Bin Collection Update: Tuesday from 3 Nov 2026 (4) |
| t05 | subject | Booked Pepper the cat in at Quillon Vets for her annual booster on 12 November. (4) | Pepper's Annual Booster at Quillon Vets (4) | Pepper's Annual Booster Appointment at Quillon Vets (4) |
| t06 | subject | Alex's passport expires on 2 April 2027, so renew it before the sabbatical. (4) | Alex Passport Renewal Reminder (4) | Passport Renewal Reminder for Alex (4) |
| t07 | subject | Kitchen refit: chose sage green cabinets and oak worktops. (4) | Sage Green Cabinets and Oak Worktops Refit (4) | Sage Green Cabinets & Oak Worktops Refit Budget (4) |
| t08 | subject | Sam promised to return the borrowed ladder to the neighbours at number 9 by… (4) | Return Ladder Neighbours 9 by Date (4) | Return Ladder & Hedge Trimmer by Saturday (4) |
| t09 | subject | Alex started a new job as audio lead at Larkspur Labs on 1 September 2025. (4) | New Job at Larkspur Labs (4) | New Job at Larkspur Labs (4) |
| t10 | subject | The car's MOT is due on 28 November at Fellgate Garage. (4) | Car MOT Due 28 Nov at Fellgate Garage (4) | MOT Update Fellgate Garage (4) |
| t11 | subject | Sam's allotment moved to plot 22 at Nettlebank allotments on 1 March 2026. (4) | Sam's Allotment Plot 22 Nettlebank 2026 (4) | Sam's Allotment Plot 22 Details (4) |
| t12 | subject | Fixed the export crash in RHY-4127 by pinning the encoder to v2.14.3. (4) | Fixed RHY-4127 Export Crash: Encoder pinned to v2.14.3 (4) | Fixed Export Crash RHY-4127 v2.14.3 Pinning Encoder (4) |
| t13 | subject | Water meter reading on 1 October: 04217 cubic metres. (4) | October Water Meter Reading: 04217 Cubic Metres (4) | Water Meter Reading Update (4) |
| t14 | subject | Sam is planning a surprise 40th birthday party for Alex on 5 December 2026. (4) | Alex's 40th Birthday Surprise Party Plan (4) | Surprise 40th Birthday Party Plan for Alex (4) |
| t15 | subject | Alex's favourite café is Kelptide Café on the harbour. (4) | Alex's Favourite Café: Kelptide Café on the Harbour (4) | Alex's Favourite Café: Kelptide Café on Harbour (4) |
| t16 | subject | Heat pump checklist (4) | Essential Heat Pump Checklist (3 a) | Heat Pump Installation Checklist (4) |
| t17 | subject | Planting plan for plot 22 (4) | 22 Plot Planting Schedule (4) | Planting Plan for Plot 22 (4) |
| t18 | subject | Recette de la tarte aux pommes de grand-mère : beurre demi-sel et pommes… (4) | Grand-mère's Apple Tart Recipe (4) | Grand-mère's Apple Tart Recipe (4) |
| t19 | preamble | Note to self (3 s) | Review Meeting Notes (2 as) | Spare Key Location and Alarm Code Note (4) |
| t20 | preamble | Quick reminder for tomorrow (3 s) | Reminder: Tomorrow's Meeting (2 as) | Reminder: Recycling Pickup Moved (4) |
| t21 | preamble | Things to remember (3 s) | Things to Remember (3 s) | Pepper's Flea Treatment Reminder (4) |
| t22 | preamble | From the call this morning (3 s) | Morning Call Summary (3 s) | Oakvale Media Contract Renewal Update (4) |
| t23 | preamble | Okay so (3 s) | Okay So (3 s) | Community Choir Coach Trip Brackenford 21 Nov (4) |
| t24 | preamble | Idea (3 s) | New Marketing Strategy (2 as) | Transform Box Room into Workshop (4) |
| t25 | preamble | Monday 3 November (3 s) | Meeting with John Smith (2 as) | Dentist Appointment Update (4) |
| t26 | preamble | TODO (3 s) | Complete Project Report (2 as) | Renew Home Insurance Policy HX-20931 (3 p) |
| t27 | preamble | Re what we talked about (3 s) | Recap of our conversation (3 s) | Sam's Dog Walk Coverage Plan (4) |
| t28 | preamble | Random thought (3 s) | Random Thought Ideas (3 s) | Coast Path Journey Plan (4) |
| t29 | preamble | Kurze Notiz (3 s) | Kurze Notiz: Quick Reminder (3 s) | Heizungs-Ablauf-Termin (3 a) |
| t30 | preamble | Para no olvidar (3 s) | Memories of Love (2 as) | Club de Remo Cena Reserva Requisito (4) |
| t31 | fragment | milk (3 s) | Milk Consumption Tips (2 as) | Alex's Birthday Shopping List (3 p) |
| t32 | fragment | 10k in 52:30 (4) | Personal Best 10k Run in 52:30 (3 a) | New 52:30 Personal Best (2 as) |
| t33 | fragment | Tuesday (3 s) | Tuesday's Meeting (2 as) | New Glasses Purchase at Lindqvist Opticians (4) |
| t34 | fragment | 3 x 2h sessions, 45 GBP each (3 s) | Cost of 3 x 2-hour sessions at 45 (3 s) | Piano Lessons for Sam with Mrs Halloran (3 p) |
| t35 | fragment | The flat on Weir Street: two bedrooms, 1,150 GBP a month, viewing on Saturday… (4) | Flat for Rent: Weir Street, 2 Bedrooms, £1,150/month, (4) | Weir Street Flat - Damp Bathroom Issue (4) |
| t36 | fragment | Pages 40–112 (3 s) | Pages 40–112 (3 s) | Book Club Meeting: The Salt Orchard (4) |
| t37 | fragment | Meter: 48211 (3 s) | 48211 Meter Analysis (2 as) | Brightwater Energy Switch Update (4) |
| t38 | fragment | ok (3 s) | Quick Decision (2 as) | Alex's Wedding Speech Commitment (4) |
| t39 | fragment | à faire (3 s) | Shopping List: à faire (2 as) | Plombier Appointment and Silicone Refill (3 a) |
| t40 | fragment | Sa., 10 Uhr (3 s) | Meeting at 10 AM (2 as) | Reservierung am Flohmarkt am Hafen (4) |

## Re-test under the successor rule (D-250)

On 9 November 2026 the owner asked for qwen3:4b-instruct-2507-q4_K_M to be tested again, judged by D-058's
successor rule, which was written as a dated proposal and committed before the run (D-250, `8a0103e`). Synthetic data
only, from `sb/p2-models2`; the same set, rubric, review, script and pinned image (`ollama/ollama:0.40.13`, pulled for the
run, CPU only), in the compose project `sb-titles-eval` on `127.0.0.1:11534`, Docker Desktop's VM with 32 CPUs, the
server with the stack's 4 GB limit; the titles adapter, its prompt and its defaults are unchanged and it stays off. One
model, both inputs, 3 passes (240 titles) plus the cold-start calls.

### The rule, as applied

- **Input**: the memory's whole text. Only the `text` rows below are judged; the `excerpt` rows are what the service
  sends today, kept for comparison.
- **Judged**: the 20 items whose excerpt names nothing (the rubric's specific point is 0 on the excerpt: the 12
  preambles and 8 of the 10 fragments). A mean of at least 3.0 and at least 0.5 above the excerpt's 3.00 there.
- **Guard**: on the other 20 (the excerpt 4.00) at most 0.25 below.
- **Kept from D-027**: the four points (private detail among them), the reviewed score deciding, p95 at most 5 s,
  at most 5% failed; and a cold load within the adapter's 30 s.
- **Memory**: reported against the 3 GB development budget, not decided here.

### Results

| Under D-250 | Mean there | vs excerpt (95% interval) | Automatic check only | Rule | Passes |
|---|---|---|---|---|---|
| judged: 20 items whose excerpt names nothing | 3.95 | +0.95 (+0.85 to +1.00) | 3.65 (+0.65) | at least 3.0, and +0.5 | yes |
| guard: the 20 whose excerpt names its subject | 3.75 | −0.25 (−0.50 to −0.05) | 3.35 (−0.65) | at most −0.25 | yes, exactly on the line |

| Check | Measured | Budget | Passes |
|---|---|---|---|
| private detail in a title | none of the 26 items' details, in any of the 40 titles | a point a title | yes (0 leaks) |
| cold load, first call to a fresh server | 13,961 ms | 30 s | yes |
| reload after unloading | 2,263 ms | (information) | |
| latency, whole text | p50 409 ms, p95 668 ms, max 762 ms over 120 calls | p95 5 s | yes |
| failed or empty | 0 of 120 (and 0 of 120 from the excerpt) | 5% | yes |
| memory | cgroup peak 3.71 GB (3.19 GB anonymous, 0.01 GB file; `docker stats` 3.00 GiB; the server's own size 3.19 GB) | 3 GB development budget | **no**, reported |

**Verdict under D-250: passes on the benchmark machine, with two caveats to weigh.** Given the whole text, qwen3 4B
writes a title that names the subject for every item whose excerpt names nothing (3.95 against the excerpt's 3.00), leaks
no private detail, starts cold in 14 s and answers in under a second. But:

- **The guard passes on its line, not with room.** Where the excerpt already names its subject the model loses a point on
  5 of the 20 items (3.75 against 4.00), exactly the 0.25 the guard allows, and the interval of that loss lies wholly
  below zero: the regression is real, only small. The automatic check alone (before the review's judgements) puts the
  loss at −0.65 and would fail the guard; the review, as D-027 and D-250 have it, decides. The rule was written after the
  first run's numbers were known and on the same set, so this is not a blind test (D-250 says so): a fresh labelled set
  is the honest one.
- **Memory is over the development budget**: 3.71 GB at peak against 3 GB, inside the stack's 4 GB container limit with
  about 0.3 GB to spare. **The server's own memory is the real limit, and it cannot be measured from here**: whether an install's server
  has 3.7 GB free beside the stack, at the times a rebuild titles, is for its operator to check. A smaller
  context (`num_ctx`), proposed above, would cut the KV cache; it is a code change that was not made or measured.

Every first-pass title is identical to the first run's (all 80, both inputs), so the existing review's 169 judgements
cover every title and none was added; the whole-set figures D-027 judges are the first run's again (excerpt 3.28,
−0.23; text 3.85, +0.35, +0.13 to +0.58), and D-027 still keeps the current setting (margin and memory). The titles are
[above](#qwen34b-instruct-2507-q4_k_m). Later passes changed one title in forty on the whole text (the same title every
pass for 0.975 of items), as before.

What it would take to use it, each the owner's call and none made here: adopt D-250 in place of D-027; give the core the
memory's text for stars whose excerpt is their title (a code change, SB-P3-11's stars); make a cold start survivable
on the install's server; and check that server's free memory. Until then the titles adapter stays off by default.

**Compute**: about 3 minutes of model time, plus the downloads: the model (2.50 GB on disk) and the image (8.5 GB
unpacked). The project's container and network were removed after the run; its volume, kept for the near-miss check
([relevance.md](relevance.md#idea-5-an-ai-check-of-near-misses)), and the image were removed once that had run. Nothing
is left running.
