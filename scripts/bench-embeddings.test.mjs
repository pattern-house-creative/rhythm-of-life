import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// engine/bench-embeddings.sh, the embedding benchmark's script (SB-P2-06): without both models' pinned files it says
// so and starts nothing, since a benchmark never downloads a model (D-041); with them it hands the run to
// engine/test-db.sh, here a stand-in that records what it was given, never a container.
const root = fileURLToPath(new URL("..", import.meta.url));
const sh = spawnSync("sh", ["-c", "exit 0"], { encoding: "utf8" }).status === 0;

function checkout(t) {
  const dir = mkdtempSync(join(tmpdir(), "rhythm-bench-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, "engine"));
  copyFileSync(join(root, "engine", "bench-embeddings.sh"), join(dir, "engine", "bench-embeddings.sh"));
  writeFileSync(
    join(dir, "engine", "test-db.sh"),
    'printf "%s\\n" "$*" "$RHYTHM_EMBEDDING_BENCH" "$RHYTHM_TEST_CONTAINER" > "$(dirname "$0")/handed.txt"\n',
  );
  for (const model of ["minilm", "bge-small", "bge-base"]) mkdirSync(join(dir, model));
  return dir;
}

function files(dir, model) {
  for (const file of [model === "bge-base" ? "model.onnx" : "model_quantized.onnx", "vocab.txt"]) writeFileSync(join(dir, model, file), "stand-in");
}

function run(dir, env) {
  const clean = { ...process.env };
  for (const name of ["RHYTHM_MODEL_DIR", "RHYTHM_MODEL_DIR_BGE_SMALL", "RHYTHM_MODEL_DIR_BGE_BASE", "RHYTHM_TEST_CONTAINER", "RHYTHM_EMBEDDING_BENCH",
    "RHYTHM_EMBEDDING_BENCH_VARIANTS"]) delete clean[name];
  return spawnSync("sh", [join(dir, "engine", "bench-embeddings.sh")], { cwd: dir, encoding: "utf8", env: { ...clean, ...env } });
}

test("without the model files the benchmark says so and starts nothing", { skip: !sh }, (t) => {
  const dir = checkout(t);
  const none = run(dir, {});
  assert.equal(none.status, 0, none.stderr);
  assert.match(none.stdout, /^NOT RUN: .*minilm \(RHYTHM_MODEL_DIR\) bge-small \(RHYTHM_MODEL_DIR_BGE_SMALL\).*never downloads a model \(D-041\)/);
  assert.equal(existsSync(join(dir, "engine", "handed.txt")), false);

  // MiniLM's files alone are not enough: bge-small's are named, MiniLM's not.
  files(dir, "minilm");
  const one = run(dir, { RHYTHM_MODEL_DIR: join(dir, "minilm"), RHYTHM_MODEL_DIR_BGE_SMALL: join(dir, "bge-small") });
  assert.equal(one.status, 0, one.stderr);
  assert.match(one.stdout, /^NOT RUN: .*for: bge-small \(RHYTHM_MODEL_DIR_BGE_SMALL\);/);
  assert.doesNotMatch(one.stdout, /minilm/);
  assert.equal(existsSync(join(dir, "engine", "handed.txt")), false);
});

test("with both models' files it runs only the benchmark through test-db.sh, in a container outside the install's names", { skip: !sh }, (t) => {
  const dir = checkout(t);
  files(dir, "minilm");
  files(dir, "bge-small");
  const both = run(dir, { RHYTHM_MODEL_DIR: join(dir, "minilm"), RHYTHM_MODEL_DIR_BGE_SMALL: join(dir, "bge-small") });
  assert.equal(both.status, 0, both.stderr);
  const [args, opted, container] = readFileSync(join(dir, "engine", "handed.txt"), "utf8").split("\n");
  assert.equal(args, "--filter EmbeddingBenchmarkRun");
  assert.equal(opted, "1");
  assert.match(container, /^embedding-bench-\d+-\d+$/);

  const named = run(dir, { RHYTHM_MODEL_DIR: join(dir, "minilm"), RHYTHM_MODEL_DIR_BGE_SMALL: join(dir, "bge-small"), RHYTHM_TEST_CONTAINER: "bench-test" });
  assert.equal(named.status, 0, named.stderr);
  assert.equal(readFileSync(join(dir, "engine", "handed.txt"), "utf8").split("\n")[2], "bench-test");
});

test("bge-base's files are needed only when a bge-base variant runs, and bge-small's only when one of its does", { skip: !sh }, (t) => {
  const dir = checkout(t);
  files(dir, "minilm");
  files(dir, "bge-small");
  const folders = { RHYTHM_MODEL_DIR: join(dir, "minilm"), RHYTHM_MODEL_DIR_BGE_SMALL: join(dir, "bge-small"), RHYTHM_MODEL_DIR_BGE_BASE: join(dir, "bge-base") };
  const without = run(dir, { ...folders, RHYTHM_EMBEDDING_BENCH_VARIANTS: "minilm,bge-base,bge-base-tuned" });
  assert.equal(without.status, 0, without.stderr);
  assert.match(without.stdout, /^NOT RUN: .*for: bge-base \(RHYTHM_MODEL_DIR_BGE_BASE\);/);
  assert.doesNotMatch(without.stdout, /bge-small/);
  assert.equal(existsSync(join(dir, "engine", "handed.txt")), false);

  files(dir, "bge-base");
  rmSync(join(dir, "bge-small"), { recursive: true });
  const base = run(dir, { ...folders, RHYTHM_EMBEDDING_BENCH_VARIANTS: "minilm,bge-base,bge-base-tuned" });
  assert.equal(base.status, 0, base.stderr);
  assert.equal(readFileSync(join(dir, "engine", "handed.txt"), "utf8").split("\n")[0], "--filter EmbeddingBenchmarkRun");
});
