# Second Brain parity: design notes

Rhythm of Life's memories, search and map relevance were built in one program of work: bringing Rhythm up to the
user-facing and AI-facing behaviour of the Second Brain memory service, and using that work to improve Rhythm's own
search, graph and insights. Parity meant the same supported workflows and guarantees, adapted to Rhythm's .NET engine,
Postgres with pgvector, the React PWA and its one MCP server. It never meant copying Second Brain's code: Second Brain
is MIT-licensed and was used as a pinned reference (commit `228cea49`, version 4.0.1), and what Rhythm adapted from its
behaviour is credited piece by piece in [`NOTICE`](../../../NOTICE).

This folder keeps the pages of that program that explain how the software works and why it was built that way: the
measurements, the rules they were judged by and what they chose. Everything in them was measured on synthetic data (the
fictional people `alex` and `sam`, the shared `Family` scope, `example.invalid` hosts).

## The pages

| Page | What it holds |
|---|---|
| [architecture.md](architecture.md) | the target architecture and how the phases depend on each other |
| [baseline.md](baseline.md) | the starting point: build and test commands as run, the synthetic corpus and the golden queries |
| [fixtures.md](fixtures.md) | the grown corpus, golden queries and labelled pairs, and the rules every label follows |
| [lexical-search.md](lexical-search.md) | how keyword search ranks its matches, and the measurements that chose it |
| [combined-retrieval.md](combined-retrieval.md) | how combined retrieval fuses words and meaning, the numbers it runs with and the gate's verdicts that chose them |
| [benchmarks.md](benchmarks.md) | the embedding benchmark (MiniLM against bge-small), each model's cost, and the similarity thresholds tuned per model |
| [relevance.md](relevance.md) | the map relevance ideas, each scored on the labelled pairs, and why none was kept |
| [titles-eval.md](titles-eval.md) | the local titles model: the labelled set, the rubric, every candidate's scores and the verdict |
| [comparison.md](comparison.md) | the comparison harness: the read-only Second Brain adapter against imported native memories on a synthetic brain |

The memory model itself is in [../memory.md](../memory.md), and what an operator turns on is in
[../../configuration.md](../../configuration.md).

## Ids you will meet

Comments, tests, migrations and these pages cite ids from the program's working notes: decisions (`D-123`), open
questions (`Q-045`), backlog items (`SB-P5-04`), acceptance stories (`story 14`) and ledger rows (`RET-08`, `SEC-12`).
Those notes (the decision log, the backlog, the progress log, the parity ledger and the open-questions log) were the
project's private working papers before its first release and are not part of this repository. Each place that cites
an id says what matters there in its own words; the id only marks that the point was decided on purpose. Older
migrations name those files by path, and stay as they were written: a migration's text is checksummed and never edited
([../migration-compat.md](../migration-compat.md)).
