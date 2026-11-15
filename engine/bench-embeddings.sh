#!/bin/sh
# engine/bench-embeddings.sh: the embedding benchmark (SB-P2-06), MiniLM against bge-small-en-v1.5 (and, when named,
# bge-base-en-v1.5) on the synthetic golden set in both load modes, each judged by the retrieval gate against the MiniLM
# locks (docs/dev/second-brain-parity/benchmarks.md). It reads each model's pinned files from the folder its variable
# names and never downloads one (D-041): without them it says so and starts nothing.
#
#   RHYTHM_MODEL_DIR=<MiniLM's files> RHYTHM_MODEL_DIR_BGE_SMALL=<bge-small's files> sh engine/bench-embeddings.sh
#   RHYTHM_EMBEDDING_BENCH_VARIANTS=minilm,bge-base,bge-base-instruction,bge-base-tuned RHYTHM_MODEL_DIR=<MiniLM's files> #     RHYTHM_MODEL_DIR_BGE_BASE=<bge-base's files> sh engine/bench-embeddings.sh
#
# The report goes to RHYTHM_PARITY_OUT (default: parity-report beside the test binaries): embedding-benchmark.json and
# .md, and each variant's gate report. RHYTHM_EMBEDDING_BENCH_VARIANTS (a comma list of variant names) picks the
# variants; without it, run 18's five (MiniLM's and bge-small's). MiniLM's files are always needed (its run must answer
# as the locks hold), bge-small's when a bge-small variant runs, bge-base's when a bge-base one does.
# It runs through engine/test-db.sh, so its throwaway Postgres container is removed afterwards; the container's name is
# RHYTHM_TEST_CONTAINER, by default embedding-bench-<pid>-<time>. Any other argument goes to dotnet test.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
variants=",${RHYTHM_EMBEDDING_BENCH_VARIANTS:-minilm,bge-small,bge-small-instruction,minilm-tuned,bge-small-tuned},"
need="minilm:model_quantized.onnx:${RHYTHM_MODEL_DIR:-}:RHYTHM_MODEL_DIR"
case "$variants" in *,bge-small,* | *,bge-small-*) need="$need bge-small:model_quantized.onnx:${RHYTHM_MODEL_DIR_BGE_SMALL:-}:RHYTHM_MODEL_DIR_BGE_SMALL" ;; esac
case "$variants" in *,bge-base,* | *,bge-base-*) need="$need bge-base:model.onnx:${RHYTHM_MODEL_DIR_BGE_BASE:-}:RHYTHM_MODEL_DIR_BGE_BASE" ;; esac
missing=""
for entry in $need; do
  name=${entry%%:*}
  rest=${entry#*:}
  weights=${rest%%:*}
  rest=${rest#*:}
  variable=${rest##*:}
  folder=${rest%:*}
  for file in "$weights" vocab.txt; do
    if [ -z "$folder" ] || [ ! -f "$folder/$file" ]; then
      missing="$missing $name ($variable)"
      break
    fi
  done
done
if [ -n "$missing" ]; then
  echo "NOT RUN: the embedding benchmark needs the pinned model files (each model's ONNX weights and vocab.txt) for:$missing; it never downloads a model (D-041)."
  exit 0
fi
export RHYTHM_EMBEDDING_BENCH=1
export RHYTHM_TEST_CONTAINER="${RHYTHM_TEST_CONTAINER:-embedding-bench-$$-$(date +%s)}"
exec sh "$here/test-db.sh" --filter EmbeddingBenchmarkRun "$@"
