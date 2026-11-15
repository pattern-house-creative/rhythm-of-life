# Local titles

Gives short titles to stars whose title is still the first words of their text (a note or memory with no heading of
its own), using a language model that runs on your own machine. The model reads the star's whole text, not only its
first words ([What the model is sent](#what-the-model-is-sent)). With it off, those stars keep their excerpts.

Off by default. It needs the `titles` compose profile: an [Ollama](https://ollama.com) server (MIT licence) on the
stack's internal network, with no published port and no internet access.

## What you need

- No account. A model download, once: about 2.5 GB for the suggested model.
- Memory: the server is limited to 4 GB by default (`RHYTHM_TITLES_MEMORY` in `.env`); a 4-billion-parameter model
  at 4-bit quantization only just fits on a CPU (3.71 to 3.72 GB at its peak, measured on a development machine's
  CPU, [below](#what-was-measured)). On the CPU titling is slow but happens in the background. The compose file
  does not wire a GPU; add a `deploy.resources.reservations.devices` entry in your own override if you have one. With
  a GPU the model's weights sit in the GPU's memory instead; how much of the server's own memory is then in use was
  not measured.

## The model

There is no default model: you choose one and name it in `rhythm.yaml`. A suggestion:

| Model | Size | Licence |
|---|---|---|
| `qwen3:4b-instruct-2507-q4_K_M` | about 2.5 GB | Apache-2.0 (its model card on Hugging Face and the Ollama library, checked October 2026) |

Prefer an instruct (non-thinking) model: a reply that starts with a reasoning block is discarded and the excerpt
stays. Check the licence of any model you choose before you use it.

Before you turn it on, know what was measured ([below](#what-was-measured)): given the star's whole text, this model
wrote better titles than the excerpt where the excerpt names nothing, and slightly worse ones where it already names
the subject. Its first load can take longer than the adapter's 30 s timeout, after which titling stops until the next
rebuild.

## What the model is sent

For a star whose title is still its excerpt, the model is sent the star's whole text, as the star shows it when you
open it, cut at 6,000 characters (D-255 in the program's decision log):

| Star | What the model is sent |
|---|---|
| a Rhythm memory | its text, for every memory that is a star: confirmed or a draft, and a fact a newer memory replaced (with `memory.tools` on). A memory that is no star at all (held for review, marked wrong, past its end, in the trash) sends nothing |
| a note ([notes](notes.md)) | the text its star opens, front matter left out. Only a note whose title is its excerpt is titled: one with a title, a heading or a file name of its own keeps it and sends nothing |
| a Second Brain memory ([second-brain](second-brain.md)) | its text as the service lists it, for the memories that are stars (never a draft, quarantined, retracted or no longer current one) |
| any other star whose title is its excerpt | the excerpt, as before |

With `memory.scopes` on, a star that no reader is served sends nothing. The prompt is exactly the one the evaluation
measured: the instruction "You title personal notes. Reply with only a title for the note: at most 8 words, in the
note's own language, with no quotes, labels or final period.", then `Note:\n<text>\n\nTitle:`, temperature 0, at most
32 tokens.

The text is held in the engine's memory for that one rebuild, beside the star and never on it, and goes only to the
titles service on the stack's internal network. It is never stored, logged, put on a star or served: the map keeps the
cleaned title and a hash of what was sent, nothing else. A title is made again when the model or the text it is sent
changes, so on an install that already titled stars from their excerpts, the first rebuilds after this change make
those titles again from the whole text, up to 60 a rebuild; until a star is reached it shows its excerpt.

## What was measured

`qwen3:4b-instruct-2507-q4_K_M` on the evaluation's 40 fictional notes and memories, given each one's whole text as
the engine now sends it (D-254,
[titles-eval.md](../dev/second-brain-parity/titles-eval.md#re-test-under-the-successor-rule-d-250)):

| | The excerpt | The model, from the whole text |
|---|---|---|
| the 20 items whose excerpt names nothing ("Note to self", "TODO", a list's first item) | 3.00 of 4 | 3.95 (+0.95, interval +0.85 to +1.00) |
| the 20 items whose excerpt names the subject | 4.00 | 3.75 (−0.25, interval −0.50 to −0.05) |
| a private detail from beyond the first line in a title | none | none of the 26 items' details |
| failed or empty titles | - | 0 of 240 calls |
| time per title | - | p50 0.41 s, p95 0.67 s |
| first load on a fresh server | - | 14.0 s (the adapter's timeout is 30 s) |
| the server's peak memory, on a CPU | - | 3.71 GB of the 4 GB limit |

That passes the successor rule proposed for this model (D-250): at least 3.0, and 0.5 above the excerpt, where the
excerpt names nothing, and at most 0.25 below it elsewhere. It passes that guard **exactly on its line**: where the
excerpt already named the subject the model lost a point on 5 of the 20 items, a loss that is real, only small. The
rule was written after a first run on the same set, so this is not a blind test. Over all 40 items the older rule
(D-027) still says no (3.85 against 3.50, short of its 0.5 margin, and over its 3 GB memory budget). Given only the
excerpt, as the engine sent it before, every model tried scored below the excerpt itself (3.28 against 3.50 for this
one, D-058).

All of it was measured on a development machine's CPU (Docker Desktop, 32 CPUs, the server limited to 4 GB). A GPU
deployment was not measured: its speed, its cold start and the server's memory there are its own, so check your
machine's free memory before you leave it on.

## Turning it on

1. Download the model into the server's volume. This is the only step that reaches the internet, and it runs only
   when you run it:
   ```sh
   docker compose -f deploy/compose/compose.yaml run --rm titles-pull qwen3:4b-instruct-2507-q4_K_M
   ```
2. In `rhythm.yaml`:
   ```yaml
   adapters:
     titles:
       enabled: true
       provider: ollama
       model: qwen3:4b-instruct-2507-q4_K_M
       url: http://titles:11434/
   ```
3. Start the server and restart the engine: add `titles` to `COMPOSE_PROFILES` in `.env` (or pass
   `--profile titles`), then `docker compose -f deploy/compose/compose.yaml up -d` and
   `docker compose -f deploy/compose/compose.yaml up -d --force-recreate --no-deps engine` (Compose does not notice a changed `rhythm.yaml` by itself).

The next map rebuild titles up to 60 stars (within a minute), and each later rebuild continues. A title is kept
until the star's text or the model changes. To change models, pull the new one and change `model`.

## Limits

- Only stars whose title is their excerpt are titled; a note with a heading keeps its heading.
- The model sees one star's text at a time, at most its first 6,000 characters, and nothing else. Its answer is
  treated as untrusted text: only the first line is kept, cleaned of quotes and labels, at most 8 words and 80
  characters, and a reply with a link or markup is dropped. A title can name something from further down the text
  than the excerpt did; it is on that star only, served only to those who may open its text.
- A rebuild asks for at most 60 titles and spends at most 60 s on them. On a map so full that a rebuild leaves stars
  out (past its 5,000 records), a star left out may have been asked first; its title is then not kept.
- `url` must name a compose service (plain `http`, no dots in the host): an Ollama on another computer or on the
  host is refused, so a star's text never leaves the stack (open question Q-085).
- If the server is down or the model is missing, the rebuild goes on with excerpts and tries again next time. The
  engine log names the failure's kind (`timeout`, `unreachable`, `http-404` for a model not pulled, ...), and the app's
  Sources & settings and `rhythm doctor` show the model as failing until a rebuild titles again, an engine restart
  in between included ([../models.md](../models.md#when-titles-fail)).
