import {
  cleanCommand,
  cleanDetail,
  cleanHome,
  cleanListen,
  cleanName,
  cleanRenamed,
  cleanScreens,
  cleanShowing,
  commandArgs,
  screenIdValid,
  settingsValid,
} from "../src/screens.js";
import { dashboardIdValid } from "../src/dashboards.js";
import { cleanHomeGrid } from "../src/parts.js";
import { object } from "../src/validation.js";
import { settings } from "./config.js";
import { json, readJson, requestGuard } from "./http.js";
import {
  answerRejected,
  callTool,
  EngineError,
  engineMode,
  forgetTools,
  hasPiece3,
} from "./engine.js";
import type { EngineReason } from "./engine.js";
import { handleStudio } from "./studio.js";

// Live screens (docs/dev/screens.md section 4): POST /api/screens carries one app_screens call,
// {op, ...}, with exactly the keys that op takes. listen holds for up to 25 seconds while the
// engine waits for a command; send stores one for another screen and waits up to 8 seconds for
// its outcome. In demo mode (no engine) every op answers {available: false} and the browser
// keeps its screens itself; so does an engine without app_screens, and then there are none. A
// failure is never a 401/403 here, which would read as lost access. A screen's Home grid and
// the commands that arrange parts go only to an engine whose tool list says it takes them.
// While viewer sessions are on (app.viewers), a listen the engine refuses because the screen's
// id is another app login's (3.2: this browser's login changed) answers {taken: true}, and the
// browser takes a new id; while they are off no login is refused, and that is as before.

// Each op's keys: those it needs, and those it may add. send also takes its action's own
// arguments (section 2), as `args` or beside these, checked by commandArgs.
const shapes: Record<string, [required: string[], optional: string[]]> = {
  listen: [
    ["op", "screen", "name"],
    ["hold", "showing", "settings"],
  ],
  done: [
    ["op", "screen", "command", "result"],
    ["detail", "showing"],
  ],
  send: [["op", "screen", "to", "action"], []],
  list: [["op", "screen"], []],
  rename: [["op", "screen", "name"], []],
};
// Failures where the command never reached the engine or the engine refused it before storing
// it. Anything else may come after it was stored, and a send repeated is a second command.
const refusals: ReadonlySet<EngineReason> = new Set([
  "not_configured",
  "credential_expired",
  "engine_auth",
  "forbidden",
  "invalid_arguments",
  "not_found",
]);
// The engine's reasons for refusing a send (migration 020's functions and the app endpoint), in
// the app's words. Any other refusal gets a general one: the engine's own text never reaches the
// browser as it is.
export const refusedBecause: Record<string, string> = {
  "No open screen has that id.": "It wasn't sent: that screen isn't open now.",
  "That screen already has 20 commands waiting.":
    "It wasn't sent: that screen already has 20 commands waiting.",
  "That star is not on the map now.":
    "It wasn't sent: that star isn't on the map now.",
  "Unknown dashboard.": "It wasn't sent: that dashboard doesn't exist now.",
  "Unknown sending screen.":
    "It wasn't sent: this screen hasn't reached the engine yet. Try again in a moment.",
};
const explained = (said: string | null) =>
  said !== null && Object.hasOwn(refusedBecause, said)
    ? refusedBecause[said]
    : null;
// How long a listen may wait for a command, in seconds.
const holdValid = (v: unknown) =>
  typeof v === "number" && Number.isInteger(v) && v >= 0 && v <= 25;
const unavailable = () => json({ error: "Screens unavailable" }, 502);
const viewers = () => {
  try {
    return settings().viewers;
  } catch {
    return false;
  }
};
// The commands that arrange parts (docs/dev/screens.md 10.6), which only an engine with piece 3
// takes.
const arranging = new Set(["move", "resize", "focus_map"]);

// The engine's arguments for one checked request, or null when it isn't one.
function argumentsOf(b: Record<string, unknown>) {
  const shape =
    typeof b.op === "string" && Object.hasOwn(shapes, b.op)
      ? shapes[b.op]
      : undefined;
  if (!shape || !screenIdValid(b.screen)) return null;
  const [required, optional] = shape;
  const extra = Object.keys(b).filter(
    (k) => !required.includes(k) && !optional.includes(k),
  );
  if (required.some((k) => !Object.hasOwn(b, k))) return null;
  switch (b.op) {
    case "listen": {
      // What it shows and keeps go on with only their own fields, cut to fit (src/screens.ts).
      const name = cleanName(b.name),
        showing = b.showing === undefined ? undefined : cleanShowing(b.showing);
      if (
        extra.length ||
        !name ||
        (b.hold !== undefined && !holdValid(b.hold)) ||
        showing === null ||
        (b.settings !== undefined && !settingsValid(b.settings))
      )
        return null;
      return {
        op: "listen",
        screen: b.screen,
        name,
        ...(b.hold !== undefined ? { hold: b.hold } : {}),
        ...(showing ? { showing } : {}),
        ...(b.settings !== undefined
          ? {
              settings: {
                layout: b.settings.layout,
                tiles: b.settings.tiles,
                ...(b.settings.home !== undefined
                  ? { home: cleanHome(b.settings.home) }
                  : {}),
                // The Home grid in Home's id order (10.5); dropped below for an older engine.
                ...(b.settings.grid !== undefined
                  ? { grid: cleanHomeGrid(b.settings.grid) }
                  : {}),
              },
            }
          : {}),
      };
    }
    case "done": {
      const detail = b.detail === undefined ? undefined : cleanDetail(b.detail),
        showing = b.showing === undefined ? undefined : cleanShowing(b.showing);
      if (
        extra.length ||
        !dashboardIdValid(b.command) ||
        (b.result !== "shown" && b.result !== "failed") ||
        detail === null ||
        showing === null
      )
        return null;
      return {
        op: "done",
        screen: b.screen,
        command: b.command.toLowerCase(),
        result: b.result,
        ...(detail ? { detail } : {}),
        ...(showing ? { showing } : {}),
      };
    }
    case "send": {
      // The action's arguments come as one `args` object (move_tile's own `to` needs it) or
      // beside send's own keys, never both, and go to the engine as `args`.
      const given = !Object.hasOwn(b, "args")
        ? Object.fromEntries(extra.map((k) => [k, b[k]]))
        : extra.length === 1 && object(b.args)
          ? b.args
          : null;
      const args = given && commandArgs(b.action, given);
      if (!screenIdValid(b.to) || !args) return null;
      return { op: "send", screen: b.screen, to: b.to, action: b.action, args };
    }
    case "rename": {
      const name = cleanName(b.name);
      return !extra.length && name
        ? { op: "rename", screen: b.screen, name }
        : null;
    }
    default:
      return extra.length ? null : { op: "list", screen: b.screen };
  }
}

export async function handleScreens(
  request: Request,
  fetcher: typeof fetch = fetch,
) {
  if (new URL(request.url).searchParams.get("studio") === "1")
    return handleStudio(request, fetcher);
  const rejected = requestGuard(request, "POST");
  if (rejected) return rejected;
  let body: unknown;
  try {
    body = await readJson(request);
  } catch {
    return json({ error: "Invalid or oversized request" }, 400);
  }
  const args = object(body) ? argumentsOf(body) : null;
  if (!args) return json({ error: "Invalid request" }, 400);
  if (engineMode() !== "live") return json({ available: false });
  const sending = args.op === "send";
  // An engine older than piece 3 refuses a Home grid and the commands that arrange parts
  // (docs/dev/screens.md 10.10): the grid is left out (the browser keeps it alone), and those
  // commands aren't sent.
  const settings: Record<string, unknown> | undefined =
    "settings" in args && object(args.settings) ? args.settings : undefined;
  const withoutGrid = () => ({
    ...args,
    settings: Object.fromEntries(
      Object.entries(settings ?? {}).filter(([key]) => key !== "grid"),
    ),
  });
  let call: Record<string, unknown> = args;
  if (
    (settings?.grid !== undefined ||
      (sending && arranging.has(String(args.action)))) &&
    !(await hasPiece3("app_screens", request.signal, fetcher))
  ) {
    if (sending)
      return json({
        ok: false,
        reason:
          "It wasn't sent: this install's engine can't arrange parts yet.",
      });
    call = withoutGrid();
  }
  try {
    // One try, never repeated: a listen claims a command and a send stores one (engine.ts).
    const result = await callTool(
      "app_screens",
      call,
      request.signal,
      fetcher,
    ).catch((error: unknown) => {
      // A listen refused while it carried a grid claimed nothing: the engine's listing was
      // wrong or old, so it is read again next time, and the listen goes once more without.
      if (
        object(call.settings) &&
        call.settings.grid !== undefined &&
        error instanceof EngineError &&
        error.reason === "invalid_arguments"
      ) {
        forgetTools();
        return callTool("app_screens", withoutGrid(), request.signal, fetcher);
      }
      throw error;
    });
    switch (args.op) {
      case "listen": {
        const answer = cleanListen(result);
        if (!answer) throw new Error();
        return json(answer);
      }
      case "done":
        if (result.ok !== true) throw new Error();
        return json({ ok: true });
      case "send": {
        const command = cleanCommand(result.command);
        if (!command) throw new Error();
        return json({ command });
      }
      case "rename": {
        const screen = cleanRenamed(result);
        if (!screen) throw new Error();
        return json({ screen });
      }
      default: {
        const screens = cleanScreens(result);
        if (!screens) throw new Error();
        return json({ screens });
      }
    }
  } catch (error) {
    // An engine that doesn't list app_screens (one older than this app) has no screens: the
    // browser stops listening, as in demo mode, instead of retrying every 30 seconds.
    if (error instanceof EngineError && error.reason === "not_connected")
      return json({ available: false });
    if (
      args.op === "listen" &&
      error instanceof EngineError &&
      error.reason === "forbidden" &&
      viewers()
    )
      return json({ taken: true });
    answerRejected("app_screens", error, request.signal);
    if (!sending) return unavailable();
    // A send always answers what happened to it, or that it may have happened, and why the
    // engine refused it when that is a reason the app knows.
    const refused = error instanceof EngineError && refusals.has(error.reason);
    const reason = !refused
      ? "It may have been sent: the answer didn't arrive. Check that screen before sending again."
      : error.reason === "invalid_arguments" || error.reason === "not_found"
        ? (explained(error.said) ??
          "It wasn't sent: that screen isn't open now, or it can't show this.")
        : "It wasn't sent: screens are unavailable right now.";
    return json({ ok: false, reason });
  }
}
