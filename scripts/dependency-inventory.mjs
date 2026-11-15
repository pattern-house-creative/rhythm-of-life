#!/usr/bin/env node
// No registry access. Compare the inventories with the committed lockfiles and
// verify the original legal files before packaging a release.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";

const root = new URL("../", import.meta.url);
const read = (file) => readFileSync(new URL(file, root));
const json = (file) => JSON.parse(read(file));
const digest = (file) => createHash("sha256").update(read(file)).digest("hex");
const write = process.argv.includes("--write");
const lock = json("app/package-lock.json");
const direct = new Set(
  Object.keys({
    ...lock.packages[""].dependencies,
    ...lock.packages[""].devDependencies,
  }),
);
const npm = {
  schemaVersion: 1,
  lockfile: "app/package-lock.json",
  packages: Object.entries(lock.packages)
    .filter(([p]) => p)
    .map(([p, m]) => {
      const name = m.name || p.split("node_modules/").at(-1);
      return {
        path: p,
        name,
        version: m.version,
        license: m.license || null,
        direct: direct.has(name) && p === `node_modules/${name}`,
        dev: Boolean(m.dev),
        optional: Boolean(m.optional),
        resolved: m.resolved || null,
        integrity: m.integrity || null,
      };
    }),
};
for (const p of npm.packages)
  assert.ok(p.license, `Missing npm licence metadata: ${p.path}`);
if (write)
  writeFileSync(
    new URL("docs/licenses/npm.json", root),
    JSON.stringify(npm, null, 2) + "\n",
  );
else
  assert.deepEqual(
    json("docs/licenses/npm.json"),
    npm,
    "npm inventory needs regeneration (--write)",
  );

// The app's Docker context is app/; keep the project's legal documents available
// there as exact copies. The build emits them alongside the browser assets.
for (const file of ["LICENSE", "NOTICE"]) {
  if (write) writeFileSync(new URL(`app/${file}`, root), read(file));
  else
    assert.ok(
      read(file).equals(read(`app/${file}`)),
      `app/${file} needs regeneration (--write)`,
    );
}

const expected = new Map();
for (const [role, file] of [
  ["runtime", "engine/src/Core/packages.lock.json"],
  ["test", "engine/tests/Knowledge.Tests/packages.lock.json"],
]) {
  for (const packages of Object.values(json(file).dependencies))
    for (const [name, m] of Object.entries(packages)) {
      if (m.type === "Project") continue;
      const key = `${name}@${m.resolved}`;
      const entry = expected.get(key) || {
        name,
        version: m.resolved,
        contentHash: m.contentHash,
        roles: [],
      };
      if (!entry.roles.includes(role)) entry.roles.push(role);
      expected.set(key, entry);
    }
}
const nuget = json("docs/licenses/nuget.json").packages;
assert.equal(
  nuget.length,
  expected.size,
  "NuGet inventory package count differs",
);
for (const p of nuget) {
  const entry = expected.get(`${p.name}@${p.version}`);
  assert.ok(entry, `Unrecognized NuGet package ${p.name}@${p.version}`);
  for (const key of ["name", "version", "contentHash", "roles"])
    assert.deepEqual(p[key], entry[key]);
  expected.delete(`${p.name}@${p.version}`);
  assert.ok(p.license, `Missing NuGet licence: ${p.name}`);
  if (p.roles.includes("runtime"))
    assert.ok(p.notices.length, `Missing runtime notices: ${p.name}`);
  for (const n of p.notices)
    assert.equal(
      digest(`docs/licenses/${n.file}`),
      n.sha256,
      `Changed original legal file ${n.file}`,
    );
}
assert.equal(expected.size, 0);

const source =
  read("engine/src/Core/EmbeddingModels.cs").toString() +
  read("engine/src/Core/Rerankers.cs").toString();
for (const model of json("docs/licenses/models.json").models) {
  const escaped = model.repository.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  assert.match(
    source,
    new RegExp(`"${escaped}"\\s*,\\s*"${model.revision}"`),
    `Changed model pin: ${model.name}`,
  );
  assert.ok(
    model.license || model.licenseStatus,
    `Unclassified model: ${model.name}`,
  );
}
// The Android launcher: the inventory names exactly the packages Gradle listed in dependencies.txt
// (./gradlew :launcher:writeDependencyList), each with its role and the licence its POM declares
// (node scripts/collect-gradle-licenses.mjs).
const listed = new Map(
  read("apps/android-launcher/launcher/dependencies.txt")
    .toString()
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => line.split(" ").reverse()),
);
const gradle = json("docs/licenses/gradle.json").packages;
assert.equal(
  gradle.length,
  listed.size,
  "Gradle inventory package count differs",
);
for (const p of gradle) {
  const id = `${p.name}:${p.version}`;
  assert.equal(
    p.role,
    listed.get(id),
    `Unrecognized Gradle package or role: ${id}`,
  );
  listed.delete(id);
  assert.ok(p.license, `Missing Gradle licence: ${id}`);
}
assert.equal(listed.size, 0);

console.log(
  `Licences: ${npm.packages.length} npm entries, ${nuget.length} NuGet packages, ${gradle.length} Gradle packages; legal files and model pins match.`,
);
console.log(
  "BGE-small export licensing remains unresolved; see docs/licenses/README.md.",
);
