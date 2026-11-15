import { object } from "./validation.js";

// The engine's version and each model's job and state (app_status, contracts/README.md 2.2;
// docs/models.md), for Sources & settings. States and counts only. The facade serves it as
// GET /api/status: available false in demo mode, with an engine older than app_status, or
// when the call failed.
export type ModelJob = "embeddings" | "titles";
export type ModelState =
  | "on"
  | "off"
  | "starting"
  | "downloading"
  | "download-failed"
  | "unverified"
  | "wrong-model"
  | "failing";
export interface ModelIndex {
  passages: number;
  indexing: boolean;
  pending: number | null;
  pausedUntil: string | null;
}
export interface ModelEntry {
  job: ModelJob;
  // map: the model whose vectors make the map's similar links (graph.similarity.model).
  role: "live" | "shadow" | "map";
  name: string | null;
  id: string | null;
  state: ModelState;
  index: ModelIndex | null;
  failure: { kind: string; at: string } | null;
}
export interface EngineStatus {
  version: string;
  schema: number;
  models: ModelEntry[];
}
export type StatusAnswer =
  | ({ mode: "live"; available: true } & EngineStatus)
  | { mode: "demo" | "live"; available: false };

const states: readonly ModelState[] = [
  "on",
  "off",
  "starting",
  "downloading",
  "download-failed",
  "unverified",
  "wrong-model",
  "failing",
];
const count = (v: unknown): v is number =>
  typeof v === "number" && Number.isInteger(v) && v >= 0;
const instant = (v: unknown): v is string =>
  typeof v === "string" &&
  v.length <= 40 &&
  /(?:Z|[+-]\d\d:\d\d)$/.test(v) &&
  Number.isFinite(Date.parse(v));
const label = (v: unknown, max = 100): v is string =>
  typeof v === "string" && v.length >= 1 && v.length <= max;
const kinds = /^(timeout|unreachable|too-large|unreadable|http-[0-9]{3})$/;

function cleanIndex(v: unknown): ModelIndex | null | undefined {
  if (v === null) return null;
  if (
    !object(v) ||
    !count(v.passages) ||
    typeof v.indexing !== "boolean" ||
    !(v.pending === null || count(v.pending)) ||
    !(v.pausedUntil === null || instant(v.pausedUntil))
  )
    return undefined;
  return {
    passages: v.passages,
    indexing: v.indexing,
    pending: v.pending as number | null,
    pausedUntil: v.pausedUntil as string | null,
  };
}

function cleanModel(v: unknown): ModelEntry | null {
  if (
    !object(v) ||
    (v.job !== "embeddings" && v.job !== "titles") ||
    (v.role !== "live" && v.role !== "shadow" && v.role !== "map") ||
    !(v.name === null || label(v.name)) ||
    !(v.id === null || label(v.id)) ||
    !states.includes(v.state as ModelState)
  )
    return null;
  const index = cleanIndex(v.index);
  if (index === undefined) return null;
  let failure: ModelEntry["failure"] = null;
  if (v.failure !== null) {
    if (
      !object(v.failure) ||
      typeof v.failure.kind !== "string" ||
      !kinds.test(v.failure.kind) ||
      !instant(v.failure.at)
    )
      return null;
    failure = { kind: v.failure.kind, at: v.failure.at };
  }
  return {
    job: v.job,
    role: v.role,
    name: v.name as string | null,
    id: v.id as string | null,
    state: v.state as ModelState,
    index,
    failure,
  };
}

// The engine's answer rebuilt from the contract's fields alone, or null. A model entry the app
// cannot read is left out; at most eight are kept.
export function cleanStatus(value: unknown): EngineStatus | null {
  if (
    !object(value) ||
    typeof value.version !== "string" ||
    !/^\d{1,5}\.\d{1,5}\.\d{1,5}$/.test(value.version) ||
    !count(value.schema) ||
    !Array.isArray(value.models) ||
    value.models.length > 8
  )
    return null;
  return {
    version: value.version,
    schema: value.schema,
    models: value.models
      .map(cleanModel)
      .filter((m): m is ModelEntry => m !== null),
  };
}

// The facade's answer as the browser reads it; anything else is "unavailable".
export function cleanStatusAnswer(value: unknown): StatusAnswer {
  if (object(value) && value.available === true && value.mode === "live") {
    const status = cleanStatus(value);
    if (status) return { mode: "live", available: true, ...status };
  }
  return {
    mode: object(value) && value.mode === "demo" ? "demo" : "live",
    available: false,
  };
}

// What a model does, in a few words. The live model draws the similar links unless a map
// model among `models` is on.
export function modelJob(model: ModelEntry, models: readonly ModelEntry[] = []) {
  if (model.job === "titles") return "Star titles";
  if (model.role === "map") return "Similar links on the map";
  if (model.role === "shadow") return "Search, being measured (not searched yet)";
  return models.some((m) => m.role === "map" && m.state === "on")
    ? "Search"
    : "Search and similar links";
}
// A model's name as people know it.
export function modelName(model: ModelEntry) {
  if (model.job === "titles") return model.name ?? "Titles model";
  return model.name === "minilm"
    ? "MiniLM"
    : model.name === "bge-small"
      ? "bge-small"
      : (model.name ?? "Embeddings model");
}
export const stateNames: Record<ModelState, string> = {
  on: "On",
  off: "Off",
  starting: "Starting",
  downloading: "Downloading",
  "download-failed": "Download failed",
  unverified: "Files not verified",
  "wrong-model": "Wrong model",
  failing: "Failing",
};
// The state's consequence, when the model isn't simply on.
export function stateNote(model: ModelEntry): string | null {
  if (model.job === "titles") {
    if (model.state === "off") return "Stars keep their first words as titles.";
    if (model.state === "failing")
      return `Stopped on ${failureNames[model.failure?.kind ?? ""] ?? model.failure?.kind ?? "a failure"}; stars keep their first words until a rebuild titles them.`;
    return null;
  }
  if (model.role === "shadow") return null;
  if (model.state === "on") return null;
  if (model.role === "map")
    return model.state === "off"
      ? null
      : "Similar links use the search model until it loads.";
  return model.state === "starting" || model.state === "downloading"
    ? "Search matches words only until it loads."
    : "Search matches words only.";
}
const failureNames: Record<string, string> = {
  timeout: "a timeout",
  unreachable: "no connection to the titles service",
  "too-large": "an answer too large",
  unreadable: "an unreadable answer",
  "http-404": "a model the service doesn't have",
};
// "1,204 passages · 3 left to index", or the job off, paused or up to date.
export function indexLine(index: ModelIndex, now = Date.now()) {
  const passages = `${index.passages.toLocaleString("en-US")} passage${index.passages === 1 ? "" : "s"}`;
  if (!index.indexing) return passages;
  const until = index.pausedUntil ? Date.parse(index.pausedUntil) : NaN;
  if (Number.isFinite(until) && until > now)
    return `${passages} · indexing paused until ${new Date(until).toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit" })}`;
  return index.pending
    ? `${passages} · ${index.pending.toLocaleString("en-US")} left to index`
    : `${passages} · up to date`;
}
