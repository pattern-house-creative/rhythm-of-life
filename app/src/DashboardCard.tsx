import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowUpRight, LayoutDashboard } from "lucide-react";
import bodyFont from "@fontsource-variable/dm-sans/files/dm-sans-latin-wght-normal.woff2?inline";
import displayFont from "@fontsource-variable/manrope/files/manrope-latin-wght-normal.woff2?inline";
import { sandboxDocument } from "./dashboardFrame";
import type { CardShape } from "./dashboardFrame";
import { dashboardCache } from "./dashboardCache";
import type { Dashboard, DashboardData } from "./dashboards";
import type { GraphSession } from "./liveGraph";
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
    const read = () =>
      session.dashboardData!(dashboard.id, dashboard.version, controller.signal)
        .then((result) => {
          if (controller.signal.aborted || !result) return;
          latest.current = result;
          dashboardCache.keepData(result);
          sendRef.current(result);
        })
        .catch(() => {});
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
  return (
    <div className={`dashboard-card ${shape}`}>
      <div className="section-label">
        <span>
          <LayoutDashboard size={14} aria-hidden="true" />
          {shownTitle.toUpperCase()}
        </span>
        <ArrowUpRight size={16} aria-hidden="true" />
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
          {dashboard === undefined && !unavailable
            ? "Opening this dashboard…"
            : dashboard === null && missing
              ? missing
              : "This card can’t be drawn right now. Open the dashboard to see it."}
        </p>
      )}
      {/* A dashboard this viewer can't read is nothing to open. */}
      {!(dashboard === null && missing) && (
        <button
          className="dashboard-card-open"
          aria-label={`Open the ${shownTitle} dashboard`}
          onClick={onOpen}
        />
      )}
    </div>
  );
}
