#!/usr/bin/env node
// Story 14's regression suite (SB-P6-06): graph stars, existing editable
// dashboards, health and data views and their links to the original source keep working, on the synthetic corpus,
// at phone and desktop sizes where the app is involved. Run it for every release candidate:
//
//   node scripts/story-14.mjs           the engine's tests (sh engine/test-db.sh: a throwaway database, removed after)
//                                       and then the app's (vitest); exits non-zero when either fails
//   node scripts/story-14.mjs --list    prints the suite: each test class and file, and what it keeps
//   node scripts/story-14.mjs --engine  or --app: one side only (not both)
//
// The engine side needs Docker for its database (RHYTHM_TEST_CONTAINER names the container, as engine/test-db.sh
// says), the app side `npm --prefix app ci` first: without it the app side fails at once (npx --no-install) rather
// than fetch vitest. Neither reaches a network or downloads a model: the tests that need the local model skip without
// it (RHYTHM_MODEL_DIR). Fictional data only.
import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));

// [what it keeps, engine test classes (Knowledge.Tests.<name>), app test files (under app/)]
export const suite = [
  ["the story on the corpus: stars with their original links, a dashboard made before kept editable, health series per reader, scopes off and on",
    ["StoryFourteenTests"], ["tests/story-14.test.tsx"]],
  ["graph stars: every map answer, per reader, the same with scopes off",
    ["MemoryScopeMapTests", "RelationshipDiscoveryTests", "CatalogTests", "GraphBudgetTests", "GraphBudgetDatabaseTests"],
    ["tests/real-graph-ui.test.tsx", "tests/explorer-panel.test.tsx", "tests/dynamic-graph.test.tsx", "tests/graph-structure.test.ts",
      "tests/pins.test.tsx", "tests/ambient-navigation.test.tsx", "tests/scene-navigation.test.tsx", "tests/touch-tap.test.tsx",
      "tests/catalog-provider.test.tsx"]],
  ["the sources stars stand for, and their originals",
    ["DriveAdapterTests", "NotesAdapterTests", "SecondBrainAdapterTests", "DocumentTextTests", "DocumentOriginalTests"],
    ["tests/document-viewer.test.tsx", "tests/insight-sources.test.tsx"]],
  ["dashboards: versions, layouts, feeds (native memories among them, SB-P6-03), edits, writes with Undo, corner cards and pins",
    ["DatabaseTests", "DashboardLayoutTests", "DashboardLayoutDatabaseTests", "DashboardMapTests", "DriveMemoryDatabaseTests", "DerivedScopeTests",
      "MemoryDashboardTests"],
    ["tests/dashboards.test.ts", "tests/dashboard-writes.test.tsx", "tests/dashboard-flow.test.tsx", "tests/dashboard-card-version.test.tsx",
      "tests/dashboard-panel.test.tsx", "tests/dashboard-layout.test.tsx", "tests/home-screen.test.tsx", "tests/home-grid.test.tsx"]],
  ["health and data views: glucose, Garmin, Nightscout and CSV series, and where each reading came from",
    ["GlucoseTests", "GlucoseSourceTests", "GlucoseDefaultTests", "InvestigationTests", "GarminNormalizeTests", "GarminExportTests",
      "GarminDatabaseTests", "NightscoutTests", "CsvAdapterTests"],
    ["tests/glucose-panel.test.tsx", "tests/additional-health.test.tsx", "tests/health-contract.test.tsx", "tests/readings.test.tsx",
      "tests/garmin-sync.test.tsx", "tests/body-panels.test.tsx"]],
  ["the memory screens and Home's cards at a phone's size and from the keyboard: focus, labels, motion (SB-P6-02)",
    [], ["tests/memory-phone.test.tsx", "tests/memory-phone-cards.test.tsx", "tests/memory-phone-css.test.ts"]],
];

export const engineClasses = () => suite.flatMap(([, engine]) => engine);
export const appFiles = () => suite.flatMap(([, , app]) => app);
// One test class each, by its full name: Knowledge.Tests.DatabaseTests. does not match MemoryScopeDatabaseTests.
export const engineFilter = () => engineClasses().map((name) => `FullyQualifiedName~Knowledge.Tests.${name}.`).join("|");

// On Windows the commands are .cmd files, found through a shell, which then needs the filter quoted.
const shell = process.platform === "win32";
function run(command, args, cwd) {
  return spawnSync(command, args, { cwd, stdio: "inherit", shell }).status === 0;
}
const same = (a, b) => (shell ? a.toLowerCase() === b.toLowerCase() : a === b);

if (process.argv[1] && same(resolve(fileURLToPath(import.meta.url)), resolve(process.argv[1]))) {
  const args = process.argv.slice(2);
  const unknown = args.filter((a) => !["--list", "--engine", "--app"].includes(a));
  const usage = "usage: node scripts/story-14.mjs [--list | --engine | --app]";
  if (unknown.length) {
    console.error(`${usage}  (unknown: ${unknown.join(" ")})`);
    process.exit(2);
  }
  // Each of --engine and --app is that side alone: both together would run neither.
  if (args.includes("--engine") && args.includes("--app")) {
    console.error(`${usage}  (--engine and --app each run one side only; give neither to run both)`);
    process.exit(2);
  }
  if (args.includes("--list")) {
    for (const [what, engine, app] of suite) {
      console.log(`${what}\n  engine: ${engine.join(", ")}\n  app: ${app.join(", ")}`);
    }
    process.exit(0);
  }
  const engine = !args.includes("--app"), app = !args.includes("--engine");
  const results = [];
  if (engine) results.push(["engine", run("sh", ["engine/test-db.sh", "--filter", shell ? `"${engineFilter()}"` : engineFilter()], root)]);
  if (app) results.push(["app", run("npx", ["--no-install", "vitest", "run", ...appFiles()], `${root}app`)]);
  for (const [side, passed] of results) console.log(`story 14, ${side}: ${passed ? "passed" : "FAILED"}`);
  process.exit(results.length > 0 && results.every(([, passed]) => passed) ? 0 : 1);
}
