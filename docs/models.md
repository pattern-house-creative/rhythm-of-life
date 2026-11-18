# Models

Every model Rhythm of Life uses, what each one does, where it runs, and how to see whether it is working. Rhythm keeps
the models in the stack small and optional: each one makes something better, and without it the same thing still works
in a plainer way. All reasoning (insights, digests, deciding what a memory means) is done by your own AI client, outside
the stack.

## The models and their jobs

| Model | Job | Where it runs | On by default | Without it |
|---|---|---|---|---|
| all-MiniLM-L6-v2 (`minilm`) | the **embeddings** model: `semantic_search`, the passages of saved documents and of the texts `adapters.embeddings.sources` lists, and the map's dashed "similar" links (unless `graph.similarity.model` names another) | inside the engine, on the CPU, about 23 MB on disk | yes | search matches words only, and the map draws no similar links |
| bge-small-en-v1.5 (`bge-small`) | the **map model** when `graph.similarity.model: bge-small`: a second vector per star, read only by the map's similar and copy links, which it draws more of (D-256); and a **candidate** embeddings model, measured against MiniLM before it may serve search, so for search it can only be a shadow, whose index is built beside MiniLM's and read by no search | inside the engine, on the CPU, about 34 MB on disk and about 58 MiB of memory, only when `graph.similarity.model` or `adapters.embeddings.shadow` names it | no | the map's similar links use MiniLM's vectors |
| bge-base-en-v1.5 (`bge-base`) | a **measured** embeddings model (768 dimensions): benchmarked against MiniLM, it can be neither the live model nor the shadow, since the passage table holds 384-dimensional vectors ([dev/second-brain-parity/benchmarks.md](dev/second-brain-parity/benchmarks.md#bge-base)) | only in the benchmark's throwaway databases | no | nothing changes |
| bge-reranker-base or ms-marco-MiniLM-L6-v2 (the **reranker**, `engine.rerank`) | reorders combined retrieval's first 20 results by a cross-encoder's scores, behind a readiness probe and a circuit breaker | inside the engine, on the CPU (1.1 GB or 91 MB on disk), only when `engine.rerank` names one | no | results keep the combined order |
| a local language model of your choosing (the **titles** model) | short **titles** for stars whose title is still the first words of their text, from the star's whole text | the optional `titles` compose service (Ollama), on the stack's internal network | no; the suggested model, given the whole text, measured better than the excerpt where it names nothing and slightly worse elsewhere ([adapters/titles.md](adapters/titles.md#what-was-measured)) | those stars keep their excerpts as titles |
| your AI client (ChatGPT, Claude or another MCP client) | everything that needs reasoning: the daily insight, reading and writing through the tools, deciding what links mean | wherever that client runs; it reaches the engine over MCP with its own token ([../harness/README.md](../harness/README.md)) | not part of the stack | the engine and the app work on their own; nothing writes insights |

The reranker is off by default and stays off: it would be turned on only if it improved search without making any kind
of question worse, within D-029's time budget, and neither model did; the measurement is in [dev/second-brain-parity/benchmarks.md](dev/second-brain-parity/benchmarks.md#the-reranker-sb-p2-12)
(SB-P2-12). While it is loading, failed its checks or rests after failures, searches keep the combined order and say so.

The embeddings models are pinned: each is one commit of its public repository, and each file is checked against its
SHA-256 before the engine loads it. Every stored vector names the model that made it, and vectors of two models are
never compared. Their settings and where their files come from are in
[configuration.md](configuration.md#embedding-models).

## Model licences

The pinned model cards for MiniLM and the ms-marco reranker declare Apache-2.0. The BGE-small,
BGE-base and BGE reranker cards declare MIT. BGE-small's quantized weights and vocabulary have
the same SHA-256 checksums as the previous download source, so existing installations can reuse
their files and stored vectors. See the [third-party inventory](licenses/README.md#model-terms).

## Seeing what each model is doing

Three places show the same states, read from the engine:

- **The app**: Sources & settings, under Models. Each model with its job and state, and for the embeddings model how
  many passages its index holds and whether the index job is still working through rows. With memory scopes on
  ([dev/memory.md](dev/memory.md) 6.2) the counts are left out there and in `/health`: they count every person's
  rows together. `rhythm doctor` then reads the passages from the database.
- **`./rhythm doctor`**, while the stack is up: the engine's version and schema, then a line per model. A model that is
  not in the state its settings ask for is a `note`, never a failure, since every model is optional.
- With `graph.similarity.model` set, the map model is listed too (role `map`). While it is not `on` (starting, its
  files missing or not matching their checksums) the map's similar links use MiniLM's vectors and `graph.similarity`'s
  numbers, and each rebuild says so in the engine log (`Map rebuild: graph.similarity.model bge-small is ...`); once it
  loads, the engine rebuilds the map once more. While either model is loading, a rebuild keeps the configured
  models' stored vectors for unchanged content. The next warm rebuild reuses them, including for Drive files.
- **The engine's `GET /health`**: the same, as JSON ([dev/engine.md](dev/engine.md)). It answers without a token, on
  the stack's internal network and on the engine's published port, which `RHYTHM_BIND` decides: this computer only by
  default, every address of the host with `0.0.0.0` on a LAN install ([configuration.md](configuration.md)). No
  exposure recipe routes it.

| State | Means |
|---|---|
| `on` | loaded and working |
| `off` | turned off in `rhythm.yaml` (`adapters.embeddings.enabled: false`, or the titles adapter not enabled) |
| `starting` | the engine has just started and has not loaded the model yet |
| `downloading` | the model's files are being fetched once, from the pinned commit, into the engine's data volume |
| `download-failed` | they could not be fetched; the engine tries again at its next start. Or give it a folder that holds them (`RHYTHM_MODEL_DIR` for MiniLM) |
| `unverified` | files are there but do not match their pinned checksums, so the model stays off |
| `wrong-model` | an embeddings model the engine was set up for is not the one `adapters.embeddings.model` names, so it stays off rather than tag vectors with the wrong model's name (a safeguard); restart the engine, which sets it up from the settings |
| `failing` | the titles model: the newest map rebuild that asked it for titles stopped on a failure |

For an embeddings model, the index shows the passages stored under its id, whether the index job covers it
(`adapters.embeddings.backfill`, `sources` or `shadow`; an existing install has none of them), the rows it still has to
do, and a pause: a round in which nothing could be indexed pauses that model's job for an hour.

## When titles fail

A failure stops the titles model for the rest of that rebuild: the stars it had not reached keep their excerpts, and
the next rebuild tries again. The engine log says so in one line (`Map rebuild: titles stopped after ...`), with the
failure's kind and counts, never a text or a title, and the app and `rhythm doctor` show the model as `failing` until a
rebuild titles without one, after a restart of the engine too: the failure's kind and time are kept beside the
rebuild's own state in the engine's data volume.

| Kind | Means | What to do |
|---|---|---|
| `timeout` | no answer within the adapter's 30 seconds; most often a model still loading after the titles service started | wait for the next rebuild, or keep the model loaded |
| `unreachable` | no connection to the titles service, or one that broke off | start the `titles` profile (`COMPOSE_PROFILES`) |
| `http-404` | the service does not have the model `adapters.titles.model` names | pull it ([adapters/titles.md](adapters/titles.md)) |
| `http-<status>` | another error status from the service | `docker compose ... logs titles` |
| `too-large`, `unreadable` | an answer over 64 KiB, or one that is not the service's JSON | check the service is Ollama |

Answers the engine cannot use as a title (a reasoning block, a link, markup, an empty line) are not failures: the
excerpt stays, and the log counts them.
