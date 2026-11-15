import type {
  GlucoseAggregate,
  GlucoseStretch,
  HealthMetric,
  HealthProvenance,
  ObservatoryData,
} from "../src/model.js";
import { healthStatus } from "../src/health.js";
import { object, providerId } from "../src/validation.js";
import { glucoseSlot, unavailableHealth } from "./healthState.js";
import { sourceName } from "../src/graphView.js";
import { answerRejected, callTool, EngineError } from "./engine.js";
import { settings, validTimeZone } from "./config.js";
import { failedSource, knownSubject } from "./health.js";
import { liveSourceKind } from "./glucose.js";

const iso = (v: unknown): v is string =>
  typeof v === "string" &&
  /(?:Z|[+-]\d\d:\d\d)$/.test(v) &&
  Number.isFinite(Date.parse(v));
const day = (v: unknown): v is string =>
  typeof v === "string" &&
  /^\d{4}-\d{2}-\d{2}$/.test(v) &&
  Number.isFinite(Date.parse(v));
const definitions = {
  steps: ["count", 1],
  resting_heart_rate: ["beats/min", 1],
  sleep_duration: ["s", 60],
  hrv_overnight: ["ms", 1],
  intensity_moderate: ["min", 1],
  intensity_vigorous: ["min", 1],
  activity_duration: ["s", 60],
  activity_distance: ["m", 1000],
  activity_training_load: ["score", 1],
  training_effect_aerobic: ["score", 1],
  training_effect_anaerobic: ["score", 1],
  activity_heart_rate: ["beats/min", 1],
} as const;
export type HealthPart = "garmin" | "history";

// 14-day archive totals from the glucose source (newer engines only). An invalid summary is
// dropped on its own; it never takes the glucose history down with it.
function archiveSummary(
  totals: unknown,
  today: string,
  prov: HealthProvenance,
): GlucoseAggregate | null {
  const count = (v: unknown) => Number.isInteger(v) && (v as number) >= 0;
  const bandKeys = ["veryLow", "low", "inRange", "high", "veryHigh"] as const;
  const dayDiff = (a: string, b: string) =>
    (Date.parse(`${a}T00:00:00Z`) - Date.parse(`${b}T00:00:00Z`)) / 86400000;
  if (
    object(totals) &&
    day(totals.startDate) &&
    day(totals.endDate) &&
    dayDiff(totals.endDate, totals.startDate) === 13 &&
    dayDiff(today, totals.endDate) >= 1 &&
    dayDiff(today, totals.endDate) <= 60 &&
    archiveKind(totals.sourceKind) &&
    totals.timeBasis === "device-local-unverified" &&
    (totals.lastImportedAt === null || iso(totals.lastImportedAt)) &&
    totals.intervalMinutes === 5 &&
    count(totals.readings) &&
    count(totals.exactReadings) &&
    (totals.exactReadings as number) <= (totals.readings as number) &&
    typeof totals.exactSum === "number" &&
    Number.isFinite(totals.exactSum) &&
    totals.exactSum >= 0 &&
    object(totals.bands) &&
    bandKeys.every((k) =>
      count((totals.bands as Record<string, unknown>)[k]),
    ) &&
    bandKeys.reduce(
      (sum, k) => sum + Number((totals.bands as Record<string, unknown>)[k]),
      0,
    ) === totals.readings
  ) {
    const bands = totals.bands as Record<(typeof bandKeys)[number], number>;
    // A daily archive re-sync covers through yesterday; older archives are stale.
    const archiveStatus =
      (totals.readings as number) === 0
        ? "missing"
        : dayDiff(today, totals.endDate) > 3
          ? "stale"
          : "available";
    return {
      startDate: totals.startDate,
      endDate: totals.endDate,
      sourceKind: "archive",
      timeBasis: "device-local-unverified",
      lastImportedAt: totals.lastImportedAt as string | null,
      intervalMinutes: 5,
      readings: totals.readings as number,
      exactReadings: totals.exactReadings as number,
      exactSum: totals.exactSum,
      bands: {
        veryLow: bands.veryLow,
        low: bands.low,
        inRange: bands.inRange,
        high: bands.high,
        veryHigh: bands.veryHigh,
      },
      // Status is explicit: archive days are not observation instants to age.
      provenance: {
        ...prov,
        recordId: null,
        observedAt: null,
        sourceUpdatedAt: totals.lastImportedAt as string | null,
        kind: "archive",
      },
      status: archiveStatus,
    };
  }
  return null;
}

const fortnightMs = 14 * 86400000,
  fortnightReadings = 4200;
// A stretch's kind: the source's archive or its live feed. The older engine names
// ("<provider>-csv" and "<provider>-live") are read the same way.
const archiveKind = (v: unknown) =>
  v === "archive" || (typeof v === "string" && /^[a-z][a-z0-9-]*-csv$/.test(v));
const liveKind = (v: unknown) =>
  v === "live" || (typeof v === "string" && /^[a-z][a-z0-9-]*-live$/.test(v));
const stretchKind = (v: unknown): GlucoseStretch["sourceKind"] | null =>
  archiveKind(v) ? "archive" : liveKind(v) ? "live" : null;
const timeBases = { archive: "device-local", live: "source-zone" } as const;
// Archive times are the device's clock; "device-local-as-<zone>" was an older engine's name.
const timeBasisValid = (kind: GlucoseStretch["sourceKind"], v: unknown) =>
  kind === "live"
    ? v === "source-zone"
    : v === "device-local" ||
      (typeof v === "string" && /^device-local-as-[a-z0-9_+-]+$/.test(v));
// The hour of the day in the install's time zone.
const localHour = (timestamp: string) =>
  Number(
    new Intl.DateTimeFormat("en-US", {
      timeZone: settings().timezone,
      hour: "numeric",
      hourCycle: "h23",
    }).format(new Date(timestamp)),
  );
// The engine's compact 14-day glucose form: [epochSeconds, value] readings, a third
// element -1 or 1 for one below or above the sensor's range, one source per stretch.
// The history gets all 14 days; the 24-hour series stays the live feed's last day.
function mapFortnight(
  payload: Record<string, unknown>,
  now: string,
): ObservatoryData {
  const result = unavailableHealth(now);
  result.healthMode = "live";
  const { start, end, series, sources, latest } = payload;
  // The engine's fictional seed belongs to its own synthetic subject, accepted only when labelled demo.
  const demo = payload.demo === true;
  if (
    !(
      knownSubject(payload.subject) ||
      (demo && typeof payload.subject === "string")
    ) ||
    (payload.demo !== undefined && typeof payload.demo !== "boolean") ||
    (payload.provider != null && !providerId(payload.provider)) ||
    payload.selection !== "history" ||
    typeof payload.complete !== "boolean" ||
    payload.intervalMinutes !== 5 ||
    !day(payload.today) ||
    !iso(start) ||
    !iso(end) ||
    Date.parse(end) - Date.parse(start) !== fortnightMs ||
    Math.abs(Date.parse(end) - Date.parse(now)) > 60000 ||
    (payload.lastSyncedAt !== null && !iso(payload.lastSyncedAt)) ||
    !Array.isArray(series) ||
    series.length > fortnightReadings ||
    !Array.isArray(sources) ||
    sources.length > 2 ||
    (latest !== null &&
      !(
        object(latest) &&
        typeof latest.resourceId === "string" &&
        iso(latest.observedAt) &&
        (latest.sourceUpdatedAt === null || iso(latest.sourceUpdatedAt))
      ))
  )
    throw new Error("Invalid glucose history contract");
  const stretches = sources.map((s): GlucoseStretch => {
    const kind = object(s) ? stretchKind(s.kind) : null;
    if (
      !object(s) ||
      !kind ||
      !timeBasisValid(kind, s.timeBasis) ||
      !iso(s.start) ||
      !iso(s.end) ||
      Date.parse(s.end) < Date.parse(s.start) ||
      !Number.isInteger(s.readings) ||
      (s.readings as number) < 1 ||
      (s.provider !== undefined && !providerId(s.provider))
    )
      throw new Error("Invalid glucose history source");
    return {
      sourceKind: kind,
      start: s.start,
      end: s.end,
      readings: s.readings as number,
      timeBasis: timeBases[kind],
      ...(providerId(s.provider) ? { provider: s.provider } : {}),
    };
  });
  const live = stretches.find((s) => s.sourceKind === "live");
  // The archive comes first and ends before the live feed starts; never interleaved.
  if (
    new Set(stretches.map((s) => s.sourceKind)).size !== stretches.length ||
    (stretches.length === 2 &&
      (stretches[0].sourceKind !== "archive" ||
        Date.parse(stretches[0].end) >= Date.parse(stretches[1].start))) ||
    !live !== (latest === null)
  )
    throw new Error("Invalid glucose history sources");
  const from = Date.parse(start) - 1000,
    until = Date.parse(end);
  let previous = -Infinity;
  const samples: NonNullable<ObservatoryData["glucoseHistory"]>["samples"] = [];
  for (const reading of series) {
    if (!Array.isArray(reading) || reading.length < 2 || reading.length > 3)
      throw new Error("Invalid glucose reading");
    const [seconds, value, bound] = reading as unknown[];
    if (
      !Number.isInteger(seconds) ||
      (seconds as number) <= previous ||
      (seconds as number) * 1000 < from ||
      (seconds as number) * 1000 >= until ||
      typeof value !== "number" ||
      !Number.isFinite(value) ||
      value <= 0 ||
      (reading.length === 3 && bound !== -1 && bound !== 1)
    )
      throw new Error("Invalid glucose reading");
    previous = seconds as number;
    samples.push({
      timestamp: new Date((seconds as number) * 1000).toISOString(),
      value,
      ...(reading.length === 3
        ? { comparison: bound === -1 ? ("<" as const) : (">" as const) }
        : {}),
    });
  }
  const within = (s: (typeof stretches)[number]) =>
    samples.filter(
      (x) =>
        Date.parse(x.timestamp) >= Date.parse(s.start) - 1000 &&
        Date.parse(x.timestamp) <= Date.parse(s.end),
    ).length;
  if (
    stretches.reduce((n, s) => n + s.readings, 0) !== samples.length ||
    stretches.some((s) => within(s) !== s.readings)
  )
    throw new Error("Invalid glucose history sources");
  // The provider the engine names, else the live stretch's, else the archive's.
  const provider = providerId(payload.provider)
    ? payload.provider
    : (live?.provider ?? stretches[0]?.provider ?? glucoseSlot);
  const prov: HealthProvenance = {
    sourceId: provider,
    recordId: latest ? (latest.resourceId as string) : null,
    observedAt: latest
      ? new Date(Date.parse(latest.observedAt as string)).toISOString()
      : (samples.at(-1)?.timestamp ?? null),
    retrievedAt: now,
    sourceUpdatedAt: latest ? (latest.sourceUpdatedAt as string | null) : null,
    lastSyncedAt: payload.lastSyncedAt as string | null,
    demo,
    kind: latest ? "live" : "archive",
  };
  const status = samples.length
    ? healthStatus({ provenance: prov, staleAfterMinutes: 20 }, Date.parse(now))
    : "missing";
  // The chart and sparkline: the live feed's exact readings of the last 24 hours.
  const day24 = until - 86400000;
  const recent = live
    ? samples.filter(
        (s) =>
          !s.comparison &&
          Date.parse(s.timestamp) >= day24 &&
          Date.parse(s.timestamp) >= Date.parse(live.start) - 1000,
      )
    : [];
  result.glucose = {
    id: "recent-glucose",
    unit: "mg/dL",
    provenance: prov,
    status: recent.length ? status : "missing",
    staleAfterMinutes: 20,
    samples: recent.map((s) => ({
      timestamp: s.timestamp,
      value: s.value,
      hour: localHour(s.timestamp),
    })),
    reason: payload.complete
      ? "Verified live observations; gaps remain unknown"
      : "Bounded, incomplete history",
  };
  // Live instants are source-zoned. The archive's device clock is read by the engine in the
  // source's configured time zone, so the two meet within one 5-minute slot.
  result.glucoseHistory = {
    start,
    end,
    intervalMinutes: 5,
    unit: "mg/dL",
    timestampBasis: "verified",
    complete: payload.complete,
    sourceKind:
      stretches.length === 2
        ? "combined"
        : (stretches[0]?.sourceKind ?? "live"),
    sources: stretches,
    samples,
    provenance: prov,
    status,
    staleAfterMinutes: 20,
  };
  result.glucoseSummary = archiveSummary(
    payload.summary,
    String(payload.today),
    prov,
  );
  result.sources = result.sources.map((s) =>
    s.id === glucoseSlot
      ? {
          ...s,
          name: sourceName(provider),
          connected: true,
          status: status === "stale" ? "stale" : "available",
          sampleAt: samples.at(-1)?.timestamp ?? null,
          retrievedAt: now,
          lastSyncedAt: payload.lastSyncedAt as string | null,
        }
      : s,
  );
  return result;
}

export function mapAdditionalHealth(
  payload: unknown,
  part: HealthPart,
  now = new Date().toISOString(),
): ObservatoryData {
  if (part === "history" && object(payload) && "series" in payload)
    return mapFortnight(payload, now);
  const result = unavailableHealth(now);
  result.healthMode = "live";
  // Only glucose has a demo seed, accepted for its synthetic subject only when labelled demo.
  const demo = part === "history" && object(payload) && payload.demo === true;
  if (
    !object(payload) ||
    !(
      knownSubject(payload.subject) ||
      (demo && typeof payload.subject === "string")
    ) ||
    payload.selection !== part ||
    typeof payload.complete !== "boolean" ||
    !Array.isArray(payload.rows) ||
    payload.rows.length > 2000 ||
    !day(payload.today) ||
    // Calendar coverage is Garmin's; the 24-hour glucose form has its start and end instead.
    (part === "garmin" &&
      (!day(payload.startDate) ||
        !day(payload.endDate) ||
        typeof payload.coverageComplete !== "boolean")) ||
    (payload.lastSyncedAt !== null && !iso(payload.lastSyncedAt))
  )
    throw new Error("Invalid health contract");
  const rows = payload.rows;
  for (const row of rows) {
    if (
      !object(row) ||
      row.subject_id !== payload.subject ||
      typeof row.resource_id !== "string" ||
      typeof row.metric !== "string" ||
      typeof row.value !== "number" ||
      !Number.isFinite(row.value) ||
      row.value < 0 ||
      typeof row.quality !== "string" ||
      !day(row.local_date) ||
      (row.observed_at !== null && !iso(row.observed_at)) ||
      (row.source_updated_at !== null && !iso(row.source_updated_at))
    )
      throw new Error("Invalid health row");
    if (
      part === "garmin" &&
      (row.provider !== "garmin" ||
        !(row.metric in definitions) ||
        row.unit !== definitions[row.metric as keyof typeof definitions][0] ||
        !validTimeZone(row.timezone) ||
        !["metric", "activity"].includes(String(row.source_kind)) ||
        row.local_date < String(payload.startDate) ||
        row.local_date >= String(payload.endDate) ||
        // Activity descriptors are optional (older readers omit them) but exact.
        (row.activity_type != null &&
          !/^[a-z0-9_]{1,40}$/.test(String(row.activity_type))) ||
        (row.pool_unit != null &&
          !["meter", "yard"].includes(String(row.pool_unit))) ||
        (row.multisport != null &&
          !["parent", "leg"].includes(String(row.multisport))))
    )
      throw new Error("Invalid Garmin row");
    if (
      part === "history" &&
      (!providerId(row.provider) ||
        !liveSourceKind(row.source_kind) ||
        (row.metric !== "glucose" && row.metric !== "sensor_glucose") ||
        row.unit !== "mg/dL" ||
        !iso(row.observed_at) ||
        row.value <= 0 ||
        !/(^|;)time_basis=(source-zone|explicit-offset)(;|$)/.test(row.quality))
    )
      throw new Error("Invalid glucose history row");
  }
  const provenance = (row?: Record<string, unknown>): HealthProvenance => ({
    sourceId:
      part === "garmin"
        ? "garmin"
        : row && providerId(row.provider)
          ? row.provider
          : glucoseSlot,
    recordId: row ? String(row.resource_id) : null,
    observedAt: (row?.observed_at as string | null) ?? null,
    retrievedAt: now,
    sourceUpdatedAt: (row?.source_updated_at as string | null) ?? null,
    lastSyncedAt: payload.lastSyncedAt as string | null,
    demo,
    kind: part === "garmin" ? "recorded" : "live",
    ...(part === "garmin" && row
      ? { calendarDate: String(row.local_date), timeZone: String(row.timezone) }
      : {}),
  });
  if (part === "history") {
    if (
      !iso(payload.start) ||
      !iso(payload.end) ||
      Date.parse(payload.end) - Date.parse(payload.start) !== 86400000 ||
      Math.abs(Date.parse(payload.end) - Date.parse(now)) > 60000
    )
      throw new Error("Invalid history window");
    // Keep each accepted value together with its source row; excluded bounds
    // must never become the provenance of an exact reading at the same instant.
    const samples = new Map<
      string,
      { value: number; row: Record<string, unknown> }
    >();
    for (const row of rows) {
      const at = String(row.observed_at),
        time = Date.parse(at);
      if (
        time < Date.parse(payload.start) ||
        time >= Date.parse(payload.end) ||
        /comparison=[<>]/.test(String(row.quality))
      )
        continue;
      const normalized = new Date(time).toISOString();
      if (
        samples.has(normalized) &&
        samples.get(normalized)!.value !== row.value
      )
        throw new Error("Conflicting history");
      samples.set(normalized, { value: Number(row.value), row });
    }
    const ordered = [...samples]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([timestamp, sample]) => ({ timestamp, value: sample.value }));
    const last = ordered.at(-1),
      lastRow = last ? samples.get(last.timestamp)?.row : undefined,
      prov = { ...provenance(lastRow), observedAt: last?.timestamp ?? null };
    const status = last
      ? healthStatus(
          { provenance: prov, staleAfterMinutes: 20 },
          Date.parse(now),
        )
      : "missing";
    result.glucose = {
      id: "recent-glucose",
      unit: "mg/dL",
      provenance: prov,
      status,
      staleAfterMinutes: 20,
      samples: ordered.map((s) => ({ ...s, hour: localHour(s.timestamp) })),
      reason: payload.complete
        ? "Verified live observations; gaps remain unknown"
        : "Bounded, incomplete history",
    };
    result.glucoseHistory = {
      start: payload.start,
      end: payload.end,
      intervalMinutes: 5,
      unit: "mg/dL",
      timestampBasis: "verified",
      complete: payload.complete,
      sourceKind: "live",
      samples: ordered,
      provenance: prov,
      status,
      staleAfterMinutes: 20,
    };
    result.glucoseSummary = archiveSummary(
      payload.summary,
      String(payload.today),
      prov,
    );
    result.sources = result.sources.map((s) =>
      s.id === glucoseSlot
        ? {
            ...s,
            name: sourceName(prov.sourceId),
            connected: true,
            status: status === "stale" ? "stale" : "available",
            sampleAt: last?.timestamp ?? null,
            retrievedAt: now,
            lastSyncedAt: payload.lastSyncedAt as string | null,
          }
        : s,
    );
    return result;
  }
  const metric = (
    key: keyof typeof definitions,
    label: string,
    unit: string,
  ): HealthMetric => {
    const row = rows
      .filter((r) => r.metric === key)
      .sort(
        (a, b) =>
          String(b.local_date).localeCompare(String(a.local_date)) ||
          String(b.observed_at).localeCompare(String(a.observed_at)),
      )[0];
    const prov = provenance(row),
      value = row ? Number(row.value) / definitions[key][1] : null;
    return {
      id: key,
      label,
      unit,
      value,
      provenance: prov,
      status: row
        ? row.local_date < String(payload.today)
          ? "stale"
          : healthStatus(
              { provenance: prov, staleAfterMinutes: 2160 },
              Date.parse(now),
            )
        : "missing",
      staleAfterMinutes: 2160,
    };
  };
  result.steps = metric("steps", "Steps", "steps");
  const sleep = metric("sleep_duration", "Sleep", "min"),
    restingHeartRate = metric("resting_heart_rate", "Resting HR", "bpm"),
    overnightHrv = metric("hrv_overnight", "Overnight HRV", "ms");
  const available = [sleep, restingHeartRate, overnightHrv].find(
    (m) => m.value !== null,
  );
  result.recovery = {
    sleepMinutes: sleep.value,
    restingHeartRate: restingHeartRate.value,
    overnightHrvMs: overnightHrv.value,
    metrics: { sleep, restingHeartRate, overnightHrv },
    provenance: available?.provenance ?? provenance(),
    status: available?.status ?? "missing",
  };
  result.intensityMinutes = {
    moderate: metric("intensity_moderate", "Moderate intensity", "min"),
    vigorous: metric("intensity_vigorous", "Vigorous intensity", "min"),
  };
  // Group by source record: training-effect metrics carry no "activity_" prefix.
  const activities = new Map<string, Record<string, unknown>[]>();
  for (const row of rows.filter((r) => r.source_kind === "activity"))
    activities.set(row.resource_id, [
      ...(activities.get(row.resource_id) ?? []),
      row,
    ]);
  let valid =
    Number.isInteger(payload.activityCount) &&
    payload.activityCount === activities.size;
  for (const [id, values] of activities) {
    const row = values[0];
    if (
      !iso(row.observed_at) ||
      Date.parse(row.observed_at) > Date.parse(now) + 60000
    ) {
      valid = false;
      continue;
    }
    const duration = values.find((r) => r.metric === "activity_duration"),
      distance = values.find((r) => r.metric === "activity_distance");
    const recorded = (key: keyof typeof definitions, digits: number) => {
      const found = values.find((r) => r.metric === key);
      return found ? Number(Number(found.value).toFixed(digits)) : null;
    };
    result.workouts.push({
      id,
      title: "Recorded activity",
      startedAt: row.observed_at,
      durationMinutes: duration
        ? Math.round(Number(duration.value) / 60)
        : null,
      distanceKm: distance
        ? Math.round(Number(distance.value) / 10) / 100
        : null,
      distanceMeters: distance ? Math.round(Number(distance.value)) : null,
      activityType:
        (values.find((r) => r.activity_type)?.activity_type as string) ?? null,
      poolUnit:
        (values.find((r) => r.pool_unit)?.pool_unit as "meter" | "yard") ??
        null,
      multisport:
        (values.find((r) => r.multisport)?.multisport as "parent" | "leg") ??
        null,
      trainingLoad: recorded("activity_training_load", 0),
      aerobicEffect: recorded("training_effect_aerobic", 1),
      anaerobicEffect: recorded("training_effect_anaerobic", 1),
      averageHeartRate: recorded("activity_heart_rate", 0),
      provenance: provenance(row),
      status: "available",
    });
  }
  result.workouts.sort((a, b) => b.startedAt.localeCompare(a.startedAt));
  result.workoutsStatus = payload.coverageComplete ? "available" : "stale";
  // Checked above for Garmin's summary, the only part that reaches here.
  result.workoutsCoverage = {
    complete: payload.complete && payload.coverageComplete === true && valid,
    start: String(payload.startDate),
    end: String(payload.endDate),
    calendarDate: payload.today,
    reason:
      "Totals cover retrieved, normalized activities only; missing days are unknown.",
  };
  result.sources = result.sources.map((s) =>
    s.id === "garmin"
      ? {
          ...s,
          connected: true,
          status: [result.steps, sleep, restingHeartRate, overnightHrv].some(
            (m) => m.status === "available",
          )
            ? "available"
            : "stale",
          sampleAt: available?.provenance.observedAt ?? null,
          calendarDate:
            available?.provenance.calendarDate ??
            result.steps.provenance.calendarDate,
          retrievedAt: now,
          lastSyncedAt: payload.lastSyncedAt as string | null,
        }
      : s,
  );
  return result;
}

export type GarminSyncAnswer = {
  accepted: boolean;
  pendingSince: string | null;
  nextAllowedAt: string | null;
  lastSyncedAt: string | null;
};
const declined: GarminSyncAnswer = {
  accepted: false,
  pendingSince: null,
  nextAllowedAt: null,
  lastSyncedAt: null,
};
// Asks the engine for a Garmin sync now. The engine rate-limits it; anything unavailable or
// malformed, demo mode included, is simply "not accepted".
export async function requestGarminSync(
  request: Request,
  fetcher: typeof fetch = fetch,
): Promise<GarminSyncAnswer> {
  try {
    const answer = await callTool(
      "app_request_garmin_sync",
      {},
      request.signal,
      fetcher,
    );
    const stamp = (v: unknown) => v === null || iso(v);
    if (
      Object.keys(answer).length !== 4 ||
      typeof answer.accepted !== "boolean" ||
      !stamp(answer.pendingSince) ||
      !iso(answer.nextAllowedAt) ||
      !stamp(answer.lastSyncedAt)
    )
      throw new Error("Invalid sync answer");
    return {
      accepted: answer.accepted,
      pendingSince: answer.pendingSince as string | null,
      nextAllowedAt: answer.nextAllowedAt,
      lastSyncedAt: answer.lastSyncedAt as string | null,
    };
  } catch (error) {
    answerRejected("app_request_garmin_sync", error, request.signal);
    return declined;
  }
}

// Garmin's summary (app_garmin_summary) or 14 days of glucose (app_recent_glucose), read through
// the engine. An adapter that is off leaves its source "Not connected"; any other failure marks
// it unavailable. Never demo data once an engine is configured.
export async function loadAdditionalHealth(
  part: HealthPart,
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  const unavailable = unavailableHealth();
  unavailable.healthMode = "live";
  try {
    return mapAdditionalHealth(
      await callTool(
        part === "garmin" ? "app_garmin_summary" : "app_recent_glucose",
        part === "garmin" ? {} : { days: 14 },
        request.signal,
        fetcher,
      ),
      part,
    );
  } catch (error) {
    answerRejected(
      part === "garmin" ? "app_garmin_summary" : "app_recent_glucose",
      error,
      request.signal,
    );
    return failedSource(
      unavailable,
      part === "garmin" ? "garmin" : glucoseSlot,
      error instanceof EngineError
        ? error
        : new EngineError("invalid_response"),
    );
  }
}
