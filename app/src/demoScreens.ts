import type { GraphSession } from "./liveGraph";
import { catalogProvider } from "./instance";
import {
  cleanCommand,
  commandArgs,
  screenIdValid,
  stampActive,
} from "./screens";
import type { ScreenCommand, ScreenSummary } from "./screens";
import { homeIds } from "./parts";
import type { HomeId } from "./parts";
import { object } from "./validation";

// The demo's screens (docs/dev/screens.md section 7): without an engine, this browser plays its
// part. Each tab is a screen; they find each other over a BroadcastChannel and pass commands
// with the engine's checks, expiry and outcomes. Nothing is stored: a closed tab is gone, and
// the others forget it. Where BroadcastChannel is missing, screens are off.

type Hub = Required<
  Pick<
    GraphSession,
    | "screenListen"
    | "screenDone"
    | "screenSend"
    | "screenList"
    | "screenRename"
    | "screenStop"
  >
>;
type Message =
  | { type: "who" }
  | { type: "seen"; id: string; name: string; showing: unknown; at: number }
  | { type: "bye"; id: string }
  | { type: "command"; to: string; command: ScreenCommand }
  | { type: "status"; command: ScreenCommand };

const channelName = "rhythm-of-life.screens";
// A screen is open while it was heard from in the last minute; a command not claimed within 30
// seconds expires, a screen holds at most 20, and a sender waits up to 8 seconds for an outcome.
const openMs = 60_000,
  expiryMs = 30_000,
  maxPending = 20,
  waitMs = 8_000,
  dayMs = 86_400_000;
const since = (sentAt: string, at: number) =>
  Math.max(0, at - Date.parse(sentAt));
const aborted = () => new DOMException("The listen was stopped", "AbortError");

// The commands that arrange parts (docs/dev/screens.md 10.6), checked as the engine checks them
// (10.7): without `dashboard`, it is filled in from what the target last reported (its
// dashboard, or null at its Home), so a screen that moved on since refuses the command; with
// one, the parts named must be among those the target reports for it, and at Home among its
// five, whose map takes no focus. The demo's dashboards live in each tab, so the target's own
// report is what this tab can check against.
const arranging = new Set(["move", "resize", "focus_map"]);
function guarded(args: Record<string, unknown>, showing: unknown) {
  if (Object.hasOwn(args, "dashboard") || !object(showing)) return args;
  const board = object(showing.dashboard) ? showing.dashboard.id : null;
  if (
    (showing.view === "dashboard" || showing.view === "full_screen") &&
    typeof board === "string"
  )
    return { ...args, dashboard: board };
  return showing.view === "home" ? { ...args, dashboard: null } : args;
}
function partRefusal(
  action: string,
  args: Record<string, unknown>,
  showing: unknown,
): string | null {
  const named = [args.part, args.with].filter(
    (p): p is string => typeof p === "string",
  );
  if (args.dashboard === null) {
    if (action === "focus_map") return "Home's map takes no focus.";
    const missing = named.find((p) => !homeIds.includes(p as HomeId));
    return missing ? `${missing} is not one of Home's parts.` : null;
  }
  const shown = object(showing) ? showing : {};
  const board = object(shown.dashboard) ? shown.dashboard.id : null;
  if (args.dashboard === undefined || board !== args.dashboard) return null;
  const parts = (Array.isArray(shown.parts) ? shown.parts : []).filter(object);
  const missing = named.find((p) => !parts.some((q) => q.id === p));
  if (missing) return `${missing} is not a part of that dashboard.`;
  if (action !== "focus_map") return null;
  const maps = parts.filter((q) => q.kind === "map");
  if (args.part === undefined)
    return maps.length === 1
      ? null
      : maps.length
        ? "that dashboard has more than one map; name the one to focus."
        : "that dashboard has no map.";
  return maps.some((q) => q.id === args.part)
    ? null
    : `${String(args.part)} is not a map.`;
}

export function createDemoScreens(): Hub | null {
  if (typeof BroadcastChannel === "undefined") return null;
  let channel: BroadcastChannel | null = null;
  // This tab's screen as its latest listen named it, and when that was.
  let me: { id: string; name: string; showing: unknown; at: number } | null =
    null;
  let settings: Record<string, unknown> = {};
  const peers = new Map<
    string,
    { name: string; showing: unknown; at: number }
  >();
  // Commands for this screen not claimed yet, oldest first, and those claimed.
  const inbox: ScreenCommand[] = [];
  const claimed = new Map<string, ScreenCommand>();
  // Commands this screen sent, and what waits on each one's outcome.
  const sent = new Map<string, ScreenCommand>();
  const waiting = new Map<string, () => void>();
  let wake: (() => void) | null = null;

  const post = (message: Message) => channel?.postMessage(message);
  const seen = () =>
    me &&
    post({
      type: "seen",
      id: me.id,
      name: me.name,
      showing: me.showing,
      at: me.at,
    });
  // The sender hears of every change to its command: claimed, shown, failed, expired.
  const tell = (command: ScreenCommand) => post({ type: "status", command });
  const receive = (event: MessageEvent<Message>) => {
    const m = event.data;
    // Another tab of this origin may run an older or newer app: anything else is ignored.
    if (!object(m)) return;
    if (m.type === "who") seen();
    else if (m.type === "seen" && screenIdValid(m.id) && m.id !== me?.id)
      peers.set(m.id, {
        name: typeof m.name === "string" ? m.name.slice(0, 60) : "Screen",
        showing: m.showing,
        at: Math.min(Date.now(), Number(m.at) || 0),
      });
    else if (m.type === "bye") peers.delete(m.id);
    else if (m.type === "command" && me && m.to === me.id) {
      const command = cleanCommand(m.command);
      if (!command) return;
      if (inbox.length >= maxPending)
        return tell({
          ...command,
          result: "failed",
          doneAt: new Date().toISOString(),
          detail: { summary: "Too many commands are waiting on that screen." },
        });
      inbox.push(command);
      wake?.();
    } else if (m.type === "status" && sent.has(m.command?.id)) {
      const command = cleanCommand(m.command);
      if (!command) return;
      sent.set(command.id, command);
      if (["shown", "failed", "expired"].includes(command.result))
        waiting.get(command.id)?.();
    }
  };
  const open = () => {
    if (channel) return;
    channel = new BroadcastChannel(channelName);
    channel.onmessage = receive;
    post({ type: "who" });
  };
  // Commands left too long are never shown; their senders hear so.
  const expire = () => {
    const now = Date.now();
    for (let i = inbox.length - 1; i >= 0; i--)
      if (since(inbox[i].sentAt, now) > expiryMs) {
        const [command] = inbox.splice(i, 1);
        tell({ ...command, result: "expired" });
      }
  };
  const claim = (): ScreenCommand | null => {
    expire();
    const next = inbox.shift();
    if (!next) return null;
    const now = Date.now();
    const command: ScreenCommand = {
      ...next,
      result: "delivered",
      deliveredAt: new Date(now).toISOString(),
      ms: { deliver: since(next.sentAt, now), show: null },
    };
    claimed.set(command.id, command);
    tell(command);
    return command;
  };
  const self = () => ({ id: me!.id, name: me!.name, settings });

  return {
    async screenListen(listen, signal) {
      if (signal?.aborted) throw aborted();
      open();
      me = {
        id: listen.screen,
        name: listen.name,
        showing: stampActive(listen.showing) ?? me?.showing ?? {},
        at: Date.now(),
      };
      if (listen.settings) settings = { ...listen.settings };
      seen();
      const now = claim();
      // One that holds for no time looks once, as the engine's does.
      if (now || (listen.hold ?? 25) <= 0)
        return { command: now, screen: self() };
      // Wait for a command, then look once more.
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(done, (listen.hold ?? 25) * 1000);
        const stop = () => {
          clearTimeout(timer);
          wake = null;
          reject(aborted());
        };
        function done() {
          clearTimeout(timer);
          signal?.removeEventListener("abort", stop);
          wake = null;
          resolve();
        }
        wake = done;
        signal?.addEventListener("abort", stop, { once: true });
      });
      return { command: claim(), screen: self() };
    },
    async screenDone(done) {
      const command = claimed.get(done.command);
      if (!command || command.result !== "delivered") return;
      claimed.delete(done.command);
      if (done.showing && me) me.showing = stampActive(done.showing);
      const now = Date.now();
      tell({
        ...command,
        result: done.result,
        doneAt: new Date(now).toISOString(),
        detail: done.detail ?? {},
        ms: { ...command.ms, show: since(command.sentAt, now) },
      });
    },
    async screenSend(send, signal) {
      const peer = peers.get(send.to);
      let args = commandArgs(send.action, send.args);
      let action = send.action;
      // A dashboard's star opens the dashboard, as the engine turns it.
      const dashboard =
        action === "open_star" &&
        typeof args?.star === "string" &&
        args.star.startsWith(`${catalogProvider()}:dashboard:`)
          ? args.star.split(":").at(-1)!
          : null;
      if (dashboard) {
        action = "open_dashboard";
        args = commandArgs(action, { dashboard });
      }
      if (!me || !args)
        return {
          ok: false,
          reason: "It wasn't sent: screens are unavailable right now.",
        };
      if (!peer || Date.now() - peer.at > openMs)
        return {
          ok: false,
          reason:
            "It wasn't sent: that screen isn't open now, or it can't show this.",
        };
      if (arranging.has(action)) {
        args = guarded(args, peer.showing);
        const refusal = partRefusal(action, args, peer.showing);
        if (refusal) return { ok: false, reason: `It wasn't sent: ${refusal}` };
      }
      const command: ScreenCommand = {
        id: crypto.randomUUID(),
        action,
        args,
        sender: { kind: "screen", screen: me.id, name: me.name },
        sentAt: new Date().toISOString(),
        deliveredAt: null,
        doneAt: null,
        result: "pending",
        detail: {},
        ms: { deliver: null, show: null },
      };
      sent.set(command.id, command);
      post({ type: "command", to: send.to, command });
      await new Promise<void>((resolve) => {
        const timer = setTimeout(finish, waitMs);
        function finish() {
          clearTimeout(timer);
          waiting.delete(command.id);
          signal?.removeEventListener("abort", finish);
          resolve();
        }
        waiting.set(command.id, finish);
        signal?.addEventListener("abort", finish, { once: true });
      });
      const now = sent.get(command.id)!;
      // Kept a little longer, so a late outcome still finds its command; then forgotten.
      setTimeout(() => sent.delete(command.id), expiryMs);
      return { ok: true, command: now };
    },
    async screenList(screen) {
      open();
      const now = Date.now();
      const list: ScreenSummary[] = [...peers]
        .filter(([, p]) => now - p.at < dayMs)
        .map(([id, p]) => ({
          id,
          name: p.name,
          open: now - p.at < openMs,
          seenAt: new Date(p.at).toISOString(),
          showing: object(p.showing) ? p.showing : {},
          self: false,
        }));
      if (me?.id === screen)
        list.push({
          id: me.id,
          name: me.name,
          open: true,
          seenAt: new Date(me.at).toISOString(),
          showing: object(me.showing) ? me.showing : {},
          self: true,
        });
      return list.sort((a, b) => b.seenAt.localeCompare(a.seenAt));
    },
    async screenRename(screen, name) {
      if (me?.id !== screen) return;
      me.name = name;
      seen();
    },
    screenStop() {
      if (me) post({ type: "bye", id: me.id });
      channel?.close();
      channel = null;
      wake = null;
      me = null;
      peers.clear();
      inbox.length = 0;
    },
  };
}
