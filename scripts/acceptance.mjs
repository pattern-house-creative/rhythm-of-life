#!/usr/bin/env node
// The acceptance script (SB-P6-08): one command runs the tests of every
// acceptance story that has tests, on synthetic data, and prints a pass or fail table, one row per story.
//
//   RHYTHM_MODEL_DIR=<folder with the pinned MiniLM files> node scripts/acceptance.mjs [--stories 1,2]
//   node scripts/acceptance.mjs --list      each story: its tests, its sandbox steps and its backlog items still open
//
// What a run starts, all of it its own and removed after:
//   - the engine's tests through engine/test-db.sh, each run in a throwaway pgvector container named after the project
//     (RHYTHM_TEST_CONTAINER), with the model on and the opt-in retrieval evaluation and gate (RHYTHM_PARITY_EVAL=1);
//     the classes a story runs with memory scopes on run again with RHYTHM_PARITY_SCOPES=on;
//   - the app's tests (vitest, never fetched: npx --no-install, so `npm --prefix app ci` first) and the repo's script
//     tests a story names (node --test);
//   - the sandbox stacks, one after the other in a Compose project of their own (COMPOSE_PROJECT_NAME, default
//     rhythm-acceptance), its own image tag (RHYTHM_VERSION, default the project's name) and two free local ports the
//     script picks (or RHYTHM_APP_PORT and RHYTHM_MCP_PORT): cli/e2e.sh, the rhythm commands end to end, then
//     cli/lifecycle.sh, a fresh install's native memory lifecycle with Rhythm alone (SB-P3-16), then cli/journeys.sh, a
//     person's journeys in the real app with the synthetic corpus (SB-P6-04), then cli/chaos.sh, restores that go wrong
//     and the real restore after them (SB-P7-10), each once the one before is down with its volumes, and each with the
//     run's project named to it (stackEnv), which its own guard reads and passes to every Compose call (D-240). Each step that proves part of a story names the story (step "<what>" <story> ...), and the
//     table counts the steps that passed.
//
// It needs Docker and RHYTHM_MODEL_DIR: the stack's engine reads the model files mounted read-only and the gate
// compares against baselines locked with that model, so no model is ever downloaded (D-041). The stack is not offline:
// its image builds pull base images and restore packages (dotnet restore, npm ci), and story 17's upgrade steps build
// with --pull and pull a tag, so cli/e2e.sh needs the image and package registries. A story passes when every
// test class, file and step it names ran and none failed; "pass (part)" while backlog items that name it are still
// open, since its tests prove only what is built. FAIL when one failed, "not run" when a side it names ran nothing,
// "incomplete" when some of it ran (a class whose every test skipped, a step never reached). Exit 0 when every story
// passed or has no tests yet, 1 otherwise, 2 for a usage error or a missing model.
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { appFiles as storyFourteenApp, engineClasses as storyFourteenEngine } from "./story-14.mjs";

const root = fileURLToPath(new URL("..", import.meta.url));

// The program plan's eighteen stories (their wording is outside the repo, D-008; the labels are the program backlog's). For each:
// engine test classes (Knowledge.Tests, any namespace), those run again with memory scopes on, app test files (under
// app/), script test files (under scripts/), and what the tests cover today. Story 14 takes its suite from
// scripts/story-14.mjs (D-118). A story with none of them has no tests yet.
export const stories = [
  { story: 1, label: "a private fact stays private",
    covers: "with memory scopes on, every read path, the map, derived records, screens, viewer sessions, tokens and doctor (SB-P1-10); the memory store's reads and its author lock (SB-P3-01, SB-P3-02); a memory's history, tag counts, projects and map stars per reader (SB-P3-03, SB-P3-17, SB-P3-07, SB-P3-11); combined retrieval per reader, the explorer's combined search included (SB-P2-08, SB-P2-10); the evaluation's leak measure (D-127); an export per reader (SB-P3-14, D-130); the terminal's memory commands per person (SB-P3-19); a sandbox copy Alex keeps for herself, no reader's sandbox export holding another's private records, and a memory Alex remembers at the terminal that Sam never lists or recalls",
    engine: ["MemoryScopeTests", "MemoryScopeDatabaseTests", "MemoryScopeMoveTests", "MemoryScopeMapTests", "MemoryScopeCopyBudgetTests",
      "MemoryScopeStatisticsTests", "CatalogScopeBudgetTests", "PassageScopeTests", "DerivedScopeTests", "ScreenScopeTests", "ScreenBindingTests",
      "ViewerSessionScopeTests", "ViewerSessionDatabaseTests", "EngineLoginTests", "MemorySettingsTests", "MemorySettingsDatabaseTests",
      "MemoryStoreTests", "MemoryAuthorLockTests", "MemoryScopesOffLockTests", "RecallScopeTests", "MemoryHistoryTests", "MemoryTagTests",
      "MemoryProjectTests", "MemoryStarTests", "RecallStarsScopeTests", "MemoryArchiveTests", "MemoryCommandDatabaseTests"],
    scopesOn: ["ParityEvaluation"],
    app: ["tests/viewer-session.test.ts", "tests/viewer-off.test.ts", "tests/viewer-corners.test.tsx", "tests/screen-login.test.tsx"] },
  { story: 2, label: "one memory shared exactly",
    covers: "an in-place un-share narrowing every copy, moves between scopes, link shares per scope, source shares and their host commands; a memory's history visible from the share that brought it on (SB-P3-03); a sandbox copy saved for Family and a source share ended; the share between private and Family behind memory.share (SB-P3-10): its owner shares one memory exactly, its author or an admin takes it back to the author's scope, every reader who lost it served nothing of it from their next read on and the map's copies dropped, through the store and the share tool as an AI client; in a fresh install's sandbox stack with memory scopes and memory.share on, an AI client shares one private memory into Family, another person's client reads it with its author, recalls it and sees its history from the share on but may not take it back, and once its author takes it back reads and recalls nothing of it (SB-P3-16)",
    engine: ["MemoryScopeTests", "MemoryScopeMoveTests", "DerivedScopeTests", "GraphBudgetTests", "GraphBudgetDatabaseTests",
      "CatalogScopeBudgetTests", "MemorySettingsDatabaseTests", "MemoryHistoryTests", "MemoryShareTests", "MemoryShareToolTests",
      "MemoryShareToolDatabaseTests"],
    app: ["tests/documents-route.test.ts"] },
  { story: 3, label: "edits, trash and undo audited",
    covers: "the native memory tables: versions, events and trash (SB-P1-07); the store's checked writers: append, update and set status, each keeping a version and one audit row, with expected revisions, idempotency keys and the author lock (SB-P3-01, SB-P3-02); a memory's history, its 20 kept states and every audit row (SB-P3-03); the audit trails with channel and client, and the admin audit (SB-P3-04); the trash, its restore and purge, and undo with AI change groups (SB-P3-06, SB-P3-05); the lifecycle through the MCP memory tools behind memory.tools, each change recorded as an AI client's, a retried undo after a restore changing nothing, and a call never naming its channel (SB-P3-09); the terminal's writes recorded through its own login, which the database tells from an AI client's (SB-P3-19); the lifecycle through the app's Memories view behind memory.tools, each change recorded as the app's, delete for good only with the trash nonce and a confirmation, at phone and desktop sizes (SB-P3-12, SB-P3-13); in a fresh install's sandbox stack, an AI client and a person each capture, edit, undo, forget and restore a memory, a stale revision changing nothing and every change in its history through its own channel (SB-P3-16); a memory deleted for good from the terminal once the person types its id, behind memory.tools (SB-P3-22)",
    engine: ["MemoryTableTests", "MemoryMigrationTests", "MemoryInputTests", "MemoryStoreTests", "MemoryChangeTests", "MemoryAuthorLockTests",
      "MemoryHistoryTests", "MemoryAuditTests", "AuditClientTests", "AdminAuditTests", "MemoryTrashTests", "MemoryTrashPurgeTests",
      "MemoryUndoTests", "MemoryGroupKeyTests", "MemoryToolTests", "MemoryToolDatabaseTests", "MemoryToolStatelessTests", "TerminalLoginTests",
      "MemoryCommandDatabaseTests", "AppMemoryTests", "AppMemoryDatabaseTests", "PersonStepTests", "PersonStepDatabaseTests",
      "AppMemoryListTests", "AppMemoryListDatabaseTests", "AppMemoryQueueTests", "AppMemoryQueueDatabaseTests",
      "AppMemoryPlansTests", "AppMemoryPlansDatabaseTests", "AppMemoryPlansCalibrationTests"],
    app: ["tests/memories-route.test.ts", "tests/memory-client.test.ts", "tests/memory-view.test.tsx", "tests/memory-list-route.test.ts", "tests/memory-list-view.test.tsx",
      "tests/memory-queues-route.test.ts", "tests/memory-queues-view.test.tsx", "tests/memory-plans-route.test.ts", "tests/memory-plans-view.test.tsx"] },
  { story: 4, label: "exact, paraphrase and linked search",
    covers: "ranked keyword search, passages and paraphrases, combined retrieval with typed-link hops (SB-P2-08), served behind engine.recall as the recall tool with explain and a receipt (SB-P2-09) and as the explorer's combined search with why (SB-P2-10), the golden queries in both load modes and the gate against its locks; in a fresh install's sandbox stack a memory recalled first by its words and by its meaning alone, and found by the explorer's search (SB-P3-16)",
    engine: ["LexicalSearchTests", "LexicalSearchDatabaseTests", "DocumentPassageTests", "RecordPassageTests", "MemoryPassageTests",
      "PassageParaphraseTests", "RecallTests", "RecallDatabaseTests", "RecallToolTests", "RecallToolDatabaseTests", "RecallStarsTests",
      "RecallStarsDatabaseTests", "ParityGateTests", "ParityGateRun", "ParityEvaluation"],
    app: ["tests/recall-route.test.ts", "tests/recall-explorer.test.tsx"] },
  { story: 5, label: "keyword search without embeddings",
    covers: "model tags, the registry, the shadow index and backfill, model status, keyword search alone, the gate",
    engine: ["EmbeddingModelTests", "EmbeddingModelDatabaseTests", "EmbeddingRegistryTests", "EmbeddingBackfillTests", "EmbeddingBackfillLimitTests",
      "EmbeddingBackfillTargetTests", "ModelStatusTests", "ModelStatusDatabaseTests", "PassageSettingsTests", "LexicalSearchDatabaseTests",
      "ParityGateRun", "ParityEvaluation"],
    app: ["tests/model-status.test.tsx"] },
  { story: 6, label: "as-of answers, current facts",
    covers: "the validity columns of the memory tables (SB-P1-07); a fact replaced later no longer the current answer and kept in history in its own words, one memory replacing another only through a confirmed plan, canonical memories protected, and the retraction cascade (SB-P4-06, SB-P4-08); as-of recall, what was true then in its words of then with beliefs marked wrong since listed under it, and the scope and hold a memory had then (SB-P4-07)",
    engine: ["MemoryTableTests", "MemoryTruthTests", "MemoryTruthToolTests", "RecallAsOfTests"] },
  { story: 7, label: "Drive citations stay honest",
    covers: "Drive originals, their copies' source ids and modified times, and Second Brain references (SB-P1-08); native memories citing Drive documents against a fake Drive behind memory.tools (SB-P4-10): the source's id and the revision a check found, get saying at each read whether the document was edited, moved, renamed, trashed, deleted or became unreadable, or could not be checked, revalidate moving a citation and saying from what to what, never silently, the Drive sync's calls and rows as without citations, and the scope and hold rules on cited memories",
    engine: ["DriveAdapterTests", "DriveMemoryDatabaseTests", "DocumentOriginalTests", "SecondBrainAdapterTests", "MemoryCitationTests",
      "MemoryCitationScopeTests"],
    app: ["tests/document-viewer.test.tsx"] },
  { story: 8, label: "pasted instructions held",
    covers: "a coding agent's session hook (SB-P7-02) redacting before anything leaves, its gate, off until installed and refused while the endpoint is off; holds without the burst (SB-P3-18): the planted injections held on every write path and released only by a person, the golden injection queries no longer returning them, a held memory off the map; through the memory tools (SB-P3-09) a held memory's words never reach an AI client, a restore answers it held, and no AI client releases a hold; a person reviews a held memory whole and releases it in the app's Memories view (SB-P3-12, SB-P3-13); the review queue in the view, its words, tags and project read before several are released at the revisions read, and rhythm release at the terminal (SB-P3-22); a held project exported and imported held (SB-P3-23); the per-token write limit behind the burst, off until engine.writeLimit is on: past 300 writes in ten minutes a token's next write refused and nothing saved, reads, the app and the terminal never counted, and the corpus and the end-to-end runs far under it (SB-P7-12)",
    engine: ["SessionHookTests", "MemoryHoldNoticeTests", "MemoryScreenTests", "MemoryHoldTests", "MemoryHoldStoryTests", "MemoryHoldStoryModelTests", "MemoryHoldMapTests",
      "MemoryToolDatabaseTests", "AppMemoryDatabaseTests", "PersonStepDatabaseTests", "MemoryArchiveHeldProjectTests", "WriteLimitTests"],
    app: ["tests/memory-view.test.tsx"], scripts: ["hooks.test.mjs"] },
  { story: 9, label: "duplicates and contradictions reviewed",
    covers: "a save flagging a near copy of another memory of its scope, with its list and keep both (SB-P5-02); an offer of a possible contradiction, an AI client naming one and only a person settling it (SB-P5-03); the labelled corpus's copies and contradictions; a person's review queues in the app and at the terminal, each count its list's length (SB-P6-01); in a sandbox stack with the synthetic corpus, a person keeping both copies and settling two pairs, both standing and one replacing the other, in the real app (SB-P6-04)",
    engine: ["MemoryDuplicateTests", "MemoryDuplicateCorpusTests", "MemoryDuplicateToolTests", "MemoryContradictionTests", "MemoryContradictionSettledTests", "MemoryContradictionToolTests",
      "MemoryContradictionCorpusTests", "AppMemoryQueueTests", "AppMemoryQueueDatabaseTests"],
    app: ["tests/memory-queues-route.test.ts", "tests/memory-queues-view.test.tsx"] },
  { story: 10, label: "capsule slots, no guessing",
    covers: "capsules (SB-P3-08): fixed slots in a fixed order, canonical memories only, drafts, held, deprecated and expired ones left out, duplicate, missing and too-long slots reported, one scope per capsule, byte-stable text and its ETag, the tool and resources only with memory.capsules on; the project registry a project capsule names (SB-P3-07); through the memory tools (SB-P3-09) update's capsule tags and an undo of a slot refused, so only a person fills a slot: from the app's Memories view or with rhythm capsule, and the capsule serves it (SB-P3-22)",
    engine: ["CapsuleTextTests", "CapsuleDatabaseTests", "MemoryProjectInputTests", "MemoryProjectTests", "MemoryProjectScopesOffLockTests", "ProjectMapTests",
      "MemoryToolDatabaseTests", "PersonStepDatabaseTests"],
    app: ["tests/memory-view.test.tsx"] },
  { story: 11, label: "insight candidates, one stream",
    covers: "insights citing memories, records and documents, in one stream (SB-P5-07); no candidates yet",
    engine: ["InsightSourceTests"], app: ["tests/insight-sources.test.tsx"] },
  { story: 12, label: "one brief, accurate counts",
    covers: "counts and the tag vocabulary from a list's own predicates, each count its list's length (SB-P3-17); the stale queue's count the length of its list for every reader and scope, from one predicate, the staleness pass bounded and resumable from its cursor on the nightly runner, and still true confirming a memory without changing it (SB-P4-09); the brief's lists in the app, a count on the Home card the length of the list it opens, a person's word on each through the app's channel (SB-P6-01)",
    engine: ["MemoryTagTests", "MemoryStalenessTests", "MemoryStalenessResumeTests", "MemoryStaleToolTests", "AppMemoryPlansTests", "AppMemoryPlansDatabaseTests", "AppMemoryPlansCalibrationTests"],
    app: ["tests/memory-plans-route.test.ts", "tests/memory-plans-view.test.tsx"] },
  { story: 13, label: "digests never overwrite",
    covers: "digests (SB-P5-13): one eligibility rule that gives every reader of a scope the same list, never a held or out-of-scope memory; the client's digest saved with the list's basis as a new memory with a drawn_from link to each source in its scope, no source changing; a repeated or scheduled run, a save again and two saves at once writing one digest; the digest read writing nothing",
    engine: ["MemoryDigestKeyTests", "MemoryDigestTests", "MemoryDigestToolTests"] },
  { story: 14, label: "map, dashboards and health kept", covers: "story 14's suite, scripts/story-14.mjs (SB-P6-06, D-118); in a sandbox stack, with every memory setting off, with memory.tools on and with memory scopes on, a plugin's glucose reading on its tile and in its panel, a note's star and its text, a data star's readings, a document's original byte for byte, a dashboard with its feeds read and an insight's feedback, in the real app at a phone's and a desktop's size (SB-P6-04)",
    engine: storyFourteenEngine(), app: storyFourteenApp(), scripts: ["story-14.test.mjs"] },
  { story: 15, label: "restore keeps ids, history, scopes",
    covers: "a restore compared table by table, every token disabled (SB-P1-13); an export imported into a fresh install giving back its ids, versions, scopes and trash dates (SB-P3-14); a sandbox backup and restore, and a sandbox export round trip; a fresh install's export holding its memories as restored, with their earlier states, which an import adds nothing from (SB-P3-16); a held project's hold carried by the export (SB-P3-23); in a throwaway stack, restores that go wrong refused with the live install as it was, then the real restore twice, every old token refused and what the backup held back (SB-P7-10)",
    engine: ["MemoryRestoreTests", "MemoryArchiveTests", "MemoryArchiveHeldProjectTests"] },
  { story: 16, label: "importer dry run, no duplicates",
    covers: "the synthetic Second Brain export files an importer will read (SB-P8-02); Rhythm's own import, repeatable without duplicates, with an origin map, held staying held, only what its reader may write (SB-P3-14) and in the sandbox; no Second Brain importer yet",
    engine: ["SecondBrainExportTests", "MemoryArchiveTests", "ArchiveCommandTests"] },
  { story: 17, label: "settings survive an upgrade",
    covers: "settings defaults and refusals, the door, logs that never carry a secret, the upgrade wrappers; a sandbox upgrade and its rollback, with every setting off by default and again with them on, leaving the settings, ports, connectors turned off and tokens as they were and no secret in a log (SB-P7-11); the capture endpoint off by default and refused while off, its token's scope, quarantine and mirrors",
    engine: ["MemorySettingsTests", "ConfigTests", "CliTests", "PerimeterTests", "RequestLogTests", "CaptureSettingsTests", "CaptureDoorTests", "CaptureReadTests", "CaptureEndpointTests"], scripts: ["rhythm-upgrade.test.mjs"] },
  { story: 18, label: "Rhythm alone, without Second Brain",
    covers: "a fresh install through the rhythm commands: init, up, a token, a document saved and found, backup and restore; native memories captured, read, changed, undone, trashed and restored by an AI client through the memory tools behind memory.tools, and the memory instructions for AI clients naming only real tools (SB-P3-09, SB-P6-07), given by the engine when a client connects with memory.tools on (SB-P3-20); MCP clients on every protocol from 2024-11-05 to the newest, with a missing, revoked or wrong token, across restarts and with the old names (SB-P6-05); memories remembered, listed and recalled from the terminal (SB-P3-19), in the sandbox too; a person capturing and tidying memories in the app's Memories view (SB-P3-12, SB-P3-13), and saving the page they are on through the app's own page, with no token in the bookmark (SB-P7-03); the fresh-install lifecycle in a sandbox stack (SB-P3-16): Second Brain off and nothing of it anywhere, the memory tools, recall, the share, the instructions and the Memories view off by default, then with them on an AI client and a person each capture, recall, edit, undo, trash and restore a memory and the export holds them, and with memory scopes and memory.share on one memory is shared exactly between private and Family and taken back (SB-P3-10, Q-157); a person's journeys in the real app against a sandbox stack with the synthetic corpus, from saving and finding a memory through review, resolve, the trash and undo, with every memory setting off first, saying what still needs a terminal (SB-P6-04)",
    engine: ["CliTests", "CliDatabaseTests", "MemoryToolDatabaseTests", "MemoryInstructionsTests", "McpClientCompatibilityTests", "McpClientCompatibilityDatabaseTests",
      "MemoryCommandTests", "MemoryCommandDatabaseTests", "AppMemoryDatabaseTests"],
    app: ["tests/memory-view.test.tsx", "tests/page-capture.test.tsx"] },
];

// The sides of a story, in the table's order.
export const sides = ["engine", "app", "scripts", "stack"];

// Every engine test class by its name, with the namespace dotnet test's filter needs (Knowledge.Tests or
// Knowledge.Tests.Parity): one class in a file of several is found too.
export function engineClassIndex(dir = join(root, "engine", "tests", "Knowledge.Tests")) {
  const index = new Map();
  const walk = (folder) => {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      const path = join(folder, entry.name);
      if (entry.isDirectory()) { if (!["bin", "obj"].includes(entry.name)) walk(path); continue; }
      if (!entry.name.endsWith(".cs")) continue;
      const text = readFileSync(path, "utf8");
      const namespace = /^namespace (Knowledge\.Tests(?:\.\w+)*);/m.exec(text)?.[1];
      if (!namespace) continue;
      for (const m of text.matchAll(/\bpublic (?:sealed )?(?:partial )?class (\w+)/g)) index.set(m[1], `${namespace}.${m[1]}`);
    }
  };
  walk(dir);
  return index;
}

// dotnet test's filter for these classes, each by its full name and a dot, so DatabaseTests never matches
// MemoryScopeDatabaseTests.
export function engineFilter(names, index = engineClassIndex()) {
  return names.map((name) => {
    if (!index.has(name)) throw new Error(`no engine test class ${name}`);
    return `FullyQualifiedName~${index.get(name)}.`;
  }).join("|");
}

// The sandbox stacks, in the order a run starts them, each in the same project once the one before is down.
export const stackScripts = ["cli/e2e.sh", "cli/lifecycle.sh", "cli/journeys.sh", "cli/chaos.sh"];

// What every sandbox stack is started with: the shell's environment, and over it the run's own project, tag, ports and model
// folder by name, so no script falls back to one Compose would infer or to a project the shell had exported (D-240).
export function stackEnv(s, ports, base = process.env) {
  return { ...base, COMPOSE_PROJECT_NAME: s.project, RHYTHM_VERSION: s.version, RHYTHM_APP_PORT: String(ports[0]),
    RHYTHM_MCP_PORT: String(ports[1]), RHYTHM_MODEL_DIR: resolve(s.model) };
}

// The sandbox stacks' steps in order, each with the stories it names and the script it is in; or, given one script's
// text, that script's steps. A step's words are its own across both scripts (scripts/acceptance.test.mjs).
export function stackSteps(text, script) {
  if (text === undefined) return stackScripts.flatMap((name) => stackSteps(readFileSync(join(root, name), "utf8"), name));
  return [...text.matchAll(/^step "([^"]+)"((?: \d+)*)[ \t]*$/gm)]
    .map((m) => ({ what: m[1], stories: m[2].trim() ? m[2].trim().split(/ +/).map(Number) : [], script }));
}

// The backlog items still open for each story: a row whose Stories cell names the story by number (a row for "all"
// is the program's own, such as this script) and whose status is not done. A recurring item is never done, and
// proves no story by itself, so it is left out. The backlog is a working note kept outside this repository:
// RHYTHM_BACKLOG names its file for whoever has one, and without it no story has open items to show.
export function openItems(text = process.env.RHYTHM_BACKLOG ? readFileSync(process.env.RHYTHM_BACKLOG, "utf8") : "") {
  const open = new Map();
  let header = null;
  for (const line of text.split(/\r?\n/)) {
    if (!line.startsWith("|")) { header = null; continue; }
    const cells = line.split("|").slice(1, -1).map((c) => c.trim());
    if (!header) { header = cells; continue; }
    const id = cells[header.indexOf("ID")], told = cells[header.indexOf("Stories")], status = cells[header.indexOf("Status")];
    if (!id || !/^SB-/.test(id) || told === undefined || status === undefined) continue;
    if (/^done\b/.test(status) || /^\w+ \(recurring\)/.test(status)) continue;
    for (const n of told.match(/^\d+(?:, \d+)*$/) ? told.split(", ").map(Number) : []) {
      if (!open.has(n)) open.set(n, []);
      open.get(n).push(id);
    }
  }
  return open;
}

const tally = () => ({ ok: 0, fail: 0, skip: 0 });

// A TRX file's results, per full class name.
export function parseTrx(xml) {
  const classes = new Map(), byTest = new Map();
  for (const m of xml.matchAll(/<UnitTest\b[^>]*\bid="([^"]+)"[\s\S]*?<TestMethod\b[^>]*\bclassName="([^"]+)"/g)) byTest.set(m[1], m[2]);
  for (const m of xml.matchAll(/<UnitTestResult\b([^>]*)>/g)) {
    const testId = /\btestId="([^"]+)"/.exec(m[1])?.[1], outcome = /\boutcome="([^"]+)"/.exec(m[1])?.[1];
    const name = byTest.get(testId);
    if (!name) continue;
    if (!classes.has(name)) classes.set(name, tally());
    const t = classes.get(name);
    if (outcome === "Passed") t.ok++;
    else if (outcome === "NotExecuted") t.skip++;
    else t.fail++;
  }
  return classes;
}

// Vitest's JSON report, per file relative to app/ (tests/x.test.ts).
export function parseVitest(json, appRoot = join(root, "app")) {
  const files = new Map();
  for (const file of JSON.parse(json).testResults ?? []) {
    const name = relative(appRoot, file.name).split("\\").join("/");
    const t = tally();
    for (const a of file.assertionResults ?? []) {
      if (a.status === "passed") t.ok++;
      else if (a.status === "failed") t.fail++;
      else t.skip++;
    }
    // A file that fails to load has no assertions.
    if (file.status === "failed" && t.fail === 0) t.fail++;
    files.set(name, t);
  }
  return files;
}

// Which of one sandbox script's steps passed in its output: a step passed once the next one began (or the script said
// all passed); the step it was in when it stopped failed; the rest were not reached.
export function stackOutcome(steps, output, passedAll) {
  const begun = [];
  for (const line of output.split(/\r?\n/)) {
    const m = /^== (.+?)(?: {2}\[stor(?:y|ies) [\d, ]+\])?$/.exec(line);
    if (m) begun.push(m[1]);
  }
  const outcome = new Map(steps.map((s) => [s.what, "not reached"]));
  begun.forEach((what, i) => {
    if (!outcome.has(what)) return;
    outcome.set(what, i < begun.length - 1 || passedAll ? "passed" : "failed");
  });
  return outcome;
}

// One story's row from the sides' results. results.engine and results.scopesOn map full class names to tallies (null:
// that run gave no results), results.app and results.scripts map files to tallies, results.stack maps steps to outcomes.
export function storyRow(s, results, { steps = stackSteps(), open = openItems(), index = engineClassIndex() } = {}) {
  const cells = {};
  const sum = (names, map, key) => {
    if (!names.length) return undefined;
    if (!map) return { ok: 0, fail: 0, skip: 0, missing: names.length };
    const t = { ...tally(), missing: 0 };
    for (const name of names) {
      const r = map.get(key(name));
      if (!r) { t.missing++; continue; }
      t.ok += r.ok; t.fail += r.fail; t.skip += r.skip;
      // A class or file whose every test was skipped did not run.
      if (r.ok === 0 && r.fail === 0) t.missing++;
    }
    return t;
  };
  const full = (name) => index.get(name) ?? name;
  const engine = sum(s.engine ?? [], results.engine, full), scopesOn = sum(s.scopesOn ?? [], results.scopesOn, full);
  if (engine || scopesOn) {
    const parts = [engine, scopesOn].filter(Boolean);
    cells.engine = parts.reduce((a, b) => ({ ok: a.ok + b.ok, fail: a.fail + b.fail, skip: a.skip + b.skip, missing: a.missing + b.missing }));
  }
  const app = sum(s.app ?? [], results.app, (f) => f);
  if (app) cells.app = app;
  const scripts = sum(s.scripts ?? [], results.scripts, (f) => f);
  if (scripts) cells.scripts = scripts;
  const mine = steps.filter((step) => step.stories.includes(s.story));
  if (mine.length) {
    const t = { ...tally(), missing: 0 };
    for (const step of mine) {
      const o = results.stack?.get(step.what) ?? "not reached";
      if (o === "passed") t.ok++;
      else if (o === "failed") t.fail++;
      else t.missing++;
    }
    t.steps = mine.length;
    cells.stack = t;
  }
  const items = open.get(s.story) ?? [];
  const named = Object.values(cells);
  let result;
  if (!named.length) result = "no tests yet";
  else if (named.some((c) => c.fail > 0)) result = "FAIL";
  else if (named.some((c) => c.ok === 0)) result = "not run";
  else if (named.some((c) => c.missing > 0)) result = "incomplete";
  else result = items.length ? "pass (part)" : "pass";
  return { story: s.story, label: s.label, result, cells, open: items };
}

const cell = (c, side) => {
  if (!c) return "-";
  if (side === "stack") return `${c.ok}/${c.steps}`;
  const said = [`${c.ok}`];
  if (c.fail) said.push(`${c.fail} FAIL`);
  if (c.skip) said.push(`${c.skip} skip`);
  if (c.missing) said.push(`${c.missing} no result`);
  return said.join(", ");
};

// The table: one row per story, every side's count (tests passed, then any failed, skipped or without a result; for
// the stack, steps passed of the story's steps), its backlog items still open and its label.
export function table(rows) {
  const head = ["story", "result", "engine", "app", "scripts", "stack", "open", "label"];
  const lines = rows.map((r) => [String(r.story), r.result, ...sides.map((side) => cell(r.cells[side], side)), String(r.open.length), r.label]);
  const widths = head.map((h, i) => Math.max(h.length, ...lines.map((l) => l[i].length)));
  const row = (cells) => cells.map((c, i) => (i === cells.length - 1 ? c : c.padEnd(widths[i]))).join("  ").trimEnd();
  const count = (word) => rows.filter((r) => r.result === word).length;
  return [row(head), row(widths.map((w) => "-".repeat(w))), ...lines.map(row), "",
    `${rows.length} stories: ${count("pass")} pass, ${count("pass (part)")} pass (part), ${count("FAIL")} FAIL, ${count("not run")} not run, ${count("incomplete")} incomplete, ${count("no tests yet")} no tests yet`].join("\n");
}

// What --list prints: each story's tests, its sandbox steps and its open items.
export function listing(selected = stories, { steps = stackSteps(), open = openItems() } = {}) {
  const out = [];
  for (const s of selected) {
    out.push(`story ${s.story}: ${s.label}`);
    if (s.covers) out.push(`  covers: ${s.covers}`);
    for (const [what, list] of [["engine", s.engine], ["engine, scopes on", s.scopesOn], ["app", s.app], ["scripts", s.scripts]])
      if (list?.length) out.push(`  ${what}: ${list.join(", ")}`);
    for (const script of stackScripts) {
      const mine = steps.filter((step) => step.script === script && step.stories.includes(s.story));
      if (mine.length) out.push(`  stack (${script}): ${mine.map((step) => step.what).join("; ")}`);
    }
    if (!s.covers) out.push("  no tests yet");
    out.push(`  open: ${(open.get(s.story) ?? []).join(", ") || "none"}`);
  }
  return out.join("\n");
}

// Two local ports nothing listens on: the system picks each, then it is closed again.
async function freePorts(count) {
  const servers = [];
  for (let i = 0; i < count; i++)
    servers.push(await new Promise((done, fail) => { const s = createServer().once("error", fail).listen(0, "127.0.0.1", () => done(s)); }));
  const ports = servers.map((s) => s.address().port);
  await Promise.all(servers.map((s) => new Promise((done) => s.close(done))));
  return ports;
}

// The settings a run takes from the environment, checked before anything starts. Throws a usage error's words.
export function settings(env = process.env) {
  const project = env.COMPOSE_PROJECT_NAME || "rhythm-acceptance";
  if (!/^[a-z0-9][a-z0-9_-]{0,40}$/.test(project)) throw new Error(`COMPOSE_PROJECT_NAME must be lower case letters, digits, - and _ (not ${project})`);
  // The sandbox stacks remove everything in their project, and rhythm is an install's own (D-240).
  if (project === "rhythm") throw new Error("COMPOSE_PROJECT_NAME must not be rhythm, an install's own project: the sandbox stacks remove everything in theirs (D-240)");
  const version = env.RHYTHM_VERSION || project;
  if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,100}$/.test(version)) throw new Error(`RHYTHM_VERSION must be an image tag (not ${version})`);
  const ports = ["RHYTHM_APP_PORT", "RHYTHM_MCP_PORT"].map((name) => env[name] || "");
  for (const p of ports) if (p && !(/^\d+$/.test(p) && Number(p) >= 1024 && Number(p) <= 65535)) throw new Error(`a port is a number from 1024 to 65535 (not ${p})`);
  if (ports[0] && ports[0] === ports[1]) throw new Error("RHYTHM_APP_PORT and RHYTHM_MCP_PORT must differ");
  const model = env.RHYTHM_MODEL_DIR || "";
  if (!model || !existsSync(join(model, "model_quantized.onnx")) || !existsSync(join(model, "vocab.txt")))
    throw new Error("NOT RUN: set RHYTHM_MODEL_DIR to a folder holding the pinned MiniLM files (model_quantized.onnx, vocab.txt); " +
      "the stack's engine and the retrieval gate need them, and no model is ever downloaded (D-041).");
  return { project, version, ports, model, container: env.RHYTHM_TEST_CONTAINER || `${project}-engine-tests` };
}

export function parseArgs(args) {
  const usage = "usage: node scripts/acceptance.mjs [--list] [--stories <n>[,<n>...]]";
  let list = false, chosen = null;
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === "--list") list = true;
    else if (a === "--stories" || a.startsWith("--stories=")) {
      const value = a === "--stories" ? args[++i] : a.slice("--stories=".length);
      if (!value || !/^\d+(,\d+)*$/.test(value)) throw new Error(`${usage}  (--stories takes story numbers, such as 1,2)`);
      chosen = [...new Set(value.split(",").map(Number))];
      const unknown = chosen.filter((n) => !stories.some((s) => s.story === n));
      if (unknown.length) throw new Error(`${usage}  (no story ${unknown.join(", ")}: they are 1 to ${stories.length})`);
    } else throw new Error(`${usage}  (unknown: ${a})`);
  }
  return { list, selected: chosen ? stories.filter((s) => chosen.includes(s.story)) : stories };
}

// On Windows npx is a .cmd file, found through a shell, which then needs its arguments quoted.
const windows = process.platform === "win32";
const quote = (a) => (windows && /[\s"&|<>^;]/.test(a) ? `"${a.replace(/"/g, '\\"')}"` : a);
function run(command, args, options = {}) {
  const shell = windows && command === "npx";
  return spawnSync(command, shell ? args.map(quote) : args, { cwd: root, stdio: "inherit", shell, ...options }).status;
}

// One sandbox script with its output shown as it comes and kept for the step outcomes.
function runStack(script, env) {
  return new Promise((done) => {
    const child = spawn("sh", [script], { cwd: root, env, stdio: ["ignore", "pipe", "pipe"] });
    let said = "";
    child.stdout.on("data", (d) => { said += d; process.stdout.write(d); });
    child.stderr.on("data", (d) => { process.stderr.write(d); });
    child.on("close", (status) => done({ status, said }));
  });
}

async function main(args) {
  let parsed, s;
  try {
    parsed = parseArgs(args);
    if (parsed.list) { console.log(listing(parsed.selected)); return 0; }
    s = settings();
  } catch (e) {
    console.error(e.message);
    return 2;
  }
  const { selected } = parsed;
  const free = await freePorts(2);
  const ports = s.ports.map((p, i) => (p ? Number(p) : free[i]));
  const work = mkdtempSync(join(tmpdir(), "rhythm-acceptance-"));
  const results = { engine: null, scopesOn: null, app: null, scripts: null, stack: null };
  const index = engineClassIndex();
  const union = (key) => [...new Set(selected.flatMap((story) => story[key] ?? []))];
  const started = Date.now();
  try {
    const base = { ...process.env, RHYTHM_MODEL_DIR: resolve(s.model), RHYTHM_PARITY_EVAL: "1" };
    // The evaluation as the gate's locks hold it: both load modes, today's keyword search, scopes off unless asked.
    for (const name of ["RHYTHM_PARITY_SCOPES", "RHYTHM_PARITY_MODE", "RHYTHM_PARITY_LEXICAL", "RHYTHM_PARITY_LOCK", "RHYTHM_PARITY_LOCK_REASON"]) delete base[name];
    for (const [key, extra, name] of [["engine", {}, "engine"], ["scopesOn", { RHYTHM_PARITY_SCOPES: "on" }, "engine-scopes-on"]]) {
      const classes = union(key);
      if (!classes.length) continue;
      console.log(`\n== acceptance: the engine's tests${key === "scopesOn" ? " with memory scopes on" : ""} (${classes.length} classes)`);
      run("sh", ["engine/test-db.sh", "--filter", engineFilter(classes, index), "--logger", `trx;LogFileName=${name}.trx`, "--results-directory", work],
        { env: { ...base, ...extra, RHYTHM_TEST_CONTAINER: `${s.container}${key === "scopesOn" ? "-scopes" : ""}` } });
      const trx = join(work, `${name}.trx`);
      results[key] = existsSync(trx) ? parseTrx(readFileSync(trx, "utf8")) : null;
    }
    const files = union("app");
    if (files.length) {
      console.log(`\n== acceptance: the app's tests (${files.length} files)`);
      const report = join(work, "vitest.json");
      run("npx", ["--no-install", "vitest", "run", ...files, "--reporter=default", "--reporter=json", `--outputFile.json=${report}`], { cwd: join(root, "app") });
      results.app = existsSync(report) ? parseVitest(readFileSync(report, "utf8")) : null;
    }
    const scripts = union("scripts");
    if (scripts.length) {
      console.log(`\n== acceptance: the script tests (${scripts.length} files)`);
      results.scripts = new Map(scripts.map((file) => {
        const status = run(process.execPath, ["--test", join("scripts", file)]);
        return [file, { ok: status === 0 ? 1 : 0, fail: status === 0 ? 0 : 1, skip: 0 }];
      }));
    }
    // Each script a selected story names a step of, in order: the next starts once the one before removed its stack.
    const steps = stackSteps();
    const stacks = stackScripts.filter((script) =>
      selected.some((story) => steps.some((step) => step.script === script && step.stories.includes(story.story))));
    if (stacks.length) {
      results.stack = new Map();
      const env = stackEnv(s, ports);
      for (const script of stacks) {
        console.log(`\n== acceptance: the sandbox stack, ${script} in project ${s.project} on ports ${ports.join(" and ")}`);
        const { status, said } = await runStack(script, env);
        const mine = steps.filter((step) => step.script === script);
        for (const [what, outcome] of stackOutcome(mine, said, status === 0 && /all steps passed/.test(said))) results.stack.set(what, outcome);
      }
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
  const rows = selected.map((story) => storyRow(story, results, { index }));
  console.log(`\nAcceptance stories (SB-P6-08): project ${s.project}, ports ${ports.join(" and ")}, test containers ${s.container}*, ` +
    `${Math.round((Date.now() - started) / 1000)} s; engine, app and scripts: tests passed; stack: steps passed of the story's\n`);
  console.log(table(rows));
  return rows.some((r) => ["FAIL", "not run", "incomplete"].includes(r.result)) ? 1 : 0;
}

const same = (a, b) => (windows ? a.toLowerCase() === b.toLowerCase() : a === b);
if (process.argv[1] && same(resolve(fileURLToPath(import.meta.url)), resolve(process.argv[1]))) process.exit(await main(process.argv.slice(2)));
