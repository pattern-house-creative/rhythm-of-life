import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
} from "react";
import { afterArranging, arrangingNow } from "./arranging";
import { dashboardCache } from "./dashboardCache";
import type { Dashboard, DashboardData } from "./dashboards";
import type { GraphSession } from "./liveGraph";
import type { Layout } from "./parts";

// While a dashboard is open its feeds are read again every two minutes (and
// after Sync now has had time to land), with no AI call.
export const refreshMs = 120_000;

// A version saved here (Studio's Save): every open view of that dashboard looks for it at once.
const savedEvent = "rhythm:dashboard-saved";
export const dashboardSaved = (id: string) =>
  window.dispatchEvent(new CustomEvent(savedEvent, { detail: { id } }));

type Loaded = { key: string; dashboard: Dashboard | null };
// How the drawn version is arranged now: its layout (null: the page alone),
// whether it was read whole, and its layout revision.
export interface Arrangement {
  layout: Layout | null;
  whole: boolean;
  revision: number;
}

// One dashboard's version as the reader, a panel or full screen shows it
// (DashboardView.tsx): the version asked for (null: the latest), read from the
// cache at once and again from the engine; the fallback to the version before
// when the newest doesn't draw; what its feeds hold now, read on open, every
// two minutes while visible and on request; and how it is arranged, which the
// feed read brings when another screen changed it (docs/dev/screens.md 10.7),
// without any frame loading again. A newer version, saved meanwhile by an AI or
// in Studio, is looked for as often, from the saved dashboards' list (which has
// no pages), and drawn in place of the one open.
export function useDashboardVersion(
  id: string,
  session: Pick<GraphSession, "dashboard" | "dashboardData" | "dashboards">,
) {
  // The version asked for (null: the latest) and a version that didn't draw.
  const [asked, setAsked] = useState<number | null>(null);
  const [failed, setFailed] = useState<{
    version: number;
    reason: string;
    fellBack: boolean;
  } | null>(null);
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [data, setData] = useState<{
    id: string;
    version: number;
    read: DashboardData | null;
    at: number;
    refreshFailed: boolean;
  } | null>(null);
  const readSequence = useRef(0);
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const [arranged, setArranged] = useState<
    (Arrangement & { frame: string }) | null
  >(null);
  const key = `${id}:${asked ?? "latest"}`;
  const dashboard = loaded?.key === key ? loaded.dashboard : undefined;
  const frame = dashboard ? `${dashboard.id}:${dashboard.version}` : null;
  // The version's own arrangement, until a read or a change brings a newer one.
  const arrangement: Arrangement | null = !dashboard
    ? null
    : arranged?.frame === frame
      ? arranged
      : {
          layout: dashboard.layout,
          whole: dashboard.layoutWhole,
          revision: dashboard.layoutRevision,
        };
  // A newer arrangement of the drawn version, from a read; one no newer than
  // what is drawn changes nothing.
  const newer = useCallback(
    (d: Dashboard, read: Partial<Dashboard> | DashboardData["dashboard"]) => {
      if (read.layoutRevision === undefined || read.layout === undefined)
        return;
      const at = `${d.id}:${d.version}`;
      setArranged((a) => {
        const drawn = a?.frame === at ? a.revision : d.layoutRevision;
        return read.layoutRevision! > drawn
          ? {
              frame: at,
              layout: read.layout ?? null,
              whole: read.layoutWhole ?? false,
              revision: read.layoutRevision!,
            }
          : a;
      });
    },
    [],
  );

  // The version itself, with its page: the one read last opens at once, and
  // is replaced only if the engine now has a different one.
  useEffect(() => {
    if (!session.dashboard) return;
    const controller = new AbortController();
    const kept = dashboardCache.version(id, asked ?? undefined);
    // eslint-disable-next-line react/set-state-in-effect
    if (kept) setLoaded({ key, dashboard: kept });
    session.dashboard(id, asked ?? undefined, controller.signal).then(
      (d) => {
        if (d) dashboardCache.keepVersion(d, asked ?? undefined);
        if (
          !kept ||
          !d ||
          d.version !== kept.version ||
          d.html !== kept.html ||
          d.editsAllowed !== kept.editsAllowed
        )
          setLoaded({ key, dashboard: d });
        // Arranged since it was kept: drawn so, with the same frame.
        else newer(kept, d);
      },
      () => {
        if (!controller.signal.aborted && !kept)
          setLoaded({ key, dashboard: null });
      },
    );
    return () => controller.abort();
  }, [id, asked, key, session, newer]);

  // A new version that breaks falls back to the one before it, once.
  const fail = useCallback(
    (reason: string) => {
      if (!dashboard) return;
      const back = asked === null && dashboard.version > 1;
      setFailed({ version: dashboard.version, reason, fellBack: back });
      if (back) setAsked(dashboard.version - 1);
    },
    [dashboard, asked],
  );

  // A newer version than the one open: the latest, when that is what is open, or a fix for the
  // newest when it didn't draw and the one before it is open instead. A version chosen by hand
  // stays. An arrangement being saved is answered first, and the look made again after it, so
  // the change is never drawn over another version and its outcome is always said.
  const recheckNow = useRef<(signal?: AbortSignal) => Promise<void>>(
    async () => {},
  );
  const recheck = useCallback(
    async (signal?: AbortSignal) => {
      if (!dashboard || !session.dashboards || !session.dashboard) return;
      const fix =
        asked !== null &&
        failed?.fellBack === true &&
        asked === failed.version - 1;
      if (asked !== null && !fix) return;
      let latest: number | undefined;
      try {
        latest = (await session.dashboards(signal)).find(
          (d) => d.id === dashboard.id,
        )?.latest;
      } catch {
        return;
      }
      const again = () =>
        afterArranging(dashboard.id, () => void recheckNow.current(signal));
      if (
        signal?.aborted ||
        latest === undefined ||
        (fix ? latest <= failed!.version : latest === dashboard.version)
      )
        return;
      if (arrangingNow(dashboard.id)) return again();
      const d = await session
        .dashboard(dashboard.id, undefined, signal)
        .catch(() => null);
      if (signal?.aborted || !d) return;
      if (arrangingNow(dashboard.id)) return again();
      // Kept first, so the latest opens as this one at once, never the version before it.
      dashboardCache.keepVersion(d);
      if (fix) {
        if (d.version > failed!.version) {
          setFailed(null);
          setAsked(null);
        }
      } else
        setLoaded((l) =>
          l?.key === key && l.dashboard?.version !== d.version
            ? { key, dashboard: d }
            : l,
        );
    },
    [dashboard, session, asked, failed, key],
  );

  // Looked for with the feeds, by the version as it is then.
  useLayoutEffect(() => {
    recheckNow.current = recheck;
  });

  // What its feeds hold now: on open, every two minutes while visible, and on request.
  const read = useCallback(
    async (signal?: AbortSignal) => {
      if (!dashboard || !session.dashboardData) return;
      const sequence = ++readSequence.current;
      setBusy(true);
      let result: DashboardData | null = null;
      try {
        result = await session.dashboardData(
          dashboard.id,
          dashboard.version,
          signal,
        );
      } catch {
        // Keep the last successful read for this version and show the failure.
      }
      if (signal?.aborted || sequence !== readSequence.current) return;
      const { id, version } = dashboard;
      if (result?.dashboard.id !== id || result.dashboard.version !== version)
        result = null;
      if (result) {
        dashboardCache.keepData(result);
        newer(dashboard, result.dashboard);
      }
      const readAt = Date.now();
      setData((previous) => {
        const kept =
          previous?.id === id && previous.version === version ? previous : null;
        return {
          id,
          version,
          read: result ?? kept?.read ?? null,
          at: result ? Date.parse(result.asOf) : (kept?.at ?? 0),
          refreshFailed: !result,
        };
      });
      setNow(readAt);
      setBusy(false);
    },
    [dashboard, session, newer],
  );
  useEffect(() => {
    if (!dashboard) return;
    const controller = new AbortController();
    // What was read last draws at once; the feeds are read again as it opens.
    const kept = dashboardCache.data(dashboard.id, dashboard.version);
    if (kept)
      // eslint-disable-next-line react/set-state-in-effect
      setData({
        id: dashboard.id,
        version: dashboard.version,
        read: kept,
        at: Date.parse(kept.asOf),
        refreshFailed: false,
      });
    void read(controller.signal);
    const interval = setInterval(() => {
      if (document.hidden) return setNow(Date.now());
      void read(controller.signal);
      void recheckNow.current(controller.signal);
    }, refreshMs);
    const visible = () => {
      if (document.hidden) return;
      void read(controller.signal);
      void recheckNow.current(controller.signal);
    };
    const saved = (event: Event) => {
      if ((event as CustomEvent).detail?.id === dashboard.id)
        void recheckNow.current(controller.signal);
    };
    document.addEventListener("visibilitychange", visible);
    window.addEventListener(savedEvent, saved);
    return () => {
      controller.abort();
      clearInterval(interval);
      document.removeEventListener("visibilitychange", visible);
      window.removeEventListener(savedEvent, saved);
    };
  }, [dashboard, read]);

  // Draws an arrangement of this version in place (a change saved here, or put
  // back), and keeps a saved one with the version, so it opens so next time.
  const show = useCallback(
    (layout: Layout | null, revision: number, whole = true) => {
      if (!dashboard) return;
      setArranged({
        frame: `${dashboard.id}:${dashboard.version}`,
        layout,
        whole,
        revision,
      });
      if (whole && revision > dashboard.layoutRevision)
        dashboardCache.keepVersion(
          { ...dashboard, layout, layoutWhole: true, layoutRevision: revision },
          dashboard.version === dashboard.latest
            ? undefined
            : dashboard.version,
        );
    },
    [dashboard],
  );

  const current =
    dashboard && data?.id === dashboard.id && data.version === dashboard.version
      ? data
      : null;
  return {
    dashboard,
    key,
    asked,
    setAsked,
    failed,
    setFailed,
    fail,
    current,
    read,
    recheck,
    busy,
    now,
    arrangement,
    show,
  };
}
