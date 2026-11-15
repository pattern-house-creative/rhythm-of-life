// The engine's provider id: its catalog provider for records the engine keeps itself ("rhythm",
// or an older engine's id the install kept: catalogProvider in instance.ts), an adapter's
// provider ("notes", "csv", "documents", "drive", "garmin", ...), a plugin's provider
// ("carelink", ...), or "demo". Display names live in graphView.ts.
export type SourceId = string;
export type DemoState = "ready" | "stale" | "empty" | "loading" | "error";
export interface Provenance {
  sourceId: SourceId;
  recordId: string;
  observedAt: string;
  demo: boolean;
}
export type MeasurementStatus =
  "available" | "stale" | "missing" | "unavailable";
export interface HealthProvenance {
  sourceId: SourceId;
  recordId: string | null;
  observedAt: string | null;
  retrievedAt?: string;
  calendarDate?: string;
  timeZone?: string;
  sourceUpdatedAt?: string | null;
  lastSyncedAt?: string | null;
  demo: boolean;
  kind?: "live" | "archive" | "recorded";
}
export interface HealthState {
  status?: MeasurementStatus;
  staleAfterMinutes?: number;
  reason?: string;
}
export interface HealthMetric extends HealthState {
  id: string;
  label: string;
  value: number | null;
  unit: string;
  provenance: HealthProvenance;
}
export interface MeasurementSeries extends HealthState {
  id: string;
  unit: string;
  samples: { hour: number; value: number; timestamp: string }[];
  provenance: HealthProvenance;
}
export interface GlucoseHistory extends HealthState {
  start: string;
  end: string; // Exclusive; each retained reading represents one fixed cadence slot.
  intervalMinutes: number;
  unit: string;
  timestampBasis: "verified" | "unverified";
  complete: boolean; // All requested pages read, not a promise of no sensor gaps.
  // "combined": the source's archive up to the first live reading, then its live feed.
  sourceKind: "demo" | "archive" | "live" | "combined";
  sources?: GlucoseStretch[];
  samples: { timestamp: string; value: number; comparison?: "<" | ">" }[];
  provenance: HealthProvenance;
}
// One source's stretch of a glucose history; stretches never overlap or interleave.
export interface GlucoseStretch {
  sourceKind: "archive" | "live";
  start: string; // First and last reading, inclusive.
  end: string;
  readings: number;
  // Archive times are the device's local clock read in its configured time zone; live times
  // are source-zoned instants.
  timeBasis: "device-local" | "source-zone";
  // The stretch's provider (its newest reading's), when the engine names it.
  provider?: SourceId;
}
// Source-computed totals over the latest 14 complete days of the glucose source's
// archive (one reading per 5-minute slot); the readings stay on the source.
// Days are the device's local calendar days; its time zone is unverified.
// Bounded (beyond sensor range) readings are counted in the outer bands only.
export interface GlucoseAggregate extends HealthState {
  startDate: string;
  endDate: string; // Inclusive.
  sourceKind: "archive";
  timeBasis: "device-local-unverified";
  lastImportedAt: string | null;
  intervalMinutes: number;
  readings: number;
  exactReadings: number;
  exactSum: number;
  bands: {
    veryLow: number;
    low: number;
    inRange: number;
    high: number;
    veryHigh: number;
  };
  provenance: HealthProvenance;
}
export interface Workout extends HealthState {
  id: string;
  title: string;
  distanceKm: number | null;
  durationMinutes: number | null;
  // The device's activity type key (e.g. "lap_swimming"), pool unit for swims, and the
  // multisport role: legs carry the time and distance, the parent groups them.
  activityType?: string | null;
  poolUnit?: "meter" | "yard" | null;
  multisport?: "parent" | "leg" | null;
  distanceMeters?: number | null;
  // Device-reported per-activity statistics; absent or null when not recorded.
  trainingLoad?: number | null;
  aerobicEffect?: number | null;
  anaerobicEffect?: number | null;
  averageHeartRate?: number | null;
  startedAt: string;
  provenance: HealthProvenance;
}
export interface RecoverySummary extends HealthState {
  metrics?: {
    sleep: HealthMetric;
    restingHeartRate: HealthMetric;
    overnightHrv: HealthMetric;
  };
  sleepMinutes: number | null;
  restingHeartRate: number | null;
  overnightHrvMs: number | null;
  provenance: HealthProvenance;
}
export type NodeType =
  "theme" | "project" | "note" | "record" | "document" | "memory" | "decision";
export interface KnowledgeNode {
  id: string;
  title: string;
  type: NodeType;
  // Optional source facets; coordinates from old fixtures are never used by layout.
  cluster?: string;
  groups?: { id: string; label: string }[];
  x?: number;
  y?: number;
  overview: string;
  provenance: Provenance;
}
export interface KnowledgeEdge {
  source: string;
  target: string;
  relationship: string;
  strength: number;
}
export interface Insight {
  title: string;
  body: string;
  nodeId: string;
}
export interface SourceStatus {
  detail?: string;
  calendarDate?: string;
  retrievedAt?: string;
  lastSyncedAt?: string | null;
  id: SourceId;
  name: string;
  connected: boolean;
  sampleAt: string | null;
  status: "demo" | "available" | "stale" | "unavailable" | "disconnected";
  // Not read yet, so whether it is connected isn't known: the home screen
  // waits before showing its tiles.
  checking?: boolean;
}
export interface ObservatoryData {
  glucose: MeasurementSeries;
  glucoseHistory: GlucoseHistory | null;
  glucoseSummary?: GlucoseAggregate | null;
  current: HealthMetric;
  recovery: RecoverySummary;
  steps: HealthMetric;
  // Daily device-reported minutes for the latest calendar day; never summed into a goal.
  intensityMinutes?: { moderate: HealthMetric; vigorous: HealthMetric };
  workouts: Workout[];
  workoutsStatus?: MeasurementStatus;
  workoutsCoverage?: {
    complete: boolean;
    start: string;
    end: string;
    calendarDate: string;
    reason?: string;
  };
  healthMode?: "demo" | "live";
  retrievedAt?: string;
  nodes: KnowledgeNode[];
  edges: KnowledgeEdge[];
  sources: SourceStatus[];
  insight: Insight;
}
export function sleepDuration(minutes: number | null) {
  return minutes == null
    ? "Unavailable"
    : `${Math.floor(minutes / 60)}h ${Math.round(minutes % 60)}m`;
}
export function sampleChange(series: MeasurementSeries) {
  const last = series.samples.at(-1),
    previous = series.samples.at(-2);
  if (
    !series.provenance.demo &&
    last &&
    previous &&
    (Date.parse(last.timestamp) - Date.parse(previous.timestamp) > 600000 ||
      Date.parse(last.timestamp) <= Date.parse(previous.timestamp))
  )
    return null;
  return last && previous
    ? { delta: last.value - previous.value, previousAt: previous.timestamp }
    : null;
}
export function weeklyWorkouts(workouts: Workout[], sampleDay: string | null) {
  if (!sampleDay || !Number.isFinite(Date.parse(sampleDay)))
    return {
      records: [] as Workout[],
      counted: [] as Workout[],
      sessions: 0,
      minutes: 0,
      days: Array<number>(7).fill(0),
    };
  // Group the source-reported calendar dates, without converting them to the viewer timezone.
  const end = new Date(`${sampleDay.slice(0, 10)}T12:00:00Z`);
  const start = new Date(end);
  start.setUTCDate(end.getUTCDate() - ((end.getUTCDay() + 6) % 7));
  const first = start.toISOString().slice(0, 10),
    last = end.toISOString().slice(0, 10);
  const records = workouts.filter(
    (w) =>
      (w.provenance.calendarDate ?? w.startedAt.slice(0, 10)) >= first &&
      (w.provenance.calendarDate ?? w.startedAt.slice(0, 10)) <= last,
  );
  records.sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt));
  // A multisport parent spans its legs: count its time once (through the legs)
  // and count the event as one session (through the parent).
  const counted = records.filter((w) => w.multisport !== "parent");
  const sessions = records.filter((w) => w.multisport !== "leg").length;
  const minutes = counted.reduce((sum, w) => sum + (w.durationMinutes ?? 0), 0);
  const days = Array.from({ length: 7 }, (_, i) =>
    counted
      .filter(
        (w) =>
          (new Date(
            `${w.provenance.calendarDate ?? w.startedAt.slice(0, 10)}T12:00:00Z`,
          ).getUTCDay() +
            6) %
            7 ===
          i,
      )
      .reduce((sum, w) => sum + (w.durationMinutes ?? 0), 0),
  );
  return { records, counted, sessions, minutes, days };
}
export interface ObservatoryAdapter {
  readonly mode: "demo" | "live" | "mixed";
  load(signal?: AbortSignal): Promise<ObservatoryData>;
}
export function filterNodes(
  nodes: KnowledgeNode[],
  query: string,
  source: string,
  type: string,
) {
  return nodes.filter(
    (n) =>
      (!query ||
        `${n.title} ${n.overview} ${n.cluster}`
          .toLowerCase()
          .includes(query.toLowerCase())) &&
      (source === "all" || n.provenance.sourceId === source) &&
      (type === "all" || n.type === type),
  );
}
