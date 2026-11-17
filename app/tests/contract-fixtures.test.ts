import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { loadHealth } from "../server/health";
import {
  mapAdditionalHealth,
  requestGarminSync,
} from "../server/additionalHealth";
import { handleDocuments } from "../server/documents";
import { readRelationshipGraph } from "../server/relationshipGraph";
import { requestRebuild } from "../server/graphRefresh";
import { cleanInsights, cleanRuns } from "../src/insights";
import {
  cleanDashboard,
  cleanDashboardData,
  cleanDashboardMap,
  cleanLayoutAnswer,
  cleanSummaries,
} from "../src/dashboards";
import { cleanListen, cleanScreens, cleanSettings } from "../src/screens";
import { cleanOriginal } from "../src/documents";
import { assertObservatory } from "../src/validation";
import { replacedBy, sourceName } from "../src/graphView";
import { GET } from "../api/observatory";
import { rpc, withEngine } from "./engine";

// The contract's own fictional examples (contracts/fixtures), each valid against its schema in
// contracts/mcp (scripts/contracts.test.mjs checks that). The facade must read every one of them.
// Tests run from app/, beside contracts/.
const fixture = (name: string) =>
  JSON.parse(
    readFileSync(resolve(process.cwd(), "../contracts/fixtures", name), "utf8"),
  );
const page = new Request("http://rhythm.example.invalid/api/graph", {
  method: "POST",
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

it("reads the latest live reading and names the glucose source after its provider", async () => {
  withEngine();
  const latest = fixture("app_latest_glucose.example.json");
  const data = await loadHealth(
    undefined,
    vi.fn().mockResolvedValue(rpc(latest)),
    () => "2026-10-28T11:58:00.000Z",
  );
  assertObservatory(data);
  expect(data.current).toMatchObject({ value: 112, status: "available" });
  expect(data.current.provenance).toMatchObject({
    sourceId: "nightscout",
    demo: false,
  });
  expect(data.sources.find((s) => s.id === "glucose")).toMatchObject({
    name: "Nightscout",
    connected: true,
  });
});

it("joins the archive and the live feed of the 14-day form", () => {
  withEngine();
  const fortnight = fixture("app_recent_glucose.example.json");
  const data = mapAdditionalHealth(fortnight, "history", fortnight.end);
  assertObservatory(data);
  expect(data.glucoseHistory).toMatchObject({
    sourceKind: "combined",
    complete: true,
  });
  expect(
    data.glucoseHistory!.sources!.map((s) => [s.sourceKind, s.provider]),
  ).toEqual([
    ["archive", "csv"],
    ["live", "nightscout"],
  ]);
  expect(data.glucoseHistory!.samples).toHaveLength(6);
  expect(data.glucoseHistory!.provenance.sourceId).toBe("nightscout");
  // The chart is the live feed's last day only.
  expect(data.glucose.samples.map((s) => s.value)).toEqual([108, 110, 112]);
  expect(data.glucoseSummary).toMatchObject({
    readings: 3,
    sourceKind: "archive",
    status: "available",
  });
  expect(data.sources.find((s) => s.id === "glucose")?.name).toBe("Nightscout");
});

it("reads the 24-hour form of the live feed", () => {
  withEngine();
  const day = fixture("app_recent_glucose.day.example.json");
  const data = mapAdditionalHealth(day, "history", day.end);
  assertObservatory(data);
  expect(data.glucose.samples.map((s) => s.value)).toEqual([110, 111.7]);
  expect(data.glucoseSummary).toBeNull();
});

it("accepts the engine's demo seed only when it is labelled demo", () => {
  withEngine();
  const fortnight = {
    ...fixture("app_recent_glucose.example.json"),
    subject: "fixture",
  };
  expect(() =>
    mapAdditionalHealth(fortnight, "history", fortnight.end),
  ).toThrow();
  const demo = mapAdditionalHealth(
    { ...fortnight, demo: true, provider: "demo-live" },
    "history",
    fortnight.end,
  );
  expect(demo.glucoseHistory!.provenance).toMatchObject({
    sourceId: "demo-live",
    demo: true,
  });
  expect(demo.sources.find((s) => s.id === "glucose")?.name).toBe("Demo live");
});

it("keeps live mode for the engine's demo seed, so its history loads too", async () => {
  withEngine();
  const latest = fixture("app_latest_glucose.example.json");
  const row = {
    ...latest.rows[0],
    subject_id: "fixture",
    provider: "demo-live",
    demo: true,
  };
  const at = new Date(Date.now() - 60000).toISOString().replace(/.d+Z$/, "Z");
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ ...latest, subject: "fixture", rows: [{ ...row, observed_at: at }] }),
  );
  const data = await (
    await GET(new Request("http://rhythm.example.invalid/api/observatory"))
  ).json();
  expect(data.healthMode).toBe("live");
  expect(data.current.provenance).toMatchObject({
    demo: true,
    sourceId: "demo-live",
  });
  expect(
    data.sources.find((s: { id: string }) => s.id === "glucose").name,
  ).toBe("Demo live");
});

it("reads the graph, insights, dashboards and refresh examples", async () => {
  withEngine();
  const graph = fixture("app_graph.example.json");
  const read = await readRelationshipGraph(
    page,
    {},
    vi.fn().mockResolvedValue(rpc(graph)),
  );
  expect(read.nodes.map((n) => n.id)).toEqual(
    graph.nodes.map((n: { id: string }) => n.id),
  );
  expect(read.edges).toHaveLength(1);
  const star = await readRelationshipGraph(
    page,
    { operation: "data", value: graph.nodes[1].id },
    vi.fn().mockResolvedValue(rpc(graph)),
  );
  expect(star?.measurements?.[0].days).toHaveLength(2);
  // A document star opened: its text, and the archived original the reader shows instead.
  const content = fixture("app_graph.content.example.json");
  const opened = await readRelationshipGraph(
    page,
    { operation: "content", value: content.nodes[0].id },
    vi.fn().mockResolvedValue(rpc(content)),
  );
  expect(opened?.resource.content).toMatch(/between one and two bar/);
  expect(cleanOriginal(opened?.resource.original)).toEqual(
    content.nodes[0].resource.original,
  );

  const insights = fixture("app_insights.example.json");
  expect(cleanInsights(insights).map((i) => i.id)).toEqual([
    insights.insights[0].id,
  ]);
  expect(cleanRuns(insights)).toHaveLength(2);

  const [summary] = cleanSummaries(fixture("app_dashboards.example.json"));
  expect(summary).toMatchObject({
    version: 2,
    editsAllowed: true,
    corners: ["main"],
  });
  expect(
    cleanDashboard(fixture("app_dashboards.dashboard.example.json"))?.feeds,
  ).toHaveLength(2);
  // A dashboard that reads memories by filter names no star, and its feed read
  // passes to the page as it came.
  const projects = cleanDashboard(
    fixture("app_dashboards.memories.version.example.json"),
  );
  expect(projects?.stars).toEqual([]);
  expect(projects?.feeds[0]).toMatchObject({ kind: "memories", limit: 500 });
  const board = fixture("app_dashboards.memories.example.json");
  expect(cleanDashboardData(board)?.feeds).toEqual(board.feeds);

  const refresh = await requestRebuild(
    page,
    vi.fn().mockResolvedValue(rpc(fixture("app_request_refresh.example.json"))),
  );
  expect(refresh).toMatchObject({ accepted: true, running: false });
});

// SB-P4-11: a replaced fact's star keeps its truth through the facade, and the app reads what replaced it; every other
// star has none.
it("reads the replaced fact example", async () => {
  withEngine();
  const graph = fixture("app_graph.replaced.example.json");
  const read = await readRelationshipGraph(
    page,
    {},
    vi.fn().mockResolvedValue(rpc(graph)),
  );
  const [fresh, old] = read.nodes;
  expect(old.truth).toEqual({ state: "replaced", replacedBy: fresh.id });
  expect(replacedBy(old)).toBe(fresh.id);
  expect(replacedBy(fresh)).toBeNull();
  for (const n of fixture("app_graph.example.json").nodes)
    expect(replacedBy(n)).toBeNull();
});

it("reads the piece 3 layout, focused map and screens examples", () => {
  // A version never arranged is the page alone; an arranged one is read whole, so it can be
  // arranged; a save answers the layout and its revision; a focused read is one page.
  const [listed] = cleanSummaries(fixture("app_dashboards.example.json"));
  expect(listed).toMatchObject({
    layout: null,
    layoutWhole: true,
    layoutRevision: 0,
  });
  const board = fixture("app_dashboards.dashboard.example.json");
  expect(cleanDashboard(board)).toMatchObject({
    layout: board.dashboard.layout,
    layoutWhole: true,
    layoutRevision: 2,
    layoutNote: "Moved the map right",
  });
  const saved = fixture("app_dashboards.layout.example.json");
  expect(cleanLayoutAnswer(saved, board.dashboard.id)).toEqual({
    ok: true,
    layout: saved.layout,
    layoutWhole: true,
    layoutRevision: 3,
  });
  const map = cleanDashboardMap(
    fixture("app_dashboards.map.example.json"),
    "map",
  );
  expect(map).toMatchObject({
    matched: 2,
    nextCursor: "o:1",
    focus: { near: { star: "notes:notes:walks.md", depth: 1 } },
  });
  expect(map?.nodes).toHaveLength(1);

  // A screen's Home grid in its settings, the parts each screen reports, and a move.
  const screen = fixture("app_screens.example.json").screen;
  expect(
    cleanSettings(cleanListen({ command: null, screen })?.screen.settings),
  ).toEqual(screen.settings);
  const screens = cleanScreens(fixture("app_screens.list.example.json"));
  expect(screens?.[0].showing.parts).toHaveLength(5);
  expect(screens?.[1].showing.parts?.[1]).toMatchObject({
    kind: "map",
    focus: "near DEMO · Training plan",
  });
  const move = cleanListen({
    command: fixture("app_screens.move.example.json").command,
    screen,
  });
  expect(move?.command).toMatchObject({
    action: "move",
    args: { part: "map", place: "right" },
  });
});

it("reads the Garmin summary and sync examples", async () => {
  withEngine();
  const garmin = fixture("app_garmin_summary.example.json");
  const data = mapAdditionalHealth(garmin, "garmin", garmin.end);
  assertObservatory(data);
  expect(data.steps.value).toBe(6420);
  expect(data.steps.provenance).toMatchObject({
    sourceId: "garmin",
    calendarDate: "2026-10-28",
    timeZone: "Europe/London",
  });
  expect(data.recovery.metrics?.restingHeartRate.value).toBe(56);
  expect(data.workouts.map((w) => [w.durationMinutes, w.distanceKm])).toEqual([
    [30, 5],
  ]);

  const sync = fixture("app_request_garmin_sync.example.json");
  expect(
    await requestGarminSync(page, vi.fn().mockResolvedValue(rpc(sync))),
  ).toEqual(sync);
});

it("reads both upload examples, the saved document and the refused file", async () => {
  withEngine();
  const upload = (answer: unknown) =>
    handleDocuments(
      new Request("http://rhythm.example.invalid/api/documents", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: "DEMO Allotment rota.txt",
          content: btoa("DEMO watering rota"),
        }),
      }),
      vi.fn().mockResolvedValue(rpc(answer)),
    );
  const saved = fixture("app_upload_document.example.json");
  const { resourceId: _, ...shown } = saved.document;
  expect(await (await upload(saved)).json()).toEqual({
    ok: true,
    document: shown,
  });
  const refused = fixture("app_upload_document.refused.example.json");
  expect(await (await upload(refused)).json()).toEqual({
    ok: false,
    reason: refused.reason,
  });
});

it("names a provider after a known one when it carries a suffix", () => {
  expect(sourceName("nightscout")).toBe("Nightscout");
  expect(sourceName("calendar")).toBe("Calendar");
  expect(sourceName("carelink-live")).toBe("CareLink live");
  expect(sourceName("carelink-csv")).toBe("CareLink CSV");
  expect(sourceName("cgm-live")).toBe("cgm-live");
});
