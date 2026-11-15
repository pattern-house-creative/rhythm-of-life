import type { GlucoseHistory, ObservatoryData, Provenance } from "../src/model";
import type { SourceId } from "../src/model";
import { demoData } from "../src/demo";
import { glucoseSlot } from "../server/healthState";
import { sourceName } from "../src/graphView";
import { handleObservatory } from "../server/routes";
import { handleGraph } from "../server/graph";
import { vi } from "vitest";

// A fictional glucose source and a fictional watch, both labelled demo: the
// demo itself connects neither (it is everyday wellbeing), so the glucose and
// Garmin tiles and panels are tested against this sample instead.
const date = "2026-10-18";
const provenance = (
  sourceId: SourceId,
  recordId: string,
  hour = "20:00",
  day = date,
): Provenance => ({
  sourceId,
  recordId: `demo:${recordId}`,
  observedAt: `${day}T${hour}:00Z`,
  demo: true,
});
const values = [
  98, 95, 92, 90, 86, 88, 92, 98, 114, 142, 159, 138, 115, 107, 103, 97, 109,
  133, 171, 188, 174, 150, 126, 112,
];
// Deterministic fictional CGM history. Never populated from a personal source.
const historyStart = Date.parse("2026-10-04T23:05:00Z");
const historyEnd = Date.parse("2026-10-18T23:05:00Z");
const glucoseHistory: GlucoseHistory = {
  start: new Date(historyStart).toISOString(),
  end: new Date(historyEnd).toISOString(),
  intervalMinutes: 5,
  unit: "mg/dL",
  timestampBasis: "verified",
  complete: true,
  sourceKind: "demo",
  provenance: provenance("demo", "glucose-history", "23:00"),
  samples: Array.from(
    { length: (historyEnd - historyStart) / 300000 },
    (_, i) => {
      const instant = historyStart + i * 300000;
      const day = Math.floor((instant - historyStart) / 86400000);
      // The demo's day is a UTC day.
      const local = new Date(instant);
      const hour = local.getUTCHours(),
        fraction = local.getUTCMinutes() / 60;
      // Known sensor gaps are intentionally left missing, including in coverage.
      if (day < 13 && hour === 13) return null;
      const baseline =
        values[hour] * (1 - fraction) + values[(hour + 1) % 24] * fraction;
      const variation = day === 13 ? 0 : 27 + 24 * Math.sin(day * 1.37);
      const lowPeriod = day === 3 && hour >= 2 && hour < 4 ? 65 : 0;
      return {
        timestamp: new Date(instant).toISOString(),
        value: Math.round(baseline + variation - lowPeriod),
      };
    },
  ).filter(
    (sample): sample is { timestamp: string; value: number } => sample !== null,
  ),
};
// What a connected glucose source and Garmin hand the home screen and their
// panels: the fictional readings the demo once had, for these sources' tests.
export const sampleHealth: ObservatoryData = {
  ...demoData,
  workoutsStatus: "available",
  glucoseHistory,
  glucose: {
    id: "daily-glucose",
    unit: "mg/dL",
    provenance: provenance("demo", "glucose", "23:00"),
    samples: values.map((value, hour) => ({
      hour,
      value,
      timestamp: `${date}T${String(hour).padStart(2, "0")}:00:00Z`,
    })),
  },
  current: {
    id: "glucose",
    label: "Glucose",
    value: 112,
    unit: "mg/dL",
    provenance: provenance("demo", "glucose", "23:00"),
  },
  recovery: {
    sleepMinutes: 462,
    restingHeartRate: 54,
    overnightHrvMs: 48,
    provenance: provenance("garmin", "recovery", "08:00"),
  },
  steps: {
    id: "steps",
    label: "Steps",
    value: 10482,
    unit: "steps",
    provenance: provenance("garmin", "steps", "23:00"),
  },
  workouts: [
    // A fictional swim, ride and run, with sample device statistics.
    {
      id: "run",
      title: "Sunday long run",
      activityType: "running",
      distanceKm: 8.4,
      durationMinutes: 52,
      trainingLoad: 142,
      aerobicEffect: 3.4,
      anaerobicEffect: 0.8,
      averageHeartRate: 146,
      startedAt: `${date}T09:00:00Z`,
      provenance: provenance("garmin", "run", "10:00"),
    },
    {
      id: "ride",
      title: "Easy afternoon ride",
      activityType: "road_biking",
      distanceKm: 18.2,
      durationMinutes: 48,
      trainingLoad: 74,
      aerobicEffect: 2.6,
      anaerobicEffect: 0.3,
      averageHeartRate: 128,
      startedAt: "2026-10-16T16:00:00Z",
      provenance: provenance("garmin", "ride", "17:00", "2026-10-16"),
    },
    {
      id: "swim",
      title: "Midweek pool swim",
      activityType: "lap_swimming",
      poolUnit: "yard",
      distanceKm: 1.83,
      distanceMeters: 1829,
      durationMinutes: 34,
      trainingLoad: 58,
      aerobicEffect: 2.4,
      anaerobicEffect: 1.1,
      averageHeartRate: 132,
      startedAt: "2026-10-14T07:00:00Z",
      provenance: provenance("garmin", "swim", "08:00", "2026-10-14"),
    },
  ],
  sources: [
    ...demoData.sources.filter(
      (s) => s.id !== glucoseSlot && s.id !== "garmin",
    ),
    ...(["demo", "garmin"] as SourceId[]).map((id, i) => ({
      // The fictional CGM fills the glucose slot, named after its provider as a real one is.
      id: id === "demo" ? glucoseSlot : id,
      name: sourceName(id),
      connected: false,
      sampleAt: provenance(id, id, i <= 1 ? "23:00" : "20:00").observedAt,
      status: "demo" as const,
    })),
  ],
};

// The app's routes, answering the home screen's health reads with the sample.
export function withSampleHealth() {
  vi.mocked(fetch).mockImplementation(async (url, init) => {
    const path = String(url);
    const request = new Request(new URL(path, "http://localhost"), init);
    if (path === "/api/observatory")
      return new Response(JSON.stringify(sampleHealth), {
        headers: { "Content-Type": "application/json" },
      });
    if (path.startsWith("/api/observatory")) return handleObservatory(request);
    if (path === "/api/graph") return handleGraph(request);
    throw new Error("Unexpected request");
  });
}
