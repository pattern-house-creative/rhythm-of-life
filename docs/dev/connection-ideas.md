# Ideas for finding connections

Ways the map could find more real links between stars, assessed in October 2026 and kept here for later.
None is built or approved but where an idea says so. The owner picks what to build; until then this is a backlog,
not a plan.

## What the map does today

Links come from references inside a record (URLs, note links), folder hubs, data-star topic words
(`CatalogStars.Records`), and local similarity (`engine/src/Core/RelationshipSimilarity.cs`). Each memory
or readable document has one 384-dimension MiniLM vector made from its first chunks. A pair becomes a
dashed `similar_to` suggestion above `graph.similarity.threshold` (0.6; 0.8 between two documents), or,
when each is among the other's `neighbors` closest, above `mutualThreshold` (0.45). See
`docs/configuration.md`.

On one real install of about 100 stars, the small model's scores bunched together: related pairs scored
about 0.45 to 0.55 and unrelated pairs about 0.3. Documents that share common vocabulary (architecture
notes, for example) scored high against everything. That is why the mutual rule exists, and what most
of the ideas below aim at.

## Four facts every idea has to fit

- **Text is only seen during a rebuild,** through one callback: `CatalogRelationshipSource.Vector(key,
  contentRevision, title, text)` in `RelationshipHost.cs`. It runs for Second Brain, notes, saved documents
  and changed Drive files, so a new signal can hook in there without touching adapters. It returns early
  while embeddings are off, so it would need rearranging.
- **Stored vectors carry their model.** A vector is kept while its content revision is unchanged, and each one
  now names the model that made it, so vectors of two models are never compared (bge-small is also 384
  dimensions, so the length check in `Comparable()` alone would not catch it). That is how the map can draw its
  similar links from a second model (`graph.similarity.model`, D-256).
- **Time and caps are tight.** A scheduled rebuild has 4 minutes (`RelationshipBudget.Scheduled`), of
  which titles may take 60 s. The titles service runs one model at a time. All suggestions together are
  capped at 2000 (`Discover()` throws past that), so new sources need one combined cap.
- **The app shows each link's excerpt and locator.** The excerpt (up to 1000 characters) is where a
  one-line reason goes. `app/src/graphView.ts` decides from the locator and type how a link is labelled.
  Every link shows in a star's panel (suggestions dashed). The sky draws them too, but with "Automatic links on the
  sky" off in Sources & settings it draws no dashed one, and Home's sky, map parts and the explorer's Focus draw folder
  and project lines only for the chosen star ([screens.md](screens.md) section 6); Trace follows confirmed links only.
  Discover's Dismiss lives in screen state only (`Explorer.tsx`) and is lost on reload,
  unless the engine serves the memories (`memory.tools` on): then it is kept (idea 1's first half, below).

## The ideas, in the suggested order

### 1. Keep the owner's judgement

Save Dismiss decisions so a dismissed pair stays hidden across rebuilds, and let the owner label about
40 pairs by title ("related" or "not"). **The first half is built** (Second Brain parity SB-P4-05, migration 057, behind
`memory.tools`): a Dismiss is kept by both stars and the type, honoured by every read and rebuild, and listed in Discover
with Show again. The labels are the parity program's labelled pairs (SB-P2-18), not a screen of their own. Every idea below can then be scored against those labels instead
of by eye. Small: a table keyed by both stars' ids, an app call, and the Discover button.

### 2. Shared names

Link stars that mention the same specific person, project, organisation, place or event: "Both mention
Acme Health". **Measured, not kept** (Second Brain parity SB-P4-12): it finds 3 more labelled pairs and no
unrelated one, but on the map it loses graph chains in both load modes
([second-brain-parity/relevance.md](second-brain-parity/relevance.md)).
- **How:** extract candidate names in the `Vector` hook (capitalised runs of words, plus names the engine
  already knows: people in `rhythm.yaml`, Second Brain project names and aliases, note tags and folders).
  Store up to 30 per record in a new `Names` field, stripped from ordinary reads like `Embedding` is. A
  name links stars only if it appears in at least 2 and at most about 5 of them; very common words and
  the household's own names are dropped. A new `NameSuggestions` emits undirected `shares_name` links,
  `inferred` like topic links (a lone first name only `suggested`), with the locator `names:shared`.
- **Cost:** under a second per rebuild and about 40 KB stored. No model.
- **Quality:** precise for multi-word, rarely used names. Finds only explicit mentions, nothing thematic.
- **Effort:** about a day: a new `RelationshipNames.cs` (around 200 lines), small changes in
  `RelationshipDiscovery.cs`, `RelationshipHost.cs`, `RelationshipEngine.cs`, the config, `graphView.ts`
  and `Explorer.tsx`, and tests.
- **Risks:** ordinary capitalised words making false links; a frequent name tangling into many pairwise
  links (a name star on the sky may suit those better); `Bridges()` already drops links between two
  different people's stars.

### 3. A correction for stars that match everything

Some stars are close to everything because of shared vocabulary. A well-known correction (CSLS) scores a
pair as `cos(a, b) − ½(r_a + r_b)`, where `r` is a star's mean similarity to its nearest neighbours. It
discounts those hub stars directly, costs almost nothing, and works with the mutual rule. Small: a few
lines in `SimilaritySuggestions` and new thresholds tuned on the labels. **Measured, not kept** (SB-P4-12): at
today's number of links it ranks pairs better, but with the mutual rule it links two more unrelated labelled
pairs and finds none today's map misses ([second-brain-parity/relevance.md](second-brain-parity/relevance.md)).

### 4. A better fingerprint model, then sections

- **Model:** bge-small-en-v1.5 (33M parameters, 384 dimensions, 512 tokens, MIT, about 34 MB quantised)
  runs in the engine with ONNX like MiniLM does, but uses CLS pooling instead of mean pooling. bge-base is
  the 768-dimension version. e5-small-v2 needs a `query:` prefix on both texts. Changes: `LocalEmbeddings.cs`
  (pooling, prefix, token limit) and `Embeddings.cs` (pinned files and checksums). The `EmbeddingModel` tag in
  the reuse check and the model check in `Comparable()` are already in place (Second Brain parity SB-P2-02), so
  a new model's vectors replace the old ones as records are read and never pair with them. These models score
  everything higher, so every threshold is re-tuned; rank-based rules carry over.
- **Sections:** today only the first 4 chunks (about 4,000 characters) count. Instead, store up to 16
  section vectors per star in a new `Sections` field and score a pair by the mean of its two best section
  pairs, so a long document isn't blended into one fingerprint. The link could say which sections matched.
- **Cost:** re-embedding everything once takes seconds (bge-small) to a minute (bge-base or sections),
  then only changed stars. About 150 to 300 MB of RAM; sections take about 50 KB per star.
- **Not recommended:** nomic-embed-text through the titles service's Ollama. Embedding would depend on
  that service, and with one loaded model it would swap with the titles model every rebuild.
- **Risks:** `semantic_search` shares the model and the `vector(384)` column, so it either keeps MiniLM
  or is re-indexed.
- **Effort:** one to two days for the model and one to two more for sections, each tried against the
  labels before it replaces anything.

### 5. AI check of near misses

The titles service's small model reviews pairs that scored just under the bars (for example 0.40 up to
the threshold, at most 5 per star) and keeps only real connections, each with a one-line reason: "AI
check: both trace a move into engineering." It could also re-check the weakest existing `similar_to`
links and drop the ones it rejects. **Measured, not kept** (SB-P4-12, 9 November 2026, D-252): on the 124 labelled
pairs the map may join, the band holds 9 pairs shown together that today's map misses and 27 unrelated ones. The titles
model (qwen3 4B, on a development machine, asked only true or false, no reason kept) keeps 7 of the 9 and one unrelated
pair, which the keep rule does not allow; the two cross-encoders keep one pair each
([second-brain-parity/relevance.md](second-brain-parity/relevance.md#idea-5-an-ai-check-of-near-misses)).
- **How:** a new `RelationshipReview` step. The texts come from a store that lives for one rebuild only,
  filled by the `Vector` hook (about 1,200 characters per star); nothing is re-read or kept. A `Review`
  call in `TitlesAdapter.cs` asks for a JSON answer `{related, reason}` (Ollama's `format`) at temperature
  0, and the reason is cleaned like titles are. The prompt keeps a pair only when the model names one
  concrete thing they share. Verdicts are cached in a new table keyed by model, prompt version and both
  content revisions (the next migration). Kept pairs stay `suggested`.
- **Cost:** on a CPU, 8 to 20 s per pair, so a first backlog of 80 to 150 pairs converges over hours at
  60 to 90 s per rebuild. On a GPU, roughly a second per pair. No new RAM (the titles model is loaded).
- **Quality:** clearly better precision inside the band; it can't find what the band leaves out. Small
  models lean towards "yes" and can give generic reasons.
- **Effort:** three to four days (about 600 to 800 lines across `TitlesAdapter.cs`, `Adapters.cs`, a new
  `RelationshipReview.cs`, the migration, `RelationshipHost.cs`, the config, the app and tests).
- **Risks:** it competes with titles for the one model slot and the rebuild budget; a star's text could
  try to steer the model (the damage is one wrong suggestion); reasons stay labelled as unconfirmed.

### 6. Cited together in an insight

Pairs of stars cited by the same insight (one that wasn't dismissed) become `cited_with` links,
`inferred`, with an excerpt like "Cited together in 'Sleep and morning glucose' (28 Oct)". Worked out
fresh each rebuild, nothing new stored. The map's own login can't read insights today, so it needs a new
database function. About a day. Few pairs, and mostly ones the AI found by following links already on
the map. **Measured, not kept** (SB-P4-12): the corpus's one labelled pair cited together is linked today
already ([second-brain-parity/relevance.md](second-brain-parity/relevance.md)).

## Not worth doing

- **Dashboard pairs:** every dashboard is already a star with verified `draws_on` links to what it reads.
- **Opened together:** nothing records what is opened, and starting would mean tracking who looked at
  what. Usage is sparse and feeds on itself.

## Other signals worth a look

- **Memories saved close together** (within about 30 minutes): a free signal for Second Brain memories.

## Trying an idea safely on real data

- **A read-only probe:** `rhythm-engine probe-links --method names|review|model|cited`, run once like
  `refresh`. It reads the stored snapshot in a read-only transaction (and re-lists sources read-only where
  it needs text), saves nothing, and prints only `score · method · title A ↔ title B` plus score
  percentiles. Names print as a count, not the names. The AI check prints keep or drop and seconds per
  pair, reasons hidden by default, with the first run capped at 20 pairs. A model trial prints each test
  star's top 5 under both models.
- **Against the labels:** the probe reports each method's precision and how many labelled pairs it finds.
- **Shadow mode:** once a method looks good, run it inside the rebuild with `mode: shadow`, logging counts
  only, before it shows anything.

## Questions for the owner before building

1. May names and AI reasons be stored and shown as evidence? Like titles, they reach the AI client through
   `explore_graph`.
2. Shared names as pairwise links, or as name stars on the sky (a star for a person, linked to everything
   that mentions them)?
3. Should AI-checked links read differently from plain similarity links (both show dashed on the sky), and
   may the check remove `similar_to` links it rejects?
4. Should shared names cross `Bridges()` (links between two different people's stars)?
5. If the map changes model, does `semantic_search` keep MiniLM?
6. Will you label about 40 pairs by title?
