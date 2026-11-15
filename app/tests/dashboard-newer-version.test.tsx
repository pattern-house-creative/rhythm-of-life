import {
  act,
  fireEvent,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import DashboardView from "../src/DashboardView";
import { createDemoDashboards } from "../src/demoDashboards";
import type { Dashboard, LayoutAnswer } from "../src/dashboards";
import type { Layout } from "../src/parts";
import { dashboardCache } from "../src/dashboardCache";
import {
  dashboardSaved,
  refreshMs,
  useDashboardVersion,
} from "../src/useDashboardVersion";

// An open dashboard (a wall's full screen, say) picks up a version saved after it opened: by an
// AI's save_dashboard, or in Studio. Fictional dashboards only (the demo's).
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  dashboardCache.clear();
});
const boards = createDemoDashboards();
const [, glance, maps] = boards.list();
// The board's versions as the engine keeps them: `saved` of them, the last the latest.
function engine(board: Dashboard) {
  const versions = new Map<number, Dashboard>([[1, board]]);
  const at = (version: number) =>
    versions.get(version) ?? {
      ...board,
      title: `${board.title}, version ${version}`,
      version,
    };
  const state = { latest: 1 };
  const save = () => {
    state.latest += 1;
    versions.set(state.latest, at(state.latest));
  };
  const get = (version = state.latest) => ({
    ...at(version),
    latest: state.latest,
  });
  const session = {
    dashboard: vi.fn(async (_id: string, version?: number) => get(version)),
    dashboardData: vi.fn(async (id: string, version: number) => ({
      ...boards.data(id)!,
      dashboard: { ...boards.data(id)!.dashboard, version },
    })),
    // The list has no pages: each dashboard's latest version.
    dashboards: vi.fn(async () => [get()]),
  };
  return { session, save, state };
}

it("draws a newer version an AI saved, found every two minutes with the feeds", async () => {
  vi.useFakeTimers();
  const { session, save } = engine(boards.get(glance.id)!);
  const { result } = renderHook(() => useDashboardVersion(glance.id, session));
  await act(() => vi.advanceTimersByTimeAsync(0));
  expect(result.current.dashboard?.version).toBe(1);
  expect(session.dashboard).toHaveBeenCalledTimes(1);
  // Nothing new: the list is read, and no page.
  await act(() => vi.advanceTimersByTimeAsync(refreshMs));
  expect(session.dashboards).toHaveBeenCalledTimes(1);
  expect(session.dashboard).toHaveBeenCalledTimes(1);
  save();
  await act(() => vi.advanceTimersByTimeAsync(refreshMs));
  expect(result.current.dashboard?.version).toBe(2);
  expect(result.current.dashboard?.latest).toBe(2);
  // Its own feeds are read as it opens.
  expect(session.dashboardData).toHaveBeenLastCalledWith(
    glance.id,
    2,
    expect.anything(),
  );
});

it("draws a version saved in Studio at once, and looks again as the screen comes back into view", async () => {
  const { session, save } = engine(boards.get(glance.id)!);
  const { result } = renderHook(() => useDashboardVersion(glance.id, session));
  await waitFor(() => expect(result.current.dashboard?.version).toBe(1));
  save();
  act(() => dashboardSaved("an-other-dashboard"));
  act(() => dashboardSaved(glance.id));
  await waitFor(() => expect(result.current.dashboard?.version).toBe(2));
  expect(session.dashboards).toHaveBeenCalledTimes(1);
  save();
  act(() => document.dispatchEvent(new Event("visibilitychange")));
  await waitFor(() => expect(result.current.dashboard?.version).toBe(3));
});

it("keeps a version chosen by hand, and tries the newest again once a fix for it is saved", async () => {
  vi.useFakeTimers();
  const { session, save } = engine(boards.get(glance.id)!);
  save();
  const { result } = renderHook(() => useDashboardVersion(glance.id, session));
  await act(() => vi.advanceTimersByTimeAsync(0));
  expect(result.current.dashboard?.version).toBe(2);
  // Version 2 doesn't draw: version 1 opens in its place.
  act(() => result.current.fail("Syntax error"));
  await act(() => vi.advanceTimersByTimeAsync(0));
  expect(result.current.dashboard?.version).toBe(1);
  await act(() => vi.advanceTimersByTimeAsync(refreshMs));
  expect(result.current.dashboard?.version).toBe(1);
  // The AI saves a fix.
  save();
  await act(() => vi.advanceTimersByTimeAsync(refreshMs));
  expect(result.current.dashboard?.version).toBe(3);
  expect(result.current.failed).toBeNull();
  // A version someone chose stays, with no list read for it.
  act(() => result.current.setAsked(1));
  await act(() => vi.advanceTimersByTimeAsync(0));
  const reads = session.dashboards.mock.calls.length;
  save();
  await act(() => vi.advanceTimersByTimeAsync(refreshMs));
  expect(result.current.dashboard?.version).toBe(1);
  expect(session.dashboards).toHaveBeenCalledTimes(reads);
});

it("lets an arrangement being saved finish first, says why it wasn't made, then draws the newer version", async () => {
  const channels: MessageChannel[] = [];
  const Channel = globalThis.MessageChannel;
  vi.stubGlobal(
    "MessageChannel",
    class extends Channel {
      constructor() {
        super();
        channels.push(this);
      }
    },
  );
  const { session, save } = engine(boards.get(maps.id)!);
  let answer!: (a: LayoutAnswer) => void;
  const arranged = {
    ...session,
    dashboardLayout: vi.fn(
      (_id: string, _version: number, _layout: Layout, _base: number) =>
        new Promise<LayoutAnswer>((resolve) => {
          answer = resolve;
        }),
    ),
  };
  render(
    <DashboardView
      id={maps.id}
      session={arranged}
      onOpenStar={() => {}}
      renderPart={(p) => <p>{`${p.kind} part`}</p>}
    />,
  );
  await screen.findByTitle("Training map dashboard");
  await waitFor(() => expect(channels.length).toBeGreaterThan(0));
  await act(async () => channels.at(-1)!.port2.postMessage({ type: "ready" }));
  const grip = await screen.findByRole("button", { name: "Arrange map" });
  fireEvent.keyDown(grip, { key: "Enter" });
  fireEvent.keyDown(grip, { key: "ArrowRight" });
  await act(async () => fireEvent.keyDown(grip, { key: "Enter" }));
  await waitFor(() =>
    expect(arranged.dashboardLayout).toHaveBeenCalledTimes(1),
  );
  // An AI saves version 2 while the change is on its way: version 1 stays, with the change.
  save();
  await act(async () => document.dispatchEvent(new Event("visibilitychange")));
  await waitFor(() => expect(session.dashboards).toHaveBeenCalled());
  expect(screen.getByTitle("Training map dashboard")).toBeInTheDocument();
  expect(
    document.querySelector<HTMLElement>('[data-part="map"]')!.style.gridColumn,
  ).toBe("6 / span 7");
  // The engine refuses it: the reason shows, and the newer version is drawn.
  await act(async () =>
    answer({
      ok: false,
      reason: "This is an earlier version; open the newest to arrange it.",
    }),
  );
  expect(
    await screen.findByText(
      "This is an earlier version; open the newest to arrange it.",
      { selector: ".dashboard-change" },
    ),
  ).toBeVisible();
  expect(
    await screen.findByTitle("Training map, version 2 dashboard"),
  ).toBeInTheDocument();
});
