import assert from "node:assert/strict";
import { test } from "node:test";

import { validateManifest } from "./import-manifest.mjs";

const sources = {
  "private-app": { description: "app", pinned: "1234567" },
  platform: { description: "platform", pinned: "snapshot-2026-10-29" },
};
const entry = (over = {}) => ({
  source: "private-app",
  commit: "1234567",
  sourcePath: "src/model.ts",
  targetPath: "app/src/model.ts",
  reason: "app contract types",
  changes: ["SourceId generalized to provider ids"],
  ...over,
});

test("a well-formed manifest passes", () => {
  assert.deepEqual(validateManifest({ sources, entries: [entry(), entry({ sourcePath: "src/x.ts", targetPath: "app/src/x.ts" })] }), []);
  assert.deepEqual(validateManifest({ sources, entries: [entry({ source: "platform", commit: "snapshot-2026-10-29", targetPath: "engine/Dockerfile" })] }), []);
});

test("only a source pinned as a snapshot takes a snapshot label", () => {
  assert.ok(validateManifest({ sources, entries: [entry({ commit: "snapshot-2026-10-29" })] }).some((p) => p.includes("commit must be")));
});

test("rejects private paths and unknown labels", () => {
  const problems = validateManifest({
    sources,
    entries: [entry({ source: "someones-laptop", sourcePath: "C:" + "/x/y.ts", targetPath: "../outside.ts" })],
  });
  assert.ok(problems.some((p) => p.includes("source must be one of")));
  assert.ok(problems.some((p) => p.includes("sourcePath")));
  assert.ok(problems.some((p) => p.includes("targetPath")));
});

test("rejects duplicates, unsorted entries, blocked targets and missing changes", () => {
  const dup = validateManifest({ sources, entries: [entry(), entry()] });
  assert.ok(dup.some((p) => p.includes("listed twice")));
  const unsorted = validateManifest({ sources, entries: [entry({ targetPath: "b.ts" }), entry({ targetPath: "a.ts" })] });
  assert.ok(unsorted.some((p) => p.includes("sorted")));
  const blocked = validateManifest({ sources, entries: [entry({ targetPath: "docs/latency-evidence.json" })] });
  assert.ok(blocked.some((p) => p.includes("blocked file")));
  const noChanges = validateManifest({ sources, entries: [entry({ changes: [] })] });
  assert.ok(noChanges.some((p) => p.includes("changes")));
});

test("checks that the target exists", () => {
  const problems = validateManifest({ sources, entries: [entry()] }, { exists: () => false });
  assert.ok(problems.some((p) => p.includes("does not exist")));
});
