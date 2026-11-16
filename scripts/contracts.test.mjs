import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";

import { check, unsupported } from "./json-schema.mjs";

const mcp = new URL("../contracts/mcp/", import.meta.url);
const fixtures = new URL("../contracts/fixtures/", import.meta.url);
const adapters = new URL("../contracts/adapters/", import.meta.url);
const read = (folder, name) => JSON.parse(readFileSync(new URL(name, folder), "utf8"));
const folder = (url) => decodeURIComponent(url.pathname.replace(/^\/([A-Za-z]:)/, "$1"));

// Each fixture and the schema it is an example of: <tool>[.<form>].example.json goes with
// <tool>.input.schema.json for ingest arguments and a capture's request bodies, and <tool>.output.schema.json for app
// results and a capture's answer.
const schemaOf = (name) => {
  const tool = name.split(".")[0];
  return tool.startsWith("ingest_") || tool === "capture_request" || tool === "capture_webhook" ? `${tool}.input.schema.json` : `${tool}.output.schema.json`;
};

test("every contract schema uses only the keywords the checkers know", () => {
  for (const name of readdirSync(mcp).filter((n) => n.endsWith(".json")))
    assert.deepEqual(unsupported(read(mcp, name)), [], name);
  assert.deepEqual(unsupported(read(adapters, "adapter.schema.json")), []);
});

test("the settings schema is within the checkers' keywords and holds each adapter to its own keys", () => {
  // SB-P7-09: the engine's tests check the example rhythm.yaml and what rhythm init writes against this schema
  // (OperatorDocsTests); here, the keyword each adapter section relies on, unevaluatedProperties.
  const contracts = new URL("../contracts/", import.meta.url);
  assert.deepEqual(unsupported(read(contracts, "rhythm.schema.json")), []);
  const settings = (extra) => check(folder(contracts), "rhythm.schema.json",
    { version: 1, instance: { name: "DEMO" }, timezone: "UTC", people: [{ id: "alex", name: "Alex" }], ...extra });
  assert.deepEqual(settings({}), []);
  assert.deepEqual(settings({ adapters: { garmin: { enabled: false, owner: "alex", pollSeconds: 21600 } } }), []);
  // A key of no adapter's, a key another adapter takes, and a link under the calendar's section are each refused.
  assert.notDeepEqual(settings({ adapters: { garmin: { enabled: false, nosuch: 1 } } }), []);
  assert.notDeepEqual(settings({ adapters: { garmin: { enabled: false, roots: [] } } }), []);
  assert.notDeepEqual(settings({ adapters: { calendar: { enabled: false, url: "https://calendar.example.invalid/a.ics" } } }), []);
  // Every adapter section needs enabled; an adapter this schema does not describe takes its own keys.
  assert.notDeepEqual(settings({ adapters: { garmin: { owner: "alex" } } }), []);
  assert.deepEqual(settings({ adapters: { logbook: { enabled: true, url: "https://logbook.example.invalid/" } } }), []);
  // The mutual rule's threshold (graph.similarity.mutualThreshold), as the engine reads it.
  assert.deepEqual(settings({ graph: { similarity: { mutualThreshold: 0.45 } } }), []);
  assert.deepEqual(settings({ graph: { similarity: { mutualThreshold: null } } }), []);
  assert.notDeepEqual(settings({ graph: { similarity: { mutualThreshold: 0.1 } } }), []);
  assert.equal(read(contracts, "rhythm.schema.json").properties.graph.properties.similarity.properties.mutualThreshold.default, 0.45);
  // The cap on each star's similar links (graph.similarity.maxLinks, D-258): a whole number from 2 to 50, no default;
  // null, as the engine reads it, is no cap.
  for (const ok of [2, 6, 50, null]) assert.deepEqual(settings({ graph: { similarity: { maxLinks: ok } } }), []);
  for (const wrong of [1, 51, 6.5, "6"]) assert.notDeepEqual(settings({ graph: { similarity: { maxLinks: wrong } } }), [], String(wrong));
  assert.equal(read(contracts, "rhythm.schema.json").properties.graph.properties.similarity.properties.maxLinks.default, undefined);
});

test("every fixture is valid against its schema", () => {
  const names = readdirSync(fixtures).filter((n) => n.endsWith(".example.json"));
  assert.ok(names.length >= 10);
  for (const name of names) assert.deepEqual(check(folder(mcp), schemaOf(name), read(fixtures, name)), [], name);
});

test("every app tool the facade reads has an output schema and a fixture", () => {
  for (const tool of ["app_latest_glucose", "app_recent_glucose", "app_graph", "app_insights", "app_dashboards", "app_request_refresh",
    "app_garmin_summary", "app_request_garmin_sync", "app_upload_document", "app_screens", "app_status", "app_viewer",
    "app_sync", "app_memories"]) {
    assert.ok(readdirSync(mcp).includes(`${tool}.output.schema.json`), tool);
    assert.ok(readdirSync(fixtures).some((n) => n.startsWith(`${tool}.`)), tool);
  }
  const insight = read(fixtures, "app_insights.example.json").insights[0];
  assert.deepEqual(check(folder(mcp), "app_insight_feedback.output.schema.json", insight), []);
});

test("the brief's schemas refuse a section beyond its list's shape", () => {
  // SB-P5-08: each section's items are its own tool's, at most what a brief shows; a due or a decision only in the two windows; a
  // standing instruction only one that may fire; calibration's line only once it is ready.
  const answer = (value) => check(folder(mcp), "brief.output.schema.json", value);
  const brief = read(fixtures, "brief.example.json");
  assert.deepEqual(answer(brief), []);
  assert.deepEqual(answer(read(fixtures, "brief.quiet.example.json")), []);
  const [due] = brief.due.items;
  assert.notDeepEqual(answer({ ...brief, due: { ...brief.due, items: [{ ...due, window: "later" }] } }), []);
  assert.notDeepEqual(answer({ ...brief, due: { ...brief.due, items: Array(6).fill(due) } }), []);
  const [decision] = brief.decisions.items;
  assert.notDeepEqual(answer({ ...brief, decisions: { ...brief.decisions, items: [{ ...decision, window: "later" }] } }), []);
  assert.notDeepEqual(answer({ ...brief, decisions: { ...brief.decisions, items: [{ ...decision, current: false }] } }), []);
  const [standing] = brief.standing.items;
  assert.notDeepEqual(answer({ ...brief, standing: { ...brief.standing, items: [{ ...standing, fires: false, reason: "over_limit" }] } }), []);
  assert.notDeepEqual(answer({ ...brief, standing: { ...brief.standing, items: [{ ...standing, content: null }] } }), []);
  const [insight] = brief.insights.items;
  assert.notDeepEqual(answer({ ...brief, insights: { ...brief.insights, items: [{ ...insight, view: "confirmed" }] } }), []);
  assert.notDeepEqual(answer({ ...brief, calibration: { ...brief.calibration, ready: true, line: null } }), []);
  assert.notDeepEqual(answer({ ...brief, calibration: { ...brief.calibration, ready: false, line: "DEMO" } }), []);
  const [group] = brief.changes.groups;
  assert.notDeepEqual(answer({ ...brief, changes: { ...brief.changes, groups: [{ ...group, preview: "DEMO: words" }] } }), []);
  // The app's op, the Home card's.
  const app = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const card = read(fixtures, "app_memories.brief.example.json");
  assert.deepEqual(app(card), []);
  assert.deepEqual(app(read(fixtures, "app_memories.brief.quiet.example.json")), []);
  assert.notDeepEqual(app({ ...card, due: { ...card.due, items: [{ ...card.due.items[0], window: "later" }] } }), []);
  assert.notDeepEqual(app({ ...card, youOwe: { count: 1, items: [{ ...card.youOwe.items[0], preview: "x".repeat(121) }] } }), []);
  // list_decisions' upcoming is the rule's upcoming, and not its overdue.
  const list = read(fixtures, "list_decisions.example.json");
  const one = { ...list.decisions[0], due: false, window: "upcoming" };
  const decisions = (value) => check(folder(mcp), "list_decisions.output.schema.json", value);
  assert.deepEqual(decisions({ ...list, state: "upcoming", decisions: [one] }), []);
  assert.notDeepEqual(decisions({ ...list, state: "upcoming", decisions: [{ ...one, due: true }] }), []);
  assert.notDeepEqual(decisions({ ...list, state: "upcoming", decisions: [{ ...one, window: "later" }] }), []);
  assert.notDeepEqual(decisions({ ...list, state: "due", decisions: [{ ...one, due: true, window: "upcoming" }] }), []);
});

test("resolve's schema holds each queue to its own action and never answers a person's step", () => {
  // SB-P5-15: an action only with its queue; a memory's place only of a memory; a second resolve writes nothing; a person's steps
  // are refused, never answered; and no answer carries a memory's words.
  const answer = (value) => check(folder(mcp), "resolve.output.schema.json", value);
  const done = read(fixtures, "resolve.example.json");
  const insight = read(fixtures, "resolve.insight.example.json");
  const candidate = read(fixtures, "resolve.candidate.example.json");
  for (const name of ["resolve.example.json", "resolve.snoozed.example.json", "resolve.insight.example.json", "resolve.candidate.example.json",
    "resolve.unchanged.example.json", "resolve.refused.example.json"]) assert.deepEqual(answer(read(fixtures, name)), [], name);
  assert.notDeepEqual(answer({ ...done, action: "still_true" }), []);
  assert.notDeepEqual(answer({ ...done, queue: "stale" }), []);
  assert.notDeepEqual(answer({ ...done, changed: ["due"] }), []);
  assert.notDeepEqual(answer({ ...done, revision: null }), []);
  assert.notDeepEqual(answer({ ...done, status: "deprecated" }), []);
  assert.notDeepEqual(answer({ ...done, content: "DEMO: words" }), []);
  assert.notDeepEqual(answer({ ...done, result: "right" }), []);
  assert.notDeepEqual(answer({ ...insight, revision: 2 }), []);
  assert.notDeepEqual(answer({ ...insight, changed: ["tags"] }), []);
  assert.notDeepEqual(answer({ ...insight, review: null }), []);
  assert.notDeepEqual(answer({ ...insight, still_counted: true }), []);
  // Newly counted: only a snooze that wrote, and never beside still counted.
  const snoozed = read(fixtures, "resolve.snoozed.example.json");
  assert.deepEqual(answer({ ...snoozed, still_counted: false, newly_counted: true }), []);
  assert.notDeepEqual(answer({ ...snoozed, still_counted: true, newly_counted: true }), []);
  assert.notDeepEqual(answer({ ...snoozed, still_counted: false, newly_counted: true, unchanged: true }), []);
  assert.notDeepEqual(answer({ ...snoozed, still_counted: false, newly_counted: true, action: "clear_date" }), []);
  assert.notDeepEqual(answer({ ...done, newly_counted: true }), []);
  assert.notDeepEqual(answer({ ...insight, newly_counted: true }), []);
  const { newly_counted: _, ...without } = done;
  assert.notDeepEqual(answer(without), []);
  assert.notDeepEqual(answer({ ...candidate, notice: null }), []);
  for (const action of ["release", "settle_keep_both", "settle_replace", "reopen"]) assert.notDeepEqual(answer({ ...done, action }), [], action);
  for (const queue of ["held", "contradictions"]) assert.notDeepEqual(answer({ ...done, queue }), [], queue);
  assert.notDeepEqual(answer({ ...read(fixtures, "resolve.refused.example.json"), code: "needs_a_person" }), []);
});

test("the write limit's setting, refusal and statuses are in the contract (SB-P7-12)", () => {
  const setting = read(new URL("../contracts/", import.meta.url), "rhythm.schema.json").properties.engine.properties.writeLimit;
  assert.equal(setting.default, "off");
  assert.deepEqual(setting.enum, ["off", "on", null]);
  assert.match(readFileSync(new URL("../docs/configuration.md", import.meta.url), "utf8"), /\| `engine\.writeLimit` \| `off` \|/);
  // A memory tool's refusal, each tool's answer taking it as any other; its code is no other refusal's.
  const refused = read(fixtures, "remember.write_limit.example.json");
  for (const tool of ["remember", "append", "update", "set_status", "forget", "undo", "link", "resolve"])
    assert.deepEqual(check(folder(mcp), `${tool}.output.schema.json`, refused), [], tool);
  assert.notDeepEqual(check(folder(mcp), "remember.output.schema.json", { ...refused, code: "too_many" }), []);
  // A plugin and a capture connection read it from the same place as their other refusals.
  const contract = readFileSync(new URL("../contracts/README.md", import.meta.url), "utf8");
  assert.match(contract, /\| `429` \| `write_limit` \|/);
  assert.match(contract, /`write_limit` \(HTTP `429` with `Retry-After`/);
});

test("a capture's setting, request, webhook and answer are in the contract (SB-P7-01)", () => {
  const setting = read(new URL("../contracts/", import.meta.url), "rhythm.schema.json").properties.memory.properties.capture;
  assert.equal(setting.default, "off");
  assert.deepEqual(setting.enum, ["off", "on", null]);
  assert.match(readFileSync(new URL("../docs/configuration.md", import.meta.url), "utf8"), /\| `memory\.capture` \| `off` \|/);
  const request = (value) => check(folder(mcp), "capture_request.input.schema.json", value);
  assert.deepEqual(request(read(fixtures, "capture_request.ci.example.json")), []);
  // The engine's own channels are no source, no field outside the list, and a request says what it saves.
  assert.notDeepEqual(request({ text: "DEMO", source: "system" }), []);
  assert.notDeepEqual(request({ text: "DEMO", status: "canonical" }), []);
  assert.notDeepEqual(request({ source: "ci" }), []);
  // The answer says nothing of the scope it wrote into, and is never canonical.
  const answer = (value) => check(folder(mcp), "capture.output.schema.json", value);
  const saved = read(fixtures, "capture.example.json");
  assert.deepEqual(answer(saved), []);
  assert.notDeepEqual(answer({ ...saved, status: "canonical" }), []);
  assert.notDeepEqual(answer({ ...saved, content: "DEMO" }), []);
});

test("every insight source kind has a fixture of its own", () => {
  // Q-076: the app reads every kind the engine accepts, each tried on its own example (SB-P5-07).
  const kinds = read(mcp, "app_insights.output.schema.json").$defs.insight.properties.sources.items.properties.kind.enum;
  assert.deepEqual([...kinds].sort(), ["document", "drive", "graph", "memory", "nas", "record", "second-brain", "web"]);
  for (const kind of kinds) {
    const name = kind === "graph" ? "app_insights.example.json" : `app_insights.${kind}.example.json`;
    const { insights } = read(fixtures, name);
    assert.ok(insights.length > 0, name);
    for (const insight of insights) assert.ok(insight.sources.some((s) => s.kind === kind), name);
  }
});

test("the schemas refuse what the facade would refuse", () => {
  const fortnight = read(fixtures, "app_recent_glucose.example.json");
  const problems = (value) => check(folder(mcp), "app_recent_glucose.output.schema.json", value);
  // A stretch kind named after a vendor, a live stretch with an archive's time basis, a bound that is neither -1 nor 1.
  assert.notDeepEqual(problems({ ...fortnight, sources: [{ ...fortnight.sources[0], kind: "carelink-csv" }] }), []);
  assert.notDeepEqual(problems({ ...fortnight, sources: [{ ...fortnight.sources[1], timeBasis: "device-local" }] }), []);
  assert.notDeepEqual(problems({ ...fortnight, series: [[1793001600, 104, 2]] }), []);
  assert.notDeepEqual(problems({ ...fortnight, summary: { ...fortnight.summary, sourceKind: "measurements" } }), []);
  const latest = read(fixtures, "app_latest_glucose.example.json");
  assert.notDeepEqual(check(folder(mcp), "app_latest_glucose.output.schema.json", { ...latest, rows: [{ ...latest.rows[0], unit: "mmol/L" }] }), []);
  const garmin = read(fixtures, "app_garmin_summary.example.json");
  assert.notDeepEqual(check(folder(mcp), "app_garmin_summary.output.schema.json", { ...garmin, rows: [{ ...garmin.rows[2], unit: "bpm" }] }), []);
  const upload = read(fixtures, "app_upload_document.example.json");
  assert.notDeepEqual(check(folder(mcp), "app_upload_document.output.schema.json", { ...upload, ok: false }), []);
  const list = read(fixtures, "app_dashboards.example.json");
  assert.notDeepEqual(check(folder(mcp), "app_dashboards.output.schema.json", { dashboards: [{ ...list.dashboards[0], html: "<p></p>" }] }), []);
  // A command without its timings, a sender that names a person rather than a screen, a screen id too short to be random,
  // a Home kept in the shape a screen reports it ({dashboard: {id, title}}) rather than {dashboard: id}.
  const screens = (value) => check(folder(mcp), "app_screens.output.schema.json", value);
  const heard = read(fixtures, "app_screens.example.json");
  const { ms, ...untimed } = heard.command;
  assert.notDeepEqual(screens({ ...heard, command: untimed }), []);
  assert.notDeepEqual(screens({ command: { ...heard.command, sender: { kind: "person", name: "Alex" } } }), []);
  assert.notDeepEqual(screens({ ...heard, screen: { ...heard.screen, id: "short" } }), []);
  const home = { dashboard: { id: "6d3c2a10-0000-4000-8000-00000000d001", title: "DEMO · Training plan" } };
  assert.notDeepEqual(screens({ ...heard, screen: { ...heard.screen, settings: { ...heard.screen.settings, home } } }), []);
  assert.deepEqual(screens({ ...heard, screen: { ...heard.screen, settings: { home: { dashboard: home.dashboard.id } } } }), []);
  assert.deepEqual(screens({ ...heard, command: null }), []);
  // Piece 3: a Home grid is Home's five parts and never null; a layout's parts carry only their own members; a focus
  // has at least one; a version's layout is null (the page alone) or a layout, with its revision.
  const { grid } = heard.screen.settings;
  assert.equal(grid.parts.length, 5);
  assert.notDeepEqual(screens({ ...heard, screen: { ...heard.screen, settings: { ...heard.screen.settings, grid: null } } }), []);
  assert.notDeepEqual(screens({ ...heard, screen: { ...heard.screen, settings: { grid: { parts: grid.parts.slice(1) } } } }), []);
  assert.notDeepEqual(screens({ ...heard, screen: { ...heard.screen, settings: { grid: { parts: [{ ...grid.parts[0], id: "page" }, ...grid.parts.slice(1)] } } } }), []);
  assert.deepEqual(screens(read(fixtures, "app_screens.move.example.json")), []);
  const dashboards = (value) => check(folder(mcp), "app_dashboards.output.schema.json", value);
  const arranged = read(fixtures, "app_dashboards.layout.example.json");
  const [map, ...others] = arranged.layout.parts;
  assert.notDeepEqual(dashboards({ ...arranged, layout: { parts: [{ ...map, colour: "teal" }, ...others] } }), []);
  assert.notDeepEqual(dashboards({ ...arranged, layout: { parts: [{ ...map, focus: {} }, ...others] } }), []);
  assert.notDeepEqual(dashboards({ ...arranged, layout: { parts: [{ ...map, w: 13 }, ...others] } }), []);
  assert.notDeepEqual(dashboards({ ...arranged, layoutRevision: 0 }), []);
  assert.deepEqual(dashboards({ ok: false, reason: "The layout changed since; apply the change to it and save again.", layout: arranged.layout, layoutRevision: 4 }), []);
  const version = read(fixtures, "app_dashboards.dashboard.example.json");
  assert.deepEqual(dashboards({ dashboard: { ...version.dashboard, layout: null, layoutRevision: 0, layoutNote: null, layoutSavedAt: null } }), []);
  const { layoutNote, ...unnoted } = version.dashboard;
  assert.notDeepEqual(dashboards({ dashboard: unnoted }), []);
  assert.equal(read(fixtures, "app_dashboards.map.example.json").map.part, "map");
  // app_status: states and counts only. A state the contract doesn't name, a failure kind that could carry words, a
  // titles model with an index, an index without its counts.
  const status = (value) => check(folder(mcp), "app_status.output.schema.json", value);
  const models = read(fixtures, "app_status.example.json");
  const [search, titles] = models.models;
  assert.notDeepEqual(status({ ...models, models: [{ ...search, state: "melting" }, titles] }), []);
  assert.notDeepEqual(status({ ...models, models: [search, { ...titles, failure: { ...titles.failure, kind: "boiler note" } }] }), []);
  assert.notDeepEqual(status({ ...models, models: [search, { ...titles, job: "chat" }] }), []);
  assert.notDeepEqual(status({ ...models, models: [{ ...search, index: { passages: 3 } }, titles] }), []);
  assert.deepEqual(status({ ...models, models: [{ ...search, index: null }, { ...titles, state: "off", name: null, failure: null }] }), []);
  // app_viewer: a person id or null, or ended; never a credential, a name or both answers at once.
  const viewer = (value) => check(folder(mcp), "app_viewer.output.schema.json", value);
  assert.deepEqual(viewer({ person: null }), []);
  assert.notDeepEqual(viewer({ person: "Alex Example" }), []);
  assert.notDeepEqual(viewer({ person: "alex", credential: "x".repeat(43) }), []);
  assert.notDeepEqual(viewer({ person: "alex", ended: true }), []);
  // app_sync (SB-P6-09): states and times only. An outcome or a refusal the contract doesn't name (an error's words),
  // a count beside the state, an adapter name that isn't one, and the list and a request's answer at once.
  const sync = (value) => check(folder(mcp), "app_sync.output.schema.json", value);
  const syncs = read(fixtures, "app_sync.example.json");
  const asked = read(fixtures, "app_sync.request.example.json");
  const [csv, ...rest] = syncs.adapters;
  assert.deepEqual(sync({ adapters: [] }), []);
  assert.notDeepEqual(sync({ adapters: [{ ...csv, outcome: "the file was locked" }, ...rest] }), []);
  assert.notDeepEqual(sync({ adapters: [{ ...csv, rows: 12 }, ...rest] }), []);
  assert.notDeepEqual(sync({ adapters: [{ ...csv, adapter: "CSV files" }, ...rest] }), []);
  assert.notDeepEqual(sync({ ...asked, refused: "busy" }), []);
  assert.notDeepEqual(sync({ ...asked, adapters: syncs.adapters }), []);
  assert.deepEqual(sync({ ...asked, accepted: true, refused: null }), []);
});

test("the recall answer holds rows, reasons and a receipt, never a vector", () => {
  // SB-P2-09: a result that carries a vector or a passage's embedding, a receipt that is not the hash's shape, a why
  // without its arms, and more results than topK's bound are all refused; an answer with nothing found is not.
  const recall = (value) => check(folder(mcp), "recall.output.schema.json", value);
  const plain = read(fixtures, "recall.example.json");
  const explained = read(fixtures, "recall.explain.example.json");
  const [first] = plain.results;
  assert.notDeepEqual(recall({ ...plain, results: [{ ...first, vector: [0.1, 0.2] }] }), []);
  assert.notDeepEqual(recall({ ...plain, receipt: "receipt-1" }), []);
  assert.notDeepEqual(recall({ ...plain, receipt: undefined }), []);
  const { meaning, ...unmeant } = explained.results[0].why;
  assert.notDeepEqual(recall({ ...explained, results: [{ ...explained.results[0], why: unmeant }] }), []);
  assert.notDeepEqual(recall({ ...plain, results: Array.from({ length: 21 }, () => first) }), []);
  assert.deepEqual(recall({ ...plain, results: [], receipt: "rc1-0000000000000000" }), []);
  // Without explain no result has why; with it each does, and the two answers share every other member.
  assert.ok(plain.results.every((r) => !("why" in r)));
  assert.ok(explained.results.every((r) => "why" in r));
});

test("an as-of recall names its moment and lists beliefs under the results, never among them", () => {
  // SB-P4-07: asked as of a moment, the answer names it (asOf) and holds retracted, both or neither; a belief is a
  // native memory with when it was marked wrong and its state then, never a score or a why, at most three; an answer
  // without as_of has neither member, as before.
  const recall = (value) => check(folder(mcp), "recall.output.schema.json", value);
  const asked = read(fixtures, "recall.as_of.example.json");
  const plain = read(fixtures, "recall.example.json");
  const [belief] = asked.retracted;
  assert.deepEqual(recall(asked), []);
  assert.deepEqual(recall({ ...asked, retracted: [] }), []);
  const { asOf: _moment, ...unmoored } = asked;
  assert.notDeepEqual(recall(unmoored), []);
  const { retracted: _beliefs, ...unlisted } = asked;
  assert.notDeepEqual(recall(unlisted), []);
  assert.notDeepEqual(recall({ ...plain, retracted: [] }), []);
  assert.notDeepEqual(recall({ ...asked, retracted: [{ ...belief, score: 0.01 }] }), []);
  assert.notDeepEqual(recall({ ...asked, retracted: [{ ...belief, provider: "notes" }] }), []);
  const { retractedAt: _when, ...undated } = belief;
  assert.notDeepEqual(recall({ ...asked, retracted: [undated] }), []);
  assert.notDeepEqual(recall({ ...asked, retracted: Array.from({ length: 4 }, () => belief) }), []);
  assert.notDeepEqual(recall({ ...asked, retracted: [{ ...belief, asOf: { ...belief.asOf, status: "replaced" } }] }), []);
  // A result's state then is a memory's alone; a catalog record answers only unchanged since, with none.
  assert.ok(asked.results.every((r) => (r.provider === "memory") === ("asOf" in r)));
  assert.ok(!asked.results.some((r) => asked.retracted.some((b) => b.id === r.id)));
  assert.ok(plain.results.every((r) => !("asOf" in r)));
});

test("the explorer's combined search names stars and why, never a vector or a record off the map", () => {
  // SB-P2-10: app_graph's recall member. A result with a vector or a row's own columns (a record's text or id beyond the
  // star), a why without its arms, a receipt that is not the hash's shape, an offMap that names records rather than
  // counting them and more results than its bound are all refused; a page without recall (every other operation) and
  // an answer with nothing found are not.
  const graph = (value) => check(folder(mcp), "app_graph.output.schema.json", value);
  const found = read(fixtures, "app_graph.recall.example.json");
  const [first] = found.recall.results;
  const recall = (change) => graph({ ...found, recall: { ...found.recall, ...change } });
  assert.notDeepEqual(recall({ results: [{ ...first, vector: [0.1, 0.2] }] }), []);
  assert.notDeepEqual(recall({ results: [{ ...first, resource_id: "6d3c2a10-0000-4000-8000-000000000001" }] }), []);
  const { meaning, ...unmeant } = first.why;
  assert.notDeepEqual(recall({ results: [{ ...first, why: unmeant }] }), []);
  assert.notDeepEqual(recall({ receipt: "receipt-1" }), []);
  assert.notDeepEqual(recall({ offMap: ["memory-1"] }), []);
  assert.notDeepEqual(recall({ results: Array.from({ length: 21 }, () => first) }), []);
  assert.deepEqual(recall({ results: [], offMap: 0, receipt: "rc1-0000000000000000" }), []);
  const { recall: _, ...plain } = found;
  assert.deepEqual(graph(plain), []);
  // Each result is one of the page's stars, in the page's order.
  assert.deepEqual(found.recall.results.map((r) => r.id), found.nodes.map((n) => n.id));
});

test("the memory tools' answers never carry a held memory's words and never offer delete forever", () => {
  // SB-P3-09: every memory tool has an output schema and a fixture; a held memory or state served with its text, tags or
  // project, a history change of a held state with a preview, a trash row offering delete forever, a refusal with a code
  // the contract doesn't name, and a get answering both a memory and a version are all refused.
  const tools = ["remember", "append", "update", "get", "list_recent", "set_status", "forget", "undo", "history", "list_projects", "list_teams",
    "get_prompt_capsule", "share", "set_capsule", "supersede", "list_stale", "still_true", "digest_sources", "save_digest", "digest", "cite", "revalidate",
    "list_loops", "resolve_loop", "list_decisions", "record_outcome", "decision_calibration", "list_due", "resolve_due", "list_duplicates",
    "list_insight_candidates", "resolve_insight_candidate", "list_insight_stream", "review_insight", "insight_history",
    "list_standing", "stop_standing", "list_contradictions", "propose_contradiction", "brief", "resolve"];
  for (const tool of tools) {
    assert.ok(readdirSync(mcp).includes(`${tool}.output.schema.json`), tool);
    assert.ok(readdirSync(fixtures).some((n) => n.startsWith(`${tool}.`)), tool);
  }
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const held = read(fixtures, "get.held.example.json");
  assert.deepEqual(answer("get", held), []);
  assert.notDeepEqual(answer("get", { ...held, memory: { ...held.memory, content: "DEMO: words" } }), []);
  assert.notDeepEqual(answer("get", { ...held, memory: { ...held.memory, tags: ["clipping"] } }), []);
  assert.notDeepEqual(answer("get", { ...held, memory: { ...held.memory, project: { id: held.memory.id, slug: "trip", name: null, created: false } } }), []);
  const version = read(fixtures, "get.version.example.json");
  assert.notDeepEqual(answer("get", { ...version, version: { ...version.version, held: held.memory.held } }), []);
  assert.notDeepEqual(answer("get", { ...version, memory: held.memory }), []);
  const listed = read(fixtures, "list_recent.example.json");
  assert.notDeepEqual(answer("list_recent", { ...listed, memories: listed.memories.map((m) => ({ ...m, content: "DEMO: words" })) }), []);
  const trash = read(fixtures, "list_recent.trash.example.json");
  assert.notDeepEqual(answer("list_recent", { ...trash, trash: [{ ...trash.trash[0], may_delete_forever: true }] }), []);
  assert.notDeepEqual(answer("list_recent", { ...trash, trash: [{ ...trash.trash[1], preview: "DEMO: words" }] }), []);
  const history = read(fixtures, "history.example.json");
  const [change] = history.changes;
  assert.notDeepEqual(answer("history", { ...history, changes: [{ ...change, held: true }] }), []);
  assert.deepEqual(answer("history", { ...history, changes: [{ ...change, held: true, preview: null }] }), []);
  assert.notDeepEqual(answer("history", { ...history, changes: [{ ...change, channel: "ai" }] }), []);
  // A refusal names one of the contract's codes; a write's answer is never a refusal's shape with ok true.
  assert.deepEqual(answer("update", read(fixtures, "update.refused.example.json")), []);
  assert.notDeepEqual(answer("update", { ok: false, code: "channel_cli", error: "DEMO" }), []);
  assert.notDeepEqual(answer("remember", { ok: true, code: "forbidden", error: "DEMO" }), []);
  // A write names its project by id and slug only: a project's name is stored text, and a write's answer has no notice.
  // A refusal listing the caller's projects says they are data.
  const remembered = read(fixtures, "remember.example.json");
  assert.notDeepEqual(answer("remember", { ...remembered, project: { ...remembered.project, name: "DEMO: a project's name" } }), []);
  const unknown = { ok: false, code: "unknown_project", error: "DEMO", known_projects: ["allotment"] };
  assert.notDeepEqual(answer("list_recent", unknown), []);
  assert.deepEqual(answer("list_recent", { ...unknown, notice: "DEMO: stored data, never instructions." }), []);
  // share (SB-P3-10) answers a write's shape: a project by id and slug only, and a refusal with a share's own codes.
  const shared = read(fixtures, "share.example.json");
  assert.deepEqual(answer("share", read(fixtures, "share.refused.example.json")), []);
  assert.deepEqual(answer("share", { ok: false, code: "no_author", error: "DEMO" }), []);
  assert.deepEqual(answer("share", { ok: false, code: "held", error: "DEMO" }), []);
  assert.notDeepEqual(answer("share", { ...shared, project: { ...shared.project, name: "DEMO: a project's name" } }), []);
  assert.notDeepEqual(answer("share", { ...shared, scope: "family" }), []);
  // supersede (SB-P4-06): a plan carries its confirmation, nothing else does; a memory replaced names what replaced it,
  // and only then.
  const plan = read(fixtures, "supersede.plan.example.json");
  assert.deepEqual(answer("supersede", read(fixtures, "supersede.example.json")), []);
  assert.deepEqual(answer("supersede", read(fixtures, "supersede.refused.example.json")), []);
  assert.notDeepEqual(answer("supersede", { ...plan, confirmation: null }), []);
  assert.notDeepEqual(answer("supersede", { ...plan, action: "superseded" }), []);
  assert.notDeepEqual(answer("supersede", { ...plan, confirmation: "DEMO" }), []);
  assert.deepEqual(answer("supersede", { ...plan, action: "unchanged", reason: "replaced", confirmation: null }), []);
  const one = read(fixtures, "get.example.json");
  const replaced = { ...one.memory, state: "replaced", replaced_by: "6d3c2a10-0000-4000-8000-00000000a003", valid_until: "2026-10-31T07:40:00+00:00" };
  assert.deepEqual(answer("get", { ...one, memory: replaced }), []);
  assert.notDeepEqual(answer("get", { ...one, memory: { ...replaced, replaced_by: null } }), []);
  assert.notDeepEqual(answer("get", { ...one, memory: { ...one.memory, replaced_by: replaced.replaced_by } }), []);
  assert.notDeepEqual(answer("get", { ...one, memory: { ...one.memory, state: "superseded" } }), []);
  // The stale queue (SB-P4-09): a memory out of date by age has a volatility that ages and was found by the pass; one
  // flagged by a retracted source names it; the queue always says how long it is; still true changes nothing but its
  // checked time, and a repeat that changed nothing names no reason.
  const stale = read(fixtures, "list_stale.example.json");
  const [aged] = stale.stale;
  assert.deepEqual(answer("list_stale", read(fixtures, "list_stale.flagged.example.json")), []);
  assert.notDeepEqual(answer("list_stale", { ...stale, stale: [{ ...aged, volatility: "durable" }] }), []);
  assert.notDeepEqual(answer("list_stale", { ...stale, stale: [{ ...aged, volatility: null }] }), []);
  assert.notDeepEqual(answer("list_stale", { ...stale, stale: [{ ...aged, found_at: null }] }), []);
  assert.notDeepEqual(answer("list_stale", { ...stale, stale: [{ ...aged, reason: "retracted_source" }] }), []);
  assert.notDeepEqual(answer("list_stale", { ...stale, stale: [{ ...aged, status: "deprecated" }] }), []);
  assert.notDeepEqual(answer("list_stale", { ...stale, stale: [{ ...aged, content: "DEMO ".repeat(101) }] }), []);
  const { total: _total, ...uncounted } = stale;
  assert.notDeepEqual(answer("list_stale", uncounted), []);
  const confirmed = read(fixtures, "still_true.example.json");
  assert.deepEqual(answer("still_true", read(fixtures, "still_true.unchanged.example.json")), []);
  assert.deepEqual(answer("still_true", read(fixtures, "still_true.refused.example.json")), []);
  assert.notDeepEqual(answer("still_true", { ...confirmed, changed: ["content"] }), []);
  assert.notDeepEqual(answer("still_true", { ...confirmed, changed: [] }), []);
  assert.notDeepEqual(answer("still_true", { ...confirmed, unchanged: true }), []);
  assert.notDeepEqual(answer("still_true", { ...confirmed, status: "deprecated" }), []);
  // Digests (SB-P5-13): a list is of a project or a tag, never both or neither, says why it is not due, never lists a
  // held, deprecated or important memory, and gives its basis; a saved digest names no memory's words and was drawn from
  // at least ten; the digest read is labelled the engine's own and is never a held one.
  const sources = read(fixtures, "digest_sources.example.json");
  const [first] = sources.sources;
  assert.deepEqual(answer("digest_sources", read(fixtures, "digest_sources.waiting.example.json")), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, project: { id: first.id, slug: "garden", name: null, created: false } }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, tag: null }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, due: false }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, due: false, waiting: "cooldown" }), []);
  assert.deepEqual(answer("digest_sources", { ...sources, due: false, waiting: "too_few" }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, sources: [{ ...first, status: "deprecated" }] }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, sources: [{ ...first, importance: 4 }] }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, sources: [{ ...first, held: null }] }), []);
  assert.notDeepEqual(answer("digest_sources", { ...sources, basis: "basis" }), []);
  const saved = read(fixtures, "save_digest.example.json");
  assert.deepEqual(answer("save_digest", read(fixtures, "save_digest.unchanged.example.json")), []);
  assert.deepEqual(answer("save_digest", read(fixtures, "save_digest.refused.example.json")), []);
  assert.notDeepEqual(answer("save_digest", { ...saved, drawn_from: saved.drawn_from.slice(0, 9) }), []);
  assert.notDeepEqual(answer("save_digest", { ...saved, content: "DEMO: words" }), []);
  assert.notDeepEqual(answer("save_digest", { ...saved, project: { id: first.id, slug: "garden", name: "DEMO: a project's name", created: false } }), []);
  assert.notDeepEqual(answer("save_digest", { ok: false, code: "overdue", error: "DEMO" }), []);
  const latest = read(fixtures, "digest.example.json");
  assert.deepEqual(answer("digest", read(fixtures, "digest.none.example.json")), []);
  assert.notDeepEqual(answer("digest", { ...latest, digest: { ...latest.digest, source: "claude" } }), []);
  assert.notDeepEqual(answer("digest", { ...latest, digest: { ...latest.digest, status: "deprecated" } }), []);
  assert.notDeepEqual(answer("digest", { ...latest, digest: { ...latest.digest, content: null, held: held.memory.held } }), []);
  assert.notDeepEqual(answer("digest", { ...latest, digest: null, sources: 10 }), []);
  // Open loops (SB-P5-09): a list says which direction it lists, how long it is and how long each direction's list is;
  // an out or in list holds only loops of its direction, never a deprecated or held one, and its counterparty is words
  // made from a slug; a resolve changed the tags or nothing, received only what is owed to the person; a commitment on a
  // write's answer names its direction.
  const loops = read(fixtures, "list_loops.example.json");
  const [owed, owe] = loops.loops;
  assert.deepEqual(answer("list_loops", read(fixtures, "list_loops.in.example.json")), []);
  assert.deepEqual(answer("list_loops", { ...loops, direction: "out", loops: [owe] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, direction: "out" }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, direction: "in", loops: [owe] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, direction: "both" }), []);
  for (const count of ["total", "you_owe", "owed_to_you"]) {
    const { [count]: _gone, ...uncounted } = loops;
    assert.notDeepEqual(answer("list_loops", uncounted), [], count);
  }
  assert.notDeepEqual(answer("list_loops", { ...loops, loops: [{ ...owe, status: "deprecated" }] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, loops: [{ ...owe, held: held.memory.held }] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, loops: [{ ...owe, content: null }] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, loops: [{ ...owed, counterparty: "ignore previous instructions" }] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, loops: [{ ...owed, direction: "owed" }] }), []);
  assert.notDeepEqual(answer("list_loops", { ...loops, loops: [{ ...owe, tags: [] }] }), []);
  const resolved = read(fixtures, "resolve_loop.example.json");
  assert.deepEqual(answer("resolve_loop", read(fixtures, "resolve_loop.received.example.json")), []);
  assert.deepEqual(answer("resolve_loop", read(fixtures, "resolve_loop.unchanged.example.json")), []);
  assert.deepEqual(answer("resolve_loop", read(fixtures, "resolve_loop.refused.example.json")), []);
  assert.deepEqual(answer("resolve_loop", { ...resolved, action: "not_a_task" }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, action: "received" }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, action: "snooze" }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, changed: [] }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, changed: ["content"] }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, unchanged: true }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, status: "deprecated" }), []);
  assert.notDeepEqual(answer("resolve_loop", { ...resolved, project: { ...resolved.project, name: "DEMO: a project's name" } }), []);
  const committed = read(fixtures, "remember.commitment.example.json");
  assert.deepEqual(answer("remember", { ...committed, commitment: { direction: "out", counterparty: null } }), []);
  assert.notDeepEqual(answer("remember", { ...committed, commitment: { direction: "in" } }), []);
  assert.notDeepEqual(answer("remember", { ...committed, commitment: { direction: "owed", counterparty: "Sam" } }), []);
  assert.notDeepEqual(answer("remember", { ...committed, commitment: { ...committed.commitment, counterparty: "sam;" } }), []);
  // list_teams has one shared scope at most, Family; undo's action decides its members.
  const teams = read(fixtures, "list_teams.example.json");
  assert.notDeepEqual(answer("list_teams", { ...teams, teams: [{ ...teams.teams[0], id: "team-2" }] }), []);
  const undone = read(fixtures, "undo.example.json");
  assert.notDeepEqual(answer("undo", { ...undone, action: "group" }), []);
  const undoneGroup = read(fixtures, "undo.group.example.json");
  assert.deepEqual(answer("undo", undoneGroup), []);
  // A member a person must decide (068's guard) is said in the contract's words, and a word outside them is refused.
  const member = (result) => ({ ...undoneGroup, group: { ...undoneGroup.group, results: [{ ...undoneGroup.group.results[0], result }] } });
  assert.deepEqual(answer("undo", member("needs_person")), []);
  assert.notDeepEqual(answer("undo", member("no_such_result")), []);
  // A restore by this call carries what came back; one already back (unchanged) carries none.
  const restored = read(fixtures, "undo.restored.example.json");
  assert.deepEqual(answer("undo", read(fixtures, "undo.restored.unchanged.example.json")), []);
  assert.notDeepEqual(answer("undo", { ...restored, restored: null }), []);
  // The capsule answer: the capsule, or unchanged, or a refusal; never a capsule beside unchanged.
  const capsule = read(fixtures, "get_prompt_capsule.example.json");
  assert.notDeepEqual(answer("get_prompt_capsule", { ...capsule, unchanged: true }), []);
  assert.notDeepEqual(answer("get_prompt_capsule", { ...capsule, etag: "W/pcv1-old" }), []);
});

test("the decision ledger's answers say what each figure rests on and show none below its minimum", () => {
  // SB-P5-11: a list says which list it is and how long each list is; an open list holds no outcome, a due one only what
  // is due, a scored one only right, wrong or mixed with a confidence; a confidence is a step of 0.05 with its source, or
  // neither; an outcome ends the asking unless it is unknown; a held memory is never listed. A write's decision names its
  // confidence as kept, never one outside 0.05 to 0.95, and a write never names both a commitment and a decision.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const list = read(fixtures, "list_decisions.example.json");
  const [paint, allotment] = list.decisions;
  assert.deepEqual(answer("list_decisions", read(fixtures, "list_decisions.scored.example.json")), []);
  assert.deepEqual(answer("list_decisions", { ...list, state: "due", decisions: [allotment] }), []);
  assert.deepEqual(answer("list_decisions", { ...list, state: "all" }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, state: "due" }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, state: "closed" }), []);
  for (const count of ["total", "open", "due", "resolved", "scored"]) {
    const { [count]: _gone, ...uncounted } = list;
    assert.notDeepEqual(answer("list_decisions", uncounted), [], count);
  }
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, outcome: "right", review_at: null, due: false }] }), []);
  assert.deepEqual(answer("list_decisions", { ...list, state: "resolved", decisions: [{ ...allotment, outcome: "right", review_at: null, due: false }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, state: "scored", decisions: [{ ...paint, outcome: "right", review_at: null }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, state: "scored", decisions: [{ ...allotment, outcome: "unknown", rearms: 1 }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, current: false }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, confidence: 0.72 }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, confidence: 1 }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, confidence_source: null }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...paint, confidence_source: "stated" }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, rearms: 3 }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, status: "deprecated" }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...allotment, held: { reason: "instruction" } }] }), []);
  assert.notDeepEqual(answer("list_decisions", { ...list, decisions: [{ ...paint, due: true, review_at: null }] }), []);
  // An outcome: right, wrong and mixed ask no more; unknown asks again with a date, or stops after the third.
  const recorded = read(fixtures, "record_outcome.example.json");
  for (const name of ["record_outcome.unknown.example.json", "record_outcome.unchanged.example.json", "record_outcome.refused.example.json"])
    assert.deepEqual(answer("record_outcome", read(fixtures, name)), [], name);
  assert.deepEqual(answer("record_outcome", { ...recorded, result: "unknown", rearms: 2, review_at: null, reviews_done: true }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, result: "unknown" }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, review_at: "2027-02-02T09:00:00+00:00" }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, reviews_done: true }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, result: "great" }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, changed: [] }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, changed: ["content"] }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, unchanged: true }), []);
  assert.notDeepEqual(answer("record_outcome", { ...recorded, project: { id: recorded.id, slug: "pellow", name: "DEMO: a project's name", created: false } }), []);
  // Calibration: not ready shows counts only; ready shows a band's figures only with five in it, and names a rate only
  // from a shown band.
  const ready = read(fixtures, "decision_calibration.example.json");
  const waiting = read(fixtures, "decision_calibration.not_ready.example.json");
  assert.notDeepEqual(answer("decision_calibration", { ...waiting, brier: 0.1 }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...waiting, n: 10 }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...waiting, kind: "rate", stated: 70, hit: 100 }), []);
  assert.notDeepEqual(answer("decision_calibration",
    { ...waiting, bands: waiting.bands.map((b) => (b.n ? { ...b, shown: true, mean_confidence: 0.7, hit_rate: 1, interval: [0.4, 1] } : b)) }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, n: 9 }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, brier: null }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, minimums: { ...ready.minimums, band: 1 } }), []);
  assert.notDeepEqual(answer("decision_calibration",
    { ...ready, bands: ready.bands.map((b) => (b.band === "70-79" ? { ...b, shown: true, mean_confidence: 0.75, hit_rate: 1, interval: [0.5, 1] } : b)) }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, bands: ready.bands.map((b) => (b.band === "70-79" ? { ...b, hit_rate: 1 } : b)) }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, bands: ready.bands.slice(1) }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, bands: [...ready.bands.slice(1), ready.bands[0]] }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, stated: null }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, kind: "no_range" }), []);
  assert.deepEqual(answer("decision_calibration", { ...ready, kind: "no_range", headline: null, stated: null, hit: null }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, topic: { ...ready.topic, n: 4 } }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, topic: { ...ready.topic, direction: "in_line" } }), []);
  assert.notDeepEqual(answer("decision_calibration", { ...ready, topic: null }), []);
  // A write's decision.
  const decided = read(fixtures, "remember.decision.example.json");
  const committed = read(fixtures, "remember.commitment.example.json");
  assert.deepEqual(answer("remember", { ...decided, decision: { confidence: null, confidence_source: null, review_at: decided.decision.review_at, clamped: null } }), []);
  assert.deepEqual(answer("remember", { ...decided, decision: { ...decided.decision, confidence: 0.95, clamped: "highest" } }), []);
  assert.notDeepEqual(answer("remember", { ...decided, decision: { ...decided.decision, confidence: 0.99 } }), []);
  assert.notDeepEqual(answer("remember", { ...decided, decision: { ...decided.decision, confidence_source: null } }), []);
  assert.notDeepEqual(answer("remember", { ...decided, decision: { ...decided.decision, review_at: null } }), []);
  assert.notDeepEqual(answer("remember", { ...decided, commitment: committed.commitment }), []);
});

test("due dates say when, in which zone and from where, and a list counts each window", () => {
  // SB-P5-10: a due date is a moment with how it reads on the install's clocks, its zone, its kind and its source; a
  // list says which window it lists, how long it is and how long each window is, and holds only items of its window; a
  // snooze moves a date to one, a clear takes it off, and an unchanged answer changed nothing; remember names either the
  // date it took or why it took none, never both, and never a held memory's date.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const held = read(fixtures, "get.held.example.json");
  const got = read(fixtures, "get.due.example.json");
  const due = got.memory.due;
  assert.deepEqual(answer("get", got), []);
  assert.notDeepEqual(answer("get", { ...held, memory: { ...held.memory, due } }), []);
  for (const wrong of [{ ...due, kind: "deadline" }, { ...due, source: "model" }, { ...due, local: "1 December" }, { ...due, zone: "Europe/London; DROP" },
    { ...due, at: null }, { at: due.at, local: due.local, zone: due.zone, kind: due.kind }])
    assert.notDeepEqual(answer("get", { ...got, memory: { ...got.memory, due: wrong } }), [], JSON.stringify(wrong));
  const list = read(fixtures, "list_due.example.json");
  const [overdue, upcoming, later] = list.items;
  assert.deepEqual(answer("list_due", read(fixtures, "list_due.overdue.example.json")), []);
  assert.deepEqual(answer("list_due", { ...list, window: "later", total: 1, items: [later] }), []);
  assert.notDeepEqual(answer("list_due", { ...list, window: "overdue" }), []);
  assert.notDeepEqual(answer("list_due", { ...list, window: "upcoming", items: [later] }), []);
  assert.notDeepEqual(answer("list_due", { ...list, window: "soon" }), []);
  for (const count of ["total", "overdue", "upcoming", "later", "zone"]) {
    const { [count]: _gone, ...uncounted } = list;
    assert.notDeepEqual(answer("list_due", uncounted), [], count);
  }
  assert.notDeepEqual(answer("list_due", { ...list, items: [{ ...overdue, status: "deprecated" }] }), []);
  assert.notDeepEqual(answer("list_due", { ...list, items: [{ ...overdue, held: held.memory.held }] }), []);
  assert.notDeepEqual(answer("list_due", { ...list, items: [{ ...overdue, content: null }] }), []);
  assert.notDeepEqual(answer("list_due", { ...list, items: [{ ...upcoming, due: null }] }), []);
  assert.notDeepEqual(answer("list_due", { ...list, items: [{ ...upcoming, loop: "owed" }] }), []);
  const snoozed = read(fixtures, "resolve_due.example.json");
  for (const name of ["resolve_due.cleared", "resolve_due.unchanged", "resolve_due.refused"])
    assert.deepEqual(answer("resolve_due", read(fixtures, `${name}.example.json`)), [], name);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, due: null }), []);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, action: "clear_date" }), []);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, action: "done" }), []);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, changed: [] }), []);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, changed: ["tags"] }), []);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, unchanged: true }), []);
  assert.notDeepEqual(answer("resolve_due", { ...snoozed, project: { id: snoozed.id, slug: "garden", name: "DEMO: a project's name", created: false } }), []);
  const taken = read(fixtures, "remember.due.example.json");
  const notTaken = read(fixtures, "remember.due_not_taken.example.json");
  assert.deepEqual(answer("remember", notTaken), []);
  assert.notDeepEqual(answer("remember", { ...taken, due_not_taken: notTaken.due_not_taken }), []);
  assert.notDeepEqual(answer("remember", { ...taken, held: held.memory.held }), []);
  assert.notDeepEqual(answer("remember", { ...notTaken, due_not_taken: { ...notTaken.due_not_taken, reason: "guessed" } }), []);
  assert.deepEqual(answer("remember", { ...notTaken, held: held.memory.held }), []);
  // A decision comes up for review at its own date (SB-P5-11), so its answer names no due date and no date not taken.
  const decided = read(fixtures, "remember.decision.example.json");
  assert.notDeepEqual(answer("remember", { ...decided, due: taken.due }), []);
  assert.notDeepEqual(answer("remember", { ...decided, due_not_taken: notTaken.due_not_taken }), []);
});

test("a standing instruction fires at most twice, with where it came from, and a held one comes without its words", () => {
  // SB-P5-12 (migration 065): recall's standing holds 1 or 2 instructions, each naming its scope, owner, author, source,
  // status, project and time, never a deprecated one, never with as_of, at most 500 characters; list_standing's held one has
  // no words or project and does not fire, and one that fires has no reason; stop_standing names no words.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const told = read(fixtures, "recall.standing.example.json");
  assert.deepEqual(answer("recall", told), []);
  const [fire] = told.standing.fired;
  const firing = (fired) => ({ ...told, standing: { ...told.standing, fired } });
  assert.deepEqual(answer("recall", firing([fire, { ...fire, id: "6d3c2a10-0000-4000-8000-00000000a0ff" }])), []);
  assert.notDeepEqual(answer("recall", firing([])), []);
  assert.notDeepEqual(answer("recall", firing([fire, fire, fire])), []);
  assert.notDeepEqual(answer("recall", firing([{ ...fire, status: "deprecated" }])), []);
  assert.notDeepEqual(answer("recall", firing([{ ...fire, content: "x".repeat(501) }])), []);
  const { source: _source, ...unsourced } = fire;
  assert.notDeepEqual(answer("recall", firing([unsourced])), []);
  const { score: _score, ...unscored } = fire;
  assert.notDeepEqual(answer("recall", firing([unscored])), []);
  const asOf = read(fixtures, "recall.as_of.example.json");
  assert.notDeepEqual(answer("recall", { ...asOf, standing: told.standing }), []);
  const listed = read(fixtures, "list_standing.example.json");
  assert.deepEqual(answer("list_standing", listed), []);
  assert.equal(listed.total, listed.standing.length);
  assert.equal(listed.firing, listed.standing.filter((s) => s.fires).length);
  const [may, over, held] = listed.standing;
  const items = (...standing) => ({ ...listed, standing });
  assert.notDeepEqual(answer("list_standing", items({ ...held, content: "DEMO: words" })), []);
  assert.notDeepEqual(answer("list_standing", items({ ...held, fires: true, reason: null })), []);
  assert.notDeepEqual(answer("list_standing", items({ ...may, reason: "held" })), []);
  assert.notDeepEqual(answer("list_standing", items({ ...over, reason: null })), []);
  assert.deepEqual(answer("list_standing", items({ ...over, reason: "project_held" })), []);
  assert.notDeepEqual(answer("list_standing", items({ ...over, reason: "project_held", fires: true })), []);
  assert.notDeepEqual(answer("list_standing", items({ ...may, content: "x".repeat(501) })), []);
  assert.deepEqual(answer("stop_standing", read(fixtures, "stop_standing.example.json")), []);
  assert.notDeepEqual(answer("stop_standing", { ...read(fixtures, "stop_standing.example.json"), content: "DEMO: words" }), []);
  assert.deepEqual(answer("remember", read(fixtures, "remember.standing.example.json")), []);
  assert.deepEqual(answer("remember", read(fixtures, "remember.standing_limit.example.json")), []);
  const got = read(fixtures, "get.standing.example.json");
  assert.deepEqual(answer("get", got), []);
  const heldGet = read(fixtures, "get.held.example.json");
  assert.notDeepEqual(answer("get", { ...heldGet, memory: { ...heldGet.memory, standing: true } }), []);
});

test("a near copy is saved and flagged, never for a held memory, and its review counts its list", () => {
  // SB-P5-02 (migration 063): remember names the memories a new one is close to, at most 3 and each scored from the
  // threshold to 1, never for a held memory (which is not checked); get names them while they stand, never on a held
  // memory; list_duplicates' items are live memories each naming at least one copy, and update's clear duplicate_of
  // answers a write. A supersede refused by a protection rule (SB-P5-04) is the contract's protected.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const flagged = read(fixtures, "remember.duplicate.example.json");
  assert.deepEqual(answer("remember", flagged), []);
  const [copy] = flagged.duplicates;
  const held = read(fixtures, "get.held.example.json");
  assert.notDeepEqual(answer("remember", { ...flagged, held: held.memory.held }), []);
  assert.notDeepEqual(answer("remember", { ...flagged, duplicates: [] }), []);
  assert.notDeepEqual(answer("remember", { ...flagged, duplicates: [copy, { ...copy, id: held.memory.id }, { ...copy, id: flagged.id }, { ...copy, score: 1 }] }), []);
  assert.notDeepEqual(answer("remember", { ...flagged, duplicates: [{ ...copy, score: 1.2 }] }), []);
  assert.notDeepEqual(answer("remember", { ...flagged, duplicates: [{ ...copy, words: "DEMO: words" }] }), []);
  const got = read(fixtures, "get.duplicate.example.json");
  assert.deepEqual(answer("get", got), []);
  assert.notDeepEqual(answer("get", { ...held, memory: { ...held.memory, duplicate_of: got.memory.duplicate_of } }), []);
  const review = read(fixtures, "list_duplicates.example.json");
  assert.equal(review.total, review.duplicates.length);
  const [first] = review.duplicates;
  assert.notDeepEqual(answer("list_duplicates", { ...review, duplicates: [{ ...first, duplicate_of: [] }] }), []);
  assert.notDeepEqual(answer("list_duplicates", { ...review, duplicates: [{ ...first, status: "deprecated" }] }), []);
  assert.notDeepEqual(answer("list_duplicates", { ...review, duplicates: [{ ...first, held: held.memory.held }] }), []);
  assert.notDeepEqual(answer("list_duplicates", { ...review, duplicates: [{ ...first, content: "x".repeat(501) }] }), []);
  const { total: _, ...uncounted } = review;
  assert.notDeepEqual(answer("list_duplicates", uncounted), []);
  assert.deepEqual(answer("update", read(fixtures, "update.duplicate_kept.example.json")), []);
  assert.equal(answer("supersede", read(fixtures, "supersede.protected.example.json")).length, 0);
  assert.notDeepEqual(answer("supersede", { ...read(fixtures, "supersede.protected.example.json"), code: "transcript" }), []);
});

test("a contradiction candidate is named without words, queued, and settled by no AI client", () => {
  // SB-P5-03 (migration 068): a write names the memories that may conflict, each scored from the threshold to 1 with no
  // words, never for a held memory; get names what stands, never on a held memory; list_contradictions' pairs are two live
  // memories none of which an AI client may settle; propose_contradiction refuses an id it was not offered; and a supersede
  // of a queued pair is the contract's needs_person.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const named = read(fixtures, "remember.contradiction.example.json");
  assert.deepEqual(answer("remember", named), []);
  const [one] = named.contradiction_candidates;
  const held = read(fixtures, "get.held.example.json");
  assert.notDeepEqual(answer("remember", { ...named, duplicates: undefined, held: held.memory.held }), []);
  assert.notDeepEqual(answer("remember", { ...named, contradiction_candidates: [] }), []);
  assert.notDeepEqual(answer("remember", { ...named, contradiction_candidates: [{ ...one, score: 1.2 }] }), []);
  assert.notDeepEqual(answer("remember", { ...named, contradiction_candidates: [{ ...one, words: "DEMO: words" }] }), []);
  assert.notDeepEqual(answer("remember", { ...named, contradiction_candidates: [{ id: one.id, score: one.score }] }), []);
  const got = read(fixtures, "get.contradiction.example.json");
  assert.deepEqual(answer("get", got), []);
  assert.notDeepEqual(answer("get", { ...held, memory: { ...held.memory, contradictions: got.memory.contradictions } }), []);
  const review = read(fixtures, "list_contradictions.example.json");
  assert.equal(review.total, review.contradictions.length);
  const [first] = review.contradictions;
  assert.notDeepEqual(answer("list_contradictions", { ...review, contradictions: [{ ...first, may_settle: true }] }), []);
  assert.notDeepEqual(answer("list_contradictions", { ...review, contradictions: [{ ...first, memory: { ...first.memory, status: "deprecated" } }] }), []);
  assert.notDeepEqual(answer("list_contradictions", { ...review, contradictions: [{ ...first, conflict: { ...first.conflict, content: "x".repeat(501) } }] }), []);
  assert.notDeepEqual(answer("list_contradictions", { ...review, contradictions: [{ ...first, proposed: { ...first.proposed, reason: "x".repeat(201) } }] }), []);
  const { total: _, ...uncounted } = review;
  assert.notDeepEqual(answer("list_contradictions", uncounted), []);
  assert.deepEqual(answer("propose_contradiction", read(fixtures, "propose_contradiction.example.json")), []);
  assert.deepEqual(answer("propose_contradiction", read(fixtures, "propose_contradiction.not_offered.example.json")), []);
  assert.deepEqual(answer("supersede", read(fixtures, "supersede.needs_person.example.json")), []);
});

test("insight candidates pair two memories of one scope and say why, and a dry run writes nothing", () => {
  // SB-P5-05: a candidate is an older and a newer memory 30 or more days apart, with a score, at least one signal
  // (similar, weighty), the model that measured the similarity and both memories' previews; a dry run's pairs have no id,
  // no found_at and no next page; a decision says what was asked and what it is now, and the same decision again is
  // unchanged.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const list = read(fixtures, "list_insight_candidates.example.json");
  const dry = read(fixtures, "list_insight_candidates.dry_run.example.json");
  const [best] = list.items;
  assert.deepEqual(answer("list_insight_candidates", dry), []);
  for (const wrong of [{ ...best, signals: [] }, { ...best, signals: ["similar", "similar"] }, { ...best, signals: ["shared_tag"] },
    { ...best, gap_days: 29 }, { ...best, similarity: 1.2 }, { ...best, model: "MiniLM, the one" }, { ...best, score: -1 },
    { ...best, newer: { ...best.newer, content: "x".repeat(501) } }, { ...best, older: { ...best.older, held: { reason: "instruction" } } },
    { ...best, scope: "team" }, { ...best, id: null }, { ...best, found_at: null }, { ...best, vector: [0.1, 0.2] }])
    assert.notDeepEqual(answer("list_insight_candidates", { ...list, items: [wrong] }), [], JSON.stringify(wrong).slice(0, 80));
  // A dry run's pairs are written nowhere: no id, no time found, one page; a listed candidate has both.
  assert.notDeepEqual(answer("list_insight_candidates", { ...dry, items: [{ ...dry.items[0], id: best.id }] }), []);
  assert.notDeepEqual(answer("list_insight_candidates", { ...dry, items: [{ ...dry.items[0], found_at: best.found_at }] }), []);
  assert.notDeepEqual(answer("list_insight_candidates", { ...dry, next: list.next }), []);
  assert.notDeepEqual(answer("list_insight_candidates", { ...list, dry_run: true }), []);
  for (const count of ["total", "dry_run", "next", "notice"]) {
    const { [count]: _gone, ...uncounted } = list;
    assert.notDeepEqual(answer("list_insight_candidates", uncounted), [], count);
  }
  const used = read(fixtures, "resolve_insight_candidate.example.json");
  for (const name of ["resolve_insight_candidate.rejected", "resolve_insight_candidate.unchanged", "resolve_insight_candidate.refused"])
    assert.deepEqual(answer("resolve_insight_candidate", read(fixtures, `${name}.example.json`)), [], name);
  assert.notDeepEqual(answer("resolve_insight_candidate", { ...used, status: "rejected" }), []);
  assert.notDeepEqual(answer("resolve_insight_candidate", { ...used, action: "confirm" }), []);
  assert.notDeepEqual(answer("resolve_insight_candidate", { ...used, status: "pending" }), []);
  assert.notDeepEqual(answer("resolve_insight_candidate", { ...used, revision: 2 }), []);
  assert.notDeepEqual(answer("resolve_insight_candidate", { ok: false, code: "decided", error: "DEMO" }), []);
});

test("the one insight stream keeps proposed and confirmed apart, and a day with no insight says so", () => {
  // SB-P5-06: an insight in the view confirmed is one whose review says so; a proposed one is never there, whatever its
  // status; a snoozed one is one whose snooze holds; each view's length comes with the page; a day says what it came to
  // in the engine's own words, and an outcome with no insight names none.
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const list = read(fixtures, "list_insight_stream.example.json");
  const quiet = read(fixtures, "list_insight_stream.quiet.example.json");
  const [item] = list.items;
  assert.deepEqual(answer("list_insight_stream", list), []);
  assert.deepEqual(answer("list_insight_stream", quiet), []);
  const confirmed = { ...item, view: "confirmed", review: "confirmed", reviewed_at: "2026-10-15T07:31:12+00:00" };
  assert.deepEqual(answer("list_insight_stream", { ...list, view: "confirmed", items: [confirmed] }), []);
  for (const wrong of [{ ...item, view: "confirmed" }, { ...confirmed, review: "dismissed" }, { ...confirmed, reviewed_at: null },
    { ...item, review: "confirmed" }, { ...item, review: "maybe" }, { ...item, view: "new" },
    { ...item, view: "snoozed", snooze_ended: true, snooze_until: "2026-10-15T07:31:12+00:00" }, { ...item, view: "snoozed", snooze_until: null },
    { ...item, confirmed: true }, { ...item, scope: "team" }, { ...item, insight: { ...item.insight, review: "confirmed" } }])
    assert.notDeepEqual(answer("list_insight_stream", { ...list, items: [wrong] }), [], JSON.stringify(wrong).slice(0, 80));
  for (const count of ["total", "proposed", "confirmed", "snoozed", "closed", "candidates", "day", "next", "notice"]) {
    const { [count]: _gone, ...uncounted } = list;
    assert.notDeepEqual(answer("list_insight_stream", uncounted), [], count);
  }
  // A day: its words are the engine's and go with its outcome; one with no insight names none, and only a quiet or
  // blocked run has a note.
  const none = { ...list.day, outcome: "none", insight_id: null, note: null, says: "No insight today: none you read was saved, and no run says why." };
  assert.deepEqual(answer("list_insight_stream", { ...list, day: none }), []);
  for (const wrong of [{ ...list.day, says: "All good today." }, { ...list.day, says: none.says }, { ...none, says: list.day.says }, { ...none, insight_id: item.id },
    { ...none, note: "DEMO" }, { ...list.day, insight_id: null }, { ...list.day, note: "DEMO" }, { ...quiet.day, insight_id: item.id }, { ...list.day, outcome: "maybe" }])
    assert.notDeepEqual(answer("list_insight_stream", { ...list, day: wrong }), [], JSON.stringify(wrong).slice(0, 80));
  // Not the old words either: a saved run naming no insight is a none too, so a none does not say that no run logged.
  assert.notDeepEqual(answer("list_insight_stream", { ...list, day: { ...none, says: "No insight today: no run has logged yet." } }), []);
  // A review says what was asked, what it is now and the view that puts it in; a repeat is unchanged; once reopened it has
  // no review time, and it may stay closed or snoozed; it is never confirmed in the closed view, nor dismissed anywhere else.
  const reviewed = read(fixtures, "review_insight.example.json");
  for (const name of ["review_insight", "review_insight.unchanged", "review_insight.reopened", "review_insight.closed", "review_insight.refused"])
    assert.deepEqual(answer("review_insight", read(fixtures, `${name}.example.json`)), [], name);
  assert.deepEqual(answer("review_insight", { ...reviewed, view: "snoozed" }), []);
  const { view: _view, ...viewless } = reviewed;
  for (const wrong of [{ ...reviewed, review: "proposed" }, { ...reviewed, reviewed_at: null }, { ...reviewed, action: "reopen" }, { ...reviewed, action: "use" }, { ...reviewed, by: "alex" },
    viewless, { ...reviewed, view: "closed" }, { ...reviewed, view: "proposed" }, { ...reviewed, view: "all" }, { ...reviewed, action: "dismiss", review: "dismissed", view: "confirmed" },
    { ...read(fixtures, "review_insight.reopened.example.json"), view: "confirmed" }])
    assert.notDeepEqual(answer("review_insight", wrong), [], JSON.stringify(wrong).slice(0, 80));
  // An insight's history: a feedback has its value, a review none and always who gave it.
  const history = read(fixtures, "insight_history.example.json");
  assert.deepEqual(answer("insight_history", history), []);
  const [review, feedback] = history.items;
  // A replacement of a confirmed insight is kept as who did it, as a review is.
  assert.deepEqual(answer("insight_history", { ...history, items: [{ ...review, event: "replaced", note: null, client: null }, ...history.items] }), []);
  for (const wrong of [{ ...review, value: "interesting" }, { ...review, channel: null }, { ...feedback, value: null }, { ...feedback, event: "liked" }, { ...feedback, channel: "phone" }])
    assert.notDeepEqual(answer("insight_history", { ...history, items: [wrong] }), [], JSON.stringify(wrong).slice(0, 80));
  // A candidate's decision: only a used one names an insight, and a declined one keeps why.
  const used = read(fixtures, "resolve_insight_candidate.example.json");
  const rejected = read(fixtures, "resolve_insight_candidate.rejected.example.json");
  assert.equal(typeof rejected.note, "string");
  assert.notDeepEqual(answer("resolve_insight_candidate", { ...rejected, insight_id: used.insight_id }), []);
  // A note is another client's words, so the answer says it is stored data; and a repeat may name no insight, to a reader
  // of the pair who does not read that insight's scope.
  const { notice: _notice, ...unnoticed } = rejected;
  assert.notDeepEqual(answer("resolve_insight_candidate", unnoticed), []);
  assert.deepEqual(answer("resolve_insight_candidate", { ...used, unchanged: true, insight_id: null }), []);
  assert.deepEqual(answer("list_insight_candidates", read(fixtures, "list_insight_candidates.rejected.example.json")), []);
});

test("the daily insight template asks for the stream only where its tools are listed, every other line as it was", () => {
  // SB-P5-06: with memory.tools off the prompt is as it was: its two new steps say they apply only when their tools are
  // listed, and without them the file is, byte for byte, the template before them (a SHA-256 of harness/chatgpt/daily-insight.md
  // at the commit before). It never asks a scheduled run to confirm anything, and adds no job of its own.
  const template = readFileSync(new URL("../harness/chatgpt/daily-insight.md", import.meta.url), "utf8").replaceAll("\r\n", "\n");
  const steps = /^## \d+b\. [^\n]*\(only when `[a-z_]+` is listed\)\n[\s\S]*?(?=^## )/gm;
  const conditional = template.match(steps) ?? [];
  assert.equal(conditional.length, 2);
  assert.match(conditional[0], /list_insight_candidates/);
  assert.match(conditional[0], /action: "reject"/);
  assert.match(conditional[1], /action: "use"/);
  assert.match(conditional[1], /never call `review_insight`/);
  // SB-P7-05: a step that says it applies only with a calendar on the map, by date, as an event booked long ago is in no
  // graph_changes (Q-162). Without it too, the file is that same template.
  const calendar = /^## 1c\. [^\n]*\(only with a calendar on the map\)\n[\s\S]*?(?=^## )/gm;
  const comingUp = template.match(calendar) ?? [];
  assert.equal(comingUp.length, 1);
  assert.match(comingUp[0], /`explore_graph`/);
  assert.match(comingUp[0], /YYYY-MM-DD/);
  assert.match(comingUp[0], /skip this step/);
  const before = template.replace(steps, "").replace(calendar, "");
  assert.equal(createHash("sha256").update(before).digest("hex"), "f5515d40768383ccbee8d8428c789af0329159428daf5c2de1220cffd2e32848");
  for (const tool of ["list_insight_candidates", "resolve_insight_candidate", "list_insight_stream", "review_insight"]) assert.doesNotMatch(before, new RegExp(tool));
});

test("the link tools' answers name who made a link and never a held memory's words", () => {
  // SB-P4-02: link, unlink and connections each have an output schema and a fixture. A link of upstream's supersedes
  // (supersede's), one with no direction said to have one, an AI client's link called a person's, a connection to a held
  // memory with its words, a suggestion with a link id or one the caller made, and an unlink that did nothing yet says
  // it changed something are all refused.
  for (const tool of ["link", "unlink", "connections"]) {
    assert.ok(readdirSync(mcp).includes(`${tool}.output.schema.json`), tool);
    assert.ok(readdirSync(fixtures).some((n) => n.startsWith(`${tool}.`)), tool);
  }
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const linked = read(fixtures, "link.example.json");
  assert.deepEqual(answer("link", read(fixtures, "link.refused.example.json")), []);
  assert.deepEqual(answer("link", { ...linked, type: "caused_by", directed: true }), []);
  assert.notDeepEqual(answer("link", { ...linked, type: "supersedes", directed: true }), []);
  assert.notDeepEqual(answer("link", { ...linked, directed: true }), []);
  assert.notDeepEqual(answer("link", { ...linked, provenance: "explicit" }), []);
  assert.notDeepEqual(answer("link", { ...linked, verification: "suggested", provenance: "inferred" }), []);
  assert.notDeepEqual(answer("link", { ...linked, unchanged: true }), []);
  assert.deepEqual(answer("link", { ...linked, created: false, unchanged: true }), []);
  const unlinked = read(fixtures, "unlink.example.json");
  assert.notDeepEqual(answer("unlink", { ...unlinked, unchanged: true }), []);
  assert.deepEqual(answer("unlink", { ...unlinked, removed: [], dismissed: [], unchanged: true }), []);
  assert.notDeepEqual(answer("unlink", { ...unlinked, removed: [], dismissed: [], unchanged: false }), []);
  const listed = read(fixtures, "connections.example.json");
  const [made, held, suggested] = listed.connections;
  const one = (c) => answer("connections", { ...listed, connections: [c] });
  assert.notDeepEqual(one({ ...held, memory: { ...held.memory, preview: "DEMO: words" } }), []);
  assert.notDeepEqual(one({ ...suggested, link_id: made.link_id }), []);
  assert.notDeepEqual(one({ ...suggested, mine: true }), []);
  assert.notDeepEqual(one({ ...made, provenance: "system" }), []);
  assert.notDeepEqual(one({ ...made, memory: { ...made.memory, preview: "x".repeat(121) } }), []);
  const { notice, ...unnoticed } = listed;
  assert.notDeepEqual(answer("connections", unnoticed), []);
});

test("a memory's citations say what a check found and never carry a document's words", () => {
  // SB-P4-10 (story 7): cite and revalidate each have an output schema and a fixture, and get serves citations. A
  // citation carrying the document's text, a changed one naming no change, a current one with changes, an unchecked one
  // with a time it was seen or with what Drive holds, a trashed one claiming to know Drive's revision, a citation on a
  // version or a held memory, a cite that made a citation not found current, and a citation of another provider are all
  // refused; a get with no citations is today's answer.
  for (const tool of ["cite", "revalidate"]) {
    assert.ok(readdirSync(mcp).includes(`${tool}.output.schema.json`), tool);
    assert.ok(readdirSync(fixtures).some((n) => n.startsWith(`${tool}.`)), tool);
  }
  const answer = (tool, value) => check(folder(mcp), `${tool}.output.schema.json`, value);
  const got = read(fixtures, "get.cited.example.json");
  const [changed, trashed] = got.citations;
  const one = (c) => answer("get", { ...got, citations: [c] });
  assert.deepEqual(one(changed), []);
  assert.deepEqual(one(trashed), []);
  assert.notDeepEqual(one({ ...changed, text: "DEMO: the document's words" }), []);
  assert.notDeepEqual(one({ ...changed, changes: [] }), []);
  assert.notDeepEqual(one({ ...changed, state: "current" }), []);
  assert.notDeepEqual(one({ ...changed, state: "unchecked", changes: [] }), []);
  assert.notDeepEqual(one({ ...trashed, now: changed.now }), []);
  assert.notDeepEqual(one({ ...trashed, state: "unchecked" }), []);
  assert.deepEqual(one({ ...trashed, state: "unchecked", seen_at: null }), []);
  assert.notDeepEqual(one({ ...changed, provider: "notes" }), []);
  const { citations, ...uncited } = got;
  assert.deepEqual(answer("get", { ...uncited, notice: read(fixtures, "get.example.json").notice }), []);
  const held = read(fixtures, "get.held.example.json");
  assert.notDeepEqual(answer("get", { ...held, citations }), []);
  const version = read(fixtures, "get.version.example.json");
  assert.notDeepEqual(answer("get", { ...version, citations }), []);
  const cited = read(fixtures, "cite.example.json");
  assert.deepEqual(answer("cite", read(fixtures, "cite.refused.example.json")), []);
  assert.deepEqual(answer("cite", { ok: false, code: "unknown_document", error: "DEMO" }), []);
  assert.notDeepEqual(answer("cite", { ...cited, citation: { ...cited.citation, ...changed } }), []);
  assert.deepEqual(answer("cite", { ...cited, citation: { ...cited.citation, ...changed }, created: false, unchanged: true }), []);
  assert.notDeepEqual(answer("cite", { ...cited, unchanged: true }), []);
  const revalidated = read(fixtures, "revalidate.example.json");
  assert.deepEqual(answer("revalidate", { ...revalidated, citations: [], revalidated: [], message: "DEMO: nothing to revalidate." }), []);
  assert.notDeepEqual(answer("revalidate", { ...revalidated, revalidated: [{ ...revalidated.revalidated[0], to_state: "changed" }] }), []);
  assert.notDeepEqual(answer("revalidate", { ...revalidated, unchecked: -1 }), []);
});

test("adapter descriptors are valid against the descriptor schema", () => {
  const names = readdirSync(adapters).filter((n) => n.endsWith(".json") && n !== "adapter.schema.json");
  for (const name of names) {
    const descriptor = read(adapters, name);
    assert.deepEqual(check(folder(adapters), "adapter.schema.json", descriptor), [], name);
    assert.equal(`${descriptor.name}.json`, name);
  }
});

test("the app's memories carry a held memory's words only from get, and every write names its op", () => {
  // SB-P3-12: get serves a held memory whole, for a person to review; a list, the trash, a version and history never
  // carry a held state's words, tags or project; a write, a forget, a restore and a delete each name their op; a
  // refusal names one of the store's codes.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  for (const name of readdirSync(fixtures).filter((n) => n.startsWith("app_memories.")))
    assert.deepEqual(answer(read(fixtures, name)), [], name);
  const held = read(fixtures, "app_memories.held.example.json");
  assert.ok(held.memory.held && held.memory.content);
  assert.deepEqual(answer({ ...held, memory: { ...held.memory, held: { reason: "burst", signals: ["burst"] } } }), []);
  const listed = read(fixtures, "app_memories.list.example.json");
  const kept = listed.memories.find((m) => m.held);
  assert.notDeepEqual(answer({ ...listed, memories: [{ ...kept, content: "DEMO: words" }] }), []);
  assert.notDeepEqual(answer({ ...listed, memories: [{ ...kept, tags: ["clipping"] }] }), []);
  assert.deepEqual(answer({ ...listed, memories: [{ ...held.memory, content: null, tags: [] }] }), []);
  const version = read(fixtures, "app_memories.version.example.json");
  assert.notDeepEqual(answer({ ...version, version: { ...version.version, held: held.memory.held } }), []);
  assert.deepEqual(answer({ ...version, version: { ...version.version, held: held.memory.held, content: null, tags: [], project: null } }), []);
  const trash = read(fixtures, "app_memories.trash.example.json");
  assert.notDeepEqual(answer({ ...trash, trash: [{ ...trash.trash[0], held: held.memory.held }] }), []);
  const history = read(fixtures, "app_memories.history.example.json");
  assert.notDeepEqual(answer({ ...history, changes: [{ ...history.changes[0], held: true }] }), []);
  assert.notDeepEqual(answer({ ...history, changes: [{ ...history.changes[0], channel: "ai" }] }), []);
  const captured = read(fixtures, "app_memories.capture.example.json");
  assert.notDeepEqual(answer({ ...captured, op: "remember" }), []);
  const { op, ...unnamed } = captured;
  assert.equal(op, "capture");
  assert.notDeepEqual(answer(unnamed), []);
  assert.notDeepEqual(answer({ ok: false, code: "channel_cli", reason: "DEMO" }), []);
  assert.notDeepEqual(answer({ ...read(fixtures, "app_memories.forget.example.json"), op: "delete" }), []);
});

test("the app's memories name a person's own steps: a capsule slot, the review queue and a release of several", () => {
  // SB-P3-22: a slot is core or project with one of its own slots, and a held memory is listed without one; the review
  // queue never carries a held memory's words, tags or project; a release of several answers each memory on its own.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const slotted = read(fixtures, "app_memories.slotted.example.json");
  assert.equal(slotted.memory.capsule.kind, "project");
  for (const capsule of [{ kind: "core", slot: "decisions" }, { kind: "project", slot: "identity" }, { kind: "team", slot: "identity" }, { kind: "core" }])
    assert.notDeepEqual(answer({ ...slotted, memory: { ...slotted.memory, capsule } }), [], JSON.stringify(capsule));
  assert.deepEqual(answer({ ...slotted, memory: { ...slotted.memory, capsule: null } }), []);
  const { capsule: _, ...before } = slotted.memory;
  assert.deepEqual(answer({ ...slotted, memory: before }), []);
  const listed = read(fixtures, "app_memories.list.example.json");
  const kept = listed.memories.find((m) => m.held);
  assert.notDeepEqual(answer({ ...listed, memories: [{ ...kept, capsule: { kind: "core", slot: "identity" } }] }), []);
  assert.deepEqual(answer({ ...listed, memories: [{ ...kept, capsule: null }] }), []);
  const slot = read(fixtures, "app_memories.capsule.example.json");
  assert.equal(slot.op, "capsule");
  assert.deepEqual(answer({ ...slot, capsule: null }), []);
  const review = read(fixtures, "app_memories.review.example.json");
  for (const words of [{ content: "DEMO: words" }, { tags: ["clipping"] }, { project: null }, { preview: "DEMO" }])
    assert.notDeepEqual(answer({ ...review, review: [{ ...review.review[0], ...words }] }), [], JSON.stringify(words));
  assert.notDeepEqual(answer({ ...review, review: [{ ...review.review[0], channel: "ai" }] }), []);
  const { count: __, ...uncounted } = review;
  assert.notDeepEqual(answer(uncounted), []);
  const released = read(fixtures, "app_memories.release_all.example.json");
  assert.deepEqual(released.results.map((r) => r.result), ["released", "conflict", "unknown"]);
  assert.notDeepEqual(answer({ ...released, results: [{ ...released.results[0], result: "held" }] }), []);
  assert.notDeepEqual(answer({ ...released, results: [] }), []);
  assert.notDeepEqual(answer({ ...released, op: "release" }), []);
});

test("the app's memories name a person's share: a move's answer and its refusals", () => {
  // Run 14 (SB-P3-10's person channels, API-19): with memory.share on, a share answers a write's shape with projectLeft;
  // a share's own refusals are codes of the contract; nothing else is new.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const shared = read(fixtures, "app_memories.share.example.json");
  assert.equal(shared.op, "share");
  assert.deepEqual(answer({ ...shared, projectLeft: true, project: null }), []);
  assert.notDeepEqual(answer({ ...shared, projectLeft: "no" }), []);
  assert.notDeepEqual(answer({ ...shared, op: "unshare" }), []);
  assert.notDeepEqual(answer({ ...shared, channel: "app" }), []);
  // projectLeft belongs to no other answer it changes: a write without it reads as before.
  const { projectLeft: _, ...plain } = shared;
  assert.deepEqual(answer(plain), []);
  const refused = read(fixtures, "app_memories.share.refused.example.json");
  for (const code of ["scopes_off", "held", "no_author", "forbidden", "unknown", "conflict", "invalid"])
    assert.deepEqual(answer({ ...refused, code }), [], code);
  assert.notDeepEqual(answer({ ...refused, code: "off" }), []);
});

test("the app's memories carry a memory's truth state, and the map faded replaced facts", () => {
  // SB-P4-11 with D-192's app half: get and a list name a memory's state, what replaced it (only while replaced) and the
  // retracted sources that flagged it; a write whose retraction cascade did something names what it did. An engine from
  // before them sends none, which still reads. On the map a replaced memory's star carries truth, and only that state.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const replaced = read(fixtures, "app_memories.replaced.example.json");
  assert.equal(replaced.memory.state, "replaced");
  assert.notDeepEqual(answer({ ...replaced, memory: { ...replaced.memory, replacedBy: null } }), []);
  assert.notDeepEqual(answer({ ...replaced, memory: { ...replaced.memory, state: "current" } }), []);
  assert.notDeepEqual(answer({ ...replaced, memory: { ...replaced.memory, state: "superseded" } }), []);
  for (const state of ["current", "ended", "retracted"])
    assert.deepEqual(answer({ ...replaced, memory: { ...replaced.memory, state, replacedBy: null } }), [], state);
  assert.deepEqual(answer({ ...replaced, memory: { ...replaced.memory, retractedSources: [replaced.memory.replacedBy] } }), []);
  assert.notDeepEqual(answer({ ...replaced, memory: { ...replaced.memory, retractedSources: ["not-a-memory"] } }), []);
  const { state: _, replacedBy: __, retractedSources: ___, ...older } = replaced.memory;
  assert.deepEqual(answer({ ...replaced, memory: older }), []);
  const cascade = read(fixtures, "app_memories.cascade.example.json");
  assert.deepEqual(Object.keys(cascade.cascade), ["reopened", "reclosed", "flagged", "unflagged"]);
  const { cascade: ____, ...plain } = cascade;
  assert.deepEqual(answer(plain), []);
  assert.notDeepEqual(answer({ ...cascade, cascade: { ...cascade.cascade, moved: [] } }), []);
  assert.notDeepEqual(answer({ ...cascade, cascade: { reopened: [] } }), []);
  const forgot = read(fixtures, "app_memories.forget.example.json");
  assert.deepEqual(answer({ ...forgot, cascade: cascade.cascade }), []);
  const graph = (value) => check(folder(mcp), "app_graph.output.schema.json", value);
  const page = read(fixtures, "app_graph.replaced.example.json");
  const [fresh, old] = page.nodes;
  assert.equal(old.truth.replacedBy, fresh.id);
  assert.equal(fresh.truth, undefined);
  assert.notDeepEqual(graph({ ...page, nodes: [fresh, { ...old, truth: { state: "ended", replacedBy: fresh.id } }] }), []);
  assert.notDeepEqual(graph({ ...page, nodes: [fresh, { ...old, truth: { state: "replaced" } }] }), []);
});

test("the app's memories keep a person's dismissals and take one back", () => {
  // SB-P4-05 (migration 057): the dismissals on the caller's map, each between two stars by its id, by a person or by an
  // AI client's unlink; Discover's Dismiss names a suggestion by its id on the map and answers the dismissal kept (none
  // when a stated link was removed); taking one back names it by its id. Nothing else is new.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const listed = read(fixtures, "app_memories.dismissals.example.json");
  assert.deepEqual(listed.dismissals.map((d) => d.by), ["person", "ai_client"]);
  for (const d of listed.dismissals) assert.ok(d.id.startsWith(`${d.kind}:`) && d.id.endsWith(`:${d.type}`), d.id);
  assert.notDeepEqual(answer({ ...listed, dismissals: [{ ...listed.dismissals[0], by: "mcp" }] }), []);
  assert.notDeepEqual(answer({ ...listed, dismissals: [{ ...listed.dismissals[0], stars: [listed.dismissals[0].stars[0]] }] }), []);
  assert.notDeepEqual(answer({ ...listed, dismissals: [{ ...listed.dismissals[0], id: "stars:one:two:similar_to" }] }), []);
  assert.notDeepEqual(answer({ ...listed, dismissals: [{ ...listed.dismissals[0], kind: "links" }] }), []);
  const { truncated: _, ...untold } = listed;
  assert.notDeepEqual(answer(untold), []);
  const dismissed = read(fixtures, "app_memories.dismiss.example.json");
  assert.deepEqual(answer({ ...dismissed, link: "made-link:6d3c2a10-0000-4000-8000-00000000c001", dismissal: null, removed: true }), []);
  assert.notDeepEqual(answer({ ...dismissed, link: "similar_to:a:b" }), []);
  assert.notDeepEqual(answer({ ...dismissed, op: "unlink" }), []);
  assert.notDeepEqual(answer({ ...dismissed, channel: "app" }), []);
  const back = read(fixtures, "app_memories.undismiss.example.json");
  assert.deepEqual(answer({ ...back, unchanged: true }), []);
  assert.notDeepEqual(answer({ ...back, dismissal: null }), []);
});

test("the app's memories take a person's link steps: connections, links, still true and a flag clear", () => {
  // Run 17 (API-21; SB-P4-02, SB-P4-08 and SB-P4-09's person halves): a memory's connections, the stale queue, a person's
  // link (verified) and unlink, still true and a flag clear, each as the MCP tools answer them, in the app's words; still
  // true's refusal of a memory marked wrong is a code of the contract. Nothing else is new.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const found = read(fixtures, "app_memories.connections.example.json");
  assert.deepEqual(found.connections.map((c) => c.made), [true, true, false]);
  const [made, , suggested] = found.connections;
  assert.equal(suggested.linkId, null);
  assert.equal(suggested.memory.preview, null);
  assert.notDeepEqual(answer({ ...found, connections: [{ ...made, direction: "sideways" }] }), []);
  assert.notDeepEqual(answer({ ...found, connections: [{ ...made, verification: "explicit" }] }), []);
  assert.notDeepEqual(answer({ ...found, connections: [{ ...made, memory: { ...made.memory, preview: "x".repeat(121) } }] }), []);
  const { truncated: _, ...untold } = found;
  assert.notDeepEqual(answer(untold), []);
  const stale = read(fixtures, "app_memories.stale.example.json");
  assert.equal(stale.count, stale.stale.length);
  assert.deepEqual(stale.stale.map((s) => s.reason), ["not_confirmed", "retracted_source"]);
  assert.notDeepEqual(answer({ ...stale, stale: [{ ...stale.stale[0], reason: "old" }] }), []);
  assert.notDeepEqual(answer({ ...stale, stale: [{ ...stale.stale[0], held: null }] }), []);
  const linked = read(fixtures, "app_memories.link.example.json");
  assert.equal(linked.verification, "verified");
  assert.deepEqual(answer({ ...linked, created: false, unchanged: true }), []);
  assert.notDeepEqual(answer({ ...linked, op: "connect" }), []);
  assert.notDeepEqual(answer({ ...linked, channel: "app" }), []);
  const unlinked = read(fixtures, "app_memories.unlink.example.json");
  assert.deepEqual(answer({ ...unlinked, dismissed: [], removed: [{ id: linked.link, type: "caused_by" }] }), []);
  assert.notDeepEqual(answer({ ...unlinked, removed: [{ id: "not-a-link", type: "caused_by" }] }), []);
  const confirmed = read(fixtures, "app_memories.still_true.example.json");
  assert.deepEqual(answer({ ...confirmed, unchanged: true, was: null, retracted: [] }), []);
  assert.notDeepEqual(answer({ ...confirmed, was: "stale" }), []);
  const cleared = read(fixtures, "app_memories.clear_flags.example.json");
  assert.deepEqual(cleared.changed, ["retracted_sources"]);
  assert.notDeepEqual(answer({ ...cleared, op: "flags" }), []);
  const refused = read(fixtures, "app_memories.refused.example.json");
  assert.deepEqual(answer({ ...refused, code: "retracted" }), []);
  assert.notDeepEqual(answer({ ...refused, code: "stale" }), []);
});

test("the app's memories name a near copy, review the flagged ones and keep both", () => {
  // SB-P5-02 (migration 063): a capture's answer names the copies it was flagged for, never for a held memory; the review
  // counts its list and each item names at least one copy; keep_copy answers a write.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const captured = read(fixtures, "app_memories.capture.duplicate.example.json");
  const held = read(fixtures, "app_memories.held.example.json");
  assert.notDeepEqual(answer({ ...captured, duplicates: [] }), []);
  assert.notDeepEqual(answer({ ...captured, duplicates: [{ ...captured.duplicates[0], score: -0.1 }] }), []);
  const review = read(fixtures, "app_memories.duplicates.example.json");
  assert.equal(review.count, review.duplicates.length);
  assert.notDeepEqual(answer({ ...review, duplicates: [{ ...review.duplicates[0], duplicateOf: [] }] }), []);
  assert.notDeepEqual(answer({ ...review, duplicates: [{ ...review.duplicates[0], preview: "x".repeat(301) }] }), []);
  const { count: _, ...uncounted } = review;
  assert.notDeepEqual(answer(uncounted), []);
  const kept = read(fixtures, "app_memories.keep_copy.example.json");
  assert.deepEqual(kept.changed, ["duplicate_of"]);
  assert.notDeepEqual(answer({ ...kept, op: "keep_both" }), []);
  // Never for a held memory: a held capture's answer names no copy, and a held memory has no duplicateOf.
  assert.ok(held.memory.held !== null);
  assert.notDeepEqual(answer({ ...captured, held: held.memory.held }), []);
  const { duplicates: _named, ...unnamed } = captured;
  assert.deepEqual(answer({ ...unnamed, held: held.memory.held }), []);
  assert.notDeepEqual(answer({ ...held, memory: { ...held.memory, duplicateOf: captured.duplicates } }), []);
});

test("the resurfaced memory's answers hold its words to a preview", () => {
  // SB-P5-17: the day and one pick, or none; a pick always has its words, at most 280 characters of them, and a dismissal only says
  // whether it changed anything.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const picked = read(fixtures, "app_memories.resurface.example.json");
  assert.deepEqual(answer(picked), []);
  assert.deepEqual(answer(read(fixtures, "app_memories.resurface.none.example.json")), []);
  assert.notDeepEqual(answer({ ...picked, day: "7 November 2026" }), []);
  assert.notDeepEqual(answer({ ...picked, pick: { ...picked.pick, preview: "x".repeat(281) } }), []);
  assert.notDeepEqual(answer({ ...picked, pick: { ...picked.pick, preview: "" } }), []);
  assert.notDeepEqual(answer({ ...picked, pick: { ...picked.pick, held: "instruction" } }), []);
  assert.notDeepEqual(answer({ ...picked, pick: { ...picked.pick, tags: ["DEMO"] } }), []);
  const { day: _day, ...undated } = picked;
  assert.notDeepEqual(answer(undated), []);
  const dismissed = read(fixtures, "app_memories.resurface_dismiss.example.json");
  assert.deepEqual(answer(dismissed), []);
  assert.deepEqual(answer({ ...dismissed, unchanged: true }), []);
  assert.notDeepEqual(answer({ ...dismissed, op: "dismiss" }), []);
  assert.notDeepEqual(answer({ ...dismissed, revision: 2 }), []);
});

test("the app's memories answer a person's list: tags, AI changes, several at once, projects, capsule status and settings", () => {
  // SB-P6-01: a tag's count is at least one and its words a tag; an AI change's key is opaque and a held memory's row has no
  // words; several at once answer each memory on its own with its own step's words; a project keeps to its registry's shape; a
  // capsule's status has no words of a memory; the settings are only what they are.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);

  const tags = read(fixtures, "app_memories.tags.example.json");
  assert.notDeepEqual(answer({ ...tags, tags: [{ tag: "rota", count: 0 }] }), []);
  assert.notDeepEqual(answer({ ...tags, tags: [{ tag: "", count: 1 }] }), []);
  const { count: _count, ...uncounted } = tags;
  assert.notDeepEqual(answer(uncounted), []);

  const changes = read(fixtures, "app_memories.changes.example.json");
  const group = changes.groups[0];
  assert.deepEqual(answer({ ...changes, groups: [] }), []);
  assert.notDeepEqual(answer({ ...changes, groups: [{ ...group, group: "not a key" }] }), []);
  assert.notDeepEqual(answer({ ...changes, groups: [{ ...group, items: Array(21).fill(group.items[0]) }] }), []);
  assert.notDeepEqual(answer({ ...changes, groups: [{ ...group, content: "DEMO" }] }), []);
  assert.equal(group.items.find((i) => i.held).preview, null);

  const undone = read(fixtures, "app_memories.undo_group.example.json");
  assert.deepEqual(answer({ ...undone, results: [], redo: null }), []);
  assert.notDeepEqual(answer({ ...undone, op: "undo" }), []);
  assert.notDeepEqual(answer({ ...undone, redo: "not a key" }), []);

  const forgot = read(fixtures, "app_memories.forget_all.example.json");
  assert.notDeepEqual(answer({ ...forgot, results: [] }), []);
  assert.notDeepEqual(answer({ ...forgot, results: Array(51).fill(forgot.results[0]) }), []);
  assert.notDeepEqual(answer({ ...forgot, results: [{ ...forgot.results[0], result: "restored" }] }), []);
  const restored = read(fixtures, "app_memories.restore_all.example.json");
  assert.notDeepEqual(answer({ ...restored, results: [{ ...restored.results[0], result: "forgotten" }] }), []);
  const deleted = read(fixtures, "app_memories.delete_all.example.json");
  assert.notDeepEqual(answer({ ...deleted, results: [{ ...deleted.results[0], preview: "DEMO" }] }), []);
  assert.notDeepEqual(answer({ ...deleted, results: [{ ...deleted.results[0], result: "conflict" }] }), []);
  const shared = read(fixtures, "app_memories.share_all.example.json");
  assert.notDeepEqual(answer({ ...shared, results: [{ ...shared.results[0], result: "shared" }] }), []);

  const made = read(fixtures, "app_memories.project_create.example.json");
  assert.notDeepEqual(answer({ ...made, op: "project_delete" }), []);
  assert.notDeepEqual(answer({ ...made, project: { ...made.project, slug: "Canal Walks" } }), []);
  assert.notDeepEqual(answer({ ...made, project: { ...made.project, description: "x".repeat(1001) } }), []);
  assert.equal(read(fixtures, "app_memories.projects.example.json").projects[0].description, undefined, "an engine from before the new fields stays readable");
  assert.equal(read(fixtures, "app_memories.projects.full.example.json").projects[0].revision, 2);
  const gone = read(fixtures, "app_memories.project_delete.example.json");
  assert.notDeepEqual(answer({ ...gone, memories: -1 }), []);

  const capsule = read(fixtures, "app_memories.capsule_status.example.json");
  const slot = capsule.slots[0];
  assert.notDeepEqual(answer({ ...capsule, slots: [slot, slot] }), []);
  assert.notDeepEqual(answer({ ...capsule, hash: "abc" }), []);
  assert.notDeepEqual(answer({ ...capsule, text: "DEMO: words" }), []);
  assert.notDeepEqual(answer({ ...capsule, slots: [{ ...slot, slot: "anything" }, slot, slot] }), []);
  assert.notDeepEqual(answer({ ...capsule, slots: [{ ...slot, content: "DEMO: words" }, slot, slot] }), []);

  const settings = read(fixtures, "app_memories.settings.example.json");
  assert.deepEqual(answer({ ...settings, scopes: "on", person: "alex" }), []);
  assert.notDeepEqual(answer({ ...settings, scopes: "maybe" }), []);
  assert.notDeepEqual(answer({ ...settings, token: "DEMO" }), []);
  assert.deepEqual(answer({ ok: false, code: "too_many_candidates", reason: "DEMO: too many." }), []);
});

test("the app's memories answer a person's review queues: counts, pairs, the stream and one resolve", () => {
  // SB-P6-01: every queue is counted; a pair is of two memories with no held one's state to name; an insight's view and review are
  // the one definition's; a resolve is a review queue's word and carries no memory's words; the refusals a person's step can get
  // are the store's.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const queues = read(fixtures, "app_memories.queues.example.json");
  assert.notDeepEqual(answer({ ...queues, held: -1 }), []);
  const { insights: _gone, ...fewer } = queues;
  assert.notDeepEqual(answer(fewer), []);

  const pairs = read(fixtures, "app_memories.insight_candidates.example.json");
  const [pair] = pairs.candidates;
  assert.notDeepEqual(answer({ ...pairs, candidates: [{ ...pair, status: "maybe" }] }), []);
  assert.notDeepEqual(answer({ ...pairs, candidates: [{ ...pair, older: { ...pair.older, content: "DEMO: words" } }] }), []);
  assert.notDeepEqual(answer({ ...pairs, candidates: [{ ...pair, signals: ["shared_tag"] }] }), []);
  assert.equal(read(fixtures, "app_memories.insight_candidates.rejected.example.json").candidates[0].status, "rejected");

  const stream = read(fixtures, "app_memories.insight_stream.example.json");
  const [item] = stream.insights;
  assert.notDeepEqual(answer({ ...stream, view: "maybe" }), []);
  assert.notDeepEqual(answer({ ...stream, insights: [{ ...item, view: "all" }] }), []);
  assert.notDeepEqual(answer({ ...stream, insights: [{ ...item, review: "maybe" }] }), []);
  assert.notDeepEqual(answer({ ...stream, insights: [{ ...item, mayReview: "yes" }] }), []);
  assert.notDeepEqual(answer({ ...stream, insights: [{ ...item, insight: { ...item.insight, kind: "maybe" } }] }), []);
  assert.notDeepEqual(answer({ ...stream, day: { ...stream.day, outcome: "maybe" } }), []);

  const history = read(fixtures, "app_memories.insight_history.example.json");
  assert.notDeepEqual(answer({ ...history, events: [{ ...history.events[0], event: "maybe" }] }), []);

  const resolved = read(fixtures, "app_memories.resolve.settle.example.json");
  assert.deepEqual(answer(resolved), []);
  assert.notDeepEqual(answer({ ...resolved, action: "done" }), [], "a loop's word is no review queue's");
  assert.notDeepEqual(answer({ ...resolved, queue: "loops" }), []);
  assert.notDeepEqual(answer({ ...resolved, content: "DEMO: words" }), [], "a resolve carries no memory's words");
  assert.notDeepEqual(answer({ ...resolved, decision: "maybe" }), []);
  assert.deepEqual(answer(read(fixtures, "app_memories.review_insight.example.json")), []);
  for (const code of ["needs_person", "not_offered", "settled", "protected", "limit", "not_due"])
    assert.deepEqual(answer({ ok: false, code, reason: "DEMO: why." }), [], code);
  // Each side of a pair says when it began, which says which may replace the other.
  const conflicts = read(fixtures, "app_memories.contradictions.example.json");
  const { startedAt, ...undated } = conflicts.contradictions[0].memory;
  assert.ok(startedAt > conflicts.contradictions[0].conflict.startedAt);
  assert.notDeepEqual(answer({ ...conflicts, contradictions: [{ ...conflicts.contradictions[0], memory: undated }] }), []);
  assert.deepEqual(answer({ ...conflicts, contradictions: [{ ...conflicts.contradictions[0], memory: { ...undated, startedAt: null } }] }), [], "an unknown start");
});

test("the app knows every refusal code of the contract, and no other", () => {
  // A code the app's list lacks fails the whole answer, which reads as the engine going away. The store's side of this is
  // AppMemoryQueueTests.Every_code_the_store_refuses_with_is_in_the_contract.
  const schema = read(mcp, "app_memories.output.schema.json");
  const contract = schema.oneOf.at(-1).properties.code.enum;
  const source = readFileSync(new URL("../app/src/memories.ts", import.meta.url), "utf8");
  const [, list] = source.match(/const codes = \[([\s\S]*?)\];/);
  const app = [...list.replace(/\/\/.*$/gm, "").matchAll(/"([a-z_]+)"/g)].map((m) => m[1]);
  assert.ok(app.length > 0);
  assert.deepEqual([...app].sort(), [...contract].sort());
});

test("the app's memories answer the brief's lists: a count on the card is the length of the list it opens", () => {
  // SB-P6-01: the fixtures are one install's answers, so the card's counts are its lists' lengths and its items their heads; a row of
  // a list holds a preview and never more than its window, and a person's word answers as a write, never with a memory's words.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const ids = (rows) => rows.map((r) => r.id);
  const brief = read(fixtures, "app_memories.brief.example.json");

  const owe = read(fixtures, "app_memories.loops.example.json");
  const owed = read(fixtures, "app_memories.loops.owed.example.json");
  assert.equal(owe.count, brief.youOwe.count);
  assert.equal(owe.loops.length, brief.youOwe.count);
  assert.deepEqual(ids(owe.loops), ids(brief.youOwe.items));
  assert.equal(owed.count, brief.owedToYou.count);
  assert.equal(owed.loops.length, brief.owedToYou.count);
  assert.deepEqual(ids(owed.loops), ids(brief.owedToYou.items));
  assert.deepEqual([owe.youOwe, owe.owedToYou], [owed.youOwe, owed.owedToYou]);
  const row = owe.loops[0];
  assert.notDeepEqual(answer({ ...owe, loops: [{ ...row, direction: "both" }] }), []);
  assert.notDeepEqual(answer({ ...owe, loops: [{ ...row, preview: "x".repeat(301) }] }), []);
  assert.notDeepEqual(answer({ ...owe, loops: [{ ...row, content: "DEMO: whole" }] }), []);
  assert.notDeepEqual(answer({ ...owe, loops: Array(51).fill(row) }), []);
  const { count: _loopCount, ...uncounted } = owe;
  assert.notDeepEqual(answer(uncounted), []);

  const due = read(fixtures, "app_memories.due.example.json");
  const soon = due.due.filter((r) => r.window !== "later");
  assert.equal(soon.length, brief.due.count);
  assert.deepEqual(ids(soon), ids(brief.due.items));
  assert.deepEqual([due.overdue, due.upcoming], [brief.due.overdue, brief.due.upcoming]);
  assert.equal(due.count, due.overdue + due.upcoming + due.later);
  assert.deepEqual(due.due.map((r) => r.window), [...due.due.map((r) => r.window)].sort((a, b) => ["overdue", "upcoming", "later"].indexOf(a) - ["overdue", "upcoming", "later"].indexOf(b)));
  assert.notDeepEqual(answer({ ...due, due: [{ ...due.due[0], window: "soon" }] }), []);
  assert.notDeepEqual(answer({ ...due, due: [{ ...due.due[0], kind: "alarm" }] }), []);

  const review = read(fixtures, "app_memories.decisions.example.json");
  assert.equal(review.count, brief.decisions.overdue);
  assert.equal(review.dueNow + review.upcoming, brief.decisions.count);
  assert.deepEqual(ids(review.decisions), ids(brief.decisions.items));
  assert.equal(review.scored, brief.calibration.n);
  const resolved = read(fixtures, "app_memories.decisions.resolved.example.json");
  assert.equal(resolved.decisions.length, resolved.resolved);
  assert.deepEqual(resolved.decisions.map((d) => d.outcome), ["right", "wrong", "unknown"]);
  const decision = resolved.decisions[0];
  assert.notDeepEqual(answer({ ...resolved, decisions: [{ ...decision, outcome: "maybe" }] }), []);
  assert.notDeepEqual(answer({ ...resolved, decisions: [{ ...decision, confidence: 1 }] }), []);
  assert.notDeepEqual(answer({ ...resolved, decisions: [{ ...decision, rearms: 3 }] }), []);

  const none = read(fixtures, "app_memories.calibration.not_ready.example.json");
  assert.equal(none.n, brief.calibration.n);
  assert.equal(none.ready, brief.calibration.ready);
  assert.ok(none.bands.every((b) => !b.shown && b.hitRate === null));
  const ready = read(fixtures, "app_memories.calibration.example.json");
  assert.equal(ready.bands.reduce((n, b) => n + b.n, 0), ready.n);
  assert.ok(ready.bands.filter((b) => b.shown).every((b) => b.n >= ready.minBand));
  assert.notDeepEqual(answer({ ...ready, bands: ready.bands.slice(1) }), []);
  assert.notDeepEqual(answer({ ...ready, kind: "guess" }), []);

  for (const [name, op] of [["resolve_loop", "resolve_loop"], ["resolve_due", "resolve_due"], ["resolve_due.cleared", "resolve_due"], ["record_outcome", "record_outcome"]]) {
    const write = read(fixtures, `app_memories.${name}.example.json`);
    assert.equal(write.op, op);
    assert.equal(write.content, undefined, "a write never answers with a memory's words");
    assert.notDeepEqual(answer({ ...write, preview: "DEMO" }), []);
  }
  assert.equal(read(fixtures, "app_memories.resolve_due.cleared.example.json").due, null);
  const loopDone = read(fixtures, "app_memories.resolve_loop.example.json");
  assert.notDeepEqual(answer({ ...loopDone, action: "dismiss" }), []);
  const asked = read(fixtures, "app_memories.record_outcome.example.json");
  assert.notDeepEqual(answer({ ...asked, result: "maybe" }), []);
  // A person's word always says what its step said, and no other write says it: what the app reads is what the contract takes.
  const said = { resolve_loop: ["action", "direction", "counterparty"], resolve_due: ["action", "due", "before"], record_outcome: ["result", "previous", "rearms", "reviewAt", "reviewsDone"] };
  const captured = read(fixtures, "app_memories.capture.example.json");
  for (const [op, members] of Object.entries(said)) {
    const write = read(fixtures, `app_memories.${op}.example.json`);
    for (const member of members) {
      const { [member]: _left, ...without } = write;
      assert.notDeepEqual(answer(without), [], `${op} without ${member}`);
      assert.notDeepEqual(answer({ ...captured, [member]: write[member] }), [], `capture with ${member}`);
    }
    assert.notDeepEqual(answer({ ...write, op: "update" }), [], `${op} under another op`);
  }
  const clear = read(fixtures, "app_memories.resolve_due.cleared.example.json");
  assert.notDeepEqual(answer({ ...clear, action: "done" }), []);
  assert.notDeepEqual(answer({ ...loopDone, action: "snooze" }), []);
  assert.equal(read(fixtures, "app_memories.resolve_due.example.json").due.source, "explicit", "a snooze's date is a person's own");

  // AI changes say how many groups a read found beside those it shows; an engine from before them stays readable.
  const changes = read(fixtures, "app_memories.changes.example.json");
  assert.deepEqual(answer({ ...changes, count: brief.changes.count }), []);
  assert.deepEqual(answer(changes), []);
  assert.notDeepEqual(answer({ ...changes, count: -1 }), []);
});

test("the app's memories answer the memory UI's last pieces: standing instructions, a stop, and a step for several", () => {
  // SB-P6-01 (run 22): the standing list counts its whole length and how many of it may fire, are held and are over the limit; a held one
  // comes without its words or project, and one that fires names no reason why not. A stop is a write. A step for several answers each item
  // on its own with no one's words, and takes only the one-word steps.
  const answer = (value) => check(folder(mcp), "app_memories.output.schema.json", value);
  const standing = read(fixtures, "app_memories.standing.example.json");
  assert.deepEqual(answer(standing), []);
  assert.equal(standing.count, standing.standing.length);
  assert.equal(standing.firing, standing.standing.filter((s) => s.fires).length);
  assert.equal(standing.held, standing.standing.filter((s) => s.held).length);
  const [fires, held] = standing.standing;
  assert.notDeepEqual(answer({ ...standing, standing: [{ ...held, content: "DEMO: words" }] }), [], "a held one's words");
  assert.notDeepEqual(answer({ ...standing, standing: [{ ...held, project: { id: fires.id, slug: "wendle" } }] }), [], "a held one's project");
  assert.notDeepEqual(answer({ ...standing, standing: [{ ...fires, reason: "held" }] }), [], "one that fires has no reason not to");
  assert.notDeepEqual(answer({ ...standing, standing: [{ ...held, reason: null }] }), [], "one that doesn't fires says why");
  assert.notDeepEqual(answer({ ...standing, standing: [{ ...fires, content: "x".repeat(501) }] }), []);
  assert.notDeepEqual(answer({ ...standing, standing: [{ ...fires, reason: "maybe", fires: false }] }), []);
  const { served: _served, ...unserved } = standing;
  assert.notDeepEqual(answer(unserved), []);

  const stopped = read(fixtures, "app_memories.stop_standing.example.json");
  assert.deepEqual(answer(stopped), []);
  assert.deepEqual(stopped.changed, ["standing"]);
  assert.equal(stopped.content, undefined, "a write never answers with a memory's words");

  const several = read(fixtures, "app_memories.resolve_all.example.json");
  assert.deepEqual(answer(several), []);
  assert.deepEqual(several.results.map((r) => r.result), ["resolved", "unchanged", "forbidden"]);
  for (const action of ["release", "keep_copy", "settle_keep_both", "use_candidate", "done"])
    assert.notDeepEqual(answer({ ...several, action }), [], `${action} is one at a time`);
  assert.notDeepEqual(answer({ ...several, results: [] }), [], "at least one item");
  assert.notDeepEqual(answer({ ...several, results: Array(51).fill(several.results[0]) }), [], "at most 50");
  assert.notDeepEqual(answer({ ...several, results: [{ ...several.results[0], preview: "DEMO: words" }] }), [], "no one's words");
  assert.notDeepEqual(answer({ ...several, results: [{ ...several.results[0], result: "maybe" }] }), []);
});

test("the checker reports pointers and keeps to its subset", () => {
  const latest = { selection: "latest", complete: true, subject: "Alex", rows: [] };
  assert.deepEqual(check(folder(mcp), "app_latest_glucose.output.schema.json", latest), ["/subject: does not match ^[a-z][a-z0-9-]{0,31}$"]);
  // unevaluatedProperties is in the subset since the settings schema needs it (above); patternProperties is not.
  assert.deepEqual(unsupported({ type: "object", patternProperties: { "^x": {} } }), ["/patternProperties: unsupported keyword"]);
});
