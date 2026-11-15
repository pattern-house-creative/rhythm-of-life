import { object } from "./validation.js";

// Sync now (SB-P6-09; app_sync, contracts/README.md 2.2): the adapters that sync or import on
// the engine's own clock, each with how often it runs on its own and how often it may be asked,
// whether a batch of it is running, and how the last one asked for ended. States and times only.
// The facade serves the list as GET /api/sync and one batch now as POST /api/sync {adapter}:
// available false in demo mode, with an engine older than app_sync, or when the call failed.
export type SyncKind = "measurements" | "imports";
export type SyncOutcome = "complete" | "failed" | "timed-out";
export type SyncRefusal = "running" | "pending" | "too-soon";
export interface AdapterSync {
  adapter: string;
  kind: SyncKind;
  interval: number;
  limit: number;
  running: boolean;
  pendingSince: string | null;
  nextAllowedAt: string | null;
  lastAskedAt: string | null;
  lastFinishedAt: string | null;
  outcome: SyncOutcome | null;
}
export interface SyncAsked {
  accepted: boolean;
  refused: SyncRefusal | null;
  adapter: AdapterSync;
}
export type SyncList =
  { available: true; adapters: AdapterSync[] } | { available: false };
export type SyncAnswer =
  ({ available: true } & SyncAsked) | { available: false };

const outcomes: readonly SyncOutcome[] = ["complete", "failed", "timed-out"];
const refusals: readonly SyncRefusal[] = ["running", "pending", "too-soon"];
const seconds = (v: unknown, least: number): v is number =>
  typeof v === "number" && Number.isInteger(v) && v >= least && v <= 31_536_000;
const instant = (v: unknown): v is string | null =>
  v === null ||
  (typeof v === "string" &&
    v.length <= 40 &&
    /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,9})?(Z|[+-]\d\d:\d\d)$/.test(v) &&
    Number.isFinite(Date.parse(v)));
export const adapterPattern = /^[a-z][a-z0-9-]{1,30}$/;

// One adapter's state rebuilt from the contract's fields alone, or null.
export function cleanAdapterSync(v: unknown): AdapterSync | null {
  if (
    !object(v) ||
    typeof v.adapter !== "string" ||
    !adapterPattern.test(v.adapter) ||
    (v.kind !== "measurements" && v.kind !== "imports") ||
    !seconds(v.interval, 1) ||
    !seconds(v.limit, 60) ||
    typeof v.running !== "boolean" ||
    !instant(v.pendingSince) ||
    !instant(v.nextAllowedAt) ||
    !instant(v.lastAskedAt) ||
    !instant(v.lastFinishedAt) ||
    !(v.outcome === null || outcomes.includes(v.outcome as SyncOutcome))
  )
    return null;
  return {
    adapter: v.adapter,
    kind: v.kind,
    interval: v.interval,
    limit: v.limit,
    running: v.running,
    pendingSince: v.pendingSince,
    nextAllowedAt: v.nextAllowedAt,
    lastAskedAt: v.lastAskedAt,
    lastFinishedAt: v.lastFinishedAt,
    outcome: v.outcome as SyncOutcome | null,
  };
}

// The engine's list: an adapter the app cannot read is left out, at most 64 are kept; null when
// the answer is not a list at all.
export function cleanSyncs(value: unknown): AdapterSync[] | null {
  if (!object(value) || !Array.isArray(value.adapters)) return null;
  return value.adapters
    .slice(0, 64)
    .map(cleanAdapterSync)
    .filter((a): a is AdapterSync => a !== null);
}

// The engine's answer to one request, or null.
export function cleanAsked(value: unknown): SyncAsked | null {
  if (
    !object(value) ||
    typeof value.accepted !== "boolean" ||
    !(
      value.refused === null || refusals.includes(value.refused as SyncRefusal)
    ) ||
    value.accepted !== (value.refused === null)
  )
    return null;
  const adapter = cleanAdapterSync(value.adapter);
  return adapter
    ? {
        accepted: value.accepted,
        refused: value.refused as SyncRefusal | null,
        adapter,
      }
    : null;
}

// The facade's answers as the browser reads them; anything else is "unavailable".
export function cleanSyncList(value: unknown): SyncList {
  const adapters =
    object(value) && value.available === true ? cleanSyncs(value) : null;
  return adapters ? { available: true, adapters } : { available: false };
}
export function cleanSyncAnswer(value: unknown): SyncAnswer {
  const asked =
    object(value) && value.available === true ? cleanAsked(value) : null;
  return asked ? { available: true, ...asked } : { available: false };
}

// An adapter's name as people know it.
const names: Record<string, string> = {
  csv: "CSV files",
  documents: "Documents folder",
  nightscout: "Nightscout",
  garmin: "Garmin",
  demo: "Demo data",
};
export const adapterName = (adapter: string) => names[adapter] ?? adapter;

// "every minute", "every 5 minutes", "every 6 hours".
export function every(secondsBetween: number) {
  const minutes = Math.round(secondsBetween / 60);
  if (secondsBetween < 60) return `every ${secondsBetween} seconds`;
  if (minutes === 1) return "every minute";
  if (minutes < 120) return `every ${minutes} minutes`;
  const hours = Math.round(minutes / 60);
  return hours === 1 ? "every hour" : `every ${hours} hours`;
}

// Whether a request now would be refused, and the state's one word.
export function waitingFor(sync: AdapterSync, now = Date.now()) {
  if (sync.running) return "running" as const;
  if (sync.pendingSince) return "pending" as const;
  if (sync.nextAllowedAt && Date.parse(sync.nextAllowedAt) > now)
    return "too-soon" as const;
  return null;
}
export function stateWord(sync: AdapterSync, now = Date.now()) {
  const why = waitingFor(sync, now);
  return why === "running"
    ? "Syncing"
    : why === "pending"
      ? "Waiting"
      : why === "too-soon"
        ? "Asked lately"
        : "Ready";
}

// What happened to the last batch asked for, in words, or null before any finished.
export function outcomeLine(sync: AdapterSync, stamp: (iso: string) => string) {
  if (!sync.outcome || !sync.lastFinishedAt) return null;
  const at = stamp(sync.lastFinishedAt);
  return sync.outcome === "complete"
    ? `Last synced now: ${at}.`
    : sync.outcome === "failed"
      ? `The last sync now failed (${at}). The engine’s log says why; the regular syncs go on.`
      : `The last sync now ran out of time (${at}); the regular syncs go on.`;
}
