import { useCallback, useEffect, useRef, useState } from "react";
import { loadObservatory, requestGarminSync } from "./apiClient";
import type { ObservatoryData } from "./model";
import { glucoseSlot, unavailableHealth } from "../server/healthState";
import { retryDelay } from "./retry";

type Part = "latest" | "history" | "garmin";
const intervals = { latest: 60_000, history: 300_000, garmin: 900_000 };
// The Garmin worker starts a sync-now job within a minute; a two-day sync is done well before this.
export const garminSyncReload = 90_000;
const ignoreClear = (_failed: boolean) => {};
// A part that could not be read: its source failed. Only the facade can say an adapter is off.
const unread = (part: Part): ObservatoryData => {
  const data = unavailableHealth();
  const slot = part === "garmin" ? "garmin" : glucoseSlot;
  return {
    ...data,
    healthMode: "live",
    sources: data.sources.map((s) =>
      s.id === slot ? { ...s, status: "unavailable" as const } : s,
    ),
  };
};
// Each source owns one flight and one timer. Nothing persists across hide/logout.
export function useHealthRefresh(
  onClear: (failed: boolean) => void = ignoreClear,
) {
  const [data, setData] = useState<ObservatoryData | null>(null);
  const [error, setError] = useState(false),
    [refreshing, setRefreshing] = useState(false),
    [clock, setClock] = useState(() => Date.now());
  const action = useRef({ refresh: () => {}, stop: () => {} });
  useEffect(() => {
    const flights = new Map<Part, AbortController>(),
      timers = new Map<Part, ReturnType<typeof setTimeout>>();
    const parts: Partial<Record<Part, ObservatoryData>> = {};
    let sync: AbortController | undefined,
      syncReload: ReturnType<typeof setTimeout> | undefined,
      // Lost access is tried again by itself, so a screen nobody touches comes back once it
      // works again (retry.ts): the next try, and how many came before it.
      access: ReturnType<typeof setTimeout> | undefined,
      accessTries = 0;
    const cancelSync = () => {
      sync?.abort();
      sync = undefined;
      clearTimeout(syncReload);
      syncReload = undefined;
    };
    let stopped = false,
      suspended = document.hidden,
      denied = false,
      live = false,
      lastStart = -Infinity;
    const publish = () => {
      if (stopped || suspended || denied || document.hidden) return;
      const base = parts.latest ?? {
        ...unavailableHealth(),
        healthMode: "live" as const,
      };
      const history = parts.history,
        garmin = parts.garmin;
      setData({
        ...base,
        ...(history
          ? {
              glucose: history.glucose,
              glucoseHistory: history.glucoseHistory,
              glucoseSummary: history.glucoseSummary,
            }
          : {}),
        ...(garmin
          ? {
              steps: garmin.steps,
              intensityMinutes: garmin.intensityMinutes,
              recovery: garmin.recovery,
              workouts: garmin.workouts,
              workoutsStatus: garmin.workoutsStatus,
              workoutsCoverage: garmin.workoutsCoverage,
            }
          : {}),
        sources: base.sources.map((s) =>
          s.id === "garmin"
            ? (garmin?.sources.find((x) => x.id === "garmin") ??
              // Not read yet: whether Garmin is connected is not known until it is.
              (base.healthMode === "live" && s.status === "disconnected"
                ? { ...s, status: "unavailable" as const, checking: true }
                : s))
            : s.id === glucoseSlot && history
              ? {
                  ...s,
                  ...(history.sources.find((x) => x.id === glucoseSlot)
                    ?.connected && !s.connected
                    ? history.sources.find((x) => x.id === glucoseSlot)!
                    : {}),
                  lastSyncedAt: history.glucose.provenance.lastSyncedAt,
                  detail: `Latest glucose: ${base.current.status ?? "available"} · recent observations: ${history.glucose.status ?? "available"}`,
                }
              : s,
        ),
      });
      setClock(Date.now());
      setRefreshing(flights.size > 0);
      setError(false);
      accessTries = 0;
    };
    const clear = (failed = false) => {
      cancelSync();
      for (const timer of timers.values()) clearTimeout(timer);
      timers.clear();
      for (const flight of flights.values()) flight.abort();
      flights.clear();
      delete parts.latest;
      delete parts.history;
      delete parts.garmin;
      document.documentElement.classList.add("privacy-curtain");
      setData(null);
      setRefreshing(false);
      setError(failed);
      onClear(failed);
    };
    const load = async (part: Part) => {
      if (
        stopped ||
        suspended ||
        denied ||
        document.hidden ||
        !navigator.onLine ||
        flights.has(part)
      )
        return;
      clearTimeout(timers.get(part));
      const request = new AbortController();
      flights.set(part, request);
      setRefreshing(true);
      if (part === "latest") lastStart = Date.now();
      try {
        const next = await loadObservatory(
          request.signal,
          part === "latest" ? undefined : part,
        );
        if (request.signal.aborted || stopped || suspended || denied) return;
        parts[part] = next;
        if (part === "latest" && next.healthMode === "live" && !live) {
          live = true;
          void load("history");
          void load("garmin");
        }
      } catch {
        if (request.signal.aborted || stopped || denied) return;
        parts[part] = unread(part);
        // Source failure stays local. Deployment auth loss clears all private state.
        if (part === "latest" && !live) {
          live = true;
          void load("history");
          void load("garmin");
        }
      } finally {
        if (flights.get(part) === request) flights.delete(part);
        if (!request.signal.aborted && !stopped && !suspended && !denied) {
          publish();
          timers.set(
            part,
            setTimeout(() => void load(part), intervals[part]),
          );
        }
      }
    };
    const loadAll = () => {
      void load("latest");
      if (live) {
        void load("history");
        void load("garmin");
      }
    };
    // Refresh also asks the engine for a Garmin sync. Accepted, or one already running: read Garmin
    // again once it has had time to finish. Too soon or unavailable: the regular schedule stands.
    const syncGarmin = async () => {
      if (!live || sync || stopped || suspended || document.hidden) return;
      const request = new AbortController();
      sync = request;
      try {
        const answer = await requestGarminSync(request.signal);
        if (request.signal.aborted || stopped || suspended || denied) return;
        if (answer.accepted || answer.pending) {
          clearTimeout(syncReload);
          syncReload = setTimeout(() => {
            syncReload = undefined;
            void load("garmin");
          }, garminSyncReload);
        }
      } catch {
        // Sync now is best effort and never an error on screen.
      } finally {
        if (sync === request) sync = undefined;
      }
    };
    const hide = () => {
      suspended = true;
      clear();
    };
    const resume = () => {
      if (document.hidden || stopped || !navigator.onLine) return;
      const was = suspended;
      suspended = false;
      denied = false;
      clearTimeout(access);
      if (was || Date.now() - lastStart > 1000) loadAll();
    };
    const visibility = () => (document.hidden ? hide() : resume());
    // Everything private is cleared at once. Unless hidden or offline meanwhile (which read
    // again as they end), the sources are read again after a while, then less often.
    const accessLost = () => {
      const first = !denied;
      denied = true;
      clear(true);
      if (!first) return;
      clearTimeout(access);
      access = setTimeout(() => {
        if (stopped || suspended || document.hidden || !navigator.onLine)
          return;
        denied = false;
        loadAll();
      }, retryDelay(accessTries++));
    };
    const offline = () => {
      suspended = true;
      clear(true);
    };
    action.current = {
      refresh: () => {
        denied = false;
        clearTimeout(access);
        loadAll();
        void syncGarmin();
      },
      stop: () => {
        stopped = true;
        clear();
      },
    };
    document.addEventListener("visibilitychange", visibility);
    const events = {
      pagehide: hide,
      pageshow: resume,
      // Focusing DevTools or another window does not hide this document.
      // Visibility/pageshow handle real restoration; focus must not reload all sources.
      offline,
      online: resume,
      "observatory-access-lost": accessLost,
    };
    for (const [event, handler] of Object.entries(events))
      window.addEventListener(event, handler);
    const age = setInterval(() => {
      if (!document.hidden) setClock(Date.now());
    }, 15_000);
    loadAll();
    return () => {
      stopped = true;
      clearTimeout(access);
      cancelSync();
      for (const f of flights.values()) f.abort();
      for (const t of timers.values()) clearTimeout(t);
      clearInterval(age);
      document.removeEventListener("visibilitychange", visibility);
      for (const [e, h] of Object.entries(events))
        window.removeEventListener(e, h);
      document.documentElement.classList.remove("privacy-curtain");
    };
  }, [onClear]);
  return {
    data,
    error,
    refreshing,
    clock,
    refresh: useCallback(() => action.current.refresh(), []),
    stop: useCallback(() => action.current.stop(), []),
  };
}
