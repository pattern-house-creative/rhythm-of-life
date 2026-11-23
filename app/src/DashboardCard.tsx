import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowUpRight, LayoutDashboard } from "lucide-react";
import bodyFont from "@fontsource-variable/dm-sans/files/dm-sans-latin-wght-normal.woff2?inline";
import displayFont from "@fontsource-variable/manrope/files/manrope-latin-wght-normal.woff2?inline";
import { sandboxDocument } from "./dashboardFrame";
import type { CardShape } from "./dashboardFrame";
import { dashboardCache } from "./dashboardCache";
import type { Dashboard, DashboardData } from "./dashboards";
import type { GraphSession } from "./liveGraph";
import { freshnessPhrases } from "./feedObservationTimes";
import { FreshnessPhrases } from "./FreshnessPhrases";
import "./DashboardCard.css";

// A corner's card is read again every five minutes while the screen is on; a
// card that hasn't drawn eight seconds after its data arrived shows its title.
export const cardRefreshMs = 300_000;
const drawMs = 8_000;

// A dashboard's card on the ambient screen: its latest version drawn in card
// mode, compact in a corner or tall in the main slot, in the same sandbox as the
// full dashboard, with no controls. Tapping anywhere on it opens the full
// dashboard.
export default function DashboardCard({
  id,
  title,
  session,
  onOpen,
  shape = "compact",
  unavailable = false,
  missing,
}: {
  id: string;
  title: string;
  session: Pick<GraphSession, "dashboard" | "dashboardData">;
  onOpen: () => void;
  shape?: CardShape;
  // The demo's preview of a missing or failed source draws no card.
  unavailable?: boolean;
  // What the card says when its dashboard can't be read at all: a card part
  // whose dashboard isn't among the saved ones calls it unavailable.
  missing?: string;
}) {
  const [dashboard, setDashboard] = useState<Dashboard | null | undefined>(
    undefined,
  );
  const [drawn, setDrawn] = useState(false);
  const [broken, setBroken] = useState(false);
  // The last read this card drew from, and what went wrong since or in it.
  const [feedStatus, setFeedStatus] = useState<{
    id: string;
    version: number;
    read?: DashboardData;
    // The last refresh failed, so the read is the one kept from before.
    failed?: boolean;
    warning?: string;
  } | null>(null);
  const port = useRef<MessagePort | null>(null),
    latest = useRef<DashboardData | null>(null),
    timer = useRef<ReturnType<typeof setTimeout> | null>(null),
    loads = useRef(0);

  useEffect(() => {
    if (!session.dashboard) return;
    const controller = new AbortController();
    session.dashboard(id, undefined, controller.signal).then(
      (d) => {
        if (d) dashboardCache.keepVersion(d);
        setDashboard(d);
      },
      () => {
        if (!controller.signal.aborted) setDashboard(null);
      },
    );
    return () => controller.abort();
  }, [id, session]);

  const send = (read: DashboardData) => {
    if (!port.current || !dashboard) return;
    port.current.postMessage({
      type: "data",
      payload: {
        feeds: read.feeds,
        asOf: read.asOf,
        dashboard: {
          id: dashboard.id,
          version: dashboard.version,
          title: dashboard.title,
        },
        mode: "card",
        shape,
        edits: false,
      },
    });
    if (!timer.current)
      timer.current = setTimeout(() => {
        timer.current = null;
        setBroken(true);
      }, drawMs);
  };
  const sendRef = useRef(send);
  useEffect(() => {
    sendRef.current = send;
  });

  useEffect(() => {
    if (!dashboard || !session.dashboardData) return;
    const controller = new AbortController();
    let sequence = 0;
    const read = async () => {
      const request = ++sequence;
      const { id, version } = dashboard;
      const result = await session.dashboardData!(
        id,
        version,
        controller.signal,
      ).catch(() => null);
      if (controller.signal.aborted || request !== sequence) return;
      if (
        !result ||
        result.dashboard.id !== id ||
        result.dashboard.version !== version
      ) {
        // The page keeps the last data it drew; the line says whether there is any.
        setFeedStatus((before) => {
          const kept =
            before?.id === id && before.version === version
              ? before.read
              : undefined;
          return {
            id,
            version,
            read: kept,
            failed: true,
            warning: kept
              ? "Refresh failed."
              : "Refresh failed. No data read yet.",
          };
        });
        return;
      }
      latest.current = result;
      dashboardCache.keepData(result);
      const feeds = Object.values(result.feeds);
      const missing = feeds.filter((feed) => feed.status !== "ok").length;
      setFeedStatus({
        id,
        version,
        read: result,
        warning: missing
          ? `${missing} of ${feeds.length} feeds unavailable.`
          : undefined,
      });
      sendRef.current(result);
    };
    void read();
    const interval = setInterval(() => {
      if (!document.hidden) void read();
    }, cardRefreshMs);
    return () => {
      controller.abort();
      clearInterval(interval);
      port.current?.close();
      port.current = null;
      if (timer.current) clearTimeout(timer.current);
      timer.current = null;
    };
  }, [dashboard, session]);

  const doc = useMemo(
    () =>
      dashboard
        ? sandboxDocument(
            dashboard.html,
            `${dashboard.title} card`,
            { body: bodyFont, display: displayFont },
            "card",
            shape,
          )
        : "",
    [dashboard, shape],
  );
  const connect = (frame: HTMLIFrameElement) => {
    loads.current += 1;
    if (loads.current > 1) {
      port.current?.close();
      port.current = null;
      setBroken(true);
      return;
    }
    const channel = new MessageChannel();
    port.current = channel.port1;
    channel.port1.onmessage = (event: MessageEvent) => {
      const m = event.data as { type?: unknown; id?: unknown } | null;
      if (m?.type === "ready") {
        if (timer.current) clearTimeout(timer.current);
        timer.current = null;
        setDrawn(true);
        setBroken(false);
      } else if (m?.type === "idle" || (m?.type === "error" && !drawn))
        setBroken(true);
      else if (m?.type === "write" && Number.isInteger(m.id))
        port.current?.postMessage({
          type: "written",
          id: m.id,
          answer: { ok: false, reason: "Open the dashboard to make changes." },
        });
    };
    frame.contentWindow?.postMessage({ type: "rhythm:init" }, "*", [
      channel.port2,
    ]);
    if (latest.current) send(latest.current);
  };

  const shownTitle = dashboard?.title ?? title;
  // A dashboard this viewer can't read is nothing to open: no button, so no
  // arrow and no edge under a pointer either.
  const opens = !(dashboard === null && missing);
  const status =
    dashboard &&
    feedStatus?.id === dashboard.id &&
    feedStatus.version === dashboard.version
      ? feedStatus
      : null;
  // Drawn yet? Not while its data couldn't be read at all.
  const waiting =
    dashboard && !drawn && !broken && !unavailable && !(status && !status.read);
  return (
    <div className={`dashboard-card ${shape}`}>
      {/* Rhythm's own frame: the title as its author wrote it, and the
          opening arrow. The page below it is the author's. */}
      <div className="dashboard-card-head">
        <span className="dashboard-card-name">
          <LayoutDashboard size={15} aria-hidden="true" />
          <span className="dashboard-card-title">{shownTitle}</span>
        </span>
        {opens && <ArrowUpRight size={16} aria-hidden="true" />}
        {/* Its page is on its way: said where the page will be. */}
        {waiting && (
          <p className="dashboard-card-status dashboard-card-wait">
            Loading preview…
          </p>
        )}
      </div>
      {dashboard && !broken && !unavailable ? (
        <iframe
          key={`${dashboard.id}:${dashboard.version}`}
          className={`dashboard-card-frame${drawn ? " drawn" : ""}`}
          title={`${shownTitle} card`}
          tabIndex={-1}
          sandbox="allow-scripts"
          referrerPolicy="no-referrer"
          allow=""
          srcDoc={doc}
          onLoad={(e) => connect(e.currentTarget)}
        />
      ) : (
        <p className="dashboard-card-status">
          {dashboard === undefined && !unavailable ? (
            "Loading preview…"
          ) : dashboard === null && missing ? (
            <strong>{missing}</strong>
          ) : (
            <>
              <strong>Preview unavailable.</strong> Open dashboard.
            </>
          )}
        </p>
      )}
      {status && !unavailable && (
        <small className="dashboard-card-freshness">
          {status.warning && (
            <>
              <span className="dashboard-card-stale">{status.warning}</span>
              {status.read && " "}
            </>
          )}
          {status.read && (
            <FreshnessPhrases
              phrases={freshnessPhrases(status.read, false, status.failed)}
            />
          )}
        </small>
      )}
      {opens && (
        <button
          className="dashboard-card-open"
          aria-label={`Open the ${shownTitle} dashboard`}
          onClick={onOpen}
        />
      )}
    </div>
  );
}
