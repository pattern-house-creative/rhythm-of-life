import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import DashboardView from "../src/DashboardView";
import { createDemoDashboards } from "../src/demoDashboards";
import type { Dashboard, DashboardData, LayoutAnswer } from "../src/dashboards";
import {
  drawGrid,
  restoreArrangement,
  saveArrangement,
} from "../src/arranging";
import type { DashboardGrid } from "../src/arranging";
import type { Layout } from "../src/parts";
import { dashboardCache } from "../src/dashboardCache";

// A dashboard's grid of parts in DashboardView (docs/dev/screens.md 10.2, 10.3, 10.9 and 10.10):
// a dashboard never arranged draws as before, a layout draws its page among the app's own parts
// with the meta line below, a newer layout from the feed read moves parts without loading the
// frame again, and only the newest version, drawn and read whole, is arranged by hand.
// Fictional dashboards only (the demo's).
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  dashboardCache.clear();
});
const boards = createDemoDashboards();
const [week, , maps] = boards.list();
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
// The page's side of its channel, once its frame has loaded.
async function page(before: number) {
  await waitFor(() => expect(channels.length).toBeGreaterThan(before));
  const port = channels.at(-1)!.port2;
  return { say: (m: unknown) => port.postMessage(m) };
}
function session(dashboard: Dashboard, data: DashboardData) {
  return {
    dashboard: vi.fn(async () => dashboard),
    dashboardData: vi.fn(async () => data),
    dashboardLayout: vi.fn(
      async (
        _id: string,
        _version: number,
        layout: Layout,
        base: number,
      ): Promise<LayoutAnswer> => ({
        ok: true,
        layout,
        layoutWhole: true,
        layoutRevision: base + 1,
      }),
    ),
  };
}
const native = (p: { id: string; kind: string }) => <p>{`${p.kind} part`}</p>;

it("draws a dashboard never arranged exactly as before parts", async () => {
  const s = session(boards.get(week.id)!, boards.data(week.id)!);
  const { container } = render(
    <DashboardView
      id={week.id}
      session={s}
      onOpenStar={() => {}}
      renderPart={native}
    />,
  );
  const frame = await screen.findByTitle("Sample training week dashboard");
  const grid = container.querySelector(".parts")!;
  expect(grid).toHaveClass("plain");
  expect(grid.contains(frame)).toBe(true);
  expect(screen.queryByRole("group")).toBeNull();
  expect(screen.queryByRole("button", { name: /^Arrange/ })).toBeNull();
  expect(screen.queryByText(/part$/)).toBeNull();
});

it("draws a layout's page among the app's own parts, labelled, with the meta line below", async () => {
  const s = session(boards.get(maps.id)!, boards.data(maps.id)!);
  const { container } = render(
    <DashboardView
      id={maps.id}
      session={s}
      onOpenStar={() => {}}
      renderPart={native}
    />,
  );
  const frame = await screen.findByTitle("Training map dashboard");
  const map = screen.getByRole("group", { name: "Map, near Training" });
  expect(within(map).getByText("map part")).toBeInTheDocument();
  expect(
    within(screen.getByRole("group", { name: "Glucose" })).getByText(
      "glucose part",
    ),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("group", { name: "Training map" }).contains(frame),
  ).toBe(true);
  const grid = container.querySelector(".parts")!;
  expect(grid).toHaveClass("saved");
  // The dashboard's own line stays below the grid.
  const meta = container.querySelector(".dashboard-meta")!;
  expect(
    grid.compareDocumentPosition(meta) & Node.DOCUMENT_POSITION_FOLLOWING,
  ).toBeTruthy();
});

it("draws a newer layout from the feed read without loading the frame again", async () => {
  const before = capture();
  const data = boards.data(maps.id)!;
  const s = session(boards.get(maps.id)!, data);
  render(
    <DashboardView
      id={maps.id}
      session={s}
      onOpenStar={() => {}}
      renderPart={native}
    />,
  );
  const frame = await screen.findByTitle("Training map dashboard");
  await page(before);
  const loaded = channels.length;
  const moved: Layout = {
    parts: [
      {
        id: "map",
        kind: "map",
        x: 5,
        y: 0,
        w: 7,
        h: 12,
        focus: data.dashboard.layout!.parts[0].focus,
      },
      { id: "page", kind: "page", x: 0, y: 0, w: 5, h: 8 },
      { id: "glucose", kind: "glucose", x: 0, y: 8, w: 5, h: 4 },
    ],
  };
  s.dashboardData.mockResolvedValue({
    ...data,
    dashboard: {
      ...data.dashboard,
      layout: moved,
      layoutRevision: 1,
      layoutWhole: true,
    },
  });
  await act(async () =>
    fireEvent.click(
      screen.getByRole("button", { name: "Read the feeds again" }),
    ),
  );
  await waitFor(() =>
    expect(
      document.querySelector<HTMLElement>('[data-part="map"]')!.style
        .gridColumn,
    ).toBe("6 / span 7"),
  );
  expect(screen.getByTitle("Training map dashboard")).toBe(frame);
  expect(channels.length).toBe(loaded);
});

it("arranges by hand only once the newest version drew, and saves it as a layout revision", async () => {
  const before = capture();
  const s = session(boards.get(maps.id)!, boards.data(maps.id)!);
  render(
    <DashboardView
      id={maps.id}
      session={s}
      onOpenStar={() => {}}
      renderPart={native}
    />,
  );
  await screen.findByTitle("Training map dashboard");
  const p = await page(before);
  // Nothing is arranged until the page has drawn.
  expect(screen.queryByRole("button", { name: "Arrange map" })).toBeNull();
  await act(async () => p.say({ type: "ready" }));
  const grip = await screen.findByRole("button", { name: "Arrange map" });
  fireEvent.keyDown(grip, { key: "Enter" });
  fireEvent.keyDown(grip, { key: "ArrowRight" });
  await act(async () => fireEvent.keyDown(grip, { key: "Enter" }));
  await waitFor(() => expect(s.dashboardLayout).toHaveBeenCalledTimes(1));
  const [id, version, layout, base, note] = s.dashboardLayout.mock.calls[0];
  expect([id, version, base]).toEqual([maps.id, 1, 0]);
  expect(layout.parts.find((q) => q.id === "map")).toMatchObject({
    x: 5,
    w: 7,
  });
  // Every member goes back exactly as read, apart from the change.
  expect(layout.parts.find((q) => q.id === "map")!.focus).toEqual(
    maps.layout!.parts[0].focus,
  );
  expect(note).toMatch(/map/);
  await waitFor(() =>
    expect(
      document.querySelector<HTMLElement>('[data-part="page"]')!.style
        .gridColumn,
    ).toBe("1 / span 5"),
  );
});

it("shows why a change made by hand wasn't made, not only to screen readers", async () => {
  const before = capture();
  const s = session(boards.get(maps.id)!, boards.data(maps.id)!);
  s.dashboardLayout.mockResolvedValueOnce({
    ok: false,
    reason: "The engine refused that layout.",
  });
  render(
    <DashboardView
      id={maps.id}
      session={s}
      onOpenStar={() => {}}
      renderPart={native}
    />,
  );
  await screen.findByTitle("Training map dashboard");
  const p = await page(before);
  await act(async () => p.say({ type: "ready" }));
  const grip = await screen.findByRole("button", { name: "Arrange map" });
  fireEvent.keyDown(grip, { key: "Enter" });
  fireEvent.keyDown(grip, { key: "ArrowRight" });
  await act(async () => fireEvent.keyDown(grip, { key: "Enter" }));
  // Below the page, where a write's outcome shows (no screen runner here).
  expect(
    await screen.findByText("The engine refused that layout.", {
      selector: ".dashboard-change",
    }),
  ).toBeVisible();
  // The part went back where it was.
  expect(
    document.querySelector<HTMLElement>('[data-part="map"]')!.style.gridColumn,
  ).toBe("1 / span 7");
});

it("arranges no earlier version and no layout it didn't read whole", async () => {
  const before = capture();
  const unread: Dashboard = { ...boards.get(maps.id)!, layoutWhole: false };
  const s = session(unread, boards.data(maps.id)!);
  render(
    <DashboardView
      id={maps.id}
      session={s}
      onOpenStar={() => {}}
      renderPart={native}
    />,
  );
  await screen.findByTitle("Training map dashboard");
  const p = await page(before);
  await act(async () => p.say({ type: "ready" }));
  expect(
    screen.getByRole("group", { name: "Map, near Training" }),
  ).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Arrange/ })).toBeNull();
});

// A grid as arranging.ts sees it, drawing whatever it is shown.
function drawnGrid(
  layout: Layout | null,
  revision = 0,
): DashboardGrid & { shown: unknown[] } {
  const shown: unknown[] = [];
  const grid = {
    id: maps.id,
    full: false,
    version: 1,
    latest: 1,
    layout,
    revision,
    whole: true,
    fold: "saved" as const,
    drawn: true,
    shown,
    show: (l: Layout | null, r: number) => {
      shown.push([l, r]);
      grid.layout = l;
      grid.revision = r;
    },
    reread: vi.fn(),
  };
  return grid;
}
const right = (l: Layout) => {
  const map = l.parts.find((p) => p.id === "map")!;
  return {
    ok: true as const,
    grid: { parts: l.parts.map((p) => (p === map ? { ...p, x: p.x + 1 } : p)) },
    summary: "Moved the map",
    announcement: "Map moved",
  };
};

it("makes a change once more on a layout that changed since, and refuses a second change meanwhile", async () => {
  const layout = maps.layout!;
  const theirs: Layout = {
    parts: layout.parts.map((p) => (p.id === "map" ? { ...p, w: 6 } : p)),
  };
  const save = vi
    .fn()
    .mockResolvedValueOnce({
      ok: false,
      reason: "Changed",
      layout: theirs,
      layoutWhole: true,
      layoutRevision: 3,
    })
    .mockImplementationOnce(
      async (_i: string, _v: number, l: Layout, base: number) => ({
        ok: true,
        layout: l,
        layoutWhole: true,
        layoutRevision: base + 1,
      }),
    );
  const grid = drawnGrid(layout);
  const saved = await saveArrangement(save, grid, right);
  expect(saved).toMatchObject({
    ok: true,
    version: 1,
    base: 3,
    revision: 4,
    before: theirs,
  });
  expect(save.mock.calls.map((c) => c[3])).toEqual([0, 3]);
  // The second save is the change made on their layout: their width, one column to the right.
  expect((save.mock.calls[1][2] as Layout).parts[0]).toMatchObject({
    x: 1,
    w: 6,
  });
  // Changed again meanwhile: nothing more is sent.
  const again = vi.fn().mockResolvedValue({
    ok: false,
    reason: "Changed",
    layout: theirs,
    layoutWhole: true,
    layoutRevision: 5,
  });
  expect(await saveArrangement(again, drawnGrid(layout), right)).toEqual({
    ok: false,
    reason: "The layout changed again meanwhile; nothing was changed",
  });
  expect(again).toHaveBeenCalledTimes(2);
});

it("shows a saved arrangement on every grid drawing that version, not only the one that made it", async () => {
  const layout = maps.layout!;
  const save = vi.fn(
    async (_i: string, _v: number, l: Layout, base: number) => ({
      ok: true as const,
      layout: l,
      layoutWhole: true,
      layoutRevision: base + 1,
    }),
  );
  // The dashboard full screen, the reader under it, and an earlier version in a panel.
  const full = drawnGrid(layout),
    reader = drawnGrid(layout),
    earlier = drawnGrid(layout);
  earlier.version = 0;
  const gone = [reader, earlier].map(drawGrid);
  try {
    expect(await saveArrangement(save, full, right)).toMatchObject({
      ok: true,
      revision: 1,
    });
    expect(full.layout).toEqual(save.mock.calls[0][2]);
    expect(reader.layout).toEqual(save.mock.calls[0][2]);
    expect(reader.revision).toBe(1);
    // Only the saved layout reaches the other grid, never the change on its way.
    expect(reader.shown).toEqual([[save.mock.calls[0][2], 1]]);
    expect(earlier.shown).toEqual([]);
  } finally {
    for (const g of gone) g();
  }
});

it("puts a refused change back, reads the dashboard again, and never sends it twice", async () => {
  const layout = maps.layout!;
  const save = vi.fn().mockResolvedValue({
    ok: false,
    reason: "The arrangement may have been saved: no answer came back.",
  });
  const grid = drawnGrid(layout);
  expect(await saveArrangement(save, grid, right)).toEqual({
    ok: false,
    reason: "The arrangement may have been saved: no answer came back.",
  });
  expect(save).toHaveBeenCalledTimes(1);
  expect(grid.layout).toEqual(layout);
  expect(grid.reread).toHaveBeenCalled();
});

it("undoes an arrangement only while nobody changed it since", async () => {
  const layout = maps.layout!;
  const stale = vi.fn().mockResolvedValue({
    ok: false,
    reason: "Changed",
    layout,
    layoutWhole: true,
    layoutRevision: 7,
  });
  expect(
    await restoreArrangement(
      stale,
      { id: maps.id, version: 1, layout, revision: 4 },
      "Undone: Moved the map",
    ),
  ).toEqual({
    ok: false,
    reason: "The layout changed since; nothing undone",
    stale: true,
  });
  const fine = vi.fn().mockResolvedValue({
    ok: true,
    layout,
    layoutWhole: true,
    layoutRevision: 5,
  });
  expect(
    await restoreArrangement(
      fine,
      { id: maps.id, version: 1, layout, revision: 4 },
      "Undone: Moved the map",
    ),
  ).toEqual({
    ok: true,
    revision: 5,
  });
  expect(fine).toHaveBeenCalledWith(
    maps.id,
    1,
    layout,
    4,
    "Undone: Moved the map",
  );
});
