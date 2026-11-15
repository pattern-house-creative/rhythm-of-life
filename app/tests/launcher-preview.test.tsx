import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import fixture from "../../contracts/fixtures/app_dashboards.dashboard.example.json";
import {
  cleanDashboard,
  type DashboardData,
  type FeedPayload,
} from "../src/dashboards";
import {
  hasAuthoredLauncherPreview,
  launcherSummary,
} from "../src/launcherPreviewModel";
import LauncherPreview, {
  previewDrawMs,
  previewRefreshMs,
} from "../src/LauncherPreview";
const board = {
  ...cleanDashboard(fixture)!,
  title: "Garden journal",
  feeds: [{ name: "garden", kind: "data" as const, star: "fixture:garden" }],
};
const feed: FeedPayload = {
  kind: "data",
  status: "ok",
  asOf: "2026-11-14T00:00:00Z",
  by: "day",
  star: { graphId: "fixture:garden", title: "Garden" },
  metrics: [{ metric: "temperature", label: "Temperature", unit: "°C" }],
  rows: [
    { metric: "temperature", unit: "°C", date: "2026-11-12", mean: 18 },
    { metric: "temperature", unit: "°C", date: "2026-11-13", mean: 21 },
    { metric: "humidity", unit: "%", date: "2026-11-14", mean: 70 },
  ],
};
const data: DashboardData = {
  dashboard: { id: board.id, version: board.version },
  asOf: feed.asOf,
  feeds: { garden: feed },
};
const summary = (f: FeedPayload) =>
  launcherSummary(
    { ...board, feeds: [{ name: "garden", kind: f.kind }] },
    { ...data, feeds: { garden: f } },
  );
afterEach(() => vi.useRealTimers());
it("automatically composes an old dashboard without preview metadata from real, unit-matched daily data", () => {
  expect(hasAuthoredLauncherPreview(board.html)).toBe(false);
  expect(launcherSummary(board, data)).toMatchObject({
    kind: "generated",
    value: "21 °C",
    label: "Temperature",
    detail: "Daily mean · 2026-11-13",
    points: [18, 21],
    partial: false,
  });
});
it("does not invent meanings for unsupported values, or read unavailable and undeclared feeds", () => {
  expect(summary({ ...feed, rows: undefined, balance: 1234 })).toMatchObject({
    kind: "fallback",
    value: "Open for details",
  });
  expect(
    summary({ ...feed, status: "unavailable", secret: "hidden" }),
  ).toMatchObject({
    kind: "fallback",
    label: "Sources unavailable",
    partial: true,
  });
  expect(
    launcherSummary(board, { ...data, feeds: { secret: feed } }).kind,
  ).toBe("fallback");
});
it("keeps truncation visible and avoids guessing a total, goal or completion percentage", () => {
  expect(
    summary({
      ...feed,
      kind: "log",
      log: "garden",
      entries: [{ note: "Plant basil", date: "2026-11-13" }],
      truncated: true,
    }),
  ).toMatchObject({
    value: "1+ entries",
    detail: "Plant basil",
    partial: true,
  });
  expect(
    summary({
      ...feed,
      kind: "links",
      stars: [{ title: "Seed plan" }],
      truncated: true,
    }),
  ).toMatchObject({ value: "1+", detail: "Seed plan", partial: true });
  expect(
    summary({
      ...feed,
      kind: "contents",
      text: "# Plant basil\nWater after sowing",
    }),
  ).toMatchObject({ detail: "Plant basil Water after sowing" });
});
it("ignores nonfinite and malformed measurements and orders actual readings", () => {
  expect(
    summary({
      ...feed,
      by: "reading",
      rows: [
        { metric: "walk", unit: "min", date: "2026-11-14", value: 12 },
        { metric: "walk", unit: "min", date: "2026-11-13", value: 8 },
        { metric: "walk", unit: "min", date: "2026-11-15", value: Infinity },
      ],
    }),
  ).toMatchObject({ value: "12 min", points: [8, 12] });
});
it("requires an explicit inert authored marker rather than detecting a script or compact mode", () => {
  expect(
    hasAuthoredLauncherPreview(
      '<script>var x="rhythm-launcher-preview"</script>',
    ),
  ).toBe(false);
  expect(
    hasAuthoredLauncherPreview(
      '<meta name="rhythm-launcher-preview" content="1">',
    ),
  ).toBe(true);
});
it("renders an automatic preview with current data and truthful provenance", async () => {
  const session = {
    dashboard: vi.fn(async () => board),
    dashboardData: vi.fn(async () => data),
  };
  render(<LauncherPreview id={board.id} session={session} />);
  expect(await screen.findByText("21 °C")).toBeVisible();
  expect(screen.getByText("From dashboard feeds")).toBeVisible();
  expect(screen.getByRole("img")).toHaveAccessibleName(/Last 2 values/);
  expect(session.dashboardData).toHaveBeenCalledWith(
    board.id,
    board.version,
    expect.any(AbortSignal),
  );
});
it("clears previous data on refresh failure and reads a newer version coherently", async () => {
  vi.useFakeTimers();
  const session = {
    dashboard: vi.fn(async () => board),
    dashboardData: vi.fn(async (): Promise<DashboardData | null> => data),
  };
  render(<LauncherPreview id={board.id} session={session} />);
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(screen.getByText("21 °C")).toBeVisible();
  session.dashboardData.mockResolvedValueOnce(null);
  await act(() => vi.advanceTimersByTimeAsync(previewRefreshMs));
  expect(screen.queryByText("21 °C")).toBeNull();
  expect(screen.getByRole("status")).toHaveTextContent("Preview unavailable");
  session.dashboard.mockResolvedValue({ ...board, version: 3 });
  session.dashboardData.mockResolvedValue({
    ...data,
    dashboard: { id: board.id, version: 3 },
  });
  await act(() => vi.advanceTimersByTimeAsync(previewRefreshMs));
  expect(screen.getByText("21 °C")).toBeVisible();
  expect(session.dashboardData).toHaveBeenLastCalledWith(
    board.id,
    3,
    expect.any(AbortSignal),
  );
});
it("aborts an interrupted preview and rejects mismatched data versions", async () => {
  let signal: AbortSignal | undefined;
  const session = {
    dashboard: vi.fn(async (_id, _version, s) => {
      signal = s;
      return board;
    }),
    dashboardData: vi.fn(async () => ({
      ...data,
      dashboard: { id: board.id, version: 99 },
    })),
  };
  const view = render(<LauncherPreview id={board.id} session={session} />);
  expect(await screen.findByRole("status")).toBeVisible();
  await waitFor(() =>
    expect(screen.getByRole("status")).toHaveTextContent("Preview unavailable"),
  );
  view.unmount();
  expect(signal).toBeDefined();
  const pending = {
    dashboard: vi.fn((_id, _version, s) => {
      signal = s;
      return new Promise<null>(() => {});
    }),
  };
  const next = render(<LauncherPreview id={board.id} session={pending} />);
  next.unmount();
  expect(signal?.aborted).toBe(true);
});
it("falls back to the generated summary if an authored preview never draws", async () => {
  vi.useFakeTimers();
  const session = {
    dashboard: async () => ({
      ...board,
      html: '<meta name="rhythm-launcher-preview" content="1"><div>silent</div>',
    }),
    dashboardData: async () => data,
  };
  render(<LauncherPreview id={board.id} session={session} />);
  await act(() => vi.advanceTimersByTimeAsync(1));
  const frame = screen.getByTitle("Garden journal launcher preview");
  expect(frame).toHaveAttribute("sandbox", "allow-scripts");
  expect(frame.getAttribute("srcdoc")).toContain('data-shape=\\"launcher\\"');
  await act(() => vi.advanceTimersByTimeAsync(previewDrawMs));
  expect(screen.queryByTitle("Garden journal launcher preview")).toBeNull();
  expect(screen.getByText("21 °C")).toBeVisible();
  expect(screen.getByText("Summary · authored preview failed")).toBeVisible();
});
it("keeps authored messages isolated: read-only launcher shape, no native open, and failure fallback", async () => {
  const ports: {
    onmessage: ((event: { data: unknown }) => void) | null;
    postMessage: ReturnType<typeof vi.fn>;
    close: ReturnType<typeof vi.fn>;
  }[] = [];
  vi.stubGlobal(
    "MessageChannel",
    class {
      port1 = { onmessage: null, postMessage: vi.fn(), close: vi.fn() };
      port2 = {};
      constructor() {
        ports.push(this.port1);
      }
    },
  );
  const session = {
    dashboard: async () => ({
      ...board,
      html: '<meta name="rhythm-launcher-preview" content="1"><script>rhythm.onData(()=>{})</script>',
    }),
    dashboardData: async () => data,
  };
  render(<LauncherPreview id={board.id} session={session} />);
  const frame = await screen.findByTitle("Garden journal launcher preview");
  if (!ports.length) fireEvent.load(frame);
  const port = ports.at(-1)!;
  expect(port.postMessage).toHaveBeenCalledWith(
    expect.objectContaining({
      payload: expect.objectContaining({
        mode: "card",
        shape: "launcher",
        edits: false,
      }),
    }),
  );
  await act(() => port.onmessage?.({ data: { type: "ready" } }));
  expect(frame).toHaveClass("drawn");
  await act(() => port.onmessage?.({ data: { type: "write", id: 4 } }));
  expect(port.postMessage).toHaveBeenLastCalledWith(
    expect.objectContaining({
      type: "written",
      answer: expect.objectContaining({ ok: false }),
    }),
  );
  await act(() => port.onmessage?.({ data: { type: "error" } }));
  expect(screen.getByText("21 °C")).toBeVisible();
  expect(port.close).toHaveBeenCalled();
});
