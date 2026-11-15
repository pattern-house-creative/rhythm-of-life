import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
} from "react";
import { mergePage } from "./knowledge";
import type { GraphPage } from "./knowledge";
import { boundGraph, emptyGraph } from "./liveGraph";
import type { GraphSession } from "./liveGraph";
import { retryDelay } from "./retry";

const noStars: readonly string[] = [];
// The map as it was last read, kept through a refresh that failed: not confirmed since.
const unconfirmed = (page: GraphPage): GraphPage => ({
  ...page,
  nodes: page.nodes.map((n) =>
    n.resource.availability === "stale"
      ? n
      : { ...n, resource: { ...n.resource, availability: "stale" as const } },
  ),
});

// `kept` is a star asked for by id (one a screen was told to show) that stays on the map, past
// refreshes that bring back only the overview, until it is let go. `lit` are stars brought onto
// the map together (a focused map part's, through its Explore), kept the same way while they
// are still there.
export function useKnowledge(
  session: GraphSession | null,
  kept: string | null = null,
  lit: readonly string[] = noStars,
) {
  const [page, setPage] = useState<GraphPage>(emptyGraph);
  const [pins, setPins] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<string | null>(null);
  const [epoch, setEpoch] = useState(0);
  const pageRef = useRef(page),
    pinsRef = useRef(pins),
    flight = useRef<AbortController | null>(null),
    pinsFlight = useRef<AbortController | null>(null),
    lastStart = useRef(-Infinity),
    denied = useRef(false),
    // Refreshes failed in a row, and the next try after them.
    failures = useRef(0),
    retry = useRef<ReturnType<typeof setTimeout> | undefined>(undefined),
    again = useRef(() => {}),
    keptRef = useRef(kept),
    litRef = useRef(lit);
  useLayoutEffect(() => {
    pageRef.current = page;
    pinsRef.current = pins;
    keptRef.current = kept;
    litRef.current = lit;
  }, [page, pins, kept, lit]);
  const refresh = useCallback(async () => {
    if (
      !session ||
      flight.current ||
      document.hidden ||
      !navigator.onLine ||
      denied.current
    )
      return;
    const controller = new AbortController();
    flight.current = controller;
    lastStart.current = Date.now();
    clearTimeout(retry.current);
    setBusy(true);
    setError(null);
    // Optional saved pins are read alongside, in a flight of their own: a slow read
    // holds back neither the map nor Refresh, Discover or a search, and they apply
    // whenever they arrive. Only clearing the map (hidden, offline, signed out) drops them.
    if (session.pins && !pinsFlight.current) {
      const pinsRead = new AbortController();
      pinsFlight.current = pinsRead;
      session.pins(pinsRead.signal).then(
        (saved) => {
          if (pinsFlight.current === pinsRead) pinsFlight.current = null;
          if (pinsRead.signal.aborted) return;
          pinsRef.current = saved;
          setPins(saved);
          setPage((current) => boundGraph(current, saved));
        },
        () => {
          if (pinsFlight.current === pinsRead) pinsFlight.current = null;
        },
      );
    }
    try {
      let result = await session.refresh(pageRef.current, controller.signal);
      // A refresh that brings back only the overview leaves the kept star out: fetch it again,
      // with its links, as the explorer fetches a star's neighbours.
      const star = keptRef.current;
      if (star && !result.nodes.some((n) => n.id === star)) {
        const found = await session
          .retrieve("neighbors", star, undefined, controller.signal)
          .catch(() => null);
        if (found) result = mergePage(result, found);
      }
      if (!controller.signal.aborted) {
        setPage(
          boundGraph(result, [
            ...pinsRef.current,
            ...(star ? [star] : []),
            ...litRef.current,
          ]),
        );
        setLastRefresh(new Date().toISOString());
        setEpoch((e) => e + 1);
        failures.current = 0;
      }
    } catch {
      // A refresh that failed (the app's server or the engine restarting, say) leaves the map as
      // it was last read, marked stale, and is tried again soon (retry.ts). Lost access clears it
      // (accessLost below), as hiding the page and going offline do.
      if (!controller.signal.aborted) {
        pageRef.current = unconfirmed(pageRef.current);
        setPage(pageRef.current);
        setError(
          "Refresh could not finish. Your last retrieved graph is still available.",
        );
        retry.current = setTimeout(
          () => again.current(),
          retryDelay(failures.current++),
        );
      }
    } finally {
      if (flight.current === controller) {
        flight.current = null;
        setBusy(false);
      }
    }
  }, [session]);
  useEffect(() => {
    // Synchronize the working set when the external source session changes.
    // eslint-disable-next-line react/set-state-in-effect
    setPage(session?.initial ?? emptyGraph());
    pageRef.current = session?.initial ?? emptyGraph();
    denied.current = false;
    again.current = () => void refresh().catch(() => {});
    void refresh().catch(() => {});
    const clear = () => {
      flight.current?.abort();
      flight.current = null;
      pinsFlight.current?.abort();
      pinsFlight.current = null;
      clearTimeout(retry.current);
      failures.current = 0;
      pageRef.current = emptyGraph();
      setPage(pageRef.current);
      setPins([]);
      pinsRef.current = [];
      setBusy(false);
      setLastRefresh(null);
      setError(null);
      lastStart.current = -Infinity;
    };
    const resume = () => {
      if (
        !document.hidden &&
        navigator.onLine &&
        Date.now() - lastStart.current > 1000
      )
        void refresh().catch(() => {});
    };
    const visibility = () => (document.hidden ? clear() : resume());
    const accessLost = () => {
      denied.current = true;
      clear();
    };
    const interval = setInterval(() => void refresh().catch(() => {}), 900_000);
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
      pinsFlight.current?.abort();
      pinsFlight.current = null;
      clearTimeout(retry.current);
      failures.current = 0;
      clearInterval(interval);
      document.removeEventListener("visibilitychange", visibility);
      for (const [event, handler] of Object.entries(events))
        window.removeEventListener(event, handler);
    };
  }, [session, refresh]);
  // `keep` ranks like pins while the merged map is bounded, for a star asked for by id.
  const accept = (next: GraphPage, keep: string[] = []) => {
    if (document.hidden || !navigator.onLine || denied.current) return;
    flight.current?.abort();
    flight.current = null;
    setBusy(false);
    setPage((p) =>
      boundGraph(mergePage(p, next), [...pinsRef.current, ...keep]),
    );
  };
  // Pinning shows at once and is saved on the engine, which answers every pin;
  // if saving fails, the pins go back to what they were.
  const togglePin = (id: string) => {
    const before = pinsRef.current;
    const pinned = !before.includes(id);
    const next = pinned
      ? [...before, id].slice(-24)
      : before.filter((n) => n !== id);
    pinsRef.current = next;
    setPins(next);
    if (!session?.setPin) return;
    session.setPin(id, pinned).then(
      (saved) => {
        pinsRef.current = saved;
        setPins(saved);
      },
      () => {
        pinsRef.current = before;
        setPins(before);
      },
    );
  };
  const discoverMore = async () => {
    if (
      !session ||
      flight.current ||
      document.hidden ||
      !navigator.onLine ||
      denied.current
    )
      return;
    const controller = new AbortController();
    flight.current = controller;
    setBusy(true);
    setError(null);
    try {
      const result = await session.retrieve(
        "discover",
        "",
        pageRef.current.discoveryCursors,
        controller.signal,
        pageRef.current.discoveryStates,
      );
      if (!controller.signal.aborted)
        setPage((p) =>
          boundGraph(
            {
              ...mergePage(p, result),
              discoveryCursors: result.cursors,
              discoveryStates: result.sourceStates,
              completeness: result.completeness,
            },
            pinsRef.current,
          ),
        );
    } catch {
      if (!controller.signal.aborted)
        setError(
          "Discovery could not finish. Try again when the source is available.",
        );
    } finally {
      if (flight.current === controller) {
        flight.current = null;
        setBusy(false);
      }
    }
  };
  return {
    page,
    accept,
    pins,
    togglePin,
    busy,
    error,
    refresh,
    discoverMore,
    lastRefresh,
    epoch,
    session,
  };
}
export type KnowledgeController = ReturnType<typeof useKnowledge>;
