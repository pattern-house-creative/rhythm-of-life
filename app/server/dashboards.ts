import {
  cleanPins,
  cleanCards,
  cleanDashboard,
  cleanDashboardData,
  cleanDashboardMap,
  cleanLayoutAnswer,
  cleanWriteAnswer,
  corners,
  dashboardIdValid,
  listedSummaries,
} from "../src/dashboards.js";
import type { Corner } from "../src/dashboards.js";
import { layoutProblem, partIdValid } from "../src/parts.js";
import { object } from "../src/validation.js";
import { json, readJson, requestGuard } from "./http.js";
import {
  answerRejected,
  callTool,
  EngineError,
  engineMode,
  hasPiece3,
} from "./engine.js";
import type { EngineReason } from "./engine.js";

// The engine reads at most 32 KiB of a request; a change, with its call
// around it, stays under that.
const maxChange = 30_000;
const writeName = (v: unknown): v is string =>
  typeof v === "string" && /^[a-z][A-Za-z0-9_]{0,39}$/.test(v);
const versionValid = (v: unknown): v is number =>
  Number.isInteger(v) && (v as number) >= 1 && (v as number) <= 10000;
// A layout change's note: the change in plain words, on one line, 1-500
// characters once trimmed (the engine counts UTF-16 units, so this does too).
const noteValid = (v: unknown): v is string =>
  typeof v === "string" &&
  v.trim().length >= 1 &&
  v.trim().length <= 500 &&
  !/\p{Cc}/u.test(v);
const unchanged = (reason: string) => json({ ok: false, reason });
// An arrangement that didn't go through for now, or whose answer was lost: trying again later may
// work, so a screen's Undo keeps it (10.6).
const notNow = (reason: string) => json({ ok: false, reason, transient: true });
// Failures where the change never reached the engine or the engine refused it before making it.
// Anything else (no answer in time, a broken connection, a gateway's error, an answer that can't
// be read) may come after the engine made the change, and a write sent again is a second write.
const refusals: ReadonlySet<EngineReason> = new Set([
  "not_configured",
  "credential_expired",
  "not_connected",
  "engine_auth",
  "forbidden",
  "invalid_arguments",
  "not_found",
]);

// Saved dashboards, read from the engine (app_dashboards): the list (GET), one
// version with its page (?id=&version=), what that version's feeds hold now
// (&data=1), read on request, a map part's focused sky (&map=&cursor=), the
// corners showing a dashboard's card (?cards=1) or the pinned stars (?pins=1).
// POST makes one change: a page's declared write or its undo, edits on or off,
// a corner's card, a pin or how the parts are arranged. In demo
// mode (the browser keeps the fictional dashboard itself) the list is empty and
// the rest are unavailable. On an upstream failure the corners and the pins fail
// closed (none) and the rest are unavailable, the list included. Nothing is
// kept; a failure is logged by its kind only (engine.ts).
export async function handleDashboards(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  if (request.method === "POST") return change(request, fetcher);
  const rejected = requestGuard(request, "GET");
  if (rejected) return rejected;
  const url = new URL(request.url);
  const id = url.searchParams.get("id");
  const version = url.searchParams.get("version");
  const data = url.searchParams.get("data");
  const cards = url.searchParams.get("cards");
  const pins = url.searchParams.get("pins");
  const map = url.searchParams.get("map");
  const cursor = url.searchParams.get("cursor");
  const known = new Set([
    "id",
    "version",
    "data",
    "cards",
    "pins",
    "map",
    "cursor",
  ]);
  if (
    [...url.searchParams.keys()].some((k) => !known.has(k)) ||
    (cards !== null && (cards !== "1" || id !== null)) ||
    (pins !== null && (pins !== "1" || id !== null || cards !== null)) ||
    (id !== null && !dashboardIdValid(id)) ||
    (version !== null &&
      (id === null ||
        !/^[1-9][0-9]{0,4}$/.test(version) ||
        +version > 10000)) ||
    (data !== null && (id === null || data !== "1")) ||
    (map !== null && (id === null || data !== null || !partIdValid(map))) ||
    // A cursor as the engine pages the map: o: and at most five digits.
    (cursor !== null && (map === null || !/^o:[0-9]{1,5}$/.test(cursor)))
  )
    return json({ error: "Invalid request" }, 400);
  const allowed = engineMode() === "live";
  const read = (args: Record<string, unknown>) =>
    callTool("app_dashboards", args, request.signal, fetcher);
  if (map !== null) {
    // A map part's focused sky (docs/dev/screens.md 10.4): only from an engine with piece 3,
    // which an older one, or one whose tool list couldn't be read, is taken not to have.
    if (
      !allowed ||
      !(await hasPiece3("app_dashboards", request.signal, fetcher))
    )
      return json({ error: "Focused maps unavailable" }, 503);
    try {
      const focused = cleanDashboardMap(
        await read({
          id: id!.toLowerCase(),
          ...(version ? { version: +version } : {}),
          map,
          ...(cursor ? { cursor } : {}),
        }),
        map,
      );
      if (!focused) throw new Error();
      return json({ map: focused });
    } catch (error) {
      answerRejected("app_dashboards", error, request.signal);
      return json({ error: "Source response unavailable or invalid" }, 502);
    }
  }
  if (pins !== null) {
    try {
      if (allowed)
        return json({
          pins: cleanPins(await read({ pins: true })),
        });
    } catch (error) {
      // Fail closed: nothing pinned.
      answerRejected("app_dashboards", error, request.signal);
    }
    return json({ pins: [] });
  }
  if (cards !== null) {
    try {
      if (allowed)
        return json({
          cards: cleanCards(await read({ cards: true })),
        });
    } catch (error) {
      // Fail closed: every corner shows its stat card.
      answerRejected("app_dashboards", error, request.signal);
    }
    return json({ cards: {} });
  }
  if (id === null) {
    if (!allowed) return json({ dashboards: [] });
    try {
      return json({ dashboards: listedSummaries(await read({})) });
    } catch (error) {
      // A failed read is never an empty list, which the browser would take for
      // every dashboard deleted (a screen's Home among them); nor does it leak
      // provider errors.
      answerRejected("app_dashboards", error, request.signal);
      return json({ error: "Source response unavailable or invalid" }, 502);
    }
  }
  if (!allowed) return json({ error: "Dashboards unavailable" }, 503);
  try {
    const result = await read({
      id: id.toLowerCase(),
      ...(version ? { version: +version } : {}),
      ...(data ? { data: true } : {}),
    });
    if (data) {
      const now = cleanDashboardData(result);
      if (!now || now.dashboard.id !== id.toLowerCase()) throw new Error();
      return json(now);
    }
    const dashboard = cleanDashboard(result);
    if (!dashboard || dashboard.id !== id.toLowerCase()) throw new Error();
    return json({ dashboard });
  } catch (error) {
    // An unknown dashboard or version (the engine's 404) reads the same as an
    // outage; never a 401/403 here, which would read as lost access.
    answerRejected("app_dashboards", error, request.signal);
    return json({ error: "Source response unavailable or invalid" }, 502);
  }
}

// A screen's arrangement of a dashboard's newest version (docs/dev/screens.md
// 10.7): the layout, the layout revision the change started from, and the
// change in plain words. Only an engine whose tool list takes layouts gets it,
// a layout that breaks the rules never reaches it, and it is never tried twice
// (engine.ts): a second try after a lost answer would meet its own revision as
// stale. Always answers {ok: true, layout, layoutRevision} or {ok: false,
// reason}, with the layout as it stands when it changed since `base`.
async function arrange(
  request: Request,
  fetcher: typeof fetch,
  id: string,
  version: number,
  layout: Record<string, unknown>,
  base: number,
  note: string,
) {
  const problem = layoutProblem(layout, id);
  if (problem) return unchanged(`${problem}.`);
  const args = { id, version, layout, base, note };
  if (new TextEncoder().encode(JSON.stringify(args)).length > maxChange)
    return unchanged("That layout is too large to send.");
  if (engineMode() !== "live")
    return notNow("Dashboard changes are unavailable right now.");
  if (!(await hasPiece3("app_dashboards", request.signal, fetcher)))
    return notNow("This install's engine can't arrange dashboards yet.");
  try {
    const answer = cleanLayoutAnswer(
      await callTool("app_dashboards", args, request.signal, fetcher),
      id,
    );
    if (!answer) throw new Error();
    return json(answer);
  } catch (error) {
    answerRejected("app_dashboards", error, request.signal);
    return notNow(
      error instanceof EngineError && refusals.has(error.reason)
        ? "The engine couldn't arrange that right now; nothing was changed."
        : "The arrangement may have been saved: the engine's answer didn't arrive or couldn't be read. It shows here once the dashboard is read again.",
    );
  }
}

// One change, as {action: "write", id, version, name, change}, {action: "undo",
// write}, {action: "edits", id, version, allowed}, {action: "card", corner,
// dashboard}, {action: "pin", star, pinned} or {action: "layout", id, version,
// layout, base, note}. A write, an undo or a layout always answers what
// happened, or why not, so the screen can say so, or that it may have happened.
async function change(request: Request, fetcher: typeof fetch) {
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let body: unknown;
  try {
    body = await readJson(request);
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  const b = object(body) ? body : null;
  const keys = b ? Object.keys(b).sort().join(",") : "";
  let args: Record<string, unknown>;
  if (
    b?.action === "write" &&
    keys === "action,change,id,name,version" &&
    dashboardIdValid(b.id) &&
    versionValid(b.version) &&
    writeName(b.name) &&
    object(b.change)
  )
    args = {
      id: b.id.toLowerCase(),
      version: b.version,
      write: { name: b.name, change: b.change },
    };
  else if (
    b?.action === "undo" &&
    keys === "action,write" &&
    dashboardIdValid(b.write)
  )
    args = { undo: b.write.toLowerCase() };
  else if (
    b?.action === "edits" &&
    keys === "action,allowed,id,version" &&
    dashboardIdValid(b.id) &&
    versionValid(b.version) &&
    typeof b.allowed === "boolean"
  )
    args = {
      id: b.id.toLowerCase(),
      version: b.version,
      allowEdits: b.allowed,
    };
  else if (
    b?.action === "card" &&
    keys === "action,corner,dashboard" &&
    corners.includes(b.corner as Corner) &&
    (b.dashboard === null || dashboardIdValid(b.dashboard))
  )
    args = {
      corner: b.corner,
      dashboard:
        b.dashboard === null ? null : (b.dashboard as string).toLowerCase(),
    };
  else if (
    b?.action === "pin" &&
    keys === "action,pinned,star" &&
    typeof b.star === "string" &&
    b.star.length >= 1 &&
    b.star.length <= 1000 &&
    typeof b.pinned === "boolean"
  )
    args = { pin: b.star, pinned: b.pinned };
  else if (
    b?.action === "layout" &&
    keys === "action,base,id,layout,note,version" &&
    dashboardIdValid(b.id) &&
    versionValid(b.version) &&
    object(b.layout) &&
    Number.isInteger(b.base) &&
    (b.base as number) >= 0 &&
    (b.base as number) <= 1_000_000 &&
    noteValid(b.note)
  )
    return arrange(
      request,
      fetcher,
      b.id.toLowerCase(),
      b.version,
      b.layout,
      b.base as number,
      b.note.trim(),
    );
  else return json({ error: "Invalid request" }, 400);
  const writing = b!.action === "write" || b!.action === "undo";
  if (
    writing &&
    new TextEncoder().encode(JSON.stringify(args)).length > maxChange
  )
    return unchanged(
      "That change is too large to send at once; split it into smaller ones.",
    );
  if (engineMode() !== "live")
    return writing
      ? unchanged("Dashboard changes are unavailable right now.")
      : json({ error: "Dashboards unavailable" }, 503);
  try {
    const result = await callTool(
      "app_dashboards",
      args,
      request.signal,
      fetcher,
    );
    if (writing) {
      const answer = cleanWriteAnswer(result);
      if (!answer) throw new Error();
      return json(answer);
    }
    if (b!.action === "edits") {
      if (!object(result) || typeof result.editsAllowed !== "boolean")
        throw new Error();
      return json({ editsAllowed: result.editsAllowed });
    }
    if (b!.action === "pin") return json({ pins: cleanPins(result) });
    return json({ cards: cleanCards(result) });
  } catch (error) {
    answerRejected("app_dashboards", error, request.signal);
    // Never a 401/403 here, which would read as lost access.
    if (!writing)
      return json({ error: "Source response unavailable or invalid" }, 502);
    return unchanged(
      error instanceof EngineError && refusals.has(error.reason)
        ? "The engine couldn't make that change right now; nothing was changed."
        : "The change may have been made: the engine's answer didn't arrive or couldn't be read. Check before trying again.",
    );
  }
}
