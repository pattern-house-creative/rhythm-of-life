import { useCallback, useEffect, useMemo, useRef } from "react";
import bodyFont from "@fontsource-variable/dm-sans/files/dm-sans-latin-wght-normal.woff2?inline";
import displayFont from "@fontsource-variable/manrope/files/manrope-latin-wght-normal.woff2?inline";
import { sandboxDocument } from "./dashboardFrame";
import type { Dashboard, DashboardData } from "./dashboards";
import "./PageFrame.css";

// A page that hasn't drawn eight seconds after its data arrived is treated as broken.
const drawMs = 8_000;

export type WriteAnswer =
  { ok: true; summary: string } | { ok: false; reason: string };

// A dashboard's page part (docs/dev/screens.md 10.3 and 10.9): its page in the same sealed
// frame as ever, with the same policies and the same bridge. Data goes in; `ready`, `idle`,
// `error`, `open` and declared writes come out, and nothing else: no message reaches the
// layout, the parts or the screen, so a page can never arrange anything. The frame's first load
// hands it the channel, which its wrapper passes to the page; a second load would mean something
// navigated the wrapper, and it gets nothing more. A new version's frame starts afresh: its own
// channel, timer and count of loads, and only data read for its version.
export function PageFrame({
  dashboard,
  data,
  edits,
  drawn,
  hidden,
  onDrawn,
  onFail,
  onStopped,
  onOpenStar,
  onWrite,
  selection,
  onSelectionDrawn,
}: {
  dashboard: Dashboard;
  // What its feeds hold now, read for this version, and whether edits are on.
  data: DashboardData | null;
  edits: boolean;
  drawn: boolean;
  hidden: boolean;
  onDrawn: () => void;
  // The page didn't draw (before it ever did), or a later refresh threw.
  onFail: (reason: string) => void;
  onStopped: (reason: string) => void;
  onOpenStar: (graphId: string) => void;
  onWrite: (
    name: string,
    change: unknown,
    reply: (answer: WriteAnswer) => void,
  ) => void;
  selection?: { revision: number; values: Record<string, unknown> };
  onSelectionDrawn?: (revision: number, ok: boolean) => void;
}) {
  const port = useRef<MessagePort | null>(null),
    loads = useRef(0),
    timer = useRef<ReturnType<typeof setTimeout> | null>(null),
    drewOnce = useRef(false),
    frameEl = useRef<HTMLIFrameElement | null>(null);
  // What the page hears, as it is now, for the handlers on the channel.
  const latest = useRef({
    data,
    edits,
    onDrawn,
    onFail,
    onStopped,
    onOpenStar,
    onWrite,
    selection,
    onSelectionDrawn,
  });
  useEffect(() => {
    latest.current = {
      data,
      edits,
      onDrawn,
      onFail,
      onStopped,
      onOpenStar,
      onWrite,
      selection,
      onSelectionDrawn,
    };
  });

  // A page that didn't draw stops its timer, so it fails only once.
  const failNow = (reason: string) => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    latest.current.onFail(reason);
  };
  const { id, version, title } = dashboard;
  const send = useCallback(
    (read: DashboardData) => {
      if (!port.current) return;
      port.current.postMessage({
        type: "data",
        payload: {
          feeds: read.feeds,
          asOf: read.asOf,
          dashboard: {
            id,
            version,
            title,
          },
          mode: "full",
          edits: latest.current.edits,
          selection: latest.current.selection,
        },
      });
      if (!drewOnce.current && !timer.current)
        timer.current = setTimeout(() => {
          timer.current = null;
          latest.current.onFail("The page didn't draw.");
        }, drawMs);
    },
    [id, version, title],
  );
  // Each new read, and edits turned on or off, reach the page.
  useEffect(() => {
    if (data?.dashboard.id === id && data.dashboard.version === version)
      send(data);
    // send reads the dashboard and the channel as they are now.
  }, [data, edits, selection, id, version, send]);
  // Leaving, or another version taking the frame's place, closes the channel and the timer.
  useEffect(
    () => () => {
      port.current?.close();
      port.current = null;
      if (timer.current) clearTimeout(timer.current);
      timer.current = null;
    },
    [dashboard.id, dashboard.version],
  );

  const doc = useMemo(
    () =>
      sandboxDocument(dashboard.html, `${dashboard.title} dashboard`, {
        body: bodyFont,
        display: displayFont,
      }),
    [dashboard.html, dashboard.title],
  );
  const connect = (frame: HTMLIFrameElement) => {
    if (frameEl.current !== frame) {
      frameEl.current = frame;
      port.current?.close();
      port.current = null;
      if (timer.current) clearTimeout(timer.current);
      timer.current = null;
      loads.current = 0;
      drewOnce.current = false;
    }
    loads.current += 1;
    if (loads.current > 1) {
      port.current?.close();
      port.current = null;
      failNow("The page tried to leave its frame.");
      return;
    }
    const channel = new MessageChannel();
    port.current = channel.port1;
    channel.port1.onmessage = (event: MessageEvent) => {
      const m = event.data as {
        type?: unknown;
        message?: unknown;
        star?: unknown;
        id?: unknown;
        name?: unknown;
        change?: unknown;
        selectionRevision?: unknown;
      } | null;
      if (m?.type === "ready") {
        if (typeof m.selectionRevision === "number")
          latest.current.onSelectionDrawn?.(m.selectionRevision, true);
        drewOnce.current = true;
        if (timer.current) clearTimeout(timer.current);
        timer.current = null;
        latest.current.onDrawn();
      } else if (m?.type === "idle")
        failNow("The page never asked for its data.");
      else if (m?.type === "error") {
        if (latest.current.selection)
          latest.current.onSelectionDrawn?.(
            latest.current.selection.revision,
            false,
          );
        const message = String(m.message ?? "").slice(0, 300);
        // After it has drawn once, a refresh that throws is noted; the page stays.
        if (drewOnce.current)
          latest.current.onStopped(`The last refresh stopped: ${message}`);
        else failNow(`The page stopped: ${message}`);
      } else if (m?.type === "open" && typeof m.star === "string")
        latest.current.onOpenStar(m.star);
      else if (
        m?.type === "write" &&
        Number.isInteger(m.id) &&
        typeof m.name === "string"
      ) {
        const ask = m.id as number;
        latest.current.onWrite(m.name, m.change, (answer) =>
          port.current?.postMessage({ type: "written", id: ask, answer }),
        );
      }
    };
    // The frame has no origin of its own, so the message can't name one; the
    // channel it carries is the only way back.
    frame.contentWindow?.postMessage({ type: "rhythm:init" }, "*", [
      channel.port2,
    ]);
    const now = latest.current.data;
    if (now?.dashboard.version === dashboard.version) send(now);
  };

  return (
    <iframe
      key={`${dashboard.id}:${dashboard.version}`}
      className={`dashboard-frame${drawn ? " drawn" : ""}`}
      hidden={hidden}
      title={`${dashboard.title} dashboard`}
      sandbox="allow-scripts allow-forms"
      referrerPolicy="no-referrer"
      allow=""
      srcDoc={doc}
      onLoad={(e) => connect(e.currentTarget)}
    />
  );
}
