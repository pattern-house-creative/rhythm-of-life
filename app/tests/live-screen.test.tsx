import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { LiveScreen } from "../src/LiveScreen";
import type { ScreenActions, ScreenView } from "../src/LiveScreen";
import { createApiSession } from "../src/apiClient";
import type { GraphSession } from "../src/liveGraph";
import { defaultTiles } from "../src/screens";
import type { ListenAnswer, ScreenCommand, Tiles } from "../src/screens";
import { arrangeInPlace, drawGrid, refuseInPlace } from "../src/arranging";
import type { DashboardGrid } from "../src/arranging";
import type { LayoutAnswer } from "../src/dashboards";
import type { HomeGrid, Layout as PartsLayout } from "../src/parts";

// A live screen's settings and its runner against an engine's answers (docs/dev/screens.md
// sections 5 and 6), and the browser's calls to /api/screens. Fictional screens only.
afterEach(() => {
  vi.restoreAllMocks();
});
const me = { id: "hallScreen00000000000002", name: "Hall" };
const banded: Tiles = { ...defaultTiles, main: "recovery", recovery: "main" };
function engine() {
  const calls: {
    args: Parameters<NonNullable<GraphSession["screenListen"]>>[0];
    answer: (value: ListenAnswer | null) => void;
  }[] = [];
  const session = {
    initial: { nodes: [], edges: [] },
    screenListen: vi.fn(
      (args: (typeof calls)[number]["args"], signal?: AbortSignal) =>
        new Promise<ListenAnswer | null>((resolve, reject) => {
          calls.push({ args, answer: resolve });
          signal?.addEventListener("abort", () => reject(new Error("aborted")));
        }),
    ),
    screenDone: vi.fn(async () => {}),
  } as unknown as GraphSession;
  return { calls, session };
}
function actions(over: Partial<ScreenActions> = {}): ScreenActions {
  return {
    showing: () => ({
      view: "home",
      layout: "hero",
      tiles: defaultTiles,
      narrow: false,
    }),
    view: () => ({ view: "home" }),
    starTitle: () => null,
    dashboardTitle: () => "Fictional plan",
    savedTitle: () => "Fictional plan",
    fetchStar: vi.fn(async () => {}),
    fetchDashboard: vi.fn(async () => {}),
    reread: vi.fn(async () => {}),
    openStar: vi.fn(),
    openDashboard: vi.fn(),
    search: vi.fn(() => ({ matches: 0, titles: [] })),
    fullScreen: vi.fn(),
    exitFullScreen: vi.fn(),
    home: vi.fn(),
    restore: vi.fn(),
    homeDashboard: null,
    setHome: vi.fn(),
    layout: "hero",
    setLayout: vi.fn(),
    tiles: defaultTiles,
    setTiles: vi.fn(),
    narrow: false,
    cards: {},
    setCard: vi.fn(async () => {}),
    ...over,
  };
}
const command = (
  action: string,
  args: Record<string, unknown> = {},
): ScreenCommand => ({
  id: "6f1c2b3a-4d5e-4f60-8a7b-9c0d1e2f3a4b",
  action,
  args,
  sender: { kind: "assistant" },
  sentAt: "2026-10-31T09:00:00Z",
  deliveredAt: "2026-10-31T09:00:00.2Z",
  doneAt: null,
  result: "delivered",
  detail: {},
  ms: { deliver: 200, show: null },
});
const self = (settings: Record<string, unknown> = {}) => ({
  id: me.id,
  name: me.name,
  settings,
});

it("takes the engine's settings when only the layout and tiles were lost, and sends them only after a change", async () => {
  const { calls, session } = engine();
  const a = actions();
  const { rerender } = render(
    <LiveScreen session={session} screen={me} stored={false} actions={a} />,
  );
  expect(calls[0].args.settings).toBeUndefined();
  await act(async () =>
    calls[0].answer({
      command: command("home"),
      screen: self({ layout: "band", tiles: banded }),
    }),
  );
  expect(a.setLayout).toHaveBeenCalledWith("band");
  expect(a.setTiles).toHaveBeenCalledWith(banded);
  // What it took is not sent back.
  rerender(
    <LiveScreen
      session={session}
      screen={me}
      stored={false}
      actions={{ ...a, layout: "band", tiles: banded }}
    />,
  );
  await vi.waitFor(() => expect(calls).toHaveLength(2));
  expect(calls[1].args.settings).toBeUndefined();
  rerender(
    <LiveScreen
      session={session}
      screen={me}
      stored={false}
      actions={{ ...a, layout: "corners", tiles: banded }}
    />,
  );
  await act(async () =>
    calls[1].answer({ command: command("home"), screen: self() }),
  );
  await vi.waitFor(() => expect(calls).toHaveLength(3));
  expect(calls[2].args.settings).toEqual({
    layout: "corners",
    tiles: banded,
    home: null,
  });
});

it("never sends back the settings it took from the engine, though the next listen leaves before App draws them", async () => {
  vi.useFakeTimers();
  try {
    const { calls, session } = engine();
    const a = actions();
    const { rerender } = render(
      <LiveScreen session={session} screen={me} stored={false} actions={a} />,
    );
    // An empty answer after the listen's whole wait: the next listen goes out at once.
    await act(async () => vi.advanceTimersByTime(25_000));
    await act(async () =>
      calls[0].answer({
        command: null,
        screen: self({ layout: "band", tiles: banded }),
      }),
    );
    expect(a.setLayout).toHaveBeenCalledWith("band");
    expect(calls).toHaveLength(2);
    expect(calls[1].args.settings).toBeUndefined();
    // A render before App has drawn them (the note, say) changes nothing.
    rerender(
      <LiveScreen session={session} screen={me} stored={false} actions={a} />,
    );
    await act(async () => vi.advanceTimersByTime(25_000));
    await act(async () => calls[1].answer({ command: null, screen: self() }));
    expect(calls[2].args.settings).toBeUndefined();
    // Once App has drawn them, a change after that is sent.
    rerender(
      <LiveScreen
        session={session}
        screen={me}
        stored={false}
        actions={{ ...a, layout: "band", tiles: banded }}
      />,
    );
    await act(async () => vi.advanceTimersByTime(25_000));
    await act(async () => calls[2].answer({ command: null, screen: self() }));
    expect(calls[3].args.settings).toBeUndefined();
    rerender(
      <LiveScreen
        session={session}
        screen={me}
        stored={false}
        actions={{ ...a, layout: "hero", tiles: banded }}
      />,
    );
    await act(async () => vi.advanceTimersByTime(25_000));
    await act(async () => calls[3].answer({ command: null, screen: self() }));
    expect(calls[4].args.settings).toEqual({
      layout: "hero",
      tiles: banded,
      home: null,
    });
  } finally {
    vi.useRealTimers();
  }
});

it("keeps a layout chosen while the first listen waited over the engine's copy", async () => {
  const { calls, session } = engine();
  const a = actions();
  const { rerender } = render(
    <LiveScreen session={session} screen={me} stored={false} actions={a} />,
  );
  // Someone picks a layout before the engine's answer arrives.
  const chosen = { ...a, layout: "corners" as const };
  rerender(
    <LiveScreen
      session={session}
      screen={me}
      stored={false}
      actions={chosen}
    />,
  );
  await act(async () =>
    calls[0].answer({
      command: command("home"),
      screen: self({ layout: "band", tiles: banded }),
    }),
  );
  expect(a.setLayout).not.toHaveBeenCalled();
  expect(a.setTiles).not.toHaveBeenCalled();
  await vi.waitFor(() => expect(calls).toHaveLength(2));
  expect(calls[1].args.settings).toEqual({
    layout: "corners",
    tiles: defaultTiles,
    home: null,
  });
});

it("fetches a star or dashboard beyond this screen's map, and reads the dashboards again for a corner", async () => {
  const { calls, session } = engine();
  // The engine checked them against its whole map; this screen has only the overview.
  const fetched = new Set<string>();
  let fresh = false;
  const a = actions({
    starTitle: (id) => (fetched.has(id) ? "Far note" : null),
    dashboardTitle: (id) => (fetched.has(id) ? "Far plan" : null),
    savedTitle: () => (fresh ? "New plan" : null),
    fetchStar: vi.fn(async (id: string) => void fetched.add(id)),
    fetchDashboard: vi.fn(async (id: string) => void fetched.add(id)),
    reread: vi.fn(async () => {}),
    view: () => ({ view: "star", star: "rhythm:memory:far" }),
    cards: {},
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = async (c: ScreenCommand) => {
    const n = calls.length;
    await act(async () => calls[n - 1].answer({ command: c, screen: self() }));
    await vi.waitFor(() => expect(calls).toHaveLength(n + 1));
    return vi.mocked(session.screenDone!).mock.calls.at(-1)![0];
  };
  expect(
    await step(command("open_star", { star: "rhythm:memory:far" })),
  ).toMatchObject({ result: "shown", detail: { summary: "Opened Far note" } });
  expect(a.fetchStar).toHaveBeenCalledWith("rhythm:memory:far");
  expect(a.openStar).toHaveBeenCalledWith("rhythm:memory:far");
  const plan = "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a";
  expect(
    await step(command("open_dashboard", { dashboard: plan })),
  ).toMatchObject({ result: "shown", detail: { summary: "Opened Far plan" } });
  expect(a.fetchDashboard).toHaveBeenCalledWith(plan);
  expect(a.openDashboard).toHaveBeenCalledWith(plan);
  // Undo fetches the star it goes back to again, should it have left the map since.
  fetched.clear();
  expect(await step(command("undo"))).toMatchObject({ result: "shown" });
  expect(a.fetchStar).toHaveBeenCalledTimes(2);
  expect(a.restore).toHaveBeenLastCalledWith({
    view: "star",
    star: "rhythm:memory:far",
  });
  // A star still missing once fetched is not opened.
  vi.mocked(a.fetchStar).mockImplementation(async () => {});
  expect(
    await step(command("open_star", { star: "rhythm:memory:gone" })),
  ).toMatchObject({
    result: "failed",
    detail: { summary: "That star isn't on this screen's map." },
  });
  expect(a.openStar).toHaveBeenCalledTimes(1);
  // A corner's dashboard newer than the list: read again, then shown.
  vi.mocked(a.reread).mockImplementation(async () => {
    fresh = true;
  });
  expect(
    await step(command("corner_card", { corner: "main", dashboard: plan })),
  ).toMatchObject({
    result: "shown",
    detail: { summary: "Showing New plan in the Main corner" },
  });
  // And one that still isn't there after reading again: failed, and unchanged.
  fresh = false;
  vi.mocked(a.reread).mockImplementation(async () => {});
  expect(
    await step(command("corner_card", { corner: "main", dashboard: plan })),
  ).toMatchObject({
    result: "failed",
    detail: { summary: "That dashboard isn't among this screen's dashboards." },
  });
  expect(a.setCard).toHaveBeenCalledTimes(1);
});

it("sends its own settings on the first listen and keeps them over the engine's", async () => {
  const { calls, session } = engine();
  const a = actions({ layout: "corners" });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  expect(calls[0].args.settings).toEqual({
    layout: "corners",
    tiles: defaultTiles,
    home: null,
  });
  await act(async () =>
    calls[0].answer({
      command: command("home"),
      screen: self({ layout: "band" }),
    }),
  );
  expect(a.setLayout).not.toHaveBeenCalled();
});

it("reports commands it can't apply, and puts a corner's card back on Undo", async () => {
  const { calls, session } = engine();
  const a = actions({ cards: { training: "old-card" } });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = async (c: ScreenCommand) => {
    const n = calls.length;
    await act(async () => calls[n - 1].answer({ command: c, screen: self() }));
    await vi.waitFor(() => expect(calls).toHaveLength(n + 1));
    return vi.mocked(session.screenDone!).mock.calls.at(-1)![0];
  };
  expect(await step(command("dance"))).toMatchObject({
    result: "failed",
    detail: { summary: "This screen can't do that." },
  });
  expect(await step(command("open_star", { star: "x" }))).toMatchObject({
    result: "failed",
    detail: { summary: "That star isn't on this screen's map." },
  });
  expect(
    await step(
      command("corner_card", {
        corner: "training",
        dashboard: "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
      }),
    ),
  ).toMatchObject({
    result: "shown",
    detail: { summary: "Showing Fictional plan in the Training corner" },
  });
  expect(screen.getByRole("status")).toHaveTextContent(
    "From your assistant: Showing Fictional plan in the Training corner",
  );
  expect(await step(command("undo"))).toMatchObject({
    result: "shown",
    detail: {
      summary: "Undone: Showing Fictional plan in the Training corner",
    },
  });
  expect(a.setCard).toHaveBeenLastCalledWith("training", "old-card");
});

it("names a screen as a screen in its note, so no name reads as the assistant", async () => {
  const { calls, session } = engine();
  render(
    <LiveScreen session={session} screen={me} stored actions={actions()} />,
  );
  const from = (sender: ScreenCommand["sender"]) => ({
    ...command("home"),
    sender,
  });
  await act(async () =>
    calls[0].answer({ command: from({ kind: "assistant" }), screen: self() }),
  );
  expect(screen.getByRole("status")).toHaveTextContent(
    "From your assistant: Back to Home",
  );
  await vi.waitFor(() => expect(calls).toHaveLength(2));
  await act(async () =>
    calls[1].answer({
      command: from({
        kind: "screen",
        screen: "kitchenScreen0000000001",
        name: "your assistant",
      }),
      screen: self(),
    }),
  );
  expect(screen.getByRole("status")).toHaveTextContent(
    "From the screen “your assistant”: Back to Home",
  );
});

it("calls /api/screens with a 40-second wait, and reads off and refused answers", async () => {
  const timeout = vi.spyOn(globalThis, "setTimeout");
  const json = (value: unknown) =>
    new Response(JSON.stringify(value), {
      headers: { "content-type": "application/json" },
    });
  const session = createApiSession(false);
  vi.mocked(fetch).mockResolvedValueOnce(json({ available: false }));
  expect(
    await session.screenListen!({ screen: me.id, name: me.name, hold: 25 }),
  ).toBeNull();
  const [url, init] = vi.mocked(fetch).mock.calls[0];
  expect(url).toBe("/api/screens");
  expect(JSON.parse(String(init!.body))).toEqual({
    op: "listen",
    screen: me.id,
    name: me.name,
    hold: 25,
  });
  expect(timeout).toHaveBeenCalledWith(expect.any(Function), 40000);
  vi.mocked(fetch).mockResolvedValueOnce(json({ available: false }));
  expect(await session.screenList!(me.id)).toEqual([]);
  const send = { screen: me.id, to: "kitchenScreen0000000001" };
  vi.mocked(fetch).mockResolvedValueOnce(
    json({ ok: false, reason: "It wasn't sent: no." }),
  );
  expect(
    await session.screenSend!({ ...send, action: "home", args: {} }),
  ).toEqual({ ok: false, reason: "It wasn't sent: no." });
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("network"));
  expect(
    await session.screenSend!({ ...send, action: "home", args: {} }),
  ).toMatchObject({ ok: false, reason: expect.stringMatching(/^It may have/) });
  vi.mocked(fetch).mockResolvedValueOnce(json({ command: command("home") }));
  expect(
    await session.screenSend!({ ...send, action: "home", args: {} }),
  ).toEqual({ ok: true, command: command("home") });
  // The action's arguments ride as one object, so move_tile's own `to` keeps its meaning.
  vi.mocked(fetch).mockResolvedValueOnce(
    json({ command: command("move_tile") }),
  );
  expect(
    await session.screenSend!({
      ...send,
      action: "move_tile",
      args: { tile: "main", to: "recovery" },
    }),
  ).toMatchObject({ ok: true });
  expect(
    JSON.parse(String(vi.mocked(fetch).mock.calls.at(-1)![1]!.body)),
  ).toEqual({
    op: "send",
    ...send,
    action: "move_tile",
    args: { tile: "main", to: "recovery" },
  });
});

const plan = "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a";
// Each command answered in turn, and how the screen said it went.
function stepper(
  calls: ReturnType<typeof engine>["calls"],
  session: GraphSession,
) {
  return async (c: ScreenCommand) => {
    const n = calls.length;
    await act(async () => calls[n - 1].answer({ command: c, screen: self() }));
    await vi.waitFor(() => expect(calls).toHaveLength(n + 1));
    return vi.mocked(session.screenDone!).mock.calls.at(-1)![0];
  };
}

it("shows a dashboard full screen, the open one when none is named, and puts back what was under it", async () => {
  const { calls, session } = engine();
  let now: ScreenView = { view: "home" },
    saved = true;
  const a = actions({
    view: () => now,
    savedTitle: () => (saved ? "Fictional plan" : null),
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = stepper(calls, session);
  // None open: nothing to show.
  expect(await step(command("full_screen"))).toMatchObject({
    result: "failed",
    detail: { summary: "No dashboard is open" },
  });
  // The one in the reader.
  now = { view: "dashboard", dashboard: plan };
  expect(await step(command("full_screen"))).toMatchObject({
    result: "shown",
    detail: { summary: "Showing Fictional plan full screen" },
  });
  expect(a.fullScreen).toHaveBeenLastCalledWith(plan);
  expect(screen.getByRole("status")).toHaveTextContent(
    "From your assistant: Showing Fictional plan full screen",
  );
  await step(command("undo"));
  expect(a.restore).toHaveBeenLastCalledWith({
    view: "dashboard",
    dashboard: plan,
  });
  // Named, it must be among this screen's dashboards, read again if newer.
  saved = false;
  expect(
    await step(command("full_screen", { dashboard: plan.toUpperCase() })),
  ).toMatchObject({
    result: "failed",
    detail: { summary: "That dashboard isn't among this screen's dashboards." },
  });
  expect(a.reread).toHaveBeenCalled();
  expect(a.fullScreen).toHaveBeenCalledTimes(1);
});

it("ends a full screen for a change to the tiles, and brings it back on Undo", async () => {
  const { calls, session } = engine();
  const covered: ScreenView = {
    view: "home",
    full: { dashboard: plan, home: false },
  };
  const a = actions({ view: () => covered });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = stepper(calls, session);
  expect(await step(command("layout", { layout: "band" }))).toMatchObject({
    result: "shown",
  });
  expect(a.exitFullScreen).toHaveBeenCalledTimes(1);
  await step(command("undo"));
  expect(a.setLayout).toHaveBeenLastCalledWith("hero");
  expect(a.restore).toHaveBeenLastCalledWith(covered);
});

it("keeps a full screen when a corner's card couldn't be changed", async () => {
  const { calls, session } = engine();
  const covered: ScreenView = {
    view: "home",
    full: { dashboard: plan, home: false },
  };
  const a = actions({
    view: () => covered,
    cards: { training: null },
    setCard: vi.fn(async () => {
      throw new Error("unavailable");
    }),
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = stepper(calls, session);
  expect(
    await step(command("corner_card", { corner: "training", dashboard: plan })),
  ).toMatchObject({
    result: "failed",
    detail: { summary: "The Training corner couldn't be changed right now." },
  });
  // Nothing changed, so nothing is left to undo either.
  expect(a.exitFullScreen).not.toHaveBeenCalled();
  expect(await step(command("undo"))).toMatchObject({
    result: "failed",
    detail: { summary: "Nothing to undo" },
  });
});

it("makes a dashboard this screen's Home and back, keeping the view at Home for Undo", async () => {
  const { calls, session } = engine();
  let homeNow: string | null = null;
  const a = actions({
    view: () => ({ view: "home" }),
    setHome: vi.fn((d: string | null) => void (homeNow = d)),
  });
  const { rerender } = render(
    <LiveScreen session={session} screen={me} stored actions={a} />,
  );
  const step = stepper(calls, session);
  expect(await step(command("set_home", { dashboard: plan }))).toMatchObject({
    result: "shown",
    detail: { summary: "Fictional plan is this screen's Home now" },
  });
  expect(a.setHome).toHaveBeenLastCalledWith(plan);
  rerender(
    <LiveScreen
      session={session}
      screen={me}
      stored
      actions={{ ...a, homeDashboard: homeNow }}
    />,
  );
  // Absent or null, the default Home.
  expect(await step(command("set_home", { dashboard: null }))).toMatchObject({
    result: "shown",
    detail: { summary: "This screen's Home is the sky and tiles again" },
  });
  expect(a.setHome).toHaveBeenLastCalledWith(null);
  // Undo puts back the Home it had, and the view it was on at Home.
  await step(command("undo"));
  expect(a.setHome).toHaveBeenLastCalledWith(plan);
  expect(a.restore).toHaveBeenLastCalledWith({ view: "home" });
  // Chosen away from Home, the view isn't touched on Undo.
  vi.mocked(a.restore).mockClear();
  const away = { ...a, view: () => ({ view: "star" as const, star: "x" }) };
  rerender(<LiveScreen session={session} screen={me} stored actions={away} />);
  await step(command("set_home", { dashboard: plan }));
  await step(command("undo"));
  expect(a.restore).not.toHaveBeenCalled();
});

it("sends its Home with its settings, and takes the engine's when it has none of its own", async () => {
  const { calls, session } = engine();
  const a = actions();
  render(
    <LiveScreen session={session} screen={me} stored={false} actions={a} />,
  );
  await act(async () =>
    calls[0].answer({
      command: null,
      screen: self({
        layout: "hero",
        tiles: defaultTiles,
        home: { dashboard: plan },
      }),
    }),
  );
  expect(a.setHome).toHaveBeenCalledWith(plan);
  // A browser with its own copy sends its Home with the rest.
  const kept = engine();
  render(
    <LiveScreen
      session={kept.session}
      screen={me}
      stored
      actions={actions({ homeDashboard: plan })}
    />,
  );
  expect(kept.calls[0].args.settings).toEqual({
    layout: "hero",
    tiles: defaultTiles,
    home: { dashboard: plan },
  });
});

it("hands on the name the engine keeps for this screen", async () => {
  const { calls, session } = engine();
  const named = vi.fn();
  render(
    <LiveScreen
      session={session}
      screen={me}
      stored
      actions={actions()}
      named={named}
    />,
  );
  await act(async () =>
    calls[0].answer({
      command: null,
      screen: { ...self(), name: "Hall 2" },
    }),
  );
  expect(named).toHaveBeenCalledWith("Hall 2");
});

// ----- Piece 3: parts (docs/dev/screens.md 10.5, 10.6 and 10.9).

const board = "00000000-0000-4000-8000-00000000d0a7";
const mapsLayout: PartsLayout = {
  parts: [
    { id: "map", kind: "map", x: 0, y: 0, w: 7, h: 12 },
    { id: "page", kind: "page", x: 7, y: 0, w: 5, h: 8 },
    { id: "glucose", kind: "glucose", x: 7, y: 8, w: 5, h: 4 },
  ],
};
// A dashboard as the grid drawing it says it is (arranging.ts).
function drawn(over: Partial<DashboardGrid> = {}) {
  const grid: DashboardGrid = {
    id: board,
    full: false,
    version: 1,
    latest: 1,
    layout: mapsLayout,
    revision: 0,
    whole: true,
    fold: "saved",
    drawn: true,
    show: (layout, revision) => Object.assign(grid, { layout, revision }),
    reread: vi.fn(),
    ...over,
  };
  return { grid, gone: drawGrid(grid) };
}
const saving = () =>
  vi.fn(
    async (
      _id: string,
      _version: number,
      layout: PartsLayout,
      base: number,
    ): Promise<LayoutAnswer> => ({
      ok: true,
      layout,
      layoutWhole: true,
      layoutRevision: base + 1,
    }),
  );
const failedWith = (summary: string) => ({
  result: "failed",
  detail: { summary },
});

it("arranges the dashboard it shows as a layout revision, reports its parts, and undoes it", async () => {
  const { calls, session } = engine();
  const save = saving();
  Object.assign(session, { dashboardLayout: save });
  const { grid, gone } = drawn();
  const a = actions({
    view: () => ({ view: "dashboard", dashboard: board }),
    showing: () => ({
      view: "dashboard",
      dashboard: { id: board, title: "Training map" },
      layout: "hero",
      tiles: defaultTiles,
      narrow: false,
    }),
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  // What it shows names its parts in reading order.
  expect(calls[0].args.showing).toMatchObject({
    parts: [
      { id: "map", kind: "map", x: 0, y: 0, w: 7, h: 12 },
      { id: "page", kind: "page", x: 7, y: 0, w: 5, h: 8 },
      { id: "glucose", kind: "glucose", x: 7, y: 8, w: 5, h: 4 },
    ],
  });
  const step = stepper(calls, session);
  expect(
    await step(
      command("move", { part: "map", place: "right", dashboard: board }),
    ),
  ).toMatchObject({ result: "shown" });
  expect(save).toHaveBeenCalledTimes(1);
  const [, version, moved, base] = save.mock.calls[0];
  expect([version, base]).toEqual([1, 0]);
  expect(moved.parts.find((p) => p.id === "map")).toMatchObject({
    x: 5,
    y: 0,
    w: 7,
    h: 12,
  });
  expect(grid.revision).toBe(1);
  expect(screen.getByRole("status")).toHaveTextContent(
    /^From your assistant: /,
  );
  // Undo saves the earlier layout back from the revision the change saved.
  expect(await step(command("undo"))).toMatchObject({ result: "shown" });
  expect(save).toHaveBeenCalledTimes(2);
  expect(save.mock.calls[1].slice(2, 4)).toEqual([mapsLayout, 1]);
  expect(grid.revision).toBe(2);
  // Somebody changed it since: nothing undone.
  expect(
    await step(command("resize", { part: "glucose", size: "taller" })),
  ).toMatchObject({ result: "shown" });
  save.mockResolvedValueOnce({
    ok: false,
    reason: "Changed",
    layout: mapsLayout,
    layoutWhole: true,
    layoutRevision: 9,
  });
  expect(await step(command("undo"))).toMatchObject(
    failedWith("The layout changed since; nothing undone"),
  );
  gone();
});

it("refuses part commands where nothing has parts, for another dashboard, an earlier version and a layout it can't arrange", async () => {
  const { calls, session } = engine();
  Object.assign(session, { dashboardLayout: saving() });
  let view: ScreenView = { view: "star", star: "x" };
  render(
    <LiveScreen
      session={session}
      screen={me}
      stored
      actions={actions({ view: () => view })}
    />,
  );
  const step = stepper(calls, session);
  expect(
    await step(command("move", { part: "map", place: "left" })),
  ).toMatchObject(
    failedWith("Nothing here has parts; open a dashboard or go Home"),
  );
  view = { view: "dashboard", dashboard: board };
  const old = drawn({ version: 1, latest: 2 });
  expect(
    await step(
      command("move", {
        part: "map",
        place: "right",
        dashboard: "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
      }),
    ),
  ).toMatchObject(failedWith("This screen isn't showing that dashboard now"));
  expect(
    await step(
      command("move", { part: "map", place: "right", dashboard: null }),
    ),
  ).toMatchObject(failedWith("This screen isn't at its Home now"));
  expect(
    await step(command("move", { part: "map", place: "right" })),
  ).toMatchObject(
    failedWith("This is an earlier version; open the newest to arrange it"),
  );
  old.gone();
  const later = drawn({ whole: false });
  expect(
    await step(command("focus_map", { focus: { query: "training" } })),
  ).toMatchObject(failedWith("This screen can't arrange this dashboard yet"));
  later.gone();
  // A folded screen still takes it, and says so.
  const folded = drawn({ fold: "one" });
  expect(
    await step(command("focus_map", { focus: { query: "training" } })),
  ).toMatchObject({
    result: "shown",
    detail: { note: "Saved; this screen shows the parts in a column" },
  });
  folded.gone();
});

it("makes a Home grid only when a change succeeds, swaps two corners as move_tile, and a layout drops the grid", async () => {
  const { calls, session } = engine();
  const a = actions();
  a.grid = null;
  a.setGrid = vi.fn((g: HomeGrid | null) => {
    a.grid = g;
  });
  a.setTiles = vi.fn((t: Tiles) => {
    a.tiles = t;
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  // At Home without a grid it reports the one its first move would make (the hero table).
  expect(calls[0].args.showing).toMatchObject({
    parts: expect.arrayContaining([
      { id: "map", kind: "map", x: 3, y: 0, w: 9, h: 8 },
    ]),
  });
  const step = stepper(calls, session);
  // Home's map takes no focus; a refused move makes no grid.
  expect(await step(command("focus_map", { focus: null }))).toMatchObject(
    failedWith("Home's map takes no focus"),
  );
  expect(
    (await step(command("move", { part: "map", place: "up" }))).result,
  ).toBe("failed");
  expect(a.setGrid).not.toHaveBeenCalled();
  // Two corners trading places is move_tile: the tiles swap and no grid is made.
  expect(
    await step(command("move", { part: "main", with: "recovery" })),
  ).toMatchObject({ result: "shown" });
  expect(a.setTiles).toHaveBeenLastCalledWith({
    ...defaultTiles,
    main: "recovery",
    recovery: "main",
  });
  expect(a.setGrid).not.toHaveBeenCalled();
  // Undo puts the tiles back.
  await step(command("undo"));
  expect(a.tiles).toEqual(defaultTiles);
  // The map moving down below the corners makes the grid.
  expect(
    await step(
      command("move", { part: "map", place: "down", dashboard: null }),
    ),
  ).toMatchObject({ result: "shown" });
  expect(a.setGrid).toHaveBeenCalledTimes(1);
  const made = a.grid!;
  expect(made.parts.find((p) => p.id === "map")).toMatchObject({ y: 4, h: 8 });
  // The settings carry it from now on.
  expect(calls.at(-1)!.args.settings).toMatchObject({ grid: made });
  // A layout drops the grid, and Undo brings it back.
  await step(command("layout", { layout: "hero" }));
  expect(a.grid).toBeNull();
  await step(command("undo"));
  expect(a.grid).toEqual(made);
});

it("keeps no grid once moves come back to the designed Home, and Undo in reverse puts back each step", async () => {
  const { calls, session } = engine();
  const a = actions();
  a.grid = null;
  a.setGrid = vi.fn((g: HomeGrid | null) => {
    a.grid = g;
  });
  a.setTiles = vi.fn((t: Tiles) => {
    a.tiles = t;
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = stepper(calls, session);
  const main = () => a.grid?.parts.find((p) => p.id === "main");
  const swapped = { ...defaultTiles, main: "recovery", recovery: "main" };
  // The Main corner past the sky makes a grid; it trades places with Recovery, and Recovery
  // goes back to its place; all on the grid.
  await step(command("move", { part: "main", place: "right" }));
  expect(main()).toEqual({ id: "main", x: 9, y: 0, w: 3, h: 12 });
  const right = a.grid;
  await step(command("move", { part: "main", with: "recovery" }));
  expect(a.tiles).toEqual(swapped);
  expect(main()).toEqual({ id: "main", x: 0, y: 8, w: 3, h: 4 });
  const traded = a.grid;
  await step(command("move_tile", { tile: "recovery", to: "recovery" }));
  expect(a.tiles).toEqual(defaultTiles);
  expect(a.grid).toEqual(right);
  // Back past the sky: the designed Home again, with no grid, and the settings carry none.
  expect(
    await step(command("move", { part: "main", place: "left" })),
  ).toMatchObject({
    result: "shown",
    detail: { summary: "Moved the Main corner left" },
  });
  expect(a.grid).toBeNull();
  expect(a.tiles).toEqual(defaultTiles);
  expect(calls.at(-1)!.args.settings).not.toHaveProperty("grid");
  // Undo in reverse puts back each step, down to Home as it was.
  await step(command("undo"));
  expect([a.grid, a.tiles]).toEqual([right, defaultTiles]);
  await step(command("undo"));
  expect([a.grid, a.tiles]).toEqual([traded, swapped]);
  await step(command("undo"));
  expect([a.grid, a.tiles]).toEqual([right, defaultTiles]);
  await step(command("undo"));
  expect([a.grid, a.tiles]).toEqual([null, defaultTiles]);
});

it("takes a person's own change on a grid with the same note, naming no sender, and the same Undo", async () => {
  const { session } = engine();
  const save = saving();
  Object.assign(session, { dashboardLayout: save });
  const { grid, gone } = drawn();
  render(
    <LiveScreen session={session} screen={me} stored actions={actions()} />,
  );
  let outcome: unknown;
  await act(async () => {
    outcome = await arrangeInPlace({
      kind: "dashboard",
      grid,
      commands: [{ action: "move", part: "map", place: "right" }],
      summary: "Moved the map",
    });
  });
  expect(outcome).toEqual({ ok: true });
  const note = screen.getByRole("status");
  expect(note).toHaveTextContent("Moved the map");
  expect(note).not.toHaveTextContent("From");
  // The note's Undo puts it back.
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Undo" })),
  );
  expect(save).toHaveBeenCalledTimes(2);
  expect(save.mock.calls[1].slice(2, 4)).toEqual([mapsLayout, 1]);
  gone();
});

it("keeps an Undo that may work later, and lets one the engine refuses go, saying why", async () => {
  const { calls, session } = engine();
  const save = saving();
  Object.assign(session, { dashboardLayout: save });
  const { grid, gone } = drawn();
  const a = actions({ view: () => ({ view: "dashboard", dashboard: board }) });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  const step = stepper(calls, session);
  expect(
    await step(command("move", { part: "map", place: "right" })),
  ).toMatchObject({ result: "shown" });
  // No answer came back: it stays, to be tried again.
  save.mockResolvedValueOnce({
    ok: false,
    reason: "The arrangement may have been saved: no answer came back.",
    transient: true,
  });
  expect(await step(command("undo"))).toMatchObject(
    failedWith("The arrangement may have been saved: no answer came back."),
  );
  // A newer version since: refused for good, so it goes, and nothing under it is blocked.
  grid.latest = 2;
  save.mockResolvedValueOnce({
    ok: false,
    reason: "This is an earlier version; open the newest to arrange it.",
  });
  expect(await step(command("undo"))).toMatchObject(
    failedWith("The dashboard has a newer version since; nothing undone"),
  );
  expect(await step(command("undo"))).toMatchObject(
    failedWith("Nothing to undo"),
  );
  expect(save).toHaveBeenCalledTimes(3);
  gone();
});

it("shows a change made by hand that wasn't made, and a drop the grid refused, in the note", async () => {
  const { session } = engine();
  const save = saving();
  save.mockResolvedValueOnce({
    ok: false,
    reason: "The engine refused that layout.",
  });
  Object.assign(session, { dashboardLayout: save });
  const { grid, gone } = drawn();
  render(
    <LiveScreen session={session} screen={me} stored actions={actions()} />,
  );
  let outcome: unknown;
  await act(async () => {
    outcome = await arrangeInPlace({
      kind: "dashboard",
      grid,
      commands: [{ action: "move", part: "map", place: "right" }],
      summary: "Moved the map",
    });
  });
  expect(outcome).toEqual({
    ok: false,
    reason: "The engine refused that layout.",
  });
  expect(screen.getByRole("status")).toHaveTextContent(
    "The engine refused that layout.",
  );
  expect(screen.queryByRole("button", { name: "Undo" })).toBeNull();
  act(() => void refuseInPlace("No room there"));
  expect(screen.getByRole("status")).toHaveTextContent("No room there");
  expect(screen.queryByRole("button", { name: "Undo" })).toBeNull();
  gone();
});

it("arranges a dashboard by command only once its newest version's page has drawn", async () => {
  const { calls, session } = engine();
  const save = saving();
  Object.assign(session, { dashboardLayout: save });
  const { grid, gone } = drawn({ drawn: false });
  render(
    <LiveScreen
      session={session}
      screen={me}
      stored
      actions={actions({
        view: () => ({ view: "dashboard", dashboard: board }),
      })}
    />,
  );
  const step = async (c: ScreenCommand) => {
    const n = calls.length;
    await act(async () => calls[n - 1].answer({ command: c, screen: self() }));
    await vi.waitFor(() => expect(calls).toHaveLength(n + 1), {
      timeout: 5000,
    });
    return vi.mocked(session.screenDone!).mock.calls.at(-1)![0];
  };
  // Still loading: it waits a moment, then says so and saves nothing.
  expect(
    await step(command("move", { part: "map", place: "right" })),
  ).toMatchObject(
    failedWith(
      "This dashboard hasn't finished drawing yet; nothing was changed",
    ),
  );
  expect(save).not.toHaveBeenCalled();
  // Drawn while the command waits: it goes ahead.
  setTimeout(() => (grid.drawn = true), 200);
  expect(
    await step(command("move", { part: "map", place: "right" })),
  ).toMatchObject({ result: "shown" });
  expect(save).toHaveBeenCalledTimes(1);
  gone();
});

it("says so with Undo when a layout chosen in settings drops the Home grid", async () => {
  const { session } = engine();
  const grid: HomeGrid = {
    parts: [
      { id: "map", x: 3, y: 4, w: 9, h: 8 },
      { id: "main", x: 0, y: 0, w: 3, h: 12 },
      { id: "recovery", x: 3, y: 0, w: 3, h: 4 },
      { id: "training", x: 6, y: 0, w: 3, h: 4 },
      { id: "changed", x: 9, y: 0, w: 3, h: 4 },
    ],
  };
  const a = actions();
  a.grid = grid;
  a.setGrid = vi.fn((g: HomeGrid | null) => {
    a.grid = g;
  });
  a.setLayout = vi.fn((l) => {
    a.layout = l;
  });
  render(<LiveScreen session={session} screen={me} stored actions={a} />);
  await act(async () => {
    await arrangeInPlace({ kind: "layout", layout: "band" });
  });
  expect(a.layout).toBe("band");
  expect(a.grid).toBeNull();
  expect(screen.getByRole("status")).toHaveTextContent(
    "Switched to the Rhythm band layout",
  );
  await act(async () =>
    fireEvent.click(screen.getByRole("button", { name: "Undo" })),
  );
  expect(a.layout).toBe("hero");
  expect(a.grid).toEqual(grid);
});

it("sends its settings again when a later answer lacks the Home grid it keeps, but not to an engine that drops it", async () => {
  const { calls, session } = engine();
  const grid: HomeGrid = {
    parts: [
      { id: "map", x: 3, y: 4, w: 9, h: 8 },
      { id: "main", x: 0, y: 0, w: 3, h: 12 },
      { id: "recovery", x: 3, y: 0, w: 3, h: 4 },
      { id: "training", x: 6, y: 0, w: 3, h: 4 },
      { id: "changed", x: 9, y: 0, w: 3, h: 4 },
    ],
  };
  render(
    <LiveScreen
      session={session}
      screen={me}
      stored
      actions={actions({ grid, setGrid: vi.fn() })}
    />,
  );
  expect(calls[0].args.settings).toMatchObject({ grid });
  const next = async (settings: Record<string, unknown>) => {
    const n = calls.length;
    await act(async () =>
      calls[n - 1].answer({ command: command("home"), screen: self(settings) }),
    );
    await vi.waitFor(() => expect(calls).toHaveLength(n + 1));
    return calls[n].args.settings;
  };
  const none = { layout: "hero", tiles: defaultTiles, home: null };
  expect(await next({ ...none, grid })).toBeUndefined();
  // An older tab listened since and replaced the engine's copy: it is sent again.
  expect(await next(none)).toMatchObject({ grid });
  // That listen carried the grid and its answer has none: an older engine, so no more.
  expect(await next(none)).toBeUndefined();
  expect(await next(none)).toBeUndefined();
});
