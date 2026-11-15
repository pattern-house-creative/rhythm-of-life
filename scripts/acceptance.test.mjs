import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import {
  engineClassIndex, engineFilter, listing, openItems, parseArgs, parseTrx, parseVitest, settings, stackEnv, stackOutcome, stackScripts, stackSteps, stories,
  storyRow, table,
} from "./acceptance.mjs";
import { appFiles as storyFourteenApp, engineClasses as storyFourteenEngine } from "./story-14.mjs";

// The acceptance script (scripts/acceptance.mjs, SB-P6-08): its story table names tests that exist, it reads
// cli/e2e.sh's steps and the backlog as they are, and it turns each side's results into the right word, so the table
// never says pass for a story whose tests did not run.
const root = fileURLToPath(new URL("..", import.meta.url));
const script = join(root, "scripts", "acceptance.mjs");
const sh = spawnSync("sh", ["-c", "exit 0"]).status === 0;

test("every story of the plan is listed once, in order, each with a label of its own", () => {
  assert.deepEqual(stories.map((s) => s.story), [...Array(18).keys()].map((i) => i + 1));
  for (const s of stories) assert.ok(typeof s.label === "string" && s.label.length > 0, `story ${s.story}`);
  assert.equal(new Set(stories.map((s) => s.label)).size, 18);
});

test("every engine class, app file and script a story names exists, each once in that story", () => {
  const index = engineClassIndex();
  for (const s of stories) {
    for (const key of ["engine", "scopesOn", "app", "scripts"]) {
      const list = s[key] ?? [];
      assert.equal(new Set(list).size, list.length, `story ${s.story} names a ${key} test twice`);
    }
    for (const name of [...(s.engine ?? []), ...(s.scopesOn ?? [])]) assert.ok(index.has(name), `story ${s.story}: no engine test class ${name}`);
    for (const file of s.app ?? []) assert.ok(existsSync(join(root, "app", file)), `story ${s.story}: no app test file ${file}`);
    for (const file of s.scripts ?? []) assert.ok(existsSync(join(root, "scripts", file)), `story ${s.story}: no script test ${file}`);
    // A story with tests says what they cover; one without has none of any kind.
    const any = ["engine", "scopesOn", "app", "scripts"].some((key) => s[key]?.length) || stackSteps().some((step) => step.stories.includes(s.story));
    assert.equal(Boolean(s.covers), any, `story ${s.story}`);
  }
});

test("story 14 runs scripts/story-14.mjs's suite and keeps no list of its own (D-118)", () => {
  const fourteen = stories.find((s) => s.story === 14);
  assert.deepEqual(fourteen.engine, storyFourteenEngine());
  assert.deepEqual(fourteen.app, storyFourteenApp());
});

test("stories 1 and 2 have engine tests, sandbox steps and, for story 1, the evaluation with scopes on", () => {
  const [one, two] = stories;
  assert.ok(one.engine.includes("MemoryScopeTests") && one.engine.includes("MemoryScopeMapTests"));
  assert.deepEqual(one.scopesOn, ["ParityEvaluation"]);
  assert.ok(two.engine.length > 0);
  const steps = stackSteps();
  for (const n of [1, 2]) {
    const mine = steps.filter((step) => step.stories.includes(n)).map((step) => step.what);
    assert.ok(mine.some((what) => what.startsWith("memory scopes on")), `story ${n}`);
    assert.ok(mine.some((what) => what.startsWith(`story ${n}:`)), `story ${n}`);
  }
});

for (const [script, least] of [["e2e.sh", 15], ["lifecycle.sh", 10], ["journeys.sh", 10], ["chaos.sh", 10]]) {
  test(`cli/${script}'s steps name stories 1 to 18 only, and its step lines are the ones the script reads`, { skip: !sh }, () => {
    const text = readFileSync(join(root, "cli", script), "utf8");
    const steps = stackSteps(text);
    assert.ok(steps.length >= least);
    assert.equal(steps.length, (text.match(/^step "/gm) ?? []).length, "every step line parses");
    assert.equal(new Set(steps.map((s) => s.what)).size, steps.length, "each step's words are its own");
    for (const step of steps) for (const n of step.stories) assert.ok(n >= 1 && n <= 18, step.what);
    assert.ok(text.split("\n").includes(`echo "cli/${script}: all steps passed."`), "the line a whole run ends with");
    // The script's own step function, as it prints it, read back by stackOutcome.
    const fn = /^step\(\) \{\n[\s\S]*?\n\}$/m.exec(text)?.[0];
    assert.ok(fn, "no step function");
    const out = spawnSync("sh", ["-c", `${fn}\nstep "first one" 18\nstep "second, two stories" 15 18\nstep "last"`], { encoding: "utf8" });
    assert.equal(out.status, 0, out.stderr);
    assert.match(out.stdout, /^== first one {2}\[story 18\]$/m);
    assert.match(out.stdout, /^== second, two stories {2}\[stories 15, 18\]$/m);
    const said = [{ what: "first one", stories: [18] }, { what: "second, two stories", stories: [15, 18] }, { what: "last", stories: [] }];
    assert.deepEqual([...stackOutcome(said, out.stdout, false)], [["first one", "passed"], ["second, two stories", "passed"], ["last", "failed"]]);
  });
}

// The sandbox scripts' steps, in the order a run starts them, each naming its script; a step's words are their own across
// all of them, since the stack's results are kept by them. The lifecycle (SB-P3-16) proves part of stories 3 and 18 with an AI
// client's and a person's steps, and of story 2 with its share step; the listing names each script's steps.
test("the stack's steps are cli/e2e.sh's, then cli/lifecycle.sh's, cli/journeys.sh's and cli/chaos.sh's, each step's words its own across them", () => {
  assert.deepEqual(stackScripts, ["cli/e2e.sh", "cli/lifecycle.sh", "cli/journeys.sh", "cli/chaos.sh"]);
  const steps = stackSteps();
  const each = stackScripts.map((script) => stackSteps(readFileSync(join(root, script), "utf8"), script));
  assert.deepEqual(steps, each.flat());
  assert.ok(each.every((list) => list.length > 0));
  assert.equal(new Set(steps.map((s) => s.what)).size, steps.length, "a step's words are their own across the scripts");
  const life = each[1];
  for (const n of [2, 3, 18]) assert.ok(life.some((step) => step.stories.includes(n)), `the lifecycle proves part of story ${n}`);
  assert.deepEqual(life.find((step) => step.what.startsWith("share:"))?.stories, [2, 18], "the share step names stories 2 and 18");
  for (const words of ["an AI client captures", "a person in the app", "recall:", "edit:", "undo:", "trash:", "restore:", "export:", "share:"])
    assert.ok(life.some((step) => step.what.includes(words)), words);
  const eighteen = listing([stories.find((s) => s.story === 18)]);
  assert.match(eighteen, /^ {2}stack \(cli\/e2e\.sh\): /m);
  assert.match(eighteen, /^ {2}stack \(cli\/lifecycle\.sh\): /m);
  assert.match(eighteen, /^ {2}stack \(cli\/journeys\.sh\): /m);
  // The restore chaos (SB-P7-10) proves part of story 15 with its real restores, and its steps are listed under it.
  const chaos = each[3];
  assert.ok(chaos.filter((step) => step.stories.includes(15)).length >= 3, "the chaos restores prove part of story 15");
  assert.match(listing([stories.find((s) => s.story === 15)]), /^ {2}stack \(cli\/chaos\.sh\): .*the restore, for real/m);
});

// Every sandbox stack, cli/chaos.sh among them, is started with the run's project named for it (COMPOSE_PROJECT_NAME), never one
// Compose would infer, with the run's own tag, ports and model folder, whatever the shell running the script had exported (D-240).
test("each sandbox stack gets the run's project, tag, ports and model by name, never an inferred or an install's project", (t) => {
  const model = mkdtempSync(join(tmpdir(), "rhythm-acceptance-model-"));
  t.after(() => rmSync(model, { recursive: true, force: true }));
  writeFileSync(join(model, "model_quantized.onnx"), "stand-in");
  writeFileSync(join(model, "vocab.txt"), "stand-in");
  const s = settings({ RHYTHM_MODEL_DIR: model, COMPOSE_PROJECT_NAME: "sb22-acceptance" });
  const env = stackEnv(s, [28380, 28481], { PATH: "/usr/bin", COMPOSE_PROJECT_NAME: "rhythm", RHYTHM_VERSION: "latest" });
  assert.equal(env.COMPOSE_PROJECT_NAME, "sb22-acceptance");
  assert.equal(env.RHYTHM_VERSION, "sb22-acceptance");
  assert.deepEqual([env.RHYTHM_APP_PORT, env.RHYTHM_MCP_PORT], ["28380", "28481"]);
  assert.equal(env.RHYTHM_MODEL_DIR, resolve(model));
  assert.equal(env.PATH, "/usr/bin");
  // Each script's own guard reads that one name, refuses an empty one or rhythm, and passes it to Compose itself.
  for (const name of stackScripts) {
    const text = readFileSync(join(root, name), "utf8");
    assert.match(text, /COMPOSE_PROJECT_NAME/, name);
    assert.match(text, /"" \| rhythm\)/, `${name} refuses an empty project or rhythm`);
    assert.match(text, /docker compose -p "\$(project|COMPOSE_PROJECT_NAME)"/, `${name} names its project on every Compose call`);
  }
});

// SB-P6-04: every file of app/tests/journeys/ runs in the script, and only what is there; stories 14 and 18 name its steps,
// and the journey the row asks for each has its step.
test("cli/journeys.sh runs every journey there is, and its steps name the stories they prove part of", () => {
  const text = readFileSync(join(root, "cli", "journeys.sh"), "utf8");
  const named = [...text.matchAll(/^journey ([a-z-]+)$/gm)].map((m) => m[1]);
  const files = readdirSync(join(root, "app", "tests", "journeys")).filter((f) => f.endsWith(".test.tsx")).map((f) => f.replace(".test.tsx", ""));
  assert.deepEqual([...new Set(named)].sort(), files.sort());
  const steps = stackSteps(text, "cli/journeys.sh");
  for (const n of [14, 18]) assert.ok(steps.some((step) => step.stories.includes(n)), `story ${n}`);
  for (const words of ["saving a memory", "changing a memory", "pasted instructions", "the review queues", "from the brief", "the trash and undo", "sharing one memory", "every memory setting off"])
    assert.ok(steps.some((step) => step.what.includes(words)), words);
  // The core journey runs with every setting off, with memory.tools on and with scopes on.
  assert.equal(named.filter((n) => n === "core").length, 3);
  // Each import is checked against the corpus's own held count, Family's and each person's.
  assert.match(text, /^held family "\$out"$/m);
  assert.match(text, /^ {2}held "\$who" "\$out"$/m);
  // The pairs the review queues' journey decides are the nightly pass's own: nothing is written for it as the database's owner.
  assert.match(text, /^pairs_found$/m);
  assert.doesNotMatch(text, /memory_insight_accrue|rhythm_admin/);
  // Nothing of it stands in for the stack: each journey talks to it, and sets no fake engine.
  for (const file of files) assert.doesNotMatch(readFileSync(join(root, "app", "tests", "journeys", `${file}.test.tsx`), "utf8"), /memoryFake|queueFake|plansFake/, file);
});

test("the corpus file the journeys import is the parity corpus's own, in the format rhythm import reads", async () => {
  const { archive, corpusId, documents, held, readCorpus } = await import("../cli/journeys-corpus.mjs");
  const corpus = readCorpus();
  for (const [which, scope, subject] of [["family", "family", "household"], ["alex", "personal:alex", "alex"], ["sam", "personal:sam", "sam"]]) {
    const file = archive(which, corpus);
    assert.equal(file.format, "rhythm-memories");
    assert.equal(file.version, 1);
    const mine = corpus.memories.filter((m) => m.scope === scope);
    assert.equal(file.memories.length, mine.length);
    assert.deepEqual(file.counts, { projects: file.projects.length, memories: mine.length, links: file.links.length });
    assert.ok(file.memories.every((m) => m.subject === subject && m.hold === null), "the import holds by the words, not by the file");
    assert.deepEqual(file.memories.map((m) => m.content), mine.map((m) => m.text), "the corpus's words, unchanged");
    assert.deepEqual(archive(which, corpus), file, "two runs write the same file");
    // Every link's ends are of this file, and a commitment is an open loop.
    const ids = new Set(file.memories.map((m) => m.id));
    assert.ok(file.links.every((l) => ids.has(l.source) && ids.has(l.target)));
    assert.ok(file.memories.filter((m) => m.commitment).every((m) => m.tags.includes("task")));
    // What the corpus marks held in this scope, by the ids the file gives them; each with phrases of its words that no
    // other memory or document of the corpus holds.
    const marked = mine.filter((m) => m.expect?.held === true);
    assert.ok(marked.length > 0, `the corpus marks an injection held in ${which}`);
    const kept = held(which, corpus);
    assert.deepEqual(kept.map((h) => h.id), marked.map((m) => corpusId(m.id)));
    for (const [i, h] of kept.entries()) {
      assert.ok(h.phrases.length > 0, h.id);
      for (const phrase of h.phrases) {
        assert.equal(phrase.length, 24);
        assert.ok(marked[i].text.includes(phrase));
        assert.ok(!corpus.memories.some((m) => m.id !== marked[i].id && m.text.includes(phrase)), phrase);
        assert.ok(!corpus.documents.some((d) => d.text.includes(phrase)), phrase);
      }
    }
  }
  assert.throws(() => held("jo", corpus), /no scope jo/);
  assert.throws(() => archive("jo", corpus), /no scope jo/);
  assert.notEqual(corpusId("a"), corpusId("b"));
  assert.ok(documents(corpus).length > 0 && documents(corpus).every((d) => d.subject === "household" && d.content));
});

test("a step passed once the next began, the one the script stopped in failed, the rest were not reached", () => {
  const steps = [{ what: "a", stories: [1] }, { what: "b", stories: [1] }, { what: "c", stories: [2] }];
  assert.deepEqual([...stackOutcome(steps, "\n== a  [story 1]\nok\n\n== b  [story 1]\nFAILED: b\n", false)],
    [["a", "passed"], ["b", "failed"], ["c", "not reached"]]);
  assert.deepEqual([...stackOutcome(steps, "== a\n== b\n== c  [story 2]\ncli/e2e.sh: all steps passed.\n", true)],
    [["a", "passed"], ["b", "passed"], ["c", "passed"]]);
  // Refused before its first step (a busy port, no model): nothing reached.
  assert.deepEqual([...stackOutcome(steps, "cli/e2e.sh: something already answers on 127.0.0.1:18080\n", false)].map(([, o]) => o),
    ["not reached", "not reached", "not reached"]);
});

test("a TRX file's results are counted per class, a skipped test as skipped", () => {
  const trx = `<?xml version="1.0" encoding="utf-8"?><TestRun><Results>
    <UnitTestResult executionId="e1" testId="t1" testName="A.One" outcome="Passed" />
    <UnitTestResult executionId="e2" testId="t2" testName="A.Two" outcome="Failed"><Output /></UnitTestResult>
    <UnitTestResult executionId="e3" testId="t3" testName="B.One" outcome="NotExecuted" />
    <UnitTestResult executionId="e4" testId="t4" testName="B.Two" outcome="Passed" />
  </Results><TestDefinitions>
    <UnitTest name="A.One" storage="x.dll" id="t1"><Execution id="e1" /><TestMethod codeBase="x.dll" className="Knowledge.Tests.A" name="One" /></UnitTest>
    <UnitTest name="A.Two" storage="x.dll" id="t2"><Execution id="e2" /><TestMethod codeBase="x.dll" className="Knowledge.Tests.A" name="Two" /></UnitTest>
    <UnitTest name="B.One" storage="x.dll" id="t3"><Execution id="e3" /><TestMethod codeBase="x.dll" className="Knowledge.Tests.Parity.B" name="One" /></UnitTest>
    <UnitTest name="B.Two" storage="x.dll" id="t4"><Execution id="e4" /><TestMethod codeBase="x.dll" className="Knowledge.Tests.Parity.B" name="Two" /></UnitTest>
  </TestDefinitions></TestRun>`;
  assert.deepEqual([...parseTrx(trx)], [["Knowledge.Tests.A", { ok: 1, fail: 1, skip: 0 }], ["Knowledge.Tests.Parity.B", { ok: 1, fail: 0, skip: 1 }]]);
});

test("vitest's report is counted per file, and a file that fails to load fails", () => {
  const app = join(root, "app");
  const report = JSON.stringify({ testResults: [
    { name: join(app, "tests", "a.test.ts"), status: "passed", assertionResults: [{ status: "passed" }, { status: "skipped" }, { status: "passed" }] },
    { name: join(app, "tests", "b.test.tsx"), status: "failed", assertionResults: [{ status: "passed" }, { status: "failed" }] },
    { name: join(app, "tests", "c.test.ts"), status: "failed", assertionResults: [] },
  ] });
  assert.deepEqual([...parseVitest(report, app)], [
    ["tests/a.test.ts", { ok: 2, fail: 0, skip: 1 }], ["tests/b.test.tsx", { ok: 1, fail: 1, skip: 0 }], ["tests/c.test.ts", { ok: 0, fail: 1, skip: 0 }]]);
});

test("a story passes only when every side it names ran and none failed; part while its backlog items are open", () => {
  const index = new Map([["A", "Knowledge.Tests.A"], ["B", "Knowledge.Tests.B"]]);
  const steps = [{ what: "s1", stories: [1] }, { what: "s2", stories: [1, 2] }];
  const open = new Map([[1, ["SB-P3-02"]]]);
  const story = { story: 1, label: "one", engine: ["A"], scopesOn: ["B"], app: ["tests/x.test.ts"] };
  const good = { engine: new Map([["Knowledge.Tests.A", { ok: 3, fail: 0, skip: 1 }]]), scopesOn: new Map([["Knowledge.Tests.B", { ok: 1, fail: 0, skip: 0 }]]),
    app: new Map([["tests/x.test.ts", { ok: 2, fail: 0, skip: 0 }]]), stack: new Map([["s1", "passed"], ["s2", "passed"]]) };
  const row = (s, r, o = open) => storyRow(s, r, { steps, open: o, index });
  assert.equal(row(story, good).result, "pass (part)");
  assert.deepEqual(row(story, good).cells.engine, { ok: 4, fail: 0, skip: 1, missing: 0 });
  assert.equal(row(story, good, new Map()).result, "pass");
  assert.equal(row(story, { ...good, stack: new Map([["s1", "passed"], ["s2", "failed"]]) }).result, "FAIL");
  assert.equal(row(story, { ...good, app: new Map([["tests/x.test.ts", { ok: 1, fail: 1, skip: 0 }]]) }).result, "FAIL");
  // A side that ran nothing: the app's report missing, the stack never reached, every engine test skipped.
  assert.equal(row(story, { ...good, app: null }).result, "not run");
  assert.equal(row(story, { ...good, stack: null }).result, "not run");
  const skipped = { engine: new Map([["Knowledge.Tests.A", { ok: 0, fail: 0, skip: 4 }]]) };
  assert.equal(row({ story: 3, label: "three", engine: ["A"] }, skipped).result, "not run");
  // Part of a side ran: one of its runs gave no report, a step was not reached, a class skipped every test or gave
  // no result while others did.
  assert.equal(row(story, { ...good, scopesOn: null }).result, "incomplete");
  assert.equal(row(story, { ...good, stack: new Map([["s1", "passed"]]) }).result, "incomplete");
  assert.equal(row(story, { ...good, ...skipped }).result, "incomplete");
  assert.equal(row({ ...story, engine: ["A", "B"] }, good).result, "incomplete");
  assert.equal(row({ story: 9, label: "nine" }, good).result, "no tests yet");
  // The table has every row and counts each word.
  const text = table([row(story, good), row({ story: 9, label: "nine" }, good)]);
  assert.match(text, /^story +result +engine +app +scripts +stack +open +label$/m);
  assert.match(text, /^1 +pass \(part\) +4, 1 skip +2 +- +2\/2 +1 +one$/m);
  assert.match(text, /^9 +no tests yet +- +- +- +- +0 +nine$/m);
  assert.match(text, /^2 stories: 0 pass, 1 pass \(part\), 0 FAIL, 0 not run, 0 incomplete, 1 no tests yet$/m);
});

test("open items are the backlog rows naming the story by number that are not done; recurring and program rows left out", () => {
  const backlog = [
    "| ID | Item | Class | Depends on | Shared files | Tests | Stories | Parallel | Status |",
    "|---|---|---|---|---|---|---|---|---|",
    "| SB-P1-10 | a | NEW | x | none | t | 1, 2 | p | done for phase 1 (run 7) |",
    "| SB-P1-11 | b | NEW | x | none | t | 1, 2 | p | built behind the switch |",
    "| SB-P3-02 | c | NEW | x | none | t | 3 | p | waiting |",
    // The word in passing does not make a row recurring: only a status that begins by saying so.
    "| SB-P7-05 | g | NEW | x | none | t | 4 | p | built, off, tested for recurring events |",
    "| SB-P6-08 | d | NEW | x | none | t | all | p | ready |",
    "| SB-X-01 | e | KEEP | x | none | t | 14 | p | ready (recurring): last taken |",
    "",
    "| Story | Label | Story | Label |",
    "|---|---|---|---|",
    "| 1 | a private fact stays private | 10 | capsule slots, no guessing |",
  ].join("\n");
  assert.deepEqual([...openItems(backlog)], [[1, ["SB-P1-11"]], [2, ["SB-P1-11"]], [3, ["SB-P3-02"]], [4, ["SB-P7-05"]]]);
  // The backlog itself is a working note kept outside this repository: with no file named, no story has open items.
  assert.deepEqual([...openItems("")], []);
});

test("the engine filter names each class by its full name and a dot, Parity classes in their namespace", () => {
  const index = engineClassIndex();
  assert.equal(index.get("ParityEvaluation"), "Knowledge.Tests.Parity.ParityEvaluation");
  assert.equal(index.get("MemoryScopeDatabaseTests"), "Knowledge.Tests.MemoryScopeDatabaseTests");
  assert.equal(engineFilter(["DatabaseTests", "ParityGateRun"], index),
    "FullyQualifiedName~Knowledge.Tests.DatabaseTests.|FullyQualifiedName~Knowledge.Tests.Parity.ParityGateRun.");
  assert.throws(() => engineFilter(["NoSuchTests"], index), /no engine test class NoSuchTests/);
});

test("options: --list, --stories, and anything else refused", () => {
  assert.deepEqual(parseArgs([]).selected.map((s) => s.story), stories.map((s) => s.story));
  assert.deepEqual(parseArgs(["--stories", "2,1,2"]).selected.map((s) => s.story), [1, 2]);
  assert.deepEqual(parseArgs(["--stories=14"]).selected.map((s) => s.story), [14]);
  assert.equal(parseArgs(["--list"]).list, true);
  assert.throws(() => parseArgs(["--stories", "19"]), /no story 19/);
  assert.throws(() => parseArgs(["--stories"]), /story numbers/);
  assert.throws(() => parseArgs(["--everything"]), /^Error: usage: node scripts\/acceptance\.mjs/);
  const list = listing(parseArgs(["--stories", "1,9"]).selected);
  assert.match(list, /^story 1: a private fact stays private$/m);
  assert.match(list, /^ {2}engine, scopes on: ParityEvaluation$/m);
  assert.match(list, /^ {2}stack \(cli\/e2e\.sh\): .*story 1: /m);
  assert.match(listing([{ story: 99, label: "none yet" }]), /^story 99: none yet\n {2}no tests yet\n {2}open: none$/m);
  assert.match(list, /^story 9: duplicates and contradictions reviewed\n {2}covers: /m);
});

test("settings: a project of its own, its tag, ports that differ, and the model files, or nothing starts", (t) => {
  const model = mkdtempSync(join(tmpdir(), "rhythm-acceptance-model-"));
  t.after(() => rmSync(model, { recursive: true, force: true }));
  writeFileSync(join(model, "model_quantized.onnx"), "stand-in");
  writeFileSync(join(model, "vocab.txt"), "stand-in");
  const base = { RHYTHM_MODEL_DIR: model };
  assert.deepEqual(settings(base), { project: "rhythm-acceptance", version: "rhythm-acceptance", ports: ["", ""], model, container: "rhythm-acceptance-engine-tests" });
  assert.deepEqual(settings({ ...base, COMPOSE_PROJECT_NAME: "sb8-acceptance", RHYTHM_APP_PORT: "28080", RHYTHM_MCP_PORT: "28181", RHYTHM_TEST_CONTAINER: "sb8-x" }),
    { project: "sb8-acceptance", version: "sb8-acceptance", ports: ["28080", "28181"], model, container: "sb8-x" });
  assert.throws(() => settings({ ...base, COMPOSE_PROJECT_NAME: "Not Mine" }), /COMPOSE_PROJECT_NAME/);
  assert.throws(() => settings({ ...base, COMPOSE_PROJECT_NAME: "rhythm" }), /must not be rhythm, an install.s own project/);
  assert.throws(() => settings({ ...base, RHYTHM_VERSION: "a:b" }), /RHYTHM_VERSION/);
  assert.throws(() => settings({ ...base, RHYTHM_APP_PORT: "80" }), /1024 to 65535/);
  assert.throws(() => settings({ ...base, RHYTHM_APP_PORT: "28080", RHYTHM_MCP_PORT: "28080" }), /must differ/);
  assert.throws(() => settings({}), /NOT RUN: set RHYTHM_MODEL_DIR/);
  writeFileSync(join(model, "vocab.txt"), "stand-in");
  rmSync(join(model, "model_quantized.onnx"));
  assert.throws(() => settings(base), /D-041/);
});

test("run without the model files, or with a wrong option, it refuses before starting anything", () => {
  const env = { ...process.env };
  delete env.RHYTHM_MODEL_DIR;
  const none = spawnSync(process.execPath, [script], { encoding: "utf8", env });
  assert.equal(none.status, 2, none.stderr);
  assert.match(none.stderr, /^NOT RUN: set RHYTHM_MODEL_DIR/);
  assert.doesNotMatch(none.stdout, /== acceptance/);
  const wrong = spawnSync(process.execPath, [script, "--everything"], { encoding: "utf8", env });
  assert.equal(wrong.status, 2);
  assert.match(wrong.stderr, /^usage: node scripts\/acceptance\.mjs/);
  const list = spawnSync(process.execPath, [script, "--list"], { encoding: "utf8", env });
  assert.equal(list.status, 0, list.stderr);
  for (const s of stories) assert.match(list.stdout, new RegExp(`^story ${s.story}: `, "m"));
});

// The script itself fetches nothing; the stack it starts does (its builds and story 17's upgrade steps), and the
// script says so rather than claiming nothing is downloaded.
test("no model is downloaded and the script fetches nothing itself; it says the stack needs the registries", () => {
  const text = readFileSync(script, "utf8");
  assert.match(text, /run\("npx", \["--no-install", "vitest", "run"/);
  assert.doesNotMatch(text.replace(/^\/\/.*$/gm, ""), /npm (ci|install)|dotnet restore|docker pull|curl /);
  assert.doesNotMatch(text, /nothing is (ever )?downloaded/);
  assert.match(text, /no model is ever downloaded/);
  assert.match(text, /needs the image and package registries/);
  const e2e = readFileSync(join(root, "cli", "e2e.sh"), "utf8");
  assert.match(e2e, /\.\/rhythm upgrade e2e-missing-tag --pull/, "story 17's pull step, which the header names");
});
