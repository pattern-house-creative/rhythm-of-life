import { useCallback, useEffect, useRef, useState } from "react";
import { listedMax } from "./dashboards";
import type { Corner, DashboardCards, DashboardSummary } from "./dashboards";
import type { GraphSession } from "./liveGraph";
import { retryDelay } from "./retry";

// The saved dashboards and the corners showing their cards, read when the app
// opens, every 15 minutes and when it comes back into view, held in memory
// only and cleared when the tab is hidden, goes offline or loses access. A
// read that fails (the app's server restarting, say) leaves what was read
// last, the corners' cards included, and is tried again soon (retry.ts);
// `complete` says whether the list is the engine's whole answer (read, and
// shorter than the most it lists), so a dashboard missing from it is one that
// is gone. reload() answers once the read under way, its own or one already
// going, ends.
export function useDashboards(session: GraphSession | null) {
  const [list, setList] = useState<DashboardSummary[]>([]);
  const [complete, setComplete] = useState(false);
  const [cards, setCards] = useState<DashboardCards>({});
  const flight = useRef<{
      controller: AbortController;
      done: Promise<void>;
    } | null>(null),
    denied = useRef(false),
    failures = useRef(0),
    retry = useRef<ReturnType<typeof setTimeout> | undefined>(undefined),
    again = useRef(() => {});
  const reload = useCallback((): Promise<void> => {
    if (flight.current) return flight.current.done;
    if (
      !session?.dashboards ||
      document.hidden ||
      !navigator.onLine ||
      denied.current
    )
      return Promise.resolve();
    const controller = new AbortController();
    clearTimeout(retry.current);
    const read = async () => {
      try {
        const [listed, chosen] = await Promise.all([
          session.dashboards!(controller.signal),
          session.dashboardCards
            ? session.dashboardCards(controller.signal).catch(() => null)
            : Promise.resolve({}),
        ]);
        if (!controller.signal.aborted) {
          setList(listed);
          // Cards that couldn't be read stay as they were.
          if (chosen) setCards(chosen);
          setComplete(listed.length < listedMax);
          failures.current = 0;
        }
      } catch {
        if (!controller.signal.aborted)
          retry.current = setTimeout(
            () => again.current(),
            retryDelay(failures.current++),
          );
      } finally {
        if (flight.current?.controller === controller) flight.current = null;
      }
    };
    const done = read();
    flight.current = { controller, done };
    return done;
  }, [session]);
  useEffect(() => {
    denied.current = false;
    again.current = () => void reload();
    // Read the session's dashboards when it starts; the read itself is async.
    // eslint-disable-next-line react/set-state-in-effect
    void reload();
    const clear = () => {
      flight.current?.controller.abort();
      flight.current = null;
      clearTimeout(retry.current);
      failures.current = 0;
      setList([]);
      setCards({});
      setComplete(false);
    };
    const resume = () => {
      if (!document.hidden && navigator.onLine) void reload();
    };
    const visibility = () => (document.hidden ? clear() : resume());
    const accessLost = () => {
      denied.current = true;
      clear();
    };
    const interval = setInterval(() => void reload(), 900_000);
    document.addEventListener("visibilitychange", visibility);
    const events = {
      pagehide: clear,
      pageshow: resume,
      offline: clear,
      online: resume,
      "observatory-access-lost": accessLost,
    };
    for (const [event, handler] of Object.entries(events))
      window.addEventListener(event, handler);
    return () => {
      clearInterval(interval);
      clearTimeout(retry.current);
      failures.current = 0;
      flight.current?.controller.abort();
      flight.current = null;
      document.removeEventListener("visibilitychange", visibility);
      for (const [event, handler] of Object.entries(events))
        window.removeEventListener(event, handler);
    };
  }, [reload]);
  // Shows a dashboard's card in a corner, or (null) its stat card again.
  const setCard = useCallback(
    async (corner: Corner, dashboard: string | null) => {
      if (!session?.setDashboardCard) return;
      setCards(await session.setDashboardCard(corner, dashboard));
    },
    [session],
  );
  return { list, complete, cards, reload, setCard };
}
