import { instance, personName } from "./instance";
import {
  useCallback,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import type { ReactNode } from "react";
import { Check, ChevronUp, RefreshCw } from "lucide-react";
import type { Dashboard, DashboardWriteRecord } from "./dashboards";
import type { GraphSession } from "./liveGraph";
import {
  arrangeable,
  drawsAsToday,
  focusPart,
  layoutOf,
  partLabel,
} from "./parts";
import type { Fold, Part } from "./parts";
import { focusSummary } from "./mapFocus";
import {
  arrangeInPlace,
  cantArrange,
  drawGrid,
  refuseInPlace,
  replay,
  saveArrangement,
} from "./arranging";
import type { DashboardGrid } from "./arranging";
import { PageFrame } from "./PageFrame";
import { freshnessPhrases, recordedSentence } from "./feedObservationTimes";
import { FreshnessPhrases } from "./FreshnessPhrases";
import type { WriteAnswer } from "./PageFrame";
import { PartsGrid } from "./PartsGrid";
import type { GridChange, GridOutcome } from "./PartsGrid";
import { useDashboardVersion } from "./useDashboardVersion";
import "./DashboardView.css";
import { useDashboardSelection } from "./dashboardControls";

const day = (iso: string) =>
  new Date(iso).toLocaleDateString("en-US", { month: "short", day: "numeric" });
const when = (iso: string) =>
  `${day(iso)}, ${new Date(iso).toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit" })}`;

// What the declared writes let this dashboard do, in words, for the Allow edits switch: each
// write as the operations the engine allows it (DashboardWrite in engine/src/Core/Dashboards.cs,
// Adapters/Drive/DriveWrites.cs). A log write adds entries; a sheet write sets cells within its
// range and adds rows to the end of a tab; a doc write replaces the text under one heading or in
// a cell of one table, or adds to the end. A file's operations are named together, the file once,
// in words no longer than the switch's description took before on a phone.
const listed = (items: string[], last = "and") =>
  items.length < 2
    ? (items[0] ?? "")
    : `${items.slice(0, -1).join(", ")} ${last} ${items.at(-1)}`;
interface FileChanges {
  title: string;
  cells: string[];
  rows: string[];
  replace: string[];
  end: boolean;
}
function allowedChanges(d: Dashboard) {
  // In the order declared; a file where its first write is.
  const changes: (string | FileChanges)[] = [];
  const files = new Map<string, FileChanges>();
  for (const w of d.writes) {
    if (w.kind === "log") {
      changes.push(
        `add to the ${w.log} log${w.subject && instance().people.length > 1 ? ` for ${personName(w.subject)}` : ""}`,
      );
      continue;
    }
    const key = `${w.kind}:${w.star ?? w.name}`;
    let file = files.get(key);
    if (!file) {
      file = {
        title: d.stars.find((s) => s.graphId === w.star)?.title ?? "a file",
        cells: [],
        rows: [],
        replace: [],
        end: false,
      };
      files.set(key, file);
      changes.push(file);
    }
    if (w.kind === "sheet") {
      // A declared range is a tab and its cells: 'My tab'!B2:D40 or Budget!B2:D40.
      const range = w.range?.match(/^(?:'((?:[^']|'')+)'|([^!]+))!(.+)$/);
      if (range)
        file.cells.push(
          `set cells ${range[3]} on the ${range[1]?.replace(/''/g, "'") ?? range[2]} tab`,
        );
      else if (w.range) file.cells.push(`set cells ${w.range}`);
      if (w.appendTo) file.rows.push(`add rows to the ${w.appendTo} tab`);
    } else if (w.section !== undefined)
      file.replace.push(`under “${w.section}”`);
    else if (w.table !== undefined)
      file.replace.push(`in a cell of table ${w.table + 1}`);
    else file.end = true;
  }
  return changes
    .map((c) => {
      if (typeof c === "string") return c;
      if (c.cells.length || c.rows.length)
        return `${listed([...c.cells, ...c.rows])} of ${c.title}`;
      if (!c.replace.length) return `add to the end of ${c.title}`;
      const replace = `replace the text ${listed(c.replace, "or")} in ${c.title}`;
      return c.end ? `${replace}, and add to its end` : replace;
    })
    .join("; ");
}
type Notice =
  | { kind: "done" | "undone"; write: DashboardWriteRecord }
  | { kind: "failed"; reason: string };

// What a host draws in a dashboard's native parts (App.tsx, parts/nativeParts.tsx): each part in
// its cell, with "Show everything" for a focused map wherever the dashboard is arranged, and a
// star's title for a map's focus in its label.
export interface DashboardParts {
  renderPart: (
    part: Part,
    at: {
      dashboard: Dashboard;
      fold: Fold;
      showEverything?: (part: Part) => void;
    },
  ) => ReactNode;
  starTitle?: (graphId: string) => string | undefined;
}

function ago(at: number, now: number) {
  const minutes = Math.floor((now - at) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"} ago`;
  return `at ${new Date(at).toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit" })}`;
}

// One dashboard in the reader, a panel or full screen: its grid of parts
// (docs/dev/screens.md 10), the page among them in its sandboxed frame, fed the
// data its feeds hold now, with the parts that must stay honest outside the
// frame: when the data was read, which feeds are unavailable and why, the
// version, and the previous version when this one fails to draw. A dashboard
// never arranged (or arranged as its page alone) draws exactly as before parts.
// The app's own parts come from `renderPart`; a part it draws nothing for keeps
// its cell empty. Full screen (`bare`) shows the grid alone, read just as
// often, and says only what stops it drawing and what a change did.
export default function DashboardView({
  id,
  session,
  onOpenStar,
  bare = false,
  renderPart,
  starTitle,
}: {
  id: string;
  session: Pick<
    GraphSession,
    | "dashboard"
    | "dashboardData"
    | "dashboardWrite"
    | "dashboardUndo"
    | "dashboardAllowEdits"
    | "dashboardLayout"
    | "dashboards"
  >;
  // A page asked to open a star; the app decides whether it can.
  onOpenStar: (graphId: string) => void;
  bare?: boolean;
  // A native part (a map, a tile, the insights, a card), drawn in its cell.
  renderPart?: DashboardParts["renderPart"];
  // A star's title, for a map's focus in its label.
  starTitle?: DashboardParts["starTitle"];
}) {
  const {
    dashboard,
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
  } = useDashboardVersion(id, session);
  // The frame (dashboard and version) that has drawn.
  const [drawnFrame, setDrawnFrame] = useState<string | null>(null);
  // The versions, unfolded in place above the status line while choosing.
  const [picking, setPicking] = useState(false);
  const versionsId = useId();
  // On a phone the status line folds to one row; this unfolds the rest.
  const [more, setMore] = useState(false);
  const moreId = useId();
  const editTargetsId = useId();
  // Edits as they were set while this version is open (null: as saved), the
  // last change with its Undo, and whether either is on its way.
  const [edits, setEdits] = useState<{ key: string; on: boolean } | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [pending, setPending] = useState(false);
  // How the grid is drawn in its box: as saved, or folded.
  const [fold, setFold] = useState<Fold>("saved");
  const writing = useRef(false);
  const frameKey = dashboard ? `${dashboard.id}:${dashboard.version}` : null;
  const drawn = frameKey !== null && drawnFrame === frameKey;
  const controls = useDashboardSelection(dashboard, drawn);
  // Per version: a newer one drawn in its place asks again.
  const editsOn =
    edits?.key === frameKey ? edits.on : (dashboard?.editsAllowed ?? false);
  const layout = useMemo(
    () => layoutOf(arrangement?.layout),
    [arrangement?.layout],
  );

  // This dashboard as a grid, for the screen's runner (arranging.ts): one
  // object, brought up to date as it draws, kept there while it is open.
  const [grid] = useState(() => ({}) as DashboardGrid);
  useLayoutEffect(() => {
    if (!dashboard || !arrangement) return;
    Object.assign(grid, {
      id: dashboard.id,
      full: bare,
      version: dashboard.version,
      latest: dashboard.latest,
      layout: arrangement.layout,
      revision: arrangement.revision,
      whole: arrangement.whole,
      fold,
      drawn,
      show,
      reread: () => {
        void read();
        void recheck();
      },
    } satisfies DashboardGrid);
  });
  const opened = Boolean(dashboard);
  useLayoutEffect(() => (opened ? drawGrid(grid) : undefined), [opened, grid]);
  // A change made by hand goes to the runner, which saves it with the same
  // note and Undo as a command; without one it is saved here.
  const arrangeHere = useCallback(
    async (change: GridChange<Part>): Promise<GridOutcome> => {
      const ran = arrangeInPlace({
        kind: "dashboard",
        grid,
        commands: change.commands,
        summary: change.summary,
      });
      if (ran) return ran;
      // Without a runner, a change that wasn't made says why below the page, as a write does.
      const saved = session.dashboardLayout
        ? await saveArrangement(session.dashboardLayout, grid, (l) =>
            replay(l, change.commands, change.summary),
          )
        : { ok: false as const, reason: cantArrange };
      if (!saved.ok) setNotice({ kind: "failed", reason: saved.reason });
      return saved.ok ? { ok: true } : saved;
    },
    [grid, session],
  );
  // A drop the grid refused itself (no room there) says why where a change's note shows.
  const refusedHere = useCallback((reason: string) => {
    if (!refuseInPlace(reason)) setNotice({ kind: "failed", reason });
  }, []);

  // A page asks for one of its declared writes. The answer goes back to the
  // page; what changed shows outside the frame with Undo; the feeds are read
  // again either way, so the page draws from the source as it is now.
  const onWrite = (
    name: string,
    change: unknown,
    reply: (answer: WriteAnswer) => void,
  ) => {
    if (!dashboard || !session.dashboardWrite)
      return reply({ ok: false, reason: "Changes aren't available here." });
    if (!editsOn)
      return reply({
        ok: false,
        reason:
          "Edits are off for this dashboard. Turn on Allow edits below it to make changes.",
      });
    if (writing.current)
      return reply({
        ok: false,
        reason: "Another change is still being written.",
      });
    writing.current = true;
    setPending(true);
    session
      .dashboardWrite(dashboard.id, dashboard.version, name, change)
      .then(
        (answer) => {
          if (answer.ok) {
            setNotice({ kind: "done", write: answer.write });
            reply({ ok: true, summary: answer.write.summary });
          } else reply(answer);
        },
        // No answer from the app's server (it timed out, or the connection or a front
        // door failed): the change may have reached the engine, so say to check first.
        () =>
          reply({
            ok: false,
            reason:
              "The change may have been made: no answer came back. Check before trying again.",
          }),
      )
      .finally(() => {
        writing.current = false;
        setPending(false);
        void read();
      });
  };
  const undo = async (write: DashboardWriteRecord) => {
    if (!session.dashboardUndo || writing.current) return;
    writing.current = true;
    setPending(true);
    try {
      const answer = await session.dashboardUndo(write.id);
      setNotice(
        answer.ok
          ? { kind: "undone", write: answer.write }
          : { kind: "failed", reason: answer.reason },
      );
    } catch {
      setNotice({
        kind: "failed",
        reason:
          "The undo may have been made: no answer came back. Check before trying again.",
      });
    } finally {
      writing.current = false;
      setPending(false);
      void read();
    }
  };
  const allow = async (on: boolean) => {
    if (!dashboard || !session.dashboardAllowEdits) return;
    setPending(true);
    try {
      const now = await session.dashboardAllowEdits(
        dashboard.id,
        dashboard.version,
        on,
      );
      // The page hears of it with the data it has (PageFrame).
      setEdits({ key: `${dashboard.id}:${dashboard.version}`, on: now });
    } catch {
      setNotice({
        kind: "failed",
        reason: "Edits couldn't be changed right now.",
      });
    } finally {
      setPending(false);
    }
  };

  if (dashboard === undefined)
    return (
      <p className="dashboard-status" aria-busy="true">
        Opening this dashboard…
      </p>
    );
  if (dashboard === null || !arrangement)
    return (
      <p className="dashboard-status">
        This dashboard is unavailable right now. Try again later.
      </p>
    );
  const feeds = current?.read ? Object.entries(current.read.feeds) : [];
  // When each feed's measurements were recorded, named, for the status line's details.
  const recorded = current?.read ? recordedSentence(current.read, true) : [];
  const missing = feeds.filter(([, f]) => f.status !== "ok");
  const broken = failed && failed.version === dashboard.version && !drawn;
  // Arranged by hand only on the newest version, drawn and read whole, with
  // room to change; never on a fold (PartsGrid).
  const canArrange =
    !!session.dashboardLayout &&
    dashboard.version === dashboard.latest &&
    drawn &&
    arrangeable(layout, arrangement.whole);
  const titleOf = (star: string) =>
    starTitle?.(star) ?? dashboard.stars.find((s) => s.graphId === star)?.title;
  // A focused map's "Show everything" clears its focus as focus_map would, with
  // the same note and Undo, wherever the newest version is arranged (a fold
  // included: it moves nothing).
  const showEverything = (part: Part) => {
    const made = focusPart(layout, part.id, null, titleOf);
    if (!made.ok) return;
    const commands = [
      { action: "focus_map" as const, part: part.id, focus: null },
    ];
    // The screen's runner says how it went in its note; without one, a change
    // that wasn't made says why below the page.
    const taken = arrangeInPlace({
      kind: "dashboard",
      grid,
      commands,
      summary: made.summary,
    });
    const ran: Promise<GridOutcome> =
      taken ??
      saveArrangement(session.dashboardLayout!, grid, (l) =>
        replay(l, commands, made.summary),
      );
    void ran.then(
      (outcome) => {
        if (!outcome.ok && !taken)
          setNotice({ kind: "failed", reason: outcome.reason });
      },
      () =>
        setNotice({
          kind: "failed",
          reason:
            "The change may have been saved: no answer came back. Check before trying again.",
        }),
    );
  };
  // The page's own states stay in its cell.
  const page = (
    <>
      {broken ? (
        <p className="dashboard-status">
          This version didn’t draw: {failed.reason} Ask your AI to fix it
          {bare ? "." : ", or choose an earlier version below."}
        </p>
      ) : current && !current.read ? (
        <p className="dashboard-status">
          This dashboard’s feeds are unavailable right now. Try again later.
        </p>
      ) : null}
      <PageFrame
        dashboard={dashboard}
        data={current?.read ?? null}
        edits={editsOn}
        drawn={drawn}
        hidden={Boolean(broken) || Boolean(current && !current.read)}
        onDrawn={() => setDrawnFrame(`${dashboard.id}:${dashboard.version}`)}
        onFail={fail}
        onStopped={(reason) =>
          setFailed({ version: dashboard.version, reason, fellBack: false })
        }
        onOpenStar={onOpenStar}
        onWrite={onWrite}
        selection={controls.selection}
        onSelectionDrawn={controls.acknowledge}
      />
    </>
  );
  return (
    <div className="dashboard-view">
      {controls.ui}
      {(bare || current?.refreshFailed) && (
        // When the measurements were recorded, then when the data was read; full screen says
        // it as a card does, the reader names each feed.
        <p className="dashboard-freshness" role="status">
          {current?.refreshFailed &&
            (current.read
              ? "Refresh failed. "
              : "Refresh failed. No data read yet.")}
          {current?.read ? (
            <FreshnessPhrases
              phrases={freshnessPhrases(
                current.read,
                !bare,
                current.refreshFailed,
              )}
            />
          ) : (
            !current?.refreshFailed &&
            (busy ? "Reading dashboard data…" : "No data read yet")
          )}
          {missing.length > 0 &&
            ` · ${missing.length} of ${feeds.length} feeds unavailable`}
        </p>
      )}
      {failed?.fellBack && failed.version !== dashboard.version && (
        <p className="dashboard-notice" role="status">
          Version {failed.version} didn’t draw (
          {failed.reason.replace(/\.$/, "")}). Showing version{" "}
          {dashboard.version}.
        </p>
      )}
      {failed && failed.version === dashboard.version && drawn && (
        <p className="dashboard-notice" role="status">
          {failed.reason}
        </p>
      )}
      <PartsGrid
        parts={layout.parts}
        plain={drawsAsToday(arrangement.layout)}
        label={(p) =>
          p.kind === "page"
            ? dashboard.title
            : `${partLabel(layout, p.id)}${p.focus ? `, ${focusSummary(p.focus, titleOf)}` : ""}`
        }
        render={(p, folded) =>
          p.kind === "page"
            ? page
            : (renderPart?.(p, {
                dashboard,
                fold: folded,
                ...(canArrange ? { showEverything } : {}),
              }) ?? null)
        }
        arrange={canArrange ? arrangeHere : null}
        refused={refusedHere}
        onFold={setFold}
      />
      {notice && (
        <p
          className={`dashboard-change ${notice.kind}`}
          role="status"
          aria-live="polite"
        >
          {notice.kind === "failed" ? (
            notice.reason
          ) : notice.kind === "undone" ? (
            <>Undone: {notice.write.summary}</>
          ) : (
            <>
              <Check size={14} aria-hidden="true" />
              {notice.write.summary}
              <button
                className="dashboard-undo"
                disabled={pending || !session.dashboardUndo}
                onClick={() => void undo(notice.write)}
              >
                Undo
              </button>
            </>
          )}
        </p>
      )}
      {!bare && picking && (
        <ul
          id={versionsId}
          className="dashboard-versions"
          aria-label="Versions"
        >
          {dashboard.versions.map((v) => (
            <li key={v.version}>
              <button
                aria-current={
                  v.version === dashboard.version ? "true" : undefined
                }
                onClick={() => {
                  setPicking(false);
                  setFailed(null);
                  setAsked(v.version === dashboard.latest ? null : v.version);
                }}
              >
                <span className="version-head">
                  <b>Version {v.version}</b>
                  {v.version === dashboard.latest && (
                    <span className="version-tag">latest</span>
                  )}
                  {v.restoredFrom !== null && (
                    <span className="version-tag">
                      restores {v.restoredFrom}
                    </span>
                  )}
                  <time dateTime={v.savedAt}>{when(v.savedAt)}</time>
                </span>
                <span className="version-note">{v.note}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {dashboard.writes.length > 0 && (
        <div className="dashboard-edits">
          <button
            className="dashboard-allow"
            role="switch"
            aria-checked={editsOn}
            aria-describedby={editTargetsId}
            disabled={pending || !session.dashboardAllowEdits}
            onClick={() => void allow(!editsOn)}
          >
            <span className="switch-track" aria-hidden="true">
              <span className="switch-thumb" />
            </span>
            Allow edits
          </button>
          <p id={editTargetsId}>
            {editsOn ? "It can " : "When on, it can "}
            {allowedChanges(dashboard)}.
            {editsOn &&
              " Changes are kept in its history; the latest made here has Undo."}
          </p>
        </div>
      )}
      <div className={`dashboard-meta${more ? " open" : ""}`} hidden={bare}>
        <button
          className="dashboard-summary"
          aria-expanded={more}
          aria-controls={moreId}
          onClick={() => setMore((m) => !m)}
        >
          {/* Its words as one run, so they wrap on a narrow phone instead of being cut. */}
          <span className="summary-text">
            <span>
              {current?.at
                ? `Data read ${ago(current.at, now)}`
                : busy
                  ? "Reading dashboard data…"
                  : "No data read yet"}
            </span>{" "}
            {missing.length > 0 && (
              <span className="summary-missing">
                {missing.length} of {feeds.length} feeds unavailable
              </span>
            )}
          </span>{" "}
          <ChevronUp size={13} aria-hidden="true" />
        </button>
        <div id={moreId} className="dashboard-details">
          <p>
            {recorded.length > 0 && (
              <>
                <FreshnessPhrases phrases={recorded} />{" "}
              </>
            )}
            {current?.at
              ? `Data read ${ago(current.at, now)}`
              : busy
                ? "Reading dashboard data…"
                : "No data read yet"}
            {feeds.length > 0 &&
              (missing.length
                ? ` · ${feeds.length - missing.length} of ${feeds.length} feeds`
                : ` · all ${feeds.length} feeds`)}
            . Read again every two minutes while open. Not saved by Rhythm of
            Life.
          </p>
          {missing.map(([name, f]) => (
            <p key={name} className="dashboard-missing">
              {name} (
              {f.kind === "log"
                ? `the ${String(f.log)} log`
                : f.kind === "memories"
                  ? "its memories"
                  : f.kind === "projects"
                    ? "its project collections"
                    : (f.star?.title ?? "a star")}
              ) unavailable: {f.reason ?? "no reason given"}
            </p>
          ))}
        </div>
        <div className="dashboard-controls">
          <button
            className="dashboard-version"
            aria-expanded={picking}
            aria-controls={versionsId}
            onClick={() => setPicking((p) => !p)}
          >
            Version {dashboard.version}
            {dashboard.version === dashboard.latest && (
              <span className="version-tag">latest</span>
            )}
            <span className="version-day">· {day(dashboard.savedAt)}</span>
            <ChevronUp size={13} aria-hidden="true" />
          </button>
          <button
            className="icon-button"
            aria-label="Read the feeds again"
            title="Read the feeds again"
            disabled={busy}
            onClick={() => void read()}
          >
            <RefreshCw size={14} aria-hidden="true" />
          </button>
        </div>
        {dashboard.version !== dashboard.latest && !failed?.fellBack && (
          <p>
            An earlier version. To make it the newest again, ask your AI to
            restore version {dashboard.version}.
          </p>
        )}
      </div>
    </div>
  );
}
