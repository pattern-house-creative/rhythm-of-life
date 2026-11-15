// The NOTICE check (SB-P7-10, D-005): NOTICE credits Second Brain for every piece of Rhythm's code that says it adapts
// Second Brain's behaviour, and every path of this repository that NOTICE names is here. A piece's own comment says
// where it comes from, by an upstream source path (src/..., public/js/..., integrations/..., test/eval/..., as the
// ledger cites them) beside the words Second Brain or upstream; NOTICE names that file, or a folder holding it. Tests
// and fixtures are left out: they cite upstream to say what they compare, not what they adapt. No dependencies.
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
// NOTICE's lines joined, so a path or a phrase that wraps is read whole.
const notice = readFileSync(join(root, "NOTICE"), "utf8").replace(/[ \t]*\r?\n[ \t]*/g, " ");

// The code that ships: the engine, its migrations and backup image, the app and its server, the harness and the CLI.
const shipped = ["engine/src", "engine/migrations", "engine/backup", "app/src", "app/server", "harness", "cli"];
const code = /\.(?:cs|sql|ts|tsx|mjs|js|sh|py)$/;
const fixture = /(?:^|\/)(?:tests?|examples)\/|\.test\.[a-z]+$/;

// An upstream source path as the comments cite it: not part of a longer path (app/src/..., ../src/...).
export const upstreamPath = /(?<![\w./-])(?:src|public\/js|integrations|test\/eval)\/[a-z0-9_-]+(?:\/[a-z0-9_.-]+)*\.(?:ts|js)\b/;

// Whether NOTICE names a file: the file itself, or a folder of it below its area (engine/src/Adapters/Calendar/).
export function named(file, text = notice) {
  if (text.includes(file)) return true;
  const parts = file.split("/");
  for (let i = 3; i < parts.length; i++) {
    const folder = parts.slice(0, i).join("/") + "/";
    if ([" ", ")", ":", ","].some((after) => text.includes(folder + after))) return true;
  }
  return false;
}

export function adapting(files, read) {
  return files.filter((f) => code.test(f) && !fixture.test(f)).filter((f) => {
    const text = read(f);
    return upstreamPath.test(text) && /Second Brain|upstream/i.test(text);
  });
}

const tracked = () => execFileSync("git", ["ls-files", "-z", "--", ...shipped], { cwd: root, encoding: "utf8" }).split("\0").filter(Boolean);
const read = (f) => readFileSync(join(root, f), "utf8");

test("every piece of code that says it adapts Second Brain is credited in NOTICE", () => {
  const credited = adapting(tracked(), read);
  assert.ok(credited.length >= 40, `the rule finds the adapted pieces (found ${credited.length})`);
  const missing = credited.filter((f) => !named(f));
  assert.deepEqual(missing, [], `credit these in NOTICE (D-005): ${missing.join(", ")}`);
});

test("every repository path NOTICE names is here", () => {
  const paths = [...new Set(notice.match(/(?<![\w./-])(?:engine|app|harness|docs|scripts|cli|contracts|deploy)\/[A-Za-z0-9_./*-]+/g) ?? [])]
    .map((p) => p.replace(/[.,;:)]+$/, ""));
  assert.ok(paths.length >= 60, `NOTICE names its pieces by path (found ${paths.length})`);
  const gone = paths.filter((p) => {
    if (!p.includes("*")) return !existsSync(join(root, p));
    const [folder, pattern] = [dirname(p), p.slice(dirname(p).length + 1)];
    const like = new RegExp("^" + pattern.replace(/[.]/g, "\\.").replace(/\*/g, "[^/]*") + "$");
    return !execFileSync("git", ["ls-files", "--", folder], { cwd: root, encoding: "utf8" }).split("\n")
      .some((f) => like.test(f.slice(folder.length + 1)));
  });
  assert.deepEqual(gone, [], `NOTICE names paths that are not here: ${gone.join(", ")}`);
});

test("NOTICE keeps Second Brain's MIT copyright line and the pinned commit", () => {
  assert.match(notice, /used under the MIT License/);
  assert.match(notice, /Copyright \(c\) 2026 Rahil Pirani/);
  assert.match(notice, /read at commit 228cea49, version 4\.0\.1/);
  assert.match(notice, /No Second Brain file is copied/);
  // The passages that do follow upstream's wording are said to, and under which licence.
  assert.match(notice, /follow Second Brain's own wording .* used under the MIT License above/);
});

test("the rule tells a credit from a path of Rhythm's own", () => {
  assert.ok(upstreamPath.test("after Second Brain's registry (src/projects/registry.ts)"));
  assert.ok(!upstreamPath.test('import { x } from "../src/validation.js"'));
  assert.ok(!upstreamPath.test("the app's types (app/src/model.ts)"));
  assert.ok(named("engine/src/Adapters/Calendar/IcsFeed.cs", "- The calendar adapter (engine/src/Adapters/Calendar/): a link"));
  assert.ok(!named("engine/src/Core/Other.cs", "- Keyword search (engine/src/Core/LexicalSearch.cs): words"));
  assert.deepEqual(adapting(["engine/src/A.cs", "engine/src/B.cs", "app/tests/C.test.ts"], (f) => ({
    "engine/src/A.cs": "// Adapted from Second Brain's share (src/capture/share.ts)",
    "engine/src/B.cs": "// Rhythm's own (app/src/model.ts)",
    "app/tests/C.test.ts": "// as upstream does (src/capture/share.ts)",
  })[f]), ["engine/src/A.cs"]);
});
