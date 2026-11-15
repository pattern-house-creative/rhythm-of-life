import type { GraphNode } from "./knowledge.js";
import { catalogProvider } from "./instance.js";
import {
  feedbackValues,
  insightIdValid,
  insightRunValid,
  insightSourceKinds,
  insightValid,
  object,
} from "./validation.js";

// The daily insights (shared contract v1). Each day the household's AI client may save
// one over MCP; the app only reads them and records feedback on them. All of their text
// is untrusted source data.
export type InsightKind = "focus" | "discovery" | "suggestion";
export type PointType = "observed" | "sourced" | "inferred" | "hypothesis";
export type InsightStatus =
  | "new"
  | "saved"
  | "explored"
  | "snoozed"
  | "dismissed"
  | "superseded"
  | "handled";
export type FeedbackValue = (typeof feedbackValues)[number];
// Where a source is: a star (graph), a record the engine keeps itself (record; "nas" and
// "rhythm" are older names for it, still read), a document, a memory (Rhythm's own or Second
// Brain's), a web page, and the older drive and second-brain.
export type InsightSourceKind = (typeof insightSourceKinds)[number];
export interface InsightPoint {
  text: string;
  type: PointType;
  // An index into the insight's sources.
  source?: number;
}
export interface InsightStar {
  graphId: string;
  title: string;
}
export interface InsightSource {
  kind: InsightSourceKind;
  title: string;
  url?: string;
  locator?: string;
  publishedAt?: string;
  retrievedAt: string;
}
export interface InsightFeedback {
  value: FeedbackValue;
  at: string;
  until: string | null;
  // The person's own words with the feedback, if any.
  note: string | null;
}
// One run of the engine, newest first in the list.
export interface InsightRun {
  id: string;
  generatedBy: DailyInsight["generatedBy"];
  outcome: "saved" | "quiet" | "blocked";
  note: string | null;
  insightId: string | null;
  at: string;
}
export interface DailyInsight {
  id: string;
  kind: InsightKind;
  title: string;
  summary: string;
  whyItMatters: string;
  nextStep: string;
  points: InsightPoint[];
  evidenceLevel: "strong" | "moderate" | "weak";
  limitations: string;
  stars: InsightStar[];
  sources: InsightSource[];
  // A short label for what made it, such as "harness-daily" or "manual".
  generatedBy: string;
  generatedAt: string;
  sourcesAsOf: string;
  supersedes: string | null;
  status: InsightStatus;
  feedback: InsightFeedback | null;
  createdAt: string;
  updatedAt: string;
}

// A valid insight, rebuilt from the contract's fields alone, or null. Unknown
// fields are left behind, and a point's source must be one of its sources.
export function cleanInsight(value: unknown): DailyInsight | null {
  if (!insightValid(value)) return null;
  const v = value as DailyInsight;
  return {
    id: v.id,
    kind: v.kind,
    title: v.title,
    summary: v.summary,
    whyItMatters: v.whyItMatters,
    nextStep: v.nextStep,
    points: v.points.map((p) => ({
      text: p.text,
      type: p.type,
      ...(typeof p.source === "number" && p.source < v.sources.length
        ? { source: p.source }
        : {}),
    })),
    evidenceLevel: v.evidenceLevel,
    limitations: v.limitations,
    stars: v.stars.map((s) => ({ graphId: s.graphId, title: s.title })),
    sources: v.sources.map((s) => ({
      kind: s.kind,
      title: s.title,
      ...(s.url ? { url: s.url } : {}),
      ...(s.locator ? { locator: s.locator } : {}),
      ...(s.publishedAt ? { publishedAt: s.publishedAt } : {}),
      retrievedAt: s.retrievedAt,
    })),
    generatedBy: v.generatedBy,
    generatedAt: v.generatedAt,
    sourcesAsOf: v.sourcesAsOf,
    supersedes: v.supersedes ?? null,
    status: v.status,
    feedback: v.feedback
      ? {
          value: v.feedback.value,
          at: v.feedback.at,
          until: v.feedback.until ?? null,
          note: v.feedback.note ?? null,
        }
      : null,
    createdAt: v.createdAt,
    updatedAt: v.updatedAt,
  };
}

// The list response, failing closed: a malformed envelope is no insights, and
// a malformed insight is left out.
export function cleanInsights(value: unknown): DailyInsight[] {
  if (
    !object(value) ||
    !Array.isArray(value.insights) ||
    value.insights.length > 50
  )
    return [];
  const seen = new Set<string>();
  const result: DailyInsight[] = [];
  for (const item of value.insights) {
    const insight = cleanInsight(item);
    if (!insight || seen.has(insight.id)) continue;
    seen.add(insight.id);
    result.push(insight);
  }
  return result;
}

// The runs in a list response, newest first, failing closed to none.
export function cleanRuns(value: unknown): InsightRun[] {
  if (!object(value) || !Array.isArray(value.runs) || value.runs.length > 50)
    return [];
  return (value.runs.filter(insightRunValid) as InsightRun[])
    .map((r) => ({
      id: r.id,
      generatedBy: r.generatedBy,
      outcome: r.outcome,
      note: r.note ?? null,
      insightId: r.insightId ?? null,
      at: r.at,
    }))
    .sort((a, b) => time(b.at) - time(a.at))
    .slice(0, 14);
}

// A feedback note: 1 to 500 characters once trimmed.
export const noteValid = (note: unknown): note is string =>
  typeof note === "string" &&
  note.trim().length >= 1 &&
  note.trim().length <= 500;

// One feedback request from the app: a known value, an optional note, and
// for "later" the time to come back, within a month.
export function feedbackInput(
  value: unknown,
  now = Date.now(),
): { id: string; value: FeedbackValue; until?: string; note?: string } | null {
  if (
    !object(value) ||
    Object.keys(value).some(
      (k) => !["id", "value", "until", "note"].includes(k),
    ) ||
    !insightIdValid(value.id) ||
    !feedbackValues.includes(value.value as FeedbackValue) ||
    (value.note !== undefined && value.note !== null && !noteValid(value.note))
  )
    return null;
  const id = value.id as string,
    choice = value.value as FeedbackValue;
  const note = noteValid(value.note) ? { note: value.note.trim() } : {};
  if (choice !== "later")
    return value.until === undefined || value.until === null
      ? { id, value: choice, ...note }
      : null;
  const until =
    typeof value.until === "string" && value.until.length <= 40
      ? Date.parse(value.until)
      : NaN;
  if (
    !Number.isFinite(until) ||
    until < now - 3_600_000 ||
    until > now + 31 * 86_400_000
  )
    return null;
  return { id, value: choice, until: new Date(until).toISOString(), ...note };
}

const time = (iso: string | null | undefined) => Date.parse(iso ?? "");
// An insight stays current for about three days.
export const currentForMs = 3 * 86_400_000;
// When an insight became the one to show: when it was made, or for a snoozed
// one, when its snooze ended. Null when it is not one to show.
export function shownSince(insight: DailyInsight, now = Date.now()) {
  if (insight.status === "new" || insight.status === "saved") {
    const at = time(insight.generatedAt);
    return Number.isFinite(at) ? at : null;
  }
  if (insight.status === "snoozed") {
    const until = time(insight.feedback?.until);
    return Number.isFinite(until) && until <= now ? until : null;
  }
  return null;
}
// The insight the ambient corner shows: the newest one that is new or saved
// and under about three days old, or snoozed and back (for three days from
// its return). One another insight replaced never returns.
export function currentInsight(
  insights: DailyInsight[],
  now = Date.now(),
): DailyInsight | null {
  const replaced = new Set(insights.map((i) => i.supersedes).filter(Boolean));
  let best: DailyInsight | null = null,
    bestAt = -Infinity;
  for (const insight of insights) {
    if (replaced.has(insight.id)) continue;
    const at = shownSince(insight, now);
    // A clock a little ahead is fine; a far-future date is not believed.
    if (at === null || now - at >= currentForMs || at - now > 3_600_000)
      continue;
    if (at > bestAt) {
      best = insight;
      bestAt = at;
    }
  }
  return best;
}
// Every insight, newest first.
export const newestFirst = (insights: DailyInsight[]) =>
  [...insights].sort(
    (a, b) => (time(b.generatedAt) || 0) - (time(a.generatedAt) || 0),
  );

// What feedback does to an insight, as the engine records it.
export const feedbackStatus: Record<FeedbackValue, InsightStatus> = {
  interesting: "saved",
  explore: "explored",
  not_useful: "dismissed",
  later: "snoozed",
  handled: "handled",
};
export function applyFeedback(
  insight: DailyInsight,
  value: FeedbackValue,
  { until, note }: { until?: string; note?: string } = {},
  now = new Date(),
): DailyInsight {
  const at = now.toISOString();
  return {
    ...insight,
    status: feedbackStatus[value],
    feedback: {
      value,
      at,
      until: value === "later" ? (until ?? null) : null,
      note: note?.trim() || null,
    },
    updatedAt: at,
  };
}
// "Remind me later": tomorrow morning or a week from now, at 7 in the morning.
export function laterUntil(when: "tomorrow" | "week", now = new Date()) {
  const d = new Date(now);
  d.setDate(d.getDate() + (when === "tomorrow" ? 1 : 7));
  d.setHours(7, 0, 0, 0);
  return d.toISOString();
}

export const kindNames: Record<InsightKind, string> = {
  focus: "Focus",
  discovery: "Discovery",
  suggestion: "Idea",
};
export const pointNames: Record<PointType, string> = {
  observed: "Observed",
  sourced: "Outside source",
  inferred: "Inference",
  hypothesis: "Hypothesis",
};
// Facts first, then outside claims, then what was inferred or guessed.
export const pointOrder: PointType[] = [
  "observed",
  "sourced",
  "inferred",
  "hypothesis",
];

const dayKey = (d: Date) => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
// "today", "yesterday" or "Mon, Oct 24", in local time.
export function dayName(iso: string | null | undefined, now = Date.now()) {
  const t = time(iso);
  if (!Number.isFinite(t)) return "";
  const d = new Date(t),
    today = new Date(now),
    yesterday = new Date(now);
  yesterday.setDate(today.getDate() - 1);
  if (dayKey(d) === dayKey(today)) return "today";
  if (dayKey(d) === dayKey(yesterday)) return "yesterday";
  return d.toLocaleDateString("en-US", {
    weekday: "short",
    month: "short",
    day: "numeric",
  });
}
export const clockTime = (iso: string) =>
  new Date(iso).toLocaleTimeString("en-US", {
    hour: "numeric",
    minute: "2-digit",
  });

// Where an insight stands, in a word or two.
export function statusName(insight: DailyInsight, now = Date.now()) {
  switch (insight.status) {
    case "new":
      return "New";
    case "saved":
      return "Saved";
    case "explored":
      return "Explored";
    case "dismissed":
      return "Not useful";
    case "superseded":
      return "Replaced";
    case "handled":
      return "Handled";
    case "snoozed": {
      const until = insight.feedback?.until;
      if (!until || !Number.isFinite(time(until))) return "Later";
      return time(until) <= now ? "Back" : `Later · ${dayName(until, now)}`;
    }
  }
}

// The newest run, in a line: "Last run 6:02 AM · quiet day", with its day
// when it wasn't today. Null with no runs.
export function runLine(runs: InsightRun[], now = Date.now()) {
  const run = runs[0];
  if (!run) return null;
  const day = dayName(run.at, now);
  const when =
    day === "today" ? clockTime(run.at) : `${day}, ${clockTime(run.at)}`;
  const note = run.note?.replace(/\s+/g, " ").trim() ?? "";
  const outcome =
    run.outcome === "saved"
      ? "new insight"
      : run.outcome === "quiet"
        ? "quiet day"
        : note
          ? `blocked: ${note.length > 80 ? `${note.slice(0, 80).trimEnd()}…` : note}`
          : "blocked";
  return `Last run ${when} · ${outcome}`;
}

// When a snoozed insight comes back: "tomorrow morning" or "on Wed, Nov 4".
export function laterName(until: string, now = Date.now()) {
  const tomorrow = new Date(now);
  tomorrow.setDate(tomorrow.getDate() + 1);
  const d = new Date(until);
  return dayKey(d) === dayKey(tomorrow)
    ? "tomorrow morning"
    : `on ${d.toLocaleDateString("en-US", { weekday: "short", month: "short", day: "numeric" })}`;
}

// The loaded star a source stands for: by its graph id (the template's locator for every
// kind), else by the record's own id among the stars of its kind. Web pages and Drive files
// open by their link instead.
// - record (and the older rhythm and nas): one of the engine's own stars, under its catalog
//   provider id;
// - document: a document star, of the engine's catalog or a source's;
// - memory: a memory star, Rhythm's own (provider memory) or Second Brain's;
// - second-brain: a Second Brain star.
// A record's own id is its star's external id, or, for the engine's stars, which carry their
// subject in it (`<subject>:<id>`), the part after the subject. A resource id from get_resource
// names no star here, so such a source is named without a link.
const sourceProviders = (kind: InsightSource["kind"]) =>
  kind === "memory"
    ? ["memory", "second-brain"]
    : kind === "second-brain"
      ? ["second-brain"]
      : kind === "document"
        ? null
        : [catalogProvider()];
export function sourceStar(source: InsightSource, nodes: GraphNode[]) {
  if (source.kind === "web" || source.kind === "drive" || !source.locator)
    return null;
  const locator = source.locator;
  const byId = nodes.find((n) => n.id === locator);
  if (byId || source.kind === "graph") return byId?.id ?? null;
  const providers = sourceProviders(source.kind);
  const node = nodes.find((n) => {
    const { provider, externalId } = n.resource.identity;
    if (providers ? !providers.includes(provider) : n.type !== "document")
      return false;
    return (
      externalId === locator ||
      n.provenance.recordId === locator ||
      externalId === `${n.resource.owner}:${locator}`
    );
  });
  return node?.id ?? null;
}
