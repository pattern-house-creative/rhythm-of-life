import type { GraphEdge, GraphNode, GraphPage } from "./knowledge.js";
import { readable } from "./knowledge.js";
import { catalogProvider } from "./instance.js";
import { cleanFocus, cleanLayout } from "./parts.js";
import type { Layout, MapFocus } from "./parts.js";
import type { FocusedPage } from "./mapFocus.js";
import { assertGraphPage, object } from "./validation.js";

// Living dashboards. An AI client writes each one over MCP, a page
// plus the feeds it reads, and the engine saves it in versions; the app shows
// it as a star linked to every star it reads, runs the page in a sandboxed
// frame and hands it fresh feed data. Every field is untrusted source data.
// A dashboard whose feeds read memories by filter (a memories feed) may name
// no star; it sits beside the project stars those feeds name, or, naming none,
// is a star of its own (withDashboards, docs/dev/screens.md 10.12).
export interface DashboardStarRef {
  graphId: string;
  title: string;
}
export interface DashboardVersionNote {
  version: number;
  note: string;
  restoredFrom: number | null;
  savedAt: string;
}
// The ambient screen's corners, each showing its stat card or a dashboard's.
export const corners = ["main", "recovery", "training", "changed"] as const;
export type Corner = (typeof corners)[number];
// Their names in Sources & settings.
export const cornerNames: Record<Corner, string> = {
  main: "Main",
  recovery: "Recovery",
  training: "Training",
  changed: "Focus",
};
// The corners showing a dashboard's card, by corner. `own`, only for a viewer session with the
// engine's memory.scopes on (migration 032): the corners that are its person's own choice (the
// stat card included), every other one showing the household's; without it every corner is the
// reader's own, as the household view's always are.
export type DashboardCards = Partial<Record<Corner, string>> & {
  own?: Corner[];
};
// Chosen for a corner, drops a viewer session's own choice there, so it shows the household's
// again (migration 032): what a screen's Undo puts back where the corner had no own choice.
export const followHousehold = "00000000-0000-0000-0000-000000000000";
// What a version may write, declared when it is saved: an engine log, cells
// in a Google Sheet, or a Google Doc's section, table cell or end.
export interface DashboardWrite {
  name: string;
  kind: "log" | "sheet" | "doc";
  log?: string;
  subject?: string;
  star?: string;
  range?: string;
  appendTo?: string;
  section?: string;
  table?: number;
  append?: boolean;
}
// How a version is arranged (docs/dev/screens.md 10.7): its layout, null for
// the page alone (never arranged, or a layout this app can't draw, which then
// isn't read whole either); whether it was read whole, which is when the app
// arranges it; its layout revision, 0 for the layout the version was saved
// with; and a revision's note and when it was saved. An engine from before
// piece 3 sends none of them, which reads as the page alone at revision 0.
export interface DashboardArrangement {
  layout: Layout | null;
  layoutWhole: boolean;
  layoutRevision: number;
  layoutNote: string | null;
  layoutSavedAt: string | null;
}
export const unarranged: DashboardArrangement = {
  layout: null,
  layoutWhole: true,
  layoutRevision: 0,
  layoutNote: null,
  layoutSavedAt: null,
};
export interface DashboardSummary extends DashboardArrangement {
  id: string;
  version: number;
  latest: number;
  title: string;
  description: string;
  stars: DashboardStarRef[];
  // Only for a version that names no star, from the engine's list: the project
  // stars its memories feeds name, which it sits beside on the sky (none: an
  // engine from before, or feeds that name no project).
  projects?: DashboardStarRef[];
  note: string;
  restoredFrom: number | null;
  savedAt: string;
  createdAt: string;
  updatedAt: string;
  versions: DashboardVersionNote[];
  writes: DashboardWrite[];
  // Someone in the household allowed edits for exactly this version's writes.
  editsAllowed: boolean;
  corners: Corner[];
}
export type FeedKind = "data" | "contents" | "links" | "log" | "memories";
export interface DashboardFeed {
  name: string;
  kind: FeedKind;
  // Every kind but log and memories names a star.
  star?: string;
  days?: number;
  by?: "reading" | "day";
  metrics?: string[];
  limit?: number;
  log?: string;
  subject?: string;
  // A memories feed's filters, as the engine stored them; the app only
  // passes them on.
  where?: Record<string, unknown>;
  fullText?: boolean;
}
// One version, with its page and the feeds it reads.
export interface Dashboard extends DashboardSummary {
  feeds: DashboardFeed[];
  html: string;
}
// What one feed holds now. The page reads the rest of its fields; the app
// only shows each feed's status beside the frame.
export interface FeedPayload {
  kind: FeedKind;
  // A log or memories feed has none.
  star?: { graphId: string; title: string | null };
  status: "ok" | "unavailable";
  reason?: string;
  asOf: string;
  [field: string]: unknown;
}
// The feed read names the version it read, and from piece 3 how that version
// is arranged now, so a screen sees another screen's change at its next read.
export interface DashboardData {
  asOf: string;
  feeds: Record<string, FeedPayload>;
  dashboard: { id: string; version: number } & Partial<DashboardArrangement>;
}
// One write as the history keeps it.
export interface DashboardWriteRecord {
  id: string;
  dashboard: string;
  version: number;
  name: string;
  kind: DashboardWrite["kind"];
  summary: string;
  writtenAt: string;
  undoneAt: string | null;
}
export type DashboardWriteAnswer =
  { ok: true; write: DashboardWriteRecord } | { ok: false; reason: string };

const uuid = (v: unknown): v is string =>
  typeof v === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
const text = (v: unknown, max: number, min = 0): v is string =>
  typeof v === "string" && v.length >= min && v.length <= max;
const count = (v: unknown, max = 10000): v is number =>
  Number.isInteger(v) && (v as number) >= 1 && (v as number) <= max;
const time = (v: unknown): v is string =>
  text(v, 40, 1) && Number.isFinite(Date.parse(v));
const feedName = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][A-Za-z0-9_]{0,39}$/.test(v);
const kinds: FeedKind[] = ["data", "contents", "links", "log", "memories"];
const logName = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][a-z0-9-]{0,39}$/.test(v);
// A log's subject: a person id from the install's settings, or "household". The engine
// checks it against its own people; here it only has to be well formed.
const subject = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][a-z0-9-]{0,31}$/.test(v);
export const dashboardIdValid = uuid;

function cleanNote(v: unknown): DashboardVersionNote | null {
  if (
    !object(v) ||
    !count(v.version) ||
    !text(v.note, 500, 1) ||
    !(v.restoredFrom === null || count(v.restoredFrom)) ||
    !time(v.savedAt)
  )
    return null;
  return {
    version: v.version,
    note: v.note,
    restoredFrom: v.restoredFrom,
    savedAt: v.savedAt,
  };
}
// How a version is arranged, from the engine's layout, layoutRevision,
// layoutNote and layoutSavedAt. A layout that can't be drawn is the page
// alone, and nothing is arranged.
function cleanArrangement(
  v: Record<string, unknown>,
  id: string,
): DashboardArrangement {
  if (v.layout === undefined && v.layoutRevision === undefined)
    return unarranged;
  const read =
    v.layout === null
      ? { layout: null, whole: true }
      : cleanLayout(v.layout, id);
  const revision = Number.isInteger(v.layoutRevision)
    ? (v.layoutRevision as number)
    : -1;
  if (!read || revision < 0 || revision > 1_000_000)
    return { ...unarranged, layoutWhole: false };
  return {
    layout: read.layout,
    // The facade passes on the layout it read, without what it couldn't, and says so: what
    // the browser reads again stays not whole.
    layoutWhole: read.whole && v.layoutWhole !== false,
    layoutRevision: revision,
    layoutNote: revision && text(v.layoutNote, 500, 1) ? v.layoutNote : null,
    layoutSavedAt: revision && time(v.layoutSavedAt) ? v.layoutSavedAt : null,
  };
}
// A dashboard's version as the engine describes it, rebuilt from the contract's
// fields alone, or null.
export function cleanSummary(v: unknown): DashboardSummary | null {
  if (
    !object(v) ||
    !uuid(v.id) ||
    !count(v.version) ||
    !count(v.latest) ||
    v.version > v.latest ||
    !text(v.title, 120, 1) ||
    !text(v.description, 600) ||
    !text(v.note, 500, 1) ||
    !(v.restoredFrom === null || count(v.restoredFrom)) ||
    !time(v.savedAt) ||
    !time(v.createdAt) ||
    !time(v.updatedAt) ||
    !Array.isArray(v.stars) ||
    v.stars.length > 12 ||
    !Array.isArray(v.versions) ||
    v.versions.length > 100
  )
    return null;
  const stars = cleanStarRefs(v.stars);
  // A version that names no star may come with the project stars it sits
  // beside (an engine from before sends none); one that names stars never does.
  const projects =
    v.projects === undefined
      ? undefined
      : v.stars.length === 0
        ? cleanStarRefs(v.projects)
        : null;
  const versions = v.versions.map(cleanNote);
  if (!stars || projects === null || versions.some((n) => !n)) return null;
  // Before the engine knew writes and cards, a dashboard had neither.
  const writes = v.writes === undefined ? [] : cleanWrites(v.writes);
  const shown =
    v.corners === undefined
      ? []
      : Array.isArray(v.corners) &&
          v.corners.every((c) => corners.includes(c as Corner))
        ? (v.corners as Corner[])
        : null;
  if (
    !writes ||
    !shown ||
    !(v.editsAllowed === undefined || typeof v.editsAllowed === "boolean")
  )
    return null;
  return {
    id: v.id.toLowerCase(),
    version: v.version,
    latest: v.latest,
    title: v.title,
    description: v.description,
    stars,
    ...(projects ? { projects } : {}),
    note: v.note,
    restoredFrom: v.restoredFrom,
    savedAt: v.savedAt,
    createdAt: v.createdAt,
    updatedAt: v.updatedAt,
    versions: versions as DashboardVersionNote[],
    writes,
    editsAllowed: v.editsAllowed === true && writes.length > 0,
    corners: shown,
    ...cleanArrangement(v, v.id.toLowerCase()),
  };
}
// At most 12 stars, each with its graph id and title, or null.
function cleanStarRefs(value: unknown): DashboardStarRef[] | null {
  if (!Array.isArray(value) || value.length > 12) return null;
  const refs = value.flatMap((s) =>
    object(s) && text(s.graphId, 1000, 1) && text(s.title, 1000)
      ? [{ graphId: s.graphId, title: s.title }]
      : [],
  );
  return refs.length === value.length ? refs : null;
}
function cleanWrites(value: unknown): DashboardWrite[] | null {
  if (!Array.isArray(value) || value.length > 12) return null;
  const writes = value.map((w): DashboardWrite | null => {
    if (
      !object(w) ||
      !feedName(w.name) ||
      (w.kind !== "log" && w.kind !== "sheet" && w.kind !== "doc")
    )
      return null;
    if (w.kind === "log")
      return logName(w.log) && subject(w.subject)
        ? {
            name: w.name,
            kind: "log",
            log: w.log,
            subject: w.subject as string,
          }
        : null;
    if (!text(w.star, 1000, 1)) return null;
    return {
      name: w.name,
      kind: w.kind,
      star: w.star,
      ...(text(w.range, 300, 1) ? { range: w.range } : {}),
      ...(text(w.appendTo, 100, 1) ? { appendTo: w.appendTo } : {}),
      ...(text(w.section, 200, 1) ? { section: w.section } : {}),
      ...(Number.isInteger(w.table) &&
      (w.table as number) >= 0 &&
      (w.table as number) <= 100
        ? { table: w.table as number }
        : {}),
      ...(w.append === true ? { append: true } : {}),
    };
  });
  return writes.every(Boolean) ? (writes as DashboardWrite[]) : null;
}
// The engine lists at most this many dashboards, the most recently changed
// first, so a list this long may leave older ones out.
export const listedMax = 100;
// The list response, failing closed: a malformed envelope is no dashboards,
// and a malformed dashboard is left out.
export function cleanSummaries(value: unknown): DashboardSummary[] {
  if (
    !object(value) ||
    !Array.isArray(value.dashboards) ||
    value.dashboards.length > listedMax
  )
    return [];
  const seen = new Set<string>();
  return value.dashboards.flatMap((d) => {
    const clean = cleanSummary(d);
    if (!clean || seen.has(clean.id)) return [];
    seen.add(clean.id);
    return [clean];
  });
}
// The list as the engine's answer: a malformed envelope is a failed read,
// never "no dashboards", since a dashboard missing from the list reads as one
// that was deleted (a screen's Home among them).
export function listedSummaries(value: unknown): DashboardSummary[] {
  if (
    !object(value) ||
    !Array.isArray(value.dashboards) ||
    value.dashboards.length > listedMax
  )
    throw new Error("Invalid dashboards response");
  return cleanSummaries(value);
}
function cleanFeed(v: unknown): DashboardFeed | null {
  if (!object(v) || !feedName(v.name) || !kinds.includes(v.kind as FeedKind))
    return null;
  if (v.kind === "log")
    return logName(v.log) && subject(v.subject)
      ? {
          name: v.name,
          kind: "log",
          log: v.log,
          subject: v.subject as string,
          ...(count(v.days, 3650) ? { days: v.days } : {}),
        }
      : null;
  if (v.kind === "memories")
    return v.star === undefined &&
      (v.where === undefined || object(v.where)) &&
      (v.fullText === undefined || typeof v.fullText === "boolean") &&
      (v.limit === undefined || count(v.limit, 1000))
      ? {
          name: v.name,
          kind: "memories",
          ...(object(v.where) ? { where: v.where } : {}),
          ...(typeof v.fullText === "boolean" ? { fullText: v.fullText } : {}),
          ...(count(v.limit, 1000) ? { limit: v.limit } : {}),
        }
      : null;
  if (!text(v.star, 1000, 1)) return null;
  return {
    name: v.name,
    kind: v.kind as FeedKind,
    star: v.star,
    ...(count(v.days, 3650) ? { days: v.days } : {}),
    ...(v.by === "reading" || v.by === "day" ? { by: v.by } : {}),
    ...(Array.isArray(v.metrics) &&
    v.metrics.length <= 40 &&
    v.metrics.every((m) => text(m, 100, 1))
      ? { metrics: v.metrics as string[] }
      : {}),
    ...(count(v.limit, 60) ? { limit: v.limit } : {}),
  };
}
// One version with its page, from `{dashboard}`, or null.
export function cleanDashboard(value: unknown): Dashboard | null {
  const v = object(value) ? value.dashboard : null;
  const summary = cleanSummary(v);
  if (
    !summary ||
    !object(v) ||
    !text(v.html, 300_000, 1) ||
    !Array.isArray(v.feeds) ||
    v.feeds.length < 1 ||
    v.feeds.length > 12
  )
    return null;
  const feeds = v.feeds.map(cleanFeed);
  if (feeds.some((f) => !f)) return null;
  return { ...summary, feeds: feeds as DashboardFeed[], html: v.html };
}
// What a version's feeds hold now: each payload passes to the page as it came
// (the frame can reach nothing else); the app checks only the fields it shows.
export function cleanDashboardData(value: unknown): DashboardData | null {
  if (
    !object(value) ||
    !time(value.asOf) ||
    !object(value.feeds) ||
    !object(value.dashboard) ||
    !uuid(value.dashboard.id) ||
    !count(value.dashboard.version)
  )
    return null;
  const entries = Object.entries(value.feeds);
  if (entries.length > 12) return null;
  const feeds: Record<string, FeedPayload> = {};
  for (const [name, f] of entries) {
    if (
      !feedName(name) ||
      !object(f) ||
      !kinds.includes(f.kind as FeedKind) ||
      (f.status !== "ok" && f.status !== "unavailable") ||
      (f.kind === "log"
        ? f.star !== undefined || !logName(f.log)
        : f.kind === "memories"
          ? f.star !== undefined
          : !object(f.star) ||
            !text(f.star.graphId, 1000, 1) ||
            !(f.star.title === null || text(f.star.title, 1000))) ||
      !time(f.asOf) ||
      (f.reason !== undefined && !text(f.reason, 300))
    )
      return null;
    feeds[name] = f as FeedPayload;
  }
  const id = value.dashboard.id.toLowerCase();
  return {
    asOf: value.asOf,
    feeds,
    dashboard: {
      id,
      version: value.dashboard.version,
      ...(value.dashboard.layout !== undefined ||
      value.dashboard.layoutRevision !== undefined
        ? cleanArrangement(value.dashboard, id)
        : {}),
    },
  };
}

// The engine's answer to saving an arrangement (app_dashboards {id, version,
// layout, base, note}, 10.7): saved, with the layout and its revision;
// refused, with why, and `transient` when trying again later may work (no
// answer came back, or the change didn't reach the engine, or the engine
// couldn't take it right now); or refused because the layout changed since
// `base`, with the layout and revision to apply the change to once more. Null
// when it is none of them.
export type LayoutAnswer =
  | ({ ok: true } & Omit<DashboardArrangement, "layoutNote" | "layoutSavedAt">)
  | { ok: false; reason: string; transient?: true }
  | ({ ok: false; reason: string } & Omit<
      DashboardArrangement,
      "layoutNote" | "layoutSavedAt"
    >);
export function cleanLayoutAnswer(
  value: unknown,
  id: string,
): LayoutAnswer | null {
  if (!object(value) || typeof value.ok !== "boolean") return null;
  const current =
    value.layout !== undefined || value.layoutRevision !== undefined
      ? cleanArrangement(value, id.toLowerCase())
      : null;
  const arranged = current && {
    layout: current.layout,
    layoutWhole: current.layoutWhole,
    layoutRevision: current.layoutRevision,
  };
  if (value.ok)
    return arranged && current.layoutWhole && !Object.hasOwn(value, "reason")
      ? { ok: true, ...arranged }
      : null;
  if (!text(value.reason, 400, 1)) return null;
  if (value.transient !== undefined && (value.transient !== true || arranged))
    return null;
  return arranged
    ? { ok: false, reason: value.reason, ...arranged }
    : value.transient
      ? { ok: false, reason: value.reason, transient: true }
      : { ok: false, reason: value.reason };
}

// A map part's focused sky (app_dashboards {id, version?, map, cursor?}, 10.4):
// one page of the readable map's stars that meet the part's focus, ranked as
// the overview ranks them, the links between them, and `matched`, the count of
// every match. `focus` is as the engine keeps it (cleanFocus).
export interface DashboardMap extends FocusedPage {
  part: string;
  focus: MapFocus | null;
}
// The focused read as the engine answers it, `{map: {...}}`, for the part
// asked for, with only readable stars and the links between two of them; null
// when it isn't one.
export function cleanDashboardMap(
  value: unknown,
  part: string,
): DashboardMap | null {
  const map = object(value) && object(value.map) ? value.map : null;
  if (
    !map ||
    map.part !== part ||
    !Number.isSafeInteger(map.matched) ||
    (map.matched as number) < 0
  )
    return null;
  const page = {
    nodes: map.nodes,
    edges: map.edges,
    nextCursor: map.nextCursor,
    completeness: map.completeness,
    issues: map.issues,
  };
  try {
    assertGraphPage(page);
  } catch {
    return null;
  }
  const nodes = page.nodes.filter(readable);
  const kept = new Set(nodes.map((n) => n.id));
  return {
    part,
    focus: cleanFocus(map.focus).focus,
    matched: map.matched as number,
    nodes,
    edges: page.edges.filter((e) => kept.has(e.source) && kept.has(e.target)),
    nextCursor:
      typeof page.nextCursor === "string" && /^o:\d{1,9}$/.test(page.nextCursor)
        ? page.nextCursor
        : null,
    completeness: page.completeness,
    issues: page.issues,
  };
}

// The corners showing a dashboard's card, from `{cards}`; a malformed answer is
// none, and a corner naming no dashboard in `known` (when given) is left out.
// `own` is kept when the engine sent it, with the corners it knows.
export function cleanCards(
  value: unknown,
  known?: Set<string>,
): DashboardCards {
  const cards: DashboardCards = {};
  if (!object(value) || !object(value.cards)) return cards;
  for (const corner of corners) {
    const id = value.cards[corner];
    if (uuid(id) && (!known || known.has(id.toLowerCase())))
      cards[corner] = id.toLowerCase();
  }
  const own = value.cards.own;
  if (Array.isArray(own)) cards.own = corners.filter((c) => own.includes(c));
  return cards;
}
// The stars pinned to the sky, from `{pins}`: at most 24 graph ids, or none.
export function cleanPins(value: unknown): string[] {
  if (!object(value) || !Array.isArray(value.pins) || value.pins.length > 24)
    return [];
  return [...new Set(value.pins.filter((p): p is string => text(p, 1000, 1)))];
}
// The engine's answer to a write or an undo, or null.
export function cleanWriteAnswer(value: unknown): DashboardWriteAnswer | null {
  if (!object(value)) return null;
  if (value.ok === false)
    return text(value.reason, 400, 1)
      ? { ok: false, reason: value.reason }
      : null;
  const w = value.write;
  if (
    value.ok !== true ||
    !object(w) ||
    !uuid(w.id) ||
    !uuid(w.dashboard) ||
    !count(w.version) ||
    !feedName(w.name) ||
    (w.kind !== "log" && w.kind !== "sheet" && w.kind !== "doc") ||
    !text(w.summary, 300, 1) ||
    !time(w.writtenAt) ||
    !(w.undoneAt === null || time(w.undoneAt))
  )
    return null;
  return {
    ok: true,
    write: {
      id: w.id.toLowerCase(),
      dashboard: w.dashboard.toLowerCase(),
      version: w.version,
      name: w.name,
      kind: w.kind,
      summary: w.summary,
      writtenAt: w.writtenAt,
      undoneAt: w.undoneAt,
    },
  };
}

// A dashboard on the sky: its own star, linked to every star it reads that is
// on the map. One that names no star reads memories by filter: it is linked to
// the project stars its feeds name instead, and with none on the map it is a
// star with no links, which the sky sets on its outer ring as it does every
// such star (graph.ts createGraph). It takes the engine's catalog provider id,
// like the engine's other stars.
export const dashboardStarId = (id: string) =>
  `${catalogProvider()}:dashboard:${id}`;
// The sky sees only a star's id, type and source.
export const isDashboardId = (id: string) =>
  id.startsWith(`${catalogProvider()}:dashboard:`);
export const isDashboardStar = (n: GraphNode) =>
  n.resource.identity.provider === catalogProvider() &&
  n.resource.identity.account === "dashboard";
export const dashboardOf = (n: GraphNode) =>
  isDashboardStar(n) ? n.resource.identity.externalId : null;
export const drawsOn = (e: GraphEdge) => e.type === "draws_on";

export function withDashboards(
  page: GraphPage,
  dashboards: DashboardSummary[],
  demo = false,
  kept: string | null = null,
): GraphPage {
  if (!dashboards.length) return page;
  const known = new Set(page.nodes.map((n) => n.id));
  const retrieved = new Date().toISOString();
  const provider = catalogProvider();
  const nodes: GraphNode[] = [];
  const edges: GraphEdge[] = [];
  for (const d of dashboards) {
    const id = dashboardStarId(d.id);
    if (known.has(id)) continue;
    // What it reads on the map: the stars it names, or for one that names
    // none, the project stars its memories feeds name.
    const named = d.stars.length > 0;
    const beside = (named ? d.stars : (d.projects ?? [])).filter((s) =>
      known.has(s.graphId),
    );
    // A dashboard that names stars joins the sky beside what it reads, so not
    // before (or after) any of its stars is on the map. One that names none
    // waits only for the map, so it never shows alone while the sky loads. One
    // a screen was asked to open (`kept`) joins whatever is on the map.
    if (
      d.id !== kept &&
      (named ? beside.length === 0 : page.nodes.length === 0)
    )
      continue;
    nodes.push({
      id,
      title: d.title,
      type: "record",
      overview: d.description || d.note,
      provenance: {
        sourceId: provider,
        recordId: d.id,
        observedAt: d.updatedAt,
        demo,
      },
      resource: {
        identity: {
          provider,
          account: "dashboard",
          externalId: d.id,
        },
        canonicalUrl: null,
        sourceUrl: null,
        sourceModifiedAt: d.savedAt,
        retrievedAt: retrieved,
        revision: `version ${d.version}`,
        owner: "household",
        access: { scope: "shared", canRead: true, canOpen: false },
        availability: "available",
        content: null,
        representation: "original",
      },
    });
    for (const star of beside) {
      edges.push({
        id: `dashboard:${d.id}:${star.graphId}`,
        source: id,
        target: star.graphId,
        relationship: named
          ? `The dashboard “${d.title}” reads this star.`
          : `The dashboard “${d.title}” reads this project's memories.`,
        strength: 0.8,
        type: "draws_on",
        origin: { provider, account: "dashboard", externalId: d.id },
        directed: true,
        verification: "verified",
        evidence: [
          {
            resourceId: id,
            excerpt: named
              ? `Version ${d.version} of the dashboard reads this star.`
              : `Version ${d.version} of the dashboard reads this project's memories by filter.`,
            locator: "Dashboard feeds",
            retrievedAt: retrieved,
          },
        ],
        sourceModifiedAt: d.savedAt,
        retrievedAt: retrieved,
      });
    }
  }
  return {
    ...page,
    nodes: [...page.nodes, ...nodes],
    edges: [...page.edges, ...edges],
  };
}
