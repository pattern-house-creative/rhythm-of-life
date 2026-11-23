import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { dashboardCache } from "../src/dashboardCache";
import { createDemoDashboards } from "../src/demoDashboards";
import {
  feedObservationTimes,
  freshnessPhrases,
  phrasesText,
} from "../src/feedObservationTimes";
import type { DashboardData } from "../src/dashboards";
import { stamp } from "../src/health";
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
  expect(result).toBe("Source day 2026-01-04");
});

it("gives each time a subject: recorded, source day or unknown, then when the data was read", () => {
  const asOf = "2026-01-04T10:08:00.000Z";
  const read: DashboardData = {
    asOf,
    dashboard: { id: "fixture", version: 1 },
    feeds: {
      air: { kind: "data", status: "ok", asOf, rows: [{ at: "2026-01-04T10:06:00.000Z" }] },
      soil: { kind: "data", status: "ok", asOf, rows: [{ at: "2026-01-04T10:06:00.000Z" }] },
      rain: { kind: "data", status: "ok", asOf, rows: [{ date: "2026-01-03" }] },
      odd: { kind: "data", status: "ok", asOf, rows: [{ value: 1 }] },
      // Logs and documents carry no measurement time, and none is made up for them.
      notes: { kind: "log", status: "ok", asOf, entries: [] },
      plan: { kind: "contents", status: "ok", asOf, text: "Fictional" },
    },
  };
  // The reader names each feed with its time.
  expect(feedObservationTimes(read, true)).toBe(
    `air, soil recorded ${stamp("2026-01-04T10:06:00.000Z")} · rain · source day 2026-01-03 · odd · recording time unknown`,
  );
  // A card keeps one phrase: the newest of several times, said to be the latest.
  expect(feedObservationTimes(read)).toBe(
    `Latest recorded ${stamp("2026-01-04T10:06:00.000Z")}`,
  );
  const later = {
    ...read,
    feeds: { ...read.feeds, rain: { ...read.feeds.rain, rows: [{ date: "2026-01-05" }] } },
  } as DashboardData;
  expect(feedObservationTimes(later)).toBe("Latest source day 2026-01-05");
  // One time among all the feeds is said once, unnamed, unless there is room to name them.
  const one = { ...read, feeds: { air: read.feeds.air, notes: read.feeds.notes } };
  expect(feedObservationTimes(one)).toBe(`Recorded ${stamp("2026-01-04T10:06:00.000Z")}`);
  expect(feedObservationTimes(one, true)).toBe(`air recorded ${stamp("2026-01-04T10:06:00.000Z")}`);
  expect(phrasesText(freshnessPhrases(one))).toBe(
    `Recorded ${stamp("2026-01-04T10:06:00.000Z")}. Data read ${stamp(asOf)}`,
  );
  // After a failed refresh, the read kept from before is the one still shown.
  expect(phrasesText(freshnessPhrases(one, false, true))).toBe(
    `Recorded ${stamp("2026-01-04T10:06:00.000Z")}. Showing data read ${stamp(asOf)}`,
  );
  // A read with no measurement feeds says only when it was read.
  expect(phrasesText(freshnessPhrases({ ...read, feeds: { notes: read.feeds.notes } }))).toBe(
    `Data read ${stamp(asOf)}`,
  );
});
