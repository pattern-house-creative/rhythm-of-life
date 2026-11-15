import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { appFiles, engineClasses, engineFilter, suite } from "./story-14.mjs";

// Story 14's suite (scripts/story-14.mjs) names tests that exist: a renamed or removed test class or file fails here,
// so the suite never quietly runs less than it says.
const root = fileURLToPath(new URL("..", import.meta.url));
const tests = join(root, "engine", "tests", "Knowledge.Tests");

// The engine test classes declared in the Knowledge.Tests namespace, as dotnet test's filter names them.
function declared() {
  const classes = new Set();
  for (const name of readdirSync(tests)) {
    if (!name.endsWith(".cs")) continue;
    const text = readFileSync(join(tests, name), "utf8");
    if (!/^namespace Knowledge\.Tests;/m.test(text)) continue;
    for (const m of text.matchAll(/\bpublic (?:sealed )?(?:partial )?class (\w+)/g)) classes.add(m[1]);
  }
  return classes;
}

test("every engine class and app file the suite names exists, each once", () => {
  const classes = declared();
  for (const name of engineClasses()) assert.ok(classes.has(name), `no engine test class ${name}`);
  for (const file of appFiles()) assert.ok(existsSync(join(root, "app", file)), `no app test file ${file}`);
  assert.equal(new Set(engineClasses()).size, engineClasses().length);
  assert.equal(new Set(appFiles()).size, appFiles().length);
  // The story's own tests lead it.
  assert.deepEqual([suite[0][1], suite[0][2]], [["StoryFourteenTests"], ["tests/story-14.test.tsx"]]);
});

test("the engine filter names each class by its full name, so one class never matches another", () => {
  const parts = engineFilter().split("|");
  assert.equal(parts.length, engineClasses().length);
  for (const [i, name] of engineClasses().entries()) assert.equal(parts[i], `FullyQualifiedName~Knowledge.Tests.${name}.`);
  // DatabaseTests is listed, MemoryScopeDatabaseTests is not: the dot after the class name keeps them apart.
  assert.ok(engineClasses().includes("DatabaseTests"));
  assert.ok(!parts.some((p) => "Knowledge.Tests.MemoryScopeDatabaseTests.Any".includes(p.slice("FullyQualifiedName~".length))));
});

test("--list prints the suite and an unknown option is refused", () => {
  const list = spawnSync(process.execPath, [join(root, "scripts", "story-14.mjs"), "--list"], { encoding: "utf8" });
  assert.equal(list.status, 0);
  for (const name of [...engineClasses(), ...appFiles()]) assert.ok(list.stdout.includes(name), name);
  const wrong = spawnSync(process.execPath, [join(root, "scripts", "story-14.mjs"), "--everything"], { encoding: "utf8" });
  assert.equal(wrong.status, 2);
  assert.match(wrong.stderr, /^usage: node scripts\/story-14\.mjs/);
  // --engine and --app together would run neither side: refused, never a silent pass.
  const both = spawnSync(process.execPath, [join(root, "scripts", "story-14.mjs"), "--engine", "--app"], { encoding: "utf8" });
  assert.equal(both.status, 2);
  assert.match(both.stderr, /^usage: node scripts\/story-14\.mjs/);
});

test("the app side never fetches vitest: npx runs it with --no-install", () => {
  const text = readFileSync(join(root, "scripts", "story-14.mjs"), "utf8");
  assert.match(text, /run\("npx", \["--no-install", "vitest", "run"/);
});
