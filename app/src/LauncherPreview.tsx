import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import bodyFont from "@fontsource-variable/dm-sans/files/dm-sans-latin-wght-normal.woff2?inline";
import displayFont from "@fontsource-variable/manrope/files/manrope-latin-wght-normal.woff2?inline";
import { sandboxDocument } from "./dashboardFrame";
import type { Dashboard, DashboardData } from "./dashboards";
import type { GraphSession } from "./liveGraph";
import {
  hasAuthoredLauncherPreview,
  launcherSummary,
} from "./launcherPreviewModel";

export const previewRefreshMs = 300_000;
export const previewDrawMs = 8_000;

function Authored({
  dashboard,
  data,
  onFailure,
}: {
  dashboard: Dashboard;
  data: DashboardData;
  onFailure: () => void;
}) {
  const port = useRef<MessagePort | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [drawn, setDrawn] = useState(false);
  const latest = useRef(data);
  useEffect(() => {
    latest.current = data;
  }, [data]);
  const doc = useMemo(
    () =>
      sandboxDocument(
        dashboard.html,
        `${dashboard.title} launcher preview`,
        { body: bodyFont, display: displayFont },
        "card",
        "launcher",
      ),
    [dashboard.html, dashboard.title],
  );
  useEffect(() => {
    timer.current = setTimeout(onFailure, previewDrawMs);
    return () => {
      port.current?.close();
      port.current = null;
      if (timer.current) clearTimeout(timer.current);
    };
  }, [onFailure]);
  const payload = useCallback(
    (read: DashboardData) => ({
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
        shape: "launcher",
        edits: false,
      },
    }),
    [dashboard.id, dashboard.version, dashboard.title],
  );
  useEffect(() => {
    port.current?.postMessage(payload(data));
  }, [data, payload]);
  return (
    <>
      {!drawn && <p className="preview-wait">Drawing preview…</p>}
      <iframe
        className={`launcher-authored${drawn ? " drawn" : ""}`}
        title={`${dashboard.title} launcher preview`}
        tabIndex={-1}
        sandbox="allow-scripts"
        referrerPolicy="no-referrer"
        allow=""
        srcDoc={doc}
        onLoad={(e) => {
          if (port.current) {
            onFailure();
            return;
          }
          const channel = new MessageChannel();
          port.current = channel.port1;
          channel.port1.onmessage = (event) => {
            const m = event.data;
            if (m?.type === "ready") {
              if (timer.current) clearTimeout(timer.current);
              setDrawn(true);
            } else if (m?.type === "error" || m?.type === "idle") onFailure();
            else if (m?.type === "write" && Number.isInteger(m.id))
              channel.port1.postMessage({
                type: "written",
                id: m.id,
                answer: {
                  ok: false,
                  reason: "Open the dashboard to make changes.",
                },
              });
            // open/navigation requests intentionally have no handler in previews.
          };
          e.currentTarget.contentWindow?.postMessage(
            { type: "rhythm:init" },
            "*",
            [channel.port2],
          );
          channel.port1.postMessage(payload(latest.current));
        }}
      />
    </>
  );
}

function Content({
  dashboard,
  data,
}: {
  dashboard: Dashboard;
  data: DashboardData;
}) {
  const [failed, setFailed] = useState(false);
  const fail = useMemo(() => () => setFailed(true), []);
  const authored = hasAuthoredLauncherPreview(dashboard.html);
  if (authored && !failed)
    return <Authored dashboard={dashboard} data={data} onFailure={fail} />;
  const summary = launcherSummary(dashboard, data);
  const points = summary.points;
  const low = points ? Math.min(...points) : 0,
    high = points ? Math.max(...points) : 0;
  const line = points
    ?.map(
      (value, i) =>
        `${(i / Math.max(1, points.length - 1)) * 140},${high === low ? 12 : 24 - ((value - low) / (high - low)) * 24}`,
    )
    .join(" ");
  return (
    <article
      className="launcher-summary"
      data-preview-kind={summary.kind}
      data-preview-ready="true"
    >
      <header>
        <span className="preview-mark" aria-hidden="true">
          ◈
        </span>
        <h1>{dashboard.title}</h1>
      </header>
      <span className="preview-label">{summary.label}</span>
      <strong className="preview-value">{summary.value}</strong>
      <p className="preview-detail">{summary.detail}</p>
      {points && points.length > 1 && (
        <svg
          className="preview-chart"
          viewBox="0 0 140 26"
          role="img"
          aria-label={`Last ${points.length} values; ${summary.detail}`}
          preserveAspectRatio="none"
        >
          <polyline points={line} />
        </svg>
      )}
      <footer>
        {failed
          ? "Summary · authored preview failed"
          : summary.kind === "fallback"
            ? "Basic preview"
            : "From dashboard feeds"}
        {summary.partial ? " · partial" : ""}
      </footer>
    </article>
  );
}

function PreviewRead({
  id,
  session,
}: {
  id: string;
  session: Pick<GraphSession, "dashboard" | "dashboardData">;
}) {
  const [read, setRead] = useState<{
    dashboard: Dashboard;
    data: DashboardData;
  } | null>(null);
  const [status, setStatus] = useState("Loading preview…");
  const [title, setTitle] = useState("Dashboard");
  useEffect(() => {
    let gone = false,
      busy = false;
    const controller = new AbortController();
    const refresh = async () => {
      if (busy || gone) return;
      busy = true;
      const request = new AbortController();
      const abort = () => request.abort();
      controller.signal.addEventListener("abort", abort, { once: true });
      const timeout = setTimeout(abort, 25_000);
      try {
        const dashboard = await session.dashboard?.(
          id,
          undefined,
          request.signal,
        );
        if (!dashboard) throw new Error("unavailable");
        if (!gone && !request.signal.aborted) setTitle(dashboard.title);
        const data = await session.dashboardData?.(
          id,
          dashboard.version,
          request.signal,
        );
        if (
          !data ||
          data.dashboard.id !== id ||
          data.dashboard.version !== dashboard.version
        )
          throw new Error("unavailable");
        if (!gone && !request.signal.aborted) setRead({ dashboard, data });
      } catch {
        if (!gone) {
          setRead(null);
          setStatus("Preview unavailable. Open the dashboard to retry.");
        }
      } finally {
        clearTimeout(timeout);
        controller.signal.removeEventListener("abort", abort);
        busy = false;
      }
    };
    void refresh();
    const interval = setInterval(() => {
      if (!document.hidden) void refresh();
    }, previewRefreshMs);
    return () => {
      gone = true;
      controller.abort();
      clearInterval(interval);
    };
  }, [id, session]);
  return read ? (
    <Content key={`${read.dashboard.id}:${read.dashboard.version}`} {...read} />
  ) : (
    <div className="preview-state" role="status">
      <span aria-hidden="true">◈</span>
      <h1>{title}</h1>
      <p>{status}</p>
    </div>
  );
}

export default function LauncherPreview(props: {
  id: string;
  session: Pick<GraphSession, "dashboard" | "dashboardData">;
}) {
  return <PreviewRead key={props.id} {...props} />;
}
