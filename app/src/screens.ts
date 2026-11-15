import { cornerNames, corners, dashboardIdValid } from "./dashboards.js";
import type { Corner } from "./dashboards.js";
import {
  cleanHomeGrid,
  normalFocus,
  partIdValid,
  places,
  rectValid,
  sizeSteps,
} from "./parts.js";
import type { HomeGrid, Place, ShowingPart, SizeStep } from "./parts.js";
import { object } from "./validation.js";

// Live screens (docs/dev/screens.md): every open copy of the app is a named screen that an AI
// client or another screen can send a command to. This is what the facade and the browser
// share: the commands and their arguments, the shapes the engine answers, this browser's
// identity, and the per-screen layout settings. Every answer is checked; names and titles are
// labels someone typed, never identity.

export const screenActions = [
  "open_star",
  "open_dashboard",
  "home",
  "layout",
  "move_tile",
  "corner_card",
  "search",
  "undo",
  "full_screen",
  "set_home",
  "move",
  "resize",
  "focus_map",
  "open_studio",
  "dashboard_controls",
  "dashboard_action",
] as const;
export type ScreenAction = (typeof screenActions)[number];
// How the tiles sit around the sky (App.tsx), and their names in Sources & settings.
export const layouts = ["corners", "hero", "band"] as const;
export type Layout = (typeof layouts)[number];
export const layoutNames: Record<Layout, string> = {
  corners: "Corners",
  hero: "Tall column",
  band: "Rhythm band",
};
// The four Home tiles are the four corners, and each place is named after the tile that sits
// there by default.
export type Tile = Corner;
export const tiles = corners;
export const tileNames = cornerNames;
// Where each tile sits now: tile to place.
export type Tiles = Record<Tile, Tile>;
export const defaultTiles: Tiles = {
  main: "main",
  recovery: "recovery",
  training: "training",
  changed: "changed",
};
export const isLayout = (v: unknown): v is Layout =>
  layouts.includes(v as Layout);
const isTile = (v: unknown): v is Tile => tiles.includes(v as Tile);

export const screenIdValid = (v: unknown): v is string =>
  typeof v === "string" && /^[A-Za-z0-9_-]{16,64}$/.test(v);
// Characters as the engine and its table count them, so an emoji is one.
const characters = (text: string) => Array.from(text).length;
// A screen's name: 1-60 characters once trimmed, no control characters.
export const cleanName = (v: unknown): string | null =>
  typeof v === "string" &&
  characters(v.trim()) >= 1 &&
  characters(v.trim()) <= 60 &&
  !/\p{Cc}/u.test(v)
    ? v.trim()
    : null;
const size = (v: unknown) => JSON.stringify(v).length;
// The engine keeps at most 4000 characters of what a screen shows (measured as the database
// stores it, a little longer than sent) and 2000 of its settings.
export const showingValid = (v: unknown): v is Record<string, unknown> =>
  object(v) && size(v) <= 4000;
export const detailValid = showingValid;
// This screen's Home: null for the default (the sky and tiles), or a dashboard it shows full
// screen whenever it goes Home.
export type Home = { dashboard: string } | null;
// `grid` is this screen's Home grid (docs/dev/screens.md 10.5), kept only once someone moved
// or resized a part of Home here: absent is no grid, and null is never one.
export interface ScreenSettings {
  layout: Layout;
  tiles: Tiles;
  home: Home;
  grid?: HomeGrid;
}
// A dashboard full screen as a screen shows it: its Home, or one over what is open (overHome
// when it replaced the Home dashboard, which exiting it shows again).
export interface FullView {
  id: string;
  home: boolean;
  overHome?: boolean;
}
// What a screen shows once another tab of its browser chose its Home `next` (it was `was`): at
// Home (the sky or the Home dashboard), the new Home. A layout or tiles change alone, which
// leaves the Home as it was, and a full screen of another dashboard keep what is shown.
export function followHome(
  full: FullView | null,
  was: string | null,
  next: Home,
): FullView | null {
  const now = next?.dashboard ?? null;
  if (now === was || (full && !full.home)) return full;
  return now ? { id: now, home: true } : null;
}
// A Home as it is kept, or undefined when it isn't one.
export function cleanHome(v: unknown): Home | undefined {
  if (v === null) return null;
  return object(v) &&
    Object.keys(v).join(",") === "dashboard" &&
    dashboardIdValid(v.dashboard)
    ? { dashboard: v.dashboard.toLowerCase() }
    : undefined;
}
// A screen's settings as it sends them: {layout, tiles, home}, with its Home grid once it has
// one, or {layout, tiles} as a screen from before the Home choice sends them.
export const settingsValid = (
  v: unknown,
): v is { layout: Layout; tiles: Tiles; home?: Home; grid?: HomeGrid } =>
  object(v) &&
  [
    "home,layout,tiles",
    "layout,tiles",
    "grid,home,layout,tiles",
    "grid,layout,tiles",
  ].includes(Object.keys(v).sort().join(",")) &&
  isLayout(v.layout) &&
  cleanTiles(v.tiles) !== null &&
  (!Object.hasOwn(v, "home") || cleanHome(v.home) !== undefined) &&
  (!Object.hasOwn(v, "grid") || cleanHomeGrid(v.grid) !== null);

// A tile map: each tile in exactly one place.
export function cleanTiles(v: unknown): Tiles | null {
  if (!object(v)) return null;
  const keys = Object.keys(v).sort().join(",");
  if (keys !== [...tiles].sort().join(",")) return null;
  const places = tiles.map((t) => v[t]);
  return places.every(isTile) && new Set(places).size === tiles.length
    ? (Object.fromEntries(tiles.map((t) => [t, v[t]])) as Tiles)
    : null;
}
// `tile` moves to the place `to` names, and whatever was there takes its old place.
export function swapTiles(map: Tiles, tile: Tile, to: Tile): Tiles {
  const from = map[tile];
  if (from === to) return map;
  const other = tiles.find((t) => map[t] === to)!;
  return { ...map, [tile]: to, [other]: from };
}
// A screen's settings, as {layout, tiles, home, grid}; any may be missing or wrong, and is then
// left out.
export function cleanSettings(v: unknown): Partial<ScreenSettings> {
  if (!object(v)) return {};
  const map = cleanTiles(v.tiles),
    home = cleanHome(v.home),
    grid = cleanHomeGrid(v.grid);
  return {
    ...(isLayout(v.layout) ? { layout: v.layout } : {}),
    ...(map ? { tiles: map } : {}),
    ...(home !== undefined ? { home } : {}),
    ...(grid ? { grid } : {}),
  };
}

// The dashboard a part command must be on (10.6): a dashboard's id, or null for the screen's
// default Home; left out, the engine fills it in from what the screen last reported. Undefined
// when it is neither.
function dashboardGuard(args: Record<string, unknown>) {
  if (!Object.hasOwn(args, "dashboard")) return {};
  if (args.dashboard === null) return { dashboard: null };
  return dashboardIdValid(args.dashboard)
    ? { dashboard: args.dashboard.toLowerCase() }
    : undefined;
}
// A cell (0-11) and a size (1-12), in whole cells.
const cell = (v: unknown) =>
  Number.isInteger(v) && (v as number) >= 0 && (v as number) <= 11;
const span = (v: unknown) =>
  Number.isInteger(v) && (v as number) >= 1 && (v as number) <= 12;

// One command's arguments, exactly as sections 2, 8 and 10.6 of the contract list them: the
// action's own arguments and nothing else. Answers them cleaned (ids in lower case, the query
// trimmed, a focus as stored), or null when any is missing, unknown or out of range.
export function commandArgs(
  action: unknown,
  args: Record<string, unknown>,
): Record<string, unknown> | null {
  const keys = Object.keys(args).sort().join(",");
  switch (action) {
    case "open_studio":
      return keys === "session" && dashboardIdValid(args.session)
        ? { session: args.session }
        : null;
    case "dashboard_controls":
    case "dashboard_action":
      return keys ===
        (action === "dashboard_controls"
          ? "dashboard,version"
          : "base,control,dashboard,value,version") &&
        dashboardIdValid(args.dashboard) &&
        Number.isInteger(args.version) &&
        (args.version as number) > 0 &&
        (action === "dashboard_controls" ||
          (Number.isInteger(args.base) &&
            (args.base as number) >= 0 &&
            partIdValid(args.control)))
        ? args
        : null;
    case "open_star":
      return keys === "star" &&
        typeof args.star === "string" &&
        args.star.length >= 1 &&
        args.star.length <= 1000
        ? { star: args.star }
        : null;
    case "open_dashboard":
      return keys === "dashboard" && dashboardIdValid(args.dashboard)
        ? { dashboard: args.dashboard.toLowerCase() }
        : null;
    case "home":
    case "undo":
      return keys === "" ? {} : null;
    case "layout":
      return keys === "layout" && isLayout(args.layout)
        ? { layout: args.layout }
        : null;
    case "move_tile":
      return keys === "tile,to" && isTile(args.tile) && isTile(args.to)
        ? { tile: args.tile, to: args.to }
        : null;
    case "corner_card":
      // No dashboard (absent or null) clears the corner.
      if (!isTile(args.corner)) return null;
      if (keys === "corner") return { corner: args.corner, dashboard: null };
      return keys === "corner,dashboard" &&
        (args.dashboard === null || dashboardIdValid(args.dashboard))
        ? {
            corner: args.corner,
            dashboard:
              args.dashboard === null
                ? null
                : (args.dashboard as string).toLowerCase(),
          }
        : null;
    case "search": {
      const query = typeof args.query === "string" ? args.query.trim() : "";
      return keys === "query" &&
        characters(query) >= 1 &&
        characters(query) <= 100 &&
        !/\p{Cc}/u.test(query)
        ? { query }
        : null;
    }
    case "full_screen":
      // No dashboard is the one open on the screen; null is not one, as the engine says.
      if (keys === "") return {};
      return keys === "dashboard" && dashboardIdValid(args.dashboard)
        ? { dashboard: args.dashboard.toLowerCase() }
        : null;
    case "set_home":
      // No dashboard (absent or null) restores the default Home.
      if (keys === "") return { dashboard: null };
      return keys === "dashboard" &&
        (args.dashboard === null || dashboardIdValid(args.dashboard))
        ? {
            dashboard:
              args.dashboard === null
                ? null
                : (args.dashboard as string).toLowerCase(),
          }
        : null;
    case "move":
    case "resize": {
      // A part, exactly one way to move or resize it, and the optional dashboard guard.
      const guard = dashboardGuard(args),
        way = Object.keys(args)
          .filter((k) => k !== "part" && k !== "dashboard")
          .sort()
          .join(",");
      if (!partIdValid(args.part) || !guard) return null;
      const to =
        action === "move"
          ? way === "place" && places.includes(args.place as Place)
            ? { place: args.place }
            : way === "with" &&
                partIdValid(args.with) &&
                args.with !== args.part
              ? { with: args.with }
              : way === "x,y" && cell(args.x) && cell(args.y)
                ? { x: args.x, y: args.y }
                : null
          : way === "size" && sizeSteps.includes(args.size as SizeStep)
            ? { size: args.size }
            : way === "h,w" && span(args.w) && span(args.h)
              ? { w: args.w, h: args.h }
              : null;
      return to ? { part: args.part, ...to, ...guard } : null;
    }
    case "focus_map": {
      // No focus (absent or null) shows everything again, stored as focus: null.
      const guard = dashboardGuard(args),
        focus = normalFocus(args.focus);
      return Object.keys(args).every((k) =>
        ["focus", "part", "dashboard"].includes(k),
      ) &&
        guard &&
        focus !== undefined &&
        (args.part === undefined || partIdValid(args.part))
        ? {
            focus,
            ...(args.part !== undefined ? { part: args.part } : {}),
            ...guard,
          }
        : null;
    }
    default:
      return null;
  }
}

export type Sender =
  { kind: "assistant" } | { kind: "screen"; screen: string; name: string };
export type CommandResult =
  "pending" | "delivered" | "shown" | "failed" | "expired";
export interface ScreenCommand {
  id: string;
  // A later piece may add actions; a screen that doesn't know one reports it failed.
  action: string;
  args: Record<string, unknown>;
  sender: Sender;
  sentAt: string;
  deliveredAt: string | null;
  doneAt: string | null;
  result: CommandResult;
  detail: Record<string, unknown>;
  ms: { deliver: number | null; show: number | null };
}
export interface ScreenSummary {
  id: string;
  name: string;
  open: boolean;
  seenAt: string;
  showing: Record<string, unknown>;
  self: boolean;
}
export interface ListenAnswer {
  command: ScreenCommand | null;
  screen: { id: string; name: string; settings: Record<string, unknown> };
}
// A listen the engine refused because this screen's id listens with another app login
// (docs/dev/screens.md 3.2): only while viewer sessions are on, once this browser's login
// changed (a session started or ended here). The browser then takes a new id.
export interface ListenTaken {
  taken: true;
}
export const listenTaken = (v: unknown): v is ListenTaken =>
  object(v) && v.taken === true;
// What sending to another screen came to: the command as it stands after the engine's short
// wait, or why it wasn't sent (or may have been).
export type SendAnswer =
  { ok: true; command: ScreenCommand } | { ok: false; reason: string };

const results: CommandResult[] = [
  "pending",
  "delivered",
  "shown",
  "failed",
  "expired",
];
const time = (v: unknown): v is string =>
  typeof v === "string" && v.length <= 40 && Number.isFinite(Date.parse(v));
const nullableTime = (v: unknown) => (time(v) ? v : null);
const ms = (v: unknown) =>
  typeof v === "number" && Number.isFinite(v) && v >= 0 ? Math.round(v) : null;

export function cleanSender(v: unknown): Sender | null {
  if (!object(v)) return null;
  if (v.kind === "assistant") return { kind: "assistant" };
  const name = cleanName(v.name);
  return v.kind === "screen" && screenIdValid(v.screen) && name
    ? { kind: "screen", screen: v.screen, name }
    : null;
}
export function cleanCommand(v: unknown): ScreenCommand | null {
  if (
    !object(v) ||
    !dashboardIdValid(v.id) ||
    typeof v.action !== "string" ||
    !/^[a-z_]{1,32}$/.test(v.action) ||
    !object(v.args) ||
    size(v.args) > 2000 ||
    !time(v.sentAt) ||
    !results.includes(v.result as CommandResult)
  )
    return null;
  const sender = cleanSender(v.sender);
  if (!sender) return null;
  return {
    id: v.id.toLowerCase(),
    action: v.action,
    args: v.args,
    sender,
    sentAt: v.sentAt,
    deliveredAt: nullableTime(v.deliveredAt),
    doneAt: nullableTime(v.doneAt),
    result: v.result as CommandResult,
    detail: detailValid(v.detail) ? v.detail : {},
    ms: object(v.ms)
      ? { deliver: ms(v.ms.deliver), show: ms(v.ms.show) }
      : { deliver: null, show: null },
  };
}
function cleanSelf(v: unknown): ListenAnswer["screen"] | null {
  const name = object(v) ? cleanName(v.name) : null;
  return object(v) && screenIdValid(v.id) && name
    ? {
        id: v.id,
        name,
        settings: settingsValid(v.settings) ? v.settings : {},
      }
    : null;
}
// listen's answer: the command this screen claimed (or none after an empty wait) and the
// screen as the engine keeps it.
export function cleanListen(v: unknown): ListenAnswer | null {
  if (!object(v)) return null;
  const screen = cleanSelf(v.screen);
  if (!screen || (v.command !== null && !object(v.command))) return null;
  return {
    command: v.command === null ? null : cleanCommand(v.command),
    screen,
  };
}
export const cleanRenamed = (v: unknown) =>
  object(v) ? cleanSelf(v.screen) : null;
export function cleanScreens(v: unknown): ScreenSummary[] | null {
  if (!object(v) || !Array.isArray(v.screens) || v.screens.length > 100)
    return null;
  const screens: ScreenSummary[] = [];
  for (const s of v.screens) {
    const name = object(s) ? cleanName(s.name) : null;
    if (!object(s) || !screenIdValid(s.id) || !name || !time(s.seenAt))
      continue;
    screens.push({
      id: s.id,
      name,
      open: s.open === true,
      seenAt: s.seenAt,
      showing: showingValid(s.showing) ? s.showing : {},
      self: s.self === true,
    });
  }
  return screens;
}

// One part of what a screen shows as a grid (parts.ts), reported in `parts` from piece 3.
export type { ShowingPart };
// What a screen reports it shows (sections 5 and 8), titles cut to 200 characters: with a
// dashboard full screen, view "full_screen" and that dashboard; its Home (this app always says,
// a screen from before the Home choice doesn't), marked missing when that dashboard was deleted
// and the screen goes to the default Home instead; and when a person last touched it. The
// browser sends that as activeAgo, how many milliseconds ago, and the facade passes it on as
// activeAt by its own clock, so screens whose clocks differ still compare. From piece 3 it
// reports its parts too: a dashboard's layout or Home's grid in reading order, else none.
export interface Showing {
  view: "home" | "star" | "dashboard" | "search" | "full_screen" | "other";
  star?: { id: string; title: string };
  dashboard?: { id: string; title: string };
  query?: string;
  layout: Layout;
  tiles: Tiles;
  narrow: boolean;
  home?: {
    dashboard: { id: string; title: string };
    missing?: true;
  } | null;
  activeAt?: string;
  activeAgo?: number;
  parts?: ShowingPart[];
}
// When a person last touched a screen, from how long ago by this clock: never before 1970.
export const activeFrom = (ago: number, now = Date.now()) =>
  new Date(Math.max(0, now - ago)).toISOString();
// What a screen shows with its activeAgo as activeAt by this clock, as the facade passes it on
// (the demo plays the facade's and the engine's part).
export function stampActive(showing: unknown, now = Date.now()): unknown {
  if (!object(showing) || typeof showing.activeAgo !== "number") return showing;
  const { activeAgo, ...rest } = showing;
  return { ...rest, activeAt: activeFrom(activeAgo as number, now) };
}
// Cut by characters, so a cut never splits one in two (the engine refuses half of one).
const clip = (text: string, max: number) =>
  text.length <= max ? text : Array.from(text).slice(0, max).join("");
export const cut = (title: string) => clip(title, 200);
// Text a screen reports, on one line and cut to `max` characters.
const line = (text: string, max: number) =>
  clip(text.replace(/\p{Cc}/gu, " "), max);
// A screen's parts as it reports them, at most 12 (a layout's most), each focus cut to 100
// characters; null when they aren't.
function cleanParts(v: unknown): ShowingPart[] | null {
  if (!Array.isArray(v) || v.length > 12) return null;
  const parts: ShowingPart[] = [];
  for (const p of v) {
    if (
      !object(p) ||
      !partIdValid(p.id) ||
      typeof p.kind !== "string" ||
      !/^[a-z][a-z0-9_]{0,31}$/.test(p.kind) ||
      !rectValid(p) ||
      (p.focus !== undefined && typeof p.focus !== "string")
    )
      return null;
    const focus = typeof p.focus === "string" ? line(p.focus, 100) : "";
    parts.push({
      id: p.id,
      kind: p.kind,
      x: p.x as number,
      y: p.y as number,
      w: p.w as number,
      h: p.h as number,
      ...(focus ? { focus } : {}),
    });
  }
  return parts;
}
const views: Showing["view"][] = [
  "home",
  "star",
  "dashboard",
  "search",
  "full_screen",
  "other",
];

// What a screen shows, as the facade passes it on: only the fields above, titles cut to 200
// characters and the search to 100, so what AI clients read stays short and always fits the
// engine's limit. Null when it isn't one. A screen from before the Home choice reports no
// Home, which reads as the default. activeAgo becomes activeAt by this clock (`now`); an
// activeAt the browser sent is its own clock's, and left out. Parts that aren't parts are left
// out, as a screen from before piece 3 reports none.
export function cleanShowing(v: unknown, now = Date.now()): Showing | null {
  const map = object(v) ? cleanTiles(v.tiles) : null;
  if (
    !object(v) ||
    !views.includes(v.view as Showing["view"]) ||
    !isLayout(v.layout) ||
    !map ||
    typeof v.narrow !== "boolean" ||
    (v.home !== undefined && v.home !== null && !object(v.home)) ||
    (v.activeAgo !== undefined &&
      !(Number.isSafeInteger(v.activeAgo) && (v.activeAgo as number) >= 0))
  )
    return null;
  const item = (x: unknown) =>
    object(x) &&
    typeof x.id === "string" &&
    x.id.length >= 1 &&
    x.id.length <= 1000 &&
    !/\p{Cc}/u.test(x.id) &&
    typeof x.title === "string"
      ? { id: x.id, title: line(x.title, 200) }
      : null;
  const star = item(v.star),
    dashboard = item(v.dashboard);
  const query =
    typeof v.query === "string" ? line(v.query.trim(), 100).trim() : "";
  // A Home is a dashboard, by its uuid.
  const homeBoard = object(v.home) ? item(v.home.dashboard) : null;
  if (object(v.home) && !(homeBoard && dashboardIdValid(homeBoard.id)))
    return null;
  const parts = v.parts === undefined ? null : cleanParts(v.parts);
  const clean: Showing = {
    view: v.view as Showing["view"],
    ...(star ? { star } : {}),
    ...(dashboard ? { dashboard } : {}),
    ...(query ? { query } : {}),
    layout: v.layout,
    tiles: map,
    narrow: v.narrow,
    ...(v.home === undefined
      ? {}
      : {
          home:
            homeBoard && object(v.home)
              ? {
                  dashboard: {
                    id: homeBoard.id.toLowerCase(),
                    title: homeBoard.title,
                  },
                  ...(v.home.missing === true
                    ? { missing: true as const }
                    : {}),
                }
              : null,
        }),
    ...(v.activeAgo !== undefined
      ? { activeAt: activeFrom(v.activeAgo as number, now) }
      : {}),
    ...(parts ? { parts } : {}),
  };
  return showingValid(clean) ? clean : null;
}
// What a screen says about a command (section 6): a summary and a note in plain words, and for
// a search how many stars matched and the first 10 titles. Nothing else, each cut to fit.
export function cleanDetail(v: unknown): Record<string, unknown> | null {
  if (!object(v)) return null;
  const titles = Array.isArray(v.titles)
    ? v.titles
        .filter((t): t is string => typeof t === "string")
        .slice(0, 10)
        .map((t) => line(t, 200))
    : null;
  const clean = {
    ...(typeof v.summary === "string" ? { summary: line(v.summary, 200) } : {}),
    ...(typeof v.note === "string" ? { note: line(v.note, 200) } : {}),
    ...(Number.isInteger(v.matches) && (v.matches as number) >= 0
      ? { matches: v.matches }
      : {}),
    ...(titles ? { titles } : {}),
  };
  return detailValid(clean) ? clean : null;
}

// In words, for the sender: what became of a command sent to `to`.
export function sentWords(command: ScreenCommand, to: string) {
  const summary =
    typeof command.detail.summary === "string"
      ? command.detail.summary.slice(0, 200)
      : "";
  return command.result === "shown"
    ? `Shown on ${to}`
    : command.result === "failed"
      ? `${to} couldn't show it${summary ? `: ${summary}` : ""}`
      : command.result === "expired"
        ? `${to} didn't pick it up in time`
        : `Sent to ${to}, not confirmed yet`;
}

// This browser's screen, kept in localStorage: a random id, made once, and a name. Without
// storage (a private window) it lasts as long as the page.
const screenKey = "rhythm-of-life.screen";
const tabKey = "rhythm-of-life.screen-tab";
let unstored: Identity | null = null,
  // Whether `unstored`'s id was taken after a refusal and no listen with it was answered since.
  unstoredFresh = false;
const random = (bytes: number) =>
  btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(bytes))))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/, "");
export const newScreenId = () => random(16);

// A first name for this screen, from the kind of device it runs on.
export function deviceName(
  agent = navigator.userAgent,
  touchPoints = navigator.maxTouchPoints ?? 0,
) {
  if (/iPhone|iPod/.test(agent)) return "iPhone";
  // An iPad asks for desktop sites as a Mac, but has a touch screen.
  if (/iPad/.test(agent) || (/Macintosh/.test(agent) && touchPoints > 1))
    return "iPad";
  if (/Android/.test(agent))
    return /Mobile/.test(agent) ? "Android phone" : "Android tablet";
  if (/CrOS/.test(agent)) return "Chromebook";
  if (/Macintosh|Mac OS X/.test(agent)) return "Mac";
  if (/Windows/.test(agent)) return "Windows PC";
  if (/Linux/.test(agent)) return "Linux PC";
  return "Browser";
}
// The browser it runs in, or null when it can't be told. A browser's agent also names those it
// is built like (Edge says Chrome and Safari too), so the checks go from the most particular.
export function browserName(agent = navigator.userAgent) {
  if (/Edg(e|A|iOS)?\//.test(agent)) return "Edge";
  if (/OPR\/|Opera/.test(agent)) return "Opera";
  if (/SamsungBrowser\//.test(agent)) return "Samsung Internet";
  if (/Firefox\/|FxiOS\//.test(agent)) return "Firefox";
  if (/Chrome\/|CriOS\//.test(agent)) return "Chrome";
  if (/Safari\//.test(agent)) return "Safari";
  return null;
}
// A first name for this screen: its device and browser ("Windows PC · Chrome"), so two
// screens of one household rarely share it. The engine numbers one that still does.
export function defaultScreenName(
  agent = navigator.userAgent,
  touchPoints = navigator.maxTouchPoints ?? 0,
) {
  const device = deviceName(agent, touchPoints),
    browser = browserName(agent);
  if (device === "Browser") return browser ?? device;
  return browser ? `${device} · ${browser}` : device;
}

// This browser's screen: its id, its name, and whether a person chose that name (else it is
// the default, which the engine's numbered copy replaces). A screen saved before names said so
// kept the device's name as its default.
type Identity = { id: string; name: string; chosen: boolean };
const made = (): Identity => ({
  id: newScreenId(),
  name: defaultScreenName(),
  chosen: false,
});
export function screenIdentity(): Identity {
  try {
    const saved: unknown = JSON.parse(
      localStorage.getItem(screenKey) ?? "null",
    );
    const name = object(saved) ? cleanName(saved.name) : null;
    if (object(saved) && screenIdValid(saved.id) && name) {
      if (typeof saved.chosen === "boolean")
        return { id: saved.id, name, chosen: saved.chosen };
      // Saved before names said whether they were chosen: the device's name was the default,
      // and becomes today's.
      const identity =
        name === deviceName()
          ? { id: saved.id, name: defaultScreenName(), chosen: false }
          : { id: saved.id, name, chosen: true };
      localStorage.setItem(screenKey, JSON.stringify(identity));
      return identity;
    }
    const fresh = unstored ?? made();
    localStorage.setItem(screenKey, JSON.stringify(fresh));
    unstored = null;
    return fresh;
  } catch {
    unstored ??= made();
    return unstored;
  }
}
// Each demo tab is a screen of its own: the browser's id and a suffix kept for the tab, with
// its own name once renamed there.
export function demoScreenIdentity(): Identity {
  const browser = screenIdentity();
  try {
    const saved: unknown = JSON.parse(sessionStorage.getItem(tabKey) ?? "null");
    const tab =
      object(saved) &&
      typeof saved.suffix === "string" &&
      /^[A-Za-z0-9_-]{8}$/.test(saved.suffix)
        ? { suffix: saved.suffix, name: cleanName(saved.name) }
        : { suffix: random(6), name: null };
    sessionStorage.setItem(tabKey, JSON.stringify(tab));
    return {
      id: `${browser.id}-${tab.suffix}`,
      name: tab.name ?? browser.name,
      chosen: tab.name !== null || browser.chosen,
    };
  } catch {
    unstored ??= made();
    return unstored;
  }
}
// Calls back with the screen's new name when another tab of this browser renames the screen
// `id` they share; answers how to stop. A page kept in the back-forward cache hears no storage
// event, so it also looks again whenever it is shown.
export function watchScreenName(id: string, renamed: (name: string) => void) {
  const follow = (value: string | null) => {
    if (!value) return;
    try {
      const saved: unknown = JSON.parse(value);
      const name =
        object(saved) && saved.id === id ? cleanName(saved.name) : null;
      if (name) renamed(name);
    } catch {
      // Not a screen's identity; nothing to follow.
    }
  };
  const changed = (e: StorageEvent) => {
    if (e.key === screenKey) follow(e.newValue);
  };
  const shown = () => {
    if (document.hidden) return;
    try {
      follow(localStorage.getItem(screenKey));
    } catch {
      // Storage can be unavailable; the next listen's answer still tells.
    }
  };
  window.addEventListener("storage", changed);
  window.addEventListener("pageshow", shown);
  document.addEventListener("visibilitychange", shown);
  return () => {
    window.removeEventListener("storage", changed);
    window.removeEventListener("pageshow", shown);
    document.removeEventListener("visibilitychange", shown);
  };
}
// A name a person chose, kept where the identity is: the tab's in a demo, else the browser's.
export function saveScreenName(name: string, demo: boolean) {
  try {
    if (demo) {
      const saved: unknown = JSON.parse(
        sessionStorage.getItem(tabKey) ?? "null",
      );
      if (object(saved))
        sessionStorage.setItem(tabKey, JSON.stringify({ ...saved, name }));
    } else
      localStorage.setItem(
        screenKey,
        JSON.stringify({ ...screenIdentity(), name, chosen: true }),
      );
  } catch {
    // Storage can be unavailable; the name still applies for this visit.
  }
  if (unstored) unstored = { ...unstored, name, chosen: true };
}
// The name screen `id` goes by, given `name`, the one the engine keeps: that one while this
// browser is still on its default name (the default, numbered when another screen has it), taken
// unless another tab took it first; else the name a person chose, as this browser keeps it now
// (another tab may have chosen it since this one last heard), which no answer replaces. Null for
// a screen that isn't this browser's.
// Each answer comes here, so it also says a listen with an id taken after a refusal
// (takeNewScreenId) was answered: that id is this login's from now on.
export function adoptScreenName(id: string, name: string): string | null {
  try {
    const saved: unknown = JSON.parse(
      localStorage.getItem(screenKey) ?? "null",
    );
    const now = screenIdentity();
    if (now.id !== id) return null;
    const heard = object(saved) && saved.fresh === true;
    const goesBy = now.chosen ? now.name : name;
    if (goesBy !== now.name || heard)
      localStorage.setItem(screenKey, JSON.stringify({ ...now, name: goesBy }));
    return goesBy;
  } catch {
    if (!unstored || unstored.id !== id) return null;
    unstoredFresh = false;
    if (unstored.chosen) return unstored.name;
    unstored = { ...unstored, name };
    return name;
  }
}
// A new id for this browser's screen once the engine refused `refused` as another app login's
// (docs/dev/screens.md 3.2): a viewer session started or ended here, so the screen this browser
// listened as is the other login's. A name a person chose stays; a default name starts again
// from the device's, which the engine numbers anew. Answers the identity to listen as: the one
// another tab took already, when it has. Null when `refused` was itself taken this way and no
// listen with it was answered since: that refusal is not about another login, so no other id
// is taken for it, and the listen backs off as after any failure.
export function takeNewScreenId(refused: string): Identity | null {
  const next = (now: Identity): Identity => ({
    id: newScreenId(),
    name: now.chosen ? now.name : defaultScreenName(),
    chosen: now.chosen,
  });
  try {
    const saved: unknown = JSON.parse(
      localStorage.getItem(screenKey) ?? "null",
    );
    const now = screenIdentity();
    if (now.id !== refused) return now;
    if (object(saved) && saved.fresh === true) return null;
    const taken = next(now);
    localStorage.setItem(screenKey, JSON.stringify({ ...taken, fresh: true }));
    return taken;
  } catch {
    if (!unstored) return null;
    if (unstored.id !== refused) return unstored;
    if (unstoredFresh) return null;
    unstored = next(unstored);
    unstoredFresh = true;
    return unstored;
  }
}

// The tile map, per screen, beside the layout.
const tilesKey = "rhythm-of-life.tiles";
export function savedTiles(): Tiles | null {
  try {
    return cleanTiles(JSON.parse(localStorage.getItem(tilesKey) ?? "null"));
  } catch {
    return null;
  }
}
export function saveTiles(map: Tiles) {
  try {
    localStorage.setItem(tilesKey, JSON.stringify(map));
  } catch {
    // The map still applies for this visit.
  }
}

// The Home grid, per screen, beside the layout (10.5): none until someone moves or resizes a
// part of Home here, and none again once a layout is chosen.
const gridKey = "rhythm-of-life.grid";
export function savedGrid(): HomeGrid | null {
  try {
    return cleanHomeGrid(JSON.parse(localStorage.getItem(gridKey) ?? "null"));
  } catch {
    return null;
  }
}
export function saveGrid(grid: HomeGrid | null) {
  try {
    if (grid) localStorage.setItem(gridKey, JSON.stringify(grid));
    else localStorage.removeItem(gridKey);
  } catch {
    // The grid still applies for this visit.
  }
}

// The layout, per screen, remembered on this browser; it holds no health or knowledge data.
const layoutKey = "rhythm-of-life.layout";
export function savedLayout(): Layout {
  try {
    const value = localStorage.getItem(layoutKey);
    if (isLayout(value)) return value;
  } catch {
    // Storage can be unavailable (private windows); the default still works.
  }
  return "hero";
}
export function saveLayout(layout: Layout) {
  try {
    localStorage.setItem(layoutKey, layout);
  } catch {
    // The choice still applies for this visit.
  }
}
// This screen's Home, beside its layout: a dashboard's id, or nothing for the default.
const homeKey = "rhythm-of-life.home";
export function savedHome(): Home {
  try {
    return (
      cleanHome(JSON.parse(localStorage.getItem(homeKey) ?? "null")) ?? null
    );
  } catch {
    return null;
  }
}
export function saveHome(home: Home) {
  try {
    if (home) localStorage.setItem(homeKey, JSON.stringify(home));
    else localStorage.removeItem(homeKey);
  } catch {
    // The choice still applies for this visit.
  }
}
// Automatic links on the sky (the owner, 2026-11-09; first "Similar links on the sky", whose key
// it keeps, so an "off" chosen then holds), per screen beside the layout: on unless someone
// turned them off in Sources & settings here. Display only, so it stays on this browser: nothing
// is sent to the engine, and the map and its links are unchanged.
const similarKey = "rhythm-of-life.similar-links";
export function savedAutomaticLinks(): boolean {
  try {
    return localStorage.getItem(similarKey) !== "off";
  } catch {
    return true;
  }
}
export function saveAutomaticLinks(on: boolean) {
  try {
    if (on) localStorage.removeItem(similarKey);
    else localStorage.setItem(similarKey, "off");
  } catch {
    // The choice still applies for this visit.
  }
}
// Calls back when another tab of this browser changes the settings they keep for their screen
// (the layout, the tiles, the Home, the Home grid or the automatic links); answers how to stop.
export function watchSettings(changed: () => void) {
  const keys = [layoutKey, tilesKey, homeKey, gridKey, similarKey];
  const listener = (e: StorageEvent) => {
    if (e.key !== null && keys.includes(e.key)) changed();
  };
  window.addEventListener("storage", listener);
  return () => window.removeEventListener("storage", listener);
}
// Whether this browser keeps its own copy of the settings; a cleared one takes the engine's.
export function settingsStored() {
  try {
    return (
      isLayout(localStorage.getItem(layoutKey)) ||
      localStorage.getItem(tilesKey) !== null ||
      localStorage.getItem(homeKey) !== null ||
      localStorage.getItem(gridKey) !== null
    );
  } catch {
    return false;
  }
}
