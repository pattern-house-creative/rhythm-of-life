import { afterEach, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { handleDashboards } from "../server/dashboards";
import { POST } from "../api/dashboards";
import {
  cleanCards,
  cleanDashboard,
  cleanDashboardData,
  cleanSummary,
  cleanWriteAnswer,
} from "../src/dashboards";
import type {
  Dashboard,
  DashboardData,
  DashboardWriteAnswer,
} from "../src/dashboards";
import { createDemoDashboards } from "../src/demoDashboards";
import { dashboardCache } from "../src/dashboardCache";
import { createApiSession } from "../src/apiClient";
import DashboardView from "../src/DashboardView";
import DashboardCard from "../src/DashboardCard";
import { frameDocument } from "../src/dashboardFrame";
import { withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
const configured = () => withEngine();
const rpc = (payload: unknown) =>
  new Response(
    JSON.stringify({
      jsonrpc: "2.0",
      id: "rhythm-app",
      result: { structuredContent: payload },
    }),
    { headers: { "content-type": "application/json" } },
  );
const sent = () =>
  JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]?.body)).params;
const change = (body: unknown, origin?: string) =>
  POST(
    new Request("http://rhythm.example.invalid/api/dashboards", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        ...(origin ? { origin } : {}),
      },
      body: JSON.stringify(body),
    }),
  );
const boards = createDemoDashboards();
const [summary] = boards.list();
const id = summary.id;
const write = {
  id: "11111111-1111-4111-8111-111111111111",
  dashboard: id,
  version: 2,
  name: "effort",
  kind: "log",
  summary: "Training log: effort 7 of 10 for Oct 29",
  writtenAt: "2026-10-29T01:00:00.000Z",
  undoneAt: null,
};

it("keeps writes, edits, corners, log feeds and write answers only as the contract says", () => {
  // A version declares what it may write; edits count only when it writes something.
  const shown = cleanSummary({ ...summary, editsAllowed: true, corners: ["training"] })!;
  expect(shown.writes).toEqual([
    { name: "effort", kind: "log", log: "training", subject: "alex" },
  ]);
  expect((shown.editsAllowed, shown.corners)).toEqual(["training"]);
  expect(cleanSummary({ ...summary, writes: [], editsAllowed: true })!.editsAllowed).toBe(false);
  // Before the engine knew writes and cards, a dashboard simply has neither.
  const { writes: _w, editsAllowed: _e, corners: _c, ...older } = summary;
  expect(cleanSummary(older)).toMatchObject({ writes: [], editsAllowed: false, corners: [] });
  for (const bad of [
    { writes: [{ name: "Bad", kind: "log", log: "x", subject: "alex" }] },
    { writes: [{ name: "a", kind: "email" }] },
    { writes: [{ name: "a", kind: "log", log: "Training", subject: "alex" }] },
    { writes: [{ name: "a", kind: "sheet" }] },
    { corners: ["ceiling"] },
    { editsAllowed: "yes" },
  ])
    expect(cleanSummary({ ...summary, ...bad })).toBeNull();
  // A log feed names a log, not a star; its payload comes through with no star.
  const dashboard = boards.get(id)!;
  expect(cleanDashboard({ dashboard })!.feeds.at(-1)).toEqual({
    name: "efforts",
    kind: "log",
    log: "training",
    subject: "alex",
    days: 7,
  });
  expect(cleanDashboard({ dashboard: { ...dashboard, feeds: [{ name: "a", kind: "log", log: "Bad", subject: "alex" }] } })).toBeNull();
  const data = boards.data(id)!;
  expect(cleanDashboardData(data)!.feeds.efforts.kind).toBe("log");
  expect(cleanDashboardData({ ...data, feeds: { efforts: { ...data.feeds.efforts, star: { graphId: "x", title: null } } } })).toBeNull();
  // Write answers and corner cards.
  expect(cleanWriteAnswer({ ok: true, write: { ...write, extra: 1 } })).toEqual({ ok: true, write });
  expect(cleanWriteAnswer({ ok: false, reason: "Edits are off for this dashboard." })).toEqual({ ok: false, reason: "Edits are off for this dashboard." });
  for (const bad of [{ ok: true }, { ok: false }, { ok: true, write: { ...write, id: "x" } }, { ok: "yes" }, null])
    expect(cleanWriteAnswer(bad)).toBeNull();
  expect(cleanCards({ cards: { main: id.toUpperCase(), ceiling: id, training: "x" } })).toEqual({ main: id });
  expect(cleanCards({ cards: { main: id } }, new Set(["other"]))).toEqual({});
  // The tall slot was once called glucose; that name is no corner now.
  expect(cleanCards({ cards: { glucose: id } })).toEqual({});
  expect(cleanCards(null)).toEqual({});
});

it("sends one checked change with the app token and answers what happened", async () => {
  configured();
  // A write: only the declared shape goes to the engine, and its answer comes back as checked.
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: true, write: { ...write, before: null, target: {} } }));
  const done = await change({ action: "write", id: id.toUpperCase(), version: 2, name: "effort", change: { metric: "rpe", value: 7, unit: "score" } });
  expect(done.status).toBe(200);
  expect(await done.json()).toEqual({ ok: true, write });
  expect(sent()).toEqual({
    name: "app_dashboards",
    arguments: { id, version: 2, write: { name: "effort", change: { metric: "rpe", value: 7, unit: "score" } } },
  });
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: false, reason: "Edits are off for this dashboard." }));
  expect(await (await change({ action: "write", id, version: 2, name: "effort", change: { note: "x" } })).json())
    .toEqual({ ok: false, reason: "Edits are off for this dashboard." });
  // Undo, edits and corners.
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: true, write: { ...write, undoneAt: "2026-10-29T01:01:00.000Z" } }));
  expect((await (await change({ action: "undo", write: write.id })).json()).write.undoneAt).toBe("2026-10-29T01:01:00.000Z");
  expect(sent().arguments).toEqual({ undo: write.id });
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ id, version: 2, editsAllowed: true }));
  expect(await (await change({ action: "edits", id, version: 2, allowed: true })).json()).toEqual({ editsAllowed: true });
  expect(sent().arguments).toEqual({ id, version: 2, allowEdits: true });
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ cards: { training: id } }));
  expect(await (await change({ action: "card", corner: "training", dashboard: id })).json()).toEqual({ cards: { training: id } });
  expect(sent().arguments).toEqual({ corner: "training", dashboard: id });
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ cards: {} }));
  await change({ action: "card", corner: "training", dashboard: null });
  expect(sent().arguments).toEqual({ corner: "training", dashboard: null });
  vi.mocked(fetch).mockResolvedValueOnce(rpc({ cards: { recovery: id } }));
  expect(await (await handleDashboards(new Request("http://rhythm.example.invalid/api/dashboards?cards=1"))).json()).toEqual({ cards: { recovery: id } });
  expect(sent().arguments).toEqual({ cards: true });

  // A change too large for the engine's request limit is refused here, with a reason.
  vi.mocked(fetch).mockClear();
  expect(await (await change({ action: "write", id, version: 2, name: "plan", change: { revision: "1", text: "é".repeat(16_000) } })).json())
    .toEqual({ ok: false, reason: "That change is too large to send at once; split it into smaller ones." });
  // Anything else is refused before it reaches the engine; an upstream failure changes nothing and says so.
  for (const bad of [
    { action: "write", id, version: 2, name: "effort" },
    { action: "write", id, version: 0, name: "effort", change: {} },
    { action: "write", id, version: 2, name: "Effort", change: {} },
    { action: "write", id, version: 2, name: "effort", change: [] },
    { action: "undo", write: "x" },
    { action: "edits", id, version: 2, allowed: "yes" },
    { action: "card", corner: "ceiling", dashboard: id },
    { action: "card", corner: "training" },
    { action: "delete", id },
  ])
    expect((await change(bad)).status).toBe(400);
  expect((await change({ action: "undo", write: write.id }, "https://x.test")).status).toBe(403);
  expect(fetch).not.toHaveBeenCalled();
  // The engine refused it, or it never left: nothing changed.
  const refused = { ok: false, reason: "The engine couldn't make that change right now; nothing was changed." };
  for (const answer of [
    new Response("", { status: 401 }),
    new Response("", { status: 403 }),
    new Response(JSON.stringify({ jsonrpc: "2.0", id: "rhythm-app", result: { structuredContent: { error: { code: "invalid_arguments" } }, isError: true } }), { headers: { "content-type": "application/json" } }),
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(answer);
    expect(await (await change({ action: "write", id, version: 2, name: "effort", change: { note: "x" } })).json()).toEqual(refused);
  }
  // No answer, a gateway's error or an answer that can't be read: the engine may have made the
  // change before it was lost, so the page is told to check rather than to try again.
  const unknown = {
    ok: false,
    reason: "The change may have been made: the engine's answer didn't arrive or couldn't be read. Check before trying again.",
  };
  for (const answer of [
    () => vi.mocked(fetch).mockRejectedValueOnce(new Error("down")),
    () => vi.mocked(fetch).mockRejectedValueOnce(new DOMException("The operation timed out.", "TimeoutError")),
    () => vi.mocked(fetch).mockResolvedValueOnce(new Response("", { status: 504 })),
    () => vi.mocked(fetch).mockResolvedValueOnce(rpc({ ok: true, write: { ...write, summary: "" } })),
    () => vi.mocked(fetch).mockResolvedValueOnce(new Response("<html>", { headers: { "content-type": "text/html" } })),
  ]) {
    answer();
    const calls = vi.mocked(fetch).mock.calls.length;
    expect(await (await change({ action: "write", id, version: 2, name: "effort", change: { note: "x" } })).json()).toEqual(unknown);
    // A write is never sent twice.
    expect(vi.mocked(fetch).mock.calls.length).toBe(calls + 1);
  }
  vi.mocked(fetch).mockRejectedValueOnce(new Error("down"));
  expect(await (await change({ action: "undo", write: write.id })).json()).toEqual(unknown);
  vi.unstubAllEnvs();
  expect(await (await change({ action: "undo", write: write.id })).json()).toEqual({
    ok: false,
    reason: "Dashboard changes are unavailable right now.",
  });
  expect((await change({ action: "card", corner: "training", dashboard: null })).status).toBe(503);
});

// The page's side of the channel: the app makes one when the frame loads and
// hands the frame its second port.
const channels: MessageChannel[] = [];
const Channel = globalThis.MessageChannel;
function capture() {
  vi.stubGlobal(
    "MessageChannel",
    class extends Channel {
      constructor() {
        super();
        channels.push(this);
      }
    },
  );
  return channels.length;
}
async function page(before: number) {
  await waitFor(() => expect(channels.length).toBeGreaterThan(before));
  const port = channels.at(-1)!.port2;
  const heard: { type: string; [k: string]: unknown }[] = [];
  port.onmessage = (e) => heard.push(e.data);
  return {
    heard,
    say: (m: unknown) => port.postMessage(m),
    answer: async (n: number) => {
      await waitFor(() => expect(heard.some((h) => h.type === "written" && h.id === n)).toBe(true));
      return heard.find((h) => h.type === "written" && h.id === n)!.answer as DashboardWriteAnswer & { summary?: string };
    },
  };
}

it("asks before a page writes, applies its declared write, and shows the change with Undo outside the frame", async () => {
  const dashboard: Dashboard = { ...boards.get(id)!, editsAllowed: false };
  const data: DashboardData = boards.data(id)!;
  const session = {
    dashboard: vi.fn(async () => dashboard),
    dashboardData: vi.fn(async () => data),
    dashboardAllowEdits: vi.fn(async (_i: string, _v: number, on: boolean) => on),
    dashboardWrite: vi.fn(async (): Promise<DashboardWriteAnswer> => ({ ok: true, write: { ...write, kind: "log" } })),
    dashboardUndo: vi.fn(async (): Promise<DashboardWriteAnswer> => ({ ok: true, write: { ...write, kind: "log", undoneAt: "2026-10-29T01:01:00.000Z" } })),
  };
  const before = capture();
  render(<DashboardView id={id} session={session} onOpenStar={() => {}} />);
  await screen.findByTitle("Sample training week dashboard");
  const p = await page(before);
  await waitFor(() => expect(p.heard.some((h) => h.type === "data")).toBe(true));
  const delivered = p.heard.find((h) => h.type === "data")!.payload as { mode: string; edits: boolean };
  expect(delivered).toMatchObject({ mode: "full", edits: false });
  // Edits off: the page hears why, and nothing is sent.
  const allow = screen.getByRole("switch", { name: "Allow edits" });
  expect(allow).toHaveAttribute("aria-checked", "false");
  expect(screen.getByText(/When on, it can add to the training log for Alex\./)).toBeInTheDocument();
  await act(async () => p.say({ type: "write", id: 1, name: "effort", change: { metric: "rpe", value: 7, unit: "score" } }));
  expect(await p.answer(1)).toEqual({ ok: false, reason: expect.stringMatching(/^Edits are off/) });
  expect(session.dashboardWrite).not.toHaveBeenCalled();
  // Edits are turned on; the page hears it with fresh data.
  await act(async () => fireEvent.click(allow));
  await waitFor(() => expect(allow).toHaveAttribute("aria-checked", "true"));
  expect(session.dashboardAllowEdits).toHaveBeenCalledWith(id, 2, true);
  expect((p.heard.filter((h) => h.type === "data").at(-1)!.payload as { edits: boolean }).edits).toBe(true);
  // The write goes through; the change shows outside the frame, and the feeds are read again.
  const reads = session.dashboardData.mock.calls.length;
  await act(async () => p.say({ type: "write", id: 2, name: "effort", change: { metric: "rpe", value: 7, unit: "score" } }));
  expect(await p.answer(2)).toEqual({ ok: true, summary: write.summary });
  expect(session.dashboardWrite).toHaveBeenCalledWith(id, 2, "effort", { metric: "rpe", value: 7, unit: "score" });
  expect(await screen.findByRole("status")).toHaveTextContent(write.summary);
  await waitFor(() => expect(session.dashboardData.mock.calls.length).toBeGreaterThan(reads));
  // Undo puts it back.
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));
  expect(session.dashboardUndo).toHaveBeenCalledWith(write.id);
  expect(await screen.findByText(`Undone: ${write.summary}`)).toBeInTheDocument();
  // A refusal from the engine reaches the page as it is.
  session.dashboardWrite.mockResolvedValueOnce({ ok: false, reason: "Budget changed after the dashboard read it." });
  await act(async () => p.say({ type: "write", id: 3, name: "effort", change: { note: "x" } }));
  expect(await p.answer(3)).toEqual({ ok: false, reason: "Budget changed after the dashboard read it." });
  // No answer at all: the change may have reached the engine, so the page is told to check.
  session.dashboardWrite.mockRejectedValueOnce(new Error("timeout"));
  await act(async () => p.say({ type: "write", id: 4, name: "effort", change: { note: "x" } }));
  expect(await p.answer(4)).toEqual({
    ok: false,
    reason: "The change may have been made: no answer came back. Check before trying again.",
  });
});

it("names what each declared write may do before edits are turned on, a file once", async () => {
  const sheet = "fixture:sheet",
    doc = "fixture:doc";
  const dashboard: Dashboard = {
    ...boards.get(id)!,
    editsAllowed: false,
    stars: [
      ...boards.get(id)!.stars,
      { graphId: sheet, title: "Sample budget" },
      { graphId: doc, title: "Sample plan" },
    ],
    writes: [
      { name: "effort", kind: "log", log: "training", subject: "alex" },
      { name: "budget", kind: "sheet", star: sheet, range: "'Bob''s budget'!B2:D40", appendTo: "Receipts" },
      { name: "week", kind: "doc", star: doc, section: "This week" },
      { name: "table", kind: "doc", star: doc, table: 0 },
      { name: "diary", kind: "doc", star: doc, append: true },
    ],
  };
  const session = {
    dashboard: vi.fn(async () => dashboard),
    dashboardData: vi.fn(async () => boards.data(id)!),
    dashboardAllowEdits: vi.fn(async (_i: string, _v: number, on: boolean) => on),
  };
  // Drawn from this declaration, not the version an earlier test kept.
  dashboardCache.clear();
  render(<DashboardView id={id} session={session} onOpenStar={() => {}} />);
  const allow = await screen.findByRole("switch", { name: "Allow edits" });
  expect(allow).toHaveAccessibleDescription(
    "When on, it can add to the training log for Alex; set cells B2:D40 on the Bob's budget tab and add rows to the Receipts tab of Sample budget; replace the text under “This week” or in a cell of table 1 in Sample plan, and add to its end.",
  );
  await act(async () => fireEvent.click(allow));
  await waitFor(() => expect(allow).toHaveAttribute("aria-checked", "true"));
  expect(allow).toHaveAccessibleDescription(
    /^It can add to the training log for Alex; .* Changes are kept in its history; the latest made here has Undo\.$/,
  );
});

it("draws a corner's card in card mode with no writes, and opens the dashboard when tapped", async () => {
  const onOpen = vi.fn();
  const session = { dashboard: vi.fn(async () => boards.get(id)!), dashboardData: vi.fn(async () => boards.data(id)!) };
  const before = capture();
  render(<DashboardCard id={id} title="Sample training week" session={session} onOpen={onOpen} />);
  const frame = await screen.findByTitle("Sample training week card");
  expect(frame.getAttribute("srcdoc")).toContain('data-mode=\\"card\\"');
  const p = await page(before);
  await waitFor(() => expect(p.heard.some((h) => h.type === "data")).toBe(true));
  expect(p.heard.find((h) => h.type === "data")!.payload).toMatchObject({ mode: "card", shape: "compact", edits: false });
  expect(frame.getAttribute("srcdoc")).toContain('data-shape=\\"compact\\"');
  await act(async () => p.say({ type: "write", id: 1, name: "effort", change: { note: "x" } }));
  expect(await p.answer(1)).toEqual({ ok: false, reason: "Open the dashboard to make changes." });
  fireEvent.click(screen.getByRole("button", { name: "Open the Sample training week dashboard" }));
  expect(onOpen).toHaveBeenCalled();
});

it("asks the main slot's dashboard for a tall card, and centres a page that draws only a compact one", async () => {
  const session = { dashboard: vi.fn(async () => boards.get(id)!), dashboardData: vi.fn(async () => boards.data(id)!) };
  const before = capture();
  render(<DashboardCard id={id} title="Sample training week" session={session} onOpen={vi.fn()} shape="tall" />);
  const frame = await screen.findByTitle("Sample training week card");
  expect(frame.getAttribute("srcdoc")).toContain('data-shape=\\"tall\\"');
  const p = await page(before);
  await waitFor(() => expect(p.heard.find((h) => h.type === "data")?.payload).toMatchObject({ mode: "card", shape: "tall" }));
  // The full dashboard has no shape; a tall card's frame centres a body that ignores it.
  expect(frameDocument("<p>x</p>", {}, "full")).toContain('<html data-mode="full"><head>');
  expect(frameDocument("<p>x</p>", {}, "card", "tall")).toContain('<html data-mode="card" data-shape="tall">');
  expect(frameDocument("<p>x</p>", {}, "card", "tall")).toContain(':where(html[data-shape="tall"]) body{min-height:100vh;display:flex;flex-direction:column;justify-content:center}');
});

it("keeps the demo's edits, log and corner cards in memory with its session", async () => {
  const session = createApiSession(true);
  expect((await session.dashboardWrite!(id, 2, "effort", { metric: "rpe", value: 7, unit: "score" })).ok).toBe(false);
  expect(await session.dashboardAllowEdits!(id, 2, true)).toBe(true);
  expect((await session.dashboards!())[0].editsAllowed).toBe(true);
  const done = await session.dashboardWrite!(id, 2, "effort", { metric: "rpe", value: 7, unit: "score" });
  expect(done.ok).toBe(true);
  const log = (await session.dashboardData!(id, 2))!.feeds.efforts as unknown as { entries: { value: number }[] };
  expect(log.entries.map((e) => e.value)).toEqual([7]);
  expect((await session.dashboardUndo!(done.ok ? done.write.id : "")).ok).toBe(true);
  expect(((await session.dashboardData!(id, 2))!.feeds.efforts as unknown as { entries: unknown[] }).entries).toEqual([]);
  // The week at a glance fills the main slot and this one a corner, until chosen otherwise.
  const glance = (await session.dashboards!())[1];
  expect(glance.title).toBe("Week at a glance");
  expect(await session.dashboardCards!()).toEqual({ main: glance.id, recovery: id });
  expect((await session.dashboards!())[0].corners).toEqual(["recovery"]);
  expect(await session.setDashboardCard!("training", id)).toEqual({ main: glance.id, recovery: id, training: id });
  expect((await session.dashboards!())[0].corners).toEqual(["recovery", "training"]);
  expect(await session.setDashboardCard!("recovery", null)).toEqual({ main: glance.id, training: id });
  expect(await session.setDashboardCard!("main", null)).toEqual({ training: id });
});
