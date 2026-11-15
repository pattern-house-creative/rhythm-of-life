import type { Dashboard, DashboardData } from "./dashboards";
import { unarranged } from "./dashboards";
import { cleanLayout } from "./parts";
import { object } from "./validation";

export interface Control {
  id: string;
  label: string;
  kind: "tab" | "filter" | "week" | "date_range";
  options?: string[];
  initial: unknown;
}
export interface StudioSession {
  id: string;
  screen: string;
  dashboard: string | null;
  revision: number;
  valid_revision: number;
  status: "active" | "saved" | "canceled" | "discarded";
  candidate: StudioDraft | null;
  preview: StudioDraft | null;
  controls: Control[];
  preview_controls: Control[];
  events: {
    seq: number;
    op: string;
    revision: number;
    at: string;
    message?: string;
    ok?: boolean;
  }[];
  feedback: { seq: number; text: string; at: string }[];
  result?: { id: string; version: number };
}
export type StudioDraft = Pick<
  Dashboard,
  "title" | "description" | "html" | "feeds" | "writes" | "note" | "stars"
> & { layout?: unknown };
let visibleStudio: string | null = null;
const shownListeners = new Set<(id: string) => void>();
export function studioShown(id: string | null) {
  visibleStudio = id;
  if (id) for (const listener of shownListeners) listener(id);
}
export async function openStudio(id: string): Promise<boolean> {
  if (visibleStudio === id) return true;
  return new Promise((resolve) => {
    const done = (ok: boolean) => {
      clearTimeout(timer);
      shownListeners.delete(listener);
      resolve(ok);
    };
    const listener = (shown: string) => {
      if (shown === id) done(true);
    };
    const timer = setTimeout(() => done(false), 6500);
    shownListeners.add(listener);
    window.dispatchEvent(
      new CustomEvent("rhythm:studio", { detail: { session: id } }),
    );
  });
}
// A change's request id: a random UUID. A page served over plain http (a screen on the home network by address) has no
// crypto.randomUUID, only crypto.getRandomValues, so it is made from 16 random bytes there (version 4, RFC 9562).
export function requestId(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
export class StudioAccessError extends Error {}
// The engine no longer has the session (its dashboard was deleted, say): there is nothing to reconnect to.
export class StudioGoneError extends Error {}
// A Studio call that hasn't answered by then is given up, and read again later.
export const studioTimeoutMs = 15_000;
export async function studioCall(
  args: Record<string, unknown>,
  signal?: AbortSignal,
): Promise<unknown> {
  const call = new AbortController();
  const stop = () => call.abort(signal?.reason);
  const timer = setTimeout(
    () =>
      call.abort(
        new DOMException("Studio didn't answer in time", "TimeoutError"),
      ),
    studioTimeoutMs,
  );
  if (signal?.aborted) stop();
  signal?.addEventListener("abort", stop);
  try {
    const response = await fetch("/api/screens?studio=1", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(args),
      signal: call.signal,
    });
    // The status first: a front door's or proxy's page is no JSON.
    if (response.status === 401 || response.status === 403)
      throw new StudioAccessError("Studio access ended.");
    const answer: unknown = await response.json().catch(() => null);
    const reason =
      object(answer) && typeof answer.error === "string" ? answer.error : null;
    // A session read the engine refuses is one it no longer has (its "Unknown Studio session").
    if (
      response.status === 404 ||
      response.status === 410 ||
      (response.status === 409 && args.op === "read")
    )
      throw new StudioGoneError(reason ?? "Studio session ended.");
    if (!response.ok || !object(answer) || reason !== null)
      throw new Error(reason ?? "Studio response unavailable");
    return answer.value;
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", stop);
  }
}
export function studioSession(value: unknown): StudioSession | null {
  if (value === null) return null;
  if (
    !object(value) ||
    typeof value.id !== "string" ||
    typeof value.screen !== "string" ||
    !Number.isInteger(value.revision) ||
    !Array.isArray(value.events) ||
    !Array.isArray(value.feedback) ||
    !["active", "saved", "canceled", "discarded"].includes(
      String(value.status),
    ) ||
    !Number.isInteger(value.valid_revision) ||
    !controlManifest(value.controls) ||
    !controlManifest(value.preview_controls) ||
    value.events.length > 400 ||
    value.feedback.length > 100 ||
    value.events.some(
      (e) =>
        !object(e) ||
        typeof e.op !== "string" ||
        typeof e.at !== "string" ||
        !Number.isInteger(e.seq) ||
        !Number.isInteger(e.revision),
    ) ||
    value.feedback.some(
      (f) =>
        !object(f) ||
        typeof f.text !== "string" ||
        typeof f.at !== "string" ||
        !Number.isInteger(f.seq),
    ) ||
    [value.candidate, value.preview].some(
      (d) =>
        d !== null &&
        (!object(d) ||
          typeof d.html !== "string" ||
          d.html.length > 300000 ||
          typeof d.title !== "string" ||
          !Array.isArray(d.feeds) ||
          !Array.isArray(d.writes) ||
          !Array.isArray(d.stars)),
    )
  )
    throw new Error("Invalid Studio state");
  return value as unknown as StudioSession;
}
export function draftDashboard(
  s: StudioSession,
  draft: StudioDraft,
  revision: number,
): Dashboard {
  return {
    ...unarranged,
    ...draft,
    id: s.dashboard ?? s.id,
    version: revision,
    latest: revision,
    layout: cleanLayout(draft.layout)?.layout ?? null,
    createdAt: "",
    updatedAt: "",
    savedAt: "",
    versions: [],
    restoredFrom: null,
    corners: [],
    editsAllowed: false,
  };
}
export function draftData(s: StudioSession, data: unknown): DashboardData {
  if (!object(data) || !object(data.feeds) || typeof data.asOf !== "string")
    throw new Error("Draft feeds unavailable");
  return {
    ...data,
    dashboard: { id: s.dashboard ?? s.id, version: s.revision },
  } as DashboardData;
}
export function accepts(c: Control, v: unknown): boolean {
  if (c.kind === "tab" || c.kind === "filter")
    return typeof v === "string" && !!c.options?.includes(v);
  if (c.kind === "week")
    return Number.isInteger(v) && (v as number) >= -520 && (v as number) <= 520;
  if (
    c.kind !== "date_range" ||
    !object(v) ||
    Object.keys(v).sort().join(",") !== "from,to" ||
    typeof v.from !== "string" ||
    typeof v.to !== "string"
  )
    return false;
  const date = (s: string) =>
    /^\d{4}-\d{2}-\d{2}$/.test(s) &&
    +s.slice(0, 4) > 0 &&
    Number.isFinite(Date.parse(s)) &&
    new Date(s).toISOString().slice(0, 10) === s;
  return date(v.from) && date(v.to) && v.from <= v.to;
}
export function controlManifest(value: unknown): value is Control[] {
  return (
    Array.isArray(value) &&
    value.length <= 8 &&
    new Set(value.map((c) => (object(c) ? c.id : null))).size ===
      value.length &&
    value.every(
      (c) =>
        object(c) &&
        typeof c.id === "string" &&
        /^[a-z][a-z0-9_]{0,31}$/.test(c.id) &&
        typeof c.label === "string" &&
        c.label.length <= 60 &&
        ["tab", "filter", "week", "date_range"].includes(String(c.kind)) &&
        ((c.kind !== "tab" && c.kind !== "filter") ||
          (Array.isArray(c.options) &&
            c.options.length > 0 &&
            c.options.length <= 20 &&
            c.options.every((o) => typeof o === "string" && o.length <= 60))) &&
        accepts(c as unknown as Control, c.initial),
    )
  );
}
