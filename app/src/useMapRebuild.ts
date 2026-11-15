import { useCallback, useEffect, useLayoutEffect, useRef } from "react";
import type { GraphSession } from "./liveGraph";

// The engine picks a rebuild request up within a minute and rebuilds in about half a minute; the map
// and dashboards are read again then, and once more in case it waited behind the scheduled rebuild.
export const rebuildRereads = [75_000, 150_000];

// Refresh rebuilds the map: reads the map and dashboards again at once and asks the engine to rebuild
// the map now. Accepted, or one already waiting: read them again once it has had time to land.
// Too soon or unavailable: the regular schedule stands. Nothing waits across hide, offline or
// lost access.
export function useMapRebuild(session: GraphSession | null, reread: () => void) {
  const latest = useRef(reread),
    flight = useRef<AbortController | null>(null),
    timers = useRef<ReturnType<typeof setTimeout>[]>([]);
  useLayoutEffect(() => {
    latest.current = reread;
  }, [reread]);
  const cancel = useCallback(() => {
    flight.current?.abort();
    flight.current = null;
    for (const t of timers.current) clearTimeout(t);
    timers.current = [];
  }, []);
  useEffect(() => {
    const visibility = () => document.hidden && cancel();
    document.addEventListener("visibilitychange", visibility);
    const events = ["pagehide", "offline", "observatory-access-lost"];
    for (const event of events) window.addEventListener(event, cancel);
    return () => {
      cancel();
      document.removeEventListener("visibilitychange", visibility);
      for (const event of events) window.removeEventListener(event, cancel);
    };
  }, [session, cancel]);
  return useCallback(async () => {
    latest.current();
    if (
      !session?.requestRebuild ||
      flight.current ||
      document.hidden ||
      !navigator.onLine
    )
      return;
    const request = new AbortController();
    flight.current = request;
    try {
      const answer = await session.requestRebuild(request.signal);
      if (request.signal.aborted || !(answer.accepted || answer.pending))
        return;
      for (const t of timers.current) clearTimeout(t);
      timers.current = rebuildRereads.map((ms) =>
        setTimeout(() => latest.current(), ms),
      );
    } catch {
      // Rebuild now is best effort and never an error on screen.
    } finally {
      if (flight.current === request) flight.current = null;
    }
  }, [session]);
}
