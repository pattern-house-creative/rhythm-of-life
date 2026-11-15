import { afterEach, expect, it, vi } from "vitest";
import {
  loadAdditionalHealth,
  mapAdditionalHealth,
} from "../server/additionalHealth";
import { assertObservatory } from "../src/validation";
import { summarizeGlucose } from "../src/glucose";
import { appToken, called, rpc, withEngine } from "./engine";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
// Synthetic only: a fictional device archive and live feed, never real readings.
const now = "2026-10-28T16:00:00.000Z";
const end = Date.parse(now),
  start = end - 14 * 86400000,
  liveStart = end - 3 * 86400000;
const iso = (ms: number) => new Date(ms).toISOString().replace("Z", "+00:00");
const summary = {
  startDate: "2026-10-14",
  endDate: "2026-10-27",
  sourceKind: "archive",
  timeBasis: "device-local-unverified",
  lastImportedAt: "2026-10-28T12:17:03Z",
  intervalMinutes: 5,
  readings: 1200,
  exactReadings: 1199,
  exactSum: 1199 * 150,
  bands: { veryLow: 1, low: 20, inRange: 900, high: 250, veryHigh: 29 },
};
function fortnight({ csv = true, live = true, legacy = false } = {}) {
  const series: number[][] = [];
  for (let t = start + 60000; t < end; t += 300000) {
    if ((t < liveStart && csv) || (t >= liveStart && live))
      series.push([t / 1000, 90 + (Math.round(t / 300000) % 60)]);
  }
  // One reading below and one above the sensor's range, in each stretch.
  const mark = (at: number, value: number, bound: number) => {
    const i = series.findIndex((r) => r[0] * 1000 >= at);
    if (i >= 0) series[i] = [series[i][0], value, bound];
  };
  if (csv) mark(start + 5 * 86400000, 40, -1);
  if (csv) mark(start + 6 * 86400000, 400, 1);
  if (live) mark(end - 3600000, 40, -1);
  const stretch = (kind: string, from: number, until: number) => {
    const inside = series.filter(
      (r) => r[0] * 1000 >= from && r[0] * 1000 < until,
    );
    return {
      kind,
      // Newer engines name each stretch's provider; the older ones did not.
      ...(legacy ? {} : { provider: kind === "live" ? "cgm-live" : "cgm-csv" }),
      start: iso(inside[0][0] * 1000),
      end: iso(inside.at(-1)![0] * 1000),
      readings: inside.length,
      timeBasis: kind.endsWith("live")
        ? "source-zone"
        : legacy
          ? "device-local-as-europe-london"
          : "device-local",
    };
  };
  const last = series.at(-1)![0] * 1000;
  return {
    subject: "alex",
    selection: "history",
    complete: true,
    ...(legacy ? {} : { demo: false, provider: live ? "cgm-live" : "cgm-csv" }),
    series,
    sources: [
      ...(csv
        ? [
            stretch(
              legacy ? "carelink-csv" : "archive",
              start,
              live ? liveStart : end,
            ),
          ]
        : []),
      ...(live
        ? [stretch(legacy ? "carelink-live" : "live", liveStart, end)]
        : []),
    ],
    latest: live
      ? {
          resourceId: "fictional-live-day",
          observedAt: iso(last),
          sourceUpdatedAt: iso(last + 30000),
        }
      : null,
    intervalMinutes: 5,
    today: "2026-10-28",
    start: new Date(start).toISOString(),
    end: now,
    lastSyncedAt: "2026-10-28T15:58:00Z",
    summary,
  };
}

it("maps 14 compact days into the history and keeps the chart to the live feed's last day", () => {
  const payload = fortnight();
  // The engine's form stays small: about 4,000 readings in well under 100 KB.
  expect(payload.series.length).toBe(4032);
  expect(JSON.stringify(payload).length).toBeLessThan(80_000);
  const data = mapAdditionalHealth(payload, "history", now);
  assertObservatory(data);
  const history = data.glucoseHistory!;
  expect(history).toMatchObject({
    start: payload.start,
    end: now,
    intervalMinutes: 5,
    timestampBasis: "verified",
    complete: true,
    sourceKind: "combined",
    status: "available",
  });
  expect(history.sources?.map((s) => [s.sourceKind, s.readings])).toEqual([
    ["archive", payload.sources[0].readings],
    ["live", payload.sources[1].readings],
  ]);
  expect(history.samples).toHaveLength(4032);
  expect(history.samples.filter((s) => s.comparison)).toEqual([
    expect.objectContaining({ value: 40, comparison: "<" }),
    expect.objectContaining({ value: 400, comparison: ">" }),
    expect.objectContaining({ value: 40, comparison: "<" }),
  ]);
  expect(history.sources?.map((s) => s.provider)).toEqual([
    "cgm-csv",
    "cgm-live",
  ]);
  expect(history.provenance).toMatchObject({
    sourceId: "cgm-live",
    recordId: "fictional-live-day",
    kind: "live",
    demo: false,
  });
  const summarized = summarizeGlucose(history);
  expect(summarized.days).toBe(14);
  expect(summarized.coverage).toBeGreaterThan(99);
  expect(summarized.bounded).toBe(3);
  // The 24-hour series: exact live readings only.
  const day = data.glucose.samples;
  expect(day).toHaveLength(287);
  expect(day.every((s) => Date.parse(s.timestamp) >= end - 86400000)).toBe(
    true,
  );
  expect(day.some((s) => s.value === 40)).toBe(false);
  // Hours follow the install's time zone: UTC without settings.
  expect(day[0].hour).toBe(16);
  expect(data.glucose.status).toBe("available");
  expect(data.glucoseSummary).toMatchObject({
    readings: 1200,
    status: "available",
  });
  // The glucose slot carries the provider's name, whichever provider it is.
  expect(data.sources.find((s) => s.id === "glucose")).toMatchObject({
    name: "cgm-live",
    connected: true,
    lastSyncedAt: "2026-10-28T15:58:00Z",
  });
});

it("labels a single-source history by its source and never shows archive readings as live", () => {
  const live = mapAdditionalHealth(fortnight({ csv: false }), "history", now);
  expect(live.glucoseHistory?.sourceKind).toBe("live");
  expect(live.glucoseHistory?.samples).toHaveLength(864);
  const archive = mapAdditionalHealth(
    fortnight({ live: false }),
    "history",
    now,
  );
  assertObservatory(archive);
  expect(archive.glucoseHistory?.sourceKind).toBe("archive");
  expect(archive.glucoseHistory?.provenance).toMatchObject({
    recordId: null,
    kind: "archive",
  });
  expect(archive.glucose.samples).toEqual([]);
  expect(archive.glucose.status).toBe("missing");
});

it("rejects a malformed compact history instead of guessing", () => {
  const bad = [
    { start: new Date(start + 86400000).toISOString() },
    { end: "2026-10-28T15:00:00.000Z" },
    { intervalMinutes: 15 },
    { subject: "jo" },
    { series: fortnight().series.slice().reverse() },
    { series: [...fortnight().series, [end / 1000 + 60, 120]] },
    {
      series: fortnight().series.map((r, i) => (i === 3 ? [r[0], 120, 2] : r)),
    },
    { series: fortnight().series.map((r, i) => (i === 3 ? [r[0], -5] : r)) },
    {
      series: Array.from({ length: 4201 }, (_, i) => [
        start / 1000 + i * 60,
        100,
      ]),
    },
    { sources: fortnight().sources.slice().reverse() },
    {
      sources: [
        { ...fortnight().sources[0], timeBasis: "source-zone" },
        fortnight().sources[1],
      ],
    },
    {
      sources: [
        { ...fortnight().sources[0], readings: 5 },
        fortnight().sources[1],
      ],
    },
    { latest: null },
  ];
  for (const change of bad)
    expect(() =>
      mapAdditionalHealth({ ...fortnight(), ...change }, "history", now),
    ).toThrow();
  // An invalid archive total is dropped on its own; the history stays.
  const data = mapAdditionalHealth(
    { ...fortnight(), summary: { ...summary, readings: -1 } },
    "history",
    now,
  );
  expect(data.glucoseSummary).toBeNull();
  expect(data.glucoseHistory?.samples).toHaveLength(4032);
});

it("reads an older engine's provider-named stretches the same way", () => {
  const data = mapAdditionalHealth(fortnight({ legacy: true }), "history", now);
  assertObservatory(data);
  expect(data.glucoseHistory?.sourceKind).toBe("combined");
  expect(data.glucoseHistory?.sources?.map((s) => s.timeBasis)).toEqual([
    "device-local",
    "source-zone",
  ]);
});

it("asks the engine for the 14-day form with the app token", async () => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(now);
  withEngine();
  vi.mocked(fetch).mockResolvedValueOnce(rpc(fortnight()));
  const data = await loadAdditionalHealth(
    "history",
    new Request("http://rhythm.example.invalid/api/observatory?source=history"),
  );
  vi.useRealTimers();
  expect(data.glucoseHistory?.samples).toHaveLength(4032);
  const [, init] = vi.mocked(fetch).mock.calls.at(-1)!;
  expect(init).toMatchObject({
    headers: expect.objectContaining({
      Authorization: `Bearer ${appToken}`,
    }),
  });
  expect(called(init)).toEqual({
    name: "app_recent_glucose",
    arguments: { days: 14 },
  });
});
