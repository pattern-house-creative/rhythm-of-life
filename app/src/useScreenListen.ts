import { useEffect, useLayoutEffect, useRef } from "react";
import type { GraphSession } from "./liveGraph";
import { listenTaken } from "./screens";
import { acquireScreenRunner } from "./screenRunner";
import type {
  ListenAnswer,
  ScreenCommand,
  ScreenSettings,
  Showing,
} from "./screens";

// After a failed listen, wait this long before the next: 1, 2, 5, 10, then 30 seconds.
export const listenBackoff = [1000, 2000, 5000, 10000, 30000];
// Someone touching this screen reaches the engine within this long, not with the next listen up
// to 25 seconds later, so an assistant can tell which screen a person is at (Q-147).
export const touchedMs = 10_000;
// A name a person chose that the engine doesn't keep goes to it again as a rename at most this
// often, until an answer carries it.
export const renameAgainMs = 60_000;

export interface ScreenHandlers {
  // What this screen shows now, sent with each listen and each outcome.
  showing: () => Showing;
  // This screen's settings when they are to be sent (the first listen, if this browser has its
  // own copy, and after a change), and a mark that confirms them once they were.
  settings: () => {
    value: ScreenSettings;
    mark: string;
  } | null;
  settingsSent: (mark: string) => void;
  // The engine's copy, from the first answer, for a browser that has none of its own.
  adopt: (settings: Record<string, unknown>) => void;
  // The engine's copy from every later answer, with the settings that listen carried (null
  // when none): a copy that no longer follows this browser's has it send them again.
  kept?: (
    settings: Record<string, unknown>,
    carried: ScreenSettings | null,
  ) => void;
  // The name the engine keeps for this screen, from every answer: numbered when another
  // screen had the default name first. Answers the name this screen goes by now: that one,
  // unless a person chose its name (in this tab or another); null to keep its own.
  named: (name: string) => string | null;
  // Applies one command and says how it went.
  apply: (command: ScreenCommand) => Promise<{
    result: "shown" | "failed";
    detail: Record<string, unknown>;
  }>;
  // The engine refused a listen because this screen's id listens with another app login (only
  // while viewer sessions are on, docs/dev/screens.md 3.2): takes a new id, which the next
  // listen goes out with, and answers whether it did (or another tab took one). Else that listen
  // failed as any other.
  taken?: (refused: string) => boolean;
}

// Keeps one listen waiting for this screen's next command while the app has a session, the page
// is visible and the browser online, as useMapRebuild and useDashboards follow the page: hidden,
// pagehide and offline stop it, pageshow, visible and online start it again, and lost access
// stops it until a new session. Each command is applied, then its outcome reported.
//
// With an engine the tabs of one browser share its screen, so one of them listens at a time (a
// lock named after the screen) and the others stand by, ready when it stops: every command, and
// the Undo that follows, reaches the same tab. Where the browser has no locks, each tab listens.
//
// What a screen shows goes with each listen, which waits up to 25 seconds. A touch (a pointer,
// a key) while one waits goes to the engine in a short listen beside it, one that holds for no
// time, at most once every touchedMs: the waiting listen is never cut short, since the engine
// may already have given it a command on its way here. A command the short one claims is
// applied in turn, as any other.
//
// A listen refused because this screen's id is another app login's (the browser's login changed:
// a viewer session started or ended) ends this loop once a new id is taken; the hook starts
// again with it.
//
// Each listen carries the name this screen goes by. A listen never strips the number the
// engine gave a name, so while the engine keeps another than the one a person chose (a rename
// that never reached it, a listen sent before the rename that arrived after it, or a number the
// engine gave a screen it no longer knew), the listening tab sends that name again as a rename,
// at most once every renameAgainMs, until an answer carries it.
export function useScreenListen(
  session: GraphSession | null,
  screen: { id: string; name: string },
  handlers: ScreenHandlers,
) {
  // The name each listen carries: App's, or the one an answer says this screen goes by (the
  // engine's, or a name chosen in another tab) from that moment, since the next listen goes out
  // before App draws it and would send the old one back. A render still carrying the old name
  // doesn't put it back; a new one from App does.
  const latest = useRef(handlers),
    name = useRef(screen.name),
    given = useRef(screen.name);
  useLayoutEffect(() => {
    latest.current = handlers;
    if (screen.name !== given.current)
      name.current = given.current = screen.name;
  });
  const id = screen.id;
  useEffect(() => {
    const listen = session?.screenListen,
      done = session?.screenDone,
      rename = session?.screenRename;
    if (!listen) return;
    // The running loop's number (0 when none), so a stop and a quick start never leave two.
    let loop = 0,
      count = 0,
      off = false,
      first = true,
      failures = 0,
      flight: AbortController | null = null,
      standing: AbortController | null = null,
      timer = 0,
      resume: (() => void) | null = null,
      // The listen waiting now, when it or the last touch's report was sent, and the touch
      // neither carries yet.
      waiting: AbortController | null = null,
      sentAt = 0,
      touchedAt = 0,
      touch = 0,
      // Commands are applied and reported one at a time, whichever listen claimed them.
      turn: Promise<void> = Promise.resolve(),
      gone = false,
      // When a name the engine didn't keep last went to it again.
      renamedAt = -Infinity;
    // What every answer says: the settings it carried arrived, the engine's own copy comes
    // with the first (and says with each later one whether it still follows this browser's),
    // and its name for this screen with each.
    const answered = (
      answer: ListenAnswer,
      settings: { value: ScreenSettings; mark: string } | null,
    ) => {
      if (settings) latest.current.settingsSent(settings.mark);
      if (first) {
        first = false;
        latest.current.adopt(answer.screen.settings);
      } else
        latest.current.kept?.(answer.screen.settings, settings?.value ?? null);
      const goesBy = latest.current.named(answer.screen.name);
      if (!goesBy) return;
      name.current = goesBy;
      if (
        goesBy === answer.screen.name ||
        Date.now() - renamedAt < renameAgainMs
      )
        return;
      renamedAt = Date.now();
      // Best effort: one that didn't arrive goes again with a later answer.
      void rename?.(id, goesBy).catch(() => {});
    };
    // Applies one command once those before it are done, and reports how it went.
    const handle = (command: ScreenCommand) => {
      const mine = turn.then(async () => {
        const outcome = await latest.current.apply(command);
        // Best effort: an outcome that didn't arrive leaves the command delivered.
        await done?.({
          screen: id,
          command: command.id,
          result: outcome.result,
          detail: outcome.detail,
          showing: latest.current.showing(),
        }).catch(() => {});
      });
      turn = mine.catch(() => {});
      return mine;
    };
    // The touch goes out in a short listen beside the waiting one, with what the screen shows
    // now and any settings not sent yet.
    const report = () => {
      touch = 0;
      if (!waiting || sentAt >= touchedAt) return;
      const settings = latest.current.settings();
      sentAt = Date.now();
      void listen({
        screen: id,
        name: name.current,
        hold: 0,
        showing: latest.current.showing(),
        ...(settings ? { settings: settings.value } : {}),
      })
        .then((answer) => {
          // A refusal is the waiting listen's to act on.
          if (!answer || listenTaken(answer) || gone) return;
          answered(answer, settings);
          if (answer.command) void handle(answer.command).catch(() => {});
        })
        .catch(() => {});
    };
    const touched = () => {
      touchedAt = Date.now();
      if (!waiting || touch) return;
      touch = window.setTimeout(
        report,
        Math.max(0, sentAt + touchedMs - Date.now()),
      );
    };
    const pause = (ms: number) =>
      new Promise<void>((resolve) => {
        resume = resolve;
        timer = window.setTimeout(resolve, ms);
      });
    const stop = () => {
      loop = 0;
      standing?.abort();
      standing = null;
      flight?.abort();
      flight = null;
      clearTimeout(timer);
      clearTimeout(touch);
      touch = 0;
      resume?.();
      resume = null;
    };
    const run = async (mine: number) => {
      const release = acquireScreenRunner(id);
      try {
        while (loop === mine) {
          const controller = new AbortController();
          flight = controller;
          const settings = latest.current.settings(),
            started = Date.now();
          try {
            waiting = controller;
            sentAt = started;
            const answer = await listen(
              {
                screen: id,
                name: name.current,
                hold: 25,
                showing: latest.current.showing(),
                ...(settings ? { settings: settings.value } : {}),
              },
              controller.signal,
            );
            waiting = null;
            if (controller.signal.aborted) return;
            // Screens are off on this install: nothing to listen for.
            if (!answer) {
              off = true;
              return stop();
            }
            if (listenTaken(answer)) {
              if (latest.current.taken?.(id)) return stop();
              throw new Error("That screen listens with another login");
            }
            failures = 0;
            answered(answer, settings);
            if (answer.command) await handle(answer.command);
            else if (Date.now() - started < 1000)
              // An empty answer that didn't wait (it should hold for a while) never spins.
              await pause(1000);
          } catch {
            if (controller.signal.aborted || loop !== mine) return;
            await pause(
              listenBackoff[Math.min(failures++, listenBackoff.length - 1)],
            );
          } finally {
            if (flight === controller) flight = null;
            if (waiting === controller) waiting = null;
          }
        }
      } finally {
        release();
      }
    };
    const start = () => {
      if (loop || off || document.hidden || !navigator.onLine) return;
      const mine = (loop = ++count);
      if (!navigator.locks) return void run(mine);
      // Waits its turn; a stop leaves the queue, and the lock goes when the loop ends.
      standing = new AbortController();
      navigator.locks
        .request(
          `rhythm-of-life.screen:${id}`,
          { signal: standing.signal },
          () => run(mine),
        )
        .catch(() => {});
    };
    const visibility = () => (document.hidden ? stop() : start());
    const accessLost = () => {
      off = true;
      stop();
    };
    const events = {
      pagehide: stop,
      pageshow: start,
      offline: stop,
      online: start,
      "observatory-access-lost": accessLost,
    };
    // The same signs of someone there as App's activeAt.
    const touches = ["pointerdown", "pointermove", "keydown", "touchstart"];
    const options = { capture: true, passive: true };
    document.addEventListener("visibilitychange", visibility);
    for (const [event, handler] of Object.entries(events))
      window.addEventListener(event, handler);
    for (const e of touches) window.addEventListener(e, touched, options);
    start();
    return () => {
      gone = true;
      stop();
      session?.screenStop?.();
      document.removeEventListener("visibilitychange", visibility);
      for (const [event, handler] of Object.entries(events))
        window.removeEventListener(event, handler);
      for (const e of touches) window.removeEventListener(e, touched, options);
    };
  }, [session, id]);
}
