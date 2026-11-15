import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { handleDashboards } from "../server/dashboards";
import { GET } from "../api/dashboards";
import { createDemoDashboards } from "../src/demoDashboards";
import { resetEngine } from "../server/engine";
import { allAppTools, withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
});
// A fictional engine, reached with the one app token.
const configured = () => withEngine();
function rpc(payload: unknown) {
  return new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
}
const boards = createDemoDashboards();
const [summary] = boards.list();
const base = "http://rhythm.example.invalid/api/dashboards";
const ask = (query = "") => handleDashboards(new Request(base + query));
const sent = () =>
  JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params;

it("lists dashboards with the app token, privately, and says when it couldn't", async () => {
  configured();
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ dashboards: [summary, { ...summary, id: "bad" }] }),
  );
  const response = await ask();
  expect(response.status).toBe(200);
  expect(response.headers.get("cache-control")).toBe(
    "private, no-store, max-age=0",
  );
  expect(await response.json()).toEqual({ dashboards: [summary] });
  expect(sent()).toEqual({ name: "app_dashboards", arguments: {} });
  // A failed or unreadable read is unavailable, never an empty list, which would read as
  // every dashboard deleted (a screen's Home among them).
  vi.mocked(fetch).mockRejectedValueOnce(new Error("down"));
  const failed = await ask();
  expect(failed.status).toBe(502);
  expect(await failed.json()).not.toHaveProperty("dashboards");
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ boards: [summary] }));
  expect((await ask()).status).toBe(502);
  // An engine with no dashboards answers an empty list.
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ dashboards: [] }));
  expect(await (await ask()).json()).toEqual({ dashboards: [] });
});

it("opens one version and reads what its feeds hold now, sending only a checked id", async () => {
  configured();
  const dashboard = boards.get(summary.id, 1)!;
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ dashboard }));
  const opened = await ask(`?id=${summary.id.toUpperCase()}&version=1`);
  expect(opened.status).toBe(200);
  expect(await opened.json()).toEqual({ dashboard });
  expect(sent()).toEqual({
    name: "app_dashboards",
    arguments: { id: summary.id, version: 1 },
  });
  const data = boards.data(summary.id)!;
  vi.mocked(fetch).mockResolvedValueOnce(rpc(data));
  const now = await ask(`?id=${summary.id}&data=1`);
  expect(await now.json()).toEqual(data);
  expect(sent()).toEqual({
    name: "app_dashboards",
    arguments: { id: summary.id, data: true },
  });
  // Another dashboard's answer, or a malformed one, is unavailable, never a lost-access 403.
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({
      dashboard: { ...dashboard, id: "00000000-0000-4000-8000-000000000001" },
    }),
  );
  expect((await ask(`?id=${summary.id}`)).status).toBe(502);
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ ...data, feeds: { plan: { kind: "data" } } }),
  );
  expect((await ask(`?id=${summary.id}&data=1`)).status).toBe(502);
});

it("refuses bad requests and answers nothing without the gate", async () => {
  for (const query of [
    "?id=nope",
    "?version=2",
    `?id=${summary.id}&version=0`,
    `?id=${summary.id}&data=yes`,
    "?data=1",
    `?id=${summary.id}&html=1`,
  ])
    expect((await ask(query)).status).toBe(400);
  expect(fetch).not.toHaveBeenCalled();
  expect(await (await GET(new Request(base))).json()).toEqual({
    dashboards: [],
  });
  expect((await ask(`?id=${summary.id}`)).status).toBe(503);
  // POST carries one change; anything without a checked body is refused.
  expect(
    (await handleDashboards(new Request(base, { method: "POST" }))).status,
  ).toBe(400);
  expect(
    (await handleDashboards(new Request(base, { method: "DELETE" }))).status,
  ).toBe(405);
  expect(
    (
      await handleDashboards(
        new Request(base, { headers: { origin: "https://elsewhere.example" } }),
      )
    ).status,
  ).toBe(403);
  expect(fetch).not.toHaveBeenCalled();
});

// ----- Piece 3: arranging a dashboard and a map part's focused sky (docs/dev/screens.md 10.7).

const maps = boards.list()[2];
const layout = maps.layout!;
// An engine with piece 3 lists `layout` and `map` among app_dashboards' arguments.
const piece3 = () =>
  resetEngine(allAppTools, { app_dashboards: ["id", "layout", "map"] });
const post = (body: unknown) =>
  handleDashboards(
    new Request(base, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    }),
  );
const arranging = {
  action: "layout",
  id: maps.id,
  version: 1,
  layout,
  base: 0,
  note: "Moved the map",
};

it("saves an arrangement from an exact body to an engine that takes layouts, and never twice", async () => {
  configured();
  piece3();
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ ok: true, layout, layoutRevision: 1 }),
  );
  const saved = await post(arranging);
  expect(await saved.json()).toEqual({
    ok: true,
    layout,
    layoutWhole: true,
    layoutRevision: 1,
  });
  expect(sent()).toEqual({
    name: "app_dashboards",
    arguments: {
      id: maps.id,
      version: 1,
      layout,
      base: 0,
      note: "Moved the map",
    },
  });
  // Changed since: the layout as it stands, to make the change on once more.
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ ok: false, reason: "Changed since.", layout, layoutRevision: 4 }),
  );
  expect(await (await post(arranging)).json()).toMatchObject({
    ok: false,
    layoutRevision: 4,
  });
  // No answer: it may have been saved, and it isn't sent again; a screen's Undo of
  // it may be tried again later.
  const before = vi.mocked(fetch).mock.calls.length;
  vi.mocked(fetch).mockRejectedValueOnce(new Error("lost"));
  vi.spyOn(console, "warn").mockImplementation(() => {});
  expect(await (await post(arranging)).json()).toEqual({
    ok: false,
    reason: expect.stringMatching(/^The arrangement may have been saved/),
    transient: true,
  });
  expect(vi.mocked(fetch).mock.calls.length).toBe(before + 1);
});

it("refuses an arrangement that isn't one before it reaches the engine", async () => {
  configured();
  piece3();
  for (const body of [
    { ...arranging, extra: 1 },
    { ...arranging, base: -1 },
    { ...arranging, note: "" },
    { ...arranging, note: "two\nlines" },
    { ...arranging, id: "nope" },
    { ...arranging, layout: [] },
  ])
    expect((await post(body)).status).toBe(400);
  // A layout that breaks the rules says why, and changes nothing.
  const overlapping = {
    parts: layout.parts.map((p) => (p.id === "page" ? { ...p, x: 6 } : p)),
  };
  expect(
    await (await post({ ...arranging, layout: overlapping })).json(),
  ).toEqual({
    ok: false,
    reason: "The parts “map” and “page” overlap.",
  });
  expect(fetch).not.toHaveBeenCalled();
  // An engine whose tool list doesn't take layouts is never asked; that may change.
  resetEngine(allAppTools);
  expect(await (await post(arranging)).json()).toEqual({
    ok: false,
    reason: "This install's engine can't arrange dashboards yet.",
    transient: true,
  });
  expect(fetch).not.toHaveBeenCalled();
});

it("reads a map part's focused sky from an engine with piece 3 only", async () => {
  configured();
  const graph = JSON.parse(
    readFileSync(
      resolve(process.cwd(), "../contracts/fixtures/app_graph.example.json"),
      "utf8",
    ),
  );
  const query = `?id=${maps.id}&map=map&cursor=o:20`;
  expect((await ask(query)).status).toBe(503);
  expect(fetch).not.toHaveBeenCalled();
  piece3();
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({
      map: {
        part: "map",
        focus: { query: "walks" },
        matched: 42,
        ...graph,
        nextCursor: "o:40",
      },
    }),
  );
  const read = await ask(query);
  expect(read.status).toBe(200);
  expect(await read.json()).toMatchObject({
    map: {
      part: "map",
      focus: { query: "walks" },
      matched: 42,
      nextCursor: "o:40",
    },
  });
  expect(sent()).toEqual({
    name: "app_dashboards",
    arguments: { id: maps.id, map: "map", cursor: "o:20" },
  });
  for (const bad of [
    "?map=map",
    `?id=${maps.id}&map=Map`,
    `?id=${maps.id}&map=map&cursor=20`,
    // Past the engine's five digits, refused here rather than by the engine.
    `?id=${maps.id}&map=map&cursor=o:123456`,
    `?id=${maps.id}&cursor=o:20`,
    `?id=${maps.id}&map=map&data=1`,
  ])
    expect((await ask(bad)).status).toBe(400);
  // Another part's answer, or one that isn't a page of the map, is unavailable.
  vi.spyOn(console, "warn").mockImplementation(() => {});
  vi.mocked(fetch).mockResolvedValueOnce(
    rpc({ map: { part: "other", focus: null, matched: 0, ...graph } }),
  );
  expect((await ask(query)).status).toBe(502);
});
