import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { dashboardCache } from "../src/dashboardCache";
import { createDemoDashboards } from "../src/demoDashboards";
import { feedObservationTimes } from "../src/feedObservationTimes";
import { useDashboardVersion } from "../src/useDashboardVersion";

afterEach(() => dashboardCache.clear());

it("keeps the last good feed time through a failed read and clears the warning on recovery", async () => {
  const boards = createDemoDashboards();
  const { id, version } = boards.list()[0];
  const sample = boards.data(id, version)!;
  const session = {
    dashboard: async () => boards.get(id, version),
    dashboardData: vi.fn(async () => sample),
  };
  const { result } = renderHook(() => useDashboardVersion(id, session));
  await waitFor(() =>
    expect(result.current.current?.at).toBe(Date.parse(sample.asOf)),
  );
  session.dashboardData.mockRejectedValueOnce(new Error("offline"));
  await act(() => result.current.read());
  expect(result.current.current).toMatchObject({
    read: sample,
    at: Date.parse(sample.asOf),
    refreshFailed: true,
  });
  await act(() => result.current.read());
  expect(result.current.current?.refreshFailed).toBe(false);
});

it("shows the source day of a daily aggregate independently of its feed read time", () => {
  const boards = createDemoDashboards();
  const { id, version } = boards.list()[0];
  const read = boards.data(id, version)!;
  const result = feedObservationTimes({
    ...read,
    feeds: {
      steps: {
        kind: "data",
        status: "ok",
        asOf: read.asOf,
        rows: [{ date: "2026-01-03" }, { date: "2026-01-04" }],
      },
    },
  });
  expect(result).toBe("Latest observation 2026-01-04");
});
