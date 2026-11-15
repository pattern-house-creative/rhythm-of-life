#!/usr/bin/env node
// Writes docs/licenses/gradle.json: the licence each package of the Android launcher declares in its POM.
//
//   node scripts/collect-gradle-licenses.mjs
//
// The packages are the ones in apps/android-launcher/launcher/dependencies.txt, which Gradle writes
// (./gradlew :launcher:writeDependencyList, in apps/android-launcher). Each POM is read from Google's Maven
// repository or from Maven Central (public network reads); a POM that declares no licence takes its parent's.
// A licence this script does not recognise is written as it is declared, with "license": null, and the run
// fails: classify it here before the inventory is used. scripts/dependency-inventory.mjs then checks the
// inventory against dependencies.txt without the network. No dependencies: Node 22 or later.
import { readFileSync, writeFileSync } from "node:fs";

const root = new URL("../", import.meta.url);
const list = "apps/android-launcher/launcher/dependencies.txt";
const repositories = ["https://dl.google.com/android/maven2", "https://repo1.maven.org/maven2"];

// What a POM calls its licence, read as an SPDX identifier.
export function spdx(name = "", url = "") {
  const text = `${name} ${url}`.toLowerCase();
  if (/apache/.test(text) && /2\.0/.test(text)) return "Apache-2.0";
  if (/eclipse public license/.test(text) && /1\.0|epl-v10/.test(text)) return "EPL-1.0";
  if (/bsd/.test(text) && /3-clause|new bsd|bsd-3|bsd license 3/.test(text)) return "BSD-3-Clause";
  if (/\bmit\b/.test(text)) return "MIT";
  return null;
}

const tag = (xml, name) => xml.match(new RegExp(`<${name}>\\s*([^<]*?)\\s*</${name}>`))?.[1] ?? null;
const block = (xml, name) => xml.match(new RegExp(`<${name}>([\\s\\S]*?)</${name}>`))?.[1] ?? null;

async function pom(group, artifact, version) {
  const path = `${group.replaceAll(".", "/")}/${artifact}/${version}/${artifact}-${version}.pom`;
  for (const repository of repositories) {
    const response = await fetch(`${repository}/${path}`);
    if (response.ok) return { url: `${repository}/${path}`, xml: await response.text() };
  }
  throw new Error(`No POM found for ${group}:${artifact}:${version}`);
}

// The licences a POM declares, or its nearest parent's.
async function licences(group, artifact, version, depth = 0) {
  const { url, xml } = await pom(group, artifact, version);
  // Leave the parent and the dependencies out before looking for this POM's own licences.
  const own = xml.replace(/<parent>[\s\S]*?<\/parent>/, "").replace(/<dependencies>[\s\S]*?<\/dependencies>/g, "");
  const declared = [...(block(own, "licenses") ?? "").matchAll(/<license>([\s\S]*?)<\/license>/g)]
    .map((m) => ({ name: tag(m[1], "name"), url: tag(m[1], "url") }));
  if (declared.length) return { pom: url, declared };
  const parent = block(xml, "parent");
  if (!parent || depth > 4) return { pom: url, declared: [] };
  return licences(tag(parent, "groupId"), tag(parent, "artifactId"), tag(parent, "version"), depth + 1);
}

const rows = readFileSync(new URL(list, root), "utf8").split(/\r?\n/).filter(Boolean).map((line) => {
  const [role, id] = line.split(" ");
  const [group, artifact, version] = id.split(":");
  return { role, group, artifact, version };
});
const packages = [];
for (const row of rows) {
  const found = await licences(row.group, row.artifact, row.version);
  const ids = [...new Set(found.declared.map((l) => spdx(l.name ?? "", l.url ?? "")))];
  packages.push({
    name: `${row.group}:${row.artifact}`,
    version: row.version,
    role: row.role,
    license: found.declared.length && !ids.includes(null) ? ids.join(" AND ") : null,
    declared: found.declared,
    pom: found.pom,
  });
}
writeFileSync(new URL("docs/licenses/gradle.json", root), JSON.stringify({ schemaVersion: 1, list, packages }, null, 2) + "\n");
const unknown = packages.filter((p) => !p.license);
const counts = Object.entries(Object.groupBy(packages, (p) => p.license ?? "unclassified")).map(([k, v]) => `${v.length} ${k}`);
console.log(`Gradle licences: ${packages.length} packages (${counts.join(", ")}).`);
if (unknown.length) {
  console.error(`Unclassified: ${unknown.map((p) => `${p.name}:${p.version}`).join(", ")}`);
  process.exit(1);
}
