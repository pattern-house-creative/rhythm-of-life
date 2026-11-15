import type { HealthProvenance, ObservatoryData } from "../src/model.js";

// The glucose source's place in the source list, whichever provider feeds it: a live feed, an
// archive or both. It is named after that provider once a reading names it (open question Q-021).
export const glucoseSlot = "glucose";

// A real-source outage is a typed empty response, never a demo fallback.
export function unavailableHealth(
  now = new Date().toISOString(),
): ObservatoryData {
  const provenance = (sourceId: string): HealthProvenance => ({
    sourceId,
    recordId: null,
    observedAt: null,
    retrievedAt: now,
    demo: false,
    kind: "live",
  });
  const glucose = provenance(glucoseSlot),
    activity = provenance("garmin");
  return {
    retrievedAt: now,
    current: {
      id: "sensor_glucose",
      label: "Glucose",
      unit: "mg/dL",
      value: null,
      status: "unavailable",
      staleAfterMinutes: 20,
      provenance: glucose,
    },
    glucose: {
      id: "sensor_glucose",
      unit: "mg/dL",
      samples: [],
      status: "unavailable",
      staleAfterMinutes: 20,
      provenance: glucose,
    },
    glucoseHistory: null,
    steps: {
      id: "steps",
      label: "Steps",
      unit: "steps",
      value: null,
      status: "unavailable",
      provenance: activity,
    },
    recovery: {
      sleepMinutes: null,
      restingHeartRate: null,
      overnightHrvMs: null,
      status: "unavailable",
      provenance: activity,
    },
    workouts: [],
    workoutsStatus: "unavailable",
    nodes: [],
    edges: [],
    insight: { title: "", body: "", nodeId: "" },
    sources: [
      {
        id: glucoseSlot,
        name: "Glucose",
        connected: false,
        sampleAt: null,
        status: "disconnected",
      },
      {
        id: "garmin",
        name: "Garmin",
        connected: false,
        sampleAt: null,
        status: "disconnected",
      },
      {
        id: "drive",
        name: "Google Drive",
        connected: false,
        sampleAt: null,
        status: "disconnected",
      },
      {
        id: "second-brain",
        name: "Second Brain",
        connected: false,
        sampleAt: null,
        status: "disconnected",
      },
      // Off and connected to nothing until its person enters a private ICS link
      // (docs/adapters/calendar.md); connected once its events are on the map.
      {
        id: "calendar",
        name: "Calendar",
        connected: false,
        sampleAt: null,
        status: "disconnected",
      },
      {
        id: "rhythm",
        name: "Rhythm engine",
        connected: false,
        sampleAt: null,
        status: "disconnected",
      },
    ],
  };
}
