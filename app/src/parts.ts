import type { Corner } from "./dashboards.js";
import type { NodeType } from "./model.js";
import type { Layout as Preset, Tiles } from "./screens.js";
import { focusSummary } from "./mapFocus.js";
import { object } from "./validation.js";

// Composable layouts (docs/dev/screens.md section 10): Home and every dashboard as a grid of
// parts, 12 columns by 12 rows of cells. This is what the app, the facade and the demo share,
// as pure functions: the kinds and their smallest sizes, the rules every layout keeps, reading
// order, folding, Home's presets and its first grid, and the moves, resizes and focus changes
// of 10.6. The engine's DashboardLayout checks the same rules. A layout comes from the engine
// and is untrusted: it is checked before it is drawn, and arranged only when it was read whole.
// Nothing here imports a value from screens.ts or dashboards.ts, which import this module.

// The grid's tracks each way.
export const gridCells = 12;

export const partKinds = [
  "page",
  "map",
  "glucose",
  "recovery",
  "training",
  "changed",
  "insights",
  "card",
] as const;
export type PartKind = (typeof partKinds)[number];
export const isPartKind = (v: unknown): v is PartKind =>
  partKinds.includes(v as PartKind);

// What a map part narrows its sky to (10.4). Every member is optional, and a focus has at least
// one: a star is shown when it meets every member given (for a list, any one of its values).
export const focusTypes: readonly NodeType[] = [
  "theme",
  "project",
  "note",
  "record",
  "document",
  "memory",
  "decision",
];
export const focusLinks = [
  "verified",
  "suggested",
  "inferred",
  "unverified",
] as const;
export type FocusLink = (typeof focusLinks)[number];
export interface MapFocus {
  query?: string;
  near?: { star: string; depth: 1 | 2 };
  types?: NodeType[];
  providers?: string[];
  groups?: string[];
  owners?: string[];
  links?: FocusLink[];
}

// A rectangle of cells: its top left cell (x and y, 0-11) and its size (w and h, 1-12).
export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}
export interface Cells {
  w: number;
  h: number;
}
// One part of a dashboard's layout (10.3): a map may have a focus, a card names a dashboard.
export interface Part extends Rect {
  id: string;
  kind: PartKind;
  focus?: MapFocus | null;
  dashboard?: string;
}
// A dashboard's grid, kept with a version. Not Home's `layout` setting, which names a preset.
export interface Layout {
  parts: Part[];
}

// Home's five parts (10.5): the main sky, and the four corners, each named after its tile.
export const homeIds = [
  "map",
  "main",
  "recovery",
  "training",
  "changed",
] as const;
export type HomeId = (typeof homeIds)[number];
export interface HomePart extends Rect {
  id: HomeId;
}
// One screen's arrangement of Home, kept in its settings as `grid`. Its parts have no kind.
export interface HomeGrid {
  parts: HomePart[];
}
// At Home the map is at least 4 × 4 cells and each corner 2 × 3.
export const homeSmallest: Record<HomeId, Cells> = {
  map: { w: 4, h: 4 },
  main: { w: 2, h: 3 },
  recovery: { w: 2, h: 3 },
  training: { w: 2, h: 3 },
  changed: { w: 2, h: 3 },
};

// Each kind's smallest size, in cells (the engine checks these) and in pixels, about (they only
// decide when a dashboard folds; the app tunes them, and its tests pin them).
export interface KindSize {
  cells: Cells;
  px: { width: number; height: number };
}
export const kindSizes: Record<PartKind, KindSize> = {
  page: { cells: { w: 3, h: 3 }, px: { width: 280, height: 180 } },
  map: { cells: { w: 4, h: 4 }, px: { width: 280, height: 220 } },
  glucose: { cells: { w: 2, h: 2 }, px: { width: 200, height: 110 } },
  recovery: { cells: { w: 2, h: 2 }, px: { width: 180, height: 110 } },
  training: { cells: { w: 2, h: 2 }, px: { width: 180, height: 110 } },
  changed: { cells: { w: 2, h: 2 }, px: { width: 200, height: 110 } },
  insights: { cells: { w: 3, h: 2 }, px: { width: 260, height: 140 } },
  card: { cells: { w: 2, h: 2 }, px: { width: 200, height: 140 } },
};
// How many parts of a kind one layout may have: exactly one page, at most 2 maps and 4 cards,
// and at most one of each other kind.
export const kindMost: Record<PartKind, number> = {
  page: 1,
  map: 2,
  glucose: 1,
  recovery: 1,
  training: 1,
  changed: 1,
  insights: 1,
  card: 4,
};

// A part id: a lowercase letter, then up to 31 lowercase letters, digits or underscores.
export const partIdValid = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][a-z0-9_]{0,31}$/.test(v);
const uuid = (v: unknown): v is string =>
  typeof v === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
const whole = (v: unknown, min: number, max: number): v is number =>
  Number.isInteger(v) && (v as number) >= min && (v as number) <= max;
// A rectangle inside the grid, in whole cells.
export const rectValid = (v: Record<string, unknown>) =>
  whole(v.x, 0, gridCells - 1) &&
  whole(v.y, 0, gridCells - 1) &&
  whole(v.w, 1, gridCells) &&
  whole(v.h, 1, gridCells) &&
  (v.x as number) + (v.w as number) <= gridCells &&
  (v.y as number) + (v.h as number) <= gridCells;
// Characters, as the engine and the database count them: an emoji is one.
const characters = (text: string) => Array.from(text).length;
// Characters as the database stores JSON: jsonb's text form puts a space after each colon and
// comma, so a size checked here is never under the engine's.
export function storedSize(v: unknown): number {
  const text = (x: unknown): string =>
    Array.isArray(x)
      ? `[${x.map(text).join(", ")}]`
      : object(x)
        ? `{${Object.entries(x)
            .filter(([, value]) => value !== undefined)
            .map(([key, value]) => `${JSON.stringify(key)}: ${text(value)}`)
            .join(", ")}}`
        : (JSON.stringify(x) ?? "null");
  return characters(text(v));
}
// JSON with its keys sorted, so two values compare as the database compares them.
export function canonical(v: unknown): string {
  return JSON.stringify(v, (_, value: unknown) =>
    object(value)
      ? Object.fromEntries(
          Object.keys(value)
            .sort()
            .map((k) => [k, value[k]]),
        )
      : value,
  );
}
export const layoutMax = 8000;
export const focusMax = 1500;

// ----- Focus.

const focusMembers = [
  "query",
  "near",
  "types",
  "providers",
  "groups",
  "owners",
  "links",
] as const;
type FocusMember = (typeof focusMembers)[number];
// A list member as stored: 1 to `max` values as given, as the engine counts them, each passing
// `check` once trimmed; stored trimmed, with repeats dropped.
function list<T>(v: unknown, max: number, check: (x: unknown) => x is T) {
  if (!Array.isArray(v) || v.length < 1 || v.length > max) return undefined;
  const values = v.map((x: unknown) => (typeof x === "string" ? x.trim() : x));
  return values.every(check) ? [...new Set(values)] : undefined;
}
const textValid = (v: unknown, max: number): v is string =>
  typeof v === "string" &&
  characters(v) >= 1 &&
  characters(v) <= max &&
  !/\p{Cc}/u.test(v);
// One focus member as it is stored (trimmed, lists without repeats, `near`'s depth written
// out), or undefined when it isn't one.
function focusMember(name: FocusMember, v: unknown): unknown {
  switch (name) {
    case "query": {
      // No control characters anywhere, as the engine checks it before trimming.
      const query = typeof v === "string" && !/\p{Cc}/u.test(v) ? v.trim() : "";
      return textValid(query, 100) ? query : undefined;
    }
    case "near": {
      const star = object(v) && typeof v.star === "string" ? v.star.trim() : "";
      if (
        !object(v) ||
        Object.keys(v).some((k) => k !== "star" && k !== "depth") ||
        !textValid(star, 1000) ||
        (v.depth !== undefined && v.depth !== 1 && v.depth !== 2)
      )
        return undefined;
      return { star, depth: v.depth ?? 1 };
    }
    case "types":
      return list(v, 7, (t): t is NodeType => focusTypes.includes(t as never));
    case "providers":
      return list(
        v,
        20,
        (p): p is string =>
          typeof p === "string" && /^[a-z][a-z0-9-]{0,40}$/.test(p),
      );
    case "groups":
      return list(v, 20, (g): g is string => textValid(g, 300));
    case "owners":
      return list(
        v,
        10,
        (o): o is string =>
          typeof o === "string" && /^[a-z][a-z0-9-]{0,31}$/.test(o),
      );
    case "links":
      return list(v, 4, (l): l is FocusLink =>
        focusLinks.includes(l as FocusLink),
      );
  }
}
// A focus as a command or a save gives it (10.4): absent or null is none (null); otherwise an
// object of known members, at least one, each as 10.4 says and at most 1,500 characters as
// the database stores it (storedSize), as the engine measures it. Answers it as stored, or
// undefined when it isn't one.
export function normalFocus(v: unknown): MapFocus | null | undefined {
  if (v === undefined || v === null) return null;
  if (!object(v)) return undefined;
  const keys = Object.keys(v);
  if (
    !keys.length ||
    keys.some((k) => !focusMembers.includes(k as FocusMember))
  )
    return undefined;
  const focus: Record<string, unknown> = {};
  for (const name of focusMembers) {
    if (!Object.hasOwn(v, name)) continue;
    const value = focusMember(name, v[name]);
    if (value === undefined) return undefined;
    focus[name] = value;
  }
  return storedSize(focus) <= focusMax ? (focus as MapFocus) : undefined;
}
// A map part's focus as the engine keeps it, with whether it was read whole: a member this app
// doesn't know (a later piece's) or can't read is left out, and the focus is then not read
// whole, though the part is still focused (the engine applies every member it keeps).
export function cleanFocus(v: unknown): {
  focus: MapFocus | null;
  whole: boolean;
} {
  if (v === undefined || v === null) return { focus: null, whole: true };
  if (!object(v)) return { focus: {}, whole: false };
  const focus: Record<string, unknown> = {};
  let read = Object.keys(v).length > 0;
  for (const [name, value] of Object.entries(v)) {
    const member = focusMembers.includes(name as FocusMember)
      ? focusMember(name as FocusMember, value)
      : undefined;
    if (member === undefined) read = false;
    else {
      if (canonical(member) !== canonical(value)) read = false;
      focus[name] = member;
    }
  }
  return {
    focus: focus as MapFocus,
    whole: read && storedSize(v) <= focusMax,
  };
}
// Two focuses compared as stored; none (absent or null) is one.
export const sameFocus = (
  a: MapFocus | null | undefined,
  b: MapFocus | null | undefined,
) => canonical(a ?? null) === canonical(b ?? null);

// ----- Layouts.

// The page alone (10.7): what a version without a layout (NULL) draws, as before piece 3.
export const layoutOf = (layout: Layout | null | undefined): Layout =>
  layout ?? {
    parts: [{ id: "page", kind: "page", x: 0, y: 0, w: 12, h: 12 }],
  };
// A layout whose only part is a page covering the grid draws exactly as today, and never folds.
export const drawsAsToday = (layout: Layout | null | undefined) => {
  const parts = layoutOf(layout).parts;
  return (
    parts.length === 1 &&
    parts[0].kind === "page" &&
    parts[0].x === 0 &&
    parts[0].y === 0 &&
    parts[0].w === gridCells &&
    parts[0].h === gridCells
  );
};

// Reading order (10.2): by y, then x, then id; the top row first, left to right.
export const readingOrder = <P extends { id: string; x: number; y: number }>(
  parts: readonly P[],
): P[] =>
  [...parts].sort(
    (a, b) =>
      a.y - b.y || a.x - b.x || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0),
  );

const overlapping = (a: Rect, b: Rect) =>
  a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
// The first two parts that overlap, or null.
function overlap<P extends Rect & { id: string }>(parts: readonly P[]) {
  for (let i = 0; i < parts.length; i++)
    for (let j = i + 1; j < parts.length; j++)
      if (overlapping(parts[i], parts[j])) return [parts[i], parts[j]];
  return null;
}
const memberKeys = (kind: PartKind) => [
  "id",
  "kind",
  "x",
  "y",
  "w",
  "h",
  ...(kind === "map" ? ["focus"] : kind === "card" ? ["dashboard"] : []),
];

// What is wrong with a dashboard's layout by 10.2-10.4, in plain words, or null when it keeps
// every rule: as the engine's DashboardLayout.Problem() checks it before saving. `self` is the
// dashboard's own id, which no card may name.
export function layoutProblem(v: unknown, self?: string): string | null {
  if (
    !object(v) ||
    Object.keys(v).join() !== "parts" ||
    !Array.isArray(v.parts)
  )
    return "A layout is {parts: [...]}";
  const parts = v.parts as unknown[];
  if (parts.length < 1 || parts.length > gridCells)
    return "A layout has 1 to 12 parts";
  const placed: Part[] = [];
  for (const p of parts) {
    if (!object(p) || !partIdValid(p.id))
      return "Each part has an id: a lowercase letter, then up to 31 lowercase letters, digits or underscores";
    if (!isPartKind(p.kind)) return `The part “${p.id}” has no known kind`;
    const extra = Object.keys(p).find(
      (k) => !memberKeys(p.kind as PartKind).includes(k),
    );
    if (extra) return `The part “${p.id}” takes no “${extra.slice(0, 40)}”`;
    if (!rectValid(p)) return `The part “${p.id}” doesn't fit in the grid`;
    const smallest = kindSizes[p.kind].cells;
    if ((p.w as number) < smallest.w || (p.h as number) < smallest.h)
      return `The part “${p.id}” is smaller than a ${p.kind}'s smallest size, ${smallest.w} × ${smallest.h}`;
    if (p.kind === "map" && normalFocus(p.focus) === undefined)
      return `The focus of “${p.id}” isn't one`;
    if (
      p.kind === "card" &&
      (!uuid(p.dashboard) || p.dashboard.toLowerCase() === self?.toLowerCase())
    )
      return `The card “${p.id}” names no other dashboard`;
    placed.push(p as unknown as Part);
  }
  const ids = placed.map((p) => p.id);
  const twice = ids.find((id, i) => ids.indexOf(id) !== i);
  if (twice) return `Two parts have the id “${twice}”`;
  const both = overlap(placed);
  if (both) return `The parts “${both[0].id}” and “${both[1].id}” overlap`;
  for (const kind of partKinds) {
    const count = placed.filter((p) => p.kind === kind).length;
    if (kind === "page" && count !== 1)
      return "A layout has exactly one page part";
    if (count > kindMost[kind])
      return `A layout has at most ${kindMost[kind]} ${kind} part${kindMost[kind] > 1 ? "s" : ""}`;
  }
  return storedSize(v) > layoutMax
    ? "A layout is at most 8,000 characters"
    : null;
}

// A dashboard's layout as the engine answers it, with whether it was read whole: every part
// kind and member known, every rule kept. A part of a kind this app doesn't know (a later
// piece's), or a card naming no other dashboard, is left out, so its cell stays empty; a focus
// member it doesn't know leaves the map focused. Only a layout read whole is arranged (10.9).
// Null when it can't be drawn at all (not a grid of parts, no single page, parts overlapping
// or outside the grid): the page alone is drawn instead, and nothing is arranged.
export function cleanLayout(
  v: unknown,
  self?: string,
): { layout: Layout; whole: boolean } | null {
  if (
    !object(v) ||
    !Array.isArray(v.parts) ||
    v.parts.length < 1 ||
    v.parts.length > gridCells
  )
    return null;
  // A member beside the parts is a later piece's.
  let read = Object.keys(v).length === 1;
  const placed: (Rect & { id: string })[] = [],
    parts: Part[] = [];
  for (const p of v.parts) {
    if (
      !object(p) ||
      !partIdValid(p.id) ||
      typeof p.kind !== "string" ||
      !rectValid(p)
    )
      return null;
    const rect = {
      x: p.x as number,
      y: p.y as number,
      w: p.w as number,
      h: p.h as number,
    };
    placed.push({ id: p.id, ...rect });
    if (!isPartKind(p.kind)) {
      read = false;
      continue;
    }
    if (Object.keys(p).some((k) => !memberKeys(p.kind as PartKind).includes(k)))
      read = false;
    const part: Part = { id: p.id, kind: p.kind, ...rect };
    if (p.kind === "map" && p.focus !== undefined) {
      const focus = cleanFocus(p.focus);
      if (!focus.whole) read = false;
      part.focus = focus.focus;
    }
    if (p.kind === "card") {
      if (
        !uuid(p.dashboard) ||
        p.dashboard.toLowerCase() === self?.toLowerCase()
      ) {
        read = false;
        continue;
      }
      part.dashboard = p.dashboard;
    }
    parts.push(part);
  }
  const ids = placed.map((p) => p.id);
  if (
    new Set(ids).size !== ids.length ||
    overlap(placed) ||
    parts.filter((p) => p.kind === "page").length !== 1
  )
    return null;
  const layout = { parts };
  if (read && layoutProblem(layout, self) !== null) read = false;
  if (storedSize(v) > layoutMax) read = false;
  return { layout, whole: read };
}

// Whether a layout drawn as saved can be arranged by hand (10.9): read whole, with more than
// one part or a part with room to grow.
export const arrangeable = (layout: Layout, read: boolean) =>
  read &&
  (layout.parts.length > 1 ||
    layout.parts.some((p) => p.w < gridCells || p.h < gridCells));

// Whether `after` is only an arrangement of `before` (10.7), as the engine checks every
// layout the app role saves: the same part ids, each with the same kind and, for a card, the
// same dashboard; only x, y, w, h and a map's focus may differ. A reason, or null when it is
// one. `before` null is the page alone.
export function arrangementOnly(
  before: unknown,
  after: unknown,
): string | null {
  const was = before ?? layoutOf(null);
  const other = "Only how the parts are arranged may change";
  if (
    !object(was) ||
    !object(after) ||
    !Array.isArray(was.parts) ||
    !Array.isArray(after.parts)
  )
    return other;
  const rest = (v: Record<string, unknown>, keys: string[]) =>
    canonical(
      Object.fromEntries(Object.entries(v).filter(([k]) => !keys.includes(k))),
    );
  if (rest(was, ["parts"]) !== rest(after, ["parts"])) return other;
  const old = new Map<unknown, Record<string, unknown>>();
  for (const p of was.parts) if (object(p)) old.set(p.id, p);
  if (after.parts.length !== was.parts.length || old.size !== was.parts.length)
    return "A part was added or removed";
  for (const p of after.parts) {
    const q = object(p) ? old.get(p.id) : undefined;
    if (!object(p) || !q) return "A part was added or removed";
    old.delete(p.id);
    const id = String(p.id);
    if (p.kind !== q.kind) return `The part “${id}” changed its kind`;
    if (q.kind === "card" && p.dashboard !== q.dashboard)
      return `The card “${id}” names another dashboard`;
    const moving = ["x", "y", "w", "h", ...(q.kind === "map" ? ["focus"] : [])];
    if (rest(p, moving) !== rest(q, moving))
      return `Only the place, size or focus of “${id}” may change`;
  }
  return null;
}

// ----- Folding.

export interface Size {
  width: number;
  height: number;
}
// The gap between two tracks, in pixels: one for both ways, or each its own.
export type Gap = number | { column: number; row: number };
const gapsOf = (gap: Gap) =>
  typeof gap === "number" ? { column: gap, row: gap } : gap;
// A part's size in pixels in a box drawn as saved: its cells and the gaps between them.
export function partPixels(r: Rect, box: Size, gap: Gap): Size {
  const { column, row } = gapsOf(gap);
  const track = (length: number, g: number) =>
    (length - (gridCells - 1) * g) / gridCells;
  return {
    width: r.w * track(box.width, column) + (r.w - 1) * column,
    height: r.h * track(box.height, row) + (r.h - 1) * row,
  };
}
// How a dashboard's grid is drawn in its box (10.2): as saved, or folded into two columns or
// one. Home never folds.
export type Fold = "saved" | "two" | "one";
// Below this width a fold has one column.
export const foldWidth = 560;
// A part 7 or more columns wide spans both columns of a two-column fold.
export const foldSpanBoth = 7;
// As saved while every part gets at least its kind's smallest size in pixels; otherwise two
// columns in a box at least 560 px wide, one column in a narrower one. A layout whose only
// part is a page covering the grid never folds.
export function foldOf(layout: Layout, box: Size, gap: Gap): Fold {
  if (drawsAsToday(layout)) return "saved";
  const fits = layout.parts.every((p) => {
    const px = partPixels(p, box, gap),
      smallest = kindSizes[p.kind].px;
    return px.width >= smallest.width && px.height >= smallest.height;
  });
  return fits ? "saved" : box.width >= foldWidth ? "two" : "one";
}
// A kind's height in a fold, in pixels (10.3), or null for its content's own: the page 70% of
// the box, so at least 30% stays free to scroll past it; a map half the box, at least 220 px;
// a card a compact card's; the tiles as in Home's phone layout, which is their content's.
export function foldedHeight(kind: PartKind, box: Size): number | null {
  switch (kind) {
    case "page":
      return box.height * 0.7;
    case "map":
      return Math.max(220, box.height / 2);
    case "card":
      return 180;
    default:
      return null;
  }
}
// The parts as a fold draws them, in reading order: how many of its columns each spans, and
// its folded height (null for its content's). A row of a two-column fold is as tall as its
// taller part.
export function foldedParts(
  layout: Layout,
  fold: Exclude<Fold, "saved">,
  box: Size,
): { part: Part; columns: 1 | 2; height: number | null }[] {
  return readingOrder(layout.parts).map((part) => ({
    part,
    columns: fold === "two" && part.w >= foldSpanBoth ? 2 : 1,
    height: foldedHeight(part.kind, box),
  }));
}

// ----- Home.

// Home's three designed layouts as grids (10.5): the map and each place, as x, y, w × h. A
// corner takes the rectangle of the place `tiles` gives it.
export const homePresets: Record<Preset, Record<"map" | Corner, Rect>> = {
  corners: {
    map: { x: 2, y: 0, w: 8, h: 12 },
    main: { x: 0, y: 0, w: 2, h: 8 },
    recovery: { x: 10, y: 0, w: 2, h: 8 },
    training: { x: 0, y: 8, w: 2, h: 4 },
    changed: { x: 10, y: 8, w: 2, h: 4 },
  },
  hero: {
    map: { x: 3, y: 0, w: 9, h: 8 },
    main: { x: 0, y: 0, w: 3, h: 12 },
    recovery: { x: 3, y: 8, w: 3, h: 4 },
    training: { x: 6, y: 8, w: 3, h: 4 },
    changed: { x: 9, y: 8, w: 3, h: 4 },
  },
  band: {
    map: { x: 0, y: 0, w: 12, h: 8 },
    main: { x: 0, y: 8, w: 4, h: 4 },
    recovery: { x: 4, y: 8, w: 3, h: 4 },
    training: { x: 7, y: 8, w: 3, h: 4 },
    changed: { x: 10, y: 8, w: 2, h: 4 },
  },
};
// The grid a preset draws from the table, each corner in the place `tiles` gives it.
export function presetGrid(layout: Preset, tiles: Tiles): HomeGrid {
  const places = homePresets[layout];
  return {
    parts: homeIds.map((id) => ({
      id,
      ...(id === "map" ? places.map : places[tiles[id]]),
    })),
  };
}

// What is wrong with a Home grid, or null: the five ids exactly once, each at least Home's
// smallest size, inside the grid, nothing overlapping, and nothing beside them (10.5).
export function homeGridProblem(v: unknown): string | null {
  if (
    !object(v) ||
    Object.keys(v).join() !== "parts" ||
    !Array.isArray(v.parts)
  )
    return "A Home grid is {parts: [...]}";
  const parts = v.parts as unknown[];
  if (parts.length !== homeIds.length)
    return "A Home grid has the map and the four corners";
  for (const p of parts) {
    if (
      !object(p) ||
      !homeIds.includes(p.id as HomeId) ||
      Object.keys(p).sort().join() !== "h,id,w,x,y"
    )
      return "A Home grid has the map and the four corners, each {id, x, y, w, h}";
    if (!rectValid(p)) return `Home's ${p.id} doesn't fit in the grid`;
    const smallest = homeSmallest[p.id as HomeId];
    if ((p.w as number) < smallest.w || (p.h as number) < smallest.h)
      return `Home's ${p.id} is smaller than ${smallest.w} × ${smallest.h}`;
  }
  const placed = parts as HomePart[];
  if (new Set(placed.map((p) => p.id)).size !== homeIds.length)
    return "A Home grid has each of its parts once";
  const both = overlap(placed);
  return both ? `Home's ${both[0].id} and ${both[1].id} overlap` : null;
}
// A Home grid as kept (settings.grid, localStorage), in Home's id order, or null when it isn't
// one. Null is never a grid: no grid is the setting left out.
export function cleanHomeGrid(v: unknown): HomeGrid | null {
  if (homeGridProblem(v) !== null || !object(v)) return null;
  const parts = v.parts as HomePart[];
  return {
    parts: homeIds.map((id) => {
      const { x, y, w, h } = parts.find((p) => p.id === id)!;
      return { id, x, y, w, h };
    }),
  };
}

// A rectangle in pixels, from the top left of the box it is drawn in.
export interface PxRect {
  x: number;
  y: number;
  width: number;
  height: number;
}
// What Home's CSS grid draws, as the first move measures it: the box, its gaps, and each
// part's area in that grid (its grid area, not its content), null where a part has none.
export interface HomeMeasure {
  width: number;
  height: number;
  gap: { column: number; row: number };
  areas: Partial<Record<HomeId, PxRect | null>>;
}
// Home's measure from its CSS grid as the browser resolved it (getComputedStyle): the tracks in
// pixels, the gaps, and `grid-template-areas`. The map draws in area `sky`, and each corner in
// the area of the place `tiles` gives it (place `changed` is area `context`).
export function measureHome(
  grid: {
    columns: number[];
    rows: number[];
    gap: { column: number; row: number };
    areas: string | string[];
  },
  tiles: Tiles,
): HomeMeasure {
  const rows = (
    typeof grid.areas === "string"
      ? [...grid.areas.matchAll(/"([^"]*)"/g)].map((m) => m[1])
      : grid.areas
  ).map((row) => row.trim().split(/\s+/));
  const offset = (tracks: number[], gap: number, i: number) =>
    tracks.slice(0, i).reduce((sum, t) => sum + t, 0) + i * gap;
  const area = (name: string): PxRect | null => {
    let top = Infinity,
      left = Infinity,
      bottom = -1,
      right = -1;
    rows.forEach((row, r) =>
      row.forEach((cell, c) => {
        if (cell !== name) return;
        top = Math.min(top, r);
        left = Math.min(left, c);
        bottom = Math.max(bottom, r);
        right = Math.max(right, c);
      }),
    );
    if (
      bottom < 0 ||
      right >= grid.columns.length ||
      bottom >= grid.rows.length
    )
      return null;
    const x = offset(grid.columns, grid.gap.column, left),
      y = offset(grid.rows, grid.gap.row, top);
    return {
      x,
      y,
      width:
        offset(grid.columns, grid.gap.column, right + 1) - grid.gap.column - x,
      height: offset(grid.rows, grid.gap.row, bottom + 1) - grid.gap.row - y,
    };
  };
  return {
    width:
      offset(grid.columns, grid.gap.column, grid.columns.length) -
      grid.gap.column,
    height: offset(grid.rows, grid.gap.row, grid.rows.length) - grid.gap.row,
    gap: grid.gap,
    areas: Object.fromEntries(
      homeIds.map((id) => [
        id,
        area(
          id === "map"
            ? "sky"
            : tiles[id] === "changed"
              ? "context"
              : tiles[id],
        ),
      ]),
    ),
  };
}
// The grid Home's drawing comes to (10.5): each part's area with every edge snapped to the
// nearest grid line, a gap counting half to each side, so Home looks the same after the first
// move, within a cell. Null when a part has no area, or the snapped grid breaks a rule (a part
// below its smallest size among them).
export function measuredGrid(m: HomeMeasure): HomeGrid | null {
  const { column, row } = m.gap;
  // A line i of the grid sits at the middle of its gap: i × pitch − gap / 2.
  const across = (m.width + column) / gridCells,
    down = (m.height + row) / gridCells;
  const parts: HomePart[] = [];
  for (const id of homeIds) {
    const a = m.areas[id];
    if (!a || !(a.width > 0) || !(a.height > 0)) return null;
    const line = (at: number, pitch: number) =>
      Math.min(gridCells, Math.max(0, Math.round(at / pitch)));
    const x = line(a.x, across),
      y = line(a.y, down);
    parts.push({
      id,
      x,
      y,
      w: line(a.x + a.width + column, across) - x,
      h: line(a.y + a.height + row, down) - y,
    });
  }
  const grid = { parts };
  return homeGridProblem(grid) === null ? grid : null;
}
// The grid the first move or resize at Home makes (10.5): the measured one on a screen drawing
// Home's layouts (`measured`), and otherwise, or when that fails, the preset's table.
export function firstHomeGrid(
  layout: Preset,
  tiles: Tiles,
  measured?: HomeMeasure | null,
): HomeGrid {
  return (measured && measuredGrid(measured)) || presetGrid(layout, tiles);
}
// The places of a layout's designed Home (10.5) as `drawn` has them with `tiles` (the grid
// the first move or resize makes): the map's rectangle and each place's.
export type HomePlaces = Record<"map" | Corner, Rect>;
export function homePlaces(drawn: HomeGrid, tiles: Tiles): HomePlaces {
  const places = {} as HomePlaces;
  for (const { id, x, y, w, h } of drawn.parts)
    places[id === "map" ? "map" : tiles[id]] = { x, y, w, h };
  return places;
}
// The tiles that draw `grid` as the designed Home with these places: the map in its own and
// each corner in the place whose rectangle it has; null for any other grid, which only a grid
// draws. A change that comes back to the designed Home keeps no grid, so moves and the moves
// back leave Home exactly as it was, each corner with its place's look (10.5).
export function designedTiles(
  grid: HomeGrid,
  places: HomePlaces,
): Tiles | null {
  const placeAt = new Map(
    Object.entries(places).map(([place, r]) => [canonical(r), place]),
  );
  const tiles = {} as Tiles;
  for (const { id, x, y, w, h } of grid.parts) {
    const place = placeAt.get(canonical({ x, y, w, h }));
    if (!place || (id === "map") !== (place === "map")) return null;
    if (id !== "map") tiles[id] = place as Corner;
  }
  return new Set(Object.values(tiles)).size === homeIds.length - 1
    ? tiles
    : null;
}

// ----- Names.

type Placed = Rect & { id: string; kind?: string };
type Grid = { parts: Placed[] };
// A Home grid's parts have no kind.
const atHome = (grid: Grid) => grid.parts.every((p) => p.kind === undefined);
const kindNames: Record<PartKind, [label: string, name: string]> = {
  page: ["Page", "page"],
  map: ["Map", "map"],
  glucose: ["Glucose", "glucose tile"],
  recovery: ["Recovery", "recovery tile"],
  training: ["Training", "training tile"],
  changed: ["Changed lately", "changed-lately tile"],
  insights: ["Insights", "daily insights"],
  card: ["Card", "card"],
};
// Home's corners by their names in Sources & settings (cornerNames in dashboards.ts).
const homeNames: Record<HomeId, [label: string, name: string]> = {
  map: ["Map", "map"],
  main: ["Main", "Main corner"],
  recovery: ["Recovery", "Recovery corner"],
  training: ["Training", "Training corner"],
  changed: ["Focus", "Focus corner"],
};
const ordinals = ["first", "second", "third", "fourth"];
const capital = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);
function names(grid: Grid, id: string): [label: string, name: string] {
  const part = grid.parts.find((p) => p.id === id);
  if (!part) return ["Part", `part “${id}”`];
  if (part.kind === undefined)
    return homeNames[part.id as HomeId] ?? ["Part", `part “${id}”`];
  if (!isPartKind(part.kind)) return ["Part", `part “${id}”`];
  const [label, name] = kindNames[part.kind];
  const same = readingOrder(grid.parts.filter((p) => p.kind === part.kind));
  if (same.length < 2) return [label, name];
  const nth = ordinals[same.findIndex((p) => p.id === id)] ?? "";
  return [`${capital(nth)} ${name}`, `${nth} ${name}`];
}
// A part's name in plain words, as a note says it: "the map", "the glucose tile", "the Main
// corner"; with two maps (or cards), "the first map" and "the second map" in reading order.
export const partName = (grid: Grid, id: string) => `the ${names(grid, id)[1]}`;
// A part's label, as its group and the announcements say it: "Map", "Glucose", "Main".
export const partLabel = (grid: Grid, id: string) => names(grid, id)[0];

// ----- Moves and resizes (10.6).

export const places = [
  "left",
  "right",
  "up",
  "down",
  "top_left",
  "top_right",
  "bottom_left",
  "bottom_right",
] as const;
export type Place = (typeof places)[number];
export type Side = "left" | "right" | "up" | "down";
export const sizeSteps = [
  "bigger",
  "smaller",
  "wider",
  "narrower",
  "taller",
  "shorter",
] as const;
export type SizeStep = (typeof sizeSteps)[number];
// Where a move takes a part: a side or a corner, another part's place, or a cell.
export type MoveTo =
  { place: Place } | { with: string } | { x: number; y: number };
// How a resize changes a part: a step, or a size in cells.
export type ResizeTo = { size: SizeStep } | { w: number; h: number };
export type ArrangeCommand =
  | ({ action: "move"; part: string } & MoveTo)
  | ({ action: "resize"; part: string } & ResizeTo)
  | { action: "focus_map"; part?: string; focus: MapFocus | null };
// A change's outcome: the grid after it, the summary a note and the saved layout's note say,
// and the announcement for screen readers; or why it was refused.
export type Arranged<G> =
  | { ok: true; grid: G; summary: string; announcement: string }
  | { ok: false; reason: string };

// A checked move, resize or focus_map's arguments (screens.ts commandArgs) as the change they
// ask for, or null for any other action.
export function arrangeCommand(
  action: string,
  args: Record<string, unknown>,
): ArrangeCommand | null {
  const part = typeof args.part === "string" ? args.part : null;
  if (action === "focus_map")
    return {
      action,
      ...(part ? { part } : {}),
      focus: (args.focus ?? null) as MapFocus | null,
    };
  if (!part) return null;
  if (action === "move") {
    if (typeof args.place === "string")
      return { action, part, place: args.place as Place };
    if (typeof args.with === "string") return { action, part, with: args.with };
    return typeof args.x === "number" && typeof args.y === "number"
      ? { action, part, x: args.x, y: args.y }
      : null;
  }
  if (action === "resize") {
    if (typeof args.size === "string")
      return { action, part, size: args.size as SizeStep };
    return typeof args.w === "number" && typeof args.h === "number"
      ? { action, part, w: args.w, h: args.h }
      : null;
  }
  return null;
}

type Axis = "x" | "y";
const lengthOf = (axis: Axis) => (axis === "x" ? "w" : "h");
const crossOf = (axis: Axis): Axis => (axis === "x" ? "y" : "x");
const start = (r: Rect, axis: Axis) => r[axis];
const end = (r: Rect, axis: Axis) => r[axis] + r[lengthOf(axis)];
// How many tracks two rectangles share along an axis.
const shared = (a: Rect, b: Rect, axis: Axis) =>
  Math.max(
    0,
    Math.min(end(a, axis), end(b, axis)) -
      Math.max(start(a, axis), start(b, axis)),
  );
const sides: Record<Side, [axis: Axis, forward: boolean]> = {
  left: ["x", false],
  right: ["x", true],
  up: ["y", false],
  down: ["y", true],
};
// The grid edge a side moves toward.
const edgeWords: Record<Side, string> = {
  left: "left",
  right: "right",
  up: "top",
  down: "bottom",
};
const cornerWords: Record<Exclude<Place, Side>, string> = {
  top_left: "top left",
  top_right: "top right",
  bottom_left: "bottom left",
  bottom_right: "bottom right",
};
const smallestOf = (p: Placed): Cells =>
  p.kind === undefined
    ? (homeSmallest[p.id as HomeId] ?? { w: 1, h: 1 })
    : isPartKind(p.kind)
      ? kindSizes[p.kind].cells
      : { w: 1, h: 1 };
const fits = (p: Placed, r: Rect) => {
  const smallest = smallestOf(p);
  return r.w >= smallest.w && r.h >= smallest.h;
};
// The parts on one side of `p` along an axis that share tracks with it across.
const beside = <P extends Placed>(
  parts: P[],
  p: P,
  axis: Axis,
  forward: boolean,
) =>
  parts.filter(
    (q) =>
      q !== p &&
      shared(q, p, crossOf(axis)) > 0 &&
      (forward
        ? start(q, axis) >= end(p, axis)
        : end(q, axis) <= start(p, axis)),
  );
const distance = (p: Rect, q: Rect, axis: Axis, forward: boolean) =>
  forward ? start(q, axis) - end(p, axis) : start(p, axis) - end(q, axis);

// A part's band on one side (10.6.1): the parts beside it that trade sides with it as one, and
// the range of cells along the move they take, from the nearest part on that side sharing its
// rows (columns): the smallest range holding it such that every part overlapping the range
// within the moving part's rows (columns) lies wholly inside it and wholly within those rows
// (columns). Null when there is none: no part there, or one reaching outside those rows.
export function bandOf<P extends Placed>(
  parts: readonly P[],
  id: string,
  side: Side,
): { parts: P[]; from: number; to: number } | null {
  const all = [...parts],
    p = all.find((q) => q.id === id);
  if (!p) return null;
  const [axis, forward] = sides[side],
    cross = crossOf(axis);
  const there = beside(all, p, axis, forward);
  if (!there.length) return null;
  const nearest = Math.min(...there.map((q) => distance(p, q, axis, forward)));
  const first = there.find((q) => distance(p, q, axis, forward) === nearest)!;
  let from = start(first, axis),
    to = end(first, axis);
  const inRange = (q: P) =>
    q !== p &&
    shared(q, p, cross) > 0 &&
    start(q, axis) < to &&
    end(q, axis) > from;
  for (let grown = true; grown;) {
    grown = false;
    for (const q of all) {
      if (!inRange(q)) continue;
      if (start(q, cross) < start(p, cross) || end(q, cross) > end(p, cross))
        return null;
      if (start(q, axis) < from) [from, grown] = [start(q, axis), true];
      if (end(q, axis) > to) [to, grown] = [end(q, axis), true];
    }
  }
  return { parts: all.filter(inRange), from, to };
}
// Whether two parts line up (10.6.1): the same rows (they line up along x) or the same
// columns (along y), with no other part between them there. The axis, or null.
export function lineUp(
  parts: readonly Placed[],
  a: Placed,
  b: Placed,
): Axis | null {
  for (const axis of ["x", "y"] as const) {
    const cross = crossOf(axis);
    if (a[cross] !== b[cross] || a[lengthOf(cross)] !== b[lengthOf(cross)])
      continue;
    const low = Math.min(end(a, axis), end(b, axis)),
      high = Math.max(start(a, axis), start(b, axis));
    if (
      !parts.some(
        (q) =>
          q !== a &&
          q !== b &&
          shared(q, a, cross) > 0 &&
          start(q, axis) < high &&
          end(q, axis) > low,
      )
    )
      return axis;
  }
  return null;
}
// The part and its band trade sides: the band keeps its sizes and its places inside the band,
// free cells included, the part keeps its size, and the free cells between them stay between.
function tradeSides(
  p: Placed,
  band: { parts: Placed[]; from: number; to: number },
  axis: Axis,
  forward: boolean,
) {
  const length = p[lengthOf(axis)];
  const shift = forward ? start(p, axis) - band.from : end(p, axis) - band.to;
  for (const q of band.parts) q[axis] += shift;
  p[axis] = forward ? band.to - length : band.from;
}
// The part at a cell, or undefined.
const at = <P extends Placed>(parts: readonly P[], x: number, y: number) =>
  parts.find((q) => x >= q.x && x < q.x + q.w && y >= q.y && y < q.y + q.h);
// Whether a rectangle covers only free cells and `p`'s own.
const free = (parts: readonly Placed[], r: Rect, p: Placed) =>
  parts.every((q) => q === p || !overlapping(q, r));

// One change worked out on copies of the parts: the moving part, every part, and the grid's
// names before the change (summaries name parts as they were).
interface Work<G extends Grid> {
  grid: G;
  parts: G["parts"];
  p: G["parts"][number];
  name: (id: string) => string;
  label: (id: string) => string;
}
function work<G extends Grid>(grid: G, id: string): Work<G> | string {
  const parts = grid.parts.map((q) => ({ ...q })) as G["parts"];
  const p = parts.find((q) => q.id === id);
  if (!p) return `There's no part “${id}” here`;
  return {
    grid,
    parts,
    p,
    name: (part) => partName(grid, part),
    label: (part) => partLabel(grid, part),
  };
}
const done = <G extends Grid>(
  w: Work<G>,
  summary: string,
  announcement: string,
): Arranged<G> => ({
  ok: true,
  grid: { ...w.grid, parts: w.parts } as G,
  summary,
  announcement,
});
const refused = (reason: string) => ({ ok: false as const, reason });

// `p` and `q` trade rectangles, sizes included, when each fits the other's (10.6.1).
function tradeRects<G extends Grid>(w: Work<G>, q: Placed): Arranged<G> {
  const { p } = w;
  if (!fits(p, q))
    return refused(
      `No room: ${w.name(p.id)} doesn't fit where ${w.name(q.id)} is`,
    );
  if (!fits(q, p))
    return refused(
      `No room: ${w.name(q.id)} doesn't fit where ${w.name(p.id)} is`,
    );
  const mine = { x: p.x, y: p.y, w: p.w, h: p.h };
  Object.assign(p, { x: q.x, y: q.y, w: q.w, h: q.h });
  Object.assign(q, mine);
  return done(
    w,
    `${capital(w.name(p.id))} traded places and sizes with ${w.name(q.id)}`,
    `${w.label(p.id)} traded places and sizes with ${w.name(q.id)}`,
  );
}
// `p` and another part, as for `with`: along the line they share they trade order, each
// keeping its size and the gap between them; otherwise they trade rectangles. At Home two
// corners always trade places, rectangles and all, as move_tile swaps them (10.5).
function withPart<G extends Grid>(w: Work<G>, q: Placed): Arranged<G> {
  const { p, parts } = w;
  const corners = atHome(w.grid) && p.id !== "map" && q.id !== "map";
  const axis = corners ? null : lineUp(parts, p, q);
  if (!axis) return tradeRects(w, q);
  const forward = start(q, axis) > start(p, axis);
  tradeSides(
    p,
    { parts: [q], from: start(q, axis), to: end(q, axis) },
    axis,
    forward,
  );
  const side: Side =
    axis === "x" ? (forward ? "right" : "left") : forward ? "down" : "up";
  return done(
    w,
    `Swapped ${w.name(p.id)} and ${w.name(q.id)}`,
    `${w.label(p.id)} moved ${side}, past ${w.name(q.id)}`,
  );
}
// What sits beside a part on one side after a move, for the announcement.
function besideWords<G extends Grid>(w: Work<G>, side: Side) {
  const [axis, forward] = sides[side];
  const behind = beside(w.parts, w.p, axis, !forward).filter(
    (q) => distance(w.p, q, axis, !forward) === 0,
  );
  const next = behind.sort(
    (a, b) =>
      shared(b, w.p, crossOf(axis)) - shared(a, w.p, crossOf(axis)) ||
      start(a, crossOf(axis)) - start(b, crossOf(axis)),
  )[0];
  return next
    ? `, beside ${w.name(next.id)}`
    : `, to the ${edgeWords[side]} edge`;
}

// Moves a part (10.6.1). A side: with a band there, the part and its band trade sides; without
// one, it trades rectangles with the nearest part there sharing the most rows (columns), then
// the topmost (leftmost), when each fits the other's; with no part there, it slides to that
// edge. Another part (`with`): along the line they share they trade order, otherwise
// rectangles. A corner: the part covering that corner cell, as for `with`, or the corner
// itself when its cells are free. A cell: the part goes there at its own size (pulled back
// inside the grid) when the cells are free or its own, else the part covering that cell, as
// for `with`. A Home grid keeps Home's smallest sizes.
export function movePart<G extends Grid>(
  grid: G,
  id: string,
  to: MoveTo,
): Arranged<G> {
  const w = work(grid, id);
  if (typeof w === "string") return refused(w);
  const { p, parts } = w;
  const name = w.name(p.id),
    label = w.label(p.id);
  if ("with" in to) {
    const q = parts.find((r) => r.id === to.with);
    if (!q) return refused(`There's no part “${to.with}” here`);
    if (q === p)
      return refused(`${capital(name)} can't trade places with itself`);
    return withPart(w, q);
  }
  if ("x" in to) {
    const target = {
      x: Math.min(to.x, gridCells - p.w),
      y: Math.min(to.y, gridCells - p.h),
      w: p.w,
      h: p.h,
    };
    if (target.x === p.x && target.y === p.y)
      return refused(`${capital(name)} is already there`);
    if (free(parts, target, p)) {
      Object.assign(p, { x: target.x, y: target.y });
      return done(
        w,
        `Moved ${name}`,
        `${label} moved to column ${target.x + 1}, row ${target.y + 1}`,
      );
    }
    const q = at(parts, to.x, to.y);
    return q && q !== p ? withPart(w, q) : refused("No room there");
  }
  const place = to.place;
  if (place in cornerWords) {
    const corner = place as Exclude<Place, Side>;
    const right = corner.endsWith("right"),
      bottom = corner.startsWith("bottom");
    const q = at(parts, right ? gridCells - 1 : 0, bottom ? gridCells - 1 : 0);
    if (q === p)
      return refused(
        `${capital(name)} is already in the ${cornerWords[corner]} corner`,
      );
    if (q) return withPart(w, q);
    const target = {
      x: right ? gridCells - p.w : 0,
      y: bottom ? gridCells - p.h : 0,
      w: p.w,
      h: p.h,
    };
    if (!free(parts, target, p)) return refused("No room there");
    Object.assign(p, { x: target.x, y: target.y });
    return done(
      w,
      `Moved ${name} to the ${cornerWords[corner]}`,
      `${label} moved to the ${cornerWords[corner]} corner`,
    );
  }
  if (!Object.hasOwn(sides, place)) return refused("That isn't a place");
  const side = place as Side;
  const [axis, forward] = sides[side],
    cross = crossOf(axis);
  const there = beside(parts, p, axis, forward);
  if (!there.length) {
    const edge = forward ? gridCells - p[lengthOf(axis)] : 0;
    if (p[axis] === edge)
      return refused(
        `${capital(name)} is already at the ${edgeWords[side]} edge`,
      );
    p[axis] = edge;
    return done(
      w,
      `Moved ${name} ${side}`,
      `${label} moved ${side}${besideWords(w, side)}`,
    );
  }
  const band = bandOf(parts, p.id, side);
  if (band) {
    tradeSides(p, band, axis, forward);
    return done(
      w,
      `Moved ${name} ${side}`,
      `${label} moved ${side}${besideWords(w, side)}`,
    );
  }
  const q = there.sort(
    (a, b) =>
      distance(p, a, axis, forward) - distance(p, b, axis, forward) ||
      shared(b, p, cross) - shared(a, p, cross) ||
      start(a, cross) - start(b, cross),
  )[0];
  return tradeRects(w, q);
}

// What a drag comes to (10.6.1), dropped with the pointer over cell `at` and the part's top
// left at cell `to`: onto free cells (or its own), a move to `to`; onto a part in its band on
// one side, a move to that side; onto any other part, `with` it. Null when it is dropped where
// it started, which changes nothing.
export function dropMove(
  parts: readonly Placed[],
  id: string,
  at: { x: number; y: number },
  to: { x: number; y: number },
): MoveTo | null {
  const p = parts.find((q) => q.id === id);
  if (!p) return null;
  const q = parts.find(
    (r) => at.x >= r.x && at.x < r.x + r.w && at.y >= r.y && at.y < r.y + r.h,
  );
  if (!q || q === p)
    return to.x === p.x && to.y === p.y ? null : { x: to.x, y: to.y };
  // At Home a corner dropped on another part goes `with` it where each fits the other's
  // rectangle (two corners trade places), so dropping it back where it was puts both back
  // (10.5), even when that part is in its band.
  if (
    atHome({ parts: [...parts] }) &&
    p.id !== "map" &&
    fits(p, q) &&
    fits(q, p)
  )
    return { with: q.id };
  const side = (["left", "right", "up", "down"] as const).find((s) =>
    bandOf(parts, id, s)?.parts.some((r) => r.id === q.id),
  );
  return side ? { place: side } : { with: q.id };
}

// The edge a resize moves along an axis (10.6.2): the one away from the grid edge the part
// touches, so the right (bottom) edge unless it touches the grid's right (bottom) but not its
// left (top). True for the right or bottom edge.
const farEdge = (p: Rect, axis: Axis) =>
  !(end(p, axis) === gridCells && start(p, axis) !== 0);
// Grows `p` by up to a step along an axis, taking cells from the parts in the way, each giving
// them up from the side facing it and keeping its smallest size; as far as all of them can.
function grow(
  parts: Placed[],
  p: Placed,
  axis: Axis,
  step: number,
): { grown: number; fills?: true; blocker?: Placed } {
  const forward = farEdge(p, axis),
    length = lengthOf(axis),
    cross = crossOf(axis);
  const room = forward ? gridCells - end(p, axis) : start(p, axis);
  if (!room) return { grown: 0, fills: true };
  const reach = Math.min(step, room);
  const [low, high] = forward
    ? [end(p, axis), end(p, axis) + reach]
    : [start(p, axis) - reach, start(p, axis)];
  const inWay = readingOrder(
    parts.filter(
      (q) =>
        q !== p &&
        shared(q, p, cross) > 0 &&
        start(q, axis) < high &&
        end(q, axis) > low,
    ),
  );
  let can = reach,
    blocker: Placed | undefined;
  for (const q of inWay) {
    const gives =
      distance(p, q, axis, forward) + q[length] - smallestOf(q)[length];
    if (gives < can) [can, blocker] = [gives, q];
  }
  if (can <= 0) return { grown: 0, blocker };
  for (const q of inWay)
    if (forward) {
      const cut = end(p, axis) + can - start(q, axis);
      if (cut > 0) [q[axis], q[length]] = [q[axis] + cut, q[length] - cut];
    } else {
      const cut = end(q, axis) - (start(p, axis) - can);
      if (cut > 0) q[length] -= cut;
    }
  if (!forward) p[axis] -= can;
  p[length] += can;
  return { grown: can, blocker };
}
// Shrinks `p` by up to a step along an axis, never below its smallest size; the part lining up
// with it on that side grows into the freed cells, which otherwise stay empty.
function shrink(parts: Placed[], p: Placed, axis: Axis, step: number): number {
  const forward = farEdge(p, axis),
    length = lengthOf(axis);
  const can = Math.min(step, p[length] - smallestOf(p)[length]);
  if (can <= 0) return 0;
  const lined = beside(parts, p, axis, forward)
    .filter((q) => lineUp(parts, p, q) === axis)
    .sort(
      (a, b) => distance(p, a, axis, forward) - distance(p, b, axis, forward),
    )[0];
  if (forward) {
    p[length] -= can;
    if (lined)
      [lined[axis], lined[length]] = [lined[axis] - can, lined[length] + can];
  } else {
    [p[axis], p[length]] = [p[axis] + can, p[length] - can];
    if (lined) lined[length] += can;
  }
  return can;
}
// Why a part couldn't grow along an axis. At Home the map names the layout with room.
function blockedWords<G extends Grid>(
  w: Work<G>,
  axis: Axis,
  step: { fills?: true; blocker?: Placed },
) {
  if (step.fills)
    return `${capital(w.name(w.p.id))} already fills the grid's ${axis === "x" ? "width" : "height"}`;
  if (atHome(w.grid) && w.p.id === "map")
    return axis === "x"
      ? "The map fills the space between the tiles; the Rhythm band layout gives it the full width"
      : "The map fills the space between the tiles; the Corners layout gives it the full height";
  return `No room: ${step.blocker ? w.name(step.blocker.id) : "a part"} is at its smallest there`;
}

// Resizes a part (10.6.2). A step is 2 cells: `wider` moves one side edge outward, `taller`
// one top or bottom edge, `bigger` both, and the others move the same edges back. Growing takes
// cells from the parts in the way, as far as all of them can give; shrinking never goes below
// the smallest size, and the part lining up on that side takes the freed cells. `w` and `h`:
// that size from the part's top left (moved up or left to stay inside the grid), the other
// parts giving way as for growing, all or nothing.
export function resizePart<G extends Grid>(
  grid: G,
  id: string,
  to: ResizeTo,
): Arranged<G> {
  const w = work(grid, id);
  if (typeof w === "string") return refused(w);
  const { p, parts } = w;
  const name = w.name(p.id),
    label = w.label(p.id);
  const columns = () => `${p.w} of 12 columns`,
    rows = () => `${p.h} of 12 rows`;
  if ("w" in to) return resizeTo(w, to.w, to.h);
  if (!sizeSteps.includes(to.size)) return refused("That isn't a size");
  switch (to.size) {
    case "wider":
    case "taller": {
      const axis = to.size === "wider" ? "x" : "y";
      const step = grow(parts, p, axis, 2);
      if (!step.grown) return refused(blockedWords(w, axis, step));
      return done(
        w,
        `Made ${name} ${to.size}`,
        `${label} ${to.size}, ${axis === "x" ? columns() : rows()}`,
      );
    }
    case "bigger": {
      const across = grow(parts, p, "x", 2),
        down = grow(parts, p, "y", 2);
      if (!across.grown && !down.grown)
        return refused(
          across.fills && down.fills
            ? `${capital(name)} already fills the grid`
            : blockedWords(
                w,
                across.fills ? "y" : "x",
                across.fills ? down : across,
              ),
        );
      return done(
        w,
        `Made ${name} bigger`,
        `${label} bigger, ${columns()} and ${rows()}`,
      );
    }
    case "narrower":
    case "shorter": {
      const axis = to.size === "narrower" ? "x" : "y";
      if (!shrink(parts, p, axis, 2))
        return refused(
          `${capital(name)} is already at its smallest ${axis === "x" ? "width" : "height"}`,
        );
      return done(
        w,
        `Made ${name} ${to.size}`,
        `${label} ${to.size}, ${axis === "x" ? columns() : rows()}`,
      );
    }
    case "smaller": {
      const across = shrink(parts, p, "x", 2),
        down = shrink(parts, p, "y", 2);
      if (!across && !down)
        return refused(`${capital(name)} is already at its smallest`);
      return done(
        w,
        `Made ${name} smaller`,
        `${label} smaller, ${columns()} and ${rows()}`,
      );
    }
  }
}
function resizeTo<G extends Grid>(
  w: Work<G>,
  width: number,
  height: number,
): Arranged<G> {
  const { p, parts } = w;
  const name = w.name(p.id),
    smallest = smallestOf(p);
  if (width < smallest.w || height < smallest.h)
    return refused(
      `${capital(name)} is at least ${smallest.w} × ${smallest.h}`,
    );
  if (width === p.w && height === p.h)
    return refused(`${capital(name)} is already ${width} × ${height}`);
  const r = {
    x: Math.min(p.x, gridCells - width),
    y: Math.min(p.y, gridCells - height),
    w: width,
    h: height,
  };
  // Each part in the way gives up cells from the side facing the part, keeping its smallest
  // size; of two sides, the one that leaves it more cells.
  const cuts: [Placed, Rect][] = [];
  for (const q of readingOrder(parts)) {
    if (q === p || !overlapping(q, r)) continue;
    const least = smallestOf(q);
    const options: Rect[] = [];
    if (q.x >= p.x + p.w)
      options.push({ ...q, x: r.x + r.w, w: q.x + q.w - r.x - r.w });
    if (q.x + q.w <= p.x) options.push({ ...q, w: r.x - q.x });
    if (q.y >= p.y + p.h)
      options.push({ ...q, y: r.y + r.h, h: q.y + q.h - r.y - r.h });
    if (q.y + q.h <= p.y) options.push({ ...q, h: r.y - q.y });
    const best = options
      .filter((o) => o.w >= least.w && o.h >= least.h)
      .sort((a, b) => b.w * b.h - a.w * a.h)[0];
    if (!best)
      return refused(`No room: ${w.name(q.id)} is at its smallest there`);
    cuts.push([q, best]);
  }
  for (const [q, cut] of cuts)
    Object.assign(q, { x: cut.x, y: cut.y, w: cut.w, h: cut.h });
  Object.assign(p, r);
  return done(
    w,
    `Resized ${name}`,
    `${w.label(p.id)} resized, ${p.w} of 12 columns and ${p.h} of 12 rows`,
  );
}

// ----- Focus changes, and changes to a dashboard or Home.

// Changes a map part's focus (10.6, `focus_map`): `part` may be left out when the layout has
// exactly one map; null shows everything again.
export function focusPart(
  layout: Layout,
  part: string | undefined,
  focus: MapFocus | null,
  title?: (star: string) => string | undefined,
): Arranged<Layout> {
  const maps = layout.parts.filter((p) => p.kind === "map");
  const target =
    part === undefined
      ? maps.length === 1
        ? maps[0]
        : undefined
      : layout.parts.find((p) => p.id === part);
  if (!target)
    return refused(
      part !== undefined
        ? `There's no part “${part}” here`
        : maps.length
          ? "This dashboard has more than one map; name the one to focus"
          : "This dashboard has no map",
    );
  const name = partName(layout, target.id),
    label = partLabel(layout, target.id);
  if (target.kind !== "map") return refused(`${capital(name)} isn't a map`);
  if (sameFocus(target.focus, focus))
    return refused(
      focus
        ? `${capital(name)} already shows that`
        : `${capital(name)} already shows everything`,
    );
  const parts = layout.parts.map((p) => {
    if (p !== target) return p;
    const { id, kind, x, y, w, h } = p;
    return focus ? { id, kind, x, y, w, h, focus } : { id, kind, x, y, w, h };
  });
  const words = focus ? focusSummary(focus, title) : "";
  return {
    ok: true,
    grid: { ...layout, parts },
    summary: focus
      ? `Focused ${name}: ${words}`
      : `${capital(name)} shows everything again`,
    announcement: focus
      ? `${label} focused: ${words}`
      : `${label} shows everything`,
  };
}
// A move, resize or focus change to a dashboard's layout (10.6).
export function changeLayout(
  layout: Layout,
  command: ArrangeCommand,
  title?: (star: string) => string | undefined,
): Arranged<Layout> {
  if (command.action === "focus_map")
    return focusPart(layout, command.part, command.focus, title);
  const { action, part, ...to } = command;
  return action === "move"
    ? movePart(layout, part, to as MoveTo)
    : resizePart(layout, part, to as ResizeTo);
}

// A Home change that only swaps two corners' rectangles, as the move_tile it is applied as
// (10.5): `tile` is the corner that moved (`moved`, when it is one of the two), and `to` the
// place the other one had. Null for any other change.
export function cornerSwap(
  before: HomeGrid,
  after: HomeGrid,
  tiles: Tiles,
  moved: string,
): { tile: Corner; to: Corner } | null {
  const rect = (g: HomeGrid, id: HomeId) => {
    const { x, y, w, h } = g.parts.find((p) => p.id === id)!;
    return canonical({ x, y, w, h });
  };
  const changed = homeIds.filter((id) => rect(before, id) !== rect(after, id));
  if (changed.length !== 2 || changed.includes("map")) return null;
  const [a, b] = changed as Corner[];
  if (rect(before, a) !== rect(after, b) || rect(before, b) !== rect(after, a))
    return null;
  const [tile, other] = moved === b ? [b, a] : [a, b];
  return { tile, to: tiles[other] };
}
// move_tile on a screen with a grid (10.5): the corner `tile` and the one in place `to` trade
// rectangles, sizes included, as their places are swapped.
export function swapCornerRects(
  grid: HomeGrid,
  tiles: Tiles,
  tile: Corner,
  to: Corner,
): HomeGrid {
  const other = (Object.keys(tiles) as Corner[]).find((t) => tiles[t] === to);
  const a = grid.parts.find((p) => p.id === tile),
    b = grid.parts.find((p) => p.id === other);
  if (!a || !b || a === b) return grid;
  return {
    parts: grid.parts.map((p) =>
      p === a ? { ...b, id: a.id } : p === b ? { ...a, id: b.id } : p,
    ),
  };
}
export type HomeChange =
  | {
      ok: true;
      // The grid to keep, or null to keep none (a swap made on a screen without one).
      grid: HomeGrid | null;
      // The move_tile this change is applied as, when it only swaps two corners.
      swap: { tile: Corner; to: Corner } | null;
      summary: string;
      announcement: string;
    }
  | { ok: false; reason: string };
export const homeFocusRefused = "Home's map takes no focus";
// A move or resize at Home (10.5), made on the screen's grid, or with none on `first`, the
// grid the first change makes (firstHomeGrid). A change that only swaps two corners comes back
// as the move_tile it is applied as, with no grid when the screen had none; anything else comes
// back as the grid to keep. Home's map takes no focus.
export function changeHome(
  grid: HomeGrid | null,
  first: HomeGrid,
  tiles: Tiles,
  command: ArrangeCommand,
): HomeChange {
  if (command.action === "focus_map") return refused(homeFocusRefused);
  const base = grid ?? first;
  const { action, part, ...to } = command;
  const result =
    action === "move"
      ? movePart(base, part, to as MoveTo)
      : resizePart(base, part, to as ResizeTo);
  if (!result.ok) return result;
  const swap = cornerSwap(base, result.grid, tiles, part);
  if (!swap) return { ...result, swap: null };
  const from = homeNames[swap.tile][0],
    place = homeNames[swap.to][0];
  return {
    ok: true,
    grid: grid ? result.grid : null,
    swap,
    summary: `Moved ${from} to the ${place} place`,
    announcement: `${from} moved to the ${place} place`,
  };
}

// ----- What a screen reports.

// One part of what a screen shows as a grid (`showing.parts`, 10.8): its rectangle as saved,
// and a map's focus in plain words. A dashboard's parts report their kind, Home's `map` or
// `corner`.
export interface ShowingPart {
  id: string;
  kind: string;
  x: number;
  y: number;
  w: number;
  h: number;
  focus?: string;
}

// A dashboard's parts as `showing.parts` reports them (10.8): in reading order, rectangles as
// saved, a map's focus in plain words; the page alone for a layout of none.
export function showingParts(
  layout: Layout | null,
  title?: (star: string) => string | undefined,
): ShowingPart[] {
  return readingOrder(layoutOf(layout).parts).map((p) => ({
    id: p.id,
    kind: p.kind,
    x: p.x,
    y: p.y,
    w: p.w,
    h: p.h,
    ...(p.focus ? { focus: focusSummary(p.focus, title) } : {}),
  }));
}
// Home's parts as `showing.parts` reports them: kind `map` or `corner`.
export const homeShowingParts = (grid: HomeGrid): ShowingPart[] =>
  readingOrder(grid.parts).map(({ id, x, y, w, h }) => ({
    id,
    kind: id === "map" ? "map" : "corner",
    x,
    y,
    w,
    h,
  }));
