#!/usr/bin/env node
// Checks scripts/import-manifest.json: the record of every file brought in from a private source.
//
//   node scripts/import-manifest.mjs --check    validate the manifest (CI and before commit)
//   node scripts/import-manifest.mjs --list     print entries as "target <- label@commit:source"
//
// Rules (docs/dev/importing.md): known source labels only; a commit id (or, for a source not under git,
// its snapshot label) on every entry; relative paths with no drive letters, no leading slash, no ".."; a target
// that exists in the repo, is not a blocked file and appears once; a reason and a list of changes;
// entries sorted by target. Exit codes: 0 ok, 1 problems, 2 usage error.

import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { FILE_RULES } from "./scrub-check.mjs";

const here = dirname(fileURLToPath(import.meta.url));

export function validateManifest(manifest, { exists = () => true } = {}) {
  const problems = [];
  if (typeof manifest !== "object" || manifest === null) return ["manifest is not an object"];
  const sources = manifest.sources ?? {};
  const labels = Object.keys(sources);
  for (const [label, s] of Object.entries(sources)) {
    if (!/^[a-z][a-z0-9-]+$/.test(label)) problems.push(`source label "${label}" is not a plain label`);
    if (!s || typeof s.description !== "string" || !s.description) problems.push(`source ${label}: description missing`);
    if (!s || typeof s.pinned !== "string" || !s.pinned) problems.push(`source ${label}: pinned commit missing`);
  }
  if (!Array.isArray(manifest.entries)) return [...problems, "entries is not an array"];

  const badPath = (p) =>
    typeof p !== "string" || !p || /^[A-Za-z]:/.test(p) || p.startsWith("/") || p.includes("\\") || p.split("/").includes("..") || /\s$/.test(p);
  const seen = new Set();
  let previous = "";
  manifest.entries.forEach((e, i) => {
    const at = `entry ${i + 1}${e && e.targetPath ? ` (${e.targetPath})` : ""}`;
    if (!e || typeof e !== "object") {
      problems.push(`${at}: not an object`);
      return;
    }
    const allowedKeys = ["source", "commit", "sourcePath", "targetPath", "reason", "changes", "notes"];
    for (const k of Object.keys(e)) if (!allowedKeys.includes(k)) problems.push(`${at}: unknown field "${k}"`);
    if (!labels.includes(e.source)) problems.push(`${at}: source must be one of ${labels.join(", ")}`);
    // A source not under git (the platform folder, the harness notes) is pinned as a dated snapshot instead.
    const snapshot = /^snapshot-\d{4}-\d{2}-\d{2}$/;
    const commitOk =
      typeof e.commit === "string" &&
      (/^[0-9a-f]{7,40}$/.test(e.commit) || (snapshot.test(sources[e.source]?.pinned ?? "") && snapshot.test(e.commit)));
    if (!commitOk) problems.push(`${at}: commit must be a commit id (or snapshot-YYYY-MM-DD for a snapshot source)`);
    if (badPath(e.sourcePath)) problems.push(`${at}: sourcePath must be a relative path inside the source`);
    if (badPath(e.targetPath)) problems.push(`${at}: targetPath must be a relative repo path`);
    else {
      if (seen.has(e.targetPath)) problems.push(`${at}: targetPath listed twice`);
      seen.add(e.targetPath);
      if (!exists(e.targetPath)) problems.push(`${at}: targetPath does not exist in the repo`);
      for (const rule of FILE_RULES) if (rule.test(e.targetPath)) problems.push(`${at}: targetPath is a blocked file (${rule.id})`);
      if (e.targetPath < previous) problems.push(`${at}: entries must be sorted by targetPath`);
      previous = e.targetPath;
    }
    if (typeof e.reason !== "string" || e.reason.trim().length < 8) problems.push(`${at}: reason missing or too short`);
    if (!Array.isArray(e.changes) || e.changes.length === 0 || e.changes.some((c) => typeof c !== "string" || !c.trim())) {
      problems.push(`${at}: changes must list what was changed (use ["none: copied as is"] when nothing was)`);
    }
  });
  return problems;
}

function main(argv) {
  const mode = argv[0] ?? "--check";
  const root = resolve(here, "..");
  const file = join(here, "import-manifest.json");
  let manifest;
  try {
    manifest = JSON.parse(readFileSync(file, "utf8"));
  } catch (e) {
    console.error(`import-manifest: cannot read ${file}: ${e.message}`);
    return 1;
  }
  if (mode === "--list") {
    for (const e of manifest.entries ?? []) console.log(`${e.targetPath} <- ${e.source}@${e.commit}:${e.sourcePath}`);
    return 0;
  }
  if (mode !== "--check") {
    console.error("usage: node scripts/import-manifest.mjs [--check | --list]");
    return 2;
  }
  const problems = validateManifest(manifest, { exists: (p) => existsSync(join(root, p)) });
  if (problems.length) {
    for (const p of problems) console.error(`import-manifest: ${p}`);
    return 1;
  }
  console.log(`import-manifest: ok (${manifest.entries.length} entries)`);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
