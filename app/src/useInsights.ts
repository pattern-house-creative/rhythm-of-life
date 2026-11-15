import { useCallback, useEffect, useRef, useState } from "react";
import { currentInsight } from "./insights";
import type { DailyInsight, FeedbackValue, InsightRun } from "./insights";
import type { GraphSession } from "./liveGraph";
import { retryDelay } from "./retry";

const without = <T>(record: Record<string, T>, key: string) =>
  Object.fromEntries(Object.entries(record).filter(([k]) => k !== key));

// Insights are read when the app opens, on each refresh and every 15 minutes,
// held in memory only, and cleared when the tab is hidden, goes offline or
// loses access. A read that fails (the app's server restarting, say) leaves
// what was read last and is tried again soon (retry.ts). The engine's latest
// runs come with them, and go with them.
export function useInsights(session: GraphSession | null) {
  const [insights, setInsights] = useState<DailyInsight[]>([]);
  const [runs, setRuns] = useState<InsightRun[]>([]);
  // Feedback being saved, and the last one that did not save, by insight.
  const [pending, setPending] = useState<Record<string, FeedbackValue>>({});
  const [failed, setFailed] = useState<Record<string, FeedbackValue>>({});
  const [now, setNow] = useState(() => Date.now());
  const flight = useRef<AbortController | null>(null),
    denied = useRef(false),
    failures = useRef(0),
    retry = useRef<ReturnType<typeof setTimeout> | undefined>(undefined),
    again = useRef(() => {});
  const reload = useCallback(async () => {
    if (
      !session?.insights ||
      flight.current ||
      document.hidden ||
      !navigator.onLine ||
      denied.current
    )
      return;
    const controller = new AbortController();
    flight.current = controller;
    clearTimeout(retry.current);
    try {
      const read = await session.insights(controller.signal);
      if (!controller.signal.aborted) {
        setInsights(read.insights);
        setRuns(read.runs);
        failures.current = 0;
      }
    } catch {
      if (!controller.signal.aborted)
        retry.current = setTimeout(
          () => again.current(),
          retryDelay(failures.current++),
        );
    } finally {
      if (flight.current === controller) flight.current = null;
      setNow(Date.now());
    }
  }, [session]);
  useEffect(() => {
    denied.current = false;
    again.current = () => void reload();
    // Read the session's insights when it starts; the read itself is async.
    // eslint-disable-next-line react/set-state-in-effect
    void reload();
    const clear = () => {
      flight.current?.abort();
      flight.current = null;
      clearTimeout(retry.current);
      failures.current = 0;
      setInsights([]);
      setRuns([]);
      setPending({});
      setFailed({});
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
      flight.current?.abort();
      flight.current = null;
      clearInterval(interval);
      clear();
      document.removeEventListener("visibilitychange", visibility);
      for (const [event, handler] of Object.entries(events))
        window.removeEventListener(event, handler);
    };
  }, [reload]);
  const send = useCallback(
    async (id: string, value: FeedbackValue, until?: string, note?: string) => {
      if (!session?.insightFeedback || denied.current) return;
      setPending((p) => ({ ...p, [id]: value }));
      setFailed((f) => without(f, id));
      try {
        const updated = await session.insightFeedback(id, value, until, note);
        setInsights((list) =>
          list.map((i) => (i.id === updated.id ? updated : i)),
        );
      } catch {
        setFailed((f) => ({ ...f, [id]: value }));
      } finally {
        setPending((p) => without(p, id));
        setNow(Date.now());
      }
    },
    [session],
  );
  return {
    insights,
    runs,
    current: currentInsight(insights, now),
    reload,
    send,
    pending,
    failed,
  };
}
export type InsightsController = ReturnType<typeof useInsights>;
