import type { HealthMetric } from "../src/model.js";
import { healthStatus } from "../src/health.js";
import { providerId } from "../src/validation.js";
import { unavailableHealth } from "./healthState.js";

// A live reading's source kind: any source's live feed, never an archive, export or CSV import.
export const liveSourceKind = (v: unknown): v is string =>
  typeof v === "string" &&
  /^[a-z][a-z0-9-]{0,60}$/.test(v) &&
  !/(?:^|-)(?:csv|archive|export)(?:-|$)/.test(v);

// Pure mapping of the latest-glucose rows the engine returns (app_latest_glucose). The caller
// supplies the subject the engine answered for; it must be a person in the settings. A row
// names its own provider (the glucose source's live feed: a plugin or an adapter), which
// becomes the reading's provenance. Archive rows are never shown as the current reading.
export function mapLiveGlucose(
  rows: unknown,
  subject: string,
  now = new Date().toISOString(),
): HealthMetric {
  const empty = unavailableHealth(now).current;
  if (!subject || !Array.isArray(rows) || rows.length > 500) return empty;
  if (!rows.length) return { ...empty, status: "missing" };
  for (const r of rows) {
    if (
      !r ||
      r.subject_id !== subject ||
      !providerId(r.provider) ||
      // "glucose" is the ingest contract's metric; "sensor_glucose" an older name for it.
      (r.metric !== "glucose" && r.metric !== "sensor_glucose") ||
      !liveSourceKind(r.source_kind) ||
      r.unit !== "mg/dL" ||
      typeof r.value !== "number" ||
      !Number.isFinite(r.value) ||
      r.value <= 0 ||
      typeof r.resource_id !== "string" ||
      !r.resource_id ||
      typeof r.sample_key !== "string" ||
      !r.sample_key ||
      typeof r.quality !== "string" ||
      typeof r.observed_at !== "string" ||
      !/(Z|[+-]\d{2}:\d{2})$/.test(r.observed_at) ||
      !Number.isFinite(Date.parse(r.observed_at)) ||
      !Number.isInteger(r.total_samples) ||
      r.total_samples !== rows.length
    )
      return empty;
  }
  // A bounded ascending page cannot establish the latest reading. Refuse a
  // truncated response rather than displaying its last row as "current".
  const latest = [...rows].sort(
    (a, b) => Date.parse(b.observed_at) - Date.parse(a.observed_at),
  )[0];
  if (
    rows.some(
      (r) =>
        Date.parse(r.observed_at) === Date.parse(latest.observed_at) &&
        (r.value !== latest.value || r.quality !== latest.quality),
    )
  )
    return empty;
  // Bounds are not exact measurements; the latest reading is shown only when exact.
  if (/comparison=[<>]/.test(latest.quality))
    return {
      ...empty,
      status: "missing",
      reason: "Latest observation is a bound, not an exact reading",
    };
  const metric: HealthMetric = {
    ...empty,
    value: latest.value,
    status: "available",
    provenance: {
      ...empty.provenance,
      sourceId: latest.provider,
      recordId: `${latest.resource_id}:${latest.sample_key}`,
      observedAt: latest.observed_at,
      // The engine's fictional seed stays labelled DEMO.
      demo: latest.demo === true,
    },
  };
  metric.status = healthStatus(metric, Date.parse(now));
  return metric.status === "unavailable" ? empty : metric;
}
