import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import {
  mapAdditionalHealth,
  loadAdditionalHealth,
} from "../server/additionalHealth";
import { assertObservatory } from "../src/validation";
import { healthStatus, workoutStats } from "../src/health";
import { unavailableHealth } from "../server/healthState";
import { useHealthRefresh } from "../src/useHealthRefresh";
import { readRelationshipGraph } from "../server/relationshipGraph";
import { summarizeGlucose } from "../src/glucose";
import { closedEngines } from "./engine";

const now = "2026-10-26T21:00:00Z";
const base = {
  subject: "alex",
  selection: "garmin",
  complete: true,
  rows: [] as Record<string, unknown>[],
  today: "2026-10-26",
  startDate: "2026-10-13",
  endDate: "2026-10-27",
  coverageComplete: false,
  lastSyncedAt: "2026-10-26T20:30:00Z",
};
const row = (
  metric: string,
  value: number,
  unit: string,
  date = "2026-10-26",
) => ({
  resource_id: metric,
  subject_id: "alex",
  provider: "garmin",
  source_kind: "metric",
  metric,
  value,
  unit,
  local_date: date,
  observed_at: null,
  source_updated_at: null,
  timezone: "Europe/London",
  quality: "source-reported",
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});
it("keeps date-only, stale, missing recovery signals independent and preserves real zero steps", () => {
  const data = mapAdditionalHealth(
    {
      ...base,
      rows: [
        row("steps", 0, "count"),
        row("sleep_duration", 27000, "s"),
        row("resting_heart_rate", 61, "beats/min", "2026-10-25"),
      ],
    },
    "garmin",
    now,
  );
  assertObservatory(data);
  expect(data.steps.value).toBe(0);
  expect(data.steps.provenance.observedAt).toBeNull();
  expect(data.steps.provenance.calendarDate).toBe("2026-10-26");
  expect(data.recovery.metrics?.sleep.value).toBe(450);
  expect(data.recovery.metrics?.restingHeartRate.status).toBe("stale");
  expect(data.recovery.metrics?.overnightHrv.status).toBe("missing");
  expect(data.steps.provenance.lastSyncedAt).toBe(base.lastSyncedAt);
  expect(data.steps.provenance.retrievedAt).toBe(now);
  expect(healthStatus(data.steps, Date.parse("2026-10-27T21:00:00Z"))).toBe(
    "stale",
  );
  expect(data.workoutsCoverage?.complete).toBe(false);
});
it("keeps absent workout distance unknown and calendar dates separate from UTC", () => {
  const data = mapAdditionalHealth(
    {
      ...base,
      coverageComplete: true,
      rows: [
        {
          ...row("activity_duration", 1800, "s"),
          resource_id: "a",
          source_kind: "activity",
          observed_at: "2026-10-26T01:00:00Z",
          local_date: "2026-10-25",
        },
      ],
    },
    "garmin",
    now,
  );
  expect(data.workouts[0].distanceKm).toBeNull();
  expect(data.workouts[0].durationMinutes).toBe(30);
  expect(data.workouts[0].provenance.calendarDate).toBe("2026-10-25");
  expect(data.workouts[0].trainingLoad).toBeNull();
  expect(workoutStats(data.workouts[0])).toBeNull();
});
it("maps recorded training statistics per activity and daily intensity without deriving totals", () => {
  const activity = (
    id: string,
    metric: string,
    value: number,
    unit: string,
  ) => ({
    ...row(metric, value, unit),
    resource_id: id,
    source_kind: "activity",
    observed_at: "2026-10-26T13:00:00Z",
  });
  const data = mapAdditionalHealth(
    {
      ...base,
      coverageComplete: true,
      activityCount: 2,
      rows: [
        activity("run", "activity_duration", 2700, "s"),
        activity("run", "activity_training_load", 84.6, "score"),
        activity("run", "training_effect_aerobic", 3.14, "score"),
        activity("run", "training_effect_anaerobic", 0, "score"),
        activity("run", "activity_heart_rate", 141.7, "beats/min"),
        // Statistics without an "activity_" prefix still belong to their activity.
        activity("strength", "training_effect_aerobic", 1.2, "score"),
        row("intensity_moderate", 22, "min"),
        row("intensity_vigorous", 0, "min", "2026-10-25"),
      ],
    },
    "garmin",
    now,
  );
  assertObservatory(data);
  expect(data.workouts).toHaveLength(2);
  expect(data.workoutsCoverage?.complete).toBe(true);
  const run = data.workouts.find((w) => w.id === "run")!;
  expect(run).toMatchObject({
    durationMinutes: 45,
    distanceKm: null,
    trainingLoad: 85,
    aerobicEffect: 3.1,
    anaerobicEffect: 0,
    averageHeartRate: 142,
  });
  expect(workoutStats(run)).toBe(
    "Load 85 · Aerobic 3.1 · Anaerobic 0.0 · Avg 142 bpm",
  );
  expect(workoutStats(data.workouts.find((w) => w.id === "strength")!)).toBe(
    "Aerobic 1.2",
  );
  expect(data.intensityMinutes?.moderate).toMatchObject({
    value: 22,
    unit: "min",
    status: "available",
  });
  // Yesterday's recorded zero stays a zero and is labelled stale, not today's.
  expect(data.intensityMinutes?.vigorous).toMatchObject({
    value: 0,
    status: "stale",
  });
  expect(data.intensityMinutes?.vigorous.provenance.calendarDate).toBe(
    "2026-10-25",
  );
});
it("carries activity type, pool unit and multisport role, and rejects malformed descriptors", () => {
  const swim = {
    ...row("activity_distance", 914.4, "m"),
    resource_id: "swim",
    source_kind: "activity",
    observed_at: "2026-10-26T13:00:00Z",
    activity_type: "lap_swimming",
    pool_unit: "yard",
    multisport: "leg",
  };
  const data = mapAdditionalHealth({ ...base, rows: [swim] }, "garmin", now);
  assertObservatory(data);
  expect(data.workouts[0]).toMatchObject({
    activityType: "lap_swimming",
    poolUnit: "yard",
    multisport: "leg",
    distanceMeters: 914,
  });
  for (const change of [
    { activity_type: "Lap Swimming" },
    { activity_type: "x".repeat(41) },
    { pool_unit: "lane" },
    { multisport: "child" },
  ])
    expect(() =>
      mapAdditionalHealth(
        { ...base, rows: [{ ...swim, ...change }] },
        "garmin",
        now,
      ),
    ).toThrow();
});
it("maps 14-day CSV archive totals separately and drops an invalid summary without losing the history", () => {
  const reading = {
    ...row("sensor_glucose", 125, "mg/dL"),
    provider: "carelink",
    source_kind: "carelink-live-day",
    resource_id: "fictional-history",
    observed_at: "2026-10-26T20:55:00Z",
    quality: "source-reported;time_basis=explicit-offset",
  };
  // base.today is 2026-10-26; the archive covers through yesterday.
  const summary = {
    startDate: "2026-10-12",
    endDate: "2026-10-25",
    sourceKind: "archive",
    timeBasis: "device-local-unverified",
    lastImportedAt: "2026-10-26T12:17:03Z",
    intervalMinutes: 5,
    readings: 1200,
    exactReadings: 1199,
    exactSum: 1199 * 150,
    bands: { veryLow: 1, low: 20, inRange: 900, high: 250, veryHigh: 29 },
  };
  const payload = {
    ...base,
    selection: "history",
    start: "2026-10-25T21:00:00Z",
    end: now,
    rows: [reading],
  };
  const data = mapAdditionalHealth({ ...payload, summary }, "history", now);
  assertObservatory(data);
  expect(data.glucoseSummary).toMatchObject({
    readings: 1200,
    bands: summary.bands,
    startDate: "2026-10-12",
    endDate: "2026-10-25",
    status: "available",
  });
  expect(data.glucoseSummary?.provenance.kind).toBe("archive");
  // An archive that stopped updating is labelled stale, not passed off as current.
  expect(
    mapAdditionalHealth(
      {
        ...payload,
        summary: { ...summary, startDate: "2026-10-06", endDate: "2026-10-19" },
      },
      "history",
      now,
    ).glucoseSummary?.status,
  ).toBe("stale");
  for (const broken of [
    { ...summary, bands: { ...summary.bands, inRange: 899 } },
    { ...summary, startDate: "2026-10-19" },
    { ...summary, endDate: "2026-10-26", startDate: "2026-10-13" },
    { ...summary, sourceKind: "live" },
    { ...summary, timeBasis: "verified" },
    { ...summary, exactReadings: 1201 },
    { ...summary, readings: -1 },
  ]) {
    const result = mapAdditionalHealth(
      { ...payload, summary: broken },
      "history",
      now,
    );
    expect(result.glucoseSummary).toBeNull();
    expect(result.glucoseHistory?.samples).toHaveLength(1);
  }
  // Older readers send no summary at all.
  expect(
    mapAdditionalHealth(payload, "history", now).glucoseSummary,
  ).toBeNull();
});
it("rejects another subject, extra medical metrics, unverified timezone and invalid units", () => {
  for (const change of [
    { subject_id: "jo" },
    { metric: "insulin" },
    { metric: "body_battery", unit: "score" },
    { metric: "activity_calories", unit: "kcal" },
    { unit: "unknown" },
    { timezone: null },
    { observed_at: "2026-10-26T12:00:00" },
  ])
    expect(() =>
      mapAdditionalHealth(
        { ...base, rows: [{ ...row("steps", 1, "count"), ...change }] },
        "garmin",
        now,
      ),
    ).toThrow();
});
it("accepts only recent verified live glucose and refuses conflicting observations or historical estimates", () => {
  const sample = {
    ...row("sensor_glucose", 123, "mg/dL"),
    provider: "carelink",
    source_kind: "carelink-live-day",
    observed_at: "2026-10-26T20:55:00Z",
    quality: "source-reported;time_basis=explicit-offset;original_unit=mg/dL",
  };
  const payload = {
    ...base,
    selection: "history",
    start: "2026-10-25T21:00:00Z",
    end: now,
    complete: false,
    rows: [sample],
  };
  const data = mapAdditionalHealth(payload, "history", now);
  assertObservatory(data);
  expect(data.glucose.samples).toHaveLength(1);
  expect(data.glucose.provenance.recordId).toBe(sample.resource_id);
  expect(
    mapAdditionalHealth(
      {
        ...payload,
        rows: [
          {
            ...sample,
            quality:
              "source-reported;time_basis=source-zone;original_unit=mg/dL",
          },
        ],
      },
      "history",
      now,
    ).glucose.samples,
  ).toHaveLength(1);
  expect(() =>
    mapAdditionalHealth(
      { ...payload, rows: [{ ...sample, quality: "source_timezone=UTC" }] },
      "history",
      now,
    ),
  ).toThrow("Invalid glucose history row");
  expect(data.current.value).toBeNull();
  expect(data.glucoseHistory?.complete).toBe(false);
  expect(summarizeGlucose(data.glucoseHistory!).gmi).toBeNull();
  expect(() =>
    mapAdditionalHealth(
      { ...payload, rows: [sample, { ...sample, value: 999 }] },
      "history",
      now,
    ),
  ).toThrow();
  expect(() =>
    mapAdditionalHealth(
      { ...payload, rows: [{ ...sample, source_kind: "carelink-csv" }] },
      "history",
      now,
    ),
  ).toThrow();
  expect(
    mapAdditionalHealth({ ...payload, rows: [] }, "history", now).glucose
      .status,
  ).toBe("missing");
});
it("does not call the engine without one, or after its credential expires", async () => {
  vi.mocked(fetch).mockClear();
  const result = await loadAdditionalHealth(
    "garmin",
    new Request("http://rhythm.example.invalid/api/observatory?source=garmin"),
  );
  expect(result.steps.value).toBeNull();
  expect(fetch).not.toHaveBeenCalled();
  for (const [, close] of closedEngines) {
    close();
    await loadAdditionalHealth(
      "garmin",
      new Request("http://rhythm.example.invalid/api/observatory"),
    );
    expect(fetch).not.toHaveBeenCalled();
  }
});
it("keeps the real graph empty when unavailable, regardless of demo parameters", async () => {
  const result = await readRelationshipGraph(
    new Request("http://rhythm.example.invalid/api/graph"),
    { operation: "refresh", demo: { generation: 5 } },
  );
  expect(result).toMatchObject({
    nodes: [],
    edges: [],
    completeness: "partial",
  });
});
it("refreshes sources independently and clears revoked, failed and hidden state without background polling", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(now);
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  let failGarmin = false;
  const latest = { ...unavailableHealth(now), healthMode: "live" };
  latest.current.value = 123;
  const garmin = mapAdditionalHealth(
    { ...base, rows: [row("steps", 456, "count")] },
    "garmin",
    now,
  );
  vi.mocked(fetch).mockImplementation(async (path) => {
    if (String(path).includes("garmin") && failGarmin)
      throw new Error("outage");
    return new Response(
      JSON.stringify(String(path).includes("garmin") ? garmin : latest),
      { headers: { "content-type": "application/json" } },
    );
  });
  const { result } = renderHook(useHealthRefresh);
  await act(async () => {});
  expect(fetch).toHaveBeenCalledTimes(3);
  expect(result.current.data?.steps.value).toBe(456);
  expect(result.current.data?.intensityMinutes?.moderate.status).toBe(
    "missing",
  );
  await act(async () => vi.advanceTimersByTimeAsync(60_000));
  expect(fetch).toHaveBeenCalledTimes(4);
  await act(async () => vi.advanceTimersByTimeAsync(240_000));
  expect(
    vi.mocked(fetch).mock.calls.filter(([p]) => String(p).includes("garmin")),
  ).toHaveLength(1);
  expect(
    vi.mocked(fetch).mock.calls.filter(([p]) => String(p).includes("history")),
  ).toHaveLength(2);
  failGarmin = true;
  await act(async () => result.current.refresh());
  expect(result.current.data?.steps.value).toBeNull();
  // A failed Garmin read clears its training statistics rather than keeping them.
  expect(result.current.data?.intensityMinutes).toBeUndefined();
  expect(result.current.data?.current.value).toBe(123);
  hidden.mockReturnValue(true);
  act(() => document.dispatchEvent(new Event("visibilitychange")));
  expect(result.current.data).toBeNull();
  const calls = vi.mocked(fetch).mock.calls.length;
  await act(async () => vi.advanceTimersByTimeAsync(900_000));
  expect(fetch).toHaveBeenCalledTimes(calls);
  hidden.mockReturnValue(false);
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
    window.dispatchEvent(new Event("focus"));
    window.dispatchEvent(new Event("pageshow"));
  });
  expect(fetch).toHaveBeenCalledTimes(calls + 3);
  vi.mocked(fetch).mockResolvedValue(new Response("", { status: 401 }));
  await act(async () => result.current.refresh());
  expect(result.current.data).toBeNull();
});

it("takes history provenance only from the accepted observation, not an excluded bound at the same instant", () => {
  const exact = {
    ...row("sensor_glucose", 125, "mg/dL"),
    provider: "carelink",
    source_kind: "carelink-live-day",
    resource_id: "fictional-exact-source",
    observed_at: "2026-10-26T20:55:00Z",
    source_updated_at: "2026-10-26T20:56:00Z",
    quality: "source-reported;time_basis=explicit-offset;original_unit=mg/dL",
  };
  const bounded = {
    ...exact,
    resource_id: "fictional-excluded-bound",
    observed_at: "2026-10-26T20:55:00+00:00",
    source_updated_at: "2026-10-26T20:57:00Z",
    value: 400,
    quality: exact.quality + ";comparison=>",
  };
  const payload = {
    ...base,
    selection: "history",
    start: "2026-10-25T21:00:00Z",
    end: now,
  };
  for (const rows of [
    [bounded, exact],
    [exact, bounded],
  ]) {
    const data = mapAdditionalHealth({ ...payload, rows }, "history", now);
    assertObservatory(data);
    expect(data.glucoseHistory?.samples).toEqual([
      {
        timestamp: new Date(exact.observed_at).toISOString(),
        value: exact.value,
      },
    ]);
    expect(data.glucose.provenance).toMatchObject({
      recordId: exact.resource_id,
      sourceUpdatedAt: exact.source_updated_at,
      observedAt: new Date(exact.observed_at).toISOString(),
      retrievedAt: now,
      lastSyncedAt: base.lastSyncedAt,
    });
    expect(data.glucoseHistory?.provenance).toEqual(data.glucose.provenance);
  }
  const excluded = mapAdditionalHealth(
    { ...payload, rows: [bounded] },
    "history",
    now,
  );
  expect(excluded.glucose.status).toBe("missing");
  expect(excluded.glucose.samples).toEqual([]);
  expect(excluded.glucose.provenance.recordId).toBeNull();
  expect(excluded.glucose.provenance.observedAt).toBeNull();
});
