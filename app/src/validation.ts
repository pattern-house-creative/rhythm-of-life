import type { GraphNode, GraphPage } from "./knowledge.js";
import type { ObservatoryData } from "./model.js";
import type { DailyInsight } from "./insights.js";

// Validate at the transport boundary; the existing domain types remain authoritative.
type Check = (value: unknown) => boolean;
const text: Check = (v) => typeof v === "string" && v.length <= 100000;
const number: Check = (v) => typeof v === "number" && Number.isFinite(v);
const boolean: Check = (v) => typeof v === "boolean";
const one =
  (...values: unknown[]): Check =>
  (v) =>
    values.includes(v);
const nullable =
  (check: Check): Check =>
  (v) =>
    v === null || check(v);
const optional =
  (check: Check): Check =>
  (v) =>
    v === undefined || check(v);
const list =
  (check: Check, max = 20000): Check =>
  (v) =>
    Array.isArray(v) && v.length <= max && v.every(check);
export const object = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !Array.isArray(v);
const shape =
  (fields: Record<string, Check>): Check =>
  (v) =>
    object(v) && Object.entries(fields).every(([k, check]) => check(v[k]));
const date: Check = (v) => text(v) && Number.isFinite(Date.parse(v as string));
// A provider id (contracts/README.md section 3.2 names adapters the same way), or "demo".
export const providerId = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][a-z0-9-]{0,40}$/.test(v);
const provider: Check = providerId;
const identity = shape({ provider, account: text, externalId: text });
const provenance = shape({
  sourceId: provider,
  recordId: text,
  observedAt: date,
  demo: boolean,
});
const node = shape({
  id: text,
  title: text,
  type: one(
    "theme",
    "project",
    "note",
    "record",
    "document",
    "memory",
    "decision",
  ),
  overview: text,
  provenance,
  groups: optional(list(shape({ id: text, label: text }))),
  // A data star's recent readings, present only when that one star is opened.
  measurements: optional(
    list(
      shape({
        metric: text,
        label: text,
        unit: text,
        latest: nullable(
          shape({ value: number, observedAt: nullable(date), date: text }),
        ),
        days: list(
          shape({
            date: text,
            min: number,
            mean: number,
            max: number,
            count: number,
          }),
          400,
        ),
      }),
      100,
    ),
  ),
});
// An opened record's text may run longer than other fields: the engine returns up to 200,000 characters.
const longText: Check = (v) => typeof v === "string" && v.length <= 200000;
const resource = shape({
  identity,
  canonicalUrl: nullable(text),
  sourceUrl: nullable(text),
  sourceModifiedAt: nullable(date),
  retrievedAt: date,
  revision: nullable(text),
  owner: text,
  access: shape({ scope: text, canRead: boolean, canOpen: boolean }),
  availability: one("available", "stale", "missing", "restricted"),
  content: nullable(longText),
  representation: optional(one("original", "archive", "index")),
  canonicalIdentity: optional(identity),
});
const edge = shape({
  source: text,
  target: text,
  relationship: text,
  strength: number,
});
const graphNode: Check = (v) => node(v) && object(v) && resource(v.resource);
const graphEdge: Check = (v) =>
  edge(v) &&
  shape({
    id: text,
    type: text,
    origin: identity,
    directed: boolean,
    verification: one(
      "verified",
      "suggested",
      "inferred",
      "disputed",
      "unverified",
    ),
    evidence: list(
      shape({
        resourceId: text,
        excerpt: text,
        locator: text,
        retrievedAt: date,
      }),
    ),
    sourceModifiedAt: nullable(date),
    retrievedAt: date,
  })(v);
export const cursorsValid: Check = (v) =>
  object(v) &&
  Object.keys(v).length <= 20 &&
  Object.entries(v).every(
    ([k, c]) =>
      k.length <= 200 &&
      (c === null || (typeof c === "string" && c.length <= 4096)),
  );
export const statesValid: Check = (v) =>
  object(v) &&
  Object.keys(v).length <= 20 &&
  Object.values(v).every(
    shape({ completeness: one("partial", "complete"), reason: optional(text) }),
  );
const page = shape({
  nodes: list(graphNode, 1500),
  edges: list(graphEdge, 12000),
  nextCursor: nullable(text),
  completeness: one("partial", "complete"),
  issues: list(shape({ provider, reason: text })),
  removedNodeIds: optional(list(text, 1500)),
  removedEdgeIds: optional(list(text, 12000)),
  sourceStates: optional(statesValid),
  discoveryStates: optional(statesValid),
  discoveryCursors: optional(cursorsValid),
});
export function assertGraphPage(value: unknown): asserts value is GraphPage {
  if (!page(value)) throw new Error("Invalid graph response");
}
export function assertGraphNode(
  value: unknown,
): asserts value is GraphNode | null {
  if (value !== null && !graphNode(value))
    throw new Error("Invalid record response");
}
const measurementStatus = one("available", "stale", "missing", "unavailable");
const healthFields = {
  status: optional(measurementStatus),
  staleAfterMinutes: optional((v: unknown) => number(v) && (v as number) > 0),
  reason: optional(text),
  provenance: shape({
    sourceId: provider,
    recordId: nullable(text),
    observedAt: nullable(date),
    retrievedAt: optional(date),
    calendarDate: optional(
      (v) => typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v),
    ),
    timeZone: optional(text),
    sourceUpdatedAt: optional(nullable(date)),
    lastSyncedAt: optional(nullable(date)),
    demo: boolean,
    kind: optional(one("live", "archive", "recorded")),
  }),
};
const metric = shape({
  ...healthFields,
  id: text,
  label: text,
  value: nullable(number),
  unit: text,
});
const series = shape({
  ...healthFields,
  id: text,
  unit: text,
  samples: list(shape({ hour: number, value: number, timestamp: date })),
});
const observatory = shape({
  current: metric,
  steps: metric,
  glucose: series,
  glucoseHistory: nullable(
    shape({
      ...healthFields,
      start: date,
      end: date,
      intervalMinutes: number,
      unit: text,
      timestampBasis: one("verified", "unverified"),
      complete: boolean,
      sourceKind: one("demo", "archive", "live", "combined"),
      sources: optional(
        list(
          shape({
            sourceKind: one("archive", "live"),
            start: date,
            end: date,
            readings: number,
            timeBasis: one("device-local", "source-zone"),
            provider: optional(provider),
          }),
          2,
        ),
      ),
      samples: list(
        shape({
          timestamp: date,
          value: number,
          comparison: optional(one("<", ">")),
        }),
      ),
    }),
  ),
  glucoseSummary: optional(
    nullable(
      shape({
        ...healthFields,
        startDate: (v) =>
          typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v),
        endDate: (v) => typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v),
        sourceKind: one("archive"),
        timeBasis: one("device-local-unverified"),
        lastImportedAt: nullable(date),
        intervalMinutes: number,
        readings: number,
        exactReadings: number,
        exactSum: number,
        bands: shape({
          veryLow: number,
          low: number,
          inRange: number,
          high: number,
          veryHigh: number,
        }),
      }),
    ),
  ),
  recovery: shape({
    ...healthFields,
    sleepMinutes: nullable(number),
    restingHeartRate: nullable(number),
    overnightHrvMs: nullable(number),
    metrics: optional(
      shape({ sleep: metric, restingHeartRate: metric, overnightHrv: metric }),
    ),
  }),
  workouts: list(
    shape({
      ...healthFields,
      id: text,
      title: text,
      distanceKm: nullable(number),
      durationMinutes: nullable(number),
      distanceMeters: optional(nullable(number)),
      activityType: optional(
        nullable((v) => typeof v === "string" && /^[a-z0-9_]{1,40}$/.test(v)),
      ),
      poolUnit: optional(nullable(one("meter", "yard"))),
      multisport: optional(nullable(one("parent", "leg"))),
      trainingLoad: optional(nullable(number)),
      aerobicEffect: optional(nullable(number)),
      anaerobicEffect: optional(nullable(number)),
      averageHeartRate: optional(nullable(number)),
      startedAt: date,
    }),
  ),
  intensityMinutes: optional(shape({ moderate: metric, vigorous: metric })),
  workoutsStatus: optional(measurementStatus),
  workoutsCoverage: optional(
    shape({
      complete: boolean,
      start: date,
      end: date,
      calendarDate: date,
      reason: optional(text),
    }),
  ),
  healthMode: optional(one("demo", "live")),
  retrievedAt: optional(date),
  nodes: list(node),
  edges: list(edge),
  sources: list(
    shape({
      id: provider,
      name: text,
      connected: boolean,
      sampleAt: nullable(date),
      detail: optional(text),
      calendarDate: optional(date),
      retrievedAt: optional(date),
      lastSyncedAt: optional(nullable(date)),
      status: one("demo", "available", "stale", "unavailable", "disconnected"),
    }),
  ),
  insight: shape({ title: text, body: text, nodeId: text }),
});
export function assertObservatory(
  value: unknown,
): asserts value is ObservatoryData {
  if (!observatory(value)) throw new Error("Invalid observatory response");
}

// Daily insights, as the engine returns them (shared contract v1). Their text is
// untrusted source data: bounded here, and only ever shown as text.
const bounded =
  (max: number, min = 0): Check =>
  (v) =>
    typeof v === "string" && v.length >= min && v.length <= max;
const https: Check = (v) => {
  // As long as the engine keeps one (InsightDraft.Https, app_insights' schema), so nothing it saved fails here.
  if (typeof v !== "string" || v.length > 2048) return false;
  try {
    return new URL(v).protocol === "https:";
  } catch {
    return false;
  }
};
// What made an insight or a run: a short label such as "harness-daily" or "manual".
const generatorLabel: Check = (v) =>
  typeof v === "string" && /^[a-z0-9][a-z0-9._-]{0,39}$/.test(v);
export const insightIdValid: Check = (v) =>
  typeof v === "string" && /^[\w-]{1,100}$/.test(v);
export const feedbackValues = [
  "interesting",
  "explore",
  "not_useful",
  "later",
  "handled",
] as const;
// A source's kind: the engine's (contracts/mcp/app_insights.output.schema.json: graph, record,
// document, memory, web, and the older nas, drive and second-brain), and "rhythm", which an
// earlier app read for a record the engine keeps.
export const insightSourceKinds = [
  "graph",
  "record",
  "document",
  "memory",
  "web",
  "nas",
  "drive",
  "second-brain",
  "rhythm",
] as const;
const insightSource: Check = (v) =>
  shape({
    kind: one(...insightSourceKinds),
    title: bounded(200, 1),
    url: optional(nullable(https)),
    locator: optional(nullable(bounded(300))),
    publishedAt: optional(nullable(date)),
    retrievedAt: date,
  })(v) &&
  // Web pages and Drive files are opened by their link, so they need one.
  (!["web", "drive"].includes(String((v as { kind: unknown }).kind)) ||
    typeof (v as { url: unknown }).url === "string");
const insightPoint = shape({
  text: bounded(400, 1),
  type: one("observed", "sourced", "inferred", "hypothesis"),
  source: optional(nullable((v) => Number.isInteger(v) && (v as number) >= 0)),
});
export const insightValid: Check = shape({
  id: insightIdValid,
  kind: one("focus", "discovery", "suggestion"),
  title: bounded(120, 1),
  summary: bounded(600),
  whyItMatters: bounded(600),
  nextStep: bounded(400),
  points: list(insightPoint, 12),
  evidenceLevel: one("strong", "moderate", "weak"),
  limitations: bounded(400),
  stars: list(shape({ graphId: bounded(1000, 1), title: bounded(200) }), 20),
  sources: list(insightSource, 20),
  generatedBy: generatorLabel,
  generatedAt: date,
  sourcesAsOf: date,
  supersedes: optional(nullable(insightIdValid)),
  status: one(
    "new",
    "saved",
    "explored",
    "snoozed",
    "dismissed",
    "superseded",
    "handled",
  ),
  feedback: optional(
    nullable(
      shape({
        value: one(...feedbackValues),
        at: date,
        until: optional(nullable(date)),
        note: optional(nullable(bounded(500))),
      }),
    ),
  ),
  createdAt: date,
  updatedAt: date,
});
// One daily run of the engine: whether it saved an insight, had a quiet day
// or was blocked, with an optional note.
export const insightRunValid: Check = shape({
  id: insightIdValid,
  generatedBy: generatorLabel,
  outcome: one("saved", "quiet", "blocked"),
  note: optional(nullable(bounded(500))),
  insightId: optional(nullable(insightIdValid)),
  at: date,
});
// The list envelope: at most 50 insights. Each one is checked on its own.
export function assertInsightList(
  value: unknown,
): asserts value is { insights: unknown[] } {
  if (
    !object(value) ||
    !Array.isArray(value.insights) ||
    value.insights.length > 50
  )
    throw new Error("Invalid insights response");
}
export function assertInsight(value: unknown): asserts value is DailyInsight {
  if (!insightValid(value)) throw new Error("Invalid insight response");
}
