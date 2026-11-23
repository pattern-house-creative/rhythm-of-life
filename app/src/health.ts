import type {
  HealthProvenance,
  HealthState,
  MeasurementStatus,
  ObservatoryData,
  SourceStatus,
  Workout,
} from "./model.js";
import { glucoseSlot } from "../server/healthState.js";

// A source whose adapter is off: the engine does not offer its tools, so the facade leaves it
// "disconnected" (contracts/README.md 1.4). A connected source that fails is "unavailable".
export const notConnected = (
  data: Pick<ObservatoryData, "sources">,
  slot: string,
) => data.sources.find((s) => s.id === slot)?.status === "disconnected";
// How to turn on the adapter behind a source that is not connected (cli/README.md, connect).
export const connectHints: Record<string, { what: string; command: string }> = {
  [glucoseSlot]: {
    what: "a glucose source, such as Nightscout,",
    command: "./rhythm connect nightscout",
  },
  garmin: { what: "Garmin", command: "./rhythm connect garmin" },
  drive: { what: "Google Drive", command: "./rhythm connect drive" },
  "second-brain": {
    what: "Second Brain",
    command: "./rhythm connect second-brain",
  },
  calendar: {
    what: "your calendar (read only, through a private ICS link you enter yourself)",
    command: "./rhythm connect calendar",
  },
};
// A source's state in words: off (not connected), the demo's sample, or how
// its last read went.
const statusWords: Record<SourceStatus["status"], string> = {
  disconnected: "Not connected",
  demo: "Sample",
  available: "Available",
  stale: "Stale",
  unavailable: "Unavailable",
};
export const statusLabel = (s: Pick<SourceStatus, "status">) =>
  statusWords[s.status] ?? s.status;

export function stamp(iso: string | null | undefined) {
  return iso && Number.isFinite(Date.parse(iso))
    ? new Date(iso).toLocaleString(undefined, {
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
        timeZoneName: "short",
      })
    : "Observation time unavailable";
}

export function healthStatus(
  item: HealthState & { provenance: HealthProvenance },
  now = Date.now(),
): MeasurementStatus {
  if (item.status === "unavailable" || item.status === "missing")
    return item.status;
  const observed = Date.parse(item.provenance.observedAt ?? "");
  if (
    !Number.isFinite(observed) &&
    item.provenance.calendarDate &&
    item.provenance.timeZone
  ) {
    try {
      const today = new Intl.DateTimeFormat("en-CA", {
        timeZone: item.provenance.timeZone,
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
      }).format(new Date(now));
      return item.provenance.calendarDate > today
        ? "unavailable"
        : item.provenance.calendarDate < today || item.status === "stale"
          ? "stale"
          : "available";
    } catch {
      return "unavailable";
    }
  }
  if (!Number.isFinite(observed)) return "missing";
  if (!item.provenance.demo && observed > now + 60000) return "unavailable";
  if (item.status === "stale") return "stale";
  if (
    !item.provenance.demo &&
    item.staleAfterMinutes !== undefined &&
    now - observed > item.staleAfterMinutes * 60000
  )
    return "stale";
  return "available";
}
export const hasMeasurement = (
  item: HealthState & { provenance: HealthProvenance },
  now = Date.now(),
) => ["available", "stale"].includes(healthStatus(item, now));
export function healthCaption(
  item: HealthState & { provenance: HealthProvenance },
) {
  const status = healthStatus(item);
  return `${item.provenance.demo ? "DEMO · " : ""}${status === "available" ? (item.provenance.kind === "archive" ? "Archived observation" : "Recorded observation") : status === "stale" ? "Stale observation" : status === "missing" ? "No measurement" : "Source unavailable"} · ${observationLabel(item.provenance)}`;
}
export function observationLabel(value: HealthProvenance) {
  return value.calendarDate
    ? `${value.calendarDate} · ${value.timeZone ?? "source calendar"}${value.observedAt ? ` · ${stamp(value.observedAt)}` : " · exact time not supplied"}`
    : stamp(value.observedAt);
}
// Only statistics the device recorded for this activity; nothing is derived.
export function workoutStats(w: Workout) {
  const parts = [
    w.trainingLoad != null && `Load ${w.trainingLoad}`,
    w.aerobicEffect != null && `Aerobic ${w.aerobicEffect.toFixed(1)}`,
    w.anaerobicEffect != null && `Anaerobic ${w.anaerobicEffect.toFixed(1)}`,
    w.averageHeartRate != null && `Avg ${w.averageHeartRate} bpm`,
  ].filter(Boolean);
  return parts.length ? parts.join(" · ") : null;
}
export function shortTime(iso: string | undefined) {
  return iso && Number.isFinite(Date.parse(iso))
    ? new Date(iso).toLocaleTimeString([], {
        hour: "numeric",
        minute: "2-digit",
        timeZoneName: "short",
      })
    : "—";
}
// The demo's sources are fictional, apart from those it lists as not connected
// (with how to connect them); only the demo marks a source "demo".
export const isDemo = (data: ObservatoryData) =>
  data.sources.some((s) => s.status === "demo") &&
  data.sources.every(
    (s) => s.status === "demo" || s.status === "disconnected",
  ) &&
  [
    data.current,
    data.steps,
    data.recovery,
    data.glucose,
    ...data.workouts,
    ...(data.glucoseHistory ? [data.glucoseHistory] : []),
  ].every((item) => item.provenance.demo);

// Cosmetic state previews apply only to fictional observations. Never alter source
// timestamps, fill missing samples, or turn a live error into demo measurements.
export function previewHealth(
  data: ObservatoryData,
  state: string,
): ObservatoryData {
  if (!isDemo(data) || state === "ready") return data;
  const status =
    state === "stale" ? "stale" : state === "empty" ? "missing" : "unavailable";
  const hide = status !== "stale";
  return {
    ...data,
    current: {
      ...data.current,
      status,
      value: hide ? null : data.current.value,
    },
    steps: { ...data.steps, status, value: hide ? null : data.steps.value },
    glucose: {
      ...data.glucose,
      status,
      samples: hide ? [] : data.glucose.samples,
    },
    glucoseHistory: data.glucoseHistory
      ? {
          ...data.glucoseHistory,
          status,
          samples: hide ? [] : data.glucoseHistory.samples,
        }
      : null,
    recovery: {
      ...data.recovery,
      status,
      ...(hide
        ? { sleepMinutes: null, restingHeartRate: null, overnightHrvMs: null }
        : {}),
    },
    workouts: hide ? [] : data.workouts,
    workoutsStatus: status,
  };
}
